// StructureBody port contract tests (Round-7 Body batch). StructureBody is a thin
// ActiveBody subclass whose only added state is the constructor object id; the point of
// interest is that this extra ObjectId folds into the SAME Objects CRC channel as the
// inherited Fix64 health ledger (GPL StructureBody::xfer chains ActiveBody::xfer then
// xfers the id). Tests run on HeadlessSimGame with real parsed INI so the module is the
// real one the parse table builds. Health arithmetic itself is covered by the S1
// WeaponDamageArmorSystemTests; here we prove (a) identity/defaults, (b) the id is in the
// walk, (c) mid-state save/load continuation, and (d) the base body still works through
// the subclass.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class StructureBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object TestStructure
  KindOf = STRUCTURE IMMOBILE
  Geometry = BOX
  GeometryMajorRadius = 20
  GeometryMinorRadius = 20
  GeometryHeight = 20
  Body = StructureBody ModuleTag_Body
    MaxHealth = 1000
    InitialHealth = 1000
  End
End

Object TestBuilder
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition, float x = 0, float y = 0)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(x, y, 0));

    private static StructureBody BodyOf(GameObject gameObject)
        => Assert.IsType<StructureBody>(gameObject.BodyModule);

    private static CombatDamageInput Damage(int amount, DamageType type = DamageType.Unresistable, GameObject? source = null)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = new Fix64(amount),
        };

    // ================================================================
    // Identity / defaults (GPL ctor + setConstructorObject)
    // ================================================================

    [Fact]
    public void Structure_UsesStructureBody()
    {
        var game = NewGame();
        var structure = Spawn(game, "TestStructure");

        Assert.IsType<StructureBody>(structure.BodyModule);
    }

    [Fact]
    public void ConstructorObjectId_DefaultsToInvalid()
    {
        var game = NewGame();
        var structure = Spawn(game, "TestStructure");

        Assert.Equal(ObjectId.Invalid, BodyOf(structure).ConstructorObjectId);
    }

    [Fact]
    public void SetConstructorObject_StoresBuilderId()
    {
        var game = NewGame();
        var structure = Spawn(game, "TestStructure");
        var builder = Spawn(game, "TestBuilder");

        BodyOf(structure).SetConstructorObject(builder);

        Assert.Equal(builder.Id, BodyOf(structure).ConstructorObjectId);
    }

    [Fact]
    public void SetConstructorObject_NullLeavesExistingIdUntouched()
    {
        // GPL setConstructorObject only writes for a non-null object - a null argument is
        // a no-op, it does NOT clear the id back to INVALID_ID.
        var game = NewGame();
        var structure = Spawn(game, "TestStructure");
        var builder = Spawn(game, "TestBuilder");

        BodyOf(structure).SetConstructorObject(builder);
        BodyOf(structure).SetConstructorObject(null);

        Assert.Equal(builder.Id, BodyOf(structure).ConstructorObjectId);
    }

    // ================================================================
    // The added ObjectId folds into the same CRC channel
    // ================================================================

    [Fact]
    public void ConstructorObjectId_ParticipatesInCrc()
    {
        // Two structures identical in every inherited field, differing ONLY in the
        // constructor id, must produce different CRCs - proof the extra ObjectId is in
        // the Xfer walk (a subclass that forgot to override Xfer would fold identically).
        var game = NewGame();
        var a = Spawn(game, "TestStructure");
        var b = Spawn(game, "TestStructure");
        var builder = Spawn(game, "TestBuilder");

        BodyOf(a).SetConstructorObject(builder);

        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(BodyOf(a)),
            PortedModuleTestKit.LiveCrc(BodyOf(b)));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        // The shadow-copy base test (api-freeze §5): live state (damaged + a stored
        // constructor id) saved, loaded into a differently-stated shadow, CRCs must match
        // and the re-save must be byte-stable. Catches read/write asymmetry across the
        // whole walk including the added field.
        var game = NewGame();
        var live = Spawn(game, "TestStructure");
        var shadow = Spawn(game, "TestStructure");
        var builder = Spawn(game, "TestBuilder");

        BodyOf(live).SetConstructorObject(builder);
        live.AttemptCombatDamage(Damage(400));
        shadow.AttemptCombatDamage(Damage(150));  // differently-stated

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_ConstructorIdAndHealthContinuationMatch()
    {
        // Mid-behavior save/load: both the added ObjectId and the inherited Fix64 health
        // round-trip, and the continuation (same follow-up damage) is bit-identical.
        var game = NewGame();
        var live = Spawn(game, "TestStructure");
        var builder = Spawn(game, "TestBuilder");
        BodyOf(live).SetConstructorObject(builder);
        live.AttemptCombatDamage(Damage(300));

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restored = Spawn(game, "TestStructure");
        PortedModuleTestKit.Load(BodyOf(restored), state);

        Assert.Equal(builder.Id, BodyOf(restored).ConstructorObjectId);
        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restored).DamageCore.CurrentHealth);

        live.AttemptCombatDamage(Damage(250));
        restored.AttemptCombatDamage(Damage(250));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restored).DamageCore.CurrentHealth);
        Assert.Equal(new Fix64(450), BodyOf(restored).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Inherited damage-to-health still works through the subclass
    // ================================================================

    [Fact]
    public void InheritedDamage_AppliesToHealth()
    {
        var game = NewGame();
        var structure = Spawn(game, "TestStructure");

        var output = structure.AttemptCombatDamage(Damage(400));

        Assert.Equal(new Fix64(400), output.ActualDamageDealt);
        Assert.Equal(new Fix64(600), BodyOf(structure).DamageCore.CurrentHealth);
    }
}

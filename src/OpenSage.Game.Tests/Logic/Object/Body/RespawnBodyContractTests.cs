// RespawnBody R8 contract tests (Body-batch), on HeadlessSimGame with real parsed INI so the
// audited parse path (ObjectFilter.Parse for PermanentlyKilledByFilter, the F-R7-2 InitialHealth
// default) and the S1 Fix64 kill-resolution chain are on the tested path.
//
// Behavioral reference: BFME2-only class, ABSENT from generals-gpl - binary-derived spec behavioral
// facts only, clean-room fresh code. The determinism-relevant fact under test is the ONE this
// Body owns: on the killing blow, the killer is tested against PermanentlyKilledByFilter and the
// permanence verdict is latched as sim state and folded into the Objects CRC channel (shadow-copy
// CRC + CRC-participation + mid-state save/load continuation). The broader respawn lifecycle is
// out of scope on this tip (finding F-RSB-1).

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class RespawnBodyContractTests
{
    // Filter = NONE +STRUCTURE: a structure killer makes the death permanent; anything else
    // (infantry, no source) leaves the hero respawn-eligible. A separate hero omits the filter
    // entirely, and a third omits InitialHealth to exercise the F-R7-2 default carry.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor BaseArmor
  Armor = DEFAULT 100%
End

Object RespawnHero
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    PermanentlyKilledByFilter = NONE +STRUCTURE
  End
End

Object RespawnHeroNoFilter
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
End

Object RespawnHeroDefaultHealth
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 250
    PermanentlyKilledByFilter = NONE +STRUCTURE
  End
End

Object StructureKiller
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End

Object InfantryKiller
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition = "RespawnHero")
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static RespawnBody BodyOf(GameObject gameObject)
        => Assert.IsType<RespawnBody>(gameObject.BodyModule);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(
        int amount, GameObject source = null, DamageType type = DamageType.Magic)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = false,
        };

    // ================================================================
    // Item 1 - ModuleData audit
    // ================================================================

    [Fact]
    public void PermanentlyKilledByFilter_IsParsed_IntoTheStructureInclude()
    {
        var game = NewGame();
        var data = Assert.IsType<RespawnBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("RespawnHero")
                .Behaviors["ModuleTag_Body"].Data);

        Assert.NotNull(data.PermanentlyKilledByFilter);
        // A structure matches; a non-structure (infantry) does not.
        var structureKiller = Spawn(game, "StructureKiller");
        var infantryKiller = Spawn(game, "InfantryKiller");
        Assert.True(data.PermanentlyKilledByFilter.Matches(structureKiller));
        Assert.False(data.PermanentlyKilledByFilter.Matches(infantryKiller));
    }

    [Fact]
    public void InitialHealth_DefaultsToMaxHealth_WhenOmitted()
    {
        // F-R7-2 / F-HB-1 carry: the shadowing Parse must re-apply ApplyHealthDefaults, else the
        // body would spawn at 0 health. RespawnHeroDefaultHealth omits InitialHealth.
        var game = NewGame();
        var data = Assert.IsType<RespawnBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("RespawnHeroDefaultHealth")
                .Behaviors["ModuleTag_Body"].Data);

        Assert.Equal(Fix(250), data.MaxHealth);
        Assert.Equal(Fix(250), data.InitialHealth);

        // And the spawned body actually starts at full (250), not 0.
        var hero = Spawn(game, "RespawnHeroDefaultHealth");
        Assert.Equal(Fix(250), BodyOf(hero).DamageCore.CurrentHealth);
    }

    // ================================================================
    // The permanence decision (the one behavior this Body owns)
    // ================================================================

    [Fact]
    public void KilledByStructure_MarksPermanentlyKilled()
    {
        var game = NewGame();
        var hero = Spawn(game);
        var killer = Spawn(game, "StructureKiller");

        hero.AttemptCombatDamage(Damage(9999, source: killer));

        Assert.True(hero.IsEffectivelyDead);
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void KilledByNonStructure_IsNotPermanent()
    {
        var game = NewGame();
        var hero = Spawn(game);
        var killer = Spawn(game, "InfantryKiller");

        hero.AttemptCombatDamage(Damage(9999, source: killer));

        Assert.True(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void KilledBySourcelessDamage_IsNotPermanent()
    {
        var game = NewGame();
        var hero = Spawn(game);

        hero.AttemptCombatDamage(Damage(9999)); // SourceId = Invalid

        Assert.True(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void NoFilter_KillIsNeverPermanent()
    {
        var game = NewGame();
        var hero = Spawn(game, "RespawnHeroNoFilter");
        var killer = Spawn(game, "StructureKiller");

        hero.AttemptCombatDamage(Damage(9999, source: killer));

        Assert.True(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void NonLethalHit_LeavesVerdictUntouched()
    {
        var game = NewGame();
        var hero = Spawn(game);
        var killer = Spawn(game, "StructureKiller");

        hero.AttemptCombatDamage(Damage(30, source: killer));

        Assert.False(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
        Assert.Equal(Fix(70), BodyOf(hero).DamageCore.CurrentHealth);
    }

    [Fact]
    public void OnlyTheKillingBlowResolves_LatchIsNotOverwritten()
    {
        // A structure kill latches permanent; a further (already-dead) hit from an infantry
        // must not flip the verdict back to false.
        var game = NewGame();
        var hero = Spawn(game);
        var structureKiller = Spawn(game, "StructureKiller");
        var infantryKiller = Spawn(game, "InfantryKiller");

        hero.AttemptCombatDamage(Damage(9999, source: structureKiller));
        Assert.True(BodyOf(hero).IsPermanentlyKilled);

        hero.AttemptCombatDamage(Damage(9999, source: infantryKiller));
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
    }

    // ================================================================
    // Item 3 - Xfer: the _permanentlyKilled latch folds into the Objects CRC channel
    // ================================================================

    [Fact]
    public void PermanentlyKilled_ParticipatesInCrc()
    {
        var game = NewGame();
        var alive = Spawn(game);
        var permanentlyDead = Spawn(game);
        permanentlyDead.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller")));

        // A subclass that forgot to walk the latch would fold identically here.
        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(BodyOf(alive)),
            PortedModuleTestKit.LiveCrc(BodyOf(permanentlyDead)));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game);
        live.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller"))); // permanently killed
        var shadow = Spawn(game);
        shadow.AttemptCombatDamage(Damage(25)); // differently-stated, still alive

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    // ================================================================
    // Item 4 - mid-state save/load continuation
    // ================================================================

    [Fact]
    public void SaveLoad_PermanentKillVerdict_Continues()
    {
        var game = NewGame();
        var live = Spawn(game);
        live.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller")));
        Assert.True(BodyOf(live).IsPermanentlyKilled);

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game); // fresh: alive, verdict false
        Assert.False(BodyOf(restoredHost).IsPermanentlyKilled);

        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // The verdict must have restored through the contract Xfer.
        Assert.True(BodyOf(restoredHost).IsPermanentlyKilled);
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(BodyOf(live)),
            PortedModuleTestKit.LiveCrc(BodyOf(restoredHost)));
    }
}

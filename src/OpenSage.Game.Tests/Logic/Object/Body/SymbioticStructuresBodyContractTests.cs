// SymbioticStructuresBody R8 contract tests (template v1.1 §5), exercised on HeadlessSimGame
// with real parsed INI so the quantizing parse path and the S1 Fix64 damage/health chain are on
// the tested path.
//
// Behavioral reference: BFME/BFME2-only module, ABSENT from generals-gpl; no behavioral spec exists.
// The Symbiote death-link is DEFERRED (needs object association beyond S1 - finding F-SSB-1), so
// the branches under test are: the module materializes as a SymbioticStructuresBody, the parsed
// Symbiote handle is retained, the F-R7-2 InitialHealth=MaxHealth default holds through the
// shadowing Parse, damage delegates to the ActiveBody health ledger, and the Xfer contract walk
// round-trips (shadow-copy CRC + mid-state save/load continuation).

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class SymbioticStructuresBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object SymbioticVictim
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = SymbioticStructuresBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    Symbiote = KeepLeft
  End
End

; Real-INI shape (wall/keep segments): a Symbiote body that OMITS InitialHealth. Exercises the
; F-R7-2 fix - the shadowing Parse must default InitialHealth to MaxHealth.
Object SymbioticDefaultHealth
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = SymbioticStructuresBody ModuleTag_Body
    MaxHealth = 250
    Symbiote = KeepRight
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5B107u)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static SymbioticStructuresBody BodyOf(GameObject gameObject)
        => Assert.IsType<SymbioticStructuresBody>(gameObject.BodyModule);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, DamageType type = DamageType.Unresistable)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
        };

    // ================================================================
    // Materialization + ModuleData audit
    // ================================================================

    [Fact]
    public void Body_MaterializesAsSymbioticStructuresBody()
    {
        var game = NewGame();
        var victim = Spawn(game, "SymbioticVictim");

        Assert.IsType<SymbioticStructuresBody>(victim.BodyModule);
        Assert.True(BodyOf(victim).HasSimXfer);
    }

    [Fact]
    public void Symbiote_HandleIsParsedAndRetained()
    {
        var game = NewGame();
        var victim = Spawn(game, "SymbioticVictim");
        var data = Assert.IsType<SymbioticStructuresBodyModuleData>(victim.Definition.Behaviors["ModuleTag_Body"].Data);

        Assert.Equal("KeepLeft", data.Symbiote);
    }

    // ================================================================
    // F-R7-2: InitialHealth defaults to MaxHealth in the shadowing Parse
    // ================================================================

    [Fact]
    public void InitialHealth_DefaultsToMaxHealth_WhenOmitted()
    {
        var game = NewGame();
        var victim = Spawn(game, "SymbioticDefaultHealth");

        // Without the F-R7-2 fix in the shadowing Parse the body would spawn at 0 health.
        Assert.Equal(Fix(250), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    // ================================================================
    // Damage delegates to the ActiveBody Fix64 health ledger (S1)
    // ================================================================

    [Fact]
    public void Damage_AppliesThroughActiveBodyLedger()
    {
        var game = NewGame();
        var victim = Spawn(game, "SymbioticVictim");

        var output = victim.AttemptCombatDamage(Damage(30));

        Assert.Equal(Fix(70), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.Equal(Fix(30), output.ActualDamageDealt);
    }

    [Fact]
    public void LethalDamage_KillsNormally()
    {
        var game = NewGame();
        var victim = Spawn(game, "SymbioticVictim");

        victim.AttemptCombatDamage(Damage(9999));

        // No immortality: a Symbiote structure dies like a plain ActiveBody (the fate-coupling
        // that would spread the death to the symbiote is the DEFERRED F-SSB-1 hook).
        Assert.True(victim.IsEffectivelyDead);
    }

    // ================================================================
    // Xfer contract walk (version wrapper + base; no own sim state)
    // ================================================================

    [Fact]
    public void ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "SymbioticVictim");
        var shadow = Spawn(game, "SymbioticVictim");

        // Drive the live body mid-behavior; the shadow starts differently-stated.
        BodyOf(live).ApplyDamageScalar(Fix64.Half);
        live.AttemptCombatDamage(Damage(40));
        shadow.AttemptCombatDamage(Damage(15));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_ContinuationMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "SymbioticVictim");
        live.AttemptCombatDamage(Damage(35));   // health 65

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game, "SymbioticVictim");
        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // Both take the same follow-up hit and land identically.
        live.AttemptCombatDamage(Damage(20));
        restoredHost.AttemptCombatDamage(Damage(20));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.Equal(Fix(45), BodyOf(restoredHost).DamageCore.CurrentHealth);
    }
}

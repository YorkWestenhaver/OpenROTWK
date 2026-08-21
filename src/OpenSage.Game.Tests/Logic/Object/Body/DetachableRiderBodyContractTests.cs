// DetachableRiderBody R8 contract tests (template v1.1 §5): the rider-death health drop,
// exercised on HeadlessSimGame with real parsed INI so the quantizing parse path (percent ->
// Fix64) and the S1 Fix64 damage/health chain are on the tested path. One test per behavioral
// branch, plus the Xfer contract walk (shadow-copy CRC + mid-state save/load continuation).
//
// Behavioral reference: BFME/BFME2-only, absent from generals-gpl; the task authorizes the
// percent-health SET as the portable core mechanic. The central claim under test is that the
// drop lands in Fix64 on the canonical BodyDamageCore, is a one-way lowering, composes with
// prior combat damage, and can itself kill the mount at 0%.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class DetachableRiderBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object RiderMount
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    HealthPercentageWhenRiderDies = 50%
  End
End

Object RiderMountHalf
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 200
    InitialHealth = 200
    HealthPercentageWhenRiderDies = 50%
  End
End

Object RiderMountZero
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    HealthPercentageWhenRiderDies = 0%
  End
End

Object RiderMountDefaultHealth
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 100
    HealthPercentageWhenRiderDies = 40%
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD00Du)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static DetachableRiderBody BodyOf(GameObject gameObject)
        => Assert.IsType<DetachableRiderBody>(gameObject.BodyModule);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, DamageType type = DamageType.Unresistable)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = false,
        };

    // ================================================================
    // The rider-death health drop (the portable core mechanic)
    // ================================================================

    [Fact]
    public void RiderDies_DropsHealthToPercentageOfMax_InFix64()
    {
        var game = NewGame();
        var mount = Spawn(game, "RiderMount");

        BodyOf(mount).OnRiderDied();

        // 50% of MaxHealth 100 = exactly Fix64 50 (percent quantized at parse, product in Fix64).
        Assert.Equal(Fix(50), BodyOf(mount).DamageCore.CurrentHealth);
        Assert.False(mount.IsEffectivelyDead);
    }

    [Fact]
    public void RiderDies_UsesMaxHealthNotCurrent()
    {
        var game = NewGame();
        var mount = Spawn(game, "RiderMountHalf");

        // 50% of MaxHealth 200 = 100 (of MAX, not of any current value).
        BodyOf(mount).OnRiderDied();

        Assert.Equal(Fix(100), BodyOf(mount).DamageCore.CurrentHealth);
    }

    [Fact]
    public void RiderDies_ComposesWithPriorCombatDamage()
    {
        var game = NewGame();
        var mount = Spawn(game, "RiderMount");

        // Pre-damage to 70, then the rider dies: the drop targets 50% of MAX (50), below 70,
        // so the mount drops to 50.
        mount.AttemptCombatDamage(Damage(30));
        Assert.Equal(Fix(70), BodyOf(mount).DamageCore.CurrentHealth);

        BodyOf(mount).OnRiderDied();

        Assert.Equal(Fix(50), BodyOf(mount).DamageCore.CurrentHealth);
    }

    [Fact]
    public void RiderDies_NeverHealsAMountAlreadyBelowTarget()
    {
        var game = NewGame();
        var mount = Spawn(game, "RiderMount");

        // Damage below the 50% target first (to 25), then the rider dies. "Drops to" is a
        // one-way lowering: the mount is NOT healed back up to 50.
        mount.AttemptCombatDamage(Damage(75));
        Assert.Equal(Fix(25), BodyOf(mount).DamageCore.CurrentHealth);

        BodyOf(mount).OnRiderDied();

        Assert.Equal(Fix(25), BodyOf(mount).DamageCore.CurrentHealth);
    }

    [Fact]
    public void RiderDies_AtZeroPercent_KillsTheMount()
    {
        var game = NewGame();
        var mount = Spawn(game, "RiderMountZero");

        // 0% of max = 0 health: a rider death with no residual mount health kills it.
        BodyOf(mount).OnRiderDied();

        Assert.Equal(Fix64.Zero, BodyOf(mount).DamageCore.CurrentHealth);
        Assert.True(mount.IsEffectivelyDead);
    }

    [Fact]
    public void RiderDies_IsIdempotentAtTarget()
    {
        var game = NewGame();
        var mount = Spawn(game, "RiderMount");

        BodyOf(mount).OnRiderDied();
        BodyOf(mount).OnRiderDied();   // second call: already at 50, one-way lowering leaves it.

        Assert.Equal(Fix(50), BodyOf(mount).DamageCore.CurrentHealth);
    }

    // ================================================================
    // ModuleData audit (F-R7-2 InitialHealth default carried through the shadowing Parse)
    // ================================================================

    [Fact]
    public void InitialHealthDefaultsToMaxHealth_WhenOmitted()
    {
        var game = NewGame();
        // RiderMountDefaultHealth omits InitialHealth. The shadowing DetachableRiderBodyModuleData.Parse
        // must still apply ActiveBody's BFME InitialHealth=MaxHealth default (F-R7-2) - otherwise
        // the mount spawns at 0 HP.
        var mount = Spawn(game, "RiderMountDefaultHealth");

        Assert.Equal(Fix(100), BodyOf(mount).DamageCore.CurrentHealth);
        Assert.False(mount.IsEffectivelyDead);
    }

    // ================================================================
    // Xfer contract walk (version wrapper + base; no own sim state)
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "RiderMount");
        var shadow = Spawn(game, "RiderMount");

        Assert.True(BodyOf(live).HasSimXfer);

        // Drive the live body mid-behavior (combat + rider death); the shadow starts differently-stated.
        live.AttemptCombatDamage(Damage(15));
        BodyOf(live).OnRiderDied();
        shadow.AttemptCombatDamage(Damage(20));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_ContinuationMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "RiderMount");
        live.AttemptCombatDamage(Damage(20));   // health 80

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game, "RiderMount");
        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // Both take the same follow-up rider death and both drop identically to 50% of max.
        BodyOf(live).OnRiderDied();
        BodyOf(restoredHost).OnRiderDied();

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.Equal(Fix(50), BodyOf(restoredHost).DamageCore.CurrentHealth);
    }
}

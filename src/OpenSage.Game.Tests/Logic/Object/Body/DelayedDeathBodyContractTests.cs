// DelayedDeathBody R8 contract tests (template v1.1 §5): the creation-armed death timer and
// the ImmortalUntilDeathTime floor, exercised on HeadlessSimGame with real parsed INI so the
// S5 quantizing parse path (ms -> LogicFrameSpan) and the S1 Fix64 health/armor chain are on
// the tested path. One test per behavioral branch, plus the Xfer contract walk on the
// [SimState] companion (shadow-copy CRC + mid-behavior save/load continuation).
//
// Behavioral reference: BFME/BFME2-RotWK only (absent from generals-gpl). The arming trigger is
// creation-armed (see DelayedDeathBody.cs header + research/modules-r8/DelayedDeathBody.md for
// the corpus evidence); DoHealthCheck / respawn / prerequisite-upgrade paths are spec-gated
// behavior-fact gaps and are not asserted here.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class DelayedDeathBodyContractTests
{
    // DelayedDeathTime = 1000 ms at BFME2's 5 Hz => ceil(1000 * 5 / 1000) = 5 frames.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object TimedUnit
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DelayedDeathBody ModuleTag_Body
    MaxHealth = 100
    DelayedDeathTime = 1000
    DoHealthCheck = No
    CanRespawn = No
  End
End

Object ImmortalTimedUnit
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DelayedDeathBody ModuleTag_Body
    MaxHealth = 100
    DelayedDeathTime = 1000
    ImmortalUntilDeathTime = Yes
  End
End

Object PlainDelayedUnit
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DelayedDeathBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object DelayedUnitDefaultHealth
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = DelayedDeathBody ModuleTag_Body
    MaxHealth = 150
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xDEAD)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static DelayedDeathBody BodyOf(GameObject gameObject)
        => Assert.IsType<DelayedDeathBody>(gameObject.BodyModule);

    private static DelayedDeathTimer TimerOf(GameObject gameObject)
        => gameObject.BehaviorModules.OfType<DelayedDeathTimer>().Single();

    private static CombatDamageInput Damage(
        int amount, DamageType type = DamageType.Unresistable, GameObject source = null, bool kill = false)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = new Fix64(amount),
            Kill = kill,
        };

    // ================================================================
    // Branch 1: the creation-armed lifetime kills the unit on the timer
    // ================================================================

    [Fact]
    public void Timer_KillsUnit_AfterDelayedDeathTime()
    {
        var game = NewGame();
        var victim = Spawn(game, "TimedUnit");

        // 5-frame lifetime: alive through frame 5's processing, dead once frame 5 is reached.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
            Assert.False(victim.IsEffectivelyDead, $"died early at step {i}");
        }

        game.Step();   // now = frame 5 >= death frame 5: the timer fires.
        Assert.True(victim.IsEffectivelyDead);
    }

    [Fact]
    public void Timer_Companion_IsRegisteredAndTicks()
    {
        var game = NewGame();
        var victim = Spawn(game, "TimedUnit");

        // The companion exists, is a real UpdateModule on the object, and carries the sim walk.
        var timer = TimerOf(victim);
        Assert.True(timer.HasSimXfer);
        Assert.True(timer.ImmortalActive == false);   // no immortality flag on this unit
    }

    // ================================================================
    // Branch 2: ImmortalUntilDeathTime floors health until the timer fires
    // ================================================================

    [Fact]
    public void Immortal_SurvivesLethalDamage_UntilDeathTime()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalTimedUnit");

        // A DAMAGE_KILL before the death time is floored at 1 HP (Fix64 floor on the core).
        victim.AttemptCombatDamage(Damage(0, DamageType.Unresistable, kill: true));
        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);

        // It keeps surviving damage frame after frame (floored at 1 HP each time)...
        for (var i = 0; i < 5; i++)
        {
            victim.AttemptCombatDamage(Damage(9999));
            game.Step();
            Assert.False(victim.IsEffectivelyDead, $"died early at step {i}");
            Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        }

        // ...until the scheduled death time, when the timer lifts the floor and kills it.
        game.Step();
        Assert.True(victim.IsEffectivelyDead);
    }

    // ================================================================
    // Branch 3: without ImmortalUntilDeathTime the floor is inactive (dies from damage)
    // ================================================================

    [Fact]
    public void NonImmortal_DiesImmediately_FromLethalDamage()
    {
        var game = NewGame();
        var victim = Spawn(game, "TimedUnit");

        // DoHealthCheck=No / no immortality: a normal lethal hit kills at once, unfloored.
        victim.AttemptCombatDamage(Damage(9999));

        Assert.True(BodyOf(victim).DamageCore.CurrentHealth <= Fix64.Zero);
        Assert.True(victim.IsEffectivelyDead);
    }

    // ================================================================
    // Branch 4: no DelayedDeathTime => behaves like a plain ActiveBody (never auto-dies)
    // ================================================================

    [Fact]
    public void NoDelayedDeathTime_NeverAutoDies()
    {
        var game = NewGame();
        var victim = Spawn(game, "PlainDelayedUnit");

        Assert.False(TimerOf(victim).ImmortalActive);

        for (var i = 0; i < 12; i++)
        {
            game.Step();
        }

        Assert.False(victim.IsEffectivelyDead);
        Assert.Equal(new Fix64(100), BodyOf(victim).DamageCore.CurrentHealth);
    }

    // ================================================================
    // ModuleData audit (F-R7-2 InitialHealth default carried through the shadowing Parse)
    // ================================================================

    [Fact]
    public void InitialHealth_DefaultsToMaxHealth_WhenOmitted()
    {
        // F-R7-2 carry: the shadowing DelayedDeathBodyModuleData.Parse must re-apply
        // ApplyHealthDefaults, else a block that omits InitialHealth spawns at 0 HP.
        // DelayedUnitDefaultHealth omits InitialHealth.
        var game = NewGame();
        var data = Assert.IsType<DelayedDeathBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("DelayedUnitDefaultHealth")
                .Behaviors["ModuleTag_Body"].Data);

        Assert.Equal(new Fix64(150), data.MaxHealth);
        Assert.Equal(new Fix64(150), data.InitialHealth);

        // And the spawned body actually starts at full (150), not 0.
        var unit = Spawn(game, "DelayedUnitDefaultHealth");
        Assert.Equal(new Fix64(150), BodyOf(unit).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Xfer contract walk (the [SimState] companion owns the timer state)
    // ================================================================

    [Fact]
    public void ShadowCopyCrcMatches_MidCountdown()
    {
        var game = NewGame();
        var live = Spawn(game, "TimedUnit");
        var shadow = Spawn(game, "TimedUnit");

        // Drive the live timer past its death (armed -> fired); the shadow stays fresh/unfired
        // with a different death frame, so a missing field in the walk would mismatch.
        for (var i = 0; i < 7; i++)
        {
            game.Step();
        }
        Assert.True(live.IsEffectivelyDead);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(TimerOf(live), TimerOf(shadow));
    }

    [Fact]
    public void MidCountdown_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunLifetime(roundTripAtFrame: -1);
        var trajectoryB = RunLifetime(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunLifetime(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var victim = Spawn(game, "TimedUnit");
        var timer = TimerOf(victim);

        var trajectory = new int[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                // Round-trip the module state AND the engine-owned wake frame (S6), exactly as
                // a real save/load walk does.
                var state = PortedModuleTestKit.Save(timer);
                var wake = timer.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(timer, state);
                timer.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = victim.IsEffectivelyDead ? 1 : 0;
        }

        return trajectory;
    }
}

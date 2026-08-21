// Mocked-game unit tests for the HitReactionBehavior R13 port (modules-r13/specs/
// HitReactionBehaviorData.md §3): one test per behavior-contract case, [create -> damage ->
// observable ModelConditionFlag effect], plus the mid-behavior save/load round-trip and the
// shadow-copy base test.
//
// Sleepy-update caveat (spec §3): a freshly spawned object's own Update() does not run on the
// same HeadlessSimGame.Step() that spawned it - it runs on the second Step(). OnDamage is a
// direct dispatch from ActiveBody.AttemptDamage, not gated by this rule, so flags set by a hit
// are observable immediately after the Step() the hit lands in. The armed-wake EXPIRY, however,
// is this module's own Update() and IS sleepy-gated - tests budget one extra Step() at the start
// to cross that boundary before counting down LifeTimerN frames.
//
// Frame arithmetic used throughout (the thing the first cut of this file got wrong):
//   * BFME2's logic rate is 5 Hz, not 30 (SageGameExtensions.LogicFramesPerSecond), so an INI
//     ms duration quantizes to ceil(ms / 200) frames - 200 ms = 1 frame, 400 ms = 2, 600 ms = 3,
//     2000 ms = 10. The retail AotR values (45/60/90/1500 ms) all collapse to 1-8 frames; the
//     small ones collapse to a single frame, which makes them useless for a multi-frame-hold
//     assertion, so these fixtures name the ms values that land on the frame counts each case
//     actually needs, and the one-frame collapse gets its own dedicated case.
//   * A hit applied between Step()s lands on the just-completed frame N (GameLogic.Update
//     increments _currentFrame last), so the wake is armed at N + LifeTimerN and this module's
//     Update() fires on the Step that runs frame N + LifeTimerN - i.e. the flags survive
//     LifeTimerN Step()s after the hit and are cleared on the next one. That is the frame the
//     hit lands on being frame zero of the hold, per the module's GPL PoisonedBehavior timing
//     citation; it is the module's convention, and these tests count against it.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class HitReactionBehaviorContractTests
{
    // demotest.ini's threshold ladder, reused verbatim (spec §3, case 1): Threshold1=0/2=25/3=50.
    // The LifeTimerN ms values are chosen per fixture for the frame count the case needs at
    // BFME2's 5 Hz logic rate (ceil(ms / 200) frames): 200 -> 1, 400 -> 2, 600 -> 3, 2000 -> 10.
    private const string Definitions = @"
Object Reactor
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HitReactionBehavior ModuleTag_Hit
    HitReactionLifeTimer1 = 400
    HitReactionLifeTimer2 = 600
    HitReactionLifeTimer3 = 400
    HitReactionThreshold1 = 0.0
    HitReactionThreshold2 = 25.0
    HitReactionThreshold3 = 50.0
    FastHitsResetReaction = No
  End
End

Object ReactorNonZeroFloor
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HitReactionBehavior ModuleTag_Hit
    HitReactionLifeTimer1 = 400
    HitReactionLifeTimer2 = 600
    HitReactionLifeTimer3 = 400
    HitReactionThreshold1 = 10.0
    HitReactionThreshold2 = 25.0
    HitReactionThreshold3 = 50.0
    FastHitsResetReaction = No
  End
End

Object ReactorOneFrameHold
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HitReactionBehavior ModuleTag_Hit
    HitReactionLifeTimer1 = 200
    HitReactionLifeTimer2 = 200
    HitReactionLifeTimer3 = 200
    HitReactionThreshold1 = 0.0
    HitReactionThreshold2 = 25.0
    HitReactionThreshold3 = 50.0
    FastHitsResetReaction = No
  End
End

Object ReactorLongHold
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HitReactionBehavior ModuleTag_Hit
    HitReactionLifeTimer1 = 2000
    HitReactionLifeTimer2 = 600
    HitReactionLifeTimer3 = 400
    HitReactionThreshold1 = 0.0
    HitReactionThreshold2 = 25.0
    HitReactionThreshold3 = 50.0
    FastHitsResetReaction = No
  End
End

Object ReactorLongHoldFastReset
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HitReactionBehavior ModuleTag_Hit
    HitReactionLifeTimer1 = 2000
    HitReactionLifeTimer2 = 600
    HitReactionLifeTimer3 = 400
    HitReactionThreshold1 = 0.0
    HitReactionThreshold2 = 25.0
    HitReactionThreshold3 = 50.0
    FastHitsResetReaction = Yes
  End
End

Object ReactorShortHoldFastReset
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HitReactionBehavior ModuleTag_Hit
    HitReactionLifeTimer1 = 600
    HitReactionLifeTimer2 = 600
    HitReactionLifeTimer3 = 400
    HitReactionThreshold1 = 0.0
    HitReactionThreshold2 = 25.0
    HitReactionThreshold3 = 50.0
    FastHitsResetReaction = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void Damage(GameObject target, float amount)
    {
        target.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = DamageType.Explosion,
            DeathType = DeathType.Normal,
            Amount = amount,
        });
    }

    private static HitReactionBehavior ReactionModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<HitReactionBehavior>().Single();

    [Fact]
    public void Tier1Hit_SetsHitReactionAndHitLevel1Only()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);

        // Cross the sleepy-update boundary before damaging, matching the case-1 harness shape.
        game.Step();
        Damage(reactor, 10f);
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel2));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));
    }

    [Fact]
    public void ThresholdBoundary_IsInclusive_AndPicksHighestCrossedTier()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        Damage(reactor, 25f); // == Threshold2 exactly
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel2));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));
    }

    [Fact]
    public void BelowEveryThreshold_DoesNothing()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("ReactorNonZeroFloor", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        Damage(reactor, 5f); // below Threshold1 = 10
        game.Step();

        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel2));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));
    }

    [Fact]
    public void ReactionAutoExpires_AfterItsTiersLifeTimer()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);

        // Cross the sleepy-update boundary (module's first live Update() opportunity) first.
        game.Step();
        Damage(reactor, 10f); // tier 1, LifeTimer1 = 400 ms -> 2 frames
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        // Frame 2 of the 2-frame hold: still held.
        game.Step();
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        // Expiry frame (hit frame + LifeTimer1): both flags clear.
        game.Step();
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
    }

    [Fact]
    public void SingleFrameHold_ClearsOnTheVeryNextStep()
    {
        // The frame-count boundary the first cut of this file tripped over: at BFME2's 5 Hz
        // logic rate every LifeTimerN at or under 200 ms quantizes to exactly one frame, so the
        // hold is the hit's own frame and nothing more. demotest.ini's 45/60/90 ms values all
        // land here, so this is the shape most retail data actually produces. Pinned as its own
        // case so a future logic-rate or quantization change fails loudly here rather than
        // silently lengthening every reaction in the game.
        var game = NewGame();
        var reactor = game.SpawnObject("ReactorOneFrameHold", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        Damage(reactor, 10f); // tier 1, LifeTimer1 = 200 ms -> 1 frame
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        game.Step();
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
    }

    [Fact]
    public void FastHitsResetReaction_False_SecondQualifyingHitIsDropped()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("ReactorLongHold", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        Damage(reactor, 10f); // tier 1, long hold
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        // Second hit, would select tier 3, arrives mid-reaction: dropped, not restarted.
        Damage(reactor, 60f);
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
    }

    [Fact]
    public void FastHitsResetReaction_True_SecondQualifyingHitRestartsToNewTier()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("ReactorLongHoldFastReset", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        Damage(reactor, 10f); // tier 1, long hold
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        // Second hit selects tier 3 while tier-1 reaction is active: restart, tier swaps.
        Damage(reactor, 60f);
        game.Step();

        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));

        // Tier 3's LifeTimer3 (400 ms -> 2 frames) runs from the SECOND hit, not from the first,
        // and does not stack onto tier 1's remaining 10-frame hold: still set one frame later
        // (frame 2 of 2), cleared on the frame after that - far short of the tier-1 schedule.
        game.Step();
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));

        game.Step();
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel3));
    }

    [Fact]
    public void FastHitsResetReaction_True_RepeatedSameTierHits_RestartNotStack()
    {
        // LifeTimer1 = 600 ms -> 3 frames at 5 Hz. Three tier-1 hits, one per Step(), each
        // re-arming the wake; the reaction must expire exactly 3 frames after the LAST hit
        // (case 7) - if durations stacked, it would still be active well past that point.
        var game = NewGame();
        var reactor = game.SpawnObject("ReactorShortHoldFastReset", game.CivilianPlayer, Vector3.Zero);

        game.Step(); // cross the sleepy-update boundary
        Damage(reactor, 10f);
        game.Step(); // hit 1 lands
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        Damage(reactor, 11f);
        game.Step(); // hit 2 lands: re-arm, not stack
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        Damage(reactor, 12f);
        game.Step(); // hit 3 (last hit) lands: re-arm, not stack
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        // Exactly 3 frames after the last hit, not 3 hits * 3 frames = 9.
        game.Step();
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        game.Step();
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        game.Step();
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
    }

    [Fact]
    public void OnHealing_IsNotImplemented_HealingNeverAffectsReaction()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);

        game.Step();
        Damage(reactor, 10f);
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        reactor.AttemptHealing(5f, reactor);
        game.Step();

        // Healing does not clear or alter the in-flight reaction; only the armed Update() expiry
        // does (case: ReactionAutoExpires_AfterItsTiersLifeTimer). Frame 2 of the 2-frame hold.
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));

        // Nor does it extend it: the reaction still expires on the schedule the hit armed.
        game.Step();
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
        Assert.False(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
    }

    [Fact]
    public void SleepyUpdate_OnDamageOnFirstPostSpawnStep_StillSetsFlagsImmediately()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);

        // No boundary-crossing Step() first: OnDamage is a direct dispatch from
        // ActiveBody.AttemptDamage, not gated by the sleepy-update rule that only applies to
        // this module's own Update().
        Damage(reactor, 10f);
        game.Step();

        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitReaction));
        Assert.True(reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidReaction()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);
        var live = ReactionModuleOf(reactor);

        game.Step();
        Damage(reactor, 10f);
        game.Step();

        var shadowHost = game.SpawnObject("Reactor", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ReactionModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_NoActiveReaction()
    {
        var game = NewGame();
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);
        var live = ReactionModuleOf(reactor);

        var shadowHost = game.SpawnObject("Reactor", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ReactionModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidReaction_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the module state (and the
        // engine-owned wake frame) through Save->Load mid-reaction; if the load path lost or
        // misread anything, B's continuation diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var reactor = game.SpawnObject("Reactor", game.CivilianPlayer, Vector3.Zero);
        var module = ReactionModuleOf(reactor);

        game.Step();
        Damage(reactor, 10f);

        var trajectory = new bool[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = reactor.ModelConditionFlags.Get(ModelConditionFlag.HitLevel1);
        }

        return trajectory;
    }
}

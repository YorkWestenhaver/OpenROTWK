// Mocked-game contract tests for the BoredUpdate port (R13), following
// bfme2-workbench/research/modules-r13/specs/BoredUpdateModuleData.md §3: the scan + BoredFilter
// gate (§1.1), the match-found-fires polarity (§1.2, F-BU-1), the self-target activation (§1.3,
// F-BU-3), the unconditional re-arm and overwrite-not-suppress behavior (§1.4), the
// TryConsumePendingActivation seam, the shadow-copy base test, and a mid-scan save/load
// round-trip.
//
// Sleepy-update caveat (api-freeze-v1 §S6, confirmed by AutoPickUpUpdateContractTests): a freshly
// spawned module's NextCallFrame is floored to "now" at creation, and Update() only runs once
// CurrentFrame >= NextCallFrame - the tick that observes CurrentFrame == N runs on the (N+1)th
// HeadlessSimGame.Step() call. A freshly spawned module's first real Update() therefore lands on
// the second Step(), never the first.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class BoredUpdateContractTests
{
    // 5 Hz logic rate (F6): 1000ms = 5 frames.
    private const string Definitions = @"
Object BoredTroll
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BoredUpdate BoredModuleTagOne
    ScanDelayTime = 1000
    ScanDistance = 50
    BoredFilter = NONE +TrollishStew
    SpecialPowerTemplate = SpecialAbilityWildTrollCooking
    CanScanWhileAttackingOrMoving = No
  End
End

Object TrollishStew
  KindOf = TrollishStew NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object NonStewProp
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB03ED) // "bored"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static BoredUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<BoredUpdate>().Single();

    [Fact]
    public void NoCandidate_NoPendingActivation()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));

        StepFrames(game, 6);

        Assert.False(ModuleOf(troll).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void MatchingCandidateInRange_SetsPendingActivation_TargetsSelf()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        var module = ModuleOf(troll);
        Assert.True(module.TryConsumePendingActivation(out var targetId));
        Assert.Equal(troll.Id, targetId);
    }

    [Fact]
    public void OutOfRangeCandidate_NoPendingActivation()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(300, 100, 0));

        StepFrames(game, 6);

        Assert.False(ModuleOf(troll).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void NonMatchingCandidate_FilterExcludes_NoPendingActivation()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("NonStewProp", game.CivilianPlayer, new Vector3(120, 100, 0));

        StepFrames(game, 6);

        Assert.False(ModuleOf(troll).PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void RearmsOnScanDelayTime_RegardlessOfOutcome()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(300, 100, 0));

        // First scan (module observes CurrentFrame == 5, seen on the 6th Step()): the only
        // candidate is out of range, so the scan fails - a failed scan must not stop the module
        // from re-arming.
        StepFrames(game, 6);
        Assert.False(ModuleOf(troll).PendingActivationTargetId.IsValid);

        // A qualifying candidate now appears in range (the far one stays out of range and does
        // not interfere). It must not be picked up before the module's next scheduled cadence
        // tick (CurrentFrame == 10, seen on the 11th Step() - 5 more Step() calls from here),
        // proving the module is asleep in between, not busy-looping - and it must be picked up
        // exactly on that tick, proving the failed scan did re-arm rather than sleeping forever.
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(120, 100, 0));
        StepFrames(game, 4);
        Assert.False(ModuleOf(troll).PendingActivationTargetId.IsValid, "must not re-scan before the next cadence tick");
        StepFrames(game, 1);
        Assert.True(ModuleOf(troll).PendingActivationTargetId.IsValid, "must re-scan exactly on the next cadence tick");
    }

    [Fact]
    public void SecondMatchOverwritesUnconsumedPending()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(120, 100, 0));
        var module = ModuleOf(troll);

        // First scan (CurrentFrame == 5, 6th Step()): match found, pending activation set.
        StepFrames(game, 6);
        Assert.True(module.PendingActivationTargetId.IsValid);
        Assert.Equal(troll.Id, module.PendingActivationTargetId);

        // Do not consume. Second scan (CurrentFrame == 10, 5 more Step() calls): still matching -
        // the pending activation is overwritten (still self), not suppressed by the prior
        // unconsumed one.
        StepFrames(game, 5);
        Assert.True(module.PendingActivationTargetId.IsValid);
        Assert.Equal(troll.Id, module.PendingActivationTargetId);
    }

    [Fact]
    public void TryConsumePendingActivation_ClearsPendingState()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(120, 100, 0));
        var module = ModuleOf(troll);

        StepFrames(game, 6);

        Assert.True(module.TryConsumePendingActivation(out var targetId));
        Assert.Equal(troll.Id, targetId);

        Assert.False(module.TryConsumePendingActivation(out _));
        Assert.False(module.PendingActivationTargetId.IsValid);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidScan()
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(120, 100, 0));
        var live = ModuleOf(troll);

        StepFrames(game, 6);
        Assert.True(live.PendingActivationTargetId.IsValid);

        var shadow = ModuleOf(game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidScan_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame();
        var troll = game.SpawnObject("BoredTroll", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("TrollishStew", game.CivilianPlayer, new Vector3(120, 100, 0));
        var module = ModuleOf(troll);

        var trajectory = new bool[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = module.PendingActivationTargetId.IsValid;
        }

        return trajectory;
    }

    [Fact]
    public void CanScanWhileAttackingOrMoving_ParsesWithoutThrowing()
    {
        // F-BU-2: real parse gap - CanScanWhileAttackingOrMoving is authored in live AotR data
        // (trollheroes.ini) but was previously absent from the engine, so unmodified data threw
        // on parse. This confirms the field now round-trips; there is nothing observable to
        // assert beyond the parsed value, since it is not wired into the scan gate.
        var game = NewGame();
        var data = (BoredUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("BoredTroll").Behaviors["BoredModuleTagOne"].Data;

        Assert.False(data.CanScanWhileAttackingOrMoving);
    }
}

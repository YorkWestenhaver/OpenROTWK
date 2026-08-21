// Mocked-game unit tests for the RunOffMapBehavior port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch from research/modules-r13/specs/RunOffMapBehaviorModuleData.md
// §4's test plan, [create -> tick -> observable effect], plus the mid-behavior save/load
// round-trip and the shadow-copy base test - the same shape as PilotFindVehicleUpdate's and
// AutoHealBehavior's sibling test files.
//
// The observables are the S2 movement seam the module drives (SimLocomotorUpdate's Mode and
// goal position, via IGameLogic.TryGetWaypointPosition's landed waypoint resolution), plus
// the terminal-condition side effects (GameObject.Kill() for DieOnMap=Yes,
// Context.GameLogic.DestroyObject for DieOnMap=No - the silent-vanish distinction is the
// entire point of exercising both in this one file, spec §4 case 7).
//
// The sleepy-update caveat, applied throughout (spec §4): a freshly spawned module's first
// Update() runs on the object's SECOND HeadlessSimGame.Step(), not the first.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class RunOffMapBehaviorContractTests
{
    // Speed 30/s -> 6/frame at the frozen 5 Hz. CloseEnoughDist widened from the 1.0 default
    // so a handful of frames closes it (arrival-branch tests).
    private const string Definitions = @"
Locomotor RunLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
  CloseEnoughDist = 5
End

Object Runner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = RunOffMapBehavior ModuleTag_Run
    RequiresSpecificTrigger = No
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
  Locomotor = SET_NORMAL RunLoco
End

Object RunnerTriggered
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = RunOffMapBehavior ModuleTag_Run
    RequiresSpecificTrigger = Yes
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
  Locomotor = SET_NORMAL RunLoco
End

Object RunnerSilentExit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = RunOffMapBehavior ModuleTag_Run
    RequiresSpecificTrigger = No
    RunOffMapWaypointName = FarExitWP
    DieOnMap = No
  End
  Locomotor = SET_NORMAL RunLoco
End

Object RunnerNoLocomotor
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RunOffMapBehavior ModuleTag_Run
    RequiresSpecificTrigger = No
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x40FF) // "ROM"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static RunOffMapBehavior RunOffMapOf(GameObject obj) =>
        obj.BehaviorModules.OfType<RunOffMapBehavior>().Single();

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().FirstOrDefault();

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    // ---------------------------------------------------------------- case 1: immediate move

    [Fact]
    public void RequiresSpecificTrigger_No_MoveIssuedImmediately()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = game.SpawnObject("Runner", game.CivilianPlayer, new Vector3(0, 0, 0));
        var loco = LocoOf(obj);

        // Sleepy-update caveat: the first Update() runs on the SECOND Step(), not the first.
        Step(game, 2);

        Assert.Equal(SimMoveMode.MoveToPosition, loco.Mode);
    }

    // ---------------------------------------------------------------- case 2: withheld until triggered

    [Fact]
    public void RequiresSpecificTrigger_Yes_NoTriggerCall_NeverMoves()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = game.SpawnObject("RunnerTriggered", game.CivilianPlayer, new Vector3(0, 0, 0));
        var loco = LocoOf(obj);

        Step(game, 20);

        Assert.Equal(SimMoveMode.Idle, loco.Mode);
    }

    // ---------------------------------------------------------------- case 3: external trigger, idempotent

    [Fact]
    public void RequiresSpecificTrigger_Yes_Triggered_MoveIssuedNextUpdate_TriggerIsIdempotent()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = game.SpawnObject("RunnerTriggered", game.CivilianPlayer, new Vector3(0, 0, 0));
        var module = RunOffMapOf(obj);
        var loco = LocoOf(obj);

        Step(game, 2);
        Assert.Equal(SimMoveMode.Idle, loco.Mode);

        module.Trigger();
        module.Trigger(); // idempotent: no crash, no double goal-position write

        Step(game, 1);
        Assert.Equal(SimMoveMode.MoveToPosition, loco.Mode);
    }

    // ---------------------------------------------------------------- case 4: unknown waypoint name (F-ROM-1)

    [Fact]
    public void UnknownWaypointName_SleepsForever_NoCrash()
    {
        var game = NewGame();
        // No waypoint named "ExitWP" is ever registered.
        var obj = game.SpawnObject("Runner", game.CivilianPlayer, new Vector3(0, 0, 0));
        var loco = LocoOf(obj);

        Step(game, 20);

        Assert.Equal(SimMoveMode.Idle, loco.Mode);
    }

    // ---------------------------------------------------------------- case 5: no SimLocomotorUpdate

    [Fact]
    public void NoLocomotor_SleepsForever_NoCrash()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = game.SpawnObject("RunnerNoLocomotor", game.CivilianPlayer, new Vector3(0, 0, 0));

        Step(game, 20);

        Assert.False(obj.IsEffectivelyDead);
        Assert.False(obj.IsDestroyed);
    }

    // ---------------------------------------------------------------- case 6: DieOnMap=Yes, arrival -> Kill()

    [Fact]
    public void DieOnMap_Yes_ArrivalAtWaypoint_KillsExactlyOnce()
    {
        var game = NewGame();
        // Close enough that a handful of steps (6/frame, CloseEnoughDist 5) reaches it.
        game.RegisterWaypoint("ExitWP", new Vector3(20, 0, 0));
        var obj = game.SpawnObject("Runner", game.CivilianPlayer, new Vector3(0, 0, 0));
        var loco = LocoOf(obj);

        // Step until the locomotor collapses to Maintain (arrived).
        for (var i = 0; i < 30 && loco.Mode != SimMoveMode.Maintain; i++)
        {
            game.Step();
        }

        Assert.Equal(SimMoveMode.Maintain, loco.Mode);

        game.Step(); // the Update() that observes arrival and calls Kill()

        Assert.True(obj.IsEffectivelyDead);

        // Step further: no second Kill(), no exception, no double-death artifact.
        Step(game, 5);
        Assert.True(obj.IsEffectivelyDead);
    }

    // ---------------------------------------------------------------- case 7: DieOnMap=No, leaving the map

    [Fact]
    public void DieOnMap_No_LeavingTheMap_DestroysSilently_NoDeath()
    {
        var game = NewGame();
        // Beyond the headless host's default +/-1000 pathfind grid extent: the mover crosses
        // out of the grid long before it would ever reach this waypoint.
        game.RegisterWaypoint("FarExitWP", new Vector3(5000, 0, 0));
        var obj = game.SpawnObject("RunnerSilentExit", game.CivilianPlayer, new Vector3(0, 0, 0));

        var destroyed = false;
        for (var i = 0; i < 400 && !destroyed; i++)
        {
            game.Step();
            destroyed = obj.IsDestroyed;
        }

        Assert.True(destroyed, "object never left the pathfind grid extent");
        // The silent-vanish distinction from case 6 is the entire point: no death sequence.
        Assert.False(obj.IsEffectivelyDead);
    }

    // ---------------------------------------------------------------- the walk

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = game.SpawnObject("Runner", game.CivilianPlayer, new Vector3(0, 0, 0));
        Step(game, 4); // drive real state: _moveIssued flips true
        var live = RunOffMapOf(obj);

        var shadowHost = game.SpawnObject("Runner", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = RunOffMapOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static long[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = game.SpawnObject("Runner", game.CivilianPlayer, new Vector3(0, 0, 0));
        var module = RunOffMapOf(obj);
        var loco = LocoOf(obj);

        var trajectory = new long[16];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = loco.Physics.Position.X.RawValue;
        }

        return trajectory;
    }
}

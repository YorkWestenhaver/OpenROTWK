// Mocked-game unit tests for the PilotFindVehicleUpdate port (api-freeze-v1 §6 fitness item
// 4): one test per landed-reachable behavior branch from the R12 task packet, [create -> tick
// -> observable effect], plus the mid-behavior save/load round-trip and the shadow-copy base
// test - the same shape as MobMemberSlavedUpdateContractTests, its direct analog for the
// AIUpdate-is-unfrozen movement seam (LOCO-F1).
//
// The observables are the S2 movement seam the module drives: the pilot's locomotor MODE
// (MoveToPosition once a vehicle is found and boarding is ordered) and its sim POSITION
// (closing on the vehicle), plus the DidMoveToBase bookkeeping flag (the base-center
// repositioning itself is an unlanded seam, F-PFV-3 - only the once-per-cycle attempt
// bookkeeping is tested).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class PilotFindVehicleUpdateContractTests
{
    // ScanRate 5 frames, ScanRange 200, MinHealth 0.5 (GPL default). Speed 30/s -> 6/frame at
    // the frozen 5 Hz.
    private const string Definitions = @"
Locomotor PilotLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object Pilot
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = PilotFindVehicleUpdate ModuleTag_Pilot
    ScanRate = 1000
    ScanRange = 200
    MinHealth = 0.5
  End
  Locomotor = SET_NORMAL PilotLoco
End

Object Jeep
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL PilotLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xFD07) // "pilot"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static PilotFindVehicleUpdate PilotOf(GameObject obj) =>
        obj.BehaviorModules.OfType<PilotFindVehicleUpdate>().Single();

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().First();

    /// <summary>A player whose IsHuman is true. The frozen Player contract has no public
    /// constructor path to a human player (IsHuman is a private setter, only ever assigned
    /// from Player.FromMapData), so the test reaches through reflection - this touches no
    /// production code, only how the test stands up its fixture.</summary>
    private static Player NewHumanPlayer(HeadlessSimGame game)
    {
        var player = new Player(99, null, new ColorRgb(255, 0, 0), game);
        typeof(Player).GetProperty(nameof(Player.IsHuman))!.SetValue(player, true);
        return player;
    }

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    // ---------------------------------------------------------------- boards a healthy vehicle

    [Fact]
    public void IdleWithHealthyFriendlyVehicleInRange_BoardsIt()
    {
        var game = NewGame();
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        var jeep = game.SpawnObject("Jeep", game.CivilianPlayer, new Vector3(50, 0, 0));
        var loco = LocoOf(pilot);

        Step(game, 6);

        Assert.Equal(SimMoveMode.MoveToPosition, loco.Mode);
        Assert.False(PilotOf(pilot).DidMoveToBase);

        // Keep ticking: the pilot should actually close the distance toward the jeep.
        Step(game, 20);
        Assert.True(loco.Physics.Position.X > Fix64.Zero,
            $"pilot never advanced toward the vehicle; x = {loco.Physics.Position.X}");
    }

    // ---------------------------------------------------------------- health gate

    [Fact]
    public void VehicleBelowMinHealth_IsIgnored_FallsBackToBaseAttempt()
    {
        var game = NewGame();
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        var jeep = game.SpawnObject("Jeep", game.CivilianPlayer, new Vector3(50, 0, 0));
        var loco = LocoOf(pilot);

        // Bring the jeep to 40% health: below the 0.5 MinHealth threshold.
        PortedModuleTestKit.ApplyDamage(jeep, amount: 60f);

        Step(game, 6);

        // Not boarded: the below-threshold vehicle was skipped.
        Assert.Equal(SimMoveMode.Idle, loco.Mode);
        // The GPL fallback ran once (F-PFV-3: the base position itself is unlanded).
        Assert.True(PilotOf(pilot).DidMoveToBase);
    }

    // ---------------------------------------------------------------- no vehicles -> base attempt once

    [Fact]
    public void NoVehiclesInRange_AttemptsBaseMoveOnlyOnce()
    {
        var game = NewGame();
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        var module = PilotOf(pilot);

        Assert.False(module.DidMoveToBase);
        Step(game, 6);
        Assert.True(module.DidMoveToBase);

        // Further scans with still nothing in range: the flag stays set (attempted once, not
        // re-attempted every cycle).
        Step(game, 20);
        Assert.True(module.DidMoveToBase);
    }

    // ---------------------------------------------------------------- base attempt, then a vehicle appears

    [Fact]
    public void VehicleAppearsAfterBaseAttempt_BoardsAndResetsFlag()
    {
        var game = NewGame();
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        var module = PilotOf(pilot);
        var loco = LocoOf(pilot);

        // First scan: nothing in range, base fallback attempted.
        Step(game, 6);
        Assert.True(module.DidMoveToBase);
        Assert.Equal(SimMoveMode.Idle, loco.Mode);

        // A friendly vehicle rolls into range before the next scan.
        game.SpawnObject("Jeep", game.CivilianPlayer, new Vector3(50, 0, 0));
        Step(game, 6);

        Assert.Equal(SimMoveMode.MoveToPosition, loco.Mode);
        Assert.False(module.DidMoveToBase);
    }

    // ---------------------------------------------------------------- human-controlled -> parked

    [Fact]
    public void HumanControlledPilot_NeverScans()
    {
        var game = NewGame();
        var human = NewHumanPlayer(game);
        var pilot = game.SpawnObject("Pilot", human, new Vector3(0, 0, 0));
        game.SpawnObject("Jeep", human, new Vector3(20, 0, 0)); // right next to the pilot
        var loco = LocoOf(pilot);

        // A vehicle sitting this close would certainly be boarded within a handful of scans
        // for an AI-owned pilot (see IdleWithHealthyFriendlyVehicleInRange_BoardsIt). A
        // human-owned one must never act.
        Step(game, 30);

        Assert.Equal(SimMoveMode.Idle, loco.Mode);
    }

    // ---------------------------------------------------------------- busy -> not idle -> no scan

    [Fact]
    public void BusyWithAMoveOrder_DoesNotScanUntilIdleAgain()
    {
        var game = NewGame();
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        var jeep = game.SpawnObject("Jeep", game.CivilianPlayer, new Vector3(50, 0, 0));
        var loco = LocoOf(pilot);

        // Give the pilot an outstanding move order of its own (as if another system, or an
        // earlier order, already has it in motion) before the module ever gets a chance to
        // scan: it must not clobber that order with a board attempt. The destination is away
        // from the jeep (negative X, phase-1 check below) but still within ScanRange (200) of
        // it once arrived, so the later re-scan in phase 3 can actually find it.
        loco.SetTargetPosition(new FixVector3(Fix64.FromDecimalLiteral("-50"), Fix64.Zero, Fix64.Zero), Fix64.FromDecimalLiteral("6"));

        Step(game, 6);
        // Still heading the way it was told, not toward the jeep: the module saw "not idle"
        // and skipped its scan.
        Assert.True(loco.Physics.Position.X < Fix64.Zero,
            $"pilot should still be honoring its own move order; x = {loco.Physics.Position.X}");

        // Let it arrive and go idle, then give the module room to scan again.
        Step(game, 200);
        Assert.True(SimMoveMode.Idle == loco.Mode || SimMoveMode.Maintain == loco.Mode);

        Step(game, 6);
        Assert.Equal(SimMoveMode.MoveToPosition, loco.Mode); // now it found and boarded the jeep
    }

    // ---------------------------------------------------------------- the walk

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        Step(game, 6); // drive real state: DidMoveToBase flips (no vehicle in range)
        var live = PilotOf(pilot);

        var shadowHost = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = PilotOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static long[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var pilot = game.SpawnObject("Pilot", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("Jeep", game.CivilianPlayer, new Vector3(80, 0, 0));
        var module = PilotOf(pilot);
        var loco = LocoOf(pilot);

        var trajectory = new long[24];
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

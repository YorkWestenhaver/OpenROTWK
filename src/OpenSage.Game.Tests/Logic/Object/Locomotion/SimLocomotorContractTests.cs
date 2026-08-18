// Mocked-game unit tests for the S2 locomotor/physics system (api-freeze-v1 §6 fitness
// item 4 shape, adapted from the Die-batch clones): core formulas and branches -
// acceleration to the speed limit, turn-rate clamping, braking/arrival, backwards
// movement, locomotor-set selection by surface and by set type, z-behavior lift - plus
// the shadow-copy base test and a mid-movement save/load continuation, plus a run-twice
// bit-determinism check over the whole trajectory (movement compounds every frame, so
// this system is THE flagship determinism target).
//
// Object definitions and locomotor templates are parsed from INI text through the real
// parser, so the quantizing rate parse functions are on the tested path.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Locomotion;

public class SimLocomotorContractTests
{
    // Speeds are dist/sec in INI; at the frozen 5 Hz: Speed 30 -> 6/frame,
    // Acceleration/Braking 100 -> 4/frame^2, TurnRate 90 -> 18 deg/frame.
    private const string Definitions = @"
Locomotor TestLegsLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Locomotor TestTreadsLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 90
  Acceleration = 100
  Braking = 100
  Appearance = TREADS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Locomotor TestWheelsLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 90
  Acceleration = 100
  Braking = 100
  MinTurnSpeed = 5
  CanMoveBackwards = Yes
  Appearance = FOUR_WHEELS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Locomotor TestWaterLoco
  Surfaces = WATER
  Speed = 20
  TurnRate = 90
  Acceleration = 100
  Braking = 100
  Appearance = OTHER
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Locomotor TestSluggishLoco
  Surfaces = GROUND
  Speed = 10
  TurnRate = 90
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Locomotor TestHoverLoco
  Surfaces = AIR
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Lift = 100
  PreferredHeight = 20
  Appearance = HOVER
  ZAxisBehavior = SURFACE_RELATIVE_HEIGHT
End

Object LegsWalker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestLegsLoco
End

Object TreadTank
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestTreadsLoco
End

Object WheelTruck
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestWheelsLoco
End

Object AmphibianScout
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestWaterLoco TestLegsLoco
  Locomotor = SET_SLUGGISH TestSluggishLoco
End

Object HoverScout
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
    Gravity = -25
  End
  Locomotor = SET_NORMAL TestHoverLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x10C0u)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().First();

    private static Fix64 F(string s) => Fix64.FromDecimalLiteral(s);

    private static FixVector3 Pos(string x, string y, string z = "0") =>
        new(F(x), F(y), F(z));

    // ------------------------------------------------------------------ math primitives

    [Fact]
    public void SimAngle_Normalize_WrapsIntoHalfOpenPi()
    {
        Assert.Equal(Fix64.Zero, SimAngle.Normalize(Fix64.PiTimes2));
        Assert.Equal(Fix64.Pi, SimAngle.Normalize(Fix64.Pi));
        // -Pi maps to (PiTimes2 - Pi), which differs from +Pi by the constants' last-bit
        // rounding (PiTimes2 is independently rounded, not 2*Pi bit-exact).
        var minusPiWrapped = SimAngle.Normalize(-Fix64.Pi);
        Assert.True(Fix64.Abs(minusPiWrapped - Fix64.Pi) <= Fix64.FromRaw(2));
        var threePi = Fix64.Pi + Fix64.PiTimes2;
        Assert.Equal(Fix64.Pi, SimAngle.Normalize(threePi));
    }

    [Fact]
    public void SimAngle_Diff_TakesTheShortWayAround()
    {
        // 350deg -> 10deg is +20deg, not -340deg.
        var a350 = F("6.1086523819801535");
        var a10 = F("0.17453292519943295");
        var diff = SimAngle.Diff(a10, a350);
        Assert.True(diff > Fix64.Zero);
        Assert.True(Fix64.Abs(diff - F("0.349066")) < F("0.001"));
    }

    [Fact]
    public void CalcSlowDownDist_MatchesTheClosedForm()
    {
        // 1.05 * (6-0)^2 / (2*4) = 4.725
        var dist = SimLocomotor.CalcSlowDownDist(F("6"), Fix64.Zero, F("4"));
        Assert.True(Fix64.Abs(dist - F("4.725")) < F("0.0001"));

        // Already at/below the desired speed: zero.
        Assert.Equal(Fix64.Zero, SimLocomotor.CalcSlowDownDist(F("2"), F("3"), F("4")));
    }

    // ------------------------------------------------------------------ acceleration & speed limit

    [Fact]
    public void Legs_AccelerateToMaxSpeed_AndHoldIt()
    {
        // Speed 30/s -> 6/frame; Acceleration 100/s^2 -> 4/frame^2. The accel force is
        // clipped to exactly the remaining speed delta, so the per-frame forward speeds
        // are 4, 6, 6, 6, ... - the speed LIMIT binds from frame 2 on.
        var game = NewGame();
        var walker = game.SpawnObject("LegsWalker", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(walker);

        loco.SetTargetPosition(Pos("1000", "0"), F("1000"));   // desired clamps to max

        var speeds = new List<Fix64>();
        for (var i = 0; i < 7; i++)
        {
            game.Step();
            speeds.Add(loco.Physics.ForwardSpeed2D());
        }

        // The module's first wake is one frame after spawn (SetWakeFrame(None) = now+1),
        // so step 0 is inert; then the accel clip gives 4, then the speed limit binds.
        Assert.Equal(Fix64.Zero, speeds[0]);
        Assert.Equal(F("4"), speeds[1]);
        for (var i = 2; i < speeds.Count; i++)
        {
            Assert.Equal(F("6"), speeds[i]);
        }

        // And the position integration matches: x = 4 + 6*(n-1) over the moving frames.
        Assert.Equal(F("34"), loco.Physics.Position.X);   // 4 + 5*6
        Assert.Equal(Fix64.Zero, loco.Physics.Position.Y);
    }

    [Fact]
    public void DesiredSpeed_BelowMax_IsRespected()
    {
        var game = NewGame();
        var walker = game.SpawnObject("LegsWalker", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(walker);

        loco.SetTargetPosition(Pos("1000", "0"), F("3"));   // half the max

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        Assert.Equal(F("3"), loco.Physics.ForwardSpeed2D());
    }

    // ------------------------------------------------------------------ turn-rate clamp

    [Fact]
    public void Treads_TurnIsClampedToTurnRatePerFrame()
    {
        // TurnRate 90 deg/s -> 18 deg/frame = 0.31415926 rad. Goal at +Y is 90 deg away:
        // the yaw must advance by exactly the clamp for 5 frames, then align.
        var game = NewGame();
        var tank = game.SpawnObject("TreadTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        var perFrame = loco.CurrentLocomotor.Template.SimMaxTurnRate;

        loco.SetTargetPosition(Pos("0", "1000"), F("1000"));

        game.Step();   // inert frame: first wake is spawn+1
        Assert.Equal(Fix64.Zero, loco.Physics.Yaw);

        for (var i = 1; i <= 5; i++)
        {
            game.Step();
            var expected = perFrame * Fix64.FromRaw((long)i << 32);
            // While still short of 90 deg the clamp binds exactly; near alignment the
            // remaining delta may be smaller than the clamp.
            if (expected < Fix64.PiOver2)
            {
                Assert.Equal(expected, loco.Physics.Yaw);
            }
        }

        // Aligned (within one LUT step) and moving up the +Y axis.
        game.Step();
        Assert.True(Fix64.Abs(loco.Physics.Yaw - Fix64.PiOver2) < F("0.001"));
        Assert.True(loco.Physics.Velocity.Y > Fix64.Zero);
    }

    [Fact]
    public void Treads_SpeedIsModulatedByTurn()
    {
        // While more than 45 deg from the goal heading, angleCoeff caps at 1 and the
        // goal speed is 0: a tank facing away does not surge forward mid-pivot.
        var game = NewGame();
        var tank = game.SpawnObject("TreadTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);

        loco.SetTargetPosition(Pos("0", "1000"), F("1000"));
        game.Step();   // 18 of 90 deg turned; still > 45 deg to go

        Assert.Equal(Fix64.Zero, loco.Physics.ForwardSpeed2D());
    }

    // ------------------------------------------------------------------ braking & arrival

    [Fact]
    public void Treads_ArriveAtTheGoal_AndStop()
    {
        var game = NewGame();
        var tank = game.SpawnObject("TreadTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        var goal = Pos("60", "0");

        loco.SetTargetPosition(goal, F("1000"));

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        // Arrived: goal collapsed to Maintain, position within CloseEnoughDist (1) + one
        // braking step of the goal, 2D velocity dead.
        Assert.Equal(SimMoveMode.Maintain, loco.Mode);
        Assert.True(Fix64.Abs(loco.Physics.Position.X - goal.X) <= F("2.5"),
            $"stopped at {loco.Physics.Position.X}");
        Assert.Equal(Fix64.Zero, loco.Physics.Velocity.X);
        Assert.Equal(Fix64.Zero, loco.Physics.Velocity.Y);
    }

    [Fact]
    public void Braking_SetsTheBrakingStatus_AndTheCheatWalksExactlyIn()
    {
        var game = NewGame();
        var tank = game.SpawnObject("TreadTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        loco.SetTargetPosition(Pos("60", "0"), F("1000"));

        var sawBraking = false;
        for (var i = 0; i < 40 && loco.Mode == SimMoveMode.MoveToPosition; i++)
        {
            game.Step();
            sawBraking |= loco.CurrentLocomotor.IsBraking;
        }
        Assert.True(sawBraking, "the braking flag never engaged on approach");
        Assert.Equal(SimMoveMode.Maintain, loco.Mode);
    }

    // ------------------------------------------------------------------ backwards (wheels)

    [Fact]
    public void Wheels_GoalBehind_EngagesBackwardsMovement()
    {
        var game = NewGame();
        var truck = game.SpawnObject("WheelTruck", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(truck);

        // Facing +X, goal directly behind: |relAngle| = 180 deg > 90, CanMoveBackwards.
        loco.SetTargetPosition(Pos("-30", "0"), F("1000"));
        game.Step();   // inert frame: first wake is spawn+1
        game.Step();

        Assert.True(loco.CurrentLocomotor.IsMovingBackwards);
    }

    // ------------------------------------------------------------------ set selection

    [Fact]
    public void LocomotorSet_SelectsBySurface_InDeclarationOrder()
    {
        var game = NewGame();
        var scout = game.SpawnObject("AmphibianScout", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(scout);

        // SET_NORMAL declares water first, land second.
        Assert.Equal(2, loco.LocomotorSet.Locomotors.Count);
        Assert.Equal(Surfaces.Water | Surfaces.Ground, loco.LocomotorSet.ValidSurfaces);

        // First-declared wins on a mask matching both...
        loco.ChooseLocomotor(Surfaces.Water | Surfaces.Ground);
        Assert.Equal(Surfaces.Water, loco.CurrentLocomotor.LegalSurfaces);

        // ...and the mask narrows deterministically.
        loco.ChooseLocomotor(Surfaces.Ground);
        Assert.Equal(Surfaces.Ground, loco.CurrentLocomotor.LegalSurfaces);
        Assert.Null(loco.LocomotorSet.FindLocomotor(Surfaces.Cliff));
    }

    [Fact]
    public void LocomotorSet_SelectsBySetType_WithNormalFallback()
    {
        var game = NewGame();
        var scout = game.SpawnObject("AmphibianScout", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(scout);

        // A declared alternate set switches to it.
        Assert.True(loco.SetLocomotorSet(LocomotorSetType.Sluggish));
        Assert.Equal(LocomotorSetType.Sluggish, loco.CurrentSetType);
        Assert.Single(loco.LocomotorSet.Locomotors);
        Assert.Equal(F("2"), loco.CurrentLocomotor.Template.SimMaxSpeed);   // 10/s -> 2/frame

        // An undeclared set type falls back to SET_NORMAL.
        Assert.True(loco.SetLocomotorSet(LocomotorSetType.Panic));
        Assert.Equal(LocomotorSetType.Normal, loco.CurrentSetType);
        Assert.Equal(2, loco.LocomotorSet.Locomotors.Count);
    }

    // ------------------------------------------------------------------ z behavior (lift)

    [Fact]
    public void Hover_LiftsToPreferredHeight_AndHoldsIt()
    {
        // Gravity -25/s^2 -> -1/frame^2; Lift 100/s^2 -> 4/frame^2 (net +3). Preferred
        // height 20 above a flat 0 surface: the climb must reach and then hold a band
        // around 20 (the a=2(dz-v) solver overshoots by at most a step).
        var game = NewGame();
        var hover = game.SpawnObject("HoverScout", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(hover);

        loco.SetTargetPosition(Pos("300", "0", "20"), F("1000"));

        for (var i = 0; i < 30; i++)
        {
            game.Step();
        }

        var z = loco.Physics.Position.Z;
        Assert.True(z > F("15") && z < F("25"), $"hover z = {z}");
        Assert.True(Fix64.Abs(loco.Physics.Velocity.Z) < F("2"));
    }

    // ------------------------------------------------------------------ determinism (run-twice)

    [Fact]
    public void RunTwice_TrajectoriesAreBitIdentical()
    {
        // The whole point of the system: two engines, same seed, same script -> the same
        // RAW BITS at every frame, for every field of the transform.
        var a = RunTrajectory(roundTripAtFrame: -1);
        var b = RunTrajectory(roundTripAtFrame: -1);
        Assert.Equal(a, b);
    }

    // ------------------------------------------------------------------ the walk

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidMovement()
    {
        var game = NewGame();
        var tank = game.SpawnObject("TreadTank", game.CivilianPlayer, Vector3.Zero);
        var live = LocoOf(tank);

        live.SetTargetPosition(Pos("80", "40"), F("1000"));
        for (var i = 0; i < 7; i++)
        {
            game.Step();   // mid-move: turning, accelerating, donut timer live
        }
        Assert.Equal(SimMoveMode.MoveToPosition, live.Mode);

        var shadowHost = game.SpawnObject("TreadTank", game.CivilianPlayer, new Vector3(500, 0, 0));
        var shadow = LocoOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidMovement_SaveLoadRoundTrip_ContinuesBitIdentically()
    {
        // Game B round-trips the module through Save->Load at frame 5, mid-turn and
        // mid-acceleration. Every subsequent position/yaw must match game A to the bit.
        var a = RunTrajectory(roundTripAtFrame: -1);
        var b = RunTrajectory(roundTripAtFrame: 5);
        Assert.Equal(a, b);
    }

    private static (long X, long Y, long Z, long Yaw)[] RunTrajectory(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xCAFE);
        var tank = game.SpawnObject("TreadTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        loco.SetTargetPosition(Pos("80", "40"), F("1000"));

        var trajectory = new (long, long, long, long)[25];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(loco, PortedModuleTestKit.Save(loco));
            }
            game.Step();
            trajectory[i] = (
                loco.Physics.Position.X.RawValue,
                loco.Physics.Position.Y.RawValue,
                loco.Physics.Position.Z.RawValue,
                loco.Physics.Yaw.RawValue);
        }
        return trajectory;
    }

    // ------------------------------------------------------------------ parse quantization

    [Fact]
    public void RateParse_QuantizesPerSecondToPerFrame()
    {
        var game = NewGame();
        var template = game.AssetStore.LocomotorTemplates.GetByName("TestLegsLoco");

        Assert.Equal(F("6"), template.SimMaxSpeed);          // 30 / 5
        Assert.Equal(F("4"), template.SimAcceleration);      // 100 / 25
        Assert.Equal(F("4"), template.SimBraking);
        // TurnRate 360 deg/s -> 72 deg/frame = 2*Pi/5 rad (within rounding at raw scale).
        Assert.True(Fix64.Abs(template.SimMaxTurnRate - Fix64.FromRaw(Fix64.PiTimes2.RawValue / 5)) <= Fix64.FromRaw(2));
        // Damaged values defaulted to the undamaged ones (validate fix-up).
        Assert.Equal(template.SimMaxSpeed, template.SimMaxSpeedDamaged);
    }
}

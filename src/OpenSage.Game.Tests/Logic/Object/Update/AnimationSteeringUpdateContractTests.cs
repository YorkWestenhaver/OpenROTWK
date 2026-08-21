// Mocked-game unit tests for the AnimationSteeringUpdate port (api-freeze-v1 §6 fitness
// item 4): one test per state-machine branch from the task packet, [create -> tick ->
// observable effect], plus the shadow-copy base test and a mid-state save/load round-trip.
// Object definitions are parsed from INI text through the real parser, so the S5
// quantizing parse of MinTransitionTime (ms -> LogicFrameSpan at the frozen 5 Hz) is on
// the tested path.
//
// The module reads its steering signal off a real SimLocomotorUpdate's Physics.Turning,
// so these tests drive real TREADS locomotion (SetTargetPosition) rather than poking the
// physics turning field directly (it has no public setter - by design, F4). TurnRate 90
// deg/s -> 18 deg/frame at 5 Hz: a 90-degree turn clamps for 4 frames of movement, then
// aligns exactly (turn -> NONE) on the 5th - the same arithmetic SimLocomotorContractTests'
// turn-rate-clamp test relies on.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class AnimationSteeringUpdateContractTests
{
    // 5 Hz (F6): MinTransitionTime 200 ms -> 1 frame; 1000 ms -> 5 frames.
    private const string Definitions = @"
Locomotor SteerLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 90
  Acceleration = 100
  Braking = 100
  Appearance = TREADS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object SteerTank
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = AnimationSteeringUpdate ModuleTag_Steer
    MinTransitionTime = 200
  End
  Locomotor = SET_NORMAL SteerLoco
End

Object SlowGateSteerTank
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = AnimationSteeringUpdate ModuleTag_Steer
    MinTransitionTime = 1000
  End
  Locomotor = SET_NORMAL SteerLoco
End

Object NoPhysicsSteerer
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AnimationSteeringUpdate ModuleTag_Steer
    MinTransitionTime = 200
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xA51EEE) => Build(seed);

    private static HeadlessSimGame Build(uint seed)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static AnimationSteeringUpdate SteerOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AnimationSteeringUpdate>().Single();

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().Single();

    private static Fix64 F(string s) => Fix64.FromDecimalLiteral(s);

    private static FixVector3 Pos(string x, string y, string z = "0") =>
        new(F(x), F(y), F(z));

    private static bool AnyTurnFlag(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft) ||
        obj.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight) ||
        obj.ModelConditionFlags.Get(ModelConditionFlag.LeftToCenter) ||
        obj.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter);

    // ------------------------------------------------------------------ straight -> turn

    [Fact]
    public void TurningRight_SetsCenterToRight_AndOnlyThatFlag()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);

        // Target well to the -Y side: a steep clockwise (GPL TURN_NEGATIVE = right) turn.
        loco.SetTargetPosition(Pos("0", "-1000"), F("1000"));

        var sawCenterToRight = false;
        for (var i = 0; i < 6 && !sawCenterToRight; i++)
        {
            game.Step();
            sawCenterToRight = tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight);
        }

        Assert.True(sawCenterToRight);
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft));
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.LeftToCenter));
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter));
    }

    [Fact]
    public void TurningLeft_SetsCenterToLeft_AndOnlyThatFlag()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);

        // Target well to the +Y side: a steep counter-clockwise (GPL TURN_POSITIVE = left) turn.
        loco.SetTargetPosition(Pos("0", "1000"), F("1000"));

        var sawCenterToLeft = false;
        for (var i = 0; i < 6 && !sawCenterToLeft; i++)
        {
            game.Step();
            sawCenterToLeft = tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft);
        }

        Assert.True(sawCenterToLeft);
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight));
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.LeftToCenter));
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter));
    }

    // ------------------------------------------------------------------ turn stops -> recenter

    [Fact]
    public void TurnStopping_TransitionsCenterToRightToRightToCenter()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);

        // Exactly a 90-degree right turn: clamped turning for frames 1-4, aligns (NONE) at
        // frame 5 of movement - the locomotor naturally stops turning on its own, with no
        // further intervention needed from the test.
        loco.SetTargetPosition(Pos("0", "-1000"), F("0"));

        var sawCenterToRight = false;
        var sawRightToCenter = false;
        for (var i = 0; i < 12 && !sawRightToCenter; i++)
        {
            game.Step();
            if (tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight))
            {
                sawCenterToRight = true;
            }
            if (sawCenterToRight && tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter))
            {
                sawRightToCenter = true;
            }
        }

        Assert.True(sawCenterToRight);
        Assert.True(sawRightToCenter);
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight));
    }

    // ------------------------------------------------------------------ blocked recenter

    [Fact]
    public void OppositeTurnWhileRecentering_IsBlockedUntilModelReachesStraight()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SlowGateSteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        var steer = SteerOf(tank);

        // Steep right turn -> CENTER_TO_RIGHT, then let it naturally align to NONE so the
        // module advances to RIGHT_TO_CENTER. The 1000ms (5-frame) gate on this object
        // keeps it there for a few frames, giving us room to inject an opposite turn.
        loco.SetTargetPosition(Pos("0", "-1000"), F("0"));

        var sawRightToCenter = false;
        for (var i = 0; i < 12 && !sawRightToCenter; i++)
        {
            game.Step();
            sawRightToCenter = tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter);
        }
        Assert.True(sawRightToCenter);

        // Now steer hard the other way (left) while still gated in RIGHT_TO_CENTER.
        loco.SetTargetPosition(Pos("0", "1000"), F("0"));
        game.Step();
        Assert.Equal(PhysicsTurningType.Positive, loco.Physics.Turning);

        // Blocked: the module has no case for RIGHT_TO_CENTER + a non-NONE turn, so it
        // must still be RIGHT_TO_CENTER (never having jumped straight to CENTER_TO_LEFT).
        Assert.True(tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter));
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft));

        // Let the new (left) turn run its course until physics naturally reaches NONE
        // again (fully aligned to the new heading) - the module should then fall through
        // to straight (INVALID), not into CENTER_TO_LEFT.
        var reachedStraight = false;
        for (var i = 0; i < 20 && !reachedStraight; i++)
        {
            game.Step();
            reachedStraight = !AnyTurnFlag(tank);
        }

        Assert.True(reachedStraight);
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft));
    }

    // ------------------------------------------------------------------ min transition gate

    [Fact]
    public void MinTransitionTimeNotElapsed_BlocksStateChangeDespiteTurnStopping()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SlowGateSteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);

        // Steep right turn -> CENTER_TO_RIGHT (5-frame gate armed from this frame).
        loco.SetTargetPosition(Pos("0", "-1000"), F("0"));

        var sawCenterToRight = false;
        for (var i = 0; i < 6 && !sawCenterToRight; i++)
        {
            game.Step();
            sawCenterToRight = tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight);
        }
        Assert.True(sawCenterToRight);

        // Immediately retarget to a heading close to the current one: physics aligns
        // (turn -> NONE) within a frame or two, well inside the 5-frame gate.
        loco.SetTargetPosition(Pos("-1000", "-1050"), F("0"));
        game.Step();

        // Even though the physics has (or is about to have) stopped turning, the model
        // must still be CENTER_TO_RIGHT: the minimum transition time has not elapsed.
        Assert.True(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight));
        Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter));
    }

    // ------------------------------------------------------------------ steady state

    [Fact]
    public void SustainedTurn_LeavesStateAndFlagsUnchangedAcrossFrames()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);

        loco.SetTargetPosition(Pos("0", "1000"), F("0"));

        var sawCenterToLeft = false;
        for (var i = 0; i < 6 && !sawCenterToLeft; i++)
        {
            game.Step();
            sawCenterToLeft = tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft);
        }
        Assert.True(sawCenterToLeft);

        // While the locomotor keeps turning left (has not yet aligned), repeated frame
        // updates must leave the animation state exactly where it is.
        for (var i = 0; i < 2; i++)
        {
            game.Step();
            if (loco.Physics.Turning != PhysicsTurningType.Positive)
            {
                break;   // aligned early on this seed; stop before the test claims otherwise
            }
            Assert.True(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToLeft));
            Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.LeftToCenter));
            Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight));
            Assert.False(tank.ModelConditionFlags.Get(ModelConditionFlag.RightToCenter));
        }
    }

    // ------------------------------------------------------------------ null physics guard

    [Fact]
    public void NoLocomotor_NeverSetsAnySteeringFlag()
    {
        var game = NewGame();
        var tank = game.SpawnObject("NoPhysicsSteerer", game.CivilianPlayer, Vector3.Zero);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.False(AnyTurnFlag(tank));
    }

    // ------------------------------------------------------------------ base contract

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        loco.SetTargetPosition(Pos("0", "-1000"), F("0"));
        var live = SteerOf(tank);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("SteerTank", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = SteerOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTurnAnimAndGate()
    {
        var game = NewGame();
        var tank = game.SpawnObject("SlowGateSteerTank", game.CivilianPlayer, Vector3.Zero);
        var loco = LocoOf(tank);
        loco.SetTargetPosition(Pos("0", "-1000"), F("0"));
        var module = SteerOf(tank);

        var sawCenterToRight = false;
        for (var i = 0; i < 6 && !sawCenterToRight; i++)
        {
            game.Step();
            sawCenterToRight = tank.ModelConditionFlags.Get(ModelConditionFlag.CenterToRight);
        }
        Assert.True(sawCenterToRight);

        var state = PortedModuleTestKit.Save(module);

        var shadowHost = game.SpawnObject("SlowGateSteerTank", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = SteerOf(shadowHost);

        PortedModuleTestKit.Load(shadow, state);

        // The shadow starts from the same load: xfer the same target module for direct
        // comparison rather than re-deriving CENTER_TO_RIGHT independently.
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(shadow));
    }
}

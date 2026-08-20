// Mocked-game unit tests for the SmartBombTargetHomingUpdate port (api-freeze-v1 §6 fitness
// item 4): one test per behavior branch from the R12 task packet, [create -> tick ->
// observable effect], plus the mid-behavior save/load round-trip and the shadow-copy base
// test - the same shape as CheckpointUpdateContractTests, its category exemplar.
//
// The observable effect is the object's own X/Y position (Translation), which the module
// pulls/pushes through the SimTransformBridge float-substrate crossing every frame it runs.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class SmartBombTargetHomingUpdateContractTests
{
    // GameData.Gravity mirrors EjectPilotDieContractTests: what IsSignificantlyAboveTerrain
    // measures against (-9 * gravity), declared so the air/ground split is a property of the
    // data, not of a zero default.
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object Bomb
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = SmartBombTargetHomingUpdate ModuleTag_Homing
    CourseCorrectionScalar = 0.99
  End
End

Object BombZeroScalar
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = SmartBombTargetHomingUpdate ModuleTag_Homing
    CourseCorrectionScalar = 0.0
  End
End

Object BombFullScalar
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = SmartBombTargetHomingUpdate ModuleTag_Homing
    CourseCorrectionScalar = 1.0
  End
End
";

    // Well above the -9*Gravity = 9 threshold; OnGround is well below it (0).
    private static readonly Vector3 HighUp = new(0, 0, 500);
    private static readonly Vector3 OnGround = new(0, 0, 0);

    private static HeadlessSimGame NewGame(uint seed = 0x50038) // "smb"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SmartBombTargetHomingUpdate ModuleOf(GameObject obj) =>
        obj.FindBehavior<SmartBombTargetHomingUpdate>();

    private static FixVector3 Target(int x, int y, int z) =>
        new FixVector3(new Fix64(x), new Fix64(y), new Fix64(z));

    // ---- TC1: target set before first update -> interpolates every frame ----

    [Fact]
    public void TargetSetBeforeFirstUpdate_InterpolatesTowardTargetEveryFrame()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("Bomb", game.CivilianPlayer, HighUp);
        ModuleOf(bomb).SetTargetPosition(Target(1000, 0, 0));

        var before = bomb.Translation;
        // A freshly spawned sleepy update module's very first Update() lands on the tick
        // after the one it was created on (SetWakeFrame(UpdateSleepTime.None) is a 1-frame
        // minimum scheduling latency shared by every module, GameLogic.cs) - this first
        // Step() only reaches that arming tick, not the module's first real tick yet.
        game.Step();
        game.Step();
        var afterOneStep = bomb.Translation;

        // scalar = 0.99: pulled 1% of the way toward target.x=1000 from x=0 -> x ~= 10.
        Assert.True(afterOneStep.X > before.X);
        Assert.Equal(10.0f, afterOneStep.X, 1);

        game.Step();
        var afterTwoSteps = bomb.Translation;
        Assert.True(afterTwoSteps.X > afterOneStep.X);
    }

    // ---- TC2: CourseCorrectionScalar = 0.0 -> direct jump to target each frame ----

    [Fact]
    public void ZeroScalar_MovesDirectlyToTargetEachFrame()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("BombZeroScalar", game.CivilianPlayer, HighUp);
        ModuleOf(bomb).SetTargetPosition(Target(123, 456, 0));

        // Two steps: the first only reaches the module's arming tick (SetWakeFrame(None)'s
        // 1-frame minimum latency, shared by every sleepy update module - see the sibling
        // interpolation test above), the second is its first real Update().
        game.Step();
        game.Step();

        var pos = bomb.Translation;
        Assert.Equal(123.0f, pos.X, 2);
        Assert.Equal(456.0f, pos.Y, 2);
        // Z is preserved from the current position, never overwritten by the target.
        Assert.Equal(HighUp.Z, pos.Z, 2);
    }

    // ---- TC3: CourseCorrectionScalar = 1.0 -> target ignored, position unchanged ----

    [Fact]
    public void FullScalar_IgnoresTargetAndHoldsPosition()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("BombFullScalar", game.CivilianPlayer, HighUp);
        ModuleOf(bomb).SetTargetPosition(Target(9999, 9999, 0));

        var before = bomb.Translation;
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        var after = bomb.Translation;

        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
    }

    // ---- TC4: below the significantly-above-terrain threshold -> no-op every frame ----

    [Fact]
    public void BelowTerrainThreshold_UpdateIsNoOp()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("Bomb", game.CivilianPlayer, OnGround);
        Assert.False(bomb.IsSignificantlyAboveTerrain);
        ModuleOf(bomb).SetTargetPosition(Target(1000, 0, 0));

        var before = bomb.Translation;
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        var after = bomb.Translation;

        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Z, after.Z, 3);
    }

    // ---- TC5: no target ever set -> no-op every frame ----

    [Fact]
    public void NoTargetReceived_UpdateIsNoOp()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("Bomb", game.CivilianPlayer, HighUp);

        var before = bomb.Translation;
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        var after = bomb.Translation;

        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Z, after.Z, 3);
    }

    // ---- TC6: zero-length target is rejected; prior target/absence is preserved ----

    [Fact]
    public void ZeroLengthTarget_BeforeAnyValidTarget_LeavesTargetUnsetAndUpdateIsNoOp()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("Bomb", game.CivilianPlayer, HighUp);
        ModuleOf(bomb).SetTargetPosition(FixVector3.Zero);

        var before = bomb.Translation;
        game.Step();
        var after = bomb.Translation;

        // Rejected: m_targetReceived stays false, so update() keeps taking the "no target"
        // early-out - position never moves.
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
    }

    [Fact]
    public void ZeroLengthTarget_AfterAValidTarget_PreservesThePriorTarget()
    {
        var game = NewGame();
        var bombControl = game.SpawnObject("BombZeroScalar", game.CivilianPlayer, HighUp);
        var bombRejected = game.SpawnObject("BombZeroScalar", game.CivilianPlayer, HighUp);

        ModuleOf(bombControl).SetTargetPosition(Target(50, 60, 0));

        ModuleOf(bombRejected).SetTargetPosition(Target(50, 60, 0));
        ModuleOf(bombRejected).SetTargetPosition(FixVector3.Zero); // rejected: no effect

        // Two steps: the first only reaches the modules' arming tick (SetWakeFrame(None)'s
        // 1-frame minimum latency, shared by every sleepy update module - see the sibling
        // interpolation test above), the second is their first real Update().
        game.Step();
        game.Step();

        // Both bombs still jump to the original (50, 60) target: the zero-length call never
        // overwrote m_target on bombRejected.
        Assert.Equal(bombControl.Translation.X, bombRejected.Translation.X, 3);
        Assert.Equal(bombControl.Translation.Y, bombRejected.Translation.Y, 3);
        Assert.Equal(50.0f, bombRejected.Translation.X, 2);
        Assert.Equal(60.0f, bombRejected.Translation.Y, 2);
    }

    // ---- shadow-copy / save-load fitness (api-freeze-v1 §6 item 4) ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var bomb = game.SpawnObject("Bomb", game.CivilianPlayer, HighUp);
        ModuleOf(bomb).SetTargetPosition(Target(1000, 0, 0));
        game.Step();
        game.Step();
        var live = ModuleOf(bomb);

        var shadowHost = game.SpawnObject("Bomb", game.CivilianPlayer, new Vector3(300, 0, 500));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static float[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var bomb = game.SpawnObject("Bomb", game.CivilianPlayer, HighUp);
        var module = ModuleOf(bomb);
        module.SetTargetPosition(Target(1000, 500, 0));

        var trajectory = new float[10];
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
            trajectory[i] = bomb.Translation.X;
        }

        return trajectory;
    }
}

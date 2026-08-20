// Mocked-game contract tests for the BridgeScaffoldBehavior port (api-freeze-v1 §6 fitness
// item 4 shape): one test per behavior branch from the R12 task packet - Rise/BuildAcross/
// TearDownAcross/Sink transitions, the reverseMotion() state machine, lateral/vertical speed
// independence - plus the shadow-copy base test and a run-twice bit-determinism check.
//
// The module carries no INI fields (matching the GPL ModuleData, which is empty - createPos/
// riseToPos/buildPos/speeds are all set programmatically through the public API, exactly as
// the retail bridge-repair caller would), so every test spawns the bare behavior and drives
// it entirely through SetPositions/SetMotion/SetLateralSpeed/SetVerticalSpeed.

using System.Linq;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class BridgeScaffoldBehaviorContractTests
{
    private const string Definitions = @"
Object Scaffold
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = BridgeScaffoldBehavior ModuleTag_Scaffold
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB61D) =>
        new HeadlessSimGame(SageGame.Bfme2, seed);

    private static HeadlessSimGame NewLoadedGame(uint seed = 0xB61D)
    {
        var game = NewGame(seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static BridgeScaffoldBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<BridgeScaffoldBehavior>().Single();

    private static FixVector3 Fx(int x, int y, int z) =>
        new FixVector3(new Fix64(x), new Fix64(y), new Fix64(z));

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    /// <summary>Steps until <paramref name="condition"/> is true or the frame budget runs out.</summary>
    private static void StepUntil(HeadlessSimGame game, System.Func<bool> condition, int maxFrames)
    {
        for (var i = 0; i < maxFrames && !condition(); i++)
        {
            game.Step();
        }
    }

    private static bool CloseTo(Vector3 a, Vector3 b, float tolerance = 0.5f) =>
        (a - b).Length() <= tolerance;

    // ------------------------------------------------------------------------------------------
    // 1. Rise motion transitions: createPos -> riseToPos at verticalSpeed, then RISE ->
    //    BUILD_ACROSS.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void Rise_MovesFromCreatePosToRiseToPos_ThenAutoTransitionsToBuildAcross()
    {
        var game = NewLoadedGame();
        var createPos = new Vector3(1000, 1000, 0);
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, createPos);
        var module = ModuleOf(scaffold);

        module.SetPositions(Fx(1000, 1000, 0), Fx(1000, 1000, 100), Fx(1200, 1000, 100));
        module.SetVerticalSpeed(Fix64.FromDecimalLiteral("10"));
        module.SetMotion(ScaffoldTargetMotion.Rise);

        Assert.Equal(ScaffoldTargetMotion.Rise, module.CurrentMotion);

        // Mid-flight: still rising, Z climbing between the two endpoints, X/Y untouched.
        var sawMidFlight = false;
        for (var i = 0; i < 60 && module.CurrentMotion == ScaffoldTargetMotion.Rise; i++)
        {
            var z = scaffold.Transform.Translation.Z;
            if (z > 1f && z < 99f)
            {
                sawMidFlight = true;
            }
            Assert.True(System.MathF.Abs(scaffold.Transform.Translation.X - 1000f) < 1f,
                $"scaffold X drifted to {scaffold.Transform.Translation.X} during a pure vertical rise");
            Assert.True(System.MathF.Abs(scaffold.Transform.Translation.Y - 1000f) < 1f,
                $"scaffold Y drifted to {scaffold.Transform.Translation.Y} during a pure vertical rise");
            game.Step();
        }
        Assert.True(sawMidFlight, "scaffold never observed mid-rise (Z between endpoints)");

        Assert.Equal(ScaffoldTargetMotion.BuildAcross, module.CurrentMotion);
        Assert.True(CloseTo(scaffold.Transform.Translation, new Vector3(1000, 1000, 100)),
            $"scaffold at {scaffold.Transform.Translation} did not arrive at riseToPos");
    }

    // ------------------------------------------------------------------------------------------
    // 2. Lateral build: riseToPos -> buildPos at lateralSpeed, then BUILD_ACROSS -> STILL.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void BuildAcross_MovesFromRiseToPosToBuildPos_ThenAutoTransitionsToStill()
    {
        var game = NewLoadedGame();
        var riseToPos = new Vector3(2000, 1000, 100);
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, riseToPos);
        var module = ModuleOf(scaffold);

        module.SetPositions(Fx(2000, 1000, 0), Fx(2000, 1000, 100), Fx(2200, 1000, 100));
        module.SetLateralSpeed(Fix64.FromDecimalLiteral("20"));
        module.SetMotion(ScaffoldTargetMotion.BuildAcross);

        StepUntil(game, () => module.CurrentMotion != ScaffoldTargetMotion.BuildAcross, maxFrames: 60);

        Assert.Equal(ScaffoldTargetMotion.Still, module.CurrentMotion);
        Assert.True(CloseTo(scaffold.Transform.Translation, new Vector3(2200, 1000, 100)),
            $"scaffold at {scaffold.Transform.Translation} did not arrive at buildPos");

        // STILL never moves again, regardless of further ticks.
        var stillPos = scaffold.Transform.Translation;
        Step(game, 5);
        Assert.Equal(stillPos, scaffold.Transform.Translation);
    }

    // ------------------------------------------------------------------------------------------
    // 3. Tear-down reversal: buildPos -> riseToPos at lateralSpeed, then TEAR_DOWN_ACROSS ->
    //    SINK.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void TearDownAcross_MovesFromBuildPosToRiseToPos_ThenAutoTransitionsToSink()
    {
        var game = NewLoadedGame();
        var buildPos = new Vector3(3200, 1000, 100);
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, buildPos);
        var module = ModuleOf(scaffold);

        module.SetPositions(Fx(3000, 1000, 0), Fx(3000, 1000, 100), Fx(3200, 1000, 100));
        module.SetLateralSpeed(Fix64.FromDecimalLiteral("20"));
        module.SetMotion(ScaffoldTargetMotion.TearDownAcross);

        StepUntil(game, () => module.CurrentMotion != ScaffoldTargetMotion.TearDownAcross, maxFrames: 60);

        Assert.Equal(ScaffoldTargetMotion.Sink, module.CurrentMotion);
        Assert.True(CloseTo(scaffold.Transform.Translation, new Vector3(3000, 1000, 100)),
            $"scaffold at {scaffold.Transform.Translation} did not arrive at riseToPos");
        Assert.False(scaffold.IsDestroyed);
    }

    // ------------------------------------------------------------------------------------------
    // 4. Sink and auto-destroy: riseToPos -> createPos at verticalSpeed; arrival destroys the
    //    object.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void Sink_MovesFromRiseToPosToCreatePos_ThenSelfDestroys()
    {
        var game = NewLoadedGame();
        var riseToPos = new Vector3(4000, 1000, 100);
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, riseToPos);
        var module = ModuleOf(scaffold);

        module.SetPositions(Fx(4000, 1000, 0), Fx(4000, 1000, 100), Fx(4200, 1000, 100));
        module.SetVerticalSpeed(Fix64.FromDecimalLiteral("10"));
        module.SetMotion(ScaffoldTargetMotion.Sink);

        // Not destroyed on the very first tick - the descent takes multiple frames at this speed.
        game.Step();
        Assert.False(scaffold.IsDestroyed);

        StepUntil(game, () => scaffold.IsDestroyed, maxFrames: 60);

        Assert.True(scaffold.IsDestroyed, "scaffold never self-destroyed after sinking to createPos");
    }

    // ------------------------------------------------------------------------------------------
    // 5. Motion reversal state machine: reverseMotion() inverts the current state.
    // ------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(ScaffoldTargetMotion.Still, ScaffoldTargetMotion.TearDownAcross)]
    [InlineData(ScaffoldTargetMotion.Rise, ScaffoldTargetMotion.Sink)]
    [InlineData(ScaffoldTargetMotion.BuildAcross, ScaffoldTargetMotion.TearDownAcross)]
    [InlineData(ScaffoldTargetMotion.TearDownAcross, ScaffoldTargetMotion.BuildAcross)]
    [InlineData(ScaffoldTargetMotion.Sink, ScaffoldTargetMotion.Rise)]
    public void ReverseMotion_InvertsEachState(ScaffoldTargetMotion from, ScaffoldTargetMotion expected)
    {
        var game = NewLoadedGame();
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, new Vector3(5000, 1000, 50));
        var module = ModuleOf(scaffold);
        module.SetPositions(Fx(5000, 1000, 0), Fx(5000, 1000, 100), Fx(5200, 1000, 100));

        // STILL is the module's own initial state; every other starting state is reached via
        // SetMotion first (mirrors the GPL: motion is always set through setMotion before it
        // is ever reversed).
        if (from != ScaffoldTargetMotion.Still)
        {
            module.SetMotion(from);
        }

        module.ReverseMotion();

        Assert.Equal(expected, module.CurrentMotion);
    }

    [Fact]
    public void ReverseMotion_MidBuildAcross_RetargetsTowardRiseToPos_PositionInvariantHolds()
    {
        var game = NewLoadedGame();
        var riseToPos = new Vector3(6000, 1000, 100);
        var buildPos = new Vector3(6200, 1000, 100);
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, riseToPos);
        var module = ModuleOf(scaffold);

        module.SetPositions(Fx(6000, 1000, 0), Fx(6000, 1000, 100), Fx(6200, 1000, 100));
        module.SetLateralSpeed(Fix64.FromDecimalLiteral("20"));
        module.SetMotion(ScaffoldTargetMotion.BuildAcross);

        // Let it travel partway toward buildPos.
        Step(game, 3);
        Assert.Equal(ScaffoldTargetMotion.BuildAcross, module.CurrentMotion);
        var midX = scaffold.Transform.Translation.X;
        Assert.True(midX > riseToPos.X, "scaffold never moved toward buildPos before the reversal");

        module.ReverseMotion();
        Assert.Equal(ScaffoldTargetMotion.TearDownAcross, module.CurrentMotion);

        StepUntil(game, () => module.CurrentMotion != ScaffoldTargetMotion.TearDownAcross, maxFrames: 60);

        // Reversed mid-flight, the scaffold heads back to riseToPos (not on to buildPos) and
        // ends up in SINK, exactly the same destination TearDownAcross always has.
        Assert.Equal(ScaffoldTargetMotion.Sink, module.CurrentMotion);
        Assert.True(CloseTo(scaffold.Transform.Translation, riseToPos),
            $"scaffold at {scaffold.Transform.Translation} did not return to riseToPos after reversal");
    }

    // ------------------------------------------------------------------------------------------
    // 6. Speed independence: lateral/vertical speeds set independently; RISE/SINK use
    //    verticalSpeed, BUILD_ACROSS/TEAR_DOWN use lateralSpeed; no overshoot past the target.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void RiseAndBuildAcross_UseTheirOwnIndependentSpeeds_AndNeverOvershootTheTarget()
    {
        var game = NewLoadedGame();
        var createPos = new Vector3(7000, 1000, 0);
        var scaffold = game.SpawnObject("Scaffold", game.CivilianPlayer, createPos);
        var module = ModuleOf(scaffold);

        module.SetPositions(Fx(7000, 1000, 0), Fx(7000, 1000, 100), Fx(7300, 1000, 100));
        module.SetVerticalSpeed(Fix64.FromDecimalLiteral("5"));   // slow rise
        module.SetLateralSpeed(Fix64.FromDecimalLiteral("50"));  // fast lateral
        module.SetMotion(ScaffoldTargetMotion.Rise);

        var riseFrames = 0;
        while (module.CurrentMotion == ScaffoldTargetMotion.Rise && riseFrames < 200)
        {
            game.Step();
            riseFrames++;
        }
        Assert.Equal(ScaffoldTargetMotion.BuildAcross, module.CurrentMotion);
        // Never overshot past Z:100 (verticalSpeed 5, distance 100 - should take many frames).
        Assert.True(riseFrames > 10, $"rise finished suspiciously fast ({riseFrames} frames) for verticalSpeed 5 over 100 units");
        Assert.True(CloseTo(scaffold.Transform.Translation, new Vector3(7000, 1000, 100)),
            $"rise overshot: ended at {scaffold.Transform.Translation}");

        var lateralFrames = 0;
        while (module.CurrentMotion == ScaffoldTargetMotion.BuildAcross && lateralFrames < 200)
        {
            game.Step();
            lateralFrames++;
        }
        Assert.Equal(ScaffoldTargetMotion.Still, module.CurrentMotion);
        Assert.True(CloseTo(scaffold.Transform.Translation, new Vector3(7300, 1000, 100)),
            $"lateral build overshot: ended at {scaffold.Transform.Translation}");

        // The fast lateral leg (300 units @ speed 50) must finish in noticeably fewer frames
        // than the slow vertical leg (100 units @ speed 5), demonstrating the two speeds are
        // genuinely independent rather than one governing both motions.
        Assert.True(lateralFrames < riseFrames,
            $"lateral leg ({lateralFrames} frames) was not faster than the vertical leg ({riseFrames} frames)");
    }

    // ------------------------------------------------------------------------------------------
    // Xfer: shadow-copy CRC equality mid-behavior + run-twice determinism.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewLoadedGame();
        var host = game.SpawnObject("Scaffold", game.CivilianPlayer, new Vector3(8000, 1000, 0));
        var live = ModuleOf(host);
        live.SetPositions(Fx(8000, 1000, 0), Fx(8000, 1000, 100), Fx(8200, 1000, 100));
        live.SetVerticalSpeed(Fix64.FromDecimalLiteral("7"));
        live.SetMotion(ScaffoldTargetMotion.Rise);
        Step(game, 3);

        var shadowHost = game.SpawnObject("Scaffold", game.CivilianPlayer, new Vector3(8500, 1000, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void RunTwice_ScaffoldMotion_IsBitDeterministic()
    {
        var gameA = NewLoadedGame(seed: 0xCAFE);
        var gameB = NewLoadedGame(seed: 0xCAFE);
        var scaffoldA = gameA.SpawnObject("Scaffold", gameA.CivilianPlayer, new Vector3(9000, 1000, 0));
        var scaffoldB = gameB.SpawnObject("Scaffold", gameB.CivilianPlayer, new Vector3(9000, 1000, 0));
        var moduleA = ModuleOf(scaffoldA);
        var moduleB = ModuleOf(scaffoldB);

        foreach (var module in new[] { moduleA, moduleB })
        {
            module.SetPositions(Fx(9000, 1000, 0), Fx(9000, 1000, 100), Fx(9200, 1000, 100));
            module.SetVerticalSpeed(Fix64.FromDecimalLiteral("9"));
            module.SetMotion(ScaffoldTargetMotion.Rise);
        }

        Step(gameA, 10);
        Step(gameB, 10);

        Assert.Equal(moduleA.CurrentMotion, moduleB.CurrentMotion);
        Assert.Equal(scaffoldA.Transform.Translation, scaffoldB.Transform.Translation);
    }
}

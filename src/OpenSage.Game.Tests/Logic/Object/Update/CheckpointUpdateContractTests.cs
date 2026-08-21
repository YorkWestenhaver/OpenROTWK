// Mocked-game unit tests for the CheckpointUpdate port (api-freeze-v1 §6 fitness item 4): one
// test per behavior branch from the R12 task packet, [create -> tick -> observable effect],
// plus the mid-behavior save/load round-trip and the shadow-copy base test - the same shape as
// EnemyNearUpdateContractTests, its direct analog.
//
// The observables are the Door1Opening/Door1Closing model-condition flags (client-side
// presentation output) and the geometry minor radius the module animates every frame.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class CheckpointUpdateContractTests
{
    // Gate: 100-unit vision, scans every 1000 ms -> 5 frames at 5 Hz (F6). Geometry default
    // (Sphere/Cylinder) minor radius of 10 gives plenty of room for the 0.333/frame step.
    private const string Definitions = @"
Object Gate
  KindOf = STRUCTURE
  VisionRange = 100
  GeometryMinorRadius = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CheckpointUpdate ModuleTag_Checkpoint
    ScanDelayTime = 1000
  End
End

Object FastGate
  KindOf = STRUCTURE
  VisionRange = 100
  GeometryMinorRadius = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CheckpointUpdate ModuleTag_Checkpoint
    ScanDelayTime = 200
  End
End

Object Grunt
  KindOf = INFANTRY
  VisionRange = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC4EC) // "checkpoint"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static CheckpointUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CheckpointUpdate>().Single();

    private static bool Opening(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening);

    private static bool Closing(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.Door1Closing);

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    private static void MakeAllies(Player a, Player b)
    {
        a.AddAlly(b);
        b.AddAlly(a);
    }

    /// <summary>
    /// Steps enough frames that a first scan is guaranteed for any ctor stagger in [0, 5]:
    /// the countdown hits zero at frame (stagger + 1) &lt;= 6, plus a margin.
    /// </summary>
    private static void StepPastFirstScan(HeadlessSimGame game)
    {
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }
    }

    [Fact]
    public void NoAllyNoEnemy_GateStaysClosed()
    {
        var game = NewGame();
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);

        StepPastFirstScan(game);

        Assert.False(Opening(gate));
        Assert.False(Closing(gate));
    }

    [Fact]
    public void AllyNearNoEnemy_OpensGate_ShrinksRadius()
    {
        var game = NewGame();
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0)); // same owner: ally

        var maxRadius = gate.Geometry.MinorRadius;

        StepPastFirstScan(game);

        Assert.True(Opening(gate));
        Assert.False(Closing(gate));
        Assert.True(gate.Geometry.MinorRadius < maxRadius);
    }

    [Fact]
    public void EnemyNear_ClosesGate_ReplacesOpeningWithClosing()
    {
        var game = NewGame();
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var maxRadius = gate.Geometry.MinorRadius;
        StepPastFirstScan(game);

        // Ally AND enemy both present -> gate is NOT open (enemy near suppresses opening).
        Assert.False(Opening(gate));
        Assert.True(Closing(gate));
        Assert.True(gate.Geometry.MinorRadius >= maxRadius - 0.001f);

        _ = ally; // keep the ally alive/in-scope for the scenario's shape
    }

    [Fact]
    public void OpenThenEnemyArrives_ReversesRadiusAnimationDirection()
    {
        var game = NewGame();
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0)); // ally, always in range
        var maxRadius = gate.Geometry.MinorRadius;

        // Let the gate open and shrink for a while with no enemy present.
        StepPastFirstScan(game);
        Assert.True(Opening(gate));
        var shrunkRadius = gate.Geometry.MinorRadius;
        Assert.True(shrunkRadius < maxRadius);
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        var moreShrunkRadius = gate.Geometry.MinorRadius;
        Assert.True(moreShrunkRadius < shrunkRadius);

        // Now an enemy shows up: the next scan window flips the gate closed and the radius
        // must start growing back, not keep shrinking.
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(-10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        // The scan is throttled to once per ScanDelayTime (5 frames): in the worst case the
        // countdown has just re-armed the instant the enemy appears, so detection (and the
        // resulting reversal to growth) can be delayed by up to 5 more frames of continued
        // shrinking. Step past that worst case (5 frames) plus enough growth frames afterward
        // (10) that the growth unambiguously outpaces whatever extra shrinking happened while
        // waiting for the next scan window.
        for (var i = 0; i < 15; i++)
        {
            game.Step();
        }

        Assert.True(Closing(gate));
        Assert.False(Opening(gate));
        var growingRadius = gate.Geometry.MinorRadius;
        Assert.True(growingRadius > moreShrunkRadius);

        _ = enemy;
    }

    [Fact]
    public void ScanDelayTime_ThrottlesScanToConfiguredInterval()
    {
        // A 1000ms (5-frame) gate must NOT react to an ally that appears and then leaves
        // within a single scan window, while a 200ms (1-frame) gate reacts every frame.
        var game = NewGame();
        var slowGate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);
        var fastGate = game.SpawnObject("FastGate", game.CivilianPlayer, new Vector3(200, 0, 0));

        // Bias could stagger the very first scan up to 5 (Gate) / 1 (FastGate) frames; park
        // both gates past their first scan with nothing around, so both read closed/no-ally.
        StepPastFirstScan(game);
        Assert.False(Opening(slowGate));
        Assert.False(Opening(fastGate));

        // Spawn an ally next to both, then immediately remove it (destroy) before the slow
        // gate's next scan window but after at least one frame - the fast gate (1-frame
        // window) must catch the blip; the slow gate must not react until ITS window comes
        // back around, and by then the ally is gone.
        var allyNearSlow = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        var allyNearFast = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(210, 0, 0));
        game.Step();
        Assert.True(Opening(fastGate)); // 1-frame window: reacted immediately

        allyNearSlow.Kill();
        allyNearFast.Kill();
        game.Step(); // reap

        for (var i = 0; i < 6; i++) // run out the slow gate's original scan window
        {
            game.Step();
        }

        // The slow gate's scan window landed after the ally was gone: it never saw it.
        Assert.False(Opening(slowGate));
    }

    [Fact]
    public void MultipleAlliesAndEnemies_AnyPresenceFlagsEvaluatedCorrectly()
    {
        var game = NewGame();
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);

        // Two allies, one far enemy: far enemy is out of vision range, so the gate should
        // still open (any-ally true, any-in-range-enemy false).
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(-10, 0, 0));
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(500, 0, 0)); // > 100
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.True(Opening(gate));

        // Now bring a second, close enemy into range alongside the far one: any-enemy flips
        // true and the gate must close even though allies are still present.
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(20, 0, 0));
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.True(Closing(gate));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepPastFirstScan(game);
        var live = ModuleOf(gate);

        var shadowHost = game.SpawnObject("Gate", game.CivilianPlayer, new Vector3(300, 0, 0));
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

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var gate = game.SpawnObject("Gate", game.CivilianPlayer, Vector3.Zero);
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(gate);

        var trajectory = new int[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            // Move the ally out of and back into range mid-run so the flags actually toggle.
            ally.UpdateTransform(i == 6 ? new Vector3(500, 0, 0) : new Vector3(10, 0, 0));
            ally.UpdateColliders();

            game.Step();
            trajectory[i] = Opening(gate) ? 1 : 0;
        }

        return trajectory;
    }
}

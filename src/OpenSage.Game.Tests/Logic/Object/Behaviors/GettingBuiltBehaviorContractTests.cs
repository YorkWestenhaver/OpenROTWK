// Mocked-game contract tests for the GettingBuiltBehavior port (R13): the self-tick (no-worker)
// construction cadence, the worker-spawn/build-target assignment, worker-death respawn delay, and
// the Rubble-restart pacing driven by RebuildTimeSeconds instead of Definition.BuildTime. See the
// R13 port spec (bfme2-workbench/research/modules-r13/specs/GettingBuiltBehaviorModuleData.md) for
// the behavioral derivation (§1.3) and findings F-GBB-1..4 this test plan pins.
//
// Sleepy-update convention: a freshly spawned module's NextCallFrame floors to "now" at creation,
// and its first Update() call happens on the Step() that advances CurrentFrame past that frame -
// in practice, the very first Step() after spawning. Every frame count below is expressed against
// this convention.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class GettingBuiltBehaviorContractTests
{
    // 5 Hz logic rate -> SpawnTimer seconds * 5 = frames. BuildTime deliberately set far larger
    // than the 10 frames RebuildTimeSeconds needs (spec test 4), so a port that wrongly fell back
    // to AdvanceConstruction()'s Definition.BuildTime denominator during a rebuild would not
    // vacuously pass.
    private const string Definitions = @"
Object SelfBuiltWall
  KindOf = STRUCTURE IMMOBILE
  BuildTime = 4.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_GettingBuilt
    UseSpawnTimerWithoutWorker = Yes
    SpawnTimer = 1.0
    RebuildWhenDead = Yes
    RebuildTimeSeconds = 2.0
  End
End

Object SelfBuiltWallNoRebuild
  KindOf = STRUCTURE IMMOBILE
  BuildTime = 4.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_GettingBuilt
    UseSpawnTimerWithoutWorker = Yes
    SpawnTimer = 1.0
  End
End

Object NoDriverWall
  KindOf = STRUCTURE IMMOBILE
  BuildTime = 4.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_GettingBuilt
  End
End

Object WorkerBuiltTower
  KindOf = STRUCTURE IMMOBILE
  BuildTime = 4.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GettingBuiltBehavior ModuleTag_GettingBuilt
    WorkerName = TestWorker
    SpawnTimer = 1.0
  End
End

Object TestWorker
  KindOf = DOZER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = WorkerAIUpdate ModuleTag_AI
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x6BB);
        game.LoadIniText(Definitions);
        return game;
    }

    /// <summary>
    /// Mirrors the confirmed initial-build caller, CastleUnpackStamper.StartSelfBuild (spec §1.2):
    /// PrepareConstruction's terrain-flatten half is skipped in the headless host (no Terrain), the
    /// same guard StartSelfBuild itself applies - only the model-condition/BuildProgress half runs.
    /// </summary>
    private static void StartSelfBuild(GameObject structure)
    {
        structure.SetIsBeingConstructed();
        structure.BuildProgress = 0.0f;
    }

    [Fact]
    public void SelfBuild_AdvancesOnSpawnTimerCadence_NoWorkerSpawned()
    {
        var game = NewGame();
        var wall = game.SpawnObject("SelfBuiltWall", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(wall);

        Assert.Equal(0.0f, wall.BuildProgress);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        // BuildTime = 4.0s = 20 frames; exactly one 5-frame SpawnTimer cycle elapsed -> exactly
        // one AdvanceConstruction() call (1 / 20 progress), not a partial/continuous advance.
        Assert.Equal(1.0f / 20.0f, wall.BuildProgress, 5);
        Assert.Equal(0, game.GameLogic.Objects.Count(o => o.Definition.Name == "TestWorker"));
    }

    [Fact]
    public void WorkerBuild_SpawnsWorkerAndAssignsSelfAsBuildTarget()
    {
        var game = NewGame();
        var tower = game.SpawnObject("WorkerBuiltTower", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(tower);

        game.Step();

        var worker = Assert.Single(game.GameLogic.Objects.Where(o => o.Definition.Name == "TestWorker"));
        Assert.False(worker.IsSelectable);
        Assert.Same(tower, ((WorkerAIUpdate)worker.AIUpdate).BuildTarget);
    }

    [Fact]
    public void RubbleRestart_OnBodyDamageStateChangeToRubble_ResetsBuildProgressAndFlags()
    {
        var game = NewGame();
        var wall = game.SpawnObject("SelfBuiltWall", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(wall);

        // Step until the initial (self-tick) build finishes.
        for (var i = 0; i < 500 && wall.BuildProgress < 1.0f; i++)
        {
            game.Step();
        }

        Assert.Equal(1.0f, wall.BuildProgress);
        Assert.False(wall.IsBeingConstructed());

        // Drive to Rubble: full health loss, not a Kill - RUBBLE structures never die (spec §1.2).
        wall.AttemptDamage(new DamageInfoInput(null) { DamageType = DamageType.Unresistable, Amount = 100f, Kill = false });

        Assert.True(wall.IsBeingConstructed());
        Assert.Equal(0.0f, wall.BuildProgress);
        Assert.False(wall.IsDestroyed);
    }

    [Fact]
    public void RubbleRestart_CompletesAtRebuildTimeSecondsNotDefinitionBuildTime()
    {
        var game = NewGame();
        var wall = game.SpawnObject("SelfBuiltWall", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(wall);

        for (var i = 0; i < 500 && wall.BuildProgress < 1.0f; i++)
        {
            game.Step();
        }

        wall.AttemptDamage(new DamageInfoInput(null) { DamageType = DamageType.Unresistable, Amount = 100f, Kill = false });
        Assert.True(wall.IsBeingConstructed());

        // RebuildTimeSeconds = 2.0 -> 10 frames at 5 Hz, independent of the (deliberately much
        // larger) BuildTime = 4.0s (20 frames) Definition.BuildTime denominator.
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.True(wall.BuildProgress >= 1.0f);
        Assert.False(wall.IsBeingConstructed());
    }

    [Fact]
    public void RubbleRestart_NoOp_WhenRebuildWhenDeadFalse()
    {
        var game = NewGame();
        var wall = game.SpawnObject("SelfBuiltWallNoRebuild", game.CivilianPlayer, Vector3.Zero);
        // Placed already-complete (no StartSelfBuild call): matches the "map-placed" case (spec §1.2).
        Assert.False(wall.IsBeingConstructed());

        wall.AttemptDamage(new DamageInfoInput(null) { DamageType = DamageType.Unresistable, Amount = 100f, Kill = false });
        Assert.False(wall.IsBeingConstructed());

        for (var i = 0; i < 20; i++)
        {
            game.Step();
            Assert.False(wall.IsBeingConstructed());
        }
    }

    [Fact]
    public void WorkerDeath_RespawnsAfterSpawnTimerDelay()
    {
        var game = NewGame();
        var tower = game.SpawnObject("WorkerBuiltTower", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(tower);

        game.Step();
        var firstWorker = Assert.Single(game.GameLogic.Objects.Where(o => o.Definition.Name == "TestWorker"));

        firstWorker.Kill();

        for (var i = 0; i < 4; i++)
        {
            game.Step();
            Assert.Empty(game.GameLogic.Objects.Where(o => o.Definition.Name == "TestWorker" && !o.IsEffectivelyDead));
        }

        game.Step();

        var replacement = Assert.Single(game.GameLogic.Objects.Where(o => o.Definition.Name == "TestWorker" && !o.IsEffectivelyDead));
        Assert.NotEqual(firstWorker.Id, replacement.Id);
        Assert.Same(tower, ((WorkerAIUpdate)replacement.AIUpdate).BuildTarget);
    }

    [Fact]
    public void NoDriverCombination_NeverAdvances()
    {
        var game = NewGame();
        var wall = game.SpawnObject("NoDriverWall", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(wall);

        for (var i = 0; i < 50; i++)
        {
            game.Step();
        }

        Assert.Equal(0.0f, wall.BuildProgress);
        Assert.True(wall.IsBeingConstructed());
    }
}

// Mocked-game contract tests for the GettingBuiltBehavior port (R13): the self-tick (no-worker)
// construction cadence, the worker-spawn/build-target assignment, worker-death respawn delay, and
// the Rubble-restart pacing driven by RebuildTimeSeconds instead of Definition.BuildTime. See the
// R13 port spec (bfme2-workbench/research/modules-r13/specs/GettingBuiltBehaviorModuleData.md) for
// the behavioral derivation (§1.3) and findings F-GBB-1..4 this test plan pins.
//
// Sleepy-update convention: GameLogic.CreateObject floors a new update module's NextCallFrame to
// frame 1 for an object created at frame 0 (a zero initial wake frame is illegal), while
// GameLogic.Update runs the modules due on the *pre-increment* frame counter. So a module created
// before any Step() is skipped by the first Step() (now == 0 < NextCallFrame == 1) and takes its
// first Update() on the SECOND Step() - see StepToFirstModuleTick. Every frame count below is
// expressed against this convention.

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

    /// <summary>
    /// Steps to (and through) the first frame on which a module spawned before any Step() actually
    /// ticks: the second Step(), per the sleepy-update convention noted at the top of this file.
    /// Every "the module acts on its first Update()" assertion goes through this rather than a bare
    /// Step(), so an off-by-one in the host's spawn-frame flooring cannot be mistaken for the
    /// module failing to act.
    /// </summary>
    private static void StepToFirstModuleTick(HeadlessSimGame game)
    {
        game.Step();
        game.Step();
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

        // The SpawnTimer countdown is pre-elapsed at construction start, so the first self-tick
        // AdvanceConstruction() lands on the module's first Update() (the 2nd Step) and the next
        // one is 5 frames later - beyond this loop. BuildTime = 4.0s = 20 frames, so exactly one
        // advance is 1/20 progress, not a partial/continuous advance.
        Assert.Equal(1.0f / 20.0f, wall.BuildProgress, 5);
        Assert.Equal(0, game.GameLogic.Objects.Count(o => o.Definition.Name == "TestWorker"));
    }

    /// <summary>
    /// Pins the cadence *between* self-ticks, not just the first one: the second
    /// AdvanceConstruction() lands exactly one SpawnTimer interval (5 frames at 5 Hz) after the
    /// first, so progress is flat in between. Together with the case above this fixes both ends of
    /// the interval, which a port that seeded the countdown with a full interval (first advance one
    /// interval late, cadence otherwise identical) would otherwise still satisfy.
    /// </summary>
    [Fact]
    public void SelfBuild_SecondAdvanceLandsOneSpawnTimerIntervalLater()
    {
        var game = NewGame();
        var wall = game.SpawnObject("SelfBuiltWall", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(wall);

        StepToFirstModuleTick(game);
        Assert.Equal(1.0f / 20.0f, wall.BuildProgress, 5);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
            Assert.Equal(1.0f / 20.0f, wall.BuildProgress, 5); // flat across the interval
        }

        game.Step(); // the 5th frame of the interval: the countdown reaches zero and fires

        Assert.Equal(2.0f / 20.0f, wall.BuildProgress, 5);
    }

    [Fact]
    public void WorkerBuild_SpawnsWorkerAndAssignsSelfAsBuildTarget()
    {
        var game = NewGame();
        var tower = game.SpawnObject("WorkerBuiltTower", game.CivilianPlayer, Vector3.Zero);
        StartSelfBuild(tower);

        // The worker spawns on the module's very first Update(), not one SpawnTimer later: the
        // countdown is pre-elapsed at construction start and only paces respawns after that.
        StepToFirstModuleTick(game);

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

        StepToFirstModuleTick(game);
        var firstWorker = Assert.Single(game.GameLogic.Objects.Where(o => o.Definition.Name == "TestWorker"));

        firstWorker.Kill();

        // Same SpawnTimer field, different module state (§1.3): with a worker already assigned the
        // countdown is a respawn delay, so the death is noticed on the next Update() and the
        // replacement lands 5 frames (SpawnTimer = 1.0s at 5 Hz) after that - not immediately.
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

// R15 FIX2-DOZER: guard tests for DozerAndWorkerState, the module behind the deterministic
// frame-~127 NullReferenceException in the R1 AotR AI-match gate (WorkerAIUpdate.Update ->
// DozerAndWorkerState.Update -> UpdateBuildTarget).
//
// Two separate defects are covered here:
//   1. The build-complete bark dereferenced Definition.VoiceTaskComplete unconditionally. No
//      BFME2/AotR worker declares VoiceTaskComplete, and retail only plays it for a locally
//      controlled dozer, so an AI worker finishing its first structure crashed.
//   2. TryGetBuildTarget/TryGetRepairTarget returned true for a still-valid ObjectId whose
//      object had already been destroyed, handing a null out-param to a [NotNullWhen(true)]
//      contract. Retail cancels the task and idles the dozer when the goal object is gone.
//
// Driven against real GameObjects on HeadlessSimGame, in the style of TurretAIUpdateContractTests.
// The state object is driven directly rather than through WorkerAIUpdate.SetBuildTarget, because
// that path routes through AIUpdate.SetTargetPoint -> GameEngine.Navigation, which the headless
// host does not provide; the crash under test is entirely inside DozerAndWorkerState.

using System.Numerics;
using System.Reflection;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update.AIUpdate;

public class DozerAndWorkerStateTests
{
    // Note: no VoiceTaskComplete, exactly like every AotR worker.
    private const string ObjectDefinitions = @"
Object DozerTestWorker
  KindOf = INFANTRY DOZER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = WorkerAIUpdate ModuleTag_AI
    RepairHealthPercentPerSecond = 20%
    BoredTime = 1000
    BoredRange = 120
  End
End

Object DozerTestStructure
  KindOf = STRUCTURE
  BuildTime = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 1);
        game.LoadIniText(ObjectDefinitions);
        return game;
    }

    private static DozerAndWorkerState StateOf(GameObject worker)
    {
        var ai = Assert.IsType<WorkerAIUpdate>(worker.AIUpdate);
        var field = typeof(WorkerAIUpdate).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<DozerAndWorkerState>(field!.GetValue(ai));
    }

    private static (HeadlessSimGame Game, GameObject Worker, GameObject Structure) NewScenario()
    {
        var game = NewGame();
        var worker = game.SpawnObject("DozerTestWorker", game.CivilianPlayer, Vector3.Zero);
        var structure = game.SpawnObject("DozerTestStructure", game.CivilianPlayer, new Vector3(10, 0, 0));
        return (game, worker, structure);
    }

    /// <summary>
    /// Puts the pair in the state the build-target branch advances from: the structure is already
    /// under active construction and the worker is flagged ActivelyConstructing, so every Update
    /// calls AdvanceConstruction.
    /// </summary>
    private static void ArmBuildCompletion(GameObject worker, GameObject structure)
    {
        structure.SetIsBeingConstructed();
        worker.ModelConditionFlags.Set(ModelConditionFlag.ActivelyConstructing, true);
    }

    /// <summary>
    /// Ticks the state until the structure finishes (BuildTime = 1s = one second of logic frames),
    /// with a generous bound so a hang shows up as a failed assert rather than a stuck test.
    /// </summary>
    private static void UpdateUntilBuilt(DozerAndWorkerState state, GameObject structure)
    {
        for (var i = 0; i < 600 && structure.BuildProgress < 1f; i++)
        {
            state.Update();
        }
    }

    [Fact]
    public void BuildCompletionOnAnAiOwnedWorkerWithNoVoiceTaskCompleteDoesNotThrow()
    {
        var (game, worker, structure) = NewScenario();

        // No local player at all: this worker is not locally controlled, which is the AI-match case.
        game.LocalPlayer = null;

        var state = StateOf(worker);
        state.SetBuildTarget(structure, game.GameLogic.CurrentFrame.Value);
        ArmBuildCompletion(worker, structure);

        UpdateUntilBuilt(state, structure);

        Assert.True(structure.BuildProgress >= 1f);
        Assert.Null(state.BuildTarget);
        Assert.False(worker.ModelConditionFlags.Get(ModelConditionFlag.ActivelyConstructing));
    }

    [Fact]
    public void BuildCompletionOnALocallyControlledWorkerWithNoVoiceTaskCompleteDoesNotThrow()
    {
        var (game, worker, structure) = NewScenario();

        // Locally controlled, so the bark path is entered - and must still survive a definition
        // that declares no VoiceTaskComplete (and a host with no audio system).
        game.LocalPlayer = game.CivilianPlayer;

        var state = StateOf(worker);
        state.SetBuildTarget(structure, game.GameLogic.CurrentFrame.Value);
        ArmBuildCompletion(worker, structure);

        UpdateUntilBuilt(state, structure);

        Assert.True(structure.BuildProgress >= 1f);
        Assert.Null(state.BuildTarget);
    }

    [Fact]
    public void ABuildTargetDestroyedMidApproachCancelsTheTaskInsteadOfThrowing()
    {
        var (game, worker, structure) = NewScenario();

        var state = StateOf(worker);
        state.SetBuildTarget(structure, game.GameLogic.CurrentFrame.Value);
        Assert.Same(structure, state.BuildTarget);

        worker.ModelConditionFlags.Set(ModelConditionFlag.ActivelyConstructing, true);
        worker.ModelConditionFlags.Set(ModelConditionFlag.Moving, true);

        game.GameLogic.DestroyObject(structure);
        game.GameLogic.DeleteDestroyed();

        state.Update();

        Assert.Null(state.BuildTarget);
        Assert.False(worker.ModelConditionFlags.Get(ModelConditionFlag.ActivelyConstructing));
        // aiIdle: the dozer stops driving at the object that no longer exists.
        Assert.False(worker.ModelConditionFlags.Get(ModelConditionFlag.Moving));
    }

    [Fact]
    public void ARepairTargetDestroyedMidApproachCancelsTheTaskInsteadOfThrowing()
    {
        var (game, worker, structure) = NewScenario();

        var state = StateOf(worker);
        state.SetRepairTarget(structure, game.GameLogic.CurrentFrame.Value);
        Assert.Same(structure, state.RepairTarget);

        game.GameLogic.DestroyObject(structure);
        game.GameLogic.DeleteDestroyed();

        state.Update();

        Assert.Null(state.RepairTarget);
        Assert.False(worker.ModelConditionFlags.Get(ModelConditionFlag.ActivelyConstructing));
    }
}

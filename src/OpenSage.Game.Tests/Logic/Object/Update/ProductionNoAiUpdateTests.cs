// R15 PROD-FIX: the main regression at 14f28317.
//
// ROOT CAUSE, recorded here because the packet's brief was to ratify-or-supersede a guard and a
// guard with no explanation is indistinguishable from papering over a bug:
//
//   NOTHING on this code path changed between 9bde4556 (3/3 demo maps green) and 14f28317 (red).
//   `git log 9bde4556..14f28317 -- '*ProductionUpdate*' '*AIUpdate*'` is EMPTY. The branch that
//   caused the regression is r15/S9-08 (AiProductionManager): it is the first thing in the
//   project's history to make the skirmish AI queue a unit, so ProduceAndMoveOut /
//   MoveProducedObjectOut executed on an Age of the Ring map for the first time ever and walked
//   into dereferences that had simply never been reached. The control run
//   (bfme2-workbench/tools/harness/out/ai-match-r15-hudwire-mainctl/) shows the sequence
//   verbatim: "[AI p3] prod f=155 queue producer=264 unit=RohanArcherNewHorde" -> "trained
//   ... f=158" -> NullReferenceException at f=282 on producer #264 RohanArcherRange.
//
// So the defects are LATENT, not new, and the fix is a correct port rather than a shim. EA GPL
// (GeneralsMD DefaultProductionExitUpdate::exitObjectViaDoor) fetches
// `AIUpdateInterface *ai = newObj->getAIUpdateInterface();` and then tests `if (ai && ...)` and
// `if (ai)` before every use: retail's answer to "a produced object has no AI" is to place it at
// the create point and give it no exit path. No crash, no assert. That is what is asserted here.
//
// Two distinct throws live on that one path, and the packet anchor has them the wrong way round:
// at 14f28317 line 429 is the AUDIO line (`Definition.SoundMoveStart.Value`), not AddTargetPoint
// (420/426). SoundMoveStart is an optional field that most AotR objects omit;
// OrderProcessor.cs:92 already wrote `SoundMoveStart?.Value` for exactly this reason and this
// call site was the sole outlier. The AIUpdate deref is the second one, reached whenever the
// exit does supply a natural rally point - which DefaultProductionExitUpdate always does, so the
// case below exercises both at once.
//
// The engine-side reason an AotR unit can lack an AIUpdate at all: RohanArcherNewHorde declares
// `Behavior = HordeAIUpdate ModuleTag_HordeAIUpdate`, but that block does not survive parsing,
// so GameObject's module walk never runs the `case AIUpdate aiUpdate: AIUpdate = aiUpdate;` arm.
// Recovering the dropped block is a separate content/parse job; the engine must not die over it.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ProductionNoAiUpdateTests
{
    private const string Definitions = @"
; The produced unit, in the shape that crashed: no AIUpdate block of any kind and no
; SoundMoveStart. This is what an AotR horde looks like to the engine once its HordeAIUpdate
; block has been dropped at parse time.
Object ProdFixUnitWithoutAi
  KindOf = INFANTRY SELECTABLE
  BuildTime = 0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

; Control: same object, but it does carry an AIUpdate, so the exit path must still do its work.
Object ProdFixUnitWithAi
  KindOf = INFANTRY SELECTABLE
  BuildTime = 0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

; DefaultProductionExitUpdate.GetNaturalRallyPoint ALWAYS returns a value, so the produced
; object's AIUpdate is dereferenced on every single production - which is why an AI that
; produces at all was enough to turn a latent null into a dead match.
Object ProdFixBarracks
  KindOf = STRUCTURE SELECTABLE IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = ProductionUpdate ModuleTag_Production
  End
  Behavior = DefaultProductionExitUpdate ModuleTag_Exit
    UnitCreatePoint = X:20.0 Y:0.0 Z:0.0
    NaturalRallyPoint = X:40.0 Y:0.0 Z:0.0
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xE47D);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject SpawnBarracksAndProduce(HeadlessSimGame game, string unitName)
    {
        var barracks = game.SpawnObject("ProdFixBarracks", game.CivilianPlayer, Vector3.Zero);

        var production = barracks.FindBehavior<ProductionUpdate>();
        Assert.NotNull(production);

        // Spawn() queues with a zero build time, so the very next ProductionUpdate.Update that
        // runs produces the unit and calls MoveProducedObjectOut - the frame the regression died
        // on. T+1: the object is registered on the frame it was created, and the sleepy update
        // list only reaches its modules on the following Step.
        production.Spawn(game.AssetStore.ObjectDefinitions.GetByName(unitName));

        game.Step();
        game.Step();

        return barracks;
    }

    private static GameObject SingleProducedUnit(HeadlessSimGame game, string unitName)
    {
        return Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == unitName);
    }

    [Fact]
    public void ProducingAUnitWithNoAiUpdate_DoesNotThrow()
    {
        var game = NewGame();

        // The regression itself: at 14f28317 this Step threw NullReferenceException out of
        // MoveProducedObjectOut and took the whole match down on the frame the AI's first unit
        // came out of the door.
        var barracks = SpawnBarracksAndProduce(game, "ProdFixUnitWithoutAi");

        Assert.Contains(barracks, game.GameLogic.Objects);
    }

    [Fact]
    public void ProducingAUnitWithNoAiUpdate_StillCreatesAndOwnsTheUnit()
    {
        var game = NewGame();
        SpawnBarracksAndProduce(game, "ProdFixUnitWithoutAi");

        // Retail's behaviour: the object exists, is owned, and is placed - it just never gets an
        // exit path, because there is no AI to walk one.
        var produced = SingleProducedUnit(game, "ProdFixUnitWithoutAi");
        Assert.Null(produced.AIUpdate);
        Assert.Same(game.CivilianPlayer, produced.Owner);
    }

    [Fact]
    public void ProducingAUnitWithNoAiUpdate_DrainsTheProductionQueue()
    {
        var game = NewGame();
        var barracks = SpawnBarracksAndProduce(game, "ProdFixUnitWithoutAi");

        // The throw used to escape mid-Update, so the job was never removed. Degrading means the
        // producer carries on: the queue empties and the building is free to build again.
        var production = barracks.FindBehavior<ProductionUpdate>();
        Assert.Empty(production.ProductionQueue);
        Assert.False(production.IsProducing);
    }

    [Fact]
    public void ProducingAUnitWithNoAiUpdate_KeepsSimulatingOnLaterFrames()
    {
        var game = NewGame();
        var barracks = SpawnBarracksAndProduce(game, "ProdFixUnitWithoutAi");

        // The guard has to hold past the producing frame, not just on it.
        game.Step();
        game.Step();
        game.Step();

        Assert.Contains(barracks, game.GameLogic.Objects);
        Assert.Contains(SingleProducedUnit(game, "ProdFixUnitWithoutAi"), game.GameLogic.Objects);
    }

    [Fact]
    public void ProducingAUnitWithAnAiUpdate_StillReceivesTheNaturalRallyPointAsATargetPoint()
    {
        var game = NewGame();
        SpawnBarracksAndProduce(game, "ProdFixUnitWithAi");

        // The control that keeps the guard honest: the fix must not have turned the exit path
        // into a no-op for objects that CAN move. NaturalRallyPoint X:40 is declared in object
        // space and run through GameObject.ToWorldspace; the barracks sits at the origin
        // unrotated, so the target point comes back out at x=40.
        var produced = SingleProducedUnit(game, "ProdFixUnitWithAi");
        Assert.NotNull(produced.AIUpdate);

        var targetPoint = Assert.Single(produced.AIUpdate.TargetPoints);
        Assert.Equal(40.0f, targetPoint.X, 3);
    }

    [Fact]
    public void ProducingAUnitWithNoAiUpdate_AddsNoTargetPointsAnywhere()
    {
        var game = NewGame();
        var barracks = SpawnBarracksAndProduce(game, "ProdFixUnitWithoutAi");

        // ...and the converse: skipping the AI work must not have leaked the produced unit's
        // rally points onto the producer instead.
        Assert.Null(barracks.AIUpdate);
        Assert.Null(SingleProducedUnit(game, "ProdFixUnitWithoutAi").AIUpdate);
    }
}

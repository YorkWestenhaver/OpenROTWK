// Contract tests for the SpawnUnitBehavior port (R13, modules-r13/specs/SpawnUnitBehaviorModuleData.md
// §4: one test per INI/observable branch, plus the persisted-gate proxy - see that file's own
// header for why a raw byte-level round trip / PortedModuleTestKit save-load is a harness N/A
// for this class, mirroring GrantUpgradeCreateContractTests's own precedent).
//
// Sleepy-update caveat does NOT apply: SpawnUnitBehavior is a CreateModule, never an
// UpdateModule - it has no Update() override and is never enqueued in GameLogic._sleepyUpdates.
// Its OnCreate() hook runs synchronously inside GameLogic.CreateObject/HeadlessSimGame.SpawnObject;
// no Step() call is needed before TryQueueUnit()'s effects are observable.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Create;

public class SpawnUnitBehaviorContractTests
{
    private const string Definitions = @"
Object OathbreakerHordeUnit
  KindOf = INFANTRY
  BuildCost = 250
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SpawnUnitCitadel
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = QueueProductionExitUpdate ModuleTag_Exit
    UnitCreatePoint   = X:0.0 Y:0.0 Z:0.0
    NaturalRallyPoint = X:0.0 Y:0.0 Z:0.0
    ExitDelay = 0
  End
  Behavior = ProductionUpdate ModuleTag_Production
  End
  Behavior = SpawnUnitBehavior ModuleTag_Spawn
    UnitName    = OathbreakerHordeUnit
    UnitCommand = Command_ConstructOathbreakerHorde
    SpawnOnce   = Yes
  End
End

Object SpawnUnitCitadelRepeatable
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = QueueProductionExitUpdate ModuleTag_Exit
    UnitCreatePoint   = X:0.0 Y:0.0 Z:0.0
    NaturalRallyPoint = X:0.0 Y:0.0 Z:0.0
    ExitDelay = 0
  End
  Behavior = ProductionUpdate ModuleTag_Production
  End
  Behavior = SpawnUnitBehavior ModuleTag_Spawn
    UnitName    = OathbreakerHordeUnit
    UnitCommand = Command_ConstructOathbreakerHorde
    SpawnOnce   = No
  End
End

Object SpawnUnitMissingRef
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = ProductionUpdate ModuleTag_Production
  End
  Behavior = SpawnUnitBehavior ModuleTag_Spawn
    UnitCommand = Command_NeverResolvable
    SpawnOnce   = Yes
  End
End

Object SpawnUnitNoProductionSibling
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = SpawnUnitBehavior ModuleTag_Spawn
    UnitName    = OathbreakerHordeUnit
    UnitCommand = Command_ConstructOathbreakerHorde
    SpawnOnce   = Yes
  End
End
";

    private static readonly Vector3 Origin = new(0, 0, 0);

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x5044574Eu);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpawnUnitBehavior ModuleOf(GameObject gameObject) =>
        gameObject.FindBehavior<SpawnUnitBehavior>();

    // ---- direct translation of the audit comment: "queue this unit up ... to build" ----

    [Fact]
    public void TryQueueUnit_QueuesTheNamedUnit_AndWithdrawsBuildCost()
    {
        var game = NewGame();
        var unit = game.SpawnObject("SpawnUnitCitadel", game.CivilianPlayer, Origin);
        var startingMoney = game.CivilianPlayer.BankAccount.Money;

        var result = ModuleOf(unit).TryQueueUnit();

        Assert.True(result);
        Assert.True(unit.ProductionUpdate.IsProducing);
        Assert.Contains(unit.ProductionUpdate.ProductionQueue, job => job.ObjectDefinition.Name == "OathbreakerHordeUnit");
        Assert.Equal(startingMoney - 250, game.CivilianPlayer.BankAccount.Money);
    }

    // ---- CanSpawnUnit is true before any use, with SpawnOnce set ----

    [Fact]
    public void CanSpawnUnit_TrueBeforeFirstUse_WithSpawnOnce()
    {
        var game = NewGame();
        var unit = game.SpawnObject("SpawnUnitCitadel", game.CivilianPlayer, Origin);

        Assert.True(ModuleOf(unit).CanSpawnUnit);
    }

    // ---- INI branch: SpawnOnce = Yes exhausts the slot after one use ----

    [Fact]
    public void SpawnOnce_ExhaustsAfterFirstUse()
    {
        var game = NewGame();
        var unit = game.SpawnObject("SpawnUnitCitadel", game.CivilianPlayer, Origin);
        var module = ModuleOf(unit);

        Assert.True(module.TryQueueUnit());
        Assert.False(module.CanSpawnUnit);

        var moneyAfterFirstQueue = game.CivilianPlayer.BankAccount.Money;
        var queueCountAfterFirst = unit.ProductionUpdate.ProductionQueue.Count;

        var second = module.TryQueueUnit();

        Assert.False(second);
        Assert.Equal(queueCountAfterFirst, unit.ProductionUpdate.ProductionQueue.Count);
        Assert.Equal(moneyAfterFirstQueue, game.CivilianPlayer.BankAccount.Money);
    }

    // ---- INI branch: SpawnOnce = No allows repeated queuing ----

    [Fact]
    public void SpawnOnce_No_AllowsRepeatedQueuing()
    {
        var game = NewGame();
        var unit = game.SpawnObject("SpawnUnitCitadelRepeatable", game.CivilianPlayer, Origin);
        var module = ModuleOf(unit);

        Assert.True(module.TryQueueUnit());
        Assert.True(module.TryQueueUnit());

        Assert.Equal(2, unit.ProductionUpdate.ProductionQueue.Count);
    }

    // ---- INI branch: a missing/absent UnitName is a silent no-op ----

    [Fact]
    public void MissingUnitName_IsASilentNoOp()
    {
        var game = NewGame();
        var unit = game.SpawnObject("SpawnUnitMissingRef", game.CivilianPlayer, Origin);
        var startingMoney = game.CivilianPlayer.BankAccount.Money;
        var module = ModuleOf(unit);

        Assert.False(module.CanSpawnUnit);

        var result = module.TryQueueUnit();

        Assert.False(result);
        Assert.False(unit.ProductionUpdate.IsProducing);
        Assert.Equal(startingMoney, game.CivilianPlayer.BankAccount.Money);
    }

    // ---- no ProductionUpdate sibling: returns false, does not throw, does not charge ----

    [Fact]
    public void NoProductionSibling_TryQueueUnit_ReturnsFalse_DoesNotThrow()
    {
        var game = NewGame();
        var unit = game.SpawnObject("SpawnUnitNoProductionSibling", game.CivilianPlayer, Origin);
        var startingMoney = game.CivilianPlayer.BankAccount.Money;
        var module = ModuleOf(unit);

        Assert.True(module.CanSpawnUnit);

        var result = module.TryQueueUnit();

        Assert.False(result);
        Assert.Equal(startingMoney, game.CivilianPlayer.BankAccount.Money);
    }

    // ---- persisted-gate continuation: the exhausted SpawnOnce gate reproduces object-for-object ----

    [Fact]
    public void SpawnOnce_ExhaustedGate_IsReproducibleAcrossInstances()
    {
        var game = NewGame();
        var unitA = game.SpawnObject("SpawnUnitCitadel", game.CivilianPlayer, Origin);
        var unitB = game.SpawnObject("SpawnUnitCitadel", game.CivilianPlayer, Origin);

        Assert.True(ModuleOf(unitA).TryQueueUnit());
        Assert.True(ModuleOf(unitB).TryQueueUnit());

        Assert.False(ModuleOf(unitA).CanSpawnUnit);
        Assert.False(ModuleOf(unitB).CanSpawnUnit);
    }
}

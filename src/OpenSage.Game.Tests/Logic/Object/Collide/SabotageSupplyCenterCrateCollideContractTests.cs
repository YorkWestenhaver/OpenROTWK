// Contract tests for the SabotageSupplyCenterCrateCollide port (R12): a saboteur crate that
// steals cash from an enemy supply center on collision. The GPL executeCrateBehavior gate is
// [not dead -> is a supply center -> is an enemy -> is still the saboteur's AI goal object]
// before the cash transfer runs, so each rejection branch gets its own test alongside the
// happy path and the "insufficient funds" partial-transfer branch.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageSupplyCenterCrateCollideContractTests
{
    private const string Definitions = @"
Object Saboteur
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSupplyCenterCrateCollide ModuleTag_Sabotage
    StealCashAmount = 500
  End
End

Object EnemySupplyCenter
  KindOf = STRUCTURE FS_SUPPLY_CENTER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object NonSupplyCenterBuilding
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End
";

    private static readonly Vector3 Origin = new(0, 0, 0);

    private sealed record Scenario(HeadlessSimGame Game, Player Saboteur, Player Target, GameObject SaboteurUnit);

    /// <summary>
    /// Two real, registered players (so BankAccount.Deposit's AcademyStats lookup resolves)
    /// each on their own team, wired to a chosen relationship - PlayerManager.OnNewGame does
    /// not yet establish player-to-player relationships (see its "TODO: Setup player
    /// relationships" note), so the test does it directly via the new Player.SetRelationship.
    /// </summary>
    private static Scenario NewScenario(RelationshipType targetRelationship)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x5AB0u);
        game.LoadIniText(Definitions);

        var mapPlayerOne = new OpenSage.Data.Map.Player { Name = "PlayerOne", Faction = "FactionOne", DisplayName = "PlayerOne" };
        var mapPlayerTwo = new OpenSage.Data.Map.Player { Name = "PlayerTwo", Faction = "FactionTwo", DisplayName = "PlayerTwo" };

        game.PlayerManager.OnNewGame(
            [
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                mapPlayerOne,
                mapPlayerTwo,
            ],
            GameType.Skirmish);

        var saboteurOwner = game.PlayerManager.GetPlayerByIndex(2);
        var targetOwner = game.PlayerManager.GetPlayerByIndex(3);
        saboteurOwner.SetRelationship(targetOwner, targetRelationship);

        var teamFactory = new TeamFactory(game);
        var saboteurTemplate = new TeamTemplate(teamFactory, 101, "SaboteurTeam", saboteurOwner, isSingleton: true);
        var saboteurTeam = new Team(saboteurTemplate, 101);

        var saboteurUnit = game.SpawnObject("Saboteur", saboteurOwner, Origin);
        saboteurUnit.Team = saboteurTeam;

        return new Scenario(game, saboteurOwner, targetOwner, saboteurUnit);
    }

    private static GameObject SpawnTarget(Scenario scenario, string definitionName, uint startingMoney)
    {
        var target = scenario.Game.SpawnObject(definitionName, scenario.Target, new Vector3(10, 0, 0));
        target.Team = new Team(new TeamTemplate(new TeamFactory(scenario.Game), 102, "TargetTeam", scenario.Target, isSingleton: true), 102);
        target.Owner.BankAccount.Money = startingMoney;
        return target;
    }

    private static void SetAsGoal(Scenario scenario, GameObject target)
    {
        scenario.SaboteurUnit.AIUpdate.GoalObject = target;
    }

    // ---- happy path: enemy supply center, sufficient funds ----

    [Fact]
    public void EnemySupplyCenter_SufficientFunds_StealsUpToStealCashAmount()
    {
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(1500u, target.Owner.BankAccount.Money);
        Assert.Equal(500u, scenario.Saboteur.BankAccount.Money);
    }

    // ---- rejection: dead target ----

    [Fact]
    public void DeadSupplyCenter_RejectsSabotage()
    {
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);
        target.IsEffectivelyDead = true;

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
    }

    // ---- rejection: not a supply center ----

    [Fact]
    public void NonSupplyCenterBuilding_RejectsSabotageRegardlessOfRelationship()
    {
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = SpawnTarget(scenario, "NonSupplyCenterBuilding", startingMoney: 2000);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
    }

    // ---- rejection: allied / neutral relationship ----

    [Theory]
    [InlineData(RelationshipType.Allies)]
    [InlineData(RelationshipType.Neutral)]
    public void NonEnemySupplyCenter_RejectsSabotage(RelationshipType relationship)
    {
        var scenario = NewScenario(relationship);
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
    }

    // ---- partial transfer: target has less than StealCashAmount available ----

    [Fact]
    public void EnemySupplyCenter_InsufficientFunds_TransfersOnlyWhatIsAvailable()
    {
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 200);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(0u, target.Owner.BankAccount.Money);
        Assert.Equal(200u, scenario.Saboteur.BankAccount.Money);
    }

    // ---- rejection: valid enemy supply center, but not the saboteur's AI goal object ----

    [Fact]
    public void EnemySupplyCenter_NotAiGoalObject_RejectsSabotage()
    {
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        // GoalObject deliberately left unset (null != target): the saboteur merely brushed
        // past the building rather than being ordered to sabotage it.

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
    }
}

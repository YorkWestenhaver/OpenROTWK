// Contract tests for the SabotageSupplyCenterCrateCollide port (R12, revised R13): a saboteur
// crate that steals cash from an enemy supply center on collision. The GPL executeCrateBehavior
// gate is [not dead -> is a supply center -> is an enemy -> is still the saboteur's AI goal
// object] before the cash transfer runs, so each rejection branch gets its own test alongside
// the happy path and the "insufficient funds" partial-transfer branch.
//
// R13: GPL's CrateCollide::onCollide destroys the saboteur immediately after a successful
// steal so the theft can only ever happen once (TheGameLogic->destroyObject). Tests below cover
// both that the saboteur is destroyed on success (GameObject.IsDestroyed) and that a second
// simulated collision frame against the same (still-overlapping) target no longer re-fires the
// theft once the saboteur has been destroyed - the concrete R12 bug this file's OnCollide used
// to have, where PartitionCellManager.Update()'s level-triggered dispatch would re-invoke
// OnCollide (and re-steal cash) on every subsequent frame the pair remained in collision.

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
    BuildingPickup = Yes
  End
End

Object SaboteurNoBuildingPickup
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

Science SCIENCE_TestSabotage
  IsGrantable = Yes
End

Object SaboteurRequiresSupplyCenterAndStructure
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSupplyCenterCrateCollide ModuleTag_Sabotage
    StealCashAmount = 500
    BuildingPickup = Yes
    RequiredKindOf = STRUCTURE FS_SUPPLY_CENTER
  End
End

Object SaboteurRequiresUnsatisfiableMask
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSupplyCenterCrateCollide ModuleTag_Sabotage
    StealCashAmount = 500
    BuildingPickup = Yes
    RequiredKindOf = FS_SUPPLY_CENTER VEHICLE
  End
End

Object SaboteurRequiresScience
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageSupplyCenterCrateCollide ModuleTag_Sabotage
    StealCashAmount = 500
    BuildingPickup = Yes
    PickupScience = SCIENCE_TestSabotage
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
    private static Scenario NewScenario(RelationshipType targetRelationship, string saboteurDefinitionName = "Saboteur")
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

        var saboteurUnit = game.SpawnObject(saboteurDefinitionName, saboteurOwner, Origin);
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
        // GPL's CrateCollide::onCollide destroys the saboteur immediately after a successful
        // steal (TheGameLogic->destroyObject) - the crate is consumed exactly once.
        Assert.True(scenario.SaboteurUnit.IsDestroyed);
    }

    // ---- R13: repeated collision (level-triggered dispatch) steals only once ----

    [Fact]
    public void EnemySupplyCenter_RepeatedOverlappingCollisions_StealsOnlyOnce()
    {
        // PartitionCellManager.Update() calls OnCollide unconditionally on every simulation
        // frame the pair is still detected as colliding (level-triggered, not edge-triggered).
        // A saboteur parked/blocked against the target for multiple frames must still only
        // drain StealCashAmount once, because GPL destroys the saboteur object on the first
        // successful theft (see OnCollide's DestroyObject call) - once destroyed, no further
        // OnCollide call for this object can execute the behavior again.
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);

        // Simulate three consecutive overlapping-collision frames, exactly as
        // PartitionCellManager.Update() would re-dispatch OnCollide for as long as
        // CollidesWith(...) keeps returning true for the still-overlapping pair.
        scenario.SaboteurUnit.OnCollide(target);
        scenario.SaboteurUnit.OnCollide(target);
        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(1500u, target.Owner.BankAccount.Money);
        Assert.Equal(500u, scenario.Saboteur.BankAccount.Money);
        Assert.True(scenario.SaboteurUnit.IsDestroyed);
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

    // ---- R13: base CrateCollide::isValidToExecute gate - BuildingPickup ----

    [Fact]
    public void MissingBuildingPickup_RejectsBuildingTargetEvenIfOtherwiseValid()
    {
        // GPL's base CrateCollide::isValidToExecute rejects any "other" with no
        // AIUpdateInterface unless md->m_isBuildingPickup && other->isKindOf(STRUCTURE) - a
        // supply center (a building, no AI) is only ever a valid sabotage target when the
        // crate collide module's own data explicitly sets BuildingPickup = Yes. Without it,
        // an otherwise-perfectly-valid enemy supply center target must still be rejected.
        var scenario = NewScenario(RelationshipType.Enemies, saboteurDefinitionName: "SaboteurNoBuildingPickup");
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
        Assert.False(scenario.SaboteurUnit.IsDestroyed);
    }

    // ---- R13: base CrateCollide::isValidToExecute gate - neutral-controlled owner ----

    [Fact]
    public void NeutralControlledSupplyCenter_RejectsSabotage()
    {
        // GPL's base CrateCollide::isValidToExecute rejects other->isNeutralControlled()
        // targets outright, before this module's own kindof/relationship checks even run.
        var scenario = NewScenario(RelationshipType.Enemies);
        var target = scenario.Game.SpawnObject("EnemySupplyCenter", scenario.Game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        target.Owner.BankAccount.Money = 2000;
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
        Assert.False(scenario.SaboteurUnit.IsDestroyed);
    }

    // ---- R13.5: shared CrateCollide::isValidToExecute base gate (crate-gate hoist) ----
    //
    // These cases exercise the base gate's own fields (RequiredKindOf-as-mask, PickupScience),
    // which this leaf's construction only started parsing/enforcing as of the 525ddaa0 hoist -
    // a target that would pass this leaf's own three checks (alive, FS_SUPPLY_CENTER, ENEMIES)
    // and the AI goal-object gate, so any rejection below can only come from the base gate.

    // RequiredKindOf is a MASK (GPL isKindOfMulti): EVERY bit must be present. The old
    // single-value parse would have kept only the last token of a multi-kind authored line.
    [Fact]
    public void RequiredKindOfMask_AcceptsTargetCarryingEveryBit()
    {
        var scenario = NewScenario(RelationshipType.Enemies, saboteurDefinitionName: "SaboteurRequiresSupplyCenterAndStructure");
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        // EnemySupplyCenter is KindOf = STRUCTURE FS_SUPPLY_CENTER - both required bits present.
        Assert.Equal(1500u, target.Owner.BankAccount.Money);
        Assert.Equal(500u, scenario.Saboteur.BankAccount.Money);
    }

    [Fact]
    public void RequiredKindOfMask_RejectsTargetMissingOneBit()
    {
        // Requires FS_SUPPLY_CENTER *and* VEHICLE; the supply center has only the former, so a
        // true mask rejects it. A single-value parse (last token wins = VEHICLE, unenforced)
        // would have accepted it.
        var scenario = NewScenario(RelationshipType.Enemies, saboteurDefinitionName: "SaboteurRequiresUnsatisfiableMask");
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
    }

    // PickupScience ("m_pickupScience"): only relevant when the collided-with object's owner
    // holds the named science. This module casts the sabotaged building as the base gate's
    // "collector" role, so it is the TARGET's owner (the sabotage victim) that must hold it.
    [Fact]
    public void PickupScience_TargetOwnerLacksIt_RejectsSabotage()
    {
        var scenario = NewScenario(RelationshipType.Enemies, saboteurDefinitionName: "SaboteurRequiresScience");
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);
        // Deliberately not granted: scenario.Target never receives SCIENCE_TestSabotage.

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(2000u, target.Owner.BankAccount.Money);
        Assert.Equal(0u, scenario.Saboteur.BankAccount.Money);
    }

    [Fact]
    public void PickupScience_TargetOwnerHasIt_AllowsSabotage()
    {
        var scenario = NewScenario(RelationshipType.Enemies, saboteurDefinitionName: "SaboteurRequiresScience");
        var target = SpawnTarget(scenario, "EnemySupplyCenter", startingMoney: 2000);
        SetAsGoal(scenario, target);
        scenario.Target.DirectlyAssignScience(scenario.Game.AssetStore.Sciences.GetByName("SCIENCE_TestSabotage"));

        scenario.SaboteurUnit.OnCollide(target);

        Assert.Equal(1500u, target.Owner.BankAccount.Money);
        Assert.Equal(500u, scenario.Saboteur.BankAccount.Money);
    }
}

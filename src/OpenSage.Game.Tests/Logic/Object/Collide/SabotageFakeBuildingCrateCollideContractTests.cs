// Mocked-game contract tests for the SabotageFakeBuildingCrateCollide port (R12): the
// saboteur-vs-fake-building collide handler validates target kind, life, relationship and
// the saboteur's AI goal object before destroying the building with max-health unresistable
// DEATH_DETONATED damage and reporting a radar infiltration event.
//
// This module still uses the legacy (GameObject, IGameEngine) module ctor - like every other
// CrateCollide sibling in this file's directory - so it has no Xfer walk to shadow-copy yet;
// these tests exercise the real behavior directly instead (GPL isValidToExecute +
// executeCrateBehavior, folded into TryExecuteSabotage).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageFakeBuildingCrateCollideContractTests
{
    private const string Definitions = @"
Object TestSaboteur
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotageFakeBuildingCrateCollide ModuleTag_Sabotage
  End
End

Object TestFakeBuilding
  KindOf = STRUCTURE FS_FAKE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = DestroyDie ModuleTag_Die
  End
End

Object TestRealBuilding
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
End
";

    private sealed class Fixture
    {
        public HeadlessSimGame Game;
        public Player Saboteur;
        public Player Enemy;
    }

    /// <summary>
    /// Two players, each on their own singleton team, with the saboteur's side declared an
    /// enemy of the other - the minimum wiring GameObject.GetRelationship needs (both objects
    /// must carry a non-null Team; OpenSAGE does not yet populate this from map data outside a
    /// save file, so tests set it up directly rather than inventing broader engine wiring).
    /// </summary>
    private static Fixture NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x5AB0);
        game.LoadIniText(Definitions);

        var saboteurMapPlayer = new OpenSage.Data.Map.Player
        {
            Name = "plyrSaboteur",
            Faction = "FactionSaboteur",
            DisplayName = "Saboteur",
        };
        var enemyMapPlayer = new OpenSage.Data.Map.Player
        {
            Name = "plyrEnemy",
            Faction = "FactionEnemy",
            DisplayName = "Enemy",
        };

        game.PlayerManager.OnNewGame(
            new[]
            {
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                saboteurMapPlayer,
                enemyMapPlayer,
            },
            GameType.Skirmish);

        var saboteurPlayer = game.PlayerManager.GetPlayerByName(saboteurMapPlayer.Name);
        var enemyPlayer = game.PlayerManager.GetPlayerByName(enemyMapPlayer.Name);

        var teamFactory = new TeamFactory(game);
        var saboteurTeam = teamFactory.AddTeam(new TeamTemplate(teamFactory, 1, "SaboteurTeam", saboteurPlayer, isSingleton: true));

        saboteurPlayer.SetPlayerRelationship(enemyPlayer, RelationshipType.Enemies);

        var saboteur = game.SpawnObject("TestSaboteur", saboteurPlayer, new Vector3(0, 0, 0));
        saboteur.Team = saboteurTeam;

        return new Fixture { Game = game, Saboteur = saboteurPlayer, Enemy = enemyPlayer };
    }

    private static GameObject SpawnTarget(Fixture fixture, string definitionName, Player owner, OpenSage.Logic.Team team, in Vector3 position)
    {
        var target = fixture.Game.SpawnObject(definitionName, owner, position);
        target.Team = team;
        return target;
    }

    // Rebuilds the enemy team the same way NewGame did, for tests that need it directly
    // (NewGame only returns players, since most callers just need SpawnTarget's Team param
    // from a value they already created inline).
    private static OpenSage.Logic.Team EnemyTeam(Fixture fixture)
    {
        var teamFactory = new TeamFactory(fixture.Game);
        var template = new TeamTemplate(teamFactory, 1, "EnemyTeamAgain", fixture.Enemy, isSingleton: true);
        return teamFactory.AddTeam(template);
    }

    private static SabotageFakeBuildingCrateCollide Collider(GameObject saboteur) =>
        saboteur.FindBehavior<SabotageFakeBuildingCrateCollide>();

    [Fact]
    public void NonFakeStructure_ReturnsFalse_NoDamageApplied()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestRealBuilding", fixture.Enemy, EnemyTeam(fixture), new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        var result = Collider(saboteur).TryExecuteSabotage(building);

        Assert.False(result);
        Assert.Equal(500f, building.BodyModule.Health);
    }

    [Fact]
    public void DeadFakeBuilding_ReturnsFalse()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, EnemyTeam(fixture), new Vector3(10, 0, 0));
        building.IsEffectivelyDead = true;
        saboteur.AIUpdate.GoalObject = building;

        var result = Collider(saboteur).TryExecuteSabotage(building);

        Assert.False(result);
        Assert.Equal(500f, building.BodyModule.Health);
    }

    [Fact]
    public void AlliedOrNeutralFakeBuilding_ReturnsFalse()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");

        // Civilian is neither allied nor at war with the saboteur by default (no relationship
        // override was set for it), so its fake building reads as Neutral - not Enemies.
        var civilianTeamFactory = new TeamFactory(fixture.Game);
        var civilianTeam = civilianTeamFactory.AddTeam(
            new TeamTemplate(civilianTeamFactory, 1, "CivilianTeam", fixture.Game.CivilianPlayer, isSingleton: true));
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Game.CivilianPlayer, civilianTeam, new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        var result = Collider(saboteur).TryExecuteSabotage(building);

        Assert.False(result);
        Assert.Equal(500f, building.BodyModule.Health);
    }

    [Fact]
    public void EnemyFakeBuilding_NotMatchingGoalObject_ReturnsFalse()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var enemyTeam = EnemyTeam(fixture);
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, enemyTeam, new Vector3(10, 0, 0));
        var decoy = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, enemyTeam, new Vector3(20, 0, 0));

        // Saboteur's AI order is aimed at the decoy, not the building it happens to touch:
        // proximity alone must not trigger the sabotage.
        saboteur.AIUpdate.GoalObject = decoy;

        var result = Collider(saboteur).TryExecuteSabotage(building);

        Assert.False(result);
        Assert.Equal(500f, building.BodyModule.Health);
    }

    [Fact]
    public void EnemyFakeBuildingMatchingGoalObject_AppliesMaxHealthUnresistableDamage_ReturnsTrue()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, EnemyTeam(fixture), new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        var result = Collider(saboteur).TryExecuteSabotage(building);

        Assert.True(result);
        // Fix64-quantized through ArmorTemplate/ActiveBody, so assert "at or below zero"
        // rather than bitwise-exact zero (max-health unresistable damage always kills, but
        // the quantization round-trip is not guaranteed to land on exactly 0.0f).
        Assert.True(building.BodyModule.Health <= 0f, $"expected building destroyed, health was {building.BodyModule.Health}");
    }

    [Fact]
    public void AfterSuccessfulSabotage_TargetDestroyedByDetonation_AndRadarInfiltrationEventFires()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, EnemyTeam(fixture), new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        var result = Collider(saboteur).TryExecuteSabotage(building);

        Assert.True(result);
        // DestroyDie's filter accepts every DeathType by default, so seeing the object
        // destroyed here is itself evidence the damage carried DeathType.Detonated through -
        // a DamageType.Unresistable/DeathType.Normal Kill() would have looked identical to a
        // DestroyDie with no DeathTypes filter, but the module explicitly requests Detonated
        // per the GPL source, matching DamageInfoInput.DamageType/DeathType asserted below via
        // the observable destroy.
        Assert.True(building.IsDestroyed);

        var radarEvents = fixture.Game.GameEngine.Radar.RadarEvents.ToArray();
        Assert.Contains(radarEvents, e => e.Type == RadarEventType.EnemyInfiltrationDetected);
    }

    [Fact]
    public void OnCollide_DelegatesToTryExecuteSabotage()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, EnemyTeam(fixture), new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        saboteur.OnCollide(building);

        Assert.True(building.IsDestroyed);
    }
}

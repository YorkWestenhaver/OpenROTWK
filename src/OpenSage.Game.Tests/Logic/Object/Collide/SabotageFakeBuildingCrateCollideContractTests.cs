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
    ; R13.5: the shared CrateCollide::isValidToExecute gate this module now runs first
    ; rejects an AI-less target unless BuildingPickup is set - the real GPL requirement for
    ; a building-kinded victim, matching the SabotageSuperweapon/SabotagePowerPlant fixtures.
    BuildingPickup = Yes
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
    /// Two players wired up the way a real skirmish game actually populates enmity: the map
    /// player's <c>Enemies</c>/<c>Allies</c> name lists, which <see cref="PlayerManager.OnNewGame"/>
    /// (via its private CreatePlayers) feeds straight into <see cref="Player.Enemies"/> -
    /// <em>not</em> <see cref="Player.SetRelationship"/>, which backs
    /// <see cref="GameObject.GetRelationship"/> and is never called by
    /// <c>PlayerManager.OnNewGame</c> in real play (it has a literal
    /// "// TODO: Setup player relationships." right after building the player list). A prior
    /// version of this fixture called <c>SetRelationship</c> directly and hand-built
    /// <c>Team</c>/<c>TeamFactory</c> objects, which populated a dictionary the production code
    /// no longer reads and let the tests pass against a relationship path real games never
    /// populate.
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
            Enemies = "plyrEnemy",
        };
        var enemyMapPlayer = new OpenSage.Data.Map.Player
        {
            Name = "plyrEnemy",
            Faction = "FactionEnemy",
            DisplayName = "Enemy",
            Enemies = "plyrSaboteur",
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

        var saboteur = game.SpawnObject("TestSaboteur", saboteurPlayer, new Vector3(0, 0, 0));

        return new Fixture { Game = game, Saboteur = saboteurPlayer, Enemy = enemyPlayer };
    }

    private static GameObject SpawnTarget(Fixture fixture, string definitionName, Player owner, in Vector3 position)
    {
        return fixture.Game.SpawnObject(definitionName, owner, position);
    }

    private static SabotageFakeBuildingCrateCollide Collider(GameObject saboteur) =>
        saboteur.FindBehavior<SabotageFakeBuildingCrateCollide>();

    [Fact]
    public void NonFakeStructure_ReturnsFalse_NoDamageApplied()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestRealBuilding", fixture.Enemy, new Vector3(10, 0, 0));
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
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, new Vector3(10, 0, 0));
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

        // Civilian is neither allied nor at war with the saboteur by default (its map-side
        // Enemies list is empty), so it is absent from Owner.Enemies - not an enemy.
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Game.CivilianPlayer, new Vector3(10, 0, 0));
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
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, new Vector3(10, 0, 0));
        var decoy = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, new Vector3(20, 0, 0));

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
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, new Vector3(10, 0, 0));
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
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, new Vector3(10, 0, 0));
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
    public void OnCollide_DelegatesToTryExecuteSabotage_AndDestroysBoth()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestFakeBuilding", fixture.Enemy, new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        saboteur.OnCollide(building);

        Assert.True(building.IsDestroyed);
        // GPL's shared CrateCollide::onCollide calls
        // TheGameLogic->destroyObject(getObject()) whenever executeCrateBehavior returns
        // true - "crate" is a misnomer for this family, getObject() is the saboteur itself,
        // so a successful sabotage consumes the saboteur as a one-shot action just like the
        // rest of the crate-collide family.
        Assert.True(saboteur.IsDestroyed);
    }

    [Fact]
    public void OnCollide_FailedSabotage_SaboteurSurvives()
    {
        var fixture = NewGame();
        var saboteur = fixture.Game.GameLogic.Objects.Single(o => o.Definition.Name == "TestSaboteur");
        var building = SpawnTarget(fixture, "TestRealBuilding", fixture.Enemy, new Vector3(10, 0, 0));
        saboteur.AIUpdate.GoalObject = building;

        saboteur.OnCollide(building);

        Assert.False(building.IsDestroyed);
        Assert.False(saboteur.IsDestroyed);
    }
}

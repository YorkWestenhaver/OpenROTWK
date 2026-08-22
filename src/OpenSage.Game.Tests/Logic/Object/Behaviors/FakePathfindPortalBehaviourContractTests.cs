// R15 L5-P9 — contract tests for the GROUNDED half of FakePathfindPortalBehaviour (castle/wall
// gates), per the L5-P2 spec (research/modules-r13/specs/FakePathfindPortalBehaviourModuleData.md).
//
// What is covered: the parse contract (spec §1, both fields default No) and the unit-category
// filter (spec §3 Claim 2) — the enemy test through GameObject.GetRelationship and the
// non-skirmish-AI-owner test through Player.AIPlayer.
//
// What is deliberately NOT covered: any pathfind-grid effect. The grid side is HELD (spec §3
// Claim 1 / blackboard [L5-P2 #1]): SimPathfindGrid has no conditional-passability primitive to
// register against, so this module answers a question and changes nothing. Tests asserting a
// gate cell becomes passable belong to the future pathfinder-subsystem packet (spec §4 / T3).
//
// Relationship wiring follows LeafletDropBehaviorContractTests: PlayerManager.OnNewGame leaves
// player-to-player relationships unset ("TODO: Setup player relationships"), and
// GameObject.GetRelationship resolves gate.Team -> gate's controlling player -> unit's Team, so
// every scenario registers extra players, points the override from the GATE's owner at the unit's
// owner, and gives both objects a real singleton Team (a null Team always reads Neutral).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.AI;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;
using Player = OpenSage.Logic.Player;
using Team = OpenSage.Logic.Team;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class FakePathfindPortalBehaviourContractTests
{
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

PlayerTemplate FactionGateTest
  Side = GateTest
  PlayableSide = Yes
  StartMoney = 0
End

Object TestWallGate
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FakePathfindPortalBehaviour ModuleTag_FakePathfind
    AllowEnemies            = No
    AllowNonSkirmishAIUnits = No
  End
End

Object TestOpenWallGate
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FakePathfindPortalBehaviour ModuleTag_FakePathfind
    AllowEnemies            = Yes
    AllowNonSkirmishAIUnits = Yes
  End
End

Object TestDefaultWallGate
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FakePathfindPortalBehaviour ModuleTag_FakePathfind
  End
End

Object TestGateUser
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End
";

    private sealed record Scenario(HeadlessSimGame Game, Player GateOwner, Player EnemyOwner, Player AlliedOwner, Player NeutralOwner);

    /// <summary>
    /// Four registered players: the gate's owner plus one enemy, one ally and one player with no
    /// relationship override at all (which reads Neutral).
    /// </summary>
    private static Scenario NewScenario(uint seed = 0x6A7E) // "GATE"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);

        var mapGatePlayer = new OpenSage.Data.Map.Player { Name = "GatePlayer", Faction = "FactionOne", DisplayName = "GatePlayer" };
        var mapEnemyPlayer = new OpenSage.Data.Map.Player { Name = "EnemyPlayer", Faction = "FactionTwo", DisplayName = "EnemyPlayer" };
        var mapAlliedPlayer = new OpenSage.Data.Map.Player { Name = "AlliedPlayer", Faction = "FactionThree", DisplayName = "AlliedPlayer" };
        var mapNeutralPlayer = new OpenSage.Data.Map.Player { Name = "NeutralPlayer", Faction = "FactionFour", DisplayName = "NeutralPlayer" };

        game.PlayerManager.OnNewGame(
            [
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                mapGatePlayer,
                mapEnemyPlayer,
                mapAlliedPlayer,
                mapNeutralPlayer,
            ],
            GameType.Skirmish);

        var gateOwner = game.PlayerManager.GetPlayerByIndex(2);
        var enemyOwner = game.PlayerManager.GetPlayerByIndex(3);
        var alliedOwner = game.PlayerManager.GetPlayerByIndex(4);
        var neutralOwner = game.PlayerManager.GetPlayerByIndex(5);

        // GetRelationship resolves through the GATE's team/player (the gate is the asker here).
        gateOwner.SetRelationship(enemyOwner, RelationshipType.Enemies);
        gateOwner.SetRelationship(alliedOwner, RelationshipType.Allies);

        return new Scenario(game, gateOwner, enemyOwner, alliedOwner, neutralOwner);
    }

    private static GameObject Spawn(Scenario scenario, string definitionName, Player owner, in Vector3 position, uint teamId)
    {
        var obj = scenario.Game.SpawnObject(definitionName, owner, position);
        obj.Team = new Team(new TeamTemplate(new TeamFactory(scenario.Game), teamId, $"Team{teamId}", owner, isSingleton: true), teamId);
        return obj;
    }

    private static FakePathfindPortalBehaviour ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<FakePathfindPortalBehaviour>().Single();

    // ---- T1: parse contract (spec §1) ----

    [Fact]
    public void AuthoredFields_ParseAndReachTheRuntimeModule()
    {
        var scenario = NewScenario();
        var closedGate = Spawn(scenario, "TestWallGate", scenario.GateOwner, Vector3.Zero, teamId: 201);
        var openGate = Spawn(scenario, "TestOpenWallGate", scenario.GateOwner, new Vector3(50, 0, 0), teamId: 201);

        Assert.False(ModuleOf(closedGate).AllowEnemies);
        Assert.False(ModuleOf(closedGate).AllowNonSkirmishAIUnits);
        Assert.True(ModuleOf(openGate).AllowEnemies);
        Assert.True(ModuleOf(openGate).AllowNonSkirmishAIUnits);
    }

    [Fact]
    public void OmittedFields_DefaultToNo_AndTheModuleIsStillCreated()
    {
        var scenario = NewScenario();
        var gate = Spawn(scenario, "TestDefaultWallGate", scenario.GateOwner, Vector3.Zero, teamId: 201);

        var module = ModuleOf(gate); // no longer [ParseOnly]: CreateModule really builds one
        Assert.False(module.AllowEnemies);
        Assert.False(module.AllowNonSkirmishAIUnits);
    }

    // ---- T2: the enemy half of the unit-category filter ----

    [Fact]
    public void AllowEnemiesOff_RefusesEnemyUnits_AndStillAdmitsAlliesAndNeutrals()
    {
        var scenario = NewScenario();
        var gate = Spawn(scenario, "TestWallGate", scenario.GateOwner, Vector3.Zero, teamId: 201);
        var enemy = Spawn(scenario, "TestGateUser", scenario.EnemyOwner, new Vector3(10, 0, 0), teamId: 202);
        var ally = Spawn(scenario, "TestGateUser", scenario.AlliedOwner, new Vector3(10, 0, 0), teamId: 203);
        var neutral = Spawn(scenario, "TestGateUser", scenario.NeutralOwner, new Vector3(10, 0, 0), teamId: 204);
        var own = Spawn(scenario, "TestGateUser", scenario.GateOwner, new Vector3(10, 0, 0), teamId: 205);

        var module = ModuleOf(gate);
        Assert.False(module.IsUnitAllowedThrough(enemy));
        Assert.True(module.IsUnitAllowedThrough(ally));
        Assert.True(module.IsUnitAllowedThrough(neutral));
        Assert.True(module.IsUnitAllowedThrough(own));
    }

    [Fact]
    public void AllowEnemiesOn_AdmitsEnemyUnits()
    {
        var scenario = NewScenario();
        var gate = Spawn(scenario, "TestOpenWallGate", scenario.GateOwner, Vector3.Zero, teamId: 201);
        var enemy = Spawn(scenario, "TestGateUser", scenario.EnemyOwner, new Vector3(10, 0, 0), teamId: 202);

        Assert.True(ModuleOf(gate).IsUnitAllowedThrough(enemy));
    }

    [Fact]
    public void NoUnit_IsNeverAdmitted()
    {
        var scenario = NewScenario();
        var gate = Spawn(scenario, "TestWallGate", scenario.GateOwner, Vector3.Zero, teamId: 201);

        Assert.False(ModuleOf(gate).IsUnitAllowedThrough(null));
    }

    // ---- T3: the owner-category half (spec §3 Claim 2) ----
    //
    // Player.FromMapData is called directly rather than through PlayerManager.OnNewGame so a
    // single game can hold both a skirmish-AI and a non-skirmish-AI player (OnNewGame's GameType
    // fixes isSkirmish for the whole match), and so no skirmish AI brain is attached.

    private static Player AiPlayerFor(HeadlessSimGame game, uint index, bool isSkirmish, bool isHuman = false)
    {
        var mapPlayer = new OpenSage.Data.Map.Player
        {
            Name = $"AiTestPlayer{index}",
            Faction = "FactionGateTest",
            DisplayName = $"AiTestPlayer{index}",
            IsHuman = isHuman,
        };

        return Player.FromMapData(index, mapPlayer, game, isSkirmish);
    }

    [Fact]
    public void OwnerClassification_MatchesTheThreeWaySplitTheFilterDependsOn()
    {
        var scenario = NewScenario();
        var game = scenario.Game;

        Assert.Null(AiPlayerFor(game, 10, isSkirmish: true, isHuman: true).AIPlayer);
        Assert.IsType<SkirmishAIPlayer>(AiPlayerFor(game, 11, isSkirmish: true).AIPlayer);
        Assert.IsType<AIPlayer>(AiPlayerFor(game, 12, isSkirmish: false).AIPlayer); // exact type, not the skirmish subclass
    }

    [Fact]
    public void AllowNonSkirmishAIUnitsOff_RefusesNonSkirmishAIOwnedUnits_Only()
    {
        var scenario = NewScenario();
        var game = scenario.Game;

        var human = AiPlayerFor(game, 10, isSkirmish: true, isHuman: true);
        var skirmishAi = AiPlayerFor(game, 11, isSkirmish: true);
        var scriptedAi = AiPlayerFor(game, 12, isSkirmish: false);

        Assert.False(FakePathfindPortalBehaviour.IsNonSkirmishAIOwned(human));
        Assert.False(FakePathfindPortalBehaviour.IsNonSkirmishAIOwned(skirmishAi));
        Assert.True(FakePathfindPortalBehaviour.IsNonSkirmishAIOwned(scriptedAi));

        // Neutral relationship, so only the owner-category half of the filter is in play.
        Assert.True(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Neutral, human, allowEnemies: false, allowNonSkirmishAIUnits: false));
        Assert.True(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Neutral, skirmishAi, allowEnemies: false, allowNonSkirmishAIUnits: false));
        Assert.False(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Neutral, scriptedAi, allowEnemies: false, allowNonSkirmishAIUnits: false));
    }

    [Fact]
    public void AllowNonSkirmishAIUnitsOn_AdmitsNonSkirmishAIOwnedUnits()
    {
        var scenario = NewScenario();
        var scriptedAi = AiPlayerFor(scenario.Game, 12, isSkirmish: false);

        Assert.True(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Neutral, scriptedAi, allowEnemies: false, allowNonSkirmishAIUnits: true));
    }

    [Fact]
    public void TheTwoFilterHalvesAreIndependent_EitherRefusalIsEnough()
    {
        var scenario = NewScenario();
        var scriptedAi = AiPlayerFor(scenario.Game, 12, isSkirmish: false);

        // Enemy AND non-skirmish AI: refused while either flag is off.
        Assert.False(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Enemies, scriptedAi, allowEnemies: true, allowNonSkirmishAIUnits: false));
        Assert.False(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Enemies, scriptedAi, allowEnemies: false, allowNonSkirmishAIUnits: true));
        Assert.True(FakePathfindPortalBehaviour.IsUnitAllowed(RelationshipType.Enemies, scriptedAi, allowEnemies: true, allowNonSkirmishAIUnits: true));
    }
}

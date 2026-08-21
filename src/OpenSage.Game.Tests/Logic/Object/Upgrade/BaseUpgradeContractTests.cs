// Mocked-game contract tests for the BaseUpgrade port (R14 packet g5-baseupgrade), per
// bfme2-workbench/research/modules-r13/specs/BaseUpgradeModuleData.md §6's implementation
// blueprint. Definitions parse from INI text through the real parser, mirroring the other
// Upgrade/*ContractTests.cs files (e.g. ReplaceSelfUpgradeContractTests.cs).
//
// HARNESS SCOPE NOTE: HeadlessSimGame's Drawables carry no draw modules (no graphics device,
// no W3D files on disk - see HeadlessSimGame.cs's header comment), so the "PlacementIndex picks
// a real matched bone" branch (spec §5.3 step 4's non-fallback arm) is exercised at the pure
// logic level instead, in BaseUpgradePlacementResolutionTests.cs. What IS exercised here end to
// end: template resolution (found vs. missing), the always-active fallback-to-object-position
// path (a real, faithfully-ported retail branch - not merely a defensive stub, and exactly what
// a headless/bone-less object hits regardless of PlacementIndex), owner/rotation/
// CreatedByObjectID propagation, and the one-shot/no-re-arm guarantee.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class BaseUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_TestBuilding
  Type = PLAYER
End

Object CarrierMissingTemplate
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BaseUpgrade ModuleTag_Base
    TriggeredBy = Upgrade_TestBuilding
    BuildingTemplateName = ThisTemplateDoesNotExist
    PlacementPrefix = upgrade
    PlacementIndex = 1
  End
End

Object CarrierNoPlacementFields
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BaseUpgrade ModuleTag_Base
    TriggeredBy = Upgrade_TestBuilding
    BuildingTemplateName = MordorTent
  End
End

Object CarrierWithPlacementFields
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BaseUpgrade ModuleTag_Base
    TriggeredBy = Upgrade_TestBuilding
    BuildingTemplateName = MordorTent
    PlacementPrefix = upgrade
    PlacementIndex = 1
  End
End

Object MordorTent
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xBA5EU) =>
        NewGame(out _, seed);

    private static HeadlessSimGame NewGame(out Player enemyOwner, uint seed = 0xBA5EU)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);

        var mapEnemyPlayer = new OpenSage.Data.Map.Player
        {
            Name = "EnemyPlayer",
            Faction = "FactionOne",
            DisplayName = "EnemyPlayer",
        };

        game.PlayerManager.OnNewGame(
            [
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                mapEnemyPlayer,
            ],
            GameType.Skirmish);

        enemyOwner = game.PlayerManager.GetPlayerByIndex(2);
        return game;
    }

    private static BaseUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<BaseUpgrade>().Single();

    private static UpgradeSet TestBuildingSet(HeadlessSimGame game) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_TestBuilding") };

    // ---- Template resolution: a BuildingTemplateName that doesn't resolve -> no spawn, no
    //      crash (spec §5.3 step 2) ----

    [Fact]
    public void MissingBuildingTemplate_NoObjectIsSpawned()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("CarrierMissingTemplate", game.CivilianPlayer, Vector3.Zero);

        ModuleOf(carrier).TryUpgrade(TestBuildingSet(game));

        Assert.DoesNotContain(game.GameLogic.Objects, o => o.CreatedByObjectID == carrier.Id);
    }

    // ---- Instant, synchronous spawn: a valid template spawns exactly one new object,
    //      same frame, no queue (spec §5.3 step 5 / §5.4) ----

    [Fact]
    public void ValidTemplate_SpawnsExactlyOneBuildingImmediately()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("CarrierNoPlacementFields", game.CivilianPlayer, Vector3.Zero);

        ModuleOf(carrier).TryUpgrade(TestBuildingSet(game));

        Assert.Single(game.GameLogic.Objects, o => o.CreatedByObjectID == carrier.Id);
        var spawned = game.GameLogic.Objects.Single(o => o.CreatedByObjectID == carrier.Id);
        Assert.Equal("MordorTent", spawned.Definition.Name);
    }

    // ---- Fallback placement: headless carriers have no draw modules, so PlacementPrefix/
    //      PlacementIndex always hit the object-position fallback (spec §5.3 step 4) - a real
    //      retail branch, not a stub. Position should exactly match the carrying object's own
    //      translation at trigger time. ----

    [Fact]
    public void NoMatchingBones_FallsBackToCarryingObjectsOwnPosition()
    {
        var game = NewGame();
        var position = new Vector3(120, 340, 7);
        var carrier = game.SpawnObject("CarrierWithPlacementFields", game.CivilianPlayer, position);

        ModuleOf(carrier).TryUpgrade(TestBuildingSet(game));

        var spawned = game.GameLogic.Objects.Single(o => o.CreatedByObjectID == carrier.Id);
        Assert.Equal(carrier.Translation, spawned.Translation);
    }

    // ---- Owner: the spawned building is owned by the carrying object's own player, not
    //      necessarily the civilian/default player (spec §5.3 step 5) ----

    [Fact]
    public void SpawnedBuilding_IsOwnedByTheCarryingObjectsPlayer()
    {
        var game = NewGame(out var enemyOwner);
        var carrier = game.SpawnObject("CarrierNoPlacementFields", enemyOwner, Vector3.Zero);

        ModuleOf(carrier).TryUpgrade(TestBuildingSet(game));

        var spawned = game.GameLogic.Objects.Single(o => o.CreatedByObjectID == carrier.Id);
        Assert.Same(enemyOwner, spawned.Owner);
    }

    // ---- Facing: the spawned building inherits the carrying object's own rotation
    //      (spec §5.3 step 6, INFERRED but low-risk) ----

    [Fact]
    public void SpawnedBuilding_InheritsCarryingObjectsRotation()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("CarrierNoPlacementFields", game.CivilianPlayer, Vector3.Zero);
        var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2345f);
        carrier.SetRotation(facing);

        ModuleOf(carrier).TryUpgrade(TestBuildingSet(game));

        var spawned = game.GameLogic.Objects.Single(o => o.CreatedByObjectID == carrier.Id);
        Assert.Equal(facing, spawned.Rotation);
    }

    // ---- One-shot: no occupancy/re-fire state exists (spec §4/§5.4) - a second trigger
    //      attempt (the shared UpgradeLogic mux, already-ported and unmodified here) must not
    //      spawn a second building ----

    [Fact]
    public void SecondTriggerAttempt_DoesNotSpawnASecondBuilding()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("CarrierNoPlacementFields", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(carrier);
        var upgrades = TestBuildingSet(game);

        module.TryUpgrade(upgrades);
        module.TryUpgrade(upgrades);

        Assert.Single(game.GameLogic.Objects, o => o.CreatedByObjectID == carrier.Id);
    }
}

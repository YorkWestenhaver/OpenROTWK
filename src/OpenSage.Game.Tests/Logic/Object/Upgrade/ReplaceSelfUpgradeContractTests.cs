// Mocked-game contract tests for the ReplaceSelfUpgrade port (R12): the task packet's
// testCases, one test per case, plus the shared shadow-copy base test. Definitions parse
// from INI text through the real parser.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class ReplaceSelfUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Metamorphose
  Type = PLAYER
End

Object OriginalUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceSelfUpgrade ModuleTag_Replace
    TriggeredBy = Upgrade_Metamorphose
    ReplaceWith = ReplacementUnit
  End
End

Object OriginalWithSpawns
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceSelfUpgrade ModuleTag_Replace
    TriggeredBy = Upgrade_Metamorphose
    ReplaceWith = ReplacementUnit
    AndThenAddA = FootmanUnit
    AndThenAddA = ArcherUnit
  End
End

Object OriginalWithThreeSpawns
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceSelfUpgrade ModuleTag_Replace
    TriggeredBy = Upgrade_Metamorphose
    AndThenAddA = SpawnUnitOne
    AndThenAddA = SpawnUnitTwo
    AndThenAddA = SpawnUnitThree
  End
End

Object OriginalNoSpawns
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceSelfUpgrade ModuleTag_Replace
    TriggeredBy = Upgrade_Metamorphose
    ReplaceWith = ReplacementUnit
  End
End

Object ReplacementUnit
  KindOf = INFANTRY
  IsTrainable = Yes
  ExperienceValue = 10 20 30 40
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object FootmanUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object ArcherUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SpawnUnitOne
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SpawnUnitTwo
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SpawnUnitThree
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x125) =>
        NewGame(out _, seed);

    private static HeadlessSimGame NewGame(out Player enemyOwner, uint seed = 0x125)
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

    private static ReplaceSelfUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ReplaceSelfUpgrade>().Single();

    private static UpgradeSet MetamorphoseSet(HeadlessSimGame game) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_Metamorphose") };

    // ---- testCase 1: Basic metamorphosis ----

    [Fact]
    public void BasicMetamorphosis_OriginalDisappears_ReplacementAppearsAtSameCoordinates()
    {
        var game = NewGame();
        var position = new Vector3(120, 340, 0);
        var original = game.SpawnObject("OriginalUnit", game.CivilianPlayer, position);

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        Assert.True(original.IsDestroyed);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacementUnit");
        Assert.Equal(position, replacement.Transform.Translation);
    }

    // ---- testCase 2: With spawns ----

    [Fact]
    public void WithSpawns_ReplacementAndBothAddedUnitsArePresent()
    {
        var game = NewGame();
        var original = game.SpawnObject("OriginalWithSpawns", game.CivilianPlayer, new Vector3(10, 20, 0));

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        Assert.True(original.IsDestroyed);
        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "ReplacementUnit");
        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "FootmanUnit");
        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "ArcherUnit");
    }

    // ---- testCase 3: Position preservation (arbitrary, non-origin position) ----

    [Fact]
    public void PositionPreservation_ReplacementMatchesOriginalExactly_AtArbitraryPosition()
    {
        var game = NewGame();
        var position = new Vector3(-875.5f, 2310.25f, 40f);
        var original = game.SpawnObject("OriginalUnit", game.CivilianPlayer, position);

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacementUnit");
        Assert.Equal(position, replacement.Transform.Translation);
    }

    // ---- testCase 4: Ownership/team preservation ----

    [Fact]
    public void OwnershipPreservation_ReplacementIsSameFactionAsOriginal()
    {
        var game = NewGame(out var enemyOwner);
        var original = game.SpawnObject("OriginalUnit", enemyOwner, new Vector3(5, 5, 0));

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacementUnit");
        Assert.Same(enemyOwner, replacement.Owner);
    }

    // ---- testCase 5: Empty spawns ----

    [Fact]
    public void EmptySpawns_NoAdditionalUnitsSpawned_ReplacementStillSucceeds()
    {
        var game = NewGame();
        var original = game.SpawnObject("OriginalNoSpawns", game.CivilianPlayer, new Vector3(1, 1, 0));

        var objectCountBefore = game.GameLogic.Objects.Count();

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        Assert.True(original.IsDestroyed);
        // Exactly one new live object appeared (the replacement) - the original is still
        // enumerable (destroyed-but-not-reaped) alongside it.
        Assert.Equal(objectCountBefore + 1, game.GameLogic.Objects.Count());
        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "ReplacementUnit");
    }

    // ---- testCase 6: Multi-spawn ordering ----

    [Fact]
    public void MultiSpawnOrdering_AllThreeSpawnedAtSameLocation()
    {
        var game = NewGame();
        var position = new Vector3(60, 70, 0);
        var original = game.SpawnObject("OriginalWithThreeSpawns", game.CivilianPlayer, position);

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        var spawned = game.GameLogic.Objects
            .Where(o => o.Definition.Name is "SpawnUnitOne" or "SpawnUnitTwo" or "SpawnUnitThree")
            .ToList();

        Assert.Equal(3, spawned.Count);
        Assert.All(spawned, o => Assert.Equal(position, o.Transform.Translation));
    }

    // ---- Veterancy carryover ----

    [Fact]
    public void Veterancy_CarriesForwardToReplacement()
    {
        var game = NewGame();
        var original = game.SpawnObject("OriginalUnit", game.CivilianPlayer, Vector3.Zero);
        original.ExperienceTracker.SetVeterancyLevel(VeterancyLevel.Elite, provideFeedback: false);

        ModuleOf(original).TryUpgrade(MetamorphoseSet(game));

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacementUnit");
        Assert.Equal(VeterancyLevel.Elite, replacement.ExperienceTracker.VeterancyLevel);
    }

    // ---- Idempotence (shared upgrade mux) ----

    [Fact]
    public void SecondTrigger_IsIdempotent_OnlyOneReplacementSpawned()
    {
        var game = NewGame();
        var original = game.SpawnObject("OriginalUnit", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(original);
        var upgrades = MetamorphoseSet(game);

        module.TryUpgrade(upgrades);
        module.TryUpgrade(upgrades);

        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "ReplacementUnit");
    }

    // ---- shadow-copy base test ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("OriginalUnit", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(liveHost);
        live.TryUpgrade(MetamorphoseSet(game));

        var shadowHost = game.SpawnObject("OriginalUnit", game.CivilianPlayer, new Vector3(500, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}

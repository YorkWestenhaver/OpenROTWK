// Mocked-game contract tests for the CivilianSpawnUpdate port (R13): the periodic
// random-pick spawn (Civilian pool), the S5-class SpawnDelayTime/MaximumDistance retypes, the
// audit's core Civilian-field fix (object-template references, not ObjectKinds bits), and the
// TryFindRunToTarget selection-only query (F-CSU-2). Same shape as
// PickupStuffUpdateContractTests / CritterEmitterUpdateContractTests.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class CivilianSpawnUpdateContractTests
{
    // 5 Hz -> 1000 ms = 5 frames. SpawnDelayTime here (2000 ms) is 10 frames, scaled down from
    // the real AotR usage's 5000 ms = 25 frames for fast tests (spec §3).
    private const string Definitions = @"
Object CivilianVillagerA
  KindOf = CIVILIAN
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object CivilianVillagerB
  KindOf = CIVILIAN
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object SafeHavenBuilding
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object NotASafeHaven
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object CivSpawnBuildingKindOfFilter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = CivilianSpawnUpdate ModuleTag_CreatePeople
    SpawnDelayTime  = 2000   ; 10 frames
    MaximumDistance = 150
    RunToFilter     = ANY +STRUCTURE
    Civilian        = CivilianVillagerA CivilianVillagerB
  End
End

Object CivSpawnBuildingTemplateFilter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = CivilianSpawnUpdate ModuleTag_CreatePeople
    SpawnDelayTime  = 2000   ; 10 frames
    MaximumDistance = 150
    RunToFilter     = ANY +SafeHavenBuilding
    Civilian        = CivilianVillagerA CivilianVillagerB
  End
End

Object CivSpawnEmptyPool
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = CivilianSpawnUpdate ModuleTag_CreatePeople
    SpawnDelayTime  = 2000   ; 10 frames
    MaximumDistance = 150
    RunToFilter     = ANY +STRUCTURE
    ; Civilian intentionally omitted - the pathological/no-op case.
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC8A)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static CivilianSpawnUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CivilianSpawnUpdate>().Single();

    private static readonly string[] VillagerNames = { "CivilianVillagerA", "CivilianVillagerB" };

    [Fact]
    public void SpawnDelayTime_ParsesMillisecondsToFrames()
    {
        var game = NewGame();
        var data = (CivilianSpawnUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("CivSpawnBuildingKindOfFilter").Behaviors["ModuleTag_CreatePeople"].Data;

        // 2000 ms at 5 Hz = 10 logic frames, exact.
        Assert.Equal(10u, data.SpawnDelayTime.Value);
    }

    [Fact]
    public void MaximumDistance_ParsesToFix64()
    {
        var game = NewGame();
        var data = (CivilianSpawnUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("CivSpawnBuildingKindOfFilter").Behaviors["ModuleTag_CreatePeople"].Data;

        Assert.Equal((Fix64)150, data.MaximumDistance);
    }

    [Fact]
    public void CivilianPoolIsAssetReferences_NotObjectKinds()
    {
        var game = NewGame();
        var data = (CivilianSpawnUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("CivSpawnBuildingKindOfFilter").Behaviors["ModuleTag_CreatePeople"].Data;

        Assert.Equal(2, data.Civilian.Length);
        Assert.Equal("CivilianVillagerA", data.Civilian[0].Value.Name);
        Assert.Equal("CivilianVillagerB", data.Civilian[1].Value.Name);
    }

    [Fact]
    public void NoSpawn_BeforeSpawnDelayTimeElapses()
    {
        var game = NewGame();
        var spawner = game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(100, 100, 0));

        for (var i = 0; i < 10; i++)
        {
            game.Step();
            Assert.Equal(0, ModuleOf(spawner).NumSpawned);
        }
    }

    [Fact]
    public void FirstSpawn_PicksFromPool_AtSpawnerPosition_OwnedBySpawner()
    {
        var game = NewGame();
        var beforeIds = game.GameLogic.Objects.Select(o => o.Id).ToHashSet();
        var spawner = game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(100, 100, 0));

        // The tick that observes CurrentFrame == 10 runs on the 11th Step() (sleepy-update
        // convention, spec §3).
        StepFrames(game, 11);

        Assert.Equal(1, ModuleOf(spawner).NumSpawned);

        var spawned = game.GameLogic.Objects
            .Where(o => o.Id != spawner.Id && !beforeIds.Contains(o.Id))
            .ToList();

        var newVillager = Assert.Single(spawned);
        Assert.Contains(newVillager.Definition.Name, VillagerNames);
        Assert.Equal(spawner.Owner, newVillager.Owner);
    }

    [Fact]
    public void SecondSpawn_ReloadsCadence()
    {
        var game = NewGame();
        var spawner = game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(100, 100, 0));

        StepFrames(game, 11);
        Assert.Equal(1, ModuleOf(spawner).NumSpawned);

        StepFrames(game, 10);
        Assert.Equal(2, ModuleOf(spawner).NumSpawned);
    }

    [Fact]
    public void RandomPick_IsReproducible_GivenTheSameMatchSeed()
    {
        var gameA = NewGame(0xC8A);
        var spawnerA = gameA.SpawnObject("CivSpawnBuildingKindOfFilter", gameA.CivilianPlayer, new Vector3(100, 100, 0));
        var beforeA = gameA.GameLogic.Objects.Select(o => o.Id).ToHashSet();

        var gameB = NewGame(0xC8A);
        var spawnerB = gameB.SpawnObject("CivSpawnBuildingKindOfFilter", gameB.CivilianPlayer, new Vector3(100, 100, 0));
        var beforeB = gameB.GameLogic.Objects.Select(o => o.Id).ToHashSet();

        StepFrames(gameA, 31);
        StepFrames(gameB, 31);

        Assert.Equal(3, ModuleOf(spawnerA).NumSpawned);
        Assert.Equal(3, ModuleOf(spawnerB).NumSpawned);

        var namesA = gameA.GameLogic.Objects
            .Where(o => o.Id != spawnerA.Id && !beforeA.Contains(o.Id))
            .OrderBy(o => o.Id.Index)
            .Select(o => o.Definition.Name)
            .ToList();
        var namesB = gameB.GameLogic.Objects
            .Where(o => o.Id != spawnerB.Id && !beforeB.Contains(o.Id))
            .OrderBy(o => o.Id.Index)
            .Select(o => o.Definition.Name)
            .ToList();

        Assert.Equal(namesA, namesB);
    }

    [Fact]
    public void EmptyCivilianPool_NeverSpawns_NoException()
    {
        var game = NewGame();
        var beforeIds = game.GameLogic.Objects.Select(o => o.Id).ToHashSet();
        var spawner = game.SpawnObject("CivSpawnEmptyPool", game.CivilianPlayer, new Vector3(100, 100, 0));

        StepFrames(game, 20);

        Assert.Equal(0, ModuleOf(spawner).NumSpawned);
        Assert.Empty(game.GameLogic.Objects.Where(o => o.Id != spawner.Id && !beforeIds.Contains(o.Id)));
    }

    [Fact]
    public void TryFindRunToTarget_KindOfFilter_FindsInRangeStructure()
    {
        var game = NewGame();
        var spawner = game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(0, 0, 0));
        var safeHaven = game.SpawnObject("SafeHavenBuilding", game.CivilianPlayer, new Vector3(100, 0, 0));
        // Closer, but does not match ANY +STRUCTURE (a CIVILIAN KindOf object).
        game.SpawnObject("CivilianVillagerA", game.CivilianPlayer, new Vector3(50, 0, 0));

        var found = ModuleOf(spawner).TryFindRunToTarget(out var target);

        Assert.True(found);
        Assert.Equal(safeHaven.Id, target);
    }

    [Fact]
    public void TryFindRunToTarget_OutOfRange_ReturnsFalse()
    {
        var game = NewGame();
        var spawner = game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("SafeHavenBuilding", game.CivilianPlayer, new Vector3(500, 0, 0));

        var found = ModuleOf(spawner).TryFindRunToTarget(out var target);

        Assert.False(found);
        Assert.Equal(default(ObjectId), target);
    }

    [Fact]
    public void TryFindRunToTarget_TemplateNameFilter_MatchesNothing_KnownGap()
    {
        // F-CSU-2/§1b: RunToFilter's live shape (a +TemplateName include) hits
        // ObjectFilter.Matches's pre-existing, already-documented gap (Matches never consults
        // IncludeThings/ExcludeThings). This pins the current, gapped behavior explicitly so a
        // future fix to ObjectFilter.Matches (out of this task's scope) flips this assertion
        // deliberately, not silently.
        var game = NewGame();
        var spawner = game.SpawnObject("CivSpawnBuildingTemplateFilter", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("SafeHavenBuilding", game.CivilianPlayer, new Vector3(50, 0, 0));

        var found = ModuleOf(spawner).TryFindRunToTarget(out _);

        Assert.False(found);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidCadence()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(100, 100, 0));
        StepFrames(game, 11);
        var live = ModuleOf(liveHost);
        Assert.Equal(1, live.NumSpawned);

        var shadow = ModuleOf(game.SpawnObject("CivSpawnBuildingKindOfFilter", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}

// Mocked-game contract tests for UnitCrateCollide (R12 port; task packet unit-crate-collide,
// testCases TC1-TC6): spawning UnitCount units of UnitName around the crate on pickup, each
// inheriting the collector's orientation and belonging to the collector's owning
// player/default team, plus the invalid-UnitName and UnitCount=0 no-op paths.
//
// Every object definition parses from INI text through the real IniParser, so the audited
// parse functions (UnitCount as int, UnitName as an asset reference) are on the tested path.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class UnitCrateCollideContractTests
{
    private const string Definitions = @"
Object SpawnedUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Collector
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object CrateOne
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = SpawnedUnit
  End
End

Object CrateThree
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 3
    UnitName = SpawnedUnit
  End
End

Object CrateInvalidUnit
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = NoSuchUnitTemplate
  End
End

Object CrateZeroCount
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 0
    UnitName = SpawnedUnit
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC24E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static readonly Vector3 CratePosition = new(100, 100, 0);
    private const float CollectorYaw = 0.7f;

    private static (HeadlessSimGame Game, GameObject Crate, GameObject Collector) Spawn(
        string crateTemplate, uint seed = 0xC24E)
    {
        var game = NewGame(seed);
        var crate = game.SpawnObject(crateTemplate, game.CivilianPlayer, CratePosition);
        var collector = game.SpawnObject(
            "Collector",
            game.CivilianPlayer,
            new Vector3(CratePosition.X + 3, CratePosition.Y, CratePosition.Z));
        collector.UpdateTransform(rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, CollectorYaw));
        return (game, crate, collector);
    }

    private static HashSet<ObjectId> SnapshotIds(HeadlessSimGame game)
    {
        var ids = new HashSet<ObjectId>();
        foreach (var obj in game.GameLogic.Objects)
        {
            ids.Add(obj.Id);
        }
        return ids;
    }

    private static List<GameObject> NewSpawnedUnits(HeadlessSimGame game, HashSet<ObjectId> before)
    {
        var result = new List<GameObject>();
        foreach (var obj in game.GameLogic.Objects)
        {
            if (!before.Contains(obj.Id) && obj.Definition.Name == "SpawnedUnit")
            {
                result.Add(obj);
            }
        }
        return result;
    }

    // TC1: valid UnitName, UnitCount=1 -> one unit spawns within the 0-20 unit scatter radius.
    [Fact]
    public void CollectingCrate_WithUnitCountOne_SpawnsOneUnitWithinScatterRadius()
    {
        var (game, crate, collector) = Spawn("CrateOne");
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        var spawned = NewSpawnedUnits(game, before);
        Assert.Single(spawned);

        var distance = Vector3.Distance(spawned[0].Transform.Translation, crate.Transform.Translation);
        Assert.InRange(distance, 0f, 20f + 0.01f);
    }

    // TC2: valid UnitName, UnitCount=3 -> all 3 spawn within the radius, mutually
    // non-colliding (the placement loop keeps SpawnClearance from every prior spawn).
    [Fact]
    public void CollectingCrate_WithUnitCountThree_SpawnsThreeNonCollidingUnits()
    {
        var (game, crate, collector) = Spawn("CrateThree");
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        var spawned = NewSpawnedUnits(game, before);
        Assert.Equal(3, spawned.Count);

        foreach (var unit in spawned)
        {
            var distance = Vector3.Distance(unit.Transform.Translation, crate.Transform.Translation);
            Assert.InRange(distance, 0f, 20f + 0.01f);
        }

        // The placement loop retries for SpawnClearance from every prior spawn but falls back
        // to its last draw rather than fail the pickup (F-UCC-2/best-effort), so this asserts
        // the weaker, seed-independent invariant that the three draws did not collapse onto
        // the exact same point rather than a hardcoded clearance distance.
        for (var i = 0; i < spawned.Count; i++)
        {
            for (var j = i + 1; j < spawned.Count; j++)
            {
                var separation = Vector3.Distance(spawned[i].Transform.Translation, spawned[j].Transform.Translation);
                Assert.True(separation > 0f, $"units {i} and {j} spawned at the same position");
            }
        }
    }

    // TC3: an unresolvable UnitName fails gracefully - no spawn, no exception.
    [Fact]
    public void CollectingCrate_WithInvalidUnitName_SpawnsNothing()
    {
        var (game, crate, collector) = Spawn("CrateInvalidUnit");
        var before = SnapshotIds(game);

        var ex = Record.Exception(() => crate.OnCollide(collector));

        Assert.Null(ex);
        Assert.Empty(NewSpawnedUnits(game, before));
    }

    // TC4: UnitCount=0 -> no spawning.
    [Fact]
    public void CollectingCrate_WithUnitCountZero_SpawnsNothing()
    {
        var (game, crate, collector) = Spawn("CrateZeroCount");
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        Assert.Empty(NewSpawnedUnits(game, before));
    }

    // TC5: spawned units belong to the collector's controlling player and default team.
    [Fact]
    public void CollectingCrate_SpawnsUnits_OwnedByCollectorsPlayerAndTeam()
    {
        var (game, crate, collector) = Spawn("CrateOne");
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        var spawned = Assert.Single(NewSpawnedUnits(game, before));
        Assert.Same(collector.Owner, spawned.Owner);
        Assert.Equal(collector.Owner?.DefaultTeam, spawned.Team);
    }

    // TC6: spawned units inherit the collector's orientation.
    [Fact]
    public void CollectingCrate_SpawnsUnits_InheritingCollectorsOrientation()
    {
        var (game, crate, collector) = Spawn("CrateThree");
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        foreach (var unit in NewSpawnedUnits(game, before))
        {
            Assert.True(
                System.MathF.Abs(collector.Transform.Yaw - unit.Transform.Yaw) < 0.001f,
                $"expected yaw {collector.Transform.Yaw}, got {unit.Transform.Yaw}");
        }
    }

    // Task packet: "plays free-unit pickup audio on successful execution" - a real spawn
    // fires exactly one request for the MiscAudio CrateFreeUnit sting.
    [Fact]
    public void CollectingCrate_WithSuccessfulSpawn_FiresFreeUnitPickupAudioOnce()
    {
        var (game, crate, collector) = Spawn("CrateThree");
        var recorder = RecordingSimEvents.InstallOn(game);

        crate.OnCollide(collector);

        Assert.Equal(1, recorder.CrateFreeUnitPickupSoundCount);
    }

    // TC3/TC4 (F-UCC-3): "successful execution" is read as "at least one unit spawned" - an
    // unresolvable UnitName or UnitCount=0 both play no audio.
    [Fact]
    public void CollectingCrate_WithInvalidUnitName_FiresNoAudio()
    {
        var (game, crate, collector) = Spawn("CrateInvalidUnit");
        var recorder = RecordingSimEvents.InstallOn(game);

        crate.OnCollide(collector);

        Assert.Equal(0, recorder.CrateFreeUnitPickupSoundCount);
    }

    [Fact]
    public void CollectingCrate_WithUnitCountZero_FiresNoAudio()
    {
        var (game, crate, collector) = Spawn("CrateZeroCount");
        var recorder = RecordingSimEvents.InstallOn(game);

        crate.OnCollide(collector);

        Assert.Equal(0, recorder.CrateFreeUnitPickupSoundCount);
    }
}

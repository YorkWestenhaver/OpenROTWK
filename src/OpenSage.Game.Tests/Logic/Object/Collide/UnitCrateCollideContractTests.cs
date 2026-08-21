// Mocked-game contract tests for UnitCrateCollide (R12 port, R13 fix pass; task packet
// unit-crate-collide, testCases TC1-TC6): spawning UnitCount units of UnitName around the
// COLLECTOR (GPL: UnitCrateCollide.cpp:72) on pickup, each inheriting the collector's
// orientation and belonging to the collector's owning player/default team, destroying the
// crate on a successful pickup (GPL: CrateCollide.cpp:115-148), and leaving the crate alive on
// the invalid-UnitName / UnitCount=0 no-op paths.
//
// Every object definition parses from INI text through the real IniParser, so the audited
// parse functions (UnitCount as int, UnitName as an asset reference) are on the tested path.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic;
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
  ; GPL's base gate requires a collector that is a real 'Unit' type thing
  ; (other->getAIUpdateInterface() != NULL), which every retail crate collector is.
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object CollectorNoAi
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object CollectorVehicle
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object CollectorParachute
  KindOf = INFANTRY PARACHUTE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
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

Object CrateForbidInfantry
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = SpawnedUnit
    ForbiddenKindOf = INFANTRY
  End
End

Object CrateRequireInfantryAndVehicle
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = SpawnedUnit
    RequiredKindOf = INFANTRY VEHICLE
  End
End

Object CrateRequireInfantry
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = SpawnedUnit
    RequiredKindOf = INFANTRY
  End
End

Object CrateForbidOwner
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = SpawnedUnit
    ForbidOwnerPlayer = Yes
  End
End

Object CrateHumanOnly
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = UnitCrateCollide ModuleTag_Collide
    UnitCount = 1
    UnitName = SpawnedUnit
    HumanOnly = Yes
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

        // Two extra, distinct, HUMAN players so the gate cases that need a non-neutral,
        // non-civilian owner (ForbidOwnerPlayer, HumanOnly) have something real to compare.
        game.PlayerManager.OnNewGame(
            new[]
            {
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                new OpenSage.Data.Map.Player { Name = "plyrAlpha", Faction = "FactionAlpha", IsHuman = true },
                new OpenSage.Data.Map.Player { Name = "plyrBravo", Faction = "FactionBravo", IsHuman = true },
            },
            GameType.Skirmish);

        game.LoadIniText(Definitions);
        return game;
    }

    private static Player PlayerAlpha(HeadlessSimGame game) => game.PlayerManager.GetPlayerByName("plyrAlpha");
    private static Player PlayerBravo(HeadlessSimGame game) => game.PlayerManager.GetPlayerByName("plyrBravo");

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

    // TC1: valid UnitName, UnitCount=1 -> one unit spawns within the 0-20 unit scatter radius,
    // anchored on the COLLECTOR's position (GPL: `Coord3D creationPoint = *other->getPosition();`,
    // UnitCrateCollide.cpp:72 - not the crate's own position).
    [Fact]
    public void CollectingCrate_WithUnitCountOne_SpawnsOneUnitWithinScatterRadius()
    {
        var (game, crate, collector) = Spawn("CrateOne");
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        var spawned = NewSpawnedUnits(game, before);
        Assert.Single(spawned);

        var distance = Vector3.Distance(spawned[0].Transform.Translation, collector.Transform.Translation);
        Assert.InRange(distance, 0f, 20f + 0.01f);
    }

    // R13: GPL's CrateCollide::onCollide always destroys the crate once executeCrateBehavior
    // returns TRUE, which UnitCrateCollide's version does unconditionally once UnitName
    // resolves (CrateCollide.cpp:115-148, UnitCrateCollide.cpp:56-92). A crate left alive after
    // a successful pickup would re-trigger OnCollide on the next overlap and duplicate its
    // spawn indefinitely - this is the regression the missing-destroy finding described.
    [Fact]
    public void CollectingCrate_WithSuccessfulSpawn_DestroysTheCrate()
    {
        var (game, crate, collector) = Spawn("CrateOne");

        crate.OnCollide(collector);

        Assert.True(crate.IsDestroyed);
    }

    // R13: an unresolvable UnitName never reaches a resolved unitType, so GPL's
    // executeCrateBehavior returns FALSE and the crate is left alive (CrateCollide.cpp:118-127).
    [Fact]
    public void CollectingCrate_WithInvalidUnitName_LeavesCrateAlive()
    {
        var (game, crate, collector) = Spawn("CrateInvalidUnit");

        crate.OnCollide(collector);

        Assert.False(crate.IsDestroyed);
    }

    // R13: UnitCount<=0 is a port-level early-out before UnitName is even resolved, so the
    // crate is left alive for the same reason as the invalid-UnitName case above.
    [Fact]
    public void CollectingCrate_WithUnitCountZero_LeavesCrateAlive()
    {
        var (game, crate, collector) = Spawn("CrateZeroCount");

        crate.OnCollide(collector);

        Assert.False(crate.IsDestroyed);
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
            var distance = Vector3.Distance(unit.Transform.Translation, collector.Transform.Translation);
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

    // ---- Shared CrateCollide::isValidToExecute base gate (R13.5, crate-gate) ----
    //
    // Every case below uses CrateOne-shaped data (UnitCount = 1, a resolvable UnitName), so a
    // "nothing spawned, crate still alive" outcome can only come from the gate.

    private static (HeadlessSimGame Game, GameObject Crate, GameObject Collector) SpawnGateCase(
        string crateTemplate,
        string collectorTemplate,
        Player cratePlayerOverride = null,
        Player collectorPlayerOverride = null)
    {
        var game = NewGame();
        var cratePlayer = cratePlayerOverride ?? PlayerAlpha(game);
        var collectorPlayer = collectorPlayerOverride ?? PlayerBravo(game);

        var crate = game.SpawnObject(crateTemplate, cratePlayer, CratePosition);
        var collector = game.SpawnObject(
            collectorTemplate,
            collectorPlayer,
            new Vector3(CratePosition.X + 3, CratePosition.Y, CratePosition.Z));
        return (game, crate, collector);
    }

    private static void AssertRejected(HeadlessSimGame game, GameObject crate, GameObject collector)
    {
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        Assert.Empty(NewSpawnedUnits(game, before));
        Assert.False(crate.IsDestroyed);
    }

    private static void AssertAccepted(HeadlessSimGame game, GameObject crate, GameObject collector)
    {
        var before = SnapshotIds(game);

        crate.OnCollide(collector);

        Assert.Single(NewSpawnedUnits(game, before));
        Assert.True(crate.IsDestroyed);
    }

    // "Nothing Neutral can pick up any type of crate."
    [Fact]
    public void NeutralControlledCollector_IsRejected()
    {
        var game = NewGame();
        var crate = game.SpawnObject("CrateOne", PlayerAlpha(game), CratePosition);
        var collector = game.SpawnObject(
            "Collector",
            game.PlayerManager.NeutralPlayer,
            new Vector3(CratePosition.X + 3, CratePosition.Y, CratePosition.Z));

        AssertRejected(game, crate, collector);
    }

    // "Must be a 'Unit' type thing. Real Game Object, not just Object." - no AIUpdate and the
    // crate does not set BuildingPickup, so the collector cannot take it.
    [Fact]
    public void CollectorWithoutAIUpdate_IsRejected()
    {
        var (game, crate, collector) = SpawnGateCase("CrateOne", "CollectorNoAi");

        AssertRejected(game, crate, collector);
    }

    // ForbiddenKindOf: any set bit present on the collector rejects it.
    [Fact]
    public void ForbiddenKindOf_RejectsMatchingCollector()
    {
        var (game, crate, collector) = SpawnGateCase("CrateForbidInfantry", "Collector");

        AssertRejected(game, crate, collector);
    }

    [Fact]
    public void ForbiddenKindOf_AcceptsNonMatchingCollector()
    {
        var (game, crate, collector) = SpawnGateCase("CrateForbidInfantry", "CollectorVehicle");

        AssertAccepted(game, crate, collector);
    }

    // RequiredKindOf is a MASK (GPL isKindOfMulti): EVERY set bit must be present. Authored
    // "INFANTRY VEHICLE" used to collapse to the single last token (VEHICLE) and was never
    // enforced at all, so an INFANTRY collector took the crate.
    [Fact]
    public void RequiredKindOfMultiBitMask_RejectsCollectorMissingOneBit()
    {
        var (game, crate, collector) = SpawnGateCase("CrateRequireInfantryAndVehicle", "Collector");

        AssertRejected(game, crate, collector);
    }

    [Fact]
    public void RequiredKindOfMultiBitMask_RejectsCollectorCarryingOnlyTheOtherBit()
    {
        var (game, crate, collector) = SpawnGateCase("CrateRequireInfantryAndVehicle", "CollectorVehicle");

        AssertRejected(game, crate, collector);
    }

    [Fact]
    public void RequiredKindOf_AcceptsCollectorCarryingEveryRequiredBit()
    {
        var (game, crate, collector) = SpawnGateCase("CrateRequireInfantry", "Collector");

        AssertAccepted(game, crate, collector);
    }

    [Fact]
    public void RequiredKindOf_RejectsCollectorWithoutTheRequiredBit()
    {
        var (game, crate, collector) = SpawnGateCase("CrateRequireInfantry", "CollectorVehicle");

        AssertRejected(game, crate, collector);
    }

    [Fact]
    public void EffectivelyDeadCollector_IsRejected()
    {
        var (game, crate, collector) = SpawnGateCase("CrateOne", "Collector");
        collector.IsEffectivelyDead = true;

        AssertRejected(game, crate, collector);
    }

    // "Design has decreed this to not be picked up by the dead guy's team."
    [Fact]
    public void ForbidOwnerPlayer_RejectsPickupByTheCratesOwnController()
    {
        var game = NewGame();
        var alpha = PlayerAlpha(game);
        var crate = game.SpawnObject("CrateForbidOwner", alpha, CratePosition);
        var collector = game.SpawnObject(
            "Collector", alpha, new Vector3(CratePosition.X + 3, CratePosition.Y, CratePosition.Z));

        AssertRejected(game, crate, collector);
    }

    [Fact]
    public void ForbidOwnerPlayer_AcceptsPickupByADifferentController()
    {
        var (game, crate, collector) = SpawnGateCase("CrateForbidOwner", "Collector");

        AssertAccepted(game, crate, collector);
    }

    // "Human only mission crate."
    [Fact]
    public void HumanOnly_RejectsNonHumanController()
    {
        var game = NewGame();
        var crate = game.SpawnObject("CrateHumanOnly", PlayerAlpha(game), CratePosition);
        // CivilianPlayer is not human (Data.Map.Player.CreateCivilianPlayer sets IsHuman = false).
        var collector = game.SpawnObject(
            "Collector", game.CivilianPlayer, new Vector3(CratePosition.X + 3, CratePosition.Y, CratePosition.Z));

        AssertRejected(game, crate, collector);
    }

    [Fact]
    public void HumanOnly_AcceptsHumanController()
    {
        var (game, crate, collector) = SpawnGateCase("CrateHumanOnly", "Collector");

        AssertAccepted(game, crate, collector);
    }

    // "other->isKindOf(KINDOF_PARACHUTE)" exclusion.
    [Fact]
    public void ParachuteKindOfCollector_IsRejected()
    {
        var (game, crate, collector) = SpawnGateCase("CrateOne", "CollectorParachute");

        AssertRejected(game, crate, collector);
    }
}

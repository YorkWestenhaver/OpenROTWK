// S3 system tests (build-roadmap pillar partition-vision): the deterministic partition
// grid — cell mapping, coverage, the getObjectsInRange query family, the shroud ledger
// algorithms, the look/unlook vision model with the timed undo queue, whole-object
// shroud status incl. the fog-memory rules, terrain LOS, and the Xfer/CRC walk with a
// mid-state save/load continuation. Plus one HeadlessSimGame test driving the F4
// quantizing bridge over a real parsed GameObject.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Partition;

public class PartitionVisionSystemTests
{
    // ---- test roster: 0 = neutral, 1 = "us", 2 = enemy of 1, 3 = ally of 1 ----
    private sealed class TestPlayers : IPartitionPlayerView
    {
        public int PlayerCount => 4;

        public uint GetLookerMask(int ownerPlayerIndex) => ownerPlayerIndex switch
        {
            1 => (1u << 1) | (1u << 3),
            3 => (1u << 1) | (1u << 3),
            _ => 1u << ownerPlayerIndex,
        };

        public uint GetEnemyAndNeutralMask(int ownerPlayerIndex) => ownerPlayerIndex switch
        {
            1 => (1u << 0) | (1u << 2),
            3 => (1u << 0) | (1u << 2),
            2 => (1u << 0) | (1u << 1) | (1u << 3),
            _ => ((1u << PlayerCount) - 1) & ~(1u << ownerPlayerIndex),
        };

        public RelationshipType GetRelationship(int viewer, int owner)
        {
            if (viewer == owner)
            {
                return RelationshipType.Allies;
            }
            if ((viewer == 1 && owner == 3) || (viewer == 3 && owner == 1))
            {
                return RelationshipType.Allies;
            }
            if (viewer == 0 || owner == 0)
            {
                return RelationshipType.Neutral;
            }
            return RelationshipType.Enemies;
        }
    }

    private static SimPartitionGrid NewGrid(int widthCells = 20, int heightCells = 20, int cellSize = 10)
        => new(
            Fix64.Zero,
            Fix64.Zero,
            new Fix64(widthCells * cellSize),
            new Fix64(heightCells * cellSize),
            new Fix64(cellSize),
            new TestPlayers());

    private static FixVector3 V(int x, int y, int z = 0) => new(new Fix64(x), new Fix64(y), new Fix64(z));

    private static PartitionObjectInfo Info(
        uint id, int radius = 1, int owner = 1, bool immobile = false, bool mine = false, int height = 10)
        => new(new ObjectId(id), new Fix64(radius), new Fix64(height), owner,
            isImmobile: immobile, isMine: mine);

    private static SimPartitionEntry Add(
        SimPartitionGrid grid, uint id, int x, int y, int z = 0, int radius = 1, int owner = 1,
        int visionRange = 0, bool immobile = false, bool mine = false)
        => grid.Register(
            Info(id, radius, owner, immobile, mine),
            V(x, y, z),
            new Fix64(visionRange),
            LogicFrame.Zero);

    // ------------------------------------------------------------------
    // Cell mapping and coverage
    // ------------------------------------------------------------------

    [Fact]
    public void WorldToCell_FloorsAgainstWorldOrigin()
    {
        var grid = NewGrid();
        grid.WorldToCell(new Fix64(25), new Fix64(199), out var cx, out var cy);
        Assert.Equal(2, cx);
        Assert.Equal(19, cy);
        Assert.Equal(3, grid.WorldToCellDist(new Fix64(25)));   // ceil(25/10)
        Assert.Equal(2, grid.WorldToCellDist(new Fix64(20)));   // exact multiple stays
    }

    [Fact]
    public void SmallObject_CoversTheCellsUnderItsExtentBox()
    {
        var grid = NewGrid();
        var centered = Add(grid, 1, 55, 55, radius: 2);       // strictly inside cell (5,5)
        var straddling = Add(grid, 2, 60, 60, radius: 2);     // corner of 4 cells
        Assert.Single(centered.CoveredCells);
        Assert.Equal(4, straddling.CoveredCells.Count);
    }

    [Fact]
    public void LargeObject_CoversTheFilledDiscreteCircle()
    {
        var grid = NewGrid();
        var big = Add(grid, 1, 100, 100, radius: 25);         // 3-cell radius circle
        // The filled discrete circle of radius 3 covers more than a 3x3 box and is
        // symmetric around the center cell.
        Assert.True(big.CoveredCells.Count > 9);
        grid.WorldToCell(new Fix64(100), new Fix64(100), out var ccx, out var ccy);
        Assert.Contains(ccy * grid.CellCountX + ccx, big.CoveredCells);
    }

    [Fact]
    public void DiscreteCellCircle_EmitsSymmetricRows()
    {
        var rows = new List<(int X1, int X2, int Y)>();
        DiscreteCellCircle.Draw(10, 10, 3, (x1, x2, y) => rows.Add((x1, x2, y)));
        // Every row above the center has its mirror below, with identical x extent.
        foreach (var (x1, x2, y) in rows)
        {
            if (y != 10)
            {
                Assert.Contains((x1, x2, 20 - y), rows);
            }
        }
        // Center row emitted exactly once.
        Assert.Single(rows.FindAll(r => r.Y == 10));
    }

    // ------------------------------------------------------------------
    // The query family
    // ------------------------------------------------------------------

    [Fact]
    public void QueryObjectsInRange_IsStrictAndAscendingObjectId()
    {
        var grid = NewGrid();
        var center = Add(grid, 10, 100, 100);
        Add(grid, 7, 130, 100);    // dist 30 - inside
        Add(grid, 3, 120, 100);    // dist 20 - inside (registered after id 7)
        Add(grid, 5, 140, 100);    // dist 40 - exactly maxDist: STRICTLY excluded (GPL <)
        Add(grid, 9, 190, 100);    // dist 90 - outside

        var results = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(40), PartitionDistanceType.Center2D, results);

        Assert.Equal(2, results.Count);
        Assert.Equal(3u, results[0].Id.Index);   // ascending ObjectId, not insertion order
        Assert.Equal(7u, results[1].Id.Index);
    }

    [Fact]
    public void QueryObjectsInRange_PositionCentered_ExcludesNobody()
    {
        var grid = NewGrid();
        Add(grid, 1, 100, 100);
        Add(grid, 2, 110, 100);

        var results = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(V(100, 100), new Fix64(15), PartitionDistanceType.Center2D, results);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Query_2DIgnoresHeight_3DDoesNot()
    {
        var grid = NewGrid();
        var center = Add(grid, 1, 100, 100);
        Add(grid, 2, 110, 100, z: 100);  // 10 away in 2D, ~100 in 3D

        var results2D = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(20), PartitionDistanceType.Center2D, results2D);
        Assert.Single(results2D);

        var results3D = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(20), PartitionDistanceType.Center3D, results3D);
        Assert.Empty(results3D);
    }

    [Fact]
    public void BoundingSphereMeasure_ShrinksByBothRadii()
    {
        var grid = NewGrid();
        var center = Add(grid, 1, 100, 100, radius: 8);
        Add(grid, 2, 150, 100, radius: 8);   // centers 50 apart, edges 34 apart

        var byCenter = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(40), PartitionDistanceType.Center2D, byCenter);
        Assert.Empty(byCenter);

        var byEdge = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(40), PartitionDistanceType.BoundingSphere2D, byEdge);
        Assert.Single(byEdge);
    }

    [Fact]
    public void GetClosestObject_PicksNearest_TieBreaksToLowerId()
    {
        var grid = NewGrid();
        var center = Add(grid, 1, 100, 100);
        Add(grid, 5, 130, 100);              // dist 30
        Add(grid, 4, 100, 120);              // dist 20 - nearest
        var closest = grid.GetClosestObject(center, new Fix64(50), PartitionDistanceType.Center2D);
        Assert.Equal(4u, closest.Id.Index);

        // Tie: two objects at identical distance - lower ObjectId wins (pinned).
        Add(grid, 9, 100, 80);               // dist 20, same as id 4
        var tie = grid.GetClosestObject(center, new Fix64(50), PartitionDistanceType.Center2D);
        Assert.Equal(4u, tie.Id.Index);
    }

    [Fact]
    public void QueryFilter_IsApplied()
    {
        var grid = NewGrid();
        var center = Add(grid, 1, 100, 100);
        Add(grid, 2, 110, 100, owner: 2);
        Add(grid, 3, 120, 100, owner: 1);

        var results = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(50), PartitionDistanceType.Center2D, results,
            static e => e.Info.OwnerPlayerIndex == 2);
        Assert.Single(results);
        Assert.Equal(2u, results[0].Id.Index);
    }

    [Fact]
    public void Query_HugeObjectStraddlingManyCells_ReturnedOnce()
    {
        var grid = NewGrid();
        var center = Add(grid, 1, 100, 100);
        Add(grid, 2, 120, 100, radius: 45);  // covers many cells around the center

        var results = new List<SimPartitionEntry>();
        grid.QueryObjectsInRange(center, new Fix64(60), PartitionDistanceType.Center2D, results);
        Assert.Single(results);              // done-stamp dedupe, GPL doneFlag semantics
    }

    // ------------------------------------------------------------------
    // The shroud ledger
    // ------------------------------------------------------------------

    [Fact]
    public void FreshMap_IsShroudedForEveryone()
    {
        var grid = NewGrid();
        for (var p = 0; p < 4; p++)
        {
            Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(p, new Fix64(5), new Fix64(5)));
        }
        // Off the map is always shrouded.
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(-50), new Fix64(5)));
    }

    [Fact]
    public void ShroudReveal_ClearsTheCircle_UndoLeavesFog()
    {
        var grid = NewGrid();
        grid.DoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);

        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(120), new Fix64(100)));
        // Well outside the circle: untouched.
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(180), new Fix64(100)));
        // Other players see nothing.
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(2, new Fix64(100), new Fix64(100)));

        grid.UndoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        // Explored, nobody looking: fogged - the "explored map stays explored" rule.
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
    }

    [Fact]
    public void Lookers_AreRefcounted()
    {
        var grid = NewGrid();
        grid.DoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        grid.DoShroudReveal(new Fix64(110), new Fix64(100), new Fix64(30), 1u << 1);

        grid.UndoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        // The overlap still has the second looker.
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(105), new Fix64(100)));

        grid.UndoShroudReveal(new Fix64(110), new Fix64(100), new Fix64(30), 1u << 1);
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(105), new Fix64(100)));
    }

    [Fact]
    public void ShroudGeneration_ReshroudsFoggedCells_ButNotWatchedOnes()
    {
        var grid = NewGrid();
        // Explore an area, then stop looking: fogged.
        grid.DoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        grid.UndoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));

        // An active shrouder re-shrouds the fogged cell...
        grid.DoShroudCover(new Fix64(100), new Fix64(100), new Fix64(20), 1u << 1);
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));

        // ...and removing it does NOT re-clear (GPL: decrement only, no status change).
        grid.UndoShroudCover(new Fix64(100), new Fix64(100), new Fix64(20), 1u << 1);
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));

        // A watched cell shrugs off shroud generation.
        grid.DoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
        grid.DoShroudCover(new Fix64(100), new Fix64(100), new Fix64(20), 1u << 1);
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
        // But when the looker leaves, the active shroud claims the cell immediately.
        grid.UndoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(30), 1u << 1);
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
    }

    [Fact]
    public void MapVerbs_RevealAndReshroud()
    {
        var grid = NewGrid();
        grid.RevealMapForPlayer(1);
        // One-shot reveal: everything at least explored (fogged), nothing watched.
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(5), new Fix64(5)));

        grid.RevealMapForPlayerPermanently(1);
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(5), new Fix64(5)));

        grid.UndoRevealMapForPlayerPermanently(1);
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(5), new Fix64(5)));

        grid.ShroudMapForPlayer(1);
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(5), new Fix64(5)));
    }

    // ------------------------------------------------------------------
    // The vision model (look/unlook + the timed undo queue)
    // ------------------------------------------------------------------

    [Fact]
    public void RegisteredLooker_RevealsForSelfAndAllies_NotEnemies()
    {
        var grid = NewGrid();
        Add(grid, 1, 100, 100, owner: 1, visionRange: 30);

        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(3, new Fix64(100), new Fix64(100)));  // ally
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(2, new Fix64(100), new Fix64(100)));
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(0, new Fix64(100), new Fix64(100)));
    }

    [Fact]
    public void Unregister_FogArrivesOnlyAfterThePersistWindow()
    {
        var grid = NewGrid();
        var scout = Add(grid, 1, 100, 100, visionRange: 30);

        var unregisterFrame = new LogicFrame(10);
        grid.Unregister(scout, unregisterFrame);

        // Still clear: the undo is queued, due at frame 15 (5-frame persist).
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));

        grid.Update(new LogicFrame(15));   // due(15) < now(15) is false: still revealed
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));

        grid.Update(new LogicFrame(16));   // now the undo pops: fogged, not shrouded
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
    }

    [Fact]
    public void Movement_MovesTheLook_TrailFadesToFog()
    {
        var grid = NewGrid();
        var scout = Add(grid, 1, 30, 100, visionRange: 25);
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(30), new Fix64(100)));
        Assert.Equal(CellShroudStatus.Shrouded, grid.GetCellShroudStatus(1, new Fix64(170), new Fix64(100)));

        grid.UpdatePosition(scout, V(170, 100), new LogicFrame(4));
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(170), new Fix64(100)));
        // Old spot still visible inside the persist window...
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(30), new Fix64(100)));
        // ...then fades to fog.
        grid.Update(new LogicFrame(10));
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(30), new Fix64(100)));
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(170), new Fix64(100)));
    }

    [Fact]
    public void SetCanLook_False_StopsLooking()
    {
        var grid = NewGrid();
        var scout = Add(grid, 1, 100, 100, visionRange: 30);
        grid.SetCanLook(scout, false, new LogicFrame(0));    // died / entered a tunnel
        grid.ProcessEntirePendingUndoShroudRevealQueue();
        Assert.Equal(CellShroudStatus.Fogged, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));

        grid.SetCanLook(scout, true, new LogicFrame(1));
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
    }

    // ------------------------------------------------------------------
    // Whole-object shroud status (fog-memory rules)
    // ------------------------------------------------------------------

    [Fact]
    public void ObjectStatus_ClearWhenSeen_FoggedMemoryOnlyForSeenImmobiles()
    {
        var grid = NewGrid();
        var enemyBuilding = Add(grid, 1, 100, 100, owner: 2, immobile: true);
        var enemyUnit = Add(grid, 2, 110, 100, owner: 2);

        // Never seen: shrouded for player 1.
        Assert.Equal(PartitionObjectShroudStatus.Shrouded, enemyBuilding.GetShroudedStatus(1, grid));

        // Player 1 scouts the area.
        grid.DoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(40), 1u << 1);
        Assert.Equal(PartitionObjectShroudStatus.Clear, enemyBuilding.GetShroudedStatus(1, grid));
        Assert.Equal(PartitionObjectShroudStatus.Clear, enemyUnit.GetShroudedStatus(1, grid));

        // The scout leaves: the building is remembered through the fog, the unit is not.
        grid.UndoShroudReveal(new Fix64(100), new Fix64(100), new Fix64(40), 1u << 1);
        Assert.Equal(PartitionObjectShroudStatus.Fogged, enemyBuilding.GetShroudedStatus(1, grid));
        Assert.Equal(PartitionObjectShroudStatus.Shrouded, enemyUnit.GetShroudedStatus(1, grid));
    }

    [Fact]
    public void ObjectStatus_MinesVanishInFog_NeutralMoversVanishInFog()
    {
        var grid = NewGrid();
        var enemyMine = Add(grid, 1, 100, 100, owner: 2, immobile: true, mine: true);
        var neutralCreep = Add(grid, 2, 110, 100, owner: 0);
        var neutralRock = Add(grid, 3, 120, 100, owner: 0, immobile: true);

        grid.DoShroudReveal(new Fix64(105), new Fix64(100), new Fix64(40), 1u << 1);
        Assert.Equal(PartitionObjectShroudStatus.Clear, enemyMine.GetShroudedStatus(1, grid));
        grid.UndoShroudReveal(new Fix64(105), new Fix64(100), new Fix64(40), 1u << 1);

        Assert.Equal(PartitionObjectShroudStatus.Shrouded, enemyMine.GetShroudedStatus(1, grid));   // mine rule
        Assert.Equal(PartitionObjectShroudStatus.Shrouded, neutralCreep.GetShroudedStatus(1, grid)); // neutral mover
        Assert.Equal(PartitionObjectShroudStatus.Fogged, neutralRock.GetShroudedStatus(1, grid));    // neutral immobile
    }

    [Fact]
    public void ObjectStatus_PartialClear_WhenFootprintStraddlesTheEdge()
    {
        var grid = NewGrid();
        var big = Add(grid, 1, 100, 100, owner: 2, radius: 35);
        // Reveal only part of the footprint.
        grid.DoShroudReveal(new Fix64(70), new Fix64(100), new Fix64(20), 1u << 1);
        Assert.Equal(PartitionObjectShroudStatus.PartialClear, big.GetShroudedStatus(1, grid));
    }

    // ------------------------------------------------------------------
    // Line of sight
    // ------------------------------------------------------------------

    private sealed class RidgeTerrain : ITerrainLogic
    {
        // A 50-high ridge across x in [40, 60]; flat elsewhere.
        public bool IsSignificantlyAboveTerrain(GameObject gameObject) => false;

        public Fix64 GetGroundHeight(in FixVector3 position)
            => position.X >= new Fix64(40) && position.X <= new Fix64(60)
                ? new Fix64(50)
                : Fix64.Zero;
    }

    [Fact]
    public void LineOfSight_BlockedByRidge_ClearOverAndAround()
    {
        var grid = NewGrid();
        var terrain = new RidgeTerrain();

        // Eye-height line at z=10 crossing the ridge: blocked.
        Assert.False(grid.IsClearLineOfSightTerrain(V(5, 105, 10), V(95, 105, 10), terrain));

        // High eyes (z=70) clear the ridge.
        Assert.True(grid.IsClearLineOfSightTerrain(V(5, 105, 70), V(95, 105, 70), terrain));

        // A line that never crosses the ridge is clear at any height.
        Assert.True(grid.IsClearLineOfSightTerrain(V(5, 105, 10), V(35, 105, 10), terrain));

        // Rising line from far away: the ridge's front edge still cuts the sight line
        // to a target on its top (the line is below 50 where it crosses x=40).
        Assert.False(grid.IsClearLineOfSightTerrain(V(5, 105, 10), V(50, 105, 55), terrain));

        // But from the foot of the ridge the top is visible.
        Assert.True(grid.IsClearLineOfSightTerrain(V(35, 105, 10), V(45, 105, 55), terrain));
    }

    [Fact]
    public void EyePosition_AddsGeometryTop()
    {
        var grid = NewGrid();
        var entry = Add(grid, 1, 100, 100, z: 5);
        var eye = SimPartitionGrid.EyePosition(entry);
        Assert.Equal(new Fix64(15), eye.Z);    // z 5 + height-above-position 10
    }

    // ------------------------------------------------------------------
    // Xfer / CRC / determinism
    // ------------------------------------------------------------------

    private static uint GridCrc(SimPartitionGrid grid, params SimPartitionEntry[] entries)
    {
        var crc = new XferCrcVisitor();
        grid.Xfer(crc);
        foreach (var entry in entries)
        {
            entry.Xfer(crc);
        }
        return crc.Value;
    }

    [Fact]
    public void Xfer_MidStateSaveLoad_CrcEqualAndContinuationIdentical()
    {
        // Run A: a scout looks, moves (queued unlook pending), an enemy building has
        // been seen (fog memory armed).
        var gridA = NewGrid();
        var scoutA = Add(gridA, 1, 100, 100, visionRange: 30);
        var houseA = Add(gridA, 2, 120, 100, owner: 2, immobile: true);
        Assert.Equal(PartitionObjectShroudStatus.Clear, houseA.GetShroudedStatus(1, gridA));
        gridA.UpdatePosition(scoutA, V(160, 100), new LogicFrame(3));   // undo queued, due 8

        // Save A.
        var stream = new System.IO.MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            gridA.Xfer(save);
            scoutA.Xfer(save);
            houseA.Xfer(save);
        }

        // Run B: fresh grid, same registrations at the CURRENT positions (the load
        // flow: objects re-register, then the walk overwrites shroud + sighting state).
        var gridB = NewGrid();
        var scoutB = Add(gridB, 1, 160, 100, visionRange: 30);
        var houseB = Add(gridB, 2, 120, 100, owner: 2, immobile: true);
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            gridB.Xfer(load);
            scoutB.Xfer(load);
            houseB.Xfer(load);
        }

        // CRC equality over the same walk.
        Assert.Equal(GridCrc(gridA, scoutA, houseA), GridCrc(gridB, scoutB, houseB));

        // Byte-stable re-save.
        var restream = new System.IO.MemoryStream();
        using (var resave = new XferSave(restream, leaveOpen: true))
        {
            gridB.Xfer(resave);
            scoutB.Xfer(resave);
            houseB.Xfer(resave);
        }
        Assert.Equal(stream.ToArray(), restream.ToArray());

        // Continuation: tick both through the pending-undo window; state stays CRC-equal
        // and the observable statuses agree (the trail fogs, the memory shows the house).
        for (var f = 4u; f <= 12; f++)
        {
            gridA.Update(new LogicFrame(f));
            gridB.Update(new LogicFrame(f));
            Assert.Equal(GridCrc(gridA, scoutA, houseA), GridCrc(gridB, scoutB, houseB));
        }
        Assert.Equal(CellShroudStatus.Fogged, gridA.GetCellShroudStatus(1, new Fix64(100), new Fix64(100)));
        Assert.Equal(
            houseA.GetShroudedStatus(1, gridA),
            houseB.GetShroudedStatus(1, gridB));
        Assert.Equal(PartitionObjectShroudStatus.Fogged, houseB.GetShroudedStatus(1, gridB));
    }

    [Fact]
    public void TwoIdenticalRuns_AreBitIdentical()
    {
        static uint Run()
        {
            var grid = NewGrid();
            var a = Add(grid, 1, 50, 50, visionRange: 40);
            var b = Add(grid, 2, 150, 150, owner: 2, visionRange: 35);
            grid.UpdatePosition(a, V(90, 90), new LogicFrame(2));
            grid.UpdatePosition(b, V(120, 120), new LogicFrame(3));
            for (var f = 3u; f <= 15; f++)
            {
                grid.Update(new LogicFrame(f));
            }
            grid.Unregister(b, new LogicFrame(15));
            for (var f = 16u; f <= 25; f++)
            {
                grid.Update(new LogicFrame(f));
            }
            return GridCrc(grid, a);
        }

        Assert.Equal(Run(), Run());
    }

    // ------------------------------------------------------------------
    // The F4 bridge over a real parsed GameObject
    // ------------------------------------------------------------------

    private const string Definitions = @"
Object ScoutTower
  KindOf = STRUCTURE IMMOBILE
  Geometry = CYLINDER
  GeometryMajorRadius = 8
  GeometryHeight = 40
  VisionRange = 120.0
  ShroudClearingRange = 90.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    [Fact]
    public void Bridge_QuantizesARealGameObjectOnce()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2);
        game.LoadIniText(Definitions);
        var tower = game.SpawnObject("ScoutTower", game.CivilianPlayer, new Vector3(105, 105, 0));

        var grid = NewGrid();
        var entry = SimPartitionBridge.Register(grid, tower, ownerPlayerIndex: 1, LogicFrame.Zero);

        // Quantized exactly through the wire path: 8, 40, 90 and (105,105,0) are all
        // exactly representable, so the Fix64 values are exact integers.
        Assert.Equal(new Fix64(8), entry.Info.BoundingRadius);
        Assert.Equal(new Fix64(40), entry.Info.HeightAbovePosition);
        Assert.Equal(new Fix64(90), entry.ShroudClearingRange);
        Assert.Equal(V(105, 105), entry.Position);
        Assert.True(entry.Info.IsImmobile);

        // And it looks: the tower's cell is clear for its owner.
        Assert.Equal(CellShroudStatus.Clear, grid.GetCellShroudStatus(1, new Fix64(105), new Fix64(105)));

        // Queryable through the public family.
        var found = grid.GetClosestObject(V(100, 100), new Fix64(50), PartitionDistanceType.Center2D);
        Assert.Same(entry, found);
    }
}

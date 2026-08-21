// Contract tests for the S5 pathfinding system (api-freeze-v1 §6 fitness item 4 shape).
//
// Two layers:
//   - pure engine tests against SimPathfindGrid/SimPathfinder (heuristic form, the
//     FIFO-among-equals open list via reproducible searches, obstacle routing, the
//     queue ring's FIFO + dedupe);
//   - HeadlessSimGame integration: a unit ordered through the pathfind seam routes
//     around a stamped structure, arrives, and does it BIT-IDENTICALLY twice - the
//     run-twice trajectory + arrival-frame check this HIGH-RISK system exists for.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Object.Pathfind;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Pathfind;

public class SimPathfindContractTests
{
    private static Fix64 F(string s) => Fix64.FromDecimalLiteral(s);

    private static FixVector3 Pos(string x, string y, string z = "0") =>
        new(F(x), F(y), F(z));

    // ================================================================== pure engine

    [Fact]
    public void CostToGoal_MatchesTheGplForm()
    {
        // 10*max + (10*min)/2 with integer division.
        Assert.Equal(0, SimPathfinder.CostToGoal(3, 4, 3, 4));
        Assert.Equal(10, SimPathfinder.CostToGoal(1, 0, 0, 0));
        Assert.Equal(15, SimPathfinder.CostToGoal(1, 1, 0, 0));       // 10 + 10/2
        Assert.Equal(10 * 7 + (10 * 3) / 2, SimPathfinder.CostToGoal(7, 3, 0, 0));
        Assert.Equal(10 * 5 + (10 * 5) / 2, SimPathfinder.CostToGoal(-5, 5, 0, 0));
    }

    private static (SimPathfindGrid Grid, SimPathfinder Finder) NewEngine()
    {
        var grid = new SimPathfindGrid(-50, -50, 50, 50);
        return (grid, new SimPathfinder(grid));
    }

    [Fact]
    public void FindPath_OnOpenGround_EndsAtTheGoalCellCenter()
    {
        var (grid, finder) = NewEngine();
        var path = finder.FindPath(
            Surfaces.Ground, Pos("5", "5"), Pos("105", "5"),
            radius: 0, centerInCell: true, ignoreObstacleId: 0);

        Assert.NotNull(path);
        // Goal cell (10,0) center = (105,5).
        Assert.Equal(F("105"), path.LastPosition.X);
        Assert.Equal(F("5"), path.LastPosition.Y);
        // First node is the exact from-position when it differs from the cell center
        // (here it doesn't - (5,5) IS cell (0,0)'s center - so the path starts there).
        Assert.Equal(F("5"), path.Nodes[0].Position.X);
        _ = grid;
    }

    [Fact]
    public void FindPath_RoutesAroundAnObstacleWall_AndNeverEntersIt()
    {
        var (grid, finder) = NewEngine();
        // A wall of obstacle cells at x = 10, y in [-4, 4]: the straight line from
        // (55,5) to (155,5) is blocked; the path must go around an end.
        grid.StampObstacle(99, 10, -4, 10, 4);

        var path = finder.FindPath(
            Surfaces.Ground, Pos("55", "5"), Pos("155", "5"),
            radius: 0, centerInCell: true, ignoreObstacleId: 0);

        Assert.NotNull(path);
        Assert.Equal(F("155"), path.LastPosition.X);
        Assert.Equal(F("5"), path.LastPosition.Y);

        foreach (var node in path.Nodes)
        {
            grid.WorldToCell(node.Position, out var cx, out var cy);
            Assert.NotEqual(SimPathfindCellType.Obstacle, grid.GetCellType(cx, cy));
        }
        // It actually deviated: some node clears the wall span vertically.
        Assert.Contains(path.Nodes, n => Fix64.Abs(n.Position.Y) > F("40"));
    }

    [Fact]
    public void FindPath_UnreachableGoal_ReturnsNull()
    {
        var (grid, finder) = NewEngine();
        // Box the goal cell (20, 0) in on all 8 sides.
        grid.StampObstacle(7, 19, -1, 19, 1);
        grid.StampObstacle(7, 21, -1, 21, 1);
        grid.StampObstacle(7, 20, -1, 20, -1);
        grid.StampObstacle(7, 20, 1, 20, 1);

        var path = finder.FindPath(
            Surfaces.Ground, Pos("5", "5"), Pos("205", "5"),
            radius: 0, centerInCell: true, ignoreObstacleId: 0);

        Assert.Null(path);
    }

    [Fact]
    public void FindPath_RunTwice_IsBitIdentical()
    {
        // The determinism core: same inputs, same engine, twice - node-for-node raw
        // equality (the FIFO tie-break and fixed neighbor order leave no freedom).
        var (grid, finder) = NewEngine();
        grid.StampObstacle(99, 10, -4, 10, 4);

        var a = finder.FindPath(Surfaces.Ground, Pos("55", "5"), Pos("155", "5"), 0, true, 0);
        var b = finder.FindPath(Surfaces.Ground, Pos("55", "5"), Pos("155", "5"), 0, true, 0);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Nodes[i].Position.X.RawValue, b.Nodes[i].Position.X.RawValue);
            Assert.Equal(a.Nodes[i].Position.Y.RawValue, b.Nodes[i].Position.Y.RawValue);
            Assert.Equal(a.Nodes[i].NextOptimized, b.Nodes[i].NextOptimized);
        }
    }

    [Fact]
    public void QueueForPath_IsFifoAndDedupes()
    {
        var (_, finder) = NewEngine();
        Assert.True(finder.QueueForPath(new ObjectId(3)));
        Assert.True(finder.QueueForPath(new ObjectId(1)));
        Assert.True(finder.QueueForPath(new ObjectId(3)));   // duplicate coalesces
        Assert.True(finder.QueueForPath(new ObjectId(2)));

        var served = new List<uint>();
        finder.ProcessQueue(id =>
        {
            served.Add(id.Index);
            return null;
        });

        Assert.Equal(new uint[] { 3, 1, 2 }, served);
        Assert.False(finder.HasQueuedRequests);
    }

    // ================================================================== integration

    private const string Definitions = @"
Locomotor TestPathLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object PathWalker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestPathLoco
End

Object BlockHouse
  KindOf = STRUCTURE IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Geometry = CYLINDER
  GeometryMajorRadius = 25
  GeometryHeight = 10
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5AFEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().First();

    /// <summary>
    /// One full scenario run: walker at (5,5), a structure stamped across the direct
    /// line at (105,5), destination (255,5). Returns the per-frame trajectory raw
    /// values and the arrival frame (first frame the mode collapses to Maintain).
    /// </summary>
    private static (List<(long X, long Y)> Trajectory, int ArrivalStep, FixVector3 Final, SimPathfindGrid Grid)
        RunObstacleScenario(uint seed)
    {
        var game = NewGame(seed);
        var wall = game.SpawnObject("BlockHouse", game.CivilianPlayer, new Vector3(105, 5, 0));
        var walker = game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0));
        var loco = LocoOf(walker);

        // Let the first-wake transform ingestion happen before the order (LOCO-F8).
        game.Step();

        loco.SetPathfindTargetPosition(Pos("255", "5"), F("1000"));

        var trajectory = new List<(long, long)>();
        var arrivalStep = -1;
        for (var step = 0; step < 120; step++)
        {
            game.Step();
            trajectory.Add((loco.Physics.Position.X.RawValue, loco.Physics.Position.Y.RawValue));
            if (arrivalStep < 0 && loco.Mode == SimMoveMode.Maintain)
            {
                arrivalStep = step;
            }
        }

        Assert.True(arrivalStep >= 0, "walker never arrived");
        _ = wall;
        return (trajectory, arrivalStep, loco.Physics.Position, game.GameLogic.SimPathfind.Grid);
    }

    [Fact]
    public void Unit_PathsAroundAnObstacle_AndReachesTheTarget()
    {
        var (trajectory, _, final, grid) = RunObstacleScenario(0x5AFEu);

        // Arrived (within the walker's close-enough of the goal).
        Assert.True(Fix64.Abs(final.X - F("255")) < F("15"), $"final X {final.X}");
        Assert.True(Fix64.Abs(final.Y - F("5")) < F("15"), $"final Y {final.Y}");

        // The straight line runs y=5 through the structure at (105,5) r=25. A radius-0
        // walker hugs the wall (GPL infantry does the same: path cells adjacent to the
        // footprint are legal, and the follow smoothing can graze the rasterized ring by
        // a few units at corners) - but it must never cut through the structure's core,
        // and it must actually deviate laterally. The exact clearance profile vs RETAIL
        // is the oracle task (PATH-O1).
        var deviated = false;
        var coreRadius = F("20");
        foreach (var (x, y) in trajectory)
        {
            var fx = Fix64.FromRaw(x);
            var fy = Fix64.FromRaw(y);
            var dx = fx - F("105");
            var dy = fy - F("5");
            Assert.True(dx * dx + dy * dy >= coreRadius * coreRadius,
                $"walker cut through the structure core at ({fx}, {fy})");
            if (Fix64.Abs(fy - F("5")) > F("15"))
            {
                deviated = true;
            }
        }
        Assert.True(deviated, "trajectory never deviated around the obstacle");
        _ = grid;
    }

    [Fact]
    public void RunTwice_TrajectoryAndArrivalFrame_AreBitIdentical()
    {
        var (trajectoryA, arrivalA, _, _) = RunObstacleScenario(0x5AFEu);
        var (trajectoryB, arrivalB, _, _) = RunObstacleScenario(0x5AFEu);
        var runA = (Trajectory: trajectoryA, ArrivalStep: arrivalA);
        var runB = (Trajectory: trajectoryB, ArrivalStep: arrivalB);

        Assert.Equal(runA.ArrivalStep, runB.ArrivalStep);
        Assert.Equal(runA.Trajectory.Count, runB.Trajectory.Count);
        for (var i = 0; i < runA.Trajectory.Count; i++)
        {
            Assert.Equal(runA.Trajectory[i], runB.Trajectory[i]);
        }
    }

    [Fact]
    public void RequestCadence_NoMotionUntilTheQueueServesThePath()
    {
        // GPL cadence skeleton: request -> the queue serves the path in the SAME frame's
        // post-module slot -> first motion on the NEXT frame's module update.
        var game = NewGame();
        var walker = game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0));
        var loco = LocoOf(walker);
        game.Step();

        loco.SetPathfindTargetPosition(Pos("205", "5"), F("1000"));
        Assert.True(loco.PathfindWaitingForPath);

        // Frame 1 after request: module update saw "waiting" (no motion); the host's
        // queue slot then delivered the path.
        game.Step();
        Assert.False(loco.PathfindWaitingForPath);
        Assert.NotNull(loco.PathfindPath);
        Assert.Equal(F("5").RawValue, loco.Physics.Position.X.RawValue);

        // Frame 2 after request: first motion.
        game.Step();
        Assert.True(loco.Physics.Position.X > F("5"));
    }

    [Fact]
    public void RepathGuard_WithinThreeFrames_DefersTheQueueing()
    {
        var game = NewGame();
        var walker = game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0));
        var loco = LocoOf(walker);
        game.Step();

        loco.SetPathfindTargetPosition(Pos("205", "5"), F("1000"));
        game.Step();                     // path delivered, timestamp stamped
        Assert.NotNull(loco.PathfindPath);

        // Immediate re-request: the spin guard defers instead of queueing.
        loco.SetPathfindTargetPosition(Pos("105", "105"), F("1000"));
        game.Step();
        Assert.True(loco.PathfindWaitingForPath);   // NOT served this frame

        // After the 5-frame (1 s) deferral the queue serves it.
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.False(loco.PathfindWaitingForPath);
        Assert.NotNull(loco.PathfindPath);
    }
}

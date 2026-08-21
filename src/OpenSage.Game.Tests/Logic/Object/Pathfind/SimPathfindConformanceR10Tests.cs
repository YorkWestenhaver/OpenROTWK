// R10 independent conformance + determinism verification of the S5 pathfinding system
// (second-set-of-eyes pass; findings in research/pathfinding-conformance-r10.md).
//
// Layers:
//   - pure engine: scratch-state isolation across interleaved searches, the per-frame
//     cell-budget gate BETWEEN queued requests (the GPL processPathfindQueue cadence),
//     obstacle removal restoring reachability (blocked-then-freed);
//   - HeadlessSimGame integration: MULTI-UNIT obstacle course run twice with
//     bit-identical trajectories AND arrival frames; two units contesting the same
//     destination; target moved mid-path (repath cadence past the 3-frame guard);
//     unreachable target leaves the unit standing still, bit-identically.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Object.Pathfind;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Pathfind;

public class SimPathfindConformanceR10Tests
{
    private static Fix64 F(string s) => Fix64.FromDecimalLiteral(s);

    private static FixVector3 Pos(string x, string y, string z = "0") =>
        new(F(x), F(y), F(z));

    // ================================================================== pure engine

    private static (SimPathfindGrid Grid, SimPathfinder Finder) NewEngine()
    {
        var grid = new SimPathfindGrid(-50, -50, 50, 50);
        return (grid, new SimPathfinder(grid));
    }

    private static List<(long X, long Y, int Opt)> Snapshot(SimPath path)
    {
        var result = new List<(long, long, int)>();
        foreach (var node in path.Nodes)
        {
            result.Add((node.Position.X.RawValue, node.Position.Y.RawValue, node.NextOptimized));
        }
        return result;
    }

    [Fact]
    public void FindPath_InterleavedWithAnUnrelatedSearch_IsBitIdentical()
    {
        // Scratch-state isolation: the generation-stamped bookkeeping of an unrelated
        // search in between must not perturb a repeated search (parent/cost/open-list
        // leakage would show up here).
        var (grid, finder) = NewEngine();
        grid.StampObstacle(99, 10, -4, 10, 4);

        var a1 = finder.FindPath(Surfaces.Ground, Pos("55", "5"), Pos("155", "5"), 0, true, 0);
        var other = finder.FindPath(Surfaces.Ground, Pos("5", "205"), Pos("205", "5"), 0, true, 0);
        var a2 = finder.FindPath(Surfaces.Ground, Pos("55", "5"), Pos("155", "5"), 0, true, 0);

        Assert.NotNull(a1);
        Assert.NotNull(other);
        Assert.NotNull(a2);
        Assert.Equal(Snapshot(a1), Snapshot(a2));
    }

    private sealed class StubClient : ISimPathfindClient
    {
        private readonly Action<SimPathfinder> _onServe;
        public int ServedCount;

        public StubClient(Action<SimPathfinder> onServe = null) => _onServe = onServe;

        public void DoPathfind(SimPathfinder pathfinder)
        {
            ServedCount++;
            _onServe?.Invoke(pathfinder);
        }
    }

    [Fact]
    public void ProcessQueue_CellBudget_DefersTheSecondRequestToTheNextFrame()
    {
        // GPL processPathfindQueue: the 5000-cell budget is checked BETWEEN requests -
        // an expensive request runs to completion, then blocks later requests until the
        // next frame. An unreachable goal exhausts the whole 101x101 grid (>10000 cell
        // infos), so the second request must NOT be served in the same ProcessQueue call.
        var (grid, finder) = NewEngine();
        // Box goal cell (20, 0) in on all 8 sides (same ring as the contract test).
        grid.StampObstacle(7, 19, -1, 19, 1);
        grid.StampObstacle(7, 21, -1, 21, 1);
        grid.StampObstacle(7, 20, -1, 20, -1);
        grid.StampObstacle(7, 20, 1, 20, 1);

        var expensive = new StubClient(p => Assert.Null(
            p.FindPath(Surfaces.Ground, Pos("5", "5"), Pos("205", "5"), 0, true, 0)));
        var cheap = new StubClient(p => Assert.NotNull(
            p.FindPath(Surfaces.Ground, Pos("5", "5"), Pos("25", "5"), 0, true, 0)));
        var clients = new Dictionary<uint, ISimPathfindClient> { [1] = expensive, [2] = cheap };

        Assert.True(finder.QueueForPath(new ObjectId(1)));
        Assert.True(finder.QueueForPath(new ObjectId(2)));

        finder.ProcessQueue(id => clients[id.Index]);   // frame 1: budget blown by #1
        Assert.Equal(1, expensive.ServedCount);
        Assert.Equal(0, cheap.ServedCount);
        Assert.True(finder.HasQueuedRequests);

        finder.ProcessQueue(id => clients[id.Index]);   // frame 2: #2 served FIFO
        Assert.Equal(1, expensive.ServedCount);
        Assert.Equal(1, cheap.ServedCount);
        Assert.False(finder.HasQueuedRequests);
    }

    [Fact]
    public void RemoveObstacle_RestoresReachability_Deterministically()
    {
        // Blocked-then-freed at the engine level: sealed goal -> null; unstamping the
        // ring restores a path bit-identical to the never-blocked baseline (the overlay
        // restore must be exact - GPL removeObstacle reclassifies).
        var (baselineGrid, baselineFinder) = NewEngine();
        var baseline = baselineFinder.FindPath(
            Surfaces.Ground, Pos("5", "5"), Pos("205", "5"), 0, true, 0);
        Assert.NotNull(baseline);

        var (grid, finder) = NewEngine();
        grid.StampObstacle(7, 19, -1, 19, 1);
        grid.StampObstacle(7, 21, -1, 21, 1);
        grid.StampObstacle(7, 20, -1, 20, -1);
        grid.StampObstacle(7, 20, 1, 20, 1);
        Assert.Null(finder.FindPath(Surfaces.Ground, Pos("5", "5"), Pos("205", "5"), 0, true, 0));

        grid.RemoveObstacle(7, 19, -1, 19, 1);
        grid.RemoveObstacle(7, 21, -1, 21, 1);
        grid.RemoveObstacle(7, 20, -1, 20, -1);
        grid.RemoveObstacle(7, 20, 1, 20, 1);
        var freed = finder.FindPath(Surfaces.Ground, Pos("5", "5"), Pos("205", "5"), 0, true, 0);

        Assert.NotNull(freed);
        Assert.Equal(Snapshot(baseline), Snapshot(freed));
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
    /// The multi-unit obstacle course: three walkers on parallel lanes, two structures
    /// astride two of the lanes, all ordered in the same frame (so they contend for the
    /// FIFO queue in a fixed order). Returns per-unit per-frame trajectories + arrival
    /// frames.
    /// </summary>
    private static (List<(long X, long Y)>[] Trajectories, int[] ArrivalSteps)
        RunMultiUnitScenario(uint seed)
    {
        var game = NewGame(seed);
        game.SpawnObject("BlockHouse", game.CivilianPlayer, new Vector3(105, 5, 0));
        game.SpawnObject("BlockHouse", game.CivilianPlayer, new Vector3(155, 85, 0));
        var walkers = new[]
        {
            game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0)),
            game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 45, 0)),
            game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 85, 0)),
        };
        var locos = walkers.Select(LocoOf).ToArray();

        game.Step(); // first-wake transform ingestion (LOCO-F8)

        locos[0].SetPathfindTargetPosition(Pos("255", "5"), F("1000"));
        locos[1].SetPathfindTargetPosition(Pos("255", "45"), F("1000"));
        locos[2].SetPathfindTargetPosition(Pos("255", "85"), F("1000"));

        var trajectories = new List<(long, long)>[3];
        for (var u = 0; u < 3; u++)
        {
            trajectories[u] = new List<(long, long)>();
        }
        var arrivals = new[] { -1, -1, -1 };

        for (var step = 0; step < 200; step++)
        {
            game.Step();
            for (var u = 0; u < 3; u++)
            {
                trajectories[u].Add((
                    locos[u].Physics.Position.X.RawValue,
                    locos[u].Physics.Position.Y.RawValue));
                if (arrivals[u] < 0 && locos[u].Mode == SimMoveMode.Maintain)
                {
                    arrivals[u] = step;
                }
            }
        }

        for (var u = 0; u < 3; u++)
        {
            Assert.True(arrivals[u] >= 0, $"walker {u} never arrived");
        }
        return (trajectories, arrivals);
    }

    [Fact]
    public void MultiUnit_ObstacleCourse_RunTwice_TrajectoriesAndArrivalFramesBitIdentical()
    {
        // THE determinism gate for this round: several units pathing simultaneously
        // through an obstacle course, twice - every unit's every-frame position raw
        // value and arrival frame must match exactly.
        var (trajA, arrA) = RunMultiUnitScenario(0x5AFEu);
        var (trajB, arrB) = RunMultiUnitScenario(0x5AFEu);

        Assert.Equal(arrA, arrB);
        for (var u = 0; u < 3; u++)
        {
            Assert.Equal(trajA[u].Count, trajB[u].Count);
            for (var i = 0; i < trajA[u].Count; i++)
            {
                Assert.Equal(trajA[u][i], trajB[u][i]);
            }
        }
    }

    private static (List<(long X, long Y)>[] Trajectories, int[] ArrivalSteps)
        RunContestedDestinationScenario(uint seed)
    {
        // Two walkers, one destination cell: with unit occupancy deferred (PATH-F3)
        // they may overlap - but the run must still be bit-reproducible, and the FIFO
        // queue serves them in order.
        var game = NewGame(seed);
        var walkers = new[]
        {
            game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0)),
            game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 105, 0)),
        };
        var locos = walkers.Select(LocoOf).ToArray();
        game.Step();

        locos[0].SetPathfindTargetPosition(Pos("155", "55"), F("1000"));
        locos[1].SetPathfindTargetPosition(Pos("155", "55"), F("1000"));

        var trajectories = new[] { new List<(long, long)>(), new List<(long, long)>() };
        var arrivals = new[] { -1, -1 };
        for (var step = 0; step < 160; step++)
        {
            game.Step();
            for (var u = 0; u < 2; u++)
            {
                trajectories[u].Add((
                    locos[u].Physics.Position.X.RawValue,
                    locos[u].Physics.Position.Y.RawValue));
                if (arrivals[u] < 0 && locos[u].Mode == SimMoveMode.Maintain)
                {
                    arrivals[u] = step;
                }
            }
        }
        Assert.True(arrivals[0] >= 0 && arrivals[1] >= 0, "a contender never arrived");
        return (trajectories, arrivals);
    }

    [Fact]
    public void TwoUnits_ContestingTheSameDestination_RunTwice_BitIdentical()
    {
        var (trajA, arrA) = RunContestedDestinationScenario(0x5AFEu);
        var (trajB, arrB) = RunContestedDestinationScenario(0x5AFEu);

        Assert.Equal(arrA, arrB);
        for (var u = 0; u < 2; u++)
        {
            for (var i = 0; i < trajA[u].Count; i++)
            {
                Assert.Equal(trajA[u][i], trajB[u][i]);
            }
        }
    }

    private static (List<(long X, long Y)> Trajectory, int ArrivalStep, FixVector3 Final)
        RunMovedTargetScenario(uint seed)
    {
        var game = NewGame(seed);
        var walker = game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0));
        var loco = LocoOf(walker);
        game.Step();

        loco.SetPathfindTargetPosition(Pos("255", "5"), F("1000"));
        for (var step = 0; step < 10; step++)
        {
            game.Step();
        }

        // Target "moved": re-request well past the 3-frame guard - queues immediately,
        // path lands next frame (no 1 s deferral).
        loco.SetPathfindTargetPosition(Pos("105", "155"), F("1000"));
        Assert.True(loco.PathfindWaitingForPath);
        game.Step();
        Assert.False(loco.PathfindWaitingForPath);
        Assert.NotNull(loco.PathfindPath);

        var trajectory = new List<(long, long)>();
        var arrival = -1;
        for (var step = 0; step < 120; step++)
        {
            game.Step();
            trajectory.Add((loco.Physics.Position.X.RawValue, loco.Physics.Position.Y.RawValue));
            if (arrival < 0 && loco.Mode == SimMoveMode.Maintain)
            {
                arrival = step;
            }
        }
        Assert.True(arrival >= 0, "walker never arrived at the moved target");
        return (trajectory, arrival, loco.Physics.Position);
    }

    [Fact]
    public void TargetMovedMidPath_RepathsImmediatelyPastTheGuard_AndArrives_BitIdentically()
    {
        var (trajA, arrA, finalA) = RunMovedTargetScenario(0x5AFEu);
        var (trajB, arrB, finalB) = RunMovedTargetScenario(0x5AFEu);

        // Arrived at the NEW destination.
        Assert.True(Fix64.Abs(finalA.X - F("105")) < F("15"), $"final X {finalA.X}");
        Assert.True(Fix64.Abs(finalA.Y - F("155")) < F("15"), $"final Y {finalA.Y}");

        Assert.Equal(arrA, arrB);
        Assert.Equal(finalA.X.RawValue, finalB.X.RawValue);
        Assert.Equal(finalA.Y.RawValue, finalB.Y.RawValue);
        for (var i = 0; i < trajA.Count; i++)
        {
            Assert.Equal(trajA[i], trajB[i]);
        }
    }

    [Fact]
    public void UnreachableTarget_UnitStaysExactlyPut()
    {
        // A sealed destination: DoPathfind returns no path (GPL findPath failure); the
        // unit must keep standing on its exact position - waitingForPath cleared, no
        // path object, no drift.
        var game = NewGame();
        var walker = game.SpawnObject("PathWalker", game.CivilianPlayer, new Vector3(5, 5, 0));
        var loco = LocoOf(walker);
        game.Step();

        // Box goal cell (20, 10) = world (205,105) in on all 8 sides, straight on the grid.
        var grid = game.GameLogic.SimPathfind.Grid;
        grid.StampObstacle(9001, 19, 9, 19, 11);
        grid.StampObstacle(9001, 21, 9, 21, 11);
        grid.StampObstacle(9001, 20, 9, 20, 9);
        grid.StampObstacle(9001, 20, 11, 20, 11);

        loco.SetPathfindTargetPosition(Pos("205", "105"), F("1000"));
        game.Step(); // queue serves; FindPath fails
        Assert.False(loco.PathfindWaitingForPath);
        Assert.Null(loco.PathfindPath);

        var startX = loco.Physics.Position.X.RawValue;
        var startY = loco.Physics.Position.Y.RawValue;
        for (var step = 0; step < 20; step++)
        {
            game.Step();
        }
        Assert.Equal(startX, loco.Physics.Position.X.RawValue);
        Assert.Equal(startY, loco.Physics.Position.Y.RawValue);
    }
}

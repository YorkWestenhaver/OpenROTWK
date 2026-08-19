// S5 pathfinding - the deterministic A* engine + the FIFO request queue (GPL Pathfinder).
//
// Behavioral reference (clean-room, semantics only): AIPathfind.cpp -
//   internalFindPath (start/goal setup, tunneling, pop-head goal test, cleanup),
//   examineNeighboringCells (THE determinism-critical expansion: fixed neighbor table
//     E,N,W,S,NE,NW,SW,SE; diagonal-needs-adjacent-orthogonal gate; skip-if-on-any-list
//     BEFORE costs; integer costs; penalty ladder),
//   PathfindCell::costToGoal (10*max + (10*min)/2), costSoFar (10/14 step + turn 4/8/16
//     + pinched 14), putOnSortedOpenList (insert before first STRICTLY greater =>
//     FIFO among equal totalCost - the tie-break that decides timing),
//   buildActualPath/prependCells (goal-pinched backup, parent walk, from-pos prepend),
//   Path::optimize (anchor walk, farthest passable node, orthogonal/diagonal-run escape),
//   queueForPath / processPathfindQueue (FIFO ring 512 with dedupe; 5000-cells-per-frame
//     budget checked BETWEEN requests, each request runs to completion),
//   PathfindCellInfo pool (30000 infos; exhaustion stops expansion deterministically).
//
// All search arithmetic is int32; costs are MASKED to 16 bits at store time to reproduce
// GPL's UnsignedShort wrap semantics exactly (bug-compat pin PATH-F8).

using System;
using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object.Pathfind;

/// <summary>The queue's callback target (GPL AIUpdateInterface::doPathfind).</summary>
public interface ISimPathfindClient
{
    void DoPathfind(SimPathfinder pathfinder);
}

[SimState]
public sealed class SimPathfinder
{
    // GPL constants.
    private const int CostOrthogonal = 10;
    private const int CostDiagonal = 14;
    private const int QueueLength = 512;              // PATHFIND_QUEUE_LEN
    private const int CellsPerFrame = 5000;           // PATHFIND_CELLS_PER_FRAME
    private const int CellInfosToAllocate = 30000;    // the info pool cap

    private readonly SimPathfindGrid _grid;

    // ---- search bookkeeping (scratch; reset per search via generation stamps) ----
    private readonly int[] _generationOf;
    private readonly int[] _parent;       // cell index, -1 none
    private readonly int[] _costSoFar;    // 16-bit masked
    private readonly int[] _totalCost;    // 16-bit masked
    private readonly byte[] _state;       // 0 none, 1 open, 2 closed
    private readonly int[] _nextOpen;
    private readonly int[] _prevOpen;
    private int _generation;
    private int _openHead = -1;
    private int _infosAllocatedThisSearch;

    // ---- persistent sim state (GPL Pathfinder::xfer surface) ----
    private readonly uint[] _queuedRequests = new uint[QueueLength];
    private int _queueHead;
    private int _queueTail;
    private int _cumulativeCellsAllocated;

    public SimPathfinder(SimPathfindGrid grid)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        var n = grid.CellCount;
        _generationOf = new int[n];
        _parent = new int[n];
        _costSoFar = new int[n];
        _totalCost = new int[n];
        _state = new byte[n];
        _nextOpen = new int[n];
        _prevOpen = new int[n];
    }

    public SimPathfindGrid Grid => _grid;

    // ==================================================================
    // Request queue (GPL queueForPath / processPathfindQueue)
    // ==================================================================

    /// <summary>FIFO enqueue with dedupe scan (GPL queueForPath). False when full.</summary>
    public bool QueueForPath(ObjectId id)
    {
        var slot = _queueHead;
        while (slot != _queueTail)
        {
            if (_queuedRequests[slot] == id.Index)
            {
                return true;
            }
            slot++;
            if (slot >= QueueLength)
            {
                slot = 0;
            }
        }
        var nextSlot = _queueTail + 1;
        if (nextSlot >= QueueLength)
        {
            nextSlot = 0;
        }
        if (nextSlot == _queueHead)
        {
            return false; // ran out of queue slots
        }
        _queuedRequests[_queueTail] = id.Index;
        _queueTail = nextSlot;
        return true;
    }

    /// <summary>
    /// GPL processPathfindQueue's drain loop: pop requests FIFO while this frame's
    /// cell budget lasts; each request runs to completion (the budget only gates whether
    /// the NEXT one starts). The resolver maps an id to its client (dead objects skip).
    /// </summary>
    public void ProcessQueue(Func<ObjectId, ISimPathfindClient> resolveClient)
    {
        _cumulativeCellsAllocated = 0;
        while (_cumulativeCellsAllocated < CellsPerFrame && _queueTail != _queueHead)
        {
            var id = new ObjectId(_queuedRequests[_queueHead]);
            _queuedRequests[_queueHead] = 0;
            resolveClient(id)?.DoPathfind(this);
            _queueHead++;
            if (_queueHead >= QueueLength)
            {
                _queueHead = 0;
            }
        }
    }

    public bool HasQueuedRequests => _queueHead != _queueTail;

    // ==================================================================
    // The A* search (GPL internalFindPath + examineNeighboringCells)
    // ==================================================================

    // GPL's neighbor table, exactly: E, N, W, S, then NE, NW, SW, SE.
    private static readonly int[] DeltaX = { 1, 0, -1, 0, 1, -1, -1, 1 };
    private static readonly int[] DeltaY = { 0, 1, 0, -1, 1, 1, -1, -1 };
    // adjacent[5] = {0,1,2,3,0}: diagonal i needs orthogonal adjacent[i-4] or adjacent[i-3].
    private static readonly int[] Adjacent = { 0, 1, 2, 3, 0 };

    /// <summary>
    /// GPL findPath/internalFindPath for a ground unit: exact-goal A* over the grid.
    /// Returns null when no path exists (zone screen deferred - the search itself
    /// exhausts, design-note PATH-F1). <paramref name="radius"/>/<paramref name="centerInCell"/>
    /// per GPL getRadiusAndCenter.
    /// </summary>
    public SimPath FindPath(
        Surfaces surfaces, in FixVector3 from, in FixVector3 to,
        int radius, bool centerInCell, uint ignoreObstacleId)
    {
        // Goal and start cells (GPL clips; our WorldToCell clamps identically).
        _grid.WorldToCell(to, out var goalX, out var goalY);
        _grid.WorldToCell(from, out var startX, out var startY);

        // Destination screen (GPL checkDestination over the footprint).
        if (!_grid.IsValidMovementFootprint(surfaces, goalX, goalY, radius, centerInCell, ignoreObstacleId))
        {
            return null;
        }

        // Tunneling: start inside an obstacle may escape through obstacle cells.
        var isTunneling = _grid.Contains(startX, startY) &&
            _grid.GetCellType(startX, startY) == SimPathfindCellType.Obstacle;

        // Goal invalid -> no path (GPL validMovementPosition sanity check).
        if (!_grid.IsValidMovementCell(surfaces, goalX, goalY, ignoreObstacleId))
        {
            return null;
        }
        // Start invalid -> cheat via tunneling (GPL "somehow we got to an impassable location").
        if (!_grid.IsValidMovementCell(surfaces, startX, startY, ignoreObstacleId))
        {
            isTunneling = true;
        }

        BeginSearch();

        var goalIndex = _grid.CellIndex(goalX, goalY);
        var startIndex = _grid.CellIndex(startX, startY);

        // GPL startPathfind: costSoFar 0, totalCost = h(start), on the open list.
        AllocateInfo(startIndex);
        if (startIndex != goalIndex)
        {
            AllocateInfo(goalIndex);
        }
        _costSoFar[startIndex] = 0;
        _totalCost[startIndex] = CostToGoal(startX, startY, goalX, goalY) & 0xFFFF;
        _parent[startIndex] = -1;
        _openHead = startIndex;
        _state[startIndex] = 1;
        _nextOpen[startIndex] = -1;
        _prevOpen[startIndex] = -1;

        while (_openHead >= 0)
        {
            // Pop head - lowest totalCost, FIFO among equals.
            var parentIndex = _openHead;
            RemoveFromOpenList(parentIndex);

            if (parentIndex == goalIndex)
            {
                isTunneling = false;
                var path = BuildActualPath(surfaces, from, goalIndex, centerInCell, ignoreObstacleId);
                EndSearch();
                return path;
            }

            _state[parentIndex] = 2; // closed

            ExamineNeighboringCells(parentIndex, goalX, goalY, goalIndex,
                surfaces, radius, centerInCell, ignoreObstacleId, ref isTunneling);
        }

        EndSearch();
        return null;
    }

    private void ExamineNeighboringCells(
        int parentIndex, int goalX, int goalY, int goalIndex,
        Surfaces surfaces, int radius, bool centerInCell, uint ignoreObstacleId,
        ref bool isTunneling)
    {
        var px = _grid.CellXOf(parentIndex);
        var py = _grid.CellYOf(parentIndex);

        Span<bool> neighborFlags = stackalloc bool[8];

        for (var i = 0; i < 8; i++)
        {
            neighborFlags[i] = false;
            var nx = px + DeltaX[i];
            var ny = py + DeltaY[i];
            if (!_grid.Contains(nx, ny))
            {
                continue;
            }
            var neighborIndex = _grid.CellIndex(nx, ny);

            // On either list already -> skip BEFORE any cost math (GPL line order:
            // first path to claim a cell wins).
            if (HasInfo(neighborIndex) && _state[neighborIndex] != 0)
            {
                continue;
            }

            // Diagonals need an accepted adjacent orthogonal this expansion.
            if (i >= 4 && !neighborFlags[Adjacent[i - 4]] && !neighborFlags[Adjacent[i - 3]])
            {
                continue;
            }

            var movementValid = _grid.IsValidMovementCell(surfaces, nx, ny, ignoreObstacleId);
            if (!movementValid && !isTunneling)
            {
                continue;
            }
            if (movementValid)
            {
                neighborFlags[i] = true;
            }

            // Footprint check for wide units (GPL checkForMovement's radius scan).
            if (radius > 0 &&
                !_grid.IsValidMovementFootprint(surfaces, nx, ny, radius, centerInCell, ignoreObstacleId))
            {
                if (!isTunneling)
                {
                    continue;
                }
                movementValid = false;
            }

            // Tunneling turns off at the first valid, unpinched cell (GPL note-to-self).
            if (movementValid && !_grid.GetPinched(nx, ny))
            {
                isTunneling = false;
            }

            if (!HasInfo(neighborIndex))
            {
                if (!AllocateInfo(neighborIndex))
                {
                    return; // out of cell infos - stop expanding (GPL pool exhaustion)
                }
            }

            // costSoFar: step + turn penalty (+ pinched inside CostSoFar) ...
            var newCostSoFar = CostSoFar(parentIndex, neighborIndex, nx, ny);
            // ... + the expansion-time penalty ladder.
            if (_grid.GetCellType(nx, ny) == SimPathfindCellType.Cliff && !_grid.GetPinched(nx, ny))
            {
                // GPL adds 7*COST_DIAGONAL for a cliff step whose |dz| < cellsize; the
                // Fix64 terrain seam has no per-cell height yet, so the penalty applies
                // to every cliff step (flat headless maps have no cliff cells anyway;
                // recorded with PATH-F9).
                newCostSoFar += 7 * CostDiagonal;
            }
            else if (_grid.GetPinched(nx, ny))
            {
                newCostSoFar += CostOrthogonal;
            }
            var costRemaining = CostToGoal(nx, ny, goalX, goalY);
            if (_grid.GetCellType(nx, ny) == SimPathfindCellType.Obstacle)
            {
                newCostSoFar += 100 * CostOrthogonal;
            }
            if (isTunneling)
            {
                if (!movementValid)
                {
                    newCostSoFar += 10 * CostOrthogonal;
                }
                costRemaining = 0; // greedy escape to the nearest valid cell
            }

            _costSoFar[neighborIndex] = newCostSoFar & 0xFFFF;
            _parent[neighborIndex] = parentIndex;
            _totalCost[neighborIndex] = (_costSoFar[neighborIndex] + costRemaining) & 0xFFFF;

            PutOnSortedOpenList(neighborIndex);
        }
    }

    /// <summary>GPL costToGoal: 10*max(|dx|,|dy|) + (10*min(|dx|,|dy|))/2, integer division.</summary>
    internal static int CostToGoal(int x, int y, int goalX, int goalY)
    {
        var dx = x - goalX;
        var dy = y - goalY;
        if (dx < 0)
        {
            dx = -dx;
        }
        if (dy < 0)
        {
            dy = -dy;
        }
        return dx > dy
            ? CostOrthogonal * dx + (CostOrthogonal * dy) / 2
            : CostOrthogonal * dy + (CostOrthogonal * dx) / 2;
    }

    /// <summary>
    /// GPL costSoFar: parent cost + step (10 orthogonal / 14 diagonal) + pinched (+14)
    /// + turn penalty vs the grandparent direction (45deg +4, 90deg +8, 135deg+ +16).
    /// </summary>
    private int CostSoFar(int parentIndex, int cellIndex, int cellX, int cellY)
    {
        var parentX = _grid.CellXOf(parentIndex);
        var parentY = _grid.CellYOf(parentIndex);
        var prevDirX = parentX - cellX;
        var prevDirY = parentY - cellY;

        var cost = _costSoFar[parentIndex] +
            (prevDirX == 0 || prevDirY == 0 ? CostOrthogonal : CostDiagonal);
        if (_grid.GetPinched(cellX, cellY))
        {
            cost += CostDiagonal;
        }

        var grandIndex = _parent[parentIndex];
        if (grandIndex >= 0)
        {
            var dirX = _grid.CellXOf(grandIndex) - parentX;
            var dirY = _grid.CellYOf(grandIndex) - parentY;
            if (dirX != prevDirX || dirY != prevDirY)
            {
                var dot = dirX * prevDirX + dirY * prevDirY;
                cost += dot > 0 ? 4 : dot == 0 ? 8 : 16;
            }
        }
        return cost;
    }

    // ==================================================================
    // Open list: sorted doubly-linked, insert before first STRICTLY greater
    // (FIFO among equal totalCost) - GPL putOnSortedOpenList exactly.
    // ==================================================================

    private void PutOnSortedOpenList(int cellIndex)
    {
        _state[cellIndex] = 1;
        if (_openHead < 0)
        {
            _openHead = cellIndex;
            _nextOpen[cellIndex] = -1;
            _prevOpen[cellIndex] = -1;
            return;
        }

        var total = _totalCost[cellIndex];
        var last = -1;
        var c = _openHead;
        while (c >= 0)
        {
            if (_totalCost[c] > total)
            {
                break;
            }
            last = c;
            c = _nextOpen[c];
        }

        if (c >= 0)
        {
            // insert just before c
            var before = _prevOpen[c];
            if (before >= 0)
            {
                _nextOpen[before] = cellIndex;
            }
            else
            {
                _openHead = cellIndex;
            }
            _prevOpen[cellIndex] = before;
            _prevOpen[c] = cellIndex;
            _nextOpen[cellIndex] = c;
        }
        else
        {
            // append at end
            _nextOpen[last] = cellIndex;
            _prevOpen[cellIndex] = last;
            _nextOpen[cellIndex] = -1;
        }
    }

    private void RemoveFromOpenList(int cellIndex)
    {
        var next = _nextOpen[cellIndex];
        var prev = _prevOpen[cellIndex];
        if (next >= 0)
        {
            _prevOpen[next] = prev;
        }
        if (prev >= 0)
        {
            _nextOpen[prev] = next;
        }
        else
        {
            _openHead = next;
        }
        _state[cellIndex] = 0;
        _nextOpen[cellIndex] = -1;
        _prevOpen[cellIndex] = -1;
    }

    // ==================================================================
    // Info pool accounting (generation-stamped scratch; GPL's 30000 pool + the
    // per-frame budget counter).
    // ==================================================================

    private void BeginSearch()
    {
        _generation++;
        _openHead = -1;
        _infosAllocatedThisSearch = 0;
    }

    private void EndSearch()
    {
        // GPL counts released open+closed infos into m_cumulativeCellsAllocated
        // (cleanOpenAndClosedLists); allocation count == release count.
        _cumulativeCellsAllocated += _infosAllocatedThisSearch;
        _openHead = -1;
    }

    private bool HasInfo(int cellIndex) => _generationOf[cellIndex] == _generation;

    private bool AllocateInfo(int cellIndex)
    {
        if (_generationOf[cellIndex] == _generation)
        {
            return true;
        }
        if (_infosAllocatedThisSearch >= CellInfosToAllocate)
        {
            return false;
        }
        _generationOf[cellIndex] = _generation;
        _infosAllocatedThisSearch++;
        _parent[cellIndex] = -1;
        _costSoFar[cellIndex] = 0;
        _totalCost[cellIndex] = 0;
        _state[cellIndex] = 0;
        _nextOpen[cellIndex] = -1;
        _prevOpen[cellIndex] = -1;
        return true;
    }

    // ==================================================================
    // Path construction (GPL buildActualPath / prependCells / Path::optimize)
    // ==================================================================

    private SimPath BuildActualPath(
        Surfaces surfaces, in FixVector3 fromPos, int goalIndex, bool centerInCell,
        uint ignoreObstacleId)
    {
        // Goal pinched but its parent not -> back up one cell.
        var gx = _grid.CellXOf(goalIndex);
        var gy = _grid.CellYOf(goalIndex);
        if (_grid.GetPinched(gx, gy) && _parent[goalIndex] >= 0)
        {
            var p = _parent[goalIndex];
            if (!_grid.GetPinched(_grid.CellXOf(p), _grid.CellYOf(p)))
            {
                goalIndex = p;
            }
        }

        var path = new SimPath();

        // Parent walk goal -> start, prepending (so the list ends up start -> goal).
        // GPL skips the LAST cell (the start cell) and prepends the unit's exact
        // position instead.
        var cell = goalIndex;
        var prevCliff = false;
        var first = true;
        while (_parent[cell] >= 0)
        {
            var x = _grid.CellXOf(cell);
            var y = _grid.CellYOf(cell);
            var pos = centerInCell ? SimPathfindGrid.CellCenter(x, y) : SimPathfindGrid.CellCorner(x, y);
            var isCliff = _grid.GetCellType(x, y) == SimPathfindCellType.Cliff;
            // Cliff boundary nodes must not be optimized away (GPL canOptimize marking).
            var canOptimize = !(prevCliff && !isCliff) || first;
            path.Prepend(pos, 0, canOptimize);
            prevCliff = isCliff;
            first = false;
            cell = _parent[cell];
        }
        if (path.Count == 0)
        {
            // Very short path: goal cell == start cell.
            var x = _grid.CellXOf(cell);
            var y = _grid.CellYOf(cell);
            path.Prepend(
                centerInCell ? SimPathfindGrid.CellCenter(x, y) : SimPathfindGrid.CellCorner(x, y),
                0, true);
        }

        // Exact start position first (when it differs).
        var firstNode = path.Nodes[0].Position;
        if (fromPos.X != firstNode.X || fromPos.Y != firstNode.Y)
        {
            path.Prepend(fromPos, 0, true);
        }

        Optimize(path, surfaces, ignoreObstacleId);
        return path;
    }

    /// <summary>
    /// GPL Path::optimize - anchor walk building the nextOptimized chain: for each
    /// anchor, take the farthest following node reachable by a passable straight line
    /// (or an all-orthogonal / all-diagonal run), else the immediate next.
    /// </summary>
    private void Optimize(SimPath path, Surfaces surfaces, uint ignoreObstacleId)
    {
        var count = path.Count;
        if (count == 0)
        {
            return;
        }
        var anchor = 0;
        while (anchor != count - 1)
        {
            // Horizon: the last node whose predecessors all allow optimization.
            var horizon = anchor + 1;
            while (horizon + 1 <= count - 1 && path.Nodes[horizon].CanOptimize)
            {
                horizon++;
            }

            var linked = false;
            for (var node = horizon; node > anchor; node--)
            {
                var a = path.Nodes[anchor].Position;
                var b = path.Nodes[node].Position;
                var passable =
                    SimPath.IsLinePassable(_grid, surfaces, ignoreObstacleId, a, b) ||
                    IsStraightRun(path, anchor, node);
                if (passable)
                {
                    path.SetNextOptimized(anchor, node);
                    anchor = node;
                    linked = true;
                    break;
                }
            }
            if (!linked)
            {
                path.SetNextOptimized(anchor, anchor + 1);
                anchor++;
            }
        }
    }

    /// <summary>
    /// GPL's "horizontal, diagonal, and vertical steps are passable" escape: an
    /// all-same-direction run of steps between two nodes is passable by construction.
    /// </summary>
    private static bool IsStraightRun(SimPath path, int anchor, int node)
    {
        long firstDx = 0, firstDy = 0;
        for (var i = anchor; i < node; i++)
        {
            var a = path.Nodes[i].Position;
            var b = path.Nodes[i + 1].Position;
            var dx = b.X.RawValue - a.X.RawValue;
            var dy = b.Y.RawValue - a.Y.RawValue;
            if (i == anchor)
            {
                firstDx = dx;
                firstDy = dy;
                continue;
            }
            if (dx != firstDx || dy != firstDy)
            {
                return false;
            }
        }
        return true;
    }

    // ==================================================================
    // Xfer (GPL Pathfinder::xfer: the queue ring + the budget counter; the grid is
    // reclassified from the world on load, never persisted)
    // ==================================================================

    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("QueueHead", ref _queueHead);
        xfer.XferInt("QueueTail", ref _queueTail);
        for (var i = 0; i < QueueLength; i++)
        {
            xfer.XferUInt("QueuedRequest", ref _queuedRequests[i]);
        }
        xfer.XferInt("CumulativeCellsAllocated", ref _cumulativeCellsAllocated);
    }
}

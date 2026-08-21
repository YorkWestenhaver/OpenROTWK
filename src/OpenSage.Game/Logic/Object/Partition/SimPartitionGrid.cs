// The deterministic partition / vision / LOS / shroud system (build-roadmap pillar
// partition-vision, S3). Fresh code from GPL semantics only: generals-gpl GeneralsMD
// GameLogic/Object/PartitionManager.cpp (+ PartitionManager.h, Object.cpp look/unlook/
// shroud, BaseHeightMap.cpp isClearLineOfSight shape). This file is [SimState]: all sim
// math is Fix64, SIMCORE001-010 run here as errors.
//
// PUBLIC API (the seam every radius/targeting/vision module calls - see
// research/systems/partition-vision.md §"public API"):
//   Register / Unregister / UpdatePosition          - object lifecycle in the index
//   QueryObjectsInRange / GetClosestObject          - the getObjectsInRange family
//   GetCellShroudStatus / GetShroudedStatus         - cell- and object-level perception
//   DoShroudReveal / QueueUndoShroudReveal / ...    - the raw shroud verbs
//   HandleShroudMaintenance / Look / Unlook         - the per-object vision model
//   RevealMapForPlayer / ShroudMapForPlayer / ...   - the script-level map verbs
//   IsClearLineOfSightTerrain                       - terrain LOS
//   Update(now)                                     - the PartitionUpdate phase body
//   Xfer                                            - shroud state persist/CRC walk
//
// DETERMINISM CONTRACT: query results are ascending ObjectId, always (frozen,
// design-module-api §6); closest-object ties break to the lower ObjectId (pinned here -
// the original's tie order was incidental cell order). No hash iteration anywhere.

using System;
using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SimPartitionGrid
{
    /// <summary>
    /// GPL HUGE_DIST (1,000,000): "no max distance". Within the F3 R1 sentinel budget.
    /// </summary>
    public static readonly Fix64 HugeDistance = new Fix64(1_000_000);

    // GPL LOS_FUDGE = 0.5 - slop added to the sight line before the terrain compare.
    private static readonly Fix64 LosFudge = Fix64.Half;

    private readonly SimPartitionCell[] _cells;
    private readonly List<SimPartitionEntry> _entries = new();       // ascending ObjectId
    private readonly List<PendingUndoReveal> _pendingUndoReveals = new(); // FIFO, timestamps monotonic
    private uint _queryStamp;

    public IPartitionPlayerView Players { get; }

    public Fix64 WorldLoX { get; }
    public Fix64 WorldLoY { get; }
    public Fix64 CellSize { get; }
    public int CellCountX { get; }
    public int CellCountY { get; }

    /// <summary>
    /// How long a queued unlook keeps its cells revealed, in logic frames (GPL GlobalData
    /// UnlookPersistDuration, default 30 frames at 30 fps = 1 s; ours defaults to 1 s at
    /// the 5 Hz title rate = 5 frames). Data, not hardcode - the BFME2 value needs a
    /// behavioral-spec pin (finding F-PV-5).
    /// </summary>
    public LogicFrameSpan UnlookPersistDuration { get; set; } = new LogicFrameSpan(5);

    public SimPartitionGrid(
        Fix64 worldLoX,
        Fix64 worldLoY,
        Fix64 worldWidth,
        Fix64 worldHeight,
        Fix64 cellSize,
        IPartitionPlayerView players)
    {
        ArgumentNullException.ThrowIfNull(players);
        Players = players;

        // GPL init(): cell size clamps up to 1; degenerate extents clamp up to 1.
        if (cellSize < Fix64.One)
        {
            cellSize = Fix64.One;
        }
        if (worldWidth < Fix64.One)
        {
            worldWidth = Fix64.One;
        }
        if (worldHeight < Fix64.One)
        {
            worldHeight = Fix64.One;
        }

        WorldLoX = worldLoX;
        WorldLoY = worldLoY;
        CellSize = cellSize;

        CellCountX = CeilToInt(worldWidth / cellSize);
        CellCountY = CeilToInt(worldHeight / cellSize);

        _cells = new SimPartitionCell[CellCountX * CellCountY];
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new SimPartitionCell(players.PlayerCount);
        }
    }

    // ------------------------------------------------------------------
    // Coordinate mapping (GPL worldToCell / worldToCellDist / getCellAt)
    // ------------------------------------------------------------------

    private static int FloorToInt(Fix64 value) => (int)(value.RawValue >> 32);

    private static int CeilToInt(Fix64 value)
    {
        var floored = value.RawValue >> 32;
        return (int)((value.RawValue & 0xFFFFFFFFL) != 0 ? floored + 1 : floored);
    }

    /// <summary>
    /// Cell coords covering a world position (may be off-grid - callers clamp).
    /// DEVIATION (recorded, F-PV-4): GPL multiplies by a cached float 1/cellSize; a
    /// Q31.32 reciprocal of a non-power-of-two cell size is inexact and floors exact
    /// multiples into the WRONG cell (100 × inv(10) = 99.999... → cell 9), so we divide
    /// by the cell size instead - the F2 custom division is exact for representable
    /// quotients and deterministic everywhere.
    /// </summary>
    public void WorldToCell(Fix64 wx, Fix64 wy, out int cx, out int cy)
    {
        cx = FloorToInt((wx - WorldLoX) / CellSize);
        cy = FloorToInt((wy - WorldLoY) / CellSize);
    }

    /// <summary>Cells needed to cover a world distance, rounded up (GPL worldToCellDist).</summary>
    public int WorldToCellDist(Fix64 w) => CeilToInt(w / CellSize);

    private int CellIndexAt(int x, int y)
        => (x < 0 || y < 0 || x >= CellCountX || y >= CellCountY) ? -1 : y * CellCountX + x;

    internal CellShroudStatus GetCellShroudStatusByIndex(int cellIndex, int playerIndex)
        => _cells[cellIndex].GetShroudStatusForPlayer(playerIndex);

    /// <summary>
    /// Cell-level shroud status at cell coords; off-grid is Shrouded (GPL
    /// getShroudStatusForPlayer null-cell fallback).
    /// </summary>
    public CellShroudStatus GetCellShroudStatus(int playerIndex, int cx, int cy)
    {
        var index = CellIndexAt(cx, cy);
        return index < 0 ? CellShroudStatus.Shrouded : _cells[index].GetShroudStatusForPlayer(playerIndex);
    }

    /// <summary>Cell-level shroud status at a world position.</summary>
    public CellShroudStatus GetCellShroudStatus(int playerIndex, Fix64 wx, Fix64 wy)
    {
        WorldToCell(wx, wy, out var cx, out var cy);
        return GetCellShroudStatus(playerIndex, cx, cy);
    }

    // ------------------------------------------------------------------
    // Registration / movement (GPL registerObject / PartitionData coverage)
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers an object at a position and performs its first shroud maintenance
    /// (coverage fill + look). Entries are kept in ascending ObjectId order.
    /// </summary>
    public SimPartitionEntry Register(
        in PartitionObjectInfo info,
        in FixVector3 position,
        Fix64 shroudClearingRange,
        LogicFrame now)
    {
        if (!info.Id.IsValid)
        {
            throw new ArgumentException("Partition registration requires a valid ObjectId");
        }

        var entry = new SimPartitionEntry(info, Players.PlayerCount)
        {
            Position = position,
            ShroudClearingRange = shroudClearingRange,
        };

        var insertAt = _entries.Count;
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id.Index == info.Id.Index)
            {
                throw new ArgumentException($"ObjectId {info.Id.Index} is already registered");
            }
            if (_entries[i].Id.Index > info.Id.Index)
            {
                insertAt = i;
                break;
            }
        }
        _entries.Insert(insertAt, entry);

        RebuildCoverage(entry);
        HandleShroudMaintenance(entry, now);
        return entry;
    }

    /// <summary>
    /// Removes an object from the index. Its last look is queued for timed undo (the
    /// fog persists for <see cref="UnlookPersistDuration"/>, GPL unlook-on-destroy);
    /// its shroud generation stops immediately.
    /// </summary>
    public void Unregister(SimPartitionEntry entry, LogicFrame now)
    {
        Unlook(entry, now);
        Unshroud(entry);
        RemoveCoverage(entry);
        _entries.Remove(entry);
    }

    /// <summary>
    /// Moves an object: coverage cells are recomputed and the vision model re-anchored
    /// (unlook old position - queued - then look from the new one), which is GPL's
    /// handlePartitionCellMaintenance on position change.
    /// </summary>
    public void UpdatePosition(SimPartitionEntry entry, in FixVector3 newPosition, LogicFrame now)
    {
        entry.Position = newPosition;
        RemoveCoverage(entry);
        RebuildCoverage(entry);
        HandleShroudMaintenance(entry, now);
    }

    /// <summary>Live entries, ascending ObjectId (the blessed whole-index iteration).</summary>
    public IReadOnlyList<SimPartitionEntry> EntriesAscendingId => _entries;

    public SimPartitionEntry FindEntry(ObjectId id)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == id)
            {
                return _entries[i];
            }
        }
        return null;
    }

    private void RebuildCoverage(SimPartitionEntry entry)
    {
        // GPL PartitionData footprint: SMALL geometry (radius <= half cell) covers the
        // 1..4 cells under its extent box; anything bigger covers the filled discrete
        // circle of its bounding radius (box geometries approximate by bounding circle -
        // finding F-PV-2).
        var radius = entry.Info.BoundingRadius;
        var halfCell = Fix64.FromRaw(CellSize.RawValue >> 1);

        if (radius <= halfCell)
        {
            WorldToCell(entry.Position.X - radius, entry.Position.Y - radius, out var cx1, out var cy1);
            WorldToCell(entry.Position.X + radius, entry.Position.Y + radius, out var cx2, out var cy2);
            for (var x = cx1; x <= cx2; x++)
            {
                for (var y = cy1; y <= cy2; y++)
                {
                    AddCoverageCell(entry, x, y);
                }
            }
        }
        else
        {
            WorldToCell(entry.Position.X, entry.Position.Y, out var ccx, out var ccy);
            var cellRadius = WorldToCellDist(radius);
            if (cellRadius < 1)
            {
                cellRadius = 1;
            }
            DiscreteCellCircle.Draw(ccx, ccy, cellRadius, (x1, x2, y) =>
            {
                for (var x = x1; x <= x2; x++)
                {
                    AddCoverageCell(entry, x, y);
                }
            });
        }
    }

    private void AddCoverageCell(SimPartitionEntry entry, int x, int y)
    {
        var index = CellIndexAt(x, y);
        if (index < 0 || entry.CoveredCells.Contains(index))
        {
            return;
        }
        entry.CoveredCells.Add(index);
        _cells[index].Entries.Add(entry);
    }

    private void RemoveCoverage(SimPartitionEntry entry)
    {
        for (var i = 0; i < entry.CoveredCells.Count; i++)
        {
            _cells[entry.CoveredCells[i]].Entries.Remove(entry);
        }
        entry.CoveredCells.Clear();

        // Coverage changed => whole-object shroudedness may have changed for everyone.
        for (var p = 0; p < Players.PlayerCount; p++)
        {
            entry.InvalidateShroudedStatus(p);
        }
    }

    // ------------------------------------------------------------------
    // Distance measures (GPL theDistCalcProcs)
    // ------------------------------------------------------------------

    // Squared distance in raw Q62.64 (wide, exact) - the F3 rule: distance-vs-range
    // compares never materialize a Fix64 square.
    private static UInt128 DistanceSquaredWideRaw(in FixVector3 a, in FixVector3 b, bool use3D)
    {
        var dx = SquareDeltaRaw(a.X.RawValue, b.X.RawValue);
        var dy = SquareDeltaRaw(a.Y.RawValue, b.Y.RawValue);
        var sum = dx + dy;
        if (use3D)
        {
            sum += SquareDeltaRaw(a.Z.RawValue, b.Z.RawValue);
        }
        return sum;
    }

    private static UInt128 SquareDeltaRaw(long a, long b)
    {
        var d = (Int128)a - b;
        var magnitude = (UInt128)(d < 0 ? -d : d);
        return magnitude * magnitude;
    }

    /// <summary>
    /// The measure the query family ranks by: for center measures the wide squared
    /// distance; for bounding-sphere measures the square of the radius-shrunk distance
    /// (GPL distCalcProc_BoundaryAndBoundary: dist − rA − rB clamped at 0, then squared).
    /// Comparable across entries; smaller = closer.
    /// </summary>
    private UInt128 Measure(
        in FixVector3 pos,
        Fix64 posRadius,
        SimPartitionEntry other,
        PartitionDistanceType dc)
    {
        switch (dc)
        {
            case PartitionDistanceType.Center2D:
                return DistanceSquaredWideRaw(pos, other.Position, use3D: false);
            case PartitionDistanceType.Center3D:
                return DistanceSquaredWideRaw(pos, other.Position, use3D: true);
            default:
                {
                    var use3D = dc == PartitionDistanceType.BoundingSphere3D;
                    var wideSq = DistanceSquaredWideRaw(pos, other.Position, use3D);
                    // sqrt of Q62.64 lands in Q31.32 raw (the FixMath.Distance shape).
                    var distRaw = (Int128)(long)Fix64Distance(wideSq).RawValue;
                    var shrunkRaw = distRaw - posRadius.RawValue - other.Info.BoundingRadius.RawValue;
                    if (shrunkRaw < 0)
                    {
                        shrunkRaw = 0;
                    }
                    var mag = (UInt128)shrunkRaw;
                    return mag * mag;
                }
        }
    }

    private static Fix64 Fix64Distance(UInt128 wideSquared)
    {
        // FixMath.Distance without re-deriving the wide square: route through the two
        // public helpers by reconstructing a 1-axis vector pair whose delta is the
        // distance... not possible losslessly; instead use the public FixMath.Distance
        // on synthetic points along one axis only when exact. We keep it simple and
        // exact: integer square root of the Q62.64 value via binary search (raw-space,
        // deterministic, ~64 iterations).
        if (wideSquared == 0)
        {
            return Fix64.Zero;
        }
        UInt128 lo = 0;
        UInt128 hi = UInt128.MaxValue >> 64; // result fits 64 bits (Q31.32 raw)
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (mid * mid <= wideSquared)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return Fix64.FromRaw((long)(ulong)lo);
    }

    /// <summary>
    /// True when the measured distance is strictly less than <paramref name="maxDist"/>
    /// (GPL's procs return <c>abDistSqr &lt; maxDistSqr</c> - strict). Negative max
    /// admits nothing.
    /// </summary>
    private static bool WithinStrict(UInt128 measure, Fix64 maxDist)
    {
        if (maxDist <= Fix64.Zero)
        {
            return false;
        }
        var maxRaw = (UInt128)(ulong)maxDist.RawValue;
        return measure < maxRaw * maxRaw;
    }

    // ------------------------------------------------------------------
    // The query family (GPL getClosestObjects / iterateObjectsInRange)
    // ------------------------------------------------------------------

    /// <summary>Optional query predicate (GPL PartitionFilter). Must be pure.</summary>
    public delegate bool PartitionQueryFilter(SimPartitionEntry entry);

    /// <summary>
    /// All registered objects whose measured distance from <paramref name="center"/> is
    /// strictly within <paramref name="maxDist"/>, excluding <paramref name="center"/>
    /// itself, in ASCENDING OBJECTID ORDER (the frozen determinism contract). Results
    /// are appended to <paramref name="results"/>.
    /// </summary>
    public void QueryObjectsInRange(
        SimPartitionEntry center,
        Fix64 maxDist,
        PartitionDistanceType dc,
        List<SimPartitionEntry> results,
        PartitionQueryFilter filter = null)
    {
        QueryCore(center, center.Position, CenterRadius(center, dc), maxDist, dc, results, filter);
        results.Sort(static (a, b) => a.Id.Index.CompareTo(b.Id.Index));
    }

    /// <summary>Position-centered variant (no excluded object, zero center radius).</summary>
    public void QueryObjectsInRange(
        in FixVector3 center,
        Fix64 maxDist,
        PartitionDistanceType dc,
        List<SimPartitionEntry> results,
        PartitionQueryFilter filter = null)
    {
        QueryCore(null, center, Fix64.Zero, maxDist, dc, results, filter);
        results.Sort(static (a, b) => a.Id.Index.CompareTo(b.Id.Index));
    }

    /// <summary>
    /// The closest satisfying object, or null. Ties on the measure break to the LOWER
    /// ObjectId (pinned; the original's tie order was incidental).
    /// </summary>
    public SimPartitionEntry GetClosestObject(
        SimPartitionEntry center,
        Fix64 maxDist,
        PartitionDistanceType dc,
        PartitionQueryFilter filter = null)
        => ClosestCore(center, center.Position, CenterRadius(center, dc), maxDist, dc, filter);

    public SimPartitionEntry GetClosestObject(
        in FixVector3 center,
        Fix64 maxDist,
        PartitionDistanceType dc,
        PartitionQueryFilter filter = null)
        => ClosestCore(null, center, Fix64.Zero, maxDist, dc, filter);

    private static Fix64 CenterRadius(SimPartitionEntry center, PartitionDistanceType dc)
        => dc is PartitionDistanceType.BoundingSphere2D or PartitionDistanceType.BoundingSphere3D
            ? center.Info.BoundingRadius
            : Fix64.Zero;

    private void QueryCore(
        SimPartitionEntry exclude,
        in FixVector3 pos,
        Fix64 posRadius,
        Fix64 maxDist,
        PartitionDistanceType dc,
        List<SimPartitionEntry> results,
        PartitionQueryFilter filter)
    {
        var stamp = ++_queryStamp;
        var scanReach = maxDist + posRadius + MaxRegisteredRadius();
        GetScanBounds(pos, scanReach, out var cx1, out var cy1, out var cx2, out var cy2);
        for (var y = cy1; y <= cy2; y++)
        {
            for (var x = cx1; x <= cx2; x++)
            {
                var cellIndex = CellIndexAt(x, y);
                if (cellIndex < 0)
                {
                    continue;
                }
                var cellEntries = _cells[cellIndex].Entries;
                for (var i = 0; i < cellEntries.Count; i++)
                {
                    var candidate = cellEntries[i];
                    if (candidate == exclude || candidate.QueryStamp == stamp)
                    {
                        continue;
                    }
                    candidate.QueryStamp = stamp;

                    if (!WithinStrict(Measure(pos, posRadius, candidate, dc), maxDist))
                    {
                        continue;
                    }
                    if (filter != null && !filter(candidate))
                    {
                        continue;
                    }
                    results.Add(candidate);
                }
            }
        }
    }

    private SimPartitionEntry ClosestCore(
        SimPartitionEntry exclude,
        in FixVector3 pos,
        Fix64 posRadius,
        Fix64 maxDist,
        PartitionDistanceType dc,
        PartitionQueryFilter filter)
    {
        var stamp = ++_queryStamp;
        var scanReach = maxDist + posRadius + MaxRegisteredRadius();
        GetScanBounds(pos, scanReach, out var cx1, out var cy1, out var cx2, out var cy2);

        SimPartitionEntry best = null;
        UInt128 bestMeasure = 0;
        for (var y = cy1; y <= cy2; y++)
        {
            for (var x = cx1; x <= cx2; x++)
            {
                var cellIndex = CellIndexAt(x, y);
                if (cellIndex < 0)
                {
                    continue;
                }
                var cellEntries = _cells[cellIndex].Entries;
                for (var i = 0; i < cellEntries.Count; i++)
                {
                    var candidate = cellEntries[i];
                    if (candidate == exclude || candidate.QueryStamp == stamp)
                    {
                        continue;
                    }
                    candidate.QueryStamp = stamp;

                    var measure = Measure(pos, posRadius, candidate, dc);
                    if (!WithinStrict(measure, maxDist))
                    {
                        continue;
                    }
                    if (filter != null && !filter(candidate))
                    {
                        continue;
                    }

                    if (best == null ||
                        measure < bestMeasure ||
                        (measure == bestMeasure && candidate.Id.Index < best.Id.Index))
                    {
                        best = candidate;
                        bestMeasure = measure;
                    }
                }
            }
        }
        return best;
    }

    private Fix64 MaxRegisteredRadius()
    {
        // Coverage guarantees an entry is present in every cell its bounding circle
        // touches, so the scan box only needs maxDist + the center's own radius; but a
        // bounding-sphere query can match an entry whose EDGE is in range while its
        // center cell lies outside the box. Extending the box by the largest registered
        // radius keeps the scan exact. O(n) over entries: bounded, deterministic.
        var max = Fix64.Zero;
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Info.BoundingRadius > max)
            {
                max = _entries[i].Info.BoundingRadius;
            }
        }
        return max;
    }

    private void GetScanBounds(
        in FixVector3 pos, Fix64 reach, out int cx1, out int cy1, out int cx2, out int cy2)
    {
        if (reach >= HugeDistance)
        {
            cx1 = 0;
            cy1 = 0;
            cx2 = CellCountX - 1;
            cy2 = CellCountY - 1;
            return;
        }
        WorldToCell(pos.X - reach, pos.Y - reach, out cx1, out cy1);
        WorldToCell(pos.X + reach, pos.Y + reach, out cx2, out cy2);
        cx1 = FixMath.Max(cx1, 0);
        cy1 = FixMath.Max(cy1, 0);
        cx2 = FixMath.Min(cx2, CellCountX - 1);
        cy2 = FixMath.Min(cy2, CellCountY - 1);
    }

    // ------------------------------------------------------------------
    // Shroud verbs (GPL doShroudReveal family)
    // ------------------------------------------------------------------

    private void ForEachPlayerInMask(uint playerMask, Action<int> action)
    {
        // GPL iterates players high-to-low; order is irrelevant here (the per-cell ops
        // commute per player) but we keep ascending for the doc'd convention.
        for (var p = 0; p < Players.PlayerCount; p++)
        {
            if ((playerMask & (1u << p)) != 0)
            {
                action(p);
            }
        }
    }

    private void CircleOverCells(Fix64 centerX, Fix64 centerY, Fix64 radius, Action<SimPartitionCell, int> perCell)
    {
        WorldToCell(centerX, centerY, out var ccx, out var ccy);
        var cellRadius = WorldToCellDist(radius);
        if (cellRadius < 1)
        {
            cellRadius = 1;
        }
        DiscreteCellCircle.Draw(ccx, ccy, cellRadius, (x1, x2, y) =>
        {
            if (y < 0 || y >= CellCountY || x1 >= CellCountX || x2 < 0)
            {
                return;
            }
            var xs = FixMath.Max(x1, 0);
            var xe = FixMath.Min(x2, CellCountX - 1);
            for (var x = xs; x <= xe; x++)
            {
                perCell(_cells[y * CellCountX + x], y * CellCountX + x);
            }
        });
    }

    /// <summary>Adds a looker over the circle for every player in the mask (GPL doShroudReveal).</summary>
    public void DoShroudReveal(Fix64 centerX, Fix64 centerY, Fix64 radius, uint playerMask)
        => ForEachPlayerInMask(playerMask, p =>
            CircleOverCells(centerX, centerY, radius, (cell, _) => AddLooker(cell, p)));

    /// <summary>Immediately removes a looker over the circle (GPL undoShroudReveal).</summary>
    public void UndoShroudReveal(Fix64 centerX, Fix64 centerY, Fix64 radius, uint playerMask)
        => ForEachPlayerInMask(playerMask, p =>
            CircleOverCells(centerX, centerY, radius, (cell, _) => RemoveLooker(cell, p)));

    /// <summary>
    /// Queues a timed looker removal for <see cref="UnlookPersistDuration"/> from now
    /// (GPL queueUndoShroudReveal): the just-unlooked area stays visible briefly, which
    /// is what makes a moving unit's trail fade instead of snapping.
    /// </summary>
    public void QueueUndoShroudReveal(Fix64 centerX, Fix64 centerY, Fix64 radius, uint playerMask, LogicFrame now)
    {
        _pendingUndoReveals.Add(new PendingUndoReveal
        {
            X = centerX,
            Y = centerY,
            Radius = radius,
            PlayerMask = playerMask,
            DueFrame = now + UnlookPersistDuration,
        });
    }

    /// <summary>Adds a shrouder over the circle (GPL doShroudCover - shroud generation).</summary>
    public void DoShroudCover(Fix64 centerX, Fix64 centerY, Fix64 radius, uint playerMask)
        => ForEachPlayerInMask(playerMask, p =>
            CircleOverCells(centerX, centerY, radius, (cell, _) => AddShrouder(cell, p)));

    /// <summary>Removes a shrouder over the circle (GPL undoShroudCover, immediate).</summary>
    public void UndoShroudCover(Fix64 centerX, Fix64 centerY, Fix64 radius, uint playerMask)
        => ForEachPlayerInMask(playerMask, p =>
            CircleOverCells(centerX, centerY, radius, (cell, _) => RemoveShrouder(cell, p)));

    /// <summary>
    /// The PartitionUpdate phase body (GPL PartitionManager::update →
    /// processPendingUndoShroudRevealQueue): pops every queued unlook whose due frame is
    /// in the past and undoes it. FIFO with monotonic timestamps, so the scan stops at
    /// the first future entry.
    /// </summary>
    public void Update(LogicFrame now)
    {
        ProcessPendingUndoShroudRevealQueue(now, considerTimestamp: true);
    }

    /// <summary>Flushes the whole queue regardless of timestamps (GPL processEntire...).</summary>
    public void ProcessEntirePendingUndoShroudRevealQueue()
        => ProcessPendingUndoShroudRevealQueue(default, considerTimestamp: false);

    private void ProcessPendingUndoShroudRevealQueue(LogicFrame now, bool considerTimestamp)
    {
        while (_pendingUndoReveals.Count > 0)
        {
            var head = _pendingUndoReveals[0];
            if (considerTimestamp && head.DueFrame >= now)
            {
                // GPL pops while m_data < now (strict).
                break;
            }
            _pendingUndoReveals.RemoveAt(0);
            UndoShroudReveal(head.X, head.Y, head.Radius, head.PlayerMask);
        }
    }

    // ---- the four per-cell shroud algorithms (GPL PartitionCell) ----

    private void AddLooker(SimPartitionCell cell, int playerIndex)
    {
        var oldStatus = cell.GetShroudStatusForPlayer(playerIndex);
        // The decreasing algorithm: a 1 goes straight to -1, otherwise decrement.
        ref var level = ref cell.ShroudLevelFor(playerIndex);
        level.CurrentShroud = (short)FixMath.Min(level.CurrentShroud - 1, -1);
        OnCellShroudEdge(cell, playerIndex, oldStatus);
    }

    private void RemoveLooker(SimPartitionCell cell, int playerIndex)
    {
        var oldStatus = cell.GetShroudStatusForPlayer(playerIndex);
        // The increasing algorithm: a -1 goes up to min(1, activeShroudLevel); otherwise increment.
        ref var level = ref cell.ShroudLevelFor(playerIndex);
        if (level.CurrentShroud == -1)
        {
            level.CurrentShroud = (short)FixMath.Min(level.ActiveShroudLevel, (short)1);
        }
        else
        {
            level.CurrentShroud++;
        }
        OnCellShroudEdge(cell, playerIndex, oldStatus);
    }

    private void AddShrouder(SimPartitionCell cell, int playerIndex)
    {
        var oldStatus = cell.GetShroudStatusForPlayer(playerIndex);
        // Increasing active shroud: bump the level; an unwatched (0) cell snaps shrouded.
        ref var level = ref cell.ShroudLevelFor(playerIndex);
        level.ActiveShroudLevel++;
        if (level.CurrentShroud == 0)
        {
            level.CurrentShroud = 1;
        }
        OnCellShroudEdge(cell, playerIndex, oldStatus);
    }

    private void RemoveShrouder(SimPartitionCell cell, int playerIndex)
    {
        // Decreasing active shroud never changes the visible status (GPL).
        ref var level = ref cell.ShroudLevelFor(playerIndex);
        level.ActiveShroudLevel--;
    }

    private static void OnCellShroudEdge(SimPartitionCell cell, int playerIndex, CellShroudStatus oldStatus)
    {
        if (cell.GetShroudStatusForPlayer(playerIndex) != oldStatus)
        {
            // Edge trigger: every object touching this cell rethinks its shroudedness.
            // (The original also pushes the local player's cell to Display/Radar here -
            // client outputs, no determinism obligation, not modeled.)
            for (var i = 0; i < cell.Entries.Count; i++)
            {
                cell.Entries[i].InvalidateShroudedStatus(playerIndex);
            }
        }
    }

    // ------------------------------------------------------------------
    // Map-level script verbs (GPL revealMapForPlayer family)
    // ------------------------------------------------------------------

    /// <summary>One-shot reveal: the whole map flips to fogged-at-worst (GPL revealMapForPlayer).</summary>
    public void RevealMapForPlayer(int playerIndex)
    {
        for (var i = 0; i < _cells.Length; i++)
        {
            AddLooker(_cells[i], playerIndex);
            RemoveLooker(_cells[i], playerIndex);
        }
    }

    /// <summary>
    /// Observer-mode reveal: a permanent looker on every cell (GPL
    /// revealMapForPlayerPermanently; flushes the pending queue first).
    /// </summary>
    public void RevealMapForPlayerPermanently(int playerIndex)
    {
        ProcessEntirePendingUndoShroudRevealQueue();
        for (var i = 0; i < _cells.Length; i++)
        {
            AddLooker(_cells[i], playerIndex);
        }
    }

    /// <summary>Undoes the permanent reveal (GPL undoRevealMapForPlayerPermanently).</summary>
    public void UndoRevealMapForPlayerPermanently(int playerIndex)
    {
        ProcessEntirePendingUndoShroudRevealQueue();
        for (var i = 0; i < _cells.Length; i++)
        {
            RemoveLooker(_cells[i], playerIndex);
        }
    }

    /// <summary>
    /// Resets the player's map to passive shroud, re-explorable (GPL shroudMapForPlayer:
    /// flush queue, then add+remove a shrouder per cell - watched cells stay watched).
    /// </summary>
    public void ShroudMapForPlayer(int playerIndex)
    {
        ProcessEntirePendingUndoShroudRevealQueue();
        for (var i = 0; i < _cells.Length; i++)
        {
            AddShrouder(_cells[i], playerIndex);
            RemoveShrouder(_cells[i], playerIndex);
        }
    }

    // ------------------------------------------------------------------
    // The per-object vision model (GPL Object::look/unlook/shroud/unshroud)
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-anchors the object's shroud interactions at its current position: undo last
    /// looking (queued) and shrouding, then redo both (GPL Object::handleShroud, the
    /// heart of handlePartitionCellMaintenance).
    /// </summary>
    public void HandleShroudMaintenance(SimPartitionEntry entry, LogicFrame now)
    {
        Unlook(entry, now);
        Unshroud(entry);
        Shroud(entry);
        Look(entry, now);
    }

    /// <summary>
    /// The look half of the vision model (GPL Object::look): reveals
    /// <see cref="SimPartitionEntry.ShroudClearingRange"/> for the owner's looker mask
    /// (self + allies, or everyone for REVEAL_TO_ALL), and additionally
    /// <see cref="SimPartitionEntry.RevealToAllRange"/> for enemies + neutrals.
    /// No-ops when <see cref="SimPartitionEntry.CanLook"/> is false.
    /// </summary>
    public void Look(SimPartitionEntry entry, LogicFrame now)
    {
        if (!entry.LastLook.IsInvalid || !entry.CanLook)
        {
            return;
        }

        var range = entry.ShroudClearingRange;
        if (range > Fix64.Zero)
        {
            var mask = entry.Info.RevealToAll
                ? AllPlayersMask()
                : Players.GetLookerMask(entry.Info.OwnerPlayerIndex);

            DoShroudReveal(entry.Position.X, entry.Position.Y, range, mask);
            entry.LastLook = new PartitionSightingInfo
            {
                X = entry.Position.X,
                Y = entry.Position.Y,
                Radius = range,
                PlayerMask = mask,
            };
        }

        var revealAllRange = entry.RevealToAllRange;
        if (revealAllRange > Fix64.Zero)
        {
            var mask = Players.GetEnemyAndNeutralMask(entry.Info.OwnerPlayerIndex);
            DoShroudReveal(entry.Position.X, entry.Position.Y, revealAllRange, mask);
            entry.LastRevealAllLook = new PartitionSightingInfo
            {
                X = entry.Position.X,
                Y = entry.Position.Y,
                Radius = revealAllRange,
                PlayerMask = mask,
            };
        }
    }

    /// <summary>Queues the undo of the last look (GPL Object::unlook).</summary>
    public void Unlook(SimPartitionEntry entry, LogicFrame now)
    {
        if (!entry.LastLook.IsInvalid)
        {
            QueueUndoShroudReveal(
                entry.LastLook.X, entry.LastLook.Y, entry.LastLook.Radius, entry.LastLook.PlayerMask, now);
            entry.LastLook.Reset();
        }
        if (!entry.LastRevealAllLook.IsInvalid)
        {
            QueueUndoShroudReveal(
                entry.LastRevealAllLook.X, entry.LastRevealAllLook.Y,
                entry.LastRevealAllLook.Radius, entry.LastRevealAllLook.PlayerMask, now);
            entry.LastRevealAllLook.Reset();
        }
    }

    /// <summary>
    /// The shroud-generation half (GPL Object::shroud): covers
    /// <see cref="SimPartitionEntry.ShroudRange"/> for every enemy and neutral player.
    /// </summary>
    public void Shroud(SimPartitionEntry entry)
    {
        if (!entry.LastShroud.IsInvalid || !entry.CanLook)
        {
            return;
        }
        var range = entry.ShroudRange;
        if (range <= Fix64.Zero)
        {
            return;
        }
        var mask = Players.GetEnemyAndNeutralMask(entry.Info.OwnerPlayerIndex);
        DoShroudCover(entry.Position.X, entry.Position.Y, range, mask);
        entry.LastShroud = new PartitionSightingInfo
        {
            X = entry.Position.X,
            Y = entry.Position.Y,
            Radius = range,
            PlayerMask = mask,
        };
    }

    /// <summary>Immediately undoes the last shroud generation (GPL Object::unshroud).</summary>
    public void Unshroud(SimPartitionEntry entry)
    {
        if (entry.LastShroud.IsInvalid)
        {
            return;
        }
        UndoShroudCover(
            entry.LastShroud.X, entry.LastShroud.Y, entry.LastShroud.Radius, entry.LastShroud.PlayerMask);
        entry.LastShroud.Reset();
    }

    /// <summary>Range change re-look (GPL Object::setShroudClearingRange).</summary>
    public void SetShroudClearingRange(SimPartitionEntry entry, Fix64 range, LogicFrame now)
    {
        if (entry.ShroudClearingRange == range)
        {
            return;
        }
        entry.ShroudClearingRange = range;
        HandleShroudMaintenance(entry, now);
    }

    /// <summary>Reveal-to-all range setter (template value; re-looks on change).</summary>
    public void SetRevealToAllRange(SimPartitionEntry entry, Fix64 range, LogicFrame now)
    {
        if (entry.RevealToAllRange == range)
        {
            return;
        }
        entry.RevealToAllRange = range;
        HandleShroudMaintenance(entry, now);
    }

    /// <summary>Shroud-generation range setter (GPL Object::setShroudRange + maintenance).</summary>
    public void SetShroudRange(SimPartitionEntry entry, Fix64 range, LogicFrame now)
    {
        if (entry.ShroudRange == range)
        {
            return;
        }
        entry.ShroudRange = range;
        HandleShroudMaintenance(entry, now);
    }

    /// <summary>
    /// Dead / contained-in-a-transport / under-construction objects stop looking (the
    /// caller owns WHICH conditions apply - GPL checks them in Object::look). Flipping
    /// the flag re-runs maintenance.
    /// </summary>
    public void SetCanLook(SimPartitionEntry entry, bool canLook, LogicFrame now)
    {
        if (entry.CanLook == canLook)
        {
            return;
        }
        entry.CanLook = canLook;
        HandleShroudMaintenance(entry, now);
    }

    private uint AllPlayersMask()
    {
        // PLAYERMASK_ALL restricted to the real roster (masks beyond PlayerCount are
        // never folded, keeping the Xfer walk roster-stable).
        return Players.PlayerCount >= 32 ? uint.MaxValue : (1u << Players.PlayerCount) - 1;
    }

    // ------------------------------------------------------------------
    // Line of sight (GPL PartitionManager::isClearLineOfSightTerrain +
    // BaseHeightMapRenderObjClass::isClearLineOfSight Bresenham shape)
    // ------------------------------------------------------------------

    /// <summary>
    /// True iff the straight line between the two EYE positions clears the terrain.
    /// Terrain only - objects, trees and buildings do not block (GPL doc). Callers pass
    /// eye positions: object position with <see cref="PartitionObjectInfo.HeightAbovePosition"/>
    /// added to z (see <see cref="EyePosition"/>).
    /// Deterministic integer Bresenham over the partition-cell grid, sampling
    /// <paramref name="terrain"/> at each step and comparing against the interpolated
    /// sight line + the 0.5 fudge (GPL LOS_FUDGE).
    /// </summary>
    public bool IsClearLineOfSightTerrain(in FixVector3 eyeA, in FixVector3 eyeB, ITerrainLogic terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);

        WorldToCell(eyeA.X, eyeA.Y, out var startX, out var startY);
        WorldToCell(eyeB.X, eyeB.Y, out var endX, out var endY);

        var deltaX = endX >= startX ? endX - startX : startX - endX;
        var deltaY = endY >= startY ? endY - startY : startY - endY;
        var stepX = endX >= startX ? 1 : -1;
        var stepY = endY >= startY ? 1 : -1;

        int xinc1, xinc2, yinc1, yinc2, den, num, numadd, numpixels;
        if (deltaX >= deltaY)
        {
            xinc1 = 0;
            xinc2 = stepX;
            yinc1 = stepY;
            yinc2 = 0;
            den = deltaX;
            num = deltaX / 2;
            numadd = deltaY;
            numpixels = deltaX;
        }
        else
        {
            xinc1 = stepX;
            xinc2 = 0;
            yinc1 = 0;
            yinc2 = stepY;
            den = deltaY;
            num = deltaY / 2;
            numadd = deltaX;
            numpixels = deltaY;
        }

        var zDelta = eyeB.Z - eyeA.Z;
        var x = startX;
        var y = startY;
        var halfCell = Fix64.FromRaw(CellSize.RawValue >> 1);
        var numpixelsFix = new Fix64(numpixels == 0 ? 1 : numpixels);

        for (var i = 0; i <= numpixels; i++)
        {
            // Sight line z at this fraction of the walk.
            var fraction = new Fix64(i) / numpixelsFix;
            var lineZ = eyeA.Z + zDelta * fraction;

            // Terrain sample at the visited cell's center.
            var sampleX = WorldLoX + new Fix64(x) * CellSize + halfCell;
            var sampleY = WorldLoY + new Fix64(y) * CellSize + halfCell;
            var terrainZ = terrain.GetGroundHeight(new FixVector3(sampleX, sampleY, Fix64.Zero));

            if (terrainZ > lineZ + LosFudge)
            {
                return false;
            }

            num += numadd;
            if (num >= den)
            {
                num -= den;
                x += xinc1;
                y += yinc1;
            }
            x += xinc2;
            y += yinc2;
        }

        return true;
    }

    /// <summary>The LOS eye position for an entry: top of the collision shape (GPL).</summary>
    public static FixVector3 EyePosition(SimPartitionEntry entry)
        => new(entry.Position.X, entry.Position.Y, entry.Position.Z + entry.Info.HeightAbovePosition);

    // ------------------------------------------------------------------
    // Persist / checksum (GPL PartitionCell::crc/xfer + the pending queue)
    // ------------------------------------------------------------------

    /// <summary>
    /// The grid's contract walk (F8 Partition/Shroud channels; declaration order ours,
    /// F9): geometry guards, every cell's per-player shroud ledger, then the pending
    /// undo-reveal queue. Entries are NOT walked here - registration facts are rebuilt
    /// by re-registration on load (GPL shape), and each entry's sighting state is walked
    /// by its owner via <see cref="SimPartitionEntry.Xfer"/>. All fields Exact (A3:
    /// integers and quantized Fix64 have no legitimate quantum gap in self-diff).
    /// </summary>
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);

        var cellCountX = CellCountX;
        var cellCountY = CellCountY;
        var playerCount = Players.PlayerCount;
        xfer.XferInt("CellCountX", ref cellCountX);
        xfer.XferInt("CellCountY", ref cellCountY);
        xfer.XferInt("PlayerCount", ref playerCount);
        if (cellCountX != CellCountX || cellCountY != CellCountY || playerCount != Players.PlayerCount)
        {
            throw new InvalidOperationException("SimPartitionGrid geometry mismatch on load");
        }

        for (var i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            for (var p = 0; p < playerCount; p++)
            {
                ref var level = ref cell.ShroudLevelFor(p);
                int current = level.CurrentShroud;
                int active = level.ActiveShroudLevel;
                xfer.XferInt($"Cell[{i}].Current[{p}]", ref current);
                xfer.XferInt($"Cell[{i}].Active[{p}]", ref active);
                if (xfer.Mode == XferMode.Load)
                {
                    level.CurrentShroud = (short)current;
                    level.ActiveShroudLevel = (short)active;
                }
            }
        }

        xfer.XferList("PendingUndoReveals", _pendingUndoReveals, static (IXfer x, ref PendingUndoReveal item) =>
        {
            x.XferFix64("X", ref item.X);
            x.XferFix64("Y", ref item.Y);
            x.XferFix64("Radius", ref item.Radius);
            x.XferUInt("PlayerMask", ref item.PlayerMask);
            x.XferFrame("DueFrame", ref item.DueFrame, Tolerance.Exact);
        });

        if (xfer.Mode == XferMode.Load)
        {
            // Every cached whole-object status is stale against the loaded cells.
            for (var i = 0; i < _entries.Count; i++)
            {
                for (var p = 0; p < playerCount; p++)
                {
                    _entries[i].InvalidateShroudedStatus(p);
                }
            }
        }
    }

    private struct PendingUndoReveal
    {
        public Fix64 X;
        public Fix64 Y;
        public Fix64 Radius;
        public uint PlayerMask;
        public LogicFrame DueFrame;
    }
}

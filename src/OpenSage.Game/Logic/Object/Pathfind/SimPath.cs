// S5 pathfinding - the path result (GPL Path / PathNode) and the path-follow projection
// (GPL Path::computePointOnPath), all Fix64.
//
// Behavioral reference (clean-room, semantics only): AIPathfind.cpp - Path::prependNode/
// appendNode, setNextOptimized, computePointOnPath (cpop cache MAX_CPOP=20, 0.1 input
// tolerance; nearest-segment strict-less scan over the OPTIMIZED chain; goal selection:
// straight-shot to segment end when the line is passable, next-segment midpoint lookahead
// past mid-segment / within 1.0 of the end, else the k-blend toward the path with
// maxPathError = 3 cells), Path::xfer.
//
// Deviation (recorded): GPL caches each optimized link's normalized 2D direction+length
// at link time (floats); we recompute them per query in Fix64 - fewer persisted fields,
// same values every query, deterministic.

using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object.Pathfind;

[SimState]
public sealed class SimPath
{
    public struct Node
    {
        public FixVector3 Position;
        /// <summary>Index of the next node in the optimized chain, or -1 (GPL m_nextOpti).</summary>
        public int NextOptimized;
        public bool CanOptimize;
        public byte Layer;
    }

    private readonly List<Node> _nodes = new();

    // cpop cache (GPL m_cpopValid/m_cpopCountdown/m_cpopIn/m_cpopOut).
    private const int MaxCpop = 20;
    private bool _cpopValid;
    private int _cpopCountdown;
    private FixVector3 _cpopIn;
    private FixVector3 _cpopOutPos;
    private Fix64 _cpopOutDist;

    private static readonly Fix64 CpopTolerance = Fix64.FromDecimalLiteral("0.1");
    private static readonly Fix64 MaxPathError =
        Fix64.FromRaw(3L * SimPathfindGrid.CellSize << 32); // 3 cells
    private static readonly Fix64 NearSegmentEnd = Fix64.One;

    public IReadOnlyList<Node> Nodes => _nodes;

    public int Count => _nodes.Count;

    public FixVector3 LastPosition => _nodes[_nodes.Count - 1].Position;

    internal void Append(in FixVector3 position, byte layer, bool canOptimize)
    {
        _nodes.Add(new Node
        {
            Position = position,
            NextOptimized = -1,
            CanOptimize = canOptimize,
            Layer = layer,
        });
    }

    internal void Prepend(in FixVector3 position, byte layer, bool canOptimize)
    {
        _nodes.Insert(0, new Node
        {
            Position = position,
            NextOptimized = -1,
            CanOptimize = canOptimize,
            Layer = layer,
        });
    }

    internal void SetNextOptimized(int nodeIndex, int nextIndex)
    {
        var node = _nodes[nodeIndex];
        node.NextOptimized = nextIndex;
        _nodes[nodeIndex] = node;
    }

    /// <summary>Trivial chain: every node links to its successor (GPL markOptimized paths).</summary>
    internal void LinkSequentialOptimized()
    {
        for (var i = 0; i < _nodes.Count - 1; i++)
        {
            SetNextOptimized(i, i + 1);
        }
    }

    private static Fix64 Length2D(Fix64 dx, Fix64 dy) => Fix64.Sqrt(dx * dx + dy * dy);

    /// <summary>
    /// GPL Path::computePointOnPath - project pos onto the optimized chain, find the
    /// nearest segment (strict-less, earliest wins), pick the movement goal, and return
    /// the along-path distance remaining to the path end.
    /// <paramref name="grid"/>/<paramref name="surfaces"/> serve the straight-shot
    /// passability probes (GPL isLinePassable).
    /// </summary>
    public void ComputePointOnPath(
        SimPathfindGrid grid, Surfaces surfaces, uint ignoreObstacleId, in FixVector3 pos,
        out FixVector3 posOnPath, out Fix64 distAlongPathToGoal)
    {
        posOnPath = default;
        distAlongPathToGoal = Fix64.Zero;
        if (_nodes.Count == 0)
        {
            _cpopValid = false;
            return;
        }

        // cpop cache: same input (within 0.1 per axis), at most 20 returns.
        if (_cpopValid && _cpopCountdown > 0 &&
            Fix64.Abs(pos.X - _cpopIn.X) <= CpopTolerance &&
            Fix64.Abs(pos.Y - _cpopIn.Y) <= CpopTolerance &&
            Fix64.Abs(pos.Z - _cpopIn.Z) <= CpopTolerance)
        {
            posOnPath = _cpopOutPos;
            distAlongPathToGoal = _cpopOutDist;
            _cpopCountdown--;
            return;
        }
        _cpopCountdown = MaxCpop;

        // Default: the path end.
        posOnPath = _nodes[_nodes.Count - 1].Position;

        var closeIndex = -1;               // start node of the nearest segment
        var closeDistSqr = Fix64.MaxValue;
        var totalLengthBefore = Fix64.Zero; // chain length before the current segment
        var lengthAlongToPos = Fix64.Zero;  // chain length up to the projected point
        var totalLength = Fix64.Zero;       // full chain length

        var prev = 0;
        for (var node = _nodes[0].NextOptimized; node >= 0; node = _nodes[node].NextOptimized)
        {
            var a = _nodes[prev].Position;
            var b = _nodes[node].Position;
            var segDx = b.X - a.X;
            var segDy = b.Y - a.Y;
            var segLen = Length2D(segDx, segDy);

            if (segLen > Fix64.Zero)
            {
                var dirX = segDx / segLen;
                var dirY = segDy / segLen;
                var toPosX = pos.X - a.X;
                var toPosY = pos.Y - a.Y;
                var along = dirX * toPosX + dirY * toPosY;

                var skipSegment = false;
                FixVector3 pointOnPath = default;
                if (along < Fix64.Zero)
                {
                    along = Fix64.Zero;
                    pointOnPath = a;
                }
                else if (along > segLen)
                {
                    if (_nodes[node].NextOptimized < 0)
                    {
                        along = segLen;
                        pointOnPath = b;
                    }
                    else
                    {
                        // Beyond this segment's end and not the last: the next segment
                        // catches the point.
                        skipSegment = true;
                    }
                }
                else
                {
                    pointOnPath = new FixVector3(a.X + along * dirX, a.Y + along * dirY, Fix64.Zero);
                }

                if (!skipSegment)
                {
                    var offX = pos.X - pointOnPath.X;
                    var offY = pos.Y - pointOnPath.Y;
                    var offSqr = offX * offX + offY * offY;
                    if (offSqr < closeDistSqr)
                    {
                        closeDistSqr = offSqr;
                        closeIndex = prev;
                        posOnPath = pointOnPath;
                        lengthAlongToPos = totalLengthBefore + along;
                    }
                }
            }

            totalLengthBefore += segLen;
            totalLength = totalLengthBefore;
            prev = node;
        }

        if (closeIndex >= 0 && _nodes[closeIndex].NextOptimized >= 0)
        {
            var closeNext = _nodes[closeIndex].NextOptimized;
            var a = _nodes[closeIndex].Position;
            var b = _nodes[closeNext].Position;
            var segDx = b.X - a.X;
            var segDy = b.Y - a.Y;
            var segLen = Length2D(segDx, segDy);
            if (segLen > Fix64.Zero)
            {
                var dirX = segDx / segLen;
                var dirY = segDy / segLen;
                var toPosX = pos.X - a.X;
                var toPosY = pos.Y - a.Y;
                var along = dirX * toPosX + dirY * toPosY;
                if (along < Fix64.Zero)
                {
                    along = Fix64.Zero;
                }

                // Off-path error and the blend factor k.
                var toDistSqr = toPosX * toPosX + toPosY * toPosY;
                var offSqr = toDistSqr - along * along;
                var offDist = offSqr <= Fix64.Zero ? Fix64.Zero : Fix64.Sqrt(offSqr);
                var k = offDist / MaxPathError;
                if (k > Fix64.One)
                {
                    k = Fix64.One;
                }

                var gotPos = false;
                if (IsLinePassable(grid, surfaces, ignoreObstacleId, pos, b))
                {
                    posOnPath = b;
                    gotPos = true;

                    var tryAhead = along > segLen * Fix64.Half;
                    if (!_nodes[closeNext].CanOptimize ||
                        _nodes[closeIndex].Layer != _nodes[closeNext].Layer)
                    {
                        tryAhead = false;
                    }
                    var veryClose = segLen - along < NearSegmentEnd;
                    if (veryClose)
                    {
                        tryAhead = true;
                    }
                    if (tryAhead)
                    {
                        var next = _nodes[closeNext].NextOptimized;
                        if (next >= 0)
                        {
                            var c = _nodes[next].Position;
                            var tryPos = new FixVector3(
                                (b.X + c.X) * Fix64.Half,
                                (b.Y + c.Y) * Fix64.Half,
                                b.Z);
                            if (veryClose || IsLinePassable(grid, surfaces, ignoreObstacleId, pos, tryPos))
                            {
                                posOnPath = tryPos;
                            }
                        }
                    }
                }
                else if (k > Fix64.Half)
                {
                    var tryDist = along + Fix64.Half * (segLen - along);
                    var tryPos = new FixVector3(a.X + tryDist * dirX, a.Y + tryDist * dirY, a.Z);
                    if (IsLinePassable(grid, surfaces, ignoreObstacleId, pos, tryPos))
                    {
                        k = Fix64.Half;
                        gotPos = true;
                        posOnPath = tryPos;
                    }
                }

                // on-path (k=0) -> along = segLen; far off (k=1) -> along unchanged.
                along += (Fix64.One - k) * (segLen - along);
                if (!gotPos)
                {
                    if (along > segLen)
                    {
                        posOnPath = b;
                    }
                    else
                    {
                        posOnPath = new FixVector3(a.X + along * dirX, a.Y + along * dirY, a.Z);
                        // Basically standing on the blended point: skip two optimized
                        // nodes ahead (GPL's dx<1 && dy<1 escape).
                        if (Fix64.Abs(pos.X - posOnPath.X) < Fix64.One &&
                            Fix64.Abs(pos.Y - posOnPath.Y) < Fix64.One)
                        {
                            var n1 = _nodes[closeIndex].NextOptimized;
                            if (n1 >= 0 && _nodes[n1].NextOptimized >= 0)
                            {
                                posOnPath = _nodes[_nodes[n1].NextOptimized].Position;
                            }
                        }
                    }
                }
                // NOTE: lengthAlongToPos stays the SCAN's raw projection (GPL uses the
                // scan-time lengthAlongPathToPos for distAlongPath, not the blended along).
            }
        }

        distAlongPathToGoal = totalLength - lengthAlongToPos;
        if (distAlongPathToGoal < Fix64.Zero)
        {
            distAlongPathToGoal = Fix64.Zero;
        }

        // GPL's final clamp: never report less remaining distance than the straight-line
        // distance to the goal point (when meaningfully far from the end).
        var deltaX = posOnPath.X - pos.X;
        var deltaY = posOnPath.Y - pos.Y;
        var lenDelta = Length2D(deltaX, deltaY);
        if (lenDelta > distAlongPathToGoal && distAlongPathToGoal > Fix64.One)
        {
            distAlongPathToGoal = lenDelta;
        }

        _cpopValid = true;
        _cpopIn = pos;
        _cpopOutPos = posOnPath;
        _cpopOutDist = distAlongPathToGoal;
    }

    private Fix64 LengthBeforeNode(int nodeIndex)
    {
        var total = Fix64.Zero;
        var prev = 0;
        for (var node = _nodes[0].NextOptimized; node >= 0; node = _nodes[node].NextOptimized)
        {
            if (prev == nodeIndex)
            {
                return total;
            }
            var a = _nodes[prev].Position;
            var b = _nodes[node].Position;
            total += Length2D(b.X - a.X, b.Y - a.Y);
            prev = node;
        }
        return total;
    }

    /// <summary>
    /// GPL Pathfinder::isLinePassable in its path-follow role: Bresenham over the cells
    /// between two world points, every touched cell must be a valid movement cell.
    /// </summary>
    internal static bool IsLinePassable(
        SimPathfindGrid grid, Surfaces surfaces, uint ignoreObstacleId,
        in FixVector3 fromWorld, in FixVector3 toWorld)
    {
        grid.WorldToCell(fromWorld, out var x0, out var y0);
        grid.WorldToCell(toWorld, out var x1, out var y1);
        return ForEachLineCell(x0, y0, x1, y1,
            (x, y) => grid.IsValidMovementCell(surfaces, x, y, ignoreObstacleId));
    }

    /// <summary>
    /// Integer Bresenham over cell indices (GPL iterateCellsAlongLine's role; fresh
    /// standard implementation - structural fidelity note in the design doc). Calls
    /// <paramref name="visit"/> for every cell including both endpoints; false stops and
    /// propagates.
    /// </summary>
    internal static bool ForEachLineCell(
        int x0, int y0, int x1, int y1, System.Func<int, int, bool> visit)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var stepX = dx >= 0 ? 1 : -1;
        var stepY = dy >= 0 ? 1 : -1;
        dx = dx >= 0 ? dx : -dx;
        dy = dy >= 0 ? dy : -dy;
        var x = x0;
        var y = y0;
        if (!visit(x, y))
        {
            return false;
        }
        var err = dx - dy;
        while (x != x1 || y != y1)
        {
            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += stepX;
            }
            else
            {
                err += dx;
                y += stepY;
            }
            if (!visit(x, y))
            {
                return false;
            }
        }
        return true;
    }

    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Nodes", _nodes, static (IXfer x, ref Node node) =>
        {
            x.XferFixVector3("Position", ref node.Position, Tolerance.Band);
            x.XferInt("NextOptimized", ref node.NextOptimized);
            x.XferBool("CanOptimize", ref node.CanOptimize);
            var layer = (int)node.Layer;
            x.XferInt("Layer", ref layer);
            node.Layer = (byte)layer;
        });
        xfer.XferBool("CpopValid", ref _cpopValid);
        xfer.XferInt("CpopCountdown", ref _cpopCountdown);
        xfer.XferFixVector3("CpopIn", ref _cpopIn, Tolerance.Band);
        xfer.XferFixVector3("CpopOutPos", ref _cpopOutPos, Tolerance.Band);
        xfer.XferFix64("CpopOutDist", ref _cpopOutDist, Tolerance.Band);
    }
}

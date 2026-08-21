// The integer scanline circle the shroud system rasterizes with (GPL
// Common/DiscreteCircle.h/.cpp — semantics reimplemented fresh; the cell sets it emits
// are conformance-relevant because every looker add/remove walks exactly these cells).
//
// Shape of the original, preserved exactly:
//   - Bresenham edge-pair generation for the TOP half only (y from center+radius down
//     to center), producing one horizontal [xStart..xEnd] segment per step;
//   - duplicate-y collapse keeping the LATER (wider) segment;
//   - drawCircle mirrors each segment to 2*yCenter - y, except the center row.
// Pure integer arithmetic - nothing here can diverge across architectures.

using System;
using System.Collections.Generic;
using OpenSage.SimCore;

namespace OpenSage.Logic.Object;

/// <summary>Receives one horizontal run of cells: [xStart..xEnd] inclusive at row y.</summary>
public delegate void CellScanline(int xStart, int xEnd, int y);

[SimState]
public static class DiscreteCellCircle
{
    private struct HorzLine
    {
        public int Y;
        public int XStart;
        public int XEnd;
    }

    /// <summary>
    /// Rasterizes the filled circle of <paramref name="radius"/> cells centered at
    /// (<paramref name="xCenter"/>, <paramref name="yCenter"/>) and calls
    /// <paramref name="scanline"/> once per covered row segment (top half + mirrored
    /// bottom half, center row once).
    /// </summary>
    public static void Draw(int xCenter, int yCenter, int radius, CellScanline scanline)
    {
        ArgumentNullException.ThrowIfNull(scanline);

        var edges = new List<HorzLine>(radius * 2 + 1);

        // GPL DiscreteCircle::generateEdgePairs - Bresenham, top half only.
        var x = 0;
        var y = radius;
        var d = (1 - radius) * 2;
        while (y >= 0)
        {
            edges.Add(new HorzLine { XStart = xCenter - x, XEnd = xCenter + x, Y = yCenter + y });

            if (d + y > 0)
            {
                y--;
                d -= (y * 2) - 1;
            }
            if (x > d)
            {
                x++;
                d += (x * 2) + 1;
            }
        }

        // GPL DiscreteCircle::removeDuplicates - same row twice keeps the later segment.
        for (var i = 0; i < edges.Count - 1;)
        {
            if (edges[i].Y == edges[i + 1].Y)
            {
                edges.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }

        // GPL DiscreteCircle::drawCircle - emit + mirror below the center row.
        var yDoubled = yCenter * 2;
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            scanline(edge.XStart, edge.XEnd, edge.Y);
            if (edge.Y != yCenter)
            {
                scanline(edge.XStart, edge.XEnd, yDoubled - edge.Y);
            }
        }
    }
}

// FIX-1 guard tests: ImageSharpTexture.ForEachPixelRegion must handle both contiguous
// and discontiguous ImageSharp pixel buffers. ImageSharp 2.x backs large images
// (above its internal pooled-buffer size, ~4MB) with multiple discontiguous buffers,
// which made the old whole-image DangerousTryGetSinglePixelMemory path throw
// "Unable to get image pixelspan" during on-demand texture loading (Scene3D.LoadObjects).
// These tests are CPU-only: they exercise the region-walk seam, not the GPU upload.

using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid.ImageSharp;
using Xunit;

namespace OpenSage.Tests.Graphics;

public class ImageSharpTextureRegionTests
{
    [Fact]
    public void SmallContiguousImage_YieldsSingleRegionCoveringAllRows()
    {
        using var image = new Image<Rgba32>(16, 16);

        Assert.True(image.DangerousTryGetSinglePixelMemory(out _));

        var regions = CollectRegions(image);

        var region = Assert.Single(regions);
        Assert.Equal(0, region.StartRow);
        Assert.Equal(16, region.RowCount);
        Assert.Equal(16 * 16, region.PixelCount);
    }

    [Fact]
    public void LargeDiscontiguousImage_FallsBackToPerRowRegions_CoveringEveryRowOnce()
    {
        // 2048x2048 Rgba32 = 16MB, above ImageSharp's default contiguous-buffer limit.
        using var image = new Image<Rgba32>(2048, 2048);

        // Precondition for the fallback path: the buffer really is discontiguous.
        // (If a future ImageSharp version makes this contiguous, the single-region
        // fast path is taken and the crash class this guards against cannot occur.)
        var isContiguous = image.DangerousTryGetSinglePixelMemory(out _);

        var regions = CollectRegions(image);

        if (isContiguous)
        {
            Assert.Single(regions);
        }
        else
        {
            Assert.Equal(2048, regions.Count);
        }

        // Every row covered exactly once, in order, regardless of path.
        var nextRow = 0;
        var totalPixels = 0L;
        foreach (var region in regions)
        {
            Assert.Equal(nextRow, region.StartRow);
            nextRow += region.RowCount;
            totalPixels += region.PixelCount;
        }
        Assert.Equal(2048, nextRow);
        Assert.Equal(2048L * 2048L, totalPixels);
    }

    [Fact]
    public void LargeDiscontiguousImage_RegionContentsMatchWrittenPixels()
    {
        using var image = new Image<Rgba32>(2048, 2048);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var value = (byte)(y % 251);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(value, value, value, 255);
                }
            }
        });

        ImageSharpTexture.ForEachPixelRegion(image, (pixels, startRow, rowCount) =>
        {
            var span = pixels.Span;
            for (var r = 0; r < rowCount; r++)
            {
                var expected = (byte)((startRow + r) % 251);
                // Check first and last pixel of each row in the region.
                Assert.Equal(expected, span[r * image.Width].R);
                Assert.Equal(expected, span[r * image.Width + image.Width - 1].R);
            }
        });
    }

    private static List<(int StartRow, int RowCount, long PixelCount)> CollectRegions(Image<Rgba32> image)
    {
        var regions = new List<(int, int, long)>();
        ImageSharpTexture.ForEachPixelRegion(image, (pixels, startRow, rowCount) =>
        {
            regions.Add((startRow, rowCount, pixels.Length));
        });
        return regions;
    }
}

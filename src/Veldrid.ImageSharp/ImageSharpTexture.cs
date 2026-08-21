using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Veldrid.ImageSharp;

public class ImageSharpTexture
{
    /// <summary>
    /// An array of images, each a single element in the mipmap chain.
    /// The first element is the largest, most detailed level, and each subsequent element
    /// is half its size, down to 1x1 pixel.
    /// </summary>
    public Image<Rgba32>[] Images { get; }

    /// <summary>
    /// The width of the largest image in the chain.
    /// </summary>
    public uint Width => (uint)Images[0].Width;

    /// <summary>
    /// The height of the largest image in the chain.
    /// </summary>
    public uint Height => (uint)Images[0].Height;

    /// <summary>
    /// The pixel format of all images.
    /// </summary>
    public PixelFormat Format { get; }

    /// <summary>
    /// The size of each pixel, in bytes.
    /// </summary>
    public uint PixelSizeInBytes => sizeof(byte) * 4;

    /// <summary>
    /// The number of levels in the mipmap chain. This is equal to the length of the Images array.
    /// </summary>
    public uint MipLevels => (uint)Images.Length;

    public ImageSharpTexture(string path) : this(Image.Load<Rgba32>(path), true) { }
    public ImageSharpTexture(string path, bool mipmap) : this(Image.Load<Rgba32>(path), mipmap) { }
    public ImageSharpTexture(string path, bool mipmap, bool srgb) : this(Image.Load<Rgba32>(path), mipmap, srgb) { }
    public ImageSharpTexture(Stream stream) : this(Image.Load<Rgba32>(stream), true) { }
    public ImageSharpTexture(Stream stream, bool mipmap) : this(Image.Load<Rgba32>(stream), mipmap) { }
    public ImageSharpTexture(Stream stream, bool mipmap, bool srgb) : this(Image.Load<Rgba32>(stream), mipmap, srgb) { }
    public ImageSharpTexture(Image<Rgba32> image, bool mipmap = true) : this(image, mipmap, false) { }
    public ImageSharpTexture(Image<Rgba32> image, bool mipmap, bool srgb)
    {
        Format = srgb ? PixelFormat.R8_G8_B8_A8_UNorm_SRgb : PixelFormat.R8_G8_B8_A8_UNorm;
        if (mipmap)
        {
            Images = MipmapHelper.GenerateMipmaps(image);
        }
        else
        {
            Images = new Image<Rgba32>[] { image };
        }
    }

    public unsafe Texture CreateDeviceTexture(GraphicsDevice gd, ResourceFactory factory)
    {
        return CreateTextureViaUpdate(gd, factory);
    }

    private unsafe Texture CreateTextureViaStaging(GraphicsDevice gd, ResourceFactory factory)
    {
        Texture staging = factory.CreateTexture(
            TextureDescription.Texture2D(Width, Height, MipLevels, 1, Format, TextureUsage.Staging));

        Texture ret = factory.CreateTexture(
            TextureDescription.Texture2D(Width, Height, MipLevels, 1, Format, TextureUsage.Sampled));

        CommandList cl = gd.ResourceFactory.CreateCommandList();
        cl.Begin();
        for (uint level = 0; level < MipLevels; level++)
        {
            Image<Rgba32> image = Images[level];
            MappedResource map = gd.Map(staging, MapMode.Write, level);
            uint rowWidth = (uint)(image.Width * 4);
            ForEachPixelRegion(image, (pixels, startRow, rowCount) =>
            {
                using (var pin = pixels.Pin())
                {
                    if (rowWidth == map.RowPitch)
                    {
                        byte* dstStart = (byte*)map.Data.ToPointer() + startRow * map.RowPitch;
                        Unsafe.CopyBlock(dstStart, pin.Pointer, (uint)(image.Width * rowCount * 4));
                    }
                    else
                    {
                        for (uint y = 0; y < rowCount; y++)
                        {
                            byte* dstStart = (byte*)map.Data.ToPointer() + (startRow + y) * map.RowPitch;
                            byte* srcStart = (byte*)pin.Pointer + y * rowWidth;
                            Unsafe.CopyBlock(dstStart, srcStart, rowWidth);
                        }
                    }
                }
            });
            gd.Unmap(staging, level);

            cl.CopyTexture(
                staging, 0, 0, 0, level, 0,
                ret, 0, 0, 0, level, 0,
                (uint)image.Width, (uint)image.Height, 1, 1);
        }
        cl.End();

        gd.SubmitCommands(cl);
        staging.Dispose();
        cl.Dispose();

        return ret;
    }

    private unsafe Texture CreateTextureViaUpdate(GraphicsDevice gd, ResourceFactory factory)
    {
        Texture tex = factory.CreateTexture(TextureDescription.Texture2D(
            Width, Height, MipLevels, 1, Format, TextureUsage.Sampled));
        for (int level = 0; level < MipLevels; level++)
        {
            Image<Rgba32> image = Images[level];
            uint levelCopy = (uint)level;
            ForEachPixelRegion(image, (pixels, y, rowCount) =>
            {
                using (var pin = pixels.Pin())
                {
                    gd.UpdateTexture(
                        tex,
                        (IntPtr)pin.Pointer,
                        (uint)(PixelSizeInBytes * image.Width * rowCount),
                        0,
                        (uint)y,
                        0,
                        (uint)image.Width,
                        (uint)rowCount,
                        1,
                        levelCopy,
                        0);
                }
            });
        }

        return tex;
    }

    /// <summary>
    /// Invokes <paramref name="action"/> for each contiguous run of pixel rows in
    /// <paramref name="image"/>. For images backed by a single contiguous buffer this is one
    /// call covering the whole image; for large images, whose backing memory ImageSharp
    /// splits into multiple pooled buffers (so DangerousTryGetSinglePixelMemory fails),
    /// this falls back to one call per row. The action receives (pixels, startRow, rowCount).
    /// </summary>
    public static void ForEachPixelRegion(Image<Rgba32> image, Action<Memory<Rgba32>, int, int> action)
    {
        if (image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> wholeImage))
        {
            action(wholeImage, 0, image.Height);
            return;
        }

        for (int y = 0; y < image.Height; y++)
        {
            action(image.DangerousGetPixelRowMemory(y), y, 1);
        }
    }
}

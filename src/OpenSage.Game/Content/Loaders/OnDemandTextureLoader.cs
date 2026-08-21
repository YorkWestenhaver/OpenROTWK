using System;
using System.IO;
using OpenSage.Data.Dds;
using OpenSage.Data.Tga;
using OpenSage.Graphics;
using OpenSage.IO;
using OpenSage.Utilities.Extensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;
using Veldrid.ImageSharp;

namespace OpenSage.Content.Loaders;

internal sealed class OnDemandTextureLoader : IOnDemandAssetLoader<TextureAsset>
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly string[] PossibleFileExtensions = new[]
    {
        ".dds",
        ".tga",
        ".jpg"
    };

    private readonly bool _generateMipMaps;
    private readonly IPathResolver _pathResolver;

    public OnDemandTextureLoader(
        bool generateMipMaps,
        IPathResolver pathResolver)
    {
        _generateMipMaps = generateMipMaps;
        _pathResolver = pathResolver;
    }

    public TextureAsset Load(string name, AssetLoadContext context)
    {
        // Find it in the file system.
        FileSystemEntry entry = null;
        foreach (var path in _pathResolver.GetPaths(name, context.Language))
        {
            foreach (var possibleFileExtension in PossibleFileExtensions)
            {
                var possibleFilePath = Path.ChangeExtension(path, possibleFileExtension);
                entry = context.FileSystem.GetFile(possibleFilePath);
                if (entry != null)
                {
                    break;
                }
            }

            if (entry != null)
            {
                break;
            }
        }

        if (entry == null)
        {
            return null;
        }

        // STANDING RULE: one bad asset never aborts the frame. A texture that fails to
        // decode or upload is logged (once — ScopedAssetCollection caches the result per
        // asset name) and replaced with the shared placeholder texture, mirroring the
        // shadow-pass AptContext.GetTexture fallback.
        Texture texture;
        try
        {
            texture = LoadImpl(entry, context.FileSystem, context.GraphicsDevice);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load texture '{entry.FilePath}' (asset '{name}'); substituting placeholder texture.");
            return new TextureAsset(
                context.StandardGraphicsResources.PlaceholderTexture,
                name,
                ownsTexture: false);
        }

        texture.Name = entry.FilePath;
        return new TextureAsset(texture, name);
    }

    private Texture LoadImpl(FileSystemEntry entry, FileSystem fileSystem, GraphicsDevice graphicsDevice)
    {
        switch (Path.GetExtension(entry.FilePath).ToLowerInvariant())
        {
            case ".dds":
                if (!DdsFile.IsDdsFile(entry))
                {
                    goto case ".tga";
                }
                var ddsFile = DdsFile.FromFileSystemEntry(entry);
                return CreateTextureFromDds(ddsFile, graphicsDevice);

            case ".tga":
                var tgaFile = TgaFile.FromFileSystemEntry(entry);
                return CreateTextureFromTga(tgaFile, graphicsDevice);

            case ".jpg":
                return CreateTextureFromJpg(entry, fileSystem, graphicsDevice);

            default:
                throw new InvalidOperationException();
        }
    }

    private Texture CreateTextureFromDds(DdsFile ddsFile, GraphicsDevice graphicsDevice)
    {
        return graphicsDevice.CreateStaticTexture2D(
            ddsFile.Header.Width,
            ddsFile.Header.Height,
            1,
            ddsFile.MipMaps,
            ddsFile.PixelFormat,
            false);
    }

    private Texture CreateTextureFromTga(TgaFile tgaFile, GraphicsDevice graphicsDevice)
    {
        var rgbaData = TgaFile.ConvertPixelsToRgba8(tgaFile);

        using (var tgaImage = Image.LoadPixelData<Rgba32>(
            rgbaData,
            tgaFile.Header.Width,
            tgaFile.Header.Height))
        {
            var imageSharpTexture = new ImageSharpTexture(
                tgaImage,
                _generateMipMaps);

            return CreateFromImageSharpTexture(imageSharpTexture, graphicsDevice);
        }
    }

    private Texture CreateTextureFromJpg(FileSystemEntry entry, FileSystem fileSystem, GraphicsDevice graphicsDevice)
    {
        ImageSharpTexture colorTexture;
        using (var stream = entry.Open())
        {
            colorTexture = new ImageSharpTexture(stream);
        }

        // Sometimes there's a .png sidecar file which contains the alpha channel.
        using (var stream = entry.Open())
        {
            var pngFilePath = Path.ChangeExtension(entry.FilePath, ".png");
            var pngEntry = fileSystem.GetFile(pngFilePath);
            if (pngEntry != null)
            {
                using var pngStream = pngEntry.Open();

                var alphaTexture = new ImageSharpTexture(pngStream);

                // Per-row access: large images are backed by discontiguous pooled
                // buffers, so a single whole-image pixelspan is not always available.
                var colorImage = colorTexture.Images[0];
                var alphaImage = alphaTexture.Images[0];

                if (colorImage.Width != alphaImage.Width || colorImage.Height != alphaImage.Height)
                {
                    Logger.Warn($"Alpha sidecar '{pngEntry.FilePath}' is {alphaImage.Width}x{alphaImage.Height} but color image '{entry.FilePath}' is {colorImage.Width}x{colorImage.Height}; ignoring alpha channel.");
                }
                else
                {
                    for (var y = 0; y < colorImage.Height; y++)
                    {
                        var colorRow = colorImage.DangerousGetPixelRowMemory(y).Span;
                        var alphaRow = alphaImage.DangerousGetPixelRowMemory(y).Span;
                        for (var x = 0; x < colorRow.Length; x++)
                        {
                            colorRow[x].A = alphaRow[x].A;
                        }
                    }
                }
            }
        }

        return CreateFromImageSharpTexture(colorTexture, graphicsDevice);
    }

    private Texture CreateFromImageSharpTexture(ImageSharpTexture imageSharpTexture, GraphicsDevice graphicsDevice)
    {
        return imageSharpTexture.CreateDeviceTexture(
            graphicsDevice,
            graphicsDevice.ResourceFactory);
    }
}

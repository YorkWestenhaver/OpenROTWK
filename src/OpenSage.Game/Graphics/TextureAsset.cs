using System.IO;
using OpenSage.Data.Dds;
using OpenSage.Utilities.Extensions;
using Veldrid;

namespace OpenSage.Graphics;

public sealed class TextureAsset : BaseAsset
{
    public Texture Texture { get; }

    internal TextureAsset(Texture texture, string name)
        : this(texture, name, ownsTexture: true)
    {
    }

    /// <summary>
    /// <paramref name="ownsTexture"/> = false wraps a shared texture (e.g.
    /// StandardGraphicsResources.PlaceholderTexture) without taking ownership,
    /// so disposing the asset scope does not dispose a texture other systems still use.
    /// </summary>
    internal TextureAsset(Texture texture, string name, bool ownsTexture)
    {
        SetNameAndInstanceId("Texture", name);
        Texture = ownsTexture ? AddDisposable(texture) : texture;
    }

    public static implicit operator Texture(TextureAsset asset) => asset?.Texture;
}

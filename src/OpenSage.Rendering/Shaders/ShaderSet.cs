using System.Reflection;
using Veldrid;

namespace OpenSage.Rendering;

public abstract class ShaderSet : DisposableBase
{
    private ushort _nextMaterialId;

    public readonly ushort Id;
    public readonly ShaderSetDescription Description;
    public readonly ResourceLayout[] ResourceLayouts;

    /// <summary>
    /// For each entry in <see cref="ResourceLayouts"/> that declares no elements, a shared,
    /// empty <see cref="ResourceSet"/> that can be bound to that slot; null for slots whose
    /// layout declares elements.
    /// </summary>
    /// <remarks>
    /// GLSL resource set indices are positional: a shader that uses sets 0 and 3 but neither 1
    /// nor 2 still produces four resource layouts, of which 1 and 2 are empty. A pipeline built
    /// from those layouts declares four resource sets, and backends may require every declared
    /// slot to have something bound before a draw, even the empty padding ones. The
    /// <c>MeshDepth</c> shader used by the shadow pass is exactly that shape, so these
    /// placeholders keep it bindable.
    /// </remarks>
    public readonly ResourceSet?[] EmptyResourceSets;

    public GraphicsDevice GraphicsDevice => Store.GraphicsDevice;

    protected readonly ShaderSetStore Store;

    protected ResourceLayout MaterialResourceLayout => ResourceLayouts[2];

    public ShaderSet(
        ShaderSetStore store,
        Assembly shaderAssembly,
        string shaderName,
        params VertexLayoutDescription[] vertexDescriptors)
    {
        Store = store;

        Id = store.GetNextId();

        var factory = store.GraphicsDevice.ResourceFactory;

        var cacheFile = ShaderCrossCompiler.GetOrCreateCachedShaders(factory, shaderAssembly, shaderName);

        var vertexShader = AddDisposable(factory.CreateShader(cacheFile.VertexShaderDescription));
        vertexShader.Name = $"{shaderName}.vert";

        var fragmentShader = AddDisposable(factory.CreateShader(cacheFile.FragmentShaderDescription));
        fragmentShader.Name = $"{shaderName}.frag";

        Description = new ShaderSetDescription(
            vertexDescriptors,
            new[] { vertexShader, fragmentShader });

        ResourceLayouts = new ResourceLayout[cacheFile.ResourceLayoutDescriptions.Length];
        EmptyResourceSets = new ResourceSet?[cacheFile.ResourceLayoutDescriptions.Length];
        for (var i = 0; i < cacheFile.ResourceLayoutDescriptions.Length; i++)
        {
            ResourceLayouts[i] = AddDisposable(
                factory.CreateResourceLayout(
                    ref cacheFile.ResourceLayoutDescriptions[i]));

            if (NeedsPlaceholderResourceSet(cacheFile.ResourceLayoutDescriptions, i))
            {
                EmptyResourceSets[i] = AddDisposable(
                    factory.CreateResourceSet(
                        new ResourceSetDescription(ResourceLayouts[i])));
                EmptyResourceSets[i]!.Name = $"{shaderName} empty resource set {i}";
            }
        }
    }

    /// <summary>
    /// True when the resource layout at <paramref name="slot"/> declares no elements, and so
    /// needs a shared empty <see cref="ResourceSet"/> bound to it before any draw. See
    /// <see cref="EmptyResourceSets"/> for why such slots exist at all.
    /// </summary>
    public static bool NeedsPlaceholderResourceSet(ResourceLayoutDescription[] descriptions, int slot)
    {
        return slot >= 0
            && slot < descriptions.Length
            && descriptions[slot].Elements.Length == 0;
    }

    internal ushort GetNextMaterialId()
    {
        return checked(_nextMaterialId++);
    }
}

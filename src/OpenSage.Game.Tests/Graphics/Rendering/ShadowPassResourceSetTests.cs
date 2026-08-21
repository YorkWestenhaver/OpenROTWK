using System.IO;
using OpenSage.Graphics.Rendering;
using OpenSage.Rendering;
using Veldrid;
using Veldrid.SPIRV;
using Xunit;

namespace OpenSage.Tests.Graphics.Rendering;

/// <summary>
/// The shadow pass draws with the MeshDepth shader, which uses resource sets 0 and 3 and neither
/// 1 nor 2. GLSL set indices are positional, so the pipeline still declares four resource sets,
/// two of them empty - and Veldrid's Metal backend dereferences whatever is bound to every
/// declared slot, so leaving the empty ones unbound is a null dereference inside
/// MTLCommandList.ActivateGraphicsResourceSet the first time anything casts a shadow.
/// </summary>
public class ShadowPassResourceSetTests
{
    private static ResourceLayoutDescription[] GetMeshDepthResourceLayouts()
    {
        var assembly = typeof(RenderPipeline).Assembly;

        var result = SpirvCompilation.CompileVertexFragment(
            ReadEmbeddedSpv(assembly, "OpenSage.Assets.Shaders.MeshDepth.vert.spv"),
            ReadEmbeddedSpv(assembly, "OpenSage.Assets.Shaders.MeshDepth.frag.spv"),
            CrossCompileTarget.HLSL,
            new CrossCompileOptions());

        return result.Reflection.ResourceLayouts;
    }

    private static byte[] ReadEmbeddedSpv(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    [Fact]
    public void TheDepthShaderDeclaresEmptyResourceSetsItNeverUses()
    {
        var layouts = GetMeshDepthResourceLayouts();

        Assert.Equal(4, layouts.Length);

        // Global constants, bound by the pipeline.
        Assert.NotEmpty(layouts[0].Elements);

        // Pass constants and material constants: declared, never used by this shader. Nothing
        // in the shadow pass has anything to bind here, which is what the placeholders are for.
        Assert.Empty(layouts[1].Elements);
        Assert.Empty(layouts[2].Elements);

        // Render item constants, bound by the mesh's before-render callback.
        Assert.NotEmpty(layouts[3].Elements);
    }

    [Fact]
    public void TheDepthShaderSlotsWithNothingToBindGetPlaceholders()
    {
        var layouts = GetMeshDepthResourceLayouts();

        Assert.False(ShaderSet.NeedsPlaceholderResourceSet(layouts, 0));
        Assert.True(ShaderSet.NeedsPlaceholderResourceSet(layouts, 1));
        Assert.True(ShaderSet.NeedsPlaceholderResourceSet(layouts, 2));
        Assert.False(ShaderSet.NeedsPlaceholderResourceSet(layouts, 3));
    }

    [Fact]
    public void SlotsOutsideTheDeclaredSetsNeedNoPlaceholder()
    {
        var layouts = GetMeshDepthResourceLayouts();

        Assert.False(ShaderSet.NeedsPlaceholderResourceSet(layouts, -1));
        Assert.False(ShaderSet.NeedsPlaceholderResourceSet(layouts, layouts.Length));
    }

    [Fact]
    public void AShaderThatUsesEveryDeclaredSetNeedsNoPlaceholders()
    {
        var element = new ResourceLayoutElementDescription(
            "SomeBuffer",
            ResourceKind.UniformBuffer,
            ShaderStages.Vertex);

        var layouts = new[]
        {
            new ResourceLayoutDescription(element),
            new ResourceLayoutDescription(element),
        };

        Assert.False(ShaderSet.NeedsPlaceholderResourceSet(layouts, 0));
        Assert.False(ShaderSet.NeedsPlaceholderResourceSet(layouts, 1));
    }
}

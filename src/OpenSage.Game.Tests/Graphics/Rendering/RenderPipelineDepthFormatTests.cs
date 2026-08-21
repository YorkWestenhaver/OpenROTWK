using OpenSage.Graphics;
using OpenSage.Graphics.Rendering;
using Veldrid;
using Xunit;

namespace OpenSage.Tests.Graphics.Rendering;

public class RenderPipelineDepthFormatTests
{
    /// <summary>
    /// Apple-family GPUs have no MTLPixelFormatDepth24Unorm_Stencil8, so Veldrid's Metal backend
    /// turns D24_UNorm_S8_UInt into MTLPixelFormatInvalid and Metal's pipeline validation aborts
    /// the process. See boot-crash-metal-r14.md.
    /// </summary>
    [Fact]
    public void MetalUsesAFormatMetalActuallyHas()
    {
        Assert.Equal(PixelFormat.D32_Float_S8_UInt, DepthStencilFormats.ForBackend(GraphicsBackend.Metal));
    }

    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.OpenGL)]
    public void OtherBackendsAreUnchanged(GraphicsBackend backend)
    {
        Assert.Equal(PixelFormat.D24_UNorm_S8_UInt, DepthStencilFormats.ForBackend(backend));
    }

    [Fact]
    public void TheGameOutputDescriptionUsesTheSelectedFormat()
    {
        Assert.Equal(
            DepthStencilFormats.GameDepthStencil,
            RenderPipeline.GameOutputDescription.DepthAttachment.Value.Format);
    }
}

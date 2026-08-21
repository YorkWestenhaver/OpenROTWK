using System.Runtime.InteropServices;
using Veldrid;

namespace OpenSage.Graphics;

/// <summary>
/// Chooses the depth-stencil <see cref="PixelFormat"/> used by the game's framebuffers and by
/// every pipeline built against <c>RenderPipeline.GameOutputDescription</c>.
/// </summary>
/// <remarks>
/// <para>
/// The historical choice, <see cref="PixelFormat.D24_UNorm_S8_UInt"/>, is not universally
/// available. Veldrid's Metal backend maps it to <c>MTLPixelFormatDepth24Unorm_Stencil8</c>, which
/// Apple-family GPUs do not implement; Veldrid then hands <c>MTLPixelFormatInvalid</c> to the
/// render-pipeline descriptor and Metal's validation layer aborts the process:
/// <c>depthAttachmentPixelFormat MTLPixelFormatInvalid is not depth renderable</c>. Because the
/// format is baked into the shared output description, this kills every pipeline the
/// <c>ShaderSetStore</c> creates, i.e. the whole boot.
/// </para>
/// <para>
/// <see cref="PixelFormat.D32_Float_S8_UInt"/> is supported on every Metal device (and is already
/// what the shadow map uses, which is why shadow init never tripped this).
/// </para>
/// <para>
/// The selection is deliberately made from the backend/platform rather than from
/// <c>GraphicsDevice.GetPixelFormatSupport</c>: on Metal that query is unreliable, answering
/// <c>true</c> for formats the device does not have (including <c>R32_Float</c> as a depth-stencil
/// target). See <c>boot-crash-metal-r14.md</c> §4.1.
/// </para>
/// </remarks>
internal static class DepthStencilFormats
{
    /// <summary>
    /// The depth-stencil format used by the game's framebuffers on this machine.
    /// </summary>
    /// <remarks>
    /// The format has to be known before any <see cref="GraphicsDevice"/> exists, because it is
    /// baked into the static output description that the shader set store is constructed with, so
    /// it is derived from the platform rather than from a live device.
    /// </remarks>
    public static readonly PixelFormat GameDepthStencil = SelectGameDepthStencil();

    /// <summary>
    /// The depth-stencil format the game uses on a given backend.
    /// </summary>
    public static PixelFormat ForBackend(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Metal => PixelFormat.D32_Float_S8_UInt,
        _ => PixelFormat.D24_UNorm_S8_UInt,
    };

    private static PixelFormat SelectGameDepthStencil()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Metal is the only backend the engine can actually run on macOS: OpenGL there caps at
            // 4.1 and has no shader storage buffers (RadiusCursorDecals needs them), and Vulkan is
            // MoltenVK layered on top of Metal, so it inherits the same format restriction.
            return ForBackend(GraphicsBackend.Metal);
        }

        // Windows (Direct3D11) and Linux (Vulkan/OpenGL): unchanged behaviour.
        return PixelFormat.D24_UNorm_S8_UInt;
    }
}

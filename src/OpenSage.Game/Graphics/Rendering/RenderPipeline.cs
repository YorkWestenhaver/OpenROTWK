using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Map;
using OpenSage.Graphics.Rendering.Shadows;
using OpenSage.Graphics.Rendering.Water;
using OpenSage.Graphics.Shaders;
using OpenSage.Gui;
using OpenSage.Mathematics;
using OpenSage.Rendering;
using Veldrid;

namespace OpenSage.Graphics.Rendering;

internal sealed class RenderPipeline : DisposableBase
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Render items whose resource sets could not be bound. Each one is logged once and then
    /// skipped for the rest of the process, so a single unrenderable asset can't abort a frame
    /// or spam the log every frame.
    /// </summary>
    private readonly HashSet<string> _unbindableRenderItems = new();

    public event EventHandler<Rendering2DEventArgs> Rendering2D;
    public event EventHandler<BuildingRenderListEventArgs> BuildingRenderList;

    private const int ParallelCullingBatchSize = 128;

    private static readonly RgbaFloat ClearColor = new RgbaFloat(105, 105, 105, 255);

    public static readonly OutputDescription GameOutputDescription = new OutputDescription(
        new OutputAttachmentDescription(DepthStencilFormats.GameDepthStencil),
        new OutputAttachmentDescription(PixelFormat.B8_G8_R8_A8_UNorm));

    private readonly RenderList _renderList;

    private readonly CommandList _commandList;

    private readonly GraphicsLoadContext _loadContext;
    private readonly GlobalShaderResources _globalShaderResources;
    private readonly GlobalShaderResourceData _globalShaderResourceData;

    internal readonly DrawingContext2D DrawingContext;

    private readonly ShadowMapRenderer _shadowMapRenderer;
    private readonly WaterMapRenderer _waterMapRenderer;

    private Texture _intermediateDepthBuffer;
    private Texture _intermediateTexture;
    private Framebuffer _intermediateFramebuffer;

    private readonly TextureCopier _textureCopier;

    public Texture ShadowMap => _shadowMapRenderer.ShadowMap;
    public Texture ReflectionMap => _waterMapRenderer.ReflectionMap;
    public Texture RefractionMap => _waterMapRenderer.RefractionMap;

    public int RenderedObjectsOpaque { get; private set; }
    public int RenderedObjectsTransparent { get; private set; }

    public RenderPipeline(IGame game)
    {
        _renderList = new RenderList();

        var graphicsDevice = game.GraphicsDevice;

        _loadContext = game.GraphicsLoadContext;

        _globalShaderResources = game.GraphicsLoadContext.ShaderResources.Global;
        _globalShaderResourceData = AddDisposable(new GlobalShaderResourceData(game.GraphicsDevice, _globalShaderResources, game.GraphicsLoadContext.StandardGraphicsResources));

        _commandList = AddDisposable(graphicsDevice.ResourceFactory.CreateCommandList());

        DrawingContext = AddDisposable(new DrawingContext2D(
            game.ContentManager,
            game.GraphicsLoadContext,
            BlendStateDescription.SingleAlphaBlend,
            GameOutputDescription));

        _shadowMapRenderer = AddDisposable(new ShadowMapRenderer(game.GraphicsDevice));
        _waterMapRenderer = AddDisposable(new WaterMapRenderer(game.AssetStore, _loadContext, game.GraphicsDevice, game.GraphicsLoadContext.ShaderResources.Global));

        _textureCopier = AddDisposable(new TextureCopier(
            game,
            game.Panel.OutputDescription));
    }

    private void EnsureIntermediateFramebuffer(GraphicsDevice graphicsDevice, Framebuffer target)
    {
        if (_intermediateDepthBuffer != null && _intermediateDepthBuffer.Width == target.Width && _intermediateDepthBuffer.Height == target.Height)
        {
            return;
        }

        RemoveAndDispose(ref _intermediateDepthBuffer);
        RemoveAndDispose(ref _intermediateTexture);
        RemoveAndDispose(ref _intermediateFramebuffer);

        _intermediateDepthBuffer = AddDisposable(graphicsDevice.ResourceFactory.CreateTexture(
            TextureDescription.Texture2D(target.Width, target.Height, 1, 1, DepthStencilFormats.GameDepthStencil, TextureUsage.DepthStencil)));

        _intermediateTexture = AddDisposable(graphicsDevice.ResourceFactory.CreateTexture(
            TextureDescription.Texture2D(target.Width, target.Height, 1, 1, target.ColorTargets[0].Target.Format, TextureUsage.RenderTarget | TextureUsage.Sampled)));

        _intermediateFramebuffer = AddDisposable(graphicsDevice.ResourceFactory.CreateFramebuffer(
            new FramebufferDescription(_intermediateDepthBuffer, _intermediateTexture)));
    }

    public void Execute(RenderContext context)
    {
        RenderedObjectsOpaque = 0;
        RenderedObjectsTransparent = 0;

        EnsureIntermediateFramebuffer(context.GraphicsDevice, context.RenderTarget);

        _renderList.Clear();

        context.Scene3D?.BuildRenderList(
            _renderList,
            context.Scene3D.Camera,
            context.GameTime);

        BuildingRenderList?.Invoke(this, new BuildingRenderListEventArgs(
            _renderList,
            context.Scene3D?.Camera,
            context.GameTime));

        _commandList.Begin();

        if (context.Scene3D != null)
        {
            _commandList.PushDebugGroup("3D Scene");
            Render3DScene(_commandList, context.Scene3D, context);
            _commandList.PopDebugGroup();
        }
        else
        {
            _commandList.SetFramebuffer(_intermediateFramebuffer);
            _commandList.ClearColorTarget(0, ClearColor);
        }

        // GUI and camera-dependent 2D elements
        {
            _commandList.PushDebugGroup("2D Scene");

            DrawingContext.Begin(
                _commandList,
                _loadContext.StandardGraphicsResources.LinearClampSampler,
                new SizeF(context.RenderTarget.Width, context.RenderTarget.Height),
                context.GameTime);

            context.Scene3D?.Render(DrawingContext);
            context.Scene2D?.Render(DrawingContext);

            _shadowMapRenderer.DrawDebugOverlay(
                context.Scene3D,
                DrawingContext);

            Rendering2D?.Invoke(this, new Rendering2DEventArgs(DrawingContext));

            DrawingContext.End();

            _commandList.PopDebugGroup();
        }

        _commandList.End();

        context.GraphicsDevice.SubmitCommands(_commandList);

        _textureCopier.Execute(
            _intermediateTexture,
            context.RenderTarget);
    }

    private void Render3DScene(
        CommandList commandList,
        IScene3D scene,
        RenderContext context)
    {
        Texture cloudTexture;
        if (scene.Lighting.TimeOfDay != TimeOfDay.Night
            && scene.Lighting.EnableCloudShadows
            && scene.Terrain != null)
        {
            cloudTexture = scene.Terrain.CloudTexture;
        }
        else
        {
            cloudTexture = _loadContext.StandardGraphicsResources.SolidWhiteTexture;
        }

        // Shadow map passes.

        commandList.PushDebugGroup("Shadow pass");

        _shadowMapRenderer.RenderShadowMap(
            scene,
            context.GraphicsDevice,
            commandList,
            (framebuffer, lightBoundingFrustum) =>
            {
                commandList.SetFramebuffer(framebuffer);

                commandList.ClearDepthStencil(1);

                commandList.SetFullViewports();

                var shadowViewProjection = lightBoundingFrustum.Matrix;
                _globalShaderResourceData.UpdateGlobalConstantBuffers(commandList, context, shadowViewProjection, null, null);

                DoRenderPass(context, commandList, _renderList.Shadow, lightBoundingFrustum, null);
            });

        commandList.PopDebugGroup();

        // Standard pass.

        commandList.PushDebugGroup("Forward pass");

        var forwardPassResourceSet = _globalShaderResourceData.GetForwardPassResourceSet(
            cloudTexture,
            _shadowMapRenderer.ShadowConstantsPSBuffer,
            _shadowMapRenderer.ShadowMap);

        commandList.SetFramebuffer(_intermediateFramebuffer);

        _globalShaderResourceData.UpdateGlobalConstantBuffers(commandList, context, scene.Camera.ViewProjection, null, null);

        commandList.ClearColorTarget(0, ClearColor);
        commandList.ClearDepthStencil(1);

        commandList.SetFullViewports();

        var standardPassCameraFrustum = scene.Camera.BoundingFrustum;

        commandList.PushDebugGroup("Opaque");
        RenderedObjectsOpaque += DoRenderPass(context, commandList, _renderList.Opaque, standardPassCameraFrustum, forwardPassResourceSet);
        commandList.PopDebugGroup();

        commandList.PushDebugGroup("Transparent");
        RenderedObjectsTransparent = DoRenderPass(context, commandList, _renderList.Transparent, standardPassCameraFrustum, forwardPassResourceSet);
        commandList.PopDebugGroup();

        scene.RenderScene.Render(commandList, _globalShaderResourceData.GlobalConstantsResourceSet, forwardPassResourceSet);

        commandList.PushDebugGroup("Water");
        DoRenderPass(context, commandList, _renderList.Water, standardPassCameraFrustum, forwardPassResourceSet);
        commandList.PopDebugGroup();

        commandList.PopDebugGroup();

        scene.Game.Scripting.CameraFadeOverlay.Render(
            commandList,
            new SizeF(context.RenderTarget.Width, context.RenderTarget.Height));
    }

    private void CalculateWaterShaderMap(IScene3D scene, RenderContext context, CommandList commandList, RenderItem renderItem, ResourceSet forwardPassResourceSet)
    {
        _waterMapRenderer.RenderWaterShaders(
            scene,
            context.GraphicsDevice,
            commandList,
            (reflectionFramebuffer, refractionFramebuffer) =>
            {
                var camera = scene.Camera;
                var clippingOffset = scene.Waters.ClippingOffset;
                var originalFarPlaneDistance = camera.FarPlaneDistance;
                var pivot = renderItem.World.Translation.Y;

                if (refractionFramebuffer != null)
                {
                    commandList.PushDebugGroup("Refraction");
                    camera.FarPlaneDistance = scene.Waters.RefractionRenderDistance;

                    var clippingPlaneTop = new Plane(-Vector3.UnitZ, pivot + clippingOffset);

                    var transparentWaterDepth = scene.AssetLoadContext.AssetStore.WaterTransparency.Current.TransparentWaterDepth;
                    var clippingPlaneBottom = new Plane(Vector3.UnitZ, -pivot + transparentWaterDepth);

                    // Render normal scene for water refraction shader
                    _globalShaderResourceData.UpdateGlobalConstantBuffers(commandList, context, camera.ViewProjection, clippingPlaneTop.AsVector4(), clippingPlaneBottom.AsVector4());

                    commandList.SetFramebuffer(refractionFramebuffer);

                    commandList.ClearColorTarget(0, ClearColor);
                    commandList.ClearDepthStencil(1);

                    commandList.SetFullViewports();

                    RenderedObjectsOpaque += DoRenderPass(context, commandList, _renderList.Opaque, camera.BoundingFrustum, forwardPassResourceSet, clippingPlaneTop, clippingPlaneBottom);
                    commandList.PopDebugGroup();
                }

                if (reflectionFramebuffer != null)
                {
                    commandList.PushDebugGroup("Reflection");
                    camera.FarPlaneDistance = scene.Waters.ReflectionRenderDistance;
                    var clippingPlane = new Plane(Vector3.UnitZ, -pivot - clippingOffset);

                    // TODO: Improve rendering speed somehow?
                    // ------------------- Used for creating stencil mask -------------------
                    _globalShaderResourceData.UpdateGlobalConstantBuffers(commandList, context, camera.ViewProjection, clippingPlane.AsVector4(), null);

                    commandList.SetFramebuffer(reflectionFramebuffer);
                    commandList.ClearColorTarget(0, ClearColor);
                    commandList.ClearDepthStencil(1);

                    // -----------------------------------------------------------------------

                    // Render inverted scene for water reflection shader
                    camera.SetMirrorX(pivot);
                    _globalShaderResourceData.UpdateGlobalConstantBuffers(commandList, context, camera.ViewProjection, clippingPlane.AsVector4(), null);

                    //commandList.SetFramebuffer(reflectionFramebuffer);
                    commandList.ClearColorTarget(0, ClearColor);
                    //commandList.ClearDepthStencil(1);

                    commandList.SetFullViewports();

                    RenderedObjectsOpaque += DoRenderPass(context, commandList, _renderList.Opaque, camera.BoundingFrustum, forwardPassResourceSet, clippingPlane);

                    camera.SetMirrorX(pivot);
                    commandList.PopDebugGroup();
                }

                if (reflectionFramebuffer != null || refractionFramebuffer != null)
                {
                    camera.FarPlaneDistance = originalFarPlaneDistance;
                    _globalShaderResourceData.UpdateGlobalConstantBuffers(commandList, context, camera.ViewProjection, null, null);

                    // Reset the render item pipeline
                    commandList.SetFramebuffer(_intermediateFramebuffer);
                    commandList.InsertDebugMarker("Setting pipeline");
                    commandList.SetPipeline(renderItem.Material.Pipeline);
                }
            });
    }

    private int DoRenderPass(
        RenderContext context,
        CommandList commandList,
        RenderBucket bucket,
        BoundingFrustum cameraFrustum,
        ResourceSet forwardPassResourceSet,
        in Plane? clippingPlane1 = null,
        in Plane? clippingPlane2 = null)
    {
        // TODO: Make culling batch size configurable at runtime
        bucket.RenderItems.CullAndSort(cameraFrustum, clippingPlane1, clippingPlane2, ParallelCullingBatchSize);

        if (bucket.RenderItems.CulledItemIndices.Count == 0)
        {
            return 0;
        }

        int? lastRenderItemIndex = null;

        foreach (var i in bucket.RenderItems.CulledItemIndices)
        {
            ref var renderItem = ref bucket.RenderItems[i];

            commandList.PushDebugGroup($"Render item: {renderItem.DebugName}");

            var passResourceSet = (bucket.RenderItemName == "Shadow")
                ? null
                : forwardPassResourceSet;

            var newMaterial = true;
            if (lastRenderItemIndex != null)
            {
                var lastMaterial = bucket.RenderItems[lastRenderItemIndex.Value].Material;

                newMaterial = lastMaterial.Pipeline != renderItem.Material.Pipeline;
            }

            if (newMaterial)
            {
                commandList.InsertDebugMarker("Setting pipeline");
                commandList.SetPipeline(renderItem.Material.Pipeline);

                // Setting a pipeline invalidates every bound resource set, so the placeholders
                // for the slots nothing else binds have to be re-bound alongside the globals.
                BindPlaceholderResourceSets(commandList, renderItem.Material.ShaderSet);

                SetGlobalResources(commandList, renderItem.Material.ShaderSet, passResourceSet);
            }

            if (bucket.RenderItemName == "Water")
            {
                CalculateWaterShaderMap(context.Scene3D, context, commandList, renderItem, forwardPassResourceSet);

                SetGlobalResources(commandList, renderItem.Material.ShaderSet, passResourceSet);
                commandList.SetGraphicsResourceSet(2, _waterMapRenderer.ResourceSetForRendering);
            }

            renderItem.BeforeRenderCallback.Invoke(commandList, renderItem);

            if (renderItem.Material.MaterialResourceSet != null)
            {
                commandList.SetGraphicsResourceSet(2, renderItem.Material.MaterialResourceSet);
            }

            commandList.SetIndexBuffer(renderItem.IndexBuffer, IndexFormat.UInt16);

            try
            {
                commandList.DrawIndexed(
                    renderItem.IndexCount,
                    1,
                    renderItem.StartIndex,
                    0,
                    0);
            }
            catch (Exception ex)
            {
                // One render item whose resource sets can't be bound - a material missing a
                // resource, or a graphics backend rejecting the binding - must not take the
                // whole frame down with it. Log it once and carry on without it.
                LogUnbindableRenderItem(bucket, renderItem, passResourceSet, ex);
            }

            lastRenderItemIndex = i;

            commandList.PopDebugGroup();
        }

        return bucket.RenderItems.CulledItemIndices.Count;
    }

    private void LogUnbindableRenderItem(
        RenderBucket bucket,
        in RenderItem renderItem,
        ResourceSet passResourceSet,
        Exception exception)
    {
        var key = $"{bucket.RenderItemName}/{renderItem.DebugName}/{renderItem.Material.ShaderSet.GetType().Name}";

        if (!_unbindableRenderItems.Add(key))
        {
            return;
        }

        Logger.Error(
            exception,
            $"Skipping render item '{renderItem.DebugName}' in the {bucket.RenderItemName} pass: " +
            $"its resource sets could not be bound " +
            $"(shader set {renderItem.Material.ShaderSet.GetType().Name}, " +
            $"{renderItem.Material.ShaderSet.ResourceLayouts.Length} resource layouts, " +
            $"material resource set {(renderItem.Material.MaterialResourceSet == null ? "missing" : "present")}, " +
            $"pass resource set {(passResourceSet == null ? "missing" : "present")}). " +
            $"This is logged once per render item.");
    }

    private void SetGlobalResources(CommandList commandList, ShaderSet shaderSet, ResourceSet passResourceSet)
    {
        commandList.SetGraphicsResourceSet(0, _globalShaderResourceData.GlobalConstantsResourceSet);

        // A shader that declares no pass constants - the shadow pass's MeshDepth, for one - gets
        // an empty layout in slot 1, which the pass resource set does not match. Those slots are
        // covered by BindPlaceholderResourceSets instead.
        if (passResourceSet != null && !HasPlaceholderResourceSet(shaderSet, 1))
        {
            commandList.SetGraphicsResourceSet(1, passResourceSet);
        }
    }

    /// <summary>
    /// Binds a shared empty resource set to every slot the shader declares but leaves empty.
    /// GLSL set indices are positional, so a shader using sets 0 and 3 still declares sets 1 and
    /// 2, and a graphics backend may require every declared slot to be bound before a draw.
    /// </summary>
    private static void BindPlaceholderResourceSets(CommandList commandList, ShaderSet shaderSet)
    {
        var emptyResourceSets = shaderSet.EmptyResourceSets;

        for (var slot = 0; slot < emptyResourceSets.Length; slot++)
        {
            var emptyResourceSet = emptyResourceSets[slot];
            if (emptyResourceSet != null)
            {
                commandList.SetGraphicsResourceSet((uint)slot, emptyResourceSet);
            }
        }
    }

    private static bool HasPlaceholderResourceSet(ShaderSet shaderSet, int slot)
    {
        var emptyResourceSets = shaderSet.EmptyResourceSets;

        return slot < emptyResourceSets.Length && emptyResourceSets[slot] != null;
    }
}

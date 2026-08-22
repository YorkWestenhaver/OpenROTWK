using System.Collections.Generic;
using System.Numerics;
using OpenSage.Graphics.Cameras;
using OpenSage.Graphics.Rendering;
using OpenSage.Graphics.Shaders;
using OpenSage.Mathematics;
using Veldrid;

namespace OpenSage.Graphics;

/// <summary>
/// A mesh is composed of the following hierarchy:
///
/// - Mesh: Vertices, Normals, Indices, Materials.
///   - MeshMaterialPasses[]: per-vertex TexCoords,
///                           per-vertex Material indices,
///                           per-triangle Texture indices.
///     - MeshParts[]: One for each unique PipelineState in a material pass.
///                    StartIndex, IndexCount, PipelineState, AlphaTest, Texturing
/// </summary>
public sealed partial class ModelMesh : ModelRenderObject
{
    internal readonly DeviceBuffer VertexBuffer;
    private readonly DeviceBuffer _indexBuffer;

    internal readonly ConstantBuffer<MeshShaderResources.MeshConstants> MeshConstantsBuffer;

    private readonly AxisAlignedBoundingBox _boundingBox;
    public override ref readonly AxisAlignedBoundingBox BoundingBox => ref _boundingBox;

    private readonly BoundingSphere _boundingSphere;
    public override ref readonly BoundingSphere BoundingSphere => ref _boundingSphere;

    public readonly List<ModelMeshPart> MeshParts;

    public readonly bool Skinned;

    public override bool Hidden { get; }
    public readonly bool CameraOriented;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>Once-per-mesh latch for the non-invertible-world-matrix warning.</summary>
    private bool _loggedNonInvertibleWorldMatrix;

    internal override void BuildRenderList(
        RenderList renderList,
        Camera camera,
        ModelInstance modelInstance,
        ModelMeshInstance modelMeshInstance,
        ModelBone parentBone,
        in Matrix4x4 modelTransform,
        bool castsShadow,
        MeshShaderResources.RenderItemConstantsPS? renderItemConstantsPS)
    {
        var meshWorldMatrix = Skinned
            ? modelTransform
            : modelInstance.AbsoluteBoneTransforms[parentBone.Index];

        BuildRenderListWithWorldMatrix(
            renderList,
            camera,
            modelInstance,
            modelMeshInstance,
            parentBone,
            meshWorldMatrix,
            castsShadow,
            renderItemConstantsPS);
    }

    internal override void BuildRenderListWithWorldMatrix(
        RenderList renderList,
        Camera camera,
        ModelInstance modelInstance,
        ModelMeshInstance modelMeshInstance,
        ModelBone parentBone,
        in Matrix4x4 meshWorldMatrix,
        bool castsShadow,
        MeshShaderResources.RenderItemConstantsPS? renderItemConstantsPS = null)
    {
        if (Hidden || !modelInstance.BoneFrameVisibilities[parentBone.Index])
        {
            return;
        }

        // STANDING RULE: one bad asset never aborts the frame. A camera-oriented mesh whose
        // bone world matrix is singular (a zero-scale / collapsed bone, which mod W3Ds do ship
        // and animations do produce transiently) has no inverse; that used to throw out of
        // Matrix4x4Utility.Invert and take the whole render pass - and therefore the game -
        // down mid-frame. Degrade to the un-oriented world matrix instead and log once per mesh.
        Matrix4x4 world;
        if (CameraOriented
            && Matrix4x4Utility.TryInvert(camera.View, out var viewInverse)
            && Matrix4x4Utility.TryInvert(meshWorldMatrix, out var meshWorldInverse))
        {
            // TODO: I don't think this is correct yet.

            var localToWorldMatrix = meshWorldMatrix;

            var cameraPosition = viewInverse.Translation;

            var toCamera = Vector3.Normalize(Vector3.TransformNormal(
                cameraPosition - meshWorldMatrix.Translation,
                meshWorldInverse));

            toCamera.Z = 0;

            var cameraOrientedRotation = Matrix4x4.CreateFromQuaternion(
                QuaternionUtility.CreateRotation(
                    Vector3.UnitX,
                    toCamera));

            world = cameraOrientedRotation * localToWorldMatrix;
        }
        else
        {
            if (CameraOriented && !_loggedNonInvertibleWorldMatrix)
            {
                _loggedNonInvertibleWorldMatrix = true;
                Logger.Warn(
                    $"Camera-oriented mesh '{Name}' has a non-invertible world matrix " +
                    $"(bone '{parentBone.Name}'); rendering it un-oriented. Logged once per mesh.");
            }

            world = meshWorldMatrix;
        }

        var meshBoundingBox = AxisAlignedBoundingBox.Transform(BoundingBox, world); // TODO: Not right for skinned meshes

        for (var i = 0; i < MeshParts.Count; i++)
        {
            var meshPart = MeshParts[i];

            var forceBlendEnabled = renderItemConstantsPS != null && renderItemConstantsPS.Value.Opacity < 1.0f;
            var blendEnabled = meshPart.BlendEnabled || forceBlendEnabled;

            // Depth pass

            // TODO: With more work, we could draw shadows for translucent and alpha-tested materials.
            if (!blendEnabled && castsShadow)
            {
                renderList.Shadow.RenderItems.Add(new RenderItem(
                    Name,
                    modelMeshInstance.MeshPartInstances[i].ModelMeshPart.Material.ShadowPass,
                    meshBoundingBox,
                    world,
                    meshPart.StartIndex,
                    meshPart.IndexCount,
                    _indexBuffer,
                    modelMeshInstance.MeshPartInstances[i].BeforeRenderCallbackDepth));
            }

            // Standard pass

            var renderQueue = blendEnabled
                ? renderList.Transparent
                : renderList.Opaque;

            renderQueue.RenderItems.Add(new RenderItem(
                FullName,
                forceBlendEnabled
                    ? modelMeshInstance.MeshPartInstances[i].ModelMeshPart.MaterialBlend.ForwardPass
                    : modelMeshInstance.MeshPartInstances[i].ModelMeshPart.Material.ForwardPass,
                meshBoundingBox,
                world,
                meshPart.StartIndex,
                meshPart.IndexCount,
                _indexBuffer,
                modelMeshInstance.MeshPartInstances[i].BeforeRenderCallback));
        }
    }
}

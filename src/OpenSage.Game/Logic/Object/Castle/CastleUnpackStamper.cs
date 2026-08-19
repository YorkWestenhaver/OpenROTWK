// Float-substrate half of the castle system (deliberately NOT [SimState], the D-7 boundary
// shape): .bse template resolution, member stamping at rotated offsets, foundation-health
// transfer, and build-plot construction placement. Positions/angles are unmigrated float
// transform substrate, so every crossing lives here and never in the [SimState] castle files.
//
// Behavioral reference: spec-castles.md §2.1 (.bse container), §5.3 (unpack placement:
// member position = castle position + offset rotated by the castle's PlacementViewAngle;
// member angle += castle angle unless DisableStructureRotation), §5.9 (foundation construct).

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Data.Map;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object.Castle;

/// <summary>One member entry of a resolved castle template, transform in float substrate.</summary>
public sealed class CastleMemberPlacement
{
    public string TemplateName { get; init; }
    public Vector3 Offset { get; init; }
    public float Angle { get; init; }

    /// <summary>The raw map object carrying the .bse properties (may be null for synthetic placements).</summary>
    internal MapObject MapObject { get; init; }
}

/// <summary>
/// Resolves a camp name to its member placements. The .bse-backed implementation is the
/// production path; tests inject placements directly (the headless host has no .bse files).
/// </summary>
public interface ICastleTemplateProvider
{
    /// <summary>Placements for <paramref name="campName"/>, or null when the camp cannot be resolved.</summary>
    IReadOnlyList<CastleMemberPlacement> GetPlacements(string campName);
}

/// <summary>
/// The production provider: <c>bases\&lt;camp&gt;\&lt;camp&gt;.bse</c> (spec §2.1), the standard
/// RefPack map-chunk container whose CastleTemplates/ObjectsList chunks our map layer already
/// parses. The BuildLists aggregate channel (Camps.map/Others.map) is not consumed here
/// (BFME1-era; finding F-CAS-7).
/// </summary>
internal sealed class BseCastleTemplateProvider : ICastleTemplateProvider
{
    private readonly IGameEngine _gameEngine;

    public BseCastleTemplateProvider(IGameEngine gameEngine)
    {
        _gameEngine = gameEngine;
    }

    public IReadOnlyList<CastleMemberPlacement> GetPlacements(string campName)
    {
        if (string.IsNullOrEmpty(campName))
        {
            return null;
        }

        var basePath = $"bases\\{campName}\\{campName}.bse";
        var entry = _gameEngine.AssetLoadContext.FileSystem.GetFile(basePath);
        if (entry == null)
        {
            return null;
        }

        var mapFile = MapFile.FromFileSystemEntry(entry);
        if (mapFile.CastleTemplates == null)
        {
            return null;
        }

        var mapObjects = new List<MapObject>(mapFile.ObjectsList.Objects);
        var placements = new List<CastleMemberPlacement>();

        foreach (var castleTemplate in mapFile.CastleTemplates.Templates)
        {
            var mapObject = mapObjects.Find(x => x.TypeName == castleTemplate.TemplateName);

            placements.Add(new CastleMemberPlacement
            {
                TemplateName = castleTemplate.TemplateName,
                Offset = new Vector3(castleTemplate.Offset.X, castleTemplate.Offset.Y, 0.0f),
                Angle = castleTemplate.Angle,
                MapObject = mapObject,
            });
        }

        return placements;
    }
}

internal static class CastleUnpackStamper
{
    /// <summary>
    /// Stamps every member of a camp template around the foundation (spec §5.3 steps 2/5):
    /// placement = foundation position + offset rotated by the foundation's
    /// PlacementViewAngle; member angle adds the castle angle unless DisableStructureRotation.
    /// Members whose template cannot be resolved are skipped (data error, not a crash).
    /// Non-instant unpacks start the normal self-build construction path.
    /// </summary>
    public static List<GameObject> StampMembers(
        GameObject foundation,
        IGameEngine gameEngine,
        IReadOnlyList<CastleMemberPlacement> placements,
        bool instant,
        bool disableStructureRotation)
    {
        var members = new List<GameObject>();
        if (placements == null)
        {
            return members;
        }

        var viewAngle = MathUtility.ToRadians(foundation.Definition.PlacementViewAngle);

        foreach (var placement in placements)
        {
            var definition = gameEngine.AssetLoadContext.AssetStore.ObjectDefinitions.GetByName(placement.TemplateName);
            if (definition == null)
            {
                continue;
            }

            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, viewAngle);
            var offset = Vector4.Transform(
                new Vector4(placement.Offset.X, placement.Offset.Y, 0.0f, 1.0f), rotation).ToVector3();

            var angle = placement.Angle + (disableStructureRotation ? 0.0f : viewAngle);

            GameObject member;
            if (placement.MapObject != null)
            {
                placement.MapObject.Position =
                    new Vector3(foundation.Translation.X, foundation.Translation.Y, 0.0f) + offset;
                member = GameObject.FromMapObject(placement.MapObject, gameEngine, false, angle);
            }
            else
            {
                member = gameEngine.GameLogic.CreateObject(definition, foundation.Owner);
                if (member != null)
                {
                    var position = new Vector3(
                        foundation.Translation.X + offset.X,
                        foundation.Translation.Y + offset.Y,
                        foundation.Translation.Z);
                    member.UpdateTransform(position, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle));
                    member.UpdateColliders();
                }
            }

            if (member == null)
            {
                continue;
            }

            if (!instant)
            {
                StartSelfBuild(member, gameEngine);
            }

            members.Add(member);
        }

        return members;
    }

    /// <summary>
    /// TransferFoundationHealthToCastleUponUnpack (spec §5.3 step 6): the foundation's current
    /// health FRACTION is copied onto the stamped castle anchor. Q7 (is the copy unconditional
    /// in the instant path?) is answered here by gating on the INI flag - recorded as a finding
    /// for a VM test. Retail additionally moves the foundation's script name onto the castle
    /// and renames the foundation "No Name" (0xc30f2f); our GameObject names are immutable
    /// once set, so the name transfer is deferred (finding F-CAS-8).
    /// </summary>
    public static void TransferFoundationHealth(GameObject foundation, GameObject castleAnchor)
    {
        if (foundation?.BodyModule == null || castleAnchor?.BodyModule == null)
        {
            return;
        }

        castleAnchor.BodyModule.SetInitialHealth((int)(
            100.0f * foundation.BodyModule.Health / Math.Max(1.0f, foundation.BodyModule.MaxHealth)));
    }

    /// <summary>
    /// The foundation-construct placement (spec §5.9): spawns the purchased structure at the
    /// plot's position/orientation and starts the BFME self-build (GettingBuiltBehavior) path.
    /// </summary>
    public static GameObject BuildOnFoundation(GameObject plot, IGameEngine gameEngine, ObjectDefinition definition)
    {
        var structure = gameEngine.GameLogic.CreateObject(definition, plot.Owner);
        if (structure == null)
        {
            return null;
        }

        structure.UpdateTransform(plot.Translation, plot.Rotation);
        structure.UpdateColliders();
        StartSelfBuild(structure, gameEngine);

        return structure;
    }

    /// <summary>
    /// Starts the BFME self-build path. PrepareConstruction touches the terrain heightmap
    /// (flattening), which the headless test host does not stand up - the model-condition
    /// half (what IsBeingConstructed reads) is set either way.
    /// </summary>
    private static void StartSelfBuild(GameObject structure, IGameEngine gameEngine)
    {
        if (gameEngine.Terrain != null)
        {
            structure.PrepareConstruction();
        }

        structure.SetIsBeingConstructed();
        structure.BuildProgress = 0.0f;
    }

    /// <summary>
    /// The plot-occupancy probe the order guard uses (same partition shape as
    /// FoundationAIUpdate.CheckForStructure): any live STRUCTURE overlapping the plot's
    /// bounding circle, the plot itself excluded.
    /// </summary>
    public static GameObject FindStructureOnPlot(GameObject plot, IGameEngine gameEngine)
    {
        var radius = plot.Geometry.BoundingCircleRadius;
        var radiusSquared = radius * radius;

        foreach (var candidate in gameEngine.GameLogic.Objects)
        {
            if (candidate == plot
                || candidate.IsDestroyed
                || !candidate.Definition.KindOf.Get(ObjectKinds.Structure)
                || candidate.Definition.KindOf.Get(ObjectKinds.BaseFoundation))
            {
                continue;
            }

            var dx = candidate.Translation.X - plot.Translation.X;
            var dy = candidate.Translation.Y - plot.Translation.Y;
            if (dx * dx + dy * dy <= radiusSquared)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Build cost as sim money (int, F3) - ObjectDefinition.BuildCost is legacy float data.</summary>
    public static uint GetBuildCost(ObjectDefinition definition)
        => definition == null ? 0u : (uint)Math.Max(0f, definition.BuildCost);
}

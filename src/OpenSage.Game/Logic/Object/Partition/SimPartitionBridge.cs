// The one float crossing for the partition system - deliberately NOT [SimState].
//
// A GameObject's transform, geometry and template ranges are still float substrate; this
// bridge quantizes them exactly once, through the F4 wire boundary
// (Fix64.FromWireFloat on the IEEE bits - bit-identical on every machine), and hands the
// [SimState] grid pure values. The same D-7 boundary pattern as SimTransformBridge (S2)
// and CombatLegacyBridge (S1): when the transform/template subsystems migrate to Fix64,
// this file shrinks and dies; the grid does not change.
//
// NOTE (integration seam, finding F-PV-1): SimContext.PartitionAdapter still routes
// ISimContext.Partition through the float quadtree. Rewiring it onto SimPartitionGrid
// (registration inside GameLogic.CreateObject + position pushes from movement) is the
// integrator's flag-day for this system, kept out of this branch so the merge stays
// conflict-free (round instruction: minimal, additive shared-file edits).

using System;
using System.Numerics;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

public static class SimPartitionBridge
{
    private static Fix64 Quantize(float value)
        => Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));

    /// <summary>
    /// Builds the quantized registration facts for a live GameObject: bounding-circle
    /// radius and geometry top from its Geometry, KindOf flags for the fog rules,
    /// owner index from the player roster.
    /// </summary>
    public static PartitionObjectInfo BuildInfo(GameObject gameObject, int ownerPlayerIndex)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        var geometry = gameObject.Geometry;
        return new PartitionObjectInfo(
            gameObject.Id,
            Quantize(geometry.BoundingCircleRadius),
            Quantize(geometry.MaxZ),
            ownerPlayerIndex,
            isImmobile: gameObject.Definition.KindOf.Get(ObjectKinds.Immobile),
            isMine: gameObject.Definition.KindOf.Get(ObjectKinds.Mine),
            revealToAll: gameObject.Definition.KindOf.Get(ObjectKinds.RevealToAll));
    }

    /// <summary>The object's float position, quantized (F4 wire path).</summary>
    public static FixVector3 QuantizePosition(in Vector3 translation)
        => new(Quantize(translation.X), Quantize(translation.Y), Quantize(translation.Z));

    /// <summary>
    /// Registers a GameObject with the grid: shroud-clearing range defaults to
    /// VisionRange when the template gives none (GPL "backwards compatible and
    /// perfectly logical default"); the template's ShroudRevealToAllRange is applied
    /// when positive.
    /// </summary>
    public static SimPartitionEntry Register(
        SimPartitionGrid grid,
        GameObject gameObject,
        int ownerPlayerIndex,
        LogicFrame now)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(gameObject);

        var definition = gameObject.Definition;
        var shroudClearingRange = definition.ShroudClearingRange > 0
            ? Quantize(definition.ShroudClearingRange)
            : Quantize(definition.VisionRange);

        var entry = grid.Register(
            BuildInfo(gameObject, ownerPlayerIndex),
            QuantizePosition(gameObject.Translation),
            shroudClearingRange,
            now);

        if (definition.ShroudRevealToAllRange > 0)
        {
            grid.SetRevealToAllRange(entry, Quantize(definition.ShroudRevealToAllRange), now);
        }

        return entry;
    }

    /// <summary>Pushes a moved GameObject's float position into the grid (quantized once).</summary>
    public static void UpdatePosition(
        SimPartitionGrid grid, SimPartitionEntry entry, GameObject gameObject, LogicFrame now)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(gameObject);
        grid.UpdatePosition(entry, QuantizePosition(gameObject.Translation), now);
    }
}

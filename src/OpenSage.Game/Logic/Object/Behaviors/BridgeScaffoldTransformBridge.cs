// BridgeScaffoldTransformBridge - the ONE float-substrate crossing BridgeScaffoldBehavior
// needs (the D-7 boundary pattern, same shape as Locomotion/SimTransformBridge.cs and
// Update/FloodTransformBridge.cs): [SimState] code in BridgeScaffoldBehavior.cs computes
// every frame's new position in Fix64 and never touches a float; this file is the single
// place that quantizes the object's OWN current transform into the sim (Pull) and writes a
// per-frame Fix64 position back out to the object's GameObject transform for display
// (Push). Not [SimState]: this file is deliberately float-typed and lives outside the
// SIMCORE rule set.
//
// Unlike FloodTransformBridge (which pulls the spawner's position ONCE at spawn and then
// drives separately-spawned MEMBER objects along a cached curve), BridgeScaffoldBehavior
// drives its OWN GameObject every tick while in motion, so PullPosition/PullYaw are read
// fresh on every Update() call rather than cached once - matching the GPL source, which
// reads getObject()->getPosition() at the top of every update() call rather than caching it
// across frames.

using System;
using System.Numerics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

internal static class BridgeScaffoldTransformBridge
{
    private static Fix64 FromFloat(float value) =>
        Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));

    /// <summary>Per-frame ingestion of the scaffold's own current world position (the F4 wire boundary).</summary>
    public static FixVector3 PullPosition(GameObject gameObject)
    {
        var t = gameObject.Transform.Translation;
        return new FixVector3(FromFloat(t.X), FromFloat(t.Y), FromFloat(t.Z));
    }

    public static Fix64 PullYaw(GameObject gameObject) =>
        FromFloat(gameObject.Transform.Yaw);

    /// <summary>
    /// Display write-back: sim Fix64 position -> GameObject float transform. Yaw is passed
    /// through unchanged (the retail update() only ever calls setPosition(), never touches
    /// facing).
    /// </summary>
    public static void Push(GameObject gameObject, in FixVector3 position, Fix64 yaw)
    {
        var translation = new Vector3(
            position.X.ToFloatForDisplay(),
            position.Y.ToFloatForDisplay(),
            position.Z.ToFloatForDisplay());
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw.ToFloatForDisplay());
        gameObject.UpdateTransform(translation, rotation, gameObject.Definition.Scale);
    }
}

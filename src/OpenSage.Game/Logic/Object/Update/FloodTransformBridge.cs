// FloodTransformBridge - the ONE float-substrate crossing FloodUpdate needs (the D-7
// boundary pattern, same shape as Locomotion/SimTransformBridge.cs): [SimState] code in
// FloodUpdate.cs computes every flood-member position/facing in Fix64 and never touches a
// float; this file is the single place that quantizes a spawned member's position into the
// sim (once, at spawn - PullPosition) and writes a per-frame Fix64 curve position back out
// to the member's GameObject transform for display (Push). Not [SimState]: this file is
// deliberately float-typed and lives outside the SIMCORE rule set.
//
// FloodUpdate drives its spawned members' transforms directly rather than through a
// SimLocomotorUpdate: a flood member's path is a scripted Bezier sweep at a fixed
// per-frame arc-length speed (design summary: "moving at configurable speed"), not a
// physics-integrated goal-seek, so there is no locomotor goal state to feed. A member
// template that also carries its own SimLocomotorUpdate would fight this write (last
// module to run in a frame wins the transform) - recorded as a known limitation (finding
// FLOOD-F1) rather than invented away; no clean-room spec or GPL reference exists for this
// module (BFME2-only, unsurveyed) to confirm or deny that shape.

using System;
using System.Numerics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

internal static class FloodTransformBridge
{
    private static Fix64 FromFloat(float value) =>
        Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));

    /// <summary>One-time ingestion of the spawner's world position (the F4 wire boundary).</summary>
    public static FixVector3 PullPosition(GameObject gameObject)
    {
        var t = gameObject.Transform.Translation;
        return new FixVector3(FromFloat(t.X), FromFloat(t.Y), FromFloat(t.Z));
    }

    public static Fix64 PullYaw(GameObject gameObject) =>
        FromFloat(gameObject.Transform.Yaw);

    /// <summary>Display write-back: sim Fix64 transform -> GameObject float transform.</summary>
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

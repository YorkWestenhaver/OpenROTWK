// SimTransformBridge - the ONE float-substrate crossing of the S2 locomotor system
// (the D-7 boundary pattern: crossings live in a non-[SimState] file; [SimState] code
// only ever passes/receives Fix64 values through these methods).
//
// The sim-authoritative transform of a locomotor-driven object is SimPhysics
// (FixVector3 Position + Fix64 Yaw). The GameObject float transform becomes a DISPLAY
// MIRROR of it:
//   - Pull*  : one-time ingestion at module creation - spawn position / map angle /
//              geometry enter the sim through Fix64.FromWireFloat (the F4 wire boundary:
//              a float32 bit pattern decomposed by integer arithmetic), so every peer
//              quantizes the identical bits to identical Fix64.
//   - Push   : per-frame display write-back through ToFloatForDisplay (the F4 display
//              escape). Nothing in the sim ever reads it back.

using System;
using System.Numerics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object.Locomotion;

internal static class SimTransformBridge
{
    private static Fix64 FromFloat(float value) =>
        Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));

    public static FixVector3 PullPosition(GameObject gameObject)
    {
        var t = gameObject.Transform.Translation;
        return new FixVector3(FromFloat(t.X), FromFloat(t.Y), FromFloat(t.Z));
    }

    public static Fix64 PullYaw(GameObject gameObject) =>
        FromFloat(gameObject.Transform.Yaw);

    public static (Fix64 BoundingCircleRadius, Fix64 MajorRadius) PullGeometry(GameObject gameObject)
    {
        var geometry = gameObject.Geometry;
        return (FromFloat(geometry.BoundingCircleRadius), FromFloat(geometry.MajorRadius));
    }

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

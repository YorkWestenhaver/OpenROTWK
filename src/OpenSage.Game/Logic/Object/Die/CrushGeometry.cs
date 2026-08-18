// The float-substrate boundary for the CrushDie port (pilot-autoheal D-7).
//
// CrushDie's crush-point selection is geometry: it reads the victim's position, its 2D unit
// direction vector and its major radius, and compares three squared distances. Those three
// reads come from unmigrated float substrate (Transform / Geometry), so - exactly as the
// pilot localized the partition and Body crossings - the widening happens HERE, in one
// non-[SimState] file, and never inside module code.
//
// The crossing uses Fix64.FromWireFloat, the blessed F4 float32-bit-pattern decomposition
// (integer arithmetic only, truncating toward zero): no float value is ever handed to the
// decision logic, and the same input bits always produce the same Fix64. The result is
// same-binary deterministic today and becomes cross-arch bit-deterministic for free when
// Transform/Geometry migrate to Fix64 - this file is then deleted, not rewritten.
//
// Scope note: reading a float substrate value is not one of F4's two blessed crossings
// (INI text, wire float). It is the third, de-facto one the migration creates, and the
// contract's answer to it is "localize it in a non-[SimState] file" - see CrushDie.md.

using System;
using System.Numerics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

internal static class CrushGeometry
{
    /// <summary>The object's world position, quantized once at the substrate boundary.</summary>
    public static FixVector3 Position(GameObject gameObject) => ToFix(gameObject.Translation);

    /// <summary>
    /// The object's 2D facing (cos yaw, sin yaw, 0), quantized once at the substrate
    /// boundary. The trigonometry itself is still the substrate's (MathF.Cos/Sin on the
    /// transform yaw); it becomes <c>FixTrig</c>'s when Transform migrates.
    /// </summary>
    public static FixVector3 UnitDirection2D(GameObject gameObject) => ToFix(gameObject.UnitDirectionVector2D);

    /// <summary>The object's major geometry radius, quantized once at the substrate boundary.</summary>
    public static Fix64 MajorRadius(GameObject gameObject) => ToFix(gameObject.Geometry.MajorRadius);

    private static Fix64 ToFix(float value) => Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));

    private static FixVector3 ToFix(in Vector3 value) => new(ToFix(value.X), ToFix(value.Y), ToFix(value.Z));
}

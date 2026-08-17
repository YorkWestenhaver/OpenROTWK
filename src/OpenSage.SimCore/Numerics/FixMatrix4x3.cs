// Minimal deterministic object-transform matrix (design-simcore-scaffolding §1.5):
// a 3x3 rotation/scale block plus a translation row, row-vector convention
// (v' = v * M + T). Rotation comes from FixTrig only — never from float trig.

using System;

namespace OpenSage.SimCore.Numerics
{
    public readonly struct FixMatrix4x3 : IEquatable<FixMatrix4x3>
    {
        public readonly Fix64 M11, M12, M13;
        public readonly Fix64 M21, M22, M23;
        public readonly Fix64 M31, M32, M33;
        public readonly Fix64 M41, M42, M43;   // translation

        public FixMatrix4x3(
            Fix64 m11, Fix64 m12, Fix64 m13,
            Fix64 m21, Fix64 m22, Fix64 m23,
            Fix64 m31, Fix64 m32, Fix64 m33,
            Fix64 m41, Fix64 m42, Fix64 m43)
        {
            M11 = m11; M12 = m12; M13 = m13;
            M21 = m21; M22 = m22; M23 = m23;
            M31 = m31; M32 = m32; M33 = m33;
            M41 = m41; M42 = m42; M43 = m43;
        }

        public static readonly FixMatrix4x3 Identity = new FixMatrix4x3(
            Fix64.One, Fix64.Zero, Fix64.Zero,
            Fix64.Zero, Fix64.One, Fix64.Zero,
            Fix64.Zero, Fix64.Zero, Fix64.One,
            Fix64.Zero, Fix64.Zero, Fix64.Zero);

        public FixVector3 Translation => new FixVector3(M41, M42, M43);

        public static FixMatrix4x3 CreateTranslation(in FixVector3 t) => new FixMatrix4x3(
            Fix64.One, Fix64.Zero, Fix64.Zero,
            Fix64.Zero, Fix64.One, Fix64.Zero,
            Fix64.Zero, Fix64.Zero, Fix64.One,
            t.X, t.Y, t.Z);

        /// <summary>Rotation about +Z (the SAGE ground-plane heading axis), radians in Q31.32.</summary>
        public static FixMatrix4x3 CreateRotationZ(Fix64 radians)
        {
            var c = FixTrig.Cos(radians);
            var s = FixTrig.Sin(radians);
            return new FixMatrix4x3(
                c, s, Fix64.Zero,
                -s, c, Fix64.Zero,
                Fix64.Zero, Fix64.Zero, Fix64.One,
                Fix64.Zero, Fix64.Zero, Fix64.Zero);
        }

        /// <summary>Transforms a point: v * M + translation.</summary>
        public FixVector3 Transform(in FixVector3 v) => new FixVector3(
            v.X * M11 + v.Y * M21 + v.Z * M31 + M41,
            v.X * M12 + v.Y * M22 + v.Z * M32 + M42,
            v.X * M13 + v.Y * M23 + v.Z * M33 + M43);

        /// <summary>Transforms a direction (rotation/scale only, no translation).</summary>
        public FixVector3 TransformNormal(in FixVector3 v) => new FixVector3(
            v.X * M11 + v.Y * M21 + v.Z * M31,
            v.X * M12 + v.Y * M22 + v.Z * M32,
            v.X * M13 + v.Y * M23 + v.Z * M33);

        /// <summary>Composition: apply a, then b.</summary>
        public static FixMatrix4x3 Multiply(in FixMatrix4x3 a, in FixMatrix4x3 b) => new FixMatrix4x3(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31,
            a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32,
            a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,

            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31,
            a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32,
            a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,

            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31,
            a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32,
            a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33,

            a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + b.M41,
            a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + b.M42,
            a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + b.M43);

        public static FixMatrix4x3 operator *(in FixMatrix4x3 a, in FixMatrix4x3 b) => Multiply(a, b);

        public bool Equals(FixMatrix4x3 other) =>
            M11 == other.M11 && M12 == other.M12 && M13 == other.M13 &&
            M21 == other.M21 && M22 == other.M22 && M23 == other.M23 &&
            M31 == other.M31 && M32 == other.M32 && M33 == other.M33 &&
            M41 == other.M41 && M42 == other.M42 && M43 == other.M43;

        public override bool Equals(object? obj) => obj is FixMatrix4x3 other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(M11); hash.Add(M12); hash.Add(M13);
            hash.Add(M21); hash.Add(M22); hash.Add(M23);
            hash.Add(M31); hash.Add(M32); hash.Add(M33);
            hash.Add(M41); hash.Add(M42); hash.Add(M43);
            return hash.ToHashCode();
        }

        public static bool operator ==(in FixMatrix4x3 a, in FixMatrix4x3 b) => a.Equals(b);

        public static bool operator !=(in FixMatrix4x3 a, in FixMatrix4x3 b) => !a.Equals(b);
    }
}

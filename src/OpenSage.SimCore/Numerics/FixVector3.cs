// Minimal deterministic 3D vector for the sim core (design-simcore-scaffolding §1.5).
// Standing rule: compare squared lengths through FixMath, don't normalize — use
// NormalizedOrZero only where a direction vector is genuinely required.

using System;

namespace OpenSage.SimCore.Numerics;

public readonly struct FixVector3 : IEquatable<FixVector3>
{
    public readonly Fix64 X;
    public readonly Fix64 Y;
    public readonly Fix64 Z;

    public static readonly FixVector3 Zero = default;

    public FixVector3(Fix64 x, Fix64 y, Fix64 z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static FixVector3 operator +(in FixVector3 a, in FixVector3 b)
        => new FixVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static FixVector3 operator -(in FixVector3 a, in FixVector3 b)
        => new FixVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static FixVector3 operator -(in FixVector3 v)
        => new FixVector3(-v.X, -v.Y, -v.Z);

    public static FixVector3 operator *(in FixVector3 v, Fix64 s)
        => new FixVector3(v.X * s, v.Y * s, v.Z * s);

    public static FixVector3 operator *(Fix64 s, in FixVector3 v)
        => v * s;

    public static Fix64 Dot(in FixVector3 a, in FixVector3 b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static FixVector3 Cross(in FixVector3 a, in FixVector3 b)
        => new FixVector3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

    /// <summary>
    /// Length via the wide raw pipeline: the squared length is never materialized as
    /// a Fix64 (overflow rule R2). Prefer the FixMath comparisons.
    /// </summary>
    public Fix64 Length()
    {
        return Fix64.FromRaw((long)Fix64.SqrtRawWide(LengthSquaredWideRaw()));
    }

    /// <summary>Squared length in raw Q62.64, computed 128-bit wide (rule R2).</summary>
    internal UInt128 LengthSquaredWideRaw()
    {
        var xr = (Int128)X.RawValue;
        var yr = (Int128)Y.RawValue;
        var zr = (Int128)Z.RawValue;
        return (UInt128)(xr * xr) + (UInt128)(yr * yr) + (UInt128)(zr * zr);
    }

    /// <summary>
    /// Unit vector in this direction, or Zero when the length is zero.
    /// Costs three custom divisions and one custom sqrt — use sparingly
    /// (locomotor heading and similar genuinely-directional cases only).
    /// </summary>
    public FixVector3 NormalizedOrZero()
    {
        var length = Length();
        if (length == Fix64.Zero)
        {
            return Zero;
        }
        return new FixVector3(X / length, Y / length, Z / length);
    }

    public bool Equals(FixVector3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is FixVector3 other && Equals(other);

    public override int GetHashCode() =>
        DeterministicHash.Combine(X.RawValue, Y.RawValue, Z.RawValue);

    public static bool operator ==(in FixVector3 a, in FixVector3 b) => a.Equals(b);

    public static bool operator !=(in FixVector3 a, in FixVector3 b) => !a.Equals(b);

    public override string ToString() => $"({X}, {Y}, {Z})";
}

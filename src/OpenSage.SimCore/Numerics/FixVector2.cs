// Minimal deterministic 2D vector for the sim core (design-simcore-scaffolding §1.5).
// Only what tick code demonstrably needs — this is not a general math library.

using System;

namespace OpenSage.SimCore.Numerics
{
    public readonly struct FixVector2 : IEquatable<FixVector2>
    {
        public readonly Fix64 X;
        public readonly Fix64 Y;

        public static readonly FixVector2 Zero = default;

        public FixVector2(Fix64 x, Fix64 y)
        {
            X = x;
            Y = y;
        }

        public static FixVector2 operator +(in FixVector2 a, in FixVector2 b)
            => new FixVector2(a.X + b.X, a.Y + b.Y);

        public static FixVector2 operator -(in FixVector2 a, in FixVector2 b)
            => new FixVector2(a.X - b.X, a.Y - b.Y);

        public static FixVector2 operator -(in FixVector2 v)
            => new FixVector2(-v.X, -v.Y);

        public static FixVector2 operator *(in FixVector2 v, Fix64 s)
            => new FixVector2(v.X * s, v.Y * s);

        public static FixVector2 operator *(Fix64 s, in FixVector2 v)
            => v * s;

        public static Fix64 Dot(in FixVector2 a, in FixVector2 b)
            => a.X * b.X + a.Y * b.Y;

        /// <summary>
        /// Length via the wide raw pipeline: the squared length is never materialized as
        /// a Fix64 (overflow rule R2). Prefer FixMath comparisons where possible.
        /// </summary>
        public Fix64 Length()
        {
            var xr = (Int128)X.RawValue;
            var yr = (Int128)Y.RawValue;
            var sq = (UInt128)(xr * xr) + (UInt128)(yr * yr);   // Q62.64, non-negative
            return Fix64.FromRaw((long)Fix64.SqrtRawWide(sq));
        }

        public bool Equals(FixVector2 other) => X == other.X && Y == other.Y;

        public override bool Equals(object? obj) => obj is FixVector2 other && Equals(other);

        public override int GetHashCode() => DeterministicHash.Combine(X.RawValue, Y.RawValue);

        public static bool operator ==(in FixVector2 a, in FixVector2 b) => a.Equals(b);

        public static bool operator !=(in FixVector2 a, in FixVector2 b) => !a.Equals(b);

        public override string ToString() => $"({X}, {Y})";
    }
}

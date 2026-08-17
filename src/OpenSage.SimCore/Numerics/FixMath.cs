// Deterministic scalar helpers and the R2 wide-compare range library
// (design-simcore-scaffolding §1.2/§1.5, api-freeze-v1 F3).
//
// Squares of data-driven sentinel ranges (AttackRange 9,999,999 => rangeSq 1e14) overflow
// Q31.32, so every distance-vs-range comparison here is computed in 128-bit raw space
// (raw*raw is Q62.64) and a Fix64 square is never materialized.
//
// Also home of the integer Min/Max/Clamp equivalents that keep analyzer rule SIMCORE002
// (System.Math banned wholesale) zero-exception.

using System;

namespace OpenSage.SimCore.Numerics
{
    public static class FixMath
    {
        // ------------------------------------------------------------------
        // Integer helpers (System.Math replacements inside the quarantine)
        // ------------------------------------------------------------------

        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;

        public static long Min(long a, long b) => a < b ? a : b;
        public static long Max(long a, long b) => a > b ? a : b;
        public static long Clamp(long value, long min, long max)
            => value < min ? min : value > max ? max : value;

        public static uint Min(uint a, uint b) => a < b ? a : b;
        public static uint Max(uint a, uint b) => a > b ? a : b;

        public static Fix64 Min(Fix64 a, Fix64 b) => a < b ? a : b;
        public static Fix64 Max(Fix64 a, Fix64 b) => a > b ? a : b;
        public static Fix64 Clamp(Fix64 value, Fix64 min, Fix64 max)
            => value < min ? min : value > max ? max : value;

        // ------------------------------------------------------------------
        // R2 wide-compare range library
        // ------------------------------------------------------------------

        /// <summary>
        /// True when the distance between a and b is &lt;= range. Both sides are computed
        /// in 128-bit raw arithmetic (Q62.64); a Fix64 square is never materialized, so
        /// sentinel ranges up to 9,999,999 compare exactly (rule R2). A negative range
        /// contains nothing.
        /// </summary>
        public static bool IsWithin(in FixVector3 a, in FixVector3 b, Fix64 range)
        {
            if (range < Fix64.Zero)
            {
                return false;
            }
            var rangeRaw = (UInt128)(ulong)range.RawValue;
            return DistanceSquaredWideRaw(a, b) <= rangeRaw * rangeRaw;
        }

        /// <summary>
        /// Compares |a−b| against |c−d| without materializing either square:
        /// −1 when |a−b| &lt; |c−d|, 0 when equal, +1 when greater.
        /// </summary>
        public static int CompareDistance(in FixVector3 a, in FixVector3 b, in FixVector3 c, in FixVector3 d)
        {
            var left = DistanceSquaredWideRaw(a, b);
            var right = DistanceSquaredWideRaw(c, d);
            return left < right ? -1 : left > right ? 1 : 0;
        }

        /// <summary>
        /// The actual distance value, for the cases that truly need it (most callers
        /// should use IsWithin / CompareDistance). Computed as the 128-bit square root
        /// of the Q62.64 wide squared distance, which lands directly in Q31.32 raw —
        /// no Fix64 square is ever formed.
        /// </summary>
        public static Fix64 Distance(in FixVector3 a, in FixVector3 b)
        {
            return Fix64.FromRaw((long)Fix64.SqrtRawWide(DistanceSquaredWideRaw(a, b)));
        }

        // Squared distance in raw Q62.64, 128-bit wide throughout. Component deltas are
        // taken on raw longs in Int128 (no saturating Fix64 subtraction), so the result
        // is exact for any pair of representable points.
        private static UInt128 DistanceSquaredWideRaw(in FixVector3 a, in FixVector3 b)
        {
            var dx = SquareDelta(a.X.RawValue, b.X.RawValue);
            var dy = SquareDelta(a.Y.RawValue, b.Y.RawValue);
            var dz = SquareDelta(a.Z.RawValue, b.Z.RawValue);
            return AddSaturating(AddSaturating(dx, dy), dz);
        }

        private static UInt128 SquareDelta(long a, long b)
        {
            var d = (Int128)a - b;                       // |d| <= 2^64 − 1
            var magnitude = (UInt128)(d < 0 ? -d : d);
            return magnitude * magnitude;                // <= (2^64 − 1)^2: fits UInt128
        }

        // The three squares can theoretically exceed 2^128 combined (only for points at
        // opposite representable extremes); saturate deterministically instead of wrapping.
        private static UInt128 AddSaturating(UInt128 a, UInt128 b)
        {
            var sum = a + b;
            return sum < a ? UInt128.MaxValue : sum;
        }
    }
}

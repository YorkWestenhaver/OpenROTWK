// SIMCORE-EXEMPT: guess-accelerator, result guess-independent, see design-simcore-scaffolding §1.4
//
// Division for the vendored Fix64 (api-freeze-v1 F2). The hardware double division is used
// ONLY as a first guess; the integer fixup below adjusts the quotient until the exact
// Euclidean remainder invariant holds, so the result is identical for any starting guess
// (CI-proven equal to the pure-integer DivideReference over edge cases plus a large
// splitmix64-driven random corpus — see DivSqrtEquivalenceTests).

using System;

namespace OpenSage.SimCore.Numerics
{
    public readonly partial struct Fix64
    {
        /// <summary>
        /// Deterministic Q31.32 division. Semantics: the unique quotient q with
        /// <c>x.Raw · 2^32 = q · y.Raw + r</c> and <c>0 &lt;= r &lt; |y.Raw|</c>
        /// (Euclidean division), saturating to MinValue/MaxValue on overflow.
        /// </summary>
        /// <exception cref="DivideByZeroException">y is zero.</exception>
        public static Fix64 operator /(Fix64 x, Fix64 y)
        {
            var b = y.m_rawValue;
            if (b == 0)
            {
                throw new DivideByZeroException();
            }

            var n = (Int128)x.m_rawValue << FRACTIONAL_PLACES;

            // Hardware-double guess. IEEE-754 double division is correctly rounded on every
            // conforming CPU, but nothing below depends on that: any guess converges to the
            // same quotient. |guess| <= 2^95, always finite, always within Int128 range.
            var q = (Int128)((double)x.m_rawValue * 4294967296.0 / b);

            // Integer fixup: adjust q until 0 <= r < |b|. Each double-guessed step shrinks
            // |r| multiplicatively; the tail is a handful of ±1 steps.
            var r = n - q * b;
            var absB = (Int128)(b > 0 ? (ulong)b : (ulong)(-(Int128)b));
            while (r < 0 || r >= absB)
            {
                var step = (Int128)((double)r / b);
                if (step == 0)
                {
                    // Only reachable when r < 0 and |r| < |b|: move one quotient step
                    // toward making r non-negative.
                    step = b > 0 ? -1 : 1;
                }
                q += step;
                r -= step * b;
            }

            return SaturateRaw(q);
        }

        /// <summary>
        /// Pure-integer reference implementation of <see cref="op_Division"/>, using only
        /// Int128 integer arithmetic. Identical semantics (Euclidean quotient, saturating).
        /// Exists solely so CI can prove the guess-accelerated operator equivalent.
        /// </summary>
        internal static Fix64 DivideReference(Fix64 x, Fix64 y)
        {
            var b = y.m_rawValue;
            if (b == 0)
            {
                throw new DivideByZeroException();
            }

            var n = (Int128)x.m_rawValue << FRACTIONAL_PLACES;
            var q = n / b;              // truncated toward zero
            var r = n - q * b;
            if (r < 0)
            {
                // Convert truncated quotient to Euclidean: force 0 <= r < |b|.
                q -= b > 0 ? 1 : -1;
            }

            return SaturateRaw(q);
        }

        private static Fix64 SaturateRaw(Int128 raw)
        {
            if (raw > long.MaxValue)
            {
                return MaxValue;
            }
            if (raw < long.MinValue)
            {
                return MinValue;
            }
            return new Fix64((long)raw);
        }
    }
}

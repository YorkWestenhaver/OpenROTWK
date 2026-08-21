// SIMCORE-EXEMPT: guess-accelerator, result guess-independent, see design-simcore-scaffolding §1.4
//
// Square root for the vendored Fix64 (api-freeze-v1 F2). Math.Sqrt supplies ONLY a first
// guess; the integer fixup walks it to the exact floor square root, so the result is
// identical for any starting guess (CI-proven equal to the vendored pure-integer
// digit-by-digit SqrtReference — see DivSqrtEquivalenceTests).
//
// SqrtReference is the classic restoring digit-by-digit integer square root (the
// FixedMath.Net/libfixmath method — see Fix64.cs header for provenance and Apache-2.0
// attribution) run at full 128-bit width. The vendored original's two-phase 64-bit
// narrowing was NOT kept: its low-half phase deviates from true round-to-nearest on
// roughly 1e-6 of inputs (found by this file's own equivalence corpus; see
// scaffolding-log.md step 1 findings). Both paths here implement exact
// round-to-nearest of sqrt(raw << 32), ties-up.

using System;

namespace OpenSage.SimCore.Numerics;

public readonly partial struct Fix64
{
    /// <summary>
    /// Deterministic Q31.32 square root, rounded to nearest raw ulp.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The argument was negative.</exception>
    public static Fix64 Sqrt(Fix64 x)
    {
        var xl = x.m_rawValue;
        if (xl < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Negative value passed to Sqrt");
        }

        return new Fix64((long)SqrtRawWide((UInt128)(ulong)xl << FRACTIONAL_PLACES));
    }

    /// <summary>
    /// Round-to-nearest integer square root of a 128-bit target: the s minimizing
    /// |s² − t| (ties resolved upward, matching the vendored reference: round up
    /// exactly when t − floor² &gt; floor). Also used by FixMath.Distance to take
    /// sqrt of a Q62.64 squared-distance without materializing a Fix64 square.
    /// </summary>
    internal static ulong SqrtRawWide(UInt128 t)
    {
        if (t == 0)
        {
            return 0;
        }

        // Hardware-double guess (correctly rounded IEEE-754 sqrt everywhere, but the
        // fixup below is what guarantees the answer, not the guess).
        var s = (ulong)Math.Sqrt((double)t);

        // Integer fixup to the exact floor square root. The guess is within a few
        // ulps, so these loops run at most a handful of iterations.
        while (s > 0 && (UInt128)s * s > t)
        {
            s--;
        }
        while ((UInt128)(s + 1) * (s + 1) <= t)
        {
            s++;
        }

        // Round to nearest: remainder greater than s means (s + 0.5)² < t.
        if (t - (UInt128)s * s > s)
        {
            s++;
        }
        return s;
    }

    /// <summary>
    /// Pure-integer reference implementation: the vendored digit-by-digit restoring
    /// algorithm at full 128-bit width, with the identical explicit nearest rounding.
    /// Exists solely so CI can prove the guess-accelerated <see cref="Sqrt"/> equivalent.
    /// </summary>
    internal static Fix64 SqrtReference(Fix64 x)
    {
        var xl = x.m_rawValue;
        if (xl < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Negative value passed to Sqrt");
        }

        return new Fix64((long)SqrtRawWideReference((UInt128)(ulong)xl << FRACTIONAL_PLACES));
    }

    /// <summary>
    /// Pure-integer 128-bit restoring square root, round-to-nearest — the reference
    /// for <see cref="SqrtRawWide"/>.
    /// </summary>
    internal static ulong SqrtRawWideReference(UInt128 t)
    {
        var num = t;
        var result = (UInt128)0;

        // second-to-top bit
        var bit = (UInt128)1 << 126;

        while (bit > num)
        {
            bit >>= 2;
        }

        while (bit != 0)
        {
            if (num >= result + bit)
            {
                num -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result = result >> 1;
            }
            bit >>= 2;
        }

        // If the next bit would have been 1, round the result upwards.
        if (num > result)
        {
            ++result;
        }
        return (ulong)result;
    }
}

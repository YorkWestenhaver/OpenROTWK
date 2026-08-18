// SPIKE (softfloat-oracle): division and square root for the vendored SoftFloat.
//
// The vendored CodesInChaos SoftFloat implements only + - * (fix64-benchmark §D noted the
// gap); an x87-mimicking oracle mode needs the full arithmetic set. These are fresh
// implementations of the textbook IEEE-754 binary32 algorithms (unpack -> integer
// long-division / integer square root -> round-to-nearest-even -> pack), written for this
// spike and validated exhaustively-at-random against hardware IEEE single-precision
// (correctly rounded per IEEE 754 for / and sqrt on every .NET target), plus explicit
// special-case tests.
//
// Semantics target: IEEE binary32, round-to-nearest-even, full subnormal support.
// NaN payloads are canonicalized to SoftFloat.NaN (0xFFC00000) rather than propagated —
// x87 payload propagation rules are a later oracle-conformance step; the game's CRC never
// hashes NaNs in healthy state.

namespace OpenSage.Mathematics;

public readonly partial struct SoftFloat
{
    public static SoftFloat operator /(SoftFloat f1, SoftFloat f2)
    {
        var rawExp1 = f1.RawExponent;
        var rawExp2 = f2.RawExponent;
        var sign = (f1._raw ^ f2._raw) & SignMask;

        // Specials.
        if (rawExp1 == 255)
        {
            if (f1.RawMantissa != 0)
            {
                return NaN; // NaN / x
            }
            if (rawExp2 == 255)
            {
                return NaN; // inf / inf, inf / NaN
            }
            return new SoftFloat(sign | RawPositiveInfinity); // inf / finite
        }
        if (rawExp2 == 255)
        {
            if (f2.RawMantissa != 0)
            {
                return NaN; // x / NaN
            }
            return new SoftFloat(sign); // finite / inf = signed zero
        }

        var man1 = f1.RawMantissa;
        var man2 = f2.RawMantissa;

        var isZero1 = rawExp1 == 0 && man1 == 0;
        var isZero2 = rawExp2 == 0 && man2 == 0;
        if (isZero2)
        {
            return isZero1 ? NaN : new SoftFloat(sign | RawPositiveInfinity); // 0/0 : x/0
        }
        if (isZero1)
        {
            return new SoftFloat(sign); // 0 / x = signed zero
        }

        // Unpack to normalized (mantissa in [2^23, 2^24), unbiased exponent of the implied
        // binary point after bit 23).
        var exp1 = NormalizeOperand(rawExp1, ref man1);
        var exp2 = NormalizeOperand(rawExp2, ref man2);

        // Quotient of the 24-bit significands with 31 extra bits of precision:
        // num in [2^54, 2^55), q = num / man2 in (2^30, 2^32).
        var num = (ulong)man1 << 31;
        var q = num / man2;
        var rem = num % man2;

        var resultExp = exp1 - exp2 + ExponentBias;

        uint man24;
        uint roundBits;
        int roundBitCount;
        if (q >= (1UL << 31))
        {
            // significand quotient in [1, 2)
            man24 = (uint)(q >> 8);
            roundBits = (uint)q & 0xFF;
            roundBitCount = 8;
        }
        else
        {
            // significand quotient in [0.5, 1): one less bit, exponent down one.
            resultExp -= 1;
            man24 = (uint)(q >> 7);
            roundBits = (uint)q & 0x7F;
            roundBitCount = 7;
        }

        return RoundAndPack(sign, resultExp, man24, roundBits, roundBitCount, sticky: rem != 0);
    }

    /// <summary>IEEE binary32 square root, round-to-nearest-even.</summary>
    public static SoftFloat Sqrt(SoftFloat f)
    {
        var rawExp = f.RawExponent;
        var man = f.RawMantissa;

        if (rawExp == 255)
        {
            if (man != 0 || (f._raw & SignMask) != 0)
            {
                return NaN; // NaN, -inf
            }
            return PositiveInfinity; // +inf
        }
        if (rawExp == 0 && man == 0)
        {
            return f; // +-0 preserved
        }
        if ((f._raw & SignMask) != 0)
        {
            return NaN; // negative
        }

        var exp = NormalizeOperand(rawExp, ref man); // value = man * 2^(exp - 23), man in [2^23, 2^24)

        // value = man * 2^e with e = exp - 23. Make e even so sqrt(2^e) is exact.
        var e = exp - 23;
        var m = (ulong)man;
        if ((e & 1) != 0)
        {
            m <<= 1;
            e -= 1;
        }

        // Scale by an even power to get >= 25 result bits: sqrt(m << 28) = sqrt(m) << 14.
        // m <= 2^25, so m << 28 <= 2^53; isqrt <= 2^26.5 -> 26..27 bits.
        m <<= 28;
        e -= 28;
        var s = IntegerSqrt(m);
        var rem2 = m - s * s;

        // result = s * 2^(e/2), s has 26 or 27 significant bits.
        var halfE = e >> 1;
        uint man24;
        uint roundBits;
        int roundBitCount;
        if (s >= (1UL << 26))
        {
            man24 = (uint)(s >> 3);
            roundBits = (uint)s & 0x7;
            roundBitCount = 3;
            halfE += 3;
        }
        else
        {
            man24 = (uint)(s >> 2);
            roundBits = (uint)s & 0x3;
            roundBitCount = 2;
            halfE += 2;
        }

        var resultExp = halfE + 23 + ExponentBias;
        return RoundAndPack(0, resultExp, man24, roundBits, roundBitCount, sticky: rem2 != 0);
    }

    /// <summary>
    /// Brings a possibly-subnormal operand to normalized form (mantissa in [2^23, 2^24))
    /// and returns its unbiased exponent.
    /// </summary>
    private static int NormalizeOperand(byte rawExp, ref uint man)
    {
        if (rawExp != 0)
        {
            man |= 0x800000;
            return rawExp - ExponentBias;
        }

        // Subnormal: value = man * 2^-149; shift until the implied bit is set.
        var exp = 1 - ExponentBias;
        while (man < 0x800000)
        {
            man <<= 1;
            exp -= 1;
        }
        return exp;
    }

    /// <summary>
    /// Rounds a 24-bit significand (with <paramref name="roundBitCount"/> extra low bits in
    /// <paramref name="roundBits"/> and a sticky flag) to nearest-even and packs sign,
    /// biased exponent and mantissa, handling overflow to infinity and underflow to
    /// subnormals/zero.
    /// </summary>
    private static SoftFloat RoundAndPack(uint sign, int biasedExp, uint man24, uint roundBits, int roundBitCount, bool sticky)
    {
        if (biasedExp <= 0)
        {
            // Underflow: denormalize man24 (plus its round bits) until the exponent is 1,
            // then round with the shifted-out bits.
            var shift = 1 - biasedExp;
            if (shift > 40)
            {
                // All significand bits are far below the subnormal range: rounds to zero
                // (guard is zero, sticky set). 40 keeps totalFrac well inside 64 bits.
                return new SoftFloat(sign);
            }

            // Merge round bits into a single 32-bit significand-with-fraction, then shift.
            var wide = ((ulong)man24 << roundBitCount) | roundBits;
            var totalFrac = roundBitCount + shift;
            man24 = (uint)(wide >> totalFrac);
            var mask = (1UL << totalFrac) - 1;
            var frac = wide & mask;
            var half = 1UL << (totalFrac - 1);
            sticky |= (frac & (half - 1)) != 0;
            var guard = (frac & half) != 0;

            var mant = man24;
            if (guard && (sticky || (mant & 1) != 0))
            {
                mant += 1; // may carry into the implicit-bit position: that IS the normal boundary, handled by packing below.
            }
            return new SoftFloat(sign | mant); // biased exponent 0; mant == 0x800000 packs as exp 1, mantissa 0 — correct.
        }

        {
            var half = 1u << (roundBitCount - 1);
            sticky |= (roundBits & (half - 1)) != 0;
            var guard = (roundBits & half) != 0;
            if (guard && (sticky || (man24 & 1) != 0))
            {
                man24 += 1;
                if (man24 == (1u << 24))
                {
                    man24 >>= 1;
                    biasedExp += 1;
                }
            }
        }

        if (biasedExp >= 255)
        {
            return new SoftFloat(sign | RawPositiveInfinity);
        }

        return new SoftFloat(sign | ((uint)biasedExp << MantissaBits) | (man24 & 0x7FFFFF));
    }

    /// <summary>Floor of the square root of a 64-bit integer, bit-by-bit (no float use).</summary>
    private static ulong IntegerSqrt(ulong value)
    {
        ulong result = 0;
        ulong bit = 1UL << 62;
        while (bit > value)
        {
            bit >>= 2;
        }
        while (bit != 0)
        {
            if (value >= result + bit)
            {
                value -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }
            bit >>= 2;
        }
        return result;
    }
}

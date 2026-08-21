// Vendored and modified from FixedMath.Net (https://github.com/asik/FixedMath.Net,
// commit b2adac7713eda01fdd31578dd5a1d15f8f7ba067), Copyright 2012 André Slupik,
// licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0).
// See THIRD-PARTY-NOTICES.md at the repository root for the full attribution.
//
// Modifications for OpenSage.SimCore (design-simcore-scaffolding §1.1, api-freeze-v1 F1):
//  - removed operator / and Sqrt (replaced by guess+fixup implementations in
//    Fix64.Division.cs / Fix64.Sqrt.cs with CI-proven pure-integer reference equivalence);
//  - removed all trigonometry (Sin/Cos/Tan/Atan/Atan2/Acos and the interpolated LUTs) —
//    replaced by the baked-table FixTrig class;
//  - removed Pow/Pow2/Log2/Ln (unused by sim code, would drag the removed division back in);
//  - removed the float/double/decimal conversion operators — the only blessed crossings are
//    Fix64.FromDecimalLiteral / Fix64.FromWireFloat / ToFloatForDisplay (Fix64.Parse.cs, F4).
// The raw representation (long, ONE = 1L << 32) and the 128-bit-aware saturating
// +, -, * are kept exactly as vendored.

using System;
using System.Runtime.CompilerServices;

namespace OpenSage.SimCore.Numerics;

/// <summary>
/// Represents a Q31.32 fixed-point number. The single scalar numeric type of the
/// deterministic simulation core.
/// </summary>
public readonly partial struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>
{
#pragma warning disable IDE1006 // vendored field name (kept exactly as FixedMath.Net upstream — see file header)
    private readonly long m_rawValue;
#pragma warning restore IDE1006

    public static readonly Fix64 MaxValue = new Fix64(MAX_VALUE);
    public static readonly Fix64 MinValue = new Fix64(MIN_VALUE);
    public static readonly Fix64 One = new Fix64(ONE);
    public static readonly Fix64 Two = new Fix64(ONE * 2);
    public static readonly Fix64 Half = new Fix64(ONE / 2);
    public static readonly Fix64 Zero = new Fix64();
    public static readonly Fix64 Pi = new Fix64(PI);
    public static readonly Fix64 PiOver2 = new Fix64(PI_OVER_2);
    public static readonly Fix64 PiTimes2 = new Fix64(PI_TIMES_2);

    internal const long MAX_VALUE = long.MaxValue;
    internal const long MIN_VALUE = long.MinValue;
    internal const int NUM_BITS = 64;
    internal const int FRACTIONAL_PLACES = 32;
    internal const long ONE = 1L << FRACTIONAL_PLACES;
    internal const long PI_TIMES_2 = 0x6487ED511;
    internal const long PI = 0x3243F6A88;
    internal const long PI_OVER_2 = 0x1921FB544;

    /// <summary>
    /// Returns a number indicating the sign of a Fix64 number.
    /// Returns 1 if the value is positive, 0 if is 0, and -1 if it is negative.
    /// </summary>
    public static int Sign(Fix64 value)
    {
        return
            value.m_rawValue < 0 ? -1 :
            value.m_rawValue > 0 ? 1 :
            0;
    }

    /// <summary>
    /// Returns the absolute value of a Fix64 number.
    /// Note: Abs(Fix64.MinValue) == Fix64.MaxValue.
    /// </summary>
    public static Fix64 Abs(Fix64 value)
    {
        if (value.m_rawValue == MIN_VALUE)
        {
            return MaxValue;
        }

        // branchless implementation, see http://www.strchr.com/optimized_abs_function
        var mask = value.m_rawValue >> 63;
        return new Fix64((value.m_rawValue + mask) ^ mask);
    }

    /// <summary>
    /// Returns the largest integer less than or equal to the specified number.
    /// </summary>
    public static Fix64 Floor(Fix64 value)
    {
        // Just zero out the fractional part
        return new Fix64((long)((ulong)value.m_rawValue & 0xFFFFFFFF00000000));
    }

    /// <summary>
    /// Returns the smallest integral value that is greater than or equal to the specified number.
    /// </summary>
    public static Fix64 Ceiling(Fix64 value)
    {
        var hasFractionalPart = (value.m_rawValue & 0x00000000FFFFFFFF) != 0;
        return hasFractionalPart ? Floor(value) + One : value;
    }

    /// <summary>
    /// Rounds a value to the nearest integral value.
    /// If the value is halfway between an even and an uneven value, returns the even value.
    /// </summary>
    public static Fix64 Round(Fix64 value)
    {
        var fractionalPart = value.m_rawValue & 0x00000000FFFFFFFF;
        var integralPart = Floor(value);
        if (fractionalPart < 0x80000000)
        {
            return integralPart;
        }
        if (fractionalPart > 0x80000000)
        {
            return integralPart + One;
        }
        // if number is halfway between two values, round to the nearest even number
        // this is the method used by System.Math.Round().
        return (integralPart.m_rawValue & ONE) == 0
                   ? integralPart
                   : integralPart + One;
    }

    /// <summary>
    /// Adds x and y. Performs saturating addition, i.e. in case of overflow,
    /// rounds to MinValue or MaxValue depending on sign of operands.
    /// </summary>
    public static Fix64 operator +(Fix64 x, Fix64 y)
    {
        var xl = x.m_rawValue;
        var yl = y.m_rawValue;
        var sum = xl + yl;
        // if signs of operands are equal and signs of sum and x are different
        if (((~(xl ^ yl) & (xl ^ sum)) & MIN_VALUE) != 0)
        {
            sum = xl > 0 ? MAX_VALUE : MIN_VALUE;
        }
        return new Fix64(sum);
    }

    /// <summary>
    /// Adds x and y without performing overflow checking. Should be inlined by the CLR.
    /// </summary>
    public static Fix64 FastAdd(Fix64 x, Fix64 y)
    {
        return new Fix64(x.m_rawValue + y.m_rawValue);
    }

    /// <summary>
    /// Subtracts y from x. Performs saturating substraction, i.e. in case of overflow,
    /// rounds to MinValue or MaxValue depending on sign of operands.
    /// </summary>
    public static Fix64 operator -(Fix64 x, Fix64 y)
    {
        var xl = x.m_rawValue;
        var yl = y.m_rawValue;
        var diff = xl - yl;
        // if signs of operands are different and signs of sum and x are different
        if ((((xl ^ yl) & (xl ^ diff)) & MIN_VALUE) != 0)
        {
            diff = xl < 0 ? MIN_VALUE : MAX_VALUE;
        }
        return new Fix64(diff);
    }

    /// <summary>
    /// Subtracts y from x without performing overflow checking. Should be inlined by the CLR.
    /// </summary>
    public static Fix64 FastSub(Fix64 x, Fix64 y)
    {
        return new Fix64(x.m_rawValue - y.m_rawValue);
    }

    private static long AddOverflowHelper(long x, long y, ref bool overflow)
    {
        var sum = x + y;
        // x + y overflows if sign(x) ^ sign(y) != sign(sum)
        overflow |= ((x ^ y ^ sum) & MIN_VALUE) != 0;
        return sum;
    }

    public static Fix64 operator *(Fix64 x, Fix64 y)
    {
        var xl = x.m_rawValue;
        var yl = y.m_rawValue;

        var xlo = (ulong)(xl & 0x00000000FFFFFFFF);
        var xhi = xl >> FRACTIONAL_PLACES;
        var ylo = (ulong)(yl & 0x00000000FFFFFFFF);
        var yhi = yl >> FRACTIONAL_PLACES;

        var lolo = xlo * ylo;
        var lohi = (long)xlo * yhi;
        var hilo = xhi * (long)ylo;
        var hihi = xhi * yhi;

        var loResult = lolo >> FRACTIONAL_PLACES;
        var midResult1 = lohi;
        var midResult2 = hilo;
        var hiResult = hihi << FRACTIONAL_PLACES;

        bool overflow = false;
        var sum = AddOverflowHelper((long)loResult, midResult1, ref overflow);
        sum = AddOverflowHelper(sum, midResult2, ref overflow);
        sum = AddOverflowHelper(sum, hiResult, ref overflow);

        bool opSignsEqual = ((xl ^ yl) & MIN_VALUE) == 0;

        // if signs of operands are equal and sign of result is negative,
        // then multiplication overflowed positively
        // the reverse is also true
        if (opSignsEqual)
        {
            if (sum < 0 || (overflow && xl > 0))
            {
                return MaxValue;
            }
        }
        else
        {
            if (sum > 0)
            {
                return MinValue;
            }
        }

        // if the top 32 bits of hihi (unused in the result) are neither all 0s or 1s,
        // then this means the result overflowed.
        var topCarry = hihi >> FRACTIONAL_PLACES;
        if (topCarry != 0 && topCarry != -1)
        {
            return opSignsEqual ? MaxValue : MinValue;
        }

        // If signs differ, both operands' magnitudes are greater than 1,
        // and the result is greater than the negative operand, then there was negative overflow.
        if (!opSignsEqual)
        {
            long posOp, negOp;
            if (xl > yl)
            {
                posOp = xl;
                negOp = yl;
            }
            else
            {
                posOp = yl;
                negOp = xl;
            }
            if (sum > negOp && negOp < -ONE && posOp > ONE)
            {
                return MinValue;
            }
        }

        return new Fix64(sum);
    }

    /// <summary>
    /// Performs multiplication without checking for overflow.
    /// Useful for performance-critical code where the values are guaranteed not to cause overflow.
    /// </summary>
    public static Fix64 FastMul(Fix64 x, Fix64 y)
    {
        var xl = x.m_rawValue;
        var yl = y.m_rawValue;

        var xlo = (ulong)(xl & 0x00000000FFFFFFFF);
        var xhi = xl >> FRACTIONAL_PLACES;
        var ylo = (ulong)(yl & 0x00000000FFFFFFFF);
        var yhi = yl >> FRACTIONAL_PLACES;

        var lolo = xlo * ylo;
        var lohi = (long)xlo * yhi;
        var hilo = xhi * (long)ylo;
        var hihi = xhi * yhi;

        var loResult = lolo >> FRACTIONAL_PLACES;
        var midResult1 = lohi;
        var midResult2 = hilo;
        var hiResult = hihi << FRACTIONAL_PLACES;

        var sum = (long)loResult + midResult1 + midResult2 + hiResult;
        return new Fix64(sum);
    }

    public static Fix64 operator %(Fix64 x, Fix64 y)
    {
        return new Fix64(
            x.m_rawValue == MIN_VALUE & y.m_rawValue == -1 ?
            0 :
            x.m_rawValue % y.m_rawValue);
    }

    public static Fix64 operator -(Fix64 x)
    {
        return x.m_rawValue == MIN_VALUE ? MaxValue : new Fix64(-x.m_rawValue);
    }

    public static bool operator ==(Fix64 x, Fix64 y) => x.m_rawValue == y.m_rawValue;

    public static bool operator !=(Fix64 x, Fix64 y) => x.m_rawValue != y.m_rawValue;

    public static bool operator >(Fix64 x, Fix64 y) => x.m_rawValue > y.m_rawValue;

    public static bool operator <(Fix64 x, Fix64 y) => x.m_rawValue < y.m_rawValue;

    public static bool operator >=(Fix64 x, Fix64 y) => x.m_rawValue >= y.m_rawValue;

    public static bool operator <=(Fix64 x, Fix64 y) => x.m_rawValue <= y.m_rawValue;

    public static explicit operator Fix64(long value)
    {
        return new Fix64(value * ONE);
    }

    public static explicit operator long(Fix64 value)
    {
        return value.m_rawValue >> FRACTIONAL_PLACES;
    }

    public override bool Equals(object? obj)
    {
        return obj is Fix64 other && other.m_rawValue == m_rawValue;
    }

    public override int GetHashCode()
    {
        return m_rawValue.GetHashCode();
    }

    public bool Equals(Fix64 other)
    {
        return m_rawValue == other.m_rawValue;
    }

    public int CompareTo(Fix64 other)
    {
        return m_rawValue.CompareTo(other.m_rawValue);
    }

    public override string ToString()
    {
        // Up to 10 decimal places, via decimal (exact for every raw value).
        return ((decimal)m_rawValue / ONE).ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static Fix64 FromRaw(long rawValue)
    {
        return new Fix64(rawValue);
    }

    /// <summary>
    /// The underlying integer representation.
    /// </summary>
    public long RawValue => m_rawValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Fix64(long rawValue)
    {
        m_rawValue = rawValue;
    }

    public Fix64(int value)
    {
        m_rawValue = value * ONE;
    }
}

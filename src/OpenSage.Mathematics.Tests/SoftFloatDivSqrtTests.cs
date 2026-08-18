// SPIKE (softfloat-oracle): validation of SoftFloat division and square root against
// hardware IEEE-754 single precision. Hardware float '/' and MathF.Sqrt are correctly
// rounded per IEEE 754 on every .NET target, so bit-comparison against them is a complete
// oracle for the normal/subnormal/special behavior of the soft implementations
// (excluding NaN payloads, which we canonicalize deliberately).

using System;
using Xunit;

namespace OpenSage.Mathematics.Tests;

public class SoftFloatDivSqrtTests
{
    private const int RandomCases = 2_000_000;

    // splitmix64: stable input generation independent of System.Random's version-dependent
    // algorithm (same rationale as tools/archprobe).
    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static float FromBits(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    private static uint ToBits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static void AssertDivMatchesHardware(uint aBits, uint bBits)
    {
        var a = FromBits(aBits);
        var b = FromBits(bBits);
        var expected = a / b;

        var soft = (SoftFloat)a / (SoftFloat)b;
        var actualBits = ToBits((float)soft);

        if (float.IsNaN(expected))
        {
            Assert.True(SoftFloat.IsNaN(soft), $"{aBits:X8} / {bBits:X8}: expected NaN, got {actualBits:X8}");
            return;
        }

        Assert.True(ToBits(expected) == actualBits,
            $"{aBits:X8} / {bBits:X8}: hardware {ToBits(expected):X8}, soft {actualBits:X8}");
    }

    private static void AssertSqrtMatchesHardware(uint bits)
    {
        var x = FromBits(bits);
        var expected = MathF.Sqrt(x);
        var soft = SoftFloat.Sqrt((SoftFloat)x);
        var actualBits = ToBits((float)soft);

        if (float.IsNaN(expected))
        {
            Assert.True(SoftFloat.IsNaN(soft), $"sqrt({bits:X8}): expected NaN, got {actualBits:X8}");
            return;
        }

        Assert.True(ToBits(expected) == actualBits,
            $"sqrt({bits:X8}): hardware {ToBits(expected):X8}, soft {actualBits:X8}");
    }

    [Fact]
    public void Division_MatchesHardware_RandomBitPatterns()
    {
        ulong state = 0xB105F00DCAFE0001UL;
        for (var i = 0; i < RandomCases; i++)
        {
            var r = SplitMix64(ref state);
            AssertDivMatchesHardware((uint)r, (uint)(r >> 32));
        }
    }

    [Fact]
    public void Division_MatchesHardware_GameMagnitudeValues()
    {
        // Positions/speeds in SAGE are feet-scale: exercise the [1e-3, 1e5] decade band densely.
        ulong state = 0x5EEDF00D00000002UL;
        for (var i = 0; i < RandomCases; i++)
        {
            var a = (float)((SplitMix64(ref state) % 100_000_000) / 1000.0 + 0.001);
            var b = (float)((SplitMix64(ref state) % 100_000_000) / 1000.0 + 0.001);
            AssertDivMatchesHardware(ToBits(a), ToBits(b));
        }
    }

    [Fact]
    public void Division_MatchesHardware_SubnormalAndBoundary()
    {
        // All pairings of the interesting boundary encodings.
        uint[] specials =
        {
            0x00000000, 0x80000000,             // +-0
            0x00000001, 0x80000001,             // min subnormal
            0x007FFFFF, 0x807FFFFF,             // max subnormal
            0x00800000, 0x80800000,             // min normal
            0x7F7FFFFF, 0xFF7FFFFF,             // max finite
            0x7F800000, 0xFF800000,             // +-inf
            0x7FC00000, 0xFFC00000,             // NaN
            0x3F800000, 0xBF800000,             // +-1
            0x3F800001, 0x34000000, 0x7F000000, // 1+ulp, 2^-23, 2^127
        };
        foreach (var a in specials)
        {
            foreach (var b in specials)
            {
                AssertDivMatchesHardware(a, b);
            }
        }

        // Random subnormal-heavy pairs (tiny / huge exercises the underflow path).
        ulong state = 0xDEADBEEF00000003UL;
        for (var i = 0; i < 200_000; i++)
        {
            var tiny = (uint)(SplitMix64(ref state) & 0x00FFFFFF);            // exp 0..1
            var huge = 0x7E000000u | (uint)(SplitMix64(ref state) & 0x00FFFFFF);
            AssertDivMatchesHardware(tiny, huge);
            AssertDivMatchesHardware(huge, tiny);
        }
    }

    [Fact]
    public void Sqrt_MatchesHardware_RandomBitPatterns()
    {
        ulong state = 0xC0FFEE0000000004UL;
        for (var i = 0; i < RandomCases; i++)
        {
            AssertSqrtMatchesHardware((uint)SplitMix64(ref state));
        }
    }

    [Fact]
    public void Sqrt_MatchesHardware_BoundaryEncodings()
    {
        uint[] specials =
        {
            0x00000000, 0x80000000, 0x00000001, 0x007FFFFF, 0x00800000,
            0x7F7FFFFF, 0x7F800000, 0xFF800000, 0x7FC00000, 0x3F800000,
            0xBF800000, 0x40000000, 0x40490FDB, 0x00000002, 0x00000003,
        };
        foreach (var v in specials)
        {
            AssertSqrtMatchesHardware(v);
        }

        // Dense sweep across the subnormal floor and the min-normal boundary.
        for (uint v = 0; v < 0x01000000; v += 97)
        {
            AssertSqrtMatchesHardware(v);
        }
    }
}

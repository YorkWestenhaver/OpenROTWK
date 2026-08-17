// Reference-equivalence CI for the two guess-accelerated ops (api-freeze-v1 F2,
// design-simcore-scaffolding §1.4): the hardware-double guess in operator / and Sqrt
// must be provably irrelevant — the integer fixup has to land on exactly the value the
// pure-integer reference computes, for edge cases and a large splitmix64-driven corpus.
//
// The full 10^8-pair corpus mandated by §1.4 runs as the trait
// Category=ReferenceEquivalenceFull (dotnet test --filter Category=ReferenceEquivalenceFull);
// the unconditional tests cover the same generator on a 10^6 prefix, so every plain
// test run still exercises the corpus path.

using System;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.SimCore.Tests
{
    public class DivSqrtEquivalenceTests
    {
        // Deterministic corpus driver (splitmix64).
        private static ulong SplitMix64(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static readonly long[] EdgeRaws =
        {
            0L, 1L, -1L, 2L, -2L, 3L, -3L,
            0x80000000L, -0x80000000L,                    // ±0.5
            1L << 32, -(1L << 32),                        // ±1
            (1L << 32) + 1, (1L << 32) - 1,
            long.MaxValue, long.MinValue,
            long.MaxValue - 1, long.MinValue + 1,
            long.MaxValue >> 1, long.MinValue >> 1,
            1L << 62, -(1L << 62),
            9_999_999L << 32, -(9_999_999L << 32),        // AttackRange sentinel
            999_999L << 32,                               // Speed sentinel
            5_000L << 32,                                 // map bound
            429497L,                                      // 0.0001
            0x3243F6A88L,                                 // π raw
        };

        [Fact]
        public void Division_EqualsReference_EdgeCases()
        {
            foreach (var a in EdgeRaws)
            {
                foreach (var b in EdgeRaws)
                {
                    if (b == 0)
                    {
                        continue;
                    }
                    var x = Fix64.FromRaw(a);
                    var y = Fix64.FromRaw(b);
                    var actual = x / y;
                    var expected = Fix64.DivideReference(x, y);
                    Assert.True(actual == expected,
                        $"a=0x{a:X16} b=0x{b:X16}: operator/={actual.RawValue:X16} reference={expected.RawValue:X16}");
                }
            }
        }

        [Fact]
        public void Division_EqualsReference_RandomCorpusPrefix()
        {
            RunDivisionCorpus(1_000_000);
        }

        [Fact]
        [Trait("Category", "ReferenceEquivalenceFull")]
        public void Division_EqualsReference_FullCorpus()
        {
            RunDivisionCorpus(100_000_000);
        }

        private static void RunDivisionCorpus(int pairs)
        {
            ulong state = 0x5EED_C0DE_D15EA5EDUL;
            for (var i = 0; i < pairs; i++)
            {
                var a = (long)SplitMix64(ref state);
                var b = (long)SplitMix64(ref state);
                if (b == 0)
                {
                    continue;
                }
                var x = Fix64.FromRaw(a);
                var y = Fix64.FromRaw(b);
                var actual = x / y;
                var expected = Fix64.DivideReference(x, y);
                if (actual != expected)
                {
                    Assert.Fail(
                        $"pair {i}: a=0x{a:X16} b=0x{b:X16}: operator/={actual.RawValue:X16} reference={expected.RawValue:X16}");
                }
            }
        }

        [Fact]
        public void Division_KnownValues_EuclideanSemantics()
        {
            var one = Fix64.One;
            var three = Fix64.FromDecimalLiteral("3");

            // 1/3: floor(2^64 / (3·2^32)) = 0x55555555.
            Assert.Equal(0x55555555L, (one / three).RawValue);

            // Euclidean: remainder is non-negative, so -1/3 rounds down to -0x55555556.
            Assert.Equal(-0x55555556L, (-one / three).RawValue);

            Assert.Equal(Fix64.Two, Fix64.FromDecimalLiteral("6") / three);
            Assert.Equal(Fix64.FromDecimalLiteral("-2"), Fix64.FromDecimalLiteral("6") / Fix64.FromDecimalLiteral("-3"));
            Assert.Equal(Fix64.Half, one / Fix64.Two);
        }

        [Fact]
        public void Division_SaturatesAndThrowsLikeReference()
        {
            // Overflowing quotient saturates identically on both paths.
            var tiny = Fix64.FromRaw(1);
            Assert.Equal(Fix64.MaxValue, Fix64.MaxValue / tiny);
            Assert.Equal(Fix64.MaxValue, Fix64.DivideReference(Fix64.MaxValue, tiny));
            Assert.Equal(Fix64.MinValue, Fix64.MinValue / tiny);
            Assert.Equal(Fix64.MinValue, Fix64.DivideReference(Fix64.MinValue, tiny));

            Assert.Throws<DivideByZeroException>(() => Fix64.One / Fix64.Zero);
            Assert.Throws<DivideByZeroException>(() => Fix64.DivideReference(Fix64.One, Fix64.Zero));
        }

        [Fact]
        public void Sqrt_EqualsReference_EdgeCases()
        {
            foreach (var a in EdgeRaws)
            {
                if (a < 0)
                {
                    continue;
                }
                var x = Fix64.FromRaw(a);
                Assert.True(Fix64.Sqrt(x) == Fix64.SqrtReference(x),
                    $"raw=0x{a:X16}: Sqrt={Fix64.Sqrt(x).RawValue:X16} reference={Fix64.SqrtReference(x).RawValue:X16}");
            }
        }

        [Fact]
        public void Sqrt_EqualsReference_RandomCorpusPrefix()
        {
            RunSqrtCorpus(1_000_000);
        }

        [Fact]
        [Trait("Category", "ReferenceEquivalenceFull")]
        public void Sqrt_EqualsReference_FullCorpus()
        {
            RunSqrtCorpus(100_000_000);
        }

        private static void RunSqrtCorpus(int count)
        {
            ulong state = 0xF1BB_0BB1_5EED_2026UL;
            for (var i = 0; i < count; i++)
            {
                var raw = (long)(SplitMix64(ref state) >> 1);   // non-negative
                var x = Fix64.FromRaw(raw);
                var actual = Fix64.Sqrt(x);
                var expected = Fix64.SqrtReference(x);
                if (actual != expected)
                {
                    Assert.Fail($"i={i} raw=0x{raw:X16}: Sqrt={actual.RawValue:X16} reference={expected.RawValue:X16}");
                }
            }
        }

        [Fact]
        public void Sqrt_KnownValues()
        {
            Assert.Equal(Fix64.Two, Fix64.Sqrt(Fix64.FromDecimalLiteral("4")));
            Assert.Equal(Fix64.FromDecimalLiteral("5"), Fix64.Sqrt(Fix64.FromDecimalLiteral("25")));
            Assert.Equal(Fix64.Half, Fix64.Sqrt(Fix64.FromDecimalLiteral("0.25")));
            Assert.Equal(Fix64.Zero, Fix64.Sqrt(Fix64.Zero));
            // √2 · 2^32 = 6074000999.9537…: round-to-nearest raw.
            Assert.Equal(6074001000L, Fix64.Sqrt(Fix64.Two).RawValue);
            Assert.Throws<ArgumentOutOfRangeException>(() => Fix64.Sqrt(Fix64.FromRaw(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fix64.SqrtReference(Fix64.FromRaw(-1)));
        }

        [Fact]
        public void SqrtRawWide_MatchesBigIntegerFloorSqrt()
        {
            // The wide helper feeding FixMath.Distance: verify round-to-nearest against
            // an independent BigInteger integer square root.
            ulong state = 0xA11CE0FD_ECADE5UL;
            for (var i = 0; i < 20_000; i++)
            {
                var hi = SplitMix64(ref state);
                var lo = SplitMix64(ref state);
                var t = ((UInt128)(hi >> (i % 64)) << 64) | lo;
                var actual = Fix64.SqrtRawWide(t);

                var big = new System.Numerics.BigInteger((ulong)(t >> 64)) << 64
                        | new System.Numerics.BigInteger((ulong)t);
                var floor = FloorSqrt(big);
                var expected = (ulong)floor;
                if (big - floor * floor > floor)
                {
                    expected++;
                }
                Assert.Equal(expected, actual);
            }
        }

        private static System.Numerics.BigInteger FloorSqrt(System.Numerics.BigInteger n)
        {
            if (n == 0)
            {
                return 0;
            }
            var x = n;
            var y = (x + 1) / 2;
            while (y < x)
            {
                x = y;
                y = (x + n / x) / 2;
            }
            return x;
        }
    }
}

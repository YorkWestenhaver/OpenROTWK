// Tests for the two blessed float boundaries (api-freeze-v1 F4):
// FromDecimalLiteral (INI text -> Fix64, integer-only) and FromWireFloat
// (IEEE-754 binary32 bits -> Fix64, integer-only), plus ToFloatForDisplay.

using System;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.SimCore.Tests
{
    public class Fix64ParseTests
    {
        [Theory]
        [InlineData("0", 0L)]
        [InlineData("1", 1L << 32)]
        [InlineData("-1", -(1L << 32))]
        [InlineData("+1", 1L << 32)]
        [InlineData("0.5", 0x80000000L)]
        [InlineData("-0.5", -0x80000000L)]
        [InlineData("0.25", 0x40000000L)]
        [InlineData("2.5", 0x280000000L)]
        [InlineData(".5", 0x80000000L)]
        [InlineData("5.", 5L << 32)]
        [InlineData("9999999", 9_999_999L << 32)]          // AttackRange sentinel (R1)
        [InlineData("999999", 999_999L << 32)]             // Speed sentinel
        [InlineData("2147483647", 2147483647L << 32)]      // integer max that fits
        [InlineData("-2147483648", -2147483648L << 32)]
        public void FromDecimalLiteral_ExactValues(string text, long expectedRaw)
        {
            Assert.Equal(expectedRaw, Fix64.FromDecimalLiteral(text).RawValue);
        }

        [Theory]
        [InlineData("1e3", "1000")]
        [InlineData("12.34e2", "1234")]
        [InlineData("1234e-2", "12.34")]
        [InlineData("1.5E-2", "0.015")]
        [InlineData("0.0001e4", "1")]
        [InlineData("5e0", "5")]
        public void FromDecimalLiteral_ExponentIsDigitShift(string text, string equivalent)
        {
            Assert.Equal(
                Fix64.FromDecimalLiteral(equivalent).RawValue,
                Fix64.FromDecimalLiteral(text).RawValue);
        }

        [Fact]
        public void FromDecimalLiteral_RoundsHalfUpOnMagnitude()
        {
            // 2^-33 is exactly half of the raw ulp: rounds up to raw 1 —
            // and the sign applies AFTER the magnitude rounding.
            const string halfUlp = "0.000000000116415321826934814453125";
            Assert.Equal(1L, Fix64.FromDecimalLiteral(halfUlp).RawValue);
            Assert.Equal(-1L, Fix64.FromDecimalLiteral("-" + halfUlp).RawValue);

            // Just below half of the raw ulp: rounds to zero.
            Assert.Equal(0L, Fix64.FromDecimalLiteral("0.0000000001").RawValue);

            // 2^-32 exactly: raw 1.
            Assert.Equal(1L, Fix64.FromDecimalLiteral("0.00000000023283064365386962890625").RawValue);

            // Smallest literal in shipping data: 0.0001 * 2^32 = 429496.7296 -> 429497.
            Assert.Equal(429497L, Fix64.FromDecimalLiteral("0.0001").RawValue);
        }

        [Fact]
        public void FromDecimalLiteral_LongFractionIsStable()
        {
            // 34 threes: arbitrary fraction lengths stay exact — correctly rounded 1/3.
            var third = Fix64.FromDecimalLiteral("0.3333333333333333333333333333333333");
            Assert.Equal(0x55555555L, third.RawValue);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("1.2.3")]
        [InlineData("--1")]
        [InlineData("1e")]
        [InlineData("e5")]
        [InlineData(".")]
        [InlineData("1,5")]
        public void FromDecimalLiteral_RejectsMalformedText(string text)
        {
            Assert.Throws<FormatException>(() => Fix64.FromDecimalLiteral(text));
        }

        [Theory]
        [InlineData("3000000000")]      // > 2^31 - 1
        [InlineData("-3000000000")]
        [InlineData("1e300")]
        public void FromDecimalLiteral_ThrowsOnOutOfRange(string text)
        {
            Assert.Throws<OverflowException>(() => Fix64.FromDecimalLiteral(text));
        }

        [Theory]
        [InlineData(1.0f, 1L << 32)]
        [InlineData(-1.0f, -(1L << 32))]
        [InlineData(0.5f, 0x80000000L)]
        [InlineData(-2.5f, -0x280000000L)]
        [InlineData(0.0f, 0L)]
        [InlineData(-0.0f, 0L)]
        [InlineData(123456.75f, (123456L << 32) + (3L << 30))]
        [InlineData(1e7f, 10_000_000L << 32)]              // exactly representable in binary32
        public void FromWireFloat_ExactlyRepresentableValues(float value, long expectedRaw)
        {
            var bits = BitConverter.SingleToUInt32Bits(value);
            Assert.Equal(expectedRaw, Fix64.FromWireFloat(bits).RawValue);
        }

        [Fact]
        public void FromWireFloat_IsBitExactMantissaShift()
        {
            // 0.1f = 13421773 * 2^-27 exactly; raw = 13421773 << 5.
            var bits = BitConverter.SingleToUInt32Bits(0.1f);
            Assert.Equal(0x3DCCCCCDu, bits);
            Assert.Equal(13421773L << 5, Fix64.FromWireFloat(bits).RawValue);
        }

        [Fact]
        public void FromWireFloat_TruncatesTowardZeroBelowResolution()
        {
            // 2^-33 is representable in binary32 but below Q31.32 resolution: truncates to 0.
            var bits = BitConverter.SingleToUInt32Bits(1.1641532182693481e-10f);
            Assert.Equal(0L, Fix64.FromWireFloat(bits).RawValue);

            // Denormals collapse to zero too.
            var denormal = BitConverter.SingleToUInt32Bits(1e-40f);
            Assert.Equal(0L, Fix64.FromWireFloat(denormal).RawValue);
        }

        [Fact]
        public void FromWireFloat_SaturatesOutOfRange()
        {
            Assert.Equal(Fix64.MaxValue, Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(3e38f)));
            Assert.Equal(Fix64.MinValue, Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(-3e38f)));
            Assert.Equal(Fix64.MaxValue, Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(float.PositiveInfinity)));
            Assert.Equal(Fix64.MinValue, Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(float.NegativeInfinity)));
        }

        [Fact]
        public void FromWireFloat_RejectsNaN()
        {
            Assert.Throws<ArgumentException>(
                () => Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(float.NaN)));
        }

        [Fact]
        public void FromWireFloat_RoundTripsFloatValuesInSimRange()
        {
            // Every binary32 whose value fits Q31.32 with all its bits converts exactly:
            // shifting the mantissa loses nothing when shift >= 0 (values >= 2^-9).
            foreach (var value in new[] { 3.14159274f, 5000.0f, 0.061f, 450.5f, 1599.99f })
            {
                var fix = Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));
                Assert.Equal(value, fix.ToFloatForDisplay());
            }
        }

        [Fact]
        public void ToFloatForDisplay_KnownValues()
        {
            Assert.Equal(1.5f, Fix64.FromDecimalLiteral("1.5").ToFloatForDisplay());
            Assert.Equal(0f, Fix64.Zero.ToFloatForDisplay());
            Assert.Equal(-0.25f, Fix64.FromDecimalLiteral("-0.25").ToFloatForDisplay());
        }
    }
}

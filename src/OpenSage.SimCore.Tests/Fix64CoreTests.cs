using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class Fix64CoreTests
{
    private static Fix64 F(string literal) => Fix64.FromDecimalLiteral(literal);

    [Fact]
    public void RawRepresentation_IsQ31_32()
    {
        Assert.Equal(1L << 32, Fix64.One.RawValue);
        Assert.Equal(0L, Fix64.Zero.RawValue);
        Assert.Equal(long.MaxValue, Fix64.MaxValue.RawValue);
        Assert.Equal(long.MinValue, Fix64.MinValue.RawValue);
        Assert.Equal(0x80000000L, Fix64.Half.RawValue);
    }

    [Fact]
    public void Addition_KnownValues()
    {
        Assert.Equal(Fix64.Two, Fix64.One + Fix64.One);
        Assert.Equal(F("3"), F("1.5") + F("1.5"));
        Assert.Equal(F("-1"), F("1") + F("-2"));
    }

    [Fact]
    public void Addition_SaturatesOnOverflow()
    {
        Assert.Equal(Fix64.MaxValue, Fix64.MaxValue + Fix64.One);
        Assert.Equal(Fix64.MinValue, Fix64.MinValue + -Fix64.One);
        Assert.Equal(Fix64.MinValue, Fix64.MinValue - Fix64.One);
        Assert.Equal(Fix64.MaxValue, Fix64.MaxValue - -Fix64.One);
    }

    [Fact]
    public void Multiplication_KnownValues()
    {
        Assert.Equal(F("3"), F("1.5") * F("2"));
        Assert.Equal(F("-3"), F("1.5") * F("-2"));
        Assert.Equal(F("0.25"), Fix64.Half * Fix64.Half);
        Assert.Equal(Fix64.Zero, Fix64.Zero * Fix64.MaxValue);
    }

    [Fact]
    public void Multiplication_SaturatesOnOverflow()
    {
        Assert.Equal(Fix64.MaxValue, Fix64.MaxValue * Fix64.Two);
        Assert.Equal(Fix64.MinValue, Fix64.MinValue * Fix64.Two);
        Assert.Equal(Fix64.MinValue, Fix64.MaxValue * F("-2"));

        // R2 rationale (design-simcore-scaffolding §1.2): the square of the
        // 9,999,999 sentinel does NOT fit Q31.32 and saturates — this is exactly
        // why all distance-vs-range compares go through FixMath's wide compare.
        var sentinel = F("9999999");
        Assert.Equal(Fix64.MaxValue, sentinel * sentinel);
    }

    [Fact]
    public void SentinelValues_FitPlain()
    {
        // R1: largest Fix64-destined literals in shipping AotR data fit with headroom.
        Assert.Equal(9_999_999L << 32, F("9999999").RawValue);
        Assert.Equal(999_999L << 32, F("999999").RawValue);
    }

    [Fact]
    public void Negation_And_Abs_HandleMinValue()
    {
        Assert.Equal(Fix64.MaxValue, -Fix64.MinValue);
        Assert.Equal(Fix64.MaxValue, Fix64.Abs(Fix64.MinValue));
        Assert.Equal(F("2"), Fix64.Abs(F("-2")));
        Assert.Equal(F("2"), Fix64.Abs(F("2")));
    }

    [Fact]
    public void Sign_KnownValues()
    {
        Assert.Equal(1, Fix64.Sign(F("0.0001")));
        Assert.Equal(-1, Fix64.Sign(F("-0.0001")));
        Assert.Equal(0, Fix64.Sign(Fix64.Zero));
    }

    [Theory]
    [InlineData("2.5", "2", "3", "2")]     // value, floor, ceiling, round (half-even)
    [InlineData("3.5", "3", "4", "4")]
    [InlineData("-0.5", "-1", "0", "0")]
    [InlineData("-1.5", "-2", "-1", "-2")]
    [InlineData("2", "2", "2", "2")]
    public void Floor_Ceiling_Round(string value, string floor, string ceiling, string round)
    {
        var v = F(value);
        Assert.Equal(F(floor), Fix64.Floor(v));
        Assert.Equal(F(ceiling), Fix64.Ceiling(v));
        Assert.Equal(F(round), Fix64.Round(v));
    }

    [Fact]
    public void LongConversions_AreFloorOnTruncate()
    {
        Assert.Equal(5L << 32, ((Fix64)5L).RawValue);
        Assert.Equal(5L, (long)F("5.75"));
        Assert.Equal(-6L, (long)F("-5.75"));   // arithmetic shift: floor semantics
    }

    [Fact]
    public void Comparisons_AreRawOrder()
    {
        Assert.True(F("1.5") > F("1.25"));
        Assert.True(F("-3") < F("-2"));
        Assert.True(F("2") >= F("2"));
        Assert.True(F("2") <= F("2"));
        Assert.True(F("2") == Fix64.Two);
        Assert.True(F("2") != Fix64.One);
        Assert.Equal(-1, F("-1").CompareTo(Fix64.Zero));
    }

    [Fact]
    public void Modulo_KnownValues()
    {
        Assert.Equal(F("1"), F("7") % F("3"));
        Assert.Equal(F("-1"), F("-7") % F("3"));   // sign follows dividend (C# semantics)
        Assert.Equal(Fix64.Zero, Fix64.MinValue % Fix64.FromRaw(-1));   // no overflow trap
    }

    [Fact]
    public void ToString_IsInvariantDecimal()
    {
        Assert.Equal("1.5", F("1.5").ToString());
        Assert.Equal("-0.25", F("-0.25").ToString());
    }
}

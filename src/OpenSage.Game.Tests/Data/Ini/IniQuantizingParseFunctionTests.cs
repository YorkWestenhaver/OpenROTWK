// Tests for the S5 quantizing parse functions (api-freeze-v1 seam S5): INI text lands as
// Fix64 / LogicFrameSpan through the blessed integer-only boundary, with the rounding rules
// pinned per formula. Expected raw values are hand-computed (Q31.32: value * 2^32).

using OpenSage.Data.Ini;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniQuantizingParseFunctionTests
{
    private readonly IniParseTestContext _context = new(SageGame.Bfme2);

    private IniParser Parser(string tokens)
    {
        var parser = _context.CreateParser(tokens);
        parser.GoToNextLine();
        return parser;
    }

    [Theory]
    // 140.5 * 2^32 = 140 * 4294967296 + 2147483648 = 603442905088
    [InlineData("140.5", 603442905088L)]
    // 2 * 2^32
    [InlineData("2", 8589934592L)]
    // -0.25 * 2^32
    [InlineData("-0.25", -1073741824L)]
    // 0.1 has no exact binary form: round-half-up of 0.1 * 2^32 = 429496729.6 -> 429496730
    [InlineData("0.1", 429496730L)]
    public void ParseFix64_QuantizesExactly(string text, long expectedRaw)
    {
        Assert.Equal(expectedRaw, Parser(text).ParseFix64().RawValue);
    }

    [Fact]
    public void ParseFix64_SameTextSameBits_RegardlessOfTrailingJunk()
    {
        // The INI corpus carries suffixes ("%", units); the float-text slice strips them,
        // matching ParseFloat's tolerance so audited fields cannot regress the gapmap.
        Assert.Equal(Parser("25").ParseFix64().RawValue, Parser("25%").ParseFix64().RawValue);
    }

    [Theory]
    // 25% -> 0.25 exactly
    [InlineData("25%", 1073741824L)]
    // 100% -> 1.0
    [InlineData("100%", 4294967296L)]
    // 12.5% -> 0.125
    [InlineData("12.5%", 536870912L)]
    public void ParseFix64Percentage_IsExact(string text, long expectedRaw)
    {
        Assert.Equal(expectedRaw, Parser(text).ParseFix64Percentage().RawValue);
    }

    [Theory]
    // BFME2 logic rate is the frozen 5 Hz (F6): frames = ceil(ms / 200).
    [InlineData("2000", 10u)]
    [InlineData("2001", 11u)]  // ceil, not floor/nearest (S5 default)
    [InlineData("199", 1u)]
    [InlineData("200", 1u)]
    [InlineData("0", 0u)]
    [InlineData("100.5", 1u)]  // fractional ms stays exact through the quantized value
    public void ParseDurationLogicFrames_CeilsAtFiveHz(string ms, uint expectedFrames)
    {
        Assert.Equal(expectedFrames, Parser(ms).ParseDurationLogicFrames().Value);
    }

    [Fact]
    public void ParseAngleDegrees_LandsAsFix64Radians()
    {
        // 180 degrees -> exactly Fix64.Pi (the LUT-pinned Q31.32 Pi, not libm's).
        Assert.Equal(Fix64.Pi.RawValue, Parser("180").ParseAngleDegrees().RawValue);
        // 90 degrees -> Pi/2 with round-half-up on the raw scale.
        Assert.Equal((Fix64.Pi.RawValue + 1) / 2, Parser("90").ParseAngleDegrees().RawValue);
        // -180 mirrors exactly.
        Assert.Equal(-Fix64.Pi.RawValue, Parser("-180").ParseAngleDegrees().RawValue);
    }

    [Fact]
    public void ParseFixVector3_QuantizesEachComponent()
    {
        var v = Parser("X:1.5 Y:-2 Z:0.25").ParseFixVector3();
        Assert.Equal(6442450944L, v.X.RawValue);
        Assert.Equal(-8589934592L, v.Y.RawValue);
        Assert.Equal(1073741824L, v.Z.RawValue);
    }

    [Fact]
    public void ParseFix64_NeverThroughDouble_LongDecimalTailStaysExact()
    {
        // 21 fraction digits: a double would have rounded at 17 significant digits.
        // Round-half-up of 0.333333333333333333333 * 2^32 = 1431655765.33 -> 1431655765.
        Assert.Equal(1431655765L, Parser("0.333333333333333333333").ParseFix64().RawValue);
    }
}

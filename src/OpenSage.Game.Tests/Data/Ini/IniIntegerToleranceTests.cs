using System;
using OpenSage.FileFormats;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

/// <summary>
/// The engine's integer field scanners are sscanf-based (scanInt = sscanf(token, "%d", &amp;value),
/// scanUnsignedInt = "%u"): they skip leading whitespace, consume an optional sign plus the leading
/// digit run, and stop at the first non-digit. Float-shaped tokens that appear in shipped content
/// — "2.", "2.75" — therefore yield 2, and only a token with no digit run at all is a data error.
/// These tests pin that contract at the parser layer (<see cref="ParseUtility"/>, which every INI
/// integer field and the W3D mapper-arg integer fields now go through) rather than at any single
/// call site.
/// </summary>
public class IniIntegerToleranceTests
{
    [Theory]
    // trailing dot — the shape that crashed AotR asset loading with Int32.Parse("2.")
    [InlineData("2.", 2)]
    [InlineData("-2.", -2)]
    // fractional — truncates at the decimal point, no rounding
    [InlineData("2.75", 2)]
    [InlineData("-2.75", -2)]
    [InlineData("0.9", 0)]
    // plain integers still parse exactly
    [InlineData("2", 2)]
    [InlineData("0", 0)]
    [InlineData("-17", -17)]
    [InlineData("+17", 17)]
    // leading whitespace is skipped, trailing junk is ignored, exactly as sscanf does
    [InlineData("  42", 42)]
    [InlineData("42f", 42)]
    [InlineData("42 ; comment", 42)]
    public void FloatShapedIntegerTokensTruncate(string token, int expected)
    {
        Assert.Equal(expected, ParseUtility.ParseInteger(token));

        Assert.True(ParseUtility.TryParseInteger(token, out var tryResult));
        Assert.Equal(expected, tryResult);

        Assert.True(ParseUtility.IsInteger(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(".5")]      // no leading digit run: sscanf("%d") matches nothing
    [InlineData("-")]
    [InlineData("+")]
    public void TokensWithNoLeadingDigitRunAreRejected(string token)
    {
        Assert.Throws<FormatException>(() => ParseUtility.ParseInteger(token));

        Assert.False(ParseUtility.TryParseInteger(token, out var tryResult));
        Assert.Equal(0, tryResult);

        Assert.False(ParseUtility.IsInteger(token));
    }

    [Fact]
    public void LongAndUnsignedScannersFollowTheSameRule()
    {
        Assert.Equal(2L, ParseUtility.ParseLong("2."));
        Assert.Equal(2u, ParseUtility.ParseUnsignedInteger("2."));
        Assert.Equal(4000000000u, ParseUtility.ParseUnsignedInteger("4000000000.5"));

        Assert.Throws<FormatException>(() => ParseUtility.ParseLong("abc"));
        Assert.Throws<FormatException>(() => ParseUtility.ParseUnsignedInteger(""));
    }

    [Fact]
    public void OverflowingDigitRunsStillRaiseOverflowSoIniParserCanClampThem()
    {
        // IniParser.ScanLong relies on this to clamp out-of-range values to long.Min/MaxValue.
        Assert.Throws<OverflowException>(() => ParseUtility.ParseLong("99999999999999999999"));
        Assert.Throws<OverflowException>(() => ParseUtility.ParseInteger("3000000000"));
    }

    [Fact]
    public void IntegerFieldOnAnObjectAcceptsATrailingDotValue()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Object TrailingDotInt\n" +
            "  TransportSlotCount = 2.\n" +
            "  EnergyProduction = -3.\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName("TrailingDotInt");
        Assert.NotNull(definition);
        Assert.Equal(2, definition.TransportSlotCount);
        Assert.Equal(-3, definition.EnergyProduction);
    }

    [Fact]
    public void GarbageIntegerFieldIsAParseErrorNotACrash()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Object GarbageInt\n" +
            "  TransportSlotCount = notanumber\n" +
            "End\n" +
            "\n" +
            "Object AfterGarbageInt\n" +
            "  TransportSlotCount = 4\n" +
            "End\n");

        Assert.NotEmpty(parser.ParseErrors);

        // Containment: the bad block does not take the rest of the file down with it.
        var good = context.AssetStore.ObjectDefinitions.GetByName("AfterGarbageInt");
        Assert.NotNull(good);
        Assert.Equal(4, good.TransportSlotCount);
    }
}

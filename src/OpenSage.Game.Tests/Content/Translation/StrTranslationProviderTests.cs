using System.IO;
using System.Text;
using OpenSage.Content.Translation.Providers;
using Xunit;

namespace OpenSage.Tests.Content.Translation;

/// <summary>
/// Parser coverage for the malformations real <c>.str</c> content contains. Every quirk below is
/// quoted from Age of the Ring 12.0 (<c>aotr/data/lotr.str</c> and <c>aotr/maps/*/map.str</c>),
/// which the parser refused outright before these were handled - see
/// <c>boot-crash-metal-r14.md</c> §5.
/// </summary>
public class StrTranslationProviderTests
{
    private static StrTranslationProvider Parse(string contents)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(contents));
        return new StrTranslationProvider(stream, "english");
    }

    [Fact]
    public void ReadsAPlainEntry()
    {
        var provider = Parse("OBJECT:GondorDamrod\r\n\"Damrod\"\r\nEND\r\n");

        Assert.Equal("Damrod", provider.GetString("OBJECT:GondorDamrod"));
    }

    /// <summary>
    /// Verbatim from <c>aotr/data/lotr.str</c>: campaign maps are labelled with the map's name,
    /// spaces and all. Ending the label at the first space made "GOOD" parse as the value and the
    /// rest of the line desync the state machine ("Unexpected token D" while looking for END).
    /// </summary>
    [Fact]
    public void LabelsMayContainSpaces()
    {
        var provider = Parse(
            "Map:MAP GOOD REDHORN\r\n\"Good Redhorn\"\r\nEND\r\n\r\n" +
            "Map:MAP GOOD COUNCIL OF ELROND\r\n\"Good Council of Elrond\"\r\nEND\r\n");

        Assert.Equal("Good Redhorn", provider.GetString("Map:MAP GOOD REDHORN"));
        Assert.Equal("Good Council of Elrond", provider.GetString("Map:MAP GOOD COUNCIL OF ELROND"));
    }

    /// <summary>
    /// Shape taken from <c>aotr/maps/map good helms deep/map.str</c>
    /// (<c>SCRIPT:Hint_HelmsDeep_Start Fight</c>).
    /// </summary>
    [Fact]
    public void LabelWithSpacesIsLookedUpWithItsSpaces()
    {
        var provider = Parse("SCRIPT:Hint_HelmsDeep_Start Fight\r\n\"Defend the Deeping Wall!\"\r\nEND\r\n");

        Assert.Equal("Defend the Deeping Wall!", provider.GetString("SCRIPT:Hint_HelmsDeep_Start Fight"));
    }

    /// <summary>
    /// <c>CONTROLBAR:ToolTipBuildCorsairsOfUmbarHorde</c> in <c>aotr/data/lotr.str</c> quotes
    /// in-world prose inside its value without escaping the quote. Retail's readToEndOfQuote ends
    /// the value at that quote and then skips whole lines until one reads END, so the trailing
    /// prose is discarded rather than scanned for an E-N-D triple.
    /// </summary>
    [Fact]
    public void ValueEndsAtAnUnescapedQuoteAndTheRestOfTheEntryIsSkipped()
    {
        var provider = Parse(
            "CONTROLBAR:ToolTipBuildCorsairsOfUmbarHorde\r\n" +
            "\"Strong vs. Pikemen and Structures\r\n" +
            " \r\n" +
            " \"There is a great fleet drawing near to the mouths of Anduin, manned by the\r\n" +
            "corsairs of Umbar in the South.\"\r\n" +
            "END\r\n" +
            "\r\n" +
            "CONTROLBAR:Next\r\n\"Next\"\r\nEND\r\n");

        Assert.Equal(
            "Strong vs. Pikemen and Structures\r\n \r\n ",
            provider.GetString("CONTROLBAR:ToolTipBuildCorsairsOfUmbarHorde"));

        // The entry after the malformed one must still be read.
        Assert.Equal("Next", provider.GetString("CONTROLBAR:Next"));
    }

    /// <summary>
    /// <c>END</c> is only END at the start of a line. A value whose prose contains the letters
    /// e-n-d mid-line must not terminate the entry early.
    /// </summary>
    [Fact]
    public void EndIsOnlyRecognisedAtTheStartOfALine()
    {
        var provider = Parse(
            "CONTROLBAR:Defend\r\n\"Defend the wall\" endless\r\nEND\r\n" +
            "CONTROLBAR:Next\r\n\"Next\"\r\nEND\r\n");

        Assert.Equal("Defend the wall", provider.GetString("CONTROLBAR:Defend"));
        Assert.Equal("Next", provider.GetString("CONTROLBAR:Next"));
    }

    /// <summary>
    /// <c>aotr/data/lotr.str</c> contains one label line with no <c>CATEGORY:</c> prefix at all
    /// (<c>TooltipRisenCarrionDebuff</c>, sitting between <c>CONTROLBAR:</c> entries). Retail keeps
    /// the whole line as the label, so the entry exists but no CATEGORY:LABEL lookup reaches it;
    /// the file must still parse.
    /// </summary>
    [Fact]
    public void LabelWithoutACategoryDoesNotFailTheFile()
    {
        var provider = Parse(
            "CONTROLBAR:RisenCarrionDebuff\r\n\"TBD\"\r\nEnd\r\n\r\n" +
            "TooltipRisenCarrionDebuff\r\n\"Modifier Type: Passive Debuff\"\r\nEnd\r\n\r\n" +
            "CONTROLBAR:SpecialAbilityWarriorsReach\r\n\"Quaking\"\r\nEnd\r\n");

        Assert.Equal("TBD", provider.GetString("CONTROLBAR:RisenCarrionDebuff"));
        Assert.Equal("Quaking", provider.GetString("CONTROLBAR:SpecialAbilityWarriorsReach"));
    }

    /// <summary>
    /// The malformation that was already tolerated before: BFME2's stock table has an entry whose
    /// value ends with two quotes.
    /// </summary>
    [Fact]
    public void TrailingJunkAfterAValueIsIgnored()
    {
        var provider = Parse(
            "OBJECT:Typo\r\n\"Value\"\"\r\nEND\r\n" +
            "OBJECT:Next\r\n\"Next\"\r\nEND\r\n");

        Assert.Equal("Value", provider.GetString("OBJECT:Typo"));
        Assert.Equal("Next", provider.GetString("OBJECT:Next"));
    }

    /// <summary>
    /// Comment lines between the value and END are skipped, as are commented-out values.
    /// <c>aotr/data/lotr.str</c> writes <c>/////"Dread Mask"</c> above the live value.
    /// </summary>
    [Fact]
    public void CommentedOutValuesAreSkipped()
    {
        var provider = Parse(
            "CONTROLBAR:RisenCarrionDebuff\r\n/////\"Dread Mask\"\r\n\"TBD\"\r\nEnd\r\n");

        Assert.Equal("TBD", provider.GetString("CONTROLBAR:RisenCarrionDebuff"));
    }
}

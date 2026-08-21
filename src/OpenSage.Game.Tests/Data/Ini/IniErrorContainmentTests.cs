using System.Linq;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniErrorContainmentTests
{
    [Fact]
    public void ErrorInOneBlockDoesNotHideLaterBlocks()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Armor BadArmor\n" +
            "  NotARealField = 5\n" +
            "End\n" +
            "\n" +
            "Armor GoodArmor\n" +
            "  Armor = DEFAULT 50%\n" +
            "End\n");

        Assert.NotNull(context.AssetStore.ArmorTemplates.GetByName("GoodArmor"));
        Assert.Single(parser.ParseErrors);
        Assert.Contains("NotARealField", parser.ParseErrors[0].Message);
    }

    [Fact]
    public void UnknownTopLevelBlockIsSkipped()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "NotARealBlockType Something\n" +
            "  Field = Value\n" +
            "End\n" +
            "\n" +
            "Armor GoodArmor\n" +
            "  Armor = DEFAULT 50%\n" +
            "End\n");

        Assert.NotNull(context.AssetStore.ArmorTemplates.GetByName("GoodArmor"));
        Assert.Single(parser.ParseErrors);
        Assert.Contains("NotARealBlockType", parser.ParseErrors[0].Message);
    }

    [Fact]
    public void MultipleBadBlocksAreAllReported()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Armor BadArmor1\n" +
            "  NotARealField = 5\n" +
            "End\n" +
            "Armor GoodArmor\n" +
            "  Armor = DEFAULT 50%\n" +
            "End\n" +
            "Armor BadArmor2\n" +
            "  AlsoNotAField = 6\n" +
            "End\n");

        Assert.NotNull(context.AssetStore.ArmorTemplates.GetByName("GoodArmor"));
        Assert.Equal(2, parser.ParseErrors.Count);
    }

    [Fact]
    public void ErrorPositionIsRecorded()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Armor BadArmor\n" +
            "  NotARealField = 5\n" +
            "End\n");

        var error = Assert.Single(parser.ParseErrors);
        Assert.Equal(2, error.Position.Line);
    }

    [Fact]
    public void FileEndingInsideABlockIsContained()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Armor TruncatedArmor\n" +
            "  Armor = DEFAULT 50%\n");

        Assert.Single(parser.ParseErrors);
    }

    [Fact]
    public void CleanFileReportsNoErrors()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Armor GoodArmor\n" +
            "  Armor = DEFAULT 50%\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);
        Assert.NotNull(context.AssetStore.ArmorTemplates.GetByName("GoodArmor"));
    }
}

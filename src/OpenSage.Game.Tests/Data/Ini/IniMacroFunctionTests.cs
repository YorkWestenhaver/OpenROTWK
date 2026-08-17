using OpenSage.Data.Ini;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniMacroFunctionTests
{
    [Fact]
    public void MacroFunctionInDefineIsEvaluatedLazily()
    {
        var context = new IniParseTestContext();

        // The macro function's argument is only defined *after* the macro
        // using it — the real engine resolves this, so evaluation must happen
        // at the use site, not at definition time.
        context.ParseFileText(
            "#define DERIVED #ADD( BASE_VALUE 10 )\n" +
            "#define BASE_VALUE 5\n");

        var parser = context.CreateParser("DERIVED");
        parser.GoToNextLine();

        Assert.Equal(15f, parser.ParseFloat());
    }

    [Fact]
    public void MacroFunctionsCanNestThroughDefines()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(
            "#define DOUBLED #MULTIPLY( DERIVED 2 )\n" +
            "#define DERIVED #ADD( BASE_VALUE 10 )\n" +
            "#define BASE_VALUE 5\n");

        var parser = context.CreateParser("DOUBLED");
        parser.GoToNextLine();

        Assert.Equal(30f, parser.ParseFloat());
    }

    [Fact]
    public void RedefiningAnArgumentChangesLaterUses()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(
            "#define DERIVED #SUBTRACT( BASE_VALUE 1 )\n" +
            "#define BASE_VALUE 5\n");

        var parser = context.CreateParser("DERIVED");
        parser.GoToNextLine();
        Assert.Equal(4f, parser.ParseFloat());

        // Text expansion semantics: the value tracks the current definition.
        context.ParseFileText("#define BASE_VALUE 10\n");

        parser = context.CreateParser("DERIVED");
        parser.GoToNextLine();
        Assert.Equal(9f, parser.ParseFloat());
    }

    [Fact]
    public void MacroFunctionDirectlyInFieldValueStillWorks()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define BASE_VALUE 8\n");

        var parser = context.CreateParser("#DIVIDE( BASE_VALUE 2 )");
        parser.GoToNextLine();

        Assert.Equal(4f, parser.ParseFloat());
    }
}

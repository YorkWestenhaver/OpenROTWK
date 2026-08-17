using OpenSage.Data.Ini;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniMacroTests
{
    [Fact]
    public void MultiTokenDefineIsStoredCompletely()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define MY_NUMBERS 1 2 3\n");

        var parser = context.CreateParser("MY_NUMBERS");
        parser.GoToNextLine();
        var values = parser.ParseFloatArray();

        Assert.Equal(new[] { 1f, 2f, 3f }, values);
    }

    [Fact]
    public void MultiTokenObjectFilterDefineExpandsAtUseSite()
    {
        var context = new IniParseTestContext();

        // Mods define whole object filters as macros; every token must survive.
        context.ParseFileText("#define MY_FILTER ANY +INFANTRY -STRUCTURE\n");

        var parser = context.CreateParser("MY_FILTER");
        parser.GoToNextLine();
        var filter = ObjectFilter.Parse(parser);

        Assert.True(filter.Rules.Get(ObjectFilterRule.Any));
        Assert.True(filter.Include.Get(ObjectKinds.Infantry));
        Assert.True(filter.Exclude.Get(ObjectKinds.Structure));
    }

    [Fact]
    public void DefineCanReferenceEarlierDefine()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define BASE_VALUE 5\n#define ALIAS BASE_VALUE\n");

        var parser = context.CreateParser("ALIAS");
        parser.GoToNextLine();

        Assert.Equal(5f, parser.ParseFloat());
    }

    [Fact]
    public void RedefinitionOverwritesEarlierDefine()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define VALUE 5\n#define VALUE 7\n");

        var parser = context.CreateParser("VALUE");
        parser.GoToNextLine();

        Assert.Equal(7f, parser.ParseFloat());
    }

    [Fact]
    public void SelfReferentialMultiTokenDefineThrowsInsteadOfStreamingForever()
    {
        var context = new IniParseTestContext();

        // 'A' expands to '1 A': a consumer that reads tokens until exhaustion
        // (filters, arrays) would otherwise receive an infinite token stream —
        // each expansion happens in a separate GetNextTokenOptional call, so
        // only a per-line expansion budget can catch it.
        context.ParseFileText("#define A 1 A\n");

        var parser = context.CreateParser("A");
        parser.GoToNextLine();

        Assert.Throws<IniParseException>(() => parser.ParseFloatArray());
    }

    [Fact]
    public void CyclicDefinesThrowInsteadOfLooping()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define A B\n");
        context.ParseFileText("#define B A\n");

        var parser = context.CreateParser("A");
        parser.GoToNextLine();

        Assert.Throws<IniParseException>(() => parser.ParseFloat());
    }
}

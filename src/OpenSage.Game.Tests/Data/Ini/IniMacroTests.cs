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
    public void MacroIsNotReExpandedInsideItsOwnExpansion()
    {
        var context = new IniParseTestContext();

        // Live mod data defines macros named after object-filter keywords whose
        // bodies contain that same keyword (e.g. '#define ALL ALL +X -Y').
        // C-preprocessor semantics: within a macro's expansion, its own name is
        // a literal token, so this must not recurse.
        context.ParseFileText("#define ALL ALL +INFANTRY\n");

        var parser = context.CreateParser("ALL -STRUCTURE");
        parser.GoToNextLine();
        var filter = ObjectFilter.Parse(parser);

        Assert.True(filter.Rules.Get(ObjectFilterRule.All));
        Assert.True(filter.Include.Get(ObjectKinds.Infantry));
        Assert.True(filter.Exclude.Get(ObjectKinds.Structure));
    }

    /// <summary>
    /// A '#define' whose name is already a macro must still define that same name — the name
    /// token is never macro-expanded. Real data re-parses shared files (a preload pass plus the
    /// main sweep), so every macro in them is redefined at least once.
    /// </summary>
    [Fact]
    public void RedefinitionDoesNotExpandTheNameBeingDefined()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define MY_FILTER ALL -STRUCTURE\n");
        context.ParseFileText("#define MY_FILTER ALL -INFANTRY\n");

        // The second #define updated MY_FILTER...
        Assert.True(context.DataContext.Defines.ContainsKey("MY_FILTER"));

        // ...and did not create a macro named after the first body's leading token.
        Assert.False(context.DataContext.Defines.ContainsKey("ALL"));

        var parser = context.CreateParser("MY_FILTER");
        parser.GoToNextLine();
        var filter = ObjectFilter.Parse(parser);

        Assert.True(filter.Rules.Get(ObjectFilterRule.All));
        Assert.True(filter.Exclude.Get(ObjectKinds.Infantry));
        Assert.False(filter.Exclude.Get(ObjectKinds.Structure));
    }

    /// <summary>
    /// The consequence of the bug above: a bogus 'ALL' macro made every plain 'ALL' keyword in
    /// unrelated fields expand into an object filter.
    /// </summary>
    [Fact]
    public void RedefinitionDoesNotPoisonTheAllKeyword()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define MY_FILTER ALL -STRUCTURE\n");
        context.ParseFileText("#define MY_FILTER ALL -STRUCTURE\n");

        var parser = context.CreateParser("ALL");
        parser.GoToNextLine();

        Assert.Equal("ALL", parser.GetNextTokenOptional()!.Value.Text);
        Assert.Null(parser.GetNextTokenOptional());
    }

    [Fact]
    public void CyclicDefinesTerminateInsteadOfLooping()
    {
        var context = new IniParseTestContext();

        context.ParseFileText("#define A B\n");
        context.ParseFileText("#define B A\n");

        var parser = context.CreateParser("A");
        parser.GoToNextLine();

        // A -> B -> A(literal): the cycle terminates and the literal token is
        // surfaced ('A' here, which is not a number).
        var token = parser.GetNextTokenOptional();
        Assert.Equal("A", token!.Value.Text);
    }
}

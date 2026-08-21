using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniChildObjectTests
{
    private const string ParentBlock =
        "Object ParentObject\n" +
        "  VisionRange = 100.0\n" +
        "  TransportSlotCount = 5\n" +
        "End\n";

    private const string ChildBlock =
        "ChildObject ChildObject_A ParentObject\n" +
        "  VisionRange = 200.0\n" +
        "End\n";

    [Fact]
    public void ChildObjectAfterParentInheritsAndOverrides()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(ParentBlock + "\n" + ChildBlock);

        var child = context.AssetStore.ObjectDefinitions.GetByName("ChildObject_A");
        Assert.NotNull(child);
        Assert.Equal(200f, child.VisionRange);         // overridden
        Assert.Equal(5, child.TransportSlotCount);     // inherited
        Assert.Empty(parser.ParseErrors);
    }

    [Fact]
    public void ChildObjectBeforeParentInSameFileIsDeferredAndResolved()
    {
        var context = new IniParseTestContext();

        // Forward reference: child block first, parent defined later in the file.
        var parser = context.ParseFileText(ChildBlock + "\n" + ParentBlock);

        var child = context.AssetStore.ObjectDefinitions.GetByName("ChildObject_A");
        Assert.NotNull(child);
        Assert.Equal(200f, child.VisionRange);
        Assert.Equal(5, child.TransportSlotCount);
        Assert.Empty(parser.ParseErrors);
        Assert.Empty(context.DataContext.PendingChildObjects);
    }

    [Fact]
    public void ChildObjectWithParentInLaterFileIsDeferredAndResolved()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(ChildBlock, @"Data\INI\Object\a_child.ini");
        Assert.Null(context.AssetStore.ObjectDefinitions.GetByName("ChildObject_A"));

        context.ParseFileText(ParentBlock, @"Data\INI\Object\z_parent.ini");

        var child = context.AssetStore.ObjectDefinitions.GetByName("ChildObject_A");
        Assert.NotNull(child);
        Assert.Equal(200f, child.VisionRange);
        Assert.Equal(5, child.TransportSlotCount);
        Assert.Empty(context.DataContext.PendingChildObjects);
    }

    [Fact]
    public void ChainsOfDeferredChildrenResolveToAFixpoint()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "ChildObject GrandChild ChildObject_A\n" +
            "  CrusherLevel = 3\n" +
            "End\n" +
            ChildBlock +
            ParentBlock);

        var grandChild = context.AssetStore.ObjectDefinitions.GetByName("GrandChild");
        Assert.NotNull(grandChild);
        Assert.Equal(3, grandChild.CrusherLevel);      // own
        Assert.Equal(200f, grandChild.VisionRange);    // from ChildObject_A
        Assert.Equal(5, grandChild.TransportSlotCount);// from ParentObject
        Assert.Empty(parser.ParseErrors);
        Assert.Empty(context.DataContext.PendingChildObjects);
    }

    [Fact]
    public void ObjectReskinBeforeTargetIsDeferredAndResolved()
    {
        var context = new IniParseTestContext();

        context.ParseFileText(
            "ObjectReskin Reskin_A ParentObject\n" +
            "  VisionRange = 300.0\n" +
            "End\n" +
            ParentBlock);

        var reskin = context.AssetStore.ObjectDefinitions.GetByName("Reskin_A");
        Assert.NotNull(reskin);
        Assert.Equal(300f, reskin.VisionRange);
        Assert.Equal(5, reskin.TransportSlotCount);
    }

    [Fact]
    public void ChildObjectWithMissingParentDoesNotAbortTheFile()
    {
        var context = new IniParseTestContext();

        // The parent never appears; the block must not kill the rest of the file.
        var parser = context.ParseFileText(
            "ChildObject Orphan NoSuchParent\n" +
            "  VisionRange = 50.0\n" +
            "End\n" +
            "Armor GoodArmor\n" +
            "  Armor = DEFAULT 50%\n" +
            "End\n");

        Assert.NotNull(context.AssetStore.ArmorTemplates.GetByName("GoodArmor"));
        Assert.Empty(parser.ParseErrors);
        var pending = Assert.Single(context.DataContext.PendingChildObjects);
        Assert.Equal("Orphan", pending.Name);
        Assert.Equal("NoSuchParent", pending.ParentName);
    }
}

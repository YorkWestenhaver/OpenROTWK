using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniUpgradeModuleTests
{
    private static ObjectDefinition ParseObject(IniParseTestContext context, string body)
    {
        var parser = context.ParseFileText(
            "Object PermanentUpgradeObject\n" + body + "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName("PermanentUpgradeObject");
        Assert.NotNull(definition);
        return definition;
    }

    [Fact]
    public void PermanentIsAcceptedOnUpgradeMuxModules()
    {
        var context = new IniParseTestContext();

        var definition = ParseObject(context,
            "  Behavior = ObjectCreationUpgrade ModuleTag_01\n" +
            "    Permanent = Yes\n" +
            "  End\n");

        var module = Assert.IsType<ObjectCreationUpgradeModuleData>(
            Assert.Single(definition.Behaviors).Value.Data);
        Assert.True(module.UpgradeData.Permanent);
    }

    [Fact]
    public void PermanentDefaultsToFalse()
    {
        var context = new IniParseTestContext();

        var definition = ParseObject(context,
            "  Behavior = ObjectCreationUpgrade ModuleTag_01\n" +
            "  End\n");

        var module = Assert.IsType<ObjectCreationUpgradeModuleData>(
            Assert.Single(definition.Behaviors).Value.Data);
        Assert.False(module.UpgradeData.Permanent);
    }

    /// <summary>
    /// AttributeModifierAuraUpdate duplicates the upgrade-mux fields instead of deriving from
    /// UpgradeModuleData, so it carries its own Permanent.
    /// </summary>
    [Fact]
    public void PermanentIsAcceptedOnAttributeModifierAuraUpdate()
    {
        var context = new IniParseTestContext();

        var definition = ParseObject(context,
            "  Behavior = AttributeModifierAuraUpdate ModuleTag_01\n" +
            "    Permanent = Yes\n" +
            "  End\n");

        var module = Assert.IsType<AttributeModifierAuraUpdateModuleData>(
            Assert.Single(definition.Behaviors).Value.Data);
        Assert.True(module.Permanent);
    }
}

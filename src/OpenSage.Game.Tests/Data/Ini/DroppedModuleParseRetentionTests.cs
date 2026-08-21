// L5-P1: formal drop of 5 [ParseOnly] modules the sufficiency census (§3.3, "Dead weight —
// five") shows have zero live module-position uses in AotR: HeroDie, RainOfFireUpdate,
// OilSpillUpdate, GateProxyBehavior, DelayedLuaEventUpdate. This is a verdict, not a deletion:
// the classes stay [ParseOnly] (their Note now begins "DROPPED-R15" — see the convention
// documented in ModulePorting.cs) so that any content still authoring the keyword continues to
// parse cleanly and dispatch through BehaviorModuleData's keyword table exactly as before;
// only the porting backlog claim is retracted. These tests assert both halves of that contract:
// parsing keeps working, and the module keeps contributing nothing at runtime (the base
// ModuleData.CreateModule returns null, so GameObject's behavior-instantiation loop skips it).

using System.Reflection;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class DroppedModuleParseRetentionTests : MockedGameTest
{
    private static ObjectDefinition ParseObject(string objectName, string behaviorBlock, string preamble = "")
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            preamble +
            $"Object {objectName}\n" +
            "  KindOf = STRUCTURE\n" +
            "  Body = ActiveBody ModuleTag_Body\n" +
            "    MaxHealth = 100\n" +
            "  End\n" +
            behaviorBlock +
            "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName(objectName);
        Assert.NotNull(definition);
        return definition;
    }

    // INT-R1B: ObjectDefinition.cs:308 files the "Body =" block into Behaviors alongside the
    // "Behavior =" ones, so the fixture's own ActiveBody is always a second entry and
    // Assert.Single(definition.Behaviors) can never hold. Select the dropped module by its
    // tag instead - that is what these tests were actually about.
    private static ModuleData DroppedModuleOf(ObjectDefinition definition)
    {
        Assert.True(
            definition.Behaviors.TryGetValue("ModuleTag_Dropped", out var container),
            "the dropped module did not land in Behaviors under its own tag");
        return container.Data;
    }

    // ---- each dropped module still parses through the real "Behavior =" keyword dispatch ----

    [Fact]
    public void HeroDie_StillParsesUnderTheDieKeywordDispatch()
    {
        var definition = ParseObject(
            "DropTestObject_HeroDie",
            "  Behavior = HeroDie ModuleTag_Dropped\n" +
            "    SpecialPowerTemplate = SpecialPower_Placeholder\n" +
            "  End\n");

        var data = Assert.IsType<HeroDieModuleData>(DroppedModuleOf(definition));
        Assert.Equal("SpecialPower_Placeholder", data.SpecialPowerTemplate);
    }

    [Fact]
    public void RainOfFireUpdate_StillParsesUnderTheUpdateKeywordDispatch()
    {
        var definition = ParseObject(
            "DropTestObject_RainOfFire",
            "  Behavior = RainOfFireUpdate ModuleTag_Dropped\n" +
            "    StartRainTime = 1000\n" +
            "    DPSMin = 1.0\n" +
            "    DPSMax = 2.0\n" +
            "  End\n");

        var data = Assert.IsType<RainOfFireUpdateModuleData>(DroppedModuleOf(definition));
        Assert.Equal(1000, data.StartRainTime);
        Assert.Equal(2.0f, data.DpsMax);
    }

    [Fact]
    public void OilSpillUpdate_StillParsesUnderTheUpdateKeywordDispatch()
    {
        var definition = ParseObject(
            "DropTestObject_OilSpill",
            "  Behavior = OilSpillUpdate ModuleTag_Dropped\n" +
            "    BreadcrumbName = OilSpillBreadcrumb\n" +
            "    AliveOnly = Yes\n" +
            "  End\n");

        var data = Assert.IsType<OilSpillUpdateModuleData>(DroppedModuleOf(definition));
        Assert.Equal("OilSpillBreadcrumb", data.BreadcrumbName);
        Assert.True(data.AliveOnly);
    }

    [Fact]
    public void GateProxyBehavior_StillParsesUnderTheBehaviorKeywordDispatch()
    {
        var definition = ParseObject(
            "DropTestObject_GateProxy",
            "  Behavior = GateProxyBehavior ModuleTag_Dropped\n" +
            "  End\n");

        Assert.IsType<GateProxyBehaviorModuleData>(DroppedModuleOf(definition));
    }

    [Fact]
    public void DelayedLuaEventUpdate_StillParsesUnderTheUpdateKeywordDispatch()
    {
        var definition = ParseObject(
            "DropTestObject_DelayedLuaEvent",
            "  Behavior = DelayedLuaEventUpdate ModuleTag_Dropped\n" +
            "  End\n");

        Assert.IsType<DelayedLuaEventUpdateModuleData>(DroppedModuleOf(definition));
    }

    // ---- parsing one of the dropped modules contributes zero runtime behavior modules ----
    // (the base BehaviorModuleData.CreateModule returns null for every [ParseOnly] class that
    // doesn't override it, and none of these five do; GameObject's instantiation loop then
    // skips adding anything for that declaration, matching a plain undeclared-behavior baseline)

    [Fact]
    public void AllFiveDroppedModules_ContributeNoRuntimeBehaviorBeyondTheBaseline()
    {
        var context = new IniParseTestContext();
        var parser = context.ParseFileText(
            "Object DropTestObject_Baseline\n" +
            "  KindOf = STRUCTURE\n" +
            "  Body = ActiveBody ModuleTag_Body\n" +
            "    MaxHealth = 100\n" +
            "  End\n" +
            "End\n" +
            "Object DropTestObject_AllFive\n" +
            "  KindOf = STRUCTURE\n" +
            "  Body = ActiveBody ModuleTag_Body\n" +
            "    MaxHealth = 100\n" +
            "  End\n" +
            "  Behavior = HeroDie ModuleTag_Dropped1\n" +
            "    SpecialPowerTemplate = SpecialPower_Placeholder\n" +
            "  End\n" +
            "  Behavior = RainOfFireUpdate ModuleTag_Dropped2\n" +
            "  End\n" +
            "  Behavior = OilSpillUpdate ModuleTag_Dropped3\n" +
            "  End\n" +
            "  Behavior = GateProxyBehavior ModuleTag_Dropped4\n" +
            "  End\n" +
            "  Behavior = DelayedLuaEventUpdate ModuleTag_Dropped5\n" +
            "  End\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);

        var baselineDefinition = context.AssetStore.ObjectDefinitions.GetByName("DropTestObject_Baseline");
        var allFiveDefinition = context.AssetStore.ObjectDefinitions.GetByName("DropTestObject_AllFive");
        Assert.NotNull(baselineDefinition);
        Assert.NotNull(allFiveDefinition);

        // The five dropped Behavior= declarations parsed - measured as a DELTA against the
        // baseline object, because Behaviors also holds the shared "Body = ActiveBody" entry
        // (ObjectDefinition.cs:308) that both fixtures declare.
        Assert.Equal(5, allFiveDefinition.Behaviors.Count - baselineDefinition.Behaviors.Count);

        var baselineObject = new GameObject(baselineDefinition, Generals.GameEngine, Generals.PlayerManager.GetPlayerByIndex(0));
        var allFiveObject = new GameObject(allFiveDefinition, Generals.GameEngine, Generals.PlayerManager.GetPlayerByIndex(0));

        // ...but none of them produced a live BehaviorModule: the declared-object's runtime
        // behavior count equals the undeclared baseline's, module for module.
        Assert.Equal(baselineObject.BehaviorModules.Count, allFiveObject.BehaviorModules.Count);
    }

    // ---- the convention itself: every dropped module's [ParseOnly] Note is a DROPPED-R15 verdict ----

    [Theory]
    [InlineData(typeof(HeroDieModuleData))]
    [InlineData(typeof(RainOfFireUpdateModuleData))]
    [InlineData(typeof(OilSpillUpdateModuleData))]
    [InlineData(typeof(GateProxyBehaviorModuleData))]
    [InlineData(typeof(DelayedLuaEventUpdateModuleData))]
    public void DroppedModule_CarriesTheDroppedR15VerdictConvention(System.Type moduleDataType)
    {
        var attribute = moduleDataType.GetCustomAttribute<ParseOnlyAttribute>();

        Assert.NotNull(attribute);
        Assert.StartsWith("DROPPED-R15", attribute.Note);
        Assert.Contains("§3.3", attribute.Note);
    }
}

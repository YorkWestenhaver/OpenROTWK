using System.Linq;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

/// <summary>
/// Parse-level regression tests for the four fixes in spec-dynamic-portal.md §3.2
/// (g2-portal): the missing WallBoundsMesh field, TopAttackRadius's retail type/default,
/// AboveWall's retail default, and the shared upgrade-mux field block replacing the
/// module's former private TriggeredBy/ConflictsWith/CustomAnimAndDuration copies, plus the
/// R15 L5-P8 Link reshape (§5.6, §6: a flat [From, Via..., To] vector in authored order).
/// These assert directly on the parsed ModuleData rather than spawning a GameObject; the
/// runtime module the L5-P8 port added is covered by
/// Logic/Object/Behaviors/DynamicPortalBehaviorContractTests.cs.
/// </summary>
public class DynamicPortalBehaviorIniTests
{
    private static DynamicPortalBehaviorModuleData ParsePortal(string body, string preamble = "")
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            preamble +
            "Object PortalObject\n" +
            "  Behavior = DynamicPortalBehaviour ModuleTag_01\n" +
            body +
            "  End\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName("PortalObject");
        Assert.NotNull(definition);

        return Assert.IsType<DynamicPortalBehaviorModuleData>(
            Assert.Single(definition.Behaviors).Value.Data);
    }

    private const string UpgradeMuxPreamble =
        "Upgrade Upgrade_PosternGate\n" +
        "  Type = PLAYER\n" +
        "End\n" +
        "Upgrade Upgrade_PosternGateConflict\n" +
        "  Type = PLAYER\n" +
        "End\n";

    // Spelling is load-bearing (spec §0): the retail/INI keyword is the British
    // "DynamicPortalBehaviour", registered against the American-spelled class.
    [Fact]
    public void RegistersUnderBritishSpelling()
    {
        var module = ParsePortal(
            "    BonePrefix = Post\n" +
            "    NumberOfBones = 1\n");

        Assert.NotNull(module);
    }

    [Fact]
    public void WallBoundsMesh_Parses()
    {
        var module = ParsePortal(
            "    WallBoundsMesh = WallSection01\n");

        Assert.Equal("WallSection01", module.WallBoundsMesh);
    }

    [Fact]
    public void WallBoundsMesh_DefaultsToEmpty()
    {
        var module = ParsePortal(
            "    BonePrefix = Post\n");

        Assert.Equal(string.Empty, module.WallBoundsMesh);
    }

    [Fact]
    public void TopAttackRadius_ParsesFractionalValue()
    {
        // Retail parses this as a float (spec §3.1 row 9, §3.2 gap 2). AotR only ever
        // authors whole numbers, so a fractional value is the discriminating case: an
        // int-typed field would either fail to parse this or silently truncate it.
        var module = ParsePortal(
            "    TopAttackRadius = 12.5\n");

        Assert.Equal(12.5f, module.TopAttackRadius);
    }

    [Fact]
    public void TopAttackRadius_DefaultsToRetailValue()
    {
        // Retail ctor default is 5.0f, not 0 (spec §3.2 gap 4, §3.3).
        var module = ParsePortal(
            "    BonePrefix = Post\n");

        Assert.Equal(5.0f, module.TopAttackRadius);
    }

    [Fact]
    public void AboveWall_DefaultsToNegativeOne()
    {
        // Retail ctor default is -1, the "no wall-top dock" sentinel (spec §3.2 gap 3,
        // §3.3, §5.4) - not 0, which every postern-gate AotR instance (which omits
        // AboveWall entirely) would otherwise misread as "waypoint 0 is the dock point".
        var module = ParsePortal(
            "    BonePrefix = Post\n");

        Assert.Equal(-1, module.AboveWall);
    }

    [Fact]
    public void AboveWall_ParsesExplicitValue()
    {
        var module = ParsePortal(
            "    BonePrefix = Ladder\n" +
            "    NumberOfBones = 4\n" +
            "    WayPoint = Index:0 Type:PreClimb\n" +
            "    WayPoint = Index:1 Type:PreClimb\n" +
            "    WayPoint = Index:2 Type:Climb\n" +
            "    WayPoint = Index:3 Type:Climb\n" +
            "    WayPoint = Index:2 Type:Climb\n" +
            "    WayPoint = Index:1 Type:Climb\n" +
            "    Link = From:0 Via:4 Via:5 To:3\n" +
            "    Link = From:3 Via:1 Via:2 To:0\n" +
            "    AboveWall = 3\n");

        Assert.Equal(3, module.AboveWall);
    }

    // The shared upgrade-mux block (UpgradeModuleData.UpgradeData): TriggeredBy,
    // ConflictsWith and CustomAnimAndDuration must still parse (they did before, as
    // private copies) and RequiresAllTriggers/RequiresAllConflictingTriggers/Permanent
    // must now parse too (spec §3.1, §3.2 gap 5, §6).
    [Fact]
    public void UpgradeMuxFields_AllParseThroughSharedBlock()
    {
        var module = ParsePortal(
            "    TriggeredBy = Upgrade_PosternGate\n" +
            "    ConflictsWith = Upgrade_PosternGateConflict\n" +
            "    RequiresAllTriggers = Yes\n" +
            "    RequiresAllConflictingTriggers = Yes\n" +
            "    Permanent = Yes\n" +
            "    CustomAnimAndDuration = AnimState:DOOR_1_OPENING AnimTime:1\n",
            UpgradeMuxPreamble);

        Assert.Single(module.UpgradeData.TriggeredBy);
        Assert.Equal("Upgrade_PosternGate", module.UpgradeData.TriggeredBy[0].Value.Name);
        Assert.Single(module.UpgradeData.ConflictsWith);
        Assert.Equal("Upgrade_PosternGateConflict", module.UpgradeData.ConflictsWith[0].Value.Name);
        Assert.True(module.UpgradeData.RequiresAllTriggers);
        Assert.True(module.UpgradeData.RequiresAllConflictingTriggers);
        Assert.True(module.UpgradeData.Permanent);
        Assert.NotNull(module.UpgradeData.CustomAnimAndDuration);
    }

    [Fact]
    public void RequiresAllTriggers_DefaultsToFalse()
    {
        var module = ParsePortal(
            "    TriggeredBy = Upgrade_PosternGate\n",
            UpgradeMuxPreamble);

        Assert.False(module.UpgradeData.RequiresAllTriggers);
        Assert.False(module.UpgradeData.RequiresAllConflictingTriggers);
        Assert.False(module.UpgradeData.Permanent);
    }

    [Fact]
    public void Link_ParsesAsAFlatRouteInAuthoredOrder()
    {
        // Retail stores and consumes a Link row as a flat, variable-length int vector in
        // authored order - [From, Via1..ViaN, To] - and its chaining loop walks consecutive
        // pairs of it (spec §5.6, §6). The former {From, Vias, To} shape round-tripped the
        // same data but would make any consumer reproduce the wrong iteration bound.
        var module = ParsePortal(
            "    BonePrefix = Ladder\n" +
            "    NumberOfBones = 4\n" +
            "    Link = From:0 Via:4 Via:5 To:3\n");

        var link = Assert.Single(module.Links);
        Assert.Equal(new[] { 0, 4, 5, 3 }, link.Route);
        Assert.Equal(0, link.From);
        Assert.Equal(3, link.To);
        Assert.Equal(new[] { 4, 5 }, link.Vias);
    }

    [Fact]
    public void Link_WithNoVias_IsATwoElementRoute()
    {
        var module = ParsePortal(
            "    BonePrefix = Post\n" +
            "    Link = From:0 To:1\n");

        var link = Assert.Single(module.Links);
        Assert.Equal(new[] { 0, 1 }, link.Route);
        Assert.Empty(link.Vias);
    }

    [Fact]
    public void WayPoint_IndexIsABoneIndex_AndListPositionIsTheWaypointNumber()
    {
        // Three separate index spaces (spec §2.1): bones 1 and 2 each appear twice, once on
        // the way up and once on the way down, so the six-entry list is not a bone list.
        var module = ParsePortal(
            "    BonePrefix = Ladder\n" +
            "    NumberOfBones = 4\n" +
            "    WayPoint = Index:0 Type:PreClimb\n" +
            "    WayPoint = Index:1 Type:PreClimb\n" +
            "    WayPoint = Index:2 Type:Climb\n" +
            "    WayPoint = Index:3 Type:Climb\n" +
            "    WayPoint = Index:2 Type:Climb\n" +
            "    WayPoint = Index:1 Type:Climb\n");

        Assert.Equal(new[] { 0, 1, 2, 3, 2, 1 }, module.WayPoints.Select(w => w.Index));
        Assert.Equal(DynamicPortalWayPointType.PreClimb, module.WayPoints[0].Type);
        Assert.Equal(DynamicPortalWayPointType.Climb, module.WayPoints[5].Type);
    }

    [Fact]
    public void FullPosternGateShape_Parses()
    {
        // Mirrors the postern-gate authored shape from spec §2.2 (WayPoint/Link/BonePrefix
        // plus the upgrade-mux trigger fields, no AboveWall/TopAttack*/GenerateNow).
        var module = ParsePortal(
            "    BonePrefix = Post\n" +
            "    NumberOfBones = 2\n" +
            "    WayPoint = Index:0 Type:Walk\n" +
            "    WayPoint = Index:1 Type:Walk\n" +
            "    Link = From:0 To:1\n" +
            "    TriggeredBy = Upgrade_PosternGate\n" +
            "    ActivationDelaySeconds = 7.0\n" +
            "    ObjectFilter = NONE +INFANTRY\n",
            UpgradeMuxPreamble);

        Assert.Equal("Post", module.BonePrefix);
        Assert.Equal(2, module.NumberOfBones);
        Assert.Equal(2, module.WayPoints.Count);
        Assert.Single(module.Links);
        Assert.Equal(7.0f, module.ActivationDelaySeconds);
        Assert.Equal(-1, module.AboveWall);
        Assert.Equal(5.0f, module.TopAttackRadius);
        Assert.Equal(string.Empty, module.WallBoundsMesh);
    }
}

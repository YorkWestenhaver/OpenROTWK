// Mocked-game contract tests for the DynamicPortalBehaviour runtime port (R15 L5-P8, off
// bfme2-workbench/research/spec-dynamic-portal.md). The parse-level regressions live in
// Data/Ini/DynamicPortalBehaviorIniTests.cs; everything here is runtime behavior: the
// generation gate, the one-shot Phase A latch vs the every-call Phase B refresh, the link
// chain's stop-one-short bound, teardown/deactivate symmetry, the AboveWall dock query and
// the wall-top attack anchor.
//
// Note on the waypoint helper objects: their template is the engine-internal
// "#dynamicportal_wp" (spec §7), and the INI tokenizer treats a leading '#' as a macro-function
// sigil, so no .ini file - test text or shipped AotR data - can declare that template. The
// helper objects therefore never materialise in the fork today; the module still computes and
// latches the whole graph, which is exactly the degradation these tests pin. The topology
// itself is asserted through the pure GetChainHops function, which needs no helper objects.

using System;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class DynamicPortalBehaviorContractTests
{
    private const string UpgradeDefinitions = @"
Upgrade Upgrade_PosternGate
  Type = PLAYER
End
";

    // The siege-climber authored shape from spec §2.2/§2.1: six waypoints over four bones,
    // an up-route and a down-route, AboveWall naming waypoint 3 as the dock point.
    private const string LadderPortal = @"
Object SiegeLadder
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DynamicPortalBehaviour ModuleTag_Portal
    BonePrefix = Ladder
    NumberOfBones = 4
    WayPoint = Index:0 Type:PreClimb
    WayPoint = Index:1 Type:PreClimb
    WayPoint = Index:2 Type:Climb
    WayPoint = Index:3 Type:Climb
    WayPoint = Index:2 Type:Climb
    WayPoint = Index:1 Type:Climb
    Link = From:0 Via:4 Via:5 To:3
    Link = From:3 Via:1 Via:2 To:0
    AboveWall = 3
    TopAttackPos = X:10.0 Y:0.0 Z:20.0
    TopAttackRadius = 30
    GenerateNow = Yes
  End
End
";

    // The postern-gate authored shape: upgrade-driven, no AboveWall, no GenerateNow.
    private const string PosternGatePortal = UpgradeDefinitions + @"
Object PosternGate
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DynamicPortalBehaviour ModuleTag_Portal
    BonePrefix = Post
    NumberOfBones = 2
    WayPoint = Index:0 Type:Walk
    WayPoint = Index:1 Type:Walk
    Link = From:0 To:1
    TriggeredBy = Upgrade_PosternGate
    WallBoundsMesh = WallSection01
    ActivationDelaySeconds = 7.0
  End
End
";

    private static (HeadlessSimGame Game, GameObject Owner, DynamicPortalBehavior Module) Spawn(
        string definitions,
        string definitionName)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x57A);
        game.LoadIniText(definitions);
        var owner = game.SpawnObject(definitionName, game.CivilianPlayer, Vector3.Zero);
        return (game, owner, owner.BehaviorModules.OfType<DynamicPortalBehavior>().Single());
    }

    // ---- the port itself (this class was [ParseOnly] before R15 L5-P8) ----

    [Fact]
    public void ModuleIsPorted_AndInstantiatesOnSpawn()
    {
        var (_, _, module) = Spawn(PosternGatePortal, "PosternGate");

        Assert.NotNull(module);
        Assert.Equal(DynamicPortalBehavior.WaypointSlotCount, module.WaypointObjects.Count);
    }

    // ---- the generation gate (spec §5.7) ----

    [Fact]
    public void GenerateNow_MaterialisesAtTheCreationHook()
    {
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");

        Assert.True(module.IsGenerated);
        Assert.False(module.IsDisabled);
    }

    [Fact]
    public void WithoutGenerateNow_TheCreationHookDeclines()
    {
        var (_, _, module) = Spawn(PosternGatePortal, "PosternGate");

        Assert.False(module.IsGenerated);

        // ...and the gate keeps declining: it only builds for GenerateNow, or as a refresh of
        // an already-generated portal.
        module.TryGeneratePortal();
        Assert.False(module.IsGenerated);
    }

    [Fact]
    public void UpgradeCompletion_RestampsWallBoundsAndBuilds()
    {
        var (game, _, module) = Spawn(PosternGatePortal, "PosternGate");

        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_PosternGate") });

        Assert.True(module.IsGenerated);

        // WallBoundsMesh is sim-affecting, not cosmetic (spec §3.2 gap 1, §5.7 step 1): the
        // upgrade hook re-stamps the owning object's pathfind footprint before it builds.
        Assert.Equal(1, module.WallBoundsRestampCount);
    }

    [Fact]
    public void NoWallBoundsMesh_NoRestamp()
    {
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");

        Assert.Equal(0, module.WallBoundsRestampCount);
    }

    // ---- Phase A is one-shot, Phase B runs every call (spec §5.3) ----

    [Fact]
    public void Rebuild_ReRegistersRouteHeads_ButDoesNotRepeatPhaseA()
    {
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");

        // Built once already by the creation hook: two link rows, so two heads and two pairs.
        Assert.Equal(2, module.RegisteredRouteHeads.Count);
        Assert.Equal(2, module.RoutePairs.Count);

        module.BuildPortal();

        // The heads are handed over again (Phase B is unconditional); the pairs are registered
        // once at first generation and are not duplicated by the refresh.
        Assert.Equal(2, module.RegisteredRouteHeads.Count);
        Assert.Equal(2, module.RoutePairs.Count);
        Assert.True(module.IsGenerated);
    }

    // ---- the link chain stops one hop short (spec §5.3 Phase B, spec Q1) ----

    [Fact]
    public void ChainHops_StopOneShortOfTheRouteEnd()
    {
        // From:0 Via:4 Via:5 To:3 chains 0 -> 4 -> 5; waypoint 3 is never a chain target - the
        // terminal hop is carried by the (first, last) pair registration instead.
        var hops = DynamicPortalBehavior.GetChainHops(new[] { 0, 4, 5, 3 }).ToList();

        Assert.Equal(new[] { (0, 4), (4, 5) }, hops);
        Assert.DoesNotContain(hops, hop => hop.To == 3);
    }

    [Fact]
    public void ChainHops_TwoElementRoute_ChainsNothing()
    {
        // A bare From:0 To:1 row has n = 2, so the n - 2 bound yields no hops at all; the whole
        // route is represented by its endpoint pair.
        Assert.Empty(DynamicPortalBehavior.GetChainHops(new[] { 0, 1 }));
    }

    [Fact]
    public void ChainHops_EmptyRoute_YieldsNothing()
    {
        Assert.Empty(DynamicPortalBehavior.GetChainHops(new int[0]));
        Assert.Empty(DynamicPortalBehavior.GetChainHops(null));
    }

    // ---- teardown and deactivate (spec §5.7) ----

    [Fact]
    public void TearDown_ClearsTheGeneratedLatchAndEverySlot()
    {
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");
        Assert.True(module.IsGenerated);

        module.TearDown();

        Assert.False(module.IsGenerated);
        Assert.All(module.WaypointObjects, id => Assert.True(id.IsInvalid));
        Assert.Empty(module.RoutePairs);
        Assert.Empty(module.RegisteredRouteHeads);
    }

    [Fact]
    public void Deactivate_LatchesDisabled_AndTheGateRefusesToRebuild()
    {
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");

        module.Deactivate();

        Assert.True(module.IsDisabled);
        Assert.False(module.IsGenerated);

        // GenerateNow is authored, so the gate would otherwise rebuild on every call.
        module.TryGeneratePortal();
        Assert.False(module.IsGenerated);
    }

    // ---- the AboveWall dock query (spec §5.4) ----

    [Fact]
    public void DockPosition_SentinelAboveWall_FallsBackToTheOwnersOwnPosition()
    {
        // Every AotR postern gate omits AboveWall, so every one of them takes this branch.
        var (_, owner, module) = Spawn(PosternGatePortal, "PosternGate");

        Assert.True(module.TryGetDockPosition(out var position));
        Assert.Equal(owner.Translation, position);
    }

    [Fact]
    public void DockPosition_AboveWallInRange_TakesTheWaypointBranch()
    {
        // AboveWall = 3 is in range of the six-entry waypoint list, so the query resolves
        // through waypoints[3].Index into the bone array rather than falling back. Headless
        // hosts have no model, so the bone lookup degrades to the object's own origin and the
        // two branches produce the same coordinate - what is pinned here is that an in-range
        // AboveWall succeeds and does not throw.
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");

        Assert.True(module.TryGetDockPosition(out _));
    }

    // ---- the wall-top attack anchor (spec §5.8) ----

    [Fact]
    public void TopAttackRadius_ReadsThroughFromTheModuleData()
    {
        var (_, _, module) = Spawn(LadderPortal, "SiegeLadder");

        Assert.Equal(30.0f, module.TopAttackRadius);
    }

    [Fact]
    public void TopAttackPosition_ScalesXAndYByTheForwardLength_ButNotZ()
    {
        var (_, owner, module) = Spawn(LadderPortal, "SiegeLadder");

        // Rotate 45 degrees about Z. The forward vector is then normalised by its LARGER
        // horizontal component (0.7071...), not by its length, so it becomes (1, 1, 0) with
        // length sqrt(2) - the factor that scales TopAttackPos.X and .Y. Z passes through
        // unscaled. TopAttackPos = (10, 0, 20), so the local point is (10*sqrt2, 0, 20), and
        // rotating that back by 45 degrees lands it at (10, 10, 20) relative to the owner.
        owner.UpdateTransform(owner.Translation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4.0f));

        Assert.True(module.TryGetTopAttackPosition(out var position));

        var relative = position - owner.Translation;
        Assert.Equal(10.0f, relative.X, 3);
        Assert.Equal(10.0f, relative.Y, 3);
        Assert.Equal(20.0f, relative.Z, 3);
    }
}

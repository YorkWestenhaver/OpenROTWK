// Mocked-game contract tests for the ReplaceObjectUpgrade port (R12 task packet's
// testCases): the triggered in-place replacement, position/orientation preservation, team
// ownership, pathfind-map consistency around the swap, the onBuildComplete pass on the
// replacement's create modules, and the missing-template no-op guard.
//
// Trigger is the ordinary TriggeredBy upgrade mux (UpgradeLogic.TryUpgrade), same shape as
// every other landed UpgradeModule in this directory (AttributeModifierUpgrade,
// LevelUpUpgrade) - no special-power/command system needed, unlike this module's BFME2
// sibling ReplaceObjectUpdate.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class ReplaceObjectUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Transform
  Type = PLAYER
End

Upgrade Upgrade_OnReplacementBuilt
  Type = PLAYER
End

Object Widget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpgrade ModuleTag_Replace
    TriggeredBy = Upgrade_Transform
    ReplaceObject = ReplacedWidget
  End
End

Object BadTemplateWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpgrade ModuleTag_Replace
    TriggeredBy = Upgrade_Transform
    ReplaceObject = ThisTemplateDoesNotExist
  End
End

Object ReplacedWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantUpgradeCreate ModuleTag_Grant
    UpgradeToGrant = Upgrade_OnReplacementBuilt
    GiveOnBuildComplete = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB4E2)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeSet TransformSet(HeadlessSimGame game) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_Transform") };

    private static ReplaceObjectUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ReplaceObjectUpgrade>().Single();

    private static uint NextTestTeamId = 800;

    private static Team AssignSingletonTeam(HeadlessSimGame game, GameObject obj, Player owner)
    {
        var id = NextTestTeamId++;
        var template = new TeamTemplate(game.TeamFactory, id, $"ReplaceUpgradeTestTeam{id}", owner, isSingleton: true);
        var team = new Team(template, id);
        obj.Team = team;
        return team;
    }

    [Fact]
    public void Trigger_ReplacesObjectWithConfiguredTemplate()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, new Vector3(10, 20, 0));

        ModuleOf(widget).TryUpgrade(TransformSet(game));

        Assert.True(widget.IsDestroyed);
        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "ReplacedWidget");
    }

    [Fact]
    public void PositionAndOrientation_ArePreservedOnReplacement()
    {
        var game = NewGame();
        var origin = new Vector3(100, 200, 0);
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, origin);
        var originalRotation = widget.Rotation;

        ModuleOf(widget).TryUpgrade(TransformSet(game));

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");
        Assert.Equal(origin.X, replacement.Translation.X, 3);
        Assert.Equal(origin.Y, replacement.Translation.Y, 3);
        Assert.Equal(originalRotation, replacement.Rotation);
    }

    [Fact]
    public void TeamOwnership_IsPreservedOnReplacement()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var widget = game.SpawnObject("Widget", owner, Vector3.Zero);
        var team = AssignSingletonTeam(game, widget, owner);

        ModuleOf(widget).TryUpgrade(TransformSet(game));

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");
        Assert.Equal(owner, replacement.Owner);
        Assert.Equal(team, replacement.Team);
    }

    [Fact]
    public void PathfindMap_NoDoubleOccupancy_ReplacementIsGridVisibleAfterOriginalRemoved()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, new Vector3(30, 40, 0));

        ModuleOf(widget).TryUpgrade(TransformSet(game));

        // The original was destroyed (which un-stamps its pathfind footprint, S5) before the
        // replacement was created, so only the replacement is queueable/grid-visible now - no
        // stale double occupancy from the destroyed original.
        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");
        Assert.False(game.GameLogic.SimPathfind.Grid.WorldToCell(
            new FixVector3(
                new Fix64((int)replacement.Translation.X),
                new Fix64((int)replacement.Translation.Y),
                Fix64.Zero),
            out _, out _));
        Assert.True(game.GameLogic.SimPathfind.QueueForPath(replacement.Id));
    }

    [Fact]
    public void BuildComplete_FiresOnReplacementCreateModules()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var widget = game.SpawnObject("Widget", owner, Vector3.Zero);

        ModuleOf(widget).TryUpgrade(TransformSet(game));

        // GrantUpgradeCreate with GiveOnBuildComplete = Yes only grants from OnBuildComplete -
        // observing the granted upgrade on the owner proves the replacement's create modules
        // received the "consider it Built" pass this upgrade drives post-creation.
        var upgradeTemplate = game.AssetStore.Upgrades.GetByName("Upgrade_OnReplacementBuilt");
        Assert.True(owner.HasUpgrade(upgradeTemplate));
    }

    [Fact]
    public void MissingTemplate_IsANoOp_DoesNotCrashOrDestroyOriginal()
    {
        var game = NewGame();
        var widget = game.SpawnObject("BadTemplateWidget", game.CivilianPlayer, Vector3.Zero);

        ModuleOf(widget).TryUpgrade(TransformSet(game));

        Assert.False(widget.IsDestroyed);
        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "ReplacedWidget");
    }

    [Fact]
    public void SecondTrigger_IsRejected_ByTheSharedUpgradeMux()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(widget);
        var upgrades = TransformSet(game);

        module.TryUpgrade(upgrades);
        Assert.True(widget.IsDestroyed);

        // Already triggered; a second call must not attempt to touch the now-destroyed
        // original again (UpgradeLogic.CanUpgrade returns false once _triggered is set).
        module.TryUpgrade(upgrades);

        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "ReplacedWidget");
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(widget);

        var shadowHost = game.SpawnObject("Widget", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}

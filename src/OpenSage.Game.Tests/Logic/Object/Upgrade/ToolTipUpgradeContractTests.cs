// Mocked-game contract tests for the ToolTipUpgrade port (R12): a client-UI module whose
// only sim-visible effect is CurrentDisplayName flipping to the parsed DisplayName once
// the shared upgrade mux fires, mirroring the StatusBitsUpgrade test shape.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class ToolTipUpgradeContractTests
{
    private const string Definitions = @"
Object GarrisonBuilding
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = TooltipUpgrade ModuleTag_TipGarrison
    TriggeredBy = Upgrade_Garrison
    DisplayName = OBJECT:GarrisonedTooltip
  End
  Behavior = TooltipUpgrade ModuleTag_TipWeapon
    TriggeredBy = Upgrade_WeaponPlatform
    DisplayName = OBJECT:WeaponPlatformTooltip
  End
End

Upgrade Upgrade_Garrison
  Type = PLAYER
End

Upgrade Upgrade_WeaponPlatform
  Type = PLAYER
End

Upgrade Upgrade_Conflicting
  Type = PLAYER
End

Object GatedTip
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = TooltipUpgrade ModuleTag_Tip
    TriggeredBy = Upgrade_Garrison
    ConflictsWith = Upgrade_Conflicting
    DisplayName = OBJECT:GarrisonedTooltip
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, params string[] upgradeNames)
    {
        var set = new UpgradeSet();
        foreach (var name in upgradeNames)
        {
            set.Add(game.AssetStore.Upgrades.GetByName(name));
        }
        return set;
    }

    [Fact]
    public void ParsesDisplayName_FromIniBehaviorBlock()
    {
        var game = NewGame();

        var data = (ToolTipUpgradeModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("GarrisonBuilding").Behaviors["ModuleTag_TipGarrison"].Data;

        Assert.Equal("OBJECT:GarrisonedTooltip", data.DisplayName);
    }

    [Fact]
    public void TriggerFiresOnUpgrade_WhenPrerequisiteSatisfied_AndUpdatesTooltip()
    {
        var game = NewGame();
        var building = game.SpawnObject("GarrisonBuilding", game.CivilianPlayer, Vector3.Zero);
        var module = building.BehaviorModules.OfType<ToolTipUpgrade>()
            .Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_Garrison")));

        // Not triggered yet: no tooltip override active.
        Assert.Null(module.CurrentDisplayName);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_Garrison"));

        // Triggered: the tooltip now reflects the upgraded DisplayName.
        Assert.Equal("OBJECT:GarrisonedTooltip", module.CurrentDisplayName);
    }

    [Fact]
    public void MultipleInstancesOnOneObject_TrackTheirOwnDisplayNameIndependently()
    {
        var game = NewGame();
        var building = game.SpawnObject("GarrisonBuilding", game.CivilianPlayer, Vector3.Zero);
        var modules = building.BehaviorModules.OfType<ToolTipUpgrade>().ToList();
        Assert.Equal(2, modules.Count);

        var garrisonModule = modules.Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_Garrison")));
        var weaponModule = modules.Single(m => m.CanUpgrade(UpgradeSetOf(game, "Upgrade_WeaponPlatform")));

        garrisonModule.TryUpgrade(UpgradeSetOf(game, "Upgrade_Garrison"));

        // Only the module gated on Upgrade_Garrison fired; the other keeps its own state.
        Assert.Equal("OBJECT:GarrisonedTooltip", garrisonModule.CurrentDisplayName);
        Assert.Null(weaponModule.CurrentDisplayName);

        weaponModule.TryUpgrade(UpgradeSetOf(game, "Upgrade_WeaponPlatform"));
        Assert.Equal("OBJECT:WeaponPlatformTooltip", weaponModule.CurrentDisplayName);
    }

    [Fact]
    public void ConflictingUpgrade_PreventsTrigger()
    {
        var game = NewGame();
        var building = game.SpawnObject("GatedTip", game.CivilianPlayer, Vector3.Zero);
        var module = building.BehaviorModules.OfType<ToolTipUpgrade>().Single();

        // Both the trigger and the conflicting upgrade are present: CanUpgrade must refuse.
        var conflicted = UpgradeSetOf(game, "Upgrade_Garrison", "Upgrade_Conflicting");
        Assert.False(module.CanUpgrade(conflicted));

        module.TryUpgrade(conflicted);
        Assert.Null(module.CurrentDisplayName);
    }

    [Fact]
    public void SecondUpgradeAttempt_IsIdempotent()
    {
        var game = NewGame();
        var building = game.SpawnObject("GatedTip", game.CivilianPlayer, Vector3.Zero);
        var module = building.BehaviorModules.OfType<ToolTipUpgrade>().Single();

        var upgrades = UpgradeSetOf(game, "Upgrade_Garrison");
        module.TryUpgrade(upgrades);
        module.TryUpgrade(upgrades);

        Assert.Equal("OBJECT:GarrisonedTooltip", module.CurrentDisplayName);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("GatedTip", game.CivilianPlayer, Vector3.Zero);
        var live = liveHost.BehaviorModules.OfType<ToolTipUpgrade>().Single();
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_Garrison"));

        var shadowHost = game.SpawnObject("GatedTip", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<ToolTipUpgrade>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesDisplayNameAndUpgradeState()
    {
        var game = NewGame();
        var building = game.SpawnObject("GatedTip", game.CivilianPlayer, Vector3.Zero);
        var module = building.BehaviorModules.OfType<ToolTipUpgrade>().Single();
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_Garrison"));

        var saved = PortedModuleTestKit.Save(module);

        var freshHost = game.SpawnObject("GatedTip", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = freshHost.BehaviorModules.OfType<ToolTipUpgrade>().Single();
        Assert.Null(fresh.CurrentDisplayName);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);

        Assert.Equal("OBJECT:GarrisonedTooltip", fresh.CurrentDisplayName);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}

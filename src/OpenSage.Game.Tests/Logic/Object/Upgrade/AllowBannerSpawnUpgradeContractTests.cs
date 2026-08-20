// Mocked-game unit tests for the AllowBannerSpawnUpgrade port (R12): one test per
// INI-configurable branch, [create -> trigger -> observable Triggered flag], plus the
// shadow-copy base test and the mid-state save/load round-trip. This module is a pure
// marker (see file header on the module) so the only observable is the shared upgrade-mux
// Triggered flag itself. Object definitions are parsed from INI text through the real
// parser.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class AllowBannerSpawnUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_BannerCarrier
  Type = PLAYER
End

Upgrade Upgrade_BannerConflict
  Type = PLAYER
End

Object PlainBannerHorde
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AllowBannerSpawnUpgrade ModuleTag_Banner
    TriggeredBy = Upgrade_BannerCarrier
  End
End

Object ConflictedBannerHorde
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AllowBannerSpawnUpgrade ModuleTag_Banner
    TriggeredBy = Upgrade_BannerCarrier
    ConflictsWith = Upgrade_BannerConflict
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB01)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static AllowBannerSpawnUpgrade BannerModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AllowBannerSpawnUpgrade>().Single();

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
    public void NotTriggered_WhenPrerequisiteNotGranted()
    {
        var game = NewGame();
        var horde = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, Vector3.Zero);
        var module = BannerModuleOf(horde);

        Assert.False(module.Triggered);
    }

    [Fact]
    public void Triggered_WhenPrerequisiteGranted()
    {
        var game = NewGame();
        var horde = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, Vector3.Zero);
        var module = BannerModuleOf(horde);

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_BannerCarrier"));

        Assert.True(module.Triggered);
    }

    [Fact]
    public void MultipleInstances_MaintainIndependentTriggeredStates()
    {
        var game = NewGame();
        var triggeredHorde = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, Vector3.Zero);
        var untriggeredHorde = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, new Vector3(10, 0, 0));

        var triggeredModule = BannerModuleOf(triggeredHorde);
        var untriggeredModule = BannerModuleOf(untriggeredHorde);

        triggeredModule.TryUpgrade(UpgradeSetOf(game, "Upgrade_BannerCarrier"));

        Assert.True(triggeredModule.Triggered);
        Assert.False(untriggeredModule.Triggered);
    }

    [Fact]
    public void ConflictingUpgrade_BlocksTriggering()
    {
        var game = NewGame();
        var horde = game.SpawnObject("ConflictedBannerHorde", game.CivilianPlayer, Vector3.Zero);
        var module = BannerModuleOf(horde);

        // Both the trigger and the conflicting upgrade are present at once: base
        // UpgradeLogic.CanUpgrade rejects when the conflict set overlaps, regardless of the
        // trigger set also overlapping.
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_BannerCarrier", "Upgrade_BannerConflict"));

        Assert.False(module.Triggered);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        var liveHost = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, Vector3.Zero);
        var live = BannerModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_BannerCarrier"));

        var shadowHost = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = BannerModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag()
    {
        var game = NewGame();
        var horde = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, Vector3.Zero);
        var module = BannerModuleOf(horde);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_BannerCarrier"));

        var saved = PortedModuleTestKit.Save(module);

        // A fresh instance starts untriggered; loading the saved state must flip it back to
        // triggered so its CRC matches the source.
        var freshHost = game.SpawnObject("PlainBannerHorde", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = BannerModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}

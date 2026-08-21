// Mocked-game unit tests for the CommandPointsUpgrade port (api-freeze-v1 §6 fitness item 4),
// mirroring CostModifierUpgradeContractTests' shape: one test per INI branch
// [create -> trigger -> observable effect on the player's CommandPointsBank], plus the
// shadow-copy base test and a mid-behavior save/load round-trip.
//
// Per modules-r13/specs/CommandPointsUpgradeModuleData.md §2a/F-CPU-1: Player.CommandPoints is
// NOT in Player.Persist/CRC, so no test here asserts anything about that channel - only this
// module's own Xfer walk (the mux-triggered flag) and the resulting CommandPointsBank.Limit.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class CommandPointsUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_MoreCommandPoints
  Type = PLAYER
End

; StartsActive, no RequiredObject: applies immediately on spawn.
Object CommandPointsBannerAlways
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CommandPointsUpgrade ModuleTag_Cp
    StartsActive = Yes
    CommandPoints = 50
  End
End

; TriggeredBy: applies only after the upgrade completes.
Object CommandPointsBannerUpgradeable
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CommandPointsUpgrade ModuleTag_Cp
    TriggeredBy = Upgrade_MoreCommandPoints
    CommandPoints = 50
  End
End

; RequiredObject matches this object's own KindOf (STRUCTURE).
Object CommandPointsBannerMatchingFilter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CommandPointsUpgrade ModuleTag_Cp
    StartsActive = Yes
    CommandPoints = 50
    RequiredObject = ALL
  End
End

; RequiredObject does NOT match this object's own KindOf (STRUCTURE, not INFANTRY).
Object CommandPointsBannerNonMatchingFilter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CommandPointsUpgrade ModuleTag_Cp
    StartsActive = Yes
    CommandPoints = 50
    RequiredObject = NONE +INFANTRY
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC90) // command points 90 :)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static CommandPointsUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CommandPointsUpgrade>().Single();

    [Fact]
    public void Parse_CommandPointsAndRequiredObject_RoundTrip()
    {
        var game = NewGame();
        var withFilter = game.SpawnObject("CommandPointsBannerMatchingFilter", game.CivilianPlayer, Vector3.Zero);
        var withoutFilter = game.SpawnObject("CommandPointsBannerAlways", game.CivilianPlayer, new Vector3(10, 0, 0));

        Assert.Equal(50, GetData(withFilter).CommandPoints);
        Assert.NotNull(GetData(withFilter).RequiredObject);

        Assert.Equal(50, GetData(withoutFilter).CommandPoints);
        Assert.Null(GetData(withoutFilter).RequiredObject);
    }

    private static CommandPointsUpgradeModuleData GetData(GameObject obj) =>
        obj.Definition.Behaviors.Values
            .Select(container => container.Data)
            .OfType<CommandPointsUpgradeModuleData>()
            .Single();

    [Fact]
    public void StartsActive_NoRequiredObject_RaisesLimit_OnCreate()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;

        Assert.Equal(0, player.CommandPoints.Limit);

        var banner = game.SpawnObject("CommandPointsBannerAlways", player, Vector3.Zero);

        Assert.Equal(50, player.CommandPoints.Limit);
        Assert.True(ModuleOf(banner).IsUpgraded);
    }

    [Fact]
    public void TriggeredBy_DoesNotRaiseLimitUntilUpgraded()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("CommandPointsBannerUpgradeable", player, Vector3.Zero);
        var module = ModuleOf(banner);

        Assert.False(module.IsUpgraded);
        Assert.Equal(0, player.CommandPoints.Limit);

        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_MoreCommandPoints") });

        Assert.True(module.IsUpgraded);
        Assert.Equal(50, player.CommandPoints.Limit);
    }

    [Fact]
    public void RequiredObject_Matching_RaisesLimit()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        game.SpawnObject("CommandPointsBannerMatchingFilter", player, Vector3.Zero);

        Assert.Equal(50, player.CommandPoints.Limit);
    }

    [Fact]
    public void RequiredObject_NonMatching_GatesTheBonus_ButStillMarksUpgraded()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("CommandPointsBannerNonMatchingFilter", player, Vector3.Zero);

        // RequiredObject gates ApplyToPlayer only, not CanUpgrade/TryUpgrade (spec §1).
        Assert.Equal(0, player.CommandPoints.Limit);
        Assert.True(ModuleOf(banner).IsUpgraded);
    }

    [Fact]
    public void DoubleApply_RaisesLimitOnlyOnce()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("CommandPointsBannerUpgradeable", player, Vector3.Zero);
        var module = ModuleOf(banner);
        var upgrade = new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_MoreCommandPoints") };

        module.TryUpgrade(upgrade);
        Assert.Equal(50, player.CommandPoints.Limit);

        // Already triggered: a second TryUpgrade / a redundant ReapplyAfterLoad must not
        // double-apply the delta (the _appliedToPlayer guard in ApplyToPlayer).
        module.TryUpgrade(upgrade);
        module.ReapplyAfterLoad();

        Assert.Equal(50, player.CommandPoints.Limit);
    }

    [Fact]
    public void RemoveFromPlayer_UndoesTheAddition()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("CommandPointsBannerAlways", player, Vector3.Zero);
        var module = ModuleOf(banner);
        Assert.Equal(50, player.CommandPoints.Limit);

        module.RemoveFromPlayer();
        Assert.Equal(0, player.CommandPoints.Limit);

        // Already removed: calling again is a no-op, no negative limit.
        module.RemoveFromPlayer();
        Assert.Equal(0, player.CommandPoints.Limit);
    }

    [Fact]
    public void OnCapture_MovesTheAdditionBetweenPlayers()
    {
        var game = NewGame();
        var oldOwner = game.CivilianPlayer;
        var newOwner = game.PlayerManager.Players[0];
        var banner = game.SpawnObject("CommandPointsBannerAlways", oldOwner, Vector3.Zero);
        var module = ModuleOf(banner);

        Assert.Equal(50, oldOwner.CommandPoints.Limit);
        Assert.Equal(0, newOwner.CommandPoints.Limit);

        module.OnCapture(oldOwner, newOwner);

        Assert.Equal(0, oldOwner.CommandPoints.Limit);
        Assert.Equal(50, newOwner.CommandPoints.Limit);
    }

    [Fact]
    public void OnCapture_NeverTriggered_IsNoOpOnBothPlayers()
    {
        var game = NewGame();
        var oldOwner = game.CivilianPlayer;
        var newOwner = game.PlayerManager.Players[0];
        var banner = game.SpawnObject("CommandPointsBannerUpgradeable", oldOwner, Vector3.Zero);
        var module = ModuleOf(banner);

        Assert.False(module.IsUpgraded);

        module.OnCapture(oldOwner, newOwner);

        Assert.Equal(0, oldOwner.CommandPoints.Limit);
        Assert.Equal(0, newOwner.CommandPoints.Limit);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var live = ModuleOf(game.SpawnObject("CommandPointsBannerUpgradeable", game.CivilianPlayer, Vector3.Zero));
        live.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_MoreCommandPoints") });

        var shadow = ModuleOf(game.SpawnObject("CommandPointsBannerUpgradeable", game.CivilianPlayer, new Vector3(100, 0, 0)));
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_RebuildsLimit_ViaReapplyAfterLoad()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("CommandPointsBannerUpgradeable", player, Vector3.Zero);
        var module = ModuleOf(banner);
        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_MoreCommandPoints") });
        Assert.Equal(50, player.CommandPoints.Limit);

        var saved = PortedModuleTestKit.Save(module);

        // Tear the derived _appliedToPlayer/Limit state down (as a fresh load would start it),
        // then load the mux flag back and let the module reconstruct the limit.
        module.RemoveFromPlayer();
        Assert.Equal(0, player.CommandPoints.Limit);

        PortedModuleTestKit.Load(module, saved);
        module.ReapplyAfterLoad();

        Assert.True(module.IsUpgraded);
        Assert.Equal(50, player.CommandPoints.Limit);   // rebuilt exactly
    }
}

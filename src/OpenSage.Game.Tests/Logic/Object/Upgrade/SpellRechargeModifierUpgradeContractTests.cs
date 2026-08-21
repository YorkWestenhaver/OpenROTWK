// Mocked-game unit tests for the SpellRechargeModifierUpgrade port (api-freeze-v1 §6 fitness
// item 4): one test per INI branch [create -> trigger -> observable effect on the player's
// special-power recharge-discount registry], plus the shadow-copy base test and a
// mid-behavior save/load round-trip. Mirrors CostModifierUpgradeContractTests.cs case-for-case
// with the KindOf dimension removed and the query surface swapped to the new registry. Object
// definitions are parsed from INI text through the real parser, so the S5 quantizing parse
// function (ParseFix64Percentage) is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class SpellRechargeModifierUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_FasterRecharge
  Type = PLAYER
End

; StartsActive: registers the modifier at construction.
Object RechargeBanner
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpellRechargeModifierUpgrade ModuleTag_Recharge
    StartsActive = Yes
    Percentage = -20%
  End
End

; TriggeredBy: registers only after the upgrade completes.
Object UpgradeableRechargeBanner
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpellRechargeModifierUpgrade ModuleTag_Recharge
    TriggeredBy = Upgrade_FasterRecharge
    Percentage = -20%
  End
End
";

    // -20% -> multiplier 0.80, exactly representable in Q31.32.
    private static readonly Fix64 DiscountedRecharge = Fix64.FromDecimalLiteral("0.80");

    private static HeadlessSimGame NewGame(uint seed = 0xC05)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpellRechargeModifierUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SpellRechargeModifierUpgrade>().Single();

    private static Fix64 RechargeFactor(Player player) =>
        player.SpecialPowerRechargeDiscount.GetSpecialPowerRechargeDiscountFactor();

    [Fact]
    public void StartsActive_RegistersDiscount_OnCreate()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;

        // Before any object exists there is no modifier.
        Assert.Equal(Fix64.One, RechargeFactor(player));

        game.SpawnObject("RechargeBanner", player, Vector3.Zero);

        Assert.Equal(DiscountedRecharge, RechargeFactor(player));
    }

    [Fact]
    public void TriggeredBy_DoesNotRegisterUntilUpgraded()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("UpgradeableRechargeBanner", player, Vector3.Zero);
        var module = ModuleOf(banner);

        Assert.False(module.IsUpgraded);
        Assert.Equal(Fix64.One, RechargeFactor(player));

        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_FasterRecharge") });

        Assert.True(module.IsUpgraded);
        Assert.Equal(DiscountedRecharge, RechargeFactor(player));
    }

    [Fact]
    public void TwoIdenticalDiscounts_RefCount_SurviveOneRemoval()
    {
        // Add() ref-counts identical-percent entries; removing one leaves the other's effect
        // intact.
        var game = NewGame();
        var player = game.CivilianPlayer;
        var a = game.SpawnObject("RechargeBanner", player, Vector3.Zero);
        var b = game.SpawnObject("RechargeBanner", player, new Vector3(50, 0, 0));

        // One collapsed ref-counted entry, single 0.80 factor (not 0.80 * 0.80).
        Assert.Equal(DiscountedRecharge, RechargeFactor(player));

        ModuleOf(a).RemoveFromPlayer();
        Assert.Equal(DiscountedRecharge, RechargeFactor(player));   // b still applies

        ModuleOf(b).RemoveFromPlayer();
        Assert.Equal(Fix64.One, RechargeFactor(player));            // both gone
    }

    [Fact]
    public void OnDelete_RemovesTheRegistration()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("RechargeBanner", player, Vector3.Zero);
        Assert.Equal(DiscountedRecharge, RechargeFactor(player));

        ModuleOf(banner).RemoveFromPlayer();
        Assert.Equal(Fix64.One, RechargeFactor(player));
    }

    [Fact]
    public void OnCapture_MovesRegistrationBetweenPlayers()
    {
        var game = NewGame();
        var oldOwner = game.CivilianPlayer;
        var newOwner = game.PlayerManager.Players[0];
        var banner = game.SpawnObject("RechargeBanner", oldOwner, Vector3.Zero);

        Assert.Equal(DiscountedRecharge, RechargeFactor(oldOwner));
        Assert.Equal(Fix64.One, RechargeFactor(newOwner));

        ModuleOf(banner).OnCapture(oldOwner, newOwner);

        Assert.Equal(Fix64.One, RechargeFactor(oldOwner));          // removed from old
        Assert.Equal(DiscountedRecharge, RechargeFactor(newOwner)); // added to new
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var live = ModuleOf(game.SpawnObject("UpgradeableRechargeBanner", game.CivilianPlayer, Vector3.Zero));
        live.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_FasterRecharge") });

        // Shadow is the same class in a different (un-triggered) state; Load must overwrite
        // the mux flag the walk carries.
        var shadow = ModuleOf(game.SpawnObject("UpgradeableRechargeBanner", game.CivilianPlayer, new Vector3(100, 0, 0)));
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_RebuildsRegistry_ViaReapplyAfterLoad()
    {
        // The player registry is transient derived state: on load it is rebuilt by the
        // triggered module re-applying itself. Simulate a world reset (registry torn down)
        // then load+reapply, and require the registry to come back identical.
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("UpgradeableRechargeBanner", player, Vector3.Zero);
        var module = ModuleOf(banner);
        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_FasterRecharge") });
        Assert.Equal(DiscountedRecharge, RechargeFactor(player));

        var saved = PortedModuleTestKit.Save(module);

        // Tear the world's derived registry state down (as a fresh load would start it), then
        // load the mux flag back and let the module reconstruct the registry.
        module.RemoveFromPlayer();
        Assert.Equal(Fix64.One, RechargeFactor(player));

        PortedModuleTestKit.Load(module, saved);
        module.ReapplyAfterLoad();

        Assert.True(module.IsUpgraded);
        Assert.Equal(DiscountedRecharge, RechargeFactor(player));   // rebuilt exactly
    }
}

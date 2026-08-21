// Mocked-game unit tests for the CostModifierUpgrade port (api-freeze-v1 §6 fitness item 4):
// one test per INI branch [create -> trigger -> observable effect on the player's production-
// cost registry], plus the shadow-copy base test and a mid-behavior save/load round-trip.
// Object definitions are parsed from INI text through the real parser, so the S5 quantizing
// parse functions (ParseFix64Percentage, ParseEnumBitArray) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class CostModifierUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_CheaperInfantry
  Type = PLAYER
End

; StartsActive: registers the modifier at construction.
Object InfantryDiscountBanner
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CostModifierUpgrade ModuleTag_Cost
    StartsActive = Yes
    EffectKindOf = INFANTRY
    Percentage = -25%
  End
End

; TriggeredBy: registers only after the upgrade completes.
Object UpgradeableDiscountBanner
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CostModifierUpgrade ModuleTag_Cost
    TriggeredBy = Upgrade_CheaperInfantry
    EffectKindOf = INFANTRY
    Percentage = -25%
  End
End

; ObjectFilter/no-EffectKindOf shape (the AotR case): audited but not acted on.
Object FilterOnlyBanner
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = CostModifierUpgrade ModuleTag_Cost
    StartsActive = Yes
    ObjectFilter = NONE +GondorPippin
    UpgradeDiscount = Yes
    ApplyToTheseUpgrades = Upgrade_CheaperInfantry
    Percentage = -30%
  End
End
";

    private static readonly BitArray<ObjectKinds> InfantryKind = new(ObjectKinds.Infantry);
    private static readonly BitArray<ObjectKinds> VehicleKind = new(ObjectKinds.Vehicle);

    // -25% -> multiplier 0.75, exactly representable in Q31.32.
    private static readonly Fix64 DiscountedInfantry = Fix64.FromDecimalLiteral("0.75");

    private static HeadlessSimGame NewGame(uint seed = 0xC05)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static CostModifierUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CostModifierUpgrade>().Single();

    private static Fix64 InfantryCostFactor(Player player) =>
        player.ProductionCostModifiers.GetProductionCostChangeBasedOnKindOf(InfantryKind);

    [Fact]
    public void StartsActive_RegistersDiscountForMatchingKindOf_OnCreate()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;

        // Before any object exists there is no modifier.
        Assert.Equal(Fix64.One, InfantryCostFactor(player));

        game.SpawnObject("InfantryDiscountBanner", player, Vector3.Zero);

        // Matching KindOf now costs 75%; a non-matching kind is untouched.
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));
        Assert.Equal(Fix64.One, player.ProductionCostModifiers.GetProductionCostChangeBasedOnKindOf(VehicleKind));
    }

    [Fact]
    public void TriggeredBy_DoesNotRegisterUntilUpgraded()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("UpgradeableDiscountBanner", player, Vector3.Zero);
        var module = ModuleOf(banner);

        Assert.False(module.IsUpgraded);
        Assert.Equal(Fix64.One, InfantryCostFactor(player));

        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_CheaperInfantry") });

        Assert.True(module.IsUpgraded);
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));
    }

    [Fact]
    public void EmptyEffectKindOf_ObjectFilterShape_DoesNotRegister()
    {
        // GPL empty-mask-matches-all would apply a global discount here; we gate registration
        // on a non-empty EffectKindOf (the ObjectFilter path has no GPL/spec reference).
        var game = NewGame();
        var player = game.CivilianPlayer;
        game.SpawnObject("FilterOnlyBanner", player, Vector3.Zero);

        Assert.Equal(Fix64.One, InfantryCostFactor(player));
        Assert.Equal(Fix64.One, player.ProductionCostModifiers.GetProductionCostChangeBasedOnKindOf(VehicleKind));
    }

    [Fact]
    public void TwoIdenticalDiscounts_RefCount_SurviveOneRemoval()
    {
        // GPL add() ref-counts identical (kindOf, percent) entries; removing one leaves the
        // other's effect intact.
        var game = NewGame();
        var player = game.CivilianPlayer;
        var a = game.SpawnObject("InfantryDiscountBanner", player, Vector3.Zero);
        var b = game.SpawnObject("InfantryDiscountBanner", player, new Vector3(50, 0, 0));

        // One collapsed ref-counted entry, single 0.75 factor (not 0.75 * 0.75).
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));

        ModuleOf(a).RemoveFromPlayer();
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));   // b still applies

        ModuleOf(b).RemoveFromPlayer();
        Assert.Equal(Fix64.One, InfantryCostFactor(player));            // both gone
    }

    [Fact]
    public void OnDelete_RemovesTheRegistration()
    {
        var game = NewGame();
        var player = game.CivilianPlayer;
        var banner = game.SpawnObject("InfantryDiscountBanner", player, Vector3.Zero);
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));

        ModuleOf(banner).RemoveFromPlayer();
        Assert.Equal(Fix64.One, InfantryCostFactor(player));
    }

    [Fact]
    public void OnCapture_MovesRegistrationBetweenPlayers()
    {
        var game = NewGame();
        var oldOwner = game.CivilianPlayer;
        var newOwner = game.PlayerManager.Players[0];
        var banner = game.SpawnObject("InfantryDiscountBanner", oldOwner, Vector3.Zero);

        Assert.Equal(DiscountedInfantry, InfantryCostFactor(oldOwner));
        Assert.Equal(Fix64.One, InfantryCostFactor(newOwner));

        ModuleOf(banner).OnCapture(oldOwner, newOwner);

        Assert.Equal(Fix64.One, InfantryCostFactor(oldOwner));          // removed from old
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(newOwner)); // added to new
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var live = ModuleOf(game.SpawnObject("UpgradeableDiscountBanner", game.CivilianPlayer, Vector3.Zero));
        live.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_CheaperInfantry") });

        // Shadow is the same class in a different (un-triggered) state; Load must overwrite
        // the mux flag the walk carries.
        var shadow = ModuleOf(game.SpawnObject("UpgradeableDiscountBanner", game.CivilianPlayer, new Vector3(100, 0, 0)));
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
        var banner = game.SpawnObject("UpgradeableDiscountBanner", player, Vector3.Zero);
        var module = ModuleOf(banner);
        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_CheaperInfantry") });
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));

        var saved = PortedModuleTestKit.Save(module);

        // Tear the world's derived registry state down (as a fresh load would start it), then
        // load the mux flag back and let the module reconstruct the registry.
        module.RemoveFromPlayer();
        Assert.Equal(Fix64.One, InfantryCostFactor(player));

        PortedModuleTestKit.Load(module, saved);
        module.ReapplyAfterLoad();

        Assert.True(module.IsUpgraded);
        Assert.Equal(DiscountedInfantry, InfantryCostFactor(player));   // rebuilt exactly
    }
}

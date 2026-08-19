// Mocked-game unit tests for the StatusBitsUpgrade port (experiment-round-4 §4.1 DoD
// item 4): one test per INI-configurable branch, [create -> trigger -> observable
// effect], plus the shadow-copy base test and the mid-state save/load round-trip.
// Object definitions are parsed from INI text through the real parser, so the parse path
// (StatusToSet = ParseEnumBitArray<ObjectStatus>) is exercised.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class StatusBitsUpgradeContractTests
{
    private const string Definitions = @"
Object ActiveStatusBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = StatusBitsUpgrade ModuleTag_Status
    StartsActive = Yes
    StatusToSet = CAN_ATTACK NO_COLLISIONS
  End
End

Upgrade Upgrade_GoIntangible
  Type = PLAYER
End

Object GatedStatusBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = StatusBitsUpgrade ModuleTag_Status
    TriggeredBy = Upgrade_GoIntangible
    StatusToSet = UNSELECTABLE
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static StatusBitsUpgrade StatusModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<StatusBitsUpgrade>().Single();

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, string upgradeName) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName(upgradeName) };

    [Fact]
    public void StartsActive_SetsEveryNamedBit_OnSpawn()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ActiveStatusBearer", game.CivilianPlayer, Vector3.Zero);

        // StartsActive fires the mux from the module ctor: both named bits are set, and no
        // unnamed bit is touched.
        Assert.True(bearer.TestStatus(ObjectStatus.CanAttack));
        Assert.True(bearer.TestStatus(ObjectStatus.NoCollisions));
        Assert.False(bearer.TestStatus(ObjectStatus.Unselectable));
    }

    [Fact]
    public void UpgradeGated_DoesNotSetUntilTriggered_ThenSets()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedStatusBearer", game.CivilianPlayer, Vector3.Zero);
        var module = StatusModuleOf(bearer);

        // Not triggered yet: the bit is clear.
        Assert.False(bearer.TestStatus(ObjectStatus.Unselectable));

        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_GoIntangible"));

        // Triggered: the named bit is now set.
        Assert.True(bearer.TestStatus(ObjectStatus.Unselectable));
    }

    [Fact]
    public void SecondUpgradeAttempt_IsIdempotent()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedStatusBearer", game.CivilianPlayer, Vector3.Zero);
        var module = StatusModuleOf(bearer);

        var upgrades = UpgradeSetOf(game, "Upgrade_GoIntangible");
        module.TryUpgrade(upgrades);
        // A second identical attempt is a no-op (mux already triggered) and never clears
        // the bit.
        module.TryUpgrade(upgrades);

        Assert.True(bearer.TestStatus(ObjectStatus.Unselectable));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        // Live: triggered (bit applied). Shadow: a fresh, untriggered instance of the same
        // class over the same data, in a different state - Load must overwrite the mux flag.
        var liveHost = game.SpawnObject("GatedStatusBearer", game.CivilianPlayer, Vector3.Zero);
        var live = StatusModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_GoIntangible"));

        var shadowHost = game.SpawnObject("GatedStatusBearer", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = StatusModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesTriggeredFlag()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GatedStatusBearer", game.CivilianPlayer, Vector3.Zero);
        var module = StatusModuleOf(bearer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_GoIntangible"));

        var saved = PortedModuleTestKit.Save(module);

        // A fresh instance starts untriggered; loading the saved state must flip it back to
        // triggered so its CRC matches the source.
        var freshHost = game.SpawnObject("GatedStatusBearer", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = StatusModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}

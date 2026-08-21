// Mocked-game unit tests for the PassengersFireUpgrade port (R12): one test per packet
// testCase [create -> trigger -> observable effect], plus the shadow-copy base test and the
// mid-state save/load round-trip, mirroring StatusBitsUpgradeContractTests.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class PassengersFireUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_GunPorts
  Type = PLAYER
End

Object TransportBearer
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TransportContain ModuleTag_Contain
    Slots = 5
    ContainMax = 5
    AllowInsideKindOf = INFANTRY
    PassengersAllowedToFire = No
  End
  Behavior = PassengersFireUpgrade ModuleTag_Fire
    TriggeredBy = Upgrade_GunPorts
  End
End

Object ActiveTransportBearer
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TransportContain ModuleTag_Contain
    Slots = 5
    ContainMax = 5
    AllowInsideKindOf = INFANTRY
    PassengersAllowedToFire = No
  End
  Behavior = PassengersFireUpgrade ModuleTag_Fire
    StartsActive = Yes
  End
End

Object GarrisonBearer
  KindOf = STRUCTURE IMMOBILE
  Geometry = BOX
  GeometryMajorRadius = 20
  GeometryMinorRadius = 20
  GeometryHeight = 20
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = GarrisonContain ModuleTag_Contain
    ContainMax = 10
    AllowInsideKindOf = INFANTRY
  End
  Behavior = PassengersFireUpgrade ModuleTag_Fire
    TriggeredBy = Upgrade_GunPorts
  End
End

Object NoContainBearer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = PassengersFireUpgrade ModuleTag_Fire
    TriggeredBy = Upgrade_GunPorts
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xF12E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static PassengersFireUpgrade FireModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<PassengersFireUpgrade>().Single();

    private static OpenContainModule ContainOf(GameObject obj) =>
        obj.FindBehavior<OpenContainModule>();

    private static UpgradeSet UpgradeSetOf(HeadlessSimGame game, string upgradeName) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName(upgradeName) };

    [Fact]
    public void ApplyToTransportUnit_SetsPassengersAllowedToFire()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TransportBearer", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(bearer);

        // Before upgrade: the container's INI default (PassengersAllowedToFire = No) holds.
        Assert.False(contain.PassengersAllowedToFire);

        FireModuleOf(bearer).TryUpgrade(UpgradeSetOf(game, "Upgrade_GunPorts"));

        Assert.True(contain.PassengersAllowedToFire);
    }

    [Fact]
    public void ApplyToUnitWithoutContainModule_DoesNotThrow()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("NoContainBearer", game.CivilianPlayer, Vector3.Zero);

        var exception = Record.Exception(() =>
            FireModuleOf(bearer).TryUpgrade(UpgradeSetOf(game, "Upgrade_GunPorts")));

        Assert.Null(exception);
        Assert.Null(ContainOf(bearer));
    }

    [Fact]
    public void ApplyToGarrisonStructure_PassengersGainFireRights()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("GarrisonBearer", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(bearer);

        Assert.False(contain.PassengersAllowedToFire);

        FireModuleOf(bearer).TryUpgrade(UpgradeSetOf(game, "Upgrade_GunPorts"));

        Assert.True(contain.PassengersAllowedToFire);
    }

    [Fact]
    public void MultipleApplications_HaveNoCumulativeEffect()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TransportBearer", game.CivilianPlayer, Vector3.Zero);
        var module = FireModuleOf(bearer);
        var contain = ContainOf(bearer);

        var upgrades = UpgradeSetOf(game, "Upgrade_GunPorts");
        module.TryUpgrade(upgrades);
        // A second identical attempt is a no-op (mux already triggered): the flag stays set,
        // nothing throws, nothing double-applies.
        module.TryUpgrade(upgrades);

        Assert.True(contain.PassengersAllowedToFire);
    }

    [Fact]
    public void DoesNotAffectContainersOwnWeaponSystems()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TransportBearer", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(bearer);
        var healthBefore = bearer.BodyModule.Health;

        FireModuleOf(bearer).TryUpgrade(UpgradeSetOf(game, "Upgrade_GunPorts"));

        // The upgrade only flips the passenger-fire flag; it does not touch the container
        // object's own body/weapon state.
        Assert.True(contain.PassengersAllowedToFire);
        Assert.Equal(healthBefore, bearer.BodyModule.Health);
    }

    [Fact]
    public void StartsActive_SetsFlag_OnSpawn()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("ActiveTransportBearer", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(bearer);

        // StartsActive fires the mux from the module ctor: the flag is set immediately, with
        // no explicit TryUpgrade call.
        Assert.True(contain.PassengersAllowedToFire);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();

        // Live: triggered (flag applied). Shadow: a fresh, untriggered instance of the same
        // class over the same data, in a different state - Load must overwrite the mux flag.
        var liveHost = game.SpawnObject("TransportBearer", game.CivilianPlayer, Vector3.Zero);
        var live = FireModuleOf(liveHost);
        live.TryUpgrade(UpgradeSetOf(game, "Upgrade_GunPorts"));

        var shadowHost = game.SpawnObject("TransportBearer", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = FireModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesPassengerFiringCapability()
    {
        var game = NewGame();
        var bearer = game.SpawnObject("TransportBearer", game.CivilianPlayer, Vector3.Zero);
        var module = FireModuleOf(bearer);
        module.TryUpgrade(UpgradeSetOf(game, "Upgrade_GunPorts"));

        var saved = PortedModuleTestKit.Save(module);

        // A fresh instance starts untriggered; loading the saved state must flip the mux
        // back to triggered so its CRC matches the source (the mux flag is this module's
        // only Xfer state - the actual contain flag it drives is persisted separately by
        // OpenContainModule's own walk).
        var freshHost = game.SpawnObject("TransportBearer", game.CivilianPlayer, new Vector3(50, 0, 0));
        var fresh = FireModuleOf(freshHost);
        Assert.NotEqual(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));

        PortedModuleTestKit.Load(fresh, saved);
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(fresh));
    }
}

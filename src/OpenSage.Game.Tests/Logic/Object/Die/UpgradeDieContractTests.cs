// Mocked-game unit tests for the UpgradeDie port (api-freeze-v1 §6 fitness item 4,
// experiment-round-4 §4.1 DoD item 4): one test per INI branch, each shaped
// [create -> trigger death -> observable effect] through the batch's death-trigger helper,
// plus the shadow-copy base test and a mid-behavior save/load continuation.
//
// The observable effect for this class is on ANOTHER object: the producer's upgrade set.
// Object definitions are parsed from INI text through the real parser, so the two
// UpgradeToRemove syntaxes AotR actually writes (with and without the BFME2 module tag) are
// on the tested path - a parse regression there fails these tests before it reaches gapmap.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class UpgradeDieContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Drone
  Type = OBJECT
End

Upgrade Upgrade_OtherThing
  Type = OBJECT
End

Object DroneProducer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
End

; The ZH one-token form: UpgradeToRemove names the upgrade and nothing else.
Object Drone
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = UpgradeDie ModuleTag_Die
    DeathTypes = ALL
    UpgradeToRemove = Upgrade_Drone
  End
End

; The BFME2 two-token form: a trailing module tag, parsed and stored, acted on by nothing.
Object TaggedDrone
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = UpgradeDie ModuleTag_Die
    DeathTypes = ALL
    UpgradeToRemove = Upgrade_Drone BaseUpgradeTag_01
  End
End

; The Die gate: only a BURNED death frees the upgrade.
Object BurnOnlyDrone
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = UpgradeDie ModuleTag_Die
    DeathTypes = NONE +BURNED
    UpgradeToRemove = Upgrade_Drone
  End
End

; The Die gate, status form: a SOLD object is exempt from this death reaction.
Object ExemptDrone
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = UpgradeDie ModuleTag_Die
    DeathTypes = ALL
    ExemptStatus = SOLD
    UpgradeToRemove = Upgrade_Drone
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD1Eu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static UpgradeDieModule DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<UpgradeDieModule>().Single();

    private static UpgradeDieModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        game.AssetStore.ObjectDefinitions.GetByName(definitionName)
            .Behaviors.Values.Select(x => x.Data).OfType<UpgradeDieModuleData>().Single();

    /// <summary>
    /// Producer holding the upgrade + a drone it produced. This is the shape the GPL comment
    /// describes: "ranger building scout drones".
    /// </summary>
    private static (GameObject Producer, GameObject Drone) SpawnPair(
        HeadlessSimGame game, string droneDefinition = "Drone", string upgradeName = "Upgrade_Drone")
    {
        var producer = game.SpawnObject("DroneProducer", game.CivilianPlayer, Vector3.Zero);
        producer.Upgrade(Upgrade(game, upgradeName));

        var drone = game.SpawnObject(droneDefinition, game.CivilianPlayer, new Vector3(20, 0, 0));
        drone.CreatedByObjectID = producer.Id;

        return (producer, drone);
    }

    [Fact]
    public void Death_FreesTheProducersUpgrade()
    {
        var game = NewGame();
        var (producer, drone) = SpawnPair(game);
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));

        PortedModuleTestKit.TriggerDeath(drone);

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
    }

    [Fact]
    public void Death_LeavesUnrelatedUpgradesAlone()
    {
        var game = NewGame();
        var (producer, drone) = SpawnPair(game);
        producer.Upgrade(Upgrade(game, "Upgrade_OtherThing"));

        PortedModuleTestKit.TriggerDeath(drone);

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_OtherThing")));
    }

    [Fact]
    public void SubLethalDamage_DoesNotFreeTheUpgrade()
    {
        var game = NewGame();
        var (producer, drone) = SpawnPair(game);

        var result = PortedModuleTestKit.ApplyDamage(drone, amount: 40f);

        Assert.False(result.Died);
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
    }

    [Fact]
    public void TwoTokenUpgradeToRemove_ParsesTheTagAndStillFreesTheUpgrade()
    {
        var game = NewGame();
        var (producer, drone) = SpawnPair(game, droneDefinition: "TaggedDrone");

        var data = DataOf(game, "TaggedDrone");
        Assert.Equal("BaseUpgradeTag_01", data.UpgradeToRemove.ModuleTag);
        Assert.Equal("Upgrade_Drone", data.UpgradeToRemove.UpgradeName.Value.Name);

        PortedModuleTestKit.TriggerDeath(drone);

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
    }

    [Fact]
    public void OneTokenUpgradeToRemove_HasNoModuleTag()
    {
        // The ZH form is the one eight AotR object files use and the one that used to throw
        // "Expected a token" and take the whole file down.
        var game = NewGame();
        var data = DataOf(game, "Drone");

        Assert.Null(data.UpgradeToRemove.ModuleTag);
        Assert.Equal("Upgrade_Drone", data.UpgradeToRemove.UpgradeName.Value.Name);
    }

    [Fact]
    public void DeathTypesFilter_OnlyTheListedDeathFreesTheUpgrade()
    {
        var game = NewGame();

        var (producerA, droneA) = SpawnPair(game, droneDefinition: "BurnOnlyDrone");
        PortedModuleTestKit.TriggerDeath(droneA, DeathType.Normal);
        Assert.True(producerA.HasUpgrade(Upgrade(game, "Upgrade_Drone")));

        var (producerB, droneB) = SpawnPair(game, droneDefinition: "BurnOnlyDrone");
        PortedModuleTestKit.TriggerDeath(droneB, DeathType.Burned);
        Assert.False(producerB.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
    }

    [Fact]
    public void ExemptStatus_SuppressesTheUpgradeRemoval()
    {
        var game = NewGame();
        var (producer, drone) = SpawnPair(game, droneDefinition: "ExemptDrone");
        drone.SetObjectStatus(ObjectStatus.Sold, true);

        PortedModuleTestKit.TriggerDeath(drone);

        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));

        // Control: the same definition without the exempt status DOES free the upgrade, so
        // the assertion above is measuring the status gate and not a broken setup.
        var (control, controlDrone) = SpawnPair(game, droneDefinition: "ExemptDrone");
        PortedModuleTestKit.TriggerDeath(controlDrone);
        Assert.False(control.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
    }

    [Fact]
    public void NoProducer_IsASilentNoOp()
    {
        // A drone that was never produced (CreatedByObjectID unset) must not throw.
        var game = NewGame();
        var drone = game.SpawnObject("Drone", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.TriggerDeath(drone);

        Assert.True(result.Died);
    }

    [Fact]
    public void ProducerDiedFirst_IsASilentNoOp()
    {
        var game = NewGame();
        var (producer, drone) = SpawnPair(game);

        producer.Destroy();
        game.Step();   // the destroy-list reap: the producer leaves the object list
        Assert.True(producer.IsDestroyed);

        var result = PortedModuleTestKit.TriggerDeath(drone);

        Assert.True(result.Died);
    }

    [Fact]
    public void ProducerWithoutTheUpgrade_RemovesNothing()
    {
        // GPL asserts here and does nothing: a data error must not mutate the upgrade set.
        var game = NewGame();
        var producer = game.SpawnObject("DroneProducer", game.CivilianPlayer, Vector3.Zero);
        producer.Upgrade(Upgrade(game, "Upgrade_OtherThing"));

        var drone = game.SpawnObject("Drone", game.CivilianPlayer, new Vector3(20, 0, 0));
        drone.CreatedByObjectID = producer.Id;

        PortedModuleTestKit.TriggerDeath(drone);

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_Drone")));
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_OtherThing")));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var (_, drone) = SpawnPair(game);
        var live = DieModuleOf(drone);

        // Mid-behavior: the object has taken damage and ticked, but has not died yet - the
        // only window in which this module is alive to be walked at all.
        PortedModuleTestKit.ApplyDamage(drone, amount: 30f);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("Drone", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = DieModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script; game B round-trips the module's walk through
        // Save->Load mid-behavior, before the death. If the walk read or wrote anything wrong,
        // B's death reaction differs from A's.
        Assert.Equal(RunScenario(roundTripAtFrame: -1), RunScenario(roundTripAtFrame: 3));
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var (producer, drone) = SpawnPair(game);
        var module = DieModuleOf(drone);
        var upgrade = Upgrade(game, "Upgrade_Drone");

        // Frame 6 kills the drone; every frame records whether the producer still holds it.
        var trajectory = new bool[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            if (i == 6)
            {
                PortedModuleTestKit.TriggerDeath(drone);
            }

            game.Step();
            trajectory[i] = producer.HasUpgrade(upgrade);
        }

        return trajectory;
    }
}

// Mocked-game unit tests for the SlaveWatcherBehavior port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch from the R13 task packet
// (bfme2-workbench/research/modules-r13/specs/SlaveWatcherBehaviorModuleData.md §3), each
// shaped [create -> tick -> observable effect], plus the shadow-copy base test and a
// mid-behavior save/load round-trip.
//
// Sleepy-update caveat (applies to every case below, spec §3): a freshly spawned
// UpdateModule's wake frame is not guaranteed live in the same HeadlessSimGame.Step() call
// that spawned it - the module's first Update() call lands on the SECOND Step() after the
// SlaveWatcherBehavior-carrying object itself is created. This module's discovery step reads
// only the slave's CreatedByObjectID field and IsDestroyed/IsEffectivelyDead status - all live
// immediately at spawn, not Update()-driven - so a freshly spawned slave does not need its own
// Update() tick to be discoverable; only the WATCHER's own Update() must run at least once
// after CreatedByObjectID is stamped and, separately, at least once again after any kill.
//
// The corpus's real producer/slave pair is wired through ObjectCreationUpgrade (an
// UpgradeModule keyed on TriggeredBy/UpgradeObject). This test file follows
// UpgradeDieContractTests' precedent instead: it stamps CreatedByObjectID directly on a
// separately-spawned slave, isolating THIS module (discovery/mirroring/death-handling/
// destroy-cascade) from ObjectCreationUpgrade's own, separately-tested, spawn machinery.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class SlaveWatcherBehaviorContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_SpawnTheSlave
  Type = OBJECT
End

Upgrade Upgrade_SlaveDied
  Type = OBJECT
End

Upgrade Upgrade_FlamingMunitions
  Type = OBJECT
End

Object Producer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SlaveWatcherBehavior ModuleTag_Watch
    RemoveUpgrade = Upgrade_SpawnTheSlave
    GrantUpgrade = Upgrade_SlaveDied
    ShareUpgrades = Yes
    ; LetSlaveLive omitted -> defaults false (kill cascade), per spec §1.3
  End
End

; No RemoveUpgrade/GrantUpgrade at all - matches armedminers.ini's live corpus shape (spec §0).
Object ProducerOptionalFieldsOmitted
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SlaveWatcherBehavior ModuleTag_WatchNoOptional
    ShareUpgrades = Yes
  End
End

; LetSlaveLive explicitly Yes - matches wildfortress.ini's authored value (spec §0).
Object ProducerLetSlaveLive
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SlaveWatcherBehavior ModuleTag_WatchLetLive
    RemoveUpgrade = Upgrade_SpawnTheSlave
    GrantUpgrade = Upgrade_SlaveDied
    LetSlaveLive = Yes
  End
End

Object Slave
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x51A7E) // 'slave'
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static SlaveWatcherBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SlaveWatcherBehavior>().Single();

    /// <summary>
    /// Spawns a Producer/Slave pair with CreatedByObjectID already stamped (the discovery
    /// step's only precondition, spec §1.1) and drives the watcher's own Update() to its
    /// first guaranteed tick.
    /// </summary>
    private static (GameObject Producer, GameObject Slave) SpawnDiscoveredPair(
        HeadlessSimGame game, string producerDefinition = "Producer")
    {
        var producer = game.SpawnObject(producerDefinition, game.CivilianPlayer, Vector3.Zero);
        var slave = game.SpawnObject("Slave", game.CivilianPlayer, new Vector3(20, 0, 0));
        slave.CreatedByObjectID = producer.Id;

        game.Step();
        game.Step(); // watcher's first Update(): discovers the slave

        return (producer, slave);
    }

    // ---- case 1/2: discovery + ShareUpgrades mirroring (spec §3 cases 1-2) ----

    [Fact]
    public void DiscoversSlaveByCreatedByObjectID_ThenSharesUpgrades()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);

        // Nothing granted at spawn time; mirroring only starts once ShareUpgrades observes
        // something on the producer, proving discovery happened (not merely "no crash").
        producer.Upgrade(Upgrade(game, "Upgrade_FlamingMunitions"));
        game.Step();

        Assert.True(slave.HasUpgrade(Upgrade(game, "Upgrade_FlamingMunitions")));
    }

    [Fact]
    public void ShareUpgrades_MirrorsProducerUpgradeGrantedAfterDiscovery()
    {
        // Same as above, phrased against spec §3 case 2's own wording: an upgrade granted
        // to the producer AFTER the slave is discovered still reaches it on a later tick,
        // proving per-tick mirroring rather than a one-shot snapshot at discovery time.
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);
        Assert.False(slave.HasUpgrade(Upgrade(game, "Upgrade_FlamingMunitions")));

        producer.Upgrade(Upgrade(game, "Upgrade_FlamingMunitions"));
        game.Step();

        Assert.True(slave.HasUpgrade(Upgrade(game, "Upgrade_FlamingMunitions")));
    }

    // ---- case 3: death handling fires exactly once (spec §3 case 3) ----

    [Fact]
    public void SlaveDeath_RemovesAndGrantsUpgradesOnProducer_Once()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);
        producer.Upgrade(Upgrade(game, "Upgrade_SpawnTheSlave"));
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_SpawnTheSlave")));

        slave.Kill();
        game.Step(); // watcher's next Update(): observes IsEffectivelyDead, fires the edge

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_SpawnTheSlave")));
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_SlaveDied")));

        // Ticking further with no new slave discovered must not re-fire (no double-grant/
        // double-remove; RemoveUpgrade already absent is idempotent, GrantUpgrade already
        // present stays present via the same idempotent Add underlying it).
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_SpawnTheSlave")));
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_SlaveDied")));
    }

    // ---- case 4: re-discovery after a successor slave spawns (spec §3 case 4) ----

    [Fact]
    public void SlaveDeath_ThenNewSlaveSpawned_IsRediscovered()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);
        slave.Kill();
        game.Step(); // case 3's edge fires, _slaveId resets

        var secondSlave = game.SpawnObject("Slave", game.CivilianPlayer, new Vector3(-20, 0, 0));
        secondSlave.CreatedByObjectID = producer.Id;
        game.Step(); // re-discovery

        producer.Upgrade(Upgrade(game, "Upgrade_FlamingMunitions"));
        game.Step();

        Assert.True(secondSlave.HasUpgrade(Upgrade(game, "Upgrade_FlamingMunitions")),
            "mirroring must reach the NEW slave, proving re-discovery after the old slave's reset");
    }

    // ---- case 5: RemoveUpgrade/GrantUpgrade are independently optional (spec §3 case 5) ----

    [Fact]
    public void RemoveUpgradeAndGrantUpgrade_OptionalFieldsAreIndependentlyOptional()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game, "ProducerOptionalFieldsOmitted");

        slave.Kill();
        game.Step(); // must not throw with both fields absent

        // No upgrade change on the producer (both fields are silent no-ops per spec §1 step 3).
        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_SpawnTheSlave")));
        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_SlaveDied")));

        // _slaveId still reset -> a later slave is still discoverable (case 4's mechanism is
        // independent of these two fields being populated).
        var secondSlave = game.SpawnObject("Slave", game.CivilianPlayer, new Vector3(-20, 0, 0));
        secondSlave.CreatedByObjectID = producer.Id;
        game.Step();

        producer.Upgrade(Upgrade(game, "Upgrade_FlamingMunitions"));
        game.Step();

        Assert.True(secondSlave.HasUpgrade(Upgrade(game, "Upgrade_FlamingMunitions")));
    }

    // ---- case 6/7: LetSlaveLive producer-destroy cascade (spec §3 cases 6-7) ----

    [Fact]
    public void LetSlaveLiveDefaultFalse_ProducerDestroyed_SlaveDies()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);

        game.GameLogic.DestroyObject(producer);

        Assert.True(slave.IsDestroyed, "the default (LetSlaveLive omitted -> false) kill cascade must fire");
    }

    [Fact]
    public void LetSlaveLiveTrue_ProducerDestroyed_SlaveSurvives()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game, "ProducerLetSlaveLive");

        game.GameLogic.DestroyObject(producer);

        Assert.False(slave.IsDestroyed, "LetSlaveLive = Yes must exempt the tracked slave from the cascade");
    }

    // ---- case 8/9: destroy-cascade edge guards (spec §3 cases 8-9) ----

    [Fact]
    public void LetSlaveLive_NoTrackedSlave_ProducerDestroyed_NoException()
    {
        var game = NewGame();
        var producer = game.SpawnObject("Producer", game.CivilianPlayer, Vector3.Zero);
        game.Step();
        game.Step(); // watcher's first Update(): no slave exists yet, _slaveId stays Invalid

        var exception = Record.Exception(() => game.GameLogic.DestroyObject(producer));
        Assert.Null(exception);
    }

    [Fact]
    public void AlreadyDestroyedSlave_ProducerDestroyed_NoDoubleDestroyException()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);

        // Fully reap the tracked slave directly (bypassing Kill/IsEffectivelyDead), leaving
        // _slaveId still pointed at it (no watcher Update() runs between this and the
        // producer's own destroy below, so the module never gets a chance to notice and
        // reset) - the same shape FloodUpdate's own `!memberObject.IsDestroyed` guard exists
        // for.
        game.GameLogic.DestroyObject(slave);
        Assert.True(slave.IsDestroyed);

        var exception = Record.Exception(() => game.GameLogic.DestroyObject(producer));
        Assert.Null(exception);
    }

    // ---- shadow-copy + save/load round-trip (spec §3 cases 10-11) ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var (producer, _) = SpawnDiscoveredPair(game); // _slaveId now non-default, the one Xfer'd field
        var live = ModuleOf(producer);

        var shadowHost = game.SpawnObject("Producer", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_RoundTrips_MidBehavior()
    {
        var game = NewGame();
        var (producer, slave) = SpawnDiscoveredPair(game);
        var module = ModuleOf(producer);

        var state = PortedModuleTestKit.Save(module);
        var wake = module.NextWakeFrameForWalk;
        PortedModuleTestKit.Load(module, state);
        module.NextWakeFrameForWalk = wake;

        // The reloaded module must still correctly fire the death-handling edge on the SAME
        // slave, proving _slaveId round-tripped through XferObjectId and still resolves via
        // GetObjectById after a load.
        producer.Upgrade(Upgrade(game, "Upgrade_SpawnTheSlave"));
        slave.Kill();
        game.Step();

        Assert.False(producer.HasUpgrade(Upgrade(game, "Upgrade_SpawnTheSlave")));
        Assert.True(producer.HasUpgrade(Upgrade(game, "Upgrade_SlaveDied")));
    }
}

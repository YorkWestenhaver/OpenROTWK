// Mocked-game unit tests for the RefundDie port (api-freeze-v1 §6 fitness item 4,
// research/modules-r13/specs/RefundDieModuleData.md §3): one test per INI-configurable branch,
// each shaped [create -> trigger death -> observable effect] through the batch's death-trigger
// helper, plus the shadow-copy base test and a mid-behavior save/load continuation.
//
// The observable effect for this class is the dying object's OWN owner's bank account.
// RefundDie is a DieModule: it has no Update() and no sleepy-update/wake-frame participation at
// all (it fires synchronously from ActiveBody's >0 -> <=0 health crossing), so unlike
// UpdateModule ports there is no "first Update runs on the second Step()" window to account for
// here. The one game.Step() call in this file is the ordinary destroy-list reap after a lethal
// hit (mirrors UpgradeDieContractTests.ProducerDiedFirst_IsASilentNoOp), unrelated to this
// module's own dispatch timing.

using System.IO;
using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class RefundDieContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_Refundable
  Type = OBJECT
End

Upgrade Upgrade_Other
  Type = OBJECT
End

; Baseline: no gates, 50% of BuildCost = 1000 refunded on death.
Object Refundable
  KindOf = STRUCTURE
  BuildCost = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = ALL
    RefundPercent = 50%
  End
End

; Upgrade-gated: only refunds if THIS object holds Upgrade_Refundable.
Object UpgradeGatedRefundable
  KindOf = STRUCTURE
  BuildCost = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = ALL
    RefundPercent = 50%
    UpgradeRequired = Upgrade_Refundable
  End
End

; Killer-gated: only refunds if the killer matches +STRUCTURE.
Object BuildingGatedRefundable
  KindOf = STRUCTURE
  BuildCost = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = ALL
    RefundPercent = 50%
    BuildingRequired = +STRUCTURE
  End
End

; Both gates set.
Object BothGatedRefundable
  KindOf = STRUCTURE
  BuildCost = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = ALL
    RefundPercent = 50%
    UpgradeRequired = Upgrade_Refundable
    BuildingRequired = +STRUCTURE
  End
End

; Zero refund percent.
Object ZeroPercentRefundable
  KindOf = STRUCTURE
  BuildCost = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = ALL
    RefundPercent = 0%
  End
End

; Zero build cost.
Object ZeroCostRefundable
  KindOf = STRUCTURE
  BuildCost = 0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = ALL
    RefundPercent = 50%
  End
End

; The Die gate: only a BURNED death refunds.
Object BurnOnlyRefundable
  KindOf = STRUCTURE
  BuildCost = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = RefundDie ModuleTag_Die
    DeathTypes = NONE +BURNED
    RefundPercent = 50%
  End
End

; Killers used to exercise BuildingRequired.
Object InfantryKiller
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object StructureKiller
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xEFDDu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeTemplate Upgrade(HeadlessSimGame game, string name) =>
        game.AssetStore.Upgrades.GetByName(name);

    private static RefundDieModule DieModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<RefundDieModule>().Single();

    private static RefundDieModuleData DataOf(HeadlessSimGame game, string definitionName) =>
        game.AssetStore.ObjectDefinitions.GetByName(definitionName)
            .Behaviors.Values.Select(x => x.Data).OfType<RefundDieModuleData>().Single();

    private static GameObject Spawn(HeadlessSimGame game, string definitionName, Player owner = null) =>
        game.SpawnObject(definitionName, owner ?? game.CivilianPlayer, Vector3.Zero);

    [Fact]
    public void Parse_RoundTripsAllThreeFields_WithTheFixedUpgradeReferenceType()
    {
        var game = NewGame();

        var gated = DataOf(game, "BothGatedRefundable");
        Assert.Equal("Upgrade_Refundable", gated.UpgradeRequired.Value.Name);
        Assert.NotNull(gated.BuildingRequired);
        Assert.Equal(new Percentage(0.5f), gated.RefundPercent);

        var baseline = DataOf(game, "Refundable");
        Assert.Null(baseline.UpgradeRequired);
        Assert.Null(baseline.BuildingRequired);
        Assert.Equal(new Percentage(0.5f), baseline.RefundPercent);
    }

    [Fact]
    public void NoGatesSet_DeathRefundsThePercentageOfBuildCost()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "Refundable", owner);
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj);

        Assert.Equal(before + 500u, owner.BankAccount.Money);
    }

    [Fact]
    public void UpgradeRequired_ObjectWithoutTheUpgrade_GetsNoRefund()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "UpgradeGatedRefundable", owner);
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj);

        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void UpgradeRequired_ObjectWithTheUpgrade_IsRefunded()
    {
        // Control for the previous test: the same definition, with the upgrade granted,
        // DOES pay the refund - isolates the gate from a broken setup.
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "UpgradeGatedRefundable", owner);
        obj.Upgrade(Upgrade(game, "Upgrade_Refundable"));
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj);

        Assert.Equal(before + 500u, owner.BankAccount.Money);
    }

    [Fact]
    public void BuildingRequired_NoKiller_GetsNoRefund()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "BuildingGatedRefundable", owner);
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj, source: null);

        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void BuildingRequired_KillerDoesNotMatch_GetsNoRefund()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "BuildingGatedRefundable", owner);
        var killer = Spawn(game, "InfantryKiller");
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj, source: killer);

        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void BuildingRequired_KillerMatches_IsRefunded()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "BuildingGatedRefundable", owner);
        var killer = Spawn(game, "StructureKiller");
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj, source: killer);

        Assert.Equal(before + 500u, owner.BankAccount.Money);
    }

    [Fact]
    public void BothGatesSet_OnlyPaysWhenBothPass()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var structureKiller = Spawn(game, "StructureKiller");
        var infantryKiller = Spawn(game, "InfantryKiller");

        // Upgrade missing, killer matches -> no refund.
        var a = Spawn(game, "BothGatedRefundable", owner);
        var beforeA = owner.BankAccount.Money;
        PortedModuleTestKit.TriggerDeath(a, source: structureKiller);
        Assert.Equal(beforeA, owner.BankAccount.Money);

        // Upgrade present, killer does not match -> no refund.
        var b = Spawn(game, "BothGatedRefundable", owner);
        b.Upgrade(Upgrade(game, "Upgrade_Refundable"));
        var beforeB = owner.BankAccount.Money;
        PortedModuleTestKit.TriggerDeath(b, source: infantryKiller);
        Assert.Equal(beforeB, owner.BankAccount.Money);

        // Upgrade present, no killer -> no refund.
        var c = Spawn(game, "BothGatedRefundable", owner);
        c.Upgrade(Upgrade(game, "Upgrade_Refundable"));
        var beforeC = owner.BankAccount.Money;
        PortedModuleTestKit.TriggerDeath(c, source: null);
        Assert.Equal(beforeC, owner.BankAccount.Money);

        // Both pass -> full refund.
        var d = Spawn(game, "BothGatedRefundable", owner);
        d.Upgrade(Upgrade(game, "Upgrade_Refundable"));
        var beforeD = owner.BankAccount.Money;
        PortedModuleTestKit.TriggerDeath(d, source: structureKiller);
        Assert.Equal(beforeD + 500u, owner.BankAccount.Money);
    }

    [Fact]
    public void ZeroRefundPercent_IsASilentNoOp()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "ZeroPercentRefundable", owner);
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj);

        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void ZeroBuildCost_IsASilentNoOp()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "ZeroCostRefundable", owner);
        var before = owner.BankAccount.Money;

        PortedModuleTestKit.TriggerDeath(obj);

        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void DeathTypesFilter_OnlyTheListedDeathRefunds()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;

        var normalDeath = Spawn(game, "BurnOnlyRefundable", owner);
        var beforeNormal = owner.BankAccount.Money;
        PortedModuleTestKit.TriggerDeath(normalDeath, DeathType.Normal);
        Assert.Equal(beforeNormal, owner.BankAccount.Money);

        var burnedDeath = Spawn(game, "BurnOnlyRefundable", owner);
        var beforeBurned = owner.BankAccount.Money;
        PortedModuleTestKit.TriggerDeath(burnedDeath, DeathType.Burned);
        Assert.Equal(beforeBurned + 500u, owner.BankAccount.Money);
    }

    [Fact]
    public void SubLethalDamage_DoesNotRefund()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "Refundable", owner);
        var before = owner.BankAccount.Money;

        var result = PortedModuleTestKit.ApplyDamage(obj, amount: 40f);

        Assert.False(result.Died);
        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void NoKiller_IsASilentNoOp()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "BuildingGatedRefundable", owner);

        var result = PortedModuleTestKit.TriggerDeath(obj, source: null);

        Assert.True(result.Died);
        Assert.Equal(0u, owner.BankAccount.Money);
    }

    [Fact]
    public void KillerDiedFirst_IsASilentNoOp()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var killer = Spawn(game, "StructureKiller");
        var obj = Spawn(game, "BuildingGatedRefundable", owner);
        var before = owner.BankAccount.Money;

        killer.Destroy();
        game.Step(); // the destroy-list reap: the killer leaves the object list
        Assert.True(killer.IsDestroyed);

        var result = PortedModuleTestKit.TriggerDeath(obj, source: killer);

        Assert.True(result.Died);
        Assert.Equal(before, owner.BankAccount.Money);
    }

    [Fact]
    public void Xfer_IsVersionOnly_AndStateInventoryIsEmpty()
    {
        var game = NewGame();
        var obj = Spawn(game, "Refundable");

        Assert.Equal(new byte[] { 0x01 }, PortedModuleTestKit.Save(DieModuleOf(obj)));

        // Two instances are indistinguishable, because there is nothing to distinguish.
        var other = Spawn(game, "Refundable");
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(DieModuleOf(obj)),
            PortedModuleTestKit.LiveCrc(DieModuleOf(other)));
    }

    [Fact]
    public void Xfer_RejectsAFutureVersion()
    {
        var game = NewGame();
        var obj = Spawn(game, "Refundable");

        Assert.Throws<InvalidDataException>(
            () => PortedModuleTestKit.Load(DieModuleOf(obj), new byte[] { 0x02 }));
    }

    [Fact]
    public void PortConstructsThroughTheContractCtor()
    {
        var game = NewGame();
        var obj = Spawn(game, "Refundable");
        var module = DieModuleOf(obj);

        Assert.IsAssignableFrom<DieModule>(module);
        Assert.Contains(module, game.GameEngine.SimContext.GameLogic
            .GetObjectById(obj.Id).BehaviorModules);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var obj = Spawn(game, "Refundable");
        var live = DieModuleOf(obj);

        // Mid-behavior: the object has taken damage and ticked, but has not died yet - the
        // only window in which this module is alive to be walked at all.
        PortedModuleTestKit.ApplyDamage(obj, amount: 30f);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        var shadowHost = Spawn(game, "Refundable");
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

    private static uint[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEEDu);
        var owner = game.CivilianPlayer;
        var obj = Spawn(game, "Refundable", owner);
        var module = DieModuleOf(obj);

        // Frame 6 kills the object; every frame records the owner's current money.
        var trajectory = new uint[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            if (i == 6)
            {
                PortedModuleTestKit.TriggerDeath(obj);
            }

            game.Step();
            trajectory[i] = owner.BankAccount.Money;
        }

        return trajectory;
    }
}

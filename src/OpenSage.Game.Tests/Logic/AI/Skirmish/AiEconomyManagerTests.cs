#nullable enable

// S9-03 (R15 L3) gate tests: AiEconomyManager, the v1 income/spend model.
//
// Same discipline as SkirmishAIBrainSpineTests.cs: no game, no INI files, no map - everything
// runs off a hand-set FakeAiWorldView. AIData/SkirmishAIData only expose their fields through
// private setters (they are normally built by IniParser), so the handful of tests that need
// specific Poor/Wealthy/FarmingThreshold values build a real instance and set those fields via
// reflection (SetPrivate below) rather than adding test-only public setters to production
// asset-data types.

using System;
using System.Reflection;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiEconomyManagerTests
{
    // ---- fixture builders ----

    private static AIData MakeAiData(int poor, int wealthy)
    {
        var data = new AIData();
        SetPrivate(data, nameof(AIData.Poor), poor);
        SetPrivate(data, nameof(AIData.Wealthy), wealthy);
        return data;
    }

    private static SkirmishAIData MakeSkirmishAiData(int farmingThreshold)
    {
        var data = new SkirmishAIData();
        SetPrivate(data, nameof(SkirmishAIData.FarmingThreshold), farmingThreshold);
        return data;
    }

    private static void SetPrivate(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (property is null)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found on {target.GetType()}.");
        }

        property.SetValue(target, value);
    }

    // heartbeatInterval is set so no CurrentFrame value used below (0, 1, 10, 11, 30, 42) is a
    // multiple of it - tests call brain.Update() (which emits the spine's heartbeat line ahead
    // of every manager, per SkirmishAIBrainSpineTests) and assert only the econ line that
    // followed it, so a stray heartbeat at one of those exact frames would corrupt the
    // trace-line assertions below.
    private const uint NoHeartbeatAtTestFrames = 97;

    private static (AiEconomyManager Manager, FakeAiWorldView World, SkirmishAIBrain Brain, RecordingAiTraceSink TraceSink) NewManager(int playerIndex = 0)
    {
        var world = new FakeAiWorldView { PlayerIndex = playerIndex };
        var traceSink = new RecordingAiTraceSink();
        var brain = new SkirmishAIBrain(world, new RecordingOrderSink(), new AiTrace(playerIndex, traceSink), NoHeartbeatAtTestFrames);
        var manager = new AiEconomyManager();
        brain.RegisterManager(manager);
        return (manager, world, brain, traceSink);
    }

    // ---- identity ----

    [Fact]
    public void Name_IsEcon()
    {
        Assert.Equal("econ", new AiEconomyManager().Name);
    }

    [Fact]
    public void SpendPlan_IsEmpty_BeforeTheFirstUpdate()
    {
        var manager = new AiEconomyManager();

        Assert.Equal(SpendPlan.Empty, manager.SpendPlan);
        Assert.True(manager.CanAfford(0));
        Assert.False(manager.CanAfford(1));
    }

    // ---- classification: no data present ----

    [Fact]
    public void Update_WithNoAiDataAndNoSkirmishAiData_ClassifiesNormalAndReservesNothing()
    {
        var (manager, world, brain, _) = NewManager();
        world.CurrentFrame = 42;
        world.Money = 800;

        brain.Update();

        var plan = manager.SpendPlan;
        Assert.Equal(42u, plan.Frame);
        Assert.Equal(800, plan.Money);
        Assert.Equal(EconomyClassification.Normal, plan.Classification);
        Assert.Equal(0, plan.Reserve);
        Assert.Equal(800, plan.Available);
    }

    // ---- classification: AIData thresholds ----

    [Fact]
    public void Update_BelowAiDataPoor_ClassifiesPoorAndReserves25Percent()
    {
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.Money = 500;

        brain.Update();

        var plan = manager.SpendPlan;
        Assert.Equal(EconomyClassification.Poor, plan.Classification);
        Assert.Equal(125, plan.Reserve); // 500 * 25 / 100
        Assert.Equal(375, plan.Available);
    }

    [Fact]
    public void Update_AboveAiDataWealthy_ClassifiesWealthyAndReservesNothing()
    {
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.Money = 6000;

        brain.Update();

        var plan = manager.SpendPlan;
        Assert.Equal(EconomyClassification.Wealthy, plan.Classification);
        Assert.Equal(0, plan.Reserve);
        Assert.Equal(6000, plan.Available);
    }

    [Fact]
    public void Update_BetweenAiDataThresholds_ClassifiesNormal()
    {
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.Money = 3000;

        brain.Update();

        Assert.Equal(EconomyClassification.Normal, manager.SpendPlan.Classification);
        Assert.Equal(0, manager.SpendPlan.Reserve);
    }

    [Theory]
    [InlineData(1000)] // exactly the Poor floor is not "< Poor"
    [InlineData(5000)] // exactly the Wealthy ceiling is not "> Wealthy"
    public void Update_AtAnExactThreshold_ClassifiesNormal_NoFloatEqualityInvolved(int moneyAtThreshold)
    {
        // Both bounds are strict (< / >) so the boundary values themselves fall in Normal - int
        // comparisons only, nothing here rounds or compares floats for equality.
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.Money = moneyAtThreshold;

        brain.Update();

        Assert.Equal(EconomyClassification.Normal, manager.SpendPlan.Classification);
    }

    // ---- classification: SkirmishAIData.FarmingThreshold folded in ----

    [Fact]
    public void Update_BelowFarmingThreshold_OverridesToPoor_EvenAboveAiDataPoor()
    {
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.SkirmishAIData = MakeSkirmishAiData(farmingThreshold: 2000);
        world.Money = 1500; // clears AIData.Poor (1000) but not the farming floor (2000)

        brain.Update();

        var plan = manager.SpendPlan;
        Assert.Equal(EconomyClassification.Poor, plan.Classification);
        Assert.Equal(375, plan.Reserve); // 1500 * 25 / 100
    }

    [Fact]
    public void Update_AboveFarmingThresholdAndAiDataPoor_ClassifiesNormal()
    {
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.SkirmishAIData = MakeSkirmishAiData(farmingThreshold: 2000);
        world.Money = 2500;

        brain.Update();

        Assert.Equal(EconomyClassification.Normal, manager.SpendPlan.Classification);
    }

    [Fact]
    public void Update_SkirmishAiDataPresentButNoAiData_StillAppliesFarmingThreshold()
    {
        var (manager, world, brain, _) = NewManager();
        world.SkirmishAIData = MakeSkirmishAiData(farmingThreshold: 300);
        world.Money = 200;

        brain.Update();

        Assert.Equal(EconomyClassification.Poor, manager.SpendPlan.Classification);
    }

    // ---- CanAfford: the single reserve policy ----

    [Fact]
    public void CanAfford_ChecksAgainstAvailable_NotRawMoney()
    {
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.Money = 500; // Poor -> reserve 125, available 375

        brain.Update();

        Assert.True(manager.CanAfford(375));
        Assert.False(manager.CanAfford(376));
        Assert.True(manager.SpendPlan.CanAfford(375));
    }

    [Fact]
    public void CanAfford_RejectsANegativeCost()
    {
        var (manager, world, brain, _) = NewManager();
        world.Money = 100;
        brain.Update();

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.CanAfford(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.SpendPlan.CanAfford(-1));
    }

    // ---- rising money across frames: the M-a evidence, at the economy-manager level ----

    [Fact]
    public void Update_RisingMoneyAcrossFrames_ProducesADeltaInSuccessivePlans()
    {
        // Mirrors SkirmishAIBrainSpineTests.Heartbeat_TracksRisingMoney_TheMaEvidence one level
        // up: the spine test proves the heartbeat reads live money, this proves the economy
        // manager's own published plan tracks it the same way, frame T then frame T+1.
        var (manager, world, brain, _) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);

        world.CurrentFrame = 10;
        world.Money = 400; // Poor
        brain.Update();
        var planAtT = manager.SpendPlan;

        world.CurrentFrame = 11;
        world.Money = 1200; // crosses into Normal one frame later
        brain.Update();
        var planAtTPlus1 = manager.SpendPlan;

        Assert.Equal(EconomyClassification.Poor, planAtT.Classification);
        Assert.Equal(EconomyClassification.Normal, planAtTPlus1.Classification);
        Assert.Equal(800, planAtTPlus1.Money - planAtT.Money);
        Assert.Equal(0, planAtTPlus1.Reserve); // reserve is released once no longer Poor
    }

    // ---- trace ----

    [Fact]
    public void Update_EmitsOneEconTraceLine_WithTheComputedPlan()
    {
        var (manager, world, brain, traceSink) = NewManager(playerIndex: 3);
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.CurrentFrame = 30;
        world.Money = 500;

        brain.Update();

        Assert.Equal(
            new[] { "[AI p3] econ f=30 money=500 class=poor reserve=125 avail=375" },
            traceSink.Lines);
    }

    [Fact]
    public void Update_TraceLine_ReportsWealthyAndNormalTags()
    {
        var (manager, world, brain, traceSink) = NewManager();
        world.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        world.CurrentFrame = 1;
        world.Money = 9000;

        brain.Update();

        Assert.Equal(new[] { "[AI p0] econ f=1 money=9000 class=wealthy reserve=0 avail=9000" }, traceSink.Lines);
    }

    // ---- registration: the manager participates in the brain's tick like any other ----

    [Fact]
    public void Manager_RegisteredOnBrain_IsFoundByGetManager()
    {
        var (manager, _, brain, _) = NewManager();

        Assert.Same(manager, brain.GetManager<AiEconomyManager>());
    }
}

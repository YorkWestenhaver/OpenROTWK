#nullable enable

// S9-06 (R15 L3) gate tests: AiBaseManager v1, the manager the dr-0039 M-b criterion grades.
//
// Same discipline as AiEconomyManagerTests/SkirmishAIBrainSpineTests: no game, no INI files, no
// map. Everything runs off a hand-set FakeAiWorldView, a RecordingOrderSink and a
// RecordingAiTraceSink, so "what did the AI do this frame" is exactly "what landed in the sink".
//
// TWO THINGS THESE TESTS EXIST TO PIN
//
// 1. M-b is CONFIRMED, not optimistic. AiMatchReport.FoundationConstructCounter must be bumped
//    only when a later snapshot shows the targeted plot occupied - never at emission time. If
//    somebody "simplifies" that into a bump on emit, MbCounter_IsNotBumped_OnEmissionAlone goes
//    red, which is the whole point of it.
// 2. Every wait is an N-frame window that lapses on the (N+1)th frame (the round's T+1
//    convention). The cooldown tests assert BOTH sides of that boundary on purpose: a test that
//    only asserted the far side would pass on an off-by-one that makes the AI act a frame early.
//
// Reflection is used to set SkirmishAIData/DifficultyTuning fields for the same reason
// AiEconomyManagerTests uses it: those are INI-parsed asset types with private setters, and
// adding test-only public setters to production data types is worse than a two-line helper.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Logic.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiBaseManagerTests
{
    // ---- fixture --------------------------------------------------------------------------

    /// <summary>
    /// Big enough that no frame any test below reaches is a multiple of it, so the spine's
    /// heartbeat never interleaves with the assertions on trace content.
    /// </summary>
    private const uint NoHeartbeat = 1_000_003;

    private sealed class Fixture
    {
        public required FakeAiWorldView World { get; init; }

        public required RecordingOrderSink Sink { get; init; }

        public required RecordingAiTraceSink TraceSink { get; init; }

        public required SkirmishAIBrain Brain { get; init; }

        public required AiOrderEmitter Emitter { get; init; }

        public AiEconomyManager? Economy { get; init; }

        public required AiBaseManager Manager { get; init; }

        /// <summary>Runs one logic frame at the world's current frame.</summary>
        public void Tick() => Brain.Update();

        /// <summary>Advances to <paramref name="frame"/>, running one tick per frame on the way.</summary>
        public void TickThrough(uint frame)
        {
            while (World.CurrentFrame < frame)
            {
                World.AdvanceFrame();
                Brain.Update();
            }
        }

        public int Count(string counter) => Brain.Trace.GetCount(counter);

        public IReadOnlyList<Order> Orders => Sink.Orders;
    }

    private static Fixture NewFixture(
        bool withEconomy = false,
        uint buildCooldownFrames = AiBaseManager.DefaultBuildCooldownFrames,
        uint confirmWindowFrames = AiBaseManager.DefaultConfirmWindowFrames,
        uint unpackCooldownFrames = AiBaseManager.DefaultUnpackCooldownFrames)
    {
        var world = new FakeAiWorldView { PlayerIndex = 2 };
        var sink = new RecordingOrderSink();
        var traceSink = new RecordingAiTraceSink();
        var brain = new SkirmishAIBrain(world, sink, new AiTrace(world.PlayerIndex, traceSink), NoHeartbeat);

        var economy = withEconomy ? new AiEconomyManager() : null;
        if (economy is not null)
        {
            brain.RegisterManager(economy);
        }

        var emitter = new AiOrderEmitter(brain);
        brain.RegisterManager(emitter);

        var manager = new AiBaseManager(emitter, economy, buildCooldownFrames, confirmWindowFrames, unpackCooldownFrames);
        brain.RegisterManager(manager);

        return new Fixture
        {
            World = world,
            Sink = sink,
            TraceSink = traceSink,
            Brain = brain,
            Emitter = emitter,
            Economy = economy,
            Manager = manager,
        };
    }

    private static AiPlotView Plot(uint id, bool occupied = false, uint occupantId = 0)
        => new(new ObjectId(id), "CastlePlot", Vector3.Zero, AiPlotKind.BuildPlot, occupied, new ObjectId(occupantId));

    private static AiPlotView PackedCastle(uint id)
        => new(new ObjectId(id), "MordorCastleFoundation", Vector3.Zero, AiPlotKind.PackedCastle, false, ObjectId.Invalid);

    private static AiBuildableTemplate Farm(int defId = 11, string name = "MordorSlaughterHouse", int cost = 300)
        => new(defId, name, cost, AiStructureRole.Economy);

    private static AiBuildableTemplate Barracks(int defId = 22, string name = "MordorOrcPit", int cost = 400)
        => new(defId, name, cost, AiStructureRole.Producer);

    private static AiObjectView OwnedStructure(uint id, string templateName)
        => new(new ObjectId(id), templateName, Vector3.Zero, 2, true, false, 1.0f);

    private static SkirmishAIData MakeSkirmishAiData(int farmingThreshold = 0, bool disableBaseBuilding = false)
    {
        var data = new SkirmishAIData();
        SetPrivate(data, nameof(SkirmishAIData.FarmingThreshold), farmingThreshold);
        SetPrivate(data, nameof(SkirmishAIData.DisableBaseBuilding), disableBaseBuilding);
        return data;
    }

    private static DifficultyTuning MakeTuning(int economyMaxFarms)
    {
        var tuning = new DifficultyTuning();
        SetPrivate(tuning, nameof(DifficultyTuning.EconomyMaxFarms), economyMaxFarms);
        return tuning;
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

    private static Order SingleOrderOfType(Fixture fixture, OrderType type)
    {
        Assert.Single(fixture.Orders);
        Assert.Equal(type, fixture.Orders[0].OrderType);
        return fixture.Orders[0];
    }

    // ---- identity -------------------------------------------------------------------------

    [Fact]
    public void Name_IsBase()
    {
        Assert.Equal("base", AiBaseManager.ManagerName);
        Assert.Equal("base", NewFixture().Manager.Name);
    }

    [Fact]
    public void MbCounterName_IsTheFrozenReportKey()
    {
        // Blackboard S9-02 #2: AiMatchReport reads no other key, so a rename here silently
        // fails M-b forever.
        Assert.Equal("base.foundation.ok", AiBaseManager.FoundationOkCounter);
        Assert.Equal(AiMatchReport.FoundationConstructCounter, AiBaseManager.FoundationOkCounter);
    }

    [Fact]
    public void Constructor_RejectsANullEmitter()
    {
        Assert.Throws<ArgumentNullException>(() => new AiBaseManager(null!));
    }

    // ---- castle unpack --------------------------------------------------------------------

    [Fact]
    public void APackedCastle_IsUnpackedBeforeAnythingElse()
    {
        var f = NewFixture();
        f.World.PlotList.Add(PackedCastle(5));
        f.World.PlotList.Add(Plot(9));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();

        var order = SingleOrderOfType(f, OrderType.CastleUnpack);
        Assert.Equal(new ObjectId(5), order.Arguments[0].Value.ObjectId);
        Assert.Equal(2, order.PlayerIndex);
        Assert.Equal(1, f.Count(AiBaseManager.UnpackIssuedCounter));
        Assert.Equal(1, f.Manager.UnpacksIssued);
        Assert.Equal(0, f.Count(AiBaseManager.FoundationIssuedCounter));
    }

    [Fact]
    public void UnpackCooldown_LapsesOnTheFrameAfterTheWindow()
    {
        var f = NewFixture(unpackCooldownFrames: 60);
        f.World.PlotList.Add(PackedCastle(5));

        f.Tick();
        Assert.Single(f.Orders);

        // The window is 60 frames; frame 60 is still inside it (inclusive gate).
        f.TickThrough(60);
        Assert.Single(f.Orders);

        // ...and frame 61 - T+1 - is the first frame that may act again.
        f.TickThrough(61);
        Assert.Equal(2, f.Orders.Count);
        Assert.Equal(OrderType.CastleUnpack, f.Orders[1].OrderType);
    }

    [Fact]
    public void UnpackedCastle_StopsBeingAnUnpackTarget()
    {
        var f = NewFixture(unpackCooldownFrames: 2);
        f.World.PlotList.Add(PackedCastle(5));

        f.Tick();
        Assert.Single(f.Orders);

        // The sim unpacked it: the anchor is now an ordinary (occupied) plot and the ring exists.
        f.World.PlotList.Clear();
        f.World.PlotList.Add(Plot(5, occupied: true, occupantId: 40));
        f.World.PlotList.Add(Plot(6));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.TickThrough(3);

        Assert.Equal(2, f.Orders.Count);
        Assert.Equal(OrderType.FoundationConstruct, f.Orders[1].OrderType);
    }

    // ---- plot fill ------------------------------------------------------------------------

    [Fact]
    public void BuildsOnTheLowestFreePlotId()
    {
        var f = NewFixture();
        f.World.PlotList.Add(Plot(9));
        f.World.PlotList.Add(Plot(4));
        f.World.PlotList.Add(Plot(7, occupied: true, occupantId: 70));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();

        var order = SingleOrderOfType(f, OrderType.FoundationConstruct);
        Assert.Equal(new ObjectId(4), order.Arguments[0].Value.ObjectId);
        Assert.Equal(11, order.Arguments[1].Value.Integer);
    }

    [Fact]
    public void AFoundationConstruct_CarriesNoSelectionOrder()
    {
        // The castle orders name their target in their own payload; pairing them with a
        // SetSelection would leave the AI player selecting a build plot. See the S9-06 block in
        // AiOrderEmitter.cs.
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();

        Assert.Single(f.Orders);
        Assert.DoesNotContain(f.Orders, o => o.OrderType == OrderType.SetSelection);
    }

    [Fact]
    public void TheOpeningBuild_IsEconomy()
    {
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Barracks(cost: 100));
        f.World.Buildable.Add(Farm(cost: 900));
        f.World.Money = 100_000;

        f.Tick();

        // Economy wins even though the producer is nine times cheaper: the fill order is a role
        // decision first and a price decision only within the chosen role.
        Assert.Equal(11, SingleOrderOfType(f, OrderType.FoundationConstruct).Arguments[1].Value.Integer);
    }

    [Fact]
    public void OnceTheEconomyTargetIsMet_TheNextBuildIsAProducer()
    {
        var f = NewFixture();
        f.World.DifficultyTuning = MakeTuning(economyMaxFarms: 2);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Buildable.Add(Barracks());
        f.World.Own.Add(OwnedStructure(50, "MordorSlaughterHouse"));
        f.World.Own.Add(OwnedStructure(51, "MordorSlaughterHouse"));
        f.World.Money = 100_000;

        f.Tick();

        Assert.Equal(22, SingleOrderOfType(f, OrderType.FoundationConstruct).Arguments[1].Value.Integer);
    }

    [Fact]
    public void UnderTheFarmingThreshold_EconomyWinsEvenPastTheTarget()
    {
        var f = NewFixture();
        f.World.DifficultyTuning = MakeTuning(economyMaxFarms: 2);
        f.World.SkirmishAIData = MakeSkirmishAiData(farmingThreshold: 5_000);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Buildable.Add(Barracks());
        f.World.Own.Add(OwnedStructure(50, "MordorSlaughterHouse"));
        f.World.Own.Add(OwnedStructure(51, "MordorSlaughterHouse"));
        f.World.Money = 1_000;

        f.Tick();

        Assert.Equal(11, SingleOrderOfType(f, OrderType.FoundationConstruct).Arguments[1].Value.Integer);
    }

    // ---- one at a time, and confirmation ---------------------------------------------------

    [Fact]
    public void OnlyOneConstructIsEverInFlight()
    {
        var f = NewFixture(buildCooldownFrames: 1, confirmWindowFrames: 1_000);
        f.World.PlotList.Add(Plot(4));
        f.World.PlotList.Add(Plot(5));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        f.TickThrough(50);

        Assert.Single(f.Orders);
        Assert.True(f.Manager.HasPendingConstruct);
        Assert.Equal(new ObjectId(4), f.Manager.PendingPlotId);
        Assert.Equal("MordorSlaughterHouse", f.Manager.PendingTemplateName);
    }

    [Fact]
    public void MbCounter_IsNotBumped_OnEmissionAlone()
    {
        var f = NewFixture(confirmWindowFrames: 1_000);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        f.TickThrough(20);

        Assert.Equal(1, f.Count(AiBaseManager.FoundationIssuedCounter));
        Assert.Equal(0, f.Count(AiBaseManager.FoundationOkCounter));
        Assert.Equal(0, f.Manager.ConstructsConfirmed);
    }

    [Fact]
    public void AnOccupiedPlot_ConfirmsTheConstruct_AndBumpsTheMbCounter()
    {
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        Assert.Single(f.Orders);

        // The sim built it: the next snapshot shows the plot occupied.
        f.World.PlotList[0] = Plot(4, occupied: true, occupantId: 80);
        f.TickThrough(3);

        Assert.Equal(1, f.Count(AiBaseManager.FoundationOkCounter));
        Assert.Equal(1, f.Manager.ConstructsConfirmed);
        Assert.False(f.Manager.HasPendingConstruct);
    }

    [Fact]
    public void AConfirmedConstruct_MakesTheMatchReportPassMb()
    {
        // The end-to-end shape the R1 gate actually reads: manager -> AiTrace counter ->
        // AiMatchReport.PlayerSnapshot -> the frozen FoundationConstructCounter key.
        // (S9-07/INT-R1B integrate fix: S9-02's real API is
        // AiMatchReport.PlayerSnapshot.Capture(brain), not AiMatchReport.Capture(brain), and
        // "FoundationConstructed" is a PlayerResult (start/end pair) property, not a
        // PlayerSnapshot one -- a single snapshot can only be asked for the raw counter, which
        // is the same check PlayerResult.FoundationConstructed performs against its End
        // snapshot. This was a real cross-branch compile break, not a rename.)
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        f.World.PlotList[0] = Plot(4, occupied: true, occupantId: 80);
        f.TickThrough(2);

        var snapshot = AiMatchReport.PlayerSnapshot.Capture(f.Brain);
        Assert.True(snapshot.GetCount(AiMatchReport.FoundationConstructCounter) > 0);
    }

    [Fact]
    public void AnUnconfirmedConstruct_TimesOutOnTheFrameAfterTheWindow()
    {
        var f = NewFixture(confirmWindowFrames: 90);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();

        f.TickThrough(90);
        Assert.True(f.Manager.HasPendingConstruct);
        Assert.Equal(0, f.Count(AiBaseManager.FoundationTimeoutCounter));

        f.TickThrough(91);
        Assert.False(f.Manager.HasPendingConstruct);
        Assert.Equal(1, f.Count(AiBaseManager.FoundationTimeoutCounter));
        Assert.Equal(0, f.Count(AiBaseManager.FoundationOkCounter));
    }

    [Fact]
    public void AVanishedPlot_ResolvesThePendingConstructAsLost()
    {
        var f = NewFixture(confirmWindowFrames: 1_000);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();

        f.World.PlotList.Clear();
        f.TickThrough(1);

        Assert.False(f.Manager.HasPendingConstruct);
        Assert.Equal(1, f.Count(AiBaseManager.FoundationLostCounter));
        Assert.Equal(0, f.Count(AiBaseManager.FoundationOkCounter));
    }

    // ---- cooldown between builds -----------------------------------------------------------

    [Fact]
    public void AfterAConfirmedBuild_TheCooldownLapsesOnTheFrameAfterTheWindow()
    {
        var f = NewFixture(buildCooldownFrames: 30);
        f.World.PlotList.Add(Plot(4));
        f.World.PlotList.Add(Plot(5));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        Assert.Single(f.Orders);

        // Confirm on frame 1, which starts a 30-frame cooldown ending inclusively at frame 31.
        f.World.PlotList[0] = Plot(4, occupied: true, occupantId: 80);
        f.TickThrough(1);
        Assert.Equal(1, f.Count(AiBaseManager.FoundationOkCounter));

        f.TickThrough(31);
        Assert.Single(f.Orders);

        f.TickThrough(32);
        Assert.Equal(2, f.Orders.Count);
        Assert.Equal(new ObjectId(5), f.Orders[1].Arguments[0].Value.ObjectId);
    }

    [Fact]
    public void ADestroyedStructure_FreesItsPlot_AndTheBaseIsRebuilt()
    {
        var f = NewFixture(buildCooldownFrames: 2);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        f.World.PlotList[0] = Plot(4, occupied: true, occupantId: 80);
        f.TickThrough(1);
        Assert.Equal(1, f.Count(AiBaseManager.FoundationOkCounter));
        Assert.Single(f.Orders);

        // The farm dies; the plot is free again in the next snapshot. There is no rebuild path -
        // the ordinary fill loop picks it back up once the cooldown lapses.
        f.World.PlotList[0] = Plot(4);
        f.TickThrough(4);

        Assert.Equal(2, f.Orders.Count);
        Assert.Equal(new ObjectId(4), f.Orders[1].Arguments[0].Value.ObjectId);
    }

    // ---- affordability ---------------------------------------------------------------------

    [Fact]
    public void AnUnaffordableTemplate_EmitsNothing_AndSaysWhy()
    {
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm(cost: 500));
        f.World.Money = 499;

        f.Tick();

        Assert.Empty(f.Orders);
        Assert.Contains(f.TraceSink.Lines, l => l.Contains("base f=0 wait", StringComparison.Ordinal));
        Assert.Contains(f.TraceSink.Lines, l => l.Contains("money=499", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheMoneyArrives_TheBuildGoesOut()
    {
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm(cost: 500));
        f.World.Money = 499;

        f.Tick();
        Assert.Empty(f.Orders);

        f.World.Money = 500;
        f.TickThrough(31);

        Assert.Single(f.Orders);
    }

    [Fact]
    public void TheEconomyManagersReserve_IsTheAffordPolicy()
    {
        // Poor mood holds back 25% (AiEconomyManager.PoorReservePercent), so 1000 money is only
        // 750 spendable and a 800-cost farm must NOT go out - even though Money >= cost.
        var f = NewFixture(withEconomy: true);
        f.World.AIData = MakeAiData(poor: 2_000, wealthy: 10_000);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm(cost: 800));
        f.World.Money = 1_000;

        f.Tick();

        Assert.Equal(EconomyClassification.Poor, f.Economy!.SpendPlan.Classification);
        Assert.Equal(750, f.Economy.SpendPlan.Available);
        Assert.Empty(f.Orders);
    }

    private static AIData MakeAiData(int poor, int wealthy)
    {
        var data = new AIData();
        SetPrivate(data, nameof(AIData.Poor), poor);
        SetPrivate(data, nameof(AIData.Wealthy), wealthy);
        return data;
    }

    // ---- degenerate worlds ------------------------------------------------------------------

    [Fact]
    public void NoPlotsAndNoTemplates_IsIdle_NotACrash()
    {
        var f = NewFixture();
        f.World.Money = 100_000;

        f.Tick();
        f.TickThrough(120);

        Assert.Empty(f.Orders);
        Assert.Contains(f.TraceSink.Lines, l => l.Contains("idle plots=0 templates=0", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPlotOccupied_IsIdle()
    {
        var f = NewFixture();
        f.World.PlotList.Add(Plot(4, occupied: true, occupantId: 80));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();

        Assert.Empty(f.Orders);
    }

    [Fact]
    public void AMalformedTemplateId_IsCountedRejected_AndNotRetriedImmediately()
    {
        // InternalId starts at 1, so 0 can only come from bad data - the emitter refuses it and
        // the manager must take its cooldown rather than spin on the same arguments.
        var f = NewFixture(buildCooldownFrames: 30);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(new AiBuildableTemplate(0, "BrokenFarm", 100, AiStructureRole.Economy));
        f.World.Money = 100_000;

        f.Tick();
        f.TickThrough(10);

        Assert.Empty(f.Orders);
        Assert.Equal(1, f.Count(AiBaseManager.FoundationRejectedCounter));
    }

    [Fact]
    public void DisableBaseBuilding_StopsTheManagerDead()
    {
        var f = NewFixture();
        f.World.SkirmishAIData = MakeSkirmishAiData(disableBaseBuilding: true);
        f.World.PlotList.Add(PackedCastle(5));
        f.World.PlotList.Add(Plot(6));
        f.World.Buildable.Add(Farm());
        f.World.Money = 100_000;

        f.Tick();
        f.TickThrough(200);

        Assert.Empty(f.Orders);
        Assert.Equal(0, f.Count(AiBaseManager.FoundationOkCounter));

        // Reported exactly once, not every frame.
        var reports = 0;
        foreach (var line in f.TraceSink.Lines)
        {
            if (line.Contains("disabled=databasebuilding", StringComparison.Ordinal))
            {
                reports++;
            }
        }

        Assert.Equal(1, reports);
    }

    [Fact]
    public void WithNoEconomyManager_TheManagerStillBuilds()
    {
        // A brain assembled without an economy manager must fall back to a plain money check,
        // not to "never affords anything".
        var f = NewFixture(withEconomy: false);
        f.World.PlotList.Add(Plot(4));
        f.World.Buildable.Add(Farm(cost: 300));
        f.World.Money = 300;

        f.Tick();

        Assert.Single(f.Orders);
    }

    // ---- brain wiring -----------------------------------------------------------------------

    [Fact]
    public void TheBrainRunsTheEmitterBeforeTheBaseManager()
    {
        // Registration order is tick order and the emitter must roll the frame budget ahead of
        // any manager that spends it.
        var f = NewFixture(withEconomy: true);

        var emitterIndex = -1;
        var baseIndex = -1;

        for (var i = 0; i < f.Brain.Managers.Count; i++)
        {
            if (ReferenceEquals(f.Brain.Managers[i], f.Emitter))
            {
                emitterIndex = i;
            }

            if (ReferenceEquals(f.Brain.Managers[i], f.Manager))
            {
                baseIndex = i;
            }
        }

        Assert.True(emitterIndex >= 0);
        Assert.True(baseIndex > emitterIndex);
    }
}

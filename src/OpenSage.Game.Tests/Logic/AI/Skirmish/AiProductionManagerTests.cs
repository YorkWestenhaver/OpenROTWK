#nullable enable

// S9-08 (R15 L3) gate tests: AiProductionManager v1 - the production half of the dr-0039 M-c
// criterion.
//
// Same discipline as AiBaseManagerTests: no game, no INI files, no map. Everything runs off a
// hand-set FakeAiWorldView, a RecordingOrderSink and a RecordingAiTraceSink.
//
// WHAT THESE TESTS EXIST TO PIN
//
// 1. M-c production evidence is CONFIRMED, not optimistic: prod.unit.ok is bumped only when a
//    later snapshot shows the targeted producer's queue GROWN, never at emission time.
// 2. The unit cap counts ORDERABLE units - a ten-member horde is ONE unit, not eleven. Counting
//    members would stall production after two hordes on the default cap.
// 3. Every wait is an N-frame window that lapses on the (N+1)th frame (the round's T+1
//    convention), asserted on both sides of the boundary.
// 4. The producer choice is independent of the order the snapshot arrived in.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Logic.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiProductionManagerTests
{
    /// <summary>
    /// Big enough that no frame any test below reaches is a multiple of it, so the spine's
    /// heartbeat never interleaves with the assertions on trace content.
    /// </summary>
    private const uint NoHeartbeat = 1_000_003;

    private const int PlayerIndex = 2;

    private sealed class Fixture
    {
        public required FakeAiWorldView World { get; init; }

        public required RecordingOrderSink Sink { get; init; }

        public required RecordingAiTraceSink TraceSink { get; init; }

        public required SkirmishAIBrain Brain { get; init; }

        public required AiOrderEmitter Emitter { get; init; }

        public AiEconomyManager? Economy { get; init; }

        public required AiProductionManager Manager { get; init; }

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
        int unitCap = AiProductionPlan.DefaultUnitCap,
        uint queueCooldownFrames = AiProductionManager.DefaultQueueCooldownFrames,
        uint confirmWindowFrames = AiProductionManager.DefaultConfirmWindowFrames,
        float rallyDistance = AiProductionManager.DefaultRallyDistance)
    {
        var world = new FakeAiWorldView { PlayerIndex = PlayerIndex, Money = 10_000 };
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

        var manager = new AiProductionManager(
            emitter,
            economy,
            unitCap,
            queueCooldownFrames,
            confirmWindowFrames,
            rallyDistance);

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

    private static AiTrainableUnit Orc(int defId = 7, string name = "MordorFighterHorde", int cost = 100, bool horde = true)
        => new(defId, name, cost, horde);

    private static AiProducerView Producer(
        uint id,
        bool canEnqueue = true,
        int queueLength = 0,
        IReadOnlyList<AiTrainableUnit>? trainable = null,
        Vector3 position = default)
        => new(new ObjectId(id), "MordorOrcPit", position, canEnqueue, queueLength, trainable ?? new[] { Orc() });

    private static AiObjectView Unit(uint id, bool isHorde = false, bool isHordeMember = false)
        => new(new ObjectId(id), "MordorFighter", Vector3.Zero, PlayerIndex, false, false, 1.0f, isHorde, isHordeMember);

    private static AiObjectView Enemy(uint id, Vector3 position)
        => new(new ObjectId(id), "GondorFighter", position, 3, false, false, 1.0f);

    private static SkirmishAIData MakeSkirmishAiData(bool disableUnitBuilding)
    {
        var data = new SkirmishAIData();
        SetPrivate(data, nameof(SkirmishAIData.DisableUnitBuilding), disableUnitBuilding);
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

    // ---- identity ---------------------------------------------------------------------------

    [Fact]
    public void Name_IsProd()
    {
        Assert.Equal("prod", AiProductionManager.ManagerName);
        Assert.Equal("prod", NewFixture().Manager.Name);
    }

    [Fact]
    public void GradingCounterNames_AreStable()
    {
        // S9-11 reads these by name off the trace. Renaming one silently un-grades M-c.
        Assert.Equal("prod.unit.ok", AiProductionManager.UnitConfirmedCounter);
        Assert.Equal("prod.unit.queued", AiProductionManager.UnitQueuedCounter);
    }

    // ---- the queue path ---------------------------------------------------------------------

    [Fact]
    public void QueuesOneUnit_AsASelectionPair_AtTheLowestIdProducer()
    {
        var fixture = NewFixture();
        fixture.World.ProducerList.Add(Producer(9));
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();

        Assert.Equal(2, fixture.Orders.Count);
        Assert.Equal(OrderType.SetSelection, fixture.Orders[0].OrderType);
        Assert.Equal(OrderType.CreateUnit, fixture.Orders[1].OrderType);

        Assert.Equal(1, fixture.Manager.UnitsQueued);
        Assert.Equal(1, fixture.Count(AiProductionManager.UnitQueuedCounter));
        Assert.True(fixture.Manager.HasPendingQueue);
        Assert.Equal(new ObjectId(4), fixture.Manager.PendingProducerId);
    }

    [Fact]
    public void ProducerChoice_IsIndependentOfSnapshotOrder()
    {
        // The live view sorts by id, but a manager that RELIED on that would break the day
        // something re-orders the snapshot. Both orderings must pick producer 4.
        foreach (var ascending in new[] { true, false })
        {
            var fixture = NewFixture();
            var ids = ascending ? new uint[] { 4, 7, 9 } : new uint[] { 9, 7, 4 };

            foreach (var id in ids)
            {
                fixture.World.ProducerList.Add(Producer(id));
            }

            fixture.Tick();

            Assert.Equal(new ObjectId(4), fixture.Manager.PendingProducerId);
        }
    }

    [Fact]
    public void QueuesNothing_WhenEveryProducerIsFull()
    {
        var fixture = NewFixture();
        fixture.World.ProducerList.Add(Producer(4, canEnqueue: false, queueLength: 3));

        fixture.Tick();

        Assert.Empty(fixture.Orders);
        Assert.Equal(0, fixture.Manager.UnitsQueued);
    }

    [Fact]
    public void QueuesTheCheapestAffordableUnit()
    {
        var fixture = NewFixture();
        fixture.World.Money = 250;
        fixture.World.ProducerList.Add(Producer(4, trainable: new[]
        {
            Orc(defId: 30, name: "MordorTrollHorde", cost: 800),
            Orc(defId: 20, name: "MordorArcherHorde", cost: 200),
            Orc(defId: 10, name: "MordorFighterHorde", cost: 100),
        }));

        fixture.Tick();

        Assert.Equal("MordorFighterHorde", fixture.Manager.PendingTemplateName);
        Assert.Equal(10, fixture.Orders[1].Arguments[0].Value.Integer);
    }

    [Fact]
    public void QueuesNothing_WhenNothingIsAffordable()
    {
        var fixture = NewFixture();
        fixture.World.Money = 50;
        fixture.World.ProducerList.Add(Producer(4, trainable: new[] { Orc(cost: 500) }));

        fixture.Tick();

        Assert.Empty(fixture.Orders);
        Assert.False(fixture.Manager.HasPendingQueue);
    }

    // ---- confirmation (the M-c evidence) ------------------------------------------------------

    [Fact]
    public void McCounter_IsNotBumped_OnEmissionAlone()
    {
        // The whole point of the confirmation machine: sending the order proves nothing, because
        // OrderProcessor tells the sender nothing back.
        var fixture = NewFixture();
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();

        Assert.Equal(1, fixture.Count(AiProductionManager.UnitQueuedCounter));
        Assert.Equal(0, fixture.Count(AiProductionManager.UnitConfirmedCounter));
        Assert.Equal(0, fixture.Manager.UnitsConfirmed);
    }

    [Fact]
    public void McCounter_IsBumped_WhenTheProducerQueueGrows()
    {
        var fixture = NewFixture();
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();
        Assert.True(fixture.Manager.HasPendingQueue);

        // The sim accepted the order: the queue now holds the entry.
        fixture.World.ProducerList[0] = Producer(4, queueLength: 1);
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(1, fixture.Count(AiProductionManager.UnitConfirmedCounter));
        Assert.Equal(1, fixture.Manager.UnitsConfirmed);
        Assert.False(fixture.Manager.HasPendingQueue);
    }

    [Fact]
    public void PendingQueue_TimesOut_OnTheFramePastTheWindow()
    {
        var fixture = NewFixture(confirmWindowFrames: 2);
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();
        Assert.True(fixture.Manager.HasPendingQueue);

        // T+1 convention: a 2-frame window is still open on frame 2.
        fixture.TickThrough(2);
        Assert.True(fixture.Manager.HasPendingQueue);
        Assert.Equal(0, fixture.Count(AiProductionManager.UnitTimeoutCounter));

        fixture.TickThrough(3);
        Assert.False(fixture.Manager.HasPendingQueue);
        Assert.Equal(1, fixture.Count(AiProductionManager.UnitTimeoutCounter));
    }

    [Fact]
    public void PendingQueue_IsLost_WhenTheProducerDies()
    {
        var fixture = NewFixture();
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();

        fixture.World.ProducerList.Clear();
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(1, fixture.Count(AiProductionManager.UnitLostCounter));
        Assert.False(fixture.Manager.HasPendingQueue);
    }

    [Fact]
    public void SecondUnit_WaitsForTheCooldown_AfterConfirmation()
    {
        var fixture = NewFixture(queueCooldownFrames: 4);
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();
        fixture.World.ProducerList[0] = Producer(4, queueLength: 1);
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(1, fixture.Manager.UnitsConfirmed);
        var ordersAfterConfirm = fixture.Orders.Count;

        // Confirmed on frame 1, cooldown 4 -> gate is closed through frame 5.
        fixture.TickThrough(5);
        Assert.Equal(ordersAfterConfirm, fixture.Orders.Count);

        fixture.TickThrough(6);
        Assert.Equal(ordersAfterConfirm + 2, fixture.Orders.Count);
        Assert.Equal(2, fixture.Manager.UnitsQueued);
    }

    // ---- the unit budget ----------------------------------------------------------------------

    [Fact]
    public void Budget_CountsAHordeAsOneUnit_NeverItsMembers()
    {
        // THE horde rule, on the production side: a ten-orc horde is one unit against the cap.
        // Counting members would stall production after two hordes on the default cap.
        var own = new List<AiObjectView> { Unit(1, isHorde: true) };
        for (uint member = 2; member <= 11; member++)
        {
            own.Add(Unit(member, isHordeMember: true));
        }

        Assert.Equal(1, AiProductionPlan.CountOrderableUnits(own));
    }

    [Fact]
    public void Budget_StopsProduction_AtTheCap()
    {
        var fixture = NewFixture(unitCap: 2);
        fixture.World.ProducerList.Add(Producer(4));
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));

        fixture.Tick();

        Assert.Empty(fixture.Orders);
        Assert.False(fixture.Manager.Budget.AllowsMore);
        Assert.Equal(2, fixture.Manager.Budget.UnitCount);
        Assert.Equal(0, fixture.Manager.Budget.Headroom);
    }

    [Fact]
    public void Budget_CountsQueuedEntriesAsCommitted()
    {
        var fixture = NewFixture(unitCap: 2);
        fixture.World.ProducerList.Add(Producer(4, queueLength: 2));
        fixture.World.Own.Add(Unit(1));

        fixture.Tick();

        Assert.Empty(fixture.Orders);
        Assert.Equal(2, fixture.Manager.Budget.InFlight);
        Assert.Equal(3, fixture.Manager.Budget.Committed);
        Assert.False(fixture.Manager.Budget.AllowsMore);
    }

    [Fact]
    public void Budget_HeadroomNeverGoesNegative()
        => Assert.Equal(0, new UnitBudget(10, 5, 3).Headroom);

    // ---- rally points ---------------------------------------------------------------------------

    [Fact]
    public void RallyPoint_IsSetOncePerProducer_TowardsTheEnemy()
    {
        var fixture = NewFixture(rallyDistance: 120.0f);
        fixture.World.ProducerList.Add(Producer(4, position: Vector3.Zero));
        fixture.World.Enemy.Add(Enemy(50, new Vector3(1000, 0, 0)));

        fixture.Tick();

        Assert.Equal(2, fixture.Orders.Count);
        Assert.Equal(OrderType.SetSelection, fixture.Orders[0].OrderType);
        Assert.Equal(OrderType.SetRallyPoint, fixture.Orders[1].OrderType);
        Assert.Equal(1, fixture.Manager.RallyPointsSet);
        Assert.Equal(1, fixture.Count(AiProductionManager.RallyPointCounter));

        // Second frame: the rally point is done, so the frame's action is a queue instead - and
        // no further SetRallyPoint is ever emitted for this producer.
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(1, fixture.Manager.RallyPointsSet);
        Assert.Equal(1, fixture.Manager.UnitsQueued);
    }

    [Fact]
    public void RallyPoint_IsSkipped_WhileNoEnemyIsKnown()
    {
        // No direction to face: the manager must not invent one, and must not stall production
        // waiting for one either.
        var fixture = NewFixture();
        fixture.World.ProducerList.Add(Producer(4));

        fixture.Tick();

        Assert.Equal(0, fixture.Manager.RallyPointsSet);
        Assert.Equal(1, fixture.Manager.UnitsQueued);
    }

    [Fact]
    public void RallyPoint_NeverOvershootsTheTarget()
    {
        var rally = AiProductionPlan.RallyPoint(Vector3.Zero, new Vector3(10, 0, 0), 120.0f);

        Assert.NotNull(rally);
        Assert.Equal(new Vector3(10, 0, 0), rally!.Value);
    }

    // ---- mod off switch ---------------------------------------------------------------------------

    [Fact]
    public void DisableUnitBuilding_StopsTheManagerDead()
    {
        var fixture = NewFixture();
        fixture.World.SkirmishAIData = MakeSkirmishAiData(disableUnitBuilding: true);
        fixture.World.ProducerList.Add(Producer(4));

        fixture.TickThrough(10);

        Assert.Empty(fixture.Orders);
        Assert.Equal(0, fixture.Manager.UnitsQueued);
    }

    // ---- economy integration -----------------------------------------------------------------------

    [Fact]
    public void ReservePolicy_ComesFromTheEconomyManager_WhenOneIsRegistered()
    {
        // Poor keeps 25% back, so 100 money affords at most 75.
        var fixture = NewFixture(withEconomy: true);
        fixture.World.Money = 100;
        fixture.World.AIData = MakeAiData(poor: 1000, wealthy: 5000);
        fixture.World.ProducerList.Add(Producer(4, trainable: new[] { Orc(cost: 80) }));

        fixture.Tick();

        Assert.Empty(fixture.Orders);

        // 70 clears the reserve; 80 did not. The idle gate closed for
        // AiProductionManager.DefaultIdleCooldownFrames after the refusal, so re-ask past it.
        fixture.World.ProducerList[0] = Producer(4, trainable: new[] { Orc(cost: 70) });
        fixture.TickThrough(AiProductionManager.DefaultIdleCooldownFrames + 1);

        Assert.Equal(2, fixture.Orders.Count);
    }

    private static AIData MakeAiData(int poor, int wealthy)
    {
        var data = new AIData();
        SetPrivate(data, nameof(AIData.Poor), poor);
        SetPrivate(data, nameof(AIData.Wealthy), wealthy);
        return data;
    }
}

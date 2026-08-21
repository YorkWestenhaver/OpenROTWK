#nullable enable

// S9-01 (R15 L3) gate tests: the skirmish AI brain spine.
//
// Every test here runs with NO game: no IGame, no GraphicsDevice, no INI data, no map. That is
// the packet's central claim - IAiWorldView and IAiOrderSink are the brain's only contact with
// the world, so the whole AI is testable off a struct-filled fake. If a later change makes
// these tests need a Game fixture, the seam has been breached.
//
// The heartbeat format assertions are deliberately exact-string. The dr-0039 guard grades M-a
// off this text and S9-02's report schema v1 parses it; a "close enough" assertion here would
// let a field rename through and break the grade silently at the R1 gate instead of loudly at
// the merge.

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Logic.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class SkirmishAIBrainSpineTests
{
    /// <summary>A manager that records when it ran and can emit orders/trace on cue.</summary>
    private sealed class SpyManager : IAiBrainManager
    {
        private readonly List<string> _log;
        private readonly int _ordersPerTick;

        public string Name { get; }

        public int Updates { get; private set; }

        /// <summary>Heartbeats the trace had already emitted the last time this manager ran.</summary>
        public int HeartbeatsSeenOnLastUpdate { get; private set; }

        public SpyManager(string name, List<string> log, int ordersPerTick = 0)
        {
            Name = name;
            _log = log;
            _ordersPerTick = ordersPerTick;
        }

        public void Update(SkirmishAIBrain brain)
        {
            Updates++;
            HeartbeatsSeenOnLastUpdate = brain.Trace.HeartbeatsEmitted;
            _log.Add(Name);

            for (var i = 0; i < _ordersPerTick; i++)
            {
                var order = new Order(brain.PlayerIndex, OrderType.MoveTo);
                order.AddIntegerArgument(i);
                brain.Orders.Submit(order);
            }
        }
    }

    private static AiObjectView Structure(uint id, string template, int owner) =>
        new(new ObjectId(id), template, Vector3.Zero, owner, IsStructure: true, IsUnderConstruction: false, HealthFraction: 1.0f);

    private static (SkirmishAIBrain Brain, FakeAiWorldView World, RecordingOrderSink Sink, RecordingAiTraceSink Trace)
        NewBrain(int playerIndex = 0, uint heartbeatInterval = SkirmishAIBrain.DefaultHeartbeatInterval)
    {
        var world = new FakeAiWorldView { PlayerIndex = playerIndex };
        var sink = new RecordingOrderSink();
        var traceSink = new RecordingAiTraceSink();
        var brain = new SkirmishAIBrain(world, sink, new AiTrace(playerIndex, traceSink), heartbeatInterval);
        return (brain, world, sink, traceSink);
    }

    // ---- heartbeat: the graded evidence line ----

    [Fact]
    public void Heartbeat_HasTheExactFormatTheMatchReportParses()
    {
        var (brain, world, _, traceSink) = NewBrain(playerIndex: 2);

        world.CurrentFrame = 30;
        world.Money = 1500;
        world.Own.Add(Structure(1, "MordorSlaughterHouse", 2));
        world.Own.Add(Structure(2, "MordorFortress", 2));
        world.Enemy.Add(Structure(3, "GondorCastleKeep", 1));

        brain.Update();

        Assert.Equal(new[] { "[AI p2] hb f=30 money=1500 own=2 enemy=1 mgr=0" }, traceSink.Lines);
    }

    [Fact]
    public void Heartbeat_ReportsTheRegisteredManagerCount()
    {
        var (brain, world, _, traceSink) = NewBrain(playerIndex: 0);
        var log = new List<string>();

        brain.RegisterManager(new SpyManager("econ", log));
        brain.RegisterManager(new SpyManager("base", log));

        world.CurrentFrame = 0;
        brain.Update();

        Assert.Equal(new[] { "[AI p0] hb f=0 money=0 own=0 enemy=0 mgr=2" }, traceSink.Lines);
    }

    [Fact]
    public void Heartbeat_FiresOnlyOnMultiplesOfTheInterval()
    {
        var (brain, world, _, traceSink) = NewBrain(playerIndex: 1, heartbeatInterval: 4);

        // Frames 0..8 inclusive: 9 ticks, heartbeats expected at 0, 4 and 8.
        for (var frame = 0; frame <= 8; frame++)
        {
            world.CurrentFrame = (uint)frame;
            brain.Update();
        }

        Assert.Equal(3, brain.Trace.HeartbeatsEmitted);
        Assert.Equal(
            new[]
            {
                "[AI p1] hb f=0 money=0 own=0 enemy=0 mgr=0",
                "[AI p1] hb f=4 money=0 own=0 enemy=0 mgr=0",
                "[AI p1] hb f=8 money=0 own=0 enemy=0 mgr=0",
            },
            traceSink.Lines);
    }

    [Fact]
    public void Heartbeat_TracksRisingMoney_TheMaEvidence()
    {
        // M-a is graded as "heartbeats appear AND money rises", so the heartbeat must read
        // money live from the world view rather than caching it at construction.
        var (brain, world, _, traceSink) = NewBrain(playerIndex: 0, heartbeatInterval: 1);

        world.CurrentFrame = 1;
        world.Money = 100;
        brain.Update();

        world.CurrentFrame = 2;
        world.Money = 250;
        brain.Update();

        Assert.Equal(
            new[]
            {
                "[AI p0] hb f=1 money=100 own=0 enemy=0 mgr=0",
                "[AI p0] hb f=2 money=250 own=0 enemy=0 mgr=0",
            },
            traceSink.Lines);
    }

    [Fact]
    public void Heartbeat_RunsBeforeManagers_SoAThrowingManagerStillLeavesEvidence()
    {
        var (brain, world, _, _) = NewBrain(playerIndex: 0, heartbeatInterval: 1);
        var log = new List<string>();
        var spy = new SpyManager("econ", log);
        brain.RegisterManager(spy);

        world.CurrentFrame = 1;
        brain.Update();

        Assert.Equal(1, spy.HeartbeatsSeenOnLastUpdate);
    }

    // ---- manager registration and tick order ----

    [Fact]
    public void Managers_RunOncePerTickInRegistrationOrder()
    {
        var (brain, world, _, _) = NewBrain();
        var log = new List<string>();

        brain.RegisterManager(new SpyManager("econ", log));
        brain.RegisterManager(new SpyManager("base", log));
        brain.RegisterManager(new SpyManager("attack", log));

        world.CurrentFrame = 1;
        brain.Update();
        world.CurrentFrame = 2;
        brain.Update();

        Assert.Equal(new[] { "econ", "base", "attack", "econ", "base", "attack" }, log);
        Assert.Equal(2u, brain.TicksRun);
    }

    [Fact]
    public void RegisterManager_RejectsTheSameInstanceTwice()
    {
        var (brain, _, _, _) = NewBrain();
        var manager = new SpyManager("econ", new List<string>());

        brain.RegisterManager(manager);

        var ex = Assert.Throws<InvalidOperationException>(() => brain.RegisterManager(manager));
        Assert.Contains("econ", ex.Message);
        Assert.Single(brain.Managers);
    }

    [Fact]
    public void GetManager_FindsARegisteredManagerByType()
    {
        var (brain, _, _, _) = NewBrain();
        var manager = new SpyManager("econ", new List<string>());
        brain.RegisterManager(manager);

        Assert.Same(manager, brain.GetManager<SpyManager>());
    }

    [Fact]
    public void Constructor_RejectsAZeroHeartbeatInterval()
    {
        var world = new FakeAiWorldView();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SkirmishAIBrain(world, new RecordingOrderSink(), heartbeatInterval: 0));
    }

    [Fact]
    public void Brain_TakesItsPlayerIndexFromTheWorldView()
    {
        var (brain, _, _, _) = NewBrain(playerIndex: 5);

        Assert.Equal(5, brain.PlayerIndex);
        Assert.Equal("[AI p5] ", brain.Trace.Prefix);
    }

    // ---- order sink ----

    [Fact]
    public void Orders_ReachTheSinkInSubmissionOrderWithTheBrainsPlayerIndex()
    {
        var (brain, world, sink, _) = NewBrain(playerIndex: 3, heartbeatInterval: 1);
        brain.RegisterManager(new SpyManager("econ", new List<string>(), ordersPerTick: 2));

        world.CurrentFrame = 1;
        brain.Update();

        Assert.Equal(2, sink.Count);
        Assert.All(sink.Orders, o => Assert.Equal(3, o.PlayerIndex));
        Assert.Equal(0, sink.Orders[0].Arguments[0].Value.Integer);
        Assert.Equal(1, sink.Orders[1].Arguments[0].Value.Integer);
    }

    [Fact]
    public void Brain_EmitsNoOrdersWithNoManagers()
    {
        // The spine ships behaviour-free: an unregistered brain must be inert, so that a
        // regression in a later manager cannot hide behind orders the spine emitted itself.
        var (brain, world, sink, _) = NewBrain(heartbeatInterval: 1);

        for (var frame = 0u; frame < 10; frame++)
        {
            world.CurrentFrame = frame;
            brain.Update();
        }

        Assert.Equal(0, sink.Count);
        Assert.Equal(10, brain.Trace.LinesEmitted);
    }

    // ---- ascending player index ----

    [Fact]
    public void Brains_TickedInAscendingIndexInterleaveTheirTraceInThatOrder()
    {
        // PlayerManager.LogicTick walks players by ascending index; this is that contract
        // expressed at the level the spine can assert without a game. The shared sink shows the
        // per-frame ordering that a match log will show.
        var traceSink = new RecordingAiTraceSink();
        var world0 = new FakeAiWorldView { PlayerIndex = 0 };
        var world1 = new FakeAiWorldView { PlayerIndex = 1 };
        var world2 = new FakeAiWorldView { PlayerIndex = 2 };

        var brains = new[]
        {
            new SkirmishAIBrain(world0, new RecordingOrderSink(), new AiTrace(0, traceSink), 1),
            new SkirmishAIBrain(world1, new RecordingOrderSink(), new AiTrace(1, traceSink), 1),
            new SkirmishAIBrain(world2, new RecordingOrderSink(), new AiTrace(2, traceSink), 1),
        };

        foreach (var world in new[] { world0, world1, world2 })
        {
            world.CurrentFrame = 7;
        }

        for (var i = 0; i < brains.Length; i++)
        {
            brains[i].Update();
        }

        Assert.Equal(
            new[]
            {
                "[AI p0] hb f=7 money=0 own=0 enemy=0 mgr=0",
                "[AI p1] hb f=7 money=0 own=0 enemy=0 mgr=0",
                "[AI p2] hb f=7 money=0 own=0 enemy=0 mgr=0",
            },
            traceSink.Lines);
    }

    // ---- AiTrace counters (the machine-readable half of S9-02's report) ----

    [Fact]
    public void Counters_AccumulateAndReadBackByName()
    {
        var trace = new AiTrace(0, new RecordingAiTraceSink());

        trace.Count("base.foundation.ok");
        trace.Count("base.foundation.ok");
        trace.Count("base.foundation.rejected", 3);

        Assert.Equal(2, trace.GetCount("base.foundation.ok"));
        Assert.Equal(3, trace.GetCount("base.foundation.rejected"));
        Assert.Equal(0, trace.GetCount("never.bumped"));
    }

    [Fact]
    public void Counters_EnumerateInOrdinalNameOrder_SoReportsAreByteStable()
    {
        var trace = new AiTrace(0, new RecordingAiTraceSink());

        trace.Count("zulu");
        trace.Count("alpha");
        trace.Count("Mike");

        var names = new List<string>();
        foreach (var pair in trace.Counters)
        {
            names.Add(pair.Key);
        }

        // Ordinal, so uppercase sorts before lowercase - deterministic across cultures.
        Assert.Equal(new[] { "Mike", "alpha", "zulu" }, names);
    }

    [Fact]
    public void TraceLine_CarriesThePlayerPrefixAndCategory()
    {
        var traceSink = new RecordingAiTraceSink();
        var trace = new AiTrace(4, traceSink);

        trace.Line("econ", "spend=500 plan=farm");

        Assert.Equal(new[] { "[AI p4] econ spend=500 plan=farm" }, traceSink.Lines);
        Assert.Equal(1, trace.LinesEmitted);
        Assert.Equal(0, trace.HeartbeatsEmitted);
    }

    [Fact]
    public void Trace_DefaultsToTheLoggingSinkWithoutThrowing()
    {
        // The live brain constructs its own AiTrace with no sink argument; make sure that path
        // does not depend on NLog being configured in a particular way.
        var trace = new AiTrace(0);

        trace.Heartbeat(0, 0, 0, 0, 0);

        Assert.Equal(1, trace.LinesEmitted);
    }
}

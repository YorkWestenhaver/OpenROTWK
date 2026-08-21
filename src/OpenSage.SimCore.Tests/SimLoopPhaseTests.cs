// Gate tests for scaffolding step 4 (api-freeze-v1 §6 build order): the frozen SimPhase
// sequence, the 5 Hz constants, the checkpoint cadence gate, the +2-frame order schedule,
// deterministic dispatch order, wire quantization at ingestion, and the recovered
// GameMessageType vocabulary pins.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class SimLoopPhaseTests
{
    private sealed class RecordingSystems : ISimSystems, ISimPhaseObserver
    {
        public readonly List<(SimPhase Phase, uint Frame)> Phases = new();
        public readonly List<(string Call, uint Frame)> Calls = new();
        public readonly List<ScheduledOrder> Dispatched = new();
        public Action<SimLoop, LogicFrame> OnIngest;
        public SimLoop Loop;

        public void OnPhase(SimPhase phase, LogicFrame frame) => Phases.Add((phase, frame.Value));

        public void IngestOrders(LogicFrame frame)
        {
            Calls.Add(("Ingest", frame.Value));
            OnIngest?.Invoke(Loop!, frame);
        }

        public void DispatchOrder(in ScheduledOrder order)
        {
            Calls.Add(("Dispatch", order.Frame.Value));
            Dispatched.Add(order);
        }

        public void ModuleUpdate(LogicFrame frame) => Calls.Add(("Module", frame.Value));

        public void PartitionUpdate(LogicFrame frame) => Calls.Add(("Partition", frame.Value));

        public void CrcCheckpoint(LogicFrame frame) => Calls.Add(("Crc", frame.Value));
    }

    private static (SimLoop, RecordingSystems) CreateLoop()
    {
        var systems = new RecordingSystems();
        var loop = new SimLoop(systems, systems);
        systems.Loop = loop;
        return (loop, systems);
    }

    // ------------------------------------------------------------------ frozen constants

    [Fact]
    public void LogicRateIsFiveHertz()
    {
        // In-binary: LOGICFRAMES_PER_SECOND = 5, per the written behavioral spec (crc-byteorder §3.2).
        Assert.Equal(5, SimLoop.LogicFramesPerSecond);
        Assert.Equal(200, SimLoop.MsPerLogicFrame);
    }

    [Fact]
    public void OrderSchedulingOffsetIsTwoFrames()
    {
        Assert.Equal(2, SimLoop.OrderSchedulingOffsetInFrames);
        Assert.Equal(2, OrderIngest.OrderSchedulingOffsetInFrames);
    }

    [Fact]
    public void PhaseSequenceIsFrozen()
    {
        // The sequence is a contract (F6): IngestOrders -> DispatchOrders -> ModuleUpdate ->
        // PartitionUpdate -> CrcCheckpoint -> EndFrame, with these exact ordinals. Any change
        // here is a netplay protocol-version bump, not a refactor.
        Assert.Equal(
            new[]
            {
                SimPhase.IngestOrders,
                SimPhase.DispatchOrders,
                SimPhase.ModuleUpdate,
                SimPhase.PartitionUpdate,
                SimPhase.CrcCheckpoint,
                SimPhase.EndFrame,
            },
            SimLoop.PhaseSequence.ToArray());

        Assert.Equal(0, (byte)SimPhase.IngestOrders);
        Assert.Equal(1, (byte)SimPhase.DispatchOrders);
        Assert.Equal(2, (byte)SimPhase.ModuleUpdate);
        Assert.Equal(3, (byte)SimPhase.PartitionUpdate);
        Assert.Equal(4, (byte)SimPhase.CrcCheckpoint);
        Assert.Equal(5, (byte)SimPhase.EndFrame);
        Assert.Equal(6, Enum.GetValues<SimPhase>().Length);
    }

    // ------------------------------------------------------------------ phase-sequence gate

    [Fact]
    public void AdvanceRunsEveryPhaseInFrozenOrderAndIncrementsFrame()
    {
        var (loop, systems) = CreateLoop();

        Assert.Equal(0u, loop.CurrentFrame.Value);
        loop.Advance();
        Assert.Equal(1u, loop.CurrentFrame.Value);

        Assert.Equal(SimLoop.PhaseSequence.ToArray(), systems.Phases.Select(p => p.Phase).ToArray());
        Assert.All(systems.Phases, p => Assert.Equal(0u, p.Frame));
    }

    [Fact]
    public void MultiFrameObservationRepeatsTheSequencePerFrame()
    {
        var (loop, systems) = CreateLoop();
        loop.CrcCheckpointIntervalInFrames = 1;

        for (var i = 0; i < 3; i++)
        {
            loop.Advance();
        }

        var expected = new List<(SimPhase, uint)>();
        for (var frame = 0u; frame < 3; frame++)
        {
            foreach (var phase in SimLoop.PhaseSequence)
            {
                expected.Add((phase, frame));
            }
        }

        Assert.Equal(expected, systems.Phases);
    }

    [Fact]
    public void CrcCheckpointBodyRunsOnlyOnTheIntervalCadence()
    {
        var (loop, systems) = CreateLoop();
        loop.CrcCheckpointIntervalInFrames = 3;

        for (var i = 0; i < 7; i++)
        {
            loop.Advance();
        }

        // Body ran at frames 0, 3, 6 (frame % interval == 0)...
        Assert.Equal(new uint[] { 0, 3, 6 }, systems.Calls.Where(c => c.Call == "Crc").Select(c => c.Frame).ToArray());

        // ...but the phase itself was entered every frame (the dump labels by phase).
        Assert.Equal(7, systems.Phases.Count(p => p.Phase == SimPhase.CrcCheckpoint));
    }

    [Fact]
    public void SubFrameDivisorIsPlainData()
    {
        // OPEN-3 escape hatch: settable data with a neutral default; no pipeline effect today.
        var (loop, systems) = CreateLoop();
        Assert.Equal(1u, loop.SubFrameDivisor);

        loop.SubFrameDivisor = 4;
        Assert.Equal(4u, loop.SubFrameDivisor);

        loop.Advance();
        Assert.Equal(SimLoop.PhaseSequence.ToArray(), systems.Phases.Select(p => p.Phase).ToArray());
    }

    // ------------------------------------------------------------------ order pipeline

    private static SimOrder MoveOrder(int playerIndex)
    {
        var order = new SimOrder(GameMessageType.MSG_DO_MOVETO, playerIndex);
        order.AddArgument(SimOrderArg.FromWirePosition(
            BitConverter.SingleToUInt32Bits(2542.51f),
            BitConverter.SingleToUInt32Bits(2026.70f),
            BitConverter.SingleToUInt32Bits(100.0f)));
        return order;
    }

    [Fact]
    public void LocalOrderIsDispatchedExactlyTwoFramesLater()
    {
        var (loop, systems) = CreateLoop();

        systems.OnIngest = (l, frame) =>
        {
            if (frame.Value == 1)
            {
                l.Orders.SubmitLocal(MoveOrder(playerIndex: 0), frame);
            }
        };

        for (var i = 0; i < 5; i++)
        {
            loop.Advance();
        }

        var dispatched = Assert.Single(systems.Dispatched);
        Assert.Equal(3u, dispatched.Frame.Value); // submitted during frame 1 -> executes frame 3
        Assert.Equal(0, dispatched.SubmissionIndex);
    }

    [Fact]
    public void DispatchOrderIsPlayerThenSubmissionIndexRegardlessOfArrivalOrder()
    {
        var (loop, systems) = CreateLoop();

        systems.OnIngest = (l, frame) =>
        {
            if (frame.Value != 0)
            {
                return;
            }

            // Arrival order deliberately scrambled across players and submission indices.
            var target = new LogicFrame(2);
            l.Orders.SubmitScheduled(MoveOrder(playerIndex: 2), target, submissionIndex: 0);
            l.Orders.SubmitScheduled(MoveOrder(playerIndex: 0), target, submissionIndex: 1);
            l.Orders.SubmitScheduled(MoveOrder(playerIndex: 1), target, submissionIndex: 0);
            l.Orders.SubmitScheduled(MoveOrder(playerIndex: 0), target, submissionIndex: 0);
            l.Orders.SubmitScheduled(MoveOrder(playerIndex: 1), target, submissionIndex: 1);
        };

        for (var i = 0; i < 3; i++)
        {
            loop.Advance();
        }

        Assert.Equal(
            new[] { (0, 0), (0, 1), (1, 0), (1, 1), (2, 0) },
            systems.Dispatched.Select(o => (o.PlayerIndex, o.SubmissionIndex)).ToArray());
    }

    [Fact]
    public void SubmissionIndicesCountPerPlayerPerFrame()
    {
        var ingest = new OrderIngest();
        var frame = new LogicFrame(10);

        var a0 = ingest.SubmitLocal(MoveOrder(0), frame);
        var a1 = ingest.SubmitLocal(MoveOrder(0), frame);
        var b0 = ingest.SubmitLocal(MoveOrder(1), frame);

        Assert.Equal((12u, 0), (a0.Frame.Value, a0.SubmissionIndex));
        Assert.Equal((12u, 1), (a1.Frame.Value, a1.SubmissionIndex));
        Assert.Equal((12u, 0), (b0.Frame.Value, b0.SubmissionIndex));
    }

    [Fact]
    public void UndispatchedPastFrameIsALockstepFailure()
    {
        var ingest = new OrderIngest();
        ingest.SubmitScheduled(MoveOrder(0), new LogicFrame(1), 0);

        Assert.Empty(ingest.DrainForFrame(new LogicFrame(0)));
        Assert.Throws<InvalidOperationException>(() => ingest.DrainForFrame(new LogicFrame(2)));
    }

    // ------------------------------------------------------------------ wire quantization

    [Fact]
    public void WireFloatArgumentsQuantizeThroughFromWireFloat()
    {
        // The F4 wire boundary: payload enters as IEEE bits and must equal
        // Fix64.FromWireFloat of those bits, component for component.
        var bits = BitConverter.SingleToUInt32Bits(118.147f);
        var arg = SimOrderArg.FromWireFloat(bits);

        Assert.Equal(SimOrderArgKind.Fixed, arg.Kind);
        Assert.Equal(Fix64.FromWireFloat(bits), arg.Fixed);

        var x = BitConverter.SingleToUInt32Bits(2542.51f);
        var y = BitConverter.SingleToUInt32Bits(2026.70f);
        var z = BitConverter.SingleToUInt32Bits(100.0f);
        var pos = SimOrderArg.FromWirePosition(x, y, z);

        Assert.Equal(SimOrderArgKind.Position, pos.Kind);
        Assert.Equal(Fix64.FromWireFloat(x), pos.Position.X);
        Assert.Equal(Fix64.FromWireFloat(y), pos.Position.Y);
        Assert.Equal(Fix64.FromWireFloat(z), pos.Position.Z);
    }

    [Fact]
    public void IntegerWireKindsCarryExactValues()
    {
        Assert.Equal(SimOrderArgKind.Integer, SimOrderArg.FromInteger(-7).Kind);
        Assert.Equal(-7, SimOrderArg.FromInteger(-7).Integer);
        Assert.True(SimOrderArg.FromBoolean(true).Boolean);
        Assert.Equal(658u, SimOrderArg.FromObjectId(658).ObjectId);
        Assert.Equal(0xDEADBEEFu, SimOrderArg.FromUnsigned(0xDEADBEEF).Unsigned);

        // gamemessage-enum-map §1: the 0x08 payload is one IRegion2D (x1,y1,x2,y2).
        var region = SimOrderArg.FromScreenRectangle(1723, 833, 1793, 844);
        Assert.Equal(SimOrderArgKind.ScreenRectangle, region.Kind);
        Assert.Equal((1723, 833, 1793, 844), (region.X0, region.Y0, region.X1, region.Y1));
    }

    // ------------------------------------------------------------------ vocabulary

    [Fact]
    public void GameMessageTypeVocabularyPins()
    {
        // Anchor values recovered from the binary's name-switch function
        // (gamemessage-enum-map.md §§1-2). BFME2 numbering, never Zero Hour.
        Assert.Equal(0, (int)GameMessageType.MSG_INVALID);
        Assert.Equal(29, (int)GameMessageType.MSG_CLEAR_GAME_DATA);
        Assert.Equal(1000, (int)GameMessageType.MSG_BEGIN_NETWORK_MESSAGES);
        Assert.Equal(1001, (int)GameMessageType.MSG_CREATE_SELECTED_GROUP);
        Assert.Equal(1004, (int)GameMessageType.MSG_DESTROY_SELECTED_GROUP);
        Assert.Equal(1050, (int)GameMessageType.MSG_DOZER_CONSTRUCT);
        Assert.Equal(1060, (int)GameMessageType.MSG_AREA_SELECTION);
        Assert.Equal(1071, (int)GameMessageType.MSG_DO_MOVETO);
        Assert.Equal(1098, (int)GameMessageType.MSG_LOGIC_CRC);
        Assert.Equal(1122, (int)GameMessageType.MSG_ENABLE_RETALIATION_MODE);
        Assert.Equal(1129, (int)GameMessageType.MSG_CHANGE_ORDERMODE);
        Assert.Equal(1999, (int)GameMessageType.MSG_END_NETWORK_MESSAGES);

        var values = Enum.GetValues<GameMessageType>();
        Assert.Equal(381, values.Length);
        Assert.Equal(173, values.Count(v => (int)v is >= 1000 and <= 1999));
    }

    [Fact]
    public void EnumHolesAreMalformedInput()
    {
        // 1 is a hole in the recovered table (0 = MSG_INVALID, 2 = first named low command).
        Assert.False(GameMessageTypes.IsKnown((GameMessageType)1));
        Assert.True(GameMessageTypes.IsKnown(GameMessageType.MSG_LOGIC_CRC));

        Assert.Throws<MalformedOrderException>(() => new SimOrder((GameMessageType)1, 0));
        Assert.Throws<MalformedOrderException>(() => new SimOrder((GameMessageType)999, 0));
        Assert.Throws<MalformedOrderException>(() => new SimOrder((GameMessageType)5000, 0));
    }
}

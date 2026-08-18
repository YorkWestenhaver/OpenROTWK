// SPIKE (softfloat-oracle): mechanical feasibility of driving a captured retail command log
// through the frozen SimLoop/OrderIngest pipe.
//
// The order stream below is the complete job-003 capture (vm-run-job003/ANALYSIS.md;
// parsed with bfme2-workbench tools/replay/bfme2rpl.py from
// job-003-replays_job003_Last_Replay.BfME2Replay): map probe_fight_long, seed 6182562,
// final frame 1504. Only 24 chunks exist; 15 are 0x44A logic-CRC checkpoints (the oracle
// TARGET values, not inputs), leaving 8 real orders and a terminator. Each order is
// transcribed as (timecode, code, player, args) exactly as parsed.
//
// What this proves: the retail log slots into SubmitScheduled/DrainForFrame/DispatchOrder
// unchanged - frames, player indices and wire argument types all fit the frozen F6 surface,
// and our CrcCheckpoint phase cadence (frame % 100 == 0) lands exactly on the frames whose
// CRCs retail recorded (0x44A at timecode N+1 carries the arg-9 pair (0, N) - the CRC is
// computed at frame N = 100,200,...,1500 and appended on the next timecode).
//
// What it deliberately does NOT prove: semantic execution. There are no handlers behind
// DispatchOrder for these codes yet, no map/lobby state, and no soft-float sim - that is
// the body of the oracle-mode estimate in research/softfloat-oracle-spike.md.

using System.Collections.Generic;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class OracleReplayFeedTests
{
    private readonly record struct ReplayOrder(uint Timecode, GameMessageType Code, int Player, SimOrderArg[] Args);

    // The 8 non-CRC, non-terminator orders of job-003, in stream order.
    private static ReplayOrder[] Job003Orders() => new[]
    {
        new ReplayOrder(1, GameMessageType.MSG_DESTROY_SELECTED_GROUP, 2, new[] { SimOrderArg.FromBoolean(true) }),
        new ReplayOrder(1, GameMessageType.MSG_ENABLE_RETALIATION_MODE, 2, new[] { SimOrderArg.FromInteger(0), SimOrderArg.FromBoolean(true) }),
        new ReplayOrder(1, GameMessageType.MSG_CHANGE_ORDERMODE, 2, new[] { SimOrderArg.FromInteger(0), SimOrderArg.FromInteger(1) }),
        new ReplayOrder(11, GameMessageType.MSG_ENABLE_RETALIATION_MODE, 2, new[] { SimOrderArg.FromInteger(2), SimOrderArg.FromBoolean(true) }),
        new ReplayOrder(664, GameMessageType.MSG_DESTROY_SELECTED_GROUP, 2, new[] { SimOrderArg.FromBoolean(true) }),
        new ReplayOrder(685, GameMessageType.MSG_AREA_SELECTION, 2, new[] { SimOrderArg.FromScreenRectangle(0x74E, 0x21, 0x75E, 0x2D) }),
        new ReplayOrder(685, GameMessageType.MSG_DESTROY_SELECTED_GROUP, 2, new[] { SimOrderArg.FromBoolean(true) }),
        new ReplayOrder(1502, GameMessageType.MSG_SELF_DESTRUCT, 2, new[] { SimOrderArg.FromBoolean(true) }),
    };

    // The 15 retail 0x44A checkpoints: (recorded timecode, CRC, frame the CRC was computed at).
    private static readonly (uint Timecode, uint Crc, uint CrcFrame)[] RetailCheckpoints =
    {
        (101, 0xC844BD24, 100), (201, 0xD181BAE4, 200), (301, 0xFBB27F8A, 300),
        (401, 0x3361FFEA, 400), (501, 0xF4C3BB1C, 500), (601, 0x02BD3FC7, 600),
        (701, 0x3099FCD9, 700), (801, 0x278DA662, 800), (901, 0xB594BA37, 900),
        (1001, 0xDEF1A669, 1000), (1101, 0x4CA6A605, 1100), (1201, 0x7677A59B, 1200),
        (1301, 0x7A32BDD9, 1300), (1401, 0x7E87FBEC, 1400), (1501, 0x6F39A787, 1500),
    };

    private const uint FinalFrame = 1504;

    private sealed class RecordingSystems : ISimSystems
    {
        public readonly List<(uint Frame, GameMessageType Code, int Player, int Submission)> Dispatched = new();
        public readonly List<uint> CheckpointFrames = new();

        public void IngestOrders(LogicFrame frame) { }

        public void DispatchOrder(in ScheduledOrder order) =>
            Dispatched.Add((order.Frame.Value, order.Order.Type, order.PlayerIndex, order.SubmissionIndex));

        public void ModuleUpdate(LogicFrame frame) { }
        public void PartitionUpdate(LogicFrame frame) { }
        public void CrcCheckpoint(LogicFrame frame) => CheckpointFrames.Add(frame.Value);
    }

    [Fact]
    public void Job003CommandLog_FlowsThroughSimLoop_AndCheckpointCadenceMatchesRetail()
    {
        var systems = new RecordingSystems();
        var loop = new SimLoop(systems) { CrcCheckpointIntervalInFrames = 100 };

        // Replay injection path: orders arrive already stamped with their execution frame
        // (replays are the same pipe as remote peers - design-simcore-scaffolding §4.3).
        var submissionPerFramePlayer = new Dictionary<(uint, int), int>();
        foreach (var o in Job003Orders())
        {
            var key = (o.Timecode, o.Player);
            submissionPerFramePlayer.TryGetValue(key, out var idx);
            submissionPerFramePlayer[key] = idx + 1;
            var order = new SimOrder(o.Code, o.Player);
            foreach (var a in o.Args)
            {
                order.AddArgument(a);
            }
            loop.Orders.SubmitScheduled(order, new LogicFrame(o.Timecode), idx);
        }

        while (loop.CurrentFrame.Value <= FinalFrame)
        {
            loop.Advance();
        }

        // Every order dispatched, on its recorded frame, in stream order.
        Assert.Equal(8, systems.Dispatched.Count);
        var expected = new (uint Frame, GameMessageType Code)[]
        {
            (1, GameMessageType.MSG_DESTROY_SELECTED_GROUP), (1, GameMessageType.MSG_ENABLE_RETALIATION_MODE),
            (1, GameMessageType.MSG_CHANGE_ORDERMODE), (11, GameMessageType.MSG_ENABLE_RETALIATION_MODE),
            (664, GameMessageType.MSG_DESTROY_SELECTED_GROUP), (685, GameMessageType.MSG_AREA_SELECTION),
            (685, GameMessageType.MSG_DESTROY_SELECTED_GROUP), (1502, GameMessageType.MSG_SELF_DESTRUCT),
        };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Frame, systems.Dispatched[i].Frame);
            Assert.Equal(expected[i].Code, systems.Dispatched[i].Code);
        }

        // Same-frame orders kept their per-player submission order (1060 before 1004 at 685).
        Assert.Equal(0, systems.Dispatched[5].Submission);
        Assert.Equal(1, systems.Dispatched[6].Submission);

        // Our checkpoint phase fires exactly on the frames retail computed its 0x44A CRCs
        // (frame 0 also passes 0 % 100 == 0; skip it - retail's first record is frame 100).
        var ourCheckpoints = systems.CheckpointFrames.FindAll(f => f != 0);
        Assert.Equal(RetailCheckpoints.Length, ourCheckpoints.Count);
        for (var i = 0; i < RetailCheckpoints.Length; i++)
        {
            Assert.Equal(RetailCheckpoints[i].CrcFrame, ourCheckpoints[i]);
            Assert.Equal(RetailCheckpoints[i].CrcFrame + 1, RetailCheckpoints[i].Timecode);
        }
    }
}

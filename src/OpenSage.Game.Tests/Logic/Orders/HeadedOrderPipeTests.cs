// R15 bridge P4b gate tests (dr-0039, packet BR-P4B): one order pipe.
//
// Every order a headed game executes now enters OrderIngest and leaves it in the DispatchOrders
// phase, at a scheduled frame, in the deterministic (playerIndex, submissionIndex) sequence.
// These tests pin the three behaviour changes the packet claims, plus the two rules that keep
// the change from silently eating orders:
//
//   * local input is +2 frames, once, and the outbound packet is stamped for the frame the
//     order executes on (not the frame it was issued on);
//   * a replay chunk executes at its OWN timecode even though the connection is read two
//     frames ahead - the dr-0036 canary, {3, 3, 7} -> frames 3, 3, 7, zero DrainForFrame
//     throws (R1-W3 exit gate);
//   * an OrderType with no verified OrderIdentityMap entry still executes, on the legacy local
//     path, rather than being dropped (P4a's synthesis amendment; S9-05's castle orders are
//     the live case).
//
// Render-free by construction, like HeadedSimSystemsTests: the host is HeadlessSimGame (real
// GameLogic, real PlayerManager, null-object scene, no files, no GraphicsDevice) and the legacy
// dispatcher is a recorder, so a test can watch WHICH order reached execution and WHEN without
// standing up a Scene3D full of players. What OrderProcessor then does with a castle order -
// the real money withdrawal - is S9-06's gate, not this one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSage.Data.Rep;
using OpenSage.IO;
using OpenSage.Logic.Orders;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using OpenSage.Network;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Orders;

[Collection(GameTraceCollection.Name)]
public class HeadedOrderPipeTests
{
    /// <summary>
    /// An <see cref="OrderType"/> value no member of the enum uses, standing in for S9-05's
    /// FoundationConstruct - which does not exist yet (BR-P4A #3), and whose eventual numeric
    /// value is not this packet's to invent. What is being proved is the ROUTING rule, which
    /// is per-map-entry and does not care which member is missing: an OrderType absent from
    /// OrderIdentityMap still executes locally instead of being dropped.
    /// </summary>
    private const OrderType FoundationConstructShaped = (OrderType)1900;

    /// <summary>Records what the legacy dispatcher was handed, and on which loop frame.</summary>
    private sealed class RecordingOrderProcessor : IOrderProcessor
    {
        private readonly Func<uint> _loopFrame;

        public readonly List<(uint Frame, Order Order)> Dispatched = new();

        public RecordingOrderProcessor(Func<uint> loopFrame)
        {
            _loopFrame = loopFrame;
        }

        public void Process(Order order) => Dispatched.Add((_loopFrame(), order));

        public IEnumerable<OrderType> Types => Dispatched.Select(d => d.Order.OrderType);

        public IEnumerable<uint> Frames => Dispatched.Select(d => d.Frame);
    }

    /// <summary>
    /// The single-player transport, plus a record of the frame each packet was stamped for.
    /// EchoConnection is what a skirmish runs on: it stores the local packet and hands it
    /// straight back, which is exactly how a peer's own orders reach it in a real match.
    /// </summary>
    private sealed class RecordingEchoConnection : EchoConnection
    {
        public readonly List<(uint Frame, int OrderCount)> Sent = new();

        public override void Send(uint frame, List<Order> orders)
        {
            Sent.Add((frame, orders.Count));
            base.Send(frame, orders);
        }

        public IEnumerable<uint> FramesWithOrders =>
            Sent.Where(s => s.OrderCount > 0).Select(s => s.Frame);
    }

    /// <summary>
    /// A headless host with the whole pipe attached: loop -> systems -> buffer -> connection,
    /// and a recording dispatcher standing in for OrderProcessor.
    /// </summary>
    private sealed class Pipe
    {
        public HeadlessSimGame Game;
        public SimLoop Loop;
        public RecordingOrderProcessor Dispatched;

        public void Advance(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Loop.Advance();
            }
        }
    }

    private static Pipe CreatePipe(IConnection connection, int framesBeforeTheMatch = 0)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var systems = new HeadedSimSystems(game);
        var loop = new SimLoop(systems, systems)
        {
            // Game.cs: a headed game runs with the CrcCheckpoint body switched off (packet 5).
            CrcCheckpointIntervalInFrames = 0,
        };

        // IGame.Orders is how the transport reaches the pipe this loop drains.
        game.SimLoop = loop;

        var dispatched = new RecordingOrderProcessor(() => loop.CurrentFrame.Value);
        game.OrderProcessor = dispatched;

        var pipe = new Pipe { Game = game, Loop = loop, Dispatched = dispatched };

        // The loop runs before a match does (Game.Update ticks it while the menu is up), so
        // tests can put frames on the clock before the buffer exists - which is precisely the
        // condition the buffer's net-frame translation is there for.
        pipe.Advance(framesBeforeTheMatch);

        // Setting the buffer builds the submitter over (loop, buffer), as Game.cs does.
        game.NetworkMessageBuffer = new NetworkMessageBuffer(game, connection);

        return pipe;
    }

    private static Order MoveOrder(int playerIndex) =>
        MoveOrder(playerIndex, new Vector3(10f, 20f, 0f));

    private static Order MoveOrder(int playerIndex, in Vector3 destination)
    {
        var order = new Order(playerIndex, OrderType.MoveTo);
        order.AddPositionArgument(destination);
        return order;
    }

    // ------------------------------------------------------------- +2-frame local schedule

    [Fact]
    public void ALocalOrderExecutesTwoFramesAfterTheFrameItIsIngestedOn()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Local);

        pipe.Advance(4);

        // The claimed behaviour change: 0 -> 2 frames (400ms at 5 Hz). The order is picked up
        // by frame 0's IngestOrders and executes in frame 2's DispatchOrders - not inside the
        // drain that carried it, which is where the legacy buffer executed it.
        Assert.Equal(new uint[] { 2 }, pipe.Dispatched.Frames);
        Assert.Equal(new[] { OrderType.MoveTo }, pipe.Dispatched.Types);
    }

    [Fact]
    public void NothingExecutesOnTheFrameTheOrderIsIssuedOrTheFrameAfter()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Local);

        pipe.Advance(1);
        Assert.Empty(pipe.Dispatched.Dispatched);

        pipe.Advance(1);
        Assert.Empty(pipe.Dispatched.Dispatched);

        pipe.Advance(1);
        Assert.Single(pipe.Dispatched.Dispatched);
    }

    [Fact]
    public void TheScheduleFollowsTheFrameTheOrderWasIssuedOn()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        // Issued after frame 0 has already run: ingested on frame 1, executed on frame 3.
        pipe.Advance(1);
        pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Local);
        pipe.Advance(4);

        Assert.Equal(new uint[] { 3 }, pipe.Dispatched.Frames);
    }

    [Fact]
    public void TheOutboundPacketIsStampedForTheFrameTheOrderExecutesOn()
    {
        var connection = new RecordingEchoConnection();
        var pipe = CreatePipe(connection);

        pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Local);
        pipe.Advance(3);

        // One packet per frame, each stamped for the frame its contents execute on: frames 0,
        // 1 and 2 send for 2, 3 and 4. The only packet carrying an order is the one stamped 2,
        // and the order did execute on frame 2 - so the stamp is applied exactly once
        // (NetworkConnection.Send used to add a second +2 of its own).
        Assert.Equal(new uint[] { 2, 3, 4 }, connection.Sent.Select(s => s.Frame));
        Assert.Equal(new uint[] { 2 }, connection.FramesWithOrders);
        Assert.Equal(new uint[] { 2 }, pipe.Dispatched.Frames);
    }

    [Fact]
    public void TheScheduleIsRelativeToTheMatchNotToTheLoopsLifetime()
    {
        // The loop has been ticking behind the main menu for ten frames before the match
        // starts. The connection still counts from zero; the pipe still schedules +2.
        var pipe = CreatePipe(new RecordingEchoConnection(), framesBeforeTheMatch: 10);

        pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Local);
        pipe.Advance(4);

        Assert.Equal(new uint[] { 12 }, pipe.Dispatched.Frames);
    }

    [Fact]
    public void OrdersIssuedInOneFrameExecuteInTheOrderTheyWereIssued()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        pipe.Game.OrderSubmitter.Submit(MoveOrder(0, new Vector3(1f, 0f, 0f)), OrderOrigin.Local);
        pipe.Game.OrderSubmitter.Submit(MoveOrder(0, new Vector3(2f, 0f, 0f)), OrderOrigin.Local);
        pipe.Game.OrderSubmitter.Submit(MoveOrder(0, new Vector3(3f, 0f, 0f)), OrderOrigin.Local);

        pipe.Advance(3);

        // Same frame, same player: the submission index preserves issue order.
        Assert.Equal(
            new[] { 1f, 2f, 3f },
            pipe.Dispatched.Dispatched.Select(d => d.Order.Arguments[0].Value.Position.X));
        Assert.Equal(new uint[] { 2, 2, 2 }, pipe.Dispatched.Frames);
    }

    [Fact]
    public void OrdersFromDifferentPlayersExecuteInPlayerIndexOrder()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        // Issued 3, then 1, then 2 - dispatch order is the deterministic (playerIndex,
        // submissionIndex) pair, never arrival order (OrderIngest.DrainForFrame).
        pipe.Game.OrderSubmitter.Submit(MoveOrder(3), OrderOrigin.Local);
        pipe.Game.OrderSubmitter.Submit(MoveOrder(1), OrderOrigin.Local);
        pipe.Game.OrderSubmitter.Submit(MoveOrder(2), OrderOrigin.Local);

        pipe.Advance(3);

        Assert.Equal(
            new[] { 1, 2, 3 },
            pipe.Dispatched.Dispatched.Select(d => d.Order.PlayerIndex));
    }

    [Fact]
    public void TheLegacyAddLocalOrderEntryPointGoesThroughTheSamePipe()
    {
        // Every existing call site (command bar, selection system, the S9 AI's LegacyOrderSink)
        // still calls AddLocalOrder; it is a shim over IGame.OrderSubmitter.
        var pipe = CreatePipe(new RecordingEchoConnection());

        pipe.Game.NetworkMessageBuffer.AddLocalOrder(MoveOrder(0));
        pipe.Advance(3);

        Assert.Equal(new uint[] { 2 }, pipe.Dispatched.Frames);
    }

    [Fact]
    public void SubmitRefusesOriginsThatAlreadyCarryTheirOwnSchedule()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        Assert.Throws<NotSupportedException>(
            () => pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Remote));
        Assert.Throws<NotSupportedException>(
            () => pipe.Game.OrderSubmitter.Submit(MoveOrder(0), OrderOrigin.Replay));
    }

    // ------------------------------------------- unmapped OrderTypes keep a local execution path

    [Theory]
    // The three real OrderTypes OrderIdentityMap deliberately leaves out today. ClearSelection
    // is the one that ships on the live input path (SelectionSystem issues it on every
    // click-away), so this is not a hypothetical class of order.
    // The FoundationConstruct stand-in gets its own test below, where its arguments matter.
    [InlineData(OrderType.ClearSelection)]
    [InlineData(OrderType.SelectAcrossScreen)]
    [InlineData(OrderType.ToggleOvercharge)]
    public void AnOrderTypeWithNoMapEntryStillExecutes(OrderType orderType)
    {
        Assert.False(OrderIdentityMap.TryGetGameMessageType(orderType, out _));

        var connection = new RecordingEchoConnection();
        var pipe = CreatePipe(connection);

        pipe.Game.OrderSubmitter.Submit(new Order(0, orderType), OrderOrigin.Local);
        pipe.Advance(4);

        // Executed once, on the legacy local path, at issue time - and never scheduled, since
        // it has no SimOrder form to schedule.
        Assert.Equal(new[] { orderType }, pipe.Dispatched.Types);
        Assert.Empty(connection.FramesWithOrders);
        Assert.Equal(0, pipe.Loop.Orders.PendingCount);
    }

    [Fact]
    public void AFoundationConstructShapedOrderReachesTheDispatcherWithItsArgumentsIntact()
    {
        // The synthesis amendment, stated as a test: a fortress that cannot be built is not a
        // playable AotR skirmish, so the castle orders must survive the pipe unchanged until
        // their map entries land.
        Assert.False(OrderIdentityMap.TryGetGameMessageType(FoundationConstructShaped, out _));

        var plotId = new ObjectId(4242);

        var order = new Order(2, FoundationConstructShaped);
        order.AddObjectIdArgument(plotId);
        order.AddIntegerArgument(17);

        var connection = new RecordingEchoConnection();
        var pipe = CreatePipe(connection);

        pipe.Game.OrderSubmitter.Submit(order, OrderOrigin.Local);
        pipe.Advance(4);

        // Local-only, by construction: it has no SimOrder form, so it is neither broadcast nor
        // scheduled - the documented, logged hole that closes when its map entry lands.
        Assert.Empty(connection.FramesWithOrders);
        Assert.Equal(0, pipe.Loop.Orders.PendingCount);

        var dispatched = Assert.Single(pipe.Dispatched.Dispatched).Order;

        // Same object, not a copy: the fallback hands the legacy dispatcher the very order it
        // was given, with no conversion in between.
        Assert.Same(order, dispatched);
        Assert.Equal(2, dispatched.PlayerIndex);
        Assert.Equal(plotId, dispatched.Arguments[0].Value.ObjectId);
        Assert.Equal(17, dispatched.Arguments[1].Value.Integer);
    }

    // ---------------------------------------------------------------- the dr-0036 replay canary

    private static ReplayConnection ReplayOf(params uint[] timecodes)
    {
        var chunks = new List<ReplayChunk>();
        foreach (var timecode in timecodes)
        {
            // A mapped OrderType, so the canary measures scheduling and not translation.
            var order = new Order(0, OrderType.MoveTo);
            order.AddPositionArgument(new Vector3(timecode, 0f, 0f));
            chunks.Add(ReplayChunk.CreateForTests(timecode, order));
        }

        return new ReplayConnection(ReplayFile.FromChunksForTests(chunks));
    }

    [Fact]
    public void ReplayChunksExecuteAtTheirOwnTimecode()
    {
        // dr-0036 canary, R1-W3 exit gate: timecodes {3, 3, 7} execute at 3, 3, 7 with zero
        // DrainForFrame throws - the connection is read two frames ahead, but reading ahead is
        // not rescheduling.
        var pipe = CreatePipe(ReplayOf(3, 3, 7));

        pipe.Advance(10);

        Assert.Equal(new uint[] { 3, 3, 7 }, pipe.Dispatched.Frames);
        Assert.Equal(0, pipe.Loop.Orders.PendingCount);
    }

    [Fact]
    public void ReplayChunksExecuteAtTheirOwnTimecodeWhenTheLoopDidNotStartAtZero()
    {
        // Same canary, with the loop already ten frames in when playback starts - the case
        // that actually ships, since Game ticks the loop behind the main menu while replay
        // timecodes stay 0-based.
        var pipe = CreatePipe(ReplayOf(3, 3, 7), framesBeforeTheMatch: 10);

        pipe.Advance(10);

        Assert.Equal(new uint[] { 13, 13, 17 }, pipe.Dispatched.Frames);
        Assert.Equal(0, pipe.Loop.Orders.PendingCount);
    }

    [Fact]
    public void AChunkAtTimecodeZeroExecutesOnTheFirstFrame()
    {
        var pipe = CreatePipe(ReplayOf(0, 1));

        pipe.Advance(4);

        Assert.Equal(new uint[] { 0, 1 }, pipe.Dispatched.Frames);
    }

    [Fact]
    public void ARecordedReplayDispatchesEveryTranslatableChunkAtItsOwnTimecode()
    {
        // The synthetic canary with a real file behind it: a recorded replay, played through
        // the whole pipe. Chunks whose OrderType has no map entry are skipped by design (the
        // remote/replay half of the unmapped rule), so the assertion is over the chunks that
        // DO translate - which is the population the pipe is responsible for.
        var replayFile = LoadRecordedReplay("Test_013_SetGroup");

        var expected = replayFile.Chunks
            .Where(c => OrderConverter.TryConvert(c.Order).Success)
            .Select(c => c.Header.Timecode)
            .ToArray();

        Assert.NotEmpty(expected);

        var pipe = CreatePipe(new ReplayConnection(replayFile));

        var lastTimecode = replayFile.Chunks[replayFile.Chunks.Count - 1].Header.Timecode;
        pipe.Advance((int)lastTimecode + 2);

        Assert.Equal(expected, pipe.Dispatched.Frames);
        Assert.Equal(0, pipe.Loop.Orders.PendingCount);
    }

    private static ReplayFile LoadRecordedReplay(string name)
    {
        using var fileSystem = new DiskFileSystem(
            Path.Combine(Environment.CurrentDirectory, "Data", "Rep", "Assets"));
        return ReplayFile.FromFileSystemEntry(fileSystem.GetFile(name + ".rep"));
    }

    // ------------------------------------------------------- the conversion back out of SimCore

    [Fact]
    public void EveryLegacyArgumentKindSurvivesTheRoundTrip()
    {
        var original = new Order(3, OrderType.SetSelection);
        original.AddIntegerArgument(-7);
        original.AddFloatArgument(12.5f);
        original.AddBooleanArgument(true);
        original.AddObjectIdArgument(new ObjectId(99));
        original.AddPositionArgument(new Vector3(1.25f, -2.5f, 3.75f));
        original.AddScreenPositionArgument(new Point2D(11, 13));
        original.AddScreenRectangleArgument(new Rectangle(4, 5, 6, 7));

        var converted = OrderConverter.TryConvert(original);
        Assert.True(converted.Success);

        Assert.True(SimOrderConverter.TryConvertBack(converted.Order, out var restored));

        Assert.Equal(original.OrderType, restored.OrderType);
        Assert.Equal(original.PlayerIndex, restored.PlayerIndex);
        Assert.Equal(-7, restored.Arguments[0].Value.Integer);
        Assert.Equal(12.5f, restored.Arguments[1].Value.Float);
        Assert.True(restored.Arguments[2].Value.Boolean);
        Assert.Equal(new ObjectId(99), restored.Arguments[3].Value.ObjectId);
        Assert.Equal(new Vector3(1.25f, -2.5f, 3.75f), restored.Arguments[4].Value.Position);
        Assert.Equal(new Point2D(11, 13), restored.Arguments[5].Value.ScreenPosition);
        Assert.Equal(new Rectangle(4, 5, 6, 7), restored.Arguments[6].Value.ScreenRectangle);
    }

    [Fact]
    public void AMessageTypeWithNoOrderTypeIsSkippedNotGuessed()
    {
        // MSG_FOUNDATION_CONSTRUCT is a real recovered message type with no OrderType entry
        // (S9-05 has not added one yet). The two enum numberings collide at identical integers
        // with different meanings, so a cast would execute the wrong order; the pipe logs and
        // skips instead.
        Assert.False(OrderIdentityMap.TryGetOrderType(GameMessageType.MSG_FOUNDATION_CONSTRUCT, out _));

        var simOrder = new SimOrder(GameMessageType.MSG_FOUNDATION_CONSTRUCT, playerIndex: 1);
        simOrder.AddArgument(SimOrderArg.FromObjectId(7));

        Assert.False(SimOrderConverter.TryConvertBack(simOrder, out var restored));
        Assert.Null(restored);
    }

    [Fact]
    public void AnArgumentKindWithNoLegacyCounterpartIsSkippedNotGuessed()
    {
        // Unsigned/Raw9/Raw10 have a recovered byte width but no recovered semantics, so there
        // is no legacy OrderArgumentType to put them in.
        var simOrder = new SimOrder(GameMessageType.MSG_CREATE_SELECTED_GROUP, playerIndex: 0);
        simOrder.AddArgument(SimOrderArg.FromUnsigned(5));

        Assert.False(SimOrderConverter.TryConvertBack(simOrder, out _));
    }

    [Fact]
    public void AnUnmappedMessageTypeDoesNotStopTheFrame()
    {
        // The whole-pipe version of the same rule: an unmapped scheduled order is skipped in
        // DispatchOrders and the frame - and the orders around it - carry on.
        var pipe = CreatePipe(new RecordingEchoConnection());

        var unmapped = new SimOrder(GameMessageType.MSG_FOUNDATION_CONSTRUCT, playerIndex: 0);
        pipe.Loop.Orders.SubmitScheduled(unmapped, new LogicFrame(1), submissionIndex: 0);

        var mapped = new SimOrder(GameMessageType.MSG_CREATE_SELECTED_GROUP, playerIndex: 0);
        pipe.Loop.Orders.SubmitScheduled(mapped, new LogicFrame(1), submissionIndex: 1);

        pipe.Advance(3);

        Assert.Equal(new[] { OrderType.SetSelection }, pipe.Dispatched.Types);
        Assert.Equal(new uint[] { 1 }, pipe.Dispatched.Frames);
        Assert.Equal(0, pipe.Loop.Orders.PendingCount);
    }

    // --------------------------------------------------------------- the pipe's own invariants

    [Fact]
    public void APositionCrossesThePipeQuantized()
    {
        // Not a rounding bug: both peers quantize at ingestion, identically, which is the
        // whole point of routing orders through SimCore. A value that is exactly
        // representable in Q31.32 comes back bit-identical.
        var pipe = CreatePipe(new RecordingEchoConnection());

        pipe.Game.OrderSubmitter.Submit(
            MoveOrder(0, new Vector3(0.5f, 0.25f, -0.125f)),
            OrderOrigin.Local);
        pipe.Advance(3);

        var dispatched = Assert.Single(pipe.Dispatched.Dispatched).Order;
        Assert.Equal(new Vector3(0.5f, 0.25f, -0.125f), dispatched.Arguments[0].Value.Position);

        // And the quantization really did happen: the Fix64 the sim saw is the one
        // Fix64.FromWireFloat produces from those bits.
        Assert.Equal(
            Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(0.5f)).ToFloatForDisplay(),
            dispatched.Arguments[0].Value.Position.X);
    }

    [Fact]
    public void AFrameWithNothingScheduledDispatchesNothing()
    {
        var pipe = CreatePipe(new RecordingEchoConnection());

        pipe.Advance(5);

        Assert.Empty(pipe.Dispatched.Dispatched);
        Assert.Equal(5u, pipe.Loop.CurrentFrame.Value);
    }
}

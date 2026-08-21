// R15 bridge P4b (dr-0039, packet BR-P4B): the transport pump, rewired onto OrderIngest.
//
// WHAT CHANGED, and why each change is the retail behaviour rather than a regression:
//
// 1. LOCAL INPUT IS NOW +2 FRAMES (0 -> 2, i.e. 400ms at BFME's 5 Hz). The buffer stamps its
//    outbound packet for currentFrame + OrderIngest.OrderSchedulingOffsetInFrames and the
//    transport loops that packet straight back, so a local order executes at frame N+2 on this
//    machine exactly as it does on every peer. Before, local orders were sent for the current
//    frame and executed inside the same drain - a 0-frame local path that no peer could ever
//    match. This is lockstep fidelity (api-freeze-v1 F6); it is expected in playtests and must
//    not be "fixed" by reverting the offset.
//
// 2. ORDERS EXECUTE IN THE DispatchOrders PHASE, not inside the drain. FrameOrders (the
//    "//TODO: use this for generating a replay file later on" dictionary) and the direct
//    OrderProcessor.Process call are both gone. Received orders are converted to SimOrders and
//    handed to OrderIngest.SubmitScheduled; SimLoop drains them at their scheduled frame, in
//    the deterministic (playerIndex, submissionIndex) sequence, and HeadedSimSystems.
//    DispatchOrder converts each one back and executes it. Arrival order no longer decides
//    execution order, and an order that misses its frame now throws out of
//    OrderIngest.DrainForFrame instead of sitting unnoticed in a dictionary.
//
// 3. THE +2 STAMP LIVES HERE, ONCE. NetworkConnection.Send used to add its own +2 on top of
//    whatever frame it was handed; now every IConnection.Send takes the frame the orders are
//    scheduled FOR, so Echo, Network and Replay all mean the same thing by their frame
//    argument. (Replay's own +2 is a READ-AHEAD, not a stamp - see ReplayConnection.)
//
// NET FRAMES vs LOOP FRAMES. Connections count from zero per match: replay chunk timecodes are
// 0-based, and NetworkConnection.Receive's "don't block before frame 2" guard assumes the same.
// SimLoop.CurrentFrame does not restart at a match - Game.Update ticks the loop while the main
// menu is up (IsLogicRunning is true from the constructor) - so this buffer translates: it
// pins the loop frame of its first Tick as frame 0 of the connection and converts in both
// directions. OrderIngest always sees absolute loop frames, connections always see net frames.

using System;
using System.Collections.Generic;
using OpenSage.Logic.Orders;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Network;

public sealed class NetworkMessageBuffer : DisposableBase
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IGame _game;
    private readonly IConnection _connection;

    private List<Order> _localOrders;

    /// <summary>
    /// The loop frame this buffer calls net frame 0. Pinned on the first <see cref="Tick"/>
    /// (see this file's header); null until then.
    /// </summary>
    private uint? _baseLoopFrame;

    /// <summary>
    /// Per-(scheduled net frame, player) submission counters. The wire carries no submission
    /// index, so the ingest side assigns one; it is deterministic across peers because all of
    /// a given player's orders for a given frame arrive in that player's single packet for
    /// that frame, in the order that player queued them.
    /// </summary>
    private readonly Dictionary<(uint Frame, int PlayerIndex), int> _submissionCounters = new();

    public NetworkMessageBuffer(IGame game, IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(connection);

        _game = game;
        _connection = connection;
        _localOrders = new List<Order>();
    }

    /// <summary>
    /// The legacy local-order entry point, kept for every existing call site (command bar,
    /// selection system, order generators, the S9 AI's LegacyOrderSink). It is a shim over the
    /// one pipe: <see cref="IGame.OrderSubmitter"/> decides whether the order is scheduled or
    /// falls back to legacy local dispatch.
    /// </summary>
    public void AddLocalOrder(Order order)
    {
        var submitter = _game.OrderSubmitter;
        if (submitter == null)
        {
            throw new InvalidOperationException(
                "This game has a NetworkMessageBuffer but no IOrderSubmitter, so a local " +
                "order has nowhere to go. Game builds the submitter whenever it sets the " +
                "buffer; a host that sets the buffer itself must do the same.");
        }

        submitter.Submit(order, OrderOrigin.Local);
    }

    /// <summary>
    /// Queues an order for broadcast at frame + 2. The submitter's mapped path, and the only
    /// way anything reaches the outbound packet - see <see cref="AddLocalOrder"/>.
    /// </summary>
    internal void EnqueueForBroadcast(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _localOrders.Add(order);
    }

    /// <summary>
    /// The IngestOrders phase body: broadcast this frame's local orders stamped for frame + 2,
    /// then drain whatever the connection has and schedule it in <see cref="IGame.Orders"/>.
    /// </summary>
    /// <param name="loopFrame">
    /// The frame SimLoop is currently executing (the phase's own frame argument, i.e.
    /// <see cref="SimLoop.CurrentFrame"/>).
    /// </param>
    internal void Tick(LogicFrame loopFrame)
    {
        _baseLoopFrame ??= loopFrame.Value;

        var netFrame = loopFrame.Value - _baseLoopFrame.Value;
        var scheduledNetFrame = netFrame + OrderIngest.OrderSchedulingOffsetInFrames;

        _connection.Send(scheduledNetFrame, _localOrders);

        // Create a new list instead of clearing, otherwise we would need to copy the list in
        // _connection.Send.
        _localOrders = new List<Order>();

        _connection.Receive(
            netFrame,
            (orderNetFrame, order) => Ingest(netFrame, orderNetFrame, order));

        PruneSubmissionCounters(netFrame);
    }

    /// <summary>
    /// Schedules one received order for its own frame. Late arrivals (a frame the loop has
    /// already passed) are clamped to the current frame rather than being handed to
    /// <see cref="OrderIngest"/>, which would - correctly - throw on the next drain; a loud
    /// warning is the right answer for a malformed replay, not a crash mid-playback.
    /// </summary>
    private void Ingest(uint currentNetFrame, uint orderNetFrame, Order order)
    {
        var conversion = OrderConverter.TryConvert(order);
        if (!conversion.Success)
        {
            // Remote/replay orders are the version-mismatch case IOrderSubmitter's contract
            // calls out: there is no local fallback that would keep peers in step, so the only
            // honest options are reject or fault. Rejecting loudly keeps a single unknown
            // order from killing a whole replay.
            Logger.Warn(
                $"Received order {order.OrderType} for net frame {orderNetFrame} has no " +
                $"verified SimCore translation ({conversion.Status}); skipping it.");
            return;
        }

        var scheduledNetFrame = orderNetFrame;
        if (scheduledNetFrame < currentNetFrame)
        {
            Logger.Warn(
                $"Order {order.OrderType} arrived for net frame {scheduledNetFrame}, which " +
                $"the loop already executed (now at {currentNetFrame}); dispatching it this " +
                "frame instead.");
            scheduledNetFrame = currentNetFrame;
        }

        var key = (scheduledNetFrame, order.PlayerIndex);
        _submissionCounters.TryGetValue(key, out var submissionIndex);
        _submissionCounters[key] = submissionIndex + 1;

        var orders = _game.Orders;
        if (orders == null)
        {
            throw new InvalidOperationException(
                "This game has no OrderIngest (IGame.Orders), so a received order has nowhere " +
                "to be scheduled. A host that pumps a NetworkMessageBuffer must expose the " +
                "SimLoop's order pipe.");
        }

        Logger.Trace(
            $"Scheduling order {order.OrderType} for player {order.PlayerIndex} at net frame " +
            $"{scheduledNetFrame} (submission {submissionIndex}).");

        orders.SubmitScheduled(
            conversion.Order,
            new LogicFrame(scheduledNetFrame + _baseLoopFrame.Value),
            submissionIndex);
    }

    /// <summary>
    /// Drops the counters for frames the loop has finished with. They only exist to number
    /// arrivals within one frame.
    /// </summary>
    private void PruneSubmissionCounters(uint currentNetFrame)
    {
        if (_submissionCounters.Count == 0)
        {
            return;
        }

        List<(uint Frame, int PlayerIndex)> stale = null;
        foreach (var key in _submissionCounters.Keys)
        {
            if (key.Frame <= currentNetFrame)
            {
                (stale ??= new List<(uint, int)>()).Add(key);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var key in stale)
        {
            _submissionCounters.Remove(key);
        }
    }
}

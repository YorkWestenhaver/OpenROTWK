// R15 bridge P4b (dr-0039, packet BR-P4B): the headed game's IOrderSubmitter.
//
// This is the entry half of the one order pipe. Everything a local player does - the command
// bar, the order generators, the selection system, and the S9 skirmish AI through
// LegacyOrderSink - now hands its Order here, and here decides which of the two paths P4a
// described actually carries it:
//
//   MAPPED (OrderIdentityMap has a GameMessageType, and every argument converts): the order
//   goes into NetworkMessageBuffer's outbound queue. The buffer broadcasts it stamped for
//   frame + 2 and the transport loops it straight back - EchoConnection stores the local
//   packet, NetworkConnection stores it AND sends it to the peers - so the local machine
//   ingests its own order off the same wire every peer does, at the same scheduled frame, and
//   dispatches it out of OrderIngest in the deterministic (playerIndex, submissionIndex)
//   sequence. That loopback is why this class never calls OrderIngest.SubmitLocal: doing both
//   would execute the order twice.
//
//   UNMAPPED (P4a's synthesis amendment, and IOrderSubmitter's written contract): the order
//   cannot survive the SimOrder round trip, so it is dispatched immediately on the legacy local
//   path instead of being dropped. S9-05's four castle orders (FoundationConstruct,
//   CastleUnpack, CastlePack, CastleUnpackExplicitObject) are the live case - they have target
//   GameMessageType values recorded in OrderIdentityMap but no entry until they are specced -
//   and a fortress that cannot be built is not a playable AotR skirmish. This fallback is
//   local-only and NOT lockstep-safe: it executes at frame N on this machine and is never
//   broadcast. It is a deliberate, logged, temporary hole that closes for each OrderType the
//   moment its map entry lands, not a supported multiplayer path. It is also why the R1 gate
//   grades dr-0040's determinism claim as "one pipe", not "desync-free".
//
// Remote and replay orders do NOT come through Submit(): they arrive already carrying their
// scheduled frame, off the connection, and NetworkMessageBuffer submits them straight to
// OrderIngest. IOrderSubmitter's Remote/Replay origins exist for submitters that receive a
// frame alongside the order; this one has no frame to honour and says so rather than guessing.

using System;
using OpenSage.Network;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Orders;

/// <summary>
/// The headed game's <see cref="IOrderSubmitter"/>: routes a locally-issued order either into
/// the +2-frame network schedule or, when it has no verified SimCore translation, onto the
/// legacy local dispatch path.
/// </summary>
public sealed class HeadedOrderSubmitter : IOrderSubmitter
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly SimLoop _simLoop;
    private readonly NetworkMessageBuffer _buffer;
    private readonly IOrderProcessor _legacyDispatch;

    /// <param name="simLoop">
    /// The frame driver. Read for <see cref="SimLoop.CurrentFrame"/> only - the frame an order
    /// is issued on, which this class reports and the buffer turns into a schedule.
    /// </param>
    /// <param name="buffer">The transport pump that broadcasts and ingests orders.</param>
    /// <param name="legacyDispatch">
    /// The pre-SimCore dispatcher, used only for the unmapped-order fallback described in this
    /// file's header.
    /// </param>
    public HeadedOrderSubmitter(SimLoop simLoop, NetworkMessageBuffer buffer, IOrderProcessor legacyDispatch)
    {
        ArgumentNullException.ThrowIfNull(simLoop);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(legacyDispatch);

        _simLoop = simLoop;
        _buffer = buffer;
        _legacyDispatch = legacyDispatch;
    }

    /// <summary>
    /// The frame a local order issued now would execute on: the loop's current frame plus the
    /// contract offset (<see cref="OrderIngest.OrderSchedulingOffsetInFrames"/>).
    /// </summary>
    public LogicFrame ScheduledFrameForLocalOrder =>
        new(_simLoop.CurrentFrame.Value + OrderIngest.OrderSchedulingOffsetInFrames);

    public void Submit(Order order, OrderOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (origin != OrderOrigin.Local)
        {
            throw new NotSupportedException(
                $"{nameof(HeadedOrderSubmitter)} submits local orders only; {origin} orders " +
                "arrive already scheduled and are ingested by NetworkMessageBuffer, which has " +
                "their frame. See this type's header.");
        }

        // Conversion is attempted here purely as the routing question "can this order survive
        // the SimOrder round trip?". The converted order is discarded: the wire carries the
        // legacy Order, and the buffer converts again on the way in, once per peer, so every
        // machine converts from identical bytes.
        var conversion = OrderConverter.TryConvert(order);
        if (!conversion.Success)
        {
            Logger.Debug(
                $"Order {order.OrderType} has no verified SimCore translation " +
                $"({conversion.Status}); dispatching it locally at frame " +
                $"{_simLoop.CurrentFrame.Value} instead of scheduling it. Local-only, not " +
                "broadcast - see HeadedOrderSubmitter's header.");

            _legacyDispatch.Process(order);
            return;
        }

        Logger.Trace(
            $"Order {order.OrderType} from player {order.PlayerIndex} issued at frame " +
            $"{_simLoop.CurrentFrame.Value}, scheduled for frame " +
            $"{ScheduledFrameForLocalOrder.Value}.");

        _buffer.EnqueueForBroadcast(order);
    }
}

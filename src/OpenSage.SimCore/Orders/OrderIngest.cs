// The scheduled-order buffer between the transport and the tick loop
// (api-freeze-v1 F6; design-simcore-scaffolding §4.3).
//
// Local commands are stamped frame + OrderSchedulingOffsetInFrames (= 2, matching the existing
// net code's constant in NetworkConnection.cs) and broadcast; remote and replay orders arrive
// already stamped. Dispatch order within a frame is the deterministic pair
// (playerIndex, submissionIndex) - never arrival order. The lockstep barrier itself (blocking
// until every peer's frame-N packet is in) lives in the transport, not here.

using System;
using System.Collections.Generic;
using OpenSage.SimCore.Ticking;

namespace OpenSage.SimCore.Orders
{
    /// <summary>
    /// One order queued for execution: the deterministic dispatch identity plus the payload.
    /// </summary>
    public readonly struct ScheduledOrder
    {
        public readonly LogicFrame Frame;
        public readonly int SubmissionIndex;
        public readonly SimOrder Order;

        public ScheduledOrder(LogicFrame frame, int submissionIndex, SimOrder order)
        {
            Frame = frame;
            SubmissionIndex = submissionIndex;
            Order = order;
        }

        public int PlayerIndex => Order.PlayerIndex;
    }

    public sealed class OrderIngest
    {
        /// <summary>
        /// Local commands issued during frame N execute at frame N + 2 on every peer
        /// (F6; NetworkConnection.cs OrderSchedulingOffsetInFrames).
        /// </summary>
        public const int OrderSchedulingOffsetInFrames = 2;

        // Keyed by scheduled frame; each bucket keeps arrival order only until DrainForFrame
        // sorts it into the deterministic (playerIndex, submissionIndex) dispatch order.
        private readonly SortedDictionary<uint, List<ScheduledOrder>> _pending = new();

        // Per-player submission counters for the local stamping path, reset per target frame.
        private readonly SortedDictionary<uint, SortedDictionary<int, int>> _localCounters = new();

        public int PendingCount
        {
            get
            {
                var count = 0;
                foreach (var bucket in _pending.Values)
                {
                    count += bucket.Count;
                }
                return count;
            }
        }

        /// <summary>
        /// Stamps a locally-issued order with the +2-frame schedule and the next per-player
        /// submission index for that frame, and enqueues it. The transport broadcasts the same
        /// stamped order to the peers.
        /// </summary>
        public ScheduledOrder SubmitLocal(SimOrder order, LogicFrame currentFrame)
        {
            ArgumentNullException.ThrowIfNull(order);

            var scheduledFrame = new LogicFrame(currentFrame.Value + OrderSchedulingOffsetInFrames);

            if (!_localCounters.TryGetValue(scheduledFrame.Value, out var perPlayer))
            {
                perPlayer = new SortedDictionary<int, int>();
                _localCounters.Add(scheduledFrame.Value, perPlayer);
            }

            perPlayer.TryGetValue(order.PlayerIndex, out var submissionIndex);
            perPlayer[order.PlayerIndex] = submissionIndex + 1;

            var scheduled = new ScheduledOrder(scheduledFrame, submissionIndex, order);
            Enqueue(scheduled);
            return scheduled;
        }

        /// <summary>
        /// Enqueues an order that already carries its schedule - the remote-peer and
        /// replay-injection path (replays are the same pipe, §4.3).
        /// </summary>
        public void SubmitScheduled(SimOrder order, LogicFrame frame, int submissionIndex)
        {
            ArgumentNullException.ThrowIfNull(order);
            Enqueue(new ScheduledOrder(frame, submissionIndex, order));
        }

        private void Enqueue(in ScheduledOrder scheduled)
        {
            if (!_pending.TryGetValue(scheduled.Frame.Value, out var bucket))
            {
                bucket = new List<ScheduledOrder>();
                _pending.Add(scheduled.Frame.Value, bucket);
            }

            bucket.Add(scheduled);
        }

        /// <summary>
        /// Removes and returns every order scheduled for <paramref name="frame"/>, sorted by
        /// (playerIndex, submissionIndex). An order left behind for an already-executed frame
        /// is a lockstep failure, so draining past it throws.
        /// </summary>
        public IReadOnlyList<ScheduledOrder> DrainForFrame(LogicFrame frame)
        {
            foreach (var key in _pending.Keys)
            {
                if (key < frame.Value)
                {
                    throw new InvalidOperationException(
                        $"Order scheduled for frame {key} was never dispatched (now draining frame {frame.Value}).");
                }
                break; // SortedDictionary iterates ascending; only the first key needs checking.
            }

            _localCounters.Remove(frame.Value);

            if (!_pending.Remove(frame.Value, out var bucket))
            {
                return Array.Empty<ScheduledOrder>();
            }

            // Deterministic dispatch order (F6): player index, then submission index. List.Sort
            // is unstable, but the comparison is total for well-formed input: a duplicate
            // (player, submission) pair would mean the transport delivered the same slot twice.
            bucket.Sort(static (a, b) =>
                a.PlayerIndex != b.PlayerIndex
                    ? a.PlayerIndex.CompareTo(b.PlayerIndex)
                    : a.SubmissionIndex.CompareTo(b.SubmissionIndex));

            return bucket;
        }
    }
}

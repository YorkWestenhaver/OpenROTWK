// R15 bridge P4a (dr-0039): the order-submission contract's origin tag.
//
// GPL pins the intra-frame subsystem order (research/l2-plan notes, GeneralsMD GameLogic.cpp
// 3620-3830 + AI.cpp 356-366): client-command processing happens once per frame, before
// AI::update. An order's origin decides whether it still needs that local bookkeeping - the
// +2-logic-frame local-input schedule and outbound broadcast (OrderIngest.SubmitLocal) - or
// whether it already carries its schedule because it came off the wire or a replay file
// (OrderIngest.SubmitScheduled). This enum is the whole of that decision; it carries no wire
// representation of its own; it is a submission-time routing tag only.

namespace OpenSage.Logic.Orders;

/// <summary>
/// Where an order given to <see cref="IOrderSubmitter"/> came from, and therefore how it must
/// be scheduled.
/// </summary>
public enum OrderOrigin
{
    /// <summary>
    /// Issued this frame by a player on this machine - a human via the command bar, or a
    /// local AI player (S9). Not yet stamped with a schedule: the submitter must stamp it
    /// (OrderIngest.SubmitLocal, +2 logic frames - api-freeze-v1 F6) and broadcast the same
    /// stamped order to peers.
    /// </summary>
    Local,

    /// <summary>
    /// Arrived from a remote peer over the network, already stamped with its target frame and
    /// per-player submission index by the peer that issued it. Only dispatched
    /// (OrderIngest.SubmitScheduled), never re-stamped.
    /// </summary>
    Remote,

    /// <summary>
    /// Injected from replay-file playback. Same pipe as <see cref="Remote"/> by design
    /// (OrderIngest.cs: "replays are the same pipe") - already stamped, only dispatched.
    /// </summary>
    Replay,
}

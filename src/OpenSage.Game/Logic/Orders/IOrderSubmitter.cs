// R15 bridge P4a (dr-0039): the order-submission contract (packet BR-P4A).
//
// This is the "L3 contract": the single entry point human input, the S9 AI lane, and replay
// playback submit orders through, independent of which execution path actually carries the
// order forward. Two shapes exist today:
//   * the legacy path - OrderProcessor dispatches a `Order` directly against Scene3D/GameLogic
//     (A2-uiflow finding #2: fully wired, functional, pre-SimCore);
//   * the SimCore path - OrderConverter.TryConvert turns the `Order` into a `SimOrder` via
//     OrderIdentityMap, and OrderIngest schedules it (bridge P4b, R1-W2).
// IOrderSubmitter does not pick between them; an implementation does, per order, guided by
// whether OrderIdentityMap has a translation.
//
// Castle-order amendment (R15 synthesis, BR-P4A packet): S9-05 (R1-W2) adds four castle
// OrderTypes (FoundationConstruct, CastleUnpack, CastlePack, CastleUnpackExplicitObject) that
// do not exist on OrderType yet as of this packet and so cannot be entered into
// OrderIdentityMap here (see OrderIdentityMap's "castle orders" section for the exact
// GameMessageType values they must eventually map to). Until an entry exists, those OrderTypes
// - and any other OrderType absent from the map - are UNMAPPED, and an implementation MUST NOT
// drop an unmapped order: it must still execute it on the legacy local path (OrderProcessor),
// which already runs CastleOrderHandler-shaped dispatch correctly today. Only Local-origin
// orders can fall back this way; Remote/Replay orders that arrive unmapped indicate a
// version/protocol mismatch and are the implementation's to reject or fault on, not silently
// drop.
namespace OpenSage.Logic.Orders;

/// <summary>
/// The order-submission contract: the one place human input, AI, and replay playback hand off
/// an order, decoupled from which path executes it.
/// </summary>
public interface IOrderSubmitter
{
    /// <summary>
    /// Submits <paramref name="order"/> with the given <paramref name="origin"/>.
    /// <para>
    /// For <see cref="OrderOrigin.Local"/> orders, the implementation stamps and schedules the
    /// order (SimCore path) or dispatches it immediately (legacy path) and is responsible for
    /// broadcasting whatever it stamped to peers. For <see cref="OrderOrigin.Remote"/> and
    /// <see cref="OrderOrigin.Replay"/> orders, the order already carries its schedule and the
    /// implementation only dispatches it.
    /// </para>
    /// <para>
    /// An <see cref="OrderIdentityMap"/> miss on a <see cref="OrderOrigin.Local"/> order is not
    /// an error: the implementation must fall back to the legacy local dispatch path rather
    /// than drop the order (see this file's header).
    /// </para>
    /// </summary>
    void Submit(Order order, OrderOrigin origin);
}

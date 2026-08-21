// R15 bridge P4b (dr-0039, packet BR-P4B): the legacy execution seam.
//
// P4a introduced IOrderSubmitter (where an order ENTERS the pipe). This is the other end:
// where an order LEAVES it and actually runs against Scene3D/GameLogic. There is exactly one
// production implementation - OrderProcessor, the pre-SimCore dispatcher that A2-uiflow #2
// found fully wired and functional - and the interface exists so the two places P4b routes
// orders through it (HeadedSimSystems.DispatchOrder for the scheduled SimCore path, and
// HeadedOrderSubmitter's unmapped-order fallback) can be tested without standing up a real
// Scene3D full of players.
//
// It is deliberately one method: the pipe hands over one order at a time, in the deterministic
// (playerIndex, submissionIndex) sequence OrderIngest.DrainForFrame produced. Batch dispatch is
// the thing P4b retired - see NetworkMessageBuffer's header.

namespace OpenSage.Logic.Orders;

/// <summary>
/// Executes a single legacy <see cref="Order"/> against the running game.
/// </summary>
public interface IOrderProcessor
{
    /// <summary>
    /// Executes <paramref name="order"/> now, on the calling frame. Callers are responsible
    /// for having scheduled it: this is the execution end of the pipe, not the entry point
    /// (that is <see cref="IOrderSubmitter"/>).
    /// </summary>
    void Process(Order order);
}

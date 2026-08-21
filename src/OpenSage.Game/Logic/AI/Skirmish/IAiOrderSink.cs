#nullable enable

// S9-01 (R15 L3): the AI's ONLY write to the world.
//
// The brain never calls NetworkMessageBuffer, never mutates a GameObject, never spends money
// directly - it submits orders, exactly as a human's clicks do, and the same OrderProcessor
// executes them. Two consequences the campaign depends on:
//   * the AI cannot cheat by construction (anything it can do, a player could have ordered);
//   * swapping the legacy Order pipe for SimOrder/OrderIngest (packet S9-16, dr-0040) is a
//     change of ONE implementation of this interface and touches zero manager files.
//
// Legacy-vs-SimCore warning (blackboard L2-plan #2): OrderType and GameMessageType collide at
// identical integers with different meanings. This seam speaks legacy Order only. The S9-16
// implementation must translate through an explicit map - never cast one enum to the other.

using OpenSage.Logic.Orders;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// Accepts orders produced by a skirmish AI brain and delivers them to the game's order pipe.
/// </summary>
public interface IAiOrderSink
{
    /// <summary>
    /// Submits one order. Implementations must preserve submission order: the AI relies on
    /// selection-then-command pairs arriving adjacent and in sequence (S9-04).
    /// </summary>
    void Submit(Order order);
}

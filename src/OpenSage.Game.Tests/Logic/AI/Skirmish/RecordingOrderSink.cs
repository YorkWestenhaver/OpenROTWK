#nullable enable

// S9-01 (R15 L3): the order sink every later AI test asserts against.
//
// AI behaviour is only observable as orders, so "what did the AI do this frame" is exactly
// "what landed in this list". S9-04's selection-pair discipline tests read Orders in
// submission order; nothing about that requires a running game.

using System.Collections.Generic;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Logic.Orders;

namespace OpenSage.Tests.Logic.AI.Skirmish;

/// <summary>An <see cref="IAiOrderSink"/> that records submissions in order.</summary>
internal sealed class RecordingOrderSink : IAiOrderSink
{
    private readonly List<Order> _orders = new();

    /// <summary>Every order submitted, oldest first.</summary>
    public IReadOnlyList<Order> Orders => _orders;

    public int Count => _orders.Count;

    public void Submit(Order order) => _orders.Add(order);

    public void Clear() => _orders.Clear();
}

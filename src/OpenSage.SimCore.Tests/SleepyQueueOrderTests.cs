// Gate tests for scaffolding step 4 (api-freeze-v1 §6 build order): the SleepyUpdateQueue's
// deterministic total order. The design-doc guarantee (design-simcore-scaffolding §4.4):
// ties never break by heap-insertion accident, and the pop sequence equals a full sort.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class SleepyQueueOrderTests
{
    private sealed class Item : ISleepyItem
    {
        public SleepyKey SleepyKey { get; set; }

        public int QueueIndex { get; set; } = -1;

        public Item(uint frame, uint objectId, ushort moduleIndex)
        {
            SleepyKey = new SleepyKey(frame, objectId, moduleIndex);
        }

        public override string ToString() => SleepyKey.ToString();
    }

    private static List<Item> PopAll(SleepyUpdateQueue<Item> queue)
    {
        var result = new List<Item>();
        while (queue.Count > 0)
        {
            queue.Validate();
            result.Add(queue.Dequeue());
        }
        return result;
    }

    // ------------------------------------------------------------------ the key itself

    [Fact]
    public void KeyOrdersByFrameThenObjectIdThenModuleIndex()
    {
        var baseline = new SleepyKey(10, 5, 3);

        Assert.True(new SleepyKey(9, 99, 99).CompareTo(baseline) < 0);   // earlier frame wins
        Assert.True(new SleepyKey(10, 4, 99).CompareTo(baseline) < 0);   // then lower objectId
        Assert.True(new SleepyKey(10, 5, 2).CompareTo(baseline) < 0);    // then lower moduleIndex
        Assert.Equal(0, new SleepyKey(10, 5, 3).CompareTo(baseline));
        Assert.True(new SleepyKey(11, 0, 0).CompareTo(baseline) > 0);
    }

    [Fact]
    public void KeyComparisonHandlesHighBitValues()
    {
        // uint comparisons, not int: values above int.MaxValue must still order correctly.
        Assert.True(new SleepyKey(0x80000000u, 0, 0).CompareTo(new SleepyKey(1, 0, 0)) > 0);
        Assert.True(new SleepyKey(1, 0x80000000u, 0).CompareTo(new SleepyKey(1, 1, 0)) > 0);
    }

    // ------------------------------------------------------------------ queue-order gate

    [Fact]
    public void PopSequenceEqualsFullSortForRandomizedInsertion()
    {
        // Deterministic seed: this is a determinism test, so the input is reproducible.
        var rng = new Random(0x5EED);
        var items = new List<Item>();
        for (var i = 0; i < 500; i++)
        {
            items.Add(new Item(
                (uint)rng.Next(0, 50),
                (uint)rng.Next(0, 40),
                (ushort)rng.Next(0, 8)));
        }

        // Full keys must be unique for the order to be total; dedupe collisions.
        items = items
            .GroupBy(i => i.SleepyKey)
            .Select(g => g.First())
            .ToList();

        var queue = new SleepyUpdateQueue<Item>();
        foreach (var item in items)
        {
            queue.Enqueue(item);
        }

        var popped = PopAll(queue);
        var sorted = items.OrderBy(i => i.SleepyKey).ToList();

        Assert.Equal(sorted.Select(i => i.SleepyKey), popped.Select(i => i.SleepyKey));
    }

    [Fact]
    public void TieOnFrameBreaksByObjectIdThenModuleIndexNotByInsertionOrder()
    {
        // All items share NextCallFrame; enqueue in scrambled order twice, with different
        // insertion sequences, and require the identical pop order both times.
        var keys = new (uint ObjectId, ushort ModuleIndex)[]
        {
            (7, 1), (2, 0), (7, 0), (1, 3), (2, 2), (1, 0),
        };

        List<SleepyKey> Run(IEnumerable<(uint ObjectId, ushort ModuleIndex)> insertionOrder)
        {
            var queue = new SleepyUpdateQueue<Item>();
            foreach (var (objectId, moduleIndex) in insertionOrder)
            {
                queue.Enqueue(new Item(5, objectId, moduleIndex));
            }
            return PopAll(queue).Select(i => i.SleepyKey).ToList();
        }

        var forward = Run(keys);
        var reversed = Run(keys.Reverse());

        var expected = keys
            .OrderBy(k => k.ObjectId)
            .ThenBy(k => k.ModuleIndex)
            .Select(k => new SleepyKey(5, k.ObjectId, k.ModuleIndex))
            .ToList();

        Assert.Equal(expected, forward);
        Assert.Equal(expected, reversed);
    }

    [Fact]
    public void RemoveAndRescheduleKeepTotalOrder()
    {
        var rng = new Random(0xF00D);
        var queue = new SleepyUpdateQueue<Item>();
        var live = new List<Item>();

        // Interleave enqueue / remove / reschedule, then require pop == sort of survivors.
        for (var i = 0; i < 300; i++)
        {
            var op = rng.Next(0, 4);
            if (op == 0 && live.Count > 0)
            {
                var victim = live[rng.Next(live.Count)];
                queue.Remove(victim);
                live.Remove(victim);
                Assert.Equal(-1, victim.QueueIndex);
            }
            else if (op == 1 && live.Count > 0)
            {
                var target = live[rng.Next(live.Count)];
                target.SleepyKey = new SleepyKey(
                    (uint)rng.Next(0, 100), target.SleepyKey.ObjectId, target.SleepyKey.ModuleIndex);
                queue.Reschedule(target);
            }
            else
            {
                // Unique (objectId, moduleIndex) per item keeps the full key unique.
                var item = new Item((uint)rng.Next(0, 100), (uint)i, 0);
                queue.Enqueue(item);
                live.Add(item);
            }

            queue.Validate();
        }

        var popped = PopAll(queue);
        Assert.Equal(live.Count, popped.Count);
        Assert.Equal(
            live.OrderBy(i => i.SleepyKey).Select(i => i.SleepyKey),
            popped.Select(i => i.SleepyKey));
    }

    [Fact]
    public void TryDequeueDueRespectsFrameGate()
    {
        var queue = new SleepyUpdateQueue<Item>();
        var early = new Item(3, 1, 0);
        var late = new Item(10, 1, 1);
        queue.Enqueue(late);
        queue.Enqueue(early);

        Assert.False(queue.TryDequeueDue(new LogicFrame(2), out _));

        Assert.True(queue.TryDequeueDue(new LogicFrame(3), out var due));
        Assert.Same(early, due);

        Assert.False(queue.TryDequeueDue(new LogicFrame(9), out _));
        Assert.True(queue.TryDequeueDue(new LogicFrame(10), out var second));
        Assert.Same(late, second);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void QueueGuardsAgainstMisuse()
    {
        var queue = new SleepyUpdateQueue<Item>();
        var item = new Item(1, 1, 1);
        queue.Enqueue(item);

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(item));

        var stranger = new Item(2, 2, 2);
        Assert.Throws<InvalidOperationException>(() => queue.Remove(stranger));
        Assert.Throws<InvalidOperationException>(() => queue.Reschedule(stranger));

        queue.Remove(item);
        Assert.Throws<InvalidOperationException>(() => queue.Peek());
        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());
    }

    [Fact]
    public void TwoIdenticalRunsProduceIdenticalSequences()
    {
        List<SleepyKey> Run()
        {
            var rng = new Random(42);
            var queue = new SleepyUpdateQueue<Item>();
            for (var i = 0; i < 200; i++)
            {
                queue.Enqueue(new Item((uint)rng.Next(0, 30), (uint)i, (ushort)(i % 4)));
            }
            return PopAll(queue).Select(i => i.SleepyKey).ToList();
        }

        Assert.Equal(Run(), Run());
    }
}

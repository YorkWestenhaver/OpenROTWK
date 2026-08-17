// The deterministic sleepy-update scheduler (api-freeze-v1 F6; design-simcore-scaffolding §4.4).
//
// SimCore owns the queue, the key, and the ordering guarantee: ties NEVER break by
// heap-insertion accident. The key is a total order - frame, then objectId, then moduleIndex -
// so the pop sequence for any set of items is a pure function of their keys, independent of
// insertion history, on every peer. [SEAM] UpdateSleepTime and the module-side Update signature
// belong to the module API (design-module-api); GameLogic's UpdateModule heap migrates onto this
// queue when the module surface lands.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenSage.SimCore.Ticking
{
    /// <summary>
    /// The deterministic total order for sleepy updates: next-call frame first, then owning
    /// object id, then module index within the object. No two live modules may share a full key.
    /// </summary>
    public readonly record struct SleepyKey(uint NextCallFrame, uint ObjectId, ushort ModuleIndex)
        : IComparable<SleepyKey>
    {
        public int CompareTo(SleepyKey o) // total order: frame, then objectId, then moduleIndex
            => NextCallFrame != o.NextCallFrame ? NextCallFrame.CompareTo(o.NextCallFrame)
             : ObjectId != o.ObjectId ? ObjectId.CompareTo(o.ObjectId)
             : ModuleIndex.CompareTo(o.ModuleIndex);
    }

    /// <summary>
    /// An entry the queue can schedule. <see cref="QueueIndex"/> is owned by the queue
    /// (-1 while not enqueued) and enables O(log n) removal and rescheduling.
    /// </summary>
    public interface ISleepyItem
    {
        SleepyKey SleepyKey { get; }

        int QueueIndex { get; set; }
    }

    /// <summary>
    /// Binary min-heap over <see cref="SleepyKey"/> with index back-references, mirroring the
    /// original engine's sleepy-module heap mechanics but keyed by the full deterministic key
    /// rather than a bare frame priority.
    /// </summary>
    public sealed class SleepyUpdateQueue<T> where T : class, ISleepyItem
    {
        private readonly List<T> _heap = new();

        public int Count => _heap.Count;

        public void Enqueue(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.QueueIndex != -1)
            {
                throw new InvalidOperationException("Item is already enqueued.");
            }

            _heap.Add(item);
            item.QueueIndex = _heap.Count - 1;
            SiftUp(_heap.Count - 1);
        }

        /// <summary>
        /// The item with the smallest key; the queue is not modified.
        /// </summary>
        public T Peek()
        {
            if (_heap.Count == 0)
            {
                throw new InvalidOperationException("The queue is empty.");
            }

            return _heap[0];
        }

        public T Dequeue()
        {
            var top = Peek();
            RemoveAt(0);
            return top;
        }

        /// <summary>
        /// Dequeues the front item iff its next-call frame is at or before <paramref name="frame"/>.
        /// </summary>
        public bool TryDequeueDue(LogicFrame frame, out T? item)
        {
            if (_heap.Count > 0 && _heap[0].SleepyKey.NextCallFrame <= frame.Value)
            {
                item = Dequeue();
                return true;
            }

            item = null;
            return false;
        }

        public void Remove(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var index = item.QueueIndex;
            if (index < 0 || index >= _heap.Count || !ReferenceEquals(_heap[index], item))
            {
                throw new InvalidOperationException("Item is not in this queue.");
            }

            RemoveAt(index);
        }

        /// <summary>
        /// Restores heap order after <paramref name="item"/>'s key changed in place.
        /// </summary>
        public void Reschedule(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var index = item.QueueIndex;
            if (index < 0 || index >= _heap.Count || !ReferenceEquals(_heap[index], item))
            {
                throw new InvalidOperationException("Item is not in this queue.");
            }

            index = SiftUp(index);
            SiftDown(index);
        }

        private void RemoveAt(int index)
        {
            _heap[index].QueueIndex = -1;

            var last = _heap.Count - 1;
            if (index < last)
            {
                _heap[index] = _heap[last];
                _heap[index].QueueIndex = index;
                _heap.RemoveAt(last);
                var i = SiftUp(index);
                SiftDown(i);
            }
            else
            {
                _heap.RemoveAt(last);
            }
        }

        private int SiftUp(int i)
        {
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (_heap[parent].SleepyKey.CompareTo(_heap[i].SleepyKey) <= 0)
                {
                    break;
                }

                Swap(parent, i);
                i = parent;
            }

            return i;
        }

        private void SiftDown(int i)
        {
            var count = _heap.Count;
            while (true)
            {
                var child = (i << 1) + 1;
                if (child >= count)
                {
                    break;
                }

                if (child + 1 < count && _heap[child + 1].SleepyKey.CompareTo(_heap[child].SleepyKey) < 0)
                {
                    child++;
                }

                if (_heap[i].SleepyKey.CompareTo(_heap[child].SleepyKey) <= 0)
                {
                    break;
                }

                Swap(i, child);
                i = child;
            }
        }

        private void Swap(int a, int b)
        {
            (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
            _heap[a].QueueIndex = a;
            _heap[b].QueueIndex = b;
        }

        /// <summary>
        /// Debug-build structural validation (design-simcore-scaffolding §4.4): heap invariant
        /// plus index back-reference consistency. The "pop sequence equals a full sort" pass is
        /// asserted by SleepyQueueOrderTests.
        /// </summary>
        [Conditional("DEBUG")]
        public void Validate()
        {
            for (var i = 0; i < _heap.Count; i++)
            {
                if (_heap[i].QueueIndex != i)
                {
                    throw new InvalidOperationException($"Sleepy queue index mismatch at {i}.");
                }

                var child = (i << 1) + 1;
                for (var c = child; c <= child + 1 && c < _heap.Count; c++)
                {
                    if (_heap[i].SleepyKey.CompareTo(_heap[c].SleepyKey) > 0)
                    {
                        throw new InvalidOperationException($"Sleepy queue heap violation at {i}->{c}.");
                    }
                }
            }
        }
    }
}

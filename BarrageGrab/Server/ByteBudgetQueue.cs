using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DanmakuBackend.Server
{
    // Reservations include the item being processed, not just pending items.
    internal sealed class ByteBudgetQueue<T> where T : class
    {
        private readonly object gate = new object();
        private readonly LinkedList<Lease> pending = new LinkedList<Lease>();
        private readonly Dictionary<string, LinkedListNode<Lease>> snapshots = new Dictionary<string, LinkedListNode<Lease>>();
        private readonly int maxItems;
        private readonly long maxBytes;
        private int count;
        private long bytes;
        private bool closed;
        internal readonly SemaphoreSlim Signal = new SemaphoreSlim(0, 1);
        internal int Count { get { lock (gate) return count; } }
        internal long Bytes { get { lock (gate) return bytes; } }
        internal bool IsClosed { get { lock (gate) return closed; } }

        internal ByteBudgetQueue(int maxItems, long maxBytes)
        {
            if (maxItems < 1 || maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
            this.maxItems = maxItems; this.maxBytes = maxBytes;
        }

        internal bool TryAdd(T value, long weight, string snapshotKey, out bool coalesced)
        {
            if (value == null || weight < 1) throw new ArgumentOutOfRangeException(nameof(weight));
            lock (gate)
            {
                coalesced = false;
                if (closed || weight > maxBytes) return false;
                LinkedListNode<Lease> old = null;
                if (snapshotKey != null) snapshots.TryGetValue(snapshotKey, out old);
                if (count - (old == null ? 0 : 1) >= maxItems || weight > maxBytes - bytes + (old?.Value.Weight ?? 0))
                    return false;
                if (old != null)
                {
                    pending.Remove(old);
                    Release(old.Value);
                    coalesced = true;
                }
                var lease = new Lease(this, value, weight, snapshotKey);
                var node = pending.AddLast(lease); // Latest snapshot belongs at its actual sequence position.
                if (snapshotKey != null) snapshots[snapshotKey] = node;
                count++; bytes += weight;
                Wake();
                return true;
            }
        }

        internal bool TryTake(out Lease lease)
        {
            lock (gate)
            {
                if (pending.Count == 0) { lease = null; return false; }
                lease = pending.First.Value;
                pending.RemoveFirst();
                if (lease.Key != null) snapshots.Remove(lease.Key);
                return true; // Keep the reservation until Dispose, including in-flight sends.
            }
        }

        internal int Close()
        {
            lock (gate)
            {
                if (closed) return 0;
                closed = true;
                int dropped = pending.Count;
                foreach (var item in pending) Release(item);
                pending.Clear(); snapshots.Clear(); Wake();
                return dropped;
            }
        }

        private void Wake()
        {
            try { Signal.Release(); } catch (SemaphoreFullException) { }
        }

        private void Release(Lease lease)
        {
            lock (gate)
            {
                if (lease.Released) return;
                lease.Released = true;
                bytes -= lease.Weight; count--; lease.Value = null;
            }
        }

        internal sealed class Lease : IDisposable
        {
            private readonly ByteBudgetQueue<T> owner;
            internal T Value;
            internal readonly long Weight;
            internal readonly string Key;
            internal readonly long QueuedAt = Stopwatch.GetTimestamp();
            internal bool Released;
            internal Lease(ByteBudgetQueue<T> owner, T value, long weight, string key)
            { this.owner = owner; Value = value; Weight = weight; Key = key; }
            public void Dispose() { owner.Release(this); }
        }
    }
}

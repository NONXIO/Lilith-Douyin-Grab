using System;
using System.Collections.Generic;
namespace DanmakuBackend.Server
{
    // One fixed FIFO per known method. Duplicate hits do NOT refresh recency,
    // preserving the original 300 unique-arrival window exactly.
    internal sealed class FixedMessageWindow
    {
        private sealed class Window { internal readonly HashSet<long> Set = new HashSet<long>(); internal readonly Queue<long> Fifo = new Queue<long>(); }
        private sealed class Context { internal readonly Dictionary<string, Window> Windows = new Dictionary<string, Window>(); internal LinkedListNode<long> Node; }
        private readonly Dictionary<long, Context> contexts = new Dictionary<long, Context>();
        private readonly LinkedList<long> lru = new LinkedList<long>();
        private readonly object gate = new object();
        private readonly int capacity;
        private string generation;
        internal FixedMessageWindow(int capacity = 300) { this.capacity = capacity; }
        internal int Count { get { lock (gate) { int count = 0; foreach (var context in contexts.Values) count += context.Windows.Count; return count; } } }
        internal bool Accept(string method, long id, string scope, long connection = 0)
        {
            lock (gate)
            {
                if (generation != scope) { contexts.Clear(); lru.Clear(); generation = scope; }
                if (!contexts.TryGetValue(connection, out var context))
                {
                    if (contexts.Count >= 8) { contexts.Remove(lru.First.Value); lru.RemoveFirst(); }
                    context = new Context { Node = lru.AddLast(connection) }; contexts.Add(connection, context);
                }
                else { lru.Remove(context.Node); lru.AddLast(context.Node); }
                var windows = context.Windows;
                if (!windows.TryGetValue(method, out var window)) windows.Add(method, window = new Window());
                if (!window.Set.Add(id)) return false;
                window.Fifo.Enqueue(id);
                if (window.Fifo.Count > capacity) window.Set.Remove(window.Fifo.Dequeue());
                return true;
            }
        }
        internal void Clear() { lock (gate) { contexts.Clear(); lru.Clear(); generation = null; } }
    }
}

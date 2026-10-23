using System;
using System.Collections.Generic;
using DanmakuBackend.Models;

namespace DanmakuBackend.Server
{
    public class BoundedRoomCache
    {
        private sealed class Entry { internal RoomInfo Info; internal string Web; internal DateTime Touch; internal LinkedListNode<string> Node; }
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> rooms = new Dictionary<string, Entry>();
        private readonly Dictionary<string, string> reverse = new Dictionary<string, string>();
        private readonly LinkedList<string> lru = new LinkedList<string>();
        private readonly int capacity;
        private readonly TimeSpan ttl;
        private readonly Func<DateTime> clock;
        private readonly Func<string> active;
        private DateTime nextSweep = DateTime.MinValue;
        public Action<int, RoomInfo> Changed;
        public BoundedRoomCache(int capacity, TimeSpan ttl, Func<DateTime> clock, Func<string> active)
        { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; this.ttl = ttl; this.clock = clock; this.active = active; }
        public int Count { get { lock (gate) { Prune(clock()); return rooms.Count; } } }
        public int ReverseCount { get { lock (gate) { Prune(clock()); return reverse.Count; } } }
        private void Remove(string key)
        {
            var entry = rooms[key];
            if (entry.Web != null && reverse.TryGetValue(entry.Web, out var mapped) && mapped == key) reverse.Remove(entry.Web);
            lru.Remove(entry.Node); rooms.Remove(key);
        }
        private void Prune(DateTime now)
        {
            string pinned = active();
            if (now >= nextSweep)
            {
                nextSweep = now.AddSeconds(30);
                var node = lru.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value != pinned && now - rooms[node.Value].Touch >= ttl) Remove(node.Value);
                    node = next;
                }
            }
            while (rooms.Count > capacity)
            {
                var node = lru.First;
                while (node != null && node.Value == pinned) node = node.Next;
                if (node == null) break; // Only one pinned room can exist.
                Remove(node.Value);
            }
        }
        private void Touch(Entry entry, DateTime now)
        { entry.Touch = now; lru.Remove(entry.Node); lru.AddLast(entry.Node); }
        public void SetRoomCache(string roomid, string web)
        {
            if (string.IsNullOrWhiteSpace(roomid)) return;
            RoomInfo info; bool created;
            lock (gate)
            {
                created = !rooms.TryGetValue(roomid, out var entry);
                info = created ? new RoomInfo { RoomId = roomid, WebRoomId = web, Title = "房间" + web } : entry.Info;
                info.WebRoomId = web;
                Store(info);
            }
            if (created) Changed?.Invoke(0, info);
        }
        public void AddRoomInfoCache(RoomInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.RoomId)) return;
            int mode;
            lock (gate) { mode = rooms.ContainsKey(info.RoomId) ? 1 : 0; Store(info); }
            Changed?.Invoke(mode, info);
        }
        private void Store(RoomInfo info)
        {
            var now = clock();
            if (rooms.TryGetValue(info.RoomId, out var entry))
            {
                if (entry.Web != null && reverse.TryGetValue(entry.Web, out var mapped) && mapped == info.RoomId) reverse.Remove(entry.Web);
                entry.Info = info; entry.Web = info.WebRoomId; Touch(entry, now);
            }
            else rooms.Add(info.RoomId, entry = new Entry { Info = info, Web = info.WebRoomId, Touch = now, Node = lru.AddLast(info.RoomId) });
            if (!string.IsNullOrWhiteSpace(entry.Web)) reverse[entry.Web] = info.RoomId;
            Prune(now);
        }
        public RoomInfo GetCachedWebRoomInfo(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            lock (gate)
            {
                var now = clock(); Prune(now);
                if (!rooms.TryGetValue(key, out var entry)) return null;
                if (key != active() && now - entry.Touch >= ttl) { Remove(key); return null; }
                Touch(entry, now); return entry.Info;
            }
        }
        public string GetCachedWebRoomid(string key) { return GetCachedWebRoomInfo(key)?.WebRoomId ?? "未知"; }
        public RoomInfo GetByWebRoomid(string web)
        {
            if (string.IsNullOrWhiteSpace(web)) return null;
            lock (gate) { return reverse.TryGetValue(web, out var key) ? GetCachedWebRoomInfo(key) : null; }
        }
    }
}

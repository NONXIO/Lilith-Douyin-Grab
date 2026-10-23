using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DanmakuBackend.Models.JsonEntity;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace DanmakuBackend.Server
{
    internal static class TransportSelfTest
    {
        internal static int Run() { return RunAsync().GetAwaiter().GetResult(); }

        private static async Task<int> RunAsync()
        {
            int checks = 0;
            Action<bool, string> check = (ok, name) => {
                if (!ok) throw new InvalidOperationException("Transport regression: " + name);
                checks++;
            };
            var queue = new ByteBudgetQueue<string>(2, 128L * 1024 * 1024);
            bool merged;
            check(queue.TryAdd("maximum legal capture", 64L * 1024 * 1024 + 512, null, out merged), "P0 input fits raw queue budget");
            check(queue.TryTake(out var active) && queue.Count == 1, "in-flight reservation included");
            check(!queue.TryAdd("too large second input", 64L * 1024 * 1024, null, out merged), "byte budget saturated");
            check(queue.TryAdd("small", 1, null, out merged) && !queue.TryAdd("third", 1, null, out merged), "item budget saturated");
            check(queue.Close() == 1 && queue.Count == 1, "close clears pending but preserves active reservation");
            active.Dispose(); active.Dispose();
            check(queue.Count == 0 && queue.Bytes == 0, "reservation released exactly once");

            queue = new ByteBudgetQueue<string>(2, 1000);
            queue.TryAdd("first", 100, null, out merged);
            queue.TryTake(out active);
            for (int i = 0; i < 20000; i++)
                if (!queue.TryAdd("snapshot-" + i, 100, "online|room", out merged)) throw new Exception("snapshot reservation");
            check(queue.Count == 2 && queue.Bytes == 200, "20000 snapshot updates bounded");
            active.Dispose(); queue.TryTake(out active);
            check(active.Value == "snapshot-19999", "latest snapshot retained"); active.Dispose(); queue.Close();

            queue = new ByteBudgetQueue<string>(2048, 100000);
            var concurrent = queue;
            Parallel.For(0, 10000, i => { bool ignored; concurrent.TryAdd("event", 10, null, out ignored); });
            check(queue.Count == 2048 && queue.Bytes == 20480, "concurrent producers atomically bounded");
            queue.Close(); check(queue.Count == 0 && queue.Bytes == 0, "overload cleanup");

            var started = NewSignal(); var release = NewSignal();
            var sent = new List<string>(); int sends = 0, peak = 0;
            var sender = new OrderedSocketSender(async packet => {
                int current = Interlocked.Increment(ref sends); peak = Math.Max(peak, current);
                try {
                    lock (sent) sent.Add(packet.Data);
                    if (packet.Data == "chat-0") { started.TrySetResult(true); await release.Task.ConfigureAwait(false); }
                } finally { Interlocked.Decrement(ref sends); }
            }, (_, __) => {}, 16, 10000);
            sender.TrySend(Packet("chat-0")); await Bound(started.Task);
            sender.TrySend(Packet("online-old"), "6|A");
            sender.TrySend(Packet("gift-1|incremental=2"));
            sender.TrySend(Packet("online-new"), "6|A");
            sender.TrySend(Packet("chat-1"));
            sender.TrySend(Packet("online-other-room"), "6|B");
            sender.TrySend(Packet("gift-2|incremental=1"));
            release.TrySetResult(true);
            await Until(() => sender.Count == 0);
            sender.Stop("test-close"); await Bound(sender.Completion);
            check(peak == 1 && sent.SequenceEqual(new[] { "chat-0", "gift-1|incremental=2", "online-new", "chat-1", "online-other-room", "gift-2|incremental=1" }), "mixed business order, snapshots and single writer");
            check(sender.Bytes == 0 && sender.Count == 0, "healthy sender drains all reservations");

            var never = NewSignal(); var timed = NewSignal(); started = NewSignal();
            int unconfirmed = 0;
            sender = new OrderedSocketSender(packet => { started.TrySetResult(true); return never.Task; },
                (reason, count) => { if (reason == "send-timeout") unconfirmed = count; },
                2, 10000, 5000, (_, token) => timed.Task);
            sender.TrySend(Packet("blocked")); await Bound(started.Task);
            check(sender.TrySend(Packet("pending")) && !sender.TrySend(Packet("overflow")), "slow-client item budget");
            timed.TrySetResult(true); await Bound(sender.Completion);
            check(unconfirmed == 2 && sender.Count == 0 && sender.Bytes == 0, "timeout reports in-flight and pending uncertainty");
            check(!sender.TrySend(Packet("after timeout")), "stopped sender rejects new work");
            never.TrySetException(new Exception("late underlying write failure"));

            var oldWrite = NewSignal(); started = NewSignal();
            var oldSent = new List<string>();
            var old = new OrderedSocketSender(packet => {
                oldSent.Add(packet.Data); started.TrySetResult(true); return oldWrite.Task;
            }, (_, __) => {});
            old.TrySend(Packet("old-1")); await Bound(started.Task); old.TrySend(Packet("old-2"));
            old.Stop("connection-replaced"); await Bound(old.Completion);
            var newSent = new List<string>();
            var currentSender = new OrderedSocketSender(packet => { newSent.Add(packet.Data); return Task.CompletedTask; }, (_, __) => {});
            currentSender.TrySend(Packet("new-1")); await Until(() => currentSender.Count == 0);
            currentSender.Stop("test-close"); await Bound(currentSender.Completion); oldWrite.TrySetResult(true);
            check(oldSent.SequenceEqual(new[] { "old-1" }) && newSent.SequenceEqual(new[] { "new-1" }), "old generation never sends pending work on new socket");

            string giftJson = "{\"room_id\":\"A\",\"incremental\":2}";
            check(WsBarrageServer.CountsAsBusiness(PackMsgType.礼物消息), "offline business gap counted");
            check(!WsBarrageServer.CountsAsBusiness(PackMsgType.直播伴侣启动) && !WsBarrageServer.CountsAsBusiness(PackMsgType.代理启动), "expected startup status is not a business gap");
            check(WsBarrageServer.SnapshotKey(new DanmakuMessagePack(giftJson, PackMsgType.礼物消息, "test")) == null, "gifts never coalesce");
            check(WsBarrageServer.SnapshotKey(new DanmakuMessagePack(giftJson, PackMsgType.弹幕消息, "test")) == null, "chats never coalesce");
            check(WsBarrageServer.SnapshotKey(new DanmakuMessagePack(giftJson, PackMsgType.直播间统计, "test")) == "6|A", "snapshot whitelist scoped by room");

            var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
            var plaintext = "{\"type\":5,\"data\":\"礼物增量=2\",\"process\":\"test\"}";
            var envelope = JObject.Parse(WsBarrageServer.EncryptForTransportTest(key, plaintext));
            var iv = Convert.FromBase64String(envelope["iv"].Value<string>());
            var encrypted = Convert.FromBase64String(envelope["data"].Value<string>());
            var tag = Convert.FromBase64String(envelope["tag"].Value<string>());
            var ciphertext = encrypted.Concat(tag).ToArray();
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, iv, null));
            var output = new byte[cipher.GetOutputSize(ciphertext.Length)];
            int length = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
            length += cipher.DoFinal(output, length);
            check(envelope["enc"].Value<string>() == "A256GCM" && iv.Length == 12 && tag.Length == 16 &&
                Encoding.UTF8.GetString(output, 0, length) == plaintext, "existing encrypted envelope round trip");
            return checks;
        }

        private static OutboundPacket Packet(string data) { return new OutboundPacket { Data = data }; }
        private static TaskCompletionSource<bool> NewSignal() { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static async Task Bound(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(3000)).ConfigureAwait(false) != task) throw new TimeoutException("Transport test deadline");
            await task.ConfigureAwait(false);
        }
        private static async Task Until(Func<bool> condition)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition()) {
                if (watch.ElapsedMilliseconds > 3000) throw new TimeoutException("Transport state deadline");
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DanmakuBackend.Server
{
    internal sealed class OutboundPacket
    {
        internal string Data;
        internal byte[] Key;
        internal bool Pong;
    }

    // One consumer per physical socket. Cancellation never moves work to another socket.
    internal sealed class OrderedSocketSender
    {
        private readonly ByteBudgetQueue<OutboundPacket> queue;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Func<OutboundPacket, Task> send;
        private readonly Action<string, int> stopped;
        private readonly Func<int, CancellationToken, Task> delay;
        private readonly int timeoutMs;
        private int stopping;
        internal Task Completion { get; }
        internal int Count => queue.Count;
        internal long Bytes => queue.Bytes;

        internal OrderedSocketSender(Func<OutboundPacket, Task> send, Action<string, int> stopped,
            int maxItems = 2048, long maxBytes = 128L * 1024 * 1024, int timeoutMs = 5000,
            Func<int, CancellationToken, Task> delay = null)
        {
            this.send = send; this.stopped = stopped; this.timeoutMs = timeoutMs;
            this.delay = delay ?? ((ms, token) => Task.Delay(ms, token));
            queue = new ByteBudgetQueue<OutboundPacket>(maxItems, maxBytes);
            Completion = Task.Run(Run);
        }

        internal bool TrySend(OutboundPacket packet, string snapshotKey = null)
        {
            bool merged;
            bool accepted = queue.TryAdd(packet, (long)packet.Data.Length * 2 + 256, snapshotKey, out merged);
            return accepted;
        }

        internal void Stop(string reason)
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0) return;
            int unconfirmed = queue.Close() + queue.Count; // Pending + in-flight, not an ACK claim.
            cancellation.Cancel();
            try { stopped(reason, unconfirmed); } catch { }
        }

        private async Task Run()
        {
            try
            {
                while (!queue.IsClosed)
                {
                    if (!queue.TryTake(out var lease))
                    {
                        await queue.Signal.WaitAsync(cancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                    using (lease)
                    using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                    {
                        try
                        {
                            if (cancellation.IsCancellationRequested) break;
                            var task = send(lease.Value);
                            var deadline = delay(timeoutMs, timer.Token);
                            var cancelled = Task.Delay(Timeout.Infinite, timer.Token);
                            var winner = await Task.WhenAny(task, deadline, cancelled).ConfigureAwait(false);
                            if (winner != task)
                            {
                                // Fleck cannot cancel an individual BeginWrite; close the physical
                                // socket and observe any eventual failure without starting another send.
                                Observe(task);
                                Stop(cancellation.IsCancellationRequested ? "cancelled" : "send-timeout");
                                break;
                            }
                            await task.ConfigureAwait(false);
                        }
                        catch (Exception) { Stop("send-error"); break; }
                        finally
                        {
                            timer.Cancel();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                Stop("stopped");
                // Keep the CTS valid for racing Stop callers; no live timers remain.
            }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

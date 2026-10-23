using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace DanmakuBackend.Proxy
{
    internal sealed class RoomRequestCoordinator<T> where T : class
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, TaskCompletionSource<bool>> running = new Dictionary<string, TaskCompletionSource<bool>>();
        private readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Func<DateTime> clock;
        private long lastErrorAt;
        private bool stopped;
        internal RoomRequestCoordinator(Func<DateTime> clock = null) { this.clock = clock ?? (() => DateTime.UtcNow); }
        internal int RunningCount { get { lock (gate) return running.Count; } }
        internal int RetryCount { get { lock (gate) return retryAt.Count; } }
        internal Task<bool> Run(string key, Func<CancellationToken, Task<T>> fetch, Action<T> commit)
        {
            TaskCompletionSource<bool> completion;
            lock (gate)
            {
                var now = clock();
                foreach (var expired in retryAt.Where(item => item.Value <= now).Select(item => item.Key).ToArray()) retryAt.Remove(expired);
                if (stopped || string.IsNullOrWhiteSpace(key) || running.ContainsKey(key) || retryAt.ContainsKey(key) || running.Count >= 8) return Task.FromResult(false);
                running.Add(key, completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            _ = RunCore(key, completion, fetch, commit);
            return completion.Task;
        }
        private async Task RunCore(string key, TaskCompletionSource<bool> completion, Func<CancellationToken, Task<T>> fetch, Action<T> commit)
        {
            bool success = false;
            try
            {
                var result = await fetch(cancellation.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!stopped && result != null) { commit(result); success = true; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var previous = Interlocked.Read(ref lastErrorAt);
                if ((previous == 0 || now - previous >= 10 * System.Diagnostics.Stopwatch.Frequency) && Interlocked.CompareExchange(ref lastErrorAt, now, previous) == previous)
                    Logger.LogWarn("房间请求失败（10秒限频）: " + error.GetType().Name);
            }
            finally
            {
                lock (gate)
                {
                    running.Remove(key);
                    if (!success && !stopped)
                    {
                        if (retryAt.Count >= 128) retryAt.Remove(retryAt.OrderBy(item => item.Value).First().Key);
                        retryAt[key] = clock().AddSeconds(5);
                    }
                }
                completion.TrySetResult(success);
            }
        }
        internal async Task Stop(int milliseconds = 2000)
        {
            Task[] tasks;
            lock (gate) { stopped = true; tasks = running.Values.Select(item => (Task)item.Task).ToArray(); retryAt.Clear(); }
            cancellation.Cancel();
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(milliseconds)).ConfigureAwait(false);
        }
    }
}

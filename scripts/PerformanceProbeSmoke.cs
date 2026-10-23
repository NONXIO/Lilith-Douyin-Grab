using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DanmakuBackend
{
    // Compiled together with PerformanceProbe.cs, never starts the proxy/license flow.
    public static class Logger
    {
        public static readonly ConcurrentQueue<string> Entries = new ConcurrentQueue<string>();
        public static void LogInfo(string message) { Entries.Enqueue(message); }
        public static void LogWarn(string message) { Entries.Enqueue(message); }
    }

    class PerformanceProbeSmoke
    {
        static int Main()
        {
            PerformanceProbe.Start();
            Parallel.For(0, 1000, i => {
                PerformanceProbe.SendStarted();
                PerformanceProbe.SendFinished(i % 700);
                PerformanceProbe.Queue(800, i % 900, i % 200);
                PerformanceProbe.Response(i % 400);
                PerformanceProbe.Drop();
            });
            var id = Guid.NewGuid().ToString();
            PerformanceProbe.Mark(id);
            PerformanceProbe.Mark(id);
            Thread.Sleep(6200);
            PerformanceProbe.Stop();
            JObject sample = null;
            int markers = 0;
            foreach (var entry in Logger.Entries) {
                var data = JObject.Parse(entry.Substring("[性能探针] ".Length));
                if (data["kind"].ToString() == "backend") sample = data;
                if (data["kind"].ToString() == "audio-incident-manual") markers++;
            }
            if (sample == null || markers != 1 || (int)sample["pendingSends"] != 0 ||
                (int)sample["sendCompleted"] != 1000 || (int)sample["dropped"] != 1000 ||
                (int)sample["queueDelayMaxMs"] != 899 || !(bool)sample["performanceAnomaly"])
                throw new Exception("Counter aggregation, marker deduplication or timer sampling failed");
            Console.WriteLine(sample.ToString());
            int before = Logger.Entries.Count;
            Thread.Sleep(1200);
            if (Logger.Entries.Count != before) throw new Exception("Probe did not stop");
            Console.WriteLine("Backend probe smoke PASS: concurrent counters, incident marker, real timer and cleanup");
            return 0;
        }
    }
}

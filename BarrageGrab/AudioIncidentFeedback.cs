using System;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;

namespace DanmakuBackend
{
    // Captures one bounded local sequence only after an authenticated user report.
    internal static class AudioIncidentFeedback
    {
        private static readonly object gate = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static Timer timer;
        private static string activeId;
        private static long lastMarker;
        private static long lastCpuMs;
        private static long lastSampleMs;
        private static int sampleIndex;

        public static void Mark(string id)
        {
            lock (gate)
            {
                var now = clock.ElapsedMilliseconds;
                if (timer != null || (lastMarker != 0 && now - lastMarker < 10000)) return;
                lastMarker = now;
                activeId = id;
                sampleIndex = 0;
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    lastCpuMs = (long)process.TotalProcessorTime.TotalMilliseconds;
                }
                lastSampleMs = now;
                WriteSnapshot(now, 0);
                timer = new Timer(Sample, null, 1000, 1000);
            }
        }

        private static void Sample(object state)
        {
            lock (gate)
            {
                if (timer == null) return;
                var now = clock.ElapsedMilliseconds;
                sampleIndex++;
                WriteSnapshot(now, sampleIndex);
                if (sampleIndex >= 10 || now - lastMarker >= 11000)
                {
                    timer.Dispose();
                    timer = null;
                    activeId = null;
                }
            }
        }

        private static void WriteSnapshot(long now, int index)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    var cpu = process.TotalProcessorTime.TotalMilliseconds;
                    var elapsed = Math.Max(1, now - lastSampleMs);
                    var cpuPercent = Math.Max(0, (cpu - lastCpuMs) / elapsed / Environment.ProcessorCount * 100);
                    var data = new
                    {
                        utc = DateTime.UtcNow.ToString("o"),
                        kind = "audio-incident-backend",
                        id = activeId,
                        index,
                        elapsedMs = now - lastMarker,
                        version = typeof(AudioIncidentFeedback).Assembly.GetName().Version.ToString(),
                        runtime = Environment.Version.ToString(),
                        os = Environment.OSVersion.VersionString,
                        is64Bit = Environment.Is64BitOperatingSystem,
                        pid = process.Id,
                        cpuPercent = Math.Round(cpuPercent, 2),
                        privateBytes = process.PrivateMemorySize64,
                        workingSetBytes = process.WorkingSet64,
                        managedBytes = GC.GetTotalMemory(false),
                        threads = process.Threads.Count,
                        handles = process.HandleCount,
                        gc0 = GC.CollectionCount(0),
                        gc1 = GC.CollectionCount(1),
                        gc2 = GC.CollectionCount(2),
                        service = AppRuntime.WsServer?.IncidentSnapshot()
                    };
                    Logger.LogWarn("[audio-incident] " + JsonConvert.SerializeObject(data));
                    lastCpuMs = (long)cpu;
                    lastSampleMs = now;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[audio-incident] snapshot unavailable: " + ex.GetType().Name);
            }
        }
    }
}

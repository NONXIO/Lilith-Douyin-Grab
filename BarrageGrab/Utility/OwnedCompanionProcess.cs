using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DanmakuBackend.Utility
{
    // A private job contains only the process we create, never a discovered/user process.
    internal sealed class OwnedCompanionProcess : IDisposable
    {
        private readonly object gate = new object();
        private IntPtr job, process;

        private static string ProductionEnvironment()
        {
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = (string)entry.Key;
                if (key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("VITE_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("MAIN_VITE_", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("NODE_ENV", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("NODE_PATH", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("NODE_REPL_EXTERNAL_MODULE", StringComparison.OrdinalIgnoreCase)) continue;
                values[key] = (string)entry.Value;
            }
            values["NODE_ENV"] = "production";
            var block = new StringBuilder();
            foreach (var pair in values) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            return block.Append('\0').ToString();
        }

        internal bool Start(string executable)
        {
            lock (gate)
            {
                if (job != IntPtr.Zero) return true;
                var path = Path.GetFullPath(executable);
                var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
                try
                {
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            if (candidate.SessionId != Process.GetCurrentProcess().SessionId || candidate.HasExited) continue;
                            if (string.Equals(candidate.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogInfo("直播伴侣已由外部启动，不接管进程");
                                return false;
                            }
                        }
                        catch
                        {
                            Logger.LogWarn("无法确认已有伴侣归属，不启动或接管该进程");
                            return false;
                        }
                    }
                }
                finally { foreach (var candidate in candidates) candidate.Dispose(); }

                var ownedJob = CreateJobObject(IntPtr.Zero, null);
                if (ownedJob == IntPtr.Zero) throw new Win32Exception();
                var info = new ExtendedLimit();
                info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ExtendedLimit)));
                var environment = Marshal.StringToHGlobalUni(ProductionEnvironment());
                var child = new ProcessInformation();
                try
                {
                    Marshal.StructureToPtr(info, memory, false);
                    if (!SetInformationJobObject(ownedJob, 9, memory, (uint)Marshal.SizeOf(typeof(ExtendedLimit)))) throw new Win32Exception();
                    var startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)) };
                    // Assign before the first instruction, so all descendants inherit the job.
                    if (!CreateProcess(path, new StringBuilder("\"" + path + "\""), IntPtr.Zero, IntPtr.Zero, false,
                        0x4 | 0x400, environment, Path.GetDirectoryName(path), ref startup, out child)) throw new Win32Exception();
                    if (!AssignProcessToJobObject(ownedJob, child.Process)) throw new Win32Exception();
                    if (ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception();
                    job = ownedJob; process = child.Process;
                    ownedJob = IntPtr.Zero; child.Process = IntPtr.Zero;
                    Logger.LogInfo("已启动并持有直播伴侣进程树，PID=" + child.ProcessId);
                    return true;
                }
                catch
                {
                    if (child.Process != IntPtr.Zero) TerminateProcess(child.Process, 1);
                    throw;
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                    Marshal.FreeHGlobal(environment);
                    if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
                    if (child.Process != IntPtr.Zero) CloseHandle(child.Process);
                    if (ownedJob != IntPtr.Zero) CloseHandle(ownedJob);
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (job == IntPtr.Zero) return;
                // Closing this private job also handles backend crashes/forced termination.
                CloseHandle(job); job = IntPtr.Zero;
                if (process != IntPtr.Zero)
                {
                    WaitForSingleObject(process, 2000);
                    CloseHandle(process); process = IntPtr.Zero;
                }
                Logger.LogInfo("已清理本次持有的直播伴侣进程树");
            }
        }

        internal bool IsOwned
        {
            get { lock (gate) { return job != IntPtr.Zero && process != IntPtr.Zero && WaitForSingleObject(process, 0) == 0x102; } }
        }

        internal void Release()
        {
            lock (gate)
            {
                if (job == IntPtr.Zero) return;
                var info = new ExtendedLimit();
                var memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ExtendedLimit)));
                try
                {
                    Marshal.StructureToPtr(info, memory, false);
                    if (!SetInformationJobObject(job, 9, memory, (uint)Marshal.SizeOf(typeof(ExtendedLimit)))) throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(memory); }
                CloseHandle(job); job = IntPtr.Zero;
                if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
                Logger.LogInfo("已释放本次直播伴侣归属，保持独立运行");
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
        {
            internal long ProcessTime, JobTime;
            internal uint LimitFlags;
            internal UIntPtr MinWorkingSet, MaxWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
        {
            internal BasicLimit Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            internal uint Size;
            internal string Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            internal ushort ShowWindow, ReservedSize;
            internal IntPtr ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
        {
            internal IntPtr Process, Thread;
            internal uint ProcessId, ThreadId;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr data, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

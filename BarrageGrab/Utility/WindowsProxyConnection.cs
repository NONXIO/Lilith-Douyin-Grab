using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DanmakuBackend.Utility
{
    // Default/LAN connection only. No RAS connection, policies or other users are modified.
    internal static class WindowsProxyConnection
    {
        internal sealed class State
        {
            public uint Flags;
            public string Server, Bypass, AutoConfigUrl;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { internal uint Low, High; }
        [StructLayout(LayoutKind.Explicit)]
        private struct OptionValue
        {
            [FieldOffset(0)] internal uint Number;
            [FieldOffset(0)] internal IntPtr Text;
            [FieldOffset(0)] internal FileTime Time;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Option { internal uint Kind; internal OptionValue Value; }
        [StructLayout(LayoutKind.Sequential)]
        private struct OptionList
        {
            internal uint Size;
            internal IntPtr Connection;
            internal uint Count, Error;
            internal IntPtr Options;
        }
        [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Query(IntPtr handle, uint option, ref OptionList list, ref uint size);
        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Set(IntPtr handle, uint option, ref OptionList list, uint size);
        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NotifyOption(IntPtr handle, uint option, IntPtr value, uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr value);

        internal static bool Matches(State a, State b)
        {
            return a != null && b != null && a.Flags == b.Flags &&
                (a.Server ?? "") == (b.Server ?? "") && (a.Bypass ?? "") == (b.Bypass ?? "") &&
                (a.AutoConfigUrl ?? "") == (b.AutoConfigUrl ?? "");
        }
        internal static State Read()
        {
            var stride = Marshal.SizeOf(typeof(Option));
            var buffer = Marshal.AllocHGlobal(stride * 4);
            try
            {
                for (int i = 0; i < 4; i++)
                    Marshal.StructureToPtr(new Option { Kind = i == 0 ? 10u : (uint)(i + 1) }, IntPtr.Add(buffer, i * stride), false);
                var list = new OptionList { Size = (uint)Marshal.SizeOf(typeof(OptionList)), Count = 4, Options = buffer };
                uint size = list.Size;
                if (!Query(IntPtr.Zero, 75, ref list, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var state = new State();
                for (int i = 0; i < 4; i++)
                {
                    var option = (Option)Marshal.PtrToStructure(IntPtr.Add(buffer, i * stride), typeof(Option));
                    if (i == 0) state.Flags = option.Value.Number;
                    else if (i == 1) state.Server = Marshal.PtrToStringUni(option.Value.Text);
                    else if (i == 2) state.Bypass = Marshal.PtrToStringUni(option.Value.Text);
                    else state.AutoConfigUrl = Marshal.PtrToStringUni(option.Value.Text);
                }
                return state;
            }
            finally
            {
                for (int i = 1; i < 4; i++)
                {
                    var option = (Option)Marshal.PtrToStructure(IntPtr.Add(buffer, i * stride), typeof(Option));
                    if (option.Value.Text != IntPtr.Zero) GlobalFree(option.Value.Text);
                }
                Marshal.FreeHGlobal(buffer);
            }
        }
        internal static void Write(State state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var stride = Marshal.SizeOf(typeof(Option));
            var buffer = Marshal.AllocHGlobal(stride * 4);
            var strings = new IntPtr[3];
            try
            {
                strings[0] = Marshal.StringToHGlobalUni(state.Server ?? "");
                strings[1] = Marshal.StringToHGlobalUni(state.Bypass ?? "");
                strings[2] = Marshal.StringToHGlobalUni(state.AutoConfigUrl ?? "");
                for (int i = 0; i < 4; i++)
                {
                    var option = new Option { Kind = (uint)(i + 1) };
                    if (i == 0) option.Value.Number = state.Flags;
                    else option.Value.Text = strings[i - 1];
                    Marshal.StructureToPtr(option, IntPtr.Add(buffer, i * stride), false);
                }
                var list = new OptionList { Size = (uint)Marshal.SizeOf(typeof(OptionList)), Count = 4, Options = buffer };
                if (!Set(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                foreach (var text in strings) if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
                Marshal.FreeHGlobal(buffer);
            }
        }
        internal static void Notify()
        {
            if (!NotifyOption(IntPtr.Zero, 39, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NotifyOption(IntPtr.Zero, 37, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}

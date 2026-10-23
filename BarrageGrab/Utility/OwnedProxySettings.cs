using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace DanmakuBackend.Utility
{
    internal static class OwnedProxySettings
    {
        internal sealed class Value { public bool Exists; public RegistryValueKind Kind; public string Json; }
        internal sealed class Record
        {
            public int Version = 2;
            public string Owner;
            public bool Restored;
            public Dictionary<string, Value> Original;
            public Dictionary<string, Value> Applied;
            public WindowsProxyConnection.State OriginalConnection, AppliedConnection;
        }
        internal static readonly string[] Names = { "ProxyEnable", "ProxyServer", "ProxyOverride" };
        private const string Key = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        private static string Journal => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxy-ownership.json");
        private static string Owner => WindowsIdentity.GetCurrent().User.Value;
        internal static Func<WindowsProxyConnection.State> ReadConnection = WindowsProxyConnection.Read;
        internal static Action<WindowsProxyConnection.State> WriteConnection = WindowsProxyConnection.Write;
        internal static Action NotifyConnection = WindowsProxyConnection.Notify;
        internal static bool Matches(Dictionary<string, Value> left, Dictionary<string, Value> right)
        {
            if (left == null || right == null || left.Count != Names.Length || right.Count != Names.Length) return false;
            foreach (var name in Names)
            {
                if (!left.TryGetValue(name, out var a) || !right.TryGetValue(name, out var b) || a == null || b == null) return false;
                if (a.Exists != b.Exists || (a.Exists && (a.Kind != b.Kind || a.Json != b.Json))) return false;
            }
            return true;
        }
        private static Dictionary<string, Value> Read(RegistryKey registry)
        {
            var result = new Dictionary<string, Value>();
            foreach (var name in Names)
            {
                var data = registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                result[name] = data == null ? new Value() : new Value { Exists = true, Kind = registry.GetValueKind(name), Json = JsonConvert.SerializeObject(data) };
            }
            return result;
        }
        private static Record Load()
        {
            if (!File.Exists(Journal)) return null;
            if (new FileInfo(Journal).Length > 32768) throw new InvalidDataException("Proxy ownership record too large");
            return JsonConvert.DeserializeObject<Record>(File.ReadAllText(Journal));
        }
        private static void Save(Record record) { File.WriteAllText(Journal, JsonConvert.SerializeObject(record)); }
        private static void Write(RegistryKey registry, Dictionary<string, Value> snapshot)
        {
            if (snapshot == null || snapshot.Count != Names.Length) throw new InvalidDataException("Invalid proxy snapshot");
            foreach (var name in Names)
            {
                var value = snapshot[name];
                if (!value.Exists) { registry.DeleteValue(name, false); continue; }
                object data;
                switch (value.Kind)
                {
                    case RegistryValueKind.DWord: data = JsonConvert.DeserializeObject<int>(value.Json); break;
                    case RegistryValueKind.QWord: data = JsonConvert.DeserializeObject<long>(value.Json); break;
                    case RegistryValueKind.String: case RegistryValueKind.ExpandString: data = JsonConvert.DeserializeObject<string>(value.Json); break;
                    case RegistryValueKind.MultiString: data = JsonConvert.DeserializeObject<string[]>(value.Json); break;
                    case RegistryValueKind.Binary: data = JsonConvert.DeserializeObject<byte[]>(value.Json); break;
                    default: throw new InvalidDataException("Unsupported proxy value type");
                }
                registry.SetValue(name, data, value.Kind);
            }
        }
        internal static void Apply(int port, string bypass)
        {
            using (var registry = Registry.CurrentUser.OpenSubKey(Key, true))
            {
                var original = Read(registry);
                var previous = Load();
                if (previous != null && previous.Owner != Owner && !previous.Restored) throw new InvalidDataException("Proxy journal belongs to another user");
                if (previous != null && previous.Version != 1 && previous.Version != 2) throw new InvalidDataException("Unsupported proxy journal version");
                // A v1 lease never owned connection-level options. Restore only its proven registry values before migrating.
                if (previous?.Version == 1 && previous.Owner == Owner && !previous.Restored && Matches(original, previous.Applied))
                {
                    Write(registry, previous.Original);
                    NotifyConnection();
                    original = Read(registry);
                }
                var originalConnection = ReadConnection();
                if (previous?.Version == 2 && previous.Owner == Owner && !previous.Restored &&
                    Matches(original, previous.Applied) && WindowsProxyConnection.Matches(originalConnection, previous.AppliedConnection))
                {
                    original = previous.Original;
                    originalConnection = previous.OriginalConnection;
                }
                var applied = new Dictionary<string, Value> {
                    ["ProxyEnable"] = new Value { Exists = true, Kind = RegistryValueKind.DWord, Json = "1" },
                    ["ProxyServer"] = new Value { Exists = true, Kind = RegistryValueKind.String, Json = JsonConvert.SerializeObject("127.0.0.1:" + port) },
                    ["ProxyOverride"] = new Value { Exists = true, Kind = RegistryValueKind.String, Json = JsonConvert.SerializeObject(bypass) }
                };
                var appliedConnection = new WindowsProxyConnection.State { Flags = 3, Server = "127.0.0.1:" + port,
                    Bypass = bypass, AutoConfigUrl = originalConnection.AutoConfigUrl };
                var record = new Record { Owner = Owner, Original = original, Applied = applied,
                    OriginalConnection = originalConnection, AppliedConnection = appliedConnection };
                Save(record); // Record intent before touching the system values.
                WriteConnection(appliedConnection);
                Write(registry, applied);
                NotifyConnection();
                if (!Matches(Read(registry), applied) || !WindowsProxyConnection.Matches(ReadConnection(), appliedConnection))
                    throw new InvalidDataException("Proxy settings read-back mismatch");
                Logger.LogInfo("全局代理连接级配置已验证：手动代理启用，flags=3，端口=" + port);
            }
        }
        internal static bool Restore()
        {
            var record = Load();
            if (record == null || record.Restored) return true;
            if ((record.Version != 1 && record.Version != 2) || record.Owner != Owner) { Logger.LogWarn("代理所有权记录不匹配，保留当前设置"); return false; }
            using (var registry = Registry.CurrentUser.OpenSubKey(Key, true))
            {
                var current = Read(registry);
                var connection = record.Version == 2 ? ReadConnection() : null;
                if (Matches(current, record.Original) && (record.Version == 1 || WindowsProxyConnection.Matches(connection, record.OriginalConnection)))
                    { record.Restored = true; Save(record); return true; }
                if (!Matches(current, record.Applied) || (record.Version == 2 && !WindowsProxyConnection.Matches(connection, record.AppliedConnection)))
                    { Logger.LogWarn("代理恢复冲突：注册表或连接级配置已变化，未覆盖用户设置"); return false; }
                if (record.Version == 2) WriteConnection(record.OriginalConnection);
                Write(registry, record.Original);
                NotifyConnection();
                if (!Matches(Read(registry), record.Original) || (record.Version == 2 && !WindowsProxyConnection.Matches(ReadConnection(), record.OriginalConnection)))
                    throw new InvalidDataException("Proxy restoration read-back mismatch");
                record.Restored = true; Save(record);
                return true;
            }
        }
    }
}

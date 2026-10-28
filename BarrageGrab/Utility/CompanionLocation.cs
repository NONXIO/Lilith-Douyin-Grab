using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace DanmakuBackend.Utility
{
    // Discovery is read-only: never start executables or save shortcuts here.
    public static class CompanionLocation
    {
        public const string MainName = "直播伴侣.exe";
        public const string LauncherName = "直播伴侣 Launcher.exe";

        public static string ResolveCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return "";
            candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (!Path.IsPathRooted(candidate)) throw new IOException("RELATIVE_PATH");
            candidate = Path.GetFullPath(candidate);
            if (!File.Exists(candidate)) throw new FileNotFoundException("MISSING_FILE");
            if (Path.GetExtension(candidate).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                candidate = ReadShortcut(candidate);
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate)) throw new IOException("INVALID_SHORTCUT");
            candidate = Path.GetFullPath(candidate);
            var name = Path.GetFileName(candidate);
            if (name.Equals(LauncherName, StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!File.Exists(candidate)) throw new FileNotFoundException("MISSING_LAUNCHER");
                var configPath = Path.Combine(directory, "launcher_config.json");
                if (new FileInfo(configPath).Length > 1024 * 1024) throw new IOException("LAUNCHER_CONFIG_TOO_LARGE");
                var config = JObject.Parse(File.ReadAllText(configPath, Encoding.UTF8));
                var token = config["cur_path"];
                var version = token?.Type == JTokenType.String ? token.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(version) || Path.IsPathRooted(version)) throw new IOException("INVALID_CUR_PATH");
                var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                candidate = Path.GetFullPath(Path.Combine(directory, version, MainName));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("CUR_PATH_OUTSIDE_INSTALLATION");
            }
            else if (!name.Equals(MainName, StringComparison.OrdinalIgnoreCase)) throw new IOException("UNSUPPORTED_EXECUTABLE");
            if (!File.Exists(candidate)) throw new FileNotFoundException("MISSING_EXECUTABLE");
            return candidate;
        }

        public static string ReadShortcut(string shortcutPath)
        {
            object shell = null, shortcut = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell", true);
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                return (string)shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        public static string Find(string configured, IEnumerable<string> candidates = null, Action<string> diagnostic = null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = 0;
            Func<string, string> attempt = path =>
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) return "";
                try { return ResolveCandidate(path); }
                catch (Exception error)
                {
                    if (failures++ < 8) diagnostic?.Invoke("伴侣位置候选无效: " + error.GetType().Name);
                    return "";
                }
            };
            var result = attempt(configured);
            if (result.Length != 0) return result;
            foreach (var candidate in candidates ?? GetCandidates(diagnostic))
            {
                result = attempt(candidate);
                if (result.Length != 0) return result;
            }
            diagnostic?.Invoke("未找到直播伴侣，请在启动页或后端设置中选择直播伴侣程序位置。");
            return "";
        }

        private static IEnumerable<string> GetCandidates(Action<string> diagnostic)
        {
            var paths = new List<string>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        var initialCount = paths.Count;
                        foreach (var keyName in uninstall.GetSubKeyNames())
                        {
                            if (paths.Count - initialCount >= 128) break;
                            try
                            {
                                using (var key = uninstall.OpenSubKey(keyName))
                                {
                                    var displayName = key?.GetValue("DisplayName") as string ?? "";
                                    var install = key?.GetValue("InstallLocation") as string ?? "";
                                    var uninstallCommand = key?.GetValue("UninstallString") as string ?? "";
                                    if (!displayName.Contains("直播伴侣") && !Regex.IsMatch(install + " " + uninstallCommand, @"[\\/]webcast_mate(?:[\\/""\s]|$)", RegexOptions.IgnoreCase)) continue;
                                    var icon = key?.GetValue("DisplayIcon") as string ?? "";
                                    AddDirectory(paths, install);
                                    foreach (var command in new[] { icon, uninstallCommand })
                                    {
                                        var match = Regex.Match(command, @"^\s*(?:""(?<path>[^""]+)""|(?<path>.*?\.exe)(?:\s|,|$))", RegexOptions.IgnoreCase);
                                        if (!match.Success) continue;
                                        var file = match.Groups["path"].Value;
                                        paths.Add(file);
                                        AddDirectory(paths, Path.GetDirectoryName(file));
                                    }
                                }
                            }
                            catch (Exception) { /* An inaccessible entry must not hide other installations. */ }
                        }
                    }
                }
                catch (Exception) { diagnostic?.Invoke("伴侣安装记录不可读: " + hive + "/" + view); }
            }
            foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            {
                var directory = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(directory)) continue;
                var pending = new Queue<Tuple<string, int>>();
                pending.Enqueue(Tuple.Create(Path.Combine(directory, "Programs"), 0));
                var visited = 0;
                while (pending.Count != 0 && visited++ < 2048)
                {
                    var item = pending.Dequeue();
                    try
                    {
                        paths.AddRange(Directory.GetFiles(item.Item1, "直播伴侣*.lnk"));
                        if (item.Item2 < 8)
                            foreach (var child in Directory.GetDirectories(item.Item1))
                                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Enqueue(Tuple.Create(child, item.Item2 + 1));
                    }
                    catch (Exception) { /* Skip only the inaccessible directory. */ }
                }
            }
            return paths;
        }

        private static void AddDirectory(List<string> paths, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            paths.Add(Path.Combine(directory, MainName));
            paths.Add(Path.Combine(directory, LauncherName));
        }
    }
}

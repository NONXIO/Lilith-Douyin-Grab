using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using IWshRuntimeLibrary;
using Microsoft.Win32;
using Newtonsoft.Json;
using File = System.IO.File;

namespace DanmakuBackend.Utility
{
    public static class LiveCompanHelper
    {
        public static string LiveCompanExePath { get; set; } = "";
        private static OwnedCompanionFile indexOwnership;
        private static readonly OwnedCompanionProcess ownedProcess = new OwnedCompanionProcess();
        public static bool StartOwnedProcess(string path)
        {
            try { return ownedProcess.Start(path); }
            catch (Exception error) { Logger.LogWarn("伴侣启动失败，未接管其他进程: " + error.GetType().Name); return false; }
        }
        public static void StopOwnedProcess() { ownedProcess.Dispose(); }
        public static string CompanionOwnership()
        {
            if (ownedProcess.IsOwned) return "owned";
            var name = Path.GetFileNameWithoutExtension(LiveCompanExePath);
            if (string.IsNullOrEmpty(name)) return "none";
            var candidates = Process.GetProcessesByName(name);
            try { return candidates.Length > 0 ? "external" : "none"; }
            finally { foreach (var candidate in candidates) candidate.Dispose(); }
        }
        public static void ReleaseOwnedProcess() { ownedProcess.Release(); }
        public static void RestoreOwnedFile()
        {
            try { indexOwnership?.Restore(); }
            catch (Exception error) { Logger.LogWarn("伴侣文件恢复失败，保留备份: " + error.GetType().Name); }
        }

        /// <summary>
        /// 获取抖音直播伴侣的exe路径
        /// </summary>
        /// <returns></returns>
        public static string GetExePath()
        {
            try
            {
                return GetExePathInternal();
            }
            catch (Exception ex)
            {
                // 直播伴侣路径探测是非关键逻辑，任何异常都不应影响后端启动
                Logger.LogWarn($"获取直播伴侣路径失败，跳过直播伴侣配置: {ex.Message}");
                return string.Empty;
            }
        }

        private static string GetExePathInternal()
        {
            LiveCompanExePath = CompanionLocation.Find(AppSetting.Current.LiveCompanPath, diagnostic: Logger.LogWarn);
            return LiveCompanExePath;
        }

        /// <summary>
        /// 初始化直播伴侣环境
        /// </summary>
        public static void SwitchSetup()
        {
            if (!AppSetting.Current.LiveCompanHookSwitch) return;
            try
            {
                SwitchSetupInternal();
            }
            catch (Exception ex)
            {
                // 直播伴侣环境设置失败不应影响后端启动
                Logger.LogWarn($"直播伴侣环境设置失败，已跳过: {ex.Message}");
            }
        }

        private static void SwitchSetupInternal()
        {
            var exePath = GetExePath();
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.LogWarn("未找到直播伴侣.exe文件，跳过环境设置");
                return;
            }

            //设置index.js
            var indexJsPath = Path.Combine(Path.GetDirectoryName(exePath), "resources", "app", "index.js");
            Logger.LogInfo($"正在配置直播伴侣 " + indexJsPath);
            if (!File.Exists(indexJsPath))
            {
                throw new Exception("未找到直播伴侣的index.js文件");
            }

            indexOwnership = new OwnedCompanionFile(indexJsPath);
            if (!indexOwnership.Restore()) return;
            var originalBytes = File.ReadAllBytes(indexJsPath);
            string indexJs;
            using (var reader = new StreamReader(new MemoryStream(originalBytes), Encoding.UTF8)) indexJs = reader.ReadToEnd();
            CheckBackFile(indexJsPath);
            var newjs = SetIndexJsContent(indexJs);
            if (newjs != indexJs && !newjs.IsNullOrWhiteSpace())
            {
                indexOwnership.Write(Encoding.UTF8.GetBytes(newjs), OwnedCompanionFile.Hash(originalBytes));
            }

            Logger.LogInfo("直播伴侣环境初始化完成");
        }

        //获取Ink快捷方式的目标路径
        private static string GetInkTargetPath(string shortcutPath)
        {
            // 创建 Windows Script Host 对象
            WshShell shell = new WshShell();

            // 使用 IWshShortcut 接口加载 .ink 文件
            IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);

            // 获取目标路径
            string targetPath = shortcut.TargetPath;

            return targetPath;
        }

        //检测备份文件
        private static void CheckBackFile(string filePath)
        {
            if (!File.Exists(filePath)) return;

            var bakPath = filePath + ".bak";
            if (!File.Exists(bakPath))
            {
                //拷贝一份备份
                File.Copy(filePath, bakPath);
                Logger.LogInfo($"已备份 {filePath} -> {bakPath}");
            }
        }

        //设置index.js内容
        private static string SetIndexJsContent(string content)
        {
            //检测注入代理
            var mineProxyHost = $"127.0.0.1:{AppSetting.Current.ProxyPort},direct://";
            SetSwitch("proxy-server", mineProxyHost, ref content);

            //移除文件损坏检测校验
            var checkReg = new Regex(@"if\(\w{1,2}\(\w\),!\w\.ok\)");
            if (checkReg.IsMatch(content))
            {
                content = checkReg.Replace(content, "if(false)");
                Logger.LogInfo($"直播伴侣文件改动检测点1已拦截");
            }


            checkReg = new Regex(@"if\(\(0,\w.integrityCheckReport\)\(\w\),!\w\.ok\)");
            if (checkReg.IsMatch(content))
            {
                content = checkReg.Replace(content, "if(false)");
                Logger.LogInfo($"直播伴侣文件改动检测点2已拦截");
            }

            return content;
        }

        //添加 electron 启动参数
        private static void SetSwitch(string name, string value, ref string content)
        {
            if (name.IsNullOrWhiteSpace()) return;

            //检测注入
            var proxyReg = new Regex($@"(?<varname>\w+)\.commandLine\.appendSwitch\(""{name}"",""(?<value>[^""]*)""\)");
            var proxyMatch = proxyReg.Match(content);
            //检测到已经存在配置，则更新参数值
            if (proxyMatch.Success)
            {
                var matchValue = proxyMatch.Groups["value"].Value;
                if (value != matchValue)
                {
                    content = proxyReg.Replace(content,
                        $@"${{varname}}.commandLine.appendSwitch(""{name}"",""{value}"")");
                    Logger.LogInfo($"直播伴侣成功覆盖启动参数  [{name}] = [{value}]");
                }
            }
            //否则添加新的配置
            else
            {
                var nosandboxReg = new Regex(@"(?<varname>\w+)\.commandLine\.appendSwitch\(""no-sandbox""\)");
                var match = nosandboxReg.Match(content);
                if (match.Success)
                {
                    var newvalue =
                        $@"{match.Groups["varname"].ToString()}.commandLine.appendSwitch(""{name}"",""{value}""),";
                    content = content.Insert(match.Index, newvalue);
                    Logger.LogInfo($"直播伴侣成功添加启动参数  [{name}] = [{value}]");
                }
            }
        }

        private static void SetInnerText(HtmlNode node, string text)
        {
            node.InnerHtml = "";
            var textNode = node.OwnerDocument.CreateTextNode(text);
            node.AppendChild(textNode);
        }
    }
}

using System;
using System.Configuration;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using static System.Configuration.ConfigurationManager;

namespace DanmakuBackend
{
    internal class AppSetting
    {
        private AppSetting()
        {
            try
            {
                ProcessFilter = (AppSettings["processFilter"] ?? "直播伴侣,douyin,chrome,firefox").Trim().Split(',');
                WsProt = int.Parse(AppSettings["wsListenPort"] ?? "8801");
                ProxyPort = int.Parse(AppSettings["proxyPort"] ?? "8800");
                FilterHostName = bool.Parse((AppSettings["filterHostName"] ?? "true").Trim());
                HostNameFilter = (AppSettings["hostNameFilter"] ?? "").Trim().Split(',')
                    .Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
                UsedProxy = bool.Parse((AppSettings["sysProxy"] ?? "true").Trim());
                UpstreamProxy = (AppSettings["upstreamProxy"] ?? "").Trim();
                AutoPause = bool.Parse((AppSettings["autoPause"] ?? "true").Trim());
                ForcePolling = bool.Parse((AppSettings["forcePolling"] ?? "false").Trim());
                PollingInterval = int.Parse((AppSettings["pollingInterval"] ?? "3000").Trim());
                DisableLivePageScriptCache = bool.Parse((AppSettings["disableLivePageScriptCache"] ?? "false").Trim());
                LiveCompanPath = (AppSettings["liveCompanPath"] ?? "").Trim();
                LiveCompanHookSwitch = bool.Parse((AppSettings["liveCompanHookSwitch"] ?? "false").Trim());
            }
            catch (Exception ex)
            {
                Logger.LogError("配置文件读取失败,请检查配置文件是否正确");
                throw ex;
            }
        }

        public static AppSetting Current { get; } = new AppSetting();

        /// <summary>
        /// 使用系统代理
        /// </summary>
        public bool UsedProxy { get; private set; } = true;

        /// <summary>
        /// 过滤的进程
        /// </summary>
        public string[] ProcessFilter { get; private set; } = { "直播伴侣", "douyin", "chrome", "firefox" };

        /// <summary>
        /// 端口号
        /// </summary>
        public int WsProt { get; set; } = 8880;

        /// <summary>
        /// 代理端口
        /// </summary>
        public int ProxyPort { get; private set; } = 8123;

        /// <summary>
        /// 使用域名过滤
        /// </summary>
        public bool FilterHostName { get; private set; } = true;

        /// <summary>
        /// 域名白名单列表
        /// </summary>
        public string[] HostNameFilter { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// 上游代理地址
        /// </summary>
        public string UpstreamProxy { get; set; } = string.Empty;

        /// <summary>
        /// 进入直播间自动暂停播放
        /// </summary>
        public bool AutoPause { get; private set; } = false;

        /// <summary>
        /// 强制启用轮询模式获取弹幕(仅对浏览器和客户端生效)
        /// </summary>
        public bool ForcePolling { get; private set; } = false;

        /// <summary>
        /// 控制轮询间隔
        /// </summary>
        public int PollingInterval { get; private set; } = 3000;

        /// <summary>
        /// 禁用直播页面脚本缓存
        /// </summary>
        public bool DisableLivePageScriptCache { get; private set; } = true;

        /// <summary>
        /// 直播伴侣文件位置
        /// </summary>
        public string LiveCompanPath { get; set; } = string.Empty;

        /// <summary>
        /// hook直播伴侣代理开关
        /// </summary>
        public bool LiveCompanHookSwitch { get; set; } = true;

        /// <summary>
        /// 从JSON文件加载配置
        /// </summary>
        /// <param name="jsonFilePath">JSON配置文件路径</param>
        public void LoadFromJson(string jsonStr = null)
        {
            try
            {
                if (jsonStr.IsNullOrEmpty())
                {
                    Logger.LogError($"配置文件为空");
                    return;
                }

                JObject config = JObject.Parse(jsonStr);
                // 从JSON读取应用配置
                var app = config["app"];
                // 网络配置
                var network = app?["network"];
                var proxy = network?["proxy"];
                ProxyPort = proxy?["port"]?.Value<int>() ?? 8827;
                UsedProxy = proxy?["enabled"]?.Value<bool>() ?? true;
                UpstreamProxy = proxy?["upstreamAddress"]?.Value<string>() ?? string.Empty;

                var websocket = network?["websocket"];
                WsProt = websocket?["listenPort"]?.Value<int>() ?? 8888;
                // 过滤配置
                var filtering = app?["filtering"];
                var processFilterStr = filtering?["processFilter"]?.Value<string>() ?? "直播伴侣,douyin,chrome";
                ProcessFilter = processFilterStr.Split(',');
                FilterHostName = filtering?["hostNameEnabled"]?.Value<bool>() ?? true;
                var hostNameFilterStr = filtering?["hostNameList"]?.Value<string>() ?? string.Empty;
                HostNameFilter = !string.IsNullOrWhiteSpace(hostNameFilterStr)
                    ? hostNameFilterStr.Split(',').Where(w => !string.IsNullOrWhiteSpace(w)).ToArray()
                    : Array.Empty<string>();
                // 弹幕配置
                var polling = app?["barrage"]?["polling"];
                ForcePolling = polling?["enabled"]?.Value<bool>() ?? false;
                PollingInterval = polling?["interval"]?.Value<int>() ?? 3000;
                DisableLivePageScriptCache = polling?["disableScriptCache"]?.Value<bool>() ?? false;

                // 直播伴侣配置
                var liveCompanion = app?["liveCompanion"];
                LiveCompanPath = liveCompanion?["path"]?.Value<string>() ?? string.Empty;
                LiveCompanHookSwitch = liveCompanion?["hookEnabled"]?.Value<bool>() ?? false;
                AutoPause = liveCompanion?["autoPause"]?.Value<bool>() ?? true;
                Logger.LogInfo("已从JSON配置文件加载设置");
            }
            catch (Exception ex)
            {
                Logger.LogError($"JSON加载配置失败: {ex.Message}");
            }
        }

        public string SaveToJson()
        {
            var config = new
            {
                app = new
                {
                    network = new
                    {
                        proxy = new
                        {
                            port = ProxyPort,
                            enabled = UsedProxy,
                            upstreamAddress = UpstreamProxy
                        },
                        websocket = new
                        {
                            listenPort = WsProt,
                        }
                    },
                    filtering = new
                    {
                        processFilter = string.Join(",", ProcessFilter),
                        hostNameEnabled = FilterHostName,
                        hostNameList = string.Join(",", HostNameFilter)
                    },
                    barrage = new
                    {
                        polling = new
                        {
                            enabled = ForcePolling,
                            interval = PollingInterval,
                            disableScriptCache = DisableLivePageScriptCache
                        }
                    },
                    liveCompanion = new
                    {
                        path = LiveCompanPath,
                        hookEnabled = LiveCompanHookSwitch,
                        autoPause = AutoPause
                    }
                }
            };
            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 解析布尔值字符串
        /// </summary>
        private bool ParseBool(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            value = value.ToLower().Trim();
            return value == "true" || value == "1" || value == "yes" || value == "y" || value == "on";
        }

        public void Save()
        {
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            config.AppSettings.Settings["wsListenPort"].Value = WsProt.ToString();
            config.AppSettings.Settings["upstreamProxy"].Value = UpstreamProxy;
            config.Save(ConfigurationSaveMode.Modified);
            RefreshSection(config.AppSettings.SectionInformation.Name);
        }
    }
}
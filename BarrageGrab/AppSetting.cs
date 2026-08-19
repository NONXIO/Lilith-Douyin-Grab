using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace DanmakuBackend
{
    internal class AppSetting
    {
        private AppSetting()
        {
            try
            {
                LoadFromFile();
            }
            catch (Exception ex)
            {
                Logger.LogError("配置文件读取失败,请检查配置文件是否正确");
                throw ex;
            }
        }

        public static AppSetting Current { get; } = new AppSetting();

        /// <summary>
        /// JSON 配置文件路径
        /// </summary>
        public static string ConfigFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        /// <summary>
        /// 使用系统代理
        /// </summary>
        public bool UsedProxy { get; private set; } = false;

        /// <summary>
        /// 过滤的进程
        /// </summary>
        public string[] ProcessFilter { get; private set; } = { "直播伴侣" };

        /// <summary>
        /// 端口号
        /// </summary>
        public int WsProt { get; set; } = 8801;

        /// <summary>
        /// 代理端口
        /// </summary>
        public int ProxyPort { get; private set; } = 8800;

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
        public bool AutoPause { get; private set; } = true;

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
        public bool DisableLivePageScriptCache { get; private set; } = false;

        /// <summary>
        /// 直播伴侣文件位置
        /// </summary>
        public string LiveCompanPath { get; set; } = string.Empty;

        /// <summary>
        /// hook直播伴侣代理开关
        /// </summary>
        public bool LiveCompanHookSwitch { get; set; } = true;

        /// <summary>
        /// 从 JSON 配置文件加载设置；若文件不存在则使用默认值并生成配置文件
        /// </summary>
        private void LoadFromFile()
        {
            if (!File.Exists(ConfigFilePath))
            {
                Logger.LogInfo($"未找到配置文件[{ConfigFilePath}]，使用默认设置");
                Save();
                return;
            }

            var jsonStr = File.ReadAllText(ConfigFilePath);
            LoadFromJson(jsonStr);
        }

        /// <summary>
        /// 从JSON字符串加载配置
        /// </summary>
        /// <param name="jsonStr">JSON配置内容</param>
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
                ProxyPort = proxy?["port"]?.Value<int>() ?? 8800;
                UsedProxy = proxy?["enabled"]?.Value<bool>() ?? false;
                UpstreamProxy = proxy?["upstreamAddress"]?.Value<string>() ?? string.Empty;

                var websocket = network?["websocket"];
                WsProt = websocket?["listenPort"]?.Value<int>() ?? 8801;
                // 过滤配置
                var filtering = app?["filtering"];
                var processFilterStr = filtering?["processFilter"]?.Value<string>() ?? "直播伴侣";
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
                LiveCompanHookSwitch = liveCompanion?["hookEnabled"]?.Value<bool>() ?? true;
                AutoPause = liveCompanion?["autoPause"]?.Value<bool>() ?? true;
                Logger.LogInfo("已从JSON配置文件加载设置");
            }
            catch (Exception ex)
            {
                Logger.LogError($"JSON加载配置失败: {ex.Message}");
            }
        }

        public string SaveToJson(bool indented = false)
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
            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = indented });
        }

        /// <summary>
        /// 保存设置到 JSON 配置文件
        /// </summary>
        public void Save()
        {
            try
            {
                var json = SaveToJson(true);
                File.WriteAllText(ConfigFilePath, json);
                Logger.LogInfo("配置已保存到 config.json");
            }
            catch (Exception ex)
            {
                Logger.LogError($"保存配置到文件失败: {ex.Message}");
            }
        }
    }
}
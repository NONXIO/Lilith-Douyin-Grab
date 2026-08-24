using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DanmakuBackend.Proxy.ProxyEventArgs;
using Microsoft.Win32;

namespace DanmakuBackend.Proxy
{
    internal abstract class SystemProxy : ISystemProxy
    {
        /// <summary>
        ///     进程名称缓存（避免重复调用 Process.GetProcessById）
        /// </summary>
        private static readonly ConcurrentDictionary<int, string> _processNameCache =
            new ConcurrentDictionary<int, string>();

        /// <summary>
        ///     弹幕事件队列：代理网络线程只做入队，重活由专用后台线程消费，
        ///     避免解析/序列化/同步发送阻塞代理数据泵导致直播伴侣卡顿
        /// </summary>
        private readonly BlockingCollection<Action> _eventQueue =
            new BlockingCollection<Action>(new ConcurrentQueue<Action>(), 50000);

        /// <summary>
        ///     代理端口
        /// </summary>
        public int ProxyPort => AppSetting.Current.ProxyPort;

        /// <summary>
        /// 接收到websocket消息事件
        /// </summary>
        public event EventHandler<WsMessageEventArgs> OnWebSocketData;

        /// <summary>
        /// 接收到http响应事件
        /// </summary>
        public event EventHandler<HttpResponseEventArgs> OnFetchResponse;

        /// <summary>
        /// 代理切换时触发
        /// </summary>
        public event EventHandler<SystemProxyChangeEventArgs> OnProxyStatus;

        /// <summary>
        /// 房间状态变更
        /// </summary>
        public event EventHandler<RoomStatusEventArgs> OnRoomStatusChange;

        public abstract string HttpUpstreamProxy { get; }

        public abstract string HttpsUpstreamProxy { get; }

        public abstract void Dispose();

        public abstract void Start();

        public abstract void SetUpstreamProxy(string addr);

        protected SystemProxy()
        {
            var worker = new Thread(EventWorkerLoop)
            {
                IsBackground = true,
                Name = "ProxyEventWorker"
            };
            worker.Start();
        }

        private void EventWorkerLoop()
        {
            foreach (var action in _eventQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"处理弹幕事件时出错: {ex.Message}");
                }
            }
        }

        /// <summary>
        ///     停止事件消费线程（应在代理 Dispose 时调用）
        /// </summary>
        protected void CompleteEventQueue()
        {
            try
            {
                _eventQueue.CompleteAdding();
            }
            catch (Exception)
            {
                // 忽略重复完成
            }
        }

        /// <summary>
        ///     注册为系统代�?
        /// </summary>
        public void RegisterSystemProxy()
        {
            var registry =
                Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
                    true);
            registry.SetValue("ProxyEnable", 1);
            registry.SetValue("ProxyServer", $"127.0.0.1:{ProxyPort}");
            registry.SetValue("ProxyOverride", BuildProxyOverride(registry));

            OnProxyStatus?.Invoke(this, new SystemProxyChangeEventArgs
            {
                Open = true
            });
        }

        /// <summary>
        ///     关闭系统代理
        /// </summary>
        public void CloseSystemProxy()
        {
            var registry =
                Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
                    true);
            registry.SetValue("ProxyEnable", 0);
            registry.SetValue("ProxyServer", $"http=localhost:{ProxyPort};https=localhost:{ProxyPort}");
            OnProxyStatus?.Invoke(this, new SystemProxyChangeEventArgs
            {
                Open = false
            });
        }

        //https://live.douyin.com/webcast/gift/list/ [礼物数据接口]

        /// <summary>
        /// 检测是否是业务相关链接
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        protected virtual bool CheckHost(string host)
        {
            host = host.Trim().ToLower();

            if (!AppSetting.Current.FilterHostName) return true;

            if (host.StartsWith("webcast")) return true;

            if (AppSetting.Current.HostNameFilter.Any(a => a.Trim().ToLower() == host)) return true;

            return false;
        }

        /// <summary>
        /// 触发websocket消息事件（入队由后台线程处理，不阻塞代理网络线程）
        /// </summary>
        /// <param name="args"></param>
        protected void FireWsEvent(WsMessageEventArgs args)
        {
            var queuedAt = DateTime.Now;
            if (!_eventQueue.TryAdd(() =>
                {
                    var delay = (DateTime.Now - queuedAt).TotalMilliseconds;
                    // 性能探针：队列积压说明弹幕处理链路消费不过来
                    if (delay > 500)
                        Logger.LogWarn($"[性能探针] WS弹幕事件排队延迟 {delay:F0}ms，处理链路可能积压");
                    OnWebSocketData?.Invoke(this, args);
                }))
            {
                Logger.LogError("弹幕事件队列已满，丢弃一条WS消息");
            }
        }

        /// <summary>
        /// 触发弹幕http弹幕事件（入队由后台线程处理，不阻塞代理网络线程）
        /// </summary>
        /// <param name="args"></param>
        protected void FireOnFetchResponse(HttpResponseEventArgs args)
        {
            var queuedAt = DateTime.Now;
            if (!_eventQueue.TryAdd(() =>
                {
                    var delay = (DateTime.Now - queuedAt).TotalMilliseconds;
                    if (delay > 500)
                        Logger.LogWarn($"[性能探针] HTTP弹幕事件排队延迟 {delay:F0}ms，处理链路可能积压");
                    OnFetchResponse?.Invoke(this, args);
                }))
            {
                Logger.LogError("弹幕事件队列已满，丢弃一条HTTP弹幕响应");
            }
        }

        /// <summary>
        /// 触发房间状态变更事件
        /// </summary>
        /// <param name="args"></param>
        protected void FireRoomStatusChange(RoomStatusEventArgs args)
        {
            OnRoomStatusChange?.Invoke(this, args);
        }

        /// <summary>
        /// 获取进程名称（带缓存优化）
        /// </summary>
        /// <param name="processID"></param>
        /// <returns></returns>
        protected string GetProcessName(int processID)
        {
            // 先从缓存中查找
            if (_processNameCache.TryGetValue(processID, out var cachedName))
            {
                return cachedName;
            }

            try
            {
                var process = Process.GetProcessById(processID);
                if (process != null)
                {
                    var processName = process.ProcessName;
                    // 缓存进程名称（限制缓存大小，避免内存泄漏）
                    if (_processNameCache.Count < 1000)
                    {
                        _processNameCache.TryAdd(processID, processName);
                    }

                    return processName;
                }
            }
            catch (Exception ex)
            {
                // 进程可能已退出，缓存一个占位符避免重复查询
                var placeholder = $"<{processID}>";
                if (_processNameCache.Count < 1000)
                {
                    _processNameCache.TryAdd(processID, placeholder);
                }

                return placeholder;
            }

            return $"<{processID}>";
        }

        private static string BuildProxyOverride(RegistryKey registry)
        {
            var current = registry.GetValue("ProxyOverride")?.ToString() ?? string.Empty;
            var items = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();

            // 避免 Supabase Realtime 通过本地代理，防止订阅卡住
            var required = new[]
            {
                "<local>",
                "*.supabase.co",
                "*.supabase.net"
            };

            foreach (var item in required)
                if (!items.Contains(item, StringComparer.OrdinalIgnoreCase))
                    items.Add(item);

            return string.Join(";", items);
        }

        public static bool ProxyIsOpen()
        {
            var registry =
                Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
                    true);
            var proxyEnable = registry.GetValue("ProxyEnable");
            if (proxyEnable != null && proxyEnable.ToString() == "1")
            {
                return true;
            }

            return false;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeviceId;
using Newtonsoft.Json;
using Supabase;
using Supabase.Realtime;
using Client = Supabase.Client;
using Timer = System.Timers.Timer;

namespace DanmakuBackend.Cloud
{
    public class DanmakuManager
    {
        private readonly Client _client;
        private readonly RealtimeBroadcast<ClientOnlineBroadcast> _clientBroadcast;
        private readonly RealtimeChannel _clientChannel;
        private readonly RealtimeBroadcast<LicenceShutdownBroadcast> _licenceBroadcast;
        private readonly RealtimeChannel _licenceChannel;
        private readonly string _machineId;
        private readonly long _roomId;

        /// <summary>
        /// 会话ID（格式：machineId:roomId）
        /// </summary>
        public readonly string SessionId;

        private Timer _heartbeatTimer;
        
        /// <summary>
        /// 授权的房间ID缓存集合（用于快速查找）
        /// </summary>
        private readonly HashSet<long> _authorizedRoomIds = new HashSet<long>();

        public DanmakuManager(string accessKey, string roomId)
        {
            _roomId = long.Parse(roomId);
            Logger.LogInfo("正在连接到Danmaku云服务...");
            _client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co", accessKey, new SupabaseOptions
            {
                AutoConnectRealtime = true
            });
            _client.InitializeAsync().Wait();
            _clientChannel = _client.Realtime.Channel("danmaku_apps");
            _clientBroadcast = _clientChannel.Register<ClientOnlineBroadcast>();
            _clientChannel.Subscribe();
            _machineId = new DeviceIdBuilder()
                .OnWindows(windows =>
                    windows
                        .AddWindowsDeviceId()
                        .AddMachineGuid()
                )
                .ToString();
            SessionId = $"{_machineId}:{roomId}";

            // 订阅授权频道
            _licenceChannel = _client.Realtime.Channel($"danmaku-licence-{_machineId}");
            _licenceBroadcast = _licenceChannel.Register<LicenceShutdownBroadcast>();
            _licenceBroadcast.AddBroadcastEventHandler((sender, broadcast) =>
            {
                if (broadcast?.Event == "shutdown") OnLicenceShutdown(broadcast.Payload?["message"]?.ToString());
            });
            _licenceChannel.Subscribe();
            Logger.LogInfo("Danmaku云服务连接成功");
            
            // 初始化时更新房间ID缓存（包含初始的 _roomId）
            UpdateAuthorizedRoomIdsCache();
        }

        /// <summary>
        /// 机器ID
        /// </summary>
        public string MachineId => _machineId;

        /// <summary>
        /// 已验证的客户端 session_id
        /// </summary>
        public string ValidatedSessionId { get; private set; }

        /// <summary>
        ///     当前会话的授权信息
        /// </summary>
        public LicenceInfo LicenceInfo
        {
            get => _licenceInfo;
            private set
            {
                _licenceInfo = value;
                UpdateAuthorizedRoomIdsCache();
            }
        }

        private LicenceInfo _licenceInfo;

        /// <summary>
        /// 更新授权的房间ID缓存
        /// </summary>
        private void UpdateAuthorizedRoomIdsCache()
        {
            _authorizedRoomIds.Clear();
            
            if (_licenceInfo != null)
            {
                // 添加当前授权的房间ID（从 RoomId 字段）
                if (_licenceInfo.RoomId > 0)
                {
                    _authorizedRoomIds.Add(_licenceInfo.RoomId);
                }
                
                // 尝试从 Id 字段解析房间ID（如果 Id 是房间ID的字符串形式）
                if (!string.IsNullOrWhiteSpace(_licenceInfo.Id))
                {
                    if (long.TryParse(_licenceInfo.Id, out var idAsRoomId) && idAsRoomId > 0)
                    {
                        _authorizedRoomIds.Add(idAsRoomId);
                    }
                }
            }
            
            // 保留原有的 _roomId 作为兼容
            if (_roomId > 0)
            {
                _authorizedRoomIds.Add(_roomId);
            }
        }

        /// <summary>
        /// 检查房间是否在授权列表中（使用缓存，避免重复解析）
        /// </summary>
        /// <param name="roomId">房间ID</param>
        /// <returns>是否在授权列表中</returns>
        public bool IsAnchorRoom(long roomId)
        {
            // 使用缓存的房间ID集合进行快速查找，避免重复解析
            return _authorizedRoomIds.Contains(roomId);
        }

        public async void ReportEvent(long roomId, string eventName, object body, string note = null)
        {
            // 仅在调试模式下上传事件
            if (!AppRuntime.IsDebugMode)
            {
                return;
            }

            Logger.LogWarn($@"报告事件<{eventName}> {note}");
            try
            {
                await _client.From<EventLog>().Insert(new EventLog
                {
                    EventName = eventName,
                    RoomId = roomId,
                    Body = body,
                    Timestamp = DateTime.Now,
                    Note = note
                });
            }
            catch (Exception e)
            {
                Logger.LogError(@"报告事件错误: " + e.Message);
            }
        }

        private void OnLicenceShutdown(string reason = null)
        {
            Logger.LogError("未授权，程序即将退出");
            MessageBox.Show(
                $@"原因: {reason ?? "授权已被终止，程序即将退出"}",
                @"未授权",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            AppRuntime.WsServer.Dispose();
            Environment.Exit(0);
        }

        /// <summary>
        /// 验证客户端会话
        /// </summary>
        /// <param name="sessionId">客户端提供的 session_id</param>
        /// <returns>验证是否成功</returns>
        public async Task<bool> ValidateSessionAsync(string sessionId)
        {
            try
            {
                // 调用 Supabase Edge Function 进行验证
                var payload = new Dictionary<string, object>
                {
                    { "machine_id", _machineId },
                    { "room_id", _roomId }
                };

                var payloadJson = JsonConvert.SerializeObject(payload);
                var response = await _client.Functions.Invoke("validate-session-and-get-licence", payloadJson);

                // 处理响应内容：将响应对象序列化为 JSON 字符串
                var responseContent = response is string strResponse 
                    ? strResponse 
                    : JsonConvert.SerializeObject(response);
                
                // 检查响应内容是否为空
                if (string.IsNullOrEmpty(responseContent))
                {
                    Logger.LogError("验证会话失败: 响应内容为空");
                    return false;
                }

                var result = JsonConvert.DeserializeObject<ValidateSessionResponse>(responseContent);

                if (result == null || result.Licence == null)
                {
                    Logger.LogDebug("验证失败: 未找到有效的授权信息");
                    return false;
                }

                // 验证成功，保存 session_id 和授权信息
                // 设置 LicenceInfo 会自动更新房间ID缓存
                ValidatedSessionId = sessionId;
                LicenceInfo = result.Licence;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"验证会话时出错: {e.Message}");
                return false;
            }
        }

        public void Destroy()
        {
            // 安全地停止心跳定时器
            if (_heartbeatTimer != null)
            {
                try
                {
                    _heartbeatTimer.Stop();
                    _heartbeatTimer.Dispose();
                }
                catch (Exception e)
                {
                    Logger.LogError("停止心跳定时器失败: " + e.Message);
                }
                _heartbeatTimer = null;
            }

            // 安全地取消订阅频道
            if (_clientChannel != null)
            {
                try
                {
                    _clientChannel.Unsubscribe();
                }
                catch (Exception e)
                {
                    Logger.LogError("取消订阅频道失败: " + e.Message);
                }
            }

            // 安全地取消订阅授权频道
            if (_licenceChannel != null)
            {
                try
                {
                    _licenceChannel.Unsubscribe();
                }
                catch (Exception e)
                {
                    Logger.LogError("取消订阅授权频道失败: " + e.Message);
                }
            }

            // 关闭 Supabase 客户端连接（异步操作，不等待完成）
            if (_client != null)
            {
                try
                {
                    // 异步关闭，不阻塞退出
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Supabase Realtime 会在客户端 Dispose 时自动关闭
                            // 这里不需要手动断开连接，避免阻塞退出
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"关闭Realtime连接失败: {ex.Message}");
                        }
                    });
                }
                catch (Exception e)
                {
                    Logger.LogError("关闭Supabase客户端失败: " + e.Message);
                }
            }
        }

        /// <summary>
        ///     Edge Function 响应模型
        /// </summary>
        private class ValidateSessionResponse
        {
            [JsonProperty("licence")] public LicenceInfo Licence { get; set; }

            [JsonProperty("error")] public string Error { get; set; }
        }
    }
}
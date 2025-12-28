using System;
using System.Collections.Generic;
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
        public LicenceInfo LicenceInfo { get; private set; }

        public bool IsAnchorRoom(long roomId)
        {
            return _roomId == roomId;
        }

        public async void ReportEvent(long roomId, string eventName, object body, string note = null)
        {
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
                if (response == null)
                {
                    Logger.LogError("验证会话失败: 响应为空");
                    return false;
                }

                // 将响应对象序列化为 JSON 字符串
                var responseContent = JsonConvert.SerializeObject(response);

                // 如果响应是字符串类型，直接使用
                if (response is string strResponse)
                {
                    responseContent = strResponse;
                }
                else if (string.IsNullOrEmpty(responseContent) || responseContent == "null")
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
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
            }

            // 安全地取消订阅频道
            if (_clientChannel != null)
                try
                {
                    _clientChannel.Unsubscribe();
                }
                catch (Exception e)
                {
                    Logger.LogError("取消订阅频道失败: " + e.Message);
                }

            // 安全地取消订阅授权频道
            if (_licenceChannel != null)
                try
                {
                    _licenceChannel.Unsubscribe();
                }
                catch (Exception e)
                {
                    Logger.LogError("取消订阅授权频道失败: " + e.Message);
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
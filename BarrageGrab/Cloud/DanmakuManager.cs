using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeviceId;
using Newtonsoft.Json;
using Supabase;
using Supabase.Realtime;
using Client = Supabase.Client;

namespace DanmakuBackend.Cloud
{
    public class DanmakuManager
    {
        private readonly Client _client;
        private readonly long _roomId;
        private RealtimeBroadcast<ShutdownBroadcast> _machineBroadcast;
        private RealtimeChannel _machineChannel;
        private RealtimeBroadcast<ShutdownBroadcast> _sessionBroadcast;
        private RealtimeChannel _sessionChannel;

        public DanmakuManager(string accessKey, string roomId)
        {
            _roomId = long.Parse(roomId);
            Logger.LogInfo("正在连接到Danmaku服务...");
            _client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co", accessKey, new SupabaseOptions
            {
                AutoConnectRealtime = true
            });
            _client.InitializeAsync().Wait();
            MachineId = new DeviceIdBuilder()
                .OnWindows(windows =>
                    windows
                        .AddWindowsDeviceId()
                        .AddMachineGuid()
                )
                .ToString();
            Logger.LogInfo("Danmaku服务已初始化，等待客户端连接...");
        }

        /// <summary>
        /// 机器ID
        /// </summary>
        public string MachineId { get; }

        /// <summary>
        /// 已验证的客户端 session_id
        /// </summary>
        public string SessionId { get; private set; }

        /// <summary>
        /// 当前会话的授权信息
        /// </summary>
        public LicenceInfo LicenceInfo { get; private set; }

        /// <summary>
        /// 检查房间是否在授权列表中（使用缓存，避免重复解析）
        /// </summary>
        /// <param name="id">房间ID</param>
        /// <returns>是否在授权列表中</returns>
        public bool VerifySession(string wid, string id)
        {
            if (LicenceInfo == null || LicenceInfo.RoomId != wid) return false;
            LicenceInfo.Id = id;
            return true;
        }

        public bool CheckRoomId(string id)
        {
            return LicenceInfo?.Id == id;
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
        /// 连接到云端并验证会话
        /// </summary>
        /// <returns></returns>
        public async Task<bool> ConnectAsync()
        {
            if (SessionId != null) return true;
            if (!await ValidateSession()) return false;

            // 订阅机器频道
            _machineChannel = _client.Realtime.Channel($"danmaku-machine-{MachineId}");
            _machineBroadcast = _machineChannel.Register<ShutdownBroadcast>();
            _machineBroadcast.AddBroadcastEventHandler((sender, broadcast) =>
            {
                if (broadcast?.Event == "shutdown") OnLicenceShutdown(broadcast.Payload?["message"]?.ToString());
            });
            await _machineChannel.Subscribe();

            // 订阅会话频道
            _sessionChannel = _client.Realtime.Channel($"danmaku-session-{SessionId}");
            _sessionBroadcast = _sessionChannel.Register<ShutdownBroadcast>();
            _sessionBroadcast.AddBroadcastEventHandler((sender, broadcast) =>
            {
                if (broadcast?.Event == "shutdown") OnLicenceShutdown(broadcast.Payload?["message"]?.ToString());
            });
            await _sessionChannel.Subscribe();

            Logger.LogInfo("Danmaku服务会话启动成功");
            return true;
        }

        /// <summary>
        /// 验证客户端会话
        /// </summary>
        /// <returns>验证是否成功</returns>
        private async Task<bool> ValidateSession()
        {
            try
            {
                var options = new Supabase.Functions.Client.InvokeFunctionOptions()
                {
                    Body =
                    {
                        { "machine_id", MachineId },
                        { "room_id", _roomId.ToString() }
                    }
                };
                var response = await _client.Functions.Invoke("validate-session-and-get-licence", options: options);
                var session = JsonConvert.DeserializeObject<ValidateSessionResponse>(response);
                if (session == null || session.Licence == null)
                {
                    Logger.LogDebug($"验证失败: {session?.Error ?? "无授权"}");
                    return false;
                }

                // 验证成功，保存 session_id 和授权信息
                SessionId = session.SessionId;
                LicenceInfo = session.Licence;
                Logger.LogDebug($"验证: {SessionId} | {LicenceInfo}");
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"注册会话时出错: {e.Message}");
                return false;
            }
        }

        public void Destroy()
        {
            try
            {
                // 取消订阅频道
                _sessionChannel?.Unsubscribe();
                // 取消订阅授权频道
                _machineChannel?.Unsubscribe();
                // 释放会话
                ReleaseSession().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Logger.LogError("Danmaku服务注销异常: " + e.Message);
            }
        }

        private async Task ReleaseSession()
        {
            if (SessionId == null) return;
            var options = new Supabase.Functions.Client.InvokeFunctionOptions
            {
                Body =
                {
                    { "session_id", SessionId }
                }
            };
            var response = await _client.Functions.Invoke("validate-session-and-get-licence", options: options);
            var res = JsonConvert.DeserializeObject<ReleaseSessionResponse>(response);
            if (res.Error != null) Logger.LogDebug($"释放会话异常: {res?.Error ?? "未知错误"}");
        }

        /// <summary>
        /// 会话验证响应
        /// </summary>
        private class ValidateSessionResponse
        {
            [JsonProperty("licence")] public LicenceInfo Licence { get; set; }
            [JsonProperty("error")] public string Error { get; set; }
            [JsonProperty("session_id")] public string SessionId { get; set; }
        }

        /// <summary>
        /// 会话验证响应
        /// </summary>
        private class ReleaseSessionResponse
        {
            [JsonProperty("error")] public string Error { get; set; }
        }
    }
}
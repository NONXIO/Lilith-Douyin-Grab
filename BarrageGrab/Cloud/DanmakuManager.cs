using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeviceId;
using Supabase;
using Supabase.Realtime;
using Client = Supabase.Client;
using Constants = Supabase.Postgrest.Constants;
using Timer = System.Timers.Timer;

namespace BarrageGrab.Cloud
{
    public class DanmakuManager
    {
        private readonly Client _client;
        private readonly RealtimeBroadcast<ClientOnlineBroadcast> _clientBroadcast;
        private readonly RealtimeChannel _clientChannel;
        private readonly RealtimeBroadcast<LicenceShutdownBroadcast> _licenceBroadcast;
        private readonly RealtimeChannel _licenceChannel;
        private readonly string _machineId;
        private readonly string _roomId;

        /// <summary>
        /// 会话ID（格式：machineId:roomId）
        /// </summary>
        public readonly string SessionId;

        private Timer _heartbeatTimer;

        public DanmakuManager(string accessKey, string roomId)
        {
            _roomId = roomId;
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
                if (broadcast?.Event == "shutdown") OnLicenceShutdown();
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

        public void ReportEvent(string eventName, object body, string note = null)
        {
            Logger.LogInfo($@"报告事件<{eventName}>[{note}]: {body.ToJson()}");
            try
            {
                _client.From<EventLog>().Insert(new EventLog
                {
                    EventName = eventName,
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

        private void OnLicenceShutdown()
        {
            Logger.LogError("收到授权关闭事件，程序即将退出");
            MessageBox.Show(
                @"未授权",
                @"未授权",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
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
                Logger.LogInfo($"验证会话: {sessionId}");

                // 查询 danmaku_online 表
                var thirtySecondsAgo = DateTime.Now.AddSeconds(-30);
                var result = await _client
                    .From<OnlineClient>()
                    .Filter("machine_id", Constants.Operator.Equals, _machineId)
                    .Filter("session_id", Constants.Operator.Equals, sessionId)
                    .Filter("banned", Constants.Operator.Equals, false)
                    .Get();

                if (!result.Models.Any())
                {
                    Logger.LogError("会话验证失败: 未找到匹配记录");
                    return false;
                }

                var client = result.Models.First();

                // 检查 last_online_at 是否在30秒内
                if (client.LastOnlineAt < thirtySecondsAgo)
                {
                    Logger.LogError($"会话验证失败: 上次在线时间过期 ({client.LastOnlineAt})");
                    return false;
                }

                // 验证成功，保存 session_id
                ValidatedSessionId = sessionId;
                Logger.LogInfo($"会话验证成功: {sessionId}");
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
    }
}
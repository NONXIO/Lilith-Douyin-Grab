using System;
using System.Linq;
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
        private readonly string _machineId;
        private readonly string _roomId;
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
            Logger.LogInfo("登记Danmaku信息...");
            _machineId = new DeviceIdBuilder()
                .OnWindows(windows =>
                    windows
                        .AddWindowsDeviceId()
                        .AddMachineGuid()
                )
                .ToString();
            UpdateUserSession();
            UpdateClientStatus(true);
            Logger.LogInfo("Danmaku云服务连接成功");
        }

        private async void UpdateUserSession()
        {
            try
            {
                var oneMinuteAgo = DateTime.Now.AddMinutes(-1);
                var existingClients = await _client
                    .From<OnlineClient>()
                    .Filter("room_id", Constants.Operator.Equals, _roomId)
                    .Filter("machine_id", Constants.Operator.NotEqual, _machineId)
                    .Filter("last_online_at", Constants.Operator.LessThan, oneMinuteAgo.ToString("o"))
                    .Get();
                if (existingClients.Models.Any())
                {
                    MessageBox.Show(
                        @"检测到有另一台设备正在使用相同的房间ID连接到Danmaku云服务。当前设备将无法继续运行。",
                        @"设备冲突",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    Environment.Exit(0);
                }

                await _client.From<OnlineClient>().Upsert(new OnlineClient
                {
                    MachineId = _machineId,
                    RoomId = _roomId,
                    LastOnlineAt = DateTime.Now
                });
            }
            catch (Exception e)
            {
                Logger.LogError("更新用户会话失败: " + e.Message);
            }
        }

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

        private void UpdateClientStatus(bool isOnline)
        {
            _clientBroadcast.Send(isOnline ? "online" : "offline", new ClientOnlineBroadcast
            {
                RoomId = _roomId,
                MachineId = Environment.MachineName,
                OnlineAt = DateTime.Now
            });
        }

        public void Destroy()
        {
            _heartbeatTimer.Stop();
            _clientChannel.Unsubscribe();
            UpdateClientStatus(false);
        }
    }
}
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using DeviceId;
using Supabase;
using Supabase.Realtime;
using Client = Supabase.Client;
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
            Console.WriteLine(@"正在连接到Danmaku云服务...");
            _client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co", accessKey, new SupabaseOptions
            {
                AutoConnectRealtime = true
            });
            _client.InitializeAsync().Wait();
            _clientChannel = _client.Realtime.Channel("danmaku_apps");
            _clientBroadcast = _clientChannel.Register<ClientOnlineBroadcast>();
            _clientChannel.Subscribe().Wait();
            _machineId = new DeviceIdBuilder()
                .OnWindows(windows =>
                    windows
                        .AddWindowsDeviceId()
                        .AddMachineGuid()
                )
                .ToString();
            UpdateUserSession().Wait();
            StartHeartbeat();
            UpdateClientStatus(true);
            Logger.PrintColor("已连接到Danmaku云服务", ConsoleColor.Green);
        }

        private void StartHeartbeat()
        {
            _heartbeatTimer = new Timer(5 * 60 * 1000); // 5 minutes
            _heartbeatTimer.Elapsed += HeartbeatCallback;
            _heartbeatTimer.Start();
        }

        private void HeartbeatCallback(object sender, ElapsedEventArgs e)
        {
            UpdateUserSession().Wait();
        }

        private async Task UpdateUserSession()
        {
            try
            {
                var existingClients = await _client.From<OnlineClient>()
                    .Where(c =>
                        c.RoomId == _roomId &&
                        c.MachineId != _machineId &&
                        c.LastOnlineAt < DateTime.Now.AddMinutes(-1)
                    )
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
            catch (Exception ex)
            {
                Logger.LogError("Failed to register client or send heartbeat: " + ex.Message);
                throw new DanmakuException("无法连接到Danmaku云服务,请检查网络连接");
            }
        }

        public void ReportEvent(string eventName, object body, string note = null)
        {
            Console.WriteLine($@"报告事件<{eventName}>[{note}]: {body.ToJson()}");
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
            }).Wait();
        }

        public void Destroy()
        {
            _heartbeatTimer.Stop();
            _clientChannel.Unsubscribe();
            UpdateClientStatus(false);
        }
    }
}
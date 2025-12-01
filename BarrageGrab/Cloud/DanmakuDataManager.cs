using System;
using BarrageGrab.Modles.JsonEntity;
using Supabase.Realtime;
using Client = Supabase.Client;

namespace BarrageGrab.Cloud
{
    public class DanmakuDataManager
    {
        private readonly Client _client;
        private readonly RealtimeChannel _clientChannel;
        private readonly RealtimeBroadcast<ClientOnlineBroadcast> _clientBoardcast;
        private readonly string _roomId;
        
        public DanmakuDataManager(string accessKey, string roomId)
        {
            this._roomId = roomId;
            Console.WriteLine(@"正在连接到Danmaku云服务...");
           _client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co",accessKey, new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = true
            });
            _client.InitializeAsync().Wait();
            _clientChannel = _client.Realtime.Channel("clients");
            _clientBoardcast = _clientChannel.Register<ClientOnlineBroadcast>();
            _clientChannel.Subscribe().Wait();
            Logger.PrintColor("已连接到Danmaku云服务", ConsoleColor.Green);
        }

        public async void ReportEvent(string eventName, object body)
        {
            try
            {
                var res = await _client.From<EventLog>().Insert(new EventLog()
                {
                    EventName = eventName,
                    Body = body,
                    Timestamp = DateTime.Now
                });
                Console.WriteLine(res.Model != null
                    ? $"Event {eventName} logged successfully."
                    : $@"Failed to log event {eventName}: {res.ResponseMessage?.StatusCode}");
            }
            catch (Exception e)
            {
                Logger.LogError(@"Failed to report event to cloud: " + e.Message);
            }
        }
        
        private void UpdateClientStatus(bool isOnline)
        {
            _clientBoardcast.Send(isOnline ? "online" : "offline", new ClientOnlineBroadcast()
            {
                RoomId = _roomId,
                MachineId = Environment.MachineName,
                OnlineAt = DateTime.Now
            }).Wait();
        }
    }
}
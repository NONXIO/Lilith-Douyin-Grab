using System;
using BarrageGrab.Modles.JsonEntity;
using Supabase;

namespace BarrageGrab.Cloud
{
    public class DanmakuDataManager
    {
        private readonly Client _client;
        public DanmakuDataManager(string accessKey)
        {
            Logger.LogInfo("正在连接到Danmaku云服务..." + accessKey);
           _client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co",accessKey, new Supabase.SupabaseOptions
            {
                AutoConnectRealtime = true
            });
            _client.InitializeAsync().Wait();
            Logger.PrintColor("已连接到Danmaku云服务", ConsoleColor.Green);
        }

        public async void ReportEvent(string eventName, object body)
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
    }
}
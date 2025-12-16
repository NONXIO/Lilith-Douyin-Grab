using System;
using Newtonsoft.Json;
using Supabase.Realtime.Models;

namespace DanmakuBackend.Cloud
{
    public class ClientOnlineBroadcast: BaseBroadcast
    {
        [JsonProperty("room_id")]
        public string RoomId {get; set;}
        [JsonProperty("machine_id")]
        public string MachineId {get; set;}
        [JsonProperty("online_at")]
        public DateTime OnlineAt {get; set;}
    }
}
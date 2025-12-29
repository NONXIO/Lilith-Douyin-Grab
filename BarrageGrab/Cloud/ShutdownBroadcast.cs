using Newtonsoft.Json;
using Supabase.Realtime.Models;

namespace DanmakuBackend.Cloud
{
    public class ShutdownBroadcast : BaseBroadcast
    {
        [JsonProperty("reason")] public string Reason { get; set; }
    }
}
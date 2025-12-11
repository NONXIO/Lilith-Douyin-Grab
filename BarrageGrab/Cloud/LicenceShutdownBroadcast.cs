using Newtonsoft.Json;
using Supabase.Realtime.Models;

namespace BarrageGrab.Cloud
{
    public class LicenceShutdownBroadcast : BaseBroadcast
    {
        [JsonProperty("reason")] public string Reason { get; set; }
    }
}
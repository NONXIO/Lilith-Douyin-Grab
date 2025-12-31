using System;
using Newtonsoft.Json;

namespace DanmakuBackend.Cloud
{
    /// <summary>
    ///     Danmaku 授权信息
    /// </summary>
    public class LicenceInfo
    {
        [JsonProperty("room_id")] public string RoomId { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("expired_at")] public DateTime? ExpiredAt { get; set; }
        [JsonProperty("anchor_name")] public object Cache { get; set; }
    }
}
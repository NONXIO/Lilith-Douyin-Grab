using System;
using Newtonsoft.Json;

namespace DanmakuBackend.Cloud
{
    /// <summary>
    ///     Danmaku 授权信息
    /// </summary>
    public class LicenceInfo
    {
        [JsonProperty("room_id")] public long RoomId { get; set; }

        [JsonProperty("expired_at")] public DateTime? ExpiredAt { get; set; }

        [JsonProperty("create_at")] public DateTime? CreateAt { get; set; }

        [JsonProperty("id")] public string Id { get; set; }

        [JsonProperty("cache")] public object Cache { get; set; }

        [JsonProperty("note")] public string Note { get; set; }
    }
}
using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace BarrageGrab.Cloud
{
    [Table("danmaku_session")]
    public class OnlineClient : BaseModel
    {
        [PrimaryKey("machine_id", false)] public string MachineId { get; set; }

        [Column("room_id")] public string RoomId { get; set; }

        [Column("last_online_at")] public DateTime LastOnlineAt { get; set; }

        [Column("session_id")] public string SessionId { get; set; }

        [Column("banned")] public bool Banned { get; set; }
    }
}
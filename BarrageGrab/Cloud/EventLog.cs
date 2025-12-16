using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace DanmakuBackend.Cloud
{
    [Table("events_logging")]
    class EventLog : BaseModel
    {
        [PrimaryKey("id", false)] public int Id { get; set; }

        [Column("room_id")] public long RoomId { get; set; }

        [Column("event_name")] public string EventName { get; set; }

        [Column("note")] public string Note { get; set; }

        [Column("event_body")] public object Body { get; set; }

        [Column("ts")] public DateTime Timestamp { get; set; }
    }
}
using System;
using BarrageGrab.Modles.JsonEntity;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace BarrageGrab.Cloud
{
    [Table("events_logging")]
    class EventLog : BaseModel
    {
        [PrimaryKey("id", false)]
        public int Id { get; set; }
        [Column("room_id")]
        public string RoomId { get; set; }
        [Column("event_name")]
        public string EventName { get; set; }
        [Column("event_body")]
        public object Body { get; set; }
        [Column("ts")]
        public DateTime Timestamp { get; set; }
    }
}
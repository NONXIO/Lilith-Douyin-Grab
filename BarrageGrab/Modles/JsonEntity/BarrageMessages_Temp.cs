
    public class RoomStatsMsg : Msg
    {
        public string DisplayShort { get; set; }
        public string DisplayMiddle { get; set; }
        public string DisplayLong { get; set; }
        public long DisplayValue { get; set; }
        public long DisplayVersion { get; set; }
        public bool Incremental { get; set; }
        public bool IsHidden { get; set; }
        public long Total { get; set; }
        public long DisplayType { get; set; }

        public override PackMsgType MsgType => PackMsgType.直播间数据;
    }

    public class ActivityEmojiGroupsMsg : Msg
    {
        public List<EffectiveActivityEmojiGroup> ActivityEmojiGroups { get; set; }
        public override PackMsgType MsgType => PackMsgType.活动红心;
    }

    public class RoomRankMsg : Msg
    {
        public List<RoomRank> Ranks { get; set; }
        public override PackMsgType MsgType => PackMsgType.直播间排行榜;
    }

    public class RoomRank
    {
        public MsgUser User { get; set; }
        public string ScoreStr { get; set; }
        public bool ProfileHidden { get; set; }
    }

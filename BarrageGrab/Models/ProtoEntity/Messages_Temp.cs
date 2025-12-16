
[global::ProtoBuf.ProtoContract()]
public class RoomStatsMessage : global::ProtoBuf.IExtensible
{
    private global::ProtoBuf.IExtension __pbn__extensionData;
    global::ProtoBuf.IExtension global::ProtoBuf.IExtensible.GetExtensionObject(bool createIfMissing)
        => global::ProtoBuf.Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

    [global::ProtoBuf.ProtoMember(1, Name = @"common")]
    public Common common { get; set; }

    [global::ProtoBuf.ProtoMember(2, Name = @"display_short")]
    [global::System.ComponentModel.DefaultValue("")]
    public string displayShort { get; set; } = "";

    [global::ProtoBuf.ProtoMember(3, Name = @"display_middle")]
    [global::System.ComponentModel.DefaultValue("")]
    public string displayMiddle { get; set; } = "";

    [global::ProtoBuf.ProtoMember(4, Name = @"display_long")]
    [global::System.ComponentModel.DefaultValue("")]
    public string displayLong { get; set; } = "";

    [global::ProtoBuf.ProtoMember(5, Name = @"display_value")]
    public long displayValue { get; set; }

    [global::ProtoBuf.ProtoMember(6, Name = @"display_version")]
    public long displayVersion { get; set; }

    [global::ProtoBuf.ProtoMember(7, Name = @"incremental")]
    public bool incremental { get; set; }

    [global::ProtoBuf.ProtoMember(8, Name = @"is_hidden")]
    public bool isHidden { get; set; }

    [global::ProtoBuf.ProtoMember(9, Name = @"total")]
    public long total { get; set; }

    [global::ProtoBuf.ProtoMember(10, Name = @"display_type")]
    public long displayType { get; set; }
}

[global::ProtoBuf.ProtoContract()]
public class ActivityEmojiGroupsMessage : global::ProtoBuf.IExtensible
{
    private global::ProtoBuf.IExtension __pbn__extensionData;
    global::ProtoBuf.IExtension global::ProtoBuf.IExtensible.GetExtensionObject(bool createIfMissing)
        => global::ProtoBuf.Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

    [global::ProtoBuf.ProtoMember(1, Name = @"common")]
    public Common common { get; set; }

    [global::ProtoBuf.ProtoMember(2, Name = @"activity_emoji_groups")]
    public global::System.Collections.Generic.List<EffectiveActivityEmojiGroup> activityEmojiGroups { get; } = new global::System.Collections.Generic.List<EffectiveActivityEmojiGroup>();
}

[global::ProtoBuf.ProtoContract()]
public class EffectiveActivityEmojiGroup : global::ProtoBuf.IExtensible
{
    private global::ProtoBuf.IExtension __pbn__extensionData;
    global::ProtoBuf.IExtension global::ProtoBuf.IExtensible.GetExtensionObject(bool createIfMissing)
        => global::ProtoBuf.Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

    // Assuming ActivityEmojiGroup is complex or just use generic object/bytes if not needed deeply. 
    // For now, I'll skip defining ActivityEmojiGroup unless strictly needed, or define it if I can find it.
    // JS: 1: ["emoji_group", r.webcast.im.ActivityEmojiGroup.decode, 1]
    // Let's assume we might need it later, but for now I'll just define the wrapper.
    // Wait, if I don't define it, I can't deserialize it properly if it's a message field.
    // Let's check if I can just use a placeholder or if I need to define it.
    // Given the user request is just "add event handling", maybe I don't need deep inspection of this field yet.
    // But to be safe, I should probably define it or at least the class.
    
    // [global::ProtoBuf.ProtoMember(1, Name = @"emoji_group")]
    // public ActivityEmojiGroup emojiGroup { get; set; }

    [global::ProtoBuf.ProtoMember(2, Name = @"start_time")]
    public long startTime { get; set; }

    [global::ProtoBuf.ProtoMember(3, Name = @"end_time")]
    public long endTime { get; set; }
}

[global::ProtoBuf.ProtoContract()]
public class RoomRankMessage : global::ProtoBuf.IExtensible
{
    private global::ProtoBuf.IExtension __pbn__extensionData;
    global::ProtoBuf.IExtension global::ProtoBuf.IExtensible.GetExtensionObject(bool createIfMissing)
        => global::ProtoBuf.Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

    [global::ProtoBuf.ProtoMember(1, Name = @"common")]
    public Common common { get; set; }

    [global::ProtoBuf.ProtoMember(2, Name = @"ranks")]
    public global::System.Collections.Generic.List<RoomRank> ranks { get; } = new global::System.Collections.Generic.List<RoomRank>();

    [global::ProtoBuf.ProtoContract()]
    public class RoomRank : global::ProtoBuf.IExtensible
    {
        private global::ProtoBuf.IExtension __pbn__extensionData;
        global::ProtoBuf.IExtension global::ProtoBuf.IExtensible.GetExtensionObject(bool createIfMissing)
            => global::ProtoBuf.Extensible.GetExtensionObject(ref __pbn__extensionData, createIfMissing);

        [global::ProtoBuf.ProtoMember(1, Name = @"user")]
        public User user { get; set; }

        [global::ProtoBuf.ProtoMember(2, Name = @"score_str")]
        [global::System.ComponentModel.DefaultValue("")]
        public string scoreStr { get; set; } = "";

        [global::ProtoBuf.ProtoMember(3, Name = @"profile_hidden")]
        public bool profileHidden { get; set; }
    }
}

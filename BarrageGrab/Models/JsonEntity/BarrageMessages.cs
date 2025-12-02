using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace BarrageGrab.Models.JsonEntity
{
    /// <summary>
    /// 弹幕消息类型
    /// </summary>
    public enum PackMsgType
    {
        [Description("无")] 无 = 0,
        [Description("消息")] 弹幕消息 = 1,
        [Description("点赞")] 点赞消息 = 2,
        [Description("进房")] 进直播间 = 3,
        [Description("关注")] 关注消息 = 4,
        [Description("礼物")] 礼物消息 = 5,
        [Description("统计")] 直播间统计 = 6,
        [Description("粉团")] 粉丝团消息 = 7,
        [Description("分享")] 直播间分享 = 8,
        [Description("下播")] 下播 = 9,
        [Description("会员表情")] 会员表情 = 10,
        [Description("会员开通")] 会员开通 = 11,
        [Description("房间数据")] 房间数据 = 12,
        [Description("房间排行")] 房间排行 = 13
    }

    /// <summary>
    /// 粉丝团消息类型
    /// </summary>
    public enum FansclubType
    {
        粉丝团升级 = 1,
        加入粉丝团 = 2,
        灭灯 = 6 //TODO: 待确定
    }

    /// <summary>
    /// 直播间分享目标
    /// </summary>
    public enum ShareType
    {
        未知 = 0,
        微信 = 1,
        朋友圈 = 2,
        微博 = 3,
        QQ空间 = 4,
        QQ = 5,
        抖音好友 = 112
    }

    /// <summary>
    /// 观众的进入方式
    /// </summary>
    public enum EnterType
    {
        正常进入 = 0,
        通过分享进入 = 6,
        //...其他暂时未知
    }

    /// <summary>
    /// 粉丝团消息
    /// </summary>
    public class FansClubInfo
    {
        /// <summary>
        /// 粉丝团名称
        /// </summary>
        [JsonProperty("name")]
        public string ClubName { get; set; }

        /// <summary>
        /// 粉丝团等级，没加入则0
        /// </summary>
        [JsonProperty("level")]
        public int Level { get; set; }
    }

    /// <summary>
    /// 星守护信息
    /// </summary>
    public class StarGuardInfo : FansClubInfo
    {
    }

    /// <summary>
    /// 直播间主播信息
    /// </summary>
    public class RoomAnchorInfo
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        [JsonProperty("uid")]
        public long UserId { get; set; }

        /// <summary>
        /// SecUid
        /// </summary>
        [JsonProperty("sec_uid")]
        public string SecUid { get; set; }

        /// <summary>
        /// 昵称
        /// </summary>
        [JsonProperty("nickname")]
        public string Nickname { get; set; }

        /// <summary>
        /// 头像地址
        /// </summary>
        [JsonProperty("avatar")]
        public string HeadImgUrl { get; set; }
    }

    /// <summary>
    /// 用户弹幕信息
    /// </summary>
    public class MsgUser : RoomAnchorInfo
    {
        /// <summary>
        /// 是否是直播间管理员
        /// </summary>
        [JsonProperty("is_admin")]
        public bool IsAdmin { get; set; }

        /// <summary>
        /// 是否是主播自己
        /// </summary>
        [JsonProperty("is_anchor")]
        public bool IsAnchor { get; set; }

        /// <summary>
        /// 是否是VIP会员
        /// </summary>
        [JsonProperty("is_vip")]
        public bool IsVip { get; set; }

        /// <summary>
        /// ShortId
        /// </summary>
        [JsonProperty("short_id")]
        public long ShortId { get; set; }

        /// <summary>
        /// 自定义ID
        /// </summary>
        [JsonProperty("display_id")]
        public string DisplayId { get; set; }

        /// <summary>
        /// 未知
        /// </summary>
        [JsonProperty("level")]
        public int Level { get; set; }

        /// <summary>
        /// 支付等级
        /// </summary>
        [JsonProperty("pay_level")]
        public int PayLevel { get; set; }

        /// <summary>
        /// 性别 1男 2女
        /// </summary>
        [JsonProperty("gender")]
        public int Gender { get; set; }

        /// <summary>
        /// 粉丝团信息
        /// </summary>
        [JsonProperty("fans_club")]
        public FansClubInfo FansClub { get; set; }

        /// <summary>
        /// 星守护信息
        /// </summary>
        [JsonProperty("star_guard")]
        public StarGuardInfo StarGuard { get; set; }

        /// <summary>
        /// 粉丝数
        /// </summary>
        [JsonProperty("follower_count")]
        public long FollowerCount { get; set; }

        /// <summary>
        ///   关注数
        /// </summary>
        [JsonProperty("following_count")]
        public long FollowingCount { get; set; }

        /// <summary>
        /// 关注状态 0 未关注 1 已关注 2,不明
        /// </summary>
        [JsonProperty("follow_status")]
        public long FollowStatus { get; set; }


        public string GenderToString()
        {
            return Gender == 1 ? "男" : Gender == 2 ? "女" : "妖";
        }
    }

    /// <summary>
    /// 数据包装器
    /// </summary>
    public class BarrageMsgPack
    {
        public BarrageMsgPack()
        {
        }

        public BarrageMsgPack(string data, PackMsgType type, string processName)
        {
            Data = data;
            Type = type;
            ProcessName = processName;
        }

        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("type")]
        public PackMsgType Type { get; set; }

        /// <summary>
        /// 进程名
        /// </summary>
        [JsonProperty("process")]
        public string ProcessName { get; set; }

        /// <summary>
        /// 消息对象
        /// </summary>
        [JsonProperty("data")]
        public string Data { get; set; }
    }

    /// <summary>
    /// 消息基类
    /// </summary>
    public class Msg
    {
        /// <summary>
        /// 弹幕ID
        /// </summary>
        [JsonProperty("mid")]
        public long MsgId { get; set; }

        /// <summary>
        /// 主播简要信息
        /// </summary>
        [JsonProperty("anchor")]
        public RoomAnchorInfo Owner { get; set; }

        /// <summary>
        /// 消息内容
        /// </summary>
        [JsonProperty("content")]
        public string Content { get; set; }

        /// <summary>
        /// 房间号
        /// </summary>
        [JsonProperty("room_id")]
        public string RoomId { get; set; }

        /// <summary>
        /// web直播间ID
        /// </summary>
        [JsonProperty("web_rid")]
        public string WebRoomId { get; set; }

        /// <summary>
        /// 用户数据
        /// </summary>
        [JsonProperty("user")]
        public MsgUser User { get; set; }

        /// <summary>
        /// 消息时间戳(毫秒)
        /// </summary>
        [JsonProperty("ts")]
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 聊天消息
    /// </summary>
    public class ChatMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "chat";
    }

    /// <summary>
    /// 礼物消息
    /// </summary>
    public class GiftMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "gift";

        /// <summary>
        /// 礼物ID
        /// </summary>
        [JsonProperty("gift_id")]
        public long GiftId { get; set; }

        /// <summary>
        /// 礼物名称
        /// </summary>
        [JsonProperty("gift_name")]
        public string GiftName { get; set; }

        /// <summary>
        /// 礼物分组ID
        /// </summary>
        [JsonProperty("group_id")]
        public long GroupId { get; set; }

        /// <summary>
        /// 本次(增量)礼物数量
        /// </summary>
        [JsonProperty("incremental")]
        public long GiftCount { get; set; }

        /// <summary>
        /// 礼物数量(连续的)
        /// </summary>
        [JsonProperty("total")]
        public long RepeatCount { get; set; }

        /// <summary>
        /// 抖币价格
        /// </summary>
        [JsonProperty("diamond")]
        public int DiamondCount { get; set; }

        /// <summary>
        /// 该礼物是否可连击
        /// </summary>
        [JsonProperty("can_combo")]
        public bool Combo { get; set; }

        /// <summary>
        /// 礼物图片地址
        /// </summary>
        [JsonProperty("gift_image")]
        public string ImgUrl { get; set; }

        /// <summary>
        /// 送礼目标(连麦直播间有用)
        /// </summary>
        [JsonProperty("target")]
        public MsgUser ToUser { get; set; }
    }

    /// <summary>
    /// 点赞消息
    /// </summary>
    public class LikeMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "like";

        /// <summary>
        /// 点赞数量
        /// </summary>
        [JsonProperty("count")]
        public long Count { get; set; }

        /// <summary>
        /// 总共点赞数量
        /// </summary>
        [JsonProperty("total")]
        public long Total { get; set; }
    }

    /// <summary>
    /// 直播间统计消息
    /// </summary>
    public class UserSeqMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "stats";

        /// <summary>
        /// 当前直播间用户数量
        /// </summary>
        [JsonProperty("online_count")]
        public long OnlineUserCount { get; set; }

        /// <summary>
        /// 累计直播间用户数量
        /// </summary>
        [JsonProperty("total_viewed")]
        public long TotalUserCount { get; set; }
    }

    /// <summary>
    /// 粉丝团消息
    /// </summary>
    public class FansclubMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "fan";

        /// <summary>
        /// 粉丝团消息类型,升级1，加入2
        /// </summary>
        [JsonProperty("type")]
        public FansclubType Type { get; set; }

        /// <summary>
        /// 粉丝团等级
        /// </summary>
        [JsonProperty("level")]
        public int Level { get; set; }
    }

    /// <summary>
    /// 会员表情消息
    /// </summary>
    public class VipEmojiMsg : Msg
    {
        /// <summary>
        /// 会员表情URL
        /// </summary>
        [JsonProperty("emoji")] public string EmojiUrl;

        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "emoji";
    }

    /// <summary>
    /// 会员购买消息
    /// </summary>
    public class VipBuyMsg : Msg
    {
        /// <summary>
        /// 动作类型，开通或续费
        /// </summary>
        [JsonProperty("action")] public string Action;

        /// <summary>
        /// 会员类型，普通会员或年度会员
        /// </summary>
        [JsonProperty("annual")] public bool IsAnnual;

        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "vip";

        /// <summary>
        /// 会员类型
        /// </summary>
        [JsonProperty("unit")] public string Unit;
    }


    /// <summary>
    /// 来了消息
    /// </summary>
    public class MemberMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "enter";

        /// <summary>
        /// 当前直播间人数
        /// </summary>
        [JsonProperty("online")]
        public long CurrentCount { get; set; }

        /// <summary>
        /// 直播间进入方式，目前已知 0 正常进入，6 通过分享进入
        /// </summary>
        [JsonProperty("enter_type")]
        public long EnterTipType { get; set; }
    }

    /// <summary>
    /// 直播间分享
    /// </summary>
    public class ShareMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "share";

        /// <summary>
        /// 分享目标
        /// </summary>
        [JsonProperty("type")]
        public ShareType ShareType { get; set; }
    }

    public class RoomStatsMsg : Msg
    {
        /// <summary>
        ///  显示数值
        /// </summary>
        [JsonProperty("display")]
        public long DisplayValue { get; set; }

        [JsonProperty("incremental")] public bool Incremental { get; set; }

        [JsonProperty("total")] public long Total { get; set; }
    }

    /// <summary>
    /// 关注消息
    /// </summary>
    public class FollowMsg : Msg
    {
    }

    /// <summary>
    /// 房间排行消息
    /// </summary>
    public class RoomRankMsg : Msg
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("_type")] public string MsgType = "rank";

        [JsonProperty("ranks")] public List<RoomRank> Ranks { get; set; }
    }

    /// <summary>
    /// 房间排行项
    /// </summary>
    public class RoomRank
    {
        [JsonProperty("user")] public MsgUser User { get; set; }

        [JsonProperty("score")] public long ScoreStr { get; set; }
    }
}
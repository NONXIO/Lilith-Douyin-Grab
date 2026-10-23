using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DanmakuBackend.Models.ProtoEntity;
using DanmakuBackend.Proxy;
using DanmakuBackend.Proxy.ProxyEventArgs;
using ProtoBuf;

namespace DanmakuBackend.Server
{
    /// <summary>
    /// 本机Wss弹幕抓取器
    /// </summary>
    public class WssBarrageGrab : IDisposable
    {
        private AppSetting appsetting = AppSetting.Current;

        //用于缓存接收过的消息ID，判断是否重复接收
        private readonly FixedMessageWindow messageWindow = new FixedMessageWindow();
        private static readonly HashSet<string> knownMethods = new HashSet<string> { "WebcastMemberMessage", "WebcastSocialMessage", "WebcastChatMessage", "WebcastHotChatMessage", "WebcastLikeMessage", "WebcastGiftMessage", "WebcastRoomUserSeqMessage", "WebcastControlMessage", "WebcastFansclubMessage", "WebcastRoomStatsMessage", "WebcastRoomRankMessage", "WebcastEmojiChatMessage", "WebcastAudioChatMessage", "WebcastRoomMessage", "WebcastExhibitionChatMessage", "WebcastRoomIntroMessage", "WebcastLowPcuGuideMessage", "WebcastLowPcuGuideChatMessage", "WebcastRoomDataSyncMessage", "WebcastInRoomBannerMessage", "WebcastRoomStreamAdaptationMessage", "WebcastHotRoomMessage", "WebcastRanklistHourEntranceMessage", "WebcastGiftSortMessage", "WebcastInRoomBannerRefreshMessage", "WebcastPrivilegeScreenChatMessage", "WebcastBindingGiftMessage", "WebcastAssetEffectUtilMessage", "WebcastRoomIndicatorMessage", "WebcastTaskCenterEntranceMessage", "WebcastResidentGuestMessage", "WebcastGiftEffectGameMessage", "WebcastChatLikeMessage", "WebcastGrowthTaskMessage", "WebcastLotteryEventNewMessage", "WebcastLotteryEventMessage", "WebcastActivityEmojiGroupsMessage", "WebcastRoomCommentTopicMessage" };
        private volatile bool disposed;
        private readonly CaptureErrorThrottle captureErrorThrottle = new CaptureErrorThrottle();

        //ISystemProxy proxy = new FiddlerProxy();
        ISystemProxy proxy = new TitaniumProxy();

        public WssBarrageGrab()
        {
            proxy.OnWebSocketData += Proxy_OnWebSocketData;
            proxy.OnFetchResponse += Proxy_OnFetchResponse;
            proxy.OnRoomStatusChange += Proxy_OnRoomStatusChange;
        }

        /// <summary>
        /// 代理
        /// </summary>
        public ISystemProxy Proxy => proxy;
        public int MessageTypeCount => messageWindow.Count;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            proxy.OnWebSocketData -= Proxy_OnWebSocketData;
            proxy.OnFetchResponse -= Proxy_OnFetchResponse;
            proxy.OnRoomStatusChange -= Proxy_OnRoomStatusChange;
            proxy.Dispose();
            messageWindow.Clear();
        }

        private void Proxy_OnRoomStatusChange(object sender, RoomStatusEventArgs e)
        {
            OnRoomStatusChange?.Invoke(this, e);
        }

        /// <summary>
        /// 进入直播间
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<MemberMessage>> OnMemberMessage;

        /// <summary>
        /// 关注
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<SocialMessage>> OnSocialMessage;

        /// <summary>
        /// 聊天
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<ChatMessage>> OnChatMessage;

        /// <summary>
        ///     高热聊天
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<HotChatMessage>> OnHotChatMessage;

        /// <summary>
        /// 聊天
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<AudioChatMessage>> OnAudioChatMessage;

        /// <summary>
        /// 点赞
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<LikeMessage>> OnLikeMessage;

        /// <summary>
        /// 礼物
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<GiftMessage>> OnGiftMessage;

        /// <summary>
        /// 直播间统计
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<RoomUserSeqMessage>> OnRoomUserSeqMessage;

        /// <summary>
        /// 直播间状态变更
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<ControlMessage>> OnControlMessage;

        /// <summary>
        /// 粉丝团消息
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<FansclubMessage>> OnFansclubMessage;

        /// <summary>
        /// 直播间统计
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<RoomStatsMessage>> OnRoomStatsMessage;

        /// <summary>
        /// 直播间排行榜
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<RoomRankMessage>> OnRoomRankMessage;

        /// <summary>
        /// 表情消息
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<EmojiChatMessage>> OnEmojiChatMessage;

        /// <summary>
        /// 房间消息
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<RoomMessage>> OnRoomMessage;

        /// <summary>
        /// 房间状态变更
        /// </summary>
        public event EventHandler<RoomStatusEventArgs> OnRoomStatusChange;

        public void Start()
        {
            proxy.Start();
        }


        //gzip解压缩
        private byte[] Decompress(byte[] zippedData)
        {
            return BoundedCapturePayload.Decompress(zippedData);
        }

        //ws数据处理
        private void Proxy_OnWebSocketData(object sender, WsMessageEventArgs e)
        {
            if (!appsetting.ProcessFilter.Contains(e.ProcessName)) return;
            var buff = e.Payload;
            if (buff == null || buff.Length == 0) return;
            //如果需要Gzip解压缩，但是开头字节不符合Gzip特征字节 则不处理
            if (e.NeedDecompress && buff[0] != 0x08) return;

            try
            {
                BoundedCapturePayload.Validate(buff);
                var enty = DeserializeProto<WssResponse>(buff);
                if (enty == null) return;

                //检测包格式
                if (enty.Headers == null || !enty.Headers.Any(a => a.Key == "compress_type" && a.Value == "gzip")) return;

                byte[] allBuff;
                //解压gzip
                allBuff = e.NeedDecompress ? Decompress(enty.Payload) : enty.Payload;
                var response = DeserializeProto<Response>(allBuff);
                response?.Messages?.ForEach(f => DoMessage(f, e.ProcessName, e.CaptureGeneration));
            }
            catch (Exception ex)
            {
                LogCaptureError(ex);
            }
        }

        //http 数据处理
        private void Proxy_OnFetchResponse(object sender, HttpResponseEventArgs e)
        {
            var payload = e.Payload;
            if (payload == null || payload.Length == 0) return;
            try
            {
                var response = DeserializeProto<Response>(payload);
                response?.Messages?.ForEach(f => { DoMessage(f, e.ProcessName); });
            }
            catch (Exception ex) { LogCaptureError(ex); }
        }

        private void LogCaptureError(Exception ex)
        {
            if (captureErrorThrottle.ShouldLog())
                Logger.LogWarn("抓取载荷拒绝（重复错误按10秒限频）: " + ex.GetType().Name + ": " + ex.Message);
        }

        //发送事件
        private void DoMessage(Message msg, string processName, long captureGeneration = 0)
        {
            if (disposed || msg == null || !knownMethods.Contains(msg.Method)) return;
            if (!messageWindow.Accept(msg.Method, msg.msgId, AppRuntime.DanmakuManager?.ActiveRoomId ?? "", captureGeneration)) return;
            try
            {
                switch (msg.Method)
                {
                    //来了
                    case "WebcastMemberMessage":
                    {
                        var arg = DeserializeProto<MemberMessage>(msg.Payload);
                        OnMemberMessage?.Invoke(this, new RoomMessageEventArgs<MemberMessage>(processName, arg));
                        break;
                    }
                    //关注
                    case "WebcastSocialMessage":
                    {
                        var arg = DeserializeProto<SocialMessage>(msg.Payload);
                        OnSocialMessage?.Invoke(this, new RoomMessageEventArgs<SocialMessage>(processName, arg));
                        break;
                    }
                    //消息
                    case "WebcastChatMessage":
                    {
                        var arg = DeserializeProto<ChatMessage>(msg.Payload);
                        OnChatMessage?.Invoke(this, new RoomMessageEventArgs<ChatMessage>(processName, arg));
                        break;
                    }
                    case "WebcastHotChatMessage":
                    {
                        var arg = DeserializeProto<HotChatMessage>(msg.Payload);
                        OnHotChatMessage?.Invoke(this, new RoomMessageEventArgs<HotChatMessage>(processName, arg));
                        break;
                    }
                    //点赞
                    case "WebcastLikeMessage":
                    {
                        var arg = DeserializeProto<LikeMessage>(msg.Payload);
                        OnLikeMessage?.Invoke(this, new RoomMessageEventArgs<LikeMessage>(processName, arg));
                        break;
                    }
                    //礼物
                    case "WebcastGiftMessage":
                    {
                        var arg = DeserializeProto<GiftMessage>(msg.Payload);
                        OnGiftMessage?.Invoke(this, new RoomMessageEventArgs<GiftMessage>(processName, arg));
                        break;
                    }
                    //直播间统计
                    case "WebcastRoomUserSeqMessage":
                    {
                        var arg = DeserializeProto<RoomUserSeqMessage>(msg.Payload);
                        OnRoomUserSeqMessage?.Invoke(this,
                            new RoomMessageEventArgs<RoomUserSeqMessage>(processName, arg));
                        break;
                    }
                    //直播间状态变更
                    case "WebcastControlMessage":
                    {
                        var arg = DeserializeProto<ControlMessage>(msg.Payload);
                        OnControlMessage?.Invoke(this, new RoomMessageEventArgs<ControlMessage>(processName, arg));
                        break;
                    }
                    //粉丝团消息
                    case "WebcastFansclubMessage":
                    {
                        var arg = DeserializeProto<FansclubMessage>(msg.Payload);
                        OnFansclubMessage?.Invoke(this,
                            new RoomMessageEventArgs<FansclubMessage>(processName, arg));
                        break;
                    }
                    //直播间统计
                    case "WebcastRoomStatsMessage":
                    {
                        var arg = DeserializeProto<RoomStatsMessage>(msg.Payload);
                        OnRoomStatsMessage?.Invoke(this,
                            new RoomMessageEventArgs<RoomStatsMessage>(processName, arg));
                        break;
                    }
                    //直播间排行榜
                    case "WebcastRoomRankMessage":
                    {
                        var arg = DeserializeProto<RoomRankMessage>(msg.Payload);
                        OnRoomRankMessage?.Invoke(this,
                            new RoomMessageEventArgs<RoomRankMessage>(processName, arg));
                        break;
                    }
                    //表情消息
                    case "WebcastEmojiChatMessage":
                    {
                        var arg = DeserializeProto<EmojiChatMessage>(msg.Payload);
                        OnEmojiChatMessage?.Invoke(this,
                            new RoomMessageEventArgs<EmojiChatMessage>(processName, arg));
                        break;
                    }
                    //语音消息
                    case "WebcastAudioChatMessage":
                    {
                        var arg = DeserializeProto<AudioChatMessage>(msg.Payload);
                        OnAudioChatMessage?.Invoke(this, new RoomMessageEventArgs<AudioChatMessage>(processName, arg));
                        break;
                    }
                    // 房间通知消息，包含 会员开通信息
                    case "WebcastRoomMessage":
                    {
                        var arg = DeserializeProto<RoomMessage>(msg.Payload);
                        OnRoomMessage?.Invoke(this, new RoomMessageEventArgs<RoomMessage>(processName, arg));
                        break;
                    }
                    // 展馆聊天消息
                    case "WebcastExhibitionChatMessage":
                    {
                        break;
                        var arg = DeserializeProto<ExhibitionChatMessage>(msg.Payload);
                        var message = new RoomMessageEventArgs<ExhibitionChatMessage>(processName, arg).Message;
                    }
                    /* 无关事件 */
                    case "WebcastRoomIntroMessage":
                    case "WebcastLowPcuGuideMessage":
                    case "WebcastLowPcuGuideChatMessage":
                    case "WebcastRoomDataSyncMessage":
                    case "WebcastInRoomBannerMessage":
                    case "WebcastRoomStreamAdaptationMessage":
                    case "WebcastHotRoomMessage":
                    case "WebcastRanklistHourEntranceMessage":
                    case "WebcastGiftSortMessage":
                    case "WebcastInRoomBannerRefreshMessage":
                    case "WebcastPrivilegeScreenChatMessage":
                    case "WebcastBindingGiftMessage":
                    case "WebcastAssetEffectUtilMessage":
                    case "WebcastRoomIndicatorMessage":
                    case "WebcastTaskCenterEntranceMessage":
                    /* 未来可能需要处理的事件 */
                    case "WebcastResidentGuestMessage": // 常驻嘉宾事件
                    case "WebcastGiftEffectGameMessage": // 礼物特效小游戏事件
                    case "WebcastChatLikeMessage": // 聊天点赞事件?
                    case "WebcastGrowthTaskMessage": // 成长任务事件
                    case "WebcastLotteryEventNewMessage": // 新抽奖事件
                    case "WebcastLotteryEventMessage": // 新抽奖事件
                    case "WebcastActivityEmojiGroupsMessage": // 活动表情包消息
                    case "WebcastRoomCommentTopicMessage": //
                        break; //不处理
                    default:
                        if (AppRuntime.IsDebugMode) Logger.LogInfo("未处理的消息类型:" + msg.Method);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"处理消息<{msg.Method}>时出错:" + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private T DeserializeProto<T>(byte[] data)
        {
            BoundedCapturePayload.Validate(data);
            using (var ms = new MemoryStream(data))
            {
                return Serializer.Deserialize<T>(ms);
            }
        }

        public class RoomMessageEventArgs<T> : EventArgs where T : class
        {
            public RoomMessageEventArgs()
            {
            }

            public RoomMessageEventArgs(string process, T data)
            {
                Process = process;
                Message = data;
            }

            /// <summary>
            /// 进程名
            /// </summary>
            public string Process { get; set; }

            /// <summary>
            /// 消息
            /// </summary>
            public T Message { get; set; }
        }
    }
}

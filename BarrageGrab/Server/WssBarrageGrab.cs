using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BarrageGrab.Models.ProtoEntity;
using BarrageGrab.Proxy;
using BarrageGrab.Proxy.ProxyEventArgs;
using ProtoBuf;

namespace BarrageGrab
{
    /// <summary>
    /// 本机Wss弹幕抓取器
    /// </summary>
    public class WssBarrageGrab : IDisposable
    {
        private AppSetting appsetting = AppSetting.Current;

        //用于缓存接收过的消息ID，判断是否重复接收
        private Dictionary<string, List<long>> msgDic = new Dictionary<string, List<long>>();

        //ISystemProxy proxy = new FiddlerProxy();
        ISystemProxy proxy = new TitaniumProxy();

        public WssBarrageGrab()
        {
            proxy.OnWebSocketData += Proxy_OnWebSocketData;
            proxy.OnFetchResponse += Proxy_OnFetchResponse;
            proxy.OnRoomStatusChange += Proxy_OnRoomStatusChange;
        }

        private void Proxy_OnRoomStatusChange(object sender, RoomStatusEventArgs e)
        {
            OnRoomStatusChange?.Invoke(this, e);
        }

        /// <summary>
        /// 代理
        /// </summary>
        public ISystemProxy Proxy => proxy;

        public void Dispose()
        {
            proxy.Dispose();
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
            MemoryStream ms = new MemoryStream(zippedData);
            GZipStream compressedzipStream = new GZipStream(ms, CompressionMode.Decompress);
            MemoryStream outBuffer = new MemoryStream();
            byte[] block = new byte[1024];
            while (true)
            {
                int bytesRead = compressedzipStream.Read(block, 0, block.Length);
                if (bytesRead <= 0)
                    break;
                else
                    outBuffer.Write(block, 0, bytesRead);
            }

            compressedzipStream.Close();
            return outBuffer.ToArray();
        }

        //ws数据处理
        private void Proxy_OnWebSocketData(object sender, WsMessageEventArgs e)
        {
            if (!appsetting.ProcessFilter.Contains(e.ProcessName)) return;
            var buff = e.Payload;
            if (buff.Length == 0) return;
            //如果需要Gzip解压缩，但是开头字节不符合Gzip特征字节 则不处理
            if (e.NeedDecompress && buff[0] != 0x08) return;

            try
            {
                var enty = Serializer.Deserialize<WssResponse>(new ReadOnlyMemory<byte>(buff));
                if (enty == null) return;

                //检测包格式
                if (!enty.Headers.Any(a => a.Key == "compress_type" && a.Value == "gzip")) return;

                byte[] allBuff;
                //解压gzip
                allBuff = e.NeedDecompress ? Decompress(enty.Payload) : enty.Payload;
                var response = Serializer.Deserialize<Response>(new ReadOnlyMemory<byte>(allBuff));


                response.Messages.ForEach(f => DoMessage(f, e.ProcessName));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"处理弹幕数据包时出错:{ex.Message}");
            }
        }

        //http 数据处理
        private void Proxy_OnFetchResponse(object sender, HttpResponseEventArgs e)
        {
            var payload = e.Payload;

            if (payload == null || payload.Length == 0) return;

            var response = Serializer.Deserialize<Response>(new ReadOnlyMemory<byte>(payload));

            response.Messages.ForEach(f => { DoMessage(f, e.ProcessName); });
        }

        //发送事件
        private void DoMessage(Message msg, string processName)
        {
            List<long> msgIdList;
            if (msgDic.TryGetValue(msg.Method, out var value))
            {
                msgIdList = value;
            }
            else
            {
                msgIdList = new List<long>(320);
                msgDic.Add(msg.Method, msgIdList);
            }

            if (msgIdList.Contains(msg.msgId))
            {
                return;
            }

            msgIdList.Add(msg.msgId);
            //每种消息类型设置300容量应该足够,不太可能存在一条消息被挤出队列后再次出现
            while (msgIdList.Count > 300)
            {
                msgIdList.RemoveAt(0);
            }

            try
            {
                switch (msg.Method)
                {
                    //来了
                    case "WebcastMemberMessage":
                    {
                        var arg = Serializer.Deserialize<MemberMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnMemberMessage?.Invoke(this, new RoomMessageEventArgs<MemberMessage>(processName, arg));
                        break;
                    }
                    //关注
                    case "WebcastSocialMessage":
                    {
                        var arg = Serializer.Deserialize<SocialMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnSocialMessage?.Invoke(this, new RoomMessageEventArgs<SocialMessage>(processName, arg));
                        break;
                    }
                    //消息
                    case "WebcastChatMessage":
                    {
                        var arg = Serializer.Deserialize<ChatMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnChatMessage?.Invoke(this, new RoomMessageEventArgs<ChatMessage>(processName, arg));
                        break;
                    }
                    //点赞
                    case "WebcastLikeMessage":
                    {
                        var arg = Serializer.Deserialize<LikeMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnLikeMessage?.Invoke(this, new RoomMessageEventArgs<LikeMessage>(processName, arg));
                        break;
                    }
                    //礼物
                    case "WebcastGiftMessage":
                    {
                        var arg = Serializer.Deserialize<GiftMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnGiftMessage?.Invoke(this, new RoomMessageEventArgs<GiftMessage>(processName, arg));
                        break;
                    }
                    //直播间统计
                    case "WebcastRoomUserSeqMessage":
                    {
                        var arg = Serializer.Deserialize<RoomUserSeqMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnRoomUserSeqMessage?.Invoke(this,
                            new RoomMessageEventArgs<RoomUserSeqMessage>(processName, arg));
                        break;
                    }
                    //直播间状态变更
                    case "WebcastControlMessage":
                    {
                        var arg = Serializer.Deserialize<ControlMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnControlMessage?.Invoke(this, new RoomMessageEventArgs<ControlMessage>(processName, arg));
                        break;
                    }
                    //粉丝团消息
                    case "WebcastFansclubMessage":
                    {
                        var arg = Serializer.Deserialize<FansclubMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnFansclubMessage?.Invoke(this,
                            new RoomMessageEventArgs<FansclubMessage>(processName, arg));
                        break;
                    }
                    //直播间统计
                    case "WebcastRoomStatsMessage":
                    {
                        var arg = Serializer.Deserialize<RoomStatsMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnRoomStatsMessage?.Invoke(this,
                            new RoomMessageEventArgs<RoomStatsMessage>(processName, arg));
                        break;
                    }
                    //直播间排行榜
                    case "WebcastRoomRankMessage":
                    {
                        var arg = Serializer.Deserialize<RoomRankMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnRoomRankMessage?.Invoke(this,
                            new RoomMessageEventArgs<RoomRankMessage>(processName, arg));
                        break;
                    }
                    //表情消息
                    case "WebcastEmojiChatMessage":
                    {
                        var arg = Serializer.Deserialize<EmojiChatMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnEmojiChatMessage?.Invoke(this,
                            new RoomMessageEventArgs<EmojiChatMessage>(processName, arg));
                        break;
                    }
                    //语音消息
                    case "WebcastAudioChatMessage":
                    {
                        var arg = Serializer.Deserialize<AudioChatMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        var message = new RoomMessageEventArgs<AudioChatMessage>(processName, arg).Message;
                        AppRuntime.DanmakuManager.ReportEvent(message.Common.roomId, message.Common.Method,
                            message.ToJson(), "语音消息");
                        break;
                    }
                    // 房间通知消息，包含 会员开通信息
                    case "WebcastRoomMessage":
                    {
                        var arg = Serializer.Deserialize<RoomMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        OnRoomMessage?.Invoke(this, new RoomMessageEventArgs<RoomMessage>(processName, arg));
                        break;
                    }
                    // 展馆聊天消息
                    case "WebcastExhibitionChatMessage":
                    {
                        var arg = Serializer.Deserialize<ExhibitionChatMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                        var message = new RoomMessageEventArgs<ExhibitionChatMessage>(processName, arg).Message;
                        AppRuntime.DanmakuManager.ReportEvent(message.Common.roomId, message.Common.Method,
                            message.ToJson(), "展馆聊天消息");
                        break;
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
                    /* 未来可能需要处理的事件 */
                    case "WebcastHotChatMessage":
                    case "WebcastResidentGuestMessage": // 常驻嘉宾事件
                    case "WebcastGiftEffectGameMessage": // 礼物特效小游戏事件
                    case "WebcastChatLikeMessage": // 聊天点赞事件?
                    case "WebcastGrowthTaskMessage": // 成长任务事件
                    case "WebcastLotteryEventNewMessage": // 新抽奖事件
                    case "WebcastLotteryEventMessage": // 新抽奖事件
                    case "WebcastActivityEmojiGroupsMessage": // 活动表情包消息
                        break; //不处理
                    default:
                        Logger.LogInfo("未处理的消息类型:" + msg.Method);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"处理消息<{msg.Method}>时出错:" + ex.Message + "\n" + ex.StackTrace);
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
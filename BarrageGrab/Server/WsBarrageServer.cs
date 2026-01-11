using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using DanmakuBackend.Models;
using DanmakuBackend.Models.JsonEntity;
using DanmakuBackend.Models.ProtoEntity;
using DanmakuBackend.Proxy.ProxyEventArgs;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DanmakuBackend.Server
{
    /// <summary>
    /// WsBarrageServer 包装消息事件委托
    /// </summary>
    public delegate void PackMessageEventHandler(WsBarrageServer sender, WsBarrageServer.PackMsgEventArgs e);

    /// <summary>
    /// 弹幕服务 
    /// </summary>
    public class WsBarrageServer : IDisposable
    {
        private static int printCount = 0; //控制台输出计数，用于判断清理控制台
        private AppSetting Appsetting = AppSetting.Current; //全局配置文件实例
        private Timer dieout = new Timer(10000); //离线客户端清理计时器

        private ConcurrentDictionary<string, Tuple<int, DateTime>> giftCountCache =
            new ConcurrentDictionary<string, Tuple<int, DateTime>>(); //礼物计数缓存

        private Timer giftCountTimer = new Timer(10000); //礼物缓存清理计时器
        private WssBarrageGrab grab = new WssBarrageGrab(); //弹幕解析器核心

        private ConcurrentDictionary<string, UserState>
            socketList = new ConcurrentDictionary<string, UserState>(); //客户端列表

        private WebSocketServer socketServer; //Ws服务器对象

        public WsBarrageServer()
        {
            // 配置 Fleck 日志输出
            FleckLog.Level = LogLevel.Debug;
            var socket = new WebSocketServer($"ws://0.0.0.0:{Appsetting.WsProt}");
            socket.RestartAfterListenError = true; //异常重启

            dieout.Elapsed += Dieout_Elapsed;
            giftCountTimer.Elapsed += GiftCountTimer_Elapsed;

            this.grab.OnChatMessage += Grab_OnChatMessage;
            this.grab.OnAudioChatMessage += Grab_OnAudioChatMessage;
            this.grab.OnLikeMessage += Grab_OnLikeMessage;
            this.grab.OnMemberMessage += Grab_OnMemberMessage;
            this.grab.OnSocialMessage += Grab_OnSocialMessage;
            this.grab.OnSocialMessage += Grab_OnShardMessage;
            this.grab.OnGiftMessage += Grab_OnGiftMessage;
            this.grab.OnRoomUserSeqMessage += Grab_OnRoomUserSeqMessage;
            this.grab.OnFansclubMessage += Grab_OnFansclubMessage;
            this.grab.OnControlMessage += Grab_OnControlMessage;
            this.grab.OnRoomStatsMessage += Grab_OnRoomStatsMessage;
            this.grab.OnRoomRankMessage += Grab_OnRoomRankMessage;
            this.grab.OnRoomMessage += Grab_OnRoomMessage;
            this.grab.OnEmojiChatMessage += Grab_OnEmojiMessage;
            this.grab.OnRoomStatusChange += Grab_OnRoomStatusChange;

            this.socketServer = socket;
            //dieout.Start();
            giftCountTimer.Start();
        }

        private UserState Client { get; set; }

        /// <summary>
        /// WS服务器启动地址
        /// </summary>
        public string ServerLocation => socketServer.Location;

        /// <summary>
        /// 数据内核
        /// </summary>
        public WssBarrageGrab Grab => grab;

        /// <summary>
        /// 是否已经释放资源
        /// </summary>
        public bool IsDisposed { get; private set; } = false;

        /// <summary>
        /// 关闭服务器连接，并关闭系统代理
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            // 停止定时器
            try
            {
                giftCountTimer?.Stop();
                giftCountTimer?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"停止礼物计数定时器失败: {ex.Message}");
            }

            try
            {
                dieout?.Stop();
                dieout?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"停止心跳定时器失败: {ex.Message}");
            }

            try
            {
                if (Client?.AuthTimer != null)
                {
                    Client.AuthTimer.Stop();
                    Client.AuthTimer.Dispose();
                }

                Client?.Socket.Close();
                Client = null;
            }
            catch (Exception ex)
            {
                Logger.LogError($"关闭客户端连接失败: {ex.Message}");
            }

            // 释放资源
            try
            {
                socketServer?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"释放WebSocket服务器失败: {ex.Message}");
            }

            try
            {
                grab?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"释放弹幕解析器失败: {ex.Message}");
            }

            OnClose?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 控制台打印事件
        /// </summary>
        public event EventHandler<PrintEventArgs> OnPrint;

        /// <summary>
        /// 服务关闭后触发
        /// </summary>
        public event EventHandler OnClose;

        //礼物缓存清理计时器回调
        private void GiftCountTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            var timeOutKeys = giftCountCache.Where(w => w.Value.Item2 < now.AddSeconds(-10) || w.Value == null)
                .Select(s => s.Key).ToList();

            //淘汰过期的礼物计数缓存
            lock (giftCountCache)
            {
                timeOutKeys.ForEach(key =>
                {
                    Tuple<int, DateTime> _;
                    giftCountCache.TryRemove(key, out _);
                });
            }
        }

        //心跳淘汰计时器回调
        private void Dieout_Elapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            if (Client != null && Client.LastPing.AddSeconds(dieout.Interval * 3) < now)
            {
                try
                {
                    Client.Socket.Close();
                    Client = null;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"清理超时客户端时出错: {ex.Message}");
                }
            }
        }

        //判断Rommid是否符合拦截规则
        private static bool CheckRoomId(long roomid)
        {
            //TODO: 移除
            return true || AppRuntime.DanmakuManager.CheckRoomId(roomid.ToString());
        }

        //解析用户
        private static MsgUser GetUser(User data)
        {
            if (data == null) return null;
            var user = new MsgUser
            {
                Id = data.Id.ToString(),
                DisplayId = data.displayId,
                ShortId = data.shortId,
                Gender = data.Gender,
                UserId = data.Id,
                Level = data.Level,
                Nickname = data.Nickname ?? "用户" + data.displayId,
                HeadImgUrl = data.avatarThumb?.urlList?.FirstOrDefault() ?? "",
                SecUid = data.Sec_uid,
                FollowerCount = data.followInfo?.followerCount ?? -1,
                FollowingCount = data.followInfo?.followingCount ?? -1,
                FollowStatus = data.followInfo?.followStatus ?? -1,
            };

            // Parse badgeImageListV2
            if (data.badgeImageListV2 != null)
            {
                foreach (var badge in data.badgeImageListV2)
                {
                    // Pay Grade (imageType 59)
                    if (badge.imageType == 1)
                        user.Pay = new PayGradeInfo
                        {
                            Icon = badge.urlList.First(),
                            Level = (int)(data.payGrade?.Level ?? -1)
                        };

                    // VIP (imageType 59)
                    if (badge.imageType == 59)
                    {
                        user.Vip = new VipSubscribeInfo
                        {
                            Icon = badge.urlList.First(),
                            Yearly = badge.Uri.Contains("yearly")
                        };
                    }

                    //fansclub
                    if (badge.imageType == 7)
                    {
                        user.FansClub = new FansClubInfo
                        {
                            Level = (int)badge.Content.Level,
                            Lighted = badge.urlList.First()?.Contains("gray") ?? false,
                            Icon = badge.urlList.First()
                        };
                    }

                    // StarGuard (imageType 51)
                    else if (badge.imageType == 51 && badge.Uri.Contains("star_guard"))
                    {
                        user.StarGuard = new StarGuardInfo
                        {
                            Level = (int)badge.Content.Level,
                            ClubName = badge.Content.Name,
                            Icon = badge.urlList.First()
                        };
                    }
                }
            }

            return user;
        }

        //检查属性定义
        private static bool HasProperty(dynamic obj, string propertyName)
        {
            if (obj is ExpandoObject)
            {
                return ((IDictionary<string, object>)obj).ContainsKey(propertyName);
            }

            return obj.GetType().GetProperty(propertyName) != null;
        }

        //创建消息对象
        private static T CreateMsg<T>(dynamic msg, MsgUser user = null) where T : Msg, new()
        {
            var roomid = msg.Common.roomId.ToString();
            RoomInfo roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(roomid);
            //判断 Common 属性是否存在
            var hasUser = HasProperty(msg, nameof(msg.User));
            var enty = new T()
            {
                MsgId = msg.Common?.msgId,
                RoomId = roomid,
                WebRoomId = roomInfo?.WebRoomId ?? "",
                User = hasUser ? GetUser(msg.User) : user
            };
            //判断是否是直播间管理员
            if (enty.User != null && roomInfo != null && roomInfo.AdminUserIds.Any())
            {
                enty.User.IsAdmin = enty.User.IsAdmin || roomInfo.AdminUserIds.Contains(enty.User.UserId.ToString());
            }

            //判断是否是主播
            if (enty.User != null && roomInfo != null && roomInfo.Owner != null)
            {
                enty.User.IsAnchor = enty.User.IsAnchor || enty.User.UserId.ToString() == roomInfo.Owner.UserId;
            }

            return enty;
        }

        //附加房间信息
        private static void AttachRoomInfo(Msg msg)
        {
            if (msg == null) return;
            var roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(msg.RoomId.ToString());
            if (roomInfo == null) return;
            if (roomInfo.Owner != null)
            {
                msg.Owner = new RoomAnchorInfo()
                {
                    Nickname = roomInfo.Owner.Nickname,
                    HeadImgUrl = roomInfo.Owner.HeadUrl,
                    SecUid = roomInfo.Owner.SecUid,
                    UserId = long.Parse(roomInfo.Owner.UserId)
                };
            }

            if (msg.WebRoomId.IsNullOrWhiteSpace())
            {
                msg.WebRoomId = roomInfo.WebRoomId;
            }
        }

        //粉丝团
        private void Grab_OnFansclubMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<FansclubMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<FansclubMsg>(msg);
            enty.Content = msg.Content;
            enty.Type = (FansclubType)msg.Action;
            enty.Level = enty.User.FansClub.Level;
            var msgType = PackMsgType.粉丝团消息;

            if (msg.User.badgeImageListV2.Exists(image => image.Uri.Contains("star_guard")))
            {
                AppRuntime.DanmakuManager.ReportEvent(msg.Common.roomId, msg.Common.Method, msg.ToJson(), "新守护相关");
            }

            AttachRoomInfo(enty);
            Broadcast(new DanmakuMessagePack(enty.ToJson(), msgType, e.Process));
        }

        //统计消息
        private void Grab_OnRoomUserSeqMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<RoomUserSeqMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<OnlineViewStatsMsg>(msg);
            enty.Online = msg.Total;
            enty.Viewed = msg.totalUser;
            enty.Content = $"当前直播间人数 {msg.onlineUserForAnchor}，累计观看人数 {msg.totalPvForAnchor}";
            Broadcast(new DanmakuMessagePack(enty.ToJson(), PackMsgType.直播间统计, e.Process));
        }

        //礼物
        private void Grab_OnGiftMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<GiftMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var key = msg.Common.roomId + "-" + msg.giftId + "-" + msg.groupId.ToString();

            int currCount = (int)msg.repeatCount;
            int lastCount = 0;

            //纠正赋值礼物数据，比如是否连击，弹幕回送会不准确
            //var findForData = giftData?.gifts?.FirstOrDefault(f => f.id == msg.giftId);
            //if (findForData != null)
            //{
            //    var ogift = msg.Gift;
            //    ogift.Name = findForData.name;
            //    ogift.Combo = findForData.combo;
            //    ogift.diamondCount = findForData.diamond_count;
            //    ogift.Name = findForData.name;
            //}

            //判断礼物重复
            if (msg.repeatEnd == 1 && giftCountCache.ContainsKey(key))
            {
                //清除缓存中的key
                if (msg.groupId > 0)
                {
                    Tuple<int, DateTime> _;
                    giftCountCache.TryRemove(key, out _);
                }

                return;
            }

            var backward = currCount <= lastCount;
            if (currCount <= 0) currCount = 1;

            if (giftCountCache.ContainsKey(key))
            {
                lastCount = giftCountCache[key].Item1;
                backward = currCount <= lastCount;
                if (!backward)
                {
                    lock (giftCountCache)
                    {
                        giftCountCache[key] = Tuple.Create(currCount, DateTime.Now);
                    }
                }
            }
            else
            {
                if (msg.groupId > 0 && !backward)
                {
                    giftCountCache.TryAdd(key, Tuple.Create(currCount, DateTime.Now));
                }
            }

            //比上次小，则说明先后顺序出了问题，直接丢掉，应为比它大的消息已经处理过了
            if (backward) return;

            var count = currCount - lastCount;

            var enty = CreateMsg<GiftMsg>(msg);
            enty.Content =
                $"{msg.User.Nickname} 送出 {msg.Gift.Name}{(msg.Gift.Combo ? "(可连击)" : "")} x {msg.repeatCount}个，增量{count}个";
            enty.DiamondCount = msg.Gift.diamondCount;
            enty.RepeatCount = msg.repeatCount;
            enty.GiftCount = count;
            enty.GroupId = msg.groupId;
            enty.GiftId = msg.giftId;
            enty.GiftName = msg.Gift.Name;
            enty.Combo = msg.Gift.Combo;
            enty.ImgUrl = msg.Gift.Image?.urlList?.FirstOrDefault() ?? "";
            enty.ToUser = GetUser(msg.toUser);

            if (enty.ToUser != null)
            {
                enty.Content += "，给" + enty.ToUser.Nickname;
            }

            var msgType = PackMsgType.礼物消息;
            AttachRoomInfo(enty);
            var pack = new DanmakuMessagePack(enty.ToJson(), PackMsgType.礼物消息, e.Process);
            Broadcast(pack);
        }

        //关注
        private void Grab_OnSocialMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<SocialMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            if (msg.Action != 1) return;
            var enty = CreateMsg<FollowMsg>(msg);
            enty.Content = $"{msg.User.Nickname} 关注了主播";
            var msgType = PackMsgType.关注消息;
            AttachRoomInfo(enty);
            var pack = new DanmakuMessagePack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //直播间分享
        private void Grab_OnShardMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<SocialMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            if (msg.Action != 3) return;
            ShareType type = ShareType.未知;
            if (Enum.IsDefined(type.GetType(), int.Parse(msg.shareTarget)))
            {
                type = (ShareType)int.Parse(msg.shareTarget);
            }

            var enty = CreateMsg<ShareMsg>(msg);
            enty.Content = $"{msg.User.Nickname} 分享了直播间到{type}";
            enty.ShareType = type;

            var msgType = PackMsgType.直播间分享;
            AttachRoomInfo(enty);

            //shareTarget: (112:好友),(1微信)(2朋友圈)(3微博)(5:qq)(4:qq空间),shareType: 1            
            var pack = new DanmakuMessagePack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //来了
        private void Grab_OnMemberMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<MemberMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enterType = e.Message.userEnterTipType;
            var enty = CreateMsg<MemberMsg>(msg);
            enty.Content = $"{msg.User.Nickname} ${(enterType == 6 ? " 通过分享" : "")}来了 | 直播间人数:{msg.memberCount}";
            enty.CurrentCount = msg.memberCount;
            enty.EnterTipType = enterType;
            AttachRoomInfo(enty);
            Broadcast(new DanmakuMessagePack(enty.ToJson(), PackMsgType.进直播间, e.Process));
            if (msg.User.badgeImageListV2.Exists(image => image.Uri.Contains("star_guard")))
            {
                AppRuntime.DanmakuManager.ReportEvent(msg.Common.roomId, msg.Common.Method, msg.ToJson(), "新守护相关");
            }
        }

        //点赞
        private void Grab_OnLikeMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<LikeMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = CreateMsg<LikeMsg>(msg);
            enty.Total = msg.Total;
            enty.Count = msg.Count;
            enty.Content = $"{msg.User.Nickname} 为主播点了{msg.Count}个赞，总点赞{msg.Total}";

            var msgType = PackMsgType.点赞消息;
            AttachRoomInfo(enty);
            var pack = new DanmakuMessagePack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //弹幕
        private void Grab_OnChatMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<ChatMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<ChatMsg>(msg);
            enty.Content = msg.Content;
            var msgType = PackMsgType.弹幕消息;
            AttachRoomInfo(enty);
            var pack = new DanmakuMessagePack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        private void Grab_OnAudioChatMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<AudioChatMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<ChatMsg>(msg);
            enty.Content = msg.Content;
            enty.IsAudio = true;
            var msgType = PackMsgType.弹幕消息;
            AttachRoomInfo(enty);
            var pack = new DanmakuMessagePack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //会员表情
        private void Grab_OnEmojiMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<EmojiChatMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<VipEmojiMsg>(msg);
            enty.EmojiUrl = msg.emojiContent.Pieces.First().imageValue.Image.urlList.First();
            enty.Content = $"[会员表情]";
            AttachRoomInfo(enty);
            Broadcast(new DanmakuMessagePack(enty.ToJson(), PackMsgType.会员表情, e.Process));
            AppRuntime.DanmakuManager.ReportEvent(msg.Common.roomId, msg.Common.Method, msg.ToJson(), "会员表情");
        }

        //直播间状态变更
        private void Grab_OnControlMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<ControlMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            DanmakuMessagePack pack = null;
            //下播
            if (msg.Status == 3)
            {
                var enty = new Msg()
                {
                    MsgId = msg.Common.msgId,
                    Content = "直播结束",
                    RoomId = msg.Common.roomId.ToString(),
                    WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                };
                AttachRoomInfo(enty);
                pack = new DanmakuMessagePack(enty.ToJson(), PackMsgType.下播, e.Process);
            }

            if (pack != null)
            {
                Broadcast(pack);
            }
        }

        //在线人数数据
        private void Grab_OnRoomStatsMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<RoomStatsMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<OnlineViewStatsMsg>(msg);
            enty.Online = msg.displaValue;
            enty.Content = msg.Display;
            enty.Viewed = msg.Total;
            var msgType = PackMsgType.房间数据;
            AttachRoomInfo(enty);
            Broadcast(new DanmakuMessagePack(enty.ToJson(), msgType, e.Process));
        }

        //直播间排行榜
        private void Grab_OnRoomRankMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<RoomRankMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = CreateMsg<RoomRankMsg>(msg);
            enty.Ranks = msg.Ranks.Select(r => new RoomRank
            {
                User = GetUser(r.User),
                ScoreStr = long.TryParse(r.Score, out var s) ? s : 0
            }).ToList();

            enty.Content = $"直播间排行榜更新: {enty.Ranks.Count}人";

            var msgType = PackMsgType.房间排行;
            AttachRoomInfo(enty);
            Broadcast(new DanmakuMessagePack(enty.ToJson(), msgType, e.Process));
        }

        //房间消息
        private void Grab_OnRoomMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<RoomMessage> e)
        {
            var msg = e.Message;
            if (msg.Common == null) return;
            if (!CheckRoomId(msg.Common.roomId)) return;
            VipBuyMsg enty = null;
            PackMsgType type = PackMsgType.无;
            var displayText = msg.Common.displayText;
            if (displayText != null && displayText.Key != null && displayText.Key.Equals("subscribe_anchor_mvp_v2",
                    StringComparison.CurrentCultureIgnoreCase))
            {
                if (displayText.Pieces != null && displayText.Pieces.Count > 2)
                {
                    var protoUser = displayText.Pieces[0].userValue?.User;
                    var msgUser = protoUser != null ? GetUser(protoUser) : null;
                    enty = CreateMsg<VipBuyMsg>(msg, msgUser);
                    enty.Action = displayText.Pieces[1].stringValue;
                    enty.Unit = displayText.Pieces[2].stringValue;
                    enty.IsAnnual = enty.Unit.Equals("年度", StringComparison.CurrentCultureIgnoreCase);
                    type = PackMsgType.会员开通;
                }
            }

            if (enty != null)
            {
                enty.Content = msg.Content;
                AttachRoomInfo(enty);
                Broadcast(new DanmakuMessagePack(enty.ToJson(), type, e.Process));
            }

            if (msg.Common.User != null && msg.Common.User.badgeImageListV2 != null &&
                msg.Common.User.badgeImageListV2.Exists(image => image.Uri.Contains("star_guard")))
            {
                AppRuntime.DanmakuManager.ReportEvent(msg.Common.roomId, msg.Common.Method, msg.ToJson(), "新守护相关");
            }
        }

        //直播间状态变更
        private void Grab_OnRoomStatusChange(object sender, RoomStatusEventArgs e)
        {
            var roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(e.RoomId);
            if (roomInfo == null) return;
            var type = e.IsConnected ? PackMsgType.直播间连接 : PackMsgType.直播间断开;
            var json = JsonConvert.SerializeObject(roomInfo);
            Broadcast(new DanmakuMessagePack(json, type, Process.GetCurrentProcess().ProcessName));
        }

        private void StartAuthHandShare(IWebSocketConnection socket)
        {
            try
            {
                var serverHello = new ServerHello
                {
                    MachineId = AppRuntime.DanmakuManager.MachineId,
                    TimeoutSeconds = 10
                };
                var helloCommand = new Command
                {
                    Cmd = CommandCode.Auth,
                    Data = serverHello
                };
                socket.Send(JsonConvert.SerializeObject(helloCommand));
            }
            catch (Exception ex)
            {
                Logger.LogError($"发送握手包失败: {ex.Message}");
            }
        }

        //监听用户连接
        private void Listen(IWebSocketConnection socket)
        {
            //客户端url
            var clientUrl = socket.ConnectionInfo.ClientIpAddress + ":" + socket.ConnectionInfo.ClientPort;
            socket.OnOpen = () =>
            {
                // 1. 授权的客户端更新新连接实例
                if (Client != null && Client.SessionId == clientUrl && Client.IsAuthenticated)
                {
                    Client.Socket.Close(); // Close old socket? Logic implies re-connection handling
                    Client.Socket = socket;
                    Client.LastPing = DateTime.Now;
                    Logger.LogInfo($"已认证客户端重连: {clientUrl}");
                    return;
                }

                StartAuthHandShare(socket);
            };

            //接收指令
            socket.OnMessage = (message) =>
            {
                try
                {
                    var cmdPack = JsonConvert.DeserializeObject<Command>(message);
                    if (cmdPack == null)
                    {
                        Logger.LogWarn("无法解析命令消息");
                        return;
                    }

                    switch (cmdPack.Cmd)
                    {
                        case CommandCode.Auth:
                            // 处理认证请求
                            _ = HandleAuthenticationAsync(socket, clientUrl, cmdPack.Data).ContinueWith(task =>
                            {
                                if (task.IsFaulted)
                                {
                                    Logger.LogError($"处理认证请求时发生未捕获的异常: {task.Exception?.GetBaseException()?.Message}");
                                }
                            });
                            break;
                        case CommandCode.Close:
                            // 关闭服务器
                            Logger.LogInfo("关闭程序...");
                            Dispose();
                            Environment.Exit(0);
                            break;
                        case CommandCode.GetConfig:
                            // 处理获取配置请求
                            HandleGetConfig(socket);
                            break;
                        case CommandCode.UpdateConfig:
                            // 处理更新配置请求
                            HandleUpdateConfig(socket, cmdPack.Data);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"处理命令时出错: {ex.Message}");
                }
            };

            socket.OnClose = () =>
            {
                Logger.LogInfo($"客户端<{socket.ConnectionInfo.Id}>关闭");
                if (Client?.ClientID == clientUrl)
                {
                    Client?.Socket.Close();
                    Client = null;
                }
            };

            socket.OnPing = (data) =>
            {
                if (Client?.ClientID != clientUrl) return;
                Client.LastPing = DateTime.Now;
                socket.SendPong(Encoding.UTF8.GetBytes("ok"));
            };
        }

        /// <summary>
        /// 处理客户端认证请求
        /// </summary>
        /// <param name="socket">连接对象</param>
        /// <param name="clientUrl">客户端URL</param>
        /// <param name="data">认证数据</param>
        private async Task HandleAuthenticationAsync(IWebSocketConnection socket, string clientUrl, object data)
        {
            // 如果已经认证过，直接返回
            if (Client != null && Client.ClientID == clientUrl && Client.IsAuthenticated)
            {
                return;
            }

            try
            {
                // 处理 data 对象：可能是 JObject、字符串或其他类型
                string dataJson;
                if (data is JObject jObj)
                {
                    dataJson = jObj.ToString();
                }
                else if (data is string str)
                {
                    dataJson = str;
                }
                else
                {
                    dataJson = JsonConvert.SerializeObject(data);
                }

                // 反序列化认证请求
                var authRequest = JsonConvert.DeserializeObject<AuthRequest>(dataJson);
                if (authRequest == null)
                {
                    Logger.LogError("认证请求格式错误，无法反序列化");
                    return;
                }

                // 验证客户端发送的 session_id
                // 注意：后端在启动时可能还没有 session_id，需要在验证时获取
                var isValid = await AppRuntime.DanmakuManager.ValidateClientSession(authRequest.SessionId);

                if (!isValid)
                {
                    Logger.LogWarn("客户端SessionId验证失败");
                    return;
                }

                // 确保云服务已连接（订阅频道）
                if (AppRuntime.DanmakuManager.SessionId != null)
                {
                    var connected = await AppRuntime.DanmakuManager.ConnectAsync();
                    if (!connected)
                    {
                        Logger.LogError("云服务连接失败，无法完成认证");
                        return;
                    }
                }

                // 认证成功
                // 只有认证成功才赋值给 Global Client
                Client = new UserState(socket, clientUrl)
                {
                    IsAuthenticated = true,
                    SessionId = authRequest.SessionId,
                    LastPing = DateTime.Now
                };
                Logger.LogInfo($"客户端[{socket.ConnectionInfo.Id}]认证成功");
            }
            catch (Exception ex)
            {
                Logger.LogError($"处理认证请求时出错: {ex.Message}");
            }

            return;
        }

        /// <summary>
        /// 广播简单事件
        /// </summary>
        /// <param name="eventType">事件类型</param>
        public void BroadcastEvent(PackMsgType eventType)
        {
            var eventMsg = new Msg
            {
                Content = eventType.ToString(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var pack = new DanmakuMessagePack(eventMsg.ToJson(), eventType, Process.GetCurrentProcess().ProcessName);
            Broadcast(pack);
        }

        /// <summary>
        /// 广播消息
        /// </summary>
        /// <param name="pack">弹幕数据包</param>
        public void Broadcast(DanmakuMessagePack pack)
        {
            if (pack == null) return;
            if (Client != null && Client.IsAuthenticated && Client.Socket.IsAvailable)
            {
                try
                {
                    Client.Socket.Send(pack.ToJson());
                }
                catch (Exception ex)
                {
                    Logger.LogError($"发送消息到客户端失败: {ex.Message}");
                    Client.Socket.Close();
                    Client = null;
                }
            }
            else if (Client != null && !Client.Socket.IsAvailable)
            {
                Client = null;
            }
        }

        /// <summary>
        /// 开始监听
        /// </summary>
        public void StartListen()
        {
            try
            {
                this.grab.Start(); //启动代理
                socketServer.Start(Listen); //启动监听         
            }
            catch (Exception)
            {
                this.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 处理获取配置请求
        /// </summary>
        private void HandleGetConfig(IWebSocketConnection socket)
        {
            var json = Appsetting.SaveToJson();
            var response = new Command
            {
                Cmd = CommandCode.GetConfig,
                Data = JObject.Parse(json) // Data expects object, JObject parses it structure-wise
            };
            socket.Send(JsonConvert.SerializeObject(response));
        }

        /// <summary>
        /// 处理更新配置请求
        /// </summary>
        private void HandleUpdateConfig(IWebSocketConnection socket, object data)
        {
            try
            {
                var json = data.ToString();
                Appsetting.LoadFromJson(json);
                Appsetting.Save(); // Save to App.config if needed, but AppSetting.cs Save logic seems to only update specific fields ?
                var response = new Command
                {
                    Cmd = CommandCode.UpdateConfig,
                    Data = new { success = true, message = "配置已更新" }
                };
                socket.Send(JsonConvert.SerializeObject(response));
            }
            catch (Exception ex)
            {
                Logger.LogError($"更新配置失败: {ex.Message}");
                var response = new Command
                {
                    Cmd = CommandCode.UpdateConfig,
                    Data = new { success = false, message = $"更新失败: {ex.Message}" }
                };
                socket.Send(JsonConvert.SerializeObject(response));
            }
        }

        /// <summary>
        /// 客户端套接字上下文
        /// </summary>
        class UserState
        {
            public UserState()
            {
            }

            public UserState(IWebSocketConnection socket, string url)
            {
                Socket = socket;
                ClientID = url;
            }

            /// <summary>
            /// 套接字
            /// </summary>
            public IWebSocketConnection Socket { get; set; }

            /// <summary>
            /// 上次发起心跳包时间
            /// </summary>
            public DateTime LastPing { get; set; } = DateTime.Now;

            /// <summary>
            /// 是否已认证
            /// </summary>
            public bool IsAuthenticated { get; set; } = false;

            /// <summary>
            /// 认证超时计时器
            /// </summary>
            public Timer AuthTimer { get; set; }

            /// <summary>
            /// 客户端会话ID
            /// </summary>
            public string SessionId { get; set; }

            public string ClientID { get; }
        }

        /// <summary>
        /// Print事件参数
        /// </summary>
        public class PrintEventArgs : EventArgs
        {
            public string Message { get; set; }

            public ConsoleColor Color { get; set; }

            public PackMsgType MsgType { get; set; }
        }

        /// <summary>
        /// 消息包事件参数
        /// </summary>
        public class PackMsgEventArgs
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public PackMsgType MsgType { get; set; }

            /// <summary>
            /// 消息内容
            /// </summary>
            public Msg Message { get; set; }
        }
    }
}
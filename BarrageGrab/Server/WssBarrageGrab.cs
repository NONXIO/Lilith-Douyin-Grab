﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BarrageGrab.Modles.ProtoEntity;
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
        //ISystemProxy proxy = new FiddlerProxy();
        ISystemProxy proxy = new TitaniumProxy();
        AppSetting appsetting = AppSetting.Current;

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
        /// 代理
        /// </summary>
        public ISystemProxy Proxy { get { return proxy; } }

        public WssBarrageGrab()
        {
            proxy.OnWebSocketData += Proxy_OnWebSocketData;
            proxy.OnFetchResponse += Proxy_OnFetchResponse;
        }

        public void Start()
        {
            proxy.Start();
        }

        public void Dispose()
        {
            proxy.Dispose();
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

            response.Messages.ForEach(f =>
            {
                DoMessage(f, e.ProcessName);
            });
        }

        //用于缓存接收过的消息ID，判断是否重复接收
        Dictionary<string, List<long>> msgDic = new Dictionary<string, List<long>>();

        //发送事件
        private void DoMessage(Message msg, string processName)
        {
            List<long> msgIdList;
            if (msgDic.ContainsKey(msg.Method))
            {
                msgIdList = msgDic[msg.Method];
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
                            this.OnMemberMessage?.Invoke(this, new RoomMessageEventArgs<MemberMessage>(processName, arg));
                            break;
                        }
                    //关注
                    case "WebcastSocialMessage":
                        {
                            var arg = Serializer.Deserialize<SocialMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnSocialMessage?.Invoke(this, new RoomMessageEventArgs<SocialMessage>(processName, arg));
                            break;
                        }
                    //消息
                    case "WebcastChatMessage":
                        {
                            var arg = Serializer.Deserialize<ChatMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnChatMessage?.Invoke(this, new RoomMessageEventArgs<ChatMessage>(processName, arg));
                            break;
                        }
                    //点赞
                    case "WebcastLikeMessage":
                        {
                            var arg = Serializer.Deserialize<LikeMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnLikeMessage?.Invoke(this, new RoomMessageEventArgs<LikeMessage>(processName, arg));
                            break;
                        }
                    //礼物
                    case "WebcastGiftMessage":
                        {
                            var arg = Serializer.Deserialize<GiftMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnGiftMessage?.Invoke(this, new RoomMessageEventArgs<GiftMessage>(processName, arg));
                            break;
                        }
                    //直播间统计
                    case "WebcastRoomUserSeqMessage":
                        {
                            var arg = Serializer.Deserialize<RoomUserSeqMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnRoomUserSeqMessage?.Invoke(this, new RoomMessageEventArgs<RoomUserSeqMessage>(processName, arg));
                            break;
                        }
                    //直播间状态变更
                    case "WebcastControlMessage":
                        {
                            var arg = Serializer.Deserialize<ControlMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnControlMessage?.Invoke(this, new RoomMessageEventArgs<ControlMessage>(processName, arg));
                            break;
                        }
                    //粉丝团消息
                    case "WebcastFansclubMessage":
                        {
                            var arg = Serializer.Deserialize<FansclubMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnFansclubMessage?.Invoke(this, new RoomMessageEventArgs<FansclubMessage>(processName, arg));
                            break;
                        }
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                return;
            }
        }

        public class RoomMessageEventArgs<T> : EventArgs where T : class
        {
            /// <summary>
            /// 进程名
            /// </summary>
            public string Process { get; set; }

            /// <summary>
            /// 消息
            /// </summary>
            public T Message { get; set; }


            public RoomMessageEventArgs()
            {

            }

            public RoomMessageEventArgs(string process, T data)
            {
                this.Process = process;
                this.Message = data;
            }
        }
    }
}

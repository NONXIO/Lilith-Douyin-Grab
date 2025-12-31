using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using DanmakuBackend.Cloud;
using DanmakuBackend.Models;
using DanmakuBackend.Server;
using DanmakuBackend.Utility;

namespace DanmakuBackend
{
    /// <summary>
    /// 程序运行时信息
    /// </summary>
    public static class AppRuntime
    {
        static AppRuntime()
        {
        }

        /// <summary>
        /// Ws弹幕服务示例
        /// </summary>
        public static WsBarrageServer WsServer { get; private set; }

        /// <summary>
        /// 房间缓存信息
        /// </summary>
        public static RoomCacheManager RoomCaches { get; private set; }

        /// <summary>
        /// 程序进程信息
        /// </summary>
        public static Process CurrentProcess { get; private set; } = Process.GetCurrentProcess();

        public static DanmakuManager DanmakuManager { get; private set; } = null;

        /// <summary>
        /// 是否启用调试模式（通过 --debug 参数启用）
        /// </summary>
        public static bool IsDebugMode { get; set; } = false;

        public static void PreInit(string[] args)
        {
            var accessKey = args[0] ?? string.Empty;
            var roomId = args[1] ?? string.Empty;
            if (string.IsNullOrEmpty(accessKey)) throw new DanmakuException("未授权");
            if (string.IsNullOrEmpty(roomId)) throw new DanmakuException("参数错误");
            DanmakuManager = new DanmakuManager(accessKey, roomId);
        }

        public static void Init()
        {
            WsServer = new WsBarrageServer();
            RoomCaches = new RoomCacheManager();
        }

        /// <summary>
        /// 房间缓存管理器
        /// </summary>
        public class RoomCacheManager
        {
            /// <summary>
            /// 房间ID - 房间信息映射缓存
            /// </summary>
            public ConcurrentDictionary<string, RoomInfo> RoomInfoCache { get; } =
                new ConcurrentDictionary<string, RoomInfo>();

            /// <summary>
            /// 添加房间缓存时触发
            /// </summary>
            public event EventHandler<RoomCacheEventArgs> OnCache;

            /// <summary>
            /// 添加WebRoomid-房间ID 映射
            /// </summary>
            /// <param name="webrid"></param>
            /// <param name="roomid"></param>
            public void SetRoomCache(string roomid, string webrid)
            {
                if (RoomInfoCache.ContainsKey(roomid))
                {
                    RoomInfoCache[roomid].WebRoomId = webrid;
                }
                else
                {
                    var info = new RoomInfo()
                    {
                        RoomId = roomid,
                        WebRoomId = webrid,
                        Title = "房间" + webrid
                    };
                    var succ = RoomInfoCache.TryAdd(roomid, info);
                    if (succ)
                    {
                        OnCache?.Invoke(this, new RoomCacheEventArgs()
                        {
                            Model = 0,
                            RoomInfo = info
                        });
                    }
                }
            }

            /// <summary>
            /// 添加房间缓存信息
            /// </summary>
            /// <param name="roomid"></param>
            /// <param name="roomInfo"></param>
            public void AddRoomInfoCache(RoomInfo roomInfo)
            {
                if (roomInfo == null) return;
                var roomid = roomInfo.RoomId;
                if (RoomInfoCache.ContainsKey(roomid))
                {
                    RoomInfoCache[roomid] = roomInfo;
                    OnCache?.Invoke(this, new RoomCacheEventArgs()
                    {
                        Model = 1,
                        RoomInfo = roomInfo
                    });
                }
                else
                {
                    var succ = RoomInfoCache.TryAdd(roomid, roomInfo);
                    if (succ)
                    {
                        OnCache?.Invoke(this, new RoomCacheEventArgs()
                        {
                            Model = 0,
                            RoomInfo = roomInfo
                        });
                    }
                }
            }

            /// <summary>
            /// 根据roomid从缓存获取webRoomid
            /// </summary>
            /// <param name="roomid"></param>
            /// <returns></returns>
            public string GetCachedWebRoomid(string roomid)
            {
                RoomInfo value;
                if (RoomInfoCache.TryGetValue(roomid, out value)) return value.WebRoomId ?? "未知";
                return "未知";
            }

            /// <summary>
            /// 根据roomid从缓存获取房间信息
            /// </summary>
            /// <param name="roomid"></param>
            /// <returns></returns>
            public RoomInfo GetCachedWebRoomInfo(string roomid)
            {
                RoomInfo value;
                if (RoomInfoCache.TryGetValue(roomid, out value)) return value;
                return null;
            }

            /// <summary>
            /// 根据webRoomid从缓存获取房间信息
            /// </summary>
            /// <param name="webrid"></param>
            /// <returns></returns>
            public RoomInfo GetByWebRoomid(string webrid)
            {
                var find = RoomInfoCache.FirstOrDefault(p => p.Value.WebRoomId == webrid);
                return find.Value;
            }


            public class RoomCacheEventArgs : EventArgs
            {
                public RoomInfo RoomInfo { get; set; }

                /// <summary>
                /// 0:添加,1更新,2删除
                /// </summary>
                public int Model { get; set; }
            }
        }
    }
}
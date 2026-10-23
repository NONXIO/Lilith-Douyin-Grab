using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using DanmakuBackend.Cloud;
using DanmakuBackend.Models;
using DanmakuBackend.Server;

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

        /// <summary>
        ///     直播伴侣是否已经启动（或尝试启动过）
        /// </summary>
        public static bool IsLiveCompanLaunched { get; set; } = false;

        public static void CheckLicence(string[] args)
        {
            var accessKey = args[0] ?? string.Empty;
            var liveId = args[1] ?? string.Empty;
            if (string.IsNullOrEmpty(accessKey)) throw new DanmakuException("未授权");
            if (string.IsNullOrEmpty(liveId)) throw new DanmakuException("参数错误");
            DanmakuManager = new DanmakuManager(accessKey, liveId);
        }

        public static void Init()
        {
            WsServer = new WsBarrageServer();
            RoomCaches = new RoomCacheManager();
        }

        /// <summary>
        /// 房间缓存管理器
        /// </summary>
        public class RoomCacheManager : BoundedRoomCache
        {
            public RoomCacheManager() : base(512, TimeSpan.FromMinutes(30), () => DateTime.UtcNow, () => DanmakuManager?.ActiveRoomId)
            { Changed = (mode, info) => OnCache?.Invoke(this, new RoomCacheEventArgs { Model = mode, RoomInfo = info }); }
            public event EventHandler<RoomCacheEventArgs> OnCache;
            public class RoomCacheEventArgs : EventArgs
            {
                public RoomInfo RoomInfo { get; set; }
                public int Model { get; set; }
            }
        }
    }
}

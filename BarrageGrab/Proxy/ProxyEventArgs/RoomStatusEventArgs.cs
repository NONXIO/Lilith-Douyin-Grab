using System;

namespace DanmakuBackend.Proxy.ProxyEventArgs
{
    public class RoomStatusEventArgs : EventArgs
    {
        /// <summary>
        /// 房间ID
        /// </summary>
        public string RoomId { get; set; }

        /// <summary>
        /// 是否连接
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// 消息
        /// </summary>
        public string Msg { get; set; }
    }
}

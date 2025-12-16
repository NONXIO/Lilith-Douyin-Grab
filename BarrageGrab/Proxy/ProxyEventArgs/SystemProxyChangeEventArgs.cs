using System;

namespace DanmakuBackend.Proxy.ProxyEventArgs
{
    public class SystemProxyChangeEventArgs : EventArgs
    {
        /// <summary>
        /// True 以开启:False: 已关闭
        /// </summary>
        public bool Open { get; set; }
    }
}

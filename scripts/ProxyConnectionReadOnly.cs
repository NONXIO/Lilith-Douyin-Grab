using System;
using DanmakuBackend.Utility;
class ProxyConnectionReadOnly
{
    static void Main()
    {
        var state = WindowsProxyConnection.Read();
        Console.WriteLine("READ_ONLY_WININET flags=" + state.Flags + " manual=" + ((state.Flags & 2) != 0) +
            " autoDetect=" + ((state.Flags & 8) != 0) + " hasPac=" + !string.IsNullOrEmpty(state.AutoConfigUrl));
    }
}

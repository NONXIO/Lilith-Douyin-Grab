using System;

namespace BarrageGrab
{
    public class DanmakuException : SystemException
    {
        public DanmakuException()
        {
        }

        public DanmakuException(string message) : base(message)
        {
        }

        public DanmakuException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
using System;

namespace DanmakuBackend
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
using System;

namespace DanmakuBackend.Models
{
    /// <summary>
    /// 业务异常
    /// </summary>
    public class BusinessExecption : Exception
    {
        public BusinessExecption()
        {
        }

        public BusinessExecption(string msg) : base(msg)
        {
            Code = -1;
        }

        public BusinessExecption(string msg, object data) : base(msg)
        {
            ErrorTarget = data;
        }

        public BusinessExecption(string msg, int code) : this(msg)
        {
            Code = code;
        }

        /// <summary>
        /// 错误码
        /// </summary>
        public int Code { get; set; } = -1;

        /// <summary>
        /// 异常附加数据
        /// </summary>
        public object ErrorTarget { get; set; } = null;
    }
}
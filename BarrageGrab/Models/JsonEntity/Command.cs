using Newtonsoft.Json;

namespace BarrageGrab.Models.JsonEntity
{
    public enum CommandCode
    {
        /// <summary>
        /// 身份验证 Data:string
        /// </summary>
        Auth = 0,

        /// <summary>
        /// 关闭程序
        /// </summary>
        Close = 1,

        /// <summary>
        /// 配置更改
        /// </summary>
        Config = 2
    }

    public class Command
    {
        /// <summary>
        /// 指令标识
        /// </summary>
        [JsonProperty("cmd")]
        public CommandCode Cmd { get; set; }

        /// <summary>
        /// 附加数据
        /// </summary>
        [JsonProperty("data")]
        public object Data { get; set; }
    }

    /// <summary>
    /// 客户端认证请求
    /// </summary>
    public class AuthRequest
    {
        /// <summary>
        /// 房间ID
        /// </summary>
        [JsonProperty("roomid")]
        public string RoomId { get; set; }

        /// <summary>
        /// 应用版本
        /// </summary>
        [JsonProperty("app_version")]
        public string AppVersion { get; set; }

        /// <summary>
        /// 会话ID
        /// </summary>
        [JsonProperty("session_id")]
        public string SessionId { get; set; }
    }

    /// <summary>
    /// 服务端Hello消息
    /// </summary>
    public class ServerHello
    {
        /// <summary>
        /// 机器ID
        /// </summary>
        [JsonProperty("machine_id")]
        public string MachineId { get; set; }

        /// <summary>
        /// 认证超时时间（秒）
        /// </summary>
        [JsonProperty("timeout_seconds")]
        public int TimeoutSeconds { get; set; }
    }
}
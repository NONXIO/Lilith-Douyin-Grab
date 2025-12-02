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
    }

    public class Command
    {
        /// <summary>
        /// 指令标识
        /// </summary>
        public CommandCode Cmd { get; set; }

        /// <summary>
        /// 附加数据
        /// </summary>
        public object Data { get; set; }
    }
}
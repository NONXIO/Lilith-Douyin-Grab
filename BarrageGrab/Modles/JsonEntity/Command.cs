using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BarrageGrab.Modles.JsonEntity
{
    /*
     * 例如发送 {"Cmd":1,"Data":true} 到ws连接地址 关闭程序
     * 前往 http://wstool.jackxiang.com/ 在线ws测试
     */

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

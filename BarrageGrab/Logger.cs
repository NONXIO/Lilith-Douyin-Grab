using System;
using System.Linq;
using System.Reflection;
using Fleck;
using NLog;
using NLog.Config;
using LogLevel = Fleck.LogLevel;

namespace DanmakuBackend
{
    public static class Logger
    {
        private static ISetupBuilder builder;
        private static NLog.Logger logger;

        static Logger()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            //读取嵌入式资源文件
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(s => s.EndsWith("nlog.config"));
            if (resourceName == null)
            {
                throw new Exception("nlog.config 嵌入式资源不存在");
            }

            builder = LogManager.Setup().LoadConfigurationFromAssemblyResource(assembly, resourceName);
            logger = builder.GetLogger("*");


            // 配置 Fleck 日志输出，使用统一的 Logger
            FleckLog.LogAction = (level, message, ex) =>
            {
                switch (level)
                {
                    case LogLevel.Debug:
                        // 不输出 Debug 日志
                        break;
                    case LogLevel.Info:
                        LogInfo(message);
                        break;
                    case LogLevel.Warn:
                        LogWarn(message);
                        break;
                    case LogLevel.Error:
                        if (ex != null)
                            LogError(ex, message);
                        else
                            LogError(message);
                        break;
                }
            };
        }

        public static void PrintColor(string message, ConsoleColor foreground = ConsoleColor.White)
        {
            var color = Console.ForegroundColor;
            Console.ForegroundColor = foreground;
            Console.WriteLine(message);
            Console.ForegroundColor = color;
        }

        public static void PrintInlineColor(string message, ConsoleColor foreground = ConsoleColor.White,
            ConsoleColor background = ConsoleColor.Black)
        {
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
            Console.Write(message);
            Console.ResetColor();
        }

        // 记录日志方法
        public static void LogTrace(string message)
        {
            logger.Trace(message);
        }

        public static void LogDebug(string message)
        {
            logger.Debug(message);
        }

        public static void LogInfo(string message)
        {
            logger.Info(message);
        }

        public static void LogWarn(string message)
        {
            logger.Warn(message);
        }

        public static void LogError(string message)
        {
            var color = Console.ForegroundColor;
            logger.Error(message);
        }

        public static void LogError(Exception ex, string message)
        {
            var color = Console.ForegroundColor;
            logger.Error(ex, message);
        }

        public static void LogFatal(string message)
        {
            var color = Console.ForegroundColor;
            logger.Fatal(message);
        }

        public static void LogFatal(Exception ex, string message)
        {
            var color = Console.ForegroundColor;
            logger.Fatal(ex, message);
        }
    }
}
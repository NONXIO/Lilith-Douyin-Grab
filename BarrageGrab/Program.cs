using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DanmakuBackend.Models.JsonEntity;
using DanmakuBackend.Utility;
using DanmakuBackend.Views;
using Microsoft.Win32;

namespace DanmakuBackend
{
    public class Program
    {
        private static FormView _mainForm;
        private static bool _exited;
        private static readonly WinApi.ControlCtrlDelegate ControlCtr = ControlCtrlHandle;
        private static readonly Mutex Mutex = new Mutex(false, "DanmakuBackendServiceMutex");

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (Debugger.IsAttached) return;
            if (!Mutex.WaitOne(TimeSpan.Zero, true))
            {
                Logger.LogFatal(@"另一个实例已在运行");
                Environment.Exit(100);
            }

            // 解析命令行参数
            bool isDebugMode = false;
            var validArgs = new List<string>();
            foreach (var arg in args)
            {
                if (arg.Equals("--debug", StringComparison.OrdinalIgnoreCase))
                {
                    isDebugMode = true;
                }
                else
                {
                    validArgs.Add(arg);
                }
            }

            // 检查访问密钥以及房间（排除 --debug 参数后）
            if (validArgs.Count != 2)
            {
                Logger.LogError("参数错误,请使用Danmaku启动此后端服务");
                MessageBox.Show(@"参数错误,请使用Danmaku启动此后端服务", @"程序初始化错误", MessageBoxButtons.OK);
                Environment.Exit(101);
            }

            // 设置调试模式
            AppRuntime.IsDebugMode = isDebugMode;
            if (isDebugMode) Logger.LogInfo("调试模式已启用");

            SetTitle("启动中...");
            try
            {
                // 关闭系统代理防止无法联网的问题
                CloseProxy();
                AppRuntime.CheckLicence(validArgs.ToArray());
                Init();
                SetTitle("运行中");
                AppRuntime.WsServer.Broadcast(new DanmakuMessagePack
                {
                    Type = PackMsgType.后端初始化,
                    Data = LiveCompanHelper.LiveCompanExePath
                });
            }
            catch (Exception ex)
            {
                SetTitle("初始化失败");
                try
                {
                    Logger.LogError(ex, $"程序初始化错误，{ex.Message}");
                }
                catch
                {
                    Logger.LogError($"程序初始化严重错误: {ex.GetType().Name} - {ex.Message}");
                }

                MessageBox.Show($@"{ex.Message}{Environment.NewLine}{ex.StackTrace}", @"程序初始化错误", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _exited = true;
            }

            // 如果使用窗体模式，使用 Application.Run 启动消息循环
            if (!_exited) Application.Run();
            else
                while (!_exited)
                    Thread.Sleep(200);

            // 执行清理
            OnClose();

            // 反注册捕获控制台关闭
            WinApi.SetConsoleCtrlHandler(ControlCtr, false);

            // 强制退出，避免等待未完成的异步任务
            Environment.Exit(0);
        }

        private static void CloseProxy()
        {
            var registry =
                Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
                    true);
            if (registry != null) registry.SetValue("ProxyEnable", 0);
        }

        private static void Init()
        {
            AppRuntime.Init();
            LiveCompanHelper.SwitchSetup();
            WinApi.SetConsoleCtrlHandler(ControlCtr, true); //捕获控制台关闭
            // WinApi.DisableQuickEditMode(); //禁用控制台快速编辑模式
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _mainForm = new FormView();
            AppRuntime.WsServer.Grab.Proxy.SetUpstreamProxy(AppSetting.Current.UpstreamProxy); //设置上游代理
            AppRuntime.WsServer.OnClose += (s, e) =>
            {
                _exited = true;
                if (_mainForm != null && !_mainForm.IsDisposed)
                    _mainForm.Invoke(new Action(Application.Exit));
            };
            AppRuntime.WsServer.StartListen(); //启动WS以及代理服务
            Logger.LogInfo("后端服务启动完成");
        }

        //设置控制台标题
        private static void SetTitle(string title)
        {
            var version = Assembly.GetAssembly(typeof(Program)).GetName().Version;
            var session = AppRuntime.DanmakuManager?.SessionId;
            if (WinApi.GetConsoleWindow() != IntPtr.Zero)
                Console.Title = string.Join(" ",
                    new List<string> { "Danmaku后端服务", $"v{version}", session, title }.Where(x => x.IsNullOrEmpty()));
        }

        //监听控制台消息事件
        private static bool ControlCtrlHandle(int ctrlType)
        {
            switch (ctrlType)
            {
                case 0: //Ctrl+C关闭
                case 2:
                    Logger.LogInfo("正在关闭服务...");
                    OnClose();
                    return true;
            }

            return false;
        }

        private static void OnClose()
        {
            try
            {
                // 释放资源
                if (AppRuntime.WsServer != null && !AppRuntime.WsServer.IsDisposed) AppRuntime.WsServer.Dispose();
                AppRuntime.DanmakuManager?.Destroy();
            }
            catch (Exception ex)
            {
                Logger.LogError($"关闭资源失败(Server/Manager): {ex.Message}");
            }

            try
            {
                // 释放 Mutex
                // 注意：如果是在非主线程（如Ctrl+C回调）调用ReleaseMutex，会抛出SynchronizationLockException，这是预期行为，忽略即可
                Mutex?.ReleaseMutex();
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                Mutex?.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }
}
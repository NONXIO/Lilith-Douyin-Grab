using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DanmakuBackend.Cloud;
using DanmakuBackend.Models.JsonEntity;
using DanmakuBackend.Utility;
using DanmakuBackend.Views;

namespace DanmakuBackend
{
    public class Program
    {
        private static FormView mainForm;
        private static bool exited;
        private static bool formExited;
        static WinApi.ControlCtrlDelegate controlCtr = ControlCtrlHandle;
        static Mutex mutex = new Mutex(false, "DanmakuBackendServiceMutex");
        private static DanmakuManager _manager = null;

        static void Main(string[] args)
        {
            if (Debugger.IsAttached) throw new DanmakuException("内部错误,请使用Danmaku启动此后端服务");
            if (!mutex.WaitOne(TimeSpan.Zero, true))
            {
                Logger.LogFatal(@"另一个实例已在运行");
                MessageBox.Show(@"另一个实例已在运行", @"程序初始化错误", MessageBoxButtons.OK);
                return;
            }

            // 检查访问密钥以及房间
            if (args.Length != 2)
            {
                Logger.LogError("参数错误,请使用Danmaku启动此后端服务");
                MessageBox.Show(@"参数错误,请使用Danmaku启动此后端服务", @"程序初始化错误", MessageBoxButtons.OK);
                return;
            }

            SetTitle("启动中...");
            AppRuntime.PreInit(args);

            try
            {
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
                Logger.LogError(ex, $"程序初始化错误，{ex.Message}");
                MessageBox.Show(ex.Message, @"程序初始化错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                exited = true;
            }

            // 如果使用窗体模式，使用 Application.Run 启动消息循环
            if (!exited)
                Application.Run();
            else
                // 控制台模式，使用传统的循环等待
                while (!exited)
                    Thread.Sleep(500);

            if (!AppRuntime.WsServer.IsDisposed) OnClose();
            WinApi.SetConsoleCtrlHandler(controlCtr, false); //反注册捕获控制台关闭            
        }

        private static void Init()
        {
            AppRuntime.Init();
            LiveCompanHelper.SwitchSetup();
            WinApi.SetConsoleCtrlHandler(controlCtr, true); //捕获控制台关闭
            WinApi.DisableQuickEditMode(); //禁用控制台快速编辑模式
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            mainForm = new FormView();
            AppRuntime.WsServer.Grab.Proxy.SetUpstreamProxy(AppSetting.Current.UpstreamProxy); //设置上游代理
            AppRuntime.WsServer.OnClose += (s, e) =>
            {
                AppRuntime.DanmakuManager.Destroy();
                exited = true;
                if (mainForm != null && !mainForm.IsDisposed)
                    mainForm.Invoke(new Action(Application.Exit));
            };
            AppRuntime.WsServer.StartListen(); //启动WS以及代理服务
        }

        //设置控制台标题
        private static void SetTitle(string title)
        {
            var version = Assembly.GetAssembly(typeof(Program)).GetName().Version;
            if (WinApi.GetConsoleWindow() != IntPtr.Zero)
            {
                Console.Title = $@"Danmaku后端服务 v{version} {title}";
            }
        }

        //监听控制台消息事件
        private static bool ControlCtrlHandle(int ctrlType)
        {
            switch (ctrlType)
            {
                case 0: //Ctrl+C关闭
                case 2:
                    Logger.LogInfo("捕获到控制台关闭请求，正在关闭服务...");
                    OnClose();
                    return true;
            }

            return false;
        }

        private static void OnClose()
        {
            AppRuntime.WsServer.Dispose();
        }
    }
}
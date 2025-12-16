using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using BarrageGrab.Forms;
using BarrageGrab.Forms.Models;
using BarrageGrab.Models.JsonEntity;
using BarrageGrab.Proxy;
using BarrageGrab.Proxy.ProxyEventArgs;
using BarrageGrab.Server;

namespace BarrageGrab
{
    public partial class FormView : Form
    {
        static int printCount = 0;

        WsBarrageServer barServer = AppRuntime.WsServer;
        WssBarrageGrab grab = AppRuntime.WsServer.Grab;
        private bool isExiting = false;
        ISystemProxy proxy = AppRuntime.WsServer.Grab.Proxy;

        // 系统托盘相关
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        public FormView()
        {
            InitializeComponent();
            // 初始化系统托盘
            InitializeTrayIcon();
            // 窗体关闭时最小化到托盘而不是退出
            FormClosing += FormView_FormClosing;
            // 强制创建窗口句柄，以便 Invoke 调用能够正常工作
            var handle = Handle;
        }

        private void FormView_Load(object sender, EventArgs e)
        {
            
        }
        
        #region 系统托盘功能

        /// <summary>
        /// 初始化系统托盘图标
        /// </summary>
        private void InitializeTrayIcon()
        {
            // 创建托盘图标
            trayIcon = new NotifyIcon();
            // 使用程序图标，如果没有则使用默认图标
            try
            {
                var iconPath = Assembly.GetExecutingAssembly().Location;
                trayIcon.Icon = Icon.ExtractAssociatedIcon(iconPath);
            }
            catch
            {
                // 如果提取图标失败，使用默认图标
                trayIcon.Icon = SystemIcons.Application;
            }

            // NotifyIcon.Text 不支持 \n 换行，并且最大长度为63个字符
            // 使用简短的单行文本作为 tooltip
            trayIcon.Text = @"Danmaku 弹幕后端服务 v" + Application.ProductVersion;
            trayIcon.Visible = true;

            // 添加左键点击事件 - 发送焦点事件
            trayIcon.MouseClick += TrayIcon_MouseClick;

            // 创建右键菜单 - 只显示退出选项
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("退出", null, ExitApplication_Click);
            trayIcon.ContextMenuStrip = trayMenu;
        }

        /// <summary>
        /// 托盘图标鼠标点击事件
        /// </summary>
        private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            // 只处理左键点击
            if (e.Button == MouseButtons.Left)
                // 发送焦点事件到所有WebSocket客户端
                barServer.BroadcastEvent(PackMsgType.焦点事件);
        }

        /// <summary>
        /// 右键菜单 - 退出程序
        /// </summary>
        private void ExitApplication_Click(object sender, EventArgs e)
        {
            isExiting = true;
            // 清理托盘图标
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            // 关闭应用程序
            Application.Exit();
        }

        /// <summary>
        /// 窗体关闭事件
        /// </summary>
        private void FormView_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!isExiting) e.Cancel = true;
        }
        #endregion
    }
}
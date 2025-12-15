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
            barServer.OnPrint += WssService_OnPrint;
            barServer.Grab.Proxy.OnProxyStatus += Proxy_OnProxyStatus;
            AppRuntime.RoomCaches.OnCache += RoomCaches_OnCache;

            // 初始化系统托盘
            InitializeTrayIcon();

            // 窗体关闭时最小化到托盘而不是退出
            FormClosing += FormView_FormClosing;

            // 强制创建窗口句柄，以便 Invoke 调用能够正常工作
            var handle = Handle;
        }

        private void InitTabPages()
        {
            var msgTypes = GetMsgTypes();
            foreach (TabPage page in this.tab_filters.TabPages)
            {
                page.Padding = new Padding(0)
                {
                    Left = 5
                };
                page.Margin = new Padding(0);
                foreach (Control control in page.Controls)
                {
                    var panel = control as FlowLayoutPanel;
                    if (panel == null) continue;
                    panel.Padding = new Padding(0);
                    panel.Margin = new Padding(0);

                    foreach (var label in msgTypes)
                    {
                        var ck = new CheckBox()
                        {
                            Text = label.Value,
                            Tag = label.Key,
                            Parent = panel,
                            Padding = new Padding(0),
                            Margin = new Padding(0)
                        };
                        string type = "console";
                        if (page == this.tabPage_Ws)
                        {
                            type = "ws";
                            ck.Checked = AppSetting.Current.PushFilter.Contains(label.Key);
                        }

                        if (page == this.tabPage_Console)
                        {
                            type = "console";
                            ck.Checked = AppSetting.Current.PrintFilter.Contains(label.Key);
                        }

                        if (page == this.tabPage_Log)
                        {
                            type = "log";
                            ck.Checked = AppSetting.Current.LogFilter.Contains(label.Key);
                        }

                        ck.Name = $"cbx_bartype_{type}_{label.Key}";
                        ck.CheckedChanged += Ck_CheckedChanged;
                        panel.Controls.Add(ck);
                    }
                }
            }
        }

        private void Ck_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox cbx = sender as CheckBox;
            if (cbx == null) return;
            var selected = GetCheckedBarTypes((FlowLayoutPanel)cbx.Parent);
            if (cbx.Parent.Parent == tabPage_Console)
            {
                AppSetting.Current.PrintFilter = selected;
            }

            if (cbx.Parent.Parent == tabPage_Ws)
            {
                AppSetting.Current.PushFilter = selected;
            }

            if (cbx.Parent.Parent == tabPage_Log)
            {
                AppSetting.Current.LogFilter = selected;
            }

            AppSetting.Current.Save();
        }

        private int[] GetCheckedBarTypes(FlowLayoutPanel panel)
        {
            List<int> list = new List<int>();
            foreach (Control ctr in panel.Controls)
            {
                var cbk = ctr as CheckBox;
                if (cbk == null) continue;
                if (cbk.Checked && cbk.Name.StartsWith("cbx_bartype_"))
                {
                    list.Add((int)cbk.Tag);
                }
            }

            return list.OrderBy(o => o).ToArray();
        }

        private Dictionary<int, string> GetMsgTypes()
        {
            var dic = new Dictionary<int, string>();
            Type enumType = typeof(PackMsgType);
            FieldInfo[] fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in fields)
            {
                var attribute =
                    (DescriptionAttribute)field.GetCustomAttribute(typeof(DescriptionAttribute), false);
                int value = (int)field.GetValue(null);
                if (attribute != null && value > 0)
                {
                    dic.Add(value, attribute.Description);
                }
            }

            return dic;
        }

        private void RoomCaches_OnCache(object sender, AppRuntime.RoomCacheManager.RoomCacheEventArgs e)
        {
            if (IsHandleCreated && !Disposing && !IsDisposed)
                Invoke(new Action(() =>
                {
                    if (e.Model == 0)
                    {
                        var item = new RoomCacheItem(e.RoomInfo);
                        label3.Text = $"房间缓存列表({AppRuntime.RoomCaches.RoomInfoCache.Count})";
                        list_roomCaches.Items.Add(item);
                    }
                }));
        }

        private void FormView_Load(object sender, EventArgs e)
        {
            this.txb_wsaddr.Text = barServer.ServerLocation;
            this.txb_upstreamProxy.Text = proxy.HttpUpstreamProxy;
            this.cbx_barrageLog.Checked = AppSetting.Current.BarrageLog;
            InitTabPages();

            this.cbx_enableProxy.Checked = SystemProxy.ProxyIsOpen();
        }

        private void Proxy_OnProxyStatus(object sender, SystemProxyChangeEventArgs e)
        {
            if (Disposing || IsDisposed || !IsHandleCreated) return;
            Invoke(new Action(() => { cbx_enableProxy.Checked = e.Open; }));
        }

        private void WssService_OnPrint(object sender, WsBarrageServer.PrintEventArgs e)
        {
            //将弹幕输出到richTextBox
            //颜色转换
            Color color = AppSetting.Current.ColorMap[e.MsgType].Item2;
            string msg = e.Message;

            if (IsHandleCreated && !Disposing && !IsDisposed)
            {
                Invoke(new Action(() =>
                {
                    //输出到richTextBox
                    rich_output.SelectionColor = color;
                    rich_output.AppendText(msg + "\n");
                    rich_output.ScrollToCaret();

                    if (++printCount > 10000)
                    {
                        rich_output.Clear();
                        printCount = 0;
                    }
                }));
            }
        }

        private void cbx_enableProxy_CheckedChanged(object sender, EventArgs e)
        {
            var checker = (CheckBox)sender;
            if (checker.Checked)
            {
                barServer.Grab.Proxy.RegisterSystemProxy();
            }
            else
            {
                barServer.Grab.Proxy.CloseSystemProxy();
            }
        }

        private void list_roomCaches_DoubleClick(object sender, EventArgs e)
        {
            // 获取双击的 ListBox 中的选中项
            var selectedItem = list_roomCaches.SelectedItem as RoomCacheItem;
            if (selectedItem == null) return;

            var form = new RoomDetail(selectedItem.RoomInfo);
            form.ShowDialog();
        }

        private void btn_updateUpProxy_Click(object sender, EventArgs e)
        {
            var text = this.txb_upstreamProxy.Text;
            try
            {
                proxy.SetUpstreamProxy(text);
                AppSetting.Current.UpstreamProxy = text;
                AppSetting.Current.Save();
            }
            catch (Exception ex)
            {
                //弹窗提示错误
                MessageBox.Show(ex.Message, "设置失败");
            }
        }

        private void cbx_barrageLog_CheckedChanged(object sender, EventArgs e)
        {
            var checker = (CheckBox)sender;
            AppSetting.Current.BarrageLog = checker.Checked;
            AppSetting.Current.Save();
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
            trayIcon.Text = "Danmaku 弹幕后端服务 v" + Application.ProductVersion;
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
            // 窗体不应该被显示，但保留此处理以防万一
            if (!isExiting) e.Cancel = true;
        }

        #endregion
    }
}
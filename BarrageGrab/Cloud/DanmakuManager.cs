using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeviceId;
using Newtonsoft.Json;
using Supabase;
using Supabase.Realtime;
using Client = Supabase.Client;
using Timer = System.Timers.Timer;

namespace DanmakuBackend.Cloud
{
    public class DanmakuManager
    {
        private readonly Client client;
        private readonly RealtimeChannel machineChannel;
        private readonly long roomId;

        private readonly RealtimeChannel sessionChannel;
        private Timer heartbeatTimer;

        private LicenceInfo licenceInfo;

        public DanmakuManager(string accessKey, string roomId)
        {
            this.roomId = long.Parse(roomId);
            Logger.LogInfo("初始化云服务...");
            client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co", accessKey, new SupabaseOptions
            {
                AutoConnectRealtime = true
            });
            client.InitializeAsync().Wait();
            MachineId = new DeviceIdBuilder()
                .OnWindows(windows =>
                    windows
                        .AddWindowsDeviceId()
                        .AddMachineGuid()
                )
                .ToString();
            if (ValidateSessionAsync().Result)
            {
                // 订阅授权频道
                machineChannel = client.Realtime.Channel($"danmaku-licence-{MachineId}");
                var machineBroadcast = machineChannel.Register<ShutdownBroadcast>();
                machineBroadcast.AddBroadcastEventHandler((sender, broadcast) =>
                {
                    if (broadcast?.Event == "shutdown") OnLicenceShutdown(broadcast.Payload?["message"]?.ToString());
                });
                machineChannel.Subscribe();
                sessionChannel = client.Realtime.Channel($"danmaku-licence-{SessionId}");
                var sessionBroadcast = sessionChannel.Register<ShutdownBroadcast>();
                sessionBroadcast.AddBroadcastEventHandler((sender, broadcast) =>
                {
                    if (broadcast?.Event == "shutdown") OnLicenceShutdown(broadcast.Payload?["message"]?.ToString());
                });
                sessionChannel.Subscribe();
                Logger.LogInfo("云服务连接成功");
            }
            else
            {
                Logger.LogFatal("云服务授权失败");
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 机器ID
        /// </summary>
        private string MachineId { get; }

        /// <summary>
        /// 已验证的客户端 session_id
        /// </summary>
        public string SessionId { get; private set; }

        /// <summary>
        ///     当前会话的授权信息
        /// </summary>
        public LicenceInfo LicenceInfo { get; private set; }

        /// <summary>
        /// 检查房间是否在授权列表中（使用缓存，避免重复解析）
        /// </summary>
        /// <param name="room">房间ID</param>
        /// <returns>是否在授权列表中</returns>
        public bool IsAnchorRoom(long room)
        {
            // 使用缓存的房间ID集合进行快速查找，避免重复解析
            return roomId == room;
        }

        public async void ReportEvent(long room, string eventName, object body, string note = null)
        {
            // 仅在调试模式下上传事件
            if (!AppRuntime.IsDebugMode)
            {
                return;
            }

            Logger.LogWarn($@"报告事件<{eventName}> {note}");
            try
            {
                await client.From<EventLog>().Insert(new EventLog
                {
                    EventName = eventName,
                    RoomId = room,
                    Body = body,
                    Timestamp = DateTime.Now,
                    Note = note
                });
            }
            catch (Exception e)
            {
                Logger.LogError(@"报告事件错误: " + e.Message);
            }
        }

        private void OnLicenceShutdown(string reason = null)
        {
            Logger.LogError("未授权，程序即将退出");
            MessageBox.Show(
                $@"原因: {reason ?? "授权已被终止，程序即将退出"}",
                @"未授权",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            AppRuntime.WsServer.Dispose();
            Environment.Exit(0);
        }

        /// <summary>
        /// 验证客户端会话
        /// </summary>
        /// <returns>验证是否成功</returns>
        private async Task<bool> ValidateSessionAsync()
        {
            try
            {
                var payload = new Supabase.Functions.Client.InvokeFunctionOptions
                {
                    Body = new Dictionary<string, object>
                    {
                        { "machine_id", MachineId },
                        { "room_id", roomId }
                    }
                };
                var response = await client.Functions.Invoke("validate-session-and-get-licence", options: payload);
                var result = JsonConvert.DeserializeObject<ValidateSessionResponse>(response);
                if (result == null || result.Licence == null)
                {
                    Logger.LogError($"验证失败: {result?.Error ?? "未知原因"}");
                    return false;
                }

                LicenceInfo = result.Licence;
                SessionId = result.SessionId;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"验证会话时出错: {e.Message}");
                return false;
            }
        }

        public void Destroy()
        {
            // 安全地停止心跳定时器
            if (heartbeatTimer != null)
            {
                try
                {
                    heartbeatTimer.Stop();
                    heartbeatTimer.Dispose();
                }
                catch (Exception e)
                {
                    Logger.LogError("停止心跳定时器失败: " + e.Message);
                }

                heartbeatTimer = null;
            }

            // 安全地取消订阅频道
            if (sessionChannel != null)
            {
                try
                {
                    sessionChannel.Unsubscribe();
                }
                catch (Exception e)
                {
                    Logger.LogError("取消订阅频道失败: " + e.Message);
                }
            }

            // 安全地取消订阅授权频道
            if (machineChannel != null)
            {
                try
                {
                    machineChannel.Unsubscribe();
                }
                catch (Exception e)
                {
                    Logger.LogError("取消订阅授权频道失败: " + e.Message);
                }
            }

            // 释放会话
            ReleaseSessionLock().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task ReleaseSessionLock()
        {
            Logger.LogInfo(@"释放会话");
            var payload = new Supabase.Functions.Client.InvokeFunctionOptions
            {
                Body = new Dictionary<string, object>
                {
                    { "machine_id", MachineId },
                    { "room_id", roomId }
                }
            };
            var response = await client.Functions.Invoke("danmaku-session-release", options: payload);
            var result = JsonConvert.DeserializeObject<ReleaseSessionResponse>(response);
            if (result.ErrorMsg != null) Logger.LogError("会话释放异常: " + result.ErrorMsg);
        }

        public bool IsSessionMatched(string session)
        {
            return SessionId == session;
        }

        /// <summary>
        ///     Edge Function 响应模型
        /// </summary>
        private class ValidateSessionResponse
        {
            [JsonProperty("licence")] public LicenceInfo Licence { get; set; }
            [JsonProperty("session_id")] public string SessionId { get; set; }
            [JsonProperty("error")] public string Error { get; set; }
        }

        /// <summary>
        ///     Edge Function 响应模型
        /// </summary>
        private class ReleaseSessionResponse
        {
            [JsonProperty("error")] public string ErrorMsg { get; set; }
            [JsonProperty("success")] public string SuccessMsg { get; set; }
        }
    }
}
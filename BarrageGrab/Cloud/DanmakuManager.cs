using System;
using System.Threading;
using System.Threading.Tasks;
using DeviceId;
using Newtonsoft.Json;
using Supabase;
using Client = Supabase.Client;

namespace DanmakuBackend.Cloud
{
    public class DanmakuManager
    {
        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        private readonly Client _client;
        private readonly string _roomId;
        private string _sessionRoomId;

        public DanmakuManager(string accessKey, string roomId)
        {
            // 生成机器ID
            MachineId = new DeviceIdBuilder()
                .OnWindows(windows =>
                    windows
                        .AddWindowsDeviceId()
                        .AddMachineGuid()
                )
                .ToString();

            // 注册房间号 (全局不可变)
            _roomId = roomId;

            // 初始化 Supabase 客户端
            _client = new Client("https://kkuqbesyrhmobaxoespi.supabase.co", accessKey, new SupabaseOptions
            {
                AutoConnectRealtime = true
            });

            _client.InitializeAsync().Wait();
            Logger.LogInfo("Danmaku服务初始化完成");
        }

        /// <summary>
        /// 机器ID
        /// </summary>
        public string MachineId { get; }

        /// <summary>
        /// 已验证的客户端 session_id
        /// </summary>
        public string SessionId { get; private set; }

        /// <summary>
        /// 当前会话的授权信息
        /// </summary>
        public LicenceInfo LicenceInfo { get; private set; }

        /// <summary>
        /// 检查房间是否在授权列表中（使用缓存，避免重复解析）
        /// </summary>
        /// <param name="sessionRoomId">房间ID</param>
        /// <returns>是否在授权列表中</returns>
        public bool SetSessionRoomId(string roomid, string sessionRoomId)
        {
            if (LicenceInfo == null || LicenceInfo.RoomId != roomid) return false;
            _sessionRoomId = sessionRoomId;
            Logger.LogInfo($"直播间[{sessionRoomId}]添加到授权列表");
            return true;
        }

        public bool CheckRoomId(string id)
        {
            return _sessionRoomId == id;
        }

        public async void ReportEvent(string roomId, string eventName, object body, string note = null)
        {
            Logger.LogWarn($@"报告事件<{eventName}> {note}");
            try
            {
                await _client.From<EventLog>().Insert(new EventLog
                {
                    EventName = eventName,
                    RoomId = roomId,
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


        /// <summary>
        /// 验证客户端会话
        /// 注意：这个方法在启动时调用，用于获取初始的 session_id 和 licence
        /// 但实际的 session 验证应该在客户端发送认证响应时进行
        /// </summary>
        /// <returns>验证是否成功</returns>
        private async Task<bool> ValidateSession()
        {
            try
            {
                var options = new Supabase.Functions.Client.InvokeFunctionOptions()
                {
                    Body =
                    {
                        { "machine_id", MachineId },
                        { "room_id", _roomId }
                    }
                };
                var response = await _client.Functions.Invoke("validate-session-and-get-licence", options: options)
                    .ConfigureAwait(false);
                var session = JsonConvert.DeserializeObject<ValidateSessionResponse>(response);
                if (session == null || session.Licence == null)
                {
                    var errorMsg = session?.Error ?? "无授权";
                    Logger.LogWarn($"验证失败: {errorMsg}");
                    return false;
                }

                // 验证成功，保存 session_id 和授权信息
                SessionId = session.SessionId;
                LicenceInfo = session.Licence;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"注册会话时出错: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 验证客户端发送的 session_id 是否有效
        /// </summary>
        /// <param name="clientSessionId">客户端发送的 session_id</param>
        /// <returns>验证是否成功</returns>
        public async Task<bool> ValidateClientSession(string clientSessionId, string rid)
        {
            await _authLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 如果后端还没有 session_id，先获取
                if (SessionId == null)
                {
                    if (!await ValidateSession().ConfigureAwait(false))
                    {
                        Logger.LogError("获取Session失败");
                        return false;
                    }
                }

                // 比较后端和客户端的 session_id
                if (SessionId != clientSessionId)
                {
                    Logger.LogWarn("Session无效");
                    return false;
                }

                if (_sessionRoomId.IsNullOrEmpty()) _sessionRoomId = rid;
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"验证客户端 Session 时出错: {e.Message}");
                return false;
            }
            finally
            {
                // 释放锁
                _authLock.Release();
            }
        }

        public void CleanSession()
        {
            SessionId = null;
            LicenceInfo = null;
            Logger.LogInfo("会话已清理");
        }

        public void Destroy()
        {
            try
            {
                // 释放会话
                ReleaseSession().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Logger.LogError("Danmaku服务注销异常: " + e.Message);
            }
        }

        private async Task ReleaseSession()
        {
            if (SessionId == null) return;
            var options = new Supabase.Functions.Client.InvokeFunctionOptions
            {
                Body =
                {
                    { "session_id", SessionId }
                }
            };
            var response = await _client.Functions.Invoke("validate-session-and-get-licence", options: options);
            var res = JsonConvert.DeserializeObject<ReleaseSessionResponse>(response);
            if (res.Error != null) Logger.LogDebug($"释放会话异常: {res?.Error ?? "未知错误"}");
        }

        /// <summary>
        /// 会话验证响应
        /// </summary>
        private class ValidateSessionResponse
        {
            [JsonProperty("licence")] public LicenceInfo Licence { get; set; }
            [JsonProperty("error")] public string Error { get; set; }
            [JsonProperty("session_id")] public string SessionId { get; set; }
        }

        /// <summary>
        /// 会话验证响应
        /// </summary>
        private class ReleaseSessionResponse
        {
            [JsonProperty("error")] public string Error { get; set; }
        }
    }
}
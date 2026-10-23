using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private const int EventQueueCapacity = 512;
        private static readonly TimeSpan EventQueueShutdownTimeout = TimeSpan.FromSeconds(2);

        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<EventLog> _eventQueue = new ConcurrentQueue<EventLog>();
        private readonly SemaphoreSlim _eventAvailable = new SemaphoreSlim(0);
        private readonly object _eventQueueLock = new object();
        private readonly Client _client;
        private readonly Task _eventWorker;
        private readonly string _roomId;
        private string _sessionRoomId;
        public string ActiveRoomId => _sessionRoomId;
        private int _isStopping;
        private int _queuedEventCount;
        private int _droppedEventCount;

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
            _eventWorker = Task.Run(ProcessEventQueueAsync);
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
        /// <param name="roomid">Web房间号(display_id/web_rid)，可能是数字也可能是用户名</param>
        /// <param name="sessionRoomId">抖音内部房间号(id_str)，每次开播会变化</param>
        /// <param name="ownerNames">主播标识(用户名/昵称)，用于授权信息只记录了主播名时的匹配</param>
        /// <returns>是否在授权列表中</returns>
        public bool SetSessionRoomId(string roomid, string sessionRoomId, params string[] ownerNames)
        {
            if (LicenceInfo == null) return false;
            // 弹幕消息使用抖音内部房间号(id_str)校验，拿不到内部房间号则无法建立会话
            if (sessionRoomId.IsNullOrWhiteSpace()) return false;

            // 授权房间可能以 Web房间号(web_rid/display_id)、内部房间号(id_str)、
            // 主播名(anchor_name/用户名/昵称) 任一形式存在。
            // 直播伴侣开播时 id_str 每次都会变化，web_rid 缺失时回退为用户名(如 Senna_Akkad)，需要兼容匹配
            var candidates = new List<string> { roomid, sessionRoomId, _sessionRoomId };
            if (ownerNames != null) candidates.AddRange(ownerNames);
            candidates.RemoveAll(string.IsNullOrWhiteSpace);

            var authorizedValues = new[] { LicenceInfo.RoomId, LicenceInfo.AnchorName };
            var authorized = candidates.Any(candidate => authorizedValues.Any(authorizedValue =>
                !authorizedValue.IsNullOrWhiteSpace() &&
                string.Equals(candidate.Trim(), authorizedValue.Trim(), StringComparison.OrdinalIgnoreCase)));

            if (!authorized)
            {
                Logger.LogWarn(
                    $"直播间[{sessionRoomId}](web:{roomid})不在授权列表，授权房间:{LicenceInfo.RoomId}(主播:{LicenceInfo.AnchorName})");
                return false;
            }

            _sessionRoomId = sessionRoomId;
            Logger.LogInfo($"直播间[{sessionRoomId}]添加到授权列表");
            return true;
        }

        public bool CheckRoomId(string id)
        {
            return _sessionRoomId == id;
        }

        public void ReportEvent(string roomId, string eventName, object body, string note = null)
        {
            var eventLog = new EventLog
            {
                EventName = eventName,
                RoomId = roomId,
                Body = body,
                Timestamp = DateTime.Now,
                Note = note
            };

            lock (_eventQueueLock)
            {
                if (_isStopping != 0 || _queuedEventCount >= EventQueueCapacity)
                {
                    Interlocked.Increment(ref _droppedEventCount);
                    return;
                }

                _eventQueue.Enqueue(eventLog);
                _queuedEventCount++;
                _eventAvailable.Release();
            }
        }

        private async Task ProcessEventQueueAsync()
        {
            while (true)
            {
                await _eventAvailable.WaitAsync().ConfigureAwait(false);

                EventLog eventLog;
                lock (_eventQueueLock)
                {
                    if (!_eventQueue.TryDequeue(out eventLog))
                    {
                        if (_isStopping != 0) return;
                        continue;
                    }

                    _queuedEventCount--;
                }

                try
                {
                    Logger.LogWarn($@"报告事件<{eventLog.EventName}> {eventLog.Note}");
                    await _client.From<EventLog>().Insert(eventLog).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Logger.LogError(@"报告事件错误: " + e.Message);
                }

                var droppedCount = Interlocked.Exchange(ref _droppedEventCount, 0);
                if (droppedCount > 0)
                {
                    Logger.LogWarn($"事件汇报队列满或服务正在关闭，已丢弃 {droppedCount} 条事件");
                }
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
            lock (_eventQueueLock)
            {
                if (_isStopping == 0)
                {
                    _isStopping = 1;
                    _eventAvailable.Release();
                }
            }

            try
            {
                if (!_eventWorker.Wait(EventQueueShutdownTimeout))
                {
                    Logger.LogWarn("等待事件汇报队列关闭超时，剩余事件将在进程退出时丢弃");
                }
                else
                {
                    var droppedCount = Interlocked.Exchange(ref _droppedEventCount, 0);
                    if (droppedCount > 0)
                    {
                        Logger.LogWarn($"服务关闭时丢弃 {droppedCount} 条事件汇报");
                    }
                }
            }
            catch (AggregateException e)
            {
                Logger.LogError("关闭事件汇报队列异常: " + e.GetBaseException().Message);
            }

            try
            {
                // 释放会话
                var release = ReleaseSession();
                if (!release.Wait(2000))
                {
                    Logger.LogWarn("释放会话等待超时，不阻塞程序退出");
                    release.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
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
            if (res.Error != null) Logger.LogWarn($"释放会话异常: {res.Error}");
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

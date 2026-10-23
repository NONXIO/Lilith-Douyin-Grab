using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DanmakuBackend.Models;
using DanmakuBackend.Models.JsonEntity;
using DanmakuBackend.Proxy.ProxyEventArgs;
using DanmakuBackend.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.StreamExtended.Network;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace DanmakuBackend.Proxy
{
    internal class TitaniumProxy : SystemProxy
    {
        const string SCRIPT_HOST = "lf-cdn-tos.bytescm.com";
        const string LIVE_HOST = "live.douyin.com";
        const string DOUYIN_HOST = "www.douyin.com";
        const string USER_INFO_PATH = "/webcast/user/me/";
        const string BARRAGE_POOL_PATH = "/webcast/im/fetch";
        const string WEBCAST_AMEMV_HOST = "webcast.amemv.com";
        private readonly Regex webcastBarrageReg = new Regex(@"webcast\d+-ws-web-\w+\.(douyin|amemv)\.com");
        private ExplicitProxyEndPoint explicitEndPoint = null;
        private ProxyServer proxyServer = null;
        private readonly RoomRequestCoordinator<RoomInfo> roomRequests = new RoomRequestCoordinator<RoomInfo>();
        private int disposing;
        private readonly CaptureErrorThrottle responseErrorThrottle = new CaptureErrorThrottle();
        private readonly CaptureErrorThrottle unexpectedWsErrorThrottle = new CaptureErrorThrottle();
        private ExternalProxy upStreamProxy = null;
        // 每条 WebSocket 连接对应一个自实现的帧解码器（替代有 Bug 的 Titanium.WebSocketDecoder）
        private readonly ConditionalWeakTable<SessionEventArgs, WebSocketStreamDecoder> wsDecoders =
            new ConditionalWeakTable<SessionEventArgs, WebSocketStreamDecoder>();

        static TitaniumProxy()
        {
            // 设置代理过滤规则
            string[] bypassList =
            {
                "localhost", "127.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                "172.20.*", "172.21.*", "172.22.*", "172.23.*", "172.24.*", "172.25.*",
                "172.26.*", "172.27.*", "172.28.*", "172.29.*", "172.30.*", "172.31.*",
                "192.168.*",
                ".*\\.supabase\\.co", ".*\\.supabase\\.net"
            };

            // 创建WebProxy对象，并设置代理过滤规则
            WebProxy proxy = new WebProxy
            {
                BypassList = bypassList,
                UseDefaultCredentials = true
            };

            // 设置不使用回环地址
            proxy.BypassProxyOnLocal = true;

            try
            {
                // 将代理设置为系统默认代理
                WebRequest.DefaultWebProxy = proxy;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("代理环境设置失败：" + ex.Message);
            }
        }

        public TitaniumProxy()
        {
            //注册系统代理
            //RegisterSystemProxy();
            proxyServer = new ProxyServer();

            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;

            proxyServer.CertificateManager.CertificateValidDays = 365 * 10;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            proxyServer.CertificateManager.CertificateEngine = CertificateEngine.DefaultWindows;
            proxyServer.CertificateManager.OverwritePfxFile = false;
            proxyServer.CertificateManager.RootCertificate = GetCert();
            if (proxyServer.CertificateManager.RootCertificate == null)
            {
                Logger.LogInfo("进行证书安装，若有提示弹窗请确定");
                proxyServer.CertificateManager.CreateRootCertificate();
            }

            proxyServer.CertificateManager.TrustRootCertificate(true);

            //https://github.com/justcoding121/titanium-web-proxy/issues/828

            proxyServer.ServerCertificateValidationCallback += ProxyServer_ServerCertificateValidationCallback;
            //proxyServer.BeforeRequest += ProxyServer_BeforeRequest;
            proxyServer.BeforeResponse += ProxyServer_BeforeResponse;
            //proxyServer.AfterResponse += ProxyServer_AfterResponse;            

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, ProxyPort, true);
            explicitEndPoint.BeforeTunnelConnectRequest += ExplicitEndPoint_BeforeTunnelConnectRequest;
            proxyServer.AddEndPoint(explicitEndPoint);
        }

        public override string HttpUpstreamProxy => proxyServer?.UpStreamHttpProxy?.ToString() ?? "";

        public override string HttpsUpstreamProxy => proxyServer?.UpStreamHttpsProxy?.ToString() ?? "";

        private X509Certificate2 GetCert()
        {
            X509Certificate2 result = proxyServer.CertificateManager.LoadRootCertificate();

            if (result != null) return result;

            return null;

            // 打开“受信任的根证书颁发机构”存储区
            using (X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);

                // 遍历证书集合
                foreach (X509Certificate2 cert in store.Certificates)
                {
                    try
                    {
                        // 尝试使用证书创建 X509Certificate2 实例
                        X509Certificate2 certificate = new X509Certificate2(cert);

                        //判断过期
                        if (DateTime.Now > certificate.NotAfter) continue;

                        //判断黑名单
                        var black = new string[] { "localhost" };
                        if (certificate.FriendlyName.ToLower().LikeIn(black) ||
                            certificate.Subject.ToLower().LikeIn(black)
                           ) continue;


                        if (certificate.FriendlyName.ToLower().LikeIn("titanium"))
                        {
                            result = certificate;
                            break;
                        }

                        // 打印证书信息
                        //Console.WriteLine("证书: " + certificate.Subject);
                        //Console.WriteLine("颁发者: " + certificate.Issuer);
                        //Console.WriteLine("有效期: " + certificate.NotBefore + " - " + certificate.NotAfter);

                        //result = certificate;
                        //break;
                    }
                    catch (CryptographicException ex)
                    {
                        //捕获加密异常，如果证书需要密码，这里会抛出异常
                        throw new Exception("证书加载失败: " + ex.Message);
                    }
                }

                store.Close();
            }

            return result;
        }

        public override void SetUpstreamProxy(string upstreamProxyAddr)
        {
            if (string.IsNullOrWhiteSpace(upstreamProxyAddr)) return;
            upstreamProxyAddr = upstreamProxyAddr.Trim();
            var reg = new Regex(@"[a-zA-Z0-9\.]+:\d+");
            if (!reg.IsMatch(upstreamProxyAddr))
            {
                throw new Exception("上游代理地址格式不正确，必须为ip:port格式");
            }

            //设置上游代理地址
            //var upstreamProxyAddr = Appsetting.Current.UpstreamProxy;
            if (!upstreamProxyAddr.IsNullOrWhiteSpace())
            {
                upStreamProxy = new ExternalProxy()
                {
                    HostName = upstreamProxyAddr.Split(':')[0],
                    Port = int.Parse(upstreamProxyAddr.Split(':')[1]),
                    ProxyType = ExternalProxyType.Http
                };
                proxyServer.UpStreamHttpProxy = upStreamProxy;
                proxyServer.UpStreamHttpsProxy = upStreamProxy;
            }
        }

        private bool CheckBrowser(string processName)
        {
            return AppSetting.Current.ProcessFilter.Contains(processName) && processName != "直播伴侣" &&
                   processName != "douyin";
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            if (System.Threading.Volatile.Read(ref disposing) != 0) return;
            try
            {
                var processId = e.HttpClient.ProcessId;
                if (processId == null || processId.Value <= 0) return;
                var process = GetProcessName(processId.Value);
                if (!AppSetting.Current.ProcessFilter.Contains(process)) return;
                var uri = e.HttpClient.Request.RequestUri;
                var route = UpstreamCompatibility.Route(uri, process, e.HttpClient.Response.StatusCode,
                    e.HttpClient.Response.ContentType,
                    e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket && webcastBarrageReg.IsMatch(uri.ToString()),
                    AppSetting.Current.ForcePolling);
                switch (route)
                {
                    case UpstreamCompatibility.ResponseRoute.Companion: await HookSelfLive(e); break;
                    case UpstreamCompatibility.ResponseRoute.Barrage: await HookBarrage(e); break;
                    case UpstreamCompatibility.ResponseRoute.Page: await HookPageAsync(e); break;
                    case UpstreamCompatibility.ResponseRoute.Script:
                        if (CheckHost(uri.Host)) await HookScriptAsync(e);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (responseErrorThrottle.ShouldLog())
                    Logger.LogWarn("代理目标响应处理失败（10秒限频）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Hook 直播伴侣开播信息并更新
        private async Task HookSelfLive(SessionEventArgs e)
        {
            //Hook 直播伴侣直播间创建 https://webcast.amemv.com/webcast/room/create/?ac=wifi&app_name=webcast_mate&version_code=7.3.3&device_platform=windows&webcast_sdk_version=1520&resolution=1707%2A1067&os_version=10.0.22621&language=zh&aid=2079&live_id=1&channel=online&device_id=2164319493312045&iid=42200736232026&extra_first_tag_id=22&extra_second_tag_id=22093&extra_third_tag_id=22093195&extra_encoder_core=qsv&extra_codec_name=h264_qsv_ex&extra_codec_is_ex=1&extra_use_265=0&msToken=8tJ0NCHWun7wHpdPd_fd0_nlUmgRwM8sQYThkqkq4-qR00mKiJ3Wd3h05r4mm5HO_R_qA2qeTIn8qR2yjcXXoh5mmXkewUuTS4G1Yoi_D-m8EZiacZVWoDqgnqw=&X-Bogus=DFSzswVLJzC4fUiKt5a4a3JCqOA1&_signature=_02B4Z6wo00001fHp2wAAAIDCdmADbSJfNUnx6d-AABpim6xRe8bmnvrrC1Z7GWTwK8sPujtot.bkv7h8bk-nde0WvO-78H3cwglXRzZk8uHTs3ZKWlZqOqaBVjgdHIRritV.peh4bkRETofZ7f
            string uri = e.HttpClient.Request.RequestUri.ToString();
            var urix = new Uri(uri);
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var response = e.HttpClient.Response;

            if (processName != "直播伴侣" || response.StatusCode != 200) return;
            if (UpstreamCompatibility.IsPath(urix, "/webcast/room/anchor_finish_info"))
            {
                var body = await e.GetResponseBodyAsString();
                var finish = UpstreamCompatibility.ParseFinish(body,
                    AppRuntime.DanmakuManager.CheckRoomId, AppRuntime.RoomCaches.GetCachedWebRoomid);
                if (finish != null)
                {
                    Logger.LogInfo($"直播伴侣关播，房间[{finish.WebRoomId}|{finish.RoomId}]");
                    AppRuntime.WsServer.Broadcast(new DanmakuMessagePack(finish.ToJson(), PackMsgType.下播, processName));
                }
                return;
            }

            if (UpstreamCompatibility.IsPath(urix, "/webcast/room/create_info"))
            {
                var msgData = JsonConvert.SerializeObject("数据捕获成功");
                AppRuntime.WsServer.Broadcast(new DanmakuMessagePack(msgData, PackMsgType.数据捕获成功, "后端服务"));
            }

            if (!UpstreamCompatibility.IsPath(urix, "/webcast/room/create")) return;
            if (processName != "直播伴侣") return;
            if (response.StatusCode != 200) return;
            var reponse = await e.GetResponseBodyAsString();
            RoomInfo roomInfo;

            //缓存直播间信息
            var tupe = RoomInfo.TryParseStreamPusherCreate(reponse, out roomInfo);
            var code = tupe.Item1;
            var msg = tupe.Item2;
            if (code != 0)
            {
                Logger.LogWarn($"直播伴侣开播房间资料缓存失败，原因:{msg}");
                return;
            }

            var jobj = JsonConvert.DeserializeObject<JObject>(reponse);
            var roomid = jobj["data"]?["id_str"]?.Value<string>();
            var nickname = jobj["data"]?["owner"]?["nickname"]?.Value<string>();
            var displayId = jobj["data"]?["owner"]?["display_id"]?.Value<string>();
            Logger.LogInfo($"直播伴侣开播，开播信息: {displayId} {nickname}, 房间[{displayId}|{roomid}]");
            if (roomInfo != null && !roomInfo.RoomId.IsNullOrWhiteSpace() && !roomInfo.WebRoomId.IsNullOrEmpty() &&
                AppRuntime.DanmakuManager.SetSessionRoomId(roomInfo.WebRoomId, roomInfo.RoomId, displayId, nickname))
            {
                AppRuntime.RoomCaches.AddRoomInfoCache(roomInfo);
                AppRuntime.WsServer.Broadcast(new DanmakuMessagePack(roomInfo.ToJson(), PackMsgType.开播, processName));
            }
        }

        // Hook 弹幕
        private async Task HookBarrage(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";
            var isLiveCompan = processName == "直播伴侣";
            //ws 方式
            if (
                e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket &&
                webcastBarrageReg.IsMatch(uri)
            )
            {
                var urix = new Uri(uri);
                var roomid = urix.GetQueryParam("room_id");
                if (AppRuntime.DanmakuManager.CheckRoomId(roomid))
                {
                    Logger.LogInfo($"直播间[{roomid}]订阅到新的弹幕流地址");
                    AppRuntime.WsServer.Broadcast(new DanmakuMessagePack("数据捕获成功", PackMsgType.数据捕获成功, processName));
                    e.DataReceived += WebSocket_DataReceived;
                }
                else
                {
                    Logger.LogWarn($"直播间[{roomid}]不在监听列表中");
                }
            }

            //轮询方式(当抖音ws连接断开后，客户端也会降级使用轮询模式获取弹幕)
            if (uri.Contains(BARRAGE_POOL_PATH) && contentType.Contains("application/protobuffer"))
            {
                var payload = await e.GetResponseBody();
                BoundedCapturePayload.Validate(payload);
                var referrer = e.HttpClient.Request.Headers.GetFirstHeader("Referer")?.Value;
                //https://live.douyin.com/22404217360

                //检查webroomid映射
                if (!referrer.IsNullOrWhiteSpace())
                {
                    var webroomid = Regex.Match(referrer, @"(?<=live\.douyin\.com\/)\d+").Value;
                    var urix = new Uri(uri);
                    var roomid = urix.GetQueryParam("room_id");
                    var cookie = e.HttpClient.Request.Headers.GetFirstHeader("Cookie")?.Value;

                    var roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(roomid);
                    if ((roomInfo == null || roomInfo.Owner == null) && cookie != null)
                    {
                        var expectedRoom = AppRuntime.DanmakuManager.ActiveRoomId;
                        _ = roomRequests.Run(webroomid,
                            token => DyApiHelper.GetRoomInfoForApi(webroomid, cookie, token), rinfo =>
                            {
                                if (AppRuntime.DanmakuManager.ActiveRoomId != expectedRoom && rinfo.RoomId != AppRuntime.DanmakuManager.ActiveRoomId) return;
                                if (AppRuntime.DanmakuManager.SetSessionRoomId(rinfo.WebRoomId, rinfo.RoomId, null, rinfo.Owner?.Nickname))
                                    AppRuntime.RoomCaches.AddRoomInfoCache(rinfo);
                            });
                    }

                    AppRuntime.RoomCaches.SetRoomCache(roomid, webroomid);
                }

                base.FireOnFetchResponse(new HttpResponseEventArgs()
                {
                    ProcessID = processid,
                    HostName = hostname,
                    ProcessName = base.GetProcessName(processid),
                    Payload = payload
                });
            }
        }

        // Upstream's body-only pause avoids global WebSocket.close overrides and timers.
        private async Task HookPageAsync(SessionEventArgs e)
        {
            var uri = e.HttpClient.Request.RequestUri;
            var html = await e.GetResponseBodyAsString();
            var webRoomId = uri.AbsolutePath.Trim('/');
            if (webRoomId.Length > 0)
            {
                var result = RoomInfo.TryParseRoomPageHtml(html, out var roomInfo);
                if (result.Item1 == 0 && roomInfo != null)
                {
                    roomInfo.WebRoomId = webRoomId;
                    roomInfo.LiveUrl = uri.ToString();
                    if (AppRuntime.DanmakuManager.SetSessionRoomId(webRoomId, roomInfo.RoomId,
                        null, roomInfo.Owner?.Nickname))
                        AppRuntime.RoomCaches.AddRoomInfoCache(roomInfo);
                }
            }
            var updated = UpstreamCompatibility.PausePage(html, AppSetting.Current.AutoPause, uri);
            if (AppSetting.Current.DisableLivePageScriptCache)
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(updated);
                ScriptAddTocks(doc, uri);
                updated = doc.DocumentNode.OuterHtml;
            }
            // No injection means CSP can remain intact.
            if (updated != html) e.SetResponseBodyString(updated);
        }

        //给部分脚本加上时间戳避免缓存
        private void ScriptAddTocks(HtmlDocument doc, Uri pageUri)
        {
            var scripts = doc.DocumentNode.SelectNodes("//script[@src]");
            if (scripts != null)
            {
                var ticks = DateTime.Now.Ticks;
                foreach (var script in scripts)
                {
                    var src = script.Attributes["src"].Value;

                    if (!UpstreamCompatibility.TryResolveScriptUri(pageUri, src, out var srcUri)) continue;
                    if (!CheckHost(srcUri.Host)) continue;

                    var fileName = Path.GetFileName(src.Split('?')[0]);
                    //目前只需要用到相关这些js
                    if (!UpstreamCompatibility.IsIdleScript(fileName) && !UpstreamCompatibility.IsPollingScript(srcUri)) continue;

                    if (src.Contains("?"))
                    {
                        src += "&_t=" + ticks;
                    }
                    else
                    {
                        src += "?_t=" + ticks;
                    }

                    script.Attributes["src"].Value = src;
                }
            }
        }

        private async Task HookScriptAsync(SessionEventArgs e)
        {
            var uri = e.HttpClient.Request.RequestUri;
            var process = GetProcessName(e.HttpClient.ProcessId.Value);
            bool disableIdle = CheckBrowser(process) && UpstreamCompatibility.IsIdleScript(Path.GetFileName(uri.AbsolutePath));
            bool forcePolling = AppSetting.Current.ForcePolling && UpstreamCompatibility.IsPollingScript(uri);
            if (!disableIdle && !forcePolling) return;
            var original = await e.GetResponseBodyAsString();
            var updated = UpstreamCompatibility.RewriteScript(original, disableIdle, forcePolling, AppSetting.Current.PollingInterval);
            if (updated != original)
            {
                e.SetResponseBodyString(updated);
                Logger.LogInfo("直播脚本适配完成: " + uri.GetLeftPart(UriPartial.Path));
            }
        }

        private Task ProxyServer_ServerCertificateValidationCallback(object sender, CertificateValidationEventArgs e)
        {
            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.CompletedTask;
        }

        //控制要解密SSL的域名
        private async Task ExplicitEndPoint_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string url = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;

            e.DecryptSsl = CheckHost(hostname);


            //ws 方式
            //if (
            //    e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket &&
            //    webcastBarrageReg.IsMatch(url)
            //   )
            //{
            //    e.HttpClient.Request.RequestUri = new Uri("wss:////localhost:8828");
            //}
        }

        //检测域名白名单
        protected override bool CheckHost(string hostname)
        {
            //需要解析SSL的域名 放在这里，全开会导致性能问题，应只解析业务需要的域名
            var decryptSsls = new string[]
            {
                SCRIPT_HOST,
                LIVE_HOST,
                WEBCAST_AMEMV_HOST, //直播伴侣开播请求地址
                "*-webcast-platform.bytetos.com", //新的脚本地址
                "*webcast*" //所有带webcast的域名
            };

            if (decryptSsls.Contains(hostname))
            {
                return true;
            }

            if (hostname.WildcardMatchAny(decryptSsls))
            {
                return true;
            }

            hostname = hostname.Trim().ToLower();
            return base.CheckHost(hostname);
        }

        //WebSocket 流读取
        private void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            string hostname = args.HttpClient.Request.RequestUri.Host;
            var processid = args.HttpClient.ProcessId.Value;
            try
            {
                // 使用自实现解码器，规避 Titanium.Web.Proxy.WebSocketDecoder 的
                // "Destination is too short"（Math.Min 扩容 Bug）。已对各消息做跨帧重组，
                // 不再需要手动拼接 Continuation。
                var decoder = wsDecoders.GetValue(args, k => new WebSocketStreamDecoder());
                var frames = decoder.Decode(e.Buffer, e.Offset, e.Count);
                foreach (var frame in frames)
                {
                    FireWsEvent(new WsMessageEventArgs
                    {
                        ProcessID = processid,
                        HostName = hostname,
                        Payload = frame.Data,
                        CaptureGeneration = decoder.CaptureGeneration,
                        ProcessName = GetProcessName(processid)
                    });
                }
            }
            catch (ProtocolException ex)
            {
                Logger.LogWarn("抓取WS连接解析已停用，等待新连接；代理转发保持运行: " + ex.Message);
            }
            catch (Exception ex)
            {
                if (unexpectedWsErrorThrottle.ShouldLog())
                    Logger.LogWarn("解析WS数据包错误（10秒限频）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }


        /// <summary>
        /// 释放资源，关闭系统代理
        /// </summary>
        public override void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref disposing, 1) != 0) return;
            proxyServer.BeforeResponse -= ProxyServer_BeforeResponse;
            explicitEndPoint.BeforeTunnelConnectRequest -= ExplicitEndPoint_BeforeTunnelConnectRequest;
            roomRequests.Stop().GetAwaiter().GetResult();
            //先停止事件消费线程，避免停代理期间残留任务
            CompleteEventQueue();

            try
            {
                if (proxyServer != null && proxyServer.ProxyRunning)
                {
                    // 设置较短的超时时间，避免长时间等待
                    proxyServer.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"停止代理服务器失败: {ex.Message}");
            }

            try
            {
                proxyServer?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"释放代理服务器失败: {ex.Message}");
            }

            if (AppSetting.Current.UsedProxy)
            {
                try
                {
                    CloseSystemProxy();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"关闭系统代理失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 启动监听
        /// </summary>
        public override void Start()
        {
            proxyServer.Start(false);
            if (AppSetting.Current.UsedProxy)
            {
                RegisterSystemProxy();
                Logger.LogInfo($"代理代理已启动(全局代理) 端口:{ProxyPort}");
                //使用其自带的系统代理设置可能会导致格式问题
                //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
                //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            }
            else
            {
                Logger.LogInfo($"代理已启动(局域代理) 端口:{ProxyPort}");
            }
        }
    }
}

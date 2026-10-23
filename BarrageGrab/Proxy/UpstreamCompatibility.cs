using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DanmakuBackend.Models.JsonEntity;
using Newtonsoft.Json.Linq;

namespace DanmakuBackend.Proxy
{
    // Selective port from apebyte/dy-barrage-grab ce23bde (2026-04-09).
    // Do not import upstream message numbers, authorization or transport here.
    internal static class UpstreamCompatibility
    {
        internal enum ResponseRoute { None, Companion, Barrage, Page, Script }
        private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(200);

        internal static bool IsPath(Uri uri, string path)
        { return uri.AbsolutePath.TrimEnd('/').Equals(path, StringComparison.Ordinal); }

        internal static bool IsIdleScript(string name)
        {
            return name.StartsWith("pausepop", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("island", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsPollingScript(Uri uri)
        {
            return uri.AbsolutePath.Contains("/obj/static/webcast/douyin_live") ||
                   uri.AbsolutePath.Contains("/obj/webcast-platform-cdn/webcast/douyin_live");
        }

        internal static bool TryResolveScriptUri(Uri page, string source, out Uri resolved)
        { return Uri.TryCreate(page, source, out resolved) && (resolved.Scheme == "https" || resolved.Scheme == "http"); }

        internal static ResponseRoute Route(Uri uri, string process, int status, string contentType,
            bool barrageWebSocket, bool forcePolling)
        {
            if (string.IsNullOrWhiteSpace(process)) return ResponseRoute.None;
            if (process == "直播伴侣" && status == 200 &&
                (IsPath(uri, "/webcast/room/create") || IsPath(uri, "/webcast/room/create_info") ||
                 IsPath(uri, "/webcast/room/anchor_finish_info"))) return ResponseRoute.Companion;
            var content = contentType ?? "";
            if (barrageWebSocket || (status == 200 &&
                uri.AbsolutePath.StartsWith("/webcast/im/fetch", StringComparison.Ordinal) &&
                content.IndexOf("application/protobuffer", StringComparison.OrdinalIgnoreCase) >= 0))
                return ResponseRoute.Barrage;
            bool browser = process != "直播伴侣" && process != "douyin";
            if (browser && status == 200 && uri.Host.Equals("live.douyin.com", StringComparison.OrdinalIgnoreCase) &&
                content.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(uri.AbsolutePath, @"^/(?:[0-9a-zA-Z_]+/?)?$", RegexOptions.None, RegexBudget))
                return ResponseRoute.Page;
            if (status == 200 && (content.IndexOf("application/javascript", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("text/javascript", StringComparison.OrdinalIgnoreCase) >= 0) &&
                ((browser && IsIdleScript(Path.GetFileName(uri.AbsolutePath))) ||
                 (forcePolling && IsPollingScript(uri)))) return ResponseRoute.Script;
            return ResponseRoute.None;
        }

        internal static string PausePage(string html, bool autoPause, Uri uri)
        {
            bool pauseQuery = Regex.IsMatch(uri.Query.TrimStart('?'), @"(?:^|&)pause=true(?:&|$)",
                RegexOptions.None, RegexBudget);
            if (!autoPause && !pauseQuery) return html;
            return html.Replace("autoplay&quot;:true", "autoplay&quot;:false")
                .Replace("\"autoplay\":true", "\"autoplay\":false");
        }

        internal static string RewriteScript(string script, bool disableIdle, bool forcePolling, int interval)
        {
            if (disableIdle)
                script = Regex.Replace(script, @"if\(!\(\d{1,},\w{1,}\.DJ\)\(\).+\w{1,}\.includes\(""live""\)\)\)\{",
                    "if(false){", RegexOptions.None, RegexBudget);
            if (forcePolling && Regex.IsMatch(script, @"if\s*\(!this\.stopPolling\)", RegexOptions.None, RegexBudget))
            {
                script = Regex.Replace(script, @"this\.errorInterval\s*=.+?,", "this.pollingInterval=" + interval + ",",
                    RegexOptions.None, RegexBudget);
                script = Regex.Replace(script, @"if\s*\(!this\.stopPolling\)", "if(true)", RegexOptions.None, RegexBudget);
            }
            return script;
        }

        internal static Msg ParseFinish(string json, Func<string, bool> isAuthorized, Func<string, string> webRoomId)
        {
            var owner = JObject.Parse(json)["data"]?["owner"] as JObject;
            var rooms = owner?["own_room"]?["room_ids_str"] as JArray;
            var roomId = rooms?.Values<string>().FirstOrDefault(id => !string.IsNullOrWhiteSpace(id) && isAuthorized(id));
            if (roomId == null) return null;
            return new Msg {
                RoomId = roomId, WebRoomId = webRoomId(roomId), Content = "直播结束",
                Owner = new RoomAnchorInfo {
                    UserId = owner["id"]?.Value<long>() ?? 0,
                    SecUid = owner["sec_uid"]?.Value<string>() ?? "",
                    Nickname = owner["nickname"]?.Value<string>() ?? "",
                    HeadImgUrl = owner["avatar_thumb"]?["url_list"]?.Values<string>().FirstOrDefault() ?? ""
                }
            };
        }
    }
}

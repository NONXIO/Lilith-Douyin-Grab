using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DanmakuBackend.Models.JsonEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Titanium.Web.Proxy;

namespace DanmakuBackend.Proxy
{
    // Deterministic, offline checks shared by the regression runner and protected EXE.
    // Never instantiate TitaniumProxy, AppRuntime, cloud services or audio devices.
    internal static class CaptureSelfTest
    {
        internal static int Run()
        {
            int checks = 0;
            Action<bool, string> check = (ok, name) => {
                if (!ok) throw new InvalidOperationException("Capture regression: " + name);
                checks++;
            };
            var payload = Enumerable.Range(0, 20000).Select(i => (byte)(i & 0xff)).ToArray();
            var wire = Frame(2, true, payload, true);
            var decoder = new WebSocketStreamDecoder();
            var actual = new List<WebSocketFrame>();
            for (int pos = 0; pos < wire.Length; pos += 137)
                actual.AddRange(decoder.Decode(wire, pos, Math.Min(137, wire.Length - pos)));
            check(actual.Count == 1 && actual[0].Data.SequenceEqual(payload), "masked split large frame");
            check(actual[0].OpCode == WebsocketOpCode.Binary && decoder.RetainedCapacity <= 8192, "large buffer released");

            for (int split = 0; split <= 8; split++)
            {
                var small = Frame(1, true, new byte[] { 7, 8 }, true);
                decoder = new WebSocketStreamDecoder();
                actual = decoder.Decode(small, 0, split).ToList();
                actual.AddRange(decoder.Decode(small, split, small.Length - split));
                check(actual.Count == 1 && actual[0].Data.SequenceEqual(new byte[] { 7, 8 }), "mask/header split " + split);
            }

            decoder = new WebSocketStreamDecoder(64, 80, 78);
            var fragmented = Join(Frame(2, false, new byte[40]), Frame(9, true, new byte[] { 1 }),
                Frame(10, true, Array.Empty<byte>()), Frame(0, true, new byte[40]));
            actual = decoder.Decode(fragmented, 0, fragmented.Length).ToList();
            check(actual.Count == 1 && actual[0].Data.Length == 80 && actual[0].OpCode == WebsocketOpCode.Binary, "fragment/control/budget boundary");
            check(decoder.RetainedCapacity == 78, "fragment storage released");
            decoder = new WebSocketStreamDecoder(64, 128, 78);
            var coalesced = Join(Frame(2, true, new byte[40]), Frame(1, true, new byte[40]));
            check(decoder.Decode(coalesced, 0, coalesced.Length).Count() == 2, "input read larger than buffer budget");
            var wide = Frame(2, true, new byte[65536]);
            check(new WebSocketStreamDecoder().Decode(wide, 0, wide.Length).Single().Data.Length == 65536, "64-bit length valid");

            var malformed = new[] {
                new byte[] { 0x82, 0x7f, 0, 0, 0, 0, 4, 0, 0, 1 },
                new byte[] { 0x82, 0x7f, 0x80, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 0x82, 0x7e, 0, 1 },
                new byte[] { 0x82, 0x7f, 0, 0, 0, 0, 0, 0, 0, 126 },
                new byte[] { 0x83, 0 }, new byte[] { 0xc2, 0 },
                new byte[] { 0x09, 0 }, new byte[] { 0x89, 126 }, new byte[] { 0x88, 1 },
                Frame(0, true, Array.Empty<byte>()),
                Join(Frame(2, false, Array.Empty<byte>()), Frame(1, true, Array.Empty<byte>()))
            };
            foreach (var invalid in malformed)
            {
                decoder = new WebSocketStreamDecoder();
                bool failed = false;
                try { decoder.Decode(invalid, 0, invalid.Length); } catch (ProtocolException) { failed = true; }
                check(failed && decoder.IsFaulted && decoder.RetainedCapacity == 0, "invalid frame terminal state");
            }
            var junk = new byte[1024];
            for (int i = 0; i < 20000; i++)
                if (decoder.Decode(junk, 0, junk.Length).Any()) throw new Exception("faulted input emitted data");
            check(decoder.RetainedCapacity == 0, "20000 failed-connection reads do not retain data or throw");
            check(new WebSocketStreamDecoder().Decode(Frame(2, true, new byte[] { 1 }), 0, 3).Count() == 1, "new connection unaffected");

            foreach (var oversized in new[] {
                Frame(2, true, new byte[65]),
                Join(Frame(2, false, new byte[50]), Frame(0, true, new byte[40])),
                Join(Frame(2, true, new byte[60]), Frame(2, true, new byte[60]))
            })
            {
                decoder = new WebSocketStreamDecoder(64, 80, 78);
                bool rejected = false;
                try { decoder.Decode(oversized, 0, oversized.Length); } catch (ProtocolException) { rejected = true; }
                check(rejected && decoder.RetainedCapacity == 0, "frame/message/output budget");
            }
            var emptyFlood = Enumerable.Repeat(new byte[] { 0x82, 0 }, 4097).SelectMany(x => x).ToArray();
            bool flooded = false;
            try { new WebSocketStreamDecoder().Decode(emptyFlood, 0, emptyFlood.Length); } catch (ProtocolException) { flooded = true; }
            check(flooded, "zero-byte output count bounded");
            decoder = new WebSocketStreamDecoder();
            var close = Join(Frame(2, false, new byte[4]), Frame(8, true, Array.Empty<byte>()));
            check(!decoder.Decode(close, 0, close.Length).Any() && decoder.RetainedCapacity == 0, "close releases unfinished message");
            check(!decoder.Decode(wire, 0, wire.Length).Any(), "closed connection terminal");
            decoder = new WebSocketStreamDecoder();
            var empty = Frame(2, true, Array.Empty<byte>());
            Parallel.For(0, 100, _ => { if (decoder.Decode(empty, 0, 2).Count() != 1) throw new Exception("concurrent decode"); });
            check(!decoder.IsFaulted, "decoder state serialization");

            var content = Enumerable.Repeat((byte)42, 1000).ToArray();
            var gzip = Compress(content);
            check(BoundedCapturePayload.Decompress(gzip, 1000).SequenceEqual(content), "gzip exact output budget");
            bool expansion = false;
            try { BoundedCapturePayload.Decompress(gzip, 999); } catch (InvalidDataException) { expansion = true; }
            check(expansion, "gzip expansion rejected");
            bool badGzip = false;
            try { BoundedCapturePayload.Decompress(new byte[] { 1, 2, 3 }); } catch (InvalidDataException) { badGzip = true; }
            check(badGzip, "invalid gzip");
            bool truncated = false;
            try { BoundedCapturePayload.Decompress(gzip.Take(gzip.Length - 4).ToArray()); } catch (InvalidDataException) { truncated = true; }
            check(truncated, "truncated gzip rejected");
            bool badInput = false;
            try { BoundedCapturePayload.Validate(new byte[10], 9); } catch (InvalidDataException) { badInput = true; }
            check(badInput, "input budget");
            long now = 0;
            var throttle = new CaptureErrorThrottle(10000, () => now);
            check(throttle.ShouldLog(), "first payload error logged");
            check(!Enumerable.Range(0, 20000).Any(_ => throttle.ShouldLog()), "repeated payload errors throttled");
            now = 10000;
            check(throttle.ShouldLog() && !throttle.ShouldLog(), "payload log interval resets once");

            var create = new Uri("https://webcast.amemv.com/webcast/room/create/?room_id=1");
            var finish = new Uri("https://webcast.amemv.com/webcast/room/anchor_finish_info");
            check(UpstreamCompatibility.IsPath(create, "/webcast/room/create"), "create slash variant");
            check(UpstreamCompatibility.IsPath(finish, "/webcast/room/anchor_finish_info"), "finish no-slash variant");
            check(UpstreamCompatibility.Route(create, "直播伴侣", 200, "application/json", false, false) == UpstreamCompatibility.ResponseRoute.Companion, "companion route");
            check(UpstreamCompatibility.Route(create, "chrome", 200, "application/json", false, false) == UpstreamCompatibility.ResponseRoute.None, "unrelated process");
            check(UpstreamCompatibility.Route(create, "直播伴侣", 500, "application/json", false, false) == UpstreamCompatibility.ResponseRoute.None, "failed response");
            check(UpstreamCompatibility.Route(new Uri("https://example.com/?x=/webcast/room/create/"), "直播伴侣", 200, "application/json", false, false) == UpstreamCompatibility.ResponseRoute.None, "query is not endpoint");
            var page = new Uri("https://live.douyin.com/abc_1");
            check(UpstreamCompatibility.TryResolveScriptUri(page, "./chunks/PausePop.js", out var relative) && relative.Host == page.Host, "relative script URI");
            check(!UpstreamCompatibility.TryResolveScriptUri(page, "javascript:void(0)", out var ignored), "non-HTTP script ignored");
            check(UpstreamCompatibility.Route(page, "chrome", 200, "text/html", false, false) == UpstreamCompatibility.ResponseRoute.Page, "live page route");
            check(UpstreamCompatibility.Route(new Uri("https://example.com/abc"), "chrome", 200, "text/html", false, false) == UpstreamCompatibility.ResponseRoute.None, "unrelated HTML");
            foreach (var name in new[] { "PausePop.ec38.js", "pausepop.js", "island_a74ce.js" })
            {
                var scriptUri = new Uri("https://lf-webcast-platform.bytetos.com/chunks/" + name);
                check(UpstreamCompatibility.IsIdleScript(name) && UpstreamCompatibility.Route(scriptUri, "chrome", 200, "application/javascript", false, false) == UpstreamCompatibility.ResponseRoute.Script, "idle script " + name);
            }
            var html = "<script>var x={\"autoplay\":true}</script>autoplay&quot;:true";
            check(!UpstreamCompatibility.PausePage(html, true, page).Contains(":true"), "pause HTML variants");
            check(UpstreamCompatibility.PausePage(html, false, page) == html, "pause disabled unchanged");
            check(UpstreamCompatibility.PausePage(html, false, new Uri(page + "?pause=true")) != html, "legacy pause query");
            check(UpstreamCompatibility.PausePage("unchanged", true, page) == "unchanged", "unknown HTML unchanged");
            var idleJs = "if(!(0,a.DJ)()&&(b.includes(\"live\"))){x()}";
            check(UpstreamCompatibility.RewriteScript(idleJs, true, false, 500).StartsWith("if(false){"), "idle rewrite");
            check(UpstreamCompatibility.RewriteScript("unmatched", true, true, 500) == "unmatched", "unknown script unchanged");
            check(UpstreamCompatibility.RewriteScript("if(!this.stopPolling){this.errorInterval=3000,x()}", false, true, 500).Contains("this.pollingInterval=500,"), "polling compatibility");

            const string finishJson = "{\"data\":{\"owner\":{\"id\":123,\"nickname\":\"anchor\",\"own_room\":{\"room_ids_str\":[\"other\",\"licensed\"]}}}}";
            var endMsg = UpstreamCompatibility.ParseFinish(finishJson, id => id == "licensed", _ => "web-id");
            check(endMsg?.RoomId == "licensed" && endMsg.WebRoomId == "web-id" && endMsg.Owner.UserId == 123, "authorized candidate selection");
            check(UpstreamCompatibility.ParseFinish(finishJson, _ => false, _ => "") == null, "unlicensed finish ignored");
            check(UpstreamCompatibility.ParseFinish("{\"data\":{}}", _ => true, _ => "") == null, "missing owner ignored");
            var pack = new DanmakuMessagePack(JsonConvert.SerializeObject(endMsg), PackMsgType.下播, "直播伴侣");
            var packJson = JObject.Parse(JsonConvert.SerializeObject(pack));
            check(packJson["type"].Value<int>() == 9 && packJson["process"].Value<string>() == "直播伴侣", "local finish envelope");
            check((int)PackMsgType.下播 == 9 && (int)PackMsgType.开播 == 18 && (int)PackMsgType.直播伴侣启动 == 19, "local type numbers unchanged");
            check(JObject.Parse(JsonConvert.SerializeObject(endMsg))["room_id"].Value<string>() == "licensed", "local finish Msg fields");
            var createResult = Models.RoomInfo.TryParseStreamPusherCreate(
                "{\"data\":{\"id_str\":\"licensed\",\"status\":2,\"owner\":{\"id_str\":\"123\",\"nickname\":\"anchor\",\"sec_uid\":\"sec\",\"display_id\":\"web-id\"}}}", out var startRoom);
            check(createResult.Item1 == 0 && startRoom.RoomId == "licensed" && startRoom.WebRoomId == "web-id", "existing create parser preserved");
            return checks;
        }

        private static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        private static byte[] Join(params byte[][] arrays) { return arrays.SelectMany(x => x).ToArray(); }

        private static byte[] Frame(int opcode, bool final, byte[] payload, bool masked = false)
        {
            var bytes = new List<byte> { (byte)((final ? 0x80 : 0) | opcode) };
            int mask = masked ? 0x80 : 0;
            if (payload.Length < 126) bytes.Add((byte)(mask | payload.Length));
            else if (payload.Length <= ushort.MaxValue)
            {
                bytes.Add((byte)(mask | 126)); bytes.Add((byte)(payload.Length >> 8)); bytes.Add((byte)(payload.Length & 0xff));
            }
            else
            {
                bytes.Add((byte)(mask | 127));
                for (int shift = 56; shift >= 0; shift -= 8) bytes.Add((byte)(((long)payload.Length >> shift) & 0xff));
            }
            var key = new byte[] { 3, 7, 11, 13 };
            if (masked) bytes.AddRange(key);
            for (int i = 0; i < payload.Length; i++) bytes.Add(masked ? (byte)(payload[i] ^ key[i & 3]) : payload[i]);
            return bytes.ToArray();
        }
    }
}

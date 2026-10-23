using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Threading;

namespace DanmakuBackend.Proxy
{
    internal static class BoundedCapturePayload
    {
        internal const int MaxBytes = WebSocketStreamDecoder.DefaultMaxPayload;

        internal static void Validate(byte[] data, int limit = MaxBytes)
        {
            if (data == null) throw new InvalidDataException("弹幕载荷为空");
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
            if (data.Length > limit) throw new InvalidDataException("弹幕载荷超限");
        }

        internal static byte[] Decompress(byte[] data, int limit = MaxBytes)
        {
            Validate(data);
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
            // Framework GZipStream can accept a truncated trailer. Check ISIZE
            // against actual bounded output instead of delivering partial data.
            if (data.Length < 18 || data[0] != 0x1f || data[1] != 0x8b || data[2] != 8)
                throw new InvalidDataException("无效或不完整的 gzip 弹幕载荷");
            int trailer = data.Length - 4;
            uint expectedSize = (uint)(data[trailer] | data[trailer + 1] << 8 |
                data[trailer + 2] << 16 | data[trailer + 3] << 24);
            if (expectedSize > limit) throw new InvalidDataException("弹幕解压输出超限");
            using (var input = new MemoryStream(data, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var block = new byte[Math.Min(8192, limit)];
                int read;
                while ((read = gzip.Read(block, 0, (int)Math.Min(block.Length, (long)limit - output.Length + 1))) > 0)
                {
                    if (read > limit - output.Length) throw new InvalidDataException("弹幕解压输出超限");
                    int required = (int)output.Length + read;
                    if (required > output.Capacity)
                        output.Capacity = (int)Math.Min(limit, Math.Max((long)required, (long)output.Capacity * 2));
                    output.Write(block, 0, read);
                }
                if (output.Length != expectedSize) throw new InvalidDataException("gzip 弹幕载荷长度不完整");
                return output.ToArray();
            }
        }
    }

    internal sealed class CaptureErrorThrottle
    {
        private readonly Func<long> milliseconds;
        private readonly int interval;
        private long last;

        internal CaptureErrorThrottle(int interval = 10000, Func<long> milliseconds = null)
        {
            if (interval < 1) throw new ArgumentOutOfRangeException(nameof(interval));
            this.interval = interval;
            var clock = Stopwatch.StartNew();
            this.milliseconds = milliseconds ?? (() => clock.ElapsedMilliseconds);
            last = -interval;
        }

        internal bool ShouldLog()
        {
            var now = milliseconds();
            var previous = Interlocked.Read(ref last);
            return now - previous >= interval && Interlocked.CompareExchange(ref last, now, previous) == previous;
        }
    }
}

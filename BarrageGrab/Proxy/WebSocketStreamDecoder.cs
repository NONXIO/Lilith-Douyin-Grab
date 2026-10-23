using System;
using System.Collections.Generic;
using Titanium.Web.Proxy;

namespace DanmakuBackend.Proxy
{
    // Connection-scoped replacement for the dependency's unsafe buffer growth.
    // A protocol fault disables capture for this connection, not proxy forwarding.
    internal sealed class WebSocketStreamDecoder
    {
        private static long nextGeneration;
        internal readonly long CaptureGeneration = System.Threading.Interlocked.Increment(ref nextGeneration);
        internal const int DefaultMaxPayload = 64 * 1024 * 1024;
        private const int MaxHeaderBytes = 14;
        private const int MaxOutputFrames = 4096;
        private readonly object gate = new object();
        private readonly int maxFramePayload, maxMessagePayload, maxBufferedBytes, initialCapacity;
        private byte[] buffer;
        private int bufferLength;
        private byte[] messageBuffer;
        private int messageLength;
        private bool fragmented, faulted, closed;
        private WebsocketOpCode messageOpCode;

        public WebSocketStreamDecoder(int maxFramePayload = DefaultMaxPayload,
            int maxMessagePayload = DefaultMaxPayload,
            int maxBufferedBytes = DefaultMaxPayload + MaxHeaderBytes)
        {
            if (maxFramePayload < 1 || maxFramePayload > int.MaxValue - MaxHeaderBytes ||
                maxMessagePayload < 1 || maxBufferedBytes < maxFramePayload + MaxHeaderBytes)
                throw new ArgumentOutOfRangeException(nameof(maxFramePayload));
            this.maxFramePayload = maxFramePayload;
            this.maxMessagePayload = maxMessagePayload;
            this.maxBufferedBytes = maxBufferedBytes;
            initialCapacity = Math.Min(8192, maxBufferedBytes);
            buffer = new byte[initialCapacity];
        }

        internal bool IsFaulted { get { lock (gate) return faulted; } }
        internal int RetainedCapacity { get { lock (gate) return buffer.Length + (messageBuffer?.Length ?? 0); } }

        public IEnumerable<WebSocketFrame> Decode(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            lock (gate)
            {
                if (faulted || closed) return Array.Empty<WebSocketFrame>();
                var frames = new List<WebSocketFrame>();
                int outputBytes = 0;
                try
                {
                    // A read may contain many valid frames: inspect bounded pieces.
                    while (count > 0 && !closed)
                    {
                        int take = Math.Min(count, maxBufferedBytes - bufferLength);
                        if (take == 0) throw new ProtocolException("WebSocket 接收缓冲超限");
                        Append(data, offset, take);
                        offset += take;
                        count -= take;
                        ParseAvailable(frames, ref outputBytes);
                    }
                    TrimBuffer();
                    return frames;
                }
                catch (ProtocolException)
                {
                    faulted = true;
                    ReleaseBuffers();
                    throw; // Only the first fault throws; later reads retain nothing.
                }
            }
        }

        private void ParseAvailable(List<WebSocketFrame> frames, ref int outputBytes)
        {
            int pos = 0;
            while (pos < bufferLength)
            {
                if (!TryParseHeader(pos, out int headerLength, out WebsocketOpCode opcode,
                    out bool final, out bool masked, out int length)) break;
                bool control = (int)opcode >= 8;
                if (!control)
                {
                    if (opcode == WebsocketOpCode.Continuation && !fragmented)
                        throw new ProtocolException("WebSocket 孤立的续帧");
                    if (opcode != WebsocketOpCode.Continuation && fragmented)
                        throw new ProtocolException("WebSocket 分片未完成时出现新消息");
                    if (length > maxMessagePayload - messageLength)
                        throw new ProtocolException("WebSocket 累计消息超限");
                }
                int maskLength = masked ? 4 : 0;
                int frameLength = headerLength + maskLength + length;
                if (frameLength > bufferLength - pos) break;
                int payloadOffset = pos + headerLength + maskLength;
                if (control)
                {
                    pos += frameLength;
                    if (opcode == WebsocketOpCode.ConnectionClose)
                    {
                        closed = true;
                        ReleaseBuffers();
                        return;
                    }
                    continue;
                }

                byte[] payload = new byte[length];
                if (masked)
                {
                    int maskPos = pos + headerLength;
                    for (int i = 0; i < length; i++)
                        payload[i] = (byte)(buffer[payloadOffset + i] ^ buffer[maskPos + (i & 3)]);
                }
                else Buffer.BlockCopy(buffer, payloadOffset, payload, 0, length);
                pos += frameLength;

                if (opcode != WebsocketOpCode.Continuation && final)
                    AddOutput(frames, opcode, payload, ref outputBytes);
                else
                {
                    if (opcode != WebsocketOpCode.Continuation)
                    {
                        fragmented = true;
                        messageOpCode = opcode;
                    }
                    EnsureMessageCapacity(length);
                    Buffer.BlockCopy(payload, 0, messageBuffer, messageLength, length);
                    messageLength += length;
                    if (final)
                    {
                        byte[] full = new byte[messageLength];
                        if (messageLength > 0) Buffer.BlockCopy(messageBuffer, 0, full, 0, messageLength);
                        AddOutput(frames, messageOpCode, full, ref outputBytes);
                        messageBuffer = null;
                        messageLength = 0;
                        fragmented = false;
                    }
                }
            }
            if (pos > 0)
            {
                int remaining = bufferLength - pos;
                if (remaining > 0) Buffer.BlockCopy(buffer, pos, buffer, 0, remaining);
                bufferLength = remaining;
            }
        }

        private void AddOutput(List<WebSocketFrame> frames, WebsocketOpCode opcode, byte[] data, ref int bytes)
        {
            if (frames.Count >= MaxOutputFrames || data.Length > maxMessagePayload - bytes)
                throw new ProtocolException("WebSocket 单次解码输出超限");
            bytes += data.Length;
            frames.Add(new WebSocketFrame(opcode, data, true));
        }

        private void Append(byte[] data, int offset, int count)
        {
            int required = bufferLength + count;
            if (required > buffer.Length)
                Array.Resize(ref buffer, (int)Math.Min(maxBufferedBytes,
                    Math.Max((long)required, (long)buffer.Length * 2)));
            Buffer.BlockCopy(data, offset, buffer, bufferLength, count);
            bufferLength = required;
        }

        private void EnsureMessageCapacity(int additional)
        {
            if (additional > maxMessagePayload - messageLength)
                throw new ProtocolException("WebSocket 累计消息超限");
            int required = messageLength + additional;
            if (messageBuffer == null || required > messageBuffer.Length)
                Array.Resize(ref messageBuffer, (int)Math.Min(maxMessagePayload,
                    Math.Max((long)required, messageBuffer == null ? 1024 : (long)messageBuffer.Length * 2)));
        }

        private void TrimBuffer()
        {
            if (!closed && buffer.Length > initialCapacity && bufferLength <= initialCapacity)
                Array.Resize(ref buffer, initialCapacity);
        }

        private void ReleaseBuffers()
        {
            buffer = Array.Empty<byte>();
            messageBuffer = null;
            bufferLength = messageLength = 0;
            fragmented = false;
        }

        private bool TryParseHeader(int pos, out int headerLength, out WebsocketOpCode opcode,
            out bool final, out bool masked, out int length)
        {
            headerLength = length = 0;
            opcode = 0;
            final = masked = false;
            if (bufferLength - pos < 2) return false;
            byte b0 = buffer[pos], b1 = buffer[pos + 1];
            final = (b0 & 0x80) != 0;
            opcode = (WebsocketOpCode)(b0 & 0x0f);
            masked = (b1 & 0x80) != 0;
            int code = (int)opcode;
            if ((b0 & 0x70) != 0) throw new ProtocolException("WebSocket 不支持的扩展位");
            if (code != 0 && code != 1 && code != 2 && code != 8 && code != 9 && code != 10)
                throw new ProtocolException("WebSocket 非法操作码");
            int len7 = b1 & 0x7f, index = pos + 2;
            if (code >= 8 && (!final || len7 > 125 || (code == 8 && len7 == 1)))
                throw new ProtocolException("WebSocket 非法控制帧");
            long size = len7;
            if (len7 == 126)
            {
                if (bufferLength - index < 2) return false;
                size = (buffer[index] << 8) | buffer[index + 1];
                index += 2;
                if (size < 126) throw new ProtocolException("WebSocket 非最短长度编码");
            }
            else if (len7 == 127)
            {
                if (bufferLength - index < 8) return false;
                if ((buffer[index] & 0x80) != 0) throw new ProtocolException("WebSocket 非法帧长度");
                size = 0;
                for (int i = 0; i < 8; i++) size = (size << 8) | buffer[index + i];
                index += 8;
                if (size <= ushort.MaxValue) throw new ProtocolException("WebSocket 非最短长度编码");
            }
            if (size > maxFramePayload) throw new ProtocolException("WebSocket 帧载荷过大: " + size);
            length = (int)size;
            headerLength = index - pos;
            return true;
        }
    }

    internal sealed class WebSocketFrame
    {
        public WebsocketOpCode OpCode { get; }
        public byte[] Data { get; }
        public bool IsFinal { get; }
        public WebSocketFrame(WebsocketOpCode opCode, byte[] data, bool isFinal)
        { OpCode = opCode; Data = data; IsFinal = isFinal; }
    }

    internal sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }
}

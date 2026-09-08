using System;
using System.Collections.Generic;
using Titanium.Web.Proxy;

namespace DanmakuBackend.Proxy
{
    /// <summary>
    /// 注意：Titanium.Web.Proxy.WebSocketDecoder 内部缓存扩容用的是 Math.Min，
    /// 当单次读取超过其内部缓冲的 2 倍时会崩溃（"Destination is too short"）。
    /// 该 Bug 在 3.2.2-beta 乃至最新的 3.2.3 稳定版中都未修复（仅未发布的 develop 分支改为 Math.Max）。
    /// 因此这里自实现一个 WebSocket 帧解码器，仅在需要时扩容（Math.Max），
    /// 并对跨读取、跨帧（continuation）的拆分消息做重组。
    /// </summary>
    internal sealed class WebSocketStreamDecoder
    {
        // 每条连接持有一个实例，内部维护跨读取的缓冲状态。
        private byte[] buffer = new byte[8192];
        private int bufferLength;

        // 跨帧消息重组状态（Text/Binary 起始帧 + 若干 Continuation 帧）。
        private byte[] messageBuffer;
        private int messageLength;
        private WebsocketOpCode messageOpCode;

        // 单帧载荷上限（防止畸形长度导致无限扩容/内存占用）。
        private const int MaxFramePayload = 64 * 1024 * 1024;

        public IEnumerable<WebSocketFrame> Decode(byte[] data, int offset, int count)
        {
            Append(data, offset, count);

            var frames = new List<WebSocketFrame>();
            int pos = 0;

            while (pos < bufferLength)
            {
                if (!TryParseHeader(pos, out int headerBaseLength, out WebsocketOpCode opcode,
                    out bool isFinal, out bool masked, out int payloadLength))
                {
                    // 头部不足，等待更多数据
                    break;
                }

                int maskKeyLength = masked ? 4 : 0;
                int frameLength = headerBaseLength + maskKeyLength + payloadLength;
                if (pos + frameLength > bufferLength)
                {
                    // 载荷尚未到齐
                    break;
                }

                // 复制载荷（客户端->服务端帧通常带掩码，需解掩码）
                byte[] payload = new byte[payloadLength];
                int payloadOffset = pos + headerBaseLength + maskKeyLength;
                if (masked)
                {
                    int maskPos = pos + headerBaseLength;
                    for (int i = 0; i < payloadLength; i++)
                    {
                        payload[i] = (byte)(buffer[payloadOffset + i] ^ buffer[maskPos + (i & 3)]);
                    }
                }
                else
                {
                    Buffer.BlockCopy(buffer, payloadOffset, payload, 0, payloadLength);
                }

                pos += frameLength;

                // 控制帧不参与消息重组，直接忽略（无需交给上层）。
                if (opcode == WebsocketOpCode.Ping ||
                    opcode == WebsocketOpCode.Pong ||
                    opcode == WebsocketOpCode.ConnectionClose)
                {
                    continue;
                }

                // 数据帧：Text(1)/Binary(2) 起始帧 + Continuation(0) 续帧
                if (opcode == WebsocketOpCode.Text || opcode == WebsocketOpCode.Binary)
                {
                    if (isFinal)
                    {
                        // 无续帧的完整消息
                        frames.Add(new WebSocketFrame(opcode, payload, true));
                    }
                    else
                    {
                        // 开始一条可能被拆分的新消息
                        EnsureMessageCapacity(payloadLength);
                        Buffer.BlockCopy(payload, 0, messageBuffer, messageLength, payloadLength);
                        messageLength += payloadLength;
                        messageOpCode = opcode;
                    }
                }
                else if (opcode == WebsocketOpCode.Continuation)
                {
                    EnsureMessageCapacity(payloadLength);
                    Buffer.BlockCopy(payload, 0, messageBuffer, messageLength, payloadLength);
                    messageLength += payloadLength;

                    if (isFinal)
                    {
                        byte[] full = new byte[messageLength];
                        Buffer.BlockCopy(messageBuffer, 0, full, 0, messageLength);
                        frames.Add(new WebSocketFrame(messageOpCode, full, true));
                        messageLength = 0;
                    }
                }
            }

            // 压缩缓冲，把未消费的剩余字节挪到最前
            if (pos > 0)
            {
                int remaining = bufferLength - pos;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(buffer, pos, buffer, 0, remaining);
                }
                bufferLength = remaining;
            }

            return frames;
        }

        /// <summary>
        /// 追加新读取到的字节，必要时按 Math.Max 扩容（修复库中 Math.Min 的 Bug）。
        /// </summary>
        private void Append(byte[] data, int offset, int count)
        {
            if (count == 0) return;

            int requiredLength = bufferLength + count;
            if (requiredLength > buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Max(requiredLength, buffer.Length * 2));
            }

            Buffer.BlockCopy(data, offset, buffer, bufferLength, count);
            bufferLength += count;
        }

        private void EnsureMessageCapacity(int additional)
        {
            int requiredLength = messageLength + additional;
            if (messageBuffer == null || requiredLength > messageBuffer.Length)
            {
                int newLength = messageBuffer == null
                    ? Math.Max(requiredLength, 1024)
                    : (int)Math.Max(requiredLength, messageBuffer.Length * 2);
                if (messageBuffer == null)
                {
                    messageBuffer = new byte[newLength];
                }
                else
                {
                    Array.Resize(ref messageBuffer, newLength);
                }
            }
        }

        /// <summary>
        /// 解析单个 WebSocket 帧的头部。headerBaseLength 不含掩码 4 字节。
        /// </summary>
        private bool TryParseHeader(int pos, out int headerBaseLength, out WebsocketOpCode opcode,
            out bool isFinal, out bool masked, out int payloadLength)
        {
            headerBaseLength = 0;
            opcode = 0;
            isFinal = false;
            masked = false;
            payloadLength = 0;

            // 至少 2 字节：FIN+opcode 和 mask+len
            if (bufferLength - pos < 2) return false;

            byte b0 = buffer[pos];
            byte b1 = buffer[pos + 1];

            isFinal = (b0 & 0x80) != 0;
            opcode = (WebsocketOpCode)(b0 & 0x0F);
            masked = (b1 & 0x80) != 0;
            int len7 = b1 & 0x7F;

            int index = pos + 2;
            long size;

            if (len7 == 126)
            {
                if (bufferLength - index < 2) return false;
                size = (buffer[index] << 8) | buffer[index + 1];
                index += 2;
            }
            else if (len7 == 127)
            {
                if (bufferLength - index < 8) return false;
                if ((buffer[index] & 0x80) != 0) throw new ProtocolException("非法 WebSocket 帧长度");
                size = ((long)buffer[index] << 56) | ((long)buffer[index + 1] << 48) |
                       ((long)buffer[index + 2] << 40) | ((long)buffer[index + 3] << 32) |
                       ((long)buffer[index + 4] << 24) | ((long)buffer[index + 5] << 16) |
                       ((long)buffer[index + 6] << 8) | buffer[index + 7];
                index += 8;
            }
            else
            {
                size = len7;
            }

            if (size > MaxFramePayload) throw new ProtocolException($"WebSocket 帧载荷过大: {size}");

            payloadLength = (int)size;
            headerBaseLength = index - pos;
            return true;
        }
    }

    /// <summary>
    /// 解码后的 WebSocket 帧结果。
    /// </summary>
    internal sealed class WebSocketFrame
    {
        public WebsocketOpCode OpCode { get; }
        public byte[] Data { get; }
        public bool IsFinal { get; }

        public WebSocketFrame(WebsocketOpCode opCode, byte[] data, bool isFinal)
        {
            OpCode = opCode;
            Data = data;
            IsFinal = isFinal;
        }
    }

    internal sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }
}

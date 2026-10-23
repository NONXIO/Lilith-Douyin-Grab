using System;
using System.Reflection;
using DanmakuBackend.Proxy;

// Historical pre-P0 failure audit, intentionally NOT the fixed-decoder test.
// Keep the vulnerable-source compiled evidence; use test-capture-regression.ps1
// for current source. No network traffic or system proxy is used here.
class DecoderAccumulationAudit
{
    static void Main()
    {
        var decoder = new WebSocketStreamDecoder();
        var invalidHeader = new byte[] { 0x82, 0x7f, 0, 0, 0, 0, 4, 0, 0, 1 }; // 64 MiB + 1
        int errors = 0;
        try { decoder.Decode(invalidHeader, 0, invalidHeader.Length); }
        catch (ProtocolException) { errors++; }
        var chunk = new byte[1024];
        for (int i = 0; i < 64; i++) {
            try { decoder.Decode(chunk, 0, chunk.Length); }
            catch (ProtocolException) { errors++; }
        }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int retained = (int)typeof(WebSocketStreamDecoder).GetField("bufferLength", flags).GetValue(decoder);
        int capacity = ((byte[])typeof(WebSocketStreamDecoder).GetField("buffer", flags).GetValue(decoder)).Length;
        if (errors != 65 || retained != 65546) throw new Exception("Failure path changed; reevaluate audit");
        Console.WriteLine("Decoder failure-path reproduced: errors=" + errors + ", retainedBytes=" + retained + ", capacityBytes=" + capacity);
    }
}

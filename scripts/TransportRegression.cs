using System;
using System.Linq;
using System.Reflection;
using System.Text;

class TransportRegression
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var suite = assembly.GetType("DanmakuBackend.Server.TransportSelfTest");
            var checks = suite.GetMethod("Run", flags).Invoke(null, null);
            Console.WriteLine("TRANSPORT_REGRESSION_PASS checks=" + checks);
            var server = assembly.GetType("DanmakuBackend.Server.WsBarrageServer");
            var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
            var payload = "{\"type\":5,\"data\":\"{\\\"room_id\\\":\\\"fixture\\\",\\\"incremental\\\":2}\",\"process\":\"test\"}";
            var encrypted = server.GetMethod("EncryptForTransportTest", flags).Invoke(null, new object[] { key, payload });
            Console.WriteLine("TRANSPORT_WIRE_FIXTURE " + encrypted);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

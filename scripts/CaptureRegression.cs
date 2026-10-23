using System;
using DanmakuBackend.Proxy;

class CaptureRegression
{
    static int Main()
    {
        try { Console.WriteLine("CAPTURE_REGRESSION_PASS checks=" + CaptureSelfTest.Run()); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

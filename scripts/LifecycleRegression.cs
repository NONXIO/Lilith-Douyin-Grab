using System;
using System.Threading;
using System.Threading.Tasks;
using DanmakuBackend.Server;
using DanmakuBackend.Proxy;
using DanmakuBackend.Models;
class LifecycleRegression
{
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void Main() { Run().GetAwaiter().GetResult(); }
    static async Task Run()
    {
        var now = DateTime.UtcNow; string active = "active";
        var cache = new BoundedRoomCache(32, TimeSpan.FromMinutes(1), () => now, () => active);
        cache.SetRoomCache(active, "pinned");
        for (var i=0;i<100000;i++) cache.SetRoomCache(i.ToString(), "web"+i);
        Check(cache.Count == 32 && cache.ReverseCount == 32,"cache capacity");
        Check(cache.GetByWebRoomid("pinned")?.RoomId == active,"active pin");
        cache.SetRoomCache(active,"changed");
        Check(cache.GetByWebRoomid("pinned") == null && cache.GetByWebRoomid("changed") != null,"reverse replacement");
        now=now.AddMinutes(2); Check(cache.Count==1,"TTL keeps active only");
        active="new";now=now.AddSeconds(31);Check(cache.Count==0,"unpin expires");
        var window=new FixedMessageWindow();
        for(long i=0;i<300;i++) Check(window.Accept("chat",i,"room",1),"fresh");
        Check(!window.Accept("chat",0,"room",1),"duplicate does not touch FIFO");
        Check(window.Accept("chat",300,"room",1) && window.Accept("chat",0,"room",1),"300 window parity");
        Check(window.Accept("chat",0,"room",2),"new connection isolated");
        Check(!window.Accept("chat",300,"room",1),"parallel connection window retained");
        for(long i=3;i<10000;i++) window.Accept("chat",1,"room",i);
        Check(window.Count<=8,"connection scope budget");
        Check(window.Accept("chat",1,"other-room",9999),"room reset");
        int committed=0,calls=0;
        var requests=new RoomRequestCoordinator<string>(()=>now);
        var held=new TaskCompletionSource<string>();
        var first=requests.Run("same",token=>{calls++;return held.Task;},value=>committed++);
        Check(!await requests.Run("same",token=>{calls++;return Task.FromResult("x");},value=>committed++),"atomic single entry");
        held.SetResult("ok");Check(await first && committed==1 && calls==1 && requests.RunningCount==0,"success cleanup");
        Check(!await requests.Run("null",token=>Task.FromResult<string>(null),value=>committed++),"null terminal");
        Check(!await requests.Run("null",token=>Task.FromResult("ok"),value=>committed++),"null backoff");
        now=now.AddSeconds(6);Check(await requests.Run("null",token=>Task.FromResult("ok"),value=>committed++),"retry after null");
        Check(!await requests.Run("error",token=>{throw new Exception("failure");},value=>committed++),"exception cleanup");
        now=now.AddSeconds(6);Check(await requests.Run("error",token=>Task.FromResult("ok"),value=>committed++),"retry after exception");
        Check(!await requests.Run("cancel",token=>Task.FromCanceled<string>(new CancellationToken(true)),value=>committed++),"cancel cleanup");
        now=now.AddSeconds(6);Check(await requests.Run("cancel",token=>Task.FromResult("ok"),value=>committed++),"retry after cancellation");
        for(int i=0;i<1000;i++) await requests.Run("failure"+i,token=>Task.FromResult<string>(null),value=>committed++);
        Check(requests.RetryCount<=128 && requests.RunningCount==0,"failure cache bounded");
        var late=new TaskCompletionSource<string>();
        var pending=requests.Run("late",token=>late.Task,value=>committed++);
        var before=committed;await requests.Stop(20);late.SetResult("stale");Check(!await pending && committed==before,"cancelled late result cannot commit");
        Check(!await requests.Run("closed",token=>Task.FromResult("no"),value=>committed++),"stop rejects producers");
        Console.WriteLine("PASS: 100000 rooms, TTL/pin/reverse, fixed 300 FIFO, parallel connection isolation, success/null/error/retry/cancel and bounded request state.");
    }
}
namespace DanmakuBackend.Models { public class RoomInfo { public string RoomId,WebRoomId,Title; } }
namespace DanmakuBackend { internal static class Logger { internal static void LogWarn(string value) {} } }

using System;
using System.IO;
using System.Text;
using DanmakuBackend.Utility;
using Microsoft.Win32;
using Newtonsoft.Json;
class OwnershipRegression
{
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void Main()
    {
        var fake = Registry.CurrentUser;
        var native = new WindowsProxyConnection.State { Flags=9, Server="connection-old", Bypass="old-bypass", AutoConfigUrl="https://synthetic.invalid/proxy.pac" };
        var nativeOriginal = JsonConvert.DeserializeObject<WindowsProxyConnection.State>(JsonConvert.SerializeObject(native));
        int nativeWrites=0, notices=0;
        bool failNotify=false;
        OwnedProxySettings.ReadConnection=()=>JsonConvert.DeserializeObject<WindowsProxyConnection.State>(JsonConvert.SerializeObject(native));
        OwnedProxySettings.WriteConnection=value=> {
            nativeWrites++;
            native=JsonConvert.DeserializeObject<WindowsProxyConnection.State>(JsonConvert.SerializeObject(value));
            // Simulate API side effects; raw registry kinds/absence must still be restored afterward.
            fake.SetValue("ProxyEnable",(value.Flags&2)!=0?1:0,RegistryValueKind.DWord);
            fake.SetValue("ProxyServer",value.Server??"",RegistryValueKind.String);
            fake.SetValue("ProxyOverride",value.Bypass??"",RegistryValueKind.String);
        };
        OwnedProxySettings.NotifyConnection=()=> { notices++;if(failNotify)throw new InvalidOperationException("simulated notify failure"); };
        fake.SetValue("ProxyEnable",0,RegistryValueKind.DWord);
        fake.SetValue("ProxyServer","user-proxy",RegistryValueKind.ExpandString);
        OwnedProxySettings.Apply(8811,"<local>;*.supabase.co");
        Check((int)fake.GetValue("ProxyEnable")==1,"apply");
        Check(native.Flags==3 && native.Server=="127.0.0.1:8811" && native.AutoConfigUrl==nativeOriginal.AutoConfigUrl,"connection manual proxy flag and PAC retained");
        Check(OwnedProxySettings.Restore(),"restore owned");
        Check((int)fake.GetValue("ProxyEnable")==0 && fake.GetValueKind("ProxyServer")==RegistryValueKind.ExpandString,"original kinds restored");
        Check(fake.GetValue("ProxyOverride")==null,"original absence restored");
        Check(WindowsProxyConnection.Matches(native,nativeOriginal) && notices==2,"native original and notifications restored");
        OwnedProxySettings.Apply(8811,"<local>");
        OwnedProxySettings.Apply(8812,"<local>");
        Check(OwnedProxySettings.Restore() && WindowsProxyConnection.Matches(native,nativeOriginal),"reapply preserves first native baseline");
        OwnedProxySettings.Apply(8811,"<local>");
        fake.SetValue("ProxyServer","user-new-value",RegistryValueKind.String);
        int writes=fake.Writes;
        Check(!OwnedProxySettings.Restore() && fake.Writes==writes && (string)fake.GetValue("ProxyServer")=="user-new-value","user proxy conflict preserved");
        OwnedProxySettings.Apply(8811,"<local>");
        Check(OwnedProxySettings.Restore() && (string)fake.GetValue("ProxyServer")=="user-new-value","new ownership uses user's current baseline");
        OwnedProxySettings.Apply(8811,"<local>");
        native.Flags=9;
        int nativeBefore=nativeWrites, registryBefore=fake.Writes;
        Check(!OwnedProxySettings.Restore() && nativeWrites==nativeBefore && fake.Writes==registryBefore,"connection-only user flag conflict preserved");
        OwnedProxySettings.Apply(8811,"<local>");
        native.AutoConfigUrl="https://user.invalid/new.pac";
        Check(!OwnedProxySettings.Restore(),"connection-only PAC conflict preserved");
        OwnedProxySettings.Apply(8811,"<local>");
        Check(OwnedProxySettings.Restore() && native.AutoConfigUrl=="https://user.invalid/new.pac","user PAC baseline restored");
        failNotify=true;
        try { OwnedProxySettings.Apply(8811,"<local>");throw new Exception("Expected notification failure"); }
        catch(InvalidOperationException) { }
        failNotify=false;
        Check(OwnedProxySettings.Restore(),"failed notification leaves recoverable ownership journal");
        OwnedProxySettings.Apply(8811,"<local>");
        var journal=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"proxy-ownership.json");
        var legacy=JsonConvert.DeserializeObject<OwnedProxySettings.Record>(File.ReadAllText(journal));
        legacy.Version=1;legacy.OriginalConnection=null;legacy.AppliedConnection=null;
        File.WriteAllText(journal,JsonConvert.SerializeObject(legacy));
        nativeBefore=nativeWrites;
        Check(OwnedProxySettings.Restore() && nativeWrites==nativeBefore,"legacy journal never writes unowned connection settings");
        OwnedProxySettings.Apply(8811,"<local>");
        legacy=JsonConvert.DeserializeObject<OwnedProxySettings.Record>(File.ReadAllText(journal));
        legacy.Version=1;legacy.OriginalConnection=null;legacy.AppliedConnection=null;
        File.WriteAllText(journal,JsonConvert.SerializeObject(legacy));
        var beforeMigration=OwnedProxySettings.ReadConnection();
        OwnedProxySettings.Apply(8822,"<local>");
        Check(OwnedProxySettings.Restore() && WindowsProxyConnection.Matches(native,beforeMigration),"active legacy migration snapshots only current native baseline");
        // Actual file helper, synthetic file in this disposable test directory.
        var target=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"synthetic-index.js");
        byte[] original={239,187,191,97,98,99};File.WriteAllBytes(target,original);
        var file=new OwnedCompanionFile(target);
        file.Write(Encoding.UTF8.GetBytes("patched"),OwnedCompanionFile.Hash(original));
        Check(file.Restore() && OwnedCompanionFile.Hash(File.ReadAllBytes(target))==OwnedCompanionFile.Hash(original),"exact original bytes/BOM restored");
        file.Write(Encoding.UTF8.GetBytes("patched"),OwnedCompanionFile.Hash(original));
        File.WriteAllText(target,"user-content");
        Check(!file.Restore() && File.ReadAllText(target)=="user-content","user file conflict preserved");
        Check(File.Exists(target+".lilith-original.bak"),"backup retained on conflict");
        try { file.Write(Encoding.UTF8.GetBytes("overwrite"),OwnedCompanionFile.Hash(File.ReadAllBytes(target)));throw new Exception("Expected ownership conflict"); }
        catch(InvalidDataException) {}
        Console.WriteLine("PASS: simulated connection flags/PAC/notification/recovery/legacy ownership and registry kinds/absence/conflicts; companion bytes/backup preserved. No native proxy writes used.");
    }
}
namespace DanmakuBackend { internal static class Logger { internal static void LogWarn(string text) {} internal static void LogInfo(string text) {} } }
// These local types replace Win32 calls only in this separately compiled fixture.
namespace Microsoft.Win32
{
    public enum RegistryValueKind { None=-1,Unknown=0,String=1,ExpandString=2,Binary=3,DWord=4,MultiString=7,QWord=11 }
    public enum RegistryValueOptions { None,DoNotExpandEnvironmentNames }
    public static class Registry { public static RegistryKey CurrentUser=new RegistryKey(); }
    public class RegistryKey:IDisposable
    {
        readonly System.Collections.Generic.Dictionary<string,object> data=new System.Collections.Generic.Dictionary<string,object>();
        readonly System.Collections.Generic.Dictionary<string,RegistryValueKind> kinds=new System.Collections.Generic.Dictionary<string,RegistryValueKind>();
        public int Writes;
        public RegistryKey OpenSubKey(string name,bool writable) { if(name!="Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings")throw new Exception("Unexpected key");return this; }
        public object GetValue(string name,object fallback=null,RegistryValueOptions option=RegistryValueOptions.None) { return data.TryGetValue(name,out var value)?value:fallback; }
        public RegistryValueKind GetValueKind(string name) { return kinds[name]; }
        public void SetValue(string name,object value,RegistryValueKind kind) { Writes++;data[name]=value;kinds[name]=kind; }
        public void DeleteValue(string name,bool strict) { Writes++;data.Remove(name);kinds.Remove(name); }
        public void Dispose() {}
    }
}

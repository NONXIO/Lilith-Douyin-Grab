using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using Newtonsoft.Json;

namespace DanmakuBackend.Utility
{
    internal sealed class OwnedCompanionFile
    {
        internal sealed class Record { public int Version = 1; public string Owner, OriginalHash, WrittenHash; public bool Restored; }
        private readonly string target, backup, journal;
        private string Owner => WindowsIdentity.GetCurrent().User.Value;
        internal OwnedCompanionFile(string target)
        {
            this.target = Path.GetFullPath(target);
            backup = this.target + ".lilith-original.bak";
            journal = this.target + ".lilith-owner.json";
        }
        internal static string Hash(Stream stream)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""); }
        internal static string Hash(byte[] bytes) { using (var stream = new MemoryStream(bytes)) return Hash(stream); }
        private Record Load()
        {
            if (!File.Exists(journal)) return null;
            if (new FileInfo(journal).Length > 4096) throw new InvalidDataException("File ownership record too large");
            return JsonConvert.DeserializeObject<Record>(File.ReadAllText(journal));
        }
        private void Save(Record record) { File.WriteAllText(journal, JsonConvert.SerializeObject(record)); }
        internal bool Restore()
        {
            var record = Load();
            if (record == null || record.Restored) return true;
            if (record.Version != 1 || record.Owner != Owner) return Conflict();
            using (var current = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var hash = Hash(current);
                if (hash == record.OriginalHash) { record.Restored = true; Save(record); return true; }
                if (hash != record.WrittenHash || !File.Exists(backup)) return Conflict();
                using (var original = File.OpenRead(backup))
                {
                    if (Hash(original) != record.OriginalHash) return Conflict();
                    original.Position = 0; current.Position = 0;
                    original.CopyTo(current); current.SetLength(original.Length); current.Flush(true);
                }
            }
            record.Restored = true; Save(record); return true;
        }
        private bool Conflict() { Logger.LogWarn("伴侣文件恢复冲突：内容或所有权已变化，保留当前文件和备份"); return false; }
        internal void Write(byte[] written, string expectedOriginalHash)
        {
            var previous = Load();
            if (File.Exists(backup) && previous == null) throw new InvalidDataException("Unowned companion backup exists");
            if (previous != null && (previous.Owner != Owner || previous.Version != 1 || !previous.Restored)) throw new InvalidDataException("Companion ownership is not ready for a new write");
            if (File.Exists(backup)) using (var stored = File.OpenRead(backup))
                if (Hash(stored) != previous.OriginalHash) throw new InvalidDataException("Companion backup changed");
            using (var current = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var originalHash = Hash(current);
                if (originalHash != expectedOriginalHash) throw new InvalidDataException("Companion changed before write");
                var record = new Record { Owner = Owner, OriginalHash = originalHash, WrittenHash = Hash(written) };
                Save(record);
                current.Position = 0;
                using (var original = new FileStream(backup, FileMode.Create, FileAccess.Write, FileShare.None)) { current.CopyTo(original); original.Flush(true); }
                current.Position = 0; current.Write(written, 0, written.Length); current.SetLength(written.Length); current.Flush(true);
            }
        }
    }
}

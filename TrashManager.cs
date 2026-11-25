using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Android.Content;
using Android.Systems;
using Android.Util;
using Newtonsoft.Json;

namespace AhoChatA
{
    public class TrashManager
    {
        private static readonly Lazy<TrashManager> _instance = new Lazy<TrashManager>(() => new TrashManager(AhoChatApp.Context));
        private readonly object _lock = new object();
        private readonly Context _ctx;
        private readonly string _trashDir;
        private readonly string _metaFile;
        private long _retentionDays = 30;

        public static TrashManager Instance => _instance.Value;

        private TrashManager(Context ctx)
        {
            _ctx = ctx.ApplicationContext;
            _trashDir = Normalize(Path.Combine(_ctx.FilesDir.CanonicalPath, ".trash"));
            _metaFile = Path.Combine(_trashDir, "meta.json");
            try { _retentionDays = Math.Max(1, _ctx.GetSharedPreferences("trash_prefs", FileCreationMode.Private).GetLong("retention_days", 30)); }
            catch { }
        }

        public long RetentionDays
        {
            get { lock (_lock) return _retentionDays; }
            set
            {
                lock (_lock)
                {
                    _retentionDays = Math.Max(1, value);
                    try { _ctx.GetSharedPreferences("trash_prefs", FileCreationMode.Private).Edit().PutLong("retention_days", _retentionDays).Apply(); }
                    catch { }
                }
            }
        }

        internal static bool IsShareDeleteTarget(string root, string path)
        {
            try
            {
                root = Normalize(root);
                path = Normalize(path);
                return path != root && IsWithin(path, root);
            }
            catch { return false; }
        }

        public bool MoveToTrash(string sourcePath, string shareRoot = null)
        {
            lock (_lock)
            {
                try
                {
                    var source = ResolvePath(sourcePath, shareRoot);
                    if (IsWithin(source, _trashDir) || IsWithin(_trashDir, source)) return false;
                    var stats = Stat(source);
                    var id = Guid.NewGuid().ToString("N");
                    var entry = new TrashEntry
                    {
                        Id = id,
                        TrashName = id,
                        OriginalPath = source,
                        OriginalName = Path.GetFileName(source),
                        IsDirectory = OsConstants.S_ISDIR(stats.StMode),
                        Size = GetSize(source, stats),
                        DeletedAt = DateTime.Now
                    };
                    EnsureTrash();
                    var destination = Path.Combine(_trashDir, entry.TrashName);
                    if (Exists(destination)) return false;
                    var entries = ReadEntries();
                    entries.Add(entry);
                    // Publish the index first. A crash before transfer leaves only a hidden stale record.
                    SaveEntries(entries);
                    try { Transfer(source, destination, entry.IsDirectory, privateDestination: true); }
                    catch
                    {
                        // Keep the indexed, complete trash copy if source cleanup failed after commit.
                        if (!Exists(destination))
                        {
                            entries.Remove(entry);
                            SaveEntries(entries);
                        }
                        throw;
                    }
                    return true;
                }
                catch (Exception ex) { Log.Error("TrashManager", "MoveToTrash failed: " + ex.Message); return false; }
            }
        }

        public bool Restore(string trashId, string targetPath = null)
        {
            lock (_lock)
            {
                try
                {
                    EnsureTrash();
                    var entries = ReadEntries();
                    var entry = entries.FirstOrDefault(e => e.Id == trashId);
                    if (entry == null) return false;
                    var source = EntryPath(entry);
                    var directory = OsConstants.S_ISDIR(Stat(source).StMode);
                    var requested = Normalize(targetPath ?? entry.OriginalPath);
                    var parent = ResolvePath(Path.GetDirectoryName(requested), null);
                    requested = Path.Combine(parent, Path.GetFileName(requested));
                    if (IsWithin(requested, _trashDir) || IsWithin(_trashDir, requested)) return false;
                    var ext = directory ? "" : Path.GetExtension(requested);
                    var name = Path.GetFileName(requested);
                    name = name.Substring(0, name.Length - ext.Length);
                    var destination = requested;
                    for (var i = 1; Exists(destination); i++)
                        destination = Path.Combine(parent, $"{name} ({i}){ext}");
                    Transfer(source, destination, directory, privateDestination: false);
                    entries.Remove(entry);
                    SaveEntries(entries);
                    return true;
                }
                catch (Exception ex) { Log.Error("TrashManager", "Restore failed: " + ex.Message); return false; }
            }
        }

        // Android's ordinary rename overwrites. Prefer the kernel's atomic no-replace operation;
        // older kernels or filesystems can still restore using exclusively created destinations.
        [DllImport("libc", SetLastError = true)]
        private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);

        private static bool TryRename(string source, string destination, bool privateDestination)
        {
            const int atFdcwd = -100;
            try { return renameat2(atFdcwd, source, atFdcwd, destination, 1) == 0; }
            catch (EntryPointNotFoundException)
            {
                // The app-private destination is a new GUID owned by this locked manager.
                return privateDestination && !Exists(destination) && new Java.IO.File(source).RenameTo(new Java.IO.File(destination));
            }
        }

        private static void Transfer(string source, string destination, bool directory, bool privateDestination)
        {
            if (TryRename(source, destination, privateDestination)) return;
            if (Exists(destination)) throw new IOException("The destination already exists.");
            var stage = Path.Combine(Path.GetDirectoryName(destination), ".mchat-trash-" + Guid.NewGuid().ToString("N"));
            Os.Mkdir(stage, 448); // 0700
            try
            {
                var payload = Path.Combine(stage, "payload");
                CopyTree(source, payload);
                if (!TryRename(payload, destination, privateDestination))
                    CopyTree(payload, destination); // Exclusive creates throughout; never merge or overwrite.
                DeleteTree(source);
            }
            finally
            {
                try { DeleteTree(stage); }
                catch (Exception ex) { Log.Error("TrashManager", "Stage cleanup failed: " + ex.Message); }
            }
        }

        private static void CopyTree(string source, string destination)
        {
            var stats = Stat(source);
            if (OsConstants.S_ISDIR(stats.StMode))
            {
                Os.Mkdir(destination, 448); // Fails if any filesystem entry already occupies the path.
                try
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(source))
                        CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
                }
                catch
                {
                    TryRemoveIncomplete(destination);
                    throw;
                }
            }
            else
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    try
                    {
                        using (output)
                        {
                            input.CopyTo(output);
                            output.Flush(true);
                        }
                    }
                    catch
                    {
                        TryRemoveIncomplete(destination);
                        throw;
                    }
                }
            }
        }

        private static void TryRemoveIncomplete(string path)
        {
            try { DeleteTree(path); }
            catch (Exception ex) { Log.Error("TrashManager", "Could not remove incomplete copy: " + ex.Message); }
        }

        private static long GetSize(string path, StructStat stats)
        {
            if (!OsConstants.S_ISDIR(stats.StMode)) return stats.StSize;
            return Directory.EnumerateFileSystemEntries(path).Sum(child => GetSize(child, Stat(child)));
        }

        private static void DeleteTree(string path)
        {
            if (OsConstants.S_ISDIR(Stat(path).StMode))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteTree(child);
                Directory.Delete(path);
            }
            else if (!new Java.IO.File(path).Delete()) throw new IOException("Could not remove file: " + path);
        }

        private static StructStat Stat(string path)
        {
            var stats = Os.Lstat(path);
            if (!OsConstants.S_ISDIR(stats.StMode) && !OsConstants.S_ISREG(stats.StMode))
                throw new IOException("Only ordinary files and directories are supported by trash.");
            return stats;
        }

        private static bool Exists(string path)
        {
            try { Os.Lstat(path); return true; }
            catch (ErrnoException ex) when (ex.Errno == OsConstants.Enoent || ex.Errno == OsConstants.Enotdir) { return false; }
        }

        private static string Normalize(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            return full.Length == 0 ? "/" : full;
        }

        private static bool IsWithin(string path, string root) => path == root || path.StartsWith(root == "/" ? root : root + "/", StringComparison.Ordinal);

        private static string ResolvePath(string path, string shareRoot)
        {
            var full = Normalize(path);
            var canonical = Normalize(new Java.IO.File(full).CanonicalPath);
            if (shareRoot != null)
            {
                var root = Normalize(shareRoot);
                if (!IsShareDeleteTarget(root, full)) throw new IOException("Invalid shared path.");
                var canonicalRoot = Normalize(new Java.IO.File(root).CanonicalPath);
                var expected = Path.Combine(canonicalRoot, full.Substring(root.Length).TrimStart('/'));
                if (canonical != expected) throw new IOException("Cannot delete through a linked shared path.");
                for (var current = canonical; current != canonicalRoot; current = Path.GetDirectoryName(current))
                    Stat(current);
            }
            // /sdcard and similar system aliases may be ancestors; do not follow a linked leaf.
            Stat(full);
            return canonical;
        }

        private void EnsureTrash()
        {
            Directory.CreateDirectory(_trashDir);
            Stat(_trashDir);
        }

        private string EntryPath(TrashEntry entry)
        {
            if (string.IsNullOrEmpty(entry.TrashName) || entry.TrashName == "." || entry.TrashName == ".." ||
                entry.TrashName.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 ||
                entry.TrashName == "meta.json" || entry.TrashName.StartsWith(".mchat-", StringComparison.Ordinal))
                throw new IOException("Invalid trash entry name.");
            return Path.Combine(_trashDir, entry.TrashName);
        }

        public bool DeletePermanently(string trashId)
        {
            lock (_lock)
            {
                try
                {
                    EnsureTrash();
                    var entries = ReadEntries();
                    var entry = entries.FirstOrDefault(e => e.Id == trashId);
                    if (entry == null) return false;
                    DeleteTree(EntryPath(entry));
                    entries.Remove(entry);
                    SaveEntries(entries);
                    return true;
                }
                catch (Exception ex) { Log.Error("TrashManager", "DeletePermanently failed: " + ex.Message); return false; }
            }
        }

        public int Clear(bool onlyExpired = false)
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var entry in GetAllEntries())
                    if ((!onlyExpired || (DateTime.Now - entry.DeletedAt).TotalDays >= _retentionDays) && DeletePermanently(entry.Id)) count++;
                return count;
            }
        }

        public List<TrashEntry> GetAllEntries()
        {
            lock (_lock)
            {
                try { EnsureTrash(); return ReadEntries().Where(IsVisible).OrderByDescending(e => e.DeletedAt).ToList(); }
                catch (Exception ex) { Log.Error("TrashManager", "GetAllEntries failed: " + ex.Message); return new List<TrashEntry>(); }
            }
        }

        private bool IsVisible(TrashEntry entry)
        {
            try { return Exists(EntryPath(entry)); }
            catch { return false; }
        }

        public TrashEntry GetEntry(string id) => GetAllEntries().FirstOrDefault(e => e.Id == id);
        public long GetTotalSize() => GetAllEntries().Sum(e => e.Size);
        public int GetCount() => GetAllEntries().Count;

        private List<TrashEntry> ReadEntries()
        {
            if (!Exists(_metaFile)) return new List<TrashEntry>();
            Stat(_metaFile);
            var entries = JsonConvert.DeserializeObject<List<TrashEntry>>(File.ReadAllText(_metaFile));
            if (entries == null || entries.Any(e => e == null)) throw new IOException("Invalid trash index.");
            return entries;
        }

        private void SaveEntries(List<TrashEntry> entries)
        {
            var stage = Path.Combine(_trashDir, ".mchat-meta-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, true))
                        writer.Write(JsonConvert.SerializeObject(entries, Formatting.Indented));
                    stream.Flush(true);
                }
                Os.Rename(stage, _metaFile);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }

        public void AutoCleanup() => Task.Run(() => Clear(onlyExpired: true));
    }

    public class TrashEntry
    {
        public string Id { get; set; }
        public string OriginalPath { get; set; }
        public string TrashName { get; set; }
        public string OriginalName { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime DeletedAt { get; set; }
    }
}

// ============================================================
// 影音模块 · 图片库（第三类媒体，与音乐 / 视频并列）
// · 只扫描「参与检索 + 勾选图片」的目录，配置与音乐/视频共用 files/music/dirs.json
// · 图片清单缓存 files/music/photos.json；缩略图缓存 files/music/photo_thumbs/
// · 支持按文件夹分组、删除、重命名、详情（修改时间 / 所在目录 / 文件名）
// · 与 MusicLibrary.ScanAsync 在同一次后台扫描里完成，不额外起线程
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.Graphics;
using Newtonsoft.Json;
using IoPath = System.IO.Path;

namespace AhoChatA.Music
{
    /// <summary>一张图片。Modified 为 unix 秒，用于缓存失效与详情展示。</summary>
    public class Photo
    {
        public string Path = "";
        public string Name = "";
        public string Folder = "";
        public long Size;
        public long Modified;
        public int Width;
        public int Height;

        [JsonIgnore]
        public string ModifiedText
        {
            get
            {
                try
                {
                    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(Modified).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                }
                catch { return ""; }
            }
        }

        [JsonIgnore]
        public string SizeText => Size >= 1024 * 1024
            ? (Size / 1024.0 / 1024.0).ToString("0.0") + " MB"
            : Math.Max(1, Size / 1024) + " KB";

        [JsonIgnore]
        public string DimensionText => Width > 0 && Height > 0 ? Width + " × " + Height : "";
    }

    /// <summary>「按文件夹显示」时的一张文件夹卡片。</summary>
    public class PhotoFolder
    {
        public string Path = "";
        public string Name = "";
        public int Count;
        public string CoverPath = "";     // 该文件夹最新一张图片，用作封面
        public long Modified;
    }

    public static class PhotoLibrary
    {
        private static readonly object _io = new object();
        private static List<Photo> _items = new List<Photo>();

        private static readonly string[] PhotoExt =
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".heic", ".heif"
        };

        private static string Root() => IoPath.Combine(MusicLibrary.PlatformFilesDir(), "music");
        private static string LibFile => IoPath.Combine(Root(), "photos.json");
        private static string ThumbDir => IoPath.Combine(Root(), "photo_thumbs");

        public static int Count { get { lock (_io) return _items.Count; } }

        public static void Init()
        {
            lock (_io)
            {
                try
                {
                    if (File.Exists(LibFile))
                        _items = JsonConvert.DeserializeObject<List<Photo>>(File.ReadAllText(LibFile))
                                 ?? new List<Photo>();
                }
                catch { _items = new List<Photo>(); }
                try { Directory.CreateDirectory(ThumbDir); } catch { }
            }
        }

        /* ---------------- 查询 ---------------- */

        /// <summary>全部图片（按修改时间倒序）。</summary>
        public static List<Photo> All()
        {
            lock (_io) return _items.OrderByDescending(p => p.Modified).ToList();
        }

        public static List<Photo> InFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return All();
            lock (_io) return _items
                .Where(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Modified).ToList();
        }

        /// <summary>按所在目录分组，每组用最新一张图片当封面。</summary>
        public static List<PhotoFolder> Folders()
        {
            lock (_io) return _items
                .GroupBy(p => p.Folder ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var newest = g.OrderByDescending(p => p.Modified).First();
                    return new PhotoFolder
                    {
                        Path = g.Key,
                        Name = string.IsNullOrEmpty(g.Key) ? L10n.T("图片") : IoPath.GetFileName(g.Key.TrimEnd('/')),
                        Count = g.Count(),
                        CoverPath = newest.Path,
                        Modified = newest.Modified
                    };
                })
                .OrderByDescending(f => f.Modified).ToList();
        }

        /// <summary>按绝对路径找图片（网页端图片接口的安全白名单）。</summary>
        public static Photo Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_io) return _items.Find(p => string.Equals(p.Path, path, StringComparison.Ordinal));
        }

        /* ---------------- 扫描（后台线程调用） ---------------- */

        /// <summary>同步扫描，返回图片总数；由 MusicLibrary.ScanAsync 在后台线程里调用。</summary>
        public static int Scan(List<MusicDir> photoDirs, HashSet<string> excluded)
        {
            var old = new Dictionary<string, Photo>(StringComparer.OrdinalIgnoreCase);
            lock (_io) foreach (var p in _items) old[p.Path] = p;

            var found = new Dictionary<string, Photo>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in photoDirs ?? new List<MusicDir>())
            {
                if (dir == null || !dir.Enabled || !dir.ForPhoto || string.IsNullOrEmpty(dir.Path)) continue;
                if (!Directory.Exists(dir.Path)) continue;
                try
                {
                    foreach (var f in MusicLibrary.SafeEnum(dir.Path))
                    {
                        var ext = IoPath.GetExtension(f.Name).ToLowerInvariant();
                        if (Array.IndexOf(PhotoExt, ext) < 0) continue;
                        var full = f.FullName;
                        if (excluded != null && excluded.Contains(full)) continue;
                        if (SkipHidden(full)) continue;
                        if (found.ContainsKey(full)) continue;   // 目录重叠时去重

                        long mod = 0;
                        try { mod = (long)(f.LastWriteTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds; }
                        catch { }
                        Photo p;
                        if (old.TryGetValue(full, out var cached) && cached != null &&
                            cached.Size == f.Length && cached.Modified == mod)
                        {
                            p = cached;
                        }
                        else
                        {
                            p = new Photo
                            {
                                Path = full,
                                Name = f.Name,
                                Folder = IoPath.GetDirectoryName(full) ?? "",
                                Size = f.Length,
                                Modified = mod
                            };
                            ReadSize(p);
                        }
                        found[full] = p;
                    }
                }
                catch { }
            }

            var list = found.Values.OrderByDescending(p => p.Modified).ToList();
            lock (_io)
            {
                _items = list;
                Save();
            }
            return list.Count;
        }

        private static bool SkipHidden(string path)
        {
            var p = path.Replace('\\', '/');
            return p.IndexOf("/Android/data/", StringComparison.OrdinalIgnoreCase) >= 0
                   || p.IndexOf("/Android/obb/", StringComparison.OrdinalIgnoreCase) >= 0
                   || p.IndexOf("/.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ReadSize(Photo p)
        {
            try
            {
                var o = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(p.Path, o);
                p.Width = o.OutWidth;
                p.Height = o.OutHeight;
            }
            catch { }
        }

        private static void Save()
        {
            try { File.WriteAllText(LibFile, JsonConvert.SerializeObject(_items)); }
            catch { }
        }

        /* ---------------- 文件操作 ---------------- */

        /// <summary>从列表移出（磁盘文件保留），与音乐/视频的 excluded.json 一致。</summary>
        public static void Drop(string path)
        {
            lock (_io)
            {
                _items.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                Save();
            }
        }

        /// <summary>真删除磁盘文件（调用方必须先弹二次确认）。</summary>
        public static bool Delete(string path, out string error)
        {
            error = null;
            var p = Find(path);
            if (p == null) { error = "图片不在图库里"; return false; }
            try
            {
                // 移入回收站而非永久删除
                if (File.Exists(path)) TrashManager.Instance.MoveToTrash(path);
                lock (_io)
                {
                    _items.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                    Save();
                }
                ClearThumbs(path);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>重命名：保留原扩展名，返回新路径。</summary>
        public static string Rename(string path, string newName, out string error)
        {
            error = null;
            var p = Find(path);
            if (p == null) { error = "图片不在图库里"; return null; }
            newName = (newName ?? "").Trim();
            if (newName.Length == 0) { error = "文件名不能为空"; return null; }
            if (newName.IndexOf('/') >= 0 || newName.IndexOf('\\') >= 0)
            { error = L10n.T("文件名不能包含路径分隔符"); return null; }
            foreach (var c in IoPath.GetInvalidFileNameChars())
                if (newName.IndexOf(c) >= 0) { error = L10n.T("文件名包含非法字符"); return null; }

            string ext = IoPath.GetExtension(path);
            string supplied = IoPath.GetExtension(newName);
            // 没写扩展名 / 写的是非图片扩展名时，一律沿用原扩展名
            if (string.IsNullOrEmpty(supplied) || Array.IndexOf(PhotoExt, supplied.ToLowerInvariant()) < 0)
                newName = IoPath.GetFileNameWithoutExtension(newName) + ext;
            else if (!string.Equals(supplied, ext, StringComparison.OrdinalIgnoreCase))
                newName = IoPath.GetFileNameWithoutExtension(newName) + ext;

            string dir = IoPath.GetDirectoryName(path);
            string dest = IoPath.Combine(dir ?? "", newName);
            if (string.Equals(dest, path, StringComparison.OrdinalIgnoreCase))
            { error = L10n.T("名称没有变化"); return null; }
            if (File.Exists(dest)) { error = L10n.T("同名文件已存在"); return null; }

            try
            {
                File.Move(path, dest);
                ClearThumbs(path);
                var fi = new FileInfo(dest);
                long mod = 0;
                try { mod = (long)(fi.LastWriteTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds; }
                catch { }
                lock (_io)
                {
                    var item = _items.Find(x => string.Equals(x.Path, path, StringComparison.Ordinal));
                    if (item != null)
                    {
                        item.Path = dest;
                        item.Name = newName;
                        item.Folder = dir ?? "";
                        item.Modified = mod;
                    }
                    Save();
                }
                return dest;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private static void ClearThumbs(string path)
        {
            try
            {
                var prefix = MusicLibrary.Hash(path) + "_";
                foreach (var f in Directory.GetFiles(ThumbDir, prefix + "*"))
                { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        /* ---------------- 缩略图 / 原图 ---------------- */

        private static string ThumbFile(Photo p, int maxPx)
        {
            int bucket = maxPx <= 240 ? 240 : maxPx <= 480 ? 480 : 960;
            return IoPath.Combine(ThumbDir, MusicLibrary.Hash(p.Path) + "_" + p.Modified + "_" + bucket + ".jpg");
        }

        /// <summary>生成（或复用）缩略图缓存，返回 jpg 路径；解码失败返回 null。</summary>
        public static string ThumbPath(Photo p, int maxPx)
        {
            if (p == null || string.IsNullOrEmpty(p.Path) || !File.Exists(p.Path)) return null;
            try
            {
                Directory.CreateDirectory(ThumbDir);
                var cache = ThumbFile(p, maxPx);
                if (File.Exists(cache) && new FileInfo(cache).Length > 0) return cache;
                int bucket = maxPx <= 240 ? 240 : maxPx <= 480 ? 480 : 960;

                var o = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(p.Path, o);
                int sample = 1;
                while (o.OutWidth / (sample * 2) >= bucket && o.OutHeight / (sample * 2) >= bucket) sample *= 2;
                var opt = new BitmapFactory.Options
                {
                    InSampleSize = sample,
                    InPreferredConfig = Bitmap.Config.Argb8888   // 保留 PNG 透明通道
                };
                using (var bmp = BitmapFactory.DecodeFile(p.Path, opt))
                {
                    if (bmp == null) return null;
                    float scale = Math.Min(1f, (float)bucket / Math.Max(bmp.Width, bmp.Height));
                    using (var small = (scale < 1f)
                        ? Bitmap.CreateScaledBitmap(bmp,
                            Math.Max(1, (int)(bmp.Width * scale)), Math.Max(1, (int)(bmp.Height * scale)), true)
                        : bmp)
                    {
                        using (var fs = File.Create(cache))
                            small.Compress(Bitmap.CompressFormat.Jpeg, 80, fs);
                    }
                }
                return File.Exists(cache) ? cache : null;
            }
            catch { return null; }
        }

        /// <summary>App 网格用的缩略图位图（调用方负责 Recycle）。</summary>
        public static Bitmap LoadThumb(Photo p, int maxPx)
        {
            var file = ThumbPath(p, maxPx);
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var o = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(file, o);
                int sample = 1;
                while (o.OutWidth / sample > maxPx * 2 || o.OutHeight / sample > maxPx * 2) sample *= 2;
                return BitmapFactory.DecodeFile(file, new BitmapFactory.Options { InSampleSize = sample });
            }
            catch { return null; }
        }

        /// <summary>预览原图（按 ImageView 宽度采样，避免超大图 OOM）。</summary>
        public static Bitmap LoadFull(Photo p, int maxPx)
        {
            if (p == null || !File.Exists(p.Path)) return null;
            try
            {
                var o = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(p.Path, o);
                int sample = 1;
                while (o.OutWidth / sample > maxPx || o.OutHeight / sample > maxPx) sample *= 2;
                return BitmapFactory.DecodeFile(p.Path, new BitmapFactory.Options { InSampleSize = sample });
            }
            catch { return null; }
        }

        public static string Mime(string path)
        {
            switch (IoPath.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".heic": case ".heif": return "image/heic";
                default: return "image/jpeg";
            }
        }
    }
}

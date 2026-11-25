// ============================================================
// 音乐模块 · 曲库（目录配置 + 扫描 + 缓存 + 歌单）
// · 目录配置存 files/music/dirs.json：内置默认目录 + 用户自定义目录，
//   每项可单独「参与检索」并分别指定是否参与音乐 / 视频 / 图片检索。
// · 图片清单由 PhotoLibrary 在同一次扫描里产出（files/music/photos.json）。
// · 扫描结果存 files/music/library.json，按 size+mtime 复用旧条目
// · 内嵌封面导出到 files/music/covers/<hash>.jpg
// · 内嵌歌词导出到 files/music/lyrics/<hash>.lrc
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using IoPath = System.IO.Path;

namespace AhoChatA.Music
{
    /// <summary>一个检索目录。Enabled=false 时完全不参与扫描，避免无关文件进入列表。</summary>
    public class MusicDir
    {
        public string Path = "";
        public string Label = "";
        public bool BuiltIn;             // 内置默认目录（不可删除，只能停用）
        public bool Enabled = true;      // 是否参与检索
        public bool ForAudio = true;     // 参与音乐检索
        public bool ForVideo = true;     // 参与视频检索
        public bool ForPhoto = true;     // 参与图片检索
    }

    public class MusicConfig
    {
        public List<MusicDir> Dirs = new List<MusicDir>();
        public bool FilterShortAudio = true;     // 过滤 <10s / <100KB 的短音频（铃声、提示音）
        public bool SkipSystemSounds = true;     // 跳过 Ringtones / Notifications / Alarms / 隐藏目录
        public int MinAudioSeconds = 10;
    }

    public static class MusicLibrary
    {
        private static readonly object _io = new object();
        private static string _root;
        private static MusicConfig _cfg = new MusicConfig();
        // 「从列表移除」的文件：只从曲库列表屏蔽，绝不删除磁盘文件
        private static HashSet<string> _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static List<MusicDir> Dirs => _cfg.Dirs;
        public static List<Song> Songs = new List<Song>();
        public static DateTime LastScan;

        public static bool FilterShortAudio { get => _cfg.FilterShortAudio; set => _cfg.FilterShortAudio = value; }
        public static bool SkipSystemSounds { get => _cfg.SkipSystemSounds; set => _cfg.SkipSystemSounds = value; }
        public static int MinAudioSeconds { get => _cfg.MinAudioSeconds; set => _cfg.MinAudioSeconds = value; }
        public static int EnabledDirCount => _cfg.Dirs.Count(d => d.Enabled);
        public static int EnabledAudioDirCount => _cfg.Dirs.Count(d => d.Enabled && d.ForAudio);
        public static int EnabledVideoDirCount => _cfg.Dirs.Count(d => d.Enabled && d.ForVideo);
        public static int EnabledPhotoDirCount => _cfg.Dirs.Count(d => d.Enabled && d.ForPhoto);

        /// <summary>目录配置变更（或首次启动）后需要强制重扫三类媒体。</summary>
        public static bool Dirty = true;

        private static readonly string[] AudioExt = { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".ape", ".wma" };
        private static readonly string[] VideoExt = { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".3gp" };
        private static readonly string[] ImgExt = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        /// <summary>内置默认目录：Music 默认启用，其余默认停用（按需勾选），避免把下载/相机里的无关文件扫进来。</summary>
        private static readonly MusicDir[] Presets =
        {
            new MusicDir { Path = "/storage/emulated/0/Music",        Label = L10n.T("Music（音乐）"),     BuiltIn = true, Enabled = true,  ForAudio = true,  ForVideo = true,  ForPhoto = false },
            new MusicDir { Path = "/storage/emulated/0/Download",     Label = L10n.T("Download（下载）"), BuiltIn = true, Enabled = false, ForAudio = true,  ForVideo = true,  ForPhoto = true  },
            new MusicDir { Path = "/storage/emulated/0/Movies",       Label = L10n.T("Movies（电影）"),   BuiltIn = true, Enabled = true,  ForAudio = false, ForVideo = true,  ForPhoto = false },
            new MusicDir { Path = "/storage/emulated/0/Pictures",     Label = L10n.T("Pictures（图片）"), BuiltIn = true, Enabled = true,  ForAudio = false, ForVideo = false, ForPhoto = true  },
            new MusicDir { Path = "/storage/emulated/0/DCIM/Camera",  Label = L10n.T("Camera（相机）"),   BuiltIn = true, Enabled = false, ForAudio = false, ForVideo = true,  ForPhoto = true  },
            // 频道分享接收目录（/sdcard/file/<类型>）：收到的分享文件自动进对应媒体列表
            new MusicDir { Path = L10n.T("/storage/emulated/0/file/音乐"),    Label = L10n.T("file/音乐（分享）"), BuiltIn = true, Enabled = true,  ForAudio = true,  ForVideo = false, ForPhoto = false },
            new MusicDir { Path = L10n.T("/storage/emulated/0/file/视频"),    Label = L10n.T("file/视频（分享）"), BuiltIn = true, Enabled = true,  ForAudio = false, ForVideo = true,  ForPhoto = false },
            new MusicDir { Path = L10n.T("/storage/emulated/0/file/图片"),    Label = L10n.T("file/图片（分享）"), BuiltIn = true, Enabled = true,  ForAudio = false, ForVideo = false, ForPhoto = true  },
        };

        private static MusicDir Clone(MusicDir d) => new MusicDir
        {
            Path = d.Path, Label = d.Label, BuiltIn = d.BuiltIn,
            Enabled = d.Enabled, ForAudio = d.ForAudio, ForVideo = d.ForVideo, ForPhoto = d.ForPhoto
        };

        private static string Root()
        {
            if (_root != null) return _root;
            _root = IoPath.Combine(PlatformFilesDir(), "music");
            try { Directory.CreateDirectory(_root); } catch { }
            try { Directory.CreateDirectory(IoPath.Combine(_root, "covers")); } catch { }
            try { Directory.CreateDirectory(IoPath.Combine(_root, "lyrics")); } catch { }
            try { Directory.CreateDirectory(IoPath.Combine(_root, "thumbs")); } catch { }
            return _root;
        }

        internal static string PlatformFilesDir()
        {
            // ReaderLibrary 同款：App 私有 files 目录
            return Application.Context.FilesDir.AbsolutePath;
        }

        private static string DirsFile => IoPath.Combine(Root(), "dirs.json");
        private static string LibFile => IoPath.Combine(Root(), "library.json");
        private static string ExcludedFile => IoPath.Combine(Root(), "excluded.json");

        /* ---------------- 配置 ---------------- */

        public static void Init(Context ctx)
        {
            lock (_io)
            {
                LoadDirs();
                LoadExcluded();
                try { PhotoLibrary.Init(); } catch { }
                try
                {
                    if (File.Exists(LibFile))
                    {
                        var cached = JsonConvert.DeserializeObject<Cache>(File.ReadAllText(LibFile));
                        if (cached != null)
                        {
                            Songs = cached.Songs ?? new List<Song>();
                            LastScan = cached.ScanTime;
                        }
                    }
                }
                catch { Songs = new List<Song>(); }
                Songs.RemoveAll(s => _excluded.Contains(s.Path));
                SortSongs();   // 缓存里的旧顺序（按歌手）也统一刷新为 A-Z 拼音序
            }
        }

        /// <summary>曲库统一按标题拼音 A-Z 排序：音乐列表与本地视频列表共用，右侧字母索引据此定位。</summary>
        public static void SortSongs()
        {
            if (Songs == null || Songs.Count < 2) return;
            Songs.Sort((a, b) =>
            {
                int r = Pinyin.Compare(a.DisplayTitle, b.DisplayTitle);
                if (r != 0) return r;
                return string.Compare(a.Path ?? "", b.Path ?? "", StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// 只加载一次曲库（读 library.json）。聊天页 / 阅读页要在「没打开过影音」时也能
        /// 随机挑一首待播曲目，需要一个不依赖 MusicActivity 的懒加载入口。
        /// 线程安全、可重复调用。
        /// </summary>
        public static void EnsureLoaded(Context ctx)
        {
            if (Volatile.Read(ref _loadedOnce) == 1) return;
            if (Interlocked.CompareExchange(ref _loadedOnce, 1, 0) != 0) return;
            try { Init(ctx); } catch { }
        }

        private static int _loadedOnce;

        /// <summary>当前是否有读取媒体文件的权限（用于「没进过影音也能挑待播曲目」的自动扫描）。</summary>
        public static bool CanReadStorage()
        {
            try
            {
                var ctx = Application.Context;
                if (ctx == null) return false;
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
                {
                    try { if (Android.OS.Environment.IsExternalStorageManager) return true; } catch { }
                }
                string perm = Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu
                    ? "android.permission.READ_MEDIA_AUDIO"
                    : "android.permission.READ_EXTERNAL_STORAGE";
                return ctx.CheckSelfPermission(perm) == Android.Content.PM.Permission.Granted;
            }
            catch { return false; }
        }

        /// <summary>「移出列表」对音乐 / 视频 / 图片同时生效（磁盘文件始终保留）。</summary>
        private static void DropFromLists(string path)
        {
            try { PhotoLibrary.Drop(path); } catch { }
        }

        /* ---------------- 移出列表（屏蔽文件，不删除） ---------------- */

        public static List<string> Excluded
        {
            get { lock (_io) return _excluded.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        }

        public static int ExcludedCount { get { lock (_io) return _excluded.Count; } }

        private static void LoadExcluded()
        {
            try
            {
                if (File.Exists(ExcludedFile))
                    _excluded = new HashSet<string>(
                        JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(ExcludedFile)) ?? new List<string>(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        private static void SaveExcluded()
        {
            lock (_io)
            {
                try { File.WriteAllText(ExcludedFile, JsonConvert.SerializeObject(_excluded.ToList(), Formatting.Indented)); }
                catch { }
            }
        }

        /// <summary>从曲库列表移出（加入屏蔽名单，文件仍留在磁盘）。</summary>
        public static void Exclude(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_io)
            {
                _excluded.Add(path);
                Songs.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                SaveLib();
            }
            DropFromLists(path);
            SaveExcluded();
        }

        public static void Restore(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_io) _excluded.Remove(path);
            SaveExcluded();
        }

        public static void RestoreAll()
        {
            lock (_io) _excluded.Clear();
            SaveExcluded();
        }

        private static void LoadDirs()
        {
            bool fresh = !File.Exists(DirsFile);
            if (fresh)
            {
                _cfg = new MusicConfig { Dirs = Presets.Select(Clone).ToList() };
                SaveDirs();
                return;
            }
            try
            {
                var txt = File.ReadAllText(DirsFile);
                var tok = JToken.Parse(txt);
                // 老版本 dirs.json 没有 ForPhoto 字段：内置目录沿用预设值，
                // 用户自定义目录默认参与图片检索，避免升级后图片页空白。
                var dirArr = tok as JArray ?? tok["Dirs"] as JArray;
                if (dirArr != null)
                    foreach (var o in dirArr.OfType<JObject>())
                    {
                        if (o.Properties().Any(pr => string.Equals(pr.Name, "ForPhoto", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var oldPath = (string)o["Path"] ?? (string)o["path"] ?? "";
                        var pre = Presets.FirstOrDefault(x => string.Equals(x.Path, oldPath, StringComparison.OrdinalIgnoreCase));
                        o["ForPhoto"] = pre != null ? pre.ForPhoto : true;
                    }
                var normalized = tok.ToString();
                if (tok is JArray arr)
                {
                    _cfg = new MusicConfig();
                    if (arr.Count > 0 && arr[0].Type == JTokenType.String)
                    {
                        // 老版本 dirs.json：字符串数组
                        foreach (var t in arr)
                        {
                            var p = (string)t;
                            if (string.IsNullOrWhiteSpace(p)) continue;
                            var preset = Presets.FirstOrDefault(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase));
                            // 旧版目录列表升级到带开关的配置：内置目录一律回到预设默认值，
                            // 避免把 Download / Camera 里的无关文件继续带进列表；用户自定义目录保持启用。
                            _cfg.Dirs.Add(preset != null
                                ? Clone(preset)
                                : new MusicDir { Path = p, Label = IoPath.GetFileName(p), Enabled = true });
                        }
                    }
                    else
                        _cfg.Dirs = JsonConvert.DeserializeObject<List<MusicDir>>(normalized) ?? new List<MusicDir>();
                }
                else
                    _cfg = JsonConvert.DeserializeObject<MusicConfig>(normalized) ?? new MusicConfig();
            }
            catch { _cfg = new MusicConfig(); }

            if (_cfg.Dirs == null) _cfg.Dirs = new List<MusicDir>();
            // 补齐后来新增的内置默认目录（默认停用，不打扰已有配置）
            EnsurePresets();
            if (_cfg.Dirs.Count == 0)
            {
                _cfg.Dirs = Presets.Select(Clone).ToList();
                SaveDirs();
            }
        }

        private static void EnsurePresets()
        {
            foreach (var p in Presets)
            {
                if (_cfg.Dirs.Any(d => string.Equals(d.Path, p.Path, StringComparison.OrdinalIgnoreCase))) continue;
                // 后补的内置目录沿用预设默认状态（Movies 仅参与视频检索）
                _cfg.Dirs.Add(Clone(p));
            }
        }

        public static void SaveDirs()
        {
            lock (_io)
            {
                try { File.WriteAllText(DirsFile, JsonConvert.SerializeObject(_cfg, Formatting.Indented)); }
                catch { }
            }
        }

        public static void ResetDefaults()
        {
            _cfg = new MusicConfig { Dirs = Presets.Select(Clone).ToList() };
            Dirty = true;
            SaveDirs();
        }

        private class Cache
        {
            public List<Song> Songs;
            public DateTime ScanTime;
        }

        private static void SaveLib()
        {
            try { File.WriteAllText(LibFile, JsonConvert.SerializeObject(new Cache { Songs = Songs, ScanTime = LastScan })); }
            catch { }
        }

        public static void AddDir(string dir)
        {
            AddDir(dir, true, true, true);
        }

        /// <summary>
        /// 添加扫描目录并在添加时就确定扫描类型（音乐 / 视频 / 图片）。
        /// 三个开关至少有一个为真，否则该目录扫不出任何东西，直接忽略。
        /// </summary>
        public static void AddDir(string dir, bool forAudio, bool forVideo, bool forPhoto)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            dir = dir.Trim().TrimEnd('/');
            if (dirsContain(dir)) return;
            if (!forAudio && !forVideo && !forPhoto) forAudio = true;   // 防止全关成L10n.T("空目录")
            _cfg.Dirs.Add(new MusicDir
            {
                Path = dir, Label = IoPath.GetFileName(dir), BuiltIn = false,
                Enabled = true, ForAudio = forAudio, ForVideo = forVideo, ForPhoto = forPhoto
            });
            Dirty = true;
            SaveDirs();
        }

        private static bool dirsContain(string dir) =>
            _cfg.Dirs.Any(d => string.Equals(d.Path, dir, StringComparison.OrdinalIgnoreCase));

        public static void RemoveDir(string dir)
        {
            var d = _cfg.Dirs.FirstOrDefault(x => string.Equals(x.Path, dir, StringComparison.OrdinalIgnoreCase));
            if (d == null || d.BuiltIn) return;   // 内置目录只能停用
            _cfg.Dirs.Remove(d);
            Dirty = true;
            SaveDirs();
        }

        public static void SetDirEnabled(string dir, bool on)
        {
            var d = _cfg.Dirs.FirstOrDefault(x => string.Equals(x.Path, dir, StringComparison.OrdinalIgnoreCase));
            if (d == null) return;
            d.Enabled = on;
            Dirty = true;
            SaveDirs();
        }

        public static void SetDirKind(string dir, bool audio, bool video)
        {
            var d = _cfg.Dirs.FirstOrDefault(x => string.Equals(x.Path, dir, StringComparison.OrdinalIgnoreCase));
            SetDirKind(dir, audio, video, d?.ForPhoto ?? true);
        }

        /// <summary>音乐 / 视频 / 图片三类媒体分别指定是否参与该目录的检索。</summary>
        public static void SetDirKind(string dir, bool audio, bool video, bool photo)
        {
            var d = _cfg.Dirs.FirstOrDefault(x => string.Equals(x.Path, dir, StringComparison.OrdinalIgnoreCase));
            if (d == null) return;
            d.ForAudio = audio;
            d.ForVideo = video;
            d.ForPhoto = photo;
            Dirty = true;
            SaveDirs();
        }

        /* ---------------- 扫描 ---------------- */

        public sealed class ScanResult
        {
            public int Added, Updated, Total, Videos, Photos, Skipped;
            public List<string> Errors = new List<string>();
        }

        /// <summary>全量扫描（后台线程跑）。只走「参与检索」的目录；增量复用未变文件。</summary>
        public static Task<ScanResult> ScanAsync(IProgress<string> progress = null)
        {
            return Task.Run(() =>
            {
                var res = new ScanResult();
                var cfg = _cfg;
                var byPath = Songs.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
                var found = new List<Song>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var dir in cfg.Dirs.Where(d => d.Enabled).ToList())
                {
                    if (!Directory.Exists(dir.Path))
                    {
                        res.Errors.Add(L10n.T("目录不存在：") + dir.Path);
                        continue;
                    }
                    try
                    {
                        foreach (var f in SafeEnum(dir.Path))
                        {
                            var ext = IoPath.GetExtension(f.Name).ToLowerInvariant();
                            bool isAudio = Array.IndexOf(AudioExt, ext) >= 0;
                            bool isVideo = Array.IndexOf(VideoExt, ext) >= 0;
                            if (!isAudio && !isVideo) continue;
                            if (isAudio && !dir.ForAudio) continue;
                            if (isVideo && !dir.ForVideo) continue;
                            if (isAudio && SkipByPath(f.FullName)) { res.Skipped++; continue; }
                            if (_excluded.Contains(f.FullName)) { res.Skipped++; continue; }
                            if (!seenPaths.Add(f.FullName)) continue;   // 多个目录重叠时去重

                            Song old = null;
                            long mod = 0;
                            try { mod = (long)(f.LastWriteTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds; } catch { }
                            byPath.TryGetValue(f.FullName, out old);
                            // 旧版本库里「没找过封面」的条目重扫一次补封面
                            bool stale = !isVideo && old != null && !old.CoverChecked && string.IsNullOrEmpty(old.CoverPath);
                            if (old != null && old.Size == f.Length && old.Modified == mod && !stale)
                            {
                                if (isAudio && !AcceptAudio(old, f.Length)) { res.Skipped++; continue; }
                                found.Add(old);
                                continue;
                            }

                            var song = ReadSong(f.FullName, f.Length, mod, isVideo);
                            if (isAudio && !AcceptAudio(song, f.Length)) { res.Skipped++; continue; }
                            found.Add(song);
                            res.Updated++;
                            progress?.Report(L10n.T("已读取 ") + song.FileName);
                        }
                    }
                    catch (Exception ex) { res.Errors.Add(dir.Path + "：" + ex.Message); }
                }

                Songs = found;
                SortSongs();   // A-Z（拼音首字母）排序，供 App 与网页端共用
                res.Total = Songs.Count;
                res.Videos = Songs.Count(s => s.IsVideo);
                // 图片与音乐/视频共用同一次目录遍历，结果单独存 photos.json
                try { res.Photos = PhotoLibrary.Scan(cfg.Dirs.Where(d => d.Enabled).ToList(), _excluded); }
                catch { res.Photos = PhotoLibrary.Count; }
                LastScan = DateTime.Now;
                Dirty = false;
                lock (_io) SaveLib();
                return res;
            });
        }

        /// <summary>系统音/通知音/隐藏目录，不进曲库。</summary>
        private static bool SkipByPath(string path)
        {
            if (!_cfg.SkipSystemSounds) return false;
            var p = path.Replace('\\', '/');
            foreach (var seg in new[] { "/Ringtones/", "/Notifications/", "/Alarms/",
                                        "/Android/data/", "/Android/obb/", "/." })
                if (p.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>过滤过短 / 过小的音频（一般是提示音、录音碎片，不是歌）。</summary>
        private static bool AcceptAudio(Song s, long size)
        {
            if (!_cfg.FilterShortAudio) return true;
            if (size < 100 * 1024) return false;
            if (s.DurationMs > 0 && s.DurationMs < _cfg.MinAudioSeconds * 1000) return false;
            return true;
        }

        internal static IEnumerable<FileInfo> SafeEnum(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                DirectoryInfo di = null;
                try { di = new DirectoryInfo(cur); } catch { }
                if (di == null || !di.Exists) continue;
                try { foreach (var d in di.EnumerateDirectories()) stack.Push(d.FullName); } catch { }
                FileInfo[] files = null;
                try { files = di.EnumerateFiles().ToArray(); } catch { }
                if (files != null) foreach (var f in files) yield return f;
            }
        }

        private static Song ReadSong(string path, long size, long modified, bool isVideo)
        {
            var s = new Song
            {
                Path = path,
                Size = size,
                Modified = modified,
                IsVideo = isVideo,
                Title = IoPath.GetFileNameWithoutExtension(path),
            };

            if (!isVideo)
            {
                using (var mr = new MediaMetadataRetriever())
                {
                    try { mr.SetDataSource(path); } catch { }
                    try { s.Title = Val(mr, MetadataKey.Title) ?? s.Title; } catch { }
                    try { s.Artist = Val(mr, MetadataKey.Artist); } catch { }
                    try { s.Album = Val(mr, MetadataKey.Album); } catch { }
                    try
                    {
                        var dur = Val(mr, MetadataKey.Duration);
                        if (dur != null && long.TryParse(dur, out var ms)) s.DurationMs = (int)ms;
                    }
                    catch { }
                    // ① 内嵌封面 → 缓存文件
                    try
                    {
                        var pic = mr.GetEmbeddedPicture();
                        if (pic != null && pic.Length > 0)
                            s.CoverPath = SaveBytes("covers", path, pic, ".jpg");
                    }
                    catch { }
                }
                // ② 歌词：同名 .lrc 优先，其次内嵌
                s.LrcPath = FindSameName(path, ".lrc");
                if (s.LrcPath == null)
                {
                    var emb = EmbedLyrics.Extract(path);
                    if (!string.IsNullOrWhiteSpace(emb))
                        s.LrcPath = SaveText("lyrics", path, emb, ".lrc");
                }
                // ③ 没有内嵌封面时找同名图片（Song.CoverPath 初始为 ""，不能只判 null）
                if (string.IsNullOrEmpty(s.CoverPath))
                    s.CoverPath = FindCover(path);
                s.CoverChecked = true;
            }
            else
            {
                using (var mr = new MediaMetadataRetriever())
                {
                    try
                    {
                        mr.SetDataSource(path);
                        s.Title = Val(mr, MetadataKey.Title) ?? s.Title;
                        var dur = Val(mr, MetadataKey.Duration);
                        if (dur != null && long.TryParse(dur, out var ms)) s.DurationMs = (int)ms;
                    }
                    catch { }
                }
            }
            return s;
        }

        private static string Val(MediaMetadataRetriever mr, MetadataKey key)
        {
            try
            {
                var v = mr.ExtractMetadata(key);
                if (string.IsNullOrWhiteSpace(v) || v == "<unknown>") return null;
                return v.Trim();
            }
            catch { return null; }
        }

        private static string FindSameName(string songPath, string extWithDot)
        {
            try
            {
                var p = IoPath.Combine(IoPath.GetDirectoryName(songPath),
                    IoPath.GetFileNameWithoutExtension(songPath) + extWithDot);
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        private static string FindCover(string songPath)
        {
            try
            {
                var dir = IoPath.GetDirectoryName(songPath);
                var stem = IoPath.GetFileNameWithoutExtension(songPath);
                foreach (var ext in ImgExt)
                {
                    var p = IoPath.Combine(dir, stem + ext);
                    if (File.Exists(p)) return p;
                }
                // 目录级 cover.jpg / cover.png / folder.jpg
                foreach (var name in new[] { "cover", "folder", "front" })
                    foreach (var ext in ImgExt)
                    {
                        var p = IoPath.Combine(dir, name + ext);
                        if (File.Exists(p)) return p;
                    }
            }
            catch { }
            return null;
        }

        private static string SaveBytes(string sub, string srcPath, byte[] data, string ext)
        {
            try
            {
                var name = Hash(srcPath) + ext;
                var full = IoPath.Combine(Root(), sub, name);
                if (!File.Exists(full) || new FileInfo(full).Length != data.Length)
                    File.WriteAllBytes(full, data);
                return full;
            }
            catch { return null; }
        }

        private static string SaveText(string sub, string srcPath, string text, string ext)
        {
            try
            {
                var name = Hash(srcPath) + ext;
                var full = IoPath.Combine(Root(), sub, name);
                File.WriteAllText(full, text);
                return full;
            }
            catch { return null; }
        }

        internal static string Hash(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var b = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
                var sb = new System.Text.StringBuilder(32);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /* ---------------- 查询 ---------------- */

        public static List<Song> Audio() { lock (_io) return Songs.FindAll(s => !s.IsVideo); }
        public static List<Song> Videos() { lock (_io) return Songs.FindAll(s => s.IsVideo); }

        /// <summary>按绝对路径找曲库条目（网页端流式播放前的安全校验也用它）。</summary>
        public static Song Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_io) return Songs.Find(s => string.Equals(s.Path, path, StringComparison.Ordinal));
        }

        /// <summary>视频缩略图：取 1 秒处的一帧，按 size+mtime 缓存到 files/music/thumbs。
        /// 用于 App 卡片网格与网页端海报墙；取不到帧返回 null。</summary>
        public static Android.Graphics.Bitmap VideoThumb(Song s, int maxPx)
        {
            if (s == null || !s.IsVideo || string.IsNullOrEmpty(s.Path)) return null;
            try
            {
                var cache = IoPath.Combine(Root(), "thumbs", Hash(s.Path) + "_" + s.Modified + ".jpg");
                if (!File.Exists(cache))
                {
                    using (var mr = new MediaMetadataRetriever())
                    {
                        mr.SetDataSource(s.Path);
                        // 优先 1 秒（避开片头黑帧），失败退回第 0 帧
                        Android.Graphics.Bitmap frame = null;
                        try { frame = mr.GetFrameAtTime(1000000); }
                        catch { }
                        if (frame == null) { try { frame = mr.GetFrameAtTime(0); } catch { } }
                        if (frame == null) return null;
                        int w = frame.Width, h = frame.Height;
                        float scale = Math.Min(1f, (float)maxPx / Math.Max(w, h));
                        var small = Android.Graphics.Bitmap.CreateScaledBitmap(
                            frame, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), true);
                        using (var fs = File.Create(cache))
                            small.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg, 78, fs);
                        small.Recycle();
                        frame.Recycle();
                    }
                }
                var opt = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                Android.Graphics.BitmapFactory.DecodeFile(cache, opt);
                int sample = 1;
                while (opt.OutWidth / sample > maxPx * 2 || opt.OutHeight / sample > maxPx * 2) sample *= 2;
                return Android.Graphics.BitmapFactory.DecodeFile(cache,
                    new Android.Graphics.BitmapFactory.Options { InSampleSize = sample });
            }
            catch { return null; }
        }

        /// <summary>视频缩略图缓存文件路径（网页端 /api/media/thumb 用）。</summary>
        public static string VideoThumbPath(Song s)
        {
            if (s == null || !s.IsVideo) return null;
            var p = IoPath.Combine(Root(), "thumbs", Hash(s.Path) + "_" + s.Modified + ".jpg");
            return File.Exists(p) ? p : null;
        }

        public static Lyrics LoadLyrics(Song s)
        {
            if (s == null) return new Lyrics();
            if (!string.IsNullOrEmpty(s.LrcPath))
            {
                try
                {
                    if (File.Exists(s.LrcPath))
                        return Lyrics.Parse(File.ReadAllText(s.LrcPath, System.Text.Encoding.UTF8));
                }
                catch { }
            }
            // 一起听 / 同步听：对端共享的歌词直接随列表带过来，没有本地 .lrc 文件
            if (!string.IsNullOrEmpty(s.LrcText))
            {
                try { return Lyrics.Parse(s.LrcText); } catch { }
            }
            return new Lyrics();
        }

        /// <summary>封面位图（活动用后须 Recycle）；取不到返回 null。</summary>
        // 封面内存缓存：列表滚动、底栏、阅读迷你条会反复要同一张图，
        // 以前每次都从磁盘解码两次（先量尺寸再解码），列表长了明显卡顿。
        private static readonly Dictionary<string, Android.Graphics.Bitmap> _coverMem
            = new Dictionary<string, Android.Graphics.Bitmap>();
        private static readonly object _coverMemLock = new object();

        public static Android.Graphics.Bitmap LoadCover(Song s, int maxPx)
        {
            if (s == null || string.IsNullOrEmpty(s.CoverPath)) return null;
            string key = s.CoverPath + "|" + maxPx;
            lock (_coverMemLock)
            {
                Android.Graphics.Bitmap hit;
                if (_coverMem.TryGetValue(key, out hit) && hit != null && !hit.IsRecycled) return hit;
            }
            try
            {
                if (!File.Exists(s.CoverPath)) return null;
                var opt = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(s.CoverPath, opt);
                int sample = 1;
                while (opt.OutWidth / sample > maxPx * 2 || opt.OutHeight / sample > maxPx * 2)
                    sample *= 2;
                var o2 = new BitmapFactory.Options { InSampleSize = sample };
                var bmp = BitmapFactory.DecodeFile(s.CoverPath, o2);
                if (bmp != null)
                {
                    lock (_coverMemLock)
                    {
                        if (_coverMem.Count > 60) _coverMem.Clear();   // 简单封顶，防大曲库吃内存
                        _coverMem[key] = bmp;
                    }
                }
                return bmp;
            }
            catch { return null; }
        }
    }
}

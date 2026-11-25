using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Util;
using AndroidX.Core.App;
using IoPath = System.IO.Path;
using AhoChatA.Music;
using AhoChatA.Reader;

namespace AhoChatA.Vod
{
    /// <summary>
    /// 统一的本机下载中心：接管「在线音乐 / 在线视频 / 小说」的下载任务。
    /// 每个任务在后台线程执行，前端通过 /api/media/vod/download 轮询状态；
    /// 支持失败重试与「目录提醒」（把文件落盘位置显式返回给前端展示）。
    /// </summary>
    public class DownloadTask
    {
        public string Id = "";
        public string Type = "";          // video | music | novel
        public string Title = "";
        public string Status = "queued";  // queued | downloading | merging | done | failed | canceled
        public int Progress = 0;          // 0-100
        public string Error = "";
        public string OutputPath = "";    // 完成后文件(视频为 mp4，音乐为 mp3)
        public string DirHint = "";       // 目录提醒文案
        public DateTime Added = DateTime.UtcNow;
        // 视频专用
        public string Url = "";           // 该集 m3u8 地址
        public int Total = 0;             // 切片总数
        public int Done = 0;              // 已下载切片数
        // 音乐专用（重试时需要重建歌曲上下文）
        public OnlineSong MusicSong = null;
        // 小说专用（书架书籍 id，运行期从阅读库取章节）
        public long DownloadedBytes = 0;
        public long TotalBytes = -1;
        public string DownloadInfo = "";
        public string BookId = "";
    }

    public static partial class DownloadCenter
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, DownloadTask> _tasks = new Dictionary<string, DownloadTask>();
        private static int _seq = 0;

        /// <summary>应用上下文（用于通知、媒体扫描等）。由 DownloaderService/Activity 初始化时设置。</summary>
        public static Context AppContext { get; set; }

        // 视频合并产物（持久）：<FilesDir>/vod/video/<标题>.mp4
        private static string VideoDir() => VodConfig.ExternalDir(Android.OS.Environment.DirectoryMovies, "video");
        // 切片临时目录：外部缓存 vod_tmp/<taskId>/（合并后删除）
        private static string TmpDir(string id) => IoPath.Combine(VodConfig.ExternalTmpRoot(), id);

        // ---------------- 对外 API ----------------

        public static DownloadTask QueueVideo(string url, string title)
        {
            return QueueHttp(url, string.IsNullOrWhiteSpace(title) ? L10n.T("在线视频") : title);
        }

        public static DownloadTask QueueMusic(OnlineSong s, string quality, string name)
        {
            var t = new DownloadTask
            {
                Id = NewId(),
                Type = "music",
                Title = string.IsNullOrWhiteSpace(name) ? s.Title : name,
                Status = "queued",
                Added = DateTime.UtcNow,
                MusicSong = s   // 必须保存，否则失败重试时 RunMusic 拿不到歌曲上下文
            };
            t.DirHint = L10n.T("音乐/封面/歌词将保存到同一目录: ") + OnlineMusic.DownloadDir();
            lock (_lock) _tasks[t.Id] = t;
            Task.Run(async () => await RunMusic(t.Id, s, quality, name));
            return t;
        }

        public static DownloadTask QueueNovel(string bookId, string title)
        {
            var t = new DownloadTask
            {
                Id = NewId(),
                Type = "novel",
                Title = string.IsNullOrWhiteSpace(title) ? L10n.T("小说") : title,
                BookId = bookId ?? "",
                Status = "queued",
                Added = DateTime.UtcNow
            };
            t.DirHint = L10n.T("合并后的 txt 将保存到: ") + NovelDir();
            lock (_lock) _tasks[t.Id] = t;
            Task.Run(() => RunNovel(t.Id));
            return t;
        }

        public static List<DownloadTask> List()
        {
            lock (_lock)
            {
                var r = new List<DownloadTask>();
                foreach (var kv in _tasks.Values) r.Add(kv);
                r.Sort((a, b) => b.Added.CompareTo(a.Added));
                return r;
            }
        }

        public static DownloadTask Get(string id)
        {
            lock (_lock) { _tasks.TryGetValue(id ?? "", out var t); return t; }
        }

        public static bool Retry(string id)
        {
            DownloadTask t;
            lock (_lock) { if (!_tasks.TryGetValue(id ?? "", out t)) return false; }
            if (t.Status != "failed" && t.Status != "canceled") return false;
            if (RetryHttp(t)) return true;
            t.Error = ""; t.Progress = 0; t.Done = 0; t.Status = "queued";
            if (t.Type == "video") Task.Run(() => RunVideo(t.Id));
            else if (t.Type == "music") Task.Run(async () => await RunMusic(t.Id, t.MusicSong, "standard", t.Title));
            else if (t.Type == "novel") Task.Run(() => RunNovel(t.Id));
            return true;
        }

        public static void ClearDone()
        {
            lock (_lock)
            {
                var dead = new List<string>();
                foreach (var kv in _tasks) if (kv.Value.Status == "done") dead.Add(kv.Key);
                foreach (var k in dead)
                {
                    _tasks.Remove(k);
                    HttpContext context;
                    if (HttpContexts.TryGetValue(k, out context) && !context.Running) { context.Cancellation?.Dispose(); HttpContexts.Remove(k); }
                }
            }
            SaveHistory();
        }

        /// <summary>
        /// 登记一个已在别处直接落盘的文件（如浏览器 Blob / GM_download 保存的文件），
        /// 使其出现在下载管理器列表中（状态直接为 done，可打开 / 分享）。
        /// </summary>
        public static DownloadTask RecordSaved(string fileName, string outputPath, string type = null, string sourceUrl = "", string note = null)
        {
            var t = new DownloadTask
            {
                Id = NewId(),
                Type = string.IsNullOrWhiteSpace(type) ? GuessTypeByName(fileName ?? outputPath) : type,
                Title = string.IsNullOrWhiteSpace(fileName) ? IoPath.GetFileName(outputPath ?? "") : fileName,
                Status = "done",
                Progress = 100,
                OutputPath = outputPath ?? "",
                Url = sourceUrl ?? "",
                DownloadInfo = note ?? L10n.T("浏览器直接保存"),
                Added = DateTime.UtcNow
            };
            try
            {
                if (!string.IsNullOrEmpty(t.OutputPath) && File.Exists(t.OutputPath))
                {
                    var len = new FileInfo(t.OutputPath).Length;
                    t.TotalBytes = len;
                    t.DownloadedBytes = len;
                }
            }
            catch { }
            lock (_lock) _tasks[t.Id] = t;
            SaveHistory();
            return t;
        }

        // ---------------- 执行：视频 ----------------

        private static async Task RunVideo(string id)
        {
            DownloadTask t;
            lock (_lock) { if (!_tasks.TryGetValue(id, out t)) return; }
            try
            {
                SetStatus(t, "downloading");
                ShowDownloadNotification(t, 0, L10n.T("正在准备..."));
                string m3u8 = HttpGetText(t.Url);
                if (string.IsNullOrEmpty(m3u8)) { Fail(t, L10n.T("无法获取 m3u8 清单")); ShowDownloadNotification(t, -1, L10n.T("获取列表失败")); return; }
                var segs = ParseSegments(m3u8, t.Url);
                if (segs.Count == 0) { Fail(t, "m3u8 中未解析到任何切片"); ShowDownloadNotification(t, -1, "无可用切片"); return; }
                t.Total = segs.Count;

                var tmp = TmpDir(t.Id);
                try { Directory.CreateDirectory(tmp); } catch { }

                // 断点续传：读取已下载的切片索引
                var progressFile = IoPath.Combine(tmp, ".progress");
                var doneSet = new HashSet<int>();
                if (File.Exists(progressFile))
                {
                    try
                    {
                        var json = File.ReadAllText(progressFile);
                        var arr = Newtonsoft.Json.JsonConvert.DeserializeObject<int[]>(json);
                        if (arr != null) foreach (var i in arr) doneSet.Add(i);
                    }
                    catch { }
                }

                var files = new List<string>(segs.Count);
                var filesLock = new object();
                int completed = doneSet.Count;
                var semaphore = new SemaphoreSlim(4); // 4 个并发下载切片
                var tasks = new List<Task>();

                for (int i = 0; i < segs.Count; i++)
                {
                    if (t.Status == "canceled") return;
                    int idx = i;
                    if (doneSet.Contains(idx))
                    {
                        var existing = IoPath.Combine(tmp, idx.ToString("D5") + ".ts");
                        if (File.Exists(existing) && new FileInfo(existing).Length > 0)
                        {
                            lock (filesLock) files.Add(existing);
                            continue;
                        }
                    }

                    await semaphore.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var dest = IoPath.Combine(tmp, idx.ToString("D5") + ".ts");
                            if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                            {
                                try { DownloadFile(segs[idx], dest); } catch { }
                            }
                            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                            {
                                lock (filesLock)
                                {
                                    files.Add(dest);
                                    doneSet.Add(idx);
                                    completed = doneSet.Count;
                                    int prog = Math.Min(99, completed * 100 / segs.Count);
                                    SetProgress(t, prog);
                                    ShowDownloadNotification(t, prog, $"正在下载... {prog}%");
                                    // 持久化进度
                                    try { File.WriteAllText(progressFile, Newtonsoft.Json.JsonConvert.SerializeObject(doneSet.ToArray())); } catch { }
                                }
                            }
                        }
                        finally { semaphore.Release(); }
                    }));
                }

                Task.WaitAll(tasks.ToArray());

                if (t.Status == "canceled") return;

                // 按顺序排序文件
                files.Sort((a, b) =>
                {
                    var na = Path.GetFileNameWithoutExtension(a);
                    var nb = Path.GetFileNameWithoutExtension(b);
                    return int.Parse(na).CompareTo(int.Parse(nb));
                });

                if (files.Count == 0) { Fail(t, "所有切片下载失败（网络或源站限制）"); ShowDownloadNotification(t, -1, "下载失败"); return; }

                SetStatus(t, "merging");
                ShowDownloadNotification(t, -1, L10n.T("正在合并..."));
                var vdir = VideoDir();
                try { Directory.CreateDirectory(vdir); } catch { }
                string outMp4 = IoPath.Combine(vdir, SafeName(t.Title) + ".mp4");
                int n = 1;
                while (File.Exists(outMp4))
                    outMp4 = IoPath.Combine(vdir, SafeName(t.Title) + "_" + (++n) + ".mp4");

                bool ok = TsToMp4.Merge(files, outMp4, msg => Log(t, msg));
                if (!ok) { Fail(t, L10n.T("切片合并为 mp4 失败（见日志）")); ShowDownloadNotification(t, -1, L10n.T("合并失败")); return; }

                // 合并完成，清理临时切片和进度文件
                try { Directory.Delete(tmp, true); } catch { }
                t.OutputPath = outMp4;
                t.Progress = 100;
                SetStatus(t, "done");
                ShowDownloadNotification(t, 100, L10n.T("下载完成"));

                // 触发媒体库扫描（不等待：扫描失败不影响本条已落盘的下载）
                try { _ = MusicLibrary.ScanAsync(); } catch { }
            }
            catch (Exception ex) { Fail(t, ex.Message); ShowDownloadNotification(t, -1, L10n.T("下载失败: ") + ex.Message); }
        }

        // ---------------- 执行：音乐（三件套同名下载） ----------------

        private static async Task RunMusic(string id, OnlineSong s, string quality, string name)
        {
            DownloadTask t;
            lock (_lock) { if (!_tasks.TryGetValue(id, out t)) return; }
            try
            {
                SetStatus(t, "downloading");
                // 重试时若 s 为空，无法重建搜索上下文，直接失败（首次入队已带 s）
                if (s == null) { Fail(t, "重试缺少歌曲上下文，请重新点击下载"); return; }
                string path = await OnlineMusic.DownloadAsync(s, quality ?? "standard", name);
                if (string.IsNullOrEmpty(path)) { Fail(t, L10n.T("下载失败（网络或存储不可用）")); return; }
                t.OutputPath = path;
                t.Progress = 100;
                SetStatus(t, "done");
            }
            catch (Exception ex) { Fail(t, ex.Message); }
        }

        // ---------------- 执行：小说（逐章抓取 → 合并为同一个 txt） ----------------

        private static string NovelDir() => VodConfig.ExternalDir(Android.OS.Environment.DirectoryDocuments);

        private static int DonePct(DownloadTask t)
        {
            return t.Total <= 0 ? 0 : System.Math.Min(99, t.Done * 100 / t.Total);
        }

        private static void RunNovel(string id)
        {
            DownloadTask t;
            lock (_lock) { if (!_tasks.TryGetValue(id, out t)) return; }
            try
            {
                if (ReaderLibrary.AppContext == null)
                {
                    try { ReaderLibrary.Init(global::Android.App.Application.Context); } catch { }
                }
                SetStatus(t, "downloading");

                var item = ReaderLibrary.Find(t.BookId);
                if (item == null) { Fail(t, "书架上找不到该书（请先在阅读里打开或收藏）"); return; }
                if (ReaderLibrary.FormatOf(item) == "pdf")
                { Fail(t, L10n.T("PDF 暂不支持整本导出为 txt（可在阅读里按页查看）")); return; }
                var book = ReaderLibrary.GetBook(item, out string err);
                if (book == null) { Fail(t, "无法读取书籍：" + (err ?? "未知错误")); return; }

                // 在线书需先联网补齐目录；本地书 GetBook 已解析好章节
                if (book.Chapters == null || book.Chapters.Count == 0)
                {
                    var e2 = ReaderLibrary.EnsureOnlineAsync(item, book).GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(e2)) { Fail(t, L10n.T("加载目录失败：") + e2); return; }
                }
                if (book.Chapters == null || book.Chapters.Count == 0) { Fail(t, "该书没有可下载的章节"); return; }

                t.Total = book.Chapters.Count;
                var texts = new List<string>(book.Chapters.Count);
                for (int i = 0; i < book.Chapters.Count; i++) texts.Add("");
                var okFlags = new bool[book.Chapters.Count];

                // 逐章抓取：每章最多重试 4 次；单次抓取超过阈值视为超时，自动重试（超时章节重试）
                const int chapterTimeoutMs = 20000;
                for (int i = 0; i < book.Chapters.Count; i++)
                {
                    if (t.Status == "canceled") return;
                    var ch = book.Chapters[i];
                    if (ch.IsVolume) { okFlags[i] = true; t.Done++; SetProgress(t, DonePct(t)); continue; }
                    string text = null;
                    bool ok = false;
                    for (int attempt = 0; attempt < 4 && !ok; attempt++)
                    {
                        try
                        {
                            var ctask = ReaderLibrary.ChapterTextAsync(item, book, i);
                            var d = Task.WhenAny(ctask, Task.Delay(chapterTimeoutMs)).GetAwaiter().GetResult();
                            if (d != ctask) { text = null; ok = false; }        // 超时：下一轮重试
                            else { text = ctask.GetAwaiter().GetResult(); ok = !string.IsNullOrWhiteSpace(text); }
                        }
                        catch { text = null; ok = false; }
                    }
                    texts[i] = text ?? "";
                    okFlags[i] = ok;
                    t.Done++;
                    SetProgress(t, DonePct(t));
                }

                // 合并为同一个 txt：即便有失败章节也强制合并（失败章用占位说明）
                SetStatus(t, "merging");
                var dir = NovelDir();
                try { Directory.CreateDirectory(dir); } catch { }
                string outTxt = IoPath.Combine(dir, SafeName(t.Title) + ".txt");
                int dup = 1;
                while (System.IO.File.Exists(outTxt))
                    outTxt = IoPath.Combine(dir, SafeName(t.Title) + "_" + (++dup) + ".txt");
                int failed = 0;
                using (var sw = new StreamWriter(outTxt, false, new System.Text.UTF8Encoding(false)))
                {
                    sw.WriteLine(t.Title);
                    sw.WriteLine();
                    for (int i = 0; i < book.Chapters.Count; i++)
                    {
                        var ch = book.Chapters[i];
                        if (ch.IsVolume)
                        {
                            sw.WriteLine();
                            sw.WriteLine("    " + (ch.Title ?? ""));
                            sw.WriteLine();
                            continue;
                        }
                        sw.WriteLine(ch.Title);
                        if (okFlags[i] && !string.IsNullOrEmpty(texts[i]))
                            sw.WriteLine(texts[i]);
                        else { sw.WriteLine(L10n.T("（本章下载失败，已跳过）")); failed++; }
                        sw.WriteLine();
                    }
                }
                t.OutputPath = outTxt;
                t.Progress = 100;
                SetStatus(t, "done");
                if (failed > 0)
                    t.Error = failed + L10n.T(" 个章节下载失败，已强制合并成功的 ") + (book.Chapters.Count - failed) + L10n.T(" 章");
            }
            catch (Exception ex) { Fail(t, ex.Message); }
        }

        // ---------------- m3u8 解析 ----------------

        private static List<string> ParseSegments(string m3u8, string baseUrl)
        {
            var list = new List<string>();
            string subPlaylist = null;
            var lines = m3u8.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#")) continue;
                if (line.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    if (subPlaylist == null) subPlaylist = line;   // 记录主播放列表里的第一个子 m3u8
                    continue;
                }
                list.Add(AbsUrl(line, baseUrl));
            }
            if (list.Count == 0 && subPlaylist != null)
            {
                var sub = AbsUrl(subPlaylist, baseUrl);
                var subText = HttpGetText(sub);
                if (!string.IsNullOrEmpty(subText)) return ParseSegments(subText, sub);
            }
            return list;
        }

        private static string AbsUrl(string uri, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(uri)) return uri;
            if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return uri;
            try
            {
                var u = new Uri(baseUrl);
                if (uri.StartsWith("/"))
                    return u.Scheme + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + uri;
                int slash = baseUrl.LastIndexOf('/');
                return (slash > 0 ? baseUrl.Substring(0, slash + 1) : baseUrl + "/") + uri;
            }
            catch { return uri; }
        }

        // ---------------- HTTP 工具 ----------------

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        static DownloadCenter()
        {
            try
            {
                _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36");
            }
            catch { }
        }

        private static string HttpGetText(string url)
        {
            try { return _http.GetStringAsync(url).GetAwaiter().GetResult(); }
            catch { return null; }
        }

        private static void DownloadFile(string url, string dest)
        {
            var bytes = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0) return;
            File.WriteAllBytes(dest, bytes);
        }

        // ---------------- 杂项 ----------------

        private static string NewId()
        {
            var s = System.Threading.Interlocked.Increment(ref _seq);
            return DateTime.UtcNow.Ticks.ToString("X") + "_" + s;
        }

        private static string SafeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "download";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { sb.Append('_'); continue; }
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    continue;
                sb.Append(c);
            }
            var r = sb.ToString().Trim('_');
            return r.Length == 0 ? "download" : r;
        }

        // ---------------- 历史记录持久化 ----------------
        // 下载列表默认只存在于内存，进程重启后历史会消失；这里把「已完成 / 失败 / 已取消」
        // 的任务落盘，保证用户回到下载管理器仍能找到已经下载好的文件。

        private const string HistoryFileName = "download_history.json";
        private static bool _historyLoaded;

        private static string HistoryPath()
        {
            var ctx = AppContext ?? global::Android.App.Application.Context;
            return IoPath.Combine(ctx.FilesDir.AbsolutePath, HistoryFileName);
        }

        private class HistoryRecord
        {
            public string Id = "";
            public string Type = "";
            public string Title = "";
            public string Status = "";
            public int Progress;
            public string Error = "";
            public string OutputPath = "";
            public string DirHint = "";
            public string Url = "";
            public string DownloadInfo = "";
            public long AddedTicks;
            public long DownloadedBytes;
            public long TotalBytes = -1;
        }

        /// <summary>载入历史记录（只恢复已结束的任务；进行中的任务重启后已无上下文）。</summary>
        public static void LoadHistory()
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            try
            {
                var file = HistoryPath();
                if (!File.Exists(file)) return;
                var records = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryRecord>>(File.ReadAllText(file));
                if (records == null) return;
                lock (_lock)
                {
                    foreach (var r in records)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                        if (r.Status != "done" && r.Status != "failed" && r.Status != "canceled") continue;
                        if (_tasks.ContainsKey(r.Id)) continue;
                        _tasks[r.Id] = new DownloadTask
                        {
                            Id = r.Id,
                            Type = string.IsNullOrEmpty(r.Type) ? "other" : r.Type,
                            Title = r.Title ?? "",
                            Status = r.Status,
                            Progress = r.Progress,
                            Error = r.Error ?? "",
                            OutputPath = r.OutputPath ?? "",
                            DirHint = r.DirHint ?? "",
                            Url = r.Url ?? "",
                            DownloadInfo = r.DownloadInfo ?? "",
                            Added = r.AddedTicks > 0 ? new DateTime(r.AddedTicks, DateTimeKind.Utc) : DateTime.UtcNow,
                            DownloadedBytes = r.DownloadedBytes,
                            TotalBytes = r.TotalBytes
                        };
                    }
                }
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("MchatDl", "加载下载历史失败: " + ex.Message); }
        }

        /// <summary>把已结束的任务写入历史文件。</summary>
        public static void SaveHistory()
        {
            try
            {
                var list = new List<HistoryRecord>();
                lock (_lock)
                {
                    foreach (var kv in _tasks)
                    {
                        var t = kv.Value;
                        if (t == null) continue;
                        if (t.Status != "done" && t.Status != "failed" && t.Status != "canceled") continue;
                        list.Add(new HistoryRecord
                        {
                            Id = t.Id,
                            Type = t.Type,
                            Title = t.Title,
                            Status = t.Status,
                            Progress = t.Progress,
                            Error = t.Error,
                            OutputPath = t.OutputPath,
                            DirHint = t.DirHint,
                            Url = t.Url,
                            DownloadInfo = t.DownloadInfo,
                            AddedTicks = t.Added.Ticks,
                            DownloadedBytes = t.DownloadedBytes,
                            TotalBytes = t.TotalBytes
                        });
                    }
                }
                var file = HistoryPath();
                if (list.Count == 0)
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    return;
                }
                File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(list));
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("MchatDl", "保存下载历史失败: " + ex.Message); }
        }

        /// <summary>按文件名 / 扩展名推断下载类型，用于下载管理器的分类筛选。</summary>
        private static string GuessTypeByName(string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            var ext = "";
            var dot = n.LastIndexOf('.');
            if (dot >= 0 && dot < n.Length - 1) ext = n.Substring(dot);
            switch (ext)
            {
                case ".m3u8": return "hls";
                case ".mp4": case ".mkv": case ".avi": case ".mov": case ".flv": case ".webm": case ".m4v": case ".ts": return "video";
                case ".mp3": case ".m4a": case ".flac": case ".wav": case ".aac": case ".ogg": case ".opus": return "music";
                case ".txt": case ".epub": case ".mobi": case ".azw3": return "novel";
                case ".pdf": case ".doc": case ".docx": case ".xls": case ".xlsx": case ".ppt": case ".pptx":
                case ".md": case ".csv": case ".zip": case ".rar": case ".7z": case ".apk": case ".json": return "document";
                default: return "other";
            }
        }

        private static void SetStatus(DownloadTask t, string st)
        {
            lock (_lock) { t.Status = st; }
            if (st == "done" || st == "failed" || st == "canceled") SaveHistory();
        }
        private static void SetProgress(DownloadTask t, int p) { lock (_lock) { t.Progress = p; } }
        private static void Fail(DownloadTask t, string err)
        {
            lock (_lock) { t.Status = "failed"; t.Error = err; }
            SaveHistory();
        }
        private static void Log(DownloadTask t, string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            Android.Util.Log.Info("MchatDl", "[" + t.Id + "] " + msg);
        }

        /// <summary>
        /// 显示/更新下载通知（通知栏进度）。
        /// </summary>
        private static void ShowDownloadNotification(DownloadTask t, int progress, string text)
        {
            try
            {
                var ctx = AppContext ?? global::Android.App.Application.Context;
                if (ctx == null) return;

                var channelId = "mchat_download";
                var mgr = (NotificationManager)ctx.GetSystemService(Context.NotificationService);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var channel = new NotificationChannel(channelId, L10n.T("下载管理"), NotificationImportance.Low);
                    channel.Description = L10n.T("文件下载进度通知");
                    mgr.CreateNotificationChannel(channel);
                }

                var builder = new NotificationCompat.Builder(ctx, channelId)
                    .SetContentTitle(t.Title)
                    .SetContentText(text)
                    .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
                    .SetProgress(100, progress, progress < 0)
                    .SetOngoing(progress < 100)
                    .SetAutoCancel(progress >= 100)
                    .SetOnlyAlertOnce(true);

                if (progress >= 100 && !string.IsNullOrEmpty(t.OutputPath) && File.Exists(t.OutputPath))
                {
                    builder.SetContentText(L10n.T("下载完成，点击打开"))
                           .SetContentIntent(PendingIntent.GetActivity(ctx, t.Id.GetHashCode(),
                               new Intent(Intent.ActionView)
                                   .SetDataAndType(Android.Net.Uri.FromFile(new Java.IO.File(t.OutputPath)), GetMimeType(t.OutputPath))
                                   .AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission),
                               PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
                }

                mgr.Notify(t.Id.GetHashCode(), builder.Build());
            }
            catch { }
        }

        private static string GetMimeType(string path)
        {
            var ext = IoPath.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".mp4": case ".mkv": case ".mov": case ".webm": return "video/*";
                case ".mp3": case ".flac": case ".m4a": case ".wav": case ".ogg": return "audio/*";
                case ".jpg": case ".jpeg": case ".png": case ".webp": case ".gif": return "image/*";
                case ".pdf": return "application/pdf";
                case ".epub": return "application/epub+zip";
                case ".txt": return "text/plain";
                default: return "application/octet-stream";
            }
        }
    }
}

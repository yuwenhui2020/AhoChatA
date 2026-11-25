using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Widget;
using AhoChatA.Vod;
using AhoChatA.Music;
using AhoChatA.Reader;

namespace AhoChatA.Vod
{
    /// <summary>
    /// 统一下载服务：接管外部 Intent（分享链接、打开方式）、M3U8 视频下载+转码合并、
    /// 音乐/小说下载。对外暴露静态 API，供 Activity/Service/Receiver 调用。
    ///
    /// 支持的 Intent：
    ///   - ACTION_SEND (text/plain) -> 识别 URL -> 视频/音乐/文档自动分类下载
    ///   - ACTION_VIEW (http/https/magnet) -> 同上
    ///   - 自定义 scheme: mchat://download?url=...&type=video|music|novel&title=...
    ///
    /// M3U8 转码：沿用 TsToMp4（MediaExtractor/MediaMuxer，无 ffmpeg 依赖），
    /// 支持多线程切片下载、断点续传、合并进度回调。
    ///
    /// 与 DownloadCenter 的关系：DownloadCenter 是内部任务队列，DownloaderService 是对外门面，
    /// 负责 URL 识别、Intent 解析、外部调用入口统一。
    /// </summary>
    public static class DownloaderService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// 从外部 Intent 解析下载参数并入队。返回任务 ID（可为空）。
        /// 支持：ACTION_SEND(text/plain)、ACTION_VIEW(http/https)、mchat://download scheme。
        /// </summary>
        public static string HandleIntent(Context ctx, Intent intent)
        {
            if (ctx == null || intent == null) return null;

            string action = intent.Action ?? "";
            string type = intent.Type ?? "";
            string url = "";
            string title = "";
            string forcedType = ""; // video | music | novel

            try
            {
                if (action == Intent.ActionSend && type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    // 分享文本/链接
                    url = intent.GetStringExtra(Intent.ExtraText) ?? "";
                    title = intent.GetStringExtra(Intent.ExtraSubject) ?? "";
                }
                else if (action == Intent.ActionView)
                {
                    // 打开链接 / 自定义 scheme
                    var data = intent.Data;
                    if (data != null)
                    {
                        url = data.ToString();
                        // mchat://download?url=...&type=video&title=...
                        if (url.StartsWith("mchat://download", StringComparison.OrdinalIgnoreCase))
                        {
                            var q = Android.Net.Uri.Parse(url).Query ?? "";
                            var dic = ParseQuery(q);
                            dic.TryGetValue("url", out url);
                            dic.TryGetValue("title", out title);
                            dic.TryGetValue("type", out forcedType);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(url)) return null;

                // 自动识别类型
                string detectedType = DetectType(url, forcedType);
                if (string.IsNullOrEmpty(detectedType))
                {
                    L10n.Toast(ctx, L10n.T("无法识别的链接类型：") + url, ToastLength.Short).Show();
                    return null;
                }

                // 入队
                var taskId = EnqueueByType(detectedType, url, title, ctx);
                if (!string.IsNullOrEmpty(taskId))
                {
                    L10n.Toast(ctx, $"已加入下载队列：{detectedType} · {title}", ToastLength.Short).Show();
                    // 打开下载管理页
                    try
                    {
                        var i = new Intent(ctx, typeof(DownloadActivity));
                        i.AddFlags(ActivityFlags.NewTask);
                        ctx.StartActivity(i);
                    }
                    catch { }
                }
                return taskId;
            }
            catch (Exception ex)
            {
                Log.Error("MchatDownloader", "HandleIntent 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 统一入队：根据类型创建下载任务。
        /// </summary>
        public static string EnqueueByType(string type, string url, string title, Context ctx = null)
        {
            if (ctx != null) DownloadCenter.AppContext = ctx.ApplicationContext;
            // Ordinary HTTP links and HLS share the same task list and validated downloader.
            return DownloadCenter.QueueHttp(url, title, forceHls: string.Equals(type, "m3u8", StringComparison.OrdinalIgnoreCase))?.Id;
        }

        /// <summary>
        /// 根据 URL 自动识别类型。
        /// </summary>
        private static string DetectType(string url, string forcedType)
        {
            if (!string.IsNullOrEmpty(forcedType)) return forcedType.ToLowerInvariant();

            var lower = url.ToLowerInvariant();
            if (lower.Contains(".m3u8") || lower.Contains("/hls/") || lower.Contains("m3u8?"))
                return "video";
            if (lower.EndsWith(".mp4") || lower.EndsWith(".mkv") || lower.EndsWith(".mov") || lower.EndsWith(".webm"))
                return "video";
            if (lower.EndsWith(".mp3") || lower.EndsWith(".flac") || lower.EndsWith(".m4a") || lower.EndsWith(".wav") || lower.EndsWith(".ogg"))
                return "music";
            if (lower.EndsWith(".epub") || lower.EndsWith(".txt") || lower.EndsWith(".pdf"))
                return "novel";
            // 默认按视频处理（m3u8 最常见）
            return "video";
        }

        /// <summary>
        /// 解析查询字符串为字典。
        /// </summary>
        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dic;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                {
                    try { dic[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]); } catch { }
                }
            }
            return dic;
        }
    }

    /// <summary>
    /// 下载任务扩展：通用文件下载（非视频/音乐/小说），支持断点续传、进度回调。
    /// </summary>
    public sealed class GenericDownloadTask
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Type { get; set; } // video|music|novel|file
        public string Status { get; set; } = "queued";
        public int Progress { get; set; }
        public string Error { get; set; }
        public string OutputPath { get; set; }
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public DateTime Added { get; set; } = DateTime.UtcNow;

        // 进度回调（供 UI 更新）
        public Action<GenericDownloadTask> OnProgress;
        public Action<GenericDownloadTask> OnCompleted;
        public Action<GenericDownloadTask, string> OnFailed;
    }

    /// <summary>
    /// 扩展 DownloadCenter 支持通用文件下载。
    /// </summary>
    public static partial class DownloadCenter
    {
        private static readonly object _genLock = new object();
        private static readonly Dictionary<string, GenericDownloadTask> _genTasks = new Dictionary<string, GenericDownloadTask>();

        /// <summary>
        /// 入队通用文件下载。
        /// </summary>
        public static GenericDownloadTask QueueGenericFile(string url, string title, string type = "file")
        {
            var task = new GenericDownloadTask
            {
                Id = NewId(),
                Url = url,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(new Uri(url).LocalPath) : title,
                Type = type,
                Status = "queued",
                Added = DateTime.UtcNow
            };
            lock (_genLock) _genTasks[task.Id] = task;
            Task.Run(() => RunGeneric(task.Id));
            return task;
        }

        private static async void RunGeneric(string id)
        {
            GenericDownloadTask t;
            lock (_genLock) { if (!_genTasks.TryGetValue(id, out t)) return; }

            try
            {
                SetStatus(t, "downloading");

                // 目标路径
                string dir = GetOutputDir(t.Type);
                Directory.CreateDirectory(dir);
                string outPath = Path.Combine(dir, SafeName(t.Title));
                // 避免同名文件覆盖
                int n = 1;
                string baseName = Path.GetFileNameWithoutExtension(outPath);
                string ext = Path.GetExtension(outPath);
                while (File.Exists(outPath))
                {
                    outPath = Path.Combine(dir, $"{baseName} ({n}){ext}");
                    n++;
                }
                t.OutputPath = outPath;

                // 使用 DownloadEngine：断点续传、多线程分片、通知栏进度、媒体库扫描
                var ctx = AhoChatA.AhoChatApp.Context;
                var engine = new DownloadEngine(ctx, maxConcurrency: 4, chunkSize: 1024 * 1024);

                engine.OnProgress += (taskId, progress) =>
                {
                    t.Progress = progress;
                    t.DownloadedBytes = (long)(t.TotalBytes * progress / 100.0);
                    t.OnProgress?.Invoke(t);
                };
                engine.OnStatus += (taskId, status) => { t.Status = status; };
                engine.OnError += (taskId, error) => { t.Error = error; t.Status = "failed"; t.OnFailed?.Invoke(t, error); };
                engine.OnComplete += (taskId, outputPath) =>
                {
                    t.Progress = 100;
                    t.Status = "done";
                    t.OutputPath = outputPath;
                    t.OnCompleted?.Invoke(t);
                    // 媒体库扫描（DownloadEngine 内部已触发，这里再触发一次确保）
                    if (t.Type == "music" || t.Type == "video") { try { MusicLibrary.ScanAsync(); } catch { } }
                };

                await engine.StartAsync(t.Id, t.Url, t.OutputPath, t.Title, t.Type);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("MchatDownloader", "通用下载失败: " + ex.Message);
                Fail(t, ex.Message);
                t.OnFailed?.Invoke(t, ex.Message);
            }
        }

        private static string GetOutputDir(string type)
        {
            switch (type?.ToLowerInvariant())
            {
                case "music": return OnlineMusic.DownloadDir();
                case "video": return VideoDir();
                case "novel": return NovelDir();
                default:
                    // 通用文件存到公共 Download 目录
                    var pub = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads);
                    var d = Path.Combine(pub.AbsolutePath, "Mchat");
                    return d;
            }
        }

        // 复用现有的状态更新方法（需与 DownloadTask 同步）
        private static void SetStatus(GenericDownloadTask t, string st) { lock (_genLock) { t.Status = st; } }
        private static void SetProgress(GenericDownloadTask t, int p) { lock (_genLock) { t.Progress = Math.Max(0, Math.Min(100, p)); } }
        private static void Fail(GenericDownloadTask t, string err) { lock (_genLock) { t.Status = "failed"; t.Error = err; } }

        /// <summary>
        /// 获取通用下载任务列表（供 API/外部查询）。
        /// </summary>
        public static List<GenericDownloadTask> ListGeneric()
        {
            lock (_genLock)
            {
                var r = new List<GenericDownloadTask>(_genTasks.Values);
                r.Sort((a, b) => b.Added.CompareTo(a.Added));
                return r;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AhoChatA.Downloads;
namespace AhoChatA.Vod
{
    public sealed class HttpDownloadInspection
    {
        public bool IsHls, IsEncrypted, Supported;
        public string Summary, OutputExtension;
        public long TotalBytes;
        public int SegmentCount;
    }
    public static partial class DownloadCenter
    {
        sealed class HttpContext
        {
            public string RequestedTitle;
            public HttpDownloadOptions Options;
            public CancellationTokenSource Cancellation;
            public bool Running;
        }
        static string HttpOutputDir()
        {
            var context = AppContext ?? global::Android.App.Application.Context;
            var external = context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads);
            return System.IO.Path.Combine(external != null ? external.AbsolutePath : context.FilesDir.AbsolutePath, "Mchat");
        }

        /// <summary>供媒体库把浏览器/在线视频下载目录纳入扫描，避免下载完在影音列表里看不到。</summary>
        public static string HttpDownloadsDir() => HttpOutputDir();
        static readonly Dictionary<string, HttpContext> HttpContexts = new Dictionary<string, HttpContext>();
        public static DownloadTask QueueHttp(string url, string title = null, string cookie = null, string userAgent = null, string referer = null, string keyOverride = null, bool forceHls = false)
        {
            var options = new HttpDownloadOptions { Url = url, Cookie = cookie, UserAgent = userAgent, Referer = referer, KeyOverride = keyOverride, ForceHls = forceHls };
            // Validate immediately; retain sensitive request context only in this private runtime dictionary.
            using (var check = new HttpDownloadJob(options)) { }
            var task = new DownloadTask { Id = NewId(), Type = forceHls ? "hls" : "file", Title = string.IsNullOrWhiteSpace(title) ? "download" : title, Url = url, DirHint = L10n.T("保存目录: ") + HttpOutputDir() };
            lock (_lock) { _tasks[task.Id] = task; HttpContexts[task.Id] = new HttpContext { Options = options, RequestedTitle = title }; }
            StartHttp(task); return task;
        }
        public static async Task<HttpDownloadInspection> InspectHttpAsync(string url, string cookie = null, string userAgent = null, string referer = null, string keyOverride = null, CancellationToken cancellationToken = default(CancellationToken), bool forceHls = false)
        {
            using (var job = new HttpDownloadJob(new HttpDownloadOptions { Url = url, Cookie = cookie, UserAgent = userAgent, Referer = referer, KeyOverride = keyOverride, ForceHls = forceHls }))
            {
                var i = await job.InspectAsync(cancellationToken).ConfigureAwait(false);
                return new HttpDownloadInspection { IsHls = i.IsHls, IsEncrypted = i.IsEncrypted, Supported = i.Supported, Summary = i.Summary, TotalBytes = i.TotalBytes, SegmentCount = i.SegmentCount, OutputExtension = i.OutputExtension };
            }
        }
        public static bool CancelHttp(string id)
        {
            lock (_lock)
            {
                HttpContext context;
                if (!HttpContexts.TryGetValue(id ?? "", out context) || !context.Running) return false;
                context.Cancellation.Cancel(); return true;
            }
        }
        static bool RetryHttp(DownloadTask task)
        {
            lock (_lock) { if (!HttpContexts.ContainsKey(task.Id)) return false; }
            StartHttp(task); return true;
        }
        static void StartHttp(DownloadTask task)
        {
            HttpContext context;
            lock (_lock)
            {
                context = HttpContexts[task.Id]; if (context.Running) return;
                context.Cancellation?.Dispose(); context.Cancellation = new CancellationTokenSource(); context.Running = true;
                task.Status = "queued"; task.Error = ""; task.Progress = 0; task.Done = 0; task.DownloadedBytes = 0; task.TotalBytes = -1;
            }
            Task.Run(async () =>
            {
                try
                {
                    task.Status = "downloading"; ShowDownloadNotification(task, -1, L10n.T("正在准备下载..."));
                    var notificationClock = System.Diagnostics.Stopwatch.StartNew();
                    long lastNotification = 0;
                    using (var job = new HttpDownloadJob(context.Options))
                    {
                        task.OutputPath = await job.DownloadAsync(HttpOutputDir(), context.RequestedTitle, p =>
                        {
                            lock (_lock)
                            {
                                task.DownloadedBytes = p.DownloadedBytes; task.TotalBytes = p.TotalBytes; task.Done = p.Done; task.Total = p.Total; task.DownloadInfo = p.Info;
                                if (p.Total > 0) task.Type = "hls";
                                task.Progress = p.Total > 0 ? (int)Math.Min(99, 100L * p.Done / p.Total) : p.TotalBytes > 0 ? (int)Math.Min(99, p.DownloadedBytes * 100 / p.TotalBytes) : 0;
                            }
                            if (notificationClock.ElapsedMilliseconds - lastNotification >= 500)
                            {
                                lastNotification = notificationClock.ElapsedMilliseconds;
                                ShowDownloadNotification(task, task.Progress, p.Info + " · " + task.Progress + "%");
                            }
                        }, context.Cancellation.Token).ConfigureAwait(false);
                    }
                    task.Progress = 100; task.Status = "done"; ShowDownloadNotification(task, 100, L10n.T("下载完成"));
                }
                catch (OperationCanceledException) { task.Status = "canceled"; task.Error = L10n.T("下载已取消；可重试重新下载"); ShowDownloadNotification(task, 100, L10n.T("已取消")); }
                catch (Exception ex) { Fail(task, ex.Message); ShowDownloadNotification(task, 100, L10n.T("下载失败: ") + ex.Message); }
                finally { lock (_lock) context.Running = false; }
            });
        }
    }
}

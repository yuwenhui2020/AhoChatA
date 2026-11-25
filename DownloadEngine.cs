using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Util;
using AndroidX.Core.App;
using AhoChatA.Music;
using SysOpCanceled = System.OperationCanceledException;

namespace AhoChatA.Vod
{
    /// <summary>
    /// 增强下载引擎：支持断点续传、多线程分片、通知栏进度、媒体库入库、限速、完成动作
    /// - 使用 .part 临时文件 + .meta 进度文件实现断点续传
    /// - 支持可配置的并发分片数（默认 4）
    /// - Android 通知栏实时显示进度（前台服务风格，需配合 Foreground Service）
    /// - 完成后触发 MediaScannerConnection 入库
    /// - 全局限速、自动打开、自动解压
    /// </summary>
    public sealed class DownloadEngine
    {
        private readonly Context _ctx;
        private readonly HttpClient _http;
        private readonly int _maxConcurrency;
        private readonly int _chunkSize; // 默认 1MB
        private readonly string _tempDir;
        
        // 全局设置
        public static int GlobalSpeedLimitKbps { get; set; } = 0; // 0 = 不限速
        public static bool AutoOpenOnComplete { get; set; } = false;
        public static bool ExtractArchivesOnComplete { get; set; } = false;

        public event Action<string, int> OnProgress;    // taskId, progress(0-100)
        public event Action<string, string> OnStatus;   // taskId, status
        public event Action<string, string> OnError;    // taskId, error
        public event Action<string, string> OnComplete; // taskId, outputPath

        private readonly Dictionary<string, DownloadState> _states = new Dictionary<string, DownloadState>();
        private readonly object _stateLock = new object();

        public DownloadEngine(Context ctx, int maxConcurrency = 4, int chunkSize = 1024 * 1024)
        {
            _ctx = ctx.ApplicationContext;
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _chunkSize = chunkSize;
            _tempDir = Path.Combine(_ctx.CacheDir.AbsolutePath, "downloads");
            Directory.CreateDirectory(_tempDir);

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36");
        }

        public static void EnsureInitialized(Context ctx)
        {
            if (_instance == null) _instance = new DownloadEngine(ctx);
        }
        private static DownloadEngine _instance;
        public static DownloadEngine Instance => _instance;

        /// <summary>
        /// 开始下载（支持断点续传）。
        /// </summary>
        /// <param name="taskId">任务唯一标识</param>
        /// <param name="url">下载 URL</param>
        /// <param name="outputPath">目标文件完整路径</param>
        /// <param name="title">显示标题（用于通知）</param>
        /// <param name="type">文件类型：video/music/file/novel</param>
        /// <returns>是否启动成功</returns>
        public async Task<bool> StartAsync(string taskId, string url, string outputPath, string title, string type = "file")
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(outputPath))
                return false;

            // 检查是否已在下载
            lock (_stateLock)
            {
                if (_states.ContainsKey(taskId)) return false;
            }

            // 确定输出目录
            var outDir = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var state = new DownloadState
            {
                TaskId = taskId,
                Url = url,
                OutputPath = outputPath,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(outputPath) : title,
                Type = type,
                TempDir = Path.Combine(_tempDir, taskId),
                PartFile = outputPath + ".part",
                MetaFile = outputPath + ".meta"
            };

            Directory.CreateDirectory(state.TempDir);

            lock (_stateLock) _states[taskId] = state;

            // 启动下载（后台任务）
            _ = Task.Run(() => RunDownloadAsync(state));

            // 显示通知
            ShowNotification(state, 0, L10n.T("正在准备..."));

            return true;
        }

        /// <summary>
        /// 暂停/取消下载。
        /// </summary>
        public void Cancel(string taskId)
        {
            lock (_stateLock)
            {
                if (_states.TryGetValue(taskId, out var s))
                {
                    s.CancelToken.Cancel();
                    s.Status = "canceled";
                }
            }
        }

        /// <summary>
        /// 获取下载状态。
        /// </summary>
        public DownloadState GetState(string taskId)
        {
            lock (_stateLock)
            {
                _states.TryGetValue(taskId, out var s);
                return s;
            }
        }

        private async Task RunDownloadAsync(DownloadState state)
        {
            try
            {
                // 1. HEAD 请求获取文件大小、是否支持 Range
                long totalSize = -1;
                bool supportsRange = false;
                using (var headReq = new HttpRequestMessage(HttpMethod.Head, state.Url))
                {
                    var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, state.CancelToken.Token);
                    if (headResp.Content.Headers.ContentLength.HasValue)
                        totalSize = headResp.Content.Headers.ContentLength.Value;
                    supportsRange = headResp.Headers.AcceptRanges.Contains("bytes");
                }
                state.TotalBytes = totalSize;
                state.SupportsRange = supportsRange;

                // 2. 读取/初始化元数据（断点续传）
                if (File.Exists(state.MetaFile))
                {
                    LoadMeta(state);
                }
                else
                {
                    // 初始化分片
                    if (supportsRange && totalSize > 0)
                    {
                        state.Chunks = CreateChunks(totalSize);
                    }
                    else
                    {
                        // 不支持 Range 或未知大小：单分片
                        state.Chunks = new List<ChunkInfo> { new ChunkInfo { Index = 0, Start = 0, End = -1, Completed = false } };
                    }
                    SaveMeta(state);
                }

                // 3. 确保 .part 文件存在且大小正确
                if (!File.Exists(state.PartFile))
                {
                    using (var fs = new FileStream(state.PartFile, FileMode.CreateNew)) { }
                }
                else
                {
                    // 验证 .part 文件大小
                    var fi = new FileInfo(state.PartFile);
                    if (fi.Length != totalSize && totalSize > 0)
                    {
                        // 大小不匹配，重新开始
                        File.Delete(state.PartFile);
                        File.Delete(state.MetaFile);
                        state.Chunks = CreateChunks(totalSize);
                        state.DownloadedBytes = 0;
                        using (var fs = new FileStream(state.PartFile, FileMode.CreateNew)) { }
                        SaveMeta(state);
                    }
                }

                state.Status = "downloading";
                UpdateStatus(state, "downloading");

                // 4. 多线程下载分片
                var semaphore = new SemaphoreSlim(_maxConcurrency);
                var tasks = new List<Task>();

                foreach (var chunk in state.Chunks.Where(c => !c.Completed))
                {
                    var localChunk = chunk;
                    await semaphore.WaitAsync(state.CancelToken.Token);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadChunkAsync(state, localChunk);
                        }
                        finally { semaphore.Release(); }
                    }, state.CancelToken.Token));
                }

                await Task.WhenAll(tasks);

                if (state.CancelToken.IsCancellationRequested)
                {
                    state.Status = "canceled";
                    UpdateStatus(state, "canceled");
                    return;
                }

                // 5. 验证完成度
                if (state.Chunks.Any(c => !c.Completed))
                {
                    state.Status = "failed";
                    UpdateStatus(state, "failed");
                    state.Error = L10n.T("部分分片下载未完成");
                    OnError?.Invoke(state.TaskId, state.Error);
                    return;
                }

                // 6. 重命名 .part -> 最终文件
                if (File.Exists(state.OutputPath) && !TrashManager.Instance.MoveToTrash(state.OutputPath))
                    throw new IOException(L10n.T("无法将已有文件移入回收站，已取消覆盖：") + state.OutputPath);
                File.Move(state.PartFile, state.OutputPath);

                // 7. 清理临时文件
                CleanupTemp(state);

                state.Status = "done";
                state.Progress = 100;
                UpdateProgress(state, 100);
                UpdateStatus(state, "done");

                // 8. 媒体库扫描
                MediaScanner.ScanFile(_ctx, state.OutputPath, GetMimeType(state.OutputPath));

                // 9. 完成动作：自动打开、自动解压
                if (AutoOpenOnComplete || ExtractArchivesOnComplete)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (AutoOpenOnComplete)
                            {
                                await Task.Delay(500); // 等待文件系统稳定
                                if (File.Exists(state.OutputPath))
                                {
                                    var intent = new Intent(Intent.ActionView);
                                    var mime = GetMimeType(state.OutputPath);
                                    var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(_ctx, "com.Ahochat.android.fileprovider", new Java.IO.File(state.OutputPath));
                                    intent.SetDataAndType(uri, mime);
                                    intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                                    _ctx.StartActivity(intent);
                                }
                            }
                            if (ExtractArchivesOnComplete && IsArchive(state.OutputPath))
                            {
                                await ExtractArchiveAsync(state.OutputPath);
                            }
                        }
                        catch { }
                    });
                }

                // 10. 完成回调
                OnComplete?.Invoke(state.TaskId, state.OutputPath);
                OnProgress?.Invoke(state.TaskId, 100);

                // 更新通知为完成
                ShowNotification(state, 100, L10n.T("下载完成"));
            }
            catch (SysOpCanceled)
            {
                state.Status = "canceled";
                UpdateStatus(state, "canceled");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("DownloadEngine", $"任务 {state.TaskId} 失败: {ex.Message}");
                state.Status = "failed";
                state.Error = ex.Message;
                UpdateStatus(state, "failed");
                OnError?.Invoke(state.TaskId, ex.Message);
            }
            finally
            {
                lock (_stateLock) _states.Remove(state.TaskId);
            }
        }

        private async Task DownloadChunkAsync(DownloadState state, ChunkInfo chunk)
        {
            if (chunk.Completed) return;

            var req = new HttpRequestMessage(HttpMethod.Get, state.Url);
            if (chunk.End >= 0)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunk.Start, chunk.End);

            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (state.CancelToken.IsCancellationRequested) return;

                try
                {
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, state.CancelToken.Token))
                    {
                        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            throw new HttpRequestException($"HTTP {resp.StatusCode}");

                        using (var stream = await resp.Content.ReadAsStreamAsync())
                        using (var fs = new FileStream(state.PartFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        {
                            fs.Seek(chunk.Start, SeekOrigin.Begin);
                            var buffer = new byte[8192];
                            int read;
                            long chunkDownloaded = 0;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            long bytesSinceThrottle = 0;
                            
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, state.CancelToken.Token)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, read, state.CancelToken.Token);
                                chunkDownloaded += read;
                                bytesSinceThrottle += read;

                                // 实时更新进度
                                Interlocked.Add(ref state.DownloadedBytes, read);
                                UpdateProgressFromState(state);

                                // 全局限速
                                if (GlobalSpeedLimitKbps > 0)
                                {
                                    var targetMs = (bytesSinceThrottle * 1000) / (GlobalSpeedLimitKbps * 1024);
                                    var elapsedMs = sw.ElapsedMilliseconds;
                                    if (targetMs > elapsedMs)
                                    {
                                        var delay = targetMs - elapsedMs;
                                        if (delay > 0) await Task.Delay((int)delay, state.CancelToken.Token);
                                    }
                                    if (sw.ElapsedMilliseconds > 1000)
                                    {
                                        sw.Restart();
                                        bytesSinceThrottle = 0;
                                    }
                                }
                            }
                        }
                    }
                    chunk.Completed = true;
                    SaveMeta(state);
                    return;
                }
                catch (SysOpCanceled) { throw; }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    Android.Util.Log.Warn("DownloadEngine", $"分片 {chunk.Index} 重试 {attempt + 1}: {ex.Message}");
                    await Task.Delay(500 * (attempt + 1), state.CancelToken.Token);
                }
            }
            throw new Exception($"分片 {chunk.Index} 下载失败，已达最大重试次数");
        }

        private List<ChunkInfo> CreateChunks(long totalSize)
        {
            var chunks = new List<ChunkInfo>();
            long start = 0;
            int index = 0;
            while (start < totalSize)
            {
                long end = Math.Min(start + _chunkSize - 1, totalSize - 1);
                chunks.Add(new ChunkInfo
                {
                    Index = index++,
                    Start = start,
                    End = end,
                    Completed = false
                });
                start = end + 1;
            }
            return chunks;
        }

        private void UpdateProgressFromState(DownloadState state)
        {
            long total = state.TotalBytes;
            long downloaded = state.DownloadedBytes;
            int progress = total > 0 ? (int)Math.Min(99, downloaded * 100 / total) : 99;
            UpdateProgress(state, progress);
        }

        private void UpdateProgress(DownloadState state, int progress)
        {
            state.Progress = progress;
            OnProgress?.Invoke(state.TaskId, progress);
            ShowNotification(state, progress, $"正在下载... {progress}%");
        }

        private void UpdateStatus(DownloadState state, string status)
        {
            state.Status = status;
            OnStatus?.Invoke(state.TaskId, status);
        }

        private void SaveMeta(DownloadState state)
        {
            try
            {
                var meta = new DownloadMeta
                {
                    TaskId = state.TaskId,
                    Url = state.Url,
                    OutputPath = state.OutputPath,
                    TotalBytes = state.TotalBytes,
                    DownloadedBytes = state.DownloadedBytes,
                    SupportsRange = state.SupportsRange,
                    Chunks = state.Chunks,
                    LastUpdated = DateTime.UtcNow
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(meta);
                File.WriteAllText(state.MetaFile, json);
            }
            catch { }
        }

        private void LoadMeta(DownloadState state)
        {
            try
            {
                var json = File.ReadAllText(state.MetaFile);
                var meta = Newtonsoft.Json.JsonConvert.DeserializeObject<DownloadMeta>(json);
                if (meta != null && meta.Url == state.Url)
                {
                    state.TotalBytes = meta.TotalBytes;
                    state.DownloadedBytes = meta.DownloadedBytes;
                    state.SupportsRange = meta.SupportsRange;
                    state.Chunks = meta.Chunks ?? new List<ChunkInfo>();
                    return;
                }
            }
            catch { }
            // 加载失败，重新初始化
            state.DownloadedBytes = 0;
            state.Chunks = null;
        }

        private void CleanupTemp(DownloadState state)
        {
            try
            {
                if (File.Exists(state.MetaFile)) File.Delete(state.MetaFile);
                if (Directory.Exists(state.TempDir)) Directory.Delete(state.TempDir, true);
            }
            catch { }
        }

        private void ShowNotification(DownloadState state, int progress, string text)
        {
            try
            {
                var channelId = "mchat_download";
                var mgr = (NotificationManager)_ctx.GetSystemService(Context.NotificationService);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var channel = new NotificationChannel(channelId, L10n.T("下载管理"), NotificationImportance.Low);
                    channel.Description = L10n.T("文件下载进度通知");
                    mgr.CreateNotificationChannel(channel);
                }

                var builder = new NotificationCompat.Builder(_ctx, channelId)
                    .SetContentTitle(state.Title)
                    .SetContentText(text)
                    .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
                    .SetProgress(100, progress, progress < 0)
                    .SetOngoing(progress < 100)
                    .SetAutoCancel(progress >= 100)
                    .SetOnlyAlertOnce(true);

                if (progress >= 100)
                {
                    builder.SetContentText(L10n.T("下载完成，点击打开"))
                           .SetContentIntent(PendingIntent.GetActivity(_ctx, state.TaskId.GetHashCode(),
                               new Intent(Intent.ActionView)
                                   .SetDataAndType(Android.Net.Uri.FromFile(new Java.IO.File(state.OutputPath)), GetMimeType(state.OutputPath))
                                   .AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission),
                               PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
                }

                mgr.Notify(state.TaskId.GetHashCode(), builder.Build());
            }
            catch { }
        }

        private string GetMimeType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".mp4":
                case ".mkv":
                case ".mov":
                case ".webm": return "video/*";
                case ".mp3":
                case ".flac":
                case ".m4a":
                case ".wav":
                case ".ogg": return "audio/*";
                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".webp":
                case ".gif": return "image/*";
                case ".pdf": return "application/pdf";
                case ".epub": return "application/epub+zip";
                case ".txt": return "text/plain";
                default: return "application/octet-stream";
            }
        }

        private static bool IsArchive(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".zip" || ext == ".rar" || ext == ".7z" || ext == ".tar" || ext == ".gz" || ext == ".bz2" || ext == ".xz";
        }

        private async Task ExtractArchiveAsync(string archivePath)
        {
            try
            {
                var extractDir = Path.Combine(Path.GetDirectoryName(archivePath), Path.GetFileNameWithoutExtension(archivePath) + "_extracted");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                Directory.CreateDirectory(extractDir);

                var ext = Path.GetExtension(archivePath).ToLowerInvariant();
                if (ext == ".zip")
                {
                    ZipFile.ExtractToDirectory(archivePath, extractDir);
                }
                else if (ext == ".tar" || ext == ".gz" || ext == ".bz2" || ext == ".xz")
                {
                    // 简易 tar 支持：需要第三方库，这里跳过
                    Android.Util.Log.Info("DownloadEngine", "tar/gz/bz2/xz 需额外库，暂不支持");
                }
                else
                {
                    Android.Util.Log.Info("DownloadEngine", $"不支持的压缩格式: {ext}");
                    return;
                }

                // 扫描解压后的文件
                MediaScanner.ScanDirectory(_ctx, extractDir);
                
                // 可选：通知用户解压完成
                Android.Util.Log.Info("DownloadEngine", $"已解压到: {extractDir}");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("DownloadEngine", $"解压失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 下载任务运行时状态（内存态）。
    /// </summary>
    public class DownloadState
    {
        public string TaskId { get; set; }
        public string Url { get; set; }
        public string OutputPath { get; set; }
        public string Title { get; set; }
        public string Type { get; set; } // video|music|file|novel
        public string Status { get; set; } = "queued"; // queued|downloading|merging|done|failed|canceled
        public int Progress { get; set; }
        public string Error { get; set; }
        public long TotalBytes { get; set; } = -1;
        public long DownloadedBytes; // 字段，供 Interlocked.Add 使用
        public bool SupportsRange { get; set; }
        public List<ChunkInfo> Chunks { get; set; }
        public string TempDir { get; set; }
        public string PartFile { get; set; }
        public string MetaFile { get; set; }
        public CancellationTokenSource CancelToken { get; set; } = new CancellationTokenSource();
    }

    /// <summary>
    /// 分片信息。
    /// </summary>
    public class ChunkInfo
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public bool Completed { get; set; }
    }

    /// <summary>
    /// 持久化元数据（.meta 文件）。
    /// </summary>
    public class DownloadMeta
    {
        public string TaskId { get; set; }
        public string Url { get; set; }
        public string OutputPath { get; set; }
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public bool SupportsRange { get; set; }
        public List<ChunkInfo> Chunks { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// 媒体库扫描工具。
    /// </summary>
    public static class MediaScanner
    {
        public static void ScanFile(Context ctx, string filePath, string mimeType = null)
        {
            if (ctx == null || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                var uri = Android.Net.Uri.FromFile(new Java.IO.File(filePath));
                var conn = new MediaScannerConnection(ctx, new MediaScannerCallback());
                conn.Connect();
                // MediaScannerConnection 没有直接 scanFile 的静态方法，用回调方式
                // 这里用简单的广播方式触发系统扫描
                var intent = new Intent(Intent.ActionMediaScannerScanFile);
                intent.SetData(uri);
                ctx.SendBroadcast(intent);
            }
            catch { }
        }

        public static void ScanDirectory(Context ctx, string dirPath)
        {
            if (ctx == null || string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return;
            try
            {
                var uri = Android.Net.Uri.FromFile(new Java.IO.File(dirPath));
                var intent = new Intent(Intent.ActionMediaScannerScanFile);
                intent.SetData(uri);
                ctx.SendBroadcast(intent);
            }
            catch { }
        }

        private class MediaScannerCallback : Java.Lang.Object, MediaScannerConnection.IMediaScannerConnectionClient
        {
            public void OnMediaScannerConnected()
            {
            }

            public void OnScanCompleted(string path, Android.Net.Uri uri)
            {
            }
        }
    }
}
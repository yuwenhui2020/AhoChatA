// ============================================================
// 影音 · 本地回环缓存代理（127.0.0.1:8891）
//
// 这是「多线程缓冲防卡顿 + 右上角遥测 + 暂停也缓冲 + 3 天过期」的核心。
// 设计要点：
//   1) 把远端直链（mp4 渐进流 / HLS 切片）转成本机 HTTP 流，ExoPlayer 走 127.0.0.1 回环读取；
//   2) 代理对资源做多线程分块下载并落盘（CacheDir/vod_disk），播放与暂停都持续预取；
//   3) 因为字节真实落盘，遥测可以给出「精确已下载字节数」——配合 ExoPlayer 的
//      缓冲时间轴（BufferedPosition - CurrentPosition）即可显示「已缓冲 Xs · Y MB」；
//   4) 缓冲文件 3 天过期，启动时空扫一次过期文件。
//
// 说明：ExoPlayer 2.16.1 的 Xamarin 绑定未暴露 SimpleCache / CacheDataSource，无法把预取字节
//       直接喂进播放器缓存；用本机代理做「真实落盘 + 精确统计」是当前最稳的方案。
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Util;
using IoPath = System.IO.Path;

namespace AhoChatA.Vod
{
    public sealed class VideoCacheProxy
    {
        public const int Port = 8891;
        public const int CacheExpiryDays = 3;
        public static readonly VideoCacheProxy Instance = new VideoCacheProxy();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>();
        private readonly ConcurrentDictionary<string, List<string>> _manifestChildren =
            new ConcurrentDictionary<string, List<string>>();
        private HttpClient _http;

        public bool Enabled { get; private set; } = true;

        private HttpClient Http
        {
            get
            {
                if (_http != null) return _http;
                lock (_lock)
                {
                    if (_http != null) return _http;
                    var handler = new HttpClientHandler
                    {
                        AllowAutoRedirect = false,   // 手动跟随，保留 Range 头
                        AutomaticDecompression = DecompressionMethods.None,
                        UseProxy = false,
                        UseCookies = false,
                    };
                    var hc = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                    hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", VodExoCache.Ua);
                    _http = hc;
                    return _http;
                }
            }
        }

        private VideoCacheProxy() { }

        /* ---------------- 生命周期 ---------------- */

        public void Start()
        {
            if (_running) return;
            lock (_lock)
            {
                if (_running) return;
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                    l.Start();
                    _listener = l;
                    _cts = new CancellationTokenSource();
                    _running = true;
                    Enabled = true;
                    Task.Run(() => LoopAsync(_cts.Token));
                    Log.Info("MchatVod", "VideoCacheProxy 已在 127.0.0.1:" + Port + " 启动");
                }
                catch (Exception ex)
                {
                    Enabled = false;
                    _running = false;
                    Log.Error("MchatVod", "VideoCacheProxy 启动失败（已降级为直连）：" + ex.Message);
                }
            }
        }

        public void EnsureStarted()
        {
            if (_running) return;
            try { Task.Run(() => Start()); } catch { }
        }

        public void Stop()
        {
            try { _running = false; _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }

        /// <summary>把远端直链包装成本机回环播放地址（ExoPlayer 读取它即可享受本地缓存 + 遥测）。</summary>
        public static string ProxyPlayUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            Instance.EnsureStarted();
            return "http://127.0.0.1:" + Port + "/p?url=" + Uri.EscapeDataString(url);
        }

        /* ---------------- 主循环 ---------------- */

        private async Task LoopAsync(CancellationToken token)
        {
            while (_running && !token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch
                {
                    if (!_running || token.IsCancellationRequested) break;
                    await Task.Delay(200);
                    continue;
                }
                _ = Task.Run(() => SafeHandle(ctx));
            }
        }

        private void SafeHandle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath;
                if (path == "/p") HandlePlay(ctx);
                else if (path == "/s") HandleSegment(ctx);
                else if (path == "/status") HandleStatus(ctx);
                else if (path == "/health") WriteText(ctx, "ok", "text/plain");
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }
            catch (Exception ex)
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                Log.Debug("MchatVod", "proxy handle err " + ex.Message);
            }
        }

        /* ---------------- 路由 ---------------- */

        // /p?url=... ：播放入口。m3u8 改写后返回；普通文件 302 到 /s（由 ExoPlayer 跟随）。
        private void HandlePlay(HttpListenerContext ctx)
        {
            string url = CleanUrl(ctx.Request.QueryString["url"]);
            if (string.IsNullOrEmpty(url)) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }
            bool isM3u8 = url.ToLowerInvariant().Contains(".m3u8");
            if (isM3u8)
            {
                string text = HttpGetString(url);
                if (text == null) { ctx.Response.StatusCode = 502; ctx.Response.Close(); return; }
                string rewritten = RewriteManifest(url, text, out var kids);
                RecordManifest(url, kids);
                WriteText(ctx, rewritten, "application/vnd.apple.mpegurl; charset=utf-8");
            }
            else
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers["Location"] = "/s?url=" + Uri.EscapeDataString(url);
                ctx.Response.Close();
            }
        }

        // /s?url=... ：单资源（mp4 / ts 切片 / 嵌套 m3u8），支持 Range/206 + 落盘缓存。
        private void HandleSegment(HttpListenerContext ctx)
        {
            string url = CleanUrl(ctx.Request.QueryString["url"]);
            if (string.IsNullOrEmpty(url)) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }
            var entry = GetEntry(url);
            if (entry == null) { ctx.Response.StatusCode = 502; ctx.Response.Close(); return; }

            if (entry.IsManifest)
            {
                string text = HttpGetString(url);
                if (text == null) { ctx.Response.StatusCode = 502; ctx.Response.Close(); return; }
                string rewritten = RewriteManifest(url, text, out var kids);
                RecordManifest(url, kids);
                WriteText(ctx, rewritten, "application/vnd.apple.mpegurl; charset=utf-8");
                return;
            }
            if (entry.StreamMode)
            {
                ServeStream(ctx, entry, url);
                return;
            }
            ServeRange(ctx, entry, url);
        }

        // /status?url=... ：遥测。返回 {length, cachedBytes}（m3u8 会递归统计其全部切片）。
        private void HandleStatus(HttpListenerContext ctx)
        {
            string url = CleanUrl(ctx.Request.QueryString["url"]);
            long total = string.IsNullOrEmpty(url) ? 0 : CachedBytesRecursive(url);
            long len = string.IsNullOrEmpty(url) ? 0 : LengthOf(url);
            WriteJson(ctx, "{\"length\":" + len + ",\"cachedBytes\":" + total + "}");
        }

        /* ---------------- 入口 / 元数据 ---------------- */

        private Entry GetEntry(string url)
        {
            string key = Md5(url);
            if (_entries.TryGetValue(key, out var e)) return e;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out e)) return e;
                e = ProbeEntry(url, key);
                if (e != null) _entries[key] = e;
                return e;
            }
        }

        private Entry ProbeEntry(string url, string key)
        {
            var dataPath = IoPath.Combine(VodConfig.DiskCacheRoot(), key + ".data");
            var metaPath = IoPath.Combine(VodConfig.DiskCacheRoot(), key + ".meta");

            // 已完整落盘（之前看过）→ 直接复用，不必再联网探测
            if (File.Exists(dataPath) && File.Exists(metaPath))
            {
                try
                {
                    var m = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(metaPath));
                    long len = (long)m["length"];
                    var e0 = new Entry
                    {
                        Url = url,
                        Key = key,
                        DataPath = dataPath,
                        MetaPath = metaPath,
                        Length = len,
                        ContentType = (string)m["ct"] ?? "application/octet-stream",
                        SupportsRange = true,
                        IsManifest = false,
                        Complete = true,
                        StreamMode = false,
                        CachedBytes = new FileInfo(dataPath).Length,
                        ChunkSize = 1 << 20,
                    };
                    e0.ChunkCount = len > 0 ? (int)((len + e0.ChunkSize - 1) / e0.ChunkSize) : 1;
                    e0.Done = new bool[e0.ChunkCount];
                    e0.Inflight = new bool[e0.ChunkCount];
                    for (int i = 0; i < e0.ChunkCount; i++) e0.Done[i] = true;
                    e0.DoneCount = e0.ChunkCount;
                    e0.Target = len;
                    return e0;
                }
                catch { }
            }

            var probe = Probe(url);
            if (probe == null) return null;
            var e = new Entry
            {
                Url = url,
                Key = key,
                DataPath = dataPath,
                MetaPath = metaPath,
                Length = probe.Length,
                ContentType = probe.ContentType ?? "application/octet-stream",
                SupportsRange = probe.SupportsRange,
                IsManifest = probe.IsManifest,
                StreamMode = !probe.IsManifest && (!probe.SupportsRange || probe.Length <= 0),
                Complete = false,
                ChunkSize = 1 << 20,
            };
            if (!e.StreamMode)
            {
                e.ChunkCount = (int)((e.Length + e.ChunkSize - 1) / e.ChunkSize);
                e.Done = new bool[e.ChunkCount];
                e.Inflight = new bool[e.ChunkCount];
                e.Target = Math.Min(e.Length, InitialWindow);
                StartWorkers(e);
            }
            try { File.WriteAllText(metaPath, Newtonsoft.Json.JsonConvert.SerializeObject(
                new { length = e.Length, ct = e.ContentType, manifest = e.IsManifest })); } catch { }
            return e;
        }

        private const long InitialWindow = 64 * 1024 * 1024;   // 起播先预取前 64MB
        private const long AheadWindow = 32 * 1024 * 1024;     // 播放位置再往前 32MB
        private const int PrefetchThreads = 4;

        private void StartWorkers(Entry e)
        {
            if (e.WorkersStarted) return;
            e.WorkersStarted = true;
            for (int i = 0; i < PrefetchThreads; i++)
            {
                var ent = e;
                Task.Run(() =>
                {
                    try { WorkerLoop(ent); }
                    catch (Exception ex) { Log.Debug("MchatVod", "prefetch worker err " + ex.Message); }
                });
            }
        }

        private void WorkerLoop(Entry e)
        {
            while (_running)
            {
                int idx = -1;
                lock (e.Lock)
                {
                    while (e.FillPtr < e.ChunkCount && e.Done[e.FillPtr]) e.FillPtr++;
                    if (e.FillPtr < e.ChunkCount && (long)e.FillPtr * e.ChunkSize < e.Target)
                    {
                        idx = e.FillPtr;
                        e.FillPtr++;
                        e.Inflight[idx] = true;
                    }
                }
                if (idx < 0)
                {
                    bool allDone = e.DoneCount >= e.ChunkCount;
                    if (allDone && e.Target >= e.Length) break;
                    Thread.Sleep(150);
                    continue;
                }
                try { DownloadChunkNow(e, idx); }
                catch
                {
                    lock (e.Lock) { e.Inflight[idx] = false; }
                }
            }
        }

        /// <summary>播放指针推进时调用：把预取窗口前移，保证始终有 32MB 缓冲在手。</summary>
        private void ExtendTarget(Entry e, long offset)
        {
            if (e.StreamMode || e.Length <= 0) return;
            long want = Math.Min(e.Length, offset + AheadWindow);
            lock (e.Lock) { if (want > e.Target) e.Target = want; }
        }

        private bool DownloadChunkNow(Entry e, int idx)
        {
            long start = (long)idx * e.ChunkSize;
            long end = Math.Min(e.Length - 1, start + e.ChunkSize - 1);
            byte[] data = HttpRangeGet(e.Url, start, end);
            if (data == null || data.Length == 0) return false;
            lock (e.IoLock)
            {
                using (var fs = new FileStream(e.DataPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    fs.Write(data, 0, data.Length);
                }
            }
            Touch(e.DataPath);
            lock (e.Lock)
            {
                if (!e.Done[idx])
                {
                    e.Done[idx] = true;
                    e.Inflight[idx] = false;
                    e.CachedBytes += data.Length;
                    e.DoneCount++;
                    Monitor.PulseAll(e.Lock);
                }
            }
            return true;
        }

        /// <summary>确保某分块已落盘；未下载则就地按需拉取（防止播放卡在还没预取到的位置）。</summary>
        private bool EnsureChunk(Entry e, int idx)
        {
            lock (e.Lock)
            {
                if (e.Done[idx]) return true;
                if (!e.Inflight[idx])
                {
                    e.Inflight[idx] = true;
                }
                else
                {
                    while (!e.Done[idx]) Monitor.Wait(e.Lock, 30000);
                    return e.Done[idx];
                }
            }
            bool ok = false;
            try { ok = DownloadChunkNow(e, idx); } catch { }
            if (!ok) { lock (e.Lock) { e.Inflight[idx] = false; } }
            return ok;
        }

        /* ---------------- 响应：Range 模式 ---------------- */

        private void ServeRange(HttpListenerContext ctx, Entry e, string url)
        {
            long len = e.Length;
            long a, b;
            ParseRange(ctx.Request.Headers["Range"], len, out a, out b);
            if (b < a || a < 0 || b >= len) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }

            ExtendTarget(e, a);

            long total = b - a + 1;
            ctx.Response.StatusCode = 206;
            ctx.Response.ContentType = e.ContentType ?? "application/octet-stream";
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["Content-Range"] = "bytes " + a + "-" + b + "/" + len;
            ctx.Response.ContentLength64 = total;

            try
            {
                using (var os = ctx.Response.OutputStream)
                {
                    byte[] buf = new byte[65536];
                    long pos = a;
                    while (pos <= b)
                    {
                        int idx = (int)(pos / e.ChunkSize);
                        if (idx >= e.ChunkCount) break;
                        EnsureChunk(e, idx);
                        long cs = (long)idx * e.ChunkSize;
                        long ce = Math.Min(len - 1, cs + e.ChunkSize - 1);
                        long segStart = Math.Max(a, cs);
                        long segEnd = Math.Min(b, ce);
                        int toRead = (int)(segEnd - segStart + 1);
                        byte[] seg;
                        lock (e.IoLock)
                        {
                            using (var fs = new FileStream(e.DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                fs.Seek(segStart, SeekOrigin.Begin);
                                seg = new byte[toRead];
                                int got = 0;
                                while (got < toRead)
                                {
                                    int n = fs.Read(seg, got, toRead - got);
                                    if (n <= 0) break;
                                    got += n;
                                }
                                if (got < toRead) Array.Resize(ref seg, got);
                            }
                        }
                        if (seg.Length == 0) break;
                        os.Write(seg, 0, seg.Length);
                        pos = ce + 1;
                    }
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        /* ---------------- 响应：流式模式（不支持 Range 的兜底） ---------------- */

        private void ServeStream(HttpListenerContext ctx, Entry e, string url)
        {
            // 已完整下载过 → 直接从盘读（支持 Range）
            if (e.Complete && File.Exists(e.DataPath))
            {
                ServeFileFromDisk(ctx, e.DataPath, e.ContentType);
                return;
            }
            ctx.Response.ContentType = e.ContentType ?? "video/mp4";
            try
            {
                using (var os = ctx.Response.OutputStream)
                using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                using (var rs = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var fs = new FileStream(e.DataPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        os.Write(buf, 0, n);
                        e.CachedBytes += n;
                    }
                }
                e.Complete = true;
                Touch(e.DataPath);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private void ServeFileFromDisk(HttpListenerContext ctx, string path, string ct)
        {
            long len = new FileInfo(path).Length;
            long a, b;
            ParseRange(ctx.Request.Headers["Range"], len, out a, out b);
            if (b < a || a < 0 || b >= len) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
            if (a == 0 && b == len - 1)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = ct ?? "application/octet-stream";
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                ctx.Response.ContentLength64 = len;
            }
            else
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.ContentType = ct ?? "application/octet-stream";
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                ctx.Response.Headers["Content-Range"] = "bytes " + a + "-" + b + "/" + len;
                ctx.Response.ContentLength64 = b - a + 1;
            }
            try
            {
                using (var os = ctx.Response.OutputStream)
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(a, SeekOrigin.Begin);
                    long remaining = b - a + 1;
                    byte[] buf = new byte[65536];
                    while (remaining > 0)
                    {
                        int to = (int)Math.Min(buf.Length, remaining);
                        int n = fs.Read(buf, 0, to);
                        if (n <= 0) break;
                        os.Write(buf, 0, n);
                        remaining -= n;
                    }
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        /* ---------------- 探测 / 抓取 ---------------- */

        private class ProbeResult
        {
            public long Length;
            public bool SupportsRange;
            public string ContentType;
            public bool IsManifest;
        }

        private ProbeResult Probe(string url)
        {
            try
            {
                var resp = SendRange(url, 0, 0);
                if (resp == null) return null;
                var pr = new ProbeResult();
                pr.ContentType = resp.Content?.Headers?.ContentType?.MediaType ?? "";
                if (resp.StatusCode == HttpStatusCode.PartialContent)   // 206
                {
                    pr.SupportsRange = true;
                    long rangeLen = ParseContentRangeLength(resp.Headers);
                    pr.Length = rangeLen >= 0 ? rangeLen : -1;
                }
                else
                {
                    pr.SupportsRange = false;
                    if (resp.Content?.Headers?.ContentLength != null)
                        pr.Length = resp.Content.Headers.ContentLength.Value;
                    else pr.Length = -1;
                }
                pr.IsManifest = pr.ContentType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0
                                || url.ToLowerInvariant().Contains(".m3u8");
                resp.Dispose();
                return pr;
            }
            catch { return null; }
        }

        /// <summary>Xamarin 的 HttpResponseHeaders 未暴露 ContentRange 属性，这里从原始头串解析总长（bytes a-b/LENGTH 末尾）。</summary>
        private static long ParseContentRangeLength(HttpResponseHeaders headers)
        {
            if (headers == null) return -1;
            if (headers.TryGetValues("Content-Range", out var vals))
            {
                foreach (var v in vals)
                {
                    var m = Regex.Match(v ?? "", @"\/\s*(\d+)\s*$");
                    if (m.Success && long.TryParse(m.Groups[1].Value, out var len)) return len;
                }
            }
            return -1;
        }

        /// <summary>带手动重定向跟随的 Range GET（始终带上 Range 头，避免 CDN 跳转后丢掉范围）。</summary>
        private HttpResponseMessage SendRange(string url, long from, long to)
        {
            for (int hop = 0; hop < 6; hop++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(from, to);
                var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                var sc = (int)resp.StatusCode;
                if (sc >= 300 && sc < 400 && resp.Headers.Location != null)
                {
                    url = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.AbsoluteUri
                                                              : new Uri(new Uri(url), resp.Headers.Location.OriginalString).AbsoluteUri;
                    resp.Dispose();
                    continue;
                }
                return resp;
            }
            return null;
        }

        private byte[] HttpRangeGet(string url, long from, long to)
        {
            var resp = SendRange(url, from, to);
            if (resp == null) return null;
            try
            {
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                // 个别 CDN 对 Range 返回 200 整文件：切片出需要的区间
                if (resp.StatusCode != HttpStatusCode.PartialContent && bytes.Length > (to - from + 1))
                {
                    long take = to - from + 1;
                    if (take <= 0 || take > bytes.Length) return bytes;
                    var slice = new byte[take];
                    Array.Copy(bytes, from, slice, 0, take);
                    return slice;
                }
                return bytes;
            }
            catch { return null; }
            finally { try { resp.Dispose(); } catch { } }
        }

        private string HttpGetString(string url)
        {
            try
            {
                var resp = Http.GetAsync(url).GetAwaiter().GetResult();
                var s = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                resp.Dispose();
                return s;
            }
            catch { return null; }
        }

        /* ---------------- m3u8 改写 ---------------- */

        private string RewriteManifest(string baseUrl, string text, out List<string> children)
        {
            children = new List<string>();
            var sb = new StringBuilder();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("#"))
                {
                if (line.IndexOf("URI=") >= 0)
                {
                    var cap1 = new List<string>();
                    line = Regex.Replace(line, "URI=\"([^\"]*)\"", m =>
                    {
                        var v = m.Groups[1].Value;
                        if (v.StartsWith("/s?")) return m.Value;
                        var abs = Resolve(baseUrl, v);
                        cap1.Add(abs);
                        return "URI=\"/s?url=" + Uri.EscapeDataString(abs) + "\"";
                    });
                    children.AddRange(cap1);
                    var cap2 = new List<string>();
                    line = Regex.Replace(line, "URI='([^']*)'", m =>
                    {
                        var v = m.Groups[1].Value;
                        if (v.StartsWith("/s?")) return m.Value;
                        var abs = Resolve(baseUrl, v);
                        cap2.Add(abs);
                        return "URI='/s?url=" + Uri.EscapeDataString(abs) + "'";
                    });
                    children.AddRange(cap2);
                }
                    sb.Append(line).Append('\n');
                }
                else if (line.Length == 0)
                {
                    sb.Append('\n');
                }
                else
                {
                    var abs = Resolve(baseUrl, line.Trim());
                    children.Add(abs);
                    sb.Append("/s?url=").Append(Uri.EscapeDataString(abs)).Append('\n');
                }
            }
            return sb.ToString();
        }

        private void RecordManifest(string url, List<string> children)
        {
            if (children == null || children.Count == 0) return;
            _manifestChildren[url] = children;
        }

        /// <summary>把 m3u8 里的相对地址解析成绝对地址（与 VodExoCache.Resolve 同逻辑）。</summary>
        private static string Resolve(string baseUrl, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;
            if (rel.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return rel;
            try
            {
                int schemeIdx = baseUrl.IndexOf("://", StringComparison.Ordinal);
                if (schemeIdx < 0) return rel;
                int hostStart = schemeIdx + 3;
                int slash = baseUrl.IndexOf('/', hostStart);
                string origin = slash < 0 ? baseUrl : baseUrl.Substring(0, slash);
                if (rel.StartsWith("//")) return baseUrl.Substring(0, hostStart) + rel.Substring(2);
                if (rel.StartsWith("/")) return origin + rel;
                int q = baseUrl.IndexOf('?');
                string noQuery = q >= 0 ? baseUrl.Substring(0, q) : baseUrl;
                int lastSlash = noQuery.LastIndexOf('/');
                return noQuery.Substring(0, lastSlash + 1) + rel;
            }
            catch { return rel; }
        }

        /* ---------------- 遥测 ---------------- */

        /// <summary>给播放页用的：当前资源（含其 m3u8 全部切片）已落盘的总字节数。</summary>
        public long CachedBytesFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            return CachedBytesRecursive(url);
        }

        private long CachedBytesRecursive(string url)
        {
            long total = 0;
            var e = GetEntryForTelemetry(url);
            if (e != null) total += Math.Max(0, e.CachedBytes);
            if (_manifestChildren.TryGetValue(url, out var kids))
            {
                foreach (var k in kids) total += CachedBytesRecursive(k);
            }
            return total;
        }

        private long LengthOf(string url)
        {
            var e = GetEntryForTelemetry(url);
            return e != null && e.Length > 0 ? e.Length : 0;
        }

        private Entry GetEntryForTelemetry(string url)
        {
            string key = Md5(url);
            if (_entries.TryGetValue(key, out var e)) return e;
            // 即便还没被播放请求过，也尝试从已落盘文件读大小
            var dataPath = IoPath.Combine(VodConfig.DiskCacheRoot(), key + ".data");
            if (File.Exists(dataPath)) return new Entry { CachedBytes = new FileInfo(dataPath).Length };
            return null;
        }

        /* ---------------- 工具 ---------------- */

        private static void ParseRange(string rangeHeader, long len, out long a, out long b)
        {
            a = 0; b = len - 1;
            if (string.IsNullOrEmpty(rangeHeader)) return;
            // 形如 bytes=0-1023 或 bytes=1024- 或 bytes=-512
            var m = Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
            if (!m.Success) return;
            string s1 = m.Groups[1].Value, s2 = m.Groups[2].Value;
            if (s1.Length > 0) a = long.Parse(s1);
            if (s2.Length > 0) b = long.Parse(s2);
            else b = len - 1;
            if (a < 0) a = 0;
            if (b >= len) b = len - 1;
        }

        private static string CleanUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            // HttpListener 已做 URL 解码；这里再保险去掉首尾空白
            return raw.Trim();
        }

        private static void Touch(string path)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
        }

        private static string Md5(string s)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var b = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                    var sb = new StringBuilder();
                    foreach (var x in b) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return Math.Abs((s ?? "").GetHashCode()).ToString("x8"); }
        }

        private static void WriteText(HttpListenerContext ctx, string text, string ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = ct;
            ctx.Response.ContentLength64 = bytes.Length;
            using (var os = ctx.Response.OutputStream) os.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static void WriteJson(HttpListenerContext ctx, string json)
        {
            WriteText(ctx, json, "application/json; charset=utf-8");
        }

        /* ---------------- 缓存条目 ---------------- */

        private class Entry
        {
            public string Url;
            public string Key;
            public string DataPath;
            public string MetaPath;
            public long Length = -1;
            public bool SupportsRange;
            public string ContentType;
            public bool IsManifest;
            public bool Complete;
            public bool StreamMode;
            public long CachedBytes;
            public int ChunkSize = 1 << 20;
            public int ChunkCount;
            public bool[] Done;
            public bool[] Inflight;
            public int FillPtr;
            public int DoneCount;
            public long Target;
            public bool WorkersStarted;
            public readonly object Lock = new object();
            public readonly object IoLock = new object();
        }
    }
}

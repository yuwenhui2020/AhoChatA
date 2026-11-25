// ============================================================
// 在线影视 · ExoPlayer 网络数据源工厂 + HLS 切片多线程预取（参考 C:\root\code\TV 的 Prefetcher）
//
// 关键点（这也是「黑屏一直加载」的真正修复）：
//   · HTTP 数据源强制浏览器 User-Agent（很多采集站/CDN 校验 UA，否则 403 黑屏）；
//   · 允许跨协议重定向（http↔https），采集站直链常常跳转；
//   · 起播/囤积缓冲交给 ExoPlayer 的 DefaultLoadControl（1.5s 起播、前瞻囤到 240s、回看 60s）。
//
// HLS 切片预取：播放/切集时后台解析 m3u8，把最前面的若干 .ts 切片用多线程并发下载到
//   CacheDir/vod_buf/seg（非持久区，进程启动即清），并逐片统计「成功/失败、耗时 ms、大小 KB」，
//   类似 TV 的 Prefetcher。并发预取能提前把 CDN 边缘内容拉到本地并预热连接，降低顺序播放卡顿概率。
//
// 说明：本项目是经典 Xamarin.Android（monoandroid13.0），ExoPlayer 2.16.1 绑定未暴露
//       SimpleCache / CacheDataSource.Factory.SetCache / BaseDataSource，无法把预取字节直接喂进
//       播放器缓存；故预取以「预热 + 落盘 + 统计」形式实现，播放本身走 ExoPlayer 内存前瞻缓冲。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Com.Google.Android.Exoplayer2.Upstream;

namespace AhoChatA.Vod
{
    public static class VodExoCache
    {
        const int HttpConnectMs = 12000;
        const int HttpReadMs = 15000;
        // 切片预取：前 N 片，并发 M 路（与 TV 的经验值一致，避免抢带宽拖慢起播）
        const int SegPrefetchCount = 6;
        const int SegConcurrency = 4;
        // 与 TV 一致：伪装成浏览器，否则很多采集站 CDN 直接 403
        public const string Ua = "Mozilla/5.0 (Linux; Android 13; TVPlayer) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";

        static readonly object _lock = new object();
        static HttpClient _http;
        static string _lastPrefetchUrl = "";

        /* ---------------- ExoPlayer 数据源 ---------------- */

        /// <summary>带浏览器 UA + 跨协议重定向的 HTTP 数据源工厂（在线流 / 本地代理流共用）。</summary>
        public static DefaultHttpDataSource.Factory HttpFactory()
        {
            return new DefaultHttpDataSource.Factory()
                .SetConnectTimeoutMs(HttpConnectMs)
                .SetReadTimeoutMs(HttpReadMs)
                .SetAllowCrossProtocolRedirects(true)
                .SetUserAgent(Ua);
        }

        /// <summary>预取专用 HttpClient：仅浏览器 UA（不设 Referer，和 ExoPlayer 自身行为一致）。</summary>
        static HttpClient Http
        {
            get
            {
                if (_http != null) return _http;
                lock (_lock)
                {
                    if (_http != null) return _http;
                    var handler = new HttpClientHandler
                    {
                        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                        UseCookies = true,
                        UseProxy = false,
                    };
                    var hc = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                    hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
                    _http = hc;
                    return _http;
                }
            }
        }

        /// <summary>播放 / 切集时调用：后台多线程预取 HLS 前几个切片并统计耗时与大小。</summary>
        public static void WarmUp(Context ctx, string url) => Prefetch(url);

        public static void Prefetch(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) < 0) return;  // 只有 HLS 有切片
            lock (_lock)
            {
                if (_lastPrefetchUrl == url) return;   // 同一条不重复预取
                _lastPrefetchUrl = url;
            }
            Task.Run(() =>
            {
                try { PrefetchCore(url); }
                catch (Exception e) { Android.Util.Log.Debug("MchatVod", "prefetch err " + e.Message); }
            });
        }

        static void PrefetchCore(string url)
        {
            var dir = System.IO.Path.Combine(Application.Context.CacheDir.AbsolutePath, "vod_buf", "seg");
            try { Directory.CreateDirectory(dir); } catch { }

            string playlist;
            try { using (var cts = new CancellationTokenSource(12000)) playlist = Http.GetStringAsync(url).GetAwaiter().GetResult(); }
            catch (Exception e) { Android.Util.Log.Debug("MchatVod", "prefetch playlist err " + e.Message); return; }
            if (string.IsNullOrEmpty(playlist)) return;

            // 解析顺序切片地址
            var segs = new List<string>();
            foreach (var raw in playlist.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                segs.Add(Resolve(url, line));
                if (segs.Count >= SegPrefetchCount) break;
            }
            if (segs.Count == 0) return;

            var results = new SegStat[segs.Count];
            var sem = new SemaphoreSlim(SegConcurrency);
            var tasks = new List<Task>(segs.Count);
            for (int i = 0; i < segs.Count; i++)
            {
                int idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync().ConfigureAwait(false);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(segs[idx]).ConfigureAwait(false);
                        sw.Stop();
                        results[idx] = new SegStat { Ok = bytes != null && bytes.Length > 0, Ms = sw.ElapsedMilliseconds, Bytes = bytes == null ? 0 : bytes.Length };
                        if (results[idx].Ok)
                        {
                            try { File.WriteAllBytes(System.IO.Path.Combine(dir, "seg_" + idx + ".ts"), bytes); } catch { }
                        }
                    }
                    catch
                    {
                        sw.Stop();
                        results[idx] = new SegStat { Ok = false, Ms = sw.ElapsedMilliseconds, Bytes = 0 };
                    }
                    finally { sem.Release(); }
                }));
            }
            try { Task.WhenAll(tasks).Wait(20000); } catch { }

            var sb = new StringBuilder();
            int ok = 0; long totalMs = 0; int totalBytes = 0;
            for (int i = 0; i < results.Length; i++)
            {
                var r = results[i];
                if (r.Ok) { ok++; totalMs += r.Ms; totalBytes += r.Bytes; }
                sb.Append('#').Append(i).Append(r.Ok ? " ok " : " FAIL ")
                  .Append(r.Ms).Append("ms ").Append(r.Bytes / 1024).Append("KB; ");
            }
            LastSummary = L10n.T("切片预取(并发") + SegConcurrency + ") " + ok + "/" + segs.Count + L10n.T(" 成功，累计 ")
                        + (totalBytes / 1024) + "KB / " + totalMs + "ms";
            Android.Util.Log.Info("MchatVod", LastSummary + " | " + sb);
        }

        /// <summary>最近一次切片预取的统计摘要（供调试/UI 展示）。</summary>
        public static string LastSummary { get; private set; } = "";

        struct SegStat { public bool Ok; public long Ms; public int Bytes; }

        /// <summary>把 m3u8 里的相对切片地址解析成绝对地址。</summary>
        static string Resolve(string baseUrl, string rel)
        {
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

        public static void Clear(Context ctx)
        {
            try
            {
                var dir = new Java.IO.File(ctx.CacheDir, "vod_buf");
                if (dir.Exists()) DeleteDir(dir);
            }
            catch { }
        }

        static void DeleteDir(Java.IO.File dir)
        {
            try
            {
                var kids = dir.ListFiles();
                if (kids != null)
                    foreach (var k in kids) { if (k.IsDirectory) DeleteDir(k); else k.Delete(); }
                dir.Delete();
            }
            catch { }
        }
    }
}

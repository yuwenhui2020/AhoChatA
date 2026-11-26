using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AhoChatA
{
    /**
     * 单引擎网页服务（包揽全局）：
     *   · 对外前端监听 7790（0.0.0.0）+ HTTPS 7791（见 WebServer.Gateway.cs 分部），
     *     按路径分流 /cam（摄像头·屏幕共享）、/draw（画笔）、/file（文件）、/hub（导航页）、
     *     其余 → 反代到内置回环网页引擎（下面这个 HttpListener，6789）。
     *   · 本类是「内置回环网页引擎」：绑 6789（仅回环），只接受来自对外前端的回环连接，
     *     托管 Assets/webui 静态资源与 /api/* 接口，并负责 /chat（聊天）等页面。
     *   · App 内部 SPA 另绑 6800（回环），给本机 WebView 用。
     *
     * 职责：
     *   1) 托管 Assets/webui 下的静态资源（index.html / css / js）
     *   2) 提供 /api/session、/api/messages、/api/send、/api/peers 四个接口，
     *      全部转交给 MchatBridge 的既有实现（网页端后端逻辑已在其中）
     *   3) 把 /__media/<file> 映射到 app 私有 media 目录，使网页端能加载图片/语音等
     */
    public sealed partial class WebServer : IDisposable
    {
        public const int Port = 6789;
        /// <summary>
        /// App 内部 SPA 回环端口。仅绑定 127.0.0.1，供 App 内 WebView 同源加载，
        /// 不再依赖 app.local 这类可能被网络/DNS(mDNS) 解析的虚拟主机，
        /// 从而避免 net::ERR_HTTP_RESPONSE_CODE_FAILURE（app.local 在部分设备上会被
        /// 解析到局域网真实主机并返回非法 HTTP 响应）。
        /// </summary>
        public const int AppPort = 6800;
        private const string MediaPrefix = "/__media/";

        private HttpListener listener = new HttpListener();
        private readonly HttpListener appListener = new HttpListener();
        private readonly MchatBridge bridge;
        private readonly string webuiDir;
        private readonly string appDir;
        private readonly string mediaDir;
        private Context _assetCtx;
        private int _extractScheduled;
        private CancellationTokenSource cts;
        private Task loopTask;
        private Task appTask;
        private Task watchdogTask;
        private bool appListenerRunning;
        public bool AppListenerRunning { get { return appListenerRunning; } }
        /// <summary>accept 循环连续失败计数：mono 的 HttpListener 在 socket 层出问题后
        /// GetContextAsync 会一直抛异常，老实现静默 continue 会变成空转/忙循环，
        /// 表现为「端口在听但谁也不应答」——真机 D2 反复出现过。连续失败达阈值就重建。</summary>
        private int acceptFailStreak;
        /// <summary>看门狗自检失败次数：loopback 自 ping 连续失败才重建，避免单次网络抖动误判。</summary>
        private int selfPingFailStreak;

        public WebServer(MchatBridge bridge, Context context)
        {
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.webuiDir = Path.Combine(context.FilesDir.AbsolutePath, "webui");
            this.appDir = Path.Combine(context.FilesDir.AbsolutePath, "www");
            this.mediaDir = Path.Combine(context.FilesDir.AbsolutePath, "media");

            // 静态资源现已直接从 APK 内 Assets 提供（ServeWebuiStatic / ServeAppStatic），
            // FilesDir 里的解压目录只是「兼容回退」。因此不再在构造期同步全量拷贝
            // 12MB / 327 个文件（实测这一步是 AhoChatA 服务启动慢的主因），
            // 改为 Start() 后由低优先级后台线程按 APK 版本戳增量补齐。
            _assetCtx = context.ApplicationContext ?? context;

            // 6789 现在是「统一网关（7790）的上游」，不直接对局域网暴露。
            // 这里用通配前缀 "+"（接受任意 Host 头）：Mono/Android 的 HttpListener 对
            // "http://127.0.0.1:6789/" 这种显式 IP 前缀做 Host 匹配时，反代改写后的
            // Host 仍可能被判 "Invalid host"，换成 "+" 后由下面 Handle 里的 Host 校验
            // 来挡掉外部直连，既消除该报错又保留「不对外暴露」的安全边界。
            listener.Prefixes.Add("http://+:" + Port + "/");
            // appListener 仅绑回环：App 内部 SPA（Assets/www）只给本机 WebView 用
            appListener.Prefixes.Add("http://127.0.0.1:" + AppPort + "/");
        }

        /* ===================== 生命周期 ===================== */

        public void Start()
        {
            if (listener.IsListening) return;
            try
            {
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    // 个别 Android / Mono 版本不支持通配前缀 "+"，退回显式回环前缀。
                    // 此时网关反代会改写 Host 为 127.0.0.1:6789，仍可命中该前缀。
                    Android.Util.Log.Warn("Mchat", "WebServer 通配前缀启动失败，退回 127.0.0.1：" + ex.ToString());
                    try { listener.Close(); } catch (Exception closeEx)
                    { Android.Util.Log.Warn("Mchat", "关闭失败的通配 HttpListener 异常: " + closeEx.ToString()); }
                    listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                    listener.Start();
                }
                try
                {
                    appListener.Start();
                    appListenerRunning = true;
                }
                catch (Exception ex)
                {
                    appListenerRunning = false;
                    Android.Util.Log.Error("Mchat", "启动回环SPA服务失败（AppPort=" + AppPort + "): " + ex.ToString());
                }
                // 对外前端（单引擎 7790 + HTTPS 7791）：路径分流到流式服务或内置回环网页引擎
                StartFront();
                cts = new CancellationTokenSource();
                var startToken = cts.Token;
                Interlocked.Exchange(ref acceptFailStreak, 0);
                Interlocked.Exchange(ref selfPingFailStreak, 0);
                loopTask = Task.Run(() => LoopAsync(startToken));
                if (appListenerRunning)
                    appTask = Task.Run(() => AppLoopAsync(startToken));
                else
                    appTask = null;
                watchdogTask = Task.Run(() => WatchdogLoop(startToken));
                Android.Util.Log.Info("Mchat", "WebServer listeners ready: internal=" + listener.IsListening
                    + ", app=" + appListenerRunning + ", front=" + IsRunning
                    + ", port=" + ExternalPortActual);
                // 端口已开始监听后再慢慢补解压目录，绝不占用启动关键路径
                ScheduleAssetExtraction();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", "WebServer.Start 失败，正在清理部分监听: " + ex.ToString());
                try { Stop(); } catch (Exception stopEx)
                { Android.Util.Log.Error("Mchat", "清理 WebServer.Start 部分状态失败: " + stopEx.ToString()); }
                throw;
            }
        }

        public void Stop()
        {
            try { cts?.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { appListener.Stop(); } catch { }
            try { appListener.Close(); } catch { }
            appListenerRunning = false;
            try { StopFront(); } catch { }
            try { loopTask?.Wait(2000); } catch { }
            try { appTask?.Wait(2000); } catch { }
            try { watchdogTask?.Wait(2000); } catch { }
        }

        public void Dispose()
        {
            Stop();
            try { listener.Abort(); } catch { }
            try { appListener.Abort(); } catch { }
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                    Interlocked.Exchange(ref acceptFailStreak, 0);
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) break;
                    // 老实现：静默 continue。mono 的 HttpListener 在 socket 层坏了之后
                    // 会持续抛异常，循环空转烧 CPU，而外部看起来就是「无响应」。
                    // 连续失败达到阈值 → 让看门狗重建监听（本循环不自己动手，避免重入）。
                    if (Interlocked.Increment(ref acceptFailStreak) >= 10)
                    {
                        Android.Util.Log.Warn("Mchat", "HTTP accept 连续失败 " + acceptFailStreak + " 次，请求重建监听");
                        RequestRebuild();
                    }
                    await Task.Delay(200, token).ConfigureAwait(false);
                    continue;
                }

                // 每个请求独立处理，不阻塞监听循环
                _ = Task.Run(() => SafeHandle(ctx, token));
            }
        }

        /* ===================== 看门狗：自检 + 自动重建 ===================== */

        // 真机现象（D2 热点宿主）：进程活着、端口在听、无任何错误日志，
        // 但所有请求（含 adb forward 过来的）全部无响应，force-stop 后才恢复。
        // 不猜具体根因（mono HttpListener / 网卡状态变化 / 半开连接都有可能），
        // 用最低成本的自检兜底：每 30s 从 loopback 给自己发一个 /api/ping，
        // 连续 3 次超时就重建整个监听。loopback 不经过任何真实网卡，
        // 网络怎么变都不影响自检的有效性。
        private async Task WatchdogLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(30000, token).ConfigureAwait(false); } catch { break; }
                if (token.IsCancellationRequested) break;
                if (SelfPing(5000))
                {
                    Interlocked.Exchange(ref selfPingFailStreak, 0);
                    continue;
                }
                if (Interlocked.Increment(ref selfPingFailStreak) >= 3)
                {
                    Android.Util.Log.Warn("Mchat", "WebServer 自检连续失败，重建 HTTP 监听");
                    Interlocked.Exchange(ref selfPingFailStreak, 0);
                    RequestRebuild();
                }
            }
        }

        /* ===================== 面向用户的健康检查 / 重启（「我的」页 Web 状态卡片） ===================== */

        /// <summary>Web 服务健康状态：正常 / 未启动 / 运行中但无响应（僵死）。</summary>
        public enum ServiceHealth { Running, NotRunning, Unresponsive }

        /// <summary>
        /// 端到端探测对外网关（7790）：从 127.0.0.1 请求 /api/ping，
        /// 既校验前端线程在应答，也校验其背后的内置引擎(6789)可达。
        /// 超时短（默认 2.5s），供 UI 定时调用，不会长时间卡住。
        /// </summary>
        public static ServiceHealth ProbeHealth(int timeoutMs = 2500)
        {
            if (!WebServer.IsRunning) return ServiceHealth.NotRunning;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + WebServer.ExternalPortActual + "/api/ping");
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return ServiceHealth.Unresponsive;
                    var buf = new byte[8];
                    s.ReadTimeout = timeoutMs;
                    return s.Read(buf, 0, buf.Length) > 0
                        ? ServiceHealth.Running : ServiceHealth.Unresponsive;
                }
            }
            catch { return ServiceHealth.Unresponsive; }
        }

        /// <summary>
        /// 完整重启 Web 服务（修复「启动后网页打不开」）：
        /// 1) 重建内置引擎 6789/6800 的监听与三个循环（Cancel+Stop→Start，不 Close，可安全复用实例）；
        /// 2) 停/启对外前端 7790/7791（静态、幂等）。
        /// 全程后台执行，做完用 ProbeHealth 复检一次并回调结果，不阻塞 UI 线程。
        /// </summary>
        public void Restart(Action<ServiceHealth> onDone = null)
        {
            Task.Run(() =>
            {
                ServiceHealth final = ServiceHealth.NotRunning;
                try
                {
                    Android.Util.Log.Info("Mchat", "用户请求重启 Web 服务…");

                    // --- 内置引擎重建（与看门狗 RequestRebuild 同一套，不 Close 监听）---
                    var oldCts = cts;
                    try { oldCts?.Cancel(); } catch { }
                    try { listener.Stop(); } catch { }
                    try { appListener.Stop(); } catch { }
                    try { loopTask?.Wait(2000); } catch { }
                    try { appTask?.Wait(2000); } catch { }
                    try { watchdogTask?.Wait(2000); } catch { }
                    try { listener.Start(); } catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Mchat", "重启：内置监听 Start 失败：" + ex.ToString());
                    }
                    try
                    {
                        appListener.Start();
                        appListenerRunning = true;
                    }
                    catch (Exception ex)
                    {
                        appListenerRunning = false;
                        Android.Util.Log.Error("Mchat", "重启：回环SPA服务失败（AppPort=" + AppPort + "): " + ex.ToString());
                    }
                    cts = new CancellationTokenSource();
                    var rebuildToken = cts.Token;
                    Interlocked.Exchange(ref acceptFailStreak, 0);
                    Interlocked.Exchange(ref selfPingFailStreak, 0);
                    loopTask = Task.Run(() => LoopAsync(rebuildToken));
                    appTask = appListenerRunning ? Task.Run(() => AppLoopAsync(rebuildToken)) : null;
                    watchdogTask = Task.Run(() => WatchdogLoop(rebuildToken));

                    // --- 对外网关重建（7790 + 7791，静态幂等）---
                    try { StopFront(); } catch { }
                    try { StartFront(); } catch (Exception ex)
                    {
                        Android.Util.Log.Error("Mchat", "重启：前端网关启动失败：" + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("Mchat", "重启 Web 服务异常：" + ex.Message);
                }

                // 给监听线程一点就绪时间再复检
                try { Thread.Sleep(700); } catch { }
                try { final = ProbeHealth(3000); } catch { }
                Android.Util.Log.Info("Mchat", "Web 服务重启后状态：" + final);
                try { onDone?.Invoke(final); } catch { }
            });
        }

        /// <summary>从 127.0.0.1 给自己发一个轻量请求，5s 内没有 OK 响应即视为僵死。</summary>
        private bool SelfPing(int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + "/api/ping");
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    var buf = new byte[16];
                    s.ReadTimeout = timeoutMs;
                    int n = s.Read(buf, 0, buf.Length);
                    return n > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>请求重建：停掉旧监听并按原配置重启（Prefixes 在实例上，重新 Start 即可）。</summary>
        private void RequestRebuild()
        {
            Task.Run(() =>
            {
                try
                {
                    var oldCts = cts;
                    try { oldCts?.Cancel(); } catch { }
                    try { listener.Stop(); } catch { }
                    try { appListener.Stop(); } catch { }
                    try { loopTask?.Wait(2000); } catch { }
                    try { appTask?.Wait(2000); } catch { }
                    try { watchdogTask?.Wait(2000); } catch { }
                    listener.Start();
                    try
                    {
                        appListener.Start();
                        appListenerRunning = true;
                    }
                    catch (Exception ex)
                    {
                        appListenerRunning = false;
                        Android.Util.Log.Error("Mchat", "重建回环SPA服务失败（AppPort=" + AppPort + "): " + ex.ToString());
                    }
                    cts = new CancellationTokenSource();
                    var rebuildToken = cts.Token;
                    Interlocked.Exchange(ref acceptFailStreak, 0);
                    Interlocked.Exchange(ref selfPingFailStreak, 0);
                    loopTask = Task.Run(() => LoopAsync(rebuildToken));
                    appTask = appListenerRunning ? Task.Run(() => AppLoopAsync(rebuildToken)) : null;
                    watchdogTask = Task.Run(() => WatchdogLoop(rebuildToken));
                    Android.Util.Log.Info("Mchat", "HTTP 监听已重建 (internal=" + listener.IsListening
                        + ", app=" + appListenerRunning + ")");
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("Mchat", "重建 HTTP 监听失败: " + ex.Message);
                }
            });
        }

        /* ===================== 回环 SPA（App 内部页面） ===================== */

        // 与 LoopAsync 类似，但监听 127.0.0.1:AppPort，专门服务 App 内部 SPA（Assets/www）。
        // 这样主框架不再依赖 app.local 虚拟主机，彻底规避 DNS/mDNS 把 app.local 解析到
        // 真实主机而返回非法 HTTP 响应（表现为 ERR_HTTP_RESPONSE_CODE_FAILURE）的问题。
        private async Task AppLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await appListener.GetContextAsync(); }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    Android.Util.Log.Error("Mchat", "App HttpListener accept 失败，停止循环（避免错误忙循环）: " + ex.ToString());
                    break;
                }

                // 每个请求独立处理，不阻塞监听循环
                _ = Task.Run(() => SafeAppHandle(ctx));
            }
        }

        private void SafeAppHandle(HttpListenerContext ctx)
        {
            try { HandleApp(ctx); }
            catch (Exception) { try { ctx.Response.Abort(); } catch { } }
        }

        private void HandleApp(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            string path = req.Url.AbsolutePath;

            // 媒体文件（聊天收发的图片/视频/音频）同样走回环同源，交给既有 ServeMedia。
            if (path.StartsWith(MediaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ServeMedia(ctx, path.Substring(MediaPrefix.Length));
                return;
            }

            // 文档阅读器（Web 版）：与浏览器入口同一套（viewer 静态资源 + 文档字节 cookie 供流）
            if (path.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase))
            {
                ServeViewerStatic(ctx, path);
                return;
            }
            if (path.StartsWith("/doc/", StringComparison.OrdinalIgnoreCase))
            {
                ServeDocFile(ctx, req);
                return;
            }
            // Web 阅读器：浏览器本地文档临时上传 + 书架读写。
            // App 内 WebView 打开阅读页时也复用同一套 viewer，必须补齐这两条 API，
            // 否则「打开本地文件 / 存入书架」在 App 内会 404。
            if (path.StartsWith("/api/doc/", StringComparison.OrdinalIgnoreCase))
            {
                ServeDocApi(ctx, path, req);
                return;
            }
            if (path.StartsWith("/api/reader/", StringComparison.OrdinalIgnoreCase))
            {
                ServeReaderApi(ctx, path, req);
                return;
            }
            // GPS 共享位置快照（App 内浏览器也需要，供 gps.html 主动拉取）
            if (path.Equals("/api/gps/state", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(res, bridge.GetGpsStateJson());
                return;
            }
            // 自动接收配置（App 内网页也用同一套 HTTP API）
            if (path.Equals("/api/autoreceive", StringComparison.OrdinalIgnoreCase))
            {
                ServeAutoReceiveApi(ctx, req);
                return;
            }

            // GPS 共享位置页（App 内）
            if (path == "/gps" || path == "/gps/")
            {
                var gAsset = OpenAsset("www/gps.html");
                if (gAsset != null) { SendAsset(ctx, gAsset, "www/gps.html"); return; }
                var gFile = Path.Combine(appDir, "gps.html");
                if (File.Exists(gFile)) { SendFile(ctx, gFile); return; }
                res.StatusCode = 404; WriteText(res, "404 Not Found"); return;
            }

            // 设置页（App 内）
            if (path == "/settings" || path == "/settings/")
            {
                var sAsset = OpenAsset("www/settings.html");
                if (sAsset != null) { SendAsset(ctx, sAsset, "www/settings.html"); return; }
                var sFile = Path.Combine(appDir, "settings.html");
                if (File.Exists(sFile)) { SendFile(ctx, sFile); return; }
                res.StatusCode = 404; WriteText(res, "404 Not Found"); return;
            }

            if (path.StartsWith("/api/dns/", StringComparison.OrdinalIgnoreCase))
            {
                ServeDnsApi(ctx, req, path);
                return;
            }
            if (path.Equals("/dns.html", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/js/dns.js", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/css/web.css", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, path);
                return;
            }

            // DNS 管理页（App 内）
            if (path == "/dns" || path == "/dns/")
            {
                ServeStatic(ctx, "/dns.html");
                return;
            }

            // 阅读模块的书源代理：让网页端能绕过 CORS 抓取书源站点（不依赖 legado 远端服务）。
            if (path.StartsWith("/api/proxy", StringComparison.OrdinalIgnoreCase))
            {
                ServeProxy(ctx, req);
                return;
            }

            // 内置浏览器代理：/ __proxy /<encoded_url> —— 把外部站点经本机中转，
            // 使 iframe 与当前站点同源，从而可注入 Web Bluetooth Polyfill 等脚本。
            // 支持 HTML 重写（相对链接、资源 URL 指回代理）、CSP 调整、SSRF 防护。
            if (path.StartsWith("/__proxy/", StringComparison.OrdinalIgnoreCase))
            {
                ServeBrowserProxy(ctx, req, path);
                return;
            }

            // 阅读模块入口：/reader 与 /reader/ 映射到 www/reader/index.html
            if (path == "/reader" || path == "/reader/")
            {
                var rAsset = OpenAsset("www/reader/index.html");
                if (rAsset != null) { SendAsset(ctx, rAsset, "www/reader/index.html"); return; }
                var rFile = Path.Combine(appDir, "reader", "index.html");
                if (File.Exists(rFile)) { SendFile(ctx, rFile); return; }
                res.StatusCode = 404; WriteText(res, "404 Not Found"); return;
            }

            // 影音模块：App 内置 WebView 也复用局域网网页端的 media.html 与影音 API。
            // 这两个静态资源必须显式走 webui，否则会被 ServeAppStatic 在 www/ 下找成 404。
            if (path == "/media" || path == "/media/")
            {
                ServeStatic(ctx, "/media.html");
                return;
            }
            if (path.Equals("/css/media.css", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/js/media.js", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/js/vod.js", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/js/hls.min.js", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, path);
                return;
            }
            // MacCMS 标准对外接口（/api.php/provide/vod/），见 WebServer.VodApi.cs::VodMacCms
            if (path.StartsWith("/api.php/provide/vod", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/provide/vod", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/zyapi.php", StringComparison.OrdinalIgnoreCase))
            {
                VodMacCms(ctx, req);
                return;
            }

            if (path.StartsWith("/api/media/", StringComparison.OrdinalIgnoreCase))
            {
                ServeMediaLibraryApi(ctx, path, req);
                return;
            }

            ServeAppStatic(ctx, path);
        }

        // 从 APK 内 Assets/www 直接提供 SPA 静态资源（与 app.local 分支一致，不依赖
        // FilesDir 解压目录是否存在/完整，彻底规避解压失败导致首屏 404 的问题）；
        // 解压目录 FilesDir/www 作为兼容回退。无扩展名的前端路由路径回退到 index.html。
        private void ServeAppStatic(HttpListenerContext ctx, string path)
        {
            var res = ctx.Response;
            if (path == "/" || string.IsNullOrEmpty(path))
                path = "/index.html";

            var rel = Sanitize(path).TrimStart('/');

            // 1) 优先从 APK assets 直接读取（最可靠，不经过解压目录）
            var assetPath = "www/" + rel;
            var assetStream = OpenAsset(assetPath);
            if (assetStream != null) { SendAsset(ctx, assetStream, assetPath); return; }

            // 2) 兼容：解压目录 FilesDir/www 中的文件
            var file = Path.Combine(appDir, rel);
            if (file.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            { SendFile(ctx, file); return; }

            // 3) 已知前端路由（/、/chat）→ 回退聊天 SPA；其余无扩展名/目录型路径 → 空白页，
            //    避免「链接不存在」被误显示为聊天界面。
            bool hasExt = !string.IsNullOrEmpty(Path.GetExtension(rel));
            if (!hasExt || rel.EndsWith("/"))
            {
                string lower = rel.TrimEnd('/').ToLowerInvariant();
                if (lower == "" || lower == "chat" || lower == "index.html")
                {
                    var idxAsset = OpenAsset("www/index.html");
                    if (idxAsset != null) { SendAsset(ctx, idxAsset, "www/index.html"); return; }
                    var idxFile = Path.Combine(appDir, "index.html");
                    if (File.Exists(idxFile)) { SendFile(ctx, idxFile); return; }
                }
                // 其余未知页面 → 空白页（404 + 空 HTML）
                ServeBlankPage(res);
                return;
            }

            res.StatusCode = 404;
            WriteText(res, "404 Not Found");
        }

        /* ===================== 请求分发 ===================== */

        private void SafeHandle(HttpListenerContext ctx, CancellationToken token)
        {
            try { Handle(ctx, token); }
            catch (Exception) { try { ctx.Response.Abort(); } catch { } }
        }

        private void Handle(HttpListenerContext ctx, CancellationToken token)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            string path = req.Url.AbsolutePath;

            // 安全边界：WebServer 用通配前缀 "+" 接受任意 Host（彻底消除反代过来的
            // "Invalid host"），但只服务「回环来源」的连接——只有统一网关（7790）会从
            // 127.0.0.1 连过来。直接用局域网 IP 访问本端口（如 http://192.168.x.x:6789/）
            // 的来源不是回环，一律拒绝，从而无法绕过网关上的双因素认证等保护。
            // 这样「全 IP 可经网关访问」与「双因素认证不被绕过」两个目标同时成立。
            // 只拒绝「明确非回环」的来源；来源无法确定（极少见）时放行，避免误伤网关自检。
            var remote = req.RemoteEndPoint?.Address;
            if (remote != null && !IPAddress.IsLoopback(remote))
            {
                try { res.StatusCode = 403; res.ContentType = "text/plain; charset=utf-8";
                      var b = Encoding.UTF8.GetBytes("forbidden"); res.Close(); } catch { }
                return;
            }

            // 根路径直接就出聊天界面（与 /chat 同一份 index.html）。
            // 以前这里是 302 到 /chat —— 本站现在不做任何重定向：
            // 重定向一旦被网关反代回本端口就会变成死循环（ERR_TOO_MANY_REDIRECTS）。
            if (path == "/" || string.IsNullOrEmpty(path) ||
                path.Equals("/chat", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/chat/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/index.html");
                return;
            }

            if (path.Equals("/download", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/download/", StringComparison.OrdinalIgnoreCase))
            {
                ServeDownloadPage(ctx);
                return;
            }

            // 导航页（与对外前端 /hub 同源内容）
            if (path.Equals("/hub", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/hub/", StringComparison.OrdinalIgnoreCase))
            {
                ServeHubPage(ctx);
                return;
            }

            // 文件共享列表（/file、/file/<目录>、/file/get/…、/file/raw?p=…）。
            // 早先这里 302 到对外端口 7790，网关又会把未识别的路径反代回 6789，
            // 一次大小写/绝对 URI 差异就会 302 到自身 → 浏览器报 ERR_TOO_MANY_REDIRECTS。
            // 现在本站**不做任何重定向**，两个端口都能直接出内容。
            if (path.Equals("/file", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/file/", StringComparison.OrdinalIgnoreCase))
            {
                ServeFileSharePage(ctx, path.Substring("/file".Length), req);
                return;
            }

            // 媒体文件优先（网页端图片/语音/视频）
            if (path.StartsWith(MediaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ServeMedia(ctx, path.Substring(MediaPrefix.Length));
                return;
            }

            // 浏览器端文档阅读器（阅读模块 Web 版）：/viewer/*.html 静态资源 + /doc/file.* 文档字节。
            // path 通过 cookie vwdoc 传递（viewer 前端 fetch("/doc/file.docx") 不带参数，改 cookie 可不动前端 JS）。
            if (path.StartsWith("/viewer/", StringComparison.OrdinalIgnoreCase))
            {
                ServeViewerStatic(ctx, path);
                return;
            }
            if (path.StartsWith("/doc/", StringComparison.OrdinalIgnoreCase))
            {
                ServeDocFile(ctx, req);
                return;
            }

            // 局域网网页文件共享页面：http://<手机IP>:7790/file（由统一网关反代到此）
            if (path.Equals("/share", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/share/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/share.html");
                return;
            }

            // 文件预览分发页：http://<手机IP>:7790/preview?path=<相对路径>&name=<文件名>（由统一网关反代到此）
            if (path.Equals("/preview", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/preview/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/preview.html");
                return;
            }

            // 网页端阅读器：http://<手机IP>:7790/reader（由统一网关反代到此）
            if (path.Equals("/reader", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/reader/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/reader.html");
                return;
            }

            // 网页端影音库：http://<手机IP>:7790/media（与 App 共用曲库/歌单/喜欢）
            if (path.Equals("/media", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/media/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/media.html");
                return;
            }

            // 网页端 GPS 共享位置页（对外浏览器）
            if (path.Equals("/gps", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/gps/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/gps.html");
                return;
            }

            // 网页端设置页（自动接收文件等）
            if (path.Equals("/settings", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/settings/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/settings.html");
                return;
            }

            // 网页端 DNS 管理页
            if (path.Equals("/dns", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/dns/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(ctx, "/dns.html");
                return;
            }

            // MacCMS 标准对外接口（/api.php/provide/vod/）：让 AhoChatA 作为标准视频源被
            // MchatTV 体系接入，字段对齐 MchatTV-CMS 的 CmsPublicServer（vod_* 前缀）。
            if (path.StartsWith("/api.php/provide/vod", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/provide/vod", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/zyapi.php", StringComparison.OrdinalIgnoreCase))
            {
                VodMacCms(ctx, req);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                // Web 阅读器：浏览器本地文件临时上传（返回 token，供 /doc/file.* 读取）
                if (path.StartsWith("/api/doc/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeDocApi(ctx, path, req);
                    return;
                }
                if (path.StartsWith("/api/reader/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeReaderApi(ctx, path, req);
                    return;
                }
                if (path.StartsWith("/api/media/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeMediaLibraryApi(ctx, path, req);
                    return;
                }
                if (path.StartsWith("/api/share/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeShareApi(ctx, path, req);
                    return;
                }
                // GPS 共享位置：网页端拉取"本机 + 对端"位置快照（对接 MchatBridge.GetGpsStateJson）
                if (path.Equals("/api/gps/state", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(res, bridge.GetGpsStateJson());
                    return;
                }
                if (path.Equals("/api/gps/start", StringComparison.OrdinalIgnoreCase))
                {
                    bridge.StartLocationShare(); WriteJson(res, "{\"ok\":true}"); return;
                }
                if (path.Equals("/api/gps/stop", StringComparison.OrdinalIgnoreCase))
                {
                    bridge.StopLocationShare(); WriteJson(res, "{\"ok\":true}"); return;
                }
                // 自动接收局域网文件配置（GET 读 / POST 写 enabled+dir）
                if (path.Equals("/api/autoreceive", StringComparison.OrdinalIgnoreCase))
                {
                    ServeAutoReceiveApi(ctx, req);
                    return;
                }

                ServeApi(ctx, path, req);
                return;
            }

            // 静态资源
            ServeStatic(ctx, path);
        }

        /// <summary>判断 Host 头是否为回环（网关反代改写后的 127.0.0.1 / localhost / ::1 等）。</summary>
        /* ===================== API ===================== */

        private void ServeApi(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            var res = ctx.Response;
            try
            {
                // 看门狗自检专用：必须最便宜（不碰 bridge、不读文件），
                // 否则自检本身会成为新的阻塞源。
                if (path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase))
                {
                    var b = Encoding.ASCII.GetBytes("pong");
                    res.StatusCode = 200;
                    res.ContentType = "text/plain";
                    res.ContentLength64 = b.Length;
                    res.OutputStream.Write(b, 0, b.Length);
                    res.OutputStream.Close();
                    return;
                }

                // 「同步听」对时端点：返回服务端收包时刻 t2 与发包时刻 t3。
                // 客户端配合自己的 T1/T4 用 NTP 四时间戳求时钟偏移与 RTT（见 Chat/SyncClock.cs）。
                // 这个端点必须最便宜：不查 bridge、不读文件、不解密，否则测出来的是服务端开销。
                if (path.Equals("/api/sync/time", StringComparison.OrdinalIgnoreCase))
                {
                    long st2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long st3 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    res.AddHeader("Cache-Control", "no-store");
                    WriteJson(res, "{\"t2\":" + st2 + ",\"t3\":" + st3 + "}");
                    try { SyncClock.NoteFollower(); } catch { }
                    return;
                }

                string ip = ClientIp(req);

                if (path.Equals("/api/session", StringComparison.OrdinalIgnoreCase))
                {
                    var guest = bridge.EnsureGuest(ip);
                    var obj = new Dictionary<string, object>
                    {
                        ["guestId"] = guest.Id,
                        ["name"] = guest.Name,
                        ["ip"] = ip,
                        ["host"] = L10n.T("宿主设备"),
                        ["seq"] = bridge.MessageSeq,
                        ["selfId"] = bridge.SelfId,
                        ["selfName"] = bridge.SelfName,
                        ["conv"] = GetQuery(req.Url.Query, "conv") ?? bridge.ActiveConv,
                        ["qaulPort"] = bridge.QaulPort,
                        ["dual"] = bridge.DualChannel
                    };
                    WriteJson(res, Json(obj));
                    return;
                }

                // 会话列表（房间 + 私聊）
                if (path.Equals("/api/conv", StringComparison.OrdinalIgnoreCase))
                {
                    var list = new List<object>();
                    foreach (var c in bridge.GetConversations())
                    {
                        list.Add(new
                        {
                            id = c.Id,
                            kind = c.IsDm ? "dm" : "room",
                            title = c.Title,
                            subtitle = c.Subtitle,
                            unread = c.Unread,
                            updatedAt = c.UpdatedAt,
                            last = c.LastText
                        });
                    }
                    WriteJson(res, Json(new { items = list, conv = bridge.ActiveConv }));
                    return;
                }

                // 加入房间：POST /api/conv/join?code=xxx
                if (path.Equals("/api/conv/join", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var code = GetQuery(req.Url.Query, "code");
                    var conv = bridge.JoinRoom(code);
                    WriteJson(res, Json(new { ok = true, conv, title = Convs.TitleOf(conv) }));
                    return;
                }

                // 切到指定会话（网页端刷新时同步宿主当前会话）
                if (path.Equals("/api/conv/select", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var conv = GetQuery(req.Url.Query, "conv");
                    if (string.IsNullOrEmpty(conv)) { res.StatusCode = 400; WriteJson(res, "{\"error\":\"conv required\"}"); return; }
                    bridge.SetActiveConv(conv);
                    WriteJson(res, Json(new { ok = true, conv = bridge.ActiveConv }));
                    return;
                }

                // 开私聊：POST /api/conv/dm?peer=<peerId>
                if (path.Equals("/api/conv/dm", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var peer = GetQuery(req.Url.Query, "peer");
                    var conv = bridge.StartDm(peer);
                    if (string.IsNullOrEmpty(conv)) { res.StatusCode = 400; WriteJson(res, "{\"error\":\"bad peer\"}"); return; }
                    WriteJson(res, Json(new { ok = true, conv }));
                    return;
                }

                if (path.Equals("/api/messages", StringComparison.OrdinalIgnoreCase))
                {
                    var since = 0L;
                    var q = req.Url.Query;
                    var m = System.Text.RegularExpressions.Regex.Match(q, @"since=(\d+)");
                    if (m.Success) long.TryParse(m.Groups[1].Value, out since);

                    var conv = GetQuery(q, "conv");
                    if (string.IsNullOrEmpty(conv)) conv = bridge.ActiveConv;

                    // 长轮询：最多等待 25s，让网页端实时收到新消息
                    var list = bridge.WaitForMessages(since, 25000, conv);
                    var obj = new Dictionary<string, object>
                    {
                        ["items"] = list,
                        ["seq"] = bridge.MessageSeq,
                        ["conv"] = bridge.ActiveConv
                    };
                    WriteJson(res, Json(obj));
                    return;
                }

                if (path.Equals("/api/send", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var raw = ReadBody(req);
                    JObject reqj;
                    try { reqj = JObject.Parse(raw); }
                    catch { res.StatusCode = 400; WriteJson(res, "{\"error\":\"bad json\"}"); return; }

                    var guest = bridge.EnsureGuest(ip);
                    string kind = (string)reqj["kind"] ?? "text";
                    string text = (string)reqj["text"] ?? "";
                    string url = (string)reqj["url"] ?? "";
                    string name = (string)reqj["name"] ?? "";
                    string base64 = (string)reqj["base64"] ?? "";
                    string conv = (string)reqj["conv"] ?? GetQuery(req.Url.Query, "conv");

                    Dictionary<string, object> msg;
                    if (!string.IsNullOrEmpty(base64) && !string.IsNullOrEmpty(name))
                        msg = bridge.SendFromGuest(guest, kind, text, url, base64, name, conv);
                    else
                        msg = bridge.SendFromGuest(guest, kind, text, url, null, null, conv);

                    // 前端期望响应本身就是消息对象（含 id），直接返回
                    WriteJson(res, Json(msg));
                    return;
                }

                // 删除单条消息：POST /api/delete  body {"id":"..."}
                if (path.Equals("/api/delete", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var raw = ReadBody(req);
                    var id = ExtractJsonField(raw, "id");
                    if (string.IsNullOrEmpty(id)) id = GetQuery(req.Url.Query, "id");
                    var ok = bridge.DeleteMessage(id);
                    WriteJson(res, "{\"ok\":" + (ok ? "true" : "false") + "}");
                    return;
                }

                // 编辑消息：POST /api/edit  body {"conv":"...","id":"...","text":"新正文"}
                if (path.Equals("/api/edit", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var raw = ReadBody(req);
                    var id = ExtractJsonField(raw, "id");
                    var text = ExtractJsonField(raw, "text");
                    var conv = ExtractJsonField(raw, "conv");
                    if (string.IsNullOrEmpty(conv)) conv = GetQuery(req.Url.Query, "conv");
                    var ok = bridge.EditMessage(conv, id, text ?? "", true);
                    WriteJson(res, "{\"ok\":" + (ok ? "true" : "false") + "}");
                    return;
                }

                // 频道状态 / 双通道开关：GET /api/channel；POST /api/channel?dual=1|0
                if (path.Equals("/api/channel", StringComparison.OrdinalIgnoreCase))
                {
                    if (req.HttpMethod == "POST")
                    {
                        var d = GetQuery(req.Url.Query, "dual");
                        if (d == "1" || d == "true") bridge.SetDualChannel(true);
                        else if (d == "0" || d == "false") bridge.SetDualChannel(false);
                    }
                    WriteJson(res, Json(bridge.ChannelStatus()));
                    return;
                }

                // 清空会话：POST /api/clear?conv=xxx（不带 conv 即当前会话）
                if (path.Equals("/api/clear", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var conv = GetQuery(req.Url.Query, "conv");
                    var n = bridge.ClearConversation(conv);
                    WriteJson(res, "{\"ok\":true,\"removed\":" + n + "}");
                    return;
                }

                // qaul 网络节点：状态 + 邻居（libp2p）
                if (path.Equals("/api/qaul", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(res, Json(bridge.GetQaulStatus()));
                    return;
                }

                // 中间层：自带发现的设备表（含经 qaul 中继发现的）+ 状态
                if (path.Equals("/api/lc/devices", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(res, Json(new { devices = bridge.GetLcDevices(), mid = bridge.GetLcMidStatus() }));
                    return;
                }

                // 立刻广播一帧发现信标
                if (path.Equals("/api/lc/beacon", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    bridge.MidBeaconNow();
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                // 往 qaul 公共 feed 发一条（qaul 客户端 Feed 页面可见）
                if (path.Equals("/api/qaul/publish", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var raw = ReadBody(req);
                    string text = null;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        try { text = (string)JObject.Parse(raw)["text"]; } catch { text = raw; }
                    }
                    var ok = !string.IsNullOrEmpty(text) && bridge.PublishQaulFeed(text);
                    WriteJson(res, Json(new { ok }));
                    return;
                }

                // 把某人移出 qaul 群：POST /api/qaul/remove?group=<hex>&peer=<base58>
                if (path.Equals("/api/qaul/remove", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var group = GetQuery(req.Url.Query, "group");
                    var peer = GetQuery(req.Url.Query, "peer");
                    var ok = !string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(peer) &&
                             bridge.RemoveFromQaulGroup(group, peer);
                    WriteJson(res, Json(new { ok }));
                    return;
                }

                // 从本机主动发一条到指定会话（含 qaul 私聊/群聊、feed、房间）：
                // POST /api/qaul/send?conv=<conv>&text=<文本>
                // 与 App 内聊天框走同一条 SendInConv 路径（会做会话路由与加密）。
                if (path.Equals("/api/qaul/send", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var conv = GetQuery(req.Url.Query, "conv");
                    var text = GetQuery(req.Url.Query, "text");
                    if (string.IsNullOrEmpty(text))
                    {
                        var raw = ReadBody(req);
                        if (!string.IsNullOrEmpty(raw))
                        {
                            try
                            {
                                var jo = JObject.Parse(raw);
                                text = (string)jo["text"];
                                if (string.IsNullOrEmpty(conv)) conv = (string)jo["conv"];
                            }
                            catch { }
                        }
                    }
                    var m = string.IsNullOrEmpty(text) ? null : bridge.SendInConv(conv, text);
                    WriteJson(res, Json(new { ok = m != null, id = m == null ? null : Msg.Str(m, "id") }));
                    return;
                }

                // 添加 qaul 静态节点（跨网段/隧道直连）：POST /api/qaul/static?addr=1.2.3.4:9200
                if (path.Equals("/api/qaul/static", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var addr = GetQuery(req.Url.Query, "addr");
                    var ok = !string.IsNullOrEmpty(addr) && bridge.AddQaulStaticPeer(addr);
                    WriteJson(res, Json(new { ok, addr }));
                    return;
                }

                // QUIC 诊断开关：POST /api/qaul/trace?on=1 → 打印每条 STREAM 帧的收发偏移。
                // 真机上没有环境变量可用，只能靠这个口子打开包级日志。
                if (path.Equals("/api/qaul/trace", StringComparison.OrdinalIgnoreCase))
                {
                    var on = GetQuery(req.Url.Query, "on");
                    AhoChatA.QuicConnection._streamTrace = on == null || on == "1" || on == "true";
                    WriteJson(res, Json(new { ok = true, streamTrace = AhoChatA.QuicConnection._streamTrace }));
                    return;
                }

                // 建群 / 邀请（便于脚本与网页端操作）：POST /api/qaul/group?name=
                if (path.Equals("/api/qaul/group", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var name = GetQuery(req.Url.Query, "name");
                    if (string.IsNullOrEmpty(name)) name = L10n.T("新群组");
                    var conv = bridge.CreateQaulGroup(name);
                    WriteJson(res, Json(new { ok = conv != null, conv, group = conv == null ? null
                        : conv.Substring(Convs.QaulGroupPrefix.Length) }));
                    return;
                }

                // POST /api/qaul/invite?group=<hex>&peer=<base58>
                if (path.Equals("/api/qaul/invite", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var group = GetQuery(req.Url.Query, "group");
                    var peer = GetQuery(req.Url.Query, "peer");
                    var ok = !string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(peer) &&
                             bridge.InviteToQaulGroup(group, peer);
                    WriteJson(res, Json(new { ok }));
                    return;
                }

                // 退出 qaul 群：POST /api/qaul/leave?group=<hex>
                if (path.Equals("/api/qaul/leave", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var group = GetQuery(req.Url.Query, "group");
                    var ok = !string.IsNullOrEmpty(group) && bridge.LeaveQaulGroup(group);
                    WriteJson(res, Json(new { ok }));
                    return;
                }

                // 已发现的 qaul 节点（邻居）：GET /api/qaul/peers
                if (path.Equals("/api/qaul/peers", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(res, Json(bridge.GetQaulPeers()));
                    return;
                }

                // 给某个节点发消息：POST /api/qaul/dm?peer=<base58>&text=...（也可用 body {"peer","text"}）
                // 公钥还没到时会排队（返回 queued=true），到手后自动补发。
                if (path.Equals("/api/qaul/dm", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var peer = GetQuery(req.Url.Query, "peer");
                    var dmText = GetQuery(req.Url.Query, "text");
                    if (string.IsNullOrEmpty(peer) || string.IsNullOrEmpty(dmText))
                    {
                        var raw = ReadBody(req);
                        if (!string.IsNullOrEmpty(raw))
                        {
                            try
                            {
                                var jo = JObject.Parse(raw);
                                if (string.IsNullOrEmpty(peer)) peer = (string)jo["peer"];
                                if (string.IsNullOrEmpty(dmText)) dmText = (string)jo["text"];
                            }
                            catch { }
                        }
                    }
                    if (string.IsNullOrEmpty(peer) || string.IsNullOrEmpty(dmText))
                    {
                        res.StatusCode = 400;
                        WriteJson(res, "{\"ok\":false,\"error\":\"peer and text required\"}");
                        return;
                    }
                    WriteJson(res, Json(bridge.SendQaulDm(peer, dmText)));
                    return;
                }

                // 主动索要某个用户的公钥：POST /api/qaul/request?peer=<base58>
                if (path.Equals("/api/qaul/request", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var peer = GetQuery(req.Url.Query, "peer");
                    var ok = !string.IsNullOrEmpty(peer) && bridge.RequestQaulUser(peer);
                    WriteJson(res, Json(new { ok }));
                    return;
                }

                // qaul 运行日志（最近 300 条，纯文本）：GET /api/qaul/log
                if (path.Equals("/api/qaul/log", StringComparison.OrdinalIgnoreCase))
                {
                    var txt = bridge.GetQaulLogText();
                    var bytes = Encoding.UTF8.GetBytes(txt ?? "");
                    res.ContentType = "text/plain; charset=utf-8";
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    res.OutputStream.Close();
                    return;
                }

                // 身份与指纹（qaul / bitchat 两套格式）
                if (path.Equals("/api/identity", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(res, Json(bridge.GetIdentitySummary()));
                    return;
                }

                // 换指纹：POST /api/identity/rotate?scope=all|qaul|bitchat
                if (path.Equals("/api/identity/rotate", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
                {
                    var scopeText = GetQuery(req.Url.Query, "scope");
                    var scope = IdentityScope.All;
                    if (string.Equals(scopeText, "qaul", StringComparison.OrdinalIgnoreCase)) scope = IdentityScope.Qaul;
                    else if (string.Equals(scopeText, "bitchat", StringComparison.OrdinalIgnoreCase)) scope = IdentityScope.Bitchat;

                    var id = bridge.RotateIdentity(scope);
                    WriteJson(res, Json(new
                    {
                        ok = id != null,
                        scope = scope.ToString().ToLowerInvariant(),
                        identity = id == null ? null : id.ToSummary()
                    }));
                    return;
                }

                if (path.Equals("/api/peers", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(res, Json(bridge.GetPeerListForWeb()));
                    return;
                }

                // 书源代理（阅读模块用）：网页端绕过 CORS 抓取书源站点。
                if (path.StartsWith("/api/proxy", StringComparison.OrdinalIgnoreCase))
                {
                    ServeProxy(ctx, req);
                    return;
                }

                // DNS API：内置 DNS 服务器控制
                if (path.StartsWith("/api/dns/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeDnsApi(ctx, req, path);
                    return;
                }

                res.StatusCode = 404;
                WriteJson(res, "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                WriteJson(res, "{\"error\":" + Escape(ex.Message) + "}");
            }
        }

        /* ===================== 阅读器 API（网页端 /reader） ===================== */
        /*
         * 网页端阅读器与 App 原生阅读器共用 ReaderLibrary：
         * shelf.json / prefs.json / books 目录是同一份，所以书架、进度、排版、
         * 上传的文档完全互通。
         *   GET  /api/reader/shelf                     书架列表
         *   GET  /api/reader/open?id=                  书籍详情 + 章节目录
         *   GET  /api/reader/chapter?id=&i=            单章正文
         *   POST /api/reader/progress?id=&i=&pct=&total=  保存进度
         *   POST /api/reader/rename?id=&name=          重命名
         *   POST /api/reader/delete?id=                删除
         *   POST /api/reader/upload?name=              上传文件（原始字节体，支持 TXT/EPUB/PDF/FB2/MD/ZIP）
         *   GET  /api/reader/cover?id=                 书籍封面（PDF 第一页 / EPUB 内嵌封面）
         *   GET  /api/reader/pdf?id=&i=&w=             PDF 单页 JPEG（i=页码 0 起，w=渲染宽度）
         *   GET/POST /api/reader/settings              排版偏好
         *   GET  /api/reader/sources                   书源列表
         *   POST /api/reader/source                    导入书源（JSON 文本体）
         *   POST /api/reader/source/toggle?id=&on=     启用/停用
         *   POST /api/reader/source/delete?id=         删除书源
         *   POST /api/reader/search?key=               搜索网络书籍
         *   POST /api/reader/add?source=&url=&name=&author=  在线书加入书架
         */

        private void ServeReaderApi(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            var res = ctx.Response;
            Action<string> fail = msg => WriteJson(res, Json(new { ok = false, error = msg ?? L10n.T("未知错误") }));
            try
            {
                AhoChatA.Reader.ReaderLibrary.Init(Android.App.Application.Context);
                string q = req.Url.Query;
                string id = GetQuery(q, "id");
                bool post = string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

                if (path.Equals("/api/reader/shelf", StringComparison.OrdinalIgnoreCase))
                {
                    var list = new List<object>();
                    foreach (var it in AhoChatA.Reader.ReaderLibrary.Shelf())
                    {
                        list.Add(new
                        {
                            id = it.Id,
                            name = ReaderShelfName(it.Name),
                            author = it.Author ?? "",
                            type = it.Type,
                            format = AhoChatA.Reader.ReaderLibrary.FormatOf(it),
                            cover = AhoChatA.Reader.PdfCache.HasCover(it.Id)
                                ? "/api/reader/cover?id=" + System.Uri.EscapeDataString(it.Id ?? "") : "",
                            total = it.Total,
                            chapter = it.LastChapter,
                            pct = Math.Round(it.Pct, 4),
                            progress = Math.Round(it.Progress, 4),
                            added = (it.Updated == DateTime.MinValue ? it.Added : it.Updated).ToString("yyyy-MM-dd HH:mm")
                        });
                    }
                    WriteJson(res, Json(new { ok = true, items = list }));
                    return;
                }

                // 书架封面：PDF = 导入时缓存好的第一页，EPUB = 内嵌封面。
                // 这里只读缓存文件，绝不重新渲染 PDF。
                if (path.Equals("/api/reader/cover", StringComparison.OrdinalIgnoreCase))
                {
                    var data = AhoChatA.Reader.ReaderLibrary.CoverBytes(id);
                    if (data == null || data.Length == 0) { res.StatusCode = 404; res.ContentLength64 = 0; res.Close(); return; }
                    res.ContentType = AhoChatA.Reader.PdfCache.CoverMime(data);
                    res.ContentLength64 = data.Length;
                    using (var o = res.OutputStream) o.Write(data, 0, data.Length);
                    return;
                }

                // PDF 单页：一页就是一张图，不做长页拼接。带磁盘缓存，重复请求不再打开 PDF。
                if (path.Equals("/api/reader/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var it = AhoChatA.Reader.ReaderLibrary.Find(id);
                    if (it == null || AhoChatA.Reader.ReaderLibrary.FormatOf(it) != "pdf")
                    { fail(L10n.T("不是 PDF 书籍")); return; }
                    int page = (int)ParseNum(GetQuery(q, "i"), 0);
                    int w = (int)ParseNum(GetQuery(q, "w"), 1080);
                    if (w < 240) w = 240;
                    if (w > 2400) w = 2400;
                    if (page < 0) page = 0;
                    if (it.Total > 0 && page >= it.Total) page = it.Total - 1;  // 页码越界钳到末页
                    var jpg = AhoChatA.Reader.ReaderLibrary.PdfPage(id, it.Path, page, w, 1f);
                    res.ContentType = "image/jpeg";
                    res.ContentLength64 = jpg.Length;
                    using (var o = res.OutputStream) o.Write(jpg, 0, jpg.Length);
                    return;
                }

                if (path.Equals("/api/reader/open", StringComparison.OrdinalIgnoreCase))
                {
                    var item = AhoChatA.Reader.ReaderLibrary.Find(id);
                    if (item == null) { fail("书籍不存在，可能已被删除"); return; }
                    string err;
                    var book = AhoChatA.Reader.ReaderLibrary.GetBook(item, out err);
                    if (book == null) { fail(err); return; }
                    if (book.Type == "online" && (book.Chapters == null || book.Chapters.Count == 0))
                    {
                        err = AhoChatA.Reader.ReaderLibrary.EnsureOnlineAsync(item, book)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                        if (err != null) { fail(err); return; }
                    }
                    var chs = new List<object>();
                    for (int i = 0; i < book.Chapters.Count; i++)
                        chs.Add(new { i = i, t = book.Chapters[i].Title });
                    WriteJson(res, Json(new
                    {
                        ok = true,
                        id = item.Id,
                        name = ReaderShelfName(string.IsNullOrEmpty(item.Name) ? book.Name : item.Name),
                        author = item.Author ?? book.Author ?? "",
                        type = item.Type,
                        format = AhoChatA.Reader.ReaderLibrary.FormatOf(item),
                        total = book.Chapters.Count,
                        chapter = Math.Max(0, Math.Min(item.LastChapter, book.Chapters.Count - 1)),
                        pct = Math.Round(item.Pct, 4),
                        chapters = chs
                    }));
                    return;
                }

                if (path.Equals("/api/reader/chapter", StringComparison.OrdinalIgnoreCase))
                {
                    int i = (int)ParseNum(GetQuery(q, "i"), 0);
                    var item = AhoChatA.Reader.ReaderLibrary.Find(id);
                    if (item == null) { fail("书籍不存在"); return; }
                    if (AhoChatA.Reader.ReaderLibrary.FormatOf(item) == "pdf")
                    {
                        // PDF 走 /api/reader/pdf 按页取图，这里只给个兼容响应
                        int pi = Math.Max(0, i);
                        WriteJson(res, Json(new
                        {
                            ok = true, i = pi, total = Math.Max(1, item.Total),
                            title = L10n.T("第 ") + (pi + 1) + L10n.T(" 页"), text = "", pdf = true
                        }));
                        return;
                    }
                    string err;
                    var book = AhoChatA.Reader.ReaderLibrary.GetBook(item, out err);
                    if (book == null) { fail(err); return; }
                    if (book.Type == "online" && (book.Chapters == null || book.Chapters.Count == 0))
                    {
                        err = AhoChatA.Reader.ReaderLibrary.EnsureOnlineAsync(item, book)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                        if (err != null) { fail(err); return; }
                    }
                    if (book.Chapters.Count == 0) { fail("目录为空"); return; }
                    i = Math.Max(0, Math.Min(i, book.Chapters.Count - 1));
                    string text = AhoChatA.Reader.ReaderLibrary.ChapterTextAsync(item, book, i)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    WriteJson(res, Json(new
                    {
                        ok = true, i = i, total = book.Chapters.Count,
                        title = book.Chapters[i].Title, text = text ?? ""
                    }));
                    return;
                }

                if (path.Equals("/api/reader/progress", StringComparison.OrdinalIgnoreCase) && post)
                {
                    int ci = (int)ParseNum(GetQuery(q, "i"), 0);
                    int total = (int)ParseNum(GetQuery(q, "total"), 0);
                    double pct = ParseNum(GetQuery(q, "pct"), 0);
                    AhoChatA.Reader.ReaderLibrary.SaveProgress(id, ci, pct, total);
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                if (path.Equals("/api/reader/rename", StringComparison.OrdinalIgnoreCase) && post)
                {
                    string name = GetQuery(q, "name");
                    if (string.IsNullOrWhiteSpace(name)) { fail(L10n.T("书名为空")); return; }
                    AhoChatA.Reader.ReaderLibrary.Rename(id, name.Trim());
                    WriteJson(res, Json(new { ok = true, name = name.Trim() }));
                    return;
                }

                if (path.Equals("/api/reader/delete", StringComparison.OrdinalIgnoreCase) && post)
                {
                    AhoChatA.Reader.ReaderLibrary.Delete(id);
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                if (path.Equals("/api/reader/upload", StringComparison.OrdinalIgnoreCase) && post)
                {
                    byte[] bytes;
                    using (var ms = new MemoryStream())
                    {
                        req.InputStream.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    if (bytes.Length == 0) { fail("没有收到文件内容"); return; }
                    string name = GetQuery(q, "name") ?? (L10n.T("上传书籍") + DateTime.Now.ToString("HHmmss"));
                    string err;
                    var item = AhoChatA.Reader.ReaderLibrary.Import(name, bytes, out err);
                    if (item == null) { fail(err); return; }
                    WriteJson(res, Json(new { ok = true, id = item.Id, name = item.Name,
                                             total = item.Total, format = item.Format ?? "" }));
                    return;
                }

                if (path.Equals("/api/reader/settings", StringComparison.OrdinalIgnoreCase))
                {
                    var p = AhoChatA.Reader.ReaderLibrary.Prefs;
                    if (post)
                    {
                        var body = ReadBody(req);
                        try
                        {
                            var o = Newtonsoft.Json.Linq.JObject.Parse(body);
                            if (o["fontSize"] != null) p.FontSize = (int)o["fontSize"];
                            if (o["lineHeight"] != null) p.LineHeight = (double)o["lineHeight"];
                            if (o["justify"] != null) p.Justify = (bool)o["justify"];
                            if (o["theme"] != null) p.Theme = (string)o["theme"];
                            if (o["margin"] != null) p.Margin = (int)o["margin"];
                            if (o["paraSpace"] != null) p.ParaSpace = (int)o["paraSpace"];
                            if (o["indent"] != null) p.Indent = (int)o["indent"];
                            if (o["font"] != null) p.Font = (string)o["font"];
                            if (o["bold"] != null) p.Bold = (bool)o["bold"];
                            AhoChatA.Reader.ReaderLibrary.SavePrefs(p);
                        }
                        catch (Exception ex) { fail(L10n.T("设置格式错误：") + ex.Message); return; }
                    }
                    WriteJson(res, Json(new
                    {
                        ok = true,
                        fontSize = p.FontSize,
                        lineHeight = p.LineHeight,
                        justify = p.Justify,
                        theme = p.Theme,
                        margin = p.Margin,
                        paraSpace = p.ParaSpace,
                        indent = p.Indent,
                        font = p.Font,
                        bold = p.Bold
                    }));
                    return;
                }

                if (path.Equals("/api/reader/sources", StringComparison.OrdinalIgnoreCase))
                {
                    var list = new List<object>();
                    foreach (var s in AhoChatA.Reader.ReaderLibrary.Sources())
                        list.Add(new
                        {
                            url = s.BookSourceUrl,
                            name = string.IsNullOrEmpty(s.BookSourceName) ? AhoChatA.Reader.ReaderLibrary.HostOf(s.BookSourceUrl) : s.BookSourceName,
                            group = s.BookSourceGroup ?? "",
                            enabled = s.Enabled
                        });
                    WriteJson(res, Json(new { ok = true, items = list }));
                    return;
                }

                if (path.Equals("/api/reader/source", StringComparison.OrdinalIgnoreCase) && post)
                {
                    var body = ReadBody(req);
                    string err;
                    int n = AhoChatA.Reader.ReaderLibrary.MergeSources(body, out err);
                    if (n <= 0) { fail(err); return; }
                    WriteJson(res, Json(new { ok = true, count = n }));
                    return;
                }

                if (path.Equals("/api/reader/source/toggle", StringComparison.OrdinalIgnoreCase) && post)
                {
                    bool on = ParseNum(GetQuery(q, "on"), 1) > 0;
                    var all = AhoChatA.Reader.ReaderLibrary.Sources();
                    var it = all.Find(x => x.BookSourceUrl == id);
                    if (it == null) { fail("书源不存在"); return; }
                    it.Enabled = on;
                    AhoChatA.Reader.ReaderLibrary.SaveSources(all);
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                if (path.Equals("/api/reader/source/delete", StringComparison.OrdinalIgnoreCase) && post)
                {
                    var all = AhoChatA.Reader.ReaderLibrary.Sources();
                    all.RemoveAll(x => x.BookSourceUrl == id);
                    AhoChatA.Reader.ReaderLibrary.SaveSources(all);
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                if (path.Equals("/api/reader/search", StringComparison.OrdinalIgnoreCase) && post)
                {
                    string key = (GetQuery(q, "key") ?? "").Trim();
                    if (key.Length == 0) { fail("请输入关键词"); return; }
                    var enabled = AhoChatA.Reader.ReaderLibrary.Sources().FindAll(x => x.Enabled);
                    if (enabled.Count == 0) { fail("还没有启用的书源，请先导入书源"); return; }
                    var found = new List<object>();
                    var hits = AhoChatA.Reader.ReaderLibrary.SearchAllAsync(key)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    foreach (var b in hits)
                    {
                        var s2 = AhoChatA.Reader.ReaderLibrary.FindSource(b.SourceUrl);
                        found.Add(new
                        {
                            name = b.Name,
                            author = b.Author ?? "",
                            url = b.BookUrl,
                            source = b.SourceUrl,
                            sourceName = s2 != null ? s2.BookSourceName : ""
                        });
                        if (found.Count >= 200) break;
                    }
                    WriteJson(res, Json(new { ok = true, items = found, sources = enabled.Count }));
                    return;
                }

                if (path.Equals("/api/reader/add", StringComparison.OrdinalIgnoreCase) && post)
                {
                    string url = GetQuery(q, "url");
                    string source = GetQuery(q, "source");
                    string name = GetQuery(q, "name") ?? L10n.T("网络书籍");
                    string author = GetQuery(q, "author") ?? "";
                    if (string.IsNullOrWhiteSpace(url)) { fail(L10n.T("缺少书籍地址")); return; }
                    var sid = "online_" + Guid.NewGuid().ToString("N");
                    var list = AhoChatA.Reader.ReaderLibrary.Shelf();
                    list.Insert(0, new AhoChatA.Reader.ShelfItem
                    {
                        Id = sid, Name = name, Author = author, Type = "online",
                        SourceUrl = source, BookUrl = url, TocUrl = url, Added = DateTime.Now
                    });
                    AhoChatA.Reader.ReaderLibrary.SaveShelf(list);
                    WriteJson(res, Json(new { ok = true, id = sid }));
                    return;
                }

                if (path.Equals("/api/reader/explore", StringComparison.OrdinalIgnoreCase))
                {
                    var all = new List<object>();
                    foreach (var src in AhoChatA.Reader.ReaderLibrary.Sources())
                    {
                        if (!src.Enabled) continue;
                        var cats = AhoChatA.Reader.ReaderLibrary.ExploreCategories(src);
                        if (cats.Count == 0) continue;
                        var cl = new List<object>();
                        foreach (var c in cats) cl.Add(new { title = c.Title, url = c.Url });
                        all.Add(new
                        {
                            url = src.BookSourceUrl,
                            name = string.IsNullOrEmpty(src.BookSourceName)
                                ? AhoChatA.Reader.ReaderLibrary.HostOf(src.BookSourceUrl) : src.BookSourceName,
                            cats = cl
                        });
                    }
                    WriteJson(res, Json(new { ok = true, sources = all }));
                    return;
                }

                if (path.Equals("/api/reader/explore/books", StringComparison.OrdinalIgnoreCase))
                {
                    string source = GetQuery(q, "source");
                    int ci = (int)ParseNum(GetQuery(q, "cat"), 0);
                    int page = (int)ParseNum(GetQuery(q, "page"), 1);
                    var src = AhoChatA.Reader.ReaderLibrary.FindSource(source);
                    if (src == null) { fail("书源不存在"); return; }
                    var cats = AhoChatA.Reader.ReaderLibrary.ExploreCategories(src);
                    if (ci < 0 || ci >= cats.Count) { fail(L10n.T("分类不存在")); return; }
                    var books = AhoChatA.Reader.ReaderLibrary.ExploreAsync(src, cats[ci], Math.Max(1, page))
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    var items = new List<object>();
                    foreach (var b in books)
                        items.Add(new
                        {
                            name = b.Name,
                            author = b.Author ?? "",
                            url = b.BookUrl,
                            source = src.BookSourceUrl,
                            sourceName = src.BookSourceName,
                            lastChapter = b.LastChapter ?? "",
                            kind = b.Kind ?? ""
                        });
                    WriteJson(res, Json(new { ok = true, items = items, page = page }));
                    return;
                }

                if (path.Equals("/api/reader/marks", StringComparison.OrdinalIgnoreCase))
                {
                    var list = new List<object>();
                    foreach (var m in AhoChatA.Reader.ReaderLibrary.Bookmarks(id))
                        list.Add(new
                        {
                            id = m.Id,
                            chapter = m.ChapterIndex,
                            title = m.ChapterTitle ?? "",
                            pct = Math.Round(m.Pct, 4),
                            text = m.Text ?? "",
                            created = m.Created.ToString("MM-dd HH:mm")
                        });
                    WriteJson(res, Json(new { ok = true, items = list }));
                    return;
                }

                if (path.Equals("/api/reader/mark/add", StringComparison.OrdinalIgnoreCase) && post)
                {
                    int ci = (int)ParseNum(GetQuery(q, "i"), 0);
                    double pct2 = ParseNum(GetQuery(q, "pct"), 0);
                    var m = AhoChatA.Reader.ReaderLibrary.AddBookmark(id, ci,
                        GetQuery(q, "title") ?? "", pct2, GetQuery(q, "text") ?? "");
                    WriteJson(res, Json(new { ok = true, id = m.Id }));
                    return;
                }

                if (path.Equals("/api/reader/mark/del", StringComparison.OrdinalIgnoreCase) && post)
                {
                    AhoChatA.Reader.ReaderLibrary.DeleteBookmark(id, GetQuery(q, "mid"));
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                if (path.Equals("/api/reader/change", StringComparison.OrdinalIgnoreCase) && post)
                {
                    string source2 = GetQuery(q, "source");
                    string url2 = GetQuery(q, "url");
                    if (string.IsNullOrWhiteSpace(url2)) { fail(L10n.T("缺少书籍地址")); return; }
                    AhoChatA.Reader.ReaderLibrary.ChangeSource(id, source2, url2,
                        GetQuery(q, "toc") ?? url2, GetQuery(q, "name") ?? "", GetQuery(q, "author") ?? "");
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                fail(L10n.T("未知的阅读接口：") + path);
            }
            catch (Exception ex)
            {
                try { fail(ex.Message); } catch { }
            }
        }

        /// <summary>书架展示名：老数据里可能存的是 local_xxx 这种 id。</summary>
        private static string ReaderShelfName(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "未命名书籍";
            if (n.StartsWith("local_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("online_", StringComparison.OrdinalIgnoreCase)) return L10n.T("未命名书籍");
            return n;
        }

        private static double ParseNum(string s, double def)
        {
            double v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        /* ===================== 文件共享页面（/file，零重定向） ===================== */
        /*
         * 与对外网关 7790 的 /file 完全同构：直接出列表 / 直接下发文件，
         * 不再 302 到别的端口（302 会被网关再反代回来，形成重定向死循环）。
         */

        private void ServeFileSharePage(HttpListenerContext ctx, string subPath, HttpListenerRequest req)
        {
            var res = ctx.Response;
            try
            {
                string root = LanShareService.EnsureShareRoot();
                if (string.IsNullOrEmpty(root))
                {
                    res.StatusCode = 503;
                    WriteText(res, L10n.T("共享目录不可用"));
                    return;
                }
                try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); } catch { }

                string query = "";
                try { query = ((req.Url != null ? req.Url.Query : "") ?? "").TrimStart('?'); } catch { }
                string host = null;
                try { host = req.UserHostName; } catch { }
                bool headOnly = string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

                if (FileShareHttp.IsDownload(subPath))
                {
                    string file = FileShareHttp.ResolveDownload(root, subPath, query);
                    if (string.IsNullOrEmpty(file))
                    {
                        res.StatusCode = 404;
                        WriteText(res, L10n.T("文件不存在"));
                        return;
                    }
                    var fi = new FileInfo(file);
                    res.StatusCode = 200;
                    res.ContentType = FileShareHttp.ContentTypeOf(file);
                    res.AddHeader("Content-Disposition",
                        "attachment; filename*=UTF-8''" + System.Uri.EscapeDataString(fi.Name));
                    res.AddHeader("Cache-Control", "no-store");
                    res.ContentLength64 = fi.Length;
                    if (!headOnly)
                    {
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            fs.CopyTo(res.OutputStream, 64 * 1024);
                    }
                    res.Close();
                    return;
                }

                string html = FileShareHttp.TryList(root, subPath, host);
                if (html == null)
                {
                    res.StatusCode = 404;
                    WriteText(res, L10n.T("目录不存在"));
                    return;
                }
                var bytes = Encoding.UTF8.GetBytes(html);
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.AddHeader("Cache-Control", "no-store");
                res.ContentLength64 = bytes.Length;
                if (!headOnly) res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
            }
            catch (Exception ex)
            {
                try { res.StatusCode = 500; WriteText(res, L10n.T("读取共享目录失败：") + ex.Message); } catch { }
                try { res.Close(); } catch { }
            }
        }

        /* ===================== 局域网文件共享 API ===================== */
        /*
         * 供访客浏览器使用（页面入口 http://<手机IP>:7790/file 或 6789/file），与 2121 的 FTP
         * 共用同一个共享目录 LanShareService.ShareRoot。
         *   GET  /api/share/list?path=<相对目录>         → 列目录
         *   GET  /api/share/download?path=<相对文件>     → 下载
         *   POST /api/share/upload?path=<目录>&name=<文件名> → 上传（原始字节体或 multipart）
         *   POST /api/share/mkdir?path=<目录>&name=<名称> → 新建文件夹
         *   POST /api/share/delete?path=<相对路径>       → 删除文件或文件夹
         */

        private void ServeShareApi(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            var res = ctx.Response;
            try
            {
                var root = LanShareService.ShareRoot;
                if (string.IsNullOrEmpty(root))
                {
                    res.StatusCode = 500;
                    WriteJson(res, L10n.T("{\"error\":\"共享目录尚未就绪\"}"));
                    return;
                }

                string rel = NormalizeRel(GetQuery(req.Url.Query, "path"));

                if (path.Equals("/api/share/list", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = SafeSharePath(root, rel);
                    if (dir == null) { res.StatusCode = 400; WriteJson(res, "{\"error\":\"非法路径\"}"); return; }
                    if (!Directory.Exists(dir)) { res.StatusCode = 404; WriteJson(res, L10n.T("{\"error\":\"目录不存在\"}")); return; }

                    var items = new System.Collections.Generic.List<object>();
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        var di = new DirectoryInfo(d);
                        items.Add(new Dictionary<string, object>
                        {
                            ["name"] = di.Name, ["dir"] = true, ["size"] = 0L,
                            ["mtime"] = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                        });
                    }
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var fi = new FileInfo(f);
                        items.Add(new Dictionary<string, object>
                        {
                            ["name"] = fi.Name, ["dir"] = false, ["size"] = fi.Length,
                            ["mtime"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                        });
                    }

                    WriteJson(res, Json(new Dictionary<string, object> { ["path"] = rel, ["items"] = items }));
                    return;
                }

                if (path.Equals("/api/share/download", StringComparison.OrdinalIgnoreCase))
                {
                    var file = SafeSharePath(root, rel);
                    if (file == null || !File.Exists(file))
                    {
                        res.StatusCode = 404;
                        WriteText(res, "404 Not Found");
                        return;
                    }

                    var bytes = File.ReadAllBytes(file);
                    res.ContentType = "application/octet-stream";
                    res.ContentLength64 = bytes.Length;
                    // 用 RFC 5987 的 filename* 编码，保证中文文件名也能正确下载
                    res.AddHeader("Content-Disposition",
                        "attachment; filename*=UTF-8''" + System.Uri.EscapeDataString(Path.GetFileName(file)));
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
                    return;
                }

                if (path.Equals("/api/share/upload", StringComparison.OrdinalIgnoreCase))
                {
                    if (req.HttpMethod != "POST")
                    { res.StatusCode = 405; WriteJson(res, L10n.T("{\"error\":\"请使用 POST\"}")); return; }
                    HandleShareUpload(ctx, root, rel, req.Url.Query);
                    return;
                }

                if (path.Equals("/api/share/mkdir", StringComparison.OrdinalIgnoreCase))
                {
                    var name = GetQuery(req.Url.Query, "name");
                    if (string.IsNullOrEmpty(name)) { res.StatusCode = 400; WriteJson(res, L10n.T("{\"error\":\"缺少名称\"}")); return; }
                    var dir = SafeSharePath(root, CombineRel(rel, name));
                    if (dir == null) { res.StatusCode = 400; WriteJson(res, "{\"error\":\"非法路径\"}"); return; }
                    Directory.CreateDirectory(dir);
                    WriteJson(res, "{\"ok\":true}");
                    return;
                }

                if (path.Equals("/api/share/delete", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    { res.StatusCode = 405; res.AddHeader("Allow", "POST"); WriteJson(res, L10n.T("{\"error\":\"请使用 POST\"}")); return; }
                    var target = SafeSharePath(root, rel);
                    if (!TrashManager.IsShareDeleteTarget(root, target))
                    { res.StatusCode = 400; WriteJson(res, L10n.T("{\"error\":\"非法路径\"}")); return; }
                    if (!File.Exists(target) && !Directory.Exists(target))
                    { res.StatusCode = 404; WriteJson(res, L10n.T("{\"error\":\"目标不存在\"}")); return; }
                    if (!TrashManager.Instance.MoveToTrash(target, root))
                    { res.StatusCode = 500; WriteJson(res, L10n.T("{\"error\":\"无法移入回收站\"}")); return; }
                    WriteJson(res, "{\"ok\":true}");
                    return;
                }

                // 共享目录与「所有文件访问」权限状态：供网页端判断是否需要引导用户授权。
                if (path.Equals("/api/share/rootinfo", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasAccess = LanShareService.HasAllFilesAccess(null);
                    bool isR = Build.VERSION.SdkInt >= BuildVersionCodes.R;
                    WriteJson(res, Json(new Dictionary<string, object>
                    {
                        ["root"] = root,
                        ["hasAllFilesAccess"] = hasAccess,
                        ["needsAllFilesAccess"] = isR && !hasAccess,
                        ["preferredRoot"] = LanShareService.PreferredRoot,
                        ["ftpPort"] = LanShareService.FtpPort,
                        ["webPort"] = LanShareService.WebPort
                    }));
                    return;
                }

                // 内联取文件：供查看器 <img>/<video>/fetch 直接加载（不强制下载）；
                // 与 /api/share/download 的区别在于 Content-Disposition=inline 且带正确 MIME。
                if (path.Equals("/api/share/raw", StringComparison.OrdinalIgnoreCase))
                {
                    var file = SafeSharePath(root, rel);
                    if (file == null || !File.Exists(file))
                    {
                        res.StatusCode = 404;
                        WriteText(res, "404 Not Found");
                        return;
                    }
                    var bytes = File.ReadAllBytes(file);
                    res.ContentType = MimeOf(file);
                    res.ContentLength64 = bytes.Length;
                    res.AddHeader("Content-Disposition",
                        "inline; filename*=UTF-8''" + System.Uri.EscapeDataString(Path.GetFileName(file)));
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
                    return;
                }

                res.StatusCode = 404;
                WriteJson(res, "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                WriteJson(res, "{\"error\":" + Escape(ex.Message) + "}");
            }
        }

        /// <summary>处理上传：支持原始字节请求体与 multipart/form-data 两种形式。</summary>
        private void HandleShareUpload(HttpListenerContext ctx, string root, string rel, string query)
        {
            var res = ctx.Response;
            try
            {
                var ct = ctx.Request.ContentType ?? "";
                string fileName = GetQuery(query, "name");
                byte[] body = ReadBodyBytes(ctx.Request);
                byte[] content = body;

                if (ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    string parsedName = null;
                    var part = FindMultipartFile(body, ct, ref parsedName);
                    if (part == null)
                    {
                        res.StatusCode = 400;
                        WriteJson(res, L10n.T("{\"error\":\"未找到上传的文件\"}"));
                        return;
                    }
                    content = part;
                    if (!string.IsNullOrEmpty(parsedName)) fileName = parsedName;
                }

                // 只保留文件名，剥离任何目录成分，防止路径穿越
                fileName = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
                if (string.IsNullOrEmpty(fileName)) fileName = "upload.bin";

                var file = SafeSharePath(root, CombineRel(rel, fileName));
                if (file == null) { res.StatusCode = 400; WriteJson(res, "{\"error\":\"非法路径\"}"); return; }

                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, content);

                WriteJson(res, "{\"ok\":true,\"name\":" + Escape(Path.GetFileName(file)) +
                               ",\"size\":" + content.Length + "}");
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                WriteJson(res, "{\"error\":" + Escape(ex.Message) + "}");
            }
        }

        private static byte[] ReadBodyBytes(HttpListenerRequest req)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    req.InputStream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { return new byte[0]; }
        }

        /// <summary>从 multipart 请求体中取出第一个带 filename 的分支。</summary>
        private static byte[] FindMultipartFile(byte[] body, string contentType, ref string fileName)
        {
            try
            {
                const string marker = "boundary=";
                int idx = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;

                var boundary = contentType.Substring(idx + marker.Length).Trim().Trim('"');
                if (boundary.Length == 0) return null;

                var delim = Encoding.ASCII.GetBytes("--" + boundary);
                var positions = new System.Collections.Generic.List<int>();
                for (int i = 0; i + delim.Length <= body.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < delim.Length; j++)
                    {
                        if (body[i + j] != delim[j]) { match = false; break; }
                    }
                    if (match) { positions.Add(i); i += delim.Length - 1; }
                }
                if (positions.Count < 2) return null;

                var crlf2 = Encoding.ASCII.GetBytes("\r\n\r\n");
                for (int k = 0; k < positions.Count - 1; k++)
                {
                    int start = positions[k] + delim.Length;
                    int end = positions[k + 1];
                    while (start < end && (body[start] == '\r' || body[start] == '\n')) start++;
                    int e2 = end;
                    while (e2 > start && (body[e2 - 1] == '\r' || body[e2 - 1] == '\n')) e2--;
                    if (start >= e2) continue;

                    int sep = IndexOfBytes(body, crlf2, start, e2 - start);
                    if (sep < 0) continue;

                    var header = Encoding.UTF8.GetString(body, start, sep - start);
                    int contentStart = sep + 4;
                    int len = e2 - contentStart;
                    if (len < 0) continue;

                    var m = System.Text.RegularExpressions.Regex.Match(
                        header, "filename\\*?\\s*=\\s*(?:\"([^\"]*)\"|([^;\\r\\n]*))",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (m.Success)
                    {
                        var fn = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(fn))
                        {
                            fileName = fn.Trim();
                            var buf = new byte[len];
                            Buffer.BlockCopy(body, contentStart, buf, 0, len);
                            return buf;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static int IndexOfBytes(byte[] src, byte[] pattern, int offset, int count)
        {
            int limit = Math.Min(offset + count, src.Length);
            for (int i = offset; i + pattern.Length <= limit; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (src[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>把相对路径规整为以 '/' 分隔、不含前导斜杠的形式。</summary>
        private static string NormalizeRel(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            var segs = new System.Collections.Generic.List<string>();
            foreach (var s in rel.Replace('\\', '/').Split('/'))
            {
                if (string.IsNullOrEmpty(s) || s == ".") continue;
                if (s == "..") { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); continue; }
                segs.Add(s);
            }
            return string.Join("/", segs);
        }

        private static string CombineRel(string rel, string name)
        {
            var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
            if (string.IsNullOrEmpty(rel)) return n ?? "";
            return rel.TrimEnd('/') + "/" + n;
        }

        /// <summary>把共享目录内的相对路径解析为物理路径，并强制限制在根目录内。</summary>
        private static string SafeSharePath(string root, string rel)
        {
            try
            {
                var combined = string.IsNullOrEmpty(rel)
                    ? root
                    : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

                var full = Path.GetFullPath(combined);
                var rootFull = Path.GetFullPath(root);

                if (!full.StartsWith(rootFull, StringComparison.Ordinal)) return null;
                return full;
            }
            catch { return null; }
        }

        private static string GetQuery(string query, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(query)) return null;
                query = query.TrimStart('?');
                foreach (var pair in query.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int i = pair.IndexOf('=');
                    if (i < 0) continue;
                    if (!pair.Substring(0, i).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                    return Android.Net.Uri.Decode(pair.Substring(i + 1).Replace("+", " "));
                }
            }
            catch { }
            return null;
        }

        /// <summary>从 JSON 请求体里取一个字符串字段（delete/clear 等简单接口用，容错优先）。</summary>
        private static string ExtractJsonField(string body, string field)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var obj = JObject.Parse(body);
                var token = obj[field];
                return token == null ? null : token.ToString();
            }
            catch { }
            return GetQuery(body, field);
        }

        /* ===================== 媒体 ===================== */

        private void ServeMedia(HttpListenerContext ctx, string name)
        {
            var res = ctx.Response;
            name = Sanitize(name);
            if (string.IsNullOrEmpty(name)) { res.StatusCode = 400; res.Close(); return; }

            var file = Path.Combine(mediaDir, name);
            if (!File.Exists(file)) { res.StatusCode = 404; res.Close(); return; }

            try
            {
                var bytes = File.ReadAllBytes(file);
                res.ContentType = MimeOf(name);
                res.ContentLength64 = bytes.Length;
                using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
            }
            catch { try { res.Abort(); } catch { } }
        }

        /* ===================== 书源代理（阅读模块） ===================== */

        // 阅读模块的书源引擎运行在网页端，受浏览器同源/CORS 限制无法直接抓取书源站点。
        // 这里提供服务端中转：网页端把目标 URL 交由本机原生 HttpWebRequest 抓取后回传，
        // 从而「不依赖 legado 远端 AhoChatA 服务」也能联网搜书/取章节。带 SSRF 防护（禁止指向
        // 本机/内网），GET/POST 均支持，自动解 gzip，限制响应体积。
        // 增加缓存策略：ETag/Last-Modified 支持、Cache-Control、本地文件缓存。

        private const long ProxyMaxBytes = 12L * 1024 * 1024; // 12MB
        private const long ProxyCacheMaxSize = 50 * 1024 * 1024; // 50MB 代理缓存
        private const int ProxyCacheTtlSeconds = 3600; // 1 小时缓存 TTL
        private readonly Dictionary<string, CachedProxyResponse> _proxyCache = new Dictionary<string, CachedProxyResponse>();
        private readonly object _proxyCacheLock = new object();

        private sealed class CachedProxyResponse
        {
            public byte[] Data;
            public string ContentType;
            public string ETag;
            public DateTime LastModified;
            public DateTime CachedAt;
            public long ExpiresAt;
        }

        private void ServeProxy(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            try
            {
                string target = GetQuery(req.Url.Query, "url");
                if (string.IsNullOrEmpty(target))
                {
                    res.StatusCode = 400;
                    WriteJson(res, L10n.T("{\"error\":\"缺少 url 参数\"}"));
                    return;
                }
                if (!System.Uri.TryCreate(target, System.UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    res.StatusCode = 400;
                    WriteJson(res, L10n.T("{\"error\":\"仅支持 http/https\"}"));
                    return;
                }
                if (IsPrivateHost(uri.Host))
                {
                    res.StatusCode = 403;
                    WriteJson(res, L10n.T("{\"error\":\"禁止访问内网/本机地址\"}"));
                    return;
                }

                string method = (GetQuery(req.Url.Query, "method") ?? req.HttpMethod).ToUpperInvariant();
                if (method != "GET" && method != "POST" && method != "HEAD")
                {
                    res.StatusCode = 405;
                    WriteJson(res, L10n.T("{\"error\":\"不支持的方法\"}"));
                    return;
                }

                // 自定义请求头（JSON map），来自书源的 header 字段或前端传入。
                var headerJson = GetQuery(req.Url.Query, "headers");
                var customHeaders = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(headerJson))
                {
                    try
                    {
                        var h = JObject.Parse(headerJson);
                        foreach (var p in h.Properties())
                            customHeaders[p.Name] = p.Value?.ToString() ?? "";
                    }
                    catch { /* 忽略非法 header JSON */ }
                }

                string bodyText = GetQuery(req.Url.Query, "body");
                string contentType = GetQuery(req.Url.Query, "contentType");

                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = method;
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 5;
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.UserAgent = customHeaders.TryGetValue("User-Agent", out var ua) && !string.IsNullOrEmpty(ua)
                    ? ua : "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";
                if (!customHeaders.TryGetValue("Accept", out var accept) || string.IsNullOrEmpty(accept))
                    request.Accept = "*/*";
                else
                    request.Accept = accept;
                if (customHeaders.TryGetValue("Referer", out var referer) && !string.IsNullOrEmpty(referer))
                    request.Referer = referer;
                // 透传其余自定义头（跳过受限制 / 会自动处理的）
                foreach (var kv in customHeaders)
                {
                    var k = kv.Key;
                    if (string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k, "Referer", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k, "Accept", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k, "Host", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k, "Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { request.Headers[k] = kv.Value; } catch { }
                }

                if (method == "POST" && !string.IsNullOrEmpty(bodyText))
                {
                    var bytes = Encoding.UTF8.GetBytes(bodyText);
                    if (!string.IsNullOrEmpty(contentType))
                        request.ContentType = contentType;
                    request.ContentLength = bytes.Length;
                    using (var os = request.GetRequestStream())
                        os.Write(bytes, 0, bytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var ct = response.ContentType ?? "application/octet-stream";
                    var etag = response.Headers["ETag"];
                    var lastModifiedStr = response.Headers["Last-Modified"];
                    DateTime lastModified = DateTime.MinValue;
                    if (!string.IsNullOrEmpty(lastModifiedStr))
                        DateTime.TryParse(lastModifiedStr, out lastModified);

                    // 检查缓存
                    var cacheKey = target;
                    bool hasValidCache = false;
                    CachedProxyResponse cached = null;
                    lock (_proxyCacheLock)
                    {
                        if (_proxyCache.TryGetValue(cacheKey, out cached))
                        {
                            if (cached.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                            {
                                // 检查 ETag/Last-Modified
                                bool etagMatch = !string.IsNullOrEmpty(etag) && cached.ETag == etag;
                                bool lmMatch = lastModified != DateTime.MinValue && cached.LastModified == lastModified;
                                if (etagMatch || lmMatch)
                                {
                                    hasValidCache = true;
                                }
                            }
                        }
                    }

                    if (hasValidCache)
                    {
                        // 返回 304 Not Modified
                        res.StatusCode = 304;
                        res.AddHeader("ETag", cached.ETag);
                        if (cached.LastModified != DateTime.MinValue)
                            res.AddHeader("Last-Modified", cached.LastModified.ToString("R"));
                        res.AddHeader("Cache-Control", "max-age=3600");
                        res.AddHeader("Access-Control-Allow-Origin", "*");
                        res.ContentLength64 = 0;
                        res.OutputStream.Close();
                        return;
                    }

                    if (response.ContentLength > ProxyMaxBytes)
                    {
                        res.StatusCode = 413;
                        WriteJson(res, L10n.T("{\"error\":\"响应过大\"}"));
                        return;
                    }
                    var buf = new MemoryStream();
                    using (var rs = response.GetResponseStream())
                    {
                        var tmp = new byte[8192];
                        long total = 0;
                        int read;
                        while ((read = rs.Read(tmp, 0, tmp.Length)) > 0)
                        {
                            total += read;
                            if (total > ProxyMaxBytes) { res.StatusCode = 413; WriteJson(res, L10n.T("{\"error\":\"响应过大\"}")); return; }
                            buf.Write(tmp, 0, read);
                        }
                    }
                    var outBytes = buf.ToArray();

                    // 更新缓存
                    var newCached = new CachedProxyResponse
                    {
                        Data = outBytes,
                        ContentType = ct,
                        ETag = etag,
                        LastModified = lastModified,
                        CachedAt = DateTime.UtcNow,
                        ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ProxyCacheTtlSeconds
                    };
                    lock (_proxyCacheLock)
                    {
                        _proxyCache[cacheKey] = newCached;
                        // 清理过期缓存
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var expired = _proxyCache.Where(kvp => kvp.Value.ExpiresAt < now).Select(kvp => kvp.Key).ToList();
                        foreach (var key in expired) _proxyCache.Remove(key);
                        // 限制缓存大小
                        if (_proxyCache.Count * ProxyMaxBytes > ProxyCacheMaxSize)
                        {
                            var oldest = _proxyCache.OrderBy(kvp => kvp.Value.CachedAt).First();
                            _proxyCache.Remove(oldest.Key);
                        }
                    }

                    res.StatusCode = (int)response.StatusCode;
                    res.ContentType = ct;
                    res.ContentLength64 = outBytes.Length;
                    res.AddHeader("ETag", etag ?? "");
                    if (lastModified != DateTime.MinValue)
                        res.AddHeader("Last-Modified", lastModified.ToString("R"));
                    res.AddHeader("Cache-Control", "max-age=3600");
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    using (var s = res.OutputStream) s.Write(outBytes, 0, outBytes.Length);
            }
            }
            catch (WebException wex)
            {
                // 把上游错误透传给前端，便于书源调试
                try
                {
                    if (wex.Response is HttpWebResponse hwr)
                    {
                        var ct = hwr.ContentType ?? "application/octet-stream";
                        using (var rs = hwr.GetResponseStream())
                        using (var ms = new MemoryStream())
                        {
                            rs.CopyTo(ms);
                            var b = ms.ToArray();
                            res.StatusCode = (int)hwr.StatusCode;
                            res.ContentType = ct;
                            res.ContentLength64 = b.Length;
                            using (var s = res.OutputStream) s.Write(b, 0, b.Length);
                            return;
                        }
                    }
                    res.StatusCode = 502;
                    WriteJson(res, "{\"error\":" + Escape(wex.Message) + "}");
                }
                catch { try { res.Abort(); } catch { } }
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                WriteJson(res, "{\"error\":" + Escape(ex.Message) + "}");
            }
        }

        /// <summary>
        /// 把外部站点经本机中转，使 iframe 与当前站点同源，从而可注入 Web Bluetooth Polyfill 等脚本。
        /// 功能：
        ///   - HTML 重写：相对链接、资源 URL (href/src/action 等) 指回 /__proxy/
        ///   - CSP 调整：移除 frame-ancestors 限制、允许内联脚本
        ///   - 资源类型透传：CSS/JS/图片/字体等直接透传（不重写），由浏览器再次请求代理
        ///   - SSRF 防护：禁止访问内网/本机地址
        ///   - 大小限制：默认 20MB，防止大文件阻塞
        /// </summary>
        private const long BrowserProxyMaxBytes = 20L * 1024 * 1024; // 20MB

        private void ServeBrowserProxy(HttpListenerContext ctx, HttpListenerRequest req, string path)
        {
            var res = ctx.Response;
            // 路径格式：/__proxy/<encoded_url>
            var encodedPath = path.Substring("/__proxy/".Length);
            if (string.IsNullOrEmpty(encodedPath))
            {
                res.StatusCode = 400;
                WriteText(res, L10n.T("缺少目标网址"));
                return;
            }

            string target;
            try
            {
                target = System.Uri.UnescapeDataString(encodedPath);
            }
            catch
            {
                res.StatusCode = 400;
                WriteText(res, L10n.T("网址编码无效"));
                return;
            }

            if (!System.Uri.TryCreate(target, System.UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    res.StatusCode = 400;
                    WriteText(res, L10n.T("仅支持 http/https 网址"));
                    return;
                }

                try
                {

                if (IsPrivateHost(uri.Host))
                {
                    res.StatusCode = 403;
                    WriteText(res, L10n.T("禁止访问内网/本机地址"));
                    return;
                }

                // 只允许 GET/HEAD（浏览器 iframe 导航通常是 GET）
                if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD")
                {
                    res.StatusCode = 405;
                    WriteText(res, L10n.T("仅支持 GET/HEAD"));
                    return;
                }

                // 代理基础路径和基础 URI（用于重写 Location 头）
                var baseUri = new System.Uri(target);
                var proxyBase = "/__proxy/";

                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = req.HttpMethod;
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 5;
                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                
                // 使用现代浏览器 UA，避免某些站点返回移动版或拦截
                request.UserAgent = "Mozilla/5.0 (Linux; Android 13; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";
                request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8";
                request.Headers["Accept-Encoding"] = "gzip, deflate, br";
                request.Headers["Upgrade-Insecure-Requests"] = "1";
                request.Headers["Sec-Fetch-Site"] = "none";
                request.Headers["Sec-Fetch-Mode"] = "navigate";
                request.Headers["Sec-Fetch-User"] = "?1";
                request.Headers["Sec-Fetch-Dest"] = "document";
                request.Headers["Cache-Control"] = "max-age=0";

                // 透传 Referer（如果有）
                var referer = req.Headers["Referer"];
                if (!string.IsNullOrEmpty(referer))
                    request.Referer = referer;

                // 透传 Cookie（如果有，用于登录态保持）
                var requestCookie = req.Headers["Cookie"];
                if (!string.IsNullOrEmpty(requestCookie))
                    request.Headers["Cookie"] = requestCookie;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var ct = response.ContentType ?? "text/html; charset=utf-8";
                    var isHtml = ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                                 ct.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase);

                    if (response.ContentLength > BrowserProxyMaxBytes)
                    {
                        res.StatusCode = 413;
                        WriteText(res, L10n.T("响应内容过大（>20MB）"));
                        return;
                    }

                    var buf = new MemoryStream();
                    using (var rs = response.GetResponseStream())
                    {
                        var tmp = new byte[8192];
                        long total = 0;
                        int read;
                        while ((read = rs.Read(tmp, 0, tmp.Length)) > 0)
                        {
                            total += read;
                            if (total > BrowserProxyMaxBytes)
                            {
                                res.StatusCode = 413;
                                WriteText(res, L10n.T("响应内容过大（>20MB）"));
                                return;
                            }
                            buf.Write(tmp, 0, read);
                        }
                    }

                    var outBytes = buf.ToArray();
                    res.StatusCode = (int)response.StatusCode;

                    if (isHtml)
                    {
                        // HTML 重写：把相对链接、资源 URL 改写为指向代理
                        var html = Encoding.UTF8.GetString(outBytes);
                        html = RewriteHtml(html, target);
                        outBytes = Encoding.UTF8.GetBytes(html);
                        res.ContentType = "text/html; charset=utf-8";
                    }
                    else
                    {
                        // 非 HTML 资源直接透传
                        res.ContentType = ct;
                    }

                    res.ContentLength64 = outBytes.Length;
                    
                    // 关键：允许 iframe 嵌入，移除 X-Frame-Options 和 CSP frame-ancestors
                    res.Headers.Remove("X-Frame-Options");
                    res.Headers.Remove("Content-Security-Policy");
                    res.Headers.Remove("Content-Security-Policy-Report-Only");
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    res.AddHeader("Access-Control-Allow-Credentials", "true");
                    res.AddHeader("X-Content-Type-Options", "nosniff");

                    // 透传上游的 Set-Cookie（保持登录态）
                    foreach (string key in response.Headers)
                    {
                        if (string.Equals(key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                        {
                            var cookies = response.Headers.GetValues(key);
                            if (cookies != null)
                            {
                                foreach (var cookie in cookies)
                                {
                                    res.Headers.Add("Set-Cookie", cookie);
                                }
                            }
                        }
                        // 重写重定向 Location 头，使其走代理
                        else if (string.Equals(key, "Location", StringComparison.OrdinalIgnoreCase))
                        {
                            var locations = response.Headers.GetValues(key);
                            if (locations != null)
                            {
                                foreach (var loc in locations)
                                {
                                    try
                                    {
                                        var abs = new System.Uri(baseUri, loc);
                                        if (abs.Scheme == "http" || abs.Scheme == "https")
                                        {
                                            var encoded = System.Uri.EscapeDataString(abs.AbsoluteUri);
                                            res.Headers.Add("Location", proxyBase + encoded);
                                        }
                                        else
                                        {
                                            res.Headers.Add("Location", loc);
                                        }
                                    }
                                    catch
                                    {
                                        res.Headers.Add("Location", loc);
                                    }
                                }
                            }
                        }
                    }

                    using (var s = res.OutputStream) s.Write(outBytes, 0, outBytes.Length);
            }
            }
            catch (WebException wex)
            {
                // 上游错误透传
                var baseUri = new System.Uri(target);
                var proxyBase = "/__proxy/";
                try
                {
                    if (wex.Response is HttpWebResponse hwr)
                    {
                        var ct = hwr.ContentType ?? "text/plain";
                        using (var rs = hwr.GetResponseStream())
                        using (var ms = new MemoryStream())
                        {
                            rs.CopyTo(ms);
                            var b = ms.ToArray();
                            res.StatusCode = (int)hwr.StatusCode;
                            res.ContentType = ct;
                            res.ContentLength64 = b.Length;
                            res.Headers.Remove("X-Frame-Options");
                            res.Headers.Remove("Content-Security-Policy");
                            res.Headers.Remove("Content-Security-Policy-Report-Only");
                            res.AddHeader("Access-Control-Allow-Origin", "*");
                            res.AddHeader("Access-Control-Allow-Credentials", "true");

                            // 透传上游的 Set-Cookie
                            foreach (string key in hwr.Headers)
                            {
                                if (string.Equals(key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                                {
                                    var cookies = hwr.Headers.GetValues(key);
                                    if (cookies != null)
                                    {
                                        foreach (var cookie in cookies)
                                        {
                                            res.Headers.Add("Set-Cookie", cookie);
                                        }
                                    }
                                }
                                // 重写重定向 Location 头
                                else if (string.Equals(key, "Location", StringComparison.OrdinalIgnoreCase))
                                {
                                    var locations = hwr.Headers.GetValues(key);
                                    if (locations != null)
                                    {
                                        foreach (var loc in locations)
                                        {
                                            try
                                            {
                                                var abs = new System.Uri(baseUri, loc);
                                                if (abs.Scheme == "http" || abs.Scheme == "https")
                                                {
                                                    var encoded = System.Uri.EscapeDataString(abs.AbsoluteUri);
                                                    res.Headers.Add("Location", proxyBase + encoded);
                                                }
                                                else
                                                {
                                                    res.Headers.Add("Location", loc);
                                                }
                                            }
                                            catch
                                            {
                                                res.Headers.Add("Location", loc);
                                            }
                                        }
                                    }
                                }
                            }

                            using (var s = res.OutputStream) s.Write(b, 0, b.Length);
                            return;
                        }
                    }
                    res.StatusCode = 502;
                    WriteText(res, L10n.T("代理连接失败：") + wex.Message);
                }
                catch { try { res.Abort(); } catch { } }
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                WriteText(res, L10n.T("代理服务器错误：") + ex.Message);
            }
        }

        /// <summary>
        /// 重写 HTML 内容，将相对 URL 和资源链接指向代理。
        /// 处理：<a href>, <form action>, <link href>, <script src>, <img src>, <iframe src>, <video src>, <audio src>, <source src>
        /// 以及 CSS 中的 url()、JS 中的动态 URL（基础处理）
        /// </summary>
        private string RewriteHtml(string html, string baseUrl)
        {
            if (string.IsNullOrEmpty(html)) return html;

            try
            {
                var baseUri = new System.Uri(baseUrl);
                var proxyBase = "/__proxy/";

                // 1. 处理 <base href> - 如果有 base 标签，用它作为基准
                var baseMatch = Regex.Match(html, @"<base\s+href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (baseMatch.Success)
                {
                    try { baseUri = new System.Uri(baseUri, baseMatch.Groups[1].Value); } catch { }
                }

                // 2. 重写属性：href, src, action, data-src, poster, background
                // 匹配：属性名="值" 或 属性名='值'
                html = Regex.Replace(html, @"\b(href|src|action|data-src|poster|background)\s*=\s*([""'])([^""']+)\2",
                    (match) =>
                    {
                        var attr = match.Groups[1].Value;
                        var quote = match.Groups[2].Value;
                        var url = match.Groups[3].Value;

                        // 跳过 data:, javascript:, mailto:, tel:, #, blob: 等
                        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("#"))
                            return match.Value;

                        // 解析为绝对 URL
                        System.Uri absolute;
                        try
                        {
                            absolute = new System.Uri(baseUri, url);
                        }
                        catch
                        {
                            return match.Value;
                        }

                        // 只代理 http/https
                        if (absolute.Scheme != "http" && absolute.Scheme != "https")
                            return match.Value;

                        // 编码并生成代理 URL
                        var encoded = System.Uri.EscapeDataString(absolute.AbsoluteUri);
                        var newUrl = proxyBase + encoded;

                        return attr + "=" + quote + newUrl + quote;
                    },
                    RegexOptions.IgnoreCase);

                // 3. 重写 meta refresh: <meta http-equiv="refresh" content="0;url=...">
                html = Regex.Replace(html, @"<meta\s+http-equiv\s*=\s*[""']refresh[""']\s+content\s*=\s*([""'])([^""']+)\1",
                    (match) =>
                    {
                        var quote = match.Groups[1].Value;
                        var content = match.Groups[2].Value;
                        var parts = content.Split(';');
                        if (parts.Length >= 2)
                        {
                            var urlPart = parts[1].Trim();
                            if (urlPart.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                            {
                                var url = urlPart.Substring(4).Trim();
                                System.Uri absolute;
                                try { absolute = new System.Uri(baseUri, url); } catch { return match.Value; }
                                if (absolute.Scheme == "http" || absolute.Scheme == "https")
                                {
                                    var encoded = System.Uri.EscapeDataString(absolute.AbsoluteUri);
                                    var newUrl = proxyBase + encoded;
                                    return $"<meta http-equiv=\"refresh\" content=\"{parts[0]};url={newUrl}\"";
                                }
                            }
                        }
                        return match.Value;
                    },
                    RegexOptions.IgnoreCase);

                // 4. 移除/调整 CSP：frame-ancestors 限制会阻止 iframe 嵌入
                html = Regex.Replace(html, @"<meta\s+http-equiv\s*=\s*[""']Content-Security-Policy[""']\s+content\s*=\s*([""'])[^""']*\1",
                    (match) =>
                    {
                        var quote = match.Groups[1].Value;
                        var content = match.Value.Substring(match.Value.IndexOf(quote) + 1);
                        content = content.Substring(0, content.LastIndexOf(quote));
                        // 移除 frame-ancestors
                        content = Regex.Replace(content, @"frame-ancestors\s+[^;]+;?", "", RegexOptions.IgnoreCase);
                        // 如果为空则移除整个标签
                        if (string.IsNullOrWhiteSpace(content))
                            return "";
                        return $"<meta http-equiv=\"Content-Security-Policy\" content={quote}{content}{quote}";
                    },
                    RegexOptions.IgnoreCase);

                // 5. 注入 base 标签（如果没有），指向原站点，确保相对资源正常加载
                if (!Regex.IsMatch(html, @"<base\s+href", RegexOptions.IgnoreCase))
                {
                    var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                    if (headEnd >= 0)
                    {
                        var baseTag = $"<base href=\"{baseUri.AbsoluteUri}\">";
                        html = html.Insert(headEnd, baseTag);
                    }
                }

                // 6. 注入脚本：拦截导航、表单提交、AJAX，强制走代理
                var injectScript = $@"
<script>
(function() {{
  var PROXY_BASE = '/__proxy/';
  var ORIGIN = '{baseUri.GetLeftPart(System.UriPartial.Authority).Replace("'", "\\'")}';

  // 拦截 a 标签点击
  document.addEventListener('click', function(e) {{
    var a = e.target.closest('a');
    if (!a || a.target === '_blank') return;
    var href = a.getAttribute('href');
    if (!href) return;
    if (href.startsWith('javascript:') || href.startsWith('mailto:') || href.startsWith('tel:') || href.startsWith('#') || href.startsWith('data:')) return;
    try {{
      var abs = new URL(href, document.baseURI).href;
      if (abs.startsWith('http://') || abs.startsWith('https://')) {{
        e.preventDefault();
        location.href = PROXY_BASE + encodeURIComponent(abs);
      }}
    }} catch {{}}
  }}, true);

  // 拦截 form 提交
  document.addEventListener('submit', function(e) {{
    var form = e.target;
    var action = form.getAttribute('action') || location.href;
    try {{
      var abs = new URL(action, document.baseURI).href;
      if (abs.startsWith('http://') || abs.startsWith('https://')) {{
        e.preventDefault();
        var data = new FormData(form);
        var params = new URLSearchParams(data).toString();
        var method = (form.getAttribute('method') || 'GET').toUpperCase();
        if (method === 'GET') {{
          location.href = PROXY_BASE + encodeURIComponent(abs + (abs.includes('?') ? '&' : '?') + params);
        }} else {{
          // POST 需要特殊处理，这里简单跳转到 GET（实际项目可用 fetch + 重写）
          location.href = PROXY_BASE + encodeURIComponent(abs);
        }}
      }}
    }} catch {{}}
  }}, true);

  // 拦截 window.open
  var _open = window.open;
  window.open = function(url, target, features) {{
    if (!url) return _open.call(this, url, target, features);
    try {{
      var abs = new URL(url, document.baseURI).href;
      if (abs.startsWith('http://') || abs.startsWith('https://')) {{
        return _open.call(this, PROXY_BASE + encodeURIComponent(abs), target, features);
      }}
    }} catch {{}}
    return _open.call(this, url, target, features);
  }};
}})();
</script>";
                
                // 注入到 </head> 前或 </body> 前
                var headEndIdx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (headEndIdx >= 0)
                {
                    html = html.Insert(headEndIdx, injectScript);
                }
                else
                {
                    var bodyEndIdx = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyEndIdx >= 0)
                        html = html.Insert(bodyEndIdx, injectScript);
                }
            }
            catch
            {
                // 重写失败时返回原 HTML，避免页面完全白屏
            }

            return html;
        }

        /// <summary>DNS API 处理器</summary>
        private void ServeDnsApi(HttpListenerContext ctx, HttpListenerRequest req, string path)
        {
            var res = ctx.Response;
            var mgr = AhoChatA.DnsCore.DnsManager.Instance;
            
            try
            {
                // GET /api/dns/config - 获取配置
                if (path.Equals("/api/dns/config", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "GET")
                {
                    WriteJson(res, Json(new { ok = true, config = mgr.GetConfig(), autoStart = mgr.AutoStart }));
                    return;
                }

                // POST /api/dns/config - 更新配置
                if (path.Equals("/api/dns/config", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    var body = ReadBody(req);
                    var update = new JObject();
                    foreach (var property in JObject.Parse(body).Properties())
                        update[char.ToUpperInvariant(property.Name[0]) + property.Name.Substring(1)] = property.Value;
                    
                    if (update["AutoStart"] != null) mgr.AutoStart = update["AutoStart"].Value<bool>();
                    mgr.UpdateConfig(cfg =>
                    {
                        if (update["Enabled"] != null) cfg.Enabled = update["Enabled"].Value<bool>();
                        if (update["Port"] != null) cfg.Port = update["Port"].Value<int>();
                        if (update["UpstreamServers"] != null) cfg.UpstreamServers = update["UpstreamServers"].ToObject<List<string>>();
                        if (update["UpstreamTimeoutMs"] != null) cfg.UpstreamTimeoutMs = update["UpstreamTimeoutMs"].Value<int>();
                        if (update["EnableCache"] != null) cfg.EnableCache = update["EnableCache"].Value<bool>();
                        if (update["CacheSize"] != null) cfg.CacheSize = update["CacheSize"].Value<int>();
                        if (update["CacheTtlSeconds"] != null) cfg.CacheTtlSeconds = update["CacheTtlSeconds"].Value<int>();
                        if (update["BlockAds"] != null) cfg.BlockAds = update["BlockAds"].Value<bool>();
                        if (update["BlockTrackers"] != null) cfg.BlockTrackers = update["BlockTrackers"].Value<bool>();
                        if (update["BlockMalware"] != null) cfg.BlockMalware = update["BlockMalware"].Value<bool>();
                        if (update["CustomBlocklists"] != null) cfg.CustomBlocklists = update["CustomBlocklists"].ToObject<List<string>>();
                        if (update["CustomAllowlists"] != null) cfg.CustomAllowlists = update["CustomAllowlists"].ToObject<List<string>>();
                        if (update["Rewrites"] != null) cfg.Rewrites = update["Rewrites"].ToObject<List<AhoChatA.DnsCore.DnsRewrite>>();
                        if (update["LogQueries"] != null) cfg.LogQueries = update["LogQueries"].Value<bool>();
                        if (update["MaxLogEntries"] != null) cfg.MaxLogEntries = update["MaxLogEntries"].Value<int>();
                        if (update["PreferSecureUpstream"] != null) cfg.PreferSecureUpstream = update["PreferSecureUpstream"].Value<bool>();
                    });
                    
                    WriteJson(res, Json(new { ok = true, config = mgr.GetConfig(), autoStart = mgr.AutoStart }));
                    return;
                }

                // GET /api/dns/stats - 获取统计
                if (path.Equals("/api/dns/stats", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "GET")
                {
                    var stats = mgr.GetStats();
                    if (stats != null)
                    {
                        WriteJson(res, Json(new { ok = true, stats }));
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无统计数据。请先在 DNS 管理页点击“启动”按钮。") }));
                    }
                    return;
                }

                // GET /api/dns/log - 获取查询日志
                if (path.Equals("/api/dns/log", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "GET")
                {
                    string q = req.Url.Query;
                    int limit = 100;
                    if (!string.IsNullOrEmpty(q))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(q, @"[?&]limit=(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var l))
                            limit = Math.Min(l, 1000);
                    }
                    
                    var logs = mgr.GetQueryLog(limit);
                    if (logs != null)
                    {
                        WriteJson(res, Json(new { ok = true, logs }));
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无查询日志。这是正常现象——没启动当然没日志。请先启动 DNS 服务。"), logs = new List<object>() }));
                    }
                    return;
                }

                // POST /api/dns/block - 添加屏蔽域名
                if (path.Equals("/api/dns/block", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    var body = ReadBody(req);
                    var data = JObject.Parse(body);
                    string domain = data["domain"]?.Value<string>();
                    if (!string.IsNullOrEmpty(domain))
                    {
                        var server = mgr.GetDnsServer();
                        if (server != null)
                        {
                            server.AddBlockedDomain(domain);
                            WriteJson(res, Json(new { ok = true }));
                        }
                        else
                        {
                            WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无法添加屏蔽域名。请先启动 DNS 服务。") }));
                        }
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("缺少 domain 参数") }));
                    }
                    return;
                }

                // POST /api/dns/unblock - 移除屏蔽域名
                if (path.Equals("/api/dns/unblock", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    var body = ReadBody(req);
                    var data = JObject.Parse(body);
                    string domain = data["domain"]?.Value<string>();
                    if (!string.IsNullOrEmpty(domain))
                    {
                        var server = mgr.GetDnsServer();
                        if (server != null)
                        {
                            server.RemoveBlockedDomain(domain);
                            WriteJson(res, Json(new { ok = true }));
                        }
                        else
                        {
                            WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无法移除屏蔽域名。请先启动 DNS 服务。") }));
                        }
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("缺少 domain 参数") }));
                    }
                    return;
                }

                // POST /api/dns/allow - 添加允许域名
                if (path.Equals("/api/dns/allow", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    var body = ReadBody(req);
                    var data = JObject.Parse(body);
                    string domain = data["domain"]?.Value<string>();
                    if (!string.IsNullOrEmpty(domain))
                    {
                        var server = mgr.GetDnsServer();
                        if (server != null)
                        {
                            server.AddAllowedDomain(domain);
                            WriteJson(res, Json(new { ok = true }));
                        }
                        else
                        {
                            WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无法添加允许域名。请先启动 DNS 服务。") }));
                        }
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("缺少 domain 参数") }));
                    }
                    return;
                }

                // POST /api/dns/rewrite - 添加重写规则
                if (path.Equals("/api/dns/rewrite", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    var body = ReadBody(req);
                    var data = JObject.Parse(body);
                    string domain = data["domain"]?.Value<string>();
                    string answer = data["answer"]?.Value<string>();
                    if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(answer))
                    {
                        var server = mgr.GetDnsServer();
                        if (server != null)
                        {
                            server.AddRewrite(new AhoChatA.DnsCore.DnsRewrite { Domain = domain, Answer = answer, Enabled = true });
                            WriteJson(res, Json(new { ok = true }));
                        }
                        else
                        {
                            WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无法添加重写规则。请先启动 DNS 服务。") }));
                        }
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("缺少 domain 或 answer 参数") }));
                    }
                    return;
                }

                // POST /api/dns/rewrite/remove - 移除重写规则
                if (path.Equals("/api/dns/rewrite/remove", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    var body = ReadBody(req);
                    var data = JObject.Parse(body);
                    string domain = data["domain"]?.Value<string>();
                    if (!string.IsNullOrEmpty(domain))
                    {
                        var server = mgr.GetDnsServer();
                        if (server != null)
                        {
                            server.RemoveRewrite(domain);
                            WriteJson(res, Json(new { ok = true }));
                        }
                        else
                        {
                            WriteJson(res, Json(new { ok = false, error = L10n.T("DNS 服务未启动，无法移除重写规则。请先启动 DNS 服务。") }));
                        }
                    }
                    else
                    {
                        WriteJson(res, Json(new { ok = false, error = L10n.T("缺少 domain 参数") }));
                    }
                    return;
                }

                // POST /api/dns/start - 启动 VPN DNS
                if (path.Equals("/api/dns/start", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    if (mgr.RequiresVpnConsent)
                    {
                        res.StatusCode = 409;
                        WriteJson(res, Json(new { ok = false, requiresConsent = true, error = L10n.T("请先在 Android 设备上授权 DNS VPN") }));
                        return;
                    }
                    mgr.StartVpn();
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                // POST /api/dns/stop - 停止 VPN DNS
                if (path.Equals("/api/dns/stop", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "POST")
                {
                    mgr.StopVpn();
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }

                // GET /api/dns/status - 获取运行状态
                if (path.Equals("/api/dns/status", StringComparison.OrdinalIgnoreCase) && 
                    req.HttpMethod == "GET")
                {
                    WriteJson(res, Json(new
                    {
                        ok = true,
                        running = mgr.IsVpnRunning,
                        serverRunning = mgr.GetDnsServer() != null,
                        port = mgr.GetConfig().Port,
                        mode = "Android VPN"
                    }));
                    return;
                }

                res.StatusCode = 404;
                WriteJson(res, "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                WriteJson(res, "{\"error\":" + Escape(ex.Message) + "}");
            }
        }

        /// <summary>SSRF 防护：解析主机名并判断是否为本机/内网地址，禁止代理访问。</summary>
        private static bool IsPrivateHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(host, out var ip))
                return IsPrivateAddress(ip);
            try
            {
                // 断网时域名解析可阻塞几十秒；这里最多等 1.2 秒，超时按「不可信」拒绝，
                // 既避免网页代理请求把 HTTP 工作线程全部堵死，也不放松 SSRF 边界。
                foreach (var addr in MchatBridge.ResolveHostBounded(host, 1200))
                    if (IsPrivateAddress(addr)) return true;
            }
            catch { return true; }
            return false;
        }

        private static bool IsPrivateAddress(System.Net.IPAddress ip)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return true;
                if (b[0] == 127) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
            }
            return false;
        }

        /* ===================== 静态资源 ===================== */

        // 从 APK 内 Assets/webui 直接提供网页端静态资源（与 ServeAppStatic 同理，
        // 不依赖 FilesDir/webui 解压目录），解压目录作为兼容回退。
        private void ServeStatic(HttpListenerContext ctx, string path)
        {
            var res = ctx.Response;
            if (path == "/" || string.IsNullOrEmpty(path))
                path = "/index.html";

            // 防目录穿越
            var rel = Sanitize(path).TrimStart('/');

            // 1) 优先从 APK assets 直接读取
            var assetPath = "webui/" + rel;
            var assetStream = OpenAsset(assetPath);
            if (assetStream != null) { SendAsset(ctx, assetStream, assetPath); return; }

            // 2) 兼容：解压目录 FilesDir/webui 中的文件
            var file = Path.Combine(webuiDir, rel);
            if (file.StartsWith(webuiDir, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            { SendFile(ctx, file); return; }

            // 3) 无扩展名/目录型路径：不再回退到聊天 SPA（那样会让「不存在的链接」误显示为聊天）。
            //    页面不存在时返回空白页（404 + 空 HTML），浏览器渲染为纯白空白页。
            bool hasExt = !string.IsNullOrEmpty(Path.GetExtension(rel));
            if (!hasExt || rel.EndsWith("/"))
            {
                ServeBlankPage(res);
                return;
            }

            res.StatusCode = 404;
            WriteText(res, "404 Not Found");
        }

        /* ===================== 浏览器文档阅读器（阅读模块 Web 版） ===================== */

        /// <summary>
        /// 服务 /viewer/*.html 与同目录 js/css。用 cookie vwdoc 记住当前文档路径，
        /// 使 viewer 前端硬编码的 fetch("/doc/file.docx") 无需改动即可取到文档字节。
        /// 同时把 ?name=&amp;ext= 原样保留给前端 JS 读取。
        /// </summary>
        private void ServeViewerStatic(HttpListenerContext ctx, string path)
        {
            var res = ctx.Response;
            var rel = Sanitize(path).TrimStart('/');       // viewer/docx.html
            var shortRel = rel.StartsWith("viewer/", StringComparison.OrdinalIgnoreCase)
                ? rel.Substring("viewer/".Length) : rel;   // docx.html

            // 记住文档路径（供 /doc/file.* 读取）。只在打开 html 时写 cookie。
            if (shortRel.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                var p = GetQuery(ctx.Request.Url.Query, "path");
                if (!string.IsNullOrEmpty(p))
                {
                    res.Headers["Set-Cookie"] = "vwdoc=" + System.Uri.EscapeDataString(p) + "; Path=/; SameSite=Lax";
                }
            }

            var assetPath = "viewer/" + shortRel;
            var stream = OpenAsset(assetPath);
            if (stream != null)
            {
                try
                {
                    using (stream)
                    {
                        byte[] bytes = ReadFully(stream);
                        // HTML 注入统一中文字体栈，提升手机上 docx/xlsx/pptx 的可读性；
                        // 并放开 CSP 的 connect-src 允许 blob:，使浏览器本地文件（?src=blob:）
                        // 能被前端直接 fetch，无需上传 app。
                        if (shortRel.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        {
                            var html = ReaderFonts.Inject(Encoding.UTF8.GetString(bytes));
                            html = html.Replace("connect-src 'self'", "connect-src 'self' blob:");
                            bytes = Encoding.UTF8.GetBytes(html);
                        }
                        res.ContentType = MimeOf(assetPath);
                        if (res.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                            || res.ContentType.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase)
                            || res.ContentType.StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
                            res.ContentType += "; charset=utf-8";
                        res.ContentLength64 = bytes.Length;
                        res.AddHeader("Cache-Control", "no-store");
                        using (var os = res.OutputStream) os.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
                catch { try { res.Abort(); } catch { } return; }
            }

            // 兼容回退：解压目录
            var file = Path.Combine(Android.App.Application.Context.FilesDir.AbsolutePath, "viewer", shortRel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) { SendFile(ctx, file); return; }

            res.StatusCode = 404;
            WriteText(res, "404 Not Found");
        }

        /// <summary>
        /// Web 阅读器上传浏览器本地文件：POST 原始字节 + ?name=，存入临时目录，
        /// 返回 token 供 /viewer/*.html?path=tmp:TOKEN 打开。仅临时保存，不入书架。
        /// </summary>
        private static string DocTmpDir => Path.Combine(
            Android.App.Application.Context.FilesDir.AbsolutePath, "doc-tmp");

        private void ServeDocApi(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Cache-Control", "no-store");
            try
            {
                if (path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    string name = GetQuery(req.Url.Query, "name") ?? "file";
                    string ext = System.IO.Path.GetExtension(name);
                    if (ext.Length > 10) ext = "";
                    string token = Guid.NewGuid().ToString("N") + ext;
                    Directory.CreateDirectory(DocTmpDir);
                    string file = Path.Combine(DocTmpDir, token);
                    using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); File.WriteAllBytes(file, ms.ToArray()); }
                    WriteJson(res, Json(new { ok = true, token = token, path = "tmp:" + token, name = name }));
                    return;
                }
                if (path.EndsWith("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    try { foreach (var f in Directory.GetFiles(DocTmpDir)) File.Delete(f); } catch { }
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }
            }
            catch (Exception ex) { WriteJson(res, Json(new { ok = false, error = ex.Message })); return; }
            res.StatusCode = 404;
            WriteText(res, "404 Not Found");
        }

        /// <summary>
        /// 服务 /doc/file.* 文档字节：优先 cookie vwdoc（viewer 前端硬编码路径），兼容 ?path=。
        /// path 可为 /__media/xxx、share:xxx、tmp:TOKEN（Web 临时上传）或 media 目录里的文件名。
        /// </summary>
        /// <summary>
        /// 流式把文件写到响应，支持 HTTP Range（206 分片）。大文档/PDF 不再整本 ReadAllBytes 进内存，
        /// 前端 pdf.mjs 的按需加载也能真正生效。
        /// </summary>
        private static void StreamFileToResponse(HttpListenerResponse res, HttpListenerRequest req,
                                                 string file, string contentType)
        {
            var info = new FileInfo(file);
            long len = info.Length;
            long start = 0, end = len - 1;
            bool ranged = false;
            var rangeHeader = req.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                var m = System.Text.RegularExpressions.Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
                if (m.Success && len > 0)
                {
                    long.TryParse(m.Groups[1].Value, out start);
                    string endStr = m.Groups[2].Value;
                    if (!string.IsNullOrEmpty(endStr)) long.TryParse(endStr, out end);
                    if (start >= len) { res.StatusCode = 416; return; }
                    if (end >= len || end < start) end = len - 1;
                    ranged = true;
                }
            }
            res.ContentType = contentType;
            res.AddHeader("Accept-Ranges", "bytes");
            res.AddHeader("Content-Disposition", "inline");
            res.AddHeader("Cache-Control", "no-store");
            if (ranged)
            {
                res.StatusCode = 206;
                res.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + len);
                res.ContentLength64 = end - start + 1;
            }
            else res.ContentLength64 = len;

            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           64 * 1024, FileOptions.SequentialScan))
            {
                if (ranged && start > 0) fs.Seek(start, SeekOrigin.Begin);
                long remaining = end - start + 1;
                byte[] buf = new byte[64 * 1024];
                using (var outStream = res.OutputStream)
                {
                    while (remaining > 0)
                    {
                        int want = (int)System.Math.Min(buf.Length, remaining);
                        int read = fs.Read(buf, 0, want);
                        if (read <= 0) break;
                        outStream.Write(buf, 0, read);
                        remaining -= read;
                    }
                }
            }
        }

        private void ServeDocFile(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            string path = GetQuery(req.Url.Query, "path");
            if (string.IsNullOrEmpty(path))
            {
                var ck = req.Headers["Cookie"] ?? "";
                foreach (var part in ck.Split(';'))
                {
                    var kv = part.Trim();
                    if (kv.StartsWith("vwdoc=", StringComparison.OrdinalIgnoreCase))
                    { path = Android.Net.Uri.Decode(kv.Substring(6)); break; }
                }
            }
            if (string.IsNullOrEmpty(path)) { res.StatusCode = 400; WriteText(res, L10n.T("未指定文档")); return; }

            path = path.TrimStart('/');
            // book: 前缀 = 书架里的「文档书」，按其 id 取回原始文件路径
            if (path.StartsWith("book:", StringComparison.OrdinalIgnoreCase))
            {
                string bid = path.Substring(5);
                AhoChatA.Reader.ShelfItem book = null;
                foreach (var it in AhoChatA.Reader.ReaderLibrary.Shelf())
                    if (string.Equals(it.Id, bid, StringComparison.Ordinal)) { book = it; break; }
                if (book == null || string.IsNullOrEmpty(book.Path) || !File.Exists(book.Path))
                { res.StatusCode = 404; WriteText(res, L10n.T("书籍不存在")); return; }
                try { StreamFileToResponse(res, req, book.Path, "application/octet-stream"); }
                catch { try { res.Abort(); } catch { } }
                return;
            }
            // tmp: 前缀 = Web 阅读器从浏览器本地上传的临时文件
            bool isTmp = path.StartsWith("tmp:", StringComparison.OrdinalIgnoreCase);
            if (isTmp) path = path.Substring(4);
            // share: 前缀 = 文件共享目录（/file 列表里的文档）；否则默认聊天 media 目录
            string baseDir = isTmp ? DocTmpDir : mediaDir;
            if (path.StartsWith("share:", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(6);
                baseDir = LanShareService.EnsureShareRoot();
            }
            if (path.StartsWith("__media/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring("__media/".Length);
            var name = Sanitize(path);
            if (string.IsNullOrEmpty(name)) { res.StatusCode = 400; WriteText(res, L10n.T("非法路径")); return; }

            var file = Path.Combine(baseDir, name);
            try
            {
                var full = Path.GetFullPath(file);
                var root = Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                { res.StatusCode = 404; WriteText(res, L10n.T("文件不存在")); return; }
            }
            catch { res.StatusCode = 400; WriteText(res, L10n.T("非法路径")); return; }

            try { StreamFileToResponse(res, req, file, MimeOf(name)); }
            catch { try { res.Abort(); } catch { } }
        }

        /* ===================== 资源读取（APK assets 优先） ===================== */

        // 打开 APK 内 assets 资源；不存在或异常时返回 null（交由解压目录/404 兜底）。
        private static System.IO.Stream OpenAsset(string assetPath)
        {
            try { return Android.App.Application.Context.Assets.Open(assetPath); }
            catch { return null; }
        }

        // 把 assets 流整体读入内存并写入响应（小体积 SPA 资源，内存方式最简单可靠）。
        private static void SendAsset(HttpListenerContext ctx, System.IO.Stream stream, string assetPath)
        {
            try
            {
                using (stream)
                {
                    var res = ctx.Response;
                    byte[] bytes;
                    if (IsHtml(assetPath))
                    {
                        using (var r = new StreamReader(stream)) bytes = Encoding.UTF8.GetBytes(InjectTheme(r.ReadToEnd()));
                    }
                    else
                    {
                        bytes = ReadFully(stream);
                    }
                    res.ContentType = MimeOf(assetPath);
                    res.ContentLength64 = bytes.Length;
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
                }
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        // 从已解压的 FilesDir 文件读入并写入响应。
        private static void SendFile(HttpListenerContext ctx, string file)
        {
            try
            {
                var res = ctx.Response;
                byte[] bytes;
                if (IsHtml(file))
                {
                    bytes = Encoding.UTF8.GetBytes(InjectTheme(File.ReadAllText(file)));
                }
                else
                {
                    bytes = File.ReadAllBytes(file);
                }
                res.ContentType = MimeOf(file);
                res.ContentLength64 = bytes.Length;
                res.AddHeader("Access-Control-Allow-Origin", "*");
                using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        private static bool IsHtml(string p)
        {
            return p != null && (p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 给网页端 HTML 注入「应用级默认主题」：仅当用户尚未自行选择（localStorage 无 mchat_theme）时，
        /// 把 App 当前日间/夜间结论写进 localStorage，使网页首屏与 App 主题一致；用户随后在前端切换仍有效。
        /// </summary>
        private static string InjectTheme(string html)
        {
            try
            {
                // 前端 CSS / MTheme 用的是 dark|light，这里必须换算，不能直接写 day|night
                string theme = ThemeManager.IsNight(Android.App.Application.Context) ? "dark" : "light";
                const string tag = "<head>";
                int i = html.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return html;
                var script = "\n<script>try{if(!localStorage.getItem('mchat_theme'))localStorage.setItem('mchat_theme','"
                           + theme + "');}catch(e){}</script>";
                return html.Substring(0, i + tag.Length) + script + html.Substring(i + tag.Length);
            }
            catch { return html; }
        }

        private static byte[] ReadFully(System.IO.Stream s)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /* ===================== 工具 ===================== */

        private static string ReadBody(HttpListenerRequest req)
        {
            using (var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static void WriteJson(HttpListenerResponse res, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
        }

        private static string Json(object obj)
        {
            return JsonConvert.SerializeObject(obj);
        }

        private static void WriteText(HttpListenerResponse res, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            using (var s = res.OutputStream) s.Write(bytes, 0, bytes.Length);
        }

        // 页面不存在时返回空白页：404 + 空 HTML 文档（浏览器渲染为纯白空白页，
        // 不会被 Chrome 默认的「404 Not Found」页面取代），避免误显示为聊天界面。
        private static void ServeBlankPage(HttpListenerResponse res)
        {
            res.StatusCode = 404;
            res.ContentType = "text/html; charset=utf-8";
            var body = Encoding.UTF8.GetBytes(
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title></title></head><body></body></html>");
            res.ContentLength64 = body.Length;
            try { using (var s = res.OutputStream) s.Write(body, 0, body.Length); } catch { }
        }

        private static string ClientIp(HttpListenerRequest req)
        {
            // HttpListener 的 RemoteEndPoint 即浏览器 IP
            var ep = req.RemoteEndPoint;
            return ep != null ? ep.Address.ToString() : "unknown";
        }

        private static string Sanitize(string s)
        {
            if (s == null) return string.Empty;
            // 去掉任何目录穿越成分
            return s.Replace('\\', '/')
                    .Replace("../", "")
                    .Replace("..\\", "")
                    .Replace("://", "");
        }

        private static string Escape(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        }

        private static string MimeOf(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".ogg": return "audio/ogg";
                case ".m4a": return "audio/mp4";
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                case ".ogv": return "video/ogg";
                case ".mov": return "video/quicktime";
                case ".bmp": return "image/bmp";
                case ".ico": return "image/x-icon";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain; charset=utf-8";
                case ".csv": return "text/csv; charset=utf-8";
                case ".tsv": return "text/tab-separated-values; charset=utf-8";
                case ".xml": return "application/xml; charset=utf-8";
                case ".mht": case ".mhtml": return "message/rfc822";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xls": return "application/vnd.ms-excel";
                case ".ppt": case ".pptx": return "application/vnd.ms-powerpoint";
                case ".doc": case ".docx": return "application/msword";
                case ".zip": return "application/zip";
                case ".woff": return "font/woff";
                case ".woff2": return "font/woff2";
                case ".ttf": return "font/ttf";
                case ".otf": return "font/otf";
                case ".eot": return "application/vnd.ms-fontobject";
                case ".wasm": return "application/wasm";
                case ".bcmap": return "application/octet-stream";
                default: return "application/octet-stream";
            }
        }

        /* ===================== 解压 Assets/webui ===================== */

        /// <summary>
        /// 兼容回退目录的增量补齐。静态资源主路径是「直接从 APK Assets 读」，
        /// 所以这一步只在后台慢慢做；用 APK 的 lastUpdateTime 作版本戳，
        /// 版本没变就整轮跳过，避免每次冷启动重写 12MB / 327 个文件。
        /// </summary>
        private void ScheduleAssetExtraction()
        {
            if (Interlocked.Exchange(ref _extractScheduled, 1) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    var stampFile = Path.Combine(_assetCtx.FilesDir.AbsolutePath, "assets.stamp");
                    long stamp = 0;
                    try
                    {
                        var pi = _assetCtx.PackageManager.GetPackageInfo(_assetCtx.PackageName, 0);
#pragma warning disable CS0618
                        stamp = pi.VersionCode + pi.LastUpdateTime;
#pragma warning restore CS0618
                    }
                    catch { }
                    if (stamp > 0 && File.Exists(stampFile))
                    {
                        long old;
                        if (long.TryParse(File.ReadAllText(stampFile), out old) && old == stamp
                            && Directory.Exists(webuiDir) && Directory.Exists(appDir)) return;
                    }
                    using (var assets = _assetCtx.Assets)
                    {
                        ExtractDir(assets, "webui", webuiDir, stamp > 0);
                        ExtractDir(assets, "www", appDir, stamp > 0);
                    }
                    if (stamp > 0) File.WriteAllText(stampFile, stamp.ToString());
                }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "资源解压(回退目录)失败(忽略): " + ex.Message); }
            });
        }

        private static void ExtractDir(Android.Content.Res.AssetManager assets, string assetDir, string outDir, bool skipExisting)
        {
            Directory.CreateDirectory(outDir);
            string[] list;
            try { list = assets.List(assetDir); }
            catch { return; }

            if (list == null || list.Length == 0)
            {
                CopyAsset(assets, assetDir, outDir, skipExisting);
                return;
            }

            foreach (var name in list)
            {
                var childAsset = assetDir + "/" + name;
                var sub = assets.List(childAsset);
                if (sub != null && sub.Length > 0)
                    ExtractDir(assets, childAsset, Path.Combine(outDir, name), skipExisting);
                else
                    CopyAsset(assets, childAsset, outDir, skipExisting);
            }
        }

        private static void CopyAsset(Android.Content.Res.AssetManager assets, string assetPath, string outDir, bool skipExisting)
        {
            var fileName = assetPath.Substring(assetPath.LastIndexOf('/') + 1);
            var dest = Path.Combine(outDir, fileName);
            // 版本戳可用时直接跳过已存在文件；升级后版本戳变化会整轮重写
            if (skipExisting && File.Exists(dest)) return;
            using (var inS = assets.Open(assetPath))
            using (var outS = File.Create(dest))
                inS.CopyTo(outS);
        }
    }
}

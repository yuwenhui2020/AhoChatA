using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Security.Cryptography.X509Certificates;
using Android.Util;
using AhoChatA.Music;

namespace AhoChatA
{
    /// <summary>
    /// WebServer 的「对外前端」分部：单一引擎包揽全局。
    ///
    /// 以前网页端有两条链路：UnifiedGateway(7790) 反代到 WebServer(6789)，既绕又容易
    /// 出现 Invalid host / 403 / 根路径误显示聊天等问题。现在把网关的对外职责整并进
    /// WebServer 本身——对外只在 7790 这一个端口上按路径分流：
    ///
    ///   /cam/*    IP 摄像头 / 屏幕共享（网页播放器、MSE、live.ts、stream.h264、info）
    ///   /draw/*   屏幕画笔局域网直播（MJPEG + 观看页）
    ///   /file/*   局域网文件共享浏览 / 下载
    ///   /hub      服务导航页：列出本机当前可用的入口与地址
    ///   其它      交给内置回环网页引擎（WebServer.Port = 6789，HttpListener）
    ///
    /// 实现上不做应用层重写（流式接口太多、太敏感），只做路径分流 + 原始字节透传：
    /// 摄像头那条直接把自己的 socket 交给 IpCameraServer 原有处理逻辑，行为与以前完全一致。
    /// HTTPS(7791) 仅用于摄像头（WebCodecs 的 VideoDecoder 只在安全上下文下暴露，
    /// 而 http://192.168.x.x 不算安全上下文，浏览器会退回 MSE）。
    /// </summary>
    public sealed partial class WebServer
    {
        /* ===================== 对外前端（单端口 7790） ===================== */

        public const string CamPrefix = "/cam";
        public const string DrawPrefix = "/draw";
        public const string FilePrefix = "/file";
        public const string HubPath = "/hub";

        /// <summary>对外统一入口端口（单引擎对外只有一个端口）。</summary>
        public const int ExternalPort = 7790;
        private static int _port = ExternalPort;
        /// <summary>实际绑定的对外端口（7790 被占用时回退到 7790-7809）。</summary>
        public static int ExternalPortActual { get { return _port; } }

        /* ---- HTTPS（7791）：WebCodecs 的 VideoDecoder 只在「安全上下文」下暴露 ---- */
        private const int HttpsPort = 7791;
        private static TcpListener _httpsListener;
        private static Thread _httpsThread;
        private static volatile bool _httpsRunning;
        private static X509Certificate2 _cert;
        public static bool HttpsRunning { get { return _httpsRunning; } }
        public static int HttpsPortActual { get; private set; }

        private static TcpListener _listener;
        private static Thread _thread;
        private static Thread _watchdog;
        private static volatile bool _running;
        public static bool IsRunning { get { return _running; } }

        public static string HttpsBaseUrl
        {
            get { return "https://" + (DeviceStatus.Ip ?? "127.0.0.1") + ":" + HttpsPortActual; }
        }

        /// <summary>形如 http://192.168.1.5:7790（不带结尾斜杠）。</summary>
        public static string BaseUrl
        {
            get { return "http://" + (DeviceStatus.Ip ?? "127.0.0.1") + ":" + _port; }
        }

        /* ===================== 生命周期（对外前端） ===================== */

        /// <summary>启动对外前端（7790）与 HTTPS（7791）。由 WebServer.Start() 调用。</summary>
        public static void StartFront()
        {
            if (_running) return;

            // Mono 的线程池最小线程数默认很低（约等于 CPU 核数），
            // 每个 HTTP 连接要占一个线程；浏览器一次开页面会并发十几个请求，
            // 线程池来不及扩容时后到的请求就排队超时——表现为「网页时得时不得」。
            // 这里抬高下限（只影响按需扩容的起点，不会常驻这么多线程）。
            try
            {
                int w, io;
                ThreadPool.GetMinThreads(out w, out io);
                ThreadPool.SetMinThreads(Math.Max(w, 24), Math.Max(io, 24));
                Log.Info("Mchat", "线程池最小线程数：worker " + Math.Max(w, 24) + " / io " + Math.Max(io, 24));
            }
            catch { }

            TcpListener l = null;
            Exception lastBindError = null;
            int port = ExternalPort;
            for (int p = ExternalPort; p < ExternalPort + 20; p++)
            {
                TcpListener candidate = null;
                try
                {
                    // 绑 0.0.0.0（IPAddress.Any）：只绑端口、不绑某个 IP，
                    // 手机换 Wi-Fi / DHCP 换地址后无需重启 App 就能继续访问。
                    candidate = new TcpListener(IPAddress.Any, p);
                    try { candidate.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                    candidate.Start();
                    l = candidate; port = p;
                    break;
                }
                catch (Exception ex)
                {
                    lastBindError = ex;
                    try { candidate?.Stop(); } catch { }
                }
            }

            if (l == null)
            {
                var message = L10n.T("统一端口启动失败（") + ExternalPort + "-" + (ExternalPort + 19) + L10n.T(" 全部绑定失败）。最后异常: ")
                    + (lastBindError == null ? "none" : lastBindError.ToString());
                Log.Error("Mchat", message);
                throw new InvalidOperationException(message, lastBindError);
            }

            _listener = l;
            _port = port;
            _thread = new Thread(Loop) { IsBackground = true, Name = "web-front" };
            _running = true;
            try
            {
                _thread.Start();
            }
            catch (Exception ex)
            {
                _running = false;
                try { l.Stop(); } catch { }
                _listener = null;
                _thread = null;
                Log.Error("Mchat", "启动对外 accept 线程失败: " + ex.ToString());
                throw;
            }
            StartWatchdog();
            Log.Info("Mchat", "WebServer 对外前端已启动：" + BaseUrl);
            // HTTPS 证书解析（约 140ms）放到独立后台线程，不再拖住 7790 对外端口的就绪。
            // 7790 是主入口，绝大多数页面用它；7791 只服务需要安全上下文的 WebCodecs。
            try
            {
                var t = new Thread(() => { try { StartHttps(); } catch { } })
                { IsBackground = true, Name = "web-https-init" };
                t.Start();
            }
            catch { }
        }

        /// <summary>加载 Assets/cert/Mchat.pfx（自签名，10 年，SAN 含本机 IP / localhost / *.local）。</summary>
        private static X509Certificate2 LoadCert()
        {
            try
            {
                var ctx = Android.App.Application.Context;
                if (ctx == null) return null;
                using (var sr = ctx.Assets.Open("cert/Mchat.pfx"))
                using (var ms = new MemoryStream())
                {
                    sr.CopyTo(ms);
                    return new X509Certificate2(ms.ToArray(), "Mchat");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "证书加载失败（HTTPS 不可用）：" + ex.Message);
                return null;
            }
        }

        /// <summary>提前解析证书（首次约 140ms），供预热调用；已解析过则直接返回。</summary>
        public static void PrewarmCertificate()
        {
            try
            {
                if (_cert == null) _cert = LoadCert();
            }
            catch { }
        }

        private static void StartHttps()
        {
            try
            {
                if (_httpsRunning) return;
                if (_cert == null) _cert = LoadCert();
                if (_cert == null) return;

                TcpListener l = null;
                for (int p = HttpsPort; p < HttpsPort + 10; p++)
                {
                    try
                    {
                        // 绑 0.0.0.0（IPAddress.Any）：只绑端口、不绑某个 IP，
                    // 手机换 Wi-Fi / DHCP 换地址后无需重启 App 就能继续访问。
                    var cand = new TcpListener(IPAddress.Any, p);
                        try { cand.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                        cand.Start();
                        l = cand; HttpsPortActual = p;
                        break;
                    }
                    catch { }
                }
                if (l == null)
                {
                    Log.Warn("Mchat", "HTTPS 端口启动失败（" + HttpsPort + "+ 被占用）");
                    return;
                }
                Log.Info("Mchat", "证书信息：HasPrivateKey=" + _cert.HasPrivateKey
                    + " Subject=" + _cert.Subject + " NotAfter=" + _cert.NotAfter.ToString("yyyy-MM-dd"));
                _httpsListener = l;
                _httpsRunning = true;
                _httpsThread = new Thread(HttpsLoop) { IsBackground = true, Name = "web-front-tls" };
                _httpsThread.Start();
                Log.Info("Mchat", "HTTPS 入口已启动：" + HttpsBaseUrl + "（自签名证书，首次需手动信任）");
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "HTTPS 启动失败：" + ex.Message);
            }
        }

        private static void HttpsLoop()
        {
            while (_httpsRunning && _httpsListener != null)
            {
                TcpClient client;
                try { client = _httpsListener.AcceptTcpClient(); }
                catch (ObjectDisposedException) { break; }
                catch { continue; }
                ThreadPool.QueueUserWorkItem(_ => HandleHttpsClient(client));
            }
        }

        private static void HandleHttpsClient(TcpClient client)
        {
            System.Net.Security.SslStream ssl = null;
            try
            {
                Log.Info("Mchat", "HTTPS 连接进来，开始握手…");
                client.ReceiveTimeout = 8000;
                ssl = new System.Net.Security.SslStream(client.GetStream(), false);
                var shake = System.Threading.Tasks.Task.Run(() =>
                    ssl.AuthenticateAsServer(_cert, false, System.Security.Authentication.SslProtocols.Tls12, false));
                if (!shake.Wait(10000))
                {
                    Log.Warn("Mchat", "TLS 握手超时（10s）");
                    try { client.Close(); } catch { }
                    return;
                }
                Log.Info("Mchat", "TLS 握手完成：协议=" + ssl.SslProtocol);
                ssl.ReadTimeout = 10000;
                ssl.WriteTimeout = 10000;

                var head = new byte[2048];
                int got = 0;
                var deadline = System.Environment.TickCount + 8000;
                while (got < head.Length && System.Environment.TickCount < deadline)
                {
                    int n;
                    try { n = ssl.Read(head, got, head.Length - got); }
                    catch { break; }
                    if (n <= 0) break;
                    got += n;
                    for (int i = 0; i < got; i++)
                        if (head[i] == (byte)'\n') goto parsed;
                    continue;
                parsed:
                    break;
                }
                if (got <= 0) { try { client.Close(); } catch { } return; }

                string path = ParsePath(head, got, System.Array.IndexOf(head, (byte)'\n'));
                var wrapped = new PrefixStream(ssl, head, got);
                string route = (path ?? "/").ToLowerInvariant();
                int qi = route.IndexOf('?');
                if (qi >= 0) route = route.Substring(0, qi);

                if (OtpAuth.Enabled)
                {
                    try
                    {
                        var info = ParseHead(Encoding.ASCII.GetString(head, 0, got), path);
                        string ip = IpOf(client);
                        if (OtpHandle(ip, info,
                                html => { WriteHtmlStream(wrapped, 200, html); try { client.Close(); } catch { } },
                                json => { WriteRawStream(wrapped, 401, json, "application/json"); try { client.Close(); } catch { } },
                                loc => { RedirectStream(wrapped, loc, CookieHeader(ip)); try { client.Close(); } catch { } }))
                            return;
                    }
                    catch { }
                }

                if (PathIs(route, CamPrefix) || PathUnder(route, CamPrefix))
                {
                    var cam = IpCameraServer.Instance;
                    if (cam == null || !cam.IsRunning)
                    {
                        WriteHtmlStream(wrapped, 200, OfflinePage());
                        return;
                    }
                    cam.ServeClientSecure(client, wrapped);   // 不再 302 补斜杠
                    return;
                }
                if (route.StartsWith(DrawPrefix) || route.StartsWith(FilePrefix) || route == HubPath)
                {
                    string httpUrl = "http://" + (DeviceStatus.Ip ?? "127.0.0.1") + ":" + _port + route;
                    WriteHtmlStream(wrapped, 200,
                        "<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>"
                        + "<style>body{margin:0;background:#f2f3f5;color:#111;font:15px/1.7 -apple-system,'PingFang SC','Microsoft YaHei',sans-serif;"
                        + "display:flex;min-height:100vh;align-items:center;justify-content:center;padding:24px}"
                        + ".c{background:#fff;border-radius:12px;padding:24px;max-width:420px;text-align:center}"
                        + "a{color:#12b7f5;word-break:break-all}</style>"
                        + L10n.T("<div class=c><h3>请改用 http 访问</h3>")
                        + L10n.T("<p>画笔与文件共享不需要安全上下文，用 http 更简单（也不用信任证书）：</p>")
                        + "<p><a href='" + httpUrl + "'>" + httpUrl + "</a></p>"
                        + L10n.T("<p style='color:#8e9099;font-size:12px'>摄像头 / 屏幕共享才需要 https（为了低延迟直推）。</p></div>"));
                    return;
                }

                ProxyStream(wrapped, client);
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "HTTPS 连接处理失败：" + ex.Message);
                try { client.Close(); } catch { }
            }
        }

        public static void StopFront()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            try { _thread?.Join(800); } catch { }
            _thread = null;
            try { _watchdog?.Join(400); } catch { }
            _watchdog = null;

            _httpsRunning = false;
            try { _httpsListener?.Stop(); } catch { }
            _httpsListener = null;
            try { _httpsThread?.Join(800); } catch { }
            _httpsThread = null;
        }

        /// <summary>本机可访问的完整地址（用于提示、二维码、分享给对端）。</summary>
        public static string UrlOf(string path)
        {
            return UrlOf(path, null);
        }

        /// <summary>
        /// 指定访问者实际使用的 Host 生成地址：手机换了 Wi-Fi / IP 变了，
        /// 页面上显示的仍然是「你正在用的那个地址」，不需要重启 App。
        /// </summary>
        public static string UrlOf(string path, string host)
        {
            if (string.IsNullOrEmpty(path)) path = "/";
            if (!path.StartsWith("/")) path = "/" + path;
            string h = string.IsNullOrEmpty(host) ? (DeviceStatus.Ip ?? "127.0.0.1") : host;

            // Host 头里通常已经带端口（192.168.1.101:7790），别再拼一次
            bool hasPort = false;
            int colon = h.LastIndexOf(':');
            if (colon > 0 && colon < h.Length - 1)
            {
                hasPort = true;
                for (int i = colon + 1; i < h.Length; i++)
                    if (!char.IsDigit(h[i])) { hasPort = false; break; }
            }
            return "http://" + h + (hasPort ? "" : ":" + _port) + path;
        }

        /// <summary>启动某个功能后提示给用户的访问地址（带一句人话说明）。</summary>
        public static string AccessHint(string path, string what)
        {
            return what + "\n" + UrlOf(path);
        }

        private static void Loop()
        {
            while (_running && _listener != null)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    // 客户端在 accept 队列里被重置会抛 ECONNABORTED 之类的错误；
                    // 这里**绝不能 break**，否则整个对外前端会在一次偶发错误后永久失效
                    // （端口仍在 LISTEN、连接却再也没人 accept，表现为「时得时不得」）。
                    Log.Warn("Mchat", "accept 偶发失败，继续服务：" + ex.SocketErrorCode);
                    try { Thread.Sleep(10); } catch { }
                    continue;
                }
                catch (Exception ex)
                {
                    Log.Warn("Mchat", "accept 异常，继续服务：" + ex.Message);
                    try { Thread.Sleep(10); } catch { }
                    continue;
                }
                if (client == null) continue;
                ThreadPool.QueueUserWorkItem(_ => Dispatch(client));
            }
            Log.Warn("Mchat", "对外前端 accept 循环退出（running=" + _running + "）");
        }

        /// <summary>
        /// 看门狗：accept 线程万一还是退出（例如系统回收），3 秒内自动重启，
        /// 避免「端口在听但没人接」的假死状态。
        /// </summary>
        private static void StartWatchdog()
        {
            if (_watchdog != null) return;
            _watchdog = new Thread(() =>
            {
                while (_running)
                {
                    try { Thread.Sleep(3000); } catch { break; }
                    if (!_running) break;
                    var t = _thread;
                    if (t != null && !t.IsAlive && _listener != null)
                    {
                        Log.Warn("Mchat", "对外前端 accept 线程已停止，自动重启");
                        var nt = new Thread(Loop) { IsBackground = true, Name = "web-front" };
                        _thread = nt;
                        try { nt.Start(); } catch { }
                    }
                }
            }) { IsBackground = true, Name = "web-front-watch" };
            _watchdog.Start();
        }

        private static void Dispatch(TcpClient client)
        {
            string path = null;
            try
            {
                client.NoDelay = true;
                try { client.SendBufferSize = 256 * 1024; } catch { }
                // 浏览器会「预连接」一条 TCP 却不立刻发请求（Chrome 的 preconnect）；
                // 不设超时会让读线程永久阻塞，线程池被占满后表现为「网页时好时坏」。
                try { client.ReceiveTimeout = 8000; } catch { }
                try { client.SendTimeout = 30000; } catch { }
                path = PeekPath(client);
                if (path == null)
                {
                    Log.Info("Mchat", "连接未收到请求行，已关闭（" + (IpOf(client) ?? "?") + "）");
                    try { client.Close(); } catch { }
                    return;
                }

                if (OtpAuth.Enabled)
                {
                    try
                    {
                        var info = ParseHead(PeekHead(client), path);
                        string ip = IpOf(client);
                        if (OtpHandle(ip, info,
                                html => WriteHtml(client, 200, html),
                                json => WriteRaw(client, 401, json, "application/json"),
                                loc => RedirectWithCookie(client, loc, ip)))
                            return;
                    }
                    catch { }
                }

                // 路径比较一律忽略大小写：本文件早期用的是区分大小写的比较，
                // 而内置引擎（6789）是忽略大小写的，于是 /File 会被反代过去再 302 回来
                // → 浏览器 ERR_TOO_MANY_REDIRECTS。现在两端口都直接出内容、不做任何重定向。
                string route = path ?? "/";

                if (PathIs(route, CamPrefix))
                {
                    var cam0 = IpCameraServer.Instance;
                    if (cam0 == null || !cam0.IsRunning)
                    {
                        WriteHtml(client, 200, OfflinePage(FileShareHttp.HostOf(PeekHead(client))));
                        return;
                    }
                    cam0.ServeClient(client);          // 摄像头服务内部把 /cam 当根，无需 302 补斜杠
                    return;
                }

                if (PathUnder(route, CamPrefix))
                {
                    var cam = IpCameraServer.Instance;
                    if (cam == null || !cam.IsRunning)
                    {
                        WriteHtml(client, 200, OfflinePage(FileShareHttp.HostOf(PeekHead(client))));
                        return;
                    }
                    cam.ServeClient(client);
                    return;
                }

                if (PathIs(route, DrawPrefix) || PathUnder(route, DrawPrefix))
                {
                    DrawShareServer.ServeClient(client, route.Substring(DrawPrefix.Length));
                    return;
                }

                if (PathIs(route, FilePrefix) || PathUnder(route, FilePrefix))
                {
                    FileShareHttp.ServeClient(client, route.Substring(FilePrefix.Length));
                    return;
                }

                if (PathIs(route, HubPath) || PathIs(route, HubPath + "/"))
                {
                    WriteHtml(client, 200, HubPage(FileShareHttp.HostOf(PeekHead(client))));
                    return;
                }

                // 同步听时钟对时：直接在网关线程回包，不经过 6789 反代，
                // 否则一次额外的回环 TCP 连接会把 RTT 样本抬高、offset 估算偏大。
                if (PathIs(route, "/api/sync/time")) { ServeSyncTime(client); return; }

                // 网页端「同步听」状态镜像：当前曲目 + 权威进度 + 资源地址（均走 app 代理）
                if (PathIs(route, "/api/media/synclisten")) { ServeSyncListen(client); return; }

                // 通用远程资源代理：把外部 CDN 资源经 app 中转，隐藏客户端（SSRF 已防护）
                if (route.StartsWith("/api/media/remote", StringComparison.OrdinalIgnoreCase))
                { ServeRemoteProxy(client, route); return; }

                // 在线音乐（搜索 / 流 / 下载）：交给内置引擎（6789）处理，
                // 由 MediaApi 的 OnlineSearch / OnlineStream / OnlineDownload 实现。
                // 注意必须放在「/api/media/stream 直连分支」之前，否则会被前缀误匹配。
                if (route.StartsWith("/api/media/online", StringComparison.OrdinalIgnoreCase))
                {
                    Proxy(client);
                    return;
                }

                // 视频流 / 图片：走网关直连流式（keep-alive + 直读文件），避免每个 Range 重建连接。
                if (route.StartsWith("/api/media/stream", StringComparison.OrdinalIgnoreCase)
                    || route.StartsWith("/api/media/image", StringComparison.OrdinalIgnoreCase))
                {
                    ServeMediaStreamDirect(client, route);
                    return;
                }

                Proxy(client);
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "处理 " + path + " 失败：" + ex.Message);
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// <summary>
        /// 网页端「同步听」状态镜像：直接由网关线程输出 SyncListen.WebStatus()，
        /// 带 CORS，供同局域网浏览器轮询当前曲目与权威进度。
        /// </summary>
        private static void ServeSyncListen(TcpClient client)
        {
            try
            {
                string body = SyncListen.WebStatus();
                var b = Encoding.UTF8.GetBytes(body);
                var head = "HTTP/1.1 200 OK\r\n"
                         + "Content-Type: application/json; charset=utf-8\r\n"
                         + "Content-Length: " + b.Length + "\r\n"
                         + "Access-Control-Allow-Origin: *\r\n"
                         + "Cache-Control: no-store\r\n"
                         + "Connection: close\r\n\r\n";
                var ns = client.GetStream();
                ns.Write(Encoding.ASCII.GetBytes(head), 0, head.Length);
                ns.Write(b, 0, b.Length);
                ns.Flush();
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// 通用远程资源代理（/api/media/remote?url=）：把外部 CDN 的图片/媒体经本机中转，
        /// 让浏览器的客户端地址不外泄给 CDN；仅允许 http/https，且禁止回源到内网/回环地址（SSRF）。
        /// 封面/海报这类小资源整块取回后回写；支持透传 Range。
        /// </summary>
        private static void ServeRemoteProxy(TcpClient client, string route)
        {
            try
            {
                int qi = route.IndexOf('?');
                var q = ParseQuery(qi >= 0 ? route.Substring(qi + 1) : "");
                string url = q.TryGetValue("url", out var uv) && !string.IsNullOrEmpty(uv)
                           ? uv : (q.TryGetValue("u", out var u2) ? u2 : "");
                if (string.IsNullOrEmpty(url))
                { WriteRaw(client, 400, "{\"error\":\"missing url\"}", "application/json"); return; }

                Uri ur;
                if (!Uri.TryCreate(url, UriKind.Absolute, out ur)
                    || (ur.Scheme != Uri.UriSchemeHttp && ur.Scheme != Uri.UriSchemeHttps))
                { WriteRaw(client, 400, "{\"error\":\"bad url\"}", "application/json"); return; }

                // SSRF 防护：拒绝访问内网 / 回环 / 链路本地地址
                if (IsPrivateHost(ur.Host))
                { WriteRaw(client, 403, "{\"error\":\"blocked\"}", "application/json"); return; }

                var req = (HttpWebRequest)WebRequest.CreateHttp(url);
                req.Method = "GET";
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.AllowAutoRedirect = true;
                req.MaximumAutomaticRedirections = 5;
                req.UserAgent = "Mchat/1.0";
                req.Accept = "*/*";

                // 透传浏览器的 Range（封面/海报按需分片），原样转发给上游
                var head = PeekHead(client);
                if (!string.IsNullOrEmpty(head))
                {
                    foreach (var line in head.Replace("\r\n", "\n").Split('\n'))
                    {
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        {
                            var rv = line.Substring(6).Trim();
                            if (!string.IsNullOrEmpty(rv)) req.Headers[System.Net.HttpRequestHeader.Range] = rv;
                        }
                    }
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    var ct = resp.ContentType;
                    if (string.IsNullOrEmpty(ct)) ct = "application/octet-stream";
                    using (var ms = new System.IO.MemoryStream())
                    {
                        using (var rs = resp.GetResponseStream()) rs.CopyTo(ms, 32768);
                        var data = ms.ToArray();
                        var h = "HTTP/1.1 200 OK\r\n"
                              + "Content-Type: " + ct + "\r\n"
                              + "Content-Length: " + data.Length + "\r\n"
                              + "Accept-Ranges: bytes\r\n"
                              + "Access-Control-Allow-Origin: *\r\n"
                              + "Cache-Control: no-store\r\n"
                              + "Connection: close\r\n\r\n";
                        var ns = client.GetStream();
                        ns.Write(Encoding.ASCII.GetBytes(h), 0, h.Length);
                        ns.Write(data, 0, data.Length);
                        ns.Flush();
                    }
                }
            }
            catch (System.Net.WebException wex)
            {
                int code = 502;
                if (wex.Response is System.Net.HttpWebResponse wr) code = (int)wr.StatusCode;
                WriteRaw(client, code, "{\"error\":\"upstream\"}", "application/json");
            }
            catch
            {
                WriteRaw(client, 502, "{\"error\":\"proxy\"}", "application/json");
            }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        /// NTP 式对时响应：t2 = 收到请求的时刻，t3 = 写出响应的时刻（都是本机墙上 UTC 毫秒）。
        /// 客户端拿 T1（发出）/ T4（收到）即可算出 offset = ((T2-T1)+(T3-T4))/2。
        /// 直接由网关线程输出，带 CORS，网页端可以跨设备 fetch 发起端的这个地址。
        /// </summary>
        private static void ServeSyncTime(TcpClient client)
        {
            try
            {
                long t2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long t3 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string body = "{\"t2\":" + t2 + ",\"t3\":" + t3 + "}";
                var b = Encoding.UTF8.GetBytes(body);
                var head = "HTTP/1.1 200 OK\r\n"
                         + "Content-Type: application/json; charset=utf-8\r\n"
                         + "Content-Length: " + b.Length + "\r\n"
                         + "Access-Control-Allow-Origin: *\r\n"
                         + "Cache-Control: no-store\r\n"
                         + "Connection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                var ns = client.GetStream();
                ns.Write(hb, 0, hb.Length);
                ns.Write(b, 0, b.Length);
                ns.Flush();
                try { SyncClock.NoteFollower(); } catch { }
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private static void Redirect(TcpClient client, string location)
        {
            try
            {
                var head = "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                var ns = client.GetStream();
                ns.Write(hb, 0, hb.Length);
                ns.Flush();
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        /* ===================== 双因素认证（2FA） ===================== */

        private static string IpOf(TcpClient client)
        {
            try
            {
                var ep = client.Client.RemoteEndPoint as IPEndPoint;
                return ep == null ? null : ep.Address.ToString();
            }
            catch { return null; }
        }

        private static string PeekHead(TcpClient client, int max = 4096)
        {
            try
            {
                var sock = client.Client;
                var buf = new byte[max];
                for (int tries = 0; tries < 40; tries++)
                {
                    int n;
                    try { n = sock.Receive(buf, 0, buf.Length, SocketFlags.Peek); }
                    catch { return null; }
                    if (n <= 0) return null;

                    int end = -1;
                    for (int i = 0; i + 3 < n; i++)
                    {
                        if (buf[i] == (byte)'\r' && buf[i + 1] == (byte)'\n'
                            && buf[i + 2] == (byte)'\r' && buf[i + 3] == (byte)'\n') { end = i + 4; break; }
                    }
                    if (end > 0) return Encoding.ASCII.GetString(buf, 0, end);

                    bool hasLine = false;
                    for (int i = 0; i < n; i++) if (buf[i] == (byte)'\n') { hasLine = true; break; }
                    if (hasLine && tries > 6) return Encoding.ASCII.GetString(buf, 0, n);
                    Thread.Sleep(10);
                }
                return null;
            }
            catch { return null; }
        }

        private sealed class ReqHead
        {
            public string Path = "/";
            public string Target = "/";
            public string CookieValue;
            public bool AppClient;
            public Dictionary<string, string> Query =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Q(string key)
            {
                string v;
                return Query.TryGetValue(key, out v) ? v : null;
            }
        }

        private static ReqHead ParseHead(string head, string fallbackPath)
        {
            var info = new ReqHead();
            info.Path = string.IsNullOrEmpty(fallbackPath) ? "/" : fallbackPath;
            info.Target = info.Path;
            if (string.IsNullOrEmpty(head)) return info;

            var lines = head.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) { if (i > 0) break; continue; }

                if (i == 0)
                {
                    var parts = line.Split(' ');
                    if (parts.Length < 2) continue;
                    var target = parts[1];
                    info.Target = target;

                    int q = target.IndexOf('?');
                    var p = q >= 0 ? target.Substring(0, q) : target;
                    if (string.IsNullOrEmpty(p)) p = "/";
                    if (!p.StartsWith("/")) p = "/" + p;
                    info.Path = p;

                    if (q >= 0)
                    {
                        foreach (var kv in target.Substring(q + 1).Split('&'))
                        {
                            if (string.IsNullOrEmpty(kv)) continue;
                            int eq = kv.IndexOf('=');
                            string k = eq >= 0 ? kv.Substring(0, eq) : kv;
                            string v = eq >= 0 ? kv.Substring(eq + 1) : "";
                            try { v = Uri.UnescapeDataString(v.Replace("+", " ")); } catch { }
                            info.Query[k] = v;
                        }
                    }
                    continue;
                }

                int c = line.IndexOf(':');
                if (c <= 0) continue;
                var name = line.Substring(0, c).Trim();
                var val = line.Substring(c + 1).Trim();

                if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var part in val.Split(';'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) continue;
                        if (string.Equals(part.Substring(0, eq).Trim(), OtpAuth.CookieName,
                                          StringComparison.OrdinalIgnoreCase))
                            info.CookieValue = part.Substring(eq + 1).Trim();
                    }
                }
                else if (string.Equals(name, "X-Mchat-App", StringComparison.OrdinalIgnoreCase))
                {
                    info.AppClient = true;
                }
            }
            return info;
        }

        /// <summary>
        /// 返回 true = 已经把响应写掉了（口令页 / 放行跳转），调用方直接 return；
        /// 返回 false = 放行，继续原来的路子。
        /// </summary>
        private static bool OtpHandle(string ip, ReqHead info, Action<string> writeHtml,
                                      Action<string> writeJson, Action<string> redirect)
        {
            if (!OtpAuth.Enabled) return false;
            bool isApi = info.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(ip) || ip == "127.0.0.1" || ip == "::1" || ip == "localhost") return false;

            if (string.Equals(info.Path, OtpAuth.SubmitPath, StringComparison.OrdinalIgnoreCase))
            {
                if (OtpAuth.Verify(info.Q("code")))
                {
                    OtpAuth.Grant(ip);
                    redirect(SafeBack(info.Q("r")));
                    return true;
                }
                if (isApi) writeJson("{\"error\":\"otp_bad\"}");
                else writeHtml(OtpPage(L10n.T("口令不对或已过期，请重新输入。"), info.Q("r")));
                return true;
            }

            if (OtpAuth.IsExemptPath(info.Path)) return false;

            if (info.AppClient && info.Path.StartsWith(DrawPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (OtpAuth.IsGranted(ip)) return false;
            if (OtpAuth.CookieOk(info.CookieValue, ip)) { OtpAuth.Grant(ip); return false; }

            var urlCode = info.Q("otp");
            if (!string.IsNullOrEmpty(urlCode) && OtpAuth.Verify(urlCode))
            {
                OtpAuth.Grant(ip);
                redirect(SafeBack(info.Q("r") ?? info.Path));
                return true;
            }

            if (isApi) writeJson("{\"error\":\"otp_required\"}");
            else writeHtml(OtpPage(null, info.Target));
            return true;
        }

        private static void WriteRaw(TcpClient client, int code, string body, string contentType)
        {
            try
            {
                var b = Encoding.UTF8.GetBytes(body ?? "");
                var head = "HTTP/1.1 " + code + " Status\r\n"
                         + "Content-Type: " + contentType + "; charset=utf-8\r\n"
                         + "Content-Length: " + b.Length + "\r\n"
                         + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                var ns = client.GetStream();
                ns.Write(hb, 0, hb.Length);
                ns.Write(b, 0, b.Length);
                ns.Flush();
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private static void WriteRawStream(Stream s, int code, string body, string contentType)
        {
            try
            {
                var b = Encoding.UTF8.GetBytes(body ?? "");
                var head = "HTTP/1.1 " + code + " Status\r\n"
                         + "Content-Type: " + contentType + "; charset=utf-8\r\n"
                         + "Content-Length: " + b.Length + "\r\n"
                         + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                s.Write(hb, 0, hb.Length);
                s.Write(b, 0, b.Length);
                s.Flush();
            }
            catch { }
        }

        private static string SafeBack(string back)
        {
            if (string.IsNullOrEmpty(back)) return "/";
            if (!back.StartsWith("/")) return "/";
            if (back.StartsWith("//")) return "/";
            return back;
        }

        private static string CookieHeader(string ip)
        {
            return "Set-Cookie: " + OtpAuth.CookieName + "=" + OtpAuth.TokenFor(ip)
                 + "; Path=/; Max-Age=28800; HttpOnly\r\n";
        }

        private static void RedirectWithCookie(TcpClient client, string location, string ip)
        {
            try
            {
                string head = "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\n"
                            + CookieHeader(ip)
                            + "Content-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                var ns = client.GetStream();
                ns.Write(hb, 0, hb.Length);
                ns.Flush();
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private static void RedirectStream(Stream s, string location, string cookieHeader)
        {
            try
            {
                string head = "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\n"
                            + (cookieHeader ?? "")
                            + "Content-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                s.Write(hb, 0, hb.Length);
                s.Flush();
            }
            catch { }
        }

        private static string OtpPage(string error, string back)
        {
            string b = SafeBack(back);
            string err = string.IsNullOrEmpty(error) ? ""
                : "<p class=err>" + He(error) + "</p>";

            return "<!doctype html><html><head><meta charset=utf-8>"
                 + "<meta name=viewport content='width=device-width,initial-scale=1'>"
                 + L10n.T("<title>需要动态口令</title><style>")
                 + "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;"
                 + "background:#f2f3f5;color:#111;padding:24px;box-sizing:border-box;"
                 + "font:15px/1.7 -apple-system,'PingFang SC','Microsoft YaHei',sans-serif}"
                 + ".c{background:#fff;border-radius:14px;padding:26px 22px;max-width:380px;width:100%;text-align:center;"
                 + "box-shadow:0 2px 14px rgba(0,0,0,.06)}"
                 + "h3{margin:0 0 10px;font-size:18px}p{margin:0 0 14px;color:#5f6368;font-size:13px}"
                 + ".err{color:#ff3b30;font-size:13px;margin:0 0 12px}"
                 + "input[name=code]{width:100%;box-sizing:border-box;padding:12px;font-size:22px;text-align:center;"
                 + "letter-spacing:6px;border:1px solid #dcdfe3;border-radius:10px;outline:none}"
                 + "input[name=code]:focus{border-color:#12b7f5}"
                 + "button{margin-top:12px;width:100%;padding:12px;border:0;border-radius:10px;background:#12b7f5;"
                 + "color:#fff;font-size:16px}.tip{margin:14px 0 0;color:#8e9099;font-size:11px}"
                 + "</style></head><body><div class=c>"
                 + L10n.T("<h3>需要动态口令</h3>")
                 + L10n.T("<p>这台设备开启了双因素认证。请在 AhoChat 里打开「设备」页，")
                 + L10n.T("「本机」下方的 6 位数字就是当前口令（每 30 秒变一次）。</p>")
                 + err
                 + "<form method=get action='" + OtpAuth.SubmitPath + "'>"
                 + "<input name=code inputmode=numeric pattern='[0-9]*' autocomplete=one-time-code "
                 + L10n.T("maxlength=6 placeholder='······' aria-label='6 位口令'>")
                 + "<input type=hidden name=r value='" + He(b) + "'>"
                 + L10n.T("<button type=submit>验证并进入</button></form>")
                 + L10n.T("<p class=tip>摄像头与屏幕共享不校验口令（VLC 等播放器无法输入）。</p>")
                 + "<script>try{document.querySelector('input[name=code]').focus()}catch(e){}</script>"
                 + "</div></body></html>";
        }

        private static string He(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;")
                    .Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        /* ===================== 请求头解析 / 透传 ===================== */

        private static string PeekPath(TcpClient client)
        {
            var sock = client.Client;
            var buf = new byte[2048];
            int total = 0;
            for (int tries = 0; tries < 40; tries++)
            {
                int n;
                try { n = sock.Receive(buf, 0, buf.Length, SocketFlags.Peek); }
                catch { return null; }
                if (n <= 0) return null;
                total = n;
                for (int i = 0; i < total; i++)
                {
                    if (buf[i] == (byte)'\n') { return ParsePath(buf, total, i); }
                }
                Thread.Sleep(10);
            }
            return null;
        }

        /// <summary>路径相等（忽略大小写，容忍结尾斜杠）。</summary>
        private static bool PathIs(string path, string other)
        {
            if (path == null) return false;
            return string.Equals(path.TrimEnd('/'), (other ?? "").TrimEnd('/'),
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>path 是否落在 prefix 之下（/file/get/x 属于 /file）。</summary>
        private static bool PathUnder(string path, string prefix)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix)) return false;
            return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ParsePath(byte[] buf, int total, int lineEnd)
        {
            string line = Encoding.ASCII.GetString(buf, 0, lineEnd).Trim();
            var parts = line.Split(' ');
            if (parts.Length < 2) return null;
            string p = parts[1];

            // 走 HTTP 代理（Clash / v2ray / 公司代理）时请求行是「绝对 URI」：
            //   GET http://192.168.1.101:7790/file HTTP/1.1
            // 不剥掉 scheme://host 就会被当成未知路径，落到 6789 再被 302 回来 → 死循环。
            int scheme = p.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                int slash = p.IndexOf('/', scheme + 3);
                p = slash >= 0 ? p.Substring(slash) : "/";
            }

            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            if (!p.StartsWith("/")) p = "/" + p;
            return p;
        }

        /// <summary>把连接原样转发到内置回环网页引擎（WebServer.Port = 6789），只改 Host 头。</summary>
        private static void Proxy(TcpClient client)
        {
            using (var upstream = new TcpClient())
            {
                try { upstream.Connect(IPAddress.Loopback, WebServer.Port); }
                catch
                {
                    WriteHtml(client, 503, L10n.T("<h3>网页端未就绪</h3><p>App 正在启动，稍后刷新。</p>"));
                    return;
                }

                var down = client.GetStream();
                var up = upstream.GetStream();

                byte[] head = ReadHeaderBlock(down, 64 * 1024);
                if (head == null) return;
                string text = Encoding.UTF8.GetString(head);
                string rewritten = RewriteHost(text, "127.0.0.1:" + WebServer.Port);
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                up.Write(bytes, 0, bytes.Length);
                up.Flush();

                var upTask = new Thread(() =>
                {
                    try { down.CopyTo(up, 32768); } catch { }
                    try { upstream.Client.Shutdown(SocketShutdown.Send); } catch { }
                }) { IsBackground = true };
                upTask.Start();
                try { up.CopyTo(down, 32768); } catch { }
                try { down.Flush(); } catch { }
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { upTask.Join(500); } catch { }
            }
        }

        /* ===================== 影音大文件直连流式（视频 / 图片） =====================
         *
         * 视频 / 图片原图是网页端最大的两类传输：浏览器播放视频时会按播放进度**连续发起多个
         * Range 请求**（缓冲、拖动进度条、seek），每个 Range 都是一次新的请求。早期统一走
         * Proxy → 6789(HttpListener) 再回环取文件，每次 Range 都要重新建一条到 6789 的 TCP
         * 连接 + HttpListener 解析 + 文件读取，局域网下的连接建立开销叠加起来就是「视频一卡一卡」。
         *
         * 这里把 /api/media/stream 与 /api/media/image 在网关线程里**直接读文件**并分块写出，
         * 同时开启 HTTP/1.1 keep-alive：浏览器可以复用同一条 TCP 连接顺序发起多个 Range 请求，
         * 网关在同一连接上循环读请求、按 Range 切片回文件，从而做到真正的流式、平滑播放。
         * 其余小请求（state / art / lyrics / 封面缩略图等）仍走 Proxy，不受影响。
         * ============================================================================ */

        /// <summary>
        /// 视频流 / 图片原图 / 缩略图的直连流式出口：复用同一条客户端连接（keep-alive），
        /// 循环读取请求、按 Range 切片写文件内容。只在对外网关（7790）这条 LAN 链路上启用，
        /// App 内置 WebView（6800）走 HandleApp → ServeMediaLibraryApi，不经过这里。
        /// </summary>
        private static void ServeMediaStreamDirect(TcpClient client, string route)
        {
            // 空闲（keep-alive 等待下一个 Range）时给一个合理超时，避免连接空挂占用资源；
            // 超时读到 null 即优雅关闭，浏览器需要时自行重连。
            try { client.ReceiveTimeout = 20000; } catch { }
            try { client.SendTimeout = 30000; } catch { }
            try { client.SendBufferSize = 256 * 1024; } catch { }
            var down = client.GetStream();
            bool keepAlive = true;
            try
            {
                while (keepAlive)
                {
                    // 阻塞读取请求头（受 ReceiveTimeout 约束，空闲超时 → null → 关闭）
                    byte[] head = ReadHeaderBlockBlock(down, 64 * 1024);
                    if (head == null) break;

                    string req = Encoding.ASCII.GetString(head);
                    var lines = req.Replace("\r\n", "\n").Split('\n');
                    if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) break;
                    var rl = lines[0].Split(' ');
                    string method = rl.Length > 0 ? rl[0].ToUpperInvariant() : "GET";
                    string target = rl.Length > 1 ? rl[1] : "/";
                    string httpVer = rl.Length > 2 ? rl[2].Trim().ToUpperInvariant() : "HTTP/1.1";

                    // HTTP/1.1 默认 keep-alive；HTTP/1.0 默认关闭，除非显式 keep-alive。
                    bool clientKeepAlive = httpVer.IndexOf("HTTP/1.0") < 0;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int c = lines[i].IndexOf(':');
                        if (c <= 0) continue;
                        headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
                    }
                    if (headers.TryGetValue("Connection", out var conn) &&
                        string.Equals(conn, "close", StringComparison.OrdinalIgnoreCase))
                        clientKeepAlive = false;

                    int qi = target.IndexOf('?');
                    var query = ParseQuery(qi >= 0 ? target.Substring(qi + 1) : "");

                    bool isImage = route.IndexOf("/image", StringComparison.OrdinalIgnoreCase) >= 0;
                    ServeFileRangeDirect(down, method, query, isImage, headers);

                    // 始终回 keep-alive 头；只有客户端明确要求 close 时才断。
                    keepAlive = clientKeepAlive;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "媒体直连流式结束：" + ex.Message);
            }
            finally
            {
                try { down.Flush(); } catch { }
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        }

        /// <summary>按 Range 读取文件并分块写出；支持 200 / 206 / 404，并带 keep-alive 头。</summary>
        private static void ServeFileRangeDirect(Stream down, string method,
            Dictionary<string, string> q, bool isImage, Dictionary<string, string> reqHeaders)
        {
            string path = q.TryGetValue("path", out var pv) ? pv : "";
            string file = null;
            string mime = "application/octet-stream";
            string dispName = "";
            bool dl = isImage && q.TryGetValue("dl", out var dlv) && dlv == "1";

            if (isImage)
            {
                var photo = PhotoLibrary.Find(path);
                if (photo == null || !File.Exists(photo.Path))
                { WriteStatusBlock(down, 404, L10n.T("{\"error\":\"图库里没有这个文件\"}")); return; }

                int w = 0;
                int.TryParse(q.TryGetValue("w", out var wv) ? wv : "0", out w);
                if (w < 0) w = 0;
                if (w > 1600) w = 1600;
                file = w > 0 ? PhotoLibrary.ThumbPath(photo, w) : photo.Path;
                bool thumb = w > 0 && !string.IsNullOrEmpty(file) && !string.Equals(file, photo.Path);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) { file = photo.Path; thumb = false; }
                mime = thumb ? "image/jpeg" : PhotoLibrary.Mime(photo.Path);
                dispName = photo.Name;
            }
            else
            {
                var song = MusicLibrary.Find(path);
                if (song == null || !File.Exists(song.Path))
                { WriteStatusBlock(down, 404, L10n.T("{\"error\":\"曲库里没有这个文件\"}")); return; }
                file = song.Path;
                mime = StreamMime(song.Path);
            }

            long total = new FileInfo(file).Length;
            long from = 0, to = total - 1;
            bool partial = false;
            if (reqHeaders.TryGetValue("Range", out var range) &&
                range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var spec = range.Substring(6).Split('-');
                    if (!string.IsNullOrEmpty(spec[0]))
                    {
                        long.TryParse(spec[0], out from);
                        if (spec.Length > 1 && !string.IsNullOrEmpty(spec[1]))
                            long.TryParse(spec[1], out to);
                    }
                    else if (spec.Length > 1 && !string.IsNullOrEmpty(spec[1]))
                    {
                        long suffix = 0;
                        long.TryParse(spec[1], out suffix);
                        from = Math.Max(0, total - suffix);
                    }
                    if (from < 0) from = 0;
                    if (to >= total || to < from) to = total - 1;
                    partial = true;
                }
                catch { from = 0; to = total - 1; partial = false; }
            }

            long length = to - from + 1;
            int code = partial ? 206 : 200;

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 " + code + (partial ? " Partial Content" : " OK") + "\r\n");
            sb.Append("Content-Type: " + mime + "\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Content-Length: " + length + "\r\n");
            if (partial)
                sb.Append("Content-Range: bytes " + from + "-" + to + "/" + total + "\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Cache-Control: " + (isImage ? "private, max-age=300" : "no-store") + "\r\n");
            if (dl)
                sb.Append("Content-Disposition: attachment; filename*=UTF-8''" +
                          Uri.EscapeDataString(dispName) + "\r\n");
            sb.Append("Connection: keep-alive\r\n");
            sb.Append("Keep-Alive: timeout=20\r\n");
            sb.Append("\r\n");
            var hb = Encoding.ASCII.GetBytes(sb.ToString());
            down.Write(hb, 0, hb.Length);

            if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            { try { down.Flush(); } catch { } return; }

            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(from, SeekOrigin.Begin);
                    var buf = new byte[64 * 1024];
                    long left = length;
                    while (left > 0)
                    {
                        int want = (int)Math.Min(buf.Length, left);
                        int got = fs.Read(buf, 0, want);
                        if (got <= 0) break;
                        down.Write(buf, 0, got);
                        left -= got;
                    }
                    down.Flush();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "媒体直连写出失败：" + ex.Message);
                try { down.Flush(); } catch { }
            }
        }

        /// <summary>写一个小体积的状态响应（JSON 错误 / 短文本），带 keep-alive 头。</summary>
        private static void WriteStatusBlock(Stream down, int code, string body)
        {
            var b = Encoding.UTF8.GetBytes(body ?? "");
            var head = "HTTP/1.1 " + code + " Error\r\n"
                     + "Content-Type: application/json; charset=utf-8\r\n"
                     + "Content-Length: " + b.Length + "\r\n"
                     + "Access-Control-Allow-Origin: *\r\n"
                     + "Cache-Control: no-store\r\n"
                     + "Connection: keep-alive\r\n"
                     + "Keep-Alive: timeout=20\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            down.Write(hb, 0, hb.Length);
            down.Write(b, 0, b.Length);
            try { down.Flush(); } catch { }
        }

        /// <summary>阻塞式读取 HTTP 头（受 socket ReceiveTimeout 约束），超时 / 断开返回 null。</summary>
        private static byte[] ReadHeaderBlockBlock(Stream s, int limit)
        {
            var ms = new MemoryStream();
            var buf = new byte[1024];
            int state = 0;
            while (ms.Length < limit)
            {
                int n;
                try { n = s.Read(buf, 0, buf.Length); }
                catch { return ms.Length > 0 ? ms.ToArray() : null; }
                if (n <= 0) return ms.Length > 0 ? ms.ToArray() : null;
                ms.Write(buf, 0, n);
                for (int i = 0; i < n; i++)
                {
                    if ((state == 0 || state == 2) && buf[i] == (byte)'\r') state++;
                    else if ((state == 1 || state == 3) && buf[i] == (byte)'\n') state++;
                    else state = 0;
                    if (state == 4) return ms.ToArray();
                }
            }
            return ms.ToArray();
        }

        /// <summary>解析 query 字符串（a=b&amp;c=d），自动 URL 解码。</summary>
        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            foreach (var kv in query.Split('&'))
            {
                if (string.IsNullOrEmpty(kv)) continue;
                int eq = kv.IndexOf('=');
                string k = eq >= 0 ? kv.Substring(0, eq) : kv;
                string v = eq >= 0 ? kv.Substring(eq + 1) : "";
                try { k = Uri.UnescapeDataString(k.Replace("+", " ")); v = Uri.UnescapeDataString(v.Replace("+", " ")); }
                catch { }
                d[k] = v;
            }
            return d;
        }

        /// <summary>HTTPS 侧的反代：把解密后的请求流转给 127.0.0.1:6789。</summary>
        private static void ProxyStream(Stream down, TcpClient client)
        {
            using (var upstream = new TcpClient())
            {
                try { upstream.Connect(IPAddress.Loopback, WebServer.Port); }
                catch
                {
                    WriteHtmlStream(down, 503, L10n.T("<h3>网页端未就绪</h3><p>App 正在启动，稍后刷新。</p>"));
                    return;
                }
                var up = upstream.GetStream();

                byte[] head = ReadHeaderBlock(down, 64 * 1024);
                if (head == null) return;
                string rewritten = RewriteHost(Encoding.UTF8.GetString(head), "127.0.0.1:" + WebServer.Port);
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                up.Write(bytes, 0, bytes.Length);
                up.Flush();

                var upTask = new Thread(() =>
                {
                    try { down.CopyTo(up, 32768); } catch { }
                    try { upstream.Client.Shutdown(SocketShutdown.Send); } catch { }
                }) { IsBackground = true };
                upTask.Start();
                try { up.CopyTo(down, 32768); } catch { }
                try { down.Flush(); } catch { }
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { upTask.Join(500); } catch { }
            }
        }

        private static void WriteHtmlStream(Stream s, int code, string body)
        {
            try
            {
                var b = Encoding.UTF8.GetBytes(body ?? "");
                string head = "HTTP/1.1 " + code + " Error\r\nContent-Type: text/html; charset=utf-8\r\n"
                    + "Content-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
                var hb = Encoding.UTF8.GetBytes(head);
                s.Write(hb, 0, hb.Length);
                s.Write(b, 0, b.Length);
                s.Flush();
            }
            catch { }
        }

        private static void RedirectStream(Stream s, string location)
        {
            try
            {
                string head = "HTTP/1.1 302 Found\r\nLocation: " + location
                    + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                s.Write(hb, 0, hb.Length);
                s.Flush();
            }
            catch { }
        }

        /// <summary>把「已经读过的一段字节」垫在真实流前面，下游读到的仍是完整请求。</summary>
        private sealed class PrefixStream : Stream
        {
            private readonly Stream _inner;
            private readonly byte[] _prefix;
            private int _pos;
            private readonly int _len;
            public PrefixStream(Stream inner, byte[] prefix, int count)
            {
                _inner = inner;
                _prefix = prefix;
                _pos = 0;
                _len = count;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos < _len)
                {
                    int n = Math.Min(count, _len - _pos);
                    Buffer.BlockCopy(_prefix, _pos, buffer, offset, n);
                    _pos += n;
                    return n;
                }
                return _inner.Read(buffer, offset, count);
            }
            public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }
            public override void Flush() { _inner.Flush(); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return _inner.CanWrite; } }
            public override long Length { get { return _inner.Length; } }
            public override long Position { get { return _pos; } set { } }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { }
        }

        private static string RewriteHost(string head, string host)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "Host: " + host;
                }
            }
            return string.Join("\r\n", lines);
        }

        private static byte[] ReadHeaderBlock(Stream s, int limit)
        {
            var ms = new MemoryStream();
            int state = 0;
            var one = new byte[1];
            while (ms.Length < limit)
            {
                int n;
                try { n = s.Read(one, 0, 1); } catch { return null; }
                if (n <= 0) return ms.Length > 0 ? ms.ToArray() : null;
                ms.WriteByte(one[0]);
                if ((state == 0 || state == 2) && one[0] == (byte)'\r') state++;
                else if ((state == 1 || state == 3) && one[0] == (byte)'\n') state++;
                else state = 0;
                if (state == 4) break;
            }
            return ms.ToArray();
        }

        /* ===================== 简易页面输出 ===================== */

        public static void WriteHtml(TcpClient client, int code, string html)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(html);
                var head = "HTTP/1.1 " + code + " OK\r\n"
                         + "Content-Type: text/html; charset=utf-8\r\n"
                         + "Content-Length: " + body.Length + "\r\n"
                         + "Cache-Control: no-store\r\n"
                         + "Connection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                var ns = client.GetStream();
                ns.Write(hb, 0, hb.Length);
                ns.Write(body, 0, body.Length);
                ns.Flush();
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        /// <summary>给 HttpListener 响应写 302 重定向（供内置网页引擎在 6789 上用）。</summary>
        internal static void RedirectTo(HttpListenerContext ctx, string location)
        {
            try
            {
                var res = ctx.Response;
                res.StatusCode = 302;
                res.AddHeader("Location", location);
                res.ContentType = "text/html; charset=utf-8";
                var b = Encoding.UTF8.GetBytes("<meta http-equiv=refresh content='0;url=" + location + "'>");
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b, 0, b.Length);
                res.OutputStream.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        /// <summary>给 HttpListener 响应输出导航页（与对外前端共用 HubPage）。</summary>
        internal static void ServeHubPage(HttpListenerContext ctx)
        {
            WriteText(ctx.Response, 200, "text/html; charset=utf-8", HubPage());
        }

        private static void WriteText(HttpListenerResponse res, int code, string contentType, string body)
        {
            try
            {
                var b = Encoding.UTF8.GetBytes(body ?? "");
                res.StatusCode = code;
                res.ContentType = contentType;
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b, 0, b.Length);
                res.OutputStream.Close();
            }
            catch { try { res.Abort(); } catch { } }
        }

        private const string Css =
            "body{margin:0;background:#0f1115;color:#e6e8eb;font:15px/1.65 -apple-system,'PingFang SC','Microsoft YaHei',sans-serif}"
            + "header{padding:18px 20px 6px}h1{font-size:19px;margin:0 0 4px}p.sub{margin:0;color:#8b929c;font-size:12px}"
            + ".list{padding:10px 14px 30px}"
            + ".card{display:block;text-decoration:none;color:inherit;background:#181b21;border:1px solid #23272f;"
            + "border-radius:12px;padding:14px 16px;margin:10px 0}"
            + ".card.off{opacity:.45;cursor:not-allowed}"
            + ".card b{display:block;font-size:15px;margin-bottom:3px}"
            + ".card span{color:#8b929c;font-size:12px;word-break:break-all}"
            + "code{background:#22272f;padding:1px 5px;border-radius:5px;font-size:12px}";

        private static string HubPage(string host = null)
        {
            var ip = string.IsNullOrEmpty(host) ? (DeviceStatus.Ip ?? "127.0.0.1") : host;
            var cam = IpCameraServer.Instance;
            bool camOn = cam != null && cam.IsRunning;
            bool drawOn = ScreenDrawService.IsRunning;

            // 已开启的卡片与未开启的卡片分开收集：默认顺序见下，
            // 未开启的功能统一排到最后且不可点击（Card 渲染成无 href 的 div）。
            var enabled = new StringBuilder();
            var disabled = new StringBuilder();
            AddCard(enabled, disabled, "/media", L10n.T("影音（网页版）"),
                L10n.T("在线浏览图片 / 播放音乐 / 视频，与 App 共用曲库、歌单、喜欢"), true, host);
            AddCard(enabled, disabled, "/download", L10n.T("下载（网页版）"),
                L10n.T("离线保存音乐 / 视频 / 小说，支持失败重试与目录提醒"), true, host);
            AddCard(enabled, disabled, "/reader", L10n.T("阅读（网页版）"),
                L10n.T("在线阅读 TXT / EPUB / PDF，支持上传书籍与书源"), true, host);
            string shareRoot = LanShareService.ShareRoot;
            if (string.IsNullOrEmpty(shareRoot)) shareRoot = LanShareService.EnsureShareRoot();
            AddCard(enabled, disabled, "/file", L10n.T("文件共享"),
                L10n.T("浏览并下载「") + shareRoot + L10n.T("」里的文件"), true, host);
            AddCard(enabled, disabled, "/chat", L10n.T("聊天（网页端）"),
                L10n.T("浏览器打开聊天界面（与 App 同源后端）"), true, host);
            AddCard(enabled, disabled, "/draw", L10n.T("画笔共享"),
                drawOn ? L10n.T("正在共享画笔画面 · 点开观看") : L10n.T("未开启（手机上打开屏幕画笔即自动共享）"), drawOn, host);
            AddCard(enabled, disabled, "/cam", L10n.T("屏幕共享（摄像头）"), camOn
                ? (cam.Source == ShareSource.Screen ? "正在共享本机屏幕" : "正在共享本机摄像头") + " · 点开观看"
                : L10n.T("未开启（在手机上点「共享摄像头」或「共享屏幕」）"), camOn, host);

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=utf-8>");
            sb.Append("<meta name=viewport content='width=device-width,initial-scale=1'>");
            sb.Append(L10n.T("<title>AhoChat 服务</title><style>")).Append(Css).Append("</style></head><body>");
            sb.Append(L10n.T("<header><h1>AhoChat 本机服务</h1><p class=sub>"))
              .Append(ip).Append(L10n.T(" · 端口 ")).Append(_port).Append(L10n.T(" · 统一入口</p></header><div class=list>"));
            sb.Append(enabled).Append(disabled);
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        /// <summary>按是否开启把卡片追加到不同缓冲：开启的在前，未开启的排到最后。</summary>
        private static void AddCard(StringBuilder on, StringBuilder off,
            string path, string title, string desc, bool enabled, string host = null)
        {
            (enabled ? on : off).Append(Card(path, title, desc, enabled, host));
        }

        private static string Card(string path, string title, string desc, bool on, string host = null)
        {
            var body = "<b>" + title + "</b><span>" + desc + "<br>" + UrlOf(path, host) + "</span>";
            // 已开启 → 可点击链接；未开启 → 无 href 的 div，点击无反应。
            return on
                ? "<a class='card' href='" + path + "'>" + body + "</a>"
                : "<div class='card off' aria-disabled='true'>" + body + "</div>";
        }

        private static string OfflinePage(string host = null)
        {
            return "<!doctype html><html><head><meta charset=utf-8>"
                 + "<meta name=viewport content='width=device-width,initial-scale=1'>"
                 + L10n.T("<title>未开启共享</title><style>") + Css + "</style></head><body>"
                 + L10n.T("<header><h1>摄像头 / 屏幕共享未开启</h1>")
                 + L10n.T("<p class=sub>请在手机上进入 AhoChat → 聊天页右下角「+」→ 共享摄像头 / 共享屏幕。</p></header>")
                 + L10n.T("<div class=list><a class='card' href='/hub'><b>返回服务列表</b><span>") + UrlOf("/hub", host) + "</span></a></div>"
                 + "</body></html>";
        }
    }
}

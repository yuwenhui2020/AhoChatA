using System;
using Android.App;
using Android.Content;
using Android.Widget;
using Android.Util;
using AhoChatA.Vod;

namespace AhoChatA
{
    /// <summary>
    /// 进程级单例宿主。
    ///
    /// 重构前：一切都在 MainActivity 里创建，而 MainActivity 是一个 WebView 容器，
    /// 于是「App = 一个网页」；网页不加载，聊天就不存在。
    ///
    /// 重构后：桥接、消息仓库、AhoChatA 服务、文件共享、前台服务由 CoreHost 统一持有，
    /// 与任何界面无关：
    ///   · ChatActivity（原生聊天界面）是 App 的入口，不创建 WebView；
    ///   · MainActivity（网页端）按需打开，把自己的 WebView 挂到同一个 Bridge 上，
    ///     网页端能力（内置浏览器、蓝牙、媒体播放器）原样保留；
    ///   · 局域网浏览器访问 手机IP:7790（统一网关）反代到内置网页后端，走的仍是同一份后端。
    /// </summary>
    public static class CoreHost
    {
        private static readonly object gate = new object();
        private static readonly object webStartGate = new object();
        private static int _bgStarted;
        private static volatile WebServer _web;
        private static bool _webRetryInProgress;
        private static bool _webDependentsStarted;
        private static readonly System.Collections.Generic.List<Action<WebServer.ServiceHealth>> _webRetryCallbacks =
            new System.Collections.Generic.List<Action<WebServer.ServiceHealth>>();

        public static MchatBridge Bridge { get; private set; }
        public static WebServer AhoChatA { get { return _web; } }
        public static Activity Host { get; private set; }

        public static bool Ready { get { return Bridge != null; } }

        /// <summary>
        /// 进程启动时（Application.OnCreate）预建 Bridge。
        /// 只做「聊天界面渲染必须要用」的部分：身份、会话仓库、频道注册表。
        /// 不起任何服务、不碰网络——那些都留给后台线程。
        /// </summary>
        public static void Preinit(global::Android.Content.Context ctx)
        {
            if (ctx == null) return;
            lock (gate)
            {
                if (Bridge != null) return;
                Perf.Mark("Preinit：新建 Bridge（进程启动期）");
                Bridge = CreateBridge(ctx);
                HookBridgeEvents(Bridge, ctx);
                Perf.Mark("Preinit：Bridge 就绪");
            }
        }

        /// <summary>
        /// Bridge 工厂。Control 故意留空：它需要 Activity 才能 startActivity / 弹界面，
        /// 由 Ensure(activity) 补上（Preinit 阶段拿不到 Activity）。
        /// </summary>
        private static MchatBridge CreateBridge(global::Android.Content.Context ctx)
        {
            // 通知渠道尽早初始化：冷启动时消息可能在 Bridge.Start() 之前就到达，
            // 若等到 Start() 里才 Init，早到的消息会因 appCtx==null 被静默跳过、不弹通知。
            try { ChatNotifier.Init(ctx); } catch { }
            return new MchatBridge(ctx, null);
        }

        /// <summary>幂等初始化：第一次调用时把整套后台能力拉起来。</summary>
        public static void Ensure(Activity activity)
        {
            if (activity == null) return;
            lock (gate)
            {
                if (Bridge != null)
                {
                    // 已由 Application 预建：这里只补 Host 与界面相关的执行器，然后拉起后台服务
                    Host = activity;
                    Bridge.SetActivity(activity);
                    if (Bridge.Control == null) Bridge.Control = new ControlExecutor(activity);
                    StartBackgroundServicesDeferred(activity);
                    return;
                }
                Host = activity;
                Perf.Mark("CoreHost.Ensure 开始");

                // 桥接不传 WebView：原生界面模式下没有任何 JS 依赖
                Perf.Mark("→ 新建 MchatBridge");
                Bridge = CreateBridge(activity);
                Bridge.SetActivity(activity);
                Perf.Mark("← Bridge 构造完成");

                HookBridgeEvents(Bridge, activity);

                // ===== 启动加速：以下服务全部与界面无关，推到子线程 =====
                // 实测同步执行要 1.4 秒（WebServer 430ms + Bridge.Start 430ms + HTTPS 证书 140ms），
                // 用户点图标后只能干等。现在 OnCreate 只保留 Bridge 构造（聊天列表要用），
                // 其余立刻在后台拉起，冷启动从 ~2.6s 降到 ~1s 以内。
                StartBackgroundServicesDeferred(activity);
            }
        }

        /// <summary>Bridge 的全局事件（与具体 Activity 无关的部分）。</summary>
        private static void HookBridgeEvents(MchatBridge b, global::Android.Content.Context ctx)
        {
            // 收到无前缀普通消息 → 系统剪贴板（App 退到后台也要生效，
            // 因此放在原生侧而不是前端）。
            b.PlainTextReceived += text =>
            {
                try { AppHostBridge.CopyText(ctx, text); } catch { }
                try
                {
                    var h = new Android.OS.Handler(Android.OS.Looper.MainLooper);
                    h.Post(() =>
                    {
                        try { L10n.Toast(ctx, text, ToastLength.Short).Show(); } catch { }
                    });
                }
                catch { }
            };
        }

        /// <summary>
        /// 只用 Context 拉起后台（开机自启 / 前台服务被系统重启时用，没有 Activity）。
        /// 幂等：已启动则直接返回。
        /// </summary>
        public static void EnsureBackground(Context ctx)
        {
            if (ctx == null) return;
            lock (gate)
            {
                if (Bridge == null)
                {
                    Bridge = CreateBridge(ctx);
                    HookBridgeEvents(Bridge, ctx);
                }
            }
            StartBackgroundServicesDeferred(ctx);
        }

        /// <summary>把后台服务丢到子线程；重复调用是安全的（内部有幂等保护）。</summary>
        private static void StartBackgroundServicesDeferred(Context ctx)
        {
            // Interlocked 幂等：ChatForegroundService.OnCreate 与 ChatActivity.OnCreate
            // 可能并发进入，原来的 bool 判断在锁外会重复拉起整套服务。
            if (System.Threading.Interlocked.CompareExchange(ref _bgStarted, 1, 0) != 0) return;
            Perf.Mark("→ 后台启动服务");
            var host = ctx;
            var bg = new System.Threading.Thread(() =>
            {
                try { StartBackgroundServices(host); }
                catch (Exception ex)
                { Android.Util.Log.Error("Mchat", "后台服务启动失败: " + ex.Message); }
            }) { IsBackground = true, Name = "corehost-bg" };
            bg.Start();
        }

        /// <summary>
        /// 后台服务：前台通知、内置网页端、统一 HTTP 入口（7790 + HTTPS 7791）、
        /// 文件共享（FTP）、短信监听、局域网发现 + qaul 节点。
        /// 放到子线程跑，避免拖慢冷启动首屏。
        /// </summary>
        private static void StartBackgroundServices(Context ctx)
        {
            var app = ctx.ApplicationContext ?? ctx;
            var startupBridge = Bridge;

            // 常驻前台服务：后台收消息 + 音乐后台播放。必须最先且很快。
            try { ChatForegroundService.Start(app); } catch { }
            Perf.Mark("后台：前台服务已启动");

            // ===== 关键修复：局域网发现 与 AhoChatA 服务并行，互不阻塞 =====
            // 以前二者串在同一条后台线程上，且 WebServer 排在前面：构造期同步解压
            // 12MB/327 个文件 + HTTPS 证书解析，全部耗时都顺延给「发现」，
            // 于是用户感觉「AhoChatA 服务器慢，局域网发现也慢」。现在发现立刻独立起跑，
            // 不再等任何一个 HTTP 端口就绪；每项独立 try/catch，互不牵连。
            // 证书解析 / 字体度量等预热也独立并行，真正起到「提前预热」的作用
            // （原来它排在整条链最后，证书早被 StartHttps 解析完了，纯属白跑）。
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Warmup.Precreate(); } catch { }
                Perf.Mark("后台：预热完成（并行）");
            });

            System.Threading.Tasks.Task.Run(() =>
            {
                Perf.Mark("→ Bridge.Start（并行，最高优先）");
                try { startupBridge.Start(); }
                catch (Exception ex) { Android.Util.Log.Error("Mchat", "Bridge 启动失败: " + ex.Message); }
                Perf.Mark("后台：Bridge.Start 完成");
            });

            System.Threading.Tasks.Task.Run(() =>
            {
                lock (webStartGate)
                {
                    WebServer started = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    WebServer candidate = null;
                    try
                    {
                        lock (gate)
                            if (!object.ReferenceEquals(Bridge, startupBridge) || _web != null) break;
                        candidate = new WebServer(startupBridge, app);
                        candidate.Start();
                        lock (gate)
                        {
                            if (object.ReferenceEquals(Bridge, startupBridge) && _web == null)
                            {
                                _web = candidate;
                                started = candidate;
                                candidate = null;
                            }
                        }
                        if (started != null)
                        {
                            Perf.Mark("后台：WebServer 已启动（attempt=" + attempt + ", front=" + WebServer.IsRunning
                                + ", app=" + started.AppListenerRunning + ", port=" + WebServer.ExternalPortActual + ")");
                            break;
                        }
                        try { candidate?.Dispose(); } catch { }
                        candidate = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Error("Mchat", "启动 WebServer 失败 attempt=" + attempt + ": " + ex.ToString());
                        try { candidate?.Dispose(); } catch (Exception disposeEx)
                        { Android.Util.Log.Error("Mchat", "清理失败的 WebServer 异常: " + disposeEx.ToString()); }
                        if (attempt < 2) try { System.Threading.Thread.Sleep(250); } catch { }
                    }
                }
                if (started == null)
                    Android.Util.Log.Error("Mchat", "WebServer 启动最终失败或已取消（后台启动闸门保持，避免重复启动）");
                else
                    StartWebDependents(app, startupBridge);
                }
            });

            // 视频本地缓存代理（127.0.0.1:8891）：多线程落盘缓冲 + 遥测；
            // 进程启动先清 3 天前的过期缓冲文件。代理独立于 WebServer，启动失败自动降级直连。
            try { VodConfig.SweepDiskCache(); } catch { }
            System.Threading.Tasks.Task.Run(() =>
            {
                try { VideoCacheProxy.Instance.Start(); }
                catch (Exception ex) { Android.Util.Log.Error("Mchat", "启动 VideoCacheProxy 失败: " + ex.Message); }
            });
        }

        private static void StartWebDependents(Context app, MchatBridge expectedBridge = null)
        {
            lock (gate)
            {
                if (_web == null || (expectedBridge != null && !object.ReferenceEquals(Bridge, expectedBridge)) || _webDependentsStarted) return;
                _webDependentsStarted = true;
            }

            try { MusicSync.Start(); }
            catch (Exception ex) { Android.Util.Log.Error("Mchat", "启动 MusicSync 失败: " + ex.ToString()); }
            try { LanShareService.Start(app); }
            catch (Exception ex) { Android.Util.Log.Error("Mchat", "启动文件共享失败: " + ex.ToString()); }
            Perf.Mark("后台：文件共享已启动");
            try { SmsInboxWatcher.EnsureStarted(app); }
            catch (Exception ex) { Android.Util.Log.Error("Mchat", "启动短信监听失败: " + ex.ToString()); }
        }

        /// <summary>WebServer 初始启动失败时由设置页调用；不触碰后台服务全局闸门。</summary>
        public static void RetryWebServer(Context ctx, Action<WebServer.ServiceHealth> onDone = null)
        {
            if (ctx == null) return;
            var app = ctx.ApplicationContext ?? ctx;
            bool start = false;
            bool missingBridge = false;
            bool existingWeb = false;
            MchatBridge bridge = null;
            lock (gate)
            {
                if (Bridge == null)
                {
                    missingBridge = true;
                    Android.Util.Log.Warn("Mchat", "RetryWebServer 跳过：Bridge 尚未就绪");
                }
                else if (_web == null)
                {
                    bridge = Bridge;
                    if (onDone != null) _webRetryCallbacks.Add(onDone);
                    if (!_webRetryInProgress)
                    {
                        _webRetryInProgress = true;
                        start = true;
                    }
                }
                else
                    existingWeb = true;
            }

            if (!start)
            {
                if (missingBridge && onDone != null)
                    System.Threading.Tasks.Task.Run(() => { try { onDone(WebServer.ServiceHealth.NotRunning); } catch { } });
                else if (existingWeb && onDone != null)
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        WebServer.ServiceHealth h;
                        try { h = WebServer.ProbeHealth(); } catch { h = WebServer.ServiceHealth.Unresponsive; }
                        try { onDone(h); } catch { }
                    });
                else if (CoreHost.AhoChatA != null && onDone != null)
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        WebServer.ServiceHealth h;
                        try { h = WebServer.ProbeHealth(); } catch { h = WebServer.ServiceHealth.Unresponsive; }
                        try { onDone(h); } catch { }
                    });
                return;
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                lock (webStartGate)
                {
                    WebServer selected = null;
                try
                {
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        lock (gate)
                        {
                            if (_web != null) { selected = _web; break; }
                            if (!object.ReferenceEquals(Bridge, bridge)) break;
                        }
                        WebServer candidate = null;
                        try
                        {
                            candidate = new WebServer(bridge, app);
                            candidate.Start();
                            lock (gate)
                            {
                                if (!object.ReferenceEquals(Bridge, bridge) || _web != null)
                                    Android.Util.Log.Warn("Mchat", "RetryWebServer 放弃发布：Bridge 已更换或服务已存在");
                                else
                                {
                                    _web = candidate;
                                    selected = candidate;
                                    candidate = null;
                                }
                            }
                            if (selected != null) break;
                        }
                        catch (Exception ex)
                        {
                            Android.Util.Log.Error("Mchat", "RetryWebServer 失败 attempt=" + attempt + ": " + ex.ToString());
                        }
                        finally
                        {
                            try { candidate?.Dispose(); }
                            catch (Exception ex) { Android.Util.Log.Error("Mchat", "RetryWebServer 清理失败: " + ex.ToString()); }
                        }
                        if (attempt < 2) try { System.Threading.Thread.Sleep(250); } catch { }
                    }
                    if (selected != null) StartWebDependents(app, bridge);
                }
                finally
                {
                    System.Collections.Generic.List<Action<WebServer.ServiceHealth>> callbacks;
                    lock (gate)
                    {
                        _webRetryInProgress = false;
                        callbacks = new System.Collections.Generic.List<Action<WebServer.ServiceHealth>>(_webRetryCallbacks);
                        _webRetryCallbacks.Clear();
                    }
                    WebServer.ServiceHealth health;
                    try { health = selected != null ? WebServer.ProbeHealth() : WebServer.ServiceHealth.NotRunning; }
                    catch { health = WebServer.ServiceHealth.Unresponsive; }
                    foreach (var callback in callbacks) try { callback(health); } catch { }
                }
                }
            });
        }


        /// <summary>网页端模式：把 WebView 挂到现有 Bridge 上；传 null 表示解绑。</summary>
        public static void AttachWebView(Android.Webkit.WebView view)
        {
            try { Bridge?.AttachWebView(view); } catch { }
        }

        public static string WebUrl
        {
            get { return "http://127.0.0.1:" + WebServer.Port + "/"; }
        }

        /// <summary>App 完全退出时释放（悬浮窗仍在运行时由调用方决定是否跳过）。</summary>
        public static void Shutdown()
        {
            lock (webStartGate)
            {
                MchatBridge bridge;
                WebServer web;
                lock (gate)
                {
                    bridge = Bridge;
                    web = _web;
                    Bridge = null;
                    _web = null;
                    _webDependentsStarted = false;
                    Host = null;
                    System.Threading.Interlocked.Exchange(ref _bgStarted, 0);
                }
                try { bridge?.Shutdown(); } catch { }
                try { MusicSync.Stop(); } catch { }
                try { web?.Stop(); web?.Dispose(); } catch { }
            }
        }
    }
}

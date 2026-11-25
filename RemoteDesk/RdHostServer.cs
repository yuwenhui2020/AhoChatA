using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Widget;
using DeskShare;
using desk;

namespace AhoChatA
{
    /// <summary>
    /// 安卓被控端：把本机屏幕与输入接入 desk 的远控协议（与 Windows 被控端同一套报文）。
    ///
    /// 这样"谁来控这台手机"就不需要另写一套协议了：
    ///   · Windows 的查看端（ViewerForm）—— 原来的局域网直连/设备码寻址直接可用；
    ///   · 另一台安卓（RemoteDeskActivity）—— RdSession 现成能解整帧 JPEG；
    ///   · 网页端 —— WebServer 的 /rd 目标会话（同样只说这套协议）。
    ///
    /// 安全模型与 Windows 被控端完全一致（Shared/RdSecure.cs 同一份源码）：
    ///   临时 X25519 握手 + HKDF 派生双向密钥 + 设备码/连接密码密文鉴权 + 逐帧序号防重放。
    /// 安卓侧不做明文回退 —— 否则中间人掐掉握手就能强制降级。
    /// </summary>
    public sealed class RdHostServer
    {
        private const string Tag = "RdHost";
        private const string Prefs = "Mchat_rd_host";
        private const string KeyCode = "dc";
        private const string KeyPwd = "pw";
        private const string KeyPort = "port";
        private const string KeyLanDirect = "lanDirect";

        private static readonly RdHostServer _inst = new RdHostServer();
        public static RdHostServer Instance { get { return _inst; } }

        private readonly object _lock = new object();
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private RdSecureChannel _chan;
        private CancellationTokenSource _cts;
        private Thread _acceptThread;
        private Thread _sendThread;
        private Thread _readThread;
        private AutoResetEvent _frameSignal = new AutoResetEvent(false);
        private byte[] _pendingFrame;
        private int _framesSent;
        private long _lastStatAt;
        private volatile bool _running;
        private string _lastError;

        /// <summary>
        /// 协商后的能力集。默认 LegacyCaps（v1）：此时帧格式、Ping/Pong、QualitySettings
        /// 处理与改动前逐字节一致；只有收到查看端 Capability 并协商成功才启用 v2 行为。
        /// 每个新连接（Serve 结束、Stop）都会重置，避免继承上一位查看端的能力。
        /// </summary>
        private Protocol.Capability _caps = Protocol.LegacyCaps;

        /// <summary>
        /// 安卓被控端声明的能力集。H264Encode 由 RdH264Encoder（MediaCodec 硬编）真实落地，
        /// 查看端协商出该位后才会请求 H264；编码器起不来时采集器自动降级 JPEG 并回报。
        /// RegionFrame/InputPriority/UnicodeKey/NormalizedCoord/SecureOnly 为声明位：
        /// 输入协议本轮不改（MouseEventEx/KeyEventEx 走 default 忽略），仅表示能理解这套机制。
        /// </summary>
        private const Protocol.Capability HostCaps =
            Protocol.Capability.H264Encode |
            Protocol.Capability.RegionFrame |
            Protocol.Capability.Abr |
            Protocol.Capability.InputPriority |
            Protocol.Capability.Heartbeat |
            Protocol.Capability.UnicodeKey |
            Protocol.Capability.NormalizedCoord |
            Protocol.Capability.SecureOnly;

        /// <summary>QoS 帧头里的 frameId（仅协商出 Abr 时才随帧发送）。</summary>
        private int _qosFrameId;

        /// <summary>ABR 当前档位：JPEG 质量与帧率（仅协商出 Abr 时由 TestDelay 调整）。</summary>
        private int _abrQuality = ScreenShareSettings.DefaultJpegQuality;
        private int _abrFps = ScreenShareSettings.DefaultMaxFps;
        /// <summary>ABR 目标码率参考（kbps，来自 QualitySettings.BitrateKbps，0=不限）。</summary>
        private int _targetBitrateKbps;
        /// <summary>上次升档时刻（UTC ms），用于良好网络下每 3 秒才升一档的限频。</summary>
        private long _lastAbrRaiseAt;

        /// <summary>集中设置（首个 Start 时初始化）。硬编码 55 / 12 / 30 都改为从这里读。</summary>
        private ScreenShareSettings _settings;
        private ScreenShareSettings Settings
        {
            get { return _settings ?? (_settings = ScreenShareSettings.Instance(Application.Context)); }
        }

        /// <summary>背压「单一在制品」标志：1 = 上一帧还在发送/待发，采集线程据此忙时丢帧。</summary>
        private int _consumerBusy;

        /// <summary>远控采集器帧率上限（读集中设置，仍夹到 1..30 与 RdScreenGrabber 兼容）。</summary>
        private int FpsCap()
        {
            int cap = Settings.MaxFps;
            return cap < 1 ? 1 : (cap > 30 ? 30 : cap);
        }

        /// <summary>UTC 毫秒时间戳（Ping/Pong、QoS 帧头、ABR 限频共用；不用不确定的 TickCount64 API）。</summary>
        private static long UtcNowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>
        /// SupportedEncoding 旧字段的首字节（CodecAbility 位）。按 _caps 如实声明，不谎报。
        /// CodecAbility 枚举里没有 H264 位 —— H264 能力由 v2 的
        /// CapabilityAck(EncodeCapability(_caps, ...)) 里的 Capability.H264Encode 表达，
        /// 不占用这个旧字段。H264 虽已由 RdH264Encoder 落地，旧字段仍恒为 None；
        /// v1（LegacyCaps）时 { None, 8 } 与改动前逐字节一致。
        /// </summary>
        private byte EncodingAbilityByte()
        {
            return (byte)Protocol.CodecAbility.None;
        }

        public RdScreenGrabber Grabber { get; private set; }

        /// <summary>状态文本（连接/断开/错误），供界面显示。</summary>
        public static event Action<string> StatusChanged;
        private static void Notify(string s)
        {
            try { Log.Warn(Tag, s); } catch { }
            var cb = StatusChanged;
            if (cb != null) { try { cb(s); } catch { } }
        }

        public bool IsRunning { get { return _running; } }
        public bool HasViewer { get { return _client != null && _client.Connected; } }
        public string LastError { get { return _lastError; } }

        public int Port
        {
            get
            {
                try
                {
                    int p = Prefs_().GetInt(KeyPort, RdWire.DefaultRemotePort);
                    // 端口迁移：9000 是早期版本的默认远控端口，现在与电脑端统一到 9012。
                    // 只搬"恰好等于旧默认值"的，用户自己改过的端口尊重不动。
                    if (p == 9000) {
                        p = RdWire.DefaultRemotePort;
                        Prefs_().Edit().PutInt(KeyPort, p).Apply();
                    }
                    return p;
                }
                catch { return RdWire.DefaultRemotePort; }
            }
            set
            {
                try { Prefs_().Edit().PutInt(KeyPort, value).Apply(); } catch { }
            }
        }

        /// <summary>局域网内已发现本机的设备免设备码/密码直连。默认开启；
        /// 仍要求同网段 + 完整 X25519 加密握手，关闭后所有来访必须输设备码和密码。</summary>
        public bool AllowLanDirect
        {
            get { try { return Prefs_().GetBoolean(KeyLanDirect, true); } catch { return true; } }
            set { try { Prefs_().Edit().PutBoolean(KeyLanDirect, value).Apply(); } catch { } }
        }

        public string DeviceCode
        {
            get
            {
                try
                {
                    string v = Prefs_().GetString(KeyCode, null);
                    if (RdDeviceCode.IsValid(RdDeviceCode.Normalize(v ?? ""))) return RdDeviceCode.Normalize(v);
                }
                catch { }
                string gen = RdDeviceCode.New();
                try { Prefs_().Edit().PutString(KeyCode, gen).Apply(); } catch { }
                return gen;
            }
        }

        public string Password
        {
            get
            {
                try
                {
                    // 注意：这里必须区分"从没设置过"（返回 null）和"用户主动清空"（返回 ""）。
                    // 以前用 IsNullOrEmpty 判断，用户把密码清空后会被当成"没设置"，
                    // 立刻又自动生成一串新密码 —— 于是"密码为空"这件事永远做不到。
                    string v = Prefs_().GetString(KeyPwd, null);
                    if (v != null) return v;
                }
                catch { }
                string gen = NewPassword();
                try { Prefs_().Edit().PutString(KeyPwd, gen).Apply(); } catch { }
                return gen;
            }
            set
            {
                try { Prefs_().Edit().PutString(KeyPwd, value ?? "").Apply(); } catch { }
            }
        }

        public string RegeneratePassword()
        {
            Password = NewPassword();
            return Password;
        }

        public string RegenerateDeviceCode()
        {
            string gen = RdDeviceCode.New();
            try { Prefs_().Edit().PutString(KeyCode, gen).Apply(); } catch { }
            return gen;
        }

        private static string NewPassword()
        {
            // 8 位数字：电脑上能单手敲，又不至于被顺手猜中（局域网内部工具，够用）
            var rnd = new Random();
            var sb = new StringBuilder(8);
            for (int i = 0; i < 8; i++) sb.Append((char)('0' + rnd.Next(0, 10)));
            return sb.ToString();
        }

        private static ISharedPreferences Prefs_()
        {
            return Application.Context.GetSharedPreferences(Prefs, FileCreationMode.Private);
        }

        /// <summary>hello 广播里要带的远控端口（未开启返回 0，Windows 端据此显示"可远控"）。</summary>
        public static int AdvertisedPort
        {
            get { return _inst._running ? _inst.Port : 0; }
        }

        /// <summary>hello 广播里要带的设备码（未开启返回空，避免无谓暴露）。</summary>
        public static string AdvertisedCode
        {
            get { return _inst._running ? _inst.DeviceCode : ""; }
        }

        /* ===================== 启停 ===================== */

        /// <summary>启动被控：屏幕采集 + 监听端口。调用方必须先拿到 MediaProjection 授权。</summary>
        public bool Start(Context ctx, int resultCode, Intent data, int maxShortSide, int fps, int quality)
        {
            Stop();
            try
            {
                _settings = ScreenShareSettings.Instance(ctx);
                _consumerBusy = 0;
                _abrQuality = _settings.ClampQuality(_settings.JpegQuality);
                _abrFps = FpsCap();
                Grabber = new RdScreenGrabber();
                Grabber.JpegReady = (d, w, h) => OnFrame(d, w, h, Protocol.CODEC_JPEG);
                Grabber.H264Ready = (d, w, h) => OnFrame(d, w, h, Protocol.CODEC_H264);
                // 【背压】采集线程产帧前问一次下游是否忙：忙则直接丢帧不做 JPEG 压缩
                // （latest-value 合并 + 单一在制品 + 忙时丢帧，参考 ScreenStream）。
                Grabber.ShouldDropFrame = () => Interlocked.CompareExchange(ref _consumerBusy, 0, 0) != 0;
                _settings.LogEffective("RdHostServer.Start");
                Grabber.Stopped = delegate(string msg)
                {
                    _lastError = msg;
                    Notify(msg);
                };
                if (!Grabber.Start(ctx, resultCode, data, maxShortSide, fps, quality))
                {
                    _lastError = L10n.T("屏幕采集启动失败");
                    Stop();
                    return false;
                }

                int port = Port;
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _running = true;

                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rd-accept" };
                _acceptThread.Start();
                _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "rd-send" };
                _sendThread.Start();

                Notify(L10n.T("被控已开启 · 端口 ") + port + L10n.T(" · 设备码 ") + DeviceCode);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = L10n.T("启动被控失败：") + ex.Message;
                Notify(_lastError);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            _caps = Protocol.LegacyCaps;   // 每个新会话从 v1 能力起步，不继承上一位查看端
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            _client = null;
            _stream = null;
            _chan = null;
            try { if (_acceptThread != null) _acceptThread.Join(400); } catch { }
            try { if (_readThread != null) _readThread.Join(400); } catch { }
            try { if (_sendThread != null) _sendThread.Join(400); } catch { }
            _acceptThread = _readThread = _sendThread = null;
            try { if (_cts != null) _cts.Dispose(); } catch { }
            _cts = null;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            try { if (Grabber != null) Grabber.Stop(); } catch { }
            Grabber = null;
            _frameSignal.Set();
        }

        /* ===================== 连接与握手 ===================== */

        private void AcceptLoop()
        {
            while (_running && _listener != null)
            {
                try
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    if (_client != null && _client.Connected)
                    {
                        Notify(L10n.T("已有查看端连接，拒绝新的连接"));
                        try { c.Close(); } catch { }
                        continue;
                    }
                    try
                    {
                        c.NoDelay = true;
                        c.SendBufferSize = 512 * 1024;
                        c.ReceiveBufferSize = 256 * 1024;
                    }
                    catch { }
                    _client = c;
                    _readThread = new Thread(delegate () { Serve(c); })
                    { IsBackground = true, Name = "rd-viewer" };
                    _readThread.Start();
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_running) Notify(L10n.T("接受连接异常：") + ex.Message);
                    try { Thread.Sleep(200); } catch { }
                }
            }
        }

        private void Serve(TcpClient client)
        {
            NetworkStream ns = null;
            try
            {
                ns = client.GetStream();
                _stream = ns;

                // ---- 第一步：加密握手（RdHello → RdHelloAck → 密文凭据 → 结果）----
                var first = Protocol.ReadMessageAsync(ns, _cts.Token, 15000).GetAwaiter().GetResult();
                if (first == null) { Notify(L10n.T("连接后未发送数据，断开")); return; }
                if (first.Value.type != Protocol.MessageType.RdHello)
                {
                    // 不做明文回退：宁可连不上，也不接受可被降级的会话
                    Notify(L10n.T("查看端未发起加密握手，已拒绝（请升级到同版本客户端）"));
                    return;
                }
                string reject = Handshake(ns, first.Value.payload, client);
                if (reject != null)
                {
                    Notify(L10n.T("拒绝连接：") + reject);
                    return;
                }

                Notify(L10n.T("查看端已连接 · 配对校验码 ") + _chanSas);
                // v2 能力协商：被控端**不主动发** Capability，等查看端发来再回 CapabilityAck；
                // v1 查看端不认识 type=43/44 会走 switch 的 default 忽略，会话照常按 LegacyCaps 走。
                // 旧字段 SupportedEncoding 按协商结果如实声明（v1 时逐字节不变，见 EncodingAbilityByte）。
                SendSealed(Protocol.MessageType.SupportedEncoding,
                    new byte[] { EncodingAbilityByte(), 8 });
                SendPermissions();
                RequestFirstFrame();     // 真正补一帧：重发缓存帧，不再是空 Set

                // ---- 第二步：读输入直到断开 ----
                ReadInputLoop(ns);
            }
            catch (Exception ex)
            {
                if (_running) Notify(L10n.T("会话异常：") + ex.Message);
            }
            finally
            {
                var c = _client;
                _client = null;
                _stream = null;
                _chan = null;
                _caps = Protocol.LegacyCaps;   // 断线重置：下一位查看端重新走能力协商
                try { if (ns != null) ns.Close(); } catch { }
                try { if (client != null) client.Close(); } catch { }
                Notify(L10n.T("查看端已断开"));
                // 断开后保持监听，等下一位（用户不用重新授权屏幕采集）
            }
        }

        private string _chanSas;

        /// <summary>写锁：发送线程与输入线程都会往同一条流写，必须串行，否则两个报文的字节会交错。</summary>
        private readonly object _writeGate = new object();

        /// <summary>连续发送失败次数：用于压掉断线后的日志洪水。</summary>
        private int _sendFailStreak;

        private string Handshake(NetworkStream ns, byte[] hello, TcpClient client)
        {
            try
            {
                if (hello.Length < RdWire.HelloBodyLength || hello[0] != RdWire.Version)
                    return L10n.T("握手版本不符");
                byte[] clientPub = new byte[32];
                Buffer.BlockCopy(hello, 1, clientPub, 0, 32);

                RdKeypair kp = RdKeypair.NewEphemeral();
                Protocol.SendAsync(ns, Protocol.MessageType.RdHelloAck, kp.PublicKey, _cts.Token).GetAwaiter().GetResult();

                RdSessionKeys keys = RdSessionKeys.Derive(kp.SharedSecret(clientPub), clientPub, kp.PublicKey, true);
                RdSecureChannel chan = new RdSecureChannel(keys);
                _chanSas = keys.Sas();

                var auth = Protocol.ReadMessageAsync(ns, _cts.Token, 15000).GetAwaiter().GetResult();
                if (auth == null || auth.Value.type != Protocol.MessageType.SecurePayload)
                    return L10n.T("未收到加密认证帧");
                byte[] inner;
                if (!chan.TryOpen(auth.Value.payload, out inner))
                    return L10n.T("认证帧校验失败（可能被中间人篡改）");

                string text = Encoding.UTF8.GetString(inner);
                // 局域网直连：密文里只有固定标记。仍然做同网段校验 + 加密握手，
                // 所以既满足"点一下就连"，又不会把端口暴露给公网/其它网段。
                if (text.StartsWith(RdWire.LanDirectToken + "\n", StringComparison.Ordinal))
                {
                    if (!AllowLanDirect) return L10n.T("被控端已关闭局域网免密直连");
                    IPEndPoint localEp = client?.Client?.LocalEndPoint as IPEndPoint;
                    IPEndPoint remoteEp = client?.Client?.RemoteEndPoint as IPEndPoint;
                    if (!RdWire.IsSameLanSubnet(localEp?.Address, remoteEp?.Address))
                        return L10n.T("局域网直连只允许同一网段的设备");
                    Log.Info(Tag, "局域网免密直连已放行：" + remoteEp?.Address);
                }
                else
                {
                    int nl = text.IndexOf('\n');
                    string code = RdDeviceCode.Normalize(nl >= 0 ? text.Substring(0, nl) : text);
                    string pwd = nl >= 0 ? text.Substring(nl + 1) : "";

                    if (!string.IsNullOrEmpty(code) && code != DeviceCode)
                        return L10n.T("设备码不正确");
                    string expect = Password;
                    // 空密码 = 用户主动关掉密码校验（和被控端 Windows 版行为一致）
                    if (!string.IsNullOrEmpty(expect) && pwd != expect)
                        return L10n.T("连接密码不正确");
                }

                Protocol.SendAsync(ns, Protocol.MessageType.SecurePayload,
                    chan.Seal(new byte[] { 1 }), _cts.Token).GetAwaiter().GetResult();
                _chan = chan;
                return null;
            }
            catch (Exception ex)
            {
                return L10n.T("握手异常：") + ex.Message;
            }
        }

        private void SendPermissions()
        {
            // 只声明本机真能做的能力：手机被控没有文件传输/剪贴板/声音回传，
            // 谎报会让查看端的按钮点了没反应，比不放这些按钮更糟。
            SendSealed(Protocol.MessageType.PermissionInfo,
                new byte[] { (byte)Protocol.Permission.Keyboard, 1 });
            SendSealed(Protocol.MessageType.PermissionInfo,
                new byte[] { (byte)Protocol.Permission.Clipboard, 0 });
            // 文件接收已实现（见 HandleFileControl），这里可以如实上报允许
            SendSealed(Protocol.MessageType.PermissionInfo,
                new byte[] { (byte)Protocol.Permission.File, 1 });
            SendSealed(Protocol.MessageType.PermissionInfo,
                new byte[] { (byte)Protocol.Permission.Audio, 0 });
        }

        /* ===================== 画面 ===================== */

        private void OnFrame(byte[] data, int w, int h, byte codec)
        {
            if (!HasViewer || data == null || data.Length == 0) return;
            // 帧格式与 desk 完全同源：[flags][codec][w][h][len][data]。
            // v1（_caps=LegacyCaps）：严格保持 14B 帧头 + flags=0 + codec=JPEG，
            // 与改动前逐字节一致。只有协商出 Abr 能力，才在 14B 帧头后追加 12B QoS 头
            // [frameId:4][sendTs:8] 并置 FLAG_QOS（len 仍是图像数据长度，不含 QoS 头）。
            bool qos = (_caps & Protocol.Capability.Abr) != 0;
            int headerLen = 14 + (qos ? Protocol.QosFrameHeaderLength : 0);
            byte[] payload = new byte[headerLen + data.Length];
            payload[0] = qos ? Protocol.FLAG_QOS : (byte)0;   // 整帧（无区域差分）
            payload[1] = codec;                                // CODEC_JPEG 或 CODEC_H264
            Protocol.WriteInt32(payload, 2, w);
            Protocol.WriteInt32(payload, 6, h);
            Protocol.WriteInt32(payload, 10, data.Length);
            if (qos)
            {
                int fid = Interlocked.Increment(ref _qosFrameId);
                Protocol.WriteInt32(payload, 14, fid);                  // frameId
                Protocol.WriteInt64(payload, 18, UtcNowMs());           // sendTs（UTC ms）
            }
            Buffer.BlockCopy(data, 0, payload, headerLen, data.Length);
            // latest-value 合并：无条件覆盖为最新帧；同时标记下游「有在制品」，
            // 采集线程据此在发送完成前忙时丢帧（背压）。
            Interlocked.Exchange(ref _pendingFrame, payload);
            Interlocked.Exchange(ref _consumerBusy, 1);
            _frameSignal.Set();
        }

        /// <summary>
        /// 真正补一帧：优先重发采集器缓存的最近一帧（ScreenStream「缓存帧立刻可交付」），再 Set 信号。
        /// 【为什么 · 补首帧空操作】旧代码只 _frameSignal.Set()，但 _pendingFrame 为 null 时
        /// SendLoop 醒来取到 null 直接 continue，等于完全没补帧 —— 查看端连上后要等屏幕
        /// 动一下才有画面。这里让采集器重发缓存帧，必然产出 _pendingFrame。
        /// </summary>
        private void RequestFirstFrame()
        {
            var g = Grabber;
            if (g != null) g.Republish();
            _frameSignal.Set();
        }

        /// <summary>
        /// 单写线程 + "最新帧覆盖"信箱：网络跟不上时丢旧帧而不是排队。
        /// 远控要的是最新画面，积压只会让操作延迟越来越大（与 Windows 被控端同一策略）。
        /// </summary>
        private void SendLoop()
        {
            while (_running)
            {
                try { _frameSignal.WaitOne(300); }
                catch { break; }
                byte[] f = Interlocked.Exchange(ref _pendingFrame, null);
                if (f == null) { Interlocked.Exchange(ref _consumerBusy, 0); continue; }
                // 【背压】进入「单一在制品」：这一帧发完（成功/失败/无查看端）都清零 busy，
                // 采集线程才允许压缩下一帧。try/finally 保证任何 continue 都不会把 busy
                // 永久留在 1（那样采集线程会一直丢帧、彻底黑屏）。
                try
                {
                    if (!HasViewer) continue;
                    if (!TrySendSealed(Protocol.MessageType.VideoFrame, f)) continue;

                    _framesSent++;
                    long now = System.Environment.TickCount;
                    if (_lastStatAt == 0) _lastStatAt = now;
                    if (now - _lastStatAt >= 5000)
                    {
                        Notify(L10n.T("已推送 ") + _framesSent + L10n.T(" 帧"));
                        _framesSent = 0;
                        _lastStatAt = now;
                    }
                }
                finally { Interlocked.Exchange(ref _consumerBusy, 0); }
            }
        }

        /* ===================== 输入 ===================== */

        private void ReadInputLoop(NetworkStream ns)
        {
            while (_running && _chan != null)
            {
                var raw = Protocol.ReadMessageAsync(ns, _cts.Token, 0).GetAwaiter().GetResult();
                if (raw == null) return;
                var msg = RdFrame.Unwrap(_chan, raw.Value.type, raw.Value.payload);
                if (msg == null)
                {
                    Notify(L10n.T("密文帧校验失败（篡改或降级），断开"));
                    return;
                }
                try { DispatchInput(msg.Value.type, msg.Value.payload); }
                catch (Exception ex) { Notify(L10n.T("处理输入失败：") + ex.Message); }
            }
        }

        private void DispatchInput(Protocol.MessageType type, byte[] p)
        {
            var input = RdInputService.Instance;
            switch (type)
            {
                case Protocol.MessageType.MouseEvent:
                    if (p.Length >= 8 && input != null)
                    {
                        // 【坐标基准 · 本轮只注释不改协议】
                        // 查看端（RdSession.WriteMouse / RdInputService）把触点换算成
                        // 0..65535 归一化坐标；本端按物理屏幕尺寸注入，而帧头里的 w/h 是
                        // **采集缩放后的尺寸**，两者基准并不一致。v2 的 NormalizedCoord /
                        // MouseEventEx 能力已声明（按帧宽高映射），但本轮不接扩展输入报文，
                        // 仍走 v1 的 8 字节 MouseEvent，保持逐字节兼容。
                        int nx = p[1] | (p[2] << 8);
                        int ny = p[3] | (p[4] << 8);
                        int button = p[5];
                        short wheel = (short)(p[6] | (p[7] << 8));
                        input.OnMouse(p[0], nx, ny, button, wheel);
                    }
                    break;

                case Protocol.MessageType.KeyEvent:
                    if (p.Length >= 5 && input != null)
                    {
                        int vk = Protocol.ReadInt32(p, 0);
                        input.OnKey(vk, p[4] != 0);
                    }
                    break;

                case Protocol.MessageType.ControlAction:
                    if (p.Length >= 1 && input != null) HandleAction((Protocol.ControlActionId)p[0], input);
                    break;

                case Protocol.MessageType.ChatMessage:
                    {
                        // 查看端的"聊天"输入框在手机被控时当作"整段文字输入"：
                        // 中文/大写/符号都靠它，VK 码本身带不出字符
                        if (p.Length > 0 && input != null)
                        {
                            int off = 0;
                            string text = Protocol.ReadString(p, ref off);
                            input.CommitText(text);
                        }
                        break;
                    }

                // ===== v2 能力协商 / QoS / 心跳 =====
                case Protocol.MessageType.Capability:
                    HandleCapability(p);
                    break;

                case Protocol.MessageType.TestDelay:
                    HandleTestDelay(p);
                    break;

                case Protocol.MessageType.QualitySettings:
                    HandleQualitySettings(p);
                    break;

                case Protocol.MessageType.ForceKeyFrame:
                    // 每帧 JPEG 都是关键帧：重发缓存的最近一帧即可（真正触发产帧）
                    RequestFirstFrame();
                    break;

                case Protocol.MessageType.Ping:
                    HandlePing(p);
                    break;

                case Protocol.MessageType.Disconnect:
                    Notify(L10n.T("查看端主动断开"));
                    break;

                case Protocol.MessageType.PermissionInfo:
                    SendPermissions();
                    break;

                case Protocol.MessageType.FileControl:
                    HandleFileControl(p);
                    break;

                case Protocol.MessageType.FileData:
                    HandleFileData(p);
                    break;

                case Protocol.MessageType.ClipboardText:
                    HandleClipboardText(p);
                    break;

                case Protocol.MessageType.ClipboardData:
                    HandleClipboardData(p);
                    break;

                default:
                    // 未知 / 本轮未启用的 type（含 MouseEventEx / KeyEventEx 扩展输入）一律忽略，
                    // 绝不断连 —— v1 端发来的任何新报文都不该把整条会话拆掉。
                    break;
            }
        }

        /// <summary>
        /// 查看端发来的能力声明（v2）。协议摘要不一致 / 负载无法解析时降级为 LegacyCaps
        /// 并回 CapabilityAck(LegacyCaps)，但**不断开连接**；否则 Negotiate 取交集存 _caps，
        /// 回 CapabilityAck(EncodeCapability(选中集, maxFps))。
        /// </summary>
        private void HandleCapability(byte[] p)
        {
            byte verMin, verCur;
            Protocol.Capability peer;
            uint hash;
            ushort maxFps;
            if (!Protocol.TryDecodeCapability(p, out verMin, out verCur, out peer, out hash, out maxFps))
            {
                Notify(L10n.T("查看端能力负载无法解析，按 v1 能力集降级（不断连）"));
                _caps = Protocol.LegacyCaps;
                SendSealed(Protocol.MessageType.CapabilityAck,
                    Protocol.EncodeCapability(Protocol.LegacyCaps, 0));
                return;
            }
            if (hash != Protocol.ProtocolHash)
            {
                Notify(L10n.T("查看端协议摘要不一致（0x") + hash.ToString("X8")
                    + L10n.T("），已按 v1 能力集降级，请升级两端到同一版本（不断连）"));
                _caps = Protocol.LegacyCaps;
                SendSealed(Protocol.MessageType.CapabilityAck,
                    Protocol.EncodeCapability(Protocol.LegacyCaps, 0));
                return;
            }
            Protocol.Capability agreed = Protocol.Negotiate(HostCaps, peer);
            _caps = agreed;
            // 30fps 硬编码改读集中设置（FpsCap 仍夹到 1..30 与采集器兼容）。
            ushort fps = (ushort)Math.Min(FpsCap(), maxFps == 0 ? FpsCap() : maxFps);
            SendSealed(Protocol.MessageType.CapabilityAck,
                Protocol.EncodeCapability(agreed, fps));
            Notify(L10n.T("能力协商完成 caps=0x") + ((uint)agreed).ToString("X") + " maxFps=" + fps);
        }

        /// <summary>
        /// 画质设置：用 QualityConfig.Decode（兼容 9B v1 / 13B v2）解析出 Mode，按 Mode 分支，
        /// 不再一律按整帧 JPEG + 15fps 解释（消除"请求 H264/30fps 却静默拿到 12~15fps JPEG"）。
        /// Fps 尊重查看端请求、按设备性能封顶 30；MaxWidth 仍换算短边；BitrateKbps 作码率参考。
        /// </summary>
        private void HandleQualitySettings(byte[] p)
        {
            if (p.Length < 9 || Grabber == null) return;
            Protocol.QualityConfig q = Protocol.QualityConfig.Decode(p);
            int shortSide = (int)Math.Round(Math.Max(360, Math.Min(1080, q.MaxWidth * 0.5625)));
            // fps 封顶、质量范围改读集中设置（10..100，参考 ScreenStream 默认 80）。
            int fps = Math.Max(1, Math.Min(FpsCap(), q.Fps));
            int quality = Settings.ClampQuality(q.Quality);
            int bitrate = q.BitrateKbps;

            Protocol.EncodingMode mode = q.Mode;
            switch (mode)
            {
                case Protocol.EncodingMode.H264:
                    // 只有协商出 H264Encode 才允许走 H264；编码器起不来时由下方统一检查降级
                    if ((_caps & Protocol.Capability.H264Encode) == 0)
                    {
                        mode = Protocol.EncodingMode.JpegFullFrame;
                        Notify(L10n.T("请求 H.264，但能力位未协商成功，已降级为整帧 JPEG"));
                    }
                    break;

                case Protocol.EncodingMode.JpegRegions:
                case Protocol.EncodingMode.JpegFullFrame:
                case Protocol.EncodingMode.RawRegions:
                    // 按 Mode 设置采集器编码模式（当前采集器只落地整帧 JPEG，
                    // Region/Raw 记录 Mode 供后续差分扩展，见 RdScreenGrabber 注释）
                    break;

                default:
                    mode = Protocol.EncodingMode.JpegFullFrame;
                    break;
            }

            Grabber.SetMode(mode);
            // 编码器真正起不来的机型：明确降级为整帧 JPEG，并让查看端按实际模式显示
            if (mode == Protocol.EncodingMode.H264 && Grabber.Mode != Protocol.EncodingMode.H264)
            {
                mode = Protocol.EncodingMode.JpegFullFrame;
                Notify(L10n.T("请求 H.264，但本机编码器不可用，已明确降级为整帧 JPEG"));
            }
            Grabber.SetMaxFps(fps);
            Grabber.SetQuality(quality);
            Grabber.SetMaxShortSide(shortSide);
            _abrQuality = quality;
            _abrFps = fps;
            if (bitrate > 0) _targetBitrateKbps = bitrate;
            Notify(L10n.T("画质调整为 ") + shortSide + L10n.T("p / 质量 ") + quality + "% / " + fps
                + L10n.T("fps / 模式 ") + mode + (bitrate > 0 ? L10n.T(" / 码率 ") + bitrate + "kbps" : ""));
        }

        /// <summary>
        /// TestDelay：v2 的 32B QoS 反馈用 TryDecodeQos 解析，按 rttMs 做 ABR 升降档
        /// （大于 300ms 立即降一档，先降码率/质量再降 fps；小于 150ms 每 3s 升一档）。
        /// 解析失败（v1 的 17B 老格式）或没协商出 Abr 时保持原样回显，兼容 v1。
        /// </summary>
        private void HandleTestDelay(byte[] p)
        {
            int frameId;
            long sendTs, recvTs;
            int rttMs, bitrateKbps;
            bool isQos = Protocol.TryDecodeQos(p, out frameId, out sendTs,
                out recvTs, out rttMs, out bitrateKbps);

            if (isQos && (_caps & Protocol.Capability.Abr) != 0)
            {
                long now = UtcNowMs();
                if (rttMs > 300)
                {
                    // 先降码率（对应 JPEG 质量），质量到底再降 fps
                    int qFloor = ScreenShareSettings.MinJpegQuality;
                    if (_abrQuality > qFloor + 5) _abrQuality = Math.Max(qFloor, _abrQuality - 5);
                    else _abrFps = Math.Max(1, _abrFps - 2);
                    if (_targetBitrateKbps > 0)
                        _targetBitrateKbps = Math.Max(0, _targetBitrateKbps - 500);
                    if (Grabber != null)
                    {
                        Grabber.SetQuality(_abrQuality);
                        Grabber.SetMaxFps(_abrFps);
                    }
                    Notify(L10n.T("QoS 拥塞 rtt=") + rttMs + L10n.T("ms，降档 → 质量 ") + _abrQuality
                        + "% / " + _abrFps + L10n.T("fps / 码率 ") + _targetBitrateKbps + "kbps");
                }
                else if (rttMs >= 0 && rttMs < 150 && now - _lastAbrRaiseAt >= 3000)
                {
                    _lastAbrRaiseAt = now;
                    // 升档：先把 fps 补回去，再提质量
                    if (_abrFps < FpsCap()) _abrFps = Math.Min(FpsCap(), _abrFps + 2);
                    else _abrQuality = Math.Min(ScreenShareSettings.MaxJpegQuality, _abrQuality + 5);
                    if (Grabber != null)
                    {
                        Grabber.SetQuality(_abrQuality);
                        Grabber.SetMaxFps(_abrFps);
                    }
                    Notify(L10n.T("QoS 良好 rtt=") + rttMs + L10n.T("ms，升档 → 质量 ") + _abrQuality
                        + "% / " + _abrFps + "fps");
                }
            }
            // 一律原样回显：v1 查看端靠 TestDelay 回显测延迟，不能因为解析成功就不回。
            TrySendSealed(Protocol.MessageType.TestDelay, p ?? new byte[0]);
        }

        /// <summary>
        /// Ping：v2 Heartbeat 下 8 字节 UTC 毫秒时间戳 → 回 Pong = [原 8B][本端 UTC ms 8B]；
        /// 其它长度（如 Windows 查看端的 9B Ping: 8B tick + 1B seq）或 v1（LegacyCaps）
        /// 一律原样回显，与改动前逐字节一致。
        /// </summary>
        private void HandlePing(byte[] p)
        {
            if (p != null && p.Length == 8 && (_caps & Protocol.Capability.Heartbeat) != 0)
            {
                byte[] r = new byte[16];
                Buffer.BlockCopy(p, 0, r, 0, 8);
                Protocol.WriteInt64(r, 8, UtcNowMs());
                TrySendSealed(Protocol.MessageType.Pong, r);
            }
            else
            {
                TrySendSealed(Protocol.MessageType.Pong, p ?? new byte[0]);
            }
        }

        /* ===================== 文件接收（电脑 → 手机） ===================== */

        // 报文格式与 desk 的 FileTransferManager 完全一致：
        //   FileControl: [cmd:1][id:4] ...(offer: name:str size:8 relPath:str / accept: offset:8)
        //   FileData:    [id:4][len:4][data]
        private readonly Dictionary<int, FileStream> _inFiles = new Dictionary<int, FileStream>();
        private readonly Dictionary<int, string> _inPaths = new Dictionary<int, string>();
        private readonly Dictionary<int, long> _inSizes = new Dictionary<int, long>();

        private static void ToastIt(string s)
        {
            try
            {
                var h = new Handler(Looper.MainLooper);
                h.Post(delegate { try { L10n.Toast(Application.Context, s, ToastLength.Long).Show(); } catch { } });
            }
            catch { }
        }

        /// <summary>
        /// 选保存位置：优先公共"下载/Mchat"，不可写时退回应用专属外部目录。
        /// Android 10+ 的分区存储下，公共下载目录在未授予 MANAGE_EXTERNAL_STORAGE 时会写失败，
        /// 所以必须留退路，不能直接抛。
        /// </summary>
        private static string ResolveSavePath(string relPath, string name)
        {
            string sub = string.IsNullOrEmpty(relPath) ? name : relPath;
            sub = sub.Replace('\\', '/').TrimStart('/');
            int clip = sub.IndexOf("__clipboard__/", StringComparison.Ordinal);
            if (clip == 0) sub = sub.Substring("__clipboard__/".Length);
            // 防目录穿越
            if (sub.Contains("..")) sub = name;
            if (string.IsNullOrEmpty(sub)) sub = "received.bin";

            string root = null;
            try
            {
                var dir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads);
                root = Path.Combine(dir.AbsolutePath, "Mchat");
                Directory.CreateDirectory(root);
                string probe = Path.Combine(root, ".wtest");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
            }
            catch
            {
                root = null;
            }
            if (root == null)
            {
                try
                {
                    var ext = Application.Context.GetExternalFilesDir(null);
                    root = Path.Combine(ext != null ? ext.AbsolutePath : Application.Context.FilesDir.AbsolutePath, "Mchat");
                    Directory.CreateDirectory(root);
                }
                catch
                {
                    root = Path.Combine(Application.Context.FilesDir.AbsolutePath, "Mchat");
                    try { Directory.CreateDirectory(root); } catch { }
                }
            }

            string full = Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar));
            try { Directory.CreateDirectory(Path.GetDirectoryName(full)); } catch { }
            return full;
        }

        private void HandleFileControl(byte[] p)
        {
            if (p.Length < 5) return;
            int cmd = p[0];
            int off = 1;
            int id = Protocol.ReadInt32(p, off);
            off += 4;

            switch (cmd)
            {
                case 1:   // 对端要发文件过来
                    {
                        string name = Protocol.ReadString(p, ref off);
                        long hi = Protocol.ReadInt32(p, off);
                        long lo = (uint)Protocol.ReadInt32(p, off + 4);
                        off += 8;
                        long size = (hi << 32) | lo;
                        string rel = "";
                        if (off + 4 <= p.Length)
                        {
                            try { rel = Protocol.ReadString(p, ref off); } catch { rel = ""; }
                        }

                        string save = ResolveSavePath(rel, name);
                        try
                        {
                            var fs = File.Create(save);
                            lock (_lock)
                            {
                                _inFiles[id] = fs;
                                _inPaths[id] = save;
                                _inSizes[id] = size;
                            }
                            // 接受并从 0 字节开始（手机侧不做续传，重发比续传省事）
                            var ms = new MemoryStream(13);
                            ms.WriteByte(2);
                            Protocol.WriteInt32(ms, id);
                            Protocol.WriteInt32(ms, 0);
                            Protocol.WriteInt32(ms, 0);
                            SendSealed(Protocol.MessageType.FileControl, ms.ToArray());
                            Notify(L10n.T("正在接收 ") + Path.GetFileName(save) + "（" + Math.Max(0, size / 1024) + " KB）");
                        }
                        catch (Exception ex)
                        {
                            Notify(L10n.T("无法创建文件：") + ex.Message);
                            var ms = new MemoryStream(5);
                            ms.WriteByte(3);
                            Protocol.WriteInt32(ms, id);
                            SendSealed(Protocol.MessageType.FileControl, ms.ToArray());
                        }
                        break;
                    }

                case 4:   // 发送完成
                    {
                        FileStream fs = null;
                        string path = null;
                        lock (_lock)
                        {
                            if (_inFiles.ContainsKey(id)) { fs = _inFiles[id]; }
                            if (_inPaths.ContainsKey(id)) { path = _inPaths[id]; }
                            _inFiles.Remove(id);
                            _inPaths.Remove(id);
                            _inSizes.Remove(id);
                        }
                        try { if (fs != null) fs.Dispose(); } catch { }
                        if (path != null)
                        {
                            Notify(L10n.T("已接收文件：") + path);
                            ToastIt(L10n.T("文件已保存到 ") + path);
                        }
                        break;
                    }

                case 2:   // 对方接受（我们主动发出去的文件）
                    {
                        Notify(L10n.T("对方已接受，开始传输 id=") + id);
                        long resume = ((long)Protocol.ReadInt32(p, off) << 32) | (uint)Protocol.ReadInt32(p, off + 4);
                        if (resume > 0) Notify(L10n.T("对方请求从 ") + resume + L10n.T(" 字节续传（手机侧按整份重发）"));
                        System.Threading.ManualResetEventSlim ev = null;
                        lock (_lock)
                        {
                            if (_outAccepted.ContainsKey(id)) _outAccepted[id] = true;
                            if (_outWait.ContainsKey(id)) ev = _outWait[id];
                        }
                        if (ev != null) { try { ev.Set(); } catch { } }
                        break;
                    }

                case 3:   // 对方拒绝
                    {
                        System.Threading.ManualResetEventSlim ev = null;
                        lock (_lock)
                        {
                            if (_outAccepted.ContainsKey(id)) _outAccepted[id] = false;
                            if (_outWait.ContainsKey(id)) ev = _outWait[id];
                        }
                        if (ev != null) { try { ev.Set(); } catch { } }
                        break;
                    }

                case 5:   // 对端取消
                    {
                        FileStream fs = null;
                        string path = null;
                        lock (_lock)
                        {
                            if (_inFiles.ContainsKey(id)) { fs = _inFiles[id]; }
                            if (_inPaths.ContainsKey(id)) { path = _inPaths[id]; }
                            _inFiles.Remove(id);
                            _inPaths.Remove(id);
                            _inSizes.Remove(id);
                        }
                        try { if (fs != null) fs.Dispose(); } catch { }
                        try { if (path != null && File.Exists(path)) TrashManager.Instance.MoveToTrash(path); } catch { }
                        Notify(L10n.T("对方取消了文件发送"));
                        break;
                    }
            }
        }

        /* ---------- 手机 → 电脑：发送文件 ---------- */

        private int _outSeq = 1000;
        private readonly Dictionary<int, System.Threading.ManualResetEventSlim> _outWait
            = new Dictionary<int, System.Threading.ManualResetEventSlim>();
        private readonly Dictionary<int, bool> _outAccepted = new Dictionary<int, bool>();

        /// <summary>
        /// 把手机上的一个文件推给当前控制端（电脑）。
        /// 先发 offer 等对方确认（对方会弹"是否接收"），再按 64KB 分块推，最后发 done。
        /// 整个过程在后台线程，不阻塞界面。
        /// </summary>
        /* ===================== 剪贴板（电脑 ⇄ 手机） ===================== */

        /// <summary>最近一次同步过的剪贴板文本：两个方向都用它去重，避免"我设了→你又发回来"的死循环。</summary>
        private string _lastClipText = "";

        private static void RunOnMain(Action act)
        {
            try
            {
                var h = new Handler(Looper.MainLooper);
                h.Post(delegate { try { act(); } catch { } });
            }
            catch { }
        }

        /// <summary>手机 → 电脑：把本机剪贴板文本推给控制端（文本相同则跳过）。</summary>
        public void SendClipboardText(string text)
        {
            if (!HasViewer || string.IsNullOrEmpty(text)) return;
            if (text == _lastClipText) return;
            _lastClipText = text;
            try
            {
                using (var ms = new MemoryStream())
                {
                    ms.WriteByte(1);                    // 与 desk 的 SendClipboardAsync 一致
                    Protocol.WriteString(ms, text);
                    if (TrySendSealed(Protocol.MessageType.ClipboardText, ms.ToArray()))
                        Notify(L10n.T("剪贴板已同步到电脑（") + text.Length + L10n.T(" 字）"));
                }
            }
            catch (Exception ex) { Notify(L10n.T("同步剪贴板失败：") + ex.Message); }
        }

        /// <summary>电脑 → 手机：写入系统剪贴板。Android 10+ 只限制"读"，写入任何线程都可以。</summary>
        private void HandleClipboardText(byte[] p)
        {
            if (p == null || p.Length < 2) return;
            string text;
            try
            {
                int off = 1;
                text = Protocol.ReadString(p, ref off);
            }
            catch { return; }
            if (string.IsNullOrEmpty(text)) return;
            _lastClipText = text;
            RunOnMain(delegate
            {
                try
                {
                    var cm = (Android.Content.ClipboardManager)Application.Context
                        .GetSystemService(Android.Content.Context.ClipboardService);
                    if (cm != null) cm.PrimaryClip = Android.Content.ClipData.NewPlainText("Mchat", text);
                }
                catch { }
            });
            Notify(L10n.T("电脑剪贴板已同步到手机（") + text.Length + L10n.T(" 字）"));
        }

        /// <summary>
        /// 电脑 → 手机：剪贴板里的图片。
        /// 手机侧不往剪贴板塞图片（跨应用授权的 content:// 很容易被拒），
        /// 而是直接落盘 + 提示路径 —— 用户点开就能看，比"粘贴失败"有用得多。
        /// </summary>
        private void HandleClipboardData(byte[] p)
        {
            if (p == null || p.Length < 5) return;
            int fmt = p[0];
            int len = Protocol.ReadInt32(p, 1);
            if (len <= 0 || 5 + len > p.Length) return;
            byte[] data = new byte[len];
            Buffer.BlockCopy(p, 5, data, 0, len);

            string ext = fmt == 2 ? ".png" : ".bin";     // ClipFormat.Png = 2
            string name = "clip_" + DateTime.Now.ToString("MMdd_HHmmss") + ext;
            string save = ResolveSavePath(name, name);
            try
            {
                File.WriteAllBytes(save, data);
                Notify(L10n.T("已保存电脑剪贴板图片：") + save);
                ToastIt(L10n.T("图片已保存：") + save);
            }
            catch (Exception ex) { Notify(L10n.T("保存剪贴板图片失败：") + ex.Message); }
        }

        /// <summary>
        /// 从 content:// 直接发文件。
        ///
        /// 【为什么要先落地成缓存文件】DocumentsUI 给的输入流背后是 ParcelFileDescriptor
        /// 或管道，跨线程边读边发时容易卡住（实测表现为：对方已接受、offer 也到了，
        /// 但文件始终 0 字节，发送线程挂在 Read 上再无下文）。
        /// 先在本线程把它整份拷进缓存文件，再从普通 FileStream 发送，
        /// 无论文件多大都只有"读本地文件"这一种行为，最稳。
        /// </summary>
        public bool SendUriToViewer(Android.Content.ContentResolver cr, Android.Net.Uri uri, string name)
        {
            if (!HasViewer || cr == null || uri == null) return false;
            if (string.IsNullOrEmpty(name)) name = "file.bin";
            Notify(L10n.T("准备发送：") + name);
            new Thread(delegate ()
            {
                string tmp = null;
                try
                {
                    tmp = Path.Combine(Application.Context.CacheDir.AbsolutePath,
                        "rdout_" + Guid.NewGuid().ToString("N") + ".bin");
                    long total = 0;
                    using (var src = cr.OpenInputStream(uri))
                    {
                        if (src == null) { Notify(L10n.T("无法读取该文件（provider 返回空流）")); return; }
                        using (var dst = System.IO.File.Create(tmp))
                        {
                            byte[] buf = new byte[65536];
                            int n;
                            while ((n = src.Read(buf, 0, buf.Length)) > 0)
                            {
                                dst.Write(buf, 0, n);
                                total += n;
                            }
                        }
                    }
                    if (total <= 0) { Notify(L10n.T("文件内容为空，已取消")); return; }
                    Notify(L10n.T("已读取 ") + total + L10n.T(" 字节，开始发送"));
                    SendFileToViewer(System.IO.File.OpenRead(tmp), name, total);
                }
                catch (Exception ex)
                {
                    Notify(L10n.T("准备文件失败：") + ex.Message);
                }
            })
            { IsBackground = true, Name = "rd-prepfile" }.Start();
            return true;
        }

        public bool SendFileToViewer(Stream src, string name, long size)
        {
            if (!HasViewer || src == null) return false;
            if (string.IsNullOrEmpty(name)) name = "file.bin";
            int id = System.Threading.Interlocked.Increment(ref _outSeq);
            var ev = new System.Threading.ManualResetEventSlim(false);
            lock (_lock) { _outWait[id] = ev; _outAccepted[id] = false; }

            var ms = new MemoryStream();
            ms.WriteByte(1);
            Protocol.WriteInt32(ms, id);
            Protocol.WriteString(ms, name);
            Protocol.WriteInt32(ms, (int)(size >> 32));
            Protocol.WriteInt32(ms, (int)(size & 0xFFFFFFFFu));
            Protocol.WriteString(ms, "");
            Notify(L10n.T("发送 offer id=") + id + " name=" + name + " size=" + size);
            if (!TrySendSealed(Protocol.MessageType.FileControl, ms.ToArray()))
            {
                lock (_lock) { _outWait.Remove(id); _outAccepted.Remove(id); }
                return false;
            }
            Notify(L10n.T("已请求发送 ") + name + "（" + Math.Max(0, size / 1024) + L10n.T(" KB），等待对方确认…"));

            new Thread(delegate ()
            {
                bool ok = false;
                try
                {
                    ok = ev.Wait(60000);
                    lock (_lock) { if (_outAccepted.ContainsKey(id)) ok = ok && _outAccepted[id]; }
                }
                catch { }
                if (!ok)
                {
                    Notify(L10n.T("对方未确认或已拒绝，已取消发送"));
                    lock (_lock) { _outWait.Remove(id); _outAccepted.Remove(id); }
                    try { src.Dispose(); } catch { }
                    return;
                }
                long sent = 0;
                try
                {
                    byte[] buf = new byte[65536];
                    while (sent < size)
                    {
                        int want = (int)Math.Min(buf.Length, size - sent);
                        int n = src.Read(buf, 0, want);
                        if (n <= 0) break;
                        var d = new MemoryStream(n + 8);
                        Protocol.WriteInt32(d, id);
                        Protocol.WriteInt32(d, n);
                        d.Write(buf, 0, n);
                        if (!TrySendSealed(Protocol.MessageType.FileData, d.ToArray()))
                        {
                            Notify(L10n.T("发送中断：连接已断开（已发 ") + sent + L10n.T(" 字节）"));
                            break;
                        }
                        sent += n;
                        if (sent == n || sent >= size) Notify(L10n.T("已发送 ") + sent + " / " + size + L10n.T(" 字节"));
                    }
                    var done = new MemoryStream(5);
                    done.WriteByte(4);
                    Protocol.WriteInt32(done, id);
                    SendSealed(Protocol.MessageType.FileControl, done.ToArray());
                    Notify(L10n.T("文件已发送：") + name + "（" + sent + L10n.T(" 字节）"));
                    ToastIt(L10n.T("已发送到电脑：") + name);
                }
                catch (Exception ex)
                {
                    Notify(L10n.T("发送文件失败：") + ex.Message);
                }
                finally
                {
                    try { src.Dispose(); } catch { }
                    lock (_lock) { _outWait.Remove(id); _outAccepted.Remove(id); }
                }
            })
            { IsBackground = true, Name = "rd-sendfile" }.Start();
            return true;
        }

        private void HandleFileData(byte[] p)
        {
            if (p.Length < 8) return;
            int id = Protocol.ReadInt32(p, 0);
            int len = Protocol.ReadInt32(p, 4);
            if (len <= 0 || 8 + len > p.Length) return;
            FileStream fs = null;
            lock (_lock) { if (_inFiles.ContainsKey(id)) fs = _inFiles[id]; }
            if (fs == null) return;
            try
            {
                fs.Write(p, 8, len);
                fs.Flush();
            }
            catch (Exception ex)
            {
                Notify(L10n.T("写入文件失败：") + ex.Message);
            }
        }

        private void HandleAction(Protocol.ControlActionId action, RdInputService input)
        {
            switch (action)
            {
                case Protocol.ControlActionId.AndroidBack: input.Global(RdInputService.GlobalBack); break;
                case Protocol.ControlActionId.AndroidHome: input.Global(RdInputService.GlobalHome); break;
                case Protocol.ControlActionId.AndroidRecents: input.Global(RdInputService.GlobalRecents); break;
                case Protocol.ControlActionId.AndroidNotifications: input.Global(RdInputService.GlobalNotifications); break;
                case Protocol.ControlActionId.AndroidVolumeUp: input.Volume(true); break;
                case Protocol.ControlActionId.AndroidVolumeDown: input.Volume(false); break;
                case Protocol.ControlActionId.LockScreen: input.PowerDialog(); break;
            }
            Notify(L10n.T("执行动作 ") + action);
        }

        /* ===================== 发送工具 ===================== */

        private bool TrySendSealed(Protocol.MessageType type, byte[] payload)
        {
            NetworkStream ns = _stream;
            RdSecureChannel chan = _chan;
            if (ns == null || chan == null) return false;
            try
            {
                var ct = _cts != null ? _cts.Token : CancellationToken.None;
                // 【封包必须在锁内】Seal 会分配发送序号，序号必须与真正写进 socket 的顺序一致。
                // 以前 Seal 在锁外：画面线程与文件/输入线程可能"先算完后写"，对端收到乱序的
                // 序号 → 密文校验失败 → 整条连接直接断开（传文件中途掉线就是这么来的）。
                lock (_writeGate)
                {
                    byte[] wire = chan.Seal(RdFrame.Pack(type, payload ?? new byte[0]));
                    Protocol.SendAsync(ns, (Protocol.MessageType)RdWire.MsgSecurePayload, wire, ct, 8000)
                        .GetAwaiter().GetResult();
                }
                _sendFailStreak = 0;
                return true;
            }
            catch (Exception ex)
            {
                // 断线后画面线程每秒还会试十几次，逐条打日志会把 logcat 冲爆，
                // 反而盖掉真正有用的信息。只在前几次和每 50 次报一次。
                int n = Interlocked.Increment(ref _sendFailStreak);
                if (n <= 3 || n % 50 == 0)
                    Notify(L10n.T("发送失败（第 ") + n + L10n.T(" 次）：") + ex.Message + (n >= 3 ? L10n.T("，连接可能已断开") : ""));
                return false;
            }
        }

        private void SendSealed(Protocol.MessageType type, byte[] payload)
        {
            TrySendSealed(type, payload);
        }
    }
}

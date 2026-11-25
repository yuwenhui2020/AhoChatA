using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics;
using DeskShare;
using desk;

namespace AhoChatA
{
    /// <summary>
    /// 安卓侧远控会话：直连 desk（Windows 远程桌面）。
    ///
    /// 「统一」体现在三处，都不是抄一遍而是同一份源码：
    ///   1. 帧格式 —— 直接链接 desk/Protocol.cs，安卓与 Windows 端共用 MessageType/QualityConfig/读写函数；
    ///   2. 加密 —— 直接链接 desk/Shared/RdSecure.cs，握手、派生、加解封完全一致；
    ///   3. X25519 —— 用本工程已有的 Identity/Curve25519.cs，desk 反过来链接它。
    ///
    /// 因此只要一端改了协议细节，另一端编译期就会不一致，不会出现"能对上但行为不同"的漂移。
    ///
    /// 本类不含任何 UI，Activity 订阅事件即可。
    /// </summary>
    public sealed class RdSession : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private RdSecureChannel _secure;
        private CancellationTokenSource _cts;
        private volatile bool _stopped;

        /// <summary>H.264 硬解码器（按需创建）。</summary>
        private RdH264Decoder _h264;

        /// <summary>是否已经向电脑端要过关键帧（每次会话只要一次）。</summary>
        private bool _askedKeyFrame;

        /// <summary>收到的视频帧计数（诊断用）。</summary>
        private int _framesReceived;

        /// <summary>收到的消息条数（诊断用）。</summary>
        private int _msgsReceived;

        /// <summary>
        /// 协商后的能力集。默认 LegacyCaps（v1）：1.5s 内收不到 CapabilityAck 就停在这里，
        /// 此后帧格式、QualitySettings、心跳等所有行为与改动前逐字节一致。
        /// </summary>
        private Protocol.Capability _caps = Protocol.LegacyCaps;

        /// <summary>CapabilityAck 是否已到达（用于 1.5s 超时降级，只降一次）。</summary>
        private volatile bool _capAcked;

        /// <summary>最近一次收到任意报文的 UTC 毫秒（Heartbeat 30s 看门狗用）。</summary>
        private long _lastRecvMs;

        /// <summary>最近一次发送 QoS 反馈的 UTC 毫秒（限频 200ms）。</summary>
        private long _lastQosAt;

        /// <summary>目标码率参考（kbps），填进 EncodeQos 与 EncodeEx 的 BitrateKbps。</summary>
        private int _bitrateKbps = 4000;

        /// <summary>集中设置（参考 ScreenStream：首帧快出画面，先用较低 fps 起步，ABR 再提升）。</summary>
        private ScreenShareSettings _settings;
        private ScreenShareSettings Settings
        {
            get { return _settings ?? (_settings = ScreenShareSettings.Instance(Android.App.Application.Context)); }
        }

        /// <summary>
        /// 首次连接请求的 fps：低起步，避免一连上就请求 30fps 把采集线程的 720p JPEG 压缩打满。
        /// 【为什么】参考 ScreenStream「启动不等首帧、缓存帧立刻可交付」：先用低帧率
        /// 让画面尽快出来，后续由 CapabilityAck / QoS 反馈逐步提升，而不是一上来就 30fps。
        /// </summary>
        private int InitialFps
        {
            get { return Math.Max(10, Math.Min(20, Settings.MaxFps)); }
        }

        /// <summary>
        /// 查看端声明的能力集（连接成功后作为 Capability 发给被控端）。
        /// H264Decode 可以声明：RdH264Decoder 已实现 MediaCodec Annex-B 解码。
        /// 不含 H264Encode / H265Decode / MultiDisplay（查看端不编码、本轮不选屏）。
        /// </summary>
        private const Protocol.Capability ViewerCaps =
            Protocol.Capability.H264Decode |
            Protocol.Capability.RegionFrame |
            Protocol.Capability.Abr |
            Protocol.Capability.InputPriority |
            Protocol.Capability.Heartbeat |
            Protocol.Capability.UnicodeKey |
            Protocol.Capability.NormalizedCoord |
            Protocol.Capability.SecureOnly;

        /// <summary>写一条调试日志到 logcat（tag=Mchat）。</summary>
        private static void LogD(string msg)
        {
            try { Android.Util.Log.Debug("Mchat", msg); } catch { }
        }

        /// <summary>UTC 毫秒时间戳（心跳、QoS 限频共用；不用不确定的 TickCount64 API）。</summary>
        private static long UtcNowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>
        /// 连接成功后启动：1.5s 等不到 CapabilityAck 就降级 LegacyCaps（不阻塞连接）；
        /// 之后仅在协商出 Heartbeat 时每 5s 发一次带时间戳 Ping、并每 1s 检查 30s 无数据。
        /// 超时断开复用现有 ConnectionLost：这里只 Cancel + Close，让 ReadLoopAsync 的
        /// finally 去触发，不新造一套断线/重连机制。
        /// </summary>
        private void StartCapabilityWatch(CancellationToken ct)
        {
            Task.Run(async () =>
            {
                try { await Task.Delay(1500, ct); }
                catch (OperationCanceledException) { return; }
                catch { return; }
                if (!_capAcked && _caps != Protocol.LegacyCaps)
                {
                    _caps = Protocol.LegacyCaps;
                    LogD(L10n.T("[远控] 能力协商超时（1.5s 无 CapabilityAck），降级为 LegacyCaps（v1 行为）"));
                }
                long lastPingAt = 0;
                try
                {
                    while (!ct.IsCancellationRequested && !_stopped)
                    {
                        await Task.Delay(1000, ct);
                        if ((_caps & Protocol.Capability.Heartbeat) == 0) continue;
                        long now = UtcNowMs();
                        if (now - lastPingAt >= 5000)
                        {
                            lastPingAt = now;
                            byte[] ping = new byte[8];
                            Protocol.WriteInt64(ping, 0, now);
                            SendCore(Protocol.MessageType.Ping, ping);
                        }
                        long last = Interlocked.Read(ref _lastRecvMs);
                        if (last > 0 && now - last > 30000)
                        {
                            LogD(L10n.T("[远控] 30s 无数据，断开并触发 ConnectionLost（复用现有断线机制）"));
                            try { _cts?.Cancel(); } catch { }
                            try { _stream?.Close(); } catch { }
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });
        }

        /// <summary>解析被控端回的 CapabilityAck，存协商结果；ABR 时补发带码率的 13B 画质配置。</summary>
        private void HandleCapabilityAck(byte[] p)
        {
            byte verMin, verCur;
            ushort maxFps;
            Protocol.Capability caps;
            uint hash;
            if (!Protocol.TryDecodeCapability(p, out verMin, out verCur, out caps, out hash, out maxFps))
            {
                LogD(L10n.T("[远控] CapabilityAck 解析失败，保持 LegacyCaps（v1 行为）"));
                return;
            }
            _caps = caps;               // 被控端回的是已取交集的选中集，直接使用
            _capAcked = true;
            LogD(L10n.T("[远控] 能力协商完成 caps=0x") + ((uint)caps).ToString("X") + " maxFps=" + maxFps);
            if ((_caps & Protocol.Capability.Abr) != 0) SendExtendedQuality();
        }

        /// <summary>ABR 协商成功后补发 13B 画质配置（EncodeEx，尾部带 BitrateKbps）。v1 不会走到这里。</summary>
        private void SendExtendedQuality()
        {
            // 流畅度优先：ABR 协商成功后直接用用户的帧率上限（不再 +5 慢慢爬），
            // 首帧已由 InitialFps 快速出画面，之后 QoS/ABR 会按 RTT 自动降档。
            int fps = Settings.MaxFps;
            int quality = Settings.ClampQuality(Settings.JpegQuality);
            Protocol.QualityConfig q = new Protocol.QualityConfig(
                PreferH264 ? Protocol.EncodingMode.H264 : Protocol.EncodingMode.JpegFullFrame,
                quality, 1280, fps, 4, 32, _bitrateKbps);
            SendCore(Protocol.MessageType.QualitySettings, q.EncodeEx());
        }

        /// <summary>
        /// 收到带 FLAG_QOS 的帧后回一条 TestDelay（EncodeQos），限频 200ms。
        /// rttMs 用「本端收到时刻 - 帧头 sendTs」近似，bitrateKbps 用当前目标码率参考。
        /// </summary>
        private void SendQosFeedback(int frameId, long sendTs)
        {
            long now = UtcNowMs();
            if (_lastQosAt != 0 && now - _lastQosAt < 200) return;
            _lastQosAt = now;
            int rttMs = (int)Math.Max(0, Math.Min(int.MaxValue, now - sendTs));
            SendCore(Protocol.MessageType.TestDelay,
                Protocol.EncodeQos(frameId, sendTs, now, rttMs, _bitrateKbps));
        }

        /// <summary>向电脑端请求一个关键帧（IDR），让它能立刻起播。</summary>
        public void RequestKeyFrame()
        {
            SendCore(Protocol.MessageType.ForceKeyFrame, new byte[0]);
        }

        /// <summary>H.264 画面的渲染目标（SurfaceView 的 Surface）。为 null 则 H.264 走不了。</summary>
        ///
        /// 【为什么用属性而不是字段】
        /// SurfaceView 初始是隐藏的，Surface 要等它变成可见才创建；而解码器可能在
        /// Surface 出现之前就已经被第一帧 H.264 触发了。若这里只是普通字段，
        /// 解码器就永远拿不到 Surface → Configure 失败 → 一直黑屏。
        /// 所以赋值时要主动把 Surface 转交给已存在的解码器。
        /// </summary>
        public Android.Views.Surface RenderSurface
        {
            get { return _renderSurface; }
            set
            {
                _renderSurface = value;
                RdH264Decoder dec = _h264;
                if (dec != null && value != null) {
                    try { dec.AttachSurface(value); } catch { }
                }
            }
        }

        private Android.Views.Surface _renderSurface;

        /// <summary>当前画面是否走 H.264 硬解码（true）还是整帧 JPEG（false）。</summary>
        public bool UsingH264 { get; private set; }

        /// <summary>画面编码方式发生变化时触发（参数 true = H.264，false = JPEG）。</summary>
        public event Action<bool> CodecChanged;

        /// <summary>收到一帧画面（已解码为位图）。在后台线程触发，由 Activity 切到 UI 线程显示。</summary>
        public Action<Bitmap> FrameReady;

        /// <summary>画面尺寸变化（宽, 高）。后台线程触发，用于调整显示比例。</summary>
        public event Action<int, int> VideoSizeChanged;

        /// <summary>状态/错误提示（连上、断开、拒绝原因等）。</summary>
        public Action<string> Status;

        /// <summary>意外掉线（非用户主动断开）时触发，界面据此自动重连。</summary>
        public Action ConnectionLost;

        public bool Connected { get; private set; }

        /// <summary>本次会话的 6 位配对校验码：与电脑端日志里显示的应当一致，用于核对有没有被中间人劫持。</summary>
        public string SessionSas { get; private set; }

        /// <summary>最近一次画面的像素尺寸（用于把触摸坐标换算到 0..65535 归一化坐标）。</summary>
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }

        /// <summary>
        /// 建立会话。返回 null 表示成功，否则是给用户的失败原因。
        /// </summary>
        public async Task<string> ConnectAsync(string host, int port, string deviceCode, string password, bool lanDirect = false)
        {
            await DisconnectAsync();
            try
            {
                // 【必须重置】DisconnectAsync() 会把 _stopped 置 true（那是"主动断开"的语义），
                // 但 ConnectAsync 一进来就调用了它。若不重置，后面启动的读循环
                // `while (!ct.IsCancellationRequested && !_stopped)` 会在第一轮就判定结束，
                // 于是连接"看起来成功"却一帧都收不到——表现就是手机端全黑。
                _stopped = false;
                // v2：每次连接都从 LegacyCaps 起步，1.5s 内收不到 CapabilityAck 就保持 v1 行为。
                _caps = Protocol.LegacyCaps;
                _capAcked = false;
                _lastRecvMs = UtcNowMs();
                _lastQosAt = 0;
                // 重连时旧的解码器要扔掉：它的 MediaCodec 绑着上一块 Surface，
                // 参数集/尺寸也可能已经变了，留着只会解不出画面。
                try { _h264?.Dispose(); } catch { }
                _h264 = null;
                _askedKeyFrame = false;
                _framesReceived = 0;
                _msgsReceived = 0;
                _cts = new CancellationTokenSource();
                _client = new TcpClient();
                // 加大收发缓冲：H264 整帧 / 高码率下，接收端短缓冲会让内核频繁背压、
                // 发送端 zlib 帧排队，表现为卡顿与延迟抖动。与 PC 控制端保持同一量级（1MB）。
                try { _client.NoDelay = true; _client.SendBufferSize = 1048576; _client.ReceiveBufferSize = 1048576; } catch { }
                var connect = _client.ConnectAsync(host, port);
                if (await Task.WhenAny(connect, Task.Delay(6000)) != connect)
                    return L10n.T("连接超时（6 秒），确认电脑与被控端端口是否正确、是否在同一局域网");

                // 【必须 await】ConnectAsync 失败时返回的是"已完成但带异常"的 Task，
                // Task.WhenAny 会直接返回它 —— 不 await 就察觉不到失败，接着
                // GetStream() 才抛出 "The operation is not allowed on non-connected
                // sockets."，把真正的连不上原因（拒绝连接/网络不可达）彻底掩盖掉。
                await connect;

                _stream = _client.GetStream();
                string err = await HandshakeAsync(deviceCode, password, lanDirect);
                if (err != null) return err;

                Connected = true;
                StartSendPump();
                var loop = ReadLoopAsync(_cts.Token);
                // v2 能力协商：主动声明查看端能力（旧被控端不认识 type=43 会走 default 忽略，
                // 会话照常完成）；1.5s 等不到 Ack 由 StartCapabilityWatch 降级，不阻塞连接。
                // 能力协商声明的 maxFps 改读集中设置；首次请求改用较低 fps 起步，
                // 别一上来就 30fps 把被控端采集线程打满（参考 ScreenStream 快出画面）。
                SendCore(Protocol.MessageType.Capability,
                    Protocol.EncodeCapability(ViewerCaps, (ushort)Settings.MaxFps));
                StartCapabilityWatch(_cts.Token);
                int initialFps = InitialFps;
                RequestQuality(initialFps, 1280, PreferH264);
                Settings.LogEffective("RdSession.Connect");
                LogD($"[远控] 已连接 {host}:{port}，首次请求 {initialFps}fps（低起步，ABR 再提升），PreferH264={PreferH264}");
                return null;
            }
            catch (Exception ex)
            {
                LogD(L10n.T("[远控] 连接失败：") + ex.GetType().Name + " - " + ex.Message);
                return L10n.T("连接失败：") + ex.Message;
            }
        }

        /// <summary>加密握手：临时 X25519 → 派生密钥 → 密文里送「设备码 + 连接密码」。
        /// 握手全程用本次连接的局部流/令牌，短超时；这样既不会被并发连接串流，
        /// 也不会把"读超时/对端断开"误报成"拒绝加密/中间人篡改"。</summary>
        private async Task<string> HandshakeAsync(string deviceCode, string password, bool lanDirect)
        {
            var stream = _stream;
            var ct = _cts.Token;
            const int HandshakeTimeoutMs = 8000;

            RdKeypair kp = RdKeypair.NewEphemeral();
            byte[] hello = new byte[RdWire.HelloBodyLength];
            hello[0] = RdWire.Version;
            Buffer.BlockCopy(kp.PublicKey, 0, hello, 1, 32);
            await Protocol.SendAsync(stream, Protocol.MessageType.RdHello, hello);

            (Protocol.MessageType type, byte[] payload)? ack =
                await Protocol.ReadMessageAsync(stream, ct, HandshakeTimeoutMs);
            if (ack == null)
                return L10n.T("电脑没有响应加密握手，请确认电脑端已开启被控端且版本一致");
            if (ack.Value.type == Protocol.MessageType.LoginResponse)
            {
                // 老版本/明文路径的回包，不是加密握手应答——不是中间人，是版本不匹配
                return L10n.T("电脑端版本较旧，未启用加密远控，请升级电脑端程序");
            }
            if (ack.Value.type != Protocol.MessageType.RdHelloAck || ack.Value.payload.Length < RdWire.HelloAckBodyLength)
                return L10n.T("电脑未接受加密握手（收到帧类型 ") + ack.Value.type + L10n.T("），请重试或升级电脑端");

            byte[] serverPub = new byte[32];
            Buffer.BlockCopy(ack.Value.payload, 0, serverPub, 0, 32);

            RdSessionKeys keys = RdSessionKeys.Derive(kp.SharedSecret(serverPub), kp.PublicKey, serverPub, isServer: false);
            RdSecureChannel chan = new RdSecureChannel(keys);
            SessionSas = keys.Sas();

            // 局域网直连：设备码与密码都不需要，密文里只带一个固定标记。
            // 被控端会校验来访 IP 是否同网段 + 是否允许直连，不是无脑放行。
            string credential = lanDirect
                ? RdWire.LanDirectToken + "\n"
                : RdDeviceCode.Normalize(deviceCode) + "\n" + (password ?? "");
            await Protocol.SendAsync(stream, Protocol.MessageType.SecurePayload,
                chan.Seal(Encoding.UTF8.GetBytes(credential)));

            (Protocol.MessageType type, byte[] payload)? resp =
                await Protocol.ReadMessageAsync(stream, ct, HandshakeTimeoutMs);
            if (resp == null)
                return L10n.T("未收到登录响应，请确认电脑端被控端状态后重试");
            if (resp.Value.type != Protocol.MessageType.SecurePayload || !chan.TryOpen(resp.Value.payload, out byte[] inner))
                return L10n.T("登录响应校验失败（帧类型/密钥不匹配），请重试；若反复失败再核对配对校验码");

            _secure = chan;
            if (inner.Length > 0 && inner[0] == 1) return null;   // 登录成功
            return inner.Length > 1 ? Encoding.UTF8.GetString(inner, 1, inner.Length - 1) : L10n.T("登录失败");
        }

        /// <summary>
        /// 要求被控端改用编码模式。
        ///
        /// 默认走「整帧 JPEG」：每帧都是完整 JPEG，手机侧 BitmapFactory 直接解码即可，
        /// 代价是带宽大（一屏 640x360 的 JPEG 约 30~60KB，30fps 就是 1~2MB/s）。
        ///
        /// 「H.264 整帧」带宽只有 JPEG 的十分之一左右，代价是依赖 MediaCodec 硬解码；
        /// 如果被控端编码器起不来，它会自动退回整帧 JPEG，手机端两种格式都能解，
		/// 所以可以放心直接请求 H.264。
        /// </summary>
        private void RequestQuality(int fps, int maxWidth, bool preferH264)
        {
            Protocol.QualityConfig q = new Protocol.QualityConfig(
                preferH264 ? Protocol.EncodingMode.H264 : Protocol.EncodingMode.JpegFullFrame,
                Settings.ClampQuality(Settings.JpegQuality), maxWidth, fps, 4, 32);
            SendCore(Protocol.MessageType.QualitySettings, q.Encode());
        }

        /// <summary>是否优先请求 H.264 整帧模式。</summary>
        public bool PreferH264 { get; set; } = true;

        public void SetQuality(int fps, int maxWidth)
        {
            RequestQuality(fps, maxWidth, PreferH264);
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                LogD(L10n.T("[远控] 读循环启动"));
                while (!ct.IsCancellationRequested && !_stopped)
                {
                    // 不设空闲超时：电脑端桌面静止时本来就不发帧，用 30 秒超时会把健康连接掐断。
                    (Protocol.MessageType type, byte[] payload)? raw = await Protocol.ReadMessageAsync(_stream, ct, 0);
                    if (raw == null)
                    {
                        Status?.Invoke(L10n.T("连接已结束"));
                        break;
                    }
                    // 收到任意报文都刷新心跳时间戳（仅在协商出 Heartbeat 时才据此判 30s 超时；
                    // 记录本身不改变任何发出的字节，v1 逐字节一致）。
                    Interlocked.Exchange(ref _lastRecvMs, UtcNowMs());
                    _msgsReceived++;
                    if (_msgsReceived <= 5 || _msgsReceived % 50 == 0)
                    {
                        LogD($"[远控] 收到第 {_msgsReceived} 条消息 外层type={raw.Value.type} len={raw.Value.payload.Length} secure={( _secure != null)}");
                    }
                    (Protocol.MessageType type, byte[] payload)? msg = RdFrame.Unwrap(_secure, raw.Value.type, raw.Value.payload);
                    if (msg == null)
                    {
                        LogD($"[远控] 解封失败（外层type={raw.Value.type}）");
                        Status?.Invoke(L10n.T("密文帧校验失败，已断开"));
                        break;
                    }
                    switch (msg.Value.type)
                    {
                        case Protocol.MessageType.VideoFrame:
                            HandleVideoFrame(msg.Value.payload);
                            break;
                        case Protocol.MessageType.CapabilityAck:
                            HandleCapabilityAck(msg.Value.payload);
                            break;
                        case Protocol.MessageType.Pong:
                            // 心跳回显：lastRecvMs 已在上面统一刷新，无需额外处理（v1 也不受影响）。
                            break;
                        default:
                            // 未知 / 本轮未启用的 type 一律忽略，**绝不断连**：
                            // v1 被控端会回显 TestDelay、发各种我们暂不处理的报文。
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogD($"[远控] 读循环异常：{ex.GetType().Name} {ex.Message}");
                if (!_stopped) Status?.Invoke(L10n.T("连接中断：") + ex.Message);
            }
            finally
            {
                Connected = false;
                _caps = Protocol.LegacyCaps;   // 断线重置：重连时重新协商，不继承上一条会话
                // 主动断开（_stopped）不触发：只有意外掉线才通知界面去重连
                if (!_stopped)
                {
                    var cb = ConnectionLost;
                    if (cb != null) { try { cb(); } catch { } }
                }
            }
        }

        /// <summary>
        /// 解视频帧。格式与 desk 端 RemoteDesktopClient.HandleFrameCore 完全同源：
        ///   [0] flags  [1] codec  [2..6) 宽  [6..10) 高  [10..14) 数据长度  [14..) 数据
        /// 整帧 JPEG：flags 的 FLAG_REGION 位为 0、codec = CODEC_JPEG。
        /// v2：FLAG_QOS 置位时 14B 帧头后追加 12B QoS 头 [frameId:4][sendTs:8]，
        ///      图像数据从 dataOff=26 开始（len 仍只是图像数据长度）。
        /// </summary>
        private void HandleVideoFrame(byte[] p)
        {
            if (p.Length < 14) return;
            byte flags = p[0];
            byte codec = p[1];
            int w = Protocol.ReadInt32(p, 2);
            int h = Protocol.ReadInt32(p, 6);
            if (w <= 0 || h <= 0) return;

            // 区域增量帧：v1（LegacyCaps）沿用旧行为直接跳过；v2 协商出 RegionFrame
            // 时先记录并忽略本帧（区域合成尚未实现），但**不整条连接断掉**。
            if ((flags & Protocol.FLAG_REGION) != 0)
            {
                if ((_caps & Protocol.Capability.RegionFrame) != 0)
                {
                    LogD(L10n.T("[远控] 收到 FLAG_REGION 增量帧（区域合成未实现，已忽略本帧，不断连）"));
                }
                return;
            }

            // QoS 帧头：仅当协商出 Abr 时被控端才会置 FLAG_QOS；这里跳过 12B 头再读图像数据。
            int dataOff = 14;
            if ((flags & Protocol.FLAG_QOS) != 0)
            {
                if (14 + Protocol.QosFrameHeaderLength > p.Length) return;
                int frameId = Protocol.ReadInt32(p, 14);
                long sendTs = Protocol.ReadInt64(p, 18);
                dataOff = 14 + Protocol.QosFrameHeaderLength;
                if ((_caps & Protocol.Capability.Abr) != 0)
                    SendQosFeedback(frameId, sendTs);
            }

            int len = Protocol.ReadInt32(p, 10);
            if (len <= 0 || dataOff + len > p.Length) return;

            _framesReceived++;
            if (_framesReceived <= 3 || _framesReceived % 30 == 0)
            {
                LogD($"[远控] 收到第 {_framesReceived} 帧 codec={codec} {w}x{h} len={len} payload={p.Length}");
            }

            try
            {
                if (codec == Protocol.CODEC_JPEG)
                {
                    if (UsingH264)
                    {
                        UsingH264 = false;
                        CodecChanged?.Invoke(false);
                    }
                    Bitmap bmp = BitmapFactory.DecodeByteArray(p, dataOff, len);
                    if (bmp == null) return;
                    FrameWidth = bmp.Width;
                    FrameHeight = bmp.Height;
                    FrameReady?.Invoke(bmp);
                }
                else if (codec == Protocol.CODEC_H264)
                {
                    // 分辨率变化：旧解码器的参数集/输出 Surface 已按旧尺寸配置，
                    // 必须 Dispose 整个实例再重建（解码器内部只同步渲染尺寸，不重建 MediaCodec）。
                    // 第一帧 FrameWidth/Height 还是 0，不算变化，不能重建刚建好的实例。
                    if (FrameWidth > 0 && FrameHeight > 0 && (FrameWidth != w || FrameHeight != h))
                    {
                        try { _h264?.Dispose(); } catch { }
                        _h264 = null;
                        // 不重置 _askedKeyFrame：重建由解码器的 ReplayKeyFrame /
                        // ConsumeKeyFrameRequest 负责从缓存 IDR 起播，v1 下不额外多发报文。
                        LogD(L10n.T("[远控] 画面尺寸变化，已重建 H264 解码器 ") + FrameWidth + "x" + FrameHeight + " -> " + w + "x" + h);
                    }
                    if (_h264 == null)
                    {
                        _h264 = new RdH264Decoder();
                        if (RenderSurface != null) _h264.AttachSurface(RenderSurface);
                    }
                    // 帧头里带着电脑端的显示尺寸，用它来摆正画面比例，
                    // 免得 16:9 的桌面被硬拉成手机竖屏的长条。
                    if (w > 0 && h > 0 && (FrameWidth != w || FrameHeight != h))
                    {
                        FrameWidth = w;
                        FrameHeight = h;
                        try { VideoSizeChanged?.Invoke(w, h); } catch { }
                    }
                    byte[] annexB = new byte[len];
                    Buffer.BlockCopy(p, dataOff, annexB, 0, len);
                    _h264.Decode(annexB);
                    // 解码器换过封装模式（旧实例已释放）时，必须重新要一个 IDR 才能起播。
                    if (_h264.ConsumeKeyFrameRequest()) RequestKeyFrame();
                    if (!UsingH264)
                    {
                        UsingH264 = true;
                        CodecChanged?.Invoke(true);
                    }
                    // 解码器刚配好（或换了 Surface）时，向电脑要一个关键帧：
                    // H.264 的关键帧间隔按输入帧数计，电脑端帧率低时可能要等好几秒，
                    // 期间手机上是黑屏。要一个 IDR 立刻就能出画面。
                    if (_h264.Configured && !_askedKeyFrame)
                    {
                        _askedKeyFrame = true;
                        RequestKeyFrame();
                    }
                }
                else if (codec == Protocol.CODEC_RAW)
                {
                    // 原始 RGBA：手工逐行拷到 Bitmap（ARGB_8888）
                    Bitmap bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
                    int stride = w * 4;
                    int[] row = new int[w];
                    for (int y = 0; y < h; y++)
                    {
                        int off = dataOff + y * stride;
                        if (off + stride > p.Length) break;
                        for (int x = 0; x < w; x++)
                        {
                            int i = off + x * 4;
                            byte b = p[i], g = p[i + 1], r = p[i + 2], a = p[i + 3];
                            row[x] = (a << 24) | (r << 16) | (g << 8) | b;
                        }
                        bmp.SetPixels(row, 0, w, 0, y, w, 1);
                    }
                    FrameWidth = w;
                    FrameHeight = h;
                    FrameReady?.Invoke(bmp);
                }
            }
            catch
            {
                // 单帧解码失败无需中断会话，下一帧继续
            }
        }

        /// <summary>
        /// 发送线程模型：**单写线程 + 队列**。
        ///
        /// 不用「每条命令起一个 Task」：那样相邻两条事件的密文帧可能乱序发出，
        /// 而接收端严格按序号校验，一乱序就整条连接被判死——现象是打字漏字、拖动后秒断。
        /// 也不能在 UI 线程里同步 GetResult()：对端不读数据时会把界面卡死。
        /// 因此入队即刻返回，由这唯一的写线程负责「按顺序封帧 + 按顺序写出」。
        /// </summary>
        private sealed class OutFrame
        {
            public Protocol.MessageType Type;
            public byte[] Payload;
        }

        private readonly System.Collections.Concurrent.BlockingCollection<OutFrame> _sendQueue
            = new System.Collections.Concurrent.BlockingCollection<OutFrame>();
        private Task _sendPump;

        private void StartSendPump()
        {
            _sendPump = Task.Run(async () =>
            {
                CancellationToken ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    OutFrame f;
                    try { f = _sendQueue.Take(ct); }
                    catch { break; }

                    RdSecureChannel chan = _secure;
                    NetworkStream ns = _stream;
                    if (ns == null || f == null) break;

                    try
                    {
                        if (chan != null)
                        {
                            // 封帧必须发生在写线程内：序号在这里单调递增，才能保证对端按序解封。
                            byte[] wire = chan.Seal(RdFrame.Pack(f.Type, f.Payload ?? new byte[0]));
                            await Protocol.SendAsync(ns, (Protocol.MessageType)RdWire.MsgSecurePayload, wire, ct, 8000);
                        }
                        else
                        {
                            await Protocol.SendAsync(ns, f.Type, f.Payload ?? new byte[0], ct, 8000);
                        }
                    }
                    catch
                    {
                        Status?.Invoke(L10n.T("发送失败，连接已断开"));
                        break;
                    }
                }
            });
        }

        private void SendCore(Protocol.MessageType type, byte[] payload)
        {
            if (_stopped || _stream == null) return;
            try
            {
                _sendQueue.Add(new OutFrame { Type = type, Payload = payload });
            }
            catch
            {
            }
        }

        /* ===================== 输入 ===================== */

        /// <summary>
        /// 归一化坐标 0..65535 —— 与 Windows 端 MouseEvent 的编码一致。
        ///
        /// 【坐标基准 · 本轮只注释 + 兼容，不改输入协议】
        /// 这里与 RdInputService 都用 0..65535 归一化坐标，而帧头里的 w/h 是**采集缩放后
        /// 的尺寸**、被控端注入用的是物理屏幕尺寸，两端基准并不一致。v2 已声明
        /// NormalizedCoord（MouseEventEx 按帧宽高映射），但本轮不新增/不改写输入报文，
        /// 仍发 v1 的 8 字节 MouseEvent，保证与旧被控端逐字节兼容。
        /// </summary>
        private static void WriteMouse(byte[] buf, byte type, double fx, double fy, byte button, short wheel)
        {
            int xn = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, fx)) * 65535.0);
            int yn = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, fy)) * 65535.0);
            buf[0] = type;
            buf[1] = (byte)(xn & 0xFF);
            buf[2] = (byte)(xn >> 8);
            buf[3] = (byte)(yn & 0xFF);
            buf[4] = (byte)(yn >> 8);
            buf[5] = button;
            buf[6] = (byte)(wheel & 0xFF);
            buf[7] = (byte)(wheel >> 8);
        }

        // 按键编码与 Windows 侧统一（Protocol.MouseButton）：1=左 2=右 3=中。
        // 早期这里用 0=左/1=右，导致手机点左键时电脑端收到 0（不认识的键）→ 完全没有点击；
        // 手机上的"右键"反而变成电脑上的左键。
        public const byte BtnLeft = 1;
        public const byte BtnRight = 2;
        public const byte BtnMiddle = 3;

        public void MouseMove(double fx, double fy) { SendMouse(0, fx, fy, 0, 0); }
        public void MouseDown(byte button, double fx, double fy) { SendMouse(1, fx, fy, button, 0); }
        public void MouseUp(byte button, double fx, double fy) { SendMouse(2, fx, fy, button, 0); }
        public void MouseWheel(short delta, double fx, double fy) { SendMouse(3, fx, fy, 0, delta); }

        /// <summary>
        /// 相对移动（触控板模式）：只发位移增量，不动绝对坐标。
        /// 远程玩游戏、画图时绝对定位会"跳"，相对移动才跟手 —— 与 Windows 查看端的
        /// SendMouseRelative 编码完全一致（type=4，dx/dy 各 2 字节，紧跟 6 字节头）。
        /// </summary>
        public void MouseRelative(int dx, int dy)
        {
            byte[] b = new byte[10];
            b[0] = 4;
            short sx = (short)Math.Max(-32768, Math.Min(32767, dx));
            short sy = (short)Math.Max(-32768, Math.Min(32767, dy));
            b[6] = (byte)(sx & 0xFF); b[7] = (byte)(sx >> 8);
            b[8] = (byte)(sy & 0xFF); b[9] = (byte)(sy >> 8);
            SendCore(Protocol.MessageType.MouseEvent, b);
        }

        private void SendMouse(byte type, double fx, double fy, byte button, short wheel)
        {
            byte[] buf = new byte[8];
            WriteMouse(buf, type, fx, fy, button, wheel);
            SendCore(Protocol.MessageType.MouseEvent, buf);
        }

        /// <summary>
        /// 发送按键。vk 是 Windows 虚拟键码（VK_*），与 desk 端 KeyEvent 编码一致：
        ///   [0..4) vk    [4] 按下/抬起    [5..8) CapsLock/NumLock/ScrollLock 状态
        /// </summary>
        public void Key(int vk, bool down)
        {
            byte[] buf = new byte[8];
            BitConverter.GetBytes(vk).CopyTo(buf, 0);
            buf[4] = (byte)(down ? 1 : 0);
            SendCore(Protocol.MessageType.KeyEvent, buf);
        }

        public void KeyPress(int vk)
        {
            Key(vk, true);
            Key(vk, false);
        }

        /// <summary>
        /// 把一串字符打进远端（内置键盘 / 文本输入共用）。
        /// ASCII 走 VK（与 Windows 键码一致，老被控端也认）；非 ASCII（中文/符号）
        /// 走 UnicodeText(type=50, UTF-16LE)，由被控端 InputInjector.Unicode 注入。
        /// 两类字符按出现顺序分别入同一个单写队列，不会乱序。
        /// </summary>
        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var unicodeBuf = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    FlushUnicode();
                    KeyPress(0x0D);
                    continue;
                }
                if (c < 32 || c > 126)
                {
                    // 中文 / 全角符号不能用 VK 表达，攒起来一次性以 UTF-16LE 发出
                    unicodeBuf.Append(c);
                    continue;
                }

                FlushUnicode();
                bool upper = c >= 'A' && c <= 'Z';
                char lower = upper ? (char)(c - 'A' + 'a') : c;
                if (upper) Key(0x10, true);
                KeyPress((int)char.ToUpperInvariant(lower));
                if (upper) Key(0x10, false);
            }
            FlushUnicode();

            void FlushUnicode()
            {
                if (unicodeBuf.Length == 0) return;
                SendUnicodeText(unicodeBuf.ToString());
                unicodeBuf.Length = 0;
            }
        }

        /// <summary>
        /// 直接发送一段 UTF-16LE 文本（中文 IME / 粘贴）。被控端按 Encoding.Unicode 解析；
        /// 旧被控端收到未知 type=50 会走 default 忽略，不影响连接。
        /// </summary>
        public void SendUnicodeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            SendCore(Protocol.MessageType.UnicodeText, Encoding.Unicode.GetBytes(text));
        }

        public async Task DisconnectAsync()
        {
            _stopped = true;
            Connected = false;
            _caps = Protocol.LegacyCaps;   // 断线重置 _caps，重连时重新协商
            _capAcked = false;
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
            _secure = null;
            await Task.FromResult(0);
        }

        public void Dispose()
        {
            try { _h264?.Dispose(); } catch { }
            _h264 = null;
            try { DisconnectAsync().Wait(1000); } catch { }
        }
    }
}

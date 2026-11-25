using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Android.Content;
using Android.Hardware.Camera2;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Encoding = System.Text.Encoding;
using Android.OS;
using Android.Util;
using Android.Views;

namespace AhoChatA
{
    /// <summary>共享来源：本机摄像头 / 本机屏幕。</summary>
    public enum ShareSource
    {
        Camera = 0,
        Screen = 1
    }

    /// <summary>
    /// IP 摄像头服务器（静态单例）：
    ///   1. Camera2 打开前/后摄，把预览帧送进 MediaCodec 硬编 H.264（Annex-B 裸流）；
    ///   2. 起一个极简 HTTP 服务，把 H.264 裸流以 video/h264 持续推给所有连入的查看端（支持多人同看）；
    ///   3. 每 ~1 秒向局域网广播 UDP 信标 LANCAM，让其它设备“嗅探”到。
    /// 同一 HTTP 地址也可被 PC 端 VLC / ffplay 直接打开（http://手机IP:端口/），/info 返回 JSON 元数据。
    /// 仅用于局域网，不跨网。
    /// </summary>
    public sealed class IpCameraServer
    {
        public const int HttpPort = 7790;
        public const int BeaconPort = 7791;
        private const int ColorFormatSurface = 0x7F000789; // MediaCodec COLOR_FormatSurface

        private static readonly IpCameraServer _instance = new IpCameraServer();
        public static IpCameraServer Instance => _instance;

        private readonly object _lock = new object();
        private bool _running;
        // 编码器输出格式（4 字节长度前缀 vs 已是 Annex-B）整个会话恒定。
        // 首帧判定后缓存，避免编码主循环每帧做一次 O(帧大小) 的全缓冲扫描（发热/掉帧主因之一）。
        private bool? _annexBFormat;
        private Context _ctx;
        private HandlerThread _camThread;
        private Handler _camHandler;
        private CameraDevice _camDevice;
        private CameraCaptureSession _camSession;
        private Surface _inputSurface;
        private MediaCodec _encoder;
        private Thread _encoderThread;
        private TcpListener _http;
        private Thread _httpThread;
        private Thread _beaconThread;
        private UdpClient _beaconSock;
        private int _beaconSent;

        private readonly List<StreamViewer> _viewers = new List<StreamViewer>();
        private readonly List<StreamViewer> _tsViewers = new List<StreamViewer>();   // /live.ts 查看端（播放器）
        private readonly List<StreamViewer> _frameViewers = new List<StreamViewer>(); // /frames（WebCodecs 低延迟通道）
        private const int MaxFrameViewers = 24;
        private int _lastKeyReqAt;
        private readonly TsMuxer _ts = new TsMuxer();
        private readonly object _tsLock = new object();   // TsMuxer 内部有连续计数器，多线程打包必须串行
        private byte[] _spsAnnexB;
        private byte[] _ppsAnnexB;

        // MSE（浏览器）分片缓存：以 IDR 为单位切片，环形保留最近若干个
        private readonly object _segLock = new object();
        private readonly List<Mp4Segment> _segments = new List<Mp4Segment>();
        private readonly List<byte[]> _segSamples = new List<byte[]>();
        private long _segBaseDecode;
        private uint _segSeq;
        private byte[] _initMp4;
        private string _html;
        private const int SegKeep = 24;      // 多留几段，容忍网络抖动/切后台再回来的客户端

        /* ---------- 码率/分辨率自适应（帧率恒定，只调码率与分辨率） ---------- */

        private readonly object _rebuildLock = new object();
        private int[][] _rungs;                 // [i] = { 宽, 高, 码率bps }，0 为最高档（用户所选上限）
        private int _rung;                      // 当前档位
        private int _abrScore;
        private DateTime _rungAt = DateTime.MinValue;
        private int _dropSignals;
        private int _stallSignals;
        private Thread _abrThread;
        private int _reconfigFloor = 6000;      // 启动后 / 每次切档后的静默期（ms）
        private int _upDelaySec = 12;           // 升档所需的L10n.T("持续流畅")时长，降档会翻倍（避免来回抖）
        private int _epoch;                     // 编码参数代次：变更后浏览器需重新取 init 段
        private long _rungSwitches;

        /* ---------- 屏幕共享（MediaProjection / VirtualDisplay） ---------- */

        private MediaProjection _mp;
        private ProjectionStopCallback _mpCb;
        private VirtualDisplay _vd;
        private DisplayManager _dm;
        private ScreenDisplayListener _dispListener;
        private int _screenDpi = 320;
        // 短边兜底改读集中设置（ScreenStream 默认 50% 缩放对 1440 短边 ≈ 720），不再散落写死。
        private int _maxShortSide = ScreenShareSettings.FallbackShortSide;
        private int _capW, _capH;          // 采集源原始像素（屏幕会随旋转变化）
        private int _capBr = 4_000_000;    // 用户选的上限码率
        private int _dispTick;

        /* ---------- 集中设置 / 首帧看门狗 / 采集会话真实状态 ---------- */

        /// <summary>集中设置（首个 Start 时初始化，之后一直复用同一实例）。</summary>
        private ScreenShareSettings _settings;
        private ScreenShareSettings Settings
        {
            get { return _settings ?? (_settings = ScreenShareSettings.Instance(_ctx)); }
        }

        /// <summary>编码输出线程是否已产出过第一帧（首帧看门狗判定用）。</summary>
        private volatile int _firstFrameSeen;
        /// <summary>采集会话是否「真正」配置成功：用来替代 CreateSessionInternal(0) 无条件 return true 的假启动。</summary>
        private volatile int _sessionStarted;
        /// <summary>采集会话配置失败（OnConfigureFailed / 建会话抛异常）时置位，供失败回报与重试。</summary>
        private volatile int _sessionFailed;
        /// <summary>会话配置完成信号：由 OnConfigured/OnConfigureFailed 置位，OpenCameraAndEncoder 等待它判定真实结果。</summary>
        private ManualResetEvent _sessionReady;

        // 当前配置（供 /info 与信标使用）
        public bool IsRunning { get { lock (_lock) return _running; } }
        public bool Front { get; private set; }
        public ShareSource Source { get; private set; }

        /// <summary>屏幕共享启动结果回调（ok, 失败原因）。同进程内使用，避免动态广播注册的 API 版本坑。</summary>
        public static event Action<bool, string> ScreenStartResult;

        internal static void NotifyScreenStartResult(bool ok, string msg)
        {
            try { ScreenStartResult?.Invoke(ok, msg); } catch { }
        }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Fps { get; private set; }
        public int Bitrate { get; private set; }
        public int Rotation { get; private set; }
        public int UsedHttpPort { get; private set; }
        public string LastError { get; private set; }
        public int ViewerCount
        {
            get
            {
                int n;
                lock (_viewers) n = _viewers.Count;
                lock (_tsViewers) n += _tsViewers.Count;
                return n;
            }
        }
        public DateTime StartTime { get; private set; }

        public string LocalUrl
        {
            get
            {
                var ip = DeviceStatus.Ip;
                // 统一端口下摄像头的固定入口是 /cam（旧的根路径仍兼容）
                string suffix = UsedHttpPort == WebServer.ExternalPortActual ? "/cam" : "/";
                return "http://" + ip + ":" + UsedHttpPort + suffix;
            }
        }

        public event Action<string> OnError;

        private IpCameraServer() { }

        public bool Start(Context ctx, bool front, int w, int h, int fps, int bitrate)
        {
            lock (_lock)
            {
                if (_running) return true;
                LastError = null;
                _annexBFormat = null;   // 新会话：重置编码格式缓存
                try
                {
                    _ctx = ctx.ApplicationContext ?? ctx;
                    // 【为什么】设置集中读 + 打印生效值；新会话复位首帧/会话真实状态标志，
                    // 避免上一次的「已启动/已产帧」残留把本次也判成成功（假启动）。
                    _settings = ScreenShareSettings.Instance(_ctx);
                    // 【为什么】订阅设置变更：改 fps/quality 只触发 ApplyEncodingSettings
                    // （改编码参数/请求关键帧），绝不重建 VirtualDisplay（对齐 ScreenStream）。
                    _settings.Changed -= OnSettingsChanged;
                    _settings.Changed += OnSettingsChanged;
                    _firstFrameSeen = 0;
                    _sessionStarted = 0;
                    _sessionFailed = 0;
                    _sessionReady = new ManualResetEvent(false);
                    Source = ShareSource.Camera;
                    Front = front;
                    // 硬编码 30fps 改读集中设置：AutoFps 时用标称 MaxFps，且不超过 MaxFps。
                    Fps = Settings.ClampFps(fps > 0 ? Math.Min(fps, Settings.MaxFps) : Settings.MaxFps);
                    Width = w; Height = h; Bitrate = bitrate;
                    Rotation = ComputeRotation(front);

                    // 关键：必须先置运行标志，否则下面各线程的循环条件 (_running) 立即为 false 而直接退出，
                    // 导致端口 listen 但无人 accept、无人编码、无人发信标（表现为查看端 0 字节 / 永久黑屏）。
                    _running = true;
                    StartTime = DateTime.Now;
                    _capW = w; _capH = h; _capBr = bitrate;
                    _maxShortSide = Math.Min(w, h);
                    BuildLadder(w, h, bitrate, _maxShortSide);
                    InitStreamState();

                    // 1) 摄像头 + 编码
                    var ok = OpenCameraAndEncoder();
                    if (!ok)
                    {
                        Stop();
                        return false;
                    }

                    // 2) HTTP 流服务
                    StartHttp();

                    // 3) UDP 信标
                    StartBeacon();

                    // 4) 码率自适应控制器
                    StartAbr();

                    Log.Warn("IpCam", "共享已启动：HTTP=" + LocalUrl + " 编码 " + w + "x" + h + "@" + Fps + " br=" + bitrate + " front=" + front);
                    Settings.LogEffective("Start(camera)");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    OnError?.Invoke(ex.Message);
                    Stop();
                    return false;
                }
            }
        }

        /// <summary>
        /// 启动屏幕共享。必须在「mediaProjection 类型的前台服务」已经 StartForeground 之后调用，
        /// 否则 Android 10+ 会拒绝 MediaProjection。
        /// </summary>
        /// <param name="shortSide">画质上限：屏幕短边像素（720 / 1080）</param>
        public bool StartScreen(Context ctx, int resultCode, Intent data, int shortSide, int fps, int bitrate)
        {
            lock (_lock)
            {
                if (_running) return true;
                LastError = null;
                _annexBFormat = null;   // 新会话：重置编码格式缓存
                try
                {
                    _ctx = ctx.ApplicationContext ?? ctx;
                    // 【为什么】同 Start(camera)：集中设置 + 复位真实状态标志，杜绝假启动残留。
                    _settings = ScreenShareSettings.Instance(_ctx);
                    // 改 fps/quality 只改编码参数，绝不重建 VirtualDisplay（对齐 ScreenStream）。
                    _settings.Changed -= OnSettingsChanged;
                    _settings.Changed += OnSettingsChanged;
                    _firstFrameSeen = 0;
                    _sessionStarted = 0;
                    _sessionFailed = 0;
                    _sessionReady = new ManualResetEvent(false);
                    Source = ShareSource.Screen;
                    Front = false;
                    // 硬编码 30fps 改读集中设置；短边未给时用 ScalePercent 兜底。
                    Fps = Settings.ClampFps(fps > 0 ? Math.Min(fps, Settings.MaxFps) : Settings.MaxFps);
                    Rotation = 0;                 // 屏幕内容已是正方向，查看端无需旋转
                    _maxShortSide = shortSide > 0 ? shortSide : ScreenShareSettings.FallbackShortSide;
                    _capBr = bitrate;

                    int sw, sh, dpi;
                    GetScreenMetrics(out sw, out sh, out dpi);
                    if (sw <= 0 || sh <= 0) { LastError = L10n.T("无法读取屏幕尺寸"); return false; }
                    _capW = sw; _capH = sh; _screenDpi = dpi;

                    _running = true;
                    StartTime = DateTime.Now;

                    BuildLadder(sw, sh, bitrate, shortSide);
                    int w0, h0, br0;
                    CurrentRung(out w0, out h0, out br0);
                    Width = w0; Height = h0; Bitrate = br0;
                    InitStreamState();

                    // 1) 编码器（输入 Surface 直接给 VirtualDisplay 用）
                    if (!CreateEncoderSurface()) { Stop(); return false; }

                    // 2) 屏幕采集
                    if (!CreateProjection(resultCode, data))
                    {
                        Log.Warn("IpCam", "屏幕采集失败：" + (LastError ?? "未知"));
                        Stop();
                        return false;
                    }

                    // 3) 编码输出抽取线程（与摄像头模式共用同一套编码循环）
                    _encoderThread = new Thread(EncoderLoop) { IsBackground = true, Name = "cam-enc" };
                    _encoderThread.Start();

                    // 4) HTTP / 信标 / 自适应 / 旋转监听
                    StartHttp();
                    StartBeacon();
                    StartAbr();
                    StartDisplayWatcher();

                    Log.Warn("IpCam", "屏幕共享已启动：HTTP=" + LocalUrl + " 编码 " + w0 + "x" + h0 + "@" + Fps
                        + " br=" + br0 + L10n.T(" 屏幕=") + sw + "x" + sh);
                    Settings.LogEffective("Start(screen)");
                    // 【为什么】启动不等首帧、不等客户端：编码器 Start 后立刻尝试从 OutputFormat
                    // 拉 csd 构造 init 段；屏幕静止再由首帧看门狗主动喂帧（见 EncoderLoop）。
                    TryEnsureInitFromOutputFormat();
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    OnError?.Invoke(ex.Message);
                    Stop();
                    return false;
                }
            }
        }

        /// <summary>启动共享时统一复位流状态（分片 / 代次 / 自适应计数）。</summary>
        private void InitStreamState()
        {
            ResetStreams();
            // 新会话浏览器必须重新取 init 段；但 init 现在可从 OutputFormat 即时重建（见下），
            // epoch 变化不会再导致 /mse/init 返回 503 或长时间黑屏。
            _epoch++;
            _rungAt = DateTime.UtcNow.AddMilliseconds(_reconfigFloor);  // 启动静默期
            _abrScore = 0;
            _upDelaySec = 12;
            Interlocked.Exchange(ref _dropSignals, 0);
            Interlocked.Exchange(ref _stallSignals, 0);
            _rungSwitches = 0;
        }

        /* ===================== 首帧死结根治 / 设置应用 ===================== */

        /// <summary>
        /// 从编码器 OutputFormat 的 csd-0 / csd-1 直接取 SPS/PPS 并构造 init 段。
        /// 【为什么 · 首帧死结】旧逻辑只在编码输出线程吃到 BUFFER_FLAG_CODEC_CONFIG 或
        /// OutputFormatChanged 时才填 _spsAnnexB/_ppsAnnexB；屏幕静止时 VirtualDisplay 不产帧，
        /// 编码器被饿死、连 format change 都不触发，于是参数集永远为 null → /mse/init 回 503、
        /// 裸流也不发参数集 → 解码器永远配不上。configure/start 之后多数机型的 OutputFormat
        /// 已经带 csd，这里主动拉一次，把「init 可用」与「产出编码帧」彻底解耦
        /// （对齐 ScreenStream「缓存帧立刻可交付」）。
        /// </summary>
        private void TryEnsureInitFromOutputFormat()
        {
            try
            {
                var enc = _encoder;
                if (enc == null) return;
                MediaFormat f = enc.OutputFormat;
                if (f == null) return;
                byte[] csd0 = BbToArray(f.GetByteBuffer("csd-0"));
                byte[] csd1 = BbToArray(f.GetByteBuffer("csd-1"));
                lock (_segLock)
                {
                    if (csd0 != null) ExtractSpsPps(csd0, ref _spsAnnexB, ref _ppsAnnexB);
                    if (csd1 != null) ExtractSpsPps(csd1, ref _spsAnnexB, ref _ppsAnnexB);
                }
                BuildInitIfPossible();
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "从 OutputFormat 取参数集失败：" + ex.Message);
            }
        }

        /// <summary>参数集齐了就立刻构造 MSE init 段（不必等任何编码帧）。</summary>
        private void BuildInitIfPossible()
        {
            byte[] sps, pps;
            lock (_segLock)
            {
                if (_initMp4 != null) return;
                sps = _spsAnnexB; pps = _ppsAnnexB;
            }
            if (sps == null || pps == null) return;
            try
            {
                var init = Mp4Muxer.BuildInit(Width, Height, sps, pps, Fps);
                if (init != null)
                {
                    lock (_segLock) { if (_initMp4 == null) _initMp4 = init; }
                    Log.Warn("IpCam", "init 段已即时重建（不依赖编码帧）sps=" + sps.Length + " pps=" + pps.Length);
                }
            }
            catch (Exception ex) { Log.Warn("IpCam", "即时构造 init 段失败：" + ex.Message); }
        }

        /// <summary>
        /// 主动往编码器输入 Surface 画一帧，唤醒「屏幕静止 → 不产帧 → 编码器饿死」的链路。
        /// 【为什么】COLOR_FormatSurface 的输入 Surface 支持 lockCanvas 软件绘制，无需 GLES 管线；
        /// 画一帧后编码器会立刻吐出 config + IDR，参数集/init 段随之就绪。部分 ROM 不支持
        /// 在 MediaCodec 输入 Surface 上绘制，catch 后由首帧看门狗的请求关键帧 + 明确错误回报兜底。
        /// </summary>
        private void KickFirstFrame()
        {
            var s = _inputSurface;
            if (s == null) return;
            try
            {
                var canvas = s.LockCanvas(null);
                if (canvas == null) return;
                try { canvas.DrawColor(Android.Graphics.Color.Black); }
                finally { s.UnlockCanvasAndPost(canvas); }
                Log.Warn("IpCam", "已主动喂一帧，唤醒静止画面下的编码器");
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "主动喂帧失败（该机型不支持在编码输入 Surface 上绘制）：" + ex.Message);
            }
        }

        /// <summary>请求编码器立刻产一个同步帧（IDR）：看门狗喂帧失败时的兜底。</summary>
        private void RequestSyncFrameNow()
        {
            var enc = _encoder;
            if (enc == null) return;
            try
            {
                var b = new Bundle();
                b.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 1);
                enc.SetParameters(b);
            }
            catch { }
        }

        /// <summary>
        /// 编码器重建后给已连接的裸流查看端立刻补发 SPS/PPS。
        /// 【为什么 · ABR/旋转断流】旧逻辑重建后清掉 init/分片并 epoch++，但已经连上的
        /// /stream.h264 查看端不会重新注册，要等下一个 1s 关键帧才拿得到参数集，表现为突然断流。
        /// 注：/live.ts（TS 包）与 /frames（9B 头 + AVCC）封装不同，不能塞裸 Annex-B 参数集，
        /// 它们靠 X-Epoch 变化后自行重新初始化，这里不碰，避免协议错乱。
        /// </summary>
        private void ResendParameterSets()
        {
            byte[] sps = _spsAnnexB, pps = _ppsAnnexB;
            if (sps == null || pps == null) return;
            byte[] param = Cat(sps, pps);
            List<StreamViewer> snap;
            lock (_viewers) snap = new List<StreamViewer>(_viewers);
            for (int i = 0; i < snap.Count; i++)
            {
                try { snap[i].Enqueue(param, true); } catch { }
            }
            if (snap.Count > 0) Log.Warn("IpCam", "重建后已向 " + snap.Count + " 个裸流查看端补发 SPS/PPS");
        }

        /// <summary>
        /// 只把新编码器输入 Surface 交给现有 VirtualDisplay 并 Resize，绝不 Release + 重建。
        /// 【为什么 · 对齐 ScreenStream】设置/分辨率变更只换 Surface/Resize：
        /// MediaProjection 授权 token 一次性，重建 VD 既可能要求重新授权，也会让已连接客户端瞬断。
        /// 【注意 · Xamarin 绑定】Android.Views 没有 VirtualDisplay.SetSurface() 方法，
        /// Java 的 setSurface(Surface) 在本工程绑定里是可写属性 VirtualDisplay.Surface，
        /// 必须写 vd.Surface = x；写成 vd.SetSurface(x) 会直接编译不过（CS1061）。
        /// </summary>
        private bool RebindVirtualDisplaySurface()
        {
            var vd = _vd;
            if (vd == null || _inputSurface == null) return false;
            try
            {
                vd.Surface = _inputSurface;
                vd.Resize(Width, Height, ScaledDpi(Width));
                Log.Warn("IpCam", "虚拟显示已换 Surface/Resize（未重建）" + Width + "x" + Height);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "换 Surface/Resize 失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>集中设置变更回调：只改编码参数，绝不重建 VirtualDisplay（对齐 ScreenStream）。</summary>
        private void OnSettingsChanged(ScreenShareSettings s)
        {
            ApplyEncodingSettings();
        }

        /// <summary>
        /// 运行期应用集中设置（fps/quality 相关）：只改编码参数，绝不重建 VirtualDisplay。
        /// 【为什么】对齐 ScreenStream「设置变更只 Resize/SetSurface」；改 fps / jpegQuality
        /// 不该动采集源。H.264 编码器运行中能即时改的是码率/同步帧，帧率上限体现在节流与
        /// 分片时长里，这里请求一个关键帧让新参数尽快生效，不重建编码器、更不重建 VD。
        /// </summary>
        public void ApplyEncodingSettings()
        {
            if (!IsRunning) return;
            var s = Settings;
            // 只改标称帧率（int 原子写），不重建编码器/VD；不用 lock(_lock) 以免
            // Start/Stop 持锁期间阻塞触发设置变更的 UI 线程。
            Fps = s.ClampFps(s.MaxFps);
            RequestSyncFrameNow();
            s.LogEffective("ApplyEncodingSettings");
        }

        /// <summary>共享中切换画质上限（不重启采集源）。屏幕共享下必须走这里：
        /// MediaProjection 的授权 token 一次性有效，重启会要求用户重新授权。
        /// </summary>
        public void SetQuality(int shortSide)
        {
            lock (_lock)
            {
                _maxShortSide = shortSide;
                if (_capW <= 0 || _capH <= 0) return;
                BuildLadder(_capW, _capH, _capBr, shortSide);
            }
            if (!IsRunning) return;
            try { ApplyRung(); } catch (Exception ex) { Log.Warn("IpCam", "切换画质失败：" + ex.Message); }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running && _camDevice == null && _http == null) { /* still cleanup */ }
                _running = false;
                try { if (_settings != null) _settings.Changed -= OnSettingsChanged; } catch { }

                try { _beaconThread?.Interrupt(); } catch { }
                try { _beaconSock?.Close(); } catch { }
                _beaconSock = null;

                try { _httpThread?.Interrupt(); } catch { }
                try { _http?.Stop(); } catch { }
                _http = null;

                try { _abrThread?.Interrupt(); } catch { }
                _abrThread = null;

                // 关闭所有查看端（每个都有自己的发送线程，Close 会一起收掉）
                List<StreamViewer> all = new List<StreamViewer>();
                lock (_viewers) { all.AddRange(_viewers); _viewers.Clear(); }
                lock (_tsViewers) { all.AddRange(_tsViewers); _tsViewers.Clear(); }
                lock (_frameViewers) { all.AddRange(_frameViewers); _frameViewers.Clear(); }
                foreach (var v in all) { try { v.Close(); } catch { } }
                lock (_segLock)
                {
                    _segments.Clear();
                    _segSamples.Clear();
                    _initMp4 = null;
                    Monitor.PulseAll(_segLock);   // 叫醒等待分片的请求，让它们尽快返回
                }

                try { _encoderThread?.Interrupt(); } catch { }

                // 屏幕共享：先拆虚拟显示，再停投影（Stop 会触发 onStop 回调，callback 里会二次进 Stop，靠 _running 拦截）
                bool wasScreen = Source == ShareSource.Screen;
                try { _vd?.Release(); } catch { }
                _vd = null;
                var mp = _mp; var mpCb = _mpCb;
                _mp = null; _mpCb = null;
                if (mp != null)
                {
                    ThreadPool.QueueUserWorkItem(__ =>
                    {
                        try { if (mpCb != null) mp.UnregisterCallback(mpCb); } catch { }
                        try { mp.Stop(); } catch { }
                    });
                }
                try { if (_dm != null && _dispListener != null) _dm.UnregisterDisplayListener(_dispListener); } catch { }
                _dispListener = null; _dm = null;

                try { _camSession?.Close(); } catch { }
                _camSession = null;
                try { _camDevice?.Close(); } catch { }
                _camDevice = null;

                if (_encoder != null)
                {
                    try { _encoder.Stop(); } catch { }
                    try { _encoder.Release(); } catch { }
                    _encoder = null;
                }
                try { _inputSurface?.Release(); } catch { }
                _inputSurface = null;

                try { _camThread?.QuitSafely(); } catch { }
                _camThread = null;

                // 屏幕共享结束后，把承载 MediaProjection 的前台服务一起收掉（不阻塞 Stop 流程）
                if (wasScreen)
                {
                    var c = _ctx;
                    if (c != null)
                        ThreadPool.QueueUserWorkItem(__ => { try { c.StopService(new Intent(c, typeof(ScreenCastService))); } catch { } });
                }
            }
        }

        /* ===================== 屏幕共享（MediaProjection） ===================== */

        /// <summary>屏幕真实像素 + dpi（含状态栏/导航栏，随当前方向变化）。</summary>
        private void GetScreenMetrics(out int w, out int h, out int dpi)
        {
            w = h = 0; dpi = 320;

            // 首选 DisplayManager：拿到的是整块屏幕（含状态栏/导航栏）的真实像素
            try
            {
                var dmgr = _ctx.GetSystemService(Context.DisplayService) as DisplayManager;
                var disps = dmgr?.GetDisplays();
                var disp = (disps != null && disps.Length > 0) ? disps[0] : null;
                if (disp != null)
                {
                    var m = new Android.Util.DisplayMetrics();
                    disp.GetRealMetrics(m);
                    w = m.WidthPixels; h = m.HeightPixels;
                    dpi = (int)m.DensityDpi;
                }
            }
            catch (Exception ex) { Log.Warn("IpCam", "DisplayManager 取屏幕尺寸失败：" + ex.Message); }

            // 兜底：应用窗口的显示指标（少数机型/多屏场景下 GetDisplays 可能为空）
            if (w <= 0 || h <= 0)
            {
                try
                {
                    var m = _ctx.Resources.DisplayMetrics;
                    w = m.WidthPixels; h = m.HeightPixels;
                    dpi = (int)m.DensityDpi;
                    Log.Warn("IpCam", "退回 Resources 屏幕尺寸：" + w + "x" + h);
                }
                catch { }
            }

            if (dpi <= 0) dpi = 320;
            if (w > 0 && h > 0) Log.Warn("IpCam", "屏幕原始尺寸 " + w + "x" + h + " dpi=" + dpi);
        }

        private Handler GetCamHandler()
        {
            if (_camHandler != null) return _camHandler;
            if (_camThread == null)
            {
                _camThread = new HandlerThread("lancam-vd");
                _camThread.Start();
            }
            _camHandler = new Handler(_camThread.Looper);
            return _camHandler;
        }

        /// <summary>用用户授权结果创建 MediaProjection，并挂上"系统停止共享"回调。</summary>
        private bool CreateProjection(int resultCode, Intent data)
        {
            try
            {
                var mgr = _ctx.GetSystemService(Context.MediaProjectionService) as MediaProjectionManager;
                if (mgr == null) { LastError = "系统不支持屏幕录制"; return false; }
                _mp = mgr.GetMediaProjection(resultCode, data);
                if (_mp == null) { LastError = "屏幕录制授权无效（授权可能已过期，请重新开始共享）"; return false; }

                _mpCb = new ProjectionStopCallback(this);
                _mp.RegisterCallback(_mpCb, GetCamHandler());
                return CreateVirtualDisplay();
            }
            catch (Exception ex)
            {
                LastError = L10n.T("屏幕采集启动失败：") + ex.Message;
                Log.Warn("IpCam", LastError);
                return false;
            }
        }

        /// <summary>把当前编码器的输入 Surface 作为虚拟显示屏的输出（屏幕内容 → 编码器）。</summary>
        private bool CreateVirtualDisplay()
        {
            if (_mp == null || _inputSurface == null) return false;
            try
            {
                int flags = (int)VirtualDisplayFlags.AutoMirror;
                _vd = _mp.CreateVirtualDisplay("lancam-screen", Width, Height, ScaledDpi(Width),
                    (DisplayFlags)flags, _inputSurface, null, GetCamHandler());
                if (_vd == null) { LastError = "创建虚拟显示屏失败"; return false; }
                Log.Warn("IpCam", "虚拟显示已创建 " + Width + "x" + Height + " dpi=" + ScaledDpi(Width));
                return true;
            }
            catch (Exception ex)
            {
                LastError = L10n.T("创建虚拟显示屏失败：") + ex.Message;
                Log.Warn("IpCam", LastError);
                return false;
            }
        }

        private int ScaledDpi(int w)
        {
            if (_capW <= 0 || _screenDpi <= 0) return 320;
            int d = (int)(_screenDpi * (double)w / _capW);
            return d < 120 ? 120 : d;
        }

        private void StartDisplayWatcher()
        {
            try
            {
                _dm = _ctx.GetSystemService(Context.DisplayService) as DisplayManager;
                if (_dm == null) return;
                _dispListener = new ScreenDisplayListener(this);
                _dm.RegisterDisplayListener(_dispListener, GetCamHandler());
            }
            catch (Exception ex) { Log.Warn("IpCam", "显示监听失败：" + ex.Message); }
        }

        /// <summary>屏幕方向变化（UI 回调线程）：防抖后重建，避免旋转动画期间反复拆装编码器。</summary>
        internal void OnDisplayChanged(int displayId)
        {
            if (!_running || Source != ShareSource.Screen) return;
            int now = System.Environment.TickCount;
            lock (_lock)
            {
                if (unchecked(now - _dispTick) < 800) return;
                _dispTick = now;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Thread.Sleep(900); } catch { }
                try { RefreshScreenGeometry(); } catch (Exception ex) { Log.Warn("IpCam", "旋转重建失败：" + ex.Message); }
            });
        }

        /// <summary>屏幕尺寸变了（横竖屏切换）：重算阶梯并把编码器/虚拟显示按新方向重建。</summary>
        private void RefreshScreenGeometry()
        {
            if (!_running || Source != ShareSource.Screen) return;
            int sw, sh, dpi;
            GetScreenMetrics(out sw, out sh, out dpi);
            if (sw <= 0 || sh <= 0) return;
            if (sw == _capW && sh == _capH) return;

            Log.Warn("IpCam", "屏幕方向变化 " + _capW + "x" + _capH + " → " + sw + "x" + sh + "，重建编码");
            lock (_lock)
            {
                _capW = sw; _capH = sh; _screenDpi = dpi;
                BuildLadder(sw, sh, _capBr, _maxShortSide);
            }
            try { ApplyRung(); } catch (Exception ex) { Log.Warn("IpCam", "旋转后应用新档位失败：" + ex.Message); }
        }

        /// <summary>系统侧停止共享（用户点通知栏"停止共享"/权限被收回）。</summary>
        internal void OnProjectionStopped()
        {
            if (!_running || Source != ShareSource.Screen) return;
            Log.Warn("IpCam", "系统已停止屏幕录制，关闭共享");
            ThreadPool.QueueUserWorkItem(_ => { try { Stop(); } catch { } });
        }

        /* ===================== 摄像头 + 硬编码 ===================== */

        private bool OpenCameraAndEncoder()
        {
            var cm = (CameraManager)_ctx.GetSystemService(Context.CameraService);
            if (cm == null) { LastError = "无法获取 CameraManager"; return false; }

            string camId = PickCamera(cm, Front);
            if (camId == null) { LastError = Front ? "未发现前置摄像头" : "未发现后置摄像头"; return false; }

            // 编码器
            if (!CreateEncoderSurface()) return false;

            // 摄像头线程
            _camThread = new HandlerThread("cam-server");
            _camThread.Start();
            _camHandler = new Handler(_camThread.Looper);

            bool opened = false;
            var are = new ManualResetEvent(false);
            var cb = new CameraStateCallback(
                device =>
                {
                    _camDevice = device;
                    Log.Warn("IpCam", "相机已打开，准备创建会话");
                    CreateSession();
                    opened = true;
                    are.Set();
                },
                () => { LastError = L10n.T("摄像头已断开"); are.Set(); },
                err => { LastError = L10n.T("摄像头打开错误：") + err; are.Set(); });

            try
            {
                cm.OpenCamera(camId, cb, _camHandler);
            }
            catch (Exception ex) { LastError = L10n.T("打开摄像头失败：") + ex.Message; return false; }

            if (!are.WaitOne(8000)) { LastError = "摄像头打开超时"; Log.Warn("IpCam", "摄像头打开超时"); return false; }
            if (!opened) return false;

            // 【为什么 · 假启动根治】CreateSession() 是在相机 OnOpened 回调线程里调
            // CreateSessionInternal(0)，那边不能阻塞等待（会与同 Handler 的会话回调死锁），
            // 旧代码因此无条件 return true，配置失败也报成功。真实结果由
            // _sessionReady/_sessionStarted/_sessionFailed 传出来，在这里（非回调线程）
            // 等待并判定，失败还能重试一次，仍失败就明确 return false 触发上层重试/报错。
            int sessionWait = Math.Max(2500, Settings.FirstFrameTimeoutMs);
            var ready = _sessionReady;
            if (ready != null && !ready.WaitOne(sessionWait))
                Log.Warn("IpCam", "等待采集会话配置超时（仍按真实结果判定）");
            if (_sessionStarted == 0 || _sessionFailed != 0)
            {
                Log.Warn("IpCam", "采集会话首次配置失败：" + (LastError ?? "OnConfigureFailed") + "，重试一次");
                try { _camSession?.Close(); } catch { }
                _camSession = null;
                _sessionFailed = 0;
                _sessionStarted = 0;
                _sessionReady = new ManualResetEvent(false);
                CreateSessionInternal(0);
                ready = _sessionReady;
                if (ready != null) ready.WaitOne(sessionWait);
            }
            if (_sessionStarted == 0 || _sessionFailed != 0)
            {
                LastError = L10n.T("采集会话配置失败：") + (LastError ?? "OnConfigureFailed");
                Log.Warn("IpCam", LastError);
                OnError?.Invoke(LastError);
                return false;   // 真失败：Start() 会 Stop() 并把原因回报给界面
            }

            // 启动编码输出抽取线程
            _encoderThread = new Thread(EncoderLoop) { IsBackground = true };
            _encoderThread.Start();
            return true;
        }

        /// <summary>按当前 Width/Height/Bitrate 创建编码器 + 输入 Surface（不动摄像头设备）。</summary>
        private bool CreateEncoderSurface()
        {
            MediaCodec enc = null;
            try
            {
                enc = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
            }
            catch (Exception ex) { LastError = L10n.T("创建编码器失败：") + ex.Message; return false; }

            var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, Width, Height);
            format.SetInteger(MediaFormat.KeyColorFormat, ColorFormatSurface);
            format.SetInteger("bitrate", Bitrate);
            format.SetInteger(MediaFormat.KeyFrameRate, Fps);
            format.SetInteger(MediaFormat.KeyCaptureRate, Fps);
            // 关键帧间隔改读集中设置（参考 ScreenStream keyFrame=1s）：短间隔让新查看端 /
            // 丢帧后快速恢复，配合 init 段即时重建避免断流。
            format.SetInteger(MediaFormat.KeyIFrameInterval, Settings.KeyFrameIntervalSec);
            format.SetInteger(MediaFormat.KeyBitrateMode, 2);    // CBR
            try { format.SetInteger("low-latency", 1); } catch { }
            try { format.SetInteger(MediaFormat.KeyProfile, 1); } catch { }       // Baseline
            try { format.SetInteger(MediaFormat.KeyLevel, 0x1000); } catch { }    // Level 4.1
            if (Source == ShareSource.Screen)
            {
                // 屏幕静止时 VirtualDisplay 不产新帧，编码器会"饿死"：画面停住、时间轴不走。
                // 这个 key 在多数机型上并不生效，且旧代码静默 catch 完全看不到失败；
                // 只作为尽力而为，真正兜底靠 EncoderLoop 的首帧看门狗主动喂一帧（见下）。
                try { format.SetLong(MediaFormat.KeyRepeatPreviousFrameAfter, 1000000L / Math.Max(1, Fps)); }
                catch (Exception ex) { Log.Warn("IpCam", "本机不支持自动重复上一帧：" + ex.Message); }
            }

            bool configured = false;
            try
            {
                enc.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                configured = true;
            }
            catch (Exception ex)
            {
                // 某些设备不接受显式 profile/level，去掉再试
                try
                {
                    format.SetInteger(MediaFormat.KeyProfile, 0);
                    format.SetInteger(MediaFormat.KeyLevel, 0);
                    enc.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                    configured = true;
                }
                catch (Exception ex2) { LastError = L10n.T("编码器配置失败：") + ex.Message + " / " + ex2.Message; }
            }
            if (!configured) { try { enc.Release(); } catch { } return false; }

            try
            {
                var surface = enc.CreateInputSurface();
                enc.Start();
                _encoder = enc;
                _inputSurface = surface;
                // 【为什么】编码器 Start 后立刻从 OutputFormat 取 csd 构造 init 段，
                // 不等第一帧、不等客户端连接（对齐 ScreenStream「快出画面」），
                // 也把「init 可用」与「产出编码帧」解耦，根治首帧死结。
                TryEnsureInitFromOutputFormat();
                return true;
            }
            catch (Exception ex)
            {
                LastError = L10n.T("编码器启动失败：") + ex.Message;
                try { enc.Release(); } catch { }
                return false;
            }
        }

        private void CreateSession()
        {
            CreateSessionInternal(0);
        }

        /// <summary>
        /// 创建采集会话。waitMs &gt; 0 时同步等待配置完成——只允许在"非相机回调线程"里调用，
        /// 否则会与投递到 _camHandler 的会话回调互相等待而死锁。
        /// </summary>
        private bool CreateSessionInternal(int waitMs)
        {
            if (_camDevice == null || _inputSurface == null) return false;

            var surface = _inputSurface;
            var done = new ManualResetEvent(false);
            // 【为什么】真实结果写到成员字段里，供 OpenCameraAndEncoder 在「非回调线程」
            // 等待并判定；旧的局部 started 只服务于 waitMs>0 的同步路径。
            var ready = _sessionReady;
            if (ready != null) ready.Reset();
            bool started = false;
            _sessionStarted = 0;
            _sessionFailed = 0;

            try
            {
                var outputs = new List<Surface> { surface };
                var cb = new SessionStateCallback(
                    session =>
                    {
                        _camSession = session;
                        try
                        {
                            var builder = _camDevice.CreateCaptureRequest(CameraTemplate.Record);
                            builder.AddTarget(surface);
                            session.SetRepeatingRequest(builder.Build(), null, _camHandler);
                            started = true;
                            _sessionStarted = 1;   // 真实配置成功
                            Log.Warn("IpCam", "会话已配置，开始向编码器推送帧 " + Width + "x" + Height + "@" + Bitrate);
                        }
                        catch (Exception ex)
                        {
                            // SetRepeatingRequest 失败也算失败，不能让上层以为起来了。
                            started = false;
                            _sessionStarted = 0;
                            _sessionFailed = 1;
                            LastError = L10n.T("开始预览失败：") + ex.Message;
                            OnError?.Invoke(LastError);
                            Log.Warn("IpCam", "开始预览失败：" + ex.Message);
                        }
                        finally { done.Set(); if (ready != null) ready.Set(); }
                    },
                    session =>
                    {
                        // 【为什么 · 假启动根治】OnConfigureFailed 必须置失败状态。
                        // 旧实现只写日志，而 waitMs==0 分支无条件 return true，
                        // 上层完全不知道失败，于是「摄像头/屏幕半天出不来画面」。
                        started = false;
                        _sessionStarted = 0;
                        _sessionFailed = 1;
                        LastError = L10n.T("摄像头会话配置失败（OnConfigureFailed）");
                        OnError?.Invoke(LastError);
                        Log.Warn("IpCam", LastError);
                        done.Set();
                        if (ready != null) ready.Set();
                    });
                _camDevice.CreateCaptureSession(outputs, cb, _camHandler);
            }
            catch (Exception ex)
            {
                started = false;
                _sessionStarted = 0;
                _sessionFailed = 1;
                LastError = L10n.T("创建会话失败：") + ex.Message;
                OnError?.Invoke(LastError);
                Log.Warn("IpCam", LastError);
                done.Set();
                if (ready != null) ready.Set();
            }

            // waitMs>0：同步等真实结果（仅用于非相机回调线程，如 RebuildPipeline）。
            if (waitMs > 0) return done.WaitOne(waitMs) && started;
            // waitMs==0：在相机 OnOpened 回调线程里发起，绝不能阻塞等待（会与投递到同一
            // Handler 的会话回调互相等死锁）。返回 true 只代表「已发起」，真实成败由
            // _sessionStarted/_sessionFailed 交给 OpenCameraAndEncoder 判定，不再是假成功。
            return true;
        }

        private string PickCamera(CameraManager cm, bool front)
        {
            try
            {
                var ids = cm.GetCameraIdList();
                var want = front ? (int)LensFacing.Front : (int)LensFacing.Back;
                foreach (var id in ids)
                {
                    var ch = cm.GetCameraCharacteristics(id);
                    var lf = ch.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer;
                    if (lf != null && lf.IntValue() == want) return id;
                }
                // 退回：任意可用
                foreach (var id in ids)
                {
                    var ch = cm.GetCameraCharacteristics(id);
                    var lf = ch.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer;
                    if (lf != null && lf.IntValue() != (int)LensFacing.External) return id;
                }
                return ids.Length > 0 ? ids[0] : null;
            }
            catch { return null; }
        }

        private int ComputeRotation(bool front)
        {
            try
            {
                var wm = _ctx.GetSystemService(Context.WindowService) as Android.Views.IWindowManager;
                int deg = 0;
                if (wm != null)
                {
                    var rot = wm.DefaultDisplay.Rotation;
                    if (rot == SurfaceOrientation.Rotation0) deg = 0;
                    else if (rot == SurfaceOrientation.Rotation90) deg = 90;
                    else if (rot == SurfaceOrientation.Rotation180) deg = 180;
                    else if (rot == SurfaceOrientation.Rotation270) deg = 270;
                }
                int sensor = 90; // 多数手机后置传感器 90°，无法在配置前取特征时给默认值
                try
                {
                    var cm = (CameraManager)_ctx.GetSystemService(Context.CameraService);
                    var id = PickCamera(cm, front);
                    if (id != null)
                    {
                        var ch = cm.GetCameraCharacteristics(id);
                        var so = ch.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer;
                        if (so != null) sensor = so.IntValue();
                    }
                }
                catch { }
                int r = (sensor - deg + 360) % 360;
                if (front) r = (360 - r) % 360; // 前置镜像
                return r;
            }
            catch { return 90; }
        }

        private void EncoderLoop()
        {
            var info = new MediaCodec.BufferInfo();
            long frameCount = 0;
            // 首帧看门狗：屏幕静止时 VirtualDisplay 不产帧、KEY_REPEAT_PREVIOUS_FRAME_AFTER
            // 多数机型不生效，编码器会被饿死而永远不吐首帧。启动后超时就主动喂一帧。
            long loopStartTick = System.Environment.TickCount;
            int firstFrameTimeoutMs = Settings.FirstFrameTimeoutMs;
            int kicks = 0;
            Log.Warn("IpCam", "编码输出线程启动，等待首帧…");
            try
            {
                while (_running && _encoder != null)
                {
                    int idx;
                    try { idx = _encoder.DequeueOutputBuffer(info, 10000); }
                    catch (Java.Lang.IllegalStateException) { break; }
                    catch (Exception) { break; }

                    // 主动加密关键帧：编码器默认 1s 一个 IDR，导致 MSE 分片粒度也是 1s。
                    // 每 ~450ms 请求一次同步帧，分片缩到 0.5s，网页延迟直接砍半。
                    if (_running && System.Environment.TickCount - _lastKeyReqAt > 450)
                    {
                        _lastKeyReqAt = System.Environment.TickCount;
                        try
                        {
                            var prm = new Bundle();
                            prm.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 1);
                            _encoder.SetParameters(prm);
                        }
                        catch { }
                    }

                    if (idx >= 0)
                    {
                        // 单帧处理必须隔离异常：任何一帧解析异常都不允许打死整个编码线程
                        try
                        {
                            var bb = _encoder.GetOutputBuffer(idx);
                            bool isConfig = (info.Flags & MediaCodecBufferFlags.CodecConfig) != 0;
                            bool isKey = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;

                            if (isConfig && bb != null)
                            {
                                byte[] cfg = BbToArray(bb, info.Offset, info.Size);
                                Log.Warn("IpCam", "编码器 config len=" + (cfg == null ? -1 : cfg.Length) + " head=" + HexHead(cfg, 8));
                                ExtractSpsPps(cfg, ref _spsAnnexB, ref _ppsAnnexB);
                                Log.Warn("IpCam", "SPS/PPS 状态：sps=" + (_spsAnnexB == null ? 0 : _spsAnnexB.Length)
                                    + " pps=" + (_ppsAnnexB == null ? 0 : _ppsAnnexB.Length));
                                // config 帧一到就构造 init 段，不必等后面的 IDR。
                                BuildInitIfPossible();
                            }
                            else if (bb != null && info.Size > 0)
                            {
                                byte[] frame = BbToArray(bb, info.Offset, info.Size);
                                if (frame != null && frame.Length > 0)
                                {
                                    // 拆成 NAL 列表：兼容"4 字节长度前缀"与"已是 Annex-B"两种编码器输出。
                                    // 编码器输出格式整个会话恒定，首帧判定后缓存，避免每帧全缓冲扫描。
                                    if (_annexBFormat == null) _annexBFormat = LooksLikeAnnexB(frame);
                                    List<byte[]> nals = _annexBFormat.Value ? SplitAnnexB(frame) : SplitLengthPrefixed(frame);
                                    if (nals.Count > 0)
                                    {
                                        byte[] annexb = ToAnnexB(nals);
                                        byte[] avcc = ToAvcc(nals);
                                        byte[] outp = annexb;
                                        if (isKey && _spsAnnexB != null && _ppsAnnexB != null)
                                        {
                                            // 关键帧前重发 SPS/PPS，保证新加入的查看端能立即解码
                                            outp = Cat(_spsAnnexB, _ppsAnnexB, annexb);
                                        }
                                        BroadcastRaw(outp, isKey);  // 裸 H.264（视频/h264）
                                        BroadcastTs(outp, isKey);   // MPEG-TS（PotPlayer / VLC）
                                        AddMp4Sample(avcc, isKey);  // 浏览器 MSE 分片 MP4
                                        BroadcastFrames(avcc, isKey);  // 浏览器 WebCodecs 低延迟通道
                                        frameCount++;
                                        _firstFrameSeen = 1;   // 首帧看门狗：已产帧，停止喂帧
                                        if (frameCount == 1) Log.Warn("IpCam", "首帧已广播 isKey=" + isKey + " len=" + annexb.Length + " viewers=" + ViewerCount);
                                        else if (frameCount % 120 == 0) Log.Warn("IpCam", "已广播帧数=" + frameCount + " viewers=" + ViewerCount);
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Log.Warn("IpCam", "处理编码输出异常：" + ex.Message); }
                        finally { try { _encoder.ReleaseOutputBuffer(idx, false); } catch { } }
                    }
                    else if ((MediaCodecInfoState)idx == MediaCodecInfoState.OutputFormatChanged)
                    {
                        // 【为什么 · 首帧死结根治】format 一变就主动从 csd-0/csd-1 取参数集
                        // 并构造 init 段，不再只写日志等编码帧；把「init 可用」与产帧解耦。
                        TryEnsureInitFromOutputFormat();
                    }

                    // 【为什么 · 首帧看门狗】FirstFrameTimeoutMs 内没有任何编码输出，说明
                    // VirtualDisplay 静止把编码器饿死了。主动喂一帧唤醒编码器，别让
                    // /mse/init 与裸流永远等参数集（首帧死结的主动兜底）。
                    if (_running && frameCount == 0 && kicks < 3
                        && unchecked(System.Environment.TickCount - loopStartTick) >= firstFrameTimeoutMs)
                    {
                        kicks++;
                        loopStartTick = System.Environment.TickCount;
                        Log.Warn("IpCam", "首帧看门狗超时，主动喂第 " + kicks + " 帧");
                        TryEnsureInitFromOutputFormat();
                        KickFirstFrame();
                        RequestSyncFrameNow();
                    }
                    // 喂帧上限用尽仍无首帧：明确回报，避免链路永远静默等待。
                    // 不在本线程内重建编码器 —— RebuildPipeline 会 Join 当前编码线程而自锁。
                    if (_running && frameCount == 0 && kicks >= 3
                        && unchecked(System.Environment.TickCount - loopStartTick) >= firstFrameTimeoutMs)
                    {
                        loopStartTick = System.Environment.TickCount;
                        Log.Warn("IpCam", "首帧看门狗：喂帧后仍无编码输出，回报错误供上层重试");
                        OnError?.Invoke(L10n.T("编码器长时间无输出（首帧看门狗）"));
                    }
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex) { Log.Warn("IpCam", "编码线程异常：" + ex.Message); }
        }

        /// <summary>
        /// 裸 H.264 广播：只做非阻塞入队，绝不在这里写 socket。
        /// （以前在这里同步写，一个慢客户端就把编码线程卡死，导致所有人一起掉线）
        /// </summary>
        private void BroadcastRaw(byte[] data, bool isKey)
        {
            if (data == null || data.Length == 0) return;
            List<StreamViewer> snap;
            lock (_viewers) snap = new List<StreamViewer>(_viewers);
            for (int i = 0; i < snap.Count; i++) snap[i].Enqueue(data, isKey);
        }

        /// <summary>
        /// WebCodecs 低延迟通道：先把解码参数（codec 串 + SPS/PPS）作为一行 JSON 发过去，
        /// 之后按 [4字节长度][1字节关键帧标记][4字节时间戳][AVCC 数据] 逐帧推。
        /// 浏览器侧用 VideoDecoder 直接解码到 canvas，不经过 MSE 的分片缓冲。
        /// </summary>
        private bool HandleFrameChannel(TcpClient client, System.IO.Stream ns, out StreamViewer viewer)
        {
            viewer = null;
            int n;
            lock (_frameViewers) n = _frameViewers.Count;
            if (n >= MaxFrameViewers)
            {
                WriteText(ns, 503, "too many viewers");
                return false;
            }

            // 等第一帧的 SPS/PPS（刚开共享时可能还没出来），超时读集中设置。
            var t0 = System.Environment.TickCount;
            int waitMs = Settings.FirstFrameTimeoutMs;
            bool kicked = false;
            while (_running && (_spsAnnexB == null || _ppsAnnexB == null)
                   && unchecked(System.Environment.TickCount - t0) < waitMs)
            {
                // 【为什么】主动从 OutputFormat 拉参数集 + 喂一帧，不再干等编码帧（首帧死结）。
                TryEnsureInitFromOutputFormat();
                if (!kicked && unchecked(System.Environment.TickCount - t0) > waitMs / 3)
                {
                    kicked = true;
                    KickFirstFrame();
                }
                Thread.Sleep(30);
            }
            if (_spsAnnexB == null || _ppsAnnexB == null)
            {
                Log.Warn("IpCam", "WebCodecs 通道：参数集超时未就绪，返回 not ready");
                WriteText(ns, 503, "not ready");
                return false;
            }

            try
            {
                string head = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n"
                            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                ns.Write(hb, 0, hb.Length);

                string init = "{\"codec\":\"" + CodecString() + "\""
                            + ",\"sps\":\"" + Convert.ToBase64String(StripStartCode(_spsAnnexB)) + "\""
                            + ",\"pps\":\"" + Convert.ToBase64String(StripStartCode(_ppsAnnexB)) + "\""
                            + ",\"w\":" + Width + ",\"h\":" + Height + ",\"fps\":" + Fps + "}\n";
                var ib = Encoding.UTF8.GetBytes(init);
                ns.Write(ib, 0, ib.Length);
                ns.Flush();

                // 队列浅一点（3）：低延迟优先，拥塞时宁可丢帧也不堆积
                var v = new StreamViewer(client, ns, 3, 2L * 1024 * 1024, RemoteOf(client));
                v.OnCongestion = OnViewerCongestion;
                // 从下一个关键帧开始发：浏览器 VideoDecoder 没有参考帧会直接报错
                v.AwaitKey = true;
                v.Start();
                lock (_frameViewers) _frameViewers.Add(v);
                viewer = v;
                Log.Warn("IpCam", "WebCodecs 接入：" + RemoteOf(client) + " frames 连接数=" + FrameViewerCount);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "WebCodecs 通道建立失败：" + ex.Message);
                try { client.Close(); } catch { }
                return false;
            }
        }

        public int FrameViewerCount { get { lock (_frameViewers) return _frameViewers.Count; } }

        private void BroadcastFrames(byte[] avcc, bool isKey)
        {
            if (avcc == null || avcc.Length == 0) return;
            List<StreamViewer> snap;
            lock (_frameViewers)
            {
                if (_frameViewers.Count == 0) return;
                snap = new List<StreamViewer>(_frameViewers);
            }

            int ts = System.Environment.TickCount;
            var pkt = new byte[9 + avcc.Length];
            pkt[0] = (byte)(avcc.Length >> 24);
            pkt[1] = (byte)(avcc.Length >> 16);
            pkt[2] = (byte)(avcc.Length >> 8);
            pkt[3] = (byte)avcc.Length;
            pkt[4] = (byte)(isKey ? 1 : 0);
            pkt[5] = (byte)(ts >> 24);
            pkt[6] = (byte)(ts >> 16);
            pkt[7] = (byte)(ts >> 8);
            pkt[8] = (byte)ts;
            Buffer.BlockCopy(avcc, 0, pkt, 9, avcc.Length);

            for (int i = 0; i < snap.Count; i++)
            {
                try { snap[i].Enqueue(pkt, isKey); } catch { }
            }
        }

        /// <summary>去掉 Annex-B 起始码（WebCodecs 的 description 只认裸 NAL）。</summary>
        private static byte[] StripStartCode(byte[] nal)
        {
            if (nal == null) return null;
            int off = 0;
            if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) off = 4;
            else if (nal.Length > 2 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) off = 3;
            if (off == 0) return nal;
            var r = new byte[nal.Length - off];
            Buffer.BlockCopy(nal, off, r, 0, r.Length);
            return r;
        }

        private void OnViewerCongestion(StreamViewer v)
        {
            Interlocked.Increment(ref _dropSignals);
        }

        private StreamViewer RegisterViewer(TcpClient client, System.IO.Stream s, string remote)
        {
            var v = new StreamViewer(client, s, 12, 3L * 1024 * 1024, remote);
            v.OnCongestion = OnViewerCongestion;
            // 连入即先发当前 SPS/PPS，保证尽快出图（按"关键帧"入队）
            if (_spsAnnexB != null && _ppsAnnexB != null)
            {
                try { v.Enqueue(Cat(_spsAnnexB, _ppsAnnexB), true); } catch { }
            }
            v.Start();
            lock (_viewers) _viewers.Add(v);
            return v;
        }

        /// <summary>一段 fMP4 媒体分片（moof+mdat），供 /mse/seg 按序号取用。</summary>
        private sealed class Mp4Segment
        {
            public uint Index;
            public byte[] Data;
        }

        /* ===================== HTTP 流服务 ===================== */

        private void StartHttp()
        {
            // 统一端口模式：所有 HTTP 服务由 UnifiedGateway 在同一个端口上按路径分流，
            // 这里不再自己监听（否则两个 listener 抢端口）。网关会把 /cam/... 的连接
            // 原样交回 ServeClient()，路由行为与以前完全一致。
            if (WebServer.IsRunning)
            {
                UsedHttpPort = WebServer.ExternalPortActual;
                _http = null;
                return;
            }

            UsedHttpPort = HttpPort;
            for (int p = HttpPort; p < HttpPort + 10; p++)
            {
                try
                {
                    _http = new TcpListener(IPAddress.Any, p);
                    _http.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _http.Start();
                    UsedHttpPort = p;
                    break;
                }
                catch
                {
                    try { _http?.Stop(); } catch { }
                    _http = null;
                }
            }
            if (_http == null) { LastError = "HTTP 端口占用"; throw new Exception("HTTP 端口占用"); }

            _httpThread = new Thread(HttpLoop) { IsBackground = true };
            _httpThread.Start();
        }

        private void HttpLoop()
        {
            try
            {
                // 注意：这里不能依赖 _running，避免启动时序导致线程启动后立即退出、留下"能连上但无人应答"的监听端口
                while (_http != null)
                {
                    TcpClient client;
                    try { client = _http.AcceptTcpClient(); }
                    catch (ThreadInterruptedException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { break; }
                    if (client == null) continue;
                    try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                    ThreadPool.QueueUserWorkItem(_ => HandleViewer(client, client.GetStream()));
                }
            }
            catch (ThreadInterruptedException) { }
            catch { }
        }

        /// <summary>由统一网关转交进来的连接：请求头尚未读取，与自监听时行为一致。</summary>
        public void ServeClient(TcpClient client)
        {
            HandleViewer(client, client.GetStream());
        }

        /// <summary>HTTPS 入口：SSL 握手后复用同一套路由（Stream 可能是 SslStream）。</summary>
        public void ServeClientSecure(TcpClient client, System.IO.Stream ns)
        {
            HandleViewer(client, ns);
        }

        private void HandleViewer(TcpClient client, System.IO.Stream ns)
        {
            StreamViewer viewer = null;
            bool isTs = false;
            bool isFrame = false;
            try
            {
                client.ReceiveTimeout = 5000;   // 等请求头，避免空连接长期占线程
                client.SendTimeout = 6000;      // 单次写超时；超时只丢帧不断线（见 StreamViewer）
                try { client.NoDelay = true; } catch { }
                try { client.SendBufferSize = 256 * 1024; } catch { }  // 吸收抖动，减少写阻塞
                string path;
                ReadHeaders(ns, out path);
                client.ReceiveTimeout = 0;

                string route = path ?? "/";
                string query = "";
                int qi = route.IndexOf('?');
                if (qi >= 0) { query = route.Substring(qi + 1); route = route.Substring(0, qi); }
                route = route.ToLowerInvariant();

                // 统一端口下路径带 /cam 前缀（http://ip:7790/cam/），内部仍按原来的相对路由判断；
                // 旧的直连方式（http://ip:7790/）也继续支持。
                if (route == "/cam") route = "/";
                else if (route.StartsWith("/cam/")) route = route.Substring(4);

                // 页面也可能以 /player 或 /index.html 结尾（旧书签 / 页面内的相对地址）：
                // 统一折叠成根，否则 /player/mse/init.mp4 会掉进「裸 H.264」兜底分支，画面永远出不来。
                if (route == "/player" || route == "/index.html") route = "/";
                else if (route.StartsWith("/player/")) route = route.Substring(7);
                if (route.EndsWith("/index.html", StringComparison.Ordinal)) route = "/";

                // 1) 网页播放器
                if (route == "" || route == "/" || route == "/index.html" || route == "/player")
                {
                    WriteSimple(ns, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LoadHtml()));
                    return;
                }

                // 2) 元数据
                if (route.Contains("/info"))
                {
                    WriteSimple(ns, "application/json", Encoding.UTF8.GetBytes(BuildInfoJson()));
                    return;
                }

                // 2b) 浏览器上报卡顿 → 自适应降档信号；down/up 供手动验证切档链路
                if (route.StartsWith("/abr"))
                {
                    if (query.IndexOf("stall=1", StringComparison.Ordinal) >= 0)
                        Interlocked.Increment(ref _stallSignals);
                    if (query.IndexOf("down=1", StringComparison.Ordinal) >= 0)
                        ThreadPool.QueueUserWorkItem(_ => StepRung(+1, L10n.T("手动测试")));
                    if (query.IndexOf("up=1", StringComparison.Ordinal) >= 0)
                        ThreadPool.QueueUserWorkItem(_ => StepRung(-1, L10n.T("手动测试")));
                    WriteSimple(ns, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"));
                    return;
                }

                // 3) MSE 初始化段（ftyp + moov）。带 X-Epoch：页面据此判断编码参数是否换过。
                if (route.StartsWith("/mse/init"))
                {
                    var init = GetInitMp4();
                    if (init == null)
                    {
                        // 【为什么】仍拿不到参数集：主动喂一帧唤醒编码器再试一次，
                        // 避免直接 503 让网页反复空转（首帧死结的最后一道兜底）。
                        Log.Warn("IpCam", "/mse/init 参数集未就绪，主动喂帧后重试");
                        KickFirstFrame();
                        TryEnsureInitFromOutputFormat();
                        init = GetInitMp4();
                    }
                    if (init == null) { Log.Warn("IpCam", "/mse/init 仍无参数集，返回 503"); WriteText(ns, 503, "init not ready"); return; }
                    int ep;
                    lock (_segLock) ep = _epoch;
                    WriteSimple(ns, "video/mp4", init, "X-Epoch: " + ep + "\r\n");
                    return;
                }

                // 4) MSE 媒体分片
                if (route.StartsWith("/mse/seg"))
                {
                    HandleSegment(ns, query);
                    return;
                }

                // 4b) WebCodecs 低延迟帧通道：不走 MSE 分片，逐帧直推，端到端约 100~200ms。
                //     注意：三个流式分支必须是互斥的 else-if——否则同一个 socket 会被注册两次，
                //     两个发送线程互相踩，报 "Cannot access a disposed object" 并立刻掉线。
                if (route.StartsWith("/frames") || route.StartsWith("/wc"))
                {
                    StreamViewer fv;
                    if (HandleFrameChannel(client, ns, out fv))
                    {
                        viewer = fv;
                        isFrame = true;
                    }
                    else return;   // 没建成，finally 里 GracefulClose 收尾
                }
                // 5) MPEG-TS（PotPlayer / VLC / ffplay）
                else if (route.StartsWith("/live.ts") || route.EndsWith(".ts") || route.Contains("/ts"))
                {
                    string tsHead = "HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nCache-Control: no-cache\r\n"
                        + "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    ns.Write(Encoding.ASCII.GetBytes(tsHead), 0, tsHead.Length);
                    ns.Flush();

                    viewer = new StreamViewer(client, ns, 12, 3L * 1024 * 1024, RemoteOf(client));
                    viewer.OnCongestion = OnViewerCongestion;
                    lock (_tsLock)
                    {
                        try { viewer.Enqueue(_ts.BuildTables(), true); } catch { }
                    }
                    viewer.Start();
                    lock (_tsViewers) _tsViewers.Add(viewer);
                    isTs = true;
                    Log.Warn("IpCam", "TS 播放器接入：" + RemoteOf(client) + " 当前查看数=" + ViewerCount);
                }
                // 6) 其它路径一律当裸 H.264 流
                else
                {
                    // 6) 其它路径一律当裸 H.264 流（stream.h264 / live.h264 / 根以外的旧链接）
                    string resp = "HTTP/1.1 200 OK\r\nContent-Type: video/h264\r\nCache-Control: no-cache\r\n"
                        + "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    ns.Write(Encoding.ASCII.GetBytes(resp), 0, resp.Length);
                    ns.Flush();
                    Log.Warn("IpCam", "查看端接入：" + RemoteOf(client) + " 当前查看数=" + (ViewerCount + 1));
                    viewer = RegisterViewer(client, ns, RemoteOf(client));
                }

                // 流式连接：等对端断开（或发送线程判定断开）
                while (_running && viewer != null && !viewer.IsClosed)
                {
                    viewer.WaitClosed(500);
                    if (viewer.IsClosed) break;
                    try
                    {
                        var sk = client.Client;
                        if (sk == null) break;
                        // Poll + Available==0 表示对端已 FIN
                        if (sk.Poll(0, SelectMode.SelectRead) && sk.Available == 0) break;
                    }
                    catch { break; }
                }
            }
            catch { }
            finally
            {
                if (viewer != null)
                {
                    if (isTs) lock (_tsViewers) _tsViewers.Remove(viewer);
                    else if (isFrame) lock (_frameViewers) _frameViewers.Remove(viewer);
                    else lock (_viewers) _viewers.Remove(viewer);

                    if (viewer.DroppedFrames > 0)
                        Log.Warn("IpCam", "查看端结束：" + viewer.Remote + " 丢帧=" + viewer.DroppedFrames + " 已发=" + viewer.SentFrames);
                    try { viewer.Close(); } catch { }
                }
                GracefulClose(client);
            }
        }

        /// <summary>
        /// 优雅关闭：先半关发送端把数据送完，再把残留的接收数据排空，最后 close。
        /// 直接 close 一个还有未读数据的 socket 会触发 RST，导致对端丢失尾部数据。
        /// </summary>
        private static void GracefulClose(TcpClient client)
        {
            try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            try
            {
                client.ReceiveTimeout = 400;
                var s = client.GetStream();
                var junk = new byte[2048];
                for (int i = 0; i < 64; i++) { if (s.Read(junk, 0, junk.Length) <= 0) break; }
            }
            catch { }
            try { client.Close(); } catch { }
        }

        private static string RemoteOf(TcpClient c)
        {
            try { return c.Client.RemoteEndPoint?.ToString() ?? "?"; } catch { return "?"; }
        }

        /// <summary>流式响应：由各查看端自己的发送线程写，这里只负责等断开。</summary>
        private static void WriteSimple(System.IO.Stream ns, string contentType, byte[] body, string extraHeaders = null)
        {
            string head = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType
                + "\r\nContent-Length: " + body.Length
                + "\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\n"
                + (extraHeaders ?? "")
                + "Connection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            if (body.Length > 0) ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        private static void WriteText(System.IO.Stream ns, int code, string text)
        {
            var body = Encoding.UTF8.GetBytes(text ?? "");
            string head = "HTTP/1.1 " + code + " " + (code == 200 ? "OK" : "Error")
                + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length
                + "\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        /* ===================== 浏览器播放页 ===================== */

        private string LoadHtml()
        {
            if (_html != null) return _html;
            try
            {
                using (var s = _ctx.Assets.Open("camera/index.html"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                    _html = r.ReadToEnd();
            }
            catch (Exception ex)
            {
                return L10n.T("<!DOCTYPE html><meta charset=\"utf-8\"><h3>播放页资源缺失</h3><p>")
                    + JsonEsc(ex.Message) + L10n.T("</p><p>可直接用播放器打开 <a href=\"live.ts\">/live.ts</a> 或 ")
                    + "<a href=\"stream.h264\">/stream.h264</a></p>";
            }
            return _html;
        }

        /* ===================== MSE 分片 ===================== */

        private byte[] GetInitMp4()
        {
            if (_initMp4 != null) return _initMp4;
            // 【为什么 · 首帧死结根治】不等编码帧：先从 OutputFormat 拉 csd 构造 init。
            // 屏幕静止 / 刚启动时编码器可能一帧都没吐，但 OutputFormat 多数已带参数集，
            // 这样 /mse/init 就不会因为「没帧」返回 503。
            TryEnsureInitFromOutputFormat();
            if (_initMp4 != null) return _initMp4;
            byte[] sps, pps;
            lock (_segLock) { sps = _spsAnnexB; pps = _ppsAnnexB; }
            if (sps == null || pps == null) return null;
            try
            {
                var init = Mp4Muxer.BuildInit(Width, Height, sps, pps, Fps);
                if (init != null) lock (_segLock) _initMp4 = init;
                return _initMp4;
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "生成 init 段失败：" + ex.Message);
                return null;
            }
        }

        private void AddMp4Sample(byte[] avcc, bool isKey)
        {
            lock (_segLock)
            {
                // 每个分片都以关键帧开头：来了关键帧且已有数据，就把上一段吐出去
                if (isKey && _segSamples.Count > 0) FlushSegmentLocked();
                _segSamples.Add(avcc);
                // 安全上限：万一关键帧间隔异常长，也别无限攒
                if (_segSamples.Count >= Math.Max(4, Fps * 3)) FlushSegmentLocked();
            }
        }

        private void FlushSegmentLocked()
        {
            if (_segSamples.Count == 0) return;
            int dur = Math.Max(1, 90000 / Math.Max(1, Fps));
            uint idx = _segSeq + 1;
            byte[] data;
            try
            {
                data = Mp4Muxer.BuildSegment(_segSamples, dur, _segBaseDecode, idx);
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "生成分片失败：" + ex.Message);
                _segSamples.Clear();
                return;
            }
            if (data != null)
            {
                _segBaseDecode += (long)_segSamples.Count * dur;
                _segSeq = idx;
                _segments.Add(new Mp4Segment { Index = idx, Data = data });
                while (_segments.Count > SegKeep) _segments.RemoveAt(0);
                Monitor.PulseAll(_segLock);
            }
            _segSamples.Clear();
        }

        private void ResetStreams()
        {
            lock (_segLock)
            {
                _segments.Clear();
                _segSamples.Clear();
                _segBaseDecode = 0;
                _segSeq = 0;
                _initMp4 = null;
                Monitor.PulseAll(_segLock);
            }
            lock (_tsLock) _ts.Reset();
            lock (_tsViewers) _tsViewers.Clear();
        }

        private void HandleSegment(System.IO.Stream ns, string query)
        {
            int want = 0;
            int ni = query.IndexOf("n=", StringComparison.OrdinalIgnoreCase);
            if (ni >= 0)
            {
                int end = query.IndexOf('&', ni);
                string vs = end > 0 ? query.Substring(ni + 2, end - ni - 2) : query.Substring(ni + 2);
                int.TryParse(vs, out want);
            }

            Mp4Segment seg = null;
            int epoch;
            lock (_segLock)
            {
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (true)
                {
                    bool shouldWait;
                    seg = PickSegmentLocked(want, out shouldWait);
                    if (seg != null) break;
                    if (!shouldWait || !_running) break;
                    int ms = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (ms <= 0) break;
                    // 要的分片还没产出：在这里等（而不是拿旧段立刻返回，否则客户端会空转重复下载）
                    Monitor.Wait(_segLock, Math.Min(ms, 800));
                }
                if (seg == null && _segments.Count > 0)
                    seg = _segments[_segments.Count - 1]; // 超时兜底：给最新一段
                epoch = _epoch;
            }

            if (seg == null) { WriteText(ns, 503, "no segment"); return; }

            string head = "HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: " + seg.Data.Length
                + "\r\nX-Seg-Index: " + seg.Index
                + "\r\nX-Epoch: " + epoch
                + "\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(seg.Data, 0, seg.Data.Length);
            ns.Flush();
        }

        /// <summary>取 <= want 的最近一段；want 太旧（已被环丢弃）就退回最早可用的那段。</summary>
        /// <summary>
        /// 取分片：
        /// - want &lt;= 0（首次请求）→ 直接给最新一段，起播最快；
        /// - 命中 want → 返回该段；
        /// - want 比环里最旧的还旧（已被丢弃）→ 跳到最旧可用；
        /// - want 比最新的还新（还没产出）→ shouldWait=true，由调用方阻塞等。
        /// </summary>
        private Mp4Segment PickSegmentLocked(int want, out bool shouldWait)
        {
            shouldWait = false;
            if (_segments.Count == 0) { shouldWait = _running; return null; }
            if (want <= 0) return _segments[_segments.Count - 1];

            for (int i = 0; i < _segments.Count; i++)
                if (_segments[i].Index == want) return _segments[i];

            uint newest = _segments[_segments.Count - 1].Index;
            if (want > newest) { shouldWait = true; return null; }
            return _segments[0]; // 已被环丢弃，退回最早可用的一段
        }

        /* ===================== 码率 / 分辨率自适应（帧率恒定） ===================== */

        /// <summary>
        /// 生成画质阶梯：0 档 = 用户选择的上限；越往下码率与分辨率越低，帧率始终不变。
        /// 阶梯按「采集源原始宽高比」等比缩放，所以竖屏共享（手机屏幕）不会变形。
        /// 相邻同分辨率档只需 SetParameters 改码率（瞬时、不打断画面）；跨分辨率才重建编码器。
        /// </summary>
        /// <param name="maxShort">画质上限的短边像素（摄像头=用户选的 720/1080；屏幕=用户选的短边上限）</param>
        private void BuildLadder(int capW, int capH, int capBr, int maxShort)
        {
            int srcShort = Math.Min(capW, capH);
            int capShort = maxShort > 0 ? Math.Min(maxShort, srcShort) : srcShort;
            if (capShort < 64) capShort = srcShort;

            // 短边阶梯：前两档同分辨率（只降码率，最轻量的降级），后面逐级降分辨率
            int[] shorts = capShort >= 1000
                ? new[] { capShort, capShort, 720, 540, 360 }
                : new[] { capShort, capShort, 540, 360, 272 };
            double[] brRatio = { 1.0, 0.62, 0.40, 0.225, 0.135 };

            var l = new List<int[]>();
            var seen = new HashSet<string>();
            for (int i = 0; i < shorts.Length; i++)
            {
                int[] wh = ScaleToShort(capW, capH, srcShort, Math.Min(shorts[i], srcShort));
                int br = Math.Max(200000, (int)(capBr * brRatio[i]));
                string key = wh[0] + "x" + wh[1] + "@" + br;
                if (!seen.Add(key)) continue;   // 采集源本身就很小的时候避免重复档位
                l.Add(new[] { wh[0], wh[1], br });
            }

            lock (_lock) { _rungs = l.ToArray(); _rung = 0; }
            Log.Warn("IpCam", "画质阶梯：" + string.Join(" | ",
                l.ConvertAll(x => x[0] + "x" + x[1] + "@" + (x[2] / 1000) + "k").ToArray()));
        }

        /// <summary>按短边目标等比缩放，保持宽高比（中间档按 16 对齐，编码器兼容性最好）。</summary>
        private static int[] ScaleToShort(int capW, int capH, int srcShort, int target)
        {
            if (target >= srcShort) return new[] { Even(capW), Even(capH) };
            double k = (double)target / srcShort;
            return new[] { Align16((int)Math.Round(capW * k)), Align16((int)Math.Round(capH * k)) };
        }

        private static int Even(int v) { v &= ~1; return v < 16 ? 16 : v; }

        private static int Align16(int v) { v = (v + 8) / 16 * 16; return v < 64 ? 64 : v; }

        /// <summary>读取当前档位的 宽/高/码率。</summary>
        private void CurrentRung(out int w, out int h, out int br)
        {
            lock (_lock)
            {
                if (_rungs == null || _rungs.Length == 0) { w = Width; h = Height; br = Bitrate; return; }
                var r = _rungs[Math.Max(0, Math.Min(_rung, _rungs.Length - 1))];
                w = r[0]; h = r[1]; br = r[2];
            }
        }

        private void StartAbr()
        {
            _abrThread = new Thread(AbrLoop) { IsBackground = true, Name = "cam-abr" };
            _abrThread.Start();
        }

        /// <summary>
        /// 每秒判定一次：有拥塞（丢帧 / 浏览器上报卡顿）就降一档，连续 12 秒干净就升一档。
        /// 帧率永远不动 —— 降的是码率和分辨率。
        /// </summary>
        private void AbrLoop()
        {
            try
            {
                int tick = 0;
                while (_running)
                {
                    Thread.Sleep(500);
                    if (!_running) break;
                    if ((++tick & 1) == 0) continue;   // 每 1 秒判定一次

                    int drops = Interlocked.Exchange(ref _dropSignals, 0);
                    int stalls = Interlocked.Exchange(ref _stallSignals, 0);
                    if (ViewerCount == 0) { _abrScore = 0; continue; }

                    bool congested = drops > 0 || stalls > 0;
                    if (congested) _abrScore = Math.Min(_abrScore + 2, 8);
                    else if (_abrScore > 0) _abrScore--;

                    double since = (DateTime.UtcNow - _rungAt).TotalSeconds;
                    int bottom;
                    lock (_lock) bottom = _rungs == null ? 0 : _rungs.Length - 1;

                    if (_abrScore >= 3 && _rung < bottom && since >= 3)
                    {
                        _abrScore = 0;
                        StepRung(+1, L10n.T("网络拥塞(drop=") + drops + " stall=" + stalls + ")");
                    }
                    else if (_abrScore == 0 && _rung > 0 && since >= _upDelaySec)
                    {
                        StepRung(-1, L10n.T("持续流畅"));
                    }
                    else if (_abrScore == 0 && since >= 60 && _upDelaySec != 12)
                    {
                        _upDelaySec = 12;   // 在某档稳定 1 分钟，重置升档冷却
                    }
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex) { Log.Warn("IpCam", "自适应线程异常：" + ex.Message); }
        }

        private void StepRung(int delta, string why)
        {
            int nv;
            lock (_lock)
            {
                if (_rungs == null) return;
                nv = _rung + delta;
                if (nv < 0 || nv >= _rungs.Length) return;
                _rung = nv;
            }
            Log.Warn("IpCam", "自适应：" + why + " → 第 " + nv + " 档");
            if (delta > 0) _upDelaySec = Math.Min(60, _upDelaySec * 2);   // 降过档就拉长下次升档的等待
            try { ApplyRung(); }
            catch (Exception ex) { Log.Warn("IpCam", "自适应切换失败：" + ex.Message); }
            Interlocked.Increment(ref _rungSwitches);
        }

        private void ApplyRung()
        {
            int w, h, br;
            lock (_lock)
            {
                if (_rungs == null || _rungs.Length == 0) return;
                var r = _rungs[_rung];
                w = r[0]; h = r[1]; br = r[2];
            }
            if (!_running) return;

            if (w == Width && h == Height)
            {
                // 同分辨率：直接改码率，瞬时生效，画面不中断
                SetVideoBitrate(br);
                Bitrate = br;
                _rungAt = DateTime.UtcNow;
                Log.Warn("IpCam", "码率调整为 " + (br / 1000) + " kbps（分辨率保持 " + w + "x" + h + "）");
                return;
            }

            RebuildPipeline(w, h, br);
            _rungAt = DateTime.UtcNow;
        }

        private void SetVideoBitrate(int br)
        {
            var enc = _encoder;
            if (enc == null) return;
            try
            {
                var b = new Bundle();
                b.PutInt("video-bitrate", br);
                enc.SetParameters(b);
            }
            catch (Exception ex) { Log.Warn("IpCam", "设置码率失败：" + ex.Message); }
        }

        /// <summary>换分辨率：重建编码器 + 采集会话（摄像头设备保持打开，不做整体重启）。</summary>
        private bool RebuildPipeline(int w, int h, int br)
        {
            lock (_rebuildLock)
            {
                if (!_running) return false;
                int pw = Width, ph = Height, pbr = Bitrate;
                Log.Warn("IpCam", "切换分辨率 " + pw + "x" + ph + " → " + w + "x" + h + " @" + (br / 1000) + "kbps");

                bool screen = Source == ShareSource.Screen;
                if (!screen)
                {
                    // 摄像头：停掉旧会话，稍后重建（不涉及 VirtualDisplay）。
                    try { _camSession?.StopRepeating(); } catch { }
                    try { _camSession?.Close(); } catch { }
                    _camSession = null;
                }
                // 【为什么 · 对齐 ScreenStream】屏幕共享绝不 Release + 重建 VirtualDisplay：
                // 旧代码在这里直接 Release，既让已连接客户端瞬断，也浪费一次性授权 token。
                // 这里保留 _vd，等新编码器建好后只 SetSurface/Resize（见下）。

                var oldEnc = _encoder;
                _encoder = null;                       // 让编码线程自然退出
                try { _encoderThread?.Join(600); } catch { }
                _encoderThread = null;
                if (oldEnc != null) { try { oldEnc.Stop(); } catch { } try { oldEnc.Release(); } catch { } }
                try { _inputSurface?.Release(); } catch { }
                _inputSurface = null;

                Width = w; Height = h; Bitrate = br;
                bool ok = CreateEncoderSurface();
                if (!ok)
                {
                    Log.Warn("IpCam", "新分辨率编码器启动失败，回滚：" + LastError);
                    Width = pw; Height = ph; Bitrate = pbr;
                    ok = CreateEncoderSurface();
                    if (!ok)
                    {
                        Log.Warn("IpCam", "回滚也失败，停止共享");
                        LastError = L10n.T("编码器无法启动");
                        Stop();
                        return false;
                    }
                    w = pw; h = ph; br = pbr;
                }

                // 新流需要新的 init 段：清掉旧分片并推进 epoch，浏览器据此重新初始化。
                // 【为什么 · 断流修复】epoch++ 保留（分辨率变了必须让浏览器重取），
                // 但下面会用新编码器的 csd 即时重建 init 段，所以不会因 _initMp4=null 而 503。
                lock (_segLock)
                {
                    _initMp4 = null;
                    _segments.Clear();
                    _segSamples.Clear();
                    _segBaseDecode = 0;
                    _segSeq = 0;
                    _epoch++;
                    Monitor.PulseAll(_segLock);
                }

                bool started;
                if (screen)
                {
                    // 只换 Surface/Resize，正常情况下不重建 VirtualDisplay（对齐 ScreenStream）。
                    started = RebindVirtualDisplaySurface();
                    if (!started)
                    {
                        // 换 Surface 失败（属性不被支持 / VD 意外不存在）：先 Release 旧 VD，
                        // 再复用同一个 MediaProjection 重建。注意 createVirtualDisplay 可以
                        // 对同一个 _mp 重复调用，一次性授权 token 只在 GetMediaProjection 时消耗，
                        // 所以这里不会重新弹授权框；总比让画面永久黑屏好。
                        try { _vd?.Release(); } catch { }
                        _vd = null;
                        started = CreateVirtualDisplay();
                        if (!started) { try { Thread.Sleep(200); } catch { } started = CreateVirtualDisplay(); }
                    }
                    if (!started) Log.Warn("IpCam", "虚拟显示换 Surface 失败：" + (LastError ?? "未知"));
                }
                else
                {
                    started = CreateSessionInternal(5000);
                    if (!started)
                    {
                        // 部分机型关掉旧会话后需要一点时间，再试一次
                        try { Thread.Sleep(200); } catch { }
                        started = CreateSessionInternal(5000);
                    }
                }
                if (!started) Log.Warn("IpCam", "新分辨率会话未确认：" + (LastError ?? "超时"));

                _encoderThread = new Thread(EncoderLoop) { IsBackground = true };
                _encoderThread.Start();
                // 【为什么 · 重建后立刻补参数集】用新编码器的 csd 即时重建 init 段，
                // 主动喂一帧触发首个 IDR，并给已连裸流查看端补发 SPS/PPS，
                // 让已连接客户端不必等下一个 1s 关键帧、也不必重新连接。
                TryEnsureInitFromOutputFormat();
                KickFirstFrame();
                ResendParameterSets();
                Log.Warn("IpCam", "分辨率已切换为 " + w + "x" + h + " @" + (br / 1000) + "kbps");
                return true;
            }
        }

        /* ===================== MPEG-TS ===================== */

        private void BroadcastTs(byte[] annexb, bool isKey)
        {
            List<StreamViewer> snap;
            lock (_tsViewers)
            {
                if (_tsViewers.Count == 0) return;
                snap = new List<StreamViewer>(_tsViewers);
            }
            byte[] pkts;
            lock (_tsLock)
            {
                try { pkts = _ts.BuildFrame(annexb, isKey, Fps); }
                catch { return; }
            }
            if (pkts == null || pkts.Length == 0) return;

            for (int i = 0; i < snap.Count; i++) snap[i].Enqueue(pkts, isKey);
        }

        private string BuildInfoJson()
        {
            int up = (int)(DateTime.Now - StartTime).TotalSeconds;
            int ep, rung, rungCount; string rungDesc;
            lock (_segLock) ep = _epoch;
            lock (_lock)
            {
                rung = _rung;
                rungCount = _rungs == null ? 1 : _rungs.Length;
                rungDesc = Width + "x" + Height + "@" + Bitrate;
            }
            return "{\"name\":\"" + JsonEsc(DeviceStatus.DeviceName) + "\",\"ip\":\"" + DeviceStatus.Ip
                + "\",\"width\":" + Width + ",\"height\":" + Height + ",\"fps\":" + Fps
                + ",\"bitrate\":" + Bitrate + ",\"rotation\":" + Rotation + ",\"front\":" + (Front ? "true" : "false")
                + ",\"source\":\"" + (Source == ShareSource.Screen ? "screen" : "camera") + "\""
                + ",\"uptimeSec\":" + up + ",\"viewers\":" + ViewerCount
                + ",\"codec\":\"" + CodecString() + "\""
                + ",\"epoch\":" + ep + ",\"rung\":" + rung + ",\"rungCount\":" + rungCount
                + ",\"current\":\"" + rungDesc + "\""
                + ",\"adaptive\":" + ((_rungs != null && _rungs.Length > 1) ? "true" : "false")
                + ",\"switchCount\":" + Interlocked.Read(ref _rungSwitches)
                + ",\"urls\":{\"page\":\"" + InfoPrefix + "/\",\"ts\":\"" + InfoPrefix + "/live.ts\",\"raw\":\"" + InfoPrefix + "/stream.h264\",\"info\":\"" + InfoPrefix + "/info\"}"
                + ",\"https\":\"" + (WebServer.HttpsRunning ? WebServer.HttpsBaseUrl + "/cam/" : "") + "\"}";
        }

        /// <summary>统一端口下所有路径都挂在 /cam 下；单独监听时就在根上。</summary>
        private string InfoPrefix
        {
            get { return UsedHttpPort == WebServer.ExternalPortActual ? "/cam" : ""; }
        }

        /// <summary>由 SPS 推出 MSE 需要的 codec 串，如 avc1.42C01F。</summary>
        private string CodecString()
        {
            var sps = _spsAnnexB;
            if (sps == null) return "avc1.42E01E";
            int off = 0;
            if (sps.Length > 4 && sps[0] == 0 && sps[1] == 0 && sps[2] == 0 && sps[3] == 1) off = 4;
            else if (sps.Length > 3 && sps[0] == 0 && sps[1] == 0 && sps[2] == 1) off = 3;
            if (sps.Length < off + 4) return "avc1.42E01E";
            return string.Format("avc1.{0:X2}{1:X2}{2:X2}", sps[off + 1], sps[off + 2], sps[off + 3]);
        }

        private static void ReadHeaders(System.IO.Stream ns, out string path)
        {
            path = null;
            var sb = new StringBuilder();
            byte[] buf = new byte[1];
            int total = 0;
            try
            {
                // 必须读到空行为止：若把请求头剩余字节留在接收缓冲里，
                // 关闭 socket 时内核会发 RST（而非 FIN），对端未读数据会被丢弃，
                // 表现为大响应（分片 MP4）随机在 ~64KB 处被截断。
                while (total < 8192)
                {
                    int r = ns.Read(buf, 0, 1);
                    if (r <= 0) break;
                    sb.Append((char)buf[0]);
                    total++;
                    if (total >= 4 && sb[total - 1] == '\n' && sb[total - 2] == '\r'
                        && sb[total - 3] == '\n' && sb[total - 4] == '\r') break;
                }
            }
            catch { }
            string header = sb.ToString();
            int sp = header.IndexOf(' ');
            if (sp >= 0)
            {
                int sp2 = header.IndexOf(' ', sp + 1);
                if (sp2 > sp) path = header.Substring(sp + 1, sp2 - sp - 1);
            }
        }

        /* ===================== UDP 信标 ===================== */

        private void StartBeacon()
        {
            _beaconThread = new Thread(BeaconLoop) { IsBackground = true };
            _beaconThread.Start();
        }

        private void BeaconLoop()
        {
            try
            {
                _beaconSock = new UdpClient();
                _beaconSock.EnableBroadcast = true;
                _beaconSock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            catch (Exception ex) { Log.Warn("IpCam", "信标套接字失败：" + ex.Message); return; }

            while (_running)
            {
                try
                {
                    string msg = "LANCAM|" + DeviceStatus.Ip + "|" + UsedHttpPort + "|"
                        + DeviceStatus.DeviceName + "|" + Width + "x" + Height + "@" + Fps + "|" + Rotation
                        + "|" + (Source == ShareSource.Screen ? "screen" : "camera");
                    byte[] data = Encoding.UTF8.GetBytes(msg);
                    _beaconSock.Send(data, data.Length, "255.255.255.255", BeaconPort);
                    if (_beaconSent == 0) { Log.Warn("IpCam", "信标已发送：" + msg); _beaconSent = 1; }
                }
                catch { }
                try { Thread.Sleep(1000); } catch (ThreadInterruptedException) { break; }
            }
        }

        /* ===================== 工具 ===================== */

        private static byte[] BbToArray(Java.Nio.ByteBuffer bb)
        {
            if (bb == null) return null;
            bb.Position(0);
            byte[] arr = new byte[bb.Remaining()];
            bb.Get(arr);
            return arr;
        }

        private static byte[] BbToArray(Java.Nio.ByteBuffer bb, int offset, int size)
        {
            if (bb == null) return null;
            int limit = bb.Limit();
            if (offset < 0) offset = 0;
            if (offset >= limit) return null;
            if (size > limit - offset) size = limit - offset; // 越界保护，避免 IndexOutOfRange 打死线程
            if (size <= 0) return null;
            bb.Position(offset);
            byte[] arr = new byte[size];
            bb.Get(arr, 0, size);
            return arr;
        }

        /// <summary>
        /// 从编码器的 csd / BUFFER_FLAG_CODEC_CONFIG 数据里提取 SPS(7) 与 PPS(8)，统一存成带起始码的 Annex-B。
        /// 兼容三种常见形态：① avcC；② Annex-B（可含多个 NAL）；③ 单个裸 NAL。
        /// 本次取到才更新，取不到保留原值，避免把已有可用值覆盖成 null。
        /// </summary>
        private static void ExtractSpsPps(byte[] data, ref byte[] sps, ref byte[] pps)
        {
            if (data == null || data.Length < 4) return;

            // ① avcC：以 0x01(configurationVersion) 开头
            if (data[0] == 0x01 && data.Length >= 8)
            {
                byte[] s, p;
                if (TryParseAvcC(data, out s, out p))
                {
                    if (s != null) sps = s;
                    if (p != null) pps = p;
                    if (sps != null && pps != null) return;
                }
            }

            // ②/③ Annex-B 或裸 NAL：按 NAL 类型归位
            foreach (var nal in SplitAnnexB(data))
            {
                if (nal == null || nal.Length < 1) continue;
                int t = nal[0] & 0x1F;
                if (t == 7) sps = Cat(StartCode(), nal);
                else if (t == 8) pps = Cat(StartCode(), nal);
            }
        }

        /// <summary>解析 avcC（长度前缀式配置记录）。失败返回 false，不抛异常。</summary>
        private static bool TryParseAvcC(byte[] d, out byte[] sps, out byte[] pps)
        {
            sps = null; pps = null;
            try
            {
                int pos = 5; // 跳过 version/profile/compat/level/lengthSizeMinusOne
                if (pos >= d.Length) return false;
                int numSps = d[pos++] & 0x1F;
                for (int i = 0; i < numSps; i++)
                {
                    if (pos + 2 > d.Length) return false;
                    int sz = (d[pos] << 8) | d[pos + 1]; pos += 2;
                    if (sz <= 0 || pos + sz > d.Length) return false;
                    sps = Cat(StartCode(), Sub(d, pos, sz));
                    pos += sz;
                }
                if (pos >= d.Length) return sps != null;
                int numPps = d[pos++];
                for (int i = 0; i < numPps; i++)
                {
                    if (pos + 2 > d.Length) return sps != null;
                    int sz = (d[pos] << 8) | d[pos + 1]; pos += 2;
                    if (sz <= 0 || pos + sz > d.Length) return sps != null;
                    pps = Cat(StartCode(), Sub(d, pos, sz));
                    pos += sz;
                }
                return sps != null || pps != null;
            }
            catch { return false; }
        }

        /// <summary>按 Annex-B 起始码切分 NAL（返回不含起始码的净荷）。无起始码则整体视为一个裸 NAL。</summary>
        private static List<byte[]> SplitAnnexB(byte[] d)
        {
            var res = new List<byte[]>();
            if (d == null || d.Length < 3) return res;

            var starts = new List<int>(); // 净荷起点
            var scLens = new List<int>(); // 对应起始码长度
            int i = 0;
            while (i + 2 < d.Length)
            {
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) { starts.Add(i + 3); scLens.Add(3); i += 3; }
                else if (i + 3 < d.Length && d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 0 && d[i + 3] == 1) { starts.Add(i + 4); scLens.Add(4); i += 4; }
                else i++;
            }

            if (starts.Count == 0) { res.Add(d); return res; }

            for (int k = 0; k < starts.Count; k++)
            {
                int begin = starts[k];
                int end = (k + 1 < starts.Count) ? starts[k + 1] - scLens[k + 1] : d.Length;
                if (end > begin) res.Add(Sub(d, begin, end - begin));
            }
            return res;
        }

        private static bool LooksLikeAnnexB(byte[] d)
        {
            if (d == null || d.Length < 4) return false;
            if (d[0] == 0 && d[1] == 0 && d[2] == 1) return true;
            if (d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 1) return true;
            return false;
        }

        /// <summary>按 4 字节大端长度前缀切分 NAL（不含长度字段）。</summary>
        private static List<byte[]> SplitLengthPrefixed(byte[] buf)
        {
            var res = new List<byte[]>();
            if (buf == null) return res;
            int pos = 0;
            while (pos + 4 <= buf.Length)
            {
                int len = (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
                pos += 4;
                if (len <= 0 || pos + len > buf.Length) break;
                res.Add(Sub(buf, pos, len));
                pos += len;
            }
            return res;
        }

        /// <summary>NAL 列表 → Annex-B（每个 NAL 前加 00 00 00 01）。</summary>
        private static byte[] ToAnnexB(List<byte[]> nals)
        {
            int total = 0;
            for (int i = 0; i < nals.Count; i++) total += 4 + nals[i].Length;
            var r = new byte[total];
            int o = 0;
            for (int i = 0; i < nals.Count; i++)
            {
                r[o] = 0; r[o + 1] = 0; r[o + 2] = 0; r[o + 3] = 1;
                o += 4;
                Buffer.BlockCopy(nals[i], 0, r, o, nals[i].Length);
                o += nals[i].Length;
            }
            return r;
        }

        /// <summary>NAL 列表 → AVCC（每个 NAL 前加 4 字节长度），fMP4 的 mdat 用这个格式。</summary>
        private static byte[] ToAvcc(List<byte[]> nals)
        {
            int total = 0;
            for (int i = 0; i < nals.Count; i++) total += 4 + nals[i].Length;
            var r = new byte[total];
            int o = 0;
            for (int i = 0; i < nals.Count; i++)
            {
                int len = nals[i].Length;
                r[o] = (byte)(len >> 24); r[o + 1] = (byte)(len >> 16);
                r[o + 2] = (byte)(len >> 8); r[o + 3] = (byte)len;
                o += 4;
                Buffer.BlockCopy(nals[i], 0, r, o, len);
                o += len;
            }
            return r;
        }

        private static string HexHead(byte[] d, int n)
        {
            if (d == null) return "null";
            var sb = new StringBuilder();
            for (int i = 0; i < n && i < d.Length; i++) sb.Append(d[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }

        /// <summary>把长度前缀（默认 4 字节大端）的 NAL 流转换为 Annex-B（起始码）裸流。</summary>
        private static byte[] ConvertFrameToAnnexB(byte[] buf, int lenSize)
        {
            if (buf == null || buf.Length < lenSize) return null;
            var outp = new List<byte>(buf.Length + 16);
            int pos = 0;
            while (pos + lenSize <= buf.Length)
            {
                int len = 0;
                for (int i = 0; i < lenSize; i++) len = (len << 8) | buf[pos + i];
                pos += lenSize;
                if (pos + len > buf.Length) break;
                outp.AddRange(StartCode());
                for (int i = 0; i < len; i++) outp.Add(buf[pos + i]);
                pos += len;
            }
            return outp.ToArray();
        }

        private static byte[] StartCode() { return new byte[] { 0, 0, 0, 1 }; }
        private static byte[] Sub(byte[] src, int off, int len)
        {
            var r = new byte[len];
            Array.Copy(src, off, r, 0, len);
            return r;
        }
        private static byte[] Cat(params byte[][] parts)
        {
            int total = 0; foreach (var p in parts) if (p != null) total += p.Length;
            var r = new byte[total]; int o = 0;
            foreach (var p in parts) if (p != null) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
        private static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /* ---------------- Camera2 回调 ---------------- */
    internal sealed class CameraStateCallback : CameraDevice.StateCallback
    {
        private readonly Action<CameraDevice> _opened;
        private readonly Action _disc;
        private readonly Action<CameraError> _err;
        public CameraStateCallback(Action<CameraDevice> opened, Action disc, Action<CameraError> err)
        { _opened = opened; _disc = disc; _err = err; }
        public override void OnOpened(CameraDevice camera) => _opened?.Invoke(camera);
        public override void OnDisconnected(CameraDevice camera) => _disc?.Invoke();
        public override void OnError(CameraDevice camera, CameraError error) => _err?.Invoke(error);
    }

    internal sealed class SessionStateCallback : CameraCaptureSession.StateCallback
    {
        private readonly Action<CameraCaptureSession> _cfg;
        private readonly Action<CameraCaptureSession> _fail;
        public SessionStateCallback(Action<CameraCaptureSession> cfg, Action<CameraCaptureSession> fail)
        { _cfg = cfg; _fail = fail; }
        public override void OnConfigured(CameraCaptureSession session) => _cfg?.Invoke(session);
        public override void OnConfigureFailed(CameraCaptureSession session) => _fail?.Invoke(session);
    }

    /* ---------------- 屏幕共享回调 ---------------- */

    /// <summary>系统停止屏幕录制（用户点通知栏停止 / 授权被收回）时通知服务器收尾。</summary>
    internal sealed class ProjectionStopCallback : MediaProjection.Callback
    {
        private readonly IpCameraServer _srv;
        public ProjectionStopCallback(IpCameraServer srv) { _srv = srv; }
        public override void OnStop() { try { _srv.OnProjectionStopped(); } catch { } }
    }

    /// <summary>监听屏幕旋转/尺寸变化，让共享画面跟着转正（否则手机横过来后画面会被压扁）。</summary>
    internal sealed class ScreenDisplayListener : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        private readonly IpCameraServer _srv;
        public ScreenDisplayListener(IpCameraServer srv) { _srv = srv; }
        public void OnDisplayAdded(int displayId) { }
        public void OnDisplayRemoved(int displayId) { }
        public void OnDisplayChanged(int displayId)
        {
            try { _srv.OnDisplayChanged(displayId); } catch { }
        }
    }
}

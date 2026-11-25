using System;
using System.IO;
using System.Threading;
using desk;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using Android.Views;

namespace AhoChatA
{
    /// <summary>
    /// 安卓被控端的屏幕采集器：MediaProjection → VirtualDisplay → ImageReader(RGBA_8888)
    /// → 缩放到指定短边 → JPEG 压缩 → 回调。
    ///
    /// 【为什么用 JPEG 而不用 H.264】
    /// 本工程桌面共享（IpCameraServer）走的是 H.264 硬编，但那条路只把画面喂给 Surface，
    /// **拿不到像素**，也就没法按远控协议发"整帧 JPEG"。而 desk 的 Windows 查看端、
    /// 网页查看端、安卓查看端三者对"整帧 JPEG"都是现成支持的（codec=0、flags=0），
    /// 换用 H.264 就得给三端各写一个解码器——rustdesk 有 ffmpeg 所以可以随便换，
    /// 我们没有，所以选 JPEG：一处实现，三端立刻都能控这台手机。
    ///
    /// 【编码模式 / 参数化（v2 协议对齐）】
    /// 采集参数不再写死：短边 / 帧率 / 质量 / 编码模式都可通过 Start(...) 与
    /// SetMode / SetMaxShortSide / SetMaxFps / SetQuality 在运行期调整；RdHostServer 收到
    /// QualitySettings 时按 QualityConfig.Mode 分支后把这里配到位（Fps 真正参与
    /// OnImageAvailable 的节流预算、质量真正参与 Compress，不再是写死的 12fps/55）。
    /// 默认值改读 ScreenShareSettings（参考 ScreenStream：50% 缩放 / 质量 80 / fps Auto→标称 30），
    /// 仅在被显式配置时变化；改 fps/quality 绝不重建 VirtualDisplay（只改编码参数）。
    ///
    /// 【H.264 已落地】
    /// Mode==H264 时把 VirtualDisplay 的输出 Surface 从 ImageReader 换到 RdH264Encoder
    /// （MediaCodec createInputSurface）的输入 Surface，从编码器 output buffer 取 Annex-B
    /// 裸流（关键帧前置 SPS/PPS），经 H264Ready 回调交给 RdHostServer 组帧（codec=CODEC_H264=4）。
    /// 编码器起不来自动回退整帧 JPEG 并由 RdHostServer 记录原因，绝不假装编码成功。
    /// JpegRegions/RawRegions 仍只记录 Mode，实际输出整帧 JPEG（不谎报区域帧）。
    /// H265 两端无解码器，SetMode 明确回退 JPEG。
    ///
    /// 【为什么不用 ImageReader 的 YUV 格式】
    /// YUV_420_888 转 RGB 要在 C# 里逐像素算，1080p 一帧就几十毫秒，直接卡死；
    /// RGBA_8888 可以整块 CopyPixelsFromBuffer 交给 Bitmap，再交给系统 JPEG 编码器，快得多。
    /// </summary>
    public sealed class RdScreenGrabber : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private const string Tag = "RdGrab";

        private MediaProjection _mp;
        private MediaProjectionCallback _mpCb;
        private ImageReader _reader;
        private VirtualDisplay _vd;
        private DisplayManager _dm;
        private DisplayListener _dl;
        private HandlerThread _thread;
        private Handler _handler;

        private int _srcW, _srcH, _srcDpi;      // 采集源（整块屏幕）像素
        private int _outW, _outH, _outDpi;      // 实际推给查看端的尺寸 / 对应 dpi
        // 默认值改读集中设置（参考 ScreenStream：50% 缩放 / 质量 80 / fps Auto→标称 30）。
        private int _maxShortSide = ScreenShareSettings.FallbackShortSide;
        private int _fps = ScreenShareSettings.DefaultMaxFps;
        private int _quality = ScreenShareSettings.DefaultJpegQuality;
        private ScreenShareSettings _settings;
        private ScreenShareSettings Settings
        {
            get { return _settings ?? (_settings = ScreenShareSettings.Instance(Application.Context)); }
        }
        /// <summary>背压闸门：下游（发送端）忙时返回 true，采集线程直接丢帧不做 JPEG 压缩。</summary>
        public Func<bool> ShouldDropFrame;
        /// <summary>最近一帧缓存（ScreenStream「缓存帧立刻可交付」）：补首帧时重发。</summary>
        private byte[] _lastJpeg;
        private int _lastW, _lastH;
        private readonly object _lastLock = new object();
        /// <summary>当前编码模式。默认整帧 JPEG（与 v1 一致）；H264 由 RdH264Encoder 接管。</summary>
        private Protocol.EncodingMode _mode = Protocol.EncodingMode.JpegFullFrame;
        /// <summary>按需创建的 H264 硬编码器：Mode==H264 时 VirtualDisplay 直接输出到它。</summary>
        private RdH264Encoder _h264;
        /// <summary>一帧 Annex-B H264 就绪（data, w, h），交给 RdHostServer 组帧。采集线程触发。</summary>
        public Action<byte[], int, int> H264Ready;
        private long _lastEmitAt;
        private volatile bool _running;
        private int _seq;

        /// <summary>一帧 JPEG 就绪（jpeg 数据、宽、高、序号）。在采集线程触发。</summary>
        public Action<byte[], int, int> JpegReady;

        /// <summary>采集被动停止（用户在系统里点了"停止共享"、或投影失效）。</summary>
        public Action<string> Stopped;

        public bool IsRunning { get { return _running; } }
        public int Width { get { return _outW; } }
        public int Height { get { return _outH; } }
        public int Fps { get { return _fps; } }
        public int Quality { get { return _quality; } }
        public int MaxShortSide { get { return _maxShortSide; } }
        /// <summary>当前编码模式（RdHostServer 按 QualityConfig.Mode 设置）。</summary>
        internal Protocol.EncodingMode Mode { get { return _mode; } }

        /* ===================== 生命周期 ===================== */

        /// <summary>
        /// 启动采集。必须在"mediaProjection 类型前台服务已 StartForeground"之后调用，
        /// 否则 Android 10+ 会直接拒绝 GetMediaProjection。
        /// </summary>
        public bool Start(Context ctx, int resultCode, Intent data, int maxShortSide, int fps, int quality)
        {
            Stop();
            try
            {
                _settings = ScreenShareSettings.Instance(ctx);
                lock (_lastLock) { _lastJpeg = null; }
                // 硬编码 720 / 12 / 55 全部改读集中设置；调用方不传（<=0）时用 Settings 默认值。
                if (maxShortSide > 0) _maxShortSide = maxShortSide;
                else _maxShortSide = ScreenShareSettings.FallbackShortSide;
                _fps = Settings.ClampFps(fps > 0 ? Math.Min(fps, Settings.MaxFps) : Settings.MaxFps);
                _quality = Settings.ClampQuality(quality > 0 ? quality : Settings.JpegQuality);
                Settings.LogEffective("RdScreenGrabber.Start");

                var mgr = ctx.GetSystemService(Context.MediaProjectionService) as MediaProjectionManager;
                if (mgr == null) { Log.Warn(Tag, "系统不支持屏幕录制"); return false; }
                _mp = mgr.GetMediaProjection(resultCode, data);
                if (_mp == null) { Log.Warn(Tag, "屏幕录制授权无效（授权可能已过期）"); return false; }

                _mpCb = new MediaProjectionCallback(this);
                _mp.RegisterCallback(_mpCb, GetHandler());

                // 投影上必须挂一次回调，Android 14 起"未注册回调就建虚拟屏"会抛异常
                if (!ReadMetrics(ctx)) return false;

                _thread = new HandlerThread("rd-capture");
                _thread.Start();
                _handler = new Handler(_thread.Looper);

                _running = true;
                if (!BuildSurface()) { Stop(); return false; }

                // 【为什么 · 首帧看门狗】屏幕静止时 ImageReader 不产帧，查看端连上会一直黑屏。
                // 启动后超时仍无帧就 Resize 一下触发重绘（只 Resize，不重建 VirtualDisplay）。
                try
                {
                    GetHandler().PostDelayed(new Java.Lang.Runnable(() =>
                    {
                        if (!_running || _seq > 0) return;
                        Log.Warn(Tag, "采集首帧超时，Resize 触发产帧（不重建 VD）");
                        KickFrame();
                    }), Settings.FirstFrameTimeoutMs);
                }
                catch { }

                // 屏幕旋转 / 分辨率变化时重建虚拟屏，否则画面会被拉长或裁掉
                try
                {
                    _dm = ctx.GetSystemService(Context.DisplayService) as DisplayManager;
                    if (_dm != null)
                    {
                        _dl = new DisplayListener(this);
                        _dm.RegisterDisplayListener(_dl, _handler);
                    }
                }
                catch { }

                Log.Warn(Tag, "采集已启动 " + _outW + "x" + _outH + " fps=" + _fps + " q=" + _quality);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "启动采集失败：" + ex.Message);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_h264 != null) _h264.Stop(); } catch { }
            _h264 = null;
            try { if (_dl != null && _dm != null) _dm.UnregisterDisplayListener(_dl); } catch { }
            _dl = null;
            try { if (_vd != null) _vd.Release(); } catch { }
            _vd = null;
            try { if (_reader != null) { _reader.SetOnImageAvailableListener(null, null); _reader.Close(); } } catch { }
            _reader = null;
            try { if (_mp != null) _mp.Stop(); } catch { }
            _mp = null;
            try { if (_thread != null) _thread.QuitSafely(); } catch { }
            try { if (_thread != null) _thread.Join(600); } catch { }
            _thread = null;
            _handler = null;
            _outW = _outH = 0;
        }

        /// <summary>运行中调画质（不重启采集，避免重新弹授权）。保留给旧调用方，内部复用下面四个 setter。</summary>
        public void Configure(int fps, int quality, int maxShortSide)
        {
            SetMaxFps(fps);
            SetQuality(quality);
            SetMaxShortSide(maxShortSide);
        }

        /// <summary>
        /// 重发缓存的最近一帧：查看端刚连上 / ForceKeyFrame 时立刻有画面。
        /// 【为什么 · 补首帧空操作】旧代码补首帧只 _frameSignal.Set()，而 _pendingFrame
        /// 为 null 时是空操作；ScreenStream 靠「缓存帧立刻可交付」秒出画面，这里照做。
        /// 没有缓存帧时退化为 Resize 触发产帧（首帧看门狗兜底）。
        /// </summary>
        public void Republish()
        {
            if (_mode == Protocol.EncodingMode.H264 && _h264 != null)
            {
                // H264 没有可重发的缓存帧：请求一个 IDR 让查看端立刻起播
                _h264.RequestKeyFrame();
                return;
            }
            byte[] jpeg; int w, h;
            lock (_lastLock)
            {
                jpeg = _lastJpeg; w = _lastW; h = _lastH;
            }
            if (jpeg == null)
            {
                Log.Warn(Tag, "补首帧：暂无缓存帧，交给首帧看门狗触发产帧");
                KickFrame();
                return;
            }
            var cb = JpegReady;
            if (cb != null) { try { cb(jpeg, w, h); } catch { } }
        }

        /// <summary>
        /// 触发虚拟显示产一帧：只 Resize 同尺寸，绝不重建 VirtualDisplay
        /// （对齐 ScreenStream「设置变更只 Resize/SetSurface」）。
        /// </summary>
        private void KickFrame()
        {
            try
            {
                var vd = _vd;
                if (vd == null) return;
                vd.Resize(_outW, _outH, _outDpi > 0 ? _outDpi : 320);
            }
            catch (Exception ex) { Log.Warn(Tag, "Resize 触发首帧失败：" + ex.Message); }
        }

        /// <summary>
        /// 设置编码模式。H264 用 RdH264Encoder 接管 VirtualDisplay 输出；编码器起不来时
        /// 明确回退 JpegFullFrame（RdHostServer 会记降级原因）。H265 与区域/Raw 仍只记录
        /// Mode、实际输出整帧 JPEG（两端均无 H265 解码器，不谎报能力）。
        /// </summary>
        internal void SetMode(Protocol.EncodingMode mode)
        {
            if (mode == Protocol.EncodingMode.H264)
            {
                // 未开始采集（_vd 为空）时先记住模式，BuildSurface 里再真正启动编码器
                if (_running && _vd != null && !TryStartH264())
                    mode = Protocol.EncodingMode.JpegFullFrame;
                _mode = mode;
                return;
            }
            if (_mode == Protocol.EncodingMode.H264) StopH264AndRestoreJpeg();
            _mode = mode == Protocol.EncodingMode.H265 ? Protocol.EncodingMode.JpegFullFrame : mode;
        }

        /// <summary>启动 H264 编码器并把 VirtualDisplay 输出切到它的输入 Surface。失败自动回 JPEG。</summary>
        private bool TryStartH264()
        {
            try
            {
                if (_h264 == null)
                {
                    _h264 = new RdH264Encoder();
                    _h264.H264Ready = data =>
                    {
                        var cb = H264Ready;
                        if (cb != null) { try { cb(data, _outW, _outH); } catch { } }
                    };
                }
                if (!_h264.IsRunning && !_h264.Start(_outW, _outH, _fps, 0))
                {
                    StopH264AndRestoreJpeg();
                    return false;
                }
                _vd.Surface = _h264.InputSurface;
                _h264.RequestKeyFrame();
                Log.Info(Tag, "VirtualDisplay 输出已切到 H264 编码器 " + _outW + "x" + _outH);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "切换 H264 失败：" + ex.Message);
                StopH264AndRestoreJpeg();
                return false;
            }
        }

        /// <summary>停掉编码器，把 VirtualDisplay 输出切回 ImageReader（JPEG 路径）。</summary>
        private void StopH264AndRestoreJpeg()
        {
            try { if (_h264 != null) _h264.Stop(); } catch { }
            _h264 = null;
            try { if (_vd != null && _reader != null) _vd.Surface = _reader.Surface; } catch { }
        }

        /// <summary>设置 JPEG 质量（范围改读集中设置 10..100，参考 ScreenStream 默认 80）。</summary>
        public void SetQuality(int quality)
        {
            if (quality > 0) _quality = Settings.ClampQuality(quality);
        }

        /// <summary>
        /// 设置最大帧率（范围改读集中设置 1..60）。真正参与 OnImageAvailable 的节流预算，
        /// 不再是写死的 12fps —— QualitySettings 请求的 fps 由此生效。
        /// 【为什么】改 fps 只改节流参数，绝不重建 VirtualDisplay（对齐 ScreenStream）。
        /// </summary>
        public void SetMaxFps(int fps)
        {
            if (fps > 0) _fps = Settings.ClampFps(fps);
        }

        /// <summary>设置输出短边上限；变化且正在采集时重建虚拟屏。</summary>
        public void SetMaxShortSide(int maxShortSide)
        {
            if (maxShortSide > 0 && maxShortSide != _maxShortSide)
            {
                _maxShortSide = maxShortSide;
                if (_running) { try { Rebuild(); } catch { } }
            }
        }

        /* ===================== 内部 ===================== */

        private Handler GetHandler()
        {
            if (_handler != null) return _handler;
            if (_thread == null) { _thread = new HandlerThread("rd-capture"); _thread.Start(); }
            _handler = new Handler(_thread.Looper);
            return _handler;
        }

        private bool ReadMetrics(Context ctx)
        {
            try
            {
                var dmgr = ctx.GetSystemService(Context.DisplayService) as DisplayManager;
                var disps = dmgr != null ? dmgr.GetDisplays() : null;
                var disp = (disps != null && disps.Length > 0) ? disps[0] : null;
                if (disp != null)
                {
                    var m = new DisplayMetrics();
                    disp.GetRealMetrics(m);
                    _srcW = m.WidthPixels; _srcH = m.HeightPixels; _srcDpi = (int)m.DensityDpi;
                }
            }
            catch { }
            if (_srcW <= 0 || _srcH <= 0)
            {
                try
                {
                    var m = ctx.Resources.DisplayMetrics;
                    _srcW = m.WidthPixels; _srcH = m.HeightPixels; _srcDpi = (int)m.DensityDpi;
                }
                catch { }
            }
            if (_srcW <= 0 || _srcH <= 0) return false;
            if (_srcDpi <= 0) _srcDpi = 320;
            ComputeOutputSize();
            return true;
        }

        /// <summary>短边不超过上限，长边按比例；宽高都取偶数（JPEG 编码器对偶数尺寸最省心）。</summary>
        private void ComputeOutputSize()
        {
            int shortSide = Math.Min(_srcW, _srcH);
            double scale = 1.0;
            if (_maxShortSide > 0 && shortSide > _maxShortSide) scale = (double)_maxShortSide / shortSide;
            int w = (int)Math.Round(_srcW * scale);
            int h = (int)Math.Round(_srcH * scale);
            w = Math.Max(2, w & ~1);
            h = Math.Max(2, h & ~1);
            _outW = w; _outH = h;
        }

        private bool BuildSurface()
        {
            try
            {
                // maxImages=2：留一帧给采集器、一帧给我们取，取晚了也不会阻塞产帧
                _reader = ImageReader.NewInstance(_outW, _outH, (ImageFormatType)1, 2);   // 1 = PixelFormat.RGBA_8888
                _reader.SetOnImageAvailableListener(this, _handler);
                int dpi = DpiFor(_outW);
                _vd = _mp.CreateVirtualDisplay("rd-host", _outW, _outH, dpi,
                    (DisplayFlags)((int)VirtualDisplayFlags.AutoMirror), _reader.Surface, null, _handler);
                // SetMode(H264) 可能早于 Start：这里补一次真正的编码器启动与 Surface 切换
                if (_vd != null && _mode == Protocol.EncodingMode.H264) TryStartH264();
                return _vd != null;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "创建虚拟屏失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 输出尺寸变化（改缩放 / 屏幕旋转）：只给现有 VirtualDisplay 换 Surface + Resize，
        /// 正常情况下不 Release + 重建（对齐 ScreenStream「设置变更只 Resize/换 Surface」）。
        /// 【为什么】重建 VD 会让已连接画面瞬断；改 fps/quality 更是连 Resize 都不需要。
        /// 【注意 · Xamarin 绑定】Java 的 setSurface(Surface) 在本工程绑定里是可写属性
        /// VirtualDisplay.Surface，写成 vd.SetSurface(x) 会编译不过（CS1061）。
        /// 只有尺寸真变了才换 ImageReader 的 Surface；尺寸没变只 Resize 触发重绘。
        /// 属性写入失败（部分 ROM）时回退到 Release + 复用 _mp 重建，避免永久黑屏。
        /// </summary>
        private void Rebuild()
        {
            try
            {
                ComputeOutputSize();
                var vd = _vd;
                if (vd == null) { try { BuildSurface(); } catch { } return; }
                var reader = _reader;
                if (reader != null && reader.Width == _outW && reader.Height == _outH)
                {
                    // 尺寸没变：只 Resize 触发重绘，连 Surface 都不换。
                    try { vd.Resize(_outW, _outH, DpiFor(_outW)); } catch { }
                    return;
                }
                // 尺寸变了：换一块匹配新尺寸的 ImageReader，只换 Surface/Resize，不重建 VD。
                var nr = ImageReader.NewInstance(_outW, _outH, (ImageFormatType)1, 2);
                nr.SetOnImageAvailableListener(this, _handler);
                var old = _reader;
                _reader = nr;
                bool bound = false;
                try
                {
                    vd.Surface = nr.Surface;
                    vd.Resize(_outW, _outH, DpiFor(_outW));
                    bound = true;
                }
                catch (Exception ex)
                {
                    Log.Warn(Tag, "换 Surface 失败，回退重建虚拟屏：" + ex.Message);
                }
                if (!bound)
                {
                    // 复用同一个 MediaProjection 重建（一次性授权 token 只在
                    // GetMediaProjection 时消耗，这里不会重新弹授权框），仍用刚建好的 nr。
                    try { vd.Release(); } catch { }
                    _vd = null;
                    try
                    {
                        _vd = _mp.CreateVirtualDisplay("rd-host", _outW, _outH, DpiFor(_outW),
                            (DisplayFlags)((int)VirtualDisplayFlags.AutoMirror), nr.Surface, null, _handler);
                    }
                    catch (Exception ex2) { Log.Warn(Tag, "重建虚拟屏失败：" + ex2.Message); }
                }
                try { if (old != null) { old.SetOnImageAvailableListener(null, null); old.Close(); } } catch { }
                Log.Warn(Tag, bound
                    ? L10n.T("虚拟显示已换 Surface/Resize（未重建）") + _outW + "x" + _outH
                    : L10n.T("虚拟显示已重建（换 Surface 不被支持）") + _outW + "x" + _outH);
                // 输出尺寸变了：H264 编码器必须按新尺寸重建，否则解码端会花屏
                if (_mode == Protocol.EncodingMode.H264)
                {
                    StopH264AndRestoreJpeg();
                    TryStartH264();
                }
            }
            catch (Exception ex) { Log.Warn(Tag, "重建失败：" + ex.Message); }
        }

        private int DpiFor(int w)
        {
            int d = (int)Math.Max(120, _srcDpi * (double)w / Math.Max(1, _srcW));
            _outDpi = d;
            return d;
        }

        internal void OnDisplayChanged()
        {
            try
            {
                if (!_running) return;
                // 旋转后整块屏幕的宽高互换：重新取尺寸 → 重算输出 → 重建虚拟屏
                var ctx = Android.App.Application.Context;
                int ow = _outW, oh = _outH;
                ReadMetrics(ctx);
                if (_outW == ow && _outH == oh) return;
                Rebuild();
            }
            catch { }
        }

        /// <summary>ImageReader 回调（采集线程）：取最新帧 → 压 JPEG → 回调。</summary>
        public void OnImageAvailable(ImageReader reader)
        {
            if (!_running) { try { reader.AcquireLatestImage()?.Close(); } catch { } return; }
            // H264 模式下 VirtualDisplay 已改画到编码器 Surface，ImageReader 不再产帧；
            // 切换瞬间的残留帧直接丢弃（画面由编码器的 Annex-B 流承载）。
            if (_mode == Protocol.EncodingMode.H264)
            {
                try { var stale = reader.AcquireLatestImage(); if (stale != null) stale.Close(); } catch { }
                return;
            }

            // 帧率节流：ImageReader 的产帧速度跟着屏幕刷新走（60Hz），
            // 不节流会让 JPEG 压缩把 CPU 打满，反而拖慢整机
            long now = System.Environment.TickCount;
            int budget = Math.Max(15, 1000 / Math.Max(1, _fps));
            if (_lastEmitAt != 0 && now - _lastEmitAt < budget - 4)
            {
                // 丢弃：必须把图取走并 Close，否则 ImageReader 的缓冲会被占满、停止产帧
                try { var skip = reader.AcquireLatestImage(); if (skip != null) skip.Close(); } catch { }
                return;
            }

            // 【背压 · 忙时丢帧】下游发送端还在处理上一帧时，直接丢弃本帧且不做 JPEG 压缩
            // （720p 压缩最耗 CPU）。参考 ScreenStream：latest-value 合并 + 单一在制品 +
            // 忙时丢帧；把「是否编码」交给下游背压决定，等价于编码与采集解耦，
            // 采集线程不再被压缩打满。
            var drop = ShouldDropFrame;
            if (drop != null && drop())
            {
                try { var skip2 = reader.AcquireLatestImage(); if (skip2 != null) skip2.Close(); } catch { }
                return;
            }

            Image img = null;
            Bitmap padded = null;
            Bitmap cropped = null;
            try
            {
                img = reader.AcquireLatestImage();
                if (img == null) return;
                var planes = img.GetPlanes();
                var plane = planes != null && planes.Length > 0 ? planes[0] : null;
                if (plane == null) return;
                var buf = plane.Buffer;
                if (buf == null) return;

                int w = img.Width, h = img.Height;
                int rowStride = plane.RowStride;
                if (rowStride < w * 4) rowStride = w * 4;
                int paddedW = rowStride / 4;

                // ARGB_8888 在内存里就是 RGBA 字节序，RGBA_8888 的 ImageReader 直接整块拷过来即可，
                // 不用做通道交换（这一点与 YUV 不同，写错会整屏偏蓝）
                padded = Bitmap.CreateBitmap(paddedW, h, Bitmap.Config.Argb8888);
                padded.CopyPixelsFromBuffer(buf);
                Bitmap use = padded;
                if (paddedW != w)
                {
                    cropped = Bitmap.CreateBitmap(padded, 0, 0, w, h);
                    use = cropped;
                }

                using (var ms = new MemoryStream(96 * 1024))
                {
                    if (!use.Compress(Bitmap.CompressFormat.Jpeg, _quality, ms)) return;
                    byte[] jpeg = ms.ToArray();
                    // 缓存最近一帧：查看端连上/ForceKeyFrame 时立刻可交付（ScreenStream 快出画面）。
                    lock (_lastLock) { _lastJpeg = jpeg; _lastW = w; _lastH = h; }
                    _lastEmitAt = now;
                    _seq++;
                    var cb = JpegReady;
                    if (cb != null) { try { cb(jpeg, w, h); } catch { } }
                }
            }
            catch (Exception ex)
            {
                // 单帧失败不致命：下一帧继续。常见原因是屏幕熄了 / 投影正在被系统收回。
                if (_seq % 60 == 0) Log.Warn(Tag, "取帧异常：" + ex.Message);
            }
            finally
            {
                try { if (cropped != null) cropped.Recycle(); } catch { }
                try { if (padded != null) padded.Recycle(); } catch { }
                try { if (img != null) img.Close(); } catch { }
            }
        }

        internal void OnProjectionStopped()
        {
            Log.Warn(Tag, "系统已停止屏幕录制");
            _running = false;
            var cb = Stopped;
            if (cb != null) { try { cb("系统已停止屏幕录制，请在手机端重新开启被控"); } catch { } }
        }

        /* ===================== 回调壳 ===================== */

        private sealed class MediaProjectionCallback : MediaProjection.Callback
        {
            private readonly RdScreenGrabber _host;
            public MediaProjectionCallback(RdScreenGrabber host) { _host = host; }
            public override void OnStop() { try { _host.OnProjectionStopped(); } catch { } }
        }

        private sealed class DisplayListener : Java.Lang.Object, DisplayManager.IDisplayListener
        {
            private readonly RdScreenGrabber _host;
            public DisplayListener(RdScreenGrabber host) { _host = host; }
            public void OnDisplayAdded(int displayId) { }
            public void OnDisplayRemoved(int displayId) { }
            public void OnDisplayChanged(int displayId) { try { _host.OnDisplayChanged(); } catch { } }
        }
    }
}

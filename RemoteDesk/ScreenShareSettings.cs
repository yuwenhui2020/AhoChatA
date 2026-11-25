using System;
using Android.App;
using Android.Content;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 屏幕共享 / 远控的集中设置：用 ISharedPreferences 持久化，所有硬编码的
    /// 720 / 12 / 55 / 30fps 都改为从这里读（无 UI 或调用方不传值时也有合理兜底）。
    ///
    /// 【默认值为什么这样取 —— 参考 ScreenStream 4.5.0】
    ///   · ScalePercent = 50：ScreenStream 的 outputSize 默认 ScaleFactor(0.5)，
    ///     先把像素减半再编码，是它「启动即出画面、不等首帧」的关键（像素少一半，
    ///     JPEG 压缩 / H.264 编码耗时立刻降下来），比本项目原来写死 720 短边更稳。
    ///   · JpegQuality = 80：ScreenStream ScreenCaptureParameters.jpegQuality 默认 80
    ///     （范围 0..100）。它靠质量而非分辨率调带宽，改质量只改编码参数、不重建采集。
    ///   · AutoFps = true / MaxFps = 30：ScreenStream 的 frameRate 默认 FrameRate.Auto，
    ///     不主动限帧；这里用 AutoFps 表示「不主动限帧」，MaxFps 只作为编码器标称帧率
    ///     与封顶值（1..60，取参考要点 1..120 区间的稳妥上限）。
    ///   · KeyFrameIntervalMs = 1000：参考要点 keyFrame = 1s；关键帧间隔短，
    ///     新查看端接入 / 丢帧后能快速恢复，配合 init 段即时重建避免断流。
    ///   · FirstFrameTimeoutMs = 2500：首帧看门狗超时。参考「启动不等首帧、缓存帧
    ///     立刻可交付」，但屏幕静止时 VirtualDisplay 不产帧会把编码器饿死，
    ///     超时后主动喂一帧，别让链路永远等（首帧死结的兜底）。
    ///
    /// 【最重要的一条 · 改 fps / jpegQuality 绝不重建 VirtualDisplay】
    /// ScreenStream 的设置变更只 Resize / SetSurface。本类只负责持久化 + 发变更事件，
    /// 消费方（IpCameraServer / RdScreenGrabber / RdHostServer）收到 Changed 后只能
    /// 改编码参数或 SetSurface/Resize，不得 Release + 重建 VirtualDisplay。
    /// </summary>
    public sealed class ScreenShareSettings
    {
        private const string Tag = "ScreenSettings";
        private const string PrefsName = "Mchat_screen_share_settings";

        // 范围与默认值全部集中定义；其它文件一律来这里读，避免再次散落硬编码。
        public const int DefaultScalePercent = 50;
        public const int MinScalePercent = 25;
        public const int MaxScalePercent = 100;

        public const int DefaultMaxFps = 30;
        public const int MinMaxFps = 1;
        public const int MaxMaxFps = 60;

        public const bool DefaultAutoFps = true;

        public const int DefaultJpegQuality = 80;
        public const int MinJpegQuality = 10;
        public const int MaxJpegQuality = 100;

        public const int DefaultKeyFrameIntervalMs = 1000;
        public const int DefaultFirstFrameTimeoutMs = 2500;

        /// <summary>短边兜底：50% 缩放对常见 1440 短边屏 ≈ 720；仅在没有源尺寸时使用。</summary>
        public const int FallbackShortSide = 720;

        private const string KScale = "scalePercent";
        private const string KFps = "maxFps";
        private const string KAutoFps = "autoFps";
        private const string KQuality = "jpegQuality";
        private const string KKeyFrame = "keyFrameIntervalMs";
        private const string KFirstFrame = "firstFrameTimeoutMs";

        private static ScreenShareSettings _instance;
        private static readonly object _instLock = new object();

        private readonly ISharedPreferences _prefs;
        private readonly object _lock = new object();

        private int _scalePercent;
        private int _maxFps;
        private bool _autoFps;
        private int _jpegQuality;
        private int _keyFrameIntervalMs;
        private int _firstFrameTimeoutMs;

        /// <summary>任意设置变更后触发。消费方据此只改编码参数，不重建 VirtualDisplay。</summary>
        public event Action<ScreenShareSettings> Changed;

        /// <summary>进程内单例；ctx 为空时用 Application.Context 兜底。</summary>
        public static ScreenShareSettings Instance(Context ctx = null)
        {
            lock (_instLock)
            {
                if (_instance == null)
                {
                    Context c = ctx ?? Application.Context;
                    _instance = new ScreenShareSettings(c);
                }
                return _instance;
            }
        }

        private ScreenShareSettings(Context ctx)
        {
            _prefs = ctx.ApplicationContext.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            // 读的时候也钳制一次：防止旧版本 / 手工改偏好写入过界值。
            _scalePercent = Clamp(_prefs.GetInt(KScale, DefaultScalePercent), MinScalePercent, MaxScalePercent);
            _maxFps = Clamp(_prefs.GetInt(KFps, DefaultMaxFps), MinMaxFps, MaxMaxFps);
            _autoFps = _prefs.GetBoolean(KAutoFps, DefaultAutoFps);
            _jpegQuality = Clamp(_prefs.GetInt(KQuality, DefaultJpegQuality), MinJpegQuality, MaxJpegQuality);
            _keyFrameIntervalMs = Clamp(_prefs.GetInt(KKeyFrame, DefaultKeyFrameIntervalMs), 100, 10000);
            _firstFrameTimeoutMs = Clamp(_prefs.GetInt(KFirstFrame, DefaultFirstFrameTimeoutMs), 500, 15000);
        }

        /// <summary>缩放百分比（25..100）。参考 ScreenStream 默认 50%。</summary>
        public int ScalePercent
        {
            get { lock (_lock) return _scalePercent; }
            set
            {
                int v = Clamp(value, MinScalePercent, MaxScalePercent);
                lock (_lock)
                {
                    if (_scalePercent == v) return;
                    _scalePercent = v;
                }
                try { _prefs.Edit().PutInt(KScale, v).Apply(); } catch { }
                Apply();
            }
        }

        /// <summary>最大帧率（1..60）；AutoFps=true 时不主动限帧，只作为编码器标称/封顶。</summary>
        public int MaxFps
        {
            get { lock (_lock) return _maxFps; }
            set
            {
                int v = Clamp(value, MinMaxFps, MaxMaxFps);
                lock (_lock)
                {
                    if (_maxFps == v) return;
                    _maxFps = v;
                }
                try { _prefs.Edit().PutInt(KFps, v).Apply(); } catch { }
                Apply();
            }
        }

        /// <summary>true = 不主动限帧（参考 ScreenStream 的 FrameRate.Auto）。</summary>
        public bool AutoFps
        {
            get { lock (_lock) return _autoFps; }
            set
            {
                lock (_lock)
                {
                    if (_autoFps == value) return;
                    _autoFps = value;
                }
                try { _prefs.Edit().PutBoolean(KAutoFps, value).Apply(); } catch { }
                Apply();
            }
        }

        /// <summary>JPEG 质量（10..100）。参考 ScreenStream 默认 80。</summary>
        public int JpegQuality
        {
            get { lock (_lock) return _jpegQuality; }
            set
            {
                int v = Clamp(value, MinJpegQuality, MaxJpegQuality);
                lock (_lock)
                {
                    if (_jpegQuality == v) return;
                    _jpegQuality = v;
                }
                try { _prefs.Edit().PutInt(KQuality, v).Apply(); } catch { }
                Apply();
            }
        }

        /// <summary>关键帧间隔（毫秒）。参考要点 keyFrame=1s。</summary>
        public int KeyFrameIntervalMs
        {
            get { lock (_lock) return _keyFrameIntervalMs; }
            set
            {
                int v = Clamp(value, 100, 10000);
                lock (_lock)
                {
                    if (_keyFrameIntervalMs == v) return;
                    _keyFrameIntervalMs = v;
                }
                try { _prefs.Edit().PutInt(KKeyFrame, v).Apply(); } catch { }
                Apply();
            }
        }

        /// <summary>首帧看门狗超时（毫秒）：超时无编码输出就主动喂帧。</summary>
        public int FirstFrameTimeoutMs
        {
            get { lock (_lock) return _firstFrameTimeoutMs; }
            set
            {
                int v = Clamp(value, 500, 15000);
                lock (_lock)
                {
                    if (_firstFrameTimeoutMs == v) return;
                    _firstFrameTimeoutMs = v;
                }
                try { _prefs.Edit().PutInt(KFirstFrame, v).Apply(); } catch { }
                Apply();
            }
        }

        /// <summary>把缩放百分比换算成目标短边像素（按源短边等比，且不超过 capShort）。</summary>
        public int ShortSideFromScale(int srcShort, int capShort)
        {
            if (srcShort <= 0) return FallbackShortSide;
            int s = (int)Math.Round(srcShort * (ScalePercent / 100.0));
            int max = capShort > 0 ? Math.Min(capShort, srcShort) : srcShort;
            if (s > max) s = max;
            if (s < 2) s = 2;
            return s & ~1;   // 取偶数，编码器/JPEG 对偶数尺寸最省心
        }

        /// <summary>钳制质量到本类范围（给 Bitmap.Compress / QualityConfig 用）。</summary>
        public int ClampQuality(int q) { return Clamp(q, MinJpegQuality, MaxJpegQuality); }

        /// <summary>钳制帧率到本类范围（1..60）。</summary>
        public int ClampFps(int fps) { return Clamp(fps, MinMaxFps, MaxMaxFps); }

        /// <summary>关键帧间隔（秒，给 MediaFormat.KeyIFrameInterval，至少 1 秒）。</summary>
        public int KeyFrameIntervalSec { get { return Math.Max(1, KeyFrameIntervalMs / 1000); } }

        /// <summary>
        /// 应用变更：持久化已在各 setter 完成，这里打印生效值并发事件。
        /// 【为什么】消费方收到 Changed 后只准改编码参数（码率/质量/帧率节流/请求关键帧）
        /// 或 SetSurface/Resize，绝不重建 VirtualDisplay（对齐 ScreenStream）。
        /// </summary>
        public void Apply()
        {
            LogEffective("Apply");
            var cb = Changed;
            if (cb != null) { try { cb(this); } catch (Exception ex) { Log.Warn(Tag, "变更回调异常：" + ex.Message); } }
        }

        /// <summary>把全部生效值打到 logcat：没有设置界面时也能确认设置真的落地。</summary>
        public void LogEffective(string where)
        {
            try { Log.Warn(Tag, "[" + where + "] 生效设置：" + Describe()); } catch { }
        }

        public string Describe()
        {
            return "scale=" + ScalePercent + "% maxFps=" + MaxFps + " autoFps=" + AutoFps
                + " jpegQuality=" + JpegQuality + " keyFrameMs=" + KeyFrameIntervalMs
                + " firstFrameTimeoutMs=" + FirstFrameTimeoutMs;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}

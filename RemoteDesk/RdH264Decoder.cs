using System;
using System.Collections.Generic;
using Android.Media;
using Android.Views;
using Java.Nio;

namespace AhoChatA
{
    /// <summary>
    /// 安卓侧 H.264 硬解码器（MediaCodec），直接渲染到 Surface。
    ///
    /// 【为什么渲染到 Surface 而不是转成 Bitmap】
    /// 远程桌面是持续视频流，每秒几十帧。若走"解码→读回 YUV→CPU 转 Bitmap→ImageView"，
    /// 每帧要多拷贝两次并做一次全屏色彩空间转换，在中低端机上很容易吃满 CPU。
    /// 让 MediaCodec 直接画到 SurfaceView 上，帧数据不经过应用内存，延迟和功耗都最低。
    /// 本工程 CameraViewActivity 里的解码器就是这条路，已经跑通。
    ///
    /// 【两种码流格式，自动兜底】
    /// MediaCodec 的 H.264 解码器对输入格式有约定，各家实现不完全一致：
    ///   模式 A（标准做法）：csd-0 放 **avcC 记录**，输入 buffer 用 **4 字节长度前缀** 的 NAL；
    ///   模式 B（本工程 CameraViewActivity 用的做法）：csd-0=SPS、csd-1=PPS 分开放，
    ///                       输入 buffer 直接放 **带起始码的 NAL**。
    /// 先按 A 试，若迟迟解不出画面（连续若干帧无输出）就自动切到 B。
    ///
    /// 【和 Windows 端的配合】
    /// desk 端 Media Foundation 编码出来的是 Annex-B 裸流，关键帧自带 SPS/PPS。
    /// 本类负责把它切成 NAL、按需补 csd、并按当前模式喂给解码器。
    ///
    /// 【RdSession 接入契约（v2 能力协商对齐）】
    /// 本类逻辑在本轮无需改动，唯一入口就是 Decode(byte[] annexB)；
    /// RdSession 在收到 codec==CODEC_H264 的帧（跳过 14B 帧头、以及 FLAG_QOS 时的
    /// 12B QoS 头之后）把 Annex-B 数据原样交给它。调用方约定：
    ///   · 按需创建（第一帧 H264 到达时 new），Surface 后到位由 AttachSurface 挂上；
    ///   · 分辨率变化时由调用方 Dispose() 整个实例再重建（本类内部的
    ///     OutputFormatChanged 只同步渲染尺寸，不重建 MediaCodec），
    ///     重建后靠 ReplayKeyFrame / ConsumeKeyFrameRequest 从缓存 IDR 起播；
    ///   · 会话结束 / 重连 / Dispose() 时必须释放，避免 MediaCodec 绑着旧 Surface。
    /// 已声明查看端 H264Decode 能力（Capability.H264Decode），故该路径正式启用。
    /// </summary>
    public sealed class RdH264Decoder : IDisposable
    {
        private MediaCodec _codec;
        private Surface _surface;
        private int _width;
        private int _height;
        private bool _configured;
        private bool _disposed;

        /// <summary>
        /// 配置互斥锁。
        /// Decode() 跑在读取循环的线程池线程上，AttachSurface() 跑在 UI 线程上，
        /// 两者都可能同时进 TryConfigure()。并发的 Configure/Cleanup 会让
        /// MediaCodec 抛 IllegalArgumentException（甚至把别处正在用的实例释放掉）。
        /// </summary>
        private readonly object _gate = new object();

        /// <summary>当前用的是长度前缀+avcC（A）还是起始码+分离参数集（B）。</summary>
        private bool _useLengthPrefix = true;

        /// <summary>连续多少帧没出画面就判定当前模式不对，换另一种重试。</summary>
        private const int MaxIdleFrames = 20;

        private int _idleFrames;
        private long _pts;
        private long _decodedFrames;
        private long _fedNals;
        private bool _retried;
        private long _decodeCalls;

        /// <summary>是否已经因为"配好了却一直不出帧"换过一次封装模式。</summary>
        private bool _modeSwitched;

        /// <summary>需要向电脑端要一个关键帧（换模式后必须重新从 IDR 起播）。</summary>
        private volatile bool _needKeyFrame;

        /// <summary>取走"要关键帧"的请求（取一次清零）。</summary>
        public bool ConsumeKeyFrameRequest()
        {
            if (!_needKeyFrame) return false;
            _needKeyFrame = false;
            return true;
        }

        public string LastError { get; private set; }

        public long DecodedFrames { get { return _decodedFrames; } }

        public long FedNals { get { return _fedNals; } }

        public bool Configured { get { return _configured; } }

        /// <summary>是否已经切换到备用的起始码模式（诊断用）。</summary>
        public bool UsedAnnexBMode { get { return !_useLengthPrefix; } }

        /// <summary>挂上渲染目标。必须在收到第一帧之前设置好。</summary>
        ///
        /// 【为什么允许后挂】
        /// SurfaceView 初始隐藏，Surface 是异步创建的，可能在第一帧 H.264 到达之后才有。
        /// 这时解码器还没配置成功（没有 Surface），此时挂上 Surface 要能立刻重新走配置流程，
        /// 否则会一直停在"没有 Surface"的状态里，永远出不了画面。
        /// </summary>
        public void AttachSurface(Surface surface)
        {
            // 【换了 Surface 实例必须重建解码器】
            // MediaCodec 一旦 Configure 就把输出目标绑死在某个 Surface 上。
            // SurfaceView 隐藏→显示、旋转、Activity 重建都会产生**新的** Surface，
            // 旧实例已失效。如果只把字段换掉不重建，解码器仍往废弃的 Surface 上画，
            // 表现就是解码计数一直在涨、屏幕却始终漆黑。
            bool needRebuild = _configured && !ReferenceEquals(surface, _surface);
            _surface = surface;
            LogD($"[H264] 挂上 Surface valid={surface != null && surface.IsValid} 需重建={needRebuild}");
            if (needRebuild)
            {
                lock (_gate)
                {
                    Cleanup();
                    if (surface != null && surface.IsValid && _sps != null && _pps != null)
                    {
                        if (TryConfigure()) _needKeyFrame = true;
                    }
                }
                return;
            }
            // 已经记住了参数集、但因为没有 Surface 而没配上的，现在补配。
            if (!_configured && _sps != null && _pps != null) {
                TryConfigure();
            }
        }

        /// <summary>写一条调试日志到 logcat。</summary>
        private static void LogD(string msg)
        {
            try { Android.Util.Log.Debug("Mchat", msg); } catch { }
        }

        /// <summary>记住下来的 SPS/PPS（不含起始码的裸 NAL）。</summary>
        private byte[] _sps;
        private byte[] _pps;

        /// <summary>用已记住的参数集配置解码器。</summary>
        private bool TryConfigure()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_configured) return true;
                if (_surface == null || _sps == null || _pps == null) {
                    return false;
                }
                // 【先 B 后 A】
                // 本项目 CameraViewActivity 的解码器走的是模式 B（csd-0=SPS、csd-1=PPS，
                // 输入带起始码的 NAL），在本机型上实机验证过能出画面。
                // 而模式 A（avcC + 长度前缀）虽然 Configure 能返回成功、喂数据也不报错，
                // 但实测解码器一个输出帧都吐不出来（表现为黑屏、fed 一直涨 out 恒为 0）。
                // 所以优先用 B，B 配不上再退 A。
                bool ok = ReconfigureAnnexB(_sps, _pps);
                if (!ok) {
                    LogD(L10n.T("[H264] 模式 B（起始码）配置失败，改试模式 A"));
                    ok = Configure(_sps, _pps);
                }
                LogD($"[H264] 配置结果={ok} 模式={(ok ? (_useLengthPrefix ? "A(长度前缀)" : "B(起始码)") : "失败")} {_width}x{_height} err={LastError}");
                if (ok) {
                    // 配置这一刻，当前这一帧可能已经在 configure 之前被跳过了
                    // （解码器还没配好时我们不会喂数据）。而 MediaCodec 必须先有 IDR
                    // 才能真正出画面——所以把缓存的关键帧补喂一次。
                    ReplayKeyFrame();
                }
                return ok;
            }
        }

        /// <summary>已输出过"还没拿到 SPS/PPS"的提示（避免刷屏）。</summary>
        private bool _loggedNoSps;

        /// <summary>缓存最近一个关键帧（IDR），配置成功后补喂。</summary>
        private byte[] _pendingKeyFrame;

        /// <summary>把缓存的关键帧喂进解码器。</summary>
        private void ReplayKeyFrame()
        {
            byte[] kf = _pendingKeyFrame;
            if (kf == null) return;
            _pendingKeyFrame = null;
            try {
                List<byte[]> nals = SplitAnnexB(kf);
                for (int i = 0; i < nals.Count; i++) {
                    int type = NalType(nals[i]);
                    // 只补喂真正的图像切片：参数集已在 csd 里，AUD（type 9）不喂
                    // （部分硬解码器对 AUD 敏感），SEI（type 6）也不喂。
                    if (type != 1 && type != 5) continue;
                    FeedNal(nals[i]);
                }
            }
            catch { }
        }

        /// <summary>
        /// 喂一个 Annex-B 码流包。函数内部完成拆包、喂解码器、抽帧渲染。
        /// </summary>
        public void Decode(byte[] annexB)
        {
            if (_disposed || annexB == null || annexB.Length < 5) return;
            try
            {
                List<byte[]> nals = SplitAnnexB(annexB);
                _decodeCalls++;
                if (_decodeCalls <= 3 || _decodeCalls % 60 == 0)
                    LogD(L10n.T("[H264] Decode 第 ") + _decodeCalls + L10n.T(" 次：包 ") + annexB.Length + "B → " + nals.Count + L10n.T(" 个 NAL，configured=") + _configured + " fed=" + _fedNals + " out=" + _decodedFrames);
                if (nals.Count == 0) return;

                // 先从流里认参数集：即使此刻还没有 Surface，也要把 SPS/PPS 记下来，
                // 等 Surface 到位时可以直接配置，不用等下一个关键帧。
                bool hasIdr = false;
                for (int i = 0; i < nals.Count; i++)
                {
                    int type = NalType(nals[i]);
                    if (type == 7 && _sps == null) _sps = StripStartCode(nals[i]);
                    else if (type == 8 && _pps == null) _pps = StripStartCode(nals[i]);
                    else if (type == 5) hasIdr = true;
                }
                if (hasIdr) {
                    // 关键帧：整包缓存（配置成功后要补喂）。
                    _pendingKeyFrame = annexB;
                }

                if (!_configured)
                {
                    if (_sps == null || _pps == null) {
                        if (_loggedNoSps == false) {
                            _loggedNoSps = true;
                            LogD(L10n.T("[H264] 还没拿到 SPS/PPS，等关键帧"));
                        }
                        return;   // 还没拿到参数集，等关键帧
                    }
                    if (!TryConfigure()) return;               // 没有 Surface 或配置失败，等下一包
                }

                for (int i = 0; i < nals.Count; i++)
                {
                    byte[] nal = nals[i];
                    int type = NalType(nal);
                    // 只喂图像切片：参数集(7/8)进 csd，AUD(9)/SEI(6) 不喂
                    // —— 部分硬解码器对起始码/AUD 敏感，喂进去会直接拒收整帧。
                    if (type != 1 && type != 5) continue;
                    if (!FeedNal(nal)) return;
                }
                Drain();

                // 【配好却零输出 → 换封装模式再试一次】
                // 有些解码器接受某种 csd/封装配置时不报错、也不吐帧（静默失败）。
                // 判据：已喂了足够多 NAL 却连一帧都没解出来。
                if (_configured && !_modeSwitched && _decodedFrames == 0 && _fedNals >= 60)
                {
                    _modeSwitched = true;
                    lock (_gate)
                    {
                        LogD(L10n.T("[H264] 已喂 ") + _fedNals + L10n.T(" 个 NAL 仍无输出，切换封装模式重试"));
                        Cleanup();
                        bool ok = _useLengthPrefix
                            ? ReconfigureAnnexB(_sps, _pps)
                            : Configure(_sps, _pps);
                        LogD(L10n.T("[H264] 换模式结果=") + ok + L10n.T(" 模式=") + (_useLengthPrefix ? L10n.T("A(长度前缀)") : L10n.T("B(起始码)")) + " err=" + LastError);
                        if (ok) _needKeyFrame = true;   // 新解码器必须从 IDR 起播
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                LogD(L10n.T("[H264] Decode 异常：") + ex);
            }
        }

        /// <summary>去掉 NAL 前面的起始码，只留裸 NAL 载荷（SPS/PPS 存进 csd 时用）。</summary>
        private static byte[] StripStartCode(byte[] nal)
        {
            int off = 0;
            if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) off = 4;
            else if (nal.Length > 2 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) off = 3;
            int len = nal.Length - off;
            if (len <= 0) return new byte[0];
            byte[] r = new byte[len];
            System.Buffer.BlockCopy(nal, off, r, 0, len);
            return r;
        }

        /// <summary>建解码器。csd-0 = avcC（模式 A）。sps/pps 必须是**不含起始码**的裸 NAL。</summary>
        private bool Configure(byte[] sps, byte[] pps)
        {
            try
            {
                int[] wh = ParseSpsSize(sps);
                int w = wh[0] > 0 ? wh[0] : 640;
                int h = wh[1] > 0 ? wh[1] : 360;
                byte firstByte = sps.Length > 0 ? sps[0] : (byte)0;
                LogD(L10n.T("[H264] 模式 A 配置：SPS ") + sps.Length + "B → " + w + "x" + h + L10n.T(" 首字节=0x") + firstByte.ToString("X2"));

                MediaFormat format = MediaFormat.CreateVideoFormat("video/avc", w, h);
                format.SetByteBuffer("csd-0", DirectBuffer(BuildAvcC(sps, pps)));
                format.SetInteger(MediaFormat.KeyMaxInputSize, 4 * 1024 * 1024);

                _codec = MediaCodec.CreateDecoderByType("video/avc");
                if (_surface == null)
                {
                    LastError = L10n.T("还没有可用的 Surface");
                    return false;
                }
                _codec.Configure(format, _surface, null, 0);
                _codec.Start();
                _configured = true;
                _width = w;
                _height = h;
                _useLengthPrefix = true;
                _idleFrames = 0;
                _pts = 0;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                LogD(L10n.T("[H264] 配置异常详情：") + ex);
                Cleanup();
                return false;
            }
        }

        /// <summary>换用备用的模式 B：csd-0=SPS、csd-1=PPS，输入直接喂带起始码的 NAL。</summary>
        /// 这里传进来的 sps/pps 是**裸 NAL**，需要补上起始码再写进 csd。</summary>
        private bool ReconfigureAnnexB(byte[] sps, byte[] pps)
        {
            try
            {
                Cleanup();
                int[] wh = ParseSpsSize(sps);
                int w = wh[0] > 0 ? wh[0] : 640;
                int h = wh[1] > 0 ? wh[1] : 360;

                MediaFormat format = MediaFormat.CreateVideoFormat("video/avc", w, h);
                format.SetByteBuffer("csd-0", DirectBuffer(WithStartCode(sps)));
                format.SetByteBuffer("csd-1", DirectBuffer(WithStartCode(pps)));
                LogD($"[H264] 模式 B 配置：SPS {sps.Length}B PPS {pps.Length}B → {w}x{h}");

                _codec = MediaCodec.CreateDecoderByType("video/avc");
                if (_surface == null) return false;
                _codec.Configure(format, _surface, null, 0);
                _codec.Start();
                _configured = true;
                _width = w;
                _height = h;
                _useLengthPrefix = false;
                _idleFrames = 0;
                _pts = 0;
                _retried = true;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                LogD(L10n.T("[H264] 配置异常详情：") + ex);
                Cleanup();
                return false;
            }
        }

        /// <summary>给裸 NAL 补上 4 字节起始码。</summary>
        private static byte[] WithStartCode(byte[] nal)
        {
            byte[] r = new byte[nal.Length + 4];
            r[0] = 0; r[1] = 0; r[2] = 0; r[3] = 1;
            System.Buffer.BlockCopy(nal, 0, r, 4, nal.Length);
            return r;
        }

        /// <summary>
        /// 把参数集包成 MediaCodec 能吃的 **direct** ByteBuffer。
        ///
        /// 【为什么不能用 ByteBuffer.Wrap】
        /// Wrap() 造的是 Java 堆上的非直连缓冲区，底层没有直接内存地址。
        /// MediaCodec 的 native 层用 GetDirectBufferAddress 取 csd 数据，
        /// 遇到非直连缓冲区会直接抛 IllegalArgumentException —— 表现就是
        /// 两种模式 configure 全部失败、画面永远黑屏。
        /// </summary>
        private static ByteBuffer DirectBuffer(byte[] data)
        {
            ByteBuffer buf = ByteBuffer.AllocateDirect(data.Length);
            buf.Put(data);
            buf.Rewind();                 // 归零位置，否则 native 侧读到的是空区间
            return buf;
        }

        /// <summary>把一个 NAL 送进解码器。</summary>
        /// <param name="nal">NAL 单元，**含起始码**（SplitAnnexB 的原样输出）。</param>
        private bool FeedNal(byte[] nal)
        {
            try
            {
                int index = -1;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    index = _codec.DequeueInputBuffer(2000);
                    if (index >= 0) break;
                }
                if (index < 0) return true;      // 暂时没空位，下个包再送
                Java.Nio.ByteBuffer input = _codec.GetInputBuffer(index);
                if (input == null) return false;
                input.Clear();
                if (_useLengthPrefix)
                {
                    // 模式 A：4 字节长度前缀 + 裸 NAL（起始码必须剥掉，否则会被当成数据）。
                    byte[] raw = StripStartCode(nal);
                    input.PutInt(raw.Length);
                    input.Put(raw);
                }
                else
                {
                    // 模式 B：直接喂带起始码的 NAL。
                    input.Put(nal);
                }
                _codec.QueueInputBuffer(index, 0, input.Position(), _pts, 0);
                _pts += 33333;                       // 30fps 步长（微秒）
                _fedNals++;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>抽输出帧并渲染到 Surface。</summary>
        private void Drain()
        {
            try
            {
                MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
                int guard = 0;
                while (guard++ < 64)
                {
                    int index = _codec.DequeueOutputBuffer(info, 0);
                    if (index < 0)
                    {
                        if ((MediaCodecInfoState)index == MediaCodecInfoState.OutputFormatChanged)
                        {
                            // 对端换了分辨率：同步渲染尺寸，避免画面被拉伸
                            try
                            {
                                MediaFormat of = _codec.OutputFormat;
                                int nw = of.GetInteger(MediaFormat.KeyWidth);
                                int nh = of.GetInteger(MediaFormat.KeyHeight);
                                if (nw > 0 && nh > 0) { _width = nw; _height = nh; }
                            }
                            catch { }
                            continue;
                        }
                        break;      // 没有更多输出
                    }
                    try { _codec.ReleaseOutputBuffer(index, true); } catch { }
                    _decodedFrames++;
                    _idleFrames = 0;
                    if (_decodedFrames <= 3)
                    {
                        LogD(L10n.T("[H264] 输出帧 #") + _decodedFrames + " size=" + info.Size + " offset=" + info.Offset + " flags=" + info.Flags);
                    }
                    if (_decodedFrames <= 3 || _decodedFrames % 30 == 0)
                    {
                        LogD($"[H264] 解码出第 {_decodedFrames} 帧（已喂 {_fedNals} 个 NAL）");
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                LogD(L10n.T("[H264] Drain 异常：") + ex);
            }
        }

        /// <summary>按 Annex-B 起始码把码流包拆成若干 NAL（每个 NAL 自带起始码）。</summary>
        private static List<byte[]> SplitAnnexB(byte[] data)
        {
            List<byte[]> result = new List<byte[]>();
            List<int> starts = new List<int>();
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    starts.Add(i);
                    i += 3;
                }
            }
            for (int k = 0; k < starts.Count; k++)
            {
                int s = starts[k];
                int e = (k + 1 < starts.Count) ? starts[k + 1] : data.Length;
                int len = e - s;
                if (len <= 4) continue;
                byte[] nal = new byte[len];
                System.Buffer.BlockCopy(data, s, nal, 0, len);
                result.Add(nal);
            }
            return result;
        }

        private static int NalType(byte[] nal)
        {
            int off = 0;
            if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) off = 4;
            else if (nal.Length > 2 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) off = 3;
            if (off >= nal.Length) return -1;
            return nal[off] & 0x1F;
        }

        /// <summary>
        /// 造 avcC（AVCDecoderConfigurationRecord）——模式 A 的 csd-0 要这个格式。
        /// </summary>
        private static byte[] BuildAvcC(byte[] sps, byte[] pps)
        {
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                ms.WriteByte(0x01);                                   // configurationVersion
                ms.WriteByte(sps.Length > 1 ? sps[1] : (byte)0);      // AVCProfileIndication
                ms.WriteByte(sps.Length > 2 ? sps[2] : (byte)0);      // profile_compatibility
                ms.WriteByte(sps.Length > 3 ? sps[3] : (byte)0);      // AVCLevelIndication
                ms.WriteByte(0xFF);                                   // 6bit 保留 + lengthSizeMinusOne=3
                ms.WriteByte(0xE1);                                   // 3bit 保留 + numOfSPS=1
                WriteUInt16(ms, sps.Length);
                ms.Write(sps, 0, sps.Length);
                ms.WriteByte(0x01);                                   // numOfPPS=1
                WriteUInt16(ms, pps.Length);
                ms.Write(pps, 0, pps.Length);
                return ms.ToArray();
            }
        }

        private static void WriteUInt16(System.IO.MemoryStream ms, int value)
        {
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)(value & 0xFF));
        }

        /// <summary>从 SPS 里读图像宽高（跳过 emulation prevention 字节）。</summary>
        /// <param name="sps">SPS NAL，可以是裸 NAL，也可以带起始码。</param>
        private static int[] ParseSpsSize(byte[] sps)
        {
            int[] result = new int[] { 0, 0 };
            // 找到 NAL 头（nal_ref_idc/nal_unit_type 所在字节）的位置。
            // 裸 NAL 的话第一个字节就是 NAL 头；带起始码的话要跳过起始码。
            int header = -1;
            for (int i = 0; i + 4 < sps.Length; i++)
            {
                if (sps[i] == 0 && sps[i + 1] == 0 && sps[i + 2] == 0 && sps[i + 3] == 1)
                {
                    header = i + 4;
                    break;
                }
                if (sps[i] == 0 && sps[i + 1] == 0 && sps[i + 2] == 1)
                {
                    header = i + 3;
                    break;
                }
            }
            if (header < 0) header = 0;   // 裸 NAL
            if (header + 4 > sps.Length) return result;
            try
            {
                // 跳过 NAL 头字节，并去掉防竞争字节（0x00 0x00 0x03 → 0x00 0x00），
                // 否则按位读会整体错位，宽高必然是垃圾值。
                BitReader r = new BitReader(UnescapeRbsp(sps, header + 1), 0);
                int profileIdc = r.Read(8);
                r.Read(8);                                  // constraint flags + reserved
                r.Read(8);                                  // level_idc
                r.ReadUE();                                 // seq_parameter_set_id
                int chromaFormatIdc = 1;
                if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 || profileIdc == 244 ||
                    profileIdc == 44 || profileIdc == 83 || profileIdc == 86 || profileIdc == 118 ||
                    profileIdc == 128 || profileIdc == 138 || profileIdc == 139 || profileIdc == 134)
                {
                    chromaFormatIdc = r.ReadUE();
                    if (chromaFormatIdc == 3) r.Read(1);
                    r.ReadUE();                              // bit_depth_luma_minus8
                    r.ReadUE();                              // bit_depth_chroma_minus8
                    r.Read(1);                              // qpprime_y_zero_transform_bypass_flag
                    if (r.Read(1) == 1)                     // seq_scaling_matrix_present_flag
                    {
                        int count = (chromaFormatIdc != 3) ? 8 : 12;
                        for (int i = 0; i < count; i++)
                        {
                            if (r.Read(1) == 1) SkipScalingList(r, (i < 6) ? 16 : 64);
                        }
                    }
                }
                r.ReadUE();                                 // log2_max_frame_num_minus4
                int picOrderCntType = r.ReadUE();
                if (picOrderCntType == 0)
                {
                    r.ReadUE();                             // log2_max_pic_order_cnt_lsb_minus4
                }
                else if (picOrderCntType == 1)
                {
                    r.Read(1);                              // delta_pic_order_always_zero_flag
                    r.ReadSE();                             // offset_for_non_ref_pic
                    r.ReadSE();                             // offset_for_top_to_bottom_field
                    int cycle = r.ReadUE();                 // num_ref_frames_in_pic_order_cnt_cycle
                    for (int i = 0; i < cycle; i++) r.ReadSE();
                }
                r.ReadUE();                                 // max_num_ref_frames
                r.Read(1);                                  // gaps_in_frame_num_value_allowed_flag
                int widthMbs = r.ReadUE() + 1;
                int heightMapUnits = r.ReadUE() + 1;
                int frameMbsOnly = r.Read(1);
                if (frameMbsOnly == 0) r.Read(1);           // mb_adaptive_frame_field_flag
                r.Read(1);                                  // direct_8x8_inference_flag
                int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
                if (r.Read(1) == 1)                          // frame_cropping_flag
                {
                    cropLeft = r.ReadUE();
                    cropRight = r.ReadUE();
                    cropTop = r.ReadUE();
                    cropBottom = r.ReadUE();
                }
                int cropUnitX = (chromaFormatIdc == 0) ? 1 : 2;
                int cropUnitY = ((chromaFormatIdc == 0) ? 1 : 2) * (frameMbsOnly == 0 ? 2 : 1);
                result[0] = widthMbs * 16 - cropUnitX * (cropLeft + cropRight);
                result[1] = (2 - frameMbsOnly) * heightMapUnits * 16 - cropUnitY * (cropTop + cropBottom);
            }
            catch
            {
            }
            return result;
        }

        /// <summary>去掉 RBSP 里的防竞争字节：00 00 03 → 00 00。</summary>
        private static byte[] UnescapeRbsp(byte[] data, int offset)
        {
            if (offset >= data.Length) return new byte[0];
            byte[] buf = new byte[data.Length - offset];
            int n = 0;
            int zeros = 0;
            for (int i = offset; i < data.Length; i++)
            {
                byte b = data[i];
                if (zeros >= 2 && b == 0x03)
                {
                    zeros = 0;
                    continue;                      // 丢掉这个 0x03
                }
                if (b == 0x00) zeros++; else zeros = 0;
                buf[n++] = b;
            }
            byte[] outBuf = new byte[n];
            System.Buffer.BlockCopy(buf, 0, outBuf, 0, n);
            return outBuf;
        }

        private static void SkipScalingList(BitReader r, int size)
        {
            int lastScale = 8;
            int nextScale = 8;
            for (int j = 0; j < size; j++)
            {
                if (nextScale != 0)
                {
                    int delta = r.ReadSE();            // se(v)，不是 1 位
                    nextScale = (lastScale + delta + 256) % 256;
                }
                if (nextScale != 0) lastScale = nextScale;
            }
        }

        /// <summary>最小位读取器（MSB 优先）。</summary>
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _bytePos;
            private int _bitPos;

            public BitReader(byte[] data, int byteOffset)
            {
                _data = data;
                _bytePos = byteOffset;
                _bitPos = 0;
            }

            public int Read(int count)
            {
                int value = 0;
                for (int i = 0; i < count; i++)
                {
                    if (_bytePos >= _data.Length) return value << (count - i);
                    int bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
                    value = (value << 1) | bit;
                    _bitPos++;
                    if (_bitPos == 8) { _bitPos = 0; _bytePos++; }
                }
                return value;
            }

            /// <summary>读 Exp-Golomb 无符号数 ue(v)。</summary>
            /// <remarks>
            /// H.264 的 SPS 里大量字段是变长编码（seq_parameter_set_id、log2_max_frame_num_minus4、
            /// pic_order_cnt_type、width/height 的宏块数、裁剪偏移……），
            /// 按固定 8 位读会整体错位，导致算出的分辨率变成 640x360 之类的默认值。
            /// </remarks>
            public int ReadUE()
            {
                int leadingZeros = 0;
                while (true)
                {
                    if (_bytePos >= _data.Length) return 0;
                    int bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
                    _bitPos++;
                    if (_bitPos == 8) { _bitPos = 0; _bytePos++; }
                    if (bit != 0) break;
                    leadingZeros++;
                    if (leadingZeros > 31) return 0;
                }
                if (leadingZeros == 0) return 0;
                int suffix = Read(leadingZeros);
                return (1 << leadingZeros) - 1 + suffix;
            }

            /// <summary>读 Exp-Golomb 有符号数 se(v)。</summary>
            public int ReadSE()
            {
                int k = ReadUE();
                int value = (k + 1) / 2;
                return (k % 2) == 0 ? -value : value;
            }
        }

        private void Cleanup()
        {
            try { if (_codec != null) { _codec.Stop(); } } catch { }
            try { if (_codec != null) { _codec.Release(); } } catch { }
            _codec = null;
            _configured = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }
    }
}

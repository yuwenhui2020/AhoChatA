using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using Java.Nio;

namespace AhoChatA
{
    /// <summary>
    /// 通话视频通道（自研，不依赖 WebRTC）。
    ///
    /// 为什么不用 WebRTC：Xamarin 侧没有可靠的官方绑定，且 WebRTC 需要 ICE/STUN/DTLS，
    /// 在"同一局域网直连"这个场景里纯属负担。参照 meshenger 的思路——既然双方 IP 已知，
    /// 就直接点对点传媒体流。差别只在 meshenger 用 WebRTC 是因为它有现成的 Java 库可用。
    ///
    /// 本实现：
    ///   · 采集：Camera2 → 同时出两路（本地预览 TextureView + 编码器输入 Surface）
    ///   · 编码：MediaCodec 硬编 H.264 Baseline，640x480@24fps ≈ 800kbps，1 秒一个 IDR
    ///   · 传输：UDP 分片，每片 1150 字节，12 字节头（帧号/分片号/关键帧标记）
    ///   · 恢复：丢任何一片就整帧丢弃，靠下一秒的 IDR 自动恢复（通话场景不需要重传）
    /// </summary>
    public static class CallVideoProtocol
    {
        public const int MaxPayload = 1150;
        public const int HeaderSize = 12;
        public const uint Magic = 0x4C435631;   // "LCV1"

        public static byte[] Pack(uint frameId, int index, int total, bool key, byte[] data, int offset, int count)
        {
            var buf = new byte[HeaderSize + count];
            buf[0] = (byte)'L'; buf[1] = (byte)'C'; buf[2] = 1; buf[3] = (byte)(key ? 1 : 0);
            buf[4] = (byte)(frameId >> 24); buf[5] = (byte)(frameId >> 16); buf[6] = (byte)(frameId >> 8); buf[7] = (byte)frameId;
            buf[8] = (byte)(total >> 8); buf[9] = (byte)total;
            buf[10] = (byte)(index >> 8); buf[11] = (byte)index;
            Array.Copy(data, offset, buf, HeaderSize, count);
            return buf;
        }

        public static bool Unpack(byte[] buf, int len, out uint frameId, out int index, out int total, out bool key, out int payloadOffset, out int payloadLen)
        {
            frameId = 0; index = 0; total = 0; key = false; payloadOffset = 0; payloadLen = 0;
            if (len <= HeaderSize) return false;
            if (buf[0] != 'L' || buf[1] != 'C' || buf[2] != 1) return false;
            key = (buf[3] & 1) != 0;
            frameId = ((uint)buf[4] << 24) | ((uint)buf[5] << 16) | ((uint)buf[6] << 8) | buf[7];
            total = (buf[8] << 8) | buf[9];
            index = (buf[10] << 8) | buf[11];
            if (total <= 0 || total > 20000 || index < 0 || index >= total) return false;
            payloadOffset = HeaderSize;
            payloadLen = len - HeaderSize;
            return true;
        }
    }

    /// <summary>本端视频：Camera2 采集 → 硬编 → UDP 发给对端。</summary>
    public sealed class CallVideoSender : IDisposable
    {
        // 分辨率/码率直接决定单帧被拆成多少个 UDP 包：包数越多，整帧凑齐的概率越低
        // （丢任一片就得整帧丢弃）。640x480@600kbps 下 IDR 约 20~35 片，局域网可用；
        // 再配合 PLI 补关键帧，丢包后能立刻恢复。
        private const int TargetW = 640, TargetH = 480, Fps = 24, Bitrate = 600000;

        private readonly Context _ctx;
        private readonly string _peerIp;
        private readonly int _peerPort;
        private readonly Handler _handler = new Handler(Looper.MainLooper);

        private CameraManager _cm;
        private CameraDevice _cam;
        private CameraCaptureSession _session;
        private MediaCodec _enc;
        private Surface _encSurface;
        private Surface _previewSurface;
        private SurfaceTexture _previewTex;
        private UdpClient _udp;
        private IPEndPoint _ep;

        private Thread _drain;
        private volatile bool _alive;
        private bool _front = true;
        private int _sensorOrientation;
        private int _w = TargetW, _h = TargetH;

        // 编码器输出里的 SPS/PPS（已统一成 Annex-B，带 00 00 00 01 起始码）。
        // 每个关键帧发送前都会把它们前置进去，保证对端一定拿得到参数集来初始化解码器——
        // 这正是视频「黑屏/异常」的根因：多数机型硬编输出是 4 字节长度前缀(AVCC)，
        // 没有起始码，接收端 ExtractCsd 永远抽不到 SPS/PPS，解码器起不来。
        private byte[] _sps, _pps;

        // 自适应码率：收到对端丢包反馈时降码率缓解拥塞，网络恢复后缓慢回升（抗弱网）
        private int _bitrate = Bitrate, _maxBitrate = Bitrate, _minBitrate = 200000;
        private long _lastLossTs, _lastAdaptTs;
        
        // 带宽估计：支持外部设置目标码率（自适应码率反馈）
        private int _targetBitrate = Bitrate;
        private long _lastBitrateUpdate = 0;

        public CallVideoSender(Context ctx, string peerIp, int peerPort)
        {
            _ctx = ctx.ApplicationContext ?? ctx;
            _peerIp = peerIp;
            _peerPort = peerPort;
        }

        public int Width { get { return _w; } }
        public int Height { get { return _h; } }

        /// <summary>本地预览用的 SurfaceTexture（由 CallActivity 的 TextureView 提供）。</summary>
        public void AttachPreview(SurfaceTexture tex) { _previewTex = tex; }

        public void Start()
        {
            if (_alive) return;
            _alive = true;
            try
            {
                _ep = new IPEndPoint(IPAddress.Parse(_peerIp), _peerPort);
                // 按对端地址族建套接字：直连 IPv6 对端时也能发视频
                _udp = new UdpClient(_ep.AddressFamily);
                try { if (_ep.AddressFamily == AddressFamily.InterNetworkV6) _udp.Client.DualMode = true; } catch { }
                try { _udp.Client.SendBufferSize = 256 * 1024; } catch { }

                _cm = (CameraManager)_ctx.GetSystemService(Context.CameraService);
                string id = PickCameraId(_front);
                if (id == null) { Log.Error("CallVideo", "找不到摄像头"); Dispose(); return; }

                var chars = _cm.GetCameraCharacteristics(id);
                try
                {
                    var o = chars.Get(CameraCharacteristics.SensorOrientation);
                    if (o != null) _sensorOrientation = (int)o;
                }
                catch { }

                StartEncoder();
                _cm.OpenCamera(id, new CamCallback(this), _handler);
            }
            catch (Exception ex)
            {
                Log.Error("CallVideo", "启动失败：" + ex);
                Dispose();
            }
        }

        /// <summary>前后摄切换：重建采集会话（编码器不动）。</summary>
        public void SwitchCamera()
        {
            if (!_alive) return;
            _front = !_front;
            _handler.Post(() =>
            {
                try
                {
                    CloseCamera();
                    string id = PickCameraId(_front);
                    if (id == null) return;
                    _cm.OpenCamera(id, new CamCallback(this), _handler);
                }
                catch (Exception ex) { Log.Warn("CallVideo", "切换摄像头失败：" + ex.Message); }
            });
        }

        /// <summary>
        /// 请立刻输出一个关键帧（IDR）。
        /// 对端连续丢帧后会通过信令要求补关键帧，这样画面能马上恢复，
        /// 而不必干等下一个周期 IDR（否则要黑屏/花屏最多 1 秒）。
        /// </summary>
        public void RequestKeyFrame()
        {
            try
            {
                var enc = _enc;
                if (enc == null) return;
                var p = new Bundle();
                p.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 1);
                enc.SetParameters(p);
            }
            catch (Exception ex) { Log.Warn("CallVideo", "请求关键帧失败：" + ex.Message); }
        }

        /// <summary>收到对端丢包反馈：降码率以缓解拥塞（自适应码率）。</summary>
        public void OnLossFeedback()
        {
            _lastLossTs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            int next = (int)(_bitrate * 0.7);
            if (next < _minBitrate) next = _minBitrate;
            if (next != _bitrate)
            {
                _bitrate = next;
                _targetBitrate = _bitrate; // 同步目标码率
                ApplyBitrate();
                Log.Info("CallVideo", "丢包反馈 → 降码率至 " + (_bitrate / 1000) + "kbps");
            }
        }

        /// <summary>
        /// 从外部（VoiceCallManager）设置目标码率（自适应码率反馈）。
        /// </summary>
        public void SetTargetBitrate(long targetBps)
        {
            int target = (int)Math.Max(_minBitrate, Math.Min(_maxBitrate, targetBps));
            if (target != _targetBitrate)
            {
                _targetBitrate = target;
                _lastBitrateUpdate = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                // 如果目标码率低于当前码率，立即应用；否则等待自适应回升
                if (target < _bitrate)
                {
                    _bitrate = target;
                    ApplyBitrate();
                }
                Log.Info("CallVideo", "外部设置目标码率 → " + (target / 1000) + "kbps");
            }
        }

        private void ApplyBitrate()
        {
            try
            {
                var enc = _enc;
                if (enc == null) return;
                // 运行期改码率走 Bundle + PARAMETER_KEY_VIDEO_BITRATE（MediaFormat 只用于 configure 阶段）
                var p = new Bundle();
                p.PutInt(MediaCodec.ParameterKeyVideoBitrate, _bitrate);
                enc.SetParameters(p);
            }
            catch (Exception ex) { Log.Warn("CallVideo", "调整码率失败：" + ex.Message); }
        }

        private string PickCameraId(bool front)
        {
            try
            {
                var facing = front ? LensFacing.Front : LensFacing.Back;
                foreach (var id in _cm.GetCameraIdList())
                {
                    var c = _cm.GetCameraCharacteristics(id);
                    var f = c.Get(CameraCharacteristics.LensFacing);
                    if (f != null && (int)f == (int)facing) return id;
                }
                var all = _cm.GetCameraIdList();
                return all.Length > 0 ? all[0] : null;
            }
            catch { return null; }
        }

        private void StartEncoder()
        {
            var fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _w, _h);
            fmt.SetInteger(MediaFormat.KeyBitRate, _bitrate);
            fmt.SetInteger(MediaFormat.KeyFrameRate, Fps);
            fmt.SetInteger(MediaFormat.KeyIFrameInterval, 1);
            // COLOR_FormatSurface = 0x7F000789（2130708361）；不同绑定版本常量名不一致，直接写值
            fmt.SetInteger(MediaFormat.KeyColorFormat, 2130708361);
            try { fmt.SetInteger(MediaFormat.KeyProfile, 1); } catch { }        // Baseline：手机解码兼容性最好
            try { fmt.SetInteger(MediaFormat.KeyBitrateMode, 2); } catch { }    // CBR
            try { fmt.SetInteger("low-latency", 1); } catch { }

            _enc = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
            _enc.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
            _enc.Start();
            _encSurface = _enc.CreateInputSurface();

            _drain = new Thread(DrainLoop) { IsBackground = true, Name = "call-video-tx" };
            _drain.Start();
        }

        internal void OnCameraOpened(CameraDevice dev)
        {
            try
            {
                _cam = dev;
                var targets = new List<Surface> { _encSurface };
                if (_previewTex != null)
                {
                    _previewTex.SetDefaultBufferSize(_w, _h);
                    _previewSurface = new Surface(_previewTex);
                    targets.Add(_previewSurface);
                }
                dev.CreateCaptureSession(targets, new SessionStateCallback(OnSessionReady, s =>
                {
                    Log.Error("CallVideo", "采集会话创建失败");
                }), _handler);
            }
            catch (Exception ex) { Log.Error("CallVideo", "打开会话失败：" + ex.Message); }
        }

        private void OnSessionReady(CameraCaptureSession session)
        {
            try
            {
                _session = session;
                var req = _cam.CreateCaptureRequest(CameraTemplate.Record);
                req.AddTarget(_encSurface);
                if (_previewSurface != null) req.AddTarget(_previewSurface);
                try { req.Set(CaptureRequest.ControlMode, (Java.Lang.Integer)(int)ControlMode.Auto); } catch { }
                session.SetRepeatingRequest(req.Build(), null, _handler);
                Log.Info("CallVideo", "视频发送已启动 " + _w + "x" + _h + " → " + _peerIp + ":" + _peerPort);
            }
            catch (Exception ex) { Log.Error("CallVideo", "启动采集失败：" + ex.Message); }
        }

        private void DrainLoop()
        {
            var info = new MediaCodec.BufferInfo();
            uint frameId = 0;
            while (_alive && _enc != null)
            {
                try
                {
                    long nm = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                    if (nm - _lastAdaptTs > 3000)
                    {
                        _lastAdaptTs = nm;
                        // 长时间没收到丢包反馈且未到上限 → 缓慢回升码率，吃回带宽
                        if (nm - _lastLossTs > 8000 && _bitrate < _maxBitrate)
                        {
                            _bitrate = Math.Min(_maxBitrate, (int)(_bitrate * 1.1));
                            _targetBitrate = _bitrate; // 同步目标码率
                            ApplyBitrate();
                        }
                        // 外部设置的目标码率高于当前码率时，缓慢追赶
                        else if (_targetBitrate > _bitrate && nm - _lastBitrateUpdate > 5000)
                        {
                            _bitrate = Math.Min(_targetBitrate, (int)(_bitrate * 1.05));
                            ApplyBitrate();
                        }
                    }

                    int idx = _enc.DequeueOutputBuffer(info, 8000);
                    if (idx == (int)MediaCodecInfoState.TryAgainLater) continue;
                    if (idx == (int)MediaCodecInfoState.OutputFormatChanged)
                    {
                        // 兜底：部分机型不通过 CodecConfig 帧给 SPS/PPS，而是放在 OutputFormat.csd 里
                        TryCaptureCsdFromFormat();
                        continue;
                    }
                    if (idx < 0) continue;

                    var buf = _enc.GetOutputBuffer(idx);
                    if (buf != null && info.Size > 0)
                    {
                        var data = new byte[info.Size];
                        buf.Position(info.Offset);
                        buf.Get(data, 0, info.Size);

                        // 编码器的 codec-config 帧（SPS/PPS）单独抽取，不用于显示
                        if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                        {
                            CaptureCsd(data);
                        }
                        else
                        {
                            // 统一转成 Annex-B（00 00 00 01 起始码）。多数机型硬编输出是
                            // 4 字节长度前缀(AVCC)，没有起始码，接收端按起始码找 SPS/PPS 会找不到。
                            var annexb = ToAnnexB(data);
                            bool key = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                            frameId++;
                            SendFrame(annexb, key, frameId);
                        }
                    }
                    _enc.ReleaseOutputBuffer(idx, false);
                }
                catch (Exception ex)
                {
                    Log.Warn("CallVideo", "编码读取异常：" + ex.Message);
                    break;
                }
            }
        }

        private void SendFrame(byte[] data, bool key, uint frameId)
        {
            var udp = _udp;
            if (udp == null || data == null || data.Length == 0) return;
            // 关键帧：把 SPS/PPS 前置进去，让对端每收到一个 IDR 就能（重新）初始化解码器
            byte[] payload = data;
            if (key && _sps != null && _pps != null)
            {
                payload = new byte[_sps.Length + _pps.Length + data.Length];
                int p = 0;
                Array.Copy(_sps, 0, payload, p, _sps.Length); p += _sps.Length;
                Array.Copy(_pps, 0, payload, p, _pps.Length); p += _pps.Length;
                Array.Copy(data, 0, payload, p, data.Length);
            }
            int total = (payload.Length + CallVideoProtocol.MaxPayload - 1) / CallVideoProtocol.MaxPayload;
            if (total > 20000) return;
            try
            {
                for (int i = 0; i < total; i++)
                {
                    int off = i * CallVideoProtocol.MaxPayload;
                    int len = Math.Min(CallVideoProtocol.MaxPayload, payload.Length - off);
                    var pkt = CallVideoProtocol.Pack(frameId, i, total, key, payload, off, len);
                    udp.Send(pkt, pkt.Length, _ep);
                }
            }
            catch { }
        }

        /* ---------- H.264 格式归一化（AVCC 长度前缀 → Annex-B 起始码） ---------- */

        private void CaptureCsd(byte[] cfg)
        {
            if (cfg == null || cfg.Length < 4) return;
            var annexb = ToAnnexB(cfg);
            var sps = ExtractNal(annexb, 7);
            var pps = ExtractNal(annexb, 8);
            if (sps != null) _sps = sps;
            if (pps != null) _pps = pps;
        }

        private void TryCaptureCsdFromFormat()
        {
            try
            {
                var f = _enc.OutputFormat;
                if (_sps == null) { var c0 = f.GetByteBuffer("csd-0"); if (c0 != null) _sps = NormalizeCsd(ToArray(c0)); }
                if (_pps == null) { var c1 = f.GetByteBuffer("csd-1"); if (c1 != null) _pps = NormalizeCsd(ToArray(c1)); }
            }
            catch { }
        }

        private static bool HasStartCode(byte[] d)
        {
            for (int i = 0; i + 3 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0 && (d[i + 2] == 1 || (i + 3 < d.Length && d[i + 2] == 0 && d[i + 3] == 1)))
                    return true;
            }
            return false;
        }

        private static bool IsNalStart(byte[] d, int i)
        {
            return d[i] == 0 && d[i + 1] == 0 && (d[i + 2] == 1 || (i + 3 < d.Length && d[i + 2] == 0 && d[i + 3] == 1));
        }

        /// <summary>把 AVCC（4 字节长度前缀）转成 Annex-B（00 00 00 01 起始码）。已是 Annex-B 则原样返回。</summary>
        private static byte[] ToAnnexB(byte[] src)
        {
            if (src == null || src.Length < 4) return src;
            if (HasStartCode(src)) return src;
            var outb = new List<byte>(src.Length + 16);
            int i = 0;
            while (i + 4 <= src.Length)
            {
                int len = (src[i] << 24) | (src[i + 1] << 16) | (src[i + 2] << 8) | src[i + 3];
                i += 4;
                if (len < 0 || i + len > src.Length)
                {
                    for (int k = i; k < src.Length; k++) outb.Add(src[k]);
                    break;
                }
                outb.Add(0); outb.Add(0); outb.Add(0); outb.Add(1);
                for (int k = 0; k < len; k++) outb.Add(src[i + k]);
                i += len;
            }
            return outb.ToArray();
        }

        /// <summary>从 Annex-B 字节流里抠出指定类型(type)的 NAL（含起始码），找不到返回 null。</summary>
        private static byte[] ExtractNal(byte[] d, int type)
        {
            int n = d.Length;
            for (int i = 0; i + 4 < n; i++)
            {
                if (!IsNalStart(d, i)) continue;
                int payloadStart = (d[i + 2] == 1) ? i + 3 : i + 4;
                int t = d[payloadStart] & 0x1F;
                if (t != type) continue;
                int j = payloadStart + 1;
                while (j + 3 < n && !IsNalStart(d, j)) j++;
                int end = (j + 3 < n) ? j : n;
                var outb = new byte[end - i];
                Array.Copy(d, i, outb, 0, outb.Length);
                return outb;
            }
            return null;
        }

        private static byte[] ToArray(Java.Nio.ByteBuffer bb)
        {
            if (bb == null) return null;
            bb.Rewind();
            var b = new byte[bb.Remaining()];
            bb.Get(b);
            return b;
        }

        /// <summary>把裸 csd（可能带 4 字节长度前缀，或已是起始码）规整成 Annex-B NAL。</summary>
        private static byte[] NormalizeCsd(byte[] nal)
        {
            if (nal == null || nal.Length == 0) return null;
            if (HasStartCode(nal)) return nal;
            byte[] body = nal;
            if (nal.Length > 4)
            {
                int len = (nal[0] << 24) | (nal[1] << 16) | (nal[2] << 8) | nal[3];
                if (len == nal.Length - 4)
                {
                    body = new byte[nal.Length - 4];
                    Array.Copy(nal, 4, body, 0, body.Length);
                }
            }
            var outb = new byte[body.Length + 4];
            outb[0] = 0; outb[1] = 0; outb[2] = 0; outb[3] = 1;
            Array.Copy(body, 0, outb, 4, body.Length);
            return outb;
        }

        private void CloseCamera()
        {
            try { _session?.StopRepeating(); } catch { }
            try { _session?.Close(); } catch { }
            _session = null;
            try { _cam?.Close(); } catch { }
            _cam = null;
        }

        public void Dispose()
        {
            _alive = false;
            try { _drain?.Join(400); } catch { }
            CloseCamera();
            try { _enc?.Stop(); } catch { }
            try { _enc?.Release(); } catch { }
            _enc = null;
            try { _encSurface?.Release(); } catch { }
            _encSurface = null;
            try { _previewSurface?.Release(); } catch { }
            _previewSurface = null;
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        private sealed class CamCallback : CameraDevice.StateCallback
        {
            private readonly CallVideoSender _o;
            public CamCallback(CallVideoSender o) { _o = o; }
            public override void OnOpened(CameraDevice camera) { _o.OnCameraOpened(camera); }
            public override void OnDisconnected(CameraDevice camera) { try { camera.Close(); } catch { } }
            public override void OnError(CameraDevice camera, CameraError error)
            {
                try { camera.Close(); } catch { }
                Log.Error("CallVideo", "摄像头错误：" + error);
            }
        }
    }

    /// <summary>对端视频：收 UDP 分片 → 组帧 → 硬解 → 渲染到 Surface。</summary>
    public sealed class CallVideoReceiver : IDisposable
    {
        private readonly int _port;
        private readonly Handler _handler = new Handler(Looper.MainLooper);

        private UdpClient _udp;
        private Thread _recv, _decode;
        private volatile bool _alive;

        private MediaCodec _dec;
        private Surface _surface;
        private int _w = 640, _h = 480;

        private readonly object _lock = new object();
        private readonly Dictionary<uint, Frame> _frames = new Dictionary<uint, Frame>();
        private Frame _ready;         // 待解码的最新整帧
        private uint _lastDecoded;
        private int _fps, _fpsCount;
        private DateTime _fpsAt = DateTime.Now;

        // —— 组帧健壮性（修掉原实现的致命缺陷）——
        // 原实现里「组不全的帧」永远不会移除（只有组满才 Remove），累积到 6 帧后
        // 又用 _frames.Clear() 把所有正在组装的帧一起清掉。于是只要丢过一次包，
        // 后续每一帧都会被反复清空，视频再也出不来（表现为接通后黑屏或极卡）。
        // 现改为：单帧超时淘汰 + 容量满时只丢最老的一帧；并统计连续丢帧，
        // 触发 PLI 请对端立刻补发关键帧（参照 WebRTC / meshenger 的丢包恢复做法）。
        private const int MaxPendingFrames = 8;      // 同时组装的帧数上限
        private const int FrameTimeoutMs = 300;      // 单帧组装超时（超过即丢弃该帧）
        private const int PliAfterLost = 3;          // 连续丢这么多帧就请求关键帧
        private int _lostFrames;

        // 丢包统计（用于自适应码率反馈）：expected=应收到分片数，received=实收
        private long _expectedParts, _receivedParts;
        private long _lastLossReport;
        // 解码停滞看门狗：输入在流动但长时间无输出 → 请对端补关键帧
        private long _lastInputTs, _lastOutputTs, _lastStallPli;
        
        // 带宽估计反馈：接收端定期评估丢包率并请求发送端调整码率
        private long _lastLossReportMs = 0;

        /// <summary>连续丢帧、需要请对端立刻补一个关键帧（IDR）时触发。</summary>
        public event Action OnNeedKeyFrame;

        /// <summary>丢包率超阈值时触发，请本端通知对端降码率（自适应码率反馈）。</summary>
        public event Action OnVideoLoss;

        /// <summary>
        /// 收到发送端的目标码率设置（自适应码率反馈）。
        /// </summary>
        public void SetTargetBitrate(long targetBps)
        {
            // 接收端不直接改码率，而是通过信令通知发送端
            // 这里可以记录用于统计
            Log.Info("CallVideo", "接收端收到目标码率建议: " + (targetBps / 1000) + "kbps");
        }

        public int Fps { get { return _fps; } }
        public bool RenderReady { get { return _surface != null; } }

        public CallVideoReceiver(int port) { _port = port; }

        public void AttachSurface(Surface surface)
        {
            // surface 变了要重建解码器；顺手把旧的真正释放掉，避免解码器泄漏。
            var old = _dec;
            _surface = surface;
            _dec = null;
            if (old != null) { try { old.Release(); } catch { } }
        }

        public void Start()
        {
            if (_alive) return;
            _alive = true;
            try
            {
                // 优先双栈绑定：同一个视频端口同时接收 IPv4 与 IPv6，兼容直连 IPv6 对端
                if (Socket.OSSupportsIPv6)
                {
                    try
                    {
                        var u6 = new UdpClient(AddressFamily.InterNetworkV6);
                        u6.Client.DualMode = true;
                        u6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, _port));
                        _udp = u6;
                    }
                    catch { _udp = null; }
                }
                if (_udp == null) _udp = new UdpClient(_port);
                try { _udp.Client.ReceiveBufferSize = 512 * 1024; } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("CallVideo", "视频端口 " + _port + " 绑定失败：" + ex.Message + "（可能上一通未释放）");
                _alive = false;
                return;
            }

            _recv = new Thread(RecvLoop) { IsBackground = true, Name = "call-video-rx" };
            _recv.Start();
            _decode = new Thread(DecodeLoop) { IsBackground = true, Name = "call-video-dec" };
            _decode.Start();

            // 启动即请求一次关键帧，缩短首帧延迟（对端可能刚建立链路还没吐 IDR）
            try { OnNeedKeyFrame?.Invoke(); } catch { }
        }

        private void RecvLoop()
        {
            var ep = new IPEndPoint(
                _udp != null && _udp.Client.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (_alive && _udp != null)
            {
                byte[] data;
                try { data = _udp.Receive(ref ep); }
                catch { break; }
                if (data == null) continue;

                uint fid; int idx, total; bool key; int off, len;
                if (!CallVideoProtocol.Unpack(data, data.Length, out fid, out idx, out total, out key, out off, out len)) continue;

                lock (_lock)
                {
                    long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

                    // ① 超时淘汰：丢过一片的帧永远组不满，必须按时间清掉；
                    //    否则它们会一直占着字典，进而触发整体 Clear 把新帧也清掉。
                    if (_frames.Count > 0)
                    {
                        List<uint> dead = null;
                        foreach (var kv in _frames)
                        {
                            if (now - kv.Value.FirstSeen > FrameTimeoutMs)
                                (dead ?? (dead = new List<uint>())).Add(kv.Key);
                        }
                        if (dead != null)
                        {
                            foreach (var id in dead) { _frames.Remove(id); _lostFrames++; }
                        }
                    }

                    Frame f;
                    if (!_frames.TryGetValue(fid, out f))
                    {
                        // ② 容量满时只丢最老的一帧，绝不整体 Clear
                        if (_frames.Count >= MaxPendingFrames)
                        {
                            uint oldest = 0; long oldestAt = long.MaxValue;
                            foreach (var kv in _frames)
                            {
                                if (kv.Value.FirstSeen < oldestAt) { oldestAt = kv.Value.FirstSeen; oldest = kv.Key; }
                            }
                            _frames.Remove(oldest);
                            _lostFrames++;
                        }
                        f = new Frame { Id = fid, Total = total, Parts = new byte[total][], Key = key, FirstSeen = now };
                        _frames[fid] = f;
                        _expectedParts += total;
                    }
                    if (idx >= f.Parts.Length) continue;

                    if (f.Parts[idx] == null)
                    {
                        var part = new byte[len];
                        Array.Copy(data, off, part, 0, len);
                        f.Parts[idx] = part;
                        f.Got++;
                        _receivedParts++;
                    }

                    if (f.Got == f.Total)
                    {
                        _frames.Remove(fid);
                        if (f.Id > _lastDecoded)
                        {
                            // ③ 组帧：按各分片实际长度拼接
                            int cap = 0;
                            for (int i = 0; i < f.Total; i++) cap += f.Parts[i] == null ? 0 : f.Parts[i].Length;
                            var frame = new byte[cap];
                            int pos = 0; bool ok = true;
                            for (int i = 0; i < f.Total; i++)
                            {
                                if (f.Parts[i] == null) { ok = false; break; }
                                Array.Copy(f.Parts[i], 0, frame, pos, f.Parts[i].Length);
                                pos += f.Parts[i].Length;
                            }
                            if (ok && pos > 0)
                            {
                                f.Data = frame;
                                _ready = f;   // 只留最新帧：解码跟不上时自动丢旧帧（低延迟优先）
                                _lostFrames = 0;
                            }
                            else _lostFrames++;
                        }
                        else
                        {
                            _lostFrames = 0;   // 迟到的旧帧，不算丢帧
                        }
                    }

                    // ⑤ 每秒评估一次丢包率，超阈值就通知对端降码率（自适应码率反馈）
                    CheckLossFeedback(now);
                }

                // ④ 连续丢帧：请对端立刻补一个 IDR，不等下一个周期关键帧
                if (_lostFrames >= PliAfterLost)
                {
                    _lostFrames = 0;
                    try { OnNeedKeyFrame?.Invoke(); } catch { }
                }
            }
        }

        /// <summary>每秒评估一次丢包率，超阈值就通知对端降码率（自适应码率反馈）。</summary>
        private void CheckLossFeedback(long nowMs)
        {
            if (nowMs - _lastLossReportMs < 1000) return;   // 每秒最多评估一次
            _lastLossReportMs = nowMs;
            if (_expectedParts <= 0) return;
            double loss = 1.0 - (double)_receivedParts / _expectedParts;
            _expectedParts = 0;
            _receivedParts = 0;
            if (loss > 0.12)   // 丢包率超 12% → 通知对端降码率
            {
                try { OnVideoLoss?.Invoke(); } catch { }
            }
        }

        private void DecodeLoop()
        {
            var info = new MediaCodec.BufferInfo();
            while (_alive)
            {
                Frame f = null;
                lock (_lock)
                {
                    if (_ready != null) { f = _ready; _ready = null; }
                }
                if (f == null) { Thread.Sleep(4); continue; }
                if (_surface == null) { Thread.Sleep(20); continue; }

                try
                {
                    if (_dec == null)
                    {
                        byte[] sps, pps;
                        if (!ExtractCsd(f.Data, out sps, out pps)) continue;   // 等带 SPS/PPS 的关键帧
                        if (!InitDecoder(sps, pps)) continue;
                    }

                    int idx = _dec.DequeueInputBuffer(4000);
                    if (idx >= 0)
                    {
                        var ib = _dec.GetInputBuffer(idx);
                        if (ib != null && f.Data.Length <= ib.Capacity())
                        {
                            ib.Clear();
                            ib.Put(f.Data);
                            _dec.QueueInputBuffer(idx, 0, f.Data.Length, (long)(f.Id * 1000L * 1000L / 24), 0);
                            _lastDecoded = f.Id;
                            _lastInputTs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                        }
                    }

                    DrainOutput(info);

                    // 解码停滞看门狗：输入在流动但长时间无输出（解码器卡在等 IDR）→ 请对端补关键帧
                    long nm = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                    if (_dec != null && _surface != null && (nm - _lastInputTs) < 2000 && (nm - _lastOutputTs) > 1500)
                    {
                        if (nm - _lastStallPli > 1000)
                        {
                            _lastStallPli = nm;
                            try { OnNeedKeyFrame?.Invoke(); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CallVideo", "解码异常：" + ex.Message);
                    try { _dec?.Release(); } catch { }
                    _dec = null;
                }
            }
        }

        private void DrainOutput(MediaCodec.BufferInfo info)
        {
            var dec = _dec;
            if (dec == null) return;
            int guard = 0;
            while (true)
            {
                int o = dec.DequeueOutputBuffer(info, 0);
                if (o == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    try
                    {
                        var fmt = dec.OutputFormat;
                        int w = fmt.GetInteger(MediaFormat.KeyWidth);
                        int h = fmt.GetInteger(MediaFormat.KeyHeight);
                        if (w > 0 && h > 0) { _w = w; _h = h; }
                    }
                    catch { }
                    continue;
                }
                if (o == (int)MediaCodecInfoState.OutputBuffersChanged) continue;
                if (o < 0) break;
                try { dec.ReleaseOutputBuffer(o, true); } catch { }
                _fpsCount++;
                _lastOutputTs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                var now = DateTime.Now;
                if ((now - _fpsAt).TotalMilliseconds >= 1000)
                {
                    _fps = _fpsCount; _fpsCount = 0; _fpsAt = now;
                }
                if (++guard > 32) break;
            }
        }

        private bool InitDecoder(byte[] sps, byte[] pps)
        {
            try
            {
                var fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _w, _h);
                fmt.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(sps));
                fmt.SetByteBuffer("csd-1", Java.Nio.ByteBuffer.Wrap(pps));
                var dec = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc);
                dec.Configure(fmt, _surface, null, 0);
                dec.Start();
                _dec = dec;
                Log.Info("CallVideo", "视频解码器已启动 " + _w + "x" + _h);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("CallVideo", "视频解码器初始化失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>从 Annex-B 帧里挑出 SPS(7) / PPS(8)。</summary>
        private static bool ExtractCsd(byte[] d, out byte[] sps, out byte[] pps)
        {
            sps = null; pps = null;
            var marks = new List<int>();
            for (int i = 0; i + 3 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) marks.Add(i);
            }
            for (int m = 0; m < marks.Count; m++)
            {
                int start = marks[m];
                int end = m + 1 < marks.Count ? marks[m + 1] : d.Length;
                int hdr = start + 3;
                if (hdr >= d.Length) continue;
                int type = d[hdr] & 0x1F;
                if (type != 7 && type != 8) continue;
                var nal = new byte[end - start];
                Array.Copy(d, start, nal, 0, nal.Length);
                if (type == 7) sps = nal; else pps = nal;
            }
            return sps != null && pps != null;
        }

        public void Dispose()
        {
            _alive = false;
            try { _udp?.Close(); } catch { }
            _udp = null;
            try { _recv?.Join(300); } catch { }
            try { _decode?.Join(500); } catch { }
            try { _dec?.Stop(); } catch { }
            try { _dec?.Release(); } catch { }
            _dec = null;
            _surface = null;
            lock (_lock) { _frames.Clear(); _ready = null; }
        }

        private sealed class Frame
        {
            public uint Id;
            public int Total;
            public int Got;
            public bool Key;
            public byte[][] Parts;
            public byte[] Data;
            /// <summary>首片到达时刻（毫秒），用于超时淘汰未组满的帧。</summary>
            public long FirstSeen;
        }
    }
}

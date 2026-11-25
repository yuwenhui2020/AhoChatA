using System;
using System.Collections.Generic;
using System.Threading;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;

namespace AhoChatA
{
    /// <summary>
    /// 安卓被控端 H.264 硬编码器：MediaCodec(COLOR_FormatSurface) → VirtualDisplay 直接绘制
    /// → drain Annex-B 裸流（关键帧自带 SPS/PPS）→ H264Ready 回调交给 RdHostServer 组帧。
    ///
    /// 编码参数与 AVCC→Annex-B 归一化直接复用工程里已验证的 CallVideoEngine / IpCameraServer
    /// 写法（Baseline + CBR + low-latency + I 帧间隔 1）。本类自包含，启动/编码任何一步失败
    /// 都通过 Start() 返回 false 让 RdScreenGrabber 降级回整帧 JPEG，绝不影响已连接会话。
    /// </summary>
    internal sealed class RdH264Encoder : IDisposable
    {
        private const string Tag = "RdH264Enc";
        // COLOR_FormatSurface = 0x7F000789；不同 Xamarin 绑定版本常量名不一致，直接写值
        private const int ColorFormatSurface = 2130708361;

        private MediaCodec _enc;
        private Thread _drain;
        private volatile bool _alive;
        private byte[] _sps, _pps;
        private int _fps;

        /// <summary>一帧 Annex-B H.264 就绪（关键帧已前置 SPS/PPS）。在 drain 线程触发。</summary>
        public Action<byte[]> H264Ready;

        /// <summary>给 VirtualDisplay 用的输入 Surface。Start 成功后才非 null。</summary>
        public Surface InputSurface { get; private set; }

        public bool IsRunning { get { return _alive && _enc != null; } }

        /// <summary>启动编码器。返回 false 表示本机不支持，调用方应降级 JPEG。</summary>
        public bool Start(int w, int h, int fps, int bitrateKbps)
        {
            Stop();
            try
            {
                _fps = fps > 0 ? Math.Min(60, fps) : 30;
                int bitrate = bitrateKbps > 0
                    ? bitrateKbps * 1000
                    : Math.Max(400000, w * h * 3 / 4);   // 约 0.3bps/px 起步，ABR 再调

                MediaFormat fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, w, h);
                fmt.SetInteger(MediaFormat.KeyBitRate, bitrate);
                fmt.SetInteger(MediaFormat.KeyFrameRate, _fps);
                fmt.SetInteger(MediaFormat.KeyIFrameInterval, 1);  // 每秒一个 IDR，接入秒开
                fmt.SetInteger(MediaFormat.KeyColorFormat, ColorFormatSurface);
                try { fmt.SetInteger(MediaFormat.KeyProfile, 1); } catch { }        // Baseline：兼容性最好
                try { fmt.SetInteger(MediaFormat.KeyBitrateMode, 2); } catch { }    // CBR
                try { fmt.SetInteger("low-latency", 1); } catch { }

                _enc = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
                _enc.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
                _enc.Start();
                InputSurface = _enc.CreateInputSurface();
                if (InputSurface == null) throw new Exception("CreateInputSurface 返回 null");

                _alive = true;
                _drain = new System.Threading.Thread(DrainLoop) { IsBackground = true, Name = "rd-h264-enc" };
                _drain.Start();
                Log.Info(Tag, "H264 编码器已启动 " + w + "x" + h + " fps=" + _fps + " bitrate=" + bitrate);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "H264 编码器启动失败：" + ex.Message);
                Stop();
                return false;
            }
        }

        /// <summary>请求一个 IDR（查看端刚连上 / 解码器重建时立刻出画面）。</summary>
        public void RequestKeyFrame()
        {
            try
            {
                var p = new Bundle();
                p.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 1);
                MediaCodec enc = _enc;
                if (enc != null) enc.SetParameters(p);
            }
            catch { }
        }

        private void DrainLoop()
        {
            var info = new MediaCodec.BufferInfo();
            while (_alive && _enc != null)
            {
                try
                {
                    int idx = _enc.DequeueOutputBuffer(info, 20000);
                    if (idx == (int)MediaCodecInfoState.TryAgainLater) continue;
                    if (idx == (int)MediaCodecInfoState.OutputFormatChanged) { TryCsdFromFormat(); continue; }
                    if (idx < 0) continue;   // OutputBuffersChanged / 其它负状态：下一轮再来

                    Java.Nio.ByteBuffer buf = _enc.GetOutputBuffer(idx);
                    if (buf != null && info.Size > 0)
                    {
                        var data = new byte[info.Size];
                        buf.Position(info.Offset);
                        buf.Get(data, 0, info.Size);

                        if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                        {
                            CaptureCsd(data);   // SPS/PPS 配置帧，不单独当画面发
                        }
                        else
                        {
                            byte[] annexb = ToAnnexB(data);
                            bool key = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                            byte[] payload = (key && _sps != null && _pps != null)
                                ? Concat(_sps, _pps, annexb) : annexb;
                            var cb = H264Ready;
                            if (cb != null) { try { cb(payload); } catch { } }
                        }
                    }
                    _enc.ReleaseOutputBuffer(idx, false);
                }
                catch (Exception ex)
                {
                    if (_alive) Log.Warn(Tag, "编码读取异常：" + ex.Message);
                    break;
                }
            }
        }

        public void Stop()
        {
            _alive = false;
            try { if (_drain != null) _drain.Join(500); } catch { }
            _drain = null;
            try { if (_enc != null) _enc.Stop(); } catch { }
            try { if (_enc != null) _enc.Release(); } catch { }
            _enc = null;
            InputSurface = null;
            _sps = null;
            _pps = null;
        }

        public void Dispose()
        {
            Stop();
        }

        /* ===================== H.264 格式归一化（复用 CallVideoEngine） ===================== */

        private void CaptureCsd(byte[] cfg)
        {
            if (cfg == null || cfg.Length < 4) return;
            byte[] annexb = ToAnnexB(cfg);
            byte[] sps = ExtractNal(annexb, 7);
            byte[] pps = ExtractNal(annexb, 8);
            if (sps != null) _sps = sps;
            if (pps != null) _pps = pps;
        }

        private void TryCsdFromFormat()
        {
            try
            {
                MediaFormat f = _enc.OutputFormat;
                if (_sps == null)
                {
                    var c0 = f.GetByteBuffer("csd-0");
                    if (c0 != null) _sps = ToAnnexB(ToArray(c0));
                }
                if (_pps == null)
                {
                    var c1 = f.GetByteBuffer("csd-1");
                    if (c1 != null) _pps = ToAnnexB(ToArray(c1));
                }
            }
            catch { }
        }

        private static byte[] ToArray(Java.Nio.ByteBuffer bb)
        {
            try
            {
                int len = bb.Remaining();
                var b = new byte[len];
                bb.Get(b, 0, len);
                return b;
            }
            catch { return null; }
        }

        private static bool HasStartCode(byte[] d)
        {
            return d != null && d.Length >= 3 && d[0] == 0 && d[1] == 0 && d[2] == 1;
        }

        /// <summary>AVCC（4 字节长度前缀）→ Annex-B（00 00 00 01）。已是 Annex-B 则原样返回。</summary>
        private static byte[] ToAnnexB(byte[] src)
        {
            if (src == null || src.Length < 4) return src;
            if (HasStartCode(src)) return src;
            var output = new List<byte>(src.Length + 16);
            int i = 0;
            while (i + 4 <= src.Length)
            {
                int len = (src[i] << 24) | (src[i + 1] << 16) | (src[i + 2] << 8) | src[i + 3];
                i += 4;
                if (len <= 0 || i + len > src.Length)
                {
                    for (int k = i; k < src.Length; k++) output.Add(src[k]);
                    break;
                }
                output.Add(0); output.Add(0); output.Add(0); output.Add(1);
                for (int k = 0; k < len; k++) output.Add(src[i + k]);
                i += len;
            }
            return output.ToArray();
        }

        /// <summary>从 Annex-B 里抠出指定 NAL type 的完整 NAL（含起始码），找不到返回 null。</summary>
        private static byte[] ExtractNal(byte[] d, int type)
        {
            if (d == null) return null;
            int n = d.Length;
            for (int i = 0; i + 3 < n; i++)
            {
                if (!(d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)) continue;
                int nalStart = i + 3;
                if (nalStart >= n) break;
                if ((d[nalStart] & 0x1F) != type) continue;
                int end = n;
                for (int j = nalStart + 1; j + 2 < n; j++)
                {
                    if (d[j] == 0 && d[j + 1] == 0 && d[j + 2] == 1) { end = j; break; }
                }
                var nal = new byte[end - i];
                Array.Copy(d, i, nal, 0, nal.Length);
                return nal;
            }
            return null;
        }

        private static byte[] Concat(byte[] a, byte[] b, byte[] c)
        {
            var r = new byte[a.Length + b.Length + c.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            Array.Copy(c, 0, r, a.Length + b.Length, c.Length);
            return r;
        }
    }
}

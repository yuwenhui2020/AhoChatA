using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Encoding = System.Text.Encoding;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using Java.Nio;

namespace AhoChatA
{
    /// <summary>
    /// 实时查看端：连入对方摄像头 HTTP 流（video/h264 裸流），用 MediaCodec 硬解 + TextureView 渲染，低延迟。
    /// 同时支持用户旋转，画面朝向以发送端广播的 rot 为准。
    /// </summary>
    [Activity(Label = "@string/act_camera_view", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class CameraViewActivity : BaseActivity, TextureView.ISurfaceTextureListener
    {
        private string _ip;
        private int _port, _rot, _w = 1280, _h = 720;
        private TextureView _tex;
        private TextView _stats, _title;
        private Android.Graphics.SurfaceTexture _surfTex;
        private Surface _surface;

        private volatile bool _alive = true;
        private Thread _reader;
        private Socket _socket;
        private MediaCodec _decoder;
        private Timer _statsTimer;
        private readonly object _decLock = new object();

        private int _userRot;
        private long _frameIndex;
        private int _fps;
        private DateTime _fpsT0 = DateTime.Now;
        private int _fpsCount;

        private int Dp(int v) => IosStyle.Dp(this, v);

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_cameraview);
            try { Window.AddFlags(WindowManagerFlags.KeepScreenOn); } catch { }   // 看监控时别息屏

            _ip = Intent.GetStringExtra("ip") ?? "";
            _port = Intent.GetIntExtra("port", 7790);
            _rot = Intent.GetIntExtra("rot", 0);
            _w = Intent.GetIntExtra("w", 1280);
            _h = Intent.GetIntExtra("h", 720);
            string name = Intent.GetStringExtra("name") ?? _ip;

            _tex = FindViewById<TextureView>(Resource.Id.cvTex);
            _tex.SurfaceTextureListener = this;
            _stats = FindViewById<TextView>(Resource.Id.cvStats);
            _title = FindViewById<TextView>(Resource.Id.cvTitle);
            _title.Text = name + "  ·  " + _ip;

            var backBox = FindViewById<FrameLayout>(Resource.Id.cvBack);
            if (backBox != null)
            {
                backBox.AddView(new IconView(this, IconKind.Back) { IconColor = unchecked((int)0xFFFFFFFF) },
                    new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
                backBox.Click += (s, e) => Finish();
            }
            var rotBox = FindViewById<FrameLayout>(Resource.Id.cvRotate);
            if (rotBox != null)
            {
                rotBox.AddView(new IconView(this, IconKind.Rotate) { IconColor = unchecked((int)0xFFFFFFFF) },
                    new FrameLayout.LayoutParams(Dp(20), Dp(20)) { Gravity = GravityFlags.Center });
                rotBox.Click += (s, e) =>
                {
                    _userRot = (_userRot + 90) % 360;
                    ApplyTransform();
                };
            }

            // 状态刷新
            _statsTimer = new Timer(_ =>
            {
                try { RunOnUiThread(UpdateStats); } catch { }
            }, null, 1000, 1000);
        }

        public override void Finish()
        {
            _alive = false;
            try { _statsTimer?.Dispose(); } catch { }
            _statsTimer = null;
            try { _socket?.Close(); } catch { }
            lock (_decLock)
            {
                if (_decoder != null) { try { _decoder.Stop(); } catch { } try { _decoder.Release(); } catch { } _decoder = null; }
            }
            try { _surface?.Release(); } catch { }
            base.Finish();
        }

        protected override void OnDestroy()
        {
            _alive = false;
            try { _statsTimer?.Dispose(); } catch { }
            _statsTimer = null;
            try { _socket?.Close(); } catch { }
            lock (_decLock)
            {
                if (_decoder != null) { try { _decoder.Stop(); } catch { } try { _decoder.Release(); } catch { } _decoder = null; }
            }
            try { _surface?.Release(); } catch { }
            _surface = null;
            base.OnDestroy();
        }

        /* --------- SurfaceTextureListener --------- */
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            _surfTex = surface;
            _surface = new Surface(surface);
            ApplyTransform();
            if (_reader == null)
            {
                _reader = new Thread(ReaderLoop) { IsBackground = true };
                _reader.Start();
            }
        }
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) => ApplyTransform();
        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            _alive = false;
            try { _socket?.Close(); } catch { }
            return true;
        }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

        private void ApplyTransform()
        {
            if (_tex == null || _tex.Width == 0) return;
            int deg = (_rot + _userRot) % 360;
            float vw = _tex.Width, vh = _tex.Height;
            var m = new Matrix();
            m.PostRotate(deg, vw / 2, vh / 2);
            float scale = (deg == 90 || deg == 270) ? System.Math.Max(vw / _h, vh / _w) : System.Math.Max(vw / _w, vh / _h);
            m.PostScale(scale, scale, vw / 2, vh / 2);
            _tex.SetTransform(m);
        }

        private void UpdateStats()
        {
            if (_stats == null) return;
            int now = _fps;
            _stats.Text = _w + "x" + _h + L10n.T("  ·  解码 ") + now + L10n.T(" fps  ·  实时低延迟");
        }

        /* --------- 读取 + 解码 --------- */
        private void ReaderLoop()
        {
            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.Connect(new IPEndPoint(IPAddress.Parse(_ip), _port));
                var ns = new NetworkStream(_socket, true);

                // 发送 HTTP GET（显式要裸流：根路径 "/" 现在是给浏览器看的播放页）
                string req = "GET /stream.h264 HTTP/1.1\r\nHost: " + _ip + "\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                var rb = Encoding.ASCII.GetBytes(req);
                ns.Write(rb, 0, rb.Length);

                // 读响应头直到 \r\n\r\n
                if (!ReadHeaders(ns))
                {
                    RunOnUiThread(() => L10n.Toast(this, L10n.T("连接失败：服务未提供视频流"), ToastLength.Short).Show());
                    Finish();
                    return;
                }

                RunOnUiThread(() => { if (_stats != null) _stats.Text = L10n.T("已连接，等待画面…"); });

                // 读 body：Annex-B H.264 裸流
                var feeder = new NalFeeder();
                byte[] tmp = new byte[65536];
                byte[] sps = null, pps = null;
                bool decoderReady = false;
                bool needReinit = false;

                while (_alive)
                {
                    int r = ns.Read(tmp, 0, tmp.Length);
                    if (r <= 0) break; // 对端断开
                    feeder.Push(tmp, r);

                    byte[] nal;
                    while ((nal = feeder.NextNal()) != null)
                    {
                        int type = NalType(nal);
                        if (type == 7)
                        {
                            // 发送端可能因为"自适应降分辨率"换了 SPS：不能沿用旧解码器
                            if (sps == null || !SameNal(sps, nal))
                            {
                                sps = nal;
                                if (decoderReady) { needReinit = true; }
                            }
                        }
                        else if (type == 8) { pps = nal; }

                        if (!decoderReady && sps != null && pps != null)
                        {
                            if (InitDecoder(sps, pps)) { decoderReady = true; RunOnUiThread(() => { if (_stats != null) _stats.Text = L10n.T("解码中…"); }); }
                        }
                        else if (type == 5 && needReinit && sps != null && pps != null)
                        {
                            // 关键帧到达时再重建，避免中间出现黑屏
                            ResetDecoder();
                            if (InitDecoder(sps, pps)) { needReinit = false; RunOnUiThread(() => { if (_stats != null) _stats.Text = L10n.T("解码中…"); }); }
                        }
                        if (decoderReady) FeedDecoder(nal);
                    }
                }
            }
            catch (ThreadInterruptedException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                RunOnUiThread(() => L10n.Toast(this, L10n.T("查看失败：") + ex.Message, ToastLength.Short).Show());
            }
            finally
            {
                _alive = false;
            }
        }

        private bool ReadHeaders(NetworkStream ns)
        {
            var sb = new StringBuilder();
            byte[] b = new byte[1];
            int total = 0;
            try
            {
                while (total < 4096)
                {
                    int r = ns.Read(b, 0, 1);
                    if (r <= 0) return false;
                    sb.Append((char)b[0]);
                    total++;
                    if (sb.ToString().EndsWith("\r\n\r\n"))
                    {
                        string headers = sb.ToString();
                        int lineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
                        if (lineEnd <= 0) return false;
                        string statusLine = headers.Substring(0, lineEnd);
                        return statusLine.IndexOf(" 200 ", StringComparison.Ordinal) >= 0
                            || statusLine.EndsWith(" 200", StringComparison.Ordinal);
                    }
                }
            }
            catch { return false; }
            return false;
        }

        private static bool SameNal(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>释放旧解码器（对端改分辨率时重建）。</summary>
        private void ResetDecoder()
        {
            MediaCodec old;
            lock (_decLock)
            {
                old = _decoder;
                _decoder = null;
            }
            if (old == null) return;
            try { old.Stop(); } catch { }
            try { old.Release(); } catch { }
        }

        private bool InitDecoder(byte[] sps, byte[] pps)
        {
            ResetDecoder();
            try
            {
                var f = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, _w, _h);
                // 必须传「带起始码的 Annex-B」SPS/PPS（或 avcC）。裸 NAL 解码器会静默不产帧→黑屏。
                f.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps));
                f.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps));
                lock (_decLock)
                {
                    _decoder = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc);
                    _decoder.Configure(f, _surface, null, 0);
                    _decoder.Start();
                }
                return true;
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => L10n.Toast(this, L10n.T("解码器初始化失败：") + ex.Message, ToastLength.Long).Show());
                return false;
            }
        }

        private void FeedDecoder(byte[] nal)
        {
            MediaCodec dec;
            lock (_decLock) dec = _decoder;
            if (dec == null) return;
            try
            {
                // 多次重试取输入缓冲，避免因短暂无可用槽而丢弃关键帧（丢关键帧会黑屏直到下个 I 帧）
                int idx = -1;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    idx = dec.DequeueInputBuffer(2000);
                    if (idx >= 0) break;
                }
                if (idx >= 0)
                {
                    var ib = dec.GetInputBuffer(idx);
                    if (ib != null && nal.Length <= ib.Capacity())
                    {
                        ib.Clear();
                        ib.Put(nal);
                        long pts = _frameIndex * (1000000 / 30);
                        dec.QueueInputBuffer(idx, 0, nal.Length, pts, 0);
                        _frameIndex++;
                    }
                    else if (ib == null) { /* skip */ }
                }
                // 抽取输出并渲染
                var oi = new MediaCodec.BufferInfo();
                int o = dec.DequeueOutputBuffer(oi, 10000);
                int guard = 0;
                while (o >= 0 || (MediaCodecInfoState)o == MediaCodecInfoState.OutputFormatChanged)
                {
                    if (o >= 0)
                    {
                        try { dec.ReleaseOutputBuffer(o, true); } catch { }
                        _fpsCount++;
                        var now = DateTime.Now;
                        if ((now - _fpsT0).TotalMilliseconds >= 1000)
                        {
                            _fps = _fpsCount;
                            _fpsCount = 0;
                            _fpsT0 = now;
                        }
                    }
                    else
                    {
                        // 对端换了分辨率：解码器会报输出格式变化，同步更新渲染尺寸避免画面拉伸
                        try
                        {
                            var of = dec.OutputFormat;
                            int nw = of.GetInteger(MediaFormat.KeyWidth);
                            int nh = of.GetInteger(MediaFormat.KeyHeight);
                            if (nw > 0 && nh > 0 && (nw != _w || nh != _h))
                            {
                                _w = nw; _h = nh;
                                RunOnUiThread(() => { ApplyTransform(); UpdateStats(); });
                            }
                        }
                        catch { }
                    }
                    if (++guard > 64) break;
                    o = dec.DequeueOutputBuffer(oi, 0);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("CamView", "解码异常：" + ex.Message);
            }
        }

        private static int NalType(byte[] nal)
        {
            int off = 0;
            if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) off = 4;
            else if (nal.Length > 2 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) off = 3;
            if (off >= nal.Length) return -1;
            return nal[off] & 0x1F;
        }
    }

    /// <summary>把字节流累积并按 Annex-B 起始码切分成 NAL 单元（含起始码）。</summary>
    internal sealed class NalFeeder
    {
        private byte[] _buf = new byte[0];

        public void Push(byte[] data, int len)
        {
            if (len <= 0) return;
            var nb = new byte[_buf.Length + len];
            Array.Copy(_buf, 0, nb, 0, _buf.Length);
            Array.Copy(data, 0, nb, _buf.Length, len);
            _buf = nb;
        }

        public byte[] NextNal()
        {
            int i = IndexOfStart(_buf, 0);
            if (i < 0) return null;
            int j = IndexOfStart(_buf, i + 3);
            if (j < 0)
            {
                // 只有一个起始码：数据还不够切分下一帧，等更多；但若堆积过多则强制吐出
                if (_buf.Length - i > 2 * 1024 * 1024)
                {
                    int len = _buf.Length - i;
                    var outp = new byte[len];
                    Array.Copy(_buf, i, outp, 0, len);
                    _buf = new byte[0];
                    return outp;
                }
                return null;
            }
            int len2 = j - i;
            var nal = new byte[len2];
            Array.Copy(_buf, i, nal, 0, len2);
            // 保留从 j 开始的剩余（以起始码开头），供下一次切分
            var rem = new byte[_buf.Length - j];
            Array.Copy(_buf, j, rem, 0, rem.Length);
            _buf = rem;
            return nal;
        }

        private static int IndexOfStart(byte[] b, int from)
        {
            for (int i = from; i + 2 < b.Length; i++)
            {
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
                {
                    // 允许前面多一个 00（4 字节起始码）
                    int s = (i > 0 && b[i - 1] == 0) ? i - 1 : i;
                    return s;
                }
            }
            return -1;
        }
    }
}

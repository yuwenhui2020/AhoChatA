using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Android.Graphics;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// App 之间互看的「专属加速通道」。
    ///
    /// 背景：浏览器观看只能走 HTTP（MJPEG / MSE 分片），有 TCP 握手、HTTP 头和队头阻塞，
    /// 延迟偏高。两台都是本 App 时没必要受这个限制——共享端直接把画面按帧用 UDP 推给
    /// 订阅者，观看端收齐分片就成帧显示，省掉握手与队头阻塞，明显更低延迟。
    /// 这条通道是"现场做的"：复用通话视频那套分片格式（CallVideoProtocol），
    /// 只是载荷从 H.264 换成 JPEG（画笔/画面共享不需要那么高的编码复杂度）。
    ///
    /// 摄像头 / 屏幕共享的帧是 IpCameraServer 内部的 H.264 直出，
    /// 这里不重复取流，它们继续用 live.ts 硬解播放——同样比浏览器 MJPEG 快。
    /// 收不到 UDP 时观看端会自动回退到 HTTP，保证一定能看到。
    /// </summary>
    public static class ShareCast
    {
        /// <summary>加速通道端口（共享端监听、观看端订阅）。</summary>
        public const int Port = 45811;

        public static readonly byte[] Sub = { (byte)'L', (byte)'C', (byte)'S', (byte)'U' };   // 订阅
        public static readonly byte[] Bye = { (byte)'L', (byte)'C', (byte)'B', (byte)'Y' };   // 退订
    }

    /// <summary>共享端：把「屏幕画笔」画面按帧推给所有订阅者。</summary>
    public sealed class ShareCastServer : IDisposable
    {
        private UdpClient _udp;
        private Thread _rx, _tx;
        private volatile bool _alive;
        private readonly List<IPEndPoint> _subs = new List<IPEndPoint>();
        private uint _fid;

        public static ShareCastServer Current { get; private set; }

        public static ShareCastServer Start()
        {
            Stop();
            var s = new ShareCastServer();
            if (!s.Bind()) { try { s.Dispose(); } catch { } return null; }
            Current = s;
            Log.Info("Mchat", "共享加速通道已开启（UDP " + ShareCast.Port + "）");
            return s;
        }

        public static void Stop()
        {
            var c = Current;
            Current = null;
            if (c != null) { try { c.Dispose(); } catch { } }
        }

        private bool Bind()
        {
            try
            {
                _udp = new UdpClient(ShareCast.Port);
                _alive = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "加速通道端口绑定失败：" + ex.Message);
                return false;
            }
            _rx = new Thread(RxLoop) { IsBackground = true, Name = "sharecast-ctl" };
            _rx.Start();
            _tx = new Thread(PumpDraw) { IsBackground = true, Name = "sharecast-tx" };
            _tx.Start();
            return true;
        }

        /// <summary>处理订阅 / 退订包。</summary>
        private void RxLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (_alive)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { break; }
                if (d == null || d.Length < 4) continue;
                if (d[0] == 'L' && d[1] == 'C' && d[2] == 'S' && d[3] == 'U')
                {
                    var ep = new IPEndPoint(any.Address, any.Port);
                    lock (_subs)
                    {
                        bool has = false;
                        foreach (var x in _subs) if (x.Address.Equals(ep.Address)) { has = true; break; }
                        if (!has) { _subs.Add(ep); Log.Info("Mchat", "加速通道：新观看端 " + ep.Address); }
                    }
                }
                else if (d[0] == 'L' && d[1] == 'C' && d[2] == 'B' && d[3] == 'Y')
                {
                    lock (_subs) _subs.RemoveAll(x => x.Address.Equals(any.Address));
                }
            }
        }

        /// <summary>抓画笔画面 → JPEG → 分片推送。</summary>
        private void PumpDraw()
        {
            while (_alive)
            {
                try
                {
                    if (!DrawShareServer.IsSharing) { Thread.Sleep(300); continue; }

                    var bmp = DrawShareServer.Grab();
                    if (bmp == null) { Thread.Sleep(120); continue; }

                    byte[] jpg = null;
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            if (bmp.Compress(Bitmap.CompressFormat.Jpeg, 72, ms)) jpg = ms.ToArray();
                        }
                    }
                    finally { try { bmp.Recycle(); } catch { } }

                    if (jpg != null && jpg.Length > 0)
                    {
                        _fid++;
                        SendFrame(jpg, _fid);
                    }
                    Thread.Sleep(110);   // ≈9fps：画笔用不着更高，省带宽也省电
                }
                catch { Thread.Sleep(200); }
            }
        }

        private void SendFrame(byte[] payload, uint fid)
        {
            int max = CallVideoProtocol.MaxPayload;
            int total = (payload.Length + max - 1) / max;
            if (total <= 0 || total > 20000) return;

            lock (_subs)
            {
                if (_subs.Count == 0) return;
                for (int i = 0; i < total; i++)
                {
                    int off = i * max;
                    int len = Math.Min(max, payload.Length - off);
                    var pkt = CallVideoProtocol.Pack(fid, i, total, true, payload, off, len);
                    for (int k = _subs.Count - 1; k >= 0; k--)
                    {
                        try { _udp.Send(pkt, pkt.Length, _subs[k]); }
                        catch { try { _subs.RemoveAt(k); } catch { } }
                    }
                }
            }
        }

        public void Dispose()
        {
            _alive = false;
            try { _udp?.Close(); } catch { }
            _udp = null;
            lock (_subs) _subs.Clear();
        }
    }

    /// <summary>观看端：订阅加速通道，收齐分片组成一帧 JPEG。</summary>
    public sealed class ShareCastClient : IDisposable
    {
        private UdpClient _udp;
        private IPEndPoint _ep;
        private Thread _t;
        private volatile bool _alive;

        /// <summary>收到一整帧（JPEG 字节）。</summary>
        public Action<byte[]> OnFrame;

        /// <summary>是否已从加速通道收到过画面（用来决定是否回退 HTTP）。</summary>
        public bool GotFrame { get; private set; }

        public bool Start(string host)
        {
            try
            {
                _udp = new UdpClient();
                _ep = new IPEndPoint(IPAddress.Parse(host), ShareCast.Port);
                _udp.Client.ReceiveTimeout = 1500;
                _alive = true;
                _t = new Thread(Loop) { IsBackground = true, Name = "sharecast-rx" };
                _t.Start();
                // UDP 订阅包可能丢，多发几次
                for (int i = 0; i < 3; i++)
                {
                    try { _udp.Send(ShareCast.Sub, ShareCast.Sub.Length, _ep); } catch { }
                    Thread.Sleep(120);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>续订：共享端 30 秒收不到订阅包就把观看端摘掉。</summary>
        public void KeepAlive()
        {
            try { _udp?.Send(ShareCast.Sub, ShareCast.Sub.Length, _ep); } catch { }
        }

        private void Loop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            uint cur = 0;
            int total = -1, got = 0;
            byte[][] parts = null;
            long startAt = 0;

            while (_alive)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { continue; }          // 收不到就继续等，主流程会自行回退 HTTP
                if (d == null) continue;

                uint fid; int idx, tot; bool key; int off, len;
                if (!CallVideoProtocol.Unpack(d, d.Length, out fid, out idx, out tot, out key, out off, out len))
                    continue;

                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                if (fid != cur || tot != total || now - startAt > 800)
                {
                    cur = fid; total = tot; got = 0;
                    parts = new byte[tot][];
                    startAt = now;
                }
                if (parts == null || idx < 0 || idx >= tot) continue;

                if (parts[idx] == null)
                {
                    var p = new byte[len];
                    Array.Copy(d, off, p, 0, len);
                    parts[idx] = p;
                    got++;
                }

                if (got == total)
                {
                    int cap = 0;
                    for (int i = 0; i < total; i++) cap += parts[i] == null ? 0 : parts[i].Length;
                    var frame = new byte[cap];
                    int pos = 0; bool ok = true;
                    for (int i = 0; i < total; i++)
                    {
                        if (parts[i] == null) { ok = false; break; }
                        Array.Copy(parts[i], 0, frame, pos, parts[i].Length);
                        pos += parts[i].Length;
                    }
                    if (ok && pos > 0)
                    {
                        GotFrame = true;
                        try { OnFrame?.Invoke(frame); } catch { }
                    }
                    got = 0;
                    parts = new byte[tot][];
                    startAt = now;
                }
            }
        }

        public void Dispose()
        {
            _alive = false;
            try { _udp?.Send(ShareCast.Bye, ShareCast.Bye.Length, _ep); } catch { }
            try { _udp?.Close(); } catch { }
            _udp = null;
        }
    }
}

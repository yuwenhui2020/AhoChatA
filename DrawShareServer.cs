using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Android.Graphics;
using Android.Util;
using Android.Views;

namespace AhoChatA
{
    /// <summary>
    /// 屏幕画笔的局域网直播。
    ///
    /// 画笔本身是叠加在系统屏幕上的悬浮层（ScreenDrawService），没法用 MediaProjection 再采一遍
    /// （同一份授权拿不到第二次，还要重复弹授权）。这里走更直接的路子：**把画布 View 直接画进 Bitmap**，
    /// 编码成 JPEG 推 MJPEG 流。画笔是手写线条，8fps 已经完全够流畅，而 JPEG 的延迟比 H.264 更低、
    /// 也不依赖任何解码器——浏览器 `<img>` 就能看。
    ///
    /// 访问：http://手机IP:7790/draw
    /// </summary>
    public static class DrawShareServer
    {
        /// <summary>同时观看上限。改用「单帧广播」后，每人只多一条 socket 写，16 路没压力。</summary>
        private const int MaxViewers = 16;
        private static int _viewers;

        private const int Fps = 12;
        private const int JpegQuality = 62;
        private const int MaxWidth = 1024;

        public static bool IsSharing { get { return ScreenDrawService.IsRunning && ScreenDrawService.SharedRoot != null; } }
        public static int ViewerCount { get { return Volatile.Read(ref _viewers); } }

        /* ===== 单生产者：一次抓帧 + 一次 JPEG 编码，广播给所有观看端 =====
           以前每个客户端各自抓帧编码，10 个人看就是 10 倍 CPU —— 这就是并发上不去的原因。 */
        private static readonly object _frameLock = new object();
        private static byte[] _latestJpeg;
        private static long _latestSeq;
        private static Thread _producer;

        private static void EnsureProducer()
        {
            lock (_frameLock)
            {
                if (_producer != null) return;
                _producer = new Thread(ProducerLoop) { IsBackground = true, Name = "draw-producer" };
                _producer.Start();
            }
        }

        private static void ProducerLoop()
        {
            try
            {
                while (ScreenDrawService.IsRunning)
                {
                    var bmp = Grab();
                    if (bmp != null)
                    {
                        byte[] jpg;
                        try { jpg = Encode(bmp); }
                        finally { bmp.Recycle(); }

                        if (jpg != null)
                        {
                            lock (_frameLock)
                            {
                                // 画面没变化就不发（画笔静止时几乎零带宽）
                                if (_latestJpeg == null || !Same(_latestJpeg, jpg))
                                {
                                    _latestJpeg = jpg;
                                    _latestSeq++;
                                    Monitor.PulseAll(_frameLock);
                                }
                            }
                        }
                    }
                    Thread.Sleep(1000 / Fps);
                }
            }
            catch { }
            finally
            {
                lock (_frameLock) { _producer = null; Monitor.PulseAll(_frameLock); }
            }
        }

        public static void ServeClient(TcpClient client, string sub)
        {
            string path = (sub ?? "").Trim();
            int qi = path.IndexOf('?');
            if (qi >= 0) path = path.Substring(0, qi);
            if (path == "") path = "/";

            if (path == "/" || path == "/index.html")
            {
                WebServer.WriteHtml(client, 200, PlayerPage());
                return;
            }

            if (path.StartsWith("/info"))
            {
                var json = "{\"sharing\":" + (IsSharing ? "true" : "false")
                         + ",\"fps\":" + Fps + ",\"viewers\":" + ViewerCount
                         + ",\"url\":\"" + WebServer.UrlOf("/draw") + "\"}";
                WebServer.WriteHtml(client, 200, json);   // 浏览器直接看也无所谓
                return;
            }

            if (path.StartsWith("/snapshot"))
            {
                var bmp = Grab();
                if (bmp == null) { WebServer.WriteHtml(client, 503, "<h3>画笔未开启</h3>"); return; }
                try
                {
                    var jpg = Encode(bmp);
                    WriteRaw(client, "image/jpeg", jpg);
                }
                finally { bmp.Recycle(); }
                return;
            }

            if (path.StartsWith("/stream"))
            {
                StreamMjpeg(client);
                return;
            }

            WebServer.WriteHtml(client, 200, PlayerPage());
        }

        /* ===================== MJPEG 推流 ===================== */

        private static void StreamMjpeg(TcpClient client)
        {
            if (!IsSharing)
            {
                WebServer.WriteHtml(client, 503, L10n.T("<h3>画笔未开启</h3><p>请在手机上打开「屏幕画笔」。</p>"));
                return;
            }
            if (Interlocked.Increment(ref _viewers) > MaxViewers)
            {
                Interlocked.Decrement(ref _viewers);
                WebServer.WriteHtml(client, 503, L10n.T("<h3>观看人数已满</h3>"));
                return;
            }

            try
            {
                client.NoDelay = true;
                try { client.SendBufferSize = 128 * 1024; } catch { }
                client.SendTimeout = 8000;

                var ns = client.GetStream();
                var head = "HTTP/1.1 200 OK\r\n"
                         + "Content-Type: multipart/x-mixed-replace; boundary=--lc frame\r\n"
                         + "Cache-Control: no-store, no-cache, must-revalidate\r\n"
                         + "Pragma: no-cache\r\n"
                         + "Connection: close\r\n\r\n";
                head = head.Replace("--lc frame", "--lcframe");
                var hb = Encoding.ASCII.GetBytes(head);
                ns.Write(hb, 0, hb.Length);
                ns.Flush();

                EnsureProducer();

                var frameHead = Encoding.ASCII.GetBytes("--lcframe\r\nContent-Type: image/jpeg\r\nContent-Length: ");
                var crlf = Encoding.ASCII.GetBytes("\r\n\r\n");
                var tail = Encoding.ASCII.GetBytes("\r\n");
                long lastSeq = -1;

                while (IsSharing)
                {
                    byte[] jpg = null;
                    lock (_frameLock)
                    {
                        if (_latestSeq == lastSeq)
                            Monitor.Wait(_frameLock, 800);

                        if (_latestSeq != lastSeq && _latestJpeg != null)
                        {
                            jpg = _latestJpeg;
                            lastSeq = _latestSeq;
                        }
                    }
                    if (jpg == null) continue;

                    try
                    {
                        ns.Write(frameHead, 0, frameHead.Length);
                        var len = Encoding.ASCII.GetBytes(jpg.Length.ToString());
                        ns.Write(len, 0, len.Length);
                        ns.Write(crlf, 0, crlf.Length);
                        ns.Write(jpg, 0, jpg.Length);
                        ns.Write(tail, 0, tail.Length);
                        ns.Flush();
                    }
                    catch { break; }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("DrawShare", "推流中断：" + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _viewers);
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /* ===================== 抓帧 ===================== */

        /// <summary>把当前画笔悬浮层渲染成 Bitmap（必须在 UI 线程绘制，这里用 Post 同步等待）。</summary>
        public static Bitmap Grab()
        {
            var root = ScreenDrawService.SharedRoot;
            if (root == null) return null;

            Bitmap result = null;
            var done = new ManualResetEventSlim(false);
            try
            {
                root.Post(() =>
                {
                    try
                    {
                        int w = root.Width, h = root.Height;
                        if (w <= 0 || h <= 0) return;
                        float scale = w > MaxWidth ? (float)MaxWidth / w : 1f;
                        int bw = Math.Max(2, (int)(w * scale));
                        int bh = Math.Max(2, (int)(h * scale));
                        var bmp = Bitmap.CreateBitmap(bw, bh, Bitmap.Config.Argb8888);
                        using (var canvas = new Canvas(bmp))
                        {
                            canvas.Scale(scale, scale);
                            root.Draw(canvas);
                        }
                        result = bmp;
                    }
                    catch (Exception ex) { Log.Warn("DrawShare", "抓帧失败：" + ex.Message); }
                    finally { done.Set(); }
                });
                done.Wait(500);
            }
            catch { }
            finally { done.Dispose(); }
            return result;
        }

        private static byte[] Encode(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Compress(Bitmap.CompressFormat.Jpeg, JpegQuality, ms);
                return ms.ToArray();
            }
        }

        /* ===================== 观看页 ===================== */

        private static string PlayerPage()
        {
            var url = WebServer.UrlOf("/draw");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=utf-8>");
            sb.Append("<meta name=viewport content='width=device-width,initial-scale=1,maximum-scale=1'>");
            sb.Append(L10n.T("<title>AhoChat 屏幕画笔</title>"));
            sb.Append("<style>");
            sb.Append("html,body{margin:0;height:100%;background:#000;overflow:hidden}");
            sb.Append("#wrap{position:fixed;inset:0;display:flex;align-items:center;justify-content:center}");
            sb.Append("#v{max-width:100%;max-height:100%;display:block}");
            sb.Append("#tip{position:fixed;left:0;right:0;bottom:0;padding:8px 12px;color:#9aa0a6;font:12px/1.5 ")
              .Append("-apple-system,'PingFang SC',sans-serif;background:linear-gradient(transparent,#000a);text-align:center}");
            sb.Append("#off{position:fixed;inset:0;display:none;align-items:center;justify-content:center;color:#8b929c;")
              .Append("font:15px/1.6 -apple-system,'PingFang SC',sans-serif;text-align:center;padding:24px}");
            sb.Append("</style></head><body><div id=wrap><img id=v alt='drawing'></div>");
            sb.Append(L10n.T("<div id=tip>屏幕画笔直播 · ")).Append(url).Append("</div>");
            sb.Append(L10n.T("<div id=off>对方没有在画，或已关闭屏幕画笔<br><span style='font-size:12px'>本页会自动重连</span></div>"));
            sb.Append("<script>");
            sb.Append("var v=document.getElementById('v'),off=document.getElementById('off'),seq=0;");
            sb.Append("function load(){v.src='/draw/stream.mjpg?t='+(++seq);}");
            sb.Append("v.onload=function(){off.style.display='none';};");
            sb.Append("v.onerror=function(){off.style.display='flex';setTimeout(load,1500);};");
            sb.Append("setInterval(function(){if(v.complete&&v.naturalWidth===0){off.style.display='flex';}else{off.style.display='none';}},1000);");
            sb.Append("load();");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }

        private static void WriteRaw(TcpClient client, string contentType, byte[] body)
        {
            try
            {
                var head = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType
                         + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                var ns = client.GetStream();
                ns.Write(hb, 0, hb.Length);
                ns.Write(body, 0, body.Length);
                ns.Flush();
            }
            catch { }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                try { client.Close(); } catch { }
            }
        }
    }
}

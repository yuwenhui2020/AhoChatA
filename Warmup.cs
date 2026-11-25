using System;
using System.Net;
using System.Net.Sockets;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 启动预热：把「第一次用才会创建、而且创建要花时间」的东西提前建好。
    ///
    /// 原则：
    ///   · 只预热**便宜且确定会用**的（字体度量、DNS、UDP socket、证书、图标 Paint 缓存）；
    ///   · **绝不预热相机 / 编码器 / 麦克风**——那些会占硬件、耗电，还可能和用户后续操作抢资源；
    ///   · 全部放在后台线程里跑，失败只记日志，不影响任何功能。
    ///
    /// 效果：点「+ → 共享摄像头」时不再等 200~400ms 的首帧；浏览器访问时端口已经在听。
    /// </summary>
    public static class Warmup
    {
        private static bool _done;
        private static readonly object _gate = new object();

        public static void Precreate()
        {
            lock (_gate)
            {
                if (_done) return;
                _done = true;
            }

            var t0 = System.Environment.TickCount;
            try
            {
                // 1) HTTPS 证书提前解析（首次要 140ms，访问 https 时才用到）
                try { WebServer.PrewarmCertificate(); } catch { }

                // 2) 网络栈预热：本机 IP 解析一次（Android 上 Dns  API 不全，用 IPAddress.Parse 即可）
                try
                {
                    var ip = DeviceStatus.Ip;
                    if (!string.IsNullOrEmpty(ip)) IPAddress.Parse(ip);
                }
                catch { }

                // 3) UDP 广播 socket 预热：局域网发现第一次发信标时要建 socket
                try
                {
                    using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        s.EnableBroadcast = true;
                        s.Bind(new IPEndPoint(IPAddress.Any, 0));
                    }
                }
                catch { }

                // 4) 图标 Paint 缓存：IconView 每次 new 都新建 Paint，先把度量跑一遍让字体缓存热起来
                try { IconView.Warmup(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Warn("Warmup", "预热失败（不影响功能）：" + ex.Message);
            }
            finally
            {
                Log.Info("Warmup", "预热完成，耗时 " + (System.Environment.TickCount - t0) + "ms");
            }
        }
    }
}

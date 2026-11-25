using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AhoChatA
{
    /// <summary>
    /// 局域网摄像头嗅探：监听 UDP 信标 LANCAM，维护带过期的摄像头列表，变化时回调 UI。
    /// 仅用于局域网发现，不做任何跨网操作。
    /// </summary>
    public sealed class CameraInfo
    {
        public string Ip;
        public int Port;
        public string Name;
        public int Width;
        public int Height;
        public int Fps;
        public int Rotation;
        public bool IsScreen;      // true = 对方共享的是屏幕，false = 摄像头
        public DateTime LastSeen;
        public bool IsSelf => Ip == DeviceStatus.Ip;
        public int AgeSec => (int)(DateTime.Now - LastSeen).TotalSeconds;
        public string Resolution => Width + "x" + Height;
    }

    public sealed class IpCameraDiscoverer
    {
        private readonly object _lock = new object();
        private UdpClient _sock;
        private Thread _thread;
        private bool _running;
        private readonly Dictionary<string, CameraInfo> _cams = new Dictionary<string, CameraInfo>();

        public event Action<List<CameraInfo>> OnCamerasChanged;

        public bool IsRunning { get { lock (_lock) return _running; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                try
                {
                    _sock = new UdpClient(IpCameraServer.BeaconPort);
                    _sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _running = true;
                    _thread = new Thread(Loop) { IsBackground = true };
                    _thread.Start();
                }
                catch (Exception ex)
                {
                    _running = false;
                    throw new Exception(L10n.T("嗅探启动失败：") + ex.Message);
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                // 只关套接字即可让 Receive 抛 ObjectDisposedException 自然退出；
                // 不再用 Thread.Interrupt（它可能在任意位置打断线程并升级成进程崩溃）
                try { _sock?.Close(); } catch { }
                _sock = null;
            }
            lock (_lock)
            {
                _cams.Clear();
            }
            OnCamerasChanged?.Invoke(Snapshot());
        }

        private void Loop()
        {
            // 整体兜底：后台线程一旦漏出异常，Mono 会把它升级成进程级 FATAL 崩溃
            try { LoopCore(); }
            catch (ThreadInterruptedException) { }
            catch (ObjectDisposedException) { }
            catch { }
        }

        private void LoopCore()
        {
            var ep = new IPEndPoint(IPAddress.Any, IpCameraServer.BeaconPort);
            DateTime lastPrune = DateTime.Now;
            while (true)
            {
                bool running;
                lock (_lock) running = _running;
                if (!running) break;
                try
                {
                    // 用带超时的接收，便于周期性清理过期项
                    _sock.Client.ReceiveTimeout = 500;
                    byte[] data = _sock.Receive(ref ep);
                    if (data != null && data.Length > 0) Parse(Encoding.UTF8.GetString(data));
                }
                catch (ThreadInterruptedException) { break; }
                catch (SocketException) { /* 超时，继续 */ }
                catch (ObjectDisposedException) { break; }
                catch { }

                if ((DateTime.Now - lastPrune).TotalMilliseconds > 500)
                {
                    lastPrune = DateTime.Now;
                    // 必须隔离：Stop() 在线程任意位置 Interrupt 时，异常若逃出本方法
                    // 会直接变成 FATAL（Mono 后台线程未捕获异常 → 整个进程 SIGABRT）
                    try { PruneAndNotify(); }
                    catch (ThreadInterruptedException) { break; }
                    catch { }
                }
            }
        }

        private void Parse(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            var p = msg.Split('|');
            if (p.Length < 5 || p[0] != "LANCAM") return;
            string ip = p[1];
            if (string.IsNullOrEmpty(ip) || ip == DeviceStatus.Ip) return; // 忽略自己
            if (!int.TryParse(p[2], out int port)) return;
            string name = p[3];
            int w = 0, h = 0, fps = 0;
            var rh = p[4].Split('@');
            var wh = rh[0].Split('x');
            if (wh.Length == 2) { int.TryParse(wh[0], out w); int.TryParse(wh[1], out h); }
            if (rh.Length == 2) int.TryParse(rh[1], out fps);
            int rot = 0;
            if (p.Length > 5) int.TryParse(p[5], out rot);
            // 第 7 字段（可选）：camera / screen。老版本没有这个字段，按摄像头处理
            bool isScreen = p.Length > 6 && p[6] == "screen";

            lock (_lock)
            {
                string key = ip + ":" + port;
                if (!_cams.TryGetValue(key, out var ci)) { ci = new CameraInfo(); _cams[key] = ci; }
                ci.Ip = ip; ci.Port = port; ci.Name = name; ci.Width = w; ci.Height = h; ci.Fps = fps;
                ci.Rotation = rot;
                ci.IsScreen = isScreen;
                ci.LastSeen = DateTime.Now;
            }
        }

        private void PruneAndNotify()
        {
            bool changed;
            lock (_lock)
            {
                changed = false;
                var keys = new List<string>(_cams.Keys);
                foreach (var k in keys)
                {
                    if (_cams[k].AgeSec > 4) { _cams.Remove(k); changed = true; }
                }
            }
            if (changed) OnCamerasChanged?.Invoke(Snapshot());
        }

        public List<CameraInfo> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<CameraInfo>();
                foreach (var kv in _cams) if (kv.Value.AgeSec <= 4) list.Add(kv.Value);
                list.Sort((a, b) => a.Name.CompareTo(b.Name));
                return list;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AhoChatA
{
    /// <summary>
    /// 画板设备（inkcanvas/painter）发现与控制。
    ///
    /// 协议（UDP 21118，与 PC 端 desk.LanDiscovery 对应）：
    ///   App → 广播 "PAINTER-PING"：发现画板程序是否在线；
    ///   PC  → "PAINTER-PONG|主机名|被控端口|MAC|PAINTER|Web端口|Web运行(0/1)|仅回环(0/1)"；
    ///   App → 单播 "PAINTER-CMD|web-on|web-off|status" 到 PC 的 21118 端口；
    ///   PC  → "PAINTER-ACK|动作|结果|Web端口|Web运行|仅回环|说明" 回执。
    ///
    /// 只要画板程序在运行就应答（PONG 与 Web 是否启动无关），因此 Web 关着时
    /// 依然能扫到设备、并远程把 Web 打开；随后浏览器访问 http://IP:Web端口/ 即可控制。
    /// </summary>
    public sealed class DrawboardDiscovery
    {
        public const int Port = 21118;
        private const string Ping = "PAINTER-PING";
        private const string Pong = "PAINTER-PONG";
        private const string Cmd = "PAINTER-CMD";
        private const string Ack = "PAINTER-ACK";
        private const string Kind = "PAINTER";
        private const int TimeoutMs = 15000;   // 15 秒没再收到 PONG 视为离线（3~6 个发现周期）

        public sealed class Device
        {
            public string Ip;
            public string HostName;
            public string Mac;
            public int HostPort;
            public int WebPort = 9090;
            public bool WebRunning;
            public bool LoopbackOnly;
            public long LastSeen;
            public bool Online { get { return Java.Lang.JavaSystem.CurrentTimeMillis() - LastSeen < TimeoutMs; } }
        }

        private static readonly DrawboardDiscovery _inst = new DrawboardDiscovery();
        public static DrawboardDiscovery Current { get { return _inst; } }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Device> _devices = new Dictionary<string, Device>();
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private long _lastScan;

        /// <summary>设备列表变化（新增/状态变化/离线）时触发，供 UI 刷新。</summary>
        public event Action Changed;

        public bool IsRunning { get { return _udp != null; } }

        public void Start()
        {
            if (_udp != null) return;
            try
            {
                var udp = new UdpClient();
                udp.EnableBroadcast = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));   // 临时端口；PC 会把 ACK 单播回这里
                _udp = udp;
                _cts = new CancellationTokenSource();
                Task.Run(() => ReceiveLoop(udp, _cts.Token));
                Android.Util.Log.Info("DrawDisc", "started on ephemeral port " + ((IPEndPoint)udp.Client.LocalEndPoint).Port);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("DrawDisc", "start failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
            _cts = null;
        }

        /// <summary>广播一帧发现请求。PC 只要进程在运行就会回 PONG。</summary>
        public void ScanNow()
        {
            var udp = _udp;
            if (udp == null) return;
            _lastScan = Java.Lang.JavaSystem.CurrentTimeMillis();
            var msg = Encoding.UTF8.GetBytes(Ping);
            try
            {
                foreach (var ep in BroadcastEndpoints())
                {
                    try { udp.Send(msg, msg.Length, ep); } catch { }
                }
                Android.Util.Log.Debug("DrawDisc", "PING sent to " + BroadcastEndpoints().Count + " endpoints");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("DrawDisc", "scan failed: " + ex.Message);
            }
        }

        /// <summary>远程控制画板 Web 开关：action = web-on / web-off / status。定向单播，不打扰别的电脑。</summary>
        public bool SendCommand(string ip, string action)
        {
            var udp = _udp;
            if (udp == null || string.IsNullOrEmpty(ip)) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(Cmd + "|" + action);
                udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(ip), Port));
                Android.Util.Log.Info("DrawDisc", "CMD " + action + " -> " + ip);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("DrawDisc", "cmd failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>当前在线的画板设备（副本）。</summary>
        public List<Device> OnlineDevices()
        {
            lock (_gate)
            {
                return _devices.Values.Where(d => d.Online).OrderBy(d => d.Ip).ToList();
            }
        }

        private async Task ReceiveLoop(UdpClient udp, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var r = await udp.ReceiveAsync();
                    if (token.IsCancellationRequested) break;
                    string text = Encoding.UTF8.GetString(r.Buffer);
                    string ip = r.RemoteEndPoint.Address.ToString();
                    var parts = text.Split('|');
                    if (parts.Length == 0) continue;

                    if (parts[0] == Pong && parts.Length >= 4)
                    {
                        // PONG：识别为画板设备并更新 Web 状态；第 5 段必须是 PAINTER 类型标记
                        string kind = parts.Length >= 5 ? parts[4] : "";
                        if (!string.IsNullOrEmpty(kind) && kind != Kind) continue;
                        int hostPort = 0, webPort = 9090;
                        int.TryParse(parts[2], out hostPort);
                        if (parts.Length >= 6) int.TryParse(parts[5], out webPort);
                        bool webRunning = parts.Length >= 7 && parts[6] == "1";
                        bool loopback = parts.Length >= 8 && parts[7] == "1";
                        Apply(ip, parts[1], parts[3], hostPort, webPort, webRunning, loopback);
                    }
                    else if (parts[0] == Ack && parts.Length >= 3)
                    {
                        // ACK：命令回执，顺带用回执里的 Web 状态刷新设备（不依赖下一次 PONG）
                        int webPort = 9090;
                        bool webRunning = false, loopback = false;
                        if (parts.Length >= 4) int.TryParse(parts[3], out webPort);
                        if (parts.Length >= 5) webRunning = parts[4] == "1";
                        if (parts.Length >= 6) loopback = parts[5] == "1";
                        Apply(ip, null, null, 0, webPort, webRunning, loopback);
                        Android.Util.Log.Info("DrawDisc", "ACK " + parts[1] + " " + parts[2] + " from " + ip
                            + (parts.Length >= 7 ? " : " + parts[6] : ""));
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("DrawDisc", "receive loop: " + ex.Message);
            }
        }

        private void Apply(string ip, string host, string mac, int hostPort,
                           int webPort, bool webRunning, bool loopback)
        {
            bool changed = false;
            lock (_gate)
            {
                Device d;
                if (!_devices.TryGetValue(ip, out d))
                {
                    d = new Device { Ip = ip };
                    _devices[ip] = d;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(host) && d.HostName != host) { d.HostName = host; changed = true; }
                if (!string.IsNullOrEmpty(mac) && d.Mac != mac) { d.Mac = mac; changed = true; }
                if (hostPort > 0 && d.HostPort != hostPort) { d.HostPort = hostPort; changed = true; }
                if (webPort > 0 && d.WebPort != webPort) { d.WebPort = webPort; changed = true; }
                if (d.WebRunning != webRunning) { d.WebRunning = webRunning; changed = true; }
                if (d.LoopbackOnly != loopback) { d.LoopbackOnly = loopback; changed = true; }
                d.LastSeen = Java.Lang.JavaSystem.CurrentTimeMillis();
            }
            if (changed)
            {
                try { Changed?.Invoke(); } catch { }
            }
        }

        /// <summary>本机各网卡的定向广播地址 + 255.255.255.255，确保热点/Wi-Fi 都能送出去。</summary>
        private List<IPEndPoint> BroadcastEndpoints()
        {
            var eps = new List<IPEndPoint>();
            try
            {
                var en = Java.Net.NetworkInterface.NetworkInterfaces;
                while (en.HasMoreElements)
                {
                    var j = (Java.Net.NetworkInterface)en.NextElement();
                    if (!j.IsUp || j.IsLoopback) continue;
                    foreach (Java.Net.InterfaceAddress ia in j.InterfaceAddresses)
                    {
                        var addr = ia.Address;
                        if (addr == null || !(addr is Java.Net.Inet4Address)) continue;
                        IPAddress ip;
                        try { ip = IPAddress.Parse(addr.HostAddress); } catch { continue; }
                        byte[] a = ip.GetAddressBytes();
                        // 用真实掩码算定向广播：a | ~mask；掩码取不到就按传统 /24 兜底
                        byte[] mask = PrefixToMask(ia.NetworkPrefixLength);
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | (~mask[i] & 0xFF));
                        eps.Add(new IPEndPoint(new IPAddress(b), Port));
                    }
                }
            }
            catch { }
            eps.Add(new IPEndPoint(IPAddress.Broadcast, Port));
            return eps.Distinct().ToList();
        }

        private static byte[] PrefixToMask(int prefix)
        {
            var mask = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int bits = Math.Max(0, Math.Min(8, prefix - i * 8));
                mask[i] = (byte)(bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF);
            }
            return mask;
        }
    }
}

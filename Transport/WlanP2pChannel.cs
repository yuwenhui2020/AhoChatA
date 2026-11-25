using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Net;
using Android.Net.Wifi.P2p;
using Android.OS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AhoChatA
{
    /// <summary>WLAN Direct 上发现/连上的一台对端。</summary>
    public class WlanP2pPeer
    {
        /// <summary>Wi-Fi Direct 设备 MAC。</summary>
        public string Mac;
        /// <summary>设备名（未连接时是机型名，已连接时可能是 "IP:port"）。</summary>
        public string DeviceName;
        /// <summary>从 DeviceName 里解析出的 IPv4（组网成功后才有）。</summary>
        public string Ip;
        /// <summary>握手后拿到的本程序昵称。</summary>
        public string Name;
        /// <summary>握手后拿到的本程序 peerId（用来和局域网节点表对齐）。</summary>
        public string Id;
        public bool Connected;
        public long LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// WLAN Direct（Wi-Fi P2P）传输渠道。
    ///
    /// 作用：让**不在同一局域网**的两台设备也能直连通信。Android 的 Wi-Fi P2P 会自己
    /// 建一个 GO 组建网（典型网段 192.168.49.x），组网成功后双方相当于同网段，
    /// 于是本类直接复用局域网那条 TCP 帧协议（4 字节长度 + JSON 头 + 二进制附件）
    /// 在 P2P 网段上收发，并额外做一次 hello 握手互认身份。
    ///
    /// 与其它链路的关系：P2P 只是**传输方式**，不产生新的聊天室。频道同时开了
    /// p2p 与 lan 时，桥接层会做双向转发（同步），使「非局域网用户 ↔ 局域网用户」
    /// 也能互通；去重靠消息 id，不会成环。
    ///
    /// 全部方法都做了 try/catch 降级：设备不支持 P2P、没开 Wi-Fi、系统弹窗未同意时
    /// 只会让本渠道静默不可用，绝不把异常冒到主线程。
    /// </summary>
    public class WlanP2pChannel
    {
        private readonly Context context;
        private WifiP2pManager manager;
        private WifiP2pManager.Channel channel;
        private volatile bool enabled;
        private volatile bool groupFormed;
        private string lastError = "";

        /// <summary>发现到的 P2P 设备（含尚未组网的）。</summary>
        private readonly ConcurrentDictionary<string, WlanP2pPeer> devices =
            new ConcurrentDictionary<string, WlanP2pPeer>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已组网、能直接收发帧的对端。</summary>
        private readonly ConcurrentDictionary<string, WlanP2pPeer> linked =
            new ConcurrentDictionary<string, WlanP2pPeer>(StringComparer.Ordinal);

        public const string SvcType = "_Mchat._tcp";
        public const int PeerTimeoutMs = 20000;

        /// <summary>收到一帧：peerId, fromIp, headerJson, body。</summary>
        public Action<string, string, string, byte[]> OnFrame;
        /// <summary>设备列表变化。</summary>
        public Action OnPeersChanged;
        /// <summary>组网状态变化。</summary>
        public Action<bool> OnGroupChanged;
        public Action<string> OnLog;

        /// <summary>
        /// 自动连接：无 LAN 自组网模式下，发现设备就自动发起组网（用户无感）。
        /// 由桥接层在「无 Wi-Fi 局域网」时置 true。
        /// </summary>
        public bool AutoConnect;

        /// <summary>上次自动发起连接的时刻，避免对每台设备狂发 connect（8 秒节流）。</summary>
        private long lastAutoConnectMs;

        /// <summary>上一次组网成功记下的 NetId（持久组），下次连接同网段时复用，免重复 WPS 确认。</summary>
        private int savedNetId = -1;
        public int PersistentNetId => savedNetId;

        public bool Enabled => enabled;
        public bool GroupFormed => groupFormed;
        public string LastError => lastError;
        public int DeviceCount => devices.Count;
        public int LinkedCount => linked.Count;

        public WlanP2pChannel(Context context)
        {
            this.context = context;
        }

        private void Log(string m)
        {
            lastError = m;
            try { OnLog?.Invoke(m); } catch { }
            Android.Util.Log.Info("WlanP2p", m);
        }

        /* ===================== 启停 ===================== */

        public bool Enable()
        {
            if (enabled) return true;
            try
            {
                manager = (WifiP2pManager)context.GetSystemService(Context.WifiP2pService);
                if (manager == null)
                {
                    Log(L10n.T("本机不支持 Wi-Fi P2P"));
                    return false;
                }
                channel = manager.Initialize(context, context.MainLooper, new ChListener());
                if (channel == null)
                {
                    Log(L10n.T("P2P 通道初始化失败"));
                    return false;
                }
                enabled = true;
                Log(L10n.T("WLAN Direct 渠道已启用"));
                return true;
            }
            catch (Exception ex)
            {
                Log(L10n.T("启用 WLAN Direct 失败: ") + ex.Message);
                enabled = false;
                return false;
            }
        }

        public void Disable()
        {
            try
            {
                if (manager != null && channel != null)
                {
                    try { manager.StopPeerDiscovery(channel, new NullAction()); } catch { }
                    try { manager.RemoveGroup(channel, new NullAction()); } catch { }
                }
            }
            catch { }
            enabled = false;
            groupFormed = false;
            linked.Clear();
            devices.Clear();
            Log(L10n.T("WLAN Direct 渠道已关闭"));
            try { OnPeersChanged?.Invoke(); } catch { }
        }

        /// <summary>开始扫描 P2P 设备（保持监听，可反复调用刷新）。</summary>
        public void Discover()
        {
            if (!enabled) Enable();
            if (!enabled || manager == null || channel == null) return;
            try { manager.DiscoverPeers(channel, new NullAction()); }
            catch (Exception ex) { Log(L10n.T("扫描 P2P 设备失败: ") + ex.Message); }
        }

        /// <summary>当前发现到的设备列表。</summary>
        public List<WlanP2pPeer> Peers()
        {
            var list = new List<WlanP2pPeer>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var kv in devices)
            {
                var p = kv.Value;
                if (p.Connected) p.LastSeen = now;
                list.Add(p);
            }
            foreach (var kv in linked)
            {
                if (!devices.ContainsKey(kv.Key)) list.Add(kv.Value);
            }
            return list;
        }

        /// <summary>已组网可直接投递的对端。</summary>
        public List<WlanP2pPeer> Linked()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return linked.Values.Where(p => now - p.LastSeen < PeerTimeoutMs).ToList();
        }

        /* ===================== 组网 ===================== */

        /// <summary>与某台设备建立 P2P 连接（GO 协商 + 组建网）。</summary>
        public void Connect(string mac)
        {
            if (!enabled) Enable();
            if (!enabled || manager == null || channel == null) return;
            try
            {
                var cfg = new WifiP2pConfig
                {
                    DeviceAddress = mac,
                    // 动态选主：电量越高越倾向当 GO（intent 0–15，高=更可能成为 GO）。
                    // GO 只是接入点，路由/去重/防泛洪在应用层 mesh，与谁当 GO 无关。
                    GroupOwnerIntent = ComputeGoIntent()
                };
                // 持久组：Android 框架会记住与同一设备组建过的组并自动复用（免重复 WPS 确认），
                // 本端只要不 RemoveGroup 就会保留；这里不再单独设置 NetId（该绑定未暴露该字段）。
                manager.Connect(channel, cfg, new ConnectListener(this));
            }
            catch (Exception ex) { Log(L10n.T("发起 P2P 连接失败: ") + ex.Message); }
        }

        /// <summary>
        /// 动态 GO 选举：按电量算 GroupOwnerIntent（0–15）。
        /// 电量高 → intent 高 → 更可能当 GO；电量低 → 让位当客户端，避免组主先耗尽。
        /// 注意：这只是链路层的接入点选择，**不是网络中心**——消息转发由应用层 mesh 决定。
        /// </summary>
        private int ComputeGoIntent()
        {
            int pct = 50;
            try
            {
                var bm = (Android.OS.BatteryManager)context.GetSystemService(Context.BatteryService);
                if (bm != null)
                {
                    int v = bm.GetIntProperty((int)Android.OS.BatteryProperty.Capacity);
                    if (v > 0 && v <= 100) pct = v;
                }
            }
            catch { }
            int intent = (int)Math.Round(pct / 100.0 * 15);
            if (intent < 0) intent = 0;
            if (intent > 15) intent = 15;
            return intent;
        }

        /// <summary>自动连接：选一台尚未连上的已发现设备发起组网（8 秒节流，避免刷屏）。</summary>
        public void TryAutoConnect()
        {
            if (!AutoConnect || groupFormed || !enabled || manager == null || channel == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - lastAutoConnectMs < 8000) return;

            string pick = null;
            foreach (var kv in devices)
            {
                var p = kv.Value;
                if (p == null || string.IsNullOrEmpty(p.Mac)) continue;
                if (p.Connected) continue;
                // 已在 linked 里（已组网）就跳过
                bool alreadyLinked = linked.Values.Any(l => string.Equals(l.Mac, p.Mac, StringComparison.OrdinalIgnoreCase));
                if (alreadyLinked) continue;
                pick = p.Mac;
                break;
            }
            if (string.IsNullOrEmpty(pick)) return;
            lastAutoConnectMs = now;
            Log(L10n.T("自动组网：连接 ") + pick);
            Connect(pick);
        }

        /// <summary>断开当前 P2P 组网。</summary>
        public void Disconnect()
        {
            try
            {
                if (manager != null && channel != null) manager.RemoveGroup(channel, new NullAction());
            }
            catch { }
        }

        /// <summary>
        /// 组网成功后调用：把组里的对端 IP 收进来，并做一次 hello 握手互认身份。
        /// 本机 P2P 网段地址从 NetworkInterface 里找 192.168.49.x/其它 P2P 地址。
        /// </summary>
        private void OnP2pGroup(WifiP2pGroup group)
        {
            try
            {
                if (group == null) return;
                groupFormed = true;
                try { if (group.NetworkId > 0) savedNetId = group.NetworkId; } catch { } // 记下持久组
                Log(L10n.T("P2P 组网成功：") + (group.NetworkName ?? "") + " owner=" + group.IsGroupOwner);

                var clients = group.ClientList;
                if (clients != null)
                {
                    foreach (var d in clients)
                    {
                        var ip = ParseIpFromDeviceName(d?.DeviceName);
                        if (!string.IsNullOrEmpty(ip)) AddLinked(ip, d?.DeviceName, d?.DeviceAddress);
                    }
                }

                // 组主那台：Owner 一定在列表里，但部分机型 ClientList 不含自己
                var owner = group.Owner;
                var ownerIp = ParseIpFromDeviceName(owner?.DeviceName);
                if (!string.IsNullOrEmpty(ownerIp)) AddLinked(ownerIp, owner?.DeviceName, owner?.DeviceAddress);

                try { OnGroupChanged?.Invoke(true); } catch { }
                try { OnPeersChanged?.Invoke(); } catch { }
            }
            catch (Exception ex) { Log(L10n.T("处理 P2P 组信息失败: ") + ex.Message); }
        }

        private void AddLinked(string ip, string deviceName, string mac)
        {
            try
            {
                var p = linked.GetOrAdd(ip, _ => new WlanP2pPeer
                {
                    Ip = ip,
                    DeviceName = deviceName,
                    Mac = mac
                });
                p.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                p.Connected = true;
                // 立刻打一次招呼，让对端也知道我们是谁、监听在哪个端口
                Handshake(ip);
                try { OnPeersChanged?.Invoke(); } catch { }
            }
            catch (Exception ex) { Log(L10n.T("登记 P2P 对端失败: ") + ex.Message); }
        }

        /// <summary>Android 在已连接设备上把 IPv4:port 塞在 DeviceName 里。</summary>
        public static string ParseIpFromDeviceName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return null;
            var s = deviceName.Trim();
            var idx = s.IndexOf(':');
            if (idx > 0) s = s.Substring(0, idx);
            // 只要 IPv4
            var parts = s.Split('.');
            if (parts.Length == 4 && parts.All(p => int.TryParse(p, out var n) && n >= 0 && n <= 255)) return s;
            return null;
        }

        /// <summary>本机在 P2P 网段上的地址（优先 192.168.49.x）。
        /// Android 上走 java.net.NetworkInterface，避免读 /sys/class/net 触发 SELinux AVC。</summary>
        public static string LocalP2pAddress()
        {
            string fallback = null;
            try
            {
#if __ANDROID__
                var en = Java.Net.NetworkInterface.NetworkInterfaces;
                while (en.HasMoreElements)
                {
                    var j = (Java.Net.NetworkInterface)en.NextElement();
                    if (!j.IsUp) continue;
                    foreach (Java.Net.InterfaceAddress ia in j.InterfaceAddresses)
                    {
                        var addr = ia.Address;
                        if (addr == null || !(addr is Java.Net.Inet4Address)) continue;
                        string s = null;
                        try { s = addr.HostAddress; } catch { }
                        if (string.IsNullOrEmpty(s)) continue;
                        if (s.StartsWith("192.168.49.")) return s;
                        if (s.StartsWith("192.168.48.")) fallback = s;
                    }
                }
#else
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ua.Address.ToString();
                        if (s.StartsWith("192.168.49.")) return s;
                        if (s.StartsWith("192.168.48.")) fallback = s;
                    }
                }
#endif
            }
            catch { }
            return fallback;
        }

        /* ===================== 帧收发 ===================== */

        /// <summary>向某个 P2P 对端发一帧（复用局域网 TCP 帧格式）。</summary>
        public void SendFrame(string ip, int port, string headerJson, byte[] body)
        {
            if (string.IsNullOrEmpty(ip)) return;
            Task.Run(() =>
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        var t = client.ConnectAsync(ip, port <= 0 ? 45778 : port);
                        if (!t.Wait(4000)) return;
                        client.SendTimeout = 15000;
                        using (var s = client.GetStream())
                        {
                            WriteFrame(s, headerJson, body);
                            s.Flush();
                        }
                    }
                }
                catch (Exception ex) { Log(L10n.T("P2P 投递失败(") + ip + "): " + ex.Message); }
            });
        }

        /// <summary>群发到所有已组网的 P2P 对端。</summary>
        public void BroadcastFrame(string headerJson, byte[] body, int port)
        {
            foreach (var p in Linked())
                SendFrame(p.Ip, port, headerJson, body);
        }

        /// <summary>互认：告诉对端「我是谁、我的 TCP 端口是多少」，并解析对端回礼。</summary>
        private void Handshake(string ip)
        {
            Task.Run(() =>
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        var t = client.ConnectAsync(ip, 45778);
                        if (!t.Wait(4000)) return;
                        client.ReceiveTimeout = 6000;
                        using (var s = client.GetStream())
                        {
                            WriteFrame(s, HelloJson(), null);
                            s.Flush();
                            // 读对方的 hello（尽力而为）
                            try
                            {
                                if (TryReadFrame(s, out var hJson, out _))
                                {
                                    var jo = JObject.Parse(hJson);
                                    var id = (string)jo["id"];
                                    var name = (string)jo["name"];
                                    var p = linked.GetOrAdd(ip, _ => new WlanP2pPeer { Ip = ip });
                                    p.Id = id;
                                    p.Name = name;
                                    p.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    p.Connected = true;
                                    if (OnFrame != null) { /* hello 只更新身份，不进会话 */ }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            });
        }

        /// <summary>由桥接层注入：本机身份，用于 P2P hello。</summary>
        public Func<string> HelloProvider;

        private string HelloJson()
        {
            var hello = HelloProvider != null ? HelloProvider() : "{}";
            return hello;
        }

        /// <summary>把 hello 的对端身份并进 P2P 节点表。</summary>
        public void ApplyHello(string ip, string id, string name)
        {
            var p = linked.GetOrAdd(ip, _ => new WlanP2pPeer { Ip = ip });
            p.Id = id;
            p.Name = name;
            p.Connected = true;
            p.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try { OnPeersChanged?.Invoke(); } catch { }
        }

        /* ===================== 帧编解码（与局域网一致） ===================== */

        public static void WriteFrame(Stream stream, string headerJson, byte[] body)
        {
            var hb = Encoding.UTF8.GetBytes(headerJson ?? "{}");
            var len = new byte[4];
            WriteInt32(len, hb.Length);
            stream.Write(len, 0, 4);
            stream.Write(hb, 0, hb.Length);
            var blen = new byte[4];
            WriteInt32(blen, body == null ? 0 : body.Length);
            stream.Write(blen, 0, 4);
            if (body != null && body.Length > 0) stream.Write(body, 0, body.Length);
        }

        public static bool TryReadFrame(Stream stream, out string headerJson, out byte[] body)
        {
            headerJson = null;
            body = null;
            var lb = ReadExact(stream, 4);
            if (lb == null) return false;
            var hlen = ReadInt32(lb, 0);
            if (hlen <= 0 || hlen > 1024 * 1024) return false;
            var hb = ReadExact(stream, hlen);
            if (hb == null) return false;
            headerJson = Encoding.UTF8.GetString(hb);

            var bb = ReadExact(stream, 4);
            if (bb == null) return false;
            var blen = ReadInt32(bb, 0);
            if (blen > 0)
            {
                if (blen > 64 * 1024 * 1024) return false;
                body = ReadExact(stream, blen);
                if (body == null) return false;
            }
            return true;
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r;
                try { r = s.Read(buf, off, n - off); }
                catch { return null; }
                if (r <= 0) return null;
                off += r;
            }
            return buf;
        }

        private static void WriteInt32(byte[] b, int v)
        {
            b[0] = (byte)((v >> 24) & 0xFF);
            b[1] = (byte)((v >> 16) & 0xFF);
            b[2] = (byte)((v >> 8) & 0xFF);
            b[3] = (byte)(v & 0xFF);
        }

        private static int ReadInt32(byte[] b, int off)
        {
            return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
        }

        /* ===================== 系统回调适配 ===================== */

        private class NullAction : Java.Lang.Object, WifiP2pManager.IActionListener
        {
            public void OnSuccess() { }
            public void OnFailure(WifiP2pFailureReason reason) { }
        }

        private class ChListener : Java.Lang.Object, WifiP2pManager.IChannelListener
        {
            public void OnChannelDisconnected() { }
        }

        private class PeerCb : Java.Lang.Object, WifiP2pManager.IPeerListListener
        {
            private readonly WlanP2pChannel owner;
            public PeerCb(WlanP2pChannel o) { owner = o; }
            public void OnPeersAvailable(WifiP2pDeviceList list)
            {
                try
                {
                    if (list == null) return;
                    var dl = list.DeviceList;
                    if (dl == null) return;
                    bool changed = false;
                    foreach (var d in dl)
                    {
                        var mac = d?.DeviceAddress;
                        if (string.IsNullOrEmpty(mac)) continue;
                        var cur = owner.devices.GetOrAdd(mac, _ => new WlanP2pPeer { Mac = mac });
                        cur.DeviceName = d.DeviceName;
                        cur.Mac = mac;
                        changed = true;
                    }
                    if (changed)
                    {
                        owner.Log(L10n.T("发现 ") + owner.devices.Count + L10n.T(" 台 P2P 设备"));
                        try { owner.OnPeersChanged?.Invoke(); } catch { }
                    }
                    // 无感自组网：发现设备且未组网时自动发起连接（内部有 8s 节流）
                    if (owner.AutoConnect && !owner.groupFormed) owner.TryAutoConnect();
                }
                catch { }
            }
        }

        private class GroupCb : Java.Lang.Object, WifiP2pManager.IGroupInfoListener
        {
            private readonly WlanP2pChannel owner;
            public GroupCb(WlanP2pChannel o) { owner = o; }
            public void OnGroupInfoAvailable(WifiP2pGroup group)
            {
                try { owner.OnP2pGroup(group); } catch { }
            }
        }

        private class InfoCb : Java.Lang.Object, WifiP2pManager.IConnectionInfoListener
        {
            private readonly WlanP2pChannel owner;
            public InfoCb(WlanP2pChannel o) { owner = o; }
            public void OnConnectionInfoAvailable(WifiP2pInfo info)
            {
                try
                {
                    if (info == null) return;
                    if (info.GroupFormed) owner.manager?.RequestGroupInfo(owner.channel, new GroupCb(owner));
                    else if (owner.groupFormed)
                    {
                        owner.groupFormed = false;
                        owner.linked.Clear();
                        owner.Log(L10n.T("P2P 组网已断开"));
                        try { owner.OnGroupChanged?.Invoke(false); } catch { }
                    }
                }
                catch { }
            }
        }

        private class ConnectListener : Java.Lang.Object, WifiP2pManager.IActionListener
        {
            private readonly WlanP2pChannel owner;
            public ConnectListener(WlanP2pChannel o) { owner = o; }
            public void OnSuccess()
            {
                try { owner.manager?.RequestConnectionInfo(owner.channel, new InfoCb(owner)); } catch { }
            }
            public void OnFailure(WifiP2pFailureReason reason)
            {
                owner.Log(L10n.T("P2P 连接被拒：") + reason);
            }
        }

        /// <summary>轮询组信息（用于按钮点击时主动刷新一次）。</summary>
        public void RefreshGroup()
        {
            try
            {
                if (!enabled || manager == null || channel == null) return;
                manager.RequestPeers(channel, new PeerCb(this));
                manager.RequestConnectionInfo(channel, new InfoCb(this));
                manager.RequestGroupInfo(channel, new GroupCb(this));
            }
            catch { }
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AhoChatA
{
    /// <summary>
    /// libp2p 的 mDNS 局域网发现（spec: libp2p/discovery/mdns）。
    ///
    /// qaul 的 LAN 模块只靠 mDNS 找邻居（libqaul/src/connections/lan.rs 里
    /// `mdns::tokio::Behaviour` + `floodsub.add_node_to_partial_view`），所以
    /// **本应用必须先能被 mDNS 发现，qaul 才会与我们建立连接**。
    ///
    /// 报文格式（与 rust-libp2p / js-libp2p 一致）：
    ///   查询：PTR `_p2p._udp.local`
    ///   应答：PTR `_p2p._udp.local` → `<base58 peerid>._p2p._udp.local`
    ///         TXT `<base58 peerid>._p2p._udp.local` → `dnsaddr=/ip4/x.x.x.x/tcp/port/p2p/<peerid>`
    ///   对端就是从 TXT 的 dnsaddr 里解出「地址 + peer id」的，所以 **/p2p/ 段不能省**。
    /// </summary>
    public sealed class MdnsService
    {
        private const string ServiceTag = "_p2p._udp.local";
        private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;

        private readonly string peerIdBase58;
        private readonly int tcpPort;
        private readonly object sendLock = new object();

        private Socket sock;
        private Thread loop;
        private volatile bool running;
        private Timer queryTimer;
        private int lastRxLog;
        private readonly object seenLock = new object();
        private readonly Dictionary<string, int> seenAt = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>发现对端：peerId(base58) + 可用 TCP 端点。</summary>
        public Action<string, List<IPEndPoint>> OnPeer;

        public bool Running { get { return running; } }
        public int Discovered { get; private set; }

        public MdnsService(string peerIdBase58, int tcpPort)
        {
            this.peerIdBase58 = peerIdBase58;
            this.tcpPort = tcpPort;
        }

        public bool Start()
        {
            if (running) return true;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // 允许发子网广播（否则 Android 直接 Access denied），
                // 并允许组播回环，方便本机多网卡场景
                try { sock.EnableBroadcast = true; } catch { }
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true); } catch { }
                // mDNS 必须从 5353 收发，否则对端不会接受应答
                sock.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); } catch { }
                // 逐网卡加入组播组：Android 上只 join 默认网卡有时收不到应答，
                // 显式按接口索引 join 更稳（WiFi + 热点都要覆盖）。
                // 注意跳过 loopback：配合下面的 MulticastLoopback=true，
                // join 了 lo 会让本机自己发的每个包都被回环投递一遍，白耗电。
                IPAddress bestIfaceIp = null;
                foreach (var ni in GetInterfaces())
                {
                    if (!ni.Up) continue;
                    if (ni.Loopback) continue;

                    try
                    {
                        IPAddress v4 = null;
                        foreach (var ua in ni.Unicast)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork) { v4 = ua.Address; break; }
                        }
                        if (v4 == null) continue;

                        // 用网卡自身的 IPv4 地址加入组播组（等价于指定接口索引，
                        // 但不需要读 sysfs 拿 idx —— 正好避开 SELinux 对 /sys/class/net 的 AVC 拒绝）
                        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(Group, v4));
                        // 出站组播绑定到哪块网卡：原先在循环里无脑覆盖，
                        // 结果是"最后枚举到的网卡"胜出——多网卡时组播全从一张卡出去。
                        // 这里按 wlan > ap > eth > 其他 挑一张，只设一次。
                        if (bestIfaceIp == null || BetterIface(ni.Name, IfaceNameOf(bestIfaceIp)))
                            bestIfaceIp = v4;
                        Android.Util.Log.Info("Mdns", "组播加入 " + ni.Name + " (" + v4 + ")");
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Mdns", "加入组播失败 " + ni.Name + ": " + ex.Message);
                    }
                }

                // 出站组播固定走挑出来的那张网卡（只设一次，不被后面的网卡覆盖）
                if (bestIfaceIp != null)
                {
                    try
                    {
                        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                            bestIfaceIp.GetAddressBytes());
                        Android.Util.Log.Info("Mdns", "出站组播网卡 → " + bestIfaceIp);
                    }
                    catch { }
                }

                running = true;
                loop = new Thread(ReadLoop);
                loop.IsBackground = true;
                loop.Name = "mdns";
                loop.Start();

                SendQuery();
                queryTimer = new Timer(_ => { try { SendQuery(); } catch { } }, null, 30000, 30000);
                Android.Util.Log.Info("Mdns", "已启动，服务名 " + ServiceTag + "，peer " + peerIdBase58);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mdns", "启动失败: " + ex.Message);
                return false;
            }
        }

        public void Stop()
        {
            running = false;
            try { if (queryTimer != null) queryTimer.Dispose(); } catch { }
            try { if (sock != null) sock.Close(); } catch { }
            sock = null;
        }

        /* ===================== 收发 ===================== */

        /// <summary>网卡名优先级：Wi-Fi 客户端 &gt; 热点 &gt; 有线 &gt; 其他。</summary>
        private static int IfaceRank(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (name.StartsWith("wlan")) return 40;
            if (name.StartsWith("ap")) return 30;
            if (name.StartsWith("eth")) return 20;
            return 10;
        }

        private static bool BetterIface(string candidate, string current)
        {
            return IfaceRank(candidate) > IfaceRank(current);
        }

        /// <summary>网卡信息（与 java.net.NetworkInterface 对接，避免在 Android 上读 /sys/class/net 触发 SELinux AVC）。</summary>
        private sealed class IfaceInfo
        {
            public string Name;
            public bool Up;
            public bool Loopback;
            public readonly List<IfaceUnicast> Unicast = new List<IfaceUnicast>();
            public sealed class IfaceUnicast
            {
                public IPAddress Address;
                public IPAddress Mask;
            }
        }

        /// <summary>
        /// 枚举本机「已启用、非环回」的 IPv4 网卡。
        /// 优先走 Java 的 java.net.NetworkInterface —— 它在 Android 上不读 /sys/class/net，
        /// 不会触发 SELinux 的 AVC 拒绝（.NET 的 NetworkInterface.GetAllNetworkInterfaces 会）。
        /// 拿不到时再回退到 .NET 实现（PC 上没问题，但 Android 上会产生 AVC 噪声）。
        /// </summary>
        private static List<IfaceInfo> GetInterfaces()
        {
            var result = new List<IfaceInfo>();
#if __ANDROID__
            try
            {
                var en = Java.Net.NetworkInterface.NetworkInterfaces;
                while (en.HasMoreElements)
                {
                    var j = (Java.Net.NetworkInterface)en.NextElement();
                    var info = new IfaceInfo { Name = j.Name, Up = j.IsUp, Loopback = j.IsLoopback };
                    foreach (Java.Net.InterfaceAddress ia in j.InterfaceAddresses)
                    {
                        var addr = ia.Address;
                        if (addr == null || !(addr is Java.Net.Inet4Address)) continue;
                        IPAddress ip;
                        try { ip = IPAddress.Parse(addr.HostAddress); }
                        catch { continue; }
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        IPAddress mask = null;
                        try { mask = PrefixToMask(ia.NetworkPrefixLength); } catch { }
                        info.Unicast.Add(new IfaceInfo.IfaceUnicast { Address = ip, Mask = mask });
                    }
                    result.Add(info);
                }
                return result;
            }
            catch
            {
                // Java 枚举失败才回退到 .NET；正常情况下 Android 上走上面的 Java 路径，
                // 不读 /sys/class/net，不会触发 SELinux 的 AVC 拒绝。
            }
#endif
            // .NET 实现（PC 调试 / Java 路径异常时的兜底；Android 上会产生 AVC 噪声，故仅兜底用）
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var info = new IfaceInfo
                {
                    Name = ni.Name,
                    Up = ni.OperationalStatus == OperationalStatus.Up,
                    Loopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                };
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        info.Unicast.Add(new IfaceInfo.IfaceUnicast { Address = ua.Address, Mask = ua.IPv4Mask });
                    }
                result.Add(info);
            }
            return result;
        }

        private static IPAddress PrefixToMask(int prefix)
            {
            if (prefix <= 0 || prefix > 32) return IPAddress.Parse("255.255.255.0");
            uint mask = prefix >= 32 ? 0xFFFFFFFF : (~((1u << (32 - prefix)) - 1));
            var b = new byte[] { (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask };
            return new IPAddress(b);
        }

        private static string IfaceNameOf(IPAddress ip)
        {
            try
            {
                foreach (var ni in GetInterfaces())
                {
                    if (!ni.Up) continue;
                    foreach (var ua in ni.Unicast)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.Address.Equals(ip)) return ni.Name;
                    }
                }
            }
            catch { }
            return null;
        }

        private void SendQuery()
        {
            var pkt = Dns.BuildQuery(0, ServiceTag);
            SendTo(pkt, new IPEndPoint(Group, MdnsPort));

            // 组播在部分路由器/省电策略下会被丢，补一发子网广播（mDNS 兼容实现普遍都收）
            foreach (var b in BroadcastAddresses())
            {
                SendTo(pkt, new IPEndPoint(b, MdnsPort));
            }
        }

        /// <summary>把每个网卡的广播地址算出来（够局域网用）。</summary>
        private List<IPAddress> BroadcastAddresses()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in GetInterfaces())
                {
                    if (!ni.Up) continue;
                    if (ni.Loopback) continue;
                    foreach (var ua in ni.Unicast)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = ua.Address.GetAddressBytes();
                        var mask = (ua.Mask != null && ua.Mask.GetAddressBytes().Length == 4)
                            ? ua.Mask.GetAddressBytes() : new byte[] { 255, 255, 255, 0 };
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(b[i] | (~mask[i] & 0xFF));
                        list.Add(new IPAddress(bc));
                    }
                }
            }
            catch { }
            return list;
        }

        private void SendTo(byte[] data, IPEndPoint ep)
        {
            lock (sendLock)
            {
                try { if (sock != null) sock.SendTo(data, ep); }
                catch (Exception ex) { Android.Util.Log.Warn("Mdns", "发送失败: " + ex.Message); }
            }
        }

        private void ReadLoop()
        {
            var buf = new byte[9000];
            while (running)
            {
                try
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int n = sock.ReceiveFrom(buf, ref remote);
                    if (n <= 0) continue;

                    var local = (IPEndPoint)remote;
                    var msg = Dns.Parse(buf, n);

                    // 应答必须逐条记录（发现失败时要能看到到底有没有回包），查询限流
                    var isResp = msg != null && !msg.IsQuery;
                    if (isResp || Environment.TickCount - lastRxLog > 10000)
                    {
                        if (!isResp) lastRxLog = Environment.TickCount;
                        Android.Util.Log.Info("Mdns", "收到 " + n + " 字节 from " + local +
                            (msg == null ? "（解析失败）" : (msg.IsQuery ? "（查询）" : "（应答 " + msg.Answers.Count + " 条）")));
                    }

                    if (msg == null) continue;

                    if (msg.IsQuery)
                    {
                        if (msg.HasQuestion(ServiceTag, 12))
                        {
                            var answers = BuildAnnounce();
                            // 组播应答（标准做法）+ 单播回问询者（更稳）
                            SendTo(answers, new IPEndPoint(Group, MdnsPort));
                            if (local.Port != MdnsPort) SendTo(answers, local);
                        }
                        continue;
                    }

                    HandleResponse(msg);
                }
                catch (Exception ex)
                {
                    if (running) Android.Util.Log.Warn("Mdns", "接收异常: " + ex.Message);
                    Thread.Sleep(200);
                }
            }
        }

        private void HandleResponse(Dns.Message msg)
        {
            var found = ExtractPeers(msg, peerIdBase58);
            foreach (var kv in found)
            {
                // 同一邻居 60 秒内只处理一次：mDNS 每 5~30 秒就会重复应答，
                // 不节流会刷屏并反复触发拨号
                lock (seenLock)
                {
                    int last;
                    if (seenAt.TryGetValue(kv.Key, out last) && Environment.TickCount - last < 60000) continue;
                    seenAt[kv.Key] = Environment.TickCount;
                }

                Android.Util.Log.Info("Mdns", "发现邻居 " + kv.Key.Substring(0, Math.Min(12, kv.Key.Length)) +
                                              " @ " + kv.Value[0]);
                Discovered++;
                var cb = OnPeer;
                if (cb != null) cb(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// 从一条 mDNS 应答里抽出「PeerId → 端点」。
        ///
        /// 关键点（照 libp2p 的 gotResponse 实现）：
        ///   · 只要求存在服务名匹配的 PTR，**不**用它的标签当 PeerId
        ///     —— js-libp2p 的标签是哈希主机名（h53qwmhg…），不是 Base58 PeerId；
        ///   · PeerId 一律从 TXT 的 dnsaddr 的 `/p2p/` 段取；
        ///   · TXT 可能落在 additional 段，Dns.Parse 已把三段合并。
        /// </summary>
        public static Dictionary<string, List<IPEndPoint>> ExtractPeers(Dns.Message msg, string selfBase58)
        {
            var result = new Dictionary<string, List<IPEndPoint>>(StringComparer.Ordinal);
            if (msg == null) return result;

            bool hasPtr = false;
            foreach (var rr in msg.Answers)
            {
                if (rr.Type == 12 && rr.Name.Equals(ServiceTag, StringComparison.OrdinalIgnoreCase))
                {
                    hasPtr = true;
                    break;
                }
            }
            if (!hasPtr) return result;

            foreach (var rr in msg.Answers)
            {
                if (rr.Type != 16) continue;
                var parsed = ParseDnsAddr(rr.Text);
                if (parsed == null) continue;
                if (parsed.Item1 == selfBase58) continue;

                List<IPEndPoint> list;
                if (!result.TryGetValue(parsed.Item1, out list))
                {
                    list = new List<IPEndPoint>();
                    result[parsed.Item1] = list;
                }
                list.Add(parsed.Item2);
            }
            return result;
        }

        /// <summary>
        /// 解析 `/ip4/1.2.3.4/tcp/1234/p2p/12D3Koo…`（TXT 里的 dnsaddr 值）。
        /// 返回 (peerIdBase58, 端点)；格式不完整返回 null。
        /// </summary>
        private static Tuple<string, IPEndPoint> ParseDnsAddr(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (!text.StartsWith("dnsaddr=", StringComparison.OrdinalIgnoreCase)) return null;
            var ma = text.Substring("dnsaddr=".Length);

            var parts = ma.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string ip = null, peer = null;
            int port = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "ip4" && i + 1 < parts.Length) ip = parts[i + 1];
                else if (parts[i] == "tcp" && i + 1 < parts.Length) int.TryParse(parts[i + 1], out port);
                else if (parts[i] == "p2p" && i + 1 < parts.Length) peer = parts[i + 1];
            }

            IPAddress parsed;
            if (ip == null || port <= 0 || string.IsNullOrEmpty(peer)) return null;
            if (!IPAddress.TryParse(ip, out parsed)) return null;
            return Tuple.Create(peer, new IPEndPoint(parsed, port));
        }

        /* ===================== 应答报文 ===================== */

        private List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in GetInterfaces())
                {
                    if (!ni.Up) continue;
                    if (ni.Loopback) continue;

                    foreach (var ua in ni.Unicast)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;      // 链路本地，qaul 不接受
                        list.Add(ip);
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mdns", "枚举网卡失败: " + ex.Message);
            }
            return list;
        }

        private byte[] BuildAnnounce()
        {
            var peerName = peerIdBase58 + "." + ServiceTag;
            var answers = new List<Dns.Record>();

            answers.Add(Dns.Record.Ptr(ServiceTag, peerName));

            foreach (var ip in LocalAddresses())
            {
                var ma = "/ip4/" + ip + "/tcp/" + tcpPort + "/p2p/" + peerIdBase58;
                answers.Add(Dns.Record.Txt(peerName, "dnsaddr=" + ma));
                // SRV + A 是老实现/其他客户端可能依赖的记录，一并给出
                answers.Add(Dns.Record.Srv(peerName, tcpPort, peerName));
                IPAddress parsed;
                if (IPAddress.TryParse(ip, out parsed))
                    answers.Add(Dns.Record.A(peerName, parsed));
            }

            return Dns.BuildResponse(0, answers);
        }
    }

    /// <summary>最小可用的 DNS 报文编解码（够 mDNS 用：PTR/TXT/SRV/A + 名称压缩指针）。</summary>
    public static class Dns
    {
        public sealed class Record
        {
            public string Name;
            public int Type;
            public string Text;        // PTR/TXT 用
            public int Port;           // SRV 用
            public IPAddress Address;  // A 用

            public static Record Ptr(string name, string target)
            {
                return new Record { Name = name, Type = 12, Text = target };
            }

            public static Record Txt(string name, string text)
            {
                return new Record { Name = name, Type = 16, Text = text };
            }

            public static Record Srv(string name, int port, string target)
            {
                return new Record { Name = name, Type = 33, Port = port, Text = target };
            }

            public static Record A(string name, IPAddress addr)
            {
                return new Record { Name = name, Type = 1, Address = addr };
            }
        }

        public sealed class Question
        {
            public string Name;
            public int Type;
        }

        public sealed class Message
        {
            public bool IsQuery;
            public readonly List<Question> Questions = new List<Question>();
            public readonly List<Record> Answers = new List<Record>();

            public IEnumerable<Record> AllRecords { get { return Answers; } }

            public bool HasQuestion(string name, int type)
            {
                foreach (var q in Questions)
                    if (q.Type == type && q.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        /* ---------- 编码 ---------- */

        public static byte[] BuildQuery(ushort txid, string name)
        {
            var ms = new MemoryStream();
            WriteHeader(ms, txid, 0x0000, 1, 0, 0, 0);
            WriteName(ms, name);
            WriteBE16(ms, 12);   // PTR
            WriteBE16(ms, 1);    // IN
            return ms.ToArray();
        }

        public static byte[] BuildResponse(ushort txid, List<Record> records)
        {
            var ms = new MemoryStream();
            WriteHeader(ms, txid, 0x8400, 0, records.Count, 0, 0);

            foreach (var rr in records)
            {
                WriteName(ms, rr.Name);
                WriteBE16(ms, rr.Type);
                WriteBE16(ms, 1);        // IN
                // TTL 300s：与 qaul 修正后的 mDNS 配置一致（docs/research/mdns-bug.md
                // 给出的 ttl=300s、query_interval=30s）。原来写 120s，而查询周期是 30s，
                // 只要连丢 4 次查询记录就过期，邻居会被当成"已消失"移出视图；
                // 300s 意味着要连丢 10 次才移除，过期只作为真正的死节点检测。
                WriteBE32(ms, 300);      // TTL

                var rdata = new MemoryStream();
                if (rr.Type == 12 || rr.Type == 33)
                {
                    if (rr.Type == 33)
                    {
                        WriteBE16(rdata, 0);          // priority
                        WriteBE16(rdata, 0);          // weight
                        WriteBE16(rdata, rr.Port);
                    }
                    WriteName(rdata, rr.Text);
                }
                else if (rr.Type == 16)
                {
                    var txt = Encoding.UTF8.GetBytes(rr.Text ?? "");
                    rdata.WriteByte((byte)Math.Min(255, txt.Length));
                    rdata.Write(txt, 0, Math.Min(255, txt.Length));
                }
                else if (rr.Type == 1)
                {
                    var b = rr.Address.GetAddressBytes();
                    rdata.Write(b, 0, b.Length);
                }

                var rd = rdata.ToArray();
                WriteBE16(ms, rd.Length);
                ms.Write(rd, 0, rd.Length);
            }
            return ms.ToArray();
        }

        private static void WriteHeader(Stream s, ushort txid, ushort flags,
                                        int qd, int an, int ns, int ar)
        {
            WriteBE16(s, txid);
            WriteBE16(s, flags);
            WriteBE16(s, qd);
            WriteBE16(s, an);
            WriteBE16(s, ns);
            WriteBE16(s, ar);
        }

        private static void WriteBE16(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        private static void WriteBE32(Stream s, long v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        private static void WriteName(Stream s, string name)
        {
            if (string.IsNullOrEmpty(name)) { s.WriteByte(0); return; }
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                s.WriteByte((byte)b.Length);
                s.Write(b, 0, b.Length);
            }
            s.WriteByte(0);
        }

        /* ---------- 解码 ---------- */

        public static Message Parse(byte[] buf, int len)
        {
            try
            {
                if (len < 12) return null;
                var msg = new Message();
                int flags = (buf[2] << 8) | buf[3];
                msg.IsQuery = (flags & 0x8000) == 0;

                int qd = (buf[4] << 8) | buf[5];
                int an = (buf[6] << 8) | buf[7];
                int ns = (buf[8] << 8) | buf[9];
                int ar = (buf[10] << 8) | buf[11];
                int pos = 12;

                for (int i = 0; i < qd; i++)
                {
                    string name;
                    pos = ReadName(buf, len, pos, out name);
                    if (pos < 0 || pos + 4 > len) return null;
                    int type = (buf[pos] << 8) | buf[pos + 1];
                    pos += 4;
                    msg.Questions.Add(new Question { Name = name, Type = type });
                }

                // 应答记录可能分散在 answer / authority / additional 三段里：
                // libp2p 的响应常把 TXT(dnsaddr) 放进 additional，只读 answer 会拿不到地址。
                int totalRR = an + ns + ar;
                for (int i = 0; i < totalRR; i++)
                {
                    string name;
                    pos = ReadName(buf, len, pos, out name);
                    if (pos < 0 || pos + 10 > len) return null;

                    int type = (buf[pos] << 8) | buf[pos + 1];
                    int rdlen = (buf[pos + 8] << 8) | buf[pos + 9];
                    pos += 10;

                    if (pos + rdlen > len) return null;
                    int rdStart = pos;
                    pos += rdlen;

                    var rr = new Record { Name = name, Type = type };
                    if (type == 12)
                    {
                        string target;
                        ReadName(buf, len, rdStart, out target);
                        rr.Text = target;
                    }
                    else if (type == 16)
                    {
                        // TXT 可能有多段，本应用只关心 dnsaddr= 那一段
                        int p = rdStart;
                        var sb = new StringBuilder();
                        while (p < rdStart + rdlen)
                        {
                            int l = buf[p++];
                            if (l <= 0 || p + l > rdStart + rdlen) break;
                            if (sb.Length > 0) sb.Append('\u0001');
                            sb.Append(Encoding.UTF8.GetString(buf, p, l));
                            p += l;
                        }
                        rr.Text = sb.ToString();
                    }
                    else if (type == 33)
                    {
                        if (rdlen >= 6)
                        {
                            rr.Port = (buf[rdStart + 4] << 8) | buf[rdStart + 5];
                            string target;
                            ReadName(buf, len, rdStart + 6, out target);
                            rr.Text = target;
                        }
                    }
                    else if (type == 1 && rdlen == 4)
                    {
                        rr.Address = new IPAddress(new[] { buf[rdStart], buf[rdStart + 1], buf[rdStart + 2], buf[rdStart + 3] });
                    }
                    msg.Answers.Add(rr);
                }

                return msg;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读域名（支持 0xC0 压缩指针）；返回新位置。</summary>
        private static int ReadName(byte[] buf, int len, int pos, out string name)
        {
            name = "";
            var sb = new StringBuilder();
            int guard = 0;
            int jumped = -1;

            while (pos < len && guard++ < 64)
            {
                int b = buf[pos];
                if (b == 0)
                {
                    pos++;
                    name = sb.ToString();
                    return jumped >= 0 ? jumped : pos;
                }

                if ((b & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= len) return -1;
                    int ptr = ((b & 0x3F) << 8) | buf[pos + 1];
                    if (jumped < 0) jumped = pos + 2;
                    pos = ptr;
                    continue;
                }

                pos++;
                if (pos + b > len) return -1;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(buf, pos, b));
                pos += b;
            }
            return -1;
        }
    }
}

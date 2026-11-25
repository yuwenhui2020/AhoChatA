using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>
    /// 真正的 qaul 网络节点（libp2p 层）。
    ///
    /// 为什么需要它：qaul 的节点之间**不是**用 9199 端口通信的，9199 只是 qauld 给本地
    /// 客户端（CLI/UI）用的 RPC 端口。qaul 节点之间走的是 libp2p：
    ///
    ///   mDNS 发现 → TCP → Noise(XX) 加密 → yamux 多路复用
    ///        → /qaul_info/1.0.0     交换路由表（谁在线、几跳）
    ///        → /qaul_messaging/1.0.0 点对点消息（容器 + 加密信封）
    ///        → /floodsub/1.0.0      广播（topic "pages"，全局 feed）
    ///
    /// 本类把这条链路完整实现了一遍，所以 qaul 客户端能把本应用当成一个真实的邻居节点：
    /// 在「网络/邻居」里看到它，在「用户」里看到本机账号，并能收到我们发的 feed。
    /// </summary>
    /// <summary>
    /// 一条 qaul 公开信息（Feed）。
    ///
    /// libqaul 里 feed 的 message_id **就是**发送者对消息体的 ed25519 签名
    /// （`services/feed/mod.rs::save_message(signature, message)`），
    /// 双方同步时正是用这批 id 去比对「你缺哪几条」。这里沿用同一约定，
    /// 否则官方客户端给的 id 我们无法在本地查表。
    /// </summary>
    public sealed class FeedMessage
    {
        public string IdHex;       // message_id（＝签名）的十六进制形态
        public byte[] Sender;      // 38 字节 PeerId（内含发送者公钥）
        public string Content;
        public long Time;

        public byte[] Id { get { return LocalIdentity.Unhex(IdHex); } }
    }

    public sealed class QaulNode
    {
        public const int DefaultPort = 9200;
        /// <summary>qaul 官方 QUIC 端口（udp/9229/quic-v1）。</summary>
        public const int DefaultQuicPort = 9229;

        /// <summary>qaul floodsub 的全局 topic（Node::new 里写死为 "pages"）。</summary>
        public const string FeedTopic = "pages";

        private readonly LocalIdentity identity;
        private readonly string displayName;
        private readonly int port;
        private readonly string storeDir;

        /// <summary>加密与消息服务（KK 会话 + 私聊/群聊/邀请）。</summary>
        public QaulCrypto Crypto;
        public QaulMessaging Messaging;

        /// <summary>用户 → 下一跳邻居（从路由信息里学到的 1 跳/多跳路径）。</summary>
        private readonly Dictionary<string, string> routes = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>已经发过 USER_REQUEST 的用户 → 上次请求时间（带冷却，避免刷请求又不至于永久放弃）。</summary>
        private readonly Dictionary<string, long> requested = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>静态节点（跨网段/公网直连用）地址列表。</summary>
        private readonly List<IPEndPoint> staticPeers = new List<IPEndPoint>();
        /// <summary>走 QUIC 的静态节点（qaul 官方公共节点就是 udp/9229/quic-v1）。</summary>
        private readonly List<IPEndPoint> staticPeersQuic = new List<IPEndPoint>();
        /// <summary>正在拨号的目标（防止同一地址并发建连，见 DialQuic 里的说明）。</summary>
        private readonly HashSet<string> dialing = new HashSet<string>();
        /// <summary>IPv6 是否可用（不可用自动禁用，避免反复报错）。</summary>
        public bool Ipv6Available { get; private set; }
        private TcpListener listener6;
        private Thread staticDialThread;
        private readonly HashSet<string> staticTried = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>发现过/连上过的邻居端点缓存（重启后自动重拨，不依赖不稳定的 mDNS 组播）。</summary>
        private readonly Dictionary<string, IPEndPoint> peerCache = new Dictionary<string, IPEndPoint>(StringComparer.Ordinal);
        /// <summary>peerCache 的插入顺序（满员时淘汰最旧的一条，而不是整表清空）。</summary>
        private readonly List<string> peerCacheOrder = new List<string>();
        private readonly object peerCacheLock = new object();
        private string peerCachePath;

        private TcpListener listener;
        private MdnsService mdns;
        private volatile bool running;
        /// <summary>空闲（灭屏/后台）时拉长各类维护循环的周期。</summary>
        private volatile bool idle;
        private Thread acceptThread, infoThread, dialThread;
        private byte[] staticSeed;

        private readonly Dictionary<string, Conn> conns = new Dictionary<string, Conn>(StringComparer.Ordinal);
        private readonly object connsLock = new object();
        private readonly Dictionary<string, long> unreachable = new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>拨号失败后的冷却：冷却期内不重复拨（避免刷屏），过期后允许重试。
        /// 用冷却代替「失败 3 次永久放弃」——MIUI 入站拦截是偶发的，过了冷却期再拨往往就通了。</summary>
        private const int UnreachableCooldownMs = 30000;
        private uint propagationId;

        /// <summary>是否启用 mDNS 发现（单元测试里互拨时关掉，避免抢 5353）。</summary>
        public bool EnableMdns = true;

        /// <summary>调试开关：一次性协议流发完不发 FIN（用于定位对端缓冲问题）。</summary>
        public static bool SkipFinForTest;

        public bool Running { get { return running; } }

        /// <summary>
        /// 省电：路由信息维护与静态节点重拨在后台时拉长周期。
        /// 这两个循环只要节点不掉线就行，灭屏后没必要 15/20 秒一次地唤醒射频。
        /// </summary>
        public void SetIdle(bool value)
        {
            idle = value;
            Log(L10n.T("省电模式：") + (value ? L10n.T("空闲（维护循环降频）") : L10n.T("活跃")));
        }
        public int Port { get { return port; } }

        /// <summary>邻居上线/下线通知（peerIdBase58, 名称, 是否在线）。</summary>
        public Action<string, string, bool> OnNeighbour;
        /// <summary>收到 qaul_messaging 容器（原始字节）。</summary>
        public Action<string, byte[]> OnMessaging;
        /// <summary>从路由信息里发现某个用户（用户 id，下一跳邻居）。</summary>
        public Action<string, string> OnUserRoute;
        /// <summary>收到 floodsub 里的 feed 消息（发送者 peerId, 文本）。</summary>
        public Action<string, string> OnFeed;
        public Action<string> OnLog;
        /// <summary>收到邻居发来的路由信息（用于界面显示「已互认」）。</summary>
        public Action<string> OnInfo;
        /// <summary>收到的路由信息条数（自检/界面用）。</summary>
        public int InfoRx;
        /// <summary>最近一条路由信息里声明的 node id（自检用：验证对端确实收到了完整字节）。</summary>
        public string LastInfoNode = "";
        /// <summary>最近一条路由信息的签名是否校验通过。</summary>
        public bool LastInfoSignatureOk;
        /// <summary>已发出的 USER_REQUEST 次数（诊断：官方节点不回公钥时看这个）。</summary>
        public int UserReqSent;
        /// <summary>收到的 USER_RESPONSE 条数（诊断用）。</summary>
        public int UserRespRx;
        /// <summary>最近一条 USER_RESPONSE 里解析出的用户数。</summary>
        public int LastUserRespCount;
        /// <summary>最近一条路由信息里对端宣称的 feed 条数（诊断 feed 同步用）。</summary>
        public int FeedAdvertised;
        /// <summary>通过 FEED_RESPONSE 收到的 feed 条数（诊断 feed 同步用）。</summary>
        public int FeedRx;
        /// <summary>对外回答过的 FEED_REQUEST 次数（诊断中继用）。</summary>
        public int FeedServed;
        /// <summary>中转过的别人消息次数（qaul_messaging 转发，诊断中继用）。</summary>
        public int Relayed;

        /// <summary>q8id(hex) → 完整 PeerId(base58)。官方路由表只给 q8id，靠它还原成用户。</summary>
        private readonly Dictionary<string, string> q8ToPeer = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>还没还原出 PeerId 的路由项：q8id(hex) → 下一跳邻居。</summary>
        private readonly Dictionary<string, string> routesByQ8 = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object q8Lock = new object();

        /* ---------- 公开信息（Feed）仓库 ---------- */

        /// <summary>已收到的 feed：message_id(hex) → 消息。qaul 用它来比对「还缺哪些」。</summary>
        private readonly Dictionary<string, FeedMessage> feedStore = new Dictionary<string, FeedMessage>(StringComparer.Ordinal);
        private readonly List<FeedMessage> feedOrder = new List<FeedMessage>();
        private readonly object feedLock = new object();
        /// <summary>已经向外索要过的 feed id（带冷却，避免每次路由信息都重复索求）。</summary>
        private readonly Dictionary<string, long> feedRequested = new Dictionary<string, long>(StringComparer.Ordinal);

        /* ---------- 节点 ≠ 用户 ---------- */

        /// <summary>
        /// 邻居节点 PeerId → 该节点上发现的**用户账号** id。
        ///
        /// libqaul 的用户账号是自己生成的一对 ed25519 密钥（`node/user_accounts.rs`
        /// 里 `create_account` 用 `Keypair::generate_ed25519()`），账号 id 与节点
        /// 的 libp2p PeerId **不是同一个东西**。早先把邻居的节点 PeerId 直接登记成
        /// 联系人，于是一台官方客户端会被「捕捉」两次：一次是它真正的账号（带昵称、
        /// 能收发），一次是它的节点身份（只有一串 id、收发都石沉大海）。
        /// 这张表负责把「节点」映射到「在该节点上的账号」。
        /// </summary>
        private readonly Dictionary<string, string> nodeToUser = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object nodeUserLock = new object();

        /// <summary>用户 → 已知的资料版本号（官方用 version 变化触发重新拉取资料）。</summary>
        private readonly Dictionary<string, uint> userVersions = new Dictionary<string, uint>(StringComparer.Ordinal);

        /// <summary>最近转发过的容器指纹及其时间（防止中继成环）。</summary>
        private readonly Dictionary<string, long> forwarded = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object forwardedLock = new object();

        /// <summary>本机的 8 字节 qaul id（PeerId 的第 7..14 字节）。</summary>
        public byte[] MyQ8
        {
            get
            {
                var q8 = new byte[8];
                Buffer.BlockCopy(identity.QaulPeerIdBytes, 6, q8, 0, 8);
                return q8;
            }
        }

        /// <summary>把 8 字节 q8id 还原成完整 PeerId（认识的才还原得出）。</summary>
        public string ResolveQ8ToPeer(byte[] q8id)
        {
            if (q8id == null || q8id.Length != 8) return null;
            var key = LocalIdentity.Hex(q8id);

            lock (q8Lock) { string p; if (q8ToPeer.TryGetValue(key, out p)) return p; }

            // 直连邻居一定能还原
            lock (connsLock)
            {
                foreach (var c in conns.Values)
                {
                    try
                    {
                        var pid = Base58.Decode(c.PeerIdB58);
                        if (pid != null && pid.Length == 38 && Curve25519.BytesEqual(Curve25519.Slice(pid, 6, 8), q8id))
                        {
                            lock (q8Lock) { q8ToPeer[key] = c.PeerIdB58; }
                            return c.PeerIdB58;
                        }
                    }
                    catch { }
                }
            }

            if (Messaging != null)
            {
                foreach (var u in Messaging.UserList())
                {
                    try
                    {
                        var pid = Base58.Decode(u.Id);
                        if (pid != null && pid.Length == 38 && Curve25519.BytesEqual(Curve25519.Slice(pid, 6, 8), q8id))
                        {
                            lock (q8Lock) { q8ToPeer[key] = u.Id; }
                            return u.Id;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 从 PeerId 直接取出对端的 ed25519 公钥，登记成用户。
        ///
        /// PeerId 的 38 字节里第 7..38 字节就是 ed25519 公钥，所以**只要能确认
        /// 「这个 id 真的用这把私钥签过名」**（qaul 的容器/ feed 都是带签名的），
        /// 公钥就不需要额外的 USER_RESPONSE —— 这才是官方客户端「自动就能聊」的原因。
        ///
        /// 但对**邻居节点的机身身份**不能这么干：libqaul 的用户账号是独立密钥，
        /// 节点 PeerId 通常并不是任何聊天账号。因此这类记录要打上 nodeIdentity
        /// 标记，界面上不当成联系人，只用于校验收到的签名。
        /// </summary>
        public void SeedKeyFromPeerId(string peerB58, bool nodeIdentity = true)
        {
            if (string.IsNullOrEmpty(peerB58) || Messaging == null) return;
            try
            {
                var pid = Base58.Decode(peerB58);
                if (pid == null || pid.Length != 38) return;
                if (pid[0] != 0x00 || pid[1] != 0x24) return;      // 只认 identity multihash

                var pub = Curve25519.Slice(pid, 6, 32);
                Messaging.UpsertUser(peerB58, null, pub, nodeIdentity);
                RememberPeer(peerB58);
                if (!Messaging.HaveKey(peerB58)) return;
                Log(L10n.T("从 PeerId 直接取得 ") + peerB58.Substring(0, 10) + L10n.T(" 的用户公钥（无需等待用户信息交换）"));
            }
            catch { }
        }

        /// <summary>
        /// 某个节点上「实际托管着」的用户账号：给他家的私聊目标。
        /// 没学到账号时返回 null（调用方退回节点 id）。
        /// </summary>
        public string UserOnNode(string nodeB58)
        {
            if (string.IsNullOrEmpty(nodeB58)) return null;
            lock (nodeUserLock)
            {
                string u;
                return nodeToUser.TryGetValue(nodeB58, out u) ? u : null;
            }
        }

        /// <summary>记下「这个用户是从哪个节点学来的」。</summary>
        public void NoteUserFrom(string nodeB58, string userB58)
        {
            if (string.IsNullOrEmpty(nodeB58) || string.IsNullOrEmpty(userB58)) return;
            if (nodeB58 == userB58) return;
            lock (nodeUserLock)
            {
                // 第一次学到的账号为准；后面若学到同节点的其它账号不覆盖，
                // 免得在多个官方客户端帐号之间反复跳。
                if (!nodeToUser.ContainsKey(nodeB58)) nodeToUser[nodeB58] = userB58;
            }
        }

        /// <summary>登记一个已知用户的 q8id 映射（学到 UserInfo 时调用）。</summary>
        public void RememberPeer(string peerB58)        {
            if (string.IsNullOrEmpty(peerB58)) return;
            try
            {
                var pid = Base58.Decode(peerB58);
                if (pid == null || pid.Length != 38) return;
                var q8 = Curve25519.Slice(pid, 6, 8);
                lock (q8Lock) { q8ToPeer[LocalIdentity.Hex(q8)] = peerB58; }
            }
            catch { }
        }

        /// <summary>记录一个邻居的最后已知端点（重启后自动重拨，不依赖不稳定的 mDNS 组播）。</summary>
        public void RememberEndpoint(string peerB58, IPEndPoint ep)
        {
            if (string.IsNullOrEmpty(peerB58) || ep == null) return;
            bool changed;
            lock (peerCacheLock)
            {
                IPEndPoint old;
                changed = !peerCache.TryGetValue(peerB58, out old) || !old.Equals(ep);
                if (changed)
                {
                    if (!peerCache.ContainsKey(peerB58))
                    {
                        // 满员时淘汰**最旧**的一条。原来是一把 Clear() 全清：
                        // 那会把天天在聊的那个对端的地址一起丢掉，重启后只能靠 mDNS 碰运气，
                        // 而 mDNS 恰恰是这台机器上最靠不住的一环（组播常被路由器隔离）。
                        while (peerCache.Count >= 8 && peerCacheOrder.Count > 0)
                        {
                            var oldest = peerCacheOrder[0];
                            peerCacheOrder.RemoveAt(0);
                            peerCache.Remove(oldest);
                        }
                        peerCacheOrder.Add(peerB58);
                    }
                    peerCache[peerB58] = ep;
                }
            }
            if (changed)
            {
                // 地址被刷新（被动入站学到 / mDNS 更新 / 静态节点）= 该端点的失败计数作废
                lock (dialFailLock) { dialFail.Remove(peerB58); }
                SavePeerCache();
            }
        }

        /// <summary>端点连续拨号失败计数（按 peer 记）：达到 <see cref="MaxDialFails"/> 就把
        /// 该端点从缓存里请出去，避免 StaticDialLoop 每 20s 砸一个早已换网段的旧地址
        /// （真机现场：D2 抱着 D1 上一网段的 192.168.1.101 反复 No route to host）。
        /// 端点被移除后，对端重新出现时由 mDNS / 被动入站 / 静态节点重新写回。</summary>
        private readonly Dictionary<string, int> dialFail = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object dialFailLock = new object();
        private const int MaxDialFails = 5;

        /// <summary>记一次对某 peer 的拨号失败；返回是否已到达退避上限。</summary>
        private bool NoteDialFail(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return false;
            lock (dialFailLock)
            {
                int n;
                dialFail.TryGetValue(peerB58, out n);
                n++;
                dialFail[peerB58] = n;
                return n >= MaxDialFails;
            }
        }

        /// <summary>把某个 peer 的端点缓存与失败计数一并清除（退避上限已到）。</summary>
        private void DropCachedEndpoint(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return;
            lock (peerCacheLock)
            {
                if (peerCache.Remove(peerB58)) peerCacheOrder.Remove(peerB58);
            }
            lock (dialFailLock) { dialFail.Remove(peerB58); }
            SavePeerCache();
        }

        private void LoadPeerCache()
        {
            try
            {
                if (string.IsNullOrEmpty(peerCachePath) || !File.Exists(peerCachePath)) return;
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(peerCachePath));
                if (dict == null) return;
                lock (peerCacheLock)
                {
                    foreach (var kv in dict)
                    {
                        var ep = ParseEndpoint(kv.Value);
                        if (ep == null) continue;
                        if (!peerCache.ContainsKey(kv.Key)) peerCacheOrder.Add(kv.Key);
                        peerCache[kv.Key] = ep;
                    }
                }
                Log(L10n.T("已加载 ") + peerCache.Count + L10n.T(" 条邻居端点缓存"));
            }
            catch { }
        }

        private void SavePeerCache()
        {
            try
            {
                if (string.IsNullOrEmpty(peerCachePath)) return;
                var dict = new Dictionary<string, string>();
                lock (peerCacheLock)
                {
                    foreach (var kv in peerCache) dict[kv.Key] = kv.Value.ToString();
                }
                File.WriteAllText(peerCachePath, JsonConvert.SerializeObject(dict));
            }
            catch { }
        }

        public QaulNode(LocalIdentity identity, string displayName, int port, string storeDir = null)
        {
            this.identity = identity;
            this.displayName = displayName ?? "Mchat";
            this.port = port > 0 ? port : DefaultPort;
            this.storeDir = storeDir;
        }

        private void Log(string msg)
        {
            Android.Util.Log.Info("QaulNode", msg);
            var cb = OnLog;
            if (cb != null) { try { cb(msg); } catch { } }
        }

        /* ===================== 生命周期 ===================== */

        public bool Start()
        {
            if (running) return true;
            if (identity == null) return false;

            // Noise 静态密钥与身份绑定但独立：换指纹后自动更换
            staticSeed = LocalIdentity.Sha256(Concat(identity.QaulSeed,
                Encoding.UTF8.GetBytes("Mchat/libp2p/noise-static/v1")));

            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Log(L10n.T("监听 ") + port + L10n.T(" 失败: ") + ex.Message);
                return false;
            }

            running = true;

            if (!string.IsNullOrEmpty(storeDir))
            {
                peerCachePath = Path.Combine(storeDir, "qaul_peers.json");
                LoadPeerCache();
            }

            // 端到端加密 + 消息服务
            try
            {
                Crypto = new QaulCrypto(identity, storeDir);
                Messaging = new QaulMessaging(identity, Crypto, this, storeDir);
                Messaging.OnLog = m => Log("msg: " + m);
            }
            catch (Exception ex) { Log(L10n.T("加密/消息服务初始化失败: ") + ex.Message); }
            LoadFeeds();

            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "qaul-accept" };
            acceptThread.Start();

            TryListenIpv6();

            infoThread = new Thread(InfoLoop) { IsBackground = true, Name = "qaul-info" };
            infoThread.Start();

            dialThread = new Thread(DialLoop) { IsBackground = true, Name = "qaul-dial" };
            dialThread.Start();

            staticDialThread = new Thread(StaticDialLoop) { IsBackground = true, Name = "qaul-static" };
            staticDialThread.Start();

            if (EnableMdns)
            {
                mdns = new MdnsService(identity.QaulPeerId, port);
                mdns.OnPeer = OnMdnsPeer;
                mdns.Start();
            }

            Log(L10n.T("libp2p 节点启动：peer=") + identity.QaulPeerId + " tcp=" + port);
            return true;
        }

        public void Stop()
        {
            running = false;
            try { if (mdns != null) mdns.Stop(); } catch { }
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (listener6 != null) listener6.Stop(); } catch { }

            List<Conn> all;
            lock (connsLock) { all = new List<Conn>(conns.Values); conns.Clear(); }
            foreach (var c in all) c.Close();
        }

        /* ===================== 入站 ===================== */

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }

                var t = new Thread(() => HandleInbound(client)) { IsBackground = true, Name = "qaul-conn-in" };
                t.Start();
            }
        }

        private void HandleInbound(TcpClient client)
        {
            Conn conn = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 60000;
                var raw = client.GetStream();

                // 1) multistream-select：读头部 → 回显 → 接受 /noise
                var proto = Ms.AcceptOne(raw, Ms.Noise);
                if (proto != Ms.Noise) { client.Close(); return; }

                // 2) Noise 握手（响应方）
                var noise = NoiseSession.Accept(raw, staticSeed, identity.QaulSeed, identity.QaulPublic);

                // 3) 复用器协商（注意：这一步已经在 Noise 加密通道里了）
                var ns = new NoiseStream(raw, noise);
                var muxProto = Ms.AcceptOne(ns, Ms.Yamux);
                if (muxProto != Ms.Yamux) { client.Close(); return; }

                var yamux = new YamuxSession(ns, false);
                var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                conn = new Conn
                {
                    PeerId = noise.RemotePeerId,
                    PeerIdB58 = noise.RemotePeerIdBase58,
                    Client = client,
                    Mux = yamux,
                    EndPoint = remoteEp,
                    Name = remoteEp == null ? null : remoteEp.ToString()
                };
                Attach(conn);
                // 被动方也必须学会对端地址：真机现场——D1 换网段后主动连 D2（D1 在
                // 192.168.179.x，D2 热点 192.168.179.209），D2 底层 TCP 明明看得到对端
                // 是 192.168.179.141，但老实现只把连接用起来、不记地址；D2 的端点缓存
                // 里还留着 D1 上一网段的 192.168.1.101，于是 D2 每轮重拨都砸在一个
                // 不存在的地址上（No route to host），而 D1→D2 方向其实是通的。
                // 不学地址 = 单向可达，重连/重启后 D2 再也找不到 D1。
                if (remoteEp != null)
                {
                    Log(L10n.T("入站连接学到对端地址 ") + conn.PeerIdB58 + " @" + remoteEp);
                    RememberEndpoint(conn.PeerIdB58, remoteEp);
                }
            }
            catch (Exception ex)
            {
                Log(L10n.T("入站连接失败: ") + ex.Message);
                try { if (conn != null) conn.Close(); else client.Close(); } catch { }
            }
        }

        /* ===================== 出站 ===================== */

        private void OnMdnsPeer(string peerIdB58, List<IPEndPoint> endpoints)
        {
            if (peerIdB58 == identity.QaulPeerId) return;
            lock (connsLock)
            {
                if (conns.ContainsKey(peerIdB58)) return;
            }
            // 记下邻居端点，重启/ mDNS 失效后也能重连
            if (!string.IsNullOrEmpty(peerIdB58) && endpoints != null && endpoints.Count > 0)
                RememberEndpoint(peerIdB58, endpoints[0]);
            // 退避：冷却期内不重复拨（避免刷屏），过期后再试。MIUI 入站拦截是偶发的，
            // 过了冷却期再拨往往就通，所以不永久放弃。
            lock (unreachable)
            {
                long last;
                if (unreachable.TryGetValue(peerIdB58, out last)
                    && Environment.TickCount - last < UnreachableCooldownMs) return;
            }

            var t = new Thread(() => DialOne(peerIdB58, endpoints)) { IsBackground = true, Name = "qaul-dial-one" };
            t.Start();
        }

        /// <summary>主动连接已发现的 qaul 节点（mDNS 里学到的地址）。</summary>
        public bool Dial(string peerIdB58, IPEndPoint ep)
        {
            return DialOne(peerIdB58, new List<IPEndPoint> { ep });
        }

        /// <summary>
        /// 连接一个「不知道 PeerId」的地址（握手时从身份签名里学习）。
        /// 用于调试/自测：直接按 IP:端口 连过去，不必先有 mDNS 记录。
        /// </summary>
        public bool DialUnknown(IPEndPoint ep)
        {
            return DialOne(null, new List<IPEndPoint> { ep });
        }

        private bool DialOne(string peerIdB58, List<IPEndPoint> endpoints)
        {
            foreach (var ep in endpoints)
            {
                Conn conn = null;
                try
                {
                    var client = new TcpClient();
                    var ar = client.BeginConnect(ep.Address, ep.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) { client.Close(); continue; }
                    client.EndConnect(ar);
                    client.NoDelay = true;
                    client.ReceiveTimeout = 60000;

                    var raw = client.GetStream();

                    // 作为发起方：multistream → /noise → /yamux
                    if (!Ms.SelectOne(raw, Ms.Noise)) { client.Close(); continue; }

                    var expected = string.IsNullOrEmpty(peerIdB58) ? null : Base58.Decode(peerIdB58);
                    var noise = NoiseSession.Dial(raw, staticSeed, identity.QaulSeed, identity.QaulPublic, expected);

                    var ns = new NoiseStream(raw, noise);
                    if (!Ms.SelectOne(ns, Ms.Yamux)) { client.Close(); continue; }

                    var yamux = new YamuxSession(ns, true);
                    conn = new Conn
                    {
                        PeerId = noise.RemotePeerId,
                        PeerIdB58 = noise.RemotePeerIdBase58,
                        Client = client,
                        Mux = yamux,
                        EndPoint = ep
                    };
                    Attach(conn);
                    Log(L10n.T("已连接 qaul 节点 ") + conn.PeerIdB58 + " @" + ep);
                    if (!string.IsNullOrEmpty(peerIdB58))
                    {
                        lock (unreachable) { unreachable.Remove(peerIdB58); }
                    }
                    // 记下来，重启/ mDNS 失效后自动重拨
                    RememberEndpoint(conn.PeerIdB58, ep);
                    return true;
                }
                catch (Exception ex)
                {
                    try { if (conn != null) conn.Close(); } catch { }
                    Log(L10n.T("连接 ") + ep + L10n.T(" 失败: ") + ex.Message);
                }
            }

            if (!string.IsNullOrEmpty(peerIdB58))
            {
                lock (unreachable) { unreachable[peerIdB58] = Environment.TickCount; }
            }
            return false;
        }

        /// <summary>
        /// 用 QUIC（UDP + TLS 1.3 + libp2p 证书）直连一个 qaul 节点。
        ///
        /// 与 DialOne 的区别：QUIC 自带加密与多路复用，**没有** Noise、没有 yamux，
        /// 也不做连接级 multistream（TLS 的 ALPN 已经协商了 "libp2p"）；
        /// 每条流内部仍然照旧用 multistream-select 选 /qaul_info 等协议。
        ///
        /// 这就是「经 qaul 中继传输、不被中继/ISP 截取」的关键路径：
        /// 明文只存在于 Noise/QUIC 之内，ISP 看到的是加密的 UDP 流量。
        /// </summary>
        public bool DialQuic(string peerIdB58, IPEndPoint ep)
        {
            // 同一个地址只允许一条"正在拨号"的路径。
            // 没有这个闸门时，启动自动拨号 + 定时重拨 + 手动加节点会同时建出好几条
            // 到同一节点的 QUIC 连接；它们的 PeerId 相同，对端（libp2p）会当成重复连接
            // 把**旧的那条踢掉**，于是刚建好的订阅流瞬间死掉 —— 现象就是
            // 「订阅 floodsub 失败: quic 流读超时」，而且时好时坏。
            var dialKey = "quic://" + ep;
            lock (dialing)
            {
                if (dialing.Contains(dialKey)) return false;
                if (IsConnectedTo(ep)) return true;
                dialing.Add(dialKey);
            }

            QuicConnection qc = null;
            try
            {
                var myCert = Der.Generate(identity.QaulSeed);
                qc = QuicConnection.Connect(ep, myCert, 8000, Log);
                var mux = new QuicMux(qc);
                var conn = new Conn
                {
                    PeerId = Base58.Decode(qc.PeerIdBase58),
                    PeerIdB58 = qc.PeerIdBase58,
                    Client = null,
                    Transport = mux,
                    Mux = mux,
                    EndPoint = ep,
                    Name = ep.ToString()
                };
                if (!string.IsNullOrEmpty(peerIdB58) && peerIdB58 != conn.PeerIdB58)
                {
                    // QUIC 的 TLS 证书已经证明了节点身份，这里只做一致性提示
                    Log(L10n.T("⚠ QUIC 对端 PeerId 与预期不一致：期望 ") + peerIdB58 + L10n.T("，实际 ") + conn.PeerIdB58);
                }
                // 「握手落定」的等待已经做在 QuicConnection.OpenStream 里
                // （HandshakeSettleMs，首流前让服务端装好 1-RTT 读密钥），
                // 这里不必再额外 sleep，否则每次建连都白等。
                Attach(conn);
                Log(L10n.T("已通过 QUIC 连接 qaul 节点 ") + conn.PeerIdB58 + " @" + ep);
                if (!string.IsNullOrEmpty(peerIdB58))
                    lock (unreachable) { unreachable.Remove(peerIdB58); }
                // 与 DialOne 对称：QUIC 直连成功同样要记住端点，
                // 否则 mDNS 失效/重启后没有重拨依据。
                RememberEndpoint(conn.PeerIdB58, ep);
                return true;
            }
            catch (Exception ex)
            {
                try { if (qc != null) qc.Close(); } catch { }
                Log(L10n.T("QUIC 连接 ") + ep + L10n.T(" 失败: ") + ex.Message);
                return false;
            }
            finally
            {
                lock (dialing) { dialing.Remove(dialKey); }
            }
        }

        private void DialLoop()
        {
            // mDNS 已经在发现阶段主动拨号，这里只做兜底：清理失效连接
            while (running)
            {
                Thread.Sleep(5000);
                try
                {
                    List<Conn> dead = new List<Conn>();
                    lock (connsLock)
                    {
                        foreach (var c in conns.Values) if (c.Mux.Closed) dead.Add(c);
                    }
                    foreach (var c in dead) Remove(c, L10n.T("连接已关闭"));
                }
                catch { }
            }
        }

        /* ===================== IPv6 / 静态节点 ===================== */

        /// <summary>
        /// 尝试在 IPv6 上监听；不支持就永久关掉（只记一次日志），
        /// 避免每个网络事件都抛一遍异常。
        /// </summary>
        private void TryListenIpv6()
        {
            if (!Socket.OSSupportsIPv6)
            {
                Ipv6Available = false;
                Log(L10n.T("本机不支持 IPv6，已自动禁用 IPv6 监听"));
                return;
            }
            try
            {
                listener6 = new TcpListener(IPAddress.IPv6Any, port);
                listener6.Server.DualMode = false;      // 与 IPv4 监听共存
                listener6.Start();
                Ipv6Available = true;
                Log(L10n.T("IPv6 监听已开启 [::]:") + port);

                var t = new Thread(() =>
                {
                    while (running)
                    {
                        try
                        {
                            var c = listener6.AcceptTcpClient();
                            var th = new Thread(() => HandleInbound(c)) { IsBackground = true, Name = "qaul-conn-v6" };
                            th.Start();
                        }
                        catch { break; }
                    }
                }) { IsBackground = true, Name = "qaul-accept6" };
                t.Start();
            }
            catch (Exception ex)
            {
                Ipv6Available = false;
                try { listener6.Stop(); } catch { }
                listener6 = null;
                Log(L10n.T("IPv6 监听不可用，已自动禁用: ") + ex.Message);
            }
        }

        /// <summary>
        /// 添加一个静态节点地址。支持三种写法：
        ///   · "1.2.3.4:9200"            —— TCP（Noise + yamux）
        ///   · "[fd00::1]:9200"          —— TCP，IPv6
        ///   · "quic://1.2.3.4:9229"     —— QUIC（TLS 1.3），端口 9229 也会自动识别
        /// </summary>
        public bool AddStaticPeer(string address)
        {
            bool quic = false;
            var raw = (address ?? "").Trim();
            if (raw.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
            {
                quic = true;
                raw = raw.Substring(7);
            }
            if (raw.EndsWith("/quic-v1", StringComparison.OrdinalIgnoreCase))
            {
                quic = true;
                raw = raw.Substring(0, raw.Length - 8);
            }
            var ep = ParseEndpoint(raw);
            if (ep == null) return false;
            // 9229 是 qaul 官方 QUIC 端口；只给 IP:9229 时按 QUIC 处理，省得用户记协议
            if (ep.Port == DefaultQuicPort) quic = true;

            var bucket = quic ? staticPeersQuic : staticPeers;
            lock (staticPeers)
            {
                foreach (var p in bucket) if (p.Equals(ep)) return true;
                bucket.Add(ep);
            }
            Log(L10n.T("加入静态节点 ") + ep + (quic ? "（QUIC）" : "（TCP）"));
            var t = new Thread(() =>
            {
                if (quic) DialQuic(null, ep);
                else DialUnknown(ep);
            }) { IsBackground = true };
            t.Start();
            return true;
        }

        /// <summary>
        /// 主动索取某个用户的公钥（KK 加密的前置条件）。
        /// 用于「刚搜到节点就要给它发消息」的场景：不等 15 秒的路由信息周期。
        /// </summary>
        public bool RequestUser(string peerB58)
        {
            if (!running || string.IsNullOrEmpty(peerB58)) return false;

            byte[] pid;
            try { pid = Base58.Decode(peerB58); } catch { return false; }
            if (pid == null || pid.Length != 38) return false;

            // 直连邻居优先，否则走路由表的下一跳
            Conn target = null;
            lock (connsLock) { conns.TryGetValue(peerB58, out target); }
            if (target == null || target.Mux.Closed)
            {
                string via = null;
                lock (routes) { routes.TryGetValue(peerB58, out via); }
                if (via == null)
                {
                    try
                    {
                        if (pid.Length == 38)
                        {
                            var q8hex = LocalIdentity.Hex(Curve25519.Slice(pid, 6, 8));
                            lock (routesByQ8) { routesByQ8.TryGetValue(q8hex, out via); }
                        }
                    }
                    catch { }
                }
                if (via != null) lock (connsLock) { conns.TryGetValue(via, out target); }
            }
            // 还不行：随便找一个邻居代问（我们问的只是「认识吗」，对方答自己认识的）
            if (target == null || target.Mux.Closed)
            {
                lock (connsLock)
                    foreach (var kv in conns)
                        if (!kv.Value.Mux.Closed) { target = kv.Value; break; }
            }
            if (target == null) return false;

            var q8 = Curve25519.Slice(pid, 6, 8);
            var list = new List<byte[]> { q8 };
            var t = new Thread(() => SendUserRequest(target, list)) { IsBackground = true };
            t.Start();
            return true;
        }

        public List<string> StaticPeers()
        {
            var list = new List<string>();
            lock (staticPeers)
            {
                foreach (var p in staticPeers) list.Add(p.ToString());
                foreach (var p in staticPeersQuic) list.Add("quic://" + p);
            }
            return list;
        }

        /// <summary>把别人的用户寻址请求继续往下传（中转节点：自己答不上就把问题接力出去）。</summary>
        private void RelayUserRequest(string viaB58, List<byte[]> ids)
        {
            if (string.IsNullOrEmpty(viaB58) || ids == null || ids.Count == 0) return;
            Conn hop = null;
            lock (connsLock) { conns.TryGetValue(viaB58, out hop); }
            if (hop == null || hop.Mux.Closed) return;
            SendUserRequest(hop, ids);
        }

        private static string ShortId(string b58)
        {
            if (string.IsNullOrEmpty(b58)) return "";
            return b58.Length <= 10 ? b58 : b58.Substring(0, 10) + "…";
        }

        private static IPEndPoint ParseEndpoint(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            address = address.Trim();
            try
            {
                // [ipv6]:port
                if (address.StartsWith("["))
                {
                    int close = address.IndexOf(']');
                    if (close < 0) return null;
                    var ip = address.Substring(1, close - 1);
                    int p2 = address.IndexOf(':', close);
                    int port = p2 > 0 ? int.Parse(address.Substring(p2 + 1)) : DefaultPort;
                    return new IPEndPoint(IPAddress.Parse(ip), port);
                }
                int colon = address.LastIndexOf(':');
                if (colon > 0 && address.IndexOf(':') == colon)
                {
                    var ip = address.Substring(0, colon);
                    int port = int.Parse(address.Substring(colon + 1));
                    return new IPEndPoint(IPAddress.Parse(ip), port);
                }
                // 纯 IPv6 字面量（无端口）
                IPAddress parsed;
                if (IPAddress.TryParse(address, out parsed)) return new IPEndPoint(parsed, DefaultPort);
            }
            catch { }
            return null;
        }

        /// <summary>静态节点：断了就重拨（每 20 秒一次）。</summary>
        private void StaticDialLoop()
        {
            while (running)
            {
                Thread.Sleep(idle ? 60000 : 20000);
                List<IPEndPoint> targets, quicTargets;
                lock (staticPeers)
                {
                    targets = new List<IPEndPoint>(staticPeers);
                    quicTargets = new List<IPEndPoint>(staticPeersQuic);
                }
                foreach (var ep in targets)
                {
                    if (ep.AddressFamily == AddressFamily.InterNetworkV6 && !Ipv6Available) continue;
                    if (IsConnectedTo(ep)) continue;
                    DialUnknown(ep);
                }
                foreach (var ep in quicTargets)
                {
                    if (ep.AddressFamily == AddressFamily.InterNetworkV6 && !Ipv6Available) continue;
                    if (IsConnectedTo(ep)) continue;
                    DialQuic(null, ep);
                }
                // 邻居端点缓存：重启后或 mDNS 失效时也能重连（不依赖不稳定的组播）。
                // 用 DialUnknown（peerId 留空，从握手学），避免缓存里陈旧的 peerId 触发 Noise 校验拒绝。
                Dictionary<string, IPEndPoint> cached;
                lock (peerCacheLock) { cached = new Dictionary<string, IPEndPoint>(peerCache); }
                foreach (var kv in cached)
                {
                    if (kv.Value.AddressFamily == AddressFamily.InterNetworkV6 && !Ipv6Available) continue;
                    if (IsConnectedTo(kv.Value)) continue;
                    var ok = DialUnknown(kv.Value);
                    // 连续拨不通：这个端点大概率是对端换了网段的旧地址。
                    // 主动移出缓存，别再每 20s 砸一次；对端回来时 mDNS / 被动入站会重新写回。
                    if (!ok && NoteDialFail(kv.Key))
                    {
                        Log(L10n.T("端点连续拨不通，暂停重拨 ") + kv.Key + " @" + kv.Value + L10n.T("（等 mDNS/入站重新发现）"));
                        DropCachedEndpoint(kv.Key);
                    }
                }
            }
        }

        /* ===================== 连接管理 ===================== */

        /// <summary>是否已经有到该地址的连接（QUIC 连接的 Client 为 null，必须判空）。</summary>
        private bool IsConnectedTo(IPEndPoint ep)
        {
            lock (connsLock)
            {
                foreach (var c in conns.Values)
                {
                    var tcp = c.Client;
                    var rep = (tcp == null || tcp.Client == null) ? c.EndPoint : tcp.Client.RemoteEndPoint as IPEndPoint;
                    if (rep == null) continue;
                    if (rep.Address.Equals(ep.Address) && rep.Port == ep.Port) return true;
                }
            }
            return false;
        }

        private sealed class Conn
        {
            public byte[] PeerId;
            public string PeerIdB58;
            public TcpClient Client;
            public IStreamMux Mux;
            /// <summary>非 TCP 传输（QUIC）的释放句柄；走 TCP 时为 null。</summary>
            public System.IDisposable Transport;
            /// <summary>对端地址（QUIC 连接没有 TcpClient，判断"是否已连"要靠它）。</summary>
            public IPEndPoint EndPoint;
            public string Name;

            /// <summary>与这个邻居之间那条常驻的 floodsub 订阅流（官方客户端同一条连接只用一条）。</summary>
            public Stream Flood;
            public readonly object FloodGate = new object();

            public void Close()
            {
                try { Mux.Close(); } catch { }
                try { if (Client != null) Client.Close(); } catch { }
                try { if (Transport != null) Transport.Dispose(); } catch { }
            }
        }

        private void Attach(Conn conn)
        {
            lock (connsLock)
            {
                Conn old;
                if (conns.TryGetValue(conn.PeerIdB58, out old))
                {
                    // 双向同时拨号：保留已有连接
                    conn.Close();
                    return;
                }
                conns[conn.PeerIdB58] = conn;
            }

            conn.Mux.OnIncomingStream = st => HandleStream(conn, st);

            Log(L10n.T("邻居上线: ") + conn.PeerIdB58);
            Notify(conn.PeerIdB58, conn.Name, true);

            // 注意：这里**不能**再把邻居的节点 PeerId 登记成聊天联系人。
            // libqaul 的用户账号是独立密钥（node/user_accounts.rs），节点 PeerId
            // 通常不是任何账号；登记它就会在联系人里多出一条「同名led peerid」的记录，
            // 而且给它发消息永远收不到（没人持有这把私钥对应的账号）。
            // 只登记 q8id → PeerId 映射，让路由与众包分发能认出它。
            RememberPeer(conn.PeerIdB58);

            // 新连接建立：丢弃与该邻居的旧 KK 会话，强制重新协商（见 QaulCrypto.ResetSession）。
            // 否则复用随旧连接失效的密钥/计数，会导致重连后私聊永远解不开。
            try { Crypto.ResetSession(conn.PeerIdB58); } catch { }

            // 建连后要做三件事：订阅 floodsub、推自己的路由信息、向对端索要用户资料。
            //
            // 这三件事**必须串行**做。以前是三个线程同时开流，在 QUIC 上会互相干扰
            // （并发开流抢流号、首流撞上服务端的握手收尾），表现就是
            // 「订阅 floodsub 失败: quic 流读超时」——而且只在真机上偶发，
            // PC 探针单线程永远复现不出来。
            //
            // 串行化只多花一两百毫秒，换来的是每次建连都稳定可用。
            var boot = new Thread(() =>
            {
                // ① floodsub：对端刚握完手可能还没准备好接受新流，失败就退避重试
                for (int attempt = 1; attempt <= 3 && running && !conn.Mux.Closed; attempt++)
                {
                    if (EnsureFloodsub(conn)) break;
                    if (attempt < 3)
                    {
                        Log(L10n.T("订阅 floodsub 第 ") + attempt + L10n.T(" 次失败，稍后重试"));
                        Thread.Sleep(300 * attempt);
                    }
                }

                // ② 推一次路由信息，qaul 收到后才会把我们写进它的路由表
                try { SendInfo(conn); }
                catch (Exception ex) { Log(L10n.T("推送路由信息失败: ") + ex.Message); }

                // ③ 直接向这个邻居索要用户资源。
                //    不能指望它的路由表里列自己——官方 qaul 的路由表是按需组装的，
                //    不显式要，就会一直卡在「等公钥」。
                try
                {
                    var q8 = Curve25519.Slice(Base58.Decode(conn.PeerIdB58), 6, 8);
                    SendUserRequest(conn, new List<byte[]> { q8 });
                }
                catch (Exception ex) { Log(L10n.T("索要用户资料失败: ") + ex.Message); }
            }) { IsBackground = true, Name = "qaul-boot" };
            boot.Start();
        }

        private void Remove(Conn conn, string reason)
        {
            bool removed = false;
            lock (connsLock) { removed = conns.Remove(conn.PeerIdB58); }
            conn.Close();
            if (removed)
            {
                Log(L10n.T("邻居下线: ") + conn.PeerIdB58 + "（" + reason + "）");
                Notify(conn.PeerIdB58, conn.Name, false);
            }
        }

        private void Notify(string peerId, string name, bool online)
        {
            var cb = OnNeighbour;
            if (cb != null) { try { cb(peerId, name, online); } catch { } }
        }

        /* ===================== 业务协议 ===================== */

        private void HandleStream(Conn conn, Stream stream)
        {
            string proto;
            try
            {
                proto = Ms.AcceptOne(stream, Ms.QaulInfo, Ms.QaulMessaging, Ms.Floodsub, Ms.Ping, Ms.Identify);
            }
            catch { try { stream.Close(); } catch { } return; }

            if (proto == null) { try { stream.Close(); } catch { } return; }

            try
            {
                if (proto == Ms.QaulInfo) HandleInfoStream(conn, stream);
                else if (proto == Ms.QaulMessaging) HandleMessagingStream(conn, stream);
                else if (proto == Ms.Floodsub) HandleFloodsubStream(conn, stream);
                else if (proto == Ms.Ping) HandlePingStream(stream);
                else if (proto == Ms.Identify) HandleIdentifyStream(conn, stream);
            }
            catch (Exception ex)
            {
                Log(L10n.T("处理 ") + proto + L10n.T(" 流失败: ") + ex.Message);
                try { stream.Close(); } catch { }
            }
        }

        private void HandlePingStream(Stream stream)
        {
            var buf = new byte[32];
            if (IoUtil.ReadExact(stream, buf, 0, 32)) stream.Write(buf, 0, 32);
            stream.Close();
        }

        private void HandleIdentifyStream(Conn conn, Stream stream)
        {
            var w = new PbWriter();
            w.Str(1, "ipfs/0.1.0");
            w.Str(2, "Mchat/qaul-libp2p/1.0");
            w.Bytes(3, IdentityProto(identity.QaulPublic));
            w.Str(4, "/ip4/0.0.0.0/tcp/" + port);
            w.Str(5, Ms.QaulInfo);
            w.Str(5, Ms.QaulMessaging);
            w.Str(5, Ms.Floodsub);
            w.Str(5, Ms.Ping);
            IoUtil.WriteVarintPrefixed(stream, w.ToArray());
            stream.Close();
        }

        private static byte[] IdentityProto(byte[] edPub)
        {
            // PublicKey { Type = Ed25519(1), Data = 32B }
            var w = new PbWriter();
            w.Int(1, 1);
            w.Bytes(2, edPub);
            return w.ToArray();
        }

        private void HandleInfoStream(Conn conn, Stream stream)
        {
            var data = IoUtil.ReadVarintPrefixed(stream, 512 * 1024);
            try { stream.Close(); } catch { }
            if (data == null || data.Length == 0) return;
            HandleInfoPayload(conn, data);
        }

        private void HandleMessagingStream(Conn conn, Stream stream)
        {
            var data = IoUtil.ReadVarintPrefixed(stream, 512 * 1024);
            try { stream.Close(); } catch { }
            if (data == null || data.Length == 0) return;

            var cb = OnMessaging;
            if (cb != null) { try { cb(conn.PeerIdB58, data); } catch { } }

            if (Messaging != null)
            {
                try { Messaging.HandleContainer(conn.PeerIdB58, data); }
                catch (Exception ex) { Log(L10n.T("处理 qaul 消息失败: ") + ex.Message); }
            }
        }

        private void HandleFloodsubStream(Conn conn, Stream stream)
        {
            // 长连接：反复读 varint 分隔的 RPC
            while (running && !conn.Mux.Closed)
            {
                var data = IoUtil.ReadVarintPrefixed(stream, 1024 * 1024);
                if (data == null) break;
                HandleFloodsubRpc(conn, data);
            }
            try { stream.Close(); } catch { }
        }

        /// <summary>
        /// 主动开一条 floodsub 流并订阅 topic。
        ///
        /// 这一步是「为什么官方端的公开信息收不到」的关键：libp2p 的 floodsub
        /// 只把消息发给**订阅了该 topic 的对端**，而 qaul 官方 app 正是靠收到我们的
        /// SubOpts 才把我们加进 topic 列表。之前我们只在「发布 feed」时顺带带一次订阅，
        /// 于是：我们发得出去（我们主动发流），官方发不过来（他们不知道我们订阅了）。
        /// </summary>
        /// <summary>确保 floodsub 订阅流存在。返回 true 表示已就绪（含本来就已订阅）。</summary>
        private bool EnsureFloodsub(Conn conn)
        {
            lock (conn.FloodGate)
            {
                if (conn.Flood != null) return true;
                if (conn.Mux.Closed) return false;
                try
                {
                    var st = conn.Mux.OpenStream();
                    if (!Ms.SelectOne(st, Ms.Floodsub)) { QStreams.Reset(st); return false; }
                    conn.Flood = st;

                    var rpc = new PbWriter();
                    rpc.Sub(1, SubscribeOpts());
                    IoUtil.WriteVarintPrefixed(st, rpc.ToArray());

                    var t = new Thread(() => ReadFloodsub(conn, st)) { IsBackground = true, Name = "qaul-flood-rx" };
                    t.Start();
                    Log(L10n.T("已向 ") + conn.PeerIdB58.Substring(0, Math.Min(10, conn.PeerIdB58.Length)) + L10n.T(" 订阅 ") + FeedTopic);
                    return true;
                }
                catch (Exception ex)
                {
                    Log(L10n.T("订阅 floodsub 失败: ") + ex.Message);
                    return false;
                }
            }
        }

        private void ReadFloodsub(Conn conn, Stream st)
        {
            try
            {
                while (running && !conn.Mux.Closed)
                {
                    byte[] data;
                    try
                    {
                        data = IoUtil.ReadVarintPrefixed(st, 1024 * 1024);
                    }
                    catch (System.IO.IOException)
                    {
                        // **读超时 ≠ 流断了**。QUIC 流的读超时只是「这段时间没新消息」
                        // （adapter 里 wait 到期抛 IOException），以前这里一抛就退出循环、
                        // 把 conn.Flood 置空，于是十几秒后又要重新订阅一次：
                        // 既丢消息，又不停开新流。间隔期间继续等就好。
                        if (conn.Mux.Closed || !running) break;
                        continue;
                    }
                    if (data == null) break;          // 对端 FIN，这条订阅流真的没了
                    HandleFloodsubRpc(conn, data);
                }
            }
            catch { }
            finally
            {
                lock (conn.FloodGate) { if (ReferenceEquals(conn.Flood, st)) conn.Flood = null; }
            }
        }

        /// <summary>把一条 floodsub 消息发到某个邻居（复用常驻订阅流，失败再开临时流）。</summary>
        private bool WriteFloodsub(Conn conn, byte[] rpc)
        {
            try
            {
                lock (conn.FloodGate)
                {
                    if (conn.Flood != null)
                    {
                        IoUtil.WriteVarintPrefixed(conn.Flood, rpc);
                        return true;
                    }
                }
            }
            catch
            {
                lock (conn.FloodGate) { conn.Flood = null; }
            }

            try
            {
                var st = conn.Mux.OpenStream();
                if (!Ms.SelectOne(st, Ms.Floodsub)) { QStreams.Reset(st); return false; }
                IoUtil.WriteVarintPrefixed(st, rpc);
                Thread.Sleep(50);
                QStreams.CloseWrite(st);
                ForgetLater(conn, QStreams.Id(st));
                return true;
            }
            catch (Exception ex)
            {
                Log(L10n.T("发送 feed 失败: ") + ex.Message);
                return false;
            }
        }

        private static void ForgetLater(Conn conn, uint streamId)
        {
            ForgetScheduler.Schedule(conn.Mux, streamId, 3000);
        }

        /// <summary>
        /// 流表项延迟清理调度器（省电/性能修复）。
        ///
        /// 原实现：每发一条流就 `new Thread(() => { Thread.Sleep(3000); mux.Forget(id); })`。
        /// 消息一多（info 15 秒一轮 + 路由转发 + 群聊 N 份单播）线程数会瞬间涨到几十个，
        /// 每条线程还要白占 3 秒的栈内存与调度开销——纯浪费。
        /// 这里改成**一个**常驻后台线程 + 到期队列：功能完全一样（延迟 3 秒只删本地流表、不发帧），
        /// 但线程数从 O(消息数) 降到 O(1)，空闲时该线程会自行退出。
        /// </summary>
        private static class ForgetScheduler
        {
            private sealed class Item
            {
                public long At;
                public IStreamMux Mux;
                public uint Id;
            }

            private static readonly List<Item> items = new List<Item>();
            private static readonly object gate = new object();
            private static Thread worker;

            public static void Schedule(IStreamMux mux, uint id, int delayMs)
            {
                if (mux == null) return;
                var it = new Item
                {
                    At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + delayMs,
                    Mux = mux,
                    Id = id
                };
                lock (gate)
                {
                    items.Add(it);
                    if (worker != null && worker.IsAlive) { Monitor.Pulse(gate); return; }
                    worker = new Thread(Loop) { IsBackground = true, Name = "qaul-forget" };
                    worker.Start();
                }
            }

            private static void Loop()
            {
                var due = new List<Item>();
                while (true)
                {
                    // 1) 取出到期项（持锁只做摘链）
                    due.Clear();
                    long soonest;
                    lock (gate)
                    {
                        if (items.Count == 0)
                        {
                            Monitor.Wait(gate, 5000);
                            if (items.Count == 0) { worker = null; return; }
                        }

                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        soonest = long.MaxValue;
                        for (int i = items.Count - 1; i >= 0; i--)
                        {
                            var it = items[i];
                            if (it.At <= now) { due.Add(it); items.RemoveAt(i); }
                            else if (it.At < soonest) soonest = it.At;
                        }
                    }

                    // 2) 真正的 Forget 放到锁外做，避免与 Yamux 内部锁互相等待
                    foreach (var it in due)
                    {
                        try { it.Mux.Forget(it.Id); } catch { }
                    }

                    // 3) 睡到下一个到期点（或新项入队被 Pulse 唤醒）
                    lock (gate)
                    {
                        var waitMs = soonest == long.MaxValue
                            ? 1000
                            : (int)Math.Max(50, Math.Min(3000,
                                soonest - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                        Monitor.Wait(gate, waitMs);
                    }
                }
            }
        }

        private void HandleFloodsubRpc(Conn conn, byte[] data)
        {
            var r = new PbReader(data);
            int field, wire;
            while (r.Next(out field, out wire))
            {
                if (field == 1)
                {
                    // SubOpts { subscribe = 1, topicid = 2 }
                    var sub = r.Sub();
                    bool subscribe = true;
                    string topic = null;
                    int f2, w2;
                    while (sub.Next(out f2, out w2))
                    {
                        if (f2 == 1) subscribe = sub.Varint() != 0;
                        else if (f2 == 2) topic = sub.Str();
                        else sub.Skip(w2);
                    }
                    if (topic != null) Log("邻居 " + conn.PeerIdB58.Substring(0, Math.Min(8, conn.PeerIdB58.Length)) + (subscribe ? " 订阅 " : " 退订 ") + topic);
                }
                else                 if (field == 2)
                {
                    // Message { from = 1, data = 2, seqno = 3, topic = 4, signature = 5, key = 6 }
                    var msg = r.Sub();
                    byte[] from = null, payload = null;
                    string topic = null;
                    int f3, w3;
                    while (msg.Next(out f3, out w3))
                    {
                        if (f3 == 1) from = msg.Bytes();
                        else if (f3 == 2) payload = msg.Bytes();
                        else if (f3 == 4) topic = msg.Str();
                        else msg.Skip(w3);
                    }
                    if (payload != null && topic == FeedTopic) HandleFeedPayload(conn, from, payload);
                }
                else r.Skip(wire);
            }
        }

        /// <summary>
        /// 处理一条 floodsub 上的 feed：验签 → 入库 → 通知界面 → 中继给其它邻居。
        ///
        /// qaul 的 feed 是**全网洪泛**的公开信息：任何节点收到新消息都要继续转给
        /// 其余邻居（libqaul 里是 `Flooder::add(...)` 后由传输层再 publish 一遍），
        /// 少了这一步，本节点就只是个孤岛终端，撑不起「中转节点」的职责。
        /// </summary>
        private void HandleFeedPayload(Conn conn, byte[] from, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;

            // FeedContainer { signature = 1, message = 2 }
            byte[] sig = null, message = null;
            int field, wire;
            var r = new PbReader(payload);
            while (r.Next(out field, out wire))
            {
                if (field == 1) sig = r.Bytes();
                else if (field == 2) message = r.Bytes();
                else r.Skip(wire);
            }
            if (message == null) return;

            // FeedMessageContent { sender = 1, content = 2, time = 3 }
            var m = new PbReader(message);
            string content = null;
            byte[] senderBytes = null;
            long time = 0;
            int f2, w2;
            while (m.Next(out f2, out w2))
            {
                if (f2 == 1) senderBytes = m.Bytes();
                else if (f2 == 2) content = m.Str();
                else if (f2 == 3) time = (long)m.Varint();
                else m.Skip(w2);
            }
            if (content == null) return;

            // 验签：feed 的 sender 是 38 字节 PeerId，公钥就内嵌在里面。
            // 验过即证明「这个 id 就是这把公钥的主人」 —— 顺手把公钥登记了，
            // 这正是不需要额外交互的自动公钥学习路径。
            string senderB58 = null;
            if (senderBytes != null && senderBytes.Length == 38 && sig != null && sig.Length == 64)
            {
                var pub = Curve25519.Slice(senderBytes, 6, 32);
                if (!Curve25519.Ed25519Verify(pub, message, sig))
                {
                    Log(L10n.T("丢弃一条验签失败的 feed（疑似伪造）"));
                    return;
                }
                senderB58 = Base58.Encode(senderBytes);
                SeedKeyFromPeerId(senderB58, false);
            }
            else if (senderBytes != null && senderBytes.Length > 0)
            {
                senderB58 = Base58.Encode(senderBytes);
            }

            if (time == 0) time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 按「签名 = message_id」入库；重复的消息不再处理也不再中继（防洪泛成环）
            if (!AddFeedMessage(sig, senderBytes, content, time)) return;

            var cb = OnFeed;
            if (cb != null) { try { cb(senderB58, content); } catch { } }

            // 中继：转发给除来路以外的其它邻居
            RelayFeedExcept(conn, payload);
        }

        /// <summary>把一条 feed 继续洪泛给其它邻居（中转节点的核心职责）。</summary>
        private void RelayFeedExcept(Conn except, byte[] container)
        {
            List<Conn> all;
            lock (connsLock) { all = new List<Conn>(conns.Values); }

            foreach (var c in all)
            {
                if (c.Mux.Closed) continue;
                if (except != null && ReferenceEquals(c, except)) continue;
                try { WriteFloodsub(c, BuildFloodsubRpc(container)); }
                catch { }
            }
        }

        /* ===================== 公开信息（Feed）仓库 ===================== */

        /// <summary>登记一条 feed；新的才算数（重复的用于去重，避免反复入库与洪泛）。</summary>
        public bool AddFeedMessage(byte[] id, byte[] sender, string content, long time)
        {
            if (id == null || id.Length == 0) return false;
            var key = LocalIdentity.Hex(id);

            lock (feedLock)
            {
                if (feedStore.ContainsKey(key)) return false;
                var fm = new FeedMessage
                {
                    IdHex = key,
                    Sender = sender,
                    Content = content ?? "",
                    Time = time > 0 ? time : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                feedStore[key] = fm;
                feedOrder.Add(fm);
                // 只留最近 300 条：feed 是全网洪泛的公共信息，历史不必全留
                while (feedOrder.Count > 300)
                {
                    feedStore.Remove(feedOrder[0].IdHex);
                    feedOrder.RemoveAt(0);
                }
            }
            SaveFeeds();
            return true;
        }

        /// <summary>最近的若干条 feed 的 message_id（放在路由信息里告诉邻居「我有这些」）。</summary>
        public List<byte[]> LatestFeedIds(int count)
        {
            var list = new List<byte[]>();
            lock (feedLock)
            {
                for (int i = feedOrder.Count - 1; i >= 0 && list.Count < count; i--)
                    list.Add(feedOrder[i].Id);
            }
            list.Reverse();
            return list;
        }

        /// <summary>挑出本地还没有的 feed id。</summary>
        public List<byte[]> MissingFeedIds(List<byte[]> ids)
        {
            var miss = new List<byte[]>();
            if (ids == null) return miss;
            lock (feedLock)
            {
                foreach (var id in ids)
                {
                    if (id == null || id.Length == 0) continue;
                    if (feedStore.ContainsKey(LocalIdentity.Hex(id))) continue;
                    miss.Add(id);
                }
            }
            return miss;
        }

        public List<FeedMessage> GetFeedsByIds(List<byte[]> ids)
        {
            var res = new List<FeedMessage>();
            if (ids == null) return res;
            lock (feedLock)
            {
                foreach (var id in ids)
                {
                    if (id == null) continue;
                    FeedMessage fm;
                    if (feedStore.TryGetValue(LocalIdentity.Hex(id), out fm)) res.Add(fm);
                }
            }
            return res;
        }

        public int FeedCount { get { lock (feedLock) { return feedOrder.Count; } } }

        public List<FeedMessage> FeedSnapshot()
        {
            lock (feedLock) { return new List<FeedMessage>(feedOrder); }
        }

        /* ===================== Feed 同步（FEED_REQUEST / FEED_RESPONSE） ===================== */

        /// <summary>
        /// 向某个邻居索要缺失的 feed 正文。
        ///
        /// 官方客户端把「有哪些 feed」放在路由信息里定期广播（FeedIdsTable），
        /// 收到的一方挑出自己缺的 id 再发 FeedRequest 正式索取 —— 这才是 qaul 里
        /// feed 的主要传播方式（floodsub 只是「顺手带一带」）。
        /// </summary>
        private void SendFeedRequest(Conn conn, List<byte[]> missing)
        {
            if (conn == null || missing == null || missing.Count == 0) return;

            // 冷却：同一个 id 30 秒内只索求一次
            var ask = new List<byte[]>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (feedRequested)
            {
                foreach (var id in missing)
                {
                    var key = LocalIdentity.Hex(id);
                    long last;
                    if (feedRequested.TryGetValue(key, out last) && now - last < 30000) continue;
                    feedRequested[key] = now;
                    ask.Add(id);
                }
            }
            if (ask.Count == 0) return;

            var ids = new PbWriter();
            foreach (var id in ask) ids.Bytes(1, id);

            var fw = new PbWriter();
            fw.Sub(1, ids);                    // FeedRequestMessage { feeds = FeedIdsTable }
            SendRouterInfoModule(conn, 1, fw.ToArray());
            Log(L10n.T("向 ") + conn.PeerIdB58.Substring(0, Math.Min(10, conn.PeerIdB58.Length)) +
                L10n.T(" 索求 ") + ask.Count + L10n.T(" 条公开信息"));
        }

        /// <summary>回复邻居索求的 feed 正文（FeedResponseTable）。</summary>
        private void SendFeedResponse(Conn conn, List<FeedMessage> messages)
        {
            if (conn == null || messages == null || messages.Count == 0) return;

            var table = new PbWriter();
            foreach (var m in messages)
            {
                var fm = new PbWriter();
                fm.Bytes(1, m.Id);
                fm.Bytes(2, m.Sender);
                fm.Str(3, m.Content);
                fm.Int(4, m.Time);
                table.Sub(1, fm);              // FeedResponseTable { messages = FeedMessage }
            }

            var fw = new PbWriter();
            fw.Sub(1, table);                  // FeedResponseMessage { feeds = FeedResponseTable }
            SendRouterInfoModule(conn, 2, fw.ToArray());
            Log(L10n.T("回复 ") + messages.Count + L10n.T(" 条公开信息给 ") +
                conn.PeerIdB58.Substring(0, Math.Min(10, conn.PeerIdB58.Length)));
        }

        /// <summary>处理邻居索求的 feed 正文（FEED_REQUEST，module=1）。</summary>
        private void HandleFeedRequest(Conn conn, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            // FeedRequestMessage { feeds = FeedIdsTable { ids = 1 } }
            var r = new PbReader(payload);
            int f, w;
            List<byte[]> want = null;
            while (r.Next(out f, out w))
            {
                if (f == 1)
                {
                    var table = r.Sub();
                    int f2, w2;
                    while (table.Next(out f2, out w2))
                    {
                        if (f2 == 1) { if (want == null) want = new List<byte[]>(); want.Add(table.Bytes()); }
                        else table.Skip(w2);
                    }
                }
                else r.Skip(w);
            }
            if (want == null || want.Count == 0) return;

            var have = new List<FeedMessage>();
            lock (feedLock)
            {
                foreach (var id in want)
                {
                    if (id == null || id.Length == 0) continue;
                    FeedMessage fm;
                    if (feedStore.TryGetValue(LocalIdentity.Hex(id), out fm)) have.Add(fm);
                }
            }
            FeedServed++;
            Log(L10n.T("邻居索求 ") + want.Count + L10n.T(" 条公开信息，我们手上有 ") + have.Count + L10n.T(" 条"));
            SendFeedResponse(conn, have);
        }

        /// <summary>接收邻居回给我们的 feed 正文（FEED_RESPONSE，module=2），入库并通知界面。</summary>
        private void HandleFeedResponse(Conn conn, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            // FeedResponseMessage { feeds = FeedResponseTable { messages = 1 (FeedMessage) } }
            var r = new PbReader(payload);
            int f, w;
            List<byte[]> messages = null;
            while (r.Next(out f, out w))
            {
                if (f == 1)
                {
                    var table = r.Sub();
                    int f2, w2;
                    while (table.Next(out f2, out w2))
                    {
                        if (f2 == 1) { if (messages == null) messages = new List<byte[]>(); messages.Add(table.Bytes()); }
                        else table.Skip(w2);
                    }
                }
                else r.Skip(w);
            }
            if (messages == null || messages.Count == 0) return;

            foreach (var m in messages)
            {
                var mr = new PbReader(m);
                byte[] id = null, sender = null;
                string content = null;
                long time = 0;
                int f3, w3;
                while (mr.Next(out f3, out w3))
                {
                    if (f3 == 1) id = mr.Bytes();
                    else if (f3 == 2) sender = mr.Bytes();
                    else if (f3 == 3) content = mr.Str();
                    else if (f3 == 4) time = (long)mr.Varint();
                    else mr.Skip(w3);
                }
                if (id == null) continue;
                if (!AddFeedMessage(id, sender, content, time)) continue;

                FeedRx++;
                var cb = OnFeed;
                if (cb != null)
                {
                    try { cb(sender != null ? Base58.Encode(sender) : null, content); }
                    catch { }
                }
            }
        }

        /// <summary>往 /qaul_info 上发一个带签名的 RouterInfoContainer（module 号对应 RouterInfoModule）。</summary>
        private void SendRouterInfoModule(Conn conn, int module, byte[] payload)
        {
            try
            {
                var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var content = new PbWriter();
                content.Bytes(1, identity.QaulPeerIdBytes);
                content.Int(2, module);
                content.Bytes(3, payload);
                content.Int(4, (long)now);
                var contentBytes = content.ToArray();

                var sig = Curve25519.Ed25519Sign(identity.QaulSeed, contentBytes);
                var container = new PbWriter();
                container.Bytes(1, sig);
                container.Bytes(2, contentBytes);
                SendOneShot(conn, Ms.QaulInfo, container.ToArray());
            }
            catch (Exception ex) { Log(L10n.T("发送 qaul_info(module=") + module + L10n.T(") 失败: ") + ex.Message); }
        }

        /* ===================== 路由信息 ===================== */

        private void InfoLoop()
        {
            while (running)
            {
                Thread.Sleep(idle ? 45000 : 15000);
                propagationId++;
                List<Conn> all;
                lock (connsLock) { all = new List<Conn>(conns.Values); }
                foreach (var c in all)
                {
                    if (c.Mux.Closed) { Remove(c, L10n.T("连接已关闭")); continue; }

                    // 订阅流断了就补：没有订阅，别人不会把 feed 推给我们
                    EnsureFloodsub(c);
                    SendInfo(c);

                    // 还没拿到这个邻居上「账号」的资料就再要一次。
                    // 官方 qaul 的路由表不一定含自己，只有显式请求才会回 UserInfo。
                    try
                    {
                        if (Messaging != null)
                        {
                            var acct = UserOnNode(c.PeerIdB58) ?? c.PeerIdB58;
                            if (!Messaging.HaveKey(acct))
                            {
                                var q8 = Curve25519.Slice(Base58.Decode(acct), 6, 8);
                                SendUserRequest(c, new List<byte[]> { q8 });
                            }
                        }
                    }
                    catch { }
                }

                try { if (Messaging != null) Messaging.Retransmit(); } catch { }
            }
        }

        /// <summary>构造并发送一份 RouterInfoContainer（qaul 用它建立路由表）。</summary>
        private void SendInfo(Conn conn)
        {
            try
            {
                var nodeId = identity.QaulPeerIdBytes;

                // RoutingInfoEntry { user=1, rtt=2, hc=3, pgid=5, version=6 }
                // hc = 跳数向量：自己写 {0}（对端 +1 变 1 跳）；邻居也写 {0}，表示经我 1 跳可达。
                var table = new PbWriter();
                int entries = 0;

                // 官方 qaul 的 RoutingInfoEntry.user 是 8 字节 q8id（不是 38 字节 PeerId）。
                // 写 38 字节会让对端 select_stale 拿它当 q8id 查表，永远查不到 —— 对端也就
                // 不会来要我们的公钥，双向都学不到对方。
                var selfEntry = new PbWriter();
                selfEntry.Bytes(1, MyQ8);
                selfEntry.Int(2, 0);
                selfEntry.Bytes(3, new byte[] { 0 });
                selfEntry.Int(5, propagationId);
                table.Sub(1, selfEntry);
                entries++;

                // 关键：把自己知道的其他人也播出去。否则 A 连了 B、C，B/C 永远
                // 不知道对方存在（没有直连 → 学不到公钥 → 群里成员之间发不出消息）。
                //
                // 中转节点还多一层责任：把自己**认识但不直连**的用户也宣扬出去。
                // 对端收到后会问我们索要这些用户的信息，而我们的 HandleUserRequest
                // 会把自己用户表里的答案给出去 —— 这才叫「中继」。
                var advertised = new List<byte[]>();
                var seenQ8 = new HashSet<string>(StringComparer.Ordinal);
                seenQ8.Add(LocalIdentity.Hex(MyQ8));

                lock (connsLock)
                    foreach (var kv in conns)
                    {
                        if (kv.Value.Mux.Closed) continue;
                        var acct = UserOnNode(kv.Key);
                        if (!string.IsNullOrEmpty(acct)) advertised.Add(Base58.Decode(acct));
                        advertised.Add(Base58.Decode(kv.Key));
                    }
                lock (routes)
                    foreach (var kv in routes)
                        if (kv.Key != identity.QaulPeerId) advertised.Add(Base58.Decode(kv.Key));
                if (Messaging != null)
                    foreach (var u in Messaging.UserList())
                        if (!string.IsNullOrEmpty(u.KeyHex) && !u.Node) advertised.Add(Base58.Decode(u.Id));

                foreach (var pid in advertised)
                {
                    if (pid == null || pid.Length != 38) continue;
                    if (Curve25519.BytesEqual(pid, nodeId)) continue;

                    var q8 = Curve25519.Slice(pid, 6, 8);
                    var q8key = LocalIdentity.Hex(q8);
                    if (!seenQ8.Add(q8key)) continue;

                    uint version;
                    lock (userVersions) { userVersions.TryGetValue(Base58.Encode(pid), out version); }

                    var e = new PbWriter();
                    e.Bytes(1, q8);
                    e.Int(2, 0);
                    e.Bytes(3, new byte[] { 0 });
                    e.Int(5, propagationId);
                    if (version > 0) e.Int(6, version);
                    table.Sub(1, e);
                    entries++;
                }

                // RouterInfoMessage { node=1, routes=2, feeds=4, timestamp=5 }
                var info = new PbWriter();
                info.Bytes(1, nodeId);
                info.Sub(2, table);

                // 关键中的关键：把「我有哪些公开信息」一并广播出去。
                // qaul 的 feed 主要不是靠 floodsub 推的，而是靠这条 id 清单让邻居
                // 发现自己缺哪些、再发 FeedRequest 来要。少了这一字段，
                // 官方客户端永远不知道我们（以及我们背后那张网）有什么 feed。
                var myFeeds = LatestFeedIds(5);
                if (myFeeds.Count > 0)
                {
                    var ids = new PbWriter();
                    foreach (var id in myFeeds) ids.Bytes(1, id);
                    info.Sub(4, ids);
                }

                var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                info.Int(5, (long)now);

                // RouterInfoContent { id=1, module=2, content=3, time=4 }
                var content = new PbWriter();
                content.Bytes(1, nodeId);
                content.Int(2, 0);                     // ROUTER_INFO
                content.Bytes(3, info.ToArray());
                content.Int(4, (long)now);
                var contentBytes = content.ToArray();

                // RouterInfoContainer { signature=1, message=2 }
                var sig = Curve25519.Ed25519Sign(identity.QaulSeed, contentBytes);
                var container = new PbWriter();
                container.Bytes(1, sig);
                container.Bytes(2, contentBytes);

                SendOneShot(conn, Ms.QaulInfo, container.ToArray());
            }
            catch (Exception ex)
            {
                Log(L10n.T("发送路由信息失败: ") + ex.Message);
                // 一次推路由失败（多半是流级瞬时错误）不该把整条连接拆掉：
                // 拆了就要重连，重连又会把对端那边的旧连接顶掉，越修越乱。
                // 只在连接确实已关闭时才清理，其余情况交给周期任务重试。
                if (conn.Mux.Closed) Remove(conn, L10n.T("连接已关闭"));
            }
        }

        private void SendOneShot(Conn conn, string protocol, byte[] payload)
        {
            if (conn.Mux.Closed) return;
            var st = conn.Mux.OpenStream();
            if (!Ms.SelectOne(st, protocol))
            {
                Log(L10n.T("协议协商被拒绝: ") + protocol);
                QStreams.Reset(st);
                return;
            }
            IoUtil.WriteVarintPrefixed(st, payload);
            Log(L10n.T("已发送 ") + protocol + " (" + payload.Length + L10n.T(" 字节, head=") +
                BitConverter.ToString(payload, 0, Math.Min(8, payload.Length)) + ") → " + conn.PeerIdB58);
            QStreams.CloseWrite(st);
            // 不能立刻 Close/Reset：RST 会让对端丢弃已收到的数据。
            // 给对端一点时间读完，然后只把本地流表项删掉（不发任何帧）。
            // 走统一的延迟清理调度器，不再每条流开一个线程（见 ForgetScheduler）。
            ForgetScheduler.Schedule(conn.Mux, QStreams.Id(st), 3000);
        }

        /// <summary>处理对端发来的路由信息（qaul → 我们）。</summary>
        private void HandleInfoPayload(Conn conn, byte[] data)
        {
            try
            {
                var r = new PbReader(data);
                byte[] message = null;
                int field, wire;
                while (r.Next(out field, out wire))
                {
                    if (field == 2) message = r.Bytes();
                    else r.Skip(wire);
                }
                if (message == null) return;

                var c = new PbReader(message);
                int module = 0;
                byte[] nodeId = null;
                byte[] contentBytes = null;
                byte[] sig = null;
                var r0 = new PbReader(data);
                int f0, w0;
                while (r0.Next(out f0, out w0))
                {
                    if (f0 == 1) sig = r0.Bytes();
                    else r0.Skip(w0);
                }
                while (c.Next(out field, out wire))
                {
                    if (field == 1) nodeId = c.Bytes();
                    else if (field == 2) module = (int)c.Varint();
                    else if (field == 3) contentBytes = c.Bytes();
                    else c.Skip(wire);
                }
                if (contentBytes == null) return;

                if (module == 2)
                {
                    // FEED_RESPONSE：FeedResponseMessage { feeds = FeedResponseTable{ messages } }
                    HandleFeedResponse(conn, contentBytes);
                    return;
                }
                if (module == 1)
                {
                    // FEED_REQUEST：FeedRequestMessage { feeds = FeedIdsTable{ ids } }
                    HandleFeedRequest(conn, contentBytes);
                    return;
                }
                if (module == 3)
                {
                    // USER_REQUEST：UserIdTable { ids = 1 }
                    HandleUserRequest(conn, contentBytes);
                    return;
                }
                if (module == 4)
                {
                    // USER_RESPONSE：UserInfoTable { info=1, signed_profiles=2 }
                    HandleUserResponse(conn, contentBytes);
                    return;
                }
                if (module == 0)
                {
                    // RouterInfoMessage：qaul 用它传播「哪些用户在线、几跳」。
                    // node=1, routes=2, feeds=4, timestamp=5；路由项 user=1, rtt=2, hc=3(字节), pgid=5, version=6
                    var r2 = new PbReader(contentBytes);
                    int f, w;
                    var unknown = new List<byte[]>();
                    var theirFeeds = new List<byte[]>();
                    while (r2.Next(out f, out w))
                    {
                        if (f == 1) LastInfoNode = Base58.Encode(r2.Bytes());
                        else if (f == 2)
                        {
                            var table = r2.Sub();
                            int f2, w2;
                            while (table.Next(out f2, out w2))
                            {
                                if (f2 != 1) { table.Skip(w2); continue; }
                                var entry = table.Sub();
                                byte[] user = null;
                                uint version = 0;
                                int f3, w3;
                                while (entry.Next(out f3, out w3))
                                {
                                    if (f3 == 1) user = entry.Bytes();
                                    else if (f3 == 6) version = (uint)entry.Varint();
                                    else entry.Skip(w3);
                                }
                                if (user == null || user.Length == 0) continue;

                                // 官方 qaul 的 RoutingInfoEntry.user 是 **8 字节 q8id**
                                // （见 router/connections.rs 的 `valid_q8id`），不是 38 字节 PeerId。
                                // 早先按 38 字节解析，导致：① 路由键错 ② 请求的 q8id 被截成 2 字节，
                                // 对端应答永远是空表 —— 这就是「一直等公钥」的根因。
                                var q8id = user.Length >= 8 ? Curve25519.Slice(user, user.Length - 8, 8) : user;
                                var q8hex = LocalIdentity.Hex(q8id);
                                if (Curve25519.BytesEqual(q8id, MyQ8)) continue;

                                // q8id → 完整 PeerId：会话/用户表里认识的就能还原
                                var uid = ResolveQ8ToPeer(q8id);

                                if (uid != null) lock (routes) { routes[uid] = conn.PeerIdB58; }
                                else lock (routesByQ8) { routesByQ8[q8hex] = conn.PeerIdB58; }

                                // 不认识（或没公钥）就去要；资料版本变新了也要重新拉一次；
                                // 请求体里放的必须是 8 字节 q8id
                                bool stale = false;
                                if (uid != null && version > 0)
                                {
                                    uint known;
                                    lock (userVersions)
                                    {
                                        known = userVersions.TryGetValue(uid, out known) ? known : 0;
                                        if (version > known) { stale = true; userVersions[uid] = version; }
                                    }
                                }

                                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                bool ask = false;
                                lock (requested)
                                {
                                    long last;
                                    if (!requested.TryGetValue(q8hex, out last) || nowMs - last > 10000)
                                    {
                                        requested[q8hex] = nowMs;
                                        ask = true;
                                    }
                                }
                                bool needKey = uid == null || Messaging == null || !Messaging.HaveKey(uid);
                                if (ask && (needKey || stale)) unknown.Add(q8id);

                                var cbNew = OnUserRoute;
                                if (cbNew != null) { try { cbNew(uid ?? q8hex, conn.PeerIdB58); } catch { } }
                            }
                        }
                        else if (f == 4)
                        {
                            // FeedIdsTable { ids = 1 }：对端「有哪些公开信息」，缺的稍后去要
                            var idsTable = r2.Sub();
                            int f2, w2;
                            while (idsTable.Next(out f2, out w2))
                            {
                                if (f2 == 1) theirFeeds.Add(idsTable.Bytes());
                                else idsTable.Skip(w2);
                            }
                        }
                        else r2.Skip(w);
                    }

                    // 不管对方路由表里有没有列自己，都顺手要一次「对端用户」的资料。
                    // 官方 qaul 只有收到针对某个 q8id 的 UserRequest 才会回 UserInfo。
                    try
                    {
                        var wantNode = Messaging != null && !Messaging.HaveKey(conn.PeerIdB58);
                        var wantUser = UserOnNode(conn.PeerIdB58);
                        byte[] selfQ8 = null;
                        if (wantNode) selfQ8 = Curve25519.Slice(Base58.Decode(conn.PeerIdB58), 6, 8);
                        else if (wantUser != null && !Messaging.HaveKey(wantUser))
                            selfQ8 = Curve25519.Slice(Base58.Decode(wantUser), 6, 8);
                        if (selfQ8 != null)
                        {
                            bool dup = false;
                            foreach (var q in unknown)
                                if (q.Length == 8 && Curve25519.BytesEqual(q, selfQ8)) { dup = true; break; }
                            if (!dup) unknown.Add(selfQ8);
                        }
                    }
                    catch { }

                    if (unknown.Count > 0) SendUserRequest(conn, unknown);

                    // 缺哪些 feed 就去要：这是官方客户端把 feed 传给我们的主通道
                    if (theirFeeds.Count > 0)
                    {
                        var missing = MissingFeedIds(theirFeeds);
                        if (missing.Count > 0) SendFeedRequest(conn, missing);
                        FeedAdvertised = theirFeeds.Count;
                    }

                    // 用 content.id 里内嵌的公钥验 container.signature
                    string signerPub = null;
                    var c3 = new PbReader(message);
                    while (c3.Next(out f, out w))
                    {
                        if (f == 1)
                        {
                            var idBytes = c3.Bytes();
                            if (idBytes != null && idBytes.Length == 38 && idBytes[0] == 0x00 && idBytes[1] == 0x24)
                                signerPub = LocalIdentity.Hex(Curve25519.Slice(idBytes, 6, 32));
                        }
                        else c3.Skip(w);
                    }
                    if (signerPub != null && sig != null)
                    {
                        LastInfoSignatureOk = Curve25519.Ed25519Verify(
                            LocalIdentity.Unhex(signerPub), message, sig);

                        // 顺手把这个「已经用签名证明持有私钥」的节点身份记下来（只作校验用，不当联系人）
                        if (LastInfoSignatureOk && !string.IsNullOrEmpty(LastInfoNode)) RememberPeer(LastInfoNode);
                    }

                    InfoRx++;
                    var cb = OnInfo;
                    if (cb != null) { try { cb(conn.PeerIdB58); } catch { } }
                }
            }
            catch (Exception ex)
            {
                Log(L10n.T("解析路由信息失败: ") + ex.Message);
            }
        }

        /// <summary>向邻居索要这些用户的公钥与昵称（UserIdTable{ids}）。</summary>
        private void SendUserRequest(Conn conn, List<byte[]> q8ids)
        {
            try
            {
                var table = new PbWriter();
                foreach (var id in q8ids) table.Bytes(1, id);

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var content = new PbWriter();
                content.Bytes(1, identity.QaulPeerIdBytes);
                content.Int(2, 3);                     // USER_REQUEST
                content.Bytes(3, table.ToArray());
                content.Int(4, now);
                var contentBytes = content.ToArray();

                var sig = Curve25519.Ed25519Sign(identity.QaulSeed, contentBytes);
                var container = new PbWriter();
                container.Bytes(1, sig);
                container.Bytes(2, contentBytes);

                SendOneShot(conn, Ms.QaulInfo, container.ToArray());
                UserReqSent++;
                Log(L10n.T("向 ") + conn.PeerIdB58.Substring(0, Math.Min(10, conn.PeerIdB58.Length)) +
                    L10n.T(" 请求 ") + q8ids.Count + L10n.T(" 个用户的信息（累计 ") + UserReqSent + L10n.T(" 次）"));
            }
            catch (Exception ex) { Log(L10n.T("发送 USER_REQUEST 失败: ") + ex.Message); }
        }

        private void HandleUserResponse(Conn conn, byte[] payload)
        {
            try
            {
                UserRespRx++;
                int parsed = 0;
                var table = new PbReader(payload);
                int f, w;
                while (table.Next(out f, out w))
                {
                    // 官方新版优先回「自签名资料」（UserInfoTable.signed_profiles），
                    // 旧版回未签名的 UserInfo。两条路径都得通 —— 只会解 field 1 的话，
                    // 对着新版官方端会一个用户都解不出来，一直在「等公钥」。
                    if (f == 2)
                    {
                        var sp = table.Sub();
                        byte[] profile = null, psig = null;
                        int fs, ws;
                        while (sp.Next(out fs, out ws))
                        {
                            if (fs == 1) profile = sp.Bytes();
                            else if (fs == 2) psig = sp.Bytes();
                            else sp.Skip(ws);
                        }
                        if (profile != null && ApplySignedProfile(profile, psig)) parsed++;
                        continue;
                    }
                    if (f != 1) { table.Skip(w); continue; }

                    var info = table.Sub();
                    byte[] idBytes = null, keyProto = null;
                    string name = null;
                    uint caps = 0;
                    int f2, w2;
                    while (info.Next(out f2, out w2))
                    {
                        if (f2 == 1) idBytes = info.Bytes();
                        else if (f2 == 2) keyProto = info.Bytes();
                        else if (f2 == 3) name = info.Str();
                        else if (f2 == 4) caps = (uint)info.Varint();
                        else info.Skip(w2);
                    }
                    if (idBytes == null || idBytes.Length != 38) continue;

                    var idB58 = Base58.Encode(idBytes);
                    byte[] edPub = null;
                    if (keyProto != null && keyProto.Length >= 36)
                        edPub = Curve25519.Slice(keyProto, keyProto.Length - 32, 32);

                    ApplyUserInfo(conn, idB58, edPub, name, caps);
                    parsed++;
                }
                LastUserRespCount = parsed;
                if (parsed == 0)
                    Log(L10n.T("用户应答里没有可用资料（对方用户表还没有这些 q8id，或字段不兼容）"));
            }
            catch (Exception ex) { Log(L10n.T("解析用户应答失败: ") + ex.Message); }
        }

        /// <summary>登记一个用户，并记下「这个用户是从哪个节点学来的」。</summary>
        private void ApplyUserInfo(Conn conn, string idB58, byte[] edPub, string name, uint caps)
        {
            if (Messaging != null) Messaging.UpsertUser(idB58, name, edPub, false);
            lock (userVersions) { if (!userVersions.ContainsKey(idB58)) userVersions[idB58] = 1; }
            RememberPeer(idB58);
            if (conn != null) NoteUserFrom(conn.PeerIdB58, idB58);
            Log(L10n.T("学到用户 ") + (!string.IsNullOrEmpty(name) ? name : idB58.Substring(0, 10)) +
                (edPub != null ? "（含公钥）" : "（无公钥）"));
        }

        /// <summary>
        /// 校验并落一份「自签名用户资料」（UserProfile + 用户私钥签名）。
        /// 这一步必须较真：签名过不了就不能采信昵称，否则谁都能冒名。
        /// </summary>
        private bool ApplySignedProfile(byte[] profileBytes, byte[] signature)
        {
            try
            {
                var p = new PbReader(profileBytes);
                byte[] id = null, keyProto = null;
                string name = null, bio = null;
                uint version = 0;
                int f, w;
                while (p.Next(out f, out w))
                {
                    if (f == 1) id = p.Bytes();
                    else if (f == 2) keyProto = p.Bytes();
                    else if (f == 3) name = p.Str();
                    else if (f == 5) bio = p.Str();
                    else if (f == 6) version = (uint)p.Varint();
                    else p.Skip(w);
                }
                if (id == null || id.Length != 38 || keyProto == null || keyProto.Length < 36) return false;

                var pub = Curve25519.Slice(keyProto, keyProto.Length - 32, 32);
                if (signature != null && signature.Length == 64 &&
                    !Curve25519.Ed25519Verify(pub, profileBytes, signature))
                {
                    Log(L10n.T("丢弃一份验签失败的用户资料（疑似伪造）"));
                    return false;
                }

                var idB58 = Base58.Encode(id);
                ApplyUserInfo(null, idB58, pub, name, 0);
                if (version > 0) lock (userVersions) { userVersions[idB58] = version; }
                if (!string.IsNullOrEmpty(bio) && Messaging != null) Messaging.SetBio(idB58, bio);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 发一条 qaul_messaging 容器给某个用户：直连邻居直接发，否则按路由表交给下一跳。
        /// </summary>
        public bool SendQaulMessaging(string destB58, byte[] payload)
        {
            return TrySendKnownRoute(destB58, payload, null);
        }

        /// <summary>按已知路由投递：直连＞路由表＞q8id 路由；<paramref name="exclude"/> 用于跳过来路。</summary>
        private bool TrySendKnownRoute(string destB58, byte[] payload, string exclude)
        {
            if (string.IsNullOrEmpty(destB58) || payload == null) return false;

            Conn direct = null;
            lock (connsLock) { conns.TryGetValue(destB58, out direct); }
            if (direct != null && !direct.Mux.Closed && direct.PeerIdB58 != exclude)
            {
                // 直投必须**真的**发出去才叫通：写失败时返回 false，让调用方走中继兜底，
                // 而不是像老实现那样无条件 return true（消息静默丢失）。
                return SendMessagingOn(direct, payload);
            }

            string via = null;
            lock (routes) { routes.TryGetValue(destB58, out via); }
            if (via == null)
            {
                // 只从官方路由表里学到 q8id、还没还原成 PeerId 的情况
                try
                {
                    var pid = Base58.Decode(destB58);
                    if (pid != null && pid.Length == 38)
                    {
                        var q8hex = LocalIdentity.Hex(Curve25519.Slice(pid, 6, 8));
                        lock (routesByQ8) { routesByQ8.TryGetValue(q8hex, out via); }
                    }
                }
                catch { }
            }
            if (via == null || via == exclude) return false;

            Conn hop = null;
            lock (connsLock) { conns.TryGetValue(via, out hop); }
            if (hop == null || hop.Mux.Closed) return false;

            return SendMessagingOn(hop, payload);
        }

        /// <summary>转发别人发给第三方的容器（qaul 的 messaging 转发）。</summary>
        public bool ForwardQaulMessaging(string destB58, byte[] payload)
        {
            return ForwardQaulMessaging(destB58, payload, null);
        }

        /// <summary>
        /// 中转：把不是给自己的消息容器往下传。
        ///
        /// qaul 的做法是「按路由表走下一跳」；但作为一个**中转节点**，遇到还没有建立
        /// 路由的目标时必须兜底发给其余邻居，否则两台不直接相连的官方客户端之间
        /// 永远通不了信（它们要靠我们这种中间节点接力）。
        /// </summary>
        public bool ForwardQaulMessaging(string destB58, byte[] payload, string fromNeighbour)
        {
            if (payload == null || payload.Length == 0) return false;

            // 指纹去重：同一份容器 1 分钟内不再转发，防止在中继链路里无限打转
            var fp = LocalIdentity.Hex(LocalIdentity.Sha256(payload));
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (forwardedLock)
            {
                long last;
                if (forwarded.TryGetValue(fp, out last) && nowMs - last < 60000) return false;
                forwarded[fp] = nowMs;
                if (forwarded.Count > 400)
                {
                    var dead = new List<string>();
                    foreach (var kv in forwarded) if (nowMs - kv.Value > 60000) dead.Add(kv.Key);
                    foreach (var k in dead) forwarded.Remove(k);
                }
            }

            if (TrySendKnownRoute(destB58, payload, fromNeighbour)) { Relayed++; return true; }

            List<Conn> all;
            lock (connsLock) { all = new List<Conn>(conns.Values); }

            bool any = false;
            foreach (var c in all)
            {
                if (c.Mux.Closed) continue;
                if (c.PeerIdB58 == fromNeighbour) continue;
                // 只有真的写成功了才算"中继出去了"；老实现无脑 any=true，
                // 调用方以为中继成功，其实一个字节都没发出去。
                try { if (SendMessagingOn(c, payload)) any = true; }
                catch { }
            }
            if (any) Relayed++;
            return any;
        }

        /// <summary>
        /// 在某条连接上发一条 qaul_messaging 容器。返回 false 表示**没发出去**
        /// （协议协商被拒 / 写失败），调用方必须据此走中继兜底——
        /// 老实现这里静默返回，直投"假成功"后消息就凭空消失了。
        /// </summary>
        private bool SendMessagingOn(Conn conn, byte[] payload)
        {
            try
            {
                var st = conn.Mux.OpenStream();
                if (!Ms.SelectOne(st, Ms.QaulMessaging))
                {
                    Log(L10n.T("协议协商被拒绝: ") + Ms.QaulMessaging);
                    QStreams.Reset(st);
                    return false;
                }
                IoUtil.WriteVarintPrefixed(st, payload);
                QStreams.CloseWrite(st);
                // 同上：延迟清理走调度器，避免每条消息一个线程
                ForgetScheduler.Schedule(conn.Mux, QStreams.Id(st), 3000);
                return true;
            }
            catch (Exception ex)
            {
                Log(L10n.T("qaul_messaging 发送失败: ") + ex.Message);
                return false;
            }
        }

        private void HandleUserRequest(Conn conn, byte[] payload)
        {
            var table = new PbReader(payload);
            var q8 = new List<byte[]>();
            int field, wire;
            while (table.Next(out field, out wire))
            {
                if (field == 1) q8.Add(table.Bytes());
                else table.Skip(wire);
            }

            var mine = new byte[8];
            Buffer.BlockCopy(identity.QaulPeerIdBytes, 6, mine, 0, 8);

            // UserInfoTable { info = 1 (UserInfo), signed_profiles = 2 }
            // UserInfo { id=1, key=2, name=3, capabilities=4 }
            // 注意：qaul 的语义是「问谁就给谁」，不只是回自己——B 问 C 的公钥时，
            // 只有 A 能回答（B/C 之间没有直连），这里必须把本地用户表里认识的也答上。
            // 这也是「中转节点」的价值：我们手里的用户目录可以被别人取用。
            var tableOut = new PbWriter();
            int answered = 0;

            foreach (var id in q8)
            {
                if (id == null || id.Length != 8) continue;

                if (Curve25519.BytesEqual(id, mine))
                {
                    var self = new PbWriter();
                    self.Bytes(1, identity.QaulPeerIdBytes);
                    self.Bytes(2, IdentityProto(identity.QaulPublic));
                    self.Str(3, displayName);
                    tableOut.Sub(1, self);
                    answered++;
                    continue;
                }

                if (Messaging == null) continue;
                var users = Messaging.UserList();
                bool hit = false;

                foreach (var u in users)
                {
                    if (string.IsNullOrEmpty(u.KeyHex)) continue;
                    byte[] pid;
                    try { pid = Base58.Decode(u.Id); } catch { continue; }
                    if (pid == null || pid.Length != 38) continue;
                    var q = new byte[8];
                    Buffer.BlockCopy(pid, 6, q, 0, 8);
                    if (!Curve25519.BytesEqual(q, id)) continue;

                    var info = new PbWriter();
                    info.Bytes(1, pid);
                    info.Bytes(2, IdentityProto(LocalIdentity.Unhex(u.KeyHex)));
                    info.Str(3, string.IsNullOrEmpty(u.Name) ? ShortId(u.Id) : u.Name);
                    tableOut.Sub(1, info);
                    answered++;
                    hit = true;
                    break;
                }
                if (hit) continue;

                // 兜底：路由里有这条 q8id（路由表里有它的下一跳邻居），
                // 但我们还没拿到完整 id —— 把「谁认识这个用户」的问题接力给那个下一跳去回答。
                if (id.Length == 8)
                {
                    string via = null;
                    lock (routesByQ8) { routesByQ8.TryGetValue(LocalIdentity.Hex(id), out via); }
                    if (!string.IsNullOrEmpty(via) && via != conn.PeerIdB58) RelayUserRequest(via, new List<byte[]> { id });
                }
            }
            if (answered == 0) return;

            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var content = new PbWriter();
            content.Bytes(1, identity.QaulPeerIdBytes);
            content.Int(2, 4);                 // USER_RESPONSE
            content.Bytes(3, tableOut.ToArray());
            content.Int(4, (long)now);
            var contentBytes = content.ToArray();

            var sig = Curve25519.Ed25519Sign(identity.QaulSeed, contentBytes);
            var container = new PbWriter();
            container.Bytes(1, sig);
            container.Bytes(2, contentBytes);

            SendOneShot(conn, Ms.QaulInfo, container.ToArray());
        }

        /* ===================== 对外：广播 feed ===================== */

        /// <summary>
        /// 向 qaul 的全局 feed（floodsub topic "pages"）发布一条消息。
        /// 这是 qaul 里唯一不加密的公共广播通道，qaul 客户端会在「Feed」里显示。
        /// </summary>
        public bool PublishFeed(string content)
        {
            if (!running || string.IsNullOrEmpty(content)) return false;

            // FeedMessageContent { sender=1, content=2, time=3 }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fm = new PbWriter();
            fm.Bytes(1, identity.QaulPeerIdBytes);
            fm.Str(2, content);
            fm.Int(3, now);
            var fmBytes = fm.ToArray();

            // FeedContainer { signature=1, message=2 }
            // 这里的 signature 同时就是 qaul 的 message_id —— 自己发的也必须入库，
            // 否则邻居按 id 来索求时我们答不上来（官方端就会有「发不出 Feed」的错觉）。
            var sig = Curve25519.Ed25519Sign(identity.QaulSeed, fmBytes);
            var container = new PbWriter();
            container.Bytes(1, sig);
            container.Bytes(2, fmBytes);
            var containerBytes = container.ToArray();

            AddFeedMessage(sig, identity.QaulPeerIdBytes, content, now);

            List<Conn> all;
            lock (connsLock) { all = new List<Conn>(conns.Values); }
            int sent = 0;
            var rpc = BuildFloodsubRpc(containerBytes);
            foreach (var c in all)
            {
                if (c.Mux.Closed) continue;
                if (WriteFloodsub(c, rpc)) sent++;
            }
            return sent > 0;
        }

        /// <summary>把一条 FeedContainer 打成 floodsub RPC（含 StrictSign 签名与订阅声明）。</summary>
        private byte[] BuildFloodsubRpc(byte[] containerBytes)
        {
            // floodsub Message { from=1, data=2, seqno=3, topic=4, signature=5, key=6 }
            // libp2p 的 pubsub 默认是 StrictSign：签名内容固定为
            //   "libp2p-pubsub:" ‖ protobuf{from,data,seqno,topic}
            // 少了这一步，libp2p/qaul 都会直接把消息丢掉。
            var body = new PbWriter();
            body.Bytes(1, identity.QaulPeerIdBytes);
            body.Bytes(2, containerBytes);
            body.Bytes(3, BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            body.Str(4, FeedTopic);
            var bodyBytes = body.ToArray();

            var signBase = Concat(Encoding.UTF8.GetBytes("libp2p-pubsub:"), bodyBytes);
            var sig = Curve25519.Ed25519Sign(identity.QaulSeed, signBase);

            var keyProto = new byte[36];
            keyProto[0] = 0x08; keyProto[1] = 0x01; keyProto[2] = 0x12; keyProto[3] = 0x20;
            Buffer.BlockCopy(identity.QaulPublic, 0, keyProto, 4, 32);

            var msg = new PbWriter();
            msg.Raw(bodyBytes);
            msg.Bytes(5, sig);
            msg.Bytes(6, keyProto);

            var rpc = new PbWriter();
            rpc.Sub(1, SubscribeOpts());
            rpc.Sub(2, msg);
            return rpc.ToArray();
        }

        private PbWriter SubscribeOpts()
        {
            // SubOpts { subscribe = 1, topicid = 2 }
            var inner = new PbWriter();
            inner.Bool(1, true);
            inner.Str(2, FeedTopic);
            return inner;
        }

        /* ===================== feed 持久化 ===================== */

        private sealed class FeedRow
        {
            public string Id;
            public string Sender;
            public string Content;
            public long Time;
        }

        private void LoadFeeds()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                var p = Path.Combine(storeDir, "qaul_feed.json");
                if (!File.Exists(p)) return;
                var rows = Newtonsoft.Json.JsonConvert.DeserializeObject<List<FeedRow>>(File.ReadAllText(p, Encoding.UTF8));
                if (rows == null) return;
                lock (feedLock)
                {
                    foreach (var r in rows)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                        if (feedStore.ContainsKey(r.Id)) continue;
                        var fm = new FeedMessage
                        {
                            IdHex = r.Id,
                            Sender = LocalIdentity.Unhex(r.Sender),
                            Content = r.Content ?? "",
                            Time = r.Time
                        };
                        feedStore[r.Id] = fm;
                        feedOrder.Add(fm);
                    }
                    while (feedOrder.Count > 300) { feedStore.Remove(feedOrder[0].IdHex); feedOrder.RemoveAt(0); }
                }
            }
            catch { }
        }

        private void SaveFeeds()
        {
            try
            {
                if (string.IsNullOrEmpty(storeDir)) return;
                Directory.CreateDirectory(storeDir);
                var rows = new List<FeedRow>();
                lock (feedLock)
                {
                    foreach (var m in feedOrder)
                        rows.Add(new FeedRow
                        {
                            Id = m.IdHex,
                            Sender = m.Sender == null ? "" : LocalIdentity.Hex(m.Sender),
                            Content = m.Content,
                            Time = m.Time
                        });
                }
                File.WriteAllText(Path.Combine(storeDir, "qaul_feed.json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(rows), Encoding.UTF8);
            }
            catch { }
        }

        /* ===================== 状态 ===================== */

        /// <summary>连接的远端地址（TCP 取 RemoteEndPoint，QUIC 取记录的 EndPoint）。</summary>
        private static string AddrOf(Conn c)
        {
            try
            {
                if (c.Client != null && c.Client.Client != null && c.Client.Client.RemoteEndPoint != null)
                    return c.Client.Client.RemoteEndPoint.ToString();
            }
            catch { }
            return c.EndPoint == null ? "" : c.EndPoint.ToString();
        }

        public List<Dictionary<string, object>> Neighbours()
        {
            var list = new List<Dictionary<string, object>>();
            lock (connsLock)
            {
                foreach (var c in conns.Values)
                {
                    // 官方客户端「设备」≠「账号」：能收发消息的是账号。
                    // peer 给界面用作会话对象，nodeId 保留真实的 libp2p 节点身份。
                    var acct = UserOnNode(c.PeerIdB58);
                    var target = string.IsNullOrEmpty(acct) ? c.PeerIdB58 : acct;
                    list.Add(new Dictionary<string, object>
                    {
                        ["peer"] = target,
                        ["node"] = c.PeerIdB58,
                        ["short"] = c.PeerIdB58.Substring(0, Math.Min(10, c.PeerIdB58.Length)),
                        ["name"] = c.Name,
                        // QUIC 连接没有 TcpClient，c.Client 是 null——直接解引用会抛 NRE，
                        // 而这里一抛，整条 "向邻居中继消息" 的循环就中断了：
                        // 表现是「qaul 中继投递失败」，频道消息到不了 QUIC 邻居。
                        ["addr"] = AddrOf(c)
                    });
                }
            }
            return list;
        }

        public int NeighbourCount
        {
            get { lock (connsLock) { return conns.Count; } }
        }

        /// <summary>当前所有直连邻居的「节点」PeerId 集合（libp2p 传输身份，不是聊天账号）。</summary>
        public HashSet<string> NodePeerIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            lock (connsLock)
            {
                foreach (var c in conns.Values) set.Add(c.PeerIdB58);
            }
            return set;
        }

        /// <summary>某个 id 是不是「节点身份」而不是「聊天账号」。</summary>
        public bool IsNodePeerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id == identity.QaulPeerId) return false;
            lock (connsLock)
            {
                if (conns.ContainsKey(id)) return true;
            }
            // 已学到账号映射的节点：它的节点身份也不是账号
            lock (nodeUserLock)
            {
                if (nodeToUser.ContainsKey(id)) return true;
            }
            return false;
        }

        /// <summary>
        /// 是否与这个 PeerId 有可用的直连连接。
        /// 重投前判断「对端是否在线」用（qaul 只在接收方在线时才重发，
        /// 见 services/messaging/retransmit.rs:92-94）。
        /// </summary>
        public bool IsConnected(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return false;
            lock (connsLock)
            {
                Conn c;
                if (!conns.TryGetValue(peerB58, out c) || c == null) return false;
                return c.Mux != null && !c.Mux.Closed;
            }
        }

        /// <summary>路由表里是否有这个用户（诊断用：说明对端的路由信息我们确实解析到了）。</summary>
        public bool HasRoute(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return false;
            lock (routes) { if (routes.ContainsKey(peerB58)) return true; }
            try
            {
                var pid = Base58.Decode(peerB58);
                if (pid == null || pid.Length != 38) return false;
                var q8hex = LocalIdentity.Hex(Curve25519.Slice(pid, 6, 8));
                lock (routesByQ8) { return routesByQ8.ContainsKey(q8hex); }
            }
            catch { return false; }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}

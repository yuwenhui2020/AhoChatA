using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware;
using Android.Locations;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Webkit;
using Android.Widget;
using Java.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// Android.OS.Environment 与 System.Environment 同名，显式取别名避免歧义
using SysEnv = System.Environment;

namespace AhoChatA
{
    /// <summary>
    /// 聊天桥接。
    ///
    /// 设计与 <see cref="WebBluetoothBridge"/> 完全一致的通信范式：
    ///   1) JS 通过 @JavascriptInterface 方法调用原生（只能传字符串）；
    ///   2) 原生异步完成网络操作；
    ///   3) 通过 webView.EvaluateJavascript 调用 window.__lanEvent 把事件推给 JS。
    ///
    /// 网络协议：
    ///   - 发现：UDP 组播 239.7.7.7:45777，周期广播自身 {peerId,name,port}，
    ///     收到他人广播即登记为在线节点（带心跳超时剔除）。
    ///   - 传输：每台设备起一个 TCP 服务端（默认 45778，占用则顺延）。发送消息时
    ///     对每个在线节点建立短连接投递一帧。
    ///   - 帧格式：4 字节大端长度 + UTF8 JSON 头 + 可选二进制附件（图片/视频/音频）。
    ///     附件落地到 app 私有目录 media/，前端通过 https://app.local/__media/<file>
    ///     访问（MainActivity 的 ShouldInterceptRequest 已做映射）。
    /// </summary>
    public class MchatBridge : Java.Lang.Object
    {
        /// <summary>当前进程的 MchatBridge 单例，供独立 Service（如通知监听）跨组件调用发送。</summary>
        public static MchatBridge Current { get; private set; }

        // 链路本地组播（224.0.0.0/24）。
        // 原来用 239.7.7.7（管理域组播），实测在部分路由器/无线驱动下会被过滤：
        // 两端各自只能收到自己的包，完全发现不到对方；而 mDNS 用的 224.0.0.251 是通的，
        // 所以改用同一网段的 224.0.0.167，既不被路由器转发，又能正常送达同网段设备。
        private const string MulticastAddress = "224.0.0.167";
        private const string MdnsAddress = "224.0.0.251";
        private const int MdnsPort = 5353;
        private const int DiscoveryPort = 45777;
        private const int DefaultTcpPort = 45778;
        private const int DiscoveryIntervalMs = 5000;
        /// <summary>
        /// 判定对端「在线」的时效窗口。
        /// 实测部分机型（同时开热点、多网卡）发现包送达率很低，可能几十秒才成功一帧；
        /// 20s 会让 peer 在两次成功之间被判离线 → 消息直接「没有目标」发不出去，
        /// 表现正是「能看到设备但收不到消息」。放宽到 90s 后，偶发丢包不再导致误判离线，
        /// 真正离线的设备最多多显示一分多钟（发送失败由出站队列退避/熔断兜底）。
        /// </summary>
        private const int PeerTimeoutMs = 90000;
        /// <summary>
        /// 中继报文的最大年龄：超过就不再转发。
        /// qaul 的未确认表 1 小时过期、DTN 报文带 expires_at，都是同一个取舍 ——
        /// 网格里最怕「某台设备上线后补发几小时前的消息」，会把所有人刷一遍旧内容。
        /// </summary>
        private const long StaleRelayMs = 10 * 60 * 1000;
        private const int MaxFrameBytes = 64 * 1024 * 1024; // 单帧 64MB 上限，防止恶意报文撑爆内存

        // 用 Context 而非 Activity：Bridge 仅用到 Context 层级能力（FilesDir/MainLooper/
        // GetSharedPreferences/GetSystemService/RegisterReceiver），因此 Application 上下文
        // 也能构造它——这是把 Bridge 预建提前到 Application.OnCreate 的前提。
        private readonly Context context;
        // 需要 Activity 的能力（权限请求、蓝牙传输构造）由 CoreHost.Ensure 在拿到 Activity 时补上。
        private Activity _activity;
        /// <summary>当前可用的 Activity；Preinit 阶段（仅有 Application 上下文）可能为 null。</summary>
        private Activity CurrentActivity => _activity ?? (context as Activity);
        /// <summary>CoreHost.Ensure 拿到 Activity 后回填，供 Activity 专属能力使用。</summary>
        public void SetActivity(Activity a) { _activity = a; }

        // 主 WebView 可能是 null：App 端已改为原生界面，只有「网页端」模式才需要挂载 WebView。
        // 所有向 JS 派发事件的方法都必须做空判断（RunJs 内部已处理）。
        private Android.Webkit.WebView webView;
        private readonly Handler mainHandler;

        // peerId 现在由身份决定：换指纹时会被替换，因此不能再是 readonly
        private string peerId;
        private string displayName;

        private TcpListener tcpListener;
        private int tcpPort;

        // 静态中转站（IPv4 / IPv6 / 域名，端口可自定义）：跨公网/跨网段时把帧中继给它，
        // 由它再分发给其它设备。持久化到 SharedPreferences，重启后自动重连。
        private readonly List<RelayEndpoint> staticRelays = new List<RelayEndpoint>();
        private const string PrefStaticRelays = "staticRelays";

        // 真机实测：无互联网时 Dns.GetHostEntry 可能阻塞 20~40 秒，
        // 若在 UI 线程触发就是一次 ANR。统一走后台线程 + 硬超时 + 短缓存。
        private static readonly ConcurrentDictionary<string, Tuple<int, IPAddress[]>> DnsResults
            = new ConcurrentDictionary<string, Tuple<int, IPAddress[]>>();
        private static readonly SemaphoreSlim DnsSlots = new SemaphoreSlim(4);
        private const int DnsDefaultTimeoutMs = 2500;
        private const int DnsOkCacheMs = 60000;
        private const int DnsFailCacheMs = 5000;
        private const string PrefTcpPort = "tcpPortPref";   // 用户指定的本机监听端口（0=自动）
        
        /// <summary>安全执行委托，捕获异常并记录日志，避免空 catch 块吞掉错误。</summary>
        private static void SafeRun(string tag, Action action)
        {
            try { action?.Invoke(); }
            catch (Exception ex) { Android.Util.Log.Warn(tag, "SafeRun failed: " + ex.Message); }
        }

        /// <summary>安全执行异步委托，捕获异常并记录日志。</summary>
        private static async Task SafeRunAsync(string tag, Func<Task> action)
        {
            try { await action?.Invoke(); }
            catch (Exception ex) { Android.Util.Log.Warn(tag, "SafeRunAsync failed: " + ex.Message); }
        }

        private int relayHandshakeBusy;   // 0/1：避免周期握手相互重叠
        private UdpClient discoverySocket;
        // 发现包专用发送口：绑定本机局域网 IP。多网卡（既连 Wi-Fi 又开热点）时，
        // 绑定 Any 的套接字发组播/受限广播会被内核从「默认路由网卡」（常见是 ap0 热点口）
        // 送出，同网段对端根本收不到 —— 表现就是「能看到设备却收不到消息」。
        private UdpClient discoverySendSocket;
        private UdpClient mdnsSocket;
        private WifiManager.MulticastLock multicastLock;
        private WifiManager.MulticastLock mdnsMulticastLock;

        private CancellationTokenSource cts;
        private volatile bool started;

        /// <summary>传输是否已启动（短信转发等后台能力据此判断「现在能不能真的发出去」）。</summary>
        public bool IsStarted { get { return started; } }

        // 在线节点表：peerId -> 节点信息
        private readonly ConcurrentDictionary<string, Peer> peers = new ConcurrentDictionary<string, Peer>();

        // 已处理消息 id，去重（组播/多路径可能造成重复投递）。
        // value = 首次见到的时刻（Unix ms），用于「按时间 + 条数」双重淘汰。
        /// <summary>
        /// 是否使用 qaul 的「官方消息层」（libp2p Messaging：SendDm/SendGroupText）。
        ///
        /// 关掉它：官方层会自己封装 Container、用载荷 CRC64 派生消息 id，还会叠加 qaul 自身的
        /// 加密与确认流程 —— 一旦握手/加密出问题，消息就静默丢失，而且 id 和我们自己的帧不一致，
        /// 同一条消息会在会话里出两遍。
        /// 现在改为：**qaul 只当传输管道**，跑我们自己的帧格式（LC1 加密 + 自带 id/meta），
        /// 也就是本程序独有的渠道；官方层仅保留接收侧兼容，不再用于发送。
        /// </summary>
        private const bool UseQaulNativeMessaging = false;


        private static int DiagRx;   // 局域网发现包接收计数（诊断用）

        /// <summary>
        /// 跨渠道去重：同一条消息会同时经「Mchat 帧（LAN / qaul chat frame，id=GUID）」和
        /// 「qaul-native（id 由载荷 CRC64 派生）」两条路到达，**两条路的 id 天生不同**，
        /// 靠 MarkSeen 按 id 拦不住，界面就会出现两遍。这里按
        /// 「会话 + 发送者 + 类型 + 正文（密文即可）+ 卡片标识」再认一次，6 秒内算同一条。
        /// </summary>
                private const long DedupFrameWindowMs = 6000;
        private const int DedupFrameMaxEntries = 10000;
        private const long DedupIdTtlMs = 20 * 60 * 1000;
        private const int DedupIdMaxEntries = 8000;
        private const long DedupContentTtlMs = 5 * 60 * 1000;
        private const int DedupContentMaxEntries = 4000;private readonly string mediaDir;

        // ===== 身份（qaul Ed25519 / bitchat X25519+Ed25519 指纹，可更换） =====
        private readonly IdentityStore identity;

        /// <summary>当前身份（含 qaul PeerId / bitchat peerID / 指纹）。</summary>
        public LocalIdentity Identity { get { return identity == null ? null : identity.Current; } }

        // ===== 会话 / 消息仓库 / qaul 渠道（合并 bitchat 房间·私聊逻辑 + qaul 传输） =====
        /// <summary>消息仓库：房间与私聊的持久化消息、删除墓碑、未读数。</summary>
        private readonly ChatStore store;
        /// <summary>qaul 消息端口（9199，qaul RPC 信封 + chat 载荷）。</summary>
        private readonly QaulChannel qaul;

        /// <summary>qaul 公共 feed 会话（走 floodsub 的 "pages" topic，qaul 客户端可见）。</summary>
        public const string QaulFeedConv = "feed:qaul";

        /// <summary>qaul 网络节点（libp2p：mDNS 发现 + Noise 加密 + yamux + qaul_info/messaging/floodsub）。</summary>
        private QaulNode qnode;
        private QaulMessaging qaulMsg;

        /// <summary>qaul 网络节点的 TCP 端口（libp2p 监听；与 9199 的 RPC 端口无关）。</summary>
        public const int QaulNodePort = 9200;
        /// <summary>当前正在浏览的会话（原生界面 / 网页端会同步它）。</summary>
        private string activeConv;

        // ===== bitchat BLE mesh（聚合 qaul + bitchat 的传输节点） =====
        /// <summary>bitchat 的 BLE mesh 传输层（外设+中心双角色，含逐跳中继）。默认关闭。</summary>
        private BitchatTransport bitchat;
        private volatile bool bitchatEnabled;

        // ===== 统一频道（频道=聊天室；链路=传输方式） =====
        /// <summary>频道注册表：频道名 → 启用的链路集合（qaul/蓝牙/P2P/局域网）。</summary>
        private readonly ChannelRegistry channels;

        // ===== WLAN Direct（Wi-Fi P2P）：让非局域网设备也能直连 =====
        private readonly WlanP2pChannel p2p;
        private volatile bool p2pEnabled;

        // ===== Wi-Fi Aware（NAN）：无 GO 的真对等发现/直连（Android 8+，设备支持时启用）=====
        private WifiAwareChannel wifiAware;

        // ===== 全渠道接入（无网自组网恢复）：无 Wi-Fi 局域网时一键拉起所有离线链路 =====
        /// <summary>
        /// 是否开启「全渠道接入」。开启后，当设备检测不到任何可用私网网卡
        /// （无 Wi-Fi 局域网）时，自动拉起 WLAN Direct + 蓝牙两条离线链路，
        /// 使附近设备无需任何路由器即可自组网，恢复设备间聊天与通话。
        /// </summary>
        private volatile bool fullChannel;
        /// <summary>避免「无局域网」状态下反复弹窗：一次无网 episode 只提示一次，连上局域网后复位。</summary>
        private bool promptDismissedForNoLan;
        /// <summary>NEARBY_WIFI_DEVICES 运行时权限请求码（Android 13+ 操作 WLAN Direct 需要）。</summary>
        private const int ReqNearbyWifi = 7308;

        // ===== Mchat 独有加密层（LC1，单层） =====
        /// <summary>
        /// LC1：频道密码 --PBKDF2--> 消息密钥，ChaCha20-Poly1305 单层 AEAD。
        /// qaul / 蓝牙 / P2P / 局域网任何一条链路出去的内容都先过这一层，
        /// 中继节点与 ISP 只能看到密文。
        /// qaul 自己的单聊/群聊本来就是端到端加密的（中继读不到），
        /// 所以这里只加一层即可，不再叠密钥交换与棘轮。
        /// </summary>
        private readonly MchatCrypto lc1 = new MchatCrypto();

        /// <summary>
        /// Mchat 中间层：夹在「聊天室」与「通信渠道」之间。
        ///   1) 出门前统一过 LC1 → qaul 通信频道上看不到任何明文（隔除窥视）；
        ///   2) 自带设备发现：周期性广播加密信标并经 qaul 中继扩散，
        ///      因此走中继（拿不到局域网探测权限）时也能找到人。
        /// </summary>
        private readonly MchatMidLayer mid = new MchatMidLayer();
        /// <summary>P2P 已组网的节点（ip → 节点信息）；与局域网节点表分开，避免被网络评估清空。</summary>
        private readonly ConcurrentDictionary<string, Peer> p2pNodes = new ConcurrentDictionary<string, Peer>();

        /// <summary>
        /// 多链路网格路由：跳数封顶 + 各链路收发/中继统计。
        /// 一条消息无论在哪个渠道进来，都只中转一次、只发给其它链路（qaul 的 flooder 同思路），
        /// 「A—蓝牙—B—P2P—C，D—qaul—B」这种拓扑才能四个人同时聊天而不刷屏。
        /// </summary>
        private readonly MeshRouter mesh = new MeshRouter();
        /// <summary>出站队列：单线程发送 + 退避重传。见 Transport/MeshOutbox.cs。</summary>
        private readonly MeshOutbox outbox;

        /// <summary>跨链路中转用的信封前缀（蓝牙只能传文本，所以整帧 base64 塞进文本里）。</summary>
        private const string MeshEnvPrefix = "[[mesh]]";

        /// <summary>是否双渠道并行收发（qaul 渠道 + Mchat 原有 TCP 渠道）。</summary>
        public bool DualChannel { get; set; }

        /// <summary>原生界面事件订阅：与 WebView 的 __lanEvent 一一对应。</summary>
        public event Action<string, object> NativeEvent;

        /// <summary>会话列表变化（新消息、已读、删除）时触发。</summary>
        public event Action ConversationsChanged;

        // ===== 语音通话（原生 AudioRecord/AudioTrack，不走 WebRTC） =====
        private readonly VoiceCallManager voiceCall;

        // ===== 语音消息录音（原生 AudioRecord → WAV，规避 WebRTC 安全上下文限制） =====
        private Android.Media.AudioRecord voiceRec;
        private MemoryStream voiceBuf;
        private DateTime voiceStart;
        private bool voiceRecording;
        private readonly object voiceLock = new object();
        private const int VoiceSampleRate = 16000;
        private const int VoiceMaxSeconds = 60;

        /// <summary>历史消息变化（新增一条）时触发，供悬浮窗刷新预览。</summary>
        public event Action HistoryChanged;

        // ===== 网页端支撑 =====
        // 统一的消息历史：app 自身、局域网对端、网页访客发出的消息都会进这里，
        // 网页端通过 seq 增量拉取，从而保证「网页端看到的内容与宿主 app 相同」。
        private readonly object historyLock = new object();
        private readonly List<HistoryItem> history = new List<HistoryItem>();
        private long messageSeq;
        private const int MaxHistory = 500;

        // ===== 网络状态感知 =====
        // 设备网络类型：决定是否需要扫描局域网。
        //  - Mobile：移动数据上网，禁止扫描（既无意义也会消耗流量/暴露）
        //  - Wifi：连接了 WiFi，扫描 WiFi 所在局域网
        //  - Hotspot：本机开启了热点，扫描热点所在局域网（通常是 192.168.43.x）
        //  - None：无任何可用数据网络
        private enum NetType { None, Mobile, Wifi, Hotspot }

        private ConnectivityManager connMgr;
        private ConnectivityManager.NetworkCallback netCallback;
        private NetType currentNetType = NetType.None;
        // 是否在“可扫描”的网络上（Wifi 或 Hotspot）。移动数据/无网络时为 false。
        private volatile bool scanEnabled;
        // 网络变化后的立即扫描是否已安排
        private readonly object netLock = new object();
        // discoverySocket 当前绑定的本机源地址（用于把组播从正确的网卡发出）。
        private IPAddress discoveryBindIp = IPAddress.Any;
        private int discoveryGen;

        // 访客表：浏览器 IP -> 访客身份。同一 IP 复用同一账号，不同 IP 即不同访客。
        private readonly ConcurrentDictionary<string, Guest> guests = new ConcurrentDictionary<string, Guest>();

        /// <summary>收到「无前缀普通文本消息」时触发，宿主用来写入系统剪贴板。</summary>
        public event Action<string> PlainTextReceived;

        // ===== Mchat 频道「设备常驻状态」=====
        /// <summary>设备状态帧（电量/IP/设备名）：只更新状态条，不进聊天记录、不渲染气泡。</summary>
        public const string DevStatKind = "devstat";
        /// <summary>设备请求帧（目前用于「请求对端定位」）。</summary>
        public const string DevReqKind = "devreq";
        /// <summary>
        /// GPS 共享专用隐藏渠道：走频道的多链路可靠出站队列（含重试/跨链路去重），
        /// 但既不落聊天历史也不推消息气泡，仅更新雷达定位缓存。
        /// </summary>
        public const string GpsShareKind = "gpsshare";
        public const string GpsShareConv = "chan:__gpsshare__";

        /// <summary>收到其它设备的状态帧时触发，供聊天界面刷新顶部状态条。</summary>
        public event Action DevicePresenceChanged;

        /// <summary>频道内其它设备的最新状态（peerId → {name, ip, battery, ...}）。</summary>
        private readonly Dictionary<string, Dictionary<string, object>> devicePresence =
            new Dictionary<string, Dictionary<string, object>>();
        private readonly object devicePresenceLock = new object();

        /// <summary>本机最近一次上报的定位文本（"HH:mm:ss-纬度,经度"）。</summary>
        public string LastLocationText { get; private set; }

        /// <summary>功能控制执行器（收到 (命令) 消息时模拟对应输入）。</summary>
        public ControlExecutor Control { get; set; }

        public MchatBridge(Context context, Android.Webkit.WebView webView = null)
        {
            this.context = context;
            this.webView = webView;
            this.mainHandler = new Handler(context.MainLooper);

            mediaDir = Path.Combine(context.FilesDir.AbsolutePath, "media");
            Directory.CreateDirectory(mediaDir);

            // 身份先于 peerId：qaul/bitchat 的指纹都由密钥推导，Mchat 的 peerId 也由它派生
            identity = new IdentityStore(context);
            peerId = identity.Current.MchatId;
            // 兜底：peerId 为空会让「id == peerId」恒真，所有局域网发现包都被当成本机丢弃，
            // 结果是永远发现不到任何设备、发消息报「暂无对端」。这里强制修好并落一条日志。
            if (string.IsNullOrEmpty(peerId))
            {
                var fb = identity?.Current?.BitchatPeerId;
                peerId = !string.IsNullOrEmpty(fb) && fb.Length >= 12
                    ? fb.Substring(0, 12)
                    : Guid.NewGuid().ToString("N").Substring(0, 12);
                Android.Util.Log.Warn("Mchat", "peerId 为空，已回退生成：" + peerId);
            }
            Android.Util.Log.Info("Mchat", "本机 peerId=" + peerId);
            displayName = LoadDisplayName();
            identity.Changed += OnIdentityChanged;

            // 初始化统一去重引擎
            _dedupEngine = new DeduplicationEngine(this);

            // 会话仓库：房间/私聊消息、删除墓碑、未读数。
            // persist:false → 即时通信模式：消息不落盘，启动即清空、进程退出即清空
            // （「不保存消息、每次启动/关闭都清除消息」的设计）；内存去重/会话索引照常。
            store = new ChatStore(Path.Combine(context.FilesDir.AbsolutePath, "chat"), persist: false);
            store.Load();
            store.SetSelfPredicate(m => Msg.Str(m, "from") == peerId);
            store.Changed += () =>
            {
                SafeRun("Mchat", () => ConversationsChanged?.Invoke());
            };
            // 默认进入统一频道（#mesh），它默认只监听 qaul 链路
            activeConv = Convs.Chan(ChannelRegistry.DefaultChannelName);
            MigrateLegacyRoomToChannel();

            // 双渠道默认开启：同一消息同时走 qaul 消息端口与 Mchat 原有 TCP 渠道，
            // 既保证新协议互通，也保持对老版本 Mchat 的兼容。
            DualChannel = LoadDualChannelPref();

            // qaul 消息端口：对等直连，收发 qaul 原生 chat 帧
            qaul = new QaulChannel();
            qaul.OnMessage = OnQaulMessage;
            // QaulChannel 内部已直接写 logcat，这里不再重复转发，避免同一行日志出现两次
            qaul.OnLog = null;

            // 频道注册表：默认频道 mesh，只监听 qaul
            channels = new ChannelRegistry(context);

            // 出站队列：所有「发出去要保证尽量送到」的报文都先排到这里，
            // 由单线程按链路令牌桶限速发送，失败按 1s/3s/7s/15s 退避重传。
            // 参考 qaul 的未确认表 + 重传循环（services/messaging/retransmit.rs）。
            outbox = new MeshOutbox(mesh);
            outbox.Send = TrySendOneLink;

            // WLAN Direct：默认关闭，由菜单或频道链路按需开启
            p2p = new WlanP2pChannel(context);
            p2p.HelloProvider = P2pHelloJson;
            p2p.OnLog = m => Android.Util.Log.Info("WlanP2p", m);
            p2p.OnPeersChanged = () => Emit("p2p", GetP2pStatus());
            p2p.OnGroupChanged = ok => Emit("p2p", GetP2pStatus());

            voiceCall = new VoiceCallManager(context, this);

            // 定位消息文本：设备名 / IP|电量 / 日期 / 纬度,经度（设备信息只有这里拿得到）
            LocationReporter.Formatter = BuildLocationText;

            Current = this;

            // 局域网音乐共享：本机一播歌就往 mesh 频道发卡片 + 广播状态给其它设备
            SafeRun("Mchat", () => AhoChatA.Music.MusicCast.Init(context));
        }

        /// <summary>
        /// 定位消息正文格式（四行）：
        /// <code>
        /// redmi k20
        /// 192.168.1.101|电量:55%
        /// 2026年10月31日
        /// 23.1231,140.3138
        /// </code>
        /// </summary>
        public static string BuildLocationText(DateTime when, double lat, double lon)
        {
            string ip = "0.0.0.0";
            int bat = -1;
            string name = "Android";
            SafeRun("Mchat", () => name = DeviceStatus.DeviceName);
            SafeRun("Mchat", () => ip = DeviceStatus.Ip);
            SafeRun("Mchat", () => bat = DeviceStatus.Battery);

            var batText = bat >= 0 ? (L10n.T("电量:") + bat + "%") : L10n.T("电量:未知");
            return name + "\n"
                 + ip + "|" + batText + "\n"
                 + when.ToString(L10n.T("yyyy年M月d日"), System.Globalization.CultureInfo.InvariantCulture) + "\n"
                 + lat.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ","
                 + lon.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把一条「系统通知」作为普通聊天消息直接自动广播出去。
        /// text 需调用方按 "应用名称:消息标题:消息内容" 格式拼好。
        /// 通过复用 SendMessage 的内部逻辑，确保写入历史并被局域网广播。
        /// 即便 app 在后台，SendMessage 的广播线程仍在工作（前台服务保活）。
        /// </summary>
        public void SendPlainText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var msg = new Dictionary<string, object>
                {
                    { "id", Guid.NewGuid().ToString("N") },
                    { "conv", activeConv },
                    { "kind", "text" },
                    { "text", text },
                    { "type", "notification" },
                    { "from", peerId },
                    { "fromName", displayName },
                    { "time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                store.Add(msg, true);
                AppendHistory(msg);
                Emit("message", msg);
                Deliver(msg, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", $"发送通知消息失败: {ex}");
            }
        }

        /// <summary>媒体文件落地目录，MainActivity 拦截 /__media/ 时使用。</summary>
        public string MediaDirectory => mediaDir;

        /// <summary>本机展示名，网页端会显示「正在连接 xxx」。</summary>
        public string SelfName => displayName;

        /// <summary>本机 peerId。</summary>
        public string SelfPeerId => peerId;

        #region 身份持久化

        /// <summary>
        /// 旧版随机 peerId 的迁移入口：IdentityStore 会在首次生成身份时读取 prefs 里的
        /// `peerId` 并沿用，保证升级后不会被对端当成"新设备"。
        /// 新安装用户由签名公钥派生，换指纹时会整体更换。
        /// </summary>
        private string LoadOrCreatePeerId()
        {
            var prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
            var id = prefs.GetString("peerId", null);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 12);
                prefs.Edit().PutString("peerId", id).Apply();
            }
            return id;
        }

        private string LoadDisplayName()
        {
            var prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
            var name = prefs.GetString("displayName", null);
            if (string.IsNullOrEmpty(name))
            {
                name = string.IsNullOrEmpty(Build.Model) ? L10n.T("设备") : Build.Model;
                prefs.Edit().PutString("displayName", name).Apply();
            }
            return name;
        }

        #endregion

        #region JS 事件推送

        private void RunJs(string script)
        {
            if (webView == null) return;   // 原生界面模式：没有 WebView，事件走 NativeEvent
            mainHandler.Post(() =>
            {
                try { webView.EvaluateJavascript(script, null); }
                catch (Exception ex) { Android.Util.Log.Error("Mchat", $"EvaluateJavascript 失败: {ex.Message}"); }
            });
        }

        /// <summary>网页端模式进入时挂载 WebView；原生界面模式传 null 解绑。</summary>
        public void AttachWebView(Android.Webkit.WebView view)
        {
            webView = view;
            if (view != null)
            {
                SafeRun("Mchat", () => view.AddJavascriptInterface(this, "AndroidMchat"));
            }
        }

        /// <summary>向 JS 派发事件：window.__lanEvent(type, payloadJson)</summary>
        internal void Emit(string type, object payload)
        {
            // 同步听：不管卡片是「新消息」还是「切歌 patch」，都在这里统一喂给
            // 播放器同步器，避免依赖聊天界面是否正好把这张卡片渲染出来。
            SafeRun("Mchat", () =>
            {
                if ((type == "message" || type == "msgupd") && payload is IDictionary<string, object> mc
                    && Msg.Str(mc, "kind") == "musiccard")
                    SyncListen.OnCard(mc);
            });

            // 原生界面订阅者（App 端不再依赖网页，但仍然复用同一套事件语义）
            SafeRun("Mchat", () => NativeEvent?.Invoke(type, payload));

            if (webView == null) return;
            string typeLit = JsonConvert.SerializeObject(type);
            string payloadLit = JsonConvert.SerializeObject(JsonConvert.SerializeObject(payload));
            RunJs($"window.__lanEvent && window.__lanEvent({typeLit}, {payloadLit});");
        }

        /// <summary>
        /// 把指令交给前端决策并执行：
        /// 若本机正在用 App 内置播放器播放，则直接控制它（window.Media.remoteControl），
        /// 否则由前端回调 execSystemControl 交给 ControlExecutor 控制系统其它播放器。
        ///
        /// 这样处理的必要性：内置播放器（audio 元素）不会注册成系统原生 MediaSession，
        /// 若一律走 ControlExecutor 只会作用于外部播放器/假会话，导致切不了歌。
        /// </summary>
        private void MaybeExecControl(Dictionary<string, object> msg)
        {
            if (msg == null || Control == null) return;

            string cmd = null;
            // 新格式：kind=control 直接带 cmd 字段
            if (msg.TryGetValue("cmd", out var cv) && cv != null)
                cmd = cv.ToString();
            // 旧格式兼容：整条被圆括号包裹的文本 (命令)
            else if (msg.TryGetValue("text", out var tv) && tv != null)
                cmd = ControlExecutor.ExtractCommand(tv.ToString());

            if (string.IsNullOrEmpty(cmd)) return;
            RunControlCommand(cmd);
        }

        /// <summary>
        /// 执行一条媒体/功能控制指令（悬浮窗、通知栏、局域网指令共用）：
        /// 优先交给前端（本机正在用内置播放器时真正切歌/暂停），
        /// 否则回退 ControlExecutor 控制外部播放器 / 系统音量。
        /// </summary>
        public void RunControlCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;

            // 原生界面模式（无 WebView）：直接执行，不依赖任何前端
            if (webView == null)
            {
                SafeRun("Mchat", () => Control?.Exec(cmd));
                return;
            }

            var script = "(function(){try{if(window.MchatRemoteExec){" +
                         "window.MchatRemoteExec(" + JsonConvert.SerializeObject(cmd) +
                         ");return;}}catch(e){console.warn(e);}" +
                         "try{AndroidMchat.execSystemControl(" + JsonConvert.SerializeObject(cmd) + ");}catch(e){}})();";
            RunJs(script);
        }

        /// <summary>悬浮窗专用：WebView 不可用（App 已退出）时直接走原生控制。</summary>
        public void RunMediaControl(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            if (webView == null)
            {
                SafeRun("Mchat", () => Control?.Exec(cmd));
                return;
            }
            RunControlCommand(cmd);
        }

        /// <summary>
        /// 供前端回调：确认本机内置播放器未在使用时，把指令交给 ControlExecutor
        /// 控制外部播放器 / 系统音量。
        /// </summary>
        [Java.Interop.Export("execSystemControl")]
        [JavascriptInterface]
        public void ExecSystemControl(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            try { Control?.Exec(cmd); }
            catch (Exception ex) { Android.Util.Log.Error("Mchat", $"系统控制执行失败: {ex}"); }
        }

        #endregion

        #region 供 VoiceCallManager 调用的内部支撑

        /// <summary>在 UI 线程安全地执行 JS（供语音通话管理器推送状态）。</summary>
        internal void RunJsSafe(string script) => RunJs(script);

        /// <summary>把一条通话信令字典广播到局域网（复用既有 TCP 广播通道）。</summary>
        internal void SendCallSignalRaw(Dictionary<string, object> sig)
        {
            try
            {
                var payload = JsonConvert.SerializeObject(sig);
                Task.Run(() => BroadcastFrame(payload, null));
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"发送通话信令失败: {ex.Message}");
            }
        }

        /// <summary>把 peerId 解析为局域网 IP（供语音通话建立 UDP 媒体通道）。</summary>
        internal string ResolvePeerIp(string peerId)
        {
            if (peers.TryGetValue(peerId, out var p))
                return p.Address;
            return null;
        }

        /// <summary>
        /// 手动直连：自动发现被路由器隔离（AP 隔离 / 跨网段 / 公网）时，直接向对方发握手。
        /// 支持 IPv4 / IPv6 / 域名，端口可自定义。地址会被持久化为「中转站」，
        /// 之后每次广播都会顺带投递给它，由它中继给其它设备。
        /// </summary>
        public bool ConnectToIp(string host, int port)
        {
            try
            {
                return AddStaticRelay(host, port);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "手动直连失败: " + ex.Message);
                return false;
            }
        }

        #region 静态中转站（IPv4 / IPv6 / 自定义端口）

        /// <summary>去掉 IPv6 方括号，返回可解析的主机名/地址。</summary>
        private static string NormalizeHost(string host)
        {
            var h = (host ?? "").Trim();
            if (h.Length == 0) return "";
            if (h[0] == '[')
            {
                int close = h.IndexOf(']');
                if (close > 0) return h.Substring(1, close - 1);
            }
            return h;
        }

        /// <summary>把主机字面量解析为 IP（支持 "[fd00::1]" 这类带方括号的 IPv6）。</summary>
        private static bool TryParseIp(string host, out IPAddress ip)
        {
            ip = null;
            var h = NormalizeHost(host);
            if (h.Length == 0) return false;
            return IPAddress.TryParse(h, out ip);
        }

        /// <summary>
        /// 有界域名解析：后台线程执行，调用方最多等 timeoutMs；结果短缓存，失败也缓存，
        /// 避免断网时每个出站包都重复吃一次几十秒的 DNS 超时。
        /// </summary>
        public static IPAddress[] ResolveHostBounded(string host, int timeoutMs = DnsDefaultTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host");
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return new[] { literal };

            var key = NormalizeHost(host.Trim()).ToLowerInvariant();
            int now = SysEnv.TickCount;
            Tuple<int, IPAddress[]> cached;
            if (DnsResults.TryGetValue(key, out cached) && cached.Item2 != null)
            {
                int age = unchecked(now - cached.Item1);
                int ttl = cached.Item2.Length == 0 ? DnsFailCacheMs : DnsOkCacheMs;
                if (age >= 0 && age < ttl) return cached.Item2;
            }

            var task = Task.Run(() =>
            {
                DnsSlots.Wait();
                try { return System.Net.Dns.GetHostEntry(key).AddressList; }
                finally { DnsSlots.Release(); }
            });

            IPAddress[] addrs;
            try
            {
                if (!task.Wait(timeoutMs))
                {
                    DnsResults[key] = Tuple.Create(SysEnv.TickCount, new IPAddress[0]);
                    throw new TimeoutException("域名解析超时: " + host);
                }
                addrs = task.Result;
            }
            catch (AggregateException ex)
            {
                DnsResults[key] = Tuple.Create(SysEnv.TickCount, new IPAddress[0]);
                throw ex.InnerException ?? ex;
            }
            catch
            {
                DnsResults[key] = Tuple.Create(SysEnv.TickCount, new IPAddress[0]);
                throw;
            }
            if (addrs == null || addrs.Length == 0)
            {
                DnsResults[key] = Tuple.Create(SysEnv.TickCount, new IPAddress[0]);
                return new IPAddress[0];
            }
            DnsResults[key] = Tuple.Create(SysEnv.TickCount, addrs);
            return addrs;
        }

        /// <summary>按地址族建立 TCP 连接（IPv4 / IPv6 都支持；域名会同时尝试 A/AAAA）。</summary>
        private static TcpClient ConnectTcp(string host, int port, int timeoutMs)
        {
            IPAddress ip;
            if (TryParseIp(host, out ip))
            {
                var c = new TcpClient(ip.AddressFamily);
                try
                {
                    if (!c.ConnectAsync(ip, port).Wait(timeoutMs)) throw new TimeoutException(L10n.T("连接超时"));
                    return c;
                }
                catch
                {
                    SafeRun("Mchat", () => c.Close());
                    throw;
                }
            }

            // 域名：解析出 A/AAAA 后逐个尝试（IPv6 优先，回退 IPv4）。
            // 绝不能在主线程裸调 Dns.GetHostEntry：断网时它会卡 20~40 秒直接触发 ANR。
            var addrs = ResolveHostBounded(host, Math.Min(timeoutMs, DnsDefaultTimeoutMs));
            Exception last = null;
            foreach (var a in addrs)
            {
                var c = new TcpClient(a.AddressFamily);
                try
                {
                    if (!c.ConnectAsync(a, port).Wait(timeoutMs))
                    {
                        try { c.Close(); } catch { }
                        continue;
                    }
                    return c;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { c.Close(); } catch { }
                }
            }
            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }

        /// <summary>
        /// 解析用户输入的主机/端口，兼容三种写法：
        ///   "1.2.3.4"、"1.2.3.4:9200"、"[fd00::1]"、"[fd00::1]:9200"、纯 IPv6 "fd00::1"。
        /// 端口写在输入里时通过 out port 返回（0 表示未指定）。
        /// </summary>
        public static bool ParseHostPort(string input, out string host, out int port)
        {
            host = ""; port = 0;
            var s = (input ?? "").Trim();
            if (s.Length == 0) return false;

            if (s[0] == '[')
            {
                int close = s.IndexOf(']');
                if (close > 0)
                {
                    host = s.Substring(1, close - 1).Trim();
                    if (close + 1 < s.Length && s[close + 1] == ':')
                        int.TryParse(s.Substring(close + 2).Trim(), out port);
                    return host.Length > 0;
                }
            }

            int first = s.IndexOf(':');
            // 只有一个冒号 → host:port（IPv4 / 域名）；多个冒号是 IPv6 字面量，整体作为主机
            if (first > 0 && first == s.LastIndexOf(':'))
            {
                host = s.Substring(0, first).Trim();
                int.TryParse(s.Substring(first + 1).Trim(), out port);
                return host.Length > 0;
            }

            host = s;
            return true;
        }

        /// <summary>仅格式化 IP：IPv6 加方括号（便于与端口拼接展示）。</summary>
        private static string FormatIpOnly(string addr)
        {
            IPAddress ip;
            if (TryParseIp(addr, out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                return "[" + ip + "]";
            return addr;
        }

        /// <summary>host:port 的可读形式：IPv6 自动加方括号，便于界面展示与复制。</summary>
        public static string FormatHostPort(string host, int port)
        {
            var h = NormalizeHost(host);
            IPAddress ip;
            if (TryParseIp(h, out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                h = "[" + ip + "]";
            return port > 0 ? h + ":" + port : h;
        }

        /// <summary>向某个地址发 hello + probe（建立/刷新登记）。IPv6 与自定义端口都走这里。</summary>
        private void SendHandshake(string host, int port)
        {
            var peer = new Peer
            {
                Id = "",
                Name = "",
                Address = NormalizeHost(host),
                Port = port > 0 ? port : DefaultTcpPort,
                LastSeen = SysEnv.TickCount
            };
            var hello = new Dictionary<string, object>
            {
                ["t"] = "hello",
                ["id"] = peerId,
                ["name"] = displayName,
                ["port"] = tcpPort,
                ["qp"] = qaul != null ? qaul.Port : 0,
                ["rd"] = RdHostServer.AdvertisedPort,
                ["dc"] = RdHostServer.AdvertisedCode
            };
            SendFrameTo(peer, JsonConvert.SerializeObject(hello), null);
            hello["t"] = "probe";
            try { SendFrameTo(peer, JsonConvert.SerializeObject(hello), null); } catch { }
            Android.Util.Log.Info("Mchat", "握手中转站 → " + FormatHostPort(peer.Address, peer.Port));
        }

        /// <summary>添加（并持久化）一个中转站地址。支持 IPv4 / IPv6 / 域名与自定义端口。</summary>
        public bool AddStaticRelay(string host, int port)
        {
            string h; int inlinePort;
            if (!ParseHostPort(host, out h, out inlinePort)) return false;
            h = NormalizeHost(h);
            if (string.IsNullOrEmpty(h)) return false;
            if (port <= 0) port = inlinePort;
            if (port <= 0) port = DefaultTcpPort;

            lock (staticRelays)
            {
                foreach (var r in staticRelays)
                    if (string.Equals(r.Host, h, StringComparison.OrdinalIgnoreCase) && r.Port == port)
                        return true;
                staticRelays.Add(new RelayEndpoint { Host = h, Port = port });
            }
            SaveStaticRelays();
            // 配置中继站只是新增一条联网通信渠道，本地局域网发现与聊天保持不变
            try { ApplyRelayModeRestrictions(); } catch { }
            // 握手要建 TCP 连接，放到后台，避免卡住调用方（多为 UI 线程）
            Task.Run(() =>
            {
                try { SendHandshake(h, port); }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "中转站握手失败: " + ex.Message); }
            });
            return true;
        }

        /// <summary>移除一个中转站。</summary>
        public bool RemoveStaticRelay(string host, int port)
        {
            var h = NormalizeHost(host);
            if (string.IsNullOrEmpty(h)) return false;
            bool removed = false;
            lock (staticRelays)
                removed = staticRelays.RemoveAll(r =>
                    string.Equals(r.Host, h, StringComparison.OrdinalIgnoreCase) && r.Port == port) > 0;
            if (removed) SaveStaticRelays();
            return removed;
        }

        /// <summary>当前配置的中转站（"host:port"，IPv6 带方括号）。</summary>
        public List<string> GetStaticRelays()
        {
            var list = new List<string>();
            lock (staticRelays)
                foreach (var r in staticRelays) list.Add(FormatHostPort(r.Host, r.Port));
            return list;
        }

        private void SaveStaticRelays()
        {
            try
            {
                List<RelayEndpoint> copy;
                lock (staticRelays) copy = new List<RelayEndpoint>(staticRelays);
                context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                       .Edit().PutString(PrefStaticRelays, JsonConvert.SerializeObject(copy)).Apply();
            }
            catch { }
        }

        private void LoadStaticRelays()
        {
            try
            {
                var raw = context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                                 .GetString(PrefStaticRelays, "");
                if (string.IsNullOrWhiteSpace(raw)) return;
                var list = JsonConvert.DeserializeObject<List<RelayEndpoint>>(raw);
                if (list == null) return;
                lock (staticRelays)
                {
                    foreach (var r in list)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Host)) continue;
                        if (r.Port <= 0) r.Port = DefaultTcpPort;
                        if (!staticRelays.Exists(x => string.Equals(x.Host, r.Host, StringComparison.OrdinalIgnoreCase)
                                                      && x.Port == r.Port))
                            staticRelays.Add(r);
                    }
                }
            }
            catch { }
            // 启动时若已有中继站，记录联网通信渠道状态（不影响本地发现）
            try { ApplyRelayModeRestrictions(); } catch { }
        }

        /// <summary>周期重发握手，保证中转站重启 / 链路中断后能自动恢复。</summary>
        private void HandshakeRelays()
        {
            // qaul 节点发生过中继转发时也记录联网通信渠道状态
            try { ApplyRelayModeRestrictions(); } catch { }
            List<RelayEndpoint> copy;
            lock (staticRelays) copy = new List<RelayEndpoint>(staticRelays);
            foreach (var r in copy)
            {
                try { SendHandshake(r.Host, r.Port); } catch { }
            }
        }

        /// <summary>中转站保活：每 8s 重发一次握手（对端节点表 20s 超时，8s 足够稳），
        /// 保证对端重启 / 链路中断 / 切网后都能自动恢复。</summary>
        private void StartRelayKeepalive(CancellationToken token)
        {
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(8000, token).ConfigureAwait(false); }
                    catch { break; }

                    if (Interlocked.CompareExchange(ref relayHandshakeBusy, 1, 0) != 0) continue;
                    try { HandshakeRelays(); }
                    catch { }
                    finally { Interlocked.Exchange(ref relayHandshakeBusy, 0); }
                }
            }, token);
        }

        /* ---------- 本机监听端口（可自定义；中转站常需固定端口） ---------- */

        /// <summary>本机当前实际监听的 TCP 端口（入站口）。</summary>
        public int LocalTcpPort { get { return tcpPort; } }

        /// <summary>用户指定的监听端口；0 表示自动从默认段挑选。</summary>
        public int PreferredTcpPort
        {
            get
            {
                try
                {
                    return context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                                  .GetInt(PrefTcpPort, 0);
                }
                catch { return 0; }
            }
        }

        /// <summary>设置本机监听端口（0=自动）。需重启传输/App 后生效。</summary>
        public bool SetPreferredTcpPort(int port)
        {
            if (port != 0 && (port < 1024 || port > 65535)) return false;
            try
            {
                context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                       .Edit().PutInt(PrefTcpPort, port).Apply();
                return true;
            }
            catch { return false; }
        }

        /// <summary>尝试在指定端口建立双栈（IPv4+IPv6）监听，失败返回 null。</summary>
        private static TcpListener TryBindPort(int p)
        {
            // 优先双栈监听（IPv6Any + DualMode）：同一端口同时接受 IPv4 与 IPv6，
            // 这样本机既能被 IPv6 中转站/对端连入，原有 IPv4 局域网也完全不受影响。
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    var l6 = new TcpListener(IPAddress.IPv6Any, p);
                    l6.Server.DualMode = true;
                    l6.Start();
                    return l6;
                }
                catch { }
            }
            // IPv6 不可用或双栈失败 → 回退纯 IPv4 监听
            try
            {
                var l4 = new TcpListener(IPAddress.Any, p);
                l4.Start();
                return l4;
            }
            catch (SocketException) { return null; }
        }

        #endregion

        /// <summary>原生通话模块（语音 / 视频），供界面直接驱动。</summary>
        public VoiceCallManager VoiceCall { get { return voiceCall; } }

        /// <summary>直接发起通话（原生界面用）。video=true 时同时开视频通道。</summary>
        public void StartCall(string peerId, string name, bool video)
        {
            try { voiceCall.Invite(peerId, name, video ? CallMode.Video : CallMode.Audio); }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "发起通话失败: " + ex.Message); }
        }

        /// <summary>麦克风权限已授予后通知语音通话模块。</summary>
        internal void NotifyMicGranted() => voiceCall.OnMicPermissionGranted();

        #endregion

        #region JS 接口

        [Java.Interop.Export("getSelfInfo")]
        [JavascriptInterface]
        public string GetSelfInfo()
        {
            return JsonConvert.SerializeObject(new
            {
                peerId,
                name = displayName,
                port = tcpPort,
                started
            });
        }

        [Java.Interop.Export("setDisplayName")]
        [JavascriptInterface]
        public void SetDisplayName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            displayName = name.Trim();
            context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                   .Edit().PutString("displayName", displayName).Apply();
        }

        [Java.Interop.Export("getPeers")]
        [JavascriptInterface]
        public string GetPeers()
        {
            var now = SysEnv.TickCount;
            var list = peers.Values
                .Where(p => now - p.LastSeen < PeerTimeoutMs)
                .Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    ip = p.Address,
                    port = p.Port,
                    qp = p.QaulPort,
                    bitchat = p.BitchatId,
                    qaul = p.QaulKeyShort
                })
                .ToList();
            return JsonConvert.SerializeObject(list);
        }

        /// <summary>返回当前网络类型与是否正在扫描，供前端展示状态。</summary>
        [Java.Interop.Export("getNetworkState")]
        [JavascriptInterface]
        public string GetNetworkState()
        {
            return JsonConvert.SerializeObject(new { type = currentNetType.ToString(), scan = scanEnabled });
        }

        /// <summary>
        /// 发送一条消息。messageJson 为完整消息对象（含 id/kind/text 等）；
        /// attachmentBase64 非空时表示随附二进制媒体，将被写入本地并同步给对端。
        /// </summary>
        [Java.Interop.Export("sendMessage")]
        [JavascriptInterface]
        public string SendMessage(string messageJson, string attachmentBase64, string attachmentName)
        {
            try
            {
                var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(messageJson)
                          ?? new Dictionary<string, object>();

                if (!msg.ContainsKey("id") || string.IsNullOrEmpty(msg["id"]?.ToString()))
                    msg["id"] = Guid.NewGuid().ToString("N");
                msg["from"] = peerId;
                msg["fromName"] = displayName;
                msg["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // 会话归属：前端可显式指定，未指定时落到当前会话
                var jsConv = msg.TryGetValue("conv", out var cv) ? cv?.ToString() : null;
                if (string.IsNullOrEmpty(jsConv)) jsConv = activeConv;
                msg["conv"] = jsConv;

                byte[] attachment = null;
                if (!string.IsNullOrEmpty(attachmentBase64))
                {
                    // 文件传输统一走 qaul：仅 qaul 会话支持；其余会话（房间/普通私聊）只发文本与指令。
                    if (Convs.IsQaulDm(jsConv) || Convs.IsQaulGroup(jsConv) || Convs.IsChan(jsConv))
                    {
                        attachment = Convert.FromBase64String(attachmentBase64);
                        var m = SendQaulFileCore(jsConv, attachmentName, attachment, "");
                        if (m == null) { Emit("error", new { message = "qaul 发送失败" }); return null; }
                        return JsonConvert.SerializeObject(m);
                    }
                    Emit("error", new { message = L10n.T("文件传输仅支持 qaul 会话（qaul 私聊 / qaul 群 / 频道）") });
                    return null;
                }

                _dedupEngine.MarkSeen(msg["id"].ToString());

                // 跨设备媒体同步消息不进聊天历史：它们每 10 秒一条，
                // 写进历史会迅速淹没真实聊天内容，也会污染网页端的历史展示。
                var outKind = msg.TryGetValue("kind", out var kindVal) ? kindVal?.ToString() : null;
                if (outKind != "mediaplayer" && outKind != "mediacontrol")
                {
                    store.Add(msg, true);
                    AppendHistory(msg);
                }

                var payload = JsonConvert.SerializeObject(msg);
                Deliver(msg, attachment);

                // 网页端自己已经本地渲染了这条消息，只把事件推给原生界面，避免重复气泡
                try { if (webView != null) NativeEvent?.Invoke("message", msg); } catch { }


                return payload;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", $"发送失败: {ex}");
                Emit("error", new { message = ex.Message });
                return null;
            }
        }

        /* ===================== 频道里的「设备常驻状态」 ===================== */

        /// <summary>已加入的 Mchat 频道会话（chan:xxx）。</summary>
        public List<string> MchatChannelConvs()
        {
            var list = new List<string>();
            try
            {
                foreach (var ch in channels.Joined())
                    if (ch != null && !string.IsNullOrEmpty(ch.Conv)) list.Add(ch.Conv);
            }
            catch { }
            if (list.Count == 0) list.Add(Convs.Chan(Convs.DefaultRoom));
            return list;
        }

        /// <summary>
        /// 某台设备当前**所有**可用接入链路（逗号分隔，可多选）。
        ///
        /// 以前设备栏只认「最后一条心跳是从哪条链路来的」，于是同一台设备
        /// 「蓝牙 + 局域网」同时在线时只显示一个图标，切换链路还会闪变。
        /// 这里改为实时汇总各链路的在线表：P2P 看 p2pNodes、局域网看 peers、
        /// 蓝牙看 Bitchat 已连接邻居、qaul 按公钥短指纹比对。
        /// </summary>
        public string GetDeviceLinks(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return "";
            var links = new List<string>();
            try
            {
                Peer node = null;
                p2pNodes.TryGetValue(peerId, out node);
                if (node != null) links.Add(ChanLink.P2p);       // WLAN Direct 对端

                Peer lan = null;
                if (peers.TryGetValue(peerId, out lan) && lan != null && !lan.IsP2p
                    && SysEnv.TickCount - lan.LastSeen < PeerTimeoutMs)
                    links.Add(ChanLink.Lan);                     // 同 Wi-Fi 局域网

                if (!links.Contains(ChanLink.Bitchat))
                {
                    // 蓝牙邻里的标识是 bitchat peerID（16 hex），与 Mchat peer id 不同域，
                    // 必须经 BitchatIdMap 映射后再比，否则「同一台设备的蓝牙链路」认不出来，
                    // 设备栏就只显示一个图标（多链路多图标失效）。
                    var btMap = BitchatIdMap();
                    foreach (var n in BitchatNeighborList())
                    {
                        if (n == null || !n.Connected) continue;
                        var key = string.IsNullOrEmpty(n.PeerId) ? n.Address : n.PeerId;
                        var mapped = btMap.TryGetValue(key, out var lanId) ? lanId : key;
                        if (mapped == peerId || key == peerId) { links.Add(ChanLink.Bitchat); break; }
                    }
                }

                // qaul：公钥短指纹比对（多网卡 / 地址漂移时 IP 会漏，指纹不会）
                var ks = node != null ? node.QaulKeyShort : (lan != null ? lan.QaulKeyShort : null);
                if (!string.IsNullOrEmpty(ks) && qnode != null)
                {
                    foreach (var n in qnode.Neighbours())
                    {
                        object op;
                        if (!n.TryGetValue("node", out op)) continue;
                        if (QaulKeyShortOf(op as string) == ks) { links.Add(ChanLink.Qaul); break; }
                    }
                }
            }
            catch { }
            return string.Join(",", links);
        }

        /// <summary>频道内其它设备的最新状态（供顶部状态条渲染）。</summary>
        public List<Dictionary<string, object>> GetDevicePresence()
        {
            var list = new List<Dictionary<string, object>>();
            lock (devicePresenceLock)
            {
                // 复制一份再加工：不能持锁调用 GetDeviceLinks（内部会取蓝牙/qaul 的锁）
                foreach (var kv in devicePresence) list.Add(new Dictionary<string, object>(kv.Value));
            }
            // 实时补上「这台设备当前的所有链路」，UI 据此显示多个连接方式图标
            foreach (var d in list)
            {
                string live = "";
                try { live = GetDeviceLinks(Msg.Str(d, "peer")); } catch { }
                // 一个都没探到时退回心跳来源链路（中继设备不直连，但仍要有个图标）
                d["links"] = string.IsNullOrEmpty(live) ? Msg.Str(d, "link") : live;
            }
            // 电量从低到高排一下，方便一眼看到快没电的设备
            list.Sort((a, b) => Msg.Num(a, "battery", 999).CompareTo(Msg.Num(b, "battery", 999)));
            return list;
        }

        /// <summary>清掉某个对端的状态（对方下线/重新上线时用）。</summary>
        public void ForgetDevicePresence(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return;
            lock (devicePresenceLock) devicePresence.Remove(peer);
            try { DevicePresenceChanged?.Invoke(); } catch { }
        }

        /// <summary>
        /// 广播本机状态（设备名 / IP / 电量）到 Mchat 频道。
        /// devstat 帧只用来刷新各端顶部的常驻状态条，不写聊天记录、不产生气泡。
        /// </summary>
        public void BroadcastDeviceStatus(string conv = null)
        {
            var targets = string.IsNullOrEmpty(conv) ? MchatChannelConvs() : new List<string> { conv };
            var battery = DeviceStatus.Battery;
            foreach (var c in targets)
            {
                var m = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = c,
                    ["kind"] = DevStatKind,
                    ["text"] = "",
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["dev"] = DeviceStatus.DeviceName,
                    ["ip"] = DeviceStatus.Ip,
                    ["battery"] = battery,
                    ["charging"] = DeviceStatus.Charging,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(Msg.Str(m, "id"));
                Deliver(m, null);
            }
        }

        /// <summary>
        /// 向频道内其它设备发起「定位请求」；对端收到后会自动开始定位，
        /// 并以 1 秒间隔**连续回传 durationMs 毫秒**（默认 5 秒），到点自动停止。
        /// </summary>
        /// <param name="conv">目标频道；空则用当前会话。</param>
        /// <param name="targetPeer">只让指定设备响应（对端 peerId）；空则频道内所有设备都响应。</param>
        /// <param name="durationMs">连续发送时长（毫秒）。</param>
        public bool RequestPeerLocation(string conv = null, string targetPeer = null, int durationMs = 5000)
        {
            var target = string.IsNullOrEmpty(conv) ? activeConv : conv;
            if (!Convs.IsChan(target))
            {
                var list = MchatChannelConvs();
                target = list.Count > 0 ? list[0] : target;
            }
            if (string.IsNullOrEmpty(target)) return false;

            if (durationMs <= 0) durationMs = 5000;

            var m = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["conv"] = target,
                ["kind"] = DevReqKind,
                ["req"] = "gps",
                ["dur"] = durationMs,
                ["text"] = "",
                ["from"] = peerId,
                ["fromName"] = displayName,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (!string.IsNullOrEmpty(targetPeer)) m["target"] = targetPeer;

            _dedupEngine.MarkSeen(Msg.Str(m, "id"));
            // 必须走该频道的**全部**链路（qaul / 局域网 / WLAN Direct / 蓝牙）：
            // Deliver 只发 qaul + 局域网，只经蓝牙或点对点连着的设备根本收不到请求，
            // 表现就是「发了定位请求对方毫无反应」。
            var reqCh = channels.ByConv(target);
            var reqLinks = EffectiveLinks(reqCh);
            var reqWire = SealOutgoing(target, m);
            var reqWireMsg = WireCopy(m, reqWire);
            EnqueueOutbound(target, reqWire, reqWireMsg, null, reqLinks, false, null);
            // 起一个超时提示：若对方迟迟不回传坐标，主动告知可能的原因，避免静默无反应。
            ArmLocReqTimeout(target, durationMs);
            Android.Util.Log.Info("Mchat", "[Loc] 已发送定位请求 conv=" + target
                + " target=" + (string.IsNullOrEmpty(targetPeer) ? L10n.T("(全部)") : targetPeer)
                + " dur=" + durationMs + "ms");
            Emit("locreq", new { conv = target, target = targetPeer ?? "", dur = durationMs });
            return true;
        }

        /// <summary>
        /// 本机自动定位：收到别人的请求（或自己点按钮）后开始，并把每个定位点发进频道。
        /// </summary>
        /// <param name="burstMs">连续上报时长（毫秒）；&gt;0 时 1 秒一个点、到点自动停；0 为常驻模式。</param>
        public bool StartSelfLocation(string conv = null, int burstMs = 0, string requester = null)
        {
            locationRequester = requester;
            if (!string.IsNullOrEmpty(conv)) locationConv = conv;
            else if (string.IsNullOrEmpty(locationConv))
            {
                var list = MchatChannelConvs();
                locationConv = list.Count > 0 ? list[0] : activeConv;
            }

            // 被远程请求时用「连续模式」：1 秒一个点、不设位移阈值、走完 burstMs 自动停，
            // 这样对端点一次就能连续看到 5 秒的轨迹，而不是只收到 1 个点后 GPS 一直亮着。
            int interval = burstMs > 0 ? 1000 : 20000;
            float minDist = burstMs > 0 ? 0f : 8f;

            var ok = LocationReporter.Start(
                fix => SendLocationFix(fix),
                err =>
                {
                    Emit("devreq", new { req = "gps", accepted = false, error = err });
                    // 把失败原因回传请求方，否则请求端只会看到「没反应」
                    SendLocAck(locationConv, locationRequester, false, err);
                },
                interval,
                minDist,
                burstMs,
                15000,
                burstMs > 0
                    ? (Action)(() => Emit("locburst", new Dictionary<string, object> { ["active"] = false, ["conv"] = locationConv }))
                    : null);

            if (ok && burstMs > 0)
            {
                Emit("locburst", new Dictionary<string, object> { ["active"] = true, ["conv"] = locationConv, ["seconds"] = burstMs / 1000 });
            }
            return ok;
        }

        /// <summary>聊天「共享位置」：点一次只发送一次定位，拿到首个有效点即停止，不再持续上报。</summary>
        public bool SendMyLocationOnce(string conv = null)
        {
            if (!string.IsNullOrEmpty(conv)) locationConv = conv;
            else if (string.IsNullOrEmpty(locationConv)) locationConv = activeConv;
            bool sent = false;
            var ok = LocationReporter.Start(
                fix =>
                {
                    if (sent) return;
                    sent = true;
                    try { SendLocationFix(fix); } catch { }
                    LocationReporter.Stop();   // 单次：发完立刻停，GPS 不常亮
                    try { Emit("locburst", new Dictionary<string, object> { ["active"] = false, ["conv"] = locationConv }); } catch { }
                },
                err =>
                {
                    try { Emit("devreq", new { req = "gps", accepted = false, error = err }); } catch { }
                },
                1000, 0f, 15000, 15000, null);
            return ok;
        }

        public void StopSelfLocation()
        {
            LocationReporter.Stop();
            Emit("devreq", new { req = "gps", accepted = false, stopped = true });
        }

        /// <summary>
        /// 给请求方回一条定位回执（只有失败时才发）：说明为什么没有回传坐标
        /// （没有定位权限 / 定位开关没开 / 本机不支持定位等）。
        /// 同样走频道全部链路，避免只连蓝牙的设备收不到。
        /// </summary>
        private void SendLocAck(string conv, string toPeer, bool ok, string error)
        {
            if (string.IsNullOrEmpty(conv)) return;
            try
            {
                var m = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = conv,
                    ["kind"] = DevReqKind,
                    ["req"] = "gps",
                    ["resp"] = true,
                    ["accepted"] = ok,
                    ["error"] = error ?? "",
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                if (!string.IsNullOrEmpty(toPeer)) m["target"] = toPeer;
                _dedupEngine.MarkSeen(Msg.Str(m, "id"));

                var ch = channels.ByConv(conv);
                var links = EffectiveLinks(ch);
                var wire = SealOutgoing(conv, m);
                var wireMsg = WireCopy(m, wire);
                EnqueueOutbound(conv, wire, wireMsg, null, links, false, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Loc", "定位回执发送失败: " + ex.Message);
            }
        }

        /// <summary>把一次定位结果作为普通消息发进频道（格式：时间-纬度,经度）。</summary>
        public void SendLocationFix(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            LastLocationText = text;
            var target = locationConv;
            if (string.IsNullOrEmpty(target) || !Convs.IsChan(target))
            {
                var list = MchatChannelConvs();
                target = list.Count > 0 ? list[0] : target;
            }
            if (string.IsNullOrEmpty(target)) return;

            try { SendInConv(target, text); } catch (Exception ex) { Android.Util.Log.Warn("Loc", "发送定位失败: " + ex.Message); }
            try { Android.Util.Log.Info("Mchat", "[Loc] 上报 " + text + " -> " + target); } catch { }
            Emit("location", new { text = text, conv = target });
        }

        /// <summary>收到短信：把全文转发到 Mchat 频道（各设备收到短信都会自动发一条）。</summary>
        public void ForwardSms(string sender, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            var head = string.IsNullOrEmpty(sender) ? L10n.T("[短信]") : L10n.T("[短信] ") + sender + "：";
            var text = head + body.Trim();

            // 只发到初始频道（不再群发到所有频道，避免刷屏与重复）
            try { SendInConv(Convs.Chan(ChannelRegistry.DefaultChannelName), text, "text"); }
            catch (Exception ex) { Android.Util.Log.Warn("Sms", "短信转发失败: " + ex.Message); }
        }

        private void HandleDevStat(Dictionary<string, object> msg)
        {
            var from = Msg.Str(msg, "from");
            if (string.IsNullOrEmpty(from) || from == peerId) return;
            var d = new Dictionary<string, object>
            {
                ["peer"] = from,
                ["name"] = Msg.Str(msg, "fromName"),
                ["dev"] = Msg.Str(msg, "dev"),
                ["ip"] = Msg.Str(msg, "ip"),
                ["battery"] = Msg.Num(msg, "battery", -1),
                ["charging"] = Msg.Bool(msg, "charging"),
                // 这条状态是从哪条链路来的（蓝牙/WLAN Direct/局域网/qaul），设备栏据此显示连接图标
                ["link"] = GuessLink(msg) ?? "",
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            lock (devicePresenceLock) devicePresence[from] = d;
            try { DevicePresenceChanged?.Invoke(); } catch { }
            Emit("devstat", d);
        }

        private void HandleDevReq(Dictionary<string, object> msg)
        {
            var req = Msg.Str(msg, "req");
            var from = Msg.Str(msg, "from");
            if (req != "gps") return;
            if (!string.IsNullOrEmpty(from) && from == peerId) return;   // 自己发的请求不自己响应

            // 回执帧（对方定位失败）：只提示，不再触发定位
            if (Msg.Bool(msg, "resp"))
            {
                // 回执是广播到频道的，只有请求方该弹提示
                var ackTarget = Msg.Str(msg, "target");
                if (!string.IsNullOrEmpty(ackTarget) && ackTarget != peerId) return;
                Emit("locresp", new Dictionary<string, object>
                {
                    ["peer"] = from,
                    ["name"] = Msg.Str(msg, "fromName"),
                    ["ok"] = Msg.Bool(msg, "accepted"),
                    ["error"] = Msg.Str(msg, "error")
                });
                return;
            }

            // 定向请求：只让被点名的设备响应，避免频道里所有设备一起开 GPS
            var targetPeer = Msg.Str(msg, "target");
            if (!string.IsNullOrEmpty(targetPeer) && targetPeer != peerId)
            {
                Android.Util.Log.Info("Mchat", "[Loc] 定位请求指定给其它设备，本机不响应");
                return;
            }

            // 聊天频道的「定位其他设备」：被请求方自动取一次本机实时位置并发一条聊天消息，
            // 全程无任何本地提示（不弹 toast / 不回执 / 不广播状态），只发一次即停。
            // 持续更新只属于雷达页的 GPS 共享（StartLocationShare 的隐藏频道），与此无关。
            Android.Util.Log.Info("Mchat", "[Loc] 收到定位请求 from=" + from + "，单次静默上报");
            SendLocationOnceSilent(Msg.Conv(msg));
        }

        /// <summary>
        /// 被远程请求定位时：取一次本机实时位置，作为普通消息发进请求所在频道，发完立刻停止定位。
        /// 全程静默——不 Emit locburst/devreq/locresp，不广播设备状态，不给请求方发失败回执，
        /// 避免在被请求设备上出现任何 toast/提示；请求方会在聊天频道看到这条定位消息。
        /// </summary>
        private void SendLocationOnceSilent(string conv)
        {
            if (!string.IsNullOrEmpty(conv)) locationConv = conv;
            else if (string.IsNullOrEmpty(locationConv)) locationConv = activeConv;
            bool sent = false;
            try
            {
                LocationReporter.Start(
                    fix =>
                    {
                        if (sent) return;
                        sent = true;
                        try { SendLocationFix(fix); } catch { }
                        LocationReporter.Stop();   // 只发一次：拿到首个有效点立刻停，GPS 不常亮
                    },
                    null,                          // 静默：定位失败不回调、不提示
                    1000, 0f, 15000, 15000, null); // 给 15s 兜底窗口：拿不到点 Tick 也会自动 Stop，GPS 不会常亮
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Loc", "静默定位上报失败: " + ex.Message);
            }
        }

        /// <summary>当前应把定位发到哪个频道（收到请求时记住）。</summary>
        private string locationConv;

        /// <summary>最近一次定位请求的发起方 peerId（定位失败时回执给它）。</summary>
        private string locationRequester;

        /// <summary>
        /// 请求方等待对方回传坐标的超时提示状态。
        /// 真实场景里「发了定位请求却毫无反应」几乎都是：对方不在同一网络、或没授权定位、
        /// 或定位服务没开——这些情况下本机收不到任何回执，用户会以为功能失效。
        /// 这里发请求后起一个定时器，超过「连续上报窗口 + 宽限」仍没收到的，主动弹提示，
        /// 让失败可见，而不是静默无反应。
        /// </summary>
        private bool _locReqPending;
        private string _locReqPendingConv;
        private readonly Handler _locReqHandler = new Handler(Looper.MainLooper);
        private readonly Java.Lang.Runnable _locReqTimeout = new Java.Lang.Runnable(LocReqTimedOut);

        private static void LocReqTimedOut()
        {
            var b = Current as MchatBridge;
            if (b == null || !b._locReqPending) return;
            b._locReqPending = false;
            try { b.Emit("locreq", new Dictionary<string, object> { ["timeout"] = true, ["conv"] = b._locReqPendingConv ?? "" }); } catch { }
        }

        private void ArmLocReqTimeout(string conv, int durMs)
        {
            try
            {
                _locReqPending = true;
                _locReqPendingConv = conv;
                _locReqHandler.RemoveCallbacks(_locReqTimeout);
                _locReqHandler.PostDelayed(_locReqTimeout, durMs + 8000);
            }
            catch { }
        }

        private void CancelLocReqTimeout()
        {
            try { _locReqHandler.RemoveCallbacks(_locReqTimeout); } catch { }
            _locReqPending = false;
        }

        /// <summary>
        /// 处理已读回执（kind=read_receipt）。
        /// 更新本地 ChatStore 的已读时间点。
        /// </summary>
        private void HandleReadReceipt(Dictionary<string, object> msg)
        {
            string conv = Msg.Conv(msg);
            string from = Msg.Str(msg, "from");
            long readUpTo = Msg.Num(msg, "readUpTo", 0);
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(from) || readUpTo <= 0) return;

            // 更新本地存储的已读进度
            store.MarkReadUpTo(conv, from, readUpTo);
        }

        /// <summary>
        /// 处理群组管理（kind=group_mgmt）。
        /// op: create | invite | kick | promote | dismiss
        /// </summary>
        private void HandleGroupMgmt(Dictionary<string, object> msg)
        {
            string op = Msg.Str(msg, "op");
            string conv = Msg.Conv(msg);
            string from = Msg.Str(msg, "from");
            string target = Msg.Str(msg, "target");
            string groupName = Msg.Str(msg, "groupName");

            if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(conv)) return;

            // 触发事件通知 UI
            try { GroupMgmtReceived?.Invoke(op, conv, from, target, groupName); } catch { }

            Android.Util.Log.Info("Mchat", $"[Group] 收到群组管理: op={op}, conv={conv}, from={from}, target={target}, groupName={groupName}");
        }

        /// <summary>
        /// 群组管理事件：op, conv, fromPeerId, targetPeerId, groupName
        /// </summary>
        public event Action<string, string, string, string, string>? GroupMgmtReceived;

        /// <summary>请求方收到对方回传的坐标（4 行格式：名 / IP|电量 / 日期 / 纬度,经度）。</summary>
        private static bool LooksLikeLocation(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            var lines = t.Split('\n');
            if (lines.Length < 1) return false;
            var last = lines[lines.Length - 1].Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(
                last, @"^-?\d{1,3}(\.\d+)?,-?\d{1,3}(\.\d+)?$");
        }

        private void OnPeerLocationSeen(string text, string fromName, string conv)
        {
            if (!_locReqPending) return;
            if (!string.IsNullOrEmpty(_locReqPendingConv) && conv != _locReqPendingConv) return;
            CancelLocReqTimeout();
            try
            {
                Emit("locfix", new Dictionary<string, object> { ["text"] = text ?? "", ["name"] = fromName ?? "" });
            }
            catch { }
        }

        /// <summary>
        /// 发送功能控制指令（音量/播放/上下曲等）到**局域网内其它设备**。
        ///
        /// 走独立 control 帧：不进聊天历史、不渲染消息、不弹浮层通知、不进剪贴板，
        /// 接收端仅执行对应模拟输入（本机不执行，浮窗/远程控制都用这一条）。
        /// </summary>
        [Java.Interop.Export("sendControl")]
        [JavascriptInterface]
        public void SendControl(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            try
            {
                // 控制指令一律走 mesh 频道：它是局域网内各设备共用的渠道。
                // 这里只是「借用」该渠道传输指令——指令不是正式聊天消息
                // （kind=control 会被界面过滤，不渲染成气泡），
                // 且仅由接收侧执行，用来控制「其它设备」而非本机。
                var conv = Convs.Chan(ChannelRegistry.DefaultChannelName);
                var msg = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = conv,
                    ["kind"] = "control",
                    ["op"] = MsgOp.Cmd,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["cmd"] = cmd,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());

                // 必须用「频道全局链路」（qaul / 局域网 / WLAN Direct / 蓝牙），
                // 不能像 Deliver 那样只发 qaul + 局域网——只连了蓝牙或只连了
                // WLAN Direct 的设备一样要能被浮窗的媒体键控制到。
                var ch = channels.ByConv(conv);
                var links = EffectiveLinks(ch);
                var wire = SealOutgoing(conv, msg);
                var wireMsg = WireCopy(msg, wire);
                EnqueueOutbound(conv, wire, wireMsg, null, links, false, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "发送控制指令失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 只把一条指令发给**指定 IP** 的那台设备（不是广播）。
        ///
        /// 投屏邀请必须定向：广播出去的话，局域网里每一台电脑都会弹"是否接收投屏"，
        /// 而用户明明只在某一台电脑的设备卡上点了「投屏」。
        /// </summary>
        public bool SendControlTo(string ip, string cmd)
        {
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(cmd)) return false;
            try
            {
                Peer target = null;
                var now = SysEnv.TickCount;
                foreach (var p in peers.Values)
                {
                    if (p == null || now - p.LastSeen >= PeerTimeoutMs) continue;
                    if (string.Equals(p.Address, ip, StringComparison.Ordinal)) { target = p; break; }
                }
                if (target == null)
                {
                    Android.Util.Log.Warn("Mchat", "定向指令找不到在线设备: " + ip);
                    return false;
                }

                var msg = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = Convs.Chan(ChannelRegistry.DefaultChannelName),
                    ["kind"] = "control",
                    ["op"] = MsgOp.Cmd,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["cmd"] = cmd,
                    // 电脑端靠 text 认指令（cmd 字段在有些链路上不传），两个位置都放一份
                    ["text"] = cmd,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                SendFrameTo(target, JsonConvert.SerializeObject(msg), null);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "定向指令发送失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 向 mesh 频道发一张「共享卡片」（屏幕画笔 / 摄像头共享 / 屏幕共享）。
        ///
        /// 局域网里其它设备看到这张卡片后点一下，就能<b>在本 App 内</b>全屏观看，
        /// 不必再用浏览器打开网页。卡片里带上 App 专属加速通道的端口（castPort），
        /// 观看端优先走这条通道（UDP 直传，比 HTTP 拉流省掉握手与队头阻塞），
        /// 收不到再自动回退到 HTTP 地址。
        /// </summary>
        /// <param name="shareType">draw=屏幕画笔 / camera=摄像头共享 / screen=屏幕共享</param>
        /// <param name="conv">
        /// 卡片发到哪个会话。默认 = 当前对话（频道或私聊都行）——
        /// 原来写死成默认频道，用户在别的频道里点共享，链接却跑到 mesh，别人一脸茫然。
        /// </param>
        public void SendShareCard(string shareType, string title, string url, int castPort, string conv = null)
        {
            try
            {
                if (string.IsNullOrEmpty(conv) || conv == Convs.WebConv)
                    conv = !string.IsNullOrEmpty(activeConv) ? activeConv : Convs.Chan(ChannelRegistry.DefaultChannelName);
                var msg = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = conv,
                    ["kind"] = "share",
                    ["shareType"] = shareType ?? "",
                    ["text"] = title ?? "",
                    ["url"] = url ?? "",
                    ["castPort"] = castPort,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                store.Add(msg, true);
                AppendHistory(msg);
                Emit("message", msg);
                Deliver(msg, null);
                Android.Util.Log.Info("Mchat", "已发送共享卡片：" + shareType + " " + url + " → " + conv);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "发送共享卡片失败: " + ex.Message);
            }
        }

        /// <summary>视频分享卡片：播放器里点「分享」时往当前会话发一张带封面的卡片。
        /// 返回消息 id，作为「一起看」会话 id（watchCard）供宿主机上报进度。</summary>
        public string SendVideoCard(Dictionary<string, object> fields)
        {
            if (fields == null) return "";
            try
            {
                var conv = !string.IsNullOrEmpty(activeConv)
                    ? activeConv : Convs.Chan(ChannelRegistry.DefaultChannelName);
                string id = Guid.NewGuid().ToString("N");
                var msg = new Dictionary<string, object>(fields)
                {
                    ["id"] = id,
                    ["conv"] = conv,
                    ["kind"] = VideoShare.Kind,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                // 让卡片自己知道会话 id：跟随端轮询状态镜像时据此过滤
                if (!msg.ContainsKey("watchCard")) msg["watchCard"] = id;
                _dedupEngine.MarkSeen(id);
                store.Add(msg, true);
                AppendHistory(msg);
                Emit("message", msg);
                Deliver(msg, null);
                return id;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "发送视频分享卡片失败: " + ex.Message);
                return "";
            }
        }

        /* ============================================================
         * 局域网音乐共享（MusicCast）
         *  · mediaplayer  —— 播放状态广播，不进聊天历史，仅驱动对端通知栏
         *  · musiccard    —— mesh 频道里的音乐卡片（封面/歌词/控制），切歌用 patch 原地更新
         *  · mediacontrol —— 对端回传的播放指令，带 target 只让播放端执行
         *  · 一起听      —— musiccard 附带 playlist，对端点「一起听」加载整条列表
         * ============================================================ */

        /// <summary>最近一次新建的音乐卡片 id（仅新卡片时更新）。</summary>
        public string MusicCastLastCardId { get; private set; }

        private string MeshConv => Convs.Chan(ChannelRegistry.DefaultChannelName);

        /// <summary>广播播放状态（进入对端通知栏）。不落库、不渲染气泡。</summary>
        public void MusicCastPublish(Dictionary<string, object> state)
        {
            if (state == null) return;
            try
            {
                var conv = MeshConv;
                var msg = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = conv,
                    ["kind"] = "mediaplayer",
                    ["state"] = state,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                Deliver(msg, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "广播播放状态失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 「同步听」时钟对时的瞬时帧：不落库、不渲染气泡、不转发给机器人。
        /// 时间戳放在消息顶层（而不是 state 里），这样 qaul 侧也能通过 meta 短键还原。
        /// </summary>
        public void SyncClockSend(string kind, Dictionary<string, object> fields)
        {
            if (fields == null) return;
            try
            {
                var msg = new Dictionary<string, object>(fields)
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = MeshConv,
                    ["kind"] = kind,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                Deliver(msg, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "同步听对时帧发送失败: " + ex.Message);
            }
        }

        /// <summary>把控制指令发回播放端（对端通知栏/卡片按钮用）。</summary>
        public void MusicCastSendCommand(string cmd, int position, string target)
        {
            if (string.IsNullOrEmpty(cmd) || string.IsNullOrEmpty(target)) return;
            try
            {
                var conv = MeshConv;
                var msg = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = conv,
                    ["kind"] = "mediacontrol",
                    ["state"] = new Dictionary<string, object>
                    {
                        ["cmd"] = cmd,
                        ["position"] = position,
                        ["target"] = target
                    },
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                Deliver(msg, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "发送播放指令失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 发送（或原地更新）mesh 频道里的音乐卡片。
        /// patch=true 时不再发新卡片，而是发一条 op=patch 的控制帧，
        /// 对端收到后按 target 覆盖同一条消息，界面只刷新不新增。
        /// </summary>
        public void MusicCastCard(string cardId, Dictionary<string, object> fields, bool patch)
        {
            if (fields == null) return;
            try
            {
                var conv = MeshConv;

                if (patch && !string.IsNullOrEmpty(cardId))
                {
                    var body = new Dictionary<string, object>(fields)
                    {
                        ["id"] = cardId,
                        ["conv"] = conv,
                        ["kind"] = "musiccard",
                        ["from"] = peerId,
                        ["fromName"] = displayName,
                        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };

                    // 与已存在的卡片合并：封面/歌词/一起听列表没随本次 patch 发出时沿用旧的，
                    // 否则整条替换会把上一首的图片、歌词和播放列表弄丢；发送时间也保持首次发卡的时间。
                    var prev = store.Get(cardId);
                    if (prev != null)
                    {
                        // 歌词只在「归属同一首歌」时沿用：patch 会带上新的 lrcKey，
                        // 换曲后把上一首的 LRC 回填过来会导致歌词张冠李戴。
                        bool sameSong = true;
                        if (body.ContainsKey("lrcKey") && prev.TryGetValue("lrcKey", out var oldLk))
                            sameSong = string.Equals(Convert.ToString(oldLk),
                                Msg.Str(body, "lrcKey", ""), StringComparison.Ordinal);
                        // tvPlaylist/tvIndex/tvHost：patch 整条替换时必须沿用，否则后上线的电视
                        // 从 SyncSessionTo 补发的缓存卡片里就取不到这份歌单了。
                        foreach (var key in new[] { "cover", "lrcKey", "playlist", "lt", "ltHost", "ltIndex", "peerHost", "tvPlaylist", "tvIndex", "tvHost" })
                            if (!body.ContainsKey(key) && prev.TryGetValue(key, out var pv) && pv != null)
                                body[key] = pv;
                        if (sameSong && !body.ContainsKey("lrc")
                            && prev.TryGetValue("lrc", out var oldLrc) && oldLrc != null)
                            body["lrc"] = oldLrc;
                        if (prev.TryGetValue("time", out var pt)) body["time"] = pt;
                    }

                    // 本机先就地更新
                    if (store.Replace(body)) Emit("msgupd", body);

                    var frame = new Dictionary<string, object>(body)
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["kind"] = Msg.ControlKind,
                        ["op"] = MsgOp.Patch,
                        ["target"] = cardId,
                        ["patchKind"] = "musiccard",
                        // 关键：对外帧必须带「当前时间」，不能沿用 body 里首发卡片的时间。
                        // body 的 time 在上面被合并成首发时间（气泡时间要稳定），而对外帧用当前时间
                        // 才能让后启动设备把补卡当作新消息展示（时间过旧会沉到历史里看不见）。
                        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    // 播放列表必须随 patch 一起发：后启动设备没收到过首发完整卡，只能靠 patch
                    // 补卡；若把 playlist 去掉，补出来的就是「裸卡」——没有「一起听」按钮，
                    // 用户就无法手动加入。歌单通常只有几 KB，比封面小得多，可以随每次 patch 携带。
                    // 封面/歌词/电视专用歌单较大，非换曲的进度 patch 仍剥离以省带宽，对端会沿用旧值。
                    if (!fields.ContainsKey("cover")) frame.Remove("cover");
                    if (!fields.ContainsKey("lrc")) frame.Remove("lrc");
                    if (!fields.ContainsKey("tvPlaylist")) frame.Remove("tvPlaylist");
                    // 若本机 Comp 的卡片暂无歌单（开关刚开启等），用缓存的完整歌单兜底，
                    // 保证 patch 帧始终携带可加入的 playlist。
                    if (!frame.ContainsKey("playlist") || string.IsNullOrEmpty(Msg.Str(frame, "playlist")))
                    {
                        try
                        {
                            int pidx;
                            string cached = AhoChatA.ListenTogether.BuildPlaylist(out pidx);
                            if (!string.IsNullOrEmpty(cached) && cached != "[]")
                            {
                                frame["playlist"] = cached;
                                frame["lt"] = 1;
                                frame["ltHost"] = AhoChatA.ListenTogether.HostBase();
                                if (!frame.ContainsKey("ltIndex")) frame["ltIndex"] = pidx;
                            }
                        }
                        catch { }
                    }
                    _dedupEngine.MarkSeen(frame["id"].ToString());
                    Deliver(frame, null);
                    return;
                }

                var id = Guid.NewGuid().ToString("N");
                var msg = new Dictionary<string, object>(fields)
                {
                    ["id"] = id,
                    ["conv"] = conv,
                    ["kind"] = "musiccard",
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                // 电视端自动播放：若「一起听」开关关闭，卡片不带 playlist（ListenTogether.Enrich
                // 直接 return），电视就无从加载歌单。这里补一组**独立字段**给电视专用：
                //   · 只在没有 playlist 时才补，开关开启时帧体积零增长、行为完全不变；
                //   · 不碰 lt/ltHost/SyncListen/开关/UI —— 手机端所有消费点只读 playlist，
                //     这些 tv* 字段不会被误当成「一起听」，其它手机设备与界面零影响；
                //   · BuildPlaylist 本就是 public 且无视开关，直接复用即可拿到整条队列。
                try
                {
                    if (string.IsNullOrEmpty(Msg.Str(msg, "playlist"))
                        && !msg.ContainsKey("playlist"))
                    {
                        int tvIdx;
                        string pl = ListenTogether.BuildPlaylist(out tvIdx);
                        if (!string.IsNullOrEmpty(pl) && pl != "[]")
                        {
                            msg["tvPlaylist"] = pl;
                            msg["tvIndex"] = tvIdx;
                            msg["tvHost"] = ListenTogether.HostBase();
                        }
                    }
                }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "tvPlaylist 补充失败: " + ex.Message); }

                _dedupEngine.MarkSeen(id);
                store.Add(msg, true);
                AppendHistory(msg);
                Emit("message", msg);
                Deliver(msg, null);
                MusicCastLastCardId = id;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "发送音乐卡片失败: " + ex.Message);
            }
        }


        /// <summary>把 base64 媒体保存到本地（用于本机发送的图片/视频预览），返回可访问 URL。</summary>
        [Java.Interop.Export("saveMedia")]
        [JavascriptInterface]
        public string SaveMedia(string base64, string fileName)
        {
            try
            {
                var name = SafeMediaName(fileName, Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(Path.Combine(mediaDir, name), Convert.FromBase64String(base64 ?? ""));
                return "/__media/" + name;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", $"保存媒体失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 网页端聊天「媒体快捷卡片」点击：把收到的分享媒体落盘 /sdcard/file/&lt;类型&gt;/
        /// 并加入本机对应媒体库（阅读 / 影音·音乐 / 图片），返回给用户看的提示文案。
        /// </summary>
        [Java.Interop.Export("importMlib")]
        [JavascriptInterface]
        public string ImportMlib(string mediaName)
        {
            return MediaShare.ImportFromMediaDir(context, mediaName);
        }

        /// <summary>
        /// 网页端「程序更新卡片」点击安装按钮：安装 UpdateManager 已下载的 APK。
        /// 返回是否成功转交系统安装器。
        /// </summary>
        [Java.Interop.Export("installApk")]
        [JavascriptInterface]
        public bool InstallApk(string fileName)
        {
            return UpdateManager.InstallApk(fileName);
        }

        /// <summary>
        /// 「一起听」：网页端音乐卡片点「一起听」时，把整条共享播放列表装进本机播放器。
        /// playlistJson 为卡片里的精简 JSON 数组；返回是否成功起播。
        /// </summary>
        [Java.Interop.Export("joinListen")]
        [JavascriptInterface]
        public bool JoinListen(string playlistJson, int index)
        {
            try { return ListenTogether.Join(playlistJson, index); }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "joinListen 失败: " + ex.Message); return false; }
        }

        /// <summary>「一起听」：只共享歌单，各播各的（与「同步听」区分）。</summary>
        [Java.Interop.Export("startSharedPlaylist")]
        [JavascriptInterface]
        public bool StartSharedPlaylist(string playlistJson, int index, string cardId, string host)
        {
            try { return SyncListen.StartPlaylist(playlistJson, index, cardId, host); }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "startSharedPlaylist 失败: " + ex.Message); return false; }
        }

        /// <summary>「同步听」：装歌单 + 立刻对齐发起端的曲目/进度/播放态，之后由 patch 持续跟随。</summary>
        [Java.Interop.Export("startSyncListen")]
        [JavascriptInterface]
        public bool StartSyncListen(string playlistJson, int index, string cardId, string stateJson)
        {
            try
            {
                Dictionary<string, object> st = null;
                if (!string.IsNullOrWhiteSpace(stateJson))
                    st = JsonConvert.DeserializeObject<Dictionary<string, object>>(stateJson);
                string host = st != null ? Msg.Str(st, "from", "") : "";
                return SyncListen.StartSync(playlistJson, index, cardId, host, st);
            }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "startSyncListen 失败: " + ex.Message); return false; }
        }

        [Java.Interop.Export("exitSyncListen")]
        [JavascriptInterface]
        public void ExitSyncListen() { try { SyncListen.Exit(); } catch { } }

        /// <summary>返回 {"mode":0/1/2,"cardId":...}，网页端据此显示「退出同步」。</summary>
        [Java.Interop.Export("getSyncListen")]
        [JavascriptInterface]
        public string GetSyncListen() { return SyncListen.StatusJson(); }

        /// <summary>「一起听」开关（网页端设置页用）。</summary>
        [Java.Interop.Export("getListenTogether")]
        [JavascriptInterface]
        public bool GetListenTogether() { return ListenTogether.Enabled; }

        [Java.Interop.Export("setListenTogether")]
        [JavascriptInterface]
        public void SetListenTogether(bool on) { ListenTogether.Enabled = on; }

        /// <summary>「自动接收局域网文件」开关与目录（网页端/工具面板用）。</summary>
        [Java.Interop.Export("getAutoReceive")]
        [JavascriptInterface]
        public bool GetAutoReceive() { return AutoReceiveSettings.Enabled; }

        [Java.Interop.Export("setAutoReceive")]
        [JavascriptInterface]
        public void SetAutoReceive(bool on) { AutoReceiveSettings.Enabled = on; }

        [Java.Interop.Export("getAutoReceiveDir")]
        [JavascriptInterface]
        public string GetAutoReceiveDir() { return AutoReceiveSettings.Dir; }

        [Java.Interop.Export("setAutoReceiveDir")]
        [JavascriptInterface]
        public void SetAutoReceiveDir(string dir) { AutoReceiveSettings.Dir = dir; }

        /// <summary>
        /// 网页端切日间/夜间：写入与原生同一份偏好（Mchat/theme），并让原生界面立即重上色。
        /// 参数取 day | night | auto；auto 跟随系统深色。
        /// </summary>
        [Java.Interop.Export("setTheme")]
        [JavascriptInterface]
        public void SetTheme(string mode)
        {
            try
            {
                if (mode != ThemeManager.DAY && mode != ThemeManager.NIGHT && mode != ThemeManager.AUTO) return;
                ThemeManager.SetMode(context, mode);
                var a = CurrentActivity;
                if (a == null) return;
                a.RunOnUiThread(() =>
                {
                    try { (a as BaseActivity)?.ApplyThemeLive(); } catch { }
                });
            }
            catch { }
        }

        /// <summary>当前主题模式：day | night | auto（网页端顶栏按钮据此画太阳/月亮）。</summary>
        [Java.Interop.Export("getTheme")]
        [JavascriptInterface]
        public string GetTheme() { return ThemeManager.GetMode(context); }

        /// <summary>
        /// 视频分享卡片点击：启动本机全屏视频播放器播放该链接。
        /// 本地文件是 http://&lt;分享者IP&gt;:7790/api/media/stream?path=...（支持 Range），
        /// 在线影视是已解析直链；两者 ExoPlayer 都能直接全屏播。
        /// </summary>
        [Java.Interop.Export("openSharedVideo")]
        [JavascriptInterface]
        public bool OpenSharedVideo(string url, string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return false;
                var intent = new Android.Content.Intent(context, typeof(VideoActivity));
                intent.PutExtra(VideoActivity.ExtraPath, url);
                intent.PutExtra(VideoActivity.ExtraTitle, title ?? "");
                intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.SingleTop);
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "打开分享视频失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 视频卡片点「一起看」：用宿主机代理地址起播，并带上会话 id / 宿主机地址，
        /// VideoActivity 据此轮询宿主机状态镜像、按 ts/seq 时间戳校验跟随进度。
        /// </summary>
        [Java.Interop.Export("openWatchTogether")]
        [JavascriptInterface]
        public bool OpenWatchTogether(string url, string title, string cardId, string host)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return false;
                var intent = new Android.Content.Intent(context, typeof(VideoActivity));
                intent.PutExtra(VideoActivity.ExtraPath, url);
                intent.PutExtra(VideoActivity.ExtraTitle, title ?? "");
                intent.PutExtra(VideoActivity.ExtraWatchCard, cardId ?? "");
                intent.PutExtra(VideoActivity.ExtraWatchHost, host ?? "");
                intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.SingleTop);
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "加入一起看失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 广播一条 WebRTC 通话信令（offer / answer / ice-candidate）。
        /// 通过局域网 TCP 广播给所有在线设备；对方设备的前端经 Emit("callsignal") 收到。
        /// 信令本身不含媒体，媒体由 WebRTC 在两端间直连（P2P）。
        /// </summary>
        [Java.Interop.Export("sendCallSignal")]
        [JavascriptInterface]
        public void SendCallSignal(string signalJson)
        {
            try
            {
                var sig = JsonConvert.DeserializeObject<Dictionary<string, object>>(signalJson)
                          ?? new Dictionary<string, object>();
                if (!sig.ContainsKey("id") || sig["id"] == null || string.IsNullOrEmpty(sig["id"].ToString()))
                    sig["id"] = Guid.NewGuid().ToString();
                sig["from"] = peerId;
                sig["fromName"] = displayName;
                sig["kind"] = "callsignal";
                sig["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var payload = JsonConvert.SerializeObject(sig);
                Task.Run(() => BroadcastFrame(payload, null));
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"发送通话信令失败: {ex.Message}");
            }
        }

        /// <summary>发起原生语音通话（点对点 UDP PCM）。</summary>
        [Java.Interop.Export("callInvite")]
        [JavascriptInterface]
        public void CallInvite(string peerIdJson)
        {
            try
            {
                var o = JsonConvert.DeserializeObject<Dictionary<string, object>>(peerIdJson)
                        ?? new Dictionary<string, object>();
                var id = o.TryGetValue("id", out var v) ? v?.ToString() : null;
                var name = o.TryGetValue("name", out var n) ? n?.ToString() : id;
                voiceCall.Invite(id, name);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"callInvite 失败: {ex.Message}");
            }
        }

        /// <summary>接听来电。</summary>
        [Java.Interop.Export("callAccept")]
        [JavascriptInterface]
        public void CallAccept()
        {
            try { voiceCall.Accept(); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"callAccept 失败: {ex.Message}");
            }
        }

        /// <summary>挂断通话。</summary>
        [Java.Interop.Export("callHangup")]
        [JavascriptInterface]
        public void CallHangup()
        {
            try { voiceCall.Hangup(); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"callHangup 失败: {ex.Message}");
            }
        }

        /// <summary>切换屏幕画笔叠加层（开/关）。
        /// 首次开启若未授予「显示在其他应用上方」权限，则跳转系统设置申请，
        /// 授权后由 MainActivity 的 OnActivityResult 自动开启。</summary>
        [Java.Interop.Export("toggleScreenDraw")]
        [JavascriptInterface]
        public void ToggleScreenDraw()
        {
            try
            {
                if (!ScreenDrawService.IsRunning && !ScreenDrawService.CanDrawOverlays(context))
                {
                    RequestScreenDrawPermission(CurrentActivity);
                    return;
                }
                ScreenDrawService.Toggle(context);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"toggleScreenDraw 失败: {ex.Message}");
            }
        }

        /// <summary>屏幕画笔叠加层是否正在运行。</summary>
        [Java.Interop.Export("isScreenDrawOn")]
        [JavascriptInterface]
        public bool IsScreenDrawOn()
        {
            return ScreenDrawService.IsRunning;
        }

        /// <summary>
        /// 局域网文件共享的访问地址（网页共享 URL + FTP 地址）。
        /// 未连接 WiFi 时返回 "未连接 WiFi"（按需求此时不展示 IP）。
        /// </summary>
        [Java.Interop.Export("getShareInfo")]
        [JavascriptInterface]
        public string GetShareInfo()
        {
            try { return LanShareService.Describe(context); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"getShareInfo 失败: {ex.Message}");
                return L10n.T("未连接 WiFi");
            }
        }

        /// <summary>
        /// 列出共享目录（LanShareService.ShareRoot）下的条目，供网页端文件管理器内嵌浏览。
        /// 返回 JSON：{ rel, items:[{name,isDir,size,rel}] }。
        /// </summary>
        [Java.Interop.Export("listShare")]
        [JavascriptInterface]
        public string ListShareJs(string relPath)
        {
            try
            {
                var root = LanShareService.ShareRoot;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "{\"rel\":\"\",\"items\":[]}";
                var rel = (relPath ?? "").Replace('\\', '/').TrimStart('/');
                var dir = string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel);
                dir = Path.GetFullPath(dir);
                var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (!dir.Equals(rootFull) && !dir.StartsWith(rootFull + Path.DirectorySeparatorChar.ToString()))
                    return "{\"rel\":\"\",\"items\":[]}";
                if (!Directory.Exists(dir)) return "{\"rel\":\"\",\"items\":[]}";
                var list = new List<Dictionary<string, object>>();
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var di = new DirectoryInfo(d);
                    list.Add(new Dictionary<string, object> { ["name"] = di.Name, ["isDir"] = true, ["size"] = 0, ["rel"] = JoinRel(rel, di.Name) });
                }
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fi = new FileInfo(f);
                    list.Add(new Dictionary<string, object> { ["name"] = fi.Name, ["isDir"] = false, ["size"] = fi.Length, ["rel"] = JoinRel(rel, fi.Name) });
                }
                return JsonConvert.SerializeObject(new { rel = rel, items = list });
            }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "listShare 失败: " + ex.Message); return "{\"rel\":\"\",\"items\":[]}"; }
        }

        /// <summary>把 base64 文件写入共享根目录（网页端文件管理器上传用）。</summary>
        [Java.Interop.Export("uploadFile")]
        [JavascriptInterface]
        public bool UploadFileJs(string base64, string name)
        {
            try
            {
                var root = LanShareService.ShareRoot;
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(name)) return false;
                name = Path.GetFileName(name);
                if (string.IsNullOrEmpty(name)) return false;
                var full = Path.Combine(root, name);
                var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                var target = Path.GetFullPath(full);
                if (!target.Equals(rootFull) && !target.StartsWith(rootFull + Path.DirectorySeparatorChar.ToString()))
                    return false;
                var bytes = Convert.FromBase64String(base64 ?? "");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                System.IO.File.WriteAllBytes(target, bytes);
                return true;
            }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "uploadFile 失败: " + ex.Message); return false; }
        }

        private static string JoinRel(string rel, string name)
        {
            return string.IsNullOrEmpty(rel) ? name : rel + "/" + name;
        }

        /// <summary>
        /// 打开「所有文件访问」系统设置页，申请把共享根目录落到公共存储 /sdcard/Mchat/file。
        /// 由网页共享页的「授予权限」按钮调用；Android 11+ 无运行时弹窗，必须跳转系统设置。
        /// </summary>
        [Java.Interop.Export("requestAllFilesAccess")]
        [JavascriptInterface]
        public void RequestAllFilesAccess()
        {
            try { RequestAllFilesAccess(CurrentActivity); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"requestAllFilesAccess 失败: {ex.Message}");
            }
        }

        private const int RequestScreenDrawPermCode = 1004;
        private const int RequestAllFilesCode = 1005;

        /// <summary>跳转系统设置页申请「显示在其他应用上方」权限（屏幕画笔/悬浮窗用）。</summary>
        public static void RequestScreenDrawPermission(Activity activity)
        {
            if (activity == null) return;
            try
            {
                var intent = new Intent(Android.Provider.Settings.ActionManageOverlayPermission);
                intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                activity.StartActivityForResult(intent, RequestScreenDrawPermCode);
            }
            catch
            {
                try
                {
                    var intent = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings);
                    intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                    activity.StartActivityForResult(intent, RequestScreenDrawPermCode);
                }
                catch { }
            }
        }

        /// <summary>跳转系统设置页申请「所有文件访问」权限（Android 11+，共享根目录落到公共存储）。</summary>
        public static void RequestAllFilesAccess(Activity activity)
        {
            if (activity == null) return;
            if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.R) return;
            try
            {
                var intent = new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission);
                intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                activity.StartActivityForResult(intent, RequestAllFilesCode);
            }
            catch
            {
                try
                {
                    var intent = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings);
                    intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                    activity.StartActivityForResult(intent, RequestAllFilesCode);
                }
                catch { }
            }
        }

        /// <summary>
        /// 开始语音消息录音（原生 AudioRecord，16kHz/16bit/单声道）。
        /// 录音结果（WAV base64）通过 Emit("voiceResult", ...) 回调前端。
        /// </summary>
        [Java.Interop.Export("startVoiceRecord")]
        [JavascriptInterface]
        public void StartVoiceRecord()
        {
            lock (voiceLock)
            {
                if (voiceRecording) return;
                if (context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                    != Android.Content.PM.Permission.Granted)
                {
                    Emit("voiceResult", new { error = L10n.T("未获得麦克风权限") });
                    return;
                }
                if (voiceCall != null && voiceCall.IsActive())
                {
                    Emit("voiceResult", new { error = L10n.T("通话中无法录音") });
                    return;
                }
                try
                {
                    int minBuf = Android.Media.AudioRecord.GetMinBufferSize(VoiceSampleRate,
                        Android.Media.ChannelIn.Mono, Android.Media.Encoding.Pcm16bit);
                    voiceRec = new Android.Media.AudioRecord(Android.Media.AudioSource.Mic, VoiceSampleRate,
                        Android.Media.ChannelIn.Mono, Android.Media.Encoding.Pcm16bit, Math.Max(minBuf, 4096));
                    voiceBuf = new MemoryStream();
                    voiceStart = DateTime.Now;
                    voiceRecording = true;
                    voiceRec.StartRecording();

                    var token = cts.Token;
                    Task.Run(() =>
                    {
                        try
                        {
                            var buf = new byte[Math.Max(minBuf, 4096)];
                            var maxBytes = VoiceSampleRate * 2 * VoiceMaxSeconds;
                            while (voiceRecording && voiceRec != null)
                            {
                                int n = voiceRec.Read(buf, 0, buf.Length);
                                if (n <= 0) break;
                                if (voiceBuf.Length >= maxBytes) break;   // 超限自动停止
                                voiceBuf.Write(buf, 0, n);
                            }
                        }
                        catch (Exception ex)
                        {
                            Android.Util.Log.Warn("Mchat", $"录音异常: {ex.Message}");
                        }
                        finally
                        {
                            FinishVoiceRecord();
                        }
                    }, token);
                }
                catch (Exception ex)
                {
                    voiceRecording = false;
                    Android.Util.Log.Error("Mchat", $"开始录音失败: {ex.Message}");
                    Emit("voiceResult", new { error = L10n.T("无法启动录音") });
                }
            }
        }

        /// <summary>停止录音并回传 WAV 数据。</summary>
        [Java.Interop.Export("stopVoiceRecord")]
        [JavascriptInterface]
        public void StopVoiceRecord()
        {
            lock (voiceLock)
            {
                if (!voiceRecording) return;
                voiceRecording = false;
                // 真正的数据整理在 FinishVoiceRecord（由录音线程 finally 调用）
            }
        }

        private void FinishVoiceRecord()
        {
            lock (voiceLock)
            {
                if (voiceRec == null) return;
                try { voiceRec.Stop(); } catch { }
                try { voiceRec.Release(); } catch { }
                voiceRec = null;

                if (voiceBuf == null) return;
                var pcm = voiceBuf.ToArray();
                voiceBuf = null;

                // 封装 WAV（44 字节头 + PCM），浏览器 <audio> 可直接播放
                var dur = (int)Math.Max(1, (DateTime.Now - voiceStart).TotalSeconds);
                var wav = EncodeWav(pcm, VoiceSampleRate);
                var base64 = Convert.ToBase64String(wav);
                Emit("voiceResult", new
                {
                    base64 = base64,
                    name = "voice.wav",
                    kind = "voice",
                    duration = dur
                });
            }
        }

        /// <summary>把裸 PCM 16bit 封装成标准 WAV 文件字节。</summary>
        private static byte[] EncodeWav(byte[] pcm, int sampleRate)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = pcm.Length;
                int byteRate = sampleRate * 2; // mono * 16bit
                // RIFF header
                w.Write(0x46464952); // "RIFF"
                w.Write(36 + dataLen);
                w.Write(0x45564157); // "WAVE"
                // fmt chunk
                w.Write(0x20746d66); // "fmt "
                w.Write(16);          // chunk size
                w.Write((short)1);    // PCM
                w.Write((short)1);    // mono
                w.Write(sampleRate);
                w.Write(byteRate);
                w.Write((short)2);    // block align = channels * bits/8
                w.Write((short)16);   // bits per sample
                // data chunk
                w.Write(0x61746164); // "data"
                w.Write(dataLen);
                w.Write(pcm);
            }
            return ms.ToArray();
        }

        [Java.Interop.Export("start")]
        [JavascriptInterface]
        public void Start()
        {
            if (started) return;
            started = true;
            cts = new CancellationTokenSource();

            // 出站队列先起：后面的 qaul/局域网/P2P 都会往里投递
            try { outbox.Start(); } catch { }

            // 组播锁改为按需持有（由 RestartDiscovery 按「能否扫描 + 是否空闲」决定），
            // 这里先装上屏幕/前后台监听，省电降频全靠它。
            RegisterScreenMonitor();
            BridgeInstance = this;
            try { ChatNotifier.Init(context); } catch { }   // 消息通知渠道
            StartTcpServer();

            // 载入已保存的中转站（IPv4/IPv6 均可），并启动保活循环自动重连
            LoadStaticRelays();
            StartRelayKeepalive(cts.Token);

            // qaul 消息端口（qaul 渠道）：与上面的 Mchat TCP 渠道并行存在。
            // 渠道可以在「通信渠道设置」里单独关闭，关掉后本机不参与 qaul 网络。
            bool qaulOn = true;
            try
            {
                qaulOn = context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                                .GetBoolean("qaulEnabled", true);
            }
            catch { }

            if (qaulOn)
            {
                try
                {
                    if (!qaul.Start(QaulChannel.DefaultPort))
                        Emit("error", new { message = L10n.T("qaul 消息端口启动失败（9199 被占用）") });
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("Qaul", "qaul 渠道启动失败: " + ex.Message);
                }

                // qaul 网络节点：这才是 qaul 客户端能「发现」本机的关键——
                // mDNS 广播 + libp2p 握手 + qaul_info 路由表，缺一不可。
                StartQaulNode();
            }

            // bitchat 蓝牙链路：**默认关闭**。它只是频道的一种传输方式，
            // 只有切换进「启用了蓝牙链路」的频道时才真正开启。
            try
            {
                bitchatEnabled = LoadBitchatPref();
                bitchat = new BitchatTransport(CurrentActivity, Identity);
                bitchat.OnMessage = OnBitchatMessage;
                bitchat.OnNeighborChange = () => Emit("bitchat", GetBitchatStatus());
                bitchat.OnLog = m => Android.Util.Log.Info("Bitchat", m);
                if (bitchatEnabled) bitchat.Enable();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Bitchat", "bitchat 初始化失败: " + ex.Message);
            }

            // WLAN Direct 链路：默认关闭；开启后与局域网并行收发并互相同步。
            p2pEnabled = LoadP2pPref();

            // 全渠道接入：若上次开启过，则在本轮网络评估时自动恢复离线链路。
            fullChannel = LoadFullChannelPref();

            // LC1 加密层：恢复上次设置的频道密码（密码本身存在私有偏好里）
            try { LoadChannelPasswords(); } catch (Exception ex) { Android.Util.Log.Warn("LC1", "恢复频道密码失败: " + ex.Message); }

            // Mchat 中间层：加密封装（隔除 qaul 窥视）+ 自带设备发现（信标经中继扩散）。
            // 走 qaul 中继时拿不到局域网探测能力，设备只能靠这一层互相找到。
            StartMidLayer();

            // 注册网络状态监听（WiFi/热点/移动数据变化都触发重新扫描）
            RegisterNetworkMonitor();
            // 第一次评估：确定当前是否可扫描，并据此启动发现
            EvaluateNetwork(true);

            // 上线握手：告知昵称与本机所在房间，便于对端补全私聊标题
            AnnouncePresence();

            Emit("ready", new
            {
                peerId,
                name = displayName,
                port = tcpPort,
                qaulPort = qaul.Port,
                dualChannel = DualChannel,
                conv = activeConv
            });

            // 之前因「应用刚被拉起、桥还没起来」而暂存的短信，现在补发出去。
            // 这样即使系统在 App 未运行时投递短信广播，重新打开 App 也不会丢。
            try
            {
                int flushed = SmsForwarder.FlushPending(this);
                if (flushed > 0) Emit("smsforward", new { state = "flushed", count = flushed });
            }
            catch (Exception ex) { Android.Util.Log.Warn("Sms", "补发暂存短信失败: " + ex.Message); }
        }

        [Java.Interop.Export("stop")]
        [JavascriptInterface]
        public void Stop() => Shutdown();

        #region 会话 / 双渠道（qaul 消息端口 + Mchat 原生渠道）

        /* ------------------------------------------------------------------
         * 本次重构的核心：把「谁和谁在哪个会话里说话」从「用哪条管道送」里拆开。
         *
         *   会话层（chat）：房间号 / 私聊 → 会话键 → qaul group_id（确定性 UUID）
         *   管道层（channel）：qaul 消息端口（QaulChannel） + Mchat 原有 TCP JSON
         *
         * 发送一条消息时同时投递到两条管道（DualChannel=true，默认）：
         *   · Mchat 渠道：保持老版本的兼容性，旧设备照样能收到；
         *   · qaul 渠道：使用 qaul 的 RPC 信封与 chat 载荷，房间=group、私聊=direct group。
         * 接收时两条管道都汇聚到 IngestIncoming，用消息 id 去重，不会出现双份。
         * ------------------------------------------------------------------ */

        private bool LoadDualChannelPref()
        {
            var prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
            return prefs.GetBoolean("dualChannel", true);
        }

        /// <summary>切换是否双渠道：true=qaul+Mchat 并行；false=只用 qaul 渠道。</summary>
        public void SetDualChannel(bool on)
        {
            DualChannel = on;
            try
            {
                context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                       .Edit().PutBoolean("dualChannel", on).Apply();
            }
            catch { }
            Emit("channel", ChannelStatus());
        }

        /* ===================== 通信渠道总控（原生「通信渠道设置」页） ===================== */

        /// <summary>qaul 渠道（网络消息 + 局域网发现）是否在跑。</summary>
        public bool QaulEnabled { get { return qaul != null && qaul.Running; } }

        /// <summary>局域网直连渠道是否开启。关闭后只剩 qaul（可经公共节点中继）。</summary>
        public bool LanEnabled { get { return DualChannel; } }

        public void SetLanEnabled(bool on) { SetDualChannel(on); }

        /// <summary>qaul 渠道开关：网络消息端口 + mDNS 节点。关掉后不再参与 qaul 网络。</summary>
        public void SetQaulEnabled(bool on)
        {
            try
            {
                if (on)
                {
                    if (!qaul.Running) qaul.Start(QaulChannel.DefaultPort);
                    StartQaulNode();
                }
                else
                {
                    try { qaul.Stop(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Qaul", "切换 qaul 渠道失败: " + ex.Message);
            }
            try
            {
                context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                       .Edit().PutBoolean("qaulEnabled", on).Apply();
            }
            catch { }
            Emit("channel", ChannelStatus());
        }

        /// <summary>本机连接信息：给「通信渠道设置」页顶部做实时展示。</summary>
        public Dictionary<string, string> ConnectionInfo()
        {
            var d = new Dictionary<string, string>();
            try { d[L10n.T("设备名")] = displayName ?? ""; } catch { }
            try { d[L10n.T("局域网 IP")] = DeviceStatus.Ip ?? L10n.T("未连接"); } catch { d[L10n.T("局域网 IP")] = L10n.T("未知"); }
            try
            {
                var ip6 = DeviceStatus.Ipv6;
                d["局域网 IPv6"] = (ip6 == "::" || string.IsNullOrEmpty(ip6)) ? "未获取" : ip6;
            }
            catch { d[L10n.T("局域网 IPv6")] = L10n.T("未知"); }

            // Wi-Fi 名称（Android 10 起读 SSID 需要定位权限，拿不到就只显示已连接）
            string ssid = "";
            try
            {
                var wm = (Android.Net.Wifi.WifiManager)context.GetSystemService(Context.WifiService);
                if (wm != null && wm.IsWifiEnabled)
                {
                    var info = wm.ConnectionInfo;
                    ssid = info == null ? "" : (info.SSID ?? "").Trim('"');
                }
            }
            catch { }
            d["Wi-Fi"] = string.IsNullOrEmpty(ssid) ? L10n.T("未连接 / 无权限读取") : ssid;

            string bt = "";
            try
            {
                var ad = Android.Bluetooth.BluetoothAdapter.DefaultAdapter;
                if (ad != null && ad.IsEnabled) bt = ad.Name ?? "已开启";
            }
            catch { }
            d[L10n.T("蓝牙")] = string.IsNullOrEmpty(bt) ? L10n.T("未开启") : bt;

            try { d[L10n.T("AhoChat 设备 id")] = peerId ?? ""; } catch { }
            try { d["qaul 身份"] = Identity == null ? "" : Identity.QaulPeerId; } catch { }
            try { d[L10n.T("统一入口")] = WebServer.UrlOf("/hub"); } catch { }
            try { d[L10n.T("局域网消息端口")] = tcpPort.ToString(); } catch { }
            try { d["qaul 消息端口"] = qaul == null || !qaul.Running ? "未开启" : qaul.Port.ToString(); } catch { }
            try { d[L10n.T("文件共享（FTP）")] = "ftp://" + DeviceStatus.Ip + ":" + FtpServer.Port; } catch { }
            try
            {
                int n;
                lock (peers) n = peers.Count;
                d[L10n.T("已发现在线设备")] = n + L10n.T(" 台");
            }
            catch { }
            return d;
        }

        public object ChannelStatus()
        {            return new
            {
                dual = DualChannel,
                qaulPort = qaul.Port,
                qaulRunning = qaul.Running,
                qaulRx = qaul.RxCount,
                qaulTx = qaul.TxCount,
                lanPort = tcpPort,
                conv = activeConv,
                nodeRunning = qnode != null && qnode.Running,
                nodePort = qnode == null ? 0 : qnode.Port,
                neighbours = qnode == null ? 0 : qnode.NeighbourCount,
                infoRx = qnode == null ? 0 : qnode.InfoRx,
                // 各链路健康度（收/发/中继/丢弃 + 最近活动）
                links = ChannelHealth(),
                // 中间层：qaul 只当管道（内容全密文）+ 自带发现（信标可经中继）
                mid = GetLcMidStatus()
            };
        }

        /// <summary>
        /// 各通信渠道的健康度快照（设置页每 2 秒刷新一次就是读它）。
        ///
        /// 每一行＝一条链路：是否开启、传输层是否真的在跑、有几个能用对端、
        /// 收/发/中继/丢弃各多少、最近一次收发在多久之前、最近一次错误。
        /// 「检测各渠道通信情况」靠的就是这份数据 —— 出问题时一眼能看出是哪条链路哑了。
        /// </summary>
        public List<Dictionary<string, string>> ChannelHealth()
        {
            var list = new List<Dictionary<string, string>>();
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int livePeers = 0;
                int qaulPeers = 0;
                try
                {
                    var t = SysEnv.TickCount;
                    foreach (var p in peers.Values)
                    {
                        if (t - p.LastSeen >= PeerTimeoutMs) continue;
                        livePeers++;
                        if (p.QaulPort > 0) qaulPeers++;
                    }
                }
                catch { }

                list.Add(HealthRow("qaul", L10n.T("qaul（Wi-Fi/局域网/以太网）"),
                    true, qaul != null && qaul.Running, qaulPeers, now));
                list.Add(HealthRow(ChanLink.Lan, L10n.T("局域网 TCP（电脑端/旧版）"),
                    DualChannel, tcpListener != null, livePeers, now));
                list.Add(HealthRow(ChanLink.P2p, "WLAN Direct（Wi-Fi P2P）",
                    p2pEnabled, p2p != null && p2p.Enabled,
                    p2pNodes == null ? 0 : p2pNodes.Count, now));
                list.Add(HealthRow(ChanLink.Bitchat, L10n.T("蓝牙（近场）"),
                    bitchatEnabled, bitchat != null && bitchat.Running, BtNeighborCount(), now));
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mesh", "读取渠道健康度失败: " + ex.Message);
            }
            return list;
        }

        /// <summary>出站队列快照：还有几条没发出去、重传了几次、过期丢了几条。</summary>
        public Dictionary<string, string> OutboxStatus()
        {
            var d = new Dictionary<string, string>();
            try
            {
                d["pending"] = outbox.Pending.ToString();
                d["enq"] = outbox.Enqueued.ToString();
                d["sent"] = outbox.SentCount.ToString();
                d["retrans"] = outbox.RetransCount.ToString();
                d["expired"] = outbox.ExpiredCount.ToString();
                d["dropped"] = outbox.DroppedCount.ToString();
            }
            catch { }
            return d;
        }

        private Dictionary<string, string> HealthRow(string link, string label, bool enabled, bool running, int peers, long nowMs)
        {
            var s = mesh.Stat(link);
            string state;
            if (!enabled) state = L10n.T("已关闭");
            else if (!running) state = L10n.T("未运行");
            else if (peers > 0) state = L10n.T("已连接 · ") + peers + L10n.T(" 台");
            else state = L10n.T("运行中 · 暂无对端");

            // 熔断优先显示：链路明明「运行中」却发不出去时，这一行才说得清问题
            int circuit = mesh.CircuitSeconds(link);
            if (enabled && running && circuit > 0) state = L10n.T("熔断中 · ") + circuit + L10n.T(" 秒后重试");

            string last;
            long lastMs = Math.Max(s.LastRxMs, s.LastTxMs);
            if (lastMs <= 0) last = L10n.T("无");
            else
            {
                long sec = Math.Max(0, (nowMs - lastMs) / 1000);
                last = sec < 60 ? sec + L10n.T(" 秒前") : (sec < 3600 ? (sec / 60) + L10n.T(" 分钟前") : (sec / 3600) + L10n.T(" 小时前"));
            }

            var d = new Dictionary<string, string>();
            d["link"] = link;
            d["name"] = label;
            d["state"] = state;
            d["ok"] = (enabled && running) ? "1" : "0";
            d["peers"] = peers.ToString();
            d["rx"] = s.Rx.ToString();
            d["tx"] = s.Tx.ToString();
            d["relayIn"] = s.RelayIn.ToString();
            d["relayOut"] = s.RelayOut.ToString();
            d["drop"] = s.Drop.ToString();
            d["last"] = last;
            d["err"] = s.LastError ?? "";
            // 链路质量与重传（参考 qaul 的 rtt / lq 与重传循环）
            d["rtt"] = s.RttMs > 0 ? ((int)Math.Round(s.RttMs)).ToString() : "";
            d["retrans"] = s.Retrans.ToString();
            d["expired"] = s.Expired.ToString();
            d["defer"] = s.RateDefer.ToString();
            d["circuit"] = circuit.ToString();
            return d;
        }

        private int BtNeighborCount()
        {
            try
            {
                var list = BitchatNeighborList();
                return list == null ? 0 : list.Count;
            }
            catch { return 0; }
        }

        #region Mchat 独有加密层（LC1）：口令派生 + 密钥交换 + 逐条棘轮

        private const string ChannelKeysPref = "lc1ChannelKeys";

        private void LoadChannelPasswords()
        {
            var prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
            var json = prefs.GetString(ChannelKeysPref, null);
            if (string.IsNullOrEmpty(json)) return;
            var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (map == null) return;
            foreach (var kv in map)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                lc1.SetPassword(kv.Key, kv.Value);
            }
        }

        private void SaveChannelPasswords()
        {
            try
            {
                var prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
                var map = new Dictionary<string, string>();
                foreach (var conv in lc1.ProtectedConversations())
                {
                    var pwd = lc1.PasswordOf(conv);
                    if (!string.IsNullOrEmpty(pwd)) map[conv] = pwd;
                }
                prefs.Edit().PutString(ChannelKeysPref, JsonConvert.SerializeObject(map)).Apply();
            }
            catch (Exception ex) { Android.Util.Log.Warn("LC1", "保存频道密码失败: " + ex.Message); }
        }

        /// <summary>给某个会话/频道设置加密密码（空密码 = 取消加密）。</summary>
        public bool SetChannelPassword(string conv, string password)
        {
            conv = string.IsNullOrEmpty(conv) ? activeConv : conv;
            if (string.IsNullOrEmpty(conv)) return false;

            if (string.IsNullOrEmpty(password))
            {
                lc1.Clear(conv);
                SaveChannelPasswords();
                Emit("lc1", GetChannelCrypto(conv));
                return true;
            }

            lc1.SetPassword(conv, password);
            SaveChannelPasswords();
            // 单层加密没有握手：设完密码立刻就能通，顺手广播一帧信标让对端尽快发现本机
            try { mid.BeaconNow(); } catch { }
            Emit("lc1", GetChannelCrypto(conv));
            return true;
        }

        public bool ClearChannelPassword(string conv)
        {
            return SetChannelPassword(conv, null);
        }

        /* ------------------------------------------------------------------
         * qaul 私聊（qaul:<remotePeerId>）的 LC1 密钥修复（错误1）
         *
         * 旧逻辑：会话键按「本机看到的对方 PeerId」命名，两台手机算出的字符串
         * 必然不同（A 眼里的 B = 12D3KooWH3…，B 眼里的 A = 12D3KooWAe…）。而 LC1 的
         * 密钥（PBKDF2 盐 / HKDF info）与 AAD 全都绑定 conv 会话串，于是同一对聊天的
         * 两端，密钥与 AAD 永远对不上 → 永久「本频道未设置密码 / 无法解密」。
         *
         * 新逻辑：加解密所用的「加密标签」改为对称的 QaulDmCryptoConv(selfQaulId,
         * remoteQaulId)（两端各拿双方公钥指纹排序后拼接，字符串一致），并基于它确定性派生
         * 共享密码。会话的「存储键」仍用各端视角下的 qaul:<remotePeerId>（不动历史消息与
         * 现有路由 / UI 逻辑），只有加密层用对称标签。
         * ------------------------------------------------------------------ */
        private string QaulDmDerivedPassword(string remotePeerB58)
        {
            return QaulDmDerivedPassword(MyQaulId(), remotePeerB58);
        }

        /// <summary>本端当前的 qaul 账号 id（取不到时退化成局域网 peerId）。</summary>
        private string MyQaulId()
        {
            return (qnode != null && qnode.Messaging != null && !string.IsNullOrEmpty(qnode.Messaging.MyIdB58))
                ? qnode.Messaging.MyIdB58
                : (peerId ?? "");
        }

        /// <summary>
        /// 本端历代 qaul 账号 id：当前在前，历史在后。
        ///
        /// qaul 把账号长期保存在 user_accounts（rust/libqaul/src/node/user_accounts.rs），
        /// 换账号后旧会话仍归旧 id、仍能解密。我们原来只认当前 PeerId：一旦换指纹，
        /// 用旧身份加密的历史私聊就再也算不出 LC1 密钥 → 永久 [无法解密]。
        /// 派生只需要 id 字符串（不需要私钥），所以存档 PeerId 文本就够回溯。
        /// </summary>
        private List<string> QaulIdCandidates()
        {
            var list = new List<string>();
            var cur = MyQaulId();
            if (!string.IsNullOrEmpty(cur)) list.Add(cur);
            try
            {
                if (identity != null)
                    foreach (var h in identity.QaulIdHistory)
                        if (!string.IsNullOrEmpty(h) && !list.Contains(h)) list.Add(h);
            }
            catch { }
            return list;
        }

        /// <summary>qaul 私聊的候选加密标签（当前身份在前、历代在后）。</summary>
        private List<string> QaulDmCryptoConvCandidates(string conv)
        {
            var list = new List<string>();
            if (!Convs.IsQaulDm(conv)) { list.Add(QaulDmCryptoConvOf(conv)); return list; }
            var remote = Convs.QaulPeerOf(conv);
            foreach (var self in QaulIdCandidates())
            {
                var tag = Convs.QaulDmCryptoConv(self, remote);
                if (!list.Contains(tag)) list.Add(tag);
            }
            if (list.Count == 0) list.Add(conv);
            return list;
        }

        private static string QaulDmDerivedPassword(string self, string remotePeerB58)
        {
            var a = self ?? "";
            var b = remotePeerB58 ?? "";
            // 排序保证两端顺序一致：A 算 sort(A,B)，B 算 sort(B,A) = sort(A,B)
            var pair = string.CompareOrdinal(a, b) <= 0 ? (a + "|" + b) : (b + "|" + a);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes("Mchat/qaul-dm/v1:" + pair));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var x in bytes) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>qaul 私聊的对称加密标签：发送/接收两端用同一个标签去算 LC1 密钥与 AAD。</summary>
        private string QaulDmCryptoConvOf(string conv)
        {
            if (!Convs.IsQaulDm(conv) || qnode == null || qnode.Messaging == null
                || string.IsNullOrEmpty(qnode.Messaging.MyIdB58)) return conv;
            return Convs.QaulDmCryptoConv(qnode.Messaging.MyIdB58, Convs.QaulPeerOf(conv));
        }

        /// <summary>确保某条 qaul 私聊会话已装载对称派生的 LC1 密码（发送/接收前都要调）。</summary>
        private void EnsureQaulDmKey(string remotePeerB58)
        {
            if (string.IsNullOrEmpty(remotePeerB58)) return;
            if (qnode == null || qnode.Messaging == null || string.IsNullOrEmpty(qnode.Messaging.MyIdB58)) return;

            // 每次都重设（幂等）：确保 qaul DM 永远用对称派生密码，不会被 UI 手设密码打乱。
            // 同时对**历代身份**各设一遍：换过指纹后，旧会话（存储键仍是 qaul:<旧对端 id>）
            // 的标签是按旧身份算的，只设当前身份会让历史消息永远 locked。
            foreach (var self in QaulIdCandidates())
            {
                var cryptoConv = Convs.QaulDmCryptoConv(self, remotePeerB58);
                lc1.SetPassword(cryptoConv, QaulDmDerivedPassword(self, remotePeerB58));
            }
        }

        /// <summary>安全码：双方线下比对，一致才说明用的是同一个密码。</summary>
        public string ChannelSafetyNumber(string conv)
        {
            return lc1.SafetyNumber(string.IsNullOrEmpty(conv) ? activeConv : conv);
        }

        /// <summary>某会话当前设置的加密密码（界面回填用）。</summary>
        public string ChannelPasswordOf(string conv)
        {
            return lc1.PasswordOf(string.IsNullOrEmpty(conv) ? activeConv : conv);
        }

        /// <summary>当前会话是否已启用 LC1 加密。</summary>
        public bool IsConvEncrypted(string conv)
        {
            return lc1.Has(string.IsNullOrEmpty(conv) ? activeConv : conv);
        }

        /// <summary>界面/网页端读的加密状态。</summary>
        public object GetChannelCrypto(string conv)
        {
            conv = string.IsNullOrEmpty(conv) ? activeConv : conv;
            return new
            {
                conv,
                enabled = lc1.Has(conv),
                handshaked = lc1.IsHandshaked(conv),
                status = lc1.StatusText(conv),
                safety = lc1.SafetyNumber(conv)
            };
        }

        /// <summary>
        /// 中间层扇出：把一段**已加密**的载荷投递到该会话的所有活动链路，不入库、不显示。
        /// 设备发现信标走的就是这条路——qaul 那条链路会同时直投 + 交给 libp2p 中继，
        /// 所以即使两台设备不直连，信标也能被中转过去。
        ///
        /// 省电参数：
        ///   preferUdp  —— 局域网链路改用 UDP 组播/广播，而不是给每个 peer 各开一条 TCP。
        ///                 信标是周期性的小包，为此每隔几秒向 N 个 peer 发起 TCP 握手极不划算。
        ///   maxRelay   —— 经 qaul 中继的节点数上限（默认 32，信标只投前面几个就够扩散）。
        /// </summary>
        private void FanoutRaw(string conv, string armored, bool preferUdp = false, int maxRelay = 32)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(armored)) return;
            try
            {
                // qaul 私聊：走 qaul 的路由/中继
                if (Convs.IsQaulDm(conv))
                {
                    var peer = Convs.QaulPeerOf(conv);
                    if (UseQaulNativeMessaging && qnode != null && qnode.Messaging != null && !string.IsNullOrEmpty(peer))
                        qnode.Messaging.SendDm(peer, armored);
                    return;
                }

                if (Convs.IsChan(conv))
                {
                    var ch = channels.ByConv(conv);
                    var links = EffectiveLinks(ch);
                    foreach (var link in links)
                    {
                        try
                        {
                            switch (link)
                            {
                                // qaul 是主链路：近处直投 + 远处交给中继，两条都走
                                case ChanLink.Qaul:
                                    SendQaulRaw(conv, armored);
                                    if (UseQaulNativeMessaging) SendQaulViaNode(conv, armored, maxRelay);
                                    break;
                                case ChanLink.Lan:
                                    // mesh 信标同样不得经静态中转站外发
                                    if (preferUdp && SendDiscoveryEnvelope(conv, armored)) break;
                                    BroadcastFrame(RawFrame(conv, armored), null, conv, IsMeshConv(conv));
                                    break;
                                case ChanLink.P2p:
                                    if (p2pEnabled && p2p != null) p2p.BroadcastFrame(RawFrame(conv, armored), null, tcpPort);
                                    break;
                                case ChanLink.Bitchat:
                                    if (bitchat != null && bitchat.Running)
                                        bitchat.SendToRoom(Convs.ChanName(conv) ?? Convs.DefaultRoom, armored, displayName);
                                    break;
                            }
                        }
                        catch { }
                    }
                    return;
                }

                // 普通房间 / Mchat 私聊
                SendQaulRaw(conv, armored);
                if (UseQaulNativeMessaging) SendQaulViaNode(conv, armored, maxRelay);
                if (!(preferUdp && SendDiscoveryEnvelope(conv, armored)))
                    BroadcastFrame(RawFrame(conv, armored), null);
            }
            catch (Exception ex) { Android.Util.Log.Warn("LC1", "中间层扇出失败: " + ex.Message); }
        }

        /// <summary>
        /// 借发现端口用 UDP 组播/广播发一段中间层载荷（设备信标）。
        /// 周期性的小包走 UDP 一发就完，比为每个 peer 各建立一条 TCP 省得多。
        /// 返回 false 表示 UDP 不可用，调用方应回退到 TCP。
        /// </summary>
        private bool SendDiscoveryEnvelope(string conv, string armored)
        {
            if (discoverySocket == null || !scanEnabled) return false;
            try
            {
                var mcastEp = new IPEndPoint(IPAddress.Parse(MulticastAddress), DiscoveryPort);
                var bcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
                var pkt = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    t = "lcbeacon",
                    id = peerId,
                    conv,
                    p = armored
                }));
                if (pkt.Length > 1400) return false;      // 超过 MTU 就别走 UDP 了
                try { discoverySocket.Send(pkt, pkt.Length, mcastEp); } catch { }
                try { discoverySocket.Send(pkt, pkt.Length, bcastEp); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>UDP 上收到一条中间层信标：解密后交给中间层登记/中继，不进聊天记录。</summary>
        private void OnUdpBeacon(string conv, string armored)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(armored)) return;
            try
            {
                string json;
                if (lc1.TryOpenBeacon(conv, armored, out json) && !string.IsNullOrEmpty(json))
                {
                    mid.Handle(conv, ChanLink.Lan, json);
                    return;
                }
                // 频道设了密码：信标是用频道密钥封的，再试一次频道密钥
                string err;
                bool viaPublic;
                if (lc1.TryOpen(conv, armored, "", out json, out err, out viaPublic) == 1 && !string.IsNullOrEmpty(json))
                    mid.Handle(conv, ChanLink.Lan, json);
            }
            catch { }
        }

        #region Mchat 中间层：隔除 qaul 窥视 + 自带设备发现

        /// <summary>装配中间层的钩子，并启动信标广播。</summary>
        private void StartMidLayer()
        {
            try
            {
                mid.SelfId = () => peerId;
                mid.SelfName = () => displayName;
                mid.QaulId = () => { var i = Identity; return i == null ? "" : i.QaulPeerId; };
                mid.BitchatId = () => { var i = Identity; return i == null ? "" : i.BitchatPeerId; };
                mid.BeaconConvs = MidBeaconConvs;
                // 信标走 UDP（局域网）+ 中继只投前 6 个节点：
                // 信标是周期性小包，给它建立 TCP 连接、或者投给几十个节点，性价比极低。
                mid.Send = (conv, text) => FanoutRaw(conv, text, true, 6);
                mid.SealBeacon = (conv, json) => lc1.SealBeacon(conv, json);
                mid.OpenBeacon = (conv, text) =>
                {
                    string json;
                    return lc1.TryOpenBeacon(conv, text, out json) ? json : null;
                };
                mid.Emit = (t, p) => Emit(t, p);
                mid.Log = m => Android.Util.Log.Info("LcMid", m);

                mid.Start(cts.Token);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("LcMid", "中间层启动失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 需要广播信标的会话：默认频道（保证任何两台 Mchat 都能互相发现）
        /// + 所有已加入的频道 + 当前会话。
        /// </summary>
        private List<string> MidBeaconConvs()
        {
            var list = new List<string>();
            try
            {
                // 默认频道必须发：它是"任意两台 Mchat 都能互相发现"的保底入口
                list.Add(Convs.Chan(ChannelRegistry.DefaultChannelName));
                if (!string.IsNullOrEmpty(activeConv) && !Convs.IsDm(activeConv)) list.Add(activeConv);
            }
            catch { }

            // 上限 3 个：信标会按频道数线性放大（每频道 × 每链路 × 每节点），
            // 加一堆频道就会变成持续的射频+CPU 唤醒源。保底频道 + 当前会话足够完成发现。
            const int MaxBeaconConvs = 3;
            var result = list.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToList();
            if (result.Count <= MaxBeaconConvs) return result;
            return result.Take(MaxBeaconConvs).ToList();
        }

        /// <summary>立刻广播一帧发现信标（界面「重新发现」按钮用）。</summary>
        public void MidBeaconNow()
        {
            try { mid.BeaconNow(); } catch (Exception ex) { Android.Util.Log.Warn("LcMid", ex.Message); }
        }

        /// <summary>中间层发现到的在线设备（界面与网页端共用）。</summary>
        public List<object> GetLcDevices()
        {
            try { return mid.DeviceList(); } catch { return new List<object>(); }
        }

        public string GetLcDevicesJson()
        {
            return JsonConvert.SerializeObject(GetLcDevices());
        }

        /// <summary>中间层状态：是否运行、广播了多少帧、发现几台、其中几台是经中继发现的。</summary>
        public object GetLcMidStatus()
        {
            try { return mid.Status(); } catch { return new { running = false }; }
        }

        public string GetLcMidStatusJson()
        {
            return JsonConvert.SerializeObject(GetLcMidStatus());
        }

        [Java.Interop.Export("getLcDevices")]
        [JavascriptInterface]
        public string GetLcDevicesJs() { return GetLcDevicesJson(); }

        [Java.Interop.Export("getLcMidStatus")]
        [JavascriptInterface]
        public string GetLcMidStatusJs() { return GetLcMidStatusJson(); }

        [Java.Interop.Export("lcBeacon")]
        [JavascriptInterface]
        public void MidBeaconNowJs() { MidBeaconNow(); }

        /// <summary>按中间层发现到的设备打开一个 Mchat 私聊。</summary>
        public string OpenLcDeviceDm(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(peerId)) return null;
            var conv = Convs.Dm(peerId, id);
            try { if (!string.IsNullOrEmpty(name)) store.SetTitle(conv, name); } catch { }
            return conv;
        }

        #endregion

        /// <summary>从入站消息里猜它走的是哪条链路（用于中间层记设备来源）。</summary>
        private static string GuessLink(Dictionary<string, object> msg)
        {
            if (msg == null) return null;
            object v;
            if (msg.TryGetValue("link", out v) && v != null)
            {
                var s = v.ToString();
                if (ChanLink.IsKnown(s)) return s;
            }
            if (msg.TryGetValue("src", out v) && v != null)
            {
                var s = v.ToString();
                if (s.IndexOf("bt", StringComparison.OrdinalIgnoreCase) >= 0) return ChanLink.Bitchat;
                if (s.IndexOf("p2p", StringComparison.OrdinalIgnoreCase) >= 0) return ChanLink.P2p;
                if (s.IndexOf("qaul", StringComparison.OrdinalIgnoreCase) >= 0) return ChanLink.Qaul;
                if (s.IndexOf("lan", StringComparison.OrdinalIgnoreCase) >= 0) return ChanLink.Lan;
            }
            return null;
        }

        /// <summary>构造一条"不入库"的裸帧（用于控制帧转发）。</summary>
        private string RawFrame(string conv, string text)
        {
            var m = Msg.New(conv, peerId, displayName, "lc1ctl", text);
            m["lc1ctl"] = true;
            return JsonConvert.SerializeObject(m);
        }

        /// <summary>
        /// 出站加密：把正文/昵称/类型打成一个 JSON，用 LC1 加密后返回可上信道的字符串。
        /// 频道没设密码时原样返回明文（不加密）。
        /// </summary>
        private string SealOutgoing(string conv, Dictionary<string, object> msg)
        {
            var plain = Msg.Str(msg, "text") ?? "";
            if (string.IsNullOrEmpty(conv)) return plain;

            // qaul 私聊：LC1 的密钥与 AAD 都绑定 conv，而 qaul DM 的存储会话键两端不同，
            // 必须改用对称加密标签，否则两端密钥/AAD 对不上 → 永久「无法解密」。
            var cryptoConv = QaulDmCryptoConvOf(conv);
            if (!lc1.Has(cryptoConv)) return plain;

            var payload = new Dictionary<string, object>
            {
                ["t"] = plain,
                ["n"] = Msg.Str(msg, "fromName") ?? "",
                ["k"] = Msg.Str(msg, "kind") ?? Msg.TextKind
            };
            var url = Msg.Str(msg, "url");
            if (!string.IsNullOrEmpty(url)) payload["u"] = url;
            var name = Msg.Str(msg, "name");
            if (!string.IsNullOrEmpty(name)) payload["f"] = name;

            var sealedText = lc1.Seal(cryptoConv, JsonConvert.SerializeObject(payload));
            return sealedText ?? plain;
        }

        /// <summary>把消息字典复制一份、把正文换成已加密的线上文本（本地存储仍留明文）。</summary>
        private static Dictionary<string, object> WireCopy(Dictionary<string, object> m, string wireText)
        {
            var copy = new Dictionary<string, object>(m, StringComparer.Ordinal);
            copy["text"] = wireText;
            copy["lc1"] = true;
            return copy;
        }

        /// <summary>
        /// 走真正的 qaul 节点（libp2p）投递：没有直连时交给 qaul 的路由表逐跳中继。
        /// qaul 没有群播，房间/频道按 qaul 自己的做法——给每个已知节点各发一份单播，
        /// 每一份都能被中间节点转发，因此不相邻的两个人也能互通。
        /// </summary>
        private void SendQaulViaNode(string conv, string wire, int maxTargets = 32, uint? fixedIndex = null)
        {
            if (qnode == null || qnode.Messaging == null || string.IsNullOrEmpty(wire)) return;
            var mm = qnode.Messaging;

            try
            {
                if (Convs.IsQaulDm(conv))
                {
                    var peer = Convs.QaulPeerOf(conv);
                    if (!string.IsNullOrEmpty(peer)) mm.SendDm(peer, wire);
                    return;
                }

                if (Convs.IsQaulGroup(conv))
                {
                    mm.SendGroupText(Convs.QaulGroupIdOf(conv), wire);
                    return;
                }

                // 频道 / 房间：逐个已知 qaul 节点投递（可中继）。
                // 关键：带上频道自己的 group_id，否则对端会把这条消息认成 qaul 私聊，
                // 频道消息与中间层信标都会掉进私聊会话里。
                //
                // 「只有非直连设备才启用中继」——已被 Mchat 帧直投覆盖的节点不再走
                // 一次 libp2p 中继：两条链路的消息 id 不同（帧 = GUID、qaul-native =
                // CRC64 派生命中 id），对端 ChatStore 按 id 跨渠道去重救不了重复，
                // 同一条消息会在会话里出两遍。每一条中继还是一次 Noise 加密 + 一次
                // TCP 往返，对已直投的设备纯属浪费。
                // 直连判据两条，互为备份：
                //   ① IP 命中：Mchat 帧目标（QaulTargets）与 qaul 邻居 advertised 地址同主机；
                //   ② qaul 公钥指纹命中：多网卡 / 地址不一致时 IP 会漏，指纹不会
                //      （PeerId 内嵌公钥，与 UDP hello 里学到的 QaulKeyShort 比对）。
                // 注意：判据只在「Mchat 帧渠道真的可用」时才算数——qaul 帧渠道没跑起来时
                // 直连集必须为空，否则消息被两边一起跳过，谁都发不出去。
                var directIp = new HashSet<string>(StringComparer.Ordinal);
                var directKey = new HashSet<string>(StringComparer.Ordinal);
                if (qaul != null && qaul.Running)
                {
                    var nowTick = SysEnv.TickCount;
                    try
                    {
                        foreach (var ep in QaulTargets(conv))
                            if (ep != null && ep.Address != null) directIp.Add(ep.Address.ToString());
                    }
                    catch { }
                    foreach (var p in peers.Values)
                    {
                        if (p == null || string.IsNullOrEmpty(p.QaulKeyShort)) continue;
                        if (nowTick - p.LastSeen >= PeerTimeoutMs || p.QaulPort <= 0) continue;
                        directKey.Add(p.QaulKeyShort);
                    }
                }

                var gid = Convs.GroupId(conv);
                // 频道中继的 index 同样必须用「持久化单调」取号器：原来是进程内计数器，
                // 重启后从 1 重新开始，会与上一轮发出的 message id 完全撞车
                // （对端 ChatStore 按 id 去重 → 重启后的头几条频道消息被整条丢掉）。
                //
                // fixedIndex：调用方（SendInChannel）已经为本条消息取好了号并**用它落库**，
                // 这里必须复用同一个 index——否则发送端落库 id 与线上 id 又是两套，
                // 对端按 id 去重时救不了发送端自己（中继绕回来 → 重影）。
                uint idx = fixedIndex ?? mm.NextIndex("relay:" + conv);
                var me = mm.MyIdB58;
                int sent = 0;

                foreach (var n in qnode.Neighbours())
                {
                    if (sent >= maxTargets) break;

                    object op;
                    if (!n.TryGetValue("peer", out op)) continue;
                    var peer = op as string;
                    if (string.IsNullOrEmpty(peer) || peer == me) continue;
                    try { if (!mm.HaveKey(peer)) continue; } catch { continue; }

                    if (directIp.Count > 0 || directKey.Count > 0)
                    {
                        object oa, on;
                        n.TryGetValue("addr", out oa);
                        if (directIp.Count > 0 && directIp.Contains(HostOf(oa as string))) continue;

                        n.TryGetValue("node", out on);
                        var ks = QaulKeyShortOf(on as string);
                        if (ks != null && directKey.Contains(ks)) continue;
                    }

                    try { if (mm.SendToPeerInGroup(peer, gid, wire, idx)) sent++; } catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Qaul", "qaul 中继投递失败: " + ex.Message);
            }
        }

        // 频道中继的序号已改用 QaulMessaging.NextIndex（持久化单调），
        // 不再用进程内计数器——重启后计数归零会与已发出的 message id 撞车。

        /// <summary>从 "1.2.3.4:9200" 里取出主机部分（用于判断"这个地址是否已直投过"）。</summary>
        private static string HostOf(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return "";
            int i = addr.LastIndexOf(':');
            return i > 0 ? addr.Substring(0, i) : addr;
        }

        /// <summary>
        /// 从 libp2p 节点 PeerId（38 字节 identity-multihash，公钥内嵌在 6..38）
        /// 解出 qaul 公钥前 16 hex，与 UDP hello 里学到的 <c>peers[].QaulKeyShort</c> 比对。
        /// 判「这个 qaul 邻居是不是那台已经能直投的设备」用指纹比用 IP 可靠：
        /// 多网卡 / NAT / 地址刷新时 IP 会变，公钥不会。
        /// </summary>
        private static string QaulKeyShortOf(string nodeB58)
        {
            if (string.IsNullOrEmpty(nodeB58)) return null;
            try
            {
                var b = Base58.Decode(nodeB58);
                if (b == null || b.Length != 38) return null;
                return LocalIdentity.Hex(Curve25519.Slice(b, 6, 32)).Substring(0, 16);
            }
            catch { return null; }
        }

        /// <summary>可作为 qaul 中继目标的节点：已换到公钥、且不是自己。</summary>
        private List<string> QaulRelayTargets()
        {
            var list = new List<string>();
            try
            {
                if (qnode == null || qnode.Messaging == null) return list;
                var me = qnode.Messaging.MyIdB58;
                foreach (var n in qnode.Neighbours())
                {
                    var peer = n.ContainsKey("peer") ? (string)n["peer"] : null;
                    if (string.IsNullOrEmpty(peer) || peer == me) continue;
                    if (!qnode.Messaging.HaveKey(peer)) continue;
                    list.Add(peer);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 入站解密：把密文正文还原成明文，并把昵称/类型放回消息字典。
        /// 返回 true 表示这是中间层的控制/发现帧（已在内部消化，调用方不应再当聊天消息处理）。
        /// </summary>
        private bool UnsealIncoming(string conv, Dictionary<string, object> msg, string senderId, string link = null)
        {
            if (msg == null) return false;
            var text = Msg.Str(msg, "text") ?? "";

            // qaul 私聊：用对称加密标签去解（与发送端 Sealing 用的标签一致）
            var cryptoConv = QaulDmCryptoConvOf(conv);
            // 换过指纹时，历史密文是用**旧身份**的标签加密的。候选标签 = 当前 + 历代。
            var convCandidates = Convs.IsQaulDm(conv) ? QaulDmCryptoConvCandidates(conv) : null;

            if (!MchatSeal.IsSealed(text))
            {
                // 加密频道里收到明文：显式标记，提醒用户这条没走加密
                if (lc1.Has(cryptoConv)) msg["enc"] = "plain";
                return false;
            }

            string json, error;
            bool viaPublic;
            var r = lc1.TryOpen(cryptoConv, text, senderId, out json, out error, out viaPublic);

            // 当前身份解不开 → 逐个试历代身份的标签（本端换过指纹时留下的历史私聊）
            if (r != 1 && convCandidates != null)
            {
                foreach (var tag in convCandidates)
                {
                    if (tag == cryptoConv) continue;
                    string j2, e2;
                    bool vp2;
                    var r2 = lc1.TryOpen(tag, text, senderId, out j2, out e2, out vp2);
                    if (r2 == 1)
                    {
                        cryptoConv = tag; json = j2; error = e2; viaPublic = vp2; r = r2;
                        break;
                    }
                }
            }

            if (r == 1)
            {
                // 中间层信标：登记设备并按需中继扩散，**不进聊天记录**
                try { if (mid.Handle(conv, link ?? GuessLink(msg), json)) return true; }
                catch (Exception ex) { Android.Util.Log.Warn("LcMid", "信标处理失败: " + ex.Message); }

                // 公开发现密钥解出来的非信标内容不展示（那不是聊天消息）
                if (viaPublic && !lc1.Has(conv))
                {
                    msg["enc"] = "mid";
                    return true;
                }
            }

            if (r != 1)
            {
                // qaul 私聊密钥已装载却仍解不开：几乎一定是对端 qaul 身份变更过
                // （重装清数据 / 换指纹），这是“旧设备”留下的历史密文，按设计无法解密。
                // 给一句清楚的说明，别再用会误导的“频道密码可能不一致”。
                string shown;
                if (Convs.IsQaulDm(conv) && lc1.Has(cryptoConv))
                    shown = L10n.T("[无法解密] 来自此前的设备身份（对端重装或更换指纹后，历史消息按设计无法解密）");
                else
                    shown = L10n.T("[无法解密] ") + (error ?? L10n.T("频道密码可能不一致"));
                msg["enc"] = "locked";
                msg["encError"] = error ?? L10n.T("无法解密");
                msg["text"] = shown;
                return false;
            }

            try
            {
                var p = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (p != null)
                {
                    object v;
                    if (p.TryGetValue("t", out v)) msg["text"] = v == null ? "" : v.ToString();
                    if (p.TryGetValue("n", out v)) msg["fromName"] = v == null ? "" : v.ToString();
                    if (p.TryGetValue("k", out v)) msg["kind"] = v == null ? Msg.TextKind : v.ToString();
                    if (p.TryGetValue("u", out v)) msg["url"] = v == null ? "" : v.ToString();
                    if (p.TryGetValue("f", out v)) msg["name"] = v == null ? "" : v.ToString();
                }
            }
            catch { }

            msg["enc"] = "lc1";
            return false;
        }

        #endregion

        #region bitchat BLE mesh（聚合 qaul + bitchat 的传输节点）

        private bool LoadBitchatPref()
        {
            var prefs = context.GetSharedPreferences("Mchat", FileCreationMode.Private);
            return prefs.GetBoolean("bitchatEnabled", false);
        }

        /// <summary>蓝牙权限未在运行时授予时，由菜单触发申请；grant 后自动启用。</summary>
        public void EnableBitchat()
        {
            try
            {
                if (bitchat == null) bitchat = new BitchatTransport(CurrentActivity, Identity);
                if (bitchat.NeedsPermission())
                {
                    bitchat.RequestPermission(granted =>
                    {
                        if (granted)
                        {
                            if (bitchat.Enable())
                            {
                                try { context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                                          .Edit().PutBoolean("bitchatEnabled", true).Apply(); } catch { }
                                Emit("bitchat", GetBitchatStatus());
                            }
                        }
                        else
                        {
                            Emit("error", new { message = L10n.T("未授予蓝牙权限，蓝牙无法启用") });
                        }
                    });
                    return;
                }
                if (bitchat.Enable())
                {
                    bitchatEnabled = true;
                    try { context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                              .Edit().PutBoolean("bitchatEnabled", true).Apply(); } catch { }
                    Emit("bitchat", GetBitchatStatus());
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Bitchat", "启用失败: " + ex.Message);
            }
        }

        /// <summary>关闭 bitchat BLE mesh。</summary>
        public void DisableBitchat()
        {
            try
            {
                bitchat?.Disable();
                bitchatEnabled = false;
                try { context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                          .Edit().PutBoolean("bitchatEnabled", false).Apply(); } catch { }
                Emit("bitchat", GetBitchatStatus());
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Bitchat", "关闭失败: " + ex.Message);
            }
        }

        public bool BitchatEnabled => bitchatEnabled && bitchat != null && bitchat.Running;

        /// <summary>Activity 的 OnRequestPermissionsResult 转交到这里。</summary>
        public static void OnPermissionResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            try
            {
                if (requestCode == ReqNearbyWifi)
                {
                    bool ok = grantResults != null && grantResults.Length > 0
                              && grantResults[0] == Android.Content.PM.Permission.Granted;
                    var inst = BridgeInstance;
                    if (ok)
                    {
                        try { inst?.ApplyFullChannelAccess(); } catch { }
                    }
                    else
                    {
                        try { inst.fullChannel = false; inst?.SaveFullChannelPref(false); } catch { }
                    }
                    return;
                }
                BitchatTransport.OnPermissionResult(requestCode, permissions, grantResults);
            }
            catch { }
        }

        /// <summary>bitchat 入站消息：落地到统一会话仓库并发事件（与 qaul 私聊同构）。</summary>
        private void OnBitchatMessage(string conv, string fromPeer, string fromName, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 【中继信封】从别的链路（qaul / WLAN Direct / 局域网）中转过来的整帧：
            // 里面自带 id / conv / hop，直接交给统一入口，绝不能当成一条新消息重新生成 id
            // —— 否则同一句话会在每个中转设备上变一次 id，去重全部失效、聊天记录里重影。
            if (text.StartsWith(MeshEnvPrefix, StringComparison.Ordinal))
            {
                var relayed = DecodeMeshEnvelope(text);
                if (relayed != null)
                {
                    try { IngestIncoming(relayed, null, fromPeer, ChanLink.Bitchat); }
                    catch (Exception ex) { Android.Util.Log.Warn("Mesh", "蓝牙中继帧处理失败: " + ex.Message); }
                    return;
                }
            }

            try
            {
                // 蓝牙只是**一种链路**：BLE 的房间消息统一落到 chan:<room> 这个频道，
                // 于是蓝牙收到的内容会和 qaul / WLAN Direct / 局域网收到的是同一个聊天室。
                if (Convs.IsBitchatRoom(conv))
                {
                    var roomCode = Convs.BitchatRoomCode(conv) ?? Convs.DefaultRoom;
                    conv = Convs.Chan(roomCode);
                    channels.Ensure(roomCode);
                }

                string title = Convs.TitleOf(conv);
                store.SetTitle(conv, title);

                var m = Msg.New(conv, fromPeer, string.IsNullOrEmpty(fromName) ? fromPeer : fromName, "text", text);
                m["id"] = LocalIdentity.Hex(LocalIdentity.Sha256(
                    System.Text.Encoding.UTF8.GetBytes(conv + fromPeer + text + (m["time"] ?? 0))));
                m["src"] = "bitchat";

                // LC1：蓝牙链路搬的同样是密文，先解密（密钥交换帧在这里消化）
                if (UnsealIncoming(conv, m, fromPeer, ChanLink.Bitchat)) return;
                text = Msg.Str(m, "text");

                _dedupEngine.MarkSeen(Msg.Str(m, "id"));
                store.Add(m, true);
                AppendHistory(m);
                Emit("message", m);

                // 同步到该频道其它已启用的链路（qaul / WLAN Direct / 局域网）
                if (Convs.IsChan(conv)) SyncAcrossLinks(conv, m, null, ChanLink.Bitchat);

                // 聚合 + 隧道：bitchat 公共频道 ↔ qaul 公共 feed 双向桥接，
                // 让「只在 qaul」或「只在 bitchat」的邻居也能看到对方的公屏消息。
                // 前缀 + 全局 seen 去重共同保证不会回环。
                if (bitchatEnabled && !text.StartsWith("[qaul] ")
                    && Convs.IsChan(conv) && qnode != null && qnode.Messaging != null)
                {
                    try { PublishQaulFeed("[bt] " + (string.IsNullOrEmpty(fromName) ? fromPeer : fromName) + ": " + text); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Bitchat", "写入 bitchat 消息失败: " + ex.Message);
            }
        }

        /// <summary>bitchat 会话发送：在 bitchat 网络里按会话类型投递；同时若 qaul 端有同名联系人则经 qaul 隧道转发。</summary>
        private Dictionary<string, object> SendBitchatInConv(string conv, string text, string kind)
        {
            var m = Msg.New(conv, peerId, displayName, "text", text);
            m["src"] = "bitchat-out";
            _dedupEngine.MarkSeen(Msg.Str(m, "id"));
            store.Add(m, true);
            AppendHistory(m);
            Emit("message", m);

            // LC1：蓝牙上传输的内容先加密
            var wire = SealOutgoing(conv, m);

            if (bitchat != null && bitchat.Running)
            {
                try
                {
                    if (Convs.IsBitchatDm(conv))
                        bitchat.SendToDm(Convs.BitchatPeerOf(conv), wire, displayName);
                    else if (Convs.IsBitchatGroup(conv))
                        bitchat.SendToGroup(Convs.BitchatGroupIdOf(conv), wire, displayName);
                    else
                        bitchat.SendToRoom(Convs.BitchatRoomCode(conv), wire, displayName);
                }
                catch (Exception ex) { Android.Util.Log.Warn("Bitchat", "发送失败: " + ex.Message); }
            }
            return m;
        }

        public object GetBitchatStatus()
        {
            try
            {
                var peers = bitchat == null ? new List<BitchatTransport.Neighbor>() : bitchat.Neighbors();
                var list = new List<object>();
                foreach (var p in peers)
                {
                    list.Add(new
                    {
                        address = p.Address,
                        peerId = p.PeerId,
                        name = p.Name,
                        rssi = p.Rssi,
                        connected = p.Connected
                    });
                }
                return new
                {
                    enabled = BitchatEnabled,
                    running = bitchat != null && bitchat.Running,
                    peerId = Identity?.BitchatPeerId,
                    neighbors = list,
                    neighbourCount = list.Count
                };
            }
            catch { return new { enabled = false, running = false, neighbours = 0 }; }
        }

        /// <summary>供原生 UI 直接使用的类型化邻居列表。</summary>
        public List<BitchatTransport.Neighbor> BitchatNeighborList()
        {
            try { return bitchat == null ? new List<BitchatTransport.Neighbor>() : bitchat.Neighbors(); }
            catch { return new List<BitchatTransport.Neighbor>(); }
        }

        /// <summary>为某个会话设置标题（私聊用昵称补全、群聊用群名）。</summary>
        public void SetConvTitle(string conv, string title)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(title)) return;
            try { store.SetTitle(conv, title); } catch { }
        }

        #endregion

        /* ===================== 发送 ===================== */

        /// <summary>把一条消息同时投递到两条渠道（不在 UI 线程阻塞）。</summary>
        private void Deliver(Dictionary<string, object> msg, byte[] body)
        {
            if (msg == null) return;
            var conv = Msg.Conv(msg);
            // LC1：局域网这条链路同样是「只用渠道」，正文先加密再出门
            var wire = SealOutgoing(conv, msg);
            var wireMsg = WireCopy(msg, wire);

            // 与频道发送共用同一个出站队列：qaul 为主，开了「局域网直连」就再加一条。
            // 以前这里是 Task.Run + try/catch，发失败只写一行日志 —— 对端正在重连时消息就静默丢了。
            // 本程序自己的通道（局域网 TCP 帧）始终启用：这是最可靠、完全由我们控制的一条。
            // qaul 只作为补充/跨网段中继 —— 之前「DualChannel 关掉就只剩 qaul」，
            // qaul 的握手/加密一旦出问题，消息就彻底发不出去。
            var links = new List<string> { ChanLink.Qaul, ChanLink.Lan };
            EnqueueOutbound(conv, wire, wireMsg, body, links, false, null);
        }

        /// <summary>私聊：只向会话对端投递一帧（Mchat 渠道）。返回 false = 对端当前不可达。</summary>
        private bool SendFrameToDm(string conv, string payload, byte[] body)
        {
            var other = Convs.DmPeer(conv, peerId);
            if (string.IsNullOrEmpty(other)) return false;
            Peer p;
            if (!peers.TryGetValue(other, out p)) return false;
            if (SysEnv.TickCount - p.LastSeen >= PeerTimeoutMs) return false;
            SendFrameTo(p, payload, body);
            return true;
        }

        /// <summary>只用 qaul 渠道投递一条消息：正文先过 LC1 加密层再上信道。</summary>
        private void SendQaul(Dictionary<string, object> msg)
        {
            if (msg == null) return;
            var conv = Msg.Conv(msg);
            SendQaulRaw(conv, SealOutgoing(conv, msg), msg);
        }

        /// <summary>底层投递：正文原样上信道（调用方自行保证已加密或是控制帧）。返回是否真的发出去了。</summary>
        private bool SendQaulRaw(string conv, string text)
        {
            return SendQaulRaw(conv, text, null);
        }

        /// <summary>
        /// 返回 true 表示至少投递给了 1 个目标；false 表示当前没有任何可用目标（对端未发现/端口未知）。
        /// 出站队列靠这个返回值判断「没人可发」，避免把没发出去的消息当成已送达丢掉。
        /// </summary>
        private bool SendQaulRaw(string conv, string text, Dictionary<string, object> msg)
        {
            if (qaul == null || !qaul.Running) return false;

            var id = msg != null ? (Msg.Str(msg, "id") ?? Guid.NewGuid().ToString("N"))
                                 : Guid.NewGuid().ToString("N");
            var me = Identity;
            text = text ?? "";

            // 签名：qaul 的 Container 用 Ed25519 签名保证不可冒充，这里对 (消息id+正文) 签名，
            // 接收端用 user_id 里内嵌的同一把公钥验签。
            // 注意：签名覆盖的是「密文」，qaul 侧自始至终拿不到明文。
            var extra = new Dictionary<string, object>();
            if (me != null)
                extra["sig"] = LocalIdentity.Hex(Curve25519.Ed25519Sign(me.QaulSeed, SigningInput(id, text)));

            var qmsg = new QaulChatMessage
            {
                GroupId = Convs.GroupId(conv),
                // user_id 用 qaul 的 PeerId（38 字节 identity-multihash），与 qaul 的账号格式一致；
                // Mchat 的路由 id 仍在 meta 的 lid 里，老客户端靠它做私聊匹配。
                SenderId = me != null ? me.QaulPeerIdBytes : Encoding.UTF8.GetBytes(peerId),
                MessageId = Encoding.UTF8.GetBytes(id),
                SentAt = msg != null ? Msg.Num(msg, "time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                     : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = 1,                    // SENT
                Text = text,
                Meta = msg != null ? Msg.MetaJson(msg, extra) : Msg.MetaJson(Msg.New(conv, peerId, displayName, "text", text), extra)
            };
            if (qmsg.SentAt <= 0) qmsg.SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            qmsg.ReceivedAt = qmsg.SentAt;

            var frame = QaulWire.EncodeChatFrame(qmsg, id, qmsg.SenderId);
            var targets = QaulTargets(conv);
            if (targets.Count == 0) return false;
            try { qaul.SendFrame(targets, frame); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "qaul 发送失败：" + ex.Message);
                return false;
            }
            return true;
        }

        /// <summary>按会话算出 qaul 渠道的投递目标：房间=所有在线节点，私聊=仅对端。</summary>
        private List<IPEndPoint> QaulTargets(string conv)
        {
            var list = new List<IPEndPoint>();
            var now = SysEnv.TickCount;
            if (Convs.IsDm(conv))
            {
                var other = Convs.DmPeer(conv, peerId);
                Peer p;
                if (!string.IsNullOrEmpty(other) && peers.TryGetValue(other, out p)
                    && now - p.LastSeen < PeerTimeoutMs && p.QaulPort > 0)
                {
                    list.Add(new IPEndPoint(IPAddress.Parse(p.Address), p.QaulPort));
                }
                return list;
            }

            foreach (var p in peers.Values)
            {
                if (now - p.LastSeen >= PeerTimeoutMs || p.QaulPort <= 0) continue;
                try { list.Add(new IPEndPoint(IPAddress.Parse(p.Address), p.QaulPort)); }
                catch { }
            }
            return list;
        }

        /* ===================== 接收 ===================== */

        /// <summary>qaul 渠道收帧：还原成 Mchat 消息字典后进入统一入口。</summary>
        private void OnQaulMessage(QaulChatMessage m, QaulEnvelope env, string fromAddress)
        {
            if (m == null) return;
            try
            {
                // qaul 的 user_id 是 38 字节 identity-multihash（内含 Ed25519 公钥）；
                // 老版本/纯 Mchat 对端则直接放 ASCII 的 12 位 peerId。
                var userBytes = (m.SenderId != null && m.SenderId.Length > 0)
                    ? m.SenderId
                    : (env != null ? env.UserId : null);
                bool isQaulIdentity = userBytes != null && userBytes.Length == 38;

                string senderId = null;
                if (userBytes != null && userBytes.Length > 0 && !isQaulIdentity)
                    senderId = Encoding.UTF8.GetString(userBytes);
                else if (isQaulIdentity)
                    senderId = "qaul:" + Base58.Encode(userBytes).Substring(0, 8);

                if (string.IsNullOrEmpty(senderId)) return;

                var id = m.MessageId != null && m.MessageId.Length > 0
                    ? Encoding.UTF8.GetString(m.MessageId) : null;
                if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N");

                var msg = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["from"] = senderId,
                    ["text"] = m.Text ?? "",
                    ["time"] = m.SentAt > 0 ? m.SentAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["src"] = "qaul"
                };

                // 元数据（昵称/会话键/富媒体类型/控制语义；lid 会覆盖 from 为 Mchat 路由 id）
                if (!string.IsNullOrEmpty(m.Meta)) Msg.ApplyMeta(msg, m.Meta);

                if (Msg.Str(msg, "from") == peerId) return;   // 自己发的回环

                // 验签：带 qaul 身份 + sig 的帧必须通过对 (id + 正文) 的 Ed25519 校验，
                // 校验失败直接丢弃——这是"防冒充"的关键，也是 qaul Container 的语义。
                if (isQaulIdentity)
                {
                    var sigHex = Msg.Str(msg, "sig");
                    if (!string.IsNullOrEmpty(sigHex))
                    {
                        var pub = Curve25519.Slice(userBytes, 6, 32);
                        var sig = LocalIdentity.Unhex(sigHex);
                        if (sig == null || !Curve25519.Ed25519Verify(pub, SigningInput(id, m.Text ?? ""), sig))
                        {
                            Android.Util.Log.Warn("Qaul", "签名校验失败，丢弃可能的冒充帧: " + id);
                            return;
                        }
                        msg["verified"] = true;
                    }
                    else
                    {
                        // 老客户端/第三方实现可能不带 sig：放行但标记未验证
                        msg["verified"] = false;
                    }
                }

                var conv = Msg.Str(msg, "conv");
                if (string.IsNullOrEmpty(conv)) conv = ResolveConvByGroup(m.GroupId);
                msg["conv"] = string.IsNullOrEmpty(conv) ? Convs.Room(Convs.DefaultRoom) : conv;

                if (string.IsNullOrEmpty(Msg.Str(msg, "fromName")))
                {
                    Peer known;
                    msg["fromName"] = peers.TryGetValue(senderId, out known) ? known.Name : senderId;
                }
                if (string.IsNullOrEmpty(Msg.Str(msg, "kind"))) msg["kind"] = Msg.TextKind;

                // qaul 文件消息：只有文件名，正文提示
                if (m.IsFile && string.IsNullOrEmpty(Msg.Str(msg, "media")))
                {
                    msg["kind"] = "file";
                    msg["name"] = m.FileName;
                    if (string.IsNullOrEmpty(Msg.Str(msg, "text")))
                        msg["text"] = L10n.T("[文件] ") + m.FileName;
                }

                IngestIncoming(msg, null, fromAddress, "qaul");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Qaul", "处理 qaul 消息失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 由 qaul group_id 反查会话键。
        /// 会话键→group_id 是确定性哈希（不可逆），因此这里对已知会话做正向比对；
        /// 都不匹配时（真实 qaul 客户端发来的、非本实现创建的群组）落到一个
        /// 以 group_id 前缀命名的房间，保证消息不丢。
        /// </summary>
        private string ResolveConvByGroup(byte[] groupId)
        {
            if (groupId == null || groupId.Length == 0) return null;
            var hex = Convs.Hex(groupId);

            // 优先命中统一频道（chan:xxx）——qaul 是主链路，频道消息经它进来最常见。
            try
            {
                foreach (var c in channels.Joined())
                {
                    if (Convs.GroupIdHex(c.Conv) == hex) return c.Conv;
                }
            }
            catch { }

            var candidates = new List<string> { activeConv, Convs.Chan(ChannelRegistry.DefaultChannelName) };
            try
            {
                foreach (var c in store.Conversations()) candidates.Add(c.Id);
            }
            catch { }

            foreach (var conv in candidates)
            {
                if (string.IsNullOrEmpty(conv)) continue;
                if (Convs.GroupIdHex(conv) == hex) return conv;
            }
            return Convs.Room("qaul-" + hex.Substring(0, Math.Min(8, hex.Length)));
        }

        /// <summary>
        /// 统一入站入口：两条渠道收到消息后都在这里落地。
        ///
        /// 处理顺序：
        ///   1) 去重（同一 id 只处理一次，双渠道不会出现重复消息）；
        ///   2) 控制帧（删除/清空/指令）→ 执行后结束，不进会话历史；
        ///   3) 附件落地 → 写仓库 → 推界面（原生 + 网页端）→ 剪贴板 → 机器人。
        /// </summary>
        private void IngestIncoming(Dictionary<string, object> msg, byte[] body,
                                    string fromAddress, string source, bool reprocess = false)
        {
            if (msg == null) return;
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                Android.Util.Log.Info("Ingest", "入站 source=" + source + " from=" + Msg.Str(msg, "from")
                    + " self=" + peerId + " id=" + id + " enc=" + Msg.Str(msg, "enc"));
            }
            catch { }
            if (Msg.Str(msg, "from") == peerId) return;   // 自己发出的帧回环

            // 跳数硬上限：超过 MaxHop 的帧直接丢弃（不落库、不进会话、不转发），
            // 从根上掐断环路与放大。hop == MaxHop 的帧仍允许本地处理，但不会再被转发。
            if (mesh.HopOf(msg) > MeshRouter.MaxHop)
            {
                mesh.NoteRx(source, 0);
                mesh.NoteDrop(source, L10n.T("超过 ") + MeshRouter.MaxHop + L10n.T(" 跳，丢弃"));
                return;
            }

            // 渠道健康度：每条链路收了多少、其中多少是中继来的（给设置页展示，也便于排障）
            mesh.NoteRx(source, 0);
            if (mesh.HopOf(msg) > 0) mesh.NoteRelayIn(source);

            var kind = Msg.Kind(msg);
            var conv = Msg.Conv(msg);
            msg["conv"] = conv;

            // mesh 频道只接受局域网链路，且对端必须完成双向互通握手。
            // 局域网 = 普通网卡（lan）+ WLAN Direct（p2p，视为另一张网卡）。
            // 经 qaul 中继 / 蓝牙逐跳 / 静态中转站来的 mesh 一律丢弃。
            if (IsMeshConv(conv))
            {
                var src = source ?? "";
                bool lanLink = string.Equals(src, ChanLink.Lan, StringComparison.Ordinal)
                            || string.Equals(src, ChanLink.P2p, StringComparison.Ordinal);
                if (!lanLink)
                {
                    mesh.NoteDrop(source, L10n.T("mesh 频道仅允许局域网传输"));
                    return;
                }
                if (!LanPeerGate.IsConfirmed(Msg.Str(msg, "from")))
                {
                    mesh.NoteDrop(source, L10n.T("对端未完成局域网双向确认"));
                    return;
                }
            }

            // 中继只是额外联网渠道：私聊与所有频道照常接收。
            // 不再因为连了中继就阻断局域网私聊或未加入频道；
            // mesh 频道仍受上面的 LAN-only + 双向确认约束，绝不经中继。

            // 「一起听 / 同步听」：局域网帧的源 IP 就是发起端可达地址。
            // 发起端偶尔拿不到本机 LAN IP（HostBase 退化成 127.0.0.1），对端若直接拿
            // 那个流地址去播，就会一直卡在「缓冲中…」。这里补一个权威的可达地址。
            try
            {
                string k0 = Msg.Str(msg, "kind", "");
                bool isMusicFrame = k0 == "musiccard" || Msg.Str(msg, "op", "") == MsgOp.Patch;
                if (isMusicFrame && source != "qaul" && !string.IsNullOrEmpty(fromAddress))
                {
                    System.Net.IPAddress tip;
                    if (System.Net.IPAddress.TryParse(fromAddress, out tip)
                        && tip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(tip))
                    {
                        int tp = WebServer.ExternalPortActual;
                        if (tp <= 0) tp = 7790;
                        msg["peerHost"] = "http://" + fromAddress + ":" + tp;
                    }
                }
            }
            catch { }

            // 私聊帧：本机是对话一方时，只接受来自会话对端的帧；
            // 本机不是任何一方（是中转站）时，帮忙把密文帧转发给其它链路/中转站，
            // 不落库、不显示、不通知——正文此时仍是 SealOutgoing 密文，第三方解不开。
            if (Convs.IsDm(conv))
            {
                if (Convs.IsDmParticipant(conv, peerId))
                {
                    if (Msg.Str(msg, "from") != Convs.DmPeer(conv, peerId)) return;
                }
                else
                {
                    RelayDmFrame(conv, msg, body, source);
                    return;
                }
            }

            // 1) 去重：双渠道可能各送一次，只处理第一条。
            //    reprocess=true 时是「解密重试」，同一条消息要允许再走一遍。
            if (!reprocess && !_dedupEngine.MarkSeen(id))
            {
                try { Android.Util.Log.Info("Ingest", "重复消息，忽略 id=" + id); } catch { }
                return;
            }

            // 【注意：这里不能做内容去重】
            // 以前这里调了一次 IsCrossChannelDup（内部 = TryMark 内容去重），
            // 而附件落地之后还会再调一次 MarkContentSeen（同一个 TryMark）。
            // 第一次调用把内容键写进去后，第二次立刻把**同一条新消息**判成重复并丢弃，
            // 于是每条消息第一次到达就没了 —— 现象就是「对端永远收不到，只有自己发的」。
            // 内容/跨渠道去重统一放在下方 MarkContentSeen 一处完成。

            // 解密失败要留原件：UnsealIncoming 会把 text 改写成「[无法解密]」，
            // 之后即使拿到密钥也解不开了，所以先拍一份密文快照。
            var rawCopy = new Dictionary<string, object>(msg);

            // 1.5) LC1 解密：密文正文（含昵称/类型）还原；密钥交换帧在此消化，不进聊天记录
            if (UnsealIncoming(conv, msg, Msg.Str(msg, "from"), Msg.Str(msg, "link"))) return;
            kind = Msg.Kind(msg);

            // 1.55) 解密失败（多半是密钥还在握手中，或频道密码不一致）：
            //   把 id 从「已读」里撤回来，并把这帧排进重试队列 ——
            //   qaul 的做法也是「确认前不销账」，否则密钥晚到一步这条消息就永久丢了。
            //
            //   注意：这里**直接返回**，不落任何占位、不提示、不转发。
            //   以前会写一条「[无法解密]」占位，密钥迟迟不到时会把会话刷满、反复打扰用户；
            //   现在改为静默丢弃，只保留原文等待重试，解开了再正常显示。
            if (Msg.Str(msg, "enc") == "locked")
            {
                lastIngestDecrypted = false;
                if (!reprocess)
                {
                    UnmarkSeen(id);
                    QueuePendingDecrypt(rawCopy, body, fromAddress, source);
                }
                mesh.NoteDrop(source, L10n.T("解密失败，静默丢弃"));
                try { Android.Util.Log.Warn("Ingest", "解密失败(locked)丢弃 id=" + id + " conv=" + conv + " from=" + Msg.Str(msg, "from")); } catch { }
                return;
            }
            else
            {
                lastIngestDecrypted = true;
            }

            // 1.6) 中间层/加密层控制帧（发现信标等）：永远不进聊天记录，也不转发给机器人
            if (msg.ContainsKey("lc1ctl") || kind == "lc1ctl") return;

            // 1.9) 设备常驻状态 / 定位请求：只更新状态条，绝不进聊天记录
            if (kind == DevStatKind) { msg["src"] = source; HandleDevStat(msg); return; }
            if (kind == DevReqKind) { HandleDevReq(msg); return; }

            // 1.10) 已读回执：只更新会话已读进度，不进聊天记录
            if (kind == "read_receipt") { HandleReadReceipt(msg); return; }

            // 1.11) 群组管理：只同步群成员变更，不进聊天记录
            if (kind == "group_mgmt") { HandleGroupMgmt(msg); return; }

            // 2) 控制帧
            if (kind == "callsignal") { HandleCallSignal(msg, fromAddress); return; }
            // GPS 专用隐藏渠道 + 旧版 location 帧：都只更新雷达缓存，绝不进聊天记录/气泡
            if (kind == GpsShareKind || kind == "location") { HandleLocationFrame(msg); return; }
            // 同步听时钟对时：瞬时帧，绝不进聊天记录
            if (kind == SyncClock.KindPing) { try { SyncClock.OnPing(msg); } catch { } return; }
            if (kind == SyncClock.KindPong) { try { SyncClock.OnPong(msg); } catch { } return; }
            if (kind == "mediaplayer" || kind == "mediacontrol")
            {
                // 原生侧：对端通知栏播放器 + 播放指令执行；网页端继续用事件驱动
                try { AhoChatA.Music.MusicCast.OnRemoteFrame(kind, msg); } catch { }
                Emit(kind, msg);
                return;
            }
            if (kind == "control" || !string.IsNullOrEmpty(Msg.Str(msg, "op")))
            {
                HandleControlFrame(msg);
                return;
            }

            // 3) 附件落地
            // 电脑端会同时塞一份 base64 到消息头里（小文件兜底）：优先用它，
            // 这样即使二进制帧体在某个版本上没被收下，图片也一定能显示出来。
            if ((body == null || body.Length == 0) && msg.ContainsKey("dataB64"))
            {
                try
                {
                    var b64 = Msg.Str(msg, "dataB64");
                    if (!string.IsNullOrEmpty(b64))
                    {
                        body = Convert.FromBase64String(b64);
                        Android.Util.Log.Info("Mchat", $"附件来自 base64：{body.Length} 字节");
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "base64 附件解码失败: " + ex.Message);
                    body = null;
                }
            }
            if (body != null && body.Length > 0)
            {
                try
                {
                    var mediaName = Msg.Str(msg, "media");
                    var fileName = SafeMediaName(mediaName, id);
                    File.WriteAllBytes(Path.Combine(mediaDir, fileName), body);
                    msg["media"] = fileName;
                    msg["mediaUrl"] = "/__media/" + fileName;

                    // 自动接收：按开关把任意扩展名的文件按原始名另存到用户目录（与四类媒体入库并行）。
                    // 白板笔迹（kind=whiteboard.stroke）属于协作内容，不受开关限制，始终落盘，
                    // 这样手机收到别人分享的白板就能直接在 Download 里找到 .icstk。
                    // 原始名优先取 name 字段（发送端保留），media 是「id+扩展名」的落地名，仅作回退。
                    // !reprocess 防止同一帧经不同链路重放时重复落盘。
                    bool isWhiteboard = kind == "whiteboard.stroke" || kind == "whiteboard";
                    if (!reprocess && (AutoReceiveSettings.Enabled || isWhiteboard))
                    {
                        var origName = Msg.Str(msg, "name");
                        if (string.IsNullOrEmpty(origName)) origName = mediaName;
                        var saved = AutoReceive.SaveIncoming(origName, body);
                        if (!string.IsNullOrEmpty(saved))
                        {
                            try
                            {
                                var arConv = Msg.Str(msg, "conv");
                                var arWho = Msg.Str(msg, "fromName");
                                if (string.IsNullOrEmpty(arWho)) arWho = Msg.Str(msg, "name");
                                ChatNotifier.NotifyMessage(arConv, L10n.T("自动接收"), L10n.T("已保存：") + Path.GetFileName(saved));
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "附件落地失败: " + ex.Message);
                }
            }

            // 内容级二次去重（兜底）：同一条消息经不同链路或多跳绕回来时，
            // 只要有任一环生成了不同的 id，id 去重就漏；这里按
            // 「会话 + 发送者 + 类型 + 原始时间 + 明文」再认一次，只显示一条。
            if (!reprocess && !MarkContentSeen(msg))
            {
                try { Android.Util.Log.Info("Ingest", "内容重复，忽略 id=" + id + " source=" + source); } catch { }
                return;
            }

            bool created = store.Add(msg, true);
            if (!created)
            {
                // 这条以前落过：要么是同一 id 的另一条链路副本（真重复），
                // 要么是先到的那份「[无法解密]」占位（解密重试成功）。
                if (!reprocess) return;
                if (store.Replace(msg)) Emit("msgupd", msg);
                return;
            }
            AppendHistory(msg);
            Emit("message", msg);

            // 请求方：收到对方回传的坐标（4 行格式，末行是「纬度,经度」）→ 取消超时提示并上报
            if (_locReqPending && !reprocess && Msg.Str(msg, "from") != peerId)
            {
                var t = Msg.Str(msg, "text");
                if (LooksLikeLocation(t))
                    OnPeerLocationSeen(t, Msg.Str(msg, "fromName"), Msg.Str(msg, "conv"));
            }

            // 通知栏提醒（所有收到的消息都进系统通知栏，而不仅是 Toast；带「回复」快捷输入）
            try
            {
                // 解密失败的（enc=locked / 占位「无法解密」）不弹通知——那是还没解开的消息，不该打扰用户。
                if (Msg.Str(msg, "from") != peerId && Msg.Str(msg, "enc") != "locked")
                {
                    var fromName = Msg.Str(msg, "fromName");
                    var preview = Msg.Str(msg, "text");
                    if (string.IsNullOrEmpty(preview)) preview = "[" + (kind ?? L10n.T("消息")) + "]";
                    if (preview != "[无法解密]")
                        ChatNotifier.NotifyMessage(conv,
                            string.IsNullOrEmpty(fromName) ? Convs.TitleOf(conv) : fromName, preview);
                }
            }
            catch { }

            // 媒体自动加载 + 切前台自动播放：文本文档 / PDF / 音乐 / 图片 / 视频，
            // 包括「分享到频道」的 mlib 内部链接与普通文件附件；批量发送（窗口内连收第二个）
            // 由 MediaAutoOpen 内部判定为忽略自动显示，仅静默入库。
            if (!reprocess && body != null && body.Length > 0)
            {
                try
                {
                    string mlib = Msg.Str(msg, "mlib", "");
                    string disp = Msg.Str(msg, "name", "");
                    string fn = Msg.Str(msg, "media", "");
                    if (!string.IsNullOrEmpty(fn))
                    {
                        string km = !string.IsNullOrEmpty(mlib) ? mlib
                                    : MediaShare.Classify(string.IsNullOrEmpty(disp) ? fn : disp);
                        if (!string.IsNullOrEmpty(km))
                            MediaAutoOpen.OnMedia(context.ApplicationContext, km,
                                string.IsNullOrEmpty(disp) ? fn : disp, Path.Combine(mediaDir, fn));
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("MediaAuto", "调度自动打开失败: " + ex.Message);
                }
            }

            // 统一频道：把这条消息补发到该频道其它已启用的链路，
            // 使「非局域网用户 ↔ 局域网用户」也能互通（靠消息 id 去重，不会成环）。
            if (Convs.IsChan(conv))
            {
                msg["src"] = source;
                SyncAcrossLinks(conv, msg, body, source);
            }

            MaybeExecControl(msg);

            if (string.IsNullOrEmpty(kind) || kind == Msg.TextKind || kind == "text")
            {
                var text = Msg.Str(msg, "text");
                if (!string.IsNullOrEmpty(text)) PlainTextReceived?.Invoke(text);
            }
        }

        /* ===================== 解密重试（密钥晚到也能救回来） ===================== */

        /// <summary>一条「收到了但解不开」的帧原件，等密钥就绪后重试。</summary>
        private sealed class PendingDecryptFrame
        {
            public Dictionary<string, object> Msg;
            public byte[] Body;
            public string Address;
            public string Source;
            public long At;
            public int Attempts;
            public long NextAt;
        }

        private readonly List<PendingDecryptFrame> pendingDecrypt = new List<PendingDecryptFrame>();
        private readonly object pendingGate = new object();
        /// <summary>最近一次 IngestIncoming 是否解密成功（解密重试据此决定还要不要再排）。</summary>
        private volatile bool lastIngestDecrypted = true;
        /// <summary>解密重试最多留多久 / 多少条 / 试几次。</summary>
        private const long PendingDecryptTtlMs = 5 * 60 * 1000;
        private const int PendingDecryptMax = 30;
        private const int PendingDecryptMaxAttempts = 8;

        private void QueuePendingDecrypt(Dictionary<string, object> raw, byte[] body, string address, string source)
        {
            if (raw == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (pendingGate)
            {
                for (int i = pendingDecrypt.Count - 1; i >= 0; i--)
                    if (now - pendingDecrypt[i].At > PendingDecryptTtlMs) pendingDecrypt.RemoveAt(i);
                if (pendingDecrypt.Count >= PendingDecryptMax) pendingDecrypt.RemoveAt(0);
                pendingDecrypt.Add(new PendingDecryptFrame
                {
                    Msg = raw,
                    Body = body,
                    Address = address,
                    Source = source,
                    At = now,
                    NextAt = now + 2000        // 2 秒后第一次重试：握手通常就在这一两秒内完成
                });
            }
        }

        /// <summary>
        /// 重试「收到了但当时解不开」的帧。
        ///
        /// 场景：对端刚连上就把历史推过来，而 LC1 会话密钥还在握手中，
        /// 于是头几条会以「[无法解密]」占位落地；等密钥装好，这里把原件重放一遍，
        /// 解密成功后原地替换占位（Emit "msgupd"，界面只刷新不新增）。
        /// qaul 的做法等价：未确认的消息会重发，且允许在会话轮换后重新加密。
        /// </summary>
        public void RetryPendingDecrypt()
        {
            List<PendingDecryptFrame> batch = null;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (pendingGate)
            {
                if (pendingDecrypt.Count == 0) return;
                for (int i = pendingDecrypt.Count - 1; i >= 0; i--)
                    if (now - pendingDecrypt[i].At > PendingDecryptTtlMs) pendingDecrypt.RemoveAt(i);

                for (int i = pendingDecrypt.Count - 1; i >= 0; i--)
                {
                    var p = pendingDecrypt[i];
                    if (p.NextAt > now) continue;
                    pendingDecrypt.RemoveAt(i);
                    if (batch == null) batch = new List<PendingDecryptFrame>();
                    batch.Add(p);
                }
            }
            if (batch == null) return;

            foreach (var p in batch)
            {
                try
                {
                    IngestIncoming(new Dictionary<string, object>(p.Msg), p.Body, p.Address, p.Source, true);
                    // 仍是 locked 说明还没解开：按退避再排一次（最多 8 次 / 5 分钟）
                    if (!lastIngestDecrypted && p.Attempts < PendingDecryptMaxAttempts)
                    {
                        p.Attempts++;
                        p.NextAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Min(30000, 2000L << Math.Min(p.Attempts, 4));
                        lock (pendingGate) { if (pendingDecrypt.Count < PendingDecryptMax) pendingDecrypt.Add(p); }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "解密重试失败: " + ex.Message);
                }
            }
        }

        /// <summary>控制帧（指令渠道）统一处理：指令 / 删除 / 清空 / 握手。</summary>
        private void HandleControlFrame(Dictionary<string, object> msg)
        {
            var op = Msg.Str(msg, "op");
            var conv = Msg.Conv(msg);

            switch (op)
            {
                case MsgOp.Cmd:
                    MaybeExecControl(msg);
                    return;

                case MsgOp.Recall:
                case MsgOp.Del:
                {
                    var target = Msg.Str(msg, "target");
                    if (string.IsNullOrEmpty(target)) return;
                    if (store.Delete(target))
                    {
                        var evt = new Dictionary<string, object>
                        {
                            ["id"] = Msg.Str(msg, "id"),
                            ["conv"] = conv,
                            ["kind"] = Msg.ControlKind,
                            ["op"] = op,
                            ["target"] = target,
                            ["text"] = "",
                            ["from"] = Msg.Str(msg, "from"),
                            ["fromName"] = Msg.Str(msg, "fromName"),
                            ["time"] = Msg.Num(msg, "time")
                        };
                        AppendHistory(evt);
                        Emit("msgdel", evt);
                    }
                    return;
                }

                case MsgOp.Edit:
                {
                    var target = Msg.Str(msg, "target");
                    var text = Msg.Str(msg, "text");
                    if (string.IsNullOrEmpty(target)) return;
                    if (store.UpdateText(target, text))
                    {
                        var evt = new Dictionary<string, object>
                        {
                            ["id"] = Msg.Str(msg, "id"),
                            ["conv"] = conv,
                            ["kind"] = Msg.ControlKind,
                            ["op"] = MsgOp.Edit,
                            ["target"] = target,
                            ["text"] = text,
                            ["from"] = Msg.Str(msg, "from"),
                            ["fromName"] = Msg.Str(msg, "fromName"),
                            ["time"] = Msg.Num(msg, "time")
                        };
                        AppendHistory(evt);
                        Emit("msgedit", evt);
                    }
                    return;
                }

                case MsgOp.Patch:
                {
                    // 整条原地替换（音乐卡片切歌走这条，不新增气泡）
                    var target = Msg.Str(msg, "target");
                    if (string.IsNullOrEmpty(target)) return;

                    var body = new Dictionary<string, object>(msg);
                    body["id"] = target;
                    body["conv"] = conv;
                    var pk = Msg.Str(msg, "patchKind");
                    body["kind"] = string.IsNullOrEmpty(pk) ? Msg.Str(msg, "kind", "text") : pk;
                    body.Remove("op");
                    body.Remove("target");
                    body.Remove("patchKind");

                    // 本次没带封面/歌词/一起听列表就沿用原来那张卡片上的，
                    // 避免切歌后卡片变成空白、或每 20 秒的进度校正把播放列表抹掉
                    var prev = store.Get(target);
                    if (prev != null)
                    {
                        bool sameSong2 = true;
                        if (body.ContainsKey("lrcKey") && prev.TryGetValue("lrcKey", out var oldLk2))
                            sameSong2 = string.Equals(Convert.ToString(oldLk2),
                                Msg.Str(body, "lrcKey", ""), StringComparison.Ordinal);
                        // tvPlaylist/tvIndex/tvHost：patch 整条替换时必须沿用，否则后上线的电视
                        // 从 SyncSessionTo 补发的缓存卡片里就取不到这份歌单了。
                        foreach (var key in new[] { "cover", "lrcKey", "playlist", "lt", "ltHost", "ltIndex", "peerHost", "tvPlaylist", "tvIndex", "tvHost" })
                            if (!body.ContainsKey(key) && prev.TryGetValue(key, out var pv) && pv != null)
                                body[key] = pv;
                        if (sameSong2 && !body.ContainsKey("lrc")
                            && prev.TryGetValue("lrc", out var oldLrc2) && oldLrc2 != null)
                            body["lrc"] = oldLrc2;
                        if (prev.TryGetValue("time", out var pt) && !body.ContainsKey("time")) body["time"] = pt;
                    }

                    if (prev != null)
                    {
                        if (store.Replace(body))
                        {
                            AppendHistory(body);
                            Emit("msgupd", body);
                        }
                    }
                    else
                    {
                        // 后加入的设备没收到过原卡片：直接补一条，别让它永远空着
                        store.Add(body, true);
                        AppendHistory(body);
                        Emit("message", body);
                    }
                    return;
                }

                case MsgOp.Clear:
                {
                    var n = store.Clear(conv);
                    var evt = new Dictionary<string, object>
                    {
                        ["id"] = Msg.Str(msg, "id"),
                        ["conv"] = conv,
                        ["kind"] = Msg.ControlKind,
                        ["op"] = MsgOp.Clear,
                        ["text"] = "",
                        ["from"] = Msg.Str(msg, "from"),
                        ["fromName"] = Msg.Str(msg, "fromName"),
                        ["time"] = Msg.Num(msg, "time")
                    };
                    AppendHistory(evt);
                    Emit("convclear", evt);
                    Android.Util.Log.Info("Mchat", $"会话 {conv} 被对端清空（{n} 条）");
                    return;
                }

                case MsgOp.Hello:
                {
                    // 对端上线：补全私聊标题
                    var dm = Convs.Dm(peerId, Msg.Str(msg, "from"));
                    var name = Msg.Str(msg, "fromName");
                    if (!string.IsNullOrEmpty(name)) store.SetTitle(dm, name);
                    return;
                }

                case MsgOp.Rename:
                {
                    // 对端重命名了某个会话：同步标题并刷新各界面
                    var title = Msg.Str(msg, "text");
                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(conv))
                    {
                        store.SetTitle(conv, title);
                        try { ConversationsChanged?.Invoke(); } catch { }
                        Emit("convrename", new { conv, title });
                        Android.Util.Log.Info("Mchat", $"会话 {conv} 被对端重命名为「{title}」");
                    }
                    return;
                }

                default:
                    // 老版本 Mchat 的把 (命令) 文本当指令，继续兼容
                    MaybeExecControl(msg);
                    return;
            }
        }

        /// <summary>上线握手：把昵称广播出去（两条渠道），便于私聊标题显示。</summary>
        private void AnnouncePresence()
        {
            try
            {
                var hello = Msg.Control(activeConv, MsgOp.Hello);
                hello["from"] = peerId;
                hello["fromName"] = displayName;
                _dedupEngine.MarkSeen(Msg.Str(hello, "id"));
                Deliver(hello, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "握手广播失败: " + ex.Message);
            }
        }

        /* ===================== 身份与指纹（qaul / bitchat 接口） ===================== */

        /// <summary>当前身份摘要（原生弹窗、网页端 /api/identity 共用）。</summary>
        public object GetIdentitySummary()
        {
            return identity == null ? null : identity.Summary();
        }

        /// <summary>
        /// 更换指纹。
        /// scope=All 全换；Qaul 只换 qaul 的 Ed25519；Bitchat 只换 bitchat 的 Noise+签名。
        /// 换完后会立刻用新身份重新广播，界面与网页端会收到 `identity` 事件。
        /// </summary>
        public LocalIdentity RotateIdentity(IdentityScope scope)
        {
            var id = identity.Rotate(scope);
            return id;
        }

        /// <summary>身份变化：更新本机 peerId，重新握手，通知界面。</summary>
        private void OnIdentityChanged(LocalIdentity id)
        {
            try
            {
                if (id == null) return;

                // 局域网身份跟着签名密钥走：换指纹后旧 id 立刻失效
                if (!string.IsNullOrEmpty(id.MchatId)) peerId = id.MchatId;

                Android.Util.Log.Info("Identity",
                    $"身份更新：Mchat={peerId} qaul={id.QaulPeerId} bitchat={id.BitchatPeerId}");

                Task.Run(() =>
                {
                    try { AnnouncePresence(); } catch { }
                    // 节点身份也跟着变了：重开 libp2p 节点并重新用新 PeerId 广播
                    try { StartQaulNode(); } catch { }
                });

                Emit("identity", id.ToSummary());
                try { ConversationsChanged?.Invoke(); } catch { }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Identity", "身份更新后的收尾失败: " + ex.Message);
            }
        }

        /// <summary>qaul 帧签名用的输入：消息 id + 正文（与 JSON 序列化无关，稳定可比对）。</summary>
        private static byte[] SigningInput(string id, string text)
        {
            var a = Encoding.UTF8.GetBytes(id ?? "");
            var t = Encoding.UTF8.GetBytes(text ?? "");
            var buf = new byte[a.Length + 1 + t.Length];
            Buffer.BlockCopy(a, 0, buf, 0, a.Length);
            buf[a.Length] = 0x1F;
            Buffer.BlockCopy(t, 0, buf, a.Length + 1, t.Length);
            return buf;
        }

        /* ===================== qaul 网络节点（libp2p） ===================== */

        /// <summary>
        /// 启动/重启 qaul 网络节点。
        /// 换指纹后必须重启：节点身份（PeerId）变了，旧的 mDNS 通告和邻居连接都要作废。
        /// </summary>
        private void StartQaulNode()
        {
            try
            {
                if (qnode != null)
                {
                    try { qnode.Stop(); } catch { }
                    qnode = null;
                }

                var id = Identity;
                if (id == null) return;

                var node = new QaulNode(id, displayName, QaulNodePort,
                                        Path.Combine(context.FilesDir.AbsolutePath, "qaul"));
                // 不接 OnLog：QaulNode 内部已经写 logcat，再接一次会重复
                // 但界面要能看「为什么等不到公钥」，这里留一份环形缓冲给对话框/HTTP 用
                node.OnLog = QaulLog;

                node.OnNeighbour = (pid, name, online) =>
                {
                    Emit("qaul", new { peer = pid, name, online, neighbours = node.NeighbourCount });
                };
                node.OnInfo = pid => Emit("qaul", new { peer = pid, info = true });
                node.OnFeed = OnQaulFeed;
                node.OnUserRoute = (uid, via) => Emit("qauluser", new { id = uid, via });

                if (node.Start())
                {
                    qnode = node;
                    // 重启节点时把当前省电状态带过去（身份更换会重建节点）
                    try { node.SetIdle(PowerIdle); } catch { }
                    // 公开信息（qaul Feed）不再作为会话入口暴露；qaul 只当通信渠道用。
                    // 这里不再预建会话标题，避免它出现在会话列表里。
                    WireQaulMessaging(node);
                }
                else
                {
                    node.Stop();
                    Emit("error", new { message = L10n.T("qaul 网络节点启动失败（端口 ") + QaulNodePort + L10n.T(" 被占用？）") });
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("QaulNode", "启动失败: " + ex.Message);
            }
        }

        /// <summary>把 qaul 的私聊/群聊事件接到本应用的会话与消息库里。</summary>
        private void WireQaulMessaging(QaulNode node)
        {
            var msg = node.Messaging;
            if (msg == null) return;
            qaulMsg = msg;

            msg.OnChat = (peer, text, mid, sentAt) =>
            {
                try
                {
                    var conv = Convs.QaulDmPrefix + peer;
                    var name = QaulUserName(peer);
                    store.SetTitle(conv, name);

                    var m = Msg.New(conv, peer, name, "text", text);
                    m["id"] = mid != null && mid.Length > 0 ? LocalIdentity.Hex(mid) : Msg.Str(m, "id");
                    if (sentAt > 0) m["time"] = sentAt;
                    m["src"] = "qaul-e2e";

                    // LC1：qaul 只是渠道，真正的内容在这一层
                    // 解密前先装载对称派生密钥（两端会话键不同，必须先对齐才能解开）
                    EnsureQaulDmKey(peer);
                    if (UnsealIncoming(conv, m, peer, ChanLink.Qaul)) return;

                    _dedupEngine.MarkSeen(Msg.Str(m, "id"));
                    store.Add(m, true);
                    AppendHistory(m);
                    Emit("message", m);
                }
                catch (Exception ex) { Android.Util.Log.Warn("QaulMsg", "写入 qaul 私聊失败: " + ex.Message); }
            };

            msg.OnGroupChat = (gidHex, text, mid, sentAt, sender) =>
            {
                try
                {
                    var conv = Convs.QaulGroupPrefix + gidHex;
                    // qaul 中继进来的频道/房间消息：group_id 命中已知频道时，落回该频道会话，
                    // 用频道密钥解（而不是伪造成 qaul 群、拿错密钥解 → 永远 [无法解密]）。
                    // 这修掉错误1 的 870ae203 残因：chan:mesh 中继被误投进 qaul 私聊。
                    try
                    {
                        var resolved = ResolveConvByGroup(Convs.Unhex(gidHex));
                        if (!string.IsNullOrEmpty(resolved) && Convs.IsChan(resolved)) conv = resolved;
                    }
                    catch { }
                    var title = Convs.IsChan(conv) ? Convs.TitleOf(conv) : QaulGroupName(gidHex);
                    store.SetTitle(conv, title);

                    // 群消息可能是任意成员发的：from 记真实发送者，fromName 用其昵称；
                    // 会话标题仍然是群名（侧栏显示用）。
                    var senderId = string.IsNullOrEmpty(sender) ? "qaul-group:" + gidHex : sender;
                    var senderName = string.IsNullOrEmpty(sender) ? title : QaulUserName(sender);

                    var m = Msg.New(conv, senderId, senderName, "text", text);
                    m["id"] = mid != null && mid.Length > 0 ? LocalIdentity.Hex(mid) : Msg.Str(m, "id");
                    if (sentAt > 0) m["time"] = sentAt;
                    m["src"] = "qaul-group";
                    m["groupName"] = title;

                    // LC1：群内容同样先过本层
                    if (UnsealIncoming(conv, m, senderId, ChanLink.Qaul)) return;

                    _dedupEngine.MarkSeen(Msg.Str(m, "id"));
                    store.Add(m, true);
                    AppendHistory(m);
                    Emit("message", m);
                }
                catch (Exception ex) { Android.Util.Log.Warn("QaulMsg", "写入 qaul 群聊失败: " + ex.Message); }
            };

            msg.OnGroupChanged = g =>
            {
                try
                {
                    // 不再因为「知道了一个群」就往顶栏塞会话——顶栏只显示真正聊过的频道/会话。
                    // 群信息记到 qaul 分组列表即可，用户从菜单「qaul 在线设备 / 群组」进入。
                    Emit("qaulgroup", new { id = g.Id, name = g.Name, members = g.Members.Count });
                }
                catch { }
            };

            msg.OnUserSeen = (id, name) =>
            {
                try
                {
                    // 关键：发现一个 qaul 设备**不再**创建顶栏会话。
                    // 同网段设备一多，顶栏会被几十条空会话淹没、根本翻不动。
                    // 现在只有真正收发过消息的会话才会出现在顶栏；
                    // 想找设备请走右上角菜单「qaul 在线设备」。
                    Emit("qauluser", new { id, name });
                }
                catch { }
            };

            msg.OnFile = (sender, name, bytes, sentAt, groupHex) =>
            {
                try
                {
                    if (bytes == null || bytes.Length == 0) return;
                    var from = string.IsNullOrEmpty(sender) ? "qaul" : sender;
                    // 群文件（带 qaul 群 id）归入群会话；否则归入 qaul 私聊会话。
                    // 频道也走 qaul 群：group_id 是「chan:xxx」确定性映射来的，
                    // 这里按 group id 反查频道，让文件落回它该在的那个频道，而不是变成一个孤立的 qgroup 会话。
                    string conv;
                    if (string.IsNullOrEmpty(groupHex) || groupHex.Length != 32)
                    {
                        conv = Convs.QaulDmPrefix + from;
                    }
                    else
                    {
                        var chanOfGroup = channels != null ? channels.ByGroupHex(groupHex) : null;
                        conv = chanOfGroup != null ? chanOfGroup.Conv : Convs.QaulGroupPrefix + groupHex;
                    }
                    // 仅私聊会话才用发送者昵称设标题；群会话标题不应被单条文件覆盖
                    if (conv.StartsWith(Convs.QaulDmPrefix, StringComparison.Ordinal))
                        store.SetTitle(conv, QaulUserName(from));

                    var mediaId = LocalIdentity.Hex(LocalIdentity.Sha256(bytes)) + "-" + bytes.Length;
                    var fileName = SafeMediaName(name, mediaId);
                    File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);

                    var kind = MediaKind(name);
                    if (string.IsNullOrEmpty(name)) name = L10n.T("文件");
                    var m = Msg.New(conv, from, QaulUserName(from), kind, Msg.FileLabel(kind, bytes.Length));
                    m["id"] = mediaId;
                    m["media"] = fileName;
                    m["name"] = name;                 // 原始文件名只用于下载时保存
                    m["mediaUrl"] = "/__media/" + fileName;
                    m["size"] = bytes.Length;
                    if (sentAt > 0) m["time"] = sentAt;
                    m["src"] = "qaul-file";
                    _dedupEngine.MarkSeen(Msg.Str(m, "id"));
                    store.Add(m, true);
                    AppendHistory(m);
                    Emit("message", m);

                    // 自动接收：qaul 链路同样按开关把任意文件另存到用户目录
                    if (AutoReceiveSettings.Enabled)
                    {
                        var saved = AutoReceive.SaveIncoming(name, bytes);
                        if (!string.IsNullOrEmpty(saved))
                        {
                            try { ChatNotifier.NotifyMessage(conv, L10n.T("自动接收"), L10n.T("已保存：") + System.IO.Path.GetFileName(saved)); }
                            catch { }
                        }
                    }

                    // 媒体自动加载 + 切前台自动播放：qaul 文件不带 mlib 标记，按扩展名判定；
                    // 批量（窗口内连收第二个）由 MediaAutoOpen 内部忽略自动显示，仅静默入库。
                    try
                    {
                        string km = MediaShare.Classify(name);
                        if (!string.IsNullOrEmpty(km))
                            MediaAutoOpen.OnMedia(context.ApplicationContext, km, name,
                                System.IO.Path.Combine(mediaDir, fileName));
                    }
                    catch (Exception ex) { Android.Util.Log.Warn("MediaAuto", "qaul 调度自动打开失败: " + ex.Message); }

                    // 系统通知栏提醒（不只用 Toast）
                    try { ChatNotifier.NotifyMessage(conv, QaulUserName(from), Msg.FileLabel(kind, bytes.Length)); }
                    catch { }
                    try { L10n.Toast(context, L10n.T("收到附件：") + Msg.FileLabel(kind, bytes.Length), ToastLength.Short).Show(); }
                    catch { }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("QaulMsg", "写入 qaul 附件失败: " + ex.Message);
                }
            };

            msg.OnFileProgress = (peer, name, sent, total) =>
            {
                try
                {
                    int pct = total > 0 ? (int)(sent * 100L / total) : 0;
                    // 每约 512KB 或完成时记一次，避免刷屏
                    if (sent >= total || (sent / 64000L) % 8 == 0)
                        QaulLog(L10n.T("发送附件「") + (name ?? "") + L10n.T("」进度 ") + pct + "% (" + sent + "/" + total + ")");
                }
                catch { }
            };

            msg.OnLog = QaulLog;

            // 启动时清理「误投期」的历史垃圾（见 PurgeMisroutedDmLocked 的说明）。
            // 派发 bug 已修，这里把已经刷进私聊会话的 locked 记录清掉。
            try { PurgeMisroutedDmLocked(); } catch { }
        }

        /// <summary>
        /// 清理「误投期」留在 qaul 私聊会话里的历史垃圾消息。
        ///
        /// 背景：错误1 的派发 bug 修复前，频道/信标中继被打成 ChatMessage(field 4)
        /// 落进 OnChat（qaul 私聊），接收端拿私聊密钥去解频道密文 → 永远
        /// [无法解密]，把私聊会话刷满 locked（真机实测：单会话 104 条同一条
        /// 频道广播的副本，id 前缀 870ae203 = CRC64(GroupId("chan:mesh"))）。
        ///
        /// 判据（精确、不误伤）：qaul 消息 id = CRC64(group)‖CRC64(sender)‖index
        /// （20 字节 → 40 hex）。真私聊的 group 恒等于「双方 DirectGroupId」（两端
        /// 算法一致），误投进来的 group 是频道/房间的——前缀必然不同。
        /// 因此：私聊会话里 enc=locked、id 是 40 hex、且 group 前缀不是本会话
        /// DirectGroupId 前缀的，就是误投产物 → 删除；
        /// group 前缀一致的真私聊（可能只是对端换了身份解不开）保留，
        /// 由 UnsealIncoming 的诚实文案说明原因。
        /// </summary>
        private int PurgeMisroutedDmLocked()
        {
            if (store == null || Identity == null) return 0;
            int purged = 0;
            try
            {
                foreach (var c in store.Conversations())
                {
                    var conv = c.Id;
                    if (string.IsNullOrEmpty(conv) || !Convs.IsQaulDm(conv)) continue;
                    var peer = Convs.QaulPeerOf(conv);
                    if (string.IsNullOrEmpty(peer)) continue;

                    byte[] peerBytes;
                    try { peerBytes = Base58.Decode(peer); } catch { continue; }
                    if (peerBytes == null || peerBytes.Length < 14) continue;

                    var expect = LocalIdentity.Hex(QaulMessaging.MessageId(
                        QaulMessaging.DirectGroupId(Identity.QaulPeerIdBytes, peerBytes),
                        Identity.QaulPeerIdBytes, 0));
                    var prefix = expect.Substring(0, 16);

                    foreach (var m in store.Messages(conv, 0))
                    {
                        if (!string.Equals(Msg.Str(m, "enc"), "locked", StringComparison.Ordinal)) continue;
                        var id = Msg.Str(m, "id");
                        // 只要 qaul-native 的 20 字节命中 id（40 hex）；
                        // Mchat 帧路径的 GUID id（32 字符）不碰。
                        if (string.IsNullOrEmpty(id) || id.Length != 40) continue;
                        if (id.Substring(0, 16) == prefix) continue;
                        if (store.Delete(id)) purged++;
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "清理误投历史失败: " + ex.Message);
            }
            if (purged > 0)
                Android.Util.Log.Info("Mchat", "已清理误投进私聊的历史消息 " + purged + " 条");
            return purged;
        }

        /* ---------- qaul 运行日志（诊断用，最近 300 条） ---------- */

        private readonly Queue<string> qaulLog = new Queue<string>();
        private readonly object qaulLogGate = new object();

        /// <summary>把 qaul 层日志同时写 logcat 与内存环形缓冲（界面上可看）。</summary>
        private void QaulLog(string m)
        {
            if (string.IsNullOrEmpty(m)) return;
            try { Android.Util.Log.Info("QaulMsg", m); } catch { }
            try
            {
                lock (qaulLogGate)
                {
                    qaulLog.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + m);
                    while (qaulLog.Count > 300) qaulLog.Dequeue();
                }
            }
            catch { }
        }

        public List<string> GetQaulLog()
        {
            lock (qaulLogGate) { return new List<string>(qaulLog); }
        }

        public string GetQaulLogText()
        {
            lock (qaulLogGate) { return string.Join("\n", qaulLog); }
        }

        private string QaulUserName(string peerB58)
        {
            try
            {
                var list = qnode == null || qnode.Messaging == null ? null : qnode.Messaging.UserList();
                if (list != null)
                    foreach (var u in list)
                        if (u.Id == peerB58 && !string.IsNullOrEmpty(u.Name)) return u.Name;
            }
            catch { }
            return "qaul " + (peerB58 == null ? "?" : (peerB58.Length > 10 ? peerB58.Substring(0, 10) + "…" : peerB58));
        }

        private string QaulGroupName(string gidHex)
        {
            try
            {
                var groups = qnode == null || qnode.Messaging == null ? null : qnode.Messaging.GroupList();
                if (groups != null)
                    foreach (var g in groups)
                        if (g.Id == gidHex && !string.IsNullOrEmpty(g.Name)) return g.Name;
            }
            catch { }
            return "群 " + (gidHex == null ? "?" : gidHex.Substring(0, Math.Min(8, gidHex.Length)));
        }

        /// <summary>收到 qaul 公共 feed（floodsub topic "pages"）的消息。</summary>
        private void OnQaulFeed(string sender, string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            try
            {
                var msg = Msg.New(QaulFeedConv, "qaul:" + (sender ?? ""),
                                  "qaul " + Short(sender), "text", content);
                msg["src"] = "qaul-feed";

                // LC1：feed 是全网明文洪泛的，只有设了频道密码才可能解出内容
                if (UnsealIncoming(QaulFeedConv, msg, "qaul:" + (sender ?? ""), ChanLink.Qaul)) return;

                store.Add(msg, true);
                AppendHistory(msg);
                if (activeConv == QaulFeedConv) store.MarkRead(QaulFeedConv);
                Emit("message", msg);

                // 隧道：qaul 公共 feed → bitchat 公共频道（mesh）。前缀避免回环。
                var shown = Msg.Str(msg, "text");
                if (bitchatEnabled && bitchat != null && bitchat.Running && !shown.StartsWith("[bt] "))
                {
                    // 转发的是「线上形态」（密文就还是密文），不带密码的人依旧看不到
                    try { bitchat.SendToRoom(Convs.BitchatDefaultRoom, "[qaul] " + (sender ?? "qaul") + ": " + (MchatSeal.IsSealed(content) ? content : shown), "qaul"); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("QaulNode", "写入 feed 消息失败: " + ex.Message);
            }
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return L10n.T("节点");
            return s.Length <= 10 ? s : s.Substring(0, 10) + "…";
        }

        /// <summary>往 qaul 公共 feed 发一条消息（qaul 客户端的 Feed 页面能看到）。</summary>
        public bool PublishQaulFeed(string text)
        {
            var msg = Msg.New(QaulFeedConv, peerId, displayName, "text", text);
            // feed 会被洪泛到整个 qaul 网络且明文可读写，因此必须加密后才发出去
            var wire = SealOutgoing(QaulFeedConv, msg);

            if (qnode == null || !qnode.Running) return false;
            bool ok = qnode.PublishFeed(wire);
            msg["src"] = "qaul-feed-out";
            _dedupEngine.MarkSeen(Msg.Str(msg, "id"));
            store.Add(msg, true);
            AppendHistory(msg);
            Emit("message", msg);
            return ok;
        }

        /// <summary>进入 qaul 公共频道。</summary>
        public string OpenQaulFeed()
        {
            store.SetTitle(QaulFeedConv, L10n.T("qaul 公共频道"));
            SetActiveConv(QaulFeedConv);
            Emit("conv", new { conv = QaulFeedConv, title = L10n.T("qaul 公共频道") });
            return QaulFeedConv;
        }

        /* ---------- qaul 联系人 / 群组 / 静态节点 ---------- */

        /// <summary>
        /// 对方是否已经先私聊过本机。
        /// 2026-11-26 需求：不允许主动私聊纯 qaul 官方客户端；只有对方先私聊本机，
        /// 本机才有这条会话、才能回复。本系列客户端之间的私聊走 dm: 会话（StartDm），
        /// 不受此限制。回复消息本身走 SendMessage/Session → SendQaulViaNode，也不经此方法。
        /// </summary>
        private bool QaulDmSessionExists(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return false;
            var conv = Convs.QaulDmPrefix + peerB58;
            if (store == null) return false;
            return store.Count(conv) > 0 || store.Conversations().Exists(c => c.Id == conv);
        }

        /// <summary>进入与某个 qaul 用户的私聊（仅对方先私聊过本机时允许）。</summary>
        public string OpenQaulDm(string peerB58)
        {
            if (string.IsNullOrEmpty(peerB58)) return null;
            if (!QaulDmSessionExists(peerB58)) return null;
            var conv = Convs.QaulDmPrefix + peerB58;
            // 进入会话就装载对称派生密钥，使「已加密 / 安全码」状态立刻正确
            EnsureQaulDmKey(peerB58);
            store.SetTitle(conv, QaulUserName(peerB58));
            SetActiveConv(conv);
            Emit("conv", new { conv, title = QaulUserName(peerB58) });

            // 刚发现、还没换到公钥的节点：立刻索要，别等 15 秒的路由信息周期
            try
            {
                if (qnode != null && qnode.Messaging != null && !qnode.Messaging.HaveKey(peerB58))
                    qnode.RequestUser(peerB58);
            }
            catch { }
            return conv;
        }

        /// <summary>
        /// 已发现的 qaul 节点（邻居）列表：可以直接挑一个发消息。
        /// hasKey=false 表示还没换到公钥，这时发消息会先排队。
        /// </summary>
        public object GetQaulPeers()
        {
            var list = new List<object>();
            try
            {
                if (qnode == null) return list;
                foreach (var n in qnode.Neighbours())
                {
                    var peer = n.ContainsKey("peer") ? (string)n["peer"] : null;
                    if (string.IsNullOrEmpty(peer)) continue;
                    list.Add(new
                    {
                        peer,
                        shortId = n.ContainsKey("short") ? (string)n["short"] : peer.Substring(0, 10),
                        name = QaulUserName(peer),
                        addr = n.ContainsKey("addr") ? (string)n["addr"] : "",
                        hasKey = qnode.Messaging != null && qnode.Messaging.HaveKey(peer),
                        session = qnode.Crypto != null && qnode.Crypto.HasTransport(peer),
                        inRouting = qnode.HasRoute(peer),
                        pending = qnode.Messaging == null ? 0 : qnode.Messaging.PendingDmCountOf(peer),
                        conv = Convs.QaulDmPrefix + peer
                    });
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 给某个已发现的 qaul 节点发一条私聊（不要求先进会话）。
        /// 返回 queued=true 表示公钥还没到、消息已排队，公钥到手后会自动补发。
        /// </summary>
        public object SendQaulDm(string peerB58, string text)
        {
            if (string.IsNullOrEmpty(peerB58) || string.IsNullOrEmpty(text))
                return new { ok = false, queued = false, error = "peer/text required" };
            if (qnode == null || qnode.Messaging == null)
                return new { ok = false, queued = false, error = L10n.T("qaul 节点未运行") };
            if (peerB58 == qnode.Messaging.MyIdB58)
                return new { ok = false, queued = false, error = L10n.T("不能发给自己") };
            // 需求：纯 qaul 官方客户端不能由本机主动私聊；只有对方先私聊过本机才有会话可回复。
            if (!QaulDmSessionExists(peerB58))
                return new { ok = false, queued = false, error = L10n.T("对方尚未私聊本机，不能主动发起 qaul 私聊") };

            var conv = OpenQaulDm(peerB58);
            // LC1：qaul 私钥只保证"来源可信"，内容机密性由本层负责
            var wire = SealOutgoing(conv, Msg.New(conv, peerId, displayName, "text", text));
            byte[] dmMid;
            var sent = qnode.Messaging.SendDm(peerB58, wire, out dmMid);
            var queued = !sent && qnode.Messaging.PendingDmCountOf(peerB58) > 0;
            if (queued) { try { qnode.RequestUser(peerB58); } catch { } }

            Dictionary<string, object> m = null;
            if (sent || queued)
            {
                m = AlreadyStored(conv, text, sent ? dmMid : null);
                if (queued) m["pending"] = true;
            }
            return new
            {
                ok = sent || queued,
                queued,
                conv,
                id = m == null ? null : Msg.Str(m, "id"),
                pending = qnode.Messaging.PendingDmCountOf(peerB58)
            };
        }

        /// <summary>主动向邻居索要某个用户的公钥与昵称。</summary>
        public bool RequestQaulUser(string peerB58)
        {
            if (qnode == null || string.IsNullOrEmpty(peerB58)) return false;
            return qnode.RequestUser(peerB58);
        }

        /// <summary>新建 qaul 群组并进入。</summary>
        public string CreateQaulGroup(string name)
        {
            if (qnode == null || qnode.Messaging == null) return null;
            var g = qnode.Messaging.CreateGroup(name);
            var conv = Convs.QaulGroupPrefix + g.Id;
            store.SetTitle(conv, g.Name);
            SetActiveConv(conv);
            Emit("conv", new { conv, title = g.Name });
            return conv;
        }

        /// <summary>邀请某个已知 qaul 用户进群。</summary>
        public bool InviteToQaulGroup(string groupIdHex, string peerB58)
        {
            if (qnode == null || qnode.Messaging == null) return false;
            return qnode.Messaging.Invite(groupIdHex, peerB58);
        }

        /// <summary>把某人移出 qaul 群（通知本人 + 广播新成员表）。</summary>
        public bool RemoveFromQaulGroup(string groupIdHex, string peerB58)
        {
            if (qnode == null || qnode.Messaging == null) return false;
            return qnode.Messaging.RemoveMember(groupIdHex, peerB58);
        }

        /// <summary>退出 qaul 群（本地移除，不通知其他人）。</summary>
        public bool LeaveQaulGroup(string groupIdHex)
        {
            if (qnode == null || qnode.Messaging == null) return false;
            var ok = qnode.Messaging.LeaveGroup(groupIdHex);
            if (ok) Emit("conv", new { conv = Convs.QaulGroupPrefix + groupIdHex, title = "", removed = true });
            return ok;
        }

        /// <summary>添加静态节点（跨网段/公网直连）。</summary>
        public bool AddQaulStaticPeer(string address)
        {
            if (qnode == null) return false;
            return qnode.AddStaticPeer(address);
        }

        public object GetQaulUsers()
        {
            var list = new List<object>();
            try
            {
                var users = qnode == null || qnode.Messaging == null
                          ? new List<QaulUser>() : qnode.Messaging.UserList();
                foreach (var u in users)
                {
                    // 绝不把「节点身份」当联系人：它是 libp2p 传输身份，不是聊天账号。
                    // 同一台官方客户端若有两条记录（一条账号、一条节点身份），只保留账号那条，
                    // 否则会出现「同一个人捕捉两次、其中一条以 peerid 显示且收发不了」。
                    if (u.Node || qnode.Messaging.IsNodeIdentity(u.Id)) continue;
                    list.Add(new
                    {
                        id = u.Id,
                        name = QaulUserName(u.Id),
                        hasKey = !string.IsNullOrEmpty(u.KeyHex),
                        conv = Convs.QaulDmPrefix + u.Id
                    });
                }
            }
            catch { }
            return list;
        }

        public object GetQaulGroups()
        {
            var list = new List<object>();
            try
            {
                var groups = qnode == null || qnode.Messaging == null
                           ? new List<QaulGroup>() : qnode.Messaging.GroupList();
                foreach (var g in groups)
                {
                    var mem = new List<object>();
                    foreach (var m in g.Members)
                    {
                        mem.Add(new
                        {
                            id = m.UserId,
                            name = QaulUserName(m.UserId),
                            role = m.Role,
                            state = m.State,
                            self = m.UserId == (qnode == null || qnode.Messaging == null
                                                ? "" : qnode.Messaging.MyIdB58)
                        });
                    }
                    list.Add(new
                    {
                        id = g.Id,
                        name = g.Name,
                        members = g.Members.Count,
                        conv = Convs.QaulGroupPrefix + g.Id,
                        revision = g.Revision,
                        memberList = mem
                    });
                }
            }
            catch { }
            return list;
        }

        [Java.Interop.Export("getQaulUsers")]
        [JavascriptInterface]
        public string GetQaulUsersJson() { return JsonConvert.SerializeObject(GetQaulUsers()); }

        [Java.Interop.Export("getQaulGroups")]
        [JavascriptInterface]
        public string GetQaulGroupsJson() { return JsonConvert.SerializeObject(GetQaulGroups()); }

        [Java.Interop.Export("openQaulDm")]
        [JavascriptInterface]
        public string OpenQaulDmJs(string peer) { return OpenQaulDm(peer); }

        [Java.Interop.Export("createQaulGroup")]
        [JavascriptInterface]
        public string CreateQaulGroupJs(string name) { return CreateQaulGroup(name); }

        [Java.Interop.Export("removeFromQaulGroup")]
        [JavascriptInterface]
        public bool RemoveFromQaulGroupJs(string groupIdHex, string peer)
        {
            return RemoveFromQaulGroup(groupIdHex, peer);
        }

        /// <summary>qaul 网络节点状态（界面/网页端展示用）。</summary>
        public object GetQaulStatus()
        {
            var id = Identity;
            return new
            {
                running = qnode != null && qnode.Running,
                port = qnode == null ? 0 : qnode.Port,
                peerId = id == null ? null : id.QaulPeerId,
                neighbours = qnode == null ? 0 : qnode.NeighbourCount,
                infoRx = qnode == null ? 0 : qnode.InfoRx,
                feedConv = QaulFeedConv,
                items = qnode == null ? new List<Dictionary<string, object>>() : qnode.Neighbours(),
                ipv6 = qnode != null && qnode.Ipv6Available,
                staticPeers = qnode == null ? new List<string>() : qnode.StaticPeers(),
                users = GetQaulUsers(),
                groups = GetQaulGroups(),
                sessions = qnode == null || qnode.Crypto == null ? 0 : qnode.Crypto.TransportCount,
                unconfirmed = qnode == null || qnode.Messaging == null ? 0 : qnode.Messaging.UnconfirmedCount,
                pendingDm = qnode == null || qnode.Messaging == null ? 0 : qnode.Messaging.PendingDmCount,
                // 公钥交换链路的诊断计数：infoRx → userReqSent → userRespRx
                userReqSent = qnode == null ? 0 : qnode.UserReqSent,
                userRespRx = qnode == null ? 0 : qnode.UserRespRx,
                lastUserRespCount = qnode == null ? 0 : qnode.LastUserRespCount,
                lastInfoNode = qnode == null ? "" : qnode.LastInfoNode,
                lastInfoSigOk = qnode != null && qnode.LastInfoSignatureOk,
                peers = GetQaulPeers()
            };
        }

        [Java.Interop.Export("getQaulStatus")]
        [JavascriptInterface]
        public string GetQaulStatusJson()
        {
            return JsonConvert.SerializeObject(GetQaulStatus());
        }

        [Java.Interop.Export("publishQaulFeed")]
        [JavascriptInterface]
        public bool PublishQaulFeedJs(string text)
        {
            return PublishQaulFeed(text);
        }

        /* ===================== 会话 API（原生界面 / 网页端共用） ===================== */

        public string SelfId { get { return peerId; } }
        public int QaulPort { get { return qaul == null ? 0 : qaul.Port; } }
        public string ActiveConv { get { return activeConv; } }

        public void SetActiveConv(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return;
            activeConv = conv;
            store.MarkRead(conv);
            try { ConversationsChanged?.Invoke(); } catch { }
        }

        /// <summary>
        /// 重命名一个会话（房间 / qaul 群组等）。标题写入唯一的 ChatStore
        /// （原生会话条、网页端、悬浮窗都从这里读，保证全局一致），
        /// 并发一条 rename 控制帧广播给其它设备，对端收到后同步改名。
        /// 私聊（dm/qaul 私聊）的标题由对端在线昵称决定，改名不持久，忽略即可。
        /// </summary>
        public void RenameConversation(string conv, string newTitle)
        {
            if (string.IsNullOrEmpty(conv) || conv == Convs.WebConv) return;
            newTitle = (newTitle ?? "").Trim();
            if (newTitle.Length == 0) return;

            // 1) 本地立即生效（单一数据源，界面各处一致）
            store.SetTitle(conv, newTitle);
            try { ConversationsChanged?.Invoke(); } catch { }
            Emit("convrename", new { conv, title = newTitle });

            // 2) 广播给其它设备同步改名（双渠道 + qaul，control 帧不进历史）
            try
            {
                var msg = Msg.Control(conv, MsgOp.Rename);
                msg["text"] = newTitle;
                msg["from"] = peerId;
                msg["fromName"] = displayName;
                msg["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _dedupEngine.MarkSeen(Msg.Str(msg, "id"));
                Deliver(msg, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "广播改名失败: " + ex.Message);
            }
        }

        /// <summary>会话列表（含未读数），并补齐私聊的在线昵称。</summary>
        public List<Conversation> GetConversations()
        {
            var list = store.Conversations();
            var now = SysEnv.TickCount;

            // 默认频道永远在列表里（它默认只监听 qaul）
            var def = Convs.Chan(ChannelRegistry.DefaultChannelName);
            if (!list.Exists(c => c.Id == def))
                list.Add(new Conversation { Id = def, Title = Convs.TitleOf(def) });

            // 频道必须「手动加入」才进列表。
            // store.Conversations() 是按「有消息的会话」生成的，对端在别的频道里说一句，
            // 这个 chan:xxx 就会带着消息冒出来——看起来就像被别人拉进了频道。
            // 这里按注册表过滤：只保留用户自己加入过的频道（默认频道始终保留）。
            //
            // 2026-11-26 需求：退出后台后消息列表只保留频道（mesh + 手动加入）。消息会被清空，
            // 于是这些频道在 store 里没有任何条目；必须按注册表把它们**补回**列表，
            // 否则「空频道」会从界面上消失。频道注册表本身已持久化（Channels.cs）。
            try
            {
                var joined = new HashSet<string>(StringComparer.Ordinal);
                var joinedChannels = channels.Joined();
                foreach (var c in joinedChannels)
                    if (c != null && !string.IsNullOrEmpty(c.Conv)) joined.Add(c.Conv);
                list.RemoveAll(c => Convs.IsChan(c.Id) && c.Id != def && !joined.Contains(c.Id));
                // 把已加入但「零消息」的频道补进来（退出清理后就是这种状态）。
                foreach (var id in joined)
                {
                    if (list.Exists(c => c.Id == id)) continue;
                    list.Add(new Conversation
                    {
                        Id = id,
                        IsDm = false,
                        Title = Convs.TitleOf(id),
                        UpdatedAt = 0
                    });
                }
                if (!list.Exists(c => c.Id == def))
                    list.Add(new Conversation { Id = def, Title = Convs.TitleOf(def) });
                // 标题一律现算：老版本把频道标题存成了「#mesh」，历史里还留着，显示时统一抹掉 #
                foreach (var c in list)
                    if (Convs.IsChan(c.Id)) c.Title = Convs.TitleOf(c.Id);
            }
            catch { }

            // 顶栏只保留「真正聊过」的私聊：一条消息都没有的设备会话直接不显示。
            // 这样同网段几十台设备也不会把顶栏撑爆；想找设备走菜单里的
            // 「qaul 在线设备」/「bitchat 在线设备」。
            list.RemoveAll(c => Convs.IsDm(c.Id) && store.Count(c.Id) <= 0);

            // 解密失败的占位文本不进入会话预览（解密失败一律静默，不打扰用户）
            foreach (var c in list)
            {
                if (!string.IsNullOrEmpty(c.LastText)
                    && c.LastText.StartsWith(L10n.T("[无法解密]"), StringComparison.Ordinal))
                    c.LastText = "";
            }

            foreach (var c in list)
            {
                if (!c.IsDm) continue;
                var other = Convs.DmPeer(c.Id, peerId);
                Peer p;
                if (!string.IsNullOrEmpty(other) &&
                    (peers.TryGetValue(other, out p) || p2pNodes.TryGetValue(other, out p)) &&
                    now - p.LastSeen < PeerTimeoutMs)
                {
                    c.Title = p.Name;
                    c.Subtitle = L10n.T("在线 · ") + p.Address;
                }
                else if (string.IsNullOrEmpty(c.Subtitle))
                {
                    c.Subtitle = L10n.T("离线");
                }
            }

            list.Sort((a, b) =>
            {
                // 默认频道置顶，其余按活跃时间
                if (a.Id == def) return -1;
                if (b.Id == def) return 1;
                return b.UpdatedAt.CompareTo(a.UpdatedAt);
            });
            return list;
        }

        public List<Dictionary<string, object>> GetMessages(string conv, int limit)
        {
            if (string.IsNullOrEmpty(conv)) conv = activeConv;
            var list = store.Messages(conv, limit <= 0 ? 300 : limit);
            // 解密失败的占位消息（历史遗留）不再展示：解密失败一律静默，避免反复打扰
            try { list = list.FindAll(m => !IsLockedPlaceholder(m)); } catch { }
            return list;
        }

        /// <summary>是否为「解密失败」的占位消息（enc=locked 或正文以 [无法解密] 开头）。</summary>
        private static bool IsLockedPlaceholder(Dictionary<string, object> m)
        {
            if (m == null) return false;
            if (string.Equals(Msg.Str(m, "enc"), "locked", StringComparison.Ordinal)) return true;
            var t = Msg.Str(m, "text");
            return !string.IsNullOrEmpty(t) && t.StartsWith(L10n.T("[无法解密]"), StringComparison.Ordinal);
        }

        /// <summary>
        /// 画板（Painter / inkcanvas）通过生态发现接入，id 为 painter-*、名称为「画板」。
        /// 它有独立的「画板设备」入口（UDP 21118），因此不参与「附近设备」列表与在线计数。
        /// </summary>
        private static bool IsPainterPeer(Peer p)
        {
            if (p == null) return false;
            if (!string.IsNullOrEmpty(p.Id) &&
                p.Id.StartsWith("painter-", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(p.Name, L10n.T("画板"), StringComparison.Ordinal);
        }

        /// <summary>当前在线的其它设备（原生界面「私聊」选择器用）。画板已排除。</summary>
        public List<Dictionary<string, string>> OnlinePeers()
        {
            var now = SysEnv.TickCount;
            var list = new List<Dictionary<string, string>>();
            foreach (var p in peers.Values)
            {
                if (now - p.LastSeen >= PeerTimeoutMs) continue;
                if (p.ViaRelay) continue; // 中继那边的设备只是通信渠道，不纳入本地发现
                if (IsPainterPeer(p)) continue;
                list.Add(new Dictionary<string, string>
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["ip"] = p.Address,
                    ["port"] = p.Port.ToString(),
                    ["qp"] = p.QaulPort.ToString(),
                    // 远控端口（>0 表示这台设备开放了被远控）
                    ["rd"] = p.RemotePort.ToString(),
                    // 设备码（9 位，可能为空）：界面上的“远程控制”直接拿它预填
                    ["dc"] = p.DeviceCode ?? "",
                    // 网页远控基址（TV/TVCMS 等）：非空时界面可给出「打开网页远控」入口
                    ["ws"] = p.WebRemote ?? ""
                });
            }
            return list;
        }

        /* ---------- 供 App 内网页端（Assets/www SPA）调用的会话/删除接口 ---------- */

        [Java.Interop.Export("getConversations")]
        [JavascriptInterface]
        public string GetConversationsJson()
        {
            var list = new List<object>();
            foreach (var c in GetConversations())
            {
                list.Add(new
                {
                    id = c.Id,
                    kind = c.IsDm ? "dm" : "room",
                    title = c.Title,
                    subtitle = c.Subtitle,
                    unread = c.Unread,
                    updatedAt = c.UpdatedAt,
                    last = c.LastText
                });
            }
            return JsonConvert.SerializeObject(new { items = list, conv = activeConv, self = peerId });
        }

        [Java.Interop.Export("getMessages")]
        [JavascriptInterface]
        public string GetMessagesJson(string conv, int limit)
        {
            var items = GetMessages(conv, limit > 0 ? limit : 300);
            return JsonConvert.SerializeObject(items);
        }

        [Java.Interop.Export("selectConv")]
        [JavascriptInterface]
        public void SelectConv(string conv)
        {
            SetActiveConv(conv);
        }

        [Java.Interop.Export("joinRoom")]
        [JavascriptInterface]
        public string JoinRoomJson(string code)
        {
            return JsonConvert.SerializeObject(new { conv = JoinRoom(code), title = Convs.TitleOf(Convs.Room(code)) });
        }

        [Java.Interop.Export("startDm")]
        [JavascriptInterface]
        public string StartDmJson(string otherPeerId)
        {
            var conv = StartDm(otherPeerId);
            return JsonConvert.SerializeObject(new { conv });
        }

        [Java.Interop.Export("deleteMessage")]
        [JavascriptInterface]
        public bool DeleteMessageJs(string id)
        {
            return DeleteMessage(id);
        }

        [Java.Interop.Export("clearConversationJs")]
        [JavascriptInterface]
        public int ClearConversationJs(string conv)
        {
            return ClearConversation(conv);
        }

        [Java.Interop.Export("clearAllMessages")]
        [JavascriptInterface]
        public int ClearAllMessagesJs()
        {
            return ClearAllMessages();
        }

        [Java.Interop.Export("renameConversation")]
        [JavascriptInterface]
        public void RenameConversationJs(string conv, string title)
        {
            RenameConversation(conv, title);
        }

        /* ---------- 身份 / 指纹（App 内网页端可调用） ---------- */

        [Java.Interop.Export("getIdentity")]
        [JavascriptInterface]
        public string GetIdentityJson()
        {
            return JsonConvert.SerializeObject(GetIdentitySummary());
        }

        [Java.Interop.Export("rotateIdentity")]
        [JavascriptInterface]
        public string RotateIdentityJs(string scope)
        {
            IdentityScope s = IdentityScope.All;
            if (string.Equals(scope, "qaul", StringComparison.OrdinalIgnoreCase)) s = IdentityScope.Qaul;
            else if (string.Equals(scope, "bitchat", StringComparison.OrdinalIgnoreCase)) s = IdentityScope.Bitchat;
            var id = RotateIdentity(s);
            return id == null ? null : JsonConvert.SerializeObject(id.ToSummary());
        }

        [Java.Interop.Export("getChannelStatus")]
        [JavascriptInterface]
        public string GetChannelStatusJson()
        {
            return JsonConvert.SerializeObject(ChannelStatus());
        }

        [Java.Interop.Export("setDualChannel")]
        [JavascriptInterface]
        public void SetDualChannelJs(bool on)
        {
            SetDualChannel(on);
        }

        /* ===================== 统一频道（频道=聊天室，链路=传输方式） ===================== */

        /// <summary>
        /// 老版本的默认房间 room:mesh 迁移到统一频道 chan:mesh：
        /// 把已有消息整条搬到新会话键，然后移除老会话，避免顶栏同时出现两个「#mesh」。
        /// </summary>
        private void MigrateLegacyRoomToChannel()
        {
            try
            {
                var oldConv = Convs.Room(Convs.DefaultRoom);
                var newConv = Convs.Chan(ChannelRegistry.DefaultChannelName);
                if (oldConv == newConv) return;
                if (store.Count(oldConv) <= 0) return;
                if (store.Count(newConv) > 0) return;   // 新频道已有消息，放弃迁移避免重复

                foreach (var m in store.Messages(oldConv, 0))
                {
                    var copy = new Dictionary<string, object>(m);
                    copy["conv"] = newConv;
                    store.Add(copy, true);
                }
                store.SetTitle(newConv, Convs.TitleOf(newConv));
                store.RemoveConversation(oldConv);
                Android.Util.Log.Info("Channels", "已把 " + oldConv + " 的历史消息迁移到 " + newConv);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Channels", "迁移老房间失败: " + ex.Message);
            }
        }

        /// <summary>频道列表（供「频道列表与链路设置」对话框使用）。</summary>
        public List<Dictionary<string, object>> GetChannelList()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                foreach (var c in channels.Joined())
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["name"] = c.Name,
                        ["title"] = ChannelRegistry.Strip(c.Title) ?? c.Name,
                        ["conv"] = c.Conv,
                        ["links"] = string.Join(",", c.LinkList()),
                        ["badge"] = c.Badge()
                    });
                }
            }
            catch { }
            return list;
        }

        /// <summary>加入一个频道。links 为空则默认只监听 qaul。</summary>
        public string JoinChannel(string name, string linksCsv = null)
        {
            var ch = channels.Ensure(name);
            if (!string.IsNullOrEmpty(linksCsv))
            {
                var arr = linksCsv.Split(',').Select(s => s.Trim()).Where(s => ChanLink.IsKnown(s)).ToList();
                channels.SetLinks(ch.Name, arr.Count == 0 ? new List<string> { ChanLink.DefaultLink } : arr);
                ch = channels.Ensure(ch.Name);
            }
            channels.MarkJoined(ch.Name);
            var conv = ch.Conv;
            store.SetTitle(conv, ChannelRegistry.Strip(ch.Title) ?? ch.Name);
            EnsureChannelLinksActive(conv);
            SetActiveConv(conv);
            Emit("conv", new { conv, title = ChannelRegistry.Strip(ch.Title) ?? ch.Name });
            Emit("channels", new { count = channels.Joined().Count });
            return conv;
        }

        public bool LeaveChannel(string name)
        {
            var ch = channels.Get(name);
            if (ch == null) return false;
            return channels.Leave(ch.Name);
        }

        /// <summary>设置某频道启用的链路。</summary>
        public bool SetChannelLinks(string name, string linksCsv)
        {
            var ch = channels.Get(name);
            if (ch == null) return false;
            var arr = (linksCsv ?? "").Split(',').Select(s => s.Trim())
                          .Where(s => ChanLink.IsKnown(s)).ToList();
            channels.SetLinks(ch.Name, arr.Count == 0 ? new List<string> { ChanLink.DefaultLink } : arr);
            // 立刻把刚打开的链路拉起来
            EnsureChannelLinksActive(ch.Conv);
            Emit("channels", new { count = channels.Joined().Count });
            return true;
        }

        /// <summary>移除除初始频道外的所有频道（「彻底退出」用）。返回移除数量。</summary>
        public int RemoveNonDefaultChannels()
        {
            int n = 0;
            try
            {
                var def = ChannelRegistry.DefaultChannelName;
                foreach (var ch in channels.Joined())
                {
                    if (ch == null || ch.Name == def) continue;
                    try { if (channels.Leave(ch.Name)) n++; } catch { }
                }
                Emit("channel", ChannelStatus());
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Channel", "移除频道失败: " + ex.Message);
            }
            return n;
        }

        /// <summary>某频道启用的链路。</summary>
        public List<string> GetChannelLinks(string convOrName)
        {
            return channels.LinksOf(convOrName);
        }

        /// <summary>
        /// 切换进某频道时，把该频道声明需要的链路拉起来。
        /// 需求：bitchat「只有切换进去才开始启用」——这里就是那个触发点。
        /// </summary>
        public void EnsureChannelLinksActive(string conv)
        {
            try
            {
                var ch = Convs.IsChan(conv ?? "") ? channels.ByConv(conv) : channels.Get(conv);
                if (ch == null) return;
                if (!scanEnabled && ch.Has(ChanLink.Bitchat) && !BitchatEnabled) EnableBitchat();
                if (ch.Has(ChanLink.P2p) && !P2pEnabled) EnableP2p();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Channels", "激活频道链路失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 在频道里发一条消息：按该频道**启用的所有链路**并行投递。
        /// 链路只决定怎么传，聊天室始终是同一个 conv，所以各端互通。
        /// </summary>
        private Dictionary<string, object> SendInChannel(string conv, string text, string kind)
        {
            var ch = Convs.IsChan(conv) ? channels.ByConv(conv) : null;
            var m = Msg.New(conv, peerId, displayName, kind, text);
            m["src"] = "chan-out";
            var links = EffectiveLinks(ch);
            m["links"] = string.Join(",", links);

            // 关键修复：本地落库的 id 必须是**真实 qaul message id**（CRC64(group)‖
            // CRC64(sender)‖index），不能用 Msg.New 的随机 GUID。原因：
            //   · 发送端与接收端 id 不一致 → 对端按 id 去重时救不了发送端自己，
            //     消息经中继绕回来会在会话里重影；
            //   · MarkSeen 标随机 id → 帧直投 / qaul 中继 / bitchat 三条链路的
            //     跨链路去重全部失效（老实现「只对非直连设备启用中继」正是在绕开这个）。
            // 而且 wire 是从 m 封的（SealOutgoing(conv, m)），id 统一后三条链路
            // 天然带同一个 id，跨链路去重才真正生效。
            uint? chanIdx = null;
            if (qnode != null && qnode.Messaging != null && !string.IsNullOrEmpty(qnode.Messaging.MyIdB58))
            {
                try
                {
                    var mm = qnode.Messaging;
                    var gid = Convs.GroupId(conv);
                    if (gid != null && gid.Length == 16)
                    {
                        chanIdx = mm.NextIndex("relay:" + conv);
                        // 【关键：不能用 qaul 的确定性 id 当 Mchat 消息 id】
                        // 它 = CRC64(group) ‖ CRC64(本机 qaul 身份) ‖ index。两台设备的
                        // qaul 默认身份可能完全相同，index 又各自从同一序列递增，
                        // 于是双方算出的 id 一模一样：对端 ChatStore 把自己的 id 当成
                        // 「已见过」→ 双向消息全部按重复丢弃。
                        // 这正是「App 之间收不到，只能收到电脑端（电脑端用随机 GUID）」的根因。
                        // qaul 协议内部的 message id 由 SendQaulViaNode(chanIdx) 自行生成，
                        // 这里保持随机 GUID 作为跨设备唯一的去重键。
                    }
                }
                catch { }
            }

            _dedupEngine.MarkSeen(Msg.Str(m, "id"));
            store.Add(m, true);
            AppendHistory(m);
            Emit("message", m);

            // LC1：正文（连同昵称/类型）先加密，之后每条链路搬的都是同一份密文
            var wire = SealOutgoing(conv, m);
            var wireMsg = WireCopy(m, wire);
            try
            {
                Android.Util.Log.Info("Send", "发送 conv=" + conv + " id=" + Msg.Str(m, "id")
                    + " enc=" + Msg.Str(wireMsg, "enc") + " links=" + (links == null ? "null" : string.Join(",", links)));
            }
            catch { }

            // 交给出站队列：单线程按链路令牌桶限速发送，失败按退避重传。
            // 以前这里是「每条链路一次 Task.Run + 失败只写日志」，瞬时故障会把消息永久丢掉。
            EnqueueOutbound(conv, wire, wireMsg, null, links, false, chanIdx);

            return m;
        }

        /// <summary>
        /// 把一条已经封好的报文排进出站队列。
        /// </summary>
        /// <param name="wire">密文正文（qaul 链路单独要用）。</param>
        /// <param name="wireMsg">线上副本：正文已是密文，携带着 id / conv / hop。</param>
        /// <param name="links">目标链路；发成功一条就移出一条，全空即送达。</param>
        /// <param name="relay">true = 中继流量（存活时间更短）。</param>
        private void EnqueueOutbound(string conv, string wire, Dictionary<string, object> wireMsg, byte[] body,
                                     List<string> links, bool relay, uint? chanIdx)
        {
            try
            {
                if (links == null || links.Count == 0 || wireMsg == null) return;
                var item = new MeshOutItem
                {
                    Id = Msg.Str(wireMsg, "id") ?? Guid.NewGuid().ToString("N"),
                    Conv = conv,
                    Wire = wire,
                    Msg = wireMsg,
                    ChanIdx = chanIdx,
                    Body = body,
                    Links = new List<string>(links),
                    Relay = relay
                };
                item.Payload = JsonConvert.SerializeObject(wireMsg);
                // 同一个 id 已在队列里时 Enqueue 返回 false：本机发送 + 中继回环不用各发一遍
                outbox.Enqueue(item);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mesh", "入队失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 出站队列调用的「单链路发送」。
        ///
        /// 返回三态而不是 bool，是为了区分「链路坏了」和「链路好但暂时没人」：
        ///   · Sent   —— 成功，记 RTT；
        ///   · NoPeer —— 没有对端/链路没开，报文留着等，**不算失败**（否则会把正常链路熔断掉）；
        ///   · Failed —— 真异常，累加失败计数，连续 3 次触发熔断。
        /// </summary>
        private SendOutcome TrySendOneLink(MeshOutItem item, string link)
        {
            if (item == null || string.IsNullOrEmpty(link)) return SendOutcome.Failed;
            var conv = item.Conv ?? "";

            switch (link)
            {
                case ChanLink.Qaul:
                {
                    if (qaul == null || !qaul.Running) return SendOutcome.NoPeer;
                    // 关键：以前无论有没有对端都报 Sent，消息其实没出门却被当作已送达，
                    // 队列直接出队 → 「发出去但对方收不到」。
                    // 现在按真实投递结果判定：没有目标就报 NoPeer，报文留在队列等对端出现。
                    bool sent = SendQaulRaw(conv, item.Wire, item.Msg);
                    if (UseQaulNativeMessaging) SendQaulViaNode(conv, item.Wire, 32, item.ChanIdx);
                    mesh.NoteTx(ChanLink.Qaul, 0);
                    if (item.Relay) mesh.NoteRelayOut(ChanLink.Qaul);
                    return sent ? SendOutcome.Sent : SendOutcome.NoPeer;
                }

                case ChanLink.Bitchat:
                {
                    if (!BitchatEnabled || bitchat == null || !bitchat.Running) return SendOutcome.NoPeer;
                    if (BtNeighborCount() <= 0) return SendOutcome.NoPeer;
                    var env = EncodeMeshEnvelope(item.Payload);
                    if (env == null) return SendOutcome.Failed;    // 超过 1500 字节，蓝牙搬不动，别重试
                    bitchat.SendToRoom(Convs.ChanName(conv) ?? Convs.DefaultRoom, env, displayName);
                    mesh.NoteTx(ChanLink.Bitchat, 0);
                    if (item.Relay) mesh.NoteRelayOut(ChanLink.Bitchat);
                    return SendOutcome.Sent;
                }

                case ChanLink.P2p:
                {
                    if (!P2pEnabled || p2p == null) return SendOutcome.NoPeer;
                    if (p2pNodes == null || p2pNodes.Count == 0) return SendOutcome.NoPeer;
                    p2p.BroadcastFrame(item.Payload, item.Body, tcpPort);
                    mesh.NoteTx(ChanLink.P2p, item.Body == null ? item.Payload.Length : item.Body.Length);
                    if (item.Relay) mesh.NoteRelayOut(ChanLink.P2p);
                    return SendOutcome.Sent;
                }

                case ChanLink.Lan:
                {
                    // 自有通道：不再因为 DualChannel 开关被跳过 —— 它是本程序最可靠的一条。
                    if (Convs.IsDm(conv))
                    {
                        // 私聊也走中继器：局域网内直连对端；同时投递给所有静态中转站，
                        // 这样跨网段/公网时对端不在 peers 表里、直连不可达时，私聊仍能经中继站送达。
                        bool direct = SendFrameToDm(conv, item.Payload, item.Body);
                        int relayed = SendToStaticRelays(item.Payload, item.Body, null);
                        if (!direct && relayed <= 0) return SendOutcome.NoPeer;
                    }
                    else
                    {
                        // mesh 频道：只投给完成 hello↔probe↔hello 双向确认的局域网设备，
                        // 且永不交给静态中转站。WLAN Direct 设备同样走这条 LAN 路径。
                        bool meshLocal = IsMeshConv(conv);
                        Func<Peer, bool> filter = meshLocal
                            ? new Func<Peer, bool>(p => LanPeerGate.IsConfirmed(p.Id))
                            : null;
                        if (BroadcastFrame(item.Payload, item.Body, item.Conv, meshLocal, filter) <= 0)
                        {
                            // 还没有已知对端：立刻发一次 UDP 组播探测，
                            // 让局域网里的设备马上回连，而不是干等下一轮信标（最多几秒）。
                            try { AnnounceSelfUdp(true); } catch { }
                            return SendOutcome.NoPeer;
                        }
                    }
                    mesh.NoteTx(ChanLink.Lan, item.Body == null ? item.Payload.Length : item.Body.Length);
                    if (item.Relay) mesh.NoteRelayOut(ChanLink.Lan);
                    return SendOutcome.Sent;
                }
            }
            return SendOutcome.Failed;
        }

        /// <summary>该 IP 是否属于 WLAN Direct（Wi-Fi P2P）：P2P 网段 192.168.49.x/48.x，或已组网的 P2P 对端。</summary>
        private bool IsP2pAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (ip.StartsWith("192.168.49.") || ip.StartsWith("192.168.48.")) return true;
            try
            {
                if (p2p != null)
                    foreach (var p in p2p.Linked())
                        if (!string.IsNullOrEmpty(p.Ip) && p.Ip == ip) return true;
            }
            catch { }
            return false;
        }

        /// <summary>默认 mesh 频道：只在局域网内、且完成双向互通确认的设备间传输。</summary>
        private static bool IsMeshConv(string conv)
        {
            return string.Equals(conv ?? "", Convs.Chan(ChannelRegistry.DefaultChannelName), StringComparison.Ordinal);
        }

        /// <summary>
        /// 中继模式：配置了静态中继站，或 qaul 已发生过中继转发。
        /// 中继只是「多一条联网通信渠道」——本地局域网发现与聊天全部保留，
        /// 不应因为连了中继就关掉局域网设备发现、私聊或未加入频道。
        /// 中继那边的远端设备不纳入本地发现列表（见 IsRelayEndpointAddress），
        /// mesh 频道也绝不经中继流转（见 IngestIncoming / SyncAcrossLinks）。
        /// </summary>
        private bool _relayRestrictionsApplied;
        public bool RelayModeActive
        {
            get
            {
                try { lock (staticRelays) if (staticRelays.Count > 0) return true; } catch { }
                try { if (qnode != null && qnode.Relayed > 0) return true; } catch { }
                return false;
            }
        }

        /// <summary>
        /// 中继已启用时只记录一次状态供界面提示，不再关闭任何本地发现能力：
        /// 局域网组播 / 蓝牙 / WLAN Direct / mDNS 照常工作，连接中继的设备仍能
        /// 在本地局域网正常发现彼此、正常聊天。
        /// </summary>
        private void ApplyRelayModeRestrictions()
        {
            if (_relayRestrictionsApplied || !RelayModeActive) return;
            _relayRestrictionsApplied = true;
            try
            {
                Android.Util.Log.Info("Mchat", "中继已启用：新增联网通信渠道；本地局域网发现与聊天保持不变，mesh 频道不经中继");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "记录中继状态失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 来源地址是否为已配置的中继端点。用于把「中继那边的设备」排除在本地发现之外：
        /// 中继站地址本身不做为可直连的局域网设备登记，避免远端设备混进附近设备列表。
        /// </summary>
        private bool IsRelayEndpointAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            try
            {
                string normalized = NormalizeHost(address);
                lock (staticRelays)
                    foreach (var r in staticRelays)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Host)) continue;
                        if (string.Equals(NormalizeHost(r.Host), normalized, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 频道实际生效的链路集合 —— **不再按频道单独配置**，统一使用 App 全局「通信渠道设置」：
        ///   qaul 是否开启、是否「局域网直连」、WLAN Direct 是否开启、蓝牙是否开启。
        /// 这样用户只需在全局设置里管一次，所有频道自动用同一套接入渠道。
        /// </summary>
        private List<string> EffectiveLinks(Channel ch)
        {
            var links = new List<string>();
            try
            {
                // 默认 mesh 频道强制只走局域网：防止有人借中继节点向外广播不良信息。
                // WLAN Direct 视为本机另一张网卡，仍归入局域网链路。
                if (ch != null && string.Equals(ch.Name, ChannelRegistry.DefaultChannelName, StringComparison.OrdinalIgnoreCase))
                {
                    links.Add(ChanLink.Lan);
                    // WLAN Direct 视为本机另一张网卡，同属局域网：mesh 也走 P2P 链路，
                    // 但仍受 LanPeerGate 双向确认与「不经静态中转站」约束。
                    if (P2pEnabled) links.Add(ChanLink.P2p);
                    return links;
                }

                if (QaulEnabled) links.Add(ChanLink.Qaul);
                links.Add(ChanLink.Lan);          // 自有通道常开（不再受 DualChannel 开关影响）
                if (P2pEnabled) links.Add(ChanLink.P2p);
                if (BitchatEnabled) links.Add(ChanLink.Bitchat);
            }
            catch { }
            if (links.Count == 0) links.Add(ChanLink.Qaul);   // 兜底：主链路
            return links;
        }

        /// <summary>
        /// 跨链路中继：一条消息从某条链路进来后，转发到该频道启用的**其它**链路，
        /// 使「只连蓝牙的 A ↔ 同时连蓝牙和 P2P 的 B ↔ 只连 P2P 的 C」也能互通。
        ///
        /// 三条硬约束（参考 qaul 的 flooder：incoming_via 排除法 + 跳数封顶 + 全局 id 去重）：
        ///   1. **从哪来不回哪去** —— 排除 arrivedBy，否则两边互相弹球；
        ///   2. **跳数封顶** —— 每中继一次 hop+1，到 MaxHop 就不再往外送，环路自然收敛；
        ///   3. **整帧转发** —— id / conv / 时间原样带走（蓝牙用 [[mesh]] 信封），
        ///      接收端按 id 去重，所以同一句话在四台设备上只出现一次。
        /// 另外这里只中转频道消息：私聊是点对点的，被中继会让第三方收到别人的私聊。
        /// </summary>
        private void SyncAcrossLinks(string conv, Dictionary<string, object> msg, byte[] body, string arrivedBy)
        {
            try
            {
                if (string.IsNullOrEmpty(conv) || !Convs.IsChan(conv)) return;
                var ch = channels.ByConv(conv);
                if (ch == null) return;

                // mesh 频道绝不做跨链路中继（包括 qaul 公共节点、静态中转站、BLE 逐跳）。
                if (IsMeshConv(conv)) return;

                if (!mesh.CanRelay(msg))
                {
                    mesh.NoteDrop(arrivedBy, L10n.T("超过 ") + MeshRouter.MaxHop + L10n.T(" 跳"));
                    return;
                }

                // 陈旧中继直接丢：qaul 的未确认表 1 小时过期，DTN 也有 expires_at。
                // 网格里最怕「某台设备补发三小时前的消息」，那会让所有人被旧消息刷一遍。
                long ts = Msg.Num(msg, "time");
                if (mesh.HopOf(msg) > 0 && ts > 0)
                {
                    long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
                    if (age > StaleRelayMs)
                    {
                        mesh.NoteDrop(arrivedBy, L10n.T("中继报文已过期（") + (age / 60000) + L10n.T(" 分钟）"));
                        return;
                    }
                }

                // 防泛洪（二次保险）：同一 id 每个节点最多转发一次。
                // 与 IngestIncoming 的 seenMessages 去重互补——这里独立记一笔，
                // 即便 seen 缓存因 TTL/上限被淘汰，也不会把同一个 id 反复转出去形成环路风暴。
                var mid = Msg.Str(msg, "id");
                if (!mesh.TryMarkRelayed(mid))
                {
                    mesh.NoteDrop(arrivedBy, L10n.T("本机已转发过该 id"));
                    return;
                }

                // 防网络风暴：全局中继总量 + 单来源速率双闸。稠密网格里"跳数×链路数"的
                // 乘法放大在这里被就地收敛，宁可少转几条也不让整条网被刷爆。
                if (!mesh.AllowRelay(Msg.Str(msg, "from")))
                {
                    mesh.NoteDrop(arrivedBy, L10n.T("中继预算超限"));
                    return;
                }

                // 关键修正：这里必须用 EffectiveLinks（含局域网 TCP）。
                // 以前用 ch.LinkList()，于是「只有 qaul 的频道」永远转不到局域网，
                // 电脑端在纯中继拓扑里收不到任何消息。
                var links = EffectiveLinks(ch);
                var targets = new List<string>();
                for (int i = 0; i < links.Count; i++)
                    if (links[i] != arrivedBy) targets.Add(links[i]);
                if (targets.Count == 0) return;

                // 线上副本：hop 只写给别的设备看，本地那条消息保持原样
                var relayMsg = new Dictionary<string, object>(msg);
                relayMsg["hop"] = mesh.NextHop(msg);
                // 中转出去必须重新加密：进来的帧已被 UnsealIncoming 还原成明文，
                // 把明文发到广播性质的蓝牙上等于把「加密频道」变回明文频道。
                string wire;
                try { wire = SealOutgoing(conv, relayMsg); }
                catch { wire = Msg.Str(relayMsg, "text"); }
                var wireMsg = WireCopy(relayMsg, wire);

                // 中继也走队列（标记 relay=true，存活时间更短、受令牌桶限速），
                // 不再每条消息各起一个 Task.Run —— 环路收敛慢时那是线程池被打满的主因。
                EnqueueOutbound(conv, wire, wireMsg, body, targets, true, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mesh", "跨链路中继失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 中转站转发私聊密文帧（本机既非 conv 的 a 也非 b）。
        /// 此刻 msg["text"] 仍是 SealOutgoing 的密文（UnsealIncoming 在后面才调用），
        /// 因此原样转发即可，目标对端用自己的会话密钥解密；中转站与旁人看不到正文。
        /// 复用 mesh 的跳数封顶 / id 去重 / 速率预算，防止环路与风暴。
        /// </summary>
        private void RelayDmFrame(string conv, Dictionary<string, object> msg, byte[] body, string arrivedBy)
        {
            try
            {
                if (!mesh.CanRelay(msg)) { mesh.NoteDrop(arrivedBy, L10n.T("私聊中继超过 ") + MeshRouter.MaxHop + L10n.T(" 跳")); return; }
                var mid = Msg.Str(msg, "id");
                if (string.IsNullOrEmpty(mid) || !mesh.TryMarkRelayed(mid))
                { mesh.NoteDrop(arrivedBy, L10n.T("本机已转发过该私聊 id")); return; }
                if (!mesh.AllowRelay(Msg.Str(msg, "from")))
                { mesh.NoteDrop(arrivedBy, L10n.T("私聊中继预算超限")); return; }

                // 线上副本：hop+1，正文密文原样带走（不重新 SealOutgoing）
                var relayMsg = new Dictionary<string, object>(msg);
                relayMsg["hop"] = mesh.NextHop(msg);
                string payload = JsonConvert.SerializeObject(relayMsg);

                // 只投给静态中转站，不广播给局域网所有 peer：
                // 同一局域网内真正对端本就直连可达（SendFrameToDm），无需中转站代转；
                // 把密文广播给所有 peer 只会扩大私聊暴露面、放大网络风暴。
                // 跨网段/公网时才需要中转站接力，因此这里只走静态中转站这一条。
                int n = SendToStaticRelays(payload, body, null);
                if (n > 0)
                {
                    mesh.NoteTx(arrivedBy ?? ChanLink.Lan, body == null ? payload.Length : body.Length);
                    mesh.NoteRelayOut(arrivedBy ?? ChanLink.Lan);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mesh", "私聊中转失败: " + ex.Message);
            }
        }

        /* ===================== 中继信封（蓝牙只能传文本） ===================== */

        /// <summary>
        /// 把整帧 JSON 编成「[[mesh]] + base64」。蓝牙链路只送文本，
        /// 这样接收端能拿回完整的 id / conv / hop，而不是当成一句新消息。
        /// 超过 1500 字节（含附件的消息）不往蓝牙中继 —— BLE 带宽有限，别为一条消息卡住整条链路。
        /// </summary>
        private static string EncodeMeshEnvelope(string payload)
        {
            try
            {
                if (string.IsNullOrEmpty(payload)) return null;
                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                if (bytes.Length > 1500) return null;
                return MeshEnvPrefix + Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        private static Dictionary<string, object> DecodeMeshEnvelope(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || !text.StartsWith(MeshEnvPrefix, StringComparison.Ordinal)) return null;
                var b64 = text.Substring(MeshEnvPrefix.Length).Trim();
                if (b64.Length == 0) return null;
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                var m = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (m == null) return null;
                if (string.IsNullOrEmpty(Msg.Str(m, "id"))) return null;
                return m;
            }
            catch { return null; }
        }

        /* ===================== WLAN Direct（Wi-Fi P2P） ===================== */

        private bool LoadP2pPref()
        {
            try
            {
                return context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                              .GetBoolean("p2pEnabled", false);
            }
            catch { return false; }
        }

        private void SaveP2pPref(bool on)
        {
            try
            {
                context.GetSharedPreferences("Mchat", FileCreationMode.Private)
                       .Edit().PutBoolean("p2pEnabled", on).Apply();
            }
            catch { }
        }

        public bool P2pEnabled => p2pEnabled && p2p != null && p2p.Enabled;

        /// <summary>开启 WLAN Direct 并立即扫描。</summary>
        public bool EnableP2p()
        {
            try
            {
                if (p2p == null) return false;
                if (!p2p.Enable())
                {
                    Emit("error", new { message = L10n.T("WLAN Direct 不可用：") + p2p.LastError });
                    return false;
                }
                p2pEnabled = true;
                SaveP2pPref(true);
                p2p.Discover();
                Emit("p2p", GetP2pStatus());
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("WlanP2p", "开启失败: " + ex.Message);
                return false;
            }
        }

        public void DisableP2p()
        {
            try
            {
                p2p?.Disable();
                p2pEnabled = false;
                SaveP2pPref(false);
                p2pNodes.Clear();
                Emit("p2p", GetP2pStatus());
            }
            catch { }
        }

        // ===== 全渠道接入（无网自组网恢复）=====

        private bool LoadFullChannelPref()
        {
            // 默认开启：无 Wi-Fi 局域网时自动启用离线链路（蓝牙/WLAN Direct/Wi-Fi Aware），用户无感。
            try { return context.GetSharedPreferences("Mchat", FileCreationMode.Private).GetBoolean("fullChannel", true); }
            catch { return true; }
        }

        private void SaveFullChannelPref(bool on)
        {
            try { context.GetSharedPreferences("Mchat", FileCreationMode.Private).Edit().PutBoolean("fullChannel", on).Apply(); }
            catch { }
        }

        /// <summary>是否已开启全渠道接入（供 UI 显示状态）。</summary>
        public bool FullChannelAccess => fullChannel;

        /// <summary>
        /// 开启「全渠道接入」：拉起 WLAN Direct + 蓝牙两条离线链路。
        /// Android 13+ 需要 NEARBY_WIFI_DEVICES 才能操作 WLAN Direct，未授予时先申请，
        /// 授权结果经 Activity.OnRequestPermissionsResult → OnPermissionResult 转回本类。
        /// </summary>
        public void EnableFullChannelAccess()
        {
            try
            {
                fullChannel = true;
                SaveFullChannelPref(true);

                if ((int)Build.VERSION.SdkInt >= 33)
                {
                    var act = CurrentActivity;
                    if (act != null &&
                        act.CheckSelfPermission("android.permission.NEARBY_WIFI_DEVICES") != Android.Content.PM.Permission.Granted)
                    {
                        act.RequestPermissions(new string[] { "android.permission.NEARBY_WIFI_DEVICES" }, ReqNearbyWifi);
                        return; // 授权后由 ApplyFullChannelAccess 真正拉起链路
                    }
                }

                ApplyFullChannelAccess();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FullChannel", "开启失败: " + ex.Message);
            }
        }

        /// <summary>真正拉起离线链路（假定权限已就绪）。</summary>
        private void ApplyFullChannelAccess()
        {
            try
            {
                if (!P2pEnabled) EnableP2p();
                if (p2p != null) p2p.AutoConnect = true;   // 无感自组网：发现即自动连接
                // 蓝牙仅在「没有 Wi-Fi 局域网」时启用（用户要求），有 Wi-Fi 时不占蓝牙也不增加重复链路
                if (!scanEnabled && !BitchatEnabled) EnableBitchat();
                EnableWifiAware();
                Emit("fullchannel", new { enabled = true, p2p = P2pEnabled, bitchat = BitchatEnabled, aware = WifiAwareEnabled });
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FullChannel", "应用失败: " + ex.Message);
            }
        }

        /// <summary>Wi-Fi Aware（NAN）链路是否已启用。</summary>
        public bool WifiAwareEnabled => wifiAware != null && wifiAware.Enabled;

        /// <summary>启用 Wi-Fi Aware（NAN）：无 GO 的真对等发现/直连。设备不支持时静默降级。</summary>
        public void EnableWifiAware()
        {
            try
            {
                if (wifiAware == null)
                {
                    wifiAware = new WifiAwareChannel(context);
                    wifiAware.OnLog = m => Android.Util.Log.Info("WifiAware", m);
                    wifiAware.OnPeersChanged = () => Emit("wifiAware", GetWifiAwareStatus());
                    wifiAware.OnStateChanged = ok => Emit("wifiAware", GetWifiAwareStatus());
                }
                wifiAware.Enable();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("WifiAware", "启用失败: " + ex.Message);
            }
        }

        public void DisableWifiAware()
        {
            try { wifiAware?.Disable(); } catch { }
        }

        public object GetWifiAwareStatus()
        {
            try
            {
                return new
                {
                    enabled = WifiAwareEnabled,
                    peers = wifiAware != null ? wifiAware.LinkedCount : 0,
                    lastError = wifiAware?.LastError
                };
            }
            catch { return new { enabled = false, peers = 0 }; }
        }

        /// <summary>关闭全渠道接入：不再自动拉起离线链路（手动链路仍按各自开关生效）。</summary>
        public void DisableFullChannelAccess()
        {
            try
            {
                fullChannel = false;
                SaveFullChannelPref(false);
                Emit("fullchannel", new { enabled = false });
            }
            catch { }
        }

        /// <summary>用户本次无网 episode 内选择「暂不」，避免反复弹窗。</summary>
        public void DismissFullChannelPrompt()
        {
            try { promptDismissedForNoLan = true; } catch { }
        }

        /// <summary>
        /// 立即广播一次自身并发探测，把发现周期「手推」一帧。
        /// 用于「设备」页打开、App 回到前台、用户下拉刷新等时机 —— 不必等下一个 5 秒周期，
        /// 设备列表能立刻补齐 / 清掉离线项。
        /// </summary>
        public void RefreshDiscoveryNow()
        {
            // 中继只是额外联网渠道，本地局域网发现照常刷新。
            try { ApplyRelayModeRestrictions(); } catch { }
            try { AnnounceSelfUdp(true); } catch { }
            try { mid.BeaconNow(); } catch { }
            try { if (P2pEnabled) DiscoverP2p(); } catch { }
        }

        /// <summary>刷新 P2P 设备列表（菜单点「WLAN Direct」时调用）。</summary>
        public void DiscoverP2p()
        {            if (!P2pEnabled && !EnableP2p()) return;
            try { p2p.Discover(); p2p.RefreshGroup(); } catch { }
        }

        /// <summary>发起与某台 P2P 设备的连接。</summary>
        public void ConnectP2p(string mac)
        {
            if (!P2pEnabled && !EnableP2p()) return;
            try { p2p.Connect(mac); } catch { }
        }

        public List<WlanP2pPeer> P2pPeers()
        {
            try { return p2p == null ? new List<WlanP2pPeer>() : p2p.Peers(); }
            catch { return new List<WlanP2pPeer>(); }
        }

        public object GetP2pStatus()
        {
            try
            {
                var list = new List<object>();
                foreach (var p in P2pPeers())
                    list.Add(new
                    {
                        mac = p.Mac,
                        name = p.Name,
                        deviceName = p.DeviceName,
                        ip = p.Ip,
                        connected = p.Connected
                    });
                return new
                {
                    enabled = P2pEnabled,
                    groupFormed = p2p != null && p2p.GroupFormed,
                    localIp = WlanP2pChannel.LocalP2pAddress(),
                    lastError = p2p?.LastError,
                    devices = list,
                    deviceCount = list.Count
                };
            }
            catch { return new { enabled = false, groupFormed = false, deviceCount = 0 }; }
        }

        /// <summary>
        /// Mesh 链路统计的可读文本（各链路 收/发/中继入出/丢弃/熔断/最近错误 + 跳数上限）。
        /// 用于「Mesh 统计」弹窗，多机联调时一眼看出风暴有没有被压住、跳数有没有封顶。
        /// </summary>
        public string GetMeshStatsText()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(L10n.T("消息跳数上限：")).Append(MeshRouter.MaxHop).Append(L10n.T(" 跳\n"));
                sb.Append(L10n.T("规则：每中继一次 hop+1，到上限不再转发；超过上限的帧入站直接丢弃。\n\n"));

                var snap = mesh.Snapshot();
                if (snap == null || snap.Count == 0)
                {
                    sb.Append(L10n.T("暂无链路统计（还没有收发或中继）。"));
                    return sb.ToString();
                }
                foreach (var kv in snap)
                {
                    var s = kv.Value;
                    sb.Append("[").Append(kv.Key).Append("] ");
                    sb.Append(L10n.T("收 ")).Append(s.Rx).Append(L10n.T(" / 发 ")).Append(s.Tx);
                    sb.Append(L10n.T(" / 中继入 ")).Append(s.RelayIn).Append(L10n.T(" 出 ")).Append(s.RelayOut);
                    sb.Append(L10n.T(" / 丢弃 ")).Append(s.Drop);
                    if (s.Retrans > 0) sb.Append(L10n.T(" / 重传 ")).Append(s.Retrans);
                    if (s.Expired > 0) sb.Append(L10n.T(" / 过期 ")).Append(s.Expired);
                    int cir = mesh.CircuitSeconds(kv.Key);
                    if (cir > 0) sb.Append(L10n.T(" / 熔断 ")).Append(cir).Append("s");
                    if (s.RttMs > 0) sb.Append(" / RTT ").Append(Math.Round(s.RttMs, 1)).Append("ms");
                    if (!string.IsNullOrEmpty(s.LastError)) sb.Append(L10n.T(" / 最后:")).Append(s.LastError);
                    sb.Append("\n");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return L10n.T("统计读取失败: ") + ex.Message;
            }
        }

        /// <summary>
        /// 接入方式摘要：列出当前**已连接**的各渠道及其设备数，例如
        /// 「qaul 2 台、WLAN Direct 1 台、蓝牙 3 台、局域网 2 台」。无任何连接时返回「无」。
        /// 供聊天顶栏「在线 N · 接入方式：…」使用。
        /// </summary>
        public string GetAccessSummary()
        {
            var parts = new List<string>();
            int qaul = 0, p2p = 0, bt = 0, lan = 0;

            // P2P 以 p2pNodes 为准；这些是 WLAN Direct 对端，**不能算进局域网**。
            var p2pIds = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var kv in p2pNodes)
                {
                    var id = kv.Value?.Id;
                    if (!string.IsNullOrEmpty(id)) p2pIds.Add(id);
                    p2p++;
                }
            }
            catch { }

            try
            {
                var o = Newtonsoft.Json.Linq.JObject.FromObject(GetQaulStatus());
                bool run = o["running"] != null && (bool)o["running"];
                if (run && o["neighbours"] != null) qaul = (int)o["neighbours"];
            }
            catch { }

            // 蓝牙按 peerId 去重（随机 MAC 重连不会重复计数）。
            // 另外把能对上 Mchat 身份的对端并到同一个键：同一台设备即使同时存在
            // 「我连它 / 它连我」两条 BLE 连接，也只算一台。
            try
            {
                var map = BitchatIdMap();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in BitchatNeighborList())
                {
                    if (!n.Connected) continue;
                    var raw = string.IsNullOrEmpty(n.PeerId) ? n.Address : n.PeerId;
                    keys.Add(map.TryGetValue(raw, out var lanId) ? lanId : raw);
                }
                bt = keys.Count;
            }
            catch { }

            // 局域网：排除 P2P 对端（按 id），避免同一台设备被同时算进 WLAN Direct 和局域网。
            try
            {
                int now = SysEnv.TickCount;
                foreach (var p in peers.Values)
                {
                    if (now - p.LastSeen >= PeerTimeoutMs) continue;
                    if (p.IsP2p) continue;
                    if (!string.IsNullOrEmpty(p.Id) && p2pIds.Contains(p.Id)) continue;
                    lan++;
                }
            }
            catch { }

            if (qaul > 0) parts.Add("qaul " + qaul + L10n.T(" 台"));
            if (p2p > 0) parts.Add("WLAN Direct " + p2p + L10n.T(" 台"));
            if (bt > 0) parts.Add(L10n.T("蓝牙 ") + bt + L10n.T(" 台"));
            if (lan > 0) parts.Add(L10n.T("局域网 ") + lan + L10n.T(" 台"));
            return parts.Count == 0 ? L10n.T("无") : string.Join(L10n.T("、", ", "), parts);
        }

        /// <summary>在线设备总数：按设备 id 去重合并各渠道（同一台设备多链路只算一台）。</summary>
        /// <summary>
        /// bitchat peerID → Mchat peer id 的映射（来自 hello 里对端通告的 bitchat 指纹）。
        /// 两条渠道的设备标识是**两套命名空间**（Mchat 12 hex / bitchat 16 hex），
        /// 不映射就会把同一台设备在「局域网」和「蓝牙」各算一次，在线数虚高。
        /// </summary>
        private Dictionary<string, string> BitchatIdMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var p in peers.Values)
                    if (p != null && !string.IsNullOrEmpty(p.BitchatId) && !string.IsNullOrEmpty(p.Id))
                        map[p.BitchatId] = p.Id;
                foreach (var kv in p2pNodes)
                {
                    var p = kv.Value;
                    if (p != null && !string.IsNullOrEmpty(p.BitchatId) && !string.IsNullOrEmpty(p.Id))
                        map[p.BitchatId] = p.Id;
                }
            }
            catch { }
            return map;
        }

        public int GetOnlineCount()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var btMap = BitchatIdMap();
            try
            {
                int now = SysEnv.TickCount;
                foreach (var p in peers.Values)
                {
                    if (now - p.LastSeen >= PeerTimeoutMs) continue;
                    if (IsPainterPeer(p)) continue; // 画板有独立入口，不计入在线设备
                    if (!string.IsNullOrEmpty(p.Id)) ids.Add(p.Id);
                    else if (!string.IsNullOrEmpty(p.Address)) ids.Add(p.Address);
                }
            }
            catch { }
            try { foreach (var kv in p2pNodes) { var id = kv.Value?.Id; if (!string.IsNullOrEmpty(id)) ids.Add(id); } } catch { }
            try
            {
                foreach (var n in BitchatNeighborList())
                {
                    if (!n.Connected) continue;
                    var key = string.IsNullOrEmpty(n.PeerId) ? n.Address : n.PeerId;
                    // 能对上「某台已知 Mchat 设备」就用那台的身份，避免同一台算两次
                    ids.Add(btMap.TryGetValue(key, out var lanId) ? lanId : "bt:" + key);
                }
            }
            catch { }

            // qaul 邻居：公网 / 跨网段可达的设备也算「在线」。
            // 已经能从局域网或 WLAN Direct 认出来的设备不重复计入（按公钥短指纹排除）。
            try
            {
                if (qnode != null)
                {
                    var known = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in peers.Values)
                        if (!string.IsNullOrEmpty(p.QaulKeyShort)) known.Add(p.QaulKeyShort);
                    foreach (var kv in p2pNodes)
                        if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.QaulKeyShort)) known.Add(kv.Value.QaulKeyShort);

                    foreach (var n in qnode.Neighbours())
                    {
                        object op;
                        if (!n.TryGetValue("node", out op)) continue;
                        var node = op as string;
                        if (string.IsNullOrEmpty(node)) continue;
                        var ks = QaulKeyShortOf(node);
                        if (ks != null && known.Contains(ks)) continue;
                        ids.Add("qaul:" + node);
                    }
                }
            }
            catch { }

            return ids.Count;
        }

        /// <summary>
        /// 通知栏快捷回复：直接往该会话发一条纯文本。
        /// 返回 false 表示「还没准备好」（进程冷启动、通道未起），调用方应稍后重试；
        /// 不能让用户点了回复却静默丢掉——这是最容易让人以为「App 坏了」的体验。
        /// </summary>
        public bool SendReplyToConv(string conv, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(conv)) return false;
            try
            {
                // 与聊天界面「发送」按钮走同一个入口，保证回复进的是同一会话、同一套落库逻辑。
                // 不再用 `if (!started) return false` 硬失败：那会让冷启动时用户点了回复却什么都没有。
                var m = SendInConv(conv, text);
                if (m != null)
                {
                    Android.Util.Log.Info("Reply", "快捷回复已发送 conv=" + conv + " text=" + text);
                    return true;
                }

                // SendInConv 返回 null 只发生在目标会话的传输层尚未就绪（qaul 节点/群没连上）。
                // 即便如此也绝不能让用户点了回复聊天里没动静：先按普通消息落库到该会话并 Emit，
                // 界面立刻看得到；再走 Deliver 尽力投递（出站队列带重试，链路好了会补发）。
                var fallback = Msg.New(conv, peerId, displayName, "text", text);
                _dedupEngine.MarkSeen(Msg.Str(fallback, "id"));
                store.Add(fallback, true);
                AppendHistory(fallback);
                Emit("message", fallback);
                Deliver(fallback, null);
                Android.Util.Log.Warn("Reply", "快捷回复：会话传输未就绪，已先落库再尽力投递 conv=" + conv);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Reply", "快捷回复发送失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>P2P 链路上的身份通告（供对端互认）。</summary>
        private string P2pHelloJson()
        {
            try
            {
                var o = new JObject
                {
                    ["t"] = "hello",
                    ["id"] = peerId,
                    ["name"] = displayName,
                    ["port"] = tcpPort,
                    ["p2p"] = true,
                    ["qp"] = qaul != null ? qaul.Port : 0,
                    ["bc"] = Identity != null ? Identity.BitchatPeerId : null,
                    ["rd"] = RdHostServer.AdvertisedPort,
                    ["dc"] = RdHostServer.AdvertisedCode
                };
                return o.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return "{}"; }
        }

        /// <summary>处理来自 P2P 对端的 hello：登记进 p2pNodes（独立于局域网节点表）。</summary>
        private void OnP2pHello(string fromIp, string id, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return;
                var p = new Peer
                {
                    Id = id,
                    Name = name,
                    Address = fromIp,
                    Port = tcpPort,
                    LastSeen = SysEnv.TickCount,
                    IsP2p = true
                };
                p2pNodes[fromIp] = p;
                p2p?.ApplyHello(fromIp, id, name);
                // WLAN Direct 是本机另一张网卡：TCP 连上且收到对方 hello 即双向互通，
                // 直接确认，使其与局域网设备一样可以参与 mesh 频道。
                LanPeerGate.ConfirmDirect(id);
                Emit("p2p", GetP2pStatus());
            }
            catch { }
        }

        /// <summary>以本机身份在指定会话里发一条消息（原生界面主入口）。</summary>
        public Dictionary<string, object> SendInConv(string conv, string text, string kind = "text", string url = null)
        {
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrEmpty(url)) return null;
            conv = string.IsNullOrEmpty(conv) ? activeConv : conv;

            // 中继只是额外联网渠道，局域网私聊 / 未加入频道聊天照常进行。
            // mesh 频道仍由 EffectiveLinks 与 SyncAcrossLinks 保证不经中继。

            // qaul 公开信息：走 floodsub 广播，而不是本项目自己的两条渠道
            if (conv == QaulFeedConv)
            {
                PublishQaulFeed(text);
                return Msg.New(conv, peerId, displayName, kind, text);
            }

            // 统一频道：按该频道启用的链路并行投递（qaul 为主链路）
            if (Convs.IsChan(conv))
            {
                EnsureChannelLinksActive(conv);
                return SendInChannel(conv, text, kind);
            }

            // 与 qaul 用户的私聊：走端到端加密（KK 会话）
            if (Convs.IsQaulDm(conv))
            {
                var peer = Convs.QaulPeerOf(conv);
                if (qnode == null || qnode.Messaging == null) return null;

                // LC1：qaul 的 Noise 只保护到 qaul 端点，中继节点与 qaul 自己的本地库
                // 都能看到明文；因此这里再包一层本程序独有的加密。
                // 先装载对称派生密钥，否则两端会话键不同会永远「未设置密码」。
                EnsureQaulDmKey(peer);
                var wire = SealOutgoing(conv, Msg.New(conv, peerId, displayName, kind, text));

                byte[] dmMid;
                var sent = qnode.Messaging.SendDm(peer, wire, out dmMid);
                if (!sent)
                {
                    // 三种「没发出去」：公钥还没到 / KK 半握手排队 / 既非邻居也无路由。
                    // 前两种只是排队，稍后自动补发——显示 pending 而不是失败，
                    // 否则用户以为消息丢了（老实现就这样，其实 crypto 队列会补达）。
                    var queued = qnode.Messaging.PendingDmCountOf(peer) > 0
                              || qnode.Messaging.SessionPendingOf(peer) > 0;
                    if (queued)
                    {
                        try { qnode.RequestUser(peer); } catch { }
                        var pm = AlreadyStored(conv, text, null);
                        pm["pending"] = true;
                        return pm;
                    }
                    return null;
                }
                return AlreadyStored(conv, text, dmMid);
            }

            // qaul 群聊：逐个成员单播
            if (Convs.IsQaulGroup(conv))
            {
                var gid = Convs.QaulGroupIdOf(conv);
                if (qnode == null || qnode.Messaging == null) return null;
                var wire = SealOutgoing(conv, Msg.New(conv, peerId, displayName, kind, text));
                byte[] groupMid;
                if (!qnode.Messaging.SendGroupText(gid, wire, out groupMid)) return null;
                // 群聊 id 统一：发送方本机用 SendGroupText 回传的真实 mid 落库，
                // 与所有接收成员看到的 id 完全一致（群发本质是同 group/sender/index，
                // 所有成员共享一个 id；此前发送方用 Msg.New 的 GUID，才出现两端不一致）。
                return AlreadyStored(conv, text, groupMid);
            }

            // bitchat 会话：走 BLE mesh（公屏/频道/私聊/群组），并以本程序为传输节点做隧道
            if (Convs.IsBitchat(conv))
            {
                return SendBitchatInConv(conv, text, kind);
            }

            var msg = Msg.New(conv, peerId, displayName, kind, text);
            if (!string.IsNullOrEmpty(url))
            {
                msg["url"] = url;
                if (kind == "text") msg["kind"] = "url";
            }

            _dedupEngine.MarkSeen(Msg.Str(msg, "id"));
            store.Add(msg, true);
            AppendHistory(msg);
            Emit("message", msg);
            Deliver(msg, null);

            return msg;
        }

        /// <summary>qaul 发送成功后，把本机这条消息写进会话（发送方自己写，不重复入库）。</summary>
        /// <param name="messageId">qaul 真实 message id（CRC64(group)‖CRC64(sender)‖index）。
        /// 必须用它落库，否则发送端与接收端 id 不一致：对端按 id 去重救不了发送端自己
        /// （中继绕回来 → 重影），MarkSeen 标的也是错的 id。</param>
        private Dictionary<string, object> AlreadyStored(string conv, string text, byte[] messageId = null)
        {
            var m = Msg.New(conv, peerId, displayName, "text", text);
            if (messageId != null && messageId.Length > 0) m["id"] = LocalIdentity.Hex(messageId);
            m["src"] = Convs.IsQaulGroup(conv) ? "qaul-group-out" : "qaul-e2e-out";
            _dedupEngine.MarkSeen(Msg.Str(m, "id"));
            store.Add(m, true);
            AppendHistory(m);
            Emit("message", m);
            return m;
        }

        /// <summary>加入（或创建）一个房间：房间号由用户随手输入，无需握手。</summary>
        public string JoinRoom(string code)
        {
            var conv = Convs.Room(code);
            store.SetTitle(conv, Convs.RoomCode(conv));
            SetActiveConv(conv);
            Emit("conv", new { conv, title = Convs.TitleOf(conv) });
            return conv;
        }

        /// <summary>进入与某个在线节点的私聊会话。</summary>
        public string StartDm(string otherPeerId, string otherName = null)
        {
            if (string.IsNullOrEmpty(otherPeerId) || otherPeerId == peerId) return null;
            var conv = Convs.Dm(peerId, otherPeerId);
            Peer p;
            var name = otherName;
            if (string.IsNullOrEmpty(name) && peers.TryGetValue(otherPeerId, out p)) name = p.Name;
            // 按 ID 私聊（对方不在线 / 不在 peers 里）时，Convs.TitleOf 只会取排序后的第二段，
            // 本机 ID 排在后面就会把标题显示成自己。这里显式用对方 ID 当标题。
            if (string.IsNullOrEmpty(name)) name = otherPeerId;
            store.SetTitle(conv, name);
            SetActiveConv(conv);
            Emit("conv", new { conv, title = name ?? Convs.TitleOf(conv) });
            return conv;
        }

        /// <summary>逐条删除：本地墓碑 + 广播给对端同步删除。</summary>
        public bool DeleteMessage(string id, bool broadcast = true)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var target = FindMessage(id);
            var conv = target != null ? Msg.Conv(target) : activeConv;
            if (!store.Delete(id)) return false;

            var evt = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["conv"] = conv,
                ["kind"] = Msg.ControlKind,
                ["op"] = MsgOp.Recall,
                ["target"] = id,
                ["text"] = "",
                ["from"] = peerId,
                ["fromName"] = displayName,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            _dedupEngine.MarkSeen(Msg.Str(evt, "id"));
            AppendHistory(evt);
            Emit("msgdel", evt);
            if (broadcast) BroadcastFrame(evt, conv);
            return true;
        }

        /// <summary>编辑自己发出的一条文本消息：本机改字并广播 edit 帧，对端同步改字。</summary>
        /// <param name="viaWeb">true 表示来自网页访客：允许编辑经本宿主代发的消息（viaHost==peerId）。</param>
        public bool EditMessage(string conv, string id, string newText, bool viaWeb = false)
        {
            if (string.IsNullOrEmpty(id) || newText == null) return false;
            var target = FindMessage(id);
            if (target == null) return false;
            // 仅允许编辑本机（或经本机代发）的消息
            var fromId = Msg.Str(target, "from");
            bool mine = fromId == peerId || (viaWeb && Msg.Str(target, "viaHost") == peerId);
            if (!mine) return false;
            var c = Msg.Conv(target);
            if (!store.UpdateText(id, newText)) return false;

            var evt = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["conv"] = c,
                ["kind"] = Msg.ControlKind,
                ["op"] = MsgOp.Edit,
                ["target"] = id,
                ["text"] = newText,
                ["from"] = peerId,
                ["fromName"] = displayName,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            BroadcastFrame(evt, c);
            Emit("msgedit", evt);
            return true;
        }

        [Java.Interop.Export("editMessage")]
        [JavascriptInterface]
        public bool EditMessageJs(string conv, string id, string newText)
        {
            return EditMessage(conv, id, newText ?? "");
        }

        /// <summary>
        /// 把控制帧（撤回/编辑/删除/清空）广播到会话所属渠道的全部链路
        /// （qaul / 局域网 / WLAN Direct / 蓝牙），确保只连蓝牙或 WLAN Direct
        /// 的设备也能收到 —— 不能像 Deliver 那样只发 qaul + 局域网。
        /// </summary>
        private void BroadcastFrame(Dictionary<string, object> msg, string conv)
        {
            try
            {
                var ch = channels.ByConv(conv);
                var links = EffectiveLinks(ch);
                var wire = SealOutgoing(conv, msg);
                var wireMsg = WireCopy(msg, wire);
                EnqueueOutbound(conv, wire, wireMsg, null, links, false, null);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "广播控制帧失败: " + ex.Message);
            }
        }

        /// <summary>清空某个会话的全部消息（默认当前会话）。</summary>
        public int ClearConversation(string conv = null, bool broadcast = true)
        {
            conv = string.IsNullOrEmpty(conv) ? activeConv : conv;
            var n = store.Clear(conv);

            var evt = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["conv"] = conv,
                ["kind"] = Msg.ControlKind,
                ["op"] = MsgOp.Clear,
                ["text"] = "",
                ["from"] = peerId,
                ["fromName"] = displayName,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            _dedupEngine.MarkSeen(Msg.Str(evt, "id"));
            AppendHistory(evt);
            Emit("convclear", evt);
            if (broadcast) Deliver(evt, null);
            return n;
        }

        /// <summary>清空全部会话（本机全量，默认也通知对端）。</summary>
        public int ClearAllMessages(bool broadcast = true)
        {
            var convs = store.Conversations();
            var total = 0;
            foreach (var c in convs)
            {
                total += ClearConversation(c.Id, broadcast);
            }
            if (convs.Count == 0) total = store.ClearAll();
            try { store.Compact(); } catch { }
            return total;
        }

        /// <summary>
        /// 彻底移除一个会话（顶栏会消失）：消息墓碑化 + 删除标题与已读进度。
        /// 与 ClearConversation 的区别：Clear 只清消息、保留标题，load 时又会按标题把会话长回来。
        /// </summary>
        public int RemoveConversation(string conv = null)
        {
            conv = string.IsNullOrEmpty(conv) ? activeConv : conv;
            var n = store.RemoveConversation(conv);
            Emit("convremove", new { conv });
            return n;
        }

        /// <summary>
        /// 经 qaul 渠道给某会话发一个本地文件（图片/视频/任意文件）。
        /// 会话可以是 qaul 私聊（qaul:&lt;peer&gt;）或 qaul 群（qgroup:&lt;id&gt;）。
        /// </summary>
        public bool SendQaulFile(string conv, string filePath, string description = "")
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
                var bytes = File.ReadAllBytes(filePath);
                return SendQaulFileBytes(conv, Path.GetFileName(filePath), bytes, description);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("QaulMsg", "发送 qaul 文件失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 全 App 唯一的文件发送出口：经 qaul 把一段字节发给某 qaul 会话（私聊或群）。
        /// 先在本机落盘并插入一条「我发出的」消息（发送方立即看到），再走 qaul 端到端分片传输。
        /// 返回插入的消息对象；失败（qaul 未就绪 / 非 qaul 会话 / 空文件）返回 null。
        /// 原生 ＋ 菜单、原生长按菜单、网页端 sendMessage 都汇聚到这里，保证只有一套文件传输。
        /// mlib 非空时消息带「媒体快捷卡片」标记（阅读/音乐/图片/视频），对端点击直接进对应媒体库。
        /// </summary>
        private Dictionary<string, object> SendQaulFileCore(string conv, string fileName, byte[] data,
                                                            string description, string mlib = null)
        {
            // 注意：这里**不能**再要求 qaulMsg 已就绪。以前 qaul 没起来就在这里直接 return null，
            // UI 立刻弹"发送失败"——可两台设备明明在同一个 Wi-Fi 里，局域网直投完全能送到。
            // qaul 只是"不在同一个局域网时"的补充通道，有就用、没有就不影响发送。
            if (data == null || data.Length == 0) { ToastSafe("文件为空"); return null; }
            if (string.IsNullOrEmpty(conv) || conv == Convs.WebConv) { ToastSafe("请切换到 qaul 会话再发送文件"); return null; }
            // 频道：conv → 确定性 group_id，"频道里发文件"就等于"往这个 qaul 群里发文件"。
            bool isChan = Convs.IsChan(conv);
            string peer = isChan ? null : Convs.QaulPeerOf(conv);
            string gid = isChan ? Convs.GroupIdHex(conv) : Convs.QaulGroupIdOf(conv);
            if (string.IsNullOrEmpty(peer) && string.IsNullOrEmpty(gid)) { ToastSafe(L10n.T("该会话不是 qaul 会话，无法发送文件")); return null; }
            if (isChan && (gid == null || gid.Length != 32)) { ToastSafe("频道 id 无效，无法发送文件"); return null; }

            // 本机落盘，发送方立即看到这张图/这个文件
            if (string.IsNullOrEmpty(fileName)) fileName = L10n.T("文件");
            var mediaId = LocalIdentity.Hex(LocalIdentity.Sha256(data)) + "-" + data.Length;
            var safe = SafeMediaName(fileName, mediaId);
            try { File.WriteAllBytes(Path.Combine(mediaDir, safe), data); }
            catch (Exception ex) { Android.Util.Log.Warn("QaulMsg", "保存待发文件失败: " + ex.Message); }

            var kind = MediaKind(fileName);
            // 正文只写「[文件] 3.00 MB」，文件名放 name 字段（下载时仍保存成原名）
            var m = Msg.New(conv, peerId, displayName, kind, Msg.FileLabel(kind, data.Length));
            m["id"] = mediaId;
            m["media"] = safe;
            m["mediaUrl"] = "/__media/" + safe;
            m["size"] = data.Length;
            m["name"] = fileName;
            // 媒体快捷卡片标记：只有「分享到频道」显式传入（阅读/音乐/图片/视频），
            // 普通聊天附件不带，避免图片/视频失去原来的直接预览体验
            if (!string.IsNullOrEmpty(mlib)) m["mlib"] = mlib;
            m["src"] = "qaul-file-out";
            m["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _dedupEngine.MarkSeen(mediaId);
            store.Add(m, true);
            AppendHistory(m);
            Emit("message", m);

            // 【先走局域网直投，再补 qaul】
            // 以前这里把"发送成功"完全押在 qaul 上：对端还没换到公钥、qaul 群还没建好，
            // 就会弹"发送失败（对方可能还没上线）"—— 可其实两台设备就在同一个 Wi-Fi 里，
            // 局域网直投明明能送到。所以现在两条路都试，任一条成功就算发出去。
            int lanOk = 0;
            try { lanOk = BroadcastFrame(JsonConvert.SerializeObject(m), data, conv); } catch { }

            bool qaulOk = false;
            try
            {
                if (qaulMsg != null)
                {
                    if (!string.IsNullOrEmpty(peer))
                    {
                        if (qaulMsg.HaveKey(peer))
                            qaulOk = qaulMsg.SendFile(peer, fileName, data, description);
                    }
                    else if (!string.IsNullOrEmpty(gid))
                    {
                        qaulOk = qaulMsg.SendFileToGroup(gid, fileName, data, description);
                    }
                }
            }
            catch (Exception ex) { Android.Util.Log.Warn("QaulMsg", "qaul 发送异常: " + ex.Message); }

            bool delivered = lanOk > 0 || qaulOk;
            m["delivered"] = delivered;
            if (delivered) ToastSafe(L10n.T("已发送：") + fileName + (lanOk > 0 ? L10n.T("（局域网直投）") : "（qaul）"));
            else ToastSafe(L10n.T("发送失败：附近没有在线设备，qaul 也未连通"));
            return m;
        }

        /// <summary>经 qaul 渠道发一段内存里的字节作为附件（供 UI 从文档选择器直接发）。</summary>
        public bool SendQaulFileBytes(string conv, string fileName, byte[] data, string description = "")
        {
            try
            {
                var m = SendQaulFileCore(conv, fileName, data, description);
                if (m == null) return false;
                object d;
                return m.TryGetValue("delivered", out d) && d is bool && (bool)d;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("QaulMsg", "发送 qaul 文件失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 「分享到频道」专用：发送媒体文件并打快捷卡片标记（book/music/photo/video），
        /// 对端点卡片即落盘 /sdcard/file/&lt;类型&gt;/ 并加入对应媒体库。
        /// </summary>
        public bool SendMediaShareBytes(string conv, string fileName, byte[] data, string mlib)
        {
            try
            {
                var m = SendQaulFileCore(conv, fileName, data, "", mlib);
                if (m == null) return false;
                object d;
                return m.TryGetValue("delivered", out d) && d is bool && (bool)d;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("QaulMsg", "分享媒体失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 原生界面发送文件/图片/视频的统一入口。
        /// 整个 App 只保留一套文件传输——qaul 的端到端分片传输：
        ///   · qaul 私聊 / qaul 群组 → 走 qaul 文件传输；
        ///   · 房间 / 普通私聊（非 qaul）→ 不发文件，提示「仅 qaul 会话支持」，
        ///     因为这些会话只保留纯文本与指令渠道（与需求一致：不再维护第二套文件传输）。
        /// </summary>
        public bool SendFileBytes(string conv, string fileName, byte[] data, string kind = null)
        {
            if (string.IsNullOrEmpty(conv) || conv == Convs.WebConv) { ToastSafe("请切换到具体会话再发送文件"); return false; }
            if (data == null || data.Length == 0) { ToastSafe("文件为空"); return false; }
            // 文件传输统一走 qaul：私聊 / qaul 群 / 普通频道（chan:）都映射到确定的 group_id，
            // 频道本身不带传输方式，所以这里和 qaul 群是同一条路径。
            if (Convs.IsQaulDm(conv) || Convs.IsQaulGroup(conv) || Convs.IsChan(conv))
                return SendQaulFileBytes(conv, fileName, data, "");
            ToastSafe(L10n.T("文件传输仅支持 qaul 会话（qaul 私聊 / qaul 群 / 频道），房间与普通私聊请使用文本"));
            return false;
        }

        private Dictionary<string, object> FindMessage(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var list = store.Messages(activeConv, 0);
            foreach (var m in list) if (Msg.Str(m, "id") == id) return m;
            // 不在当前会话时按全部最近消息兜底
            foreach (var m in store.Recent(500)) if (Msg.Str(m, "id") == id) return m;
            return null;
        }

        /// <summary>把通话信令交给原生通话模块，同时透传给前端做界面提示。</summary>
        private void HandleCallSignal(Dictionary<string, object> msg, string fromAddress)
        {
            var sigType = Msg.Str(msg, "type");
            var sigFrom = Msg.Str(msg, "from");
            var sigFromName = Msg.Str(msg, "fromName", sigFrom);
            var sigTo = Msg.Str(msg, "to");
            var sigMode = Msg.Str(msg, "mode");
            if (voiceCall.HandleSignal(sigType, sigFrom, sigFromName, sigTo, fromAddress, sigMode))
                Emit("callsignal", msg);
        }

        #endregion

        #region TCP 服务端

        private void StartTcpServer()
        {
            // 候选端口：优先用户指定的监听端口（中转站场景常只需放行一个固定端口），
            // 其次默认端口段。
            var ports = new List<int>();
            int prefPort = PreferredTcpPort;
            if (prefPort >= 1024 && prefPort <= 65535) ports.Add(prefPort);
            for (int p = DefaultTcpPort; p < DefaultTcpPort + 20; p++)
                if (!ports.Contains(p)) ports.Add(p);

            foreach (var p in ports)
            {
                var listener = TryBindPort(p);
                if (listener != null)
                {
                    tcpListener = listener;
                    tcpPort = p;
                    break;
                }
                tcpListener = null;
            }

            if (tcpListener == null)
            {
                Emit("error", new { message = L10n.T("无法监听任何端口") });
                return;
            }

            Android.Util.Log.Info("Mchat", $"TCP 监听 {tcpPort}");
            var token = cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
                        _ = Task.Run(() => HandleClient(client), token);
                    }
                    catch (Exception)
                    {
                        if (token.IsCancellationRequested) break;
                        await Task.Delay(300).ConfigureAwait(false);
                    }
                }
            }, token);
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 30000;

                    // 上限 8MB：电脑端会给小文件附一份 base64 兜底在消息头里，
                    // 1MB 的旧上限会把这类帧**整条丢掉**（连二进制体一起丢），
                    // 表现就是"电脑发图片/文件过来一点反应都没有"。两侧必须一致放宽。
                    var headerLen = ReadInt32(stream);
                    if (headerLen <= 0 || headerLen > 8 * 1024 * 1024) return;
                    var headerBytes = ReadExact(stream, headerLen);
                    var headerJson = Encoding.UTF8.GetString(headerBytes);

                    var bodyLen = ReadInt32(stream);
                    byte[] body = null;
                    if (bodyLen > 0)
                    {
                        if (bodyLen > MaxFrameBytes) return;
                        body = ReadExact(stream, bodyLen);
                    }

                    var fromAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "";
                    OnFrameReceived(headerJson, body, fromAddress);

                    // 回执：告诉发送端"收全并落地了"。电脑端据此给出真实的成败提示。
                    try { stream.Write(new byte[] { (byte)'K' }, 0, 1); stream.Flush(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"接收异常: {ex.Message}");
            }
        }

        private void OnFrameReceived(string headerJson, byte[] body, string fromAddress)
        {
            try
            {
                var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(headerJson);
                if (msg == null) return;

                // WLAN Direct 的身份通告：TCP 服务端监听在 0.0.0.0，P2P 网段的连接也会打到这里。
                // 对端组网后主动 hello 一次，我们据此把它登记进 P2P 节点表。
                var t = Msg.Str(msg, "t");
                if (t == "hello" || t == "probe")
                {
                    var hid = msg.TryGetValue("id", out var hv) ? hv?.ToString() : null;
                    if (!string.IsNullOrEmpty(hid) && !string.IsNullOrEmpty(fromAddress))
                    {
                        var hname = msg.TryGetValue("name", out var hnv) ? hnv?.ToString() : null;
                        // 来源是 P2P 网段，或 hello 里标了 p2p，都归到 WLAN Direct（p2pNodes），
                        // 绝不写进局域网 peers 表——否则 P2P 设备会被算成「局域网」。
                        bool fromP2p = IsP2pAddress(fromAddress)
                                       || (msg.TryGetValue("p2p", out var pv) && pv is bool pb && pb);
                        if (fromP2p) OnP2pHello(fromAddress, hid, hname);
                        else
                        {
                            // 同一条 TCP 通道上也可能是局域网对端，顺手刷新它的在线时间。
                            //
                            // 【只能"更新"，不能整条替换】UDP hello 里带着 rd/dc（是否可远控、
                            // 设备码），而这条 TCP hello 只有一个 id/name。以前这里直接 new Peer
                            // 覆盖整个条目，于是设备刚从 UDP 那边被标成"可远控"，立刻又被冲成
                            // "未通告端口"——界面表现为远控按钮时灰时亮，点进去还以为是对方没开。
                            // 对端在 hello 里通告了自己的监听端口（"port"）；中转站常用自定义端口，
                            // 必须按它给的端口回连，不能想当然用本机端口。
                            int hport = 0;
                            try
                            {
                                if (msg.TryGetValue("port", out var hpv) && hpv != null)
                                    int.TryParse(hpv.ToString(), out hport);
                            }
                            catch { }

                            // 网页远控（ws）：TCP hello 也带上时一并记录，和 UDP 发现保持一致。
                            // 约定 ws 为端口数字（用源 IP 拼 URL），也兼容完整 URL。
                            string hws = "";
                            try
                            {
                                if (msg.TryGetValue("ws", out var hwv) && hwv != null)
                                {
                                    hws = hwv.ToString().Trim();
                                    if (!string.IsNullOrEmpty(hws)
                                        && !hws.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                        && !hws.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                        && int.TryParse(hws, out var wp) && wp > 0 && wp <= 65535)
                                        hws = "http://" + fromAddress + ":" + wp;
                                }
                            }
                            catch { hws = ""; }

                            // 中继那边的设备不纳入本地发现：来自中继端点的 hello 标记为 ViaRelay，
                            // 三个设备列表出口会统一过滤，避免远端设备混进「附近设备」。
                            // 本地局域网对端仍按原样登记，连中继不影响局域网发现与聊天。
                            bool viaRelay = IsRelayEndpointAddress(fromAddress);
                            Peer exist;
                            if (hid != null && peers.TryGetValue(hid, out exist) && exist != null)
                            {
                                if (!string.IsNullOrEmpty(hname)) exist.Name = hname;
                                if (!string.IsNullOrEmpty(fromAddress)) exist.Address = fromAddress;
                                if (hport > 0) exist.Port = hport;
                                if (!string.IsNullOrEmpty(hws)) exist.WebRemote = hws;
                                exist.ViaRelay = viaRelay;
                                exist.LastSeen = SysEnv.TickCount;
                            }
                            else if (hid != null)
                            {
                                peers[hid] = new Peer
                                {
                                    Id = hid, Name = hname, Address = fromAddress,
                                    Port = hport > 0 ? hport : DefaultTcpPort,
                                    WebRemote = hws,
                                    ViaRelay = viaRelay,
                                    LastSeen = SysEnv.TickCount
                                };
                            }
                        }
                    }
                    return;
                }

                var id = msg.TryGetValue("id", out var idv) ? idv?.ToString() : null;
                if (string.IsNullOrEmpty(id)) return;

                // 两条渠道共用同一个入站入口：通话信令 / 位置 / 媒体同步 / 控制帧 /
                // 普通消息的分发与去重都在 IngestIncoming 内统一完成。
                // WLAN Direct 与局域网共用同一个 TCP 入站口，这里按对端地址区分来源，
                // 否则 P2P 来的消息会被误标成「局域网」（界面/统计都跟着错）。
                string srcLink = "lan";
                try { if (IsP2pAddress(fromAddress)) srcLink = ChanLink.P2p; } catch { }
                IngestIncoming(msg, body, fromAddress, srcLink);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", $"解析帧失败: {ex.Message}");
            }
        }

        #endregion

        #region 发送

        /// <summary>
        /// 把一个节点"没见过的"本次运行期消息补发过去（消息 + 附件）。
        /// 去重交给对端：他按消息 id 丢重复，所以这里不需要记谁收过什么。
        /// </summary>
        private void SyncSessionTo(Peer peer)
        {
            try
            {
                // 新设备刚上线：主动推一张「当前完整音乐分享卡片」给它，这样后启动的设备
                // 不用等本机下一次切歌就能立刻看到分享，并由用户自己点卡片上的「一起听」按钮加入。
                // 【行为变更】现在不再自动加入一起听——音乐卡片只展示，加入与否完全由用户手动决定。
                // 用「当前时间」和「新的消息 id」是为了让这张卡作为新消息显示（不是历史滚动位置），
                // 与自动加入无关；电视端也仍可自行加载歌单播放。
                try
                {
                    Dictionary<string, object> freshest = null;
                    foreach (var m in store.Recent(200))
                    {
                        if (Msg.Str(m, "kind") != "musiccard") continue;
                        // 优先带 playlist 的卡片；「一起听」关闭时 tvPlaylist 也算
                        if (!string.IsNullOrEmpty(Msg.Str(m, "playlist"))
                            || !string.IsNullOrEmpty(Msg.Str(m, "tvPlaylist")))
                        { freshest = m; break; }
                        if (freshest == null) freshest = m;
                    }
                    if (freshest != null)
                    {
                        // store 里的卡片是 patch 合并后的完整版（首发时带封面/歌词/歌单，
                        // patch 时 1847-1865 行把旧值合并回 body 再 Replace），直接透传即可。
                        var card = new Dictionary<string, object>(freshest)
                        {
                            ["id"] = Guid.NewGuid().ToString("N"),
                            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        SendFrameTo(peer, JsonConvert.SerializeObject(card), null);
                        Android.Util.Log.Info("Mchat", "已向新设备 " + peer.Name + " 推送当前音乐卡片");
                    }
                }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "推音乐卡片给新设备失败: " + ex.Message); }

                var recent = store.Recent(200);
                int sent = 0;
                foreach (var m in recent)
                {
                    // 音乐卡片已在上面以「当前时间 + 新 id」单独推送过（作为新消息展示）。
                    // 这里若再原样补发一次，对端会出现两张重复的卡，因此跳过。
                    if (Msg.Str(m, "kind") == "musiccard") continue;
                    string conv = Msg.Str(m, "conv");
                    if (string.IsNullOrEmpty(conv) || !conv.StartsWith("chan:")) continue;
                    byte[] body = null;
                    var media = Msg.Str(m, "media");
                    if (!string.IsNullOrEmpty(media))
                    {
                        try { body = File.ReadAllBytes(Path.Combine(mediaDir, media)); }
                        catch { body = null; }
                    }
                    try { SendFrameTo(peer, JsonConvert.SerializeObject(m), body); sent++; }
                    catch { }
                }
                if (sent > 0)
                    Android.Util.Log.Info("Mchat", $"已向新设备 {peer.Name} 补发缓存 {sent} 条");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "补发缓存失败: " + ex.Message);
            }
        }

        private int BroadcastFrame(string headerJson, byte[] body, string conv = null,
                                   bool skipRelay = false, Func<Peer, bool> peerFilter = null)
        {
            var now = SysEnv.TickCount;
            var targets = peers.Values
                .Where(p => now - p.LastSeen < PeerTimeoutMs && !p.ViaRelay && (peerFilter == null || peerFilter(p)))
                .ToList();
            int okCount = 0;
            foreach (var peer in targets)
            {
                try { SendFrameTo(peer, headerJson, body); okCount++; }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", $"投递到 {peer.Name}({peer.Address}) 失败: {ex.Message}");
                }
            }

            // 静态中转站：跨公网/跨网段时把同一帧也交给它，由它再中继给其它设备。
            // mesh 频道必须留在局域网内：这里按会话再判一次，任何调用方都别想
            // 把 mesh 帧交给中继站 —— 中继只是通信渠道，不承载 mesh 频道流转。
            bool meshLocal = IsMeshConv(conv);
            if (!skipRelay && !meshLocal) okCount += SendToStaticRelays(headerJson, body, targets);
            return okCount;
        }

        /// <summary>
        /// 把一帧投递给所有静态中转站（IPv4/IPv6/域名）。返回成功投递数。
        /// 所有聊天渠道（含私聊）都走这条：连了中继器的消息必须经它中转。
        /// 私聊帧经中继站广播后，非会话对端设备在 IngestIncoming:4208 按 from 校验丢弃，
        /// 且正文经 SealOutgoing 加密，第三方收不到也解不开，不会泄漏私聊内容。
        /// </summary>
        private int SendToStaticRelays(string headerJson, byte[] body, List<Peer> excludePeers)
        {
            int okCount = 0;
            List<RelayEndpoint> relays;
            lock (staticRelays) relays = new List<RelayEndpoint>(staticRelays);
            foreach (var r in relays)
            {
                bool dup = false;
                if (excludePeers != null)
                    foreach (var peer in excludePeers)
                    {
                        if (peer.Port == r.Port &&
                            string.Equals(NormalizeHost(peer.Address), r.Host, StringComparison.OrdinalIgnoreCase))
                        { dup = true; break; }
                    }
                if (dup) continue;
                try
                {
                    SendFrameTo(new Peer { Address = r.Host, Port = r.Port }, headerJson, body);
                    okCount++;
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "投递中转站 " + FormatHostPort(r.Host, r.Port) + " 失败: " + ex.Message);
                }
            }
            return okCount;
        }

        private void SendFrameTo(Peer peer, string headerJson, byte[] body)
        {
            using (var client = ConnectTcp(peer.Address, peer.Port, 4000))
            {

                client.SendTimeout = 60000;
                using (var stream = client.GetStream())
                {
                    var headerBytes = Encoding.UTF8.GetBytes(headerJson);
                    WriteInt32(stream, headerBytes.Length);
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    WriteInt32(stream, body?.Length ?? 0);
                    if (body != null && body.Length > 0) stream.Write(body, 0, body.Length);
                    stream.Flush();

                    // 等对端一个字节的回执：读到了才算真的送到。老版本对端不会回，
                    // 读不到也不判失败（保持向后兼容）。
                    try
                    {
                        client.ReceiveTimeout = 3000;
                        int b = stream.ReadByte();
                        if (b == (byte)'K') { }
                    }
                    catch { }
                }
            }
        }

        #endregion

        #region 网络状态感知

        /// <summary>
        /// 注册网络状态监听。优先使用 API 21+ 的 ConnectivityManager.NetworkCallback
        /// （能精确感知 WiFi/热点/移动数据切换）；低版本回退到 CONNECTIVITY_ACTION 广播。
        /// 任何网络变化都会触发 EvaluateNetwork 重新评估是否扫描。
        /// </summary>
        private void RegisterNetworkMonitor()
        {
            try
            {
                connMgr = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService);
                if (connMgr == null) return;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                {
                    netCallback = new LanNetworkCallback(this);
                    var request = new Android.Net.NetworkRequest.Builder()
                        .AddTransportType(Android.Net.TransportType.Wifi)
                        .AddTransportType(Android.Net.TransportType.Cellular)
                        .AddTransportType(Android.Net.TransportType.Ethernet)
                        .Build();
                    try { connMgr.RegisterNetworkCallback(request, netCallback); }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Mchat", $"注册 NetworkCallback 失败，回退广播: {ex.Message}");
                        RegisterLegacyReceiver();
                    }
                }
                else
                {
                    RegisterLegacyReceiver();
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"注册网络监听失败: {ex.Message}");
            }
        }

        private void RegisterLegacyReceiver()
        {
            try
            {
                var filter = new IntentFilter(ConnectivityManager.ConnectivityAction);
                context.RegisterReceiver(new LanConnectivityReceiver(this), filter);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"注册广播失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 评估当前网络并决定扫描策略。
        /// force：首次调用或希望无条件重建发现时传 true。
        /// </summary>
        private void EvaluateNetwork(bool force)
        {
            lock (netLock)
            {
                NetType prev = currentNetType;
                NetType net = DetectNetworkType();

                if (!force && net == prev)
                {
                    // 类型未变，但 IP 可能变了（同一 WiFi 重新获取地址），仍重建一次以稳妥
                }

                currentNetType = net;
                bool prevScan = scanEnabled;

                // 是否可扫描 / 绑哪张网卡：**不看默认网络类型**，只看有没有一张在用的私网网卡。
                // 手机开着热点时默认路由在移动数据网卡上，旧逻辑会判成 Mobile 而把局域网
                // 发现整个关掉，热点下面的客户端一个都扫不到。
                IPAddress lanIp;
                bool onDefaultRoute;
                bool hasLan = TryFindLanInterface(out lanIp, out onDefaultRoute);

                if (hasLan && (net == NetType.Mobile || net == NetType.None))
                    net = onDefaultRoute ? NetType.Wifi : NetType.Hotspot;

                currentNetType = net;
                scanEnabled = hasLan;
                discoveryBindIp = hasLan ? lanIp : IPAddress.Any;

                Android.Util.Log.Info("Mchat",
                    $"网络评估: {net} scan={scanEnabled} bind={discoveryBindIp}" +
                    (hasLan ? $" 子网广播={SubnetBroadcastOf(lanIp)}" : ""));

                // ===== 全渠道接入（无网自组网，用户无感）=====
                // 没有任何可用私网网卡（即无 Wi-Fi 局域网）时，自动拉起离线链路恢复通信：
                //   · 蓝牙 BLE（bitchat）——低功耗、可长期待机发现；
                //   · WLAN Direct（Wi-Fi P2P）——持久组 + 动态选主 + 自动连接；
                //   · Wi-Fi Aware（NAN）——无 GO 的真对等发现/直连（设备支持时）。
                // 全程无需用户操作（默认 fullChannel=true；可在菜单关闭）。
                if (started && fullChannel)
                {
                    // 蓝牙只在「没有 Wi-Fi 局域网」时启用（用户明确要求）：
                    // 有 Wi-Fi 时走局域网 TCP，更快更省电，也避免同一条消息多链路重复。
                    if (!hasLan)
                    {
                        if (!BitchatEnabled) EnableBitchat();
                        if (!P2pEnabled) EnableP2p();
                        if (p2p != null) p2p.AutoConnect = true;
                        EnableWifiAware();
                    }
                    else
                    {
                        promptDismissedForNoLan = false;
                        if (p2p != null) p2p.AutoConnect = false;
                        if (BitchatEnabled)
                        {
                            try { DisableBitchat(); } catch { }
                            Android.Util.Log.Info("Bitchat", "已连 Wi-Fi：蓝牙通道停用（仅在无 Wi-Fi 时启用）");
                        }
                    }
                }
                else if (hasLan)
                {
                    promptDismissedForNoLan = false;
                    if (p2p != null) p2p.AutoConnect = false;
                }

                bool changed = force || net != prev || prevScan != scanEnabled;

                if (changed && started)
                {
                    RestartDiscovery();
                }

                // 即便尚未 start，也先告知前端当前网络状态
                Emit("network", new { type = net.ToString(), scan = scanEnabled });
            }
        }

        /// <summary>
        /// 探测设备当前的网络类型：热点 / WiFi / 移动数据 / 无网络。
        /// 热点检测通过反射 WifiManager.IsWifiApEnabled（隐藏 API），
        /// 因为热点开启时 WiFi 通常断开，ConnectivityManager 看不到可用网络。
        /// </summary>
        private NetType DetectNetworkType()
        {
            try
            {
                // 1) 先判断热点是否开启（最高优先级：热点局域网一定可扫描）
                if (IsWifiApEnabled())
                    return NetType.Hotspot;

                // 2) 通过 ConnectivityManager 判断活动网络
                if (connMgr != null)
                {
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    {
                        var net = connMgr.ActiveNetwork;
                        if (net != null)
                        {
                            var caps = connMgr.GetNetworkCapabilities(net);
                            if (caps != null)
                            {
                                if (caps.HasTransport(Android.Net.TransportType.Wifi))
                                    return NetType.Wifi;
                                if (caps.HasTransport(Android.Net.TransportType.Cellular))
                                    return NetType.Mobile;
                                if (caps.HasTransport(Android.Net.TransportType.Ethernet))
                                    return NetType.Wifi; // 有线视为可扫描局域网
                            }
                            // 有活动网络但能力为空（例如仅热点共享且未联网），回退到热点判定
                            return NetType.Hotspot;
                        }
                    }
                    else
                    {
                        // 老版本：用 NetworkInfo
                        var info = connMgr.ActiveNetworkInfo;
                        if (info != null && info.IsConnected)
                        {
                            if (info.Type == Android.Net.ConnectivityType.Wifi) return NetType.Wifi;
                            if (info.Type == Android.Net.ConnectivityType.Mobile) return NetType.Mobile;
                            if (info.Type == Android.Net.ConnectivityType.Ethernet) return NetType.Wifi;
                        }
                    }
                }

                // 3) 无活动网络，但热点可能仍开启（已步骤1判断），否则视为无网络
                return NetType.None;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"检测网络类型失败: {ex.Message}");
                return NetType.None;
            }
        }

        /// <summary>反射调用 WifiManager.IsWifiApEnabled() 判断热点是否开启。</summary>
        private bool IsWifiApEnabled()
        {
            try
            {
                var wifi = (WifiManager)context.ApplicationContext.GetSystemService(Context.WifiService);
                var m = typeof(WifiManager).GetMethod("isWifiApEnabled");
                if (m != null)
                {
                    var ret = m.Invoke(wifi, null);
                    if (ret is bool b) return b;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"判断热点失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 返回指定网络类型对应的本机 IP，用于把组播从正确的网卡发出。
        /// WiFi：取非热点保留段、非回环的 IPv4；Hotspot：取 192.168.43.x/44.x 等热点段。
        /// 找不到时回退到 IPAddress.Any（由系统选路）。
        /// </summary>
        /// <summary>
        /// 找到可用于局域网发现的本机网卡 IP。
        ///
        /// 旧实现有两个坑：
        /// 1) 靠 <see cref="WifiManager.isWifiApEnabled"/>（隐藏 API）判断热点，
        ///    Android 12+ 上它已经取不到值，反射直接返回 false；
        /// 2) 热点网段写死 192.168.43~46，而 Android 12+ 热点子网是**随机**的
        ///    （实测这台机器是 192.168.179.x），根本匹配不上。
        ///
        /// 结果就是：手机开着热点、下面挂着一堆客户端，app 却因为默认路由在
        /// 移动数据网卡上而判成 Mobile、把局域网发现整个关掉。
        ///
        /// 新逻辑只看事实——**有没有一张在用的、带私网 IPv4 的网卡**：
        ///   · 默认路由就在私网网卡上（普通 Wi-Fi/以太）→ 用它；
        ///   · 默认路由在移动数据上，但另有私网网卡（开热点）→ 用热点那张。
        /// <summary>
        /// 枚举本机「已启用、非环回」的 IPv4 网卡（名称 + 单播地址 + 掩码）。
        /// 在 Android 上优先走 java.net.NetworkInterface —— 它不读 /sys/class/net，
        /// 避免触发 SELinux 对 sysfs_net 的 AVC 拒绝（.NET 的
        /// NetworkInterface.GetAllNetworkInterfaces 会读 sysfs，产生每 8 秒一次的噪声）。
        /// 非 Android（PC 调试）回退到 .NET 实现。
        /// </summary>
        private sealed class LanIfaceInfo
        {
            public string Name;
            public bool Up;
            public bool Loopback;
            public readonly List<LanIfaceUnicast> Unicast = new List<LanIfaceUnicast>();
            public sealed class LanIfaceUnicast { public IPAddress Address; public IPAddress Mask; }
        }

        private static List<LanIfaceInfo> EnumLanInterfaces()
        {
            var result = new List<LanIfaceInfo>();
#if __ANDROID__
            try
            {
                var en = Java.Net.NetworkInterface.NetworkInterfaces;
                while (en.HasMoreElements)
                {
                    var j = (Java.Net.NetworkInterface)en.NextElement();
                    var info = new LanIfaceInfo { Name = j.Name, Up = j.IsUp, Loopback = j.IsLoopback };
                    foreach (Java.Net.InterfaceAddress ia in j.InterfaceAddresses)
                    {
                        var addr = ia.Address;
                        if (addr == null || !(addr is Java.Net.Inet4Address)) continue;
                        IPAddress ip;
                        try { ip = IPAddress.Parse(addr.HostAddress); } catch { continue; }
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        IPAddress mask = null;
                        try
                        {
                            int prefix = ia.NetworkPrefixLength;
                            uint m = prefix >= 32 ? 0xFFFFFFFF : (~((1u << (32 - prefix)) - 1));
                            mask = new IPAddress(new byte[] { (byte)(m >> 24), (byte)(m >> 16), (byte)(m >> 8), (byte)m });
                        }
                        catch { }
                        info.Unicast.Add(new LanIfaceInfo.LanIfaceUnicast { Address = ip, Mask = mask });
                    }
                    result.Add(info);
                }
                return result;
            }
            catch { }
#endif
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var info = new LanIfaceInfo
                {
                    Name = ni.Name,
                    Up = ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up,
                    Loopback = ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                };
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    info.Unicast.Add(new LanIfaceInfo.LanIfaceUnicast { Address = ua.Address, Mask = ua.IPv4Mask });
                }
                result.Add(info);
            }
            return result;
        }

        /// </summary>
        private bool TryFindLanInterface(out IPAddress lanIp, out bool onDefaultRoute)
        {
            lanIp = IPAddress.Any;
            onDefaultRoute = false;
            try
            {
                var defIface = DefaultRouteInterfaceName();

                IPAddress onDefault = IPAddress.Any;   // 默认路由网卡上的私网 IP
                IPAddress offDefault = IPAddress.Any;  // 其他私网网卡的 IP（热点/AP）
                IPAddress wifiIp = IPAddress.Any;      // 真实无线 STA 网卡（wlan*）上的私网 IP

                foreach (var ni in EnumLanInterfaces())
                {
                    if (!ni.Up) continue;
                    if (ni.Loopback) continue;

                    bool virtualIface = IsVirtualLanIface(ni.Name);
                    foreach (var ua in ni.Unicast)
                    {
                        var ip = ua.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ip)) continue;
                        if (!IsPrivateIpv4(ip)) continue;

                        if (ni.Name == defIface)
                        {
                            // 手机开热点时 ap0/p2p0 的默认路由 metric 常常更小，会被误判成
                            // 「默认路由网卡」；此时不能让它压过真正连着 Wi-Fi 的 wlan0。
                            if (virtualIface)
                            {
                                if (IPAddress.Any.Equals(offDefault)) offDefault = ip;
                            }
                            else if (IPAddress.Any.Equals(onDefault)) onDefault = ip;
                        }
                        else
                        {
                            if (IPAddress.Any.Equals(offDefault)) offDefault = ip;
                        }

                        if (!virtualIface && IsWlanIface(ni.Name) && IPAddress.Any.Equals(wifiIp))
                            wifiIp = ip;
                    }
                }

                // 1) 真实无线网卡优先：这才是与同网段设备真正互通的地址
                if (!IPAddress.Any.Equals(wifiIp))
                {
                    lanIp = wifiIp;
                    onDefaultRoute = !string.IsNullOrEmpty(defIface)
                        && defIface.StartsWith("wlan", StringComparison.OrdinalIgnoreCase);
                    return true;
                }
                // 2) 默认路由网卡（已排除热点/P2P 虚拟接口）
                if (!IPAddress.Any.Equals(onDefault))
                {
                    lanIp = onDefault; onDefaultRoute = true; return true;
                }
                // 3) 其余私网网卡（纯热点场景：只有 ap0 可用）
                if (!IPAddress.Any.Equals(offDefault))
                {
                    lanIp = offDefault; onDefaultRoute = false; return true;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"枚举局域网网卡失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>热点 / P2P / 蜂窝等虚拟接口：地址通常只在本机或热点子网内可达。</summary>
        private static bool IsVirtualLanIface(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            var n = name.ToLowerInvariant();
            return n.StartsWith("ap") || n.StartsWith("p2p") || n.StartsWith("swlan")
                || n.StartsWith("rmnet") || n.StartsWith("ccmni") || n.StartsWith("clat")
                || n.StartsWith("dummy") || n.StartsWith("tun") || n.StartsWith("v4-");
        }

        /// <summary>真实无线 STA 网卡（wlan0 / wlan1 ...）。</summary>
        private static bool IsWlanIface(string name)
        {
            return !string.IsNullOrEmpty(name) && name.ToLowerInvariant().StartsWith("wlan");
        }

        /// <summary>私网 IPv4 段（10/8、172.16/12、192.168/16）。移动数据的 CGNAT 也在 10/8，
        /// 但它会被"默认路由网卡"规则排除掉，不会误判。</summary>
        private static bool IsPrivateIpv4(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        /// <summary>默认路由走哪张网卡（读 /proc/net/route，取 metric 最小的 0.0.0.0 条目）。</summary>
        private static string DefaultRouteInterfaceName()
        {
            try
            {
                string best = null;
                long bestMetric = long.MaxValue;
                foreach (var line in File.ReadLines("/proc/net/route"))
                {
                    var p = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 8) continue;
                    if (p[0] == "Iface") continue;
                    if (p[1] != "00000000") continue;          // 只取默认路由
                    long metric;
                    if (!long.TryParse(p[6], out metric)) metric = 0;
                    if (metric < bestMetric) { bestMetric = metric; best = p[0]; }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// 由本机局域网 IP 算出该网段的定向广播地址（192.168.179.209/24 → 192.168.179.255）。
        /// 开热点时默认路由在移动数据上，往 255.255.255.255 广播会从移动网卡出去、
        /// 永远到不了热点子网；改成往"自己网段的广播地址"发，内核才会从对应网卡送出去。
        /// </summary>
        private string SubnetBroadcastOf(IPAddress ip)
        {
            if (ip == null || IPAddress.Any.Equals(ip)) return IPAddress.Broadcast.ToString();

            var a = ip.GetAddressBytes();
            var m = DefaultMaskOf(a);

            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | (~m[i] & 0xFF));
            return new IPAddress(b).ToString();
        }

        /// <summary>取网卡真实掩码；取不到就按传统 /24 兜底。</summary>
        private static byte[] DefaultMaskOf(byte[] a)
        {
            try
            {
                foreach (var ni in EnumLanInterfaces())
                {
                    if (!ni.Up) continue;
                    foreach (var ua in ni.Unicast)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (!ua.Address.GetAddressBytes().SequenceEqual(a)) continue;
                        if (ua.Mask != null) return ua.Mask.GetAddressBytes();
                    }
                }
            }
            catch { }
            return new byte[] { 255, 255, 255, 0 };
        }

        // API 21+ 网络状态回调
        private class LanNetworkCallback : ConnectivityManager.NetworkCallback
        {
            private readonly MchatBridge owner;
            public LanNetworkCallback(MchatBridge owner) { this.owner = owner; }
            public override void OnAvailable(Android.Net.Network network) => owner.EvaluateNetwork(false);
            public override void OnLost(Android.Net.Network network) => owner.EvaluateNetwork(false);
            public override void OnCapabilitiesChanged(Android.Net.Network network, Android.Net.NetworkCapabilities networkCapabilities) => owner.EvaluateNetwork(false);
            public override void OnLinkPropertiesChanged(Android.Net.Network network, Android.Net.LinkProperties linkProperties) => owner.EvaluateNetwork(false);
        }

        // 低版本（API < 21）广播接收器。注意：此类由代码手动 RegisterReceiver 注册，
        // 不要加 [BroadcastReceiver] 特性（那会要求公共默认构造且向 manifest 声明）。
        private class LanConnectivityReceiver : BroadcastReceiver
        {
            private readonly MchatBridge owner;
            public LanConnectivityReceiver(MchatBridge owner) { this.owner = owner; }
            public override void OnReceive(Context context, Intent intent)
            {
                if (intent?.Action == ConnectivityManager.ConnectivityAction)
                    owner.EvaluateNetwork(false);
            }
        }

        #endregion

        #region 省电：屏幕/前后台感知与自适应轮询

        private volatile bool screenOff;
        private volatile bool appBackgrounded;
        private ScreenStateReceiver screenReceiver;

        /// <summary>
        /// true = 灭屏或 App 在后台。此时所有周期性轮询（局域网发现、中间层信标、位置广播）
        /// 一律降频，避免后台持续唤醒 CPU 与射频。
        /// </summary>
        public bool PowerIdle { get { return screenOff || appBackgrounded; } }

        /// <summary>ChatActivity 的 OnResume / OnPause 回调。</summary>
        public void SetAppForeground(bool foreground)
        {
            var prev = PowerIdle;
            appBackgrounded = !foreground;
            if (prev != PowerIdle) OnPowerModeChanged();
        }

        private void OnPowerModeChanged()
        {
            var idle = PowerIdle;
            Android.Util.Log.Info("Mchat", "省电模式：" + (idle ? "空闲（轮询降频）" : "活跃"));

            // 中间层信标降频（10s ↔ 60s）
            try { mid.SetIdle(idle); } catch { }
            // qaul 节点的维护循环降频（15s ↔ 45s / 20s ↔ 60s）
            try { qnode?.SetIdle(idle); } catch { }
            // 蓝牙扫描档位：灭屏/后台降到 LowPower（连续扫描是 BLE 最费电的用法）
            try { if (bitchat != null && bitchat.Running) bitchat.SetPowerSave(idle); } catch { }
            // 组播锁：只要能扫描就**一直持有**。
            // Android 在没有 MulticastLock 时会过滤组播/广播包，空闲时释放锁会导致
            // 设备「听不见」别人的发现信标 —— 实测直接表现为发消息报「暂无对端」。
            try { SetMulticastLock(scanEnabled); } catch { }

            if (!idle)
            {
                // 回到前台/亮屏：立刻补一帧，缩短重新发现的时间，之后恢复常频
                try
                {
                    Task.Run(() =>
                    {
                        try { AnnounceSelfUdp(); } catch { }
                        try { mid.BeaconNow(); } catch { }
                    });
                }
                catch { }
            }

            Emit("power", new
            {
                idle,
                discoveryMs = CurrentDiscoveryIntervalMs(),
                beaconMs = mid.CurrentIntervalMs()
            });
        }

        private void RegisterScreenMonitor()
        {
            try
            {
                screenReceiver = new ScreenStateReceiver(
                    () => { screenOff = true; OnPowerModeChanged(); },
                    () => { screenOff = false; OnPowerModeChanged(); });
                var filter = new IntentFilter();
                filter.AddAction(Intent.ActionScreenOff);
                filter.AddAction(Intent.ActionScreenOn);
                // 用 ApplicationContext 注册：ChatActivity 销毁后悬浮窗/前台服务还在跑，
                // 挂在 Activity 上会随界面一起失效（还会泄漏 Activity）。
                var app = context.ApplicationContext ?? context;
                app.RegisterReceiver(screenReceiver, filter);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"注册屏幕监听失败: {ex.Message}");
            }
        }

        private void UnregisterScreenMonitor()
        {
            try { if (screenReceiver != null) context.UnregisterReceiver(screenReceiver); } catch { }
            screenReceiver = null;
        }

        private sealed class ScreenStateReceiver : BroadcastReceiver
        {
            private readonly Action onOff;
            private readonly Action onOn;
            public ScreenStateReceiver(Action off, Action onn) { onOff = off; onOn = onn; }

            public override void OnReceive(Context c, Intent intent)
            {
                if (intent == null || string.IsNullOrEmpty(intent.Action)) return;
                if (intent.Action == Intent.ActionScreenOff) { try { onOff(); } catch { } }
                else if (intent.Action == Intent.ActionScreenOn) { try { onOn(); } catch { } }
            }
        }

        #endregion

        #region UDP 组播发现

        /// <summary>
        /// 组播锁按需持有。
        /// 原来一启动就 Acquire 且全程不 Release——MulticastLock 会阻止 WiFi 进入省电模式，
        /// 这是很隐蔽的一路耗电。现在只在「可扫描且非空闲」时持有，其余时间释放。
        /// </summary>
        private void SetMulticastLock(bool on)
        {
            try
            {
                if (on)
                {
                    if (multicastLock == null)
                    {
                        var wifi = (WifiManager)context.ApplicationContext.GetSystemService(Context.WifiService);
                        multicastLock = wifi?.CreateMulticastLock("Mchat");
                        multicastLock?.SetReferenceCounted(true);
                    }
                    if (multicastLock != null && !multicastLock.IsHeld) multicastLock.Acquire();
                }
                else
                {
                    if (multicastLock != null && multicastLock.IsHeld) multicastLock.Release();
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"组播锁切换失败: {ex.Message}");
            }
        }

        private void AcquireMulticastLock()
        {
            SetMulticastLock(true);
        }

        /// <summary>刚启动的一小段时间内快速连发，缩短「开 App 后要等好几秒才看到邻居」的空窗。</summary>
        private long _discoveryBoostUntil;

        /// <summary>发现循环的实际间隔：空闲时大幅拉长。</summary>
        private int CurrentDiscoveryIntervalMs()
        {
            if (!scanEnabled) return 15000;          // 不可扫描时本来就不发包
            if (PowerIdle) return 12000;             // 灭屏/后台：12s（原来 30s，太慢导致对方找不到我们）
            // 启动后 12 秒内 700ms 一发，让设备列表尽快出现；之后回到常频省电
            if (Android.OS.SystemClock.ElapsedRealtime() < _discoveryBoostUntil)
                return 700;
            // 前台：没人时更急一点，尽快把设备找出来；已经有人了就稳住频率别太耗电
            return peers.Count > 0 ? 5000 : 3000;
        }

        /// <summary>
        /// 启动局域网发现。仅在 <see cref="scanEnabled"/> 为真时才周期性广播自身；
        /// 否则只保持接收（移动数据/无网络时不外发任何探测，避免泄露与耗流）。
        /// 套接字绑定 IPAddress.Any，由系统按路由表选路，确保能收到来自组播与广播的包。
        /// 发送时「组播 + 定向广播(255.255.255.255)」双发，提升在路由器/热点下跨设备的可达性。
        /// </summary>
        private void StartDiscovery()
        {
            var token = cts.Token;
            // 进程启动后首个 12 秒快发信标（只在第一次生效，避免每次网络切换都高频广播）
            if (_discoveryBoostUntil == 0)
                _discoveryBoostUntil = Android.OS.SystemClock.ElapsedRealtime() + 12000;
            // 代际号：每次重建发现就 +1，旧循环发现自己的代际过期就退出。
            // 否则 RestartDiscovery 只关了 socket，旧循环退避重试后会转到新 socket 上，
            // 变成两个循环同时收同一个 socket —— 每个包处理两遍，白耗电。
            int myGen = Interlocked.Increment(ref discoveryGen);
            try
            {
                // 绑定到 Any 而非具体网卡 IP：避免在某些 ROM/多网卡下因源地址绑定错误
                // 而收不到对端发来的发现包（组播与广播都能正常接收）。
                discoverySocket = new UdpClient();
                discoverySocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                discoverySocket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                try { discoverySocket.JoinMulticastGroup(IPAddress.Parse(MulticastAddress)); }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", $"加入组播组失败(继续用广播): {ex.Message}"); }
                discoverySocket.MulticastLoopback = false;
                try
                {
                    Android.Util.Log.Info("Disc", "发现套接字已绑定 "
                        + ((IPEndPoint)discoverySocket.Client.LocalEndPoint) + L10n.T(" 组播=") + MulticastAddress + ":" + DiscoveryPort);
                }
                catch { }

                // 初始化 mDNS 发现套接字（备用）
                InitMdnsDiscovery();

                // 接收广播
                Task.Run(async () =>
                {
                    int backoff = 500;
                    while (!token.IsCancellationRequested)
                    {
                        if (myGen != discoveryGen) return;      // 已被重建，退出
                        try
                        {
                            var result = await discoverySocket.ReceiveAsync().ConfigureAwait(false);
                            OnDiscoveryPacket(result.Buffer, result.RemoteEndPoint);
                            backoff = 500;      // 恢复正常，退避清零
                        }
                        catch (Exception)
                        {
                            if (token.IsCancellationRequested) break;
                            if (myGen != discoveryGen) return;  // 已被重建，退出
                            // 退避而不是固定 500ms 死循环重试：
                            // 套接字一旦持续异常，固定间隔重试会变成常驻的唤醒源。
                            try { await Task.Delay(backoff, token).ConfigureAwait(false); } catch { break; }
                            backoff = Math.Min(10000, backoff * 2);
                        }
                    }
                }, token);

                // 周期广播自身 + 清理离线。无论是否可扫描都运行，便于网络恢复后立即发现邻居。
                Task.Run(async () =>
                {
                    int tick = 0;
                    while (!token.IsCancellationRequested)
                    {
                        if (myGen != discoveryGen) return;      // 已被重建，退出
                        if (scanEnabled)
                        {
                            // probe 只是用来"催"刚上线的新设备（收到者立刻回 hello）。
                            // 稳定期每 3 轮发一次就够：广播包减半 = 射频唤醒减半。
                            bool withProbe = peers.Count == 0 || (tick % 3 == 0);
                            try { AnnounceSelfUdp(withProbe); }
                            catch (Exception ex) { Android.Util.Log.Warn("Mchat", $"广播失败: {ex.Message}"); }
                        }
                        else
                        {
                            // 不可扫描时（移动数据/无网络），把已记录的对端逐出，
                            // 因为它们的 IP 在当前网络下已不可达。
                            if (peers.Count > 0)
                            {
                                peers.Clear();
                                Emit("peers", GetPeerList());
                            }
                        }

                        PrunePeers();
                        // 顺带重放「收到了但当时解不开」的帧：密钥握完就能救回来
                        try { RetryPendingDecrypt(); } catch { }
                        tick++;

                        // 开/关热点不一定触发系统网络回调（默认路由可能一直停在移动数据上），
                        // 所以每隔几轮主动重估一次网卡。只在真变了才重启发现，不会抖动。
                        if (tick % 6 == 0)
                        {
                            try { EvaluateNetwork(false); } catch { }
                        }

                        // 间隔随电源状态自适应（灭屏/后台最长 30 秒）
                        try { await Task.Delay(CurrentDiscoveryIntervalMs(), token).ConfigureAwait(false); }
                        catch (System.OperationCanceledException) { break; }
                        catch { }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", $"组播初始化失败: {ex.Message}");
                Emit("error", new { message = L10n.T("局域网发现初始化失败: ") + ex.Message });
                return;
            }
        } // 关闭 StartDiscovery 方法

        /// <summary>
        /// 初始化 mDNS 发现套接字（备用通道）
        /// </summary>
        private void InitMdnsDiscovery()
        {
            try
            {
                mdnsSocket = new UdpClient();
                mdnsSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                mdnsSocket.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                try { mdnsSocket.JoinMulticastGroup(IPAddress.Parse(MdnsAddress)); }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", $"mDNS 组播加入失败: {ex.Message}"); }
                mdnsSocket.MulticastLoopback = false;

                // 获取组播锁
                if (mdnsMulticastLock == null)
                {
                    var wifiMgr = (WifiManager)context.GetSystemService(Context.WifiService);
                    mdnsMulticastLock = wifiMgr.CreateMulticastLock("MchatMdnsLock");
                    mdnsMulticastLock.SetReferenceCounted(true);
                    mdnsMulticastLock.Acquire();
                }

                Android.Util.Log.Info("Disc", "mDNS 发现套接字已绑定端口 " + MdnsPort);

                // 启动 mDNS 接收循环
                var token = cts.Token;
                Task.Run(async () =>
                {
                    int backoff = 500;
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var result = await mdnsSocket.ReceiveAsync().ConfigureAwait(false);
                            OnMdnsPacket(result.Buffer, result.RemoteEndPoint);
                            backoff = 500;
                        }
                        catch (Exception)
                        {
                            if (token.IsCancellationRequested) break;
                            try { await Task.Delay(backoff, token).ConfigureAwait(false); } catch { break; }
                            backoff = Math.Min(10000, backoff * 2);
                        }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"mDNS 初始化失败(仅组播可用): {ex.Message}");
            }
        }

        // 处理 mDNS 数据包
        private void OnMdnsPacket(byte[] buffer, IPEndPoint remoteEp)
        {
            try
            {
                var json = Encoding.UTF8.GetString(buffer);
                var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (msg != null && msg.ContainsKey("t") && msg["t"].ToString() == "mdns-beacon")
                {
                    // 处理 mDNS 信标包
                    OnDiscoveryPacket(buffer, remoteEp);
                }
            }
            catch { }
        }

        /// <summary>
        /// 组播 + 定向广播一帧 hello（可选再发一帧 probe）。
        /// 定向广播是因为部分路由器/热点会丢组播；两路都发更可靠。
        ///
        /// 三路 destinations：
        ///  1. 组播 239.x —— 同网段组播，正常 Wi-Fi 下最快；
        ///  2. 255.255.255.255 —— 受限广播，走默认路由那张网卡；
        ///  3. **自己网段的定向广播**（如 192.168.179.255）—— 关键的一路。
        ///     开着热点时默认路由在移动数据上，(2) 会从移动网卡出去，
        ///     永远到不了热点子网；(3) 才会被内核从对应网卡送出去。
        /// </summary>
        /// <summary>
        /// 发现包专用发送口：绑定本机局域网 IP，让源地址与出口网卡都固定为真正连通的那张网卡。
        /// 拿不到具体 IP（无局域网 / 只有 Any）时返回 null，调用方回退共用接收口。
        /// </summary>
        private UdpClient DiscoverySender()
        {
            if (discoverySendSocket != null) return discoverySendSocket;
            try
            {
                if (discoveryBindIp == null || IPAddress.Any.Equals(discoveryBindIp) || IPAddress.IPv6Any.Equals(discoveryBindIp))
                    return null;
                var s = new UdpClient(new IPEndPoint(discoveryBindIp, 0));
                s.EnableBroadcast = true;
                // 组播必须显式指定出口网卡，否则仍按默认路由走（多网卡时常常是热点口）
                try
                {
                    s.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        discoveryBindIp.GetAddressBytes());
                }
                catch { }
                try { s.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1); } catch { }
                discoverySendSocket = s;
                Android.Util.Log.Info("Disc", "发现发送口已绑定 " + discoveryBindIp + "（固定源地址/出口网卡）");
                return s;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Disc", "创建发现发送口失败(回退共用接收口): " + ex.Message);
                return null;
            }
        }

        private void AnnounceSelfUdp(bool withProbe = false)
        {
            if (discoverySocket == null || !scanEnabled) return;
            try
            {
                var eps = new List<IPEndPoint>
                {
                    new IPEndPoint(IPAddress.Parse(MulticastAddress), DiscoveryPort),
                    new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)
                };
                try
                {
                    var sub = SubnetBroadcastOf(discoveryBindIp);
                    if (!string.IsNullOrEmpty(sub) && sub != IPAddress.Broadcast.ToString())
                        eps.Add(new IPEndPoint(IPAddress.Parse(sub), DiscoveryPort));
                }
                catch { }

                var hello = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    t = "hello",
                    id = peerId,
                    name = displayName,
                    port = tcpPort,
                    qp = qaul != null ? qaul.Port : 0,
                    bc = Identity != null ? Identity.BitchatPeerId : null,
                    qk = Identity != null ? Identity.QaulKeyHex.Substring(0, 16) : null,
                    // 本机开放被控时把端口与设备码一起通告：电脑端据此把手机列为「可远控」。
                    // 未开启时 rd=0 / dc="" ，对端会直接忽略（与 desk 的解析约定一致）。
                    rd = RdHostServer.AdvertisedPort,
                    dc = RdHostServer.AdvertisedCode,
                    // ws=网页服务端口：电脑端用源 IP 拼出 http://<手机IP>:<端口> 作为「网页远控」入口，
                    // 与电脑端 hello 里的 ws=9013 字段语义一致（MchatW.ParseWebRemote）。
                    ws = WebServer.ExternalPortActual > 0 ? WebServer.ExternalPortActual : 7790
                }));
                var sender = DiscoverySender() ?? discoverySocket;
                foreach (var ep in eps)
                {
                    try { sender.Send(hello, hello.Length, ep); } catch { }
                }

                if (!withProbe) return;
                var probe = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    t = "probe",
                    id = peerId
                }));
                foreach (var ep in eps)
                {
                    try { sender.Send(probe, probe.Length, ep); } catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"组播广播失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 网络类型变化后重建发现套接字：清空旧 peer、按新网卡重新绑定组播，
        /// 并立刻广播一帧 hello 让新上线的邻居快速发现本机。
        /// </summary>
        private void RestartDiscovery()
        {
            try
            {
                // 关闭旧接收套接字，让接收任务自然退出
                try { discoverySocket?.Close(); } catch { }
                discoverySocket = null;
                // 发送口绑定了旧网卡的 IP，网络变化后必须重建
                try { discoverySendSocket?.Close(); } catch { }
                discoverySendSocket = null;
                // 关闭 mDNS 套接字
                try { mdnsSocket?.Close(); } catch { }
                mdnsSocket = null;

                // 清空旧节点：切换网络后旧 IP 不可达，避免显示过期在线设备
                if (peers.Count > 0)
                {
                    peers.Clear();
                    Emit("peers", GetPeerList());
                }

                StartDiscovery();

                // 组播锁跟着"能不能扫"走（不随省电空闲释放，否则收不到对方信标）
                try { SetMulticastLock(scanEnabled); } catch { }

                // 立即广播一帧（含 probe），缩短邻居发现延迟
                AnnounceSelfUdp(true);

                Emit("network", new { type = currentNetType.ToString(), scan = scanEnabled });
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", $"重建发现失败: {ex.Message}");
            }
        }

        private void OnDiscoveryPacket(byte[] data, IPEndPoint remote)
        {
            try
            {
                // 诊断：确认是否真的收到局域网发现包（只打前若干条，避免刷屏）
                DiagRx++;
                if (DiagRx <= 12 || DiagRx % 50 == 0)
                    Android.Util.Log.Info("Disc", "rx #" + DiagRx + " " + remote + " " + data.Length + "B");
            }
            catch { }
            try
            {
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(Encoding.UTF8.GetString(data));
                if (obj == null) return;

                // 中间层信标：走 UDP 来的，解密后交给中间层登记/中继，绝不进聊天记录
                if (obj.TryGetValue("t", out var tv0) && tv0?.ToString() == "lcbeacon")
                {
                    var conv0 = obj.TryGetValue("conv", out var cv0) ? cv0?.ToString() : null;
                    var payload = obj.TryGetValue("p", out var pv0) ? pv0?.ToString() : null;
                    if (!string.IsNullOrEmpty(conv0) && !string.IsNullOrEmpty(payload)) OnUdpBeacon(conv0, payload);
                    return;
                }

                var id = obj.TryGetValue("id", out var v) ? v?.ToString() : null;
                try
                {
                    if (DiagRx <= 20 || DiagRx % 200 == 0)
                    {
                        var tt0 = obj.TryGetValue("t", out var tvd) ? tvd?.ToString() : "?";
                        Android.Util.Log.Info("Disc", "parse t=" + tt0 + " id=" + id + " self=" + peerId
                            + (id == peerId ? " ※SAME-AS-SELF" : ""));
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(id) || id == peerId) return;

                var name = obj.TryGetValue("name", out var nv) ? nv?.ToString() : id;
                int port = DefaultTcpPort;
                if (obj.TryGetValue("port", out var pv)) int.TryParse(pv?.ToString(), out port);

                // 对端 qaul 消息端口（新版才会广播，老版本没有该字段 → 0，仅走 Mchat 渠道）
                int qport = 0;
                if (obj.TryGetValue("qp", out var qv)) int.TryParse(qv?.ToString(), out qport);

                // 对端的两套身份指纹（新版才有）
                var bc = obj.TryGetValue("bc", out var bcv) ? bcv?.ToString() : null;
                var qk = obj.TryGetValue("qk", out var qkv) ? qkv?.ToString() : null;

                // 对端的远控端口（desk Windows 端通告；0 或缺失表示不可远控）
                int rport = 0;
                if (obj.TryGetValue("rd", out var rdv)) int.TryParse(rdv?.ToString(), out rport);

                // 对端设备码（可选）：desk 与开启被控的安卓端都会广播，用于自动填表
                var dcode = obj.TryGetValue("dc", out var dcv) ? (dcv?.ToString() ?? "") : "";
                dcode = (dcode ?? "").Trim();

                // 对端的网页远控（可选）：TV / TVCMS / 画板在 hello 的 ws 字段通告自己的 HTTP 端口。
                // 约定 ws 为端口数字（如 8080），用 hello 源 IP 拼成完整基址；也兼容直接写完整 URL。
                var wrem = obj.TryGetValue("ws", out var wsv) ? (wsv?.ToString() ?? "") : "";
                wrem = (wrem ?? "").Trim();
                if (!string.IsNullOrEmpty(wrem))
                {
                    if (wrem.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || wrem.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        // 已是完整 URL，原样保留
                    }
                    else if (int.TryParse(wrem, out var wport) && wport > 0 && wport <= 65535)
                    {
                        wrem = "http://" + remote.Address.ToString() + ":" + wport;
                    }
                    else wrem = "";
                }

                // 收到对端主动探测：立即回一帧 hello，让对方快速发现刚上线的本机，
                // 避免对方要等到本机下一个周期广播窗口才能发现本机。
                if (obj.TryGetValue("t", out var tv) && tv?.ToString() == "probe")
                {
                    if (scanEnabled && discoverySocket != null)
                    {
                        try
                        {
                            var reply = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                            {
                                t = "hello",
                                id = peerId,
                                name = displayName,
                                port = tcpPort,
                                qp = qaul != null ? qaul.Port : 0,
                                bc = Identity != null ? Identity.BitchatPeerId : null,
                                qk = Identity != null ? Identity.QaulKeyHex.Substring(0, 16) : null,
                                rd = RdHostServer.AdvertisedPort,
                                dc = RdHostServer.AdvertisedCode
                            }));
                            discoverySocket.Send(reply, reply.Length,
                                new IPEndPoint(remote.Address, DiscoveryPort));
                        }
                        catch (Exception ex)
                        {
                            Android.Util.Log.Warn("Mchat", $"探测回包失败: {ex.Message}");
                        }
                    }
                }

                // 局域网双向互通确认：收到 hello 就回一帧 probe；对方回 hello 才算 LAN peer。
                // 这是 mesh 频道准入的前置条件，不影响其它频道的设备发现。
                if (obj.TryGetValue("t", out var tvh) && tvh?.ToString() == "hello")
                {
                    LanPeerGate.OnHello(id, remote.Address, addr =>
                    {
                        if (!scanEnabled || discoverySocket == null) return;
                        var probe = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                        {
                            t = "probe",
                            id = peerId,
                            name = displayName
                        }));
                        discoverySocket.Send(probe, probe.Length, new IPEndPoint(addr, DiscoveryPort));
                    });
                }

                bool isNew = !peers.ContainsKey(id);
                peers[id] = new Peer
                {
                    Id = id,
                    Name = name,
                    Address = remote.Address.ToString(),
                    Port = port,
                    QaulPort = qport,
                    BitchatId = bc,
                    QaulKeyShort = qk,
                    RemotePort = rport,
                    DeviceCode = dcode,
                    WebRemote = wrem,
                    LastSeen = SysEnv.TickCount
                };

                // 电视端（如「电视:1」）：设备栏只认 devstat 帧，而电视只发 hello，
                // 这里把它的 hello 名同步进设备状态表，电视才会出现在频道设备列表中。
                try
                {
                    if (name != null && name.StartsWith("电视", StringComparison.Ordinal))
                    {
                        var tvd = new Dictionary<string, object>
                        {
                            ["peer"] = id,
                            ["name"] = name,
                            ["dev"] = L10n.T("电视"),
                            ["ip"] = remote.Address.ToString(),
                            ["battery"] = -1.0,
                            ["charging"] = false,
                            ["link"] = "lan",
                            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        lock (devicePresenceLock) devicePresence[id] = tvd;
                        try { DevicePresenceChanged?.Invoke(); } catch { }
                    }
                }
                catch { }

                // 新设备接入：把本次运行期缓存的消息（含附件字节）补发给它，
                // 让后上线的设备也能看到之前的对话与图片/文件。
                if (isNew)
                {
                    try
                    {
                        var target = peers[id];
                        System.Threading.Tasks.Task.Run(() => SyncSessionTo(target));
                    }
                    catch { }
                }

                // 只在「新上线」或「昵称/端口变了」时才通知界面：
                // 对端每 5 秒广播一次 hello，若每次都 Emit，WebView 就要每秒被唤醒一次。
                var changed = isNew;
                if (!changed && obj.TryGetValue("t", out var tt) && tt?.ToString() == "hello")
                {
                    Peer old;
                    if (peers.TryGetValue(id, out old) && old != null)
                        changed = old.Name != name || old.Port != port || old.QaulPort != qport;
                }

                if (changed)
                {
                    Emit("peers", GetPeerList());
                    if (isNew)
                    {
                        // 新邻居上线：握手一次，让对方拿到本机昵称（私聊标题需要）
                        Task.Run(() => AnnouncePresence());
                    }
                }
            }
            catch { }
        }

        private void PrunePeers()
        {
            var now = SysEnv.TickCount;
            bool changed = false;
            foreach (var kv in peers.ToArray())
            {
                if (now - kv.Value.LastSeen >= PeerTimeoutMs)
                {
                    peers.TryRemove(kv.Key, out _);
                    changed = true;
                }
            }
            if (changed) Emit("peers", GetPeerList());
        }

        private object GetPeerList()
        {
            var now = SysEnv.TickCount;
            return peers.Values
                .Where(p => now - p.LastSeen < PeerTimeoutMs && !p.ViaRelay)
                .Select(p => new { id = p.Id, name = p.Name, ip = FormatIpOnly(p.Address), port = p.Port, qp = p.QaulPort, rd = p.RemotePort })
                .ToList();
        }

        #endregion

        #region 网页端支撑：历史 / 访客 / 长轮询

        /// <summary>当前消息序号，网页端据此做增量拉取。</summary>
        public long MessageSeq
        {
            get { lock (historyLock) { return messageSeq; } }
        }

        private class HistoryItem
        {
            public long Seq;
            public Dictionary<string, object> Message;
        }

        /// <summary>把一条消息写入统一历史，并唤醒所有等待中的网页端长轮询。</summary>
        private void AppendHistory(Dictionary<string, object> msg)
        {
            if (msg == null) return;
            lock (historyLock)
            {
                messageSeq++;
                history.Add(new HistoryItem { Seq = messageSeq, Message = msg });
                if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
                Monitor.PulseAll(historyLock);
            }
            // 通知悬浮窗等订阅方刷新（在锁外触发，避免死锁）
            try { HistoryChanged?.Invoke(); } catch { }
        }

        /// <summary>取最近 N 条消息快照（悬浮窗预览用，跨会话、含重启前的历史）。</summary>
        public List<Dictionary<string, object>> RecentMessages(int max)
        {
            if (max <= 0) max = 10;
            var persisted = store.Recent(max);
            if (persisted.Count >= max) return persisted;

            // 内存里的控制帧（删除/清空）也算「最近事件」，追加在末尾
            List<Dictionary<string, object>> tail;
            lock (historyLock)
            {
                tail = history.Skip(Math.Max(0, history.Count - (max - persisted.Count)))
                              .Select(h => h.Message).ToList();
            }
            var result = new List<Dictionary<string, object>>(persisted);
            result.AddRange(tail);
            return result;
        }

        /// <summary>
        /// 以本机身份直接发送一条文本消息（悬浮窗输入框用）：
        /// 与前端 sendMessage 等价——进历史、推本机界面、广播局域网，并交给机器人处理。
        /// </summary>
        public Dictionary<string, object> SendLocalText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var msg = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["conv"] = activeConv,
                    ["kind"] = "text",
                    ["text"] = text,
                    ["from"] = peerId,
                    ["fromName"] = displayName,
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _dedupEngine.MarkSeen(msg["id"].ToString());
                store.Add(msg, true);
                AppendHistory(msg);
                Emit("message", msg);
                Deliver(msg, null);
                return msg;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"浮窗发送失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 长轮询：返回 seq 大于 since 的消息。若暂时没有新消息则阻塞等待，
        /// 直到有新消息或超时（返回空集合）。
        /// conv 非空时只返回该会话的消息与控制帧。
        /// </summary>
        public List<Dictionary<string, object>> WaitForMessages(long since, int timeoutMs, string conv = null)
        {
            var target = string.IsNullOrEmpty(conv) ? activeConv : conv;

            // 首次拉取（since<=0）以持久化仓库为权威视图并且立即返回：
            //   · 已删除的消息不会从内存历史里「复活」；
            //   · 空房间也能秒开，不必干等一轮长轮询（25s）。
            if (since <= 0)
            {
                var init = store.Messages(target, 300);
                try { init = init.FindAll(m => !IsLockedPlaceholder(m)); } catch { }
                return init;
            }

            lock (historyLock)
            {
                var pending = Collect(since, target);
                if (pending.Count > 0) return pending;

                Monitor.Wait(historyLock, timeoutMs);
                return Collect(since, target);
            }
        }

        // 调用方必须已持有 historyLock
        private List<Dictionary<string, object>> Collect(long since, string conv = null)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var item in history)
            {
                if (item.Seq <= since) continue;
                var msg = item.Message;
                if (!string.IsNullOrEmpty(conv) && Msg.Conv(msg) != conv) continue;
                // 普通消息若已被删除（墓碑），不再回放；控制帧保留，供网页端同步删除状态
                if (!Msg.IsControl(msg) && store.IsDeleted(Msg.Str(msg, "id"))) continue;
                list.Add(msg);
            }
            return list;
        }

        /// <summary>网页访客身份。</summary>
        public class Guest
        {
            public string Id;
            public string Name;
            public string Ip;
        }

        /// <summary>按浏览器 IP 领取（或复用）访客身份，展示名固定为「网页端+IP」。</summary>
        public Guest EnsureGuest(string ip)
        {
            var key = string.IsNullOrEmpty(ip) ? "unknown" : ip;
            return guests.GetOrAdd(key, k => new Guest
            {
                Id = "web-" + k.Replace('.', '-'),
                Name = L10n.T("网页端") + k,
                Ip = k
            });
        }

        /// <summary>
        /// 代访客发送一条消息：既落到本机历史与 app 界面，也广播到局域网其他设备。
        /// 与 app 自身发送的区别只在于 from / fromName 使用访客身份。
        /// </summary>
        public Dictionary<string, object> SendFromGuest(Guest guest, string kind, string text, string url)
        {
            return SendFromGuest(guest, kind, text, url, null, null, null);
        }

        /// <summary>
        /// 代访客发送一条消息。
        /// base64 / name 非空时表示随附二进制媒体（如语音消息），写入本地媒体目录
        /// 并生成可访问的 mediaUrl，随消息一并广播。
        /// </summary>
        public Dictionary<string, object> SendFromGuest(Guest guest, string kind, string text, string url,
                                                         string base64, string name, string conv = null)
        {
            var msg = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["conv"] = string.IsNullOrEmpty(conv) ? activeConv : conv,
                ["kind"] = string.IsNullOrEmpty(kind) ? "text" : kind,
                ["text"] = text ?? "",
                ["from"] = guest.Id,
                ["fromName"] = guest.Name,
                ["viaHost"] = peerId,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (!string.IsNullOrEmpty(url)) msg["url"] = url;

            // 语音等带二进制的消息：落地为本地媒体并暴露 mediaUrl
            if (!string.IsNullOrEmpty(base64) && !string.IsNullOrEmpty(name))
            {
                try
                {
                    var data = Convert.FromBase64String(base64);
                    var fileName = SafeMediaName(name, msg["id"].ToString());
                    File.WriteAllBytes(Path.Combine(mediaDir, fileName), data);
                    msg["media"] = fileName;
                    msg["mediaUrl"] = "/__media/" + fileName;
                    // media 是「id+扩展名」的落地名；原始文件名单独放 name，供对端自动接收按原名保存
                    msg["name"] = name;
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", $"访客媒体落地失败: {ex.Message}");
                }
            }

            _dedupEngine.MarkSeen(msg["id"].ToString());
            store.Add(msg, true);
            AppendHistory(msg);

            // 推给宿主 app 的界面，使手机界面同步显示这条访客消息
            Emit("message", msg);

            // 访客消息（来自网页端等）若被圆括号包裹，视为功能控制指令，在本机执行
            MaybeExecControl(msg);

            var payload = JsonConvert.SerializeObject(msg);
            byte[] attachment = null;
            if (msg.TryGetValue("media", out var m) && m != null)
            {
                try { attachment = File.ReadAllBytes(Path.Combine(mediaDir, m.ToString())); }
                catch { attachment = null; }
            }
            Deliver(msg, attachment);
            _ = payload;

            return msg;
        }

        /// <summary>给网页端用的在线设备列表（含宿主自身）。</summary>
        public object GetPeerListForWeb()
        {
            var now = SysEnv.TickCount;
            var list = new List<object>
            {
                new
                {
                    id = peerId,
                    name = displayName,
                    self = true,
                    bitchat = Identity != null ? Identity.BitchatPeerId : null,
                    qaul = Identity != null ? Identity.QaulKeyHex.Substring(0, 16) : null
                }
            };
            foreach (var p in peers.Values)
            {
                if (now - p.LastSeen >= PeerTimeoutMs) continue;
                if (p.ViaRelay) continue; // 中继那边的设备只是通信渠道，不纳入本地发现
                if (IsPainterPeer(p)) continue; // 画板有独立入口，不混入附近设备列表
                list.Add(new
                {
                    id = p.Id,
                    name = p.Name,
                    self = false,
                    bitchat = p.BitchatId,
                    qaul = p.QaulKeyShort,
                    ip = p.Address,
                    rd = p.RemotePort,
                    dc = p.DeviceCode,
                    ws = p.WebRemote
                });
            }
            return list;
        }

        #endregion

        #region 位置共享

        private LocationManager locMgr;
        private SensorManager sensorMgr;
        private readonly object locLock = new object();
        private LocPoint selfLoc;
        private bool locSharing;
        private System.Timers.Timer locTimer;
        private System.Timers.Timer locBearTimer;
        private float currentBearing;
        private long lastBearPush;
        /// <summary>上一次对外广播的位置（用于"没动就不发"的省电判定）。</summary>
        private double lastBcLat, lastBcLon;
        private bool hasBcRef;
        private long lastLocBroadcast;
        /// <summary>最近一次被接受的定位（精度择优的基准，防 network 粗定位抖动覆盖 GPS）。</summary>
        private Location lastAcceptedLoc;
        private readonly ConcurrentDictionary<string, LocPoint> peerLocs =
            new ConcurrentDictionary<string, LocPoint>();
        private readonly LocListener locListener = new LocListener();
        private readonly OriListener oriListener = new OriListener();

        /// <summary>供 LocListener 回调定位到当前 Bridge 实例（单例）。</summary>
        private static MchatBridge BridgeInstance;

        private class LocPoint
        {
            public double Lat, Lon, Alt;
            public float Bearing, Acc;
            public string Name, From;
            public long Time;          // 定位采样时间（仅展示用）
            public long ReceivedAt;    // 本机收到该帧的时间（判新鲜度用，避免静止设备被误删）
            public int Battery = -1;   // 设备电量百分比（-1=未知），随位置帧一起广播，供方位雷达显示
        }

        /// <summary>本机是否正在共享位置（供 UI 判断是否发/撤位置共享卡片）。</summary>
        public bool IsLocationSharing
        {
            get { return locSharing; }
        }

        [Java.Interop.Export("startLocationShare")]
        [JavascriptInterface]
        public void StartLocationShare()
        {
            if (locSharing) { EmitSelfLocation(); return; }
            locSharing = true;
            try
            {
                locMgr = (LocationManager)context.GetSystemService(Context.LocationService);
                sensorMgr = (SensorManager)context.GetSystemService(Context.SensorService);

                bool granted = context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                                == Android.Content.PM.Permission.Granted
                            || context.CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation)
                                == Android.Content.PM.Permission.Granted;

                if (locMgr != null && granted)
                {
                    // 精度修复 1：向「所有已启用的定位源」注册更新（gps / network / fused / passive），
                    // 不再只认 "gps"/"network" 两个字面量——部分 ROM 的融合定位 provider 名不同。
                    // 具体接受哪一帧由 OnLocationFix 的精度闸门决定（见 ShouldAccept）。
                    foreach (var provider in SafeProviders())
                    {
                        try
                        {
                            // 省电：原来 GPS 1 秒、网络源 4 秒且 minDistance=0（原地不动也持续刷新），
                            // 等于让 GPS 常亮。改为 GPS 5 秒 + 位移 5 米、网络源 15 秒 + 位移 20 米：
                            // 站着不动时几乎不再触发定位，走路时仍然跟得上。
                            bool isGps = provider == LocationManager.GpsProvider;
                            long minTime = isGps ? 5000 : 15000;
                            float minDist = isGps ? 5f : 20f;
                            // 指定主线程 Looper：本方法可能由后台线程（网页 /api/gps/start、对方 devreq 等）触发，
                            // 不带 Looper 的重载在后台线程会抛 "Can't create handler inside thread..."。
                            locMgr.RequestLocationUpdates(provider, minTime, minDist, locListener, Looper.MainLooper);
                        }
                        catch { }
                    }

                    // 精度修复 2：暖启动只用 60 秒内的新鲜定位，避免把几小时前的
                    // 陈旧 last-known 当成当前位置显示（表现为「定位漂移」）。
                    var last = BestLastKnownLocation();
                    if (last != null) UpdateSelfLocation(last);
                    else Emit("locationError", new { message = L10n.T("正在等待卫星定位…") });
                }
                else if (!granted)
                {
                    Emit("locationError", new { message = L10n.T("未授权定位权限") });
                }

                // 朝向变化推送给前端：节流到 250ms（原来是 80ms），
                // 再密肉眼看不出差别，却会让 CPU 一直在算、WebView 一直在跑 JS。
                oriListener.OnBearing = b =>
                {
                    lock (locLock) { currentBearing = b; }
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now - lastBearPush > 250)
                    {
                        lastBearPush = now;
                        EmitSelfLocation();
                    }
                };
                if (sensorMgr != null)
                {
                    // 省电：SensorDelay.Ui 是 ~20ms 一采（等于传感器常开），
                    // 换成 Normal（~200ms）后箭头依旧跟手，CPU 占用大幅下降。
                    try { sensorMgr.RegisterListener(oriListener,
                        sensorMgr.GetDefaultSensor(SensorType.Accelerometer), SensorDelay.Normal); } catch { }
                    try { sensorMgr.RegisterListener(oriListener,
                        sensorMgr.GetDefaultSensor(SensorType.MagneticField), SensorDelay.Normal); } catch { }
                }

                // 广播周期 500ms → 3000ms（配合 BroadcastSelfLocation 里的位移阈值，
                // 原地不动时基本不发网络包）
                locTimer = new System.Timers.Timer(3000);
                locTimer.Elapsed += (s, e) => BroadcastSelfLocation();
                locTimer.Start();

                // 朝向推送 1000ms → 2000ms
                locBearTimer = new System.Timers.Timer(2000);
                locBearTimer.Elapsed += (s, e) => EmitSelfLocation();
                locBearTimer.Start();

                EmitSelfLocation();
            }
            catch (System.Exception ex)
            {
                Android.Util.Log.Error("Mchat", "启动位置共享失败: " + ex.Message);
            }
        }

        [Java.Interop.Export("stopLocationShare")]
        [JavascriptInterface]
        public void StopLocationShare()
        {
            if (!locSharing) return;
            locSharing = false;
            try { locTimer?.Stop(); locTimer?.Dispose(); locTimer = null; } catch { }
            try { locBearTimer?.Stop(); locBearTimer?.Dispose(); locBearTimer = null; } catch { }
            try { locMgr?.RemoveUpdates(locListener); } catch { }
            try { sensorMgr?.UnregisterListener(oriListener); } catch { }
            locMgr = null; sensorMgr = null;
            lock (locLock) { lastAcceptedLoc = null; }
        }

        /* ===================== 定位精度择优（GPS 准度修复核心） ===================== */

        /// <summary>取当前已启用的定位源列表；取不到时回退 gps/network。</summary>
        private List<string> SafeProviders()
        {
            var list = new List<string>();
            try { foreach (var p in locMgr.GetProviders(true) ?? new List<string>()) if (!string.IsNullOrEmpty(p)) list.Add(p); }
            catch { }
            if (list.Count == 0)
            {
                try { list.Add(LocationManager.GpsProvider); } catch { }
                try { list.Add(LocationManager.NetworkProvider); } catch { }
            }
            return list;
        }

        /// <summary>在各定位源的 last-known 中挑「最新鲜且最准」的一帧。</summary>
        private Location BestLastKnownLocation()
        {
            Location best = null;
            foreach (var p in SafeProviders())
            {
                Location l = null;
                try { l = locMgr.GetLastKnownLocation(p); } catch { }
                if (l == null) continue;
                if (!IsFresh(l, 60000)) continue;   // 超过 1 分钟的陈旧定位不用
                if (best == null || IsBetter(l, best)) best = l;
            }
            return best;
        }

        /// <summary>定位是否足够新鲜（优先按 elapsed realtime 判断，回退按墙上时钟）。</summary>
        private static bool IsFresh(Location l, long maxAgeMs)
        {
            try
            {
                var ageNanos = Android.OS.SystemClock.ElapsedRealtimeNanos() - l.ElapsedRealtimeNanos;
                return ageNanos / 1000000.0 <= maxAgeMs;
            }
            catch
            {
                try { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - l.Time <= maxAgeMs; }
                catch { return true; }
            }
        }

        private static bool IsGps(Location l)
        {
            try { return l.Provider == LocationManager.GpsProvider; } catch { return false; }
        }

        /// <summary>定位精度（无精度信息按 9999m 计）。</summary>
        private static float Acc(Location l)
        {
            try { return l.HasAccuracy && l.Accuracy > 0 ? l.Accuracy : 9999f; } catch { return 9999f; }
        }

        /// <summary>a 是否比 b 更优：GPS 优先，其次精度更高。</summary>
        private static bool IsBetter(Location a, Location b)
        {
            if (a == null) return false;
            if (b == null) return true;
            bool aGps = IsGps(a), bGps = IsGps(b);
            if (aGps != bGps) return aGps;
            return Acc(a) < Acc(b);
        }

        private static long AgeMs(Location l)
        {
            try
            {
                return (long)((Android.OS.SystemClock.ElapsedRealtimeNanos() - l.ElapsedRealtimeNanos) / 1000000.0);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 精度闸门：决定是否接受一帧新定位。
        /// 修复点：原实现无脑接受 gps/network 的全部回调，network 粗定位（数十~上千米误差）
        /// 会频繁覆盖 GPS 精准定位，表现为地图上的点来回跳动、越用越偏。
        /// 新规则：
        ///   1) 明显更优（精度提升 40%+）→ 接受；
        ///   2) GPS 定位战胜 network 定位 → 接受；
        ///   3) network 定位不覆盖 30 秒内的 GPS 定位；
        ///   4) 精度相近时，仅当旧定位放置超过 8 秒才刷新（消除同精度抖动）；
        ///   5) 精度差于 500 米的定位直接拒绝。
        /// </summary>
        private bool ShouldAccept(Location loc)
        {
            Location cur;
            lock (locLock) { cur = lastAcceptedLoc; }
            if (cur == null) return true;

            float newAcc = Acc(loc), curAcc = Acc(cur);
            bool newGps = IsGps(loc), curGps = IsGps(cur);
            long curAgeMs = AgeMs(cur);

            if (newAcc > 500f) return false;                              // 太粗，拒收
            if (newAcc < curAcc * 0.6f) return true;                      // 明显更优
            if (newGps && !curGps && newAcc < 200f) return true;          // GPS 战胜网络定位
            if (!newGps && curGps && curAgeMs < 30000) return false;      // 网络定位不覆盖新鲜 GPS
            if (newAcc <= Math.Max(curAcc, 50f) && curAgeMs > 8000) return true;  // 同精度防抖刷新
            return false;
        }

        /// <summary>定位回调入口：过精度闸门后才更新自身位置。</summary>
        private void OnLocationFix(Location loc)
        {
            if (loc == null) return;
            if (!ShouldAccept(loc)) return;
            lock (locLock) { lastAcceptedLoc = loc; }
            UpdateSelfLocation(loc);
        }

        private void UpdateSelfLocation(Location loc)
        {
            if (loc == null) return;
            // 移动中优先采用 GPS 自带的朝向（比罗盘稳）；静止时用罗盘朝向
            float bearing = currentBearing;
            try
            {
                if (loc.HasBearing && loc.HasSpeed && loc.Speed > 1.5f)
                    bearing = loc.Bearing;
            }
            catch { }
            lock (locLock)
            {
                selfLoc = new LocPoint
                {
                    Lat = loc.Latitude,
                    Lon = loc.Longitude,
                    Alt = loc.HasAltitude ? loc.Altitude : 0,
                    Bearing = bearing,
                    Acc = Acc(loc) >= 9999f ? 0f : Acc(loc),
                    Name = displayName,
                    From = peerId,
                    Battery = DeviceStatus.Battery,
                    Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            EmitSelfLocation();
        }

        private long lastSelfLocEmit;
        private double lastEmitLat, lastEmitLon;
        private float lastEmitBearing;

        private void EmitSelfLocation()
        {
            LocPoint p; lock (locLock) { p = selfLoc; }
            if (p == null) return;
            // 使用实时朝向，而非定位时的旧朝向
            p.Bearing = currentBearing;

            // 省电：Emit 会跑到 WebView 里执行 JS，每秒一次等于让 WebView 每秒醒来一次。
            // 位置基本没变（<1 米）且朝向变化 <3° 时，最多 5 秒推一次。
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool moved = Math.Abs(p.Lat - lastEmitLat) > 1e-6 || Math.Abs(p.Lon - lastEmitLon) > 1e-6
                         || DistanceMeters(lastEmitLat, lastEmitLon, p.Lat, p.Lon) > 1.0;
            bool turned = Math.Abs(p.Bearing - lastEmitBearing) > 3f;
            if (!moved && !turned && now - lastSelfLocEmit < 5000) return;

            lastSelfLocEmit = now;
            lastEmitLat = p.Lat; lastEmitLon = p.Lon; lastEmitBearing = p.Bearing;
            Emit("selflocation", ToDict(p));
        }

        private Dictionary<string, object> ToDict(LocPoint p)
        {
            return new Dictionary<string, object>
            {
                ["from"] = p.From, ["name"] = p.Name,
                ["lat"] = p.Lat, ["lon"] = p.Lon,
                ["alt"] = p.Alt, ["bearing"] = p.Bearing,
                ["acc"] = p.Acc, ["time"] = p.Time,
                ["battery"] = p.Battery
            };
        }

        /// <summary>
        /// 供网页端（App 内 www / 对外 webui）主动拉取"本机 + 当前缓存的所有对端"位置快照。
        /// 返回 JSON：{ self:{...}|null, peers:[{...}], bearing:float, now:ms }。
        /// 与 Emit 推送互补：页面首次加载时先拉一次，之后靠事件/轮询刷新。
        /// </summary>
        [Java.Interop.Export("getGpsState")]
        [JavascriptInterface]
        public string GetGpsState()
        {
            try
            {
                var list = new List<Dictionary<string, object>>();
                lock (locLock)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var kv in peerLocs)
                    {
                        if (kv.Value == null) continue;
                        long fresh = kv.Value.ReceivedAt > 0 ? kv.Value.ReceivedAt : kv.Value.Time;
                        if (fresh > 0 && now - fresh > 30000) continue; // 按收包时间过滤 30 秒过期
                        list.Add(ToDict(kv.Value));
                    }
                    var self = selfLoc == null ? null : ToDict(selfLoc);
                    return JsonConvert.SerializeObject(new Dictionary<string, object>
                    {
                        ["self"] = self,
                        ["peers"] = list,
                        ["bearing"] = currentBearing,
                        ["now"] = now
                    });
                }
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return "{\"self\":null,\"peers\":[],\"bearing\":0,\"now\":" + now + ",\"error\":" +
                       JsonConvert.SerializeObject(ex.Message) + "}";
            }
        }

        /// <summary>非桥调用（WebServer / 内部）也用同一份快照。</summary>
        internal string GetGpsStateJson() { return GetGpsState(); }

        private void BroadcastSelfLocation()
        {
            if (!locSharing) return;
            LocPoint p;
            lock (locLock) { p = selfLoc; }
            if (p == null)
            {
                try { UpdateSelfLocation(BestLastKnownLocation()); }
                catch { }
                lock (locLock) { p = selfLoc; }
                if (p == null) return;
            }

            // 省电闸门：原来每 500ms 就向每个在线 peer 开一条 TCP 连发一帧，
            // 人站着不动时这些包全是白发的（10 个 peer = 每秒 20 次握手）。
            // 现在：最短间隔 2.5 秒；若位移不足 2 米，则放宽到 15 秒一发（仅作心跳）。
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - lastLocBroadcast < 2500) return;
            if (hasBcRef && DistanceMeters(lastBcLat, lastBcLon, p.Lat, p.Lon) < 2.0
                         && nowMs - lastLocBroadcast < 15000) return;
            lastLocBroadcast = nowMs;
            lastBcLat = p.Lat; lastBcLon = p.Lon; hasBcRef = true;
            // GPS 共享专用隐藏渠道：走频道的多链路可靠出站队列（含重试/去重），
            // 既不落聊天历史也不推消息气泡，只被 IngestIncoming 拦截更新雷达。
            // time 用「当前时间」而非定位采样时间：静止时 GPS 采样不更新，若发采样时间，
            // 对端按时间判过期会把没动的设备删掉（时隐时现的根因）。
            var frame = new Dictionary<string, object>
            {
                ["id"] = System.Guid.NewGuid().ToString("N"),
                ["conv"] = GpsShareConv,
                ["kind"] = GpsShareKind,
                ["from"] = peerId,
                ["fromName"] = displayName,
                ["name"] = displayName,
                ["lat"] = p.Lat, ["lon"] = p.Lon, ["alt"] = p.Alt,
                ["bearing"] = p.Bearing, ["acc"] = p.Acc,
                ["time"] = nowMs,          // 发送时刻：作为对方的新鲜度时间
                ["fixTime"] = p.Time,      // 定位采样时刻：仅展示
                ["battery"] = DeviceStatus.Battery
            };
            // 用 Dictionary 重载 → SealOutgoing + EnqueueOutbound（可靠），不是裸 TCP 广播
            BroadcastFrame(frame, GpsShareConv);
        }

        /// <summary>两点球面距离（米）。近似即可，只用于"有没有动"的省电判定。</summary>
        private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        private void HandleLocationFrame(Dictionary<string, object> header)
        {
            try
            {
                double lat = ReadDouble(header, "lat");
                double lon = ReadDouble(header, "lon");
                // 坐标合法性校验（对齐 GpsRadarActivity.cs:229）
                if (double.IsNaN(lat) || double.IsNaN(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180) return;
                if (Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001) return; // (0,0) 无效

                var p = new LocPoint
                {
                    From = ReadString(header, "from"),
                    Name = ReadString(header, "name") ?? ReadString(header, "fromName") ?? "",
                    Lat = lat,
                    Lon = lon,
                    Alt = double.IsNaN(ReadDouble(header, "alt")) ? 0 : ReadDouble(header, "alt"),
                    Bearing = (float)(double.IsNaN(ReadDouble(header, "bearing")) ? 0 : ReadDouble(header, "bearing")),
                    Acc = (float)(double.IsNaN(ReadDouble(header, "acc")) ? 0 : ReadDouble(header, "acc")),
                    Time = ReadLong(header, "fixTime") > 0 ? ReadLong(header, "fixTime") : ReadLong(header, "time"),
                    ReceivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),  // 以本机收包时刻判新鲜度
                    Battery = (int)(double.IsNaN(ReadDouble(header, "battery")) ? -1 : ReadDouble(header, "battery"))
                };
                // 若对方时钟偏差很大，仍以本机收包时刻为准（ReceivedAt 上面已设置）。
                if (string.IsNullOrEmpty(p.From) || p.From == peerId) return;
                peerLocs[p.From] = p;
                PrunePeerLocs();
                Emit("location", ToDict(p));
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("Mchat", "处理位置帧失败: " + ex.Message); }
        }

        /// <summary>清理超过 30 秒未收到任何心跳的对端位置（按收包时间，静止设备不会被误删）。</summary>
        private void PrunePeerLocs()
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 30000;
                foreach (var key in peerLocs.Keys)
                {
                    if (peerLocs.TryGetValue(key, out var lp))
                    {
                        long fresh = lp.ReceivedAt > 0 ? lp.ReceivedAt : lp.Time;
                        if (fresh < cutoff) peerLocs.TryRemove(key, out _);
                    }
                }
            }
            catch { }
        }

        private double ReadDouble(Dictionary<string, object> d, string k, double def = double.NaN)
        {
            if (d != null && d.TryGetValue(k, out var v) && v != null)
            {
                try { return Convert.ToDouble(v); } catch { }
            }
            return def;
        }

        private string? ReadString(Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v) && v != null)
            {
                try { return v.ToString(); } catch { }
            }
            return null;
        }

        private long ReadLong(Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v) && v != null)
            {
                try { return Convert.ToInt64(v); } catch { }
            }
            return 0;
        }

        private class LocListener : Java.Lang.Object, ILocationListener
        {
            public void OnLocationChanged(Location location) => BridgeInstance?.OnLocationFix(location);
            public void OnProviderDisabled(string provider) { }
            public void OnProviderEnabled(string provider) { }
            public void OnStatusChanged(string provider, Availability status, Android.OS.Bundle extras) { }
        }

        private class OriListener : Java.Lang.Object, ISensorEventListener
        {
            public Action<float> OnBearing;
            private float[] gravity, geomagnetic;
            public void OnSensorChanged(SensorEvent e)
            {
                if (e.Sensor.Type == SensorType.Accelerometer) gravity = e.Values.ToArray();
                else if (e.Sensor.Type == SensorType.MagneticField) geomagnetic = e.Values.ToArray();
                else return;
                if (gravity == null || geomagnetic == null) return;
                float[] r = new float[9], i = new float[9];
                if (!SensorManager.GetRotationMatrix(r, i, gravity, geomagnetic)) return;
                float[] ori = new float[3];
                SensorManager.GetOrientation(r, ori);
                float azimuth = (float)(ori[0] * 180.0 / System.Math.PI + 360) % 360;
                OnBearing?.Invoke(azimuth);
            }
            public void OnAccuracyChanged(Sensor sensor, SensorStatus accuracy) { }
        }

        #endregion

        #region 工具

        /// <summary>
        /// StringBuilder 缓存池：减少热路径上的字符串拼接分配。
        /// </summary>
        private static class StringBuilderCache
        {
            [ThreadStatic]
            private static StringBuilder _cachedInstance;
            private const int MaxBuilderSize = 360;

            public static StringBuilder Acquire()
            {
                var sb = _cachedInstance;
                if (sb == null || sb.Capacity > MaxBuilderSize)
                {
                    sb = new StringBuilder(128);
                }
                else
                {
                    sb.Length = 0;
                }
                _cachedInstance = null;
                return sb;
            }

            public static string GetStringAndRelease(StringBuilder sb)
            {
                string result = sb.ToString();
                sb.Length = 0;
                _cachedInstance = sb;
                return result;
            }
        }

        /// <summary>
        /// 统一去重引擎：id去重 + 内容去重 + 跨渠道去重
        /// 使用 LRU + TTL 双重淘汰，批量清理减少锁竞争
        /// </summary>
        private class DeduplicationEngine
        {
            // id 去重：使用统一常量
            private readonly ConcurrentDictionary<string, long> _seenIds = new ConcurrentDictionary<string, long>();
            private readonly ConcurrentQueue<string> _idOrder = new ConcurrentQueue<string>();
            private readonly long IdTtlMs = DedupIdTtlMs;
            private readonly int IdMaxEntries = DedupIdMaxEntries;

            // 内容去重：使用统一常量
            private readonly ConcurrentDictionary<string, long> _seenContent = new ConcurrentDictionary<string, long>();
            private readonly ConcurrentQueue<string> _contentOrder = new ConcurrentQueue<string>();
            private readonly long ContentTtlMs = DedupContentTtlMs;
            private readonly int ContentMaxEntries = DedupContentMaxEntries;

            // 跨渠道去重：使用统一常量
            private readonly ConcurrentDictionary<string, long> _seenFrames = new ConcurrentDictionary<string, long>();
            private readonly long FrameWindowMs = DedupFrameWindowMs;
            private readonly int FrameMaxEntries = DedupFrameMaxEntries;

            // 批量清理：每 N 次插入触发一次
            private int _insertCount = 0;
            private const int CleanupInterval = 50;

            private readonly MchatBridge _bridge;

            public DeduplicationEngine(MchatBridge bridge)
            {
                _bridge = bridge;
            }

            /// <summary>
            /// 统一去重入口：先 id，再内容，再跨渠道
            /// 返回 true = 新消息可处理，false = 重复丢弃
            /// </summary>
            public bool TryMark(string msgId, Dictionary<string, object> msg)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // 1. ID 去重（最快路径）
                if (!string.IsNullOrEmpty(msgId))
                {
                    if (!_seenIds.TryAdd(msgId, now))
                        return false; // 重复 id
                    _idOrder.Enqueue(msgId);
                }

                // 2. 内容去重（兜底）
                var contentKey = BuildContentKey(msg);
                if (!string.IsNullOrEmpty(contentKey))
                {
                    if (!_seenContent.TryAdd(contentKey, now))
                        return false; // 重复内容
                    _contentOrder.Enqueue(contentKey);
                }

                // 3. 跨渠道去重（Mchat 帧 vs qaul-native 等）
                var frameKey = BuildFrameKey(msg);
                if (!string.IsNullOrEmpty(frameKey))
                {
                    // 注意：这里必须用 TryAdd 判断「是不是第一次」。
                    // 以前用 GetOrAdd 取 existing：键不存在时它返回的正是刚写入的 now，
                    // now - existing == 0 ≤ 6000 → 被当成「6 秒内跨渠道重复」直接丢弃，
                    // 于是**每条新消息第一次到达都会被丢掉**（对端永远收不到，界面上
                    // 只有自己发的消息；电脑端因为帧键不同才侥幸能收到）。
                    if (!_seenFrames.TryAdd(frameKey, now))
                    {
                        if (_seenFrames.TryGetValue(frameKey, out var existing) && now - existing <= 6000)
                            return false; // 6 秒内跨渠道重复
                        _seenFrames[frameKey] = now; // 超过窗口，刷新时间戳
                    }
                }

                // 批量清理
                if (++_insertCount >= CleanupInterval)
                {
                    _insertCount = 0;
                    Cleanup(now);
                }

                return true; // 新消息
            }

            private string BuildContentKey(Dictionary<string, object> msg)
            {
                try
                {
                    var from = Msg.Str(msg, "from");
                    var text = Msg.Str(msg, "text");
                    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(text))
                        return null;
                    // 使用 StringBuilder 避免多次字符串拼接分配
                    var sb = StringBuilderCache.Acquire();
                    sb.Append(Msg.Conv(msg));
                    sb.Append('\u001F');
                    sb.Append(Msg.Str(msg, "from"));
                    sb.Append('\u001F');
                    sb.Append(Msg.Str(msg, "kind"));
                    sb.Append('\u001F');
                    sb.Append(Msg.Num(msg, "time", 0).ToString());
                    sb.Append('\u001F');
                    sb.Append(Msg.Str(msg, "text"));
                    return StringBuilderCache.GetStringAndRelease(sb);
                }
                catch { return null; }
            }

            private string BuildFrameKey(Dictionary<string, object> msg)
            {
                try
                {
                    var from = Msg.Str(msg, "from");
                    if (string.IsNullOrEmpty(from)) return null;

                    var text = Msg.Str(msg, "text") ?? "";
                    var song = Msg.Str(msg, "songKey") ?? "";
                    if (text.Length == 0 && song.Length == 0) return null;

                    var sb = StringBuilderCache.Acquire();
                    sb.Append(Msg.Conv(msg));
                    sb.Append('\u001F');
                    sb.Append(from);
                    sb.Append('\u001F');
                    sb.Append(Msg.Str(msg, "kind"));
                    sb.Append('\u001F');
                    sb.Append(text);
                    sb.Append('\u001F');
                    sb.Append(song);
                    sb.Append('\u001F');
                    sb.Append(Msg.Num(msg, "seq", 0).ToString());
                    sb.Append('\u001F');
                    sb.Append(Msg.Str(msg, "title") ?? "");
                    return StringBuilderCache.GetStringAndRelease(sb);
                }
                catch { return null; }
            }

            private void Cleanup(long now)
            {
                long idCutoff = now - IdTtlMs;
                while (_idOrder.Count > IdMaxEntries && _idOrder.TryDequeue(out var old))
                    _seenIds.TryRemove(old, out _);
                while (_idOrder.TryPeek(out var head))
                {
                    if (!_seenIds.TryGetValue(head, out var t) || t >= idCutoff) break;
                    if (!_idOrder.TryDequeue(out var dropped)) break;
                    _seenIds.TryRemove(dropped, out _);
                }

                long contentCutoff = now - ContentTtlMs;
                while (_contentOrder.Count > ContentMaxEntries && _contentOrder.TryDequeue(out var old))
                    _seenContent.TryRemove(old, out _);
                while (_contentOrder.TryPeek(out var head))
                {
                    if (!_seenContent.TryGetValue(head, out var t) || t >= contentCutoff) break;
                    if (!_contentOrder.TryDequeue(out var dropped)) break;
                    _seenContent.TryRemove(dropped, out _);
                }

                long frameCutoff = now - (FrameWindowMs * 10); // 10倍窗口清理
                if (_seenFrames.Count > FrameMaxEntries)
                {
                    // 避免 LINQ 分配：直接遍历并收集过期键
                    var keysToRemove = new List<string>(64);
                    foreach (var kvp in _seenFrames)
                    {
                        if (now - kvp.Value > frameCutoff)
                            keysToRemove.Add(kvp.Key);
                    }
                    foreach (var k in keysToRemove)
                        _seenFrames.TryRemove(k, out _);
                }
            }

            // 兼容旧接口
            public bool MarkSeen(string id) => !string.IsNullOrEmpty(id) && _seenIds.TryAdd(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            public void UnmarkSeen(string id) { if (!string.IsNullOrEmpty(id)) _seenIds.TryRemove(id, out _); }
        }

        private readonly DeduplicationEngine _dedupEngine;

        /// <summary>
        /// 内容级去重（兜底，与 <see cref="MarkSeen"/> 的 id 去重互补）。
        ///
        /// 多跳转发时同一条消息理论上 id 相同；但只要有任何一条链路（第三方实现、
        /// 旧版本、中继重封包）生成了不同 id，id 去重就失效，聊天界面会看到两遍。
        /// 这里用「发送者 + 会话 + 明文正文 + 原始发送时间」再认一次：
        /// 时间来自原消息的 time 字段（不是本地收到的时刻），所以绕多少跳都能对上。
        /// 返回 false 表示这条内容刚出现过，应当直接丢弃。
        /// </summary>
        /// <summary>
        /// 跨渠道去重（按内容，不看 id）：Mchat 帧与 qaul-native 两条路的 id 不同，
        /// 同一条消息会在会话里出两遍。这里用「会话+发送者+类型+正文+卡片标识」兜底，
        /// 6 秒窗口内的同内容视为同一帧的副本。
        /// 注意：比的是**线上文本**（密文），不是明文，既能对上又不会泄露内容。
        /// </summary>
        private bool IsCrossChannelDup(Dictionary<string, object> msg)
        {
            return !_dedupEngine.TryMark(null, msg); // 复用统一去重引擎的跨渠道检查
        }

        private bool MarkContentSeen(Dictionary<string, object> msg)
        {
            return _dedupEngine.TryMark(null, msg); // 复用统一去重引擎
        }

        /// <summary>
        /// 撤销一次 MarkSeen。用于「解密失败」这类**可恢复**的情况：
        /// 消息先到、密钥（LC1 握手/qaul 身份）后到时，如果 id 被永久标成已读，
        /// 等密钥就绪后同一条消息重传过来会被直接丢掉，用户永远看到 "[无法解密]"。
        /// qaul 的处理方式也是「确认前不销账」，思路一致。
        /// </summary>
        private void UnmarkSeen(string id)
        {
            _dedupEngine.UnmarkSeen(id);
        }

        private static string SafeMediaName(string original, string fallbackId)
        {
            var ext = "";
            if (!string.IsNullOrEmpty(original))
            {
                try { ext = Path.GetExtension(original); } catch { }
            }
            if (string.IsNullOrEmpty(ext) || ext.Length > 12) ext = ".bin";
            var invalid = Path.GetInvalidFileNameChars();
            ext = new string(ext.Where(c => !invalid.Contains(c)).ToArray());
            return fallbackId + ext;
        }

        /// <summary>按扩展名把收到的 qaul 附件归类成会话消息类型：pic / video / audio / file。</summary>
        private static string MediaKind(string name)
        {
            var ext = (Path.GetExtension(name) ?? "").ToLower().TrimStart('.');
            if (ext == "jpg" || ext == "jpeg" || ext == "png" || ext == "gif" || ext == "webp"
                || ext == "bmp" || ext == "heic" || ext == "heif" || ext == "avif") return "pic";
            if (ext == "mp4" || ext == "mov" || ext == "m4v" || ext == "3gp" || ext == "webm"
                || ext == "mkv" || ext == "avi" || ext == "flv") return "video";
            if (ext == "mp3" || ext == "wav" || ext == "ogg" || ext == "m4a" || ext == "aac"
                || ext == "flac" || ext == "amr" || ext == "opus") return "audio";
            return "file";
        }

        /// <summary>安全地弹一个短 Toast（context 为空或异常时静默）。</summary>
        private void ToastSafe(string message)
        {
            try
            {
                if (context != null) L10n.Toast(context, message, ToastLength.Short).Show();
                else QaulLog(message);
            }
            catch { }
        }

        private static void WriteInt32(System.IO.Stream s, int value)
        {
            var b = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            s.Write(b, 0, 4);
        }

        private static int ReadInt32(System.IO.Stream s)
        {
            var b = ReadExact(s, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }

        private static byte[] ReadExact(System.IO.Stream s, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = s.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException(L10n.T("连接提前关闭"));
                offset += read;
            }
            return buffer;
        }

        #endregion

        #region 释放

        public void Shutdown()
        {
            started = false;
            try { voiceCall?.Hangup(); } catch { }
            try { mid?.Stop(); } catch { }
            try { cts?.Cancel(); } catch { }
            try { tcpListener?.Stop(); } catch { }
            try { qaul?.Stop(); } catch { }
            try { if (qnode != null) qnode.Stop(); } catch { }
            qnode = null;
            try { store?.Compact(); } catch { }
            try { discoverySocket?.Close(); } catch { }
            try { multicastLock?.Release(); } catch { }
            try { UnregisterNetworkMonitor(); } catch { }
            try { UnregisterScreenMonitor(); } catch { }
            try { outbox.Stop(); } catch { }
            tcpListener = null;
            discoverySocket = null;
            multicastLock = null;
            peers.Clear();
            // 2026-11-26 需求：退出后台 = 消息列表只留下频道。
            //   · 所有会话（频道 + 私聊）的消息全部清空；
            //   · 私聊会话整体删除（否则零消息标题还会留在列表里）；
            //   · 频道本身保留（mesh + 手动加入），由 ChannelRegistry 持久化；
            // 消息本来就不落盘，这里主动清一次是为了让「悬浮窗仍在、Bridge 未必重建」的场景也干净。
            try
            {
                if (store != null)
                {
                    // Load() 在 persist=false 下会清空 items / deletedIds / knownIds / readAt / titles
                    // 且不回放、不写盘——比逐会话墓碑更彻底，连私聊标题都不留。
                    store.ClearAll();
                    store.Load();
                }
            }
            catch { }
            try { channels?.ClearForExit(); } catch { }
            try { LanPeerGate.Clear(); } catch { }

            // 关键：清掉静态引用。否则 App 退出后 Current 仍指向一个**已停止**的桥，
            // 后台来短信时 SmsForwarder 会以为桥可用，把消息发进一个死掉的传输层（静默丢失）。
            try { if (Current == this) Current = null; } catch { }

            // 顺带收掉定位，避免退出后 GPS 还亮着
            try { LocationReporter.Stop(); } catch { }
        }

        /// <summary>注销网络状态监听。</summary>
        private void UnregisterNetworkMonitor()
        {
            try
            {
                if (netCallback != null && connMgr != null && Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                {
                    connMgr.UnregisterNetworkCallback(netCallback);
                    netCallback = null;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", $"注销网络监听失败: {ex.Message}");
            }
        }

        #endregion

        #endregion

        private class Peer
        {
            public string Id;
            public string Name;
            public string Address;
            public int Port;
            /// <summary>对端 qaul 消息端口（0 表示对端不支持 qaul 渠道）。</summary>
            public int QaulPort;
            /// <summary>对端 bitchat peerID（16 hex，指纹前 8 字节）。</summary>
            public string BitchatId;
            /// <summary>对端 qaul 公钥前 16 hex（用于核对身份）。</summary>
            public string QaulKeyShort;
            /// <summary>
            /// 对端的远控端口（0 = 不可远控）。
            /// desk（Windows 远程桌面）在 hello 里带 rd 字段通告自己的远控端口，
            /// 手机据此知道"这台电脑能被控"，才在界面上给出远控入口。
            /// </summary>
            public int RemotePort;
            /// <summary>
            /// 对端 9 位设备码（desk 与“允许被远控”的安卓端都会在 hello 里带 dc）。
            /// 有了它，界面点“远程控制”就能把设备码自动填好，用户不必再手抄一串数字。
            /// </summary>
            public string DeviceCode;
            /// <summary>
            /// 对端的「HTTP 网页远控」基址（如 http://192.168.1.50:8080）。
            /// TV / TVCMS / 画板这类设备没有 desk 9012 端口，但自带网页远控（点按/滑动、Web 控制台），
            /// 在 hello 的 ws 字段里通告；手机端据此在设备详情里给出「打开网页远控」入口。
            /// 为空表示对端不提供网页远控。
            /// </summary>
            public string WebRemote;
            public int LastSeen;
            /// <summary>该节点是通过 WLAN Direct（P2P）连上的，不是局域网组播发现的。</summary>
            public bool IsP2p;
            /// <summary>
            /// 该节点来自中继链路（中继那边的设备）。它只是通信对端，
            /// 不是本地局域网可直连设备，发现列表里一律不显示。
            /// </summary>
            public bool ViaRelay;
        }

        /// <summary>静态中转站端点：IPv4 / IPv6 / 域名 + 自定义端口。</summary>
        public class RelayEndpoint
        {
            public string Host { get; set; }
            public int Port { get; set; }
        }
    }
}

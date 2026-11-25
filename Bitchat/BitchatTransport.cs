using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Newtonsoft.Json;

#nullable enable

namespace AhoChatA
{
    /// <summary>
    /// bitchat 的 BLE mesh 传输层（之前只实现了身份与包格式，BLE 传输未做）。
    ///
    /// 本类把本应用变成 bitchat 网络的一个**双角色节点**：
    ///   · 作为外设（Peripheral）：开 GATT Server + 广播 service UUID，让附近设备连上来收消息；
    ///   · 作为中心（Central）：扫描同 service UUID 的邻居并主动连接，向它们写消息。
    /// 两点之间若不止一跳，则按 bitchat 的 CourierEnvelope 思路做**逐跳中继**（tunnel），
    /// 即本程序作为传输节点，把不是发给自己的包按 TTL 继续转发——这正是「聊天的两个程序
    /// 没有直连时，用本程序开展消息隧道」的实现基础。
    ///
    /// 为最大化互操作，BLE 的 service UUID 直接复用 qaul 的 BLE service
    /// （99e91399-80ed-4943-9bcb-39c532a76023），特征沿用 1401/1402/1403 三段。
    /// 这样本程序同时是 qaul-over-BLE 与 bitchat 的汇聚节点，承载的是 bitchat 的包格式。
    ///
    /// 全部分支都 try/catch 包裹：BLE 不可用 / 不支持多播广播 / 权限缺失时安全降级，
    /// 绝不让崩溃冒泡到主线程。
    /// </summary>
    public class BitchatTransport
    {
        // qaul / bitchat 共用的 BLE service 与三段特征
        public static readonly Java.Util.UUID ServiceUuid =
            Java.Util.UUID.FromString("99e91399-80ed-4943-9bcb-39c532a76023");
        public static readonly Java.Util.UUID CharReadUuid =
            Java.Util.UUID.FromString("99e91399-80ed-4943-9bcb-39c532a761401");
        public static readonly Java.Util.UUID CharMsgUuid =
            Java.Util.UUID.FromString("99e91399-80ed-4943-9bcb-39c532a761402");
        public static readonly Java.Util.UUID CharGdUuid =
            Java.Util.UUID.FromString("99e91399-80ed-4943-9bcb-39c532a761403");

        private const int MaxConnections = 6;
        private const int ChunkHeaderSize = 4;          // 0xF1, msgSeq, seq, total
        private const byte ChunkMagic = 0xF1;

        private readonly Activity context;
        private readonly LocalIdentity identity;

        private BluetoothManager btManager;
        private BluetoothAdapter btAdapter;
        private BluetoothLeAdvertiser advertiser;
        private BluetoothLeScanner scanner;
        private BluetoothGattServer gattServer;

        private readonly GattServerCb serverCb;
        private readonly ScanCb scanCb;
        private readonly AdvCb advCb;

        private volatile bool running;
        private int mtu = 23;                 // 未协商时按默认 ATT MTU，payload 20 字节，保证跨设备安全

        private readonly object peerGate = new object();
        private readonly Dictionary<string, PeerConn> peers = new Dictionary<string, PeerConn>();
        private readonly ConcurrentDictionary<string, byte> seenMessages = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentQueue<string> seenOrder = new ConcurrentQueue<string>();

        // 权限请求回调（由 Activity 的 OnRequestPermissionsResult 转交）
        private static Action<bool> pendingPermCb;
        public const int BluetoothPermRequestCode = 2001;

        public Action<string, string, string, string> OnMessage;   // conv, fromPeer, fromName, text
        public Action OnNeighborChange;
        public Action<string> OnLog;
        public Action<bool> OnEnabledChanged;

        public bool Running => running;
        public int NeighborCount { get { lock (peerGate) return peers.Count; } }

        public BitchatTransport(Activity context, LocalIdentity identity)
        {
            this.context = context;
            this.identity = identity;
            serverCb = new GattServerCb(this);
            scanCb = new ScanCb(this);
            advCb = new AdvCb(this);
        }

        /* ===================== 权限 ===================== */

        /// <summary>是否需要运行时申请蓝牙权限（Android 12+ 必须）。</summary>
        public bool NeedsPermission()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                return context.CheckSelfPermission(Android.Manifest.Permission.BluetoothScan) != Android.Content.PM.Permission.Granted
                    || context.CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) != Android.Content.PM.Permission.Granted;
            }
            return context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Android.Content.PM.Permission.Granted;
        }

        /// <summary>申请蓝牙权限；result 经 Activity.OnRequestPermissionsResult → OnPermissionResult 转交。</summary>
        public void RequestPermission(Action<bool> onResult)
        {
            pendingPermCb = onResult;
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                {
                    context.RequestPermissions(new string[]
                    {
                        Android.Manifest.Permission.BluetoothScan,
                        Android.Manifest.Permission.BluetoothConnect,
                        Android.Manifest.Permission.BluetoothAdvertise
                    }, BluetoothPermRequestCode);
                }
                else
                {
                    context.RequestPermissions(new string[]
                    {
                        Android.Manifest.Permission.AccessFineLocation,
                        Android.Manifest.Permission.Bluetooth,
                        Android.Manifest.Permission.BluetoothAdmin
                    }, BluetoothPermRequestCode);
                }
            }
            catch (Exception ex)
            {
                Log(L10n.T("申请蓝牙权限失败: ") + ex.Message);
                pendingPermCb = null;
                onResult?.Invoke(false);
            }
        }

        public static void OnPermissionResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            if (requestCode != BluetoothPermRequestCode) return;
            bool ok = grantResults != null && grantResults.Length > 0;
            foreach (var g in grantResults) if (g != Android.Content.PM.Permission.Granted) ok = false;
            var cb = pendingPermCb; pendingPermCb = null;
            cb?.Invoke(ok);
        }

        /* ===================== 启停 ===================== */

        public bool Enable()
        {
            if (running) return true;
            try
            {
                if (NeedsPermission())
                {
                    Log(L10n.T("蓝牙权限未授予，无法启用 bitchat mesh"));
                    return false;
                }
                btManager = (BluetoothManager)context.GetSystemService(Context.BluetoothService);
                btAdapter = btManager?.Adapter;
                if (btAdapter == null || !btAdapter.IsEnabled)
                {
                    Log(L10n.T("蓝牙不可用或未开启"));
                    return false;
                }

                running = true;

                // 1) GATT Server（外设角色）
                try
                {
                    gattServer = btManager.OpenGattServer(context, serverCb);
                    if (gattServer != null) gattServer.AddService(BuildService());
                }
                catch (Exception ex) { Log(L10n.T("GATT Server 启动失败: ") + ex.Message); }

                // 2) 广播（让邻居能发现我们）
                try
                {
                    advertiser = btAdapter.BluetoothLeAdvertiser;
                    if (advertiser != null)
                    {
                        var settings = new AdvertiseSettings.Builder()
                            .SetAdvertiseMode(AdvertiseMode.Balanced)
                            .SetConnectable(true)
                            .SetTimeout(0)
                            .Build();
                        var data = new AdvertiseData.Builder()
                            .AddServiceUuid(new ParcelUuid(ServiceUuid))
                            .Build();
                        // 扫描应答不再塞 peerId：128 位服务 UUID + 16 字节 peerId 的
                        // Service Data 会超过 BLE 传统广播 31 字节上限（ADVERTISE_FAILED_DATA_TOO_LARGE）。
                        // 扫描端本来也不读这段 Service Data（只用 MAC/RSSI + GATT 握手换身份），
                        // 去掉后广播恢复正常，身份仍由 GATT 特征里的 hello 交换。
                        var scanResp = new AdvertiseData.Builder()
                            .SetIncludeDeviceName(false)
                            .Build();
                        advertiser.StartAdvertising(settings, data, scanResp, advCb);
                    }
                    else Log(L10n.T("本机不支持 BLE 广播（多播广播）；仅作为中心扫描/连接"));
                }
                catch (Exception ex) { Log(L10n.T("广播失败: ") + ex.Message); }

                // 3) 扫描（中心角色，发现邻居）
                try { StartScan(); }
                catch (Exception ex) { Log(L10n.T("扫描失败: ") + ex.Message); }
                StartScanWatchdog();

                Log(L10n.T("bitchat BLE mesh 已启用，本机 peerID=") + identity.BitchatPeerId);
                OnEnabledChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                Log(L10n.T("启用 bitchat 失败: ") + ex.Message);
                running = false;
                return false;
            }
        }

        private volatile bool powerSave;

        /// <summary>扫描看门狗：MIUI/ColorOS 有时会静默停掉 BLE 扫描，定期重启一次保活。</summary>
        private System.Threading.Timer scanWatchdog;

        /// <summary>
        /// 扫描档位——BLE 省电的关键开关。
        /// 原来固定用 ScanMode.LowLatency：那是「不间歇连续扫描」，蓝牙射频常开，
        /// 是 BLE 最费电的用法。改成默认 Balanced（按 duty cycle 扫），
        /// 灭屏/后台再降到 LowPower。
        /// </summary>
        private Android.Bluetooth.LE.ScanMode CurrentScanMode()
        {
            return powerSave ? Android.Bluetooth.LE.ScanMode.LowPower
                             : Android.Bluetooth.LE.ScanMode.Balanced;
        }

        private void StartScan()
        {
            scanner = btAdapter?.BluetoothLeScanner;
            if (scanner == null) return;
            var sset = new ScanSettings.Builder().SetScanMode(CurrentScanMode()).Build();
            var filter = new ScanFilter.Builder().SetServiceUuid(new ParcelUuid(ServiceUuid)).Build();
            var filters = new List<ScanFilter> { filter };
            scanner.StartScan(filters, sset, scanCb);
        }

        /// <summary>切换省电扫描档位（灭屏 / App 进后台时由宿主调用）。</summary>
        public void SetPowerSave(bool on)
        {
            if (powerSave == on) return;
            powerSave = on;
            if (!running) return;
            try { scanner?.StopScan(scanCb); } catch { }
            try { StartScan(); } catch (Exception ex) { Log(L10n.T("切换扫描档位失败: ") + ex.Message); }
            Log(L10n.T("BLE 扫描档位 → ") + (on ? L10n.T("LowPower（省电）") : "Balanced"));
        }

        public void Disable()
        {
            running = false;
            try { scanWatchdog?.Dispose(); scanWatchdog = null; } catch { }
            try { scanner?.StopScan(scanCb); } catch { }
            try { advertiser?.StopAdvertising(advCb); } catch { }
            lock (peerGate)
            {
                foreach (var p in peers.Values)
                {
                    try { p.Gatt?.Close(); } catch { }
                }
                peers.Clear();
            }
            try { gattServer?.Close(); gattServer = null; } catch { }
            OnNeighborChange?.Invoke();
            OnEnabledChanged?.Invoke(false);
            Log(L10n.T("bitchat BLE mesh 已关闭"));
        }

        /* ===================== 发送 ===================== */

        /// <summary>在公共频道（bitchat:room）广播一条消息。</summary>
        public void SendToRoom(string roomCode, string text, string fromName)
        {
            var payload = JsonPayload("room", roomCode, fromName, text);
            SendPacket(BuildPacket(roomCode, payload, null), "bitchat:" + NormalRoom(roomCode));
        }

        /// <summary>给某个 bitchat 私聊对端发消息。</summary>
        public void SendToDm(string peerId, string text, string fromName)
        {
            var payload = JsonPayload("dm", null, fromName, text);
            var rid = PeerIdTo8(peerId);
            SendPacket(BuildPacket(null, payload, rid), "bitchatdm:" + peerId);
        }

        /// <summary>向 bitchat 群组发消息（群内逐成员单播，与 qaul 群语义一致）。</summary>
        public void SendToGroup(string groupIdHex, string text, string fromName)
        {
            var payload = JsonPayload("group", groupIdHex, fromName, text);
            SendPacket(BuildPacket(null, payload, null), "bitchatgroup:" + groupIdHex);
        }

        private BitchatPacket BuildPacket(string room, byte[] payload, byte[] recipient)
        {
            var p = new BitchatPacket
            {
                Type = BitchatType.Message,
                Ttl = 7,
                Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SenderId = PeerIdTo8(identity.BitchatPeerId),
                RecipientId = recipient,
                Payload = payload ?? new byte[0]
            };
            p.Compressed = false;
            return p;
        }

        private void SendPacket(BitchatPacket packet, string convHint)
        {
            if (!running) return;
            byte[] raw;
            try { raw = packet.Encode(false, null); }
            catch (Exception ex) { Log(L10n.T("打包失败: ") + ex.Message); return; }

            var neighbors = new List<PeerConn>();
            lock (peerGate) neighbors.AddRange(peers.Values);
            foreach (var p in neighbors)
            {
                try { EnqueueChunked(p, raw); }
                catch (Exception ex) { Log(L10n.T("入队失败: ") + ex.Message); }
            }
        }

        /// <summary>把 raw 按 MTU 分片，逐片发给某个邻居（优先中心侧 GATT，否则外设侧 notify）。</summary>
        private void EnqueueChunked(PeerConn p, byte[] raw)
        {
            int chunkSize = Math.Max(20, mtu - ChunkHeaderSize);
            int total = (raw.Length + chunkSize - 1) / chunkSize;
            byte msgSeq = p.NextMsgSeq();
            for (int seq = 0; seq < total; seq++)
            {
                int off = seq * chunkSize;
                int len = Math.Min(chunkSize, raw.Length - off);
                var chunk = new byte[len + ChunkHeaderSize];
                chunk[0] = ChunkMagic;
                chunk[1] = msgSeq;
                chunk[2] = (byte)seq;
                chunk[3] = (byte)total;
                Buffer.BlockCopy(raw, off, chunk, ChunkHeaderSize, len);
                p.OutQueue.Enqueue(chunk);
            }
            p.Flush(this);
        }

        /* ===================== 入站处理 ===================== */

        /// <summary>收到一个（已重组的）BLE 包字节流，解码并按中继/落地规则处理。</summary>
        private void HandleIncomingBytes(byte[] raw, string viaAddress)
        {
            if (raw == null || raw.Length == 0) return;
            BitchatPacket packet;
            try { packet = BitchatPacket.Decode(raw); }
            catch (Exception ex) { Log(L10n.T("解码失败: ") + ex.Message); return; }
            if (packet == null) return;

            // 先用包里的 SenderId 认人（不论这一包最终是落地还是中继）：
            // Android 的随机 BLE 地址会让同一台设备的两条连接看起来是两个地址，
            // 学到 peerId 后立刻合并，顶栏台数才不会翻倍。
            try { LearnPeerId(viaAddress, Hex8(packet.SenderId)); } catch { }

            var self8 = PeerIdTo8(identity.BitchatPeerId);
            bool forMe = packet.RecipientId == null || Equal8(packet.RecipientId, self8);

            if (!forMe)
            {
                // 中继（消息隧道）：不是发给自己的，按 TTL 继续向其它邻居转发。
                if (packet.Ttl > 1)
                {
                    packet.Ttl = (byte)(packet.Ttl - 1);
                    byte[] rebroadcast;
                    try { rebroadcast = packet.Encode(false, null); }
                    catch { return; }
                    var others = new List<PeerConn>();
                    lock (peerGate)
                    {
                        foreach (var kv in peers)
                            if (kv.Key != viaAddress) others.Add(kv.Value);
                    }
                    foreach (var o in others)
                    {
                        try { EnqueueChunked(o, rebroadcast); } catch { }
                    }
                    Log(L10n.T("中继 bitchat 包（TTL=") + packet.Ttl + "）→ " + others.Count + L10n.T(" 邻居"));
                }
                return;
            }

            // 落地：解析 payload
            try
            {
                var json = Encoding.UTF8.GetString(packet.Payload ?? new byte[0]);
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (obj == null) return;
                var t = MsgStr(obj, "t");
                // 身份握手包：只为互认 peerId，不产生任何聊天内容
                if (t == "hi") return;
                var c = MsgStr(obj, "c");
                var n = MsgStr(obj, "n");
                var x = MsgStr(obj, "x");
                var mid = MsgStr(obj, "mid");
                if (string.IsNullOrEmpty(mid)) mid = LocalIdentity.Hex(LocalIdentity.Sha256(raw)).Substring(0, 24);

                // 去重
                if (!MarkSeen(mid)) return;

                var fromPeer = Hex8(packet.SenderId);
                string conv;
                if (t == "group") conv = Convs.BitchatGroupPrefix + (c ?? "");
                else if (t == "dm") conv = Convs.BitchatDm(identity.BitchatPeerId, fromPeer);
                else conv = Convs.BitchatPrefix + NormalRoom(c);

                OnMessage?.Invoke(conv, fromPeer, n, x);
            }
            catch (Exception ex) { Log(L10n.T("处理 bitchat 落地消息失败: ") + ex.Message); }
        }

        /* ===================== 邻居与去重 ===================== */

        public List<Neighbor> Neighbors()
        {
            var list = new List<Neighbor>();
            lock (peerGate)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var stale = new List<string>();
                foreach (var kv in peers)
                {
                    var p = kv.Value;
                    bool connected = p.Gatt != null || p.ServerSide;
                    // 没连上又没有心跳的条目要清掉：否则关掉蓝牙/走远的设备会一直挂在
                    // 邻居表里，顶栏「蓝牙 N 台」和界面上的设备数会越看越多。
                    if (!connected && now - p.LastSeen > 5 * 60 * 1000) { stale.Add(kv.Key); continue; }

                    list.Add(new Neighbor
                    {
                        Address = kv.Key,
                        PeerId = p.PeerId,
                        Name = p.Name,
                        Rssi = p.Rssi,
                        LastSeen = p.LastSeen,
                        Connected = connected
                    });
                }
                for (int i = 0; i < stale.Count; i++) peers.Remove(stale[i]);
            }
            return list;
        }

        private bool MarkSeen(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (seenMessages.ContainsKey(id)) return false;
            seenMessages[id] = 0;
            seenOrder.Enqueue(id);
            while (seenOrder.Count > 2000) { if (seenOrder.TryDequeue(out var old)) seenMessages.TryRemove(old, out _); }
            return true;
        }

        private void UpsertPeer(string addr, PeerConn p)
        {
            lock (peerGate)
            {
                peers[addr] = p;
            }
            OnNeighborChange?.Invoke();
        }

        /// <summary>
        /// 从收到的包学对端 peerId（包里带 SenderId），学到后按 peerId 合并重复条目。
        ///
        /// 为什么必须主动学：Android 的 BLE 随机地址会让「我连它」和「它连我」看起来
        /// 是两个不同地址 → 同一台设备被算成两台（顶栏「蓝牙 2 台」而实际只有 1 台）。
        /// </summary>
        private void LearnPeerId(string addr, string peerId)
        {
            if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(peerId)) return;
            bool learned = false;
            lock (peerGate)
            {
                if (peers.TryGetValue(addr, out var p) && p != null && p.PeerId != peerId)
                {
                    p.PeerId = peerId;
                    learned = true;
                }
            }
            if (learned)
            {
                MergeByPeerId(addr, peerId);
                OnNeighborChange?.Invoke();
            }
        }

        /// <summary>连上后稍等再发握手（等服务发现 / MTU 协商 / 通知订阅完成）。</summary>
        private void ScheduleHello(string addr)
        {
            try
            {
                var h = new Handler(Looper.MainLooper);
                h.PostDelayed(() => { try { SendHelloTo(addr); } catch { } }, 700);
            }
            catch { }
        }

        /// <summary>
        /// 应用层握手：告诉对端「我是谁」，对端据此把两条重复连接合并成一台设备。
        /// t=hi 的包只用于身份互认，不会渲染成聊天消息。
        /// </summary>
        private void SendHelloTo(string addr)
        {
            try
            {
                PeerConn p;
                lock (peerGate) peers.TryGetValue(addr, out p);
                if (p == null) return;
                var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    ["t"] = "hi",
                    ["mid"] = Guid.NewGuid().ToString("N")
                }));
                var packet = BuildPacket(null, payload, null);
                EnqueueChunked(p, packet.Encode(false, null));
            }
            catch (Exception ex) { Log(L10n.T("发送握手失败: ") + ex.Message); }
        }

        /// <summary>
        /// 按 peerId 合并同一台设备：Android 每次重连会用新的随机 BLE 地址，
        /// 若不合并，关一次蓝牙再开就会被当成"新设备"，计数越滚越多。
        /// 学到 peerId 后，把同 peerId 的其它地址条目清掉（旧连接多半已失效）。
        /// </summary>
        internal void MergeByPeerId(string addr, string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            lock (peerGate)
            {
                string stale = null;
                foreach (var kv in peers)
                {
                    if (kv.Key == addr) continue;
                    if (kv.Value != null && kv.Value.PeerId == peerId) { stale = kv.Key; break; }
                }
                if (stale != null && peers.TryGetValue(stale, out var old))
                {
                    try { old.Gatt?.Close(); } catch { }
                    old.Gatt = null;
                    old.ServerSide = false;
                    peers.Remove(stale);
                }
            }
        }

        /* ===================== 工具 ===================== */

        private static string NormalRoom(string c)
        {
            if (string.IsNullOrEmpty(c)) return Convs.BitchatDefaultRoom;
            return c;
        }

        private static byte[] PeerIdTo8(string peerIdHex)
        {
            var bytes = LocalIdentity.Unhex(peerIdHex);
            if (bytes == null || bytes.Length < 8) return new byte[8];
            var r = new byte[8];
            Buffer.BlockCopy(bytes, 0, r, 0, 8);
            return r;
        }

        private static string Hex8(byte[] id)
        {
            return LocalIdentity.Hex(id);
        }

        private static bool Equal8(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != 8 || b.Length != 8) return false;
            for (int i = 0; i < 8; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string MsgStr(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return "";
            return v.ToString();
        }

        private static byte[] JsonPayload(string t, string c, string n, string x)
        {
            var obj = new Dictionary<string, object>
            {
                ["t"] = t,
                ["c"] = c ?? "",
                ["n"] = n ?? "",
                ["x"] = x ?? "",
                ["mid"] = Guid.NewGuid().ToString("N")
            };
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj));
        }

        private static BluetoothGattService BuildService()
        {
            var svc = new BluetoothGattService(ServiceUuid, GattServiceType.Primary);
            var read = new BluetoothGattCharacteristic(CharReadUuid,
                GattProperty.Read, GattPermission.Read);
            var msg = new BluetoothGattCharacteristic(CharMsgUuid,
                GattProperty.Write | GattProperty.Notify, GattPermission.Write);
            var gd = new BluetoothGattCharacteristic(CharGdUuid,
                GattProperty.Write, GattPermission.Write);
            svc.AddCharacteristic(read);
            svc.AddCharacteristic(msg);
            svc.AddCharacteristic(gd);
            return svc;
        }

        internal void Log(string m)
        {
            try { OnLog?.Invoke(m); } catch { }
            Android.Util.Log.Info("Bitchat", m);
        }

        /* ===================== 邻居结构 ===================== */

        public class Neighbor
        {
            public string Address;
            public string PeerId;
            public string Name;
            public int Rssi;
            public long LastSeen;
            public bool Connected;
        }

        /// <summary>单个邻居连接：既可能是中心侧 GATT（主动连），也可能是外设侧（被动连）。</summary>
        private class PeerConn
        {
            public string Address;
            public string PeerId;
            public string Name;
            public int Rssi;
            public long LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            public BluetoothGatt Gatt;       // 中心侧：我们主动连上对方的 GATT
            public bool ServerSide;          // 外设侧：对方连上我们的 GATT Server
            public bool Sending;             // GATT 同一时刻只允许一个未完成的写/通知（流控关键）
            public byte msgSeqCounter;
            public readonly ConcurrentQueue<byte[]> OutQueue = new ConcurrentQueue<byte[]>();
            // 分片重组缓冲：msgSeq -> (total, received count, buffers)
            public readonly Dictionary<byte, FragBuf> InFrags = new Dictionary<byte, FragBuf>();

            public byte NextMsgSeq() { msgSeqCounter++; return msgSeqCounter; }

            /// <summary>启动发送：一次只发一个分片，等 OnCharacteristicWrite / OnNotificationSent 回来再发下一个。</summary>
            public void Flush(BitchatTransport owner) { owner.SendNextChunk(this); }
        }

        internal class FragBuf
        {
            public int Total;
            public int Received;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
            public byte[] Assemble()
            {
                var ms = new System.IO.MemoryStream();
                for (int i = 0; i < Total; i++)
                {
                    if (Chunks.TryGetValue(i, out var c)) ms.Write(c, 0, c.Length);
                }
                return ms.ToArray();
            }
        }

        /* ===================== GATT Server 回调（外设角色） ===================== */

        private class GattServerCb : BluetoothGattServerCallback
        {
            private readonly BitchatTransport owner;
            public GattServerCb(BitchatTransport o) { owner = o; }

            public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
            {
                try
                {
                    if (device == null) return;
                    var addr = device.Address;
                    if (newState == ProfileState.Connected)
                    {
                        var p = owner.GetOrCreatePeer(addr);
                        p.ServerSide = true;
                        // 不要清 Gatt：我们可能同时也主动连着它（双角色），清掉会丢链路。
                        p.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        owner.UpsertPeer(addr, p);
                        owner.Log(L10n.T("bitchat 外设侧连接: ") + addr);
                        // 连上就立刻互换身份：否则同一台设备「我连它」+「它连我」会被
                        // 当成两台（各自看到的是不同的随机 BLE 地址），顶栏台数翻倍。
                        owner.ScheduleHello(addr);
                    }
                    else
                    {
                        lock (owner.peerGate)
                        {
                            if (owner.peers.TryGetValue(addr, out var p))
                            {
                                p.ServerSide = false;
                                lock (p) p.Sending = false;
                                if (p.Gatt == null) owner.peers.Remove(addr);
                            }
                        }
                        owner.OnNeighborChange?.Invoke();
                    }
                }
                catch (Exception ex) { owner.Log("server OnConnectionStateChange: " + ex.Message); }
            }

            public override void OnNotificationSent(BluetoothDevice? device, GattStatus status)
            {
                try { owner.OnNotifyComplete(device?.Address); }
                catch (Exception ex) { owner.Log("OnNotificationSent: " + ex.Message); }
            }

            public override void OnCharacteristicReadRequest(BluetoothDevice? device, int requestId, int offset, BluetoothGattCharacteristic? characteristic)
            {
                try
                {
                    if (characteristic?.Uuid.Equals(CharReadUuid) == true)
                    {
                        // 返回本机 announce（peerID + 昵称 + 公钥），让对端补全信息
                        var announce = new BitchatAnnounce
                        {
                            Nickname = owner.identity != null ? (owner.context as Activity)?.Title?.ToString() ?? "" : "",
                            NoisePublicKey = owner.identity?.NoisePublic ?? new byte[32],
                            SigningPublicKey = owner.identity?.SignPublic ?? new byte[32]
                        };
                        var payload = announce.Encode();
                        if (offset >= payload.Length) payload = new byte[0];
                        else
                        {
                            var tmp = new byte[payload.Length - offset];
                            Buffer.BlockCopy(payload, offset, tmp, 0, tmp.Length);
                            payload = tmp;
                        }
                        gattServerSafe(device, requestId, characteristic, payload);
                    }
                    else
                    {
                        gattServerSafe(device, requestId, characteristic, new byte[0]);
                    }
                }
                catch (Exception ex) { owner.Log("OnCharacteristicReadRequest: " + ex.Message); }
            }

            public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattCharacteristic? characteristic, bool preparedWrite, bool responseNeeded, int offset, byte[] value)
            {
                try
                {
                    if (characteristic?.Uuid.Equals(CharMsgUuid) == true)
                    {
                        var v = value ?? characteristic.GetValue();
                        owner.FeedChunk(device?.Address, v);
                    }
                }
                catch (Exception ex) { owner.Log("OnCharacteristicWriteRequest: " + ex.Message); }
                finally
                {
                    if (responseNeeded)
                    {
                        try { gattServerSafe(device, requestId, characteristic, new byte[0]); } catch { }
                    }
                }
            }

            private void gattServerSafe(BluetoothDevice? device, int requestId, BluetoothGattCharacteristic? characteristic, byte[] payload)
            {
                if (device == null || characteristic == null) return;
                try { owner.gattServer?.SendResponse(device, requestId, GattStatus.Success, 0, payload); }
                catch { }
            }
        }

        /* ===================== GATT Client 回调（中心角色） ===================== */

        private class GattClientCb : BluetoothGattCallback
        {
            private readonly BitchatTransport owner;
            private readonly string address;
            public GattClientCb(BitchatTransport o, string addr) { owner = o; address = addr; }

            public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
            {
                try
                {
                    if (gatt == null) return;
                    if (newState == ProfileState.Connected)
                    {
                        var p = owner.GetOrCreatePeer(address);
                        p.Gatt = gatt;
                        // 注意：不要清 ServerSide —— 两台设备可能同时互相连（双角色），
                        // 清掉会把另一条可用链路也丢掉。
                        owner.UpsertPeer(address, p);
                        try { gatt.DiscoverServices(); } catch { }
                        owner.Log(L10n.T("bitchat 中心侧连接: ") + address);
                        owner.ScheduleHello(address);
                    }
                    else
                    {
                        PeerConn p;
                        lock (owner.peerGate) owner.peers.TryGetValue(address, out p);
                        try { gatt.Close(); } catch { }
                        if (p != null)
                        {
                            lock (p) { if (p.Gatt == gatt) { p.Gatt = null; p.Sending = false; } }
                            // 只有两个方向都断了才移出邻居表
                            if (p.Gatt == null && !p.ServerSide)
                                lock (owner.peerGate) owner.peers.Remove(address);
                        }
                        owner.ScheduleReconnect(address, gatt.Device);
                        owner.OnNeighborChange?.Invoke();
                    }
                }
                catch (Exception ex) { owner.Log("client OnConnectionStateChange: " + ex.Message); }
            }

            public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
            {
                try
                {
                    // 写完成 → 放行下一片（流控）。无论成功失败都要放行，否则队列会卡死。
                    if (characteristic?.Uuid.Equals(CharMsgUuid) == true)
                        owner.OnClientWriteComplete(address);
                }
                catch (Exception ex) { owner.Log("OnCharacteristicWrite: " + ex.Message); }
            }

            public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
            {
                try
                {
                    if (gatt == null) return;
                    var svc = gatt.GetService(ServiceUuid);
                    if (svc == null) return;
                    var msgChar = svc.GetCharacteristic(CharMsgUuid);
                    if (msgChar != null)
                    {
                        try { gatt.SetCharacteristicNotification(msgChar, true); } catch { }
                        // 开启 CLIENT_CHARACTERISTIC_CONFIG 通知
                        var ccc = msgChar.GetDescriptor(Java.Util.UUID.FromString("00002902-0000-1000-8000-00805f9b34fb"));
                        if (ccc != null)
                        {
                            try { ccc.SetValue(BluetoothGattDescriptor.EnableNotificationValue.ToArray()); gatt.WriteDescriptor(ccc); } catch { }
                        }
                    }
                    // 协商更大 MTU，提升吞吐（失败不影响默认 23 字节）
                    try { gatt.RequestMtu(517); } catch { }
                    // 读对方 announce
                    var readChar = svc.GetCharacteristic(CharReadUuid);
                    if (readChar != null) { try { gatt.ReadCharacteristic(readChar); } catch { } }
                }
                catch (Exception ex) { owner.Log("OnServicesDiscovered: " + ex.Message); }
            }

            public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, byte[]? value)
            {
                try
                {
                    if (characteristic?.Uuid.Equals(CharMsgUuid) == true)
                        owner.FeedChunk(address, value ?? characteristic.GetValue());
                }
                catch (Exception ex) { owner.Log("OnCharacteristicChanged: " + ex.Message); }
            }

            public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, byte[]? value, GattStatus status)
            {
                try
                {
                    if (characteristic?.Uuid.Equals(CharReadUuid) == true)
                    {
                        var v = value ?? characteristic.GetValue();
                        var a = BitchatAnnounce.Decode(v);
                        if (a != null)
                        {
                            owner.MergeByPeerId(address, a.PeerId);
                            var p = owner.GetOrCreatePeer(address);
                            p.PeerId = a.PeerId;
                            p.Name = a.Nickname;
                            owner.UpsertPeer(address, p);
                            owner.Log(L10n.T("bitchat 邻居: ") + a.PeerId);
                        }
                    }
                }
                catch (Exception ex) { owner.Log("OnCharacteristicRead: " + ex.Message); }
            }

            // ===== Android 12 及以下走的是「无 value 参数」的旧重载（API 33+ 才用上面带 value 的版本）。
            // 只覆写 4 参版本会导致老系统上「通知/读取回调根本不触发」——这正是蓝牙连上却收发不到的原因。
            public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
            {
                try
                {
                    if (characteristic?.Uuid.Equals(CharMsgUuid) == true)
                        owner.FeedChunk(address, characteristic.GetValue());
                }
                catch (Exception ex) { owner.Log("OnCharacteristicChanged(old): " + ex.Message); }
            }

            public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
            {
                try
                {
                    if (status == GattStatus.Success && characteristic?.Uuid.Equals(CharReadUuid) == true)
                    {
                        var a = BitchatAnnounce.Decode(characteristic.GetValue());
                        if (a != null)
                        {
                            owner.MergeByPeerId(address, a.PeerId);
                            var p = owner.GetOrCreatePeer(address);
                            p.PeerId = a.PeerId;
                            p.Name = a.Nickname;
                            owner.UpsertPeer(address, p);
                            owner.Log(L10n.T("bitchat 邻居: ") + a.PeerId);
                        }
                    }
                }
                catch (Exception ex) { owner.Log("OnCharacteristicRead(old): " + ex.Message); }
            }

            public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status)
            {
                if (status == GattStatus.Success && mtu > 0) owner.mtu = mtu;
            }
        }

        /* ===================== 扫描回调 ===================== */

        private class ScanCb : ScanCallback
        {
            private readonly BitchatTransport owner;
            public ScanCb(BitchatTransport o) { owner = o; }
            public override void OnScanResult(ScanCallbackType callbackType, ScanResult result)
            {
                try
                {
                    var device = result?.Device;
                    if (device == null) return;
                    var addr = device.Address;
                    var p = owner.GetOrCreatePeer(addr);
                    p.Rssi = result?.Rssi ?? 0;
                    p.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    owner.UpsertPeer(addr, p);
                    owner.TryConnect(addr, device);
                }
                catch (Exception ex) { owner.Log("OnScanResult: " + ex.Message); }
            }
            public override void OnScanFailed(ScanFailure errorCode)
            {
                owner.Log(L10n.T("bitchat 扫描失败: ") + errorCode);
            }
        }

        /* ===================== 广播回调 ===================== */

        private class AdvCb : AdvertiseCallback
        {
            private readonly BitchatTransport owner;
            public AdvCb(BitchatTransport o) { owner = o; }
            public override void OnStartSuccess(AdvertiseSettings settingsInEffect)
            {
                owner.Log(L10n.T("bitchat 广播已启动"));
            }
            public override void OnStartFailure(AdvertiseFailure errorCode)
            {
                owner.Log(L10n.T("bitchat 广播启动失败: ") + errorCode);
            }
        }

        /* ===================== 内部连接/发送辅助 ===================== */

        private PeerConn GetOrCreatePeer(string addr)
        {
            lock (peerGate)
            {
                if (!peers.TryGetValue(addr, out var p) || p == null)
                {
                    p = new PeerConn { Address = addr };
                    peers[addr] = p;
                }
                return p;
            }
        }

        private void TryConnect(string addr, BluetoothDevice device)
        {
            if (!running) return;
            lock (peerGate)
            {
                if (peers.TryGetValue(addr, out var p) && p.Gatt != null) return;
                if (peers.Count > MaxConnections) return;
            }
            try
            {
                var cb = new GattClientCb(this, addr);
                // 显式走 LE 传输：双模设备用默认 AUTO 时偶尔会先去试 BR/EDR 而连不上。
                try { device.ConnectGatt(context, false, cb, BluetoothTransports.Le); }
                catch { device.ConnectGatt(context, false, cb); }
            }
            catch (Exception ex) { Log(L10n.T("连接失败: ") + ex.Message); }
        }

        /// <summary>断线后延迟重连（BLE 需主动重连恢复；厂商 ROM 偶尔静默断开）。</summary>
        private void ScheduleReconnect(string addr, BluetoothDevice device)
        {
            if (!running || device == null) return;
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(3000);
                        if (!running) return;
                        lock (peerGate) { if (peers.TryGetValue(addr, out var p) && p.Gatt != null) return; }
                        TryConnect(addr, device);
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>启动扫描看门狗：没有已连接邻居时定期重启扫描，避免扫描被系统静默停掉后再也发现不了人。</summary>
        private void StartScanWatchdog()
        {
            try
            {
                scanWatchdog?.Dispose();
                scanWatchdog = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (!running) return;
                        int connected;
                        lock (peerGate) connected = peers.Values.Count(x => x.Gatt != null);
                        if (connected == 0)
                        {
                            try { scanner?.StopScan(scanCb); } catch { }
                            try { StartScan(); } catch { }
                        }
                    }
                    catch { }
                }, null, 20000, 25000);
            }
            catch { }
        }

        /// <summary>收到一个 BLE 分片：重组为完整包后交给 HandleIncomingBytes。</summary>
        private void FeedChunk(string addr, byte[] value)
        {
            if (value == null || value.Length < ChunkHeaderSize) return;
            if (value[0] != ChunkMagic) return;
            byte msgSeq = value[1];
            byte seq = value[2];
            byte total = value[3];
            var body = new byte[value.Length - ChunkHeaderSize];
            Buffer.BlockCopy(value, ChunkHeaderSize, body, 0, body.Length);

            var p = GetOrCreatePeer(addr);
            FragBuf buf;
            lock (p.InFrags)
            {
                if (!p.InFrags.TryGetValue(msgSeq, out buf))
                {
                    buf = new FragBuf { Total = total };
                    p.InFrags[msgSeq] = buf;
                }
                if (!buf.Chunks.ContainsKey(seq)) { buf.Chunks[seq] = body; buf.Received++; }
            }
            if (buf.Received >= total)
            {
                var raw = buf.Assemble();
                lock (p.InFrags) p.InFrags.Remove(msgSeq);
                HandleIncomingBytes(raw, addr);
            }
        }

        /// <summary>
        /// 发送下一个分片。**流控关键**：GATT 同一连接同一时刻只允许一个未完成的写/通知，
        /// 上一条的 OnCharacteristicWrite / OnNotificationSent 回来后才发下一条。
        /// 以前一次性 blast 最多 8 片，第 2 片起就被系统以 "busy" 丢弃，导致长消息永远拼不齐。
        /// </summary>
        private void SendNextChunk(PeerConn p)
        {
            if (p == null) return;
            lock (p)
            {
                if (p.Sending) return;
                if (!p.OutQueue.TryDequeue(out var chunk) || chunk == null) return;
                p.Sending = true;
                bool ok;
                try { ok = TrySendOne(p, chunk); }
                catch (Exception ex) { ok = false; Log(L10n.T("发送分片异常: ") + ex.Message); }
                if (!ok)
                {
                    // 没发出去就放回队列（分片带 seq，顺序不影响重组），下次再试。
                    p.OutQueue.Enqueue(chunk);
                    p.Sending = false;
                }
            }
        }

        private bool TrySendOne(PeerConn p, byte[] chunk)
        {
            // 双向都连着时优先用中心侧写通道（有明确的写完成回调）。
            if (p.Gatt != null)
            {
                var svc = p.Gatt.GetService(ServiceUuid);
                var ch = svc?.GetCharacteristic(CharMsgUuid);
                if (ch == null) return false;
                ch.WriteType = GattWriteType.Default;   // 带响应，才有 OnCharacteristicWrite 可做流控
                ch.SetValue(chunk);
                return p.Gatt.WriteCharacteristic(ch);
            }
            if (p.ServerSide)
            {
                var ch = FindServerMsgChar();
                var device = FindServerDevice(p.Address);
                if (ch == null || device == null) return false;
                ch.SetValue(chunk);
                return gattServer.NotifyCharacteristicChanged(device, ch, false);
            }
            return false;
        }

        private void OnClientWriteComplete(string address)
        {
            PeerConn p;
            lock (peerGate) peers.TryGetValue(address, out p);
            if (p == null) return;
            lock (p) { p.Sending = false; }
            SendNextChunk(p);
        }

        private void OnNotifyComplete(string address)
        {
            PeerConn p;
            lock (peerGate) peers.TryGetValue(address, out p);
            if (p == null) return;
            lock (p) { p.Sending = false; }
            SendNextChunk(p);
        }

        private BluetoothGattCharacteristic FindServerMsgChar()
        {
            try
            {
                var svc = gattServer?.GetService(ServiceUuid);
                return svc?.GetCharacteristic(CharMsgUuid);
            }
            catch { return null; }
        }

        private BluetoothDevice FindServerDevice(string addr)
        {
            try { return btAdapter?.GetRemoteDevice(addr); }
            catch { return null; }
        }
    }
}

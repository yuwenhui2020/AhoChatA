using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>中间层发现到的一个 Mchat 设备。</summary>
    public class LcDevice
    {
        public string Id;          // Mchat peerId
        public string Name;        // 昵称
        public string QaulId;      // 对端 qaul PeerId（base58），用于走 qaul 渠道直聊
        public string BitchatId;   // 对端 bitchat peerID
        public string Conv;        // 在哪个频道/会话上看到的
        public string Link;        // 从哪条链路收到的（qaul / lan / p2p / bitchat / local）
        public int Distance;       // 距离（跳数）：1=直连，>1=经中继
        public long LastSeen;      // Unix 毫秒
        public List<string> Channels = new List<string>();

        public bool IsOnline(long now, int timeoutMs)
        {
            return now - LastSeen < timeoutMs;
        }
    }

    /// <summary>
    /// Mchat 中间层（MchatMidLayer）——夹在「聊天室」与「通信渠道」之间的一层。
    ///
    /// 它干两件事：
    ///
    /// 1) **隔除 qaul 通信频道的窥视**
    ///    所有经过 qaul（含中继节点、公网节点、明文洪泛的 feed）的 Mchat 载荷，
    ///    出门前都先过 LC1 加密层：有频道密码就用频道密码，没密码就用"公开发现密钥"。
    ///    结果是 qaul 侧自始至终只能拿到密文，看不到明文正文、昵称、频道名与设备清单。
    ///    qaul 只被当管道用，qaul 自己的端到端加密照旧保留（两层互不干涉）。
    ///
    /// 2) **自带设备发现（不依赖局域网探测权限）**
    ///    走 qaul 中继时拿不到局域网探测能力（没有组播/扫描权限，也不在同一网段），
    ///    所以发现不能靠 UDP 组播。中间层改为周期性广播**加密信标**（beacon）：
    ///      · 信标内容：本机 peerId、昵称、qaul/bitchat 身份、所在频道、时间戳、剩余跳数；
    ///      · 信标走"频道启用的每条链路"，其中 qaul 链路会经 libp2p 路由逐跳中继；
    ///      · 收到信标的节点登记设备，并在剩余跳数 &gt; 1 时减一继续转发（洪泛扩散，带去重）。
    ///    这样两台**没有直连**的设备，只要都连着 qaul 网络（哪怕各自在不同网段、靠中继），
    ///    也能互相发现——这正是"走中继没有局域网权限"场景下唯一可行的发现方式。
    ///
    /// 中间层只做发现与加密封装，不改变聊天室模型：频道还是那个频道，链路只影响怎么传。
    /// </summary>
    public class MchatMidLayer
    {
        /// <summary>活跃时的信标周期（前台 + 亮屏）。</summary>
        public const int BeaconIntervalMs = 10000;
        /// <summary>空闲时的信标周期（灭屏或 App 在后台）：发现可以慢，电不能白烧。</summary>
        public const int BeaconIdleIntervalMs = 60000;
        /// <summary>超过这个时间没再收到信标就认为离线。</summary>
        public const int DeviceTimeoutMs = 90000;
        /// <summary>信标初始剩余跳数（1 = 不转发，只给直连邻居）。</summary>
        public const int MaxHops = 3;
        /// <summary>转发去重记录保留时长。</summary>
        private const int RelayMemoMs = 180000;

        private const string BeaconTag = "b1";

        /* ---------- 宿主注入的钩子（由 MchatBridge 装配） ---------- */

        /// <summary>本机 Mchat peerId。</summary>
        public Func<string> SelfId;
        /// <summary>本机昵称。</summary>
        public Func<string> SelfName;
        /// <summary>本机 qaul PeerId（base58）。</summary>
        public Func<string> QaulId;
        /// <summary>本机 bitchat peerID。</summary>
        public Func<string> BitchatId;
        /// <summary>当前要广播信标的频道 conv 列表（默认频道 + 已加入频道 + 当前会话）。</summary>
        public Func<List<string>> BeaconConvs;
        /// <summary>(conv, 已加密正文) → 投递到该频道启用的所有链路。</summary>
        public Action<string, string> Send;
        /// <summary>(conv, JSON 信标) → 返回加密后的正文。</summary>
        public Func<string, string, string> SealBeacon;
        /// <summary>(conv, 密文) → 解出 JSON 信标，解不开返回 null。</summary>
        public Func<string, string, string> OpenBeacon;
        /// <summary>通知界面/网页端。</summary>
        public Action<string, object> Emit;
        /// <summary>日志。</summary>
        public Action<string> Log;

        private readonly ConcurrentDictionary<string, LcDevice> devices =
            new ConcurrentDictionary<string, LcDevice>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> relayed =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private CancellationTokenSource cts;
        private volatile bool running;
        private volatile bool idle;
        private long beaconCount;
        private long relayCount;
        /// <summary>唤醒信号：间隔变化/回到前台时立刻打断点，不必空等一个周期。</summary>
        private readonly object wakeGate = new object();
        private TaskCompletionSource<bool> wake;

        public bool Running { get { return running; } }

        /// <summary>当前信标间隔（界面展示用）。</summary>
        public int CurrentIntervalMs()
        {
            return idle ? BeaconIdleIntervalMs : BeaconIntervalMs;
        }

        /// <summary>
        /// 切换空闲/活跃。灭屏或 App 进后台时由宿主调用，信标周期从 10s 拉长到 60s。
        /// 回到活跃时唤醒等待中的循环并立刻补发一帧，把"重新被发现"的延迟压回去。
        /// </summary>
        public void SetIdle(bool value)
        {
            if (idle == value) return;
            idle = value;
            Log_(L10n.T("信标周期 → ") + CurrentIntervalMs() + "ms");

            // 唤醒 sleep 中的循环，让它按新间隔重新计时
            try { lock (wakeGate) { wake?.TrySetResult(true); } } catch { }
            if (!value)
            {
                // 回到前台：立刻补一帧，别干等一个周期
                try { Task.Run(() => { try { BeaconNow(); } catch { } }); } catch { }
            }
        }

        private void Log_(string s)
        {
            try { Android.Util.Log.Info("LcMid", s); } catch { }
            try { if (Log != null) Log(s); } catch { }
        }

        /* ===================== 生命周期 ===================== */

        public void Start(CancellationToken token)
        {
            if (running) return;
            running = true;
            cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (wakeGate) { wake = new TaskCompletionSource<bool>(); }

            Task.Run(async () =>
            {
                // 启动后先立刻广播一帧，缩短被发现的时间
                try { BeaconNow(); } catch { }
                while (!cts.IsCancellationRequested)
                {
                    await WaitNextTick().ConfigureAwait(false);
                    if (cts.IsCancellationRequested) break;
                    try { BeaconNow(); } catch { }
                    try { Prune(); } catch { }
                }
            });

            Log_(L10n.T("中间层已启动：信标周期 ") + CurrentIntervalMs() + L10n.T("ms（空闲 ") + BeaconIdleIntervalMs +
                 L10n.T("ms），跳数上限 ") + MaxHops);
        }

        /// <summary>
        /// 等到下一个发送时刻。间隔随电源状态变化，且能被 SetIdle 立刻唤醒，
        /// 这样"回到前台"不用空等整整一个 60 秒周期。
        /// </summary>
        private async Task WaitNextTick()
        {
            TaskCompletionSource<bool> w;
            lock (wakeGate) { w = wake; }
            if (w == null) { await Task.Delay(CurrentIntervalMs()).ConfigureAwait(false); return; }

            var tick = Task.Delay(CurrentIntervalMs());
            try { await Task.WhenAny(tick, w.Task).ConfigureAwait(false); }
            catch { await Task.Delay(1000).ConfigureAwait(false); }

            // 被唤醒就换一个新的信号源
            if (w.Task.IsCompleted) lock (wakeGate) { wake = new TaskCompletionSource<bool>(); }
        }

        public void Stop()
        {
            running = false;
            try { cts?.Cancel(); } catch { }
            try { lock (wakeGate) { wake?.TrySetResult(true); } } catch { }
            cts = null;
        }

        /* ===================== 信标 ===================== */

        /// <summary>立刻在所有需要广播的频道上发一帧信标。</summary>
        public void BeaconNow()
        {
            if (Send == null || SealBeacon == null) return;
            var self = SelfId == null ? null : SelfId();
            if (string.IsNullOrEmpty(self)) return;

            var convs = new List<string>();
            try { if (BeaconConvs != null) convs = BeaconConvs() ?? new List<string>(); } catch { }
            if (convs.Count == 0) convs.Add(Convs.Chan(ChannelRegistry.DefaultChannelName));

            var payload = new Dictionary<string, object>
            {
                ["lc"] = BeaconTag,
                ["id"] = self,
                ["n"] = SelfName == null ? "" : (SelfName() ?? ""),
                ["q"] = QaulId == null ? "" : (QaulId() ?? ""),
                ["b"] = BitchatId == null ? "" : (BitchatId() ?? ""),
                ["h"] = MaxHops,
                ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["c"] = convs.Select(Convs.ChanName).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList()
            };
            var json = JsonConvert.SerializeObject(payload);

            foreach (var conv in convs.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var armored = SealBeacon(conv, json);
                    if (string.IsNullOrEmpty(armored)) continue;
                    Send(conv, armored);
                    beaconCount++;
                }
                catch (Exception ex)
                {
                    Log_(L10n.T("信标发送失败 ") + conv + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 入站：判断一条已解密的载荷是不是信标。
        /// 返回 true = 是信标且已消化（登记设备 + 按需中继），调用方不要再当聊天消息处理。
        /// </summary>
        public bool Handle(string conv, string link, string payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson)) return false;

            Dictionary<string, object> o;
            try { o = JsonConvert.DeserializeObject<Dictionary<string, object>>(payloadJson); }
            catch { return false; }
            if (o == null) return false;

            object tag;
            if (!o.TryGetValue("lc", out tag) || tag == null) return false;
            if (!string.Equals(tag.ToString(), BeaconTag, StringComparison.Ordinal)) return false;

            var id = Str(o, "id");
            if (string.IsNullOrEmpty(id)) return false;

            var self = SelfId == null ? null : SelfId();
            if (!string.IsNullOrEmpty(self) && string.Equals(id, self, StringComparison.Ordinal)) return true; // 自己的回声

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ts = Num(o, "t", now);
            var hops = (int)Num(o, "h", 1);

            // 登记设备（同 id 只保留最新一条，但把见过的频道合并起来）
            var dev = new LcDevice
            {
                Id = id,
                Name = Str(o, "n"),
                QaulId = Str(o, "q"),
                BitchatId = Str(o, "b"),
                Conv = conv,
                Link = string.IsNullOrEmpty(link) ? "?" : link,
                Distance = Math.Max(1, MaxHops - hops + 1),
                LastSeen = now
            };
            var chans = o["c"] as Newtonsoft.Json.Linq.JArray;
            if (chans != null)
                foreach (var c in chans) if (c != null) dev.Channels.Add(c.ToString());

            devices.AddOrUpdate(id, dev, (k, old) =>
            {
                // 更近的（跳数更小的）记录优先，昵称/身份取最新的非空值
                if (dev.Distance < old.Distance)
                {
                    dev.Channels = Merge(old.Channels, dev.Channels);
                    return dev;
                }
                if (string.IsNullOrEmpty(old.Name)) old.Name = dev.Name;
                if (string.IsNullOrEmpty(old.QaulId)) old.QaulId = dev.QaulId;
                if (string.IsNullOrEmpty(old.BitchatId)) old.BitchatId = dev.BitchatId;
                old.LastSeen = now;
                old.Channels = Merge(old.Channels, dev.Channels);
                return old;
            });

            // 中继扩散：跳数还有富余就减一转发，让不直连的设备也能被发现（去重防环）
            var memoKey = id + "|" + ts;
            if (hops > 1 && Send != null && SealBeacon != null)
            {
                long prev;
                if (!relayed.TryGetValue(memoKey, out prev) || now - prev > RelayMemoMs)
                {
                    if (relayed.TryAdd(memoKey, now) || (relayed.TryGetValue(memoKey, out prev) && now - prev > RelayMemoMs))
                    {
                        try
                        {
                            o["h"] = hops - 1;
                            var fwd = SealBeacon(conv, JsonConvert.SerializeObject(o));
                            if (!string.IsNullOrEmpty(fwd))
                            {
                                Send(conv, fwd);
                                relayCount++;
                            }
                        }
                        catch (Exception ex) { Log_(L10n.T("信标中继失败: ") + ex.Message); }
                    }
                }
            }
            else
            {
                relayed[memoKey] = now;
            }

            Notify();
            return true;
        }

        private static List<string> Merge(List<string> a, List<string> b)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (a != null) foreach (var x in a) if (!string.IsNullOrEmpty(x)) set.Add(x);
            if (b != null) foreach (var x in b) if (!string.IsNullOrEmpty(x)) set.Add(x);
            return set.ToList();
        }

        /* ===================== 设备表 ===================== */

        private void Prune()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool changed = false;

            foreach (var kv in devices)
            {
                if (now - kv.Value.LastSeen > DeviceTimeoutMs)
                {
                    LcDevice d;
                    if (devices.TryRemove(kv.Key, out d)) changed = true;
                }
            }
            foreach (var kv in relayed.ToList())
            {
                if (now - kv.Value > RelayMemoMs) relayed.TryRemove(kv.Key, out _);
            }
            if (changed) Notify();
        }

        private long lastNotify;
        private string lastNotifySig;

        /// <summary>
        /// 通知界面设备表有变化。
        /// 信标是周期性的，还会在多跳中继里重复出现——若每收到一帧就 Emit，
        /// WebView 就要被反复拉起来跑 JS。这里做两层收敛：
        /// 内容没变不发；内容变了也至少隔 2 秒（灭屏时 10 秒）再发。
        /// </summary>
        private void Notify()
        {
            try
            {
                if (Emit == null) return;

                string sig;
                var devs = Devices();
                lock (devices)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append(devs.Count);
                    foreach (var d in devs) sb.Append('|').Append(d.Id).Append(d.Distance).Append(d.Link);
                    sig = sb.ToString();
                }
                if (sig == lastNotifySig) return;

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int minGap = idle ? 10000 : 2000;
                if (now - lastNotify < minGap) return;

                lastNotify = now;
                lastNotifySig = sig;
                Emit("lcdevices", DeviceList());
            }
            catch { }
        }

        /// <summary>在线设备列表（按昵称排序，不含自己）。</summary>
        public List<LcDevice> Devices()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var self = SelfId == null ? null : SelfId();
            return devices.Values
                .Where(d => d != null && !string.Equals(d.Id, self, StringComparison.Ordinal))
                .Where(d => now - d.LastSeen < DeviceTimeoutMs)
                .OrderBy(d => string.IsNullOrEmpty(d.Name) ? d.Id : d.Name)
                .ToList();
        }

        /// <summary>给界面/网页端的设备列表。</summary>
        public List<object> DeviceList()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Devices().Select(d => (object)new
            {
                id = d.Id,
                name = d.Name,
                qaul = d.QaulId,
                bitchat = d.BitchatId,
                conv = d.Conv,
                link = d.Link,
                distance = d.Distance,
                relay = d.Distance > 1,
                age = now - d.LastSeen,
                channels = d.Channels
            }).ToList();
        }

        public LcDevice Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            LcDevice d;
            return devices.TryGetValue(id, out d) ? d : null;
        }

        /// <summary>中间层状态（界面展示 "中间层：已启用 / 发现 N 台"）。</summary>
        public object Status()
        {
            var devs = Devices();
            return new
            {
                running,
                beaconInterval = BeaconIntervalMs,
                maxHops = MaxHops,
                beacons = beaconCount,
                relayed = relayCount,
                devices = devs.Count,
                direct = devs.Count(d => d.Distance <= 1),
                viaRelay = devs.Count(d => d.Distance > 1),
                timeout = DeviceTimeoutMs / 1000
            };
        }

        /* ===================== 小工具 ===================== */

        private static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        private static long Num(Dictionary<string, object> o, string key, long def)
        {
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt64(v); }
            catch { return def; }
        }
    }
}

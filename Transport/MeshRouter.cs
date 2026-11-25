using System;
using System.Collections.Generic;
using System.Linq;

namespace AhoChatA
{
    /// <summary>一条链路的运行统计（给「通信渠道设置」页做健康度展示，也用于中继决策）。</summary>
    public sealed class MeshLinkStat
    {
        public long Rx;          // 收到多少帧
        public long Tx;          // 发出多少帧
        public long RelayIn;     // 从别的链路中转进来多少帧
        public long RelayOut;    // 中转出去多少帧
        public long Drop;        // 因为重复/超过跳数/陈旧被丢弃
        public long LastRxMs;    // 最近一次收（Unix ms）
        public long LastTxMs;
        public string LastError = "";

        /* ---- 链路质量（参考 qaul 的 RoutingConnectionEntry：rtt / lq / last_update） ---- */

        /// <summary>平滑往返时延（毫秒）。取 qaul 的 alpha=1/8 指数平滑，见 MeshRouter.NoteSuccess。</summary>
        public double RttMs;
        /// <summary>连续失败次数。清零条件只有「一次成功」。</summary>
        public int FailStreak;
        /// <summary>熔断到期时间（Unix ms）：在此之前不再往这条链路发，避免对着死链路重试拖慢整队。</summary>
        public long CircuitUntil;
        /// <summary>成功发出的帧数（含重传成功）。</summary>
        public long Sent;
        /// <summary>因失败被重新排队的次数。</summary>
        public long Retrans;
        /// <summary>超过存活时间仍发不出去而丢弃的条数。</summary>
        public long Expired;
        /// <summary>因为限速（令牌桶空了）被推迟的次数。</summary>
        public long RateDefer;

        /// <summary>令牌桶余量（内部用）。</summary>
        internal double Tokens;
        internal long TokensAtMs;
    }

    /// <summary>
    /// 多链路网格路由（Mesh）。
    ///
    /// 背景：本 App 有四条互相独立的传输链路 ——
    ///   qaul（Wi-Fi/局域网/以太网，可经公共节点跨网）、WLAN Direct（Wi-Fi P2P）、
    ///   蓝牙 BLE（bitchat）、局域网 TCP（Mchat 老协议，电脑端在用）。
    ///
    /// 目标：任何一条链路收到的消息，都能中继到本机其它**正在运行**的链路，
    /// 于是「A—蓝牙—B—Wi-Fi P2P—C，D—qaul—B」这样的四台设备可以同时聊天。
    ///
    /// 参考 qaul 的做法：
    ///   · `router/flooder.rs`：每条消息带唯一 id（去重）、incoming_via 排除法、跳数封顶；
    ///   · `router/neighbours.rs`：RTT 用 alpha=1/8 指数平滑，避免单次抖动就换路；
    ///   · `router/table.rs`：按 lq（链路质量）挑最好的一条路，条目超时即淘汰
    ///     （且**跳数越多过期越快**，远路由一定先死，防止旧路由长期占位）；
    ///   · `services/messaging/retransmit.rs`：发出去没确认的要重传，超时彻底丢弃。
    ///
    /// 本类负责策略与记账（跳数、限速、熔断、质量指标）；
    /// 真正的收发在 MchatBridge（它知道会话/加密/附件），排队重传在 MeshOutbox。
    /// </summary>
    public sealed class MeshRouter
    {
        /// <summary>最多中转几跳。4 跳足够覆盖「蓝牙 + P2P + qaul」的典型拓扑，又不至于把网刷爆。</summary>
        public const int MaxHop = 4;

        /// <summary>连续失败多少次就熔断这条链路。</summary>
        private const int CircuitThreshold = 3;
        /// <summary>熔断基础时长；连续失败越多翻倍越长，封顶 2 分钟。</summary>
        private const long CircuitBaseMs = 30000;
        private const long CircuitMaxMs = 120000;

        private readonly object gate = new object();
        private readonly Dictionary<string, MeshLinkStat> stats = new Dictionary<string, MeshLinkStat>();

        public MeshLinkStat Stat(string link)
        {
            if (string.IsNullOrEmpty(link)) link = "?";
            lock (gate)
            {
                MeshLinkStat s;
                if (!stats.TryGetValue(link, out s))
                {
                    s = new MeshLinkStat();
                    stats[link] = s;
                }
                return s;
            }
        }

        /// <summary>各链路统计快照（供「Mesh 统计」页/排障展示）。</summary>
        public List<KeyValuePair<string, MeshLinkStat>> Snapshot()
        {
            lock (gate) return stats.ToList();
        }

        /* ===================== 记账 ===================== */

        public void NoteRx(string link, int bytes)
        {
            var s = Stat(link);
            lock (gate) { s.Rx++; s.LastRxMs = NowMs(); }
            _ = bytes;
        }

        public void NoteTx(string link, int bytes)
        {
            var s = Stat(link);
            lock (gate) { s.Tx++; s.LastTxMs = NowMs(); }
            _ = bytes;
        }

        public void NoteRelayIn(string link)
        {
            var s = Stat(link);
            lock (gate) { s.RelayIn++; }
        }

        public void NoteRelayOut(string link)
        {
            var s = Stat(link);
            lock (gate) { s.RelayOut++; }
        }

        public void NoteDrop(string link, string why)
        {
            var s = Stat(link);
            lock (gate) { s.Drop++; if (!string.IsNullOrEmpty(why)) s.LastError = why; }
        }

        public void NoteError(string link, string err)
        {
            var s = Stat(link);
            lock (gate) { if (!string.IsNullOrEmpty(err)) s.LastError = err; }
        }

        public void NoteReset(string link)
        {
            var s = Stat(link);
            lock (gate)
            {
                s.Rx = s.Tx = s.RelayIn = s.RelayOut = s.Drop = 0;
                s.Sent = s.Retrans = s.Expired = s.RateDefer = 0;
                s.FailStreak = 0; s.CircuitUntil = 0; s.RttMs = 0;
                s.LastError = "";
            }
        }

        /* ===================== 链路质量：RTT 平滑 + 熔断 ===================== */

        /// <summary>
        /// 一次发送成功。RTT 按 qaul 的算法做指数平滑：
        ///   new = (old * 7 + rtt) / 8
        /// 单次抖动不会把估算值带偏（qaul 用同一公式给路由表算 lq）。
        /// 同时清零失败计数并解除熔断。
        /// </summary>
        public void NoteSuccess(string link, int rttMs)
        {
            var s = Stat(link);
            lock (gate)
            {
                if (rttMs < 0) rttMs = 0;
                if (rttMs > 60000) rttMs = 60000;
                s.RttMs = s.RttMs <= 0 ? rttMs : (s.RttMs * 7 + rttMs) / 8.0;
                s.Sent++;
                s.FailStreak = 0;
                s.CircuitUntil = 0;
                s.LastTxMs = NowMs();
            }
        }

        /// <summary>
        /// 一次发送失败。连续失败到阈值就熔断这条链路一段时间，
        /// 后续报文直接跳过它（排队等），避免「死链路把整队拖慢」。
        /// </summary>
        public void NoteFail(string link, string err)
        {
            var s = Stat(link);
            lock (gate)
            {
                if (!string.IsNullOrEmpty(err)) s.LastError = err;
                s.FailStreak++;
                if (s.FailStreak >= CircuitThreshold)
                {
                    int over = s.FailStreak - CircuitThreshold;
                    long wait = CircuitBaseMs << Math.Min(over, 2);   // 30s → 60s → 120s
                    if (wait > CircuitMaxMs) wait = CircuitMaxMs;
                    s.CircuitUntil = NowMs() + wait;
                }
            }
        }

        /// <summary>这条链路现在能不能发（熔断期内返回 false；不该把它算作新失败）。</summary>
        public bool IsUsable(string link)
        {
            var s = Stat(link);
            lock (gate)
            {
                return s.CircuitUntil <= NowMs();
            }
        }

        /// <summary>熔断剩余秒数（0 = 正常）。</summary>
        public int CircuitSeconds(string link)
        {
            var s = Stat(link);
            lock (gate)
            {
                long left = s.CircuitUntil - NowMs();
                return left <= 0 ? 0 : (int)((left + 999) / 1000);
            }
        }

        public void NoteRetrans(string link)
        {
            var s = Stat(link);
            lock (gate) { s.Retrans++; }
        }

        public void NoteExpired(string link)
        {
            var s = Stat(link);
            lock (gate) { s.Expired++; }
        }

        /* ===================== 限速（令牌桶） ===================== */

        /// <summary>
        /// 每条链路的吞吐上限（帧/秒）。蓝牙是共享的窄带，必须压得比有线低一个量级；
        /// 局域网/qaul/P2P 是可用带宽大的通道，给宽一点但也要有上限 ——
        /// 中继环路收敛慢的时候，没有限速会把对端和本机的线程池一起打满。
        /// </summary>
        private static double RateOf(string link)
        {
            switch (link)
            {
                case ChanLink.Bitchat: return 6;
                case ChanLink.P2p: return 40;
                default: return 60;
            }
        }

        /// <summary>取一个发送令牌。取不到说明这一瞬间超速，调用方应当稍后再试（不算失败）。</summary>
        public bool TryAcquire(string link)
        {
            var s = Stat(link);
            lock (gate)
            {
                long now = NowMs();
                double rate = RateOf(link);
                if (s.TokensAtMs <= 0)
                {
                    s.Tokens = rate;          // 初始给一秒的量，避免第一批发不出去
                    s.TokensAtMs = now;
                }
                else
                {
                    long dt = now - s.TokensAtMs;
                    if (dt > 0)
                    {
                        s.Tokens += rate * dt / 1000.0;
                        double cap = rate * 2;
                        if (s.Tokens > cap) s.Tokens = cap;
                        s.TokensAtMs = now;
                    }
                }
                if (s.Tokens < 1)
                {
                    s.RateDefer++;
                    return false;
                }
                s.Tokens -= 1;
                return true;
            }
        }

        /* ===================== 中继预算（防网络风暴 / 泛洪放大） ===================== */

        // 链路级令牌桶（TryAcquire）只限制"单条链路"的发送速率；
        // 稠密网格里一次收包会向多条其它链路各转一份，是"跳数 × 链路数"的乘法放大。
        // 这里再压两道闸：全局中继总量 + 单来源中继速率，避免把整条网刷爆。
        private const double RelayRatePerSec = 40;         // 全链路合计每秒最多转发多少条
        private const double PerOriginRatePerSec = 12;     // 单个来源 peer 每秒最多被转发多少条
        private const int RelaySeenMax = 2000;             // "已转发 id" 记忆上限（FIFO 淘汰）
        private const long RelaySeenTtlMs = 120000;

        private double relayTokens;
        private long relayTokensAt;
        private readonly Dictionary<string, double> originTokens = new Dictionary<string, double>();
        private readonly Dictionary<string, long> originAt = new Dictionary<string, long>();
        private readonly Dictionary<string, long> relayedIds = new Dictionary<string, long>();
        private readonly Queue<string> relayedOrder = new Queue<string>();

        /// <summary>
        /// 取一个"中继许可"。全局桶或来源桶任一空 → 拒绝本次转发（网络风暴时就地收敛）。
        /// origin 为空（自己发的/无来源）时只受全局桶约束。
        /// </summary>
        public bool AllowRelay(string origin)
        {
            lock (gate)
            {
                long now = NowMs();

                // 全局桶
                if (relayTokensAt <= 0) { relayTokens = RelayRatePerSec; relayTokensAt = now; }
                else
                {
                    long dt = now - relayTokensAt;
                    if (dt > 0)
                    {
                        relayTokens += RelayRatePerSec * dt / 1000.0;
                        double cap = RelayRatePerSec * 2;
                        if (relayTokens > cap) relayTokens = cap;
                        relayTokensAt = now;
                    }
                }
                if (relayTokens < 1) return false;

                // 来源桶
                if (!string.IsNullOrEmpty(origin))
                {
                    double tok;
                    long at;
                    if (!originTokens.TryGetValue(origin, out tok)) { tok = PerOriginRatePerSec; at = now; }
                    else
                    {
                        at = originAt.TryGetValue(origin, out var a) ? a : now;
                        long dt2 = now - at;
                        if (dt2 > 0)
                        {
                            tok += PerOriginRatePerSec * dt2 / 1000.0;
                            double cap2 = PerOriginRatePerSec * 2;
                            if (tok > cap2) tok = cap2;
                            at = now;
                        }
                    }
                    if (tok < 1) return false;
                    tok -= 1;
                    originTokens[origin] = tok;
                    originAt[origin] = at;
                    // 来源表别无限涨：超过 512 个来源时清掉最旧的一半
                    if (originTokens.Count > 512)
                    {
                        foreach (var k in originAt.OrderBy(kv => kv.Value).Take(256).Select(kv => kv.Key).ToList())
                        {
                            originAt.Remove(k);
                            originTokens.Remove(k);
                        }
                    }
                }

                relayTokens -= 1;
                return true;
            }
        }

        /// <summary>
        /// 标记"这条 id 我已经转发过"。返回 false = 已转过（不再转，保证整网每个节点对同一 id 最多转发一次）。
        /// 相比 seenMessages（会因 TTL/上限被淘汰），这里是独立的二次保险，防止旧 id 被反复转发形成环路风暴。
        /// </summary>
        public bool TryMarkRelayed(string id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            lock (gate)
            {
                if (relayedIds.ContainsKey(id)) return false;
                relayedIds[id] = NowMs();
                relayedOrder.Enqueue(id);
                long now = NowMs();
                while (relayedOrder.Count > RelaySeenMax)
                {
                    var old = relayedOrder.Dequeue();
                    relayedIds.Remove(old);
                }
                // 偶尔按时间清理一下（避免长期运行内存膨胀）
                if (relayedOrder.Count % 200 == 0)
                {
                    while (relayedOrder.Count > 0 && now - (relayedIds.TryGetValue(relayedOrder.Peek(), out var t) ? t : 0) > RelaySeenTtlMs)
                        relayedIds.Remove(relayedOrder.Dequeue());
                }
                return true;
            }
        }

        /* ===================== 中继策略 ===================== */

        /// <summary>消息当前跳数（0 = 本机原创或对端原创）。</summary>
        public int HopOf(IDictionary<string, object> msg)
        {
            if (msg == null) return 0;
            object v;
            if (!msg.TryGetValue("hop", out v) || v == null) return 0;
            long n;
            if (v is long) n = (long)v;
            else if (v is int) n = (int)v;
            else if (!long.TryParse(v.ToString(), out n)) return 0;
            if (n < 0) return 0;
            return n > 255 ? 255 : (int)n;
        }

        /// <summary>还能不能再往外中转一跳。</summary>
        public bool CanRelay(IDictionary<string, object> msg)
        {
            return HopOf(msg) < MaxHop;
        }

        /// <summary>中转给别的链路时用的跳数副本（不改原消息，避免本地记录被写上 hop）。</summary>
        public int NextHop(IDictionary<string, object> msg)
        {
            int h = HopOf(msg) + 1;
            return h > 255 ? 255 : h;
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}

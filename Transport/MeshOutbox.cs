using System;
using System.Collections.Generic;
using System.Threading;

namespace AhoChatA
{
    /// <summary>单条链路的发送结果。</summary>
    public enum SendOutcome
    {
        /// <summary>已交给链路。</summary>
        Sent,
        /// <summary>
        /// 链路在跑、但当前没有可用对端（例如频道里还没人上线）。
        /// 这**不是失败**：报文要留着等对端出现，而且不能据此熔断链路。
        /// </summary>
        NoPeer,
        /// <summary>真失败（连接被拒 / 传输层异常）。</summary>
        Failed
    }

    /// <summary>一条待发出的网格报文。</summary>
    public sealed class MeshOutItem
    {
        public string Id;
        public string Conv;
        /// <summary>已经封好的线上 JSON（正文是密文，队列里不碰明文）。</summary>
        public string Payload;
        /// <summary>qaul 链路用的密文正文（= Payload 里 text 字段的值）。</summary>
        public string Wire;
        /// <summary>线上副本（正文已是密文），qaul 需要里面的 id / time。</summary>
        public Dictionary<string, object> Msg;
        /// <summary>qaul 频道索引（发送端分配，重传时必须沿用，否则对端会当成新消息）。</summary>
        public uint? ChanIdx;
        public byte[] Body;
        /// <summary>还没送达的链路；发成功的会被移出，空了就算送达。</summary>
        public List<string> Links = new List<string>();
        public long CreatedAt;
        public long NextTryAt;
        public int Attempts;
        public string LastError = "";
        /// <summary>true = 中继转发（存活时间更短，避免旧消息在网里绕圈）。</summary>
        public bool Relay;
        /// <summary>是否遇到过**真失败**（区别于「暂时没对端」）。过期时用它决定要不要报红。</summary>
        public bool SawFailure;
    }

    /// <summary>
    /// 出站队列 + 重传（参考 qaul 的 `services/messaging/retransmit.rs` 与 DTN 托管）。
    ///
    /// 为什么需要它：以前每个链路投递都是一次 `Task.Run`，发失败只写一行日志，
    /// 于是「对端刚好在重连」「蓝牙这一瞬间拥塞」这类瞬时故障会把消息**永久丢掉**，
    /// 用户看到的就是「明明发出去了，对面没收到」。
    ///
    /// qaul 的做法是：消息先落「未确认表」，定时重传，确认后删除，超过存活时间才丢。
    /// 这里取它的骨架，但按网格聊天的场景调整：
    ///   · **单工作线程顺序发** —— 不再每条消息各起一个线程，避免瞬时高峰把线程池打满；
    ///   · **指数退避重传** —— 1s / 3s / 7s / 15s，最多 4 次，60 秒还没送到就丢（中继 30 秒）；
    ///   · **三态结果** —— 「没有对端」不算失败（不熔断），只有真异常才累加失败计数；
    ///   · **令牌桶限速** —— 由 MeshRouter 按链路类型给令牌，蓝牙被压到 6 帧/秒。
    /// </summary>
    public sealed class MeshOutbox
    {
        /// <summary>最多重试几次（首次发送之后）。</summary>
        public const int MaxAttempts = 4;
        /// <summary>普通消息在队列里最多躺多久。</summary>
        public const long TtlMs = 60000;
        /// <summary>中继消息的存活时间：短一些，旧消息没必要在网里反复绕。</summary>
        public const long RelayTtlMs = 30000;
        /// <summary>队列上限。超了就丢最老的，宁可丢也不让内存无限涨。</summary>
        private const int MaxQueue = 300;
        private const int IdleSleepMs = 120;

        private readonly object gate = new object();
        private readonly List<MeshOutItem> queue = new List<MeshOutItem>();
        private readonly HashSet<string> activeIds = new HashSet<string>();

        private Thread worker;
        private volatile bool running;
        private readonly MeshRouter router;

        /// <summary>真正的单链路发送动作，由 MchatBridge 注入（它知道加密/附件/会话）。</summary>
        public Func<MeshOutItem, string, SendOutcome> Send;

        private long enqueued, sent, retrans, expired, dropped, done;

        public MeshOutbox(MeshRouter router)
        {
            this.router = router;
        }

        public void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "mesh-outbox";
            try { worker.Start(); } catch (Exception ex) { Android.Util.Log.Warn("Mesh", "出站队列启动失败: " + ex.Message); }
        }

        public void Stop()
        {
            running = false;
            try { if (worker != null) worker.Join(800); } catch { }
            worker = null;
            lock (gate) { queue.Clear(); activeIds.Clear(); }
        }

        /* ===================== 入队 ===================== */

        /// <summary>
        /// 入队。同一个消息 id 已经在队列里就不重复排队 ——
        /// 否则「本机发送 + 中继回环」会让同一条消息在队列里出现两份、各发一遍。
        /// </summary>
        public bool Enqueue(MeshOutItem item)
        {
            if (item == null || item.Links == null || item.Links.Count == 0) return false;
            if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            long now = NowMs();
            item.CreatedAt = now;
            item.NextTryAt = now;

            lock (gate)
            {
                if (activeIds.Contains(item.Id)) return false;

                // 队列满了：丢最老的（通常是已经反复重试不成功的），给新消息腾位置
                while (queue.Count >= MaxQueue)
                {
                    var oldest = queue[0];
                    queue.RemoveAt(0);
                    activeIds.Remove(oldest.Id);
                    dropped++;
                    if (router != null) router.NoteDrop(oldest.Links.Count > 0 ? oldest.Links[0] : "?", "队列已满");
                }

                queue.Add(item);
                activeIds.Add(item.Id);
                enqueued++;
            }
            return true;
        }

        /* ===================== 统计 ===================== */

        public int Pending { get { lock (gate) { return queue.Count; } } }
        public long Enqueued { get { return Interlocked.Read(ref enqueued); } }
        public long SentCount { get { return Interlocked.Read(ref sent); } }
        public long RetransCount { get { return Interlocked.Read(ref retrans); } }
        public long ExpiredCount { get { return Interlocked.Read(ref expired); } }
        public long DroppedCount { get { return Interlocked.Read(ref dropped); } }
        public long DoneCount { get { return Interlocked.Read(ref done); } }

        /* ===================== 工作线程 ===================== */

        private void Loop()
        {
            while (running)
            {
                MeshOutItem item = null;
                try { item = Take(); }
                catch (Exception ex) { Android.Util.Log.Warn("Mesh", "出站队列取任务失败: " + ex.Message); }

                if (item == null)
                {
                    try { Thread.Sleep(IdleSleepMs); } catch { }
                    continue;
                }
                try { Process(item); }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mesh", "出站队列发送异常: " + ex.Message);
                    Requeue(item, true);
                }
            }
        }

        /// <summary>取出「最早该发」的一条；顺手把彻底过期的清掉。</summary>
        private MeshOutItem Take()
        {
            long now = NowMs();
            MeshOutItem pick = null;
            lock (gate)
            {
                // ① 清理过期
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    var it = queue[i];
                    if (now - it.CreatedAt > (it.Relay ? RelayTtlMs : TtlMs))
                    {
                        queue.RemoveAt(i);
                        activeIds.Remove(it.Id);
                        expired++;
                        if (router != null) router.NoteExpired(it.Links.Count > 0 ? it.Links[0] : "?");
                    }
                }

                // ② 挑一条到点的
                long bestAt = long.MaxValue;
                for (int i = 0; i < queue.Count; i++)
                {
                    var it = queue[i];
                    if (it.NextTryAt > now) continue;
                    if (it.NextTryAt < bestAt) { bestAt = it.NextTryAt; pick = it; }
                }
                if (pick != null)
                {
                    queue.Remove(pick);
                    activeIds.Remove(pick.Id);
                }
            }
            return pick;
        }

        private void Process(MeshOutItem item)
        {
            var keep = new List<string>();
            bool anySent = false;

            for (int i = 0; i < item.Links.Count; i++)
            {
                string link = item.Links[i];

                // 熔断期内：留着等它恢复，且不计失败（否则熔断永远解不开）
                if (router != null && !router.IsUsable(link)) { keep.Add(link); continue; }
                // 超速：稍后再试，同样不计失败
                if (router != null && !router.TryAcquire(link)) { keep.Add(link); continue; }

                long t0 = NowMs();
                SendOutcome outcome;
                string err = "";
                try
                {
                    outcome = Send != null ? Send(item, link) : SendOutcome.Failed;
                    if (outcome == SendOutcome.Failed) err = "链路返回失败";
                }
                catch (Exception ex)
                {
                    outcome = SendOutcome.Failed;
                    err = ex.Message;
                }

                if (outcome == SendOutcome.Sent)
                {
                    anySent = true;
                    Interlocked.Increment(ref sent);
                    if (router != null) router.NoteSuccess(link, (int)(NowMs() - t0));
                }
                else
                {
                    keep.Add(link);
                    item.LastError = err;
                    if (outcome == SendOutcome.NoPeer)
                    {
                        // 没有对端不是故障：不写「最近错误」（那是红色的，会误导排障），
                        // 只在统计里体现为「重传」，报文继续排队等对端出现。
                        item.LastError = "";
                    }
                    else if (router != null)
                    {
                        item.SawFailure = true;
                        router.NoteFail(link, err);
                    }
                }
            }

            _ = anySent;

            // 只要有一条链路真正送达，就认为这条消息已经送出去了。
            // 以前要求「所有链路都成功」，qaul 的握手/加密一出问题，即便局域网早就送达，
            // 报文也会一直留在队列里反复重发 —— 既让发送端越来越卡，也会造成重复投递。
            if (keep.Count == 0 || anySent) { Interlocked.Increment(ref done); return; }

            item.Links = keep;
            item.Attempts++;

            long now = NowMs();
            long ttl = item.Relay ? RelayTtlMs : TtlMs;
            if (item.Attempts > MaxAttempts || now - item.CreatedAt > ttl)
            {
                Interlocked.Increment(ref expired);
                if (item.SawFailure)
                {
                    // 真失败才记成「丢包」，否则链路正常只是对方不在，报红会把人误导到错误方向
                    if (router != null) router.NoteDrop(keep[0], "重传 " + item.Attempts + " 次仍未送达");
                }
                else if (router != null)
                {
                    // 只是没对端：状态行保持干净，排障日志里留一句就够
                    router.NoteExpired(keep[0]);
                }
                Android.Util.Log.Info("Mesh", "报文过期丢弃：id=" + item.Id
                    + L10n.T(" 链路=") + string.Join("/", keep.ToArray())
                    + L10n.T(" 尝试=") + item.Attempts
                    + (item.SawFailure ? L10n.T(" 原因=") + (item.LastError ?? "") : L10n.T(" 原因=暂无对端")));
                return;
            }

            item.NextTryAt = now + BackoffMs(item.Attempts);

            bool ok = false;
            lock (gate)
            {
                if (queue.Count < MaxQueue && !activeIds.Contains(item.Id))
                {
                    queue.Add(item);
                    activeIds.Add(item.Id);
                    ok = true;
                }
            }
            if (ok)
            {
                Interlocked.Increment(ref retrans);
                if (router != null) router.NoteRetrans(keep[0]);
            }
            else
            {
                Interlocked.Increment(ref dropped);
            }
        }

        private void Requeue(MeshOutItem item, bool countAsFail)
        {
            if (item == null) return;
            item.Attempts++;
            if (item.Attempts > MaxAttempts)
            {
                Interlocked.Increment(ref expired);
                return;
            }
            item.NextTryAt = NowMs() + BackoffMs(item.Attempts);
            lock (gate)
            {
                if (queue.Count < MaxQueue && !activeIds.Contains(item.Id))
                {
                    queue.Add(item);
                    activeIds.Add(item.Id);
                    Interlocked.Increment(ref retrans);
                }
            }
            _ = countAsFail;
        }

        /// <summary>退避：1s → 3s → 7s → 15s（qaul 是固定 3 秒起步，这里按尝试次数递增）。</summary>
        private static long BackoffMs(int attempt)
        {
            if (attempt <= 1) return 1000;
            long v = 1000L << Math.Min(attempt - 1, 5);
            return v > 20000 ? 20000 : v;
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}

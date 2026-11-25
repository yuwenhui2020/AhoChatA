// ============================================================
// 「同步听」跨设备时钟同步（NTP / Cristian 四时间戳）
//
// 参考 NTPv4 on-wire 算法与主流 watch-party 的 server-authoritative timeline：
//   T1 客户端发出请求时的本地时间
//   T2 服务端收到请求时的服务端时间
//   T3 服务端发回响应时的服务端时间
//   T4 客户端收到响应时的本地时间
//
//   offset θ = ((T2-T1) + (T3-T4)) / 2    // 服务端时钟 - 本地时钟
//   delay  δ = (T4-T1) - (T3-T2)          // 往返时延
//
// 实践要点（来自 NTP 的 clock filter）：样本按 RTT 排序、取**最小 RTT** 那一组，
// 因为排队只会让 RTT 变大，最小样本最接近真实传播时延；同时保留最近 8 组防止时钟漂移后
// 一直用陈旧样本。offset 用来把「发起端写的 ts」换算成「本机此刻」。
//
// 两条采样通道，谁先到用谁（同一套 AddSample）：
//   ① mesh 瞬时帧 syncping/syncpong：与 musiccard 走同一条信道，测到的 RTT 最贴近卡片时延；
//   ② HTTP GET <ltHost>/api/sync/time：网关直出 t2/t3，链路不通时也能给网页端用。
//
// 本文件不碰播放器；SyncListen 用 HostNow() / OneWayMs 把卡片锚点折算成目标进度。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.OS;
using Newtonsoft.Json;

namespace AhoChatA
{
    public static class SyncClock
    {
        public const string KindPing = "syncping";
        public const string KindPong = "syncpong";

        private const int SampleWindow = 8;
        private const int ProbeMs = 2000;
        private const int FollowerTtlMs = 30000;
        private const int HttpTimeoutMs = 1200;
        private const long MaxRttMs = 5000;
        private const long MaxOffsetMs = 24L * 60 * 60 * 1000;

        private sealed class Sample
        {
            public long Offset, Rtt, At;
        }

        private static readonly object _lock = new object();
        private static readonly List<Sample> _samples = new List<Sample>();
        private static readonly Dictionary<long, long> _pending = new Dictionary<long, long>();
        private static long _offset, _rtt, _lastFollowerAt;
        private static int _seq;
        private static string _host = "", _base = "";
        private static bool _running;
        private static Handler _h;

        public static long UtcNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public static long Elapsed => SystemClock.ElapsedRealtime();

        /// <summary>发起端时钟 - 本机时钟（毫秒）。拿到有效样本前为 0。</summary>
        public static long Offset => _offset;
        public static long RttMs => _rtt;
        public static long OneWayMs => _rtt > 0 ? Math.Max(1, _rtt / 2) : 0;
        public static bool Valid => _rtt > 0;

        /// <summary>本机此刻对应的「发起端墙上时间」。</summary>
        public static long HostNow => UtcNow + _offset;

        /// <summary>本机是否作为播放端、最近 30 秒内有人在对时（用于加快 patch 频率）。</summary>
        public static bool HasFollowers
        {
            get { return _lastFollowerAt > 0 && UtcNow - _lastFollowerAt < FollowerTtlMs; }
        }

        private static Handler H
        {
            get
            {
                if (_h == null) _h = new Handler(Looper.MainLooper);
                return _h;
            }
        }

        public static string StatusJson()
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    valid = Valid,
                    offset = _offset,
                    rtt = _rtt,
                    host = _host,
                    samples = _samples.Count
                });
            }
            catch { return "{\"valid\":false}"; }
        }

        /// <summary>开始对某个发起端采样；重复调用只更新目标，不重置已有样本。</summary>
        public static void Ensure(string hostPeer, string hostBase)
        {
            try
            {
                string np = hostPeer ?? "";
                string nb = string.IsNullOrEmpty(hostBase) ? "" : hostBase.TrimEnd('/');
                // 换了发起端就丢弃旧样本：offset 是相对某一台设备算的，混用会直接算错进度
                if (!string.Equals(np, _host, StringComparison.Ordinal)
                    || !string.Equals(nb, _base, StringComparison.Ordinal))
                {
                    lock (_lock)
                    {
                        _samples.Clear();
                        _pending.Clear();
                        _offset = 0;
                        _rtt = 0;
                    }
                }
                _host = np;
                _base = nb;
                _running = true;
                H.RemoveCallbacksAndMessages(null);
                H.Post(Probe);
            }
            catch { }
        }

        public static void Stop()
        {
            _running = false;
            try { H.RemoveCallbacksAndMessages(null); } catch { }
            lock (_lock)
            {
                _samples.Clear();
                _pending.Clear();
                _offset = 0;
                _rtt = 0;
            }
            _host = _base = "";
            _lastFollowerAt = 0;
        }

        private static void Probe()
        {
            if (!_running) return;
            try
            {
                if (!string.IsNullOrEmpty(_host)) SendPing();
                if (!string.IsNullOrEmpty(_base)) Task.Run(() => HttpSample(_base));
            }
            catch { }
            if (_running)
            {
                try { H.PostDelayed(Probe, ProbeMs); } catch { }
            }
        }

        /* ---------------- mesh 四时间戳 ---------------- */

        private static void SendPing()
        {
            try
            {
                var br = MchatBridge.Current;
                if (br == null) return;
                long q = ++_seq;
                long t1 = UtcNow;
                lock (_lock)
                {
                    _pending[q] = t1;
                    if (_pending.Count > 32)
                    {
                        var old = new List<long>();
                        foreach (var kv in _pending) if (UtcNow - kv.Value > 15000) old.Add(kv.Key);
                        foreach (var k in old) _pending.Remove(k);
                    }
                }
                var d = new Dictionary<string, object>
                {
                    ["q"] = q,
                    ["t1"] = t1,
                    ["to"] = _host
                };
                br.SyncClockSend(KindPing, d);
            }
            catch { }
        }

        /// <summary>发起端收到对时请求：立刻回 t2/t3，并记住「有人正在跟随」。</summary>
        public static void OnPing(Dictionary<string, object> msg)
        {
            if (msg == null) return;
            try
            {
                var br = MchatBridge.Current;
                if (br == null) return;
                string from = Msg.Str(msg, "from", "");
                if (string.IsNullOrEmpty(from)) return;
                if (string.Equals(from, br.SelfPeerId, StringComparison.OrdinalIgnoreCase)) return;

                string to = Msg.Str(msg, "to", "");
                if (!string.IsNullOrEmpty(to)
                    && !string.Equals(to, br.SelfPeerId, StringComparison.OrdinalIgnoreCase)) return;

                long t2 = UtcNow;
                var d = new Dictionary<string, object>
                {
                    ["q"] = Msg.Num(msg, "q", 0),
                    ["t1"] = Msg.Num(msg, "t1", 0),
                    ["t2"] = t2,
                    ["t3"] = UtcNow,
                    ["to"] = from
                };
                NoteFollower();
                br.SyncClockSend(KindPong, d);
            }
            catch { }
        }

        /// <summary>跟随端收到应答：补齐 T4，算 offset / RTT。</summary>
        public static void OnPong(Dictionary<string, object> msg)
        {
            if (msg == null) return;
            try
            {
                string from = Msg.Str(msg, "from", "");
                if (!string.IsNullOrEmpty(_host)
                    && !string.Equals(from, _host, StringComparison.OrdinalIgnoreCase)) return;
                string to = Msg.Str(msg, "to", "");
                var self = MchatBridge.Current?.SelfPeerId;
                if (!string.IsNullOrEmpty(to) && !string.IsNullOrEmpty(self)
                    && !string.Equals(to, self, StringComparison.OrdinalIgnoreCase)) return;

                long q = Msg.Num(msg, "q", 0);
                long t1 = Msg.Num(msg, "t1", 0);
                long t2 = Msg.Num(msg, "t2", 0);
                long t3 = Msg.Num(msg, "t3", 0);
                long t4 = UtcNow;
                if (t1 <= 0 || t2 <= 0 || t3 <= 0) return;

                lock (_lock)
                {
                    if (!_pending.ContainsKey(q)) return;   // 迟到 / 重复应答
                    _pending.Remove(q);
                }
                AddSample(t1, t2, t3, t4);
            }
            catch { }
        }

        /// <summary>本机作为播放端：有人来取时间（HTTP 或 ping），短期内加密进度 patch。</summary>
        public static void NoteFollower() { _lastFollowerAt = UtcNow; }

        /* ---------------- HTTP 四时间戳（网关 /api/sync/time 直出） ---------------- */

        private static void HttpSample(string hostBase)
        {
            long t1 = UtcNow;
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(hostBase + "/api/sync/time");
                req.Method = "GET";
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                req.KeepAlive = false;
                req.Proxy = null;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                {
                    string raw = sr.ReadToEnd();
                    long t4 = UtcNow;
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    var d = JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
                    if (d == null) return;
                    long t2 = d.ContainsKey("t2") ? Convert.ToInt64(d["t2"]) : 0;
                    long t3 = d.ContainsKey("t3") ? Convert.ToInt64(d["t3"]) : 0;
                    if (t2 <= 0 || t3 <= 0) return;
                    AddSample(t1, t2, t3, t4);
                }
            }
            catch { }
        }

        /// <summary>NTP 样本滤波：最小 RTT 样本胜出，窗口 8 组。</summary>
        public static void AddSample(long t1, long t2, long t3, long t4)
        {
            long rtt = (t4 - t1) - (t3 - t2);
            if (rtt < 0 || rtt > MaxRttMs) return;
            long off = ((t2 - t1) + (t3 - t4)) / 2;
            if (Math.Abs(off) > MaxOffsetMs) return;

            lock (_lock)
            {
                _samples.Add(new Sample { Offset = off, Rtt = rtt, At = UtcNow });
                while (_samples.Count > SampleWindow) _samples.RemoveAt(0);
                Sample best = null;
                foreach (var s in _samples)
                    if (best == null || s.Rtt < best.Rtt) best = s;
                if (best != null) { _offset = best.Offset; _rtt = best.Rtt; }
            }
        }
    }
}

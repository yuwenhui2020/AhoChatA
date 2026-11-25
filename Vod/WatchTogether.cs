// ============================================================
// 「一起看」状态镜像（宿主机权威）
//
// 与音乐「同步听」同样的思路，但视频卡片不走 mesh patch 帧：
//   · 分享卡片同时带「外源 CDN 真实地址」与「本机代理地址」，
//     点「观看」各播各的，点「一起看」只连宿主机代理；
//   · 宿主机（App 播放器或网页播放器）把当前进度 Publish 到这里，
//     局域网跟随端短轮询 Snapshot()，按 ts/seq 做时间戳校验后闭环跟随。
//
// 时间戳统一由本机服务端打（UTC ms），跟随端用同一个 /api/sync/time
// 做 NTP 对时，因此不会受两端系统时钟偏差影响；超过 OnlineTtlMs
// 没有新帧即视为宿主机离线 —— 这正是「只有本机在线才能一起看」。
// ============================================================
using System;

namespace AhoChatA.Vod
{
    public static class WatchTogether
    {
        /// <summary>宿主机连续 15 秒没有上报即判定离线。</summary>
        public const long OnlineTtlMs = 15000;

        public class State
        {
            public string CardId = "";
            public string VKey = "";
            public string Title = "";
            public string Poster = "";
            public string WatchHost = "";
            public string WatchUrl = "";
            public string EpisodeName = "";
            public int EpisodeIndex = 0;
            public long PositionMs = 0;
            public long DurationMs = 0;
            public bool Playing = false;
            /// <summary>由服务端覆盖写入，客户端传值仅作兼容占位。</summary>
            public long Ts = 0;
            public long Seq = 0;
        }

        private static readonly object _lock = new object();
        private static State _s = new State();
        private static long _seq = 0;

        public static long NowMs
        {
            get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        }

        /// <summary>宿主机上报一帧，返回会话 id（为空时自动生成）。</summary>
        public static string Publish(State s)
        {
            if (s == null) return "";
            string id;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(s.CardId)) s.CardId = Guid.NewGuid().ToString("N");
                // 时间戳校验：旧帧/重放帧不得把已追上的进度拉回去（容差 1 秒）
                if (_s.CardId == s.CardId && s.Ts > 0 && s.Ts < _s.Ts - 1000) return s.CardId;
                s.Seq = ++_seq;
                s.Ts = NowMs;
                _s = s;
                id = s.CardId;
            }
            // 播放期持续维持 3 天过期策略（内部 10 分钟节流，几乎无开销）
            try { VodConfig.SweepDiskCache(); } catch { }
            return id;
        }

        public static void Stop(string cardId)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(cardId) || _s.CardId == cardId) _s = new State();
            }
        }

        /// <summary>跟随端拉取的状态镜像；所有地址都是宿主机代理，不暴露外源 CDN。</summary>
        public static object Snapshot()
        {
            State s;
            lock (_lock) { s = _s; }
            long now = NowMs;
            bool online = !string.IsNullOrEmpty(s.CardId) && s.Ts > 0 && (now - s.Ts) <= OnlineTtlMs;
            return new
            {
                ok = true,
                online = online,
                cardId = s.CardId,
                vkey = s.VKey,
                title = s.Title,
                poster = s.Poster,
                watchHost = s.WatchHost,
                watchUrl = s.WatchUrl,
                episodeName = s.EpisodeName,
                episodeIndex = s.EpisodeIndex,
                position = s.PositionMs,
                duration = s.DurationMs,
                playing = s.Playing,
                ts = s.Ts,
                seq = s.Seq,
                serverTime = now
            };
        }
    }
}

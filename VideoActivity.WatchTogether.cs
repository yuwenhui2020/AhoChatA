// ============================================================
// VideoActivity 的「一起看」分支
//
//   宿主机：分享卡片后每 500ms 把 ExoPlayer 的权威进度写进 WatchTogether 状态镜像；
//   跟随端：点卡片「一起看」进来，轮询宿主机 /api/media/vod/watchtogether，
//           用 ts/seq 做时间戳校验（旧帧 / 重放帧丢弃），换算锚点后闭环校正进度。
//
// 视频源始终是宿主机的代理地址，因此跟随端复用宿主机已加载的详情/直链/海报/清单缓存。
// ============================================================
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using Android.Widget;
using Newtonsoft.Json.Linq;
using AhoChatA.Vod;

namespace AhoChatA
{
    public partial class VideoActivity
    {
        public const string ExtraWatchCard = "watch_card";
        public const string ExtraWatchHost = "watch_host";

        private string _watchPublishCard = "";
        private bool _watchFollowInited;
        private string _watchFollowCard = "", _watchFollowHost = "", _watchFollowUrl = "";
        private long _watchFollowTs = 0, _watchFollowSeq = 0;
        private long _watchAnchorPos = 0, _watchAnchorAt = 0, _watchAnchorAge = 0, _watchAnchorDur = 0;
        private bool _watchAnchorPlaying = false;
        private int _watchEpisodeIndex = -1;
        private string _watchVKey = "";
        private long _watchLastHardSeek = 0;
        private int _watchPolling = 0, _watchPollTick = 0;
        private bool _watchOfflineNotified = false, _watchAnchorReady = false;

        private void WatchTick()
        {
            try { PublishWatchFrame(); } catch { }
            if (!_watchFollowInited) { _watchFollowInited = true; InitWatchFollow(); }
            if (string.IsNullOrEmpty(_watchFollowCard)) return;
            if (++_watchPollTick >= 2) { _watchPollTick = 0; WatchPoll(); }
            try { WatchApply(); } catch { }
        }

        /* ---------------- 宿主机上报 ---------------- */

        private void PublishWatchFrame()
        {
            if (string.IsNullOrEmpty(_watchPublishCard)) return;
            var s = (_index >= 0 && _index < _queue.Count) ? _queue[_index] : null;
            // 队列里的非直链源是 http://127.0.0.1:6789/api/media/vod/stream?u=...
            // 先拆回外源地址，否则跟随端会拿到双重代理（SafeRemote 直接 400）
            string path = VideoShare.UnwrapLocalStream(s?.Path ?? "");
            bool isHttp = path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            string poster = "";
            try
            {
                if (!string.IsNullOrEmpty(_vodKey))
                {
                    var kv = _vodKey.Split('|');
                    var d = VodCache.LoadDetail(kv[0], kv.Length > 1 ? kv[1] : "");
                    if (d != null) poster = d.Pic ?? "";
                }
            }
            catch { }

            WatchTogether.Publish(new WatchTogether.State
            {
                CardId = _watchPublishCard,
                VKey = _vodKey,
                Title = s?.Title ?? "",
                Poster = poster,
                WatchHost = ListenTogether.HostBase(),
                WatchUrl = isHttp
                    ? VodApi.StreamUrl(path, "", false)
                    : "/api/media/stream?path=" + Uri.EscapeDataString(path),
                EpisodeName = s?.Title ?? "",
                EpisodeIndex = _index < 0 ? 0 : _index,
                PositionMs = _exo.PositionMs,
                DurationMs = _exo.DurationMs,
                Playing = _exo.IsPlaying,
                Ts = WatchTogether.NowMs
            });
        }

        /* ---------------- 跟随端 ---------------- */

        private void InitWatchFollow()
        {
            _watchFollowCard = Intent?.GetStringExtra(ExtraWatchCard) ?? "";
            _watchFollowHost = Intent?.GetStringExtra(ExtraWatchHost) ?? "";
            _watchFollowUrl = Intent?.GetStringExtra(ExtraPath) ?? "";
            if (string.IsNullOrEmpty(_watchFollowCard) || string.IsNullOrEmpty(_watchFollowHost)) return;
            try { SyncClock.Ensure(_watchFollowCard, _watchFollowHost); } catch { }
            L10n.Toast(this, L10n.T("已加入一起看，正在对齐宿主机进度…"), ToastLength.Short).Show();
            WatchPoll();
        }

        private void WatchPoll()
        {
            if (string.IsNullOrEmpty(_watchFollowCard)) return;
            if (Interlocked.Exchange(ref _watchPolling, 1) != 0) return;
            string host = _watchFollowHost;
            Task.Run(() =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(host.TrimEnd('/') + "/api/media/vod/watchtogether");
                    req.Method = "GET";
                    req.Timeout = 1800;
                    req.ReadWriteTimeout = 1800;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream()))
                    {
                        var o = JObject.Parse(sr.ReadToEnd());
                        RunOnUiThread(() => { try { WatchAbsorb(o); } catch { } });
                    }
                }
                catch { }
                finally { Interlocked.Exchange(ref _watchPolling, 0); }
            });
        }

        private void WatchAbsorb(JObject o)
        {
            if (JS(o, "cardId") != "" && JS(o, "cardId") != _watchFollowCard)
            {
                _watchAnchorPlaying = false;
                if (!_watchOfflineNotified)
                {
                    _watchOfflineNotified = true;
                    L10n.Toast(this, L10n.T("该一起看会话已结束"), ToastLength.Short).Show();
                }
                return;
            }

            if (!JB(o, "online"))
            {
                if (!_watchOfflineNotified)
                {
                    _watchOfflineNotified = true;
                    L10n.Toast(this, L10n.T("发起端不在线，一起看已暂停"), ToastLength.Short).Show();
                }
                _watchAnchorPlaying = false;
                return;
            }
            _watchOfflineNotified = false;

            // ── 时间戳校验：局域网/qaul 双渠道的迟到帧、重放帧一律丢弃 ──
            long ts = JL(o, "ts"), seq = JL(o, "seq");
            if (ts > 0 && _watchFollowTs > 0 && ts < _watchFollowTs - 1000) return;
            if (seq > 0 && _watchFollowSeq > 0 && seq < _watchFollowSeq && ts <= _watchFollowTs) return;
            _watchFollowTs = ts; _watchFollowSeq = seq;

            string vkey = JS(o, "vkey");
            int ep = (int)JL(o, "episodeIndex");
            string rel = JS(o, "watchUrl");
            string newUrl = rel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? rel : _watchFollowHost.TrimEnd('/') + (rel.StartsWith("/") ? rel : "/" + rel);

            // 锚点年龄：优先用服务端同一时钟的 serverTime - ts，避免两端墙上时钟偏差；
            // 已对时则用 SyncClock.HostNow，与「同步听」完全一致。
            long age = JL(o, "serverTime") - ts;
            if (SyncClock.Valid) age = SyncClock.HostNow - ts;
            if (age < 0) age = 0;
            if (age > 15000) age = 15000;

            long pos = JL(o, "position");
            bool playing = JB(o, "playing");
            long dur = JL(o, "duration");
            _watchAnchorPos = pos;
            _watchAnchorAge = age;
            _watchAnchorAt = SyncClock.Elapsed;
            _watchAnchorPlaying = playing;
            _watchAnchorDur = dur;
            _watchAnchorReady = true;

            // 首帧只建立基线：播放器已按卡片里的宿主机代理地址起播，不要重复装载
            if (_watchEpisodeIndex < 0)
            {
                _watchVKey = vkey; _watchEpisodeIndex = ep; _watchFollowUrl = newUrl;
            }
            // 切集 / 换源：跟随宿主机重新装载（视频源仍是宿主机代理）
            else if ((vkey != _watchVKey || ep != _watchEpisodeIndex || newUrl != _watchFollowUrl)
                && !string.IsNullOrEmpty(rel))
            {
                _watchVKey = vkey; _watchEpisodeIndex = ep; _watchFollowUrl = newUrl;
                _currentUrl = newUrl;
                try { _exo.Load(newUrl, pos + (playing ? age : 0), null); } catch { }
            }
        }

        private void WatchApply()
        {
            if (!_watchAnchorReady) return;   // 首个状态帧到达前不要抢先暂停/seek
            long d = SyncClock.Elapsed - _watchAnchorAt;
            if (d < 0 || d > 6L * 60 * 60 * 1000) d = 0;
            long want = _watchAnchorPos + (_watchAnchorPlaying ? _watchAnchorAge + d : 0);
            if (_watchAnchorDur > 0 && want > _watchAnchorDur) want = _watchAnchorDur;
            if (want < 0) want = 0;

            try
            {
                if (_watchAnchorPlaying && !_exo.IsPlaying) _exo.Start();
                else if (!_watchAnchorPlaying && _exo.IsPlaying) { _exo.Pause(); _exo.SetSpeed(1f); return; }
            }
            catch { }

            long cur = _exo.PositionMs;
            long drift = want - cur;
            long now = SyncClock.Elapsed;
            if (Math.Abs(drift) > 1500 && now - _watchLastHardSeek > 1200)
            {
                _watchLastHardSeek = now;
                _exo.SetSpeed(1f);
                _exo.SeekTo(want);
            }
            else if (Math.Abs(drift) > 120)
            {
                float rate = (float)Math.Max(0.94, Math.Min(1.06, 1 + drift / 6000.0));
                _exo.SetSpeed(rate);
            }
            else _exo.SetSpeed(1f);
        }

        private static long JL(JObject o, string k) { try { var t = o[k]; return t == null ? 0 : (long)t; } catch { return 0; } }
        private static string JS(JObject o, string k) { try { var t = o[k]; return t == null ? "" : (string)t; } catch { return ""; } }
        private static bool JB(JObject o, string k) { try { var t = o[k]; return t != null && (bool)t; } catch { return false; } }
    }
}

// ============================================================
// 「同步听」：与「一起听」并列的第二种共享模式。
//
//   一起听(Playlist)：只共享同一个歌单，对端加载后各播各的，可自由切歌/拖进度；
//   同步听(Sync)    ：同一时刻只听同一首歌，发起端是唯一权威时钟。
//
// ── 时间同步（2026-11-07 重构，参考 NTPv4 / watch-party 权威时间轴）──────────
// 旧实现把「收到卡片的本机 ElapsedRealtime」当锚点，等于丢掉了整段单程时延，
// 还隐含假设两台设备墙上时钟一致，于是一开始就慢半拍；4 秒一次的 patch +
// 1 秒一次的校正又把这个偏差放大成肉眼可见的延时。
//
// 新实现：
//   ① SyncClock 用四时间戳求 offset/RTT（最小 RTT 样本胜出），把发起端写的 ts
//      换算成本机时间；
//   ② 每张卡片是一个**锚点** (position, ts, playing)。目标进度 =
//      position + (发起端此刻 - ts) + (本机单调时钟自收到帧起走过的毫秒)，
//      自动补偿「生成→投递→收到」的时延与两机时钟差；
//   ③ 250ms 一次闭环：|drift|<120ms 不动，120~1500ms 用 ±6% 变速渐进追上，
//      >1500ms 才 hard seek（带冷却），避免每秒一拖造成卡顿；
//   ④ 没有对时样本时退化为「收到时刻 + 估计单程时延」的相对时间轴，仍可正常工作。
//
// ── 切歌 / 歌词 / 卡死 ────────────────────────────────────────────────
//   · 曲目按 songKey（列表项 k）定位，ltIndex 只作兜底，杜绝「只切了动作却放了别的歌」；
//   · 跟随端禁止自动续播与「准备失败跳下一首」，一切等发起端卡片；
//   · Prepared 那一刻才回调取目标进度，网络流缓冲几秒也不会 seek 到过期位置；
//   · 切歌即把卡片里属于本曲的完整 LRC 写入当前 Song 并刷新歌词 UI，
//     歌词滚动用 DisplayPosition（权威时间轴）而不是会突跳的本地 Position。
// ============================================================
using System;
using System.Collections.Generic;
using Android.OS;
using Newtonsoft.Json;
using AhoChatA.Music;

namespace AhoChatA
{
    public static class SyncListen
    {
        public enum Mode { Off = 0, Playlist = 1, Sync = 2 }

        private const int TickMs = 250;              // 闭环校正周期
        private const int DeadbandMs = 120;          // 小于它不校正
        private const int HardSeekMs = 1500;         // 大于它才硬 seek
        private const int HardSeekCooldownMs = 1200;
        private const int MaxRetries = 3;
        private const long MaxAgeMs = 15000;         // 时钟异常时的锚点年龄上限

        private static Mode _mode = Mode.Off;
        private static string _cardId = "", _host = "", _key = "", _playlist = "";
        private static string _ltHost = "", _peerHost = "", _clockTarget = "", _clockBase = "";
        // 最近一张卡片带的发起端可达地址：点「一起听」时桥只传 from，不带整帧状态，
        // 靠这里缓存一份，仍能修掉回环地址导致的「缓冲中…」。
        private static string _lastLtHost = "", _lastPeerHost = "";
        // 本机作为「发起端」广播一起听歌单的公告，与「跟随端」的 _mode 互相独立。
        // MusicCast 每发一张卡片（含 patch）都会经 ListenTogether.Enrich 刷新一次；
        // 长时间不再广播就自动失效，避免关掉播放后网页端还显示「共享中」。
        private const long HostShareTtlMs = 120000;
        private static readonly object _hostLock = new object();
        private static bool _hostShare;
        private static string _hostPlaylist = "";
        private static int _hostIndex;
        private static long _hostAt;
        // 实时队列快照：App 单纯播放音乐（从没广播过音乐卡片）时也要能被网页端发现。
        // BuildPlaylist 会逐曲读歌词，不能每次 GET 都重算，缓存 5 秒。
        private const long LivePlaylistTtlMs = 5000;
        private static readonly object _liveLock = new object();
        private static string _livePlaylist = "";
        private static string _liveSig = "";
        private static int _liveIndex;
        private static long _liveAt;

        private static int _index = -1, _dur, _displayPos;
        private static long _anchorTs = -1, _anchorSeq = -1;
        private static long _anchorElapsed, _anchorHostNowAtRecv;
        private static int _anchorPos;
        private static bool _anchorPlaying;
        private static long _lastHardSeekAt, _lastResumeAt;
        private static string _lrcFetchedKey = "";
        private static int _retries;
        private static Handler _h;
        private static bool _ticking;

        public static Mode Current => _mode;
        public static bool InSync => _mode == Mode.Sync;
        public static string CardId => _cardId;

        /// <summary>
        /// 供歌词 / 进度 UI 使用的「发起端权威进度」。
        /// 本地 Position 在 hard seek 时会突跳，歌词跟着它会闪；用这个不会。
        /// </summary>
        public static int DisplayPosition => InSync ? _displayPos : MusicPlayer.Position;

        public static bool IsSyncing(string cardId)
        {
            return InSync && (string.IsNullOrEmpty(cardId) || string.IsNullOrEmpty(_cardId)
                              || string.Equals(cardId, _cardId, StringComparison.Ordinal));
        }

        public static string StatusJson()
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    mode = (int)_mode,
                    cardId = _cardId,
                    host = _host,
                    index = _index,
                    clock = SyncClock.Valid,
                    offset = SyncClock.Offset,
                    rtt = SyncClock.RttMs,
                    position = _displayPos
                });
            }
            catch { return "{\"mode\":0}"; }
        }

        /// <summary>
        /// 给网页端「共享听」用的状态镜像（GET /api/media/synclisten）。覆盖三种身份：
        ///   · 本机是发起端（MusicCast 正广播带歌单的音乐卡片）→ mode=1, role=host；
        ///   · 本机是一起听跟随端                            → mode=1, role=follow；
        ///   · 本机是同步听跟随端                            → mode=2, role=follow。
        /// 旧实现只认 _mode（跟随态），而发起端广播卡片时 _mode 一直是 Off，
        /// 于是网页端永远拿到 mode=0，点「同步听」也加不进 App 发起的一起听。
        /// 资源地址统一相对化，浏览器无论用 LAN IP、localhost 还是 adb forward 访问都同源可用。
        /// </summary>
        public static string WebStatus()
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string hp = "";
                int hi = 0;
                bool host = _mode == Mode.Off && ListenTogether.Enabled
                            && TryHostShare(now, out hp, out hi);
                // 「没关一起听」且本机确实有曲目在播：即使从没广播过音乐卡片、或距上次
                // 广播已超过 120 秒，也要让 /media 网页端能检测到播放状态并加入。
                // 旧实现只认 NoteHostShare 公告，App 单纯听歌时网页端永远拿到 mode=0。
                if (!host && _mode == Mode.Off && ListenTogether.Enabled
                    && MusicPlayer.Current != null
                    && (MusicPlayer.IsPlaying || MusicPlayer.Position > 0))
                {
                    hp = LivePlaylist(now, out hi);
                    host = !string.IsNullOrEmpty(hp) && hp != "[]";
                }
                int mode = _mode == Mode.Sync ? 2
                         : (_mode == Mode.Playlist || host) ? 1 : 0;

                if (mode == 0)
                    return JsonConvert.SerializeObject(new
                    {
                        mode = 0, role = "", sharing = false, serverTime = now,
                        position = 0, playing = false, playlist = ""
                    });

                string role = host ? "host" : "follow";
                var s = MusicPlayer.Current;
                string streamUrl = "", coverUrl = "", lyricUrl = "", lrc = "", key = "", name = "", artist = "";
                int dur = _dur, pos = _displayPos;
                int index = host ? hi : _index;
                bool playing = _anchorPlaying;
                string hostId = host ? ListenTogether.HostBase() : _host;
                string playlist = _mode == Mode.Playlist ? _playlist : (host ? hp : "");

                if (s != null)
                {
                    streamUrl = LocalApiUrl(ListenTogether.OnlineStreamUrl(s));
                    coverUrl = LocalApiUrl(ListenTogether.CoverUrl(s));
                    lyricUrl = LocalApiUrl(ListenTogether.LyricUrl(s));
                    lrc = ListenTogether.LyricText(s);
                    key = ListenTogether.SongKeyOf(s);
                    name = s.DisplayTitle;
                    artist = s.ArtistLine;
                    if (s.DurationMs > 0) dur = s.DurationMs;
                    // 一起听没有严格时间锚：直接反映本机真实播放态与进度
                    if (mode != 2)
                    {
                        playing = MusicPlayer.IsPlaying;
                        pos = MusicPlayer.Position;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    mode = mode,
                    role = role,
                    sharing = true,
                    index = index,
                    songKey = key,
                    name = name,
                    artist = artist,
                    duration = dur,
                    position = pos,
                    playing = playing,
                    serverTime = now,
                    host = hostId,
                    streamUrl = streamUrl,
                    coverUrl = coverUrl,
                    lyricUrl = lyricUrl,
                    lrc = lrc,
                    playlist = playlist
                });
            }
            catch { return "{\"mode\":0,\"role\":\"\",\"playlist\":\"\"}"; }
        }

        /// <summary>
        /// 把本机网关的绝对地址换成同源相对路径。网页端可能经 LAN IP、localhost
        /// 或 adb forward 访问，绝对 LAN 地址会被 appProxy 误判为跨源而送进
        /// /api/media/remote，再被 SSRF 防护挡掉。远程封面等外部 URL 原样保留，
        /// 仍由 appProxy 走二次代理。
        /// </summary>
        private static string LocalApiUrl(string u)
        {
            if (string.IsNullOrEmpty(u) || u.StartsWith("/")) return u;
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u;
            int i = u.IndexOf("/api/media/", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? u.Substring(i) : u;
        }

        /// <summary>
        /// 由 ListenTogether.Enrich 在广播（含 patch）音乐卡片时调用：把「本机正在发起
        /// 一起听」登记进状态镜像，供网页端发现。只有拿得出歌单才公告，
        /// 避免把普通播放误报成共享。
        /// </summary>
        public static void NoteHostShare(string playlist, int index)
        {
            if (_mode == Mode.Sync) return;   // 同步听跟随者不对外发起
            if (string.IsNullOrWhiteSpace(playlist) || playlist == "[]") return;
            lock (_hostLock)
            {
                _hostShare = true;
                _hostPlaylist = playlist;
                _hostIndex = index;
                _hostAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

        public static void ClearHostShare()
        {
            lock (_hostLock)
            {
                _hostShare = false;
                _hostPlaylist = "";
                _hostIndex = 0;
                _hostAt = 0;
            }
        }

        /// <summary>
        /// 本机实时队列快照。网关线程每 1.5~3 秒会问一次，BuildPlaylist 要逐曲取歌词，
        /// 这里按 5 秒节流；曲目切换时由 songKey 变化体现，歌单本身无需每次重算。
        /// </summary>
        private static string LivePlaylist(long now, out int index)
        {
            lock (_liveLock)
            {
                string sig;
                try
                {
                    var q = MusicPlayer.Queue;
                    sig = (MusicPlayer.Current != null ? MusicPlayer.Current.Path : "") + "|"
                          + (q == null ? 0 : q.Count);
                }
                catch { sig = ""; }
                bool same = string.Equals(sig, _liveSig, StringComparison.Ordinal);
                // 队列没变就别再逐曲读歌词；变了（换歌 / 增删曲目）立刻重建
                if (!string.IsNullOrEmpty(_livePlaylist) && same
                    && now - _liveAt < LivePlaylistTtlMs * 12)
                {
                    index = _liveIndex;
                    return _livePlaylist;
                }
                int i;
                string p = "";
                try { p = ListenTogether.BuildPlaylist(out i); } catch { i = 0; }
                _livePlaylist = p ?? "[]";
                _liveSig = sig;
                _liveIndex = i;
                _liveAt = now;
                index = i;
                return _livePlaylist;
            }
        }

        /// <summary>网关线程读、主线程写，统一在锁里取快照，避免读到半更新的状态。</summary>
        private static bool TryHostShare(long now, out string playlist, out int index)
        {
            lock (_hostLock)
            {
                playlist = _hostPlaylist;
                index = _hostIndex;
                return _hostShare && !string.IsNullOrEmpty(_hostPlaylist)
                       && now - _hostAt <= HostShareTtlMs;
            }
        }

        private static Handler H
        {
            get
            {
                if (_h == null) _h = new Handler(Looper.MainLooper);
                return _h;
            }
        }

        private static string FirstNonEmpty(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? (b ?? "") : a;
        }

        /// <summary>「一起听」：只装歌单，不接管进度。</summary>
        public static bool StartPlaylist(string playlist, int index, string cardId, string host)
        {
            string lt = FirstNonEmpty(_ltHost, _lastLtHost);
            string ph = FirstNonEmpty(_peerHost, _lastPeerHost);
            ExitInternal(); // 确保完全清理同步听状态
            if (!ListenTogether.Join(playlist, index, lt, ph)) return false;
            _mode = Mode.Playlist;
            _cardId = cardId ?? "";
            _host = host ?? "";
            _playlist = playlist ?? "";
            _index = index;
            // 共享歌单模式：允许自动续播、跳过失败等正常行为
            MusicPlayer.SuppressAutoAdvance = false;
            MusicPlayer.InitialSeekProvider = null;
            try { MusicPlayer.PrepareFailed -= OnPrepareFailed; } catch { }
            return true;
        }

        /// <summary>「同步听」：装歌单 + 立刻对齐发起端当前曲目/进度/播放态。</summary>
        public static bool StartSync(string playlist, int index, string cardId, string host,
                                     IDictionary<string, object> state)
        {
            ExitInternal();
            try
            {
                _ltHost = state != null ? Msg.Str(state, "ltHost", "") : "";
                _peerHost = state != null ? Msg.Str(state, "peerHost", "") : "";
                _ltHost = FirstNonEmpty(_ltHost, _lastLtHost);
                _peerHost = FirstNonEmpty(_peerHost, _lastPeerHost);
                // 必须在 Join 之前装好：PlaySharedList 会立刻 PlayCurrent，
                // Prepared 回调要靠它拿到权威进度。
                MusicPlayer.SuppressAutoAdvance = true;
                MusicPlayer.InitialSeekProvider = TimelinePosition;
                MusicPlayer.PrepareFailed -= OnPrepareFailed;
                MusicPlayer.PrepareFailed += OnPrepareFailed;

                if (!ListenTogether.Join(playlist, index, _ltHost, _peerHost))
                {
                    MusicPlayer.ResetSyncState();
                    return false;
                }
                _mode = Mode.Sync;
                _cardId = cardId ?? "";
                _host = host ?? "";
                _playlist = playlist ?? "";
                _index = -1;
                EnsureClock();
                Absorb(state, true);
                StartTimer();
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "同步听启动失败: " + ex.Message);
                ExitInternal();
                return false;
            }
        }

        public static void Exit() { ExitInternal(); }

        private static void ExitInternal()
        {
            _mode = Mode.Off;
            _ticking = false;
            try { H.RemoveCallbacksAndMessages(null); } catch { }
            try { MusicPlayer.PrepareFailed -= OnPrepareFailed; } catch { }
            try { MusicPlayer.ResetSyncState(); } catch { }
            try { SyncClock.Stop(); } catch { }
            // 恢复正常播放行为
            MusicPlayer.SuppressAutoAdvance = false;
            MusicPlayer.InitialSeekProvider = null;
            _cardId = _host = _key = _playlist = "";
            _ltHost = _peerHost = _clockTarget = _clockBase = "";
            _index = -1;
            _dur = _displayPos = _anchorPos = 0;
            _anchorTs = _anchorSeq = -1;
            _anchorElapsed = _anchorHostNowAtRecv = 0;
            _anchorPlaying = false;
            _lastHardSeekAt = 0;
            _lastResumeAt = 0;
            _lrcFetchedKey = "";
            _retries = 0;
        }

        private static void EnsureClock()
        {
            // 只有可达的局域网地址才用于 HTTP 对时；127.0.0.1 指的是对端自己，
            // 拿它的时间没有意义，那种情况交给 mesh ping。
            string b = ListenTogether.ReachableHost(_ltHost, _peerHost);
            if (string.Equals(_clockTarget, _host, StringComparison.Ordinal)
                && string.Equals(_clockBase, b, StringComparison.Ordinal)) return;
            _clockTarget = _host;
            _clockBase = b;
            SyncClock.Ensure(_host, _clockBase);
        }

        /// <summary>由 MchatBridge 在收到 musiccard / op=patch 时调用（与 UI 是否可见无关）。</summary>
        public static void OnCard(IDictionary<string, object> msg)
        {
            if (msg == null) return;
            try
            {
                // 不在同步态也记住发起端地址，供随后点「一起听」时使用
                var lt0 = Msg.Str(msg, "ltHost", "");
                var ph0 = Msg.Str(msg, "peerHost", "");
                if (!string.IsNullOrEmpty(lt0)) _lastLtHost = lt0;
                if (!string.IsNullOrEmpty(ph0)) _lastPeerHost = ph0;
            }
            catch { }
            if (!InSync) return;
            try
            {
                var id = Msg.Str(msg, "id", "");
                if (!string.IsNullOrEmpty(_cardId) && !string.IsNullOrEmpty(id)
                    && !string.Equals(id, _cardId, StringComparison.Ordinal)) return;
                if (string.IsNullOrEmpty(_cardId) && !string.IsNullOrEmpty(id)) _cardId = id;

                var from = Msg.Str(msg, "from", "");
                if (string.IsNullOrEmpty(_host)) _host = from;
                else if (!string.Equals(from, _host, StringComparison.Ordinal)) return;
                var self = MchatBridge.Current?.SelfPeerId;
                if (!string.IsNullOrEmpty(self) && string.Equals(from, self, StringComparison.OrdinalIgnoreCase)) return;

                // 乱序 / 重放帧：qaul 与局域网双渠道可能把旧 patch 晚送到，
                // 用旧进度回锚会把已经追上的播放又拉回去。
                long ts = Msg.Num(msg, "ts", 0);
                long seq = Msg.Num(msg, "seq", 0);
                if (ts > 0 && _anchorTs > 0 && ts < _anchorTs - 1000) return;
                if (seq > 0 && _anchorSeq > 0 && seq < _anchorSeq && ts <= _anchorTs) return;

                var lt = Msg.Str(msg, "ltHost", "");
                var ph = Msg.Str(msg, "peerHost", "");
                if (!string.IsNullOrEmpty(lt)) _ltHost = lt;
                if (!string.IsNullOrEmpty(ph)) _peerHost = ph;
                EnsureClock();

                var copy = new Dictionary<string, object>(msg);
                H.Post(() => { try { Absorb(copy, false); } catch { } });
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "同步听处理卡片失败: " + ex.Message);
            }
        }

        /* ---------------- 权威时间轴 ---------------- */

        /// <summary>Prepared 回调取用：此刻发起端应该播到哪一毫秒。</summary>
        private static int TimelinePosition()
        {
            try
            {
                long d = SyncClock.Elapsed - _anchorElapsed;
                if (d < 0 || d > 6L * 60 * 60 * 1000) d = 0;
                long age = _anchorHostNowAtRecv - _anchorTs;
                if (age < 0) age = 0;
                if (age > MaxAgeMs) age = MaxAgeMs;
                long t = _anchorPos + (_anchorPlaying ? age + d : 0);
                if (_dur > 0 && t > _dur) t = _dur;
                if (t < 0) t = 0;
                return (int)t;
            }
            catch { return 0; }
        }

        private static int CurrentQueueIndex()
        {
            var cur = MusicPlayer.Current;
            if (cur == null) return -1;
            var q = MusicPlayer.Queue;
            for (int i = 0; i < q.Count; i++)
                if (ReferenceEquals(q[i], cur)) return i;
            return -1;
        }

        /// <summary>按 songKey 定位曲目，ltIndex 只作兜底（发起端/跟随端过滤结果可能不同）。</summary>
        private static int ResolveIndex(IDictionary<string, object> msg)
        {
            int idx = (int)Math.Max(0, Msg.Num(msg, "ltIndex", _index < 0 ? 0 : _index));
            string key = Msg.Str(msg, "songKey", "");
            if (!string.IsNullOrEmpty(key))
            {
                var q = MusicPlayer.Queue;
                for (int i = 0; i < q.Count; i++)
                {
                    if (q[i] != null && string.Equals(q[i].SharedKey, key, StringComparison.Ordinal))
                    { idx = i; break; }
                }
            }
            int n = MusicPlayer.Queue.Count;
            if (n > 0 && idx >= n) idx = n - 1;
            return Math.Max(0, idx);
        }

        private static void Absorb(IDictionary<string, object> msg, bool force)
        {
            if (msg == null)
            {
                // 没有状态可对齐时，至少别把刚起播的本机播放器暂停掉
                _anchorTs = 0;
                _anchorElapsed = SyncClock.Elapsed;
                _anchorHostNowAtRecv = 0;
                _anchorPos = 0;
                _anchorPlaying = true;
                _dur = 0;
                return;
            }

            // 列表变了（发起端换了歌单）就重新装载
            bool rejoined = false;
            var pl = Msg.Str(msg, "playlist", "");
            if (!string.IsNullOrEmpty(pl) && pl != _playlist)
            {
                int pi = (int)Math.Max(0, Msg.Num(msg, "ltIndex", 0));
                if (ListenTogether.Join(pl, pi, _ltHost, _peerHost))
                {
                    _playlist = pl;
                    rejoined = true;
                }
            }

            // ── 时间锚点 ──
            long ts = Msg.Num(msg, "ts", 0);
            long localUtc = SyncClock.UtcNow;
            long hostNow;
            if (SyncClock.Valid && ts > 0)
                hostNow = localUtc + SyncClock.Offset;                 // 换算成发起端时间
            else
                hostNow = (ts > 0 ? ts : localUtc) + Math.Max(30, SyncClock.OneWayMs); // 相对退化

            _anchorTs = ts > 0 ? ts : localUtc;
            _anchorHostNowAtRecv = hostNow;
            _anchorElapsed = SyncClock.Elapsed;
            _anchorPos = (int)Math.Max(0, Msg.Num(msg, "position", 0));
            _dur = (int)Math.Max(0, Msg.Num(msg, "duration", 0));
            _anchorPlaying = Msg.Bool(msg, "playing");
            long seq = Msg.Num(msg, "seq", 0);
            if (seq > 0) _anchorSeq = seq;

            int idx = ResolveIndex(msg);
            string key = Msg.Str(msg, "songKey", "");
            bool changed = force
                ? (CurrentQueueIndex() != idx)
                : (idx != _index || (!string.IsNullOrEmpty(key) && key != _key));
            if (rejoined) changed = false;   // Join 已经按 ltIndex 起播了

            _index = idx;
            if (!string.IsNullOrEmpty(key)) _key = key;

            if (changed)
            {
                _retries = 0;
                _lastHardSeekAt = 0;
                try { MusicPlayer.SetSyncRate(1f); } catch { }
                ApplyCardLyrics(msg);
                try { MusicPlayer.PlayAt(idx); } catch { }
            }
            else
            {
                ApplyCardLyrics(msg);
            }
            Apply();
        }

        /// <summary>
        /// 切歌时把卡片里属于**当前曲目**的完整歌词写进 Song。
        /// patch 合并会沿用上一首卡片的 lrc，所以必须用 lrcKey 校验归属。
        /// </summary>
        private static void ApplyCardLyrics(IDictionary<string, object> msg)
        {
            try
            {
                var cur = MusicPlayer.Current;
                if (cur == null) return;
                string lrc = Msg.Str(msg, "lrc", "");
                string lk = Msg.Str(msg, "lrcKey", "");
                if (!string.IsNullOrEmpty(lrc) && !string.IsNullOrEmpty(lk)
                    && string.Equals(lk, _key, StringComparison.Ordinal)
                    && !string.Equals(cur.LrcText, lrc, StringComparison.Ordinal))
                {
                    cur.LrcText = lrc;
                    try { MusicPlayer.RefreshMeta(); } catch { }
                }
                // 列表里只带开头几句：后台按 lu 补完整 LRC（本机曲库命中则有 LrcPath，无需取）。
                // 每首只取一次，否则 1.2 秒一个 patch 会不断重复发同一条 HTTP 请求。
                string lk2 = _key + "|" + (cur.Path ?? "");
                if (!string.Equals(lk2, _lrcFetchedKey, StringComparison.Ordinal)
                    && string.IsNullOrEmpty(cur.LrcPath) && !string.IsNullOrEmpty(cur.LrcUrl))
                {
                    _lrcFetchedKey = lk2;
                    ListenTogether.EnsureLyrics(cur, cur.LrcUrl);
                }
            }
            catch { }
        }

        /// <summary>把本机播放器拉到「发起端此刻应有的位置 / 播放态」。</summary>
        private static void Apply()
        {
            if (!InSync) return;
            try
            {
                int target = TimelinePosition();
                _displayPos = target;
                var cur = MusicPlayer.Current;
                if (cur == null) return;

                long now = SyncClock.Elapsed;
                bool localPlaying = MusicPlayer.IsPlaying;
                if (_anchorPlaying && !localPlaying)
                {
                    // 250ms 的闭环里不能每次都 Resume：缓冲期间 Resume 会反复
                    // StartForegroundService + 广播 StateChanged，把主线程打满。
                    if (now - _lastResumeAt > 800)
                    {
                        _lastResumeAt = now;
                        try { MusicPlayer.Resume(); } catch { }
                    }
                }
                else if (!_anchorPlaying && localPlaying)
                {
                    try { MusicPlayer.Pause(); } catch { }
                }

                int dur = MusicPlayer.Duration;
                if (MusicPlayer.IsBuffering || dur <= 0 || !_anchorPlaying)
                {
                    try { MusicPlayer.SetSyncRate(1f); } catch { }
                    return;
                }

                int local = MusicPlayer.Position;
                int drift = target - local;      // >0：本机落后，需要加速 / 往前 seek

                if (Math.Abs(drift) > HardSeekMs && now - _lastHardSeekAt > HardSeekCooldownMs)
                {
                    int want = Math.Min(target, Math.Max(0, dur - 300));
                    try { MusicPlayer.SeekTo(want); } catch { }
                    _lastHardSeekAt = now;
                    try { MusicPlayer.SetSyncRate(1f); } catch { }
                }
                else if (Math.Abs(drift) > DeadbandMs)
                {
                    // 6 秒线性收敛：600ms 偏差 → 1.10（再由 SetSyncRate 夹到 1.06）
                    float rate = 1f + drift / 6000f;
                    try { MusicPlayer.SetSyncRate(rate); } catch { }
                }
                else
                {
                    try { MusicPlayer.SetSyncRate(1f); } catch { }
                    _retries = 0;
                }
            }
            catch { }
        }

        /// <summary>
        /// 网络源准备失败：绝不跳下一首。先尝试换成本机曲库里的同一首，
        /// 再按有限次数重播当前曲，避免「缓冲中…」无限挂着。
        /// </summary>
        private static void OnPrepareFailed(string path)
        {
            if (!InSync) return;
            try
            {
                H.Post(() =>
                {
                    if (!InSync) return;
                    if (_retries >= MaxRetries)
                    {
                        Android.Util.Log.Warn("Mchat", "同步听：播放源不可用，等待发起端下一张卡片 " + path);
                        return;
                    }
                    _retries++;
                    var cur = MusicPlayer.Current;
                    if (cur != null && _retries == 1)
                    {
                        try { ListenTogether.TryUseLocal(cur); } catch { }
                    }
                    int wait = 700 * _retries;
                    H.PostDelayed(() =>
                    {
                        if (!InSync) return;
                        try { MusicPlayer.PlayAt(Math.Max(0, _index)); } catch { }
                    }, wait);
                });
            }
            catch { }
        }

        private static void StartTimer()
        {
            if (_ticking) return;
            _ticking = true;
            H.PostDelayed(Tick, TickMs);
        }

        private static void Tick()
        {
            if (!_ticking || !InSync) { _ticking = false; return; }
            try { Apply(); } catch { }
            if (_ticking) H.PostDelayed(Tick, TickMs);
        }
    }
}

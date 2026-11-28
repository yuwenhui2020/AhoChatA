// ============================================================
// 音乐模块 · 播放引擎（进程级单例）
// · MediaPlayer 封装：播放/暂停/Seek/倍速/自动下一曲
// · 队列 + 顺序/列表循环/单曲循环/随机
// · 音频焦点：来电或其它应用抢占时自动暂停
// · 状态通过事件广播给 Activity 与前台服务（通知栏）
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Widget;
using AhoChatA.Music;

namespace AhoChatA.Music
{
    public static class MusicPlayer
    {
        private static MediaPlayer _mp;
        private static readonly object _lock = new object();
        private static Timer _tick;
        private static bool _preparing;
        private static AudioManager _audio;
        private static int _playToken;      // 每次 PlayCurrent +1，用于丢弃过期的准备回调
        private static int _pendingToken;   // 当前正在准备的那次 token
        private static int _preparedDone;   // 已完成准备的 token（Prepared 事件 + 同步回退去重）
        private static string _preparePath = "";              // 本次准备对应的曲目（防串曲）
        private static TaskCompletionSource<bool> _preparedTcs; // 准备结果（成功/失败），供看门狗等待

        // 「用户想要播放」的意图。缓冲期间点暂停/播放不再被直接丢弃，
        // 而是记下来等 Prepared 时执行，避免「点了播放没反应」像卡住。
        private static volatile bool _wantPlay;
        private static volatile bool _pausedByFocus;   // 是不是因为音频焦点被抢才停的
        private static FocusListener _focusCb;

        // 耳机/蓝牙断开监听：系统广播 AUDIO_BECOMING_NOISY（有线拔出、蓝牙断开都会发）。
        // 只在本音乐引擎里注册，视频播放器不监听，因此不会暂停视频。
        private static NoisyAudioReceiver _noisyReceiver;
        private static bool _noisyRegistered;

        // 播放停滞看门狗：网络流播到中途卡死时，位置会长时间不动
        private static int _stallPos = -1;
        private static int _stallBeats;
        private static int _stallRetries;

        /// <summary>正在缓冲/准备（网络流首次加载时置位，Prepared 后清掉）。</summary>
        public static bool IsBuffering { get; private set; }

        /// <summary>0=不循环 1=列表循环 2=单曲循环</summary>
        public static int Repeat;
        public static bool Shuffle;

        public static List<Song> Queue = new List<Song>();
        public static List<int> Order = new List<int>();   // 播放顺序（随机时为打乱后的索引）
        public static int OrderPos = -1;

        public static Song Current =>
            OrderPos >= 0 && OrderPos < Order.Count &&
            Order[OrderPos] >= 0 && Order[OrderPos] < Queue.Count
                ? Queue[Order[OrderPos]] : null;

        public static bool IsPlaying
        {
            get
            {
                if (_preparing) return false;
                lock (_lock) { try { return _mp != null && _mp.IsPlaying; } catch { return false; } }
            }
        }

        public static int Position
        {
            get
            {
                // 未准备完成时访问 CurrentPosition 会被系统记成 "wrong state" 错误并刷日志
                if (_preparing) return 0;
                lock (_lock) { try { return _mp?.CurrentPosition ?? 0; } catch { return 0; } }
            }
        }

        public static int Duration
        {
            get
            {
                var s = Current;
                if (s != null && s.DurationMs > 0) return s.DurationMs;
                if (_preparing) return 0;
                lock (_lock) { try { return _mp?.Duration ?? 0; } catch { return 0; } }
            }
        }

        /// <summary>倍速（1.0f 正常）。API23+ 生效。</summary>
        public static float Speed = 1.0f;

        /* ---------------- 同步听专用 ---------------- */
        /// <summary>渐进漂移校正用的微调倍速（±6%），与用户手动倍速 Speed 相乘。</summary>
        private static float _syncRate = 1.0f;
        private static bool _speedApplied;
        public static float SyncRate => _syncRate;

        /// <summary>同步听跟随端：准备失败/播完都不许自动跳到下一首，一切等发起端卡片。</summary>
        public static bool SuppressAutoAdvance;

        /// <summary>
        /// 每首歌 Prepared 后回调一次，返回该时刻应处的播放位置（毫秒）。
        /// 把它做成回调而不是固定值，是因为网络流 prepare 可能花 1~3 秒，
        /// 提前算好的 seek 目标到真正起播时已经过期。
        /// </summary>
        public static Func<int> InitialSeekProvider;

        /// <summary>同步听：一次准备（含换源重试）彻底失败，交给 SyncListen 换本机源/重试。</summary>
        public static event Action<string> PrepareFailed;

        /* ---------------- 事件 ---------------- */

        /// <summary>播放/暂停状态或当前曲目变化。</summary>
        public static event Action StateChanged;
        /// <summary>换曲（重新加载歌词/封面）。</summary>
        public static event Action SongChanged;
        /// <summary>
        /// 是否已真正开始播放过（用户首次按下播放才置 true）。
        /// 待播(pending)态为 false：此时只是随机挑了一首「待播」歌用于本地底栏展示，
        /// 不应把卡片广播给其它设备；只有用户真正开播后才允许 MusicCast 广播第一张卡片并同步状态，
        /// 避免本机一启动就打扰局域网内其它设备。
        /// </summary>
        public static bool HasStarted { get; private set; }
        /// <summary>约 500ms 一次的位置回报。</summary>
        public static event Action<int> PositionTick;
        /// <summary>队列内容变化（加歌 / 移除 / 换队列），UI 刷新列表用。</summary>
        public static event Action QueueChanged;

        internal static void Fire(Action a)
        {
            try { MainThread(a); } catch { }
        }

        internal static void MainThread(Action a)
        {
            var h = new Handler(Looper.MainLooper);
            h.Post(() => { try { a(); } catch { } });
        }

        /* ---------------- 队列 ---------------- */

        public static void PlayAll(IEnumerable<Song> songs, int startIndex)
        {
            Queue = (songs ?? new List<Song>()).ToList();
            RebuildOrder(startIndex);
            NotifyQueueChanged();
            PlayCurrent();
        }

        private static readonly Random _rnd = new Random();

        /// <summary>
        /// 当前没有曲目时，随机挑一首作为「待播」曲目（**不加载播放器、不出声**）：
        /// 优先从播放列表里随机选一首；播放列表为空时从本地曲库随机挑一首并放进播放列表。
        /// 这样主界面 / 聊天底栏 / 阅读底栏在没有播放时也能常驻显示一张卡片，
        /// 用户按播放键才真正开始加载与播放。
        /// </summary>
        public static bool EnsurePending()
        {
            try
            {
                if (Current != null) return true;

                Song pick = null;
                int qi = -1;
                if (Queue.Count > 0)
                {
                    qi = _rnd.Next(Queue.Count);
                    pick = Queue[qi];
                }
                if (pick == null)
                {
                    // 曲库平时是「打开影音页」才加载的；聊天/阅读底栏在没进过影音时也要能挑歌，
                    // 这里补一次懒加载（幂等，只有第一次会读盘）。
                    try { MusicLibrary.EnsureLoaded(Android.App.Application.Context); } catch { }
                    var lib = MusicLibrary.Audio();
                    if (lib != null && lib.Count > 0)
                    {
                        pick = lib[_rnd.Next(lib.Count)];
                        if (pick != null)
                        {
                            Queue.Add(pick);
                            qi = Queue.Count - 1;
                        }
                    }
                }
                if (pick == null)
                {
                    // 全新安装 / 换包名后曲库还没扫过：有权限就后台补扫一次，
                    // 扫完再挑歌并通知界面把底栏显示出来（用户不必先打开影音页）。
                    TryAutoScanLibrary();
                    return false;
                }
                if (qi < 0) return false;

                RebuildOrder(qi);
                NotifyQueueChanged();
                Fire(() => { try { SongChanged?.Invoke(); } catch { } });
                Fire(StateChanged);
                return true;
            }
            catch { return false; }
        }

        private static int _autoScanTried;

        /// <summary>曲库为空且从没扫过时，在后台补扫一次（仅当已有存储权限）。</summary>
        private static void TryAutoScanLibrary()
        {
            try
            {
                if (MusicLibrary.LastScan != default) return;          // 扫过了，只是没找到歌
                if (Interlocked.CompareExchange(ref _autoScanTried, 1, 0) != 0) return;
                if (!MusicLibrary.CanReadStorage()) return;
                Task.Run(async () =>
                {
                    try { await MusicLibrary.ScanAsync().ConfigureAwait(false); } catch { }
                    MainThread(() =>
                    {
                        try
                        {
                            if (EnsurePending()) return;
                            // 仍然没有可播曲目（真的没歌）：让界面重新判一次可见性
                            StateChanged?.Invoke();
                        }
                        catch { }
                    });
                });
            }
            catch { }
        }

        public static void PlayAt(int queueIndex)
        {
            for (int i = 0; i < Order.Count; i++)
            {
                if (Order[i] == queueIndex) { OrderPos = i; break; }
            }
            PlayCurrent();
        }

        /// <summary>把一首歌插到「当前曲之后」立刻播放（下一曲播放）。</summary>
        public static void PlayNext(Song song)
        {
            if (song == null) return;
            var cur = Current;
            if (cur == null || Queue.Count == 0)
            {
                PlayAll(new List<Song> { song }, 0);
                return;
            }
            if (string.Equals(cur.Path, song.Path, StringComparison.OrdinalIgnoreCase))
            {
                PlayCurrent();
                return;
            }

            // 以「当前播放顺序」为基准重建队列，随机模式下的既定顺序不会被打乱
            var seq = OrderedSongs();
            seq.RemoveAll(x => string.Equals(x.Path, song.Path, StringComparison.OrdinalIgnoreCase));
            int ci = seq.FindIndex(x => string.Equals(x.Path, cur.Path, StringComparison.OrdinalIgnoreCase));
            if (ci < 0) { seq.Insert(0, cur); ci = 0; }
            seq.Insert(ci + 1, song);

            Queue = seq;
            Order = Enumerable.Range(0, Queue.Count).ToList();
            OrderPos = ci;                 // 当前曲位置不变，不打断播放
            NotifyQueueChanged();
        }

        /// <summary>从播放列表移出指定项；若移出的正好是当前曲，自动接着播下一首。</summary>
        public static void RemoveAt(int queueIndex)
        {
            if (queueIndex < 0 || queueIndex >= Queue.Count) return;
            var cur = Current;
            string curPath = cur?.Path;
            string gonePath = Queue[queueIndex].Path;
            bool removingCurrent = curPath != null &&
                string.Equals(gonePath, curPath, StringComparison.OrdinalIgnoreCase);

            var seq = OrderedSongs();
            seq.RemoveAll(x => string.Equals(x.Path, gonePath, StringComparison.OrdinalIgnoreCase));

            Queue = seq;
            if (Queue.Count == 0)
            {
                Order = new List<int>();
                OrderPos = -1;
                StopAll();
                NotifyQueueChanged();
                return;
            }
            Order = Enumerable.Range(0, Queue.Count).ToList();
            int p = curPath == null ? -1
                : Queue.FindIndex(x => string.Equals(x.Path, curPath, StringComparison.OrdinalIgnoreCase));
            OrderPos = p >= 0 ? p : Math.Min(Math.Max(OrderPos, 0), Queue.Count - 1);
            NotifyQueueChanged();
            if (removingCurrent) PlayCurrent();
        }

        /// <summary>按当前播放顺序导出歌曲列表（含尚未轮到的），顺序稳定。</summary>
        private static List<Song> OrderedSongs()
        {
            var seq = new List<Song>();
            foreach (var qi in Order)
                if (qi >= 0 && qi < Queue.Count) seq.Add(Queue[qi]);
            foreach (var s in Queue)
                if (!seq.Any(x => string.Equals(x.Path, s.Path, StringComparison.OrdinalIgnoreCase)))
                    seq.Add(s);
            return seq;
        }

        public static void NotifyQueueChanged() { Fire(() => QueueChanged?.Invoke()); }

        /// <summary>封面/歌词异步补齐后让 UI 重读元数据，不重新准备播放器、不打断播放。</summary>
        public static void RefreshMeta() { Fire(() => { try { SongChanged?.Invoke(); } catch { } }); }

        /// <summary>异步资源（封面/歌词）就绪后刷新通知栏与界面。</summary>
        public static void RefreshNotification() { Fire(StateChanged); }

        /// <summary>
        /// 确保处于「正在播放」：远端切歌指令执行完后调用，
        /// 避免因为暂停态 / 焦点丢失而停在暂停，出现「收到了切歌却没声音」。
        /// </summary>
        public static void EnsurePlaying(int delayMs = 600)
        {
            try
            {
                Task.Run(async () =>
                {
                    await Task.Delay(delayMs);
                    if (Current == null) return;
                    if (_preparing || IsBuffering) return;
                    if (!IsPlaying)
                    {
                        try { _mp?.Start(); } catch { }
                        RequestFocus();
                        EnsureService();
                        Fire(StateChanged);
                    }
                });
            }
            catch { }
        }

        private static void RebuildOrder(int startQueueIndex)
        {
            Order = Enumerable.Range(0, Queue.Count).ToList();
            if (Shuffle && Order.Count > 1)
            {
                var rnd = new Random();
                for (int i = Order.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    (Order[i], Order[j]) = (Order[j], Order[i]);
                }
                OrderPos = Order.IndexOf(startQueueIndex);
                if (OrderPos > 0)   // 把选中的放最前面
                {
                    Order.RemoveAt(OrderPos);
                    Order.Insert(0, startQueueIndex);
                    OrderPos = 0;
                }
            }
            else
            {
                OrderPos = startQueueIndex >= 0 && startQueueIndex < Queue.Count ? startQueueIndex : -1;
            }
        }

        public static void SetShuffle(bool on)
        {
            Shuffle = on;
            var cur = Current;
            int idx = cur == null ? -1 : Queue.FindIndex(s => s.Path == cur.Path);
            RebuildOrder(idx);
        }

        /* ---------------- 播放模式（顺序 / 列表循环 / 单曲循环 / 随机） ---------------- */

        /// <summary>0=顺序 1=列表循环 2=单曲循环 3=随机</summary>
        public static int Mode
        {
            get { if (Shuffle) return 3; return Repeat == 2 ? 2 : (Repeat == 1 ? 1 : 0); }
            set
            {
                int m = Math.Max(0, Math.Min(3, value));
                if (m == 3) { Repeat = 1; SetShuffle(true); }
                else { Repeat = m; SetShuffle(false); }
                Fire(StateChanged);
            }
        }

        public static int CycleMode()
        {
            Mode = (Mode + 1) % 4;
            return Mode;
        }

        public static string ModeName
        {
            get
            {
                switch (Mode)
                {
                    case 1: return "列表循环";
                    case 2: return "单曲循环";
                    case 3: return "随机播放";
                    default: return L10n.T("顺序播放");
                }
            }
        }

        /* ---------------- 队列增删（「加入播放列表」用） ---------------- */

        /// <summary>加一首到播放列表末尾；队列为空时直接开始播放。已存在则跳过。</summary>
        public static void AddToQueue(Song song)
        {
            if (song == null) return;
            if (Queue.Count == 0 || Current == null)
            {
                PlayAll(new List<Song> { song }, 0);
                return;
            }
            if (Queue.Any(x => string.Equals(x.Path, song.Path, StringComparison.OrdinalIgnoreCase))) return;
            Queue.Add(song);
            Order.Add(Queue.Count - 1);
            NotifyQueueChanged();
        }

        /// <summary>批量加入播放列表（不打断当前播放）。</summary>
        public static void AddRangeToQueue(IEnumerable<Song> songs)
        {
            if (songs == null) return;
            var list = songs.ToList();
            if (list.Count == 0) return;
            if (Queue.Count == 0 || Current == null) { PlayAll(list, 0); return; }
            bool changed = false;
            foreach (var s in list)
            {
                if (s == null) continue;
                if (Queue.Any(x => string.Equals(x.Path, s.Path, StringComparison.OrdinalIgnoreCase))) continue;
                Queue.Add(s);
                Order.Add(Queue.Count - 1);
                changed = true;
            }
            if (changed) NotifyQueueChanged();
        }

        /// <summary>把队列中的某一项上移/下移（delta = -1 上移，+1 下移）。</summary>
        public static void MoveInQueue(int queueIndex, int delta)
        {
            if (queueIndex < 0 || queueIndex >= Queue.Count) return;
            int to = queueIndex + delta;
            if (to < 0 || to >= Queue.Count) return;

            var cur = Current;
            var item = Queue[queueIndex];
            Queue.RemoveAt(queueIndex);
            Queue.Insert(to, item);

            // 重建顺序并让「当前曲」位置保持不变
            Order = Enumerable.Range(0, Queue.Count).ToList();
            if (cur != null)
            {
                int p = Queue.IndexOf(cur);
                OrderPos = p >= 0 ? p : Math.Min(Math.Max(OrderPos, 0), Queue.Count - 1);
            }
            NotifyQueueChanged();
        }

        public static void ClearQueue()
        {
            Queue.Clear();
            Order.Clear();
            OrderPos = -1;
            StopAll();
            NotifyQueueChanged();
        }

        /* ---------------- 播放控制 ---------------- */

        public static void PlayCurrent()
        {
            var s = Current;
            if (s == null) return;
            HasStarted = true;   // 用户真正开播：此后才允许 MusicCast 广播卡片（待播态不广播）
            RequestFocus();
            EnsureService();

            int token = Interlocked.Increment(ref _playToken);
            _pendingToken = token;
            _preparing = true;
            IsBuffering = true;
            _wantPlay = true;
            _stallPos = -1; _stallBeats = 0; _stallRetries = 0;
            _preparePath = s.Path;
            Fire(StateChanged);

            // 关键：这里不再 await 解析直链。在线歌曲直接拿「可流式播放的地址」立刻开播，
            // 让 MediaPlayer 边下边放（抢先播放）；直链解析留给后台预热。
            Info("PlayCurrent: " + s.DisplayTitle + " path=" + s.Path);
            Task.Run(() => PrepareLoopAsync(s, token));
        }

        /// <summary>准备循环：先能用最快的方式开播；失败先换源重试同一首，仍失败才跳下一首。</summary>
        private static async Task PrepareLoopAsync(Song s, int token)
        {
            string src;
            try { src = s.IsOnline ? OnlineMusic.GetPlaybackUrl(s) : s.Path; }
            catch { src = s.Path; }
            if (string.IsNullOrEmpty(src)) src = s.Path;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (token != Volatile.Read(ref _playToken)) return;

                if (attempt > 0)
                {
                    // 第一次失败：换一个源（现场解析出的 CDN 直链），重试同一首歌，不跳歌
                    if (!s.IsOnline) break;
                    try { src = await OnlineMusic.ResolveAltUrlAsync(s) ?? src; } catch { }
                    if (token != Volatile.Read(ref _playToken)) return;
                }

                bool ok = await PrepareOneAsync(token, src);
                if (ok) return;
                if (token != Volatile.Read(ref _playToken)) return;
            }

            // 两次都不行才跳下一首（队列只剩 1 首则停下，避免死循环）。
            // 同步听跟随端例外：跳歌会造成「只同步了切歌动作、听的却是另一首」，
            // 这里停下来通知同步器换本机源 / 重试，曲目始终锁定在发起端那一首。
            _preparing = false;
            IsBuffering = false;
            bool synced = SuppressAutoAdvance;
            string failedPath = s?.Path ?? "";
            Fire(() =>
            {
                StateChanged?.Invoke();
                if (synced)
                {
                    try { PrepareFailed?.Invoke(failedPath); } catch { }
                }
                else if (Queue.Count > 1) Next(auto: true);
            });
        }

        /// <summary>用指定地址准备一次，返回是否在超时内准备成功。</summary>
        private static async Task<bool> PrepareOneAsync(int token, string src)
        {
            var tcs = new TaskCompletionSource<bool>();
            _preparedTcs = tcs;
            try
            {
                // 每首新建一个 MediaPlayer：切歌时旧实例直接 Release，
                // 迟到回调（旧实例的 Prepared/Error）由 sender 校验丢弃，不会把新歌卡死。
                var mp = Create();
                lock (_lock)
                {
                    var old = _mp;
                    _mp = mp;
                    try { old?.Reset(); } catch { }
                    try { old?.Release(); } catch { }
                }
                _pendingToken = token;
                mp.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)
                    .SetContentType(AudioContentType.Music)
                    .Build());
                mp.SetDataSource(src);
                try
                {
                    mp.PrepareAsync();   // 立刻返回，边缓冲边等 Prepared
                }
                catch
                {
                    mp.Prepare();        // 个别机型不支持 PrepareAsync 时回退同步
                    CompletePrepared(token);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("准备播放失败：") + ex.Message);
                return false;
            }

            // 看门狗：超时即认为这个源不可用，交给外层换源。
            // 同步听跟随端用更短的 6 秒，避免「缓冲中…」一直挂着不动。
            int watchdog = SuppressAutoAdvance ? 6000 : 10000;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(watchdog)).ConfigureAwait(false);
            if (done != tcs.Task) { Info("prepare timeout: " + src); return false; }
            bool ok;
            try { ok = await tcs.Task.ConfigureAwait(false); } catch { ok = false; }
            return ok;
        }

        /// <summary>准备完成：清缓冲标记、广播换曲并开始播放。可在任意线程调用。</summary>
        private static void CompletePrepared(int token)
        {
            if (token != Volatile.Read(ref _playToken)) return;
            if (Interlocked.Exchange(ref _preparedDone, token) == token) return;  // 已处理过，避免 Start 两次
            lock (_lock) { try { ApplySpeedLocked(); } catch { } }
            _preparing = false;
            IsBuffering = false;
            _stallPos = -1; _stallBeats = 0;
            // 缓冲期间用户可能按过暂停：尊重那个意图，不要把歌强行播起来
            Info("prepared ok, wantPlay=" + _wantPlay);
            // 同步听：Prepared 这一刻才向同步器要目标进度，避免网络流缓冲几秒后 seek 到过期位置。
            int initialSeek = -1;
            try { if (InitialSeekProvider != null) initialSeek = InitialSeekProvider(); } catch { }
            if (initialSeek > 0)
            {
                try { _mp?.SeekTo(initialSeek); } catch { }
            }
            if (_wantPlay) { try { _mp?.Start(); } catch { } }
            EnsureOnlineAssets(Current);             // 在线歌曲：后台补齐封面与歌词文件
            try { _preparedTcs?.TrySetResult(true); } catch { }
            Fire(() =>
            {
                try { StartTick(); } catch { }
                StateChanged?.Invoke();
                SongChanged?.Invoke();
            });
            Fire(StateChanged);
        }

        /// <summary>
        /// 在线歌曲的封面/歌词是异步落盘的：这里在「开始播放」后统一补齐，
        /// 完成后广播一次 SongChanged，音乐页、聊天底栏迷你条、通知栏都会自动刷新。
        /// 以前只有音乐页会去抓，聊天底栏就一直没有封面和歌词。
        /// </summary>
        private static void EnsureOnlineAssets(Song s)
        {
            if (s == null || !s.IsOnline) return;
            if (!string.IsNullOrEmpty(s.CoverPath) && !string.IsNullOrEmpty(s.LrcPath)) return;

            Task.Run(async () =>
            {
                bool changed = false;
                try
                {
                    if (string.IsNullOrEmpty(s.CoverPath))
                    {
                        var p = await OnlineMusic.FetchCoverAsync(s);
                        if (!string.IsNullOrEmpty(p)) changed = true;
                    }
                }
                catch { }
                try
                {
                    if (string.IsNullOrEmpty(s.LrcPath))
                    {
                        var p = await OnlineMusic.FetchLyricsAsync(s);
                        if (!string.IsNullOrEmpty(p)) changed = true;
                    }
                }
                catch { }

                if (!changed) return;
                Fire(() =>
                {
                    try { SongChanged?.Invoke(); } catch { }
                    try { StateChanged?.Invoke(); } catch { }
                });
            });
        }

        /// <summary>本次准备失败（解码错误/源不可用），交给外层换源重试。</summary>
        private static void FailPrepared(int token)        {
            Info("prepared FAIL token=" + token);
            if (token != Volatile.Read(ref _playToken)) return;
            _preparing = false;
            IsBuffering = false;
            try { _preparedTcs?.TrySetResult(false); } catch { }
            Fire(StateChanged);
        }

        public static void Toggle()
        {
            if (Current == null) return;
            if (IsPlaying) Pause(); else Resume();
        }

        public static void Pause()
        {
            _wantPlay = false;
            _pausedByFocus = false;
            PauseInternal(abandonFocus: true);
        }

        /// <summary>暂停本体；音频焦点被动丢失时不要放弃焦点（否则恢复后抢不回来）。</summary>
        private static void PauseInternal(bool abandonFocus)
        {
            try { if (_mp != null && _mp.IsPlaying) _mp.Pause(); } catch { }
            if (abandonFocus) AbandonFocus();
            Fire(StateChanged);
        }

        public static void Resume()
        {
            _wantPlay = true;
            // 还在缓冲：不要直接 return 让用户以为卡住，记下意图，Prepared 后照常播放
            if (_preparing)
            {
                RequestFocus();
                EnsureService();
                Fire(StateChanged);
                return;
            }

            // 「待播」状态（底栏常驻的卡片，播放器还没建过）：这时才真正开始加载并播放
            bool ready;
            lock (_lock) { ready = _mp != null; }
            if (!ready)
            {
                PlayCurrent();
                return;
            }

            try { _mp?.Start(); ApplySpeed(); } catch { }
            RequestFocus();
            EnsureService();
            Fire(StateChanged);
        }

        public static void SeekTo(int ms)
        {
            try { _mp?.SeekTo(ms); } catch { }
            Fire(StateChanged);
        }

        /// <summary>
        /// 「一起听」入口：把对端共享过来的整条播放列表装进本机队列，从指定曲目开始连续播放。
        /// 列表项 Path 已是 http 绝对地址（本机网关 stream / online 代理），MediaPlayer 可直接
        /// 边下边放；播完自动走既有的「下一曲」逻辑，不需要额外轮询对端状态。
        /// </summary>
        public static void PlaySharedList(List<Song> songs, int index)
        {
            if (songs == null || songs.Count == 0) return;
            EnsureService();
            try
            {
                Queue = songs;
                Order = Enumerable.Range(0, songs.Count).ToList();
                OrderPos = Math.Max(0, Math.Min(index, songs.Count - 1));
                Shuffle = false;
                NotifyQueueChanged();
                PlayCurrent();
            }
            catch (Exception ex) { Info(L10n.T("PlaySharedList 失败: ") + ex.Message); }
        }

        /// <summary>
        /// 「一起播放」同步入口：播放本地已缓存的文件，并按对齐时间精确开始。
        /// startAtLocalElapsed 是本机 ElapsedRealtime 目标起点；seekMs 是该时刻应处的播放位置（已按时钟偏移折算）。
        /// </summary>
        public static void PlayExternal(string path, string title, string artist, long startAtLocalElapsed, int seekMs)
        {
            Interlocked.Increment(ref _playToken);   // 作废可能在途的 PlayCurrent 回调
            EnsureService();
            Task.Run(() =>
            {
                try
                {
                    var mp = Create(false);
                    lock (_lock)
                    {
                        _mp?.Release();
                        _mp = mp;
                        _preparing = true;
                    }
                    mp.Reset();
                    mp.SetDataSource(path);
                    mp.SetAudioAttributes(new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.Media)
                        .SetContentType(AudioContentType.Music)
                        .Build());
                    mp.Prepare();
                    ApplySpeedLocked();

                    // 让 Current / 通知 / 迷你条都指向这份文件
                    var song = new Song { Path = path, Title = title, Artist = artist };
                    Queue = new List<Song> { song };
                    Order = new List<int> { 0 };
                    OrderPos = 0;

                    long delay = startAtLocalElapsed - SystemClock.ElapsedRealtime();
                    if (delay > 0) System.Threading.Thread.Sleep((int)Math.Min(delay, 60000));
                    try { mp.SeekTo(Math.Max(0, seekMs)); } catch { }
                    mp.Start();
                    _preparing = false;
                    Fire(() =>
                    {
                        try { StartTick(); } catch { }
                        StateChanged?.Invoke();
                        SongChanged?.Invoke();
                    });
                }
                catch (Exception)
                {
                    _preparing = false;
                    Fire(() => StateChanged?.Invoke());
                }
            });
        }

        public static void Next(bool auto = false)
        {
            if (Order.Count == 0) return;
            if (auto && Repeat == 2) { SeekTo(0); Resume(); return; }   // 单曲循环
            if (OrderPos < Order.Count - 1)
                OrderPos++;
            else if (Repeat == 1 || !auto || Shuffle)                    // 列表循环；手动点下一曲也回头
                OrderPos = 0;
            else
            { Pause(); return; }                                         // 播完就停
            PlayCurrent();
        }

        public static void Prev()
        {
            if (Order.Count == 0) return;
            // 播放超过 3 秒先回到本曲开头（通用播放器习惯）
            if (Position > 3000) { SeekTo(0); Fire(StateChanged); return; }
            if (OrderPos > 0) OrderPos--;
            else OrderPos = Order.Count - 1;
            PlayCurrent();
        }

        /// <summary>0.75 → 1.0 → 1.25 → 1.5 → 2.0 循环，返回新倍速。</summary>
        public static float CycleSpeed()
        {
            float[] sp = { 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
            int i = Array.IndexOf(sp, Speed);
            Speed = sp[(i + 1) % sp.Length];
            ApplySpeed();
            Fire(StateChanged);
            return Speed;
        }

        private static void ApplySpeed()
        {
            lock (_lock) { ApplySpeedLocked(); }
        }

        private static void ApplySpeedLocked()
        {
            if (_mp == null) return;
            float rate = Speed * _syncRate;
            if (rate < 0.5f) rate = 0.5f;
            if (rate > 2.0f) rate = 2.0f;
            bool nonDefault = Math.Abs(rate - 1.0f) > 0.001f;
            // 从没偏离过 1.0 就不去动 PlaybackParams：部分机型的网络流一写就重新缓冲
            if (!nonDefault && !_speedApplied) return;
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    _mp.PlaybackParams = new PlaybackParams().SetSpeed(rate);
                    _speedApplied = nonDefault;
                }
            }
            catch { }
        }

        /// <summary>同步听漂移校正：不改变用户手动倍速，只叠加一个 ±6% 的微调。</summary>
        public static void SetSyncRate(float rate)
        {
            // 只做 ±6% 的微校正：人耳基本听不出变调/变速，6 秒能收敛 360ms
            if (rate < 0.94f) rate = 0.94f;
            if (rate > 1.06f) rate = 1.06f;
            if (Math.Abs(rate - _syncRate) < 0.002f) return;
            _syncRate = rate;
            ApplySpeed();
        }

        public static void ResetSyncState()
        {
            _syncRate = 1.0f;
            SuppressAutoAdvance = false;
            InitialSeekProvider = null;
            ApplySpeed();
        }

        public static void StopAll()
        {
            Interlocked.Increment(ref _playToken);   // 让还在准备的回调失效
            try { _preparedTcs?.TrySetResult(false); } catch { }
            try { _mp?.Stop(); } catch { }
            try { _mp?.Release(); } catch { }
            _mp = null;
            _preparing = false;
            IsBuffering = false;
            StopTick();
            Fire(StateChanged);
        }

        /* ---------------- 内部 ---------------- */

        private static MediaPlayer Create(bool withEvents = true)
        {
            _speedApplied = false;   // 新实例没写过 PlaybackParams
            var p = new MediaPlayer();
            p.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                .SetContentType(AudioContentType.Music)
                .Build());
            if (!withEvents) return p;   // 「一起播放」同步场景不需要自动续播事件

            // 注意：Xamarin 的 Prepared/Completion/Error 事件共用同一个监听器槽，
            // 这里只挂事件、绝不手动 SetOnPreparedListener，否则会互相覆盖。
            // 另外旧实例被 Release 后仍可能投递迟到回调，统一用 sender 校验过滤。
            p.Prepared += (s, e) =>
            {
                if (!ReferenceEquals(s, _mp)) return;
                CompletePrepared(Volatile.Read(ref _pendingToken));
            };
            p.Completion += (s, e) =>
            {
                if (!ReferenceEquals(s, _mp)) return;
                // 同步听跟随端：切歌只能由发起端的卡片驱动，本机播完不自动换曲
                if (SuppressAutoAdvance) return;
                // 只有「当前这一首」正常播完才自动下一曲，迟到的旧回调不会把歌名跳掉
                if (!string.Equals(Current?.Path, _preparePath, StringComparison.OrdinalIgnoreCase)) return;

                // 网络流（HTTP MediaPlayer）在缓冲尽头会误发 Completion：
                // 播放位置离总时长还很远就当没发生，否则会「播到一半莫名跳歌」。
                try
                {
                    lock (_lock)
                    {
                        int dur = _mp?.Duration ?? 0;
                        int pos = _mp?.CurrentPosition ?? 0;
                        if (dur > 0 && pos > 0 && pos < dur - 1500) return;
                    }
                }
                catch { }

                Fire(() => Next(auto: true));
            };
            p.Error += (s, e) =>
            {
                Info("player error what=" + (e != null ? e.What.ToString() : "?")
                     + " extra=" + (e != null ? e.Extra.ToString() : "?"));
                if (!ReferenceEquals(s, _mp)) return;
                try { _mp?.Reset(); } catch { }
                // 出错不直接跳歌：先让外层换源重试同一首（跳歌会让标题乱跳且用户听不到歌）
                FailPrepared(Volatile.Read(ref _pendingToken));
            };
            return p;
        }

        private static void StartTick()
        {
            if (_tick != null) return;
            _tick = new Timer(_ =>
            {
                int pos = Position;
                try { PositionTick?.Invoke(pos); } catch { }
                WatchStall(pos);
            }, null, 0, 500);
        }

        /// <summary>
        /// 播放停滞看门狗：在线流走的是网络 MediaPlayer，缓冲边界、DNS 抖动、
        /// CDN 掐断都会造成「位置不动但 IsPlaying 仍为真」的假播放。
        /// 这里先轻推 seek 唤醒缓冲，反复无效才重新准备同一首（绝不跳歌）。
        /// </summary>
        private static void WatchStall(int pos)
        {
            bool playing;
            lock (_lock) { try { playing = _mp != null && _mp.IsPlaying; } catch { playing = false; } }
            if (!playing || _preparing || IsBuffering)
            {
                _stallPos = -1; _stallBeats = 0;
                return;
            }

            // 时长未知（部分流 / 扫描未解析出时长的文件）不参与停滞判定：
            // 这类源的位置可能长时间报 0，误判会导致"主动 Seek / 重播"，反而把播放打断。
            int dur = Duration;
            if (dur <= 0 || pos <= 0)
            {
                _stallPos = -1; _stallBeats = 0;
                return;
            }

            if (pos == _stallPos) _stallBeats++;
            else { _stallPos = pos; _stallBeats = 0; _stallRetries = 0; }

            // 约 12 秒没动：轻推一下（+1ms）触发重新缓冲
            if (_stallBeats == 24)
            {
                try { lock (_lock) { _mp?.SeekTo(pos <= 0 ? 1 : pos + 1); } } catch { }
            }
            // 约 30 秒还在原地：重新准备同一首（换源，但不跳歌）
            else if (_stallBeats >= 60)
            {
                _stallBeats = 0;
                _stallRetries++;
                if (_stallRetries > 2)
                {
                    // 同一首反复卡死才放弃，跳下一曲（队列只有 1 首就停着）。
                    // 同步听跟随端不跳歌，交给同步器换源重试。
                    _stallRetries = 0;
                    if (SuppressAutoAdvance)
                    {
                        string p = Current?.Path ?? "";
                        Fire(() => { try { PrepareFailed?.Invoke(p); } catch { } });
                        return;
                    }
                    Fire(() => { if (Queue.Count > 1) Next(auto: true); });
                    return;
                }
                Fire(() => PlayCurrent());
            }
        }

        private static void StopTick()
        {
            try { _tick?.Dispose(); } catch { }
            _tick = null;
        }

        internal static void Info(string msg)
        {
            try { Android.Util.Log.Info("MchatMusic", msg ?? ""); } catch { }
        }

        private static void RequestFocus()
        {
            try
            {
                var ctx = Application.Context;
                if (_audio == null)
                    _audio = (AudioManager)ctx.GetSystemService(Context.AudioService);
                if (_focusCb == null) _focusCb = new FocusListener();
                // 带上监听器：原来传 null，系统抢焦点时我们毫不知情，
                // 结果就是"声音被静音/被暂停但界面还显示在播"，看起来像莫名暂停。
                _audio?.RequestAudioFocus(_focusCb, Stream.Music, AudioFocus.Gain);
                RegisterNoisyReceiver(ctx);
            }
            catch { }
        }

        /// <summary>
        /// 进程启动时即可调用，提前注册耳机断开监听（不必等第一次播放）。
        /// 收到广播时若未在播放则无动作，因此提前注册完全安全。
        /// </summary>
        public static void InitNoiseListener()
        {
            try { RegisterNoisyReceiver(Application.Context); }
            catch { }
        }

        /// <summary>
        /// 注册"音频输出变为外放"广播（蓝牙耳机断开 / 有线耳机拔出时系统发出）。
        /// 应用级常驻一次即可；收到后只在音乐引擎内暂停，视频播放器不订阅此广播。
        /// </summary>
        private static void RegisterNoisyReceiver(Context ctx)
        {
            if (_noisyRegistered || ctx == null) return;
            try
            {
                _noisyReceiver = new NoisyAudioReceiver();
                var filter = new IntentFilter("android.media.AUDIO_BECOMING_NOISY");
                // AUDIO_BECOMING_NOISY 是系统保护广播，动态注册无需 RECEIVER_EXPORTED 标志，
                // 用兼容所有 API 版本的简单重载即可。
                ctx.RegisterReceiver(_noisyReceiver, filter);
                _noisyRegistered = true;
            }
            catch { }
        }

        /// <summary>收到 AUDIO_BECOMING_NOISY：暂停音乐（不自动恢复，等用户手动点播放）。</summary>
        private sealed class NoisyAudioReceiver : BroadcastReceiver
        {
            public override void OnReceive(Context context, Intent intent)
            {
                try
                {
                    if (intent == null || intent.Action != "android.media.AUDIO_BECOMING_NOISY") return;
                    if (!IsPlaying) return;
                    Pause();
                }
                catch { }
            }
        }

        /// <summary>
        /// 音频焦点变化：来电/导航播报会抢焦点，放开后自动恢复，
        /// 这样既不会被系统强行掐断，也不会留下"界面在播、实际没声"的假状态。
        /// </summary>
        private class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
        {
            public void OnAudioFocusChange(AudioFocus change)
            {
                try
                {
                    switch (change)
                    {
                        case AudioFocus.Loss:
                        case AudioFocus.LossTransient:
                            if (IsPlaying) { _pausedByFocus = true; PauseInternal(abandonFocus: false); }
                            break;

                        case AudioFocus.LossTransientCanDuck:
                            lock (_lock) { try { _mp?.SetVolume(0.2f, 0.2f); } catch { } }
                            break;

                        case AudioFocus.Gain:
                            lock (_lock) { try { _mp?.SetVolume(1f, 1f); } catch { } }
                            if (_pausedByFocus)
                            {
                                _pausedByFocus = false;
                                Resume();
                            }
                            break;
                    }
                }
                catch { }
            }
        }

        private static void AbandonFocus()
        {
            try { _audio?.AbandonAudioFocus(_focusCb); } catch { }
        }

        private static void EnsureService()
        {
            try
            {
                var ctx = Application.Context;
                var i = new Intent(ctx, typeof(MusicService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    ctx.StartForegroundService(i);
                else
                    ctx.StartService(i);
            }
            catch { }
        }
    }

    /// <summary>
    /// 音乐前台服务：通知栏显示当前曲目 + 上一首/播放暂停/下一首。
    /// 按钮通过 Service 的 OnStartCommand action 处理（无需广播）。
    /// </summary>
    [Service(Exported = false, Name = "com.Ahochat.android.MusicService")]
    public class MusicService : Service
    {
        public const string ActionPrev = "com.Ahochat.android.music.PREV";
        public const string ActionToggle = "com.Ahochat.android.music.TOGGLE";
        public const string ActionNext = "com.Ahochat.android.music.NEXT";
        public const string ActionSeek = "com.Ahochat.android.music.SEEK";
        public const string ExtraFrac = "frac";
        private const int SeekSegments = 60;

        private const string ChannelId = "Mchat_music";
        private const int NotificationId = 2004;
        private MediaSession _session;

        public static bool IsRunning { get; private set; }

        public override void OnCreate()
        {
            base.OnCreate();
            IsRunning = true;
            MusicPlayer.StateChanged += UpdateNotification;
            MusicPlayer.PositionTick += UpdateProgress;
            MusicPlayer.SongChanged += UpdateSession;
            _session = new MediaSession(this, "MchatMusic");
            _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
            _session.SetCallback(new SessionCb());
            _session.Active = true;
            UpdateSession();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var act = intent?.Action;
            if (act == ActionPrev) MusicPlayer.Prev();
            else if (act == ActionToggle) MusicPlayer.Toggle();
            else if (act == ActionNext) MusicPlayer.Next();
            else if (act == ActionSeek)
            {
                float f = intent.GetFloatExtra(ExtraFrac, 0f);
                int d = MusicPlayer.Duration;
                if (d > 0) MusicPlayer.SeekTo((int)(f * d));
            }
            StartForegroundCompat();
            UpdateNotification();
            return StartCommandResult.Sticky;
        }

        public override void OnTaskRemoved(Intent rootIntent)
        {
            // 没在播放就别赖在后台
            if (!MusicPlayer.IsPlaying) StopSelf();
            base.OnTaskRemoved(rootIntent);
        }

        public override void OnDestroy()
        {
            IsRunning = false;
            MusicPlayer.StateChanged -= UpdateNotification;
            MusicPlayer.PositionTick -= UpdateProgress;
            MusicPlayer.SongChanged -= UpdateSession;
            try { _session?.Release(); } catch { }
            base.OnDestroy();
        }

        public override IBinder OnBind(Intent intent) => null;

        /* ---------------- 通知 ---------------- */

        private RemoteViews _rv;
        private string _rvSong = "";
        private string _rvCover = "";

        private Notification.Builder BuildNotification()
        {
            var nm = (NotificationManager)GetSystemService(NotificationService);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O && nm.GetNotificationChannel(ChannelId) == null)
            {
                var ch = new NotificationChannel(ChannelId, L10n.T("音乐播放"), NotificationImportance.Low);
                ch.SetShowBadge(false);
                nm.CreateNotificationChannel(ch);
            }

            var song = MusicPlayer.Current;
            var open = PendingIntent.GetActivity(this, 0,
                new Intent(this, typeof(MusicActivity)).SetFlags(ActivityFlags.SingleTop),
                Build.VERSION.SdkInt >= BuildVersionCodes.S
                    ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                    : PendingIntentFlags.UpdateCurrent);

            var b = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(this, ChannelId)
                : new Notification.Builder(this);
            b.SetContentTitle(song?.DisplayTitle ?? L10n.T("AhoChat 音乐"))
             .SetContentText(song?.ArtistLine ?? "")
             .SetSmallIcon(Resource.Drawable.icon)
             .SetContentIntent(open)
             .SetOngoing(MusicPlayer.IsPlaying)
             .SetOnlyAlertOnce(true)
             .SetShowWhen(false);

            // 大视图：自定义（封面 + 实时进度 + 分段点按拖动条）
            // 封面是异步落盘的，CoverPath 变化时也要重建，否则在线歌曲的通知栏一直没有图
            if (_rv == null || _rvSong != (song?.Path ?? "") || _rvCover != (song?.CoverPath ?? ""))
                _rv = BuildRemoteViews(song);
            try { b.SetCustomBigContentView(_rv); } catch { }

            // 媒体样式（锁屏控制 + 让自定义大视图在 Android 12+ 正常显示）
            try { b.SetStyle(new Notification.MediaStyle().SetMediaSession(_session.SessionToken)); } catch { }

            if (song != null)
            {
                b.AddAction(MakeAction(Android.Resource.Drawable.IcMediaPrevious, "上一首", ActionPrev));
                b.AddAction(MakeAction(
                    MusicPlayer.IsPlaying ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay,
                    L10n.T("播放暂停"), ActionToggle));
                b.AddAction(MakeAction(Android.Resource.Drawable.IcMediaNext, "下一首", ActionNext));
            }
            return b;
        }

        /// <summary>判断系统当前是否处于深色模式，用于通知配色自适应。</summary>
        private bool IsSystemDark()
        {
            try
            {
                var ui = Resources.Configuration.UiMode & Android.Content.Res.UiMode.NightMask;
                return ui == Android.Content.Res.UiMode.NightYes;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>构造通知大视图（封面/标题/艺人 + 进度条 + 分段点按拖动层）。</summary>
        private RemoteViews BuildRemoteViews(Song song)
        {
            var rv = new RemoteViews(PackageName, Resource.Layout.notification_music);
            rv.SetTextViewText(Resource.Id.nfTitle, song?.DisplayTitle ?? "Mchat 音乐");
            rv.SetTextViewText(Resource.Id.nfArtist, song?.ArtistLine ?? "");
            var bmp = song != null ? MusicLibrary.LoadCover(song, 128) : null;
            // 一起听 / 同步听的曲目没有本地封面文件，用共享的远程封面缓存
            if (bmp == null && song != null) bmp = SongArt.Cached(SongArt.Url(song));
            if (bmp != null) rv.SetImageViewBitmap(Resource.Id.nfCover, bmp);
            else
            {
                // 无封面统一显示「灰底 + 音乐符号」，不再显示程序图标
                var ph = SongArt.Placeholder(true);
                if (ph != null) rv.SetImageViewBitmap(Resource.Id.nfCover, ph);
                else rv.SetImageViewResource(Resource.Id.nfCover, Resource.Drawable.icon);
            }

            int dur = MusicPlayer.Duration;
            int pos = MusicPlayer.Position;
            int max = dur > 0 ? dur : 1000;
            rv.SetProgressBar(Resource.Id.nfProgress, max, Math.Min(pos, max), false);
            rv.SetTextViewText(Resource.Id.nfElapsed, Song.FormatMs(pos));
            rv.SetTextViewText(Resource.Id.nfTotal, Song.FormatMs(dur));

            // 控制按钮：上一曲 / 播放暂停 / 下一曲
            rv.SetImageViewResource(Resource.Id.nfPlay,
                MusicPlayer.IsPlaying ? Android.Resource.Drawable.IcMediaPause
                                     : Android.Resource.Drawable.IcMediaPlay);
            rv.SetOnClickPendingIntent(Resource.Id.nfPrev, MakeControlPi(ActionPrev));
            rv.SetOnClickPendingIntent(Resource.Id.nfPlay, MakeControlPi(ActionToggle));
            rv.SetOnClickPendingIntent(Resource.Id.nfNext, MakeControlPi(ActionNext));

            // 主题自适应配色：背景透明，系统提供主题面（浅色或深色）。
            // 据此选取文字 / 图标颜色，保证在浅色通知面上也清晰可见。
            bool dark = IsSystemDark();
            int bg, title, artist, time, icon;
            if (dark)
            {
                bg = unchecked((int)0xFF121212);
                title = unchecked((int)0xFFFFFFFF);
                artist = unchecked((int)0xFFB3B3B3);
                time = unchecked((int)0xFF9A9A9A);
                icon = unchecked((int)0xFFFFFFFF);
            }
            else
            {
                bg = unchecked((int)0xFFF2F3F5);
                title = unchecked((int)0xFF1A1A1A);
                artist = unchecked((int)0xFF666666);
                time = unchecked((int)0xFF888888);
                icon = unchecked((int)0xFF333333);
            }
            rv.SetInt(Resource.Id.nfRoot, "setBackgroundColor", bg);
            rv.SetTextColor(Resource.Id.nfTitle, new Android.Graphics.Color(title));
            rv.SetTextColor(Resource.Id.nfArtist, new Android.Graphics.Color(artist));
            rv.SetTextColor(Resource.Id.nfElapsed, new Android.Graphics.Color(time));
            rv.SetTextColor(Resource.Id.nfTotal, new Android.Graphics.Color(time));
            rv.SetInt(Resource.Id.nfPrev, "setColorFilter", icon);
            rv.SetInt(Resource.Id.nfPlay, "setColorFilter", icon);
            rv.SetInt(Resource.Id.nfNext, "setColorFilter", icon);

            _rvSong = song?.Path ?? "";
            _rvCover = song?.CoverPath ?? "";
            return rv;
        }

        private PendingIntent MakeControlPi(string action)
        {
            int req = 7000 + action.GetHashCode() % 1000;
            return PendingIntent.GetService(this, req,
                new Intent(this, GetType()).SetAction(action),
                Build.VERSION.SdkInt >= BuildVersionCodes.S
                    ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                    : PendingIntentFlags.UpdateCurrent);
        }

        /// <summary>每 500ms 刷新通知里的进度（不重建拖动分段）。</summary>
        private DateTime _lastNotif = DateTime.MinValue;

        private void UpdateProgress(int pos)
        {
            try
            {
                var song = MusicPlayer.Current;
                if (song == null) return;
                int dur = MusicPlayer.Duration;
                int cur = MusicPlayer.Position;

                // 锁屏 / 蓝牙 / 车机读的是 MediaSession 的 PlaybackState。
                // 之前只在换歌时写一次，进度永远是 0，锁屏就看不到走条。
                try
                {
                    var ps = new PlaybackState.Builder()
                        .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause
                                  | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious
                                  | PlaybackState.ActionSeekTo)
                        .SetState(MusicPlayer.IsPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                            cur, MusicPlayer.Speed)
                        .Build();
                    _session?.SetPlaybackState(ps);
                }
                catch { }

                if (_rv == null) return;
                // 通知栏节流到 ~1 秒一次：PositionTick 是 500ms，每拍都重建 RemoteViews 太浪费
                var now = DateTime.UtcNow;
                if ((now - _lastNotif).TotalMilliseconds < 900) return;
                _lastNotif = now;

                int max = dur > 0 ? dur : 1000;
                _rv.SetProgressBar(Resource.Id.nfProgress, max, Math.Min(cur, max), false);
                _rv.SetTextViewText(Resource.Id.nfElapsed, Song.FormatMs(cur));
                _rv.SetTextViewText(Resource.Id.nfTotal, Song.FormatMs(dur));
                var nm = (NotificationManager)GetSystemService(NotificationService);
                nm.Notify(NotificationId, BuildNotification().Build());
            }
            catch { }
        }

        private Notification.Action MakeAction(int icon, string title, string action)
        {
            var pi = PendingIntent.GetService(this, title.GetHashCode(), 
                new Intent(this, GetType()).SetAction(action),
                Build.VERSION.SdkInt >= BuildVersionCodes.S
                    ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                    : PendingIntentFlags.UpdateCurrent);
            return new Notification.Action.Builder(icon, title, pi).Build();
        }

        private void StartForegroundCompat()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, BuildNotification().Build(),
                    ForegroundService.TypeMediaPlayback);
            else
                StartForeground(NotificationId, BuildNotification().Build());
        }

        private void UpdateNotification()
        {
            try
            {
                var nm = (NotificationManager)GetSystemService(NotificationService);
                if (!MusicPlayer.IsPlaying && MusicPlayer.Current == null)
                {
                    nm.Cancel(NotificationId);
                    StopSelf();
                    return;
                }
                nm.Notify(NotificationId, BuildNotification().Build());
            }
            catch { }
        }

        /* ---------------- MediaSession（锁屏控制） ---------------- */

        private void UpdateSession()
        {
            try
            {
                var song = MusicPlayer.Current;
                var md = new MediaMetadata.Builder();
                if (song != null)
                {
                    md.PutString(MediaMetadata.MetadataKeyTitle, song.DisplayTitle);
                    md.PutString(MediaMetadata.MetadataKeyArtist, song.ArtistLine);
                    if (song.DurationMs > 0)
                        md.PutLong(MediaMetadata.MetadataKeyDuration, song.DurationMs);
                    // 内嵌封面给锁屏
                    var bmp = MusicLibrary.LoadCover(song, 512);
                    if (bmp != null) md.PutBitmap(MediaMetadata.MetadataKeyArt, bmp);
                }
                _session.SetMetadata(md.Build());

                var ps = new PlaybackState.Builder()
                    .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause
                              | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious
                              | PlaybackState.ActionSeekTo)
                    .SetState(MusicPlayer.IsPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                        MusicPlayer.Position, MusicPlayer.Speed)
                    .Build();
                _session.SetPlaybackState(ps);
            }
            catch { }
        }

        private class SessionCb : MediaSession.Callback
        {
            public override void OnPlay() { MusicPlayer.Resume(); }
            public override void OnPause() { MusicPlayer.Pause(); }
            public override void OnSkipToNext() { MusicPlayer.Next(); }
            public override void OnSkipToPrevious() { MusicPlayer.Prev(); }
            public override void OnSeekTo(long pos) { MusicPlayer.SeekTo((int)pos); }
        }
    }
}

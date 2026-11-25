// ============================================================
// 局域网音乐播放共享（MusicCast）
//
// 场景：本机用 App 播音乐时——
//   ① 在默认频道 mesh 里维护一张「音乐卡片」：封面 + 歌词 + 上一曲/播放暂停/下一曲；
//   ② 向局域网其它设备广播播放状态（kind=mediaplayer），对端在通知栏出现同名播放器控件；
//   ③ 切歌 / 暂停 / 拖动进度时**原地更新同一张卡片**（kind=control&op=patch），
//      不再发新卡片，避免刷屏；
//   ④ 对端在通知栏或卡片上点按钮 → kind=mediacontrol（带 target）回到本机执行。
//
// 帧格式复用网页端 remote-media.js 已定义的协议，保证 App 与网页端互通：
//   mediaplayer  : state{title,artist,album,duration,position,playing,cover}
//   mediacontrol : state{cmd,position,target}
//   musiccard    : 存进聊天的消息（kind=musiccard），patch 时用同 id 覆盖
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;

namespace AhoChatA.Music{
    public static class MusicCast
    {
        public const string KindCard = "musiccard";
        public const string KindState = "mediaplayer";
        public const string KindCtrl = "mediacontrol";

        private const string ActionCmd = "Mchat.musiccast.CMD";
        private const string ExtraCmd = "cmd";
        private const int NotifId = 2005;
        private const string NotifChannel = "Mchat_remote_music";

        private const int HeartbeatMs = 10000;    // 状态心跳
        // 同步听要靠 patch 的 position 校正进度：20 秒一次太稀，对端会明显跑偏。
        // patch 帧不含封面/歌词/列表，只有几百字节。没有跟随者时 4 秒一次足够；
        // SyncClock 探测到有人在对时，就自动加密到 1.2 秒，把最大误差压到亚秒级。
        private const int CardRefreshMs = 4000;
        private const int CardRefreshSyncMs = 1200;
        private const int RemoteTimeoutMs = 35000;

        private static bool _started;
        private static Context _ctx;
        private static Handler _ui;

        /* 本机作为「播放端」 */
        private static string _cardId = "";
        private static string _lastSongKey = "";
        private static string _lastSig = "";
        private static long _lastBeatAt;
        private static long _lastCardAt;
        private static int _lastSentPos = -1;
        private static int _seq;
        private static bool _wasActive;
        private static string _coverB64 = "", _coverFor = "";
        private static string _lrcText = "", _lrcFor = "";
        private static string _sentCoverFor = "", _sentLrcFor = "";   // 已随卡片发出的封面/歌词

        /* 本机作为「受控端」（显示对端的通知栏播放器） */
        private static string _rFrom = "";
        private static string _rName = "";
        private static string _rTitle = "";
        private static string _rArtist = "";
        private static string _rCover = "";
        private static int _rDuration, _rPosition;
        private static bool _rPlaying;
        private static long _rSeenAt;
        private static Bitmap _rBitmap;

        public static bool IsRemoteActive => !string.IsNullOrEmpty(_rFrom);

        /* 供悬浮窗等外部 UI 读取的远端播放快照（只读） */
        public static string RemoteFrom => _rFrom;
        public static string RemoteName => _rName;
        public static string RemoteTitle => _rTitle;
        public static string RemoteArtist => _rArtist;
        public static string RemoteCover => _rCover;
        public static int RemoteDuration => _rDuration;
        public static int RemotePosition => _rPosition;
        public static bool RemotePlaying => _rPlaying;

        /// <summary>远端状态是否仍然新鲜（超过 35s 没收到心跳视为已停止，避免控制一台早已停播的设备）。</summary>
        public static bool HasFreshRemote
        {
            get
            {
                if (string.IsNullOrEmpty(_rFrom)) return false;
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _rSeenAt < RemoteTimeoutMs;
            }
        }

        /* ---------------- 初始化 ---------------- */

        public static void Init(Context ctx)
        {
            if (_started) return;
            _started = true;
            _ctx = ctx.ApplicationContext;
            _ui = new Handler(Looper.MainLooper);
            try
            {
                MusicPlayer.SongChanged += () => Push(force: true, newCard: _cardId.Length == 0);
                // 暂停/播放/切歌都会改变签名，force:false 也照样立即广播；进度微调交给心跳
                MusicPlayer.StateChanged += () => Push(force: false, newCard: false);
                MusicPlayer.PositionTick += _ => Tick();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "MusicCast 初始化失败: " + ex.Message);
            }
        }

        /// <summary>500ms 一次的位置回调：只做节流判断，不在这里读大文件。</summary>
        private static void Tick()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (MusicPlayer.Current == null) return;

            if (now - _lastBeatAt >= HeartbeatMs) { Push(force: true, newCard: false); return; }

            int refresh = SyncClock.HasFollowers ? CardRefreshSyncMs : CardRefreshMs;
            if (now - _lastCardAt >= refresh) { PushCard(patchOnly: true); return; }

            // 拖动进度条 / 跳转不会改变播放状态签名，旧实现要等下一次定时 patch，
            // 对端会晚 1~4 秒才跟上。这里检测到「位置偏离按上次 patch 推算的值」
            // 就立刻补一帧，把 seek 延时压到 500ms 以内。
            if (MusicPlayer.IsPlaying && _lastSentPos >= 0 && _lastCardAt > 0)
            {
                long since = Math.Min(now - _lastCardAt, 60000);
                int expect = _lastSentPos + (int)since;
                if (Math.Abs(MusicPlayer.Position - expect) > 900) PushCard(patchOnly: true);
            }
        }

        /* ---------------- 播放状态广播 + 卡片 ---------------- */

        private static void Push(bool force, bool newCard)
        {
            // PositionTick 来自定时器线程，状态事件来自主线程：统一挪到主线程，
            // 避免 Emit（要跑 WebView JS）和 UI 相关操作跨线程。
            _ui.Post(() => PushCore(force, newCard));
        }

        private static void PushCore(bool force, bool newCard)
        {
            // 同步听模式下本机是跟随者：绝不能把自己的播放状态广播出去，
            // 否则会在频道里再生成一张卡片，和发起端的卡片互相打架。
            if (SyncListen.InSync) return;
            // 待播(pending)态：只是本地底栏挑了一首「待播」歌，还没真正开播，
            // 绝不能把卡片广播出去打扰其它设备；用户首次播放后 HasStarted 才置 true。
            if (!MusicPlayer.HasStarted) return;
            try
            {
                var br = MchatBridge.Current;
                var s = MusicPlayer.Current;
                if (br == null) return;

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (s == null)
                {
                    if (_wasActive)
                    {
                        _wasActive = false;
                        _lastSig = "";
                        br.MusicCastPublish(StateOf(null, now));
                        if (_cardId.Length > 0) br.MusicCastCard(_cardId, CardOf(null, now), patch: true);
                    }
                    return;
                }

                string sig = s.Path + "|" + MusicPlayer.IsPlaying + "|" + MusicPlayer.Duration;
                bool changed = sig != _lastSig;
                if (!force && !changed && now - _lastBeatAt < HeartbeatMs) return;

                _lastSig = sig;
                _lastBeatAt = now;
                _wasActive = true;

                br.MusicCastPublish(StateOf(s, now));

                // 卡片：第一次发「新卡片」，之后一律 patch 同一个 id
                bool needNew = newCard || _cardId.Length == 0;
                br.MusicCastCard(_cardId, CardOf(s, now), patch: !needNew);
                if (needNew) _cardId = br.MusicCastLastCardId ?? "";
                _lastSentPos = MusicPlayer.Position;
                _lastCardAt = now;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "MusicCast 广播失败: " + ex.Message);
            }
        }

        private static void PushCard(bool patchOnly)
        {
            _ui.Post(() => PushCardCore(patchOnly));
        }

        private static void PushCardCore(bool patchOnly)
        {
            if (SyncListen.InSync) return;   // 跟随者不广播进度校正
            if (!MusicPlayer.HasStarted) return;   // 待播态不广播（见 PushCore 说明）
            var s = MusicPlayer.Current;
            var br = MchatBridge.Current;
            if (s == null || br == null || _cardId.Length == 0) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            br.MusicCastCard(_cardId, CardOf(s, now), patch: patchOnly);
            _lastSentPos = MusicPlayer.Position;
            _lastCardAt = now;
        }

        private static Dictionary<string, object> StateOf(Song s, long now)
        {
            var d = new Dictionary<string, object>
            {
                // ts 必须和下面的 position 同一瞬间取样：对端要用 ts 反推
                // 「这张卡片生成后已经过去多久」，差几十毫秒就够累积出可闻偏差。
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            if (s == null)
            {
                d["title"] = "";
                d["playing"] = false;
                return d;
            }
            d["title"] = s.DisplayTitle;
            d["artist"] = s.Artist ?? "";
            d["album"] = s.Album ?? "";
            d["duration"] = MusicPlayer.Duration;
            d["position"] = MusicPlayer.Position;
            d["playing"] = MusicPlayer.IsPlaying;
            var cover = CoverB64(s);
            if (!string.IsNullOrEmpty(cover)) d["cover"] = cover;
            return d;
        }

        private static Dictionary<string, object> CardOf(Song s, long now)
        {
            string key = SongKeyOf(s);
            var d = new Dictionary<string, object>
            {
                ["title"] = s?.DisplayTitle ?? "",
                ["artist"] = s?.Artist ?? "",
                ["album"] = s?.Album ?? "",
                ["duration"] = s == null ? 0 : MusicPlayer.Duration,
                ["position"] = s == null ? 0 : MusicPlayer.Position,
                ["playing"] = s != null && MusicPlayer.IsPlaying,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["seq"] = ++_seq,
                ["songKey"] = key,
                // 歌词归属：patch 合并会沿用上一首的 lrc，没有它跟随端无法分辨
                ["lrcKey"] = key,
            };

            // 封面与歌词只在「换曲」或「刚取到」时附带：
            // 否则每 20 秒的进度校正也会背上几十 KB，白白占用局域网带宽。
            var cover = CoverB64(s);
            if (!string.IsNullOrEmpty(cover) && key != _sentCoverFor)
            {
                d["cover"] = cover;
                _sentCoverFor = key;
            }
            var lrc = LrcOf(s);
            if (!string.IsNullOrEmpty(lrc) && key != _sentLrcFor)
            {
                d["lrc"] = lrc;
                _sentLrcFor = key;
            }

            // 「一起听」：随卡片附带整条播放列表（在线给代理播放链接、本地给 Range 流链接）。
            // 关闭开关时这里不加任何字段，音乐卡片照常发送与 patch 更新。
            try { ListenTogether.Enrich(d, s, key); ListenTogether.NoteSent(d); } catch { }
            return d;
        }

        private static string SongKeyOf(Song s)
        {
            if (s == null) return "";
            return s.IsOnline ? ("on:" + s.OnlinePlat + ":" + s.OnlineId) : ("local:" + s.Path);
        }

        /// <summary>封面转 base64（256px、JPEG 60，约 20~35KB），按曲目缓存，避免每次重编码。</summary>
        private static string CoverB64(Song s)
        {
            if (s == null) return "";
            string key = SongKeyOf(s);
            if (key == _coverFor && !string.IsNullOrEmpty(_coverB64)) return _coverB64;

            _coverB64 = "";
            try
            {
                Bitmap bmp = null;
                try { bmp = MusicLibrary.LoadCover(s, 256); } catch { }
                if (bmp == null) return "";

                // qaul 的 Noise 层单帧上限 65535 字节，meta 里还要塞歌词，
                // 所以封面必须压到 14KB 以内（base64 后约 19KB），否则整条卡片发不出去。
                var tries = new[] { (px: 200, q: 55), (px: 140, q: 45), (px: 96, q: 40) };
                foreach (var t in tries)
                {
                    Bitmap scaled = ScaleTo(bmp, t.px);
                    byte[] bytes = null;
                    using (var ms = new MemoryStream())
                    {
                        scaled.Compress(Bitmap.CompressFormat.Jpeg, t.q, ms);
                        bytes = ms.ToArray();
                    }
                    if (!ReferenceEquals(scaled, bmp)) { try { scaled.Recycle(); } catch { } }
                    if (bytes != null && bytes.Length > 0 && bytes.Length <= 14 * 1024)
                    {
                        _coverB64 = Convert.ToBase64String(bytes);
                        break;
                    }
                }
                try { bmp.Recycle(); } catch { }
            }
            catch (Exception ex) { Android.Util.Log.Warn("Mchat", "封面编码失败: " + ex.Message); }
            // 没拿到封面就不记住 key，等在线封面落盘后下次还能重试
            _coverFor = string.IsNullOrEmpty(_coverB64) ? "" : key;
            return _coverB64;
        }

        private static Bitmap ScaleTo(Bitmap src, int max)
        {
            if (src == null) return null;
            int w = src.Width, h = src.Height;
            if (w <= max && h <= max) return src;
            float r = Math.Min((float)max / w, (float)max / h);
            return Bitmap.CreateScaledBitmap(src, Math.Max(1, (int)(w * r)), Math.Max(1, (int)(h * r)), true);
        }

        /// <summary>歌词原文（LRC），按曲目缓存；在线歌词可能稍后才落盘，所以每次换曲都重读。</summary>
        private static string LrcOf(Song s)
        {
            if (s == null) return "";
            string key = SongKeyOf(s);
            if (key == _lrcFor && !string.IsNullOrEmpty(_lrcText)) return _lrcText;

            _lrcText = "";
            try
            {
                string txt = "";
                if (!string.IsNullOrEmpty(s.LrcPath) && File.Exists(s.LrcPath))
                    txt = File.ReadAllText(s.LrcPath, Encoding.UTF8);
                // 在线歌曲 / 共享列表里的歌词是内联 LRC，没有 .lrc 文件
                if (string.IsNullOrWhiteSpace(txt)) txt = s.LrcText ?? "";
                // 8KB 仍在 qaul 单帧预算内；以前截 4000 会让长歌词（双语/翻译）在对端缺一截
                if (txt.Length > 8000) txt = txt.Substring(0, 8000);
                _lrcText = txt;
            }
            catch { }
            _lrcFor = string.IsNullOrEmpty(_lrcText) ? "" : key;
            return _lrcText;
        }

        /// <summary>取某时刻应显示的歌词行（供卡片/通知用）。</summary>
        public static string LyricLineAt(string lrc, int positionMs)
        {
            if (string.IsNullOrEmpty(lrc)) return "";
            string best = "";
            try
            {
                foreach (var raw in lrc.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length < 5 || line[0] != '[') continue;
                    int close = line.IndexOf(']');
                    if (close <= 1) continue;
                    var stamp = line.Substring(1, close - 1);
                    int colon = stamp.IndexOf(':');
                    if (colon <= 0) continue;
                    if (!int.TryParse(stamp.Substring(0, colon), out int mm)) continue;
                    var rest = stamp.Substring(colon + 1);
                    if (!double.TryParse(rest, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double ss)) continue;
                    int at = (int)((mm * 60 + ss) * 1000);
                    if (at <= positionMs)
                    {
                        var text = line.Substring(close + 1).Trim();
                        if (text.Length > 0) best = text;
                    }
                    else break;
                }
            }
            catch { }
            return best;
        }

        /* ---------------- 受控端：接收广播 / 显示通知栏 ---------------- */

        /// <summary>收到 mediaplayer / mediacontrol 帧（原生侧入口）。</summary>
        public static void OnRemoteFrame(string kind, Dictionary<string, object> msg)
        {
            if (msg == null) return;
            try
            {
                var br = MchatBridge.Current;
                string from = Msg.Str(msg, "from");
                if (br != null && string.Equals(from, br.SelfPeerId, StringComparison.OrdinalIgnoreCase)) return;

                var state = msg.TryGetValue("state", out var sv) ? AsDict(sv) : null;
                if (state == null) return;

                if (kind == KindCtrl)
                {
                    string target = state.TryGetValue("target", out var tv) ? tv?.ToString() : null;
                    if (!string.IsNullOrEmpty(target) &&
                        br != null && string.Equals(target, br.SelfPeerId, StringComparison.OrdinalIgnoreCase))
                    {
                        ExecLocal(state.TryGetValue("cmd", out var cv) ? cv?.ToString() : null,
                                  state.TryGetValue("position", out var pv) ? ToInt(pv) : -1);
                    }
                    return;
                }

                // mediaplayer：刷新受控端的通知栏
                string title = state.TryGetValue("title", out var t) ? t?.ToString() : "";
                bool playing = state.TryGetValue("playing", out var pl) && ToBool(pl);
                if (!playing && string.IsNullOrEmpty(title))
                {
                    if (string.Equals(_rFrom, from, StringComparison.OrdinalIgnoreCase)) HideRemote();
                    return;
                }

                // 本机自己正在放歌就不接管，避免两套播放互相打架
                if (MusicPlayer.Current != null && MusicPlayer.IsPlaying) return;

                _rFrom = from;
                _rName = Msg.Str(msg, "fromName", L10n.T("局域网设备"));
                _rTitle = string.IsNullOrEmpty(title) ? L10n.T("未知歌曲") : title;
                _rArtist = state.TryGetValue("artist", out var a) ? a?.ToString() : "";
                _rDuration = state.TryGetValue("duration", out var du) ? ToInt(du) : 0;
                _rPosition = state.TryGetValue("position", out var po) ? ToInt(po) : 0;
                _rPlaying = playing;
                string cover = state.TryGetValue("cover", out var c) ? c?.ToString() : "";
                if (!string.IsNullOrEmpty(cover)) _rCover = cover;
                _rSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                ShowRemoteNotification();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "处理远端播放状态失败: " + ex.Message);
            }
        }

        private static void ShowRemoteNotification()
        {
            var ctx = _ctx ?? Application.Context;
            var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                if (nm.GetNotificationChannel(NotifChannel) == null)
                {
                    var ch = new NotificationChannel(NotifChannel, L10n.T("局域网音乐"), NotificationImportance.Low);
                    ch.SetShowBadge(false);
                    nm.CreateNotificationChannel(ch);
                }
            }

            var b = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(ctx, NotifChannel)
                : new Notification.Builder(ctx);

            b.SetContentTitle(_rTitle)
             .SetContentText(string.IsNullOrEmpty(_rArtist) ? _rName : _rArtist + L10n.T(" · 来自 ") + _rName)
             .SetSmallIcon(Resource.Drawable.icon)
             .SetOngoing(_rPlaying)
             .SetOnlyAlertOnce(true)
             .SetShowWhen(false);

            if (_rBitmap == null && !string.IsNullOrEmpty(_rCover))
            {
                try
                {
                    var bytes = Convert.FromBase64String(_rCover);
                    _rBitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                }
                catch { }
            }
            if (_rBitmap != null) b.SetLargeIcon(_rBitmap);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                b.SetProgress(_rDuration <= 0 ? 1000 : _rDuration,
                              Math.Min(_rPosition, _rDuration <= 0 ? 1000 : _rDuration), false);

            b.AddAction(Android.Resource.Drawable.IcMediaPrevious, "上一曲", Pi("prev"));
            b.AddAction(_rPlaying ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay,
                        L10n.T("播放暂停"), Pi("toggle"));
            b.AddAction(Android.Resource.Drawable.IcMediaNext, "下一曲", Pi("next"));
            b.AddAction(Android.Resource.Drawable.IcMenuCloseClearCancel, "停止", Pi("stop"));

            nm.Notify(NotifId, b.Build());
        }

        private static PendingIntent Pi(string cmd)
        {
            var ctx = _ctx ?? Application.Context;
            var i = new Intent(ctx, typeof(RemoteMusicReceiver));
            i.SetAction(ActionCmd);
            i.PutExtra(ExtraCmd, cmd);
            int req = 8100 + (cmd ?? "").GetHashCode() % 100;
            return PendingIntent.GetBroadcast(ctx, req, i,
                Build.VERSION.SdkInt >= BuildVersionCodes.S
                    ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                    : PendingIntentFlags.UpdateCurrent);
        }

        private static void HideRemote()
        {
            try
            {
                var ctx = _ctx ?? Application.Context;
                var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService);
                nm.Cancel(NotifId);
            }
            catch { }
            _rFrom = "";
            _rCover = "";
            try { _rBitmap?.Recycle(); } catch { }
            _rBitmap = null;
        }

        /// <summary>受控端通知栏/卡片上的按钮：把指令发回真正的播放端。</summary>
        public static void SendCommand(string cmd, int position = -1, string target = null)
        {
            try
            {
                if (string.IsNullOrEmpty(target)) target = _rFrom;
                if (string.IsNullOrEmpty(target)) return;
                MchatBridge.Current?.MusicCastSendCommand(cmd, position, target);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "发送播放指令失败: " + ex.Message);
            }
        }

        /// <summary>本机作为播放端收到指令：真正执行到内置播放器。</summary>
        private static void ExecLocal(string cmd, int position)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            switch (cmd)
            {
                case "play": MusicPlayer.Resume(); MusicPlayer.EnsurePlaying(); break;
                case "pause": MusicPlayer.Pause(); break;
                case "toggle": MusicPlayer.Toggle(); break;
                // 切歌后必须自动播放（远端点下一曲/上一曲却停在暂停态是最容易被投诉的问题）
                case "next": MusicPlayer.Next(); MusicPlayer.EnsurePlaying(); break;
                case "prev": MusicPlayer.Prev(); MusicPlayer.EnsurePlaying(); break;
                case "stop": MusicPlayer.StopAll(); break;
                case "seek": if (position >= 0) MusicPlayer.SeekTo(position); MusicPlayer.EnsurePlaying(300); break;
            }
            // 执行后立刻广播一次，让所有受控端马上校准
            Push(force: true, newCard: false);
        }

        /* ---------------- 工具 ---------------- */

        private static Dictionary<string, object> AsDict(object o)
        {
            if (o is Dictionary<string, object> d) return d;
            if (o is Newtonsoft.Json.Linq.JObject jo)
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(jo.ToString());
            return null;
        }

        private static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o); } catch { return 0; }
        }

        private static bool ToBool(object o)
        {
            if (o == null) return false;
            if (o is bool b) return b;
            return string.Equals(o.ToString(), "true", StringComparison.OrdinalIgnoreCase) || o.ToString() == "1";
        }
    }

    /// <summary>通知栏按钮的接收器：把命令发回播放端。</summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class RemoteMusicReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try
            {
                var cmd = intent?.GetStringExtra("cmd");
                if (string.IsNullOrEmpty(cmd)) return;
                if (cmd == "stop")
                {
                    MusicCast.SendCommand("stop");
                    return;
                }
                MusicCast.SendCommand(cmd);
            }
            catch { }
        }
    }
}

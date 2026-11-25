// ============================================================
// 音乐卡片（聊天里的 musiccard 气泡）
//
// 由播放端通过 mesh 频道下发，字段来自帧里的扁平数据：
//   title / artist / album / duration / position / playing / ts / lrc / cover(base64) / from
//
// 卡片自己带 1 秒心跳：位置与歌词行在本地推进，所以对端不必为「歌词走到下一句」
// 频繁收包；播放端只在切歌 / 暂停 / 进度校正时 patch 同一张卡片即可。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    internal sealed class MusicCardView : LinearLayout
    {
        private readonly int _accent, _textMain, _textSub, _line;
        private readonly float _density;

        private readonly ImageView _cover;
        private readonly TextView _title, _artist, _lyric, _time, _from;
        private readonly IconView _btnPlay;
        private readonly ProgressBar _bar;
        private readonly Handler _handler = new Handler(Looper.MainLooper);
        private bool _ticking;

        private string _songKey = "";
        private int _duration, _position;
        private bool _playing;
        private long _ts;
        /// <summary>
        /// 收到本帧时的「本地单调时钟」（SystemClock.ElapsedRealtime）。
        /// 进度推算**只用它**，绝不用发送端的 UTC 时间戳：
        /// 两台设备的系统时钟一旦有几秒偏差，用 ts 做差会让进度整体偏快/偏慢，
        /// 而且偏差会一直存在（就是「进度比实际慢一点」的根源）。
        /// 本地单调时钟只受网络单程延迟影响（局域网内几十毫秒），且不会随对时跳变。
        /// </summary>
        private long _baseAtElapsed;
        /// <summary>数据版本号：每次 Apply 自增，详情页据此判断需不需要刷新内容。</summary>
        private int _version;
        private string _lrc = "";
        private string _coverB64 = "";
        private string _peerId = "";
        private Bitmap _coverBitmap;
        private readonly TextView _joinBtn, _syncBtn;
        private string _playlist = "";
        private int _ltIndex;
        private string _cardId = "";
        private IDictionary<string, object> _lastMsg;

        private int Dp(int v) => (int)(v * _density);

        public MusicCardView(Context ctx, IDictionary<string, object> msg) : base(ctx)
        {
            _density = Resources.DisplayMetrics.Density;
            _accent = unchecked((int)0xFF12B7F5);
            _textMain = unchecked((int)0xFF111111);
            _textSub = unchecked((int)0xFF8E9099);
            _line = unchecked((int)0xFFE8EAEE);

            Orientation = Orientation.Vertical;
            SetPadding(Dp(12), Dp(10), Dp(12), Dp(8));

            var bg = new Android.Graphics.Drawables.GradientDrawable();
            bg.SetColor(unchecked((int)0xFFFFFFFF));
            bg.SetCornerRadius(Dp(14));
            bg.SetStroke(Dp(1), new Color(_line));
            Background = bg;
            Elevation = Dp(1);

            /* 顶部：封面 + 歌名/歌手 */
            var top = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            AddView(top);

            var coverBox = new FrameLayout(ctx);
            coverBox.LayoutParameters = new LayoutParams(Dp(54), Dp(54));
            _cover = new ImageView(ctx);
            _cover.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _cover.SetScaleType(ImageView.ScaleType.CenterCrop);
            var cbg = new Android.Graphics.Drawables.GradientDrawable();
            cbg.SetColor(unchecked((int)0xFFEFF1F4));
            cbg.SetCornerRadius(Dp(10));
            _cover.Background = cbg;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                // 封面本身裁成圆角（否则 centerCrop 的方图会盖住外层圆角）
                _cover.OutlineProvider = new RoundProvider(Dp(10));
                _cover.ClipToOutline = true;
            }
            coverBox.AddView(_cover);
            // 无封面先给「灰底 + 双音符」占位，别留白、也别显示程序图标
            try { _cover.SetImageBitmap(MusicActivity.PlaceholderBitmap(true)); } catch { }
            top.AddView(coverBox);

            var info = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            info.LayoutParameters = new LayoutParams(0, -2, 1f) { LeftMargin = Dp(10) };
            _title = new TextView(ctx) { TextSize = 14 };
            _title.SetTextColor(new Color(_textMain));
            _title.SetSingleLine(true);
            _title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _artist = new TextView(ctx) { TextSize = 11 };
            _artist.SetTextColor(new Color(_textSub));
            _artist.SetSingleLine(true);
            _artist.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            info.AddView(_title);
            info.AddView(_artist);
            top.AddView(info);

            /* 歌词（本地按进度推进） */
            _lyric = new TextView(ctx) { TextSize = 12 };
            _lyric.SetTextColor(new Color(_accent));
            _lyric.SetSingleLine(true);
            _lyric.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _lyric.SetPadding(0, Dp(8), 0, Dp(4));
            _lyric.Text = "";
            AddView(_lyric);

            /* 进度条 */
            _bar = new ProgressBar(ctx, null, Android.Resource.Attribute.ProgressBarStyleHorizontal);
            _bar.Max = 1000;
            _bar.Progress = 0;
            var barLp = new LayoutParams(-1, Dp(3)) { TopMargin = Dp(2) };
            AddView(_bar, barLp);

            /* 控制行 */
            var row = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(4), 0, 0);
            AddView(row);

            row.AddView(MakeIconBtn(IconKind.Prev, () => Send("prev")));
            _btnPlay = MakeIconBtn(IconKind.Play, () => Send("toggle"));
            row.AddView(_btnPlay);
            row.AddView(MakeIconBtn(IconKind.Next, () => Send("next")));

            _time = new TextView(ctx) { TextSize = 10 };
            _time.SetTextColor(new Color(_textSub));
            _time.LayoutParameters = new LayoutParams(0, -2, 1f) { LeftMargin = Dp(8) };
            row.AddView(_time);

            _from = new TextView(ctx) { TextSize = 10 };
            _from.SetTextColor(new Color(_textSub));
            _from.SetSingleLine(true);
            row.AddView(_from);

            // 与视频卡片对齐：第一个按钮「播放」= 装歌单各播各的（视频卡片的「观看」），
            // 第二个按钮「一起听」= 严格跟随发起端进度（视频卡片的「一起看」）。
            _joinBtn = ShareBtn(L10n.T("播放"), unchecked((int)0xFFE8F7FE), _accent);
            _syncBtn = ShareBtn(L10n.T("一起听"), unchecked((int)0xFFEAFAF1), unchecked((int)0xFF12A05C));
            _joinBtn.Click += (s, e) =>
            {
                try
                {
                    bool ok = SyncListen.StartPlaylist(_playlist, _ltIndex, _cardId, _peerId);
                    L10n.Toast(Context, ok ? L10n.T("已播放共享歌单（可自由切歌）") : L10n.T("列表不可用"),
                        ToastLength.Short).Show();
                    RefreshShareButtons();
                }
                catch { }
            };
            _syncBtn.Click += (s, e) =>
            {
                try
                {
                    if (SyncListen.IsSyncing(_cardId))
                    {
                        SyncListen.Exit();
                        L10n.Toast(Context, L10n.T("已退出一起听"), ToastLength.Short).Show();
                    }
                    else
                    {
                        bool ok = SyncListen.StartSync(_playlist, _ltIndex, _cardId, _peerId, _lastMsg);
                        L10n.Toast(Context, ok ? L10n.T("已加入一起听，正在对齐进度…") : L10n.T("列表不可用"),
                            ToastLength.Short).Show();
                    }
                    RefreshShareButtons();
                }
                catch { }
            };
            var joinRow = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            joinRow.SetGravity(GravityFlags.Right);
            joinRow.SetPadding(0, Dp(4), 0, 0);
            joinRow.AddView(_joinBtn);
            var gap = new View(ctx);
            gap.LayoutParameters = new LayoutParams(Dp(6), 1);
            joinRow.AddView(gap);
            joinRow.AddView(_syncBtn);
            AddView(joinRow);

            _cover.Click += (s, e) => ShowDetail();
            var openDetail = new EventHandler((s, e) => ShowDetail());
            _title.Click += openDetail;
            _artist.Click += openDetail;

            Apply(msg);
        }

        /// <summary>
        /// 点封面/歌名打开详情页：大封面 + 歌名歌手 + 滚动歌词。
        /// 本机不出声，歌词按「播放端的进度 + 本地时钟」推算，和真实播放设备同步。
        /// </summary>
        private void ShowDetail()
        {
            try
            {
                var ctx = Context;
                var root = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
                root.SetPadding(Dp(20), Dp(18), Dp(20), Dp(10));

                var art = new ImageView(ctx);
                var lp = new LinearLayout.LayoutParams(Dp(180), Dp(180));
                lp.Gravity = GravityFlags.CenterHorizontal;
                art.LayoutParameters = lp;
                art.SetScaleType(ImageView.ScaleType.CenterCrop);
                try
                {
                    if (!string.IsNullOrEmpty(_coverB64))
                    {
                        var bytes = Convert.FromBase64String(_coverB64);
                        art.SetImageBitmap(BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length));
                    }
                }
                catch { }
                root.AddView(art);

                var t = new TextView(ctx) { Text = _title.Text, TextSize = 18, Gravity = GravityFlags.Center };
                t.SetTextColor(new Color(_textMain));
                t.SetPadding(0, Dp(12), 0, Dp(2));
                root.AddView(t);

                var a = new TextView(ctx) { Text = _artist.Text, TextSize = 12, Gravity = GravityFlags.Center };
                a.SetTextColor(new Color(_textSub));
                root.AddView(a);

                var info = new TextView(ctx) { TextSize = 11, Gravity = GravityFlags.Center };
                info.SetTextColor(new Color(_textSub));
                info.SetPadding(0, Dp(6), 0, Dp(8));
                root.AddView(info);

                var scroll = new ScrollView(ctx);
                scroll.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(240));
                var panel = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
                panel.SetPadding(0, Dp(4), 0, Dp(20));
                scroll.AddView(panel);
                root.AddView(scroll);

                // 歌词/标题/封面都要能跟着「播放端切歌」实时变。
                // 以前这里是打开瞬间的快照，切歌后详情页一直显示旧内容，必须关掉重开。
                var lines = new List<LrcRow>();
                var views = new List<TextView>();
                int shownVer = -1;
                int lastIdx = -2;

                Action rebuild = () =>
                {
                    try
                    {
                        t.Text = _title.Text;
                        a.Text = _artist.Text;
                        try
                        {
                            if (!string.IsNullOrEmpty(_coverB64))
                            {
                                var bytes = Convert.FromBase64String(_coverB64);
                                var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                                if (bmp != null) art.SetImageBitmap(bmp);
                            }
                            else art.SetImageDrawable(null);
                        }
                        catch { }

                        lines = ParseLrc(_lrc);
                        views.Clear();
                        panel.RemoveAllViews();
                        if (lines.Count == 0)
                        {
                            var empty = new TextView(ctx) { Text = L10n.T("暂无歌词"), TextSize = 13, Gravity = GravityFlags.Center };
                            empty.SetTextColor(new Color(_textSub));
                            empty.SetPadding(0, Dp(20), 0, 0);
                            panel.AddView(empty);
                        }
                        foreach (var ln in lines)
                        {
                            var tv = new TextView(ctx) { Text = ln.Text, TextSize = 14, Gravity = GravityFlags.Center };
                            tv.SetTextColor(new Color(_textSub));
                            tv.SetPadding(0, Dp(6), 0, Dp(6));
                            panel.AddView(tv);
                            views.Add(tv);
                        }
                        lastIdx = -2;   // 强制重新高亮
                        try { scroll.ScrollTo(0, 0); } catch { }
                    }
                    catch { }
                };

                var dlg = L10n.Dialog(ctx)
                    .SetView(root)
                    .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                    .Create();
                dlg.Show();

                // 先把「当前最新的」信息渲染出来（标题/封面/歌词），再开始同步进度：
                // 这样打开瞬间不会闪一下旧内容，进度也不会基于过期数据起算。
                shownVer = _version;
                rebuild();

                var handler = new Handler(Looper.MainLooper);
                Action step = null;
                step = () =>
                {
                    try
                    {
                        if (dlg == null || !dlg.IsShowing) return;

                        // 数据变了（切歌 / 暂停 / 歌词后到）就把详情内容整体重建一次
                        if (_version != shownVer) { shownVer = _version; rebuild(); }

                        int pos = CurrentPos();
                        info.Text = Format(pos) + " / " + Format(_duration)
                                  + (_playing ? " · " + _from.Text : L10n.T(" · 已暂停"));

                        int idx = -1;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (lines[i].TimeMs <= pos + 200) idx = i; else break;
                        }
                        if (idx != lastIdx)
                        {
                            lastIdx = idx;
                            for (int i = 0; i < views.Count; i++)
                            {
                                bool on = i == idx;
                                views[i].SetTextColor(new Color(on ? _accent : _textSub));
                                views[i].TextSize = on ? 16 : 14;
                            }
                            if (idx >= 0 && idx < views.Count)
                            {
                                var v = views[idx];
                                scroll.Post(() => { try { scroll.SmoothScrollTo(0, Math.Max(0, v.Top - Dp(80))); } catch { } });
                            }
                        }
                    }
                    catch { }
                    // 500ms 刷新：进度和歌词换句都更跟手，开销可忽略
                    handler.PostDelayed(step, 500);
                };
                handler.PostDelayed(step, 120);
                dlg.SetOnDismissListener(new DismissCb(() => { try { handler.RemoveCallbacksAndMessages(null); } catch { } }));
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "打开音乐详情失败: " + ex.Message);
            }
        }

        private sealed class DismissCb : Java.Lang.Object, IDialogInterfaceOnDismissListener
        {
            private readonly Action _a;
            public DismissCb(Action a) { _a = a; }
            public void OnDismiss(IDialogInterface dialog) { try { _a(); } catch { } }
        }

        private struct LrcRow { public int TimeMs; public string Text; }

        private static List<LrcRow> ParseLrc(string lrc)
        {
            var list = new List<LrcRow>();
            if (string.IsNullOrEmpty(lrc)) return list;
            foreach (var raw in lrc.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 5 || line[0] != '[') continue;
                int close = line.IndexOf(']');
                if (close <= 1) continue;
                var stamp = line.Substring(1, close - 1);
                int colon = stamp.IndexOf(':');
                if (colon <= 0) continue;
                int mm;
                if (!int.TryParse(stamp.Substring(0, colon), out mm)) continue;
                double ss;
                if (!double.TryParse(stamp.Substring(colon + 1), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out ss)) continue;
                var text = line.Substring(close + 1).Trim();
                if (text.Length == 0) continue;
                list.Add(new LrcRow { TimeMs = (int)((mm * 60 + ss) * 1000), Text = text });
            }
            list.Sort((x, y) => x.TimeMs.CompareTo(y.TimeMs));
            return list;
        }

        private TextView ShareBtn(string text, int bgArgb, int fgArgb)
        {
            var b = new TextView(Context) { Text = text, TextSize = 11, Gravity = GravityFlags.Center };
            b.SetTextColor(new Color(fgArgb));
            var bg = new Android.Graphics.Drawables.GradientDrawable();
            bg.SetColor(bgArgb);
            bg.SetCornerRadius(Dp(10));
            b.Background = bg;
            b.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
            b.Visibility = ViewStates.Gone;
            return b;
        }

        private void RefreshShareButtons()
        {
            try
            {
                bool has = _playlist.Length > 0;
                _joinBtn.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
                _syncBtn.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
                bool syncing = SyncListen.IsSyncing(_cardId);
                _syncBtn.Text = syncing ? L10n.T("退出一起听") : L10n.T("一起听");
                _syncBtn.SetTextColor(new Color(syncing
                    ? unchecked((int)0xFFD9534F) : unchecked((int)0xFF12A05C)));
            }
            catch { }
        }

        private IconView MakeIconBtn(IconKind kind, Action click)
        {
            var ic = new IconView(Context, kind) { IconColor = _textMain, Thickness = 1.7f };
            ic.LayoutParameters = new LayoutParams(Dp(40), Dp(34));
            ic.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
            ic.Click += (s, e) => { try { click(); } catch { } };
            return ic;
        }

        private void Send(string cmd)
        {
            try { AhoChatA.Music.MusicCast.SendCommand(cmd, -1, _peerId); } catch { }
        }

        /// <summary>用新的消息体刷新这张卡片（切歌 / 暂停 / 进度校正都走这里）。</summary>
        public void Apply(IDictionary<string, object> msg)
        {
            if (msg == null) return;
            _peerId = Msg.Str(msg, "from", _peerId);
            _cardId = Msg.Str(msg, "id", _cardId);
            try { _lastMsg = new Dictionary<string, object>(msg); } catch { }
            var fromName = Msg.Str(msg, "fromName", "");
            _from.Text = string.IsNullOrEmpty(fromName) ? L10n.T("局域网") : L10n.T("来自 ") + fromName;

            _title.Text = Msg.Str(msg, "title", L10n.T("未知歌曲"));
            var artist = Msg.Str(msg, "artist", "");
            var album = Msg.Str(msg, "album", "");
            _artist.Text = string.IsNullOrEmpty(album) ? artist : (artist + " · " + album);

            _duration = (int)Msg.Num(msg, "duration");
            _position = (int)Msg.Num(msg, "position");
            _playing = Msg.Bool(msg, "playing");
            _ts = Msg.Num(msg, "ts");
            if (_ts <= 0) _ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 进度基准：以「本机收到这帧的时刻」为 0 点，用本地单调时钟往后推。
            // position 与播放在同一时刻采样，因此这里不需要再去减网络延迟。
            _baseAtElapsed = SystemClock.ElapsedRealtime();

            var lrc = Msg.Str(msg, "lrc", "");
            if (!string.IsNullOrEmpty(lrc)) _lrc = lrc;

            // 「一起听」列表（patch 帧不带列表时沿用已有的那份）
            var pl = Msg.Str(msg, "playlist", "");
            if (!string.IsNullOrEmpty(pl)) _playlist = pl;
            if (Msg.Num(msg, "ltIndex") > 0 || msg.ContainsKey("ltIndex"))
                _ltIndex = (int)Msg.Num(msg, "ltIndex");
            RefreshShareButtons();

            var songKey = Msg.Str(msg, "songKey", _title.Text ?? "");
            if (!string.Equals(songKey, _songKey, StringComparison.Ordinal))
            {
                _songKey = songKey;
                _lrc = lrc ?? "";   // 换曲时重置换行，别把上一首的歌词带过来
            }

            var cover = Msg.Str(msg, "cover", "");
            if (!string.IsNullOrEmpty(cover) && cover != _coverB64)
            {
                _coverB64 = cover;
                LoadCover(cover);
            }

            _btnPlay.Kind = _playing ? IconKind.Pause : IconKind.Play;
            _version++;
            UpdateNow();
            StartTick();
        }

        /// <summary>
        /// 当前进度：帧里的 position + 「收到帧之后经过的本地时间」。
        /// 暂停时直接停在 position，不再累加。
        /// </summary>
        private int CurrentPos()
        {
            int pos = _position;
            if (_playing)
            {
                long d = SystemClock.ElapsedRealtime() - _baseAtElapsed;
                if (d > 0 && d < 6 * 60 * 60 * 1000) pos += (int)d;
            }
            if (_duration > 0 && pos > _duration) pos = _duration;
            return pos < 0 ? 0 : pos;
        }

        private void LoadCover(string b64)
        {
            try
            {
                var bytes = Convert.FromBase64String(b64);
                var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bmp == null) return;
                try { _coverBitmap?.Recycle(); } catch { }
                _coverBitmap = bmp;
                _cover.SetImageBitmap(bmp);
            }
            catch { }
        }

        private void StartTick()
        {
            if (_ticking) return;
            _ticking = true;
            _handler.PostDelayed(Tick, 500);
        }

        private void Tick()
        {
            try
            {
                if (!IsAttachedToWindow) { _ticking = false; return; }
                UpdateNow();
            }
            catch { }
            // 500ms 一拍：进度条更平滑，且不会像 1 秒一拍那样「看起来总慢半拍」
            if (_ticking) _handler.PostDelayed(Tick, 500);
        }

        private void UpdateNow()
        {
            int pos = CurrentPos();

            _time.Text = Format(pos) + " / " + Format(_duration);
            if (_duration > 0) _bar.Progress = (int)(pos / (double)_duration * 1000);

            var line = AhoChatA.Music.MusicCast.LyricLineAt(_lrc, pos);
            _lyric.Text = string.IsNullOrEmpty(line) ? (_playing ? L10n.T("正在播放") : L10n.T("已暂停")) : line;
        }

        private static string Format(int ms)
        {
            if (ms <= 0) return "00:00";
            var t = TimeSpan.FromMilliseconds(ms);
            return (int)t.TotalHours > 0
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        /// <summary>把子 View 裁成圆角（封面用）。</summary>
        private sealed class RoundProvider : ViewOutlineProvider
        {
            private readonly int _r;
            public RoundProvider(int radius) { _r = radius; }
            public override void GetOutline(View view, Android.Graphics.Outline outline)
            {
                try { outline.SetRoundRect(0, 0, view.Width, view.Height, _r); } catch { }
            }
        }

        protected override void OnDetachedFromWindow()
        {
            _ticking = false;
            try { _handler.RemoveCallbacksAndMessages(null); } catch { }
            try { _coverBitmap?.Recycle(); } catch { }
            _coverBitmap = null;
            base.OnDetachedFromWindow();
        }
    }
}

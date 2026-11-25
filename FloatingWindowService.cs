using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using Android.Runtime;
using AndroidX.Core.App;
using AhoChatA.Music;

namespace AhoChatA
{
    /// <summary>
    /// 聊天悬浮窗。
    ///
    /// 需要系统「悬浮窗」权限（SYSTEM_ALERT_WINDOW，设置页可手动授予）。
    /// 功能：
    ///   1) 常驻小浮标：有新消息时显示未读徽标，点击展开面板；
    ///   2) 展开面板：最近消息预览、输入框快速收发消息、五个媒体控制键
    ///      （上一曲/播放暂停/下一曲/音量加/音量减，逻辑与 App 内「功能控制」一致）；
    ///   3) 机器人状态一键切换；
    ///   4) 浮标可任意拖拽；面板与 App 主界面共享同一份消息历史（MchatBridge）。
    ///
    /// 生命周期协同：浮窗运行期间即使退出 App 主界面，桥接（TCP/UDP 收发）与
    /// 前台服务也保持存活，从而真正实现「不打开 App 也能预览和收发消息」；
    /// 浮窗关闭且 App 已退出时，整体收尾停止。
    /// </summary>
    [Service(Exported = false, Name = "com.ztwl.FloatingWindowService")]
    public class FloatingWindowService : Service, View.IOnTouchListener
    {
        public const string ChannelId = "Mchat_float";
        private const int NotificationId = 2002;

        public const string ActionShow = "com.ztwl.action.FLOAT_SHOW";
        public const string ActionHide = "com.ztwl.action.FLOAT_HIDE";
        public const string ActionToggle = "com.ztwl.action.FLOAT_TOGGLE";
        public const string ExtraText = "text";

        /// <summary>浮窗是否正在运行（MainActivity 退出时据此决定是否保留后台桥接）。</summary>
        public static bool IsRunning { get; private set; }

        private const int MaxShowMessages = 10;

        private Android.Views.IWindowManager wm;
        private View root;
        private WindowManagerLayoutParams lp;

        private View collapsedBar;
        private View panel;
        private TextView badge;
        private ScrollView scroll;
        private LinearLayout msgList;
        private EditText input;

        // 上半：聊天窗口（频道切换 + 消息 + 输入）；下半：指令窗口（可单独收起）
        private LinearLayout convList;
        private LinearLayout cmdRow;
        private TextView cmdToggle;
        private bool cmdCollapsed;
        private string floatConv;                 // 浮窗当前会话

        private bool expanded;
        private bool dragging;
        private int initX, initY;
        private float touchX, touchY;
        private long touchDownAt;
        private int unread;

        // 缩小图标与展开面板各自独立的位置。
        // 以前两者共用一个 lp.X/Y，于是「图标拖到右下角 → 展开后整个面板被挤到屏幕外」，
        // 用户也说不该联动。现在分开记，展开/收起各自回到自己的位置。
        private int iconX, iconY;
        private int panelX = -1, panelY = -1;     // -1 = 还没定过，展开时按默认位置算
        private const string PrefPos = "floatPos";

        private System.Timers.Timer refreshTimer;

        // 浮窗内嵌的音乐播放条（本机 / 其它设备二选一控制）
        private View _musicBar;
        private ImageView _musicCover;
        private TextView _musicTitle, _musicSub, _musicPrev, _musicPlay, _musicNext;
        private Handler _musicUi;
        private Java.Lang.Runnable _musicTick;
        private string _musicCoverKey = "";
        private bool _musicRemoteTarget;
        private bool _musicIconInit, _musicIconPlaying;

        public override IBinder OnBind(Intent intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            IsRunning = true;
            EnsureChannel();
            StartForegroundCompat();
            BuildWindow();

            var bridge = MchatBridge.Current;
            if (bridge != null) bridge.HistoryChanged += OnHistoryChanged;

            // 轮询兜底：事件万一漏掉也会刷新预览。
            // 2.5s → 6s：悬浮窗本身有 HistoryChanged 事件驱动刷新，
            // 轮询只是兜底，太密的话等于每 2.5 秒强制唤醒一次 UI 线程重建整列视图。
            refreshTimer = new System.Timers.Timer(6000);
            refreshTimer.Elapsed += (s, e) => RefreshMessages();
            refreshTimer.AutoReset = true;
            refreshTimer.Start();

            RefreshMessages();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var action = intent?.Action;
            switch (action)
            {
                case ActionHide:
                    StopSelf();
                    return StartCommandResult.NotSticky;
                case ActionToggle:
                    TogglePanel();
                    break;
            }

            // 外部（如通知栏快捷输入）直接带文本发送
            var text = intent?.GetStringExtra(ExtraText);
            if (!string.IsNullOrEmpty(text))
            {
                SendText(text);
                ExpandPanel();
            }

            StartForegroundCompat();
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            IsRunning = false;
            try { refreshTimer?.Stop(); refreshTimer?.Dispose(); } catch { }
            refreshTimer = null;

            var bridge = MchatBridge.Current;
            if (bridge != null) bridge.HistoryChanged -= OnHistoryChanged;

            // 音乐条定时器 / 播放器事件解绑
            try
            {
                if (_musicUi != null && _musicTick != null) _musicUi.RemoveCallbacks(_musicTick);
                _musicTick = null;
                _musicUi = null;
                MusicPlayer.StateChanged -= OnFloatMusicChanged;
                MusicPlayer.SongChanged -= OnFloatMusicChanged;
            }
            catch { }

            try { if (root != null) wm?.RemoveView(root); } catch { }
            root = null;

            // App 界面已全部退出：浮窗一关，桥接与前台服务整体收尾
            // （原生聊天界面 ChatActivity 存活即算 App 还开着）
            if (!ChatActivity.IsAlive)
            {
                ChatForegroundService.Stop(this);
                try { MchatBridge.Current?.Shutdown(); } catch { }
            }
            base.OnDestroy();
        }

        /* ===================== 窗口构建 ===================== */

        /// <summary>在应用主线程（UI 线程）执行，Service 没有 Activity 的 RunOnUiThread，改用主 Looper 的 Handler。</summary>
        private void PostUi(Action action)
        {
            if (action == null) return;
            new Handler(Looper.MainLooper).Post(action);
        }

        private void BuildWindow()
        {
            wm = GetSystemService(WindowService).JavaCast<Android.Views.IWindowManager>();
            root = LayoutInflater.From(this).Inflate(Resource.Layout.floating_panel, null);

            Android.Views.WindowManagerTypes wmType = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? (Android.Views.WindowManagerTypes)2038   // TypeApplicationOverlay
                : (Android.Views.WindowManagerTypes)2002;  // TypePhone

            lp = new WindowManagerLayoutParams(
                WindowManagerLayoutParams.WrapContent,
                WindowManagerLayoutParams.WrapContent,
                wmType,
                // NotTouchModal 必须加：否则这个悬浮窗是「触摸模态」的，
                // 展开后屏幕其余部分的手势全被它吃掉（用户反馈：展开后只能点浮窗本身）。
                WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutNoLimits,
                (Android.Graphics.Format)(-3));  // Translucent
            lp.Gravity = GravityFlags.Top | GravityFlags.Start;

            RestorePos();
            lp.X = iconX;
            lp.Y = iconY;

            root.SetOnTouchListener(this);

            collapsedBar = root.FindViewById(Resource.Id.floatCollapsed);
            panel = root.FindViewById(Resource.Id.floatPanel);
            badge = root.FindViewById<TextView>(Resource.Id.floatBadge);
            scroll = root.FindViewById<ScrollView>(Resource.Id.floatScroll);
            msgList = root.FindViewById<LinearLayout>(Resource.Id.floatMsgList);
            input = root.FindViewById<EditText>(Resource.Id.floatInput);
            convList = root.FindViewById<LinearLayout>(Resource.Id.floatConvList);
            cmdRow = root.FindViewById<LinearLayout>(Resource.Id.floatCmdRow);
            cmdToggle = root.FindViewById<TextView>(Resource.Id.floatCmdToggle);

            floatConv = MchatBridge.Current != null ? MchatBridge.Current.ActiveConv : null;

            // 悬浮窗图标：用代码把直角图标裁成圆角（不依赖图标资源本身是否带圆角），
            // 这样即使换成直角 PNG，显示出来也是圆角的。
            try
            {
                var iconView = root.FindViewById<ImageView>(Resource.Id.floatIcon);
                ApplyRoundedCorners(iconView, 10);
            }
            catch { }

            root.FindViewById<TextView>(Resource.Id.floatCollapse).Click += (s, e) => CollapsePanel();
            root.FindViewById<TextView>(Resource.Id.floatClose).Click += (s, e) => StopSelf();
            root.FindViewById<TextView>(Resource.Id.floatSend).Click += (s, e) => OnSendClick();

            // 指令窗口单独收起/展开（聊天窗口不受影响，只能整体收起或关闭浮窗）
            if (cmdToggle != null)
                cmdToggle.Click += (s, e) => SetCmdCollapsed(!cmdCollapsed);

            RefreshFloatConvs();

            input.EditorAction += (s, e) =>
            {
                if (e.ActionId == Android.Views.InputMethods.ImeAction.Send)
                {
                    OnSendClick();
                    e.Handled = true;
                }
            };

            // 媒体控制键：与 App 内「功能控制」同一条执行链
            // （内置播放器优先，否则控制系统媒体会话 / 音量）
            BindMediaKey(Resource.Id.floatPrev, "上一曲");
            BindMediaKey(Resource.Id.floatPlay, "播放暂停");
            BindMediaKey(Resource.Id.floatNext, "下一曲");
            BindMediaKey(Resource.Id.floatVolUp, "音量加");
            BindMediaKey(Resource.Id.floatVolDown, "音量减");

            // 媒体控制 / 音量 / 关闭：用矢量图标替代 ⏮ ▶❚❚ ⏭ 🔊 🔉 ✕ 字符（避免各 ROM 渲染不一致）
            try
            {
                SetPanelButton(Resource.Id.floatClose, IconKind.Close, "");
                SetPanelButton(Resource.Id.floatPrev, IconKind.Prev, "");
                SetPanelButton(Resource.Id.floatPlay, IconKind.Play, "");
                SetPanelButton(Resource.Id.floatNext, IconKind.Next, "");
                SetPanelButton(Resource.Id.floatVolUp, IconKind.Speaker, "+");
                SetPanelButton(Resource.Id.floatVolDown, IconKind.Speaker, "−");
            }
            catch { }

            // 音乐播放条：嵌入浮窗（封面 + 标题 + 上一曲/播放暂停/下一曲）
            InitMusicBar();

            try { wm.AddView(root, lp); }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FloatWin", $"添加浮窗失败(可能未授权悬浮窗权限): {ex.Message}");
                StopSelf();
            }
        }

        /* ===================== 浮窗音乐条 ===================== */

        private void InitMusicBar()
        {
            _musicBar = root.FindViewById(Resource.Id.floatMusicBar);
            _musicCover = root.FindViewById<ImageView>(Resource.Id.floatMusicCover);
            _musicTitle = root.FindViewById<TextView>(Resource.Id.floatMusicTitle);
            _musicSub = root.FindViewById<TextView>(Resource.Id.floatMusicSub);
            _musicPrev = root.FindViewById<TextView>(Resource.Id.floatMusicPrev);
            _musicPlay = root.FindViewById<TextView>(Resource.Id.floatMusicPlay);
            _musicNext = root.FindViewById<TextView>(Resource.Id.floatMusicNext);

            try
            {
                SetPanelButton(Resource.Id.floatMusicPrev, IconKind.Prev, "");
                SetPanelButton(Resource.Id.floatMusicPlay, IconKind.Play, "");
                SetPanelButton(Resource.Id.floatMusicNext, IconKind.Next, "");
            }
            catch { }

            if (_musicPrev != null) _musicPrev.Click += (s, e) => MusicPrev();
            if (_musicPlay != null) _musicPlay.Click += (s, e) => MusicToggle();
            if (_musicNext != null) _musicNext.Click += (s, e) => MusicNext();
            if (_musicBar != null)
            {
                // 点整条（非按钮区）→ 打开 App 的音乐界面
                _musicBar.Click += (s, e) => OpenMusic();
            }

            // 播放器状态变化时立即刷新（进度靠 800ms 轮询兜底）
            try
            {
                MusicPlayer.StateChanged += OnFloatMusicChanged;
                MusicPlayer.SongChanged += OnFloatMusicChanged;
            }
            catch { }

            _musicUi = new Handler(Looper.MainLooper);
            _musicTick = new Java.Lang.Runnable(() =>
            {
                // 面板收起时不刷新（省电/省主线程），展开后下一拍自动恢复
                try { if (expanded) RefreshMusicBar(); } catch { }
                try { _musicUi?.PostDelayed(_musicTick, expanded ? 800 : 2000); } catch { }
            });
            _musicUi.Post(_musicTick);
        }

        private void OnFloatMusicChanged()
        {
            try { _musicUi?.Post(() => { try { RefreshMusicBar(); } catch { } }); } catch { }
        }

        /// <summary>
        /// 当前该控制谁：本机在播 → 本机；本机没播但别的设备在播（心跳新鲜）→ 那台设备；
        /// 否则仍控制本机（待播放态也能开始播放）。
        /// </summary>
        private bool UseRemoteMusic()
        {
            try
            {
                bool localPlaying = MusicPlayer.Current != null && MusicPlayer.IsPlaying;
                if (localPlaying) return false;
                // 只有「其它设备确实正在播放」才接管；对方只是暂停/离线则仍控制本机
                return MusicCast.HasFreshRemote && MusicCast.RemotePlaying;
            }
            catch { return false; }
        }

        private void MusicPrev()
        {
            try
            {
                if (UseRemoteMusic()) { MusicCast.SendCommand("prev"); return; }
                if (MusicPlayer.Current == null) { OpenMusic(); return; }
                MusicPlayer.Prev();
            }
            catch (Exception ex) { Android.Util.Log.Warn("FloatWin", "上一曲失败: " + ex.Message); }
        }

        private void MusicNext()
        {
            try
            {
                if (UseRemoteMusic()) { MusicCast.SendCommand("next"); return; }
                if (MusicPlayer.Current == null) { OpenMusic(); return; }
                MusicPlayer.Next();
            }
            catch (Exception ex) { Android.Util.Log.Warn("FloatWin", "下一曲失败: " + ex.Message); }
        }

        private void MusicToggle()
        {
            try
            {
                if (UseRemoteMusic()) { MusicCast.SendCommand("toggle"); return; }
                if (MusicPlayer.Current == null) { OpenMusic(); return; }
                MusicPlayer.Toggle();
            }
            catch (Exception ex) { Android.Util.Log.Warn("FloatWin", "播放暂停失败: " + ex.Message); }
        }

        private void OpenMusic()
        {
            try
            {
                var i = new Intent(this, typeof(MusicActivity));
                i.AddFlags(ActivityFlags.NewTask);
                try
                {
                    // 有待播/在播歌曲时直接进播放详情页，否则进曲库挑歌
                    i.PutExtra(MusicActivity.ExtraShow,
                        MusicPlayer.Current != null ? MusicActivity.EntryPlayer : MusicActivity.EntryLibrary);
                }
                catch { }
                StartActivity(i);
            }
            catch (Exception ex) { Android.Util.Log.Warn("FloatWin", "打开音乐界面失败: " + ex.Message); }
        }

        private void RefreshMusicBar()
        {
            if (_musicBar == null) return;
            _musicBar.Visibility = ViewStates.Visible;

            bool remote = UseRemoteMusic();
            _musicRemoteTarget = remote;

            if (remote)
            {
                var title = MusicCast.RemoteTitle;
                _musicTitle.Text = string.IsNullOrEmpty(title) ? L10n.T("其它设备播放中") : title;
                var artist = MusicCast.RemoteArtist;
                var name = MusicCast.RemoteName;
                _musicSub.Text = (string.IsNullOrEmpty(artist) ? "" : artist + " · ")
                               + L10n.T("来自 ") + (string.IsNullOrEmpty(name) ? L10n.T("局域网设备") : name);

                SetMusicPlayIcon(MusicCast.RemotePlaying);
                LoadRemoteCover();
            }
            else
            {
                var s = MusicPlayer.Current;
                _musicTitle.Text = s == null ? L10n.T("未在播放") : s.DisplayTitle;
                if (s == null)
                {
                    _musicSub.Text = L10n.T("点击选择歌曲");
                }
                else
                {
                    var sub = s.Artist ?? "";
                    if (!MusicPlayer.IsPlaying) sub += MusicPlayer.HasStarted ? L10n.T(" · 已暂停") : L10n.T(" · 待播放");
                    _musicSub.Text = sub.Trim(' ', '·');
                }

                SetMusicPlayIcon(MusicPlayer.IsPlaying);
                LoadLocalCover(s);
            }
        }

        private void SetMusicPlayIcon(bool playing)
        {
            try
            {
                if (_musicPlay == null) return;
                if (_musicIconInit && _musicIconPlaying == playing) return;   // 状态没变不重绘
                _musicIconInit = true;
                _musicIconPlaying = playing;
                _musicPlay.Text = "";
                SetPanelButton(Resource.Id.floatMusicPlay, playing ? IconKind.Pause : IconKind.Play, "");
            }
            catch { }
        }

        private void LoadLocalCover(Song s)
        {
            try
            {
                string key = s == null ? "" : (s.Path ?? s.DisplayTitle ?? "");
                if (key == _musicCoverKey) return;
                _musicCoverKey = key;
                if (s == null)
                {
                    _musicCover.SetImageBitmap(MusicActivity.PlaceholderBitmap(true));
                    return;
                }
                // 本地文件：封面已解析成 CoverPath 缓存文件，直接解码。
                // SongArt.LoadInto 只认 CoverUrl/OnlineCover（URL），本地歌曲两者皆空，
                // 旧实现在这里直接 return——浮窗永远没有封面（本机播放最常见场景）。
                Bitmap bmp = null;
                if (!string.IsNullOrEmpty(s.CoverPath))
                {
                    try { bmp = MusicLibrary.LoadCover(s, 96); } catch { }
                }
                if (bmp != null)
                {
                    try { _musicCover.SetScaleType(ImageView.ScaleType.CenterCrop); } catch { }
                    _musicCover.SetImageBitmap(bmp);
                    return;
                }
                // 在线歌曲 / 一起听共享歌曲：封面是 URL，仍走 SongArt（下载 + 缓存 + 回填）
                if (!string.IsNullOrEmpty(SongArt.Url(s)))
                {
                    SongArt.LoadInto(_musicCover, s, 96);
                    return;
                }
                _musicCover.SetImageBitmap(MusicActivity.PlaceholderBitmap(true));
            }
            catch { }
        }

        private void LoadRemoteCover()
        {
            try
            {
                var cover = MusicCast.RemoteCover;
                if (string.Equals(cover, _musicCoverKey)) return;
                _musicCoverKey = cover;
                if (string.IsNullOrEmpty(cover))
                {
                    _musicCover.SetImageBitmap(MusicActivity.PlaceholderBitmap(true));
                    return;
                }
                var bytes = Convert.FromBase64String(cover);
                var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bmp != null) _musicCover.SetImageBitmap(bmp);
            }
            catch { }
        }

        private void BindMediaKey(int id, string cmd)
        {
            var v = root.FindViewById<TextView>(id);
            if (v == null) return;
            v.Click += (s, e) =>
            {
                try
                {
                    var b = MchatBridge.Current;
                    if (b == null) return;
                    // 需求：这些键控制的是**局域网内其它设备**，不是本机。
                    // 走 SendControl → mesh 频道 control 帧 → 对端 MaybeExecControl 执行。
                    // （本机自己执行的是 RunControlCommand，浮窗这里不能用它。）
                    if (b.GetOnlineCount() <= 0)
                    {
                        L10n.Toast(this, L10n.T("局域网内没有其它设备，无法控制"), ToastLength.Short).Show();
                        return;
                    }
                    b.SendControl(cmd);
                }
                catch (Exception ex) { Android.Util.Log.Warn("FloatWin", "媒体控制失败: " + ex.Message); }
            };
        }

        /// <summary>把浮窗里的「字符图标」按钮换成矢量图标（替代 ⏮ ▶❚❚ ⏭ 🔊 🔉 ✕）。</summary>
        private void SetPanelButton(int id, IconKind kind, string text)
        {
            var v = root.FindViewById<TextView>(id);
            if (v == null) return;
            int px = Dp(18);
            try
            {
                v.SetCompoundDrawablesWithIntrinsicBounds(
                    new IconDrawable(this, kind, (int)Color.Rgb(0x1a, 0x1a, 0x1a), px), null, null, null);
                v.CompoundDrawablePadding = Dp(4);
            }
            catch { }
            v.Text = text;
        }

        /* ===================== 位置：图标 / 面板 各自独立 ===================== */

        private void RestorePos()
        {
            try
            {
                var p = GetSharedPreferences(PrefPos, FileCreationMode.Private);
                iconX = p.GetInt("iconX", 0);
                iconY = p.GetInt("iconY", 260);
                panelX = p.GetInt("panelX", -1);
                panelY = p.GetInt("panelY", -1);
            }
            catch { iconX = 0; iconY = 260; panelX = -1; panelY = -1; }
        }

        private void SavePos()
        {
            try
            {
                var p = GetSharedPreferences(PrefPos, FileCreationMode.Private);
                p.Edit().PutInt("iconX", iconX).PutInt("iconY", iconY)
                        .PutInt("panelX", panelX).PutInt("panelY", panelY).Apply();
            }
            catch { }
        }

        /// <summary>
        /// 把坐标夹到屏幕内，避免拖到边缘后整块看不见（面板比图标大，尤其容易越界）。
        ///
        /// 注意：**不能直接用 root.Width/Height**。切换展开/收起时窗口尺寸还没重新测量，
        /// 拿到的还是上一种状态的尺寸；用面板宽度去夹图标会把图标硬拽回屏幕左侧
        /// （表现为「收起后浮标位置跑了」）。折叠状态一律按图标尺寸常量算。
        /// </summary>
        private void ClampActive()
        {
            try
            {
                var dm = Resources.DisplayMetrics;
                int sw = dm.WidthPixels, sh = dm.HeightPixels;

                int w, h;
                if (expanded)
                {
                    w = Dp(280);
                    h = root != null && root.Height > Dp(120) ? root.Height : Dp(360);
                }
                else
                {
                    w = Dp(44);
                    h = Dp(44);
                }
                if (w > sw) w = sw;
                if (h > sh) h = sh;

                int maxX = Math.Max(0, sw - w);
                int maxY = Math.Max(0, sh - h);
                if (lp.X < 0) lp.X = 0;
                if (lp.Y < 0) lp.Y = 0;
                if (lp.X > maxX) lp.X = maxX;
                if (lp.Y > maxY) lp.Y = maxY;
            }
            catch { }
        }

        /* ===================== 指令窗口 / 频道切换 ===================== */

        private int Dp(float v) { return (int)(v * Resources.DisplayMetrics.Density + 0.5f); }

        /// <summary>指令窗口单独收起：只隐藏音量/媒体那一行，聊天窗口不受影响。</summary>
        private void SetCmdCollapsed(bool collapsed)
        {
            cmdCollapsed = collapsed;
            if (cmdRow != null) cmdRow.Visibility = collapsed ? ViewStates.Gone : ViewStates.Visible;
            if (cmdToggle != null)
            {
                try
                {
                    cmdToggle.SetCompoundDrawablesWithIntrinsicBounds(
                        new IconDrawable(this, collapsed ? IconKind.ChevronRight : IconKind.ChevronDown, (int)Color.Rgb(0x1a, 0x1a, 0x1a), Dp(16)), null, null, null);
                    cmdToggle.CompoundDrawablePadding = Dp(4);
                }
                catch { }
                cmdToggle.Text = "";
            }
        }

        /// <summary>浮窗顶部的频道切换条。</summary>
        private void RefreshFloatConvs()
        {
            var bridge = MchatBridge.Current;
            if (bridge == null || convList == null) return;

            PostUi(() =>
            {
                if (convList == null) return;
                convList.RemoveAllViews();

                System.Collections.Generic.List<Conversation> convs;
                try { convs = bridge.GetConversations(); } catch { return; }

                if (string.IsNullOrEmpty(floatConv)) floatConv = bridge.ActiveConv;

                int shown = 0;
                foreach (var c in convs)
                {
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    if (shown >= 6) break;
                    bool cur = c.Id == floatConv;

                    var chip = new TextView(this);
                    chip.Text = (string.IsNullOrEmpty(c.Title) ? c.Id : c.Title);
                    chip.SetTextSize(Android.Util.ComplexUnitType.Sp, 11);
                    chip.SetTextColor(Color.ParseColor(cur ? "#07c160" : "#666666"));
                    if (cur)
                    {
                        try
                        {
                            chip.SetCompoundDrawablesWithIntrinsicBounds(
                                new IconDrawable(this, IconKind.Dot, (int)Color.ParseColor("#07c160"), Dp(8)), null, null, null);
                            chip.CompoundDrawablePadding = Dp(4);
                        }
                        catch { }
                    }
                    chip.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
                    chip.SetBackgroundResource(Resource.Drawable.floating_key_bg);

                    var captured = c.Id;
                    chip.Click += (s, e) => SwitchFloatConv(captured);

                    var lp = new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent);
                    lp.RightMargin = Dp(4);
                    convList.AddView(chip, lp);
                    shown++;
                }
            });
        }

        private void SwitchFloatConv(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return;
            floatConv = conv;
            try { MchatBridge.Current?.SetActiveConv(conv); } catch { }
            lastRenderSig = null;
            RefreshMessages();
            RefreshFloatConvs();
        }

        /* ===================== 拖拽 / 点击 ===================== */

        public bool OnTouch(View v, MotionEvent e)
        {
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    initX = lp.X;
                    initY = lp.Y;
                    touchX = e.RawX;
                    touchY = e.RawY;
                    touchDownAt = System.Environment.TickCount;
                    dragging = false;
                    return true;

                case MotionEventActions.Move:
                    {
                        float dx = e.RawX - touchX;
                        float dy = e.RawY - touchY;
                        if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6) dragging = true;
                        if (dragging)
                        {
                            lp.X = initX + (int)dx;
                            lp.Y = initY + (int)dy;
                            ClampActive();
                            // 拖哪个状态就记哪个位置：图标与面板互不影响
                            if (expanded) { panelX = lp.X; panelY = lp.Y; }
                            else { iconX = lp.X; iconY = lp.Y; }
                            try { wm.UpdateViewLayout(root, lp); } catch { }
                        }
                        return true;
                    }

                case MotionEventActions.Up:
                    // 轻点（未拖动且时间短）→ 只在**收起状态**下展开。
                    // 展开后不再「点空白就收起」：以前那样点偏一点就整个面板消失，
                    // 很像卡死；收起只认标题栏那个「—」按钮。
                    if (!dragging && System.Environment.TickCount - touchDownAt < 400)
                    {
                        if (!expanded) ExpandPanel();
                    }
                    else if (dragging)
                        SavePos();
                    return true;

                case MotionEventActions.Cancel:
                    return true;
            }
            return false;
        }

        /* ===================== 面板展开 / 收起 ===================== */

        private void TogglePanel()
        {
            if (expanded) CollapsePanel();
            else ExpandPanel();
        }

        private void ExpandPanel()
        {
            if (expanded || root == null) return;
            expanded = true;
            unread = 0;
            UpdateBadge();
            collapsedBar.Visibility = ViewStates.Gone;
            panel.Visibility = ViewStates.Visible;
            try { RefreshMusicBar(); } catch { }   // 展开立即刷新音乐条，不等下一拍

            // 展开时允许获取焦点（输入法才能弹出），但仍然 NotTouchModal：
            // 面板以外的区域照样能点到底下的 App，不会被悬浮窗整屏吃掉。
            lp.Flags = WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutNoLimits;
            lp.SoftInputMode = SoftInput.AdjustPan;

            // 面板用自己的位置；第一次展开放在屏幕上方居中，不跟随图标的拖动位置
            if (panelX < 0 || panelY < 0)
            {
                var dm = Resources.DisplayMetrics;
                panelX = Math.Max(Dp(8), (dm.WidthPixels - Dp(300)) / 2);
                panelY = Dp(90);
            }
            lp.X = panelX;
            lp.Y = panelY;
            ClampActive();
            try { wm.UpdateViewLayout(root, lp); } catch { }

            // 布局完成后再夹一次（此时才知道面板真实宽高，避免贴边被裁掉）
            try
            {
                root.Post(() =>
                {
                    if (!expanded) return;
                    ClampActive();
                    panelX = lp.X; panelY = lp.Y;
                    try { wm.UpdateViewLayout(root, lp); } catch { }
                    SavePos();
                });
            }
            catch { }

            RefreshMessages();
            RefreshFloatConvs();
        }

        private void CollapsePanel()
        {
            if (!expanded || root == null) return;
            expanded = false;
            collapsedBar.Visibility = ViewStates.Visible;
            panel.Visibility = ViewStates.Gone;

            lp.Flags = WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutNoLimits;

            // 收回到图标自己的位置（与面板位置解耦）
            lp.X = iconX;
            lp.Y = iconY;
            ClampActive();
            try { wm.UpdateViewLayout(root, lp); } catch { }

            try
            {
                root.Post(() =>
                {
                    if (expanded) return;
                    ClampActive();
                    iconX = lp.X; iconY = lp.Y;
                    try { wm.UpdateViewLayout(root, lp); } catch { }
                    SavePos();
                });
            }
            catch { }
        }

        /* ===================== 消息预览 ===================== */

        private void OnHistoryChanged()
        {
            if (!expanded) unread++;
            RefreshMessages();
        }

        /// <summary>上一次渲染的内容指纹：内容没变就别重建视图。</summary>
        private string lastRenderSig;

        private void RefreshMessages()
        {
            var bridge = MchatBridge.Current;
            if (bridge == null || root == null) return;

            System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> list;
            try
            {
                var conv = string.IsNullOrEmpty(floatConv) ? bridge.ActiveConv : floatConv;
                list = bridge.GetMessages(conv, MaxShowMessages);
            }
            catch { return; }

            // 兜底轮询每 6 秒跑一次，但绝大多数时候消息根本没变。
            // 原来无条件 RemoveAllViews + 新建 N 个 TextView，白白唤醒 UI 线程、
            // 触发一轮布局与绘制。这里先比指纹，变了才动 UI。
            string sig = BuildSignature(list);
            if (sig == lastRenderSig) return;
            lastRenderSig = sig;

            PostUi(() =>
            {
                if (root == null || msgList == null) return;
                msgList.RemoveAllViews();
                foreach (var m in list)
                {
                    if (m == null) continue;
                    var from = m.TryGetValue("fromName", out var fv) ? fv?.ToString() : "";
                    var text = m.TryGetValue("text", out var tv) ? tv?.ToString() : "";
                    if (string.IsNullOrEmpty(text)) continue;

                    var tv2 = new TextView(this)
                    {
                        Text = (string.IsNullOrEmpty(from) ? "" : from + "：") + text
                    };
                    tv2.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
                    tv2.SetTextColor(Color.ParseColor("#1a1a1a"));
                    tv2.SetMaxLines(2);
                    tv2.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    tv2.SetPadding(0, 4, 0, 4);
                    msgList.AddView(tv2);
                }

                if (!expanded) UpdateBadge();

                // 预览滚到最新
                scroll?.Post(() =>
                {
                    try { scroll.ScrollTo(0, scroll.Height); } catch { }
                });
            });
        }

        /// <summary>把消息列表压成一个可比对的指纹（只取前几条 + 条数，够用且便宜）。</summary>
        private static string BuildSignature(
            System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> list)
        {
            if (list == null) return "null";
            var sb = new System.Text.StringBuilder();
            sb.Append(list.Count);
            int n = Math.Min(list.Count, 8);
            for (int i = 0; i < n; i++)
            {
                var m = list[i];
                if (m == null) { sb.Append("|-"); continue; }
                object idv, tv;
                m.TryGetValue("id", out idv);
                m.TryGetValue("text", out tv);
                sb.Append('|').Append(idv ?? "").Append(':').Append(tv ?? "");
            }
            return sb.ToString();
        }

        private void UpdateBadge()
        {
            if (badge == null) return;
            PostUi(() =>
            {
                if (badge == null) return;
                if (unread > 0 && !expanded)
                {
                    badge.Visibility = ViewStates.Visible;
                    badge.Text = unread > 99 ? "99+" : unread.ToString();
                }
                else
                {
                    badge.Visibility = ViewStates.Gone;
                }
            });
        }

        /* ===================== 发送 / 机器人 ===================== */

        private void OnSendClick()
        {
            var text = input?.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            SendText(text);
            input.Text = string.Empty;
            unread = 0;
            UpdateBadge();
        }

        /// <summary>经桥接发送：进历史、推 App 界面、广播局域网、交机器人处理。</summary>
        private void SendText(string text)
        {
            try
            {
                MchatBridge.Current?.SendLocalText(text);
                unread = 0;
                UpdateBadge();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("FloatWin", "浮窗发送失败: " + ex.Message);
            }
        }

        /* ===================== 前台通知 ===================== */

        private void EnsureChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var manager = (NotificationManager)GetSystemService(NotificationService);
                if (manager.GetNotificationChannel(ChannelId) != null) return;
                var channel = new NotificationChannel(ChannelId, L10n.T("悬浮窗"), NotificationImportance.Min)
                {
                    Description = L10n.T("悬浮窗快速收发消息与媒体控制")
                };
                channel.SetShowBadge(false);
                channel.EnableVibration(false);
                channel.SetSound(null, null);
                manager.CreateNotificationChannel(channel);
            }
            catch { }
        }

        private void StartForegroundCompat()
        {
            try
            {
                var content = new Intent(this, typeof(ChatActivity));
                content.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
                var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                    ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                    : PendingIntentFlags.UpdateCurrent;
                var pending = PendingIntent.GetActivity(this, 0, content, flags);

                var closeIntent = new Intent(this, typeof(FloatingWindowService));
                closeIntent.SetAction(ActionHide);
                var closePending = PendingIntent.GetService(this, 1, closeIntent, flags);

                var notification = new NotificationCompat.Builder(this, ChannelId)
                    .SetSmallIcon(Resource.Drawable.icon)
                    .SetContentTitle(L10n.T("悬浮窗运行中"))
                    .SetContentText(L10n.T("点击浮标可快速预览和收发消息"))
                    .SetPriority((int)NotificationPriority.Min)
                    .SetOngoing(true)
                    .SetOnlyAlertOnce(true)
                    .SetShowWhen(false)
                    .SetContentIntent(pending)
                    .AddAction(0, L10n.T("关闭浮窗"), closePending)
                    .Build();

                // 服务类型已在清单中声明（dataSync），双参重载即按清单类型生效，
                // 同时兼容各 Android 版本（部分版本的绑定常量不可用）。
                StartForeground(NotificationId, notification);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FloatWin", $"StartForeground 失败: {ex.Message}");
                try { StartForeground(NotificationId, BuildFallbackNotification()); } catch { }
            }
        }

        private Notification BuildFallbackNotification()
        {
            var content = new Intent(this, typeof(ChatActivity));
            content.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;
            return new NotificationCompat.Builder(this, ChannelId)
                .SetSmallIcon(Resource.Drawable.icon)
                .SetContentTitle(L10n.T("悬浮窗运行中"))
                .SetContentText(L10n.T("点击浮标可快速预览和收发消息"))
                .SetOngoing(true)
                .SetContentIntent(PendingIntent.GetActivity(this, 0, content, flags))
                .Build();
        }

        /* ===================== 静态工具（权限 / 启停） ===================== */

        /// <summary>是否已授予悬浮窗权限。</summary>
        public static bool CanDrawOverlays(Context context)
        {
            if (context == null) return false;
            if (Build.VERSION.SdkInt < BuildVersionCodes.M) return true;
            try { return Settings.CanDrawOverlays(context); }
            catch { return false; }
        }

        /// <summary>跳转系统设置页申请悬浮窗权限。</summary>
        public static void RequestOverlayPermission(Context context)
        {
            if (context == null) return;
            try
            {
                var intent = new Intent("android.settings.action.MANAGE_OVERLAY_PERMISSION");
                intent.SetData(Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch
            {
                try
                {
                    var intent = new Intent(Settings.ActionApplicationDetailsSettings);
                    intent.SetData(Android.Net.Uri.Parse("package:" + context.PackageName));
                    intent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(intent);
                }
                catch { }
            }
        }

        /// <summary>打开浮窗（调用前需确保已有悬浮窗权限）。</summary>
        public static void Show(Context context)
        {
            if (context == null) return;
            try
            {
                var intent = new Intent(context, typeof(FloatingWindowService));
                intent.SetAction(ActionShow);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(intent);
                else
                    context.StartService(intent);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FloatWin", $"启动浮窗失败: {ex.Message}");
            }
        }

        /// <summary>关闭浮窗。</summary>
        public static void Hide(Context context)
        {
            if (context == null) return;
            try
            {
                var intent = new Intent(context, typeof(FloatingWindowService));
                intent.SetAction(ActionHide);
                context.StartService(intent);
            }
            catch { }
        }

        /* ===================== 图标圆角裁切 ===================== */

        /// <summary>
        /// 把任意直角图标在代码里裁成圆角（API21+）。
        /// 借助 View 的 Outline + ClipToOutline 实现，不依赖图标资源本身是否带圆角。
        /// </summary>
        private void ApplyRoundedCorners(View view, float radiusDp)
        {
            if (view == null || Build.VERSION.SdkInt < BuildVersionCodes.Lollipop) return;
            float radius = radiusDp * Resources.DisplayMetrics.Density;
            view.OutlineProvider = new RoundedOutlineProvider(radius);
            view.ClipToOutline = true;
        }

        /// <summary>生成圆角矩形外轮廓，供 ClipToOutline 裁切用。</summary>
        private class RoundedOutlineProvider : ViewOutlineProvider
        {
            private readonly float _radius;
            public RoundedOutlineProvider(float radius) { _radius = radius; }

            public override void GetOutline(View view, Outline outline)
            {
                if (view == null || outline == null) return;
                int w = view.Width;
                int h = view.Height;
                if (w <= 0 || h <= 0)
                {
                    // 尺寸还没测出来：先占位，layout 完成后系统会再次回调 GetOutline
                    outline.SetRect(0, 0, 0, 0);
                    return;
                }
                outline.SetRoundRect(0, 0, w, h, _radius);
            }
        }
    }
}

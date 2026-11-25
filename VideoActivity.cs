// ============================================================
// 影音模块 · 原生视频播放器 Activity（C# / Xamarin.Android）
//
// 参考 jellyfin-android 2.7.2 的 PlayerFragment / PlayerGestureHelper / PlayerMenus：
//   · 全屏黑色播放层 + 可自动隐藏的控制层（顶部标题/返回，中部上一集·播放·下一集，
//     底部进度条与时间，底部工具行：锁定 / 音轨信息 / 倍速 / 画面比例 / 列表）
//   · 单击显隐控制、双击左右区域快退快进、横向滑动 seek、
//     纵向滑动左半屏亮度 / 右半屏音量，配合中央 HUD 提示
//   · 锁定后只显示解锁按钮，避免误触；播放结束自动下一个
// 播放内核：Media3 ExoPlayer（ExoVideoPlayer 封装），与 TV 项目一致——直连已解析直链，
// 浏览器 UA + 跨协议重定向，跳过系统 VideoView(MediaPlayer) 对 HLS 支持差导致的黑屏卡加载。
// 手势/控制层（单击显隐、双击±10s、横滑 seek、纵滑亮度/音量、锁定、倍速、画面比例、列表）
// 在 ExoPlayer 之上自绘，倍速走 ExoPlayer.PlaybackParameters。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using Newtonsoft.Json;
using AhoChatA.Music;
using AhoChatA.Vod;
using Orientation = Android.Widget.Orientation;

namespace AhoChatA
{
    [Activity(Label = "@string/act_video", Exported = false,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                                     ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize,
              Theme = "@style/AppTheme.NoActionBar")]
    public partial class VideoActivity : BaseActivity
    {
        public const string ExtraPath = "video_path";
        /// <summary>在线影视：整条线路的播放队列（JSON 数组 VodPlayItem），播完自动下一集。</summary>
        public const string ExtraQueueJson = "vod_queue";
        public const string ExtraIndex = "vod_index";
        public const string ExtraResume = "vod_resume";
        public const string ExtraVodKey = "vod_key";
        /// <summary>分享卡片点开时的显示标题（本地文件名或影视剧集名）。</summary>
        public const string ExtraTitle = "video_title";

        private const int White = unchecked((int)0xFFFFFFFF);
        private const int DimWhite = unchecked((int)0xFFD8DCE2);
        private const int Accent = unchecked((int)0xFF007AFF);
        private const int OverlayBg = unchecked((int)0xCC101318);

        private ExoVideoPlayer _exo;
        private FrameLayout _stage;
        private LinearLayout _controls, _topBar, _centerBar, _bottomBar, _toolBar;
        private TextView _title, _tCur, _tTotal, _speedBtn, _scaleBtn, _listBtn, _audioBtn, _telemetry;
        private string _currentUrl;
        private View _lockBtn, _unlockBtn;
        private SeekBar _seek;
        private View _btnPlay, _btnPrev, _btnNext;
        private IconView _btnPlayIcon;
        private LinearLayout _hud;
        private TextView _hudText, _hudSub;
        private ProgressBar _hudBar, _loading;

        private readonly Handler _h = new Handler(Looper.MainLooper);
        private Java.Lang.Runnable _tickRun, _hideRun, _tapRun, _hudHideRun;
        private List<Song> _queue = new List<Song>();
        private int _index = -1;
        private string _vodKey = "";
        private float _speed = 1.0f;
        private bool _crop, _locked, _pausedByUser, _seeking, _finished;
        private int _resumePos;
        private float _brightness = -1f;
        private AudioManager _am;
        private int _maxVol = 15;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
            Window.SetBackgroundDrawable(new ColorDrawable(new Color(unchecked((int)0xFF000000))));
            HideSystemUi();

            _am = (AudioManager)GetSystemService(AudioService);
            try { _maxVol = _am.GetStreamMaxVolume(Android.Media.Stream.Music); } catch { }

            var queueJson = Intent?.GetStringExtra(ExtraQueueJson);
            var path = Intent?.GetStringExtra(ExtraPath);
            var sharedTitle = Intent?.GetStringExtra(ExtraTitle) ?? "";
            _vodKey = Intent?.GetStringExtra(ExtraVodKey) ?? "";
            int start = Intent?.GetIntExtra(ExtraIndex, 0) ?? 0;
            int resume = Intent?.GetIntExtra(ExtraResume, 0) ?? 0;

            // 视频分享卡片点开：链接可能是 http（本机网关流 / 在线直链），
            // 直接作为单曲队列全屏播放，不扫描本机曲库、也不要求文件存在。
            bool isUrl = !string.IsNullOrEmpty(path)
                         && path.StartsWith("http", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(queueJson))
            {
                // 在线影视：整条线路直接作为播放队列，不扫描本机曲库（省掉一次全盘遍历）
                try
                {
                    var eps = JsonConvert.DeserializeObject<List<VodPlayItem>>(queueJson);
                    _queue = eps.Select(x => new Song { Path = x.Url, Title = x.Name }).ToList();
                    _index = Math.Max(0, Math.Min(start, _queue.Count - 1));
                }
                catch { _queue = new List<Song>(); _index = -1; }
            }
            else if (isUrl)
            {
                string name = sharedTitle;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = System.IO.Path.GetFileName(new Uri(path).AbsolutePath); } catch { }
                    if (string.IsNullOrWhiteSpace(name)) name = L10n.T("分享的视频");
                }
                _queue = new List<Song> { new Song { Path = path, Title = name, IsVideo = true } };
                _index = 0;
            }
            else
            {
                MusicLibrary.Init(this);
                _queue = MusicLibrary.Videos();
                if (!string.IsNullOrEmpty(path))
                {
                    // 直投单文件：即使库还没扫到也直接播
                    int at = _queue.FindIndex(s => s.Path == path);
                    if (at >= 0) _index = at;
                    else
                    {
                        _queue = new List<Song> { new Song { Path = path, Title = System.IO.Path.GetFileName(path), IsVideo = true } };
                        _index = 0;
                    }
                }
                else if (_queue.Count > 0) _index = 0;
            }

            if (_index < 0 || _index >= _queue.Count)
            {
                L10n.Toast(this, L10n.T("没有可播放的视频"), ToastLength.Long).Show();
                Finish();
                return;
            }

            BuildUi();
            PlayAt(_index, resume);
        }

        // 已在前台（SingleTop 复用）时收到视频直投：复用播放器直接切到该文件
        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            try
            {
                var path = intent?.GetStringExtra(ExtraPath);
                if (string.IsNullOrEmpty(path)) return;
                bool isUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                // 分享卡片是 http 链接，不能再用 File.Exists 拦掉
                if (!isUrl && !System.IO.File.Exists(path)) return;
                if (isUrl)
                {
                    string name = intent.GetStringExtra(ExtraTitle) ?? L10n.T("分享的视频");
                    _queue = new List<Song> { new Song { Path = path, Title = name, IsVideo = true } };
                    _index = 0;
                }
                else
                {
                    MusicLibrary.Init(this);
                    _queue = MusicLibrary.Videos();
                    int at = _queue.FindIndex(s => s.Path == path);
                    if (at >= 0) _index = at;
                    else
                    {
                        _queue = new List<Song> { new Song { Path = path, Title = System.IO.Path.GetFileName(path), IsVideo = true } };
                        _index = 0;
                    }
                }
                PlayAt(_index, 0);
            }
            catch (Exception ex) { Android.Util.Log.Warn("Video", "OnNewIntent 视频直投失败: " + ex.Message); }
        }

        /// <summary>在线影视退出播放时回写集数与进度，详情页据此续播。</summary>
        protected override void OnStop()
        {
            try
            {
                if (!string.IsNullOrEmpty(_vodKey) && _queue.Count > 0 && _index >= 0)
                {
                    var r = VodCache.History().Find(x => x.Key == _vodKey);
                    if (r != null)
                    {
                        r.EpisodeIndex = _index;
                        if (_index < _queue.Count) r.EpisodeName = _queue[_index].DisplayTitle;
                        try
                        {
                            if (_exo != null)
                            {
                                r.PositionMs = _exo.PositionMs;
                                r.DurationMs = _exo.DurationMs;
                            }
                        }
                        catch { }
                        VodCache.AddHistory(r);
                    }
                }
            }
            catch { }
            base.OnStop();
        }

        /* ============================================================
         * 界面（全动态，无 axml）
         * ============================================================ */

        private void BuildUi()
        {
            var root = new FrameLayout(this);
            root.SetBackgroundColor(new Color(unchecked((int)0xFF000000)));
            root.LayoutParameters = new ViewGroup.LayoutParams(-1, -1);

            // 视频层：ExoPlayer 渲染到 PlayerView（与 TV 项目一致的 Media3 播放内核）
            _stage = new FrameLayout(this) { };
            _stage.LayoutParameters = new FrameLayout.LayoutParams(-1, -1, GravityFlags.Center);
            _exo = new ExoVideoPlayer(this);
            var vlp = new FrameLayout.LayoutParams(-1, -1, GravityFlags.Center);
            _exo.SurfaceView.LayoutParameters = vlp;
            _stage.AddView(_exo.SurfaceView);
            root.AddView(_stage);

            _exo.Prepared += (s, e) =>
            {
                try
                {
                    _loading.Visibility = ViewStates.Gone;
                    long dur = _exo.DurationMs;
                    _tTotal.Text = Song.FormatMs((int)dur);
                    _seek.Max = Math.Max(1, (int)dur);
                    ApplySpeed();
                    ApplyScale();
                    _exo.Start();
                    SyncPlayIcon();
                    StartTick();
                }
                catch { }
            };
            _exo.Completion += (s, e) =>
            {
                if (_finished) return;
                if (_index < _queue.Count - 1) PlayAt(_index + 1, 0);
                else
                {
                    _finished = true;
                    SyncPlayIcon();
                    ShowControls(true);
                }
            };
            _exo.Error += (s, e) =>
            {
                _loading.Visibility = ViewStates.Gone;
                L10n.Toast(this, L10n.T("无法播放该视频（编码可能不受支持或视频源已失效）"), ToastLength.Short).Show();
            };

            _loading = new ProgressBar(this);
            _loading.Indeterminate = true;
            _loading.LayoutParameters = new FrameLayout.LayoutParams(Dp(56), Dp(56), GravityFlags.Center);
            root.AddView(_loading);

            // 手势层（透明，铺满全屏；控制层放在它上面以便按钮可点）
            var gesture = new View(this);
            gesture.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            gesture.Touch += OnStageTouch;
            root.AddView(gesture);

            /* ---- 控制层 ---- */
            _controls = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _controls.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _controls.SetBackgroundColor(new Color(unchecked((int)0x99000000)));
            // 不要给 _controls 挂 Touch 监听并置 Handled=true：View.dispatchTouchEvent 会先
            // 调父的 OnTouchListener，返回 true 后按钮就收不到点击了。LinearLayout 默认不
            // clickable，空白区事件会自然穿透到下面的透明手势层，按钮区由子控件自己消费。

            // 顶栏
            _topBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _topBar.SetGravity(GravityFlags.CenterVertical);
            _topBar.SetPadding(Dp(16), Dp(6), Dp(16), Dp(6));
            var back = IconGlyphBtn(IconKind.Back, 22, () => Finish());
            _topBar.AddView(back, new LinearLayout.LayoutParams(Dp(44), Dp(44)));
            _title = new TextView(this) { TextSize = 17 };
            _title.SetTextColor(new Color(White));
            _title.SetSingleLine(true);
            _title.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
            _title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f) { LeftMargin = Dp(4) };
            _topBar.AddView(_title);
            // 右上角缓冲遥测：「已缓冲 Xs · Y MB」，让用户知道何时会卡顿
            _telemetry = new TextView(this)
            {
                TextSize = 11,
                Gravity = GravityFlags.Right | GravityFlags.CenterVertical,
            };
            _telemetry.SetTextColor(new Color(unchecked((int)0xFFBFBFBF)));
            _telemetry.SetSingleLine(true);
            _telemetry.Text = L10n.T("已缓冲 --");
            _telemetry.LayoutParameters = new LinearLayout.LayoutParams(Dp(118), -2)
            {
                LeftMargin = Dp(6),
                RightMargin = Dp(2),
            };
            _topBar.AddView(_telemetry);
            _lockBtn = IconGlyphBtn(IconKind.Lock, 20, () => SetLocked(true));
            _topBar.AddView(_lockBtn, new LinearLayout.LayoutParams(Dp(44), Dp(44)));
            _controls.AddView(_topBar);

            var mid = new LinearLayout(this) { Orientation = Orientation.Vertical };
            mid.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);
            mid.SetGravity(GravityFlags.Center);

            // 中部：上一集 / 播放暂停 / 下一集（Jellyfin exo_player_control_view 同款排布）
            _centerBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _centerBar.SetGravity(GravityFlags.Center);
            _btnPrev = CircleBtn(IconKind.Prev, 24, () => PlayAt(_index - 1, 0));
            _btnPlay = CircleBtn(IconKind.Play, 30, TogglePlay, 36, out _btnPlayIcon);
            _btnNext = CircleBtn(IconKind.Next, 24, () => PlayAt(_index + 1, 0));
            _centerBar.AddView(_btnPrev, new LinearLayout.LayoutParams(Dp(58), Dp(58)) { RightMargin = Dp(22) });
            _centerBar.AddView(_btnPlay, new LinearLayout.LayoutParams(Dp(72), Dp(72)));
            _centerBar.AddView(_btnNext, new LinearLayout.LayoutParams(Dp(58), Dp(58)) { LeftMargin = Dp(22) });
            mid.AddView(_centerBar, new LinearLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.Center });
            _controls.AddView(mid, new LinearLayout.LayoutParams(-1, 0, 1f));

            // 底部：进度条 + 时间
            _bottomBar = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _bottomBar.SetPadding(Dp(16), Dp(6), Dp(16), Dp(4));

            var seekRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            seekRow.SetGravity(GravityFlags.CenterVertical);
            _tCur = SmallTime("00:00");
            _tTotal = SmallTime("00:00");
            _seek = new SeekBar(this);
            _seek.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            _seek.ProgressChanged += (s, e) =>
            {
                if (_seeking && e.FromUser)
                {
                    _tCur.Text = Song.FormatMs(e.Progress);
                    // 拖拽进度时只显示时间提示，不显示遮挡画面的控件层
                    ShowHud(Song.FormatMs(e.Progress), L10n.T("拖动定位 · 共 ") + Song.FormatMs(SafeDur()),
                        (int)(100f * e.Progress / Math.Max(1, SafeDur())));
                }
            };
            _seek.StartTrackingTouch += (s, e) =>
            {
                _seeking = true;
                // 拖拽期间去掉全屏半透明遮罩，只留进度条与时间提示，避免遮挡画面
                SetDragOverlay(true);
            };
            _seek.StopTrackingTouch += (s, e) =>
            {
                _seeking = false;
                try { _exo.SeekTo(_seek.Progress); } catch { }
                SetDragOverlay(false);
                HideHudSoon();
                ShowControls(false);
            };
            seekRow.AddView(_tCur);
            seekRow.AddView(_seek);
            seekRow.AddView(_tTotal);
            _bottomBar.AddView(seekRow);

            // 底部工具行
            _toolBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _toolBar.SetGravity(GravityFlags.CenterVertical);
            _toolBar.SetPadding(0, Dp(2), 0, Dp(10));
            _audioBtn = ToolBtn(L10n.T("音轨"), () =>
                L10n.Toast(this, L10n.T("本机视频音轨不可切换"), ToastLength.Short).Show());
            _speedBtn = ToolBtn("1.0x", CycleSpeed);
            _scaleBtn = ToolBtn(L10n.T("适应"), ToggleScale);
            _listBtn = ToolBtn(L10n.T("列表"), ShowQueue);
            _toolBar.AddView(_audioBtn, ToolLp());
            _toolBar.AddView(_speedBtn, ToolLp());
            _toolBar.AddView(_scaleBtn, ToolLp());
            _toolBar.AddView(_listBtn, ToolLp());
            // 下载：把当前这一集/这一部交给统一下载管理器（TS 切片合并为 mp4）
            _toolBar.AddView(ToolBtn(L10n.T("下载"), DownloadCurrentVideo), ToolLp());
            // 手动分享：往聊天里发一张带封面的视频卡片，对端点开即可全屏播放
            _toolBar.AddView(ToolBtn(L10n.T("分享"), ShareCurrentVideo), ToolLp());
            // 系统分享：把当前视频原样转发给其它应用（微信 / QQ / 网盘等）
            _toolBar.AddView(ToolBtn(L10n.T("系统分享"), ShareCurrentVideoToSystem), ToolLp());
            _toolBar.AddView(new View(this) { LayoutParameters = new LinearLayout.LayoutParams(0, 1, 0.3f) });
            _toolBar.AddView(ToolBtn(L10n.T("旋转"), RotateScreen), ToolLp());
            _bottomBar.AddView(_toolBar);
            _controls.AddView(_bottomBar);
            root.AddView(_controls);

            // HUD（手势提示，参考 Jellyfin gesture_overlay / seek_overlay）
            _hud = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _hud.SetGravity(GravityFlags.CenterHorizontal);
            _hud.Background = RoundBg(OverlayBg, 12);
            _hud.SetPadding(Dp(20), Dp(14), Dp(20), Dp(14));
            _hud.Visibility = ViewStates.Gone;
            _hudText = new TextView(this) { TextSize = 18, Gravity = GravityFlags.Center };
            _hudText.SetTextColor(new Color(White));
            _hudSub = new TextView(this) { TextSize = IosStyle.CaptionSize, Gravity = GravityFlags.Center };
            _hudSub.SetTextColor(new Color(DimWhite));
            _hudBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal);
            _hudBar.Max = 100;
            _hud.AddView(_hudText);
            _hud.AddView(_hudSub, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(3) });
            _hud.AddView(_hudBar, new LinearLayout.LayoutParams(Dp(170), -2) { TopMargin = Dp(8) });
            root.AddView(_hud, new FrameLayout.LayoutParams(-2, -2, GravityFlags.Center));

            // 锁定态：只有右上角一个解锁按钮
            _unlockBtn = IconGlyphBtn(IconKind.Unlock, 20, () => SetLocked(false));
            _unlockBtn.Background = RoundBg(OverlayBg, 20);
            _unlockBtn.Visibility = ViewStates.Gone;
            root.AddView(_unlockBtn, new FrameLayout.LayoutParams(Dp(44), Dp(44),
                GravityFlags.Top | GravityFlags.Right) { TopMargin = Dp(16), RightMargin = Dp(16) });

            SetContentView(root);
            ShowControls(false);   // 进入全屏默认不显示控件，点击画面中间才唤起
        }

    // 等权列宽：竖屏下 7 个 WrapContent 按钮（尤其「音轨 · 本机」）总宽会超出屏幕，
    // 把右侧「分享/旋转」挤出被裁掉。改为 weight 均分，任何屏宽都能全部显示。
    private LinearLayout.LayoutParams ToolLp() =>
        new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(3) };

        private TextView SmallTime(string t)
        {
            var v = new TextView(this) { Text = t, TextSize = IosStyle.CaptionSize };
            v.SetTextColor(new Color(DimWhite));
            v.SetPadding(Dp(2), 0, Dp(6), 0);
            return v;
        }

        private TextView ToolBtn(string text, Action click)
        {
            var v = new TextView(this) { Text = text, TextSize = IosStyle.CaptionSize, Gravity = GravityFlags.Center };
            v.SetTextColor(new Color(White));
            v.Background = RoundBg(unchecked((int)0x33FFFFFF), 12);
            v.SetPadding(Dp(3), Dp(6), Dp(3), Dp(6));
            v.SetMinimumHeight(Dp(44));
            v.Click += (s, e) => click();
            return v;
        }

        /// <summary>「下载」：把当前这一集/这一部交给统一下载管理器。
        /// 走与播放相同的已解析直链（s.Path），后台抓取 TS 切片并合并为 mp4，
        /// 进度 / 失败重试 / 保存路径都在「服务 → 媒体 → 下载」里查看。</summary>
        private void DownloadCurrentVideo()
        {
            try
            {
                if (_index < 0 || _index >= _queue.Count) return;
                var s = _queue[_index];
                if (string.IsNullOrEmpty(s.Path) || !s.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                { L10n.Toast(this, L10n.T("本地视频无需下载"), ToastLength.Short).Show(); return; }
                DownloadCenter.QueueVideo(s.Path, s.Title);
                L10n.Toast(this, L10n.T("已加入下载队列，可在「下载」查看进度"), ToastLength.Long).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("加入下载失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>「分享」：把当前这一集/这一部生成带封面的卡片发到聊天。
        /// 本地视频给本机 HTTP Range 流链接，在线影视给已解析播放直链。</summary>
        private void ShareCurrentVideo()
        {
            try
            {
                if (_index < 0 || _index >= _queue.Count) return;
                var s = _queue[_index];
                string poster = "";
                // 在线影视：从详情缓存里取出海报 URL，让卡片在别的设备上也有图
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
                var fields = VideoShare.Build(s, s.Title, poster, _vodKey);
                if (fields == null) return;
                var bridge = MchatBridge.Current ?? CoreHost.Bridge;
                if (bridge == null)
                {
                    L10n.Toast(this, L10n.T("分享失败：服务尚未就绪"), ToastLength.Short).Show();
                    return;
                }
                // 分享的同时开启「一起看」宿主机上报：普通观看与一起看共用这一张卡片
                _watchPublishCard = bridge.SendVideoCard(fields);
                L10n.Toast(this, L10n.T("已分享到聊天：") + s.DisplayTitle, ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>系统分享：把当前视频的原始文件转发给其它应用（与「分享」到聊天频道不同）。</summary>
        private void ShareCurrentVideoToSystem()
        {
            try
            {
                if (_index < 0 || _index >= _queue.Count) { L10n.Toast(this, L10n.T("没有可分享的视频"), ToastLength.Short).Show(); return; }
                var s = _queue[_index];
                if (string.IsNullOrEmpty(s.Path)
                    || s.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || !System.IO.File.Exists(s.Path))
                {
                    L10n.Toast(this, L10n.T("在线视频暂不支持系统分享，可先「下载」到本地"), ToastLength.Short).Show();
                    return;
                }
                InternalViewer.ShareFile(this, s.Path, "video/*");
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private TextView IconTextBtn(string text, float size, Action click)
        {
            var v = new TextView(this) { Text = text, TextSize = size, Gravity = GravityFlags.Center };
            v.SetTextColor(new Color(White));
            v.Click += (s, e) => click();
            return v;
        }

        /// <summary>矢量图标按钮（Canvas 自绘，替代 ⏮ ▶ ⏭ ⏸ 🔒 🔓 ‹ 等字体字形）。</summary>
        private FrameLayout IconGlyphBtn(IconKind kind, int iconDp, Action click, out IconView icon)
        {
            var f = new FrameLayout(this) { Clickable = true };
            f.Click += (s, e) => click();
            icon = new IconView(this, kind) { IconColor = White };
            int s = Dp(iconDp);
            f.AddView(icon, new FrameLayout.LayoutParams(s, s) { Gravity = GravityFlags.Center });
            return f;
        }

        private FrameLayout IconGlyphBtn(IconKind kind, int iconDp, Action click)
        {
            IconView _;
            return IconGlyphBtn(kind, iconDp, click, out _);
        }

        /// <summary>圆形控制按钮；radiusDp 是圆角半径（dp）。</summary>
        private FrameLayout CircleBtn(IconKind kind, int iconDp, Action click, int radiusDp = 29)
        {
            IconView _;
            return CircleBtn(kind, iconDp, click, radiusDp, out _);
        }

        private FrameLayout CircleBtn(IconKind kind, int iconDp, Action click, int radiusDp, out IconView icon)
        {
            var f = IconGlyphBtn(kind, iconDp, click, out icon);
            f.Background = RoundBg(unchecked((int)0x33FFFFFF), radiusDp);
            return f;
        }

        private Drawable RoundBg(int color, int radiusDp)
        {
            var d = new GradientDrawable();
            d.SetColor(color);
            d.SetCornerRadius(Dp(radiusDp));
            return d;
        }

        /* ============================================================
         * 播放控制
         * ============================================================ */

        private void PlayAt(int index, int resume)
        {
            if (index < 0 || index >= _queue.Count) return;
            _index = index;
            _resumePos = resume;
            _finished = false;
            _pausedByUser = false;
            var s = _queue[index];
            _title.Text = s.DisplayTitle;
            _loading.Visibility = ViewStates.Visible;
            _tCur.Text = "00:00";
            _tTotal.Text = Song.FormatMs(s.DurationMs);
            _seek.Progress = 0;
            _currentUrl = s.Path;   // 原始直链，用于遥测查询本地缓存字节
            try
            {
                bool isHls = !string.IsNullOrEmpty(s.Path) && s.Path.ToLowerInvariant().Contains(".m3u8");
                _exo.Load(s.Path, _resumePos, isHls);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("打开视频失败：") + ex.Message, ToastLength.Short).Show();
            }
            // 后台预热下一集（仅在线影视队列）：把下一集头部数据灌进 SimpleCache，切集近乎秒开
            if (_queue.Count > 1 && index + 1 < _queue.Count)
            {
                var next = _queue[index + 1].Path;
                if (!string.IsNullOrEmpty(next) && next.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    VodExoCache.WarmUp(this, next);
            }
            ShowControls(false);   // 进入/切换视频不自动显示控件
        }

        private void TogglePlay()
        {
            try
            {
                if (_exo.IsPlaying) { _exo.Pause(); _pausedByUser = true; }
                else { _exo.Start(); _pausedByUser = false; _finished = false; }
            }
            catch { }
            SyncPlayIcon();
            // 播放/暂停不自动唤起控件（控件只在点屏幕中间时出现）
        }

        private void SyncPlayIcon()
        {
            bool playing = false;
            try { playing = _exo.IsPlaying; } catch { }
            if (_btnPlayIcon != null) _btnPlayIcon.Kind = playing ? IconKind.Pause : IconKind.Play;
        }

        private void CycleSpeed()
        {
            float[] sp = { 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
            int i = Array.IndexOf(sp, _speed);
            _speed = sp[(i + 1) % sp.Length];
            _speedBtn.Text = _speed.ToString("0.0#") + "x";
            ApplySpeed();
        }

        private void ApplySpeed()
        {
            try { _exo.SetSpeed(_speed); } catch { }
        }

        private void ToggleScale()
        {
            _crop = !_crop;
            _scaleBtn.Text = _crop ? L10n.T("填充") : L10n.T("适应");
            ApplyScale();
        }

        /// <summary>适应=完整显示（默认），填充=裁掉两侧黑边铺满屏幕。
        /// 用 ExoPlayer 的 PlayerView.ResizeMode 实现（Fit / Zoom），无需手动变换 surface。</summary>
        private void ApplyScale()
        {
            try { _exo.SetCrop(_crop); } catch { }
        }

        private void RotateScreen()
        {
            bool land = Resources.Configuration.Orientation == Android.Content.Res.Orientation.Landscape;
            RequestedOrientation = land
                ? ScreenOrientation.Portrait
                : ScreenOrientation.SensorLandscape;
        }

        private void ShowQueue()
        {
            if (_queue.Count == 0) return;
            var names = _queue.Select((s, i) => (i == _index ? "• " : "") + (i + 1) + ". " + s.DisplayTitle).ToArray();
            L10n.Dialog(this)
                .SetTitle(L10n.T("视频列表（") + _queue.Count + "）")
                .SetItems(names, (s, e) => PlayAt(e.Which, 0))
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Create().Show();
        }

        /* ============================================================
         * 控制层显隐 / 锁定
         * ============================================================ */

        private void ShowControls(bool show)
        {
            if (_locked) show = false;
            _controls.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            Cancel(_hideRun);
            if (show && !_locked)
            {
                _hideRun = new Java.Lang.Runnable(() => ShowControls(false));
                _h.PostDelayed(_hideRun, 4000);
            }
        }

        /// <summary>拖拽进度：去掉控件层全屏半透明遮罩，隐藏上下/中部栏，仅保留进度条本身可继续拖动，
        /// 使拖拽期间除时间提示外不遮挡视频画面。松手后恢复各栏可见性。</summary>
        private void SetDragOverlay(bool dragging)
        {
            if (_controls == null) return;
            if (dragging)
            {
                _controls.Background = null;
                if (_topBar != null) _topBar.Visibility = ViewStates.Gone;
                if (_centerBar != null) _centerBar.Visibility = ViewStates.Gone;
                if (_toolBar != null) _toolBar.Visibility = ViewStates.Gone;
            }
            else
            {
                _controls.SetBackgroundColor(new Color(unchecked((int)0x99000000)));
                if (_topBar != null) _topBar.Visibility = ViewStates.Visible;
                if (_centerBar != null) _centerBar.Visibility = ViewStates.Visible;
                if (_toolBar != null) _toolBar.Visibility = ViewStates.Visible;
            }
        }

        private void SetLocked(bool on)
        {
            _locked = on;
            _unlockBtn.Visibility = on ? ViewStates.Visible : ViewStates.Gone;
            ShowControls(!on);
            L10n.Toast(this, on ? L10n.T("已锁定，点右上角解锁") : L10n.T("已解锁"), ToastLength.Short).Show();
        }

        private void StartTick()
        {
            Cancel(_tickRun);
            _tickRun = new Java.Lang.Runnable(() =>
            {
                try
                {
                    if (!_seeking)
                    {
                        long pos = _exo.PositionMs;
                        long dur = _exo.DurationMs;
                        if (dur > 0) { _seek.Max = (int)dur; _seek.Progress = (int)Math.Min(pos, dur); }
                        _tCur.Text = Song.FormatMs((int)pos);
                        _tTotal.Text = Song.FormatMs((int)dur);
                    }
                    SyncPlayIcon();
                    UpdateTelemetry();
                    WatchTick();
                }
                catch { }
                _h.PostDelayed(_tickRun, 500);
            });
            _h.PostDelayed(_tickRun, 500);
        }

        /// <summary>右上角遥测：已缓冲秒数（ExoPlayer 缓冲时间轴）+ 本地已落盘字节（缓存代理统计）。
        /// 播放与暂停时都会刷新——暂停时代理仍在后台预取，所以「已缓冲」会持续增长。</summary>
        private void UpdateTelemetry()
        {
            try
            {
                if (_telemetry == null) return;
                long bufMs = _exo.BufferedPositionMs - _exo.PositionMs;
                if (bufMs < 0) bufMs = 0;
                int sec = (int)(bufMs / 1000);
                long bytes = VideoCacheProxy.Instance.CachedBytesFor(_currentUrl);
                double mb = bytes / 1048576.0;
                _telemetry.Text = L10n.T("已缓冲 ") + sec + "s · " + mb.ToString("0.0") + " MB";
            }
            catch { }
        }

        private void HideSystemUi()
        {
            try
            {
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    (int)SystemUiFlags.Fullscreen |
                    (int)SystemUiFlags.HideNavigation |
                    (int)SystemUiFlags.ImmersiveSticky |
                    (int)SystemUiFlags.LayoutStable |
                    (int)SystemUiFlags.LayoutHideNavigation |
                    (int)SystemUiFlags.LayoutFullscreen);
            }
            catch { }
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus) HideSystemUi();
        }

        public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            _h.PostDelayed(new Java.Lang.Runnable(ApplyScale), 200);
        }

        /* ============================================================
         * 手势：单击 / 双击 / 横向 seek / 纵向亮度音量
         * ============================================================ */

        private const int SwipeDp = 16;
        private float _downX, _downY, _startBright, _startVol;
        private int _startPos, _seekTarget;
        private long _lastTap;
        private float _lastTapX;
        private int _mode;   // 0=未定 1=seek 2=亮度/音量
        private bool _moved;

        private void OnStageTouch(object sender, View.TouchEventArgs e)
        {
            var ev = e.Event;
            e.Handled = true;
            if (_locked) return;
            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    _downX = ev.GetX(); _downY = ev.GetY();
                    _startPos = SafePos();
                    _seekTarget = _startPos;
                    _startBright = CurrentBrightness();
                    try { _startVol = _am.GetStreamVolume(Android.Media.Stream.Music); } catch { _startVol = 0; }
                    _moved = false; _mode = 0;
                    break;

                case MotionEventActions.Move:
                    float dx = ev.GetX() - _downX, dy = ev.GetY() - _downY;
                    float th = Dp(SwipeDp);
                    if (!_moved && (Math.Abs(dx) > th || Math.Abs(dy) > th))
                    {
                        _moved = true;
                        _mode = Math.Abs(dx) > Math.Abs(dy) * 1.1f ? 1 : 2;
                        Cancel(_tapRun);
                        // 手势拖拽期间隐藏控件层（含全屏遮罩），只保留时间/亮度音量提示
                        ShowControls(false);
                    }
                    if (!_moved) break;
                    int w = Math.Max(1, _stage.Width), hh = Math.Max(1, _stage.Height);
                    if (_mode == 1)
                    {
                        int dur = Math.Max(1, SafeDur());
                        _seekTarget = Clamp(_startPos + (int)(dx / w * dur * 1.2f), 0, dur);
                        ShowHud((_seekTarget >= _startPos ? "+" : "-") +
                                Song.FormatMs(Math.Abs(_seekTarget - _startPos)),
                                Song.FormatMs(_seekTarget) + " / " + Song.FormatMs(dur),
                                (int)(100f * _seekTarget / dur));
                    }
                    else
                    {
                        float delta = -dy / hh;
                        bool left = _downX < w / 2f;
                        if (left)
                        {
                            float b = ClampF(_startBright + delta * 1.4f, 0.02f, 1f);
                            SetBrightness(b);
                            ShowHud(L10n.T("亮度 ") + (int)(b * 100) + "%", L10n.T("左半屏上下滑动调节"), (int)(b * 100));
                        }
                        else
                        {
                            int v = Clamp((int)(_startVol + delta * _maxVol * 1.4f), 0, _maxVol);
                            try { _am.SetStreamVolume(Android.Media.Stream.Music, v, 0); } catch { }
                            ShowHud(L10n.T("音量 ") + v + " / " + _maxVol, L10n.T("右半屏上下滑动调节"),
                                (int)(100f * v / Math.Max(1, _maxVol)));
                        }
                    }
                    break;

                case MotionEventActions.Up:
                    if (_moved)
                    {
                        if (_mode == 1) { try { _exo.SeekTo(_seekTarget); } catch { } }
                        HideHudSoon();
                        // 手势拖拽只出时间提示，不显示控件层
                        break;
                    }
                    long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                    bool sameSide = (ev.GetX() > _stage.Width / 2f) == (_lastTapX > _stage.Width / 2f);
                    if (now - _lastTap < 320 && sameSide)
                    {
                        Cancel(_tapRun);
                        _lastTap = 0;
                        int delta = ev.GetX() > _stage.Width / 2f ? 10000 : -10000;
                        int target = Clamp(SafePos() + delta, 0, Math.Max(0, SafeDur()));
                        try { _exo.SeekTo(target); } catch { }
                        ShowHud(delta > 0 ? L10n.T("+10 秒") : L10n.T("-10 秒"),
                                Song.FormatMs(target) + " / " + Song.FormatMs(SafeDur()),
                                (int)(100f * target / Math.Max(1, SafeDur())));
                        HideHudSoon();
                    }
                    else
                    {
                        _lastTap = now;
                        _lastTapX = ev.GetX();
                        Cancel(_tapRun);
                        // 只有点击屏幕中间区域才唤起/切换控件，其余区域一律不显示控件
                        float tapX = ev.GetX(), tapY = ev.GetY();
                        int tapW = Math.Max(1, _stage.Width), tapH = Math.Max(1, _stage.Height);
                        bool center = tapX > tapW * 0.3f && tapX < tapW * 0.7f
                                   && tapY > tapH * 0.25f && tapY < tapH * 0.75f;
                        _tapRun = new Java.Lang.Runnable(() =>
                        {
                            if (center) ShowControls(_controls.Visibility != ViewStates.Visible);
                            else ShowControls(false);
                        });
                        _h.PostDelayed(_tapRun, 300);
                    }
                    break;
            }
        }

        private void ShowHud(string title, string sub, int pct)
        {
            Cancel(_hudHideRun);
            _hudText.Text = title;
            _hudSub.Text = sub;
            _hudBar.Progress = Clamp(pct, 0, 100);
            _hud.Visibility = ViewStates.Visible;
        }

        private void HideHudSoon()
        {
            Cancel(_hudHideRun);
            _hudHideRun = new Java.Lang.Runnable(() => _hud.Visibility = ViewStates.Gone);
            _h.PostDelayed(_hudHideRun, 700);
        }

        private float CurrentBrightness()
        {
            try
            {
                float b = Window.Attributes.ScreenBrightness;
                return b < 0 ? 0.6f : b;
            }
            catch { return 0.6f; }
        }

        private void SetBrightness(float b)
        {
            try
            {
                var lp = Window.Attributes;
                lp.ScreenBrightness = b;
                Window.Attributes = lp;
            }
            catch { }
        }

        private int SafePos() { try { return (int)_exo.PositionMs; } catch { return 0; } }
        private int SafeDur() { try { return (int)_exo.DurationMs; } catch { return 0; } }
        private static int Clamp(int v, int a, int b) => v < a ? a : (v > b ? b : v);
        private static float ClampF(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        private void Cancel(Java.Lang.IRunnable r) { if (r != null) try { _h.RemoveCallbacks(r); } catch { } }
        private int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);

        /* ============================================================
         * 生命周期
         * ============================================================ */

        protected override void OnPause()
        {
            try { if (_exo.IsPlaying) { _exo.Pause(); _pausedByUser = true; } } catch { }
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            Cancel(_tickRun); Cancel(_hideRun); Cancel(_tapRun); Cancel(_hudHideRun);
            // 宿主机退出播放器即结束一起看：只有本机在线才能一起看
            try { if (!string.IsNullOrEmpty(_watchPublishCard)) Vod.WatchTogether.Stop(_watchPublishCard); } catch { }
            try { _exo.Stop(); } catch { }
            try { _exo.Release(); } catch { }
            base.OnDestroy();
        }

        public override void OnBackPressed()
        {
            if (_locked) { SetLocked(false); return; }
            base.OnBackPressed();
        }
    }
}

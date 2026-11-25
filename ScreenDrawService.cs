using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.Provider;
using AndroidX.Core.App;

namespace AhoChatA
{
    /// <summary>
    /// 屏幕画笔叠加层。
    ///
    /// 由两个悬浮窗组成（均依赖 SYSTEM_ALERT_WINDOW 权限）：
    ///   1) 绘制窗（全屏）：最底层是一张铺满屏幕的「纯色填充」，用来遮蔽原屏幕内容以防干扰；
    ///      其上叠加一张透明画布（DrawView），画笔在其上绘制，橡皮擦用 Clear 模式擦除绘制
    ///      层（露出纯色底，故永远擦不掉纯色填充本身）。
    ///   2) 控件栏（屏幕底端、宽 80%、可整体拖拽）：画笔（兼作把手，长按其展开/收起、
    ///      拖拽其移动整栏、短按调画笔色与直径）、橡皮擦（可设擦除半径）、油漆桶
    ///      （调纯色填充色）、关闭画板。
    /// </summary>
    [Service(Exported = false, Name = "com.ztwl.ScreenDrawService")]
    public class ScreenDrawService : Service
    {
        public const string ActionToggle = "com.Ahochat.android.action.SCREEN_DRAW_TOGGLE";
        private const string ChannelId = "Mchat_draw";
        private const int NotificationId = 2003;

        /// <summary>当前是否正在运行（广播接收器据此切换开关）。</summary>
        public static bool IsRunning { get; private set; }

        /// <summary>当前实例。局域网画笔直播（DrawShareServer）通过它抓画面。</summary>
        public static ScreenDrawService Instance { get; private set; }

        /// <summary>要被直播的根视图：纯色底 + 透明画布，和本机看到的完全一致。</summary>
        public static View SharedRoot { get { return Instance == null ? null : Instance.drawRoot; } }

        private Android.Views.IWindowManager wm;
        private View drawRoot;
        private View barRoot;
        private DrawView drawView;
        private View bgView;
        private WindowManagerLayoutParams lpDraw;
        private WindowManagerLayoutParams lpBar;

        // 控件栏元素
        private ImageButton btnPen, btnEraser, btnBucket, btnClose;
        private View chooser;
        private LinearLayout swatches;
        private SeekBar sizeSeek;

        private bool expanded = true;
        private bool erasing;
        private Color fillColor = Color.ParseColor("#1b1b1b");
        private Color penColor = Color.White;
        private float penWidth = 8f;
        private float eraserWidth = 30f;
        private string chooserMode = "pen"; // "pen" | "bucket"
        private string sizeTarget = "pen";  // 底部尺寸条当前控制的是画笔还是橡皮擦

        private int screenW, screenH, cell;

        public override IBinder OnBind(Intent intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            IsRunning = true;
            Instance = this;
            wm = GetSystemService(WindowService).JavaCast<Android.Views.IWindowManager>();

            var metrics = Resources.DisplayMetrics;
            screenW = metrics.WidthPixels;
            screenH = metrics.HeightPixels;
            // 控件栏尺寸：先按屏宽/屏高算出「原尺寸」，再整体取一半 —— 用户要求控件栏缩到原来的一半。
            // 下限 24dp 兜底：再小手指点不准。
            int maxCellByWidth = (int)((screenW * 0.8f - 24) / 4);
            int maxCellByHeight = (int)(screenH * 0.1f) - 14;
            if (maxCellByHeight < 40) maxCellByHeight = 40;     // 兜底最小可点尺寸
            int fullCell = Math.Min(maxCellByWidth, maxCellByHeight);
            float density = Resources.DisplayMetrics.Density;
            int minCell = (int)(24 * density);
            cell = Math.Max(minCell, fullCell / 2);

            BuildDrawWindow();
            BuildBarWindow();
            EnsureChannel();
            StartForegroundCompat();

            // 画笔一开就自动通过统一端口对外直播，别的设备打开这个地址即可看到正在画的内容。
            // （只读镜像，不参与绘制，也没有任何控制权。）
            PostUi(() =>
            {
                try
                {
                    if (WebServer.IsRunning)
                    {
                        L10n.Toast(this, L10n.T("画笔已共享：") + WebServer.UrlOf("/draw"),
                            ToastLength.Long).Show();
                    }
                }
                catch { }
            });
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            StartForegroundCompat();
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            IsRunning = false;
            Instance = null;
            try { if (drawRoot != null) wm?.RemoveView(drawRoot); } catch { }
            try { if (barRoot != null) wm?.RemoveView(barRoot); } catch { }
            drawRoot = null;
            barRoot = null;
            base.OnDestroy();
        }

        /* ===================== 主线程调度 ===================== */
        private void PostUi(Action action)
        {
            if (action == null) return;
            new Handler(Looper.MainLooper).Post(action);
        }

        /* ===================== 绘制窗 ===================== */
        private void BuildDrawWindow()
        {
            drawRoot = LayoutInflater.From(this).Inflate(Resource.Layout.screen_draw, null);
            bgView = drawRoot.FindViewById(Resource.Id.sdBg);
            var host = drawRoot.FindViewById<FrameLayout>(Resource.Id.sdCanvasHost);
            drawView = new DrawView(this)
            {
                LayoutParameters = new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent)
            };
            host.AddView(drawView);

            Android.Views.WindowManagerTypes wmType = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? (Android.Views.WindowManagerTypes)2038      // TypeApplicationOverlay
                : (Android.Views.WindowManagerTypes)2002;      // TypePhone

            lpDraw = new WindowManagerLayoutParams(
                WindowManagerLayoutParams.MatchParent,
                WindowManagerLayoutParams.MatchParent,
                wmType,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen,
                (Android.Graphics.Format)(-3))                 // Translucent
            {
                Gravity = GravityFlags.Top | GravityFlags.Start,
                X = 0,
                Y = 0
            };

            try { wm.AddView(drawRoot, lpDraw); }
            catch (Exception ex)
            {
                Android.Util.Log.Error("ScreenDraw", $"添加绘制窗失败(可能未授权悬浮窗): {ex.Message}");
                StopSelf();
            }
        }

        /* ===================== 控件栏窗 ===================== */
        private void BuildBarWindow()
        {
            barRoot = LayoutInflater.From(this).Inflate(Resource.Layout.screen_draw_bar, null);

            btnPen = barRoot.FindViewById<ImageButton>(Resource.Id.sdPen);
            btnEraser = barRoot.FindViewById<ImageButton>(Resource.Id.sdEraser);
            btnBucket = barRoot.FindViewById<ImageButton>(Resource.Id.sdBucket);
            btnClose = barRoot.FindViewById<ImageButton>(Resource.Id.sdClose);
            chooser = barRoot.FindViewById(Resource.Id.sdChooser);
            swatches = barRoot.FindViewById<LinearLayout>(Resource.Id.sdSwatches);
            sizeSeek = barRoot.FindViewById<SeekBar>(Resource.Id.sdSizeSeek);

            // 统一图标尺寸为正方形（cell × cell）
            SetSquare(btnPen);
            SetSquare(btnEraser);
            SetSquare(btnBucket);
            SetSquare(btnClose);

            BuildSwatches();

            // 画笔按钮兼任把手：长按展开/收起、拖拽移动整栏、短按调画笔色与直径
            btnPen.SetOnTouchListener(new PenTouchListener(this));
            btnEraser.Click += (s, e) => SelectEraser();
            btnBucket.Click += (s, e) => ToggleBucket();
            btnClose.Click += (s, e) => StopSelf();

            sizeSeek.ProgressChanged += (s, e) =>
            {
                float w = e.Progress + 2;
                if (sizeTarget == "eraser")
                {
                    eraserWidth = w;
                    drawView.SetEraserWidth(w);
                }
                else
                {
                    penWidth = w;
                    drawView.SetPenWidth(w);
                }
            };

            // 初始：展开、画笔激活、调色面板（画笔模式）显示
            SelectPen();
            ApplyExpand(true);

            Android.Views.WindowManagerTypes wmType = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? (Android.Views.WindowManagerTypes)2038
                : (Android.Views.WindowManagerTypes)2002;

            lpBar = new WindowManagerLayoutParams(
                WindowManagerLayoutParams.WrapContent,
                WindowManagerLayoutParams.WrapContent,
                wmType,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen,
                (Android.Graphics.Format)(-3))
            {
                Gravity = GravityFlags.Top | GravityFlags.Start,
            };
            PositionBar();

            try { wm.AddView(barRoot, lpBar); }
            catch (Exception ex)
            {
                Android.Util.Log.Error("ScreenDraw", $"添加控件栏失败: {ex.Message}");
                StopSelf();
            }
        }

        private void SetSquare(ImageButton b)
        {
            b.LayoutParameters = new LinearLayout.LayoutParams(cell, cell);
        }

        private void BuildSwatches()
        {
            swatches.RemoveAllViews();
            string[] palette =
            {
                "#ffffff", "#000000", "#9e9e9e",
                "#f44336", "#ff9800", "#ffeb3b",
                "#4caf50", "#2196f3", "#00bcd4",
                "#9c27b0", "#e91e63", "#795548"
            };
            int sw = (int)(cell * 0.72f);
            foreach (var hex in palette)
            {
                var v = new View(this)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(sw, sw)
                    {
                        LeftMargin = 4,
                        RightMargin = 4,
                        TopMargin = 4,
                        BottomMargin = 4
                    }
                };
                v.SetBackgroundColor(Color.ParseColor(hex));
                v.SetOnClickListener(new SwatchListener(this, hex));
                swatches.AddView(v);
            }
        }

        // 调色面板里的色块点击：根据当前模式改画笔色或纯色填充色
        private class SwatchListener : Java.Lang.Object, View.IOnClickListener
        {
            private readonly ScreenDrawService svc;
            private readonly string hex;
            public SwatchListener(ScreenDrawService svc, string hex) { this.svc = svc; this.hex = hex; }
            public void OnClick(View v)
            {
                var color = Color.ParseColor(hex);
                if (svc.chooserMode == "pen")
                {
                    svc.penColor = color;
                    svc.drawView.SetPenColor(color);
                }
                else
                {
                    svc.fillColor = color;
                    svc.bgView.SetBackgroundColor(color);
                }
            }
        }

        /* ===================== 工具选择 ===================== */
        private void SelectPen()
        {
            erasing = false;
            drawView.SetErasing(false);
            sizeTarget = "pen";
            chooserMode = "pen";
            chooser.Visibility = ViewStates.Visible;
            sizeSeek.Visibility = ViewStates.Visible;
            SyncSeek();
            HighlightTool(btnPen, true);
            HighlightTool(btnEraser, false);
            PositionBar();
        }

        private void SelectEraser()
        {
            erasing = true;
            drawView.SetErasing(true);
            sizeTarget = "eraser";
            chooser.Visibility = ViewStates.Gone;
            sizeSeek.Visibility = ViewStates.Visible;
            SyncSeek();
            HighlightTool(btnPen, false);
            HighlightTool(btnEraser, true);
            PositionBar();
        }

        private void ToggleBucket()
        {
            if (chooserMode == "bucket" && chooser.Visibility == ViewStates.Visible)
            {
                chooser.Visibility = ViewStates.Gone;
                PositionBar();
                return;
            }
            chooserMode = "bucket";
            chooser.Visibility = ViewStates.Visible;
            sizeSeek.Visibility = ViewStates.Gone;
            HighlightTool(btnPen, false);
            HighlightTool(btnEraser, false);
            PositionBar();
        }

        // 把当前工具的尺寸同步到尺寸条（画笔/橡皮擦各自记录自己的直径）
        private void SyncSeek()
        {
            if (sizeTarget == "eraser")
                sizeSeek.Progress = (int)(eraserWidth - 2);
            else
                sizeSeek.Progress = (int)(penWidth - 2);
        }

        private void HighlightTool(ImageButton b, bool on)
        {
            b.SetBackgroundResource(on ? Resource.Drawable.screen_tool_active : Android.Resource.Color.Transparent);
        }

        /* ===================== 展开 / 收缩 / 拖拽 ===================== */
        private void ApplyExpand(bool expand)
        {
            expanded = expand;
            // 画笔按钮始终作为把手（拖拽 / 长按 / 短按），不随展开收起隐藏；
            // 收起时仅保留画笔，其余工具（橡皮擦 / 油漆桶 / 关闭）隐藏。
            btnEraser.Visibility = expand ? ViewStates.Visible : ViewStates.Gone;
            btnBucket.Visibility = expand ? ViewStates.Visible : ViewStates.Gone;
            btnClose.Visibility = expand ? ViewStates.Visible : ViewStates.Gone;
            // 收缩时若调色面板开着，一并收起
            if (!expand) chooser.Visibility = ViewStates.Gone;
            PositionBar();
        }

        private void ToggleExpand()
        {
            ApplyExpand(!expanded);
        }

        private void PositionBar()
        {
            if (lpBar == null) return;
            // 展开宽度 = 实际内容宽度（4 个单元格 + 间距/内边距），保证不裁切按钮；
            // 尺寸与内边距都按「一半」缩放后的 cell 计算。
            int width = expanded ? cell * 4 + 12 : cell + 6;
            lpBar.Width = width;
            lpBar.X = Math.Max(0, (screenW - width) / 2);
            // 估算控件栏总高（按钮行 + 内边距 + 调色面板），使底部贴近屏幕底（留 8px）
            bool chooserShown = expanded && chooser != null && chooser.Visibility == ViewStates.Visible;
            int chooserH = chooserShown ? (int)(cell * 0.72f) + 8 : 0;
            int barH = cell + 6 + chooserH;
            lpBar.Y = Math.Max(0, screenH - barH - 8);
            try { if (barRoot != null) wm.UpdateViewLayout(barRoot, lpBar); } catch { }
        }

        // 画笔按钮兼任把手：
        //   短按 → 选画笔并展开调色 / 直径面板（画笔自身的颜色与半径设置）；
        //   长按（不拖动）→ 展开 / 收起控件栏；
        //   长按 + 拖动 → 移动整个控件栏。
        private class PenTouchListener : Java.Lang.Object, View.IOnTouchListener
        {
            private readonly ScreenDrawService svc;
            private int initX, initY;
            private float touchX, touchY;
            private long downAt;
            private bool dragging, longPressed;
            private const int LongPressMs = 200;
            private const int MoveSlop = 6;

            public PenTouchListener(ScreenDrawService svc) { this.svc = svc; }

            public bool OnTouch(View v, MotionEvent e)
            {
                switch (e.Action)
                {
                    case MotionEventActions.Down:
                        longPressed = false;
                        dragging = false;
                        initX = svc.lpBar.X;
                        initY = svc.lpBar.Y;
                        touchX = e.RawX;
                        touchY = e.RawY;
                        downAt = System.Environment.TickCount;
                        return true;

                    case MotionEventActions.Move:
                        if (System.Environment.TickCount - downAt > LongPressMs) longPressed = true; // 长按就绪
                        if (longPressed)
                        {
                            float dx = e.RawX - touchX;
                            float dy = e.RawY - touchY;
                            if (Math.Abs(dx) > MoveSlop || Math.Abs(dy) > MoveSlop) dragging = true;
                            if (dragging)
                            {
                                svc.lpBar.X = initX + (int)dx;
                                svc.lpBar.Y = initY + (int)dy;
                                try { svc.wm.UpdateViewLayout(svc.barRoot, svc.lpBar); } catch { }
                            }
                        }
                        return true;

                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        if (System.Environment.TickCount - downAt > LongPressMs) longPressed = true;
                        if (!dragging)
                        {
                            if (longPressed)
                            {
                                // 长按（未拖动）：展开 / 收起控件栏
                                svc.ToggleExpand();
                            }
                            else
                            {
                                // 短按：选画笔，并展开调色 / 直径面板（画笔自身设置）
                                if (!svc.expanded) svc.ApplyExpand(true);
                                svc.SelectPen();
                            }
                        }
                        return true;
                }
                return false;
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
                var channel = new NotificationChannel(ChannelId, L10n.T("屏幕画笔"), NotificationImportance.Min)
                {
                    Description = L10n.T("屏幕叠加绘图板")
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

                var closeIntent = new Intent(this, typeof(ScreenDrawService));
                closeIntent.SetAction(ActionToggle);
                var closePending = PendingIntent.GetService(this, 2, closeIntent, flags);

                var notification = new NotificationCompat.Builder(this, ChannelId)
                    .SetSmallIcon(Resource.Drawable.icon)
                    .SetContentTitle(L10n.T("屏幕画笔运行中"))
                    .SetContentText(L10n.T("底部工具栏可绘制 / 擦除 / 调色：长按画笔展开收起，拖动画笔移动"))
                    .SetPriority((int)NotificationPriority.Min)
                    .SetOngoing(true)
                    .SetOnlyAlertOnce(true)
                    .SetShowWhen(false)
                    .SetContentIntent(pending)
                    .AddAction(0, L10n.T("关闭画板"), closePending)
                    .Build();

                StartForeground(NotificationId, notification);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("ScreenDraw", $"StartForeground 失败: {ex.Message}");
            }
        }

        /* ===================== 静态启停 ===================== */

        /// <summary>是否已授予「显示在其他应用上方」(SYSTEM_ALERT_WINDOW) 权限。</summary>
        public static bool CanDrawOverlays(Context context)
        {
            if (context == null) return false;
            if (Build.VERSION.SdkInt < BuildVersionCodes.M) return true;
            try { return Settings.CanDrawOverlays(context); }
            catch { return false; }
        }

        /// <summary>跳转系统设置页申请「显示在其他应用上方」权限。</summary>
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

        public static void Show(Context context)
        {
            if (context == null) return;
            // 未授权悬浮窗：直接返回，由调用方（MchatBridge）先去申请权限
            if (!CanDrawOverlays(context)) return;
            try
            {
                var intent = new Intent(context, typeof(ScreenDrawService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(intent);
                else
                    context.StartService(intent);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("ScreenDraw", $"启动屏幕画笔失败: {ex.Message}");
            }
        }

        public static void Hide(Context context)
        {
            if (context == null) return;
            try
            {
                var intent = new Intent(context, typeof(ScreenDrawService));
                context.StopService(intent);
            }
            catch { }
        }

        public static void Toggle(Context context)
        {
            if (context == null) return;
            if (IsRunning) Hide(context);
            else Show(context);
        }
    }
}

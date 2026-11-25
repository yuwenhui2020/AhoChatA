using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 在手机上远控 Windows 桌面（对应 desk 端）。
    ///
    /// 界面全部用代码构建、不引 XML 布局：这样新增一个页面不必改动 csproj 的资源清单，
    /// 也避开 aapt2 打包资源的坑（本工程历史上被 assets 路径坑过）。
    ///
    /// 两段式界面：
    ///   ① 连接页 —— 列出局域网上"可被远控"的设备（由 hello 里的 rd 字段发现）+ 手动输入；
    ///   ② 会话页 —— 显示画面并处理触摸/按键输入。
    /// </summary>
    [Activity(Label = "@string/act_remotedesk", Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.KeyboardHidden)]
    public class RemoteDeskActivity : BaseActivity
    {
        private LinearLayout root;
        private LinearLayout connectPage;
        private LinearLayout sessionPage;
        private ImageView screen;
        /// <summary>H.264 硬解码的渲染目标。H.264 走这里，JPEG 走上面的 ImageView。</summary>
        private SurfaceView surfaceView;

        /// <summary>画面舞台：ImageView（JPEG）与 SurfaceView（H.264）叠放在这里。</summary>
        private FrameLayout stage;
        private TextView statusTxt;
        /// <summary>从「附近设备」一键远控进来时隐藏，IP/端口/设备码已由设备卡片带入。</summary>
        private LinearLayout manualCardRef;
        private RdSession session;

        private double lastFx, lastFy;
        private bool dragging;
        private long _lastMoveSentAt;   // MOVE 节流：12ms 内的连续移动只发最新坐标

        // ---- 触控板模式（相对鼠标）----
        // 绝对定位在手机上"点哪跳哪"，看文档还行；但远程画图/玩游戏必须用相对位移，
        // 手感才像真的鼠标（rustdesk 也是这两种模式并存，可随时切换）。
        private bool trackpad;
        private float tpLastX, tpLastY;
        private float tpDownX, tpDownY;
        private bool tpMoved;

        // ---- 画质档位 ----
        private static readonly string[] PresetNames = new string[] { L10n.T("流畅"), L10n.T("均衡"), L10n.T("高清") };
        private static readonly int[] PresetFps = new int[] { 15, 30, 30 };
        private static readonly int[] PresetWidth = new int[] { 960, 1280, 1920 };
        private int preset = 1;

        // ---- 断线自动重连 ----
        private string lastIp, lastCode, lastPwd;
        private int lastPort;
        private int reconnectTries;
        /// <summary>本次连接是否走局域网免密直连（设备码/密码都不需要）。</summary>
        private bool lanDirect;
        private bool userStopped;
        // 帧信箱：读线程放最新帧，主线程合并绘制（见 StartSession 的 FrameReady）
        private sealed class FrameBox
        {
            public Bitmap Pending;
            public bool Posting;
            public Bitmap Shown;
        }
        private readonly FrameBox _frameBox = new FrameBox();

        private void DrawPendingFrame()
        {
            Bitmap bmp;
            lock (_frameBox)
            {
                bmp = _frameBox.Pending;
                _frameBox.Pending = null;
                _frameBox.Posting = false;
                if (bmp == null) return;
            }
            try
            {
                ApplyAspect(bmp.Width, bmp.Height);
                screen.SetImageBitmap(bmp);
                var old = _frameBox.Shown;
                _frameBox.Shown = bmp;
                if (old != null && old != bmp) { try { old.Recycle(); } catch { } }
            }
            catch { }
        }

        // 一键远控（auto=1）：握手期内的连接抖动自动重试，用户无需再点第二次
        private bool autoMode;
        private int autoTries;
        private string _autoIp, _autoCode, _autoName;
        private int _autoPort;
        private const int AutoMaxTries = 4;
        private bool _awaitingPassword;   // 已为L10n.T("密码不正确")弹过框，避免重复弹

        // 远控页配色别名：统一映射到 IosStyle，保持与「允许被远控」页同一套视觉。
        // （这些短名在页面内多处使用，集中定义避免散落 magic number。）
        private static readonly int Accent = IosStyle.Accent;
        private static readonly int Main = IosStyle.Text;
        private static readonly int Card = IosStyle.Surface;
        private static readonly int Bg = IosStyle.Background;
        private static readonly int Line = IosStyle.Separator;
        private static readonly int AccentText = IosStyle.Surface;
        private static readonly int Sub = IosStyle.Secondary;

        private TextView tpBtn;
        private TextView qualityBtn;
        private TextView kbBtn;
        /// <summary>内置数字化全键盘面板：点工具栏"键盘"就地显隐，不再弹系统软键盘遮挡画面。</summary>
        private LinearLayout keyboardPanel;
        /// <summary>粘滞修饰键状态（点一次后持续生效，直到再点一次取消），方便组合键。</summary>
        private bool _kbShift, _kbCtrl, _kbAlt;
        private TextView _kbShiftBtn, _kbCtrlBtn, _kbAltBtn;

        // ---- 记住上次连接（设备页一键远控靠它：第二次只需点一下，不用再输密码）----
        private const string PrefsName = "Mchat_rd_client";
        private Android.Content.ISharedPreferences Prefs
        {
            get { return GetSharedPreferences(PrefsName, FileCreationMode.Private); }
        }

        private static string PwKey(string ip, int port) { return "pw_" + ip + "_" + port; }

        private string SavedPassword(string ip, int port)
        {
            try { return Prefs.GetString(PwKey(ip, port), "") ?? ""; } catch { return ""; }
        }

        private void SavePassword(string ip, int port, string pw)
        {
            try
            {
                Prefs.Edit().PutString(PwKey(ip, port), pw ?? "")
                    .PutString("last_ip", ip).PutInt("last_port", port).Apply();
            }
            catch { }
        }
        private long downAt;
        private long longPressAt;
        private bool longPressFired;
        private int lastFrameW, lastFrameH;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // 【必须常亮】远控是"盯着屏幕看+操作"的场景，息屏会带来两个后果：
            // SurfaceView 不再消费帧 → MediaCodec 输出缓冲区堆满 → 解码变慢甚至卡住
            // → 读取循环被拖住 → 电脑端写 socket 超时（>3s）判定对端不收数据，直接断连。
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(new Color(IosStyle.Background));
            root.LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            SetContentView(root);

            root.AddView(BuildTopBar());
            BuildConnectPage();
            BuildSessionPage();

            ShowConnectPage();
            ApplyIntentTarget();
        }

        /// <summary>
        /// 从「附近设备」点远控进来（auto=1）时，IP/端口/设备码已由卡片带入：
        /// 隐藏手动连接卡片，有记住的密码直接连，否则立即弹密码框在远控时输入。
        /// 普通入口（直接打开本页）仍保留手动连接表单。
        /// </summary>
        private void ApplyIntentTarget()
        {
            try
            {
                Intent it = Intent;
                string ip = it != null ? (it.GetStringExtra("ip") ?? "") : "";
                int port = it != null ? it.GetIntExtra("port", DeskShare.RdWire.DefaultRemotePort) : DeskShare.RdWire.DefaultRemotePort;
                string code = it != null ? (it.GetStringExtra("code") ?? "") : "";
                string targetName = it != null ? (it.GetStringExtra("name") ?? "") : "";
                bool auto = it != null && it.GetBooleanExtra("auto", false);
                bool direct = it != null && it.GetBooleanExtra("lanDirect", false);

                // 没有显式目标：用上次连过的那台，省得每次重新敲
                if (string.IsNullOrEmpty(ip))
                {
                    ip = Prefs.GetString("last_ip", "") ?? "";
                    port = Prefs.GetInt("last_port", DeskShare.RdWire.DefaultRemotePort);
                }
                if (string.IsNullOrEmpty(ip)) return;

                if (ipBoxRef != null) ipBoxRef.Text = ip;
                if (portBoxRef != null) portBoxRef.Text = port.ToString();
                if (codeBoxRef != null) codeBoxRef.Text = DeskShare.RdDeviceCode.Format(code);

                string pw = SavedPassword(ip, port);
                if (pwBoxRef != null) pwBoxRef.Text = pw;

                if (auto)
                {
                    // 一键远控：不展示 IP / 端口 / 设备码输入框，设备信息都已自动带来。
                    // 没保存过密码时先用空密码直连：MchatW 没设密码时加密通道本就允许空密码登录，
                    // 绝大多数情况一次就连上，不必再弹密码框；只有被明确告知"密码不正确"才弹框。
                    autoMode = true;
                    autoTries = 0;
                    _autoIp = ip; _autoPort = port; _autoCode = code;
                    _autoName = string.IsNullOrEmpty(targetName) ? ip : targetName;
                    if (manualCardRef != null) manualCardRef.Visibility = ViewStates.Gone;
                    statusTxt.Text = L10n.T("正在连接 ") + _autoName + (direct ? L10n.T("（局域网直连）") : "") + " …";
                    StartSession(ip, port, code, !string.IsNullOrEmpty(pw) ? pw : "", direct);
                    return;
                }

                if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(pw))
                {
                    // 有保存的密码 → 直接连，不再让用户点第二次
                    StartSession(ip, port, code, pw);
                }
                else if (pwBoxRef != null && pwBoxRef.Text.Length == 0)
                {
                    pwBoxRef.RequestFocus();
                }
            }
            catch { }
        }

        public override void OnBackPressed()
        {
            if (sessionPage.Visibility == ViewStates.Visible)
            {
                StopSession();
                ShowConnectPage();
                return;
            }
            base.OnBackPressed();
        }

        protected override void OnDestroy()
        {
            try { StopSession(); } catch { }
            base.OnDestroy();
        }

        /* ===================== 顶部标题栏 ===================== */

        private View BuildTopBar()
        {
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            IosStyle.StyleToolbar(bar);
            bar.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(56));

            var back = new FrameLayout(this) { Clickable = true };
            back.AddView(new IconView(this, IconKind.Back) { IconColor = Accent },
                new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
            back.Click += (s, e) =>
            {
                if (sessionPage != null && sessionPage.Visibility == ViewStates.Visible)
                {
                    StopSession();
                    ShowConnectPage();
                }
                else Finish();
            };
            bar.AddView(back, new LinearLayout.LayoutParams(Dp(44), Dp(44)));

            var title = new TextView(this) { Text = L10n.T("远程桌面"), TextSize = 17 };
            IosStyle.StyleTitle(title);
            title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            bar.AddView(title);

            return bar;
        }

        /* ===================== 连接页 ===================== */

        private void BuildConnectPage()
        {
            connectPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            connectPage.SetPadding(Dp(IosStyle.PageInset), 0, Dp(IosStyle.PageInset), Dp(IosStyle.PageInset));
            root.AddView(connectPage, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            // —— 发现的电脑：只显示 hello 里带 rd（远控端口）的设备 ——
            var foundCard = Card_(connectPage, L10n.T("可被远控的设备"));
            int found = 0;
            try
            {
                List<Dictionary<string, string>> peers = CoreHost.Bridge?.OnlinePeers();
                if (peers != null)
                {
                    foreach (var p in peers)
                    {
                        int rd = 0;
                        if (p.TryGetValue("rd", out var rds)) int.TryParse(rds, out rd);
                        if (rd <= 0) continue;
                        if (!p.TryGetValue("ip", out var ip) || string.IsNullOrEmpty(ip)) continue;
                        string name = p.TryGetValue("name", out var nm) ? nm : ip;
                        string dc = p.TryGetValue("dc", out var dcs) ? (dcs ?? "") : "";
                        found++;
                        AddPeerRow(foundCard, name, ip, rd, dc);
                    }
                }
            }
            catch { }

            if (found == 0)
            {
                var empty = new TextView(this)
                {
                    Text = L10n.T("暂未发现可被远控的电脑。\n请确认电脑上运行了 desk 并已「启动被控端」。"),
                    TextSize = IosStyle.CaptionSize
                };
                empty.SetTextColor(new Color(IosStyle.Secondary));
                empty.SetPadding(Dp(IosStyle.PageInset - 2), Dp(12), Dp(IosStyle.PageInset - 2), Dp(12));
                foundCard.AddView(empty);
            }

            // —— 手动连接（无卡片目标时才用；一键远控进来会被隐藏）——
            var manualCard = Card_(connectPage, L10n.T("手动连接"));
            manualCardRef = manualCard;
            var ipBox = Row_(manualCard, L10n.T("IP 地址"), "");
            var portBox = Row_(manualCard, L10n.T("端口"), DeskShare.RdWire.DefaultRemotePort.ToString());
            portBox.InputType = InputTypes.ClassNumber;
            var codeBox = Row_(manualCard, L10n.T("设备码"), "");
            codeBox.InputType = InputTypes.ClassNumber;
            var pwdBox = Row_(manualCard, L10n.T("连接密码"), "");
            pwdBox.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
            ipBoxRef = ipBox; portBoxRef = portBox; codeBoxRef = codeBox; pwBoxRef = pwdBox;

            statusTxt = new TextView(this) { Text = "", TextSize = IosStyle.CaptionSize };
            statusTxt.SetTextColor(new Color(IosStyle.Secondary));
            statusTxt.SetPadding(Dp(IosStyle.PageInset - 2), Dp(10), Dp(IosStyle.PageInset - 2), Dp(4));
            connectPage.AddView(statusTxt);

            var go = PrimaryButton(L10n.T("连接"));
            go.Click += (s, e) =>
            {
                int port;
                if (!int.TryParse(portBox.Text, out port) || port <= 0) port = DeskShare.RdWire.DefaultRemotePort;
                StartSession(ipBox.Text.Trim(), port, codeBox.Text, pwdBox.Text);
            };
            connectPage.AddView(go, Margins(Dp(IosStyle.PageInset - 2), Dp(8), Dp(IosStyle.PageInset - 2), Dp(20)));
        }

        private void AddPeerRow(LinearLayout card, string name, string ip, int port, string deviceCode)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.SetPadding(Dp(IosStyle.PageInset), Dp(8), Dp(IosStyle.PageInset), Dp(8));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var t = new TextView(this) { Text = name, TextSize = IosStyle.BodySize };
            t.SetTextColor(new Color(IosStyle.Text));
            row.AddView(t);

            var d = new TextView(this) { Text = ip + " : " + port + (string.IsNullOrEmpty(deviceCode) ? "" : L10n.T(" · 设备码 ") + DeskShare.RdDeviceCode.Format(deviceCode)), TextSize = IosStyle.CaptionSize };
            d.SetTextColor(new Color(IosStyle.Secondary));
            d.SetPadding(0, Dp(2), 0, 0);
            row.AddView(d);

            // 这台是通过局域网 hello 发现的：直接免设备码/密码连接，不再弹输入表单
            row.Click += (s, e) => StartSession(ip, port, deviceCode, "", true);
            card.AddView(row);
            card.AddView(Divider());
        }

        /// <summary>一键远控时只问连接密码：设备码已从 hello 自动获取，无需用户输入。</summary>
        private void PromptPasswordOnly(string name, string ip, int port, string deviceCode)
        {
            string saved = SavedPassword(ip, port);
            if (!string.IsNullOrEmpty(saved))
            {
                StartSession(ip, port, deviceCode, saved);
                return;
            }

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(4), Dp(18), Dp(0));

            var tip = new TextView(this)
            {
                Text = L10n.T("设备码已自动获取，无需输入；请输入电脑端显示的连接密码。"),
                TextSize = IosStyle.CaptionSize
            };
            tip.SetTextColor(new Color(IosStyle.Secondary));
            box.AddView(tip);

            var pwd = new EditText(this) { Hint = L10n.T("连接密码") };
            pwd.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
            box.AddView(pwd);

            L10n.Dialog(this)
                .SetTitle(L10n.T("远程控制 ") + name)
                .SetMessage(ip + " : " + port)
                .SetView(box)
                .SetNegativeButton(L10n.T("取消"), (s, e) =>
                {
                    // 取消后让用户仍能走手动连接（否则一键入口下没有补填路径）
                    if (manualCardRef != null) manualCardRef.Visibility = ViewStates.Visible;
                })
                .SetPositiveButton(L10n.T("连接"), (s, e) => StartSession(ip, port, deviceCode, pwd.Text))
                .Show();
        }

        /// <summary>设备列表里点电脑：设备码自动带入，只让用户确认连接密码。</summary>
        private void PromptCredentials(string name, string ip, int port, string deviceCode)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(8), Dp(18), Dp(0));

            var codeTip = new TextView(this)
            {
                Text = L10n.T("设备码：") + DeskShare.RdDeviceCode.Format(deviceCode ?? "") + L10n.T("（已自动获取）"),
                TextSize = IosStyle.CaptionSize
            };
            codeTip.SetTextColor(new Color(IosStyle.Secondary));
            box.AddView(codeTip);

            string saved = SavedPassword(ip, port);
            var pwd = new EditText(this) { Text = saved, Hint = L10n.T("连接密码（连过一次就会记住）") };
            pwd.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
            box.AddView(pwd);

            // 已经连过（有记住的密码）就不必再弹框，点一下直接连
            if (!string.IsNullOrEmpty(saved))
            {
                StartSession(ip, port, deviceCode, saved);
                return;
            }

            L10n.Dialog(this)
                .SetTitle(L10n.T("连接到 ") + name)
                .SetMessage(ip + " : " + port)
                .SetView(box)
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .SetPositiveButton(L10n.T("连接"), (s, e) => StartSession(ip, port, deviceCode, pwd.Text))
                .Show();
        }

        /* ===================== 会话页 ===================== */

        private void BuildSessionPage()
        {
            sessionPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sessionPage.Visibility = ViewStates.Gone;
            root.AddView(sessionPage, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            // 顶部标题栏由 root 统一提供（OnCreate 里已加过一次），这里不再重复添加，
            // 否则远控会话页会顶着两条一模一样的"远程桌面"标题，白占一截高度。

            // JPEG 走 ImageView、H.264 走 SurfaceView，两者要占用同一块区域、靠可见性切换。
            // 如果直接把它们当兄弟节点加进竖向 LinearLayout，两个都带 weight=1 时会各分一半高度，
            // 即使只有一个可见，SurfaceView 的绘制区域也会和预期不一致。
            // 这里用 FrameLayout 让二者严格重叠，谁可见谁就铺满整块画面区。
            stage = new FrameLayout(this);
            stage.SetBackgroundColor(Color.Black);

            screen = new ImageView(this);
            screen.SetBackgroundColor(Color.Black);
            screen.SetScaleType(ImageView.ScaleType.FitXy);
            stage.AddView(screen, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // H.264 硬解码直接画到这个 SurfaceView，帧数据不经过应用内存。
            surfaceView = new SurfaceView(this);
            surfaceView.SetBackgroundColor(Color.Black);
            surfaceView.Visibility = ViewStates.Gone;
            // 【必须置顶】SurfaceView 默认的图层在窗口之下，靠系统在窗口的
            // TransparentRegion 上"打洞"才能露出来。实测本机型（MIUI/Android 12）上
            // 这个洞没有被打出来（dumpsys SurfaceFlinger 里窗口的
            // TransparentRegion 是空的 count=0），于是不透明的窗口黑底把视频层整块盖住：
            // 解码计数一路涨、SurfaceView 的 activeBuffer 也有真实画面，屏幕却全黑。
            // 置顶后视频层直接压在窗口之上，不再依赖打洞。它的图层范围严格等于
            // 本控件的布局区域，不会遮住上下两条工具栏。
            surfaceView.SetZOrderOnTop(true);
            stage.AddView(surfaceView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            surfaceView.Holder.AddCallback(new SurfaceHolderCallback(this));

            sessionPage.AddView(stage, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetBackgroundColor(new Color(IosStyle.Surface));
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));

            bar.AddView(ToolButton("Esc", () => session?.KeyPress(0x1B)), FillBtn());
            bar.AddView(ToolButton("Win", () => session?.KeyPress(0x5B)), FillBtn());
            bar.AddView(ToolButton(L10n.T("右键"), () =>
            {
                // 当前坐标上直接点一次右键
                RdSession s = session;
                if (s == null) return;
                s.MouseDown(RdSession.BtnRight, lastFx, lastFy);
                s.MouseUp(RdSession.BtnRight, lastFx, lastFy);
            }), FillBtn());
            kbBtn = ToolButton(L10n.T("键盘"), ToggleKeyboard);
            bar.AddView(kbBtn, FillBtn());
            tpBtn = ToolButton(L10n.T("触控板"), () => { trackpad = !trackpad; RefreshToolbar(); });
            bar.AddView(tpBtn, FillBtn());
            qualityBtn = ToolButton(L10n.T("均衡"), () =>
            {
                preset = (preset + 1) % PresetNames.Length;
                ApplyPreset();
                RefreshToolbar();
            });
            bar.AddView(qualityBtn, FillBtn());
            bar.AddView(ToolButton(L10n.T("断开"), () =>
            {
                userStopped = true;
                StopSession();
                ShowConnectPage();
            }), FillBtn());

            sessionPage.AddView(bar, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // 内置数字化全键盘：默认隐藏，点工具栏"键盘"就地展开在画面下方，
            // 不弹系统软键盘（那会把画面顶掉一半，远控时看不到被控端在做什么）。
            keyboardPanel = BuildKeyboardPanel();
            sessionPage.AddView(keyboardPanel, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            AttachTouch();
        }

        private void AttachTouch()
        {
            // 【两个视图都要绑】
            // JPEG 模式显示的是 ImageView 里的位图，H.264 模式显示的是 SurfaceView，
            // 而且这两者靠可见性切换 —— 被隐藏的 View 收不到任何触摸事件。
            // 只绑 screen 的话，一旦进了 H.264 远控，手指在画面上怎么点都没有反应。
            screen.Touch += (s, e) => HandleScreenTouch(screen, e);
            surfaceView.Touch += (s, e) => HandleScreenTouch(surfaceView, e);

            screen.LongClick += (s, e) => HandleScreenLongClick();
            surfaceView.LongClick += (s, e) => HandleScreenLongClick();
        }

        /// <summary>长按后松手判定为右键（左右键在按下/抬起的配对里完成）。</summary>
        private void HandleScreenLongClick()
        {
            RdSession curs = session;
            if (curs == null || !dragging) return;
            longPressFired = true;
            curs.MouseUp(RdSession.BtnLeft, lastFx, lastFy);
            curs.MouseDown(RdSession.BtnRight, lastFx, lastFy);
        }

        /// <summary>把画面上的触摸换算成 0..1 归一化坐标发给电脑。</summary>
        /// <param name="src">收到事件的那个视图；用它的宽高做换算基准。</param>
        private void HandleScreenTouch(Android.Views.View src, View.TouchEventArgs e)
        {
            {
                RdSession curs0 = session;
                if (curs0 == null) { e.Handled = true; return; }

                float x = e.Event.GetX();
                float y = e.Event.GetY();
                float vw = src.Width;
                float vh = src.Height;
                if (vw <= 0 || vh <= 0) { e.Handled = true; return; }

                double fx = x / vw;
                double fy = y / vh;
                if (fx < 0) fx = 0; if (fx > 1) fx = 1;
                if (fy < 0) fy = 0; if (fy > 1) fy = 1;
                lastFx = fx;
                lastFy = fy;

                if (trackpad) { HandleTrackpadTouch(e.Event); e.Handled = true; return; }

                switch (e.Event.Action)
                {
                    case MotionEventActions.Down:
                        dragging = true;
                        downAt = Java.Lang.JavaSystem.CurrentTimeMillis();
                        longPressAt = downAt + 600;
                        longPressFired = false;
                        curs0.MouseMove(fx, fy);
                        curs0.MouseDown(RdSession.BtnLeft, fx, fy);
                        e.Handled = true;
                        break;

                    case MotionEventActions.Move:
                        if (longPressFired) { e.Handled = true; break; }
                        // 节流：手机触摸采样 60~120Hz，逐条发会把视频帧和网络挤爆。
                        // 12ms（≈80Hz）足够跟手，其余中间点直接丢弃；松手时的 Up 会
                        // 带最终坐标补齐，不会丢终点。
                        {
                            long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                            if (now - _lastMoveSentAt >= 12)
                            {
                                _lastMoveSentAt = now;
                                curs0.MouseMove(fx, fy);
                            }
                        }
                        e.Handled = true;
                        break;

                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        if (dragging)
                        {
                            if (longPressFired) curs0.MouseUp(RdSession.BtnRight, fx, fy);
                            else curs0.MouseUp(RdSession.BtnLeft, fx, fy);
                        }
                        dragging = false;
                        longPressFired = false;
                        e.Handled = true;
                        break;

                    default:
                        e.Handled = true;
                        break;
                }
            }
        }


        /// <summary>
        /// 触控板模式：手指位移 = 鼠标位移；轻点 = 左键单击。
        /// 灵敏度 1.6 是"一屏手机滑动 ≈ 走完 1.6 屏桌面"的经验值，太小划不动，太大点不准。
        /// </summary>
        private void HandleTrackpadTouch(MotionEvent ev)
        {
            RdSession s = session;
            if (s == null) return;
            const float Sensitivity = 1.6f;

            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    tpLastX = ev.GetX();
                    tpLastY = ev.GetY();
                    tpDownX = tpLastX;
                    tpDownY = tpLastY;
                    tpMoved = false;
                    break;

                case MotionEventActions.Move:
                    float dx = ev.GetX() - tpLastX;
                    float dy = ev.GetY() - tpLastY;
                    tpLastX = ev.GetX();
                    tpLastY = ev.GetY();
                    if (Math.Abs(ev.GetX() - tpDownX) + Math.Abs(ev.GetY() - tpDownY) > 8) tpMoved = true;
                    if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) break;
                    s.MouseRelative((int)Math.Round(dx * Sensitivity), (int)Math.Round(dy * Sensitivity));
                    break;

                case MotionEventActions.Up:
                    if (!tpMoved)
                    {
                        // 轻点 = 在当前光标处单击（远端 host 会忽略坐标，用自己那里的光标位置）
                        s.MouseDown(RdSession.BtnLeft, lastFx, lastFy);
                        s.MouseUp(RdSession.BtnLeft, lastFx, lastFy);
                    }
                    break;
            }
        }

        private void ApplyPreset()
        {
            RdSession s = session;
            if (s == null) return;
            try { s.SetQuality(PresetFps[preset], PresetWidth[preset]); }
            catch { }
        }

        private void RefreshToolbar()
        {
            try
            {
                if (tpBtn != null)
                {
                    tpBtn.Text = trackpad ? L10n.T("触控板（已开）") : L10n.T("触控板");
                    tpBtn.SetTextColor(new Color(trackpad ? Accent : Main));
                    tpBtn.SetTypeface(null, trackpad ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                }
                if (qualityBtn != null) qualityBtn.Text = PresetNames[preset];
            }
            catch { }
        }

        private void ApplyAspect(int fw, int fh)
        {
            // 画面容器现在是 FrameLayout（ImageView 与 SurfaceView 叠放），
            // 原来的 LinearLayout.LayoutParams 强转会直接抛 InvalidCastException，
            // 统一交给 ApplyVideoAspect 处理。
            if (fw <= 0 || fh <= 0) return;
            if (fw == lastFrameW && fh == lastFrameH) return;
            lastFrameW = fw;
            lastFrameH = fh;
            ApplyVideoAspect(fw, fh);
        }

        /// <summary>
        /// 等 SurfaceView 的 Surface 真正创建好，绑给会话。
        ///
        /// 【为什么不能只取一次】
        /// SurfaceView 从隐藏切到可见后，Surface 是**异步**创建的：这一瞬间
        /// Holder.Surface 往往还是 null，要过几十毫秒才真的有。
        /// 如果只取一次就绑不上 → 解码器永远配不出来 → 手机端一直黑屏。
        /// 所以这里轮询一小会儿，直到拿到有效 Surface 为止。
        /// </summary>
        /// <summary>
        /// 按视频真实宽高比摆放 SurfaceView。
        /// 直接把 MatchParent 交给 SurfaceView 会把 16:9 的桌面硬拉成手机竖屏的长条，
        /// 文字和图标全部变形。这里按"完整装进舞台、居中、不裁剪"来算。
        /// </summary>
        private void ApplyVideoAspect(int vw, int vh)
        {
            try
            {
                if (stage == null || surfaceView == null || vw <= 0 || vh <= 0) return;
                int sw = stage.Width, sh = stage.Height;
                if (sw <= 0 || sh <= 0)
                {
                    // 布局还没算完，等一帧再来
                    stage.Post(() => ApplyVideoAspect(vw, vh));
                    return;
                }
                double scale = Math.Min((double)sw / vw, (double)sh / vh);
                int nw = (int)Math.Round(vw * scale);
                int nh = (int)Math.Round(vh * scale);
                FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(nw, nh);
                lp.Gravity = GravityFlags.Center;
                surfaceView.LayoutParameters = lp;
                if (screen != null)
                {
                    // 两个控件要用各自独立的 LayoutParams 实例（会被父容器接管）
                    FrameLayout.LayoutParams lp2 = new FrameLayout.LayoutParams(nw, nh);
                    lp2.Gravity = GravityFlags.Center;
                    screen.LayoutParameters = lp2;
                }
            }
            catch { }
        }

        private void WaitAndBindSurface()
        {
            if (surfaceView == null || session == null) return;
            try
            {
                ISurfaceHolder holder = surfaceView.Holder;
                if (holder != null && holder.Surface != null && holder.Surface.IsValid)
                {
                    session.RenderSurface = holder.Surface;
                    return;
                }
            }
            catch { }
            // 起一个后台轮询：最多等 3 秒，每 50ms 试一次。
            System.Threading.Thread worker = new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    try
                    {
                        ISurfaceHolder h = surfaceView.Holder;
                        if (h != null && h.Surface != null && h.Surface.IsValid)
                        {
                            RdSession s = session;
                            if (s != null) s.RenderSurface = h.Surface;
                            return;
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(50);
                }
            });
            worker.IsBackground = true;
            worker.Name = L10n.T("等 Surface 创建");
            worker.Start();
        }

        /// <summary>工具栏"键盘"：就地展开/收起内置全键盘，不弹系统软键盘遮挡画面。</summary>
        private void ToggleKeyboard()
        {
            if (keyboardPanel == null) return;
            bool show = keyboardPanel.Visibility != ViewStates.Visible;
            keyboardPanel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            if (kbBtn != null) kbBtn.SetTextColor(new Color(show ? Accent : Main));
        }

        /// <summary>
        /// 内置数字化全键盘：功能行 + 数字行 + 三行字母 + 符号行 + 空格/回车。
        /// 修饰键（Shift/Ctrl/Alt）为粘滞态：点一下持续生效、发完字符自动抬起，
        /// 再点一下取消；中文/任意字符用顶部文本框，TypeText 会自动切到 UnicodeText。
        /// </summary>
        private LinearLayout BuildKeyboardPanel()
        {
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetBackgroundColor(new Color(Card));
            panel.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
            panel.Visibility = ViewStates.Gone;

            // 文本输入行（中文 / 粘贴 → UnicodeText type=50）
            var textRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var ed = new EditText(this) { Hint = L10n.T("中文/任意文本 → 远端"), TextSize = 13 };
            ed.SetTextColor(new Color(Main));
            ed.SetBackgroundColor(new Color(Bg));
            ed.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
            textRow.AddView(ed, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var send = NewKey(L10n.T("发送"));
            send.SetTextColor(new Color(AccentText));
            send.SetBackgroundColor(new Color(Accent));
            send.Click += (s, e) =>
            {
                try
                {
                    string t = ed.Text ?? "";
                    if (t.Length > 0) { session?.TypeText(t); ed.Text = ""; }
                }
                catch { }
            };
            textRow.AddView(send, new LinearLayout.LayoutParams(Dp(56), ViewGroup.LayoutParams.WrapContent));
            panel.AddView(textRow, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            AddKeyRow(panel,
                new[] { "Esc", "Tab", "Ctrl", "Alt", "Shift", "Win", "←", "↑", "↓", "→", "Del", "⌫" },
                new[] { 0x1B, 0x09, 0x11, 0x12, 0x10, 0x5B, 0x25, 0x26, 0x28, 0x27, 0x2E, 0x08 },
                new[] { false, false, true, true, true, false, false, false, false, false, false, false });
            AddKeyRow(panel,
                new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
                new[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30 }, null);
            AddKeyRow(panel,
                new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
                new[] { 0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50 }, null);
            AddKeyRow(panel,
                new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L" },
                new[] { 0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C }, null);
            AddKeyRow(panel,
                new[] { "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" },
                new[] { 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC, 0xBE, 0xBF }, null);
            AddKeyRow(panel,
                new[] { "-", "=", "[", "]", "\\", ";", "'", "`" },
                new[] { 0xBD, 0xBB, 0xDB, 0xDD, 0xDC, 0xBA, 0xDE, 0xC0 }, null);

            var lastRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            lastRow.SetPadding(0, Dp(2), 0, 0);
            var space = NewKey(L10n.T("空格"));
            space.Click += (s, e) => { try { SendKeyWithModifiers(0x20); } catch { } };
            lastRow.AddView(space, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 3f));
            var enter = NewKey("Enter");
            enter.Click += (s, e) => { try { SendKeyWithModifiers(0x0D); } catch { } };
            lastRow.AddView(enter, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var clear = NewKey(L10n.T("清修饰"));
            clear.Click += (s, e) => { _kbShift = _kbCtrl = _kbAlt = false; RefreshSticky(); };
            lastRow.AddView(clear, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            panel.AddView(lastRow, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            return panel;
        }

        /// <summary>单个键帽：圆角 + 细边框，iOS 风格，日夜色取自页面常量。</summary>
        private TextView NewKey(string label)
        {
            var t = new TextView(this) { Text = label, TextSize = 12, Gravity = GravityFlags.Center };
            t.SetTextColor(new Color(Main));
            var gd = new Android.Graphics.Drawables.GradientDrawable();
            gd.SetColor(new Color(Card));
            gd.SetCornerRadius(Dp(5));
            gd.SetStroke(1, new Color(Line));
            t.Background = gd;
            t.SetPadding(Dp(2), Dp(8), Dp(2), Dp(8));
            return t;
        }

        private void AddKeyRow(LinearLayout parent, string[] labels, int[] vks, bool[] sticky)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(0, Dp(2), 0, 0);
            for (int i = 0; i < vks.Length; i++)
            {
                var key = NewKey(labels[i]);
                int vk = vks[i];
                bool isSticky = sticky != null && i < sticky.Length && sticky[i];
                key.Click += (s, e) =>
                {
                    try { if (isSticky) ToggleSticky(vk); else SendKeyWithModifiers(vk); }
                    catch { }
                };
                if (vk == 0x10) _kbShiftBtn = key;
                else if (vk == 0x11) _kbCtrlBtn = key;
                else if (vk == 0x12) _kbAltBtn = key;
                var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                lp.SetMargins(Dp(1), 0, Dp(1), 0);
                row.AddView(key, lp);
            }
            parent.AddView(row, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        private void ToggleSticky(int vk)
        {
            if (vk == 0x10) _kbShift = !_kbShift;
            else if (vk == 0x11) _kbCtrl = !_kbCtrl;
            else if (vk == 0x12) _kbAlt = !_kbAlt;
            RefreshSticky();
        }

        private void RefreshSticky()
        {
            ApplyStickyLook(_kbShiftBtn, _kbShift);
            ApplyStickyLook(_kbCtrlBtn, _kbCtrl);
            ApplyStickyLook(_kbAltBtn, _kbAlt);
        }

        private void ApplyStickyLook(TextView btn, bool on)
        {
            if (btn == null) return;
            btn.SetTextColor(new Color(on ? AccentText : Main));
            var gd = new Android.Graphics.Drawables.GradientDrawable();
            gd.SetColor(new Color(on ? Accent : Card));
            gd.SetCornerRadius(Dp(5));
            gd.SetStroke(1, new Color(on ? Accent : Line));
            btn.Background = gd;
        }

        /// <summary>按字符键时附带粘滞修饰键，按下顺序 Ctrl→Alt→Shift，抬起逆序。</summary>
        private void SendKeyWithModifiers(int vk)
        {
            RdSession s = session;
            if (s == null) return;
            if (_kbCtrl) s.Key(0x11, true);
            if (_kbAlt) s.Key(0x12, true);
            if (_kbShift) s.Key(0x10, true);
            s.KeyPress(vk);
            if (_kbShift) s.Key(0x10, false);
            if (_kbAlt) s.Key(0x12, false);
            if (_kbCtrl) s.Key(0x11, false);
        }

        /* ===================== 会话生命周期 ===================== */

        // 连接互斥：0=空闲 1=正在连。防止快速多点/自动重连时多个 ConnectAsync
        // 落在同一个会话实例上串流，握手读错帧被误报成"拒绝加密/中间人"。
        private int _connecting;

        private void StartSession(string ip, int port, string deviceCode, string password, bool direct = false)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                ToastIt(L10n.T("请填写 IP 地址"));
                return;
            }
            // 上一次连接还没建立起来就再次点击：直接忽略，不让两条握手并发
            if (System.Threading.Interlocked.CompareExchange(ref _connecting, 1, 0) != 0) return;

            StopSession();
            userStopped = false;
            lastIp = ip; lastPort = port; lastCode = deviceCode; lastPwd = password;
            lanDirect = direct;
            statusTxt.Text = L10n.T("正在连接 ") + ip + ":" + port + (direct ? L10n.T("（局域网直连）") : "") + " …";

            // ★ 关键：用局部快照，lambda / 回调一律捕获 s，绝不读 this.session 字段。
            // 以前 lambda 里写 session.ConnectAsync，执行时才解析字段；重连换新实例后
            // 两条异步流会用同一个 RdSession，交叉收发握手帧 → 误判加密被拒/中间人。
            var s = new RdSession();
            session = s;
            // 意外掉线自动重连：手机进电梯、切 Wi-Fi、电脑休眠恢复都会断，
            // 让用户重新输一遍设备码和密码是最招人烦的体验（rustdesk 也是自动重连）
            s.ConnectionLost = () => RunOnUiThread(() => TryReconnect());
            // 帧信箱：读线程只把"最新一帧"放进槽位并投递一次绘制；主线程忙时
            // 中间帧被覆盖丢弃（而不是排队），避免越积越多、画面越来越滞后。
            // 上一张 Bitmap 及时 Recycle，防止高帧率下内存抖动。
            s.FrameReady = bmp =>
            {
                Bitmap old;
                lock (_frameBox)
                {
                    old = _frameBox.Pending;
                    _frameBox.Pending = bmp;
                    if (_frameBox.Posting) return;   // 已排了一次绘制，覆盖即可
                    _frameBox.Posting = true;
                }
                try { if (old != null && old != bmp) old.Recycle(); } catch { }
                RunOnUiThread(DrawPendingFrame);
            };
            // H.264 与 JPEG 走两套显示控件：H.264 是 SurfaceView 直出，JPEG 是 ImageView。
            s.VideoSizeChanged += (vw, vh) => RunOnUiThread(() => ApplyVideoAspect(vw, vh));
            s.CodecChanged += isH264 => RunOnUiThread(() =>
            {
                try
                {
                    screen.Visibility = isH264 ? ViewStates.Gone : ViewStates.Visible;
                    surfaceView.Visibility = isH264 ? ViewStates.Visible : ViewStates.Gone;
                    if (isH264) WaitAndBindSurface();
                }
                catch { }
            });
            s.Status = msg => RunOnUiThread(() => ToastIt(msg));

            Task.Run(async () =>
            {
                string err = await s.ConnectAsync(ip, port, deviceCode, password, lanDirect);
                System.Threading.Interlocked.Exchange(ref _connecting, 0);
                RunOnUiThread(() =>
                {
                    if (err != null)
                    {
                        // 一键远控且明确"密码不正确"：弹一次密码框让用户填写，
                        // 而不是把人丢回满是 IP/端口的连接页。
                        if (autoMode && !_awaitingPassword && err.Contains(L10n.T("密码不正确")))
                        {
                            _awaitingPassword = true;
                            PromptPasswordOnly(_autoName, _autoIp, _autoPort, _autoCode);
                            return;
                        }
                        // 握手期内的"拒绝/超时/未响应"多为对方刚上线或端口抖动，
                        // 自动重试几次，用户不必反复点；只有确定性的失败才回连接页提示。
                        if (autoMode && !userStopped && autoTries < AutoMaxTries && IsRetryableRemoteError(err))
                        {
                            autoTries++;
                            statusTxt.Text = L10n.T("正在连接 ") + (string.IsNullOrEmpty(lastIp) ? "" : lastIp + " ")
                                           + L10n.T("（第 ") + (autoTries + 1) + L10n.T(" 次尝试）…");
                            new Handler(Looper.MainLooper).PostDelayed(() =>
                            {
                                if (!userStopped) StartSession(lastIp, lastPort, lastCode, lastPwd, lanDirect);
                            }, 700);
                            return;
                        }
                        statusTxt.Text = err;
                        ToastIt(err);
                        // 一键远控失败时把输入表单放回来，方便手动改端口/密码重试
                        if (manualCardRef != null) manualCardRef.Visibility = ViewStates.Visible;
                        ShowConnectPage();
                        return;
                    }
                    reconnectTries = 0;
                    autoTries = 0;
                    _awaitingPassword = false;
                    SavePassword(ip, port, password);
                    ApplyPreset();
                    RefreshToolbar();
                    statusTxt.Text = L10n.T("已连接") + (s.SessionSas != null ? L10n.T("（配对校验码 ") + s.SessionSas + "）" : "");
                    connectPage.Visibility = ViewStates.Gone;
                    sessionPage.Visibility = ViewStates.Visible;
                });
            });
        }

        /// <summary>掉线后在会话页后台重试（不弹回连接页，避免把用户正在看的东西跳掉）。</summary>
        private void TryReconnect()
        {
            if (userStopped) return;
            if (reconnectTries >= 8)
            {
                ToastIt(L10n.T("重连失败，已断开"));
                StopSession();
                ShowConnectPage();
                return;
            }
            reconnectTries++;
            ToastIt(L10n.T("连接中断，正在重连（第 ") + reconnectTries + L10n.T(" 次）…"));
            StartSession(lastIp, lastPort, lastCode, lastPwd, lanDirect);
        }

        /// <summary>
        /// 判断远控失败是否值得自动重试：握手/超时/未响应这类多为对方刚上线或端口抖动，重试常能成功；
        /// 密码/设备码错误、版本不符这类是确定性失败，重试无意义，应直接提示用户。
        /// </summary>
        private static bool IsRetryableRemoteError(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            // 确定性失败：重试也不会变好
            if (err.Contains(L10n.T("密码不正确")) || err.Contains(L10n.T("设备码不正确")) ||
                err.Contains(L10n.T("登录失败")) || err.Contains(L10n.T("版本较旧")) ||
                err.Contains(L10n.T("版本不符")) || err.Contains(L10n.T("请升级"))) return false;
            // 暂时性失败：对方刚开机、单连接占位未释放、网络抖动
            return err.Contains(L10n.T("超时")) || err.Contains(L10n.T("没有响应")) || err.Contains(L10n.T("未收到"))
                || err.Contains(L10n.T("拒绝")) || err.Contains(L10n.T("握手")) || err.Contains(L10n.T("未接受"))
                || err.Contains(L10n.T("校验失败")) || err.Contains(L10n.T("连接失败")) || err.Contains(L10n.T("重试"));
        }

        private void StopSession()
        {
            try { session?.Dispose(); } catch { }
            session = null;
            lastFrameW = 0;
            lastFrameH = 0;
            lock (_frameBox)
            {
                try { _frameBox.Pending?.Recycle(); } catch { }
                try { _frameBox.Shown?.Recycle(); } catch { }
                _frameBox.Pending = null;
                _frameBox.Shown = null;
                _frameBox.Posting = false;
            }
            try { screen?.SetImageBitmap(null); } catch { }
        }

        private void ShowConnectPage()
        {
            connectPage.Visibility = ViewStates.Visible;
            sessionPage.Visibility = ViewStates.Gone;
        }

        /* ===================== 界面小工具 ===================== */

        private LinearLayout Card_(LinearLayout parent, string title)
        {
            parent.AddView(SectionTitle(title));
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = IosStyle.Rounded(this, Card, IosStyle.CardRadius);
            parent.AddView(card, Margins(0, 0, 0, Dp(24)));
            return card;
        }

        private TextView SectionTitle(string t)
        {
            var tv = new TextView(this) { Text = t, TextSize = 12 };
            tv.SetTextColor(new Color(Sub));
            tv.SetPadding(0, Dp(16), 0, Dp(8));
            return tv;
        }

        private View Divider()
        {
            var v = new View(this);
            v.SetBackgroundColor(new Color(Line));
            v.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            return v;
        }

        // 供"从设备页带目标进来"时预填
        private EditText ipBoxRef, portBoxRef, codeBoxRef, pwBoxRef;

        private EditText Row_(LinearLayout parent, string label, string initial)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.PageInset), Dp(8), Dp(IosStyle.PageInset), Dp(8));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var lab = new TextView(this) { Text = label, TextSize = IosStyle.BodySize };
            lab.SetTextColor(new Color(IosStyle.Text));
            lab.LayoutParameters = new LinearLayout.LayoutParams(Dp(80), ViewGroup.LayoutParams.WrapContent);
            row.AddView(lab);

            var ed = new EditText(this) { Text = initial ?? "" };
            IosStyle.StyleInput(ed);
            ed.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            row.AddView(ed);

            parent.AddView(row);
            parent.AddView(Divider());
            return ed;
        }

        private Button PrimaryButton(string text)
        {
            var b = new Button(this) { Text = text };
            IosStyle.StylePrimaryButton(b);
            return b;
        }

        private Button ToolButton(string text, Action onClick)
        {
            var b = new Button(this) { Text = text, TextSize = IosStyle.CaptionSize };
            IosStyle.StyleSecondaryButton(b);
            // 7 个按钮同屏：压缩左右内边距，避免工具栏总宽超出屏幕导致「断开」被裁掉。
            b.SetPadding(Dp(3), 0, Dp(3), 0);
            b.SetMinWidth(0);
            b.Click += (s, e) => { try { onClick?.Invoke(); } catch { } };
            return b;
        }

        private LinearLayout.LayoutParams FillBtn()
        {
            var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lp.LeftMargin = Dp(2);
            lp.RightMargin = Dp(2);
            return lp;
        }

        private LinearLayout.LayoutParams Margins(int l, int t, int r, int b)
        {
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(l, t, r, b);
            return lp;
        }

        private void ToastIt(string msg)
        {
            try { L10n.Toast(this, msg, ToastLength.Short).Show(); } catch { }
        }

        private int Dp(int v) => IosStyle.Dp(this, v);

        /// <summary>
        /// SurfaceView 的生命周期回调：把可用的 Surface 交给会话，
        /// 供 H.264 硬解码器直接渲染（帧数据不经过应用内存）。
        /// </summary>
        private sealed class SurfaceHolderCallback : Java.Lang.Object, ISurfaceHolderCallback
        {
            private readonly RemoteDeskActivity _host;

            public SurfaceHolderCallback(RemoteDeskActivity host)
            {
                _host = host;
            }

            public void SurfaceCreated(ISurfaceHolder holder)
            {
                try { Android.Util.Log.Debug("Mchat", "[H264] Surface 已创建"); } catch { }
                Attach(holder);
            }

            public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
            {
                try { Android.Util.Log.Debug("Mchat", $"[H264] Surface 尺寸变化 {width}x{height}"); } catch { }
                Attach(holder);
            }

            public void SurfaceDestroyed(ISurfaceHolder holder)
            {
                RdSession s = _host.session;
                if (s != null) s.RenderSurface = null;
            }

            private void Attach(ISurfaceHolder holder)
            {
                try
                {
                    RdSession s = _host.session;
                    if (s != null && holder != null && holder.Surface != null && holder.Surface.IsValid)
                    {
                        s.RenderSurface = holder.Surface;
                    }
                }
                catch { }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media.Projection;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AhoChatA.Music;

namespace AhoChatA
{
    /// <summary>
    /// App 原生聊天界面（不再依赖网页）。
    ///
    /// 界面语言沿用 Mchat 网页端：同一套配色（#ededed 底 / #f7f7f7 顶栏 /
    /// #95ec69 自己的气泡 / 白色对方气泡 / #07c160 主色），同一套结构
    /// （顶栏 → 会话 → 消息 → 输入栏）。
    ///
    /// 聊天逻辑取自 bitchat：
    ///   · 房间（房间号，默认 mesh）与私聊（点谁跟谁单聊）分开；
    ///   · 消息删除：长按气泡可逐条删除，菜单可清空当前会话或全部消息；
    ///   · 删除是「墓碑 + 广播」，对端同步删除。
    /// </summary>
    [Activity(Label = "@string/app_name",
              Exported = true,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
              WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
              Theme = "@style/AppTheme.NoActionBar")]
    public class ChatActivity : BaseActivity
    {
        private MchatBridge bridge;
        private string pendingQaulConv;
        // 自动进入「一起看」的去重集合：每张视频分享卡片只自动触发一次。
        // （音乐「一起听」已改为纯手动加入，不再走这里的自动触发。）
        private readonly HashSet<string> _autoJoined = new HashSet<string>(StringComparer.Ordinal);

        private TextView txtTitle, txtSubtitle, btnSend;
        private FrameLayout btnPlus;
        private const int ReqShareStorage = 7312;
        private const int ReqPickSplash = 7313;   // 选择自定义开屏图片
        private LinearLayout convList, msgList;
        private ScrollView msgScroll;
        private EditText msgInput;

        /* ---------------- 新版界面（底部四个 Tab + 会话列表/聊天两态） ---------------- */
        private FrameLayout btnBack, btnMenu, btnTheme;
        private IconView iconTheme;
        private View pageMessages, pageChat, pageDevices, pageServices, pageMe;
        private LinearLayout devicesBox, servicesBox, meBox, bottomNav;

        // 设备页的自动刷新（见 StartDeviceWatch）
        private Handler _devTick;
        private string _devicesSig = "";
        // 四个 Tab 页按需构建：切页不再无条件 RemoveAllViews + 重建整页视图，
        // 这是真机上切页慢帧与视图数持续上涨的主因。数据变化由各自的刷新入口打脏标记。
        private bool _messagesBuilt, _devicesBuilt, _servicesBuilt, _meBuilt;
        private string _servicesSig = "", _meSig = "";
        // 顶栏「在线数 / 接入方式」每次切页都要，底层会枚举网卡与所有链路；
        // 0.8 秒短缓存即可，既跟得上设备上下线，又不会把切页拖成慢帧。
        private long _headerCacheAt;
        private int _headerOnline;
        private string _headerAccess;

        // 切换语言需要 Recreate 才能用新语言重新构建全部动态文案，但 Recreate 会走
        // OnDestroy → CoreHost.Shutdown()，把 Bridge / WebServer / 前台服务全部拆掉再
        // 在主线程重建（真机实测多花 30ms 构造 + 后台服务整轮重启，界面明显顿一下）。
        // 这个标志让「重建界面」与「退出 App」彻底分开：只在后者才真正 Shutdown。
        private static bool _recreateKeepCore;

        // Recreate 后保留用户原来的位置，避免切个语言就被踢回消息页。
        private const string StkTab = "aho.tab";
        private const string StkInChat = "aho.inChat";
        private const string StkConv = "aho.conv";
        // 画板设备（inkcanvas/painter）发现：UDP 21118，与「附近设备」并列展示
        private DrawboardDiscovery _drawDisc;
        private long _drawLastScan;
        private View pageHost;
        private View chatInputBar;
        private int _gestureInset;
        // 键盘避让：_statusTop 是根布局顶部 padding（边到边时才非 0），
        // _keyboardPad 是当前因软键盘而抬升的底部 padding（系统不缩窗时才会用上）。
        private int _statusTop;
        private int _keyboardPad;
        /// <summary>MIUI 手势导航下「布局比可见区域高」的那一截，启动时量一次，作为底部最小留白。</summary>
        private int _bottomOverflow;

        /// <summary>
        /// 根布局的顶/底内边距只有一个出口：状态栏高度 + max(键盘高度, 底部遮挡量)。
        /// 以前键盘监听和底部遮挡修正各自直接 SetPadding，后跑的会把先跑的覆盖掉，
        /// 表现就是「键盘弹起时输入栏有时抬起、有时又被按回去」。
        /// </summary>
        private void SyncRootPadding()
        {
            var root = FindViewById<View>(Resource.Id.chatRoot);
            if (root == null) return;
            int bottom = Math.Max(_keyboardPad, _bottomOverflow);
            if (root.PaddingTop == _statusTop && root.PaddingBottom == bottom) return;
            root.SetPadding(root.PaddingLeft, _statusTop, root.PaddingRight, bottom);
        }
        private ViewTreeObserver.IOnGlobalLayoutListener _kbListener;
        // 共享卡片：共享开启时向 mesh 频道广播一次；关闭后复位，下次再开重发一张
        private bool _camCardSent, _drawCardSent, _gpsCardSent;
        private LinearLayout navMsg, navDevices, navServices, navMe;
        private FrameLayout navMsgIcon, navDevicesIcon, navServicesIcon, navMeIcon;
        private TextView navMsgLabel, navDevicesLabel, navServicesLabel, navMeLabel;
        private IconView iconChat, iconRadar, iconGrid, iconPerson;
        private int currentTab = 0;          // 0 消息 1 设备 2 服务 3 我的
        private bool inChat;                 // 是否处于聊天页（隐藏底部 Tab）
        private static readonly int Accent = IosStyle.Accent;   // iOS systemBlue
        private static readonly int NavIdle = IosStyle.Secondary;
        private static readonly int TextMain = IosStyle.Text;
        private static readonly int TextSub = IosStyle.Secondary;
        private static readonly int CardBg = IosStyle.Surface;
        private static readonly int LineColor2 = IosStyle.Separator;  // iOS 柔和分隔线

        // Mchat 频道顶部的设备常驻状态条（电量 / IP / 设备名 / 定位请求）
        private LinearLayout devBar, devOthers;
        private TextView devSelfName, devSelfIp, devSelfBattery, devAskLoc;
        private bool devBarHidden;                 // 用户主动收起过
        private Handler devTimer;                  // 定时刷新电量
        private TextView otpCodeTxt, otpMetaTxt;   // 「设备」页本机的 6 位动态口令

        // 「我的」页顶部：IPv4 / IPv6 两行地址（点击复制；网络变动时定时刷新）
        private TextView _meIp4, _meIp6;
        private string _lastShownIp4, _lastShownIp6;
        private Handler _meIpTimer;
        private Java.Lang.Runnable _meIpRun;
        // 「我的」页 Web 服务状态卡片（运行状态实时探测 + 一键重启）
        private TextView _webState, _webDesc;
        private IconView _webIcon;
        private WebServer.ServiceHealth _webLastHealth = (WebServer.ServiceHealth)(-1);
        private int _webHealthTick;
        private bool _webRestarting;
        private TextView _devCardIp;   // 「设备」页本机卡上的 IP（随 OTP 秒针同步刷新）
        private Handler otpTimer;
        private bool otpTicking;

        private string currentConv = Convs.Room(Convs.DefaultRoom);
        private string editingMessageId; // 正在编辑的消息 id（非空表示编辑态）
        private string pendingFileConv; // 文档选择器返回后要发文件的会话
        private bool pendingPickMedia;  // 本次选择是否来自「图片/视频」入口（决定图片压缩策略）
        private static readonly int ReqPickFile = 7301;
        /// <summary>单个附件大小上限：超过则拒绝（mesh 整帧 + byte[] 常驻，太大必 OOM）。</summary>
        private const long MaxSendBytes = 512L * 1024 * 1024;
        private long lastRenderTime;
        private View emptyTip;
        private readonly Dictionary<string, View> bubbleViews = new Dictionary<string, View>(StringComparer.Ordinal);
        // 音乐卡片：切歌时原地刷新这张卡片，不走「重建整个列表」（否则会闪、会丢滚动位置）
        private readonly Dictionary<string, MusicCardView> musicCards = new Dictionary<string, MusicCardView>(StringComparer.Ordinal);

        /* ---------------- 底部音乐迷你播放条 ---------------- */
        private View musicMiniBar;
        private ImageView miniCover;
        private TextView miniTitle, miniLyric0, miniLyric1, miniLyric2;
        private ImageButton miniPrev, miniPlay, miniNext;
        private Lyrics _miniLyrics;
        private string _miniLyricKey = "";

        private int Dp(float v)
        {
            return IosStyle.Dp(this, v);
        }

        /// <summary>原生界面是否存活（网页端据此判断是否需要收尾）。</summary>
        public static bool IsAlive { get; private set; }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            Perf.Mark("OnCreate 进入");
            // 新实例起来，旧实例给出的「保留核心服务」授权到此为止；
            // 正常冷启动时它本来就是 false。
            _recreateKeepCore = false;
            RequestWindowFeature(WindowFeatures.NoTitle);
            base.OnCreate(savedInstanceState);

            // 窗口与系统栏的关系：**保持系统默认（fitsSystemWindows）**。
            //
            // 这里踩过的坑，写下来免得再犯：
            //   · 为了「底栏贴屏幕最底」加 FLAG_LAYOUT_NO_LIMITS → 窗口的 fit insets 被清空，
            //     adjustResize 彻底失效；
            //   · 改用 SetDecorFitsSystemWindows(false) 边到边 → 同样不缩窗，且本机 MIUI 上
            //     **根本不派发 IME insets**（日志里键盘高度恒为 0，dex2oat 还报了
            //     "Accessing hidden method WindowInsets$Type;->ime()I ... denied"）。
            //   两条路的结果都是「唤起输入法，输入框被键盘盖住」。
            // 现在回到最朴素、各 ROM 都支持的方式：窗口正常避让系统栏 + adjustResize，
            // 键盘弹出时系统自己把窗口缩短，输入栏天然落在键盘上方，一行避让代码都不用写。
            // 「贴底」的观感用导航栏配色补齐（见下面 SetNavigationBarColor）。
            try { Window.SetSoftInputMode(SoftInput.AdjustResize | SoftInput.StateHidden); } catch { }
            try { Window.SetStatusBarColor(Color.White); } catch { }
            try { Window.SetNavigationBarColor(new Color(unchecked((int)0xFFF7F7F7))); } catch { }

            SetContentView(Resource.Layout.activity_chat_native);
            Perf.Mark("SetContentView 完成");

            IsAlive = true;
            CoreHost.Ensure(this);
            Perf.Mark("CoreHost 完成");
            bridge = CoreHost.Bridge;
            currentConv = bridge.ActiveConv;

            // 画板设备发现器：常驻 UDP 监听，设备列表变化时刷新「设备」页
            _drawDisc = DrawboardDiscovery.Current;
            _drawDisc.Changed += OnDrawDiscChanged;
            _drawDisc.Start();

            txtTitle = FindViewById<TextView>(Resource.Id.txtTitle);
            txtSubtitle = FindViewById<TextView>(Resource.Id.txtSubtitle);
            btnMenu = FindViewById<FrameLayout>(Resource.Id.btnMenu);
            AddBoxIcon(btnMenu, IconKind.More, 24, unchecked((int)0xFF111111));
            // 主题切换按钮：紧挨菜单按钮（日间显示月亮、夜间显示太阳）
            btnTheme = FindViewById<FrameLayout>(Resource.Id.btnTheme);
            iconTheme = AddBoxIcon(btnTheme, IconKind.Moon, 22, unchecked((int)0xFF111111));
            btnPlus = FindViewById<FrameLayout>(Resource.Id.btnPlus);
            AddBoxIcon(btnPlus, IconKind.Plus, 24, TextMain);
            btnSend = FindViewById<TextView>(Resource.Id.btnSend);
            // 发送按钮改成主色胶囊，跟底部 Tab 的选中色统一
            btnSend.Background = RoundBg(Accent, IosStyle.ControlRadius);
            btnSend.SetTextColor(Color.White);
            convList = FindViewById<LinearLayout>(Resource.Id.convList);
            msgList = FindViewById<LinearLayout>(Resource.Id.msgList);
            msgScroll = FindViewById<ScrollView>(Resource.Id.msgScroll);
            msgInput = FindViewById<EditText>(Resource.Id.msgInput);

            // ===== 新版界面骨架 =====
            btnBack = FindViewById<FrameLayout>(Resource.Id.btnBack);
            AddBoxIcon(btnBack, IconKind.Back, 24, unchecked((int)0xFF111111));
            pageMessages = FindViewById(Resource.Id.pageMessages);
            pageChat = FindViewById(Resource.Id.pageChat);
            pageDevices = FindViewById(Resource.Id.pageDevices);
            pageServices = FindViewById(Resource.Id.pageServices);
            pageMe = FindViewById(Resource.Id.pageMe);
            devicesBox = FindViewById<LinearLayout>(Resource.Id.devicesBox);
            servicesBox = FindViewById<LinearLayout>(Resource.Id.servicesBox);
            meBox = FindViewById<LinearLayout>(Resource.Id.meBox);
            bottomNav = FindViewById<LinearLayout>(Resource.Id.bottomNav);
            pageHost = FindViewById(Resource.Id.pageHost);
            chatInputBar = FindViewById(Resource.Id.inputBar);
            navMsg = FindViewById<LinearLayout>(Resource.Id.navMsg);
            navDevices = FindViewById<LinearLayout>(Resource.Id.navDevices);
            navServices = FindViewById<LinearLayout>(Resource.Id.navServices);
            navMe = FindViewById<LinearLayout>(Resource.Id.navMe);
            navMsgIcon = FindViewById<FrameLayout>(Resource.Id.navMsgIcon);
            navMsgLabel = FindViewById<TextView>(Resource.Id.navMsgLabel);
            navDevicesIcon = FindViewById<FrameLayout>(Resource.Id.navDevicesIcon);
            navDevicesLabel = FindViewById<TextView>(Resource.Id.navDevicesLabel);
            navServicesIcon = FindViewById<FrameLayout>(Resource.Id.navServicesIcon);
            navServicesLabel = FindViewById<TextView>(Resource.Id.navServicesLabel);
            navMeIcon = FindViewById<FrameLayout>(Resource.Id.navMeIcon);
            navMeLabel = FindViewById<TextView>(Resource.Id.navMeLabel);

            iconChat = AddBoxIcon(navMsgIcon, IconKind.Chat, 26, TextMain);
            iconRadar = AddBoxIcon(navDevicesIcon, IconKind.Radar, 26, NavIdle);
            iconGrid = AddBoxIcon(navServicesIcon, IconKind.Grid, 26, NavIdle);
            iconPerson = AddBoxIcon(navMeIcon, IconKind.Person, 26, NavIdle);

            // 输入栏保留 56dp 最小高度，同时允许多行草稿与放大文字撑高。
            // 按钮自身保证 44dp 点击区域；不要把整个输入栏锁成固定高度。
            try
            {
                var inputBar = FindViewById(Resource.Id.inputBar);
                if (inputBar != null)
                {
                    int h = Dp(IosStyle.ToolbarHeight);
                    var lp = inputBar.LayoutParameters;
                    if (lp != null)
                    {
                        inputBar.SetMinimumHeight(h);
                        lp.Height = ViewGroup.LayoutParams.WrapContent;
                        inputBar.LayoutParameters = lp;
                    }
                }
            }
            catch { }

            // 更根本的兜底：布局高度按「整块屏幕」算，但窗口可见区域比它少一条导航栏
            // （MIUI 手势导航实测少 130px），于是最底下那条输入栏被裁掉一半——看得见、点不到。
            // 这里量一次「布局底边」与「可见区域底边」的差，把差值补成根布局的底部内边距。
            try { ApplyBottomInsetFix(); } catch { }
            // 底部 Tab 兜底：MIUI 手势导航 + fitsSystemWindows 会把 LinearLayout 的可用高度吃掉一截，
            // 非 weight 的 bottomNav 有概率被压到 20 多像素（图标和文字直接布局不出来，Tab 等于失效）。
            // 这里强制一个明确高度，保证四个 Tab 永远可点。
            try { ForceNavHeight(); } catch { }
            // 状态栏 / 底部手势区：手动按 WindowInsets 补 padding（不用 fitsSystemWindows，见布局注释）
            try { ApplySystemBarInsets(); } catch { }
            // 软键盘避让：聊天页唤起键盘时把输入栏顶到键盘上方，避免被遮挡
            try { SetupKeyboardAvoidance(); } catch { }

            // 频道顶部设备状态条
            devBar = FindViewById<LinearLayout>(Resource.Id.devBar);
            devOthers = FindViewById<LinearLayout>(Resource.Id.devOthers);
            devSelfName = FindViewById<TextView>(Resource.Id.devSelfName);
            devSelfIp = FindViewById<TextView>(Resource.Id.devSelfIp);
            // 顶部状态条里的本机 IP 也可点击复制（devTimer 会随网络变化刷新它）
            if (devSelfIp != null) devSelfIp.Click += (s, e) => CopyText(DeviceStatus.Ip);
            devSelfBattery = FindViewById<TextView>(Resource.Id.devSelfBattery);
            devAskLoc = FindViewById<TextView>(Resource.Id.devAskLoc);
            // 设备状态条常显：已去掉「点击状态条收起」和菜单里的隐藏开关，
            // 老版本收起来过的用户也强制恢复显示（否则会以为功能消失了）。
            devBarHidden = false;
            if (devAskLoc != null)
            {
                // 点一下：让频道内（或指定）设备连续发送 5 秒 GPS
                devAskLoc.Click += (s, e) => RequestPeerLocBurst(null);
                // 长按：选择只让某一台设备发定位
                devAskLoc.SetOnLongClickListener(new LongClickAction(() => { ShowLocTargetPicker(); return true; }));
            }

            btnSend.Click += (s, e) => SendCurrent();
            btnMenu.Click += (s, e) => ShowMainMenu();
            if (btnTheme != null) btnTheme.Click += (s, e) => ToggleThemeQuick();
            btnPlus.Click += (s, e) => ShowPlusPanel();

            if (btnBack != null) btnBack.Click += (s, e) => CloseChat();
            if (navMsg != null) navMsg.Click += (s, e) => ShowTab(0);
            if (navDevices != null) navDevices.Click += (s, e) => ShowTab(1);
            if (navServices != null) navServices.Click += (s, e) => ShowTab(2);
            if (navMe != null) navMe.Click += (s, e) => ShowTab(3);

            msgInput.EditorAction += (s, e) =>
            {
                if (e.ActionId == ImeAction.Send || e.ActionId == ImeAction.Done)
                {
                    SendCurrent();
                    e.Handled = true;
                }
            };

            bridge.NativeEvent += OnBridgeEvent;
            bridge.ConversationsChanged += OnConversationsChanged;
            bridge.DevicePresenceChanged += OnDevicePresenceChanged;

            // 音乐迷你播放条
            InitMiniBar();
            MusicPlayer.StateChanged += OnMiniState;
            MusicPlayer.PositionTick += OnMiniTick;
            MusicPlayer.SongChanged += OnMiniSong;

            EnsureRuntimePermissions();

            // 注意：这里不再手动 RefreshConversations/RefreshMessages/UpdateDevBar——
            // 下面的 ShowTab(0) 会做同样的事，以前是「先做一遍、ShowTab 再做一遍」，
            // 白烧 60~130ms 的主线程时间（冷启动时尤其明显）。
            StartDevTimer();

            // 打开 App 先看会话列表；如果是从通知带 conv 进来的，直接进聊天
            try { PrivacyActivity.ApplySecureFlag(this); } catch { }
            pendingOpenConv = Intent?.GetStringExtra("openConv");

            // 切语言导致的 Recreate：把用户放回原来的 Tab / 会话，而不是踢回消息页。
            // 同时跳过「首帧后预热其余三个 Tab」——那次预热是切换后紧接着的一阵卡顿来源，
            // 而且 OnResume 已经把三页标脏，用户真点过去时会按需构建，并不缺内容。
            bool restored = false;
            try
            {
                if (savedInstanceState != null)
                {
                    currentTab = savedInstanceState.GetInt(StkTab, currentTab);
                    inChat = savedInstanceState.GetBoolean(StkInChat, inChat);
                    string savedConv = savedInstanceState.GetString(StkConv);
                    if (!string.IsNullOrEmpty(savedConv)) currentConv = savedConv;
                    restored = true;
                }
            }
            catch { restored = false; }

            if (restored)
            {
                if (inChat && !string.IsNullOrEmpty(currentConv)) OpenChatConv(currentConv);
                else ShowTab(currentTab);
            }
            else
            {
                ShowTab(0);
                // 首帧之后再预热其余三个 Tab（仍走 UI 线程，但避开用户点击的那 16ms）。
                try
                {
                    bottomNav?.PostDelayed(new Java.Lang.Runnable(() =>
                    {
                        try
                        {
                            if (!_devicesBuilt) { BuildDevicesTab(); _devicesBuilt = true; }
                            if (!_servicesBuilt) { BuildServicesTab(); _servicesBuilt = true; _servicesSig = SafeIp() + "|" + WebServer.ExternalPortActual; }
                            if (!_meBuilt) { BuildMeTab(); _meBuilt = true; _meSig = (bridge?.SelfName ?? "") + "|" + SafeIp(); }
                        }
                        catch { }
                    }), 1500);
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(pendingOpenConv)) OpenChatConv(pendingOpenConv);
            Perf.Mark("OnCreate 收尾");
        }

        /* ===================== 页面切换（底部 Tab / 聊天） ===================== */

        private void ShowTab(int tab)
        {
            currentTab = tab;
            inChat = false;

            if (pageMessages != null) pageMessages.Visibility = tab == 0 ? ViewStates.Visible : ViewStates.Gone;
            if (pageDevices != null) pageDevices.Visibility = tab == 1 ? ViewStates.Visible : ViewStates.Gone;
            if (pageServices != null) pageServices.Visibility = tab == 2 ? ViewStates.Visible : ViewStates.Gone;
            if (pageMe != null) pageMe.Visibility = tab == 3 ? ViewStates.Visible : ViewStates.Gone;
            if (pageChat != null) pageChat.Visibility = ViewStates.Gone;
            if (chatInputBar != null) chatInputBar.Visibility = ViewStates.Gone;
            if (bottomNav != null) bottomNav.Visibility = ViewStates.Visible;
            if (btnBack != null) btnBack.Visibility = ViewStates.Gone;

            SetNavSelected(tab);
            HideKeyboard();

            switch (tab)
            {
                case 0:
                    StopDeviceWatch(); StopMeIpWatch();
                    // 会话列表由 ConversationsChanged 主动打脏，切回时通常无需重建
                    if (!_messagesBuilt) { RefreshConversations(); _messagesBuilt = true; }
                    break;
                case 1:
                    // 发现广播 / 画板 PING 都是网络 I/O，挪到后台线程；
                    // 设备列表本身由 2.5s 的 watch 按签名增量刷新，不在 UI 线程同步扫。
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { bridge?.RefreshDiscoveryNow(); } catch { }
                        try { _drawDisc?.ScanNow(); _drawLastScan = Java.Lang.JavaSystem.CurrentTimeMillis(); } catch { }
                    });
                    StopMeIpWatch();
                    if (!_devicesBuilt) { BuildDevicesTab(); _devicesBuilt = true; }
                    StartDeviceWatch();
                    break;
                case 2:
                    StopDeviceWatch(); StopMeIpWatch();
                    string sigS = SafeIp() + "|" + WebServer.ExternalPortActual;
                    if (!_servicesBuilt || sigS != _servicesSig)
                    { BuildServicesTab(); _servicesBuilt = true; _servicesSig = sigS; }
                    break;
                case 3:
                    StopDeviceWatch();
                    string sigM = (bridge?.SelfName ?? "") + "|" + SafeIp();
                    if (!_meBuilt || sigM != _meSig)
                    { BuildMeTab(); _meBuilt = true; _meSig = sigM; }
                    break;
            }
            UpdateHeader();
            ReapplyTheme();   // 切 tab 会重建内容，补一次主题重刷（夜间）
        }

        private void SetNavSelected(int tab)
        {
            StyleNav(iconChat, navMsgLabel, tab == 0);
            StyleNav(iconRadar, navDevicesLabel, tab == 1);
            StyleNav(iconGrid, navServicesLabel, tab == 2);
            StyleNav(iconPerson, navMeLabel, tab == 3);
        }

        private static void StyleNav(IconView icon, TextView label, bool on)
        {
            if (label != null)
            {
                label.SetTextColor(new Android.Graphics.Color(on ? Accent : NavIdle));
                label.SetTypeface(null, on ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            }
            if (icon != null)
            {
                icon.IconColor = on ? Accent : NavIdle;
                icon.Emphasized = on;
                icon.Invalidate();
            }
        }

        /// <summary>往一个方形容器里居中放一枚矢量图标，返回它方便后续改色。</summary>
        private IconView AddBoxIcon(FrameLayout box, IconKind kind, int sizeDp, int color)
        {
            if (box == null) return null;
            box.RemoveAllViews();
            var ic = new IconView(this, kind) { IconColor = color };
            var lp = new FrameLayout.LayoutParams(Dp(sizeDp), Dp(sizeDp));
            lp.Gravity = GravityFlags.Center;
            box.AddView(ic, lp);
            return ic;
        }

        /// <summary>进入某个会话的聊天页。</summary>
        private void OpenChatConv(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return;
            inChat = true;

            if (conv != currentConv)
            {
                currentConv = conv;
                editingMessageId = null; // 切换会话退出编辑态
                try { bridge.SetActiveConv(conv); } catch { }
                try { bridge.EnsureChannelLinksActive(conv); } catch { }
                if (Convs.IsChan(conv)) { try { bridge.BroadcastDeviceStatus(conv); } catch { } }
            }

            if (pageMessages != null) pageMessages.Visibility = ViewStates.Gone;
            if (pageDevices != null) pageDevices.Visibility = ViewStates.Gone;
            if (pageServices != null) pageServices.Visibility = ViewStates.Gone;
            if (pageMe != null) pageMe.Visibility = ViewStates.Gone;
            if (pageChat != null) pageChat.Visibility = ViewStates.Visible;
            if (chatInputBar != null) chatInputBar.Visibility = ViewStates.Visible;
            if (bottomNav != null) bottomNav.Visibility = ViewStates.Gone;
            if (btnBack != null) btnBack.Visibility = ViewStates.Visible;

            try { PrivacyActivity.ApplySecureFlag(this); } catch { }

            RefreshMessages(true);
            UpdateHeader();
            UpdateDevBar();
        }

        private void CloseChat()
        {
            inChat = false;
            HideKeyboard();
            // 聊天期间 OnConversationsChanged 有意跳过列表刷新（避免每收一条消息
            // 都重建会话行），而 _messagesBuilt 仍为 true，ShowTab(0) 也就不再刷。
            // 于是发出去的消息在列表里仍旧显示「还没有消息」——返回时补刷一次。
            try { RefreshConversations(); } catch { }
            ShowTab(currentTab);
        }

        private void HideKeyboard()
        {
            try
            {
                var imm = (Android.Views.InputMethods.InputMethodManager)
                    GetSystemService(InputMethodService);
                if (imm != null && msgInput != null) imm.HideSoftInputFromWindow(msgInput.WindowToken, 0);
                if (msgInput != null) msgInput.ClearFocus();
            }
            catch { }
        }

        /// <summary>外部（通知点击等）指定要打开的会话，等界面就绪后再进。</summary>
        private string pendingOpenConv;

        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            var conv = intent?.GetStringExtra("openConv");
            if (string.IsNullOrEmpty(conv)) return;
            pendingOpenConv = conv;
            OpenChatConv(conv);
        }

        /// <summary>设备状态条需要定位（自动上报）与短信转发权限。</summary>
        private void EnsureRuntimePermissions()
        {
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.M) return;
                var need = new List<string>();
                if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Android.Content.PM.Permission.Granted)
                    need.Add(Android.Manifest.Permission.AccessFineLocation);
                if (CheckSelfPermission("android.permission.RECEIVE_SMS") != Android.Content.PM.Permission.Granted)
                    need.Add("android.permission.RECEIVE_SMS");
                // READ_SMS：部分 ROM 会拦 SMS_RECEIVED 广播，用收件箱监听兜底时需要它
                if (CheckSelfPermission("android.permission.READ_SMS") != Android.Content.PM.Permission.Granted)
                    need.Add("android.permission.READ_SMS");
                // Android 13+ 通知权限：不授权就弹不出消息通知
                if ((int)Build.VERSION.SdkInt >= 33 &&
                    CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Android.Content.PM.Permission.Granted)
                    need.Add("android.permission.POST_NOTIFICATIONS");
                if (need.Count > 0) RequestPermissions(need.ToArray(), 7302);
            }
            catch { }
        }

        private void StartDevTimer()
        {
            try
            {
                devTimer = new Handler(Looper.MainLooper);
                devTimer.PostDelayed(new Java.Lang.Runnable(() =>
                {
                    UpdateDevBar();
                    // 每 2 分钟顺带把本机电量广播给频道（对方状态条能实时看到）
                    try { bridge?.BroadcastDeviceStatus(currentConv); } catch { }
                    if (devTimer != null) devTimer.PostDelayed(new Java.Lang.Runnable(TickDev), 10000);
                }), 30000);
            }
            catch { }
        }

        private void TickDev()
        {
            UpdateDevBar();
            try { bridge?.BroadcastDeviceStatus(currentConv); } catch { }
            if (devTimer != null) devTimer.PostDelayed(new Java.Lang.Runnable(TickDev), 10000);
        }

        private void OnDevicePresenceChanged()
        {
            RunOnUiThread(() =>
            {
                UpdateDevBar();
                if (!inChat && currentTab == 1) { try { BuildDevicesTab(); } catch { } ReapplyTheme(); }
                if (!inChat) UpdateHeader();
            });
        }

        /// <summary>
        /// 刷新频道顶部的设备常驻状态条：
        /// 本机（设备名 · IP · 电量图标+数字）+ 频道内其它设备。
        /// 只在 Mchat 频道（chan:）里出现，而且随时可以点一下收起。
        /// </summary>
        private void UpdateDevBar()
        {
            if (devBar == null) return;
            // 设备状态条常显（已去掉「隐藏」功能）
            if (!Convs.IsChan(currentConv))
            {
                devBar.Visibility = ViewStates.Gone;
                return;
            }
            devBar.Visibility = ViewStates.Visible;

            if (devSelfName != null) devSelfName.Text = DeviceStatus.DeviceName;
            if (devSelfIp != null) devSelfIp.Text = DeviceStatus.Ip;
            if (devSelfBattery != null)
            {
                devSelfBattery.Text = DeviceStatus.BatteryText;
                try { Icons.Leading(devSelfBattery, DeviceStatus.Charging ? IconKind.Bolt : IconKind.Battery, TextSub, 13f, 4f); } catch { }
            }

            // 其它设备：每个一行，形如 "Pixel 7 · 192.168.1.9 · 🔋56%"（过期 3 分钟的不显示）
            if (devOthers != null)
            {
                devOthers.RemoveAllViews();
                List<Dictionary<string, object>> list;
                try { list = bridge == null ? null : bridge.GetDevicePresence(); } catch { list = null; }
                if (list != null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    int shown = 0;
                    foreach (var d in list)
                    {
                        if (shown >= 4) break;
                        if (now - Msg.Num(d, "at", 0) > 180000) continue;
                        var name = Msg.Str(d, "dev");
                        if (string.IsNullOrEmpty(name)) name = Msg.Str(d, "name", L10n.T("设备"));
                        var ip = Msg.Str(d, "ip", "?");
                        var peer = Msg.Str(d, "peer");
                        long bat = Msg.Num(d, "battery", -1);
                        var batText = bat < 0 ? L10n.T("电量 --") : bat + "%" + (Msg.Bool(d, "charging") ? L10n.T(" 充电") : "");
                        var link = Msg.Str(d, "links");
                        if (string.IsNullOrEmpty(link)) link = Msg.Str(d, "link");

                        // 一行：名称·IP·电量 + 连接方式图标（可多个）；点击提示是否私聊
                        var row = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
                        row.SetGravity(GravityFlags.Center);

                        var tv = new TextView(this);
                        tv.Text = name + " · " + ip + " · " + batText;
                        tv.TextSize = 10;
                        tv.SetTextColor(new Android.Graphics.Color(unchecked((int)0xFF9AA0A6)));
                        row.AddView(tv);

                        // 电量后面跟连接方式图标。同一台设备可能同时经「蓝牙 + 局域网 /
                        // WLAN Direct」接入，每个已连链路各画一个图标，一眼看出怎么连上的。
                        var kinds = LinkIconsOf(link, ip);
                        for (int k = 0; k < kinds.Count && k < 4; k++)
                        {
                            var ic = new IconView(this, kinds[k]);
                            ic.IconColor = unchecked((int)0xFF9AA0A6);
                            var lp = new LinearLayout.LayoutParams(Dp(12), Dp(12));
                            lp.LeftMargin = Dp(3);
                            row.AddView(ic, lp);
                        }

                        var capName = name; var capPeer = peer;
                        row.SetMinimumHeight(Dp(IosStyle.ControlHeight));
                        row.Click += (s, e) => PromptPrivateChat(capPeer, capName);
                        devOthers.AddView(row);
                        shown++;
                    }
                }
            }
        }

        /// <summary>设备行的连接方式图标：蓝牙 / WLAN Direct / 网络（WiFi 或 qaul）。</summary>
        private IconKind LinkIconOf(string link, string ip)
        {
            if (link == ChanLink.Bitchat) return IconKind.Bluetooth;
            if (link == ChanLink.P2p) return IconKind.P2P;
            if (!string.IsNullOrEmpty(ip) &&
                (ip.StartsWith("192.168.49.") || ip.StartsWith("192.168.48."))) return IconKind.P2P;
            return IconKind.Wifi;
        }

        /// <summary>
        /// 一台设备的全部连接方式图标（按 links 里的链路顺序去重）。
        /// 顺序固定为：WLAN Direct → 局域网 → 蓝牙 → qaul，避免同一个组合每次刷新图标乱跳。
        /// </summary>
        private List<IconKind> LinkIconsOf(string links, string ip)
        {
            var kinds = new List<IconKind>();
            bool p2p = false, lan = false, bt = false, qaul = false;

            if (!string.IsNullOrEmpty(links))
            {
                foreach (var raw in links.Split(','))
                {
                    var l = (raw ?? "").Trim();
                    if (l.Length == 0) continue;
                    if (l == ChanLink.P2p) p2p = true;
                    else if (l == ChanLink.Lan) lan = true;
                    else if (l == ChanLink.Bitchat) bt = true;
                    else if (l == ChanLink.Qaul) qaul = true;
                    else lan = true;   // 未知链路按L10n.T("有网")处理
                }
            }
            else
            {
                // 老数据没有 links 字段：退回按 IP 猜
                var one = LinkIconOf(null, ip);
                if (one == IconKind.P2P) p2p = true; else lan = true;
            }

            if (p2p) kinds.Add(IconKind.P2P);
            if (lan || qaul) kinds.Add(IconKind.Wifi);
            if (bt) kinds.Add(IconKind.Bluetooth);
            if (kinds.Count == 0) kinds.Add(IconKind.Wifi);
            return kinds;
        }

        /// <summary>点击设备：提示是否与它私聊。</summary>
        private void PromptPrivateChat(string peer, string name)
        {
            if (string.IsNullOrEmpty(peer)) return;
            var shown = string.IsNullOrEmpty(name) ? peer : name;
            L10n.Dialog(this)
                .SetTitle(shown)
                .SetMessage(L10n.T("是否与「") + shown + L10n.T("」私聊？"))
                .SetPositiveButton(L10n.T("私聊"), (s, e) =>
                {
                    try
                    {
                        var conv = bridge?.OpenLcDeviceDm(peer, name);
                        if (!string.IsNullOrEmpty(conv)) OpenChatConv(conv);
                    }
                    catch { }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /* ===================== 远程定位（点一次，对端连续发 5 秒 GPS） ===================== */

        /// <summary>连续上报时长：点一次让对端连续发 5 秒定位（1 秒一个点，约 5 条）。</summary>
        private const int LocBurstMs = 5000;

        /// <summary>向频道内设备（或指定设备）发起定位请求。</summary>
        private void RequestPeerLocBurst(string targetPeer)
        {
            try
            {
                if (bridge == null) return;
                // 定位请求默认发到 mesh 频道（= 局域网内各设备），用于「定位其它设备」；
                // 若当前正停留在某个频道会话里，就只请求该频道内的设备。
                var conv = Convs.IsChan(currentConv)
                    ? currentConv
                    : Convs.Chan(ChannelRegistry.DefaultChannelName);
                bool ok = bridge.RequestPeerLocation(conv, targetPeer, LocBurstMs);
                if (!ok)
                {
                    L10n.Toast(this, L10n.T("定位请求只能发到 AhoChat 频道"), ToastLength.Short).Show();
                    return;
                }
                var who = string.IsNullOrEmpty(targetPeer) ? L10n.T("频道内设备") : L10n.T("指定设备");
                L10n.Toast(this, L10n.T("已请求") + who + L10n.T("定位，等待回传…"), ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("请求定位失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>长按定位按钮：选择只让某一台设备发定位（不选则全频道），或在本机自测。</summary>
        private void ShowLocTargetPicker()
        {
            // 只请求「其它设备」发定位；本机不再把自己的位置发出去
            var names = new List<string> { L10n.T("全部设备（频道内都发）") };
            var ids = new List<string> { "" };
            try
            {
                var list = bridge?.GetDevicePresence();
                if (list != null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var d in list)
                    {
                        if (now - Msg.Num(d, "at", 0) > 180000) continue;   // 3 分钟没心跳的跳过
                        var peer = Msg.Str(d, "peer");
                        if (string.IsNullOrEmpty(peer)) continue;
                        var dev = Msg.Str(d, "dev");
                        if (string.IsNullOrEmpty(dev)) dev = Msg.Str(d, "name", L10n.T("设备"));
                        names.Add(dev);
                        ids.Add(peer);
                    }
                }
            }
            catch { }

            L10n.Dialog(this)
                .SetTitle(L10n.T("让哪台设备发送 ") + (LocBurstMs / 1000) + L10n.T(" 秒定位？"))
                .SetItems(names.ToArray(), (s, e) =>
                {
                    if (e.Which < 0 || e.Which >= ids.Count) return;
                    var id = ids[e.Which];
                    RequestPeerLocBurst(string.IsNullOrEmpty(id) ? null : id);
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private bool GetBoolPref(string key, bool def)
        {
            try
            {
                var p = GetSharedPreferences("Mchat", FileCreationMode.Private);
                return p.GetBoolean(key, def);
            }
            catch { return def; }
        }

        private void SetBoolPref(string key, bool value)
        {
            try
            {
                GetSharedPreferences("Mchat", FileCreationMode.Private).Edit().PutBoolean(key, value).Apply();
            }
            catch { }
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (bridge == null) return;
            try { bridge.SetActiveConv(currentConv); } catch { }
            // 回到前台：退出省电降频（发现/信标立刻恢复常频并补发一帧）
            try { bridge.SetAppForeground(true); } catch { }
            RefreshConversations();
            _messagesBuilt = true;
            // 从设置页返回后，渠道/昵称/服务地址可能变了：下一切页重建对应页面
            _devicesBuilt = false; _servicesBuilt = false; _meBuilt = false;
            UpdateHeader();

            // 用户可能在系统设置里刚给了短信权限：回到前台再尝试挂上收件箱监听
            try { SmsInboxWatcher.EnsureStarted(this); } catch { }

            // 本机的共享（画笔 / 摄像头 / 屏幕）一旦开着，就往 mesh 频道发一张共享卡片，
            // 局域网里其它设备点卡片即可在本 App 内全屏观看。
            try { MaybeBroadcastShareCard(); } catch { }

            // 2FA 开关可能刚在「隐私与安全」里被拨动，回前台时重建设备页让口令立刻出现/消失
            try { if (!inChat && currentTab == 1) BuildDevicesTab(); } catch { }
            ReapplyTheme();
            UpdateThemeIcon();   // 可能刚在设置页改过主题，回来后同步顶栏图标
        }

        protected override void OnPause()
        {
            // 进后台：所有周期性轮询降频，别在看不见的时候继续烧电
            try { bridge?.SetAppForeground(false); } catch { }
            otpTicking = false;   // 口令秒级刷新没必要在后台跑，回到设备页会重启
            base.OnPause();
        }

        /// <summary>语言切换会 Recreate，这里记住用户正在看的页面，恢复时原样回去。</summary>
        protected override void OnSaveInstanceState(Bundle outState)
        {
            try
            {
                outState.PutInt(StkTab, currentTab);
                outState.PutBoolean(StkInChat, inChat);
                outState.PutString(StkConv, currentConv);
            }
            catch { }
            base.OnSaveInstanceState(outState);
        }

        protected override void OnDestroy()
        {
            IsAlive = false;
            try
            {
                if (_kbListener != null)
                {
                    FindViewById<View>(Resource.Id.chatRoot)?.ViewTreeObserver
                        ?.RemoveOnGlobalLayoutListener(_kbListener);
                    _kbListener = null;
                }
            }
            catch { }
            try
            {
                if (bridge != null)
                {
                    bridge.NativeEvent -= OnBridgeEvent;
                    bridge.ConversationsChanged -= OnConversationsChanged;
                    bridge.DevicePresenceChanged -= OnDevicePresenceChanged;
                }
                try
                {
                    MusicPlayer.StateChanged -= OnMiniState;
                    MusicPlayer.PositionTick -= OnMiniTick;
                    MusicPlayer.SongChanged -= OnMiniSong;
                }
                catch { }
                if (_drawDisc != null)
                {
                    try { _drawDisc.Changed -= OnDrawDiscChanged; } catch { }
                }
                if (devTimer != null) { devTimer.RemoveCallbacksAndMessages(null); devTimer = null; }
                StopMeIpWatch();
                if (_meIpTimer != null) { _meIpTimer.RemoveCallbacksAndMessages(null); _meIpTimer = null; }
            }
            catch { }

            // 只有「真正退出」才拆核心服务。切语言的 Recreate 只是换一套视图，
            // 若在这里 Shutdown，新界面就得在主线程重建 Bridge 并重启 WebServer、
            // 前台服务、FTP、Bridge.Start——真机实测这一整轮重启就是切换后那阵卡顿，
            // 还会打断正在进行的传输与播放。
            bool keepCore = _recreateKeepCore || IsChangingConfigurations || FloatingWindowService.IsRunning;
            try
            {
                if (!keepCore)
                {
                    try { ChatForegroundService.Stop(this); } catch { }
                    try { LanShareService.Stop(this); } catch { }
                    try { CoreHost.Shutdown(); } catch { }
                }
            }
            finally { _recreateKeepCore = false; }
            base.OnDestroy();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == 5173)
            {
                if (resultCode == Result.Ok)
                {
                    try { DnsCore.DnsManager.Instance.StartVpn(); }
                    catch (Exception ex) { L10n.Toast(this, ex.Message, ToastLength.Long).Show(); }
                }
                else L10n.Toast(this, L10n.T("未授权 DNS VPN"), ToastLength.Short).Show();
                return;
            }

            // 「投屏到电脑」的屏幕录制授权回来了
            if (requestCode == ReqCastProjection)
            {
                if (resultCode != Result.Ok || data == null)
                {
                    L10n.Toast(this, L10n.T("未授权屏幕录制，无法投屏"), ToastLength.Short).Show();
                    _castTargetIp = null;
                    return;
                }
                StartScreenCastToPc((int)resultCode, data);
                return;
            }

            // 选择自定义开屏图片：读成字节存到应用私有目录
            if (requestCode == ReqPickSplash)
            {
                if (resultCode != Result.Ok || data == null || data.Data == null) return;
                var uri = data.Data;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        byte[] bytes;
                        using (var st = ContentResolver.OpenInputStream(uri))
                        using (var ms = new System.IO.MemoryStream())
                        {
                            if (st == null)
                            {
                                RunOnUiThread(() => L10n.Toast(this, L10n.T("无法读取图片"), ToastLength.Short).Show());
                                return;
                            }
                            st.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                        bool ok = SplashSettings.SaveCustomImage(bytes);
                        RunOnUiThread(() =>
                        {
                            L10n.Toast(this, ok ? L10n.T("已设置为自定义开屏图片") : L10n.T("设置失败"), ToastLength.Short).Show();
                            if (ok)
                            {
                                SplashSettings.SetEnabled(true);
                                BuildMeTab();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUiThread(() => L10n.Toast(this, L10n.T("设置失败：") + ex.Message, ToastLength.Short).Show());
                    }
                });
                return;
            }

            if (requestCode != ReqPickFile) return;
            var conv = pendingFileConv;
            bool pickMedia = pendingPickMedia;
            pendingFileConv = null;
            pendingPickMedia = false;
            if (resultCode != Result.Ok || data == null) return;
            if (string.IsNullOrEmpty(conv)) return;

            // 收集待发 URI：多选走 ClipData，单选走 data.Data
            var uris = new List<Android.Net.Uri>();
            if (data.ClipData != null)
            {
                var clip = data.ClipData;
                for (int i = 0; i < clip.ItemCount; i++)
                {
                    var u = clip.GetItemAt(i)?.Uri;
                    if (u != null) uris.Add(u);
                }
            }
            else if (data.Data != null) uris.Add(data.Data);
            if (uris.Count == 0) return;

            // 逐个查元数据（显示名 + 体积），超大文件直接拒（防 OOM）
            var items = new List<Tuple<Android.Net.Uri, string, long>>();
            foreach (var u in uris)
            {
                if (u == null) continue;
                string nm = "file";
                long sz = -1;
                try
                {
                    using (var cursor = ContentResolver.Query(u, null, null, null, null))
                    {
                        if (cursor != null && cursor.MoveToFirst())
                        {
                            int nameIdx = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                            if (nameIdx >= 0)
                            {
                                var s = cursor.GetString(nameIdx);
                                if (!string.IsNullOrEmpty(s)) nm = s;
                            }
                            int sizeIdx = cursor.GetColumnIndex(Android.Provider.OpenableColumns.Size);
                            if (sizeIdx >= 0 && !cursor.IsNull(sizeIdx)) sz = cursor.GetLong(sizeIdx);
                        }
                        cursor?.Close();
                    }
                }
                catch { }

                if (sz > MaxSendBytes)
                {
                    L10n.Toast(this, "「" + nm + "」" + MediaCompress.SizeText(sz)
                        + L10n.T(" 超过 ") + MediaCompress.SizeText(MaxSendBytes) + L10n.T(" 上限，已跳过"), ToastLength.Long).Show();
                    continue;
                }
                items.Add(Tuple.Create(u, nm, sz));
            }
            if (items.Count == 0) return;

            SendPickedFiles(conv, pickMedia, items);
        }

        /// <summary>
        /// 发送已选文件（带进度对话框）。图片先过 MediaCompress 压缩，
        /// 视频/文件原样；逐个发送并实时刷新 x/y 进度，结束汇总成败。
        /// </summary>
        private void SendPickedFiles(string conv, bool pickMedia, List<Tuple<Android.Net.Uri, string, long>> items)
        {
            if (items == null || items.Count == 0) return;

            var dlg = new Dialog(this);
            dlg.RequestWindowFeature((int)WindowFeatures.NoTitle);

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(22), Dp(20), Dp(22), Dp(20));

            var title = new TextView(this) { Text = L10n.T("正在发送…"), TextSize = 15 };
            title.SetTextColor(new Color(TextMain));
            box.AddView(title);

            var bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal);
            var barLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(6));
            barLp.TopMargin = Dp(12);
            bar.Max = items.Count;
            bar.Progress = 0;
            box.AddView(bar, barLp);

            var info = new TextView(this) { Text = "0 / " + items.Count, TextSize = 12 };
            info.SetTextColor(new Color(TextSub));
            var infoLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            infoLp.TopMargin = Dp(8);
            box.AddView(info, infoLp);

            dlg.SetContentView(box);
            var win = dlg.Window;
            if (win != null)
            {
                win.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Transparent));
                win.SetDimAmount(0.4f);
                win.AddFlags(WindowManagerFlags.DimBehind);
            }
            dlg.SetCancelable(false);
            dlg.SetCanceledOnTouchOutside(false);
            dlg.Show();

            System.Threading.Tasks.Task.Run(() =>
            {
                int done = 0, fail = 0;
                string lastNote = "";
                foreach (var it in items)
                {
                    byte[] bytes = null;
                    string note = "";
                    try
                    {
                        using (var st = ContentResolver.OpenInputStream(it.Item1))
                        {
                            if (st != null)
                            {
                                // 预分配容量：省掉 MemoryStream 反复扩容的翻倍复制
                                long hint = it.Item3;
                                using (var ms = new System.IO.MemoryStream(
                                    hint > 0 && hint < int.MaxValue ? (int)hint : 0))
                                {
                                    st.CopyTo(ms);
                                    bytes = ms.ToArray();
                                }
                            }
                        }
                        if (bytes != null && bytes.Length > 0)
                        {
                            // 图片发送前压缩（视频/文件不动；失败时原图照发）
                            if (pickMedia && MediaCompress.IsImageName(it.Item2))
                                bytes = MediaCompress.CompressImage(it.Item2, bytes, out note);
                            if (!bridge.SendFileBytes(conv, it.Item2, bytes, null)) fail++;
                        }
                        else fail++;
                    }
                    catch { fail++; }
                    lastNote = note;
                    done++;
                    int d = done, f = fail;
                    RunOnUiThread(() =>
                    {
                        try { bar.Progress = d; info.Text = d + " / " + items.Count + (f > 0 ? L10n.T("（失败 ") + f + "）" : ""); }
                        catch { }
                    });
                }
                int dDone = done, dFail = fail, dTotal = items.Count;
                string dNote = lastNote;
                RunOnUiThread(() =>
                {
                    try { dlg.Dismiss(); } catch { }
                    string summary = dFail == 0 ? "已发送 " + dDone + " 个文件"
                        : L10n.T("发送完成：成功 ") + (dDone - dFail) + "/" + dTotal + L10n.T("，失败 ") + dFail;
                    if (!string.IsNullOrEmpty(dNote)) summary += " · " + dNote;
                    L10n.Toast(this, summary, ToastLength.Long).Show();
                });
            });
        }

        /* ===================== 投屏到电脑 ===================== */

        private const int ReqCastProjection = 9231;
        private string _castTargetIp;
        private string _castTargetName;

        /// <summary>
        /// 设备卡上的「投屏」：向系统申请屏幕录制 → 开屏幕共享 → 告诉那台电脑来拉流。
        ///
        /// 安卓各家 ROM 的"系统投屏"协议不统一（Miracast / Cast / 厂商私有），
        /// 做接收端无法保证通用；而 MediaProjection 是系统公开接口、每台安卓都支持，
        /// 所以这里用同一条系统录屏通路把画面送过去 —— 对电脑来说一样是"手机投屏过来"。
        /// </summary>
        private void StartCastToPc(string ip, string name)
        {
            _castTargetIp = ip;
            _castTargetName = name;
            try
            {
                var mgr = (MediaProjectionManager)GetSystemService(Context.MediaProjectionService);
                if (mgr == null)
                {
                    L10n.Toast(this, L10n.T("系统不支持屏幕录制"), ToastLength.Long).Show();
                    return;
                }
                StartActivityForResult(mgr.CreateScreenCaptureIntent(), ReqCastProjection);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("申请屏幕录制失败：") + ex.Message, ToastLength.Long).Show();
            }
        }

        private void StartScreenCastToPc(int rc, Intent data)
        {
            string ip = _castTargetIp;
            string name = _castTargetName;
            if (string.IsNullOrEmpty(ip)) return;

            int shortSide = 1080;          // 电脑屏幕大，给足清晰度
            int fps = 30;
            int br = 8_000_000;
            try
            {
                var i = new Intent(this, typeof(ScreenCastService));
                i.SetAction(ScreenCastService.ActionStart);
                i.PutExtra(ScreenCastService.ExtraResultCode, rc);
                i.PutExtra(ScreenCastService.ExtraData, data);
                i.PutExtra(ScreenCastService.ExtraShort, shortSide);
                i.PutExtra(ScreenCastService.ExtraFps, fps);
                i.PutExtra(ScreenCastService.ExtraBitrate, br);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(i);
                else StartService(i);
                L10n.Toast(this, L10n.T("正在投屏到 ") + (name ?? ip) + "…", ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("启动投屏失败：") + ex.Message, ToastLength.Long).Show();
                return;
            }

            // 等服务把 HTTP 端口开出来再通知电脑（端口要等绑定完才知道）
            new Thread(() =>
            {
                try
                {
                    int port = 0;
                    for (int k = 0; k < 30; k++)
                    {
                        Thread.Sleep(300);
                        try { port = IpCameraServer.Instance.UsedHttpPort; } catch { }
                        if (port > 0) break;
                    }
                    if (port <= 0)
                    {
                        RunOnUiThread(() => L10n.Toast(this, L10n.T("投屏服务未就绪"), ToastLength.Short).Show());
                        return;
                    }
                    string myIp = "";
                    try { myIp = DeviceStatus.Ip ?? ""; } catch { }
                    if (string.IsNullOrEmpty(myIp)) return;
                    string cmd = "CAST|" + myIp + "|" + port + "|" + DeviceStatus.DeviceName;
                    bool ok = bridge != null && bridge.SendControlTo(ip, cmd);
                    RunOnUiThread(() => L10n.Toast(this,
                        ok ? L10n.T("已请求投屏到 ") + (name ?? ip) + L10n.T("，请在电脑上确认")
                           : L10n.T("没找到 ") + (name ?? ip) + L10n.T("，请确认电脑端在线"),
                        ToastLength.Long).Show());
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "投屏邀请失败: " + ex.Message);
                }
            }) { IsBackground = true }.Start();
        }

        /* ===================== 事件 ===================== */

        private void OnBridgeEvent(string type, object payload)
        {
            RunOnUiThread(() =>
            {
                try
                {
                    switch (type)
                    {
                        case "message":
                        {
                            var msg = payload as Dictionary<string, object>;
                            if (msg == null) return;
                            if (Msg.IsControl(msg)) return;
                            if (Msg.Conv(msg) != currentConv) { RefreshConversations(); return; }
                            AppendMessage(msg);
                            // 正在看这个会话：把未读清零（否则自己收到的消息会一直挂红点）
                            bridge.SetActiveConv(currentConv);
                            ScrollToBottom();
                            break;
                        }
                case "msgdel":
                {
                    var msg = payload as Dictionary<string, object>;
                    var target = msg == null ? null : Msg.Str(msg, "target");
                    if (!string.IsNullOrEmpty(target)) RemoveBubble(target);
                    break;
                }
                case "msgedit":
                {
                    var msg = payload as Dictionary<string, object>;
                    if (msg == null) return;
                    if (Msg.Conv(msg) != currentConv) return;
                    // 重新渲染当前会话，使编辑后的正文与「已编辑」标记生效
                    RefreshMessages(true);
                    break;
                }
                        case "msgupd":
                        {
                            // 消息内容被原地替换（典型：先到的是「[无法解密]」占位，
                            // 密钥就绪后重放成功）。从仓库重建这一屏，避免出现两条。
                            var msg = payload as Dictionary<string, object>;
                            if (msg == null) return;
                            if (Msg.Conv(msg) != currentConv) return;
                            // 音乐卡片（切歌/暂停/进度校正）：只刷新这一张卡片，绝不重建整屏
                            if (Msg.Kind(msg) == "musiccard")
                            {
                                MusicCardView card;
                                if (musicCards.TryGetValue(Msg.Str(msg, "id"), out card) && card != null)
                                {
                                    card.Apply(msg);
                                    break;
                                }
                            }
                            RefreshMessages(true);
                            break;
                        }
                        case "convclear":
                        {
                            var msg = payload as Dictionary<string, object>;
                            if (msg == null || Msg.Conv(msg) == currentConv) RefreshMessages(true);
                            break;
                        }
                        case "convrename":
                        case "convremove":
                            // 对端重命名/移除了会话：立即刷新会话条（名称全局一致）
                            RefreshConversations();
                            UpdateHeader();
                            break;
                        case "peers":
                        case "ready":
                        case "network":
                        case "channel":
                            UpdateHeader();
                            break;
                        case "fullchannel-needed":
                            ShowFullChannelPrompt();
                            break;
                        case "fullchannel":
                            UpdateHeader();
                            break;
                        case "error":
                        {
                            var msg = payload as Dictionary<string, object>;
                            var text = msg == null ? null : Msg.Str(msg, "message");
                            if (!string.IsNullOrEmpty(text))
                                L10n.Toast(this, text, ToastLength.Short).Show();
                            break;
                        }
                        case "locburst":
                        {
                            // 本机被远程请求（或手动）连续上报定位：开始/结束各提示一次
                            var d = payload as Dictionary<string, object>;
                            bool active = d != null && Msg.Bool(d, "active");
                            int sec = d == null ? 5 : (int)Msg.Num(d, "seconds", 5);
                            L10n.Toast(this,
                                active ? L10n.T("正在连续发送定位（") + sec + L10n.T(" 秒）") : L10n.T("定位连续上报已结束"),
                                ToastLength.Short).Show();
                            break;
                        }
                        case "locresp":
                        {
                            // 对方没回传坐标时说明原因（没定位权限 / 定位开关没开等）
                            var d = payload as Dictionary<string, object>;
                            bool ok = d != null && Msg.Bool(d, "ok");
                            if (!ok)
                            {
                                var nm = d == null ? null : Msg.Str(d, "name");
                                var er = d == null ? null : Msg.Str(d, "error");
                                L10n.Toast(this,
                                    (string.IsNullOrEmpty(nm) ? L10n.T("对方") : nm) + L10n.T(" 定位失败：")
                                    + (string.IsNullOrEmpty(er) ? L10n.T("未知原因") : er),
                                    ToastLength.Long).Show();
                            }
                            break;
                        }
                        case "locfix":
                        {
                            // 收到对方回传的坐标：明确提示，让用户知道「定位成功了」
                            var d = payload as Dictionary<string, object>;
                            var text = d == null ? null : Msg.Str(d, "text");
                            var nm = d == null ? null : Msg.Str(d, "name");
                            if (!string.IsNullOrEmpty(text))
                                L10n.Toast(this,
                                    L10n.T("收到") + (string.IsNullOrEmpty(nm) ? L10n.T("对方") : nm) + L10n.T("的位置：\n") + text,
                                    ToastLength.Long).Show();
                            break;
                        }
                        case "locreq":
                        {
                            // 发出定位请求后迟迟收不到对方回传：主动说明可能的原因
                            var d = payload as Dictionary<string, object>;
                            if (d != null && Msg.Bool(d, "timeout"))
                                L10n.Toast(this,
                                    L10n.T("未收到对方定位：可能不在同一网络，或对方未授权定位 / 未开启定位服务"),
                                    ToastLength.Long).Show();
                            break;
                        }
                        case "smsforward":
                        {
                            var d = payload as Dictionary<string, object>;
                            var state = d == null ? null : Msg.Str(d, "state");
                            if (state == "sent")
                                L10n.Toast(this, L10n.T("短信已转发到频道"), ToastLength.Short).Show();
                            else if (state == "queued" || state == "failed")
                                L10n.Toast(this, L10n.T("短信已暂存，桥就绪后自动补发"), ToastLength.Short).Show();
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("ChatUI", "处理事件失败: " + ex.Message);
                }
            });
        }

        private void OnConversationsChanged()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    // 列表页始终刷新；聊天页只在标题/未读变化时需要
                    if (!inChat) RefreshConversations();
                    UpdateHeader();
                }
                catch { }
            });
        }

        /// <summary>把 Func&lt;bool&gt; 适配成 Android 的长按监听器（返回 true 表示已消费事件）。</summary>
        private sealed class LongClickAction : Java.Lang.Object, Android.Views.View.IOnLongClickListener
        {
            private readonly Func<bool> action;
            public LongClickAction(Func<bool> action) { this.action = action; }
            public bool OnLongClick(Android.Views.View v)
            {
                try { return action != null && action(); } catch { return false; }
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            // 通话前申请的麦克风权限
            if (requestCode == ReqMicChat)
            {
                bool ok = grantResults != null && grantResults.Length > 0
                          && grantResults[0] == Android.Content.PM.Permission.Granted;
                if (ok)
                {
                    var a = _afterMic;
                    _afterMic = null;
                    a?.Invoke();
                }
                else
                {
                    L10n.Toast(this, L10n.T("没有麦克风权限，无法通话"), ToastLength.Short).Show();
                }
            }

            // 文件共享存储权限：授权后热切换共享根到 /sdcard/Mchat/file 并重开对话框
            if (requestCode == ReqShareStorage)
            {
                try { LanShareService.ReconfigureRoot(this); } catch { }
                ShowFileShareDialogCore();
                return;
            }

            // 转发给网页端/桥接层处理其它权限（相机、存储等）
            try { MchatBridge.OnPermissionResult(requestCode, permissions, grantResults); }
            catch { }
        }

        /* ===================== 顶栏 / 会话条 ===================== */

        /// <summary>
        /// 修正「布局比窗口可见区域高」导致的底部遮挡。
        /// 典型场景：MIUI 手势导航，窗口可见底 = 2210，但布局按 2340 排，
        /// 结果整条输入栏被推出屏幕，看着在、点不到。
        /// </summary>
        private void ApplySystemBarInsets()
        {
            try
            {
                var root = FindViewById<View>(Resource.Id.chatRoot);
                if (root == null) return;

                Action<WindowInsets> apply = insets =>
                {
                    try
                    {
                        int top = 0, bottom = 0;
                        if (insets != null)
                        {
                            bottom = insets.SystemWindowInsets?.Bottom ?? 0;
                            top = insets.StableInsetTop;
                            if (top <= 0) top = insets.SystemWindowInsets?.Top ?? 0;
                        }
                        if (bottom <= 0)
                        {
                            try
                            {
                                int rid = Resources.GetIdentifier("navigation_bar_height", "dimen", "android");
                                if (rid > 0) bottom = Resources.GetDimensionPixelSize(rid);
                            }
                            catch { }
                        }
                        if (bottom <= 0) bottom = (int)(48 * Resources.DisplayMetrics.Density);
                        _gestureInset = bottom;

                        // 顶部：窗口现在由系统避让状态栏（见 OnCreate 的说明），root 不需要再补。
                        // 只有当别处又把窗口改成「边到边」时才需要补回来，避免重复上移。
                        bool edgeToEdge = false;
                        try
                        {
                            var flags = Window.Attributes?.Flags ?? 0;
                            edgeToEdge = (flags & WindowManagerFlags.LayoutNoLimits) != 0;
                        }
                        catch { }
                        if (!edgeToEdge) top = 0;
                        if (top > 200) top = 0;

                        // 软键盘不再在这里算 —— 现在靠系统 adjustResize 把窗口缩短，
                        // 键盘高度天然就体现为「窗口变矮」，输入栏自动落在键盘上方。
                        // 个别 ROM 不缩窗时由 SetupKeyboardAvoidance 的可见区域估算兜底。

                        _statusTop = top;
                        SyncRootPadding();

                        // 内容区不给手势区预留位置（底栏贴底由导航栏配色保证观感）
                        if (pageHost != null)
                            pageHost.SetPadding(pageHost.PaddingLeft, pageHost.PaddingTop,
                                                pageHost.PaddingRight, 0);

                        Android.Util.Log.Info("Mchat", "系统栏补偿：状态栏 " + top
                            + L10n.T("px / 手势区 ") + bottom + L10n.T("px / 键盘 ") + _keyboardPad
                            + L10n.T("px / 窗口边到边 ") + edgeToEdge);
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Mchat", "系统栏补偿失败: " + ex.Message);
                    }
                };

                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    Window.DecorView?.SetOnApplyWindowInsetsListener(new InsetsListener(apply));
                root.Post(() => { try { apply(Window.DecorView?.RootWindowInsets); } catch { } });
            }
            catch { }
        }

        private sealed class InsetsListener : Java.Lang.Object, Android.Views.View.IOnApplyWindowInsetsListener
        {
            private readonly Action<WindowInsets> _act;
            public InsetsListener(Action<WindowInsets> act) { _act = act; }
            public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
            {
                try { _act?.Invoke(insets); } catch { }
                return insets;
            }
        }

        /// <summary>
        /// 软键盘避让兜底。
        ///
        /// 正常路径是系统 adjustResize：键盘弹出 → 窗口变矮 → 输入栏自动落在键盘上方，
        /// 这里什么都不用做（overlap 会是 0）。
        /// 但个别 ROM（含 MIUI 的部分版本）不缩窗，此时「可见区域底边」会明显高于
        /// 「根布局实际底边」，把差值补成根布局底部 padding，效果等同于系统缩窗。
        ///
        /// 阈值取屏幕高 15%：手势导航条约 130px（本机 2340 高 → 阈值 351px），
        /// 不会被误判成键盘，所以平时底栏仍然贴底。
        /// </summary>
        private void SetupKeyboardAvoidance()
        {
            var root = FindViewById<View>(Resource.Id.chatRoot);
            if (root == null) return;
            _kbListener = new KeyboardAvoidListener(this, root, pad =>
            {
                if (pad == _keyboardPad) return;
                _keyboardPad = pad;
                try { SyncRootPadding(); } catch { }
                if (pad > 0) ScrollToBottom();
            });
            root.ViewTreeObserver.AddOnGlobalLayoutListener(_kbListener);
        }

        private sealed class KeyboardAvoidListener : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
        {
            private readonly Activity act;
            private readonly View root;
            private readonly Action<int> apply;

            public KeyboardAvoidListener(Activity act, View root, Action<int> apply)
            {
                this.act = act; this.root = root; this.apply = apply;
            }

            public void OnGlobalLayout()
            {
                try
                {
                    var r = new Android.Graphics.Rect();
                    root.GetWindowVisibleDisplayFrame(r);
                    int[] loc = new int[2];
                    root.GetLocationOnScreen(loc);
                    int rootBottom = loc[1] + root.Height;
                    int overlap = rootBottom - r.Bottom;
                    if (overlap < 0) overlap = 0;
                    int screenH = act.Resources.DisplayMetrics.HeightPixels;
                    // 明显高于手势导航条才算键盘，避免把导航条当键盘而顶起草稿区
                    int threshold = (int)(screenH * 0.15);
                    apply(overlap > threshold ? overlap : 0);
                }
                catch { }
            }
        }

        private void ForceNavHeight()
        {
            if (bottomNav == null) return;
            int labelHeight = new[] { navMsgLabel, navDevicesLabel, navServicesLabel, navMeLabel }
                .Where(label => label != null).Select(label => (int)Math.Ceiling(label.TextSize * 1.4f)).DefaultIfEmpty(0).Max();
            int bar = Math.Max(Dp(IosStyle.ToolbarHeight), Dp(30) + labelHeight);
            // 关键：高度必须包含「底栏当前已有的底部内边距」。ApplySystemBarInsets 会按手势区
            // 给底栏加底部 padding（把图标抬到手势区上方），这里据此把高度撑足；不读 padding 而
            // 硬设 56dp 会和它打架，把图标压进 24px 的缝里（看不见也点不到）。
            // 用「当前 padding」而非自己猜手势高度，既消除执行顺序依赖，也不会误伤无导航栏设备。
            int padBottom = bottomNav.PaddingBottom;
            int h = bar + padBottom;
            var lp = bottomNav.LayoutParameters;
            if (lp == null) lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, h);
            if (lp.Height != h)
            {
                lp.Height = h;
                bottomNav.LayoutParameters = lp;
            }
            bottomNav.Post(() =>
            {
                try
                {
                    int padNow = bottomNav.PaddingBottom;
                    int h2 = bar + padNow;
                    var lp2 = bottomNav.LayoutParameters;
                    if (lp2 != null && lp2.Height != h2)
                    {
                        lp2.Height = h2;
                        bottomNav.LayoutParameters = lp2;
                    }
                    Android.Util.Log.Info("Mchat", "底部 Tab 高度=" + bottomNav.Height
                        + " (期望 " + h2 + ", 底部padding " + padNow + ") root=" + (FindViewById(Resource.Id.chatRoot)?.Height ?? -1));
                }
                catch { }
            });
        }

        private void ApplyBottomInsetFix()
        {
            var root = FindViewById<View>(Resource.Id.chatRoot);
            if (root == null)
            {
                Android.Util.Log.Warn("Mchat", "chatRoot 未找到，跳过底部遮挡修正");
                return;
            }

            // 等第一帧真正布局完成再量（OnCreate 阶段 Height 还是 0）
            new Handler(Looper.MainLooper).PostDelayed(() =>
            {
                try
                {
                    if (root.Height <= 0) return;

                    var rect = new Android.Graphics.Rect();
                    root.GetWindowVisibleDisplayFrame(rect);
                    var loc = new int[2];
                    root.GetLocationOnScreen(loc);

                    int top = loc[1];
                    int bottom = top + root.Height;
                    int overflow = bottom - rect.Bottom;

                    Android.Util.Log.Info("Mchat",
                        L10n.T("布局底边 ") + bottom + L10n.T(" / 可见底 ") + rect.Bottom + L10n.T(" / 高 ") + root.Height);

                    if (overflow > Dp(6))
                    {
                        // 只记录，不直接改 padding —— 统一走 SyncRootPadding，
                        // 免得和键盘抬起的高度互相覆盖。
                        _bottomOverflow = overflow;
                        SyncRootPadding();
                        Android.Util.Log.Info("Mchat", "输入栏被遮挡 " + overflow + "px，已补底部内边距");
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "底部遮挡修正失败: " + ex.Message);
                }
            }, 350);
        }

        /// <summary>顶栏「接入方式」摘要，异常时降级为「无」。</summary>
        private static string SafeIp()
        {
            try { return DeviceStatus.Ip ?? ""; } catch { return ""; }
        }

        private void EnsureHeaderStats()
        {
            long now = Java.Lang.JavaSystem.CurrentTimeMillis();
            if (_headerCacheAt != 0 && now - _headerCacheAt < 800) return;
            _headerCacheAt = now;
            try { _headerOnline = bridge.GetOnlineCount(); }
            catch { try { _headerOnline = bridge.OnlinePeers().Count; } catch { _headerOnline = 0; } }
            try { _headerAccess = bridge.GetAccessSummary(); }
            catch { _headerAccess = L10n.T("无"); }
        }

        private string SafeAccessSummary()
        {
            EnsureHeaderStats();
            return _headerAccess ?? L10n.T("无");
        }

        /// <summary>顶栏「在线」数（按设备 id 去重合并各渠道），异常时退化用局域网在线数。</summary>
        private int SafeOnline()
        {
            EnsureHeaderStats();
            return _headerOnline;
        }

        private void UpdateHeader()
        {
            try
            {
                // 底部 Tab 页：标题就是页面名，副标题给一句人话摘要
                if (!inChat)
                {
                    string t, s;
                    switch (currentTab)
                    {
                        case 1:
                            t = L10n.T("附近设备");
                            s = SafeOnline() + L10n.T(" 台在线 · 同一网络自动发现，可直接聊天或通话");
                            break;
                        case 2:
                            t = L10n.T("共享与服务");
                            s = L10n.T("摄像头 / 屏幕 / 画笔 / 文件，一个端口全搞定");
                            break;
                        case 3:
                            t = L10n.T("我的");
                            s = L10n.T("通信渠道 · 身份 · 机器人 · 更多设置");
                            break;
                        default:
                            t = "AhoChat";
                            s = L10n.T("在线 ") + SafeOnline() + L10n.T(" 台 · 接入方式：") + SafeAccessSummary();
                            break;
                    }
                    txtTitle.Text = t;
                    txtSubtitle.Text = s;
                    return;
                }

                var convs = bridge.GetConversations();
                var cur = convs.FirstOrDefault(c => c.Id == currentConv);
                string title;
                if (cur != null) title = cur.Title;
                else if (Convs.IsDm(currentConv))
                {
                    // 零消息私聊被 GetConversations 过滤，cur 为 null；
                    // Convs.TitleOf 取排序后的固定一段，本机 id 排在后面时会把标题显示成自己。
                    var other = Convs.DmPeer(currentConv, bridge.SelfPeerId);
                    title = string.IsNullOrEmpty(other) ? Convs.TitleOf(currentConv) : other;
                }
                else title = Convs.TitleOf(currentConv);
                txtTitle.Text = title;

                // 顶栏副标题：在线数 + 接入方式（各已连接渠道及设备数），不再显示加密状态。
                txtSubtitle.Text = L10n.T("在线 ") + SafeOnline() + L10n.T(" · 接入方式：") + SafeAccessSummary();
            }
            catch { }
        }

        private void RefreshConversations()
        {
            if (convList == null) return;
            convList.RemoveAllViews();

            List<Conversation> convs;
            try { convs = bridge.GetConversations(); }
            catch { convs = new List<Conversation>(); }

            // 一个白色圆角卡片装所有会话行，行间一条内缩分割线——iOS 分组列表的观感。
            var card = NewGroup(convList, null);
            card.AddView(BuildActionRow(IconKind.Plus, L10n.T("发起新会话"),
                L10n.T("选择在线设备私聊，或加入一个频道"), ShowNewChatSheet));

            if (convs.Count == 0)
            {
                card.AddView(BuildDivider());
                card.AddView(BuildHintRow(L10n.T("还没有会话。\n让另一台设备也打开 AhoChat，它会自动出现在「设备」里。")));
            }

            for (int i = 0; i < convs.Count; i++)
            {
                card.AddView(BuildDivider());
                card.AddView(BuildConvRow(convs[i]));
            }
            ReapplyTheme();
        }

        /// <summary>夜间模式下，内容动态重建后补一次统一重上色（幂等；日间直接跳过）。</summary>
        private void ReapplyTheme()
        {
            try
            {
                UpdateThemeIcon();                                 // 顺带同步「太阳 / 月亮」形态
                if (ThemeManager.IsNight(this)) ThemeEngine.Apply(this);
            }
            catch { }
        }

        /// <summary>主题被切换（顶部按钮 / 设置页 / App 内网页端）时，同步顶栏图标与底栏按钮着色。</summary>
        protected override void OnThemeChanged()
        {
            UpdateThemeIcon();
            try { RefreshMiniBar(); } catch { }   // 重刷底栏播放/上下曲按钮图标颜色（暗色模式可见性）
        }

        /// <summary>
        /// 顶栏一键切换日间 / 夜间：只重刷配色、不重建界面（重建会闪一下）。
        /// 与设置页「外观」共用同一份偏好（ThemeManager），两边互相同步。
        /// </summary>
        private void ToggleThemeQuick()
        {
            try
            {
                bool toNight = !ThemeManager.IsNight(this);
                ThemeManager.SetMode(this, toNight ? ThemeManager.NIGHT : ThemeManager.DAY);
                ApplyThemeLive();
                UpdateThemeIcon();
                ReapplyTheme();   // 顶栏之外的动态内容也补一次（幂等）
            }
            catch { }
        }

        /// <summary>主题按钮图标：日间显示月亮（点一下进夜间），夜间显示太阳。</summary>
        private void UpdateThemeIcon()
        {
            try
            {
                bool night = ThemeManager.IsNight(this);

                // 顶栏标题/副标题在布局里是硬编码深色，这里显式跟随主题，
                // 避免「引擎快照 + 多次切换」时出现深底深字的低对比。
                try
                {
                    if (txtTitle != null)
                        txtTitle.SetTextColor(new Color(night ? unchecked((int)0xFFE6E9EF) : unchecked((int)0xFF1A1A1A)));
                    if (txtSubtitle != null)
                        txtSubtitle.SetTextColor(new Color(night ? unchecked((int)0xFF9AA4B2) : unchecked((int)0xFF9AA0A6)));
                }
                catch { }

                if (iconTheme == null) return;
                iconTheme.Kind = night ? IconKind.Sun : IconKind.Moon;
                iconTheme.Invalidate();
            }
            catch { }
        }

        /// <summary>会话行：圆形头像 + 标题 + 最后一条 + 时间/未读。</summary>
        private View BuildConvRow(Conversation c)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));
            row.Clickable = true;

            var avatar = new TextView(this)
            {
                Text = Initial(c.Title),
                Gravity = GravityFlags.Center,
                TextSize = IosStyle.BodySize
            };
            avatar.SetTextColor(Color.White);
            avatar.SetTypeface(null, TypefaceStyle.Bold);
            avatar.Background = RoundBg(AvatarColor(c.Title), 23);   // 46dp 圆形
            avatar.LayoutParameters = new LinearLayout.LayoutParams(Dp(46), Dp(46));
            row.AddView(avatar);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(8), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var title = new TextView(this)
            {
                // 频道名不再加 # 前缀（用户要求列表里只显示裸名字）
                Text = (c.Title ?? ""),
                TextSize = IosStyle.BodySize
            };
            title.SetTextColor(new Color(TextMain));
            title.SetSingleLine(true);
            title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            col.AddView(title);

            string last = string.IsNullOrEmpty(c.LastText) ? L10n.T("还没有消息") : c.LastText;
            if (!string.IsNullOrEmpty(c.LastFrom)) last = c.LastFrom + "：" + last;
            var sub = new TextView(this) { Text = last, TextSize = IosStyle.CaptionSize };
            sub.SetTextColor(new Color(TextSub));
            sub.SetSingleLine(true);
            sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            sub.SetPadding(0, Dp(3), 0, 0);
            col.AddView(sub);
            row.AddView(col);

            var right = new LinearLayout(this) { Orientation = Orientation.Vertical };
            right.SetGravity(GravityFlags.CenterHorizontal);

            var time = new TextView(this) { Text = FriendlyTime(c.UpdatedAt), TextSize = IosStyle.CaptionSize };
            time.SetTextColor(new Color(NavIdle));
            right.AddView(time);

            if (c.Unread > 0)
            {
                var badge = new TextView(this)
                {
                    Text = c.Unread > 99 ? "99+" : c.Unread.ToString(),
                    Gravity = GravityFlags.Center,
                    TextSize = 10
                };
                badge.SetTextColor(Color.White);
                badge.Background = RoundBg(unchecked((int)0xFFFF3B30), 9);
                badge.SetMinWidth(Dp(18));
                badge.SetPadding(Dp(5), 0, Dp(5), 0);
                var blp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(18));
                blp.TopMargin = Dp(5);
                badge.LayoutParameters = blp;
                right.AddView(badge);
            }
            row.AddView(right);

            row.Click += (s, e) => OpenChatConv(c.Id);
            row.LongClick += (s, e) => ShowConvMenu(c);
            return row;
        }

        /// <summary>列表顶部/分组里的功能入口行（淡色圆角图标 + 标题 + 说明 + 右箭头）。</summary>
        private View BuildActionRow(IconKind kind, string title, string desc, Action onClick)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var box = new FrameLayout(this);
            box.Background = RoundBg(unchecked((int)0xFFEAF6FE), 11);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
            var ic = new IconView(this, kind) { IconColor = Accent };
            var iclp = new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center };
            box.AddView(ic, iclp);
            row.AddView(box);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(6), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var t = new TextView(this) { Text = title, TextSize = IosStyle.BodySize };
            t.SetTextColor(new Color(TextMain));
            col.AddView(t);

            if (!string.IsNullOrEmpty(desc))
            {
                var d = new TextView(this) { Text = desc, TextSize = IosStyle.CaptionSize };
                d.SetTextColor(new Color(TextSub));
                d.SetPadding(0, Dp(3), 0, 0);
                col.AddView(d);
            }
            row.AddView(col);

            var chev = new IconView(this, IconKind.ChevronRight) { IconColor = unchecked((int)0xFFC7C9CE), Thickness = 1.5f };
            row.AddView(chev, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

            row.Click += (s, e) => onClick?.Invoke();
            return row;
        }

        private View BuildHintRow(string text)
        {
            var t = new TextView(this) { Text = text, TextSize = IosStyle.CaptionSize };
            t.SetTextColor(new Color(TextSub));
            t.SetPadding(Dp(16), Dp(16), Dp(16), Dp(18));
            t.SetLineSpacing(0, 1.35f);
            return t;
        }

        /// <summary>行间分割线：从文字起始处开始（iOS 的内缩分割线）。</summary>
        private View BuildDivider()
        {
            var v = new View(this);
            v.SetBackgroundColor(new Color(LineColor2));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            lp.LeftMargin = Dp(62);
            v.LayoutParameters = lp;
            return v;
        }

        /// <summary>新建一个分组卡片（可选组标题），返回可直接往里 addView 的容器。</summary>
        private LinearLayout NewGroup(LinearLayout parent, string header)
        {
            if (!string.IsNullOrEmpty(header))
            {
                var h = new TextView(this) { Text = header, TextSize = IosStyle.CaptionSize };
                h.SetTextColor(new Color(TextSub));
                h.SetPadding(Dp(IosStyle.PageInset), Dp(6), Dp(IosStyle.PageInset), Dp(3));
                parent.AddView(h);
            }
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = RoundBg(CardBg, IosStyle.CardRadius);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.LeftMargin = Dp(IosStyle.PageInset); lp.RightMargin = Dp(IosStyle.PageInset);
            // 统一所有分组卡片的左右宽度与底部间距；无标题卡片顶部留 4dp，避免和上一组贴住。
            if (string.IsNullOrEmpty(header)) lp.TopMargin = Dp(2);
            lp.BottomMargin = Dp(8);
            parent.AddView(card, lp);
            return card;
        }

        /// <summary>圆角/描边背景（用代码生成，避免为每个颜色加一份 drawable）。</summary>
        private Android.Graphics.Drawables.GradientDrawable RoundBg(long color, int radiusDp, long strokeColor = 0)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor((int)color);
            d.SetCornerRadius(Dp(radiusDp));
            if (strokeColor != 0)
                d.SetStroke(Dp(1), Android.Content.Res.ColorStateList.ValueOf(new Color((int)strokeColor)));
            return d;
        }

        private static string Initial(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch > 0x2E80) return ch.ToString();
            }
            return s.Substring(0, 1);
        }

        private static int AvatarColor(string s)
        {
            int[] palette =
            {
                unchecked((int)0xFF4C9AFF), unchecked((int)0xFF34C759), unchecked((int)0xFFFF9F0A),
                unchecked((int)0xFF9B59F6), unchecked((int)0xFFFF3B30), unchecked((int)0xFF32ADE6),
                unchecked((int)0xFFAF52DE), unchecked((int)0xFF30B0C7)
            };
            if (string.IsNullOrEmpty(s)) return palette[0];
            int h = 0;
            foreach (var ch in s) h = (h * 31 + ch) & 0x7FFFFFFF;
            return palette[h % palette.Length];
        }

        private static string FriendlyTime(long ms)
        {
            try
            {
                if (ms <= 0) return "";
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                var now = DateTimeOffset.Now;
                if (t.Date == now.Date) return t.ToString("HH:mm");
                if (t.Date == now.Date.AddDays(-1)) return "昨天";
                if (t.Year == now.Year) return t.ToString("M/d");
                return t.ToString("yy/M/d");
            }
            catch { return ""; }
        }

        /// <summary>「发起新会话」：私聊某台设备 / 按设备 ID 私聊 / 加入频道。
        /// qaul 现在只作为通信渠道存在，不再单独暴露「qaul 群组 / 公开信息」入口。</summary>
        private void ShowNewChatSheet()
        {
            var items = new List<string>
            {
                L10n.T("和在线设备私聊"),
                L10n.T("私聊指定 ID 用户"),
                L10n.T("加入 / 新建频道")
            };
            L10n.Dialog(this)
                .SetTitle(L10n.T("发起新会话"))
                .SetItems(items.ToArray(), (s, e) =>
                {
                    switch (items[e.Which])
                    {
                        case "和在线设备私聊": ShowPeerPicker(); break;
                        case "私聊指定 ID 用户": ShowDmByIdDialog(); break;
                        case "加入 / 新建频道": ShowJoinChannelDialog(); break;
                    }
                })
                .Show();
        }

        /// <summary>
        /// 按「通信渠道的 AhoChat 设备 ID」直接发起私聊。对方不在线也能建会话：
        /// 消息进本地会话与出站队列，等对方上线/经中继可达时投递（与在线设备私聊同一条路径）。
        /// 此处只接受本系列 AhoChat 的设备 ID（dm: 会话），纯 qaul 官方客户端 ID 不接受——
        /// 按需求，纯 qaul 设备只能由对方先私聊本机后才建立会话。
        /// </summary>
        private void ShowDmByIdDialog()
        {
            if (bridge == null) return;
            var et = new EditText(this)
            {
                Hint = L10n.T("粘贴对方 AhoChat 设备 ID"),
                InputType = Android.Text.InputTypes.ClassText
            };
            et.SetSingleLine(true);
            et.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            L10n.Dialog(this)
                .SetTitle(L10n.T("私聊指定 ID 用户"))
                .SetMessage(L10n.T("在对方「我的」页点设备 ID 即可复制，粘贴到此处。"))
                .SetView(et)
                .SetPositiveButton(L10n.T("开始私聊"), (s2, e2) =>
                {
                    var id = (et.Text ?? "").ToString().Trim();
                    if (string.IsNullOrEmpty(id))
                    {
                        L10n.Toast(this, L10n.T("请先填写设备 ID"), ToastLength.Short).Show();
                        return;
                    }
                    if (string.Equals(id, bridge.SelfPeerId, StringComparison.OrdinalIgnoreCase))
                    {
                        L10n.Toast(this, L10n.T("不能和自己私聊"), ToastLength.Short).Show();
                        return;
                    }
                    var conv = bridge.StartDm(id, null);
                    if (string.IsNullOrEmpty(conv))
                    {
                        L10n.Toast(this, L10n.T("设备 ID 无效"), ToastLength.Short).Show();
                        return;
                    }
                    currentConv = conv;
                    RefreshConversations();
                    OpenChatConv(conv);
                })
                .SetNegativeButton(L10n.T("取消"), (s2, e2) => { })
                .Show();
        }

        private void SwitchConv(string conv)
        {
            // 所有"进入某个会话"的入口最终都走这里：切会话 + 进入聊天页
            if (string.IsNullOrEmpty(conv)) return;
            OpenChatConv(conv);
        }

        /* ===================== 消息渲染 ===================== */

        private void RefreshMessages(bool scrollBottom)
        {
            msgList.RemoveAllViews();
            bubbleViews.Clear();
            musicCards.Clear();
            lastRenderTime = 0;

            List<Dictionary<string, object>> messages;
            try { messages = bridge.GetMessages(currentConv, 300); }
            catch { messages = new List<Dictionary<string, object>>(); }

            emptyTip = null;
            if (messages.Count == 0)
            {
                var empty = new TextView(this);
                empty.Text = currentConv == Convs.Room(Convs.DefaultRoom)
                    ? L10n.T("这里还没有消息\n在下面输入框发第一条吧")
                    : L10n.T("还没有消息");
                empty.TextSize = 13;
                empty.SetTextColor(Color.ParseColor("#a6a6a6"));
                empty.Gravity = GravityFlags.Center;
                empty.SetPadding(0, Dp(40), 0, 0);
                msgList.AddView(empty);
                emptyTip = empty;
            }
            else
            {
                foreach (var m in messages) AppendMessage(m, false);
            }

            if (scrollBottom) ScrollToBottom();
            ReapplyTheme();
        }

        private void AppendMessage(Dictionary<string, object> msg)
        {
            AppendMessage(msg, true);
        }

        /// <summary>
        /// 收到他人分享的卡片时的协同播放处理：
        ///  · 音乐（musiccard）→ 不再自动加入「一起听」。音乐卡片上已有「一起听」按钮，
        ///    由用户手动点击后才 SyncListen.StartSync —— 避免收卡就被强行拉进同步播放。
        ///  · 视频（videocard）→ 保持原样：自动进入「一起看」（VideoActivity + watchHost），
        ///    视频源走分享端代理，复用其缓存，避免卡顿。
        /// 视频仍只对「刚收到（60 秒内）」且「每张卡片仅一次」生效，历史消息不会批量触发。
        /// </summary>
        private void TryAutoJoinShared(IDictionary<string, object> msg, bool isSelf)
        {
            try
            {
                if (isSelf) return;
                string kind = Msg.Kind(msg);

                // 音乐卡片：彻底不自动加入，只保留卡片本身的「一起听」按钮供用户手动点击。
                // （本方法内曾有一处 SyncListen.StartSync 自动加入，现已移除；
                //   全程序仅此一处承担过音乐自动加入，删除后不再有自动加入一起听的代码。）
                if (kind == "musiccard") return;

                if (kind != VideoShare.Kind) return;

                string id = Msg.Str(msg, "id", "");
                if (string.IsNullOrEmpty(id)) return;
                lock (_autoJoined)
                {
                    if (!_autoJoined.Add(id)) return;   // 每张视频卡片只自动进入一次
                }
                // 仅对刚刚收到的分享生效，跳过历史消息（时间戳较旧）
                long t = (long)Msg.Num(msg, "time");
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (t <= 0 || (now - t) > 60000) return;

                string watchUrl = VideoShare.WatchPlayUrl(msg);
                string watchHost = Msg.Str(msg, "watchHost", "");
                if (string.IsNullOrEmpty(watchUrl) || string.IsNullOrEmpty(watchHost)) return;
                // 自动进入「一起看」，视频源使用分享端的代理（复用其缓存，防止卡顿）
                var i = new Intent(this, typeof(VideoActivity));
                i.PutExtra(VideoActivity.ExtraPath, watchUrl);
                i.PutExtra(VideoActivity.ExtraTitle, Msg.Str(msg, "title", L10n.T("一起看")));
                i.PutExtra(VideoActivity.ExtraWatchCard, Msg.Str(msg, "watchCard", id));
                i.PutExtra(VideoActivity.ExtraWatchHost, watchHost);
                // 延后一点，等当前气泡布局完成再起播，避免打断渲染
                new Handler(Looper.MainLooper).PostDelayed(() =>
                {
                    try { StartActivity(i); } catch { }
                }, 350);
                L10n.Toast(this, L10n.T("已自动进入「一起看」"), ToastLength.Short).Show();
            }
            catch { }
        }

        private void AppendMessage(Dictionary<string, object> msg, bool checkDup)
        {
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return;
            if (checkDup && bubbleViews.ContainsKey(id)) return;
            if (Msg.IsControl(msg)) return;

            // 共享卡片（屏幕画笔 / 摄像头 / 屏幕共享）：点一下在本 App 内全屏观看
            if (Msg.Kind(msg) == "share")
            {
                // 共享卡片也走普通消息流程：先移除「还没有消息」占位，否则会两行并存。
                if (emptyTip != null)
                {
                    try { msgList.RemoveView(emptyTip); } catch { }
                    emptyTip = null;
                }
                try { msgList.AddView(BuildShareCard(msg)); } catch { }
                return;
            }

            // 第一条消息进来时移除「还没有消息」占位
            if (emptyTip != null)
            {
                try { msgList.RemoveView(emptyTip); } catch { }
                emptyTip = null;
            }

            var time = Msg.Num(msg, "time");
            var container = new LinearLayout(this);
            container.Orientation = Orientation.Vertical;
            container.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            // 时间分隔：间隔超过 5 分钟插入一行居中时间
            if (lastRenderTime == 0 || time - lastRenderTime > 5 * 60 * 1000)
            {
                var clock = new TextView(this);
                clock.Text = FormatTime(time);
                clock.TextSize = 11;
                clock.SetTextColor(Color.ParseColor("#a6a6a6"));
                clock.Gravity = GravityFlags.Center;
                clock.SetPadding(0, Dp(8), 0, Dp(4));
                container.AddView(clock);
            }
            lastRenderTime = time;

            var isSelf = Msg.Str(msg, "from") == bridge.SelfId;
            var kind = Msg.Kind(msg);
            TryAutoJoinShared(msg, isSelf);

            var row = new LinearLayout(this);
            row.Orientation = Orientation.Horizontal;
            row.SetPadding(0, Dp(2), 0, Dp(2));

            // 头像
            var avatar = new TextView(this);
            avatar.Text = Initial(Msg.Str(msg, "fromName", "?"));
            avatar.TextSize = 13;
            avatar.Gravity = GravityFlags.Center;
            avatar.SetTextColor(Color.White);
            avatar.Background = RoundBg(AvatarColor(Msg.Str(msg, "fromName", "?")), 17);   // 34dp 圆形
            var avLp = new LinearLayout.LayoutParams(Dp(34), Dp(34));
            avLp.TopMargin = Dp(2);
            if (isSelf) avLp.LeftMargin = Dp(6); else avLp.RightMargin = Dp(6);
            avatar.LayoutParameters = avLp;

            // 右侧/左侧内容列（昵称 + 气泡）
            var col = new LinearLayout(this);
            col.Orientation = Orientation.Vertical;
            var colLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            col.LayoutParameters = colLp;

            if (!isSelf)
            {
                var name = new TextView(this);
                name.Text = Msg.Str(msg, "fromName", L10n.T("未知设备"));
                name.TextSize = 10;
                name.SetTextColor(Color.ParseColor("#9a9a9a"));
                name.SetPadding(Dp(4), 0, 0, Dp(2));
                col.AddView(name);
            }
            else
            {
                // 自己的气泡贴右（靠头像一侧），与网页端一致
                col.SetGravity(GravityFlags.Right);
            }

            var maxWidth = (int)(Resources.DisplayMetrics.WidthPixels * 0.70);

            // ---- 附件消息：画成媒体卡片（图片直接显示图片、视频显示封面、文件显示文件卡片）----
            // 以前这里和普通文本一样只画一行「🖼 文件名」，图片根本看不见。
            string mediaFile = Msg.Str(msg, "media", "");
            if (string.IsNullOrEmpty(mediaFile)) mediaFile = Msg.Str(msg, "mediaUrl", "");
            string mediaName = Msg.Str(msg, "name", "");
            if (string.IsNullOrEmpty(mediaName)) mediaName = Msg.Str(msg, "text", "");
            MediaKinds mk = string.IsNullOrEmpty(mediaFile)
                ? MediaKinds.None
                : MediaCard.KindOf(kind, string.IsNullOrEmpty(mediaName) ? mediaFile : mediaName);

            var captured = msg;

            if (kind == VideoShare.Kind)
            {
                // 视频分享卡片：封面 + 标题 + 一键全屏播放
                View vv;
                try
                {
                    vv = new VideoCardView(this, msg);
                    vv.LayoutParameters = new LinearLayout.LayoutParams(
                        (int)(Resources.DisplayMetrics.WidthPixels * 0.66), ViewGroup.LayoutParams.WrapContent);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "视频卡片渲染失败: " + ex.Message);
                    vv = new TextView(this) { Text = BubbleText(msg), TextSize = 15 };
                }
                var vLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                vLp.RightMargin = Dp(4); vLp.LeftMargin = Dp(4);
                vv.LayoutParameters = vLp;
                vv.LongClick += (s, e) => { ShowMessageMenu(captured); };
                col.AddView(vv);
            }
            else if (kind == "musiccard")
            {
                // 局域网音乐卡片：封面 + 歌词 + 控制按钮（卡片自己按秒推进歌词）
                View mv;
                try
                {
                    var card = new MusicCardView(this, msg);
                    musicCards[id] = card;
                    card.LayoutParameters = new LinearLayout.LayoutParams(
                        (int)(Resources.DisplayMetrics.WidthPixels * 0.66), ViewGroup.LayoutParams.WrapContent);
                    mv = card;
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "音乐卡片渲染失败: " + ex.Message);
                    var mvTv = new TextView(this) { Text = BubbleText(msg), TextSize = 15 };
                    Icons.Leading(mvTv, IconKind.Music, Accent, 16f, 5f);
                    mv = mvTv;
                }
                var mLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                mLp.RightMargin = Dp(4);
                mLp.LeftMargin = Dp(4);
                mv.LayoutParameters = mLp;
                mv.LongClick += (s, e) => { ShowMessageMenu(captured); };
                col.AddView(mv);
            }
            else if (kind == "update")
            {
                // 程序更新卡片：版本 / 更新说明 / 下载状态 + 一键安装按钮
                View uv;
                try
                {
                    uv = new UpdateCardView(this, msg);
                    uv.LayoutParameters = new LinearLayout.LayoutParams(
                        (int)(Resources.DisplayMetrics.WidthPixels * 0.70), ViewGroup.LayoutParams.WrapContent);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "更新卡片渲染失败: " + ex.Message);
                    uv = new TextView(this) { Text = BubbleText(msg), TextSize = 15 };
                }
                var uLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                uLp.RightMargin = Dp(4); uLp.LeftMargin = Dp(4);
                uv.LayoutParameters = uLp;
                uv.LongClick += (s, e) => { ShowMessageMenu(captured); };
                col.AddView(uv);
            }
            else if (mk != MediaKinds.None)
            {
                View card;
                try
                {
                    card = new MediaCard(this, Accent, TextMain, TextSub, unchecked((int)0xFFECECEC))
                        .Create(msg, isSelf, bridge != null ? bridge.MediaDirectory : "",
                                (int)(Resources.DisplayMetrics.WidthPixels * 0.66));
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "附件卡片渲染失败: " + ex.Message);
                    var cardTv = new TextView(this) { Text = BubbleText(msg), TextSize = 15 };
                    var ci = Icons.ForMessageKind(kind);
                    if (ci.HasValue) Icons.Leading(cardTv, ci.Value, TextSub, 16f, 5f);
                    card = cardTv;
                }
                card.LongClick += (s, e) => ShowMessageMenu(captured);
                var cLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                cLp.RightMargin = Dp(4);
                cLp.LeftMargin = Dp(4);
                card.LayoutParameters = cLp;
                col.AddView(card);
            }
            else
            {
                var bubble = new TextView(this);
                var body = BubbleText(msg);
                bubble.Text = body;
                bubble.TextSize = 15;
                try
                {
                    var mkIcon = Icons.ForMessageKind(kind);
                    if (mkIcon.HasValue) Icons.Leading(bubble, mkIcon.Value, isSelf ? unchecked((int)0xFFFFFFFF) : TextSub, 16f, 5f);
                }
                catch { }
                bubble.SetTextColor(isSelf ? Color.White : new Color(TextMain));
                bubble.Background = RoundBg(isSelf ? Accent : unchecked((int)0xFFFFFFFF), 17);
                bubble.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
                bubble.SetLineSpacing(0f, 1.15f);
                bubble.SetTextIsSelectable(false);

                var bLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                bLp.RightMargin = Dp(4);
                bLp.LeftMargin = Dp(4);
                bubble.SetMaxWidth(maxWidth);
                bubble.LayoutParameters = bLp;

                bubble.LongClick += (s, e) => ShowMessageMenu(captured);

                // 正文里的链接 / 裸 IP：点一下就用内置浏览器打开。
                // 裸 IP 不带端口按 80 端口访问，带了端口就用那个端口（见 TextLinks）。
                var linkColor = isSelf ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFF2F7CF6);
                bool linked = TextLinks.MakeClickable(bubble, body, linkColor, u => OpenInBrowser(u));

                // 卡片类消息（分享的链接/图片/音视频）整条气泡都可点
                var url = Msg.Str(msg, "url");
                if (!linked && !string.IsNullOrEmpty(url) && (kind == "url" || kind == "pic" || kind == "video" || kind == "music"))
                {
                    bubble.Click += (s, e) => OpenInBrowser(url);
                }

                col.AddView(bubble);
            }

            if (isSelf)
            {
                row.AddView(col);
                row.AddView(avatar);
                row.SetGravity(GravityFlags.Right);
            }
            else
            {
                row.AddView(avatar);
                row.AddView(col);
                row.SetGravity(GravityFlags.Left);
            }

            container.AddView(row);
            msgList.AddView(container);
            bubbleViews[id] = container;
        }

        private string BubbleText(Dictionary<string, object> msg)
        {
            var kind = Msg.Kind(msg);
            var text = Msg.Str(msg, "text", "");
            var url = Msg.Str(msg, "url", "");

            switch (kind)
            {
                // 附件一律显示「类型 + 体积」，不显示文件名（文件名只用于下载保存）
                case "pic": return Msg.FileLabel("pic", Msg.Num(msg, "size"));
                case "video": return Msg.FileLabel("video", Msg.Num(msg, "size"));
                case "music": return Msg.FileLabel("audio", Msg.Num(msg, "size"));
                case "url": return string.IsNullOrEmpty(text) ? url : text;
                case "notice": return text;
                case "voice": return "语音" + (Msg.Num(msg, "duration") > 0 ? " " + Msg.Num(msg, "duration") + "″" : "");
                case "file": return Msg.FileLabel("file", Msg.Num(msg, "size"));
                case "location": return "位置共享";
                case "update":
                    var card = UpdateManager.ParseCard(text);
                    if (card != null)
                    {
                        var v = Msg.Str(card, "version", "");
                        var ttl = Msg.Str(card, "title", "");
                        return string.IsNullOrEmpty(v) ? ttl : ttl + " " + v;
                    }
                    return L10n.T("程序更新", "App update");
                default:
                    var t = text ?? "";
                    if (Msg.Bool(msg, "edited")) t += L10n.T(" (已编辑)");
                    return t;
            }
        }

        private void RemoveBubble(string id)
        {
            View v;
            if (!bubbleViews.TryGetValue(id, out v)) return;
            try { msgList.RemoveView(v); } catch { }
            bubbleViews.Remove(id);
        }

        private void ScrollToBottom()
        {
            if (msgScroll == null || msgList == null) return;
            msgScroll.Post(() =>
            {
                try { msgScroll.FullScroll(FocusSearchDirection.Down); } catch { }
            });
            // 刚从列表页切进聊天页时，第一帧还没测量完，补一次延迟滚动
            msgScroll.PostDelayed(() =>
            {
                try { msgScroll.FullScroll(FocusSearchDirection.Down); } catch { }
            }, 90);
        }

        private static string FormatTime(long ms)
        {
            try
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                var today = DateTimeOffset.Now.Date;
                if (t.Date == today) return t.ToString("HH:mm");
                return t.ToString("MM-dd HH:mm");
            }
            catch { return ""; }
        }

        /* ===================== 发送 ===================== */

        private void SendCurrent()
        {
            var text = msgInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            if (!string.IsNullOrEmpty(editingMessageId))
            {
                // 编辑态：更新已有消息而非发送新消息。失败时保留文字与编辑态，不吞掉输入
                var id = editingMessageId;
                bool ok = bridge.EditMessage(currentConv, id, text.Trim());
                if (ok)
                {
                    editingMessageId = null;
                    msgInput.Text = "";
                }
                else L10n.Toast(this, L10n.T("编辑失败，内容已保留"), ToastLength.Short).Show();
                return;
            }

            var msg = bridge.SendInConv(currentConv, text.Trim());
            if (msg != null)
            {
                msgInput.Text = "";        // 只有发送成功才清空，失败保留原文可重试
                ScrollToBottom();
            }
            else L10n.Toast(this, L10n.T("发送失败，内容已保留，点发送可重试"), ToastLength.Short).Show();
        }

        /* ===================== 删除 / 菜单 ===================== */

        private void ShowMessageMenu(Dictionary<string, object> msg)
        {
            var id = Msg.Str(msg, "id");
            var isSelf = Msg.Str(msg, "from") == bridge.SelfId;
            var isText = Msg.Kind(msg) == Msg.TextKind;

            // 动态构造菜单项（点击后按文本分发，索引随条件变化，不再维护数值常量）
            var labels = new List<string>();
            labels.Add(L10n.T("复制文本"));
            labels.Add(L10n.T("分享到其他程序"));
            string body = Msg.Str(msg, "text", "");
            string link = ExtractFirstUrl(body);
            if (string.IsNullOrEmpty(link)) link = Msg.Str(msg, "url", "");
            bool isLink = !string.IsNullOrEmpty(link)
                && (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            if (isLink) labels.Add(L10n.T("链接操作"));
            if (isSelf) labels.Add(L10n.T("撤回"));
            if (isSelf && isText) labels.Add(L10n.T("编辑"));
            labels.Add(L10n.T("删除(仅本机)"));

            L10n.Dialog(this)
                .SetTitle(L10n.T("消息"))
                .SetItems(labels.ToArray(), (s, e) =>
                {
                    var pick = labels[e.Which];
                    if (pick == "复制文本")
                    {
                        try
                        {
                            var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                            cm.Text = body;
                            L10n.Toast(this, L10n.T("已复制"), ToastLength.Short).Show();
                        }
                        catch { }
                    }
                    else if (pick == "分享到其他程序")
                    {
                        ShareMessageOut(msg);
                    }
                    else if (pick == "链接操作")
                    {
                        ShowLinkActions(link);
                    }
                    else if (pick == "撤回")
                    {
                        ConfirmRecall(msg);
                    }
                    else if (pick == "编辑")
                    {
                        BeginEditMessage(msg);
                    }
                    else if (pick == "删除(仅本机)")
                    {
                        if (!string.IsNullOrEmpty(id)) { bridge.DeleteMessage(id, false); RemoveBubble(id); }
                    }
                })
                .Show();
        }

        /// <summary>从文本中提取第一个 http/https 链接。</summary>
        private static string ExtractFirstUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"https?://[^\s\u4e00-\u9fa5<>""'（）()]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Value : "";
        }

        /// <summary>把消息（文本或文件）用系统分享窗口发出去。</summary>
        private void ShareMessageOut(Dictionary<string, object> msg)
        {
            try
            {
                string name = Msg.Str(msg, "name", "");
                string media = Msg.Str(msg, "media", "");
                string mediaUrl = Msg.Str(msg, "mediaUrl", "");
                string localPath = "";
                // 优先取已落地的本地文件路径
                foreach (var candidate in new[] { media, mediaUrl })
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    if (!candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && !candidate.StartsWith("content:", StringComparison.OrdinalIgnoreCase)
                        && System.IO.File.Exists(candidate)) { localPath = candidate; break; }
                }
                var send = new Intent(Intent.ActionSend);
                send.AddFlags(ActivityFlags.GrantReadUriPermission);
                if (!string.IsNullOrEmpty(localPath))
                {
                    var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this,
                        "com.Ahochat.android.fileprovider", new Java.IO.File(localPath));
                    send.SetType("*/*");
                    send.PutExtra(Intent.ExtraStream, uri);
                    if (!string.IsNullOrEmpty(name)) send.PutExtra(Intent.ExtraSubject, name);
                }
                else
                {
                    send.SetType("text/plain");
                    string text = !string.IsNullOrEmpty(name) ? name : Msg.Str(msg, "text", "");
                    send.PutExtra(Intent.ExtraText, text);
                }
                StartActivity(Intent.CreateChooser(send, L10n.T("分享到")));
            }
            catch (System.Exception ex) { L10n.Toast(this, L10n.T("分享失败: ") + ex.Message, ToastLength.Short).Show(); }
        }

        /// <summary>链接三选一：浏览器打开 / 下载内容 / 发到聊天频道。</summary>
        private void ShowLinkActions(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            L10n.Dialog(this)
                .SetTitle(L10n.T("链接操作"))
                .SetItems(new[] { L10n.T("在浏览器打开"), L10n.T("下载内容"), L10n.T("发送到聊天频道") }, (s, e) =>
                {
                    if (e.Which == 0)
                    {
                        OpenInBrowser(url);
                    }
                    else if (e.Which == 1)
                    {
                        try
                        {
                            var d = new Intent(this, typeof(DownloaderEntryActivity));
                            d.SetAction(Intent.ActionView);
                            d.SetData(Android.Net.Uri.Parse(url));
                            d.PutExtra(Intent.ExtraText, url);
                            d.AddFlags(ActivityFlags.NewTask);
                            StartActivity(d);
                        }
                        catch (System.Exception ex) { L10n.Toast(this, L10n.T("下载失败: ") + ex.Message, ToastLength.Short).Show(); }
                    }
                    else
                    {
                        ShowSendLinkToConv(url);
                    }
                })
                .Show();
        }

        /// <summary>选择会话，把链接作为 url 卡片发出去。</summary>
        private void ShowSendLinkToConv(string url)
        {
            var convs = new List<Conversation>();
            try { convs = bridge.GetConversations(); } catch { }
            if (convs.Count == 0) { L10n.Toast(this, L10n.T("暂无会话"), ToastLength.Short).Show(); return; }
            var items = convs.Select(c => c.Title ?? c.Id).ToArray();
            L10n.Dialog(this)
                .SetTitle(L10n.T("发送到聊天频道"))
                .SetItems(items, (s, e) =>
                {
                    if (e.Which < 0 || e.Which >= convs.Count) return;
                    bridge.SendInConv(convs[e.Which].Id, url, "url", url);
                    L10n.Toast(this, L10n.T("已发送链接"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ConfirmRecall(Dictionary<string, object> msg)
        {
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return;
            L10n.Dialog(this)
                .SetTitle(L10n.T("撤回这条消息？"))
                .SetMessage(L10n.T("双方都会删除这条消息（本地立即生效，并通知在线设备同步）。"))
                .SetPositiveButton(L10n.T("撤回"), (s, e) =>
                {
                    bridge.DeleteMessage(id, true);
                    RemoveBubble(id);
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>进入编辑态：把原消息正文填入输入框，发送时改为「编辑」而非「新消息」。</summary>
        private void BeginEditMessage(Dictionary<string, object> msg)
        {
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return;
            editingMessageId = id;
            msgInput.Text = Msg.Str(msg, "text", "");
            msgInput.SetSelection(msgInput.Text.Length);
            msgInput.RequestFocus();
            L10n.Toast(this, L10n.T("编辑中：修改后发送即更新该消息"), ToastLength.Short).Show();
        }

        private void ConfirmDelete(Dictionary<string, object> msg)
        {
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return;
            L10n.Dialog(this)
                .SetTitle(L10n.T("删除这条消息？"))
                .SetMessage(L10n.T("双方都会删除这条消息（本地立即生效，并通知在线设备同步）。"))
                .SetPositiveButton(L10n.T("删除"), (s, e) =>
                {
                    bridge.DeleteMessage(id);
                    RemoveBubble(id);
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ShowConvMenu(Conversation c)
        {
            var items = new List<string> { L10n.T("清空此会话消息") };
            // 默认频道是应用首页，不提供「移除」（移除后会被重新补回，等于无效操作）
            bool isDefault = c.Id == Convs.Chan(ChannelRegistry.DefaultChannelName)
                             || c.Id == Convs.Room(Convs.DefaultRoom);
            if (!isDefault) items.Add(L10n.T("移除该会话"));
            if (!c.IsDm && !isDefault) items.Add(L10n.T("复制频道名"));
            if (!c.IsDm) items.Add(L10n.T("重命名会话"));
            // 接入渠道已改为全局统一控制（见「通信渠道设置」），不再按频道单独选择。
            // qaul 私聊 / 群聊才能经 qaul 发文件附件
            if (Convs.IsQaulDm(c.Id) || Convs.IsQaulGroup(c.Id)) items.Add(L10n.T("经 qaul 发送文件"));

            L10n.Dialog(this)
                .SetTitle(c.Title)
                .SetItems(items.ToArray(), (s, e) =>
                {
                    var pick = items[e.Which];
                    if (pick == "清空此会话消息") ClearConv(c.Id);
                    else if (pick == "经 qaul 发送文件")
                    {
                        pendingFileConv = c.Id;
                        var intent = new Intent(Intent.ActionOpenDocument);
                        intent.AddCategory(Intent.CategoryOpenable);
                        intent.SetType("*/*");
                        try { StartActivityForResult(intent, ReqPickFile); }
                        catch { L10n.Toast(this, L10n.T("无法打开文件选择器"), ToastLength.Short).Show(); }
                    }
                    else if (pick == "复制频道名")
                    {
                        try
                        {
                            var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                            cm.Text = Convs.IsChan(c.Id) ? Convs.ChanName(c.Id) : Convs.RoomCode(c.Id);
                            L10n.Toast(this, L10n.T("频道名已复制"), ToastLength.Short).Show();
                        }
                        catch { }
                    }
                    else if (pick == "重命名会话")
                    {
                        ShowRenameConvDialog(c);
                    }
                    else if (pick == "移除该会话")
                    {
                        bridge.RemoveConversation(c.Id);
                        L10n.Toast(this, L10n.T("已移除会话"), ToastLength.Short).Show();
                        if (currentConv == c.Id) SwitchConv(Convs.Room(Convs.DefaultRoom));
                        RefreshConversations();
                    }
                })
                .Show();
        }

        /// <summary>重命名会话（房间 / qaul 群组）：本地生效并广播，所有设备与界面统一。</summary>
        private void ShowRenameConvDialog(Conversation c)
        {
            var input = new EditText(this);
            input.SetSingleLine(true);
            input.Text = c.Title ?? "";

            var wrap = new LinearLayout(this);
            wrap.Orientation = Orientation.Vertical;
            wrap.SetPadding(Dp(18), Dp(8), Dp(18), 0);
            wrap.AddView(input);

            L10n.Dialog(this)
                .SetTitle(L10n.T("重命名会话"))
                .SetView(wrap)
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    var name = (input.Text ?? "").Trim();
                    if (name.Length == 0) { L10n.Toast(this, L10n.T("名称不能为空"), ToastLength.Short).Show(); return; }
                    bridge.RenameConversation(c.Id, name);
                    L10n.Toast(this, L10n.T("已重命名，各设备将同步"), ToastLength.Short).Show();
                    RefreshConversations();
                    UpdateHeader();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ClearConv(string conv)
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("清空消息？"))
                .SetMessage(L10n.T("该会话的全部消息将被删除，并通知在线设备同步删除。"))
                .SetPositiveButton(L10n.T("清空"), (s, e) =>
                {
                    var n = bridge.ClearConversation(conv);
                    RefreshMessages(true);
                    RefreshConversations();
                    L10n.Toast(this, L10n.T("已清空 ") + n + L10n.T(" 条消息"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /* ===================== 频道（频道=聊天室，链路=传输方式） ===================== */

        /// <summary>
        /// 菜单「加入频道」：填频道名 + 勾选接入方式。
        /// 默认只勾 qaul（需求：默认只监听 qaul 频道）；可再加蓝牙 / WLAN Direct / 局域网。
        /// 一个频道可以同时用多种方式接入，各端消息互通。
        /// </summary>
        private void ShowJoinChannelDialog()
        {
            var input = new EditText(this);
            input.Hint = L10n.T("频道名，例如 mesh / 501 / 项目组");
            input.SetSingleLine(true);
            input.SetTextColor(Color.ParseColor("#1a1a1a"));

            var boxes = new List<Android.Widget.CheckBox>();
            var linksBox = new LinearLayout(this);
            linksBox.Orientation = Orientation.Vertical;
            linksBox.SetPadding(Dp(18), Dp(6), Dp(18), 0);
            for (int i = 0; i < ChanLink.All.Length; i++)
            {
                var link = ChanLink.All[i];
                var cb = new Android.Widget.CheckBox(this);
                cb.Text = ChanLink.Label(link);
                cb.TextSize = 14;
                cb.Checked = (i == 0);   // 默认只勾 qaul
                boxes.Add(cb);
                linksBox.AddView(cb);
            }

            var wrap = new LinearLayout(this);
            wrap.Orientation = Orientation.Vertical;
            wrap.SetPadding(Dp(18), Dp(8), Dp(18), 0);
            wrap.AddView(input);

            var tip = new TextView(this);
            tip.Text = L10n.T("接入方式（可多选；qaul 为主链路，有公共节点可经公网中转）");
            tip.TextSize = 12;
            tip.SetPadding(Dp(18), Dp(10), Dp(18), 0);
            wrap.AddView(tip);
            wrap.AddView(linksBox);

            L10n.Dialog(this)
                .SetTitle(L10n.T("加入频道"))
                .SetView(wrap)
                .SetPositiveButton(L10n.T("进入"), (s, e) =>
                {
                    var name = Convs.NormalizeChannel(input.Text);
                    var picked = new List<string>();
                    for (int i = 0; i < boxes.Count; i++)
                        if (boxes[i].Checked) picked.Add(ChanLink.All[i]);
                    if (picked.Count == 0) picked.Add(ChanLink.Qaul);

                    var conv = bridge.JoinChannel(name, string.Join(",", picked));
                    if (string.IsNullOrEmpty(conv)) return;
                    currentConv = conv;
                    RefreshConversations();
                    RefreshMessages(true);
                    UpdateHeader();

                    // 蓝牙（bitchat）已降级为全局渠道开关，不再作为频道链路；这里只提示一次
                    if (picked.Contains(ChanLink.P2p) && !bridge.P2pEnabled)
                    {
                        bridge.EnableP2p();
                        L10n.Toast(this, L10n.T("正在开启 WLAN Direct 链路"), ToastLength.Short).Show();
                    }
                    else
                        L10n.Toast(this, L10n.T("已进入 #") + name + "（" +
                            string.Join("+", picked.Select(ChanLink.ShortLabel)) + "）",
                            ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>菜单「频道列表与链路设置」：列出已加入频道，长按改链路。</summary>
        private void ShowChannelListDialog()
        {
            var chans = bridge.GetChannelList();
            if (chans.Count == 0)
            {
                L10n.Toast(this, L10n.T("还没有加入任何频道"), ToastLength.Short).Show();
                return;
            }

            // 接入渠道全局统一，频道不再单独显示链路徽标。
            var labels = chans.Select(c =>
                (c.ContainsKey("title") ? c["title"].ToString() : "#")).ToArray();

            L10n.Dialog(this)
                .SetTitle(L10n.T("频道列表"))
                .SetItems(labels, (s, e) =>
                {
                    var c = chans[e.Which];
                    ShowChannelLinksDialog(c["name"].ToString(), c["title"].ToString());
                })
                .SetNeutralButton(L10n.T("刷新"), (s, e) => ShowChannelListDialog())
                .SetPositiveButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        /// <summary>接入渠道已全局统一，这里只做说明（保留入口，避免遗留调用报错）。</summary>
        private void ShowChannelLinksDialog(string name, string title)
        {
            L10n.Dialog(this)
                .SetTitle(title)
                .SetMessage(L10n.T("接入渠道已改为由「通信渠道设置」统一控制，不再按频道单独选择。\n\n") +
                            L10n.T("本频道会自动使用 App 当前启用的所有渠道（qaul / WLAN Direct / 蓝牙 / 局域网）互联，各端消息自动互通。"))
                .SetPositiveButton(L10n.T("去设置"), (s, e) =>
                {
                    try { StartActivity(new Intent(this, typeof(ChannelSettingsActivity))); } catch { }
                })
                .SetNegativeButton(L10n.T("知道了"), (s, e) => { })
                .Show();
        }

        /// <summary>菜单「WLAN Direct」：开关 + 扫描 + 点选连接。</summary>
        private void ShowP2pDialog()
        {
            bridge.DiscoverP2p();
            var peers = bridge.P2pPeers();
            var st = bridge.GetP2pStatus() as Newtonsoft.Json.Linq.JObject
                     ?? Newtonsoft.Json.Linq.JObject.FromObject(bridge.GetP2pStatus());

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("WLAN Direct（Wi-Fi P2P）" + (bridge.P2pEnabled ? L10n.T("：已开启") : L10n.T("：未开启")));
            sb.AppendLine(L10n.T("本机 P2P 地址：") + (string)st["localIp"] ?? "-");
            sb.AppendLine("已组网：" + ((bool?)st["groupFormed"] == true ? "是" : "否"));
            if (!string.IsNullOrEmpty((string)st["lastError"]))
                sb.AppendLine((string)st["lastError"]);
            sb.AppendLine();

            if (peers.Count == 0)
            {
                sb.AppendLine(L10n.T("（没有发现 P2P 设备：需对方也支持 Wi-Fi Direct 且系统允许）"));
            }

            var box = new LinearLayout(this);
            box.Orientation = Orientation.Vertical;
            var tv = new TextView(this);
            tv.Text = sb.ToString();
            tv.TextSize = 12;
            tv.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));
            box.AddView(tv);

            foreach (var p in peers)
            {
                var row = new TextView(this);
                row.Text = (string.IsNullOrEmpty(p.Name) ? (p.DeviceName ?? p.Mac) : p.Name) +
                           "  " + (p.Connected ? L10n.T("已连接 ") + p.Ip : L10n.T("未连接"));
                row.TextSize = 14;
                row.SetPadding(Dp(20), Dp(10), Dp(20), Dp(10));
                row.SetTextColor(Color.ParseColor("#1f7a3d"));
                try { Icons.Leading(row, IconKind.Signal, Color.ParseColor("#1f7a3d"), 14f, 6f); } catch { }
                var mac = p.Mac;
                row.Click += (s, e) =>
                {
                    if (string.IsNullOrEmpty(mac)) return;
                    bridge.ConnectP2p(mac);
                    L10n.Toast(this, L10n.T("已发起 P2P 连接…"), ToastLength.Short).Show();
                };
                box.AddView(row);
            }

            var scroll = new ScrollView(this);
            scroll.AddView(box);

            L10n.Dialog(this)
                .SetTitle("WLAN Direct（Wi-Fi P2P）")
                .SetView(scroll)
                .SetPositiveButton(bridge.P2pEnabled ? L10n.T("关闭 P2P") : L10n.T("开启 P2P"), (s, e) =>
                {
                    if (bridge.P2pEnabled) { bridge.DisableP2p(); L10n.Toast(this, L10n.T("WLAN Direct 已关闭"), ToastLength.Short).Show(); }
                    else L10n.Toast(this, bridge.EnableP2p() ? L10n.T("WLAN Direct 已开启") : L10n.T("WLAN Direct 不可用"), ToastLength.Short).Show();
                })
                .SetNeutralButton(L10n.T("重新扫描"), (s, e) => { bridge.DiscoverP2p(); ShowP2pDialog(); })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        /* ===================== bitchat 蓝牙链路 ===================== */

        /// <summary>菜单「蓝牙链路」：开关（默认关闭）。</summary>

        /// <summary>菜单「蓝牙 Mesh (bitchat)」：开启/关闭。未授权蓝牙权限时由桥接层弹系统授权。</summary>
        private void ToggleBitchat()
        {
            try
            {
                var wasEnabled = bridge.BitchatEnabled;
                if (wasEnabled) bridge.DisableBitchat();
                else bridge.EnableBitchat();
                var nowEnabled = bridge.BitchatEnabled;
                L10n.Toast(this,
                    nowEnabled ? L10n.T("蓝牙已开启")
                        : wasEnabled ? L10n.T("蓝牙已关闭")
                        : L10n.T("正在开启蓝牙…（如弹出请允许蓝牙权限）"),
                    ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("蓝牙操作失败: ") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>
        /// 菜单「bitchat 在线设备（蓝牙）」：列出 BLE mesh 发现的邻居。
        /// 蓝牙只是链路之一；这里点进去是为了**找到设备**，不会自动往顶栏塞会话。
        /// </summary>
        private void ShowBitchatDevices()
        {
            if (!bridge.BitchatEnabled)
            {
                // 默认禁用：第一次进这个入口时才开启（含蓝牙权限申请）
                bridge.EnableBitchat();
                L10n.Toast(this, L10n.T("正在开启蓝牙链路（如弹出请允许蓝牙权限）…"), ToastLength.Short).Show();
            }
            try
            {
                var peers = bridge.BitchatNeighborList();

                var labels = new List<string>();
                foreach (var n in peers)
                {
                    var peer = n.PeerId ?? n.Address ?? "?";
                    var name = string.IsNullOrEmpty(n.Name) ? "" : " · " + n.Name;
                    var conn = n.Connected ? L10n.T(" · 已连接") : L10n.T(" · 未连接");
                    labels.Add(peer + name + conn);
                }
                if (labels.Count == 0)
                    labels.Add(L10n.T("（暂无蓝牙邻居：需要对方也开启本程序的蓝牙链路）"));

                L10n.Dialog(this)
                    .SetTitle(L10n.T("蓝牙在线设备"))
                    .SetItems(labels.ToArray(), (s, e) =>
                    {
                        if (e.Which < 0 || e.Which >= peers.Count) return;
                        var n = peers[e.Which];
                        var peer = n.PeerId;
                        if (string.IsNullOrEmpty(peer)) return;
                        var conv = Convs.BitchatDm(bridge.Identity.BitchatPeerId, peer);
                        bridge.SetConvTitle(conv, string.IsNullOrEmpty(n.Name) ? peer : n.Name);
                        SwitchConv(conv);
                    })
                    .SetNeutralButton(L10n.T("重新扫描"), (s, e) => ShowBitchatDevices())
                    .SetPositiveButton(L10n.T("关闭"), (s, e) => { })
                    .Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("读取蓝牙设备失败: ") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>
        /// 菜单「Mchat 在线设备（中间层·含中继）」：中间层自带发现的结果。
        ///
        /// 与「局域网在线设备」的区别：这一栏**不依赖局域网探测权限**——
        /// 发现靠的是周期性加密信标，信标走每条链路（含 qaul 中继）并按跳数扩散，
        /// 所以走中继、不在同一网段、拿不到组播权限时也能找到人。
        /// 点进去是为了找设备，不会自动往顶栏塞会话。
        /// </summary>
        private void ShowLcDevices()
        {
            try
            {
                var st = Newtonsoft.Json.Linq.JObject.FromObject(bridge.GetLcMidStatus());
                var beacons = st["beacons"] == null ? 0 : (long)st["beacons"];
                var relayed = st["relayed"] == null ? 0 : (long)st["relayed"];

                var list = bridge.GetLcDevices();
                var labels = new List<string>();
                foreach (var o in list)
                {
                    var jo = (Newtonsoft.Json.Linq.JObject)o;
                    var id = jo["id"] == null ? "?" : jo["id"].ToString();
                    var name = jo["name"] == null ? "" : jo["name"].ToString();
                    var link = jo["link"] == null ? "?" : jo["link"].ToString();
                    var dist = jo["distance"] == null ? 1 : (int)jo["distance"];
                    var age = jo["age"] == null ? 0L : (long)jo["age"];

                    var shown = string.IsNullOrEmpty(name) ? id : name;
                    var via = dist > 1 ? L10n.T(" · 中继 ") + dist + L10n.T(" 跳") : L10n.T(" · 直连");
                    var chans = jo["channels"] as Newtonsoft.Json.Linq.JArray;
                    var ctext = "";
                    if (chans != null && chans.Count > 0)
                        ctext = " · #" + string.Join(" #", chans.Select(x => x.ToString()));

                    labels.Add(shown + " · " + ChanLink.ShortLabel(link) + via +
                               " · " + (age / 1000) + "s" + ctext);
                }
                if (labels.Count == 0)
                    labels.Add(L10n.T("（暂无：需要对方也开着本程序。可点「立即广播」再等 10 秒）"));

                L10n.Dialog(this)
                    .SetTitle(L10n.T("AhoChat 在线设备（信标 ") + beacons + L10n.T(" · 中继 ") + relayed + "）")
                    .SetItems(labels.ToArray(), (s, e) =>
                    {
                        if (e.Which < 0 || e.Which >= list.Count) return;
                        var jo = (Newtonsoft.Json.Linq.JObject)list[e.Which];
                        var id = jo["id"] == null ? null : jo["id"].ToString();
                        if (string.IsNullOrEmpty(id)) return;
                        var name = jo["name"] == null ? null : jo["name"].ToString();
                        var conv = bridge.OpenLcDeviceDm(id, name);
                        if (string.IsNullOrEmpty(conv)) return;
                        SwitchConv(conv);
                    })
                    .SetNeutralButton(L10n.T("立即广播"), (s, e) =>
                    {
                        bridge.MidBeaconNow();
                        L10n.Toast(this, L10n.T("已广播发现信标"), ToastLength.Short).Show();
                        ShowLcDevices();
                    })
                    .SetPositiveButton(L10n.T("关闭"), (s, e) => { })
                    .Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("读取中间层设备失败: ") + ex.Message, ToastLength.Short).Show();
            }
        }

        private void ShowMainMenu()
        {
            // 右上角只管「设置与管理」；发消息、通话、共享这些日常动作都在输入框的「+」里。
            var items = new List<string>
            {
                L10n.T("通信渠道设置（IP / 蓝牙 / 渠道开关）"),
                L10n.T("频道列表"),
                L10n.T("加入频道"),
                L10n.T("频道密码（端到端加密）"),
                L10n.T("在线设备（局域网 · qaul · 中继）"),
                L10n.T("Mesh 统计（链路收发 / 跳数 / 丢弃）"),
                L10n.T("qaul 静态节点"),
                L10n.T("身份与指纹"),
                L10n.T("设置昵称"),
                L10n.T("机器人设置"),
                L10n.T("服务地址（本机所有入口）"),
                L10n.T("权限管理"),
                L10n.T("悬浮窗"),
                L10n.T("短信转发（状态 / 自检）"),
                L10n.T("全渠道接入（无网自组网）"),
                L10n.T("彻底退出（移除非初始频道）"),
                L10n.T("清空全部消息")
            };

            L10n.Dialog(this)
                .SetTitle(L10n.T("菜单"))
                .SetItems(items.ToArray(), (s, e) =>
                {
                    switch (items[e.Which])
                    {
                        case "通信渠道设置（IP / 蓝牙 / 渠道开关）":
                            StartActivity(new Intent(this, typeof(ChannelSettingsActivity)));
                            break;
                        case "频道列表": ShowChannelListDialog(); break;
                        case "加入频道": ShowJoinChannelDialog(); break;
                        case "频道密码（端到端加密）": ShowChannelPasswordDialog(); break;
                        case "在线设备（局域网 · qaul · 中继）": ShowLcDevices(); break;
                        case "Mesh 统计（链路收发 / 跳数 / 丢弃）": ShowMeshStats(); break;
                        case "qaul 静态节点": ShowQaulStaticPeers(); break;
                        case "身份与指纹": ShowIdentityDialog(); break;
                        case "设置昵称": ShowRenameDialog(); break;
                        case "服务地址（本机所有入口）": ShowServicesDialog(); break;
                        case "权限管理": ShowPermissionsDialog(); break;
                        case "悬浮窗": ToggleFloatingWindow(); break;
                        case "短信转发（状态 / 自检）": ShowSmsForwardDialog(); break;
                        case "全渠道接入（无网自组网）": ToggleFullChannel(); break;
                        case "彻底退出（移除非初始频道）": ConfirmHardExit(); break;
                        case "清空全部消息": ClearAll(); break;
                    }
                })
                .Show();
        }

        /// <summary>本机所有对外入口一览（先列 IP，再列路径入口，便于多网卡自行拼接）。</summary>
        private void ShowServicesDialog()
        {
            var cam = IpCameraServer.Instance;
            var port = WebServer.ExternalPortActual;
            var ips = DeviceStatus.AllIps;
            var ip6 = DeviceStatus.Ipv6;

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(18), Dp(6), Dp(18), Dp(6));

            Action<string> label = (t) =>
            {
                var tv = new TextView(this) { Text = t };
                tv.TextSize = 11;
                tv.SetTextColor(new Color(TextSub));
                tv.SetPadding(0, Dp(10), 0, Dp(2));
                col.AddView(tv);
            };
            Action<string, string> copyRow = (show, copy) =>
            {
                var tv = new TextView(this) { Text = show };
                tv.TextSize = 14;
                tv.SetTextColor(new Color(Accent));
                tv.SetPadding(0, Dp(3), 0, Dp(3));
                tv.Click += (s, e) => CopyText(copy);
                col.AddView(tv);
            };

            // 本机 IPv4（每个地址点击即复制 "ip:端口"）
            label(L10n.T("本机 IPv4（端口 ") + port + L10n.T("，点击复制）"));
            foreach (var ip in ips)
            {
                var a = MchatBridge.FormatHostPort(ip, port);
                copyRow(a, a);
            }

            // 本机 IPv6
            if (ip6 != "::" && !string.IsNullOrEmpty(ip6))
            {
                label(L10n.T("本机 IPv6（端口 ") + port + L10n.T("，点击复制）"));
                var a6 = MchatBridge.FormatHostPort(ip6, port);
                copyRow(a6, a6);
            }
            else
            {
                label(L10n.T("本机 IPv6：未获取（当前网络无 IPv6 地址）"));
            }

            // 统一入口路径
            label(L10n.T("统一入口（一个端口，按路径分流，用上方地址拼接）"));
            var sb = new System.Text.StringBuilder();
            sb.Append("/hub".PadRight(16)).Append(L10n.T("服务导航\n"));
            sb.Append("/cam".PadRight(16)).Append(L10n.T("摄像头 / 屏幕共享"))
              .Append(cam != null && cam.IsRunning ? "（共享中）" : "（未开启）").Append("\n");
            sb.Append("/draw".PadRight(16)).Append(L10n.T("屏幕画笔直播"))
              .Append(ScreenDrawService.IsRunning ? L10n.T("（共享中）") : L10n.T("（未开启）")).Append("\n");
            sb.Append("/file".PadRight(16)).Append(L10n.T("文件浏览 / 下载\n"));
            sb.Append("/media".PadRight(16)).Append(L10n.T("影音库（图片 / 音乐 / 视频）\n"));
            sb.Append("/reader".PadRight(16)).Append(L10n.T("网页阅读器\n"));
            sb.Append("/".PadRight(16)).Append(L10n.T("网页端聊天\n"));
            sb.Append("/cam/live.ts".PadRight(16)).Append("（HLS）\n");
            sb.Append("/cam/stream.h264".PadRight(16)).Append(L10n.T("（裸流）"));
            var paths = new TextView(this) { Text = sb.ToString() };
            paths.TextSize = 13;
            paths.SetTextColor(new Color(TextMain));
            col.AddView(paths);

            // FTP（单独端口，点击复制完整地址）
            label(L10n.T("FTP（协议不同，单独端口，点击复制）"));
            foreach (var ip in ips)
            {
                var u = "ftp://" + ip + ":" + FtpServer.Port;
                copyRow(u, u);
            }

            var sv = new ScrollView(this);
            sv.AddView(col);

            L10n.Dialog(this)
                .SetTitle(L10n.T("服务地址"))
                .SetView(sv)
                .SetPositiveButton(L10n.T("复制导航页"), (s, e) => CopyText(WebServer.UrlOf("/hub")))
                .SetNeutralButton(L10n.T("打开导航页"), (s, e) => OpenInBrowser(WebServer.UrlOf("/hub")))
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void CopyText(string text)
        {
            try
            {
                var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                cm.Text = text ?? "";
                L10n.Toast(this, L10n.T("已复制：") + text, ToastLength.Short).Show();
            }
            catch { }
        }

        /// <summary>
        /// 无 Wi-Fi 局域网时弹出的「全渠道接入」询问框：让用户一键开启离线自组网，
        /// 拉起 WLAN Direct + 蓝牙，使附近设备无需路由器即可通信。
        /// </summary>
        private void ShowFullChannelPrompt()
        {
            try
            {
                if (IsFinishing || IsDestroyed) return;
                L10n.Dialog(this)
                    .SetTitle(L10n.T("无 Wi-Fi 局域网"))
                    .SetMessage(L10n.T("当前设备没有可用的 Wi-Fi 局域网，设备间无法通过局域网直接通信。\n\n是否开启「全渠道接入」？它将自动启用 WLAN Direct（Wi-Fi 直连）与蓝牙，让附近设备无需任何路由器即可自组网，恢复设备间的聊天与通话。"))
                    .SetPositiveButton(L10n.T("开启全渠道接入"), (s, e) =>
                    {
                        try
                        {
                            bridge.EnableFullChannelAccess();
                            L10n.Toast(this, L10n.T("正在开启全渠道接入…"), ToastLength.Short).Show();
                            UpdateHeader();
                        }
                        catch { }
                    })
                    .SetNegativeButton(L10n.T("暂不"), (s, e) =>
                    {
                        try { bridge.DismissFullChannelPrompt(); } catch { }
                    })
                    .SetCancelable(true)
                    .Show();
            }
            catch { }
        }

        /// <summary>菜单里的「全渠道接入」开关：开启即拉起离线链路，关闭则不再自动恢复。</summary>
        private void ToggleFullChannel()
        {
            try
            {
                if (bridge.FullChannelAccess)
                {
                    L10n.Dialog(this)
                        .SetTitle(L10n.T("关闭全渠道接入"))
                        .SetMessage(L10n.T("关闭后，失去 Wi-Fi 局域网时将不再自动启用 WLAN Direct 与蓝牙自组网。仍可在「通信渠道设置」里手动开启各条链路。"))
                        .SetPositiveButton(L10n.T("关闭"), (s, e) =>
                        {
                            try
                            {
                                bridge.DisableFullChannelAccess();
                                L10n.Toast(this, L10n.T("已关闭全渠道接入"), ToastLength.Short).Show();
                                UpdateHeader();
                            }
                            catch { }
                        })
                        .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                        .Show();
                }
                else
                {
                    bridge.EnableFullChannelAccess();
                    L10n.Toast(this, L10n.T("正在开启全渠道接入…"), ToastLength.Short).Show();
                    UpdateHeader();
                }
            }
            catch { }
        }

        /// <summary>Mesh 链路统计：各链路 收/发/中继/丢弃/熔断 + 跳数上限，多机联调排障用。</summary>
        private void ShowMeshStats()
        {
            try
            {
                L10n.Dialog(this)
                    .SetTitle(L10n.T("Mesh 链路统计"))
                    .SetMessage(bridge.GetMeshStatsText())
                    .SetPositiveButton(L10n.T("关闭"), (s, e) => { })
                    .Show();
            }
            catch { }
        }

        /* ===================== 权限管理 ===================== */

        private struct PermCheck { public string label; public bool granted; public Action fix; }

        /// <summary>权限管理：逐项列关键权限状态；点击未授权项跳系统设置手动开启。</summary>
        private void ShowPermissionsDialog()
        {
            try
            {
                var checks = BuildPermissionChecks();
                var listView = new LinearLayout(this) { Orientation = Orientation.Vertical };
                for (int pi = 0; pi < checks.Count; pi++)
                {
                    var pc = checks[pi];
                    var r = new TextView(this);
                    r.Text = pc.label;
                    r.TextSize = 15;
                    r.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
                    try
                    {
                        r.SetCompoundDrawablesWithIntrinsicBounds(
                            new IconDrawable(this, pc.granted ? IconKind.Check : IconKind.Close,
                                (int)(pc.granted ? Color.Rgb(0x22, 0xC5, 0x5E) : Color.Rgb(0xE5, 0x4D, 0x4D)), Dp(18)),
                            null, null, null);
                        r.CompoundDrawablePadding = Dp(10);
                    }
                    catch { }
                    if (!pc.granted)
                    {
                        var fix = pc.fix;
                        r.Click += (s2, e2) => { if (fix != null) fix(); else OpenAppSettings(); };
                    }
                    listView.AddView(r);
                }
                var psv = new ScrollView(this); psv.AddView(listView);

                var dlg = L10n.Dialog(this)
                    .SetTitle(L10n.T("权限管理"))
                    .SetView(psv)
                    .SetPositiveButton(L10n.T("申请运行时权限"), (s, e) => RequestAllRuntimePerms())
                    .SetNegativeButton(L10n.T("关闭"), (s, e) => { });

                bool anyMissing = false;
                foreach (var c in checks) if (!c.granted) { anyMissing = true; break; }
                if (anyMissing) dlg.SetNeutralButton(L10n.T("系统设置"), (s, e) => OpenAppSettings());
                dlg.Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("打开权限管理失败: ") + ex.Message, ToastLength.Short).Show();
            }
        }

        private List<PermCheck> BuildPermissionChecks()
        {
            int api = (int)Android.OS.Build.VERSION.SdkInt;
            var list = new List<PermCheck>();
            Action<string, string, int> add = (label, perm, minApi) =>
            {
                if (api < minApi) { list.Add(new PermCheck { label = label + L10n.T("（本系统无需）"), granted = true }); return; }
                bool ok;
                try { ok = CheckSelfPermission(perm) == Permission.Granted; } catch { ok = false; }
                list.Add(new PermCheck { label = label, granted = ok });
            };

            add(L10n.T("定位（请求其它设备定位）"), Android.Manifest.Permission.AccessFineLocation, 0);
            add(L10n.T("蓝牙（扫描 / 连接 / 广播）"), "android.permission.BLUETOOTH_SCAN", 31);
            add(L10n.T("附近设备（WLAN Direct）"), "android.permission.NEARBY_WIFI_DEVICES", 33);
            add(L10n.T("通知"), "android.permission.POST_NOTIFICATIONS", 33);
            add(L10n.T("短信（转发）"), "android.permission.RECEIVE_SMS", 0);
            add(L10n.T("读取短信（兜底转发）"), "android.permission.READ_SMS", 0);
            add(L10n.T("相机"), "android.permission.CAMERA", 0);
            add(L10n.T("麦克风（语音 / 通话）"), "android.permission.RECORD_AUDIO", 0);

            try
            {
                bool overlay = Android.Provider.Settings.CanDrawOverlays(this);
                list.Add(new PermCheck { label = L10n.T("悬浮窗（显示在其他应用上层）"), granted = overlay });
            }
            catch { }
            try
            {
                bool allFiles = api < 30 ? true : Android.OS.Environment.IsExternalStorageManager;
                list.Add(new PermCheck { label = L10n.T("所有文件访问"), granted = allFiles });
            }
            catch { }
            // 自启动/保活：忽略电池优化（国产 ROM 冻结后台会收不到消息、弹不出横幅）
            try
            {
                bool ignore = AutoStartHelper.IsIgnoringBatteryOptimizations(this);
                var act = this;
                list.Add(new PermCheck
                {
                    label = L10n.T("自启动 / 后台保活（忽略电池优化）"),
                    granted = ignore,
                    fix = () => AutoStartHelper.GuideAutoStart(act)
                });
            }
            catch { }
            return list;
        }

        private void RequestAllRuntimePerms()
        {
            try
            {
                int api = (int)Android.OS.Build.VERSION.SdkInt;
                var need = new List<string>();
                Action<string> want = p => { try { if (CheckSelfPermission(p) != Permission.Granted) need.Add(p); } catch { } };

                want(Android.Manifest.Permission.AccessFineLocation);
                if (api >= 31) { want("android.permission.BLUETOOTH_SCAN"); want("android.permission.BLUETOOTH_CONNECT"); want("android.permission.BLUETOOTH_ADVERTISE"); }
                if (api >= 33) { want("android.permission.NEARBY_WIFI_DEVICES"); want("android.permission.POST_NOTIFICATIONS"); }
                want("android.permission.RECEIVE_SMS");
                want("android.permission.READ_SMS");
                want(Android.Manifest.Permission.Camera);
                want(Android.Manifest.Permission.RecordAudio);

                if (need.Count > 0) RequestPermissions(need.ToArray(), 7302);
                else L10n.Toast(this, L10n.T("运行时权限均已授权"), ToastLength.Short).Show();
            }
            catch { }
        }

        private void OpenAppSettings()
        {
            try
            {
                var i = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings);
                i.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                i.AddFlags(ActivityFlags.NewTask);
                StartActivity(i);
            }
            catch
            {
                try { StartActivity(new Intent(Android.Provider.Settings.ActionSettings)); } catch { }
            }
        }

        /// <summary>彻底退出：移除除初始频道外的所有频道 → 停后台 → 退出应用。</summary>
        private void ConfirmHardExit()
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("彻底退出"))
                .SetMessage(L10n.T("将移除除初始频道外的所有频道，并停止后台服务、退出应用。确定吗？"))
                .SetPositiveButton(L10n.T("退出"), (s, e) =>
                {
                    int removed = 0;
                    try { removed = bridge?.RemoveNonDefaultChannels() ?? 0; } catch { }
                    try { CoreHost.Shutdown(); } catch { }
                    try { ChatForegroundService.Stop(this); } catch { }
                    L10n.Toast(this, L10n.T("已移除 ") + removed + L10n.T(" 个频道，正在退出"), ToastLength.Short).Show();
                    try { FinishAffinity(); } catch { }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>切换传输渠道：仅 qaul（走中继、不落局域网）或双渠道。</summary>
        private void ToggleQaulOnly()
        {
            bool dual = bridge == null || bridge.DualChannel;
            L10n.Dialog(this)
                .SetTitle(L10n.T("传输渠道"))
                .SetMessage(L10n.T("当前：") + (dual ? L10n.T("qaul + 局域网直连（双渠道）") : L10n.T("仅 qaul 渠道")) +
                            L10n.T("\n\n「仅 qaul」时所有消息只经 qaul 网络（可走公共节点中继）传输，不再直接广播到局域网。"))
                .SetPositiveButton(L10n.T("仅走 qaul"), (s, e) =>
                {
                    try { bridge.SetDualChannel(false); } catch { }
                    L10n.Toast(this, L10n.T("已切换为仅 qaul 渠道"), ToastLength.Short).Show();
                    UpdateHeader();
                })
                .SetNegativeButton(L10n.T("双渠道"), (s, e) =>
                {
                    try { bridge.SetDualChannel(true); } catch { }
                    L10n.Toast(this, L10n.T("已切换为双渠道"), ToastLength.Short).Show();
                    UpdateHeader();
                })
                .SetNeutralButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /* ===================== 短信转发 ===================== */

        /// <summary>
        /// 短信转发状态与自检：一眼看清「权限有没有给 / 广播和收件箱哪条路在工作 /
        /// 有多少条暂存没发出去」，并可当场发一条测试短信验证整条链路。
        /// </summary>
        private void ShowSmsForwardDialog()
        {
            bool recv = CheckSelfPermission("android.permission.RECEIVE_SMS") == Permission.Granted;
            bool read = SmsInboxWatcher.HasReadSms(this);
            bool watch = SmsInboxWatcher.Running;
            int pending = SmsForwarder.PendingCount(this);

            var msg =
                L10n.T("短信 → AhoChat 频道 转发\n\n")
                + L10n.T("· 短信广播权限：") + (recv ? L10n.T("已授权") : L10n.T("未授权")) + "\n"
                + L10n.T("· 收件箱监听（兜底）：") + (watch ? L10n.T("运行中") : (read ? L10n.T("未启动") : L10n.T("缺 READ_SMS"))) + "\n"
                + L10n.T("· 待补发：") + pending + L10n.T(" 条\n")
                + L10n.T("· 最近状态：") + SmsForwarder.LastStatus + "\n"
                + (string.IsNullOrEmpty(SmsForwarder.LastPreview) ? "" : L10n.T("· 最近短信：") + SmsForwarder.LastPreview + "\n")
                + L10n.T("\n转发以频道消息形式发出，需要本机在线（App 进程存活）。\n")
                + L10n.T("短信到达时 App 未运行会被暂存，下次启动自动补发。");

            var builder = L10n.Dialog(this)
                .SetTitle(L10n.T("短信转发"))
                .SetMessage(msg)
                .SetPositiveButton(L10n.T("发送测试短信"), (s, e) =>
                {
                    SmsForwarder.Handle(this, "10086",
                        L10n.T("【AhoChat 测试】这是一条用于验证短信转发链路的测试短信。"), "test");
                    L10n.Toast(this, L10n.T("已投递测试短信，请查看当前频道"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { });

            if (!read || !recv)
            {
                builder.SetNeutralButton(L10n.T("去授权"), (s, e) =>
                {
                    try
                    {
                        RequestPermissions(new[]
                        {
                            "android.permission.RECEIVE_SMS",
                            "android.permission.READ_SMS"
                        }, 7302);
                    }
                    catch { }
                });
            }
            else
            {
                builder.SetNeutralButton(L10n.T("清空暂存"), (s, e) =>
                {
                    SmsForwarder.ClearPending(this);
                    L10n.Toast(this, L10n.T("暂存短信已清空"), ToastLength.Short).Show();
                });
            }
            builder.Show();
        }

        /// <summary>
        /// 输入框「＋」：从底部升起的宫格面板。
        /// 比一列文字更容易扫，常用动作（通话 / 共享）一眼就能看到。
        /// </summary>
        private void ShowPlusPanel()
        {
            var dlg = new Dialog(this);
            dlg.RequestWindowFeature((int)WindowFeatures.NoTitle);

            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.Background = RoundBg(0xFFF7F8FA, 16);
            panel.SetPadding(Dp(10), Dp(12), Dp(10), Dp(10));

            var grid = new LinearLayout(this) { Orientation = Orientation.Vertical };
            grid.Background = RoundBg(CardBg, 12);
            grid.SetPadding(0, Dp(6), 0, Dp(6));
            panel.AddView(grid);

            // 第一行：内容
            var r1 = PlusRow(grid);
            PlusItem(r1, IconKind.Image, L10n.T("图片/视频"), () => { dlg.Dismiss(); PickFile(true); });
            PlusItem(r1, IconKind.File, L10n.T("文件"), () => { dlg.Dismiss(); PickFile(false); });
            PlusItem(r1, IconKind.Link, L10n.T("链接"), () => { dlg.Dismiss(); ShowSendUrlDialog(); });
            PlusItem(r1, IconKind.Group, L10n.T("频道"), () => { dlg.Dismiss(); ShowJoinChannelDialog(); });

            grid.AddView(BuildDivider());

            // 第二行：通话与画面
            var r2 = PlusRow(grid);
            PlusItem(r2, IconKind.Mic, L10n.T("语音通话"), () => { dlg.Dismiss(); StartCallFlow(false); });
            PlusItem(r2, IconKind.Video, L10n.T("视频通话"), () => { dlg.Dismiss(); StartCallFlow(true); });
            PlusItem(r2, IconKind.Camera, L10n.T("共享摄像头"), () => { dlg.Dismiss(); OpenIpCamera(false); ArmShareCardWatch(); });
            PlusItem(r2, IconKind.Screen, L10n.T("共享屏幕"), () => { dlg.Dismiss(); OpenIpCamera(true); ArmShareCardWatch(); });

            grid.AddView(BuildDivider());

            // 第三行：工具
            var r3 = PlusRow(grid);
            PlusItem(r3, IconKind.Pen, L10n.T("屏幕画笔"), () => { dlg.Dismiss(); ToggleScreenDraw(); ArmShareCardWatch(); });
            PlusItem(r3, IconKind.Pin, L10n.T("共享位置"), () =>
            {
                dlg.Dismiss();
                StartActivity(new Intent(this, typeof(GpsRadarActivity)));
                ArmShareCardWatch();
            });
            PlusItem(r3, IconKind.Folder, L10n.T("文件共享"), () => { dlg.Dismiss(); ShowFileShareDialog(); });
            PlusItem(r3, IconKind.Trash, L10n.T("清空会话"), () => { dlg.Dismiss(); ClearConv(currentConv); });

            var cancel = new TextView(this) { Text = L10n.T("取消"), Gravity = GravityFlags.Center, TextSize = 16 };
            cancel.SetTextColor(new Color(TextMain));
            cancel.Background = RoundBg(CardBg, 12);
            var clp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(50));
            clp.TopMargin = Dp(8);
            panel.AddView(cancel, clp);
            cancel.Click += (s, e) => dlg.Dismiss();

            dlg.SetContentView(panel);
            var win = dlg.Window;
            if (win != null)
            {
                win.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Transparent));
                win.SetGravity(GravityFlags.Bottom);
                win.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                win.SetDimAmount(0.35f);
                win.AddFlags(WindowManagerFlags.DimBehind);
            }
            dlg.Show();
        }

        private LinearLayout PlusRow(LinearLayout parent)
        {
            var r = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            parent.AddView(r, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            return r;
        }

        private void PlusItem(LinearLayout row, IconKind kind, string label, Action act)
        {
            var cell = new LinearLayout(this) { Orientation = Orientation.Vertical };
            cell.SetGravity(GravityFlags.Center);
            cell.SetPadding(0, Dp(12), 0, Dp(12));
            cell.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var box = new FrameLayout(this);
            box.Background = RoundBg(0xFFF1F3F6, 12);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(46), Dp(46));
            var ic = new IconView(this, kind) { IconColor = unchecked((int)0xFF3C4043) };
            box.AddView(ic, new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
            cell.AddView(box);

            var t = new TextView(this)
            {
                Text = label,
                TextSize = 12,
                Gravity = GravityFlags.Center
            };
            t.SetTextColor(new Color(unchecked((int)0xFF3C4043)));
            t.SetPadding(0, Dp(7), 0, 0);
            cell.AddView(t, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            cell.Click += (s, e) => act?.Invoke();
            row.AddView(cell);
        }

        /* ===================== 设备 / 服务 / 我的 三个 Tab ===================== */

        /// <summary>
        /// 「设备」页「本机」下方的 6 位动态口令：浏览器访问本机 AhoChatA 功能时要输的就是它。
        /// 开关在「我的 → 隐私与安全」；关着的时候这里只给一行引导，不显示数字。
        /// 每台设备各有一把密钥，所以 A 机的口令开不了 B 机的网页。
        /// </summary>
        private void AddOtpRow(LinearLayout card)
        {
            card.AddView(BuildDivider());

            if (!OtpAuth.Enabled)
            {
                otpCodeTxt = null;
                otpMetaTxt = null;
                otpTicking = false;
                card.AddView(BuildActionRow(IconKind.Check, L10n.T("网页访问双因素认证：未开启"),
                    L10n.T("在「我的 → 隐私与安全」里打开后，这里显示 6 位口令"),
                    () => StartActivity(new Intent(this, typeof(PrivacyActivity)))));
                return;
            }

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var lab = new TextView(this) { Text = L10n.T("网页访问动态口令"), TextSize = 12 };
            lab.SetTextColor(new Color(TextSub));
            col.AddView(lab);

            var code = new TextView(this) { Text = OtpAuth.CurrentCode, TextSize = 26 };
            code.SetTextColor(new Color(unchecked((int)0xFF111111)));
            code.SetTypeface(Android.Graphics.Typeface.Monospace, TypefaceStyle.Bold);
            code.SetPadding(0, Dp(2), 0, 0);
            col.AddView(code);

            var meta = new TextView(this) { Text = OtpAuth.SecondsLeft + L10n.T(" 秒后刷新 · 点击复制"), TextSize = 11 };
            meta.SetTextColor(new Color(TextSub));
            meta.SetPadding(0, Dp(2), 0, 0);
            col.AddView(meta);

            row.AddView(col);

            var shield = new IconView(this, IconKind.Check) { IconColor = IosStyle.Accent, Thickness = 1.7f };
            row.AddView(shield, new LinearLayout.LayoutParams(Dp(22), Dp(22)));

            row.Click += (s, e) =>
            {
                try
                {
                    var cb = (ClipboardManager)GetSystemService(ClipboardService);
                    cb.PrimaryClip = ClipData.NewPlainText("otp", OtpAuth.CurrentCode);
                    L10n.Toast(this, L10n.T("口令已复制：") + OtpAuth.CurrentCode, ToastLength.Short).Show();
                }
                catch { }
            };

            card.AddView(row);

            otpCodeTxt = code;
            otpMetaTxt = meta;
            StartOtpTicker();
        }

        /// <summary>每秒刷一次口令（只在这一页开着时跑，离开「设备」页自动停）。</summary>
        private void StartOtpTicker()
        {
            if (otpTicking) return;
            otpTicking = true;
            if (otpTimer == null) otpTimer = new Handler(Looper.MainLooper);
            otpTimer.PostDelayed(new Java.Lang.Runnable(TickOtp), 1000);
        }

        private void TickOtp()
        {
            if (!otpTicking) return;
            if (inChat || currentTab != 1) { otpTicking = false; return; }

            try
            {
                if (otpCodeTxt != null) otpCodeTxt.Text = OtpAuth.CurrentCode;
                if (otpMetaTxt != null) otpMetaTxt.Text = OtpAuth.SecondsLeft + L10n.T(" 秒后刷新 · 点击复制");
                // 网络变化导致本机 IP 改变时，设备页卡片同步刷新（1 秒粒度）
                if (_devCardIp != null)
                {
                    var s = DeviceStatus.Ip + L10n.T(" · 端口 ") + WebServer.ExternalPortActual;
                    if (!string.Equals(_devCardIp.Text, s)) _devCardIp.Text = s;
                }
            }
            catch { }

            if (otpTicking && otpTimer != null)
                otpTimer.PostDelayed(new Java.Lang.Runnable(TickOtp), 1000);
        }

        /// <summary>在线设备的状态指纹：变化即需要重建这一屏。</summary>
        private static string DevicesSignature(List<Dictionary<string, string>> peers)
        {
            if (peers == null || peers.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                string id = p.ContainsKey("id") ? p["id"] : "";
                string ip = p.ContainsKey("ip") ? p["ip"] : "";
                string rd = p.ContainsKey("rd") ? p["rd"] : "";
                string dc = p.ContainsKey("dc") ? p["dc"] : "";
                string ws = p.ContainsKey("ws") ? p["ws"] : "";
                sb.Append(id).Append('|').Append(ip).Append('|').Append(rd).Append('|').Append(dc)
                  .Append('|').Append(ws).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>画板设备指纹：IP / 主机名 / Web 开关 / Web 端口变化即重建列表。</summary>
        private string DrawboardSignature()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                foreach (var d in _drawDisc.OnlineDevices())
                {
                    sb.Append(d.Ip).Append(':').Append(d.WebRunning ? '1' : '0')
                      .Append(':').Append(d.WebPort).Append(';');
                }
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>停在「设备」页时每 2.5 秒比一次指纹，变了才重建（不重建就不动 OTP 与滚动位置）。</summary>
        private void StartDeviceWatch()
        {
            StopDeviceWatch();
            try
            {
                if (_devTick == null) _devTick = new Handler(Looper.MainLooper);
                Action poll = null;
                poll = delegate
                {
                    try
                    {
                        if (IsFinishing) return;
                        if (currentTab == 1)
                        {
                            // 画板设备：每 2.5s 补一帧 PING（保活 + 发现后启动的电脑），并做超时清理
                            long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                            if (_drawDisc != null && now - _drawLastScan >= 2400)
                            {
                                try { _drawDisc.ScanNow(); } catch { }
                                _drawLastScan = now;
                            }
                            List<Dictionary<string, string>> peers = null;
                            try { peers = bridge.OnlinePeers(); } catch { }
                            string sig = DevicesSignature(peers) + "#D" + DrawboardSignature();
                            if (sig != _devicesSig) { BuildDevicesTab(); ReapplyTheme(); }
                        }
                    }
                    catch { }
                    try { _devTick.PostDelayed(new Java.Lang.Runnable(poll), 2500); } catch { }
                };
                _devTick.PostDelayed(new Java.Lang.Runnable(poll), 2500);
            }
            catch { }
        }

        private void StopDeviceWatch()
        {
            try { if (_devTick != null) _devTick.RemoveCallbacksAndMessages(null); } catch { }
        }

        /// <summary>「设备」：本机信息 + 附近在线设备（可直接聊天/通话）。</summary>
        private void BuildDevicesTab()
        {
            if (devicesBox == null) return;
            devicesBox.RemoveAllViews();

            /* ---------- 本机 ---------- */
            var selfCard = NewGroup(devicesBox, L10n.T("本机"));
            var self = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            self.SetGravity(GravityFlags.CenterVertical);
            self.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            self.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            self.AddView(RoundAvatar(bridge.SelfName, 48, L10n.T("我")));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(6), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var nm = new TextView(this)
            {
                Text = string.IsNullOrEmpty(bridge.SelfName) ? L10n.T("本机") : bridge.SelfName,
                TextSize = IosStyle.BodySize
            };
            nm.SetTextColor(new Color(TextMain));
            col.AddView(nm);

            var ip = new TextView(this)
            {
                Text = DeviceStatus.Ip + L10n.T(" · 端口 ") + WebServer.ExternalPortActual,
                TextSize = IosStyle.CaptionSize
            };
            ip.SetTextColor(new Color(TextSub));
            ip.SetPadding(0, Dp(2), 0, 0);
            ip.Click += (s, e) => CopyText(DeviceStatus.Ip);   // 点击复制本机 IP
            ip.SetMinimumHeight(Dp(28));
            ip.Gravity = GravityFlags.CenterVertical;
            _devCardIp = ip;
            col.AddView(ip);
            self.AddView(col);

            var chev = new IconView(this, IconKind.ChevronRight) { IconColor = unchecked((int)0xFFC7C9CE), Thickness = 1.5f };
            self.AddView(chev, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

            self.Click += (s, e) => ShowRenameDialog();
            selfCard.AddView(self);

            /* ---------- 本机动态口令（2FA，浏览器访问 AhoChatA 用） ---------- */
            try { AddOtpRow(selfCard); } catch { }

            /* ---------- 远程桌面：控制局域网里的 Windows 电脑（Mchat 电脑端被控） ---------- */
            try
            {
                var rdCard = NewGroup(devicesBox, L10n.T("远程桌面"));
                rdCard.AddView(BuildActionRow(IconKind.Screen, L10n.T("远程控制电脑"),
                    L10n.T("连接局域网里运行 AhoChat 的 Windows 电脑（设备码 + 连接密码）"),
                    () => StartActivity(new Intent(this, typeof(RemoteDeskActivity)))));
                // 被控方向：让电脑/其它手机来控本机（需要无障碍服务 + 屏幕录制授权）
                rdCard.AddView(BuildActionRow(IconKind.Screen, L10n.T("允许本机被远控"),
                    L10n.T("开启后电脑可在「设备」页直接控制这台手机（无障碍 + 屏幕录制）"),
                    () => StartActivity(new Intent(this, typeof(RemoteHostActivity)))));
            }
            catch { }

            /* ---------- 信号探测：分开扫描 WiFi / 蓝牙信号 ---------- */
            try
            {
                var probeCard = NewGroup(devicesBox, L10n.T("信号探测"));
                probeCard.AddView(BuildActionRow(IconKind.Radar, L10n.T("WiFi / 蓝牙信号探测"),
                    L10n.T("扫描周边 WiFi 信号·信道·加密与蓝牙设备信号强度"),
                    () => StartActivity(new Intent(this, typeof(SignalProbeActivity)))));
            }
            catch { }

            /* ---------- 附近设备 ---------- */
            List<Dictionary<string, string>> peers = null;
            try { peers = bridge.OnlinePeers(); } catch { }
            // 记下这一屏"线上有哪些设备、各自什么状态"，定时器据此判断要不要重建 ——
            // 以前只在切标签时重建，设备后到/后开被控就永远显示旧状态（典型现象：
            // 顶部写"1 台在线"，下面却还是"暂时没有发现其它设备"）。
            _devicesSig = DevicesSignature(peers) + "#D" + DrawboardSignature();

            var peerCard = NewGroup(devicesBox, L10n.T("附近设备"));

            if (peers == null || peers.Count == 0)
            {
                var empty = new LinearLayout(this) { Orientation = Orientation.Vertical };
                empty.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));

                var t1 = new TextView(this) { Text = L10n.T("暂时没有发现其它设备"), TextSize = 14 };
                t1.SetTextColor(new Color(unchecked((int)0xFF3C4043)));
                t1.Gravity = GravityFlags.CenterHorizontal;
                empty.AddView(t1);

                var t2 = new TextView(this)
                {
                    Text = L10n.T("让另一台设备也打开 AhoChat，连同一个 Wi-Fi，\n两边就会自动出现在对方的列表里。"),
                    TextSize = IosStyle.CaptionSize
                };
                t2.SetTextColor(new Color(TextSub));
                t2.Gravity = GravityFlags.CenterHorizontal;
                t2.SetPadding(0, Dp(7), 0, 0);
                t2.SetLineSpacing(0, 1.3f);
                empty.AddView(t2);
                peerCard.AddView(empty);

                peerCard.AddView(BuildDivider());
                peerCard.AddView(BuildActionRow(IconKind.Radar, L10n.T("手动输入 IP 直连"),
                    L10n.T("自动发现被路由器隔离时用"), ShowManualConnect));
            }
            else
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    if (i > 0) peerCard.AddView(BuildDivider());
                    peerCard.AddView(BuildPeerRow(peers[i]));
                }
            }

            /* ---------- 画板设备（inkcanvas / painter，UDP 21118）---------- */
            try { BuildDrawboardSection(); } catch { }

            var tip = new TextView(this) { Text = L10n.T("右侧按钮直接远控或发消息；点名字那一行展开语音/视频通话。") };
            tip.TextSize = IosStyle.CaptionSize;
            tip.SetTextColor(new Color(TextSub));
            tip.SetPadding(Dp(26), Dp(10), Dp(26), Dp(4));
            devicesBox.AddView(tip);
        }

        private void OnDrawDiscChanged()
        {
            try
            {
                RunOnUiThread(() =>
                {
                    if (IsFinishing || currentTab != 1 || inChat) return;
                    BuildDevicesTab();
                    ReapplyTheme();
                });
            }
            catch { }
        }

        /// <summary>
        /// 「画板设备」分组：扫描局域网内运行中的画板程序（inkcanvas/painter）。
        /// 每台设备给三个操作：打开网页(9090)、开/关 Web、刷新状态。
        /// Web 关着也能扫到并远程打开；打开后用内置浏览器访问其 Web 控制台。
        /// </summary>
        private void BuildDrawboardSection()
        {
            var card = NewGroup(devicesBox, L10n.T("画板设备"));

            List<DrawboardDiscovery.Device> list = null;
            try { list = _drawDisc.OnlineDevices(); } catch { }

            if (list == null || list.Count == 0)
            {
                var empty = new TextView(this)
                {
                    Text = L10n.T("未发现画板设备 · 点此重新扫描\n（电脑上运行画板程序后即可出现）"),
                    TextSize = IosStyle.CaptionSize
                };
                empty.SetTextColor(new Color(TextSub));
                empty.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14));
                empty.Gravity = GravityFlags.CenterHorizontal;
                empty.Click += (s, e) =>
                {
                    try { _drawDisc.ScanNow(); } catch { }
                    L10n.Toast(this, L10n.T("已发送扫描广播，请稍候…"), ToastLength.Short).Show();
                };
                card.AddView(empty);
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) card.AddView(BuildDivider());
                card.AddView(BuildDrawboardRow(list[i]));
            }

            var hint = new TextView(this) { Text = L10n.T("点「开 Web」后，用「打开网页」进入控制台控制画板"), TextSize = IosStyle.CaptionSize };
            hint.SetTextColor(new Color(TextSub));
            hint.SetPadding(Dp(16), Dp(8), Dp(16), Dp(12));
            card.AddView(hint);
        }

        private View BuildDrawboardRow(DrawboardDiscovery.Device d)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);

            var ic = new IconView(this, IconKind.Pen) { IconColor = Accent };
            row.AddView(ic, new LinearLayout.LayoutParams(Dp(22), Dp(22)));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(10), 0, Dp(6), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            string title = string.IsNullOrEmpty(d.HostName) ? L10n.T("画板设备") : d.HostName;
            var nm = new TextView(this) { Text = title, TextSize = 15 };
            nm.SetTextColor(new Color(TextMain));
            col.AddView(nm);

            string webState = d.WebRunning
                ? (L10n.T("Web 已开 · :") + d.WebPort + (d.LoopbackOnly ? L10n.T("（仅本机）") : ""))
                : (L10n.T("Web 已关 · :") + d.WebPort);
            var sub = new TextView(this) { Text = d.Ip + " · " + webState, TextSize = IosStyle.CaptionSize };
            sub.SetTextColor(new Color(d.WebRunning ? Accent : TextSub));
            sub.SetPadding(0, Dp(3), 0, 0);
            col.AddView(sub);
            row.AddView(col);
            box.AddView(row);

            // 操作行：打开网页 / 开或关 Web / 刷新
            var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            actions.SetPadding(Dp(32), Dp(8), 0, 0);

            var openBtn = PillButton(L10n.T("打开网页"), 0xFFEAF6FE, Accent);
            openBtn.Click += (s, e) =>
            {
                string url = "http://" + d.Ip + ":" + d.WebPort + "/";
                try { BrowserActivity.Open(this, url, L10n.T("画板设备")); }
                catch { L10n.Toast(this, L10n.T("打开失败：") + url, ToastLength.Short).Show(); }
            };
            actions.AddView(openBtn);

            var toggleBtn = PillButton(d.WebRunning ? L10n.T("关闭 Web") : L10n.T("开启 Web"), 0xFFF2F3F5, TextMain);
            toggleBtn.Click += (s, e) =>
            {
                string action = d.WebRunning ? "web-off" : "web-on";
                bool ok = false;
                try { ok = _drawDisc.SendCommand(d.Ip, action); } catch { }
                L10n.Toast(this, ok
                    ? L10n.T("已发送「") + (d.WebRunning ? L10n.T("关闭") : L10n.T("开启")) + L10n.T(" Web」指令，稍后自动刷新")
                    : L10n.T("发送失败，请确认在同一 Wi-Fi"), ToastLength.Short).Show();
            };
            var tlp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            tlp.SetMargins(Dp(8), 0, 0, 0);
            actions.AddView(toggleBtn, tlp);

            var refreshBtn = PillButton(L10n.T("刷新"), 0xFFF2F3F5, TextSub);
            refreshBtn.Click += (s, e) =>
            {
                try { _drawDisc.SendCommand(d.Ip, "status"); _drawDisc.ScanNow(); } catch { }
                L10n.Toast(this, L10n.T("正在查询状态…"), ToastLength.Short).Show();
            };
            var rlp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            rlp.SetMargins(Dp(8), 0, 0, 0);
            actions.AddView(refreshBtn, rlp);

            box.AddView(actions);
            return box;
        }

        /// <summary>
        /// 附近设备的一行：
        ///   第一行 [头像] 名字（单行省略） / IP·设备码
        ///   第二行 [远控][网页远控][投屏][消息]   ← 按钮独立成行，不再和名字抢宽度
        ///   第三行 [语音通话][视频通话]（点名字展开，默认收起）
        ///
        /// 以前所有按钮都塞在第一行右侧：MchatW 同时有远控/网页远控/投屏/消息四个按钮，
        /// 把名字挤成一字一行，整个 item 老高。现在名字独占整行宽度，按钮换行放置。
        /// </summary>
        private View BuildPeerRow(Dictionary<string, string> p)
        {
            string name = p.ContainsKey("name") ? p["name"] : (p.ContainsKey("id") ? p["id"] : "?");
            string ipAddr = p.ContainsKey("ip") ? p["ip"] : "";
            string id = p.ContainsKey("id") ? p["id"] : null;
            int rdPort = 0;
            if (p.ContainsKey("rd")) int.TryParse(p["rd"], out rdPort);
            string code = p.ContainsKey("dc") ? p["dc"] : "";
            string webRemote = p.ContainsKey("ws") ? p["ws"] : "";

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };

            // ---- 第一行：头像 + 可随字体缩放换行的名字/地址 ----
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(4));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            row.AddView(RoundAvatar(name, 42));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(6), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var nm = new TextView(this) { Text = name, TextSize = IosStyle.BodySize };
            nm.SetTextColor(new Color(TextMain));
            col.AddView(nm);

            bool isPc = !string.IsNullOrEmpty(id) && id.StartsWith("W", StringComparison.OrdinalIgnoreCase);
            string sub = ipAddr;
            if (!string.IsNullOrEmpty(code))
                sub += " · " + DeskShare.RdDeviceCode.Format(code);
            // Windows 设备不展示"未通告端口"这类让人犹豫的状态——点卡片就直连远控
            if (rdPort <= 0 && !isPc) sub += L10n.T(" · 未开启被控");
            var ip = new TextView(this) { Text = sub, TextSize = IosStyle.CaptionSize };
            ip.SetTextColor(new Color(TextSub));
            ip.SetPadding(0, Dp(3), 0, 0);
            ip.SetSingleLine(true);
            ip.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            col.AddView(ip);
            row.AddView(col);
            box.AddView(row);

            // ---- 第二行：常驻按钮独立成行、等宽平分，绝不挤压名字或互相挤变形 ----
            var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            actions.SetGravity(GravityFlags.CenterVertical);
            actions.SetPadding(Dp(IosStyle.CardInset), 0, Dp(IosStyle.CardInset), Dp(10));

            // 统一加入等宽按钮（weight=1），无论几个按钮都平分这一行宽度
            Action<string, long, int, EventHandler> addBtn = (text, bg, fg, click) =>
            {
                var b = PillButton(text, bg, fg);
                b.Gravity = GravityFlags.Center;
                var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                lp.SetMargins(Dp(3), 0, Dp(3), 0);
                b.Click += click;
                actions.AddView(b, lp);
            };

            bool rdReady = rdPort > 0 || isPc;   // PC 没通告端口也允许试（可能由系统服务托管）
            addBtn(L10n.T("远控"), rdReady ? 0xFFEAF6FE : 0xFFF2F3F5, rdReady ? Accent : TextSub,
                (s, e) => OpenRemoteFor(ipAddr, rdPort, code, name, isPc));

            // 网页远控：TV / TVCMS 这类"没有桌面被控端、只有网页控制台"的设备才显示。
            // Windows 端（MchatW）走专用加密远控协议，网页控制台按钮会让人误用，这里不再显示。
            if (!isPc && !string.IsNullOrEmpty(webRemote))
            {
                addBtn(L10n.T("网页远控"), 0xFFEAF6FE, Accent, (s, e) =>
                {
                    try
                    {
                        var uri = Android.Net.Uri.Parse(webRemote);
                        StartActivity(new Intent(Intent.ActionView, uri));
                    }
                    catch (System.Exception ex)
                    {
                        L10n.Toast(this, L10n.T("打开网页远控失败：") + ex.Message, ToastLength.Short).Show();
                    }
                });
            }

            // 投屏：把本机画面投到那台电脑上（只对电脑端显示）
            if (isPc && !string.IsNullOrEmpty(ipAddr))
                addBtn(L10n.T("投屏"), 0xFFEAF6FE, Accent, (s, e) => StartCastToPc(ipAddr, name));

            addBtn(L10n.T("消息"), 0xFFEAF6FE, Accent, (s, e) =>
            {
                if (string.IsNullOrEmpty(id)) return;
                var conv = bridge.StartDm(id, name);
                if (!string.IsNullOrEmpty(conv)) OpenChatConv(conv);
            });
            box.AddView(actions);

            // ---- 第三行：通话展开区（默认收起）----
            var callPanel = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            callPanel.Visibility = ViewStates.Gone;
            callPanel.SetPadding(Dp(70), 0, Dp(14), Dp(10));

            var voiceBtn = PillButton(L10n.T("语音通话"), 0xFFEAF6FE, Accent);
            voiceBtn.Click += (s, e) => StartCallTo(id, name, false);
            callPanel.AddView(voiceBtn, Weighted());

            var videoBtn = PillButton(L10n.T("视频通话"), 0xFFEAF6FE, Accent);
            videoBtn.Click += (s, e) => StartCallTo(id, name, true);
            var vlp = Weighted();
            vlp.SetMargins(Dp(8), 0, 0, 0);
            callPanel.AddView(videoBtn, vlp);

            // Windows 设备：点卡片（头像 + 名字 + 地址这一块）直接发起远控，
            // 不再展开通话面板——PC 端用户要的就是一键远控，没有"是否在线/端口/加密"的判断。
            // 其它设备保持原样：点头像名字展开语音/视频通话。
            if (isPc)
                AvatarAndText(row, (s, e) => OpenRemoteFor(ipAddr, rdPort, code, name, true));
            else
            {
                EventHandler toggle = (s, e) =>
                {
                    bool show = callPanel.Visibility != ViewStates.Visible;
                    callPanel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
                };
                AvatarAndText(row, toggle);
                box.AddView(callPanel);
            }
            return box;
        }

        /// <summary>把点击只挂在左侧"头像 + 文字"上，右侧按钮保持独立。</summary>
        private void AvatarAndText(LinearLayout row, EventHandler onClick)
        {
            row.Click += onClick;
        }

        private Button PillButton(string text, long bg, int fg)
        {
            var b = new Button(this) { Text = text, TextSize = IosStyle.CaptionSize };
            b.SetTextColor(new Color(fg));
            b.SetBackgroundColor(Color.Transparent);
            b.Background = RoundBg(bg, IosStyle.ControlRadius);
            b.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            b.SetMinimumWidth(Dp(IosStyle.ControlHeight));
            b.SetAllCaps(false);
            b.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
            b.LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            return b;
        }

        private LinearLayout.LayoutParams Weighted()
        {
            return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        }

        /// <summary>从设备卡片一键远控 PC：IP / 端口 / 设备码全部自动带过去，用户无需判断
        /// 设备是否在线、是否通告端口、是否接受加密——这些全交给远控页自动处理。
        /// 非 PC 设备（未通告被控端口）仍给出明确提示，避免误点。</summary>
        private void OpenRemoteFor(string ip, int rdPort, string code, string name, bool isPc)
        {
            if (!isPc && rdPort <= 0)
            {
                L10n.Toast(this,
                    L10n.T("对方还没开启被远控：请在手机上打开「允许本机被远控」"),
                    ToastLength.Long).Show();
                return;
            }
            // PC 无条件直连：hello 没带 rd 就用默认端口，不拦、不提示、不让用户做判断。
            if (isPc && rdPort <= 0) rdPort = DeskShare.RdWire.DefaultRemotePort;

            var i = new Intent(this, typeof(RemoteDeskActivity));
            i.PutExtra("ip", ip ?? "");
            i.PutExtra("port", rdPort);
            i.PutExtra("code", code ?? "");
            i.PutExtra("name", name ?? "");
            i.PutExtra("auto", true);
            // 设备是从局域网发现包里来的 → 直连免设备码/密码，点一下就进画面
            i.PutExtra("lanDirect", true);
            StartActivity(i);
        }

        private void ShowDeviceActions(string peerId, string name)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            var items = new[] { L10n.T("发消息"), L10n.T("语音通话"), L10n.T("视频通话") };
            L10n.Dialog(this)
                .SetTitle(name)
                .SetItems(items, (s, e) =>
                {
                    switch (e.Which)
                    {
                        case 0:
                            var conv = bridge.StartDm(peerId, name);
                            if (!string.IsNullOrEmpty(conv)) OpenChatConv(conv);
                            break;
                        case 1: StartCallTo(peerId, name, false); break;
                        case 2: StartCallTo(peerId, name, true); break;
                    }
                })
                .Show();
        }

        /* ---------------- 服务 ---------------- */

        /// <summary>
        /// 本机开启共享时，往 mesh 频道广播一张「共享卡片」（同一路共享只发一次）。
        /// 屏幕画笔额外开启 App 专属加速通道（UDP）——它的帧源可以直接抓取，
        /// 于是同为 App 的观看端可以走这条更快的通道，收不到再回退 HTTP。
        /// </summary>
        private bool MaybeBroadcastShareCard(bool force = false)
        {
            if (bridge == null) return false;
            bool posted = false;
            // 卡片发到「当前对话」：用户在哪个频道里点的共享，卡片就落在那儿
            string target = string.IsNullOrEmpty(currentConv) || currentConv == Convs.WebConv ? null : currentConv;

            var cam = IpCameraServer.Instance;
            bool camOn = cam != null && cam.IsRunning;
            bool drawOn = ScreenDrawService.IsRunning;
            bool gpsOn = false;
            try { gpsOn = bridge != null && bridge.IsLocationSharing; } catch { }

            if (camOn && (!_camCardSent || force))
            {
                _camCardSent = true;
                string type = cam.Source == ShareSource.Screen ? "screen" : "camera";
                bridge.SendShareCard(type,
                    type == "screen" ? "屏幕共享" : "摄像头共享",
                    cam.LocalUrl, ShareCast.Port, target);
                posted = true;
            }
            if (!camOn) _camCardSent = false;

            if (drawOn && (!_drawCardSent || force))
            {
                _drawCardSent = true;
                ShareCastServer.Start();
                bridge.SendShareCard("draw", L10n.T("屏幕画笔"), WebServer.UrlOf("/draw"), ShareCast.Port, target);
                posted = true;
            }
            if (!drawOn)
            {
                if (_drawCardSent) ShareCastServer.Stop();
                _drawCardSent = false;
            }

            // 位置共享：发一张 location 卡片到当前频道，点卡片可打开 GPS 雷达页一起看
            if (gpsOn && (!_gpsCardSent || force))
            {
                _gpsCardSent = true;
                bridge.SendShareCard("location", L10n.T("位置共享"), WebServer.UrlOf("/gps"), 0, target);
                posted = true;
            }
            if (!gpsOn) _gpsCardSent = false;
            return posted;
        }

        /// <summary>
        /// 点了「共享摄像头 / 共享屏幕 / 屏幕画笔」之后立刻把共享卡片发到当前对话。
        /// 共享服务是异步起来的（摄像头要等预览出帧、画笔要等悬浮窗权限回来），
        /// 所以这里短间隔重试几次，服务一就绪就发，不用等用户切回聊天页。
        /// </summary>
        private void ArmShareCardWatch()
        {
            try
            {
                var h = new Handler(Looper.MainLooper);
                int tries = 0;
                Action tick = null;
                tick = () =>
                {
                    try
                    {
                        if (IsFinishing || IsDestroyed) return;
                        if (MaybeBroadcastShareCard(true)) return;      // 发出去了就收工
                        if (++tries < 15) h.PostDelayed(() => tick(), 400);
                    }
                    catch { }
                };
                h.PostDelayed(() => tick(), 250);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "共享卡片定时投递失败: " + ex.Message);
            }
        }

        /// <summary>把一条 kind=share 的消息渲染成「共享卡片」。</summary>
        private View BuildShareCard(Dictionary<string, object> msg)
        {
            var type = Msg.Str(msg, "shareType");
            var url = Msg.Str(msg, "url");
            var title = Msg.Str(msg, "text");
            var from = Msg.Str(msg, "fromName");

            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.SetPadding(Dp(12), Dp(11), Dp(12), Dp(11));
            card.Background = RoundBg(CardBg, 12);
            var lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(Dp(10), Dp(6), Dp(10), Dp(6));
            card.LayoutParameters = lp;

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);

            var kind = type == "draw" ? IconKind.Pen
                     : (type == "screen" ? IconKind.Screen
                     : (type == "location" ? IconKind.Pin : IconKind.Camera));
            var box = new FrameLayout(this);
            box.Background = RoundBg(unchecked((int)0xFFE7F6FE), 11);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(40), Dp(40));
            var ic = new IconView(this, kind) { IconColor = Accent };
            box.AddView(ic, new FrameLayout.LayoutParams(Dp(23), Dp(23)) { Gravity = GravityFlags.Center });
            row.AddView(box);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(8), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            var t1 = new TextView(this) { Text = string.IsNullOrEmpty(title) ? L10n.T("共享") : title, TextSize = 15 };
            t1.SetTextColor(new Color(TextMain));
            var t2 = new TextView(this)
            {
                Text = (string.IsNullOrEmpty(from) ? "" : from + " · ") + L10n.T("点击在本 App 内全屏观看"),
                TextSize = 11
            };
            t2.SetTextColor(new Color(TextSub));
            col.AddView(t1);
            col.AddView(t2);
            row.AddView(col);

            card.AddView(row);
            // 位置共享卡片直接打开 GPS 雷达页（本机也顺便开启共享，双方即可互相看到），
            // 其余共享类型进 App 内全屏观看页。
            card.Click += (s, e) =>
            {
                if (type == "location")
                {
                    try { StartActivity(new Intent(this, typeof(GpsRadarActivity))); }
                    catch (System.Exception ex)
                    {
                        L10n.Toast(this, L10n.T("打开位置共享失败：") + ex.Message, ToastLength.Short).Show();
                    }
                    return;
                }
                OpenShareViewer(type, title, url);
            };
            return card;
        }

        /// <summary>打开 App 内全屏观看页（不是用浏览器）。</summary>
        private void OpenShareViewer(string type, string title, string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    L10n.Toast(this, L10n.T("这条共享没有可观看的地址"), ToastLength.Short).Show();
                    return;
                }
                var i = new Intent(this, typeof(ShareViewActivity));
                i.PutExtra("type", string.IsNullOrEmpty(type) ? "draw" : type);
                i.PutExtra("title", string.IsNullOrEmpty(title) ? L10n.T("共享") : title);
                i.PutExtra("url", url);
                i.PutExtra("host", HostOf(url));
                StartActivity(i);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("打开共享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private static string HostOf(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "";
                return new Uri(url).Host;
            }
            catch { return ""; }
        }

        private void BuildServicesTab()
        {
            if (servicesBox == null) return;
            servicesBox.RemoveAllViews();

            var cam = IpCameraServer.Instance;
            bool camOn = cam != null && cam.IsRunning;
            bool drawOn = ScreenDrawService.IsRunning;

            // 「媒体」分组：阅读 / 影音 / 下载 / 浏览器 四项平级，排在「共享」之前。
            var mediaCard = NewGroup(servicesBox, L10n.T("媒体"));
            mediaCard.AddView(ServiceRow(IconKind.Book, L10n.T("阅读"), L10n.T("本地书籍与在线书源，支持 TXT/EPUB"),
                true, OpenReader, L10n.T("打开")));
            mediaCard.AddView(BuildDivider());
            mediaCard.AddView(ServiceRow(IconKind.Media, L10n.T("影音"), L10n.T("本地音乐与视频，支持歌词、封面与歌单"),
                true, OpenMedia, L10n.T("打开")));
            mediaCard.AddView(BuildDivider());
            mediaCard.AddView(ServiceRow(IconKind.Download, L10n.T("下载"), L10n.T("音乐 / 视频 / 小说的下载管理，含进度与重试"),
                true, OpenDownload, L10n.T("打开")));
            mediaCard.AddView(BuildDivider());
            mediaCard.AddView(ServiceRow(IconKind.Globe, L10n.T("浏览器"), L10n.T("内置浏览器 · 广告拦截 · 油猴脚本 · 扫码"),
                true, OpenBrowser, L10n.T("打开")));
            mediaCard.AddView(BuildDivider());
            mediaCard.AddView(ServiceRow(IconKind.Camera, L10n.T("扫一扫"), L10n.T("扫描二维码 / 条码，网址直接用内置浏览器打开"),
                true, () => ScanActivity.Open(this), L10n.T("扫描")));

            var shareCard = NewGroup(servicesBox, L10n.T("共享"));
            // 四个共享入口启动时都会往"最近一次聊天的频道"发一张分享卡片（见 ArmShareCardWatch）。
            shareCard.AddView(ServiceRow(IconKind.Camera, L10n.T("摄像头共享"), L10n.T("把摄像头实时共享到局域网"),
                camOn && cam.Source == ShareSource.Camera, () => { OpenIpCamera(false); ArmShareCardWatch(); }));
            shareCard.AddView(BuildDivider());
            shareCard.AddView(ServiceRow(IconKind.Screen, L10n.T("屏幕共享"), L10n.T("把整个屏幕共享出去"),
                camOn && cam.Source == ShareSource.Screen, () => { OpenIpCamera(true); ArmShareCardWatch(); }));
            shareCard.AddView(BuildDivider());
            shareCard.AddView(ServiceRow(IconKind.Pen, L10n.T("屏幕画笔"), L10n.T("在屏幕上写画，其它设备可实时观看"),
                drawOn, () => { ToggleScreenDraw(); ArmShareCardWatch(); }));
            shareCard.AddView(BuildDivider());
            // 位置共享：打开 GPS 方位雷达图（页面一开就广播本机位置），并发一张位置分享卡片。
            shareCard.AddView(ServiceRow(IconKind.Pin, L10n.T("位置共享"), L10n.T("方位雷达图 · 显示设备方位与电量"),
                false, () => { StartActivity(new Intent(this, typeof(GpsRadarActivity))); ArmShareCardWatch(); }, L10n.T("雷达")));

            var netCard = NewGroup(servicesBox, L10n.T("文件与网页"));
            netCard.AddView(ServiceRow(IconKind.Folder, L10n.T("文件共享"), L10n.T("浏览 / 下载共享目录里的文件"),
                true, ShowFileShareDialog, L10n.T("打开")));
            netCard.AddView(BuildDivider());
            netCard.AddView(ServiceRow(IconKind.Grid, L10n.T("网页端 / 服务地址"), L10n.T("所有入口一览，可复制"),
                true, ShowServicesDialog, L10n.T("打开")));

            var note = new TextView(this)
            {
                Text = L10n.T("网页服务共用 ") + WebServer.ExternalPortActual + L10n.T(" 端口，只有路径不同；FTP 为独立端口。")
            };
            note.TextSize = IosStyle.CaptionSize;
            note.SetTextColor(new Color(TextSub));
            note.SetPadding(Dp(26), Dp(10), Dp(26), Dp(4));
            servicesBox.AddView(note);
        }

        /// <summary>打开原生阅读模块。</summary>
        private void OpenReader()
        {
            try
            {
                StartActivity(new Intent(this, typeof(ReaderActivity)));
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("无法打开阅读：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>打开原生影音（音乐 + 视频）模块。</summary>
        private void OpenMedia()
        {
            try
            {
                StartActivity(new Intent(this, typeof(MusicActivity)));
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("无法打开影音：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>打开原生下载管理器（音乐 / 视频 / 小说统一下载中心）。</summary>
        private void OpenDownload()
        {
            try
            {
                StartActivity(new Intent(this, typeof(DownloadActivity)));
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("无法打开下载：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>打开内置浏览器（广告拦截、资源嗅探、离线保存）。</summary>
        private void OpenBrowser()
        {
            try
            {
                BrowserActivity.OpenHome(this); // 默认打开原生起始页（搜索框 + 书签）
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("无法打开浏览器：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private View ServiceRow(IconKind kind, string title, string desc, bool on, Action act, string actionText = null)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var box = new FrameLayout(this);
            box.Background = RoundBg(on ? unchecked((int)0xFFE7F6FE) : unchecked((int)0xFFF1F3F6), 11);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
            var ic = new IconView(this, kind) { IconColor = on ? Accent : unchecked((int)0xFF6B6F76) };
            box.AddView(ic, new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
            row.AddView(box);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(8), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var t = new TextView(this) { Text = title, TextSize = IosStyle.BodySize };
            t.SetTextColor(new Color(TextMain));
            col.AddView(t);

            var d = new TextView(this) { Text = desc, TextSize = IosStyle.CaptionSize };
            d.SetTextColor(new Color(TextSub));
            d.SetPadding(0, Dp(3), 0, 0);
            col.AddView(d);
            row.AddView(col);

            var state = new TextView(this)
            {
                Text = actionText ?? (on ? L10n.T("已开启") : L10n.T("去开启")),
                TextSize = IosStyle.CaptionSize
            };
            int stateColor = actionText != null ? Accent : (on ? Accent : unchecked((int)0xFF9AA0A6));
            state.SetTextColor(new Color(stateColor));
            row.AddView(state);

            row.Click += (s, e) => act?.Invoke();
            return row;
        }

        /* ---------------- 我的 ---------------- */

        private void BuildMeTab()
        {
            if (meBox == null) return;
            meBox.RemoveAllViews();

            // 个人卡
            var headCard = NewGroup(meBox, null);
            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

            head.AddView(RoundAvatar(bridge.SelfName, 48));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(6), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var nm = new TextView(this)
            {
                Text = string.IsNullOrEmpty(bridge.SelfName) ? L10n.T("未命名设备") : bridge.SelfName,
                TextSize = 18
            };
            nm.SetTextColor(new Color(TextMain));
            col.AddView(nm);

            // 第一行：IPv4（保持原有排版）· 设备短 id
            _lastShownIp4 = DeviceStatus.Ip;
            _lastShownIp6 = DeviceStatus.Ipv6;
            var sub4 = new TextView(this)
            {
                Text = _lastShownIp4 + " · " + ShortId(bridge.SelfPeerId),
                TextSize = IosStyle.CaptionSize
            };
            sub4.SetTextColor(new Color(TextSub));
            sub4.SetPadding(0, Dp(2), 0, 0);
            sub4.Click += (s, e) => CopyText(_lastShownIp4);   // 点击复制 IPv4
            col.AddView(sub4);
            sub4.SetMinimumHeight(Dp(26));
            sub4.Gravity = GravityFlags.CenterVertical;
            _meIp4 = sub4;

            // 第二行：IPv6（网络变动时下方 StartMeIpWatch 会实时刷新）
            var sub6 = new TextView(this)
            {
                Text = "IPv6  " + (_lastShownIp6 == "::" ? "未获取" : _lastShownIp6),
                TextSize = IosStyle.CaptionSize
            };
            sub6.SetTextColor(new Color(TextSub));
            sub6.SetPadding(0, 0, 0, 0);
            sub6.Click += (s, e) =>
            {
                var ip6 = DeviceStatus.Ipv6;
                if (ip6 == "::") L10n.Toast(this, L10n.T("当前没有可用的 IPv6 地址"), ToastLength.Short).Show();
                else CopyText(ip6);                            // 点击复制 IPv6
            };
            col.AddView(sub6);
            sub6.SetMinimumHeight(Dp(28));
            sub6.Gravity = GravityFlags.CenterVertical;
            _meIp6 = sub6;

            head.AddView(col);

            var chev = new IconView(this, IconKind.ChevronRight) { IconColor = unchecked((int)0xFFC7C9CE), Thickness = 1.5f };
            head.AddView(chev, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

            head.Click += (s, e) => ShowIdentityDialog();
            headCard.AddView(head);

            var conn = NewGroup(meBox, L10n.T("连接"));
            // AhoChat 设备 ID（通信渠道里标识本机的 id）：点击整行复制完整值，
            // 对方在「发起新会话 → 私聊指定 ID 用户」里粘贴即可直接建会话。
            var selfId = bridge == null ? "" : (bridge.SelfPeerId ?? "");
            conn.AddView(MeRow(L10n.T("我的设备 ID"), selfId, () =>
            {
                if (string.IsNullOrEmpty(selfId))
                {
                    L10n.Toast(this, L10n.T("设备 ID 尚未就绪"), ToastLength.Short).Show();
                    return;
                }
                CopyText(selfId);
                L10n.Toast(this, L10n.T("设备 ID 已复制，发给对方即可私聊"), ToastLength.Short).Show();
            }));
            conn.AddView(BuildDivider());
            conn.AddView(MeRow(L10n.T("通信渠道设置"), L10n.T("IP · 蓝牙 · qaul · WLAN Direct"),
                () => StartActivity(new Intent(this, typeof(ChannelSettingsActivity)))));
            conn.AddView(BuildDivider());
            conn.AddView(MeRow(L10n.T("身份与指纹"), L10n.T("查看或更换设备身份"), ShowIdentityDialog));
            conn.AddView(BuildDivider());
            conn.AddView(MeRow(L10n.T("设置昵称"), L10n.T("其它设备看到的名字"), ShowRenameDialog));

            var sec = NewGroup(meBox, L10n.T("安全"));
            sec.AddView(MeRow(L10n.T("隐私与安全"), L10n.T("占用检测 · 防截屏 · 敏感权限审计"),
                () => StartActivity(new Intent(this, typeof(PrivacyActivity)))));

            var tools = NewGroup(meBox, L10n.T("工具"));
            tools.AddView(MeRow(L10n.T("悬浮窗"), FloatingWindowService.IsRunning ? L10n.T("运行中") : L10n.T("在其他 App 上显示小窗"),
                ToggleFloatingWindow));
            tools.AddView(BuildDivider());
            tools.AddView(MeRow(L10n.T("短信转发"), L10n.T("新短信转发到当前频道"), ShowSmsForwardDialog));
            tools.AddView(BuildDivider());
            tools.AddView(MeRow("DNS VPN", L10n.T("授权或停止系统 DNS 代理 · 网页控制台 /dns"), () =>
            {
                var dns = DnsCore.DnsManager.Instance;
                dns.Initialize(this);
                if (dns.IsVpnRunning) { dns.StopVpn(); return; }
                var consent = Android.Net.VpnService.Prepare(this);
                if (consent != null) StartActivityForResult(consent, 5173);
                else
                {
                    try { dns.StartVpn(); }
                    catch (Exception ex) { L10n.Toast(this, ex.Message, ToastLength.Long).Show(); }
                }
            }));
            tools.AddView(BuildDivider());
            tools.AddView(MeRow(L10n.T("DNS 管理网页"), L10n.T("上游服务器 · 过滤规则 · 查询记录"),
                () => BrowserActivity.Open(this, "http://127.0.0.1:" + WebServer.AppPort + "/dns", L10n.T("DNS 管理"))));
            tools.AddView(BuildDivider());
            tools.AddView(MeRow(L10n.T("服务地址"), WebServer.UrlOf("/hub"), ShowServicesDialog));

            // Web 服务运行状态 + 一键重启（修复「启动后网页打不开」）
            var web = NewGroup(meBox, L10n.T("Web 服务"));
            web.AddView(BuildWebStatusRow());

            var other = NewGroup(meBox, L10n.T("其他"));
            other.AddView(MeRow(L10n.T("开屏页"), SplashDesc(), ShowSplashSettings));
            other.AddView(BuildDivider());
            other.AddView(MeRow(L10n.T("语言", "Language"), LangDesc(), ShowLanguageDialog));
            other.AddView(BuildDivider());
            other.AddView(MeRow(L10n.T("清空全部消息"), L10n.T("所有会话的消息都会删除"), ClearAll));
            other.AddView(BuildDivider());
            other.AddView(MeRow(L10n.T("鸣谢"), L10n.T("本程序使用的开源项目"),
                () => StartActivity(new Intent(this, typeof(AcknowledgementsActivity)))));
            other.AddView(BuildDivider());
            other.AddView(MeRow(L10n.T("关于", "About"),
                L10n.T("版本 " + UpdateManager.CurrentVersion + " · 检查更新", "Version " + UpdateManager.CurrentVersion + " · Check for updates"),
                () => StartActivity(new Intent(this, typeof(AboutActivity)))));

            var ver = new TextView(this) { Text = L10n.T("AhoChat · 局域网优先的自组网聊天", "AhoChat · ad-hoc chat, LAN first"), TextSize = IosStyle.CaptionSize };
            ver.SetTextColor(new Color(unchecked((int)0xFFB0B4B8)));
            ver.Gravity = GravityFlags.CenterHorizontal;
            ver.SetPadding(0, Dp(18), 0, Dp(10));
            meBox.AddView(ver);

            // 网络变化（Wi-Fi/热点/移动数据切换）时，保证 IP 显示实时刷新
            StartMeIpWatch();
        }

        /// <summary>
        /// 「我的」页 Web 服务状态行：左侧状态图标（绿/黄/灰）+ 地址 + 右侧状态文字，
        /// 整行可点 = 立即重新检测；下方另有「重新启动 Web 服务」入口。
        /// </summary>
        private View BuildWebStatusRow()
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var box = new FrameLayout(this);
            box.Background = RoundBg(unchecked((int)0xFFF1F3F6), 11);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
            _webIcon = new IconView(this, IconKind.Globe)
            { IconColor = unchecked((int)0xFF9AA0A6) };
            box.AddView(_webIcon, new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
            row.AddView(box);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(8), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var title = new TextView(this) { Text = L10n.T("运行状态"), TextSize = IosStyle.BodySize };
            title.SetTextColor(new Color(TextMain));
            col.AddView(title);

            _webDesc = new TextView(this)
            { Text = L10n.T("正在检测…"), TextSize = IosStyle.CaptionSize };
            _webDesc.SetTextColor(new Color(TextSub));
            _webDesc.SetPadding(0, Dp(3), 0, 0);
            col.AddView(_webDesc);
            row.AddView(col);

            _webState = new TextView(this) { Text = L10n.T("检测中"), TextSize = IosStyle.CaptionSize };
            _webState.SetTextColor(new Color(unchecked((int)0xFF9AA0A6)));
            row.AddView(_webState);

            row.Click += (s, e) => RefreshWebStatus(true);
            var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
            holder.AddView(row);
            holder.AddView(BuildDivider());

            var restart = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            restart.SetGravity(GravityFlags.CenterVertical);
            restart.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            restart.SetMinimumHeight(Dp(IosStyle.RowMinHeight));
            var rbox = new FrameLayout(this);
            rbox.Background = RoundBg(unchecked((int)0xFFE7F6FE), 11);
            rbox.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
            rbox.AddView(new IconView(this, IconKind.Refresh) { IconColor = Accent },
                new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
            restart.AddView(rbox);
            var rt = new TextView(this) { Text = L10n.T("重新启动 Web 服务"), TextSize = IosStyle.BodySize };
            rt.SetTextColor(new Color(TextMain));
            rt.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            rt.SetPadding(Dp(12), 0, Dp(8), 0);
            restart.AddView(rt);
            var ract = new TextView(this) { Text = L10n.T("重启"), TextSize = IosStyle.CaptionSize };
            ract.SetTextColor(new Color(Accent));
            restart.AddView(ract);
            restart.Click += (s, e) => RestartWebService();
            holder.AddView(restart);

            // 首次进入强制检测一次（异步），重置节流计数保证不被跳过
            _webLastHealth = (WebServer.ServiceHealth)(-1);
            _webHealthTick = 0;
            RefreshWebStatus(true);
            return holder;
        }

        /// <summary>异步探测 Web 健康状态并刷新状态行；force=true 时忽略节流立即探测。</summary>
        private void RefreshWebStatus(bool force)
        {
            if (!force && _webHealthTick++ % 5 != 0) return;   // 2s 轮询 → 每 10s 探测一次，减少开销
            if (_webRestarting) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                var h = WebServer.ProbeHealth();
                RunOnUiThread(() => ApplyWebHealth(h));
            });
        }

        private void ApplyWebHealth(WebServer.ServiceHealth h)
        {
            if (_webState == null || _webDesc == null || _webIcon == null) return;
            try
            {
                _webLastHealth = h;
                string url = "http://" + (DeviceStatus.Ip ?? "127.0.0.1") + ":" + WebServer.ExternalPortActual;
                switch (h)
                {
                    case WebServer.ServiceHealth.Running:
                        _webState.Text = L10n.T("运行中");
                        _webState.SetTextColor(new Color(unchecked((int)0xFF34C759)));
                        _webDesc.Text = url + L10n.T("/hub · 局域网可访问");
                        _webIcon.IconColor = unchecked((int)0xFF34C759);
                        break;
                    case WebServer.ServiceHealth.NotRunning:
                        _webState.Text = L10n.T("未启动");
                        _webState.SetTextColor(new Color(unchecked((int)0xFFFF9F0A)));
                        _webDesc.Text = L10n.T("服务未启动 · 点此重新检测");
                        _webIcon.IconColor = unchecked((int)0xFFFF9F0A);
                        break;
                    default:
                        _webState.Text = L10n.T("无响应");
                        _webState.SetTextColor(new Color(unchecked((int)0xFFFF3B30)));
                        _webDesc.Text = L10n.T("端口在听但无应答 · 建议重启");
                        _webIcon.IconColor = unchecked((int)0xFFFF3B30);
                        break;
                }
            }
            catch { }
        }

        /// <summary>点击「重新启动 Web 服务」：后台重启 + 复检，期间禁用重复点击并给出提示。</summary>
        private void RestartWebService()
        {
            if (_webRestarting)
            {
                L10n.Toast(this, L10n.T("正在重启，请稍候…"), ToastLength.Short).Show();
                return;
            }
            var web = CoreHost.AhoChatA;
            _webRestarting = true;
            if (_webState != null) { _webState.Text = L10n.T("重启中"); _webState.SetTextColor(new Color(Accent)); }
            if (_webDesc != null) _webDesc.Text = L10n.T("正在重建监听与网关…");
            L10n.Toast(this, L10n.T("正在重启 Web 服务…"), ToastLength.Short).Show();

            Action<WebServer.ServiceHealth> onDone = h => RunOnUiThread(() =>
            {
                _webRestarting = false;
                ApplyWebHealth(h);
                string msg = h == WebServer.ServiceHealth.Running
                    ? L10n.T("Web 服务已重启，运行正常")
                    : "重启后仍未就绪（" + (h == WebServer.ServiceHealth.NotRunning ? "未启动" : "无响应") + "），请稍后重试";
                L10n.Toast(this, msg, ToastLength.Long).Show();
            });

            if (web == null)
                CoreHost.RetryWebServer(this, onDone);
            else
                web.Restart(onDone);
        }

        /// <summary>「我的」页 IP 行定时刷新：只在可见时跑，2 秒一次，值变了才改文本。</summary>
        private void StartMeIpWatch()
        {
            StopMeIpWatch();
            if (_meIpTimer == null) _meIpTimer = new Handler(Looper.MainLooper);
            _meIpRun = new Java.Lang.Runnable(() =>
            {
                try
                {
                    if (_meIp4 != null)
                    {
                        var ip4 = DeviceStatus.Ip;
                        if (!string.Equals(ip4, _lastShownIp4))
                        {
                            _lastShownIp4 = ip4;
                            _meIp4.Text = ip4 + " · " + ShortId(bridge.SelfPeerId);
                        }
                    }
                    if (_meIp6 != null)
                    {
                        var ip6 = DeviceStatus.Ipv6;
                        if (!string.Equals(ip6, _lastShownIp6))
                        {
                            _lastShownIp6 = ip6;
                            _meIp6.Text = "IPv6  " + (ip6 == "::" ? L10n.T("未获取") : ip6);
                        }
                    }
                    // Web 服务状态随本轮询一起刷新（内部带节流，约每 10s 探测一次）
                    RefreshWebStatus(false);
                }
                catch { }
                try { _meIpTimer.PostDelayed(_meIpRun, 2000); } catch { }
            });
            _meIpTimer.Post(_meIpRun);
        }

        private void StopMeIpWatch()
        {
            try
            {
                if (_meIpTimer != null && _meIpRun != null) _meIpTimer.RemoveCallbacks(_meIpRun);
            }
            catch { }
            _meIpRun = null;
        }

        /// <summary>开屏页状态摘要（「我的 → 其他」里显示）。</summary>
        private string SplashDesc()
        {
            if (!SplashSettings.Enabled) return L10n.T("已关闭 · 启动直接进入主界面");
            return SplashSettings.HasCustomImage ? L10n.T("已开启 · 自定义图片") : L10n.T("已开启 · 默认图片");
        }

        /// <summary>语言设置的副标题：跟随系统 / 简体中文 / English。</summary>
        private string LangDesc()
        {
            var o = L10n.Override;
            if (o == "zh") return "简体中文";
            if (o == "en") return "English (US)";
            return L10n.T("跟随系统", "Follow system") + " · " + (L10n.IsChinese ? "简体中文" : "English (US)");
        }

        /// <summary>语言：跟随系统 / 简体中文 / English，选中后立即重建界面。</summary>
        private void ShowLanguageDialog()
        {
            var items = new[]
            {
                L10n.T("跟随系统", "Follow system"),
                "简体中文",
                "English (US)"
            };
            var o = L10n.Override;
            int sel = o == "zh" ? 1 : o == "en" ? 2 : 0;

            L10n.Dialog(this)
                .SetTitle(L10n.T("语言", "Language"))
                .SetSingleChoiceItems(items, sel, (s, e) =>
                {
                    string next = e.Which == 1 ? "zh" : (e.Which == 2 ? "en" : null);
                    if (next == L10n.Override)
                    {
                        var same = s as Android.Content.IDialogInterface;
                        if (same != null) same.Dismiss();
                        return;   // 选了当前语言就不必重建
                    }
                    L10n.SetOverride(next);
                    var dlg = s as Android.Content.IDialogInterface;
                    if (dlg != null) dlg.Dismiss();
                    // 这次销毁只是为了换语言重画界面，核心服务必须留着。
                    _recreateKeepCore = true;
                    Recreate();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>开屏页设置：开关 / 自定义图片 / 恢复默认。</summary>
        private void ShowSplashSettings()
        {
            var items = new[]
            {
                SplashSettings.Enabled ? L10n.T("关闭开屏页") : L10n.T("打开开屏页"),
                L10n.T("设置自定义开屏图片"),
                L10n.T("恢复默认开屏页")
            };

            L10n.Dialog(this)
                .SetTitle(L10n.T("开屏页"))
                .SetItems(items, (s, e) =>
                {
                    if (e.Which == 0)
                    {
                        bool on = !SplashSettings.Enabled;
                        SplashSettings.SetEnabled(on);
                        L10n.Toast(this, on ? L10n.T("已开启开屏页") : L10n.T("已关闭开屏页"), ToastLength.Short).Show();
                        BuildMeTab();
                    }
                    else if (e.Which == 1)
                    {
                        PickSplashImage();
                    }
                    else
                    {
                        SplashSettings.ClearCustomImage();
                        if (!SplashSettings.Enabled) SplashSettings.SetEnabled(true);
                        L10n.Toast(this, L10n.T("已恢复默认开屏页"), ToastLength.Short).Show();
                        BuildMeTab();
                    }
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        /// <summary>选择一张图片作为自定义开屏。</summary>
        private void PickSplashImage()
        {
            var intent = new Intent(Intent.ActionGetContent);
            intent.SetType("image/*");
            try { StartActivityForResult(intent, ReqPickSplash); }
            catch { L10n.Toast(this, L10n.T("无法打开图片选择器"), ToastLength.Short).Show(); }
        }

        private View MeRow(string title, string desc, Action act)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(6), Dp(IosStyle.CardInset), Dp(6));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var t = new TextView(this) { Text = title, TextSize = IosStyle.BodySize };
            t.SetTextColor(new Color(TextMain));
            col.AddView(t);

            if (!string.IsNullOrEmpty(desc))
            {
                var d = new TextView(this) { Text = desc, TextSize = IosStyle.CaptionSize };
                d.SetTextColor(new Color(TextSub));
                d.SetPadding(0, Dp(3), 0, 0);
                col.AddView(d);
            }
            row.AddView(col);

            var chev = new IconView(this, IconKind.ChevronRight) { IconColor = unchecked((int)0xFFC7C9CE), Thickness = 1.5f };
            row.AddView(chev, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

            row.Click += (s, e) => act?.Invoke();
            return row;
        }

        private TextView RoundAvatar(string name, int sizeDp, string fallback = null)
        {
            var t = new TextView(this)
            {
                Text = fallback ?? Initial(name),
                Gravity = GravityFlags.Center,
                TextSize = sizeDp >= 50 ? 20 : 16
            };
            t.SetTextColor(Color.White);
            t.SetTypeface(null, TypefaceStyle.Bold);
            t.Background = RoundBg(AvatarColor(name), sizeDp / 2);
            t.LayoutParameters = new LinearLayout.LayoutParams(Dp(sizeDp), Dp(sizeDp));
            return t;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "—";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private void AddSectionLabel(LinearLayout parent, string text)
        {
            var t = new TextView(this) { Text = text, TextSize = 12 };
            t.SetTextColor(new Color(TextSub));
            t.SetPadding(Dp(IosStyle.PageInset), Dp(IosStyle.SectionGap), Dp(IosStyle.PageInset), Dp(8));
            parent.AddView(t);
        }

        private void AddCard(LinearLayout parent, View content)
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = RoundBg(CardBg, IosStyle.CardRadius);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.LeftMargin = Dp(IosStyle.PageInset); lp.RightMargin = Dp(IosStyle.PageInset);
            lp.BottomMargin = Dp(8);
            parent.AddView(card, lp);
            card.AddView(content);
        }

        /* ===================== 通话 / 共享入口 ===================== */

        /// <summary>
        /// 发起通话：先选设备，再进通话页。
        /// 顺序很重要——先把通话界面拉起来，再触发邀请，这样界面在 onResume 时能读到正确状态。
        /// </summary>
        private void StartCallFlow(bool video)
        {
            if (bridge == null) return;
            var peers = bridge.OnlinePeers();
            if (peers == null || peers.Count == 0)
            {
                L10n.Toast(this, L10n.T("还没有发现在线设备，请确认对方也打开了 App 且在同一网络"),
                    ToastLength.Long).Show();
                return;
            }

            AskMicThen(() =>
            {
                var names = peers.Select(p =>
                    (p.ContainsKey("name") ? p["name"] : p["id"]) + "  (" +
                    (p.ContainsKey("ip") ? p["ip"] : "?") + ")").ToArray();

                L10n.Dialog(this)
                    .SetTitle(video ? L10n.T("选择视频通话对象") : L10n.T("选择语音通话对象"))
                    .SetItems(names, (s, e) =>
                    {
                        var peer = peers[e.Which];
                        var id = peer.ContainsKey("id") ? peer["id"] : null;
                        if (string.IsNullOrEmpty(id)) return;
                        var name = peer.ContainsKey("name") ? peer["name"] : id;

                        var i = new Intent(this, typeof(CallActivity));
                        i.PutExtra("incoming", false);
                        StartActivity(i);
                        bridge.StartCall(id, name, video);
                    })
                    .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                    .Show();
            });
        }

        private Action _afterMic;

        /// <summary>直接呼叫某台设备（设备列表里点「通话」时用）。</summary>
        private void StartCallTo(string peerId, string name, bool video)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            AskMicThen(() =>
            {
                var i = new Intent(this, typeof(CallActivity));
                i.PutExtra("incoming", false);
                StartActivity(i);
                bridge.StartCall(peerId, name, video);
            });
        }

        /// <summary>手动直连：粘贴「IP:端口」即可自动识别（IPv4 / IPv6）。中转站管理在「通信渠道设置」。</summary>
        private void ShowManualConnect()
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(8), Dp(20), 0);

            var input = new EditText(this) { Hint = L10n.T("例如 192.168.1.23:45778 或 [fd00::1]:45778") };
            input.SetSingleLine(true);
            input.SetTextColor(Color.ParseColor("#1A1A1A"));
            input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
            box.AddView(input);

            var tip = new TextView(this)
            {
                Text = L10n.T("直接粘贴复制来的「IP:端口」即可自动识别；IPv6 用 [地址]:端口。")
                     + L10n.T("添加/移除中转站在「通信渠道设置」。")
            };
            tip.TextSize = 11;
            tip.SetTextColor(Color.ParseColor("#8E9099"));
            tip.SetPadding(Dp(2), Dp(6), Dp(2), 0);
            box.AddView(tip);

            L10n.Dialog(this)
                .SetTitle(L10n.T("手动直连"))
                .SetView(box)
                .SetPositiveButton(L10n.T("连接"), (s, e) =>
                {
                    var raw = (input.Text ?? "").Trim();
                    if (raw.Length == 0) return;
                    string host; int inlinePort;
                    if (!MchatBridge.ParseHostPort(raw, out host, out inlinePort) || string.IsNullOrEmpty(host))
                    {
                        L10n.Toast(this, L10n.T("地址格式不对，例如 192.168.1.23:45778"), ToastLength.Short).Show();
                        return;
                    }
                    int p = inlinePort > 0 ? inlinePort : 45778;
                    bool ok = bridge.ConnectToIp(host, p);
                    L10n.Toast(this, ok
                        ? L10n.T("已向 ") + MchatBridge.FormatHostPort(host, p) + L10n.T(" 发起握手，稍候查看设备列表")
                        : L10n.T("地址格式不对"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void AskMicThen(Action act)
        {
            if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted)
            {
                act();
                return;
            }
            _afterMic = act;
            RequestPermissions(new[] { Android.Manifest.Permission.RecordAudio }, ReqMicChat);
        }

        private const int ReqMicChat = 7311;

        private void OpenIpCamera(bool screen)
        {
            var i = new Intent(this, typeof(IpCameraActivity));
            i.PutExtra("screen", screen);
            StartActivity(i);
        }

        private void ShareMyLocation()
        {
            try
            {
                var ok = bridge.SendMyLocationOnce(currentConv);
                L10n.Toast(this, ok ? L10n.T("已发送一次当前位置") : L10n.T("定位不可用，请检查定位权限"),
                    ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("共享位置失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>文件共享：给出浏览器地址 + FTP 地址，可复制、可直接打开。</summary>
        private void ShowFileShareDialog()
        {
            // 未获得存储访问权限时，先引导授权：否则网页/ FTP 只能看到应用私有临时目录，
            // 用户放到 /sdcard/Mchat/file 的文件不会出现（这正是「网页端文件共享用不了」的根因）。
            if (LanShareService.NeedStorageAccess(this))
            {
                L10n.Dialog(this)
                    .SetTitle(L10n.T("需要存储权限"))
                    .SetMessage(L10n.T("文件共享默认目录为 ") + LanShareService.PreferredRoot +
                        L10n.T("。当前未获得存储访问权限，网页 / FTP 只能看到应用私有临时目录。") +
                        L10n.T("是否现在授权以使用 ") + LanShareService.PreferredRoot + "？")
                    .SetPositiveButton(L10n.T("去授权"), (s, e) => RequestShareStorage())
                    .SetNegativeButton(L10n.T("仍用临时目录"), (s, e) => ShowFileShareDialogCore())
                    .Show();
                return;
            }
            ShowFileShareDialogCore();
        }

        private void ShowFileShareDialogCore()
        {
            var url = WebServer.UrlOf("/file");
            var ftp = "ftp://" + DeviceStatus.Ip + ":" + FtpServer.Port;
            var msg = L10n.T("共享目录：") + LanShareService.ShareRoot + "\n\n"
                    + L10n.T("浏览器（同一局域网任意设备）：\n") + url + "\n\n"
                    + L10n.T("FTP 客户端：\n") + ftp + "\n\n"
                    + L10n.T("把文件放进上面的目录即可被看到/下载。FTP 协议与 HTTP 不同，只能单独占一个端口。");

            L10n.Dialog(this)
                .SetTitle(L10n.T("文件共享"))
                .SetMessage(msg)
                .SetPositiveButton(L10n.T("打开网页"), (s, e) => OpenInBrowser(url))
                .SetNeutralButton(L10n.T("复制地址"), (s, e) => CopyText(url))
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        /// <summary>申请存储访问权限：Android 11+ 引导「所有文件访问」；Android 10 申请 WRITE_EXTERNAL_STORAGE。</summary>
        private void RequestShareStorage()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    var intent = new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission);
                    intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                    StartActivity(intent);
                }
                else
                {
                    RequestPermissions(new[] { Android.Manifest.Permission.WriteExternalStorage }, ReqShareStorage);
                }
            }
            catch
            {
                try { RequestPermissions(new[] { Android.Manifest.Permission.WriteExternalStorage }, ReqShareStorage); } catch { }
            }
        }

        /// <summary>打开文档选择器挑选要发送的图片/视频或任意文件（mediaOnly 时优先图片视频）。</summary>
        private void PickFile(bool mediaOnly)
        {
            // 频道（chan:）也走 qaul 的文件传输：频道名 → 确定性 group_id，
            // 所以「在 Mchat 频道里发文件」与「在 qaul 群里发文件」是同一条链路。
            if (!Convs.IsQaulDm(currentConv) && !Convs.IsQaulGroup(currentConv) && !Convs.IsChan(currentConv))
            {
                L10n.Toast(this, L10n.T("文件传输仅支持 qaul 会话（qaul 私聊 / qaul 群 / 频道）"), ToastLength.Short).Show();
                return;
            }
            pendingFileConv = currentConv;
            pendingPickMedia = mediaOnly;
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            if (mediaOnly) intent.PutExtra(Intent.ExtraMimeTypes, new[] { "image/*", "video/*" });
            // 多选：一次勾选多张图/多个文件批量发送（进度对话框里逐个发）
            intent.PutExtra(Intent.ExtraAllowMultiple, true);
            try { StartActivityForResult(intent, ReqPickFile); }
            catch { L10n.Toast(this, L10n.T("无法打开文件选择器"), ToastLength.Short).Show(); }
        }

        private void ClearAll()
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("清空全部消息？"))
                .SetMessage(L10n.T("所有房间与私聊的消息都会被删除，并通知在线设备同步删除。此操作不可撤销。"))
                .SetPositiveButton(L10n.T("全部清空"), (s, e) =>
                {
                    var n = bridge.ClearAllMessages();
                    RefreshMessages(true);
                    RefreshConversations();
                    L10n.Toast(this, L10n.T("已清空 ") + n + L10n.T(" 条消息"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ShowJoinRoomDialog()
        {
            var input = new EditText(this);
            input.Hint = L10n.T("房间号，例如 mesh / 501");
            input.SetSingleLine(true);
            input.SetTextColor(Color.ParseColor("#1a1a1a"));

            var wrap = new LinearLayout(this);
            wrap.Orientation = Orientation.Vertical;
            wrap.SetPadding(Dp(18), Dp(8), Dp(18), 0);
            wrap.AddView(input);

            L10n.Dialog(this)
                .SetTitle(L10n.T("加入房间"))
                .SetView(wrap)
                .SetPositiveButton(L10n.T("进入"), (s, e) =>
                {
                    var code = Convs.NormalizeRoom(input.Text);
                    var conv = bridge.JoinRoom(code);
                    currentConv = conv;
                    RefreshConversations();
                    RefreshMessages(true);
                    UpdateHeader();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ShowPeerPicker()
        {
            var peers = bridge.OnlinePeers();
            if (peers.Count == 0)
            {
                L10n.Toast(this, L10n.T("当前没有在线的其它设备"), ToastLength.Short).Show();
                return;
            }

            var names = peers.Select(p =>
                (p.ContainsKey("name") ? p["name"] : p["id"]) + "  (" +
                (p.ContainsKey("ip") ? p["ip"] : "") + ")").ToArray();

            L10n.Dialog(this)
                .SetTitle(L10n.T("选择要私聊的设备"))
                .SetItems(names, (s, e) =>
                {
                    var peer = peers[e.Which];
                    var conv = bridge.StartDm(peer["id"], peer["name"]);
                    if (string.IsNullOrEmpty(conv)) return;
                    currentConv = conv;
                    RefreshConversations();
                    // 没有消息的私聊会被 GetConversations() 过滤掉，若只设 activeConv
                    // 而不进聊天页，用户就再也找不到它、发不出第一条消息。
                    OpenChatConv(conv);
                })
                .Show();
        }

        /* ===================== qaul 联系人 / 群组 ===================== */

        private void ShowQaulContacts()
        {
            var users = bridge.GetQaulUsers() as System.Collections.IList;
            var names = new List<string>();
            var ids = new List<string>();

            if (users != null)
            {
                foreach (var o in users)
                {
                    var jo = Newtonsoft.Json.Linq.JObject.FromObject(o);
                    var name = (string)jo["name"] ?? "?";
                    var id = (string)jo["id"] ?? "";
                    var hasKey = (bool?)jo["hasKey"] == true;
                    names.Add(name + (hasKey ? "" : L10n.T("（等公钥）")) + "\n" + Short(id, 22));
                    ids.Add(id);
                }
            }

            if (names.Count == 0)
            {
                L10n.Toast(this, L10n.T("还没有发现 qaul 用户（需要同网段的 qaul 节点在线）"), ToastLength.Long).Show();
                return;
            }

            L10n.Dialog(this)
                .SetTitle(L10n.T("qaul 联系人"))
                .SetItems(names.ToArray(), (s2, e2) =>
                {
                    var conv = bridge.OpenQaulDm(ids[e2.Which]);
                    if (conv != null) SwitchConv(conv);
                })
                .SetNegativeButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        private void ShowQaulGroups()
        {
            var groups = bridge.GetQaulGroups() as System.Collections.IList;
            var items = new List<string> { L10n.T("新建群组") };
            var ids = new List<string> { null };

            if (groups != null)
            {
                foreach (var o in groups)
                {
                    var jo = Newtonsoft.Json.Linq.JObject.FromObject(o);
                    items.Add((string)jo["name"] + "（" + (int?)jo["members"] + L10n.T(" 人）"));
                    ids.Add((string)jo["id"]);
                }
            }

            L10n.Dialog(this)
                .SetTitle(L10n.T("qaul 群组"))
                .SetItems(items.ToArray(), (s2, e2) =>
                {
                    if (ids[e2.Which] == null) { AskCreateGroup(); return; }
                    ShowGroupActions(ids[e2.Which], items[e2.Which]);
                })
                .SetNegativeButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        private void AskCreateGroup()
        {
            var input = new EditText(this);
            input.Hint = L10n.T("群组名称");
            input.SetSingleLine(true);

            L10n.Dialog(this)
                .SetTitle(L10n.T("新建 qaul 群组"))
                .SetView(input)
                .SetPositiveButton(L10n.T("创建"), (s2, e2) =>
                {
                    var name = (input.Text ?? "").Trim();
                    if (name.Length == 0) name = "新群组";
                    var conv = bridge.CreateQaulGroup(name);
                    if (conv == null) { L10n.Toast(this, L10n.T("创建失败（节点未运行）"), ToastLength.Short).Show(); return; }
                    SwitchConv(conv);
                })
                .SetNegativeButton(L10n.T("取消"), (s2, e2) => { })
                .Show();
        }

        private void ShowGroupActions(string groupId, string title)
        {
            var actions = new[] { L10n.T("进入群聊"), L10n.T("邀请成员"), L10n.T("成员 / 移出"), L10n.T("退出群聊") };
            L10n.Dialog(this)
                .SetTitle(title)
                .SetItems(actions, (s2, e2) =>
                {
                    if (e2.Which == 0)
                    {
                        SwitchConv(Convs.QaulGroupPrefix + groupId);
                    }
                    else if (e2.Which == 1)
                    {
                        ShowInvitePicker(groupId);
                    }
                    else if (e2.Which == 2)
                    {
                        ShowMemberPicker(groupId);
                    }
                    else
                    {
                        L10n.Dialog(this)
                            .SetTitle(L10n.T("退出群聊"))
                            .SetMessage(L10n.T("只在本机移除这个群，其他成员不受影响。"))
                            .SetPositiveButton(L10n.T("退出"), (s3, e3) =>
                            {
                                var ok = bridge.LeaveQaulGroup(groupId);
                                L10n.Toast(this, ok ? L10n.T("已退出") : L10n.T("退出失败"), ToastLength.Short).Show();
                                RefreshConversations();
                            })
                            .SetNegativeButton(L10n.T("取消"), (s3, e3) => { })
                            .Show();
                    }
                })
                .SetNegativeButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        /// <summary>列成员，点谁就把谁移出群（自己/管理员不可移出）。</summary>
        private void ShowMemberPicker(string groupId)
        {
            var names = new List<string>();
            var ids = new List<string>();
            try
            {
                var groups = bridge.GetQaulGroups() as System.Collections.IList;
                if (groups != null)
                {
                    foreach (var o in groups)
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.FromObject(o);
                        if ((string)jo["id"] != groupId) continue;
                        var arr = jo["memberList"] as Newtonsoft.Json.Linq.JArray;
                        if (arr == null) break;
                        foreach (var mj in arr)
                        {
                            var jm = (Newtonsoft.Json.Linq.JObject)mj;
                            var role = (int?)jm["role"] ?? 0;
                            var st = (int?)jm["state"] ?? 0;
                            names.Add(((string)jm["name"] ?? "?") +
                                      (role == 255 ? "（管理员）" : "") +
                                      (st == 1 ? "" : "（待接受）"));
                            ids.Add((string)jm["id"] ?? "");
                        }
                        break;
                    }
                }
            }
            catch { }

            if (names.Count == 0)
            {
                L10n.Toast(this, L10n.T("没有成员信息"), ToastLength.Short).Show();
                return;
            }

            L10n.Dialog(this)
                .SetTitle(L10n.T("成员（点选＝移出）"))
                .SetItems(names.ToArray(), (s2, e2) =>
                {
                    var peer = ids[e2.Which];
                    if (string.IsNullOrEmpty(peer)) return;
                    L10n.Dialog(this)
                        .SetTitle(L10n.T("移出群聊"))
                        .SetMessage(L10n.T("确定把 ") + names[e2.Which] + L10n.T(" 移出这个群？"))
                        .SetPositiveButton(L10n.T("移出"), (s3, e3) =>
                        {
                            var ok = bridge.RemoveFromQaulGroup(groupId, peer);
                            L10n.Toast(this, ok ? L10n.T("已移出") : L10n.T("移出失败"), ToastLength.Short).Show();
                        })
                        .SetNegativeButton(L10n.T("取消"), (s3, e3) => { })
                        .Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        private void ShowInvitePicker(string groupId)
        {
            var users = bridge.GetQaulUsers() as System.Collections.IList;
            var names = new List<string>();
            var ids = new List<string>();
            if (users != null)
            {
                foreach (var o in users)
                {
                    var jo = Newtonsoft.Json.Linq.JObject.FromObject(o);
                    names.Add((string)jo["name"] ?? "?");
                    ids.Add((string)jo["id"] ?? "");
                }
            }
            if (names.Count == 0)
            {
                L10n.Toast(this, L10n.T("没有可邀请的 qaul 用户"), ToastLength.Short).Show();
                return;
            }

            L10n.Dialog(this)
                .SetTitle(L10n.T("邀请成员"))
                .SetItems(names.ToArray(), (s2, e2) =>
                {
                    var ok = bridge.InviteToQaulGroup(groupId, ids[e2.Which]);
                    L10n.Toast(this, ok ? L10n.T("已发送邀请") : L10n.T("邀请失败（对方公钥未知/未连接）"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        private void ShowQaulStaticPeers()
        {
            var st = Newtonsoft.Json.Linq.JObject.FromObject(bridge.GetQaulStatus());
            var list = st["staticPeers"] as Newtonsoft.Json.Linq.JArray;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("本机 IPv6：" + ((bool?)st["ipv6"] == true ? "可用" : "不可用（已自动禁用）"));
            sb.AppendLine(L10n.T("监听端口：") + (st["port"] ?? 0));
            sb.AppendLine();
            if (list == null || list.Count == 0) sb.AppendLine("（暂无静态节点）");
            else foreach (var x in list) sb.AppendLine("• " + (string)x);

            var input = new EditText(this);
            input.Hint = L10n.T("1.2.3.4:9200 或 [fd00::1]:9200");
            input.SetSingleLine(true);

            var box = new LinearLayout(this);
            box.Orientation = Orientation.Vertical;
            box.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));
            var tv = new TextView(this);
            tv.Text = sb.ToString();
            tv.TextSize = 12;
            box.AddView(tv);
            box.AddView(input);

            L10n.Dialog(this)
                .SetTitle(L10n.T("qaul 静态节点"))
                .SetView(box)
                .SetPositiveButton(L10n.T("添加"), (s2, e2) =>
                {
                    var addr = (input.Text ?? "").Trim();
                    if (addr.Length == 0) return;
                    var ok = bridge.AddQaulStaticPeer(addr);
                    L10n.Toast(this, ok ? L10n.T("已添加并尝试连接") : L10n.T("地址格式不对"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        private static string Short(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /* ===================== qaul 节点信息 ===================== */

        /// <summary>
        /// 展示 qaul 网络节点状态：本机 PeerId、监听端口、邻居列表。
        /// 这里能看到「qaul 客户端应该看到的那个身份」，便于两台设备互认。
        /// </summary>
        private void ShowQaulNodeDialog()
        {
            var st = bridge.GetQaulStatus() as Newtonsoft.Json.Linq.JObject
                     ?? Newtonsoft.Json.Linq.JObject.FromObject(bridge.GetQaulStatus());

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("libp2p 节点：" + ((bool?)st["running"] == true ? "运行中" : "未运行"));
            sb.AppendLine(L10n.T("监听端口：") + (st["port"] ?? 0));
            sb.AppendLine(L10n.T("本机 qaul PeerId："));
            sb.AppendLine((string)st["peerId"] ?? "-");
            sb.AppendLine();
            sb.AppendLine(L10n.T("邻居：") + (st["neighbours"] ?? 0) + L10n.T("    互认路由表：") + (st["infoRx"] ?? 0)
                          + L10n.T("    待发私聊：") + (st["pendingDm"] ?? 0));
            sb.AppendLine(L10n.T("公钥交换：已请求 ") + (st["userReqSent"] ?? 0) +
                          L10n.T(" 次 / 收到应答 ") + (st["userRespRx"] ?? 0) +
                          L10n.T(" 次（最近一条含 ") + (st["lastUserRespCount"] ?? 0) + L10n.T(" 个用户）"));
            if ((int?)(st["infoRx"]) == 0)
                sb.AppendLine(L10n.T("注意：还没收到对端的路由信息：对方可能不认识本机，或 /qaul_info 帧没解析成功"));
            else if ((int?)(st["userRespRx"]) == 0)
                sb.AppendLine(L10n.T("注意：路由信息有了，但对方没回用户信息（USER_RESPONSE）：对方用户表可能还没记录本机"));
            sb.AppendLine();

            // 可点列表：优先列出已识别的账号（昵称，正确那条），再补上还没换到公钥的
            // 原始邻居。两者按 id 去重——同一台官方客户端只会显示一次。
            var usersArr = st["users"] as Newtonsoft.Json.Linq.JArray;
            var peersArr = st["peers"] as Newtonsoft.Json.Linq.JArray;
            var labels = new List<string>();
            var labelKinds = new List<IconKind>();
            var ids = new List<string>();
            var covered = new HashSet<string>();

            // 1) 已识别账号（昵称，官方客户端真正的聊天身份）
            if (usersArr != null)
            {
                foreach (var u in usersArr)
                {
                    var id = (string)u["id"];
                    if (string.IsNullOrEmpty(id) || covered.Contains(id)) continue;
                    covered.Add(id);
                    var name = (string)u["name"] ?? id.Substring(0, 10);
                    var hasKey = (bool?)u["hasKey"] == true;
                    labels.Add(name + (hasKey ? "" : L10n.T("（等公钥）")));
                    labelKinds.Add(IconKind.Person);
                    ids.Add(id);
                }
            }

            // 2) 邻居：还没识别成账号的原始节点（等公钥交换完成后会自动转为上面的昵称条目）
            if (peersArr != null)
            {
                foreach (var p in peersArr)
                {
                    var peer = (string)p["peer"];
                    if (string.IsNullOrEmpty(peer) || covered.Contains(peer)) continue;
                    covered.Add(peer);
                    var hasKey = (bool?)p["hasKey"] == true;
                    var pending = (int?)p["pending"] ?? 0;
                    var inRouting = (bool?)p["inRouting"] == true;
                    var name = (string)p["name"];
                    var label = string.IsNullOrEmpty(name) ? peer.Substring(0, 10) : name;
                    labels.Add(label + (hasKey ? "" : L10n.T("（等公钥）")) +
                               (pending > 0 ? L10n.T(" · 待发 ") + pending : "") +
                               (inRouting ? "" : L10n.T(" · 无路由")));
                    labelKinds.Add(IconKind.Chat);
                    ids.Add(peer);
                }
            }

            if (ids.Count == 0)
            {
                sb.AppendLine(L10n.T("（暂无邻居 / 账号）"));
                sb.AppendLine(L10n.T("qaul 客户端能发现本机的前提：同一局域网、允许组播、"));
                sb.AppendLine(L10n.T("本应用在前台或常驻服务里运行（端口 ") + (st["port"] ?? 0) + "）。");
                sb.AppendLine();
                sb.AppendLine(L10n.T("跨网段请用下面的「手动添加节点」填 IP:端口。"));
                sb.AppendLine(L10n.T("注意：qaul 官方公共节点（144.91.74.192 / IPv6 那个）"));
                sb.AppendLine(L10n.T("只监听 udp/9229/quic-v1，本节点的 libp2p 实现是纯 TCP，"));
                sb.AppendLine(L10n.T("连不上它们——需要一台同样跑本程序的机器当中继。"));
            }
            else
            {
                sb.AppendLine(L10n.T("点下面的账号即可发消息："));
            }

            var box = new LinearLayout(this);
            box.Orientation = Orientation.Vertical;

            var tv = new TextView(this);
            tv.Text = sb.ToString();
            tv.TextSize = 12;
            tv.SetTextIsSelectable(true);
            tv.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));
            box.AddView(tv);

            var btnStyle = new Action<TextView>(b =>
            {
                b.TextSize = 14;
                b.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
                b.SetTextColor(Color.ParseColor("#1f7a3d"));
            });

            AlertDialog dlg = null;

            for (int i = 0; i < ids.Count; i++)
            {
                var peer = ids[i];
                var row = new TextView(this);
                row.Text = labels[i];
                btnStyle(row);
                try { Icons.Leading(row, labelKinds[i], Color.ParseColor("#1f7a3d"), 16f, 8f); } catch { }
                row.Click += (s, e) =>
                {
                    var conv = bridge.OpenQaulDm(peer);
                    if (conv == null) { L10n.Toast(this, L10n.T("无法进入会话（对方尚未私聊本机）"), ToastLength.Short).Show(); return; }
                    if (dlg != null) { try { dlg.Dismiss(); } catch { } }
                    SwitchConv(conv);
                    L10n.Toast(this, L10n.T("已进入与 ") + peer.Substring(0, 10) + L10n.T(" 的私聊，发消息即可"),
                                  ToastLength.Short).Show();
                };
                box.AddView(row);
            }

            var scroll = new ScrollView(this);
            scroll.AddView(box);

            dlg = L10n.Dialog(this)
                .SetTitle(L10n.T("qaul 在线设备（点选即进入私聊）"))
                .SetView(scroll)
                .SetPositiveButton(L10n.T("关闭"), (s2, e2) => { })
                .SetNeutralButton(L10n.T("添加节点"), (s2, e2) => ShowAddQaulPeerDialog())
                .SetNegativeButton(L10n.T("日志"), (s2, e2) => ShowQaulLog())
                .Create();
            dlg.Show();
        }

        /// <summary>手动添加 qaul 静态节点：填 IP:端口，跨网段/公网直连用。</summary>
        private void ShowAddQaulPeerDialog()
        {
            var input = new EditText(this);
            input.Hint = L10n.T("例如 192.168.1.50:9200 或 [fd00::1]:9200");
            input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
            input.SetSingleLine(true);
            input.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));

            L10n.Dialog(this)
                .SetTitle(L10n.T("手动添加 qaul 节点"))
                .SetMessage(L10n.T("填对方机器的 IP 和 qaul 端口（默认 9200，可自定义）。") +
                            L10n.T("支持 IPv4 / IPv6，IPv6 写成 [fd00::1]:9200。") +
                            L10n.T("对方需运行本程序或兼容的 libp2p/TCP 节点。"))
                .SetView(input)
                .SetPositiveButton(L10n.T("添加"), (s, e) =>
                {
                    var addr = (input.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(addr)) return;
                    var ok = bridge.AddQaulStaticPeer(addr);
                    L10n.Toast(this, ok ? L10n.T("已添加 ") + addr : L10n.T("地址无效或 qaul 未运行"),
                                   ToastLength.Short).Show();
                    if (ok) ShowQaulNodeDialog();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>显示 qaul 运行日志（最近 300 条），用来排查「等不到公钥」。</summary>
        private void ShowQaulLog()
        {
            string txt;
            try { txt = bridge.GetQaulLogText(); }
            catch { txt = ""; }
            if (string.IsNullOrEmpty(txt)) txt = L10n.T("（暂无日志）");

            var tv = new TextView(this);
            tv.Text = txt;
            tv.TextSize = 10;
            tv.SetTextIsSelectable(true);
            tv.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));

            var scroll = new ScrollView(this);
            scroll.AddView(tv);

            L10n.Dialog(this)
                .SetTitle(L10n.T("qaul 日志"))
                .SetView(scroll)
                .SetPositiveButton(L10n.T("关闭"), (s2, e2) => { })
                .Show();
        }

        /* ===================== 身份与指纹 ===================== */

        /// <summary>
        /// 身份弹窗：同时展示 qaul（Ed25519 → libp2p PeerId）与 bitchat
        /// （X25519 → peerID、Ed25519 → 指纹）两套格式，并支持分别更换。
        /// 「换指纹」会立即生效并在局域网重新广播，旧身份不再使用。
        /// </summary>
        private void ShowIdentityDialog()
        {
            var id = bridge == null ? null : bridge.Identity;
            if (id == null)
            {
                L10n.Toast(this, L10n.T("身份尚未初始化"), ToastLength.Short).Show();
                return;
            }

            var pal = IosStyle.Colors(this);
            var scroll = new ScrollView(this);
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetBackgroundColor(new Color(pal.Background));
            box.SetPadding(Dp(IosStyle.PageInset), Dp(8), Dp(IosStyle.PageInset), Dp(8));
            scroll.AddView(box);

            Action<string, string, string> section = (title, subtitle, value) =>
            {
                var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
                card.Background = IosStyle.GroupCardBackground(this, pal);
                card.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
                var lp = new LinearLayout.LayoutParams(-1, -2);
                lp.BottomMargin = Dp(7);
                card.LayoutParameters = lp;

                var head = IosStyle.BodyText(this, title, pal, IosStyle.BodySize, true);
                card.AddView(head);

                if (!string.IsNullOrEmpty(subtitle))
                {
                    var sub = IosStyle.BodyText(this, subtitle, pal, IosStyle.CaptionSize);
                    sub.SetTextColor(new Color(pal.Secondary));
                    sub.SetPadding(0, Dp(2), 0, Dp(4));
                    card.AddView(sub);
                }

                var val = IosStyle.BodyText(this, value, pal, 12f);
                val.SetTextColor(new Color(pal.Accent));
                val.Typeface = Typeface.Monospace;
                val.SetTextIsSelectable(true);
                val.SetPadding(Dp(9), Dp(7), Dp(9), Dp(7));
                val.Background = IosStyle.Rounded(this,
                    pal.Night ? unchecked((int)0xFF2C2C2E) : unchecked((int)0xFFF2F2F7), IosStyle.ControlRadius);
                val.Click += (s2, e2) => { CopyText(value); L10n.Toast(this, L10n.T("已复制"), ToastLength.Short).Show(); };
                card.AddView(val);
                box.AddView(card);
            };

            section(L10n.T("AhoChat 身份"), L10n.T("局域网内的设备 id（换指纹时会一起更换）"), id.MchatId + L10n.T("   · 第 ") + id.Generation + (L10n.IsChinese ? L10n.T(" 代") : ""));
            section(L10n.T("qaul 身份（Ed25519 / libp2p PeerId）"), L10n.T("与 qaul 的 user_id 同格式，可直接互认"), id.QaulPeerId);
            section(L10n.T("qaul 公钥指纹"), L10n.T("供对端核对（4 位一组）"), LocalIdentity.Grouped(id.QaulKeyHex));

            var tip = IosStyle.BodyText(this,
                L10n.T("更换指纹后：旧身份立即作废并在局域网重新广播，对端会看到一个新设备；本机聊天记录仍保留。"),
                pal, IosStyle.CaptionSize);
            tip.SetTextColor(new Color(pal.Secondary));
            tip.SetPadding(Dp(4), Dp(2), Dp(4), Dp(6));
            box.AddView(tip);

            var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            Action<string, IdentityScope> addButton = (text, scope) =>
            {
                var btn = IosStyle.ActionButton(this, text, scope == IdentityScope.All, pal);
                var lp = new LinearLayout.LayoutParams(0, -2, 1f) { RightMargin = Dp(6) };
                btn.LayoutParameters = lp;
                btn.Click += (s2, e2) => ConfirmRotate(scope);
                buttons.AddView(btn);
            };
            addButton(L10n.T("更换身份"), IdentityScope.All);
            addButton(L10n.T("只换 qaul"), IdentityScope.Qaul);
            box.AddView(buttons);

            var close = IosStyle.BodyText(this, L10n.T("关闭"), pal, IosStyle.BodySize);
            close.Gravity = GravityFlags.End;
            close.SetTextColor(new Color(pal.Accent));
            close.SetPadding(0, Dp(8), Dp(4), 0);
            box.AddView(close);

            var dlg = L10n.Dialog(this)
                .SetTitle(L10n.T("身份与指纹"))
                .SetView(scroll)
                .Create();
            close.Click += (s2, e2) => dlg.Dismiss();
            IosStyle.ShowCardDialog(dlg, this, 0.94f, 0);
        }

        /// <summary>换指纹前的二次确认（不可撤销，必须让用户明确知道后果）。</summary>
        private void ConfirmRotate(IdentityScope scope)
        {
            string what = scope == IdentityScope.Qaul ? "qaul（Ed25519 / PeerId）"
                        : scope == IdentityScope.Bitchat ? "蓝牙（peerID + 身份指纹）"
                        : L10n.T("qaul + 蓝牙 两套身份（并更换 AhoChat 设备 id）");

            L10n.Dialog(this)
                .SetTitle(L10n.T("更换指纹"))
                .SetMessage(L10n.T("将重新生成") + what + "。\n\n" +
                            L10n.T("· 旧身份立即作废，对端会把你当成一台新设备；\n") +
                            L10n.T("· 聊天记录保留在本机；\n") +
                            L10n.T("· 操作不可撤销。"))
                .SetPositiveButton(L10n.T("确认更换"), (s2, e2) =>
                {
                    var id = bridge.RotateIdentity(scope);
                    if (id == null)
                    {
                        L10n.Toast(this, L10n.T("更换失败"), ToastLength.Short).Show();
                        return;
                    }
                    L10n.Toast(this, L10n.T("指纹已更换（第 ") + id.Generation + L10n.T(" 代）"), ToastLength.Short).Show();
                    UpdateHeader();
                    ShowIdentityDialog();
                })
                .SetNegativeButton(L10n.T("取消"), (s2, e2) => { })
                .Show();
        }

        private void ShowRenameDialog()
        {
            var pal = IosStyle.Colors(this);
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetBackgroundColor(new Color(pal.Background));
            box.SetPadding(Dp(IosStyle.PageInset), Dp(10), Dp(IosStyle.PageInset), Dp(6));

            var tip = IosStyle.BodyText(this, L10n.T("其它设备在聊天和附近设备列表里看到的名字。"), pal, IosStyle.CaptionSize);
            tip.SetTextColor(new Color(pal.Secondary));
            tip.SetPadding(0, 0, 0, Dp(7));
            box.AddView(tip);

            var input = new EditText(this)
            {
                Hint = L10n.T("请输入昵称"),
                Text = bridge.SelfName ?? ""
            };
            input.SetSingleLine(true);
            input.SetMaxLines(1);
            input.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            IosStyle.StyleInput(input);
            box.AddView(input, new LinearLayout.LayoutParams(-1, -2));

            var dlg = L10n.Dialog(this)
                .SetTitle(L10n.T("设置昵称"))
                .SetView(box)
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    bridge.SetDisplayName(input.Text);
                    L10n.Toast(this, L10n.T("昵称已保存"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Create();
            IosStyle.ShowCardDialog(dlg, this, 0.94f, 0);
        }

        /// <summary>
        /// 「频道密码」面板：给当前会话/频道设置 Mchat 独有的端到端加密密码。
        /// 双方设置同一个密码即可互通；安全码用于线下核对，防止中间人。
        /// </summary>
        private void ShowChannelPasswordDialog()
        {
            var conv = string.IsNullOrEmpty(currentConv) ? Convs.Room(Convs.DefaultRoom) : currentConv;

            var box = new LinearLayout(this);
            box.Orientation = Orientation.Vertical;
            box.SetPadding(Dp(20), Dp(8), Dp(20), 0);

            var cur = bridge.GetChannelCrypto(conv);
            var jo = Newtonsoft.Json.Linq.JObject.FromObject(cur);
            var enabled = jo["enabled"] != null && (bool)jo["enabled"];
            var handshaked = jo["handshaked"] != null && (bool)jo["handshaked"];
            var safety = jo["safety"] == null ? "" : jo["safety"].ToString();

            var info = new TextView(this);
            info.TextSize = 13;
            info.SetTextColor(Color.ParseColor("#666666"));
            info.Text =
                L10n.T("当前会话：") + Convs.TitleOf(conv) + "\n" +
                L10n.T("状态：") + (enabled ? L10n.T("已加密（LC1 单层·频道密码）")
                                    : L10n.T("未加密（明文经 qaul / 蓝牙 / 局域网传输）")) + "\n" +
                L10n.T("说明：qaul 自己的单聊/群聊已端到端加密，中继读不到内容；\n") +
                L10n.T("这里再加一层，只是为了让 qaul 侧连明文都拿不到。\n") +
                (enabled ? L10n.T("安全码：") + safety + L10n.T("\n与对方核对一致才说明是同一个密码。") : "");
            box.AddView(info);

            var input = new EditText(this);
            input.SetSingleLine(true);
            input.Hint = L10n.T("输入频道密码（留空 = 取消加密）");
            if (enabled) input.Text = bridge.ChannelPasswordOf(conv) ?? "";
            box.AddView(input);

            var dlg = L10n.Dialog(this)
                .SetTitle(L10n.T("频道密码（LC1 端到端加密）"))
                .SetView(box)
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    var pwd = input.Text ?? "";
                    bridge.SetChannelPassword(conv, pwd);
                    L10n.Toast(this,
                        string.IsNullOrEmpty(pwd) ? L10n.T("已取消该会话的加密") : L10n.T("已设置加密密码（单层，qaul 侧只见密文）"),
                        ToastLength.Short).Show();
                    UpdateHeader();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { });

            if (enabled)
            {
                dlg.SetNeutralButton(L10n.T("重新应用"), (s, e) =>
                {
                    bridge.SetChannelPassword(conv, bridge.ChannelPasswordOf(conv));
                    L10n.Toast(this, L10n.T("已重新应用频道密码"), ToastLength.Short).Show();
                });
            }

            dlg.Show();
        }

        private void ShowSendUrlDialog()
        {
            var input = new EditText(this);
            input.Hint = "https://…";
            input.SetSingleLine(true);

            L10n.Dialog(this)
                .SetTitle(L10n.T("发送链接"))
                .SetView(input)
                .SetPositiveButton(L10n.T("发送"), (s, e) =>
                {
                    var url = (input.Text ?? "").Trim();
                    if (url.Length == 0) return;
                    bridge.SendInConv(currentConv, url, "url", url);
                    ScrollToBottom();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /* ===================== 其它入口 ===================== */

        /// <summary>展示局域网网页端（外部浏览器）的访问地址：统一入口 :7790（网关反代到内置网页后端）。</summary>
        private void ShowWebEndpoint()
        {
            try
            {
                var info = bridge.GetShareInfo();
                L10n.Dialog(this)
                    .SetTitle(L10n.T("网页端地址（浏览器访问）"))
                    .SetMessage(string.IsNullOrEmpty(info) ? L10n.T("请先连接 WiFi 后查看访问地址") : info)
                    .SetPositiveButton(L10n.T("复制"), (s, e) =>
                    {
                        try
                        {
                            var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                            cm.Text = info;
                            L10n.Toast(this, L10n.T("已复制访问信息"), ToastLength.Short).Show();
                        }
                        catch { }
                    })
                    .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                    .Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("获取网页端地址失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private void OpenSharePage()
        {
            OpenInBrowser("http://127.0.0.1:" + WebServer.ExternalPortActual + "/file");
        }

        /// <summary>
        /// 应用内浏览器打开网页（http / https）。
        /// 以前统一丢给系统浏览器，用户一跳出 App 就断了上下文；现在聊天里点链接、
        /// 点裸 IP 都在应用内看，返回键即可回到聊天。其它协议（ftp / tel / mailto）仍走系统。
        /// </summary>
        private void OpenInBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            // 站内相对路径（如 /api/share/raw?path=…）补成本机绝对地址，
            // 否则会被当成「主机名」拼出 http:///api/... 这种坏地址。
            if (url.StartsWith("/"))
            {
                int port = 0;
                try { port = WebServer.ExternalPortActual; } catch { }
                if (port <= 0) port = 7790;
                url = "http://127.0.0.1:" + port + url;
            }
            if (BrowserActivity.CanHandle(url)) BrowserActivity.Open(this, url);
            else OpenExternal(url);
        }

        private void OpenExternal(string url)
        {
            try
            {
                var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                StartActivity(intent);
            }
            catch
            {
                L10n.Toast(this, L10n.T("没有可打开该链接的应用"), ToastLength.Short).Show();
            }
        }

        private void ToggleScreenDraw()
        {
            try
            {
                if (!Android.Provider.Settings.CanDrawOverlays(this))
                {
                    MchatBridge.RequestScreenDrawPermission(this);
                    return;
                }
                if (ScreenDrawService.IsRunning) ScreenDrawService.Hide(this);
                else ScreenDrawService.Show(this);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("屏幕画笔操作失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private void ToggleFloatingWindow()
        {
            try
            {
                if (FloatingWindowService.IsRunning) { FloatingWindowService.Hide(this); return; }
                if (!FloatingWindowService.CanDrawOverlays(this))
                {
                    MchatBridge.RequestScreenDrawPermission(this);
                    return;
                }
                FloatingWindowService.Show(this);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("悬浮窗操作失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        public override void OnBackPressed()
        {
            // 聊天页 → 回会话列表（符合 IM 的直觉）
            if (inChat) { CloseChat(); return; }
            // 其它 Tab → 回消息页
            if (currentTab != 0) { ShowTab(0); return; }

            if (!FloatingWindowService.IsRunning)
            {
                FinishAffinity();
                return;
            }
            base.OnBackPressed();
        }

        /* ===================== 底部音乐迷你播放条 ===================== */

        private void InitMiniBar()
        {
            musicMiniBar = FindViewById<View>(Resource.Id.musicMiniBar);
            miniCover = FindViewById<ImageView>(Resource.Id.miniCover);
            // clipToOutline 不是合法的 XML 属性，必须在代码里设置，否则圆角背景不裁切（封面显示为方形）
            if (miniCover != null) miniCover.ClipToOutline = true;
            miniTitle = FindViewById<TextView>(Resource.Id.miniTitle);
            miniLyric0 = FindViewById<TextView>(Resource.Id.miniLyric0);
            miniLyric1 = FindViewById<TextView>(Resource.Id.miniLyric1);
            miniLyric2 = FindViewById<TextView>(Resource.Id.miniLyric2);
            miniPrev = FindViewById<ImageButton>(Resource.Id.miniPrev);
            miniPlay = FindViewById<ImageButton>(Resource.Id.miniPlay);
            miniNext = FindViewById<ImageButton>(Resource.Id.miniNext);

            if (miniPrev != null) miniPrev.Click += (s, e) => MusicPlayer.Prev();
            if (miniNext != null) miniNext.Click += (s, e) => MusicPlayer.Next();
            if (miniPlay != null) miniPlay.Click += (s, e) => MusicPlayer.Toggle();
            if (musicMiniBar != null)
                musicMiniBar.Click += (s, e) =>
                {
                    try { StartActivity(new Intent(this, typeof(MusicActivity))); } catch { }
                };

            RefreshMiniBar();
        }

        private void OnMiniState() => RefreshMiniBar();
        private void OnMiniSong() => RefreshMiniBar();
        private void OnMiniTick(int _) => UpdateMiniLyrics();

        private void RefreshMiniBar()
        {
            if (musicMiniBar == null) return;
            var s = MusicPlayer.Current;
            if (s == null)
            {
                // 没有在播也让底栏常驻：随机挑一首「待播」曲目（优先播放列表，其次本地曲库），
                // 用户按播放键才真正开始播放。
                MusicPlayer.EnsurePending();
                s = MusicPlayer.Current;
            }
            if (s == null)
            {
                if (musicMiniBar.Visibility != ViewStates.Gone) musicMiniBar.Visibility = ViewStates.Gone;
                return;
            }
            if (musicMiniBar.Visibility != ViewStates.Visible) musicMiniBar.Visibility = ViewStates.Visible;

            miniTitle.Text = s.DisplayTitle;
            if (miniPlay != null)
                miniPlay.SetImageResource(MusicPlayer.IsPlaying
                    ? Android.Resource.Drawable.IcMediaPause
                    : Android.Resource.Drawable.IcMediaPlay);

            // 暗色模式下 ImageButton 的 android:tint 不会被 ThemeEngine 翻色，
            // 这里手动按当前主题给三个按钮着色，避免深色图标压在深色底栏上看不见。
            var miniIconColor = ThemeManager.IsNight(this)
                ? Android.Graphics.Color.Argb(255, 0xEA, 0xEA, 0xEA)
                : Android.Graphics.Color.Argb(255, 0x11, 0x11, 0x11);
            var miniMode = Android.Graphics.PorterDuff.Mode.SrcIn;
            if (miniPrev != null) miniPrev.SetColorFilter(miniIconColor, miniMode);
            if (miniPlay != null) miniPlay.SetColorFilter(miniIconColor, miniMode);
            if (miniNext != null) miniNext.SetColorFilter(miniIconColor, miniMode);

            if (miniCover != null)
            {
                var bmp = MusicLibrary.LoadCover(s, 96);
                if (bmp != null)
                {
                    miniCover.SetImageBitmap(MakeRounded(bmp, 8));
                }
                else if (!string.IsNullOrEmpty(AhoChatA.Music.SongArt.Url(s)))
                {
                    // 一起听 / 同步听的远程封面：先给灰色音符占位，后台异步取图
                    miniCover.SetImageBitmap(MusicActivity.PlaceholderBitmap(true));
                    AhoChatA.Music.SongArt.LoadInto(miniCover, s, 96);
                }
                else
                {
                    // 无封面统一显示「灰底 + 音乐符号」，不再显示程序图标
                    miniCover.SetImageBitmap(MusicActivity.PlaceholderBitmap(true));
                }
            }

            // 歌词文件可能在播放后异步落盘（在线歌曲）：key 里带上 LrcPath，
            // 这样歌词一到位就会重新解析，而不是一直记着「这首没歌词」。
            string lrcKey = s.Path + "|" + (s.LrcPath ?? "");
            if (_miniLyricKey != lrcKey)
            {
                _miniLyricKey = lrcKey;
                try { _miniLyrics = MusicLibrary.LoadLyrics(s); } catch { _miniLyrics = null; }
            }
            UpdateMiniLyrics();
        }

        private void UpdateMiniLyrics()
        {
            if (miniLyric0 == null) return;
            var s = MusicPlayer.Current;
            // 同步听：底栏歌词同样跟权威时间轴，避免跟随端 seek 校正时闪跳
            int pos = SyncListen.InSync ? SyncListen.DisplayPosition : MusicPlayer.Position;
            string l0 = "", l1 = "", l2 = "";
            if (s != null && _miniLyrics != null && _miniLyrics.IsValid)
            {
                int idx = _miniLyrics.LineAt(pos);
                if (idx < 0)
                {
                    l0 = "";
                    l1 = _miniLyrics.Lines.Count > 0 ? _miniLyrics.Lines[0].Text : "";
                    l2 = _miniLyrics.Lines.Count > 1 ? _miniLyrics.Lines[1].Text : "";
                }
                else
                {
                    // 双语歌词：同时间戳组内的译文与原文并列在同一行，后续行跳过整组
                    int gs = _miniLyrics.GroupStart(idx);
                    int ge = _miniLyrics.GroupEnd(idx);
                    var sb = new System.Text.StringBuilder(_miniLyrics.Lines[gs].Text);
                    for (int k = gs + 1; k <= ge; k++)
                    {
                        var tx = _miniLyrics.Lines[k].Text;
                        if (!string.IsNullOrEmpty(tx)) sb.Append(" / ").Append(tx);
                    }
                    l0 = sb.ToString();
                    l1 = ge + 1 < _miniLyrics.Lines.Count ? _miniLyrics.Lines[ge + 1].Text : "";
                    l2 = ge + 2 < _miniLyrics.Lines.Count ? _miniLyrics.Lines[ge + 2].Text : "";
                }
            }
            else if (s != null)
            {
                l0 = s.ArtistLine;
            }
            miniLyric0.Text = l0;
            miniLyric1.Text = l1;
            miniLyric2.Text = l2;
        }

        // 把封面位图裁成圆角，不依赖 ImageView.ClipToOutline（部分 OEM ROM 不生效）。
        private static Bitmap MakeRounded(Bitmap src, int radiusDp)
        {
            if (src == null) return null;
            int w = src.Width, h = src.Height;
            if (w <= 0 || h <= 0) return src;
            float density = Android.Content.Res.Resources.System.DisplayMetrics.Density;
            float r = radiusDp * density;
            Bitmap outBmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
            using (var c = new Canvas(outBmp))
            using (var paint = new Paint { AntiAlias = true, Dither = true })
            using (var path = new Path())
            {
                path.AddRoundRect(new RectF(0, 0, w, h), r, r, Path.Direction.Cw);
                c.ClipPath(path);
                c.DrawBitmap(src, 0, 0, paint);
            }
            return outBmp;
        }
    }
}

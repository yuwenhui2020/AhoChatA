using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net.Wifi;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using Java.Lang;
using ViewGroup = Android.Views.ViewGroup;
using ManifestPermission = Android.Manifest.Permission;
using Exception = System.Exception;

namespace AhoChatA
{
    /// <summary>
    /// 信号探测仪：分开探测 WiFi 与蓝牙信号。
    /// - WiFi：列出周边 AP 的 SSID、信号强度(dBm+格数)、信道、频段、带宽、加密方式、BSSID，
    ///         并画出 2.4GHz 信道占用分布图（参考开源 WiFiAnalyzer 的思路）。
    /// - 蓝牙：经典蓝牙（已配对/发现）与 BLE 广播分开列出，各自显示信号强度。
    ///
    /// 权限：Manifest 已声明全部所需权限；运行时按 API 级别申请定位/附近设备/蓝牙扫描权限。
    /// </summary>
    [Activity(Label = "@string/act_signal", Exported = false, Theme = "@style/AppTheme.NoActionBar",
              ScreenOrientation = ScreenOrientation.Portrait)]
    public class SignalProbeActivity : Activity
    {
        /// <summary>iOS 风格色板（日/夜两套）。字段名保持与界面引用一致，切换主题时按 ThemeManager 重建。</summary>
        private sealed class SignalPalette
        {
            public Color PageBg, CardBg, TextMain, TextSub, Accent, Good, Warn, Bad, ChipOff, Separator;
            public Color BarsOff;              // 未点亮信号格
            public int FillTopA, FillTopB;     // 频谱渐变的两个 ARGB（顶/底）

            public static SignalPalette FromTheme(Context ctx)
            {
                bool night = ThemeManager.IsNight(ctx);
                var p = new SignalPalette();
                if (night)
                {
                    p.PageBg = Color.Rgb(28, 28, 30);          // iOS secondarySystemGroupedBackground
                    p.CardBg = Color.Rgb(44, 44, 46);          // secondarySystemBackground
                    p.TextMain = Color.Rgb(242, 242, 247);     // label
                    p.TextSub = Color.Rgb(152, 152, 157);      // secondaryLabel
                    p.Accent = Color.Rgb(10, 132, 255);        // systemBlue (dark)
                    p.Good = Color.Rgb(48, 209, 88);           // systemGreen (dark)
                    p.Warn = Color.Rgb(255, 179, 64);          // systemOrange (dark)
                    p.Bad = Color.Rgb(255, 105, 97);           // systemRed (dark)
                    p.ChipOff = Color.Rgb(58, 58, 60);         // systemGray5 (dark)
                    p.Separator = Color.Rgb(72, 72, 74);       // separator (dark)
                    p.BarsOff = Color.Rgb(90, 90, 92);
                    p.FillTopA = Color.Argb(130, 48, 209, 88);
                    p.FillTopB = Color.Argb(20, 10, 132, 255);
                }
                else
                {
                    p.PageBg = new Color(IosStyle.Background);        // systemGroupedBackground
                    p.CardBg = new Color(IosStyle.Surface);                     // systemBackground
                    p.TextMain = new Color(IosStyle.Text);         // label
                    p.TextSub = new Color(IosStyle.Secondary);       // secondaryLabel
                    p.Accent = new Color(IosStyle.Accent);          // systemBlue
                    p.Good = Color.Rgb(52, 199, 89);            // systemGreen
                    p.Warn = Color.Rgb(255, 149, 0);            // systemOrange
                    p.Bad = Color.Rgb(255, 59, 48);             // systemRed
                    p.ChipOff = Color.Rgb(238, 238, 243);       // systemGray6
                    p.Separator = new Color(IosStyle.Separator);     // separator
                    p.BarsOff = Color.Rgb(209, 209, 214);
                    p.FillTopA = Color.Argb(140, 52, 199, 89);
                    p.FillTopB = Color.Argb(20, 0, 122, 255);
                }
                return p;
            }
        }

        private SignalPalette _pal;
        private bool _paletteNight;
        private const string Tag = "MchatSignal";
        private const int PermReq = 9201;

        private WifiManager _wifi;
        private BluetoothAdapter _bt;
        private BluetoothLeScanner _bleScanner;
        private BleScanCb _bleCb;
        private Dictionary<string, BtInfo> _bleFound;   // BLE 实时结果，供循环重绘

        private LinearLayout _wifiList, _btClassicList, _btBleList;
        private TextView _wifiStatus, _btStatus, _wifiDetail;
        private ChannelGraphView _channelGraph;
        private Button _btnWifi, _btnBt;
        private ScrollView _wifiPage, _btPage;

        private bool _wifiMode = true;
        private bool _wifiScanning, _btScanning;
        private bool _permissionsReady;
        private Handler _handler;
        private readonly List<ApInfo> _aps = new List<ApInfo>();

        // 当前连接实时信息（RSSI 变化广播 + 高频重绘，不依赖扫描缓存）
        private int _connectedLinkSpeed;
        private int _connectedIp;
        private int _connectedNetId = -1;
        private TextView _connectedSigText;
        private SignalBarsView _connectedBars;
        private ApInfo _connectedApRef;

        // WiFi 过滤 / 排序状态
        private EditText _search;
        private Spinner _sortSpinner;
        private CheckBox _hideHidden;
        private readonly List<Button> _bandChips = new List<Button>();
        private readonly List<Button> _secChips = new List<Button>();
        private string _filterText = "";
        private int _filterBand;                 // 0=全部, 24/5/6=对应频段
        private string _filterSec = "all";       // all/open/wpa2/wpa3/wep
        private int _sortMode;                   // 0=信号强→弱 1=信号弱→强 2=信道 3=名称
        private bool _hideHiddenNetworks;
        private int _shownCount;

        // 实时循环：每 100ms 重绘 UI（10 次/秒），让当前连接的 dBm / 链路速率变化更跟手。
        // 注意：Android 9+ 系统级限制"真实 WiFi 扫描"频率（前台实测约每 8~30 秒一次），
        // 任何普通 App 都无法突破；但 RSSI_CHANGED 广播 + WifiInfo.getRssi() 是实时的，
        // 因此"已连接"那一行的数据可以做到接近实时刷新。这里如实区分两者，不伪造信号。
        // 扫描请求间隔 2000ms：一旦系统节流窗口过去，最多再等 2 秒就能发起下一次真实扫描
        // （原来是 4 秒），显著缩短"新数据出现 → 被应用看到"的延迟；被节流时 StartScan 返回
        // false，仅记日志不弹窗，不会造成额外开销。
        private Java.Lang.Runnable _realtimeTick;
        private bool _realtimeRunning;
        private long _lastScanRequestAt;           // 上次请求扫描的时间(ms)
        private long _lastScanDataAt;              // 上次读到新扫描数据的时间(ms)
        private long _lastMaxResultTs;             // 上次读到的最大 ScanResult.Timestamp(us)
        private bool _lastScanAccepted = true;     // 上次 StartScan 是否被系统接受
        private int _connectedRssi = int.MinValue; // 当前连接 AP 的实时 RSSI
        private const long RealtimeIntervalMs = 100;
        private const long StartScanIntervalMs = 2000;

        // iOS 系统风格配色：代理到当前色板（日/夜），所有界面引用无需改动
        private Color PageBg { get { return _pal.PageBg; } }
        private Color CardBg { get { return _pal.CardBg; } }
        private Color TextMain { get { return _pal.TextMain; } }
        private Color TextSub { get { return _pal.TextSub; } }
        private Color Accent { get { return _pal.Accent; } }
        private Color Good { get { return _pal.Good; } }
        private Color Warn { get { return _pal.Warn; } }
        private Color Bad { get { return _pal.Bad; } }
        private Color ChipOff { get { return _pal.ChipOff; } }
        private Color Separator { get { return _pal.Separator; } }

        private sealed class ApInfo
        {
            public string Ssid, Bssid, Caps;
            public int Level, Freq;
            public bool Connected;

            // ScanResult 扩展字段（API 21+ / 23+ / 30+）
            public long TimestampUs;                 // 本 AP 扫描时间戳(us)
            public int ChannelWidth = -1;           // ScanResult.ChannelWidth，-1=未知
            public int CenterFreq0, CenterFreq1;    // 中心频点(MHz)，用于 40/80/160MHz 绑定
            public int WifiStandard = -1;           // ScanResult.WifiStandard，-1=未知
            public bool Ieee80211mc;                // 是否支持 802.11mc RTT 测距

            public int LinkSpeed;                    // 仅已连接：协商链路速率(Mbps)
            public string Ip;                        // 仅已连接：本机 IP
            public int NetworkId = -1;               // 仅已连接：网络 ID
        }

        private sealed class BtInfo
        {
            public string Name, Address;
            public int Rssi;
            public bool Ble;
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.Apply(this);
            base.OnCreate(savedInstanceState);
            _pal = SignalPalette.FromTheme(this);
            _paletteNight = ThemeManager.IsNight(this);
            _handler = new Handler(Looper.MainLooper);
            _wifi = (WifiManager)ApplicationContext.GetSystemService(Context.WifiService);
            _bt = BluetoothAdapter.DefaultAdapter;

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(PageBg);
            SetContentView(root);

            // 与其他原生页面共用工具栏与可伸展的标题，保留系统文字缩放。
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetPadding(Dp(4), 0, Dp(8), 0);
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetBackgroundColor(CardBg);
            bar.SetMinimumHeight(Dp(IosStyle.ToolbarHeight));
            var back = new FrameLayout(this) { ContentDescription = L10n.T("返回") };
            back.AddView(new IconView(this, IconKind.Back) { IconColor = Accent.ToArgb() },
                new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
            back.Click += (s, e) => Finish();
            bar.AddView(back, new LinearLayout.LayoutParams(Dp(IosStyle.ControlHeight), Dp(IosStyle.ControlHeight)));
            var title = new TextView(this) { Text = L10n.T("信号探测"), TextSize = IosStyle.TitleSize };
            title.SetTextColor(TextMain);
            title.SetTypeface(null, TypefaceStyle.Bold);
            bar.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var refresh = new TextView(this) { Text = L10n.T("扫描"), TextSize = IosStyle.CaptionSize, Gravity = GravityFlags.Center };
            refresh.SetTextColor(Accent);
            refresh.SetMinimumWidth(Dp(IosStyle.ControlHeight));
            refresh.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            refresh.Click += (s, e) => EnsurePermissions(() => { if (_wifiMode) ScanWifi(); else ScanBluetooth(); });
            bar.AddView(refresh);
            root.AddView(bar);

            // iOS 分段控件（Segmented Control）
            var segWrap = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            segWrap.SetPadding(Dp(16), Dp(8), Dp(16), Dp(10));
            var seg = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            SetRoundBg(seg, ChipOff, IosStyle.ControlRadius);
            seg.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
            _btnWifi = new Button(this) { Text = "Wi-Fi", TextSize = IosStyle.CaptionSize };
            _btnBt = new Button(this) { Text = L10n.T("蓝牙"), TextSize = IosStyle.CaptionSize };
            _btnWifi.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            _btnBt.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            _btnWifi.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            _btnBt.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            _btnWifi.Click += (s, e) => SwitchMode(true);
            _btnBt.Click += (s, e) => SwitchMode(false);
            seg.AddView(_btnWifi);
            seg.AddView(_btnBt);
            segWrap.AddView(seg, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            root.AddView(segWrap);

            // WiFi 页面
            _wifiPage = new ScrollView(this);
            var wp = new LinearLayout(this) { Orientation = Orientation.Vertical };
            wp.SetPadding(Dp(IosStyle.PageInset), Dp(4), Dp(IosStyle.PageInset), Dp(IosStyle.SectionGap));
            _wifiStatus = new TextView(this) { Text = L10n.T("尚未扫描"), TextSize = IosStyle.CaptionSize };
            _wifiStatus.SetTextColor(TextSub);
            _wifiStatus.SetPadding(Dp(4), 0, Dp(4), Dp(8));
            wp.AddView(_wifiStatus);

            // 分组卡片：信道频谱
            var graphCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
            SetRoundBg(graphCard, CardBg, IosStyle.CardRadius, Separator);
            graphCard.SetPadding(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset));
            _channelGraph = new ChannelGraphView(this, _pal);
            _channelGraph.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(150));
            graphCard.AddView(_channelGraph);
            _wifiDetail = new TextView(this) { TextSize = IosStyle.CaptionSize };
            _wifiDetail.SetTextColor(TextSub);
            _wifiDetail.SetPadding(Dp(2), Dp(6), Dp(2), Dp(2));
            graphCard.AddView(_wifiDetail);
            var graphLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            graphLp.SetMargins(0, 0, 0, Dp(IosStyle.SectionGap));
            graphCard.LayoutParameters = graphLp;
            wp.AddView(graphCard);

            wp.AddView(BuildFilterBar());

            // 分组卡片：Wi-Fi 列表（行间分隔线）
            var listCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
            SetRoundBg(listCard, CardBg, IosStyle.CardRadius, Separator);
            _wifiList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            listCard.AddView(_wifiList);
            wp.AddView(listCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _wifiPage.AddView(wp);
            root.AddView(_wifiPage, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            // 蓝牙页面
            _btPage = new ScrollView(this);
            var bp = new LinearLayout(this) { Orientation = Orientation.Vertical };
            bp.SetPadding(Dp(IosStyle.PageInset), Dp(4), Dp(IosStyle.PageInset), Dp(IosStyle.SectionGap));
            _btStatus = new TextView(this) { Text = L10n.T("尚未扫描"), TextSize = IosStyle.CaptionSize };
            _btStatus.SetTextColor(TextSub);
            _btStatus.SetPadding(Dp(4), 0, Dp(4), Dp(4));
            bp.AddView(_btStatus);
            bp.AddView(IosSectionLabel(L10n.T("经典蓝牙")));
            var btClassicCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
            SetRoundBg(btClassicCard, CardBg, IosStyle.CardRadius, Separator);
            _btClassicList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            btClassicCard.AddView(_btClassicList);
            bp.AddView(btClassicCard);
            bp.AddView(IosSectionLabel(L10n.T("BLE 广播设备")));
            var btBleCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
            SetRoundBg(btBleCard, CardBg, IosStyle.CardRadius, Separator);
            _btBleList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            btBleCard.AddView(_btBleList);
            bp.AddView(btBleCard);
            _btPage.AddView(bp);
            _btPage.Visibility = ViewStates.Gone;
            root.AddView(_btPage, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            SwitchMode(true);
            StartRealtimeLoop();
            EnsurePermissions(() => ScanWifi());
        }

        protected override void OnResume()
        {
            base.OnResume();
            IosStyle.ApplySystemBars(this);
            IosStyle.StyleCheckBoxes(this);
            // App 内切换主题（设置页/顶栏）或系统深色开关：重建界面以套用新色板。
            // 纯代码构建的界面重建最干净，避免逐个视图改色遗漏自绘 Paint/GradientDrawable。
            if (_pal != null && ThemeManager.IsNight(this) != _paletteNight)
                Recreate();
        }

        /// <summary>实时循环：每 400ms 重绘 UI；周期性触发 WiFi 扫描（如实反馈节流）。</summary>
        private void StartRealtimeLoop()
        {
            if (_realtimeRunning) return;
            _realtimeRunning = true;
            _realtimeTick = new Java.Lang.Runnable(() =>
            {
                if (!_realtimeRunning) return;
                try
                {
                    long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                    if (!_permissionsReady) { }
                    else if (_wifiMode)
                    {
                        // 读缓存 + 用 Timestamp 判断数据是否真的更新，并重绘
                        LoadWifiResultsSilent();
                        // 周期请求真实扫描（受系统节流，记录是否被接受）
                        if (now - _lastScanRequestAt >= StartScanIntervalMs)
                            RequestWifiScan(false);
                        UpdateWifiStatus(now);
                    }
                    else if (_bt != null && _bleScanner != null)
                    {
                        if (_bleFound != null)
                        {
                            RenderBle(_bleFound.Values);
                            _btStatus.Text = L10n.T("实时探测中 · BLE ") + _bleFound.Count + L10n.T(" 台 · 0.1 秒刷新");
                        }
                    }
                }
                catch { }
                _handler.PostDelayed(_realtimeTick, RealtimeIntervalMs);
            });
            _handler.PostDelayed(_realtimeTick, RealtimeIntervalMs);
        }

        private void StopRealtimeLoop()
        {
            _realtimeRunning = false;
            if (_realtimeTick != null) { try { _handler.RemoveCallbacks(_realtimeTick); } catch { } }
        }

        /// <summary>
        /// 实时循环用：只在扫描缓存出现新数据时重建列表；否则仅就地刷新当前连接的 dBm。
        /// 这样既跟得上系统节流后的新结果，又能以 100ms 粒度看到已连接链路的实时变化。
        /// </summary>
        private void LoadWifiResultsSilent()
        {
            try
            {
                var results = _wifi?.ScanResults;
                if (results != null)
                {
                    long maxTs = 0;
                    foreach (var r in results) if (r.Timestamp > maxTs) maxTs = r.Timestamp;
                    if (maxTs > _lastMaxResultTs)
                    {
                        LoadWifiIntoAps(results);   // 新数据：重建列表
                        return;
                    }
                }
                UpdateConnectedLive();              // 无新数据：仅就地刷新 dBm
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "读取扫描缓存失败：" + ex.Message);
            }
        }

        /// <summary>从 WifiInfo 读取实时 RSSI/链路信息，就地更新"已连接"行，避免整表重绘。</summary>
        private void UpdateConnectedLive()
        {
            if (_wifi == null) return;
            try
            {
                var conn = _wifi.ConnectionInfo;
                if (conn == null) return;
                _connectedRssi = conn.Rssi;
                _connectedLinkSpeed = conn.LinkSpeed;
                _connectedIp = conn.IpAddress;
                _connectedNetId = conn.NetworkId;
            }
            catch { return; }

            if (_connectedApRef != null)
                _connectedApRef.Level = _connectedRssi;

            if (_connectedSigText != null)
            {
                int bars = BarsOf(_connectedRssi);
                _connectedSigText.Text = _connectedRssi + " dBm";
                _connectedSigText.SetTextColor(SignalColor(bars));
                _connectedBars?.Set(bars, SignalColor(bars));
            }
        }

        /// <summary>RSSI_CHANGED / 网络状态广播回调：立即刷新当前连接实时信息。</summary>
        private void OnRssiChanged()
        {
            if (!_wifiMode) return;
            UpdateConnectedLive();
            UpdateWifiStatus(Java.Lang.JavaSystem.CurrentTimeMillis());
        }

        private bool _btScannedOnce;

        private void SwitchMode(bool wifi)
        {
            _wifiMode = wifi;
            _wifiPage.Visibility = wifi ? ViewStates.Visible : ViewStates.Gone;
            _btPage.Visibility = wifi ? ViewStates.Gone : ViewStates.Visible;
            StyleTab(_btnWifi, wifi);
            StyleTab(_btnBt, !wifi);
            // 首次切到蓝牙页时自动扫描（WiFi 由 OnCreate 触发，避免重复）
            if (!wifi && !_btScannedOnce)
            {
                _btScannedOnce = true;
                EnsurePermissions(() => ScanBluetooth());
            }
        }

        private void StyleTab(Button b, bool on)
        {
            if (on)
            {
                SetRoundBg(b, CardBg, IosStyle.ControlRadius);
                b.SetTextColor(TextMain);
            }
            else
            {
                b.SetBackgroundColor(Color.Transparent);
                b.SetTextColor(TextSub);
            }
        }

        /// <summary>iOS 分组列表的 1px 分隔线（左侧内缩，与文字对齐）。</summary>
        private View IosDivider()
        {
            var v = new View(this);
            v.SetBackgroundColor(Separator);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            lp.SetMargins(Dp(16), 0, 0, 0);
            v.LayoutParameters = lp;
            return v;
        }

        /* ================= 权限 ================= */

        private bool NeedPerms()
        {
            int api = (int)Build.VERSION.SdkInt;
            var need = new List<string>();
            if (api >= 31)
            {
                if (CheckSelfPermission(ManifestPermission.BluetoothScan) != Permission.Granted)
                    need.Add(ManifestPermission.BluetoothScan);
                if (CheckSelfPermission(ManifestPermission.BluetoothConnect) != Permission.Granted)
                    need.Add(ManifestPermission.BluetoothConnect);
            }
            if (api >= 33)
            {
                if (CheckSelfPermission("android.permission.NEARBY_WIFI_DEVICES") != Permission.Granted)
                    need.Add("android.permission.NEARBY_WIFI_DEVICES");
            }
            else
            {
                if (CheckSelfPermission(ManifestPermission.AccessFineLocation) != Permission.Granted)
                    need.Add(ManifestPermission.AccessFineLocation);
            }
            return need.Count > 0;
        }

        private void EnsurePermissions(Action onGranted)
        {
            _permAction = onGranted;
            if (!NeedPerms()) { _permissionsReady = true; _permAction = null; onGranted?.Invoke(); return; }
            _permissionsReady = false;
            var list = new List<string>();
            int api = (int)Build.VERSION.SdkInt;
            if (api >= 31)
            {
                list.Add(ManifestPermission.BluetoothScan);
                list.Add(ManifestPermission.BluetoothConnect);
            }
            if (api >= 33) list.Add("android.permission.NEARBY_WIFI_DEVICES");
            else list.Add(ManifestPermission.AccessFineLocation);
            RequestPermissions(list.ToArray(), PermReq);
        }

        private Action _permAction;

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode != PermReq) return;
            bool ok = grantResults != null && grantResults.Length > 0 && grantResults.All(g => g == Permission.Granted);
            _permissionsReady = ok;
            var action = _permAction;
            _permAction = null;
            if (ok) action?.Invoke();
            else
            {
                _wifiStatus.Text = L10n.T("需要定位/附近设备/蓝牙权限才能扫描信号");
                _btStatus.Text = L10n.T("需要蓝牙扫描权限才能扫描信号");
            }
        }

        /* ================= WiFi 扫描 ================= */

        private void ScanWifi()
        {
            if (_wifi == null) { _wifiStatus.Text = L10n.T("此设备没有 Wi-Fi"); return; }
            RegisterWifiReceiver();
            RequestWifiScan(true);
            // 兜底：即使收不到广播，也延时直接读一次缓存结果
            _handler.PostDelayed(() => { LoadWifiResults(); }, 2500);
        }

        /// <summary>统一请求系统扫描；manual=true 时给出即时反馈，并避免手动狂点。</summary>
        private void RequestWifiScan(bool manual)
        {
            if (_wifi == null) return;
            long now = Java.Lang.JavaSystem.CurrentTimeMillis();
            if (!manual && now - _lastScanRequestAt < StartScanIntervalMs) return;
            _lastScanRequestAt = now;
            try
            {
                if (!_wifi.IsWifiEnabled && manual)
                    _wifiStatus.Text = L10n.T("Wi-Fi 已关闭，部分机型需开启后才能扫描");
                bool started = _wifi.StartScan();
                _lastScanAccepted = started;
                Android.Util.Log.Info(Tag, "StartScan=" + started + " manual=" + manual);
                if (manual)
                    _wifiStatus.Text = started
                        ? L10n.T("已请求系统扫描，等待结果…")
                        : L10n.T("系统正在节流扫描，将显示最近一次结果");
            }
            catch (Exception ex)
            {
                _lastScanAccepted = false;
                Android.Util.Log.Warn(Tag, "StartScan 失败：" + ex.Message);
                if (manual) _wifiStatus.Text = L10n.T("扫描请求失败：") + ex.Message;
            }
        }

        private BroadcastReceiver _wifiReceiver;

        private void RegisterWifiReceiver()
        {
            try
            {
                if (_wifiReceiver != null) { try { UnregisterReceiver(_wifiReceiver); } catch { } }
                _wifiReceiver = new WifiScanReceiver(this);
                var filter = new IntentFilter(WifiManager.ScanResultsAvailableAction);
                // RSSI_CHANGED 随信标实时到达，用于快速刷新"已连接"行的 dBm / 链路速率
                filter.AddAction(WifiManager.RssiChangedAction);
                filter.AddAction(WifiManager.WifiStateChangedAction);
                RegisterReceiver(_wifiReceiver, filter);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "注册扫描广播失败：" + ex.Message);
            }
        }

        private sealed class WifiScanReceiver : BroadcastReceiver
        {
            private readonly SignalProbeActivity owner;
            public WifiScanReceiver(SignalProbeActivity o) { owner = o; }
            public override void OnReceive(Context context, Intent intent)
            {
                if (intent.Action == WifiManager.RssiChangedAction
                    || intent.Action == WifiManager.WifiStateChangedAction)
                {
                    owner.OnRssiChanged();
                    return;
                }
                // resultsUpdated=false 表示系统节流导致本次没有真正扫描，缓存是旧的
                bool updated = intent.GetBooleanExtra("resultsUpdated", false);
                owner.OnScanBroadcast(updated);
            }
        }

        private void OnScanBroadcast(bool updated)
        {
            _lastScanAccepted = updated;
            Android.Util.Log.Info(Tag, "SCAN_RESULTS_AVAILABLE updated=" + updated);
            LoadWifiResults();
        }

        private void LoadWifiResults()
        {
            try
            {
                var results = _wifi.ScanResults;
                LoadWifiIntoAps(results);
                _wifiScanning = false;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "读取扫描结果失败：" + ex.Message);
                _wifiScanning = false;
            }
        }

        /// <summary>把 ScanResults 解析到 _aps，并用 Timestamp 判断是否为真实新数据。</summary>
        private void LoadWifiIntoAps(IList<Android.Net.Wifi.ScanResult> results)
        {
            string curSsid = "", curBssid = "";
            try
            {
                var conn = _wifi.ConnectionInfo;
                if (conn != null)
                {
                    curSsid = (conn.SSID ?? "").Trim('"');
                    curBssid = conn.BSSID ?? "";
                    _connectedRssi = conn.Rssi;
                    _connectedLinkSpeed = conn.LinkSpeed;
                    _connectedIp = conn.IpAddress;
                    _connectedNetId = conn.NetworkId;
                }
            }
            catch { }

            int api = (int)Build.VERSION.SdkInt;
            var list = new List<ApInfo>();
            long maxTs = 0;
            if (results != null)
            {
                foreach (var r in results)
                {
                    if (r.Timestamp > maxTs) maxTs = r.Timestamp;
                    var ssid = string.IsNullOrEmpty(r.Ssid) ? L10n.T("(隐藏网络)") : r.Ssid;
                    bool hasBssid = !string.IsNullOrEmpty(curBssid) && curBssid != "02:00:00:00:00:00";
                    bool connected = hasBssid
                        ? string.Equals(curBssid, r.Bssid, StringComparison.OrdinalIgnoreCase)
                        : (!string.IsNullOrEmpty(curSsid) && ssid == curSsid);
                    var ap = new ApInfo
                    {
                        Ssid = ssid, Bssid = r.Bssid, Caps = r.Capabilities ?? "",
                        Level = r.Level, Freq = r.Frequency, Connected = connected,
                        TimestampUs = r.Timestamp
                    };
                    // 扩展字段按 API 级别读取，避免低版本上访问新属性
                    try
                    {
                        if (api >= 21)
                        {
                            ap.ChannelWidth = r.ChannelWidth;
                            ap.CenterFreq0 = r.CenterFreq0;
                            ap.CenterFreq1 = r.CenterFreq1;
                        }
                        if (api >= 23) ap.Ieee80211mc = r.Is80211mcResponder();
                        if (api >= 30) ap.WifiStandard = r.WifiStandard;
                    }
                    catch (System.Exception ex)
                    {
                        Android.Util.Log.Warn(Tag, "读取扩展扫描字段失败：" + ex.Message);
                    }
                    if (connected)
                    {
                        ap.LinkSpeed = _connectedLinkSpeed;
                        ap.Ip = FormatIp(_connectedIp);
                        ap.NetworkId = _connectedNetId;
                    }
                    list.Add(ap);
                }
            }
            if (maxTs > _lastMaxResultTs)
            {
                _lastMaxResultTs = maxTs;
                _lastScanDataAt = Java.Lang.JavaSystem.CurrentTimeMillis();
            }
            // 当前连接 AP 用 WifiInfo.getRssi() 的值：它随信标/链路质量刷新，比全扫描缓存更频繁，
            // 让"已连接"行的 dBm 真正跳动（系统真实扫描仍受节流，见状态栏提示）。
            if (_connectedRssi != int.MinValue)
                foreach (var ap in list)
                    if (ap.Connected) ap.Level = _connectedRssi;
            list.Sort((a, b) => b.Level.CompareTo(a.Level));
            _aps.Clear();
            _aps.AddRange(list);
            RenderWifi();
        }

        private static string FormatIp(int ip)
        {
            if (ip == 0) return "";
            return (ip & 0xFF) + "." + ((ip >> 8) & 0xFF) + "." + ((ip >> 16) & 0xFF) + "." + ((ip >> 24) & 0xFF);
        }

        /// <summary>状态栏：如实显示数据新鲜度、系统节流、当前连接实时 RSSI。</summary>
        private void UpdateWifiStatus(long now)
        {
            if (_wifiStatus == null) return;
            if (_aps.Count == 0 && _lastScanDataAt == 0)
            {
                _wifiStatus.Text = L10n.T("等待系统扫描结果…（首次扫描可能需要几秒）");
                return;
            }
            long age = _lastScanDataAt > 0 ? (now - _lastScanDataAt) / 1000 : -1;
            string fresh = age < 0 ? L10n.T("尚未获取") : (age <= 2 ? L10n.T("刚刚更新") : age + L10n.T(" 秒前更新"));
            string throttle = (!_lastScanAccepted && age > 6) ? L10n.T(" · 系统节流中") : "";
            string rssi = _connectedRssi != int.MinValue ? " · 当前 " + _connectedRssi + " dBm / " + _connectedLinkSpeed + " Mbps(实时)" : "";
            bool filtering = _filterBand != 0 || _filterSec != "all" || _hideHiddenNetworks
                             || !string.IsNullOrWhiteSpace(_filterText);
            string filt = filtering ? L10n.T(" · 过滤后 ") + _shownCount + "/" + _aps.Count : "";
            _wifiStatus.Text = _aps.Count + L10n.T(" 个 Wi-Fi") + filt + L10n.T(" · 数据") + fresh + throttle + rssi
                             + L10n.T("\n(真实扫描受系统限制约 8~30 秒/次；已连接行 0.1 秒刷新)");
        }

        private void RenderWifi()
        {
            if (_wifiList == null) return;
            var shown = ApplySort(_aps.Where(PassesFilter)).ToList();
            _shownCount = shown.Count;
            UpdateWifiStatus(Java.Lang.JavaSystem.CurrentTimeMillis());
            _wifiDetail.Text = BuildChannelSummary(_aps);
            _channelGraph.SetAps(_aps);
            _channelGraph.Invalidate();

            _wifiList.RemoveAllViews();
            _connectedApRef = null;
            _connectedSigText = null;
            _connectedBars = null;
            if (shown.Count == 0)
            {
                var empty = new TextView(this)
                {
                    Text = _aps.Count == 0 ? "暂无扫描结果" : "无符合过滤条件的 Wi-Fi",
                    TextSize = 14
                };
                empty.SetTextColor(TextSub);
                empty.Gravity = GravityFlags.Center;
                empty.SetPadding(Dp(16), Dp(24), Dp(16), Dp(24));
                _wifiList.AddView(empty);
                return;
            }
            for (int i = 0; i < shown.Count; i++)
            {
                if (i > 0) _wifiList.AddView(IosDivider());
                _wifiList.AddView(BuildApRow(shown[i]));
            }
        }

        /* ================= 过滤 / 排序 ================= */

        private bool PassesFilter(ApInfo ap)
        {
            if (_hideHiddenNetworks && ap.Ssid == "(隐藏网络)") return false;
            var t = (_filterText ?? "").Trim();
            if (t.Length > 0)
            {
                bool hit = (ap.Ssid ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                        || (ap.Bssid ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) return false;
            }
            if (_filterBand != 0 && !BandMatch(ap.Freq, _filterBand)) return false;
            if (_filterSec != "all" && !SecMatch(ap.Caps, _filterSec)) return false;
            return true;
        }

        private static bool BandMatch(int freq, int band)
        {
            if (band == 24) return freq >= 2400 && freq < 2500;
            if (band == 5) return freq >= 5000 && freq < 5950;
            if (band == 6) return freq >= 5950;
            return true;
        }

        private static bool SecMatch(string caps, string filter)
        {
            caps = caps ?? "";
            switch (filter)
            {
                case "open": return !caps.Contains("WPA") && !caps.Contains("WEP") && !caps.Contains("SAE");
                case "wep": return caps.Contains("WEP");
                case "wpa2": return caps.Contains("WPA2");
                case "wpa3": return caps.Contains("WPA3") || caps.Contains("SAE");
                default: return true;
            }
        }

        private IEnumerable<ApInfo> ApplySort(IEnumerable<ApInfo> src)
        {
            switch (_sortMode)
            {
                case 1: return src.OrderBy(a => a.Level);
                case 2: return src.OrderBy(a => ChannelOf(a.Freq)).ThenByDescending(a => a.Level);
                case 3: return src.OrderBy(a => a.Ssid, StringComparer.OrdinalIgnoreCase);
                default: return src.OrderByDescending(a => a.Level);
            }
        }

        private View BuildFilterBar()
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            SetRoundBg(box, CardBg, IosStyle.CardRadius, Separator);
            box.SetPadding(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset));
            var boxLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            boxLp.SetMargins(0, 0, 0, Dp(IosStyle.SectionGap));
            box.LayoutParameters = boxLp;

            // iOS 搜索框：圆角灰底 + 放大镜图标
            _search = new EditText(this) { Hint = L10n.T("搜索 SSID / BSSID"), TextSize = 14 };
            _search.SetSingleLine(true);
            SetRoundBg(_search, ChipOff, IosStyle.ControlRadius);
            _search.SetTextColor(TextMain);
            _search.SetHintTextColor(TextSub);
            _search.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
            try
            {
                var searchIcon = Resources.GetDrawable(Android.Resource.Drawable.IcMenuSearch);
                searchIcon.SetColorFilter(new Android.Graphics.PorterDuffColorFilter(TextSub, Android.Graphics.PorterDuff.Mode.SrcIn));
                _search.SetCompoundDrawablesWithIntrinsicBounds(searchIcon, null, null, null);
                _search.CompoundDrawablePadding = Dp(8);
            }
            catch { }
            _search.TextChanged += (s, e) => { _filterText = _search.Text; RenderWifi(); };
            _search.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            box.AddView(_search, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            int[] bandVals = { 0, 24, 5, 6 };
            box.AddView(BuildChipGroup(L10n.T("频段"), new[] { L10n.T("全部"), "2.4G", "5G", "6G" }, _bandChips,
                idx => _filterBand = bandVals[idx]));
            string[] secVals = { "all", "open", "wpa2", "wpa3", "wep" };
            box.AddView(BuildChipGroup(L10n.T("加密"), new[] { L10n.T("全部"), L10n.T("开放"), "WPA2", "WPA3", "WEP" }, _secChips,
                idx => _filterSec = secVals[idx]));

            var sortRow = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sortRow.SetGravity(GravityFlags.CenterVertical);
            sortRow.SetPadding(0, Dp(6), 0, 0);
            var slab = new TextView(this) { Text = L10n.T("排序"), TextSize = IosStyle.CaptionSize };
            slab.SetTextColor(TextMain);
            slab.SetPadding(0, 0, Dp(6), 0);
            sortRow.AddView(slab);
            _sortSpinner = new Spinner(this);
            var items = new[] { L10n.T("信号 强→弱"), L10n.T("信号 弱→强"), L10n.T("信道"), L10n.T("名称") };
            var ad = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, items);
            ad.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _sortSpinner.Adapter = ad;
            _sortSpinner.ItemSelected += (s, e) => { _sortMode = e.Position; RenderWifi(); };
            _sortSpinner.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            sortRow.AddView(_sortSpinner, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _hideHidden = new CheckBox(this) { Text = L10n.T("隐藏隐藏网络"), TextSize = IosStyle.CaptionSize };
            _hideHidden.SetTextColor(TextSub);
            _hideHidden.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            _hideHidden.CheckedChange += (s, e) => { _hideHiddenNetworks = e.IsChecked; RenderWifi(); };
            sortRow.AddView(_hideHidden);
            box.AddView(sortRow);

            return box;
        }

        private View BuildChipGroup(string title, string[] labels, List<Button> store, Action<int> pick)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(8), 0, 0);
            var lab = new TextView(this) { Text = title, TextSize = IosStyle.CaptionSize };
            lab.SetTextColor(TextSub);
            lab.SetPadding(0, 0, Dp(8), 0);
            row.AddView(lab);
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var b = new Button(this) { Text = labels[i], TextSize = IosStyle.CaptionSize };
                b.SetPadding(Dp(10), 0, Dp(10), 0);
                b.SetMinimumHeight(Dp(IosStyle.ControlHeight));
                b.SetMinimumWidth(Dp(IosStyle.ControlHeight));
                var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                lp.SetMargins(0, 0, Dp(6), 0);
                b.LayoutParameters = lp;
                b.Click += (s, e) => { pick(idx); RestyleChips(store, idx); RenderWifi(); };
                store.Add(b);
                row.AddView(b);
            }
            RestyleChips(store, 0);
            var hsv = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
            hsv.AddView(row);
            return hsv;
        }

        private void RestyleChips(List<Button> store, int sel)
        {
            for (int i = 0; i < store.Count; i++)
            {
                bool on = i == sel;
                SetRoundBg(store[i], on ? Accent : ChipOff, IosStyle.ControlRadius);
                store[i].SetTextColor(on ? Color.White : TextMain);
            }
        }

        private View BuildApRow(ApInfo ap)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            var name = new TextView(this)
            {
                Text = ap.Ssid + (ap.Connected ? "  ✓" : ""),
                TextSize = IosStyle.BodySize
            };
            name.SetTextColor(ap.Connected ? Accent : TextMain);
            top.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            int bars = BarsOf(ap.Level);
            var barsView = new SignalBarsView(this, _pal);
            barsView.Set(bars, SignalColor(bars));
            var blp = new LinearLayout.LayoutParams(Dp(24), Dp(16));
            blp.SetMargins(0, 0, Dp(8), 0);
            top.AddView(barsView, blp);
            var sig = new TextView(this) { Text = ap.Level + " dBm", TextSize = IosStyle.CaptionSize };
            sig.SetTextColor(SignalColor(bars));
            sig.Gravity = GravityFlags.End;
            top.AddView(sig);
            row.AddView(top);

            // 若正在重建"已连接"行，登记引用供 100ms 就地刷新
            if (ap.Connected)
            {
                _connectedApRef = ap;
                _connectedSigText = sig;
                _connectedBars = barsView;
            }

            int ch = ChannelOf(ap.Freq);
            string band = BandOf(ap.Freq);
            string secure = SecurityOf(ap.Caps);
            string width = WidthLabel(ap);
            string std = StandardLabel(ap);
            string extras = "";
            if (ap.Ieee80211mc) extras += " · RTT";
            if (ap.CenterFreq0 > 0) extras += L10n.T(" · 中心 ") + ap.CenterFreq0 + (ap.CenterFreq1 > 0 ? "/" + ap.CenterFreq1 : "") + "MHz";

            var info = new TextView(this)
            {
                Text = L10n.T("信道 ") + (ch > 0 ? ch.ToString() : "?") + " · " + band
                     + " · " + width + " · " + std + " · " + secure + extras,
                TextSize = IosStyle.CaptionSize
            };
            info.SetTextColor(TextSub);
            info.SetPadding(0, Dp(4), 0, 0);
            row.AddView(info);

            string detail = "BSSID " + (ap.Bssid ?? "") + " · " + ap.Freq + " MHz"
                          + L10n.T(" · 数据 ") + AgeLabel(ap.TimestampUs);
            if (ap.Connected)
                detail += L10n.T("\n链路 ") + ap.LinkSpeed + " Mbps · IP " + (string.IsNullOrEmpty(ap.Ip) ? "—" : ap.Ip)
                        + L10n.T(" · 网络 ID ") + ap.NetworkId;
            var detailView = new TextView(this) { Text = detail, TextSize = IosStyle.CaptionSize };
            detailView.SetTextColor(TextSub);
            detailView.SetPadding(0, Dp(2), 0, 0);
            row.AddView(detailView);

            return row;
        }

        /* ================= 蓝牙扫描 ================= */

        private void ScanBluetooth()
        {
            if (_bt == null) { _btStatus.Text = L10n.T("此设备没有蓝牙"); return; }
            _btScanning = true;
            _btStatus.Text = L10n.T("正在扫描蓝牙设备…");

            var classic = new List<BtInfo>();
            try
            {
                foreach (var d in _bt.BondedDevices)
                {
                    classic.Add(new BtInfo
                    {
                        Name = string.IsNullOrEmpty(d.Name) ? L10n.T("(未知设备)") : d.Name,
                        Address = d.Address,
                        Rssi = 0,
                        Ble = false
                    });
                }
            }
            catch { }
            RenderClassic(classic);

            // 经典蓝牙发现（RSSI 需配对或发现时才有）
            try
            {
                if (_bt.IsDiscovering) _bt.CancelDiscovery();
                RegisterBtReceiver(classic);
                _bt.StartDiscovery();
            }
            catch { }

            // BLE 持续扫描：回调实时更新 RSSI，实时循环每 400ms 重绘列表
            var bleFound = _bleFound ?? (_bleFound = new Dictionary<string, BtInfo>());
            try
            {
                if (_bleScanner == null)
                {
                    _bleCb = new BleScanCb(bleFound, null);
                    _bleScanner = _bt.BluetoothLeScanner;
                }
                if (_bleScanner != null)
                {
                    try { _bleScanner.StopScan(_bleCb); } catch { }
                    _bleScanner.StartScan(_bleCb);
                    _btStatus.Text = L10n.T("经典蓝牙 ") + classic.Count + L10n.T(" 台 · BLE 持续扫描中…");
                }
                else { _btScanning = false; }
            }
            catch (Exception ex)
            {
                _btStatus.Text = L10n.T("BLE 扫描失败：") + ex.Message;
                _btScanning = false;
            }
        }

        private BroadcastReceiver _btReceiver;

        private void RegisterBtReceiver(List<BtInfo> classic)
        {
            try
            {
                if (_btReceiver != null) { try { UnregisterReceiver(_btReceiver); } catch { } }
                _btReceiver = new BtDiscoverReceiver(classic, () => _handler.Post(() => RenderClassic(classic)));
                var filter = new IntentFilter();
                filter.AddAction(BluetoothDevice.ActionFound);
                filter.AddAction(BluetoothAdapter.ActionDiscoveryFinished);
                if ((int)Build.VERSION.SdkInt >= 33)
                    RegisterReceiver(_btReceiver, filter);
                else
                    RegisterReceiver(_btReceiver, filter);
            }
            catch { }
        }

        private sealed class BtDiscoverReceiver : BroadcastReceiver
        {
            private readonly List<BtInfo> _list;
            private readonly Action _changed;
            public BtDiscoverReceiver(List<BtInfo> list, Action changed) { _list = list; _changed = changed; }
            public override void OnReceive(Context context, Intent intent)
            {
                if (intent.Action == BluetoothDevice.ActionFound)
                {
                    var d = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
                    if (d == null) return;
                    int rssi = intent.GetShortExtra(BluetoothDevice.ExtraRssi, 0);
                    var existing = _list.FirstOrDefault(x => x.Address == d.Address);
                    if (existing != null) existing.Rssi = rssi;
                    else _list.Add(new BtInfo
                        {
                            Name = string.IsNullOrEmpty(d.Name) ? L10n.T("(未知设备)") : d.Name,
                            Address = d.Address, Rssi = rssi, Ble = false
                        });
                    _changed?.Invoke();
                }
            }
        }

        private sealed class BleScanCb : ScanCallback
        {
            private readonly Dictionary<string, BtInfo> _found;
            private readonly Action _changed;
            public BleScanCb(Dictionary<string, BtInfo> found, Action changed)
            {
                _found = found; _changed = changed;
            }
            public override void OnScanResult(ScanCallbackType callbackType, Android.Bluetooth.LE.ScanResult result)
            {
                try
                {
                    var d = result?.Device;
                    if (d == null) return;
                    string name = string.IsNullOrEmpty(d.Name) ? L10n.T("(未命名 BLE)") : d.Name;
                    if (!_found.ContainsKey(d.Address))
                    {
                        _found[d.Address] = new BtInfo { Name = name, Address = d.Address, Rssi = result.Rssi, Ble = true };
                        _changed?.Invoke();
                    }
                    else _found[d.Address].Rssi = result.Rssi;
                }
                catch { }
            }
        }

        private void RenderClassic(IEnumerable<BtInfo> list)
        {
            _btClassicList.RemoveAllViews();
            var items = list.OrderBy(x => x.Name).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) _btClassicList.AddView(IosDivider());
                _btClassicList.AddView(BuildBtRow(items[i]));
            }
        }

        private void RenderBle(IEnumerable<BtInfo> list)
        {
            _btBleList.RemoveAllViews();
            var items = list.OrderByDescending(x => x.Rssi).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) _btBleList.AddView(IosDivider());
                _btBleList.AddView(BuildBtRow(items[i]));
            }
        }

        private View BuildBtRow(BtInfo d)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));
            row.SetGravity(GravityFlags.CenterVertical);
            var name = new TextView(this) { Text = d.Name, TextSize = IosStyle.BodySize };
            name.SetTextColor(TextMain);
            row.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var rssi = new TextView(this)
            {
                Text = d.Ble ? (d.Rssi + " dBm") : (d.Rssi == 0 ? "已配对" : d.Rssi + " dBm"),
                TextSize = IosStyle.CaptionSize
            };
            rssi.SetTextColor(d.Ble ? SignalColor(BarsOf(d.Rssi)) : TextSub);
            row.AddView(rssi);
            return row;
        }

        /* ================= 工具：信道/信号/安全/带宽 ================= */

        /// <summary>信道带宽：优先用 ScanResult.ChannelWidth 真实值，回退到按频段推断。</summary>
        private static string WidthLabel(ApInfo ap)
        {
            // ChannelWidth: 0=20MHz 1=40MHz 2=80MHz 3=160MHz 4=80+80 5=320MHz(API33)
            switch (ap.ChannelWidth)
            {
                case 0: return "20 MHz";
                case 1: return "40 MHz";
                case 2: return "80 MHz";
                case 3: return "160 MHz";
                case 4: return "80+80 MHz";
                case 5: return "320 MHz";
            }
            return WidthOf(ap.Caps, ap.Freq); // 未知则用旧推断
        }

        /// <summary>WiFi 代际：来自 ScanResult.WifiStandard (API30+)，未知时按频段猜测。</summary>
        private static string StandardLabel(ApInfo ap)
        {
            // 1=Legacy 4=11n(WiFi4) 5=11ac(WiFi5) 6=11ax(WiFi6) 7=11ad 8=11be(WiFi7)
            switch (ap.WifiStandard)
            {
                case 1: return "WiFi 4?";
                case 4: return "WiFi 4";
                case 5: return "WiFi 5";
                case 6: return ap.Freq >= 5950 ? "WiFi 6E" : "WiFi 6";
                case 7: return "WiGig";
                case 8: return "WiFi 7";
            }
            if (ap.Freq >= 5950) return "6GHz";
            if (ap.Freq >= 5000) return "5GHz";
            return "2.4GHz";
        }

        /// <summary>把 ScanResult.Timestamp(us) 转成人类可读的数据年龄。</summary>
        private static string AgeLabel(long timestampUs)
        {
            if (timestampUs <= 0) return L10n.T("时间未知");
            long ageMs = System.Math.Max(0, SystemClock.ElapsedRealtime() - timestampUs / 1000);
            if (ageMs < 2000) return L10n.T("刚刚");
            if (ageMs < 60000) return (ageMs / 1000) + L10n.T(" 秒前");
            if (ageMs < 3600000) return (ageMs / 60000) + L10n.T(" 分钟前");
            return (ageMs / 3600000) + L10n.T(" 小时前");
        }

        private static int ChannelOf(int freq)
        {
            if (freq >= 2412 && freq <= 2472) return (freq - 2407) / 5;
            if (freq == 2484) return 14;
            if (freq >= 5000 && freq <= 5900) return (freq - 5000) / 5;
            if (freq >= 5950 && freq <= 7125) return (freq - 5950) / 5;
            return 0;
        }

        private static string BandOf(int freq)
        {
            if (freq >= 2400 && freq < 2500) return "2.4 GHz";
            if (freq >= 5000 && freq < 5950) return "5 GHz";
            if (freq >= 5950) return "6 GHz";
            return L10n.T("未知频段");
        }

        private static string SecurityOf(string caps)
        {
            caps = caps ?? "";
            bool wpa3 = caps.Contains("WPA3") || caps.Contains("SAE");
            bool wpa2 = caps.Contains("WPA2");
            if (wpa3 && wpa2) return "WPA2/WPA3";
            if (wpa3) return "WPA3";
            if (wpa2) return "WPA2";
            if (caps.Contains("WPA")) return "WPA";
            if (caps.Contains("WEP")) return "WEP";
            if (caps.Contains("OWE")) return L10n.T("OWE 增强开放");
            return L10n.T("开放");
        }

        private static string WidthOf(string caps, int freq)
        {
            // ScanResult.Capabilities 不含带宽字段；这里按频段给出常见默认，避免误导。
            if (freq >= 5000) return "20/80 MHz";
            return "20 MHz";
        }

        private static int BarsOf(int level)
        {
            if (level >= -55) return 4;
            if (level >= -67) return 3;
            if (level >= -78) return 2;
            if (level >= -89) return 1;
            return 0;
        }

        private Color SignalColor(int bars)
        {
            if (bars >= 3) return Good;
            if (bars == 2) return Warn;
            return Bad;
        }

        private string BuildChannelSummary(List<ApInfo> aps)
        {
            var g24 = aps.Where(a => a.Freq >= 2400 && a.Freq < 2500).ToList();
            var g5 = aps.Where(a => a.Freq >= 5000 && a.Freq < 5950).ToList();
            var g6 = aps.Where(a => a.Freq >= 5950).ToList();
            int rtt = aps.Count(a => a.Ieee80211mc);
            int ax = aps.Count(a => a.WifiStandard >= 6);
            string s = "2.4G " + g24.Count + " · 5G " + g5.Count + " · 6G " + g6.Count
                     + " · WiFi6+ " + ax + " · RTT " + rtt;
            if (g24.Count > 0)
            {
                var busiest = g24.GroupBy(a => ChannelOf(a.Freq))
                                 .OrderByDescending(g => g.Count())
                                 .Take(3)
                                 .Select(g => "ch" + g.Key + "(" + g.Count() + ")");
                s += L10n.T("\n2.4GHz 最拥挤：") + string.Join(" ", busiest);
            }
            return s;
        }

        private int Dp(int v) { return IosStyle.Dp(this, v); }

        /// <summary>iOS 圆角矩形背景（可带 1px 分隔线描边）。</summary>
        private GradientDrawable RoundRect(Color fill, int radiusDp, Color? stroke = null)
        {
            var d = new GradientDrawable();
            d.SetShape(ShapeType.Rectangle);
            d.SetColor(fill);
            d.SetCornerRadius(Dp(radiusDp));
            if (stroke.HasValue)
            {
                d.SetStroke(1, stroke.Value);
            }
            return d;
        }

        private void SetRoundBg(View v, Color fill, int radiusDp, Color? stroke = null)
        {
            v.SetBackgroundDrawable(RoundRect(fill, radiusDp, stroke));
        }

        /// <summary>iOS 列表分区标题（灰色大写小字）。</summary>
        private TextView IosSectionLabel(string text)
        {
            var t = new TextView(this) { Text = text, TextSize = IosStyle.CaptionSize };
            t.SetTextColor(TextSub);
            t.SetPadding(0, Dp(IosStyle.SectionGap), 0, Dp(8));
            return t;
        }

        protected override void OnDestroy()
        {
            StopRealtimeLoop();
            try { _handler?.RemoveCallbacksAndMessages(null); } catch { }
            _permAction = null;
            try { if (_bleScanner != null && _bleCb != null) _bleScanner.StopScan(_bleCb); } catch { }
            try { if (_bt != null && _bt.IsDiscovering) _bt.CancelDiscovery(); } catch { }
            try { if (_wifiReceiver != null) UnregisterReceiver(_wifiReceiver); } catch { }
            try { if (_btReceiver != null) UnregisterReceiver(_btReceiver); } catch { }
            base.OnDestroy();
        }

        /// <summary>4 格图形信号强度条：按 bars(0~4) 点亮对应格数，颜色随信号好坏变化。</summary>
        private sealed class SignalBarsView : View
        {
            private int _bars;
            private readonly Paint _on, _off;

            public SignalBarsView(Context c, SignalPalette pal) : base(c)
            {
                _on = new Paint(PaintFlags.AntiAlias);
                _off = new Paint(PaintFlags.AntiAlias) { Color = pal.BarsOff };
            }

            public void Set(int bars, Color color)
            {
                _bars = bars;
                _on.Color = color;
                Invalidate();
            }

            protected override void OnDraw(Canvas canvas)
            {
                base.OnDraw(canvas);
                int w = Width, h = Height;
                if (w <= 0 || h <= 0) return;
                float gap = w * 0.10f;
                float bw = (w - gap * 3) / 4f;
                float r = bw * 0.3f;
                for (int i = 0; i < 4; i++)
                {
                    float bh = h * (0.30f + 0.70f * (i / 3f));
                    float x = i * (bw + gap);
                    var rect = new RectF(x, h - bh, x + bw, h);
                    canvas.DrawRoundRect(rect, r, r, i < _bars ? _on : _off);
                }
            }
        }

        /// <summary>
        /// 2.4GHz 频谱曲线图：横轴频率(2400~2485MHz)，纵轴信号强度。
        /// 每个 AP 按其 20MHz 信道带宽把能量扩散到邻信道（平顶+滚降+旁瓣），
        /// 同频点取最大值叠加成一条连续曲线，直观展示 AP 对邻信道的干扰影响。
        /// </summary>
        private sealed class ChannelGraphView : View
        {
            private readonly List<ApInfo> _aps = new List<ApInfo>();
            private readonly Paint _line, _fill, _text, _grid, _refLine, _peak;
            private readonly Path _path = new Path();
            private const float FMin = 2400f, FMax = 2485f;
            private const float DbmMin = -100f, DbmMax = -20f;

            private readonly int _fillTopA, _fillTopB;

            public ChannelGraphView(Context c, SignalPalette pal) : base(c)
            {
                _line = new Paint(PaintFlags.AntiAlias) { Color = pal.Accent, StrokeWidth = 5 };
                _line.SetStyle(Paint.Style.Stroke);
                _fill = new Paint(PaintFlags.AntiAlias);
                _text = new Paint(PaintFlags.AntiAlias) { Color = pal.TextSub, TextSize = 20 };
                _grid = new Paint { Color = pal.Separator, StrokeWidth = 1 };
                _refLine = new Paint(PaintFlags.AntiAlias) { Color = pal.Warn, StrokeWidth = 2 };
                _refLine.SetPathEffect(new Android.Graphics.DashPathEffect(new float[] { 8, 6 }, 0));
                _peak = new Paint(PaintFlags.AntiAlias) { Color = pal.Bad, StrokeWidth = 3 };
                _fillTopA = pal.FillTopA;
                _fillTopB = pal.FillTopB;
            }

            public void SetAps(List<ApInfo> aps)
            {
                _aps.Clear();
                if (aps != null)
                    foreach (var ap in aps)
                        if (ap.Freq >= 2400 && ap.Freq < 2500) _aps.Add(ap); // 只画 2.4GHz 频段
            }

            private float YOf(float dbm, int h)
            {
                float top = 14f, bottom = h - 26f;
                float ratio = (dbm - DbmMin) / (DbmMax - DbmMin);
                ratio = System.Math.Max(0f, System.Math.Min(1f, ratio));
                return bottom - (bottom - top) * ratio;
            }

            private float XOf(float freq, int w)
            {
                return (freq - FMin) / (FMax - FMin) * w;
            }

            /// <summary>单个 AP 在频率 f 处的能量(dBm)：中心平顶 ±9MHz，9~13MHz 滚降，之外旁瓣衰减。</summary>
            private static float EnergyAt(ApInfo ap, float f)
            {
                float d = System.Math.Abs(f - ap.Freq);
                if (d <= 9f) return ap.Level;
                if (d <= 13f) return ap.Level - (d - 9f) * 2.2f;      // 滚降约 -9dB
                if (d <= 20f) return ap.Level - 9f - (d - 13f) * 2.6f; // 旁瓣继续衰减
                return -100f;
            }

            private float CurveAt(float f)
            {
                float best = DbmMin;
                foreach (var ap in _aps)
                {
                    float e = EnergyAt(ap, f);
                    if (e > best) best = e;
                }
                return best;
            }

            protected override void OnDraw(Canvas canvas)
            {
                base.OnDraw(canvas);
                int w = Width, h = Height;
                if (w <= 0 || h <= 0) return;
                canvas.DrawColor(Color.Transparent);

                // 背景：dBm 水平网格线
                for (int dbm = -90; dbm <= -30; dbm += 20)
                {
                    float y = YOf(dbm, h);
                    canvas.DrawLine(0, y, w, y, _grid);
                    canvas.DrawText(dbm + "", 4, y - 3, _text);
                }

                // 1/6/11 不重叠信道的橙色参考线，标注信道号
                int[] refCh = { 1, 6, 11 };
                foreach (int ch in refCh)
                {
                    float freq = 2412f + (ch - 1) * 5f;
                    float x = XOf(freq, w);
                    canvas.DrawLine(x, 10, x, h - 22, _refLine);
                    canvas.DrawText("ch" + ch, x + 3, 20, _text);
                }

                // 曲线采样（每 2px 一个点），叠加成连续频谱
                _path.Reset();
                float bottom = h - 22f;
                bool started = false;
                int peakX = 0;
                float peakDb = DbmMin;
                for (int px = 0; px <= w; px += 2)
                {
                    float freq = FMin + (float)px / w * (FMax - FMin);
                    float dbm = CurveAt(freq);
                    float y = YOf(dbm, h);
                    if (!started) { _path.MoveTo(px, bottom); _path.LineTo(px, y); started = true; }
                    else _path.LineTo(px, y);
                    if (dbm > peakDb) { peakDb = dbm; peakX = px; }
                }
                _path.LineTo(w, bottom);
                _path.Close();

                // 渐变填充 + 曲线
                _fill.SetStyle(Paint.Style.Fill);
                _fill.SetShader(new Android.Graphics.LinearGradient(
                    0, YOf(DbmMax, h), 0, bottom,
                    new int[] { _fillTopA, _fillTopB },
                    null, Android.Graphics.Shader.TileMode.Clamp));
                canvas.DrawPath(_path, _fill);
                _fill.SetShader(null);

                _path.Reset();
                started = false;
                for (int px = 0; px <= w; px += 2)
                {
                    float freq = FMin + (float)px / w * (FMax - FMin);
                    float y = YOf(CurveAt(freq), h);
                    if (!started) { _path.MoveTo(px, y); started = true; }
                    else _path.LineTo(px, y);
                }
                canvas.DrawPath(_path, _line);

                // 峰值标注
                if (peakDb > DbmMin)
                {
                    canvas.DrawCircle(peakX, YOf(peakDb, h), 6, _peak);
                    canvas.DrawText(((int)peakDb) + "dBm", System.Math.Max(2, peakX - 30),
                        System.Math.Max(14, YOf(peakDb, h) - 10), _peak);
                }

                // 信道号刻度（1~13）
                for (int ch = 1; ch <= 13; ch++)
                {
                    float x = XOf(2412f + (ch - 1) * 5f, w);
                    canvas.DrawText(ch.ToString(), x - 5, h - 5, _text);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 通信渠道设置。
    ///
    /// 上方是**本机连接信息**（实时刷新：局域网 IP、Wi-Fi、蓝牙名、各端口、设备 id），
    /// 下方是**渠道开关**：局域网直连 / qaul（网络消息·中继·局域网发现）/ WLAN Direct / 蓝牙。
    /// 再下面是当前发现的设备与「手动输入 IP 直连」。
    ///
    /// 这里是把原来散落在聊天菜单里的一堆开关收拢到一起——用户不需要知道
    /// "哪个菜单项对应哪条链路"，只管开不开。
    /// </summary>
    [Activity(Label = "@string/act_channel", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class ChannelSettingsActivity : BaseActivity
    {
        private static readonly Color BgColor = new Color(IosStyle.Background);
        private static readonly Color CardColor = new Color(IosStyle.Surface);
        private static readonly Color TextColor = new Color(IosStyle.Text);
        private static readonly Color SubColor = new Color(IosStyle.Secondary);
        private static readonly Color LineColor = new Color(IosStyle.Separator);
        private static readonly Color AccentColor = new Color(IosStyle.Accent);

        // 使用标准 iOS 间距。此前这里的 SpacingScale=0.3 把页边距/行距/列宽
        // 全部压到 30%，导致文字贴边、左侧 112dp 的键列被压成 33dp 而换行、
        // 与值文字重叠、行距错乱。不要再压缩，各个调用点本身已是紧凑值。
        private int SDp(float v) => IosStyle.Dp(this, v);

        private MchatBridge _bridge;
        private LinearLayout _infoBox, _peerBox, _healthBox;
        private Switch _swLan, _swQaul, _swP2p, _swBt, _swListen;
        private TextView _btHint;
        private Timer _timer;
        private bool _destroyed;
        private bool _binding;
        private Action _refreshRelays;        // 已添加中转站列表刷新
        private Action _refreshLocalRelay;    // 本机中转地址/端口文案刷新（随网络变化）

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            _bridge = CoreHost.Bridge;
            if (_bridge == null)
            {
                L10n.Toast(this, L10n.T("后台服务未就绪"), ToastLength.Short).Show();
                Finish();
                return;
            }

            BuildUi();
            BindStates();
            RefreshInfo();
            RefreshPeers();

            _timer = new Timer(_ => RunOnUiThread(() =>
            {
                if (_destroyed || IsFinishing) return;
                RefreshInfo();
                RefreshPeers();
                if (ThemeManager.IsNight(this)) ApplyThemeLive();
            }), null, 2000, 2000);
        }

        protected override void OnDestroy()
        {
            _destroyed = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            base.OnDestroy();
        }

        /* ===================== 界面 ===================== */

        private void BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(BgColor);

            // 顶栏
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            var palette = IosStyle.Colors(this);
            bar.SetBackgroundColor(new Color(palette.Surface));
            bar.SetPadding(SDp(4), 0, SDp(8), 0);

            var back = new FrameLayout(this) { ContentDescription = L10n.T("返回") };
            back.LayoutParameters = new LinearLayout.LayoutParams(SDp(44), SDp(44));
            var backIcon = new IconView(this, IconKind.Back) { IconColor = unchecked((int)0xFF111111) };
            back.AddView(backIcon, new FrameLayout.LayoutParams(SDp(24), SDp(24)) { Gravity = GravityFlags.Center });
            back.Click += (s, e) => Finish();
            bar.AddView(back);

            var title = new TextView(this) { Text = L10n.T("通信渠道设置") };
            title.TextSize = IosStyle.TitleSize;
            title.SetTextColor(TextColor);
            title.SetTypeface(null, TypefaceStyle.Bold);
            title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            bar.AddView(title);

            var refresh = new TextView(this) { Text = L10n.T("刷新") };
            refresh.TextSize = IosStyle.CaptionSize;
            refresh.SetTextColor(AccentColor);
            refresh.Gravity = GravityFlags.Center;
            refresh.SetMinimumWidth(SDp(IosStyle.ControlHeight));
            refresh.SetMinimumHeight(SDp(IosStyle.ControlHeight));
            refresh.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            refresh.Click += (s, e) => { RefreshInfo(); RefreshPeers(); if (ThemeManager.IsNight(this)) ApplyThemeLive(); };
            bar.AddView(refresh);

            bar.SetMinimumHeight(SDp(IosStyle.ToolbarHeight));
            root.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var scroll = new ScrollView(this);
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.SetPadding(SDp(IosStyle.PageInset), SDp(10), SDp(IosStyle.PageInset), SDp(12));

            // 本机连接信息
            body.AddView(SectionTitle(L10n.T("本机连接信息")));
            _infoBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var infoCard = Card(_infoBox);
            body.AddView(infoCard);

            // 渠道实时状态（收/发/中继/丢弃 + 最近活动）——每条链路一行，2 秒刷新
            body.AddView(SectionTitle(L10n.T("渠道实时状态")));
            _healthBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var healthCard = Card(_healthBox);
            body.AddView(healthCard);

            // 渠道开关
            body.AddView(SectionTitle(L10n.T("通信渠道")));
            var swBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var swCard = Card(swBox);

            _swLan = AddSwitch(swBox, L10n.T("局域网（IP直连）"),
                L10n.T("同一 Wi-Fi 下设备之间直接收发，不经过任何中继（最快）"), true);
            _swQaul = AddSwitch(swBox, L10n.T("加密网络（中继）"),
                L10n.T("主要用于公网/跨网通信：经公共节点中继，即使不在同一网络也能互通"), true);
            _swP2p = AddSwitch(swBox, L10n.T("WLAN Direct（点对点直连）"),
                L10n.T("没有路由器时设备之间点对点直连（部分机型需系统支持）"), false);
            _swBt = AddSwitch(swBox, L10n.T("蓝牙（近场直连）"),
                L10n.T("本 App 自带的蓝牙通信渠道，无网时近距离互连；仅在本 App 之间使用"), false);
            _btHint = new TextView(this) { Text = "" };
            _btHint.TextSize = IosStyle.CaptionSize;
            _btHint.SetTextColor(SubColor);
            _btHint.SetPadding(SDp(4), 0, SDp(4), SDp(6));
            swBox.AddView(_btHint);

            _swLan.CheckedChange += (s, e) => { if (!_binding) { _bridge.SetLanEnabled(e.IsChecked); } };
            _swQaul.CheckedChange += (s, e) => { if (!_binding) { _bridge.SetQaulEnabled(e.IsChecked); } };
            _swP2p.CheckedChange += (s, e) =>
            {
                if (_binding) return;
                if (e.IsChecked) _bridge.EnableP2p(); else _bridge.DisableP2p();
            };
            _swBt.CheckedChange += (s, e) =>
            {
                if (_binding) return;
                if (e.IsChecked) _bridge.EnableBitchat(); else _bridge.DisableBitchat();
                RefreshInfo();
            };

            body.AddView(swCard);

            // 播放分享：控制本机发出的音乐卡片是否附带「一起听」播放列表
            body.AddView(SectionTitle(L10n.T("播放分享")));
            var shareBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _swListen = AddSwitch(shareBox, L10n.T("一起听（音乐）"),
                L10n.T("开启后本机播放音乐时，会自动分享播放列表连同封面/歌词一起发给局域网设备（mesh频道），对方可点卡片加入一起听"), true);
            _swListen.CheckedChange += (s, e) =>
            {
                if (_binding) return;
                ListenTogether.Enabled = e.IsChecked;
            };
            body.AddView(Card(shareBox));

            // 外观：日间 / 夜间 / 跟随系统（与网页端、原生界面统一）
            body.AddView(SectionTitle(L10n.T("外观")));
            var themeBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            themeBox.AddView(BuildThemeChooser());
            body.AddView(Card(themeBox));

            // 设备发现
            body.AddView(SectionTitle(L10n.T("已发现的设备（局域网自动发现）")));
            _peerBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.AddView(Card(_peerBox));

            // 中转站（IPv6 / 自定义端口）
            body.AddView(SectionTitle(L10n.T("中转站（IPv6 / 自定义端口）")));
            var relayCard = new LinearLayout(this) { Orientation = Orientation.Vertical };

            // 本机中转地址：IPv4 / IPv6 各一行，点击复制。
            // IPv6 统一显示为 [地址]:端口（浏览器兼容写法，且能与端口区分）。
            var local4 = new TextView(this) { TextSize = IosStyle.CaptionSize };
            local4.SetTextColor(TextColor);
            local4.SetPadding(SDp(4), SDp(6), SDp(4), SDp(6));
            local4.SetMinimumHeight(SDp(IosStyle.RowMinHeight));
            local4.Click += (s, e) =>
                CopyToClipboard(MchatBridge.FormatHostPort(DeviceStatus.Ip, _bridge.LocalTcpPort));
            relayCard.AddView(local4);

            var local6 = new TextView(this) { TextSize = IosStyle.CaptionSize };
            local6.SetTextColor(TextColor);
            local6.SetPadding(SDp(4), SDp(6), SDp(4), SDp(6));
            local6.SetMinimumHeight(SDp(IosStyle.RowMinHeight));
            local6.Click += (s, e) =>
            {
                var ip6 = DeviceStatus.Ipv6;
                if (ip6 == "::" || string.IsNullOrEmpty(ip6))
                {
                    L10n.Toast(this, L10n.T("当前没有可用的 IPv6 地址"), ToastLength.Short).Show();
                    return;
                }
                CopyToClipboard(MchatBridge.FormatHostPort(ip6, _bridge.LocalTcpPort));
            };
            relayCard.AddView(local6);

            var portRow = new TextView(this) { TextSize = IosStyle.CaptionSize };
            portRow.SetMinimumHeight(SDp(IosStyle.RowMinHeight));
            portRow.SetTextColor(SubColor);
            portRow.SetPadding(SDp(4), SDp(6), SDp(4), SDp(6));
            portRow.Click += (s, e) => ShowSetListenPort();
            relayCard.AddView(portRow);

            // 已添加的中转站（支持多个）
            var relayList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            relayCard.AddView(relayList);

            var addRelay = new TextView(this) { Text = L10n.T("＋ 添加中转站（粘贴 IP:端口 自动识别）"), TextSize = IosStyle.BodySize };
            addRelay.SetTextColor(AccentColor);
            addRelay.SetMinimumHeight(SDp(IosStyle.RowMinHeight));
            addRelay.SetPadding(SDp(4), SDp(6), SDp(4), SDp(6));
            addRelay.Click += (s, e) => ShowManualConnect();
            relayCard.AddView(addRelay);

            var relayNote = new TextView(this)
            {
                Text = L10n.T("本机中转默认开启：其他设备复制本机地址即可经本机中转联机\n")
                     + L10n.T("添加中转站时直接粘贴复制来的「IP:端口」即可\n（IPv6 形如 [fd00::1]:45778），可添加多个，断线自动重连。")
            };
            relayNote.TextSize = IosStyle.CaptionSize;
            relayNote.SetTextColor(SubColor);
            relayNote.SetPadding(SDp(4), 0, SDp(4), SDp(6));
            relayCard.AddView(relayNote);
            body.AddView(Card(relayCard));

            Action refreshLocal = () =>
            {
                try
                {
                    int port = _bridge.LocalTcpPort;
                    local4.Text = L10n.T("本机IPv4中转地址：\n") + MchatBridge.FormatHostPort(DeviceStatus.Ip, port);

                    var ip6 = DeviceStatus.Ipv6;
                    if (ip6 == "::" || string.IsNullOrEmpty(ip6))
                    {
                        local6.Text = L10n.T("本机IPv6中转地址：\n未获取");
                        local6.SetTextColor(SubColor);
                    }
                    else
                    {
                        local6.Text = L10n.T("本机IPv6中转地址：\n") + MchatBridge.FormatHostPort(ip6, port);
                        local6.SetTextColor(TextColor);
                    }

                    var pref = _bridge.PreferredTcpPort;
                    portRow.Text = L10n.T("监听端口：") + port
                                 + (pref > 0 ? L10n.T("（已指定 ") + pref + L10n.T("，重启后生效）") : L10n.T("（自动）"));
                    // qaul 中继按官方同一套机制承担，但端口各自独立：
                    //   qaul 消息 9199 / libp2p 中继 9200 / 本程序 TCP 中转 45778（官方 libp2p 用 9229）。
                    try
                    {
                        var st = Newtonsoft.Json.Linq.JObject.FromObject(_bridge.ChannelStatus());
                        if (st != null)
                        {
                            int qp = (int?)st["qaulPort"] ?? 0;
                            int np = (int?)st["nodePort"] ?? 0;
                            portRow.Text += L10n.T("\nqaul 消息端口：") + qp
                                          + L10n.T("  · libp2p 中继端口：") + np
                                          + L10n.T("  · 官方默认为 9229（本机不同）");
                        }
                    }
                    catch { }
                }
                catch { }
            };
            _refreshLocalRelay = refreshLocal;
            refreshLocal();

            Action refreshRelays = null;
            refreshRelays = () =>
            {
                relayList.RemoveAllViews();
                List<string> saved = new List<string>();
                try { saved = _bridge.GetStaticRelays(); } catch { }
                if (saved.Count == 0)
                {
                    var e0 = new TextView(this) { Text = L10n.T("暂无使用的中转站，可点击下方添加") };
                    e0.TextSize = IosStyle.CaptionSize;
                    e0.SetTextColor(SubColor);
                    e0.SetPadding(SDp(4), 0, SDp(4), SDp(6));
                    relayList.AddView(e0);
                    return;
                }
                foreach (var a in saved)
                {
                    var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    row.SetPadding(SDp(4), SDp(6), SDp(4), SDp(6));
                    var tv = new TextView(this) { Text = a };
                    tv.TextSize = IosStyle.CaptionSize;
                    tv.SetTextColor(TextColor);
                    // 不使用 weight=1，地址只占内容宽度，"移除" 按钮紧跟其后
                    tv.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                    tv.SetMinimumHeight(SDp(IosStyle.ControlHeight));
                    tv.Gravity = GravityFlags.CenterVertical;
                    tv.Click += (s2, e2) => CopyToClipboard(a);
                    var del = new TextView(this) { Text = L10n.T("移除") };
                    del.TextSize = IosStyle.CaptionSize;
                    del.SetTextColor(new Color(IosStyle.Danger));
                    del.SetMinimumWidth(SDp(IosStyle.ControlHeight));
                    del.SetMinimumHeight(SDp(IosStyle.ControlHeight));
                    del.Gravity = GravityFlags.Center;
                    del.SetPadding(SDp(10), SDp(4), SDp(10), SDp(4));
                    var addr = a;
                    del.Click += (s2, e2) =>
                    {
                        string hh; int pp;
                        MchatBridge.ParseHostPort(addr, out hh, out pp);
                        try { _bridge.RemoveStaticRelay(hh, pp); } catch { }
                        refreshRelays();
                    };
                    row.AddView(tv);
                    row.AddView(del);
                    relayList.AddView(row);
                }
            };
            _refreshRelays = refreshRelays;
            refreshRelays();

            // 手动直连
            var manual = new TextView(this)
            {
                Text = L10n.T("手动输入 IP 直连"),
                TextSize = 14,
                Gravity = GravityFlags.Center
            };
            manual.SetTextColor(AccentColor);
            manual.SetPadding(SDp(8), SDp(6), SDp(8), SDp(6));
            manual.SetMinimumHeight(SDp(IosStyle.RowMinHeight));
            manual.Background = IosStyle.Rounded(this, CardColor.ToArgb(), IosStyle.CardRadius);
            manual.Click += (s, e) => ShowManualConnect();
            body.AddView(manual, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var note = new TextView(this)
            {
                Text = L10n.T("说明：只要两台设备在同一 Wi-Fi（或其中一台开了热点），打开「局域网直连」就能互相发现，")
                     + L10n.T("不需要输入 IP。只有在自动发现被路由器隔离（AP 隔离 / 不同网段）时，才需要手动输入对方 IP。")
            };
            note.TextSize = IosStyle.CaptionSize;
            note.SetTextColor(SubColor);
            note.SetPadding(SDp(6), SDp(7), SDp(6), 0);
            body.AddView(note);

            scroll.AddView(body);
            root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            SetContentView(root);
        }

        private TextView SectionTitle(string text)
        {
            var t = new TextView(this) { Text = text };
            t.TextSize = IosStyle.CaptionSize;
            t.SetTextColor(SubColor);
            t.SetPadding(0, SDp(7), 0, SDp(3));
            return t;
        }

        private LinearLayout Card(View content)
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = IosStyle.GroupCardBackground(this);
            card.SetPadding(SDp(IosStyle.CardInset), SDp(5), SDp(IosStyle.CardInset), SDp(5));
            card.AddView(content);
            return card;
        }

        private Switch AddSwitch(LinearLayout box, string title, string desc, bool value)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, SDp(4), 0, SDp(4));
            row.SetMinimumHeight(SDp(40));

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var t = new TextView(this) { Text = title };
            t.TextSize = IosStyle.BodySize;
            t.SetTextColor(TextColor);
            col.AddView(t);
            var d = new TextView(this) { Text = desc };
            d.TextSize = IosStyle.CaptionSize;
            d.SetTextColor(SubColor);
            d.SetPadding(0, SDp(2), SDp(8), 0);
            col.AddView(d);

            // 不使用 weight=1，让左侧只占内容宽度，右侧 Switch 紧跟其后，符合 iOS 风格
            row.AddView(col, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            var sw = new Switch(this) { Checked = value };
            sw.SetMinimumWidth(SDp(IosStyle.ControlHeight));
            sw.SetMinimumHeight(SDp(IosStyle.ControlHeight));
            // Switch 右侧留出标准边距，保持与其他模块一致
            var swLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            swLp.RightMargin = SDp(IosStyle.CardInset);
            row.AddView(sw, swLp);

            box.AddView(row);
            var line = new View(this);
            line.SetBackgroundColor(LineColor);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            lp.LeftMargin = SDp(IosStyle.CardInset);
            box.AddView(line, lp);
            return sw;
        }

        private LinearLayout BuildThemeChooser()
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(SDp(2), SDp(2), SDp(2), SDp(2));
            row.Background = IosStyle.Rounded(this,
                IosStyle.Colors(this).Night ? IosStyle.NightElevated : unchecked((int)0xFFEFEFF4), IosStyle.ControlRadius);
            FillThemeChooser(row);
            return row;
        }

        // 重建分段控件（3 个按钮），点击后即时重刷配色而不是 Recreate——避免整页重建造成的闪烁。
        private void FillThemeChooser(LinearLayout row)
        {
            row.RemoveAllViews();
            var opts = new[]
            {
                new { key = ThemeManager.DAY, label = L10n.T("日间") },
                new { key = ThemeManager.NIGHT, label = L10n.T("夜间") },
                new { key = ThemeManager.AUTO, label = L10n.T("跟随系统") }
            };
            var current = ThemeManager.GetMode(this);
            foreach (var o in opts)
            {
                var b = new TextView(this) { Text = o.label, TextSize = IosStyle.CaptionSize, Gravity = GravityFlags.Center };
                b.SetPadding(SDp(8), SDp(8), SDp(8), SDp(8));
                b.SetMinimumHeight(SDp(IosStyle.ControlHeight - 4));
                var selected = string.Equals(o.key, current, StringComparison.Ordinal);
                b.Background = IosStyle.Rounded(this,
                    selected ? AccentColor.ToArgb() : Color.Transparent.ToArgb(), IosStyle.ControlRadius - 2);
                b.SetTextColor(selected ? Color.White : TextColor);
                b.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                b.Click += (s, e) =>
                {
                    if (string.Equals(ThemeManager.GetMode(this), o.key, StringComparison.Ordinal)) return;
                    ThemeManager.SetMode(this, o.key);
                    FillThemeChooser(row);   // 先按新选中项重建按钮（此时是日间色）
                    ApplyThemeLive();        // 再按当前模式统一上色（夜间会把白底刷成深色）
                };
                row.AddView(b);
            }
        }

        /* ===================== 数据刷新 ===================== */

        private void BindStates()
        {
            _binding = true;
            try
            {
                _swLan.Checked = _bridge.LanEnabled;
                _swQaul.Checked = _bridge.QaulEnabled;
                _swP2p.Checked = _bridge.P2pEnabled;
                _swBt.Checked = _bridge.BitchatEnabled;
                _swListen.Checked = ListenTogether.Enabled;
            }
            catch { }
            _binding = false;
        }

        private void RefreshInfo()
        {
            if (_infoBox == null) return;
            Dictionary<string, string> info;
            try { info = _bridge.ConnectionInfo(); }
            catch { return; }

            _infoBox.RemoveAllViews();
            foreach (var kv in info)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                // 纯信息展示行用紧凑行高，避免 13sp 小字被 46dp 行高撑出大片空白
                row.SetPadding(0, SDp(3), 0, SDp(3));
                row.SetMinimumHeight(SDp(32));

                var k = new TextView(this) { Text = kv.Key };
                k.TextSize = IosStyle.CaptionSize;
                k.SetTextColor(SubColor);
                // 键列宽度只需容纳最长键名（「文件共享（FTP）」约 88dp）。
                // 之前 112dp 太宽，短键（如「设备名」）后面拖 70dp 空白才到值，显得松垮。
                k.LayoutParameters = new LinearLayout.LayoutParams(SDp(92), ViewGroup.LayoutParams.WrapContent);
                row.AddView(k);

                var v = new TextView(this) { Text = kv.Value ?? "" };
                v.TextSize = IosStyle.CaptionSize;
                v.SetTextColor(TextColor);
                v.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                // 地址类字段高亮并支持点击复制（IP / IPv6 / 端口 / 入口 / FTP）
                if (IsCopyableAddr(kv.Key))
                {
                    v.SetTextColor(AccentColor);
                    var val = kv.Value ?? "";
                    v.SetMinimumHeight(SDp(32));
                    v.Gravity = GravityFlags.CenterVertical;
                    v.Click += (s2, e2) => CopyToClipboard(val);
                }
                row.AddView(v);

                _infoBox.AddView(row);
            }

            if (_btHint != null)
            {
                _btHint.Text = _bridge.BitchatEnabled
                    ? L10n.T("蓝牙渠道：已开启 · 邻居 ") + BtNeighborCount() + L10n.T(" 台")
                    : L10n.T("蓝牙渠道：已关闭");
            }

            RefreshHealth();

            // 渠道开关可能与后台实际状态不同步（比如蓝牙等待授权），这里做一次静默同步
            _binding = true;
            try
            {
                _swLan.Checked = _bridge.LanEnabled;
                _swQaul.Checked = _bridge.QaulEnabled;
                _swBt.Checked = _bridge.BitchatEnabled;
                _swP2p.Checked = _bridge.P2pEnabled;
                // 影音页「设置」里也能改这个开关，这里每 2 秒静默同步一次
                _swListen.Checked = ListenTogether.Enabled;
            }
            catch { }
            _binding = false;

            // 网络变化后本机 IP 可能变了，实时刷新中转地址文案
            try { _refreshLocalRelay?.Invoke(); } catch { }
        }

        /// <summary>
        /// 渲染「渠道实时状态」：每条链路一行（状态 + 收发/中继/丢弃 + 最近活动）。
        ///
        /// 这张表就是「检测各渠道通信情况」的入口：
        ///   · 状态「运行中 · 暂无对端」→ 链路本身通了，但对端没发现（多半是防火墙/权限）；
        ///   · 「已关闭 / 未运行」→ 开关没开或传输层起不来；
        ///   · 收 = 0 但发 > 0 → 对方收不到（或根本没人在线）；
        ///   · 丢弃 > 0 → 有重复帧/超过跳数的中继被拦掉（正常现象，不必紧张）。
        /// </summary>
        private void RefreshHealth()
        {
            if (_healthBox == null) return;
            List<Dictionary<string, string>> list;
            try { list = _bridge.ChannelHealth(); }
            catch { return; }

            _healthBox.RemoveAllViews();
            foreach (var row in list)
            {
                string state = row.ContainsKey("state") ? row["state"] : "";
                bool ok = row.ContainsKey("ok") && row["ok"] == "1";

                var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
                wrap.SetPadding(0, SDp(4), 0, SDp(4));

                var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                head.SetGravity(GravityFlags.CenterVertical);

                var dot = new TextView(this);
                try { Icons.Leading(dot, IconKind.Dot, ok ? Color.Rgb(0x22, 0xC5, 0x5E) : Color.Rgb(0xC0, 0xC4, 0xCC), 11f, 6f); } catch { }
                head.AddView(dot);

                var name = new TextView(this) { Text = row["name"] };
                name.TextSize = IosStyle.CaptionSize;
                name.SetTextColor(TextColor);
                name.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                head.AddView(name);

                var st = new TextView(this) { Text = state };
                st.TextSize = IosStyle.CaptionSize;
                st.SetTextColor(ok ? Color.Rgb(0x22, 0x8B, 0x4A) : SubColor);
                head.AddView(st);

                wrap.AddView(head);

                var stat = new TextView(this)
                {
                    Text = L10n.T("收 ") + row["rx"] + L10n.T(" · 发 ") + row["tx"]
                         + L10n.T(" · 中继进 ") + row["relayIn"] + L10n.T(" / 出 ") + row["relayOut"]
                         + (Get(row, "rtt").Length > 0 ? " · RTT " + row["rtt"] + "ms" : "")
                         + L10n.T(" · 重传 ") + Get(row, "retrans", "0")
                         + L10n.T(" · 丢弃 ") + row["drop"]
                         + (row["last"] != "无" ? " · " + row["last"] : "")
                };
                stat.TextSize = IosStyle.CaptionSize;
                stat.SetTextColor(SubColor);
                stat.SetPadding(SDp(17), SDp(3), 0, 0);
                wrap.AddView(stat);

                string err = row.ContainsKey("err") ? row["err"] : "";
                if (!string.IsNullOrEmpty(err))
                {
                    var e = new TextView(this) { Text = L10n.T("最近错误：") + err };
                    e.TextSize = 10;
                    e.SetTextColor(Color.Rgb(0xD9, 0x53, 0x4F));
                    e.SetPadding(SDp(17), SDp(2), 0, 0);
                    wrap.AddView(e);
                }

                _healthBox.AddView(wrap);
                var line = new View(this);
                line.SetBackgroundColor(LineColor);
                var lineLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
                lineLp.LeftMargin = SDp(IosStyle.CardInset);
                line.LayoutParameters = lineLp;
                _healthBox.AddView(line);
            }

            // 队列汇总：链路都「正常」但消息发不出去时，问题就在这里（队列积压/反复重传/过期丢弃）
            try
            {
                var q = _bridge.OutboxStatus();
                string expired = Get(q, "expired", "0");
                var sum = new TextView(this)
                {
                    Text = L10n.T("出站队列：") + Get(q, "pending", "0") + L10n.T(" 条待发")
                         + L10n.T(" · 累计入队 ") + Get(q, "enq", "0")
                         + L10n.T(" · 重传 ") + Get(q, "retrans", "0")
                         + (expired != "0" ? " · 过期丢弃 " + expired : "")
                };
                sum.TextSize = IosStyle.CaptionSize;
                sum.SetTextColor(SubColor);
                sum.SetPadding(0, SDp(8), 0, 0);
                _healthBox.AddView(sum);
            }
            catch { }
        }

        /// <summary>安全取行内字段（后端加字段时前端不至于 NRE）。</summary>
        private static string Get(Dictionary<string, string> row, string key, string def = "")
        {
            string v;
            if (row != null && row.TryGetValue(key, out v) && v != null) return v;
            return def;
        }

        private string BtNeighborCount()
        {
            try
            {
                var list = _bridge.BitchatNeighborList();
                return list == null ? "0" : list.Count.ToString();
            }
            catch { return "0"; }
        }

        private void RefreshPeers()
        {
            if (_peerBox == null) return;
            List<Dictionary<string, string>> peers;
            try { peers = _bridge.OnlinePeers(); }
            catch { return; }

            _peerBox.RemoveAllViews();
            if (peers == null || peers.Count == 0)
            {
                var empty = new TextView(this) { Text = L10n.T("暂无。确认对方也打开了 App，且两台设备在同一网络。") };
                empty.TextSize = IosStyle.CaptionSize;
                empty.SetTextColor(SubColor);
                empty.SetPadding(0, SDp(8), 0, SDp(8));
                _peerBox.AddView(empty);
                return;
            }

            foreach (var p in peers)
            {
                string name = p.ContainsKey("name") ? p["name"] : (p.ContainsKey("id") ? p["id"] : "?");
                string ip = p.ContainsKey("ip") ? p["ip"] : "";
                string port = p.ContainsKey("port") ? p["port"] : "";

                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(0, SDp(7), 0, SDp(7));

                var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var t = new TextView(this) { Text = name };
                t.TextSize = 14;
                t.SetTextColor(TextColor);
                col.AddView(t);
                var s = new TextView(this) { Text = ip + (string.IsNullOrEmpty(port) ? "" : ":" + port) };
                s.TextSize = IosStyle.CaptionSize;
                s.SetTextColor(SubColor);
                col.AddView(s);
                row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                var call = new TextView(this) { Text = L10n.T("通话"), TextSize = IosStyle.CaptionSize };
                call.SetTextColor(AccentColor);
                call.SetMinimumWidth(SDp(56));
                call.SetMinimumHeight(SDp(IosStyle.ControlHeight));
                call.Gravity = GravityFlags.Center;
                call.SetPadding(SDp(10), SDp(6), SDp(10), SDp(6));
                call.Click += (s2, e2) =>
                {
                    var id = p.ContainsKey("id") ? p["id"] : null;
                    if (string.IsNullOrEmpty(id)) return;
                    var i = new Intent(this, typeof(CallActivity));
                    i.PutExtra("incoming", false);
                    StartActivity(i);
                    _bridge.StartCall(id, name, false);
                };
                row.AddView(call);

                _peerBox.AddView(row);

                var line = new View(this);
                line.SetBackgroundColor(LineColor);
                var lineLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
                lineLp.LeftMargin = SDp(IosStyle.CardInset);
                _peerBox.AddView(line, lineLp);
            }
        }

        /* ===================== 手动直连 ===================== */

        private void ShowManualConnect()
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(SDp(20), SDp(8), SDp(20), 0);

            var input = new EditText(this) { Hint = L10n.T("例如 192.168.1.23:45778 或 [fd00::1]:45778") };
            input.SetSingleLine(true);
            input.SetTextColor(TextColor);
            input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
            box.AddView(input);

            var tip = new TextView(this)
            {
                Text = L10n.T("直接粘贴复制来的地址（IP 与端口连在一起）即可自动识别；IPv6 请用 [地址]:端口。")
                     + L10n.T("可添加多个中转站，断线自动重连。")
            };
            tip.TextSize = IosStyle.CaptionSize;
            tip.SetTextColor(SubColor);
            tip.SetPadding(SDp(2), SDp(6), SDp(2), 0);
            box.AddView(tip);

            L10n.Dialog(this)
                .SetTitle(L10n.T("添加中转站"))
                .SetView(box)
                .SetPositiveButton(L10n.T("添加"), (s, e) =>
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
                    bool ok = _bridge.AddStaticRelay(host, p);
                    L10n.Toast(this, ok
                        ? L10n.T("已添加中转站：") + MchatBridge.FormatHostPort(host, p)
                        : L10n.T("地址格式不对"), ToastLength.Short).Show();
                    if (ok)
                    {
                        try { _refreshRelays?.Invoke(); } catch { }
                        try { _refreshLocalRelay?.Invoke(); } catch { }
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>设置本机监听端口（中转站固定端口场景）。修改后重启 App 生效。</summary>
        private void ShowSetListenPort()
        {
            var input = new EditText(this);
            input.InputType = Android.Text.InputTypes.ClassNumber;
            input.SetSingleLine(true);
            input.SetTextColor(TextColor);
            input.Hint = L10n.T("留空或 0 = 自动（默认从 45778 起）");
            int cur = 0;
            try { cur = _bridge.PreferredTcpPort; } catch { }
            if (cur > 0) input.Text = cur.ToString();

            L10n.Dialog(this)
                .SetTitle(L10n.T("本机监听端口"))
                .SetMessage(L10n.T("中转站场景可指定固定端口（需在路由器/防火墙放行）。端口范围 1024-65535。\n")
                          + "当前实际监听：" + (_bridge != null ? _bridge.LocalTcpPort : 0) + "。修改后重启 App 生效。")
                .SetView(input)
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    int p = 0;
                    var rawPort = (input.Text ?? "").Trim();
                    if (rawPort.Length > 0 && !int.TryParse(rawPort, out p))
                    {
                        L10n.Toast(this, L10n.T("端口需在 1024-65535（或留空用自动）"), ToastLength.Short).Show();
                        return;
                    }
                    if (p != 0 && (p < 1024 || p > 65535))
                    {
                        L10n.Toast(this, L10n.T("端口需在 1024-65535（或留空用自动）"), ToastLength.Short).Show();
                        return;
                    }
                    bool ok = _bridge != null && _bridge.SetPreferredTcpPort(p);
                    L10n.Toast(this, ok
                        ? (p > 0 ? L10n.T("已保存：") + p + L10n.T("，重启 App 后生效") : L10n.T("已恢复自动端口，重启 App 后生效"))
                        : L10n.T("保存失败"), ToastLength.Short).Show();
                    try { _refreshRelays?.Invoke(); } catch { }
                    try { _refreshLocalRelay?.Invoke(); } catch { }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        /// <summary>哪些连接信息字段是可复制的地址（IP / IPv6 / 端口 / 入口 / FTP）。</summary>
        private static bool IsCopyableAddr(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return key.Contains("IP") || key.Contains("IPv6") || key.Contains(L10n.T("端口"))
                || key.Contains(L10n.T("入口")) || key.Contains("FTP") || key.Contains(L10n.T("地址"));
        }

        private void CopyToClipboard(string text)
        {
            try
            {
                var cm = (Android.Content.ClipboardManager)GetSystemService(Context.ClipboardService);
                if (cm != null) cm.PrimaryClip = Android.Content.ClipData.NewPlainText("mchat", text ?? "");
                L10n.Toast(this, L10n.T("已复制：") + text, ToastLength.Short).Show();
            }
            catch { }
        }

            }
}

using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace AhoChatA
{
    /// <summary>
    /// 关于：显示版本号与程序简介，并提供手动检查更新。
    /// 点击版本号或"检查更新"按钮均可触发检测（不受一周一次限制）。
    /// </summary>
    [Activity(Label = "@string/act_about", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class AboutActivity : BaseActivity
    {
        private static readonly Color BgColor = new Color(IosStyle.Background);
        private static readonly Color CardColor = new Color(IosStyle.Surface);
        private static readonly Color TextMain = new Color(IosStyle.Text);
        private static readonly Color TextSub = new Color(IosStyle.Secondary);
        private static readonly Color Accent = new Color(IosStyle.Accent);

        private int Dp(int v) => IosStyle.Dp(this, v);

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            BuildUi();
        }

        private void BuildUi()
        {
            var root = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
            };
            root.SetBackgroundColor(BgColor);

            // 顶栏
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetPadding(Dp(4), 0, Dp(IosStyle.PageInset), 0);
            bar.SetBackgroundColor(CardColor);
            var back = new FrameLayout(this) { ContentDescription = L10n.T("返回") };
            back.AddView(new IconView(this, IconKind.Back) { IconColor = IosStyle.Accent },
                new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
            back.Click += (s, e) => Finish();
            bar.AddView(back, new LinearLayout.LayoutParams(Dp(IosStyle.ControlHeight), Dp(IosStyle.ControlHeight)));
            var title = new TextView(this) { Text = L10n.T("关于", "About"), TextSize = IosStyle.TitleSize };
            title.SetTextColor(TextMain);
            title.SetTypeface(null, TypefaceStyle.Bold);
            bar.AddView(title, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            bar.SetMinimumHeight(Dp(IosStyle.ToolbarHeight));
            root.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var scroll = new ScrollView(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, 0, 1f)
            };
            scroll.SetPadding(Dp(IosStyle.PageInset), Dp(IosStyle.PageInset), Dp(IosStyle.PageInset), Dp(IosStyle.PageInset));

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.Background = IosStyle.Rounded(this, IosStyle.Surface, IosStyle.CardRadius);

            // 应用名 + 版本（版本可点）
            box.AddView(InfoRow(
                L10n.T("AhoChat · 去中心化聊天", "AhoChat · Decentralized chat"),
                L10n.T("版本", "Version") + " " + UpdateManager.CurrentVersion,
                () => Check()));
            box.AddView(Divider());

            // 简介
            var intro = new TextView(this)
            {
                Text = L10n.T(
                    "AhoChat 是一款基于蓝牙 / Wi-Fi Direct 的自组织网状网络聊天工具，无需互联网即可在附近设备间收发消息、共享文件与影音。内置网页浏览器、影音库、一起听 / 同步听等能力。",
                    "AhoChat is a mesh-network chat app over Bluetooth / Wi-Fi Direct. Chat, share files and media with nearby devices without the Internet. Built-in browser, media library and synced listening."),
                TextSize = IosStyle.BodySize
            };
            intro.SetTextColor(TextMain);
            intro.SetPadding(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset));
            box.AddView(intro);
            box.AddView(Divider());

            // 用途声明与合规约束
            var notice = new TextView(this)
            {
                Text = L10n.T(
                    "主要用途：在网络中断地区使用 WLAN Direct 构建对等网络，恢复基础通信功能。禁止将本程序用于煽动任何国家的政治体系。",
                    "Purpose: rebuild peer-to-peer networks with WLAN Direct in areas without connectivity, restoring basic communications. Never use this app to incite political systems of any country."),
                TextSize = IosStyle.CaptionSize
            };
            notice.SetTextColor(TextSub);
            notice.SetPadding(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset / 2), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset / 2));
            box.AddView(notice);
            box.AddView(Divider());

            // 分享本程序
            var share = new Button(this)
            {
                Text = L10n.T("分享本程序", "Share AhoChat"),
                TextSize = IosStyle.BodySize
            };
            share.SetTextColor(Color.White);
            share.SetBackgroundColor(new Color(IosStyle.Accent));
            share.Click += (s, e) => ShowShare();
            var shareParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(IosStyle.ControlHeight));
            shareParams.SetMargins(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), 0);
            box.AddView(share, shareParams);

            // 检查更新按钮
            var btn = new Button(this)
            {
                Text = L10n.T("检查更新", "Check for updates"),
                TextSize = IosStyle.BodySize
            };
            btn.SetTextColor(Color.White);
            btn.SetBackgroundColor(new Color(IosStyle.Accent));
            btn.Click += (s, e) => Check();
            var btnParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(IosStyle.ControlHeight));
            btnParams.SetMargins(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset / 2), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset));
            box.AddView(btn, btnParams);

            scroll.AddView(box);
            root.AddView(scroll);
            SetContentView(root);
        }

        private View InfoRow(string main, string sub, Action click)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));
            row.SetPadding(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset));
            row.Click += (s, e) => click?.Invoke();

            var t = new TextView(this) { Text = main, TextSize = IosStyle.BodySize };
            t.SetTextColor(TextMain);
            row.AddView(t);
            var s = new TextView(this) { Text = sub, TextSize = IosStyle.CaptionSize };
            s.SetTextColor(Accent);
            row.AddView(s);
            return row;
        }

        private View Divider()
        {
            var d = new View(this) { LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 1) };
            d.SetBackgroundColor(new Color(IosStyle.Separator));
            return d;
        }

        private void ShowShare()
        {
            var items = new[]
            {
                L10n.T("分享安装链接（gh-proxy 加速）", "Share install link (gh-proxy)"),
                L10n.T("蓝牙 / 外部应用发送安装包", "Send installer via Bluetooth / other apps"),
                L10n.T("加入局域网文件共享（网页 /file）", "Add to LAN file share (web /file)")
            };
            L10n.Dialog(this)
                .SetTitle(L10n.T("分享本程序", "Share AhoChat"))
                .SetItems(items, (s, e) =>
                {
                    switch (e.Which)
                    {
                        case 0:
                            ShareAppHelper.ShareLink(this);
                            break;
                        case 1:
                            ShareAppHelper.ShareInstaller(this);
                            break;
                        case 2:
                            var url = ShareAppHelper.ShareToLan(this);
                            var msg = string.IsNullOrEmpty(url)
                                ? L10n.T("加入局域网共享失败", "Failed to add to LAN share")
                                : L10n.T("安装包已加入共享目录，浏览器打开：", "Installer added. Open in browser: ") + url;
                            L10n.Toast(this, msg, ToastLength.Long).Show();
                            break;
                    }
                })
                .SetNegativeButton(L10n.T("取消", "Cancel"), (s, e) => { })
                .Show();
        }

        private void Check()
        {
            _ = UpdateManager.CheckAsync(true);
        }
    }
}

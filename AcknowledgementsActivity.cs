using System;
using System.Collections.Generic;
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
    /// 鸣谢：列出本程序在开发过程中借鉴 / 使用的开源项目，点击任一项跳转到其 GitHub 仓库。
    /// 仅作致谢与出处标注，不涉及任何功能逻辑。
    /// </summary>
    [Activity(Label = "@string/act_ack", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class AcknowledgementsActivity : BaseActivity
    {
        private static readonly Color BgColor = new Color(IosStyle.Background);
        private static readonly Color CardColor = new Color(IosStyle.Surface);
        private static readonly Color TextMain = new Color(IosStyle.Text);
        private static readonly Color TextSub = new Color(IosStyle.Secondary);
        private static readonly Color LineColor = new Color(IosStyle.Separator);
        private static readonly Color Accent = new Color(IosStyle.Accent);

        // 用户名/项目名 + 在本程序中的用途 + 仓库地址
        private static readonly List<LibItem> Libs = new List<LibItem>
        {
            new LibItem("gedoor/legado", L10n.T("阅读规则引擎与书源模型（阅读模块的底层，规则求值与书源数据模型忠实移植自此）"),
                "https://github.com/gedoor/legado"),
            new LibItem("iTsMe-Zen/Areada", L10n.T("电子书 / PDF 解析与连续页阅读（阅读模块的 PDF 引擎、EPUB/FB2/Markdown 解析转译自此）"),
                "https://github.com/iTsMe-Zen/Areada"),
            new LibItem("Moriafly/SaltPlayerSource", L10n.T("本地音乐播放器交互与界面参考（音乐播放页、歌词与专辑封面展示）"),
                "https://github.com/Moriafly/SaltPlayerSource"),
            new LibItem("jellyfin/jellyfin-android", L10n.T("视频播放器控制层与手势参考（播放页、进度条、双指缩放与横滑 seek）"),
                "https://github.com/jellyfin/jellyfin-android"),
            new LibItem("jellyfin/jellyfin-web", L10n.T("影音库网页端数据流参考（网页影音库的分区导航与 nowPlayingBar）"),
                "https://github.com/jellyfin/jellyfin-web"),
            new LibItem("zzzprojects/html-agility-pack", L10n.T("HTML 解析（阅读规则中的网页内容提取与网页抓取）"),
                "https://github.com/zzzprojects/html-agility-pack"),
            new LibItem("atifaziz/Fizzler", L10n.T("CSS 选择器（阅读规则里的元素定位）"),
                "https://github.com/atifaziz/Fizzler"),
            new LibItem("sebastienros/jint", L10n.T("JavaScript 引擎（书源规则里的 JS 求值）"),
                "https://github.com/sebastienros/jint"),
            new LibItem("qaul/qaul.net", L10n.T("去中心化网状网络通信参考（qaul 链路、Noise 握手与 floodsub）"),
                "https://github.com/qaul/qaul.net"),
            new LibItem("jackjackbits/bitchat", L10n.T("蓝牙网状传输参考（离线自组网、蓝牙发现与多跳中继）"),
                "https://github.com/jackjackbits/bitchat"),
            new LibItem("bcgit/bc-csharp", L10n.T("BouncyCastle C# 加密原语（TLS 1.3 / Noise 等密码学基础）"),
                "https://github.com/bcgit/bc-csharp"),
            new LibItem("dotnet/android", L10n.T("Xamarin.Android / .NET —— 本程序的运行与开发框架"),
                "https://github.com/dotnet/android"),
        };

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

            var title = new TextView(this) { Text = L10n.T("鸣谢"), TextSize = IosStyle.TitleSize };
            title.SetTextColor(TextMain);
            title.SetTypeface(null, TypefaceStyle.Bold);
            bar.AddView(title, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            bar.SetMinimumHeight(Dp(IosStyle.ToolbarHeight));
            root.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            // 说明
            var intro = new TextView(this)
            {
                Text = L10n.T("本程序在开发过程中借鉴 / 使用了以下开源项目，谨此致谢。点击任一项可前往其 GitHub 仓库。"),
                TextSize = IosStyle.CaptionSize
            };
            intro.SetTextColor(TextSub);
            intro.SetPadding(Dp(IosStyle.PageInset), Dp(IosStyle.PageInset), Dp(IosStyle.PageInset), Dp(8));
            root.AddView(intro);

            // 列表（可滚动）
            var scroll = new ScrollView(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, 0, 1f)
            };
            scroll.SetPadding(Dp(IosStyle.PageInset), Dp(8), Dp(IosStyle.PageInset), Dp(IosStyle.PageInset));

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.Background = IosStyle.Rounded(this, IosStyle.Surface, IosStyle.CardRadius);

            for (int i = 0; i < Libs.Count; i++)
            {
                box.AddView(LibRow(Libs[i]));
                if (i < Libs.Count - 1)
                    box.AddView(Divider());
            }

            scroll.AddView(box);
            root.AddView(scroll);

            SetContentView(root);
        }

        private View LibRow(LibItem item)
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.SetBackgroundColor(Color.Transparent);
            card.SetMinimumHeight(Dp(IosStyle.RowMinHeight));
            card.SetPadding(Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset), Dp(IosStyle.CardInset));

            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);

            var name = new TextView(this) { Text = item.Repo, TextSize = IosStyle.BodySize };
            name.SetTextColor(Accent);
            name.SetTypeface(null, TypefaceStyle.Bold);
            name.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            top.AddView(name);

            var go = new TextView(this) { Text = "GitHub ↗", TextSize = IosStyle.CaptionSize };
            go.SetTextColor(TextSub);
            top.AddView(go, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            card.AddView(top);

            var desc = new TextView(this) { Text = item.Usage, TextSize = IosStyle.CaptionSize };
            desc.SetTextColor(TextSub);
            desc.SetPadding(0, Dp(6), 0, 0);
            desc.SetLineSpacing(Dp(2), 1.0f);
            card.AddView(desc);

            card.Click += (s, e) => OpenUrl(item.Url);
            return card;
        }

        private View Divider()
        {
            var d = new View(this)
            {
                LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1)
            };
            d.SetBackgroundColor(LineColor);
            return d;
        }

        private void OpenUrl(string url)
        {
            try
            {
                // GitHub 仓库地址：优先在应用内浏览器打开，用户返回时还留在鸣谢页
                if (BrowserActivity.CanHandle(url)) { BrowserActivity.Open(this, url); return; }
                var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                StartActivity(intent);
            }
            catch
            {
                L10n.Toast(this, L10n.T("没有可打开该链接的应用"), ToastLength.Short).Show();
            }
        }

        private class LibItem
        {
            public string Repo;
            public string Usage;
            public string Url;
            public LibItem(string repo, string usage, string url)
            {
                Repo = repo; Usage = usage; Url = url;
            }
        }
    }
}

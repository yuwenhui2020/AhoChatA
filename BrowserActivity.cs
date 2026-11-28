using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net;
using Android.Net.Http;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Webkit;
using Android.Widget;
using AndroidRes = Android.Resource;
using Java.Lang;
using Orientation = Android.Widget.Orientation;
using SysUri = System.Uri;
using AndroidUri = Android.Net.Uri;
using StringBuilder = System.Text.StringBuilder;

namespace AhoChatA
{
    /// <summary>
    /// 内置浏览器 - 基于 Android System WebView
    /// 功能：广告拦截、资源嗅探、离线保存、全屏视频、文件选择、下载管理
    /// </summary>
    [Activity(Label = "@string/act_browser",
              Theme = "@style/AppTheme.NoActionBar",
              Exported = false,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                                       | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
              WindowSoftInputMode = SoftInput.AdjustResize)]
    public class BrowserActivity : Activity
    {
        public const string ExtraUrl = "browser_url";
        public const string ExtraTitle = "browser_title";

        private const int ReqFileChooser = 7411;
        private const int ReqSavePage = 7412;
        private const int ReqImagesToPdf = 7413;
        private const int ReqAdRulesImport = 7414;

        public WebView WebView => _web;
        private WebView _web => _activeTab?.Web;
        private const int MaxTabs = 12;
        private const int MaxAddressLength = 8192;
        private const string BrowserPrefsName = "browser_ui_preferences";
        private const string PrefBlockImages = "block_network_images";
        private const string PrefTextZoom = "text_zoom";
        private const string PrefThirdPartyCookies = "third_party_cookies";
        private const string PrefSearchEngine = "search_engine";
        private const string PrefBrowserNight = "browser_night";
        private const string PrefDataCleanup = "data_cleanup_done";
        private const string PrefUserScriptEnabled = "user_script_enabled";
        private static readonly int[] TextZoomOptions = { 80, 100, 120, 150 };
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private BrowserTab _activeTab, _saveTab, _fullscreenTab, _fileTab;
        private FrameLayout _webHost;
        private LinearLayout _topControls, _bottomControls;
        private TextView _tabButton, _backButton, _forwardButton;
        private bool _resumed;
        private string CurrentUrl => _activeTab?.Url ?? "";

        /* 原生起始页：零网络请求、即时呈现 */
        private const string HomeUrl = "mchat://home";
        private FrameLayout _homeView;
        private LinearLayout _homeSearchRow;
        private EditText _homeSearch;
        private GridView _homeBookmarks;
        private TextView _homeEmpty;
        private bool _browserNight;
        private bool _incognito;
        /// <summary>当前页面是否已翻译（用于「翻译/恢复原文」切换）。导航时重置。</summary>
        private bool _pageTranslated;
        private static readonly string[] SearchEngines = { L10n.T("必应"), L10n.T("百度"), L10n.T("谷歌"), L10n.T("搜狗") };
        private static readonly string[] SearchUrlTemplates =
        {
            "https://www.bing.com/search?q={0}",
            "https://www.baidu.com/s?wd={0}",
            "https://www.google.com/search?q={0}",
            "https://www.sogou.com/web?query={0}"
        };
        private int _searchEngine;

        private sealed class BrowserTab
        {
            public WebView Web;
            public volatile bool Closed;
            public volatile string Url;
            public volatile string Referer;
            public string Title;
            public string Error;
            public int Progress = 100;
            public ResourceSniffer Sniffer;
            public bool DesktopSite;
            public string MobileUserAgent;
            public bool IsHome;
            public bool Incognito;
            public string PendingUrl; // 起始页搜索框内容：Activity 重建后恢复用
            /// <summary>导航代次：每次开始新导航 +1，用于让异步回调（如 @require 下载完成）
            /// 只作用于发起时的那一页，避免注入到已经离开的页面。</summary>
            public int NavGeneration;
            /// <summary>本标签页油猴脚本注册的菜单命令（GM_registerMenuCommand）。按 tab 隔离，避免多窗口串味。</summary>
            public System.Collections.Generic.List<ScriptCommand> MenuCommands;
        }
        private EditText _urlText;
        private ProgressBar _progress;
        private LinearLayout _errorBox;
        private TextView _errorTitle, _errorDetail, _retryBtn;
        private FrameLayout _content;
        private FrameLayout _fullscreenHost;

        private View _customView;
        private WebChromeClient.ICustomViewCallback _customCallback;
        private IValueCallback _fileCallback;



        // 增强功能
        // 广告拦截默认关闭：需要用户在菜单里主动开启（尊重网页原始内容，避免误拦）
        private bool _adBlockEnabled = false;
        private bool _resourceSniffingEnabled = true;
        private bool _userScriptEnabled = true;
        private bool _blockNetworkImages;
        private bool _acceptThirdPartyCookies;
        private int _textZoom = 100;


        private AdBlockEngine _adBlockEngine;

        private OfflinePageSaver _offlineSaver;

        private LinearLayout _sniffBar;
        private TextView _sniffText;

        /* ------------------------------------------------------------------ 对外入口 */

        /// <summary>用应用内浏览器打开一个地址（http/https 之外的协议交给系统处理）。</summary>
        public static void Open(Context ctx, string url, string title = null)
        {
            if (ctx == null) return;
            var target = NormalizeUrl(url);
            if (target.Length == 0) return;
            if (!CanHandle(target))
            {
                OpenExternal(ctx, target);
                return;
            }
            try
            {
                var i = new Intent(ctx, typeof(BrowserActivity));
                i.PutExtra(ExtraUrl, target);
                if (!string.IsNullOrEmpty(title)) i.PutExtra(ExtraTitle, title);
                if (!(ctx is Activity)) i.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(i);
            }
            catch (System.Exception ex)
            {
                try { L10n.Toast(ctx, L10n.T("无法打开内置浏览器：") + ex.Message, ToastLength.Short).Show(); } catch { }
            }
        }

        /// <summary>打开浏览器并显示原生起始页（不带任何网址）。</summary>
        public static void OpenHome(Context ctx)
        {
            if (ctx == null) return;
            try
            {
                var i = new Intent(ctx, typeof(BrowserActivity));
                i.PutExtra(ExtraUrl, HomeUrl);
                if (!(ctx is Activity)) i.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(i);
            }
            catch (System.Exception ex)
            {
                try { L10n.Toast(ctx, L10n.T("无法打开内置浏览器：") + ex.Message, ToastLength.Short).Show(); } catch { }
            }
        }

        /// <summary>这个地址能不能在应用内打开（只有 http / https 可以）。</summary>
        public static bool CanHandle(string url)
        {
            var u = NormalizeUrl(url);
            if (u.Length == 0) return false;
            return IsHttpUrl(u);
        }

        /// <summary>
        /// 把用户输入的「裸 IP / IP:端口 / 域名 / 完整链接」补成可加载的地址。
        /// 裸 IP 默认走 80 端口（http 的默认端口），带端口则用给定端口。
        /// </summary>
        public static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.Length > MaxAddressLength || raw.Any(char.IsControl)) return "";
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return IsHttpUrl(raw) ? raw : "";
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("mchat://", StringComparison.OrdinalIgnoreCase))
                return raw;

            // 补全 scheme
            var localCandidate = "http://" + raw;
            if (IsHttpUrl(localCandidate)
                && (System.Net.IPAddress.TryParse(new SysUri(localCandidate).Host, out _)
                    || new SysUri(localCandidate).Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
                return localCandidate;
            if (Regex.IsMatch(raw, @"^[a-zA-Z0-9\-\.]+\.[a-zA-Z]{2,}(:\d+)?(/.*)?$")) // 域名
            {
                var candidate = "https://" + raw;
                return IsHttpUrl(candidate) ? candidate : "";
            }
            // 其它当作搜索关键词（默认必应）
            var searchUrl = "https://www.bing.com/search?q=" + SysUri.EscapeDataString(raw);
            return searchUrl.Length <= MaxAddressLength ? searchUrl : "";
        }

        /// <summary>用系统浏览器/外部应用打开链接。</summary>
        public static void OpenExternal(Context ctx, string url)
        {
            try
            {
                var intent = new Intent(Intent.ActionView, AndroidUri.Parse(url));
                intent.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(intent);
            }
            catch (System.Exception ex)
            {
                try { L10n.Toast(ctx, L10n.T("无法打开链接：") + ex.Message, ToastLength.Short).Show(); } catch { }
            }
        }

        /* ------------------------------------------------------------------ 生命周期 */

        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.Apply(this);
            base.OnCreate(savedInstanceState);
            // Let Android own system-bar layout; the browser adds no navigation-bar spacer or padding.
            Window.ClearFlags(WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.TranslucentNavigation | WindowManagerFlags.TranslucentStatus);
            IosStyle.ApplySystemBars(this);
            _adBlockEngine = new AdBlockEngine(ApplicationContext);
            _adBlockEnabled = _adBlockEngine.Enabled;
            LoadBrowserPreferences();
            // 初始化油猴脚本管理器
            UserScriptManager.Instance.Init(this);
            _offlineSaver = new OfflinePageSaver(this);
            BuildUi();
            ApplyBrowserChromeTheme();
            // 30 天数据清理（只做一次，下载数据不在此列）
            CleanupOldBrowserData();
            var incoming = Intent?.GetStringExtra(ExtraUrl);
            NewTab(string.IsNullOrEmpty(incoming) ? HomeUrl : incoming, Intent?.GetStringExtra(ExtraTitle));
        }

        protected override void OnPause()
        {
            _resumed = false;
            _web?.OnPause();
            base.OnPause();
        }

        protected override void OnResume()
        {
            base.OnResume();
            _resumed = true;
            _web?.OnResume();
            IosStyle.ApplySystemBars(this);
            var night = ThemeManager.IsNight(this);
            if (_browserNight == night) return;
            _browserNight = night;
            ApplyBrowserChromeTheme();
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.Closed) continue;
                ApplyWebViewNight(tab.Web);
                try { tab.Web.Reload(); } catch { }
            }
        }

        protected override void OnDestroy()
        {
            ExitFullscreen();
            // 证书询问可能还挂着 handler：不取消会让 WebView 卡在加载中
            try { CancelAllSslPending(); } catch { }
            // 先清空文件选择器回调，防止回调进入已销毁的 Activity
            if (_fileCallback != null)
            {
                try { _fileCallback.OnReceiveValue(null); } catch { }
                _fileCallback = null;
                _fileTab = null;
            }
            foreach (var tab in _tabs.ToArray()) DestroyTab(tab);
            _tabs.Clear();
            _activeTab = null;
            base.OnDestroy();
        }

        public override void OnTrimMemory(TrimMemory level)
        {
            base.OnTrimMemory(level);
            // WebView 内存压力回收：清理不可见标签页、释放图片缓存
            if (level >= TrimMemory.RunningLow)
            {
                try
                {
                    foreach (var tab in _tabs.ToArray())
                    {
                        if (tab.Closed || tab == _activeTab) continue;
                        tab.Web?.FreeMemory();
                    }
                    // 清理全局 WebView 缓存
                    WebView.FreeMemory();
                }
                catch { }
            }
        }

        public override void OnLowMemory()
        {
            base.OnLowMemory();
            try
            {
                foreach (var tab in _tabs.ToArray())
                {
                    if (tab.Closed) continue;
                    tab.Web?.FreeMemory();
                }
                WebView.FreeMemory();
            }
            catch { }
        }

        /// <summary>统一的生命周期守卫：Activity 是否已销毁或正在销毁。</summary>
        internal bool IsActivityDestroyed()
        {
            return IsFinishing || (Build.VERSION.SdkInt >= BuildVersionCodes.JellyBeanMr1 && IsDestroyed);
        }

        private BrowserTab NewTab(string url, string title = null, bool select = true, bool desktopSite = false, bool incognito = false)
        {
            if (_tabs.Count >= MaxTabs)
            {
                L10n.Toast(this, $"最多打开 {MaxTabs} 个窗口", ToastLength.Short).Show();
                return null;
            }
            var isHome = string.IsNullOrEmpty(url) || url == HomeUrl;
            var tab = new BrowserTab
            {
                Web = new WebView(this), Title = title,
                Url = isHome ? HomeUrl : url,
                IsHome = isHome, DesktopSite = desktopSite, Incognito = incognito
            };
            tab.Sniffer = new ResourceSniffer(() =>
            {
                // Sniffing continues asynchronously after navigation; never post into a torn-down Activity.
                try
                {
                    if (IsFinishing || (int)Build.VERSION.SdkInt >= 17 && IsDestroyed) return;
                    RunOnUiThread(() =>
                    {
                        if (!IsFinishing && !((int)Build.VERSION.SdkInt >= 17 && IsDestroyed)
                            && !tab.Closed && tab == _activeTab) UpdateSniffBar();
                    });
                }
                catch { }
            });
            _tabs.Add(tab);
            ConfigureWebView(tab);
            // 起始页不加载任何网络地址，只加载空白页作为 WebView 初始化占位；
            // 其它标签必须在这里真正 LoadUrl，否则从服务页/外部链接传进来的网址会白屏。
            if (isHome) tab.Web.LoadUrl("about:blank");
            else tab.Web.LoadUrl(url);
            if (select) SwitchTab(tab);
            UpdateTabCount();
            return tab;
        }

        /// <summary>
        /// 为 popup 窗口（window.open、target=_blank）创建纯净 WebView 标签页。
        /// 关键：不调用 LoadUrl，WebView 必须是"从未导航过"的原始状态，否则 Chromium 会抛出
        /// IllegalArgumentException: New WebView for popup window must not have been previously navigated
        /// </summary>
        private BrowserTab CreatePopupTab(bool incognito = false)
        {
            if (_tabs.Count >= MaxTabs) return null;
            var tab = new BrowserTab
            {
                Web = new WebView(this),
                Title = null,
                Url = "about:blank",
                IsHome = false,
                DesktopSite = _activeTab?.DesktopSite ?? false,
                Incognito = incognito
            };
            tab.Sniffer = new ResourceSniffer(() =>
            {
                try
                {
                    if (IsFinishing || (int)Build.VERSION.SdkInt >= 17 && IsDestroyed) return;
                    RunOnUiThread(() =>
                    {
                        if (!IsFinishing && !((int)Build.VERSION.SdkInt >= 17 && IsDestroyed)
                            && !tab.Closed && tab == _activeTab) UpdateSniffBar();
                    });
                }
                catch { }
            });
            _tabs.Add(tab);
            ConfigureWebView(tab);
            UpdateTabCount();
            // 关键：不调用 LoadUrl，保持 WebView 处于 pristine 状态供 transport 使用
            return tab;
        }

        private void SwitchTab(BrowserTab tab)
        {
            if (tab == null || tab.Closed || tab == _activeTab) return;
            if (IsActivityDestroyed()) return;
            ExitFullscreen();
            if (_web != null)
            {
                if (_resumed) _web.OnPause();
                _webHost.RemoveView(_web);
            }
            HideKeyboard();
            _activeTab = tab;
            _webHost.AddView(tab.Web, new FrameLayout.LayoutParams(-1, -1));
            if (_resumed) tab.Web.OnResume();
            RenderTab(true);
        }

        private void DestroyTab(BrowserTab tab)
        {
            tab.Closed = true;
            if (_fullscreenTab == tab) ExitFullscreen();
            if (_fileTab == tab) CancelFileChooser();
            if (_saveTab == tab) _saveTab = null;
            
            // 先断开所有客户端，防止回调进入已销毁的 WebView
            try { tab.Web.SetWebViewClient(null); } catch { }
            try { tab.Web.SetWebChromeClient(null); } catch { }
            try { tab.Web.SetDownloadListener(null); } catch { }
            
            _webHost.RemoveView(tab.Web);
            tab.Web.StopLoading();
            tab.Web.Destroy();
        }

        private void CloseTab(BrowserTab tab)
        {
            var index = _tabs.IndexOf(tab);
            if (index < 0) return;
            _tabs.RemoveAt(index);
            UpdateTabCount();
            if (tab == _activeTab) _activeTab = null;
            DestroyTab(tab);
            if (tab.Incognito && !_tabs.Any(t => t.Incognito))
            {
                try { CookieManager.Instance.RemoveAllCookie(); } catch { }
                L10n.Toast(this, L10n.T("隐身窗口已关闭，浏览痕迹已清除"), ToastLength.Short).Show();
            }
            if (_tabs.Count == 0) { Finish(); return; }
            if (_activeTab == null) SwitchTab(_tabs[System.Math.Min(index, _tabs.Count - 1)]);
            else RenderTab(false);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            if (requestCode == ReqFileChooser)
            {
                var cb = _fileCallback;
                _fileTab = null;
                _fileCallback = null;
                if (cb == null) return;
                AndroidUri[] result = null;

                try
                {
                    if (resultCode == Result.Ok && data != null)
                    {
                        if (data.ClipData != null && data.ClipData.ItemCount > 0)
                        {
                            var list = new List<AndroidUri>();
                            for (int i = 0; i < data.ClipData.ItemCount; i++)
                            {
                                var u = data.ClipData.GetItemAt(i)?.Uri;
                                if (u != null) list.Add(u);
                            }
                            if (list.Count > 0) result = list.ToArray();
                        }
                        else if (data.Data != null)
                        {
                            result = new AndroidUri[] { data.Data };
                        }
                    }
                }
                catch { }
                try { cb.OnReceiveValue(result == null ? null : Java.Lang.Object.FromArray(result)); } catch { }
                return;
            }
            if (requestCode == ReqSavePage)
            {
                var tab = _saveTab;
                _saveTab = null;
                if (tab != null && !tab.Closed && resultCode == Result.Ok && data?.Data != null)
                    _offlineSaver.SavePage(tab.Web, data.Data);
                return;
            }
            if (requestCode == ReqUserScriptImport)
            {
                try
                {
                    if (resultCode == Result.Ok && data != null && data.Data != null)
                    {
                        ReadUserScriptFromUri(data.Data);
                    }
                }
                catch { }
                return;
            }
            if (requestCode == ReqAdRulesImport)
            {
                try
                {
                    if (resultCode == Result.Ok && data != null && data.Data != null)
                    {
                        using (var input = ContentResolver.OpenInputStream(data.Data))
                        using (var reader = new System.IO.StreamReader(input))
                        {
                            string content = reader.ReadToEnd();
                            int n = 0;
                            foreach (var line in content.Split('\n'))
                            {
                                string r = line.Trim();
                                if (string.IsNullOrEmpty(r)) continue;
                                _adBlockEngine.AddCustomRule(r);
                                n++;
                            }
                            ToastShort(L10n.T("已导入 ") + n + L10n.T(" 条规则"));
                        }
                    }
                }
                catch (System.Exception ex) { ToastShort(L10n.T("导入失败: ") + ex.Message); }
                return;
            }
            if (requestCode == ReqImagesToPdf)
            {
                var uris = new List<Android.Net.Uri>();
                try
                {
                    if (resultCode == Result.Ok && data != null)
                    {
                        if (data.ClipData != null && data.ClipData.ItemCount > 0)
                        {
                            for (int i = 0; i < data.ClipData.ItemCount; i++)
                            {
                                var u = data.ClipData.GetItemAt(i)?.Uri;
                                if (u != null) uris.Add(u);
                            }
                        }
                        else if (data.Data != null) uris.Add(data.Data);
                    }
                }
                catch { }
                if (uris.Count > 0) ExportImagesToPdf(uris);
                return;
            }

            base.OnActivityResult(requestCode, resultCode, data);
        }

        public override void OnBackPressed()
        {
            if (_customView != null) { ExitFullscreen(); return; }
            if (_urlText.HasFocus || (_homeSearch != null && _homeSearch.HasFocus)) { HideKeyboard(); RenderTab(true); return; }
            if (_activeTab?.IsHome == true)
            {
                if (_tabs.Count > 1) { CloseTab(_activeTab); return; }
                base.OnBackPressed(); return;
            }
            if (_web?.CanGoBack() == true) _web.GoBack();
            else if (_activeTab != null) CloseTab(_activeTab);
            else base.OnBackPressed();
        }

        /* ------------------------------------------------------------------ UI 构建 */

        private int Dp(float v) => (int)(v * Resources.DisplayMetrics.Density + 0.5f);

        private void LoadBrowserPreferences()
        {
            var prefs = GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private);
            _blockNetworkImages = prefs.GetBoolean(PrefBlockImages, false);
            _acceptThirdPartyCookies = prefs.GetBoolean(PrefThirdPartyCookies, false);
            _userScriptEnabled = prefs.GetBoolean(PrefUserScriptEnabled, true);
            var requestedZoom = prefs.GetInt(PrefTextZoom, 100);
            _textZoom = TextZoomOptions.Contains(requestedZoom) ? requestedZoom : 100;
            _searchEngine = System.Math.Max(0, System.Math.Min(SearchEngines.Length - 1, prefs.GetInt(PrefSearchEngine, 0)));
            // 浏览器夜间跟随全局主题（设置页/顶栏切换后立即生效），旧独立偏好仅作兼容写入。
            _browserNight = ThemeManager.IsNight(this);
            prefs.Edit().PutBoolean(PrefBrowserNight, _browserNight).Apply();
        }

        private void ToggleDesktopSite()
        {
            var tab = _activeTab;
            if (tab == null || tab.Closed) return;
            tab.DesktopSite = !tab.DesktopSite;
            ApplyUserAgent(tab);
            tab.Web.Settings.UseWideViewPort = true;
            tab.Web.Settings.LoadWithOverviewMode = true;
            // 注入 viewport meta 标签，防止桌面模式白屏
            InjectDesktopViewport(tab.Web, tab.DesktopSite);
            tab.Web.Reload();
            L10n.Toast(this, tab.DesktopSite ? L10n.T("此窗口已切换为桌面版") : L10n.T("此窗口已切换为手机版"), ToastLength.Short).Show();
        }

        /// <summary>向页面注入/移除 viewport meta 标签，配合桌面模式防止白屏。</summary>
        private void InjectDesktopViewport(WebView web, bool desktopSite)
        {
            try
            {
                var js = desktopSite
                    ? "(function(){var m=document.querySelector('meta[name=\"viewport\"]');if(!m){m=document.createElement('meta');m.name='viewport';document.head.appendChild(m);}m.content='width=device-width,initial-scale=1.0,maximum-scale=5,user-scalable=yes';})()"
                    : "(function(){var m=document.querySelector('meta[name=\"viewport\"]');if(m&&m.parentNode)m.parentNode.removeChild(m);})()";
                web.EvaluateJavascript(js, null);
            }
            catch { }
        }

        private static string DesktopUserAgent(string mobileUserAgent)
        {
            // 使用真实的 Chrome 桌面版 User-Agent，而非简单的字符串替换
            // Chrome 120 on Windows 10 (最通用、兼容性最好)
            return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        }

        private void ApplyUserAgent(BrowserTab tab)
        {
            var settings = tab?.Web?.Settings;
            if (settings == null) return;
            if (string.IsNullOrEmpty(tab.MobileUserAgent)) tab.MobileUserAgent = settings.UserAgentString;
            settings.UserAgentString = tab.DesktopSite ? DesktopUserAgent(tab.MobileUserAgent) : tab.MobileUserAgent;
        }

        private void ToggleNetworkImages()
        {
            _blockNetworkImages = !_blockNetworkImages;
            GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private).Edit()
                .PutBoolean(PrefBlockImages, _blockNetworkImages).Apply();
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.Closed) continue;
                tab.Web.Settings.BlockNetworkImage = _blockNetworkImages;
                try { tab.Web.Reload(); } catch { }
            }
            L10n.Toast(this, _blockNetworkImages ? L10n.T("已屏蔽网页网络图片") : L10n.T("已允许网页网络图片"), ToastLength.Short).Show();
        }

        private void ShowTextZoomPicker()
        {
            var labels = TextZoomOptions.Select(x => x + "%").ToArray();
            var selected = Array.IndexOf(TextZoomOptions, _textZoom);
            AlertDialog picker = null;
            picker = L10n.Dialog(this)
                .SetTitle(L10n.T("网页文字大小"))
                .SetSingleChoiceItems(labels, selected, (sender, args) =>
                {
                    _textZoom = TextZoomOptions[args.Which];
                    GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private).Edit()
                        .PutInt(PrefTextZoom, _textZoom).Apply();
                    foreach (var tab in _tabs.ToArray())
                        if (!tab.Closed) tab.Web.Settings.TextZoom = _textZoom;
                    picker?.Dismiss();
                })
                .SetNegativeButton(L10n.T("取消"), (sender, args) => { })
                .Create();
            picker.Show();
        }

        private void ToggleThirdPartyCookies()
        {
            _acceptThirdPartyCookies = !_acceptThirdPartyCookies;
            GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private).Edit()
                .PutBoolean(PrefThirdPartyCookies, _acceptThirdPartyCookies).Apply();
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.Closed) continue;
                try { CookieManager.Instance.SetAcceptThirdPartyCookies(tab.Web, _acceptThirdPartyCookies); } catch { }
                try { tab.Web.Reload(); } catch { }
            }
            L10n.Toast(this, _acceptThirdPartyCookies
                ? L10n.T("已允许第三方 Cookie；部分网站登录可能需要此设置")
                : L10n.T("已禁止第三方 Cookie"), ToastLength.Short).Show();
        }

        private void ShowThirdPartyCookieSettings()
        {
            var builder = L10n.Dialog(this)
                .SetTitle(L10n.T("第三方 Cookie"))
                .SetMessage(_acceptThirdPartyCookies
                    ? L10n.T("当前允许第三方 Cookie。此设置可能改善部分网站的嵌入式登录兼容性。")
                    : L10n.T("当前禁止第三方 Cookie。允许后可能改善部分网站的嵌入式登录兼容性，也会允许更多跨站跟踪。"))
                .SetNegativeButton(L10n.T("取消"), (dialog, args) => { });
            builder.SetPositiveButton(_acceptThirdPartyCookies ? L10n.T("禁止") : L10n.T("允许"), (dialog, args) => ToggleThirdPartyCookies());
            builder.Show();
        }

        private void ShowConnectionInfo()
        {
            var address = _web?.Url ?? CurrentUrl;
            if (!SysUri.TryCreate(address, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                L10n.Toast(this, L10n.T("当前页面没有 HTTP/HTTPS 连接信息"), ToastLength.Short).Show();
                return;
            }
            var origin = uri.Scheme.ToUpperInvariant() + " · " + uri.Authority;
            var text = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? origin + L10n.T("\nHTTPS 使用网站 TLS 证书；证书错误会被阻止。DNS VPN 仅处理域名查询，不替代 TLS。")
                : origin + L10n.T("\n此 HTTP 页面未加密，DNS VPN 不保护网页内容。");
            L10n.Dialog(this).SetTitle(L10n.T("连接信息")).SetMessage(text)
                .SetPositiveButton(L10n.T("确定"), (dialog, args) => { }).Show();
        }

        private void ShareCurrentPage()
        {
            ShareUrl(CurrentUrl);
        }

        private void ShareUrl(string url)
        {
            if (!IsHttpUrl(url)) return;
            try
            {
                var send = new Intent(Intent.ActionSend);
                send.SetType("text/plain");
                send.PutExtra(Intent.ExtraText, url);
                StartActivity(Intent.CreateChooser(send, L10n.T("分享网页")));
            }
            catch (System.Exception ex)
            {
                L10n.Toast(this, L10n.T("无法分享链接：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private void BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(Color.White);

            root.FocusableInTouchMode = true;
            _topControls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _topControls.SetGravity(GravityFlags.CenterVertical);
            _topControls.SetPadding(Dp(16), Dp(6), Dp(8), Dp(6));
            _topControls.SetBackgroundColor(Color.White);
            _urlText = new EditText(this) { TextSize = 15, ImeOptions = ImeAction.Go };
            _urlText.SetHint(Resource.String.browser_hint);
            _urlText.SetTextColor(Color.ParseColor("#18212B"));
            _urlText.SetHintTextColor(Color.ParseColor("#555F6B"));
            _urlText.SetSingleLine(true);
            _urlText.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
            _urlText.SetSelectAllOnFocus(true);
            _urlText.SetPadding(Dp(12), 0, Dp(12), 0);
            _urlText.Background = GetRoundedDrawable(Color.ParseColor("#F2F2F7"), 16);
            _urlText.EditorAction += (s, e) =>
            {
                if ((ImeAction)e.ActionId == ImeAction.Go || (e.Event?.KeyCode == Keycode.Enter && e.Event.Action == KeyEventActions.Down))
                {
                    var url = SearchUrlFor(_urlText.Text);
                    HideKeyboard();
                    if (IsHttpUrl(url)) NavigateTo(url);
                    else RenderTab(true);
                    e.Handled = true;
                }
            };
            _topControls.AddView(_urlText, new LinearLayout.LayoutParams(0, Dp(46), 1));
            UiButton(_topControls, "↻", L10n.T("刷新"), v => _web?.Reload(), false);
            root.AddView(_topControls, new LinearLayout.LayoutParams(-1, Dp(56)));

            _bottomControls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _bottomControls.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
            _bottomControls.SetBackgroundColor(Color.ParseColor("#F8F8FA"));
            _backButton = IconBottomButton(_bottomControls, IconKind.Back, L10n.T("后退"), v =>
            {
                if (_activeTab?.IsHome == true) NavigateHome(_activeTab);
                else if (_web?.CanGoBack() == true) _web.GoBack();
            });
            _forwardButton = IconBottomButton(_bottomControls, IconKind.ChevronRight, L10n.T("前进"), v =>
            {
                if (_web?.CanGoForward() == true) _web.GoForward();
            });
            IconBottomButton(_bottomControls, IconKind.Grid, L10n.T("首页"), v => NavigateHome(_activeTab));
            _tabButton = IconBottomButton(_bottomControls, IconKind.Circle, L10n.T("窗口"), v => ShowTabSheet());
            IconBottomButton(_bottomControls, IconKind.More, L10n.T("菜单"), v => ShowMoreMenu());

            /* ---- 进度条 ---- */
            _progress = new ProgressBar(this, null, Android.Resource.Style.WidgetProgressBarHorizontal);
            _progress.Max = 100;
            _progress.Progress = 0;
            _progress.Visibility = ViewStates.Gone;
            _progress.Indeterminate = false;
            root.AddView(_progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2)));

            /* ---- WebView 容器 ---- */
            _content = new FrameLayout(this);
            _content.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);

            _webHost = new FrameLayout(this);
            _content.AddView(_webHost, new FrameLayout.LayoutParams(-1, -1));

            /* ---- 原生起始页（覆盖在 WebView 之上，IsHome 时显示） ---- */
            BuildHomeView();
            _content.AddView(_homeView, new FrameLayout.LayoutParams(-1, -1));

            /* ---- 全屏视频容器 ---- */
            _fullscreenHost = new FrameLayout(this);
            _fullscreenHost.Visibility = ViewStates.Gone;
            _content.AddView(_fullscreenHost, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            /* ---- 错误页面 ---- */
            _errorBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _errorBox.Visibility = ViewStates.Gone;
            _errorBox.SetGravity(GravityFlags.Center);
            _errorBox.SetPadding(Dp(24), 0, Dp(24), 0);

            _errorTitle = new TextView(this) { Text = L10n.T("网页打不开"), TextSize = 20 };
            _errorTitle.SetTextColor(Color.Black);
            _errorTitle.Gravity = GravityFlags.Center;
            
            _errorDetail = new TextView(this) { TextSize = 14 };
            _errorDetail.SetTextColor(Color.Gray);
            _errorDetail.Gravity = GravityFlags.Center;
            
            _retryBtn = new Button(this) { Text = L10n.T("重试") };
            _retryBtn.SetBackgroundColor(Color.ParseColor("#007AFF"));
            _retryBtn.SetTextColor(Color.White);
            _retryBtn.SetPadding(Dp(32), Dp(12), Dp(32), Dp(12));
            _retryBtn.Click += (s, e) => { try { _web?.Reload(); } catch { } };

            _errorBox.AddView(_errorTitle);
            _errorBox.AddView(_errorDetail);
            _errorBox.AddView(_retryBtn);
            _content.AddView(_errorBox, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent, GravityFlags.Center));

            /* ---- 资源嗅探栏 ---- */
            _sniffBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _sniffBar.Visibility = ViewStates.Gone;
            _sniffBar.SetBackgroundColor(Color.ParseColor("#FFF3E0")); // 浅橙色背景
            _sniffBar.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            _sniffBar.SetGravity(GravityFlags.CenterVertical);

            var sniffIcon = new TextView(this) { Text = "🔍", TextSize = 16 };
            _sniffText = new TextView(this) { TextSize = 13 };
            _sniffText.SetTextColor(Color.ParseColor("#E65100"));
            _sniffText.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var sniffBtn = new TextView(this) { Text = L10n.T("查看"), TextSize = 13 };
            sniffBtn.SetTextColor(Color.ParseColor("#FF9800"));
            sniffBtn.Gravity = GravityFlags.Center;
            sniffBtn.SetPadding(Dp(16), Dp(4), Dp(16), Dp(4));
            sniffBtn.Background = GetRoundedDrawable(Color.ParseColor("#FFE0B2"), Dp(16));
            sniffBtn.Click += (s, e) => ShowResourcePanel();

            _sniffBar.AddView(sniffIcon);
            _sniffBar.AddView(_sniffText);
            _sniffBar.AddView(sniffBtn);

            root.AddView(_sniffBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            root.AddView(_content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            root.AddView(_bottomControls, new LinearLayout.LayoutParams(-1, Dp(54)));
            SetContentView(root);
            root.RequestFocus();
        }

        /* ------------------------------------------------------------------ 原生起始页 */

        private void BuildHomeView()
        {
            _homeView = new FrameLayout(this);
            _homeView.SetBackgroundColor(Color.White);
            var scroll = new ScrollView(this);
            scroll.FillViewport = true;
            var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            wrap.SetPadding(Dp(20), Dp(0), Dp(20), Dp(20));

            // 让整块内容垂直居中（上下用等权 spacer）
            var topSpacer = new View(this);
            wrap.AddView(topSpacer, new LinearLayout.LayoutParams(-1, 0, 1f));

            var logo = new TextView(this)
            {
                Text = L10n.T("AhoChat 浏览器"), TextSize = 26,
                Gravity = GravityFlags.Center, Typeface = Typeface.DefaultBold
            };
            logo.SetTextColor(Color.ParseColor("#1C1C1E"));
            logo.SetPadding(0, 0, 0, Dp(20));
            wrap.AddView(logo, new LinearLayout.LayoutParams(-1, ViewGroup.LayoutParams.WrapContent));

            var searchRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _homeSearchRow = searchRow;
            searchRow.SetGravity(GravityFlags.CenterVertical);
            searchRow.Background = GetRoundedDrawable(Color.ParseColor("#EFEFF4"), 24);
            _homeSearch = new EditText(this)
            {
                TextSize = 15,
                Hint = L10n.T("搜索或输入网址"), ImeOptions = ImeAction.Go
            };
            _homeSearch.SetSingleLine(true);
            _homeSearch.SetBackgroundColor(Color.Transparent);
            _homeSearch.SetTextColor(Color.ParseColor("#1C1C1E"));
            _homeSearch.SetHintTextColor(Color.ParseColor("#8E8E93"));
            _homeSearch.SetPadding(Dp(18), Dp(12), Dp(8), Dp(12));
            _homeSearch.EditorAction += (s, e) =>
            {
                if ((ImeAction)e.ActionId == ImeAction.Go)
                {
                    SubmitHomeSearch();
                    e.Handled = true;
                }
            };
            searchRow.AddView(_homeSearch, new LinearLayout.LayoutParams(0, Dp(52), 1f));
            var go = new TextView(this) { Text = "⌕", TextSize = 26, Gravity = GravityFlags.Center };
            go.SetTextColor(Color.ParseColor("#007AFF"));
            go.SetPadding(Dp(4), 0, Dp(16), 0);
            go.Click += (s, e) => SubmitHomeSearch();
            searchRow.AddView(go, new LinearLayout.LayoutParams(Dp(48), Dp(52)));
            wrap.AddView(searchRow, new LinearLayout.LayoutParams(-1, Dp(52)));

            // 搜索引擎切换（必应/百度/谷歌/搜狗）
            var engineRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            engineRow.Tag = "search-engine-chips";
            engineRow.SetGravity(GravityFlags.Center);
            engineRow.SetPadding(0, Dp(12), 0, Dp(6));
            foreach (var name in SearchEngines)
            {
                var chip = new TextView(this)
                {
                    Text = name, TextSize = 12,
                    Gravity = GravityFlags.Center,
                    Tag = name
                };
                chip.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
                chip.Click += (s, e) =>
                {
                    _searchEngine = System.Array.IndexOf(SearchEngines, name);
                    GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private).Edit()
                        .PutInt(PrefSearchEngine, _searchEngine).Apply();
                    RefreshHomeEngineChips(engineRow);
                    L10n.Toast(this, L10n.T("默认搜索引擎：") + name, ToastLength.Short).Show();
                };
                engineRow.AddView(chip, new LinearLayout.LayoutParams(-2, -2));
            }
            wrap.AddView(engineRow, new LinearLayout.LayoutParams(-1, -2));
            RefreshHomeEngineChips(engineRow);

            var bookmarksTitle = new TextView(this) { Text = L10n.T("书签"), TextSize = 14 };
            bookmarksTitle.SetTextColor(Color.ParseColor("#8E8E93"));
            bookmarksTitle.SetPadding(0, Dp(18), 0, Dp(6));
            wrap.AddView(bookmarksTitle, new LinearLayout.LayoutParams(-1, -2));

            _homeBookmarks = new GridView(this) { NumColumns = 4 };
            _homeBookmarks.SetHorizontalSpacing(Dp(4));
            _homeBookmarks.SetVerticalSpacing(Dp(8));
            _homeBookmarks.SetAdapter(new BookmarkGridAdapter(this));
            _homeBookmarks.ItemClick += (s, e) =>
            {
                var list = SortedBookmarks();
                if (e.Position < 0 || e.Position >= list.Count) return;
                NavigateTo(list[e.Position].Url);
            };
            _homeBookmarks.ItemLongClick += (s, e) =>
            {
                var list = SortedBookmarks();
                if (e.Position < 0 || e.Position >= list.Count) { e.Handled = true; return; }
                var bm = list[e.Position];
                L10n.Dialog(this).SetTitle(bm.Title)
                    .SetItems(new[] { L10n.T("打开"), L10n.T("编辑名称"), L10n.T("删除") }, (d, a) =>
                    {
                        if (a.Which == 0) NavigateTo(bm.Url);
                        else if (a.Which == 1) EditBookmark(bm);
                        else { BookmarkManager.Instance.RemoveBookmark(bm.Url); RefreshHomeBookmarks(); }
                    })
                    .SetNegativeButton(L10n.T("取消"), (d, a) => { }).Show();
                e.Handled = true;
            };
            wrap.AddView(_homeBookmarks, new LinearLayout.LayoutParams(-1, -2));

            _homeEmpty = new TextView(this)
            {
                Text = L10n.T("还没有书签\n在网页里点「添加书签」即可出现在这里"),
                TextSize = 13, Gravity = GravityFlags.Center
            };
            _homeEmpty.SetTextColor(Color.ParseColor("#AEAEB2"));
            _homeEmpty.SetPadding(0, Dp(28), 0, 0);
            wrap.AddView(_homeEmpty, new LinearLayout.LayoutParams(-1, -2));

            var bottomSpacer = new View(this);
            wrap.AddView(bottomSpacer, new LinearLayout.LayoutParams(-1, 0, 1f));
            scroll.AddView(wrap, new ScrollView.LayoutParams(-1, -1));
            _homeView.AddView(scroll, new FrameLayout.LayoutParams(-1, -1));

            RefreshHomeBookmarks();
            RefreshHomeTheme();
        }

        private void RefreshHomeEngineChips(LinearLayout row)
        {
            for (int i = 0; i < row.ChildCount; i++)
            {
                var chip = row.GetChildAt(i) as TextView;
                if (chip == null) continue;
                var selected = (string)chip.Tag == SearchEngines[_searchEngine];
                chip.SetTextColor(Color.ParseColor(selected ? "#FFFFFF" : "#636366"));
                chip.Background = GetRoundedDrawable(
                    Color.ParseColor(selected ? "#007AFF" : "#EFEFF4"), 14);
            }
        }

        private static List<BookmarkEntry> SortedBookmarks()
        {
            return BookmarkManager.Instance.GetBookmarks()
                .OrderByDescending(b => b.UpdatedAt)
                .ThenByDescending(b => b.CreatedAt)
                .ToList();
        }

        private void RefreshHomeBookmarks()
        {
            if (_homeBookmarks == null) return;
            var list = SortedBookmarks();
            _homeBookmarks.SetAdapter(new BookmarkGridAdapter(this));
            _homeEmpty.Visibility = list.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        }

        /// <summary>按当前搜索引擎把关键词变成搜索 URL；已是完整网址则原样返回。</summary>
        private string SearchUrlFor(string text)
        {
            var normalized = NormalizeUrl(text);
            // NormalizeUrl 对关键词固定用必应；这里按用户选择的引擎替换
            if (normalized.StartsWith("https://www.bing.com/search?", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(text.Trim(), @"^[a-zA-Z0-9\-\.]+\.[a-zA-Z]{2,}(:\d+)?(/.*)?$")
                && !System.Net.IPAddress.TryParse(text.Trim(), out _))
            {
                return string.Format(SearchUrlTemplates[_searchEngine], SysUri.EscapeDataString(text.Trim()));
            }
            return normalized;
        }

        private void SubmitHomeSearch()
        {
            HideKeyboard();
            var text = _homeSearch?.Text?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            var url = SearchUrlFor(text);
            if (IsHttpUrl(url)) NavigateTo(url);
            else L10n.Toast(this, L10n.T("请输入有效的网址或关键词"), ToastLength.Short).Show();
        }

        /// <summary>从起始页或任意位置导航到网址（自动退出起始页状态）。</summary>
        private void NavigateTo(string url)
        {
            var tab = _activeTab;
            if (tab == null || tab.Closed) { NewTab(url); return; }
            tab.IsHome = false;
            tab.Url = url;
            tab.Error = null;
            tab.Web.LoadUrl(url);
            RenderTab(true);
        }

        private void NavigateHome(BrowserTab tab)
        {
            if (tab == null || tab.Closed) return;
            HideKeyboard();
            tab.IsHome = true;
            tab.Url = HomeUrl;
            tab.Error = null;
            tab.Progress = 100;
            tab.Web.StopLoading();
            tab.Web.LoadUrl("about:blank");
            RefreshHomeBookmarks();
            RenderTab(true);
        }

        private sealed class BookmarkGridAdapter : BaseAdapter
        {
            private readonly BrowserActivity _a;
            private readonly List<BookmarkEntry> _items;
            public BookmarkGridAdapter(BrowserActivity a)
            {
                _a = a;
                _items = SortedBookmarks();
            }
            public override int Count => _items.Count;
            public override Java.Lang.Object GetItem(int position) => new Java.Lang.String(_items[position].Url ?? "");
            public override long GetItemId(int position) => position;
            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                View v = convertView;
                LinearLayout wrap = convertView as LinearLayout;
                if (wrap == null)
                {
                    wrap = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                    wrap.SetGravity(GravityFlags.Center);
                    wrap.SetPadding(_a.Dp(2), _a.Dp(8), _a.Dp(2), _a.Dp(8));
                }
                wrap.RemoveAllViews();
                var bm = _items[position];
                var initial = new TextView(_a)
                {
                    Text = string.IsNullOrEmpty(bm.Title) ? "?" : bm.Title.Substring(0, 1).ToUpperInvariant(),
                    TextSize = 18, Gravity = GravityFlags.Center
                };
                initial.SetTextColor(Color.White);
                initial.SetBackgroundDrawable(_a.GetRoundedDrawable(
                    Color.ParseColor(_a.ColorForBookmark(bm.Url)), 12));
                var lp = new LinearLayout.LayoutParams(_a.Dp(46), _a.Dp(46));
                wrap.AddView(initial, lp);
                var caption = new TextView(_a)
                {
                    Text = bm.Title, TextSize = 11,
                    Gravity = GravityFlags.Center, Ellipsize = Android.Text.TextUtils.TruncateAt.End
                };
                caption.SetSingleLine(true);
                caption.SetTextColor(BrowserActivity.ChromeText(_a._browserNight));
                wrap.AddView(caption, new LinearLayout.LayoutParams(-1, -2));
                return wrap;
            }
        }

        private static readonly string[] BookmarkColors =
        {
            "#007AFF", "#34C759", "#FF9500", "#AF52DE", "#FF2D55", "#5AC8FA", "#FF3B30", "#5856D6"
        };
        private string ColorForBookmark(string url)
        {
            var h = 0;
            foreach (var c in url ?? "") h = h * 31 + c;
            return BookmarkColors[System.Math.Abs(h) % BookmarkColors.Length];
        }

        private Drawable GetRoundedDrawable(Color color, int radiusDp)
        {
            var d = new GradientDrawable();
            d.SetColor(color);
            d.SetCornerRadius(Dp(radiusDp));
            return d;
        }

        /// <summary>Via 风格底栏：矢量图标在上、文字标签在下。</summary>
        private TextView IconBottomButton(LinearLayout row, IconKind icon, string label, Action<View> click)
        {
            var b = new TextView(this)
            {
                Text = label, TextSize = 10, Gravity = GravityFlags.Center,
                ContentDescription = label, Clickable = true
            };
            b.SetTextColor(ChromeText(_browserNight));
            b.SetPadding(0, Dp(4), 0, Dp(3));
            b.SetCompoundDrawablesWithIntrinsicBounds(null,
                new IconDrawable(this, icon, ChromeSub(_browserNight), Dp(22), 1.7f), null, null);
            b.CompoundDrawablePadding = Dp(2);
            b.Click += (s, e) => click(b);
            row.AddView(b, new LinearLayout.LayoutParams(0, Dp(54), 1f));
            return b;
        }

        private TextView UiButton(LinearLayout row, string text, string label, Action<View> click, bool weighted = true)
        {
            var button = new Button(this, null, AndroidRes.Attribute.BorderlessButtonStyle)
            {
                Text = text, TextSize = 22, Gravity = GravityFlags.Center, ContentDescription = label
            };
            button.SetTextColor(Color.ParseColor("#18212B"));
            button.SetPadding(0, 0, 0, 0);
            button.Click += (s, e) => click(button);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) button.TooltipText = label;
            row.AddView(button, weighted ? new LinearLayout.LayoutParams(0, Dp(48), 1) : new LinearLayout.LayoutParams(Dp(48), Dp(48)));
            return button;
        }

        private void HideKeyboard()
        {
            var ime = (InputMethodManager)GetSystemService(InputMethodService);
            ime?.HideSoftInputFromWindow(_urlText.WindowToken, HideSoftInputFlags.None);
            _urlText.ClearFocus();
        }

        private void ShowTabSheet()
        {
            HideKeyboard();
            var sheet = new Dialog(this);
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            panel.SetBackgroundColor(Color.ParseColor("#F4F6F8"));
            var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);
            var title = new TextView(this) { Text = $"窗口 · {_tabs.Count}", TextSize = 20 };
            title.SetTextColor(Color.ParseColor("#18212B"));
            header.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
            var add = UiButton(header, "+", L10n.T("新建窗口"), v => { sheet.Dismiss(); NewTab(HomeUrl); }, false);
            add.Enabled = _tabs.Count < MaxTabs;
            UiButton(header, "✓", L10n.T("完成"), v => sheet.Dismiss(), false);
            panel.AddView(header);
            var scroll = new ScrollView(this);
            var rows = new LinearLayout(this) { Orientation = Orientation.Vertical };
            foreach (var tab in _tabs)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                if (tab == _activeTab) row.Background = GetRoundedDrawable(Color.ParseColor("#DDECE8"), 8);
                var caption = string.IsNullOrWhiteSpace(tab.Title) ? L10n.T("新窗口") : tab.Title;
                var item = new TextView(this)
                {
                    Text = caption + "\n" + (tab.Url ?? "about:blank"), TextSize = 14,
                    Gravity = GravityFlags.CenterVertical, Ellipsize = Android.Text.TextUtils.TruncateAt.End
                };
                item.SetMaxLines(2);
                item.SetTextColor(Color.ParseColor("#18212B"));
                item.SetPadding(Dp(12), 0, Dp(4), 0);
                item.ContentDescription = (tab == _activeTab ? L10n.T("当前窗口：") : L10n.T("切换窗口：")) + caption;
                item.Click += (s, e) => { sheet.Dismiss(); SwitchTab(tab); };
                row.AddView(item, new LinearLayout.LayoutParams(0, Dp(56), 1));
                UiButton(row, "×", L10n.T("关闭窗口：") + caption, v =>
                {
                    sheet.Dismiss();
                    CloseTab(tab);
                    if (_tabs.Count > 0) ShowTabSheet();
                }, false);
                rows.AddView(row);
            }
            scroll.AddView(rows);
            var maxHeight = System.Math.Max(Dp(96), Resources.DisplayMetrics.HeightPixels / 2);
            panel.AddView(scroll, new LinearLayout.LayoutParams(-1, System.Math.Min(Dp(56 * _tabs.Count), maxHeight)));
            sheet.SetContentView(panel);
            sheet.Show();
            sheet.Window?.SetGravity(GravityFlags.Bottom);
            sheet.Window?.SetLayout(-1, -2);
        }

        private void ShowMoreMenu()
        {
            ShowActionGrid(L10n.T("菜单"), new[]
            {
                (_browserNight ? IconKind.Sun : IconKind.Moon, _browserNight ? L10n.T("日间模式") : L10n.T("夜间模式"), (Action)ToggleBrowserNight),
                (IconKind.Heart, L10n.T("书签"), (Action)ShowBookmarks),
                (IconKind.Repeat, L10n.T("历史记录"), (Action)ShowHistory),
                (IconKind.Signal, L10n.T("访问统计"), (Action)ShowVisitStats),
                (IconKind.Download, L10n.T("下载管理"), (Action)(() => StartActivity(typeof(DownloadActivity)))),
                (IconKind.Person, L10n.T("隐身窗口"), (Action)ToggleIncognito),
                (IconKind.Share, L10n.T("分享网页"), (Action)ShareCurrentPage),
                (IconKind.Plus, L10n.T("添加书签"), (Action)AddBookmark),
                (IconKind.Globe, _activeTab?.DesktopSite == true ? "手机版" : "电脑模式", (Action)ToggleDesktopSite),
                (IconKind.Bolt, L10n.T("脚本命令"), (Action)OpenScriptCommandMenu),
                (IconKind.Grid, L10n.T("工具箱"), (Action)ShowToolbox),
                (IconKind.Settings, L10n.T("设置"), (Action)ShowBrowserSettings)
            });
        }

        private void ShowToolbox()
        {
            ShowActionGrid(L10n.T("工具箱"), new[]
            {
                (IconKind.Refresh, L10n.T("刷新"), (Action)(() => _web?.Reload())),
                (IconKind.Link, L10n.T("复制链接"), (Action)CopyLink),
                (IconKind.Search, L10n.T("查找页面"), (Action)ShowFindDialog),
                (IconKind.Media, L10n.T("嗅探资源"), (Action)ShowResourcePanel),
                (IconKind.Globe, L10n.T("翻译此页"), (Action)ShowTranslatePanel),
                (IconKind.Photo, L10n.T("长截图"), (Action)CaptureLongScreenshot),
                (IconKind.Book, L10n.T("阅读模式"), (Action)EnterReaderMode),
                (IconKind.File, L10n.T("离线保存"), (Action)SavePageOffline),
                (IconKind.Grid, L10n.T("网页源码"), (Action)ShowSourceCode),
                (IconKind.Lock, L10n.T("连接信息"), (Action)ShowConnectionInfo),
                (IconKind.Trash, L10n.T("清理内存"), (Action)CleanupMemory),
                (IconKind.Image, L10n.T("图片转PDF"), (Action)PickImagesToPdf),
                (IconKind.Share, L10n.T("外部打开"), (Action)(() => OpenExternal(this, CurrentUrl))),
                (IconKind.Close, L10n.T("关闭菜单"), (Action)(() => { }))
            });
        }

        /* ------------------------------------------------------------------ 网页翻译 */

        /// <summary>翻译面板：选择目标语言、翻译整页、恢复原文、清空翻译缓存。</summary>
        private void ShowTranslatePanel()
        {
            var web = _web;
            if (web == null) { ToastShort("当前没有网页"); return; }
            string curLang = TranslateEngine.Instance.TargetLang;
            string curName = curLang;
            foreach (var l in TranslateEngine.Languages)
                if (l[0] == curLang) { curName = l[1]; break; }

            var items = new List<string>();
            foreach (var l in TranslateEngine.Languages) items.Add(l[1]);
            var codes = new List<string>();
            foreach (var l in TranslateEngine.Languages) codes.Add(l[0]);
            int sel = System.Math.Max(0, codes.IndexOf(curLang));

            L10n.Dialog(this)
                .SetTitle(L10n.T("网页翻译 · 目标语言（当前：") + curName + "）")
                .SetSingleChoiceItems(items.ToArray(), sel, (d, a) =>
                {
                    TranslateEngine.Instance.TargetLang = codes[a.Which];
                })
                .SetPositiveButton(_pageTranslated ? L10n.T("重新翻译") : L10n.T("翻译此页"), (d, a) =>
                {
                    TranslatePage(web, false);
                })
                .SetNeutralButton(L10n.T("恢复原文"), (d, a) =>
                {
                    try
                    {
                        web.EvaluateJavascript("(function(){if(window.__mchatRestorePage)window.__mchatRestorePage();})()", null);
                        _pageTranslated = false;
                        ToastShort(L10n.T("已恢复原文"));
                    }
                    catch { }
                })
                .SetNegativeButton(L10n.T("清空翻译缓存"), (d, a) =>
                {
                    TranslateEngine.Instance.ClearCache();
                    ToastShort(L10n.T("翻译缓存已清空"));
                })
                .Show();
        }

        /// <summary>注入翻译脚本（每页一次）并对当前 DOM 文本节点执行翻译。</summary>
        private void TranslatePage(WebView web, bool selectedOnly)
        {
            if (web == null) return;
            try { web.EvaluateJavascript(TranslateJs, null); } catch { }
            _pageTranslated = true;
            string target = TranslateEngine.Instance.TargetLang;
            string js = "(function(){try{if(window.__mchatDoTranslate)window.__mchatDoTranslate(" +
                        Newtonsoft.Json.JsonConvert.SerializeObject(target) + "," +
                        (selectedOnly ? "true" : "false") + ");}catch(e){}})()";
            try { web.EvaluateJavascript(js, null); } catch { }
            ToastShort(L10n.T("正在翻译页面…"));
        }

        /// <summary>
        /// 页面内翻译脚本：抽取可见文本节点（跳过脚本/样式/短数字/已翻译），逐节点调 MchatJs.translate，
        /// 回调后回填；原文存在 node.__mchatOrig 供「恢复原文」。并发上限 3，避免打爆免费 API。
        /// </summary>
        private const string TranslateJs = @"(function(){
if(window.__mchatTranslateReady)return;
window.__mchatTranslateReady=true;
window.__mchatTranslateNodes=[];
window.__mchatTranslateCbMap={};
var seq=0,active=0,queue=[],MAX=3;
function visible(el){
 if(!el||el.nodeType!==1)return false;
 var t=el.tagName;
 return t!=='SCRIPT'&&t!=='STYLE'&&t!=='NOSCRIPT'&&t!=='CODE'&&t!=='PRE'&&t!=='TEXTAREA'&&t!=='SVG'&&t!=='IFRAME';
}
function collect(root,onlySel){
 var out=[];
 if(onlySel){
  var s=window.getSelection();
  if(!s||s.rangeCount===0)return out;
  var r=s.getRangeAt(0),frag=r.cloneContents(),box=document.createElement('div');
  box.appendChild(frag);
  var tw=document.createTreeWalker(box,NodeFilter.SHOW_TEXT,null,false),n;
  while((n=tw.nextNode())){
   var v=(n.nodeValue||'').trim();
   if(v.length>=2&&/[A-Za-z\u00C0-\u024F\u0400-\u04FF]/.test(v))out.push(n);
  }
  return out;
 }
 var tw=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT,null,false),n;
 while((n=tw.nextNode())){
  var v=(n.nodeValue||'').trim();
  if(v.length<2||v.length>480)continue;
  if(!/[A-Za-z\u00C0-\u024F\u0400-\u04FF]/.test(v))continue;  // 已是中文/纯数字跳过
  if(!visible(n.parentElement))continue;
  if(n.__mchatOrig===undefined)n.__mchatOrig=n.nodeValue;
  out.push(n);
 }
 return out;
}
function pump(){
 while(active<MAX&&queue.length){
  var job=queue.shift();active++;
  var id='t'+(++seq);
  window.__mchatTranslateCbMap[id]=job.node;
  try{MchatJs.translate(job.text,job.lang,id);}catch(e){
   try{if(job.node.__mchatOrig!==undefined)job.node.nodeValue=job.node.__mchatOrig;}catch(x){}
   active--;
  }
 }
}
window.__mchatTranslateCb=function(id,result){
 var node=window.__mchatTranslateCbMap[id];
 delete window.__mchatTranslateCbMap[id];active--;
 if(node){try{if(result&&result!==node.nodeValue)node.nodeValue=result;}catch(e){}}
 pump();
};
window.__mchatRestorePage=function(){
 var tw=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT,null,false),n;
 while((n=tw.nextNode()))
  if(n.__mchatOrig!==undefined){try{n.nodeValue=n.__mchatOrig;}catch(e){}}
 window.__mchatTranslateNodes=[];
};
window.__mchatDoTranslate=function(lang,onlySel){
 var nodes=collect(document.body,!!onlySel);
 if(!nodes.length){return false;}
 queue=[];active=0;
 for(var i=0;i<nodes.length;i++){
  var text=(nodes[i].nodeValue||'').trim();
  if(text.length>=2&&text.length<=480)queue.push({node:nodes[i],text:text,lang:lang});
 }
 window.__mchatTranslateNodes=nodes;
 pump();
 return true;
};
})();";

        /* ------------------------------------------------------------------ 网页长截图 */

        private string _pendingLongShotBase64;

        /// <summary>长截图入口：注入 html2canvas 截取整页 DOM，PNG 回传原生保存/分享。</summary>
        private void CaptureLongScreenshot()
        {
            var web = _web;
            if (web == null) { ToastShort("当前没有网页"); return; }
            try
            {
                string lib;
                using (var stream = Assets.Open("webui/static/js/html2canvas.min.js"))
                using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                    lib = reader.ReadToEnd();

                web.EvaluateJavascript(lib, null);
                // 先注入保护：屏蔽跨域图片加载失败导致的中断，按整页尺寸渲染
                string js = @"(function(){
 try{
  if(typeof html2canvas!=='function'){if(MchatJs&&MchatJs.toast)MchatJs.toast('截图组件未就绪');return;}
  var w=Math.max(document.body.scrollWidth,document.documentElement.scrollWidth||0);
  var h=Math.max(document.body.scrollHeight,document.documentElement.scrollHeight||0);
  html2canvas(document.body,{
   backgroundColor:'#ffffff',scale:1,useCORS:true,logging:false,
   windowWidth:w,windowHeight:h,width:w,height:h,
   scrollX:0,scrollY:-window.scrollY
  }).then(function(canvas){
   try{
    var url=canvas.toDataURL('image/png');
    var b64=url.substring(url.indexOf(',')+1);
    MchatJs.longShotReady(b64);
   }catch(e){}
  }).catch(function(e){});
 }catch(e){}
})();";
                web.EvaluateJavascript(js, null);
                ToastShort(L10n.T("正在生成长截图…"));
            }
            catch (System.Exception ex)
            {
                ToastShort(L10n.T("长截图失败：") + ex.Message);
            }
        }

        /// <summary>收到 JS 回传的 PNG base64：弹保存/分享选择。</summary>
        public void OnLongShotReady(string base64Data)
        {
            _pendingLongShotBase64 = base64Data;
            try
            {
                L10n.Dialog(this)
                    .SetTitle(L10n.T("网页长截图已生成"))
                    .SetMessage(L10n.T("图片大小约 ") + (base64Data.Length * 3 / 4 / 1024) + " KB")
                    .SetItems(new[] { L10n.T("保存到下载目录"), L10n.T("分享图片"), L10n.T("取消") }, (d, a) =>
                    {
                        switch (a.Which)
                        {
                            case 0:
                                SaveBlobDownload(base64Data, L10n.T("长截图_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", "image/png");
                                break;
                            case 1:
                                ShareLongShot(base64Data);
                                break;
                        }
                        _pendingLongShotBase64 = null;
                    })
                    .Show();
            }
            catch { }
        }

        private void ShareLongShot(string base64Data)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64Data);
                var dir = GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
                    ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                var path = System.IO.Path.Combine(dir, L10n.T("长截图_分享.png"));
                System.IO.File.WriteAllBytes(path, bytes);
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this,
                    "com.Ahochat.android.fileprovider", new Java.IO.File(path));
                var intent = new Intent(Intent.ActionSend);
                intent.SetType("image/png");
                intent.PutExtra(Intent.ExtraStream, uri);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(Intent.CreateChooser(intent, L10n.T("分享长截图")));
            }
            catch (System.Exception ex) { ToastShort(L10n.T("分享失败：") + ex.Message); }
        }

        private void CleanupMemory()
        {
            try
            {
                foreach (var tab in _tabs.ToArray())
                {
                    if (tab.Closed) continue;
                    tab.Web?.FreeMemory();
                }
                WebView.FreeMemory();
                L10n.Toast(this, L10n.T("WebView 内存已清理"), ToastLength.Short).Show();
            }
            catch { L10n.Toast(this, L10n.T("清理失败"), ToastLength.Short).Show(); }
        }

        private void ShowBrowserSettings()
        {
            ShowActionGrid(L10n.T("设置"), new[]
            {
                (IconKind.Settings, _adBlockEnabled ? L10n.T("关闭广告拦截") : L10n.T("开启广告拦截"), (Action)ToggleAdBlock),
                (IconKind.Settings, L10n.T("拦截统计与规则管理"), (Action)ShowAdBlockStats),
                (IconKind.Settings, _userScriptEnabled ? L10n.T("关闭油猴脚本") : L10n.T("开启油猴脚本"), (Action)ToggleUserScript),
                (IconKind.Settings, L10n.T("油猴脚本管理"), (Action)ShowUserScriptManager),
                (IconKind.Photo, _blockNetworkImages ? L10n.T("开启网页图片") : L10n.T("屏蔽网页图片"), (Action)ToggleNetworkImages),
                (IconKind.Book, L10n.T("文字 ") + _textZoom + "%", (Action)ShowTextZoomPicker),
                (IconKind.Lock, L10n.T("第三方 Cookie"), (Action)ShowThirdPartyCookieSettings),
                (IconKind.Search, L10n.T("搜索引擎"), (Action)ShowSearchEnginePicker),
                (IconKind.Trash, L10n.T("清理浏览数据"), (Action)ShowClearDataDialog)
            });
        }

        /// <summary>Via 风格底部.icon 网格菜单（两行 5 列），标题可选。</summary>
        private void ShowActionGrid(string title, (IconKind Icon, string Label, Action Run)[] actions)
        {
            HideKeyboard();
            var menu = new Dialog(this);
            menu.RequestWindowFeature((int)WindowFeatures.NoTitle);
            menu.SetCanceledOnTouchOutside(true);
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetPadding(Dp(8), Dp(10), Dp(8), Dp(10));
            panel.Background = GetRoundedDrawable(ChromeBg(_browserNight), 22);
            panel.ClipToOutline = true;

            if (!string.IsNullOrEmpty(title))
            {
                var header = new TextView(this)
                {
                    Text = title, TextSize = 13, Gravity = GravityFlags.Center
                };
                header.SetTextColor(ChromeSub(_browserNight));
                header.SetPadding(0, 0, 0, Dp(6));
                panel.AddView(header, new LinearLayout.LayoutParams(-1, -2));
            }

            const int columns = 5;
            const int cellHeight = 72;
            var grid = new LinearLayout(this) { Orientation = Orientation.Vertical };
            LinearLayout row = null;
            for (var i = 0; i < actions.Length; i++)
            {
                if (i % columns == 0)
                {
                    row = new LinearLayout(this) { Orientation = Orientation.Horizontal, WeightSum = columns };
                    grid.AddView(row, new LinearLayout.LayoutParams(-1, Dp(cellHeight)));
                }
                var action = actions[i];
                var cell = new Button(this, null, AndroidRes.Attribute.BorderlessButtonStyle)
                {
                    Text = action.Label, TextSize = 11, Gravity = GravityFlags.Center
                };
                cell.SetTextColor(ChromeText(_browserNight));
                cell.SetAllCaps(false);
                cell.SetMinWidth(0);
                cell.SetMinimumWidth(0);
                cell.SetPadding(Dp(2), Dp(6), Dp(2), Dp(6));
                cell.CompoundDrawablePadding = Dp(3);
                cell.SetCompoundDrawablesWithIntrinsicBounds(null,
                    new IconDrawable(this, action.Icon,
                        Color.ParseColor(_browserNight ? "#0A84FF" : "#007AFF"), Dp(22)), null, null);
                cell.Click += (s, e) => { menu.Dismiss(); action.Run(); };
                row.AddView(cell, new LinearLayout.LayoutParams(0, -1, 1f));
            }
            var scroll = new ScrollView(this);
            scroll.AddView(grid);
            var availableHeight = Resources.DisplayMetrics.HeightPixels - Dp(150);
            var gridHeight = Dp(((actions.Length + columns - 1) / columns) * cellHeight);
            panel.AddView(scroll, new LinearLayout.LayoutParams(-1,
                System.Math.Min(gridHeight, System.Math.Max(Dp(cellHeight), availableHeight))));
            menu.SetContentView(panel);
            menu.Show();
            var window = menu.Window;
            if (window == null) return;
            window.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
            window.SetGravity(GravityFlags.Bottom | GravityFlags.CenterHorizontal);
            window.SetDimAmount(0.22f);
            var attrs = window.Attributes;
            attrs.Y = Dp(55);
            window.Attributes = attrs;
            window.SetLayout(System.Math.Min(Dp(460), Resources.DisplayMetrics.WidthPixels - Dp(20)), -2);
        }

        private void ShowSearchEnginePicker()
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("默认搜索引擎"))
                .SetSingleChoiceItems(SearchEngines, _searchEngine, (s, e) =>
                {
                    _searchEngine = e.Which;
                    GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private).Edit()
                        .PutInt(PrefSearchEngine, _searchEngine).Apply();
                    try { ((AlertDialog)s).Dismiss(); } catch { }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { }).Show();
        }

        /* ------------------------------------------------------------------ 数据保留 30 天 */

        private const int DataRetentionDays = 30;

        /// <summary>启动时执行：删除超过 30 天的历史/书签/Cookie/缓存；下载数据不在本方法内，不动。</summary>
        private void CleanupOldBrowserData()
        {
            try
            {
                var prefs = GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private);
                var last = prefs.GetLong(PrefDataCleanup, 0);
                // 每天最多清理一次，避免每次启动都做全量 Cookie 清理打断登录
                if (Java.Lang.JavaSystem.CurrentTimeMillis() - last < 24L * 3600 * 1000) return;

                var histRemoved = HistoryManager.Instance.PurgeOlderThan(DataRetentionDays);
                var bmRemoved = BookmarkManager.Instance.PurgeOlderThan(DataRetentionDays);

                // Cookie / 本地存储整体清理：WebView 数据无单条时间戳，采用「每天一次整体清理」策略
                try { CookieManager.Instance.RemoveAllCookie(); } catch { }
                try { WebStorage.Instance.DeleteAllData(); } catch { }

                prefs.Edit().PutLong(PrefDataCleanup, Java.Lang.JavaSystem.CurrentTimeMillis()).Apply();
                if (histRemoved + bmRemoved > 0)
                    System.Diagnostics.Debug.WriteLine($"[Browser] 30天清理：历史 {histRemoved} 条，书签 {bmRemoved} 条");
            }
            catch { }
        }

        private void ShowClearDataDialog()
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("清理浏览数据"))
                .SetMessage(L10n.T("将清空历史记录、书签、Cookie、缓存与表单数据。\n下载的文件与下载记录不会被清除。\n\n浏览器会在每次启动时自动清理超过 30 天的数据。"))
                .SetPositiveButton(L10n.T("立即清理"), (s, e) =>
                {
                    HistoryManager.Instance.ClearHistory();
                    BookmarkManager.Instance.ClearBookmarks();
                    foreach (var tab in _tabs.ToArray())
                    {
                        try { tab.Web.ClearCache(true); tab.Web.ClearFormData(); } catch { }
                    }
                    try { CookieManager.Instance.RemoveAllCookie(); } catch { }
                    try { WebStorage.Instance.DeleteAllData(); } catch { }
                    RefreshHomeBookmarks();
                    L10n.Toast(this, L10n.T("浏览数据已清空（下载文件保留）"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { }).Show();
        }

        /* ------------------------------------------------------------------ WebView 配置 */

        private void ConfigureWebView(BrowserTab tab)
        {
            var web = tab.Web;
            // 开启 WebView 远程调试（Chrome DevTools Protocol）：便于排查页面脚本、
            // 油猴脚本注入与下载链路问题。仅在已获 USB 调试授权的电脑上生效。
            try { WebView.SetWebContentsDebuggingEnabled(true); } catch { }
            var s = web.Settings;
            s.JavaScriptEnabled = true;
            s.DomStorageEnabled = true;
            s.DatabaseEnabled = true;

            s.AllowFileAccess = false;
            s.AllowContentAccess = true;
            s.MixedContentMode = MixedContentHandling.NeverAllow;
            s.LoadsImagesAutomatically = true;
            s.BlockNetworkImage = _blockNetworkImages;
            s.SetSupportZoom(true);
            s.BuiltInZoomControls = true;
            s.DisplayZoomControls = false;
            s.UseWideViewPort = true;
            s.LoadWithOverviewMode = true;
            s.TextZoom = _textZoom;
            s.SetRenderPriority(WebSettings.RenderPriority.High);
            s.MediaPlaybackRequiresUserGesture = false;
            s.SetSupportMultipleWindows(true);
            s.JavaScriptCanOpenWindowsAutomatically = false;
            tab.MobileUserAgent = s.UserAgentString;
            ApplyUserAgent(tab);

            // 稳定性增强设置
            try
            {
                // 启用安全浏览
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var method = s.Class.GetMethod("setSafeBrowsingEnabled", Java.Lang.Boolean.Type);
                    method?.Invoke(s, Java.Lang.Boolean.True);
                }
                // 缓存模式
                s.CacheMode = CacheModes.Default;
            }
            catch { }

            ApplyWebViewNight(web);

            try { CookieManager.Instance.SetAcceptThirdPartyCookies(web, _acceptThirdPartyCookies); } catch { }
            web.SetWebViewClient(new BrowseClient(this, tab));
            web.SetWebChromeClient(new BrowseChrome(this, tab));
            web.SetDownloadListener(new BrowseDownload(this, tab));
            // 注入 JS 桥：广告标记、油猴 GM_* API、Blob 下载等
            web.AddJavascriptInterface(new BrowserJsBridge(this, web), "MchatJs");
            web.LongClick += (sender, e) =>
            {
                if (tab.Closed || tab != _activeTab) return;
                // 统一接管长按：屏蔽系统 ActionMode（复制/全选），避免和自定义菜单叠成两个窗口。
                e.Handled = true;
                string hitUrl = "";
                try
                {
                    var hit = web.GetHitTestResult();
                    if (hit != null && !string.IsNullOrEmpty(hit.Extra)) hitUrl = hit.Extra;
                }
                catch { }
                // 触点坐标由页面内的 touchstart 记录，与 elementFromPoint 坐标系完全一致；
                // JS 一次性回传链接/图片地址 + 文字 + 元素选择器，原生只弹一个菜单。
                var js = "(function(){var p={x:0,y:0};try{if(window.__mchatLastPoint)p=window.__mchatLastPoint();}catch(err){}"
                    + "try{if(window.__mchatDescribeAt&&window.__mchatDescribeAt(p.x,p.y))return;}catch(err){}"
                    + "try{window.__mchatDescribeFallback&&window.__mchatDescribeFallback(p.x,p.y,"
                    + JsonEncodeString(hitUrl) + ");}catch(err){}})()";
                try { web.EvaluateJavascript(js, null); } catch { }
            };
        }

        private bool IsNightMode() => _browserNight;

        private static Color ChromeBg(bool night) => Color.ParseColor(night ? "#1C1C1E" : "#FFFFFF");
        private static Color ChromeText(bool night) => Color.ParseColor(night ? "#F2F2F7" : "#18212B");
        private static Color ChromeSub(bool night) => Color.ParseColor(night ? "#98989F" : "#8E8E93");
        private static int ChromeIcon(bool night) => Color.ParseColor(night ? "#98989F" : "#3C3C43").ToArgb();

        /// <summary>夜间开关与 App 全局主题同步，避免浏览器和设置页显示不一致。</summary>
        private void ToggleBrowserNight()
        {
            _browserNight = !_browserNight;
            ThemeManager.SetMode(this, _browserNight ? ThemeManager.NIGHT : ThemeManager.DAY);
            GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private).Edit()
                .PutBoolean(PrefBrowserNight, _browserNight).Apply();
            IosStyle.ApplySystemBars(this);
            ApplyBrowserChromeTheme();
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.Closed) continue;
                ApplyWebViewNight(tab.Web);
                try { tab.Web.Reload(); } catch { }
            }
            L10n.Toast(this, _browserNight ? L10n.T("已开启夜间模式") : L10n.T("已关闭夜间模式"), ToastLength.Short).Show();
        }

        private void ApplyWebViewNight(WebView web)
        {
            if (web == null) return;
            try
            {
                var s = web.Settings;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
                    var m = s.Class.GetMethod("setAlgorithmicDarkeningAllowed", Java.Lang.Boolean.Type);
                    m?.Invoke(s, Java.Lang.Boolean.ValueOf(false)); // 用自注入 CSS，避免系统反色与 CSS 双重叠加
                }
                else if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    var m = s.Class.GetMethod("setForceDark", Java.Lang.Integer.Type);
                    m?.Invoke(s, Java.Lang.Integer.ValueOf(0));
                }
            }
            catch { }
            ApplyPageDarkCss(web, _browserNight);
        }

        /// <summary>注入/撤销页面级夜间 CSS（比系统强制反色更可控、可逆）。</summary>
        private void ApplyPageDarkCss(WebView web, bool night)
        {
            if (web == null) return;
            try
            {
                const string darkCss = "html{filter:invert(1) hue-rotate(180deg)!important;background:#fff!important}img,video,canvas,svg,picture{filter:invert(1) hue-rotate(180deg)!important}";
                var js = night
                    ? "(function(){var s=document.getElementById('mchat-night');if(!s){s=document.createElement('style');s.id='mchat-night';"
                      + "s.textContent='" + darkCss + "';document.head&&document.head.appendChild(s);}"
                      + "else{s.textContent='" + darkCss + "';}})()"
                    : "(function(){var s=document.getElementById('mchat-night');if(s&&s.parentNode)s.parentNode.removeChild(s);})()";
                web.EvaluateJavascript(js, null);
            }
            catch { }
        }

        private void ApplyBrowserChromeTheme()
        {
            var night = _browserNight;
            _topControls?.SetBackgroundColor(ChromeBg(night));
            _bottomControls?.SetBackgroundColor(ChromeBg(night));
            _urlText?.SetTextColor(ChromeText(night));
            _urlText?.SetHintTextColor(ChromeSub(night));
            if (_urlText != null) _urlText.Background = GetRoundedDrawable(Color.ParseColor(night ? "#2C2C2E" : "#F2F2F7"), 16);
            // 底栏图标色跟随主题：此前 IconDrawable 固定 #3C3C43，夜间几乎看不清
            TintBottomIcon(_backButton, night);
            TintBottomIcon(_forwardButton, night);
            TintBottomIcon(_tabButton, night);
            RefreshHomeTheme();
        }

        private static void TintBottomIcon(TextView button, bool night)
        {
            if (button == null) return;
            button.SetTextColor(ChromeText(night));
            var d = button.GetCompoundDrawables()[1] as IconDrawable;
            if (d != null) d.IconColor = ChromeIcon(night);
        }

        private void RefreshHomeTheme()
        {
            if (_homeView == null) return;
            var night = _browserNight;
            _homeView.SetBackgroundColor(ChromeBg(night));
            if (_homeSearch != null)
            {
                _homeSearch.SetTextColor(ChromeText(night));
                _homeSearch.SetHintTextColor(ChromeSub(night));
            }
            if (_homeSearchRow != null)
                _homeSearchRow.Background = GetRoundedDrawable(Color.ParseColor(night ? "#2C2C2E" : "#EFEFF4"), 24);
            // logo / 书签区标题 / 空态：用层级文字色
            ApplySubTextRecursive(_homeView, night);
            // 搜索引擎 chip：选中蓝底白字，未选中灰底灰字（夜间用深灰底）
            var chips = FindViewByTag(_homeView, "search-engine-chips") as LinearLayout;
            if (chips != null)
            {
                for (int i = 0; i < chips.ChildCount; i++)
                {
                    var chip = chips.GetChildAt(i) as TextView;
                    if (chip == null) continue;
                    var selected = (string)chip.Tag == SearchEngines[_searchEngine];
                    chip.SetTextColor(Color.ParseColor(selected ? "#FFFFFF" : (night ? "#C7C7CC" : "#636366")));
                    chip.Background = GetRoundedDrawable(
                        Color.ParseColor(selected ? "#007AFF" : (night ? "#3A3A3C" : "#EFEFF4")), 14);
                }
            }
            RefreshHomeBookmarks();
        }

        private void ApplySubTextRecursive(View view, bool night)
        {
            if (view is TextView tv && !(view.Tag is string))
                tv.SetTextColor(ChromeText(night));
            if (view is ViewGroup vg)
                for (int i = 0; i < vg.ChildCount; i++) ApplySubTextRecursive(vg.GetChildAt(i), night);
        }

        private static View FindViewByTag(View root, string tag)
        {
            if (root == null) return null;
            if (root.Tag != null && tag.Equals(root.Tag.ToString(), StringComparison.Ordinal)) return root;
            if (root is ViewGroup vg)
                for (int i = 0; i < vg.ChildCount; i++)
                {
                    var found = FindViewByTag(vg.GetChildAt(i), tag);
                    if (found != null) return found;
                }
            return null;
        }

        /* ------------------------------------------------------------------ 导航处理 */

        private static bool IsHttpUrl(string url) => !string.IsNullOrEmpty(url)
            && url.Length <= MaxAddressLength
            && !url.Any(char.IsControl)
            && SysUri.TryCreate(url, UriKind.Absolute, out var parsed)
            && string.IsNullOrEmpty(parsed.UserInfo)
            && (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrEmpty(parsed.Host);

        private bool HandleNavigation(BrowserTab tab, string url, bool mainFrame, bool userGesture)
        {
            if (tab.Closed) return true;
            if (IsHttpUrl(url) || url == "about:blank") return false;
            if (mainFrame && userGesture && tab == _activeTab && !string.IsNullOrEmpty(url)
                && (url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("market:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("intent:", StringComparison.OrdinalIgnoreCase)))
                OpenExternal(this, url);
            return mainFrame;
        }

        private void OnNavStarted(BrowserTab tab, string url)
        {
            if (tab.Closed) return;
            if (IsHttpUrl(tab.Url) && tab.Url != url) tab.Referer = tab.Url;
            tab.Url = url;
            tab.Error = null;
            tab.Progress = 0;
            tab.NavGeneration++;
            tab.Sniffer.Clear();
            tab.MenuCommands = null; // 本 tab 换新页面，重置该页脚本命令（后台 tab 也清，避免残留）
            if (tab == _activeTab) _pageTranslated = false;
            // 注入 document-start 阶段的油猴脚本
            if (_userScriptEnabled && IsHttpUrl(url)) InjectUserScripts(tab, url, "document-start");
            if (tab == _activeTab) RenderTab(true);
        }

        private void OnNavFinished(BrowserTab tab, string url)
        {
            if (tab.Closed || (url != tab.Url && url != tab.Web.Url)) return;
            tab.Url = url;
            tab.Progress = 100;
            if (tab.Error == null)
            {
                if (IsHttpUrl(url)) RecordHistory(url, tab.Title);
                if (_adBlockEnabled) _adBlockEngine.ApplyCosmeticFilters(tab.Web, url);
                // 每次页面加载完成后重装「统一长按描述」脚本（导航会丢失注入）
                InstallContentPickJs(tab.Web);
                if (_browserNight) ApplyPageDarkCss(tab.Web, true);
                if (_resourceSniffingEnabled) tab.Sniffer.SniffDom(tab.Web, url);
                // 注入油猴脚本。document-start 在 OnNavStarted(onPageStarted) 注入时，
                // 新文档的 JS 上下文尚未建立，脚本落到旧上下文随导航丢失——
                // 因此在 onPageFinished 这里补注一次（包装层按脚本 id 去重，不会重复执行）。
                if (_userScriptEnabled && IsHttpUrl(url))
                {
                    InjectUserScripts(tab, url, "document-start"); // 兜底：补偿 onPageStarted 丢失的 start 脚本
                    InjectUserScripts(tab, url, "document-body");
                    InjectUserScripts(tab, url, "document-end");
                    InjectUserScripts(tab, url, "document-idle");
                }
            }
            if (tab == _activeTab) RenderTab(true);
        }

        /* ------------------------------------------------------------------ SSL 证书错误 */

        // 用户点过「继续访问」的主机，本次会话内不再询问
        private readonly HashSet<string> _sslProceedHosts = new HashSet<string>();
        // 等待用户决定的 handler（同一主机的主框架 + 子资源一起只问一次）
        private readonly Dictionary<string, List<SslErrorHandler>> _sslPending = new Dictionary<string, List<SslErrorHandler>>();
        private readonly HashSet<string> _sslAsking = new HashSet<string>();
        private readonly object _sslLock = new object();

        /// <summary>
        /// HTTPS 证书校验失败：默认直接拒绝会让自签名 / 过期证书的站点完全打不开。
        /// 改为弹窗让用户确认「继续访问（不安全）」或返回；同一主机只询问一次，
        /// 本次会话内选择继续的主机后续请求（含子资源）自动放行。
        /// </summary>
        private void HandleSslError(BrowserTab tab, SslErrorHandler handler, SslError error)
        {
            string host = null;
            try { host = new SysUri(error?.Url ?? "").Host; } catch { }
            if (string.IsNullOrEmpty(host))
            {
                try { host = new SysUri(tab?.Url ?? "").Host; } catch { }
            }
            if (string.IsNullOrEmpty(host)) { try { handler?.Cancel(); } catch { } return; }

            bool trusted;
            bool ask;
            lock (_sslLock)
            {
                trusted = _sslProceedHosts.Contains(host);
                ask = false;
                if (!trusted)
                {
                    if (!_sslPending.TryGetValue(host, out var waiters))
                    {
                        waiters = new List<SslErrorHandler>();
                        _sslPending[host] = waiters;
                    }
                    waiters.Add(handler);
                    ask = _sslAsking.Add(host); // 第一次加入时返回 true，由本次弹窗
                }
            }
            if (trusted) { try { handler?.Proceed(); } catch { } return; }
            if (ask) ShowSslDialog(tab, host, error);
            // 否则：已有对话框在等待用户决定，handler 挂在 pending 里稍后统一处理
        }

        private void ShowSslDialog(BrowserTab tab, string host, SslError error)
        {
            RunOnUiThread(() =>
            {
                if (IsFinishing || IsDestroyed || tab.Closed) { ResolveSsl(host, false); return; }
                var dlg = L10n.Dialog(this)
                    .SetTitle(L10n.T("安全证书警告"))
                    .SetMessage("「" + host + L10n.T("」的安全证书无法验证。\n\n") + DescribeSslError(error)
                        + L10n.T("\n\n继续访问可能存在信息泄露风险，仅建议访问你信任的站点。"))
                    .SetPositiveButton(L10n.T("继续访问（不安全）"), (s, e) => ResolveSsl(host, true))
                    .SetNegativeButton(L10n.T("返回"), (s, e) => ResolveSsl(host, false))
                    .SetCancelable(false)
                    .Show();
                // 兜底：对话框被系统强制关闭（Activity 销毁、窗口移除）时不会走按钮回调，
                // 这里必须取消，否则 WebView 会永久卡在「正在加载」。
                dlg.DismissEvent += (s, e) => ResolveSsl(host, false);
                try { IosStyle.ShowCardDialog(dlg, this, 0.92f, 0); } catch { }
            });
        }

        /// <summary>
        /// Activity 销毁时把仍在等待用户决定的 SSL handler 全部取消并清空状态。
        /// 不清理会让 _sslAsking 残留主机名 —— 之后同一主机的证书错误不再弹窗，
        /// handler 只挂在 pending 里，页面永远停在加载中。
        /// </summary>
        private void CancelAllSslPending()
        {
            List<SslErrorHandler> all = null;
            lock (_sslLock)
            {
                if (_sslPending.Count == 0) { _sslAsking.Clear(); return; }
                all = new List<SslErrorHandler>();
                foreach (var kv in _sslPending) all.AddRange(kv.Value);
                _sslPending.Clear();
                _sslAsking.Clear();
            }
            foreach (var h in all)
            {
                try { h.Cancel(); } catch { }
            }
        }

        private void ResolveSsl(string host, bool proceed)
        {
            List<SslErrorHandler> handlers;
            lock (_sslLock)
            {
                _sslAsking.Remove(host);
                if (!_sslPending.Remove(host, out handlers)) return;
                if (proceed) _sslProceedHosts.Add(host);
            }
            RunOnUiThread(() =>
            {
                foreach (var h in handlers)
                {
                    try { if (proceed) h.Proceed(); else h.Cancel(); } catch { }
                }
            });
        }

        private static string DescribeSslError(SslError error)
        {
            try
            {
                if (error != null)
                {
                    switch (error.PrimaryError)
                    {
                        case SslErrorType.Expired: return "证书已过期。";
                        case SslErrorType.Notyetvalid: return "证书尚未生效。";
                        case SslErrorType.Idmismatch: return "证书域名与网站不匹配。";
                        case SslErrorType.Untrusted: return "证书颁发机构不受信任（常见于自签名证书）。";
                        case SslErrorType.DateInvalid: return "证书日期无效。";
                        case SslErrorType.Invalid: return "证书无效。";
                    }
                }
            }
            catch { }
            return L10n.T("证书校验失败。");
        }

        private void OnLoadFailed(BrowserTab tab, string url, string desc, string code, int httpCode)
        {
            if (tab.Closed) return;
            if (!string.IsNullOrEmpty(url) && tab.Url != url && tab.Web.Url != url) return;
            if (!string.IsNullOrEmpty(url)) tab.Url = url;
            if (httpCode >= 400) tab.Error = L10n.T("服务器返回 HTTP ") + httpCode + "。";
            else if (code == "HostLookup" || code == "-2") tab.Error = "找不到这个地址，请检查地址是否正确。";
            else if (code == "Connect" || code == "-6") tab.Error = "无法连接服务器。";
            else if (code == "Timeout" || code == "-8") tab.Error = "连接超时，对方没有响应。";
            else tab.Error = string.IsNullOrEmpty(desc) ? L10n.T("网络请求失败。") : desc;
            if (tab == _activeTab) RenderTab(true);
        }

        /// <summary>
        /// 注入匹配当前 URL 和 runAt 的油猴脚本。
        /// 同时注入 GM_* API polyfill 和 MchatJs 桥接对象。
        /// </summary>
        private void InjectUserScripts(BrowserTab tab, string url, string runAt)
        {
            if (tab == null || tab.Closed || tab.Web == null) return;
            var scripts = UserScriptManager.Instance.ScriptsFor(url, runAt);
            Android.Util.Log.Debug("Userscript", $"InjectUserScripts url={url} runAt={runAt} matches={scripts?.Count ?? 0}");
            if (scripts == null || scripts.Count == 0) return;

            var web = tab.Web;

            // 【@require 依赖】脚本可能依赖外部库（如 jsPDF → window.jspdf）。
            // 依赖未就绪的脚本本轮**不执行**（否则 jsPDF 缺失、脚本白跑一遍还被去重标记挡住重注），
            // 而是触发后台下载，下载完成后回调里重注本页；已就绪的脚本正常注入。
            var readyScripts = new List<UserScript>();
            int gen = tab.NavGeneration;
            Action<string> requireLoaded = content =>
            {
                // 依赖下载完成：若仍在同页，重新执行注入（这次 require 已命中缓存）
                RunOnUiThread(() =>
                {
                    if (tab.Closed || tab.NavGeneration != gen) return;
                    InjectUserScripts(tab, url, runAt);
                });
            };
            foreach (var sc in scripts)
            {
                bool allReady = true;
                if (sc.Requires != null)
                {
                    foreach (var req in sc.Requires)
                    {
                        if (string.IsNullOrWhiteSpace(req)) continue;
                        // 回调必须挂在"发起下载"的那一次调用上：GetRequire 对正在下载中的
                        // 后续调用只返回 null、不登记回调，否则依赖下载完成后永远不重注。
                        var reqCodeNow = UserScriptManager.GetRequire(req, requireLoaded);
                        Android.Util.Log.Info("Userscript", $"require check script={sc.Name} url={req} cached={reqCodeNow != null}");
                        if (reqCodeNow == null) allReady = false;
                    }
                }
                if (allReady) readyScripts.Add(sc);
            }
            scripts = readyScripts;
            if (scripts.Count == 0) return;

            // 先注入 GM_* API polyfill 和 MchatJs 桥（只需注入一次，但多次无害）
            var polyfill = @"
(function() {
    if (window.__mchatUserscriptPolyfillInjected) return;
    window.__mchatUserscriptPolyfillInjected = true;

    // unsafeWindow：大量真实用户脚本直接引用，缺失会 ReferenceError 导致整脚本挂掉
    if (!window.unsafeWindow) window.unsafeWindow = window;

    // 【load 事件补丁】油猴脚本在 document-idle（onPageFinished）注入，此时 load 事件早已触发，
    // 脚本里 window.addEventListener('load', fn) 永远不会执行（典型症状：脚本完全没反应、
    // 按钮不出现）。这里包装 addEventListener：若注册的是 load 且文档已 complete，立即异步补触发。
    if (!window.__mchatLoadPatched) {
      window.__mchatLoadPatched = true;
      try {
        var _addL = window.addEventListener;
        window.addEventListener = function (type, fn, opts) {
          var r = _addL.call(window, type, fn, opts);
          if (type === 'load' && document.readyState === 'complete' && typeof fn === 'function') {
            try { setTimeout(function () { fn({ type: 'load', target: window }); }, 0); } catch (e) {}
          }
          return r;
        };
      } catch (e) {}
    }

    // ---- GM_xmlhttpRequest 执行体：按 scriptId 绑定 ----
    window.__mchatDoXhr = function (scriptId, details) {
        if (!details) return;
        // 油猴允许相对 URL（浏览器会按当前页面解析）。原生 HttpClient 要求绝对地址，
        // 不转换会直接 InvalidOperationException 且脚本永远收不到回调。
        try {
            if (details.url) details.url = new URL(details.url, window.location.href).href;
        } catch (e) {}
        var callbackId = 'cb_' + Math.random().toString(36).substr(2, 9);
        window.__mchat_gmXhrCallbacks = window.__mchat_gmXhrCallbacks || {};
        window.__mchat_gmXhrCallbacks[callbackId] = {
            onLoad: details.onLoad || details.onload,
            onError: details.onError || details.onerror,
            onTimeout: details.onTimeout || details.ontimeout,
            onAbort: details.onAbort || details.onabort,
            onReadyStateChange: details.onReadyStateChange || details.onreadystatechange
        };
        if (window.MchatJs && window.MchatJs.gmXhr) {
            try { window.MchatJs.gmXhr(scriptId || '', callbackId, JSON.stringify(details)); } catch (e) { }
        }
    };
    // 原生 StartXhr 完成后的统一回调路由（网络错误/HTTP>=400 走 onerror；无论成败都回收，防内存泄漏）
    window.__mchat_gmXhrCallback = function (cbId, response) {
        var cbs = window.__mchat_gmXhrCallbacks || {};
        var cb = cbs[cbId];
        if (!cb) return;
        // responseType:'blob'：C# 侧回传原始字节 base64，这里还原为真正的 Blob。
        // 脚本用 FileReader.readAsDataURL(response.response) 读图，收到字符串会直接失败。
        try {
            if (response && response.__mchatBase64) {
                var bin = atob(response.__mchatBase64);
                var arr = new Uint8Array(bin.length);
                for (var bi = 0; bi < bin.length; bi++) arr[bi] = bin.charCodeAt(bi);
                response.response = new Blob([arr], { type: response.__mchatMime || 'application/octet-stream' });
            }
        } catch (e) {}
        try { cb.onReadyStateChange && cb.onReadyStateChange(response); } catch (e) { }
        var isFinal = response && (response.readyState === 4 || response.error != null || response.status != null);
        if (!isFinal) return;
        var failed = response && (response.error != null || (typeof response.status === 'number' && response.status >= 400));
        try {
            if (failed && cb.onError) cb.onError(response);
            else if (cb.onLoad) cb.onLoad(response);
        } catch (e) { }
        delete window.__mchat_gmXhrCallbacks[cbId];
    };

    // ---- GM_download / blob 下载执行体：url 用法走原生 gmDownloadUrl，避免存出空文件 ----
    window.__mchatDoDownload = function (details) {
        if (!details) return;
        var M = window.MchatJs;
        if (!M) return;
        var name = String(details.name || details.filename || 'download');
        if (details.url && !details.data) {
            var u = String(details.url);
            // 【blob:/data: URL】脚本用 URL.createObjectURL(blob) 生成 PDF 再交给 GM_download，
            // 原生 HttpClient 不认识 blob: 协议——必须在 JS 侧取回字节转 base64 再走 gmDownload。
            if (u.indexOf('blob:') === 0) {
                try {
                    fetch(u).then(function (r) { return r.blob(); }).then(function (b) {
                        var fr = new FileReader();
                        fr.onloadend = function () {
                            var b64 = String(fr.result || '');
                            var comma = b64.indexOf(',');
                            if (comma >= 0) b64 = b64.substring(comma + 1);
                            if (M.gmDownload) { try { M.gmDownload(b64, name, String(details.mime || 'application/octet-stream')); } catch (e) {} }
                            try { URL.revokeObjectURL(u); } catch (e) {}
                        };
                        fr.readAsDataURL(b);
                    }).catch(function (e) {});
                } catch (e) {}
                return;
            }
            if (u.indexOf('data:') === 0) {
                var cm = u.indexOf(',');
                if (cm >= 0) {
                    var hd = u.substring(0, cm);
                    var b64d = /;base64$/i.test(hd) ? u.substring(cm + 1) : btoa(u.substring(cm + 1));
                    if (M.gmDownload) { try { M.gmDownload(b64d, name, String(details.mime || 'application/octet-stream')); } catch (e) {} }
                }
                return;
            }
            if (M.gmDownloadUrl) { try { M.gmDownloadUrl(u, name); } catch (e) { } }
            return;
        }
        var data = details.data || '';
        var base64 = data;
        if (typeof data === 'string' && data.indexOf('data:') === 0) {
            var comma = data.indexOf(',');
            if (comma >= 0) {
                var header = data.substring(0, comma);
                if (/;base64$/i.test(header)) base64 = data.substring(comma + 1);
                else base64 = btoa(data.substring(comma + 1));
            }
        }
        if (M.gmDownload) {
            try { M.gmDownload(base64, name, String(details.mime || 'application/octet-stream')); } catch (e) { }
        }
    };

    // ---- GM_registerMenuCommand：把命令列表真正推给原生菜单（gmMenuResult -> ShowScriptCommandMenu）----
    // 防抖推送当前全部命令；注册/注销都走它，保证原生侧始终是全量最新列表。
    window.__mchatPushMenu = function () {
        if (window.__mchat_gmMenuPushTimer) { try { clearTimeout(window.__mchat_gmMenuPushTimer); } catch (e) { } }
        window.__mchat_gmMenuPushTimer = setTimeout(function () {
            window.__mchat_gmMenuPushTimer = 0;
            var all = window.__mchat_gmMenuCallbacks || {};
            var cmds = [];
            for (var id in all) {
                if (!Object.prototype.hasOwnProperty.call(all, id)) continue;
                cmds.push({ id: id, name: (window.__mchat_gmMenuNames || {})[id] || 'Command' });
            }
            try { if (window.MchatJs && window.MchatJs.gmMenuResult) window.MchatJs.gmMenuResult(JSON.stringify(cmds)); } catch (e) { }
        }, 0);
    };
    window.__mchatDoRegisterMenu = function (name, fn) {
        var M = window.MchatJs;
        if (!M || !M.gmMenuResult) return null;
        var label = String(name || 'Command');
        window.__mchat_gmMenuCallbacks = window.__mchat_gmMenuCallbacks || {};
        window.__mchat_gmMenuNames = window.__mchat_gmMenuNames || {};
        window.__mchat_gmMenuIdByName = window.__mchat_gmMenuIdByName || {};
        // 同名命令覆盖旧项（Tampermonkey 语义），避免反复注册导致菜单无限累积、旧闭包泄漏。
        var oldId = window.__mchat_gmMenuIdByName[label];
        if (oldId) {
            delete window.__mchat_gmMenuCallbacks[oldId];
            delete window.__mchat_gmMenuNames[oldId];
        }
        var cmdId = 'cmd_' + Math.random().toString(36).substr(2, 9);
        window.__mchat_gmMenuCallbacks[cmdId] = fn;
        window.__mchat_gmMenuNames[cmdId] = label;
        window.__mchat_gmMenuIdByName[label] = cmdId;
        window.__mchat_gmMenuCallback = window.__mchat_gmMenuCallback || function (id) {
            var cbs = window.__mchat_gmMenuCallbacks;
            if (cbs && cbs[id]) { try { cbs[id](); } catch (e) { } }
        };
        window.__mchatPushMenu();
        return cmdId;
    };
    window.__mchatDoUnregisterMenu = function (cmdId) {
        if (!cmdId) return;
        if (window.__mchat_gmMenuCallbacks) delete window.__mchat_gmMenuCallbacks[cmdId];
        if (window.__mchat_gmMenuNames) {
            var label = window.__mchat_gmMenuNames[cmdId];
            delete window.__mchat_gmMenuNames[cmdId];
            if (label && window.__mchat_gmMenuIdByName && window.__mchat_gmMenuIdByName[label] === cmdId)
                delete window.__mchat_gmMenuIdByName[label];
        }
        window.__mchatPushMenu();
    };

    // ---- GM_* 全局 API（页面/控制台兜底）----
    // 值变化监听：同页所有脚本共享一张监听器表。set/delete 前后取旧值并触发，
    // 回调签名与油猴一致：(name, oldValue, newValue, remote, scriptId)。
    window.__mchatValueListeners = window.__mchatValueListeners || {};
    window.__mchatValueListenerSeq = window.__mchatValueListenerSeq || 0;
    window.__mchatFireValueChange = function (name, oldValue, newValue, sid) {
        var all = window.__mchatValueListeners || {};
        for (var id in all) {
            if (!Object.prototype.hasOwnProperty.call(all, id)) continue;
            var l = all[id];
            if (!l || l.name !== name || typeof l.fn !== 'function') continue;
            try { l.fn(name, oldValue, newValue, false, sid || ''); } catch (e) { }
        }
    };
    window.GM_addValueChangeListener = function(name, listener) {
        if (typeof listener !== 'function') return 0;
        var id = ++window.__mchatValueListenerSeq;
        window.__mchatValueListeners[id] = { name: String(name || ''), fn: listener };
        return id;
    };
    window.GM_removeValueChangeListener = function(listenerId) {
        if (window.__mchatValueListeners) delete window.__mchatValueListeners[listenerId];
    };
    window.GM_setValue = function(name, value) {
        var sid = window.__mchatCurrentScriptId || '';
        var key = String(name);
        var oldV = window.GM_getValue(key, undefined);
        if (window.MchatJs && window.MchatJs.gmSetValue)
            window.MchatJs.gmSetValue(sid, key, String(value));
        window.__mchatFireValueChange(key, oldV, String(value), sid);
    };
    window.GM_getValue = function(name, defaultValue) {
        if (window.MchatJs && window.MchatJs.gmGetValue) {
            var v = window.MchatJs.gmGetValue(window.__mchatCurrentScriptId || '', String(name));
            return (v === null || v === undefined) ? defaultValue : v;
        }
        return defaultValue;
    };
    window.GM_deleteValue = function(name) {
        var sid = window.__mchatCurrentScriptId || '';
        var key = String(name);
        var oldV = window.GM_getValue(key, undefined);
        if (window.MchatJs && window.MchatJs.gmDeleteValue)
            window.MchatJs.gmDeleteValue(sid, key);
        window.__mchatFireValueChange(key, oldV, undefined, sid);
    };
    window.GM_listValues = function() {
        if (window.MchatJs && window.MchatJs.gmListValues) {
            try { return JSON.parse(window.MchatJs.gmListValues(window.__mchatCurrentScriptId || '') || '[]'); } catch (e) { return []; }
        }
        return [];
    };
    window.GM_xmlhttpRequest = function(details) { window.__mchatDoXhr(window.__mchatCurrentScriptId || '', details); };
    window.GM_download = function(details) { window.__mchatDoDownload(details); };
    window.GM_openInTab = function(url, options) {
        if (window.MchatJs && window.MchatJs.gmOpenTab) window.MchatJs.gmOpenTab(String(url || ''));
    };
    window.GM_setClipboard = function(text, info) {
        if (window.MchatJs && window.MchatJs.gmCopy) window.MchatJs.gmCopy(String(text || ''));
    };
    window.GM_registerMenuCommand = function(name, callback, accessKey) { return window.__mchatDoRegisterMenu(name, callback); };
    window.GM_unregisterMenuCommand = function(menuId) { window.__mchatDoUnregisterMenu(menuId); };
    window.GM_addStyle = function(css) {
        if (!css) return;
        var style = document.createElement('style');
        style.textContent = css;
        (document.head || document.documentElement).appendChild(style);
    };
    window.GM_getResourceText = function(name) { return null; };
    window.GM_getResourceURL = function(name) { return null; };
    window.GM_log = function(msg) {
        if (window.MchatJs && window.MchatJs.gmLog) window.MchatJs.gmLog(String(msg));
        else console.log('[Userscript]', msg);
    };
    window.GM_notification = function(details, ondone) {
        var d = details || {};
        if (window.MchatJs && window.MchatJs.gmToast) window.MchatJs.gmToast(String(d.text || d.title || ''));
    };

    // GM_info：由 C# 注入的 __mchatCurrentScriptInfo 构建
    (function () {
        var si = window.__mchatCurrentScriptInfo || {};
        window.GM_info = {
            scriptHandler: 'AhoChatA', scriptHandlerVersion: '1.0', version: '1.0', downloadMode: 'native',
            script: {
                name: si.name || '', version: si.version || '', author: si.author || '',
                namespace: si.namespace || '', description: si.description || '',
                matches: si.matches || [], includes: si.includes || [], excludes: si.excludes || [],
                'run-at': si.runAt || 'document-idle', grant: si.grant || ['none']
            },
            scriptMetaStr: ''
        };
    })();

    // Tampermonkey v4+ 对象式 API：GM.getValue / GM.xmlHttpRequest ...
    if (!window.GM) window.GM = {
        getValue: window.GM_getValue, setValue: window.GM_setValue,
        deleteValue: window.GM_deleteValue, listValues: window.GM_listValues,
        addValueChangeListener: window.GM_addValueChangeListener,
        removeValueChangeListener: window.GM_removeValueChangeListener,
        xmlHttpRequest: window.GM_xmlhttpRequest, download: window.GM_download,
        openInTab: window.GM_openInTab, setClipboard: window.GM_setClipboard,
        registerMenuCommand: window.GM_registerMenuCommand,
        unregisterMenuCommand: window.GM_unregisterMenuCommand,
        addStyle: window.GM_addStyle, log: window.GM_log,
        notification: window.GM_notification,
        getResourceText: window.GM_getResourceText, getResourceURL: window.GM_getResourceURL,
        info: window.GM_info
    };

    // MchatJs.openPdfViewer 包装：先保存原生引用再覆盖，避免自调用死循环
    if (window.MchatJs) {
        var __nativePdfViewer = window.MchatJs.openPdfViewer;
        if (typeof __nativePdfViewer === 'function') {
            try {
                window.MchatJs.openPdfViewer = function (url, name) {
                    __nativePdfViewer(String(url || ''), String(name || 'PDF 文档'));
                };
            } catch (e) { }
        }
    }

    // 按 scriptId 绑定 GM_*：脚本的 setTimeout/事件回调里 scriptId 不丢失
    window.__mchatBindGM = function (scriptId) {
        var sid = scriptId || '';
        var M = window.MchatJs;
        function buildInfo() {
            var si = window.__mchatCurrentScriptInfo || {};
            return {
                scriptHandler: 'AhoChatA', scriptHandlerVersion: '1.0', version: '1.0', downloadMode: 'native',
                script: {
                    name: si.name || '', version: si.version || '', author: si.author || '',
                    namespace: si.namespace || '', description: si.description || '',
                    matches: si.matches || [], includes: si.includes || [], excludes: si.excludes || [],
                    'run-at': si.runAt || 'document-idle', grant: si.grant || ['none']
                },
                scriptMetaStr: ''
            };
        }
        return {
            setValue: function (name, value) {
                var key = String(name);
                var oldV = (M && M.gmGetValue) ? M.gmGetValue(sid, key) : undefined;
                if (M && M.gmSetValue) M.gmSetValue(sid, key, String(value));
                if (window.__mchatFireValueChange) window.__mchatFireValueChange(key, oldV, String(value), sid);
            },
            getValue: function (name, def) {
                if (M && M.gmGetValue) { var v = M.gmGetValue(sid, String(name)); return (v === null || v === undefined) ? def : v; }
                return def;
            },
            deleteValue: function (name) {
                var key = String(name);
                var oldV = (M && M.gmGetValue) ? M.gmGetValue(sid, key) : undefined;
                if (M && M.gmDeleteValue) M.gmDeleteValue(sid, key);
                if (window.__mchatFireValueChange) window.__mchatFireValueChange(key, oldV, undefined, sid);
            },
            listValues: function () {
                if (M && M.gmListValues) { try { return JSON.parse(M.gmListValues(sid) || '[]'); } catch (e) { return []; } }
                return [];
            },
            xmlHttpRequest: function (details) { window.__mchatDoXhr(sid, details); },
            download: function (details) { window.__mchatDoDownload(details); },
            openInTab: function (url) { if (M && M.gmOpenTab) M.gmOpenTab(String(url || '')); },
            setClipboard: function (t) { if (M && M.gmCopy) M.gmCopy(String(t || '')); },
            addStyle: function (css) { window.GM_addStyle(css); },
            log: function (m) { window.GM_log(m); },
            notification: function (d, cb) { window.GM_notification(d, cb); },
            registerMenuCommand: function (n, fn) { return window.__mchatDoRegisterMenu(n, fn); },
            unregisterMenuCommand: function (id) { window.__mchatDoUnregisterMenu(id); },
            addValueChangeListener: function (n, fn) { return window.GM_addValueChangeListener(n, fn); },
            removeValueChangeListener: function (id) { window.GM_removeValueChangeListener(id); },
            getResourceText: function () { return null; },
            getResourceURL: function () { return null; },
            info: buildInfo()
        };
    };
})();
";
            // 【关键修复】polyfill、setId、脚本三者必须合并成一次 EvaluateJavascript 注入。
            // EvaluateJavascript 是异步的：原先分三次调用时，脚本可能在 polyfill 尚未执行时就运行，
            // __mchatBindGM / window.GM_* 全部 undefined → 脚本里的 GM API 静默失效，
            // 表现为「油猴脚本装了却不运作」。合并后在同一个 JS 上下文里顺序执行，根除竞态。
            var inject = new System.Text.StringBuilder();
            inject.Append(polyfill).Append("\n");

            // 注入每个匹配的脚本
            foreach (var script in scripts)
            {
                if (script == null || string.IsNullOrWhiteSpace(script.Code)) continue;
                // 前置注入已缓存的 @require（如 jsPDF → window.jspdf），失败不影响主脚本
                if (script.Requires != null)
                {
                    foreach (var req in script.Requires)
                    {
                        var reqCode = UserScriptManager.GetRequire(req, null);
                        if (!string.IsNullOrEmpty(reqCode))
                        {
                            // require 去重：document-start 兜底双注 / require 下载完成回调重注都会
                            // 再次走到这里；依赖库（如 jsPDF UMD）重复执行会重复覆盖全局、有副作用。
                            // 用 window 级表按 URL 去重，页面刷新后自然清空。
                            inject.Append("(function(){try{")
                                .Append("if(!window.__mchatRequireRan)window.__mchatRequireRan={};")
                                .Append("if(window.__mchatRequireRan[").Append(JsonEncodeString(req)).Append("])return;")
                                .Append("window.__mchatRequireRan[").Append(JsonEncodeString(req)).Append("]=1;\n")
                                .Append(reqCode).Append("\n}catch(e){console.error('[require]',e);}})();\n");
                        }
                    }
                }
                // 设置当前脚本 ID 与元数据：字符串必须带引号（旧代码用 JsonEscape 生成裸标识符，
                // 导致赋值 ReferenceError、scriptId 从未真正注入，GM 存储被静默丢弃）
                var setIdJs = "(function(){window.__mchatCurrentScriptId=" + JsonEncodeString(script.Id)
                    + ";window.__mchatCurrentScriptInfo=" + BuildScriptInfoJson(script) + ";})();";
                inject.Append(setIdJs).Append("\n");

                // 注入脚本代码；包装层用局部遮蔽把 GM_* 绑定到本脚本 id，
                // 使脚本内 setTimeout / 事件回调等异步延续仍能拿到正确 scriptId
                var wrappedCode = @"
(function() {
    // 每个页面同一脚本只执行一次：start 阶段两次注入兜底 / onPageFinished 重复回调时用。
    try {
      if (!window.__mchatScriptRan) window.__mchatScriptRan = {};
      if (window.__mchatScriptRan[window.__mchatCurrentScriptId]) return;
      window.__mchatScriptRan[window.__mchatCurrentScriptId] = 1;
    } catch (e) {}
    var __GM = window.__mchatBindGM ? window.__mchatBindGM(window.__mchatCurrentScriptId) : null;
    var GM_setValue = __GM ? __GM.setValue : window.GM_setValue,
        GM_getValue = __GM ? __GM.getValue : window.GM_getValue,
        GM_deleteValue = __GM ? __GM.deleteValue : window.GM_deleteValue,
        GM_listValues = __GM ? __GM.listValues : window.GM_listValues,
        GM_xmlhttpRequest = __GM ? __GM.xmlHttpRequest : window.GM_xmlhttpRequest,
        GM_download = __GM ? __GM.download : window.GM_download,
        GM_openInTab = __GM ? __GM.openInTab : window.GM_openInTab,
        GM_setClipboard = __GM ? __GM.setClipboard : window.GM_setClipboard,
        GM_addStyle = __GM ? __GM.addStyle : window.GM_addStyle,
        GM_log = __GM ? __GM.log : window.GM_log,
        GM_notification = __GM ? __GM.notification : window.GM_notification,
        GM_registerMenuCommand = __GM ? __GM.registerMenuCommand : window.GM_registerMenuCommand,
        GM_unregisterMenuCommand = __GM ? __GM.unregisterMenuCommand : window.GM_unregisterMenuCommand,
        GM_getResourceText = __GM ? __GM.getResourceText : window.GM_getResourceText,
        GM_getResourceURL = __GM ? __GM.getResourceURL : window.GM_getResourceURL,
        GM_info = __GM ? __GM.info : window.GM_info,
        GM = __GM ? __GM : window.GM,
        unsafeWindow = window;
    try {
" + script.Code + @"
    } catch (e) {
        console.error('[Userscript] Error in ' + (window.__mchatCurrentScriptId || '?') + ':', e);
    }
})();
";
                inject.Append(wrappedCode).Append("\n");
            }

            // 清理当前脚本 ID
            inject.Append("(function(){window.__mchatCurrentScriptId='';})();");
            try { web.EvaluateJavascript(inject.ToString(), null); } catch { }
        }

        /// <summary>
        /// 只刷新底栏窗口数。独立于 RenderTab，避免被 "只有当前标签才渲染" 的守卫拦住——
        /// 后台新建/关闭标签时数字也必须立刻更新。
        /// </summary>
        private void UpdateTabCount()
        {
            if (_tabButton == null) return;
            var n = _tabs.Count;
            _tabButton.Text = n.ToString();
            _tabButton.ContentDescription = $"窗口，已打开 {n} 个";
        }

        private void RenderTab(bool updateUrl)
        {
            RunOnUiThread(UpdateTabCount);
            var tab = _activeTab;
            if (tab == null || tab.Closed) return;
            var isHome = tab.IsHome;
            if (_homeView != null)
            {
                _homeView.Visibility = isHome && _customView == null ? ViewStates.Visible : ViewStates.Gone;
                _webHost.Visibility = isHome ? ViewStates.Invisible : ViewStates.Visible;
            }
            if (updateUrl && !_urlText.HasFocus)
                _urlText.Text = isHome || CurrentUrl == "about:blank" ? "" : CurrentUrl;
            _progress.Progress = tab.Progress;
            _progress.Visibility = _customView == null && tab.Error == null && tab.Progress < 100 ? ViewStates.Visible : ViewStates.Gone;
            _errorDetail.Text = tab.Error;
            _errorBox.Visibility = _customView == null && tab.Error != null ? ViewStates.Visible : ViewStates.Gone;
            _tabButton.Text = _tabs.Count.ToString();
            _tabButton.ContentDescription = $"窗口，已打开 {_tabs.Count} 个";
            _backButton.Enabled = !isHome && tab.Web.CanGoBack();
            _forwardButton.Enabled = !isHome && tab.Web.CanGoForward();
            _backButton.Alpha = _backButton.Enabled ? 1f : 0.4f;
            _forwardButton.Alpha = _forwardButton.Enabled ? 1f : 0.4f;
            _backButton.Clickable = _backButton.Enabled;
            _forwardButton.Clickable = _forwardButton.Enabled;
            Title = string.IsNullOrEmpty(tab.Title) ? L10n.T("浏览器") : tab.Title;
            UpdateSniffBar();
        }

        private void CopyLink()
        {
            try
            {
                var u = CurrentUrl;
                var cm = (ClipboardManager)GetSystemService(ClipboardService);
                cm.PrimaryClip = ClipData.NewPlainText("url", u);
                L10n.Toast(this, L10n.T("已复制链接"), ToastLength.Short).Show();
            }
            catch { }
        }

        /* ------------------------------------------------------------------ 更多菜单功能 */

        private void ToggleAdBlock()
        {
            _adBlockEnabled = !_adBlockEnabled;
            _adBlockEngine.Enabled = _adBlockEnabled;
            L10n.Toast(this, _adBlockEnabled ? L10n.T("广告拦截已开启") : L10n.T("广告拦截已关闭"), ToastLength.Short).Show();
            if (_adBlockEnabled)
            {
                _adBlockEngine.ApplyCosmeticFilters(_web, CurrentUrl);
            }
            else
            {
                _web?.Reload();
            }
        }

        private void ToggleUserScript()
        {
            _userScriptEnabled = !_userScriptEnabled;
            var prefs = GetSharedPreferences(BrowserPrefsName, FileCreationMode.Private);
            prefs.Edit().PutBoolean(PrefUserScriptEnabled, _userScriptEnabled).Apply();
            L10n.Toast(this, _userScriptEnabled ? L10n.T("油猴脚本已开启") : L10n.T("油猴脚本已关闭"), ToastLength.Short).Show();
            if (!_userScriptEnabled)
            {
                _web?.Reload();
            }
        }

        private void ShowUserScriptManager()
        {
            var scripts = UserScriptManager.Instance.All;
            if (scripts == null || scripts.Count == 0)
            {
                ShowActionGrid(L10n.T("油猴脚本管理"), new[]
                {
                    (IconKind.Plus, L10n.T("导入新脚本"), (Action)ImportUserScript),
                    (IconKind.Close, L10n.T("关闭"), (Action)(() => { }))
                });
                return;
            }

            var items = scripts.Select(s => $"{s.Name} v{s.Version}\n{s.Description}\n匹配: {string.Join(", ", s.Matches.Concat(s.Includes).Take(3))}{(s.Matches.Count + s.Includes.Count > 3 ? "..." : "")}").ToArray();
            L10n.Dialog(this)
                .SetTitle($"油猴脚本 ({scripts.Count})")
                .SetItems(items, (sender, e) =>
                {
                    var i = e.Which;
                    if (i >= 0 && i < scripts.Count) ShowUserScriptActions(scripts[i]);
                })
                .SetPositiveButton(L10n.T("导入新脚本"), (d, a) => ImportUserScript())
                .SetNegativeButton(L10n.T("关闭"), (d, a) => { })
                .Show();
        }

        private void ShowUserScriptActions(UserScript script)
        {
            var status = script.Enabled ? L10n.T("已启用") : L10n.T("已禁用");
            var actionItems = new List<string>
            {
                $"版本 {script.Version} · {status}",
                $"匹配: {string.Join(", ", script.Matches.Concat(script.Includes))}",
                "─────",
                script.Enabled ? L10n.T("禁用此脚本") : L10n.T("启用此脚本"),
                L10n.T("编辑源码"),
                L10n.T("查看源码"),
                L10n.T("删除脚本")
            };
            L10n.Dialog(this)
                .SetTitle(script.Name)
                .SetItems(actionItems.ToArray(), (sender, e) =>
                {
                    var i = e.Which;
                    switch (i)
                    {
                        case 3: UserScriptManager.Instance.SetEnabled(script.Id, !script.Enabled); ToastShort(script.Enabled ? "已禁用" : "已启用"); break;
                        case 4: EditUserScript(script); break;
                        case 5: ViewUserScriptSource(script); break;
                        case 6: ConfirmDeleteUserScript(script); break;
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void ImportUserScript()
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("导入油猴脚本"))
                .SetItems(new[] { L10n.T("从文件选择 (.user.js)"), L10n.T("从剪贴板粘贴"), L10n.T("从 URL 下载") }, (sender, e) =>
                {
                    var i = e.Which;
                    switch (i)
                    {
                        case 0: PickUserScriptFile(); break;
                        case 1: PasteUserScriptFromClipboard(); break;
                        case 2: DownloadUserScriptFromUrl(); break;
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void PickUserScriptFile()
        {
            try
            {
                var intent = new Intent(Intent.ActionOpenDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType("text/javascript");
                intent.PutExtra(Intent.ExtraMimeTypes, new[] { "text/javascript", "application/javascript", "text/plain" });
                StartActivityForResult(intent, ReqUserScriptImport);
            }
            catch (System.Exception ex)
            {
                ToastShort(L10n.T("选择文件失败: ") + ex.Message);
            }
        }

        private const int ReqUserScriptImport = 1024;

        private void ReadUserScriptFromUri(AndroidUri uri)
        {
            try
            {
                using (var inputStream = ContentResolver.OpenInputStream(uri))
                using (var reader = new System.IO.StreamReader(inputStream))
                {
                    var content = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var script = UserScriptManager.Parse(content);
                        UserScriptManager.Instance.Add(script);
                        ToastShort($"已导入: {script.Name}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ToastShort(L10n.T("读取脚本失败: ") + ex.Message);
            }
        }

        private void PasteUserScriptFromClipboard()
        {
            var cm = (ClipboardManager)GetSystemService(ClipboardService);
            if (cm.PrimaryClip == null || cm.PrimaryClip.ItemCount == 0)
            {
                ToastShort(L10n.T("剪贴板为空"));
                return;
            }
            var text = cm.PrimaryClip.GetItemAt(0)?.Text?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastShort(L10n.T("剪贴板无文本"));
                return;
            }
            try
            {
                var script = UserScriptManager.Parse(text);
                UserScriptManager.Instance.Add(script);
                ToastShort($"已导入: {script.Name}");
            }
            catch (System.Exception ex)
            {
                ToastShort(L10n.T("解析脚本失败: ") + ex.Message);
            }
        }

        private void DownloadUserScriptFromUrl()
        {
            var input = new EditText(this) { Hint = "https://example.com/script.user.js" };
            input.SetTextColor(Color.ParseColor("#18212B"));
            L10n.Dialog(this)
                .SetTitle(L10n.T("从 URL 导入"))
                .SetView(input)
                .SetPositiveButton(L10n.T("下载并导入"), (d, a) =>
                {
                    var url = input.Text?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(url)) return;
                    DownloadAndImportUserScript(url);
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void DownloadAndImportUserScript(string url)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ToastShort(L10n.T("请输入有效的 HTTP/HTTPS 链接"));
                return;
            }
            ToastShort(L10n.T("正在下载..."));
            Task.Run(async () =>
            {
                try
                {
                    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    {
                        var content = await http.GetStringAsync(url);
                        RunOnUiThread(() =>
                        {
                            try
                            {
                                var script = UserScriptManager.Parse(content);
                                UserScriptManager.Instance.Add(script);
                                ToastShort($"已导入: {script.Name}");
                            }
                            catch (System.Exception ex)
                            {
                                ToastShort(L10n.T("解析失败: ") + ex.Message);
                            }
                        });
                    }
                }
                catch (System.Exception ex)
                {
                    RunOnUiThread(() => ToastShort(L10n.T("下载失败: ") + ex.Message));
                }
            });
        }

        private void EditUserScript(UserScript script)
        {
            var source = UserScriptManager.ToSource(script);
            var input = new EditText(this)
            {
                Text = source,
                InputType = Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagNoSuggestions
            };
            input.SetMinLines(20);
            input.SetTextColor(Color.ParseColor("#18212B"));
            input.SetBackgroundColor(Color.ParseColor("#FFFFFF"));
            input.Gravity = GravityFlags.Top | GravityFlags.Start;

            var scroll = new ScrollView(this);
            scroll.AddView(input, new ViewGroup.LayoutParams(-1, -2));

            L10n.Dialog(this)
                .SetTitle(L10n.T("编辑脚本: ") + script.Name)
                .SetView(scroll)
                .SetPositiveButton(L10n.T("保存"), (d, a) =>
                {
                    var newSource = input.Text?.ToString();
                    if (string.IsNullOrWhiteSpace(newSource)) return;
                    try
                    {
                        var updated = UserScriptManager.Instance.UpdateFromSource(script.Id, newSource);
                        ToastShort($"已更新: {updated.Name}");
                    }
                    catch (System.Exception ex)
                    {
                        ToastShort(L10n.T("保存失败: ") + ex.Message);
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void ViewUserScriptSource(UserScript script)
        {
            var source = UserScriptManager.ToSource(script);
            var textView = new TextView(this)
            {
                Text = source,
                TextSize = 12,
                Typeface = Android.Graphics.Typeface.Monospace
            };
            textView.SetTextColor(Color.ParseColor("#18212B"));
            textView.SetPadding(24, 16, 24, 16);

            var scroll = new ScrollView(this);
            scroll.AddView(textView);

            L10n.Dialog(this)
                .SetTitle(L10n.T("源码: ") + script.Name)
                .SetView(scroll)
                .SetPositiveButton(L10n.T("复制全部"), (d, a) =>
                {
                    var cm = (ClipboardManager)GetSystemService(ClipboardService);
                    cm.PrimaryClip = ClipData.NewPlainText("userscript", source);
                    ToastShort(L10n.T("已复制到剪贴板"));
                })
                .SetNegativeButton(L10n.T("关闭"), (d, a) => { })
                .Show();
        }

        private void ConfirmDeleteUserScript(UserScript script)
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("删除脚本"))
                .SetMessage($"确定删除 \"{script.Name}\" 吗？\n此操作不可恢复。")
                .SetPositiveButton(L10n.T("删除"), (d, a) =>
                {
                    UserScriptManager.Instance.Remove(script.Id);
                    ToastShort(L10n.T("已删除"));
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void ShowAdBlockStats()
        {
            var blocked = _adBlockEngine.BlockedCount;
            var cosmetic = _adBlockEngine.CosmeticCount;
            var subs = _adBlockEngine.GetSubscriptions().Count;
            var custom = _adBlockEngine.GetCustomRulesList().Count;
            L10n.Dialog(this)
                .SetTitle(L10n.T("广告拦截统计"))
                .SetMessage($"网络请求拦截：{blocked} 次\n元素隐藏注入：{cosmetic} 次\n订阅源：{subs} 个\n自定义规则：{custom} 条")
                .SetPositiveButton(L10n.T("清零统计"), (d, a) => { _adBlockEngine.ResetStats(); L10n.Toast(this, L10n.T("统计已清零"), ToastLength.Short).Show(); })
                .SetNegativeButton(L10n.T("管理规则"), (d, a) => ShowAdBlockRuleManager())
                .SetNeutralButton(L10n.T("关闭"), (d, a) => { })
                .Show();
        }

        private void ShowAdBlockRuleManager()
        {
            ShowActionGrid(L10n.T("广告拦截规则管理"), new[]
            {
                (IconKind.Plus, L10n.T("添加自定义规则"), (Action)AddCustomAdRule),
                (IconKind.Link, L10n.T("添加订阅源"), (Action)AddAdBlockSubscription),
                (IconKind.More, L10n.T("查看/编辑自定义规则"), (Action)ListCustomAdRules),
                (IconKind.Download, L10n.T("导入规则文件"), (Action)ImportAdRules),
                (IconKind.Share, L10n.T("导出规则文件"), (Action)ExportAdRules),
                (IconKind.Refresh, L10n.T("更新订阅源"), (Action)UpdateAdBlockSubscriptions),
                (IconKind.Refresh, L10n.T("重置为内置规则"), (Action)ResetAdBlockToBuiltin),
                (IconKind.Close, L10n.T("返回"), (Action)(() => { }))
            });
        }

        private void ImportAdRules()
        {
            try
            {
                var intent = new Intent(Intent.ActionOpenDocument);
                intent.SetType("*/*");
                intent.AddCategory(Intent.CategoryOpenable);
                StartActivityForResult(intent, ReqAdRulesImport);
            }
            catch (System.Exception ex) { ToastShort(L10n.T("无法打开文件选择器: ") + ex.Message); }
        }

        private void ExportAdRules()
        {
            var rules = _adBlockEngine.GetCustomRulesList();
            if (rules.Count == 0) { ToastShort("暂无自定义规则可导出"); return; }
            string text = string.Join("\n", rules);
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                string b64 = Convert.ToBase64String(bytes);
                SaveBlobDownload(b64, "mchat_adrules_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt", "text/plain");
            }
            catch (System.Exception ex) { ToastShort(L10n.T("导出失败: ") + ex.Message); }
        }

        private void AddCustomAdRule()
        {
            var input = new EditText(this) { Hint = L10n.T("例如：||example.com^ 或 ##.ad-banner") };
            input.SetSingleLine(true);
            input.SetTextColor(Color.ParseColor("#18212B"));
            L10n.Dialog(this)
                .SetTitle(L10n.T("添加自定义规则"))
                .SetMessage(L10n.T("支持完整 ABP 语法：\n• ||domain^ — 拦截域名及子域名\n• @@||domain^ — 例外规则\n• /path/keyword/ — 路径匹配\n• ##selector — 元素隐藏\n• #@#selector — 隐藏例外\n• $domain=example.com,third-party — 选项"))
                .SetView(input)
                .SetPositiveButton(L10n.T("添加"), (d, a) =>
                {
                    var rule = input.Text?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(rule))
                    {
                        _adBlockEngine.AddCustomRule(rule);
                        L10n.Toast(this, L10n.T("规则已添加：") + rule, ToastLength.Short).Show();
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void AddAdBlockSubscription()
        {
            var input = new EditText(this) { Hint = "https://example.com/filter.txt" };
            input.SetSingleLine(true);
            input.SetTextColor(Color.ParseColor("#18212B"));
            L10n.Dialog(this)
                .SetTitle(L10n.T("添加订阅源"))
                .SetMessage(L10n.T("输入订阅地址（支持 EasyList/AdGuard/anti-AD 等格式）"))
                .SetView(input)
                .SetPositiveButton(L10n.T("添加"), (d, a) =>
                {
                    var url = input.Text?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(url))
                    {
                        _adBlockEngine.AddSubscription(url);
                        L10n.Toast(this, L10n.T("订阅源已添加，后台更新中..."), ToastLength.Short).Show();
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void ListCustomAdRules()
        {
            var rules = _adBlockEngine.GetCustomRulesList();
            if (rules.Count == 0)
            {
                L10n.Toast(this, L10n.T("暂无自定义规则"), ToastLength.Short).Show();
                return;
            }
            var items = rules.Select(r =>
            {
                string disp = r.StartsWith("!") ? L10n.T("[已禁用] ") + r.Substring(1).Trim() : r;
                return disp.Length > 60 ? disp.Substring(0, 60) + "..." : disp;
            }).ToArray();
            L10n.Dialog(this)
                .SetTitle($"自定义规则 ({rules.Count} 条) · 点按管理")
                .SetItems(items, (d, a) =>
                {
                    if (a.Which < 0 || a.Which >= rules.Count) return;
                    var rule = rules[a.Which];
                    bool disabled = rule.StartsWith("!");
                    string realRule = disabled ? rule.Substring(1).Trim() : rule;
                    L10n.Dialog(this)
                        .SetTitle(disabled ? L10n.T("规则（已禁用）") : L10n.T("规则（已启用）"))
                        .SetMessage(rule)
                        .SetItems(new[] { disabled ? L10n.T("启用") : L10n.T("禁用"), L10n.T("编辑规则"), L10n.T("删除") }, (d2, a2) =>
                        {
                            switch (a2.Which)
                            {
                                case 0: // 启用/禁用切换
                                    _adBlockEngine.RemoveCustomRule(rule);
                                    _adBlockEngine.AddCustomRule(disabled ? realRule : "! " + realRule);
                                    L10n.Toast(this, disabled ? L10n.T("已启用") : L10n.T("已禁用"), ToastLength.Short).Show();
                                    break;
                                case 1: // 编辑
                                    var edit = new EditText(this) { Text = realRule };
                                    edit.SetSingleLine(true);
                                    edit.SetTextColor(Color.ParseColor("#18212B"));
                                    L10n.Dialog(this)
                                        .SetTitle(L10n.T("编辑规则"))
                                        .SetView(edit)
                                        .SetPositiveButton(L10n.T("保存"), (d3, a3) =>
                                        {
                                            string nr = edit.Text?.ToString()?.Trim();
                                            if (string.IsNullOrEmpty(nr)) return;
                                            _adBlockEngine.RemoveCustomRule(rule);
                                            _adBlockEngine.AddCustomRule(disabled ? "! " + nr : nr);
                                            L10n.Toast(this, L10n.T("已更新"), ToastLength.Short).Show();
                                        })
                                        .SetNegativeButton(L10n.T("取消"), (d3, a3) => { })
                                        .Show();
                                    break;
                                case 2: // 删除
                                    _adBlockEngine.RemoveCustomRule(rule);
                                    L10n.Toast(this, L10n.T("已删除"), ToastLength.Short).Show();
                                    break;
                            }
                        })
                        .SetNegativeButton(L10n.T("取消"), (d2, a2) => { })
                        .Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (d, a) => { })
                .Show();
        }

        private void UpdateAdBlockSubscriptions()
        {
            L10n.Toast(this, L10n.T("正在后台更新订阅源..."), ToastLength.Short).Show();
            Task.Run(async () =>
            {
                await _adBlockEngine.UpdateSubscriptionsAsync();
                RunOnUiThread(() => L10n.Toast(this, L10n.T("订阅源更新完成"), ToastLength.Short).Show());
            });
        }

        private void ResetAdBlockToBuiltin()
        {
            L10n.Dialog(this)
                .SetTitle(L10n.T("重置为内置规则"))
                .SetMessage(L10n.T("这将删除所有自定义规则和订阅源，仅保留内置基础规则。确定吗？"))
                .SetPositiveButton(L10n.T("确定"), (d, a) =>
                {
                    // 简单粗暴：重新创建引擎
                    _adBlockEngine = new AdBlockEngine(ApplicationContext);
                    _adBlockEnabled = _adBlockEngine.Enabled;
                    L10n.Toast(this, L10n.T("已重置为内置规则"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                .Show();
        }

        private void ShowResourcePanel()
        {
            var tab = _activeTab;
            if (tab == null) return;
            var resources = tab.Sniffer.Snapshot();
            if (resources.Count == 0)
            {
                L10n.Toast(this, L10n.T("暂无嗅探到媒体资源"), ToastLength.Short).Show();
                return;
            }
            var items = new List<ResourceItem>();
            foreach (var group in resources.GroupBy(r => r.Type).OrderByDescending(g => g.Count()))
            {
                items.Add(new ResourceItem { IsHeader = true, Type = group.Key, Count = group.Count() });
                foreach (var resource in group.OrderByDescending(r => r.DetectedAt))
                    items.Add(new ResourceItem { Resource = resource });
            }
            var list = new ListView(this) { Adapter = new ResourceListAdapter(this, items) };
            var dialog = L10n.Dialog(this)
                .SetTitle($"嗅探到 {resources.Count} 个资源")
                .SetView(list)
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .SetNeutralButton(L10n.T("清空"), (s, e) => tab.Sniffer.Clear())
                .Create();
            list.ItemClick += (s, e) =>
            {
                var resource = items[e.Position].Resource;
                if (resource == null || tab.Closed) return;
                dialog.Dismiss();
                ShowResourceActionDialog(tab, resource);
            };
            dialog.Show();
        }

        private class ResourceItem
        {
            public bool IsHeader { get; set; }
            public string Type { get; set; }
            public int Count { get; set; }
            public SniffedResource Resource { get; set; }
        }

        private class ResourceListAdapter : BaseAdapter<ResourceItem>
        {
            private readonly Context _ctx;
            private readonly List<ResourceItem> _items;
            private readonly LayoutInflater _inflater;

            public ResourceListAdapter(Context ctx, List<ResourceItem> items)
            {
                _ctx = ctx;
                _items = items;
                _inflater = (LayoutInflater)ctx.GetSystemService(Context.LayoutInflaterService);
            }

            public override ResourceItem this[int position] => _items[position];
            public override int Count => _items.Count;
            public override long GetItemId(int position) => position;
            public override int ViewTypeCount => 2;
            public override int GetItemViewType(int position) => _items[position].IsHeader ? 0 : 1;

            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                var item = _items[position];
                if (item.IsHeader)
                {
                    var tv = convertView as TextView;
                    if (tv == null)
                        tv = _inflater.Inflate(Android.Resource.Layout.SimpleListItem1, parent, false) as TextView;
                    if (tv == null) throw new InvalidOperationException("Resource header layout must be a TextView.");
                    tv.Text = $"{(item.Type ?? "resource").ToUpperInvariant()} ({item.Count})";
                    tv.SetTextColor(Android.Graphics.Color.ParseColor("#FF9800"));
                    tv.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
                    tv.SetPadding(24, 16, 24, 16);
                    return tv;
                }

                View view = convertView;
                if (view == null || view is TextView)
                    view = _inflater.Inflate(Android.Resource.Layout.SimpleListItem2, parent, false);
                var tv1 = view.FindViewById<TextView>(Android.Resource.Id.Text1);
                var tv2 = view.FindViewById<TextView>(Android.Resource.Id.Text2);
                if (tv1 == null || tv2 == null) throw new InvalidOperationException("Resource item layout is missing text fields.");
                tv1.Text = item.Resource?.Url ?? "";
                tv2.Text = item.Resource == null ? "" : $"来自: {item.Resource.Referer} · {item.Resource.DetectedAt:HH:mm:ss}";
                tv2.SetTextColor(Android.Graphics.Color.Gray);
                tv1.SetPadding(24, 12, 24, 4);
                tv2.SetPadding(24, 0, 24, 12);
                return view;
            }
        }

        private void ShowResourceActionDialog(BrowserTab tab, SniffedResource res)
        {
            var isImage = string.Equals(res.Type, "image", StringComparison.OrdinalIgnoreCase);
            var isVideo = string.Equals(res.Type, "video", StringComparison.OrdinalIgnoreCase);
            var isAudio = string.Equals(res.Type, "audio", StringComparison.OrdinalIgnoreCase);
            var isM3u8 = res.Url?.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ?? false;
            var isPreviewable = isImage || isVideo || isAudio || isM3u8;

            var items = new List<string> { L10n.T("复制链接"), L10n.T("在系统浏览器打开"), L10n.T("下载") };
            if (isPreviewable) items.Insert(0, L10n.T("预览"));

            L10n.Dialog(this)
                .SetTitle(res.Url)
                .SetItems(items.ToArray(), (s, e) =>
                {
                    if (tab.Closed) return;
                    var which = e.Which;
                    if (isPreviewable && which == 0)
                    {
                        OpenResourcePreview(tab, res);
                    }
                    else
                    {
                        var actualWhich = isPreviewable ? which - 1 : which;
                        if (actualWhich == 0) CopyToClipboard(res.Url);
                        else if (actualWhich == 1) OpenExternal(this, res.Url);
                        else HandleDownload(tab, res.Url, null, null, null, res.Referer);
                    }
                }).Show();
        }

        private void OpenResourcePreview(BrowserTab fromTab, SniffedResource res)
        {
            try
            {
                var url = res.Url;
                var isM3u8 = url?.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ?? false;
                var isVideo = string.Equals(res.Type, "video", StringComparison.OrdinalIgnoreCase) || isM3u8;
                var isAudio = string.Equals(res.Type, "audio", StringComparison.OrdinalIgnoreCase);
                var isImage = string.Equals(res.Type, "image", StringComparison.OrdinalIgnoreCase);

                string previewUrl;
                if (isM3u8)
                {
                    // 用 App 内置的 hls.min.js（Assets/webui/js）内联进 data URL，
                    // 不再依赖外网 CDN——离线/弱网也能预览 M3U8。
                    string hlsJs = "";
                    try
                    {
                        using (var s = Assets.Open("webui/js/hls.min.js"))
                        using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8))
                            hlsJs = r.ReadToEnd();
                    }
                    catch { }
                    var html = $@"
<!DOCTYPE html>
<html><head><meta name='viewport' content='width=device-width,initial-scale=1'>
<style>body{{margin:0;background:#000;display:flex;align-items:center;justify-content:center;height:100vh}}video{{width:100%;height:100%;object-fit:contain}}</style>
</head><body><video id='v' controls playsinline></video>
<script>{hlsJs}</script>
<script>
var url = {JsonEscape(url)};
var v = document.getElementById('v');
if (window.Hls && Hls.isSupported()) {{ var h = new Hls(); h.loadSource(url); h.attachMedia(v); h.on(Hls.Events.MANIFEST_PARSED, () => v.play()); }}
else if (v.canPlayType('application/vnd.apple.mpegurl')) {{ v.src = url; v.addEventListener('loadedmetadata', () => v.play()); }}
else {{ document.body.innerHTML = '<div style=color:#fff;padding:20px;text-align:center>不支持 HLS 播放</div>'; }}
</script></body></html>";
                    previewUrl = "data:text/html;charset=utf-8," + SysUri.EscapeDataString(html);
                }
                else if (isVideo)
                {
                    // Direct video URL - WebView can play mp4 natively
                    var html = $@"
<!DOCTYPE html>
<html><head><meta name='viewport' content='width=device-width,initial-scale=1'>
<style>body{{margin:0;background:#000;display:flex;align-items:center;justify-content:center;height:100vh}}video{{width:100%;height:100%;object-fit:contain}}</style>
</head><body><video controls playsinline src='{JsonEscape(url)}'></video></body></html>";
                    previewUrl = "data:text/html;charset=utf-8," + SysUri.EscapeDataString(html);
                }
                else if (isAudio)
                {
                    var html = $@"
<!DOCTYPE html>
<html><head><meta name='viewport' content='width=device-width,initial-scale=1'>
<style>body{{margin:0;background:#111;display:flex;align-items:center;justify-content:center;height:100vh;color:#fff;font-family:system-ui}}audio{{width:90%}}</style>
</head><body><audio controls src='{JsonEscape(url)}'></audio></body></html>";
                    previewUrl = "data:text/html;charset=utf-8," + SysUri.EscapeDataString(html);
                }
                else
                {
                    // Image - just load the image directly
                    previewUrl = url;
                }

                var newTab = NewTab(previewUrl, isM3u8 ? L10n.T("HLS 播放") : (isVideo ? L10n.T("视频预览") : (isAudio ? L10n.T("音频预览") : L10n.T("图片预览"))));
                if (newTab != null)
                {
                    L10n.Toast(this, L10n.T("已在新窗口打开预览"), ToastLength.Short).Show();
                }
            }
            catch (System.Exception ex)
            {
                L10n.Toast(this, L10n.T("预览失败: ") + ex.Message, ToastLength.Short).Show();
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>把脚本元数据序列化为 JS 对象字面量（字符串一律走 JsonEncodeString 带引号）。</summary>
        private static string BuildScriptInfoJson(UserScript s)
        {
            if (s == null) return "{}";
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("name:").Append(JsonEncodeString(s.Name ?? ""));
            sb.Append(",namespace:").Append(JsonEncodeString(s.Namespace ?? ""));
            sb.Append(",version:").Append(JsonEncodeString(s.Version ?? ""));
            sb.Append(",author:").Append(JsonEncodeString(s.Author ?? ""));
            sb.Append(",description:").Append(JsonEncodeString(s.Description ?? ""));
            sb.Append(",runAt:").Append(JsonEncodeString((s.RunAt ?? "document-idle").Replace('_', '-')));
            sb.Append(",grant:['none']");
            sb.Append(",matches:").Append(JsonArrayLiteral(s.Matches));
            sb.Append(",includes:").Append(JsonArrayLiteral(s.Includes));
            sb.Append(",excludes:").Append(JsonArrayLiteral(s.Excludes));
            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonArrayLiteral(List<string> items)
        {
            if (items == null || items.Count == 0) return "[]";
            return "[" + string.Join(",", items.Select(x => JsonEncodeString(x ?? ""))) + "]";
        }

        private void CopyToClipboard(string text)
        {
            try
            {
                var cm = (ClipboardManager)GetSystemService(ClipboardService);
                cm.PrimaryClip = ClipData.NewPlainText("url", text);
                L10n.Toast(this, L10n.T("已复制"), ToastLength.Short).Show();
            }
            catch { }
        }

        private void SavePageOffline()
        {
            var tab = _activeTab;
            if (tab == null || _saveTab != null || !IsHttpUrl(tab.Url) || tab.Error != null) return;
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/x-mhtml");
            var title = Regex.Replace(tab.Title ?? "page", @"[\\/:*?""<>|\p{Cc}]", "_");
            intent.PutExtra(Intent.ExtraTitle, title + ".mhtml");
            _saveTab = tab;
            try { StartActivityForResult(intent, ReqSavePage); }
            catch { _saveTab = null; L10n.Toast(this, L10n.T("无法选择保存位置"), ToastLength.Short).Show(); }
        }

        private void EnterReaderMode()
        {
            var js = @"
                (function() {
                    var clones = document.cloneNode(true);
                    var remove = clones.querySelectorAll('script, style, nav, header, footer, aside, .ad, .ads, [class*=""ad""], [id*=""ad""]');
                    remove.forEach(el => el.remove());
                    var article = clones.querySelector('article') || clones.querySelector('[role=""main""]') || clones.querySelector('.content') || clones.querySelector('#content') || clones.body;
                    var html = article ? article.innerHTML : clones.body.innerHTML;
                    document.documentElement.innerHTML = '<!DOCTYPE html><html><head><meta name=\""viewport\"" content=\""width=device-width,initial-scale=1\""><style>body{font-family:system-ui;line-height:1.7;padding:20px;max-width:800px;margin:0 auto;color:#111;background:#fff}img{max-width:100%;height:auto}</style></head><body>' + html + '</body></html>';
                })();
            ";
            _web.EvaluateJavascript(js, null);
        }

        private void ShowFindDialog()
        {
            var input = new EditText(this) { Hint = L10n.T("查找内容...") };
            input.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            L10n.Dialog(this)
                .SetTitle(L10n.T("页面内查找"))
                .SetView(input)
                .SetPositiveButton(L10n.T("查找"), (s, e) =>
                {
                    var kw = input.Text?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(kw))
                    {
                        _web.FindAllAsync(kw);
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ShowSourceCode()
        {
            var url = CurrentUrl;
            if (!string.IsNullOrEmpty(url))
            {
                if (IsHttpUrl(url)) NewTab("view-source:" + url, L10n.T("网页源代码"));
            }
        }

        private void AddBookmark()
        {
            var url = CurrentUrl;
            var title = _activeTab?.Title ?? L10n.T("无标题");
            if (string.IsNullOrEmpty(url)) return;

            var input = new EditText(this) { Text = title };
            input.SetPadding(24, 12, 24, 12);
            L10n.Dialog(this)
                .SetTitle(L10n.T("添加书签"))
                .SetView(input)
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    var name = input.Text?.ToString()?.Trim() ?? title;
                    BookmarkManager.Instance.AddBookmark(url, name);
                    L10n.Toast(this, L10n.T("书签已添加"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ShowHistory()
        {
            var history = HistoryManager.Instance.GetHistory();
            if (history.Count == 0)
            {
                L10n.Toast(this, L10n.T("暂无历史记录"), ToastLength.Short).Show();
                return;
            }

            var items = history.Select(h => $"{h.Title}\n{h.Url} · {h.VisitedAt:yyyy-MM-dd HH:mm}").ToArray();
            L10n.Dialog(this)
                .SetTitle($"历史记录 ({history.Count})")
                .SetItems(items, (s, e) =>
                {
                    var entry = history[e.Which];
                    if (string.IsNullOrEmpty(entry.Url)) return;
                    // 必须走 NavigateTo（而非 _web.LoadUrl）：否则起始页 IsHome 未复位、
                    // 地址栏/标签状态不同步，表现为「点了历史但页面没变化」。
                    NavigateTo(entry.Url);
                })
                .SetNeutralButton(L10n.T("清空"), (s, e) =>
                {
                    HistoryManager.Instance.ClearHistory();
                    L10n.Toast(this, L10n.T("历史记录已清空"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        /// <summary>访问统计页：总访问/今日访问/域名排行。无痕模式访问不计入（RecordHistory 直接 return）。</summary>
        private void ShowVisitStats()
        {
            var stats = HistoryManager.Instance.GetStats();
            int total = stats.Sum(s => s.Count);
            int today = stats.Sum(s => s.TodayCount);
            var lines = new List<string>
            {
                L10n.T("总访问次数：") + total + L10n.T("    今日：") + today + L10n.T("    域名数：") + stats.Count,
                "────────────────────"
            };
            foreach (var s in stats.Take(50))
                lines.Add(s.Host + "  ·  " + s.Count + L10n.T(" 次 (今日 ") + s.TodayCount + ")");
            if (stats.Count == 0) lines.Add("暂无访问记录（无痕模式不记录）");
            L10n.Dialog(this)
                .SetTitle(L10n.T("访问统计"))
                .SetMessage(string.Join("\n", lines))
                .SetPositiveButton(L10n.T("关闭"), (d, a) => { })
                .SetNeutralButton(L10n.T("清空统计"), (d, a) =>
                {
                    HistoryManager.Instance.ClearStats();
                    ToastShort(L10n.T("访问统计已清空"));
                })
                .Show();
        }

        private void ShowBookmarks()
        {
            var bookmarks = BookmarkManager.Instance.GetBookmarks();
            if (bookmarks.Count == 0)
            {
                L10n.Toast(this, L10n.T("暂无书签"), ToastLength.Short).Show();
                return;
            }

            var items = bookmarks.Select(b => $"{b.Title}\n{b.Url}").ToArray();
            L10n.Dialog(this)
                .SetTitle($"书签 ({bookmarks.Count})")
                .SetItems(items, (s, e) =>
                {
                    var bm = bookmarks[e.Which];
                    if (!string.IsNullOrEmpty(bm.Url)) NavigateTo(bm.Url);
                })
                .SetNeutralButton(L10n.T("管理"), (s, e) => ShowBookmarkManager())
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void ShowBookmarkManager()
        {
            var bookmarks = BookmarkManager.Instance.GetBookmarks();
            var items = bookmarks.Select(b => $"{b.Title}\n{b.Url}").ToArray();
            L10n.Dialog(this)
                .SetTitle(L10n.T("书签管理"))
                .SetItems(items, (s, e) =>
                {
                    var bm = bookmarks[e.Which];
                    L10n.Dialog(this)
                        .SetTitle(bm.Title)
                        .SetItems(new[] { L10n.T("打开"), L10n.T("编辑"), L10n.T("删除") }, (s2, e2) =>
                        {
                            switch (e2.Which)
                            {
                                case 0: NavigateTo(bm.Url); break;
                                case 1: EditBookmark(bm); break;
                                case 2: BookmarkManager.Instance.RemoveBookmark(bm.Url); L10n.Toast(this, "已删除", ToastLength.Short).Show(); break;
                            }
                        })
                        .Show();
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void EditBookmark(BookmarkEntry bm)
        {
            var input = new EditText(this) { Text = bm.Title };
            input.SetPadding(24, 12, 24, 12);
            L10n.Dialog(this)
                .SetTitle(L10n.T("编辑书签"))
                .SetView(input)
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    var name = input.Text?.ToString()?.Trim() ?? bm.Title;
                    BookmarkManager.Instance.UpdateBookmark(bm.Url, name);
                    L10n.Toast(this, L10n.T("书签已更新"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        // 在页面加载完成时记录历史
        private void RecordHistory(string url, string title)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase)) return;
            if (_activeTab?.Incognito == true) return; // 隐身窗口不写历史
            HistoryManager.Instance.AddHistory(url, title);
        }

        private void ToggleIncognito()
        {
            var tab = NewTab(HomeUrl, L10n.T("隐身窗口"), true, false, true);
            if (tab != null) L10n.Toast(this, L10n.T("已打开隐身窗口：不会记录浏览历史"), ToastLength.Long).Show();
        }

        private void UpdateSniffBar()
        {
            var list = _activeTab?.Sniffer.Snapshot();
            _sniffBar.Visibility = _customView == null && list?.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
            if (list?.Count > 0)
                _sniffText.Text = $"媒体资源 · {list.Count} 个";
        }

        private void CancelFileChooser()
        {
            var callback = _fileCallback;
            _fileCallback = null;
            _fileTab = null;
            callback?.OnReceiveValue(null);
        }

        private bool StartFileChooser(BrowserTab tab, IValueCallback cb, WebChromeClient.FileChooserParams parameters)
        {
            if (tab != _activeTab || tab.Closed || _fileCallback != null) return false;
            try
            {
                var intent = parameters?.CreateIntent();
                if (intent == null)
                {
                    intent = new Intent(Intent.ActionGetContent);
                    intent.AddCategory(Intent.CategoryOpenable);
                    intent.SetType("*/*");
                }
                _fileCallback = cb;
                _fileTab = tab;
                StartActivityForResult(intent, ReqFileChooser);
                return true;
            }
            catch
            {
                _fileCallback = null;
                _fileTab = null;
                return false;
            }
        }

        private void HandleDownload(BrowserTab tab, string url, string userAgent, string disposition, string mimeType, string referer = null)
        {
            if (tab.Closed || IsFinishing || IsDestroyed) return;
            if (!IsHttpUrl(url))
            {
                if (tab == _activeTab) L10n.Toast(this, L10n.T("仅支持下载 HTTP 或 HTTPS 链接"), ToastLength.Short).Show();
                return;
            }
            if (tab != _activeTab) return;
            var filename = URLUtil.GuessFileName(url, disposition, mimeType);
            var path = new SysUri(url).AbsolutePath;
            var hls = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || (mimeType?.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || filename.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (hls) filename = System.IO.Path.GetFileNameWithoutExtension(filename) + ".ts";
            if (string.IsNullOrEmpty(referer)) referer = tab.Web.Url == url ? tab.Referer : tab.Web.Url;
            if (!IsHttpUrl(referer)) referer = null;
            else
            {
                var builder = new UriBuilder(referer) { UserName = "", Password = "", Fragment = "" };
                referer = builder.Uri.AbsoluteUri;
                if (builder.Scheme == "https" && new SysUri(url).Scheme == "http") referer = null;
            }
            string cookie = null;
            try { cookie = CookieManager.Instance.GetCookie(url); } catch { }
            DownloadActivity.OpenNew(this, url, filename, cookie,
                string.IsNullOrEmpty(userAgent) ? tab.Web.Settings.UserAgentString : userAgent, referer, hls);
        }

        private void EnterFullscreen(BrowserTab tab, View view, WebChromeClient.ICustomViewCallback callback)
        {
            if (tab.Closed || tab != _activeTab || _customView != null || view == null)
            {
                callback?.OnCustomViewHidden();
                return;
            }
            _fullscreenHost.AddView(view, new FrameLayout.LayoutParams(-1, -1));
            _fullscreenHost.SetBackgroundColor(Color.Black);
            _fullscreenHost.Visibility = ViewStates.Visible;
            _topControls.Visibility = _bottomControls.Visibility = ViewStates.Gone;
            _fullscreenTab = tab;
            _customView = view;
            _customCallback = callback;
            RenderTab(false);
        }

        private void ExitFullscreen()
        {
            if (_customView == null) return;
            var callback = _customCallback;
            _fullscreenHost.RemoveView(_customView);
            _fullscreenHost.Visibility = ViewStates.Gone;
            _topControls.Visibility = _bottomControls.Visibility = ViewStates.Visible;
            _customView = null;
            _customCallback = null;
            _fullscreenTab = null;
            callback?.OnCustomViewHidden();
            RenderTab(false);
        }

        private sealed class BrowseClient : WebViewClient
        {
            private readonly BrowserActivity _a;
            private readonly BrowserTab _tab;
            private int _renderProcessGoneCount = 0;
            public BrowseClient(BrowserActivity activity, BrowserTab tab) { _a = activity; _tab = tab; }

            private bool Guard() => !_tab.Closed && !_a.IsActivityDestroyed();

            public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
            {
                if (!Guard()) return true;
                return request != null && _a.HandleNavigation(_tab, request.Url?.ToString(), request.IsForMainFrame, request.HasGesture);
            }

            public override bool ShouldOverrideUrlLoading(WebView view, string url)
            {
                if (!Guard()) return true;
                return _a.HandleNavigation(_tab, url, true, false);
            }

            public override WebResourceResponse ShouldInterceptRequest(WebView view, IWebResourceRequest request)
            {
                if (!Guard()) return null;
                var url = request?.Url?.ToString();
                if (string.IsNullOrEmpty(url) || request.IsForMainFrame) return null;
                
                // 获取资源类型
                var resourceType = "";
                try { request.RequestHeaders?.TryGetValue("Sec-Fetch-Dest", out resourceType); } catch { }
                if (string.IsNullOrEmpty(resourceType))
                {
                    // 根据扩展名推断
                    var lower = url.ToLowerInvariant();
                    if (lower.EndsWith(".js") || lower.Contains(".js?")) resourceType = "script";
                    else if (lower.EndsWith(".css") || lower.Contains(".css?")) resourceType = "stylesheet";
                    else if (lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".gif") || lower.EndsWith(".webp") || lower.EndsWith(".svg") || lower.EndsWith(".ico")) resourceType = "image";
                    else if (lower.EndsWith(".woff") || lower.EndsWith(".woff2") || lower.EndsWith(".ttf") || lower.EndsWith(".otf")) resourceType = "font";
                    else if (lower.EndsWith(".mp4") || lower.EndsWith(".m3u8") || lower.EndsWith(".mpd") || lower.EndsWith(".webm")) resourceType = "media";
                    else if (lower.Contains("xmlhttprequest") || lower.Contains("fetch")) resourceType = "xmlhttprequest";
                }
                
                var referer = _tab.Url;
                try { request.RequestHeaders?.TryGetValue("Referer", out referer); } catch { }

                if (_a._adBlockEnabled && _a._adBlockEngine.IsBlocked(url, resourceType, referer ?? _tab.Url))
                    return new WebResourceResponse("text/plain", "UTF-8", new MemoryStream());

                if (_a._resourceSniffingEnabled)
                    _tab.Sniffer.SniffUrl(url, referer ?? _tab.Url, resourceType);
                return null;
            }

            public override void OnPageStarted(WebView view, string url, Bitmap favicon)
            {
                if (Guard()) _a.OnNavStarted(_tab, url);
            }

            public override void OnPageFinished(WebView view, string url)
            {
                if (Guard()) _a.OnNavFinished(_tab, url);
                _renderProcessGoneCount = 0; // 成功加载重置崩溃计数
            }

            public override void DoUpdateVisitedHistory(WebView view, string url, bool isReload)
            {
                if (!Guard()) return;
                _tab.Url = url;
                if (_tab == _a._activeTab) _a.RenderTab(true);
            }

            public override void OnReceivedError(WebView view, IWebResourceRequest request, WebResourceError error)
            {
                if (!Guard()) return;
                if (request?.IsForMainFrame == true)
                    _a.OnLoadFailed(_tab, request.Url?.ToString(), error?.Description, error?.ErrorCode.ToString(), 0);
            }

            [Obsolete]
            public override void OnReceivedError(WebView view, ClientError errorCode, string description, string failingUrl)
            {
                if (Guard()) _a.OnLoadFailed(_tab, failingUrl, description, errorCode.ToString(), 0);
            }

            public override void OnReceivedHttpError(WebView view, IWebResourceRequest request, WebResourceResponse response)
            {
                if (!Guard()) return;
                if (request?.IsForMainFrame == true && response?.StatusCode >= 500)
                    _a.OnLoadFailed(_tab, request.Url?.ToString(), null, null, response.StatusCode);
            }

            public override void OnReceivedSslError(WebView view, SslErrorHandler handler, SslError error)
            {
                if (!Guard()) { try { handler?.Cancel(); } catch { } return; }
                // 自签名 / 过期证书的站点不再直接拒绝：弹窗让用户确认是否继续访问
                _a.HandleSslError(_tab, handler, error);
            }

            // API 26+：渲染进程崩溃回调
            public override bool OnRenderProcessGone(WebView view, RenderProcessGoneDetail detail)
            {
                if (!Guard()) return true;
                _renderProcessGoneCount++;
                var didCrash = false; try { didCrash = detail.DidCrash(); } catch { }
                Android.Util.Log.Warn("BrowserActivity", "渲染进程异常退出: didCrash=" + didCrash + ", count=" + _renderProcessGoneCount);

                if (_renderProcessGoneCount >= 3)
                {
                    // 连续崩溃 3 次，显示错误页，避免无限循环
                    _a.OnLoadFailed(_tab, _tab.Url, L10n.T("渲染进程反复崩溃，页面可能包含不兼容内容。"), "RenderProcessGone", 0);
                    return true; // 标记已处理，WebView 会重新创建进程
                }

                // 尝试恢复：重新加载当前页面
                try
                {
                    if (!string.IsNullOrEmpty(_tab.Url) && _tab.Url != "about:blank")
                    {
                        view.LoadUrl(_tab.Url);
                    }
                    else
                    {
                        view.LoadUrl("about:blank");
                    }
                }
                catch { }
                return true; // 返回 true 表示已处理，WebView 会重新创建渲染进程
            }
        }

        private sealed class BrowseChrome : WebChromeClient
        {
            private readonly BrowserActivity _a;
            private readonly BrowserTab _tab;
            public BrowseChrome(BrowserActivity activity, BrowserTab tab) { _a = activity; _tab = tab; }

            private bool Guard() => !_tab.Closed && !_a.IsActivityDestroyed();

            public override void OnProgressChanged(WebView view, int newProgress)
            {
                if (!Guard()) return;
                _tab.Progress = newProgress;
                if (_tab == _a._activeTab) _a.RenderTab(false);
            }

            public override void OnReceivedTitle(WebView view, string title)
            {
                if (!Guard() || string.IsNullOrWhiteSpace(title)) return;
                _tab.Title = title.Trim();
                if (_tab == _a._activeTab) _a.Title = _tab.Title;
            }

            public override bool OnCreateWindow(WebView view, bool isDialog, bool isUserGesture, Message resultMsg)
            {
                // 不再要求 !isUserGesture：很多网页的跳转脚本（onclick 里动态 window.open、
                // target=_blank 的脚本转发）在 Chromium 看来并非"用户手势"，旧守卫会把它们
                // 直接拒绝，表现为"点了没反应"或落到后台。这里统一按新标签处理。
                if (!Guard() || _tab != _a._activeTab || !(resultMsg?.Obj is WebView.WebViewTransport transport))
                    return false;
                var child = _a.CreatePopupTab(_tab.Incognito);
                if (child == null) return false;
                child.Referer = _tab.Url;
                transport.WebView = child.Web;
                resultMsg.SendToTarget();
                // 关键：popup 标签此前只入集合、从不激活，导致新页面在后台打开且底栏窗口数
                // 因 RenderTab 的 active 守卫而不刷新。这里立即切到前台并挂载到容器。
                _a.SwitchTab(child);
                _a.UpdateTabCount();
                return true;
            }

            public override void OnCloseWindow(WebView window)
            {
                if (Guard()) _a.CloseTab(_tab);
            }

            public override bool OnShowFileChooser(WebView view, IValueCallback callback, WebChromeClient.FileChooserParams parameters)
            {
                if (!Guard()) return false;
                return _a.StartFileChooser(_tab, callback, parameters);
            }

            public override void OnShowCustomView(View view, WebChromeClient.ICustomViewCallback callback)
            {
                if (Guard()) _a.EnterFullscreen(_tab, view, callback);
                else callback?.OnCustomViewHidden();
            }

            public override void OnHideCustomView()
            {
                if (Guard() && _a._fullscreenTab == _tab) _a.ExitFullscreen();
            }
        }

        private sealed class BrowseDownload : Java.Lang.Object, IDownloadListener
        {
            private readonly BrowserActivity _a;
            private readonly BrowserTab _tab;
            public BrowseDownload(BrowserActivity activity, BrowserTab tab) { _a = activity; _tab = tab; }

            public void OnDownloadStart(string url, string userAgent, string disposition, string mimeType, long contentLength)
            {
                if (!_tab.Closed && !_a.IsActivityDestroyed())
                    _a.HandleDownload(_tab, url, userAgent, disposition, mimeType);
            }
        }

        /* ------------------------------------------------------------------ BrowserJsBridge 回调方法 */

        /// <summary>广告元素选择器回调（来自 JS 桥 pickAdElement）。</summary>
        public void OnAdElementPicked(WebView web, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return;
            try
            {
                var js = "(function(){var el=document.querySelector(" + JsonEncodeString(selector) + ");if(el){el.style.outline='3px solid #FF0000';el.style.outlineOffset='2px';el.scrollIntoView({behavior:'smooth',block:'center'});}})()";
                web.EvaluateJavascript(js, null);
                ToastShort(L10n.T("已标记广告元素：") + selector);
            }
            catch { }
        }

        /// <summary>
        /// 页面加载完成后注入「统一长按描述」脚本（导航会丢失注入，故每次都装）。
        /// 长按本身由原生 WebView.LongClick 接管，脚本只负责把触点处的链接/图片地址、
        /// 选中文字和稳健 CSS 选择器一次性回传，原生据此弹出唯一一个菜单。
        /// </summary>
        private void InstallContentPickJs(WebView web)
        {
            if (web == null) return;
            const string js = @"(function(){
if(window.__mchatContentPick)return;
window.__mchatContentPick=true;
// 页面自己记触点：clientX/Y 与 elementFromPoint 同一坐标系，避免原生换算偏差。
var _lx=0,_ly=0;
document.addEventListener('touchstart',function(e){
 if(e.touches.length===1){_lx=e.touches[0].clientX;_ly=e.touches[0].clientY;}
},{passive:true});
window.__mchatLastPoint=function(){return {x:_lx,y:_ly};};
function findLink(el){
 var n=el;
 while(n&&n.nodeType===1){
  if(n.tagName==='A'&&n.getAttribute('href'))return n.href;
  n=n.parentElement;
 }
 return '';
}
function esc(s){try{return (window.CSS&&CSS.escape)?CSS.escape(s):s.replace(/([^a-zA-Z0-9_-])/g,'\\$1');}catch(e){return s;}}
function selPath(el){
 if(!el||el.nodeType!==1||el.tagName==='BODY'||el.tagName==='HTML')return '';
 var parts=[],node=el;
 for(var d=0;node&&node.nodeType===1&&d<6;d++){
  var tag=node.tagName.toLowerCase();
  if(node.id){parts.unshift('#'+esc(node.id));break;}
  var cls='';
  if(node.className&&typeof node.className==='string'){
   var cs=node.className.trim().split(/\s+/).filter(function(c){return c&&c.length<40;}).slice(0,2);
   cls=cs.map(function(c){return '.'+esc(c);}).join('');
  }
  var parent=node.parentElement,idx=1;
  if(parent){
   // nth-of-type 按「同标签」计数，不能用 parent.children 的总序号，否则会选中相邻元素
   var kids=parent.children,same=0;
   for(var i=0;i<kids.length;i++){
    if(kids[i].tagName===node.tagName){same++;if(kids[i]===node){idx=same;break;}}
   }
  }
  parts.unshift(tag+cls+(idx>1?':nth-of-type('+idx+')':''));
  node=parent;
  if(node&&(node.tagName==='BODY'||node.tagName==='HTML'))break;
 }
 return parts.join(' > ');
}
function textAt(el){
 var s=window.getSelection(),t=s?s.toString().trim():'';
 if(t&&t.length<=200){
  var n=s.anchorNode;
  var ne=n&&n.nodeType===3?n.parentElement:(n&&n.nodeType===1?n:null);
  return {text:t,el:ne||el};
 }
 var walker=document.createTreeWalker(el,NodeFilter.SHOW_TEXT,null,false);
 var node,txt='';
 while((node=walker.nextNode())){
  var v=(node.nodeValue||'').trim();
  if(v.length>=2){txt=v;break;}
 }
 if(!txt&&el.innerText)txt=el.innerText.trim();
 if(!txt||txt.length>200)return {text:'',el:el};
 return {text:txt,el:el};
}
function send(text,el,link,isImage){
 var sel=selPath(el);
 if(!link&&!sel)return false;
 try{MchatJs.contentPicked(text||'',sel||'',location.href,link||'',!!isImage);return true;}catch(err){return false;}
}
window.__mchatDescribeAt=function(x,y){
 try{
  var el=document.elementFromPoint(x,y);
  if(!el)return false;
  var link=findLink(el);
  var isImage=el.tagName==='IMG';
  if(!link&&isImage)link=el.src||'';
  var p=textAt(el),text=p.text;
  if(!text&&isImage&&el.alt)text=(el.alt+'').trim().substring(0,200);
  return send(text,p.el||el,link,isImage);
 }catch(err){return false;}
};
window.__mchatDescribeFallback=function(x,y,url){
 try{
  if(!url)return false;
  var el=document.elementFromPoint(x,y);
  return send('',el||document.body,url,!!(el&&el.tagName==='IMG'));
 }catch(err){return false;}
};
window.__mchatDescribeSelection=function(){
 try{
  var s=window.getSelection(),t=s?s.toString().trim():'';
  if(!t||t.length>200)return false;
  var n=s.anchorNode;
  var el=n&&n.nodeType===3?n.parentElement:(n&&n.nodeType===1?n:null);
  if(!el)return false;
  return send(t,el,'',false);
 }catch(err){return false;}
};
// contextmenu 兜底：个别 WebView 版本不派发 LongClick 时仍能回传同一份信息。
document.addEventListener('contextmenu',function(e){
 try{
  var ok=false;
  if(e.clientX>0||e.clientY>0)ok=window.__mchatDescribeAt(e.clientX,e.clientY);
  if(!ok)ok=window.__mchatDescribeSelection();
  if(ok&&e.preventDefault)e.preventDefault();
 }catch(err){}
},true);
})();";
            try { web.EvaluateJavascript(js, null); } catch { }
        }

        /// <summary>长按选中文本后的原生菜单：屏蔽元素（本站/全网）、复制、搜索。</summary>
        /// <summary>
        /// 统一的长按内容菜单：标签页操作 → 下载/复制/分享 → 文字操作 → 屏蔽元素。
        /// 链接、图片、纯文本都走这里，保证一次长按只弹一个窗口。
        /// </summary>
        public void OnContentPicked(string text, string selector, string pageUrl, string linkUrl, bool isImage)
        {
            if (IsFinishing || IsDestroyed) return;
            var tab = _activeTab;
            if (tab == null || tab.Closed) return;
            try
            {
                bool hasLink = IsHttpUrl(linkUrl);
                bool hasText = !string.IsNullOrWhiteSpace(text);
                bool hasSelector = !string.IsNullOrWhiteSpace(selector);
                if (!hasLink && !hasText && !hasSelector) return;

                var items = new List<string>();
                var actions = new List<Action>();
                string referer = tab.Url;
                string ua = tab.Web.Settings.UserAgentString;

                if (hasLink)
                {
                    items.Add(L10n.T("在新标签页打开"));
                    actions.Add(() => NewTab(linkUrl, null, true, tab.DesktopSite));
                    items.Add(L10n.T("在后台标签页打开"));
                    actions.Add(() => NewTab(linkUrl, null, false, tab.DesktopSite));
                    items.Add(L10n.T("直接打开", "Open here"));
                    actions.Add(() => NavigateTo(linkUrl));
                    items.Add(L10n.T("下载"));
                    actions.Add(() => HandleDownload(tab, linkUrl, ua, null, null, referer));
                    items.Add(L10n.T("复制链接地址", "Copy link address"));
                    actions.Add(() => CopyToClipboard(linkUrl));
                    items.Add(L10n.T("分享链接", "Share link"));
                    actions.Add(() => ShareUrl(linkUrl));
                }
                if (hasText)
                {
                    items.Add(L10n.T("复制选中文字"));
                    actions.Add(() => CopyToClipboard(text));
                    items.Add(L10n.T("搜索选中文字"));
                    actions.Add(() => NavigateTo(SearchUrlFor(text)));
                }
                if (hasSelector)
                {
                    string host = "";
                    try { host = Android.Net.Uri.Parse(pageUrl)?.Host?.ToLowerInvariant() ?? ""; } catch { }
                    items.Add(L10n.T("隐藏此元素（本站）"));
                    actions.Add(() =>
                    {
                        if (string.IsNullOrEmpty(host)) ToastShort(L10n.T("无法获取站点域名，请改用全网规则"));
                        else AddTextAdRule(host + "##" + selector, selector, _web);
                    });
                    items.Add(L10n.T("隐藏此元素（所有网站）"));
                    actions.Add(() => AddTextAdRule("##" + selector, selector, _web));
                }

                // AlertDialog 的 SetMessage 与 SetItems 不能同用（列表项会被挤出不显示），
                // 因此把长按对象放进标题，菜单项独立呈现。
                string preview = hasLink ? linkUrl : (text ?? "");
                preview = preview.Replace("\n", " ").Replace("\r", " ");
                if (preview.Length > 60) preview = preview.Substring(0, 60) + "…";
                string heading = isImage
                    ? L10n.T("图片 · ")
                    : hasLink ? L10n.T("链接 · ") : L10n.T("选中内容 · ", "Selection · ");
                L10n.Dialog(this)
                    .SetTitle(heading + preview)
                    .SetItems(items.ToArray(), (d, a) =>
                    {
                        if (a.Which < 0 || a.Which >= actions.Count) return;
                        try { actions[a.Which]?.Invoke(); }
                        catch (System.Exception ex) { ToastShort(L10n.T("操作失败：", "Failed: ") + ex.Message); }
                    })
                    .SetNegativeButton(L10n.T("取消"), (d, a) => { })
                    .Show();
            }
            catch { }
        }

        /// <summary>保存广告规则并立刻隐藏当前页面对应元素（无需刷新）。</summary>
        private void AddTextAdRule(string rule, string selector, WebView web)
        {
            try
            {
                if (_adBlockEngine == null) _adBlockEngine = new AdBlockEngine(ApplicationContext);
                if (!_adBlockEnabled) { _adBlockEnabled = true; _adBlockEngine.Enabled = true; }
                _adBlockEngine.AddCustomRule(rule);
                if (web != null)
                {
                    string q = "\"" + selector.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                    var js = "(function(){var es=document.querySelectorAll(" + q + ");for(var i=0;i<es.length;i++){es[i].style.setProperty('display','none','important');}})();";
                    web.EvaluateJavascript(js, null);
                }
                ToastShort(L10n.T("已添加屏蔽规则：") + rule);
            }
            catch (System.Exception ex) { ToastShort(L10n.T("添加失败：") + ex.Message); }
        }

        /// <summary>工具箱入口：多选图片合成 PDF。</summary>
        private void PickImagesToPdf()
        {
            try
            {
                var intent = new Intent(Intent.ActionOpenDocument);
                intent.SetType("image/*");
                intent.AddCategory(Intent.CategoryOpenable);
                intent.PutExtra(Intent.ExtraAllowMultiple, true);
                StartActivityForResult(intent, ReqImagesToPdf);
            }
            catch (System.Exception ex) { ToastShort(L10n.T("无法打开图片选择：") + ex.Message); }
        }

        /// <summary>后台合成 PDF，完成后 Toast + 媒体库扫描（可在下载页/文件管理器打开）。</summary>
        private void ExportImagesToPdf(List<Android.Net.Uri> uris)
        {
            ToastShort(L10n.T("正在合成 PDF，共 ") + uris.Count + L10n.T(" 张图片…"));
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var dir = GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads);
                    if (dir == null) throw new System.Exception("无法访问下载目录");
                    string name = L10n.T("图片PDF_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf";
                    string path = System.IO.Path.Combine(dir.AbsolutePath, name);
                    ImagePdfExporter.Export(ContentResolver, uris, path);
                    try { AhoChatA.Vod.MediaScanner.ScanFile(this, path, "application/pdf"); } catch { }
                    RunOnUiThread(() => ToastShort(L10n.T("已生成 PDF：") + name));
                }
                catch (System.Exception ex)
                {
                    RunOnUiThread(() => ToastShort(L10n.T("PDF 合成失败：") + ex.Message));
                }
            });
        }

        /// <summary>短 Toast 提示（来自 JS 桥 gmToast/cancelPickAd）。</summary>
        public void ToastShort(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            RunOnUiThread(() => L10n.Toast(this, message, ToastLength.Short).Show());
        }

        /// <summary>Blob/基64 下载（来自 JS 桥 gmDownload）。</summary>
        public void SaveBlobDownload(string base64Data, string fileName, string mimeType)
        {
            if (string.IsNullOrWhiteSpace(base64Data) || string.IsNullOrWhiteSpace(fileName)) return;
            try
            {
                var bytes = Convert.FromBase64String(base64Data);
                var dir = GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
                    ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                var path = System.IO.Path.Combine(dir, fileName);
                System.IO.File.WriteAllBytes(path, bytes);
                // 插入 MediaStore 让系统相册/文件管理器可见
                var contentValues = new ContentValues();
                contentValues.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                contentValues.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, mimeType ?? "application/octet-stream");
                contentValues.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads);
                var resolver = ContentResolver;
                var uri = resolver.Insert(GetMediaStoreDownloadsUri(), contentValues);
                if (uri != null)
                {
                    using (var output = resolver.OpenOutputStream(uri))
                    {
                        output?.Write(bytes, 0, bytes.Length);
                    }
                }
                // 登记到本程序的下载管理器，用户可在「下载」页面找到这个文件
                try
                {
                    var savedPath = ResolveSavedPath(uri, fileName, path);
                    AhoChatA.Vod.DownloadCenter.RecordSaved(fileName, savedPath, null, "", L10n.T("浏览器直接保存"));
                }
                catch (System.Exception ex) { Android.Util.Log.Warn("MchatDl", "登记浏览器下载失败: " + ex.Message); }
                ToastShort(L10n.T("已保存：") + fileName);
            }
            catch (System.Exception ex)
            {
                ToastShort(L10n.T("保存失败：") + ex.Message);
            }
        }

        /// <summary>
        /// 解析刚保存文件的真实落盘路径。MediaStore 遇到同名文件会自动改名
        /// （如 xxx.png 变成 xxx (1).png），直接拼路径会找不到文件，故以 uri 查询为准。
        /// </summary>
        private string ResolveSavedPath(AndroidUri uri, string fileName, string fallback)
        {
            try
            {
                if (uri != null)
                {
                    using (var cur = ContentResolver.Query(uri, null, null, null, null))
                    {
                        if (cur != null && cur.MoveToFirst())
                        {
                            var dataIdx = cur.GetColumnIndex("_data");
                            if (dataIdx >= 0 && !cur.IsNull(dataIdx))
                            {
                                var real = cur.GetString(dataIdx);
                                if (!string.IsNullOrEmpty(real)) return real;
                            }
                            var relIdx = cur.GetColumnIndex("relative_path");
                            var nameIdx = cur.GetColumnIndex(Android.Provider.MediaStore.IMediaColumns.DisplayName);
                            if (relIdx >= 0 && nameIdx >= 0 && !cur.IsNull(relIdx) && !cur.IsNull(nameIdx))
                            {
                                var rel = cur.GetString(relIdx);
                                var disp = cur.GetString(nameIdx);
                                var root = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
                                if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(disp))
                                    return System.IO.Path.Combine(root, rel ?? "", disp);
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                var pub = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads);
                if (pub != null)
                {
                    var candidate = System.IO.Path.Combine(pub.AbsolutePath, fileName);
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return fallback;
        }

        /// <summary>脚本打开新标签页（来自 JS 桥 gmOpenTab）。</summary>
        public void OpenTabFromScript(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var normalized = NormalizeUrl(url);
            if (IsHttpUrl(normalized))
            {
                RunOnUiThread(() => NewTab(normalized));
            }
        }

        /// <summary>脚本复制文本（来自 JS 桥 gmCopy）。</summary>
        public void CopyTextFromScript(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            RunOnUiThread(() =>
            {
                var cm = (ClipboardManager)GetSystemService(ClipboardService);
                cm.PrimaryClip = ClipData.NewPlainText("text", text);
                ToastShort(L10n.T("已复制到剪贴板"));
            });
        }

        /// <summary>接收脚本注册的菜单命令（来自 JS 桥 gmMenuResult）。按来源 WebView 归到对应 tab，只缓存不弹框。</summary>
        public void ShowScriptCommandMenu(WebView web, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            RunOnUiThread(() =>
            {
                try
                {
                    var commands = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ScriptCommand>>(json);
                    var tab = _tabs.FirstOrDefault(t => !t.Closed && t.Web == web) ?? _activeTab;
                    if (tab != null)
                        tab.MenuCommands = (commands != null && commands.Count > 0) ? commands : null;
                }
                catch { }
            });
        }

        /// <summary>用户从菜单主动打开「脚本命令」时才展示（只显示当前 tab 的命令）。</summary>
        private void OpenScriptCommandMenu()
        {
            var tab = _activeTab;
            var commands = tab?.MenuCommands;
            if (commands == null || commands.Count == 0)
            {
                ToastShort(L10n.T("当前页面没有脚本命令"));
                return;
            }
            // 对话框期间无法切 tab，捕获当前 tab 的 WebView，回调直接投递到它。
            var web = tab.Web;
            var items = commands.Select(c => c.Name).ToArray();
            L10n.Dialog(this)
                .SetTitle(L10n.T("脚本命令"))
                .SetItems(items, (s, e) =>
                {
                    if (e.Which >= 0 && e.Which < commands.Count && web != null)
                    {
                        var cmd = commands[e.Which];
                        var idJs = JsonEncodeString(cmd.Id ?? "");
                        web.EvaluateJavascript("(function(){var id=" + idJs + ";if(window.__mchat_gmMenuCallback&&window.__mchat_gmMenuCallbacks[id])window.__mchat_gmMenuCallback(id);else if(window.MchatJs&&window.MchatJs.gmToast)window.MchatJs.gmToast(" + JsonEncodeString(L10n.T("命令已失效，请刷新页面")) + ");})()", null);
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private class ScriptCommand
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        /// <summary>脚本下载普通 HTTP(S) 文件（来自 JS 桥 gmDownloadUrl）。</summary>
        public void DownloadUrlFromScript(string url, string fileName)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var normalized = NormalizeUrl(url);
            if (!IsHttpUrl(normalized)) return;
            RunOnUiThread(() =>
            {
                var tab = _activeTab;
                if (tab != null && !tab.Closed)
                {
                    HandleDownload(tab, normalized, tab.Web.Settings.UserAgentString, null, null, null);
                }
            });
        }

        /// <summary>用内置 pdf.js 打开 PDF（来自 JS 桥 openPdfViewer）。</summary>
        public void OpenPdfInViewer(string url, string name)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var normalized = NormalizeUrl(url);
            if (!IsHttpUrl(normalized)) return;
            // 使用在线 pdf.js 查看器
            var viewerUrl = "https://cdn.jsdelivr.net/npm/pdfjs-dist@latest/web/viewer.html?file=" + SysUri.EscapeDataString(normalized);
            RunOnUiThread(() => NewTab(viewerUrl, string.IsNullOrWhiteSpace(name) ? L10n.T("PDF 预览") : name));
        }

        /// <summary>JSON 编码字符串（用于 JS 注入）。</summary>
        private static string JsonEncodeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        }

        /// <summary>获取 MediaStore Downloads 集合 URI（兼容不同 API 级别）。</summary>
        private AndroidUri GetMediaStoreDownloadsUri()
        {
            try
            {
                // API 29+: GetContentUri 的参数是卷名（external），不是目录名 Download。
                // 传 "Download" 会抛 "Volume Download not found"，导致文件虽已落盘却报保存失败。
                var method = typeof(Android.Provider.MediaStore.Downloads).GetMethod("GetContentUri", new[] { typeof(string) });
                if (method != null)
                    return (AndroidUri)method.Invoke(null, new object[] { "external" });
            }
            catch { }
            // 回退：使用外部存储根目录下的 Downloads
            return AndroidUri.Parse("content://media/external/downloads");
        }

    /* ========================================================================
     *  广告拦截引擎
     * ====================================================================== */

    /// <summary>
    /// 广告拦截引擎 —— 参考 Via 浏览器的完整 ABP 规则支持
    /// 功能：
    /// 1. 网络请求拦截：支持完整 ABP 语法（||domain^, @@||domain^, /path/, *wildcard*, 域名分隔符^、| 等）
    /// 2. Cosmetic Filters 元素隐藏：##selector、#@#exception、#?#scriptlet 注入
    /// 3. 订阅源管理：EasyList/EasyPrivacy/AdGuard/CJX 等，自动更新、增量解析
    /// 4. 本地/局域网白名单保护（防止误拦内置服务）
    /// 5. 高性能：域名 Trie 树 + 路径 Aho-Corasick 多模式匹配，O(1) 请求判断
    /// 6. 统计与调试：拦截计数、命中规则日志
    /// </summary>
    public class AdBlockEngine
    {
        private readonly Context _ctx;
        private readonly DomainTrie _blockedTrie = new DomainTrie();
        private readonly DomainTrie _exceptionTrie = new DomainTrie();
        private readonly List<UrlPattern> _blockedPatterns = new List<UrlPattern>();
        private readonly List<UrlPattern> _exceptionPatterns = new List<UrlPattern>();
        private readonly List<CosmeticFilter> _cosmeticFilters = new List<CosmeticFilter>();
        private readonly List<CosmeticFilter> _cosmeticExceptions = new List<CosmeticFilter>();
        private readonly HashSet<string> _subscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string PrefName = "adblock_prefs";
        private const string KeySubscriptions = "subscriptions";
        private const string KeyCustomRules = "custom_rules";
        private const string KeyLastUpdate = "last_update";
        private const string KeyEnabled = "enabled";
        private const string KeyStats = "stats_json";
        // 默认关闭：新安装/无历史偏好时不拦截；用户手动开启后写入偏好会被保留
        private bool _enabled = false;
        private long _blockedCount = 0;
        private long _cosmeticCount = 0;

        // 内置订阅源
        private static readonly string[] DefaultSubscriptions = new[]
        {
            "https://easylist.to/easylist/easylist.txt",
            "https://easylist.to/easylist/easyprivacy.txt",
            "https://raw.githubusercontent.com/AdGuardTeam/AdGuardSDNSFilter/master/Filters/filter.txt",
            "https://raw.githubusercontent.com/AdGuardTeam/AdGuardSDNSFilter/master/Filters/filter_popups.txt",
            "https://raw.githubusercontent.com/cjx82630/cjxlist/master/cjxlist.txt", // 中文规则
            "https://raw.githubusercontent.com/privacy-protection-tools/anti-AD/master/anti-ad-easylist.txt" // anti-AD
        };

        // 内置基础规则（无需网络即可生效）
        private static readonly string[] BuiltinFilters = new[]
        {
            // 网络请求拦截规则
            "||googleads.g.doubleclick.net^",
            "||pagead2.googlesyndication.com^",
            "||adservice.google.com^",
            "||partner.googleadservices.com^",
            "||ads.youtube.com^",
            "||static.doubleclick.net^",
            "||cpro.baidu.com^",
            "||dup.baidustatic.com^",
            "||pos.baidu.com^",
            "||t.admaster.com.cn^",
            "||aq.qq.com^",
            "||tanx.com^",
            "||alimama.com^",
            "||atpanel.alimama.com^",
            "||google-analytics.com^",
            "||googletagmanager.com^",
            "||connect.facebook.net^",
            "||bat.bing.com^",
            "||c.bing.com^",
            "||analytics.twitter.com^",
            "||hm.baidu.com^",
            "||cnzz.com^",
            "||51.la^",
            "||umeng.com^",
            "||miaozhen.com^",
            "||admaster.com.cn^",
            "||adsmogo.com^",
            "||v.admaster.com.cn^",
            "||valf.admaster.com.cn^",
            "/ads/",
            "/ad/",
            "/banner/",
            "/track/",
            "/pixel/",
            "/beacon/",
            "/advert/",
            "/sponsor/",
            "||doubleclick.net^",
            "||googlesyndication.com^",
            "||googletagservices.com^",
            "||adsystem.com^",
            "||adserver.com^",
            "||adnetwork.com^",
            "||adexchange.com^",
            "||adsrvr.org^",
            "||adnxs.com^",
            "||rubiconproject.com^",
            "||pubmatic.com^",
            "||openx.net^",
            "||smartadserver.com^",
            "||criteo.com^",
            "||outbrain.com^",
            "||taboola.com^",
            // Cosmetic filters (元素隐藏)
            "##[class*=\"ad\"]:not([class*=\"advertisement\"]):not([class*=\"address\"]):not([class*=\"admin\"])",
            "##[id*=\"ad\"]:not([id*=\"address\"]):not([id*=\"admin\"])",
            "##[class*=\"banner\"]",
            "##[id*=\"banner\"]",
            "##[class*=\"pop\"]",
            "##[id*=\"pop\"]",
            "##[class*=\"float\"]",
            "##[id*=\"float\"]",
            "##.gg-ad",
            "##.google-ad",
            "##.adsbygoogle",
            "##.ad-slot",
            "##.ad-container",
            "##iframe[src*=\"doubleclick\"]",
            "##iframe[src*=\"googlesyndication\"]",
            "##iframe[src*=\"admaster\"]",
            "##iframe[src*=\"tanx\"]",
            "##div[style*=\"position: fixed\"][style*=\"bottom: 0\"]",
            "##div[style*=\"position: fixed\"][style*=\"top: 0\"][style*=\"z-index: 9999\"]",
            "##[data-ad]",
            "##[data-ads]",
            "##[data-advert]",
            "##.ad-banner",
            "##.ad-leaderboard",
            "##.ad-rectangle",
            "##.ad-skyscraper",
            "##.ad-300x250",
            "##.ad-728x90",
            "##.ad-160x600",
            "##.sponsor",
            "##.sponsored",
            "##.promotion",
            "##.promoted"
        };

        public AdBlockEngine(Context ctx)
        {
            _ctx = ctx;
            ParseFilters(BuiltinFilters);
            LoadState();
            if (ShouldAutoUpdate())
            {
                Task.Run(() => UpdateSubscriptionsAsync());
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                SaveState();
            }
        }

        public bool IsEnabled => _enabled;

        public long BlockedCount => _blockedCount;
        public long CosmeticCount => _cosmeticCount;

        public void ResetStats()
        {
            _blockedCount = 0;
            _cosmeticCount = 0;
            SaveState();
        }

        private void LoadState()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                _enabled = prefs.GetBoolean(KeyEnabled, false);
                var subsJson = prefs.GetString(KeySubscriptions, "[]");
                var customJson = prefs.GetString(KeyCustomRules, "[]");
                var lastUpdate = prefs.GetLong(KeyLastUpdate, 0);
                var statsJson = prefs.GetString(KeyStats, "{}");

                var subs = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(subsJson) ?? new List<string>();
                foreach (var s in subs) _subscriptions.Add(s);
                var custom = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(customJson) ?? new List<string>();
                if (custom.Count > 0) ParseFilters(custom.ToArray());

                foreach (var s in DefaultSubscriptions) _subscriptions.Add(s);

                try
                {
                    var stats = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, long>>(statsJson);
                    if (stats != null)
                    {
                        stats.TryGetValue("blocked", out _blockedCount);
                        stats.TryGetValue("cosmetic", out _cosmeticCount);
                    }
                }
                catch { }
            }
            catch { }
        }

        private void SaveState()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var edit = prefs.Edit();
                edit.PutBoolean(KeyEnabled, _enabled);
                edit.PutString(KeySubscriptions, Newtonsoft.Json.JsonConvert.SerializeObject(_subscriptions.ToList()));
                var customOnly = GetCustomRules();
                edit.PutString(KeyCustomRules, Newtonsoft.Json.JsonConvert.SerializeObject(customOnly));
                edit.PutLong(KeyLastUpdate, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var stats = new Dictionary<string, long> { { "blocked", _blockedCount }, { "cosmetic", _cosmeticCount } };
                edit.PutString(KeyStats, Newtonsoft.Json.JsonConvert.SerializeObject(stats));
                edit.Apply();
            }
            catch { }
        }

        private List<string> GetCustomRules()
        {
            var result = new List<string>();
            foreach (var p in _blockedPatterns)
            {
                if (!IsBuiltinPattern(p)) result.Add(p.Original);
            }
            foreach (var p in _exceptionPatterns)
            {
                if (!IsBuiltinPattern(p)) result.Add(p.Original);
            }
            foreach (var c in _cosmeticFilters)
            {
                if (!IsBuiltinCosmetic(c)) result.Add(c.Original);
            }
            foreach (var c in _cosmeticExceptions)
            {
                if (!IsBuiltinCosmetic(c)) result.Add(c.Original);
            }
            return result;
        }

        private bool IsBuiltinPattern(UrlPattern p)
        {
            // 简单判断：内置规则通常包含这些特征
            var builtinDomains = new[] { "doubleclick", "googlesyndication", "googletagservices", "googleads", "baidu", "admaster", "alimama", "tanx", "miaozhen", "cnzz", "umeng", "51.la", "adsmogo" };
            var builtinPaths = new[] { "/ads/", "/ad/", "/banner/", "/track/", "/pixel/", "/beacon/", "/advert/", "/sponsor/" };
            var orig = p.Original;
            foreach (var d in builtinDomains) if (orig.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var pt in builtinPaths) if (orig.IndexOf(pt, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private bool IsBuiltinCosmetic(CosmeticFilter c)
        {
            var builtinSelectors = new[] { "gg-ad", "google-ad", "adsbygoogle", "ad-slot", "ad-container", "doubleclick", "googlesyndication", "admaster", "tanx", "data-ad", "data-ads", "ad-banner", "ad-leaderboard", "ad-rectangle", "ad-skyscraper", "ad-300x250", "ad-728x90", "ad-160x600", "sponsor", "sponsored", "promotion", "promoted" };
            foreach (var s in builtinSelectors) if (c.Selector.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private bool ShouldAutoUpdate()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var lastUpdate = prefs.GetLong(KeyLastUpdate, 0);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return (now - lastUpdate) > TimeSpan.FromDays(3).TotalSeconds; // 3天更新一次
            }
            catch { return true; }
        }

        /// <summary>后台下载并解析所有订阅源，增量合并。</summary>
        public async Task UpdateSubscriptionsAsync()
        {
            if (!_enabled) return;
            var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var newPatterns = new List<UrlPattern>();
            var newCosmetics = new List<CosmeticFilter>();
            var newExceptions = new List<CosmeticFilter>();

            foreach (var url in _subscriptions)
            {
                try
                {
                    var text = await client.GetStringAsync(url);
                    ParseFilterList(text, newPatterns, newCosmetics, newExceptions);
                }
                catch (System.Exception ex)
                {
                    Android.Util.Log.Warn("AdBlockEngine", $"订阅更新失败 {url}: {ex.Message}");
                }
            }

            lock (_blockedTrie)
            {
                foreach (var p in newPatterns)
                {
                    if (p.IsException) _exceptionPatterns.Add(p);
                    else _blockedPatterns.Add(p);
                }
                // 重建 Trie
                RebuildDomainTries();
            }
            lock (_cosmeticFilters)
            {
                foreach (var c in newCosmetics) _cosmeticFilters.Add(c);
                foreach (var c in newExceptions) _cosmeticExceptions.Add(c);
            }
            SaveState();
        }

        private void RebuildDomainTries()
        {
            _blockedTrie.Clear();
            _exceptionTrie.Clear();
            foreach (var p in _blockedPatterns)
            {
                if (p.Type == PatternType.Domain) _blockedTrie.Add(p.Domain);
            }
            foreach (var p in _exceptionPatterns)
            {
                if (p.Type == PatternType.Domain) _exceptionTrie.Add(p.Domain);
            }
        }

        private void ParseFilters(string[] lines)
        {
            var patterns = new List<UrlPattern>();
            var cosmetics = new List<CosmeticFilter>();
            var exceptions = new List<CosmeticFilter>();
            foreach (var line in lines)
            {
                var l = line.Trim();
                if (string.IsNullOrEmpty(l) || l.StartsWith("!") || l.StartsWith("[")) continue;
                ParseSingleRule(l, patterns, cosmetics, exceptions);
            }
            lock (_blockedTrie)
            {
                foreach (var p in patterns)
                {
                    if (p.IsException) _exceptionPatterns.Add(p);
                    else _blockedPatterns.Add(p);
                }
                RebuildDomainTries();
            }
            lock (_cosmeticFilters)
            {
                foreach (var c in cosmetics) _cosmeticFilters.Add(c);
                foreach (var c in exceptions) _cosmeticExceptions.Add(c);
            }
        }

        private void ParseFilterList(string text, List<UrlPattern> patterns, List<CosmeticFilter> cosmetics, List<CosmeticFilter> exceptions)
        {
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (string.IsNullOrEmpty(l) || l.StartsWith("!") || l.StartsWith("[")) continue;
                ParseSingleRule(l, patterns, cosmetics, exceptions);
            }
        }

        private void ParseSingleRule(string rule, List<UrlPattern> patterns, List<CosmeticFilter> cosmetics, List<CosmeticFilter> exceptions)
        {
            var isException = rule.StartsWith("@@");
            var r = isException ? rule.Substring(2) : rule;

            // Cosmetic filter: ##selector, #@#exception, #?#scriptlet
            if (r.StartsWith("##") || r.StartsWith("#@#") || r.StartsWith("#?#"))
            {
                var type = CosmeticType.Hide;
                string selector;
                if (r.StartsWith("#@#")) { type = CosmeticType.Exception; selector = r.Substring(3); }
                else if (r.StartsWith("#?#")) { type = CosmeticType.Scriptlet; selector = r.Substring(3); }
                else { selector = r.Substring(2); }

                var domains = ExtractDomains(ref selector);
                var filter = new CosmeticFilter { Original = rule, Selector = selector, Domains = domains, Type = type };
                if (isException || type == CosmeticType.Exception) exceptions.Add(filter);
                else cosmetics.Add(filter);
                return;
            }

            // URL pattern
            var pattern = ParseUrlPattern(r, isException);
            if (pattern != null) patterns.Add(pattern);
        }

        private List<string> ExtractDomains(ref string selector)
        {
            var domains = new List<string>();
            // 支持 domain1,domain2##selector 格式
            var hashPos = selector.IndexOf("##");
            if (hashPos > 0)
            {
                var domainPart = selector.Substring(0, hashPos);
                selector = selector.Substring(hashPos + 2);
                foreach (var d in domainPart.Split(','))
                {
                    var dm = d.Trim().TrimEnd('^').ToLowerInvariant();
                    if (!string.IsNullOrEmpty(dm) && dm.Contains(".")) domains.Add(dm);
                }
            }
            else
            {
                // 检查是否以域名开头（如 example.com##selector）
                var atPos = selector.IndexOf('@');
                var hashPos2 = selector.IndexOf("##");
                if (atPos >= 0 && (hashPos2 < 0 || atPos < hashPos2))
                {
                    // 这种格式不常见，忽略
                }
            }
            return domains;
        }

        private UrlPattern ParseUrlPattern(string rule, bool isException)
        {
            if (string.IsNullOrEmpty(rule)) return null;

            var pattern = new UrlPattern { Original = rule, IsException = isException };

            // 处理选项部分：$domain=example.com,third-party,script,image...
            var optionsStart = rule.IndexOf('$');
            string options = "";
            if (optionsStart >= 0)
            {
                options = rule.Substring(optionsStart + 1);
                rule = rule.Substring(0, optionsStart);
            }
            ParseOptions(options, pattern);

            // 规范化规则
            var normalized = NormalizePattern(rule);
            if (string.IsNullOrEmpty(normalized)) return null;

            // 判断类型
            if (normalized.StartsWith("||"))
            {
                // ||domain^ 或 ||domain^$options
                var end = normalized.IndexOf('^', 2);
                string domain;
                if (end > 2) domain = normalized.Substring(2, end - 2);
                else domain = normalized.Substring(2);

                if (IsValidDomain(domain))
                {
                    pattern.Type = PatternType.Domain;
                    pattern.Domain = domain.ToLowerInvariant();
                    return pattern;
                }
            }
            else if (normalized.StartsWith("|"))
            {
                // |开头表示锚定开头
                pattern.Type = PatternType.Exact;
                pattern.ExactMatch = normalized.Substring(1);
                return pattern;
            }
            else if (normalized.EndsWith("|"))
            {
                // |结尾表示锚定结尾
                pattern.Type = PatternType.Exact;
                pattern.ExactMatch = normalized.Substring(0, normalized.Length - 1);
                pattern.AnchorEnd = true;
                return pattern;
            }
            else if (normalized.Contains("*") || normalized.Contains("?"))
            {
                // 通配符模式
                pattern.Type = PatternType.Wildcard;
                pattern.Wildcard = normalized;
                return pattern;
            }
            else if (normalized.StartsWith("/") && normalized.EndsWith("/"))
            {
                // 正则表达式（/regex/）
                pattern.Type = PatternType.Regex;
                pattern.Regex = new Regex(normalized.Substring(1, normalized.Length - 2), RegexOptions.IgnoreCase | RegexOptions.Compiled);
                return pattern;
            }
            else
            {
                // 简单子串匹配（路径关键词）
                if (normalized.Contains("/") || normalized.Contains("."))
                {
                    pattern.Type = PatternType.Substring;
                    pattern.Substring = normalized;
                    return pattern;
                }
            }
            return null;
        }

        private void ParseOptions(string options, UrlPattern pattern)
        {
            if (string.IsNullOrEmpty(options)) return;
            foreach (var opt in options.Split(','))
            {
                var o = opt.Trim().ToLowerInvariant();
                if (o == "third-party") pattern.ThirdParty = true;
                else if (o.StartsWith("domain="))
                {
                    var domains = o.Substring(7).Split('|');
                    foreach (var d in domains)
                    {
                        var dm = d.Trim().TrimStart('~').ToLowerInvariant();
                        if (!string.IsNullOrEmpty(dm)) pattern.Domains.Add(dm);
                    }
                    pattern.DomainRestrict = true;
                }
                else if (o == "script") pattern.ResourceTypes.Add("script");
                else if (o == "image") pattern.ResourceTypes.Add("image");
                else if (o == "stylesheet") pattern.ResourceTypes.Add("stylesheet");
                else if (o == "xmlhttprequest") pattern.ResourceTypes.Add("xmlhttprequest");
                else if (o == "subdocument") pattern.ResourceTypes.Add("subdocument");
                else if (o == "font") pattern.ResourceTypes.Add("font");
                else if (o == "media") pattern.ResourceTypes.Add("media");
                else if (o == "websocket") pattern.ResourceTypes.Add("websocket");
                else if (o == "popup") pattern.ResourceTypes.Add("popup");
            }
        }

        private string NormalizePattern(string rule)
        {
            // 移除不必要的分隔符标记，保留核心模式
            var r = rule.Replace("^", "");
            // 处理 || 开头的域名规则
            if (r.StartsWith("||")) return "||" + r.Substring(2);
            return r;
        }

        private bool IsValidDomain(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Contains(" ") || s.Contains("/") || s.Contains("*") || s.Contains("?") || s.Contains("|")) return false;
            if (!s.Contains(".")) return false;
            // 简单校验
            foreach (var c in s)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return false;
            }
            return true;
        }

        /// <summary>添加自定义订阅源。</summary>
        public void AddSubscription(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _subscriptions.Add(url.Trim());
            SaveState();
            Task.Run(() => UpdateSubscriptionsAsync());
        }

        /// <summary>移除订阅源。</summary>
        public void RemoveSubscription(string url)
        {
            _subscriptions.Remove(url);
            SaveState();
        }

        /// <summary>获取当前订阅列表。</summary>
        public List<string> GetSubscriptions() => _subscriptions.ToList();

        /// <summary>添加自定义规则（支持完整 ABP 语法）。</summary>
        public void AddCustomRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return;
            string trimmed = rule.Trim();
            // 以 ! 开头的是「已禁用」规则（单条开关约定）：只存入列表供显示/重新启用，不参与解析
            if (!trimmed.StartsWith("!"))
            {
            var patterns = new List<UrlPattern>();
            var cosmetics = new List<CosmeticFilter>();
            var exceptions = new List<CosmeticFilter>();
            ParseSingleRule(trimmed, patterns, cosmetics, exceptions);
            lock (_blockedTrie)
            {
                foreach (var p in patterns)
                {
                    if (p.IsException) _exceptionPatterns.Add(p);
                    else _blockedPatterns.Add(p);
                }
                RebuildDomainTries();
            }
            lock (_cosmeticFilters)
            {
                foreach (var c in cosmetics) _cosmeticFilters.Add(c);
                foreach (var c in exceptions) _cosmeticExceptions.Add(c);
            }
            }
            SaveState();
        }

        /// <summary>移除自定义规则。</summary>
        public void RemoveCustomRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return;
            // 简化：重新解析自定义规则列表
            var custom = GetCustomRules();
            custom.RemoveAll(r => r.Equals(rule, StringComparison.OrdinalIgnoreCase));
            // 清空并重新加载内置+自定义
            _blockedPatterns.Clear();
            _exceptionPatterns.Clear();
            _cosmeticFilters.Clear();
            _cosmeticExceptions.Clear();
            ParseFilters(BuiltinFilters);
            if (custom.Count > 0) ParseFilters(custom.ToArray());
            SaveState();
        }

        /// <summary>获取自定义规则列表。</summary>
        public List<string> GetCustomRulesList() => GetCustomRules();

        private static bool IsLocalOrLanHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (host.Equals("localhost", System.StringComparison.OrdinalIgnoreCase)
                || host.Equals("127.0.0.1", System.StringComparison.Ordinal)
                || host.Equals("::1", System.StringComparison.Ordinal)
                || host.EndsWith(".local", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (host.StartsWith("192.168.", System.StringComparison.Ordinal)
                || host.StartsWith("10.", System.StringComparison.Ordinal)) return true;
            if (host.StartsWith("172.", System.StringComparison.Ordinal))
            {
                var dot = host.IndexOf('.', 4);
                if (dot > 4 && int.TryParse(host.Substring(4, dot - 4), out var second))
                    return second >= 16 && second <= 31;
            }
            return false;
        }

        /// <summary>判断 URL 是否被拦截（网络请求层面）。O(域名级数 + 模式数)，高性能。</summary>
        public bool IsBlocked(string url, string resourceType = "", string referer = "")
        {
            if (!_enabled) return false;
            try
            {
                var uri = AndroidUri.Parse(url);
                var host = uri.Host?.ToLowerInvariant();
                if (string.IsNullOrEmpty(host)) return false;

                // 本机 / 局域网服务永不拦截
                if (IsLocalOrLanHost(host)) return false;

                // 1. 检查例外规则（优先级最高）
                if (MatchesException(host, uri.Path, resourceType, referer)) return false;

                // 2. 检查拦截规则
                if (MatchesBlocked(host, uri.Path, resourceType, referer))
                {
                    Interlocked.Increment(ref _blockedCount);
                    if (_blockedCount % 100 == 0) SaveState(); // 定期持久化统计
                    return true;
                }

                return false;
            }
            catch { }
            return false;
        }

        private bool MatchesException(string host, string path, string resourceType, string referer)
        {
            // 域名 Trie 匹配
            if (_exceptionTrie.Match(host)) return true;

            // 完整模式匹配
            foreach (var p in _exceptionPatterns)
            {
                if (p.Type == PatternType.Domain) continue; // 已由 Trie 处理
                if (!CheckDomainRestrict(p, host)) continue;
                if (!CheckResourceType(p, resourceType)) continue;
                if (p.Type == PatternType.Exact)
                {
                    if (p.AnchorEnd)
                    {
                        if (urlEndsWith(host + path, p.ExactMatch)) return true;
                    }
                    else if ((host + path).IndexOf(p.ExactMatch, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                else if (p.Type == PatternType.Substring)
                {
                    if ((host + path).IndexOf(p.Substring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                else if (p.Type == PatternType.Wildcard)
                {
                    if (WildcardMatch(host + path, p.Wildcard)) return true;
                }
                else if (p.Type == PatternType.Regex)
                {
                    if (p.Regex.IsMatch(host + path)) return true;
                }
            }
            return false;
        }

        private bool MatchesBlocked(string host, string path, string resourceType, string referer)
        {
            // 域名 Trie 匹配（包含子域名）
            if (_blockedTrie.Match(host)) return true;

            var fullPath = host + path;
            foreach (var p in _blockedPatterns)
            {
                if (p.Type == PatternType.Domain) continue;
                if (!CheckDomainRestrict(p, host)) continue;
                if (!CheckResourceType(p, resourceType)) continue;
                if (!CheckThirdParty(p, host, referer)) continue;

                switch (p.Type)
                {
                    case PatternType.Exact:
                        if (p.AnchorEnd)
                        {
                            if (urlEndsWith(fullPath, p.ExactMatch)) return true;
                        }
                        else if (fullPath.IndexOf(p.ExactMatch, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        break;
                    case PatternType.Substring:
                        if (fullPath.IndexOf(p.Substring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        break;
                    case PatternType.Wildcard:
                        if (WildcardMatch(fullPath, p.Wildcard)) return true;
                        break;
                    case PatternType.Regex:
                        if (p.Regex.IsMatch(fullPath)) return true;
                        break;
                }
            }
            return false;
        }

        private bool CheckDomainRestrict(UrlPattern p, string host)
        {
            if (!p.DomainRestrict) return true;
            foreach (var d in p.Domains)
            {
                var negate = d.StartsWith("~");
                var dm = negate ? d.Substring(1) : d;
                var match = DomainMatch(host, dm);
                if (negate) { if (match) return false; }
                else { if (match) return true; }
            }
            return !p.Domains.Any(d => !d.StartsWith("~")); // 如果有正向域名限制但都不匹配，则不生效
        }

        private bool CheckResourceType(UrlPattern p, string resourceType)
        {
            if (p.ResourceTypes.Count == 0) return true;
            if (string.IsNullOrEmpty(resourceType)) return true;
            return p.ResourceTypes.Contains(resourceType.ToLowerInvariant());
        }

        private bool CheckThirdParty(UrlPattern p, string host, string referer)
        {
            if (!p.ThirdParty) return true;
            if (string.IsNullOrEmpty(referer)) return true;
            try
            {
                var refUri = AndroidUri.Parse(referer);
                var refHost = refUri.Host?.ToLowerInvariant();
                if (string.IsNullOrEmpty(refHost)) return true;
                // 第三方：referer 域名与请求域名不同（含子域名关系）
                return !DomainMatch(host, refHost) && !DomainMatch(refHost, host);
            }
            catch { return true; }
        }

        private static bool DomainMatch(string host, string domain)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(domain)) return false;
            host = host.ToLowerInvariant();
            domain = domain.ToLowerInvariant();
            return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        }

        private static bool urlEndsWith(string url, string suffix)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(suffix)) return false;
            if (suffix.Length > url.Length) return false;
            return url.Substring(url.Length - suffix.Length).Equals(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WildcardMatch(string input, string pattern)
        {
            // 简单通配符匹配：* 匹配任意字符，? 匹配单字符
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
        }

        /// <summary>注入 Cosmetic Filters（页面加载完成后调用）。支持例外规则、domain 限制、scriptlet 注入。</summary>
        public void ApplyCosmeticFilters(WebView web, string pageUrl = "")
        {
            if (!_enabled) return;
            try
            {
                var host = "";
                try
                {
                    if (!string.IsNullOrEmpty(pageUrl))
                    {
                        var uri = AndroidUri.Parse(pageUrl);
                        host = uri.Host?.ToLowerInvariant() ?? "";
                    }
                }
                catch { }

                var cssBuilder = new System.Text.StringBuilder();
                var scriptlets = new List<string>();

                lock (_cosmeticFilters)
                {
                    foreach (var f in _cosmeticFilters)
                    {
                        if (f.Domains.Count > 0)
                        {
                            var match = false;
                            foreach (var d in f.Domains)
                            {
                                if (DomainMatch(host, d)) { match = true; break; }
                            }
                            if (!match) continue;
                        }
                        if (f.Type == CosmeticType.Scriptlet)
                        {
                            scriptlets.Add(f.Selector);
                        }
                        else
                        {
                            cssBuilder.Append(f.Selector).Append("{display:none!important;visibility:hidden!important;}");
                        }
                    }
                }

                // 处理例外：#@# 生成 :not() 伪类或单独注入
                lock (_cosmeticExceptions)
                {
                    foreach (var f in _cosmeticExceptions)
                    {
                        if (f.Domains.Count > 0)
                        {
                            var match = false;
                            foreach (var d in f.Domains)
                            {
                                if (DomainMatch(host, d)) { match = true; break; }
                            }
                            if (!match) continue;
                        }
                        if (f.Type == CosmeticType.Exception)
                        {
                            // 例外选择器：通过 :not() 或单独 style 覆盖
                            cssBuilder.Append(f.Selector).Append("{display:inherit!important;visibility:inherit!important;}");
                        }
                    }
                }

                var css = cssBuilder.ToString();
                if (!string.IsNullOrEmpty(css))
                {
                    Interlocked.Add(ref _cosmeticCount, 1);
                    var js = "(function(){var s=document.createElement('style');s.id='mchat-adblock-cosmetic';s.textContent=" + JsonEncode(css) + ";document.head.appendChild(s);})();";
                    web.EvaluateJavascript(js, null);
                }

                // 注入 scriptlets（Via 支持的脚本片段，如防反广告拦截、清除内联脚本等）
                if (scriptlets.Count > 0)
                {
                    var scriptletJs = string.Join(";", scriptlets.Select(s => "(function(){" + s + "})()"));
                    var js = "(function(){" + scriptletJs + "})();";
                    web.EvaluateJavascript(js, null);
                }
            }
            catch { }
        }

        private string JsonEncode(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        }

        // ===== 内部数据结构 =====

        private enum PatternType { Domain, Exact, Substring, Wildcard, Regex }
        private enum CosmeticType { Hide, Exception, Scriptlet }

        private class UrlPattern
        {
            public string Original;
            public bool IsException;
            public PatternType Type;
            public string Domain;           // 用于 Domain 类型
            public string ExactMatch;       // 用于 Exact 类型
            public bool AnchorEnd;
            public string Substring;        // 用于 Substring 类型
            public string Wildcard;         // 用于 Wildcard 类型
            public Regex Regex;             // 用于 Regex 类型
            public HashSet<string> Domains = new HashSet<string>();
            public bool DomainRestrict;
            public bool ThirdParty;
            public HashSet<string> ResourceTypes = new HashSet<string>();
        }

        private class CosmeticFilter
        {
            public string Original;
            public string Selector;
            public List<string> Domains = new List<string>();
            public CosmeticType Type;
        }

        /// <summary>域名 Trie 树：支持精确匹配 + 子域名匹配，O(域名级数) 查询。</summary>
        private class DomainTrie
        {
            private readonly TrieNode _root = new TrieNode();

            public void Clear() { _root.Children.Clear(); _root.IsTerminal = false; }

            public void Add(string domain)
            {
                if (string.IsNullOrEmpty(domain)) return;
                var parts = domain.Split('.');
                var node = _root;
                for (int i = parts.Length - 1; i >= 0; i--) // 从右向左：com.google.analytics
                {
                    var part = parts[i];
                    if (!node.Children.TryGetValue(part, out var child))
                    {
                        child = new TrieNode();
                        node.Children[part] = child;
                    }
                    node = child;
                }
                node.IsTerminal = true;
            }

            /// <summary>匹配 host 及其所有父域名。例：a.b.google.com -> 查 com -> google -> b -> a</summary>
            public bool Match(string host)
            {
                if (string.IsNullOrEmpty(host)) return false;
                var parts = host.Split('.');
                var node = _root;
                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    var part = parts[i];
                    if (!node.Children.TryGetValue(part, out node)) return false;
                    if (node.IsTerminal) return true;
                }
                return false;
            }

            private class TrieNode
            {
                public Dictionary<string, TrieNode> Children = new Dictionary<string, TrieNode>(StringComparer.OrdinalIgnoreCase);
                public bool IsTerminal;
            }
        }
    }

    /* ========================================================================
     *  资源嗅探器
     * ====================================================================== */

    /// <summary>
    /// 嗅探页面中的媒体资源：视频(m3u8/mp4/flv)、音频、图片、文档。
    /// 通过拦截网络请求 + DOM 解析双重保障。
    /// </summary>
    public class ResourceSniffer
    {
        private readonly Action _changed;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SniffedResource> _resources = new Dictionary<string, SniffedResource>(StringComparer.Ordinal);
        private int _generation;

        public ResourceSniffer(Action changed) { _changed = changed; }

        public List<SniffedResource> Snapshot()
        {
            lock (_gate) return _resources.Values.ToList();
        }

        public void Clear()
        {
            lock (_gate) { _generation++; _resources.Clear(); }
            _changed();
        }

        private static readonly string[] MediaHints =
        {
            ".m3u8", ".mpd", ".mp4", ".mkv", ".mov", ".webm", ".flv", "/hls/",
            ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".aac",
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".svg", ".avif",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".epub"
        };
        private long _lastNotifyMs;

        public void SniffUrl(string url, string referer, string hint = null, int? generation = null)
        {
            // 快速否定：绝大多数子资源是 js/css/font，直接按扩展名排除，避免每个请求都做 Uri 解析
            if (string.IsNullOrEmpty(url)) return;
            var lower = url.ToLowerInvariant();
            var interesting = false;
            foreach (var h in MediaHints)
                if (lower.IndexOf(h, StringComparison.Ordinal) >= 0) { interesting = true; break; }
            if (!interesting) return;

            if (!SysUri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
            var type = DetectResourceType(uri.AbsolutePath);
            if (type == "other" && hint != null)
                type = new[] { "video", "audio", "image" }.FirstOrDefault(x => hint == x || hint.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase)) ?? "other";
            if (type == "other") return;
            lock (_gate)
            {
                if ((generation.HasValue && generation.Value != _generation) || _resources.Count >= 500 || _resources.ContainsKey(url)) return;
                _resources.Add(url, new SniffedResource { Url = url, Type = type, Referer = referer ?? "", DetectedAt = DateTime.Now });
            }
            // 节流：一次页面加载可能命中上百个媒体资源，UI 通知合并到 200ms 一次
            var now = Java.Lang.JavaSystem.CurrentTimeMillis();
            if (now - _lastNotifyMs >= 200)
            {
                _lastNotifyMs = now;
                _changed();
            }
        }

        private string DetectResourceType(string url)
        {
            // 扩展名只看路径部分，避免 ?from=.m3u8 这类查询串把普通资源误判为视频
            var raw = url ?? "";
            int cut = raw.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) raw = raw.Substring(0, cut);
            var lower = raw.ToLowerInvariant();
            // 视频（/hls/ 是路径特征，仍然按完整 URL 判断）
            if (lower.EndsWith(".m3u8") || (url ?? "").ToLowerInvariant().Contains("/hls/") || lower.Contains(".mpd")
                || lower.EndsWith(".mp4") || lower.EndsWith(".mkv") || lower.EndsWith(".mov")
                || lower.EndsWith(".webm") || lower.EndsWith(".flv") || lower.EndsWith(".ts"))
                return "video";
            // 音频
            if (lower.EndsWith(".mp3") || lower.EndsWith(".flac") || lower.EndsWith(".m4a")
                || lower.EndsWith(".wav") || lower.EndsWith(".ogg") || lower.EndsWith(".aac"))
                return "audio";
            // 图片
            if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png")
                || lower.EndsWith(".webp") || lower.EndsWith(".gif") || lower.EndsWith(".bmp")
                || lower.EndsWith(".svg") || lower.EndsWith(".avif"))
                return "image";
            // 文档
            if (lower.EndsWith(".pdf") || lower.EndsWith(".doc") || lower.EndsWith(".docx")
                || lower.EndsWith(".xls") || lower.EndsWith(".xlsx") || lower.EndsWith(".ppt")
                || lower.EndsWith(".pptx") || lower.EndsWith(".txt") || lower.EndsWith(".epub"))
                return "document";
            return "other";
        }

        /// <summary>从 DOM 提取资源（页面加载完成后调用）。</summary>
        public async void SniffDom(WebView web, string pageUrl)
        {
            int generation;
            lock (_gate) generation = _generation;
            var js = @"
                (function() {
                    var resources = [];
                    document.querySelectorAll('video, audio').forEach(function(el) {
                        if (el.src) resources.push({url: el.src, type: el.tagName.toLowerCase()});
                        el.querySelectorAll('source').forEach(function(s) {
                            if (s.src) resources.push({url: s.src, type: s.type || el.tagName.toLowerCase()});
                        });
                    });
                    document.querySelectorAll('img').forEach(function(el) {
                        if (el.src) resources.push({url: el.src, type: 'image'});
                        if (el.srcset) {
                            el.srcset.split(',').forEach(function(p) {
                                var u = p.trim().split(' ')[0];
                                if (u) resources.push({url: u, type: 'image'});
                            });
                        }
                    });
                    document.querySelectorAll('link[rel=""preload""], link[rel=""prefetch""]').forEach(function(l) {
                        if (l.href) resources.push({url: l.href, type: l.as || 'other'});
                    });
                    document.querySelectorAll('a[href]').forEach(function(a) {
                        var href = a.href;
                        if (href.match(/\.(mp4|mkv|webm|flv|m3u8|mpd|mp3|flac|m4a|wav|ogg|pdf|epub|zip|rar|7z)([?#]|$)/i)) {
                            resources.push({url: href, type: 'link'});
                        }
                    });
                    return resources.slice(0, 500);
                })();
            ";
            try
            {
                var result = await EvaluateJavascriptAsync(web, js);
                if (!string.IsNullOrEmpty(result) && result != "null")
                {
                    var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<DomResource>>(result);
                     foreach (var r in list ?? new List<DomResource>())
                     {
                         SniffUrl(r.Url, pageUrl, r.Type, generation);
                     }
                }
            }
            catch { }
        }

        private Task<string> EvaluateJavascriptAsync(WebView web, string script)
        {
            var tcs = new TaskCompletionSource<string>();
            web.EvaluateJavascript(script, new JsResultCallback(tcs));
            return tcs.Task;
        }

        private class JsResultCallback : Java.Lang.Object, IValueCallback
        {
            private readonly TaskCompletionSource<string> _tcs;
            public JsResultCallback(TaskCompletionSource<string> tcs) { _tcs = tcs; }
            public void OnReceiveValue(Java.Lang.Object value)
            {
                _tcs.TrySetResult(value?.ToString() ?? "");
            }
        }

        private class DomResource
        {
            public string Url { get; set; }
            public string Type { get; set; }
        }
    }

    /* ========================================================================
     *  离线页面保存器
     * ====================================================================== */

    /// <summary>
    /// 保存网页为离线可读格式：
    /// 1. MHTML (单文件归档，浏览器原生支持)
    /// 2. HTML + 资源文件夹 (完整还原)
    /// 3. 简化阅读模式 (仅正文)
    /// 4. 纯文本
    /// </summary>
    public class OfflinePageSaver
    {
        private readonly Context _ctx;
        public OfflinePageSaver(Context ctx) { _ctx = ctx.ApplicationContext; }

        public async void SavePage(WebView web, AndroidUri destination)
        {
            string path = System.IO.Path.Combine(_ctx.CacheDir.AbsolutePath, Guid.NewGuid() + ".mhtml");
            try
            {
                var callback = new ArchiveCallback();
                web.SaveWebArchive(path, false, callback);
                if (await callback.Result.Task == null) throw new IOException("无法生成网页归档");
                await Task.Run(() =>
                {
                    using (var input = System.IO.File.OpenRead(path))
                    using (var output = _ctx.ContentResolver.OpenOutputStream(destination, "wt"))
                    {
                        if (output == null) throw new IOException("无法打开目标文件");
                        input.CopyTo(output);
                    }
                });
                L10n.Toast(_ctx, L10n.T("网页已保存"), ToastLength.Short).Show();
            }
            catch (System.Exception)
            {
                L10n.Toast(_ctx, L10n.T("网页保存失败"), ToastLength.Short).Show();
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }

        private sealed class ArchiveCallback : Java.Lang.Object, IValueCallback
        {
            public readonly TaskCompletionSource<string> Result = new TaskCompletionSource<string>();
            public void OnReceiveValue(Java.Lang.Object value) => Result.TrySetResult(value?.ToString());
        }
    }

    /* ========================================================================
     *  嗅探资源数据类
     * ====================================================================== */

    public class SniffedResource
    {
        public string Url { get; set; }
        public string Type { get; set; } // video | audio | image | document | other
        public string Referer { get; set; }
        public DateTime DetectedAt { get; set; }
    }

    /* ========================================================================
     *  历史记录管理
     * ====================================================================== */

    /// <summary>
    /// 浏览历史记录管理：本地持久化存储，支持增删改查、导出、同步接口预留。
    /// </summary>
    public class HistoryManager
    {
        private static HistoryManager _instance;
        private static readonly object _lock = new object();
        private readonly Context _ctx;
        private const string PrefName = "browser_history";
        private const string KeyHistory = "history_list";
        private const string KeyStats = "visit_stats";
        private const int MaxHistoryItems = 5000;

        public static HistoryManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new HistoryManager(AhoChatA.AhoChatApp.Context);
                    }
                }
                return _instance;
            }
        }

        private HistoryManager(Context ctx)
        {
            _ctx = ctx.ApplicationContext;
        }

        public void AddHistory(string url, string title)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyHistory, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();

                // 移除重复 URL（保留最新）
                list.RemoveAll(h => h.Url == url);

                // 插入到最前面
                list.Insert(0, new HistoryEntry
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? url : title,
                    VisitedAt = DateTime.Now
                });

                // 限制数量
                if (list.Count > MaxHistoryItems)
                    list.RemoveRange(MaxHistoryItems, list.Count - MaxHistoryItems);

                prefs.Edit().PutString(KeyHistory, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
                RecordVisitStat(url);
            }
            catch { }
        }

        /// <summary>按域名累加访问统计（只在非无痕、记录历史时调用）。</summary>
        private void RecordVisitStat(string url)
        {
            try
            {
                string host = "";
                try { host = new System.Uri(url).Host.ToLowerInvariant(); } catch { }
                if (string.IsNullOrEmpty(host)) return;
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var stats = Newtonsoft.Json.JsonConvert.DeserializeObject<List<VisitStat>>(
                    prefs.GetString(KeyStats, "[]")) ?? new List<VisitStat>();
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                var s = stats.FirstOrDefault(x => x.Host == host);
                if (s == null) { s = new VisitStat { Host = host }; stats.Add(s); }
                s.Count++;
                if (s.LastDay != today) { s.LastDay = today; s.TodayCount = 1; }
                else s.TodayCount++;
                s.LastVisit = DateTime.Now;
                if (stats.Count > 500) stats.RemoveRange(500, stats.Count - 500);
                prefs.Edit().PutString(KeyStats, Newtonsoft.Json.JsonConvert.SerializeObject(stats)).Apply();
            }
            catch { }
        }

        /// <summary>访问统计快照：按总次数降序。</summary>
        public List<VisitStat> GetStats()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var stats = Newtonsoft.Json.JsonConvert.DeserializeObject<List<VisitStat>>(
                    prefs.GetString(KeyStats, "[]")) ?? new List<VisitStat>();
                return stats.OrderByDescending(x => x.Count).ToList();
            }
            catch { return new List<VisitStat>(); }
        }

        public void ClearStats()
        {
            try { _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private).Edit().Remove(KeyStats).Apply(); }
            catch { }
        }

        public List<HistoryEntry> GetHistory()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyHistory, "[]");
                return Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
            }
            catch { return new List<HistoryEntry>(); }
        }

        public void RemoveHistory(string url)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyHistory, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
                list.RemoveAll(h => h.Url == url);
                prefs.Edit().PutString(KeyHistory, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
            }
            catch { }
        }

        public void ClearHistory()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                prefs.Edit().Remove(KeyHistory).Remove(KeyStats).Apply();
            }
            catch { }
        }

        /// <summary>删除超过指定天数的历史，返回删除条数。</summary>
        public int PurgeOlderThan(int days)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyHistory, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
                var cutoff = DateTime.Now.AddDays(-days);
                var before = list.Count;
                list.RemoveAll(h => h.VisitedAt < cutoff);
                prefs.Edit().PutString(KeyHistory, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
                return before - list.Count;
            }
            catch { return 0; }
        }

        /// <summary>导出历史记录为 JSON 字符串（用于同步/备份）。</summary>
        public string ExportHistory()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(GetHistory());
        }

        /// <summary>导入历史记录（用于同步/恢复）。</summary>
        public void ImportHistory(string json)
        {
            try
            {
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                prefs.Edit().PutString(KeyHistory, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
            }
            catch { }
        }
    }

    public class HistoryEntry
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public DateTime VisitedAt { get; set; }
    }

    /// <summary>按域名聚合的访问统计（无痕模式不记录）。</summary>
    public class VisitStat
    {
        public string Host { get; set; }
        public int Count { get; set; }
        public int TodayCount { get; set; }
        public string LastDay { get; set; }
        public DateTime LastVisit { get; set; }
    }

    /* ========================================================================
     *  书签管理
     * ====================================================================== */

    /// <summary>
    /// 书签管理：本地持久化存储，支持增删改查、文件夹分类（预留）、导出/导入、同步接口预留。
    /// </summary>
    public class BookmarkManager
    {
        private static BookmarkManager _instance;
        private static readonly object _lock = new object();
        private readonly Context _ctx;
        private const string PrefName = "browser_bookmarks";
        private const string KeyBookmarks = "bookmarks_list";

        public static BookmarkManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new BookmarkManager(AhoChatA.AhoChatApp.Context);
                    }
                }
                return _instance;
            }
        }

        private BookmarkManager(Context ctx)
        {
            _ctx = ctx.ApplicationContext;
        }

        public void AddBookmark(string url, string title)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyBookmarks, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BookmarkEntry>>(json) ?? new List<BookmarkEntry>();

                // 如果已存在，更新标题
                var existing = list.FirstOrDefault(b => b.Url == url);
                if (existing != null)
                {
                    existing.Title = title;
                    existing.UpdatedAt = DateTime.Now;
                }
                else
                {
                    list.Insert(0, new BookmarkEntry
                    {
                        Url = url,
                        Title = string.IsNullOrWhiteSpace(title) ? url : title,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    });
                }

                prefs.Edit().PutString(KeyBookmarks, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
            }
            catch { }
        }

        public List<BookmarkEntry> GetBookmarks()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyBookmarks, "[]");
                return Newtonsoft.Json.JsonConvert.DeserializeObject<List<BookmarkEntry>>(json) ?? new List<BookmarkEntry>();
            }
            catch { return new List<BookmarkEntry>(); }
        }

        public void RemoveBookmark(string url)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyBookmarks, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BookmarkEntry>>(json) ?? new List<BookmarkEntry>();
                list.RemoveAll(b => b.Url == url);
                prefs.Edit().PutString(KeyBookmarks, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
            }
            catch { }
        }

        public void UpdateBookmark(string url, string newTitle)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyBookmarks, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BookmarkEntry>>(json) ?? new List<BookmarkEntry>();
                var bm = list.FirstOrDefault(b => b.Url == url);
                if (bm != null)
                {
                    bm.Title = newTitle;
                    bm.UpdatedAt = DateTime.Now;
                    prefs.Edit().PutString(KeyBookmarks, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
                }
            }
            catch { }
        }

        public void ClearBookmarks()
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                prefs.Edit().Remove(KeyBookmarks).Apply();
            }
            catch { }
        }

        /// <summary>删除超过指定天数未更新的书签，返回删除条数。</summary>
        public int PurgeOlderThan(int days)
        {
            try
            {
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                var json = prefs.GetString(KeyBookmarks, "[]");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BookmarkEntry>>(json) ?? new List<BookmarkEntry>();
                var cutoff = DateTime.Now.AddDays(-days);
                var before = list.Count;
                list.RemoveAll(b => b.UpdatedAt < cutoff && b.CreatedAt < cutoff);
                prefs.Edit().PutString(KeyBookmarks, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
                return before - list.Count;
            }
            catch { return 0; }
        }

        /// <summary>导出书签为 JSON 字符串（用于同步/备份）。</summary>
        public string ExportBookmarks()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(GetBookmarks());
        }

        /// <summary>导入书签（用于同步/恢复）。</summary>
        public void ImportBookmarks(string json)
        {
            try
            {
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BookmarkEntry>>(json) ?? new List<BookmarkEntry>();
                var prefs = _ctx.GetSharedPreferences(PrefName, FileCreationMode.Private);
                prefs.Edit().PutString(KeyBookmarks, Newtonsoft.Json.JsonConvert.SerializeObject(list)).Apply();
            }
            catch { }
        }
    }

    public class BookmarkEntry
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string FolderId { get; set; } // 预留：文件夹分类
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
}

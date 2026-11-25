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
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.DocumentFile.Provider;
using Java.IO;
using Orientation = Android.Widget.Orientation;
using SysIO = System.IO;
using SysPath = System.IO.Path;
using JavaFile = Java.IO.File;
using AhoChatA.Vod;

namespace AhoChatA
{
    /// <summary>
    /// 原生（App 端）下载管理器：接管「在线音乐 / 在线视频 / 小说 / 直链 / m3u8」的全部下载任务，
    /// 统一管理进度、状态、保存路径与失败重试（后端逻辑在 DownloadCenter / DownloadEngine）。
    /// 入口挂在 ChatActivity「服务」页的「媒体」分组里（阅读 / 影音 下方）。
    /// 功能：分类筛选、批量操作、限速、剪贴板监听、自定义保存目录、滑动删除、历史记录搜索、队列优先级、完成动作、多协议支持
    /// </summary>
    [Activity(Label = "@string/act_download", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class DownloadActivity : BaseActivity
    {
        private static readonly Color BgColor = Color.Rgb(0xF2, 0xF2, 0xF7);
        private static readonly Color CardBg = Color.Rgb(0xFF, 0xFF, 0xFF);
        private static readonly Color TextMain = Color.Rgb(0x1C, 0x1C, 0x1E);
        private static readonly Color TextSub = Color.Rgb(0x8E, 0x8E, 0x93);
        private static readonly Color Accent = Color.Rgb(0x00, 0x7A, 0xFF);
        private static readonly Color Warn = Color.Rgb(0xFF, 0x3B, 0x30);
        private static readonly Color Ok = Color.Rgb(0x34, 0xC7, 0x59);
        private static readonly Color Divider = Color.Rgb(0xE5, 0xE5, 0xEA);

        public const string ExtraNewTask = "download_new_task";
        private const string ExtraUrl = "download_url", ExtraName = "download_name", ExtraCookie = "download_cookie",
            ExtraUserAgent = "download_ua", ExtraReferer = "download_referer", ExtraHls = "download_hls";

        // 分类常量
        private static readonly string[] Categories = { L10n.T("全部"), L10n.T("视频"), L10n.T("音乐"), L10n.T("小说"), L10n.T("文档"), "m3u8", L10n.T("其他") };
        private const string PrefDownloadDir = "download_custom_dir";
        private const string PrefSpeedLimit = "download_speed_limit_kbps";
        private const string PrefClipboardMonitor = "download_clipboard_monitor";
        private const string PrefAutoOpen = "download_auto_open";
        private const string PrefExtractArchive = "download_extract_archive";

        public static void OpenNew(Context ctx, string url = null, string fileName = null, string cookie = null,
            string userAgent = null, string referer = null, bool forceHls = false)
        {
            if (ctx == null) return;
            var intent = new Intent(ctx, typeof(DownloadActivity));
            intent.PutExtra(ExtraNewTask, true);
            intent.PutExtra(ExtraUrl, url ?? "");
            intent.PutExtra(ExtraName, fileName ?? "");
            intent.PutExtra(ExtraCookie, cookie ?? "");
            intent.PutExtra(ExtraUserAgent, userAgent ?? "");
            intent.PutExtra(ExtraReferer, referer ?? "");
            intent.PutExtra(ExtraHls, forceHls);
            intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            if (!(ctx is Activity)) intent.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
        }

        // UI 组件
        private LinearLayout _list;
        private TextView _empty;
        private Timer _timer;
        private AlertDialog _newTaskDialog;
        private CancellationTokenSource _inspectCancellation;
        private volatile bool _destroyed;
        
        // 新增功能组件
        private LinearLayout _categoryBar;
        private int _currentCategory = 0;
        private bool _batchMode = false;
        private LinearLayout _batchBar;
        private TextView _batchCount;
        private HashSet<string> _selectedIds = new HashSet<string>();
        private View _speedLimitRow;
        private SeekBar _speedLimitBar;
        private TextView _speedLimitLabel;
        private CheckBox _clipboardMonitorCb;
        private CheckBox _autoOpenCb;
        private CheckBox _extractArchiveCb;
        private DocumentFile _customDownloadDir;
        private CancellationTokenSource _clipboardCancellation;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            DownloadCenter.AppContext = ApplicationContext;
            DownloadCenter.LoadHistory();
            DownloadEngine.EnsureInitialized(ApplicationContext);
            LoadSettings();
            BuildUi();
            HandleNewTaskIntent(Intent);
            StartClipboardMonitor();
            
            // 定时轮询
            _timer = new Timer(_ =>
            {
                if (_destroyed) return;
                try { RunOnUiThread(() => { if (!_destroyed) Refresh(); }); } catch { }
            }, null, 1000, 1200);
        }

        protected override void OnResume()
        {
            base.OnResume();
            Refresh();
            if (_customDownloadDir != null) EnsureDownloadDir();
        }

        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;
            HandleNewTaskIntent(intent);
        }

        protected override void OnDestroy()
        {
            _destroyed = true;
            StopClipboardMonitor();
            try { _inspectCancellation?.Cancel(); _newTaskDialog?.Dismiss(); } catch { }
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            base.OnDestroy();
        }

        /* ===================== 设置持久化 ===================== */

        private void LoadSettings()
        {
            var prefs = GetSharedPreferences("download_settings", FileCreationMode.Private);
            _currentCategory = prefs.GetInt("category", 0);
            
            // 自定义下载目录
            var dirUriStr = prefs.GetString(PrefDownloadDir, null);
            if (!string.IsNullOrEmpty(dirUriStr))
            {
                try { _customDownloadDir = DocumentFile.FromTreeUri(this, Android.Net.Uri.Parse(dirUriStr)); } catch { }
            }
            
            // 限速
            var limitKbps = prefs.GetInt(PrefSpeedLimit, 0);
            DownloadEngine.GlobalSpeedLimitKbps = limitKbps;
            
            // 剪贴板监听
            var clipboardOn = prefs.GetBoolean(PrefClipboardMonitor, true);
            
            // 完成动作
            DownloadEngine.AutoOpenOnComplete = prefs.GetBoolean(PrefAutoOpen, false);
            DownloadEngine.ExtractArchivesOnComplete = prefs.GetBoolean(PrefExtractArchive, false);
        }

        private void SaveSettings()
        {
            var prefs = GetSharedPreferences("download_settings", FileCreationMode.Private).Edit();
            prefs.PutInt("category", _currentCategory);
            prefs.PutInt(PrefSpeedLimit, DownloadEngine.GlobalSpeedLimitKbps);
            prefs.PutBoolean(PrefClipboardMonitor, _clipboardMonitorCb?.Checked ?? true);
            prefs.PutBoolean(PrefAutoOpen, _autoOpenCb?.Checked ?? false);
            prefs.PutBoolean(PrefExtractArchive, _extractArchiveCb?.Checked ?? false);
            if (_customDownloadDir != null)
                prefs.PutString(PrefDownloadDir, _customDownloadDir.Uri.ToString());
            prefs.Apply();
        }

        /* ===================== 界面构建 ===================== */

        private void BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(BgColor);

            // 顶栏
            var bar = BuildTopBar();
            root.AddView(bar);

            // 分类筛选栏
            _categoryBar = BuildCategoryBar();
            root.AddView(_categoryBar);

            // 批量操作栏（默认隐藏）
            _batchBar = BuildBatchBar();
            _batchBar.Visibility = ViewStates.Gone;
            root.AddView(_batchBar);

            // 限速/设置行（可折叠）
            _speedLimitRow = BuildSpeedLimitRow();
            root.AddView(_speedLimitRow);

            // 说明提示
            var hint = new TextView(this)
            {
                Text = L10n.T("支持：HTTP/HTTPS 直链、m3u8 视频流、在线音乐、在线视频、小说章节。\n") +
                       L10n.T("点「＋ 新建」粘贴链接，支持 AES-128 解密、断点续传、多线程分片、后台下载。\n") +
                       L10n.T("文件默认保存到应用专属 Download/Mchat 目录；可在设置中自定义保存位置。\n") +
                       L10n.T("长按任务卡片进入选择模式，支持批量取消/重试/删除；完成后可自动打开或解压。"),
                TextSize = 12
            };
            hint.SetTextColor(TextSub);
            hint.SetPadding(Dp(16), Dp(12), Dp(16), Dp(8));
            hint.SetLineSpacing(0, 1.3f);
            root.AddView(hint);

            // 列表区
            var scroll = new ScrollView(this);
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.SetPadding(Dp(16), Dp(4), Dp(16), Dp(20));

            _empty = new TextView(this)
            {
                Text = L10n.T("暂无下载任务。点「＋ 新建」粘贴直链或 m3u8，或在浏览器中下载文件。"),
                TextSize = 13
            };
            _empty.SetTextColor(TextSub);
            _empty.SetPadding(Dp(8), Dp(16), Dp(8), Dp(16));
            body.AddView(_empty);

            _list = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _list.SetPadding(0, Dp(4), 0, 0);
            body.AddView(_list);

            scroll.AddView(body);
            root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            
            SetContentView(root);

            // 处理新任务 Intent
            HandleNewTaskIntent(Intent);
        }

        private void HandleNewTaskIntent(Intent intent)
        {
            if (intent?.GetBooleanExtra(ExtraNewTask, false) != true) return;
            intent.RemoveExtra(ExtraNewTask);
            ShowNewTask(intent.GetStringExtra(ExtraUrl), intent.GetStringExtra(ExtraName),
                intent.GetStringExtra(ExtraCookie), intent.GetStringExtra(ExtraUserAgent),
                intent.GetStringExtra(ExtraReferer), intent.GetBooleanExtra(ExtraHls, false));
        }

        private LinearLayout BuildTopBar()
        {
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetBackgroundColor(Color.Rgb(0xF7, 0xF7, 0xF7));
            bar.SetPadding(Dp(16), 0, Dp(16), 0);

            var back = new FrameLayout(this);
            back.LayoutParameters = new LinearLayout.LayoutParams(Dp(44), Dp(44));
            back.AddView(new IconView(this, IconKind.Back) { IconColor = unchecked((int)0xFF111111) },
                new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
            back.Click += (s, e) => { if (_batchMode) ExitBatchMode(); else Finish(); };
            bar.AddView(back);

            var title = new TextView(this) { Text = L10n.T("下载管理"), TextSize = 17 };
            title.SetTextColor(TextMain);
            title.SetTypeface(null, TypefaceStyle.Bold);
            title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            bar.AddView(title);

            var create = new TextView(this) { Text = L10n.T("＋ 新建"), TextSize = 14, Gravity = GravityFlags.Center };
            create.SetTextColor(Accent);
            create.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
            create.SetMinimumHeight(Dp(44));
            create.Click += (s, e) => ShowNewTask();
            bar.AddView(create);

            var more = new TextView(this) { Text = "⋮", TextSize = 24, Gravity = GravityFlags.Center };
            more.SetTextColor(TextSub);
            more.SetPadding(Dp(8), 0, Dp(8), 0);
            more.SetMinimumHeight(Dp(44));
            more.Click += (s, e) => ShowMoreMenu();
            bar.AddView(more);

            bar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(56));
            return bar;
        }

        private LinearLayout BuildCategoryBar()
        {
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
            bar.SetBackgroundColor(CardBg);

            var scroll = new HorizontalScrollView(this);
            scroll.HorizontalScrollBarEnabled = false;
            
            var container = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            for (int i = 0; i < Categories.Length; i++)
            {
                var cat = Categories[i];
                var idx = i;
                var chip = new TextView(this)
                {
                    Text = cat,
                    TextSize = 13,
                    Gravity = GravityFlags.Center
                };
                chip.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
                UpdateCategoryChip(chip, idx == _currentCategory);
                chip.Click += (s, e) => 
                {
                    _currentCategory = idx;
                    SaveSettings();
                    RefreshCategoryBar();
                    Refresh();
                };
                container.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
            }
            scroll.AddView(container);
            bar.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(44)));
            return bar;
        }

        private void UpdateCategoryChip(TextView chip, bool selected)
        {
            chip.SetTextColor(selected ? Accent : TextMain);
            chip.SetTypeface(null, selected ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        private void RefreshCategoryBar()
        {
            var scroll = _categoryBar.GetChildAt(0) as HorizontalScrollView;
            var container = scroll?.GetChildAt(0) as LinearLayout;
            if (container != null)
            {
                for (int j = 0; j < container.ChildCount; j++)
                {
                    var chip = container.GetChildAt(j) as TextView;
                    if (chip != null) UpdateCategoryChip(chip, j == _currentCategory);
                }
            }
        }

        private LinearLayout BuildBatchBar()
        {
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetBackgroundColor(Color.Rgb(0xE3, 0xF2, 0xFD));
            bar.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));

            _batchCount = new TextView(this) { Text = L10n.T("已选 0 项"), TextSize = 15 };
            _batchCount.SetTextColor(Accent);
            // 四个操作按钮若各占 32dp 左右内边距，会把计数列挤到只剩几 dp 而竖排。
            // 计数列保底宽度 + 按钮内边距收敛，保证窄屏下「已选 N 项」仍单行完整显示。
            _batchCount.SetMinimumWidth(Dp(76));
            bar.AddView(_batchCount, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            Action<TextView, Color, Action> addBtn = (tv, color, click) =>
            {
                tv.SetTextColor(color);
                tv.SetPadding(Dp(7), Dp(8), Dp(7), Dp(8));
                tv.Click += (s, e) => click();
                bar.AddView(tv);
            };
            addBtn(new TextView(this) { Text = L10n.T("取消全部"), TextSize = 14, Gravity = GravityFlags.Center },
                Warn, BatchCancelAll);
            addBtn(new TextView(this) { Text = L10n.T("重试失败"), TextSize = 14, Gravity = GravityFlags.Center },
                Accent, BatchRetryFailed);
            addBtn(new TextView(this) { Text = L10n.T("删除选中"), TextSize = 14, Gravity = GravityFlags.Center },
                Warn, BatchDeleteSelected);
            addBtn(new TextView(this) { Text = L10n.T("完成"), TextSize = 14, Gravity = GravityFlags.Center },
                Accent, ExitBatchMode);

            bar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            return bar;
        }

        private View BuildSpeedLimitRow()
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.SetBackgroundColor(CardBg);
            row.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));

            var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);
            
            var title = new TextView(this) { Text = L10n.T("下载限速"), TextSize = 15 };
            title.SetTextColor(TextMain);
            header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            var toggle = new Switch(this) { Checked = DownloadEngine.GlobalSpeedLimitKbps > 0 };
            toggle.CheckedChange += (s, e) =>
            {
                if (e.IsChecked)
                {
                    DownloadEngine.GlobalSpeedLimitKbps = 1024; // 默认 1 MB/s
                    _speedLimitBar.Visibility = ViewStates.Visible;
                    _speedLimitLabel.Visibility = ViewStates.Visible;
                }
                else
                {
                    DownloadEngine.GlobalSpeedLimitKbps = 0;
                    _speedLimitBar.Visibility = ViewStates.Gone;
                    _speedLimitLabel.Visibility = ViewStates.Gone;
                }
                SaveSettings();
            };
            header.AddView(toggle);

            _speedLimitLabel = new TextView(this) { Text = "1 MB/s", TextSize = 14 };
            _speedLimitLabel.SetTextColor(TextSub);
            header.AddView(_speedLimitLabel);

            row.AddView(header);

            _speedLimitBar = new SeekBar(this);
            _speedLimitBar.Max = 50000; // 50 MB/s max
            _speedLimitBar.Progress = DownloadEngine.GlobalSpeedLimitKbps;
            _speedLimitBar.Visibility = toggle.Checked ? ViewStates.Visible : ViewStates.Gone;
            _speedLimitBar.ProgressChanged += (s, e) =>
            {
                if (e.FromUser)
                {
                    DownloadEngine.GlobalSpeedLimitKbps = e.Progress;
                    _speedLimitLabel.Text = FormatSpeed(e.Progress);
                    SaveSettings();
                }
            };
            _speedLimitLabel.Visibility = toggle.Checked ? ViewStates.Visible : ViewStates.Gone;
            row.AddView(_speedLimitBar);

            // 剪贴板监听/完成动作
            var opts = new LinearLayout(this) { Orientation = Orientation.Vertical };
            opts.SetPadding(0, Dp(12), 0, 0);

            _clipboardMonitorCb = new CheckBox(this) { Text = L10n.T("监听剪贴板自动识别链接"), Checked = true };
            _clipboardMonitorCb.SetTextColor(TextMain);
            opts.AddView(_clipboardMonitorCb);

            _autoOpenCb = new CheckBox(this) { Text = L10n.T("下载完成自动打开文件"), Checked = false };
            _autoOpenCb.SetTextColor(TextMain);
            opts.AddView(_autoOpenCb);

            _extractArchiveCb = new CheckBox(this) { Text = L10n.T("下载完成自动解压压缩包"), Checked = false };
            _extractArchiveCb.SetTextColor(TextMain);
            opts.AddView(_extractArchiveCb);

            var pal = IosStyle.Colors(this);
            var dirBtn = new TextView(this)
            {
                Text = L10n.T("📁 自定义保存目录"),
                TextSize = IosStyle.BodySize,
                Gravity = GravityFlags.Center
            };
            dirBtn.SetTextColor(new Color(pal.Accent));
            dirBtn.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            dirBtn.Background = IosStyle.Rounded(this,
                pal.Night ? unchecked((int)0xFF2C2C2E) : unchecked((int)0xFFE7F6FE),
                IosStyle.ControlRadius);
            dirBtn.Click += (s, e) => PickDownloadDirectory();
            opts.AddView(dirBtn);

            row.AddView(opts);
            return row;
        }

        /* ===================== 新建任务对话框 ===================== */

        private void ShowNewTask(string initialUrl = null, string initialName = null, string cookie = null,
            string userAgent = null, string referer = null, bool forceHls = false)
        {
            try { _newTaskDialog?.Dismiss(); } catch { }
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.SetPadding(Dp(20), Dp(8), Dp(20), Dp(12));
            Func<string, string, bool, EditText> field = (label, value, secret) =>
            {
                var caption = new TextView(this) { Text = label, TextSize = 13 };
                caption.SetPadding(0, Dp(8), 0, Dp(2));
                body.AddView(caption);
                var input = new EditText(this) { Text = value ?? "", TextSize = 14 };
                input.SetSingleLine(false);
                input.SetMinHeight(Dp(44));
                input.SetMaxLines(3);
                input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagNoSuggestions;
                if (secret) input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword;
                body.AddView(input, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                return input;
            };
            var url = field(L10n.T("下载链接（HTTP / HTTPS）"), initialUrl, false);
            url.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri | Android.Text.InputTypes.TextFlagNoSuggestions;
            url.Hint = L10n.T("https://example.com/file.zip 或 playlist.m3u8");
            var name = field(L10n.T("文件名（可选，留空自动识别）"), initialName, false);
            var hls = new CheckBox(this) { Text = L10n.T("按 m3u8 下载（无扩展名链接可勾选）"), Checked = forceHls };
            body.AddView(hls);
            var key = field(L10n.T("AES-128 Key（可选）"), null, true);
            key.Hint = L10n.T("32 位十六进制 / Base64 / Key 的 HTTP(S) 地址");
            var note = new TextView(this)
            {
                Text = L10n.T("会自动检查 m3u8 的 EXT-X-KEY；留空时读取清单中的 Key 地址。支持 AES-128，检测到 SAMPLE-AES / DRM 时会明确提示。TS 分片合并为单个 .ts 文件。"),
                TextSize = 12
            };
            note.SetTextColor(TextSub);
            note.SetPadding(0, Dp(6), 0, Dp(8));
            body.AddView(note);
            var contextToggle = new CheckBox(this) { Text = L10n.T("使用浏览器下载上下文（Cookie / User-Agent / 来源页）"), Checked = !string.IsNullOrEmpty(cookie) || !string.IsNullOrEmpty(userAgent) || !string.IsNullOrEmpty(referer) };
            body.AddView(contextToggle);
            var requestFields = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
            body.AddView(requestFields);
            Func<string, string, bool, EditText> advanced = (label, value, secret) =>
            {
                var input = field(label, value, secret);
                var caption = body.GetChildAt(body.IndexOfChild(input) - 1);
                body.RemoveView(caption);
                body.RemoveView(input);
                requestFields.AddView(caption);
                requestFields.AddView(input);
                return input;
            };
            var cookieInput = advanced(L10n.T("Cookie（仅需要登录的网站）"), cookie, true);
            var uaInput = advanced(L10n.T("User-Agent（可选）"), userAgent, false);
            var refInput = advanced(L10n.T("来源页 Referer（可选）"), referer, false);
            var expand = new TextView(this) { Text = L10n.T("编辑请求上下文 ▾"), TextSize = 13 };
            expand.SetTextColor(Accent);
            expand.SetPadding(0, Dp(8), 0, Dp(8));
            expand.Click += (s, e) => requestFields.Visibility = requestFields.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            body.AddView(expand);

            // 协议选择
            var protoRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            protoRow.SetGravity(GravityFlags.CenterVertical);
            var protoLabel = new TextView(this) { Text = L10n.T("协议："), TextSize = 13 };
            protoLabel.SetPadding(0, 0, Dp(8), 0);
            protoRow.AddView(protoLabel);
            
            var spinner = new Spinner(this);
            var adapter = new ArrayAdapter(this, Android.Resource.Layout.SimpleSpinnerItem, new[] { "HTTP/HTTPS", "FTP", "WebDAV", "SFTP (需密钥)" });
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            spinner.Adapter = adapter;
            protoRow.AddView(spinner, new LinearLayout.LayoutParams(0, Dp(44), 1f));
            body.AddView(protoRow);

            var result = new TextView(this) { Text = L10n.T("尚未检测。检测会请求链接；开始下载时也会重新验证。"), TextSize = 13 };
            result.SetPadding(0, Dp(8), 0, Dp(8));
            body.AddView(result);
            var inspect = new Button(this) { Text = L10n.T("检测链接 / 加密方式") };
            body.AddView(inspect);
            var scroll = new ScrollView(this);
            scroll.AddView(body);
            var dialog = L10n.Dialog(this).SetTitle(L10n.T("新建下载任务")).SetView(scroll)
                .SetNegativeButton(L10n.T("取消"), (s, e) => { }).SetPositiveButton(L10n.T("开始下载"), (s, e) => { }).Create();
            _newTaskDialog = dialog;
            CancellationTokenSource inspection = null;
            int revision = 0;
            bool closing = false;
            Action invalidate = () =>
            {
                revision++;
                inspection?.Cancel();
                inspect.Enabled = true;
                result.Text = L10n.T("参数已修改，请重新检测；开始下载时会验证。");
            };
            url.TextChanged += (s, e) => invalidate();
            key.TextChanged += (s, e) => invalidate();
            cookieInput.TextChanged += (s, e) => invalidate();
            uaInput.TextChanged += (s, e) => invalidate();
            refInput.TextChanged += (s, e) => invalidate();
            hls.CheckedChange += (s, e) => invalidate();
            contextToggle.CheckedChange += (s, e) => invalidate();
            inspect.Click += async (s, e) =>
            {
                if (!ValidateHttpLink(url.Text, out string target)) { result.Text = L10n.T("请输入有效的 HTTP / HTTPS 直链。"); return; }
                inspection?.Cancel();
                inspection = new CancellationTokenSource();
                _inspectCancellation = inspection;
                int version = ++revision;
                inspect.Enabled = false;
                result.Text = L10n.T("正在读取链接并检测清单与加密方式…");
                try
                {
                    var details = await DownloadCenter.InspectHttpAsync(target,
                        contextToggle.Checked ? cookieInput.Text : null,
                        contextToggle.Checked ? uaInput.Text : null,
                        contextToggle.Checked ? refInput.Text : null,
                        key.Text, inspection.Token, hls.Checked);
                    if (closing || version != revision) return;
                    result.Text = details.Summary;
                    result.SetTextColor(details.Supported ? TextMain : Warn);
                }
                catch (System.OperationCanceledException) { }
                catch (Exception ex) { if (!closing && version == revision) { result.Text = L10n.T("检测失败：") + ex.Message; result.SetTextColor(Warn); } }
                finally { if (!closing && version == revision) inspect.Enabled = true; }
            };
            dialog.DismissEvent += (s, e) =>
            {
                closing = true;
                inspection?.Cancel();
                if (_newTaskDialog == dialog) _newTaskDialog = null;
            };
            dialog.Show();
            dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
            dialog.GetButton((int)DialogButtonType.Positive).Click += (s, e) =>
            {
                if (!ValidateHttpLink(url.Text, out string target)) { result.Text = L10n.T("请输入有效的 HTTP / HTTPS 直链。"); return; }
                try
                {
                    DownloadCenter.QueueHttp(target, name.Text,
                        contextToggle.Checked ? cookieInput.Text : null,
                        contextToggle.Checked ? uaInput.Text : null,
                        contextToggle.Checked ? refInput.Text : null, key.Text, hls.Checked);
                    dialog.Dismiss();
                    Refresh();
                    L10n.Toast(this, L10n.T("已加入下载任务"), ToastLength.Short).Show();
                }
                catch (Exception ex) { result.Text = L10n.T("无法创建任务：") + ex.Message; result.SetTextColor(Warn); }
            };
        }

        private static bool ValidateHttpLink(string value, out string normalized)
        {
            normalized = (value ?? "").Trim();
            return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
        }

        /* ===================== 刷新列表 ===================== */

        private void Refresh()
        {
            if (_list == null || IsFinishing) return;
            var tasks = DownloadCenter.List();
            _list.RemoveAllViews();
            
            // 过滤分类
            var filtered = tasks.Where(t => MatchCategory(t)).ToList();
            
            if (filtered.Count == 0)
            {
                _empty.Visibility = ViewStates.Visible;
                _empty.Text = _currentCategory == 0 
                    ? L10n.T("暂无下载任务。点「＋ 新建」粘贴直链或 m3u8，或在浏览器中下载文件。")
                    : $"暂无 {Categories[_currentCategory]} 类任务。";
            }
            else
            {
                _empty.Visibility = ViewStates.Gone;
                foreach (var t in filtered) _list.AddView(TaskCard(t));
            }
        }

        private bool MatchCategory(DownloadTask t)
        {
            if (_currentCategory == 0) return true;
            var cat = Categories[_currentCategory];
            var type = t.Type?.ToLowerInvariant() ?? "";
            // 分类名是中文、任务类型是英文，必须显式映射；否则视频/音乐/小说/m3u8 四个分类恒为空。
            switch (cat)
            {
                case "视频": return type == "video";
                case "音乐": return type == "music";
                case "小说": return type == "novel";
                case "m3u8": return type == "hls";
                case "文档": return type == "file" || type == "document";
                case "其他": return type != "video" && type != "music" && type != "novel"
                                  && type != "file" && type != "hls" && type != "document";
                default: return true;
            }
        }

        /* ===================== 任务卡片 ===================== */

        private View TaskCard(DownloadTask t)
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = RoundBg(CardBg, 14);
            card.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(0, 0, 0, Dp(10));

            // 长按进入批量模式
            card.LongClick += (s, e) =>
            {
                if (!_batchMode)
                {
                    EnterBatchMode();
                    ToggleSelection(t.Id);
                }
                e.Handled = true;
            };
            
            // 点击选中/取消选中
            if (_batchMode)
            {
                card.Click += (s, e) => ToggleSelection(t.Id);
                // 选中态视觉反馈
                if (_selectedIds.Contains(t.Id))
                    card.SetBackgroundColor(Color.Rgb(0xE3, 0xF2, 0xFD));
            }
            else
            {
                card.Click += (s, e) => ShowTaskDetail(t);
            }

            // 顶部行
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);

            var iconBox = new FrameLayout(this);
            iconBox.Background = RoundBg(unchecked((int)0xFFE7F6FE), 11);
            iconBox.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
            var kind = GetIconForType(t.Type);
            iconBox.AddView(new IconView(this, kind) { IconColor = Accent },
                new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
            top.AddView(iconBox);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            col.SetPadding(Dp(12), 0, Dp(8), 0);
            var name = new TextView(this) { Text = t.Title, TextSize = 16 };
            name.SetTextColor(TextMain);
            name.SetTypeface(null, TypefaceStyle.Bold);
            col.AddView(name);

            var st = new TextView(this) { Text = StatusLine(t), TextSize = 13 };
            st.SetTextColor(StatusColor(t));
            st.SetPadding(0, Dp(3), 0, 0);
            col.AddView(st);
            top.AddView(col);

            // 批量模式下的勾选指示器
            if (_batchMode)
            {
                var check = new CheckBox(this) { Checked = _selectedIds.Contains(t.Id) };
                check.Click += (s, e) => ToggleSelection(t.Id);
                top.AddView(check);
            }

            // 操作按钮
            if (t.Status == "failed" || t.Status == "canceled")
            {
                var retry = new TextView(this) { Text = L10n.T("重试"), TextSize = 14, Gravity = GravityFlags.Center };
                retry.SetTextColor(Color.White);
                retry.Background = RoundBg(Accent, 14);
                retry.SetPadding(Dp(16), Dp(7), Dp(16), Dp(7));
                var rid = t.Id;
                retry.Click += (s, e) =>
                {
                    try
                    {
                        if (DownloadCenter.Retry(rid)) L10n.Toast(this, L10n.T("已重新加入下载"), ToastLength.Short).Show();
                    }
                    catch { }
                };
                top.AddView(retry);
            }
            if (t.Type == "file" || t.Type == "hls" || t.Type == "video")
            {
                if (t.Status == "queued" || t.Status == "downloading" || t.Status == "merging")
                {
                    var cancel = new TextView(this) { Text = L10n.T("取消"), TextSize = 14, Gravity = GravityFlags.Center };
                    cancel.SetTextColor(Warn);
                    cancel.SetPadding(Dp(12), Dp(8), Dp(8), Dp(8));
                    cancel.Click += (s, e) => { if (DownloadCenter.CancelHttp(t.Id)) Refresh(); };
                    top.AddView(cancel);
                }
            }
            card.AddView(top);

            // 详细信息
            if (!string.IsNullOrWhiteSpace(t.DownloadInfo))
            {
                var detail = new TextView(this) { Text = t.DownloadInfo, TextSize = 12 };
                detail.SetTextColor(TextSub);
                detail.SetPadding(0, Dp(6), 0, 0);
                card.AddView(detail);
            }

            // 进度条
            if (t.Status != "done" && t.Status != "failed" && t.Status != "canceled")
            {
                var pb = new ProgressBar(this, null, Android.Resource.Style.WidgetProgressBarHorizontal);
                pb.Max = 100;
                pb.Progress = Math.Max(0, Math.Min(100, t.Progress));
                pb.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                {
                    TopMargin = Dp(10)
                };
                card.AddView(pb);
            }

            // 保存路径 + 操作
            string path = !string.IsNullOrEmpty(t.OutputPath) ? t.OutputPath : t.DirHint;
            if (!string.IsNullOrEmpty(path))
            {
                var pRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                pRow.SetGravity(GravityFlags.CenterVertical);
                pRow.SetPadding(0, Dp(8), 0, 0);
                var pTxt = new TextView(this) { Text = path, TextSize = 12 };
                pTxt.SetTextColor(TextSub);
                pTxt.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                pTxt.SetLineSpacing(0, 1.25f);
                pRow.AddView(pTxt);
                var copy = new TextView(this) { Text = L10n.T("复制"), TextSize = 13, Gravity = GravityFlags.Center };
                copy.SetTextColor(Accent);
                copy.SetPadding(Dp(10), Dp(6), Dp(4), Dp(6));
                var p = path;
                copy.Click += (s, e) => CopyPath(p);
                pRow.AddView(copy);
                if (t.Status == "done" && SysIO.File.Exists(t.OutputPath))
                {
                    var open = new TextView(this) { Text = L10n.T("打开"), TextSize = 13, Gravity = GravityFlags.Center };
                    open.SetTextColor(Accent);
                    open.SetPadding(Dp(12), Dp(6), Dp(4), Dp(6));
                    open.Click += (s, e) => OpenDownloadedFile(t.OutputPath);
                    pRow.AddView(open);
                    
                    var share = new TextView(this) { Text = L10n.T("分享"), TextSize = 13, Gravity = GravityFlags.Center };
                    share.SetTextColor(Accent);
                    share.SetPadding(Dp(12), Dp(6), Dp(4), Dp(6));
                    share.Click += (s, e) => ShareFile(t.OutputPath);
                    pRow.AddView(share);
                    
                    // 完成动作提示
                    if (DownloadEngine.AutoOpenOnComplete || DownloadEngine.ExtractArchivesOnComplete)
                    {
                        var actionHint = new TextView(this) { Text = "⚡", TextSize = 12, Gravity = GravityFlags.Center };
                        actionHint.SetTextColor(Ok);
                        actionHint.SetPadding(Dp(4), 0, Dp(4), 0);
                        pRow.AddView(actionHint);
                    }
                }
                card.AddView(pRow);
            }

            // 音乐三件套
            if (t.Type == "music" && !string.IsNullOrEmpty(t.OutputPath)
                && t.OutputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                var base0 = t.OutputPath.Substring(0, t.OutputPath.Length - 4);
                var tags = new List<string>();
                if (SysIO.File.Exists(base0 + ".jpg") || SysIO.File.Exists(base0 + ".png") || SysIO.File.Exists(base0 + ".webp")) tags.Add(L10n.T("封面"));
                if (SysIO.File.Exists(base0 + ".lrc")) tags.Add(L10n.T("歌词"));
                if (tags.Count > 0)
                {
                    var tag = new TextView(this) { Text = L10n.T("已包含：") + string.Join("、", tags), TextSize = 12 };
                    tag.SetTextColor(Ok);
                    tag.SetPadding(0, Dp(6), 0, 0);
                    card.AddView(tag);
                }
            }

            // 失败原因
            if (!string.IsNullOrEmpty(t.Error))
            {
                var err = new TextView(this) { Text = L10n.T("失败原因：") + t.Error, TextSize = 12 };
                err.SetTextColor(Warn);
                err.SetPadding(0, Dp(6), 0, 0);
                err.SetLineSpacing(0, 1.25f);
                card.AddView(err);
            }

            card.LayoutParameters = lp;
            return card;
        }

        private IconKind GetIconForType(string type)
        {
            switch (type?.ToLowerInvariant())
            {
                case "video": return IconKind.Video;
                case "music": return IconKind.Music;
                case "novel": return IconKind.Book;
                case "hls": return IconKind.Video;
                case "document": return IconKind.File;
                default: return IconKind.Download;
            }
        }

        /* ===================== 批量操作 ===================== */

        private void EnterBatchMode()
        {
            _batchMode = true;
            _selectedIds.Clear();
            _batchBar.Visibility = ViewStates.Visible;
            _batchCount.Text = L10n.T("已选 0 项");
            Refresh();
        }

        private void ExitBatchMode()
        {
            _batchMode = false;
            _selectedIds.Clear();
            _batchBar.Visibility = ViewStates.Gone;
            Refresh();
        }

        private void ToggleSelection(string id)
        {
            if (_selectedIds.Contains(id)) _selectedIds.Remove(id);
            else _selectedIds.Add(id);
            _batchCount.Text = $"已选 {_selectedIds.Count} 项";
            Refresh();
        }

        private void BatchCancelAll()
        {
            foreach (var id in _selectedIds.ToList())
                DownloadCenter.CancelHttp(id);
            ExitBatchMode();
            Refresh();
            L10n.Toast(this, L10n.T("已取消选中任务"), ToastLength.Short).Show();
        }

        private void BatchRetryFailed()
        {
            foreach (var id in _selectedIds.ToList())
                DownloadCenter.Retry(id);
            ExitBatchMode();
            Refresh();
            L10n.Toast(this, L10n.T("已重试失败任务"), ToastLength.Short).Show();
        }

        private void BatchDeleteSelected()
        {
            foreach (var id in _selectedIds.ToList())
            {
                DownloadCenter.CancelHttp(id);
                // 从列表移除（仅记录）
                var task = DownloadCenter.Get(id);
                if (task != null && task.Status == "done")
                {
                    try { DownloadCenter.ClearDone(); } catch { }
                }
            }
            ExitBatchMode();
            Refresh();
            L10n.Toast(this, L10n.T("已删除选中记录"), ToastLength.Short).Show();
        }

        /* ===================== 任务详情/更多菜单 ===================== */

        private void ShowTaskDetail(DownloadTask t)
        {
            var actions = new List<(string Label, Action Run)>
            {
                (L10n.T("打开文件"), () => OpenDownloadedFile(t.OutputPath)),
                (L10n.T("分享文件"), () => ShareFile(t.OutputPath)),
                (L10n.T("复制路径"), () => CopyPath(t.OutputPath)),
                (L10n.T("复制链接"), () => CopyPath(t.Url)),
                (L10n.T("查看详情"), () => ShowDetailDialog(t))
            };
            if (t.Status == "failed" || t.Status == "canceled")
                actions.Insert(0, (L10n.T("重试"), () => DownloadCenter.Retry(t.Id)));
            if (t.Status != "done")
                actions.Insert(0, (L10n.T("取消下载"), () => DownloadCenter.CancelHttp(t.Id)));
            actions.Add((L10n.T("删除记录"), () =>
            {
                DownloadCenter.CancelHttp(t.Id);
                if (t.Status == "done") DownloadCenter.ClearDone();
            }));

            var items = actions.Select(a => a.Label).ToArray();
            L10n.Dialog(this)
                .SetTitle(t.Title)
                .SetItems(items, (s, e) =>
                {
                    if (e.Which >= 0 && e.Which < actions.Count) actions[e.Which].Run();
                    Refresh();
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void ShowDetailDialog(DownloadTask t)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"类型: {t.Type}");
            sb.AppendLine($"状态: {StatusText(t.Status)}");
            sb.AppendLine($"进度: {t.Progress}%");
            sb.AppendLine($"链接: {t.Url}");
            sb.AppendLine($"保存路径: {t.OutputPath}");
            sb.AppendLine($"目录: {t.DirHint}");
            if (t.TotalBytes > 0) sb.AppendLine($"大小: {FormatBytes(t.TotalBytes)}");
            if (t.DownloadedBytes > 0) sb.AppendLine($"已下载: {FormatBytes(t.DownloadedBytes)}");
            if (!string.IsNullOrEmpty(t.Error)) sb.AppendLine($"错误: {t.Error}");
            sb.AppendLine($"添加时间: {t.Added:yyyy-MM-dd HH:mm}");
            
            L10n.Dialog(this)
                .SetTitle(L10n.T("任务详情"))
                .SetMessage(sb.ToString())
                .SetPositiveButton(L10n.T("复制全部"), (s, e) => CopyPath(sb.ToString()))
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void ShowMoreMenu()
        {
            var items = new (string Icon, string Label, Action Action)[]
            {
                ("🔍", L10n.T("搜索任务"), (Action)ShowSearchDialog),
                ("📁", L10n.T("自定义保存目录"), (Action)PickDownloadDirectory),
                ("⚡", L10n.T("限速设置"), (Action)ToggleSpeedLimit),
                ("📋", L10n.T("剪贴板监听"), (Action)ToggleClipboardMonitor),
                ("🗑️", L10n.T("清除已完成"), (Action)(() => { DownloadCenter.ClearDone(); Refresh(); })),
                ("📤", L10n.T("导出任务列表"), (Action)ExportTaskList),
                ("⚙️", L10n.T("完成动作设置"), (Action)ShowCompletionActions),
            };
            ShowActionGrid(L10n.T("更多功能"), items);
        }

        private void ShowActionGrid(string title, (string Icon, string Label, Action Action)[] actions)
        {
            var menu = L10n.Dialog(this).Create();
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            panel.Background = RoundBg(CardBg, 16);
            
            var header = new TextView(this) { Text = title, TextSize = 16, Gravity = GravityFlags.Center };
            header.SetTextColor(TextMain);
            header.SetPadding(0, 0, 0, Dp(12));
            panel.AddView(header);

            foreach (var a in actions)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
                
                var icon = new TextView(this) { Text = a.Icon, TextSize = 24, Gravity = GravityFlags.Center };
                row.AddView(icon, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
                
                var label = new TextView(this) { Text = a.Label, TextSize = 16 };
                label.SetTextColor(TextMain);
                label.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                label.SetPadding(Dp(12), 0, 0, 0);
                row.AddView(label);
                
                row.Click += (s, e) => { menu.Dismiss(); a.Action(); };
                panel.AddView(row);
            }
            menu.SetView(panel);
            menu.Show();
        }

        private void ShowSearchDialog()
        {
            var input = new EditText(this) { Hint = L10n.T("搜索文件名/链接/类型...") };
            input.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            L10n.Dialog(this)
                .SetTitle(L10n.T("搜索下载任务"))
                .SetView(input)
                .SetPositiveButton(L10n.T("搜索"), (s, e) =>
                {
                    var kw = input.Text?.ToString()?.ToLowerInvariant() ?? "";
                    var results = DownloadCenter.List().Where(t => 
                        t.Title.ToLowerInvariant().Contains(kw) || 
                        t.Url.ToLowerInvariant().Contains(kw) ||
                        t.Type.ToLowerInvariant().Contains(kw)).ToList();
                    if (results.Count == 0) { L10n.Toast(this, L10n.T("无匹配任务"), ToastLength.Short).Show(); return; }
                    ShowSearchResults(results);
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ShowSearchResults(List<DownloadTask> results)
        {
            var items = results.Select(t => $"{t.Title}\n{t.Url} · {StatusText(t.Status)}").ToArray();
            L10n.Dialog(this)
                .SetTitle($"搜索结果 ({results.Count})")
                .SetItems(items, (s, e) => ShowTaskDetail(results[e.Which]))
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void PickDownloadDirectory()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            StartActivityForResult(intent, 1001);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            if (requestCode == 1001 && resultCode == Result.Ok && data?.Data != null)
            {
                var uri = data.Data;
                var doc = DocumentFile.FromTreeUri(this, uri);
                if (doc != null)
                {
                    var takeFlags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission;
                    ContentResolver.TakePersistableUriPermission(uri, takeFlags);
                    _customDownloadDir = doc;
                    var prefs = GetSharedPreferences("download_settings", FileCreationMode.Private).Edit();
                    prefs.PutString(PrefDownloadDir, uri.ToString());
                    prefs.Apply();
                    L10n.Toast(this, L10n.T("已设置自定义下载目录：") + doc.Name, ToastLength.Short).Show();
                }
            }
            base.OnActivityResult(requestCode, resultCode, data);
        }

        private void EnsureDownloadDir()
        {
            if (_customDownloadDir != null && !_customDownloadDir.Exists())
            {
                try { _customDownloadDir = DocumentFile.FromTreeUri(this, Android.Net.Uri.Parse(_customDownloadDir.Uri.ToString())); } catch { }
            }
        }

        private void ToggleSpeedLimit()
        {
            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetPadding(Dp(16), Dp(12), Dp(16), Dp(8));

            var label = new TextView(this)
            {
                Text = DownloadEngine.GlobalSpeedLimitKbps <= 0
                    ? L10n.T("不限速")
                    : FormatSpeed(DownloadEngine.GlobalSpeedLimitKbps),
                TextSize = IosStyle.TitleSize,
                Gravity = GravityFlags.Center
            };
            label.SetTextColor(TextMain);
            panel.AddView(label, new LinearLayout.LayoutParams(-1, -2));

            var seek = new SeekBar(this) { Max = 50000, Progress = DownloadEngine.GlobalSpeedLimitKbps };
            seek.ProgressChanged += (s, e) => label.Text = e.Progress <= 0 ? L10n.T("不限速") : FormatSpeed(e.Progress);
            panel.AddView(seek, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(12) });

            var tip = new TextView(this)
            {
                Text = L10n.T("向左拖到 0 为不限速；设置对所有下载任务生效。"),
                TextSize = IosStyle.CaptionSize
            };
            tip.SetTextColor(TextSub);
            panel.AddView(tip, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });

            L10n.Dialog(this)
                .SetTitle(L10n.T("下载限速"))
                .SetView(panel)
                .SetPositiveButton(L10n.T("确定"), (s, e) =>
                {
                    DownloadEngine.GlobalSpeedLimitKbps = seek.Progress;
                    if (_speedLimitBar != null) _speedLimitBar.Progress = seek.Progress;
                    SaveSettings();
                    Refresh();
                    L10n.Toast(this, seek.Progress <= 0 ? L10n.T("已取消限速") : L10n.T("限速：") + FormatSpeed(seek.Progress), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ToggleClipboardMonitor()
        {
            if (_clipboardMonitorCb != null)
            {
                _clipboardMonitorCb.Checked = !_clipboardMonitorCb.Checked;
                SaveSettings();
                if (_clipboardMonitorCb.Checked) StartClipboardMonitor(); else StopClipboardMonitor();
            }
        }

        private void ShowCompletionActions()
        {
            var items = new[]
            {
                L10n.T("下载完成自动打开文件"),
                L10n.T("下载完成自动解压压缩包")
            };
            var checkedItems = new[] { DownloadEngine.AutoOpenOnComplete, DownloadEngine.ExtractArchivesOnComplete };
            L10n.Dialog(this)
                .SetTitle(L10n.T("完成后动作"))
                .SetMultiChoiceItems(items, checkedItems, (s, e) =>
                {
                    if (e.Which == 0) DownloadEngine.AutoOpenOnComplete = e.IsChecked;
                    else DownloadEngine.ExtractArchivesOnComplete = e.IsChecked;
                    SaveSettings();
                })
                .SetPositiveButton(L10n.T("确定"), (s, e) => { })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Show();
        }

        private void ExportTaskList()
        {
            var tasks = DownloadCenter.List();
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(tasks, Newtonsoft.Json.Formatting.Indented);
            var dir = GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath ?? "";
            var path = SysPath.Combine(dir, "Mchat_downloads_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            try
            {
                SysIO.File.WriteAllText(path, json);
                L10n.Toast(this, L10n.T("已导出到：") + path, ToastLength.Short).Show();
            }
            catch { L10n.Toast(this, L10n.T("导出失败"), ToastLength.Short).Show(); }
        }

        /* ===================== 剪贴板监听 ===================== */

        private void StartClipboardMonitor()
        {
            if (_clipboardMonitorCb?.Checked != true) return;
            _clipboardCancellation = new CancellationTokenSource();
            Task.Run(async () =>
            {
                string lastText = "";
                while (!_clipboardCancellation.Token.IsCancellationRequested)
                {
                    await Task.Delay(2000, _clipboardCancellation.Token);
                    try
                    {
                        var cm = (ClipboardManager)GetSystemService(ClipboardService);
                        if (cm?.PrimaryClip != null && cm.PrimaryClip.ItemCount > 0)
                        {
                            var text = cm.PrimaryClip.GetItemAt(0)?.CoerceToText(this)?.ToString()?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(text) && text != lastText && IsDownloadableLink(text))
                            {
                                lastText = text;
                                RunOnUiThread(() => ShowClipboardSnackbar(text));
                            }
                        }
                    }
                    catch { }
                }
            });
        }

        private void StopClipboardMonitor()
        {
            _clipboardCancellation?.Cancel();
        }

        private bool IsDownloadableLink(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            // 简单判断：http/https 开头，包含常见下载扩展名或 m3u8
            return (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                   (Regex.IsMatch(text, @"\.(mp4|mkv|avi|mov|webm|flv|mp3|flac|m4a|wav|ogg|zip|rar|7z|pdf|epub|txt|m3u8)([?#]|$)", RegexOptions.IgnoreCase) ||
                    text.Contains(".m3u8"));
        }

        private void ShowClipboardSnackbar(string link)
        {
            var layout = Window.DecorView.FindViewById(Android.Resource.Id.Content);
            var toast = L10n.Toast(this, L10n.T("检测到下载链接：") + link, ToastLength.Long);
            toast.Show();
            // 也可以用 AlertDialog 代替 Snackbar
            var dialog = L10n.Dialog(this)
                .SetTitle(L10n.T("检测到下载链接"))
                .SetMessage(link)
                .SetPositiveButton(L10n.T("新建任务"), (s, e) => ShowNewTask(link))
                .SetNegativeButton(L10n.T("忽略"), (s, e) => { })
                .Show();
        }

        /* ===================== 文件操作 ===================== */

        private void OpenDownloadedFile(string path)
        {
            try
            {
                if (!SysIO.File.Exists(path)) { L10n.Toast(this, L10n.T("文件不存在"), ToastLength.Short).Show(); return; }
                if (ApkInstallActivity.IsApkName(SysPath.GetFileName(path)) && ApkInstallActivity.RedirectLocalFileToInstaller(this, path, SysPath.GetFileName(path))) return;
                if (OfficeViewerActivity.IsSupported(SysPath.GetExtension(path))) { OfficeViewerActivity.Open(this, path, SysPath.GetFileName(path)); return; }
                var uri = FileProvider.GetUriForFile(this, "com.Ahochat.android.fileprovider", new JavaFile(path));
                var mime = path.EndsWith(".ts") ? "video/mp2t" : Android.Webkit.MimeTypeMap.Singleton.GetMimeTypeFromExtension(SysPath.GetExtension(path)) ?? "application/octet-stream";
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, mime);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(intent);
            }
            catch (Exception ex) { L10n.Toast(this, L10n.T("无法打开文件：") + ex.Message, ToastLength.Short).Show(); }
        }

        private void ShareFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !SysIO.File.Exists(path)) { L10n.Toast(this, L10n.T("文件不存在"), ToastLength.Short).Show(); return; }
                var uri = FileProvider.GetUriForFile(this, "com.Ahochat.android.fileprovider", new JavaFile(path));
                var intent = new Intent(Intent.ActionSend);
                intent.SetType("*/*");
                intent.PutExtra(Intent.ExtraStream, uri);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(Intent.CreateChooser(intent, L10n.T("分享文件")));
            }
            catch { L10n.Toast(this, L10n.T("分享失败"), ToastLength.Short).Show(); }
        }

        private void CopyPath(string p)
        {
            try
            {
                var cm = (ClipboardManager)GetSystemService(ClipboardService);
                cm.PrimaryClip = ClipData.NewPlainText("path", p);
                L10n.Toast(this, L10n.T("路径已复制"), ToastLength.Short).Show();
            }
            catch { L10n.Toast(this, L10n.T("复制失败"), ToastLength.Short).Show(); }
        }

        /* ===================== 工具方法 ===================== */

        private string StatusLine(DownloadTask t)
        {
            var sb = new StringBuilder();
            sb.Append(StatusText(t.Status));
            if (t.Total > 0) sb.Append("  ").Append(t.Done).Append('/').Append(t.Total);
            if (t.DownloadedBytes > 0 || t.TotalBytes > 0)
            {
                sb.Append("  ").Append(FormatBytes(t.DownloadedBytes));
                if (t.TotalBytes > 0) sb.Append(" / ").Append(FormatBytes(t.TotalBytes));
            }
            if (t.Status != "done" && t.Status != "failed" && t.Status != "canceled")
                sb.Append("  ").Append(Math.Max(0, Math.Min(100, t.Progress))).Append('%');
            return sb.ToString();
        }

        private string FormatBytes(long size)
        {
            if (size < 1024) return Math.Max(0, size) + " B";
            if (size < 1024 * 1024) return (size / 1024d).ToString("0.0") + " KB";
            if (size < 1024L * 1024 * 1024) return (size / (1024d * 1024)).ToString("0.0") + " MB";
            return (size / (1024d * 1024 * 1024)).ToString("0.00") + " GB";
        }

        private string FormatSpeed(int kbps)
        {
            if (kbps <= 0) return L10n.T("不限速");
            if (kbps < 1024) return kbps + " KB/s";
            return (kbps / 1024d).ToString("0.0") + " MB/s";
        }

        private string StatusText(string s)
        {
            switch (s)
            {
                case "queued": return "排队中";
                case "downloading": return "下载中";
                case "merging": return "合并中";
                case "done": return "完成";
                case "failed": return "失败";
                case "canceled": return "已取消";
                default: return s;
            }
        }

        private Color StatusColor(DownloadTask t)
        {
            if (t.Status == "done") return Ok;
            if (t.Status == "failed" || t.Status == "canceled") return Warn;
            return Accent;
        }

        private int Dp(float v) => (int)(v * Resources.DisplayMetrics.Density + 0.5f);

        private Android.Graphics.Drawables.GradientDrawable RoundBg(long color, int radiusDp, long strokeColor = 0)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor((int)color);
            d.SetCornerRadius(Dp(radiusDp));
            if (strokeColor != 0)
                d.SetStroke(Dp(1), Android.Content.Res.ColorStateList.ValueOf(new Color((int)strokeColor)));
            return d;
        }
    }
}
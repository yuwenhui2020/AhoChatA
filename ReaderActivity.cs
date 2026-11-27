// ============================================================
// 阅读模块 · 原生阅读器 Activity（C# / Xamarin.Android）
//
// 入口：ChatActivity「服务」页 → 媒体 → 阅读。
// 功能：本地 TXT/EPUB 导入（读取真实文件名、可重命名）、在线书源搜索、
//       书架封面网格、阅读进度（章号 + 章内位置，与网页端共用）、
//       沉浸式阅读（点中间隐藏/显示工具栏，点左右翻页）、
//       目录搜索、排版面板（6 种背景色 / 字号 / 行距 / 页边距 / 对齐）。
//
// 数据统一走 ReaderLibrary，网页端（/reader）读写的是同一份书架与偏好。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Webkit;
using Android.Provider;
using AhoChatA.Music;
using AhoChatA.Reader;

namespace AhoChatA
{
    [Activity(Label = "@string/act_reader", Exported = false,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize,
              Theme = "@style/AppTheme.NoActionBar")]
    public class ReaderActivity : BaseActivity
    {
        /// <summary>收到书籍后由 MediaAutoOpen 直投的文件路径（媒体目录里的真实文件）。</summary>
        public const string ExtraBook = "book_path";
        /// <summary>伴随 ExtraBook 的书名（与入库时一致，用于在书架中精确定位）。</summary>
        public const string ExtraBookName = "book_name";

        /* ---------------- 与主界面一致的配色 ---------------- */
        private const int Accent = unchecked((int)0xFF007AFF);
        private const int TextMain = unchecked((int)0xFF1C1C1E);
        private const int TextSub = unchecked((int)0xFF8E8E93);
        private const int PageBg = unchecked((int)0xFFF2F2F7);
        private const int CardBg = unchecked((int)0xFFFFFFFF);
        private const int ChipBg = unchecked((int)0xFFEFF1F4);
        private const int ChipText = unchecked((int)0xFF4A4E57);

        private ReaderStore _store;
        private ReaderEngine _engine;
        private List<ShelfItem> _shelf;
        private List<BookSource> _sources;
        private ReaderPrefs _prefs;

        private readonly Dictionary<string, Book> _openBooks = new Dictionary<string, Book>();
        // 书架封面解码结果（后台解码 + 内存复用），避免每次进书架都重新解码同一批图
        private readonly Dictionary<string, Bitmap> _coverCache = new Dictionary<string, Bitmap>();

        private LinearLayout _shelfPage;
        private LinearLayout _shelfList;
        private TextView _shelfSub;
        // 阅读页用 FrameLayout：正文始终铺满整个窗口，顶/底栏作为浮层压在正文之上。
        // 这样菜单显隐不会改变正文视口尺寸，滚动位置与分页都不会跳动。
        private FrameLayout _readerPage;
        private WebView _web;
        private FrameLayout _bodyHost;      // 正文容器：文本 WebView 与 PDF 连续拼页视图二选一
        private AhoChatA.Reader.PdfContinuousView _pdfScroll;
        private TextView _chapterTitle;
        private Book _curBook;
        private string _curId;
        private int _ci = 0;

        // PDF：当前文件路径、当前渲染缩放档、防止异步页图串页
        private string _pdfPath;
        private int _pdfBucket = 100;
        private int _pdfSeq;

        /* 阅读页需要跟随主题变色的控件 */
        private LinearLayout _topBar, _botBar;
        private LinearLayout _topChrome, _botChrome;   // 浮在正文上的顶/底栏容器（Gone 时不占位）
        private View _topLine, _botLine;
        private TextView _btnBack, _btnToc, _btnAa, _btnMark, _btnPrev, _btnNext, _progText;
        private bool _chromeHidden;

        /* ---------------- 阅读页音乐迷你条（随简易菜单唤起） ---------------- */
        private LinearLayout _musicBar;
        private View _musicLine, _mbProg;
        private ImageView _mbCover;
        private TextView _mbTitle, _mbLyric;
        private IconView _mbPrev, _mbToggle, _mbNext, _mbQueueBtn;
        private Lyrics _mbLyrics;
        private int _mbLyricIdx = -2;
        private string _mbSongKey = "";
        private bool _mbEventsOn;

        // 章节内阅读位置（0~1），换章时归零；用于恢复滚动位置与保存进度
        private double _pct;
        private bool _restoring;
        private bool _pendingRestore;

        // 连读：同一份文档里依次拼接多章，读到底自动续下一章
        private readonly List<int> _docChaps = new List<int>();    // 文档内已渲染的章节号（顺序）
        private readonly List<double> _anchors = new List<double>();// 各章起点占「可滚动高度」的比例 0~1
        private bool _appending;
        private bool _appendStop;      // 拼接失败后本次阅读不再重试，避免反复打网络

        private bool _saveQueued;

        // 「解析」页（替换原「发现」入口）
        private LinearLayout _explorePage, _exBody;
        private TextView _exTitle;
        private EditText _parseUrl;
        private TextView _parseStatus;
        private NovelParseResult _parsed;
        private readonly List<CheckBox> _parseChecks = new List<CheckBox>();
        private bool _parsing;
        private bool _previewing;
        private const int ChapterPreviewLimit = 8000;
        // 旧发现页状态保留，方法仍可复用，但入口已被「解析」替换
        private ExploreCategory _exCat;
        private BookSource _exSrc;
        private int _exPage = 1;

        // 朗读
        internal Android.Speech.Tts.TextToSpeech _tts;
        internal bool _ttsReady;
        internal string _ttsPending;
        private bool _ttsOn;

        private const int REQ_PICK = 1001;
        private const int REQ_SRC = 1002;
        private const int REQ_SCAN = 1003;   // 扫描文件夹（SAF 目录树）

        // 书架视图筛选：仅显示 TXT/PDF（在线书始终显示）。默认开，可在书架页关闭。
        private bool _filterTxtPdf = true;
        private readonly HashSet<string> _collapsed = new HashSet<string>();   // 已折叠的书架文件夹

        // 增量搜索对话框状态
        private TextView _srchStatus;
        private LinearLayout _srchBox;
        private readonly HashSet<string> _srchSeen = new HashSet<string>();
        private int _srchTotal, _srchDone, _srchCount;

        // 扫描文件夹时纳入的书籍扩展名
        private static readonly string[] BookExts = { ".txt", ".text", ".pdf", ".epub", ".fb2", ".fbz", ".md", ".markdown", ".zip" };

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            ReaderLibrary.Init(this);
            _store = ReaderLibrary.Store;
            _engine = ReaderLibrary.Engine;
            _shelf = ReaderLibrary.Shelf();
            _sources = ReaderLibrary.Sources();
            _prefs = ReaderLibrary.Prefs;

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            root.AddView(BuildShelfPage());
            root.AddView(BuildExplorePage());
            root.AddView(BuildReaderPage());
            _explorePage.Visibility = ViewStates.Gone;
            _readerPage.Visibility = ViewStates.Gone;

            SetContentView(root);
            ApplyChrome();

            // 收到书籍直投：媒体自动加载后由 MediaAutoOpen 把文件落到这里，打开即读
            try
            {
                var autoPath = Intent?.GetStringExtra(ExtraBook);
                if (!string.IsNullOrEmpty(autoPath) && File.Exists(autoPath))
                {
                    var autoName = Intent.GetStringExtra(ExtraBookName);
                    if (string.IsNullOrEmpty(autoName)) autoName = System.IO.Path.GetFileName(autoPath);
                    AutoOpenBookPath(autoPath, autoName);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Reader", "书籍直投打开失败: " + ex.Message);
            }

            // 旧数据（升级前导入、没有封面的 PDF）只补一次：
            // PdfCache 内有 marker，绝不会在每次启动阅读功能时重读每本 PDF 的第一页
            Task.Run(() =>
            {
                try
                {
                    var done = AhoChatA.Reader.PdfCache.BackfillCovers(_shelf, ReaderLibrary.ScreenWidth);
                    if (done.Count > 0)
                        RunOnUiThread(() => { _shelf = ReaderLibrary.Shelf(); RenderShelf(); });
                }
                catch { }
            });
        }

        // 已在前台（SingleTop 复用）时收到书籍直投：从 OnNewIntent 进入，复用同一套打开逻辑
        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            try
            {
                var p = intent?.GetStringExtra(ExtraBook);
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    var n = intent.GetStringExtra(ExtraBookName);
                    if (string.IsNullOrEmpty(n)) n = System.IO.Path.GetFileName(p);
                    AutoOpenBookPath(p, n);
                }
            }
            catch (Exception ex) { Android.Util.Log.Warn("Reader", "OnNewIntent 书籍直投失败: " + ex.Message); }
        }

        protected override void OnResume()
        {
            base.OnResume();
            // 网页端可能刚上传/改名/读了进度，回到前台就同步一次
            try
            {
                _shelf = ReaderLibrary.Shelf();
                _sources = ReaderLibrary.Sources();
                _prefs = ReaderLibrary.Prefs;
                if (_readerPage == null || _readerPage.Visibility != ViewStates.Visible)
                    RenderShelf();
                // 停在书架时顺手把「上次读到哪」往后的章节缓存好，点开书就不用等网络
                if (_readerPage == null || _readerPage.Visibility != ViewStates.Visible)
                    ReaderLibrary.PrefetchAhead(50);
            }
            catch { }

            // 后台播放状态可能在这期间变过（从音乐界面返回、通知栏切歌等），重连一次并刷新迷你条
            try
            {
                BindMusicEvents();
                UpdateMusicBar();
            }
            catch { }
            // RenderShelf 可能在 base.OnResume 之后重建视图，这里再统一一次控件外观。
            IosStyle.StyleCheckBoxes(this);
        }

        protected override void OnPause()
        {
            // 阅读页在后台时不需要跟着 500ms 刷新，退订避免白耗电
            try { UnbindMusicEvents(); } catch { }
            if (_ttsOn) StopTts();
            SaveCurrentProgress();
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            try { UnbindMusicEvents(); } catch { }
            try { _tts?.Stop(); _tts?.Shutdown(); } catch { }
            try { _pdfScroll?.Release(); } catch { }   // 释放 PDF 已加载的页图位图
            base.OnDestroy();
        }

        /* ============================================================
         *  主题（背景色）表
         * ============================================================ */
        private sealed class Pal
        {
            public int Bg, Fg, Sub, Line, Chrome, Sheet, Chip, ChipSel, Cur;
            public bool Dark;
        }

        private static readonly (string key, string label, int preview)[] Themes =
        {
            ("light", L10n.T("白"),   unchecked((int)0xFFFDFCFA)),
            ("sepia", L10n.T("米黄"), unchecked((int)0xFFF4EAD5)),
            ("green", L10n.T("护眼"), unchecked((int)0xFFCCE8CF)),
            ("gray",  L10n.T("浅灰"), unchecked((int)0xFFE7E9EC)),
            ("dark",  L10n.T("深灰"), unchecked((int)0xFF1B1E22)),
            ("black", L10n.T("黑"),   unchecked((int)0xFF000000)),
        };

        private static Pal Palette(string key)
        {
            switch (key)
            {
                case "sepia":
                    return new Pal
                    {
                        Bg = unchecked((int)0xFFF4EAD5), Fg = unchecked((int)0xFF4A3B2C),
                        Sub = unchecked((int)0xFF8D7659), Line = unchecked((int)0xFFDCCBA9),
                        Chrome = unchecked((int)0xFFF1E5CC), Sheet = unchecked((int)0xFFF9F2E2),
                        Chip = unchecked((int)0xFFEEE2C9), ChipSel = unchecked((int)0xFFFCF6E8),
                        Cur = unchecked((int)0xFFF3E7CE), Dark = false
                    };
                case "green":
                    return new Pal
                    {
                        Bg = unchecked((int)0xFFCCE8CF), Fg = unchecked((int)0xFF22301F),
                        Sub = unchecked((int)0xFF5F7360), Line = unchecked((int)0xFFB2D2B7),
                        Chrome = unchecked((int)0xFFDCEEE0), Sheet = unchecked((int)0xFFE4F2E7),
                        Chip = unchecked((int)0xFFD3E8D7), ChipSel = unchecked((int)0xFFF2FAF4),
                        Cur = unchecked((int)0xFFDCEFE1), Dark = false
                    };
                case "gray":
                    return new Pal
                    {
                        Bg = unchecked((int)0xFFE7E9EC), Fg = unchecked((int)0xFF26282C),
                        Sub = unchecked((int)0xFF767B84), Line = unchecked((int)0xFFD2D5DA),
                        Chrome = unchecked((int)0xFFF0F1F3), Sheet = unchecked((int)0xFFF4F5F7),
                        Chip = unchecked((int)0xFFDFE2E6), ChipSel = unchecked((int)0xFFF8F9FA),
                        Cur = unchecked((int)0xFFE3EEF8), Dark = false
                    };
                case "dark":
                    return new Pal
                    {
                        Bg = unchecked((int)0xFF1B1E22), Fg = unchecked((int)0xFFB9BFC8),
                        Sub = unchecked((int)0xFF767E89), Line = unchecked((int)0xFF2C3137),
                        Chrome = unchecked((int)0xFF23272C), Sheet = unchecked((int)0xFF23272C),
                        Chip = unchecked((int)0xFF2F343B), ChipSel = unchecked((int)0xFF3C444E),
                        Cur = unchecked((int)0xFF272C33), Dark = true
                    };
                case "black":
                    return new Pal
                    {
                        Bg = unchecked((int)0xFF000000), Fg = unchecked((int)0xFF8E959F),
                        Sub = unchecked((int)0xFF5E646C), Line = unchecked((int)0xFF1E2125),
                        Chrome = unchecked((int)0xFF0E1013), Sheet = unchecked((int)0xFF141619),
                        Chip = unchecked((int)0xFF202428), ChipSel = unchecked((int)0xFF2C3238),
                        Cur = unchecked((int)0xFF191C20), Dark = true
                    };
                default:
                    return new Pal
                    {
                        Bg = unchecked((int)0xFFFDFCFA), Fg = unchecked((int)0xFF2A2C30),
                        Sub = unchecked((int)0xFF9AA0A8), Line = unchecked((int)0xFFE6E7EA),
                        Chrome = unchecked((int)0xFFFFFFFF), Sheet = unchecked((int)0xFFFFFFFF),
                        Chip = unchecked((int)0xFFEFF1F4), ChipSel = unchecked((int)0xFFFFFFFF),
                        Cur = unchecked((int)0xFFF2F8FD), Dark = false
                    };
            }
        }

        private Pal C => Palette(_prefs.Theme);

        private static string Hex(int argb) => "#" + (argb & 0xFFFFFF).ToString("X6");
        private string BgHex() => Hex(C.Bg);
        private string FgHex() => Hex(C.Fg);
        private string SubHex() => Hex(C.Sub);
        private string LineHex() => Hex(C.Line);

        private int ThText => C.Fg;
        private int ThSub => C.Sub;
        private int ThChrome => C.Chrome;
        private int ThLine => C.Line;
        private int ThSheet => C.Sheet;
        private int ThChip => C.Chip;
        private int ThChipSel => C.ChipSel;
        internal int CurChapterBg() => C.Cur;

        /* ============================================================
         *  书架页
         * ============================================================ */
        private View BuildShelfPage()
        {
            _shelfPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _shelfPage.SetBackgroundColor(new Color(PageBg));
            _shelfPage.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
            header.SetPadding(Dp(16), Dp(8), Dp(16), Dp(4));

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);

            var title = new TextView(this) { Text = L10n.T("书架"), TextSize = 22 };
            title.SetTextColor(new Color(TextMain));
            title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            row.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            row.AddView(Pill(L10n.T("解析"), v => OpenExplore()));
            row.AddView(Pill(L10n.T("搜索"), OnSearch));
            row.AddView(Pill(L10n.T("导入"), OnImport));
            row.AddView(Pill(L10n.T("书源"), OnManageSources));
            row.AddView(Pill(L10n.T("网页"), OnWebReader));
            header.AddView(row);

            _shelfSub = new TextView(this) { TextSize = 12 };
            _shelfSub.SetTextColor(new Color(TextSub));
            _shelfSub.SetPadding(0, Dp(4), 0, 0);
            header.AddView(_shelfSub);

            // 第二行：扫描文件夹 + 仅 TXT/PDF 筛选
            var row2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row2.SetGravity(GravityFlags.CenterVertical);
            row2.SetPadding(Dp(2), Dp(8), Dp(2), Dp(2));
            row2.AddView(Pill(L10n.T("扫描文件夹"), v => OnScanFolder(v)));
            var filterChk = new CheckBox(this)
            {
                Text = L10n.T("仅显示 TXT/PDF"),
                Checked = _filterTxtPdf,
                TextSize = 13
            };
            filterChk.SetTextColor(new Color(ChipText));
            IosStyle.StyleCheckBoxes(filterChk);
            filterChk.CheckedChange += (s, e) => { _filterTxtPdf = e.IsChecked; RenderShelf(); };
            row2.AddView(filterChk);
            header.AddView(row2);

            _shelfPage.AddView(header);

            var scroll = new ScrollView(this);
            scroll.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f);
            scroll.VerticalScrollBarEnabled = false;
            _shelfList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _shelfList.SetPadding(Dp(16), Dp(4), Dp(16), Dp(28));
            scroll.AddView(_shelfList);
            _shelfPage.AddView(scroll);

            RenderShelf();
            return _shelfPage;
        }

        private void RenderShelf()
        {
            if (_shelfList == null) return;
            _shelfList.RemoveAllViews();

            int local = 0, online = 0;
            foreach (var it in _shelf) { if (it.Type == "local") local++; else online++; }
            _shelfSub.Text = _shelf.Count == 0
                ? L10n.T("本地与网络书籍都放这里，网页端也能上传")
                : (L10n.T("共 ") + _shelf.Count + L10n.T(" 本 · 本地 ") + local + (online > 0 ? L10n.T(" · 在线 ") + online : ""));

            // 「仅 TXT/PDF」筛选：在线书始终显示；本地非 txt/pdf 仅隐藏（仍可打开，不删除）
            var view = new List<ShelfItem>();
            if (_filterTxtPdf)
            {
                foreach (var it in _shelf)
                {
                    if (it.Type == "online") { view.Add(it); continue; }
                    var f = ReaderLibrary.FormatOf(it);
                    if (f == "txt" || f == "pdf") view.Add(it);
                }
            }
            else
            {
                view.AddRange(_shelf);
            }

            if (view.Count == 0)
            {
                if (_shelf.Count > 0 && _filterTxtPdf) _shelfList.AddView(FilterHiddenNote());
                else _shelfList.AddView(EmptyShelf());
                return;
            }

            // 分区：先文件夹（pdf-only 组 → txt-only 组 → 其它），再顶层单本。
            // 组内/顶层单本按「pdf 在 txt 前；本地在在线前，但 txt 允许本地与在线混排」。
            var top = new List<ShelfItem>();
            var groups = new Dictionary<string, List<ShelfItem>>();
            foreach (var it in view)
            {
                var f = it.Folder ?? "";
                if (f.Length == 0) top.Add(it);
                else
                {
                    if (!groups.TryGetValue(f, out var g)) { g = new List<ShelfItem>(); groups[f] = g; }
                    g.Add(it);
                }
            }

            top.Sort(CompareShelfBooks);

            var groupList = new List<KeyValuePair<string, List<ShelfItem>>>(groups);
            groupList.Sort((a, b) =>
            {
                int c = GroupRank(a.Value).CompareTo(GroupRank(b.Value));
                if (c != 0) return c;
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var kv in groupList)
            {
                kv.Value.Sort(CompareShelfBooks);
                bool collapsed = _collapsed.Contains(kv.Key);
                _shelfList.AddView(FolderHeader(kv.Key, kv.Value.Count, collapsed));
                if (!collapsed) AppendGrid(_shelfList, kv.Value);
            }

            if (top.Count > 0) AppendGrid(_shelfList, top);
        }

        /// <summary>文件夹排序权重：只有 pdf 的文件夹 → 只有 txt 的文件夹 → 其它/混合。</summary>
        private static int GroupRank(List<ShelfItem> items)
        {
            bool hasPdf = false, hasTxt = false, hasOther = false;
            foreach (var it in items)
            {
                var f = ReaderLibrary.FormatOf(it);
                if (f == "pdf") hasPdf = true;
                else if (f == "txt") hasTxt = true;
                else hasOther = true;
            }
            if (hasPdf && !hasTxt && !hasOther) return 0;  // 只有 pdf
            if (hasTxt && !hasPdf && !hasOther) return 1;  // 只有 txt
            return 2;                                       // 混合 / 其它
        }

        /// <summary>单本排序：pdf 在 txt 前，txt 在其它前；非 txt 本地在在线前，txt 本地与在线按名称混排。</summary>
        private static int CompareShelfBooks(ShelfItem a, ShelfItem b)
        {
            int fa = BookRank(a), fb = BookRank(b);
            if (fa != fb) return fa.CompareTo(fb);   // pdf(0) → txt(1) → 其它(2)
            if (fa == 1)
                return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
            // 本地在在线前
            bool la = a.Type == "local", lb = b.Type == "local";
            if (la != lb) return la ? -1 : 1;
            return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static int BookRank(ShelfItem it)
        {
            var f = ReaderLibrary.FormatOf(it);
            if (f == "pdf") return 0;
            if (f == "txt") return 1;
            return 2;
        }

        /// <summary>把若干书按 3 列网格铺进容器。</summary>
        private void AppendGrid(LinearLayout container, List<ShelfItem> items)
        {
            LinearLayout row = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (i % 3 == 0)
                {
                    row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    row.LayoutParameters = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                    container.AddView(row);
                }
                row.AddView(BookCell(items[i]));
            }
            int rem = items.Count % 3;
            if (rem != 0 && row != null)
            {
                for (int k = 0; k < 3 - rem; k++)
                {
                    var filler = new View(this);
                    filler.LayoutParameters = new LinearLayout.LayoutParams(0, 1, 1f);
                    row.AddView(filler);
                }
            }
        }

        /// <summary>书架文件夹头部：名称 + 数量 + 折叠箭头；点击折叠，长按出文件夹操作。</summary>
        private View FolderHeader(string name, int count, bool collapsed)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(4), Dp(12), Dp(4), Dp(2));

            var arrow = new TextView(this) { Text = collapsed ? "▸" : "▾", TextSize = 16 };
            arrow.SetTextColor(new Color(TextSub));
            arrow.SetPadding(0, 0, Dp(6), 0);
            row.AddView(arrow);

            var t = new TextView(this) { Text = name + "  (" + count + ")", TextSize = 14 };
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            t.SetTextColor(new Color(TextMain));
            row.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            var cap = name;
            row.Click += (s, e) =>
            {
                if (_collapsed.Contains(cap)) _collapsed.Remove(cap); else _collapsed.Add(cap);
                RenderShelf();
            };
            row.LongClick += (s, e) => { FolderActions(cap); e.Handled = true; };
            return row;
        }

        private View FilterHiddenNote()
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetGravity(GravityFlags.CenterHorizontal);
            box.SetPadding(0, Dp(80), 0, Dp(20));
            var t = new TextView(this)
            {
                Text = L10n.T("已按「仅显示 TXT/PDF」筛选，其它格式的书籍被隐藏。\n关闭上方开关即可显示全部（其它格式仍可正常打开）。"),
                TextSize = 13
            };
            t.SetTextColor(new Color(ChipText));
            t.Gravity = GravityFlags.Center;
            t.SetLineSpacing(1, 1.2f);
            box.AddView(t);
            return box;
        }

        private View EmptyShelf()
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetGravity(GravityFlags.CenterHorizontal);
            box.SetPadding(0, Dp(80), 0, Dp(20));

            var icon = new IconView(this, IconKind.Book) { IconColor = unchecked((int)0xFFC6CBD3), Thickness = 1.4f };
            box.AddView(icon, new LinearLayout.LayoutParams(Dp(58), Dp(58)));

            var t1 = new TextView(this) { Text = L10n.T("书架还是空的"), TextSize = 15 };
            t1.SetTextColor(new Color(ChipText));
            t1.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            t1.SetPadding(0, Dp(14), 0, 0);
            t1.Gravity = GravityFlags.Center;
            box.AddView(t1);

            var t2 = new TextView(this)
            {
                Text = L10n.T("点「导入」选 TXT / EPUB，或在电脑浏览器打开\n手机地址「:7790/reader」直接上传阅读"),
                TextSize = 12
            };
            t2.SetTextColor(new Color(TextSub));
            t2.Gravity = GravityFlags.Center;
            t2.SetPadding(0, Dp(7), 0, 0);
            box.AddView(t2);

            return box;
        }

        /// <summary>书架格子：渐变封面（首字）+ 书名 + 进度。</summary>
        private View BookCell(ShelfItem item)
        {
            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lp.SetMargins(Dp(6), Dp(8), Dp(6), Dp(12));
            col.LayoutParameters = lp;

            var cover = new FrameLayout(this);
            cover.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(146));
            cover.Background = CoverDrawable(item.Name);

            // 首字占位先放上：封面解码完成后自动隐藏，书架多本时也不会卡住首屏
            var touch = new LinearLayout(this) { Orientation = Orientation.Vertical };
            touch.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            var initial = new TextView(this) { Text = Initial(item.Name), TextSize = 33 };
            initial.SetTextColor(Color.Argb(232, 255, 255, 255));
            initial.SetTypeface(Typeface.Create("serif", TypefaceStyle.Normal), TypefaceStyle.Bold);
            initial.Gravity = GravityFlags.Center;
            touch.AddView(initial, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            // PDF 用导入时缓存好的第一页作封面；EPUB 用内嵌封面。
            // 只解码缓存 JPEG（绝不打开原 PDF），且解码放后台，不占界面线程。
            if (AhoChatA.Reader.PdfCache.HasCover(item.Id))
            {
                var coverIv = new ImageView(this);
                coverIv.LayoutParameters = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
                coverIv.SetScaleType(ImageView.ScaleType.CenterCrop);
                coverIv.Visibility = ViewStates.Invisible;
                cover.AddView(coverIv);

                Bitmap hit;
                if (_coverCache.TryGetValue(item.Id ?? "", out hit) && hit != null && !hit.IsRecycled)
                {
                    coverIv.SetImageBitmap(hit);
                    coverIv.Visibility = ViewStates.Visible;
                    initial.Visibility = ViewStates.Gone;
                }
                else
                {
                    var ivRef = coverIv;
                    var initialRef = initial;
                    var coverId = item.Id ?? "";
                    var coverPath = AhoChatA.Reader.PdfCache.CoverPath(item.Id);
                    Task.Run(() =>
                    {
                        Bitmap bmp = null;
                        try
                        {
                            var opt = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Rgb565 };
                            bmp = BitmapFactory.DecodeFile(coverPath, opt);
                        }
                        catch { }
                        if (bmp == null) return;
                        RunOnUiThread(() =>
                        {
                            try
                            {
                                if (_coverCache.Count > 80) _coverCache.Clear();
                                _coverCache[coverId] = bmp;
                                if (ivRef.Parent != null)
                                {
                                    ivRef.SetImageBitmap(bmp);
                                    ivRef.Visibility = ViewStates.Visible;
                                    initialRef.Visibility = ViewStates.Gone;
                                }
                            }
                            catch { }
                        });
                    });
                }
            }

            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var barLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(3));
            barLp.SetMargins(Dp(10), 0, Dp(10), Dp(10));
            barLp.Gravity = GravityFlags.Bottom;
            bar.LayoutParameters = barLp;
            var trackBg = new GradientDrawable();
            trackBg.SetCornerRadius(Dp(2));
            trackBg.SetColor(Color.Argb(52, 255, 255, 255));
            bar.Background = trackBg;
            double pr = Math.Max(0, Math.Min(1, item.Progress));
            if (pr > 0.001)
            {
                var fill = new View(this);
                fill.SetBackgroundColor(Color.Argb(226, 255, 255, 255));
                bar.AddView(fill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, (float)pr));
            }
            if (pr < 0.999)
            {
                var rest = new View(this);
                bar.AddView(rest, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, (float)Math.Max(0.001, 1 - pr)));
            }
            touch.AddView(bar);
            cover.AddView(touch);
            col.AddView(cover);

            var name = new TextView(this) { Text = DisplayName(item), TextSize = 12.5f };
            name.SetTextColor(new Color(TextMain));
            name.SetMaxLines(2);
            name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            name.SetPadding(Dp(2), Dp(8), Dp(2), 0);
            col.AddView(name);

            string sub = string.IsNullOrEmpty(item.Author) ? (item.Type == "local" ? "本地书籍" : "网络书籍") : item.Author;
            if (item.Progress > 0.001) sub += " · " + (int)Math.Round(item.Progress * 100) + "%";
            var subTv = new TextView(this) { Text = sub, TextSize = 10.5f };
            subTv.SetTextColor(new Color(TextSub));
            subTv.SetPadding(Dp(2), Dp(2), Dp(2), 0);
            subTv.SetMaxLines(1);
            subTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            col.AddView(subTv);

            var id = item.Id;
            col.Click += (s, e) => OpenBook(id);
            col.LongClick += (s, e) => { BookActions(item); e.Handled = true; };
            return col;
        }

        /// <summary>书架展示用书名；老数据里可能存的是 local_xxx / online_xxx 这种 id。</summary>
        private static string DisplayName(ShelfItem it)
        {
            string n = (it?.Name ?? "").Trim();
            if (n.Length == 0) return "未命名书籍";
            if (n.StartsWith("local_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("online_", StringComparison.OrdinalIgnoreCase)) return L10n.T("未命名书籍");
            return n;
        }

        private static string Initial(string s)
        {
            if (string.IsNullOrEmpty(s)) return L10n.T("书");
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch) || ch > 0x2E80)
                    return (ch >= 'a' && ch <= 'z') ? ((char)(ch - 32)).ToString() : ch.ToString();
            return s.Substring(0, 1);
        }

        private static int Darken(int c, float f)
        {
            int a = (c >> 24) & 0xFF;
            int r = (int)(((c >> 16) & 0xFF) * f);
            int g = (int)(((c >> 8) & 0xFF) * f);
            int b = (int)((c & 0xFF) * f);
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        private Drawable CoverDrawable(string name)
        {
            uint[] pal =
            {
                0xFF5B8DEFu, 0xFF7C6BF5u, 0xFFE0685Fu, 0xFF2FB6A6u,
                0xFFE2953Fu, 0xFF4E9FD1u, 0xFFB36BD6u, 0xFF3F8F6Eu
            };
            int h = 0;
            foreach (var ch in (name ?? "")) h = (h * 31 + ch) & 0x7FFFFFFF;
            int c1 = unchecked((int)pal[h % pal.Length]);
            var d = new GradientDrawable(GradientDrawable.Orientation.TopBottom,
                new[] { c1, Darken(c1, 0.78f) });
            d.SetCornerRadius(Dp(10));
            return d;
        }

        /* ---------------- 长按：重命名 / 删除 ---------------- */

        private void BookActions(ShelfItem item)
        {
            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var t = new TextView(this) { Text = DisplayName(item), TextSize = 13 };
            t.SetTextColor(new Color(ThSub));
            t.SetPadding(Dp(22), Dp(2), Dp(22), Dp(10));
            t.SetMaxLines(1);
            t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            sheet.AddView(t);

            sheet.AddView(ActionRow(L10n.T("重命名"), ThText, () => { actionDlg?.Dismiss(); RenameBook(item); }));
            if (item.Type == "online")
            {
                sheet.AddView(ActionRow(L10n.T("换源"), ThText, () => { actionDlg?.Dismiss(); ChangeSourceOf(item); }));
                sheet.AddView(ActionRow(L10n.T("缓存全书"), ThText, () => { actionDlg?.Dismiss(); CacheWholeBook(item); }));
                sheet.AddView(ActionRow(L10n.T("清除缓存"), ThText, () => { actionDlg?.Dismiss(); ClearBookCache(item); }));
            }
            sheet.AddView(ActionRow(L10n.T("书签"), ThText, () => { actionDlg?.Dismiss(); ShowBookmarksFor(item); }));
            // 文件夹操作：重命名所在文件夹 / 移动到其它文件夹
            if (!string.IsNullOrEmpty(item.Folder))
                sheet.AddView(ActionRow(L10n.T("重命名文件夹"), ThText, () => { actionDlg?.Dismiss(); RenameFolderOf(item.Folder); }));
            sheet.AddView(ActionRow(L10n.T("移动到文件夹…"), ThText, () => { actionDlg?.Dismiss(); MoveBookToFolder(item); }));
            // 本地书可分享到频道：对端点卡片直接进它的阅读库
            if (item.Type == "local" && !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                sheet.AddView(ActionRow(L10n.T("分享到频道"), ThText, () => { actionDlg?.Dismiss(); ShareBook(item); }));
            sheet.AddView(ActionRow(L10n.T("删除"), unchecked((int)0xFFE0564B), () => { actionDlg?.Dismiss(); ConfirmDelete(item); }));
            sheet.AddView(ActionRow(L10n.T("取消"), ThSub, () => actionDlg?.Dismiss()));

            actionDlg = BottomSheet(sheet, ViewGroup.LayoutParams.WrapContent);
            actionDlg.Show();
        }
        private Dialog actionDlg;

        /// <summary>把本地书分享到频道：对端点快捷卡片即加入它的阅读库。</summary>
        private void ShareBook(ShelfItem item)
        {
            try
            {
                string name = DisplayName(item);
                string ext = System.IO.Path.GetExtension(item.Path ?? "");
                if (ext.Length > 0) name += ext.ToLowerInvariant();   // 保留扩展名，对端才认得出类型
                MediaShare.ShareToChannel(this, name, item.Path);
            }
            catch (Exception ex)
            {
                ShowToast(L10n.T("分享失败：") + ex.Message);
            }
        }

        private View ActionRow(string text, int color, Action onClick)
        {
            var tv = new TextView(this) { Text = text, TextSize = 15.5f };
            tv.SetTextColor(new Color(color));
            tv.Gravity = GravityFlags.Center;
            tv.SetPadding(Dp(20), Dp(16), Dp(20), Dp(16));
            tv.Click += (s, e) => onClick();
            return tv;
        }

        private void RenameBook(ShelfItem item)
        {
            var input = new EditText(this) { Hint = L10n.T("书名") };
            string cur = DisplayName(item);
            input.Text = cur == L10n.T("未命名书籍") ? "" : cur;
            input.SetSingleLine(true);
            input.TextSize = 14;
            input.SetTextColor(new Color(TextMain));
            input.Background = RoundBg(ChipBg, 10);
            input.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(6), Dp(20), Dp(2));
            box.AddView(input);

            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("重命名"));
            b.SetView(box);
            b.SetPositiveButton(L10n.T("保存"), (s, e) =>
            {
                var name = (input.Text ?? "").Trim();
                if (name.Length == 0) { ShowToast(L10n.T("书名不能为空")); return; }
                ReaderLibrary.Rename(item.Id, name);
                Book bk;
                if (_openBooks.TryGetValue(item.Id, out bk) && bk != null) bk.Name = name;
                _shelf = ReaderLibrary.Shelf();
                RenderShelf();
                if (_curBook != null && _curId == item.Id)
                {
                    _curBook.Name = name;
                    _chapterTitle.Text = _curBook.Chapters.Count > _ci ? _curBook.Chapters[_ci].Title : name;
                }
                ShowToast(L10n.T("已重命名"));
            });
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        private void ConfirmDelete(ShelfItem item)
        {
            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("删除书籍"));
            b.SetMessage(L10n.T("确定从书架移除「") + item.Name + L10n.T("」吗？") +
                         (item.Type == "local" ? "\n（本地文件副本也会删除）" : ""));
            b.SetPositiveButton(L10n.T("删除"), (s, e) =>
            {
                ReaderLibrary.Delete(item.Id);
                _openBooks.Remove(item.Id);
                _shelf = ReaderLibrary.Shelf();
                RenderShelf();
                ShowToast(L10n.T("已删除"));
            });
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        /* ---------------- 文件夹操作（长按书籍） ---------------- */

        private void RenameFolderOf(string oldName)
        {
            var input = new EditText(this) { Hint = L10n.T("文件夹名") };
            input.SetSingleLine(true);
            input.Text = oldName;
            input.TextSize = 14;
            input.SetTextColor(new Color(TextMain));
            input.Background = RoundBg(ChipBg, 10);
            input.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(6), Dp(20), Dp(2));
            box.AddView(input);
            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("重命名文件夹"));
            b.SetView(box);
            b.SetPositiveButton(L10n.T("保存"), (s, e) =>
            {
                var nm = (input.Text ?? "").Trim();
                if (nm.Length == 0) { ShowToast(L10n.T("名称不能为空")); return; }
                ReaderLibrary.RenameFolder(oldName, nm);
                _shelf = ReaderLibrary.Shelf();
                RenderShelf();
                ShowToast(L10n.T("已重命名文件夹"));
            });
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        private void MoveBookToFolder(ShelfItem item)
        {
            var folders = ReaderLibrary.FolderList();
            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            sheet.AddView(ActionRow(L10n.T("移到顶层"), ThText, () =>
            {
                actionDlg?.Dismiss();
                ReaderLibrary.MoveToFolder(item.Id, "");
                _shelf = ReaderLibrary.Shelf();
                RenderShelf();
            }));
            sheet.AddView(ActionRow(L10n.T("新建文件夹…"), ThText, () =>
            {
                actionDlg?.Dismiss();
                NewFolderAndMove(item);
            }));
            foreach (var f in folders)
            {
                if (f == item.Folder) continue;
                var ff = f;
                sheet.AddView(ActionRow(f, ThText, () =>
                {
                    actionDlg?.Dismiss();
                    ReaderLibrary.MoveToFolder(item.Id, ff);
                    _shelf = ReaderLibrary.Shelf();
                    RenderShelf();
                }));
            }
            actionDlg = BottomSheet(sheet, ViewGroup.LayoutParams.WrapContent);
            actionDlg.Show();
        }

        private void NewFolderAndMove(ShelfItem item)
        {
            var input = new EditText(this) { Hint = L10n.T("文件夹名") };
            input.SetSingleLine(true);
            input.TextSize = 14;
            input.SetTextColor(new Color(TextMain));
            input.Background = RoundBg(ChipBg, 10);
            input.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(6), Dp(20), Dp(2));
            box.AddView(input);
            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("新建文件夹并移入"));
            b.SetView(box);
            b.SetPositiveButton(L10n.T("确定"), (s, e) =>
            {
                var nm = (input.Text ?? "").Trim();
                if (nm.Length == 0) { ShowToast(L10n.T("名称不能为空")); return; }
                ReaderLibrary.MoveToFolder(item.Id, nm);
                _shelf = ReaderLibrary.Shelf();
                RenderShelf();
            });
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        /// <summary>文件夹长按菜单：重命名文件夹 / 解散分组（仅改展示，不动文件）。</summary>
        private void FolderActions(string folderName)
        {
            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            sheet.AddView(ActionRow(L10n.T("重命名文件夹"), ThText, () =>
            {
                actionDlg?.Dismiss();
                RenameFolderOf(folderName);
            }));
            sheet.AddView(ActionRow(L10n.T("解散分组"), ThText, () =>
            {
                actionDlg?.Dismiss();
                ReaderLibrary.DissolveFolder(folderName);   // 把组内书移回顶层
                _shelf = ReaderLibrary.Shelf();
                RenderShelf();
                ShowToast(L10n.T("已解散分组"));
            }));
            sheet.AddView(ActionRow(L10n.T("取消"), ThSub, () => actionDlg?.Dismiss()));

            actionDlg = BottomSheet(sheet, ViewGroup.LayoutParams.WrapContent);
            actionDlg.Show();
        }

        /* ---------------- 打开书籍 ---------------- */
        private void OpenBook(string id) { _ = OpenBookInternal(id, -1, -1); }

        private async Task OpenBookInternal(string id, int chapter, double pct)
        {
            // async 方法在第一个 await 之前的异常会同步抛给调用方（OpenBook / OpenBookAt
            // 都是直接调用、没有保护），await 之后的异常则变成未观察异常 —— 两种都会让
            // 界面永久停在「正在打开…」或者直接崩溃。统一在这里兜住。
            try
            {
                _shelf = ReaderLibrary.Shelf();
                var item = _shelf.Find(x => x.Id == id);
                if (item == null) return;

                ShowToast(L10n.T("正在打开…"));
                Book book;
                if (!_openBooks.TryGetValue(id, out book) || book == null)
                {
                    // 本地书要整本读盘 + 解析章节（大 TXT 可能几百毫秒），放后台跑，别卡界面
                    string err = null;
                    var target = item;
                    book = await Task.Run(() =>
                    {
                        string e;
                        var b = ReaderLibrary.GetBook(target, out e);
                        err = e;
                        return b;
                    });
                    if (book == null) { ShowToast(err ?? L10n.T("打开失败")); return; }
                }

                if (book.Type == "online" && (book.Chapters == null || book.Chapters.Count == 0))
                {
                    var err = await ReaderLibrary.EnsureOnlineAsync(item, book);
                    if (err != null) { ShowToast(err); return; }
                }
                if (book.Chapters == null || book.Chapters.Count == 0) { ShowToast(L10n.T("无章节")); return; }

                _openBooks[id] = book;
                _curBook = book;
                _curId = id;
                _pdfPath = (item.Type == "local" && !string.IsNullOrEmpty(item.Path)
                            && ReaderLibrary.FormatOf(item) == "pdf") ? item.Path : null;
                _pdfBucket = 100;
                if (_pdfScroll != null) _pdfScroll.Release();

                if (chapter >= 0)
                {
                    _ci = Math.Max(0, Math.Min(chapter, book.Chapters.Count - 1));
                    _pct = Math.Max(0, Math.Min(1, pct));
                }
                else
                {
                    _ci = Math.Max(0, Math.Min(item.LastChapter, book.Chapters.Count - 1));
                    _pct = Math.Max(0, Math.Min(1, item.Pct));
                }

                ShowReader();
                await ShowChapter(_ci, _pct, false);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Reader", "打开书籍失败: " + ex.Message);
                try { ShowToast(L10n.T("打开失败：") + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 收到书籍直投：在书架中按书名（入库时一致）定位已导入的书，打开即读；
        /// 若尚未入库则兜底直接导入再打开。避免重复入库产生多本同名书。
        /// </summary>
        private void AutoOpenBookPath(string path, string name)
        {
            try
            {
                _shelf = ReaderLibrary.Shelf();
                string nm = ReaderLibrary.NormalizeName(name);
                var item = _shelf.Find(x => x.Name == nm)
                        ?? _shelf.Find(x => x.Name == name);
                if (item == null)
                {
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
                    item = _shelf.Find(x => System.IO.Path.GetFileNameWithoutExtension(x.Path ?? "") == baseName);
                }
                if (item == null)
                {
                    string err;
                    var it = ReaderLibrary.Import(name, File.ReadAllBytes(path), out err);
                    if (it == null) { ShowToast(err ?? L10n.T("打开失败")); return; }
                    item = it;
                }
                _ = OpenBookInternal(item.Id, -1, -1);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Reader", "AutoOpenBookPath 失败: " + ex.Message);
            }
        }

        /* ============================================================
         *  阅读页
         * ============================================================ */
        private View BuildReaderPage()
        {
            _readerPage = new FrameLayout(this);
            _readerPage.SetBackgroundColor(new Color(PageBg));
            _readerPage.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            // —— 顶栏 ——
            _topBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _topBar.SetGravity(GravityFlags.CenterVertical);
            _topBar.SetPadding(Dp(4), 0, Dp(6), 0);
            _topBar.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(50));

            _btnBack = BarText("‹", 28, v => BackToShelf());
            _btnBack.SetPadding(Dp(6), 0, Dp(10), 0);
            _btnBack.Gravity = GravityFlags.Center;
            _topBar.AddView(_btnBack, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));

            _chapterTitle = new TextView(this) { Text = "", TextSize = 14.5f };
            _chapterTitle.SetMaxLines(1);
            _chapterTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _chapterTitle.SetPadding(Dp(2), 0, Dp(8), 0);
            _chapterTitle.Gravity = GravityFlags.CenterVertical;
            _topBar.AddView(_chapterTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

            _btnToc = BarText(L10n.T("目录"), 13, v => ShowToc());
            _topBar.AddView(_btnToc, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
            _btnMark = BarText(L10n.T("书签"), 13, v => ShowBookmarks());
            _topBar.AddView(_btnMark, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
            _btnAa = BarText("Aa", 13, v => ShowSettings());
            _topBar.AddView(_btnAa, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));

            // 顶栏先构造成浮层容器，正文加完后再挂到页面上（保证压在正文之上）
            _topLine = new View(this);
            _topLine.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);

            _topChrome = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var topLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            topLp.Gravity = GravityFlags.Top;
            _topChrome.LayoutParameters = topLp;
            _topChrome.Clickable = true;   // 空白处也吃掉点击，避免穿透到正文触发翻页
            _topChrome.AddView(_topBar);
            _topChrome.AddView(_topLine);

            // —— 正文容器：文本书走 WebView，PDF 走「连续纵向拼页」的 PdfContinuousView ——
            // 始终铺满整个阅读页：菜单只是浮层，不会改变这里的尺寸。
            _bodyHost = new FrameLayout(this);
            _bodyHost.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            _web = new WebView(this);
            _web.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            _web.Settings.JavaScriptEnabled = false;
            _web.Settings.JavaScriptEnabled = true;   // 仅用于「连读」追加章节与量测章节锚点
            _web.Settings.DefaultFontSize = _prefs.FontSize;
            _web.VerticalScrollBarEnabled = false;
            _web.OverScrollMode = OverScrollMode.Never;
            _web.SetWebViewClient(new ReaderClient(this));
            _web.SetOnTouchListener(new ReaderTouch(this));
            _web.ScrollChange += (s, e) =>
            {
                if (_restoring) return;
                OnWebScrolled(e.ScrollY);
            };
            _bodyHost.AddView(_web);

            _pdfScroll = new AhoChatA.Reader.PdfContinuousView(this);
            _pdfScroll.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            _pdfScroll.Visibility = ViewStates.Gone;
            _pdfScroll.Tap = OnPdfTap;
            _pdfScroll.PageChanged = OnPdfPageChanged;
            _bodyHost.AddView(_pdfScroll);

            _readerPage.AddView(_bodyHost);

            // —— 音乐迷你条 ——
            // 只在「简易菜单」（顶栏 / 底栏）唤起时出现：正在听歌时随手能切歌、看歌词，
            // 收起菜单后完全让位给正文，不干扰阅读。
            _musicBar = BuildMusicBar();
            _musicBar.Visibility = ViewStates.Gone;

            // —— 底栏 ——
            _botLine = new View(this);
            _botLine.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);

            // 系统导航栏有自己的区域，App 内容不会被它遮挡，这里不再额外预留高度。
            _botBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _botBar.SetGravity(GravityFlags.CenterVertical);
            _botBar.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(52));

            _btnPrev = BarText(L10n.T("上一章"), 13.5f, v => PrevChapter());
            _btnPrev.Gravity = GravityFlags.Center;
            _botBar.AddView(_btnPrev, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

            _progText = new TextView(this) { Text = "", TextSize = 11 };
            _progText.Gravity = GravityFlags.Center;
            _progText.SetPadding(Dp(8), 0, Dp(8), 0);
            _botBar.AddView(_progText, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));

            _btnNext = BarText(L10n.T("下一章"), 13.5f, v => NextChapter());
            _btnNext.Gravity = GravityFlags.Center;
            _botBar.AddView(_btnNext, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

            // —— 底部浮层：音乐条 + 分隔线 + 底栏，整体压在正文之上 ——
            _botChrome = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var botLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            botLp.Gravity = GravityFlags.Bottom;
            _botChrome.LayoutParameters = botLp;
            _botChrome.Clickable = true;
            _botChrome.AddView(_musicBar);
            _botChrome.AddView(_botLine);
            _botChrome.AddView(_botBar);

            // 先加正文再加两栏浮层，保证浮层在最上层
            _readerPage.AddView(_topChrome);
            _readerPage.AddView(_botChrome);

            return _readerPage;
        }

        /* ============================================================
         *  阅读页音乐迷你条
         *
         *  只在「简易菜单」（顶栏 / 底栏）唤起时出现，正在听歌时随手可切歌、看歌词；
         *  收起菜单就彻底让位给正文，不干扰阅读。
         *  交互：封面 / 歌名 → 影音曲库页（顶部带「搜索在线音乐」）；
         *        歌词 → 播放详情页；列表键 → 待播放列表浮层（任选切歌）。
         *  从阅读页进入音乐界面走普通 StartActivity，返回键自然回到阅读页继续读。
         * ============================================================ */

        private LinearLayout BuildMusicBar()
        {
            var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            wrap.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

            _musicLine = new View(this);
            _musicLine.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            wrap.AddView(_musicLine);

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(12), Dp(6), Dp(4), Dp(6));

            _mbCover = new ImageView(this);
            _mbCover.SetScaleType(ImageView.ScaleType.CenterCrop);
            try
            {
                _mbCover.ClipToOutline = true;
                _mbCover.OutlineProvider = new RoundOutline(Dp(7));
            }
            catch { }
            var coverLp = new LinearLayout.LayoutParams(Dp(42), Dp(42));
            coverLp.RightMargin = Dp(10);
            row.AddView(_mbCover, coverLp);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetGravity(GravityFlags.CenterVertical);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            _mbTitle = new TextView(this) { Text = "", TextSize = 13f };
            _mbTitle.SetMaxLines(1);
            _mbTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            col.AddView(_mbTitle);

            _mbLyric = new TextView(this) { Text = "", TextSize = 11.5f };
            _mbLyric.SetMaxLines(1);
            _mbLyric.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _mbLyric.SetPadding(0, Dp(2), 0, 0);
            col.AddView(_mbLyric);

            row.AddView(col);

            // 覆盖层：封面 / 歌名 → 曲库页；歌词 → 播放详情页
            _mbCover.Click += (s, e) => OpenMusicShow(MusicActivity.EntryLibrary);
            _mbTitle.Click += (s, e) => OpenMusicShow(MusicActivity.EntryLibrary);
            _mbLyric.Click += (s, e) => OpenMusicDetail();

            _mbPrev = NewBarIcon(IconKind.Prev);
            row.AddView(IconBox(_mbPrev, 38, () => { try { MusicPlayer.Prev(); } catch { } }));

            _mbToggle = NewBarIcon(IconKind.Play);
            row.AddView(IconBox(_mbToggle, 42, () => { try { MusicPlayer.Toggle(); } catch { } }));

            _mbNext = NewBarIcon(IconKind.Next);
            row.AddView(IconBox(_mbNext, 38, () => { try { MusicPlayer.Next(); } catch { } }));

            _mbQueueBtn = NewBarIcon(IconKind.Queue);
            row.AddView(IconBox(_mbQueueBtn, 40, ShowQueueSheet));

            wrap.AddView(row);

            // 底部 2dp 播放进度：ScaleX 从 0→1，避免每帧改布局
            _mbProg = new View(this);
            _mbProg.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2));
            _mbProg.PivotX = 0;
            _mbProg.ScaleX = 0f;
            wrap.AddView(_mbProg);

            return wrap;
        }

        private IconView NewBarIcon(IconKind kind)
        {
            var ic = new IconView(this, kind) { IconColor = ThText, Thickness = 1.8f };
            return ic;
        }

        private FrameLayout IconBox(IconView ic, int sizeDp, Action click)
        {
            var box = new FrameLayout(this);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(sizeDp), Dp(sizeDp));
            var ilp = new FrameLayout.LayoutParams(Dp(21), Dp(21));
            ilp.Gravity = GravityFlags.Center;
            ic.LayoutParameters = ilp;
            box.AddView(ic);
            box.Click += (s, e) => { try { click(); } catch { } };
            return box;
        }

        /// <summary>封面圆角裁剪用（API21+ 的 outline 裁剪，不用额外建位图）。</summary>
        private sealed class RoundOutline : ViewOutlineProvider
        {
            private readonly int _r;
            public RoundOutline(int r) { _r = r; }
            public override void GetOutline(View view, Android.Graphics.Outline outline)
            {
                try { outline.SetRoundRect(0, 0, view.Width, view.Height, _r); } catch { }
            }
        }

        private static string MbSongKey(Song s)
            => s == null ? "" : ((s.Path ?? "") + "|" + (s.Title ?? "") + "|" + (s.OnlineId ?? ""));

        /// <summary>按当前播放状态整条刷新（换曲时重载封面 / 歌词）。</summary>
        private void UpdateMusicBar()
        {
            if (_musicBar == null) return;
            SyncMusicBarVisibility();   // 先定可见性，否则下面的进度/歌词刷新会被跳过
            var s = MusicPlayer.Current;
            if (s == null)
            {
                _mbSongKey = "";
                _mbLyrics = null;
                _mbLyricIdx = -2;
                return;
            }

            string key = MbSongKey(s);
            if (key != _mbSongKey)
            {
                _mbSongKey = key;
                _mbTitle.Text = s.DisplayTitle;
                _mbLyricIdx = -2;
                LoadMusicBarLyrics(s);
                LoadMusicBarCover(s);
            }

            try
            {
                _mbToggle.Kind = MusicPlayer.IsPlaying ? IconKind.Pause : IconKind.Play;
                _mbToggle.IconColor = ThText;
                _mbToggle.Invalidate();
                _mbPrev.IconColor = ThText; _mbPrev.Invalidate();
                _mbNext.IconColor = ThText; _mbNext.Invalidate();
                _mbQueueBtn.IconColor = MusicPlayer.Queue.Count > 0 ? Accent : ThSub;
                _mbQueueBtn.Invalidate();
            }
            catch { }

            OnMusicTick(MusicPlayer.Position);
        }

        /// <summary>
        /// 迷你条可见性：简易菜单展开 + 停在阅读页 + 有可显示的曲目。
        /// 没在播放时也会自动挑一首「待播」曲目，保证这条卡片常驻（点播放键才真正起播）。
        /// </summary>
        private void SyncMusicBarVisibility()
        {
            if (_musicBar == null) return;
            bool show = !_chromeHidden
                        && _readerPage != null && _readerPage.Visibility == ViewStates.Visible;
            if (show && MusicPlayer.Current == null)
            {
                MusicPlayer.EnsurePending();
            }
            show = show && MusicPlayer.Current != null;
            var v = show ? ViewStates.Visible : ViewStates.Gone;
            if (_musicBar.Visibility != v)
            {
                _musicBar.Visibility = v;
                // 收起期间跳过了进度更新，重新出现时立刻对齐到当前歌词行（不必等下一次 500ms 回报）
                if (v == ViewStates.Visible)
                {
                    _mbLyricIdx = -2;
                    OnMusicTick(MusicPlayer.Position);
                }
            }
        }

        private void LoadMusicBarCover(Song s)
        {
            try
            {
                var bmp = MusicLibrary.LoadCover(s, 128);
                if (bmp != null && !bmp.IsRecycled)
                {
                    _mbCover.SetImageBitmap(bmp);
                    _mbCover.SetScaleType(ImageView.ScaleType.CenterCrop);
                    return;
                }
            }
            catch { }

            var url = SongArt.Url(s);
            var hit = SongArt.Cached(url);
            if (hit != null && !hit.IsRecycled)
            {
                _mbCover.SetImageBitmap(hit);
                _mbCover.SetScaleType(ImageView.ScaleType.CenterCrop);
                return;
            }
            try
            {
                var ph = SongArt.Placeholder(true);
                if (ph != null) _mbCover.SetImageBitmap(ph);
            }
            catch { }
            if (!string.IsNullOrEmpty(url)) SongArt.LoadInto(_mbCover, s, 128);
        }

        private void LoadMusicBarLyrics(Song s)
        {
            try { _mbLyrics = MusicLibrary.LoadLyrics(s); } catch { _mbLyrics = null; }
            bool has = _mbLyrics != null && _mbLyrics.IsValid;
            _mbLyric.Text = has ? "" : (s.IsOnline ? L10n.T("歌词加载中…") : L10n.T("暂无歌词"));
            // 在线歌曲本地没词就异步取一次，成功后落成 .lrc，下次直接命中
            if (!has && s.IsOnline) FetchMusicBarLyrics(s);
        }

        private void FetchMusicBarLyrics(Song s)
        {
            if (s == null || !s.IsOnline) return;
            if (!string.IsNullOrEmpty(s.LrcPath) && File.Exists(s.LrcPath)) return;
            var captured = s;
            Task.Run(async () =>
            {
                try { await OnlineMusic.FetchLyricsAsync(captured); } catch { }
                RunOnUiThread(() =>
                {
                    try
                    {
                        if (!ReferenceEquals(MusicPlayer.Current, captured)) return;
                        _mbLyrics = MusicLibrary.LoadLyrics(captured);
                        _mbLyricIdx = -2;
                        _mbLyric.Text = (_mbLyrics != null && _mbLyrics.IsValid) ? "" : L10n.T("暂无歌词");
                        OnMusicTick(MusicPlayer.Position);
                    }
                    catch { }
                });
            });
        }

        /// <summary>约 500ms 一次的进度回报：更新歌词行与底部进度线。</summary>
        private void OnMusicTick(int pos)
        {
            if (_musicBar == null || _musicBar.Visibility != ViewStates.Visible) return;
            try
            {
                int dur = MusicPlayer.Duration;
                _mbProg.ScaleX = dur > 0 ? Math.Max(0f, Math.Min(1f, pos / (float)dur)) : 0f;

                if (_mbLyrics == null || !_mbLyrics.IsValid) return;
                int idx = _mbLyrics.LineAt((long)pos + 300);
                if (idx == _mbLyricIdx) return;
                _mbLyricIdx = idx;
                var text = idx >= 0 && idx < _mbLyrics.Lines.Count ? _mbLyrics.Lines[idx].Text : "";
                if (!string.IsNullOrWhiteSpace(text)) _mbLyric.Text = text;
            }
            catch { }
        }

        /* ---------------- 音乐条事件绑定 ---------------- */

        private void BindMusicEvents()
        {
            if (_mbEventsOn) return;
            _mbEventsOn = true;
            MusicPlayer.StateChanged += OnMusicStateChanged;
            MusicPlayer.SongChanged += OnMusicSongChanged;
            MusicPlayer.PositionTick += OnMusicTick;
        }

        private void UnbindMusicEvents()
        {
            if (!_mbEventsOn) return;
            _mbEventsOn = false;
            MusicPlayer.StateChanged -= OnMusicStateChanged;
            MusicPlayer.SongChanged -= OnMusicSongChanged;
            MusicPlayer.PositionTick -= OnMusicTick;
        }

        private void OnMusicStateChanged()
        {
            RunOnUiThread(() => { try { UpdateMusicBar(); } catch { } });
        }

        private void OnMusicSongChanged()
        {
            RunOnUiThread(() => { try { UpdateMusicBar(); } catch { } });
        }

        /* ---------------- 跳到音乐界面 ---------------- */

        private void OpenMusicDetail()
        {
            if (MusicPlayer.Current == null) { OpenMusicShow(MusicActivity.EntryLibrary); return; }
            OpenMusicShow(MusicActivity.EntryPlayer);
        }

        /// <summary>
        /// 打开影音模块。用普通 StartActivity 压栈，阅读页仍在栈里，
        /// 所以音乐界面按一次返回键就回到原书原位置继续阅读。
        /// </summary>
        private void OpenMusicShow(string show)
        {
            try
            {
                SaveCurrentProgress();
                var i = new Intent(this, typeof(MusicActivity));
                i.PutExtra(MusicActivity.ExtraShow, show);
                StartActivity(i);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Reader", "打开音乐界面失败: " + ex.Message);
                try { L10n.Toast(this, L10n.T("打开音乐界面失败"), ToastLength.Short).Show(); } catch { }
            }
        }

        /* ---------------- 待播放列表浮层 ---------------- */

        private void ShowQueueSheet()
        {
            var q = MusicPlayer.Queue;
            if (q == null || q.Count == 0)
            {
                try { L10n.Toast(this, L10n.T("播放列表为空"), ToastLength.Short).Show(); } catch { }
                return;
            }

            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(20), Dp(2), Dp(14), Dp(4));

            var t = new TextView(this) { Text = L10n.T("待播放列表"), TextSize = 16 };
            t.SetTextColor(new Color(ThText));
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            // 播放模式：点一下循环切换（顺序 / 列表 / 单曲 / 随机）
            var mode = new TextView(this) { Text = MusicPlayer.ModeName, TextSize = 12 };
            mode.SetTextColor(new Color(Accent));
            mode.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
            mode.Background = RoundBg(ThChip, 12);
            head.AddView(mode);

            var cnt = new TextView(this) { Text = "  " + q.Count + L10n.T(" 首"), TextSize = 12 };
            cnt.SetTextColor(new Color(ThSub));
            head.AddView(cnt);
            sheet.AddView(head);

            var tip = new TextView(this) { Text = L10n.T("点任意一首直接切换播放"), TextSize = 11 };
            tip.SetTextColor(new Color(ThSub));
            tip.SetPadding(Dp(20), 0, Dp(20), Dp(6));
            sheet.AddView(tip);

            var lv = new ListView(this);
            lv.Divider = null;
            lv.DividerHeight = 0;
            lv.VerticalScrollBarEnabled = false;
            var adapter = new QueueSheetAdapter(this);
            lv.Adapter = adapter;

            Dialog dlg = null;
            lv.ItemClick += (s, e) =>
            {
                int qi = adapter.QueueIndexOf(e.Position);
                if (qi < 0) return;
                try { MusicPlayer.PlayAt(qi); } catch { }
                adapter.Rebuild();
                try { if (dlg != null) dlg.Dismiss(); } catch { }
            };

            mode.Click += (s, e) =>
            {
                try { MusicPlayer.CycleMode(); } catch { }
                mode.Text = MusicPlayer.ModeName;
                adapter.Rebuild();
            };

            sheet.AddView(lv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            dlg = BottomSheet(sheet, Dp(430));
            try { dlg.Show(); } catch { }
        }

        /// <summary>待播放列表按「播放顺序」排列（随机模式下就是打乱后的顺序），点行切歌。</summary>
        private sealed class QueueSheetAdapter : BaseAdapter
        {
            private readonly ReaderActivity _a;
            private readonly List<int> _view = new List<int>();   // 显示位置 → Queue 索引

            public QueueSheetAdapter(ReaderActivity a) { _a = a; Rebuild(); }

            public void Rebuild()
            {
                _view.Clear();
                var order = MusicPlayer.Order;
                if (order != null && order.Count > 0)
                {
                    foreach (var qi in order)
                        if (qi >= 0 && qi < MusicPlayer.Queue.Count) _view.Add(qi);
                }
                else
                {
                    for (int i = 0; i < MusicPlayer.Queue.Count; i++) _view.Add(i);
                }
                try { NotifyDataSetChanged(); } catch { }
            }

            public int QueueIndexOf(int position)
                => position >= 0 && position < _view.Count ? _view[position] : -1;

            public override int Count => _view.Count;
            public override Java.Lang.Object GetItem(int position) => null;
            public override long GetItemId(int position) => position;

            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                int qi = QueueIndexOf(position);
                var song = qi >= 0 && qi < MusicPlayer.Queue.Count ? MusicPlayer.Queue[qi] : null;
                bool cur = song != null && ReferenceEquals(MusicPlayer.Current, song);

                var row = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(_a.Dp(20), _a.Dp(11), _a.Dp(18), _a.Dp(11));
                if (cur) row.SetBackgroundColor(new Color(_a.CurChapterBg()));

                var num = new TextView(_a) { Text = cur ? "♪" : (position + 1).ToString(), TextSize = 11 };
                num.SetTextColor(new Color(cur ? Accent : _a.ThSub));
                num.Gravity = GravityFlags.Center;
                row.AddView(num, new LinearLayout.LayoutParams(_a.Dp(30), ViewGroup.LayoutParams.WrapContent));

                var col = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

                var title = new TextView(_a)
                {
                    Text = song != null ? song.DisplayTitle : "—",
                    TextSize = 13.5f,
                    Ellipsize = Android.Text.TextUtils.TruncateAt.End
                };
                title.SetMaxLines(1);
                title.SetTextColor(new Color(cur ? Accent : _a.ThText));
                if (cur) title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
                col.AddView(title);

                var sub = new TextView(_a) { Text = song != null ? song.ArtistLine : "", TextSize = 11 };
                sub.SetMaxLines(1);
                sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                sub.SetTextColor(new Color(_a.ThSub));
                col.AddView(sub);
                row.AddView(col);

                var dur = new TextView(_a) { Text = Song.FormatMs(song != null ? song.DurationMs : 0), TextSize = 11 };
                dur.SetTextColor(new Color(_a.ThSub));
                row.AddView(dur);

                return row;
            }
        }

        /// <summary>当前打开的是不是 PDF（图片逐页模式）。</summary>
        private bool IsPdfBook()
            => !string.IsNullOrEmpty(_pdfPath)
               || (_curBook != null && _curBook.Format == "pdf");

        private void ShowReader()
        {
            // 开始读正文：把预缓存的带宽让出来，先把当前这一章拿快
            try { ReaderLibrary.CancelPrefetch(); } catch { }
            _shelfPage.Visibility = ViewStates.Gone;
            _readerPage.Visibility = ViewStates.Visible;
            bool pdf = IsPdfBook();
            _web.Visibility = pdf ? ViewStates.Gone : ViewStates.Visible;
            _pdfScroll.Visibility = pdf ? ViewStates.Visible : ViewStates.Gone;
            _chromeHidden = false;
            _topChrome.Visibility = ViewStates.Visible;
            _botChrome.Visibility = ViewStates.Visible;
            ApplyChrome();
            UpdateMusicBar();
        }

        public override void OnBackPressed()
        {
            if (_readerPage != null && _readerPage.Visibility == ViewStates.Visible)
            {
                BackToShelf();
                return;
            }
            if (_explorePage != null && _explorePage.Visibility == ViewStates.Visible)
            {
                CloseExplore();
                return;
            }
            base.OnBackPressed();
        }

        private void BackToShelf()
        {
            if (_ttsOn) StopTts();
            SaveCurrentProgress();
            SetChromeVisible(true);
            _pdfSeq++;                     // 作废还在途中的页图异步回调
            _pdfScroll?.Release();         // 立刻回收所有页图位图
            _pdfPath = null;
            _pdfScroll.Visibility = ViewStates.Gone;
            _web.Visibility = ViewStates.Visible;
            _readerPage.Visibility = ViewStates.Gone;
            _shelfPage.Visibility = ViewStates.Visible;
            _shelf = ReaderLibrary.Shelf();
            RenderShelf();
            // 刚存过进度：按新的阅读位置继续把后面 50 章缓存好，下次点开是秒开
            ReaderLibrary.PrefetchAhead(50);
        }

        private async Task ShowChapter(int idx, double restorePct = 0, bool save = true)
        {
            if (_curBook == null || _curBook.Chapters.Count == 0) return;
            if (IsPdfBook()) { ShowPdfPage(idx, save); return; }
            idx = Math.Max(0, Math.Min(idx, _curBook.Chapters.Count - 1));
            _ci = idx;
            _pct = Math.Max(0, Math.Min(1, restorePct));

            var ch = _curBook.Chapters[idx];
            _chapterTitle.Text = ch.Title;
            UpdateProgressText();

            string body;
            if (ch.Content != null) body = ch.Content;
            else
            {
                RenderLoading();
                body = await ReaderLibrary.ChapterTextAsync(
                    new ShelfItem { Id = _curId, Type = _curBook.Type, SourceUrl = _curBook.SourceUrl }, _curBook, idx);
                ch.Content = body;
            }
            _pendingRestore = true;
            _restoring = true;          // 加载期间的 ScrollChange 不算进度
            RenderBody(ch);
            if (save) SaveCurrentProgress();
        }

        /* ---------------- PDF：连续纵向拼页，每页独立、间距为 0 ---------------- */

        /// <summary>
        /// 打开 / 跳转到 PDF 的某一页。页图由 PdfContinuousView 负责窗口化加载与回收，
        /// 命中 PdfCache 磁盘缓存即直接解码，不在翻页时反复打开 PDF 重渲染。
        /// </summary>
        private void ShowPdfPage(int idx, bool save, bool resetZoom = true)
        {
            if (_curBook == null || _curBook.Chapters.Count == 0 || _pdfScroll == null) return;
            idx = Math.Max(0, Math.Min(idx, _curBook.Chapters.Count - 1));
            _ci = idx;
            _pct = 0;                       // PDF 以「页」为最小进度单位，页内比例恒为 0
            _restoring = false;
            _chapterTitle.Text = _curBook.Chapters[idx].Title;
            UpdateProgressText();

            _web.Visibility = ViewStates.Gone;
            _pdfScroll.Visibility = ViewStates.Visible;

            if (!_pdfScroll.IsLoadedFor(_curId))
                _pdfScroll.Load(_pdfPath, _curId, _curBook.Chapters.Count, PageBg, idx);
            else
                _pdfScroll.ShowAtPage(idx);

            if (save) SaveCurrentProgress();
        }

        /// <summary>连续拼页模式下点击屏幕：呼出 / 收起工具栏（不再用左右点击翻页）。</summary>
        private void OnPdfTap()
        {
            if (_chromeHidden) SetChromeVisible(true);
            else SetChromeVisible(false);
        }

        /// <summary>滚动导致最顶部可见页变化时同步进度、标题与底部计数。</summary>
        private void OnPdfPageChanged(int pg)
        {
            if (!IsPdfBook() || _curBook == null) return;
            _ci = pg;
            if (pg >= 0 && pg < _curBook.Chapters.Count)
                _chapterTitle.Text = _curBook.Chapters[pg].Title;
            UpdateProgressText();
            QueueProgressSave();
        }

        private void UpdateProgressText()
        {
            if (_curBook == null || _curBook.Chapters.Count == 0) { _progText.Text = ""; return; }
            bool pdf = IsPdfBook();
            _btnPrev.Text = pdf ? L10n.T("上一页") : L10n.T("上一章");
            _btnNext.Text = pdf ? L10n.T("下一页") : L10n.T("下一章");
            _progText.Text = (_ci + 1) + " / " + _curBook.Chapters.Count;
            bool hasPrev = _ci > 0, hasNext = _ci < _curBook.Chapters.Count - 1;
            _btnPrev.Alpha = hasPrev ? 1f : 0.32f;
            _btnNext.Alpha = hasNext ? 1f : 0.32f;
        }

        private void RenderLoading()
        {
            string html = "<!doctype html><html><head><meta charset=utf-8>"
                + "<meta name=viewport content='width=device-width,initial-scale=1'>"
                + "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;"
                + "background:" + BgHex() + ";color:" + SubHex() + ";font-size:13px;"
                + "font-family:-apple-system,'Noto Sans CJK SC',sans-serif}</style></head>"
                + L10n.T("<body>正在加载…</body></html>");
            _web.LoadDataWithBaseURL(null, html, "text/html", "utf-8", null);
        }

        /// <summary>正文样式（跟随排版偏好），连读拼接时复用同一份 CSS。</summary>
        private string BodyCss()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<style>");
            sb.Append("html,body{margin:0;padding:0;background:").Append(BgHex()).Append("}");
            sb.Append("body{padding:14px ").Append(_prefs.Margin).Append("px 46px;font-size:")
              .Append(_prefs.FontSize).Append("px;");
            sb.Append("line-height:").Append(_prefs.LineHeight.ToString("0.00")).Append(";");
            sb.Append("color:").Append(FgHex()).Append(";");
            sb.Append("text-align:").Append(_prefs.Justify ? "justify" : "left").Append(";");
            sb.Append("text-justify:inter-ideograph;letter-spacing:.02em;");
            sb.Append("-webkit-text-size-adjust:100%;overflow-wrap:break-word;");
            string fam;
            switch (_prefs.Font)
            {
                case "sans": fam = "'Noto Sans CJK SC','Source Han Sans SC','PingFang SC',sans-serif"; break;
                case "system": fam = "-apple-system,system-ui,'Noto Sans CJK SC',sans-serif"; break;
                default: fam = "'Noto Serif CJK SC','Source Han Serif SC','Songti SC',serif"; break;
            }
            sb.Append("font-family:").Append(fam).Append(";");
            if (_prefs.Bold) sb.Append("font-weight:600;");
            sb.Append("p{margin:0 0 ").Append(_prefs.ParaSpace).Append("px;text-indent:")
              .Append(_prefs.Indent).Append("em}");
            sb.Append(".hd{margin:2px 0 22px;text-align:center;text-indent:0}");
            sb.Append(".hd .t{font-size:1.04em;letter-spacing:.10em;color:").Append(SubHex()).Append("}");
            sb.Append(".hd .l{margin:13px auto 0;width:44px;height:1px;background:").Append(LineHex()).Append("}");
            sb.Append(".empty{padding:40px 0;text-align:center;text-indent:0;color:").Append(SubHex()).Append(";font-size:.85em}");
            sb.Append("#tail .hd{margin-top:10px}");
            sb.Append("img{max-width:100%;height:auto}</style>");
            return sb.ToString();
        }

        /// <summary>单章正文 HTML（章名 + 段落），不含文档外壳；连读时逐章追加。</summary>
        private string ChapterHtml(Chapter ch, int idx)
        {
            var paras = (ch?.Content ?? "").Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.Append("<div class=ch data-i='").Append(idx).Append("'>");

            int first = -1;
            for (int i = 0; i < paras.Length; i++)
                if (!string.IsNullOrWhiteSpace(paras[i])) { first = i; break; }

            string firstText = first >= 0 ? paras[first].Trim() : "";
            bool titleInBody = firstText.Length > 0 && !string.IsNullOrEmpty(ch?.Title)
                               && firstText.Replace(" ", "").StartsWith(ch.Title.Replace(" ", ""));
            if (!titleInBody && !string.IsNullOrEmpty(ch?.Title))
            {
                sb.Append("<div class=hd><div class=t>").Append(Encode(ch.Title)).Append("</div>")
                  .Append("<div class=l></div></div>");
            }

            int count = 0;
            foreach (var raw in paras)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string p = raw.Trim(' ', '\t', '\u3000', '\r');
                if (p.Length == 0) continue;
                sb.Append("<p>").Append(Encode(p)).Append("</p>");
                count++;
            }
            if (count == 0)
                sb.Append(L10n.T("<p class=empty>本章暂无正文<br>可能是付费章节或书源规则需要调整</p>"));

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>渲染正文：衬线字体、首行缩进、可调行距 / 页边距，章名单独成行加细分隔线。</summary>
        private void RenderBody(Chapter ch)
        {
            _docChaps.Clear();
            _anchors.Clear();
            _appendStop = false;

            var sb = new System.Text.StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=utf-8>");
            sb.Append("<meta name=viewport content='width=device-width,initial-scale=1,maximum-scale=1'>");
            sb.Append(BodyCss());
            sb.Append("</head><body>");
            sb.Append(ChapterHtml(ch, _ci));
            sb.Append("<div id=tail></div>");
            sb.Append("</body></html>");

            _docChaps.Add(_ci);
            _web.LoadDataWithBaseURL(null, sb.ToString(), "text/html", "utf-8", null);
        }

        /* ---------------- 连读：本章到底自动拼接下一章 ---------------- */

        /// <summary>正文可滚动高度（像素）。</summary>
        private int ScrollMax()
        {
            try
            {
                int content = (int)(_web.ContentHeight * _web.Scale);
                return Math.Max(0, content - _web.Height);
            }
            catch { return 0; }
        }

        private int AnchorPx(int pos)
        {
            if (pos < 0 || pos >= _anchors.Count) return 0;
            int max = ScrollMax();
            return (int)Math.Round(_anchors[pos] * max);
        }

        /// <summary>滚动回调：定位当前章节、更新进度、并判断是否要续下一章。</summary>
        private void OnWebScrolled(int y)
        {
            if (_curBook == null || _curBook.Chapters.Count == 0) return;

            if (_anchors.Count > 0 && _anchors.Count == _docChaps.Count)
            {
                int max = ScrollMax();
                int tol = Math.Max(Dp(40), _web.Height / 14);   // 章名已顶到屏幕上方一点时也算新章
                int pos = 0;
                for (int i = 0; i < _anchors.Count; i++)
                    if (_anchors[i] * max <= y + tol) pos = i;

                int top = (int)Math.Round(_anchors[pos] * max);
                int nextTop = pos + 1 < _anchors.Count ? (int)Math.Round(_anchors[pos + 1] * max) : max;
                int span = Math.Max(1, nextTop - top);
                _pct = Math.Max(0, Math.Min(1, (double)(y - top) / span));

                int chap = _docChaps[pos];
                if (chap != _ci)
                {
                    _ci = chap;
                    _chapterTitle.Text = _curBook.Chapters[chap].Title;
                    UpdateProgressText();
                }
            }
            else
            {
                _pct = Math.Max(0, Math.Min(1, (double)y / Math.Max(1, ScrollMax())));
            }

            QueueProgressSave();
            EnsureAppend(y);
        }

        /// <summary>接近章末（剩余不足两屏）就预取下一章并追加到文档尾部。</summary>
        private void EnsureAppend(int y)
        {
            if (!_prefs.AutoAppend || _appending || _appendStop) return;
            int max = ScrollMax();
            if (max > 0 && max - y > _web.Height * 2) return;
            MaybeAppend();
        }

        private async void MaybeAppend()
        {
            if (!_prefs.AutoAppend || _appending || _appendStop) return;
            if (_curBook == null || _curBook.Chapters == null || _curBook.Chapters.Count == 0) return;
            // 一屏内最多续 12 章；到上限就从当前章重新开一份文档，避免超长 HTML 拖慢渲染
            if (_docChaps.Count >= 12)
            {
                if (_ci < _curBook.Chapters.Count - 1) NextOrPrev(_ci + 1, 0);
                else _appendStop = true;
                return;
            }

            int last = _docChaps.Count > 0 ? _docChaps[_docChaps.Count - 1] : _ci;
            int nxt = last + 1;
            if (nxt < 0 || nxt >= _curBook.Chapters.Count) return;
            if (_docChaps.Contains(nxt)) return;

            _appending = true;
            try
            {
                var ch = _curBook.Chapters[nxt];
                if (ch.Content == null)
                {
                    string body = await ReaderLibrary.ChapterTextAsync(
                        new ShelfItem { Id = _curId, Type = _curBook.Type, SourceUrl = _curBook.SourceUrl },
                        _curBook, nxt);
                    if (string.IsNullOrEmpty(body)) { _appendStop = true; return; }
                    ch.Content = body;
                }
                await AppendHtmlAsync(ChapterHtml(ch, nxt));
                _docChaps.Add(nxt);
                MeasureAnchors(null);
            }
            catch
            {
                _appendStop = true;   // 失败就停，别反复打网络
            }
            finally
            {
                _appending = false;
            }
        }

        private Task AppendHtmlAsync(string html)
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                string js = "(function(){var t=document.getElementById('tail');if(!t)return 'no';"
                          + "t.insertAdjacentHTML('beforeend'," + QuoteJs(html) + ");return 'ok';})()";
                _web.EvaluateJavascript(js, new JsCallback(v =>
                {
                    if (v != null && v.Contains("no")) _appendStop = true;
                    tcs.TrySetResult(true);
                }));
                // 兜底：WebView 偶尔不回调，1.2s 后照常继续
                _web.PostDelayed(() => tcs.TrySetResult(true), 1200);
            }
            catch { tcs.TrySetResult(false); }
            return tcs.Task;
        }

        /// <summary>量测各章起点（换算成「可滚动高度的比例」，规避 CSS px / 物理 px 单位差异）。</summary>
        private void MeasureAnchors(Action done)
        {
            try
            {
                string js = "(function(){var d=document.documentElement,"
                          + "max=Math.max(1,d.scrollHeight-window.innerHeight),"
                          + "l=document.querySelectorAll('.ch'),a=[];"
                          + "for(var i=0;i<l.length;i++){var r=l[i].getBoundingClientRect();"
                          + "a.push(((r.top+window.pageYOffset)/max).toFixed(6));}"
                          + "return a.join(',');})()";
                _web.EvaluateJavascript(js, new JsCallback(v =>
                {
                    _anchors.Clear();
                    string raw = (v ?? "").Trim().Trim('"');   // JS 字符串结果带一层引号
                    foreach (var s in raw.Split(','))
                    {
                        double f;
                        if (double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out f))
                            _anchors.Add(f);
                    }
                    try { done?.Invoke(); } catch { }
                    // 整章都在一屏内（无法滚动）时滚不动，直接续下一章
                    if (_docChaps.Count > 0 && ScrollMax() <= _web.Height + 4) MaybeAppend();
                }));
            }
            catch { try { done?.Invoke(); } catch { } }
        }

        /// <summary>滚动到文档内第 pos 章（章首）。</summary>
        private void ScrollToDocPos(int pos)
        {
            if (pos < 0 || pos >= _anchors.Count) return;
            int max = ScrollMax();
            if (pos < _docChaps.Count && _curBook != null)
            {
                _ci = _docChaps[pos];
                _pct = 0;
                _chapterTitle.Text = _curBook.Chapters[_ci].Title;
                UpdateProgressText();
                QueueProgressSave();
            }
            _restoring = true;
            _web.ScrollTo(0, Math.Min(max, AnchorPx(pos)));
            _web.PostDelayed(() =>
            {
                _restoring = false;
                OnWebScrolled(_web.ScrollY);   // 停稳后按实际位置再校准一次
            }, 160);
        }

        /// <summary>滚动到文档内第 pos 章的指定比例处。</summary>
        private void ScrollToDocPct(int pos, double pct)
        {
            if (pos < 0 || pos >= _anchors.Count) { ScrollToPct(pct); return; }
            if (pos < _docChaps.Count && _curBook != null)
            {
                _ci = _docChaps[pos];
                _pct = Math.Max(0, Math.Min(1, pct));
                _chapterTitle.Text = _curBook.Chapters[_ci].Title;
                UpdateProgressText();
            }
            int max = ScrollMax();
            int top = AnchorPx(pos);
            int bottom = pos + 1 < _anchors.Count ? AnchorPx(pos + 1) : max;
            int span = Math.Max(1, bottom - top);
            _web.ScrollTo(0, Math.Max(0, Math.Min(max, top + (int)Math.Round(span * pct))));
        }

        /// <summary>把字符串转成可安全嵌进 JS 的字面量。</summary>
        private static string QuoteJs(string s)
        {
            if (s == null) return "''";
            var sb = new System.Text.StringBuilder(s.Length + 16);
            sb.Append('\'');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('\'');
            return sb.ToString();
        }

        private sealed class JsCallback : Java.Lang.Object, IValueCallback
        {
            private readonly Action<string> _cb;
            public JsCallback(Action<string> cb) { _cb = cb; }
            public void OnReceiveValue(Java.Lang.Object value)
            {
                try { _cb(value == null ? "" : value.ToString()); } catch { }
            }
        }

        /// <summary>顶栏 / 底栏 / 状态栏 / 正文底色统一跟随主题。</summary>
        private void ApplyChrome()
        {
            try
            {
                var p = C;
                _topBar.SetBackgroundColor(new Color(p.Chrome));
                _botBar.SetBackgroundColor(new Color(p.Chrome));
                _topLine.SetBackgroundColor(new Color(p.Line));
                _botLine.SetBackgroundColor(new Color(p.Line));
                _chapterTitle.SetTextColor(new Color(p.Fg));
                _progText.SetTextColor(new Color(p.Sub));
                _btnBack.SetTextColor(new Color(p.Fg));
                _btnToc.SetTextColor(new Color(p.Sub));
                _btnMark.SetTextColor(new Color(p.Sub));
                _btnAa.SetTextColor(new Color(p.Sub));
                _btnPrev.SetTextColor(new Color(p.Fg));
                _btnNext.SetTextColor(new Color(p.Fg));
                _readerPage.SetBackgroundColor(new Color(p.Bg));
                _bodyHost.SetBackgroundColor(new Color(p.Bg));
                _web.SetBackgroundColor(new Color(p.Bg));
                _pdfScroll.SetPageBg(p.Bg);

                // 音乐迷你条跟随阅读配色（不跟随系统夜间主题，保持阅读面板一致性）
                if (_musicBar != null)
                {
                    _musicBar.SetBackgroundColor(new Color(p.Chrome));
                    _musicLine.SetBackgroundColor(new Color(p.Line));
                    _mbTitle.SetTextColor(new Color(p.Fg));
                    _mbLyric.SetTextColor(new Color(Accent));
                    _mbProg.SetBackgroundColor(new Color(Accent));
                    _mbPrev.IconColor = p.Fg; _mbPrev.Invalidate();
                    _mbToggle.IconColor = p.Fg; _mbToggle.Invalidate();
                    _mbNext.IconColor = p.Fg; _mbNext.Invalidate();
                    _mbQueueBtn.IconColor = MusicPlayer.Queue.Count > 0 ? Accent : p.Sub;
                    _mbQueueBtn.Invalidate();
                }

                Window.SetStatusBarColor(new Color(p.Chrome));
                Window.SetNavigationBarColor(new Color(p.Chrome));
                ApplySystemBars();
            }
            catch { }
        }

        /// <summary>
        /// 系统栏固定显示、窗口尺寸固定。
        /// 阅读页不再做沉浸式（隐藏系统栏）：一旦隐藏/显示系统栏，窗口高度就会变，
        /// 正文会重新排版、滚动位置跟着跳。菜单只是浮层，正文视口始终不变。
        /// </summary>
        private void ApplySystemBars()
        {
            try
            {
                var f = SystemUiFlags.LayoutStable;
                if (!C.Dark) f |= SystemUiFlags.LightStatusBar | SystemUiFlags.LightNavigationBar;
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(int)f;
            }
            catch { }
        }

        /* ---------------- 工具栏隐藏 / 翻页 ---------------- */
        /// <summary>显示 / 收起简易菜单。只切换浮层可见性，正文视口尺寸不变，所以阅读位置不会跳动。</summary>
        private void SetChromeVisible(bool show)
        {
            if (_chromeHidden == !show) return;
            _chromeHidden = !show;
            _topChrome.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _botChrome.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            SyncMusicBarVisibility();
            // 首次唤起时迷你条可能是刚创建出来的「待播」曲目，需要补一次标题/封面/歌词
            if (show) UpdateMusicBar();
        }

        private void ScrollToPct(double pct)
        {
            float content = _web.ContentHeight * _web.Scale;
            int max = (int)Math.Max(0, content - _web.Height);
            if (max <= 0) return;
            _web.ScrollTo(0, (int)Math.Round(max * Math.Max(0, Math.Min(1, pct))));
        }

        private void PageTurn(int dir)
        {
            if (IsPdfBook())
            {
                if (dir < 0) PrevChapter(); else NextChapter();
                return;
            }
            int h = _web.Height;
            if (h <= 0) return;
            int content = (int)(_web.ContentHeight * _web.Scale);
            int max = Math.Max(0, content - h);
            int target = _web.ScrollY + dir * (int)(h * 0.9f);

            if (dir < 0 && target <= 0)
            {
                int pos = _docChaps.IndexOf(_ci);
                if (pos > 0) { ScrollToDocPos(pos - 1); return; }   // 已连读：回到上一章开头
                if (_ci > 0) NextOrPrev(_ci - 1, 1.0);   // 上一章 → 停在章末
                else _web.ScrollTo(0, 0);
                return;
            }
            if (dir > 0 && target >= max)
            {
                int pos = _docChaps.IndexOf(_ci);
                if (pos >= 0 && pos < _docChaps.Count - 1) { ScrollToDocPos(pos + 1); return; }
                if (_curBook != null && _ci < _curBook.Chapters.Count - 1) NextOrPrev(_ci + 1, 0);
                else _web.ScrollTo(0, max);
                return;
            }
            _web.ScrollTo(0, Math.Max(0, Math.Min(max, target)));
        }

        /// <summary>正文区域点击：左右翻页，中间呼出/收起工具栏。</summary>
        internal void OnReaderTap(float x, int width)
        {
            if (width <= 0) return;
            if (_chromeHidden) { SetChromeVisible(true); return; }
            if (x < width * 0.28f) PageTurn(-1);
            else if (x > width * 0.72f) PageTurn(1);
            else SetChromeVisible(false);
        }

        private void PrevChapter()
        {
            if (IsPdfBook()) { if (_ci > 0) NextOrPrev(_ci - 1, 0); return; }
            int pos = _docChaps.IndexOf(_ci);
            if (pos > 0) { ScrollToDocPos(pos - 1); return; }
            if (_ci > 0) NextOrPrev(_ci - 1, 0);
        }
        private void NextChapter()
        {
            if (_curBook == null) return;
            if (IsPdfBook()) { if (_ci < _curBook.Chapters.Count - 1) NextOrPrev(_ci + 1, 0); return; }
            int pos = _docChaps.IndexOf(_ci);
            if (pos >= 0 && pos < _docChaps.Count - 1) { ScrollToDocPos(pos + 1); return; }
            if (_ci < _curBook.Chapters.Count - 1) NextOrPrev(_ci + 1, 0);
        }
        private async void NextOrPrev(int idx, double restorePct)
        {
            // async void：异常不捕获会直接抛到 UI 线程导致崩溃（翻章失败时尤其容易触发）
            try { await ShowChapter(idx, restorePct); }
            catch (Exception ex) { Android.Util.Log.Warn("Reader", "翻章失败: " + ex.Message); }
        }

        /* ---------------- 进度 ---------------- */
        private void QueueProgressSave()
        {
            if (_saveQueued) return;
            _saveQueued = true;
            _web.PostDelayed(() =>
            {
                _saveQueued = false;
                SaveCurrentProgress();
            }, 900);
        }

        private void SaveCurrentProgress()
        {
            if (_curBook == null || string.IsNullOrEmpty(_curId)) return;
            ReaderLibrary.SaveProgress(_curId, _ci, _pct, _curBook.Chapters?.Count ?? 0);
        }

        /// <summary>正文加载完成后恢复本章上次的滚动位置。</summary>
        internal void RestoreScroll()
        {
            if (!_pendingRestore) return;
            _pendingRestore = false;
            double pct = _pct;
            _web.PostDelayed(() =>
            {
                MeasureAnchors(() =>
                {
                    if (pct <= 0.002) { _restoring = false; return; }
                    try
                    {
                        ScrollToDocPct(Math.Max(0, _docChaps.IndexOf(_ci)), pct);
                        _web.PostDelayed(() => { _restoring = false; }, 150);
                    }
                    catch { _restoring = false; }
                });
            }, 120);
        }

        /* ============================================================
         *  目录（底部面板 + 搜索）
         * ============================================================ */
        private void ShowToc()
        {
            if (_curBook == null || _curBook.Chapters.Count == 0) return;

            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(20), Dp(2), Dp(12), Dp(6));
            var t = new TextView(this) { Text = L10n.T("目录"), TextSize = 16 };
            t.SetTextColor(new Color(ThText));
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            bool pdf = IsPdfBook();
            var cnt = new TextView(this) { Text = _curBook.Chapters.Count + (pdf ? L10n.T(" 页") : L10n.T(" 章")), TextSize = 12 };
            cnt.SetTextColor(new Color(ThSub));
            head.AddView(cnt);
            sheet.AddView(head);

            var search = new EditText(this) { Hint = pdf ? L10n.T("输入页码，如 12") : L10n.T("搜索章节名") };
            search.TextSize = 13;
            search.SetSingleLine(true);
            search.SetTextColor(new Color(ThText));
            search.SetHintTextColor(new Color(ThSub));
            search.Background = RoundBg(ThChip, 10);
            search.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            var slp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            slp.LeftMargin = Dp(18); slp.RightMargin = Dp(18); slp.BottomMargin = Dp(6);
            sheet.AddView(search, slp);

            var lv = new ListView(this);
            lv.Divider = null;
            lv.DividerHeight = 0;
            lv.VerticalScrollBarEnabled = false;
            var adapter = new TocAdapter(this, _curBook, _ci);
            lv.Adapter = adapter;
            lv.ItemClick += (s, e) =>
            {
                int chap = adapter.IndexAt(e.Position);
                try { tocDlg?.Dismiss(); } catch { }
                NextOrPrev(chap, 0);
            };
            sheet.AddView(lv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            var dlg = BottomSheet(sheet, (int)(Resources.DisplayMetrics.HeightPixels * 0.78));
            tocDlg = dlg;
            dlg.Show();

            int pos = adapter.PositionOf(_ci);
            if (pos > 2) lv.SetSelection(Math.Max(0, pos - 2));

            search.TextChanged += (s, e) =>
            {
                adapter.Filter(search.Text ?? "");
                lv.SetSelection(0);
            };
        }
        private Dialog tocDlg;

        private sealed class TocAdapter : BaseAdapter
        {
            private readonly ReaderActivity _a;
            private readonly List<Chapter> _ch;
            private readonly List<int> _view = new List<int>();
            private readonly int _cur;

            public TocAdapter(ReaderActivity a, Book book, int cur)
            {
                _a = a; _ch = book.Chapters; _cur = cur;
                Filter("");
            }

            public void Filter(string kw)
            {
                _view.Clear();
                bool empty = string.IsNullOrWhiteSpace(kw);
                string k = empty ? "" : kw.Trim();
                for (int i = 0; i < _ch.Count; i++)
                    if (empty || (_ch[i].Title ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        _view.Add(i);
                NotifyDataSetChanged();
            }

            public int IndexAt(int position) => _view.Count == 0 ? 0 : _view[Math.Min(position, _view.Count - 1)];
            public int PositionOf(int chapter)
            {
                int p = _view.IndexOf(chapter);
                return p < 0 ? 0 : p;
            }

            public override int Count => _view.Count;
            public override Java.Lang.Object GetItem(int position) => null;
            public override long GetItemId(int position) => _view[position];

            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                int idx = _view[position];
                bool cur = idx == _cur;

                var row = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(_a.Dp(20), _a.Dp(13), _a.Dp(18), _a.Dp(13));
                if (cur) row.SetBackgroundColor(new Color(_a.CurChapterBg()));

                var num = new TextView(_a) { Text = (idx + 1).ToString(), TextSize = 10.5f };
                num.SetTextColor(new Color(cur ? Accent : _a.ThSub));
                num.Gravity = GravityFlags.Center;
                row.AddView(num, new LinearLayout.LayoutParams(_a.Dp(34), ViewGroup.LayoutParams.WrapContent));

                var title = new TextView(_a)
                {
                    Text = _ch[idx].Title,
                    TextSize = 14,
                    Ellipsize = Android.Text.TextUtils.TruncateAt.End
                };
                title.SetMaxLines(1);
                title.SetTextColor(new Color(cur ? Accent : _a.ThText));
                if (cur) title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
                row.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                return row;
            }
        }

        /* ============================================================
         *  排版设置（底部面板）
         * ============================================================ */
        private void ShowSettings()
        {
            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(20), Dp(2), Dp(20), Dp(8));
            var t = new TextView(this) { Text = L10n.T("阅读设置"), TextSize = 16 };
            t.SetTextColor(new Color(ThText));
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var book = new TextView(this) { Text = _curBook?.Name ?? "", TextSize = 11.5f };
            book.SetTextColor(new Color(ThSub));
            book.SetMaxLines(1);
            book.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            head.AddView(book, new LinearLayout.LayoutParams(Dp(140), ViewGroup.LayoutParams.WrapContent));
            sheet.AddView(head);

            // —— 字号 ——
            var sizeRow = SettingRow(L10n.T("字号"));
            var sizeBox = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            sizeBox.SetGravity(GravityFlags.CenterVertical);

            var minus = StepBtn("A−", ThChip, ThText);
            var sizeVal = new TextView(this) { Text = _prefs.FontSize.ToString(), TextSize = 14 };
            sizeVal.SetTextColor(new Color(ThText));
            sizeVal.Gravity = GravityFlags.Center;
            var plus = StepBtn("A+", ThChip, ThText);

            minus.Click += (s, e) =>
            {
                _prefs.FontSize = Math.Max(13, _prefs.FontSize - 1);
                sizeVal.Text = _prefs.FontSize.ToString();
                ReaderLibrary.SavePrefs(_prefs); ReRender();
            };
            plus.Click += (s, e) =>
            {
                _prefs.FontSize = Math.Min(32, _prefs.FontSize + 1);
                sizeVal.Text = _prefs.FontSize.ToString();
                ReaderLibrary.SavePrefs(_prefs); ReRender();
            };

            sizeBox.AddView(minus);
            sizeBox.AddView(sizeVal, new LinearLayout.LayoutParams(Dp(46), ViewGroup.LayoutParams.WrapContent));
            sizeBox.AddView(plus);
            sizeRow.AddView(sizeBox, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(sizeRow);

            // —— 行距 ——
            var lsRow = SettingRow(L10n.T("行距"));
            lsRow.AddView(Segmented(new[] { L10n.T("紧凑"), L10n.T("标准"), L10n.T("宽松"), L10n.T("疏朗") }, LineSpaceIndex(),
                i =>
                {
                    _prefs.LineHeight = i == 0 ? 1.5 : (i == 1 ? 1.85 : (i == 2 ? 2.2 : 2.5));
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(lsRow);

            // —— 页边距 ——
            var mgRow = SettingRow(L10n.T("边距"));
            mgRow.AddView(Segmented(new[] { L10n.T("窄"), L10n.T("适中"), L10n.T("宽") }, MarginIndex(),
                i =>
                {
                    _prefs.Margin = i == 0 ? 14 : (i == 1 ? 22 : 34);
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(mgRow);

            // —— 对齐 ——
            var alRow = SettingRow(L10n.T("对齐"));
            alRow.AddView(Segmented(new[] { L10n.T("两端"), L10n.T("左对齐") }, _prefs.Justify ? 0 : 1,
                i =>
                {
                    _prefs.Justify = i == 0;
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(alRow);

            // —— 段距 ——
            var psRow = SettingRow(L10n.T("段距"));
            psRow.AddView(Segmented(new[] { L10n.T("紧凑"), L10n.T("标准"), L10n.T("宽松") }, ParaIndex(),
                i =>
                {
                    _prefs.ParaSpace = i == 0 ? 6 : (i == 1 ? 14 : 24);
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(psRow);

            // —— 首行缩进 ——
            var inRow = SettingRow(L10n.T("缩进"));
            inRow.AddView(Segmented(new[] { L10n.T("无"), L10n.T("2 字") }, _prefs.Indent >= 2 ? 1 : 0,
                i =>
                {
                    _prefs.Indent = i == 0 ? 0 : 2;
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(inRow);

            // —— 字体 ——
            var ftRow = SettingRow(L10n.T("字体"));
            ftRow.AddView(Segmented(new[] { L10n.T("宋体"), L10n.T("黑体"), L10n.T("系统") }, FontIndex(),
                i =>
                {
                    _prefs.Font = i == 0 ? "serif" : (i == 1 ? "sans" : "system");
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(ftRow);

            // —— 字重 ——
            var bdRow = SettingRow(L10n.T("字重"));
            bdRow.AddView(Segmented(new[] { L10n.T("常规"), L10n.T("加粗") }, _prefs.Bold ? 1 : 0,
                i =>
                {
                    _prefs.Bold = i == 1;
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(bdRow);

            // —— 连读（本章到底自动续下一章）——
            var ccRow = SettingRow(L10n.T("连读"));
            ccRow.AddView(Segmented(new[] { L10n.T("开"), L10n.T("关") }, _prefs.AutoAppend ? 0 : 1,
                i =>
                {
                    _prefs.AutoAppend = i == 0;
                    ReaderLibrary.SavePrefs(_prefs); ReRender(); ReopenSettings();
                }),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(ccRow);

            // —— 朗读 ——
            var rdRow = SettingRow(L10n.T("朗读"));
            var rdBtn = new TextView(this) { Text = _ttsOn ? L10n.T("停止朗读") : L10n.T("开始朗读"), TextSize = 12.5f };
            rdBtn.Gravity = GravityFlags.Center;
            rdBtn.SetTextColor(new Color(Accent));
            rdBtn.Background = RoundBg(ThChip, 9);
            rdBtn.SetPadding(Dp(14), Dp(7), Dp(14), Dp(7));
            rdBtn.Click += (s2, e2) => { ToggleTts(); try { settingsDlg?.Dismiss(); } catch { } };
            rdRow.AddView(rdBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(rdRow);

            // —— 背景色 ——
            var thRow = SettingRow(L10n.T("背景"));
            var sw = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            sw.SetGravity(GravityFlags.CenterVertical);
            foreach (var th in Themes) sw.AddView(ThemeSwatch(th.key, th.label, th.preview));
            thRow.AddView(sw, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            sheet.AddView(thRow);

            var tip = new TextView(this) { Text = L10n.T("阅读时点屏幕中间可隐藏/显示工具栏，点左右两侧翻页"), TextSize = 10.5f };
            tip.SetTextColor(new Color(ThSub));
            tip.SetPadding(Dp(20), Dp(4), Dp(20), Dp(2));
            sheet.AddView(tip);

            var done = new TextView(this) { Text = L10n.T("完成"), TextSize = 15 };
            done.Gravity = GravityFlags.Center;
            done.SetTextColor(Color.White);
            done.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            done.Background = RoundBg(Accent, 12);
            var dlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46));
            dlp.LeftMargin = Dp(20); dlp.RightMargin = Dp(20); dlp.TopMargin = Dp(10); dlp.BottomMargin = Dp(14);
            done.LayoutParameters = dlp;
            done.Click += (s, e) => { try { settingsDlg?.Dismiss(); } catch { } };
            sheet.AddView(done);

            settingsDlg = BottomSheet(sheet, ViewGroup.LayoutParams.WrapContent);
            settingsDlg.Show();
        }
        private Dialog settingsDlg;

        private int LineSpaceIndex()
        {
            if (_prefs.LineHeight < 1.7) return 0;
            if (_prefs.LineHeight < 2.0) return 1;
            if (_prefs.LineHeight < 2.35) return 2;
            return 3;
        }

        private int ParaIndex()
        {
            if (_prefs.ParaSpace <= 9) return 0;
            if (_prefs.ParaSpace <= 18) return 1;
            return 2;
        }

        private int FontIndex()
        {
            if (_prefs.Font == "sans") return 1;
            if (_prefs.Font == "system") return 2;
            return 0;
        }

        private int MarginIndex()
        {
            if (_prefs.Margin <= 16) return 0;
            if (_prefs.Margin <= 26) return 1;
            return 2;
        }

        private LinearLayout SettingRow(string label)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(20), Dp(7), Dp(20), Dp(7));
            var l = new TextView(this) { Text = label, TextSize = 13 };
            l.SetTextColor(new Color(ThSub));
            row.AddView(l, new LinearLayout.LayoutParams(Dp(46), ViewGroup.LayoutParams.WrapContent));
            return row;
        }

        private TextView StepBtn(string text, int bg, int fg)
        {
            var b = new TextView(this) { Text = text, TextSize = 13 };
            b.Gravity = GravityFlags.Center;
            b.SetTextColor(new Color(fg));
            b.Background = RoundBg(bg, 9);
            b.LayoutParameters = new LinearLayout.LayoutParams(Dp(46), Dp(36));
            return b;
        }

        private View Segmented(string[] labels, int selected, Action<int> onPick)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            box.Background = RoundBg(ThChip, 9);
            box.SetPadding(Dp(3), Dp(3), Dp(3), Dp(3));
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var item = new TextView(this) { Text = labels[i], TextSize = 12.5f };
                item.Gravity = GravityFlags.Center;
                item.SetPadding(Dp(2), Dp(7), Dp(2), Dp(7));
                if (i == selected)
                {
                    item.SetTextColor(new Color(ThText));
                    item.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
                    item.Background = RoundBg(ThChipSel, 7);
                }
                else item.SetTextColor(new Color(ThSub));
                item.Click += (s, e) => onPick(idx);
                box.AddView(item, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            }
            return box;
        }

        private View ThemeSwatch(string key, string label, int color)
        {
            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetGravity(GravityFlags.Center);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            bool sel = _prefs.Theme == key;
            var dot = new View(this);
            var d = new GradientDrawable();
            d.SetShape(ShapeType.Oval);
            d.SetColor(color);
            d.SetStroke(Dp(sel ? 2 : 1), Android.Content.Res.ColorStateList.ValueOf(
                new Color(sel ? Accent : ThLine)));
            dot.Background = d;
            var dlp = new LinearLayout.LayoutParams(Dp(26), Dp(26));
            dlp.Gravity = GravityFlags.Center;
            col.AddView(dot, dlp);

            var t = new TextView(this) { Text = label, TextSize = 9.5f };
            t.SetTextColor(new Color(sel ? ThText : ThSub));
            t.Gravity = GravityFlags.Center;
            t.SetPadding(0, Dp(4), 0, 0);
            col.AddView(t);

            dot.Click += (s, e) =>
            {
                _prefs.Theme = key;
                ReaderLibrary.SavePrefs(_prefs);
                ApplyChrome();
                ReRender();
                ReopenSettings();
            };
            return col;
        }

        private void ReopenSettings()
        {
            try { settingsDlg?.Dismiss(); } catch { }
            settingsDlg = null;
            ShowSettings();
        }

        private View SheetHandle(int lineColor)
        {
            var wrap = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            wrap.SetGravity(GravityFlags.Center);
            wrap.SetPadding(0, Dp(8), 0, Dp(8));
            var bar = new View(this);
            var d = new GradientDrawable();
            d.SetCornerRadius(Dp(2));
            d.SetColor(new Color(lineColor));
            bar.Background = d;
            wrap.AddView(bar, new LinearLayout.LayoutParams(Dp(38), Dp(4)));
            return wrap;
        }

        private void ReRender()
        {
            var ch = (_curBook != null && _curBook.Chapters != null && _ci < _curBook.Chapters.Count)
                ? _curBook.Chapters[_ci] : null;
            if (ch != null)
            {
                _pendingRestore = true;
                _restoring = true;
                _web.Settings.DefaultFontSize = _prefs.FontSize;
                RenderBody(ch);
            }
            UpdateProgressText();
        }

        /* ============================================================
         *  导入本地书
         * ============================================================ */
        private void OnImport(View v)
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            intent.PutExtra(Intent.ExtraMimeTypes, new[]
            {
                "application/pdf", "application/epub+zip", "text/plain",
                "application/x-fictionbook+xml", "text/markdown",
                "text/x-markdown", "application/zip", "application/octet-stream"
            });
            StartActivityForResult(intent, REQ_PICK);
        }

        /// <summary>从 ContentResolver 取文件真实显示名（LastPathSegment 常常只是 document/1234）。</summary>
        private string QueryDisplayName(Android.Net.Uri uri)
        {
            try
            {
                using (var cur = ContentResolver.Query(uri, null, null, null, null))
                {
                    if (cur != null && cur.MoveToFirst())
                    {
                        int i = cur.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                        if (i >= 0 && !cur.IsNull(i))
                        {
                            var v = cur.GetString(i);
                            if (!string.IsNullOrWhiteSpace(v)) return v;
                        }
                    }
                }
            }
            catch { }
            return uri.LastPathSegment ?? "";
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode == REQ_SRC && resultCode == Result.Ok && data != null)
            {
                try
                {
                    byte[] raw;
                    using (var stream = ContentResolver.OpenInputStream(data.Data))
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        raw = ms.ToArray();
                    }
                    if (raw.Length == 0) { ShowToast(L10n.T("文件为空")); return; }
                    // 书源 JSON 可能是 GBK/UTF-8(BOM)，统一探测后再解析
                    string text = EncodingEx.Detect(raw, null);
                    if (string.IsNullOrWhiteSpace(text)) { ShowToast(L10n.T("文件内容无法识别")); return; }
                    ImportSources(text);
                }
                catch (Exception ex) { ShowToast(L10n.T("导入失败：") + ex.Message); }
                return;
            }

            if (requestCode == REQ_SCAN && resultCode == Result.Ok && data != null)
            {
                BeginScan(data.Data);
                return;
            }

            if (requestCode == REQ_PICK && resultCode == Result.Ok && data != null)
            {
                // PDF 导入要渲染第一页做封面，放到后台线程，避免大文件卡住 UI
                var uri = data.Data;
                Task.Run(() =>
                {
                    try
                    {
                        byte[] bytes;
                        using (var stream = ContentResolver.OpenInputStream(uri))
                        using (var ms = new MemoryStream())
                        {
                            if (stream == null) { RunOnUiThread(() => ShowToast(L10n.T("无法读取文件"))); return; }
                            stream.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                        string raw = QueryDisplayName(uri);
                        string err;
                        var item = ReaderLibrary.Import(raw, bytes, out err);
                        RunOnUiThread(() =>
                        {
                            if (item == null) { ShowToast(err ?? L10n.T("导入失败")); return; }
                            _shelf = ReaderLibrary.Shelf();
                            RenderShelf();
                            ShowToast(L10n.T("已加入书架：") + item.Name);
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUiThread(() => ShowToast(L10n.T("导入失败：") + ex.Message));
                    }
                });
            }
        }

        /* ============================================================
         *  扫描文件夹（SAF 目录树 → 批量导入本地书）
         * ============================================================ */
        private void OnScanFolder(View v)
        {
            // 目录管理：手动输入路径 / 预设默认目录 / 系统目录选择器 / 一键扫描全部
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));

            var hint = new TextView(this)
            {
                Text = L10n.T("已保存的扫描目录（本地文件扫描，需「所有文件访问」权限）"),
                TextSize = 12
            };
            hint.SetTextColor(new Color(TextSub));
            root.AddView(hint);

            var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.AddView(list, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6) });

            var input = new EditText(this) { Hint = "/storage/emulated/0/Books", TextSize = 13 };
            input.SetSingleLine(true);
            root.AddView(input, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10) });

            var presetRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            foreach (var d in ReaderLibrary.DefaultScanDirs)
            {
                var chip = new TextView(this) { Text = System.IO.Path.GetFileName(d), TextSize = 12 };
                chip.SetTextColor(new Color(Accent));
                chip.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
                chip.Click += (s, e) => input.Text = d;
                presetRow.AddView(chip);
            }
            root.AddView(presetRow);

            Action refreshList = null;
            refreshList = () =>
            {
                list.RemoveAllViews();
                var dirs = ReaderLibrary.ScanDirs();
                if (dirs.Count == 0)
                    list.AddView(new TextView(this) { Text = L10n.T("（暂无目录，可添加或直接用预设）"), TextSize = 12 });
                foreach (var d in new List<string>(dirs))
                {
                    var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    var t = new TextView(this) { Text = d, TextSize = 12 };
                    t.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    var del = new TextView(this) { Text = L10n.T("移除"), TextSize = 12 };
                    del.SetTextColor(new Color(Accent));
                    del.SetPadding(Dp(8), 0, 0, 0);
                    del.Click += (s, e) =>
                    {
                        var cur = ReaderLibrary.ScanDirs();
                        cur.Remove(d);
                        ReaderLibrary.SaveScanDirs(cur);
                        refreshList();
                    };
                    row.AddView(t); row.AddView(del);
                    list.AddView(row);
                }
            };
            refreshList();

            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("本地书籍扫描目录"));
            b.SetView(root);
            b.SetPositiveButton(L10n.T("添加并扫描"), (s, e) =>
            {
                var p = (input.Text ?? "").Trim().TrimEnd('/');
                if (p.Length > 0)
                {
                    var cur = ReaderLibrary.ScanDirs();
                    if (!cur.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) cur.Add(p);
                    ReaderLibrary.SaveScanDirs(cur);
                }
                EnsureReaderStorageThenScan();
            });
            b.SetNeutralButton(L10n.T("选择文件夹(SAF)"), (s, e) =>
            {
                try
                {
                    var intent = new Intent(Intent.ActionOpenDocumentTree);
                    intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
                    StartActivityForResult(intent, REQ_SCAN);
                }
                catch (Exception ex) { ShowToast(L10n.T("无法打开文件夹选择器：") + ex.Message); }
            });
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        private void EnsureReaderStorageThenScan()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                bool all;
                try { all = Android.OS.Environment.IsExternalStorageManager; }
                catch { all = true; }
                if (!all)
                {
                    ShowToast(L10n.T("需要「所有文件访问」权限才能按路径扫描本地书籍"));
                    try
                    {
                        StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                            Android.Net.Uri.Parse("package:" + PackageName)));
                    }
                    catch
                    {
                        try { StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission)); } catch { }
                    }
                    return;
                }
            }
            else
            {
                bool granted;
                try
                {
                    granted = Build.VERSION.SdkInt < BuildVersionCodes.M ||
                              CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted;
                }
                catch { granted = true; }
                if (!granted)
                {
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                        RequestPermissions(new[] { Android.Manifest.Permission.ReadExternalStorage }, 3002);
                    return;
                }
            }
            BeginScanSavedDirs();
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == 3002 && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                BeginScanSavedDirs();
        }

        private void BeginScan(Android.Net.Uri treeUri)
        {
            if (treeUri == null) return;
            try { ContentResolver.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission); } catch { }
            // 持久化这个 SAF 目录：下次进来自动重扫，和影音「添加文件夹」行为一致。
            // content:// 的 tree uri 在 BeginScanSavedDirs 里按 Uri 走 ScanTree 分支。
            var dirs = ReaderLibrary.ScanDirs() ?? new List<string>();
            string u = treeUri.ToString();
            bool exists = false;
            foreach (var d in dirs) if (string.Equals(d, u, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
            if (!exists) { dirs.Add(u); ReaderLibrary.SaveScanDirs(dirs); }
            ShowToast(L10n.T("正在扫描文件夹…"));
            Task.Run(() => ScanTree(treeUri));
        }

        /// <summary>按保存的目录清单扫描（绝对路径，需「所有文件访问」权限；与影音目录扫描一致）。</summary>
        private void BeginScanSavedDirs()
        {
            var dirs = ReaderLibrary.ScanDirs();
            if (dirs == null || dirs.Count == 0) { ShowToast(L10n.T("还没有扫描目录，请先添加")); return; }
            ShowToast(L10n.T("正在扫描 ") + dirs.Count + L10n.T(" 个目录…"));
            Task.Run(() =>
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in ReaderLibrary.Shelf()) if (!string.IsNullOrEmpty(it.Name)) existing.Add(it.Name);
                var hits = new List<(string Id, string Parent)>();
                foreach (var dir in dirs)
                {
                    if (!string.IsNullOrEmpty(dir) && dir.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
                    {
                        // SAF 选的目录树（持久 URI 权限），按 DocumentsContract 遍历
                        try
                        {
                            var tu = Android.Net.Uri.Parse(dir);
                            string treeId = DocumentsContract.GetTreeDocumentId(tu);
                            string rootName = TreeDisplayName(tu) ?? L10n.T("书籍");
                            if (!string.IsNullOrEmpty(treeId)) TraverseTree(tu, treeId, rootName, existing, hits);
                        }
                        catch { }
                    }
                    else ScanAbsDir(dir, existing, hits, 0);
                }
                RunOnUiThread(() =>
                {
                    if (hits.Count == 0) { ShowToast(L10n.T("这些目录里没有可导入的书籍")); return; }
                    ReaderLibrary.ApplyFolderGrouping(hits);
                    _shelf = ReaderLibrary.Shelf();
                    RenderShelf();
                    ShowToast(L10n.T("已导入 ") + hits.Count + L10n.T(" 本"));
                });
            });
        }

        private void ScanAbsDir(string dir, HashSet<string> existing, List<(string Id, string Parent)> hits, int depth)
        {
            if (depth > 6 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { ScanAbsDir(sub, existing, hits, depth + 1); } catch { }
                }
                foreach (var file in Directory.GetFiles(dir))
                {
                    string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (Array.IndexOf(BookExts, ext) < 0) continue;
                    string name = System.IO.Path.GetFileName(file);
                    if (existing.Contains(name)) continue;
                    try
                    {
                        var bytes = File.ReadAllBytes(file);
                        if (bytes.Length == 0) continue;
                        string err;
                        var item = ReaderLibrary.Import(name, bytes, out err);
                        if (item != null) { hits.Add((item.Id, System.IO.Path.GetFileName(dir))); existing.Add(name); }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ScanTree(Android.Net.Uri treeUri)
        {
            try
            {
                // 已存在的书名集合，跳过同名书避免重复导入
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in ReaderLibrary.Shelf()) if (!string.IsNullOrEmpty(it.Name)) existing.Add(it.Name);

                string treeId = DocumentsContract.GetTreeDocumentId(treeUri);
                string rootName = TreeDisplayName(treeUri) ?? L10n.T("书籍");
                var hits = new List<(string Id, string Parent)>();
                if (!string.IsNullOrEmpty(treeId))
                    TraverseTree(treeUri, treeId, rootName, existing, hits);

                RunOnUiThread(() =>
                {
                    if (hits.Count == 0)
                    {
                        ShowToast(L10n.T("该文件夹内没有可导入的书籍（txt/pdf/epub/fb2/md/zip）"));
                        return;
                    }
                    ReaderLibrary.ApplyFolderGrouping(hits);
                    _shelf = ReaderLibrary.Shelf();
                    RenderShelf();
                    ShowToast(L10n.T("已导入 ") + hits.Count + L10n.T(" 本（同目录超过 4 本自动归入文件夹）"));
                });
            }
            catch (Exception ex) { RunOnUiThread(() => ShowToast(L10n.T("扫描失败：") + ex.Message)); }
        }

        private void TraverseTree(Android.Net.Uri treeUri, string parentDocId, string dirName,
                                  HashSet<string> existing, List<(string Id, string Parent)> hits)
        {
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
            using (var cur = ContentResolver.Query(childrenUri, new[]
                 {
                     DocumentsContract.Document.ColumnDocumentId,
                     DocumentsContract.Document.ColumnDisplayName,
                     DocumentsContract.Document.ColumnMimeType
                 }, null, null, null))
            {
                if (cur == null) return;
                int iId = cur.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
                int iName = cur.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                int iMime = cur.GetColumnIndex(DocumentsContract.Document.ColumnMimeType);
                while (cur.MoveToNext())
                {
                    string cid = iId >= 0 ? cur.GetString(iId) : null;
                    if (string.IsNullOrEmpty(cid)) continue;
                    string name = iName >= 0 ? (cur.GetString(iName) ?? "") : "";
                    string mime = iMime >= 0 ? (cur.GetString(iMime) ?? "") : "";
                    var childUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, cid);

                    if (mime == DocumentsContract.Document.MimeTypeDir)
                    {
                        TraverseTree(treeUri, cid, name ?? dirName, existing, hits);
                        continue;
                    }
                    string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                    if (Array.IndexOf(BookExts, ext) < 0) continue;
                    if (existing.Contains(name)) continue;   // 跳过已存在的同名书
                    try
                    {
                        byte[] bytes;
                        using (var st = ContentResolver.OpenInputStream(childUri))
                        using (var ms = new MemoryStream()) { st.CopyTo(ms); bytes = ms.ToArray(); }
                        if (bytes == null || bytes.Length == 0) continue;
                        string err;
                        var item = ReaderLibrary.Import(name, bytes, out err);
                        if (item != null) { hits.Add((item.Id, dirName)); existing.Add(name); }
                    }
                    catch { }
                }
            }
        }

        private string TreeDisplayName(Android.Net.Uri treeUri)
        {
            try
            {
                string treeId = DocumentsContract.GetTreeDocumentId(treeUri);
                var docUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeId);
                using (var cur = ContentResolver.Query(docUri, new[] { DocumentsContract.Document.ColumnDisplayName }, null, null, null))
                {
                    if (cur != null && cur.MoveToFirst())
                    {
                        int i = cur.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                        if (i >= 0) return cur.GetString(i);
                    }
                }
            }
            catch { }
            return null;
        }

        /* ============================================================
         *  网页端入口提示
         * ============================================================ */
        private void OnWebReader(View v)
        {
            string url = "http://" + LocalIp() + ":7790/reader";
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(6), Dp(20), Dp(2));

            var t1 = new TextView(this) { Text = url, TextSize = 15 };
            t1.SetTextColor(new Color(Accent));
            t1.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            box.AddView(t1);

            var t2 = new TextView(this)
            {
                Text = L10n.T("同一局域网内，用电脑浏览器打开上面的地址即可阅读；\n书架上传的 TXT / EPUB 会出现在 App 里，阅读进度、字号、背景色两边同步。"),
                TextSize = 12
            };
            t2.SetTextColor(new Color(TextSub));
            t2.SetPadding(0, Dp(8), 0, 0);
            box.AddView(t2);

            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("网页端阅读器"));
            b.SetView(box);
            b.SetPositiveButton(L10n.T("复制地址"), (s, e) =>
            {
                try
                {
                    var clip = (ClipboardManager)GetSystemService(ClipboardService);
                    clip.PrimaryClip = ClipData.NewPlainText("reader", url);
                    ShowToast(L10n.T("已复制"));
                }
                catch { }
            });
            b.SetNegativeButton(L10n.T("关闭"), (s, e) => { });
            b.Create().Show();
        }

        private string LocalIp()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (System.Net.IPAddress.IsLoopback(ua.Address)) continue;
                        var s = ua.Address.ToString();
                        if (s.StartsWith("169.254")) continue;
                        return s;
                    }
                }
            }
            catch { }
            return L10n.T("手机IP");
        }

        /* ============================================================
         *  在线搜索
         * ============================================================ */
        private void OnSearch(View v)
        {
            _sources = ReaderLibrary.Sources();
            var enabled = _sources.FindAll(x => x.Enabled);
            if (enabled.Count == 0)
            {
                ShowToast(L10n.T("请先在「书源」里导入并启用书源"));
                return;
            }
            var input = new EditText(this) { Hint = L10n.T("输入书名或作者") };
            input.SetSingleLine(true);
            input.TextSize = 14;
            input.Background = RoundBg(ChipBg, 10);
            input.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));

            var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
            layout.SetPadding(Dp(20), Dp(4), Dp(20), Dp(2));
            layout.AddView(input);

            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("搜索网络书籍（") + enabled.Count + L10n.T(" 个源）"));
            b.SetView(layout);
            b.SetPositiveButton(L10n.T("搜索"), async (s, e) => await DoSearch(input.Text == null ? "" : input.Text.Trim()));
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        private async Task DoSearch(string key)
        {
            if (string.IsNullOrEmpty(key)) { ShowToast(L10n.T("请输入关键词")); return; }
            var enabled = ReaderLibrary.Sources().FindAll(x => x.Enabled && x.BookSourceType == 0);
            if (enabled.Count == 0) { ShowToast(L10n.T("没有启用的文本书源，请先导入书源")); return; }

            _srchSeen.Clear();
            _srchCount = 0;
            _srchTotal = enabled.Count;
            _srchDone = 0;

            _srchBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var sv = new ScrollView(this);
            sv.AddView(_srchBox);
            _srchStatus = new TextView(this) { Text = L10n.T("正在搜索 0/") + enabled.Count + L10n.T(" 个源…"), TextSize = 12 };
            _srchStatus.SetTextColor(new Color(TextSub));
            _srchStatus.SetPadding(Dp(18), Dp(10), Dp(18), Dp(2));

            var bld = L10n.Dialog(this);
            bld.SetTitle(L10n.T("搜索网络书籍"));
            bld.SetView(sv);
            bld.SetNegativeButton(L10n.T("关闭"), (s, e) => { });
            srchDlg = bld.Create();
            srchDlg.Show();

            // 逐源并发搜索：一个源有结果就立刻追加到列表，并回报已完成源数
            await ReaderLibrary.SearchIncrementalAsync(key,
                batch => RunOnUiThread(() => AppendSearchBatch(batch)),
                (d, t) => RunOnUiThread(() => { _srchDone = d; _srchTotal = t; UpdateSearchStatus(); }));
            RunOnUiThread(UpdateSearchStatus);
        }

        /// <summary>把一批搜索结果（来自单个书源）追加进增量对话框，去重并显示进度。</summary>
        private void AppendSearchBatch(List<Book> batch)
        {
            if (_srchBox == null) return;
            foreach (var b in batch)
            {
                var k = b.Name + "|" + b.SourceUrl;
                if (!_srchSeen.Add(k)) continue;          // 去重（跨源也生效）
                if (_srchSeen.Count > 200) continue;      // 已达上限：仍记录去重但不上屏
                _srchCount++;

                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(18), Dp(12), Dp(12), Dp(12));

                var txt = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var t = new TextView(this) { Text = b.Name, TextSize = 14.5f };
                t.SetTextColor(new Color(TextMain));
                txt.AddView(t);
                var sub = new TextView(this)
                {
                    Text = (string.IsNullOrEmpty(b.Author) ? L10n.T("佚名") : b.Author) + " · " + ReaderLibrary.HostOf(b.SourceUrl),
                    TextSize = 11.5f
                };
                sub.SetTextColor(new Color(TextSub));
                sub.SetPadding(0, Dp(3), 0, 0);
                txt.AddView(sub);
                row.AddView(txt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                var go = new TextView(this) { Text = L10n.T("加入"), TextSize = 12 };
                go.SetTextColor(new Color(Accent));
                go.SetPadding(Dp(12), Dp(6), Dp(8), Dp(6));
                row.AddView(go);

                var cap = b;
                row.Click += (s, e) => { try { srchDlg?.Dismiss(); } catch { } AddOnlineBook(cap); };
                _srchBox.AddView(row);

                var dv = new View(this);
                dv.SetBackgroundColor(new Color(unchecked((int)0xFFEEEFF1)));
                var dlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
                dlp.LeftMargin = Dp(18);
                dv.LayoutParameters = dlp;
                _srchBox.AddView(dv);
            }
            UpdateSearchStatus();
        }

        private void UpdateSearchStatus()
        {
            if (_srchStatus == null) return;
            _srchStatus.Text = L10n.T("已搜索 ") + _srchDone + "/" + _srchTotal + L10n.T(" 个源 · 找到 ") + _srchCount + L10n.T(" 本");
        }

        private AlertDialog srchDlg;

        private void ShowSearchResults(List<Book> list)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            foreach (var b in list)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(18), Dp(12), Dp(12), Dp(12));

                var txt = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var t = new TextView(this) { Text = b.Name, TextSize = 14.5f };
                t.SetTextColor(new Color(TextMain));
                txt.AddView(t);
                var sub = new TextView(this)
                {
                    Text = (string.IsNullOrEmpty(b.Author) ? L10n.T("佚名") : b.Author) + " · " + ReaderLibrary.HostOf(b.SourceUrl),
                    TextSize = 11.5f
                };
                sub.SetTextColor(new Color(TextSub));
                sub.SetPadding(0, Dp(3), 0, 0);
                txt.AddView(sub);
                row.AddView(txt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                var go = new TextView(this) { Text = L10n.T("加入"), TextSize = 12 };
                go.SetTextColor(new Color(Accent));
                go.SetPadding(Dp(12), Dp(6), Dp(8), Dp(6));
                row.AddView(go);

                var cap = b;
                row.Click += (s, e) => { try { srchDlg?.Dismiss(); } catch { } AddOnlineBook(cap); };
                box.AddView(row);

                var dv = new View(this);
                dv.SetBackgroundColor(new Color(unchecked((int)0xFFEEEFF1)));
                var dlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
                dlp.LeftMargin = Dp(18);
                dv.LayoutParameters = dlp;
                box.AddView(dv);
            }
            var sv = new ScrollView(this);
            sv.AddView(box);

            var bld = L10n.Dialog(this);
            bld.SetTitle(L10n.T("搜索结果（") + list.Count + "）");
            bld.SetView(sv);
            bld.SetNegativeButton(L10n.T("关闭"), (s, e) => { });
            srchDlg = bld.Create();
            srchDlg.Show();
        }

        private void AddOnlineBook(Book b)
        {
            var sid = "online_" + Guid.NewGuid().ToString("N");
            var list = ReaderLibrary.Shelf();
            list.Insert(0, new ShelfItem
            {
                Id = sid, Name = b.Name, Author = b.Author, Type = "online",
                SourceUrl = b.SourceUrl, BookUrl = b.BookUrl, TocUrl = b.TocUrl,
                CoverUrl = b.CoverUrl, Added = DateTime.Now
            });
            ReaderLibrary.SaveShelf(list);
            _shelf = ReaderLibrary.Shelf();
            RenderShelf();
            OpenBook(sid);
        }

        /* ============================================================
         *  书源管理（底部面板：文件 / 剪贴板 / 网址）
         * ============================================================ */
        private void OnManageSources(View v)
        {
            _sources = ReaderLibrary.Sources();

            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(20), Dp(2), Dp(20), Dp(6));
            var t = new TextView(this) { Text = L10n.T("书源"), TextSize = 16 };
            t.SetTextColor(new Color(ThText));
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var cnt = new TextView(this) { Text = _sources.Count + L10n.T(" 个"), TextSize = 12 };
            cnt.SetTextColor(new Color(ThSub));
            head.AddView(cnt);
            sheet.AddView(head);

            var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _srcList = list;
            RenderSourceList(list);
            var sv = new ScrollView(this);
            sv.AddView(list);
            var slp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
            slp.LeftMargin = Dp(14); slp.RightMargin = Dp(14);
            sheet.AddView(sv, slp);

            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            btns.SetPadding(Dp(14), Dp(6), Dp(14), Dp(4));
            btns.AddView(SmallBtn(L10n.T("粘贴 JSON"), v2 => ImportSourcesFromClipboard()), Weight());
            btns.AddView(SmallBtn(L10n.T("选文件"), v2 => PickSourceFile()), Weight());
            btns.AddView(SmallBtn(L10n.T("网址导入"), v2 => ImportSourcesFromUrl()), Weight());
            btns.AddView(SmallBtn(L10n.T("恢复内置"), v2 => RestoreBuiltinSources()), Weight());
            sheet.AddView(btns);

            var done = new TextView(this) { Text = L10n.T("完成"), TextSize = 15 };
            done.Gravity = GravityFlags.Center;
            done.SetTextColor(Color.White);
            done.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            done.Background = RoundBg(Accent, 12);
            var dlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46));
            dlp.LeftMargin = Dp(20); dlp.RightMargin = Dp(20); dlp.TopMargin = Dp(6); dlp.BottomMargin = Dp(14);
            done.LayoutParameters = dlp;
            done.Click += (s, e) => { try { svcDlg?.Dismiss(); } catch { } };
            sheet.AddView(done);

            svcDlg = BottomSheet(sheet, (int)(Resources.DisplayMetrics.HeightPixels * 0.72));
            svcDlg.Show();
            if (_sources.Count == 0) RestoreBuiltinSources();   // 首次无书源时自动拉取内置推荐
        }
        private Dialog svcDlg;
        private LinearLayout _srcList;

        private LinearLayout.LayoutParams Weight()
        {
            var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lp.SetMargins(Dp(4), 0, Dp(4), 0);
            return lp;
        }

        private TextView SmallBtn(string text, Action<View> onClick)
        {
            var b = new TextView(this) { Text = text, TextSize = 12.5f };
            b.Gravity = GravityFlags.Center;
            b.SetTextColor(new Color(ThText));
            b.Background = RoundBg(ThChip, 10);
            b.SetPadding(Dp(10), Dp(11), Dp(10), Dp(11));
            b.Click += (s, e) => onClick(b);
            return b;
        }

        private void RenderSourceList(LinearLayout list)
        {
            list.RemoveAllViews();
            if (_sources.Count == 0)
            {
                var t = new TextView(this)
                {
                    Text = L10n.T("暂无书源。\n\n打开时会自动拉取内置推荐书源；也可手动：\n· 粘贴 legado 书源 JSON（数组或单个对象）\n· 选择 .json 文件\n· 填一个书源 JSON 的网址\n· 点「恢复内置」一键导入推荐书源\n\n导入后勾选框控制是否参与搜索。"),
                    TextSize = 12.5f
                };
                t.SetTextColor(new Color(ThSub));
                t.SetPadding(Dp(6), Dp(10), Dp(6), Dp(10));
                t.SetLineSpacing(1, 1.15f);
                list.AddView(t);
                return;
            }
            foreach (var src in _sources)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetPadding(Dp(6), Dp(2), Dp(6), Dp(2));
                row.SetGravity(GravityFlags.CenterVertical);
                var cb = new CheckBox(this) { Checked = src.Enabled };
                cb.Text = src.BookSourceName + (string.IsNullOrEmpty(src.BookSourceGroup) ? "" : "（" + src.BookSourceGroup + "）");
                cb.TextSize = 13.5f;
                cb.SetTextColor(new Color(ThText));
                IosStyle.StyleCheckBoxes(cb);
                var captured = src;
                cb.CheckedChange += (s, e) =>
                {
                    captured.Enabled = e.IsChecked;
                    var all = ReaderLibrary.Sources();
                    var it = all.Find(x => x.BookSourceUrl == captured.BookSourceUrl);
                    if (it != null) { it.Enabled = e.IsChecked; ReaderLibrary.SaveSources(all); }
                    _sources = all;
                };
                row.AddView(cb, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                var del = new TextView(this) { Text = L10n.T("删除"), TextSize = 11.5f };
                del.SetTextColor(new Color(unchecked((int)0xFFE0564B)));
                del.SetPadding(Dp(10), Dp(8), Dp(6), Dp(8));
                del.Click += (s, e) =>
                {
                    var all = ReaderLibrary.Sources();
                    all.RemoveAll(x => x.BookSourceUrl == captured.BookSourceUrl);
                    ReaderLibrary.SaveSources(all);
                    _sources = all;
                    RenderSourceList(list);
                    ShowToast(L10n.T("已删除书源"));
                };
                row.AddView(del);
                list.AddView(row);
            }
        }

        private void PickSourceFile()
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            StartActivityForResult(intent, REQ_SRC);
        }

        private void ImportSourcesFromClipboard()
        {
            try
            {
                var clip = (ClipboardManager)GetSystemService(ClipboardService);
                string text = "";
                if (clip.HasPrimaryClip && clip.PrimaryClip.GetItemAt(0) != null)
                    text = clip.PrimaryClip.GetItemAt(0).Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) { ShowToast(L10n.T("剪贴板为空")); return; }
                ImportSources(text);
            }
            catch (Exception ex) { ShowToast(L10n.T("导入失败：") + ex.Message); }
        }

        private void ImportSourcesFromUrl()
        {
            var input = new EditText(this) { Hint = L10n.T("https://…/yuan.json  或  /sdcard/yuan.json") };
            input.SetSingleLine(true);
            input.TextSize = 13;
            input.Text = "/sdcard/yuan.json";
            input.Background = RoundBg(ChipBg, 10);
            input.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));

            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(4), Dp(20), Dp(2));
            box.AddView(input);
            var tip = new TextView(this) { Text = L10n.T("支持 http(s) 网址，也支持手机里的文件路径（如 /sdcard/yuan.json）"), TextSize = 11 };
            tip.SetTextColor(new Color(TextSub));
            tip.SetPadding(0, Dp(8), 0, 0);
            box.AddView(tip);

            var b = L10n.Dialog(this);
            b.SetTitle(L10n.T("从网址 / 路径导入书源"));
            b.SetView(box);
            b.SetPositiveButton(L10n.T("导入"), async (s, e) =>
            {
                var url = (input.Text ?? "").Trim();
                if (url.Length == 0) { ShowToast(L10n.T("请输入地址或路径")); return; }
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // 本地文件：/sdcard/yuan.json 或 file:///sdcard/yuan.json
                    var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? url.Substring(7) : url;
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length == 0) { ShowToast(L10n.T("文件为空")); return; }
                        ImportSources(EncodingEx.Detect(bytes, null));
                    }
                    catch (Exception ex) { ShowToast(L10n.T("读取失败：") + ex.Message); }
                    return;
                }
                ShowToast(L10n.T("正在下载…"));
                var text = await _engine.FetchAsync(url);
                if (string.IsNullOrWhiteSpace(text)) { ShowToast(L10n.T("下载失败，检查网络或地址")); return; }
                ImportSources(text);
            });
            b.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            b.Create().Show();
        }

        private void ImportSources(string text)
        {
            string err;
            int n = ReaderLibrary.MergeSources(text, out err);
            _sources = ReaderLibrary.Sources();
            try { svcDlg?.Dismiss(); } catch { }
            if (n <= 0) { ShowToast(err ?? L10n.T("未识别到书源")); return; }
            ShowToast(L10n.T("已导入/更新 ") + n + L10n.T(" 个书源"));
        }

        private bool _restoringBuiltin;
        /// <summary>从内置书源地址一键拉取并合并（默认启用），用于首次使用或书源被清空后恢复。</summary>
        private void RestoreBuiltinSources()
        {
            if (_restoringBuiltin) return;
            _restoringBuiltin = true;
            ShowToast(L10n.T("正在导入内置书源…"));
            Task.Run(async () =>
            {
                try
                {
                    int n = await ReaderLibrary.RestoreBuiltinSourcesAsync().ConfigureAwait(false);
                    RunOnUiThread(() =>
                    {
                        _sources = ReaderLibrary.Sources();
                        if (_srcList != null) RenderSourceList(_srcList);
                        ShowToast(n > 0 ? (L10n.T("已导入/更新 ") + n + L10n.T(" 个内置书源"))
                                        : L10n.T("内置书源导入失败，请检查网络后点「恢复内置」重试"));
                    });
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() => ShowToast(L10n.T("导入失败：") + ex.Message));
                }
                finally { _restoringBuiltin = false; }
            });
        }

        /* ============================================================
         *  发现（explore）
         * ============================================================ */
        private readonly List<Book> _exBooks = new List<Book>();

        private View BuildExplorePage()
        {
            _explorePage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _explorePage.SetBackgroundColor(new Color(PageBg));
            _explorePage.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(16), 0, Dp(16), 0);
            head.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(56));

            var back = new TextView(this) { Text = "‹", TextSize = 26 };
            back.SetTextColor(new Color(TextMain));
            back.Gravity = GravityFlags.Center;
            back.SetPadding(Dp(10), 0, Dp(10), 0);
            back.Click += (s, e) => CloseExplore();
            head.AddView(back, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            var t = new TextView(this) { Text = L10n.T("解析"), TextSize = 20 };
            t.SetTextColor(new Color(TextMain));
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            _exTitle = new TextView(this) { Text = "", TextSize = 11.5f };
            _exTitle.SetTextColor(new Color(TextSub));
            _exTitle.SetMaxLines(1);
            _exTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            head.AddView(_exTitle, new LinearLayout.LayoutParams(Dp(150), ViewGroup.LayoutParams.WrapContent));
            _explorePage.AddView(head);

            _exBody = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _exBody.SetPadding(Dp(14), 0, Dp(14), Dp(26));
            var sv = new ScrollView(this);
            sv.AddView(_exBody);
            _explorePage.AddView(sv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));
            RenderParseIntro();
            return _explorePage;
        }

        private void OpenExplore()
        {
            _shelfPage.Visibility = ViewStates.Gone;
            _explorePage.Visibility = ViewStates.Visible;
            RenderParseIntro();
        }

        /* ---------------- 在线小说链接解析 ---------------- */

        private void RenderParseIntro()
        {
            if (_exBody == null) return;
            _exBody.RemoveAllViews();
            _parseChecks.Clear();
            _parsed = null;

            var intro = new TextView(this)
            {
                Text = L10n.T("粘贴小说目录页或详情页链接，自动识别站点规则并列出章节；勾选后按章下载。"),
                TextSize = 12.5f
            };
            intro.SetTextColor(new Color(TextSub));
            intro.SetPadding(Dp(2), Dp(8), Dp(2), Dp(12));
            _exBody.AddView(intro);

            _parseUrl = new EditText(this)
            {
                Hint = L10n.T("例如：https://example.com/book/123/"),
                TextSize = 14,
                Background = RoundBg(CardBg, 12)
            };
            _parseUrl.SetTextColor(new Color(TextMain));
            _parseUrl.SetPadding(Dp(12), Dp(11), Dp(12), Dp(11));
            // 不在获得焦点时自动粘剪贴板：用户可能正在输入，自动粘贴会把剪贴板里的
            // 任意文字插到链接前面，导致解析失败。需要粘贴时用户自己长按即可。
            _exBody.AddView(_parseUrl, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var go = new TextView(this) { Text = L10n.T("解析链接"), TextSize = 15 };
            go.Gravity = GravityFlags.Center;
            go.SetTextColor(Color.White);
            go.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            go.Background = RoundBg(Accent, 12);
            var gop = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(44));
            gop.TopMargin = Dp(10);
            go.LayoutParameters = gop;
            go.Click += (s, e) => ParseUrlAction();
            _exBody.AddView(go);

            _parseStatus = new TextView(this)
            {
                Text = L10n.T("尚未解析"),
                TextSize = 12
            };
            _parseStatus.SetTextColor(new Color(TextSub));
            _parseStatus.SetPadding(Dp(2), Dp(12), Dp(2), Dp(8));
            _exBody.AddView(_parseStatus);
            _exTitle.Text = "";
        }

        private async void ParseUrlAction()
        {
            if (_parsing) return;
            var url = NovelAnalyzer.NormalizeUrl(_parseUrl?.Text);
            if (string.IsNullOrEmpty(url))
            {
                ShowToast(L10n.T("请输入有效的 http/https 链接"));
                return;
            }
            _parsing = true;
            _parseStatus.Text = L10n.T("正在解析…");
            try
            {
                var result = await NovelAnalyzer.ParseAsync(url, msg => RunOnUiThread(() =>
                {
                    if (_parseStatus != null) _parseStatus.Text = msg;
                }));
                if (result == null || result.Chapters.Count == 0)
                {
                    _parseStatus.Text = result == null ? L10n.T("解析失败") : result.Message;
                    ShowToast(_parseStatus.Text);
                    return;
                }
                _parsed = result;
                RenderParsedChapters();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("NovelParse", "解析异常: " + ex);
                _parseStatus.Text = L10n.T("解析失败：") + ex.Message;
                ShowToast(_parseStatus.Text);
            }
            finally { _parsing = false; }
        }

        private void RenderParsedChapters()
        {
            if (_exBody == null || _parsed == null) return;
            // RenderParseIntro 会清空状态；先保存解析结果，恢复后再渲染章节列表
            var parsed = _parsed;
            RenderParseIntro();
            _parsed = parsed;
            _parseUrl.Text = parsed.Book.BookUrl;
            _parseChecks.Clear();

            var head = new TextView(this)
            {
                Text = parsed.Book.Name + L10n.T(" · 共 ") + parsed.Chapters.Count + L10n.T(" 章 · ") + parsed.SourceName,
                TextSize = 13
            };
            head.SetTextColor(new Color(TextMain));
            head.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.SetPadding(Dp(2), Dp(4), Dp(2), Dp(2));
            _exBody.AddView(head);

            var hint = new TextView(this)
            {
                Text = L10n.T("点击章节名预览正文"),
                TextSize = 12
            };
            hint.SetTextColor(new Color(TextSub));
            hint.SetPadding(Dp(2), Dp(0), Dp(2), Dp(8));
            _exBody.AddView(hint);

            var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            actions.SetPadding(Dp(0), Dp(0), Dp(0), Dp(6));
            actions.AddView(ParseAction(L10n.T("全选"), v => SetAllParseChapters(1)));
            actions.AddView(ParseAction(L10n.T("全不选"), v => SetAllParseChapters(0)));
            actions.AddView(ParseAction(L10n.T("反选"), v => SetAllParseChapters(-1)));
            actions.AddView(ParseAction(L10n.T("加载更多"), v => LoadMoreParseChapters()));
            _exBody.AddView(actions);

            for (int i = 0; i < _parsed.Chapters.Count; i++)
                _exBody.AddView(ParseChapterRow(i, _parsed.Chapters[i]));

            var download = new TextView(this) { Text = L10n.T("下载选中章节"), TextSize = 15 };
            download.Gravity = GravityFlags.Center;
            download.SetTextColor(Color.White);
            download.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            download.Background = RoundBg(Accent, 12);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46));
            lp.TopMargin = Dp(12);
            download.LayoutParameters = lp;
            download.Click += (s, e) => DownloadParsedChapters();
            _exBody.AddView(download);

            _parseStatus.Text = _parsed.Message;
            _exTitle.Text = _parsed.SourceName;
        }

        private TextView ParseAction(string text, Action<View> onClick)
        {
            var v = Pill(text, onClick);
            var lp = v.LayoutParameters as LinearLayout.LayoutParams;
            if (lp != null)
            {
                lp.Width = 0;
                lp.Weight = 1f;
                lp.LeftMargin = Dp(3);
                lp.RightMargin = Dp(3);
            }
            return v;
        }

        private View ParseChapterRow(int index, Chapter ch)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.Background = RoundBg(CardBg, 10);
            row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
            var lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.BottomMargin = Dp(6);
            row.LayoutParameters = lp;

            // CheckBox 自己处理点击（默认会切换状态），不要再手工反转，否则等于没点
            var cb = new CheckBox(this) { Checked = true, Focusable = false, FocusableInTouchMode = false };
            row.AddView(cb, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            _parseChecks.Add(cb);

            var tv = new TextView(this)
            {
                Text = (index + 1) + ". " + (ch.Title ?? ""),
                TextSize = 13.5f
            };
            tv.SetTextColor(new Color(Accent));
            tv.PaintFlags = tv.PaintFlags | PaintFlags.UnderlineText;
            tv.SetMaxLines(2);
            tv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            tv.LayoutParameters = new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f) { Gravity = GravityFlags.CenterVertical };
            tv.Click += (s, e) => PreviewParsedChapter(ch);
            row.AddView(tv);
            // 点标题预览；点整行空白处仍切换勾选，方便批量选择。
            row.Click += (s, e) => { cb.Checked = !cb.Checked; };
            return row;
        }

        private async void PreviewParsedChapter(Chapter ch)
        {
            if (ch == null || _previewing) return;
            _previewing = true;
            if (_parseStatus != null) _parseStatus.Text = L10n.T("正在读取章节预览…");
            try
            {
                var pv = await NovelAnalyzer.ChapterPreviewAsync(
                    _parsed == null ? null : _parsed.Source, ch).ConfigureAwait(true);

                var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
                box.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));

                var meta = new TextView(this) { TextSize = 13 };
                meta.SetTextColor(new Color(TextMain));
                var completeness = pv.Warnings.Count == 0
                    ? L10n.T("完整性：未发现截断或噪声迹象，已读到最后一页")
                    : L10n.T("完整性：存在可疑迹象，请核对");
                meta.Text = L10n.T("已读取 ") + pv.PageCount + L10n.T(" 页同章内容 · ") +
                            (pv.Text ?? "").Length + L10n.T(" 字") + "\n" + completeness;
                if (pv.Warnings.Count > 0)
                    meta.Text += "\n" + string.Join("\n", pv.Warnings.ToArray());
                box.AddView(meta);

                var bodyText = pv.Text ?? "";
                if (bodyText.Length > ChapterPreviewLimit)
                {
                    bodyText = bodyText.Substring(0, ChapterPreviewLimit) + "\n…\n" +
                               L10n.T("预览仅显示前 ") + ChapterPreviewLimit + L10n.T(" 字，下载会保存全文");
                }
                var body = new TextView(this) { Text = bodyText, TextSize = 13.5f };
                body.SetTextColor(new Color(TextMain));
                body.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
                var scroll = new ScrollView(this);
                scroll.SetBackgroundDrawable(RoundBg(CardBg, 8));
                scroll.AddView(body);
                box.AddView(scroll, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, 0, 1f));

                var b = L10n.Dialog(this);
                b.SetTitle(ch.Title ?? L10n.T("章节预览"));
                b.SetView(box);
                b.SetPositiveButton(L10n.T("关闭"), (s, e) => { });
                b.Create().Show();
            }
            catch (Exception ex)
            {
                ShowToast(L10n.T("预览失败：") + ex.Message);
            }
            finally
            {
                _previewing = false;
                if (_parseStatus != null)
                    _parseStatus.Text = _parsed == null ? "" : _parsed.Message;
            }
        }

        private void SetAllParseChapters(int mode)
        {
            if (_parsed == null) return;
            for (int i = 0; i < _parseChecks.Count && i < _parsed.Chapters.Count; i++)
            {
                if (mode == 1) _parseChecks[i].Checked = true;
                else if (mode == 0) _parseChecks[i].Checked = false;
                else _parseChecks[i].Checked = !_parseChecks[i].Checked;
            }
        }

        private async void LoadMoreParseChapters()
        {
            if (_parsed == null || !_parsed.HasMore || _parsing) return;
            _parsing = true;
            _parseStatus.Text = L10n.T("正在加载更多章节…");
            try
            {
                var old = _parsed.Chapters.Count;
                await NovelAnalyzer.LoadMoreAsync(_parsed, msg => RunOnUiThread(() =>
                {
                    if (_parseStatus != null) _parseStatus.Text = msg;
                }));
                if (_parsed.Chapters.Count > old) RenderParsedChapters();
                else ShowToast(L10n.T("没有更多章节了"));
            }
            catch (Exception ex) { ShowToast(L10n.T("加载失败：") + ex.Message); }
            finally { _parsing = false; }
        }

        private void DownloadParsedChapters()
        {
            if (_parsed == null) { ShowToast(L10n.T("请先解析链接")); return; }
            var selected = new List<Chapter>();
            for (int i = 0; i < _parseChecks.Count && i < _parsed.Chapters.Count; i++)
            {
                if (_parseChecks[i].Checked) selected.Add(_parsed.Chapters[i]);
            }
            if (selected.Count == 0) { ShowToast(L10n.T("至少选择一个章节")); return; }

            try
            {
                var task = AhoChatA.Vod.DownloadCenter.QueueNovelChapters(
                    _parsed.Book.Name,
                    _parsed.Source == null ? "" : _parsed.Source.BookSourceUrl,
                    selected,
                    _parsed.SourceName);
                ShowToast(L10n.T("已加入下载队列：") + selected.Count + L10n.T(" 章，进度见「服务 → 媒体 → 下载」"));
            }
            catch (Exception ex) { ShowToast(L10n.T("加入下载队列失败：") + ex.Message); }
        }

        private void CloseExplore()
        {
            _explorePage.Visibility = ViewStates.Gone;
            _shelfPage.Visibility = ViewStates.Visible;
            RenderShelf();
        }

        private TextView Hint(string text)
        {
            var v = new TextView(this) { Text = text, TextSize = 13 };
            v.SetTextColor(new Color(TextSub));
            v.Gravity = GravityFlags.Center;
            v.SetPadding(Dp(24), Dp(40), Dp(24), Dp(40));
            return v;
        }

        private View ExploreRow(string title, string sub, Action onClick)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.Background = RoundBg(CardBg, 12);
            row.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14));
            var lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.BottomMargin = Dp(8);
            row.LayoutParameters = lp;

            var t = new TextView(this) { Text = title, TextSize = 15 };
            t.SetTextColor(new Color(TextMain));
            t.SetMaxLines(1);
            t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            row.AddView(t);

            if (!string.IsNullOrWhiteSpace(sub))
            {
                var s2 = new TextView(this) { Text = sub, TextSize = 11.5f };
                s2.SetTextColor(new Color(TextSub));
                s2.SetPadding(0, Dp(3), 0, 0);
                s2.SetMaxLines(2);
                row.AddView(s2);
            }
            row.Click += (s, e) => onClick();
            return row;
        }

        private void RenderExploreSources()
        {
            _exBody.RemoveAllViews();
            _exTitle.Text = L10n.T("选择书源");
            _exSrc = null;
            _exCat = null;

            var all = ReaderLibrary.Sources();
            var list = all.FindAll(x => x.Enabled &&
                (!string.IsNullOrWhiteSpace(x.ExploreUrl) ||
                 (x.ExploreRule != null && !string.IsNullOrWhiteSpace(x.ExploreRule.BookList))));

            if (list.Count == 0)
            {
                _exBody.AddView(Hint(L10n.T("还没有带「发现」规则的书源\n\n导入 legado 书源后，这里会列出它的分类")));
                return;
            }
            foreach (var src in list)
            {
                var c = src;
                var name = string.IsNullOrWhiteSpace(c.BookSourceName)
                    ? ReaderLibrary.HostOf(c.BookSourceUrl) : c.BookSourceName;
                _exBody.AddView(ExploreRow(name, c.BookSourceGroup ?? "", () => RenderExploreCategories(c)));
            }
        }

        private void RenderExploreCategories(BookSource src)
        {
            _exSrc = src;
            _exCat = null;
            var cats = ReaderLibrary.ExploreCategories(src);
            _exTitle.Text = src.BookSourceName;
            _exBody.RemoveAllViews();

            if (cats.Count == 0)
            {
                _exBody.AddView(Hint(L10n.T("该书源没有可用的「发现」分类")));
                return;
            }
            foreach (var cat in cats)
            {
                var c = cat;
                _exBody.AddView(ExploreRow(c.Title, "", () => RenderExploreBooks(src, c, 1)));
            }
        }

        private async void RenderExploreBooks(BookSource src, ExploreCategory cat, int page)
        {
            _exSrc = src;
            _exCat = cat;
            _exPage = page;
            _exTitle.Text = src.BookSourceName + " · " + cat.Title;

            if (page == 1)
            {
                _exBooks.Clear();
                _exBody.RemoveAllViews();
                _exBody.AddView(Hint(L10n.T("正在加载…")));
            }

            List<Book> got;
            try { got = await ReaderLibrary.ExploreAsync(src, cat, page); }
            catch (Exception ex) { got = new List<Book>(); ShowToast(L10n.T("加载失败：") + ex.Message); }

            if (page == 1 && got.Count == 0)
            {
                _exBody.RemoveAllViews();
                _exBody.AddView(Hint(L10n.T("这个分类没有取到书\n可能是书源规则失效或该分类为空")));
                return;
            }
            _exBooks.AddRange(got);
            RebuildExploreBookList(got.Count);
        }

        private void RebuildExploreBookList(int lastCount)
        {
            _exBody.RemoveAllViews();
            foreach (var b in _exBooks)
            {
                var book = b;
                var sub = (string.IsNullOrWhiteSpace(book.Author) ? "" : book.Author);
                if (!string.IsNullOrWhiteSpace(book.LastChapter))
                    sub += (sub.Length > 0 ? " · " : "") + L10n.T("最新：") + book.LastChapter;
                _exBody.AddView(ExploreRow(book.Name, sub, () => ShowBookDetail(book)));
            }
            if (lastCount >= 20)
            {
                int next = _exPage + 1;
                _exBody.AddView(ExploreRow(L10n.T("加载更多…"), "", () => RenderExploreBooks(_exSrc, _exCat, next)));
            }
        }

        private void ShowBookDetail(Book b)
        {
            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var name = new TextView(this) { Text = b.Name ?? "", TextSize = 17 };
            name.SetTextColor(new Color(ThText));
            name.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            name.SetPadding(Dp(22), Dp(4), Dp(22), Dp(4));
            sheet.AddView(name);

            var meta = new TextView(this)
            {
                Text = (string.IsNullOrWhiteSpace(b.Author) ? L10n.T("佚名") : b.Author)
                     + (string.IsNullOrWhiteSpace(b.Kind) ? "" : " · " + b.Kind),
                TextSize = 12
            };
            meta.SetTextColor(new Color(ThSub));
            meta.SetPadding(Dp(22), 0, Dp(22), Dp(8));
            sheet.AddView(meta);

            if (!string.IsNullOrWhiteSpace(b.Intro))
            {
                var intro = new TextView(this) { Text = b.Intro, TextSize = 12.5f };
                intro.SetTextColor(new Color(ThSub));
                intro.SetPadding(Dp(22), 0, Dp(22), Dp(10));
                intro.SetMaxLines(6);
                intro.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                var sc = new ScrollView(this);
                sc.AddView(intro);
                sheet.AddView(sc, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            }

            var add = new TextView(this) { Text = L10n.T("加入书架"), TextSize = 15 };
            add.Gravity = GravityFlags.Center;
            add.SetTextColor(Color.White);
            add.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            add.Background = RoundBg(Accent, 12);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46));
            lp.LeftMargin = Dp(20); lp.RightMargin = Dp(20); lp.TopMargin = Dp(6); lp.BottomMargin = Dp(8);
            add.LayoutParameters = lp;
            add.Click += (s, e) =>
            {
                try { detailDlg?.Dismiss(); } catch { }
                AddBookToShelf(b, true);
            };
            sheet.AddView(add);

            sheet.AddView(ActionRow(L10n.T("取消"), ThSub, () => { try { detailDlg?.Dismiss(); } catch { } }));

            detailDlg = BottomSheet(sheet, ViewGroup.LayoutParams.WrapContent);
            detailDlg.Show();
        }
        private Dialog detailDlg;

        /// <summary>加入书架（openText 为 true 时直接打开）。</summary>
        private void AddBookToShelf(Book b, bool openText)
        {
            var sid = "online_" + Guid.NewGuid().ToString("N");
            var list = ReaderLibrary.Shelf();
            list.Insert(0, new ShelfItem
            {
                Id = sid, Name = b.Name, Author = b.Author, Type = "online",
                SourceUrl = b.SourceUrl, BookUrl = b.BookUrl, TocUrl = b.TocUrl,
                CoverUrl = b.CoverUrl, Added = DateTime.Now
            });
            ReaderLibrary.SaveShelf(list);
            _shelf = ReaderLibrary.Shelf();
            RenderShelf();
            ShowToast(L10n.T("已加入书架"));
            if (openText) OpenBook(sid);
        }

        /* ============================================================
         *  换源 / 缓存
         * ============================================================ */
        private Dialog SimpleSheet(string title, string body)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.Background = TopRoundedBg(ThSheet, 18);
            box.AddView(SheetHandle(ThLine));
            var t = new TextView(this) { Text = title, TextSize = 14 };
            t.SetTextColor(new Color(ThText));
            t.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            t.SetPadding(Dp(22), Dp(4), Dp(22), Dp(6));
            box.AddView(t);
            var b = new TextView(this) { Text = body, TextSize = 12.5f };
            b.SetTextColor(new Color(ThSub));
            b.SetPadding(Dp(22), 0, Dp(22), Dp(22));
            box.AddView(b);
            var d = BottomSheet(box, ViewGroup.LayoutParams.WrapContent);
            d.Show();
            return d;
        }

        private async void ChangeSourceOf(ShelfItem item)
        {
            var name = DisplayName(item);
            var loading = SimpleSheet(L10n.T("换源"), L10n.T("正在其它书源中搜索「") + name + "」…");
            List<Book> found;
            try { found = await ReaderLibrary.SearchAllAsync(name); }
            catch { found = new List<Book>(); }
            try { loading.Dismiss(); } catch { }

            var hits = found.FindAll(x => !string.IsNullOrWhiteSpace(x.BookUrl));
            if (hits.Count == 0) { ShowToast(L10n.T("其它书源里没搜到这本书")); return; }

            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));
            var head = new TextView(this) { Text = L10n.T("换源 · 共 ") + hits.Count + L10n.T(" 个结果"), TextSize = 14 };
            head.SetTextColor(new Color(ThText));
            head.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.SetPadding(Dp(22), Dp(4), Dp(22), Dp(6));
            sheet.AddView(head);

            var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
            foreach (var h in hits)
            {
                var b = h;
                var src = ReaderLibrary.FindSource(b.SourceUrl);
                var srcName = src == null ? ReaderLibrary.HostOf(b.SourceUrl) : src.BookSourceName;
                var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
                row.SetPadding(Dp(22), Dp(11), Dp(22), Dp(11));
                var tt = new TextView(this) { Text = b.Name + "  ·  " + (string.IsNullOrWhiteSpace(b.Author) ? L10n.T("佚名") : b.Author), TextSize = 13.5f };
                tt.SetTextColor(new Color(ThText));
                tt.SetMaxLines(1);
                row.AddView(tt);
                var ss = new TextView(this) { Text = srcName ?? "", TextSize = 11 };
                ss.SetTextColor(new Color(ThSub));
                ss.SetPadding(0, Dp(2), 0, 0);
                row.AddView(ss);
                row.Click += (s2, e2) =>
                {
                    try { srcDlg?.Dismiss(); } catch { }
                    ReaderLibrary.ChangeSource(item.Id, b.SourceUrl, b.BookUrl, b.TocUrl, b.Name, b.Author);
                    _openBooks.Remove(item.Id);
                    _shelf = ReaderLibrary.Shelf();
                    RenderShelf();
                    ShowToast(L10n.T("已切换到「") + (srcName ?? L10n.T("其它书源")) + "」");
                };
                list.AddView(row);
            }
            var sv = new ScrollView(this);
            sv.AddView(list);
            sheet.AddView(sv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            srcDlg = BottomSheet(sheet, (int)(Resources.DisplayMetrics.HeightPixels * 0.6));
            srcDlg.Show();
        }
        private Dialog srcDlg;

        private async void CacheWholeBook(ShelfItem item)
        {
            Book book;
            if (!_openBooks.TryGetValue(item.Id, out book) || book == null)
            {
                string err;
                book = ReaderLibrary.GetBook(item, out err);
                if (book == null) { ShowToast(err ?? L10n.T("打开失败")); return; }
            }
            if (book.Type == "online" && (book.Chapters == null || book.Chapters.Count == 0))
            {
                var err = await ReaderLibrary.EnsureOnlineAsync(item, book);
                if (err != null) { ShowToast(err); return; }
            }
            if (book.Chapters == null || book.Chapters.Count == 0) { ShowToast(L10n.T("没有章节")); return; }

            // 走统一下载中心：整本缓存的进度 / 失败重试 / 落盘位置都能在「服务 → 媒体 → 下载」里看到
            AhoChatA.Vod.DownloadCenter.QueueNovel(item.Id, item.Name);
            ShowToast(L10n.T("已加入下载队列：整本缓存进度见「服务 → 媒体 → 下载」"));
        }

        private void ClearBookCache(ShelfItem item)
        {
            ReaderLibrary.ClearCache(item.Id);
            ShowToast(L10n.T("已清除该书缓存"));
        }

        /* ============================================================
         *  书签
         * ============================================================ */
        private void ShowBookmarks()
        {
            if (_curBook == null || string.IsNullOrEmpty(_curId))
            {
                ShowToast(L10n.T("先打开一本书"));
                return;
            }
            ShowBookmarksFor(new ShelfItem
            {
                Id = _curId,
                Name = _curBook.Name,
                Type = _curBook.Type,
                SourceUrl = _curBook.SourceUrl
            });
        }

        private void ShowBookmarksFor(ShelfItem item)
        {
            bool isCurrent = item.Id == _curId;
            var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
            sheet.Background = TopRoundedBg(ThSheet, 18);
            sheet.AddView(SheetHandle(ThLine));

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            head.SetPadding(Dp(22), Dp(2), Dp(22), Dp(6));
            var ttl = new TextView(this) { Text = L10n.T("书签"), TextSize = 15 };
            ttl.SetTextColor(new Color(ThText));
            ttl.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            head.AddView(ttl, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var nm = new TextView(this) { Text = DisplayName(item), TextSize = 11 };
            nm.SetTextColor(new Color(ThSub));
            nm.SetMaxLines(1);
            nm.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            head.AddView(nm, new LinearLayout.LayoutParams(Dp(150), ViewGroup.LayoutParams.WrapContent));
            sheet.AddView(head);

            if (isCurrent)
                sheet.AddView(ActionRow(L10n.T("在当前位置添加书签"), Accent, () =>
                {
                    try { markDlg?.Dismiss(); } catch { }
                    AddBookmarkHere();
                }));

            var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var marks = ReaderLibrary.Bookmarks(item.Id);
            if (marks.Count == 0)
            {
                list.AddView(Hint(L10n.T("还没有书签")));
            }
            else
            {
                foreach (var bm in marks)
                {
                    var m = bm;
                    var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
                    row.SetPadding(Dp(22), Dp(10), Dp(22), Dp(10));
                    row.Click += (s, e) =>
                    {
                        try { markDlg?.Dismiss(); } catch { }
                        if (isCurrent) NextOrPrev(m.ChapterIndex, m.Pct);
                        else OpenBookAt(item.Id, m.ChapterIndex, m.Pct);
                    };
                    row.LongClick += (s, e) =>
                    {
                        L10n.Dialog(this)
                            .SetTitle(L10n.T("删除书签"))
                            .SetMessage(string.IsNullOrWhiteSpace(m.Text) ? (m.ChapterTitle ?? "") : m.Text)
                            .SetPositiveButton(L10n.T("删除"), (s2, e2) =>
                            {
                                ReaderLibrary.DeleteBookmark(item.Id, m.Id);
                                try { markDlg?.Dismiss(); } catch { }
                                ShowBookmarksFor(item);
                            })
                            .SetNegativeButton(L10n.T("取消"), (s2, e2) => { })
                            .Show();
                    };

                    var line1 = new TextView(this) { Text = m.ChapterTitle ?? (L10n.T("第 ") + (m.ChapterIndex + 1) + L10n.T(" 章")), TextSize = 13 };
                    line1.SetTextColor(new Color(ThText));
                    line1.SetMaxLines(1);
                    line1.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    row.AddView(line1);

                    var line2 = new TextView(this)
                    {
                        Text = (string.IsNullOrWhiteSpace(m.Text) ? "" : m.Text + "  ·  ")
                             + m.Created.ToString("MM-dd HH:mm"),
                        TextSize = 11
                    };
                    line2.SetTextColor(new Color(ThSub));
                    line2.SetMaxLines(1);
                    line2.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    row.AddView(line2);
                    list.AddView(row);
                }
            }
            var sv = new ScrollView(this);
            sv.AddView(list);
            sheet.AddView(sv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));
            sheet.AddView(ActionRow(L10n.T("关闭"), ThSub, () => { try { markDlg?.Dismiss(); } catch { } }));

            markDlg = BottomSheet(sheet, (int)(Resources.DisplayMetrics.HeightPixels * 0.62));
            markDlg.Show();
        }
        private Dialog markDlg;

        private void AddBookmarkHere()
        {
            if (_curBook == null || _curBook.Chapters == null || _ci >= _curBook.Chapters.Count) return;
            string snippet = "";
            try
            {
                var txt = _curBook.Chapters[_ci].Content;
                if (!string.IsNullOrEmpty(txt))
                {
                    int off = (int)(txt.Length * Math.Max(0, Math.Min(1, _pct)));
                    int st = Math.Max(0, Math.Min(off, Math.Max(0, txt.Length - 40)));
                    snippet = txt.Substring(st, Math.Min(40, txt.Length - st)).Replace("\n", " ").Trim();
                }
            }
            catch { }
            ReaderLibrary.AddBookmark(_curId, _ci, _curBook.Chapters[_ci].Title, _pct, snippet);
            ShowToast(L10n.T("已添加书签"));
        }

        private async void OpenBookAt(string id, int chapter, double pct)
        {
            await OpenBookInternal(id, chapter, pct);
        }

        /* ============================================================
         *  朗读（TTS）
         * ============================================================ */
        private void ToggleTts()
        {
            if (_ttsOn) { StopTts(); return; }
            if (_curBook == null || _curBook.Chapters == null || _ci >= _curBook.Chapters.Count)
            {
                ShowToast(L10n.T("没有可朗读的内容"));
                return;
            }
            var text = _curBook.Chapters[_ci].Content;
            if (string.IsNullOrWhiteSpace(text)) { ShowToast(L10n.T("本章还没有正文")); return; }

            if (_tts == null)
            {
                _tts = new Android.Speech.Tts.TextToSpeech(this, new TtsInit(this));
                _ttsPending = text;
                ShowToast(L10n.T("正在准备朗读…"));
            }
            else if (!_ttsReady)
            {
                _ttsPending = text;
            }
            else
            {
                SpeakText(text);
            }
        }

        internal void SpeakText(string text)
        {
            try
            {
                _tts.Stop();
                _tts.SetLanguage(Java.Util.Locale.SimplifiedChinese);
                _ttsOn = true;
                // 按标点切成 <=600 字的片段排队，避免超过引擎上限
                int i = 0;
                while (i < text.Length)
                {
                    int len = Math.Min(600, text.Length - i);
                    int cut = len;
                    for (int k = i + len - 1; k > i; k--)
                    {
                        char c = text[k];
                        if (c == '。' || c == '！' || c == '？' || c == '\n' || c == '；')
                        {
                            cut = k - i + 1;
                            break;
                        }
                    }
                    var piece = text.Substring(i, cut).Replace("\n", " ").Trim();
                    if (piece.Length > 0)
                    {
                        var mode = Android.Speech.Tts.QueueMode.Add;
                        _tts.Speak(piece, mode, null, Guid.NewGuid().ToString());
                    }
                    i += cut;
                }
                ShowToast(L10n.T("开始朗读"));
            }
            catch (Exception ex)
            {
                _ttsOn = false;
                ShowToast(L10n.T("朗读失败：") + ex.Message);
            }
        }

        private void StopTts()
        {
            _ttsOn = false;
            _ttsPending = null;
            try { _tts?.Stop(); } catch { }
            ShowToast(L10n.T("已停止朗读"));
        }

        internal sealed class TtsInit : Java.Lang.Object, Android.Speech.Tts.TextToSpeech.IOnInitListener
        {
            private readonly ReaderActivity _a;
            public TtsInit(ReaderActivity a) { _a = a; }
            public void OnInit(Android.Speech.Tts.OperationResult status)
            {
                _a.RunOnUiThread(() =>
                {
                    if (status != Android.Speech.Tts.OperationResult.Success)
                    {
                        _a.ShowToast(L10n.T("朗读引擎初始化失败"));
                        return;
                    }
                    _a._ttsReady = true;
                    var pending = _a._ttsPending;
                    _a._ttsPending = null;
                    if (pending != null) _a.SpeakText(pending);
                });
            }
        }

        /* ============================================================
         *  小工具
         * ============================================================ */
        private TextView Pill(string text, Action<View> onClick)
        {
            var t = new TextView(this) { Text = text, TextSize = 12 };
            t.SetTextColor(new Color(ChipText));
            t.Gravity = GravityFlags.Center;
            t.SetPadding(Dp(11), Dp(7), Dp(11), Dp(7));
            t.Background = RoundBg(ChipBg, 15);
            var lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            lp.LeftMargin = Dp(5);
            t.LayoutParameters = lp;
            t.Click += (s, e) => onClick(t);
            return t;
        }

        private TextView BarText(string text, float size, Action<View> onClick)
        {
            var t = new TextView(this) { Text = text, TextSize = size };
            t.SetPadding(Dp(12), 0, Dp(12), 0);
            t.Gravity = GravityFlags.Center;
            t.Click += (s, e) => onClick(t);
            return t;
        }

        private Drawable RoundBg(int color, int radiusDp)
        {
            var d = new GradientDrawable();
            d.SetColor(new Color(color));
            d.SetCornerRadius(Dp(radiusDp));
            return d;
        }

        private Drawable TopRoundedBg(int color, int radiusDp)
        {
            var d = new GradientDrawable();
            d.SetColor(new Color(color));
            float r = Dp(radiusDp);
            d.SetCornerRadii(new[] { r, r, r, r, 0f, 0f, 0f, 0f });
            return d;
        }

        private Dialog BottomSheet(View content, int height)
        {
            var d = new Dialog(this);
            d.RequestWindowFeature((int)WindowFeatures.NoTitle);
            d.SetContentView(content);
            d.SetCanceledOnTouchOutside(true);
            var w = d.Window;
            if (w != null)
            {
                w.SetBackgroundDrawableResource(Android.Resource.Color.Transparent);
                w.SetLayout(ViewGroup.LayoutParams.MatchParent, height);
                w.SetGravity(GravityFlags.Bottom);
            }
            return d;
        }

        private int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);

        internal int DpI(int v) => Dp(v);

        private static string Encode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void ShowToast(string msg)
        {
            L10n.Toast(this, msg, ToastLength.Short).Show();
        }

        /// <summary>正文加载完成 → 恢复本章滚动位置。</summary>
        private sealed class ReaderClient : WebViewClient
        {
            private readonly ReaderActivity _a;
            public ReaderClient(ReaderActivity a) { _a = a; }
            public override void OnPageFinished(WebView view, string url)
            {
                base.OnPageFinished(view, url);
                try { _a.RestoreScroll(); } catch { }
            }
        }

        /// <summary>正文手势：区分点击与滚动，点击交给 OnReaderTap。</summary>
        private sealed class ReaderTouch : Java.Lang.Object, View.IOnTouchListener
        {
            private readonly ReaderActivity _a;
            private float _x0, _y0;
            private long _t0;
            private bool _moved;

            public ReaderTouch(ReaderActivity a) { _a = a; }

            public bool OnTouch(View v, MotionEvent e)
            {
                switch (e.ActionMasked)
                {
                    case MotionEventActions.Down:
                        _x0 = e.RawX; _y0 = e.RawY; _t0 = Android.OS.SystemClock.ElapsedRealtime();
                        _moved = false;
                        break;
                    case MotionEventActions.Move:
                        if (Math.Abs(e.RawX - _x0) > _a.DpI(8) || Math.Abs(e.RawY - _y0) > _a.DpI(8))
                            _moved = true;
                        break;
                    case MotionEventActions.Up:
                        long dt = Android.OS.SystemClock.ElapsedRealtime() - _t0;
                        if (!_moved && dt < 320) _a.OnReaderTap(e.GetX(), v.Width);
                        break;
                }
                return false;   // 不拦截，滚动仍由 WebView 处理
            }
        }
    }
}

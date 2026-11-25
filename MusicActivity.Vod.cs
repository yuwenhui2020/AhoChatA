// ============================================================
// 影音 · 在线影视（并入「视频」栏目，与本地视频聚合显示）
//
// 与「音乐 / 图片 / 歌单 / 喜欢」并列在顶栏；视频栏目同时展示：
//   · 本地视频（来自 MusicLibrary.Videos，角标「本地」）
//   · 在线影视（多渠道聚合，角标「N 源 / 类型」）
// 两者用角标区分。海报墙 3 列，在线部分跨源同名合并、海报磁盘缓存（CacheDir，系统可清理）。
// 大搜索框：250ms 防抖，本地片名即时过滤 + 联网 wd 搜索 + 本地预抓缓存过滤。
// 分类 / 源管理 / 观看历史 / 刷新；详情页 VodDetailActivity；播放走 ExoPlayer（直连已解析直链）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Newtonsoft.Json;
using AhoChatA.Music;
using AhoChatA.Vod;

namespace AhoChatA
{
    public partial class MusicActivity
    {
        private const int SecVod = 5;   // 仅保留常量，不再作为独立导航

        private LinearLayout _vodPage, _vodBar;
        private GridView _vodGrid;
        private TextView _vodEmpty, _vodCatBtn;
        private VideoTabAdapter _vodAdapter;
        private AlphabetIndexBar _videoIndex;   // 视频网格右侧 A-Z 索引
        private List<VodItem> _vodItems = new List<VodItem>();
        private List<VodCat> _vodCats = new List<VodCat>();
        private List<Song> _videoLocal = new List<Song>();
        private readonly Dictionary<string, Bitmap> _vodPosters =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Bitmap> _localThumbs =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private int _vodToken, _vodPageNum = 1;
        private bool _vodInited, _vodLoading, _vodRefreshing, _videoLocalLoaded;
        // 视频栏目内的子栏：本地视频(0) / 在线影视(1)。默认本地；无本地视频则隐藏子栏直接显示在线。
        private LinearLayout _videoSubBar;
        private TextView _videoSubLocal, _videoSubOnline;
        private int _videoSubTab = 0;
        // 默认展示「电视剧」，分类顺序固定 电视剧 → 电影 → 其它（与网页端一致）
        private string _vodCat = L10n.T("电视剧"), _vodKeyword = "";

        private View BuildVodPage()
        {
            _vodPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _vodPage.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _vodPage.SetBackgroundColor(new Color(PageBg));
            _vodPage.Visibility = ViewStates.Gone;

            // 子栏：本地视频 / 在线影视。默认本地；无本地视频时隐藏本行，直接展示在线。
            _videoSubBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _videoSubBar.SetPadding(Dp(12), Dp(8), Dp(12), Dp(2));
            _videoSubLocal = ChipBtn(L10n.T("本地视频"), () => SetVideoSubTab(0));
            _videoSubOnline = ChipBtn(L10n.T("在线影视"), () => SetVideoSubTab(1));
            _videoSubBar.AddView(_videoSubLocal);
            _videoSubBar.AddView(Sp(8));
            _videoSubBar.AddView(_videoSubOnline);
            _vodPage.AddView(_videoSubBar);

            // 工具行：本地数量 + 分类 / 源管理 / 观看历史 / 刷新
            _vodBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _vodBar.SetGravity(GravityFlags.CenterVertical);
            _vodBar.SetPadding(Dp(12), Dp(2), Dp(12), Dp(6));
            var scroller = new HorizontalScrollView(this);
            scroller.LayoutParameters = new LinearLayout.LayoutParams(-1, -2);
            var chips = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            chips.SetGravity(GravityFlags.CenterVertical);
            // 固定快捷入口：电视剧第一、电影第二
            chips.AddView(ChipBtn(L10n.T("电视剧"), () => { _vodCat = L10n.T("电视剧"); _vodCatBtn.Text = _vodCat; LoadVod(true); }));
            chips.AddView(Sp(6));
            chips.AddView(ChipBtn(L10n.T("电影"), () => { _vodCat = L10n.T("电影"); _vodCatBtn.Text = _vodCat; LoadVod(true); }));
            chips.AddView(Sp(6));
            _vodCatBtn = ChipBtn(_vodCat, ShowVodCats);
            chips.AddView(_vodCatBtn); chips.AddView(Sp(6));
            chips.AddView(ChipBtn(L10n.T("源管理"), ShowVodSources)); chips.AddView(Sp(6));
            chips.AddView(ChipBtn(L10n.T("观看历史"), ShowVodHistory)); chips.AddView(Sp(6));
            chips.AddView(ChipBtn(L10n.T("刷新"), () => RefreshVod())); chips.AddView(Sp(6));
            chips.AddView(ChipBtn(L10n.T("下一页"), () => LoadVod(false)));
            scroller.AddView(chips);
            _vodBar.AddView(scroller, new LinearLayout.LayoutParams(-1, -2));
            _vodPage.AddView(_vodBar);

            _vodEmpty = new TextView(this) { Text = L10n.T("正在加载在线影视…"), TextSize = 13, Gravity = GravityFlags.Center };
            _vodEmpty.SetTextColor(new Color(TextSub));
            _vodEmpty.SetPadding(Dp(16), Dp(40), Dp(16), Dp(40));
            _vodEmpty.LayoutParameters = new LinearLayout.LayoutParams(-1, -2);
            _vodPage.AddView(_vodEmpty);

            _vodGrid = new GridView(this) { NumColumns = 3 };
            _vodGrid.SetBackgroundColor(new Color(PageBg));
            _vodGrid.SetPadding(Dp(8), Dp(2), Dp(8), Dp(8));
            _vodGrid.SetHorizontalSpacing(Dp(6));
            _vodGrid.SetVerticalSpacing(Dp(8));
            var gridWrap = new FrameLayout(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f)
            };
            _vodGrid.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _vodAdapter = new VideoTabAdapter(this);
            _vodGrid.Adapter = _vodAdapter;
            _vodGrid.ItemClick += (s, e) =>
            {
                var it = _vodAdapter.ItemAt(e.Position);
                if (it == null) return;
                if (it.IsLocal) OpenLocalVideo(it.Local);
                else OpenVodDetail(it.Online);
            };
            gridWrap.AddView(_vodGrid);

            // 右侧 A-Z 索引条：本地视频 / 在线影视都按片名拼音首字母定位
            _videoIndex = new AlphabetIndexBar(this, ThemeManager.IsNight(this));
            var azLp = new FrameLayout.LayoutParams(Dp(26), -1, GravityFlags.Right | GravityFlags.CenterVertical);
            azLp.SetMargins(0, Dp(10), Dp(2), Dp(10));
            _videoIndex.LayoutParameters = azLp;
            _videoIndex.LetterSelected += letter => JumpToVideoLetter(letter);
            gridWrap.AddView(_videoIndex);
            _vodPage.AddView(gridWrap);
            return _vodPage;
        }

        /* ---------------- 进入分区 / 加载 ---------------- */

        private void SetVideoSubTab(int tab)
        {
            _videoSubTab = tab;
            SyncVideoSubBar();
            RenderVideoTab();
        }

        private void SyncVideoSubBar()
        {
            if (_videoSubBar == null) return;
            bool hasLocal = _videoLocal != null && _videoLocal.Count > 0;
            _videoSubBar.Visibility = hasLocal ? ViewStates.Visible : ViewStates.Gone;
            bool local = _videoSubTab == 0;
            _videoSubLocal.SetTextColor(new Color(local ? Accent : ChipText));
            _videoSubLocal.Background = RoundBg(local ? unchecked((int)0xFFE7F6FE) : ChipBg, 9);
            _videoSubOnline.SetTextColor(new Color(local ? ChipText : Accent));
            _videoSubOnline.Background = RoundBg(local ? ChipBg : unchecked((int)0xFFE7F6FE), 9);
        }

        private void OnEnterVideoTab()
        {
            _search.Hint = L10n.T("搜索片名（本地片库 + 在线多源）");
            if (!_videoLocalLoaded)
            {
                _videoLocalLoaded = true;
                try { MusicLibrary.Init(this); _videoLocal = MusicLibrary.Videos(); } catch { _videoLocal = new List<Song>(); }
            }
            // 每次进入视频默认显示本地；没有本地视频则回落到在线并隐藏子栏
            _videoSubTab = (_videoLocal != null && _videoLocal.Count > 0) ? 0 : 1;
            SyncVideoSubBar();
            if (_vodInited) { if (_vodItems.Count == 0) LoadVod(true); else RenderVideoTab(); return; }
            _vodInited = true;
            LoadVod(true);
            Task.Run(() =>
            {
                try
                {
                    // 只有本地还没有订阅缓存时才联网拉，避免每次进入分区都重刷导致缓存失效
                    if (VodConfig.Sites().Count <= 8) VodConfig.RefreshSubs();
                    VodApi.BuildCats(VodConfig.EnabledSites());
                }
                catch { }
                RunOnUiThread(() => { if (_section == SecVideo) LoadVod(true); });
            });
        }

        private string VodStatusLine()
        {
            var st = VodConfig.Status();
            string s = L10n.T("本地视频 ") + _videoLocal.Count + L10n.T("个 · 已加载 ") + st.CachedItems + L10n.T(" 部在线影视剧 / ");
            if (_vodLoading) s += L10n.T(" · 加载中…");
            if (_vodRefreshing) s += L10n.T(" · 正在刷新订阅");
            if (!string.IsNullOrWhiteSpace(_vodCat)) s += " · " + _vodCat;
            if (!string.IsNullOrWhiteSpace(st.Updated)) s += " · " + st.Updated;
            return s;
        }

        private void RefreshVod()
        {
            if (_vodRefreshing) return;
            _vodRefreshing = true;
            _vodEmpty.Text = L10n.T("正在刷新订阅源…");
            RunOnUiThread(() => ReloadList());
            Task.Run(() =>
            {
                try { VodConfig.RefreshSubs(); VodCache.ClearAll(); VodApi.BuildCats(VodConfig.EnabledSites()); }
                catch { }
                RunOnUiThread(() => { _vodRefreshing = false; _vodPageNum = 1; LoadVod(true); });
            });
        }

        private void LoadVod(bool first)
        {
            if (_vodLoading) return;
            int token = ++_vodToken;
            _vodLoading = true;
            if (first) { _vodPageNum = 1; _vodEmpty.Text = L10n.T("正在聚合本地与在线影视…"); }
            RunOnUiThread(() =>
            {
                if (_videoSubTab == 1) _vodEmpty.Visibility = ViewStates.Visible;
                ReloadList();
            });

            string kw = _vodKeyword, cat = _vodCat;
            int page = first ? 1 : _vodPageNum + 1;
            Task.Run(() =>
            {
                try
                {
                    List<VodItem> list;
                    if (!string.IsNullOrWhiteSpace(kw))
                    {
                        int ok;
                        list = VodApi.Search(kw, page, out ok);
                    }
                    else
                    {
                        bool cached;
                        list = VodApi.Home(page, cat, out cached);
                    }
                    if (token != _vodToken) return;
                    RunOnUiThread(() =>
                    {
                        if (token != _vodToken) return;
                        _vodLoading = false;
                        if (first) _vodItems = list ?? new List<VodItem>();
                        else
                        {
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var x in _vodItems) seen.Add(VodApi.NameKey(x.Name));
                            foreach (var x in list ?? new List<VodItem>())
                                if (seen.Add(VodApi.NameKey(x.Name))) _vodItems.Add(x);
                            _vodPageNum = page;
                        }
                        bool empty = _videoSubTab == 1 && _vodItems.Count == 0;
                        _vodEmpty.Visibility = empty ? ViewStates.Visible : ViewStates.Gone;
                        _vodEmpty.Text = empty
                            ? (string.IsNullOrWhiteSpace(kw) ? L10n.T("暂无内容，点「刷新」重试或到「源管理」启用更多渠道") : L10n.T("没有找到「") + kw + "」")
                            : "";
                        RenderVideoTab();
                        ReloadList();
                        // 首页后台继续预抓分页，本地搜索能覆盖更多片名
                        if (first && string.IsNullOrWhiteSpace(kw))
                            Task.Run(() => { try { VodApi.WarmPages(VodConfig.Load().MaxPages, null); } catch { } });
                    });
                }
                catch
                {
                    if (token != _vodToken) return;
                    RunOnUiThread(() =>
                    {
                        _vodLoading = false;
                        bool empty = _videoSubTab == 1 && _vodItems.Count == 0;
                        _vodEmpty.Visibility = empty ? ViewStates.Visible : ViewStates.Gone;
                        _vodEmpty.Text = L10n.T("加载失败，点「刷新」重试");
                        RenderVideoTab();
                        ReloadList();
                    });
                }
            });
        }

        /// <summary>点右侧字母：滚动到第一部片名首字/拼音首字母匹配的影片。</summary>
        private void JumpToVideoLetter(string letter)
        {
            if (_vodGrid == null || _vodAdapter == null || _vodAdapter.Count == 0) return;
            string target = (letter ?? "").ToUpperInvariant();
            for (int i = 0; i < _vodAdapter.Count; i++)
            {
                var it = _vodAdapter.ItemAt(i);
                if (it == null) continue;
                string title = it.IsLocal ? (it.Local == null ? "" : it.Local.DisplayTitle)
                                          : (it.Online == null ? "" : it.Online.Name);
                if (Pinyin.FirstLetter(title).ToString().Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    _vodGrid.SetSelection(i);
                    _videoIndex?.SetActiveLetter(target);
                    return;
                }
            }
            _videoIndex?.SetActiveLetter(null);
        }

        /// <summary>按当前子栏渲染网格：本地视频栏只显示本地，在线影视栏只显示在线。</summary>
        private void RenderVideoTab()
        {
            // 先按片名拼音 A-Z 排序，再构建展示项（在线接口返回顺序不稳定）
            _videoLocal.Sort((a, b) => Pinyin.Compare(a.DisplayTitle, b.DisplayTitle));
            _vodItems.Sort((a, b) => Pinyin.Compare(a.Name, b.Name));

            var combined = new List<VideoTabItem>();
            if (_videoSubTab == 0)
            {
                var q = (_query ?? "").Trim();
                foreach (var s in _videoLocal)
                {
                    if (!string.IsNullOrEmpty(q) &&
                        (s.DisplayTitle ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (s.FileName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    combined.Add(new VideoTabItem { IsLocal = true, Local = s });
                }
            }
            else
            {
                foreach (var it in _vodItems) combined.Add(new VideoTabItem { IsLocal = false, Online = it });
            }
            _vodAdapter.Replace(combined);
            if (_videoIndex != null)
                _videoIndex.Visibility = combined.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
            SyncVideoSubBar();

            // 本地视频栏仅展示本地片库：隐藏在线分类/刷新工具条与在线空态提示；
            // 在线影视栏才显示工具条，无结果时给出空态说明。
            bool online = _videoSubTab == 1;
            if (_vodBar != null) _vodBar.Visibility = online ? ViewStates.Visible : ViewStates.Gone;
            if (_vodEmpty != null)
            {
                if (online)
                {
                    if (!_vodLoading && _vodItems.Count == 0)
                    {
                        _vodEmpty.Text = L10n.T("暂无内容，点「刷新」重试或到「源管理」启用更多渠道");
                        _vodEmpty.Visibility = ViewStates.Visible;
                    }
                }
                else
                {
                    _vodEmpty.Visibility = ViewStates.Gone;
                }
            }
        }

        /// <summary>影视分区搜索：250ms 防抖，过期结果丢弃。</summary>
        private void ScheduleVodSearch(string q)
        {
            if (_section != SecVideo || !_vodInited) { _vodKeyword = ""; return; }
            int token = ++_vodToken;
            var kw = (q ?? "").Trim();
            _vodKeyword = kw;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250);
                    if (token != _vodToken) return;
                    RunOnUiThread(() => { _vodLoading = false; _vodPageNum = 1; LoadVod(true); });
                }
                catch { }
            });
        }

        /* ---------------- 弹窗：分类 / 源 / 历史 ---------------- */

        private void ShowVodCats()
        {
            try
            {
                if (_vodCats.Count == 0)
                {
                    var cached = VodCache.LoadCats(VodApi.CatsSig());
                    // 缓存还没有时用内置骨架，弹窗立刻可用，不再提示「请稍后再点」
                    _vodCats = cached ?? VodApi.DefaultCats();
                    Task.Run(() =>
                    {
                        try { VodApi.BuildCats(VodConfig.EnabledSites()); } catch { }
                    });
                }
                // 固定顺序：电视剧 → 电影 → 其它 → 最后「全部分类」
                var names = new List<string>();
                foreach (var c in _vodCats) names.Add(c.Name + (c.Hits > 0 ? " (" + c.Hits + ")" : ""));
                names.Add(L10n.T("全部分类"));
                var dlg = L10n.Dialog(this);
                dlg.SetTitle(L10n.T("选择分类"));
                dlg.SetItems(names.ToArray(), (s, e) =>
                {
                    _vodCat = e.Which < _vodCats.Count ? _vodCats[e.Which].Name : "";
                    _vodCatBtn.Text = string.IsNullOrEmpty(_vodCat) ? L10n.T("全部分类") : _vodCat;
                    LoadVod(true);
                });
                dlg.SetNegativeButton(L10n.T("取消"), (s, e) => { });
                dlg.Show();
            }
            catch { }
        }

        /// <summary>观看记录：与影片列表同样的海报墙（3 列对齐卡片，带封面），不再是文字菜单。</summary>
        private void ShowVodHistory()
        {
            try
            {
                var his = VodCache.History();
                if (his.Count == 0) { L10n.Toast(this, L10n.T("还没有观看记录"), ToastLength.Short).Show(); return; }
                // Xamarin.Android 的 GridView.VerticalSpacing / HorizontalSpacing 只有 getter，
                // 必须用 SetVerticalSpacing / SetHorizontalSpacing 方法（对象初始化器会报 CS0200）。
                var grid = new GridView(this) { NumColumns = 3 };
                grid.SetVerticalSpacing(8);
                grid.SetHorizontalSpacing(8);
                grid.SetPadding(10, 10, 10, 10);
                grid.Adapter = new VodHistoryAdapter(this, his);
                var builder = L10n.Dialog(this);
                builder.SetTitle(L10n.T("观看历史"));
                builder.SetView(grid);
                builder.SetNegativeButton(L10n.T("清空"), (s, e) => VodCache.ClearHistory());
                builder.SetNeutralButton(L10n.T("返回"), (s, e) => { });
                var dialog = builder.Create();
                grid.ItemClick += (s, e) =>
                {
                    try { dialog.Dismiss(); } catch { }
                    if (e.Position < 0 || e.Position >= his.Count) return;
                    var r = his[e.Position];
                    VodDetailActivity.Open(this, r.SiteKey, "", r.Name, r.Key.Split('|').Last(), r.EpisodeIndex,
                        (int)Math.Min(r.PositionMs, int.MaxValue));
                };
                dialog.Show();
            }
            catch { }
        }

        /// <summary>观看历史卡片适配器：封面（外源 CDN 海报，走磁盘缓存）+ 片名 + 续播集/进度。</summary>
        private sealed class VodHistoryAdapter : BaseAdapter<VodWatchRecord>
        {
            private readonly MusicActivity _a;
            private readonly List<VodWatchRecord> _list;

            public VodHistoryAdapter(MusicActivity a, List<VodWatchRecord> list) { _a = a; _list = list; }
            public override VodWatchRecord this[int position] { get { return _list[position]; } }
            public override int Count { get { return _list.Count; } }
            public override long GetItemId(int position) { return position; }

            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                var r = _list[position];
                var box = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                box.SetPadding(2, 2, 2, 2);
                int w = (int)(_a.Resources.DisplayMetrics.WidthPixels / 3.6);
                var iv = new ImageView(_a)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(w, (int)(w * 1.35))
                };
                iv.SetScaleType(ImageView.ScaleType.CenterCrop);
                iv.SetBackgroundColor(new Color(unchecked((int)0xFF1B1F24)));
                string tag = r.Key + "|" + position;
                iv.Tag = new Java.Lang.String(tag);
                if (!string.IsNullOrEmpty(r.Pic)) _a.LoadVodPoster(r.Pic, iv, tag);
                box.AddView(iv);

                var name = new TextView(_a) { Text = r.Name ?? "", TextSize = 12 };
                name.SetTextColor(new Color(unchecked((int)0xFF222222)));
                name.SetSingleLine(true);
                name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                box.AddView(name);

                var pos = r.PositionMs > 0
                    ? " · " + Song.FormatMs((int)Math.Min(r.PositionMs, int.MaxValue)) : "";
                var sub = new TextView(_a) { Text = (r.EpisodeName ?? "") + pos, TextSize = 10 };
                sub.SetTextColor(new Color(unchecked((int)0xFF8E9099)));
                sub.SetSingleLine(true);
                sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                box.AddView(sub);
                return box;
            }
        }

        /// <summary>多渠道源管理：模式切换、订阅增删、站点勾选、粘贴 TVBox JSON、刷新。</summary>
        private void ShowVodSources()
        {
            try
            {
                var cfg = VodConfig.Load();
                var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
                box.SetPadding(Dp(20), Dp(12), Dp(20), Dp(8));

                var tip = new TextView(this)
                {
                    Text = L10n.T("视频源来自 TVBox 类公开订阅配置。仅接入 type 0/1 的苹果CMS JSON 采集站；")
                         + L10n.T("多仓 urls[] 会自动展开。源由你自行启用或停用，失败自动回退内置实测源。"),
                    TextSize = 12
                };
                tip.SetTextColor(new Color(TextSub));
                box.AddView(tip);
                box.AddView(Sp(8));

                var modeRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                foreach (var m in new[] { L10n.T("内置"), L10n.T("订阅"), L10n.T("自定义"), "CMS" })
                {
                    string val = m == "内置" ? "builtin" : m == "订阅" ? "subs" : m == "自定义" ? "custom" : "cms";
                    var b = ChipBtn(m, () => { });
                    b.Text = m;
                    try
                    {
                        b.SetCompoundDrawablesWithIntrinsicBounds(
                            new IconDrawable(this, cfg.Mode == val ? IconKind.Dot : IconKind.Circle,
                                (int)(cfg.Mode == val ? Color.Rgb(0x07, 0xc1, 0x60) : Color.Rgb(0xbb, 0xbb, 0xbb)), Dp(10)),
                            null, null, null);
                        b.CompoundDrawablePadding = Dp(4);
                    }
                    catch { }
                    b.Click += (s, e) =>
                    {
                        cfg.Mode = val; VodConfig.Save(cfg);
                        // 切换模式后重建弹窗，让对应配置区（如 CMS 地址）立即出现
                        ShowVodSources();
                    };
                    modeRow.AddView(b); modeRow.AddView(Sp(6));
                }
                box.AddView(modeRow);
                box.AddView(Sp(10));

                // CMS 模式：编辑两套 CMS 服务（影视CMS:7777 / 海洋CMS:6666）的地址与令牌
                if (cfg.Mode == "cms")
                {
                    var cmsTitle = new TextView(this) { Text = L10n.T("CMS 服务（影视CMS:7777 + 海洋CMS:6666）"), TextSize = 14 };
                    cmsTitle.SetTypeface(null, TypefaceStyle.Bold);
                    box.AddView(cmsTitle);
                    var cmsHint = new TextView(this)
                    {
                        Text = L10n.T("影片列表/分类/搜索/详情/封面全部实时向这些 CMS 的对外 API 索取；同机运行填 127.0.0.1:端口，其它设备填其局域网 IP:端口。"),
                        TextSize = 11
                    };
                    cmsHint.SetTextColor(new Color(TextSub));
                    box.AddView(cmsHint);
                    box.AddView(Sp(4));

                    var servers = VodConfig.CmsServers();
                    while (servers.Count < 2) servers.Add(new VodCmsServer());
                    var hostEds = new List<EditText>();
                    var tokenEds = new List<EditText>();
                    for (int i = 0; i < servers.Count; i++)
                    {
                        var srv = servers[i];
                        var nameLbl = new TextView(this) { Text = (srv.Name.Length > 0 ? srv.Name : "CMS " + (i + 1)), TextSize = 12 };
                        nameLbl.SetTextColor(new Color(TextSub));
                        box.AddView(nameLbl);
                        var hostEt = new EditText(this) { Text = srv.Host, Hint = L10n.T("IP:端口，如 127.0.0.1:7777") };
                        hostEt.SetSingleLine(true);
                        box.AddView(hostEt); hostEds.Add(hostEt);
                        var tokenEt = new EditText(this) { Text = srv.Token, Hint = L10n.T("访问令牌（可选，留空=公开）") };
                        tokenEt.SetSingleLine(true);
                        box.AddView(tokenEt); tokenEds.Add(tokenEt);
                        box.AddView(Sp(6));
                    }
                    box.AddView(ChipBtn(L10n.T("保存 CMS 地址"), () =>
                    {
                        var list = new List<VodCmsServer>();
                        for (int i = 0; i < hostEds.Count; i++)
                        {
                            string h = (hostEds[i].Text ?? "").Trim();
                            if (h.Length == 0) continue;
                            list.Add(new VodCmsServer
                            {
                                Name = i == 0 ? "影视CMS" : (i == 1 ? "海洋CMS" : "CMS " + (i + 1)),
                                Host = h,
                                Token = (tokenEds[i].Text ?? "").Trim()
                            });
                        }
                        if (list.Count == 0) { L10n.Toast(this, L10n.T("请至少填写一套 CMS 地址"), ToastLength.Short).Show(); return; }
                        VodConfig.SetCmsServers(list);
                        try { VodCache.ClearAll(); } catch { }
                        L10n.Toast(this, L10n.T("已保存 ") + list.Count + L10n.T(" 套 CMS，点「刷新订阅」生效"), ToastLength.Short).Show();
                    }));
                    box.AddView(Sp(6));
                    box.AddView(ChipBtn(L10n.T("测试连接"), () =>
                    {
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                var sb = new System.Text.StringBuilder();
                                foreach (var sv in VodConfig.CmsServers())
                                {
                                    string u = VodConfig.HostRoot(VodConfig.CmsApiFor(sv.Host))
                                        + "/api/health" + (string.IsNullOrEmpty(sv.Token) ? "" : "?token=" + Uri.EscapeDataString(sv.Token));
                                    string t = VodApi.HttpGetText(u, 6000);
                                    bool ok = !string.IsNullOrEmpty(t) && t.Contains("\"ok\":true");
                                    sb.Append((sv.Name.Length > 0 ? sv.Name : "CMS") + " " + sv.Host + (ok ? " ✔" : " ✘") + "  ");
                                }
                                RunOnUiThread(() => L10n.Toast(this, sb.ToString(), ToastLength.Long).Show());
                            }
                            catch (Exception ex) { RunOnUiThread(() => L10n.Toast(this, L10n.T("测试失败：") + ex.Message, ToastLength.Long).Show()); }
                        });
                    }));
                    box.AddView(Sp(10));
                }

                var subTitle = new TextView(this) { Text = L10n.T("订阅渠道"), TextSize = 14 };
                subTitle.SetTypeface(null, TypefaceStyle.Bold);
                box.AddView(subTitle);
                foreach (var sub in cfg.Subs)
                {
                    var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    var cb = new CheckBox(this) { Checked = sub.Enabled };
                    var one = sub;
                    cb.CheckedChange += (s, e) => { one.Enabled = e.IsChecked; VodConfig.Save(cfg); };
                    row.AddView(cb);
                    var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                    col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    var n = new TextView(this) { Text = one.Name, TextSize = 13 };
                    n.SetTextColor(new Color(TextMain));
                    var u = new TextView(this) { Text = one.Url, TextSize = 10 };
                    u.SetTextColor(new Color(TextSub));
                    u.SetSingleLine(true);
                    u.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
                    col.AddView(n); col.AddView(u);
                    row.AddView(col);
                    var del = ChipBtn("×", () => { cfg.Subs.Remove(one); VodConfig.Save(cfg); ShowVodSources(); });
                    del.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
                    row.AddView(del);
                    box.AddView(row);
                }
                var addRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                addRow.AddView(ChipBtn(L10n.T("+ 添加订阅地址"), () => PromptText(L10n.T("添加 TVBox 订阅"), L10n.T("粘贴订阅 JSON 的 URL"), "", url =>
                {
                    if (!string.IsNullOrWhiteSpace(url))
                    { VodConfig.AddSub(new VodSub { Name = L10n.T("自定义订阅"), Url = url.Trim(), Enabled = true }); ShowVodSources(); }
                })));
                addRow.AddView(Sp(6));
                addRow.AddView(ChipBtn(L10n.T("粘贴配置 JSON"), () => PromptText(L10n.T("粘贴 TVBox 配置"), L10n.T("直接粘贴整份 JSON"), cfg.CustomJson, json =>
                {
                    cfg.CustomJson = json; cfg.Mode = "custom"; VodConfig.Save(cfg); ShowVodSources();
                }, true)));
                box.AddView(addRow);
                box.AddView(Sp(10));

                var siteTitle = new TextView(this) { Text = L10n.T("采集站点（勾选启用）"), TextSize = 14 };
                siteTitle.SetTypeface(null, TypefaceStyle.Bold);
                box.AddView(siteTitle);
                var sites = VodConfig.Sites();
                foreach (var st in sites.Take(40))
                {
                    var cb = new CheckBox(this) { Checked = st.Enabled, Text = st.Name + "  [" + st.Origin + "]", TextSize = 12 };
                    var one = st;
                    cb.CheckedChange += (s, e) => VodConfig.SetEnabled(one.Key, e.IsChecked);
                    box.AddView(cb);
                }
                box.AddView(Sp(10));
                var foot = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                foot.AddView(ChipBtn(L10n.T("刷新订阅"), () => { VodConfig.Invalidate(); RefreshVod(); }));
                foot.AddView(Sp(6));
                foot.AddView(ChipBtn(L10n.T("清空缓存"), () => { VodConfig.ClearCache(); LoadVod(true); }));
                box.AddView(foot);

                var sv = new ScrollView(this);
                sv.AddView(box);
                var dlg = L10n.Dialog(this);
                dlg.SetTitle(L10n.T("在线影视 · 源管理"));
                dlg.SetView(sv);
                dlg.SetNegativeButton(L10n.T("关闭"), (s, e) => { });
                dlg.Show();
            }
            catch (Exception ex) { L10n.Toast(this, L10n.T("源管理打开失败：") + ex.Message, ToastLength.Short).Show(); }
        }

        private void PromptText(string title, string hint, string initial, Action<string> onOk, bool multiline = false)
        {
            var input = new EditText(this) { Hint = hint, Text = initial ?? "" };
            input.SetSingleLine(!multiline);
            if (multiline) { input.SetMinLines(6); input.Gravity = GravityFlags.Top; }
            input.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
            var wrap = new FrameLayout(this);
            wrap.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
            wrap.AddView(input);
            var dlg = L10n.Dialog(this);
            dlg.SetTitle(title);
            dlg.SetView(wrap);
            dlg.SetPositiveButton(L10n.T("确定"), (s, e) => onOk(input.Text));
            dlg.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            dlg.Show();
        }

        private void OpenVodDetail(VodItem it)
        {
            if (it == null) return;
            VodDetailActivity.Open(this, it.SiteKey, it.Api, it.Name, it.Id, 0, 0);
        }

        private void OpenLocalVideo(Song s)
        {
            if (s == null) return;
            var i = new Intent(this, typeof(VideoActivity));
            i.PutExtra(VideoActivity.ExtraPath, s.Path);
            StartActivity(i);
        }

        /* ---------------- 海报加载（磁盘缓存，异步不卡列表） ---------------- */

        private void LoadVodPoster(string url, ImageView iv, string tag)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Bitmap bmp;
            if (_vodPosters.TryGetValue(url, out bmp)) { iv.SetImageBitmap(bmp); return; }
            iv.SetImageBitmap(null);
            Task.Run(() =>
            {
                try
                {
                    var bytes = VodApi.PosterBytes(url);
                    if (bytes == null || bytes.Length == 0) return;
                    var opt = new BitmapFactory.Options { InSampleSize = 2, InPurgeable = true };
                    var b = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, opt);
                    if (b == null) return;
                    RunOnUiThread(() =>
                    {
                        var cur = iv.Tag as Java.Lang.String;
                        if (cur == null || !string.Equals(cur.ToString(), tag, StringComparison.Ordinal)) return;
                        if (_vodPosters.Count > 180) _vodPosters.Clear();
                        _vodPosters[url] = b;
                        iv.SetImageBitmap(b);
                    });
                }
                catch { }
            });
        }

        private void LoadLocalVideoPoster(Song s, ImageView iv, string tag)
        {
            if (s == null) return;
            Bitmap bmp;
            if (_localThumbs.TryGetValue(s.Path, out bmp)) { iv.SetImageBitmap(bmp); return; }
            iv.SetImageBitmap(null);
            var path = s.Path; var t = tag;
            Task.Run(() =>
            {
                try
                {
                    var b = MusicLibrary.VideoThumb(s, 320);
                    if (b == null) return;
                    RunOnUiThread(() =>
                    {
                        if (_localThumbs.Count > 120) _localThumbs.Clear();
                        _localThumbs[path] = b;
                        var cur = iv.Tag as Java.Lang.String;
                        if (cur != null && cur.ToString() == t) iv.SetImageBitmap(b);
                    });
                }
                catch { }
            });
        }

        /* ---------------- 合并网格 Adapter ---------------- */

        private class VideoTabItem
        {
            public bool IsLocal;
            public Song Local;
            public VodItem Online;
        }

        private class VideoTabAdapter : BaseAdapter<VideoTabItem>
        {
            private readonly MusicActivity _a;
            private List<VideoTabItem> _items = new List<VideoTabItem>();
            public VideoTabAdapter(MusicActivity a) { _a = a; }
            public void Replace(List<VideoTabItem> items) { _items = items ?? new List<VideoTabItem>(); NotifyDataSetChanged(); }
            public VideoTabItem ItemAt(int i) { return (i >= 0 && i < _items.Count) ? _items[i] : null; }
            public override int Count => _items.Count;
            public override VideoTabItem this[int i] => _items[i];
            public override long GetItemId(int i) => i;

            private class Holder : Java.Lang.Object
            {
                public ImageView Cover;
                public TextView Title, Sub, Badge;
                public string Tag;
            }

            public override View GetView(int i, View cv, ViewGroup parent)
            {
                var it = _items[i];
                LinearLayout card;
                Holder h;
                if (cv is LinearLayout l && l.Tag is Holder old) { card = l; h = old; }
                else
                {
                    card = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                    card.SetBackgroundColor(new Color(CardBg));
                    card.Background = _a.RoundBg(CardBg, 10);
                    card.SetPadding(_a.Dp(5), _a.Dp(5), _a.Dp(5), _a.Dp(7));
                    var poster = new FrameLayout(_a);
                    poster.LayoutParameters = new LinearLayout.LayoutParams(-1, _a.Dp(148));
                    var iv = new ImageView(_a);
                    iv.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
                    iv.SetScaleType(ImageView.ScaleType.CenterCrop);
                    iv.SetBackgroundColor(new Color(ChipBg));
                    poster.AddView(iv);
                    var badge = new TextView(_a) { TextSize = 10 };
                    badge.SetTextColor(new Color(White));
                    badge.Background = _a.RoundBg(unchecked((int)0xAA000000), 5);
                    badge.SetPadding(_a.Dp(4), _a.Dp(2), _a.Dp(4), _a.Dp(2));
                    poster.AddView(badge, new FrameLayout.LayoutParams(-2, -2,
                        GravityFlags.Bottom | GravityFlags.Right) { RightMargin = _a.Dp(5), BottomMargin = _a.Dp(5) });
                    card.AddView(poster);
                    var t = new TextView(_a) { TextSize = 12 };
                    t.SetTextColor(new Color(TextMain));
                    t.SetSingleLine(true);
                    t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    t.SetPadding(_a.Dp(2), _a.Dp(6), _a.Dp(2), 0);
                    card.AddView(t);
                    var sub = new TextView(_a) { TextSize = 10 };
                    sub.SetTextColor(new Color(TextSub));
                    sub.SetSingleLine(true);
                    sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    sub.SetPadding(_a.Dp(2), _a.Dp(2), _a.Dp(2), 0);
                    card.AddView(sub);
                    h = new Holder { Cover = iv, Title = t, Sub = sub, Badge = badge };
                    card.Tag = h;
                }
                h.Tag = (it.IsLocal ? "L|" : "O|") + i;
                h.Cover.Tag = new Java.Lang.String(h.Tag);
                if (it.IsLocal)
                {
                    var s = it.Local;
                    h.Title.Text = s.DisplayTitle ?? System.IO.Path.GetFileNameWithoutExtension(s.Path ?? "");
                    h.Sub.Text = L10n.T("本地视频");
                    h.Badge.Text = L10n.T("本地");
                    h.Badge.Visibility = ViewStates.Visible;
                    _a.LoadLocalVideoPoster(s, h.Cover, h.Tag);
                }
                else
                {
                    var v = it.Online;
                    h.Title.Text = v.Name;
                    h.Sub.Text = string.IsNullOrWhiteSpace(v.Remarks)
                        ? (v.SiteName ?? "") : v.Remarks + (string.IsNullOrWhiteSpace(v.Year) ? "" : " · " + v.Year);
                    int n = v.Variants == null ? 0 : v.Variants.Count;
                    h.Badge.Text = n > 1 ? n + L10n.T(" 源") : (string.IsNullOrWhiteSpace(v.TypeName) ? "" : v.TypeName);
                    h.Badge.Visibility = string.IsNullOrEmpty(h.Badge.Text) ? ViewStates.Gone : ViewStates.Visible;
                    _a.LoadVodPoster(v.Pic, h.Cover, h.Tag);
                }
                return card;
            }
        }
    }
}

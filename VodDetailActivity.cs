// ============================================================
// 在线影视 · 详情页（原生 App）
//
// 封面 / 简介 / 同名多源站点切换 / 播放线路切换 / 剧集网格 / 续播。
// 点剧集把「整条线路」打包给 VideoActivity，播完自动下一集；
// 播放地址统一走本地回环代理（注入 UA/Referer + 重写 m3u8），见 WebServer.VodApi.cs。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Newtonsoft.Json;
using AhoChatA.Music;
using AhoChatA.Vod;

namespace AhoChatA
{
    [Activity(Label = "@string/act_vod", Exported = false, Theme = "@style/AppTheme.NoActionBar")]
    public class VodDetailActivity : BaseActivity
    {
        public const string ExtraSite = "vod_site";
        public const string ExtraApi = "vod_api";
        public const string ExtraName = "vod_name";
        public const string ExtraId = "vod_id";
        public const string ExtraIndex = "vod_index";
        public const string ExtraResume = "vod_resume";

        private const int Accent = unchecked((int)0xFF007AFF);
        private const int TextMain = unchecked((int)0xFF1C1C1E);
        private const int TextSub = unchecked((int)0xFF8E8E93);
        private const int PageBg = unchecked((int)0xFFF2F2F7);
        private const int CardBg = unchecked((int)0xFFFFFFFF);
        private const int ChipBg = unchecked((int)0xFFF2F2F7);
        private const int Line = unchecked((int)0xFFC6C6C8);

        private string _site, _api, _name, _id;
        private int _startIndex, _resumeMs;
        private VodItem _item;
        private List<VodSource> _sources = new List<VodSource>();
        private int _line;
        private int _castStartIndex;     // 长按剧集时从该集起投；顶栏按钮默认 0

        private ImageView _cover;
        private TextView _title, _meta, _desc, _sourceHint, _lineBtn, _status;
        private LinearLayout _variantRow;
        private GridView _epGrid;
        private EpisodeAdapter _epAdapter;
        private ProgressBar _loading;

        public static void Open(Context ctx, string site, string api, string name, string id, int episodeIndex, int resumeMs)
        {
            var i = new Intent(ctx, typeof(VodDetailActivity));
            i.PutExtra(ExtraSite, site ?? "");
            i.PutExtra(ExtraApi, api ?? "");
            i.PutExtra(ExtraName, name ?? "");
            i.PutExtra(ExtraId, id ?? "");
            i.PutExtra(ExtraIndex, episodeIndex);
            i.PutExtra(ExtraResume, resumeMs);
            ctx.StartActivity(i);
        }

        private int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);

        private GradientDrawable RoundBg(int color, int r)
        {
            var d = new GradientDrawable();
            d.SetColor(color);
            d.SetCornerRadius(Dp(r));
            return d;
        }

        private TextView Chip(string text, Action click)
        {
            var t = new TextView(this) { Text = text, TextSize = 12, Gravity = GravityFlags.Center };
            t.SetTextColor(new Color(Accent));
            t.Background = RoundBg(ChipBg, 12);
            t.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
            t.Click += (s, e) => click();
            return t;
        }

        protected override void OnCreate(Android.OS.Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            _site = Intent?.GetStringExtra(ExtraSite) ?? "";
            _api = Intent?.GetStringExtra(ExtraApi) ?? "";
            _name = Intent?.GetStringExtra(ExtraName) ?? "";
            _id = Intent?.GetStringExtra(ExtraId) ?? "";
            _startIndex = Intent?.GetIntExtra(ExtraIndex, 0) ?? 0;
            _resumeMs = Intent?.GetIntExtra(ExtraResume, 0) ?? 0;

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(new Color(PageBg));
            root.LayoutParameters = new LinearLayout.LayoutParams(-1, -1);

            // 顶栏
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            top.SetBackgroundColor(new Color(CardBg));
            top.SetPadding(Dp(16), Dp(6), Dp(16), Dp(6));
            var back = new TextView(this) { Text = L10n.T("‹ 返回"), TextSize = 15 };
            back.SetTextColor(new Color(Accent));
            back.SetPadding(Dp(6), Dp(6), Dp(18), Dp(6));
            back.Click += (s, e) => Finish();
            top.AddView(back);
            var head = new TextView(this) { Text = L10n.T("影视详情"), TextSize = 16 };
            head.SetTypeface(null, TypefaceStyle.Bold);
            head.SetTextColor(new Color(TextMain));
            top.AddView(head);
            var space = new View(this);
            space.LayoutParameters = new LinearLayout.LayoutParams(0, 1, 1f);
            top.AddView(space);
            var refresh = Chip(L10n.T("换源"), () => Reload(true));
            top.AddView(refresh);
            var cast = Chip(L10n.T("投放"), () => { _castStartIndex = 0; ShowCastPicker(null); });
            top.AddView(cast);
            top.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(56));
            root.AddView(top);

            // 上部信息：左封面 + 右文字
            var info = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            info.SetBackgroundColor(new Color(CardBg));
            info.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            _cover = new ImageView(this);
            _cover.SetScaleType(ImageView.ScaleType.CenterCrop);
            _cover.SetBackgroundColor(new Color(ChipBg));
            _cover.Background = RoundBg(ChipBg, 8);
            _cover.LayoutParameters = new LinearLayout.LayoutParams(Dp(112), Dp(158));
            info.AddView(_cover);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f) { LeftMargin = Dp(14) };
            _title = new TextView(this) { Text = _name, TextSize = 18 };
            _title.SetTypeface(null, TypefaceStyle.Bold);
            _title.SetTextColor(new Color(TextMain));
            col.AddView(_title);
            _meta = new TextView(this) { Text = L10n.T("正在加载…"), TextSize = 12 };
            _meta.SetTextColor(new Color(TextSub));
            _meta.SetPadding(0, Dp(6), 0, 0);
            col.AddView(_meta);
            _desc = new TextView(this) { Text = "", TextSize = 12 };
            _desc.SetTextColor(new Color(TextSub));
            _desc.SetPadding(0, Dp(8), 0, 0);
            _desc.SetMaxLines(6);
            _desc.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _desc.Click += (s, e) =>
            {
                if (_desc.MaxLines == 6) { _desc.SetMaxLines(100); _desc.Ellipsize = null; }
                else { _desc.SetMaxLines(6); _desc.Ellipsize = Android.Text.TextUtils.TruncateAt.End; }
            };
            col.AddView(_desc);
            info.AddView(col);
            root.AddView(info);

            // 多源站点 chips
            _variantRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _variantRow.SetGravity(GravityFlags.CenterVertical);
            _variantRow.SetPadding(Dp(16), Dp(8), Dp(16), Dp(4));
            var variants = new HorizontalScrollView(this);
            variants.LayoutParameters = new LinearLayout.LayoutParams(-1, -2);
            variants.AddView(_variantRow);
            root.AddView(variants);

            // 线路 + 状态
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetPadding(Dp(16), Dp(4), Dp(16), Dp(6));
            _lineBtn = Chip(L10n.T("线路"), ShowLines);
            bar.AddView(_lineBtn); bar.AddView(Sp(8));
            _status = new TextView(this) { Text = "", TextSize = 11 };
            _status.SetTextColor(new Color(TextSub));
            _status.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            bar.AddView(_status);
            root.AddView(bar);

            _loading = new ProgressBar(this) { Indeterminate = true };
            _loading.LayoutParameters = new LinearLayout.LayoutParams(Dp(28), Dp(28));
            _loading.Visibility = ViewStates.Gone;
            root.AddView(_loading);

            // 剧集网格
            _epGrid = new GridView(this) { NumColumns = 6 };
            _epGrid.SetBackgroundColor(new Color(PageBg));
            _epGrid.SetPadding(Dp(10), Dp(4), Dp(10), Dp(10));
            _epGrid.SetHorizontalSpacing(Dp(6));
            _epGrid.SetVerticalSpacing(Dp(6));
            _epGrid.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);
            _epAdapter = new EpisodeAdapter(this);
            _epGrid.Adapter = _epAdapter;
            _epGrid.ItemClick += (s, e) => PlayEpisode(e.Position);
            _epGrid.ItemLongClick += (s, e) =>
            {
                _castStartIndex = e.Position;
                ShowCastPicker(null);
            };
            root.AddView(_epGrid);

            SetContentView(root);
            Reload(false);
        }

        private View Sp(int w) => new View(this)
        { LayoutParameters = new LinearLayout.LayoutParams(Dp(w), 1) };

        private void Reload(bool force)
        {
            _loading.Visibility = ViewStates.Visible;
            _status.Text = L10n.T("正在读取影片信息与播放线路…");
            string site = _site, api = _api, name = _name, id = _id;
            Task.Run(() =>
            {
                try
                {
                    var item = VodApi.Detail(site, id, name);
                    if (item == null) throw new Exception("站点不存在");
                    if (!string.IsNullOrEmpty(api)) item.Api = api;
                    var sources = VodApi.ParseSources(item.PlayFrom, item.PlayUrl);
                    // 先秒显剧集网格，解析/预热放到后台，避免详情页卡 18 秒
                    RunOnUiThread(() =>
                    {
                        _item = item; _sources = sources; _line = 0;
                        _loading.Visibility = ViewStates.Gone;
                        Render();
                    });
                    if (sources.Count > 0)
                    {
                        int defLine = 0;
                        Task.Run(() =>
                        {
                            try
                            {
                                VodApi.ResolveEpisodes(sources[defLine]);
                                VodApi.WarmEpisodes(sources[defLine]);
                                RunOnUiThread(() =>
                                {
                                    if (_sources == sources)
                                    {
                                        _lineBtn.Text = _sources[defLine].Name + " · " + _sources[defLine].Episodes.Count + L10n.T(" 集");
                                        Render();
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() =>
                    {
                        _loading.Visibility = ViewStates.Gone;
                        _status.Text = L10n.T("加载失败：") + ex.Message + L10n.T("（可点右上「换源」重试）");
                    });
                }
            });
        }

        private void Render()
        {
            if (_item == null) return;
            _title.Text = _item.Name;
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(_item.Remarks)) bits.Add(_item.Remarks);
            if (!string.IsNullOrWhiteSpace(_item.Year)) bits.Add(_item.Year);
            if (!string.IsNullOrWhiteSpace(_item.Area)) bits.Add(_item.Area);
            if (!string.IsNullOrWhiteSpace(_item.TypeName)) bits.Add(_item.TypeName);
            if (!string.IsNullOrWhiteSpace(_item.Score)) bits.Add(L10n.T("评分 ") + _item.Score);
            bits.Add(_item.SiteName);
            _meta.Text = string.Join(" · ", bits);
            _desc.Text = string.IsNullOrWhiteSpace(_item.Desc)
                ? ((string.IsNullOrWhiteSpace(_item.Actor) ? L10n.T("暂无简介") : L10n.T("主演：") + _item.Actor)) 
                : (string.IsNullOrWhiteSpace(_item.Actor) ? _item.Desc : L10n.T("主演：") + _item.Actor + "\n" + _item.Desc);
            LoadCover(_item.Pic);

            _variantRow.RemoveAllViews();
            var variants = _item.Variants ?? new List<VodItem> { _item };
            foreach (var v in variants.Take(10))
            {
                var one = v;
                var c = Chip(v.SiteName, () =>
                {
                    _site = one.SiteKey; _api = one.Api; _id = one.Id; _name = one.Name;
                    Reload(false);
                });
                if (one.SiteKey == _site && one.Id == _id)
                { c.SetTextColor(new Color(unchecked((int)0xFFFFFFFF))); c.Background = RoundBg(Accent, 12); }
                _variantRow.AddView(c);
                _variantRow.AddView(Sp(6));
            }
            _lineBtn.Text = _sources.Count == 0 ? L10n.T("无线路")
                : (_sources[_line].Name + " · " + _sources[_line].Episodes.Count + L10n.T(" 集"));
            _status.Text = _sources.Count == 0
                ? L10n.T("该源没有可用播放地址，点右上「换源」切换其它站点")
                : L10n.T("共 ") + _sources.Count + L10n.T(" 条线路 · 点击集数开始播放（自动连播下一集）");
            _epAdapter.Replace(_sources.Count == 0 ? new List<VodEpisode>() : _sources[_line].Episodes);
            if (_startIndex > 0 && _startIndex < _epAdapter.Count) _epGrid.SetSelection(_startIndex);
            if (_resumeMs > 0) _status.Text = L10n.T("上次看到 ") + Song.FormatMs(_resumeMs) + " · " + _status.Text;
        }

        private void LoadCover(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Task.Run(() =>
            {
                try
                {
                    var bytes = VodApi.PosterBytes(url);
                    if (bytes == null) return;
                    var b = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length,
                        new BitmapFactory.Options { InSampleSize = 2 });
                    if (b == null) return;
                    RunOnUiThread(() => _cover.SetImageBitmap(b));
                }
                catch { }
            });
        }

        private void ShowLines()
        {
            if (_sources.Count == 0) return;
            var names = _sources.Select((s, i) => s.Name + " · " + s.Episodes.Count + L10n.T(" 集")
                + (s.DirectCount > 0 ? L10n.T(" (直链 ") + s.DirectCount + ")" : "")).ToArray();
            var dlg = L10n.Dialog(this);
            dlg.SetTitle(L10n.T("选择播放线路"));
            dlg.SetItems(names, (s, e) =>
            {
                _line = e.Which;
                _loading.Visibility = ViewStates.Visible;
                _status.Text = L10n.T("正在解析线路…");
                int which = _line;
                Task.Run(() =>
                {
                    try { VodApi.ResolveEpisodes(_sources[which]); } catch { }
                    RunOnUiThread(() =>
                    {
                        _loading.Visibility = ViewStates.Gone;
                        _lineBtn.Text = _sources[which].Name + " · " + _sources[which].Episodes.Count + L10n.T(" 集");
                        _epAdapter.Replace(_sources[which].Episodes);
                        _status.Text = L10n.T("共 ") + _sources.Count + L10n.T(" 条线路 · 点击集数开始播放");
                    });
                });
            });
            dlg.Show();
        }

        /* ---------------- 投放到 TV 端（发送真实直链，TV 自行读取并缓存） ---------------- */

        /// <summary>选择 TV：优先上次使用的地址，支持扫描局域网 / 手动输入。</summary>
        private void ShowCastPicker(List<TvDevice> scanned)
        {
            scanned = scanned ?? new List<TvDevice>();
            var map = new List<TvDevice>();
            var items = new List<string>();
            string last = VodCast.LastIp(this);
            if (!string.IsNullOrWhiteSpace(last)) { map.Add(new TvDevice { Ip = last, Name = L10n.T("上次 · ") + last }); items.Add(L10n.T("上次使用 · ") + last); }
            foreach (var d in scanned) { map.Add(d); items.Add(d.ToString()); }
            bool canScan = scanned.Count == 0;
            if (canScan) items.Add(L10n.T("扫描局域网 TV 端"));
            items.Add(L10n.T("手动输入 TV 地址"));

            var dlg = L10n.Dialog(this);
            dlg.SetTitle(L10n.T("投放到 TV 端（TV 自行读取真实链接）"));
            dlg.SetNegativeButton(L10n.T("取消"), (s, e) => { });
            dlg.SetItems(items.ToArray(), (s, e) =>
            {
                int idx = e.Which;
                if (idx < map.Count) { VodCast.SaveIp(this, map[idx].Ip); DoCast(map[idx]); return; }
                int extra = idx - map.Count;
                if (canScan && extra == 0)
                {
                    L10n.Toast(this, L10n.T("正在扫描局域网…"), ToastLength.Short).Show();
                    Task.Run(async () =>
                    {
                        var found = await VodCast.ScanAsync();
                        RunOnUiThread(() => ShowCastPicker(found));
                    });
                    return;
                }
                PromptText(L10n.T("TV 端地址"), L10n.T("请输入 TV 所在局域网 IP（如 192.168.1.20）"), last, ip =>
                {
                    ip = (ip ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(ip)) return;
                    VodCast.SaveIp(this, ip);
                    DoCast(new TvDevice { Ip = ip, Name = L10n.T("电视 · ") + ip });
                });
            });
            dlg.Show();
        }

        /// <summary>把当前线路从 _castStartIndex 起投给 TV（先解析出真实直链）。</summary>
        private void DoCast(TvDevice tv)
        {
            if (_sources == null || _sources.Count == 0 || _line < 0 || _line >= _sources.Count)
            {
                L10n.Toast(this, L10n.T("请先等待线路加载完成"), ToastLength.Short).Show();
                return;
            }
            var src = _sources[_line];
            if (src.Episodes.Count == 0) { L10n.Toast(this, L10n.T("该线路没有可投的集数"), ToastLength.Short).Show(); return; }
            int start = _castStartIndex;
            L10n.Toast(this, L10n.T("正在投放到 ") + tv.Ip + L10n.T("（TV 将自行读取真实链接）"), ToastLength.Short).Show();
            Task.Run(() =>
            {
                try { if (src.Episodes.Any(x => !VodApi.IsDirectPlayUrl(x.PlayUrl))) VodApi.ResolveEpisodes(src); } catch { }
                var names = src.Episodes.Select(ep => _item.Name + " · " + ep.Name).ToList();
                bool ok = VodCast.CastLine(tv, src.Episodes, names, start);
                RunOnUiThread(() => L10n.Toast(this,
                    ok ? (L10n.T("已投放到 ") + tv.Ip + L10n.T("，TV 开始播放并缓存"))
                       : (L10n.T("投放失败：无法连接 ") + tv.Ip + L10n.T("（请确认 TV 端已启动且与手机同一局域网）")),
                    ToastLength.Long).Show());
            });
        }

        private void PromptText(string title, string hint, string init, Action<string> ok)
        {
            var et = new EditText(this) { Text = init ?? "", Hint = hint };
            L10n.Dialog(this)
                .SetTitle(title)
                .SetView(et)
                .SetPositiveButton(L10n.T("确定"), (s, e) => ok(et.Text))
                .SetNegativeButton(L10n.T("取消"), (s, ev) => { })
                .Show();
        }

        private void PlayEpisode(int index)
        {
            if (_item == null || _sources.Count == 0) return;
            var src = _sources[_line];
            if (index < 0 || index >= src.Episodes.Count) return;
            // 若线路尚未解析（Real 未填充），先同步解析，保证点哪集都给到可播直链
            if (src.Episodes.Any(e => string.IsNullOrWhiteSpace(e.Real) && !VodApi.IsDirectPlayUrl(e.PlayUrl)))
                VodApi.ResolveEpisodes(src);
            var list = new List<VodPlayItem>();
            foreach (var ep in src.Episodes)
            {
                // 已解析出直链（m3u8/mp4）→ 直接给 ExoPlayer（与 TV 一致，无需代理）；
                // 嗅探型源若尚未解析出直链，则仍走本地代理做在线解析兜底
                var direct = string.IsNullOrWhiteSpace(ep.Real) ? ep.PlayUrl : ep.Real;
                var play = VodApi.IsDirectPlayUrl(direct) ? direct : VodApi.StreamUrl(ep.PlayUrl, "", true);
                list.Add(new VodPlayItem
                {
                    Name = _item.Name + " · " + ep.Name,
                    Url = play
                });
            }

            // 先落一条观看记录，播放器退出时只回写集数与进度
            VodCache.AddHistory(new VodWatchRecord
            {
                Key = _item.SiteKey + "|" + _item.Id,
                Name = _item.Name, Pic = _item.Pic,
                SiteKey = _item.SiteKey, SiteName = _item.SiteName,
                Remarks = _item.Remarks, EpisodeName = src.Episodes[index].Name,
                EpisodeIndex = index, PositionMs = 0,
                Urls = list.Select(x => x.Url).ToList(),
                Names = list.Select(x => x.Name).ToList()
            });

            var i = new Intent(this, typeof(VideoActivity));
            i.PutExtra(VideoActivity.ExtraQueueJson, JsonConvert.SerializeObject(list));
            i.PutExtra(VideoActivity.ExtraIndex, index);
            i.PutExtra(VideoActivity.ExtraResume, index == _startIndex ? _resumeMs : 0);
            i.PutExtra(VideoActivity.ExtraVodKey, _item.SiteKey + "|" + _item.Id);
            StartActivity(i);
        }

        /* ---------------- 剧集按钮 ---------------- */

        private class EpisodeAdapter : BaseAdapter<VodEpisode>
        {
            private readonly VodDetailActivity _a;
            private List<VodEpisode> _items = new List<VodEpisode>();
            public EpisodeAdapter(VodDetailActivity a) { _a = a; }
            public void Replace(List<VodEpisode> x) { _items = x ?? new List<VodEpisode>(); NotifyDataSetChanged(); }
            public override int Count => _items.Count;
            public override VodEpisode this[int i] => _items[i];
            public override long GetItemId(int i) => i;

            public override View GetView(int i, View cv, ViewGroup parent)
            {
                var t = cv as TextView ?? new TextView(_a)
                {
                    TextSize = 12,
                    Gravity = GravityFlags.Center
                };
                t.SetTextColor(new Color(TextMain));
                t.Background = _a.RoundBg(CardBg, 7);
                t.SetPadding(_a.Dp(3), _a.Dp(9), _a.Dp(3), _a.Dp(9));
                t.SetSingleLine(true);
                t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                var name = _items[i].Name ?? "";
                // 「第01集」压缩成「01」，网格更整齐
                var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d{1,3})");
                t.Text = m.Success ? m.Groups[1].Value : name;
                return t;
            }
        }
    }
}

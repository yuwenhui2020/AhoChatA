// ============================================================
// 在线影视 · 采集站 API 访问层
//
// 移植自 C:\root\code\TV\VodApi.cs，按本项目改为 Newtonsoft.Json + HttpWebRequest。
// 典型接口（苹果CMS / MacCMS V10）：
//   列表  {api}?ac=videolist&pg=1[&t=分类][&wd=关键词]
//   分类  {api}?ac=list
//   详情  {api}?ac=detail&ids=123
// 播放串：vod_play_from "线路A$$$线路B"
//         vod_play_url  "第01集$https://..#第02集$https://..$$$第01集$.."
//
// 实测坑：
//   · 订阅里的 api 常带 ?ac=list 尾巴，必须截断后重拼（Base）。
//   · 不少采集站 wd= 被禁用（索尼直接返回「暂不支持搜索」）或忽略（火狐/量子
//     返回全量列表），所以搜索 = 联网有效结果 + 本地预抓缓存过滤，两路合并。
//   · 播放页返回 HTML（DPlayer + hls.js），用正则嗅探真实 m3u8；最常见是
//     原地址 + /index.m3u8，命中后整条线路套模板，避免逐集请求。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using IoPath = System.IO.Path;

namespace AhoChatA.Vod
{
    public static class VodApi
    {
        public const string UserAgent =
            "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";

        private static bool _httpInited;
        private static readonly object _httpLock = new object();

        public static void EnsureHttp()
        {
            if (_httpInited) return;
            lock (_httpLock)
            {
                if (_httpInited) return;
                _httpInited = true;
                try
                {
                    ServicePointManager.Expect100Continue = false;
                    ServicePointManager.DefaultConnectionLimit = 24;
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                }
                catch { }
            }
        }

        /// <summary>带浏览器 UA 的 GET，返回文本（去 BOM / 零宽字符），失败返回 null。</summary>
        public static string HttpGetText(string url, int timeoutMs = 9000)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                EnsureHttp();
                using (var resp = Open(url, null, null, timeoutMs))
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var s = sr.ReadToEnd();
                    if (string.IsNullOrEmpty(s)) return null;
                    return s.TrimStart('\ufeff', '\u200B', ' ', '\t', '\r', '\n');
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("MchatVod", "GET 失败 " + url + " : " + ex.Message);
                return null;
            }
        }

        public static byte[] HttpGetBytes(string url, int timeoutMs = 12000, string referer = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                EnsureHttp();
                using (var resp = Open(url, referer, null, timeoutMs))
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        /// <summary>打开一个带 UA / 可选 Referer / 可选 Range 的响应，供文本与流式代理共用。</summary>
        public static HttpWebResponse Open(string url, string referer, string range, int timeoutMs)
        {
            EnsureHttp();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.Accept = "application/json, text/plain, */*";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 6;
            // 连接复用：HLS 一个节目有几十上百个 .ts 分片，复用 TCP/TLS 能显著缩短起播与拖动延迟。
            // 同时显式置空代理，避免 Android 每次请求都做系统代理探测（部分机型上很慢）。
            req.KeepAlive = true;
            req.Proxy = null;
            try { req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
            if (!string.IsNullOrEmpty(referer))
            {
                try { req.Referer = referer; } catch { }
            }
            else
            {
                try { req.Referer = HostOf(url); } catch { }
            }
            if (!string.IsNullOrEmpty(range))
            {
                try { req.AddRange(ParseRangeStart(range)); }
                catch { try { req.Headers["Range"] = range; } catch { } }
            }
            return (HttpWebResponse)req.GetResponse();
        }

        private static long ParseRangeStart(string range)
        {
            var m = Regex.Match(range ?? "", @"bytes=(\d+)-", RegexOptions.IgnoreCase);
            long v;
            return m.Success && long.TryParse(m.Groups[1].Value, out v) ? v : 0;
        }

        /* ---------------- URL 构造 ---------------- */

        /// <summary>规范化 api：去掉 ?ac=list 等尾巴。</summary>
        public static string Base(string api)
        {
            if (string.IsNullOrWhiteSpace(api)) return "";
            var b = api.Trim();
            int q = b.IndexOf('?');
            if (q >= 0) b = b.Substring(0, q);
            return b.TrimEnd('?', '&');
        }

        public static string BuildListUrl(string api, int page, string typeId, string keyword)
        {
            var b = Base(api);
            if (b.Length == 0) return "";
            var sb = new StringBuilder(b);
            sb.Append(b.Contains("?") ? "&" : "?").Append("ac=videolist&pg=").Append(Math.Max(1, page));
            if (!string.IsNullOrWhiteSpace(typeId)) sb.Append("&t=").Append(System.Uri.EscapeDataString(typeId));
            if (!string.IsNullOrWhiteSpace(keyword)) sb.Append("&wd=").Append(System.Uri.EscapeDataString(keyword.Trim()));
            return sb.ToString();
        }

        public static string BuildDetailUrl(string api, string id)
        {
            var b = Base(api);
            if (b.Length == 0 || string.IsNullOrWhiteSpace(id)) return "";
            return b + (b.Contains("?") ? "&" : "?") + "ac=detail&ids=" + System.Uri.EscapeDataString(id);
        }

        public static string BuildClassUrl(string api)
        {
            var b = Base(api);
            return b.Length == 0 ? "" : b + (b.Contains("?") ? "&" : "?") + "ac=list";
        }

        public static string HostOf(string url)
        {
            try
            {
                var u = new System.Uri(url);
                return u.Scheme + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + "/";
            }
            catch { return ""; }
        }

        /// <summary>CMS 封面是相对路径 /api/cover?id=N，必须补全成绝对地址并附带 token——否则 PosterBytes 会因非 http 直接返回 null，海报全空。</summary>
        static void CmsCoverAbsolute(string api, string token, VodItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Pic)) return;
            if (!it.Pic.StartsWith("/api/cover", StringComparison.OrdinalIgnoreCase)
                && !it.Pic.StartsWith("api/cover", StringComparison.OrdinalIgnoreCase)) return;
            string root = VodConfig.HostRoot(api);
            if (string.IsNullOrEmpty(root)) return;
            string abs = root + (it.Pic.StartsWith("/") ? it.Pic : "/" + it.Pic);
            if (!string.IsNullOrEmpty(token) && abs.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0)
                abs += (abs.IndexOf('?') < 0 ? "?" : "&") + "token=" + Uri.EscapeDataString(token);
            it.Pic = abs;
        }

        /* ---------------- 列表 ---------------- */

        private static string S(JObject o, string k)
        {
            var v = o[k];
            if (v == null || v.Type == JTokenType.Null || v.Type == JTokenType.Array || v.Type == JTokenType.Object) return "";
            try { return v.ToString(); }
            catch { return ""; }
        }

        private static string AbsPic(string pic, string api)
        {
            if (string.IsNullOrWhiteSpace(pic)) return "";
            if (pic.StartsWith("//")) return "https:" + pic;
            if (pic.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return pic;
            var host = HostOf(api);
            if (host.Length == 0) return pic;
            try { return new System.Uri(new System.Uri(host), pic).AbsoluteUri; }
            catch { return host.TrimEnd('/') + "/" + pic.TrimStart('/'); }
        }

        private static VodItem ToItem(JObject e, VodSite site)
        {
            var it = new VodItem
            {
                Id = S(e, "vod_id"),
                Name = S(e, "vod_name"),
                Pic = S(e, "vod_pic"),
                Remarks = S(e, "vod_remarks"),
                TypeName = S(e, "type_name"),
                Year = S(e, "vod_year"),
                Area = S(e, "vod_area"),
                Desc = CleanHtml(S(e, "vod_content")),
                Actor = S(e, "vod_actor"),
                Score = S(e, "vod_score"),
                PlayFrom = S(e, "vod_play_from"),
                PlayUrl = S(e, "vod_play_url"),
                SiteKey = site.Key,
                SiteName = site.Name,
                Api = site.Api
            };
            if (string.IsNullOrWhiteSpace(it.Pic))
            {
                it.Pic = S(e, "vod_pic_thumb");
                if (string.IsNullOrWhiteSpace(it.Pic)) it.Pic = S(e, "vod_pic_slide");
            }
            if (string.IsNullOrWhiteSpace(it.Remarks))
            {
                var serial = S(e, "vod_serial");
                if (!string.IsNullOrWhiteSpace(serial) && Regex.IsMatch(serial, @"\d"))
                    it.Remarks = L10n.T("共 ") + serial.Trim() + L10n.T(" 集");
            }
            it.Pic = AbsPic(it.Pic, site.Api);
            return it;
        }

        public static List<VodItem> FetchList(VodSite site, int page, string typeId, string keyword, out int total,
            int timeoutMs = 0)
        {
            total = 0;
            var res = new List<VodItem>();
            var prof = SourceDetector.GetProfile(site.Key, site.Api);
            if (prof == null) return res;
            var url = SourceDetector.BuildListUrl(prof, site.Api, page, typeId, 0, keyword, site.IsCms ? site.Token : null);
            if (url.Length == 0) return res;
            // 搜索走短超时：慢站不该把整次搜索拖到 7~9 秒
            int tmo = timeoutMs > 0 ? timeoutMs : VodConfig.Load().TimeoutMs;
            var txt = HttpGetText(url, tmo);
            if (string.IsNullOrWhiteSpace(txt)) return res;
            int pc, lim; bool err; string em;
            res = SourceDetector.ParseList(txt, site, out total, out pc, out lim, out err, out em);
            if (site.IsCms) foreach (var it in res) CmsCoverAbsolute(site.Api, site.Token, it);
            return res;
        }

        /// <summary>多源并发拉取并按「站点轮流」交错合并，避免单源霸占整屏。</summary>
        public static List<VodItem> FetchPaged(List<VodSite> sites, int page, Dictionary<string, string> typeIds,
            string keyword, out int okSites, int waitMs = 9000)
        {
            okSites = 0;
            var buckets = new List<VodItem>[sites.Count];
            int ok = 0;
            var tasks = new Task[sites.Count];
            for (int i = 0; i < sites.Count; i++)
            {
                int slot = i;
                var site = sites[slot];
                string tid = null;
                if (typeIds != null) typeIds.TryGetValue(site.Key, out tid);
                // 定向翻页时某站没有该分类就不拉，否则会退化成全站列表把电影混进电视剧
                if (typeIds != null && typeIds.Count > 0 && string.IsNullOrWhiteSpace(tid))
                {
                    tasks[slot] = Task.FromResult(0);
                    continue;
                }
                string id = tid;
                tasks[slot] = Task.Run(() =>
                {
                    try
                    {
                        int total;
                        var list = FetchList(site, page, id, keyword, out total);
                        if (list.Count > 0) { buckets[slot] = list; Interlocked.Increment(ref ok); }
                    }
                    catch { }
                });
            }
            try { Task.WaitAll(tasks, waitMs); } catch { }
            okSites = ok;
            return Interleave(buckets);
        }

        private static List<VodItem> Interleave(List<VodItem>[] buckets)
        {
            var merged = new List<VodItem>();
            int max = 0;
            foreach (var b in buckets) if (b != null) max = Math.Max(max, b.Count);
            for (int row = 0; row < max; row++)
                foreach (var b in buckets)
                    if (b != null && row < b.Count) merged.Add(b[row]);
            return merged;
        }

        /// <summary>首屏/翻页快速拉取：只要凑够 minSites 个站或超过 fastMs 就立刻返回，
        /// 绝不为了等最慢的采集站把 HTTP 请求挂住 9 秒。未完成的站继续在后台跑，
        /// 全部结束后统一把完整结果并入首页缓存（下一次刷新即可见）。
        /// 这是「加载不卡顿」的核心：冷启动最多等 2 秒左右就有内容。</summary>
        public static List<VodItem> FetchPagedFast(List<VodSite> sites, int page,
            Dictionary<string, string> typeIds, string keyword,
            int minSites, int fastMs, string sig, string cat)
        {
            var buckets = new List<VodItem>[sites.Count];
            int finished = 0, success = 0;
            var tasks = new Task[sites.Count];
            for (int i = 0; i < sites.Count; i++)
            {
                int slot = i;
                var site = sites[slot];
                string tid = null;
                if (typeIds != null) typeIds.TryGetValue(site.Key, out tid);
                if (typeIds != null && typeIds.Count > 0 && string.IsNullOrWhiteSpace(tid))
                {
                    tasks[slot] = Task.FromResult(0);
                    System.Threading.Interlocked.Increment(ref finished);
                    continue;
                }
                string id = tid;
                tasks[slot] = Task.Run(() =>
                {
                    try
                    {
                        int total;
                        var list = FetchList(site, page, id, keyword, out total);
                        if (list.Count > 0)
                        {
                            buckets[slot] = list;
                            System.Threading.Interlocked.Increment(ref success);
                        }
                    }
                    catch { }
                    finally { System.Threading.Interlocked.Increment(ref finished); }
                });
            }

            int waited = 0;
            while (waited < fastMs
                   && System.Threading.Volatile.Read(ref success) < minSites
                   && System.Threading.Volatile.Read(ref finished) < sites.Count)
            {
                System.Threading.Thread.Sleep(60);
                waited += 60;
            }
            var early = Interleave(buckets);

            // 收尾：等所有站结束（最多再 12 秒），把「慢站」结果也并进缓存。
            // 只写盘一次，避免每个站完成都重写 home.json。
            Task.Run(() =>
            {
                try { Task.WaitAll(tasks, 12000); } catch { }
                try
                {
                    var all = Interleave(buckets);
                    if (all.Count > early.Count) VodCache.AppendHome(sig, page, all, cat ?? "");
                }
                catch { }
            });
            return early;
        }

        /* ---------------- 分类 ---------------- */

        public static List<Tuple<string, string>> FetchClasses(VodSite site)
        {
            var res = new List<Tuple<string, string>>();
            var prof = SourceDetector.GetProfile(site.Key, site.Api);
            if (prof == null) return res;
            var map = SourceDetector.FetchClasses(prof, site.Api, site.IsCms ? site.Token : null);
            foreach (var kv in map)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    res.Add(Tuple.Create(kv.Value, kv.Key));
            return res;
        }

        /// <summary>分类展示顺序：电视剧永远第一、电影永远第二，其余按命中站点数排。</summary>
        private static int CatRank(string name)
        {
            if (name == "电视剧") return 0;
            if (name == "电影") return 1;
            return 2;
        }

        /// <summary>站点「总类」名：用来挑定向翻页优先采用的 type_id（内容最全）。</summary>
        private static bool IsPrimaryClass(string raw)
        {
            var n = (raw ?? "").Trim().Replace(" ", "");
            return n == "电视剧" || n == "连续剧" || n == "剧集"
                || n == "电影" || n == "电影片" || n == "影片";
        }

        /// <summary>内置分类骨架：首次点「分类」或首屏 loadCats 立刻返回，绝不等网络。
        /// 真实分类由 BuildCatsCore 后台聚合并写 cats.json，下次请求即为完整版。</summary>
        public static List<VodCat> DefaultCats()
        {
            return new List<VodCat>
            {
                new VodCat { Name = L10n.T("电视剧"), Hits = 0, TypeIds = new Dictionary<string, string>() },
                new VodCat { Name = L10n.T("电影"),   Hits = 0, TypeIds = new Dictionary<string, string>() },
                new VodCat { Name = L10n.T("综艺"),   Hits = 0, TypeIds = new Dictionary<string, string>() },
                new VodCat { Name = L10n.T("动漫"),   Hits = 0, TypeIds = new Dictionary<string, string>() },
                new VodCat { Name = L10n.T("纪录片"), Hits = 0, TypeIds = new Dictionary<string, string>() }
            };
        }

        private static int _catsBuilding;

        // 分类归一规则的版本号：只影响 cats.json 的键，不动首页缓存。
        // 规则改动（如把「犯罪片 / 记录片」并入大类）时 +1，旧缓存自动失效重聚。
        private const string CatsSigRev = "c3";
        public static string CatsSig() { return VodConfig.Signature() + CatsSigRev; }

        /// <summary>真实分类缓存是否可用；供 /vod/cats 决定 partial 标记，避免前端拿到骨架却不重试。</summary>
        public static bool CatsReady()
        {
            var hit = VodCache.LoadCats(CatsSig());
            return hit != null && hit.Count > 0 && hit.Exists(x => x.Hits > 0);
        }

        /// <summary>聚合各站分类：缓存优先；无缓存时立即返回骨架 + 后台聚合（不让任何人等 10 秒）。</summary>
        public static List<VodCat> BuildCats(List<VodSite> sites)
        {
            var sig = CatsSig();
            var hit = VodCache.LoadCats(sig);
            // 旧版本在离线时把「只有电视剧/电影、hits 全 0」的降级结果写进了缓存，
            // 之后网络恢复也不会重拉。这里识别这类无效缓存，让它自愈重建。
            if (hit != null && hit.Count > 0 && hit.Exists(x => x.Hits > 0)) return hit;
            if (System.Threading.Interlocked.Exchange(ref _catsBuilding, 1) == 0)
            {
                Task.Run(() =>
                {
                    try { BuildCatsCore(sites, sig); }
                    catch { }
                    finally { System.Threading.Interlocked.Exchange(ref _catsBuilding, 0); }
                });
            }
            return DefaultCats();
        }

        /// <summary>真实分类聚合并落盘。只在后台线程执行。</summary>
        private static void BuildCatsCore(List<VodSite> sites, string sig)
        {
            var pairs = new List<Tuple<string, string>>[sites.Count];
            var tasks = new Task[sites.Count];
            for (int i = 0; i < sites.Count; i++)
            {
                int slot = i;
                tasks[slot] = Task.Run(() => { try { pairs[slot] = FetchClasses(sites[slot]); } catch { } });
            }
            try { Task.WaitAll(tasks, 12000); } catch { }
            var map = new Dictionary<string, VodCat>(StringComparer.OrdinalIgnoreCase);
            var order = new List<VodCat>();
            for (int i = 0; i < sites.Count; i++)
            {
                if (pairs[i] == null) continue;
                foreach (var p in pairs[i])
                {
                    if (IsBlockedCategory(p.Item1)) continue;
                    var name = NormalizeCategory(p.Item1);
                    if (name.Length == 0) continue;
                    VodCat c;
                    if (!map.TryGetValue(name, out c))
                    {
                        c = new VodCat { Name = name };
                        map[name] = c; order.Add(c);
                    }
                    c.Hits++;
                    // 同名归一后一站可能有多个子分类（如 电视剧/国产剧/欧美剧）。
                    // 定向翻页只能带一个 type_id，优先采用「总类」那个，内容最全；
                    // 没有总类时才退而求其次用第一个子分类。
                    string key = sites[i].Key;
                    if (IsPrimaryClass(p.Item1) || !c.TypeIds.ContainsKey(key))
                        c.TypeIds[key] = p.Item2;
                }
            }
            // 一个站点都没拿到分类（离线 / 全超时）时**不要落盘**：
            // 否则会把「只有电视剧、电影」的降级结果当真实缓存写死，前端也不再重试。
            bool anyReal = order.Exists(x => x.Hits > 0);
            if (!anyReal) return;

            // 保证一定有电视剧和电影，即便某站分类名完全没命中。
            // 必须在排序**之前**补，否则追加的两个大类会跑到末尾，破坏固定顺序。
            foreach (var must in new[] { L10n.T("电视剧"), L10n.T("电影") })
                if (!order.Exists(x => x.Name == must)) order.Add(new VodCat { Name = must, Hits = 0 });

            // 固定顺序：电视剧 → 电影 → 其它（其它内部按命中数降序）
            order.Sort((a, b) =>
            {
                int ra = CatRank(a.Name), rb = CatRank(b.Name);
                if (ra != rb) return ra.CompareTo(rb);
                return b.Hits.CompareTo(a.Hits);
            });
            if (order.Count > 24) order = order.GetRange(0, 24);
            VodCache.SaveCats(sig, order);
        }

        /// <summary>部分采集站会带成人/擦边分类，直接不收进聚合结果（也不参与 hits 计数）。</summary>
        private static bool IsBlockedCategory(string raw)
        {
            var n = (raw ?? "").Trim();
            if (n.Length == 0) return true;
            string[] bad = { L10n.T("三级"), L10n.T("两性"), L10n.T("写真"), L10n.T("热舞"), L10n.T("成人"), L10n.T("情色"), L10n.T("色情"), L10n.T("福利"), L10n.T("里番"), L10n.T("无码"), L10n.T("有码") };
            foreach (var w in bad) if (n.Contains(w)) return true;
            return false;
        }

        /// <summary>把各站五花八门的分类名收敛成少数大类。
        /// 剧集（电视剧/连续剧/国产剧/大陆剧/港台剧/欧美剧/日韩剧/韩剧/日剧…）→ 电视剧；
        /// 电影（电影片/动作片/喜剧片/爱情片…）→ 电影。这样默认「电视剧」内容最全，
        /// 且「电影」固定排第二，其余分类依次靠后。</summary>
        public static string NormalizeCategory(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var n = s.Trim().Replace(" ", "").Replace(L10n.T("片"), "");
            if (n.Contains(L10n.T("综艺"))) return L10n.T("综艺");
            if (n.Contains(L10n.T("动漫")) || n.Contains(L10n.T("动画"))) return L10n.T("动漫");
            // 有些站写成「记录片」（记 / 纪 混用），两种都并到纪录片
            if (n.Contains(L10n.T("纪录")) || n.Contains(L10n.T("记录"))) return L10n.T("纪录片");
            // 剧集类一律并到「电视剧」（含「连续剧」「剧集」「国产剧」「欧美剧」「韩剧」等）
            if (n.Contains(L10n.T("剧")) || n.Contains(L10n.T("连续")) || n.Contains(L10n.T("剧场"))) return L10n.T("电视剧");
            // 部分站把剧集按题材细分（古装仙侠 / 现代都市 / 重生民国 / 穿越年代 / 言情总裁…），
            // 不带「剧」字，但同样属于连续剧，不能让它们变成独立大类
            string[] seriesWords = { L10n.T("古装"), L10n.T("仙侠"), L10n.T("都市"), L10n.T("民国"), L10n.T("年代"), L10n.T("穿越"), L10n.T("重生"),
                                     L10n.T("总裁"), L10n.T("女频"), L10n.T("男频"), L10n.T("恋爱"), L10n.T("言情"), L10n.T("偶像"), L10n.T("青春"),
                                     L10n.T("谍战"), L10n.T("军旅"), L10n.T("农村"), L10n.T("宫斗"), L10n.T("网剧"), L10n.T("短剧") };
            foreach (var w in seriesWords) if (n.Contains(w)) return L10n.T("电视剧");
            // 剩下的电影子类型与「电影」本身全部并到「电影」。
            // 注意 n 已去掉「片」字：犯罪片→犯罪、灾难片→灾难、预告片→预告。
            string[] movieWords = { L10n.T("电影"), L10n.T("动作"), L10n.T("喜剧"), L10n.T("爱情"), L10n.T("科幻"), L10n.T("恐怖"), L10n.T("悬疑"),
                                    L10n.T("战争"), L10n.T("剧情"), L10n.T("伦理"), L10n.T("理论"), L10n.T("微视"), L10n.T("犯罪"), L10n.T("灾难"),
                                    L10n.T("惊悚"), L10n.T("冒险"), L10n.T("奇幻"), L10n.T("西部"), L10n.T("预告") };
            foreach (var w in movieWords) if (n.Contains(w)) return L10n.T("电影");
            // 未识别的（如「体育」「音乐」）原样保留，排在电视剧/电影之后
            return s.Trim();
        }

        /* ---------------- 详情 / 播放串 ---------------- */

        public static void FillDetail(VodItem it, bool force = false)
        {
            if (it == null) return;
            if (!force)
            {
                var c = VodCache.LoadDetail(it.SiteKey, it.Id);
                if (c != null)
                {
                    it.PlayFrom = c.PlayFrom; it.PlayUrl = c.PlayUrl;
                    if (!string.IsNullOrWhiteSpace(c.Desc)) it.Desc = c.Desc;
                    if (!string.IsNullOrWhiteSpace(c.Pic)) it.Pic = c.Pic;
                    if (!string.IsNullOrWhiteSpace(c.Actor)) it.Actor = c.Actor;
                    return;
                }
            }
            var prof = SourceDetector.GetProfile(it.SiteKey, it.Api);
            if (prof == null) return;
            string cmsToken = VodConfig.CmsTokenForApi(it.Api);
            var url = SourceDetector.BuildDetailUrl(prof, it.Api, new List<string> { it.Id }, cmsToken);
            if (url.Length == 0) return;
            var txt = HttpGetText(url, VodConfig.Load().TimeoutMs);
            if (string.IsNullOrWhiteSpace(txt)) return;
            if (SourceDetector.ParseDetail(txt, it))
            {
                if (VodConfig.IsCmsApi(it.Api)) CmsCoverAbsolute(it.Api, cmsToken, it);
                VodCache.SaveDetail(it.SiteKey, it.Id, new VodCache.DetailBundle
                {
                    PlayFrom = it.PlayFrom ?? "", PlayUrl = it.PlayUrl ?? "",
                    Desc = it.Desc ?? "", Pic = it.Pic ?? "", Actor = it.Actor ?? ""
                });
            }
        }

        /// <summary>$$$ 分线路、# 分集、$ 分「集名|地址」。</summary>
        public static List<VodSource> ParseSources(string from, string play)
        {
            var res = new List<VodSource>();
            if (string.IsNullOrWhiteSpace(play)) return res;
            var froms = (from ?? "").Split(new[] { "$$$" }, StringSplitOptions.None);
            var groups = play.Split(new[] { "$$$" }, StringSplitOptions.None);
            for (int g = 0; g < groups.Length; g++)
            {
                var src = new VodSource
                {
                    Name = (g < froms.Length && !string.IsNullOrWhiteSpace(froms[g]))
                        ? froms[g].Trim() : (L10n.T("线路 ") + (g + 1))
                };
                foreach (var seg in groups[g].Split('#'))
                {
                    int d = seg.IndexOf('$');
                    if (d <= 0) continue;
                    var name = seg.Substring(0, d).Trim();
                    var u = seg.Substring(d + 1).Trim();
                    int extra = u.IndexOf('$');        // 有些站点在 URL 后追加 $备注
                    if (extra > 0) u = u.Substring(0, extra).Trim();
                    if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    src.Episodes.Add(new VodEpisode
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? (src.Episodes.Count + 1).ToString("00") : name,
                        Url = u
                    });
                }
                if (src.Episodes.Count > 0)
                {
                    src.DirectCount = src.Episodes.Count(x => IsDirectPlayUrl(x.Url));
                    res.Add(src);
                }
            }
            res.Sort((a, b) => b.DirectCount.CompareTo(a.DirectCount));   // 直链线路优先
            return res;
        }

        public static bool IsDirectPlayUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            var low = url.ToLowerInvariant();
            int q = low.IndexOf('?');
            if (q >= 0) low = low.Substring(0, q);
            return low.Contains(".m3u8") || low.EndsWith(".mp4") || low.EndsWith(".mkv")
                || low.EndsWith(".ts") || low.EndsWith(".flv") || low.EndsWith(".avi")
                || low.EndsWith(".mov") || low.EndsWith(".m4v");
        }

        private static readonly Regex MediaUrlRx = new Regex(
            @"https?:\\?/\\?/[^\s'""<>\\)]+?\.(?:m3u8|mp4)(?:\?[^\s'""<>\\)]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string ExtractMediaUrl(string html, string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var m = MediaUrlRx.Match(html);
            if (m.Success) return m.Value.Replace("\\/", "/").Replace("\\", "");
            var rel = Regex.Match(html,
                @"['""](/?[A-Za-z0-9_\-./]+\.(?:m3u8|mp4)(?:\?[^'""\s]*)?)['""]",
                RegexOptions.IgnoreCase);
            if (!rel.Success) return "";
            var v = rel.Groups[1].Value;
            if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return v;
            try { return new System.Uri(new System.Uri(pageUrl), v).AbsoluteUri; }
            catch { return ""; }
        }

        /// <summary>播放页 → 真实直链（含 7 天缓存）。</summary>
        public static string ResolvePlayUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || IsDirectPlayUrl(url)) return url;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            var hit = VodCache.LoadResolved(url);
            if (!string.IsNullOrWhiteSpace(hit)) return hit;
            var txt = HttpGetText(url, 8000);
            if (string.IsNullOrWhiteSpace(txt)) return url;
            var t = txt.TrimStart();
            if (t.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                VodCache.SaveResolved(url, url);
                return url;
            }
            var real = ExtractMediaUrl(txt, url);
            if (string.IsNullOrWhiteSpace(real)) return url;
            VodCache.SaveResolved(url, real);
            return real;
        }

        /// <summary>整条线路解析：先用第一集推断后缀模板，命中则批量套用，否则 6 路并发。</summary>
        public static void ResolveEpisodes(VodSource src)
        {
            if (src == null || src.Episodes.Count == 0) return;
            int sample = -1;
            for (int i = 0; i < src.Episodes.Count; i++)
                if (!IsDirectPlayUrl(src.Episodes[i].Url)) { sample = i; break; }
            if (sample < 0) return;
            var orig = src.Episodes[sample].Url;
            var real = ResolvePlayUrl(orig);
            if (!string.IsNullOrWhiteSpace(real) && real != orig && IsDirectPlayUrl(real))
            {
                src.Episodes[sample].Real = real;
                string suffix = real.StartsWith(orig, StringComparison.Ordinal)
                    ? real.Substring(orig.Length) : null;
                if (!string.IsNullOrEmpty(suffix))
                {
                    foreach (var ep in src.Episodes)
                    {
                        if (IsDirectPlayUrl(ep.Url)) continue;
                        if (!string.IsNullOrEmpty(ep.Real)) continue;
                        ep.Real = ep.Url + suffix;
                        VodCache.SaveResolved(ep.Url, ep.Real);
                    }
                    src.DirectCount = src.Episodes.Count(x => IsDirectPlayUrl(x.PlayUrl));
                    return;
                }
            }
            var gate = new SemaphoreSlim(6);
            var tasks = new List<Task>();
            foreach (var ep in src.Episodes)
            {
                if (IsDirectPlayUrl(ep.Url) || !string.IsNullOrEmpty(ep.Real)) continue;
                var one = ep;
                tasks.Add(Task.Run(() =>
                {
                    gate.Wait();
                    try
                    {
                        var r = ResolvePlayUrl(one.Url);
                        if (!string.IsNullOrWhiteSpace(r) && IsDirectPlayUrl(r)) one.Real = r;
                    }
                    catch { }
                    finally { gate.Release(); }
                }));
            }
            try { Task.WaitAll(tasks.ToArray(), 18000); } catch { }
            src.DirectCount = src.Episodes.Count(x => IsDirectPlayUrl(x.PlayUrl));
        }

        /// <summary>后台预热：解析后拉取前几集的 m3u8 进代理缓存，用户真正起播时近乎秒开。</summary>
        public static void WarmEpisodes(VodSource src)
        {
            if (src == null || src.Episodes.Count == 0) return;
            int n = Math.Min(src.Episodes.Count, 4);
            for (int i = 0; i < n; i++)
            {
                var ep = src.Episodes[i];
                var u = string.IsNullOrWhiteSpace(ep.Real) ? ep.Url : ep.Real;
                if (!IsDirectPlayUrl(u)) u = ResolvePlayUrl(u);
                if (string.IsNullOrWhiteSpace(u)) continue;
                var w = WarmUrl(u, HostOf(u), true);
                try { HttpGetText(w, 6000); } catch { }
            }
        }

        /* ---------------- 同名合并 / 归一化 ---------------- */

        public static string NameKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new StringBuilder();
            foreach (var ch in name)
            {
                int c = (int)ch;
                if (c >= 0xFF01 && c <= 0xFF5E) c -= 0xFEE0;      // 全角转半角
                if (c == 0x3000 || c == 0x00A0) continue;
                var x = (char)c;
                if ("《》〈〉【】〖〗()[]{}.,-_~!:;\'\" ".IndexOf(x) >= 0) continue;
                sb.Append(char.ToLowerInvariant(x));
            }
            return sb.ToString();
        }

        /// <summary>同名跨源合并成一张卡片，Variants 保留所有站点版本。</summary>
        public static List<VodItem> MergeByName(List<VodItem> list)
        {
            var map = new Dictionary<string, VodItem>(StringComparer.Ordinal);
            var res = new List<VodItem>();
            foreach (var it in list)
            {
                var key = NameKey(it.Name);
                if (key.Length == 0) key = "\u0000" + it.Key;
                VodItem rep;
                if (!map.TryGetValue(key, out rep))
                {
                    rep = it;
                    rep.Variants = new List<VodItem>();
                    map[key] = rep; res.Add(rep);
                }
                else if (Score(it) > Score(rep))
                {
                    // 换代表但保留已收集的变体
                    var variants = rep.Variants;
                    int at = res.IndexOf(rep);
                    it.Variants = variants;
                    map[key] = it;
                    if (at >= 0) res[at] = it;
                    rep = it;
                }
                rep.Variants.Add(it);
            }
            return res;
        }

        private static int Score(VodItem it)
        {
            if (it == null) return -1;
            int s = 0;
            if (!string.IsNullOrWhiteSpace(it.Pic)) s += 1000;
            if (!string.IsNullOrWhiteSpace(it.Desc)) s += 100;
            if (!string.IsNullOrWhiteSpace(it.Year)) s += 10;
            s += Math.Min(90, EpisodeNumber(it));
            return s;
        }

        public static int EpisodeNumber(VodItem it)
        {
            if (it == null) return 0;
            var m = Regex.Match(it.Remarks ?? "", @"(\d{1,3})");
            int n;
            return m.Success && int.TryParse(m.Groups[1].Value, out n) ? Math.Min(99, n) : 0;
        }

        public static string CleanHtml(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            bool inTag = false;
            foreach (var ch in s)
            {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString()
                .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
                .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("\\/", "/")
                .Trim();
        }

        /* ---------------- 首页 / 搜索（App 与网页端共用） ---------------- */

        /// <summary>首页：缓存优先（秒开），没有缓存才联网；联网也走「快速返回 + 后台补齐」。</summary>
        public static List<VodItem> Home(int page, string cat, out bool fromCache)
        {
            fromCache = false;
            var sig = VodConfig.Signature();
            var sites = VodConfig.EnabledSites();
            var cache = VodCache.LoadHome(sig);
            if (page <= 1 && cache != null && cache.List != null && cache.List.Count > 0)
            {
                fromCache = true;
                return FilterCat(cache.List, cat);
            }
            VodCat catInfo = null;
            if (!string.IsNullOrWhiteSpace(cat))
            {
                var cats = VodCache.LoadCats(CatsSig()) ?? new List<VodCat>();
                catInfo = cats.Find(x => x.Name == cat);
                // 骨架分类 TypeIds 为空：不要传空字典（会被当成「没有该分类」而跳过全部站点）
                if (catInfo != null && catInfo.TypeIds.Count == 0) catInfo = null;
            }
            var typeIds = catInfo == null ? null : catInfo.TypeIds;
            var pg = Math.Max(1, page);
            // 冷启动 / 翻页都不再同步等 9 秒：2 秒内凑够 4 个源就返回，其余后台补齐
            var fresh = FetchPagedFast(sites, pg, typeIds, null, 4, 2000, sig, cat ?? "");
            var merged = VodCache.AppendHome(sig, pg, fresh, cat ?? "");
            return FilterCat(merged, cat);
        }

        private static List<VodItem> FilterCat(List<VodItem> list, string cat)
        {
            var src = list ?? new List<VodItem>();
            if (!string.IsNullOrWhiteSpace(cat))
            {
                src = src.FindAll(x => (x.TypeName ?? "").IndexOf(cat, StringComparison.OrdinalIgnoreCase) >= 0
                    || NormalizeCategory(x.TypeName) == cat);
            }
            return MergeByName(src);
        }

        /// <summary>后台预抓后续分页。限速版：延迟起步、每页之间留空档、单页超时收短，
        /// 避免 12 页 × 全站并发把首屏「快速返回」的慢站补齐和翻页请求一起挤爆线程池。</summary>
        public static void WarmPages(int maxPages, Action<int, int> onPage = null)
        {
            var cfg = VodConfig.Load();
            int max = Math.Min(maxPages <= 0 ? cfg.MaxPages : maxPages, cfg.MaxPages);
            if (max > 6) max = 6;                 // 首轮最多补 6 页，够本地搜索用
            int start = VodCache.PageOf(VodConfig.Signature(), "") + 1;
            // 让首屏先跑完，别一上来就和 15 个源抢连接
            try { System.Threading.Thread.Sleep(2500); } catch { }
            for (int p = start; p <= max; p++)
            {
                int ok;
                var fresh = FetchPaged(VodConfig.EnabledSites(), p, null, null, out ok, 4000);
                VodCache.AppendHome(VodConfig.Signature(), p, fresh, "");
                if (onPage != null) onPage(p, max);
                if (ok == 0) break;
                try { System.Threading.Thread.Sleep(800); } catch { }
            }
        }

        /* ---------------- 搜索结果缓存 ----------------
         * 旧实现每次输入都等 Task.WaitAll(12000) 把最慢的源拖满，且结果不落缓存；
         * 现在命中缓存直接返回，冷搜「凑够 2 个源或 2.2 秒」即出结果，慢源后台补齐。 */
        private sealed class SearchEntry
        {
            public List<VodItem> Items = new List<VodItem>();
            public long At;
            public int OkSites;
        }
        private const long SearchCacheTtlMs = 3L * 60 * 1000;
        private const int SearchFastMs = 2200;
        private const int SearchSiteTimeoutMs = 3500;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SearchEntry> _searchCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, SearchEntry>();
        private static long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        private static void PutSearchCache(string ck, List<VodItem> items, int okSites)
        {
            try
            {
                if (_searchCache.Count > 80)
                    foreach (var k in _searchCache.Keys.Take(40).ToArray())
                    { SearchEntry dummy; _searchCache.TryRemove(k, out dummy); }
                _searchCache[ck] = new SearchEntry { Items = items, At = NowMs(), OkSites = okSites };
            }
            catch { }
        }

        /// <summary>联网结果 + 本地预抓缓存命中，并做去重 / 同名合并。</summary>
        private static List<VodItem> FinishSearch(string kw, string kkey, List<VodItem>[] buckets)
        {
            var res = Interleave(buckets);
            try
            {
                var cache = VodCache.LoadHome(VodConfig.Signature());
                if (cache != null && cache.List != null)
                {
                    var local = cache.List.FindAll(x =>
                        (x.Name ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        NameKey(x.Name).IndexOf(kkey, StringComparison.OrdinalIgnoreCase) >= 0);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in res) seen.Add(it.Key);
                    foreach (var it in local) if (seen.Add(it.Key)) res.Add(it);
                }
            }
            catch { }
            return MergeByName(res);
        }

        /// <summary>搜索 = 各站 wd= 联网结果（过滤掉忽略 wd 的站）+ 本地预抓缓存命中。</summary>
        public static List<VodItem> Search(string keyword, int page, out int okSites)
        {
            okSites = 0;
            var kw = (keyword ?? "").Trim();
            if (kw.Length == 0) return new List<VodItem>();
            var ck = kw.ToLowerInvariant() + "|" + Math.Max(1, page);
            SearchEntry cached;
            if (_searchCache.TryGetValue(ck, out cached) && NowMs() - cached.At < SearchCacheTtlMs)
            {
                okSites = cached.OkSites;
                return cached.Items;
            }

            var sites = VodConfig.EnabledSites().FindAll(s => s.Searchable);
            var buckets = new List<VodItem>[sites.Count];
            int ok = 0, done = 0;
            var tasks = new Task[sites.Count];
            var kkey = NameKey(kw);
            for (int i = 0; i < sites.Count; i++)
            {
                int slot = i;
                var site = sites[slot];
                tasks[slot] = Task.Run(() =>
                {
                    try
                    {
                        int total;
                        var raw = FetchList(site, Math.Max(1, page), null, kw, out total, SearchSiteTimeoutMs);
                        if (raw.Count == 0) { site.WdProbed = true; site.Searchable = false; return; }
                        // 只保留真正命中关键词的：忽略 wd 的站会返回全量列表，别让它污染结果
                        var hit = raw.FindAll(x => NameKey(x.Name).IndexOf(kkey, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (hit.Count == 0) { site.WdProbed = true; site.Searchable = false; return; }
                        buckets[slot] = hit;
                        Interlocked.Increment(ref ok);
                    }
                    catch { }
                    finally { Interlocked.Increment(ref done); }
                });
            }

            // 快速返回：有 2 个站出结果就走，最多等 2.2 秒；慢站继续在后台跑
            int waited = 0;
            while (waited < SearchFastMs
                   && Volatile.Read(ref ok) < 2
                   && Volatile.Read(ref done) < sites.Count)
            {
                Thread.Sleep(60);
                waited += 60;
            }
            var early = FinishSearch(kw, kkey, buckets);
            int okEarly = Volatile.Read(ref ok);
            PutSearchCache(ck, early, okEarly);

            // 收尾：慢站结果并入同一把缓存，下一次输入（或翻页回来）直接是完整结果
            Task.Run(() =>
            {
                try { Task.WaitAll(tasks, 8000); } catch { }
                try
                {
                    var all = FinishSearch(kw, kkey, buckets);
                    if (all.Count > early.Count) PutSearchCache(ck, all, Volatile.Read(ref ok));
                }
                catch { }
            });

            okSites = okEarly;
            return early;
        }

        /* ---------------- 详情聚合（含同名其它站点） ---------------- */

        public static VodItem Detail(string siteKey, string id, string name)
        {
            var all = VodConfig.Sites();
            var it = all.Find(s => s.Key == siteKey);
            if (it == null) return null;
            var item = new VodItem
            {
                Id = id, SiteKey = it.Key, SiteName = it.Name, Api = it.Api,
                Name = name ?? "", Variants = new List<VodItem>()
            };
            // 从首页缓存里补齐标题/封面/简介与其它同名站点变体
            var cache = VodCache.LoadHome(VodConfig.Signature());
            if (cache != null && cache.List != null)
            {
                var self = cache.List.Find(x => x.Key == item.Key);
                if (self != null)
                {
                    item.Name = self.Name; item.Pic = self.Pic; item.Desc = self.Desc;
                    item.Remarks = self.Remarks; item.TypeName = self.TypeName;
                    item.Year = self.Year; item.Area = self.Area; item.Actor = self.Actor;
                }
                var k = NameKey(item.Name);
                if (k.Length > 0)
                {
                    foreach (var x in cache.List)
                    {
                        if (x.Key == item.Key) continue;
                        if (NameKey(x.Name) == k && !item.Variants.Exists(v => v.SiteKey == x.SiteKey))
                            item.Variants.Add(x);
                    }
                }
            }
            item.Variants.Insert(0, item);
            FillDetail(item);
            return item;
        }

        /* ---------------- 海报 ---------------- */

        // 海报拉取失败的「负缓存」：慢 CDN 每张卡都等 9 秒会把整面瀑布墙拖死
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _posterMiss
            = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        public static byte[] PosterBytes(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            var dir = IoPath.Combine(VodConfig.CacheRoot(), "posters");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var file = IoPath.Combine(dir, HashName(url) + ".img");
            try
            {
                if (File.Exists(file))
                {
                    var fi = new FileInfo(file);
                    // 与视频缓存统一 3 天；过期后重新拉，避免海报永久变陈旧
                    if (fi.Length > 100 && fi.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-VideoCacheProxy.CacheExpiryDays))
                        return File.ReadAllBytes(file);
                }
            }
            catch { }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), missAt;
            if (_posterMiss.TryGetValue(url, out missAt) && now - missAt < 10L * 60 * 1000) return null;
            var bytes = HttpGetBytes(url, 6000);
            if (bytes == null || bytes.Length < 100)
            {
                _posterMiss[url] = now;
                return null;
            }
            long dummy;
            _posterMiss.TryRemove(url, out dummy);
            try { File.WriteAllBytes(file, bytes); } catch { }
            return bytes;
        }

        private static string HashName(string s)
        {
            try
            {
                using (var m = System.Security.Cryptography.MD5.Create())
                {
                    var b = m.ComputeHash(Encoding.UTF8.GetBytes(s));
                    var sb = new StringBuilder();
                    foreach (var x in b) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return Math.Abs(s.GetHashCode()).ToString("x8"); }
        }

        /* ---------------- 本地代理地址 ---------------- */

        /// <summary>生成本地回环流地址。absolute=true 给 App 的 VideoView，false 给网页端。
        /// warm=true 时追加 &amp;warm=1，调用方（详情页/播放器）用来在后台预热 m3u8，不真正下载整段。</summary>
        public static string StreamUrl(string url, string referer, bool absolute, bool warm = false)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var q = "/api/media/vod/stream?u=" + System.Uri.EscapeDataString(url);
            if (!string.IsNullOrWhiteSpace(referer)) q += "&r=" + System.Uri.EscapeDataString(referer);
            if (warm) q += "&warm=1";
            return (absolute ? "http://127.0.0.1:6789" : "") + q;
        }

        /// <summary>预热地址：在后台触发一次「解析 + 拉 m3u8 进缓存」，用户真正播放时近乎秒开。</summary>
        public static string WarmUrl(string url, string referer, bool absolute)
        {
            return StreamUrl(url, referer, absolute, true);
        }

        public static string PosterUrl(string url, bool absolute)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var q = "/api/media/vod/poster?u=" + System.Uri.EscapeDataString(url);
            return (absolute ? "http://127.0.0.1:6789" : "") + q;
        }
    }
}

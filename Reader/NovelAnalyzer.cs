// ============================================================
// 阅读模块 · 在线小说链接解析（App 端「解析」页专用）
//
// 参考 uncle-novel-main 的“解析下载”流程：
//   链接 → 目录规则 → 章节列表 → 用户勾选 → 下载器按章抓取。
//
// 本实现优先复用 AhoChat 已有的 legado 书源规则（ruleToc/ruleContent），
// 没有匹配书源时退化为通用 HTML 识别（锚文本/链接特征 + 正文容器探测），
// 因此用户直接粘贴小说目录页或详情页链接也能列出章节。
// 仅 App 端使用，网页端不暴露此能力。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using IoPath = System.IO.Path;

namespace AhoChatA.Reader
{
    /// <summary>一次链接解析的结果。</summary>
    public class NovelParseResult
    {
        public Book Book = new Book();
        public List<Chapter> Chapters = new List<Chapter>();
        public BookSource Source;              // null = 通用解析（无书源规则）
        public string NextPageUrl = "";        // 通用目录的下一页；空 = 没有更多
        public string Message = "";
        public bool RuleBased { get { return Source != null && Source.TocRule != null; } }
        public bool HasMore { get { return !string.IsNullOrEmpty(NextPageUrl); } }
        public string SourceName
        {
            get
            {
                if (Source != null && !string.IsNullOrWhiteSpace(Source.BookSourceName))
                    return Source.BookSourceName;
                if (Source != null && !string.IsNullOrWhiteSpace(Source.BookSourceUrl))
                    return ReaderLibrary.HostOf(Source.BookSourceUrl);
                return L10n.T("通用解析");
            }
        }
    }

    public static class NovelAnalyzer
    {
        private const int MaxTocPages = 12;      // 通用目录最多自动翻 12 页，避免一次粘链接就抓几千页
        private const int MaxChapterPages = 12;  // 单章最多跟 12 个续页（如 0_2.html），避免误抓整本书
        private const int MinChapters = 2;       // 少于 2 章的“目录”基本是误判

        private static readonly Regex ChapterTitleRegex = new Regex(
            @"(第\s*[0-9零一二三四五六七八九十百千万两]+\s*[章节回卷部篇集话])" +
            @"|([0-9]{1,5}\s*[章节回卷部篇集话])" +
            @"|(chapter\s*[0-9]+)" +
            @"|(^[-—·\s]*[0-9]{1,5}[-—·\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ChapterUrlRegex = new Regex(
            @"/(chapter|chapters|read|toc|mulu|zhang|chapter_[0-9]+|chapter-[0-9]+)(/|_|-|\.|$)" +
            @"|\bch(?:apter)?[_/-]?[0-9]{1,6}\b" +
            @"|\b[0-9]{1,6}\.html?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NavTextRegex = new Regex(
            @"^(下一页|下页|上一页|上页|首页|末页|更多|更多章节|查看更多|返回目录|目录|全部章节|完结|简介|书架|加入书架|版权|下载)$",
            RegexOptions.Compiled);

        private static readonly string[] ContentSelectors =
        {
            // 笔趣阁类站点：#content 是整块面板，真正的正文在 #htmlContent
            "#htmlContent", ".showtxt", ".read_text", ".txtbi",
            "#content", "#chaptercontent", "#chapter-content", "#bookcontent", "#article", "#articlecontent",
            ".chapter-content", ".chaptercontent", ".read-content", ".readcontent", ".content", ".article",
            ".neirong", ".text", ".post-content", "article", "main"
        };

        /* ---------------- 入口 ---------------- */

        /// <summary>把用户输入规整成可请求的 http(s) 链接。</summary>
        public static string NormalizeUrl(string raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) return "";
            if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return "";
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return "";
            return u.AbsoluteUri;
        }

        /// <summary>解析链接：优先书源规则，失败则用通用网页识别。</summary>
        public static async Task<NovelParseResult> ParseAsync(string rawUrl, Action<string> progress = null)
        {
            var result = new NovelParseResult();
            var url = NormalizeUrl(rawUrl);
            if (string.IsNullOrEmpty(url))
            {
                result.Message = L10n.T("请输入有效的 http/https 链接");
                return result;
            }

            Report(progress, L10n.T("正在匹配书源规则…"));
            var src = FindSource(url);
            // 只复用真正属于该站点的书源：有些用户书源用宽泛正则匹配到别的站，
            // 拿它的 content 规则抓正文会把导航/按钮一起抓进来。
            if (src != null && !HostMatches(src.BookSourceUrl, url)) src = null;
            var book = new Book
            {
                Type = "online",
                Name = "",
                BookUrl = url,
                TocUrl = url,
                SourceUrl = src == null ? "" : src.BookSourceUrl
            };

            // 1) 已导入的 legado 书源：沿用本程序成熟的目录/正文规则
            if (src != null && src.TocRule != null &&
                !string.IsNullOrWhiteSpace(src.TocRule.ChapterList))
            {
                try
                {
                    Report(progress, L10n.T("正在按书源规则读取目录…"));
                    var engine = ReaderLibrary.Engine;
                    await engine.GetBookInfoAsync(src, book).ConfigureAwait(false);
                    var chapters = await engine.GetTocAsync(src, book).ConfigureAwait(false);
                    if (chapters != null && chapters.Count > 0)
                    {
                        result.Book = book;
                        result.Source = src;
                        result.Chapters = chapters;
                        result.Message = L10n.T("按书源规则解析成功");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("NovelParse", "规则解析失败，转通用解析: " + ex.Message);
                }
            }

            // 2) 通用解析：目录页 → （若是详情页）跳到目录链接 → 锚文本/URL 特征识别
            Report(progress, L10n.T("正在通用解析目录…"));
            var engine2 = ReaderLibrary.Engine;
            var html = await engine2.FetchAsync(url, src == null ? null : src.ParseHeader()).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                result.Message = L10n.T("网页读取失败，请检查链接或网络");
                return result;
            }

            string next;
            var chapters2 = ChaptersFromHtml(html, url, out next);
            Android.Util.Log.Info("NovelParse",
                "通用目录 " + url + " HTML=" + html.Length + "B, 章节=" + chapters2.Count + ", 下一页=" + next);
            if (chapters2.Count < MinChapters)
            {
                var tocLink = FindTocLink(html, url);
                if (!string.IsNullOrEmpty(tocLink) && tocLink != url)
                {
                    Report(progress, L10n.T("检测到详情页，正在打开目录…"));
                    var tocHtml = await engine2.FetchAsync(tocLink, src == null ? null : src.ParseHeader()).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(tocHtml))
                    {
                        var more = ChaptersFromHtml(tocHtml, tocLink, out next);
                        if (more.Count > chapters2.Count) { chapters2 = more; url = tocLink; }
                    }
                }
            }

            if (chapters2.Count == 0)
            {
                result.Message = L10n.T("没有识别到章节链接；可尝试导入该书源规则后再解析");
                return result;
            }

            book.TocUrl = url;
            book.Name = TitleFromHtml(html, url);
            result.Book = book;
            result.Source = src;               // 即使目录用通用解析，正文仍可复用书源的 content 规则
            result.Chapters = chapters2;
            result.NextPageUrl = next;
            result.Message = L10n.T("通用解析成功");

            // 参考 uncle-novel-main 的目录体验：常见站点有多页目录，先把可见页自动补齐，
            // 超过上限仍保留“加载更多”按钮，避免一次粘链接就发起无限请求。
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };
            int pages = 1;
            while (!string.IsNullOrEmpty(result.NextPageUrl) && pages < MaxTocPages)
            {
                if (!visited.Add(result.NextPageUrl))
                {
                    result.NextPageUrl = "";
                    break;
                }
                Report(progress, L10n.T("正在加载更多章节…"));
                var added = await LoadMoreAsync(result).ConfigureAwait(false);
                if (added == 0 && string.IsNullOrEmpty(result.NextPageUrl)) break;
                pages++;
            }
            return result;
        }

        /// <summary>通用目录加载下一页，追加到 result.Chapters，返回新增章数。</summary>
        public static async Task<int> LoadMoreAsync(NovelParseResult result, Action<string> progress = null)
        {
            if (result == null || string.IsNullOrEmpty(result.NextPageUrl)) return 0;
            var url = result.NextPageUrl;
            Report(progress, L10n.T("正在加载更多章节…"));
            var headers = result.Source == null ? null : result.Source.ParseHeader();
            var html = await ReaderLibrary.Engine.FetchAsync(url, headers).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html)) return 0;

            string next;
            var got = ChaptersFromHtml(html, url, out next);
            result.NextPageUrl = next;
            var seen = new HashSet<string>(result.Chapters.Select(c => c.Url ?? ""), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var ch in got)
            {
                if (!seen.Add(ch.Url ?? "")) continue;
                ch.Index = result.Chapters.Count;
                result.Chapters.Add(ch);
                added++;
            }
            return added;
        }

        /* ---------------- 书源匹配 ---------------- */

        private static bool HostMatches(string sourceUrl, string targetUrl)
        {
            var a = ReaderLibrary.HostOf(sourceUrl ?? "");
            var b = ReaderLibrary.HostOf(targetUrl ?? "");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase) ||
                   b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按 host / bookUrlPattern 从已导入书源里挑最合适的一个。</summary>
        public static BookSource FindSource(string url)
        {
            try
            {
                var host = ReaderLibrary.HostOf(url);
                if (string.IsNullOrEmpty(host)) return null;
                BookSource best = null;
                int bestScore = 0;
                foreach (var s in ReaderLibrary.Sources())
                {
                    if (s == null || !s.Enabled) continue;
                    int score = 0;
                    var sh = ReaderLibrary.HostOf(s.BookSourceUrl ?? "");
                    if (!string.IsNullOrEmpty(sh))
                    {
                        if (string.Equals(sh, host, StringComparison.OrdinalIgnoreCase)) score += 100;
                        else if (host.EndsWith("." + sh, StringComparison.OrdinalIgnoreCase) ||
                                 sh.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)) score += 60;
                    }
                    if (!string.IsNullOrWhiteSpace(s.BookUrlPattern))
                    {
                        try { if (Regex.IsMatch(url, s.BookUrlPattern, RegexOptions.IgnoreCase)) score += 80; }
                        catch { }
                    }
                    if (s.TocRule != null && !string.IsNullOrWhiteSpace(s.TocRule.ChapterList)) score += 10;
                    if (score > bestScore) { bestScore = score; best = s; }
                }
                return best;
            }
            catch { return null; }
        }

        /* ---------------- 通用目录解析 ---------------- */

        private sealed class Candidate
        {
            public string Title;
            public string Url;
            public int Order;
        }

        private static List<Chapter> ChaptersFromHtml(string html, string baseUrl, out string nextUrl)
        {
            nextUrl = "";
            var list = new List<Chapter>();
            if (string.IsNullOrWhiteSpace(html)) return list;
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
            if (anchors == null) return list;

            Uri baseUri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri)) return list;

            var groups = new Dictionary<string, List<Candidate>>();
            int order = 0;
            foreach (var a in anchors)
            {
                order++;
                var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                if (href.Length == 0 || href.StartsWith("#") ||
                    href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;

                var title = CleanText(a.InnerText);
                if (title.Length == 0 || title.Length > 80 || NavTextRegex.IsMatch(title)) continue;
                if (!ChapterTitleRegex.IsMatch(title) && !ChapterUrlRegex.IsMatch(href)) continue;

                Uri abs;
                if (!Uri.TryCreate(baseUri, href, out abs)) continue;
                if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) continue;

                var key = GroupKey(abs.AbsoluteUri);
                List<Candidate> g;
                if (!groups.TryGetValue(key, out g)) groups[key] = g = new List<Candidate>();
                g.Add(new Candidate { Title = title, Url = abs.AbsoluteUri, Order = order });
            }

            List<Candidate> best = null;
            foreach (var kv in groups)
            {
                if (kv.Value.Count < MinChapters) continue;
                if (best == null || kv.Value.Count > best.Count) best = kv.Value;
            }
            if (best == null) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in best.OrderBy(x => x.Order))
            {
                if (!seen.Add(c.Url)) continue;
                list.Add(new Chapter
                {
                    Index = list.Count,
                    Title = c.Title,
                    Url = c.Url
                });
            }

            // 找“下一页”：用于用户主动点“加载更多”，不自动无限制抓取
            foreach (var a in anchors)
            {
                var rel = (a.GetAttributeValue("rel", "") ?? "").Trim();
                var text = CleanText(a.InnerText);
                if (!string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(text, @"^(下一页|下页|更多章节|查看更多|更多)$")) continue;
                var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                Uri u;
                if (Uri.TryCreate(baseUri, href, out u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) &&
                    !seen.Contains(u.AbsoluteUri))
                {
                    nextUrl = u.AbsoluteUri;
                    break;
                }
            }
            return list;
        }

        private static string GroupKey(string url)
        {
            var s = url.Split('#')[0].Split('?')[0];
            // 把路径中的数字段归一化：/book/123/456.html → /book/{#}/{#}.html
            return Regex.Replace(s, @"/\d+(?=[/.]|$)", "/{#}");
        }

        private static string FindTocLink(string html, string baseUrl)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                Uri baseUri;
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri)) return "";
                var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
                if (anchors == null) return "";
                foreach (var a in anchors)
                {
                    var text = CleanText(a.InnerText);
                    if (!Regex.IsMatch(text, @"(目录|章节目录|全部章节|查看目录)") || text.Length > 14) continue;
                    var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                    Uri u;
                    if (Uri.TryCreate(baseUri, href, out u) &&
                        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                        return u.AbsoluteUri;
                }
            }
            catch { }
            return "";
        }

        private static string TitleFromHtml(string html, string url)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var metas = doc.DocumentNode.SelectNodes("//meta");
                if (metas != null)
                {
                    foreach (var m in metas)
                    {
                        var key = (m.GetAttributeValue("property", "") ?? "").ToLowerInvariant();
                        if (string.IsNullOrEmpty(key)) key = (m.GetAttributeValue("name", "") ?? "").ToLowerInvariant();
                        if (key != "og:novel:book_name" && key != "og:title" && key != "author") continue;
                        var v = CleanText(m.GetAttributeValue("content", ""));
                        if (v.Length > 1 && v.Length < 80 && key != "author") return CleanTitle(v);
                    }
                }
                var h1 = doc.DocumentNode.QuerySelector("h1");
                if (h1 != null)
                {
                    var v = CleanText(h1.InnerText);
                    if (v.Length > 1 && v.Length < 80) return CleanTitle(v);
                }
                var title = doc.DocumentNode.SelectSingleNode("//title");
                if (title != null) return CleanTitle(CleanText(title.InnerText));
            }
            catch { }
            return ReaderLibrary.NormalizeName(new Uri(url).Segments.LastOrDefault() ?? L10n.T("未命名书籍"));
        }

        private static string CleanTitle(string s)
        {
            s = CleanText(s);
            s = Regex.Replace(s, @"[-_|·—]+\s*[^|_-]{0,16}(小说|文学|阅读|网|书|笔趣|书屋|在线)$", "", RegexOptions.IgnoreCase);
            if (s.Length < 1 || s.Length > 80) return L10n.T("未命名书籍");
            return s;
        }

        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return HtmlEntity.DeEntitize(Regex.Replace(s, @"\s+", " ")).Trim();
        }

        /* ---------------- 正文 ---------------- */

        /// <summary>
        /// 下载器用：优先书源 content 规则；无规则时按常见正文容器探测。
        /// </summary>
        public static async Task<string> ChapterTextAsync(BookSource src, Chapter ch)
        {
            if (ch == null || string.IsNullOrEmpty(ch.Url)) return "";
            var engine = ReaderLibrary.Engine;
            if (src != null && src.ContentRule != null &&
                !string.IsNullOrWhiteSpace(src.ContentRule.Content))
            {
                try
                {
                    var txt = await engine.FetchContentAsync(src, ch.Url).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(txt)) return txt;
                }
                catch { }
            }

            var headers = src == null ? null : src.ParseHeader();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = ch.Url;
            var htmlParts = new List<string>();

            // 笔趣阁等站点会把一章拆成 0.html / 0_2.html，目录只列第一页；
            // 只跟同章续页，不跟“下一章”，否则会把整本书串进一章。
            for (int page = 0; page < MaxChapterPages && !string.IsNullOrEmpty(current); page++)
            {
                if (!visited.Add(current)) break;
                var html = await engine.FetchAsync(current, headers).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html)) break;

                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var next = FindChapterContinuationUrl(doc, current);
                var junk = doc.DocumentNode.SelectNodes("//script|//style|//nav|//header|//footer|//aside|//form|//iframe|//svg|//select|//option|//button");
                if (junk != null) foreach (var n in junk) n.Remove();

                HtmlNode best = null;
                // 选择器按精度排序：命中第一个足够长的容器就停。
                // 不能取“最长节点”，否则 #content 这类外层面板会把导航和按钮一起吞进来。
                foreach (var sel in ContentSelectors)
                {
                    HtmlNode node = null;
                    try { node = doc.DocumentNode.QuerySelector(sel); } catch { }
                    if (node == null) continue;
                    if (CleanText(node.InnerText).Length < 80) continue;
                    best = node;
                    break;
                }
                if (best == null)
                {
                    var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
                    best = body;
                }
                htmlParts.Add(best.InnerHtml);

                if (string.IsNullOrEmpty(next) || visited.Contains(next)) break;
                current = next;
            }

            if (htmlParts.Count == 0) return "";
            var text = BookParser.HtmlToText(string.Join("\n", htmlParts.ToArray()));
            // 去掉常见的“上一章/下一章/目录”导航尾巴
            text = Regex.Replace(text, @"(?m)^\s*(上一章|下一章|上一页|下一页|返回目录|加入书签|目录)\s*[：:]?.*$", "");
            return AnalyzeRule.CleanBlock(text);
        }

        /// <summary>只识别同一章的分页续页（0.html → 0_2.html），不把下一章当成续页。</summary>
        private static string FindChapterContinuationUrl(HtmlDocument doc, string chapterUrl)
        {
            try
            {
                Uri current;
                if (!Uri.TryCreate(chapterUrl, UriKind.Absolute, out current)) return "";
                var currentName = Uri.UnescapeDataString(IoPath.GetFileName(current.AbsolutePath));
                var currentBase = Regex.Replace(currentName, @"\.(html?|htm)$", "", RegexOptions.IgnoreCase);
                var suffix = Regex.Match(currentBase, @"_(?<n>\d+)$");
                var stem = suffix.Success ? currentBase.Substring(0, suffix.Index) : currentBase;
                int pageNo = suffix.Success ? int.Parse(suffix.Groups["n"].Value) : 1;
                var dir = current.AbsoluteUri.Substring(0, current.AbsoluteUri.Length - currentName.Length);

                var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
                if (anchors == null) return "";
                foreach (var a in anchors)
                {
                    var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                    if (href.Length == 0 || href.StartsWith("#") ||
                        href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                    // 不靠“下一页/下一章”文案判断：该站把同章续页也写成“下一章”。
                    // 只要 href 是同章 stem_N.html 且 N 递增，就一定是续页；下一章 1.html 不会匹配。
                    Uri next;
                    if (!Uri.TryCreate(current, href, out next)) continue;
                    var nextName = Uri.UnescapeDataString(IoPath.GetFileName(next.AbsolutePath));
                    var nextBase = Regex.Replace(nextName, @"\.(html?|htm)$", "", RegexOptions.IgnoreCase);
                    var m = Regex.Match(nextBase, @"^" + Regex.Escape(stem) + @"_(?<n>\d+)$");
                    if (!m.Success) continue;
                    int n;
                    if (!int.TryParse(m.Groups["n"].Value, out n) || n <= pageNo) continue;
                    var nextDir = next.AbsoluteUri.Substring(0, next.AbsoluteUri.Length - nextName.Length);
                    if (!string.Equals(nextDir, dir, StringComparison.OrdinalIgnoreCase)) continue;
                    return next.AbsoluteUri;
                }
            }
            catch { }
            return "";
        }

        private static void Report(Action<string> progress, string msg)
        {
            try { progress?.Invoke(msg); } catch { }
        }
    }
}

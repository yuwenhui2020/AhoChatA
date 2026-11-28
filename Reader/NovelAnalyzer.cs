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
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
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

    /// <summary>章节预览：正文、实际跟随的同章页数和可疑内容提示。</summary>
    public sealed class ChapterPreview
    {
        public string Text = "";
        public int PageCount;
        public bool ReachedPageLimit;
        public readonly List<string> Warnings = new List<string>();
    }

    public static class NovelAnalyzer
    {
        // apibi.cc 的证书在部分国内 Android 环境不被信任；只对这一个主机放宽校验，
        // 其它站点仍走 ReaderEngine 的标准证书校验。
        private static readonly HttpClient _apibiHttp = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
                req != null && req.RequestUri != null && IsApibiHost(req.RequestUri.Host)
        }) { Timeout = TimeSpan.FromSeconds(30) };

        private const int MaxTocPages = 120;     // 目录多页时自动补齐；超过上限仍保留“加载更多”按钮
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

        // 章号只认「章/回/话」和 chapter N；卷号不能被误当作章号。
        private static readonly Regex ChapterNumberRegex = new Regex(
            @"第\s*([0-9零一二三四五六七八九十百千万两]+)\s*[章节回话]" +
            @"|chapter\s*([0-9]{1,6})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LooseChapterNumberRegex = new Regex(
            @"(?<![0-9])([0-9]{1,6})\s*[章节回话]", RegexOptions.Compiled);

        // 分卷标识只取章号前面的部分，避免把章名里的「新篇章」误当成卷名。
        private static readonly Regex NumericVolumeRegex = new Regex(
            @"第\s*([0-9零一二三四五六七八九十百千万两]+)\s*([卷部篇集])" +
            @"|([卷部篇集])\s*([0-9零一二三四五六七八九十百千万两]+)", RegexOptions.Compiled);

        private static readonly Regex NamedVolumeRegex = new Regex(
            @"番外篇|番外|外传|正篇|新篇|终篇|旧篇|续篇|前传|后传|特别篇|上卷|下卷|上部|下部|上册|下册",
            RegexOptions.Compiled);

        private static readonly Regex NavTextRegex = new Regex(
            @"^(下一页|下页|上一页|上页|首页|末页|更多|更多章节|查看更多|返回目录|目录|章节目录|全部章节|最新章节|最新九章|" +
            @"完结|简介|详情|书架|加入书架|收藏|推荐|版权|下载|txt下载|全文阅读|免费阅读|开始阅读|立即阅读|在线阅读|手机阅读|电脑阅读)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] ContentSelectors =
        {
            // 笔趣阁类站点：桌面版正文在 #htmlContent；移动版正文在 #tts-content / #nr。
            // 不能先命中外层面板，否则会把「字体/关灯/回目录/推荐」等按钮一起抓进来。
            "#htmlContent", "#tts-content", "#nr", ".showtxt", ".read_text", ".txtbi",
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

            // biquge.mom 的目录/正文由前端接口提供，HTML 里只有「正在打开这本书」。
            int momId;
            if (TryBiqugeMomId(url, out momId))
                return await ParseBiqugeMomAsync(url, momId, progress).ConfigureAwait(false);

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
                    // 书源没给封面时，从目录页自己找（og:image / #fmimg / .cover 等）
                    if (string.IsNullOrEmpty(book.CoverUrl))
                    {
                        var infoUrl = string.IsNullOrEmpty(book.BookUrl) ? book.TocUrl : book.BookUrl;
                        try
                        {
                            var infoHtml = await engine.FetchAsync(infoUrl, src.ParseHeader()).ConfigureAwait(false);
                            book.CoverUrl = CoverFromHtml(infoHtml, infoUrl);
                        }
                        catch { }
                    }
                    var chapters = await engine.GetTocAsync(src, book).ConfigureAwait(false);
                    if (chapters != null && chapters.Count > 0)
                    {
                        SortChapters(chapters);
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
            string tocHtml = "";
            string tocLink = "";
            if (chapters2.Count < MinChapters)
            {
                tocLink = FindTocLink(html, url);
                if (!string.IsNullOrEmpty(tocLink) && tocLink != url)
                {
                    Report(progress, L10n.T("检测到详情页，正在打开目录…"));
                    tocHtml = await engine2.FetchAsync(tocLink, src == null ? null : src.ParseHeader()).ConfigureAwait(false);
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
            SortChapters(chapters2);

            book.TocUrl = url;
            book.Name = TitleFromHtml(html, url);
            // 从目录页/详情页自取封面：og:image 优先，其次 #fmimg / .cover 中的真实图片
            book.CoverUrl = CoverFromHtml(html, url);
            if (string.IsNullOrEmpty(book.CoverUrl) && !string.IsNullOrEmpty(tocHtml))
                book.CoverUrl = CoverFromHtml(tocHtml, tocLink);
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
                Report(progress, L10n.T("正在加载更多章节…") + " " + (pages + 1));
                var before = result.NextPageUrl;
                var added = await LoadMoreAsync(result).ConfigureAwait(false);
                // 读取失败时 LoadMoreAsync 会保留原下一页地址，供用户点「加载更多」重试；
                // 这里不能因为 added==0 就把地址清掉，否则剩余目录永久丢失。
                if (added == 0 && string.Equals(before, result.NextPageUrl, StringComparison.OrdinalIgnoreCase)) break;
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
            string html = "";
            // 目录页偶发 502/空壳响应，重试几次；仍失败时保留 NextPageUrl，便于用户点「加载更多」重试。
            for (int attempt = 0; attempt < 3 && string.IsNullOrWhiteSpace(html); attempt++)
            {
                if (attempt > 0)
                {
                    Report(progress, L10n.T("目录下一页读取失败，正在重试…") + " " + (attempt + 1));
                    await Task.Delay(700 * attempt).ConfigureAwait(false);
                }
                try
                {
                    html = await ReaderLibrary.Engine.FetchAsync(url, headers).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("NovelParse", "目录下一页失败: " + ex.Message);
                    html = "";
                }
            }
            if (string.IsNullOrWhiteSpace(html))
            {
                Report(progress, L10n.T("目录下一页读取失败，可点「加载更多」重试"));
                return 0;
            }

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
            if (added > 0) SortChapters(result.Chapters);
            return added;
        }

        /* ---------------- biquge.mom 接口解析 ---------------- */

        private static bool IsApibiHost(string host)
        {
            return string.Equals(host, "apibi.cc", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "api.apibi.cc", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "www.apibi.cc", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryBiqugeMomId(string url, out int id)
        {
            id = 0;
            try
            {
                Uri u;
                if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return false;
                if (!string.Equals(u.Host, "www.biquge.mom", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(u.Host, "biquge.mom", StringComparison.OrdinalIgnoreCase)) return false;
                var m = Regex.Match(u.Query, @"[?&]id=(\d+)", RegexOptions.IgnoreCase);
                return m.Success && int.TryParse(m.Groups[1].Value, out id) && id > 0;
            }
            catch { return false; }
        }

        private static bool TryBiqugeMomChapter(string url, out int bookId, out int chapterId)
        {
            bookId = 0; chapterId = 0;
            try
            {
                Uri u;
                if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return false;
                if (!string.Equals(u.Host, "www.biquge.mom", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(u.Host, "biquge.mom", StringComparison.OrdinalIgnoreCase)) return false;
                var a = Regex.Match(u.Query, @"[?&]novelId=(\d+)", RegexOptions.IgnoreCase);
                var b = Regex.Match(u.Query, @"[?&]chapter=(\d+)", RegexOptions.IgnoreCase);
                return a.Success && b.Success &&
                       int.TryParse(a.Groups[1].Value, out bookId) &&
                       int.TryParse(b.Groups[1].Value, out chapterId);
            }
            catch { return false; }
        }

        private static string ApibiToken(string jsonParams)
        {
            // apibi.cc 前端公开协议：md5('book@token.html') 前 16 位作 IV、后 16 位作 AES-128 key。
            var key = Encoding.ASCII.GetBytes("a3dc22cf70418a51");
            var iv = Encoding.ASCII.GetBytes("394c2c3202da6270");
            var data = Encoding.UTF8.GetBytes(jsonParams);
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var enc = aes.CreateEncryptor())
                    return Convert.ToBase64String(enc.TransformFinalBlock(data, 0, data.Length));
            }
        }

        private static async Task<string> ApibiGetAsync(string path, string jsonParams)
        {
            var token = Uri.EscapeDataString(ApibiToken(jsonParams));
            var url = "https://api.apibi.cc/api/" + path + "?token=" + token;
            try
            {
                return await _apibiHttp.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("NovelParse", "apibi 请求失败 " + path + ": " + ex);
                return "";
            }
        }

        private static async Task<NovelParseResult> ParseBiqugeMomAsync(string url, int id, Action<string> progress)
        {
            var result = new NovelParseResult();
            Report(progress, L10n.T("正在读取站点接口目录…"));
            var raw = await ApibiGetAsync("booklist", "{\"id\":" + id + "}").ConfigureAwait(false);
            JObject listJson = null;
            try { listJson = JObject.Parse(raw); } catch { }
            var arr = listJson == null ? null : listJson["list"] as JArray;
            if (arr == null || arr.Count == 0)
            {
                result.Message = L10n.T("网页读取失败，请检查链接或网络");
                return result;
            }

            var title = L10n.T("未命名书籍") + " " + id;
            var momCover = "";
            try
            {
                var infoRaw = await ApibiGetAsync("book", "{\"id\":" + id + "}").ConfigureAwait(false);
                var info = JObject.Parse(infoRaw);
                var t = info["title"] == null ? "" : info["title"].ToString().Trim();
                if (t.Length > 0) title = t;
                var cv = info["cover"] ?? info["pic"] ?? info["img"] ?? info["image"];
                if (cv != null)
                {
                    var cover = CoverUrl(cv.ToString(), new Uri("https://www.biquge.mom/"));
                    momCover = cover;
                }
            }
            catch { }

            var book = new Book
            {
                Type = "online",
                Name = title,
                BookUrl = url,
                TocUrl = url,
                CoverUrl = momCover,
                SourceUrl = "https://www.biquge.mom/"
            };
            var chapters = new List<Chapter>();
            for (int i = 0; i < arr.Count; i++)
            {
                var name = arr[i] == null ? "" : arr[i].ToString().Trim();
                if (name.Length == 0) continue;
                chapters.Add(new Chapter
                {
                    Index = chapters.Count,
                    Title = name,
                    Url = "https://www.biquge.mom/reader?novelId=" + id + "&chapter=" + (i + 1)
                });
            }
            SortChapters(chapters);
            result.Book = book;
            result.Source = new BookSource
            {
                BookSourceName = "笔趣阁(mom)",
                BookSourceUrl = "https://www.biquge.mom/",
                Enabled = true
            };
            result.Chapters = chapters;
            result.Message = L10n.T("通用解析成功");
            return result;
        }

        /* ---------------- 章节排序 ---------------- */

        /// <summary>按“第1章 / 第一章 / 第001章”排序；分卷单独成组；无章号时保持原顺序。</summary>
        public static void SortChapters(List<Chapter> chapters)
        {
            if (chapters == null || chapters.Count < 2) return;
            int count = chapters.Count;
            var numbers = new int[count];
            var volumes = new string[count];
            int parsed = 0;
            for (int i = 0; i < count; i++)
            {
                var title = NormalizeTitleDigits(chapters[i] == null ? "" : chapters[i].Title);
                var match = ChapterNumberMatch(title);
                int n;
                numbers[i] = TryChapterNumberFromMatch(match, out n) ? n : 0;
                if (numbers[i] > 0) parsed++;
                volumes[i] = VolumeKey(title, match);
            }
            // 绝大多数标题都没有章号，就完全尊重站点原顺序。
            if (parsed * 2 < count) return;

            // 同一卷可能从 1 重新开始编号。卷之间保持目录顺序，卷内才按章号排。
            // 用最后一次出现的位置定卷序：目录顶部常有「最新章节」重复引用，
            // 若用首次出现位置，终篇会被误排到正文前面。
            var volumeOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++) volumeOrder[volumes[i]] = i;

            var order = Enumerable.Range(0, count).ToList();
            order.Sort((a, b) =>
            {
                int va, vb;
                volumeOrder.TryGetValue(volumes[a], out va);
                volumeOrder.TryGetValue(volumes[b], out vb);
                int c = va.CompareTo(vb);
                if (c != 0) return c;
                if (numbers[a] > 0 && numbers[b] > 0)
                {
                    c = numbers[a].CompareTo(numbers[b]);
                    if (c != 0) return c;
                }
                // 无章号的条目或同章号，保持原本先后顺序。
                return a.CompareTo(b);
            });
            var sorted = new List<Chapter>(count);
            foreach (var i in order) sorted.Add(chapters[i]);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i] != null) sorted[i].Index = i;
                chapters[i] = sorted[i];
            }
        }

        private static string NormalizeTitleDigits(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            return title.Replace('０', '0').Replace('１', '1').Replace('２', '2')
                        .Replace('３', '3').Replace('４', '4').Replace('５', '5')
                        .Replace('６', '6').Replace('７', '7').Replace('８', '8').Replace('９', '9');
        }

        private static Match ChapterNumberMatch(string normalizedTitle)
        {
            var m = ChapterNumberRegex.Match(normalizedTitle ?? "");
            if (!m.Success) m = LooseChapterNumberRegex.Match(normalizedTitle ?? "");
            return m;
        }

        private static bool TryChapterNumberFromMatch(Match m, out int number)
        {
            number = 0;
            if (m == null || !m.Success) return false;
            var token = m.Groups.Count > 2 && m.Groups[2].Success
                ? m.Groups[2].Value
                : m.Groups[1].Value;
            return TryParseNumberToken(token, out number);
        }

        private static bool TryParseNumberToken(string token, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(token)) return false;
            if (Regex.IsMatch(token, "^[0-9]+$"))
                return int.TryParse(token, out number) && number > 0;
            number = ChineseNumber(token);
            return number > 0;
        }

        /// <summary>取章号前面的卷/篇/部标识；取不到时归入默认卷。</summary>
        private static string VolumeKey(string normalizedTitle, Match chapterMatch)
        {
            if (string.IsNullOrWhiteSpace(normalizedTitle)) return "";
            int end = chapterMatch != null && chapterMatch.Success
                ? chapterMatch.Index
                : normalizedTitle.Length;
            if (end <= 0) return "";
            var prefix = normalizedTitle.Substring(0, end);

            var numeric = NumericVolumeRegex.Match(prefix);
            if (numeric.Success)
            {
                var token = numeric.Groups[1].Success ? numeric.Groups[1].Value : numeric.Groups[4].Value;
                var unit = numeric.Groups[2].Success ? numeric.Groups[2].Value : numeric.Groups[3].Value;
                int n;
                if (TryParseNumberToken(token, out n)) return unit + n;
            }

            var named = NamedVolumeRegex.Match(prefix);
            if (!named.Success) return "";
            var name = named.Value;
            if (name == "番外篇" || name == "番外") return "番外";
            if (name == "上卷" || name == "上册" || name == "上部") return "上";
            if (name == "下卷" || name == "下册" || name == "下部") return "下";
            return name;
        }

        private static bool TryChapterNumber(string title, out int number)
        {
            number = 0;
            var match = ChapterNumberMatch(NormalizeTitleDigits(title));
            return TryChapterNumberFromMatch(match, out number);
        }

        private static int ChineseNumber(string s)
        {
            int total = 0, section = 0, num = 0;
            foreach (var c in s)
            {
                int d = ChineseDigit(c);
                if (d >= 0) { num = d; continue; }
                if (c == '十' || c == '百' || c == '千')
                {
                    var unit = c == '十' ? 10 : (c == '百' ? 100 : 1000);
                    section += (num == 0 ? 1 : num) * unit;
                    num = 0;
                }
                else if (c == '万')
                {
                    section += num;
                    total += (section == 0 ? 1 : section) * 10000;
                    section = 0; num = 0;
                }
                else if (c == '亿')
                {
                    section += num;
                    total += (section == 0 ? 1 : section) * 100000000;
                    section = 0; num = 0;
                }
            }
            return total + section + num;
        }

        private static int ChineseDigit(char c)
        {
            switch (c)
            {
                case '零': return 0;
                case '一': return 1;
                case '二': return 2;
                case '两': return 2;
                case '三': return 3;
                case '四': return 4;
                case '五': return 5;
                case '六': return 6;
                case '七': return 7;
                case '八': return 8;
                case '九': return 9;
                default: return -1;
            }
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
            var doc = LoadDoc(html);
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
            var chosen = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in best.OrderBy(x => x.Order))
            {
                Candidate old;
                if (!chosen.TryGetValue(c.Url, out old))
                {
                    chosen[c.Url] = c;
                    continue;
                }
                // 同一章 URL 常被「开始阅读/免费阅读/最新九章」重复引用：
                // 优先保留真正带章号的标题，否则第一章会被按钮文案占用。
                var oldNumbered = ChapterTitleRegex.IsMatch(old.Title ?? "");
                var newNumbered = ChapterTitleRegex.IsMatch(c.Title ?? "");
                if (newNumbered && !oldNumbered) chosen[c.Url] = c;
            }
            foreach (var c in chosen.Values.OrderBy(x => x.Order))
            {
                if (!seen.Add(c.Url)) continue;
                list.Add(new Chapter
                {
                    Index = list.Count,
                    Title = c.Title,
                    Url = c.Url
                });
            }

            // 找“下一页”：兼容 rel/class/id/onclick；只认明确的翻页文案，不误抓“下一章”。
            foreach (var a in anchors)
            {
                var rel = (a.GetAttributeValue("rel", "") ?? "").Trim();
                var cls = (a.GetAttributeValue("class", "") ?? "").Trim();
                var id = (a.GetAttributeValue("id", "") ?? "").Trim();
                var text = CleanText(a.InnerText);
                bool looksNext =
                    string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(text, @"^(下一页|下页|后一页|更多章节|查看更多|更多)$") ||
                    Regex.IsMatch(cls + " " + id, @"(^|[\s_-])(next|page-next|xiayiye)\b", RegexOptions.IgnoreCase);
                if (!looksNext) continue;
                var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                if (IsPlaceholderHref(href))
                {
                    var jsUrl = ExtractLocationFromJs(a.GetAttributeValue("onclick", ""));
                    if (!string.IsNullOrEmpty(jsUrl)) href = jsUrl;
                }
                if (IsPlaceholderHref(href)) continue;
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
                var doc = LoadDoc(html);
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
                var doc = LoadDoc(html);
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

        /// <summary>站点通用占位图 / 图标，不能当书籍封面用。</summary>
        private static readonly Regex CoverPlaceholderRegex = new Regex(
            @"nocover|no-cover|noimg|no-img|placeholder|default|loading|blank|transparent|favicon|logo|og\.(png|jpe?g)|share\.(png|jpe?g)|/public/img/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 从目录页 HTML 里找书籍封面：og:image / twitter:image 优先，
        /// 其次常见笔趣阁结构里的 #fmimg、.cover、.bookcover 图片。
        /// 懒加载站点的 data-src / data-original 优先于 src。
        /// </summary>
        public static string CoverFromHtml(string html, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            try
            {
                Uri baseUri;
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri)) return "";
                var doc = LoadDoc(html);

                var metas = doc.DocumentNode.SelectNodes("//meta");
                if (metas != null)
                {
                    foreach (var m in metas)
                    {
                        var key = (m.GetAttributeValue("property", "") ?? "").ToLowerInvariant();
                        if (string.IsNullOrEmpty(key)) key = (m.GetAttributeValue("name", "") ?? "").ToLowerInvariant();
                        if (key != "og:image" && key != "og:image:url" && key != "twitter:image") continue;
                        var cover = CoverUrl(m.GetAttributeValue("content", ""), baseUri);
                        if (!string.IsNullOrEmpty(cover)) return cover;
                    }
                }

                string[] selectors =
                {
                    "#fmimg img", ".cover img", ".bookcover img", ".book-cover img",
                    "#bookimg img", ".bookimg img", ".pic img", "#sideimg img", "img[src*=bookcover]"
                };
                foreach (var selector in selectors)
                {
                    foreach (var img in doc.DocumentNode.QuerySelectorAll(selector))
                    {
                        var cover = CoverUrl(img.GetAttributeValue("data-original", ""), baseUri);
                        if (string.IsNullOrEmpty(cover)) cover = CoverUrl(img.GetAttributeValue("data-src", ""), baseUri);
                        if (string.IsNullOrEmpty(cover)) cover = CoverUrl(img.GetAttributeValue("src", ""), baseUri);
                        if (!string.IsNullOrEmpty(cover)) return cover;
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("NovelParse", "封面提取失败: " + ex.Message);
            }
            return "";
        }

        private static string CoverUrl(string raw, Uri baseUri)
        {
            var s = CleanText(raw ?? "");
            if (s.Length < 6 || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "";
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u) && !Uri.TryCreate(baseUri, s, out u)) return "";
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return "";
            var path = u.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".svg") || path.EndsWith(".gif") || path.EndsWith(".ico")) return "";
            if (CoverPlaceholderRegex.IsMatch(u.AbsoluteUri)) return "";
            return u.AbsoluteUri;
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

        /// <summary>
        /// HtmlAgilityPack 会按 meta charset 再取一次 Encoding；Android Mono 缺 GBK 代码页会抛异常。
        /// 这里文本已经由 EncodingEx 解码过，统一把 meta charset 改成 utf-8 再交给 HAP。
        /// </summary>
        private static HtmlDocument LoadDoc(string html)
        {
            var safe = Regex.Replace(html ?? "", @"(charset\s*=\s*)([""']?)[a-z0-9_-]+\2",
                "${1}\"utf-8\"", RegexOptions.IgnoreCase);
            var doc = new HtmlDocument();
            doc.LoadHtml(safe);
            return doc;
        }

        /* ---------------- 正文 ---------------- */

        /// <summary>下载器用：只取正文文本。</summary>
        public static async Task<string> ChapterTextAsync(BookSource src, Chapter ch)
        {
            var preview = await ChapterPreviewAsync(src, ch).ConfigureAwait(false);
            return preview == null ? "" : preview.Text;
        }

        /// <summary>
        /// 预览用：抓取正文并报告同章分页数量、是否触及页数上限及可疑内容。
        /// 下载器与预览共用同一套抓取逻辑，避免“预览正常、下载不全”。
        /// </summary>
        public static async Task<ChapterPreview> ChapterPreviewAsync(BookSource src, Chapter ch)
        {
            var preview = new ChapterPreview();
            if (ch == null || string.IsNullOrEmpty(ch.Url))
            {
                preview.Warnings.Add(L10n.T("章节链接为空"));
                return preview;
            }

            // biquge.mom 的正文是接口返回的整章纯文本，没有分页。
            int momBook, momChapter;
            if (TryBiqugeMomChapter(ch.Url, out momBook, out momChapter))
            {
                try
                {
                    var raw = await ApibiGetAsync("chapter",
                        "{\"id\":" + momBook + ",\"chapterid\":" + momChapter + "}").ConfigureAwait(false);
                    var json = JObject.Parse(raw);
                    var momText = json["txt"] == null ? "" : json["txt"].ToString();
                    if (!string.IsNullOrWhiteSpace(momText))
                    {
                        preview.Text = AnalyzeRule.CleanBlock(momText);
                        preview.PageCount = 1;
                        AddPreviewWarnings(preview);
                        return preview;
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("NovelParse", "mom 章节接口失败: " + ex.Message);
                }
            }

            var engine = ReaderLibrary.Engine;
            if (src != null && src.ContentRule != null &&
                !string.IsNullOrWhiteSpace(src.ContentRule.Content))
            {
                try
                {
                    var txt = await engine.FetchContentAsync(src, ch.Url).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        preview.Text = txt;
                        preview.PageCount = 1;
                        AddPreviewWarnings(preview);
                        return preview;
                    }
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

                var doc = LoadDoc(html);
                var next = FindChapterContinuationUrl(doc, current);
                // piquge 把后半章正文放在 JS 解密后的 #morecontent；静态文本里只是占位提示。
                var moreHtml = current != null && current.IndexOf("piquge.com", StringComparison.OrdinalIgnoreCase) >= 0
                    ? DecryptPiqugeMore(html)
                    : "";
                var more = doc.DocumentNode.SelectNodes("//*[@id='morecontent']");
                if (more != null) foreach (var n in more)
                {
                    if (!string.IsNullOrEmpty(moreHtml)) n.InnerHtml = moreHtml;
                    else n.Remove();
                }
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
                preview.PageCount++;

                if (string.IsNullOrEmpty(next) || visited.Contains(next)) break;
                if (page == MaxChapterPages - 1)
                {
                    preview.ReachedPageLimit = true;
                    break;
                }
                current = next;
            }

            if (htmlParts.Count == 0)
            {
                AddPreviewWarnings(preview);
                return preview;
            }
            var text = BookParser.HtmlToText(string.Join("\n", htmlParts.ToArray()));
            // 去掉常见的“上一章/下一章/目录”导航尾巴
            text = Regex.Replace(text, @"(?m)^\s*(上一章|下一章|上一页|下一页|返回目录|加入书签|目录|本章未完.*|更多内容加载中.*|本站只支持手机浏览器访问.*)\s*[：:]?.*$", "");
            preview.Text = AnalyzeRule.CleanBlock(text);
            AddPreviewWarnings(preview);
            return preview;
        }

        private static void AddPreviewWarnings(ChapterPreview preview)
        {
            if (preview == null) return;
            var text = preview.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                preview.Warnings.Add(L10n.T("未抓到正文"));
                return;
            }
            if (text.Length < 200)
                preview.Warnings.Add(L10n.T("正文过短，可能只抓到导航或提示"));
            var junkHits = 0;
            var junkWords = new[]
            {
                "加入书架", "字体", "护眼", "回目录", "最新章节", "免费阅读",
                "开始阅读", "本章未完", "更多内容加载中", "全本txt下载", "请收藏"
            };
            foreach (var word in junkWords)
                junkHits += Regex.Matches(text, Regex.Escape(word), RegexOptions.IgnoreCase).Count;
            if (junkHits > 3)
                preview.Warnings.Add(L10n.T("含较多导航/按钮文案，可能不是正文"));
            if (Regex.IsMatch(text, @"(502 Bad Gateway|Bad Gateway|error code: 502)", RegexOptions.IgnoreCase))
                preview.Warnings.Add(L10n.T("抓到服务器错误页"));
            if (preview.ReachedPageLimit)
                preview.Warnings.Add(L10n.T("达到单章页数上限，可能仍有续页"));
        }

        /// <summary>
        /// 只识别同一章的分页续页：0.html → 0_2.html，或同路径 ?page=1 → ?page=2。
        /// 不靠“下一页/下一章”文案判断，避免把下一章当成续页。
        /// </summary>
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
                var dir = current.AbsolutePath.Substring(0, current.AbsolutePath.Length - currentName.Length);
                int curQueryPage;
                string curRestQuery;
                ReadPageQuery(current, out curQueryPage, out curRestQuery);

                var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
                if (anchors == null) return "";
                foreach (var a in anchors)
                {
                    var href = (a.GetAttributeValue("href", "") ?? "").Trim();
                    if (IsPlaceholderHref(href))
                    {
                        // piquge 的「下一页」用 onclick="location.href='/802/.../1_2.html'"。
                        var jsUrl = ExtractLocationFromJs(a.GetAttributeValue("onclick", ""));
                        if (!string.IsNullOrEmpty(jsUrl)) href = jsUrl;
                        else
                        {
                            var dataUrl = (a.GetAttributeValue("data-href", "") ?? "").Trim();
                            if (string.IsNullOrEmpty(dataUrl))
                                dataUrl = (a.GetAttributeValue("data-url", "") ?? "").Trim();
                            if (!string.IsNullOrEmpty(dataUrl)) href = dataUrl;
                        }
                    }
                    if (IsPlaceholderHref(href)) continue;

                    Uri next;
                    if (!Uri.TryCreate(current, href, out next)) continue;
                    if (!string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase)) continue;

                    // 查询参数分页：同一路径，只有 page/p/read/cpage/section 递增
                    if (string.Equals(next.AbsolutePath, current.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                    {
                        int nq;
                        string rest;
                        if (ReadPageQuery(next, out nq, out rest) && nq > curQueryPage &&
                            string.Equals(rest, curRestQuery, StringComparison.OrdinalIgnoreCase))
                            return next.AbsoluteUri;
                        continue;
                    }

                    // 路径分页：同目录同章 stem_N.html
                    var nextName = Uri.UnescapeDataString(IoPath.GetFileName(next.AbsolutePath));
                    var nextBase = Regex.Replace(nextName, @"\.(html?|htm)$", "", RegexOptions.IgnoreCase);
                    var m = Regex.Match(nextBase, @"^" + Regex.Escape(stem) + @"_(?<n>\d+)$");
                    if (!m.Success) continue;
                    int n;
                    if (!int.TryParse(m.Groups["n"].Value, out n) || n <= pageNo) continue;
                    var nextDir = next.AbsolutePath.Substring(0, next.AbsolutePath.Length - nextName.Length);
                    if (!string.Equals(nextDir, dir, StringComparison.OrdinalIgnoreCase)) continue;
                    return next.AbsoluteUri;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// piquge.com 把后半章正文放进 var c="..."，页面脚本解密后替换 #morecontent。
        /// 这里复刻它的 base64 规则，避免只下载到静态首屏。
        /// </summary>
        private static string DecryptPiqugeMore(string html)
        {
            try
            {
                var m = Regex.Match(html ?? "", @"var\s+c\s*=\s*""([A-Za-z0-9+/=_-]{200,})""",
                    RegexOptions.IgnoreCase);
                if (!m.Success) return "";
                var raw = Convert.FromBase64String(StandardBase64(m.Groups[1].Value));
                if (raw.Length < 14) return "";
                int n;
                if (!int.TryParse(Encoding.ASCII.GetString(raw, 8, 3), out n) || n < 100 || n > 999) return "";
                var start = 11 + n;
                var end = raw.Length - n;
                if (end <= start) return "";
                var inner = Encoding.ASCII.GetString(raw, start, end - start);
                // 站点脚本用这两个片段替代 URL-safe base64 的字符，拼出真正的标准 base64。
                inner = inner.Replace("-", "PHA+").Replace("_", "8L3A+");
                return Encoding.UTF8.GetString(Convert.FromBase64String(StandardBase64(inner)));
            }
            catch { return ""; }
        }

        private static string StandardBase64(string s)
        {
            var t = (s ?? "").Replace('-', '+').Replace('_', '/');
            var pad = (4 - t.Length % 4) % 4;
            return pad == 0 ? t : t + new string('=', pad);
        }

        private static bool IsPlaceholderHref(string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return true;
            var s = href.Trim();
            return s.StartsWith("#") ||
                   s.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                   s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractLocationFromJs(string js)
        {
            if (string.IsNullOrEmpty(js)) return "";
            var m = Regex.Match(js,
                @"(?:window\.)?location(?:\.href)?\s*=\s*(['\""'])([^'\""']+)\1",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[2].Value.Trim() : "";
        }

        private static bool ReadPageQuery(Uri u, out int page, out string rest)
        {
            page = 1;
            rest = "";
            if (u == null || string.IsNullOrEmpty(u.Query)) return false;
            var parts = u.Query.TrimStart('?').Split('&');
            var others = new List<string>();
            bool found = false;
            foreach (var part in parts)
            {
                var m = Regex.Match(part, @"^(?:page|p|read|cpage|section)=(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success && !found)
                {
                    found = int.TryParse(m.Groups[1].Value, out page) && page > 0;
                    if (!found) page = 1;
                }
                else others.Add(part);
            }
            others.Sort(StringComparer.OrdinalIgnoreCase);
            rest = string.Join("&", others.ToArray());
            return found;
        }

        private static void Report(Action<string> progress, string msg)
        {
            try { progress?.Invoke(msg); } catch { }
        }
    }
}

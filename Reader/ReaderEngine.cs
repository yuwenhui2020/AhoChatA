// ============================================================
// 阅读模块 · 引擎编排（联网抓取 + 搜索/发现/详情/目录/正文）
//
// 全部用 C# 在设备端直接发请求（不依赖 legado 远端服务，也不走 WebView）。
// 规则由 AnalyzeRule 求值；header / loginUrl / concurrentRate 取自书源 JSON。
//
// 覆盖的 legado 能力：
//   · searchUrl：searchKey、{{page}}、{{key}}、`url,{"method":"POST","body":"…"}` 选项
//   · checkKeyWord 搜索词校验、bookUrlPattern 结果过滤
//   · ruleExplore / exploreUrl（发现·分类）
//   · ruleToc：chapterList / isVolume / isVip / isPay / formatJs / nextTocUrl 分页
//   · ruleContent：content / webJs / sourceRegex / replaceRegex / nextContentUrl 分页
//   · ruleBookInfo：init / canReName / coverUrl / tocUrl / downloadUrls
// ============================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jint.Runtime;
using Jint;
using Jint.Native;
using Newtonsoft.Json.Linq;

namespace AhoChatA.Reader
{
    public class ReaderEngine
    {
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            UseCookies = true,
            CookieContainer = ReaderCookies.Jar,      // 与 java.ajax / java.getCookie 共用同一罐
            UseProxy = false                          // 书源直连，避免系统 HTTP_PROXY 干扰（legado 同）
        });

        static ReaderEngine() { _http.Timeout = TimeSpan.FromSeconds(30); }

        private const int MaxTocPages = 30;
        private const int MaxContentPages = 10;

        // ---------------- 基础抓取 ----------------
        public async Task<string> FetchAsync(string url, Dictionary<string, string> headers = null, string postBody = null, string contentType = null, string charset = null)
        {
            var text = await FetchOnce(url, headers, postBody, contentType, charset, false).ConfigureAwait(false);
            // 笔趣阁等站点前面挂着 Cloudflare，偶发 502（正文只有 "error code: 502"）。
            // 只有异常短响应才重试，正常页面不增加请求。
            for (int i = 0; i < 2 && (string.IsNullOrWhiteSpace(text) || text.Length < 100 || IsBotStub(text)); i++)
            {
                await Task.Delay(700).ConfigureAwait(false);
                var again = await FetchOnce(url, headers, postBody, contentType, charset, false).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(again) && again.Length > (text == null ? 0 : text.Length)) text = again;
            }
            // 反爬桩（<html>…window.location.href="/"）：补 Referer 再取一次
            if (IsBotStub(text))
            {
                var again = await FetchOnce(url, headers, postBody, contentType, charset, true).ConfigureAwait(false);
                if (again != null && again.Length > text.Length) text = again;
            }
            return text;
        }

        private static bool IsBotStub(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 800) return false;
            return text.IndexOf("window.location", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<string> FetchOnce(string url, Dictionary<string, string> headers, string postBody,
            string contentType, string charset, bool withReferer)
        {
            try
            {
                using (var req = new HttpRequestMessage(
                    postBody == null ? HttpMethod.Get : HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110 Mobile Safari/537.36");
                    if (headers != null) foreach (var h in headers) SafeAddHeader(req, h.Key, h.Value);
                    if (withReferer)
                    {
                        try { var u = new Uri(url); req.Headers.TryAddWithoutValidation("Referer", u.Scheme + "://" + u.Host + "/"); }
                        catch { }
                    }
                    if (postBody != null)
                    {
                        req.Content = new StringContent(postBody, Encoding.UTF8,
                            contentType ?? "application/x-www-form-urlencoded");
                    }
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        var cs = charset;
                        if (string.IsNullOrEmpty(cs)) cs = resp.Content.Headers.ContentType?.CharSet;
                        var text = EncodingEx.Detect(bytes, cs);
                        Android.Util.Log.Info("Mchat",
                            "阅读抓取 " + url + " -> HTTP " + (int)resp.StatusCode + ", " + bytes.Length + "B, text=" + (text == null ? 0 : text.Length));
                        return text;
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "阅读抓取失败 " + url + " : " + ex.ToString());
                return "";
            }
        }

        private static void SafeAddHeader(HttpRequestMessage req, string k, string v)
        {
            try
            {
                if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) return;
                if (k.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) return;
                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) return;
                req.Headers.TryAddWithoutValidation(k, v);
            }
            catch { }
        }

        // ---------------- URL 规则解析（legado AnalyzeUrl 子集） ----------------
        public sealed class UrlSpec
        {
            public string Url = "";
            public string Body;
            public bool IsPost;
            public Dictionary<string, string> Headers;
            public string Charset;      // 响应编码（gbk / utf-8 …）
        }

        /// <summary>
        /// 展开书源里的 URL 规则，顺序对齐 legado AnalyzeUrl：
        /// ① 整条 &lt;js&gt;/@js: 求值 → ② 尾部 ,{options} → ③ 旧式 ## POST 体 → ④ {{}}/searchKey 替换
        /// </summary>
        public static UrlSpec ExpandUrlRule(string rule, BookSource src, string key, int page)
        {
            var spec = new UrlSpec();
            if (string.IsNullOrWhiteSpace(rule)) return spec;
            var s = rule.Trim();

            // ① 整条就是 JS（现代书源常见：把 url / method / headers 全交给脚本算）
            s = EvalUrlJs(s, src, key, page);

            // ② 尾部 ,{"method":"POST","body":"…","charset":"gbk"} 选项
            int optIdx = FindOptionsIndex(s);
            if (optIdx > 0)
            {
                var opt = s.Substring(optIdx + 1).Trim();
                s = s.Substring(0, optIdx).Trim();
                var o = ParseOptions(opt);
                if (o != null)
                {
                    var method = (string)o["method"] ?? "GET";
                    spec.IsPost = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
                    spec.Body = (string)o["body"];
                    spec.Charset = (string)o["charset"];
                    if (o["headers"] != null)
                        spec.Headers = o["headers"].ToObject<Dictionary<string, string>>();
                }
            }

            // ③ 旧式 ## 分隔 POST 体
            int h = s.IndexOf("##", StringComparison.Ordinal);
            if (h > 0)
            {
                var after = s.Substring(h + 2);
                s = s.Substring(0, h);
                spec.IsPost = true;
                if (after.StartsWith("@json:", StringComparison.OrdinalIgnoreCase))
                {
                    spec.Body = after.Substring(6);
                    if (spec.Headers == null) spec.Headers = new Dictionary<string, string>();
                }
                else if (after.StartsWith("@form:", StringComparison.OrdinalIgnoreCase)) spec.Body = after.Substring(6);
                else spec.Body = after;
            }

            // ④ 关键字 / 页码 / 内嵌 JS
            var encKey = Uri.EscapeDataString(key ?? "");
            s = s.Replace("searchKey", encKey)
                 .Replace("{{page}}", page.ToString())
                 .Replace("{{key}}", encKey)
                 .Replace("{{searchKey}}", encKey);
            s = ExpandJsTemplate(s, src, key, page);
            if (!string.IsNullOrEmpty(spec.Body))
            {
                spec.Body = spec.Body.Replace("searchKey", encKey)
                                     .Replace("{{page}}", page.ToString())
                                     .Replace("{{key}}", encKey);
                spec.Body = ExpandJsTemplate(spec.Body, src, key, page);
            }
            spec.Url = s.Trim();
            return spec;
        }

        /// <summary>整条 URL 规则是 &lt;js&gt;…&lt;/js&gt; 或 @js: 时求值成字符串。</summary>
        private static string EvalUrlJs(string s, BookSource src, string key, int page)
        {
            string code = null;
            var t = s.Trim();
            if (t.StartsWith("@js:", StringComparison.OrdinalIgnoreCase))
                code = t.Substring(4);
            else if (t.StartsWith("<js>", StringComparison.OrdinalIgnoreCase) &&
                     t.EndsWith("</js>", StringComparison.OrdinalIgnoreCase) && t.Length > 9)
                code = t.Substring(4, t.Length - 9);
            if (code == null) return s;

            try
            {
                var js = code;
                if (!string.IsNullOrWhiteSpace(src?.JsLib)) js = src.JsLib + "\n" + js;

                var ar = new AnalyzeRule(src, src?.BookSourceUrl);
                var e = new Jint.Engine();
                e.SetValue("key", key ?? "");
                e.SetValue("page", page);
                e.SetValue("baseUrl", src?.BookSourceUrl ?? "");
                e.SetValue("result", "");
                e.SetValue("src", "");
                e.SetValue("source", src);
                e.SetValue("java", ar.MakeJava());
                e.SetValue("cookie", ar.MakeCookie());
                var v = e.Evaluate(js);
                var r = v == null || v.IsNull() || v.IsUndefined() ? "" : v.ToString();
                return string.IsNullOrWhiteSpace(r) ? "" : r.Trim();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "阅读 URL 规则 JS 失败: " + ex.Message);
                return "";
            }
        }

        private static int FindOptionsIndex(string s)
        {
            int i = s.LastIndexOf(',');
            while (i > 0)
            {
                var tail = s.Substring(i + 1).Trim();
                if (tail.StartsWith("{", StringComparison.Ordinal) && tail.EndsWith("}", StringComparison.Ordinal)
                    && ParseOptions(tail) != null)
                    return i;
                i = s.LastIndexOf(',', i - 1);
            }
            return -1;
        }

        /// <summary>
        /// 解析 URL 尾部的选项。书源里既有标准 JSON，也有 JS 对象字面量
        /// （`{charset:'gbk',headers:{...}}`），后者要先归一化再解析。
        /// </summary>
        public static JObject ParseOptions(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            if (!t.StartsWith("{", StringComparison.Ordinal)) return null;
            try { return JObject.Parse(t); } catch { }
            try { return JObject.Parse(NormalizeJsObject(t)); } catch { }
            return null;
        }

        /// <summary>把 JS 对象字面量改写成 JSON：单引号字符串 + 不带引号的键名。</summary>
        private static string NormalizeJsObject(string s)
        {
            var sb = new StringBuilder(s.Length + 16);
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < s.Length) { sb.Append(c).Append(s[i + 1]); i++; continue; }
                    if (c == quote) { sb.Append('"'); quote = '\0'; continue; }
                    if (c == '"') { sb.Append("\\\""); continue; }
                    sb.Append(c);
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; sb.Append('"'); continue; }
                sb.Append(c);
            }
            var r = sb.ToString();
            // 不带引号的键名：{ key:  →  { "key":
            r = System.Text.RegularExpressions.Regex.Replace(r, @"([{,]\s*)([A-Za-z_$][\w$]*)(\s*:)",
                "$1\"$2\"$3");
            return r;
        }

        /// <summary>URL 模板里的 {{表达式}}：key / page 为内置绑定，java.* 也可用。</summary>
        public static string ExpandJsTemplate(string s, BookSource src, string key, int page)
        {
            if (s.IndexOf("{{", StringComparison.Ordinal) < 0) return s;
            AnalyzeRule ar = null;
            return System.Text.RegularExpressions.Regex.Replace(s, @"\{\{([\w\W]*?)\}\}", m =>
            {
                var code = m.Groups[1].Value;
                if (code == "page") return page.ToString();
                if (code == "key") return key ?? "";
                try
                {
                    if (ar == null) ar = new AnalyzeRule(src, src?.BookSourceUrl);
                    var e = new Jint.Engine();
                    e.SetValue("key", key ?? "");
                    e.SetValue("page", page);
                    e.SetValue("baseUrl", src?.BookSourceUrl ?? "");
                    e.SetValue("source", src);
                    e.SetValue("java", ar.MakeJava());
                    e.SetValue("cookie", ar.MakeCookie());
                    var v = e.Evaluate(code);
                    return v == null || v.IsNull() || v.IsUndefined() ? "" : v.ToString();
                }
                catch { return ""; }
            });
        }

        private static string SafeUri(string s)
        {
            try { return new Uri(s).AbsoluteUri; } catch { return s; }
        }

        /// <summary>把相对地址按书源地址补全（legado 的 searchUrl / exploreUrl 允许写相对路径）。</summary>
        internal static string Resolve(BookSource src, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var b = src?.BookSourceUrl ?? "";
                if (u.StartsWith("//")) u = "https:" + u;
                else if (u.StartsWith("/"))
                {
                    try { var bu = new Uri(b); u = bu.Scheme + "://" + bu.Authority + u; } catch { }
                }
                else
                {
                    try { u = new Uri(new Uri(b.EndsWith("/") ? b : b + "/"), u).AbsoluteUri; } catch { }
                }
            }
            return SafeUri(u);
        }

        // ---------------- 搜索 ----------------
        public async Task<List<Book>> SearchAsync(BookSource src, string key)
        {
            var list = new List<Book>();
            if (src == null || string.IsNullOrEmpty(src.SearchUrl)) return list;

            var spec = ExpandUrlRule(src.SearchUrl, src, key, 1);
            if (string.IsNullOrWhiteSpace(spec.Url)) return list;

            var headers = src.ParseHeader();
            if (spec.Headers != null) foreach (var kv in spec.Headers) headers[kv.Key] = kv.Value;

            var absUrl = Resolve(src, spec.Url);
            var html = await FetchAsync(absUrl, headers,
                spec.IsPost ? (spec.Body ?? "") : null,
                spec.IsPost ? GuessContentType(spec.Body) : null,
                spec.Charset);
            // 反爬桩 / 404 小页面：URL 多由 JS 现算（含站点随机的搜索入口），重算一次再取
            if (IsBotStub(html) || html.Length < 1200)
            {
                var spec2 = ExpandUrlRule(src.SearchUrl, src, key, 1);
                var abs2 = Resolve(src, spec2.Url);
                if (!string.IsNullOrEmpty(abs2))
                {
                    var headers2 = src.ParseHeader();
                    if (spec2.Headers != null) foreach (var kv in spec2.Headers) headers2[kv.Key] = kv.Value;
                    var html2 = await FetchAsync(abs2, headers2,
                        spec2.IsPost ? (spec2.Body ?? "") : null,
                        spec2.IsPost ? GuessContentType(spec2.Body) : null,
                        spec2.Charset);
                    if (html2.Length > html.Length) { html = html2; absUrl = abs2; }
                }
            }
            if (string.IsNullOrEmpty(html)) return list;

            // checkKeyWord：返回页面里没有关键词 → 视为搜索失败（源已失效）
            if (!string.IsNullOrWhiteSpace(src.SearchRule?.CheckKeyWord)
                && html.IndexOf(key ?? "", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Android.Util.Log.Info("Mchat", "阅读搜索 " + src.BookSourceName + ": checkKeyWord 未命中，跳过");
                return list;
            }

            var ar = new AnalyzeRule(src, absUrl);
            bool isJson = html.TrimStart().StartsWith("{") || html.TrimStart().StartsWith("[");
            if (isJson) ar.SetJson(html); else ar.SetHtml(html);

            var sr = src.SearchRule ?? new SearchRule();
            var nodes = ar.GetList(sr.BookList);
            Android.Util.Log.Info("Mchat",
                L10n.T("阅读搜索 ") + src.BookSourceName + ": html=" + html.Length + " chars, nodes=" + nodes.Count + ", bookList='" + sr.BookList + "'");

            foreach (var node in nodes)
            {
                var sub = new AnalyzeRule(src, absUrl).SetNode(node);
                var name = sub.GetString(sr.Name);
                if (string.IsNullOrEmpty(name)) continue;
                name = FirstLine(name);

                var bookUrl = sub.AbsoluteUrl(sub.GetString(sr.BookUrl));
                if (string.IsNullOrEmpty(bookUrl)) continue;

                if (!string.IsNullOrWhiteSpace(src.BookUrlPattern))
                {
                    try { if (!System.Text.RegularExpressions.Regex.IsMatch(bookUrl, src.BookUrlPattern)) continue; }
                    catch { }
                }

                var b = new Book
                {
                    Name = name,
                    Author = FirstLine(sub.GetString(sr.Author)),
                    Intro = sub.GetString(sr.Intro),
                    CoverUrl = sub.AbsoluteUrl(sub.GetString(sr.CoverUrl)),
                    Kind = FirstLine(sub.GetString(sr.Kind)),
                    LastChapter = FirstLine(sub.GetString(sr.LastChapter)),
                    Type = "online",
                    SourceUrl = src.BookSourceUrl,
                    BookUrl = bookUrl
                };
                b.Id = b.BookUrl;
                list.Add(b);
            }
            return list;
        }

        private static string GuessContentType(string body)
        {
            var t = (body ?? "").TrimStart();
            return t.StartsWith("{") || t.StartsWith("[") ? "application/json" : "application/x-www-form-urlencoded";
        }

        private static List<string> SplitLines(string s)
        {
            var l = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return l;
            foreach (var x in s.Split('\n'))
            {
                var t = x.Trim();
                if (t.Length > 0) l.Add(t);
            }
            return l;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        // ---------------- 发现（explore） ----------------
        /// <summary>解析 exploreUrl：legado 里是一段 JSON 数组 [{"title":"分类","url":"…"}]。</summary>
        public List<ExploreCategory> ExploreCategories(BookSource src)
        {
            var list = new List<ExploreCategory>();
            var raw = src?.ExploreUrl;
            if (string.IsNullOrWhiteSpace(raw)) raw = src?.ExploreRule?.ExploreUrl;
            if (string.IsNullOrWhiteSpace(raw)) return list;

            string text = raw.Trim();
            if (text.StartsWith("<js>", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("@js:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("{{", StringComparison.Ordinal))
            {
                var ar = new AnalyzeRule(src, src.BookSourceUrl);
                text = ar.GetString(text);
            }
            if (string.IsNullOrWhiteSpace(text)) return list;
            text = text.Trim();

            // 新版：JSON 数组 [{"title":"分类","url":"…"}]
            if (text[0] == '[')
            {
                try
                {
                    var arr = JArray.Parse(text);
                    foreach (var it in arr)
                    {
                        var title = (string)it["title"] ?? "";
                        var url = (string)it["url"] ?? "";
                        if (title.Length == 0) continue;
                        list.Add(new ExploreCategory { Title = title, Url = url });
                    }
                    return list;
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "exploreUrl 解析失败: " + ex.Message);
                    return list;
                }
            }

            // 旧版：每行 `分类名::地址`（允许前置空格、分组标题行没有 ::）
            foreach (var line0 in text.Split('\n'))
            {
                var line = line0.Trim().Trim('\r').Trim();
                if (line.Length == 0) continue;
                int sep = line.IndexOf("::", StringComparison.Ordinal);
                if (sep <= 0) continue;                       // 分组标题 / 说明行，跳过
                var title = line.Substring(0, sep).Trim();
                var url = line.Substring(sep + 2).Trim();
                if (title.Length == 0 || url.Length == 0) continue;
                list.Add(new ExploreCategory { Title = title, Url = url });
            }
            if (list.Count == 0)
                Android.Util.Log.Warn("Mchat", "exploreUrl 无法识别: " + text.Substring(0, Math.Min(80, text.Length)));
            return list;
        }

        /// <summary>发现页某个分类下的书籍列表。</summary>
        public async Task<List<Book>> ExploreAsync(BookSource src, ExploreCategory cat, int page = 1)
        {
            var list = new List<Book>();
            if (src == null || cat == null || string.IsNullOrEmpty(cat.Url)) return list;

            var spec = ExpandUrlRule(cat.Url, src, "", page);
            if (string.IsNullOrWhiteSpace(spec.Url)) return list;
            var url = Resolve(src, spec.Url);

            var headers = src.ParseHeader();
            if (spec.Headers != null) foreach (var kv in spec.Headers) headers[kv.Key] = kv.Value;
            var html = await FetchAsync(url, headers,
                spec.IsPost ? (spec.Body ?? "") : null,
                spec.IsPost ? GuessContentType(spec.Body) : null,
                spec.Charset);
            if (string.IsNullOrEmpty(html)) return list;

            var ar = new AnalyzeRule(src, url);
            bool isJson = html.TrimStart().StartsWith("{") || html.TrimStart().StartsWith("[");
            if (isJson) ar.SetJson(html); else ar.SetHtml(html);

            var er = src.ExploreRule ?? new ExploreRule();
            var nodes = ar.GetList(er.BookList);
            foreach (var node in nodes)
            {
                var sub = new AnalyzeRule(src, url).SetNode(node);
                var name = FirstLine(sub.GetString(er.Name));
                if (string.IsNullOrEmpty(name)) continue;
                var bookUrl = sub.AbsoluteUrl(sub.GetString(er.BookUrl));
                if (string.IsNullOrEmpty(bookUrl)) continue;
                list.Add(new Book
                {
                    Name = name,
                    Author = FirstLine(sub.GetString(er.Author)),
                    Intro = sub.GetString(er.Intro),
                    CoverUrl = sub.AbsoluteUrl(sub.GetString(er.CoverUrl)),
                    Kind = FirstLine(sub.GetString(er.Kind)),
                    LastChapter = FirstLine(sub.GetString(er.LastChapter)),
                    Type = "online",
                    SourceUrl = src.BookSourceUrl,
                    BookUrl = bookUrl,
                    Id = bookUrl
                });
            }
            return list;
        }

        // ---------------- 书籍详情 ----------------
        public async Task<Book> GetBookInfoAsync(BookSource src, Book book)
        {
            if (string.IsNullOrEmpty(book.BookUrl)) return book;
            var html = await FetchAsync(book.BookUrl, src.ParseHeader());
            if (string.IsNullOrEmpty(html)) return book;
            var ir = src.BookInfoRule ?? new BookInfoRule();
            var ar = new AnalyzeRule(src, book.BookUrl);
            bool isJson = html.TrimStart().StartsWith("{") || html.TrimStart().StartsWith("[");
            if (isJson) ar.SetJson(html); else ar.SetHtml(html);
            if (!string.IsNullOrEmpty(ir.Init)) ar.GetString(ir.Init);

            if (string.IsNullOrEmpty(book.Name)) book.Name = FirstLine(ar.GetString(ir.Name));
            book.Author = FirstLine(ar.GetString(ir.Author));
            book.Intro = ar.GetString(ir.Intro);
            book.CoverUrl = ar.AbsoluteUrl(ar.GetString(ir.CoverUrl));
            book.Kind = FirstLine(ar.GetString(ir.Kind));
            book.LastChapter = FirstLine(ar.GetString(ir.LastChapter));
            book.TocUrl = ar.AbsoluteUrl(ar.GetString(ir.TocUrl));
            if (string.IsNullOrEmpty(book.TocUrl)) book.TocUrl = book.BookUrl;
            if (string.IsNullOrWhiteSpace(book.Name)) book.Name = L10n.T("未命名书籍");
            return book;
        }

        // ---------------- 目录 ----------------
        public async Task<List<Chapter>> GetTocAsync(BookSource src, Book book)
        {
            var outp = new List<Chapter>();
            if (string.IsNullOrEmpty(book.TocUrl)) return outp;

            var tr = src.TocRule ?? new TocRule();
            string listRule = tr.ChapterList;
            if (string.IsNullOrWhiteSpace(listRule)) listRule = "@css:a";

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(book.TocUrl);

            while (queue.Count > 0 && visited.Count < MaxTocPages)
            {
                var url = queue.Dequeue();
                if (string.IsNullOrEmpty(url) || visited.Contains(url)) continue;
                visited.Add(url);

                var html = await FetchAsync(url, src.ParseHeader());
                if (string.IsNullOrEmpty(html)) continue;

                var ar = new AnalyzeRule(src, url);
                bool isJson = html.TrimStart().StartsWith("{") || html.TrimStart().StartsWith("[");
                if (isJson) ar.SetJson(html); else ar.SetHtml(html);

                if (!string.IsNullOrEmpty(tr.PreUpdateJs)) ar.GetString(tr.PreUpdateJs);

                var nodes = ar.GetList(listRule);
                Android.Util.Log.Info("Mchat", "阅读目录 " + url + ": nodes=" + nodes.Count);

                foreach (var node in nodes)
                {
                    var sub = new AnalyzeRule(src, url).SetNode(node);
                    var title = FirstLine(sub.GetString(tr.ChapterName));
                    var curl = sub.AbsoluteUrl(sub.GetString(tr.ChapterUrl));
                    if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(curl)) continue;

                    if (IsTrue(sub.GetString(tr.IsVolume))) continue;   // 分卷标题不参与阅读

                    if (string.IsNullOrEmpty(curl)) curl = url;
                    outp.Add(new Chapter
                    {
                        Index = outp.Count,
                        Title = string.IsNullOrEmpty(title) ? (L10n.T("第 ") + (outp.Count + 1) + L10n.T(" 章")) : title,
                        Url = curl,
                        IsVip = IsTrue(sub.GetString(tr.IsVip)),
                        IsPay = IsTrue(sub.GetString(tr.IsPay)),
                        Tag = FirstLine(sub.GetString(tr.UpdateTime))
                    });
                }

                if (!string.IsNullOrWhiteSpace(tr.NextTocUrl))
                {
                    var nexts = SplitLines(ar.GetString(tr.NextTocUrl));
                    foreach (var nu in nexts)
                    {
                        var abs = ar.AbsoluteUrl(nu);
                        if (string.IsNullOrEmpty(abs) || abs == url) continue;
                        if (visited.Contains(abs) || queue.Contains(abs)) continue;
                        queue.Enqueue(abs);
                    }
                }
            }

            // formatJs：逐章跑一遍，返回值作为新标题
            if (!string.IsNullOrWhiteSpace(tr.FormatJs))
            {
                for (int i = 0; i < outp.Count; i++)
                {
                    try
                    {
                        var e = new Jint.Engine();
                        e.SetValue("index", i + 1);
                        e.SetValue("title", outp[i].Title);
                        e.SetValue("chapter", outp[i]);
                        var v = e.Evaluate(tr.FormatJs);
                        var t = v == null ? null : v.ToString();
                        if (!string.IsNullOrWhiteSpace(t)) outp[i].Title = t.Trim();
                    }
                    catch { }
                }
            }

            for (int i = 0; i < outp.Count; i++) outp[i].Index = i;
            return outp;
        }

        private static bool IsTrue(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim().ToLowerInvariant();
            return t == "true" || t == "1" || t == "是" || t == "yes" || t == "vip";
        }

        // ---------------- 正文 ----------------
        public async Task<string> GetContentAsync(BookSource src, string chapterUrl, ReaderStore store, string bookId, int idx)
        {
            if (!string.IsNullOrEmpty(bookId))
            {
                var cached = store?.LoadChapter(bookId, idx);
                if (cached != null) return cached;
            }
            var txt = await FetchContentAsync(src, chapterUrl);
            if (string.IsNullOrEmpty(txt)) return "";
            if (!string.IsNullOrEmpty(bookId)) store?.SaveChapter(bookId, idx, txt);
            return txt;
        }

        /// <summary>抓一章正文（含多页拼接、净化、替换）。</summary>
        public async Task<string> FetchContentAsync(BookSource src, string chapterUrl)
        {
            var cr = src.ContentRule ?? new ContentRule();
            var sb = new StringBuilder();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(chapterUrl);

            while (queue.Count > 0 && visited.Count < MaxContentPages)
            {
                var url = queue.Dequeue();
                if (string.IsNullOrEmpty(url) || visited.Contains(url)) continue;
                visited.Add(url);

                var html = await FetchAsync(url, src.ParseHeader());
                if (string.IsNullOrEmpty(html)) continue;

                var ar = new AnalyzeRule(src, url);
                bool isJson = html.TrimStart().StartsWith("{") || html.TrimStart().StartsWith("[");
                if (isJson) ar.SetJson(html); else ar.SetHtml(html);

                if (!string.IsNullOrEmpty(cr.WebJs)) ar.GetString(cr.WebJs);

                var txt = ar.GetString(string.IsNullOrWhiteSpace(cr.Content) ? "text" : cr.Content);

                if (!string.IsNullOrEmpty(txt) && txt.IndexOf('<') >= 0 && txt.IndexOf('>') > 0)
                    txt = BookParser.HtmlToText(txt);

                if (!string.IsNullOrEmpty(txt)) sb.Append(txt).Append('\n');

                if (!string.IsNullOrWhiteSpace(cr.NextContentUrl))
                {
                    var nexts = SplitLines(ar.GetString(cr.NextContentUrl));
                    foreach (var nu in nexts)
                    {
                        var abs = ar.AbsoluteUrl(nu);
                        if (string.IsNullOrEmpty(abs) || abs == url || visited.Contains(abs)) continue;
                        if (queue.Contains(abs)) continue;
                        queue.Enqueue(abs);
                    }
                }
            }

            var body = AnalyzeRule.CleanBlock(sb.ToString());

            if (!string.IsNullOrWhiteSpace(cr.SourceRegex))
            {
                try { body = System.Text.RegularExpressions.Regex.Replace(body, cr.SourceRegex, ""); }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(cr.ReplaceRegex))
                body = AnalyzeRule.ApplyReplaceRules(body, cr.ReplaceRegex);
            return AnalyzeRule.CleanBlock(body);
        }

        // ---------------- 书源自检 ----------------
        public async Task<(int count, string msg)> CheckSourceAsync(BookSource src, string key)
        {
            try
            {
                var list = await SearchAsync(src, string.IsNullOrWhiteSpace(key) ? L10n.T("斗破") : key);
                if (list.Count == 0) return (0, "搜索无结果，规则或站点可能已失效");
                return (list.Count, L10n.T("正常，返回 ") + list.Count + L10n.T(" 条"));
            }
            catch (Exception ex)
            {
                return (0, L10n.T("异常：") + ex.Message);
            }
        }
    }
}

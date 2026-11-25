// ============================================================
// 阅读模块 · 书籍解析（TXT 章节切分 + EPUB 解包）
//
// legado 的 TXT 目录规则（TxtTocRule）很灵活；这里实现一组常见中文
// 章节标记 + 英文 Chapter 的自动识别，覆盖绝大多数 TXT 小说。
// EPUB 用 System.IO.Compression 直接解包（DEFLATE/STORE 原生支持）。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AhoChatA.Reader
{
    public static partial class BookParser
    {
        // 中文章节标记：第X章/回/卷/节…，以及「卷 X」「Chapter N」等。
        // 注意：CJK 属于 \w，`第1章开始` 这类无空格写法会因 \b 失效，
        // 因此这里只匹配「开头前缀」，再用行长度上限过滤正文行。
        private static readonly Regex[] TxtPatterns =
        {
            new Regex(@"^\s*第\s*[0-9零一二三四五六七八九十百千两]+\s*[章回卷节部篇集幕]"),
            new Regex(@"^\s*卷\s*[0-9零一二三四五六七八九十百千两]+"),
            new Regex(@"^\s*Chapter\s+[0-9]+", RegexOptions.IgnoreCase),
            new Regex(@"^\s*[0-9]+\s*[\.、]\s*\S+"),
            new Regex(@"^\s*[（(][0-9零一二三四五六七八九十百千两]+[)）]\s*\S+")
        };
        private const int TocLineMaxLen = 40;   // 章节标题行长度上限

        public static List<Chapter> ParseTxt(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var chapters = new List<Chapter>();
            var cur = new Chapter { Index = 0, Title = L10n.T("正文") };
            var body = new StringBuilder();
            bool hasSplit = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (IsChapterLine(line))
                {
                    if (!hasSplit)
                    {
                        // 首个章节标记之前的内容通常是书名/作者/简介等元信息：
                        // 短则丢弃，长则作为「前言」保留，避免混进第一章正文。
                        var pre = body.ToString().Trim();
                        if (pre.Length > 300)
                            chapters.Add(new Chapter { Index = 0, Title = L10n.T("前言"), Content = pre });
                        body.Clear();
                    }
                    else if (body.Length > 0)
                    {
                        cur.Content = body.ToString().Trim();
                        chapters.Add(cur);
                        body.Clear();
                    }
                    hasSplit = true;
                    cur = new Chapter { Index = chapters.Count, Title = line.Trim() };
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(line)) body.AppendLine(line);
                }
            }
            if (body.Length > 0 || !hasSplit)
            {
                cur.Content = body.ToString().Trim();
                chapters.Add(cur);
            }
            if (chapters.Count == 0)
            {
                chapters.Add(new Chapter { Index = 0, Title = L10n.T("正文"), Content = text.Trim() });
            }
            return chapters;
        }

        private static bool IsChapterLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (line.Trim().Length > TocLineMaxLen) return false;
            foreach (var p in TxtPatterns) if (p.IsMatch(line)) return true;
            return false;
        }

        // ---------------- EPUB ----------------
        public static Book ParseEpub(byte[] data, string defaultName)
        {
            var book = new Book { Type = "local", Name = defaultName };
            using (var ms = new MemoryStream(data))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                // 1) container.xml → OPF 路径
                var container = zip.GetEntry("META-INF/container.xml");
                if (container == null) return book;
                string opfPath = null;
                using (var sr = new StreamReader(container.Open()))
                {
                    var xml = sr.ReadToEnd();
                    var m = Regex.Match(xml, "full-path=\"([^\"]+)\"");
                    if (m.Success) opfPath = m.Groups[1].Value;
                }
                if (opfPath == null) return book;

                var opfEntry = zip.GetEntry(NormalizeZipPath(Path.GetDirectoryName(opfPath), Path.GetFileName(opfPath)));
                if (opfEntry == null) return book;
                string opfDir = Path.GetDirectoryName(opfPath).Replace('\\', '/');
                string opfText;
                using (var sr = new StreamReader(opfEntry.Open())) opfText = sr.ReadToEnd();

                // 2) OPF：标题/作者 + manifest(id->href) + spine
                var titleM = Regex.Match(opfText, "<dc:title[^>]*>([^<]+)</dc:title>", RegexOptions.IgnoreCase);
                if (titleM.Success) book.Name = DecodeEntities(titleM.Groups[1].Value.Trim());
                var creatorM = Regex.Match(opfText, "<dc:creator[^>]*>([^<]+)</dc:creator>", RegexOptions.IgnoreCase);
                if (creatorM.Success) book.Author = DecodeEntities(creatorM.Groups[1].Value.Trim());

                var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match mi in Regex.Matches(opfText, "<item\\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var tag = mi.Value;
                    var id = Attr(tag, "id");
                    var href = Attr(tag, "href");
                    if (id != null && href != null)
                        manifest[id] = NormalizeZipPath(opfDir, href);
                }

                var spine = new List<string>();
                var sm = Regex.Match(opfText, "<spine[^>]*>(.*?)</spine>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (sm.Success)
                {
                    foreach (Match sr in Regex.Matches(sm.Groups[1].Value, "<itemref\\b[^>]*>", RegexOptions.IgnoreCase))
                    {
                        var idref = Attr(sr.Value, "idref");
                        if (idref != null && manifest.ContainsKey(idref)) spine.Add(manifest[idref]);
                    }
                }

                // 3) 逐章读取 XHTML → 纯文本
                int idx = 0;
                foreach (var rel in spine)
                {
                    var entry = zip.GetEntry(rel);
                    if (entry == null) continue;
                    string html;
                    using (var sr = new StreamReader(entry.Open())) html = sr.ReadToEnd();
                    var ch = new Chapter
                    {
                        Index = idx++,
                        Title = ChapterTitle(html, idx),
                        Content = HtmlToText(html)
                    };
                    if (!string.IsNullOrWhiteSpace(ch.Content))
                        book.Chapters.Add(ch);
                }
                if (book.Chapters.Count == 0)
                {
                    book.Chapters.Add(new Chapter { Index = 0, Title = L10n.T("正文"), Content = HtmlToText(opfText) });
                }
            }
            return book;
        }

        private static string ChapterTitle(string html, int n)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var h = doc.DocumentNode.SelectSingleNode("//h1 | //h2 | //h3 | //h4 | //title");
            if (h != null && !string.IsNullOrWhiteSpace(h.InnerText))
                return DecodeEntities(h.InnerText.Trim());
            return L10n.T("第 ") + n + L10n.T(" 节");
        }

        public static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            // 块级结束标签 / 换行标签 → 换行，保留段落结构
            html = Regex.Replace(html, @"</(p|div|tr|li|h1|h2|h3|h4|h5|h6)>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            // 去掉所有剩余标签
            html = Regex.Replace(html, @"<[^>]+>", "");
            html = DecodeEntities(html);
            html = Regex.Replace(html, @"[ \t]+\n", "\n");
            html = Regex.Replace(html, @"\n{2,}", "\n");
            return html.Trim();
        }

        private static string DecodeEntities(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return System.Net.WebUtility.HtmlDecode(s);
        }

        private static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string NormalizeZipPath(string dir, string href)
        {
            href = href.Split('#')[0].Split('?')[0];
            if (string.IsNullOrEmpty(dir)) return href.TrimStart('/');
            // 处理 ../ 与 ./
            var parts = (dir.Replace('\\', '/').TrimEnd('/') + "/" + href).Split('/');
            var stack = new List<string>();
            foreach (var p in parts)
            {
                if (p == "" || p == ".") continue;
                if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
                else stack.Add(p);
            }
            return string.Join("/", stack);
        }
    }
}

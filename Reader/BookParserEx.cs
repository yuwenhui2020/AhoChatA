// ============================================================
// 阅读模块 · 多格式解析扩展（partial BookParser）
//
// 转译自 Areada 1.1.4：
//   data/DocumentResolver.kt              → DetectFormat（格式识别）
//   data/ZipBookContainer.kt              → TryExtractBook（压缩包内取书）
//   reader/fb2/Fb2Engine.kt               → ParseFb2（section 拍平）
//   reader/markdown/MarkdownEngine.kt     → ParseMarkdown（标题 + 行内标记）
//   reader/epub/EpubEngine.kt             → ParseEpubFull（NAV/NCX 目录 + 封面）
//   reader/pdf/PdfPageRenderer.kt         → PdfChapters（一页 = 一章）
//
// 全部是纯 .NET 代码（不引用 Android），因此离线自检工程也能编译。
// 原有的 ParseTxt / ParseEpub 保持不动，避免破坏既有行为与断言。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AhoChatA.Reader
{
    public static partial class BookParser
    {
        /* ============================================================
         * 格式识别（转译 Areada DocumentResolver.detectSupportedType）
         * ============================================================ */

        /// <summary>按扩展名判断格式；返回 txt/epub/pdf/fb2/md/zip，null 表示未知。</summary>
        private static string ExtFormat(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.ToLowerInvariant();
            int q = n.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) n = n.Substring(0, q);
            if (n.EndsWith(".pdf")) return "pdf";
            if (n.EndsWith(".epub")) return "epub";
            if (n.EndsWith(".fb2.zip") || n.EndsWith(".fb2.xml") || n.EndsWith(".fb2") || n.EndsWith(".fbz")) return "fb2";
            if (n.EndsWith(".md") || n.EndsWith(".markdown")) return "md";
            if (n.EndsWith(".txt") || n.EndsWith(".text")) return "txt";
            if (n.EndsWith(".zip") || n.EndsWith(".7z") || n.EndsWith(".rar")
                || n.EndsWith(".tar") || n.EndsWith(".gz") || n.EndsWith(".bz2")
                || n.EndsWith(".xz") || n.EndsWith(".tgz")) return "zip";
            return null;
        }

        /// <summary>按扩展名 + 文件头识别；PDF 优先走魔数，避免改了后缀就打不开。</summary>
        public static string DetectFormat(string name, byte[] data)
        {
            string byName = ExtFormat(name);
            if (byName == "epub" || byName == "pdf" || byName == "fb2" || byName == "md" || byName == "txt")
                return byName;

            if (data != null && data.Length > 4)
            {
                // "%PDF-"
                if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
                    return "pdf";
                // ZIP 家族：先看是不是 EPUB（有 META-INF/container.xml）
                if (data[0] == 0x50 && data[1] == 0x4B)
                {
                    try
                    {
                        using (var ms = new MemoryStream(data))
                        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                            if (ZipEntry(zip, "META-INF/container.xml") != null) return "epub";
                    }
                    catch { }
                    return "zip";
                }
                // FB2 是 XML，声明里带 FictionBook
                string head = Encoding.ASCII.GetString(data, 0, Math.Min(512, data.Length));
                if (head.IndexOf("FictionBook", StringComparison.OrdinalIgnoreCase) >= 0) return "fb2";
            }

            if (byName == "zip") return "zip";
            return "txt";
        }

        public static string ExtensionOf(string format)
        {
            switch (format)
            {
                case "epub": return ".epub";
                case "pdf": return ".pdf";
                case "fb2": return ".fb2";
                case "md": return ".md";
                default: return ".txt";
            }
        }

        /* ============================================================
         * 压缩包内取第一本书（转译 Areada ZipBookContainer.supportedZipEntryType）
         * ============================================================ */

        private static ZipArchiveEntry ZipEntry(ZipArchive zip, string name)
        {
            if (zip == null || string.IsNullOrEmpty(name)) return null;
            string n = name.Replace('\\', '/').TrimStart('/');
            var e = zip.GetEntry(n);
            if (e != null) return e;
            foreach (var x in zip.Entries)
                if (string.Equals(x.FullName.Replace('\\', '/').TrimStart('/'), n,
                                  StringComparison.OrdinalIgnoreCase)) return x;
            return null;
        }

        private static int FormatRank(string f)
        {
            switch (f)
            {
                case "epub": return 0;
                case "pdf": return 1;
                case "fb2": return 2;
                case "txt": return 3;
                case "md": return 4;
                default: return 99;
            }
        }

        /// <summary>从 ZIP/FBZ 里挑出第一本受支持的书，返回其内部文件名与字节。</summary>
        public static bool TryExtractBook(byte[] data, out string innerName, out byte[] innerData)
        {
            innerName = null;
            innerData = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry pick = null;
                    int best = 99;
                    foreach (var e in zip.Entries)
                    {
                        string n = e.FullName.Replace('\\', '/');
                        if (n.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (n.StartsWith("/") || n.Contains("..")) continue;   // 防 Zip Slip
                        if (string.IsNullOrEmpty(e.Name)) continue;            // 目录
                        if (e.Name.StartsWith(".")) continue;
                        int rank = FormatRank(ExtFormat(e.Name));
                        if (rank < best) { best = rank; pick = e; }
                    }
                    if (pick == null) return false;
                    innerName = pick.Name;
                    using (var s = pick.Open())
                    using (var mem = new MemoryStream())
                    {
                        s.CopyTo(mem);
                        innerData = mem.ToArray();
                    }
                    return innerData.Length > 0;
                }
            }
            catch { return false; }
        }

        /* ============================================================
         * PDF：一页就是一章，绝不拼接长页
         * ============================================================ */

        /// <summary>
        /// 把 PDF 的每一页映射成一个章节条目，让目录 / 进度 / 书签 / 上下页
        /// 全部复用已有的章节逻辑；真正的页面由 PdfEngine 逐页渲染成图片。
        /// </summary>
        public static List<Chapter> PdfChapters(int pageCount)
        {
            var list = new List<Chapter>();
            int n = Math.Max(1, pageCount);
            for (int i = 0; i < n; i++)
                list.Add(new Chapter
                {
                    Index = i,
                    Title = L10n.T("第 ") + (i + 1) + L10n.T(" 页"),
                    Url = "pdfpage:" + i,
                    Content = ""
                });
            return list;
        }

        /* ============================================================
         * FB2（转译 Areada Fb2Engine.collectSections）
         * ============================================================ */

        public static Book ParseFb2(byte[] data, string defaultName)
        {
            var book = new Book { Type = "local", Name = defaultName, Format = "fb2" };
            byte[] xmlBytes = data;
            // FBZ / fb2.zip 里先取出真正的 .fb2
            if (data != null && data.Length > 4 && data[0] == 0x50 && data[1] == 0x4B)
            {
                string innerName;
                byte[] inner;
                if (TryExtractBook(data, out innerName, out inner)) xmlBytes = inner;
            }
            if (xmlBytes == null || xmlBytes.Length == 0) return book;

            // 编码：优先用 XML 声明（Areada 交给 DOM 解析器），
            // 这里显式解码后去掉声明，避免 Mono 不认识 windows-1251 时解析失败
            string xml = DecodeXmlBytes(xmlBytes);
            xml = Regex.Replace(xml, @"^\s*<\?xml[^>]*\?>", "", RegexOptions.IgnoreCase).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,   // 防 XXE（对应 Areada 的 setFeature）
                    XmlResolver = null,
                    IgnoreComments = true
                };
                var doc = new XmlDocument();
                using (var xr = XmlReader.Create(new StringReader(xml), settings)) doc.Load(xr);

                var ti = FindNode(doc.DocumentElement, "title-info");
                string t = Clean(NodeValue(ti, "book-title"));
                if (t.Length > 0) book.Name = t;
                string author = Clean(NodeValue(ti, "first-name") + " " + NodeValue(ti, "last-name"));
                if (author.Length == 0) author = Clean(NodeValue(ti, "nickname"));
                book.Author = author;

                var sections = new List<KeyValuePair<string, string>>();
                var body = FindNode(doc.DocumentElement, "body");
                CollectFb2Sections(body, sections, 0);

                if (sections.Count == 0)
                {
                    // 兜底（Areada extractAllBodyText）：整篇按文本兜底切章
                    string plain = body != null ? HtmlToText(body.InnerText) : "";
                    book.Chapters = ParseTxt(plain);
                }
                else
                {
                    int idx = 0;
                    foreach (var kv in sections)
                    {
                        book.Chapters.Add(new Chapter
                        {
                            Index = idx++,
                            Title = string.IsNullOrWhiteSpace(kv.Key) ? L10n.T("第 ") + idx + L10n.T(" 节") : kv.Key,
                            Content = kv.Value.Trim()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // XML 坏了也尽量给一份纯文本，不让整本书打不开
                book.Chapters = ParseTxt(Regex.Replace(xml, @"<[^>]+>", "\n"));
                book.Intro = ex.Message;
            }
            if (book.Chapters.Count == 0) book.Chapters = ParseTxt("");
            return book;
        }

        private static string DecodeXmlBytes(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            string head = Encoding.ASCII.GetString(b, 0, Math.Min(256, b.Length));
            var m = Regex.Match(head, @"encoding\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (m.Success) return EncodingEx.Decode(b, m.Groups[1].Value);
            return EncodingEx.Detect(b, null);
        }

        private static XmlNode FindNode(XmlNode node, string local)
        {
            if (node == null) return null;
            if (node.NodeType == XmlNodeType.Element && node.LocalName == local) return node;
            foreach (XmlNode c in node.ChildNodes)
            {
                var r = FindNode(c, local);
                if (r != null) return r;
            }
            return null;
        }

        private static string NodeValue(XmlNode parent, string local)
        {
            var n = FindNode(parent, local);
            return n == null ? "" : Clean(n.InnerText);
        }

        private static string Clean(string s)
            => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();

        /// <summary>递归发现 section 并拍平成一维章节表（含嵌套 section）。</summary>
        private static void CollectFb2Sections(XmlNode node, List<KeyValuePair<string, string>> outp, int depth)
        {
            if (node == null || depth > 12 || outp.Count > 4000) return;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element || child.LocalName != "section") continue;
                string title = "";
                var sb = new StringBuilder();
                foreach (XmlNode c in child.ChildNodes)
                {
                    if (c.NodeType != XmlNodeType.Element) continue;
                    string ln = c.LocalName;
                    if (ln == "section") continue;
                    if (ln == "title" && title.Length == 0) { title = Clean(c.InnerText); continue; }
                    if (ln == "binary" || ln == "image" || ln == "description" || ln == "empty-line") continue;
                    string txt = Clean(c.InnerText);
                    if (txt.Length > 0) sb.AppendLine(txt);
                }
                if (sb.Length > 0) outp.Add(new KeyValuePair<string, string>(title, sb.ToString()));
                CollectFb2Sections(child, outp, depth + 1);
            }
        }

        /* ============================================================
         * Markdown（转译 Areada MarkdownEngine，补足按标题切章）
         * ============================================================ */

        public static Book ParseMarkdown(string text, string defaultName)
        {
            text = (text ?? "").TrimStart('\uFEFF');
            var m = Regex.Match(text, @"(?m)^\s*#\s+(.+)$");
            string name = m.Success ? Clean(m.Groups[1].Value) : defaultName;
            var book = new Book { Type = "local", Name = string.IsNullOrEmpty(name) ? defaultName : name, Format = "md" };

            var cur = new Chapter { Index = 0, Title = string.IsNullOrEmpty(name) ? L10n.T("正文") : name };
            var sb = new StringBuilder();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var hm = Regex.Match(raw, @"^\s*(#{1,3})\s+(.+)$");
                if (hm.Success && sb.ToString().Trim().Length > 0)
                {
                    cur.Content = sb.ToString().Trim();
                    book.Chapters.Add(cur);
                    cur = new Chapter { Index = book.Chapters.Count, Title = Clean(hm.Groups[2].Value) };
                    sb.Clear();
                    continue;
                }
                if (hm.Success) { cur.Title = Clean(hm.Groups[2].Value); continue; }
                sb.AppendLine(MdLine(raw));
            }
            cur.Content = sb.ToString().Trim();
            if (book.Chapters.Count == 0 || cur.Content.Length > 0) book.Chapters.Add(cur);
            return book;
        }

        /// <summary>行内 Markdown 标记降级为纯文本（Areada convertInline 的逆操作）。</summary>
        private static string MdLine(string line)
        {
            if (line == null) return "";
            line = Regex.Replace(line, @"^\s{0,3}>\s?", "");
            line = Regex.Replace(line, @"^\s*[-*+]\s+", "· ");
            line = Regex.Replace(line, @"^\s*\d+\.\s+", "");
            line = Regex.Replace(line, @"!\[([^\]]*)\]\([^)]*\)", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            line = Regex.Replace(line, @"\*\*\*(.+?)\*\*\*", "$1");
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"(?<!\*)\*([^*]+?)\*(?!\*)", "$1");
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            line = Regex.Replace(line, @"^\s*(#{1,6})\s*", "");
            return line.TrimEnd();
        }

        /* ============================================================
         * EPUB 增强（转译 Areada EpubEngine.buildTitleByHref / buildManifest）
         * ============================================================ */

        private sealed class EpItem
        {
            public string Href = "";
            public string Media = "";
            public string Props = "";
            public string Path = "";
        }

        private static string DirOf(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Replace('\\', '/');
            int i = p.LastIndexOf('/');
            return i < 0 ? "" : p.Substring(0, i);
        }

        private static string FileOf(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.Replace('\\', '/');
            int i = p.LastIndexOf('/');
            return i < 0 ? p : p.Substring(i + 1);
        }

        private static string Attr2(string tag, string name)
        {
            var m = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value
                 : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }

        /// <summary>
        /// EPUB 完整解析：spine 顺序 + NAV/NCX 目录标题 + 内嵌封面。
        /// 与旧版 ParseEpub 的差别只在「正文没有 h1 时」用目录标题代替「第 N 节」，
        /// 章节数量与正文内容保持一致。
        /// </summary>
        public static Book ParseEpubFull(byte[] data, string defaultName, out byte[] cover)
        {
            cover = null;
            var book = new Book { Type = "local", Name = defaultName, Format = "epub" };
            using (var ms = new MemoryStream(data))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var container = ZipEntry(zip, "META-INF/container.xml");
                if (container == null) return book;
                string opfPath;
                using (var sr = new StreamReader(container.Open()))
                {
                    var mm = Regex.Match(sr.ReadToEnd(), "full-path\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
                    if (!mm.Success) return book;
                    opfPath = mm.Groups[1].Value;
                }
                var opfEntry = ZipEntry(zip, NormalizeZipPath(DirOf(opfPath), FileOf(opfPath)));
                if (opfEntry == null) return book;
                string opfDir = DirOf(opfPath);
                string opf;
                using (var sr = new StreamReader(opfEntry.Open())) opf = sr.ReadToEnd();

                var tm = Regex.Match(opf, "<dc:title[^>]*>([^<]+)</dc:title>", RegexOptions.IgnoreCase);
                if (tm.Success) book.Name = DecodeEntities(tm.Groups[1].Value.Trim());
                var cm = Regex.Match(opf, "<dc:creator[^>]*>([^<]+)</dc:creator>", RegexOptions.IgnoreCase);
                if (cm.Success) book.Author = DecodeEntities(cm.Groups[1].Value.Trim());

                // manifest：id -> (href, media-type, properties, zip 内路径)
                var items = new Dictionary<string, EpItem>(StringComparer.OrdinalIgnoreCase);
                foreach (Match mi in Regex.Matches(opf, "<item\\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    string id = Attr2(mi.Value, "id"), href = Attr2(mi.Value, "href");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href)) continue;
                    items[id] = new EpItem
                    {
                        Href = href,
                        Media = Attr2(mi.Value, "media-type") ?? "",
                        Props = Attr2(mi.Value, "properties") ?? "",
                        Path = NormalizeZipPath(opfDir, href)
                    };
                }

                // 目录标题：EPUB3 NAV 优先，EPUB2 NCX 补充（先到先得）
                var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in items)
                {
                    var it = kv.Value;
                    bool nav = it.Props.ToLowerInvariant().Contains("nav")
                               || it.Href.EndsWith("nav.xhtml", StringComparison.OrdinalIgnoreCase)
                               || it.Href.EndsWith("toc.xhtml", StringComparison.OrdinalIgnoreCase);
                    if (!nav) continue;
                    var e = ZipEntry(zip, it.Path);
                    if (e == null) continue;
                    string h;
                    using (var sr = new StreamReader(e.Open())) h = sr.ReadToEnd();
                    foreach (Match a in Regex.Matches(h,
                        @"<a\b[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    {
                        string key = NormalizeZipPath(DirOf(it.Path), a.Groups[1].Value);
                        string title = DecodeEntities(Regex.Replace(a.Groups[2].Value, @"<[^>]+>", "")).Trim();
                        if (title.Length > 0 && !titles.ContainsKey(key)) titles[key] = Clean(title);
                    }
                }
                string tocId = null;
                var sm0 = Regex.Match(opf, "<spine\\b[^>]*>", RegexOptions.IgnoreCase);
                if (sm0.Success) tocId = Attr2(sm0.Value, "toc");
                var ncxCandidates = new List<EpItem>();
                if (!string.IsNullOrEmpty(tocId) && items.ContainsKey(tocId)) ncxCandidates.Add(items[tocId]);
                foreach (var kv in items)
                    if (kv.Value.Media.IndexOf("ncx", StringComparison.OrdinalIgnoreCase) >= 0
                        || kv.Value.Href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase))
                        if (!ncxCandidates.Contains(kv.Value)) ncxCandidates.Add(kv.Value);
                foreach (var it in ncxCandidates)
                {
                    var e = ZipEntry(zip, it.Path);
                    if (e == null) continue;
                    string ncx;
                    using (var sr = new StreamReader(e.Open())) ncx = sr.ReadToEnd();
                    foreach (Match np in Regex.Matches(ncx,
                        @"<navPoint\b[^>]*>(.*?)(?=<navPoint\b|</navMap>)",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    {
                        var src = Regex.Match(np.Groups[1].Value,
                            @"<content\b[^>]*?src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                        var txt = Regex.Match(np.Groups[1].Value, @"<text>(.*?)</text>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (!src.Success || !txt.Success) continue;
                        string key = NormalizeZipPath(DirOf(it.Path), src.Groups[1].Value);
                        string title = DecodeEntities(Regex.Replace(txt.Groups[1].Value, @"<[^>]+>", "")).Trim();
                        if (title.Length > 0 && !titles.ContainsKey(key)) titles[key] = Clean(title);
                    }
                }

                // 内嵌封面：properties="cover-image" 或 <meta name="cover" content="id">
                string coverId = null;
                foreach (var kv in items)
                    if (kv.Value.Props.ToLowerInvariant().Contains("cover-image")) { coverId = kv.Key; break; }
                if (coverId == null)
                {
                    var meta = Regex.Match(opf,
                        @"<meta\b(?=[^>]*\bname\s*=\s*[""']cover[""'])[^>]*?\bcontent\s*=\s*[""']([^""']+)[""']",
                        RegexOptions.IgnoreCase);
                    if (!meta.Success)
                        meta = Regex.Match(opf,
                            @"<meta\b(?=[^>]*\bcontent\s*=\s*[""']([^""']+)[""'])[^>]*?\bname\s*=\s*[""']cover[""']",
                            RegexOptions.IgnoreCase);
                    if (meta.Success) coverId = meta.Groups[1].Value;
                }
                if (coverId != null && items.ContainsKey(coverId))
                {
                    var ce = ZipEntry(zip, items[coverId].Path);
                    if (ce != null)
                    {
                        try
                        {
                            using (var s = ce.Open())
                            using (var mem = new MemoryStream()) { s.CopyTo(mem); cover = mem.ToArray(); }
                        }
                        catch { }
                    }
                }

                // spine：按 itemref 顺序组装章节
                var sm = Regex.Match(opf, "<spine[^>]*>(.*?)</spine>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (sm.Success)
                {
                    int idx = 0;
                    foreach (Match sr in Regex.Matches(sm.Groups[1].Value, "<itemref\\b[^>]*>", RegexOptions.IgnoreCase))
                    {
                        string idref = Attr2(sr.Value, "idref");
                        if (string.IsNullOrEmpty(idref) || !items.ContainsKey(idref)) continue;
                        var it = items[idref];
                        var entry = ZipEntry(zip, it.Path);
                        if (entry == null) continue;
                        string html;
                        using (var s = new StreamReader(entry.Open())) html = s.ReadToEnd();
                        var ch = new Chapter
                        {
                            Index = idx++,
                            Title = EpubChapterTitle(html, it.Path, idx, titles),
                            Content = HtmlToText(html)
                        };
                        if (!string.IsNullOrWhiteSpace(ch.Content)) book.Chapters.Add(ch);
                    }
                }
                if (book.Chapters.Count == 0)
                    book.Chapters.Add(new Chapter { Index = 0, Title = L10n.T("正文"), Content = HtmlToText(opf) });
            }
            return book;
        }

        private static string EpubChapterTitle(string html, string path, int n, Dictionary<string, string> titles)
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            var h = doc.DocumentNode.SelectSingleNode("//h1 | //h2 | //h3 | //h4 | //title");
            if (h != null && !string.IsNullOrWhiteSpace(h.InnerText))
                return DecodeEntities(h.InnerText.Trim());
            string t;
            if (!string.IsNullOrEmpty(path) && titles.TryGetValue(path, out t) && !string.IsNullOrWhiteSpace(t))
                return t;
            return L10n.T("第 ") + n + L10n.T(" 节");
        }
    }
}

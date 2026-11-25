// ============================================================
// 阅读模块 · 书库服务层（App 原生界面 与 网页端 共用同一份数据）
//
// 设计要点：
//   · 所有写操作都在 _io 锁内做「读-改-写」，避免 App 与网页端
//     同时写 shelf.json 时互相覆盖（两端的书架、进度完全共用）。
//   · 每次读取都从磁盘加载，App 切回前台 / 网页刷新都能看到对方的变化。
//   · 本地书正文落到 FilesDir/reader/books/<id>.<ext>，书架只存元数据。
//   · 排版偏好存 FilesDir/reader/prefs.json，网页端改完 App 立刻生效。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Android.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace AhoChatA.Reader
{
    public static class ReaderLibrary
    {
        private static Context _ctx;
        private static ReaderStore _store;
        private static ReaderEngine _engine;
        private static readonly object _io = new object();
        private static readonly Dictionary<string, Book> _cache = new Dictionary<string, Book>();
        private static ReaderPrefs _prefs;

        public static void Init(Context ctx)
        {
            if (_store != null) return;
            lock (_io)
            {
                if (_store != null) return;
                _ctx = ctx.ApplicationContext ?? ctx;
                _store = new ReaderStore(_ctx);
                _engine = new ReaderEngine();
                PdfCache.Init(_ctx);
            }
        }

        public static ReaderEngine Engine => _engine ?? (_engine = new ReaderEngine());
        public static ReaderStore Store => _store;
        public static Android.Content.Context AppContext => _ctx;

        /// <summary>屏幕宽度（像素），用于预缓存 PDF 第一页时选定渲染宽度。</summary>
        public static int ScreenWidth
        {
            get
            {
                try { return Math.Max(480, _ctx.Resources.DisplayMetrics.WidthPixels); }
                catch { return 1080; }
            }
        }

        /// <summary>旧书架数据没有 Format 字段，按本地文件扩展名推断。</summary>
        public static string FormatOf(ShelfItem it)
        {
            if (it == null) return "txt";
            if (!string.IsNullOrEmpty(it.Format)) return it.Format;
            string p = it.Path ?? "";
            if (p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
            if (p.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)) return "epub";
            if (p.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase)) return "fb2";
            if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "md";
            return "txt";
        }

        /// <summary>书架封面（PDF 第一页 / EPUB 内嵌封面）；无封面返回 null。</summary>
        public static byte[] CoverBytes(string id) => PdfCache.CoverJpeg(id);

        /// <summary>PDF 单页 JPEG（网页端与 App 共用同一份磁盘缓存）。</summary>
        public static byte[] PdfPage(string id, string path, int page, int width, float zoom = 1f)
            => PdfCache.PageJpeg(id, path, page, width, zoom);
        public static string BookDir
        {
            get
            {
                var d = Path.Combine(_ctx.FilesDir.AbsolutePath, "reader", "books");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        /* ---------------- 本地扫描目录 ---------------- */

        /// <summary>预设默认扫描目录（与影音一致，指向常见的书籍/下载位置）。</summary>
        public static readonly string[] DefaultScanDirs =
        {
            "/storage/emulated/0/Download",
            "/storage/emulated/0/Documents",
            "/storage/emulated/0/Books",
            L10n.T("/storage/emulated/0/小说"),
            L10n.T("/storage/emulated/0/电子书")
        };

        public static List<string> ScanDirs()
        {
            lock (_io)
            {
                var dirs = _store.LoadScanDirs();
                if (dirs == null) dirs = new List<string>(DefaultScanDirs);   // 仅首次用预设默认目录
                return dirs;
            }
        }

        public static void SaveScanDirs(List<string> dirs)
        {
            lock (_io) { _store.SaveScanDirs(dirs ?? new List<string>()); }
        }

        /* ---------------- 书架 ---------------- */

        public static List<ShelfItem> Shelf()
        {
            lock (_io) return _store.LoadShelf();
        }

        public static ShelfItem Find(string id)
        {
            lock (_io) return _store.LoadShelf().Find(x => x.Id == id);
        }

        /// <summary>整表覆盖保存（导入/删除后调用；写入前应基于最新磁盘内容修改）。</summary>
        public static void SaveShelf(List<ShelfItem> list)
        {
            lock (_io) _store.SaveShelf(list);
        }

        public static void Rename(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (_io)
            {
                var list = _store.LoadShelf();
                var it = list.Find(x => x.Id == id);
                if (it == null) return;
                it.Name = name.Trim();
                _store.SaveShelf(list);
            }
            Book b;
            if (_cache.TryGetValue(id ?? "", out b) && b != null) b.Name = name.Trim();
        }

        public static void Delete(string id)
        {
            string path = null;
            lock (_io)
            {
                var list = _store.LoadShelf();
                var it = list.Find(x => x.Id == id);
                if (it != null)
                {
                    path = it.Path;
                    list.Remove(it);
                    _store.SaveShelf(list);
                }
            }
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) TrashManager.Instance.MoveToTrash(path); } catch { }
            PdfCache.Remove(id);          // 清掉封面 / 页图缓存，避免占着空间
            lock (_io) _cache.Remove(id ?? "");
        }

        /// <summary>
        /// 导入本地文件（App 的文件选择器、网页端上传都走这里）。
        /// 支持 TXT / EPUB / PDF / FB2 / FBZ / Markdown / ZIP 内含书籍。
        /// PDF 在导入瞬间就把第一页渲染成封面并缓存阅读用页图。
        /// </summary>
        /* ---------------- 书架文件夹（分组） ---------------- */

        /// <summary>扫描导入后自动分组：同一父目录下的书超过 4 本，才把它们归到一个
        /// 以该目录名为名的书架文件夹；≤4 本保持顶层。不改变任何真实文件路径。</summary>
        public static void ApplyFolderGrouping(List<(string Id, string Parent)> hits)
        {
            if (hits == null || hits.Count == 0) return;
            var byParent = new Dictionary<string, List<string>>();
            foreach (var h in hits)
            {
                if (!byParent.TryGetValue(h.Parent, out var g)) { g = new List<string>(); byParent[h.Parent] = g; }
                g.Add(h.Id);
            }
            lock (_io)
            {
                var list = _store.LoadShelf();
                foreach (var kv in byParent)
                {
                    if (kv.Value.Count <= 4) continue;   // 超过四本才合为文件夹
                    foreach (var id in kv.Value)
                    {
                        var it = list.Find(x => x.Id == id);
                        if (it != null) it.Folder = kv.Key;
                    }
                }
                _store.SaveShelf(list);
            }
        }

        /// <summary>重命名一个书架文件夹（只改展示名，不影响文件）。</summary>
        public static void RenameFolder(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (oldName == newName) return;
            lock (_io)
            {
                var list = _store.LoadShelf();
                foreach (var it in list)
                    if (it.Folder == oldName) it.Folder = newName;
                _store.SaveShelf(list);
            }
        }

        /// <summary>把一本书移入（或移出：folder 传空）某个书架文件夹。</summary>
        public static void MoveToFolder(string id, string folder)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_io)
            {
                var list = _store.LoadShelf();
                var it = list.Find(x => x.Id == id);
                if (it == null) return;
                it.Folder = folder ?? "";
                _store.SaveShelf(list);
            }
        }

        /// <summary>当前书架中所有文件夹名（去重）。</summary>
        public static List<string> FolderList()
        {
            var s = new HashSet<string>();
            foreach (var it in Shelf()) if (!string.IsNullOrEmpty(it.Folder)) s.Add(it.Folder);
            return new List<string>(s);
        }

        /// <summary>解散一个书架文件夹：把组内所有书移回顶层（Folder 置空），不影响文件。</summary>
        public static void DissolveFolder(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return;
            lock (_io)
            {
                var list = _store.LoadShelf();
                foreach (var it in list) if (it.Folder == folderName) it.Folder = "";
                _store.SaveShelf(list);
            }
        }

        public static ShelfItem Import(string displayName, byte[] bytes, out string error)
        {
            error = null;
            if (bytes == null || bytes.Length == 0) { error = "文件为空"; return null; }

            string name = NormalizeName(displayName);

            // Office / 文档类：不做章节解析，原样保存为「文档书」，打开时用 Asset viewer 渲染。
            // 这样 Web 上传书架、本地文件夹扫描、系统分享 三条入口都统一由阅读模块管理。
            string rawExt = (System.IO.Path.GetExtension(displayName) ?? "").TrimStart('.').ToLowerInvariant();
            string[] viewerExts = { "docx", "xlsx", "pptx", "doc", "xls", "ppt" };
            bool isViewerDoc = false;
            foreach (var ve in viewerExts) if (rawExt == ve) { isViewerDoc = true; break; }
            if (isViewerDoc)
            {
                string vid = "local_" + Guid.NewGuid().ToString("N");
                string vdest = Path.Combine(BookDir, vid + "." + rawExt);
                try
                {
                    File.WriteAllBytes(vdest, bytes);
                    var vitem = new ShelfItem
                    {
                        Id = vid, Name = name, Author = "", Type = "viewer",
                        Format = rawExt, Path = vdest, Added = DateTime.Now, Total = 1
                    };
                    lock (_io)
                    {
                        var list = _store.LoadShelf();
                        list.Insert(0, vitem);
                        _store.SaveShelf(list);
                    }
                    error = null;
                    return vitem;
                }
                catch (Exception ex) { error = L10n.T("写入失败：") + ex.Message; return null; }
            }

            string format = BookParser.DetectFormat(displayName, bytes);

            // 压缩包：取出内部第一本受支持的书再走一遍导入
            if (format == "zip")
            {
                string innerName;
                byte[] inner;
                if (!BookParser.TryExtractBook(bytes, out innerName, out inner))
                {
                    error = L10n.T("压缩包内没有找到可阅读的书籍（EPUB / PDF / TXT / FB2 / MD）");
                    return null;
                }
                return Import(innerName, inner, out error);
            }

            string id = "local_" + Guid.NewGuid().ToString("N");
            string ext = BookParser.ExtensionOf(format);
            string dest = Path.Combine(BookDir, id + ext);
            try { File.WriteAllBytes(dest, bytes); }
            catch (Exception ex) { error = L10n.T("写入失败：") + ex.Message; return null; }

            Action cleanup = () => { try { if (File.Exists(dest)) File.Delete(dest); } catch { } };
            Book book;
            byte[] epubCover = null;
            try
            {
                switch (format)
                {
                    case "pdf":
                    {
                        int n;
                        try { n = PdfEngine.CountPages(dest); }
                        catch (Exception ex) { error = L10n.T("PDF 打开失败：") + ex.Message; cleanup(); return null; }
                        if (n <= 0) { error = L10n.T("PDF 没有可显示的页面"); cleanup(); return null; }
                        book = new Book
                        {
                            Type = "local", Name = name, Format = "pdf", Kind = "pdf",
                            Chapters = BookParser.PdfChapters(n)
                        };
                        break;
                    }
                    case "epub":
                    {
                        book = BookParser.ParseEpubFull(bytes, name, out epubCover);
                        if (book.Chapters.Count == 0) { error = "EPUB 解析失败：没有可用章节"; cleanup(); return null; }
                        break;
                    }
                    case "fb2":
                    {
                        book = BookParser.ParseFb2(bytes, name);
                        if (book.Chapters.Count == 0) { error = "FB2 解析失败：没有可用章节"; cleanup(); return null; }
                        break;
                    }
                    case "md":
                    {
                        string md = EncodingEx.Detect(bytes, null);
                        book = BookParser.ParseMarkdown(md, name);
                        break;
                    }
                    default:
                    {
                        string text = EncodingEx.Detect(bytes, null);
                        if (string.IsNullOrWhiteSpace(text)) { error = L10n.T("文件内容为空或编码无法识别"); cleanup(); return null; }
                        book = new Book
                        {
                            Type = "local", Name = name, Format = "txt",
                            Chapters = BookParser.ParseTxt(text)
                        };
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                error = L10n.T("解析失败：") + ex.Message;
                cleanup();
                return null;
            }

            // 封面：PDF 用第一页（并预缓存第一页阅读图），EPUB 用内嵌封面
            if (format == "pdf") PdfCache.EnsureFirstPage(id, dest, ScreenWidth);
            else if (epubCover != null && epubCover.Length > 0) PdfCache.SaveCover(id, epubCover);

            var item = new ShelfItem
            {
                Id = id,
                Name = name,
                Author = book.Author ?? "",
                Type = "local",
                Format = format,
                Path = dest,
                Added = DateTime.Now,
                Total = book.Chapters.Count
            };
            book.Id = id;
            book.Name = name;
            book.Format = format;

            lock (_io)
            {
                var list = _store.LoadShelf();
                list.Insert(0, item);
                _store.SaveShelf(list);
                _cache[id] = book;
            }
            return item;
        }

        /* ---------------- 打开 / 章节 ---------------- */

        /// <summary>拿到一本书的完整章节表（本地解析，结果缓存）。</summary>
        public static Book GetBook(ShelfItem item, out string error)
        {
            error = null;
            if (item == null) { error = "书籍不存在"; return null; }

            // 文档书（docx/xlsx/pptx/doc/xls/ppt）：不做章节解析，给一个占位章节，
            // 真正的渲染由 /viewer/*.html 负责（书架打开时前端据此分流）。
            if (item.Type == "viewer")
            {
                return new Book
                {
                    Id = item.Id, Name = item.Name, Author = "", Type = "viewer",
                    Format = FormatOf(item),
                    Chapters = new List<Chapter> { new Chapter { Index = 0, Title = L10n.T("文档") } }
                };
            }

            lock (_io)
            {
                Book cached;
                if (_cache.TryGetValue(item.Id ?? "", out cached) && cached != null
                    && cached.Chapters != null && cached.Chapters.Count > 0)
                {
                    if (!string.IsNullOrEmpty(item.Name)) cached.Name = item.Name;
                    if (string.IsNullOrEmpty(cached.Format)) cached.Format = FormatOf(item);
                    return cached;
                }
            }

            if (item.Type == "online")
            {
                var b = new Book
                {
                    Id = item.Id, Name = item.Name, Author = item.Author, Type = "online",
                    SourceUrl = item.SourceUrl, BookUrl = item.BookUrl, TocUrl = item.TocUrl,
                    CoverUrl = item.CoverUrl
                };
                // 目录有磁盘缓存就先戴上：冷启动打开在线书不必再等一次联网拉目录
                try
                {
                    var tocJson = _store?.LoadToc(item.Id);
                    if (!string.IsNullOrEmpty(tocJson))
                    {
                        var chs = JsonConvert.DeserializeObject<List<Chapter>>(tocJson);
                        if (chs != null && chs.Count > 0) b.Chapters = chs;
                    }
                }
                catch { }
                return b;   // 章节正文见 EnsureOnlineAsync / ChapterTextAsync
            }

            try
            {
                if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
                {
                    error = L10n.T("本地文件已丢失");
                    return null;
                }
                string fmt = FormatOf(item);
                // ★ 大文件打开慢的关键修复：PDF 不需要把整本读进内存（页数已在导入时缓存，
                // 内容由 viewer 走 /doc 的 Range 分片按需加载）。只在真正需要解析字节的格式才读。
                byte[] bytes = fmt == "pdf" ? null : File.ReadAllBytes(item.Path);
                Book book;
                byte[] epubCover;
                switch (fmt)
                {
                    case "pdf":
                        // 页数在导入时就已写进 shelf.json，这里不再打开 PDF；
                        // 只有真正要显示某一页时才由 PdfCache 打开并渲染
                        int pages = item.Total > 0 ? item.Total : PdfEngine.CountPages(item.Path);
                        book = new Book
                        {
                            Type = "local", Name = item.Name, Format = "pdf", Kind = "pdf",
                            Chapters = BookParser.PdfChapters(pages)
                        };
                        break;
                    case "epub":
                        book = BookParser.ParseEpubFull(bytes, item.Name, out epubCover);
                        if (epubCover != null && epubCover.Length > 0 && !PdfCache.HasCover(item.Id))
                            PdfCache.SaveCover(item.Id, epubCover);
                        break;
                    case "fb2":
                        book = BookParser.ParseFb2(bytes, item.Name);
                        break;
                    case "md":
                        book = BookParser.ParseMarkdown(EncodingEx.Detect(bytes, null), item.Name);
                        break;
                    default:
                        var text = EncodingEx.Detect(bytes, null);
                        book = new Book
                        {
                            Type = "local", Name = item.Name, Format = "txt",
                            Chapters = BookParser.ParseTxt(text)
                        };
                        break;
                }
                book.Id = item.Id;
                if (!string.IsNullOrEmpty(item.Name)) book.Name = item.Name;
                lock (_io) { _cache[item.Id ?? ""] = book; }
                return book;
            }
            catch (Exception ex)
            {
                error = L10n.T("解析失败：") + ex.Message;
                return null;
            }
        }

        /// <summary>在线书补齐详情 + 目录。</summary>
        public static async Task<string> EnsureOnlineAsync(ShelfItem item, Book book)
        {
            if (item == null || book == null) return "书籍不存在";
            if (book.Chapters != null && book.Chapters.Count > 0) return null;

            var src = FindSource(item.SourceUrl);
            if (src == null) return "找不到该书对应的书源，请重新导入书源";
            try
            {
                if (!string.IsNullOrEmpty(book.BookUrl))
                    await Engine.GetBookInfoAsync(src, book).ConfigureAwait(false);
                book.Chapters = await Engine.GetTocAsync(src, book).ConfigureAwait(false);
                if (book.Chapters.Count == 0) return "目录为空，可能是书源规则失效";
                lock (_io) { _cache[item.Id ?? ""] = book; }
                // 目录落盘：下次冷启动直接命中，不再等联网
                try { _store?.SaveToc(item.Id, JsonConvert.SerializeObject(book.Chapters)); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                return L10n.T("目录获取失败：") + ex.Message;
            }
        }

        public static async Task<string> ChapterTextAsync(ShelfItem item, Book book, int idx)
        {
            if (book == null || book.Chapters == null || idx < 0 || idx >= book.Chapters.Count) return "";
            var ch = book.Chapters[idx];
            if (ch.Content != null) return ch.Content;

            var src = FindSource(item.SourceUrl ?? book.SourceUrl);
            if (src == null) return "";
            var text = await Engine.GetContentAsync(src, ch.Url, _store, book.Id ?? item.Id, idx).ConfigureAwait(false);
            ch.Content = text;
            return text;
        }

        /* ---------------- 进度 ---------------- */

        /// <summary>保存阅读进度（章号 + 章内位置）。App 与网页端都调用这里。</summary>
        public static void SaveProgress(string id, int chapter, double pct, int total)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (pct < 0) pct = 0; if (pct > 1) pct = 1;
            lock (_io)
            {
                var list = _store.LoadShelf();
                var it = list.Find(x => x.Id == id);
                if (it == null) return;
                it.LastChapter = Math.Max(0, chapter);
                it.Pct = pct;
                if (total > 0) it.Total = total;
                int t = it.Total;
                it.Progress = t <= 0 ? 0 : Math.Min(1.0, (it.LastChapter + pct) / (double)t);
                it.Updated = DateTime.Now;
                _store.SaveShelf(list);
            }
        }

        /* ---------------- 书签 ---------------- */

        public static List<Bookmark> Bookmarks(string bookId)
        {
            lock (_io) return _store.LoadBookmarks(bookId);
        }

        public static Bookmark AddBookmark(string bookId, int chapter, string chapterTitle, double pct, string text)
        {
            lock (_io)
            {
                var list = _store.LoadBookmarks(bookId);
                var bm = new Bookmark
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    BookId = bookId,
                    ChapterIndex = chapter,
                    ChapterTitle = chapterTitle ?? "",
                    Pct = pct,
                    Text = text ?? "",
                    Created = DateTime.Now
                };
                list.Add(bm);
                _store.SaveBookmarks(bookId, list);
                return bm;
            }
        }

        public static void DeleteBookmark(string bookId, string id)
        {
            lock (_io)
            {
                var list = _store.LoadBookmarks(bookId);
                list.RemoveAll(x => x.Id == id);
                _store.SaveBookmarks(bookId, list);
            }
        }

        /* ---------------- 离线缓存 ---------------- */

        /// <summary>把整本书的正文抓到本地（legado 的「缓存全部」）。</summary>
        public static async Task<int> CacheAllAsync(ShelfItem item, Book book, Action<int, int> progress = null)
        {
            if (item == null || book?.Chapters == null || book.Chapters.Count == 0) return 0;
            // PDF 是按页渲染的图片，没有可缓存的正文文本
            if (FormatOf(item) == "pdf") return 0;
            int ok = 0;
            for (int i = 0; i < book.Chapters.Count; i++)
            {
                try
                {
                    var t = await ChapterTextAsync(item, book, i).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(t)) ok++;
                }
                catch { }
                progress?.Invoke(i + 1, book.Chapters.Count);
            }
            return ok;
        }

        public static int CachedCount(string bookId) => _store?.CountCached(bookId) ?? 0;

        /* ---------------- 阅读位置预缓存（点开即读，不用等网络） ---------------- */

        private static int _prefetching;
        // 本次进程内已经尝试过的章节（成功的会命中磁盘缓存，失败的别在每次进书架时反复重试）
        private static readonly HashSet<string> _prefetchTried = new HashSet<string>();

        /// <summary>是否正在后台预缓存（书架页可据此提示）。</summary>
        public static bool IsPrefetching => Volatile.Read(ref _prefetching) != 0;

        /// <summary>
        /// 后台把每本在线书「上次读到的那一章」往后 N 章正文抓到本地磁盘缓存。
        /// 之后点开书时对应章节直接命中缓存，几乎零等待。
        /// 单实例串行执行；已缓存的章节只查文件存在性，不重复下载；每章间让出一点带宽，
        /// 避免和用户正在读的那一章抢网络。
        /// </summary>
        public static void PrefetchAhead(int count = 50)
        {
            if (count <= 0) return;
            if (Interlocked.CompareExchange(ref _prefetching, 1, 0) != 0) return;

            Task.Run(async () =>
            {
                try
                {
                    var shelf = Shelf();
                    foreach (var it in shelf)
                    {
                        if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                        if (!string.Equals(it.Type, "online", StringComparison.OrdinalIgnoreCase)) continue;
                        if (FormatOf(it) == "pdf") continue;
                        try
                        {
                            Book book;
                            string err;
                            book = GetBook(it, out err);
                            if (book == null) continue;

                            if (book.Chapters == null || book.Chapters.Count == 0)
                            {
                                // 只给「读过」的书补目录，避免一进书架就对整架未读的书发请求
                                if (it.LastChapter <= 0 && it.Pct <= 0) continue;
                                if (await EnsureOnlineAsync(it, book).ConfigureAwait(false) != null) continue;
                            }

                            int total = book.Chapters.Count;
                            if (total <= 0) continue;
                            int start = Math.Max(0, Math.Min(it.LastChapter, total - 1));
                            int end = Math.Min(total, start + count);
                            int newCached = 0;
                            for (int i = start; i < end; i++)
                            {
                                if (_prefetching == 0) return;              // 被取消
                                if (_store != null && _store.HasChapter(it.Id, i)) continue;
                                string key = it.Id + ":" + i;
                                lock (_prefetchTried) { if (!_prefetchTried.Add(key)) continue; }
                                try { await ChapterTextAsync(it, book, i).ConfigureAwait(false); newCached++; }
                                catch
                                {
                                    // 单章失败多半是瞬时超时/限流：稍等再试一次，仍失败就留到下次进书架
                                    try
                                    {
                                        await Task.Delay(700).ConfigureAwait(false);
                                        await ChapterTextAsync(it, book, i).ConfigureAwait(false);
                                        newCached++;
                                    }
                                    catch { }
                                }
                                try { await Task.Delay(150).ConfigureAwait(false); } catch { }
                            }
                            try
                            {
                                Android.Util.Log.Info("MchatReader",
                                    $"预缓存《{it.Name}》第 {start}-{end - 1} 章：新缓存 {newCached} 章（共 {end - start} 章）");
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
                catch { }
                finally { Interlocked.Exchange(ref _prefetching, 0); }
            });
        }

        /// <summary>取消正在进行的预缓存（进入阅读时可调用，把带宽让给当前章）。</summary>
        public static void CancelPrefetch() => Interlocked.Exchange(ref _prefetching, 0);

        public static void ClearCache(string bookId)
        {
            _store?.ClearChapterCache(bookId);
            lock (_io)
            {
                Book b;
                if (_cache.TryGetValue(bookId ?? "", out b) && b?.Chapters != null)
                    foreach (var c in b.Chapters) c.Content = null;
            }
        }

        /* ---------------- 换源 ---------------- */

        /// <summary>
        /// 把书架条目切到另一个书源的同名书（legado 的「换源」）。
        /// 保留原书架 id 与阅读进度，只换 sourceUrl / bookUrl / tocUrl。
        /// </summary>
        public static void ChangeSource(string id, string sourceUrl, string bookUrl, string tocUrl, string bookName, string author)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(bookUrl)) return;
            lock (_io)
            {
                var list = _store.LoadShelf();
                var it = list.Find(x => x.Id == id);
                if (it == null) return;
                it.Type = "online";
                it.SourceUrl = sourceUrl;
                it.BookUrl = bookUrl;
                it.TocUrl = string.IsNullOrEmpty(tocUrl) ? bookUrl : tocUrl;
                if (!string.IsNullOrEmpty(bookName)) it.Name = bookName;
                if (!string.IsNullOrEmpty(author)) it.Author = author;
                it.Updated = DateTime.Now;
                _store.SaveShelf(list);
                _store.ClearChapterCache(id);
                _cache.Remove(id);
            }
        }

        /* ---------------- 发现 ---------------- */

        /// <summary>某个书源的分类列表（exploreUrl）。</summary>
        public static List<ExploreCategory> ExploreCategories(BookSource src)
        {
            try { return Engine.ExploreCategories(src) ?? new List<ExploreCategory>(); }
            catch { return new List<ExploreCategory>(); }
        }

        public static Task<List<Book>> ExploreAsync(BookSource src, ExploreCategory cat, int page = 1)
            => Engine.ExploreAsync(src, cat, page);

        /// <summary>跨书源搜索（换源 / 网页端共用）：只搜文本源，按权重排序，全量并发（单源超时 25s）。</summary>
        public static async Task<List<Book>> SearchAllAsync(string key, int maxSources = 0)
        {
            var enabled = Sources().FindAll(x => x.Enabled && x.BookSourceType == 0
                                                 && !string.IsNullOrEmpty(x.SearchUrl));
            enabled.Sort((a, b) =>
            {
                int w = b.Weight.CompareTo(a.Weight);
                return w != 0 ? w : string.Compare(a.BookSourceName, b.BookSourceName, StringComparison.Ordinal);
            });
            if (maxSources > 0 && enabled.Count > maxSources) enabled = enabled.GetRange(0, maxSources);

            // 各源并发跑（legado 同样是并发搜索）：单源超时/报错不影响整体结果
            var tasks = new List<Task<List<Book>>>();
            foreach (var src in enabled)
            {
                var s = src;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var work = Engine.SearchAsync(s, key);
                        var done = await Task.WhenAny(work, Task.Delay(25000)).ConfigureAwait(false);
                        if (done != work) return new List<Book>();          // 超时的源直接跳过
                        var r = await work.ConfigureAwait(false);
                        if (r != null) foreach (var b in r) b.SourceUrl = s.BookSourceUrl;
                        return r ?? new List<Book>();
                    }
                    catch { return new List<Book>(); }
                }));
            }

            var outp = new List<Book>();
            foreach (var t in tasks)
            {
                try { outp.AddRange(await t.ConfigureAwait(false)); } catch { }
            }
            return outp;
        }

        /// <summary>跨书源搜索（增量版）：各源并发跑，单个源一有结果就通过 onBatch 抛出，
        /// 并通过 onProgress(done, total) 回报已完成的源数。UI 可据此逐源追加列表并显示进度。</summary>
        public static async Task SearchIncrementalAsync(string key, Action<List<Book>> onBatch, Action<int, int> onProgress, int maxSources = 0)
        {
            var enabled = Sources().FindAll(x => x.Enabled && x.BookSourceType == 0
                                                 && !string.IsNullOrEmpty(x.SearchUrl));
            enabled.Sort((a, b) =>
            {
                int w = b.Weight.CompareTo(a.Weight);
                return w != 0 ? w : string.Compare(a.BookSourceName, b.BookSourceName, StringComparison.Ordinal);
            });
            if (maxSources > 0 && enabled.Count > maxSources) enabled = enabled.GetRange(0, maxSources);

            int total = enabled.Count;
            int done = 0;
            var tasks = new List<Task<List<Book>>>();
            foreach (var src in enabled)
            {
                var s = src;
                tasks.Add(Task.Run(async () =>
                {
                    var r = new List<Book>();
                    try
                    {
                        var work = Engine.SearchAsync(s, key);
                        var w = await Task.WhenAny(work, Task.Delay(25000)).ConfigureAwait(false);
                        if (w == work)
                        {
                            r = await work.ConfigureAwait(false) ?? new List<Book>();
                            foreach (var b in r) b.SourceUrl = s.BookSourceUrl;
                        }
                    }
                    catch { }
                    int d = System.Threading.Interlocked.Increment(ref done);
                    try { onProgress?.Invoke(d, total); } catch { }
                    return r;
                }));
            }

            foreach (var t in tasks)
            {
                List<Book> r = null;
                try { r = await t.ConfigureAwait(false); } catch { }
                if (r != null && r.Count > 0)
                {
                    try { onBatch?.Invoke(r); } catch { }
                }
            }
        }

        /// <summary>书源可用性自检。</summary>
        public static Task<(int count, string msg)> CheckSourceAsync(BookSource src, string key)
            => Engine.CheckSourceAsync(src, key);

        /* ---------------- 书源 ---------------- */

        public static List<BookSource> Sources()
        {
            lock (_io) return _store.LoadSources();
        }

        public static BookSource FindSource(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var list = Sources();
            return list.Find(x => x.BookSourceUrl == url)
                ?? list.Find(x => !string.IsNullOrEmpty(x.BookSourceUrl)
                                  && x.BookSourceUrl.TrimEnd('/') == url.TrimEnd('/'));
        }

        public static void SaveSources(List<BookSource> list)
        {
            lock (_io) _store.SaveSources(list);
        }

        /// <summary>内置书源合集（联网验证过可用的 legado 书源地址；要更新书源时替换此列表即可）。</summary>
        public static readonly string[] BuiltinSourceUrls = new[]
        {
            "https://cdn.jsdelivr.net/gh/XIU2/Yuedu/shuyuan",                              // XIU2 精品书源（22 条，质量高）
            "https://cdn.jsdelivr.net/gh/tickmao/Novel@master/sources/legado/full.json",   // tickmao 全量（303 条，逐条校验可用）
            "https://raw.githubusercontent.com/liufuyou/read/main/sources/yck-novel.json",  // YCK 小说合集（267 条）
            "https://gcore.jsdelivr.net/gh/yuedu521/yiciyuan/1012.json",                   // 一程漫画（208 条，图片源）
        };

        /// <summary>拉取并合并全部内置书源，返回导入/更新的条数（网络失败返回 0）。</summary>
        public static async Task<int> RestoreBuiltinSourcesAsync()
        {
            int total = 0;
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(40);
                    foreach (var u in BuiltinSourceUrls)
                    {
                        try
                        {
                            var text = await http.GetStringAsync(u).ConfigureAwait(false);
                            string err;
                            int n = MergeSources(text, out err);
                            if (n > 0) total += n;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return total;
        }

        /// <summary>
        /// 解析并合并书源文本，返回 (新增+更新数量, 错误信息)。
        /// 兼容：单个对象 / 数组 / {bookSources:[...]} / 带 BOM / JSON 前后有杂字符。
        /// </summary>
        public static int MergeSources(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = L10n.T("内容为空"); return 0; }
            text = text.Trim().Trim('\uFEFF');
            // 去掉 JSONP / 注释包裹
            int lb = text.IndexOfAny(new[] { '[', '{' });
            if (lb < 0) { error = L10n.T("没有找到 JSON 内容（应以 [ 或 { 开头）"); return 0; }

            var arr = new List<BookSource>();
            try
            {
                var tok = JToken.Parse(ExtractJson(text, lb));
                if (tok is JArray)
                {
                    foreach (var t in (JArray)tok) { var s = t.ToObject<BookSource>(); if (s != null) arr.Add(s); }
                }
                else if (tok is JObject)
                {
                    var o = (JObject)tok;
                    JToken inner = o["bookSources"] ?? o["data"] ?? o["sources"] ?? o["items"];
                    if (inner is JArray)
                        foreach (var t in (JArray)inner) { var s = t.ToObject<BookSource>(); if (s != null) arr.Add(s); }
                    else
                    {
                        var s = o.ToObject<BookSource>();
                        if (s != null) arr.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                error = L10n.T("JSON 解析失败：") + ex.Message;
                return 0;
            }

            arr.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.BookSourceUrl));
            if (arr.Count == 0) { error = "没有识别到有效书源（缺少 bookSourceUrl 字段）"; return 0; }

            int n = 0;
            lock (_io)
            {
                var list = _store.LoadSources();
                foreach (var s in arr)
                {
                    if (string.IsNullOrWhiteSpace(s.BookSourceName)) s.BookSourceName = HostOf(s.BookSourceUrl);
                    var exist = list.Find(x => x.BookSourceUrl == s.BookSourceUrl);
                    if (exist != null) list[list.IndexOf(exist)] = s;
                    else list.Add(s);
                    n++;
                }
                _store.SaveSources(list);
            }
            return n;
        }

        /// <summary>从 JSON 起点截取到配对的收尾括号，容忍尾部多余文本。</summary>
        private static string ExtractJson(string s, int start)
        {
            char open = s[start];
            char close = open == '[' ? ']' : '}';
            int depth = 0;
            bool inStr = false, esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return s.Substring(start, i - start + 1);
                }
            }
            return s.Substring(start);
        }

        /* ---------------- 排版偏好 ---------------- */

        public static ReaderPrefs Prefs
        {
            get
            {
                lock (_io)
                {
                    if (_prefs != null) return _prefs;
                    var p = Path.Combine(_ctx.FilesDir.AbsolutePath, "reader", "prefs.json");
                    if (File.Exists(p))
                    {
                        try { _prefs = Newtonsoft.Json.JsonConvert.DeserializeObject<ReaderPrefs>(File.ReadAllText(p)); }
                        catch { }
                    }
                    if (_prefs == null) _prefs = new ReaderPrefs();
                    // 迁移旧版 SharedPreferences 设置
                    try
                    {
                        if (!File.Exists(p))
                        {
                            var sp = _ctx.GetSharedPreferences("Mchat_reader", FileCreationMode.Private);
                            if (sp.Contains("fontSize"))
                            {
                                _prefs.FontSize = sp.GetInt("fontSize", _prefs.FontSize);
                                _prefs.Theme = sp.GetString("theme", _prefs.Theme);
                                _prefs.Justify = sp.GetBoolean("justify", _prefs.Justify);
                                _prefs.LineHeight = sp.GetFloat("lineSpace", (float)_prefs.LineHeight);
                            }
                        }
                    }
                    catch { }
                    Sanitize(_prefs);
                    return _prefs;
                }
            }
        }

        public static void SavePrefs(ReaderPrefs p)
        {
            if (p == null) return;
            Sanitize(p);
            lock (_io)
            {
                _prefs = p;
                try
                {
                    var dir = Path.Combine(_ctx.FilesDir.AbsolutePath, "reader");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "prefs.json"),
                        Newtonsoft.Json.JsonConvert.SerializeObject(p));
                }
                catch { }
            }
        }

        private static void Sanitize(ReaderPrefs p)
        {
            if (p.FontSize < 12) p.FontSize = 12;
            if (p.FontSize > 34) p.FontSize = 34;
            if (p.LineHeight < 1.2) p.LineHeight = 1.2;
            if (p.LineHeight > 2.6) p.LineHeight = 2.6;
            if (p.Margin < 10) p.Margin = 10;
            if (p.Margin > 48) p.Margin = 48;
            if (string.IsNullOrEmpty(p.Theme)) p.Theme = "light";
        }

        /* ---------------- 工具 ---------------- */

        public static string NormalizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return L10n.T("未命名");
            string n = raw.Trim().Trim('\uFEFF');
            int slash = n.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0 && slash < n.Length - 1) n = n.Substring(slash + 1);
            if (n.StartsWith("document/", StringComparison.OrdinalIgnoreCase)) n = n.Substring(9);
            // 某些 provider 返回 primary:Download/xxx.txt
            int colon = n.LastIndexOf(':');
            if (colon >= 0 && colon < n.Length - 1) n = n.Substring(colon + 1);
            slash = n.LastIndexOf('/');
            if (slash >= 0 && slash < n.Length - 1) n = n.Substring(slash + 1);
            // 去掉已知书籍扩展名（长后缀必须排前面）
            string[] exts = { ".fb2.zip", ".fb2.xml", ".markdown", ".epub", ".pdf",
                              ".fb2", ".fbz", ".txt", ".text", ".md", ".zip", ".7z", ".tgz" };
            foreach (var e in exts)
                if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                { n = n.Substring(0, n.Length - e.Length); break; }
            n = n.Trim();
            // 纯数字的名字（document/12345 这类 uri 片段）没法当书名
            if (n.Length == 0) return "未命名";
            bool allDigit = true;
            foreach (var c in n) if (!char.IsDigit(c)) { allDigit = false; break; }
            if (allDigit) return L10n.T("未命名书籍");
            return n;
        }

        public static string HostOf(string url)
        {
            try { return string.IsNullOrEmpty(url) ? "" : new Uri(url).Host; }
            catch { return ""; }
        }
    }
}

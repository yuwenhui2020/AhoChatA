// ============================================================
// 阅读模块 · PDF 第一页封面缓存 + 页图磁盘缓存
//
// 设计目标（用户明确要求）：
//   「如果导入 pdf 的话，封面使用 pdf 的第一页！并且提前缓存好第一页，
//     不要每次启动阅读功能后傻傻的读取每个 pdf 的第一页」
//
// 因此：
//   · 导入瞬间一次性渲染第 0 页 → 同时产出「书架封面」和「阅读用第一页」；
//   · 之后书架只 DecodeFile 这张 JPEG，永不打开 PDF；
//   · 每次翻页渲染出的页图也按 书id + 页码 + 宽度档 + 缩放档 落盘，
//     回来重读/重启 App 直接命中磁盘，不重复渲染；
//   · 老数据（升级前已在书架但没封面的 PDF）由 BackfillCovers 一次性补齐，
//     用 marker 文件保证整个生命周期只跑一次，避免每次启动都遍历 PDF。
//
// 缓存分桶思路转译自 Areada ui/reader/PdfReaderState.kt：
//   pdfRenderScaleForZoom 把连续缩放量化成 1 / 1.5 / 2 / 3 四档，
//   用整数 bucket 作为缓存 key，避免浮点 scale 造成缓存碎片。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
// Android.Graphics.Path 与 System.IO.Path 同名，这里明确取文件系统 Path
using Path = System.IO.Path;

namespace AhoChatA.Reader
{
    public static class PdfCache
    {
        private const int CoverWidth = 480;
        private const int CoverQuality = 82;
        private const int PageQuality = 88;
        private const string BackfillMarker = ".backfill_v1";

        // 内存优化配置
        private const int MaxPageCacheCount = 200;      // 最多缓存 200 页图
        private const long MaxCacheSizeBytes = 100 * 1024 * 1024; // 100MB 磁盘缓存上限
        private const int MaxConcurrentRenders = 2;     // 最大并发渲染数
        private const long CacheTtlMs = 30L * 24 * 60 * 60 * 1000; // 30 天缓存 TTL

        private static Context _ctx;
        private static readonly object _io = new object();

        public static void Init(Context ctx)
        {
            if (_ctx != null) return;
            lock (_io) { if (_ctx == null) _ctx = ctx.ApplicationContext ?? ctx; }
        }

        private static string Root
        {
            get
            {
                var d = Path.Combine(_ctx.FilesDir.AbsolutePath, "reader");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string CoverDir
        {
            get
            {
                var d = Path.Combine(Root, "covers");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string PageRoot
        {
            get
            {
                var d = Path.Combine(Root, "pdfpages");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string CoverPath(string id) => Path.Combine(CoverDir, Safe(id) + ".jpg");
        public static bool HasCover(string id) => File.Exists(CoverPath(id));

        /// <summary>网页端 / 书架用的封面 URL 后缀判断；无封面时前端退回渐变色。</summary>
        public static bool CoverAvailable(string id) => HasCover(id);

        private static string Safe(string id)
        {
            if (string.IsNullOrEmpty(id)) return "none";
            var sb = new System.Text.StringBuilder();
            foreach (var c in id)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        /// <summary>宽度按 120px 分桶，避免屏幕宽度几像素抖动造成缓存 miss。</summary>
        private static int WidthBucket(int w)
        {
            if (w < 240) w = 240;
            return ((w + 119) / 120) * 120;
        }

        /// <summary>Areada pdfRenderScaleForZoom：连续缩放 → 离散渲染档。</summary>
        public static int ScaleBucketForZoom(float zoom)
        {
            if (zoom <= 1.15f) return 100;
            if (zoom <= 1.75f) return 150;
            if (zoom <= 2.5f) return 200;
            return 300;
        }

        public static float ScaleForBucket(int bucket) => Math.Max(1f, Math.Min(3f, bucket / 100f));

        private static string PagePath(string id, int page, int width, int bucket)
            => Path.Combine(PageDir(id), "w" + WidthBucket(width) + "_b" + bucket + "_p" + page + ".jpg");

        private static string PageDir(string id)
        {
            var d = Path.Combine(PageRoot, Safe(id));
            Directory.CreateDirectory(d);
            return d;
        }

        private static bool HasPage(string id, int page, int width, int bucket)
            => File.Exists(PagePath(id, page, width, bucket));

        /* ------------------------------------------------------------
         * 导入时调用：一次性把「第一页」同时做成封面 + 阅读页缓存
         * ------------------------------------------------------------ */

        /// <summary>
        /// 导入 PDF 时预先缓存第 0 页。只渲染一次高分辨率位图，
        /// 封面是它的缩小版，阅读页直接复用，避免重复开 PDF。
        /// screenWidth 传设备屏幕宽度（像素）。
        /// </summary>
        public static void EnsureFirstPage(string id, string pdfPath, int screenWidth)
        {
            if (_ctx == null || string.IsNullOrEmpty(id)) return;
            try
            {
                bool needCover = !HasCover(id);
                int bucket = 100;
                bool needPage = !HasPage(id, 0, screenWidth, bucket);
                if (!needCover && !needPage) return;

                lock (_io)
                {
                    using (var engine = new PdfEngine(pdfPath))
                    {
                        if (engine.PageCount <= 0) return;
                        // 只渲染一次：取封面与阅读页所需的最大宽度
                        int renderWidth = Math.Max(CoverWidth, WidthBucket(screenWidth));
                        using (var full = engine.RenderPage(0, renderWidth, 1f))
                        {
                            if (needCover)
                            {
                                int cw = Math.Min(CoverWidth, full.Width);
                                int ch = Math.Max(1, (int)Math.Round(full.Height * (cw / (double)full.Width)));
                                using (var small = Bitmap.CreateScaledBitmap(full, cw, ch, true))
                                    WriteJpegAtomically(CoverPath(id), small, CoverQuality);
                            }
                            if (needPage)
                                WriteJpegAtomically(PagePath(id, 0, screenWidth, bucket), full, PageQuality);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MchatPdf", "第一页预缓存失败: " + ex.Message);
            }
        }

        private static void WriteJpegAtomically(string dest, Bitmap bmp, int quality)
        {
            string tmp = dest + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    if (!bmp.Compress(Bitmap.CompressFormat.Jpeg, quality, fs))
                        throw new IOException(L10n.T("JPEG 编码失败"));
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        /* ------------------------------------------------------------
         * 阅读 / 网页端：按页取 JPEG，命中缓存就不再打开 PDF
         * ------------------------------------------------------------ */

        /// <summary>返回某页的 JPEG；缓存未命中才渲染并落盘。支持 LRU 淘汰和内存压力处理。</summary>
        public static byte[] PageJpeg(string id, string pdfPath, int page, int width, float zoom = 1f)
        {
            int bucket = ScaleBucketForZoom(zoom);
            string file = PagePath(id, page, width, bucket);
            if (File.Exists(file))
            {
                try { return File.ReadAllBytes(file); }
                catch { /* 缓存损坏则重渲染 */ }
            }
            lock (_io)
            {
                if (File.Exists(file))
                {
                    try { return File.ReadAllBytes(file); } catch { }
                }
                
                // LRU 淘汰：检查缓存大小和文件数量
                TryEvictOldCache();
                
                using (var engine = new PdfEngine(pdfPath))
                {
                    if (page < 0 || page >= engine.PageCount)
                        throw new ArgumentOutOfRangeException(nameof(page));
                    
                    // 并发控制：限制并发渲染数量
                    while (ActiveRenderCount >= MaxConcurrentRenders)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                    ActiveRenderCount++;
                    
                    try
                    {
                        using (var bmp = engine.RenderPage(page, WidthBucket(width), ScaleForBucket(bucket)))
                        {
                            using (var ms = new MemoryStream())
                            {
                                if (!bmp.Compress(Bitmap.CompressFormat.Jpeg, PageQuality, ms))
                                    throw new IOException(L10n.T("PDF 页 JPEG 编码失败"));
                                byte[] data = ms.ToArray();
                                try
                                {
                                    string tmp = file + ".tmp";
                                    File.WriteAllBytes(tmp, data);
                                    if (File.Exists(file)) File.Delete(file);
                                    File.Move(tmp, file);
                                }
                                catch { }
                                return data;
                            }
                        }
                    }
                    finally
                    {
                        ActiveRenderCount--;
                    }
                }
            }
        }
        
        private static int ActiveRenderCount = 0;
        
        /// <summary>LRU 淘汰：清理过期和超量缓存</summary>
        private static void TryEvictOldCache()
        {
            try
            {
                var pageDir = PageRoot;
                if (!Directory.Exists(pageDir)) return;
                
                var files = Directory.GetFiles(pageDir, "*.jpg", SearchOption.AllDirectories);
                long totalSize = 0;
                var fileInfos = new List<FileInfo>();
                
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    totalSize += fi.Length;
                    fileInfos.Add(fi);
                }
                
                // 按最后访问时间排序
                fileInfos.Sort((a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                
                // 1. 清理过期文件 (> 30 天)
                var cutoff = DateTime.UtcNow - TimeSpan.FromMilliseconds(CacheTtlMs);
                foreach (var fi in fileInfos)
                {
                    if (fi.LastAccessTimeUtc < cutoff)
                    {
                        try { fi.Delete(); } catch { }
                    }
                }
                
                // 2. 清理超出文件数量限制
                files = Directory.GetFiles(pageDir, "*.jpg", SearchOption.AllDirectories);
                if (files.Length > MaxPageCacheCount)
                {
                    fileInfos = files.Select(f => new FileInfo(f)).OrderBy(fi => fi.LastAccessTimeUtc).ToList();
                    int toRemove = files.Length - MaxPageCacheCount;
                    for (int i = 0; i < toRemove; i++)
                    {
                        try { fileInfos[i].Delete(); } catch { }
                    }
                }
                
                // 3. 清理超出总大小限制
                files = Directory.GetFiles(pageDir, "*.jpg", SearchOption.AllDirectories);
                totalSize = files.Sum(f => new FileInfo(f).Length);
                if (totalSize > MaxCacheSizeBytes)
                {
                    var ordered = files.Select(f => new FileInfo(f)).OrderBy(fi => fi.LastAccessTimeUtc).ToList();
                    long freed = 0;
                    foreach (var fi in ordered)
                    {
                        if (totalSize - freed <= MaxCacheSizeBytes) break;
                        try { freed += fi.Length; fi.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>翻页时后台预渲染相邻页，写进磁盘缓存（Areada 的预渲染前后各一页）。</summary>
        public static void PrefetchAsync(string id, string pdfPath, int page, int width)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pdfPath)) return;
            Task.Run(() =>
            {
                try
                {
                    foreach (int p in new[] { page - 1, page + 1 })
                        if (p >= 0 && !HasPage(id, p, width, 100)) PageJpeg(id, pdfPath, p, width, 1f);
                }
                catch { }
            });
        }

        public static byte[] CoverJpeg(string id)
        {
            string p = CoverPath(id);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        /// <summary>EPUB / FB2 自带的封面图直接落盘共用同一份封面缓存。</summary>
        public static void SaveCover(string id, byte[] bytes)
        {
            if (_ctx == null || string.IsNullOrEmpty(id) || bytes == null || bytes.Length == 0) return;
            try
            {
                string tmp = CoverPath(id) + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(CoverPath(id))) File.Delete(CoverPath(id));
                File.Move(tmp, CoverPath(id));
            }
            catch { }
        }

        /// <summary>按文件头判断封面 MIME（EPUB 内嵌封面可能是 PNG）。</summary>
        public static string CoverMime(byte[] data)
        {
            if (data != null && data.Length > 4 && data[0] == 0x89 && data[1] == 0x50
                && data[2] == 0x4E && data[3] == 0x47) return "image/png";
            if (data != null && data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return "image/jpeg";
            if (data != null && data.Length > 4 && data[0] == 0x47 && data[1] == 0x49) return "image/gif";
            return "image/jpeg";
        }

        /// <summary>删除书籍时清理它所有的封面 / 页图缓存。</summary>
        public static void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                string c = CoverPath(id);
                if (File.Exists(c)) File.Delete(c);
                string d = Path.Combine(PageRoot, Safe(id));
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
            catch { }
        }

        /* ------------------------------------------------------------
         * 旧数据一次性补齐（升级前导入的 PDF 没有封面）
         * ------------------------------------------------------------ */

        /// <summary>
        /// 整个 App 生命周期只补一次：marker 存在就直接返回，
        /// 绝不在每次启动阅读功能时遍历书架里的每个 PDF。
        /// 返回新生成封面的书籍 id 列表，供调用方刷新书架。
        /// </summary>
        public static List<string> BackfillCovers(List<ShelfItem> shelf, int screenWidth)
        {
            var done = new List<string>();
            if (_ctx == null || shelf == null) return done;
            string marker = Path.Combine(CoverDir, BackfillMarker);
            if (File.Exists(marker)) return done;
            try
            {
                foreach (var it in shelf)
                {
                    if (it == null || !IsPdfPath(it.Path) || HasCover(it.Id)) continue;
                    if (!File.Exists(it.Path)) continue;
                    EnsureFirstPage(it.Id, it.Path, screenWidth);
                    if (HasCover(it.Id)) done.Add(it.Id);
                }
                File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
            return done;
        }

        public static bool IsPdfPath(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }
}

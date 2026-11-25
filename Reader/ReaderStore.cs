// ============================================================
// 阅读模块 · 本地持久化（书架 / 书源 / 书签 / 在线章节缓存）
//
// 全部存放于 App 私有目录（ctx.FilesDir/reader），无需任何权限。
// 章节正文不入书架库：本地书按需读文件，在线书按 bookId 缓存到磁盘。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Newtonsoft.Json;

namespace AhoChatA.Reader
{
    public class ReaderStore
    {
        private readonly string _dir;
        public ReaderStore(Context ctx)
        {
            _dir = Path.Combine(ctx.FilesDir.AbsolutePath, "reader");
            try { Directory.CreateDirectory(_dir); } catch { }
        }

        private string File(string name) => Path.Combine(_dir, name);

        // ---------- 书架 ----------
        public List<ShelfItem> LoadShelf()
        {
            var p = File("shelf.json");
            if (!System.IO.File.Exists(p)) return new List<ShelfItem>();
            try { return JsonConvert.DeserializeObject<List<ShelfItem>>(System.IO.File.ReadAllText(p)) ?? new List<ShelfItem>(); }
            catch { return new List<ShelfItem>(); }
        }
        public void SaveShelf(List<ShelfItem> list)
        {
            try { System.IO.File.WriteAllText(File("shelf.json"), JsonConvert.SerializeObject(list)); } catch { }
        }

        // ---------- 书源 ----------
        public List<BookSource> LoadSources()
        {
            var p = File("sources.json");
            if (!System.IO.File.Exists(p)) return new List<BookSource>();
            try { return JsonConvert.DeserializeObject<List<BookSource>>(System.IO.File.ReadAllText(p)) ?? new List<BookSource>(); }
            catch { return new List<BookSource>(); }
        }
        public void SaveSources(List<BookSource> list)
        {
            try { System.IO.File.WriteAllText(File("sources.json"), JsonConvert.SerializeObject(list)); } catch { }
        }

        // ---------- 本地扫描目录 ----------
        /// <summary>尚未保存过扫描目录时返回 null（用于区分「从未配置」与「用户清空」）。</summary>
        public List<string> LoadScanDirs()
        {
            var p = File("scandirs.json");
            if (!System.IO.File.Exists(p)) return null;
            try { return JsonConvert.DeserializeObject<List<string>>(System.IO.File.ReadAllText(p)) ?? new List<string>(); }
            catch { return new List<string>(); }
        }
        public void SaveScanDirs(List<string> list)
        {
            try { System.IO.File.WriteAllText(File("scandirs.json"), JsonConvert.SerializeObject(list)); } catch { }
        }

        // ---------- 书签 ----------
        public List<Bookmark> LoadBookmarks(string bookId)
        {
            var p = File("bm_" + Safe(bookId) + ".json");
            if (!System.IO.File.Exists(p)) return new List<Bookmark>();
            try { return JsonConvert.DeserializeObject<List<Bookmark>>(System.IO.File.ReadAllText(p)) ?? new List<Bookmark>(); }
            catch { return new List<Bookmark>(); }
        }
        public void SaveBookmarks(string bookId, List<Bookmark> list)
        {
            try { System.IO.File.WriteAllText(File("bm_" + Safe(bookId) + ".json"), JsonConvert.SerializeObject(list)); } catch { }
        }

        // ---------- 在线章节缓存 ----------
        public void SaveChapter(string bookId, int idx, string html)
        {
            try { System.IO.File.WriteAllText(File("ch_" + Safe(bookId) + "_" + idx + ".html"), html ?? ""); } catch { }
        }
        public string LoadChapter(string bookId, int idx)
        {
            var p = File("ch_" + Safe(bookId) + "_" + idx + ".html");
            if (!System.IO.File.Exists(p)) return null;
            try { return System.IO.File.ReadAllText(p); } catch { return null; }
        }

        /// <summary>这一章是否已在磁盘缓存里（只查存在性，不读内容，预缓存时用来跳过）。</summary>
        public bool HasChapter(string bookId, int idx)
        {
            try { return System.IO.File.Exists(File("ch_" + Safe(bookId) + "_" + idx + ".html")); }
            catch { return false; }
        }

        // ---------- 在线书目录缓存 ----------
        // 目录是每次打开书都要用的东西，之前只存在内存里，进程重启后要重新联网拉一遍，
        // 打开书就得多等一次网络往返。这里落一份到磁盘，冷启动也能秒开。
        public void SaveToc(string bookId, string json)
        {
            try { System.IO.File.WriteAllText(File("toc_" + Safe(bookId) + ".json"), json ?? ""); } catch { }
        }

        public string LoadToc(string bookId)
        {
            var p = File("toc_" + Safe(bookId) + ".json");
            if (!System.IO.File.Exists(p)) return null;
            try { return System.IO.File.ReadAllText(p); } catch { return null; }
        }

        /// <summary>清掉一本书的全部正文缓存。</summary>
        public void ClearChapterCache(string bookId)
        {
            try
            {
                var prefix = "ch_" + Safe(bookId) + "_";
                foreach (var f in Directory.GetFiles(_dir, prefix + "*.html"))
                    try { System.IO.File.Delete(f); } catch { }
                var toc = File("toc_" + Safe(bookId) + ".json");
                if (System.IO.File.Exists(toc)) try { System.IO.File.Delete(toc); } catch { }
            }
            catch { }
        }

        /// <summary>已缓存章节数（用于「缓存全部」进度显示）。</summary>
        public int CountCached(string bookId)
        {
            try { return Directory.GetFiles(_dir, "ch_" + Safe(bookId) + "_*.html").Length; }
            catch { return 0; }
        }

        private static string Safe(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in s ?? "")
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
                else sb.Append('_');
            return sb.ToString();
        }
    }
}

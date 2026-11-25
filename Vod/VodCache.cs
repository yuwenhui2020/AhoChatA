// ============================================================
// 在线影视 · 本地缓存与观看历史
//
// 全部落在 App 私有 files/vod/ 下（不用 CacheDir，避免被系统「清理缓存」抹掉）。
// home.json      多源聚合后的列表（后台分页预抓，App 冷启动 / 本地搜索直接读它）
// d_{md5}.json   详情播放串
// r_{md5}.json   播放页 → 直链
// cats.json      分类目录（同名合并 + 各站 type_id 映射）
// history.json   观看记录（含整条线路，用于续播，最多 40 条）
//
// 所有视频缓存（首页 / 详情 / 直链 / 海报 / 落盘分片）统一 3 天过期，
// 与是否开启「一起看」无关：一起看只是让跟随端改走宿主机代理，
// 因此复用宿主机这同一份缓存，不额外产生另一套过期策略。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using IoPath = System.IO.Path;

namespace AhoChatA.Vod
{
    public static class VodCache
    {
        private static readonly object _io = new object();
        /// <summary>所有视频缓存统一 3 天；与 VideoCacheProxy.CacheExpiryDays 保持一致。</summary>
        public const long CacheTtlMs = 3L * 24 * 3600 * 1000;
        private const long HomeTtlMs = CacheTtlMs;
        private const long DetailTtlMs = CacheTtlMs;
        private const long ResolvedTtlMs = CacheTtlMs;
        private const int HistoryLimit = 40;

        public class HomeBundle
        {
            public long Ts;
            public string Sig = "";
            public int Page;
            public List<VodItem> List = new List<VodItem>();
            public Dictionary<string, int> Pages = new Dictionary<string, int>();
        }
        public class DetailBundle
        {
            public long Ts;
            public string PlayFrom = "";
            public string PlayUrl = "";
            public string Desc = "";
            public string Pic = "";
            public string Actor = "";
        }
        public class ResolvedBundle
        {
            public long Ts;
            public string Real = "";
        }
        public class CatBundle
        {
            public long Ts;
            public string Sig = "";
            public List<VodCat> List = new List<VodCat>();
        }
        public class HistoryBundle
        {
            public List<VodWatchRecord> List = new List<VodWatchRecord>();
        }

        private static string Dir
        {
            get
            {
                var p = IoPath.Combine(VodConfig.CacheRoot(), "cache");
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                return p;
            }
        }

        private static long Now { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; } }

        private static string Hash(string s)
        {
            try
            {
                using (var m = MD5.Create())
                {
                    var b = m.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                    var sb = new StringBuilder();
                    foreach (var x in b) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return Math.Abs((s ?? "").GetHashCode()).ToString("x8"); }
        }

        private static T Read<T>(string file) where T : class
        {
            try { return File.Exists(file) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(file)) : null; }
            catch { return null; }
        }

        private static void Write<T>(string file, T obj)
        {
            try
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(obj));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception ex) { Android.Util.Log.Warn("MchatVod", "缓存写入失败: " + ex.Message); }
        }

        /* ---------------- 首页聚合 ---------------- */

        public static HomeBundle LoadHome(string sig)
        {
            lock (_io)
            {
                var b = Read<HomeBundle>(IoPath.Combine(Dir, "home.json"));
                if (b == null || b.List == null || b.Sig != sig) return null;
                if (Now - b.Ts > HomeTtlMs) return null;
                return b;
            }
        }

        public static void SaveHome(string sig, int page, List<VodItem> list, Dictionary<string, int> pages)
        {
            lock (_io)
            {
                Write(IoPath.Combine(Dir, "home.json"), new HomeBundle
                {
                    Ts = Now, Sig = sig, Page = page,
                    List = list ?? new List<VodItem>(),
                    Pages = pages ?? new Dictionary<string, int>()
                });
            }
        }

        /// <summary>把新抓到的一页追加进首页缓存，按 siteKey|id 去重。</summary>
        public static List<VodItem> AppendHome(string sig, int page, List<VodItem> fresh, string cat)
        {
            lock (_io)
            {
                var b = Read<HomeBundle>(IoPath.Combine(Dir, "home.json"));
                if (b == null || b.Sig != sig || b.List == null)
                    b = new HomeBundle { Sig = sig, List = new List<VodItem>(), Pages = new Dictionary<string, int>() };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in b.List) seen.Add(it.Key);
                foreach (var it in fresh ?? new List<VodItem>())
                    if (seen.Add(it.Key)) b.List.Add(it);
                if (b.List.Count > 1200) b.List = b.List.GetRange(0, 1200);
                if (page > b.Page) b.Page = page;
                var key = string.IsNullOrEmpty(cat) ? "" : cat;
                if (!b.Pages.ContainsKey(key) || b.Pages[key] < page) b.Pages[key] = page;
                b.Ts = Now;
                Write(IoPath.Combine(Dir, "home.json"), b);
                return b.List;
            }
        }

        public static int PageOf(string sig, string cat)
        {
            lock (_io)
            {
                var b = Read<HomeBundle>(IoPath.Combine(Dir, "home.json"));
                if (b == null || b.Sig != sig || b.Pages == null) return 0;
                int p;
                return b.Pages.TryGetValue(cat ?? "", out p) ? p : 0;
            }
        }

        /* ---------------- 详情 ---------------- */

        public static DetailBundle LoadDetail(string siteKey, string id)
        {
            lock (_io)
            {
                var b = Read<DetailBundle>(IoPath.Combine(Dir, "d_" + Hash(siteKey + "|" + id) + ".json"));
                if (b == null || Now - b.Ts > DetailTtlMs) return null;
                return b;
            }
        }

        public static void SaveDetail(string siteKey, string id, DetailBundle b)
        {
            if (b == null) return;
            lock (_io) { b.Ts = Now; Write(IoPath.Combine(Dir, "d_" + Hash(siteKey + "|" + id) + ".json"), b); }
        }

        /* ---------------- 播放页 → 直链 ---------------- */

        public static string LoadResolved(string pageUrl)
        {
            lock (_io)
            {
                var b = Read<ResolvedBundle>(IoPath.Combine(Dir, "r_" + Hash(pageUrl) + ".json"));
                if (b == null || Now - b.Ts > ResolvedTtlMs) return null;
                return b.Real;
            }
        }

        public static void SaveResolved(string pageUrl, string real)
        {
            if (string.IsNullOrWhiteSpace(pageUrl) || string.IsNullOrWhiteSpace(real)) return;
            lock (_io) { Write(IoPath.Combine(Dir, "r_" + Hash(pageUrl) + ".json"), new ResolvedBundle { Ts = Now, Real = real }); }
        }

        /* ---------------- 分类 ---------------- */

        public static List<VodCat> LoadCats(string sig)
        {
            lock (_io)
            {
                var b = Read<CatBundle>(IoPath.Combine(Dir, "cats.json"));
                if (b == null || b.List == null || b.Sig != sig || Now - b.Ts > HomeTtlMs) return null;
                return b.List;
            }
        }

        public static void SaveCats(string sig, List<VodCat> cats)
        {
            lock (_io) { Write(IoPath.Combine(Dir, "cats.json"), new CatBundle { Ts = Now, Sig = sig, List = cats }); }
        }

        /* ---------------- 观看历史 ---------------- */

        private static string HistFile { get { return IoPath.Combine(VodConfig.Root(), "history.json"); } }

        public static List<VodWatchRecord> History()
        {
            lock (_io)
            {
                var b = Read<HistoryBundle>(HistFile);
                return b == null || b.List == null ? new List<VodWatchRecord>() : b.List;
            }
        }

        public static void AddHistory(VodWatchRecord r)
        {
            if (r == null || string.IsNullOrEmpty(r.Key)) return;
            lock (_io)
            {
                var b = Read<HistoryBundle>(HistFile) ?? new HistoryBundle();
                if (b.List == null) b.List = new List<VodWatchRecord>();
                b.List.RemoveAll(x => x.Key == r.Key);
                r.LastTs = Now;
                b.List.Insert(0, r);
                if (b.List.Count > HistoryLimit) b.List = b.List.GetRange(0, HistoryLimit);
                Write(HistFile, b);
            }
        }

        public static void ClearHistory()
        {
            lock (_io) { try { if (File.Exists(HistFile)) TrashManager.Instance.MoveToTrash(HistFile); } catch { } }
        }

        public static void ClearAll()
        {
            lock (_io)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Dir, "*")) File.Delete(f);
                    var posters = IoPath.Combine(VodConfig.CacheRoot(), "posters");
                    if (Directory.Exists(posters)) foreach (var f in Directory.GetFiles(posters, "*")) File.Delete(f);
                }
                catch { }
                // 观看历史刻意保留：清缓存不应该抹掉追剧进度
            }
        }
    }
}

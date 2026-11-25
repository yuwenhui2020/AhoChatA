// ============================================================
// 在线影视 · 多渠道源配置
//
// 渠道类型：
//   1) 内置实测源（不依赖任何订阅，冷启动秒开、订阅挂了自动兜底）
//   2) TVBox 标准订阅 JSON：{ "sites": [...] }
//   3) 多仓列表：{ "urls": [ {"url": "...", "name": "..."} ] } —— 递归展开
//   4) 用户粘贴的 JSON / 手工添加的站点
// 只接受 type 0/1 且 api 以 http 开头的 CMS JSON 站；type 3 是 csp_ 爬虫 jar，跳过。
// 订阅内容落盘到 files/vod/subs/，离线可用；刷新走后台线程，不阻塞首屏。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Android.App;
using Android.OS;
using IoPath = System.IO.Path;

namespace AhoChatA.Vod
{
    public static class VodConfig
    {
        private static readonly object _lock = new object();
        private static VodConfigDoc _cfg;
        private static List<VodSite> _sites;
        private static string _signature = "";

        /// <summary>实测可用的内置采集站（2026-11-10 用关键词「兰香如故」逐个联网验证：能搜到结果的保留，
        /// 其余删除——suoni / niuniu / tyys 直接返回「暂不支持搜索」，yhzy 返回 403。均为标准 MacCMS V10 JSON，
        /// 同品牌镜像已去重仅留主源）。搜索 = 各站 wd= 结果（过滤忽略 wd 的站）+ 本地预抓缓存命中。</summary>
        public const string BuiltInJson = @"{
  ""name"":""内置实测影视源"",
  ""sites"":[
    {""key"":""lzi"",""name"":""量子资源"",""type"":1,""api"":""http://cj.lziapi.com/api.php/provide/vod/""},
    {""key"":""ffzy"",""name"":""非凡资源"",""type"":1,""api"":""http://cj.ffzyapi.com/api.php/provide/vod/""},
    {""key"":""gszy"",""name"":""光速资源"",""type"":1,""api"":""https://api.guangsuapi.com/api.php/provide/vod/""},
    {""key"":""hnzy"",""name"":""红牛资源"",""type"":1,""api"":""https://www.hongniuzy2.com/api.php/provide/vod/""},
    {""key"":""jszy"",""name"":""极速资源"",""type"":1,""api"":""https://jszyapi.com/api.php/provide/vod/""},
    {""key"":""wjzy"",""name"":""无尽资源"",""type"":1,""api"":""https://api.wujinapi.com/api.php/provide/vod/""},
    {""key"":""modu"",""name"":""魔都资源"",""type"":1,""api"":""https://caiji.moduapi.cc/api.php/provide/vod/""},
    {""key"":""dytt"",""name"":""电影天堂"",""type"":1,""api"":""http://caiji.dyttzyapi.com/api.php/provide/vod/""},
    {""key"":""zuid"",""name"":""最大资源"",""type"":1,""api"":""https://api.zuidapi.com/api.php/provide/vod/""},
    {""key"":""bfzy"",""name"":""暴风资源"",""type"":1,""api"":""https://bfzyapi.com/api.php/provide/vod/""},
    {""key"":""hhzy"",""name"":""豪华资源"",""type"":1,""api"":""https://hhzyapi.com/api.php/provide/vod/""},
    {""key"":""bdzy"",""name"":""百度资源"",""type"":1,""api"":""https://api.apibdzy.com/api.php/provide/vod/""},
    {""key"":""360zy"",""name"":""360资源"",""type"":1,""api"":""https://360zy.com/api.php/provide/vod/""},
    {""key"":""huya"",""name"":""虎牙资源"",""type"":1,""api"":""https://www.huyaapi.com/api.php/provide/vod/""},
    {""key"":""iqiyi"",""name"":""爱奇艺资源"",""type"":1,""api"":""https://iqiyizyapi.com/api.php/provide/vod/""},
    {""key"":""subo"",""name"":""速播资源"",""type"":1,""api"":""https://subocj.com/api.php/provide/vod/""},
    {""key"":""p2100"",""name"":""飘零影院"",""type"":1,""api"":""https://p2100.net/api.php/provide/vod/""}
  ]}";

        public static string Root()
        {
            var p = IoPath.Combine(Application.Context.FilesDir.AbsolutePath, "vod");
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }

        /// <summary>系统可清理的「持久缓存」区（CacheDir）：在线影视索引、详情、解析、分类与封面海报。
        /// 同 FilesDir 的区别——系统「设置 → 存储 → 清理缓存」会清掉这里，但应用不关闭时一直保留，
        /// 离线也能看首页/详情/封面。观看历史等需要长期保留的数据仍放在 Root()。</summary>
        public static string CacheRoot()
        {
            var p = IoPath.Combine(Application.Context.CacheDir.AbsolutePath, "vod");
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }

        /// <summary>外部共享存储（SD 卡 / 模拟 SD）上的「下载」根：在系统媒体目录 Music / Movies /
        /// Documents 下再建一个 Mchat 子目录。这样下载的歌曲 / 视频 / 小说在文件管理器里可见，
        /// 也能被系统媒体库扫描到。使用 GetExternalFilesDir，无需 WRITE_EXTERNAL_STORAGE 权限，
        /// Android 全版本可用——这是「下载到 SD 卡、文件找得到」的关键。</summary>
        public static string ExternalDir(string type, string sub = "Mchat")
        {
            string baseDir;
            try
            {
                var d = Application.Context.GetExternalFilesDir(type);
                baseDir = d != null ? d.AbsolutePath : Application.Context.FilesDir.AbsolutePath;
            }
            catch { baseDir = Application.Context.FilesDir.AbsolutePath; }
            var p = IoPath.Combine(baseDir, sub);
            try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); } catch { }
            return p;
        }

        /// <summary>外部缓存区（视频 ts 切片临时目录）：合并完成后会被清掉。</summary>
        public static string ExternalTmpRoot()
        {
            string baseDir;
            try
            {
                var d = Application.Context.ExternalCacheDir;
                baseDir = d != null ? d.AbsolutePath : Application.Context.CacheDir.AbsolutePath;
            }
            catch { baseDir = Application.Context.CacheDir.AbsolutePath; }
            var p = IoPath.Combine(baseDir, "vod_tmp");
            try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); } catch { }
            return p;
        }

        /// <summary>非持久的视频缓冲缓存区（CacheDir/vod_buf）：每次进程启动首次使用时清空，
        /// 关闭程序即清除——用于 ExoPlayer 的 SimpleCache 缓冲与「下一集预加载」。</summary>
        public static string BufferRoot()
        {
            var p = IoPath.Combine(Application.Context.CacheDir.AbsolutePath, "vod_buf");
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }

        private static bool _bufferWiped;
        /// <summary>跨进程启动只清一次：保证视频缓冲缓存「关闭程序后清除」。</summary>
        public static void EnsureBufferCleared()
        {
            try
            {
                if (_bufferWiped) return;
                _bufferWiped = true;
                var p = BufferRoot();
                if (Directory.Exists(p))
                {
                    foreach (var f in Directory.GetFiles(p)) try { File.Delete(f); } catch { }
                    foreach (var d in Directory.GetDirectories(p)) try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }

        /// <summary>持久的视频缓冲缓存区（CacheDir/vod_disk）：本地缓存代理落盘处，
        /// 与 vod_buf 不同——它 3 天过期（不是每次启动清空），用于「暂停也缓冲 / 离线续看」。</summary>
        public static string DiskCacheRoot()
        {
            var p = IoPath.Combine(Application.Context.CacheDir.AbsolutePath, "vod_disk");
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }

        private static readonly object _sweepLock = new object();
        private static long _lastSweepMs;

        /// <summary>按统一的 3 天周期清理所有视频缓存：落盘分片、首页/详情/直链 JSON、海报。
        /// 一律按最后写入时间判断，与是否开启「一起看」无关；10 分钟节流，可在播放期反复调用。</summary>
        public static void SweepDiskCache()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_sweepLock)
            {
                if (_lastSweepMs != 0 && now - _lastSweepMs < 10L * 60 * 1000) return;
                _lastSweepMs = now;
            }
            try
            {
                var cut = DateTime.UtcNow.AddDays(-VideoCacheProxy.CacheExpiryDays);
                var roots = new System.Collections.Generic.List<string>();
                try { roots.Add(DiskCacheRoot()); } catch { }
                try { roots.Add(IoPath.Combine(CacheRoot(), "cache")); } catch { }
                try { roots.Add(IoPath.Combine(CacheRoot(), "posters")); } catch { }
                try { roots.Add(IoPath.Combine(CacheRoot(), "seg")); } catch { }
                foreach (var p in roots)
                {
                    if (!Directory.Exists(p)) continue;
                    foreach (var f in Directory.GetFiles(p))
                    {
                        try { if (System.IO.File.GetLastWriteTimeUtc(f) < cut) System.IO.File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        private static string ConfigFile { get { return IoPath.Combine(Root(), "onlinevod.json"); } }
        private static string SubDir
        {
            get
            {
                var p = IoPath.Combine(Root(), "subs");
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                return p;
            }
        }

        private static string Md5(string s)
        {
            try
            {
                using (var md5 = MD5.Create())
                {
                    var b = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                    var sb = new StringBuilder();
                    foreach (var x in b) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return Math.Abs((s ?? "").GetHashCode()).ToString("x8"); }
        }

        /* ---------------- 自带 CMS（影视CMS / 海洋CMS）双服务 ---------------- */

        /// <summary>对外视频 API 路径（与 MchatTV-CMS 的 CmsPublicServer.ApiPath 完全一致）。</summary>
        public const string CmsApiPath = "/api.php/provide/vod/";
        /// <summary>默认两套 CMS：影视CMS 监听 7777、海洋CMS 监听 6666（同机运行时 127.0.0.1 即可）。</summary>
        public const string DefaultCmsHost = "127.0.0.1:7777";
        public const string DefaultCmsHost2 = "127.0.0.1:6666";

        /// <summary>把 host:port 拼成完整 CMS API 地址。</summary>
        public static string CmsApiFor(string host)
        {
            var h = (host ?? "").Trim();
            if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) h = h.Substring(7);
            else if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = h.Substring(8);
            h = h.TrimEnd('/');
            int p = h.IndexOf('/'); if (p > 0) h = h.Substring(0, p);
            return "http://" + (h.Length == 0 ? DefaultCmsHost : h) + CmsApiPath;
        }

        /// <summary>判断某 api 是否指向本程序自带 CMS（用于取数时走 CMS 分支）。</summary>
        public static bool IsCmsApi(string api)
        {
            if (string.IsNullOrEmpty(api)) return false;
            int q = api.IndexOf('?'); if (q >= 0) api = api.Substring(0, q);
            return api.IndexOf(CmsApiPath, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>根据某 CMS api 地址反查其访问令牌（FillDetail 等只有 VodItem.Api 时可用）。</summary>
        public static string CmsTokenForApi(string api)
        {
            if (!IsCmsApi(api)) return "";
            string host = HostRoot(api);
            foreach (var sv in CmsServers())
            {
                if (string.Equals(HostRoot(CmsApiFor(sv.Host)), host, StringComparison.OrdinalIgnoreCase))
                    return sv.Token ?? "";
            }
            return "";
        }

        /// <summary>取 http(s)://host:port 部分。</summary>
        public static string HostRoot(string url)
        {
            try { return new Uri((url ?? "").Trim()).GetLeftPart(UriPartial.Authority); }
            catch { return ""; }
        }

        /// <summary>当前配置的 CMS 服务清单；首次为空时写入默认双套（影视CMS + 海洋CMS）。</summary>
        public static List<VodCmsServer> CmsServers()
        {
            var c = Load();
            if (c.CmsServers == null || c.CmsServers.Count == 0)
            {
                c.CmsServers = new List<VodCmsServer>
                {
                    new VodCmsServer { Name = L10n.T("影视CMS"), Host = DefaultCmsHost, Token = "" },
                    new VodCmsServer { Name = L10n.T("海洋CMS"), Host = DefaultCmsHost2, Token = "" }
                };
                Save(c);
            }
            return c.CmsServers;
        }

        public static void SetCmsServers(List<VodCmsServer> servers)
        {
            if (servers == null) return;
            var c = Load();
            c.CmsServers = servers;
            Save(c);
        }

        /* ---------------- 配置读写 ---------------- */

        public static VodConfigDoc Default()
        {
            return new VodConfigDoc
            {
                Mode = "subs",
                Subs = new List<VodSub>
                {
                    new VodSub { Name = L10n.T("TVBox 官方 · GitHub"), Url = "https://raw.githubusercontent.com/TVboxorg/TVbox/main/dist/official.json", Enabled = true },
                    new VodSub { Name = L10n.T("TVBox 官方 · jsDelivr 镜像"), Url = "https://cdn.jsdelivr.net/gh/TVboxorg/TVbox@main/dist/official.json", Enabled = true },
                    new VodSub { Name = L10n.T("wzh15802 多仓聚合"), Url = "https://gh-proxy.com/https://raw.githubusercontent.com/wzh15802/tvbox/main/tv.json", Enabled = false }
                },
                MaxPages = 12,
                TimeoutMs = 7000,
                MaxSites = 32,
                HomeTtlHours = 72,
                Rev = ConfigRev
            };
        }

        /// <summary>配置结构版本。升级内置源表时 +1，Load() 会把老配置平滑迁移。
        /// v2：内置实测源从 8 个扩到 15 个，站点上限从 24 提到 32。</summary>
        public const int ConfigRev = 2;

        public static VodConfigDoc Load()
        {
            lock (_lock)
            {
                if (_cfg != null) return _cfg;
                try
                {
                    var p = ConfigFile;
                    if (!File.Exists(p))
                    {
                        try
                        {
                            using (var a = Application.Context.Assets.Open("webui/sources/onlinevod.json"))
                            using (var ms = new MemoryStream())
                            {
                                a.CopyTo(ms);
                                File.WriteAllBytes(p, ms.ToArray());
                            }
                        }
                        catch { File.WriteAllText(p, JsonConvert.SerializeObject(Default(), Formatting.Indented)); }
                    }
                    if (File.Exists(p))
                    {
                        var c = JsonConvert.DeserializeObject<VodConfigDoc>(File.ReadAllText(p));
                        if (c != null)
                        {
                            if (c.Subs == null) c.Subs = new List<VodSub>();
                            if (c.ExtraSites == null) c.ExtraSites = new List<VodSite>();
                            if (c.Disabled == null) c.Disabled = new List<string>();
                            if (c.MaxPages <= 0) c.MaxPages = 12;
                            if (c.TimeoutMs <= 0) c.TimeoutMs = 9000;
                            if (c.MaxSites <= 0) c.MaxSites = 32;
                            // 平滑迁移：老配置 24 站上限装不下扩充后的 15 个内置源 + 订阅站。
                            // 只在版本号变化时写盘一次，不会每次启动都重写。
                            if (c.Rev < ConfigRev)
                            {
                                c.Rev = ConfigRev;
                                if (c.MaxSites < 32) c.MaxSites = 32;
                                try { File.WriteAllText(p, JsonConvert.SerializeObject(c, Formatting.Indented)); } catch { }
                            }
                            _cfg = c;
                            return _cfg;
                        }
                    }
                }
                catch (Exception ex) { Android.Util.Log.Warn("MchatVod", "配置读取失败: " + ex.Message); }
                _cfg = Default();
                return _cfg;
            }
        }

        public static void Save(VodConfigDoc c)
        {
            if (c == null) return;
            lock (_lock)
            {
                try
                {
                    var p = ConfigFile;
                    var tmp = p + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(c, Formatting.Indented));
                    if (File.Exists(p)) File.Delete(p);
                    File.Move(tmp, p);
                    _cfg = c;
                    _sites = null;
                }
                catch (Exception ex) { Android.Util.Log.Warn("MchatVod", "配置保存失败: " + ex.Message); }
            }
        }

        /// <summary>切换模式 / 改订阅 / 启停站点后调用，使站点表重算。</summary>
        public static void Invalidate()
        {
            lock (_lock) { _sites = null; }
        }

        /* ---------------- 订阅文本：缓存优先，后台刷新 ---------------- */

        private static string SubCacheFile(string url) { return IoPath.Combine(SubDir, Md5(url) + ".json"); }

        private static string CachedSub(string url)
        {
            try { var f = SubCacheFile(url); return File.Exists(f) ? File.ReadAllText(f) : null; }
            catch { return null; }
        }

        /// <summary>后台刷新所有启用的订阅（含多仓展开）。返回新抓到的渠道数。</summary>
        public static int RefreshSubs()
        {
            var c = Load();
            int got = 0;
            var urls = new List<VodSub>();
            foreach (var s in c.Subs)
                if (s != null && s.Enabled && !string.IsNullOrWhiteSpace(s.Url)) urls.Add(s);
            // 并发下载，避免多仓串行拖慢
            var tasks = new System.Threading.Tasks.Task[urls.Count];
            for (int i = 0; i < urls.Count; i++)
            {
                var sub = urls[i];
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var txt = VodApi.HttpGetText(sub.Url, 12000);
                        if (string.IsNullOrWhiteSpace(txt) || txt.Length < 20) return;
                        var t = txt.TrimStart('\ufeff', ' ', '\t', '\r', '\n');
                        if (!t.StartsWith("{") && !t.StartsWith("[")) return;   // hex/base64 加密配置不支持
                        File.WriteAllText(SubCacheFile(sub.Url) + ".tmp", txt);
                        var f = SubCacheFile(sub.Url);
                        if (File.Exists(f)) File.Delete(f);
                        File.Move(f + ".tmp", f);
                        System.Threading.Interlocked.Increment(ref got);
                    }
                    catch { }
                });
            }
            try { System.Threading.Tasks.Task.WaitAll(tasks, 15000); } catch { }
            // 多仓列表里的子仓也预取一层，避免点开订阅后还要现等
            try { ExpandMultiRepos(c, 1); } catch { }
            Invalidate();
            return got;
        }

        private static void ExpandMultiRepos(VodConfigDoc c, int depth)
        {
            if (depth > 2) return;
            var pending = new List<string>();
            foreach (var s in c.Subs)
            {
                if (s == null || !s.Enabled) continue;
                var txt = CachedSub(s.Url);
                if (string.IsNullOrEmpty(txt)) continue;
                foreach (var u in MultiRepoUrls(txt)) pending.Add(u);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in pending.Distinct().Take(10))
            {
                if (!seen.Add(u)) continue;
                if (File.Exists(SubCacheFile(u))) continue;
                try
                {
                    var t = VodApi.HttpGetText(u, 8000);
                    if (string.IsNullOrWhiteSpace(t) || t.Length < 20) continue;
                    var f = SubCacheFile(u);
                    File.WriteAllText(f + ".tmp", t);
                    if (File.Exists(f)) File.Delete(f);
                    File.Move(f + ".tmp", f);
                }
                catch { }
            }
        }

        /// <summary>识别多仓配置 {"urls":[{"url":..}]}，抽出子仓地址。</summary>
        public static List<string> MultiRepoUrls(string json)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return res;
            try
            {
                var root = JObject.Parse(json);
                var arr = root["urls"] as JArray;
                if (arr == null) return res;
                foreach (var e in arr)
                {
                    string u = null;
                    if (e is JObject) u = (string)((JObject)e)["url"];
                    else if (e.Type == JTokenType.String) u = (string)e;
                    if (!string.IsNullOrWhiteSpace(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        res.Add(u);
                }
            }
            catch { }
            return res;
        }

        /* ---------------- 站点表 ---------------- */

        /// <summary>按标准 TVBox 配置解析 sites。跳过 type!=0/1、非 http、hide=1、加密配置。</summary>
        public static List<VodSite> ParseSites(string json, string origin)
        {
            var res = new List<VodSite>();
            if (string.IsNullOrWhiteSpace(json)) return res;
            try
            {
                var s = json.Replace("\ufeff", "").TrimStart();
                if (!s.StartsWith("{") && !s.StartsWith("[")) return res;
                var root = JToken.Parse(s);
                JArray arr = null;
                if (root is JArray) arr = (JArray)root;
                else if (root is JObject) arr = ((JObject)root)["sites"] as JArray;
                if (arr == null) return res;
                foreach (var e in arr)
                {
                    if (!(e is JObject)) continue;
                    var o = (JObject)e;
                    var api = Str(o, "api");
                    if (!api.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    int type = 1;
                    var tv = o["type"];
                    if (tv != null)
                    {
                        if (tv.Type == JTokenType.Integer) type = (int)tv;
                        else int.TryParse((string)tv, out type);
                    }
                    if (type != 0 && type != 1) continue;
                    if (Str(o, "hide") == "1") continue;
                    var key = Str(o, "key");
                    if (string.IsNullOrWhiteSpace(key)) key = api;
                    var name = Str(o, "name");
                    if (string.IsNullOrWhiteSpace(name)) name = key;
                    res.Add(new VodSite
                    {
                        Key = key, Name = CleanName(name), Api = api, Type = type,
                        Origin = origin ?? "", Enabled = true
                    });
                }
            }
            catch { }
            return res;
        }

        private static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                // 去掉订阅里常见的彩色 emoji / 装饰竖线，避免 App 端字体缺字
                if (ch == '┃' || ch == '丨' || ch == '|') { sb.Append('·'); continue; }
                if (ch > 0xFFFF) continue;              // surrogate pair = emoji
                if (ch >= 0x2600 && ch <= 0x27BF) continue;
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        private static string Str(JObject o, string k)
        {
            var v = o[k];
            if (v == null || v.Type == JTokenType.Null) return "";
            try { return v.Type == JTokenType.String ? (string)v : v.ToString(); }
            catch { return ""; }
        }

        /// <summary>把一段配置文本展开成站点列表；遇到多仓则读已缓存的子仓内容。</summary>
        private static void Flatten(string text, string origin, List<VodSite> into, int depth)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var t = text.TrimStart('\ufeff', ' ', '\t', '\r', '\n');
            if (!t.StartsWith("{") && !t.StartsWith("[")) return;
            var multi = MultiRepoUrls(t);
            if (multi.Count > 0)
            {
                if (depth >= 2) return;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in multi.Take(10))
                {
                    if (!seen.Add(u)) continue;
                    var sub = CachedSub(u);
                    if (!string.IsNullOrEmpty(sub)) Flatten(sub, origin + L10n.T(" / 子仓"), into, depth + 1);
                }
                return;
            }
            into.AddRange(ParseSites(t, origin));
        }

        /// <summary>当前生效的站点表。首次同步只读本地缓存（不联网），后台刷新另调 RefreshSubs。</summary>
        public static List<VodSite> Sites(bool forceRecompute = false)
        {
            lock (_lock)
            {
                if (_sites != null && !forceRecompute) return _sites;
                var c = Load();
                var all = new List<VodSite>();
                // 内置源永远作为兜底
                all.AddRange(ParseSites(BuiltInJson, L10n.T("内置")));
                foreach (var s in c.ExtraSites)
                    if (s != null && !string.IsNullOrWhiteSpace(s.Api)) all.Add(s);

                if (c.Mode == "cms")
                {
                    // 同时接入所有已配置的 CMS 服务（影视CMS / 海洋CMS …），各自独立取数。
                    var servers = CmsServers();
                    for (int i = 0; i < servers.Count; i++)
                    {
                        var sv = servers[i];
                        string api = CmsApiFor(sv.Host);
                        all.Add(new VodSite
                        {
                            Key = "cms_" + i + "_" + sv.Host.Replace(':', '_'),
                            Name = (string.IsNullOrWhiteSpace(sv.Name) ? "CMS" : sv.Name) + "（" + sv.Host + "）",
                            Api = api,
                            Type = 1,
                            Origin = "CMS",
                            Enabled = true,
                            IsCms = true,
                            Token = sv.Token ?? "",
                            Searchable = true
                        });
                    }
                }
                else if (c.Mode == "custom")
                {
                    Flatten(c.CustomJson, L10n.T("自定义"), all, 0);
                }
                else if (c.Mode == "subs")
                {
                    foreach (var sub in c.Subs)
                    {
                        if (sub == null || !sub.Enabled) continue;
                        var txt = CachedSub(sub.Url);
                        if (!string.IsNullOrEmpty(txt)) Flatten(txt, sub.Name, all, 0);
                    }
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var disabled = new HashSet<string>(c.Disabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var res = new List<VodSite>();
                foreach (var s in all)
                {
                    // api 去重时截掉 ?ac=list 尾巴，避免同一站点在订阅里换个参数又算一个
                    var norm = s.Api.Split('?')[0].TrimEnd('/').ToLowerInvariant();
                    if (!seen.Add(norm)) continue;
                    s.Enabled = !disabled.Contains(s.Key) && !disabled.Contains(s.Api);
                    res.Add(s);
                    if (res.Count >= c.MaxSites) break;
                }
                _sites = res;
                _signature = SignatureOf(res);
                return _sites;
            }
        }

        /// <summary>只返回启用站点。</summary>
        public static List<VodSite> EnabledSites()
        {
            return Sites().FindAll(s => s.Enabled);
        }

        public static string Signature()
        {
            var s = Sites();
            lock (_lock) { if (_signature.Length == 0) _signature = SignatureOf(s); return _signature; }
        }

        private static string SignatureOf(List<VodSite> sites)
        {
            var sb = new StringBuilder();
            foreach (var s in sites) sb.Append(s.Api).Append('|');
            sb.Append(Load().Mode);
            return Md5(sb.ToString()).Substring(0, 12);
        }

        public static VodStatus Status()
        {
            var c = Load();
            var all = Sites();
            var home = VodCache.LoadHome(Signature());
            return new VodStatus
            {
                Mode = c.Mode,
                SiteCount = all.Count,
                EnabledCount = all.Count(s => s.Enabled),
                CachedItems = home == null || home.List == null ? 0 : home.List.Count,
                Pages = home == null ? 0 : home.Page,
                Signature = Signature(),
                Updated = home == null || home.Ts <= 0 ? "" : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds(home.Ts).ToLocalTime().ToString("MM-dd HH:mm"),
                Subs = c.Subs,
                Sites = all
            };
        }

        public static void SetEnabled(string key, bool on)
        {
            var c = Load();
            if (c.Disabled == null) c.Disabled = new List<string>();
            if (on) c.Disabled.RemoveAll(x => x == key);
            else if (!c.Disabled.Contains(key)) c.Disabled.Add(key);
            Save(c);
        }

        public static void AddSub(VodSub sub)
        {
            if (sub == null || string.IsNullOrWhiteSpace(sub.Url)) return;
            var c = Load();
            if (!c.Subs.Exists(x => x.Url == sub.Url)) c.Subs.Add(sub);
            Save(c);
        }

        public static void RemoveSub(string url)
        {
            var c = Load();
            c.Subs.RemoveAll(x => x.Url == url);
            Save(c);
        }

        public static void ClearCache()
        {
            try { VodCache.ClearAll(); } catch { }
            Invalidate();
        }
    }
}

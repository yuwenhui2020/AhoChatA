// ============================================================
// 在线音乐（搜索 / 播放 / 下载 / 缓存 / 歌词 / 封面）
//
// 背景：用户提供的音源（C:\root\SYNC\同步\1，念心音源，RC4+base64 混淆）
// 反解后确认它 **只** 实现了 musicUrl：
//     http://mcp.nianxinxz.com/share/ceshi/{kg|kw|mg|tx}.php?id=<ID>&level=<LEVEL>&type=mp3
//     网易单独走 http://music.nxinxz.com/csy/wy.php?id=<ID>...
//   其中 kg 的 id = 酷狗 FileHash，其它平台 = songmid。代理会 302 到真实 CDN。
//   音源 **不提供** 搜索、歌词、封面。
//
// 因此本类的职责：
//   · 取流模板完全读 onlinesource.json（自兼容音源格式，可改平台/音质/域名）；
//   · 搜索自行实现（默认酷狗官方搜索），带内存缓存；
//   · 播放：优先本地缓存文件 → 其次解析出 302 后的**真实 CDN 直链**（省掉本地网关
//     一次转发，明显更快）→ 最后才回退到本地网关代理流；
//   · 缓存：加入播放列表即后台预下载到缓存目录，下次直接读本地文件（秒开）；
//   · 歌词：酷狗 krcs 检索 + lyrics 下载接口，落成 .lrc 供 Lyrics.Parse 直接渲染；
//   · 封面：搜索结果里的 Image 落盘成 jpg，写回 Song.CoverPath，播放页/迷你条/
//     通知栏/锁屏自动复用同一套加载逻辑。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AhoChatA.Vod;
using IoPath = System.IO.Path;

namespace AhoChatA.Music
{
    /// <summary>一条在线搜索结果。</summary>
    public class OnlineSong
    {
        public string Plat = "kg";        // 平台键（kg/kw/mg/tx/wy）
        public string Id = "";            // 普通品质 id（kg=FileHash，其它=songmid）
        public string HqId = "";          // 高品质 id（320k）
        public string SqId = "";          // 无损 id（flac）
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public int DurationMs;
        public string DurationText = "";
        public string Cover = "";         // 封面地址（由搜索引擎给出，可能为相对路径）
        public string FileName = "";      // 建议文件名（不含扩展名）
        public string DirectUrl = "";    // 搜索即给出的可播直链（如歌曲宝），优先于按平台解析
    }

    /// <summary>音源配置（与 onlinesource.json 对应）。</summary>
    public class OnlineSource
    {
        public string Name = L10n.T("念心音源（兼容解析版）");
        public string Note = "";
        public OnlineProxy Proxy = new OnlineProxy();
        public Dictionary<string, OnlinePlatform> Platforms = new Dictionary<string, OnlinePlatform>();
        public OnlineSearch Search = new OnlineSearch();
        public OnlineDownloadCfg Download = new OnlineDownloadCfg();
        public OnlineLyricCfg Lyric = new OnlineLyricCfg();
        public OnlineCacheCfg Cache = new OnlineCacheCfg();
    }

    public class OnlineProxy
    {
        public string Base = "https://mcp.nianxinxz.com/share/ceshi/";
        public string Type = "mp3";
    }

    public class OnlinePlatform
    {
        public string Label = "";
        public string Php = "";
        public string IdField = "hash";
        public Dictionary<string, string> Qualities = new Dictionary<string, string>();
    }

    public class OnlineSearch
    {
        public string Provider = "kugou";
        public string Endpoint = "https://songsearch.kugou.com/song_search_v2";
        public string Params = "keyword={q}&page={page}&pagesize=20&filtername=1&userid=-1&clientver=&platform=WebFilter";
        public OnlineSearchFields Fields = new OnlineSearchFields();
        public string CoverBase = "https://www.kugou.com/Content/Common/";
    }

    public class OnlineSearchFields
    {
        public string Lists = "data.lists";
        public string Title = "SongName";
        public string Artist = "SingerName";
        public string Album = "AlbumName";
        public string Duration = "Duration";
        public string Id = "FileHash";
        public string HqId = "HQFileHash";
        public string SqId = "SQFileHash";
        public string Cover = "Image";
    }

    public class OnlineDownloadCfg
    {
        public string Dir = "/storage/emulated/0/Music";
    }

    /// <summary>歌词接口（酷狗）。音源本身不带歌词，这里单列，可关闭或换实现。</summary>
    public class OnlineLyricCfg
    {
        public bool Enabled = true;
        public string SearchEndpoint = "https://krcs.kugou.com/search";
        public string DownloadEndpoint = "https://lyrics.kugou.com/download";
    }

    /// <summary>在线音频缓存（加入播放列表即预下载，播第二次起本地秒开）。</summary>
    public class OnlineCacheCfg
    {
        public bool Enabled = true;
        public string Dir = "";          // 留空 = files/music/online_cache
        public int MaxMb = 512;          // 超过后按最旧优先清理
        public bool PrefetchOnAdd = true;
    }

    public static class OnlineMusic
    {
        private static readonly object _io = new object();
        private static OnlineSource _cfg;
        private static bool _inited;

        /* ---------------- 进程内缓存 ---------------- */

        private sealed class SearchEntry { public List<OnlineSong> Songs; public DateTime Expire; }
        private sealed class DirectEntry { public string Url; public DateTime Expire; }

        private static readonly Dictionary<string, SearchEntry> _searchCache = new Dictionary<string, SearchEntry>();
        private static readonly Dictionary<string, DirectEntry> _directCache = new Dictionary<string, DirectEntry>();
        private static readonly HashSet<string> _caching = new HashSet<string>();
        private static bool _httpInited;

        /// <summary>配置文件路径（files/music/onlinesource.json），首次从内置资源拷贝出来便于编辑。</summary>
        public static string ConfigPath
        {
            get { return IoPath.Combine(MusicLibrary.PlatformFilesDir(), "music", "onlinesource.json"); }
        }

        public static void Init()
        {
            lock (_io)
            {
                if (_inited) return;
                _inited = true;
                _cfg = LoadConfig();
            }
            EnsureHttp();
        }

        private static OnlineSource LoadConfig()
        {
            try
            {
                var p = ConfigPath;
                if (!File.Exists(p))
                {
                    try
                    {
                        using (var a = Android.App.Application.Context.Assets.Open("webui/sources/onlinesource.json"))
                        using (var ms = new MemoryStream())
                        {
                            a.CopyTo(ms);
                            File.WriteAllBytes(p, ms.ToArray());
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(L10n.T("拷贝 onlinesource.json 失败：") + ex.Message); }
                }
                if (File.Exists(p))
                {
                    try { return JsonConvert.DeserializeObject<OnlineSource>(File.ReadAllText(p)) ?? DefaultSource(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(L10n.T("解析 onlinesource.json 失败：") + ex.Message); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(L10n.T("加载在线音源配置失败：") + ex.Message); }
            return DefaultSource();
        }

        private static OnlineSource DefaultSource()
        {
            // 与内置资源一致的内置兜底，确保资源缺失也能用。
            var s = new OnlineSource();
            s.Platforms["kg"] = new OnlinePlatform
            {
                Label = L10n.T("酷狗"), Php = "kg.php", IdField = "hash",
                Qualities = new Dictionary<string, string> { { "128k", "standard" }, { "320k", "exhigh" }, { "flac", "lossless" } }
            };
            s.Search.Endpoint = "https://songsearch.kugou.com/song_search_v2";
            s.Search.Params = "keyword={q}&page={page}&pagesize=20&filtername=1&userid=-1&clientver=&platform=WebFilter";
            return s;
        }

        public static OnlineSource Config
        {
            get { lock (_io) { if (_cfg == null) _cfg = LoadConfig(); return _cfg; } }
        }

        /* ---------------- 播放地址 ---------------- */

        /// <summary>按平台 + 音质拼出第三方代理的播放地址（该地址会 302 到真实 MP3）。</summary>
        public static string ResolvePlayUrl(OnlineSong s, string quality)
        {
            if (s == null) return "";
            var cfg = Config;
            if (!cfg.Platforms.TryGetValue(s.Plat, out var pf))
                cfg.Platforms.TryGetValue("kg", out pf);
            if (pf == null) return "";

            // 按音质选 id：无损 > 高品质 > 普通
            string id = s.Id ?? "";
            if (!string.IsNullOrEmpty(quality))
            {
                if (quality == "lossless" && !string.IsNullOrEmpty(s.SqId)) id = s.SqId;
                else if (quality == "exhigh" && !string.IsNullOrEmpty(s.HqId)) id = s.HqId;
            }
            if (string.IsNullOrEmpty(id)) return "";

            string level = "standard";
            if (!string.IsNullOrEmpty(quality) && pf.Qualities.TryGetValue(quality, out var lv))
                level = lv;

            return cfg.Proxy.Base + pf.Php
                 + "?id=" + Uri.EscapeDataString(id)
                 + "&level=" + Uri.EscapeDataString(level)
                 + "&type=" + Uri.EscapeDataString(cfg.Proxy.Type);
        }

        public static string ResolvePlayUrl(Song s, string quality)
        {
            if (s == null) return "";
            return ResolvePlayUrl(FromSong(s), quality);
        }

        /// <summary>Song → OnlineSong（只带足取流所需字段）。</summary>
        public static OnlineSong FromSong(Song s)
        {
            if (s == null) return null;
            return new OnlineSong
            {
                Plat = string.IsNullOrEmpty(s.OnlinePlat) ? "kg" : s.OnlinePlat,
                Id = s.OnlineId ?? "",
                HqId = s.OnlineHqId ?? "",
                SqId = s.OnlineSqId ?? "",
                Title = s.Title ?? "",
                Artist = s.Artist ?? "",
                Album = s.Album ?? "",
                DurationMs = s.DurationMs,
                Cover = s.OnlineCover ?? "",
            };
        }

        /// <summary>解析封面绝对地址（搜索返回的相对路径补上 coverBase；酷狗封面含 {size} 占位符需替换）。</summary>
        public static string ResolveCoverUrl(OnlineSong s)
        {
            if (s == null || string.IsNullOrEmpty(s.Cover)) return "";
            var url = s.Cover;
            // 酷狗封面模板带 {size} 占位符（如 stdmusic/{size}/xxx.jpg），不替换无法作为图片加载
            if (url.IndexOf("{size}", StringComparison.OrdinalIgnoreCase) >= 0)
                url = url.Replace("{size}", "240");
            // 协议相对地址（//img...）补成 https，否则会被误当相对路径拼接
            if (url.StartsWith("//")) url = "https:" + url;
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            var base0 = Config.Search.CoverBase;
            if (string.IsNullOrEmpty(base0)) base0 = "https://www.kugou.com/Content/Common/";
            return base0 + url.TrimStart('/');
        }

        /* ---------------- 搜索 ---------------- */

        public static async Task<List<OnlineSong>> SearchAsync(string keyword, int page)
        {
            var songs = new List<OnlineSong>();
            if (string.IsNullOrWhiteSpace(keyword)) return songs;
            EnsureHttp();

            string cacheKey = keyword.Trim().ToLowerInvariant() + "|" + Math.Max(1, page);
            lock (_searchCache)
            {
                if (_searchCache.TryGetValue(cacheKey, out var ce) && ce.Expire > DateTime.UtcNow)
                    return new List<OnlineSong>(ce.Songs);
            }

            try
            {
                var sc = Config.Search;
                string url = sc.Endpoint + "?" + sc.Params
                    .Replace("{q}", Uri.EscapeDataString(keyword.Trim()))
                    .Replace("{page}", (page <= 0 ? 1 : page).ToString());
                var json = await HttpGetString(url);
                if (string.IsNullOrEmpty(json)) { Android.Util.Log.Info("MchatMusic", "搜索无响应: " + url); return songs; }
                var root = JObject.Parse(json);
                var lists = root.SelectToken(sc.Fields.Lists) as JArray;
                if (lists == null) { Android.Util.Log.Info("MchatMusic", "搜索无 data.lists，body前120=" + (json.Length > 120 ? json.Substring(0, 120) : json)); return songs; }
                foreach (JObject it in lists)
                {
                    var s = new OnlineSong { Plat = "kg" };
                    s.Title = (string)it[sc.Fields.Title] ?? L10n.T("未知");
                    s.Artist = (string)it[sc.Fields.Artist] ?? L10n.T("未知歌手");
                    s.Album = (string)it[sc.Fields.Album] ?? "";
                    var dur = (long?)it[sc.Fields.Duration];
                    s.DurationMs = dur.HasValue ? (int)(dur.Value * 1000) : 0;
                    s.DurationText = Song.FormatMs(s.DurationMs);
                    s.Id = (string)it[sc.Fields.Id] ?? "";
                    s.HqId = (string)it[sc.Fields.HqId] ?? "";
                    s.SqId = (string)it[sc.Fields.SqId] ?? "";
                    s.Cover = (string)it[sc.Fields.Cover] ?? "";
                    s.FileName = MakeFileName(s.Title, s.Artist);
                    if (!string.IsNullOrEmpty(s.Id)) songs.Add(s);
                }
                Android.Util.Log.Info("MchatMusic", "搜索「" + keyword.Trim() + "」原始 " + lists.Count + " 条，可用 " + songs.Count + " 条");

                lock (_searchCache)
                {
                    // 简单的容量控制，避免长期驻留
                    if (_searchCache.Count > 60) _searchCache.Clear();
                    _searchCache[cacheKey] = new SearchEntry
                    {
                        Songs = new List<OnlineSong>(songs),
                        Expire = DateTime.UtcNow.AddMinutes(10)
                    };
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Info("MchatMusic", "在线搜索异常: " + ex);
                System.Diagnostics.Debug.WriteLine(L10n.T("在线搜索失败：") + ex.Message);
            }
            return songs;
        }

        /// <summary>把在线歌曲信息打包成给前端的 DTO（含已解析的播放/封面地址）。</summary>
        public static object ToDto(OnlineSong s, string quality)
        {
            return new
            {
                plat = s.Plat,
                id = s.Id,
                hqId = s.HqId,
                sqId = s.SqId,
                title = s.Title,
                artist = s.Artist,
                album = s.Album,
                duration = s.DurationMs,
                durationText = s.DurationText,
                cover = ResolveCoverUrl(s),
                fileName = s.FileName,
                // 直连播放地址：走网关 online/stream，由服务端去拼代理并流式转发
                stream = "/api/media/online/stream?plat=" + Uri.EscapeDataString(s.Plat)
                       + "&id=" + Uri.EscapeDataString(s.Id)
                       + "&q=" + Uri.EscapeDataString(quality ?? "standard"),
            };
        }

        /* ---------------- 播放源解析（App 内播放的核心加速点） ---------------- */

        /// <summary>
        /// 同步拿到「立刻可播」的地址（不做任何网络等待）：
        /// 1) 已缓存到本地的文件（最快，秒开）；
        /// 2) 20 分钟内预热过的真实 CDN 直链；
        /// 3) 直接返回代理地址 —— MediaPlayer 会自行跟随 302，可以马上开始缓冲，
        ///    不用像以前那样先花 2~3 秒把直链解析出来再开播。
        /// </summary>
        public static string GetPlaybackUrl(Song song, string quality = "standard")
        {
            if (song == null) return null;
            if (!song.IsOnline) return song.Path;
            try
            {
                // 1) 本地缓存文件最稳：零网络依赖，seek 也准
                var cached = ExistingCache(song, quality);
                if (cached != null) { song.CachePath = cached; return cached; }

                // 2) 本地回环网关流：MediaPlayer 只连 127.0.0.1 的一条常连接，
                //    第三方 302 / Range 全由网关内部处理。比播放器直连 CDN 稳得多
                //    （CDN 对 stagefright UA 会掐断，且断线后没法续）。
                if (!string.IsNullOrEmpty(song.Path) &&
                    song.Path.StartsWith("http://127.0.0.1", StringComparison.Ordinal))
                    return song.Path;
            }
            catch { }

            // 3) 兜底：代理地址（让播放器自己跟随 302）
            try
            {
                var proxy = ResolvePlayUrl(song, quality);
                if (!string.IsNullOrEmpty(proxy)) return proxy;
            }
            catch { }
            return song.Path;
        }

        /// <summary>备用源：现场解析 302 得到直链（失败则回退代理地址）。播放出错时重试用。</summary>
        public static async Task<string> ResolveAltUrlAsync(Song song, string quality = "standard")
        {
            if (song == null || !song.IsOnline) return song?.Path;
            var proxy = ResolvePlayUrl(song, quality);
            if (string.IsNullOrEmpty(proxy)) return song.Path;
            try
            {
                var direct = await FollowRedirectsAsync(proxy);
                if (!string.IsNullOrEmpty(direct) && !string.Equals(direct, proxy, StringComparison.OrdinalIgnoreCase))
                {
                    CacheDirect(song, quality, direct);
                    return direct;
                }
            }
            catch { }
            return proxy;
        }

        private static void CacheDirect(Song song, string quality, string direct)
        {
            try
            {
                string key = SongKey(song, quality);
                lock (_directCache)
                {
                    if (_directCache.Count > 300) _directCache.Clear();
                    _directCache[key] = new DirectEntry { Url = direct, Expire = DateTime.UtcNow.AddMinutes(20) };
                }
            }
            catch { }
        }

        /// <summary>
        /// 给播放器返回真正可用的数据源（带解析，用于预热/兼容旧调用）：
        /// 本地缓存 → 直链缓存 → 现场解析 → 代理地址。
        /// </summary>
        public static async Task<string> GetPlayableSourceAsync(Song song, string quality = "standard", bool cacheOnResolve = true)
        {
            if (song == null) return null;
            if (!song.IsOnline) return song.Path;

            // 1) 本地缓存文件（必须校验文件真实存在，之前只拼路径导致播放器拿到不存在的文件）
            try
            {
                if (!string.IsNullOrEmpty(song.CachePath) && File.Exists(song.CachePath)) return song.CachePath;
                var cached = ExistingCache(song, quality);
                if (cached != null)
                {
                    song.CachePath = cached;
                    return cached;
                }
            }
            catch { }

            // 2) 已解析的直链
            string key = SongKey(song, quality);
            lock (_directCache)
            {
                if (_directCache.TryGetValue(key, out var de) && de.Expire > DateTime.UtcNow && !string.IsNullOrEmpty(de.Url))
                    return de.Url;
            }

            // 3) 现场解析 302
            try
            {
                var proxy = ResolvePlayUrl(song, quality);
                if (!string.IsNullOrEmpty(proxy))
                {
                    var direct = await FollowRedirectsAsync(proxy);
                    if (!string.IsNullOrEmpty(direct))
                    {
                        CacheDirect(song, quality, direct);
                        if (cacheOnResolve && Config.Cache.Enabled)
                            _ = Task.Run(() => CacheAsync(song, quality));
                        return direct;
                    }
                    // 解析失败也要能用：直接回代理地址（MediaPlayer 会自己跟随 302），
                    // 而不是回退到 127.0.0.1 本地网关（网关没起就会直接报错跳歌）。
                    return proxy;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(L10n.T("解析直链失败：") + ex.Message); }

            return song.Path;
        }

        /// <summary>
        /// 搜索结果到达后后台预热：只解析直链（1 字节探测，不下音频），
        /// 这样用户点播放时代理解析这一步已经提前做完了（实测能省 2~3 秒）。
        /// </summary>
        public static void Warmup(IEnumerable<Song> songs, int count = 4)
        {
            if (songs == null) return;
            var list = songs.Where(x => x != null && x.IsOnline).Take(Math.Max(0, count)).ToList();
            if (list.Count == 0) return;

            // 播放已经改走「本地回环网关流」，点下去立刻开播；
            // 这里做两件轻量预热：① 先把 302 直链解析结果塞进网关缓存（每次 Range 就不用再等 3 秒）；
            // ② 提前取回封面与歌词，列表/详情页/聊天底栏一开播就有图有词。
            Task.Run(async () =>
            {
                var sem = new System.Threading.SemaphoreSlim(2);
                var tasks = list.Select(async s =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        if (string.IsNullOrEmpty(s.CoverPath)) await FetchCoverAsync(s);
                    }
                    catch { }
                    finally { sem.Release(); }
                }).ToList();
                tasks.Add(Task.Run(async () =>
                {
                    foreach (var s in list)
                    {
                        try
                        {
                            if (CachedDirectUrl(s.OnlinePlat, s.OnlineId) != null) continue;
                            var proxy = ResolvePlayUrl(s, "standard");
                            if (string.IsNullOrEmpty(proxy)) continue;
                            var direct = await FollowRedirectsAsync(proxy);
                            if (!string.IsNullOrEmpty(direct) && !string.Equals(direct, proxy, StringComparison.OrdinalIgnoreCase))
                                RememberDirectUrl(s.OnlinePlat, s.OnlineId, "standard", direct);
                        }
                        catch { }
                    }
                }));
                try { await Task.WhenAll(tasks); } catch { }
                // 歌词最慢，放在直链/封面之后串行取
                foreach (var s in list)
                {
                    try { if (string.IsNullOrEmpty(s.LrcPath)) await FetchLyricsAsync(s); } catch { }
                }
            });
        }

        /// <summary>跟随 302 拿到最终 CDN 地址（只取 1 字节，代价极低）。HttpClient 默认自动跟随重定向，最终地址在 RequestMessage.RequestUri。</summary>
        private static async Task<string> FollowRedirectsAsync(string url)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(9000))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    try { req.Headers.Range = new RangeHeaderValue(0, 0); } catch { }
                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        var finalUrl = resp.RequestMessage != null && resp.RequestMessage.RequestUri != null
                            ? resp.RequestMessage.RequestUri.ToString() : url;
                        return string.IsNullOrEmpty(finalUrl) ? url : finalUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("跟随跳转失败：") + ex.Message);
                return "";
            }
        }

        /* ---------------- 音频缓存 ---------------- */

        /// <summary>缓存根目录：配置了就用配置，否则 files/music/online_cache。</summary>
        public static string CacheDir()
        {
            try
            {
                var d = Config.Cache?.Dir;
                if (!string.IsNullOrWhiteSpace(d)) return d;
            }
            catch { }
            return IoPath.Combine(MusicLibrary.PlatformFilesDir(), "music", "online_cache");
        }

        /// <summary>该歌曲在缓存目录中的目标文件（不保证存在）。</summary>
        public static string CachedFilePath(Song s, string quality)
        {
            if (s == null || !s.IsOnline) return null;
            try
            {
                var dir = CacheDir();
                var name = SafeName((s.OnlinePlat ?? "kg") + "_" + (s.OnlineId ?? "") + "_" + (quality ?? "standard"));
                var f = IoPath.Combine(dir, name + ".mp3");
                return f;
            }
            catch { return null; }
        }

        /// <summary>若已缓存则返回文件路径，否则 null。</summary>
        public static string ExistingCache(Song s, string quality = "standard")
        {
            try
            {
                var f = CachedFilePath(s, quality);
                if (f != null && File.Exists(f) && new FileInfo(f).Length > 1024) return f;
            }
            catch { }
            return null;
        }

        /// <summary>把在线音频缓存到本地（已存在则直接返回）。可安全重复调用。</summary>
        public static async Task<string> CacheAsync(Song s, string quality = "standard")
        {
            if (s == null || !s.IsOnline) return null;
            if (!Config.Cache.Enabled) return null;

            var dest = CachedFilePath(s, quality);
            if (dest == null) return null;
            var exist = ExistingCache(s, quality);
            if (exist != null) { s.CachePath = exist; return exist; }

            string key = SongKey(s, quality);
            lock (_caching) { if (!_caching.Add(key)) return null; }
            string tmp = dest + ".part";
            try
            {
                Directory.CreateDirectory(IoPath.GetDirectoryName(dest));
                var url = ResolvePlayUrl(s, quality);
                if (string.IsNullOrEmpty(url)) return null;
                if (!await DownloadToFileAsync(url, tmp)) return null;
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    return null;
                }
                s.CachePath = dest;
                try { EvictCache(); } catch { }
                return dest;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("缓存在线音频失败：") + ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
            finally { lock (_caching) { _caching.Remove(key); } }
        }

        /// <summary>后台批量预热（加入播放列表时调用）：并发 2，顺序下载。</summary>
        public static void PrefetchQueue(IEnumerable<Song> songs, string quality = "standard")
        {
            if (songs == null) return;
            if (!Config.Cache.Enabled || !Config.Cache.PrefetchOnAdd) return;
            var list = songs.Where(x => x != null && x.IsOnline).ToList();
            if (list.Count == 0) return;
            Task.Run(async () =>
            {
                foreach (var s in list)
                {
                    try
                    {
                        // 正在前台播放的那首不抢带宽（它走的是流式播放，和后台静默缓存互不干扰）
                        var cur = MusicPlayer.Current;
                        if (cur != null && string.Equals(cur.Path, s.Path, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ExistingCache(s, quality) != null) continue;

                        // 前台正在缓冲时先让路，避免抢带宽把正在播的歌卡住（最多等 20 秒）
                        for (int w = 0; w < 40 && MusicPlayer.IsBuffering; w++) await Task.Delay(500);

                        await CacheAsync(s, quality);
                    }
                    catch { }
                }
            });
        }

        private static async Task<bool> DownloadToFileAsync(string url, string dest)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(60000))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    using (var input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                        await input.CopyToAsync(fs).ConfigureAwait(false);
                    return new FileInfo(dest).Length > 1024;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("下载到文件失败：") + ex.Message);
                return false;
            }
        }

        /// <summary>按 MaxMb 上限清理最旧的缓存文件。</summary>
        private static void EvictCache()
        {
            var dir = CacheDir();
            if (!Directory.Exists(dir)) return;
            long max = Math.Max(64, Config.Cache.MaxMb) * 1024L * 1024L;
            var files = new DirectoryInfo(dir).GetFiles("*.mp3")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > max) { try { f.Delete(); } catch { } }
            }
        }

        /* ---------------- 歌词 ---------------- */

        private static string LyricDir()
        {
            return IoPath.Combine(MusicLibrary.PlatformFilesDir(), "music", "lyrics_online");
        }

        /// <summary>
        /// 取在线歌词（酷狗）。成功时把 .lrc 落盘并写回 s.LrcPath，
        /// 之后 MusicLibrary.LoadLyrics / Lyrics.Parse 直接可用。
        /// </summary>
        public static async Task<string> FetchLyricsAsync(Song s)
        {
            if (s == null || !s.IsOnline) return null;
            try
            {
                if (!string.IsNullOrEmpty(s.LrcPath) && File.Exists(s.LrcPath)) return s.LrcPath;
                var lc = Config.Lyric;
                if (lc == null || !lc.Enabled) return null;

                var dir = LyricDir();
                Directory.CreateDirectory(dir);
                var file = IoPath.Combine(dir, SafeName((s.OnlinePlat ?? "kg") + "_" + (s.OnlineId ?? "")) + ".lrc");
                if (File.Exists(file) && new FileInfo(file).Length > 0)
                {
                    s.LrcPath = file;
                    return file;
                }

                var kw = (s.Title ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s.Artist)) kw += " - " + s.Artist.Trim();
                if (string.IsNullOrWhiteSpace(kw)) return null;

                var u1 = lc.SearchEndpoint
                       + "?ver=1&man=no&client=mobi"
                       + "&keyword=" + Uri.EscapeDataString(kw)
                       + "&duration=" + Math.Max(0, s.DurationMs)
                       + "&hash=" + Uri.EscapeDataString(s.OnlineId ?? "");
                var j1 = await HttpGetString(u1);
                if (string.IsNullOrEmpty(j1)) return null;
                var cands = JObject.Parse(j1)["candidates"] as JArray;
                if (cands == null || cands.Count == 0) return null;

                string id = (string)cands[0]["id"];
                string ak = (string)cands[0]["accesskey"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ak)) return null;

                var u2 = lc.DownloadEndpoint
                       + "?ver=1&client=pc&id=" + Uri.EscapeDataString(id)
                       + "&accesskey=" + Uri.EscapeDataString(ak)
                       + "&fmt=lrc&charset=utf8";
                var j2 = await HttpGetString(u2);
                if (string.IsNullOrEmpty(j2)) return null;
                var b64 = (string)JObject.Parse(j2)["content"];
                if (string.IsNullOrEmpty(b64)) return null;

                var lrc = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                if (string.IsNullOrWhiteSpace(lrc)) return null;
                File.WriteAllText(file, lrc, new UTF8Encoding(false));
                s.LrcPath = file;
                return file;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("获取在线歌词失败：") + ex.Message);
                return null;
            }
        }

        /* ---------------- 封面 ---------------- */

        private static string CoverDir()
        {
            return IoPath.Combine(MusicLibrary.PlatformFilesDir(), "music", "covers_online");
        }

        /// <summary>把远程封面落盘为本地 jpg 并写回 s.CoverPath（播放页/通知/锁屏共用）。</summary>
        public static async Task<string> FetchCoverAsync(Song s)
        {
            if (s == null || !s.IsOnline) return null;
            try
            {
                if (!string.IsNullOrEmpty(s.CoverPath) && File.Exists(s.CoverPath)) return s.CoverPath;
                var url = s.OnlineCover;
                if (string.IsNullOrEmpty(url)) return null;

                var dir = CoverDir();
                Directory.CreateDirectory(dir);
                var file = IoPath.Combine(dir, SafeName((s.OnlinePlat ?? "kg") + "_" + (s.OnlineId ?? "")) + ".jpg");
                if (File.Exists(file) && new FileInfo(file).Length > 512)
                {
                    s.CoverPath = file;
                    return file;
                }

                var bytes = await HttpGetBytes(url);
                if (bytes == null || bytes.Length < 512) return null;
                File.WriteAllBytes(file, bytes);
                s.CoverPath = file;
                return file;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("获取在线封面失败：") + ex.Message);
                return null;
            }
        }

        /* ---------------- 下载到本地曲库 ---------------- */

        /// <summary>下载真实 MP3 到默认音乐目录，返回落盘路径（失败返回 null）。</summary>
        public static async Task<string> DownloadAsync(OnlineSong s, string quality, string name)
        {
            try
            {
                // 兜底：调用方若只通过 name 传了标题而没填 s.Title，补上以便歌词检索有关键词
                if (string.IsNullOrEmpty(s.Title) && !string.IsNullOrEmpty(name)) s.Title = name;
                var url = ResolvePlayUrl(s, quality);
                if (string.IsNullOrEmpty(url)) return null;
                var dir = DownloadDir();
                try { Directory.CreateDirectory(dir); } catch { }
                string baseName = string.IsNullOrWhiteSpace(name) ? s.FileName : MakeFileName(name, "");
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "online_" + MusicLibrary.Hash(s.Id ?? DateTime.Now.Ticks.ToString());

                // 流式落盘：整首歌（无损可达几十 MB）不再一次性 ReadAsByteArrayAsync 驻留内存，
                // 改成边下边写到临时文件，只把文件头 12 字节读进内存判格式，避免 OOM。
                var temp = IoPath.Combine(dir, "dl_" + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    if (!await StreamToFileAsync(url, temp).ConfigureAwait(false)) return null;
                    var fi = new FileInfo(temp);
                    if (!fi.Exists || fi.Length < 1024) return null;

                    var header = new byte[12];
                    using (var fs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fs.Read(header, 0, header.Length) < header.Length) return null;
                    }
                    string audioExt = AudioExt(header) ?? ".mp3";
                    string dest = IoPath.Combine(dir, baseName + audioExt);
                    int n = 1;
                    while (File.Exists(dest))
                        dest = IoPath.Combine(dir, baseName + "_" + (++n) + audioExt);
                    File.Move(temp, dest);

                    // 封面 / 歌词：与播放同逻辑，保证下载三件套与播放时一致。
                    try
                    {
                        var meta = new Song
                        {
                            IsOnline = true,
                            OnlinePlat = s.Plat,
                            OnlineId = s.Id,
                            OnlineHqId = s.HqId,
                            OnlineSqId = s.SqId,
                            OnlineCover = s.Cover,
                            Title = string.IsNullOrEmpty(s.Title) ? name : s.Title,
                            Artist = s.Artist,
                            Album = s.Album,
                            DurationMs = s.DurationMs,
                        };
                        var coverLocal = await FetchCoverAsync(meta).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(coverLocal) && File.Exists(coverLocal))
                        {
                            byte[] cb = File.ReadAllBytes(coverLocal);
                            string ext = ImageExt(cb);
                            if (string.IsNullOrEmpty(ext)) ext = "jpg";
                            File.Copy(coverLocal, IoPath.Combine(dir, baseName + "." + ext), true);
                        }
                        var lrcLocal = await FetchLyricsAsync(meta).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(lrcLocal))
                        {
                            string destLrc = IoPath.Combine(dir, baseName + ".lrc");
                            await FetchLyricsToAsync(s, destLrc).ConfigureAwait(false);
                            lrcLocal = File.Exists(destLrc) ? destLrc : null;
                        }
                        if (!string.IsNullOrEmpty(lrcLocal) && File.Exists(lrcLocal))
                            File.Copy(lrcLocal, IoPath.Combine(dir, baseName + ".lrc"), true);
                    }
                    catch { }

                    // 触发本地曲库重扫，让刚下载的歌立即出现在「音乐」列表（不等待其结果）
                    try { _ = MusicLibrary.ScanAsync(); } catch { }
                    return dest;
                }
                finally
                {
                    // 异常或中途失败时清掉半成品，避免残留 .part 垃圾文件
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("在线下载失败：") + ex.Message);
                return null;
            }
        }

        /// <summary>流式下载：响应体边读边写文件，内存占用恒定（不随文件大小增长）。</summary>
        private static async Task<bool> StreamToFileAsync(string url, string dest, int timeoutMs = 120000)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        Android.Util.Log.Info("MchatMusic", "流式下载 HTTP " + (int)resp.StatusCode + " " + url);
                        return false;
                    }
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await src.CopyToAsync(dst, 81920, cts.Token).ConfigureAwait(false);
                        await dst.FlushAsync(cts.Token).ConfigureAwait(false);
                    }
                    var len = new FileInfo(dest).Length;
                    Android.Util.Log.Info("MchatMusic", "流式下载完成 " + len + "B " + url);
                    return len > 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(L10n.T("流式下载失败：") + ex.Message);
                Android.Util.Log.Info("MchatMusic", "流式下载失败 " + url + " : " + ex.Message);
                return false;
            }
        }

        /// <summary>把远程封面落盘为 &lt;dir&gt;/&lt;baseName&gt;.&lt;ext&gt;（与音乐/歌词同名同目录）。</summary>
        private static async Task DownloadCoverToAsync(OnlineSong s, string dir, string baseName)
        {
            var url = ResolveCoverUrl(s);
            if (string.IsNullOrEmpty(url)) return;
            var bytes = await HttpGetBytes(url);
            if (bytes == null || bytes.Length < 512) return;
            string ext = ImageExt(bytes);
            if (string.IsNullOrEmpty(ext)) ext = "jpg";
            try { File.WriteAllBytes(IoPath.Combine(dir, baseName + "." + ext), bytes); } catch { }
        }

        /// <summary>把在线歌词落盘为指定路径（&lt;baseName&gt;.lrc，与音乐/封面同名同目录）。</summary>
        private static async Task FetchLyricsToAsync(OnlineSong s, string destLrc)
        {
            try
            {
                // 用户主动下载整首，歌词是预期组成之一：即便全局「歌词」开关关闭也尽力获取；
                // 端点若未配置则回退内置默认（酷狗歌词服务）。
                var lc = Config.Lyric;
                string searchEp = (lc != null && !string.IsNullOrEmpty(lc.SearchEndpoint))
                    ? lc.SearchEndpoint : "https://krcs.kugou.com/search";
                string dlEp = (lc != null && !string.IsNullOrEmpty(lc.DownloadEndpoint))
                    ? lc.DownloadEndpoint : "https://lyrics.kugou.com/download";
                var kw = (s.Title ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s.Artist)) kw += " - " + s.Artist.Trim();
                if (string.IsNullOrWhiteSpace(kw)) return;
                var u1 = searchEp
                       + "?ver=1&man=no&client=mobi"
                       + "&keyword=" + Uri.EscapeDataString(kw)
                       + "&duration=" + Math.Max(0, s.DurationMs)
                       + "&hash=" + Uri.EscapeDataString(s.Id ?? "");
                var j1 = await HttpGetString(u1);
                if (string.IsNullOrEmpty(j1)) return;
                var cands = JObject.Parse(j1)["candidates"] as JArray;
                if (cands == null || cands.Count == 0) return;
                string id = (string)cands[0]["id"];
                string ak = (string)cands[0]["accesskey"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ak)) return;
                var u2 = dlEp
                       + "?ver=1&client=pc&id=" + Uri.EscapeDataString(id)
                       + "&accesskey=" + Uri.EscapeDataString(ak)
                       + "&fmt=lrc&charset=utf8";
                var j2 = await HttpGetString(u2);
                if (string.IsNullOrEmpty(j2)) return;
                var b64 = (string)JObject.Parse(j2)["content"];
                if (string.IsNullOrEmpty(b64)) return;
                var lrc = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                if (string.IsNullOrWhiteSpace(lrc)) return;
                File.WriteAllText(destLrc, lrc, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>按文件头猜音频扩展名；猜不出时调用方回退 .mp3。</summary>
        private static string AudioExt(byte[] b)
        {
            if (b == null || b.Length < 12) return null;
            if (b[0] == 'f' && b[1] == 'L' && b[2] == 'a' && b[3] == 'C') return ".flac";
            if (b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') return ".ogg";
            if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                && b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E') return ".wav";
            if (b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return ".m4a";
            if (b[0] == 'I' && b[1] == 'D' && b[2] == '3') return ".mp3";
            if (b[0] == 0xFF && (b[1] == 0xFB || b[1] == 0xF3 || b[1] == 0xF2 || b[1] == 0xF1 || b[1] == 0xF9)) return ".mp3";
            return null;
        }

        /// <summary>根据文件头判断图片扩展名（jpg/png/webp/gif）。</summary>
        private static string ImageExt(byte[] b)
        {
            if (b == null || b.Length < 12) return "";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x4F) return "png";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "webp";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "gif";
            return "";
        }

        /// <summary>默认下载目录：统一落到外部共享存储的 Music/Mchat，文件在文件管理器可见、
        /// 也能被系统音乐库扫描到（这才是「下载到 SD 卡、文件找得到」）。</summary>
        public static string DownloadDir()
        {
            try
            {
                var cfgDir = Config.Download?.Dir;
                if (!string.IsNullOrEmpty(cfgDir)) return cfgDir;
            }
            catch { }
            return VodConfig.ExternalDir(Android.OS.Environment.DirectoryMusic);
        }

        /* ---------------- HTTP 工具（HttpClient：与阅读模块同一套稳定栈，规避 HttpWebRequest 在部分机型上的 IPv6/代理卡死） ---------------- */

        private const string UserAgent = "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36";

        private static readonly object _httpLock = new object();
        private static HttpClient _http;

        /// <summary>独立 HttpClient：不走系统代理、自动解压、固定浏览器 UA（和 ReaderEngine 一致的稳妥配置）。</summary>
        private static HttpClient Http
        {
            get
            {
                if (_http != null) return _http;
                lock (_httpLock)
                {
                    if (_http != null) return _http;
                    var handler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                        UseCookies = true,
                        UseProxy = false,
                    };
                    var hc = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                    hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
                    hc.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                    hc.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.kugou.com/");
                    _http = hc;
                    return _http;
                }
            }
        }

        private static void EnsureHttp()
        {
            if (_httpInited) return;
            _httpInited = true;
            try { var _ = Http; } catch { }   // 预初始化
            try
            {
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 12;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
        }

        private static async Task<string> HttpGetString(string url, int timeoutMs = 12000)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Android.Util.Log.Info("MchatMusic", "GET " + url + " -> HTTP " + (int)resp.StatusCode + ", " + (text == null ? 0 : text.Length) + "B");
                    return text;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Info("MchatMusic", "GET 失败 " + url + " : " + ex.Message);
                System.Diagnostics.Debug.WriteLine(L10n.T("HTTP GET 失败：") + url + " " + ex.Message);
                return null;
            }
        }

        private static async Task<byte[]> HttpGetBytes(string url, int timeoutMs = 30000)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    Android.Util.Log.Info("MchatMusic", "GET(bytes) " + url + " -> HTTP " + (int)resp.StatusCode + ", " + (bytes == null ? 0 : bytes.Length) + "B");
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Info("MchatMusic", "GET(bytes) 失败 " + url + " : " + ex.Message);
                System.Diagnostics.Debug.WriteLine(L10n.T("HTTP GET 字节失败：") + url + " " + ex.Message);
                return null;
            }
        }

        /* ---------------- 工具 ---------------- */

        private static string SongKey(Song s, string quality)
        {
            return (s.OnlinePlat ?? "kg") + ":" + (s.OnlineId ?? "") + ":" + (quality ?? "standard");
        }

        /// <summary>
        /// 按平台/歌曲 id 取已解析的 CDN 直链。
        /// 本地网关流每次 Range 请求都要重新走一遍代理 302（约 3 秒），
        /// 命中这里就能直连 CDN，换歌/拖动进度条都不会再等。
        /// </summary>
        public static string CachedDirectUrl(string plat, string id, string quality = "standard")
        {
            if (string.IsNullOrEmpty(id)) return null;
            string key = (string.IsNullOrEmpty(plat) ? "kg" : plat) + ":" + id + ":" + (quality ?? "standard");
            lock (_directCache)
            {
                if (_directCache.TryGetValue(key, out var de) && de.Expire > DateTime.UtcNow && !string.IsNullOrEmpty(de.Url))
                    return de.Url;
            }
            return null;
        }

        /// <summary>网关跟随 302 成功后，把最终地址记下来。</summary>
        public static void RememberDirectUrl(string plat, string id, string quality, string url)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url)) return;
            string key = (string.IsNullOrEmpty(plat) ? "kg" : plat) + ":" + id + ":" + (quality ?? "standard");
            lock (_directCache)
            {
                if (_directCache.Count > 300) _directCache.Clear();
                _directCache[key] = new DirectEntry { Url = url, Expire = DateTime.UtcNow.AddMinutes(20) };
            }
        }

        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            var r = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(r) ? "unknown" : r;
        }

        public static string MakeFileName(string title, string artist)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(artist)) sb.Append(artist.Trim()).Append(" - ");
            sb.Append(string.IsNullOrWhiteSpace(title) ? L10n.T("未命名") : title.Trim());
            var s = sb.ToString();
            foreach (var c in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
                s = s.Replace(c, '_');
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > 120) s = s.Substring(0, 120).Trim();
            return string.IsNullOrWhiteSpace(s) ? L10n.T("未命名") : s;
        }
    }
}

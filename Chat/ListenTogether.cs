// ============================================================
// 「一起听」：把本机正在播放的**整条播放队列**随音乐卡片一起发给局域网。
//
//   · 给音乐卡片附加 playlist（精简 JSON 数组），每一项都带**对端可直接访问**的
//     播放链接、封面链接、歌词，以及发起端稳定的 songKey(k)；
//   · 在线歌曲 → 播放链接指向本机对外网关的 online/stream 代理；
//   · 本地歌曲 → 播放链接指向本机 /api/media/stream?path=（HTTP Range，边下边放）；
//   · 对端点「一起听 / 同步听」即可把整条列表加载进本机播放器。
//
// 2026-11-07 同步听修复要点：
//   · HostBase 绝不能退化成 127.0.0.1（对端会一直「缓冲中…」），补 WiFi IP 兜底；
//   · 列表项带 k=发起端 songKey，切歌按 key 定位曲目，不再只依赖 ltIndex；
//   · 对端若在**本机曲库**里有同一首歌就直接用本地文件（秒开、不依赖对端网关）；
//   · 歌词不再只给开头几句：全部曲目后台并发按 lu 取完整 LRC，切歌时也能立即补齐；
//   · peerHost（局域网帧源 IP）可覆盖卡片里不可达的回环地址。
//
// 体积控制：qaul 帧有 65535 字节上限，列表最多 20 首、字段短名、歌词截断；
// patch 帧不重发列表（与 cover/lrc 同策略）。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Newtonsoft.Json;
using AhoChatA.Music;

namespace AhoChatA
{
    public static class ListenTogether
    {
        private const string PrefName = "Mchat";
        private const string KeyEnabled = "listenTogether";
        private const int MaxItems = 20;
        private const int MaxLrcChars = 400;
        // 当前正在播的这首给尽可能完整的歌词（对端一加入就能看到），其它曲目只带开头几句
        private const int MaxLrcCharsCurrent = 6000;
        private const int MaxFullLrcChars = 12000;

        /// <summary>默认打开。关闭后音乐卡片仍照常更新，只是不再附带播放列表。</summary>
        public static bool Enabled
        {
            get
            {
                try
                {
                    var ctx = Application.Context;
                    return ctx.GetSharedPreferences(PrefName, FileCreationMode.Private)
                              .GetBoolean(KeyEnabled, true);
                }
                catch { return true; }
            }
            set
            {
                try
                {
                    var ctx = Application.Context;
                    ctx.GetSharedPreferences(PrefName, FileCreationMode.Private)
                       .Edit().PutBoolean(KeyEnabled, value).Apply();
                }
                catch { }
                // 关掉开关后，网页端不应再看到「本机正在发起一起听」
                if (!value) { try { SyncListen.ClearHostShare(); } catch { } }
            }
        }

        /// <summary>
        /// 本机对外网关基址，形如 http://192.168.1.5:7790。
        /// DeviceStatus.Ip 偶尔返回 0.0.0.0（热点/飞行模式切换瞬间），此时退回 WiFi 接口 IP；
        /// 绝不能返回 127.0.0.1 —— 对端拿到这个地址会一直停在「缓冲中…」。
        /// </summary>
        public static string HostBase()
        {
            string ip = "";
            try { ip = DeviceStatus.Ip; } catch { }
            if (!IsLanIp(ip))
            {
                try { ip = LanShareService.GetWifiIp(Application.Context); } catch { ip = null; }
            }
            if (!IsLanIp(ip)) ip = "127.0.0.1";
            int port = 7790;
            try { port = WebServer.ExternalPortActual; } catch { }
            if (port <= 0) port = 7790;
            return "http://" + ip + ":" + port;
        }

        private static bool IsLanIp(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0") return false;
            if (ip.StartsWith("127.") || ip == "localhost" || ip == "::1") return false;
            return true;
        }

        private static string Abs(string rel)
        {
            var b = HostBase();
            return b + (rel.StartsWith("/") ? rel : "/" + rel);
        }

        /// <summary>曲目的稳定标识，切歌按它匹配（ltIndex 只是兜底）。</summary>
        public static string SongKeyOf(Song s)
        {
            if (s == null) return "";
            return s.IsOnline ? ("on:" + s.OnlinePlat + ":" + s.OnlineId) : ("local:" + s.Path);
        }

        /// <summary>
        /// 把列表里的 127.0.0.1 / localhost 换成发起端可达地址。
        /// 优先用卡片带的 peerHost（来自局域网帧源 IP），其次 ltHost。
        /// </summary>
        public static string ReachableHost(string ltHost, string peerHost = "")
        {
            foreach (var h in new[] { peerHost, ltHost })
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                try
                {
                    var u = new System.Uri(h);
                    if (IsLanIp(u.Host)) return u.GetLeftPart(UriPartial.Authority);
                }
                catch { }
            }
            return "";
        }

        private static string RewriteUrl(string url, string authority)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(authority)) return url;
            try
            {
                var u = new System.Uri(url);
                string h = u.Host;
                bool loopback = h == "127.0.0.1" || h == "localhost" || h == "[::1]"
                               || h == "::1" || h.StartsWith("127.");
                if (!loopback) return url;
                var a = new System.Uri(authority);
                if (!IsLanIp(a.Host)) return url;
                var ub = new UriBuilder(u) { Host = a.Host, Port = a.Port };
                return ub.Uri.ToString();
            }
            catch { return url; }
        }

        /// <summary>把在线歌曲 Path 里的回环代理地址换成对端可达的局域网地址。</summary>
        public static string OnlineStreamUrl(Song s)
        {
            var p = s.Path ?? "";
            const string loop = "http://127.0.0.1:6789";
            if (p.StartsWith(loop, StringComparison.OrdinalIgnoreCase))
                return Abs(p.Substring(loop.Length));
            if (p.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
            {
                int i = p.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
                return i > 0 ? Abs(p.Substring(i)) : p;
            }
            if (p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return p;

            if (!string.IsNullOrEmpty(s.OnlinePlat) && !string.IsNullOrEmpty(s.OnlineId))
                return Abs("/api/media/online/stream?plat=" + System.Uri.EscapeDataString(s.OnlinePlat)
                      + "&id=" + System.Uri.EscapeDataString(s.OnlineId) + "&q=standard");

            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                return Abs("/api/media/stream?path=" + System.Uri.EscapeDataString(p));
            return "";
        }

        public static string CoverUrl(Song s)
        {
            if (s.IsOnline)
            {
                if (!string.IsNullOrWhiteSpace(s.OnlineCover)
                    && s.OnlineCover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return s.OnlineCover;
                return "";
            }
            return Abs("/api/media/art?path=" + System.Uri.EscapeDataString(s.Path ?? ""));
        }

        public static string LyricUrl(Song s)
        {
            if (s.IsOnline) return "";
            return Abs("/api/media/lyrics?path=" + System.Uri.EscapeDataString(s.Path ?? ""));
        }

        /// <summary>歌词原文：优先内联 LRC（在线歌曲），其次本地 .lrc 文件。</summary>
        public static string LyricText(Song s)
        {
            try
            {
                if (!string.IsNullOrEmpty(s.LrcText)) return s.LrcText;
                var p = s.LrcPath;
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) return "";
                return File.ReadAllText(p);
            }
            catch { return ""; }
        }

        private static string Cut(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>队列签名：队列内容/顺序变了才需要重发列表。</summary>
        public static string QueueSignature()
        {
            try
            {
                var q = MusicPlayer.Queue;
                if (q == null || q.Count == 0) return "";
                var cur = MusicPlayer.Current;
                return q.Count + ":" + string.Join("|", q.Take(MaxItems).Select(x => x?.Path ?? ""))
                       + "#" + (cur?.Path ?? "");
            }
            catch { return ""; }
        }

        /// <summary>构造精简播放列表 JSON 字符串（字段名刻意取短）。</summary>
        public static string BuildPlaylist(out int index)
        {
            index = 0;
            try
            {
                var q = MusicPlayer.Queue;
                if (q == null || q.Count == 0) return "[]";
                var cur = MusicPlayer.Current;
                var curPath = cur?.Path ?? "";
                var list = new List<object>();
                for (int i = 0; i < q.Count && list.Count < MaxItems; i++)
                {
                    var s = q[i];
                    if (s == null || s.IsDivider) continue;
                    string playUrl = s.IsOnline ? OnlineStreamUrl(s)
                        : Abs("/api/media/stream?path=" + System.Uri.EscapeDataString(s.Path ?? ""));
                    if (string.IsNullOrEmpty(playUrl)) continue;
                    bool isCur = string.Equals(s.Path, curPath, StringComparison.OrdinalIgnoreCase);
                    if (isCur) index = list.Count;
                    list.Add(new
                    {
                        t = Cut(s.DisplayTitle, 48),
                        a = Cut(s.Artist, 28),
                        b = Cut(s.Album, 28),
                        d = s.DurationMs,
                        u = playUrl,
                        c = CoverUrl(s),
                        lu = LyricUrl(s),
                        l = Cut(LyricText(s), isCur ? MaxLrcCharsCurrent : MaxLrcChars),
                        k = SongKeyOf(s),
                        o = s.IsOnline ? 1 : 0
                    });
                }
                return JsonConvert.SerializeObject(list);
            }
            catch { return "[]"; }
        }

        /// <summary>给音乐卡片字段附加「一起听」信息。只在开关打开且队列变化/换曲时带列表。</summary>
        public static void Enrich(Dictionary<string, object> fields, Song current, string songKey)
        {
            if (fields == null) return;
            if (!Enabled)
            {
                try { SyncListen.ClearHostShare(); } catch { }
                return;
            }
            try
            {
                fields["lt"] = 1;
                fields["ltHost"] = HostBase();
                int idx;
                var sig = QueueSignature();
                if (songKey != _sentFor || sig != _sentSig || string.IsNullOrEmpty(_lastHostPlaylist))
                {
                    var json = BuildPlaylist(out idx);
                    fields["playlist"] = json;
                    fields["ltIndex"] = idx;
                    _sentFor = songKey;
                    _sentSig = sig;
                    _lastHostPlaylist = json;
                    _lastHostIndex = idx;
                }
                else
                {
                    idx = _sentIndex;
                    fields["ltIndex"] = idx;
                }
                // 卡片只发给对端；本机 WebServer 的状态镜像也要知道「我是发起端」，
                // 否则 /api/media/synclisten 返回 mode=0，网页端加不进一起听。
                SyncListen.NoteHostShare(_lastHostPlaylist, _lastHostIndex);
            }
            catch { }
        }

        private static string _sentFor = "", _sentSig = "";
        private static int _sentIndex;
        // 最近一次真正构造出来的共享歌单（patch 帧不重发列表，公告要复用这份）
        private static string _lastHostPlaylist = "";
        private static int _lastHostIndex;

        public static void NoteSent(Dictionary<string, object> fields)
        {
            try
            {
                if (fields != null && fields.ContainsKey("ltIndex"))
                {
                    _sentIndex = Convert.ToInt32(fields["ltIndex"]);
                    _lastHostIndex = _sentIndex;
                }
            }
            catch { }
        }

        /* ---------------- 完整歌词 ---------------- */

        /// <summary>
        /// /api/media/lyrics 返回 {"ok":true,"text":"..."}，把完整 LRC 写回 Song.LrcText。
        /// 并发取、3 秒超时：原先 20 首串行 × 6 秒最坏要 2 分钟，切歌时歌词永远是缺的。
        /// </summary>
        private static string FetchOneLyrics(string url)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                req.Method = "GET";
                req.Proxy = null;
                string raw;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    raw = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw)) return "";
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
                object tv;
                if (obj == null || !obj.TryGetValue("text", out tv) || tv == null) return "";
                var txt = Convert.ToString(tv) ?? "";
                return txt.Length > MaxFullLrcChars ? txt.Substring(0, MaxFullLrcChars) : txt;
            }
            catch { return ""; }
        }

        /// <summary>列表里只带了开头几句，后台并发补齐全部曲目的完整歌词。</summary>
        private static void FetchLyrics(List<Tuple<Song, string>> items)
        {
            try
            {
                var tasks = items.Select(p => Task.Run(() =>
                {
                    var txt = FetchOneLyrics(p.Item2);
                    if (!string.IsNullOrWhiteSpace(txt)) p.Item1.LrcText = txt;
                    return !string.IsNullOrWhiteSpace(txt);
                })).ToArray();
                Task.WaitAll(tasks, 6000);
                if (tasks.Any(t => t.IsCompleted && t.Result))
                    MusicPlayer.RefreshMeta();
            }
            catch { }
        }

        /// <summary>切歌时给当前曲单独补一次完整歌词（列表里的那几句不够用）。</summary>
        public static void EnsureLyrics(Song song, string url)
        {
            if (song == null || string.IsNullOrWhiteSpace(url)) return;
            Task.Run(() =>
            {
                try
                {
                    var txt = FetchOneLyrics(url);
                    if (string.IsNullOrWhiteSpace(txt)) return;
                    song.LrcText = txt;
                    MusicPlayer.RefreshMeta();
                }
                catch { }
            });
        }

        /* ---------------- 对端加入 ---------------- */

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// 对端本机曲库里找同一首歌：命中就直接播本地文件。
        /// 这样即使发起端的网关暂时不可达，跟随端也不会卡在「缓冲中…」，
        /// 更不会因准备失败自动跳到下一首（听起来像「只同步了切歌动作」）。
        /// </summary>
        private static Song FindLocal(string key, string title, string artist)
        {
            try
            {
                var all = MusicLibrary.Audio();
                if (key != null && key.StartsWith("on:", StringComparison.Ordinal))
                {
                    var parts = key.Split(':');
                    if (parts.Length >= 3)
                    {
                        var hit = all.Find(x => x != null
                            && string.Equals(x.OnlinePlat, parts[1], StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.OnlineId, parts[2], StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(x.Path) && File.Exists(x.Path));
                        if (hit != null) return hit;
                    }
                }
                string nt = Norm(title), na = Norm(artist);
                if (nt.Length < 2) return null;
                foreach (var s in all)
                {
                    if (s == null || s.IsVideo || s.IsDivider) continue;
                    if (string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path)) continue;
                    string st = Norm(s.Title);
                    try { if (string.IsNullOrEmpty(st)) st = Norm(Path.GetFileNameWithoutExtension(s.Path)); }
                    catch { }
                    if (st != nt) continue;
                    string sa = Norm(s.Artist);
                    if (na.Length > 0 && sa.Length > 0 && sa != na) continue;
                    return s;
                }
            }
            catch { }
            return null;
        }

        /// <summary>同步听播放源失败时：把该 Song 的播放地址改指向本机曲库里的同一首。</summary>
        public static bool TryUseLocal(Song s)
        {
            if (s == null) return false;
            try
            {
                var local = FindLocal(s.SharedKey, s.Title, s.Artist);
                if (local == null || string.Equals(local.Path, s.Path, StringComparison.Ordinal)) return false;
                s.Path = local.Path;
                s.CoverPath = local.CoverPath;
                s.LrcPath = local.LrcPath;
                if (local.DurationMs > 0) s.DurationMs = local.DurationMs;
                if (!string.IsNullOrEmpty(local.LrcText)) s.LrcText = local.LrcText;
                Android.Util.Log.Info("Mchat", "同步听改用本机曲库播放: " + s.DisplayTitle);
                return true;
            }
            catch { return false; }
        }

        public static bool Join(string playlistJson, int index)
        {
            return Join(playlistJson, index, "", "");
        }

        /// <summary>对端点击「一起听 / 同步听」：把列表加载进本机播放器并连续播放。</summary>
        public static bool Join(string playlistJson, int index, string ltHost, string peerHost)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(playlistJson)) return false;
                var arr = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(playlistJson);
                if (arr == null || arr.Count == 0) return false;
                string authority = ReachableHost(ltHost, peerHost);
                var songs = new List<Song>();
                var needLrc = new List<Tuple<Song, string>>();
                foreach (var d in arr)
                {
                    string url = d.ContainsKey("u") ? Convert.ToString(d["u"]) : "";
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    url = RewriteUrl(url, authority);
                    string lrc = d.ContainsKey("l") ? Convert.ToString(d["l"]) : "";
                    string lu = d.ContainsKey("lu") ? Convert.ToString(d["lu"]) : "";
                    lu = RewriteUrl(lu, authority);
                    string key = d.ContainsKey("k") ? Convert.ToString(d["k"]) : "";
                    string title = d.ContainsKey("t") ? Convert.ToString(d["t"]) : "";
                    string artist = d.ContainsKey("a") ? Convert.ToString(d["a"]) : "";

                    var song = new Song
                    {
                        Path = url,
                        Title = title,
                        Artist = artist,
                        Album = d.ContainsKey("b") ? Convert.ToString(d["b"]) : "",
                        DurationMs = d.ContainsKey("d") ? Convert.ToInt32(d["d"]) : 0,
                        IsOnline = false,
                        CoverUrl = d.ContainsKey("c") ? Convert.ToString(d["c"]) : "",
                        LrcText = lrc,
                        LrcUrl = lu,
                        SharedKey = key
                    };

                    // 本机有同一首歌 → 直接播本地文件，彻底绕开网络缓冲
                    var local = FindLocal(key, title, artist);
                    if (local != null)
                    {
                        song.Path = local.Path;
                        song.CoverPath = local.CoverPath;
                        song.LrcPath = local.LrcPath;
                        if (local.DurationMs > 0) song.DurationMs = local.DurationMs;
                        if (!string.IsNullOrEmpty(local.LrcText)) song.LrcText = local.LrcText;
                    }
                    else
                    {
                        // 所有曲目都并发补完整歌词，切歌时不会只剩开头几句
                        if (!string.IsNullOrWhiteSpace(lu))
                            needLrc.Add(Tuple.Create(song, lu));
                    }
                    songs.Add(song);
                }
                if (songs.Count == 0) return false;
                int at = Math.Max(0, Math.Min(index, songs.Count - 1));
                MusicPlayer.PlaySharedList(songs, at);
                if (needLrc.Count > 0) Task.Run(() => FetchLyrics(needLrc));
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "一起听加载失败: " + ex.Message);
                return false;
            }
        }
    }
}

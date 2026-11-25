// ============================================================
// 多音源框架（酷狗默认 + 歌曲宝兜底）
// ------------------------------------------------------------
// 设计要点（对应需求）：
//   1) 逐级兜底：默认音源（酷狗）搜索「无结果」时，才向上游兜底音源请求；
//      顺序固定为  默认(酷狗) → 歌曲宝，UI 永不出现音源切换入口。
//   2) 对「用户」隐藏音源身份：App 与网页端都不展示任何音源名称/切换按钮，
//      仅在全部失败时弹一句无具体音源名的通用提示「是否尝试其它音乐源」。
//   3) 对「上游站点」隐藏客户端身份：统一使用通用桌面浏览器 UA / 同源 Referer，
//      不携带任何能区分 App 还是网页端的标识（不写 X-App / 不暴露包名）。
//   4) 站长维护：只需修改下方 GequbaoBase 常量即可换站，无需改动其它代码。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Android.Util;
using AhoChatA.Music;

namespace AhoChatA.Music
{
    /// <summary>
    /// 多音源搜索 / 播放地址解析的统一定制入口。所有对外的“音源身份”都收敛在这里，
    /// 业务层（App 搜索、网页端搜索、流式播放）只调用 SearchAsync / ResolvePlayUrl，
    /// 永远拿不到“用的是哪个音源”这种信息。
    /// </summary>
    public static class MusicSources
    {
        // ===================== 站长可维护的音源链接 =====================
        /// <summary>二级音源（歌曲宝）：默认音源搜索失败时的第一兜底。只改这里即可换域名。</summary>
        public const string GequbaoBase = "https://www.gequbao.com";
        // ===============================================================

        public enum Tier : int { Default = 1, Gequbao = 2 }
        public static readonly int MaxTier = (int)Tier.Gequbao;

        // 通用桌面浏览器 UA —— 不暴露 App / 网页端身份，也不带任何包名/渠道标识
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        static readonly HttpClient _hc;
        static MusicSources()
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseCookies = true,
                    UseProxy = false,
                };
                _hc = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                _hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
                _hc.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception ex) { Log.Warn("MchatMusic", "MusicSources.HttpClient 初始化失败: " + ex.Message); }
        }

        /// <summary>是否还有更高优先级的兜底音源可用（用于 UI 决定是否提示「尝试其它音源」）。</summary>
        public static bool HasFallback(int tier) => tier < MaxTier;

        /// <summary>按层级搜索；tier 决定本次用哪个音源（不混源，避免用户感知来源）。</summary>
        public static async Task<List<OnlineSong>> SearchAsync(string keyword, int page, int tier)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return new List<OnlineSong>();
            try
            {
                if (tier == (int)Tier.Gequbao) return await GequbaoSearch(keyword, page);
                return await OnlineMusic.SearchAsync(keyword, page);   // 默认：酷狗
            }
            catch (Exception ex)
            {
                Log.Warn("MchatMusic", "MusicSources.SearchAsync tier=" + tier + " 异常: " + ex.Message);
                return new List<OnlineSong>();
            }
        }

        /// <summary>按平台解析播放地址（gqb 走歌曲宝，其余回落酷狗）。</summary>
        public static string ResolvePlayUrl(OnlineSong s, string quality)
        {
            if (s == null) return "";
            if (!string.IsNullOrEmpty(s.DirectUrl)) return s.DirectUrl;   // 搜索即给直链的音源
            if (s.Plat == "gqb") return GequbaoPlayUrl(s, quality);
            return OnlineMusic.ResolvePlayUrl(s, quality);
        }

        /// <summary>歌词：酷狗直接取；其它音源尽力用「歌名+歌手」去酷狗借一份近似歌词。</summary>
        public static async Task<string> FetchLyricsAsync(Song song)
        {
            if (song == null) return "";
            if (string.IsNullOrEmpty(song.OnlinePlat) || song.OnlinePlat == "kg")
                return await OnlineMusic.FetchLyricsAsync(song);
            try
            {
                var kw = ((song.Title ?? "") + " " + (song.Artist ?? "")).Trim();
                var list = await OnlineMusic.SearchAsync(kw, 1);
                OnlineSong hit = null;
                foreach (var x in list) { if (SongTitleMatch(x.Title, song.Title)) { hit = x; break; } }
                if (hit == null && list.Count > 0) hit = list[0];
                if (hit != null)
                {
                    var kg = new Song
                    {
                        IsOnline = true,
                        OnlinePlat = "kg",
                        OnlineId = hit.Id,
                        Title = hit.Title,
                        Artist = hit.Artist,
                        DurationMs = hit.DurationMs,
                    };
                    return await OnlineMusic.FetchLyricsAsync(kg);
                }
            }
            catch (Exception ex) { Log.Warn("MchatMusic", "非酷狗歌词回退失败: " + ex.Message); }
            return "";
        }

        // ---------------- 二级音源：歌曲宝（gequbao.com） ----------------
        static async Task<List<OnlineSong>> GequbaoSearch(string keyword, int page)
        {
            var songs = new List<OnlineSong>();
            try
            {
                // 歌曲宝搜索走站点同源表单接口，参数与浏览器一致，不暴露客户端类型
                string body = "keyword=" + Uri.EscapeDataString(keyword);
                string json = await HttpPostForm(GequbaoBase + "/api/guess-musics", body,
                                                 GequbaoBase + "/s/" + Uri.EscapeDataString(keyword));
                if (string.IsNullOrEmpty(json)) return songs;
                var root = JObject.Parse(json);
                if ((int?)root["code"] != 1) return songs;
                var arr = root["data"] as JArray;
                if (arr == null) return songs;
                foreach (JObject it in arr)
                {
                    string id = (string)it["business_key"];
                    if (string.IsNullOrEmpty(id)) id = (string)it["id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    var s = new OnlineSong { Plat = "gqb", Id = id };
                    s.Title = (string)it["title"] ?? (string)it["keyword"] ?? L10n.T("未知");
                    s.Artist = (string)it["author"] ?? "";
                    s.Album = "";
                    s.DurationMs = 0;
                    s.DurationText = "";
                    s.Cover = "";   // 列表不含封面，播放时用占位图；如需可再抓详情页
                    s.FileName = OnlineMusic.MakeFileName(s.Title, s.Artist);
                    songs.Add(s);
                }
                Log.Info("MchatMusic", "歌曲宝搜索「" + keyword + "」" + songs.Count + " 条");
            }
            catch (Exception ex) { Log.Warn("MchatMusic", "歌曲宝搜索异常: " + ex.Message); }
            return songs;
        }

        static string GequbaoPlayUrl(OnlineSong s, string quality)
        {
            try
            {
                // 1) 取详情页里的加密 play_id（Laravel 密文，直接透传给解析接口）
                string html = HttpGetString(GequbaoBase + "/music/" + Uri.EscapeDataString(s.Id),
                                            GequbaoBase + "/").GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(html)) return "";
                var m = System.Text.RegularExpressions.Regex.Match(
                    html, "window\\.appData\\s*=\\s*JSON\\.Parse\\('(.*?)'\\)",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!m.Success) return "";
                // 内部是标准 JSON（含 \u0022 转义），直接交给 JObject 解析
                var data = JObject.Parse(m.Groups[1].Value);
                string playId = (string)data["play_id"];
                if (string.IsNullOrEmpty(playId)) return "";

                // 2) 解析真实播放地址（站点返回 CDN 直链，如酷我）
                string body = "id=" + Uri.EscapeDataString(playId) + "&purpose=play";
                string json = HttpPostForm(GequbaoBase + "/member/common-play-url", body,
                                           GequbaoBase + "/music/" + Uri.EscapeDataString(s.Id))
                                           .GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(json)) return "";
                var r = JObject.Parse(json);
                if ((int?)r["code"] != 1) return "";
                string u = (string)r["data"]?["url"];
                if (!string.IsNullOrEmpty(u))
                    OnlineMusic.RememberDirectUrl("gqb", s.Id, quality ?? "standard", u);  // 缓存，避免每次重解
                return u ?? "";
            }
            catch (Exception ex) { Log.Warn("MchatMusic", "歌曲宝解析播放地址异常: " + ex.Message); return ""; }
        }

        // ---------------- 隐式 HTTP（通用浏览器指纹，不暴露客户端） ----------------
        static async Task<string> HttpGetString(string url, string referer)
        {
            try
            {
                using (var cts = new CancellationTokenSource(15000))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
                    using (var resp = await _hc.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Log.Warn("MchatMusic", "GET 失败 " + url + " : " + ex.Message); return null; }
        }

        static async Task<string> HttpPostForm(string url, string form, string referer)
        {
            try
            {
                using (var cts = new CancellationTokenSource(15000))
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    req.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
                    using (var resp = await _hc.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Log.Warn("MchatMusic", "POST 失败 " + url + " : " + ex.Message); return null; }
        }

        // ---------------- 小工具 ----------------
        static bool SongTitleMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            a = Normalize(a); b = Normalize(b);
            return a == b || a.Contains(b) || b.Contains(a);
        }
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
        static string FmtMs(int ms)
        {
            if (ms <= 0) return "";
            int t = ms / 1000;
            int m = t / 60, s = t % 60;
            return m + ":" + (s < 10 ? "0" : "") + s;
        }
    }
}

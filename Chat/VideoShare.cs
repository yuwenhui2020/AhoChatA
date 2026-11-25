// ============================================================
// 视频分享卡片：在播放界面 / 在线影视详情里点「分享」，往当前频道发一张带封面的卡片。
//
// 分享时**同时**给两种地址，不再让用户选「普通分享」还是「一起看」，卡片右下角
// 直接放两个按钮：
//   · 「观看」    → url       外源 CDN 的真实 m3u8/mp4（或本地视频的 Range 网关流），
//                   各播各的，宿主机不在线也能看；
//   · 「一起看」  → watchHost + watchUrl
//                   宿主机自己的播放代理（/api/media/vod/stream?u=...），
//                   复用宿主机已加载的详情 / 直链 / 海报 / m3u8 改写缓存，
//                   并跟随宿主机的 ts/seq 权威进度；宿主机离线即不可用。
//
// 封面来自视频瀑布墙的外源 CDN 海报（poster），posterProxy 是宿主机代理后的地址。
// 发送走 MchatBridge.SendVideoCard：store.Add + Emit + Deliver。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using Android.Graphics;
using Newtonsoft.Json;
using AhoChatA.Music;
using AhoChatA.Vod;
using Path = System.IO.Path;

namespace AhoChatA
{
    public static class VideoShare
    {
        public const string Kind = "videocard";

        /// <summary>
        /// 把播放队列里常见的「本机回环代理地址」拆回原始外源地址：
        /// http://127.0.0.1:6789/api/media/vod/stream?u=xxx → xxx。
        /// VodDetailActivity 对非直链源会把整条线路包成本机代理再交给 ExoPlayer，
        /// 直接把这种 Path 分享出去，别的设备会指向 127.0.0.1；再包一层则形成双重代理，
        /// 被 VodStream 的 SafeRemote 判成回环地址直接 400，一起看也就永远拿不到宿主机缓存。
        /// </summary>
        public static string UnwrapLocalStream(string url)
        {
            string u = url ?? "";
            const string marker = "/api/media/vod/stream?u=";
            for (int i = 0; i < 3; i++)
            {
                int q = u.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (q < 0) break;
                string raw = u.Substring(q + marker.Length);
                int amp = raw.IndexOf('&');
                if (amp >= 0) raw = raw.Substring(0, amp);
                string dec;
                try { dec = Uri.UnescapeDataString(raw); } catch { break; }
                if (string.IsNullOrWhiteSpace(dec) || dec == u) break;
                u = dec;
            }
            return u;
        }

        /// <summary>普通观看用的外源真实直链：播放页地址先本地解析，不给对端回环地址。</summary>
        public static string RealPlayUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (VodApi.IsDirectPlayUrl(path)) return path;
            try
            {
                var real = VodApi.ResolvePlayUrl(path);
                if (!string.IsNullOrWhiteSpace(real)) return real;
            }
            catch { }
            return path;
        }

        /// <summary>从本机曲库里正在播放的这一条构造分享卡片字段。</summary>
        public static Dictionary<string, object> Build(Song s, string title, string onlinePoster, string vodKey = "")
        {
            if (s == null) return null;
            var path = UnwrapLocalStream(s.Path);
            bool isHttp = path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            string host = ListenTogether.HostBase();

            // 「观看」：在线给外源 CDN 真实 m3u8/mp4；本地给宿主机网关的 Range 流
            string localRel = "/api/media/stream?path=" + Uri.EscapeDataString(s.Path ?? "");
            string url = isHttp ? RealPlayUrl(path) : host + localRel;
            // 「一起看」：只包一层宿主机代理，命中宿主机看片时落盘的 3 天缓存
            string watchUrl = isHttp ? VodApi.StreamUrl(path, "", false) : localRel;

            string coverB64 = "";
            string poster = onlinePoster ?? "";
            if (!isHttp)
            {
                try
                {
                    var bmp = MusicLibrary.VideoThumb(s, 320);
                    if (bmp != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            bmp.Compress(Bitmap.CompressFormat.Jpeg, 72, ms);
                            coverB64 = Convert.ToBase64String(ms.ToArray());
                        }
                        bmp.Recycle();
                    }
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                ["title"] = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title,
                ["url"] = url,                   // 普通观看：外源 CDN 真实地址
                ["cover"] = coverB64,            // 本地缩略图 base64（约 10~20KB）
                ["poster"] = poster,             // 外源 CDN 封面（视频瀑布墙海报）
                ["posterProxy"] = PosterProxy(poster),
                ["local"] = !isHttp ? 1 : 0,
                ["duration"] = s.DurationMs,
                ["name"] = Path.GetFileName(path),
                ["watchHost"] = host,            // 宿主机 LAN 地址
                ["watchUrl"] = watchUrl,         // 宿主机播放代理（相对）
                ["vkey"] = vodKey ?? "",
                ["episodeName"] = "",
                ["episodeIndex"] = 0
            };
        }

        /// <summary>网页端在线影视详情分享：外源直链 + 宿主机代理，封面取瀑布墙海报。</summary>
        public static Dictionary<string, object> BuildOnline(string title, string realUrl, string poster,
            string watchUrl, string vkey, string episodeName, int episodeIndex, long durationMs)
        {
            return new Dictionary<string, object>
            {
                ["title"] = string.IsNullOrWhiteSpace(title) ? L10n.T("分享的视频") : title,
                ["url"] = realUrl ?? "",          // 真实外源 m3u8 / mp4
                ["cover"] = "",
                ["poster"] = poster ?? "",        // 外源 CDN 封面
                ["posterProxy"] = PosterProxy(poster),
                ["local"] = 0,
                ["duration"] = durationMs,
                ["name"] = title ?? "",
                ["watchHost"] = ListenTogether.HostBase(),
                ["watchUrl"] = watchUrl ?? "",   // 本机代理 /api/media/vod/stream?u=
                ["vkey"] = vkey ?? "",
                ["episodeName"] = episodeName ?? "",
                ["episodeIndex"] = episodeIndex
            };
        }

        private static string PosterProxy(string poster)
        {
            if (string.IsNullOrWhiteSpace(poster)
                || !poster.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
            return VodApi.PosterUrl(poster, false);
        }

        /// <summary>卡片点击后的跳转参数：交给 VideoActivity 直接全屏播放。</summary>
        public static string PlayUrl(IDictionary<string, object> msg)
        {
            var u = Msg.Str(msg, "url", "");
            if (!string.IsNullOrWhiteSpace(u)) return u;
            return Msg.Str(msg, "mediaUrl", "");
        }

        /// <summary>「一起看」的完整地址：宿主机 LAN 基址 + 代理相对路径。</summary>
        public static string WatchPlayUrl(IDictionary<string, object> msg)
        {
            string rel = Msg.Str(msg, "watchUrl", "");
            if (string.IsNullOrWhiteSpace(rel)) return "";
            if (rel.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return rel;
            string host = Msg.Str(msg, "watchHost", "");
            if (string.IsNullOrWhiteSpace(host)) return rel;
            return host.TrimEnd('/') + (rel.StartsWith("/") ? rel : "/" + rel);
        }

        /// <summary>封面：优先本地缩略图 base64，其次宿主机代理海报，最后外源 CDN 原图。</summary>
        public static string CoverOf(IDictionary<string, object> msg)
        {
            string cover = Msg.Str(msg, "cover", "");
            if (!string.IsNullOrEmpty(cover)) return "data:image/jpeg;base64," + cover;
            string pp = Msg.Str(msg, "posterProxy", "");
            if (!string.IsNullOrEmpty(pp)) return pp;
            return Msg.Str(msg, "poster", "");
        }
    }
}

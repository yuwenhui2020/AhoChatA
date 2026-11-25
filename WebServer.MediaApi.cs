// ============================================================
// 网页端影音 API（WebServer 的 partial）
//
// 参考 jellyfin-web 12.4 的媒体库/播放条数据流，但全部落在本地：
//   /api/media/state      曲库 + 歌单 + 喜欢 + 目录（与 App 共用 files/music/*.json）
//   /api/media/stream     本地文件流，支持 HTTP Range（浏览器拖动进度条必需）
//   /api/media/art        封面（内嵌/同名图）或视频缩略图
//   /api/media/lyrics     同名 .lrc / 内嵌歌词
//   /api/media/image      图片原图 / 缩略图（图片库白名单校验）
//   /api/media/photo      图片详情 / 重命名 / 删除
//   /api/media/favorite   切换喜欢
//   /api/media/playlist   创建 / 重命名 / 删除 / 加歌 / 移歌
//   /api/media/scan       触发重新扫描
//   /api/media/exclude    从列表移出 / 恢复
//   /api/media/dirs       检索目录配置（读 / 开关）
//
// 安全：所有文件访问都必须先在曲库里按绝对路径查到条目，
// 防止把 /api/media/stream 当成任意文件读取接口。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using AhoChatA.Music;
using Newtonsoft.Json;

namespace AhoChatA
{
    public sealed partial class WebServer
    {
        private static int _mediaInited;

        private static void EnsureMedia()
        {
            if (System.Threading.Interlocked.Exchange(ref _mediaInited, 1) == 0)
            {
                try { MusicLibrary.Init(Application.Context); } catch { }
                try { PlaylistStore.Init(); } catch { }
                try { OnlineMusic.Init(); } catch { }
            }
        }

        private void ServeMediaLibraryApi(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            EnsureMedia();
            var res = ctx.Response;
            bool post = string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");

            try
            {
                string p = path.Substring("/api/media/".Length).Trim('/').ToLowerInvariant();

                if (p == "state") { WriteJson(res, Json(MediaState())); return; }
                // 「共享听」状态镜像：网关(7790)有 ServeSyncListen，但 App 内置 WebView
                // 是从这里(6789)直出 media.html 的，缺这一支会让 /media 页永远拿不到
                // App 正在播放的曲目，点「同步听」也毫无反应。两端输出同一份 JSON。
                if (p == "synclisten")
                {
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    res.AddHeader("Cache-Control", "no-store");
                    WriteJson(res, SyncListen.WebStatus());
                    return;
                }
                if (p == "stream") { ServeMediaStream(ctx, req); return; }
                if (p == "art") { ServeMediaArt(ctx, req); return; }
                if (p == "image") { ServeMediaImage(ctx, req); return; }
                if (p == "photo") { MediaPhoto(ctx, req, post); return; }
                if (p == "lyrics") { ServeMediaLyrics(ctx, req); return; }
                if (p == "favorite" && post) { MediaFavorite(ctx, req); return; }
                if (p == "playlist" && post) { MediaPlaylist(ctx, req); return; }
                if (p == "scan" && post) { MediaScan(ctx); return; }
                if (p == "exclude" && post) { MediaExclude(ctx, req); return; }
                if (p == "dirs") { MediaDirs(ctx, req, post); return; }
                if (p == "excluded") { WriteJson(res, Json(new { items = MusicLibrary.Excluded })); return; }

                // 在线音乐（搜索 / 流 / 下载）：搜本机没有的歌、在线播放、下载到默认音乐目录
                if (p == "online/search") { OnlineSearch(ctx, req); return; }
                if (p == "online/stream") { OnlineStream(ctx, req); return; }
                if (p == "online/lyrics") { OnlineLyrics(ctx, req); return; }
                if (p == "online/download" && post) { OnlineDownload(ctx, req); return; }

                // 在线影视（TVBox 订阅 + 苹果CMS 多源聚合），App 与网页端共用
                if (p == "vod" || p.StartsWith("vod/")) { ServeVodApi(ctx, req, p, post); return; }

                res.StatusCode = 404;
                WriteJson(res, "{\"error\":\"unknown media api\"}");
            }
            catch (Exception ex)
            {
                try { res.StatusCode = 500; WriteJson(res, Json(new { error = ex.Message })); }
                catch { try { res.Abort(); } catch { } }
            }
        }

        /* ---------------- 状态 ---------------- */

        private static object SongDto(Song s)
        {
            return new
            {
                path = s.Path,
                title = s.DisplayTitle,
                fileName = s.FileName,
                artist = string.IsNullOrWhiteSpace(s.Artist) ? L10n.T("未知歌手") : s.Artist,
                album = s.Album ?? "",
                duration = s.DurationMs,
                durationText = Song.FormatMs(s.DurationMs),
                size = s.Size,
                isVideo = s.IsVideo,
                hasCover = !string.IsNullOrEmpty(s.CoverPath),
                hasLyrics = !string.IsNullOrEmpty(s.LrcPath),
                favorite = PlaylistStore.IsFavorite(s.Path),
            };
        }

        private static object PhotoDto(Photo p)
        {
            return new
            {
                path = p.Path,
                name = p.Name,
                folder = p.Folder,
                modified = p.Modified,
                modifiedText = p.ModifiedText,
                size = p.Size,
                sizeText = p.SizeText,
                dimensions = p.DimensionText,
            };
        }

        private static object MediaState()
        {
            var audio = MusicLibrary.Audio();
            var videos = MusicLibrary.Videos();
            var photos = PhotoLibrary.All();
            var lists = PlaylistStore.AllLists.Select(pl =>
            {
                // 歌单封面 = 「最新添加的那首歌」的封面。
                // 前端用 art?path=<值> 取封面，而 /api/media/art 是按「歌曲 path」查歌再取它的
                // CoverPath，所以这里必须返回那首歌的 Path（不是 CoverPath 图片路径，否则查不到歌
                // 会退化成 1×1 透明图 → 前端不显示）。从末尾往前找最新添加且有封面的非视频歌曲。
                var songs = PlaylistStore.SongsOf(pl.Id);
                string coverSong = "";
                for (int k = songs.Count - 1; k >= 0; k--)
                {
                    if (!songs[k].IsVideo && !string.IsNullOrEmpty(songs[k].CoverPath))
                    { coverSong = songs[k].Path; break; }
                }
                return new
                {
                    id = pl.Id,
                    name = pl.Name,
                    count = PlaylistStore.CountOf(pl.Id),
                    isFavorites = pl.Id == PlaylistStore.FavoritesId,
                    cover = coverSong,
                };
            }).ToList();

            return new
            {
                ok = true,
                audio = audio.Select(SongDto).ToList(),
                videos = videos.Select(SongDto).ToList(),
                photos = photos.Select(PhotoDto).ToList(),
                photoFolders = PhotoLibrary.Folders().Select(f => new
                {
                    path = f.Path, name = f.Name, count = f.Count, cover = f.CoverPath
                }).ToList(),
                playlists = lists,
                favorites = PlaylistStore.Favorites,
                dirs = MusicLibrary.Dirs.Select(d => new
                {
                    path = d.Path, label = d.Label, enabled = d.Enabled,
                    audio = d.ForAudio, video = d.ForVideo, photo = d.ForPhoto, builtIn = d.BuiltIn
                }).ToList(),
                stats = new
                {
                    audio = audio.Count,
                    videos = videos.Count,
                    photos = photos.Count,
                    excluded = MusicLibrary.ExcludedCount,
                    lastScan = MusicLibrary.LastScan == default
                        ? "" : MusicLibrary.LastScan.ToString("MM-dd HH:mm"),
                    filterShortAudio = MusicLibrary.FilterShortAudio,
                    skipSystemSounds = MusicLibrary.SkipSystemSounds,
                }
            };
        }

        /* ---------------- 流媒体（Range） ---------------- */

        private static void ServeMediaStream(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            string target = GetQuery(req.Url.Query, "path");
            var song = MusicLibrary.Find(target);
            if (song == null || !File.Exists(song.Path))
            {
                res.StatusCode = 404;
                WriteJson(res, L10n.T("{\"error\":\"曲库里没有这个文件\"}"));
                return;
            }

            long total = new FileInfo(song.Path).Length;
            long from = 0, to = total - 1;
            bool partial = false;
            string range = req.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var spec = range.Substring(6).Split('-');
                    if (!string.IsNullOrEmpty(spec[0]))
                    {
                        long.TryParse(spec[0], out from);
                        if (spec.Length > 1 && !string.IsNullOrEmpty(spec[1]))
                            long.TryParse(spec[1], out to);
                    }
                    else if (spec.Length > 1 && !string.IsNullOrEmpty(spec[1]))
                    {
                        long suffix = 0;
                        long.TryParse(spec[1], out suffix);
                        from = Math.Max(0, total - suffix);
                    }
                    if (from < 0) from = 0;
                    if (to >= total || to < from) to = total - 1;
                    partial = true;
                }
                catch { from = 0; to = total - 1; partial = false; }
            }

            long length = to - from + 1;
            res.ContentType = StreamMime(song.Path);
            res.AddHeader("Accept-Ranges", "bytes");
            res.ContentLength64 = length;
            if (partial)
            {
                res.StatusCode = 206;
                res.AddHeader("Content-Range", "bytes " + from + "-" + to + "/" + total);
            }
            else res.StatusCode = 200;

            if (string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            { res.Close(); return; }

            // 大文件分块写出，避免整段读进内存；客户端断开时及时退出
            try
            {
                using (var fs = new FileStream(song.Path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(from, SeekOrigin.Begin);
                    var buf = new byte[64 * 1024];
                    long left = length;
                    var output = res.OutputStream;
                    while (left > 0)
                    {
                        int want = (int)Math.Min(buf.Length, left);
                        int got = fs.Read(buf, 0, want);
                        if (got <= 0) break;
                        output.Write(buf, 0, got);
                        left -= got;
                    }
                    output.Flush();
                }
            }
            catch { try { res.Abort(); } catch { } }
        }

        private static string MediaMime(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".m4a": return "audio/mp4";
                case ".flac": return "audio/flac";
                case ".wav": return "audio/wav";
                case ".ogg": case ".oga": return "audio/ogg";
                case ".opus": return "audio/ogg";
                case ".aac": return "audio/aac";
                case ".ape": return "audio/x-ape";
                case ".wma": return "audio/x-ms-wma";
                default: return MimeOf(file);
            }
        }

        /// <summary>视频优先走 MimeOf（video/mp4 等），音频走 MediaMime。</summary>
        private static string StreamMime(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".mp4": case ".m4v": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".avi": return "video/x-msvideo";
                case ".3gp": return "video/3gpp";
                default: return MediaMime(file);
            }
        }

        /* ---------------- 封面 / 缩略图 ---------------- */

        /// <summary>
        /// 兜底封面：本地音频大多没有内嵌封面，部分视频缩略图解码也会失败。
        /// 旧实现回 1×1 透明 PNG，前端把 &lt;img&gt; 隐藏后只剩音符占位，
        /// 看起来就是「视频和音频在 Web 端都没有封面」。这里按标题生成稳定的渐变 SVG，
        /// 保证每张卡片都有画面，且同名文件颜色固定、不会每次刷新都变。
        /// </summary>
        internal static byte[] GeneratedCover(string label, bool isVideo)
        {
            string text = (label ?? "").Trim();
            if (text.Length == 0) text = isVideo ? "视频" : "音乐";
            try { text = System.IO.Path.GetFileNameWithoutExtension(text); } catch { }
            if (string.IsNullOrWhiteSpace(text)) text = isVideo ? L10n.T("视频") : L10n.T("音乐");
            if (text.Length > 2) text = text.Substring(0, 2);

            long h = 0;
            foreach (char c in (label ?? "")) h = (h * 31 + c) & 0x7fffffffL;
            string[] pal = {
                "#3b4a6b,#1c2333", "#5b3a63,#261a2e", "#2f5d55,#142a28",
                "#6b4a32,#2a1c13", "#3d4f7a,#1a2138", "#6a3550,#2a1622",
                "#4a5b3a,#1d2614", "#374d63,#151f2b"
            };
            var p = pal[(int)(h % pal.Length)].Split(',');
            string glyph = isVideo ? "▶" : "♪";
            string safe = System.Security.SecurityElement.Escape(text);
            string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"360\" height=\"360\" viewBox=\"0 0 360 360\">"
                + "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">"
                + "<stop offset=\"0\" stop-color=\"" + p[0] + "\"/><stop offset=\"1\" stop-color=\"" + p[1] + "\"/>"
                + "</linearGradient></defs>"
                + "<rect width=\"360\" height=\"360\" fill=\"url(#g)\"/>"
                + "<circle cx=\"180\" cy=\"152\" r=\"66\" fill=\"rgba(255,255,255,.10)\"/>"
                + "<text x=\"180\" y=\"152\" font-size=\"62\" fill=\"rgba(255,255,255,.86)\" text-anchor=\"middle\" "
                + "dominant-baseline=\"central\" font-family=\"sans-serif\">" + glyph + "</text>"
                + "<text x=\"180\" y=\"258\" font-size=\"34\" fill=\"rgba(255,255,255,.92)\" text-anchor=\"middle\" "
                + "font-family=\"sans-serif\">" + safe + "</text></svg>";
            return Encoding.UTF8.GetBytes(svg);
        }

        internal static void WriteGeneratedCover(HttpListenerResponse res, string label, bool isVideo)
        {
            var b = GeneratedCover(label, isVideo);
            res.StatusCode = 200;
            res.ContentType = "image/svg+xml; charset=utf-8";
            res.AddHeader("Cache-Control", "public, max-age=86400");
            res.ContentLength64 = b.Length;
            using (var o = res.OutputStream) o.Write(b, 0, b.Length);
        }

        /// <summary>CDN 海报可能是 PNG/WebP/GIF，旧版一律回 image/jpeg，部分浏览器直接不渲染。</summary>
        internal static string ImageMimeOf(byte[] b)
        {
            if (b == null || b.Length < 4) return "application/octet-stream";
            if (b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
            if (b[0] == 0x89 && b[1] == 0x50) return "image/png";
            if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return "image/gif";
            if (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F') return "image/webp";
            if (b[0] == (byte)'B' && b[1] == (byte)'M') return "image/bmp";
            try
            {
                if (Encoding.UTF8.GetString(b, 0, Math.Min(160, b.Length))
                    .IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0) return "image/svg+xml";
            }
            catch { }
            return "application/octet-stream";
        }

        private static void ServeMediaArt(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            var song = MusicLibrary.Find(GetQuery(req.Url.Query, "path"));
            string file = null;
            if (song != null)
            {
                if (song.IsVideo)
                {
                    file = MusicLibrary.VideoThumbPath(song);
                    if (file == null)
                    {
                        var bmp = MusicLibrary.VideoThumb(song, 360);
                        try { bmp?.Recycle(); } catch { }
                        file = MusicLibrary.VideoThumbPath(song);
                    }
                }
                else file = song.CoverPath;
            }
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                // 没有真实封面也给一张生成封面，保证视频 / 音频卡片都有画面
                var raw = GetQuery(req.Url.Query, "path") ?? "";
                WriteGeneratedCover(res, song != null ? song.DisplayTitle : raw, song != null && song.IsVideo);
                return;
            }
            try
            {
                var bytes = File.ReadAllBytes(file);
                res.ContentType = MimeOf(file);
                res.AddHeader("Cache-Control", "public, max-age=86400");
                res.ContentLength64 = bytes.Length;
                using (var o = res.OutputStream) o.Write(bytes, 0, bytes.Length);
            }
            catch { try { res.Abort(); } catch { } }
        }

        /* ---------------- 图片：原图 / 缩略图 / 详情 / 删除 / 重命名 ---------------- */

        /// <summary>图片二进制。w>0 时返回按宽度生成的缩略图缓存，w=0 返回原图。
        /// 路径必须先在图片库白名单里查到，防止被当成任意文件读取接口。</summary>
        private static void ServeMediaImage(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            var photo = PhotoLibrary.Find(GetQuery(req.Url.Query, "path"));
            if (photo == null || !File.Exists(photo.Path))
            {
                res.StatusCode = 404;
                WriteJson(res, L10n.T("{\"error\":\"图库里没有这个文件\"}"));
                return;
            }
            int w = 0;
            int.TryParse(GetQuery(req.Url.Query, "w") ?? "0", out w);
            if (w < 0) w = 0;
            if (w > 1600) w = 1600;
            string file = w > 0 ? PhotoLibrary.ThumbPath(photo, w) : photo.Path;
            bool thumb = w > 0 && !string.IsNullOrEmpty(file) && !string.Equals(file, photo.Path);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { file = photo.Path; thumb = false; }

            try
            {
                var bytes = File.ReadAllBytes(file);
                res.ContentType = thumb ? "image/jpeg" : PhotoLibrary.Mime(photo.Path);
                res.ContentLength64 = bytes.Length;
                res.AddHeader("Cache-Control", "private, max-age=300");
                if ((GetQuery(req.Url.Query, "dl") ?? "") == "1")
                    res.AddHeader("Content-Disposition",
                        "attachment; filename*=UTF-8''" + System.Uri.EscapeDataString(photo.Name));
                using (var o = res.OutputStream) o.Write(bytes, 0, bytes.Length);
            }
            catch { try { res.Abort(); } catch { } }
        }

        private static void MediaPhoto(HttpListenerContext ctx, HttpListenerRequest req, bool post)
        {
            string op = GetQuery(req.Url.Query, "op") ?? "info";
            string p = GetQuery(req.Url.Query, "path") ?? "";
            var photo = PhotoLibrary.Find(p);
            object result;
            switch (op)
            {
                case "info":
                    if (photo == null) { result = new { ok = false, error = "图片不在图库里" }; break; }
                    result = new
                    {
                        ok = true,
                        item = new
                        {
                            path = photo.Path, name = photo.Name, folder = photo.Folder,
                            modified = photo.Modified, modifiedText = photo.ModifiedText,
                            size = photo.Size, sizeText = photo.SizeText,
                            width = photo.Width, height = photo.Height,
                            dimensions = photo.DimensionText,
                        }
                    };
                    break;
                case "rename":
                    if (!post) { result = new { ok = false, error = L10n.T("需要 POST") }; break; }
                    string err;
                    var dest = PhotoLibrary.Rename(p, GetQuery(req.Url.Query, "name") ?? "", out err);
                    var fresh = dest == null ? null : PhotoLibrary.Find(dest);
                    result = dest == null
                        ? (object)new { ok = false, error = err }
                        : new { ok = true, path = dest, name = fresh?.Name ?? Path.GetFileName(dest) };
                    break;
                case "delete":
                    if (!post) { result = new { ok = false, error = L10n.T("需要 POST") }; break; }
                    string derr;
                    bool done = PhotoLibrary.Delete(p, out derr);
                    result = done ? (object)new { ok = true } : new { ok = false, error = derr };
                    break;
                default:
                    result = new { ok = false, error = L10n.T("未知操作") };
                    break;
            }
            WriteJson(ctx.Response, Json(result));
        }

        /* ---------------- 歌词 ---------------- */

        private static void ServeMediaLyrics(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var song = MusicLibrary.Find(GetQuery(req.Url.Query, "path"));
            string text = "";
            if (song != null && !string.IsNullOrEmpty(song.LrcPath))
            {
                try { if (File.Exists(song.LrcPath)) text = File.ReadAllText(song.LrcPath, Encoding.UTF8); }
                catch { }
            }
            WriteJson(ctx.Response, Json(new { ok = true, text }));
        }

        /* ---------------- 喜欢 / 歌单 ---------------- */

        private static void MediaFavorite(HttpListenerContext ctx, HttpListenerRequest req)
        {
            string p = GetQuery(req.Url.Query, "path");
            if (MusicLibrary.Find(p) == null)
            { WriteJson(ctx.Response, L10n.T("{\"error\":\"文件不在曲库里\"}")); return; }
            bool on = PlaylistStore.ToggleFavorite(p);
            WriteJson(ctx.Response, Json(new { ok = true, favorite = on }));
        }

        private static void MediaPlaylist(HttpListenerContext ctx, HttpListenerRequest req)
        {
            string op = GetQuery(req.Url.Query, "op") ?? "";
            string id = GetQuery(req.Url.Query, "id") ?? "";
            string name = GetQuery(req.Url.Query, "name") ?? "";
            string p = GetQuery(req.Url.Query, "path") ?? "";
            object result;
            switch (op)
            {
                case "create":
                    if (string.IsNullOrWhiteSpace(name)) { result = new { ok = false, error = L10n.T("歌单名不能为空") }; break; }
                    var pl = PlaylistStore.Create(name.Trim());
                    result = new { ok = true, id = pl?.Id ?? "", name = pl?.Name ?? "" };
                    break;
                case "rename":
                    PlaylistStore.Rename(id, name.Trim());
                    result = new { ok = true };
                    break;
                case "delete":
                    PlaylistStore.Delete(id);
                    result = new { ok = true };
                    break;
                case "add":
                    if (MusicLibrary.Find(p) == null) { result = new { ok = false, error = "文件不在曲库里" }; break; }
                    PlaylistStore.AddTo(id, p);
                    result = new { ok = true };
                    break;
                case "remove":
                    PlaylistStore.RemoveFrom(id, p);
                    result = new { ok = true };
                    break;
                case "detail":
                    var songs = PlaylistStore.SongsOf(id).Select(SongDto).ToList();
                    result = new { ok = true, id, name = PlaylistStore.NameOf(id), songs };
                    break;
                default:
                    result = new { ok = false, error = L10n.T("未知操作") };
                    break;
            }
            WriteJson(ctx.Response, Json(result));
        }

        /* ---------------- 扫描 / 移出 / 目录 ---------------- */

        private static void MediaScan(HttpListenerContext ctx)
        {
            var task = MusicLibrary.ScanAsync();
            task.ContinueWith(t =>
            {
                try { System.Diagnostics.Debug.WriteLine("web media scan done"); } catch { }
            });
            WriteJson(ctx.Response, Json(new { ok = true, started = true }));
        }

        private static void MediaExclude(HttpListenerContext ctx, HttpListenerRequest req)
        {
            string op = GetQuery(req.Url.Query, "op") ?? "add";
            string p = GetQuery(req.Url.Query, "path") ?? "";
            if (op == "restoreall") MusicLibrary.RestoreAll();
            else if (op == "restore") MusicLibrary.Restore(p);
            else MusicLibrary.Exclude(p);
            WriteJson(ctx.Response, Json(new { ok = true }));
        }

        private static void MediaDirs(HttpListenerContext ctx, HttpListenerRequest req, bool post)
        {
            if (post)
            {
                string op = GetQuery(req.Url.Query, "op") ?? "";
                string dir = GetQuery(req.Url.Query, "dir") ?? "";
                bool on = (GetQuery(req.Url.Query, "on") ?? "1") == "1";
                switch (op)
                {
                    case "add":
                        {
                            // 添加时即可指定扫描类型：?audio=1&video=0&photo=0。
                            // 三个参数都不传时兼容旧调用，默认三类全开。
                            string av = GetQuery(req.Url.Query, "audio");
                            string vv = GetQuery(req.Url.Query, "video");
                            string pv = GetQuery(req.Url.Query, "photo");
                            if (av == null && vv == null && pv == null) MusicLibrary.AddDir(dir);
                            else MusicLibrary.AddDir(dir, av == "1", vv == "1", pv == "1");
                        }
                        break;
                    case "remove": MusicLibrary.RemoveDir(dir); break;
                    case "enable": MusicLibrary.SetDirEnabled(dir, on); break;
                    case "kind":
                        {
                            var cur = MusicLibrary.Dirs.FirstOrDefault(
                                x => string.Equals(x.Path, dir, StringComparison.OrdinalIgnoreCase));
                            var pv = GetQuery(req.Url.Query, "photo");
                            MusicLibrary.SetDirKind(dir,
                                (GetQuery(req.Url.Query, "audio") ?? "1") == "1",
                                (GetQuery(req.Url.Query, "video") ?? "1") == "1",
                                pv == null ? (cur?.ForPhoto ?? true) : pv == "1");
                        }
                        break;
                    case "filter":
                        MusicLibrary.FilterShortAudio = (GetQuery(req.Url.Query, "short") ?? "1") == "1";
                        MusicLibrary.SkipSystemSounds = (GetQuery(req.Url.Query, "system") ?? "1") == "1";
                        break;
                }
            }
            WriteJson(ctx.Response, Json(new
            {
                ok = true,
                dirs = MusicLibrary.Dirs.Select(d => new
                {
                    path = d.Path, label = d.Label, enabled = d.Enabled,
                    audio = d.ForAudio, video = d.ForVideo, photo = d.ForPhoto, builtIn = d.BuiltIn
                }).ToList()
            }));
        }

        /* ---------------- 在线音乐：搜索 / 流 / 下载 ---------------- */

        /// <summary>
        /// 在线搜索：默认走酷狗官方搜索，返回可直接播放的歌曲列表。
        /// 该接口只做搜索，不关心本机有没有这首歌；播放走 online/stream 流式转发。
        /// </summary>
        private static async void OnlineSearch(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");
            string q = GetQuery(req.Url.Query, "q") ?? "";
            int page = 1;
            int.TryParse(GetQuery(req.Url.Query, "page") ?? "1", out page);
            int tier = 1;
            int.TryParse(GetQuery(req.Url.Query, "tier") ?? "1", out tier);
            if (string.IsNullOrWhiteSpace(q))
            {
                WriteJson(res, Json(new { ok = true, items = new object[0], keyword = "", page = page, hasMoreFallback = false }));
                return;
            }
            try
            {
                // 逐级兜底：tier 决定本次用哪个音源（默认酷狗 → 歌曲宝），UI 不暴露音源名
                var songs = await MusicSources.SearchAsync(q.Trim(), page, tier);
                var items = songs.Select(s => OnlineMusic.ToDto(s, "standard")).ToList();
                WriteJson(res, Json(new { ok = true, items, keyword = q.Trim(), page,
                    count = items.Count, hasMoreFallback = MusicSources.HasFallback(tier) }));
            }
            catch (Exception ex)
            {
                WriteJson(res, Json(new { ok = false, error = ex.Message, items = new object[0], hasMoreFallback = MusicSources.HasFallback(tier) }));
            }
        }

        /// <summary>
        /// 在线歌词：按平台 + 歌曲 id（酷狗为 hash）+ 歌名/歌手/时长向酷狗取 LRC，
        /// 返回原始 LRC 文本，供网页端播放条 / 详情页渲染滚动歌词。
        /// 结果会落盘缓存（OnlineMusic.FetchLyricsAsync 内部处理），二次请求秒回。
        /// </summary>
        private static async void OnlineLyrics(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");
            string plat = GetQuery(req.Url.Query, "plat") ?? "kg";
            string id = GetQuery(req.Url.Query, "id") ?? "";
            string name = GetQuery(req.Url.Query, "name") ?? "";
            string artist = GetQuery(req.Url.Query, "artist") ?? "";
            int duration = 0;
            int.TryParse(GetQuery(req.Url.Query, "duration") ?? "0", out duration);
            if (string.IsNullOrEmpty(id) && string.IsNullOrWhiteSpace(name))
            {
                WriteJson(res, Json(new { ok = false, text = "" }));
                return;
            }
            try
            {
                var song = new Song
                {
                    IsOnline = true,
                    OnlinePlat = string.IsNullOrEmpty(plat) ? "kg" : plat,
                    OnlineId = id,
                    Title = name,
                    Artist = artist,
                    DurationMs = duration,
                };
                var path = await MusicSources.FetchLyricsAsync(song);
                string text = "";
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    text = System.IO.File.ReadAllText(path);
                WriteJson(res, Json(new { ok = !string.IsNullOrWhiteSpace(text), text }));
            }
            catch (Exception ex)
            {
                WriteJson(res, Json(new { ok = false, text = "", error = ex.Message }));
            }
        }

        /// <summary>
        /// 在线流式播放：server 端解析出第三方代理播放地址，再向该地址（会 302 到真实 MP3）
        /// 发起请求并把字节流回写给浏览器；透传 Range 头，支持拖动进度条。
        /// 这条链路对局域网（7790）与 App 内置 WebView（6800）都生效。
        /// </summary>
        private static void OnlineStream(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            string plat = GetQuery(req.Url.Query, "plat") ?? "kg";
            string id = GetQuery(req.Url.Query, "id") ?? "";
            string quality = GetQuery(req.Url.Query, "q") ?? "standard";
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");

            if (string.IsNullOrEmpty(id))
            {
                res.StatusCode = 400; WriteJson(res, L10n.T("{\"error\":\"缺少歌曲 id\"}")); return;
            }
            var song = new OnlineSong { Plat = plat, Id = id };
            // 命中直链缓存就直连 CDN：否则每次 Range（换歌、拖动进度）都要重新等一遍代理 302。
            string url = OnlineMusic.CachedDirectUrl(plat, id, quality);
            if (string.IsNullOrEmpty(url)) url = MusicSources.ResolvePlayUrl(song, quality);
            if (string.IsNullOrEmpty(url))
            {
                res.StatusCode = 502; WriteJson(res, L10n.T("{\"error\":\"无法解析播放地址\"}")); return;
            }

            HttpWebResponse up = null;
            try
            {
                var wreq = (HttpWebRequest)WebRequest.Create(url);
                wreq.Method = "GET";
                wreq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                wreq.Timeout = 30000;
                wreq.ReadWriteTimeout = 30000;
                wreq.AllowAutoRedirect = true;
                // 透传浏览器的 Range（拖动进度条 / 预读）
                var range = req.Headers["Range"];
                if (!string.IsNullOrEmpty(range))
                {
                    try { wreq.Headers.Add("Range", range); } catch { }
                }
                up = (HttpWebResponse)wreq.GetResponse();

                // 上游跟完 302 的最终地址（CDN）——记下来，后续 Range 直接命中，不再走代理
                try
                {
                    var final = up.ResponseUri?.AbsoluteUri;
                    if (!string.IsNullOrEmpty(final) &&
                        !string.Equals(final, url, StringComparison.OrdinalIgnoreCase))
                        OnlineMusic.RememberDirectUrl(plat, id, quality, final);
                }
                catch { }

                res.ContentType = string.IsNullOrEmpty(up.ContentType) ? "audio/mpeg" : up.ContentType;
                res.AddHeader("Accept-Ranges", "bytes");
                if (up.StatusCode == HttpStatusCode.PartialContent)
                {
                    res.StatusCode = 206;
                    var cr = up.Headers["Content-Range"];
                    if (!string.IsNullOrEmpty(cr)) res.AddHeader("Content-Range", cr);
                }
                else res.StatusCode = 200;
                if (up.ContentLength >= 0) res.ContentLength64 = up.ContentLength;

                using (var src = up.GetResponseStream())
                {
                    var buf = new byte[64 * 1024];
                    var outp = res.OutputStream;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        try { outp.Write(buf, 0, n); }
                        catch { break; }   // 客户端断开 → 结束
                    }
                    try { outp.Flush(); } catch { }
                }
                // 流式写完后关闭响应，否则播放器一直等待 EOF（卡住 / 无法拖动）
                try { res.Close(); } catch { }
            }
            catch (Exception ex)
            {
                try { res.StatusCode = 502; WriteJson(res, "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}"); }
                catch { try { res.Abort(); } catch { } }
            }
            finally
            {
                try { up?.Close(); } catch { }
            }
        }

        /// <summary>
        /// 下载在线歌曲到默认音乐目录（/storage/emulated/0/Music 或首个启用音乐检索的内置目录），
        /// 落盘后触发曲库重扫，使其立即出现在本地「音乐」列表。
        /// </summary>
        private static async void OnlineDownload(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");
            string plat = GetQuery(req.Url.Query, "plat") ?? "kg";
            string id = GetQuery(req.Url.Query, "id") ?? "";
            string quality = GetQuery(req.Url.Query, "q") ?? "standard";
            string name = GetQuery(req.Url.Query, "name") ?? "";
            string cover = GetQuery(req.Url.Query, "cover") ?? "";
            if (string.IsNullOrEmpty(id))
            {
                WriteJson(res, Json(new { ok = false, error = L10n.T("缺少歌曲 id") })); return;
            }
            try
            {
                var song = new OnlineSong { Plat = plat, Id = id };
                if (!string.IsNullOrEmpty(name)) song.Title = name;
                if (!string.IsNullOrEmpty(cover)) song.Cover = cover;
                string path = await OnlineMusic.DownloadAsync(song, quality, name);
                if (string.IsNullOrEmpty(path))
                    WriteJson(res, Json(new { ok = false, error = L10n.T("下载失败（网络或存储不可用）") }));
                else
                    WriteJson(res, Json(new { ok = true, path }));
            }
            catch (Exception ex)
            {
                WriteJson(res, Json(new { ok = false, error = ex.Message }));
            }
        }
    }
}

// ============================================================
// 在线影视 HTTP 接口（WebServer 的 partial）
//
//   /api/media/vod/config            读配置 / 站点状态
//   /api/media/vod/config    POST    切模式、增删订阅、启停站点、刷新、清缓存
//   /api/media/vod/sites            站点列表
//   /api/media/vod/cats             聚合分类
//   /api/media/vod/home             首页 / 分页（缓存优先）
//   /api/media/vod/search           搜索（联网 wd + 本地预抓缓存）
//   /api/media/vod/detail           详情 + 线路 + 剧集
//   /api/media/vod/resolve          播放页 → 直链
//   /api/media/vod/history          观看记录（带封面，卡片瀑布墙用）
//   /api/media/vod/poster?u=        海报代理（磁盘缓存）
//   /api/media/vod/stream?u=&r=     播放代理：注入 UA/Referer、重写 m3u8、透传 Range
//   /api/media/vod/share    POST    网页端分享：同时生成外源直链 + 本机代理地址
//   /api/media/vod/watchtogether    「一起看」状态镜像（GET 跟随端轮询 / POST 宿主机上报）
//
// 播放代理是整套方案的关键：VideoView / 浏览器都不允许自定义请求头，而采集站
// CDN 校验 UA/Referer；m3u8 里的分片又是相对路径，只转发清单文本会让播放器
// 直连 CDN 再次丢失请求头（网页端还会撞 CORS）。所以清单逐行重写成代理地址。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using AhoChatA.Vod;

namespace AhoChatA
{
    public sealed partial class WebServer
    {
        private static int _vodWarmStarted;

        /* ---------------- m3u8 改写结果内存缓存 ----------------
         * 播放器会反复请求清单（重试 / 拖动 / 分段子清单），命中即秒回；
         * 30s TTL 足以覆盖一次起播与短暂拖动，过期后由代理重新拉取。 */
        private sealed class M3u8CacheEntry { public string Text; public long Ts; }
        private static readonly Dictionary<string, M3u8CacheEntry> _m3u8Cache =
            new Dictionary<string, M3u8CacheEntry>(StringComparer.Ordinal);
        private static readonly object _m3u8Lock = new object();
        private const int M3u8CacheMax = 96;
        private const long M3u8CacheTtlMs = 30000;

        private static long VodNowMs
        {
            get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        }

        private static bool TryM3u8Cache(string key, out string text)
        {
            lock (_m3u8Lock)
            {
                M3u8CacheEntry e;
                if (_m3u8Cache.TryGetValue(key, out e) && (VodNowMs - e.Ts < M3u8CacheTtlMs)) { text = e.Text; return true; }
                text = null; return false;
            }
        }

        private static void PutM3u8Cache(string key, string text)
        {
            lock (_m3u8Lock)
            {
                if (_m3u8Cache.Count >= M3u8CacheMax)
                {
                    var now = VodNowMs;
                    var expired = new List<string>();
                    foreach (var kv in _m3u8Cache) if (now - kv.Value.Ts >= M3u8CacheTtlMs) expired.Add(kv.Key);
                    foreach (var k in expired) _m3u8Cache.Remove(k);
                    if (_m3u8Cache.Count >= M3u8CacheMax) _m3u8Cache.Clear();
                }
                _m3u8Cache[key] = new M3u8CacheEntry { Text = text, Ts = VodNowMs };
            }
        }

        private static string TrimLen(string s, int max)
        {
            var v = (s ?? "").Trim();
            return v.Length <= max ? v : v.Substring(0, max);
        }

        /// <summary>
        /// 判断一个 URL 本身是否 m3u8 清单：只看 ?/# 之前的路径，
        /// 避免查询串里出现 .m3u8 字样就把普通分片误判成清单。
        /// </summary>
        private static bool IsM3u8Url(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var s = url;
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            return s.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>网页端在线影视「分享」请求体（字段与 VideoShare.BuildOnline 对齐）。</summary>
        private sealed class VodShareReq
        {
            public string title = "";
            public string url = "";
            public string poster = "";
            public string watchUrl = "";
            public string vkey = "";
            public string episodeName = "";
            public int episodeIndex = 0;
            public long duration = 0;
        }

        // ---------------- 统一下载中心接口（音乐 / 视频 / 小说 都由此接管） ----------------
        private static void VodDownload(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");
            string op = GetQuery(req.Url.Query, "op") ?? "";
            try
            {
                // op=fetch：把已下载落盘的文件直接以附件流输出，交给浏览器下载器
                // （用户要求：web 端下载由浏览器发起并写入浏览器下载目录，而非指令 app 再下）
                if (op == "fetch")
                {
                    string fid = GetQuery(req.Url.Query, "id") ?? "";
                    var fetchTask = AhoChatA.Vod.DownloadCenter.List().FirstOrDefault(t => t.Id == fid);
                    string fp = fetchTask?.OutputPath ?? "";
                    if (string.IsNullOrEmpty(fp) || !System.IO.File.Exists(fp))
                    { res.StatusCode = 404; WriteJson(res, Json(new { ok = false, error = L10n.T("文件不存在或未完成") })); return; }
                    try
                    {
                        var fi = new System.IO.FileInfo(fp);
                        res.ContentType = "application/octet-stream";
                        res.ContentLength64 = fi.Length;
                        res.AddHeader("Content-Disposition", "attachment; filename*=UTF-8''" + System.Uri.EscapeDataString(fi.Name));
                        using (var fs = System.IO.File.OpenRead(fp)) fs.CopyTo(res.OutputStream);
                    }
                    catch (Exception ex) { Android.Util.Log.Warn("Mchat", "fetch 下载失败：" + ex.Message); }
                    return;
                }
                if (op == "list")
                {
                    AhoChatA.Vod.DownloadCenter.LoadHistory();
                    var list = AhoChatA.Vod.DownloadCenter.List()
                        .Select(t => new
                        {
                            id = t.Id, type = t.Type, title = t.Title, status = t.Status,
                            progress = t.Progress, error = t.Error, outputPath = t.OutputPath,
                            dirHint = t.DirHint, total = t.Total, done = t.Done
                        }).ToArray();
                    WriteJson(res, Json(new { ok = true, list }));
                    return;
                }
                if (op == "retry")
                {
                    string id = GetQuery(req.Url.Query, "id") ?? "";
                    bool ok = AhoChatA.Vod.DownloadCenter.Retry(id);
                    WriteJson(res, Json(new { ok }));
                    return;
                }
                if (op == "clear")
                {
                    AhoChatA.Vod.DownloadCenter.ClearDone();
                    WriteJson(res, Json(new { ok = true }));
                    return;
                }
                // op=start
                string type = GetQuery(req.Url.Query, "type") ?? "video";
                if (type == "video")
                {
                    string url = GetQuery(req.Url.Query, "url") ?? "";
                    string title = GetQuery(req.Url.Query, "title") ?? "";
                    if (string.IsNullOrEmpty(url))
                    { WriteJson(res, Json(new { ok = false, error = L10n.T("缺少视频 m3u8 地址") })); return; }
                    var dup = AhoChatA.Vod.DownloadCenter.List().FirstOrDefault(x =>
                        (x.Status == "queued" || x.Status == "downloading" || x.Status == "merging")
                        && string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
                    if (dup != null)
                    { WriteJson(res, Json(new { ok = true, duplicate = true, id = dup.Id, status = dup.Status })); return; }
                    var t = AhoChatA.Vod.DownloadCenter.QueueVideo(url, title);
                    WriteJson(res, Json(new { ok = true, id = t.Id, status = t.Status }));
                    return;
                }
                if (type == "music")
                {
                    string plat = GetQuery(req.Url.Query, "plat") ?? "kg";
                    string id = GetQuery(req.Url.Query, "id") ?? "";
                    string name = GetQuery(req.Url.Query, "name") ?? "";
                    string cover = GetQuery(req.Url.Query, "cover") ?? "";
                    string artist = GetQuery(req.Url.Query, "artist") ?? "";
                    string dur = GetQuery(req.Url.Query, "duration") ?? "";
                    if (string.IsNullOrEmpty(id))
                    { WriteJson(res, Json(new { ok = false, error = L10n.T("缺少歌曲 id") })); return; }
                    var s = new AhoChatA.Music.OnlineSong { Plat = plat, Id = id, Title = name, Artist = artist };
                    if (!string.IsNullOrEmpty(cover)) s.Cover = cover;
                    if (int.TryParse(dur, out int dms)) s.DurationMs = dms;
                    var dup = AhoChatA.Vod.DownloadCenter.List().FirstOrDefault(x =>
                        x.Type == "music"
                        && (x.Status == "queued" || x.Status == "downloading" || x.Status == "merging")
                        && x.MusicSong != null
                        && string.Equals(x.MusicSong.Id, id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.MusicSong.Plat, plat, StringComparison.OrdinalIgnoreCase));
                    if (dup != null)
                    { WriteJson(res, Json(new { ok = true, duplicate = true, id = dup.Id, status = dup.Status })); return; }
                    var t = AhoChatA.Vod.DownloadCenter.QueueMusic(s, "standard", name);
                    WriteJson(res, Json(new { ok = true, id = t.Id, status = t.Status }));
                    return;
                }
                if (type == "novel")
                {
                    string id = GetQuery(req.Url.Query, "id") ?? "";
                    string title = GetQuery(req.Url.Query, "title") ?? "";
                    if (string.IsNullOrEmpty(id))
                    { WriteJson(res, Json(new { ok = false, error = L10n.T("缺少书籍 id") })); return; }
                    var t = AhoChatA.Vod.DownloadCenter.QueueNovel(id, title);
                    WriteJson(res, Json(new { ok = true, id = t.Id, status = t.Status }));
                    return;
                }
                WriteJson(res, Json(new { ok = false, error = L10n.T("未知下载类型") }));
            }
            catch (Exception ex)
            {
                WriteJson(res, Json(new { ok = false, error = ex.Message }));
            }
        }

        // 下载中心页面（与 /hub 同风格，纯服务端生成 HTML + 内联轮询 JS）
        private static void ServeDownloadPage(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            var h = new StringBuilder();
            h.Append("<!doctype html><html><head><meta charset=utf-8>");
            h.Append("<meta name=viewport content='width=device-width,initial-scale=1'>");
            h.Append(L10n.T("<title>下载中心</title><style>"));
            h.Append("body{font-family:system-ui,sans-serif;background:#0f1115;color:#e6e6e6;margin:0}");
            h.Append("header{padding:16px 20px;background:#171a21;border-bottom:1px solid #262b36}");
            h.Append("h1{margin:0;font-size:20px}");
            h.Append(".sub{color:#8b93a1;font-size:13px;margin-top:4px}");
            h.Append(".list{padding:16px 20px;display:flex;flex-direction:column;gap:12px}");
            h.Append(".card{background:#171a21;border:1px solid #262b36;border-radius:10px;padding:14px}");
            h.Append(".row{display:flex;align-items:center;justify-content:space-between;gap:10px}");
            h.Append(".title{font-weight:600}");
            h.Append(".tag{font-size:12px;padding:2px 8px;border-radius:999px;background:#222836;color:#9fb3ff}");
            h.Append(".bar{height:8px;background:#222836;border-radius:999px;overflow:hidden;margin-top:10px}");
            h.Append(".bar>i{display:block;height:100%;background:linear-gradient(90deg,#4f8cff,#7c5cff)}");
            h.Append(".meta{color:#8b93a1;font-size:12px;margin-top:8px;word-break:break-all}");
            h.Append(".err{color:#ff8080;font-size:12px;margin-top:6px}");
            h.Append("button{cursor:pointer;border:1px solid #36405a;background:#1d2430;color:#e6e6e6;border-radius:8px;padding:6px 12px;font-size:13px}");
            h.Append("button:hover{border-color:#4f8cff}");
            h.Append("</style></head><body>");
            h.Append(L10n.T("<header><h1>下载中心</h1><div class=sub>离线保存音乐 / 视频 / 小说 · 失败可重试 · 完成后提示保存位置</div></header>"));
            h.Append(L10n.T("<div class=list id=list><div class=meta>加载中…</div></div>"));
            h.Append("<script>");
            h.Append("function esc(s){return String(s||'').replace(/[&<>\"']/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}[c]});}");
            h.Append(L10n.T("function st(s){return {queued:'排队中',downloading:'下载中',merging:'合并中',done:'完成',failed:'失败',canceled:'已取消'}[s]||s;}"));
            h.Append(L10n.T("function tp(s){return {video:'视频',music:'音乐',novel:'小说'}[s]||s;}"));
            h.Append("function refresh(){fetch('/api/media/vod/download?op=list').then(function(r){return r.json();}).then(function(d){");
            h.Append(L10n.T("var box=document.getElementById('list');if(!d.ok||!d.list||!d.list.length){box.innerHTML='<div class=meta>暂无下载任务</div>';return;}"));
            h.Append("var html=d.list.map(function(t){var p=t.progress||0;var s='<div class=card><div class=row><span class=title>'+esc(t.title)+'</span><span class=tag>'+st(t.status)+'</span></div>';");
            h.Append("s+='<div class=bar><i style=\"width:'+p+'%\"></i></div>';");
            h.Append(L10n.T("s+='<div class=meta>类型: '+tp(t.type)+(t.total?(' · 切片 '+t.done+'/'+t.total):'')+'</div>';"));
            h.Append("if(t.dirHint)s+='<div class=meta>📁 '+esc(t.dirHint)+'</div>';");
            h.Append(L10n.T("if(t.outputPath){s+='<div class=meta>✅ 已保存: '+esc(t.outputPath)+'</div>';"));
            h.Append(L10n.T("s+='<button style=\"margin-top:8px\" onclick=\"window.open(\\'/api/media/vod/download?op=fetch&id=\\'+encodeURIComponent(t.id)+\\'\\',\\'_blank\\')\">⬇ 浏览器下载</button>';}"));
            h.Append("if(t.error)s+='<div class=err>⚠ '+esc(t.error)+'</div>';");
            h.Append("if(t.status==='failed')s+='<button onclick=\"retry(\\''+t.id+'\\')\">重试</button>';");
            h.Append("s+='</div>';return s;}).join('');box.innerHTML=html;");
            h.Append("});}");
            h.Append("function retry(id){fetch('/api/media/vod/download?op=retry&id='+encodeURIComponent(id)).then(refresh);}");
            h.Append("refresh();setInterval(refresh,2000);");
            h.Append("</script></body></html>");
            WriteText(res, h.ToString());
        }

        private static void ServeVodApi(HttpListenerContext ctx, HttpListenerRequest req, string p, bool post)
        {
            var res = ctx.Response;
            string op = p == "vod" ? "" : p.Substring(4).Trim('/').ToLowerInvariant();
            if (op == "stream") { VodStream(ctx, req); return; }
            if (op == "poster") { VodPoster(ctx, req); return; }

            try
            {
                if (op == "config")
                {
                    if (post) { VodConfigPost(ctx, req); return; }
                    var c = VodConfig.Load();
                    WriteJson(res, Json(new
                    {
                        ok = true,
                        status = VodConfig.Status(),
                        mode = c.Mode,
                        subs = c.Subs,
                        custom = c.CustomJson ?? "",
                        maxPages = c.MaxPages
                    }));
                    return;
                }
                if (op == "sites")
                {
                    WriteJson(res, Json(new { ok = true, sites = VodConfig.Sites() }));
                    return;
                }
                if (op == "cats")
                {
                    // partial=true 表示这次返回的是内置骨架，真实分类正在后台聚合，
                    // 前端据此延时重拉一次；分类弹层永远不会卡住等 10 秒网络。
                    bool partial = !VodApi.CatsReady();
                    var cats = VodApi.BuildCats(VodConfig.EnabledSites());
                    WriteJson(res, Json(new { ok = true, partial = partial, cats = cats.Select(x => new { name = x.Name, hits = x.Hits }) }));
                    return;
                }
                if (op == "home")
                {
                    int page; int.TryParse(GetQuery(req.Url.Query, "page") ?? "1", out page);
                    var cat = GetQuery(req.Url.Query, "cat") ?? "";
                    bool cached;
                    var list = VodApi.Home(page, cat, out cached);
                    StartVodWarm();
                    WriteJson(res, Json(new { ok = true, page = page, cat = cat, cached = cached, items = list.Select(VodDto) }));
                    return;
                }
                if (op == "search")
                {
                    var kw = GetQuery(req.Url.Query, "wd") ?? "";
                    int page; int.TryParse(GetQuery(req.Url.Query, "page") ?? "1", out page);
                    int okSites;
                    var list = VodApi.Search(kw, page, out okSites);
                    WriteJson(res, Json(new { ok = true, wd = kw, okSites = okSites, items = list.Select(VodDto) }));
                    return;
                }
                if (op == "detail")
                {
                    var site = GetQuery(req.Url.Query, "site") ?? "";
                    var id = GetQuery(req.Url.Query, "id") ?? "";
                    var name = GetQuery(req.Url.Query, "name") ?? "";
                    int line; int.TryParse(GetQuery(req.Url.Query, "line") ?? "0", out line);
                    var item = VodApi.Detail(site, id, name);
                    if (item == null) { WriteJson(res, "{\"ok\":false,\"error\":\"站点不存在\"}"); return; }
                    var sources = VodApi.ParseSources(item.PlayFrom, item.PlayUrl);
                    if (sources.Count > 0)
                    {
                        if (line < 0 || line >= sources.Count) line = 0;
                        // 解析 + 预热放到后台，详情页秒开；播放器真正起播时直链早已在缓存里
                        var idx = line;
                        Task.Run(() => { try { VodApi.ResolveEpisodes(sources[idx]); VodApi.WarmEpisodes(sources[idx]); } catch { } });
                    }
                    WriteJson(res, Json(new
                    {
                        ok = true,
                        item = VodDto(item),
                        line = line,
                        sources = sources.Select(s => new { name = s.Name, count = s.Episodes.Count, direct = s.DirectCount }),
                        episodes = sources.Count == 0 ? new object[0] : sources[line].Episodes.Select(e => new
                        {
                            name = e.Name,
                            url = e.Url,
                            direct = VodApi.IsDirectPlayUrl(e.PlayUrl),
                            play = VodApi.StreamUrl(e.PlayUrl, "", false)
                        }).ToArray()
                    }));
                    return;
                }
                if (op == "resolve")
                {
                    var u = GetQuery(req.Url.Query, "u") ?? "";
                    var real = VodApi.ResolvePlayUrl(u);
                    WriteJson(res, Json(new { ok = true, real = real, play = VodApi.StreamUrl(real, "", false) }));
                    return;
                }
                if (op == "download")
                {
                    VodDownload(ctx, req);
                    return;
                }
                if (op == "watchtogether")
                {
                    // 局域网跟随端会跨域 GET，宿主机上报是同源 POST；两者都放通。
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    res.AddHeader("Cache-Control", "no-store");
                    if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        res.StatusCode = 204; res.ContentLength64 = 0; res.Close(); return;
                    }
                    if (post)
                    {
                        string body = ReadVodBody(req);
                        var st = JsonConvert.DeserializeObject<WatchTogether.State>(
                            string.IsNullOrWhiteSpace(body) ? "{}" : body)
                            ?? new WatchTogether.State();
                        // 宿主机 LAN 地址由服务端权威下发，网页端经 localhost/adb forward
                        // 打开时上报的 location.origin 对局域网设备不可达。
                        st.WatchHost = ListenTogether.HostBase();
                        // 统一用服务端时钟打时间戳，避免网页端本机时钟偏差触发旧帧误判
                        st.Ts = WatchTogether.NowMs;
                        if ((GetQuery(req.Url.Query, "op") ?? "publish") == "stop")
                        {
                            WatchTogether.Stop(st == null ? "" : st.CardId);
                            WriteJson(res, "{\"ok\":true}");
                            return;
                        }
                        string cardId = WatchTogether.Publish(st);
                        WriteJson(res, Json(new { ok = true, cardId = cardId, ts = WatchTogether.NowMs }));
                        return;
                    }
                    WriteJson(res, Json(WatchTogether.Snapshot()));
                    return;
                }
                if (op == "share")
                {
                    // 网页端「分享」：一次性生成同时带「外源直链」与「本机代理」的视频卡片
                    res.AddHeader("Access-Control-Allow-Origin", "*");
                    res.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    res.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
                    if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        res.StatusCode = 204; res.ContentLength64 = 0; res.Close(); return;
                    }
                    if (!post) { WriteJson(res, "{\"ok\":false,\"error\":\"POST required\"}"); return; }
                    var q = JsonConvert.DeserializeObject<VodShareReq>(ReadVodBody(req)) ?? new VodShareReq();
                    // 网页 POST 的字段会进入聊天卡片，先做长度与空值归一，避免异常长文本刷屏
                    q.title = TrimLen(q.title, 300);
                    q.url = TrimLen(q.url, 2000);
                    q.poster = TrimLen(q.poster, 2000);
                    q.watchUrl = TrimLen(q.watchUrl, 2000);
                    q.vkey = TrimLen(q.vkey, 300);
                    q.episodeName = TrimLen(q.episodeName, 200);
                    var fields = VideoShare.BuildOnline(q.title, q.url, q.poster, q.watchUrl,
                        q.vkey, q.episodeName, q.episodeIndex, q.duration);
                    var bridge = MchatBridge.Current ?? CoreHost.Bridge;
                    if (bridge == null) { WriteJson(res, "{\"ok\":false,\"error\":\"服务未就绪\"}"); return; }
                    string cardId = bridge.SendVideoCard(fields);
                    WriteJson(res, Json(new { ok = true, cardId = cardId }));
                    return;
                }
                if (op == "history")
                {
                    if (post)
                    {
                        string body = ReadVodBody(req);
                        string hop = GetQuery(req.Url.Query, "op") ?? "save";
                        if (hop == "clear") VodCache.ClearHistory();
                        else if (!string.IsNullOrWhiteSpace(body))
                        {
                            try
                            {
                                var r = JsonConvert.DeserializeObject<VodWatchRecord>(body);
                                VodCache.AddHistory(r);
                            }
                            catch { }
                        }
                        WriteJson(res, "{\"ok\":true}");
                        return;
                    }
                    // 统一输出 camelCase，网页端不必再猜 C# 字段大小写；
                    // poster 给本地代理地址，卡片封面直接复用宿主机已缓存的海报字节。
                    var his = VodCache.History();
                    WriteJson(res, Json(new
                    {
                        ok = true,
                        items = his.Select(r => new
                        {
                            key = r.Key, name = r.Name, pic = r.Pic,
                            poster = VodApi.PosterUrl(r.Pic, false),
                            siteKey = r.SiteKey, siteName = r.SiteName, remarks = r.Remarks,
                            episodeName = r.EpisodeName, episodeIndex = r.EpisodeIndex,
                            positionMs = r.PositionMs, durationMs = r.DurationMs, lastTs = r.LastTs,
                            urls = r.Urls, names = r.Names
                        })
                    }));
                    return;
                }
                WriteJson(res, "{\"ok\":false,\"error\":\"unknown vod api\"}");
            }
            catch (Exception ex)
            {
                try { WriteJson(res, Json(new { ok = false, error = ex.Message })); }
                catch { try { res.Abort(); } catch { } }
            }
        }

        private static object VodDto(VodItem it)
        {
            if (it == null) return null;
            return new
            {
                id = it.Id,
                site = it.SiteKey,
                siteName = it.SiteName,
                name = it.Name,
                pic = it.Pic,
                // 无海报的条目也给一个生成封面地址，瀑布墙不会出现空白卡
                poster = string.IsNullOrWhiteSpace(it.Pic)
                    ? "/api/media/vod/poster?n=" + System.Uri.EscapeDataString(it.Name ?? "")
                    : VodApi.PosterUrl(it.Pic, false),
                remarks = it.Remarks,
                type = it.TypeName,
                year = it.Year,
                area = it.Area,
                desc = it.Desc,
                actor = it.Actor,
                score = it.Score,
                variants = it.Variants == null ? new object[0] : it.Variants.Select(v => new
                {
                    site = v.SiteKey, siteName = v.SiteName, id = v.Id, name = v.Name, pic = v.Pic
                }).ToArray()
            };
        }

        private static string ReadVodBody(HttpListenerRequest req)
        {
            try
            {
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        /* ---------------- 配置操作 ---------------- */

        private static void VodConfigPost(HttpListenerContext ctx, HttpListenerRequest req)
        {
            string op = GetQuery(req.Url.Query, "op") ?? "";
            string body = ReadVodBody(req);
            var c = VodConfig.Load();
            switch (op)
            {
                case "mode":
                    var m = GetQuery(req.Url.Query, "v") ?? "builtin";
                    if (m == "builtin" || m == "subs" || m == "custom") c.Mode = m;
                    break;
                case "sub":
                    var url = GetQuery(req.Url.Query, "url") ?? "";
                    var name = GetQuery(req.Url.Query, "name") ?? L10n.T("自定义订阅");
                    bool on = (GetQuery(req.Url.Query, "on") ?? "1") == "1";
                    var hit = c.Subs.Find(x => x.Url == url);
                    if (hit != null) { hit.Enabled = on; hit.Name = name; }
                    else c.Subs.Add(new VodSub { Url = url, Name = name, Enabled = on });
                    break;
                case "subdel":
                    c.Subs.RemoveAll(x => x.Url == (GetQuery(req.Url.Query, "url") ?? ""));
                    break;
                case "custom":
                    c.CustomJson = string.IsNullOrWhiteSpace(body) ? (GetQuery(req.Url.Query, "json") ?? "") : body;
                    if (!string.IsNullOrWhiteSpace(c.CustomJson)) c.Mode = "custom";
                    break;
                case "site":
                    VodConfig.SetEnabled(GetQuery(req.Url.Query, "key") ?? "",
                        (GetQuery(req.Url.Query, "on") ?? "1") == "1");
                    break;
                case "refresh":
                    Task.Run(() => { try { VodConfig.RefreshSubs(); VodCache.ClearAll(); } catch { } });
                    WriteJson(ctx.Response, "{\"ok\":true,\"refreshing\":true}");
                    return;
                case "clear":
                    VodConfig.ClearCache();
                    break;
            }
            VodConfig.Save(c);
            WriteJson(ctx.Response, Json(new { ok = true, status = VodConfig.Status() }));
        }

        /// <summary>首页命中缓存后，后台继续预抓分页，让本地搜索覆盖更多片名。</summary>
        private static void StartVodWarm()
        {
            if (System.Threading.Interlocked.Exchange(ref _vodWarmStarted, 1) != 0) return;
            Task.Run(() =>
            {
                // 预热不参与首屏竞争，延迟起步，让首屏请求与慢站补齐先完成
                try { System.Threading.Thread.Sleep(1200); } catch { }
                try { VodApi.WarmPages(VodConfig.Load().MaxPages, null); }
                catch { }
                finally { System.Threading.Interlocked.Exchange(ref _vodWarmStarted, 0); }
            });
        }

        /* ---------------- 海报代理 ---------------- */

        private static void VodPoster(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            try
            {
                var u = GetQuery(req.Url.Query, "u") ?? "";
                var n = GetQuery(req.Url.Query, "n") ?? "";
                res.AddHeader("Access-Control-Allow-Origin", "*");
                // 无海报 / 非法地址 / CDN 拉取失败：统一回生成封面，不再让前端隐藏图片
                if (!SafeRemote(u)) { WriteGeneratedCover(res, n.Length > 0 ? n : u, true); return; }
                var bytes = VodApi.PosterBytes(u);
                if (bytes == null || bytes.Length == 0)
                { WriteGeneratedCover(res, n.Length > 0 ? n : u, true); return; }
                res.ContentType = ImageMimeOf(bytes);
                // 与视频缓存统一：海报磁盘缓存同样 3 天（259200 秒）
                res.AddHeader("Cache-Control", "public, max-age=259200");
                res.ContentLength64 = bytes.Length;
                using (var o = res.OutputStream) o.Write(bytes, 0, bytes.Length);
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        /* ---------------- 播放代理流 ---------------- */

        /// <summary>SSRF 防护：只放行公网 http/https，拒绝回环、内网、局域网与本地域名。</summary>
        private static bool SafeRemote(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
            try
            {
                var u = new System.Uri(url);
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
                var host = u.Host;
                if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host.EndsWith(".local")) return false;
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(host, out ip))
                {
                    var b = ip.GetAddressBytes();
                    if (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] == 169 && b[1] == 254
                        || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                        || b[0] == 192 && b[1] == 168) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /* ---------------- 视频分片磁盘缓存（统一 3 天） ----------------
         * 宿主机自己看片 / 局域网设备「一起看」都会经过这条代理，命中即直接回本地字节，
         * 不再重复向 CDN 拉分片 —— 这就是「一起看采用本设备原有加载的视频缓存」。
         * 清单(m3u8)不落盘，仍走 30 秒内存缓存（清单会随 CDN 变动）。 */
        private static string VodSegDir()
        {
            var p = System.IO.Path.Combine(VodConfig.CacheRoot(), "seg");
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }

        private static string VodSegKey(string url)
        {
            using (var md5 = MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
                var sb = new StringBuilder();
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool TryVodSeg(string url, out byte[] data, out string ct)
        {
            data = null; ct = null;
            try
            {
                var f = System.IO.Path.Combine(VodSegDir(), VodSegKey(url) + ".bin");
                if (!File.Exists(f)) return false;
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-VideoCacheProxy.CacheExpiryDays)) return false;
                data = File.ReadAllBytes(f);
                var c = f + ".ct";
                ct = File.Exists(c) ? File.ReadAllText(c) : "";
                if (string.IsNullOrWhiteSpace(ct)) ct = "video/mp2t";
                return data.Length > 0;
            }
            catch { return false; }
        }

        private static void PutVodSeg(string url, byte[] data, string ct)
        {
            try
            {
                if (data == null || data.Length == 0 || data.Length > 32L * 1024 * 1024) return;
                var f = System.IO.Path.Combine(VodSegDir(), VodSegKey(url) + ".bin");
                File.WriteAllBytes(f, data);
                try { File.WriteAllText(f + ".ct", ct ?? ""); } catch { }
            }
            catch { }
        }

        /// <summary>
        /// 复用 App 端 ExoPlayer 已经落盘的原生视频缓存（CacheDir/vod_disk/&lt;md5(url)&gt;.data）。
        /// VideoCacheProxy 的键就是 md5(最终直链)，与 VodSegKey(target) 完全一致，所以宿主机
        /// 自己看片时下载过的字节，局域网「一起看」的 Web 跟随端可以直接命中、不再回源 CDN。
        /// 只在 .meta 记录的长度已完整落盘时才复用，避免把仍在预取的稀疏分块当成完整文件。
        /// 支持 Range（浏览器播 mp4 时会发），未命中时返回 false 让 VodStream 正常回源。
        /// </summary>
        private static bool TryVodAppCache(string target, HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                // m3u8 清单必须走 RewriteM3u8 把分片改写成宿主机代理，
                // 直接吐 App 端缓存里的原始清单会让浏览器绕过代理直连 CDN
                if (IsM3u8Url(target)) return false;

                string key = VodSegKey(target);
                string data = System.IO.Path.Combine(VodConfig.DiskCacheRoot(), key + ".data");
                string meta = System.IO.Path.Combine(VodConfig.DiskCacheRoot(), key + ".meta");
                if (!File.Exists(data) || !File.Exists(meta)) return false;
                if (File.GetLastWriteTimeUtc(data) < DateTime.UtcNow.AddDays(-VideoCacheProxy.CacheExpiryDays)) return false;

                var m = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(meta));
                long len = (long)m["length"];
                string ct = (string)m["ct"] ?? "application/octet-stream";
                if (ct.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0
                    || ct.IndexOf("m3u8", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (len <= 0 || new FileInfo(data).Length < len) return false;

                long from = 0, to = len - 1;
                bool partial = false;
                string range = req.Headers["Range"];
                if (!string.IsNullOrEmpty(range))
                {
                    var rm = Regex.Match(range, @"bytes=(\d+)-(\d*)", RegexOptions.IgnoreCase);
                    if (!rm.Success) return false;
                    long.TryParse(rm.Groups[1].Value, out from);
                    if (rm.Groups[2].Success && rm.Groups[2].Value.Length > 0)
                        long.TryParse(rm.Groups[2].Value, out to);
                    if (from < 0 || from >= len) return false;
                    if (to >= len || to < from) to = len - 1;
                    partial = true;
                }
                // 预热请求不该由缓存满足（它本来就是为了触发落盘）
                if ((GetQuery(req.Url.Query, "warm") ?? "") == "1") return false;

                long count = to - from + 1;
                res.StatusCode = partial ? 206 : 200;
                res.ContentType = string.IsNullOrWhiteSpace(ct) ? "application/octet-stream" : ct;
                res.AddHeader("Accept-Ranges", "bytes");
                res.AddHeader("Cache-Control", "public, max-age=259200");
                res.AddHeader("X-Vod-Cache", "app-disk");
                if (partial) res.AddHeader("Content-Range", "bytes " + from + "-" + to + "/" + len);
                res.ContentLength64 = count;

                try
                {
                    using (var fs = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Seek(from, System.IO.SeekOrigin.Begin);
                        var buf = new byte[64 * 1024];
                        long left = count;
                        var outp = res.OutputStream;
                        while (left > 0)
                        {
                            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) break;
                            outp.Write(buf, 0, n);
                            left -= n;
                        }
                        outp.Flush();
                    }
                }
                catch
                {
                    // 响应已经开始写，不能再回源，直接断连让播放器重试
                    try { res.Abort(); } catch { }
                }
                return true;
            }
            catch { return false; }
        }

        private static void VodStream(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Headers", "Range, Content-Type");
            res.AddHeader("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            res.AddHeader("Access-Control-Expose-Headers", "Content-Range, Accept-Ranges, Content-Length, X-Vod-Cache");
            // 局域网跟随端带 Range 时浏览器会先发 CORS 预检（预检不一定带 u 参数，
            // 所以必须在 SafeRemote 校验之前放通）
            if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            { res.StatusCode = 204; res.ContentLength64 = 0; res.Close(); return; }

            string url = GetQuery(req.Url.Query, "u") ?? "";
            string referer = GetQuery(req.Url.Query, "r") ?? "";
            bool warm = (GetQuery(req.Url.Query, "warm") ?? "") == "1";
            if (!SafeRemote(url)) { res.StatusCode = 400; try { res.Close(); } catch { } return; }
            if (referer.Length > 512 || referer.IndexOf('\r') >= 0 || referer.IndexOf('\n') >= 0) referer = "";

            // 非直链（播放页 HTML）先在本地解析成真实 m3u8/mp4，避免把网页当媒体喂给播放器。
            // 缓存键一律用「最终直链」：宿主机可能用播放页地址、Web 跟随端用已解析直链，
            // 归一化之后两端才会命中同一份 3 天缓存。
            string target = url;
            if (!VodApi.IsDirectPlayUrl(target))
            {
                var r = VodApi.ResolvePlayUrl(target);
                if (!string.IsNullOrWhiteSpace(r)) target = r;
            }

            // 浏览器实际访问宿主机用的地址。网关转发会把 Host 改成 127.0.0.1:6789，
            // 所以只能由前端显式带 h=；校验后只保留 scheme://host:port。
            string hostHint = GetQuery(req.Url.Query, "h") ?? "";
            if (hostHint.Length > 200) hostHint = "";
            if (!string.IsNullOrEmpty(hostHint))
            {
                System.Uri hu;
                if (!System.Uri.TryCreate(hostHint, UriKind.Absolute, out hu)
                    || (hu.Scheme != Uri.UriSchemeHttp && hu.Scheme != Uri.UriSchemeHttps))
                    hostHint = "";
                else hostHint = hu.GetLeftPart(UriPartial.Authority);
            }

            // 当前分片到达时，顺手把清单里后续 2 片后台拉进宿主机缓存
            if (!warm && !string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
                && !IsM3u8Url(target))
            {
                string[] nx;
                if (_segNext.TryGetValue(target, out nx))
                    PrefetchSegments(new List<string>(nx), referer);
            }

            // 一级：本代理自己落盘的分片缓存；二级：App 端 ExoPlayer 已落盘的原生视频缓存。
            // 命中即完全不向 CDN 发包 —— Web 端一起看复用宿主机缓存数据就靠这两级。
            bool head = string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            bool segCacheable = !warm && !head && string.IsNullOrEmpty(req.Headers["Range"])
                && !IsM3u8Url(target);
            if (!warm && !head)
            {
                if (segCacheable)
                {
                    byte[] hit; string hitCt;
                    if (TryVodSeg(target, out hit, out hitCt))
                    {
                        res.StatusCode = 200;
                        res.ContentType = string.IsNullOrWhiteSpace(hitCt) ? "video/mp2t" : hitCt;
                        res.AddHeader("Accept-Ranges", "bytes");
                        res.AddHeader("Cache-Control", "public, max-age=259200");
                        res.AddHeader("X-Vod-Cache", "proxy-disk");
                        res.ContentLength64 = hit.Length;
                        using (var o = res.OutputStream) o.Write(hit, 0, hit.Length);
                        return;
                    }
                }
                if (TryVodAppCache(target, req, res)) return;
            }

            // m3u8 改写结果进内存缓存：播放器会重复请求清单（重试 / 拖动），命中即秒回。
            // hostHint 必须进 key：不同宿主机清单里的分片绝对地址不同，不能互相复用。
            string cacheKey = target + "|" + referer + "|" + hostHint;
            if (!warm && TryM3u8Cache(cacheKey, out var cached))
            {
                IndexManifest(cached);
                res.StatusCode = 200;
                res.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
                res.AddHeader("Cache-Control", "no-store");
                var bytes = Encoding.UTF8.GetBytes(cached);
                res.ContentLength64 = bytes.Length;
                using (var o = res.OutputStream) o.Write(bytes, 0, bytes.Length);
                PrefetchSegments(ManifestTargets(cached).Take(2).ToList(), referer);
                return;
            }

            HttpWebResponse up = null;
            try
            {
                // m3u8 永远整份拉取再改写；二进制媒体在预热时只拉 0-0 预热握手，正式播放才走 Range。
                bool isM3u8 = IsM3u8Url(target);
                string rangeArg = isM3u8 ? null : (warm ? "bytes=0-0" : req.Headers["Range"]);
                up = VodApi.Open(target, referer, rangeArg, 15000);
                var ct = up.ContentType ?? "application/octet-stream";
                var finalUrl = "";
                try { finalUrl = up.ResponseUri.AbsoluteUri; } catch { }
                if (string.IsNullOrEmpty(finalUrl)) finalUrl = target;

                bool likelyM3u8 = isM3u8
                    || ct.ToLowerInvariant().Contains("mpegurl") || ct.ToLowerInvariant().Contains("m3u8");
                bool textLike = ct.ToLowerInvariant().Contains("text/")
                    || ct.ToLowerInvariant().Contains("json") || ct.ToLowerInvariant().Contains("javascript");
                bool canRewrite = likelyM3u8 || (textLike && up.ContentLength >= 0 && up.ContentLength < 300000);

                // 探测文本会消费响应流，所以读进内存后原样留着，非 m3u8 时直接回写。
                string playlist = null;
                byte[] rawText = null;
                if (canRewrite)
                {
                    using (var ms = new MemoryStream())
                    {
                        up.GetResponseStream().CopyTo(ms);
                        rawText = ms.ToArray();
                    }
                    var txt = Encoding.UTF8.GetString(rawText);
                    if (!string.IsNullOrEmpty(txt) && txt.TrimStart('\ufeff', ' ', '\r', '\n')
                        .StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                        playlist = txt;
                }

                if (playlist != null)
                {
                    var rewritten = RewriteM3u8(playlist, finalUrl, referer, hostHint);
                    // 不论是否预热都写缓存：预热命中后，播放器正式起播时清单秒回
                    PutM3u8Cache(cacheKey, rewritten);
                    IndexManifest(rewritten);
                    if (warm)
                    {
                        res.StatusCode = 200;
                        res.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
                        res.ContentLength64 = 0;
                        try { res.Close(); } catch { }
                        return;
                    }
                    var bytes = Encoding.UTF8.GetBytes(rewritten);
                    res.StatusCode = 200;
                    res.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
                    res.AddHeader("Cache-Control", "no-store");
                    res.ContentLength64 = bytes.Length;
                    using (var o = res.OutputStream) o.Write(bytes, 0, bytes.Length);
                    // 清单已发出，后台先把前 2 片拉进宿主机缓存，播放器起播即命中
                    PrefetchSegments(ManifestTargets(rewritten).Take(2).ToList(), referer);
                    return;
                }

                // 二进制（mp4 / ts 分片 / 加密密钥）：流式透传，Range 与 206 原样带回
                res.ContentType = ct;
                res.AddHeader("Accept-Ranges", "bytes");
                if (up.StatusCode == HttpStatusCode.PartialContent)
                {
                    res.StatusCode = 206;
                    var cr = up.Headers["Content-Range"];
                    if (!string.IsNullOrEmpty(cr)) res.AddHeader("Content-Range", cr);
                }
                else res.StatusCode = 200;
                if (rawText != null) res.ContentLength64 = rawText.Length;
                else if (up.ContentLength >= 0) res.ContentLength64 = up.ContentLength;

                if (warm || string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                { res.Close(); return; }

                if (rawText != null)
                {
                    using (var o = res.OutputStream) o.Write(rawText, 0, rawText.Length);
                    return;
                }

                MemoryStream segBuf = (segCacheable && !likelyM3u8) ? new MemoryStream() : null;
                using (var src = up.GetResponseStream())
                {
                    var buf = new byte[64 * 1024];
                    var outp = res.OutputStream;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        try { outp.Write(buf, 0, n); }
                        catch { break; }
                        if (segBuf != null && segBuf.Length + n <= 32L * 1024 * 1024) segBuf.Write(buf, 0, n);
                    }
                    try { outp.Flush(); } catch { }
                }
                // 流式写完后必须关闭响应，否则客户端（hls.js / ExoPlayer）会一直等待 EOF，
                // 导致视频/音频缓冲不结束、无法拖动；chunked 上游还会直接卡死。
                try { res.Close(); } catch { }
                // 边下边播的同时落盘：下一次（含局域网一起看）直接命中本机缓存
                if (segBuf != null)
                {
                    if (segBuf.Length > 0) PutVodSeg(target, segBuf.ToArray(), ct);
                    try { segBuf.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                // 流式响应一旦开始写就不能再改状态码，只能断开
                try { res.Abort(); } catch { }
                Android.Util.Log.Debug("MchatVod", "代理流失败 " + target + " : " + ex.Message);
            }
            finally { try { up?.Close(); } catch { } }
        }

        private static readonly Regex UriAttrRx = new Regex("URI=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /* ---------------- HLS 分片预取 ----------------
         * 旧版每个分片都由浏览器发起、服务端单路同步回源，局域网一起看会一片一卡。
         * 这里在返回清单时预先缓存前 2 片，并在每个分片请求到来时后台并发缓存后续 2 片，
         * 配合 3 天磁盘缓存，跟随端播放几乎全程命中宿主机本地字节。 */
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _segNext
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _segPrefetchAt
            = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        /// <summary>从改写后的清单里按顺序还原分片真实直链（与 VodStream 的 target 同构）。</summary>
        private static List<string> ManifestTargets(string manifest)
        {
            var res = new List<string>();
            foreach (var line in (manifest ?? "").Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                int i = s.IndexOf("u=", StringComparison.Ordinal);
                if (i < 0) continue;
                string raw = s.Substring(i + 2);
                int amp = raw.IndexOf('&');
                if (amp >= 0) raw = raw.Substring(0, amp);
                try
                {
                    var t = System.Uri.UnescapeDataString(raw);
                    if (!string.IsNullOrWhiteSpace(t) && !IsM3u8Url(t)) res.Add(t);
                }
                catch { }
            }
            return res;
        }

        private static void IndexManifest(string manifest)
        {
            try
            {
                var urls = ManifestTargets(manifest);
                for (int i = 0; i < urls.Count; i++)
                {
                    var next = new List<string>();
                    for (int k = 1; k <= 2 && i + k < urls.Count; k++) next.Add(urls[i + k]);
                    _segNext[urls[i]] = next.ToArray();
                }
            }
            catch { }
        }

        private static void CacheSegment(string target, string referer)
        {
            try
            {
                byte[] d; string c;
                if (TryVodSeg(target, out d, out c)) return;
                using (var up = VodApi.Open(target, referer, null, 10000))
                using (var st = up.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    if (ms.Length > 0 && ms.Length <= 32L * 1024 * 1024)
                        PutVodSeg(target, ms.ToArray(), up.ContentType ?? "video/mp2t");
                }
            }
            catch { }
        }

        private static void PrefetchSegments(List<string> targets, string referer)
        {
            if (targets == null || targets.Count == 0) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var todo = new List<string>();
            foreach (var t in targets)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                long last;
                if (_segPrefetchAt.TryGetValue(t, out last) && now - last < 60000) continue;
                _segPrefetchAt[t] = now;
                byte[] d; string c;
                if (TryVodSeg(t, out d, out c)) continue;
                todo.Add(t);
            }
            if (todo.Count == 0) return;
            string rf = referer;
            Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 2 },
                        t => CacheSegment(t, rf));
                }
                catch { }
            });
        }

        /// <summary>把 m3u8 里所有相对 URL（分片 / 子清单 / AES 密钥）改写成代理地址。
        /// hostHint 为浏览器实际访问宿主机用的 base（网关转发会改写 Host 头，服务端无法自行推断）；
        /// 带上它时清单里每个分片都是宿主机绝对地址，跟随端不会再回落到自己网关。 </summary>
        private static string RewriteM3u8(string text, string finalUrl, string referer, string hostHint)
        {
            var sb = new StringBuilder();
            System.Uri baseUri;
            try { baseUri = new System.Uri(finalUrl); } catch { return text; }
            Func<string, string> proxyOf = abs =>
            {
                var rel = VodApi.StreamUrl(abs, referer, false);
                if (string.IsNullOrEmpty(hostHint)) return rel;
                rel += (rel.IndexOf('?') >= 0 ? "&" : "?")
                     + "h=" + System.Uri.EscapeDataString(hostHint);
                return hostHint + rel;
            };
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) { sb.Append('\n'); continue; }
                if (line[0] == '#')
                {
                    if (UriAttrRx.IsMatch(line))
                    {
                        var replaced = UriAttrRx.Replace(line, m =>
                        {
                            string abs;
                            try { abs = new System.Uri(baseUri, m.Groups[1].Value).AbsoluteUri; }
                            catch { return m.Value; }
                            return "URI=\"" + proxyOf(abs) + "\"";
                        });
                        sb.Append(replaced).Append('\n');
                    }
                    else sb.Append(line).Append('\n');
                    continue;
                }
                string absolute;
                try { absolute = new System.Uri(baseUri, line.Trim()).AbsoluteUri; }
                catch { sb.Append(line).Append('\n'); continue; }
                sb.Append(proxyOf(absolute)).Append('\n');
            }
            return sb.ToString();
        }
        /* ---------------- MacCMS 标准对外接口（视频源互通） ----------------
         * 让 AhoChatA 作为「标准视频源」被 MchatTV 体系 / 任意 MacCMS 客户端接入：
         * 路径 /api.php/provide/vod/ ，字段使用 vod_* 前缀，与 MchatTV-CMS 的
         * CmsPublicServer 输出完全对齐。多源聚合的影片用 SiteKey|Id 作为全局唯一
         * vod_id（客户端回传 ids 时再拆回 site+id）；vod_play_from/vod_play_url
         * 输出原始未解析串，由客户端自行 ParseSources（与 MchatTV-CMS 行为一致）。
         * 现有 web 前端(vod.js) 完全不受影响。 */

        private static void VodMacCms(HttpListenerContext ctx, HttpListenerRequest req)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Cache-Control", "no-store");
            string path = (req.Url != null ? req.Url.AbsolutePath : "") ?? "";
            string query = (req.Url != null ? req.Url.Query : "") ?? "";
            string ac = GetQuery(query, "ac");
            if (ac == "list" || ac == "class" ||
                path.EndsWith("/class", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/classes", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/types", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(res, MacClassesJson());
                return;
            }
            if (ac == "detail" || !string.IsNullOrEmpty(GetQuery(query, "ids")))
            {
                WriteJson(res, MacDetailJson(query));
                return;
            }
            // 默认：videolist / search
            WriteJson(res, MacVideoListJson(query));
        }

        private static string MacClassesJson()
        {
            try
            {
                var cats = VodApi.BuildCats(VodConfig.EnabledSites());
                return Json(new
                {
                    code = 1,
                    msg = "success",
                    @class = cats.Select(c => new { type_id = c.Name, type_name = c.Name, type_sort = 0 }).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Json(new { code = 0, msg = ex.Message, @class = new object[0] });
            }
        }

        private static string MacVideoListJson(string query)
        {
            try
            {
                int page = ParseInt(GetQuery(query, "pg"), 1);
                if (page < 1) page = ParseInt(GetQuery(query, "page"), 1);
                if (page < 1) page = 1;
                int size = ParseInt(GetQuery(query, "limit"), 20);
                if (size < 1 || size > 200) size = 20;
                string wd = GetQuery(query, "wd");
                if (string.IsNullOrEmpty(wd)) wd = GetQuery(query, "keyword");
                string cat = GetQuery(query, "t");

                List<VodItem> list;
                if (!string.IsNullOrWhiteSpace(wd))
                {
                    int okSites;
                    list = VodApi.Search(wd.Trim(), page, out okSites);
                }
                else
                {
                    bool fromCache;
                    list = VodApi.Home(page, cat ?? "", out fromCache);
                }
                if (list == null) list = new List<VodItem>();

                int pageCount = list.Count == 0 ? page : page + 1;
                int total = list.Count == 0 ? (page - 1) * size : page * size + list.Count;
                return Json(new
                {
                    code = 1,
                    msg = "success",
                    page = page,
                    pagecount = pageCount,
                    limit = size,
                    total = total,
                    list = list.Select(it => MacVodObj(it, true)).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Json(new { code = 0, msg = ex.Message, page = 1, pagecount = 1, limit = 20, total = 0, list = new object[0] });
            }
        }

        private static string MacDetailJson(string query)
        {
            try
            {
                string ids = GetQuery(query, "ids") ?? "";
                var items = new List<object>();
                foreach (var part in ids.Split(','))
                {
                    var seg = part.Trim();
                    if (seg.Length == 0) continue;
                    int bar = seg.IndexOf('|');
                    if (bar < 0) continue;
                    string site = seg.Substring(0, bar);
                    string id = seg.Substring(bar + 1);
                    if (site.Length == 0 || id.Length == 0) continue;
                    var it = VodApi.Detail(site, id, "");
                    if (it == null) continue;
                    items.Add(MacVodObj(it, true));
                }
                return Json(new { code = 1, msg = "success", list = items.ToArray() });
            }
            catch (Exception ex)
            {
                return Json(new { code = 0, msg = ex.Message, list = new object[0] });
            }
        }

        // MacCMS V10 标准字段；海报走本机代理 /api/media/vod/poster?u= ，
        // 避免客户端直连源站图床（与 MchatTV-CMS 的 /api/cover 思路一致）。
        private static object MacVodObj(VodItem it, bool full)
        {
            string poster = string.IsNullOrEmpty(it.Pic)
                ? ""
                : VodApi.PosterUrl(it.Pic, false);
            return new
            {
                vod_id = it.Key,
                vod_name = it.Name,
                type_id = it.TypeName,
                type_name = it.TypeName,
                vod_pic = poster,
                vod_remarks = it.Remarks,
                vod_year = it.Year,
                vod_area = it.Area,
                vod_actor = it.Actor,
                vod_director = "",
                vod_content = it.Desc,
                vod_hits = 0,
                vod_score = it.Score,
                vod_serial = "",
                vod_time = "",
                vod_play_from = full ? it.PlayFrom : "",
                vod_play_url = full ? it.PlayUrl : "",
                site_name = it.SiteName
            };
        }

        private static int ParseInt(string s, int def)
        {
            int n;
            return int.TryParse(s, out n) ? n : def;
        }

    }
}

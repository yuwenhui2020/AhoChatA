using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace AhoChatA.Vod
{
    // 与 MchatTV-CMS 采集器同源的多格式视频源自动识别与解析器（移植到 AhoChatA 在线影视）。
    // 兼容三类主流接口（对外输出统一为 VodItem，字段风格对齐 MacCMS V10）：
    //   1) MacCMS V10 / 爱影CMS  —— JSON（默认 / ?ac=videolist），字段 vod_*
    //   2) MacCMS V8 / 赞片CMS    —— 通用 XML（?ac=videolist&at=xml），<dl><dd flag>
    //   3) SeaCMS / 飞飞 / MaxCMS  —— SeaCMS 接口，路径 /api.php/Seacms/vod/ 或 /zyapi.php，
    //                                  短字段名 name/id/pic/type/play，且每集地址自带播放器标识。
    // 站点返回 {"code":0,"msg":"接口不正确"} 这类错误响应时，当作「此方式不行」继续试下一种。
    // 注意：本工程是经典 Xamarin.Android（monoandroid13.0），统一使用 Newtonsoft.Json，不引用 System.Text.Json。
    public class CmsSourceProfile
    {
        public string Fmt = "json";
        public string ListAct = "videolist";
        public string JsonFlag = "";
        public string Field = "maccms";
        public string Path = "";
        public string TimeParam = "h";
        public string PageParam = "pg";
        public string ClassAct = "list";
        public int LimitMax = 20;
        public string Detected = "";

        public static CmsSourceProfile Parse(string s)
        {
            var p = new CmsSourceProfile();
            if (string.IsNullOrWhiteSpace(s)) return p;
            foreach (var kv in s.Split(';'))
            {
                int i = kv.IndexOf('='); if (i < 0) continue;
                string k = kv.Substring(0, i).Trim(), v = kv.Substring(i + 1).Trim();
                if (k == "fmt") p.Fmt = v;
                else if (k == "listact") p.ListAct = v;
                else if (k == "jsonflag") p.JsonFlag = v;
                else if (k == "field") p.Field = v;
                else if (k == "path") p.Path = v;
                else if (k == "timep") p.TimeParam = v;
                else if (k == "pagep") p.PageParam = v;
                else if (k == "classact") p.ClassAct = v;
                else if (k == "limitmax") { int n; if (int.TryParse(v, out n)) p.LimitMax = n; }
                else if (k == "detected") p.Detected = v;
            }
            return p;
        }

        public override string ToString()
        {
            return "fmt=" + Fmt + ";listact=" + ListAct + ";jsonflag=" + JsonFlag + ";field=" + Field +
                   ";path=" + Path + ";timep=" + TimeParam + ";pagep=" + PageParam +
                   ";classact=" + ClassAct + ";limitmax=" + LimitMax + ";detected=" + Detected;
        }

        public string Label()
        {
            string f = Fmt == "json" ? "JSON" : "XML";
            string s = Field == "seacms" ? "SeaCMS" : (Path == "Seacms" || Path == "zyapi" ? "SeaCMS" : "MacCMS");
            return f + "·" + s;
        }
    }

    public static class SourceDetector
    {
        static readonly ConcurrentDictionary<string, CmsSourceProfile> _cache =
            new ConcurrentDictionary<string, CmsSourceProfile>();

        public static CmsSourceProfile GetProfile(string key, string api)
        {
            if (_cache.TryGetValue(key ?? "", out var c)) return c;
            var p = Detect(CleanBase(api));
            if (p != null) _cache[key ?? ""] = p;
            return p;
        }

        public static void ClearProfile(string key) { if (key != null) _cache.TryRemove(key, out _); }

        public static string BuildListUrl(CmsSourceProfile p, string baseApi, int page, string typeId, int hours, string keyword = "", string token = null)
        {
            string b = ResolveBase(p, baseApi);
            var sb = new StringBuilder(b);
            sb.Append('?').Append("ac=").Append(p.ListAct);
            if (p.Fmt == "json" && !string.IsNullOrEmpty(p.JsonFlag)) sb.Append('&').Append(p.JsonFlag);
            string pp = p.PageParam == "page" ? "page" : "pg";
            sb.Append('&').Append(pp).Append('=').Append(page);
            if (!string.IsNullOrEmpty(typeId)) sb.Append("&t=").Append(Uri.EscapeDataString(typeId));
            if (!string.IsNullOrEmpty(keyword)) sb.Append("&wd=").Append(Uri.EscapeDataString(keyword));
            if (hours > 0 && p.TimeParam == "h") sb.Append("&h=").Append(hours);
            if (!string.IsNullOrEmpty(token)) sb.Append("&token=").Append(Uri.EscapeDataString(token));
            return sb.ToString();
        }

        public static string BuildDetailUrl(CmsSourceProfile p, string baseApi, List<string> ids, string token = null)
        {
            string b = ResolveBase(p, baseApi);
            var sb = new StringBuilder(b);
            sb.Append('?').Append("ac=detail&ids=").Append(string.Join(",", ids));
            if (p.Fmt == "json" && !string.IsNullOrEmpty(p.JsonFlag)) sb.Append('&').Append(p.JsonFlag);
            if (!string.IsNullOrEmpty(token)) sb.Append("&token=").Append(Uri.EscapeDataString(token));
            return sb.ToString();
        }

        public static string BuildClassUrl(CmsSourceProfile p, string baseApi, string token = null)
        {
            var sb = new StringBuilder(ResolveBase(p, baseApi) + "?ac=" + (p.ClassAct == "class" ? "class" : "list"));
            if (!string.IsNullOrEmpty(token)) sb.Append("&token=").Append(Uri.EscapeDataString(token));
            return sb.ToString();
        }

        public static List<VodItem> ParseList(string text, VodSite site, out int total, out int pageCount, out int limit, out bool isError, out string errMsg)
        {
            total = 0; pageCount = 0; limit = 0; isError = false; errMsg = "";
            return ParseResponse(text, site, out _, out total, out pageCount, out limit, out isError, out errMsg);
        }

        public static bool ParseDetail(string text, VodItem it)
        {
            VodSite site = new VodSite { Key = it.SiteKey, Name = it.SiteName, Api = it.Api };
            List<VodItem> dummy; int tot, pc, lim; bool err; string em;
            var items = ParseResponse(text, site, out dummy, out tot, out pc, out lim, out err, out em);
            if (err || items.Count == 0) return false;
            var v = items[0];
            if (!string.IsNullOrEmpty(v.PlayFrom)) it.PlayFrom = v.PlayFrom;
            if (!string.IsNullOrEmpty(v.PlayUrl)) it.PlayUrl = v.PlayUrl;
            if (!string.IsNullOrEmpty(v.Desc)) it.Desc = v.Desc;
            if (!string.IsNullOrEmpty(v.Pic)) it.Pic = v.Pic;
            if (!string.IsNullOrEmpty(v.Actor)) it.Actor = v.Actor;
            return true;
        }

        public static Dictionary<string, string> FetchClasses(CmsSourceProfile p, string baseApi, string token = null)
        {
            var map = new Dictionary<string, string>();
            string text = VodApi.HttpGetText(BuildClassUrl(p, baseApi, token), 9000);
            if (string.IsNullOrWhiteSpace(text)) return map;
            string t = text.TrimStart().TrimStart('\uFEFF', '\u200B');
            try
            {
                if (t.StartsWith("{"))
                {
                    var root = JToken.Parse(t);
                    var arr = root["class"] as JArray ?? root["list"] as JArray;
                    if (arr != null)
                        foreach (var e in arr)
                        {
                            string id = First(e, "type_id", "id", "tid");
                            string nm = First(e, "type_name", "name", "typename");
                            if (id.Length > 0 && nm.Length > 0) map[id] = nm;
                        }
                }
                else if (t.StartsWith("<"))
                {
                    var x = XDocument.Parse(t);
                    foreach (var ty in x.Descendants("ty")) map[Attr(ty, "id")] = (ty.Value ?? "").Trim();
                    foreach (var c in x.Descendants("class"))
                    {
                        string id = Attr(c, "id");
                        if (id.Length > 0 && !map.ContainsKey(id)) map[id] = (c.Value ?? "").Trim();
                    }
                }
            }
            catch { }
            return map;
        }

        public static CmsSourceProfile Detect(string baseApi, Action<string> log = null)
        {
            baseApi = CleanBase(baseApi);
            var cands = new List<KeyValuePair<string, CmsSourceProfile>>();
            cands.Add(KV(baseApi + "?ac=videolist", P("json", "videolist", "", "maccms", "")));
            cands.Add(KV(baseApi + "?ac=videolist&at=json", P("json", "videolist", "at=json", "maccms", "")));
            cands.Add(KV(baseApi + "?ac=videolist&f=json", P("json", "videolist", "f=json", "maccms", "")));
            cands.Add(KV(baseApi + "?ac=list", P("json", "list", "", "maccms", "")));
            cands.Add(KV(baseApi + "?ac=videolist&at=xml", P("xml", "videolist", "at=xml", "maccms", "")));
            string sc = SwapPath(baseApi, "Seacms");
            cands.Add(KV(sc + "?ac=videolist", P("xml", "videolist", "", "seacms", "Seacms")));
            cands.Add(KV(sc + "?ac=list", P("xml", "list", "", "seacms", "Seacms")));
            string zy = SwapPath(baseApi, "zyapi");
            cands.Add(KV(zy + "?ac=videolist", P("xml", "videolist", "", "seacms", "zyapi")));

            foreach (var c in cands)
            {
                try
                {
                    string text = VodApi.HttpGetText(c.Key, 8000);
                    List<VodItem> dummy; int tot, pc, lim; bool err; string em;
                    var items = ParseResponse(text, null, out dummy, out tot, out pc, out lim, out err, out em);
                    if (err) continue;
                    if (items.Count > 0)
                    {
                        var p = c.Value;
                        p.Fmt = (text.TrimStart().StartsWith("<") ? "xml" : "json");
                        if (lim > 0 && lim > 1) p.LimitMax = lim;
                        p.Detected = p.Label() + (tot > 0 ? L10n.T(" · 远端约 ") + tot + L10n.T(" 条") : "") + L10n.T(" · 采样 ") + items.Count + L10n.T(" 条");
                        log?.Invoke(L10n.T("识别为 ") + p.Detected);
                        return p;
                    }
                }
                catch { }
            }
            return null;
        }

        static KeyValuePair<string, CmsSourceProfile> KV(string url, CmsSourceProfile p)
            => new KeyValuePair<string, CmsSourceProfile>(url, p);
        static CmsSourceProfile P(string fmt, string listact, string jsonflag, string field, string path)
            => new CmsSourceProfile { Fmt = fmt, ListAct = listact, JsonFlag = jsonflag, Field = field, Path = path };

        static string ResolveBase(CmsSourceProfile p, string baseApi)
        {
            string b = CleanBase(baseApi);
            if (p.Path == "Seacms") return SwapPath(b, "Seacms");
            if (p.Path == "zyapi") return SwapPath(b, "zyapi");
            return b;
        }

        public static string CleanBase(string api)
        {
            if (string.IsNullOrWhiteSpace(api)) return "";
            string u = api.Trim();
            int q = u.IndexOf('?'); if (q >= 0) u = u.Substring(0, q);
            int h = u.IndexOf('#'); if (h >= 0) u = u.Substring(0, h);
            u = Regex.Replace(u, @"/?(provide|Seacms|zyapi\.php)?$",
                "", RegexOptions.IgnoreCase);
            u = u.TrimEnd('/');
            if (u.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) return u;
            if (!u.EndsWith("/")) u += "/";
            return u;
        }

        static string SwapPath(string baseApi, string variant)
        {
            try
            {
                if (variant == "zyapi")
                    return new Uri(baseApi).GetLeftPart(UriPartial.Authority) + "/zyapi.php";
                if (variant == "Seacms")
                    return Regex.Replace(baseApi, @"/provide/", "/Seacms/",
                        RegexOptions.IgnoreCase);
            }
            catch { }
            return baseApi;
        }

        static List<VodItem> ParseResponse(string text, VodSite site, out List<VodItem> items,
            out int total, out int pageCount, out int limit, out bool isError, out string errMsg)
        {
            items = new List<VodItem>();
            total = 0; pageCount = 0; limit = 0; isError = false; errMsg = "";
            string t = (text ?? "").TrimStart().TrimStart('\uFEFF', '\u200B');
            try
            {
                if (t.StartsWith("{") || t.StartsWith("["))
                {
                    var root = JToken.Parse(t);
                    if (root.Type == JTokenType.Object)
                    {
                        int code = Num(root, "code");
                        if (code == 0)
                        {
                            var la = root["list"];
                            bool hasList = la != null && la.Type == JTokenType.Array;
                            if (!hasList)
                            {
                                isError = true; errMsg = First(root, "msg", "info"); return items;
                            }
                        }
                        var arr = root["list"] as JArray;
                        if (arr != null)
                            foreach (var e in arr)
                            {
                                var v = ParseVideoJson(e, site);
                                if (v != null && !string.IsNullOrEmpty(v.Name)) items.Add(v);
                            }
                        total = Num(root, "total"); pageCount = Num(root, "pagecount");
                        limit = Num(root, "limit");
                    }
                    else if (root.Type == JTokenType.Array)
                    {
                        foreach (var e in root)
                        {
                            var v = ParseVideoJson(e, site);
                            if (v != null && !string.IsNullOrEmpty(v.Name)) items.Add(v);
                        }
                    }
                }
                else if (t.StartsWith("<"))
                {
                    var x = XDocument.Parse(t);
                    var listEl = x.Descendants("list").FirstOrDefault() ?? x.Root;
                    if (listEl != null)
                    {
                        pageCount = IntAttr(listEl, "pagecount");
                        limit = IntAttr(listEl, "pagesize"); if (limit == 0) limit = IntAttr(listEl, "limit");
                        total = IntAttr(listEl, "recordcount");
                    }
                    foreach (var v in x.Descendants("video"))
                    {
                        var cv = ParseVideoXml(v, site);
                        if (cv != null && !string.IsNullOrEmpty(cv.Name)) items.Add(cv);
                    }
                }
                else
                {
                    isError = true; errMsg = L10n.T("无法识别的返回内容（既非 JSON 也非 XML）");
                }
            }
            catch (Exception e) { isError = true; errMsg = L10n.T("解析异常：") + e.Message; }
            return items;
        }

        static VodItem ParseVideoJson(JToken e, VodSite site)
        {
            string name = First(e, "vod_name", "name");
            string id = First(e, "vod_id", "id");
            if (string.IsNullOrWhiteSpace(id)) id = StableId(site, name);
            int hits = 0; double score = 0;
            int.TryParse(First(e, "vod_hits", "hits"), out hits);
            double.TryParse(First(e, "vod_score", "score"), NumberStyles.Any, CultureInfo.InvariantCulture, out score);
            string remarks = First(e, "vod_remarks", "note", "vod_note", "remarks");
            if (string.IsNullOrWhiteSpace(remarks))
            {
                string serial = First(e, "vod_serial", "serial");
                if (!string.IsNullOrWhiteSpace(serial) && serial != "0") remarks = "共 " + serial + " 集";
            }
            string pic = First(e, "vod_pic", "vod_pic_thumb", "vod_pic_slide", "pic");
            string from = First(e, "vod_play_from", "play_from", "playfrom", "from");
            string url = First(e, "vod_play_url", "play_url", "playurl", "url");
            if (string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(url)) from = "";
            (from, url) = NormalizePlay(from, url);
            return new VodItem
            {
                Id = id, Name = name, Pic = AbsPic(pic, site?.Api), Remarks = remarks,
                TypeName = First(e, "type_name", "vod_class", "type", "typename"),
                Year = First(e, "vod_year", "year"), Area = First(e, "vod_area", "area"),
                Desc = StripHtml(First(e, "vod_content", "des", "vod_des", "content")),
                Actor = First(e, "vod_actor", "actor"), Score = First(e, "vod_score", "score"),
                PlayFrom = from, PlayUrl = url,
                SiteKey = site?.Key ?? "", SiteName = site?.Name ?? "", Api = site?.Api ?? ""
            };
        }

        static VodItem ParseVideoXml(XElement v, VodSite site)
        {
            string name = Elem(v, "vod_name", "name");
            string id = Elem(v, "vod_id", "id");
            if (string.IsNullOrWhiteSpace(id)) id = StableId(site, name);
            int hits = 0; double score = 0;
            int.TryParse(Elem(v, "vod_hits", "hits"), out hits);
            double.TryParse(Elem(v, "vod_score", "score"), NumberStyles.Any, CultureInfo.InvariantCulture, out score);
            string remarks = Elem(v, "vod_remarks", "note", "vod_note", "remarks");
            if (string.IsNullOrWhiteSpace(remarks))
            {
                string serial = Elem(v, "vod_serial", "serial");
                if (!string.IsNullOrWhiteSpace(serial) && serial != "0") remarks = "共 " + serial + " 集";
            }
            string pic = Elem(v, "vod_pic", "vod_pic_thumb", "pic");
            string from, url;
            bool seacms = v.Element("dl") == null && (v.Elements("play").Any() || v.Element("play") != null);
            if (seacms)
            {
                var groups = v.Elements("play").Select(p => p.Value).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (groups.Count == 0 && v.Element("play") != null) groups.Add(v.Element("play").Value);
                (from, url) = NormalizeSeaPlay(groups);
            }
            else
            {
                from = PlayFrom(v); url = PlayUrl(v);
            }
            return new VodItem
            {
                Id = id, Name = name, Pic = AbsPic(pic, site?.Api), Remarks = remarks,
                TypeName = Elem(v, "type_name", "vod_class", "type", "typename"),
                Year = Elem(v, "vod_year", "year"), Area = Elem(v, "vod_area", "area"),
                Desc = StripHtml(Elem(v, "vod_content", "des", "vod_des", "content")),
                Actor = Elem(v, "vod_actor", "actor"), Score = Elem(v, "vod_score", "score"),
                PlayFrom = from, PlayUrl = url,
                SiteKey = site?.Key ?? "", SiteName = site?.Name ?? "", Api = site?.Api ?? ""
            };
        }

        static (string, string) NormalizePlay(string from, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return (from, url);
            if (!string.IsNullOrWhiteSpace(from)) return (from, url);
            var groups = url.Split(new[] { "$$$" }, StringSplitOptions.None).ToList();
            return NormalizeSeaPlay(groups);
        }

        static (string, string) NormalizeSeaPlay(List<string> groups)
        {
            var froms = new List<string>();
            var urls = new List<string>();
            string lastFlag = "";
            foreach (var g in groups)
            {
                string gg = (g ?? "").Trim();
                if (gg.Length == 0) continue;
                var eps = gg.Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries);
                var bucket = new Dictionary<string, List<string>>();
                foreach (var ep in eps)
                {
                    string e = ep.Trim();
                    if (e.Length == 0) continue;
                    int idx = e.IndexOf('$');
                    string flag = "", addr = e;
                    if (idx > 0 && idx < e.Length - 1) { flag = e.Substring(0, idx); addr = e.Substring(idx + 1); }
                    if (string.IsNullOrEmpty(flag)) flag = lastFlag;
                    if (string.IsNullOrEmpty(flag)) flag = "default";
                    lastFlag = flag;
                    if (!bucket.ContainsKey(flag)) bucket[flag] = new List<string>();
                    bucket[flag].Add(addr);
                }
                foreach (var kv in bucket)
                {
                    froms.Add(kv.Key);
                    urls.Add(string.Join("#", kv.Value));
                }
            }
            return (string.Join("$$$", froms), string.Join("$$$", urls));
        }

        static string PlayFrom(XElement v)
        {
            var dl = v.Element("dl");
            if (dl == null) return "";
            return string.Join("$$$", dl.Elements("dd").Select(d => Attr(d, "flag")).ToArray());
        }

        static string PlayUrl(XElement v)
        {
            var dl = v.Element("dl");
            if (dl == null) return "";
            return string.Join("$$$", dl.Elements("dd").Select(d => d.Value.Trim()).ToArray());
        }

        static string AbsPic(string pic, string api)
        {
            if (string.IsNullOrWhiteSpace(pic) || pic.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return pic;
            try
            {
                var u = new Uri(api);
                return u.GetLeftPart(UriPartial.Authority) + (pic.StartsWith("/") ? "" : "/") + pic;
            }
            catch { return pic; }
        }

        static string StableId(VodSite site, string name)
        {
            string raw = (site?.Key ?? "") + "|" + (name ?? "");
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static string First(JToken e, params string[] keys)
        {
            foreach (var k in keys)
            {
                try
                {
                    var v = e[k];
                    if (v != null && v.Type != JTokenType.Null && v.Type != JTokenType.None)
                    {
                        if (v.Type == JTokenType.String) return ((string)v).Trim();
                        return v.ToString().Trim();
                    }
                }
                catch { }
            }
            return "";
        }

        static string Elem(XElement p, params string[] names)
        {
            foreach (var n in names)
            {
                var e = p.Element(n);
                if (e != null) return e.Value.Trim();
            }
            return "";
        }

        static string Attr(XElement e, string name)
        {
            var a = e.Attribute(name);
            return a != null ? a.Value.Trim() : "";
        }

        static int Num(JToken root, string key)
        {
            try
            {
                var v = root[key];
                if (v == null || v.Type == JTokenType.Null || v.Type == JTokenType.None) return 0;
                int n;
                if (int.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n)) return n;
            }
            catch { }
            return 0;
        }

        static int IntAttr(XElement e, string name)
        {
            int n;
            var a = e.Attribute(name);
            return a != null && int.TryParse(a.Value, out n) ? n : 0;
        }

        static string StripHtml(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            try { return Regex.Replace(s, "<[^>]+>", "").Trim(); }
            catch { return s; }
        }
    }
}

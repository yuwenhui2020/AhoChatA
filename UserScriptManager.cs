using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AhoChatA
{
    /// <summary>一条油猴脚本：元数据 + 源码 + 开关状态。</summary>
    public sealed class UserScript
    {
        public string Id { get; set; }            // 稳定 id（导入时生成）
        public string Name { get; set; } = L10n.T("未命名脚本");
        public string Description { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public string Author { get; set; } = "";
        public string Namespace { get; set; } = "";
        public List<string> Matches { get; set; } = new List<string>();
        public List<string> Includes { get; set; } = new List<string>();
        public List<string> Excludes { get; set; } = new List<string>();
        /// <summary>@require 依赖脚本 URL 列表（如 jsPDF）。注入前需先加载这些依赖。</summary>
        public List<string> Requires { get; set; } = new List<string>();
        public string RunAt { get; set; } = "document-idle"; // document-start / document-body / document-end / document-idle（统一连字符）
        public bool NoFrames { get; set; }
        public string Code { get; set; } = "";      // 去掉元数据注释头后的可执行 JS
        public bool Enabled { get; set; } = true;
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 油猴脚本管理器：脚本的增删改、元数据解析、URL 匹配、注入脚本生成、
    /// GM_setValue 持久化、GM_xmlhttpRequest 代理。单例。
    /// </summary>
    public sealed class UserScriptManager
    {
        private static readonly UserScriptManager _inst = new UserScriptManager();
        public static UserScriptManager Instance => _inst;

        private const string PrefName = "userscript_prefs";
        private const string KeyScripts = "scripts";
        private const string KeyValues = "values";
        private Android.Content.Context _ctx;
        private readonly object _gate = new object();
        private List<UserScript> _scripts = new List<UserScript>();
        private Dictionary<string, Dictionary<string, string>> _values
            = new Dictionary<string, Dictionary<string, string>>();

        public void Init(Android.Content.Context ctx)
        {
            _ctx = ctx?.ApplicationContext;
            Load();
            EnsureBuiltIns();
        }

        /// <summary>
        /// 内置脚本：PDF.js 阅读助手。点击页面中的 PDF 链接时交给浏览器原生
        /// 下载并用内置 pdf.js 阅读器打开，而不是让系统 WebView 直接显示 PDF。
        /// </summary>
        private const string BuiltInPdfId = "mchat_builtin_pdfjs";
        private const string BuiltInPdfScript = @"// ==UserScript==
// @name         AhoChatA PDF.js 阅读助手
// @namespace    mchat.local
// @version      1.0
// @description  把 PDF 链接交给内置 pdf.js 阅读器打开（支持搜索、选字、夜间模式）。
// @author       AhoChatA
// @match        *://*/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==
(function () {
  if (window.__mchatPdfAssistant) return;
  window.__mchatPdfAssistant = true;
  function openPdf(href, name) {
    try {
      if (window.MchatJs && window.MchatJs.openPdfViewer) window.MchatJs.openPdfViewer(String(href || ''), String(name || 'PDF 文档'));
      else window.open(href, '_blank');
    } catch (e) { window.open(href, '_blank'); }
  }
  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    var href = a.href || '';
    if (!/\.pdf(\?|#|$)/i.test(href)) return;
    e.preventDefault();
    e.stopPropagation();
    openPdf(href, (a.textContent || '').trim() || 'PDF 文档');
  }, true);
})();";

        /// <summary>
        /// jsPDF 依赖提供器：把 jspdf UMD 注入为每个页面的 window.jspdf。
        /// 兼容老版本导入的「新闻图片转PDF」脚本（当时程序还没解析 @require，
        /// 脚本元数据里没有依赖信息），无需用户重新导入或改动脚本本身。
        /// </summary>
        private const string BuiltInJsPdfId = "mchat_builtin_jspdf";
        private const string BuiltInJsPdfScript = @"// ==UserScript==
// @name         AhoChatA jsPDF 依赖库
// @namespace    mchat.local
// @version      1.0
// @description  为页面注入 jsPDF，供图片转 PDF 等脚本使用。
// @author       AhoChatA
// @match        *://*/*
// @run-at       document-idle
// @require      https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js
// @grant        none
// ==/UserScript==
(function () { })();";

        private void EnsureBuiltIns()
        {
            lock (_gate)
            {
                bool changed = false;
                if (!_scripts.Any(s => s.Id == BuiltInPdfId))
                {
                    var s = Parse(BuiltInPdfScript);
                    s.Id = BuiltInPdfId;
                    s.Enabled = true;
                    _scripts.Add(s);
                    changed = true;
                }
                if (!_scripts.Any(s => s.Id == BuiltInJsPdfId))
                {
                    var s = Parse(BuiltInJsPdfScript);
                    s.Id = BuiltInJsPdfId;
                    s.Enabled = true;
                    _scripts.Add(s);
                    changed = true;
                }
                // 用户提供的「新闻图片转PDF」脚本原样内置：即使偏好设置被清空也能自动恢复。
                // 内容与 /sdcard/Download/pdf.js 完全一致，不做任何修改。
                if (!_scripts.Any(s => s.Name == "新闻图片转PDF"))
                {
                    try
                    {
                        using (var stream = Android.App.Application.Context.Assets.Open("user_scripts/news_pdf.js"))
                        using (var reader = new StreamReader(stream))
                        {
                            var s = Parse(reader.ReadToEnd());
                            s.Enabled = true;
                            _scripts.Add(s);
                            changed = true;
                            Android.Util.Log.Info("Userscript", $"restored bundled script {s.Name} requires={s.Requires.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Userscript", "restore bundled script: " + ex.Message);
                    }
                }
                if (changed) Save();
            }
        }

        private Android.Content.ISharedPreferences Prefs =>
            _ctx?.GetSharedPreferences(PrefName, Android.Content.FileCreationMode.Private);

        private void Load()
        {
            try
            {
                var p = Prefs;
                if (p == null) return;
                var json = p.GetString(KeyScripts, "[]");
                _scripts = Newtonsoft.Json.JsonConvert.DeserializeObject<List<UserScript>>(json) ?? new List<UserScript>();
                var vjson = p.GetString(KeyValues, "{}");
                _values = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(vjson)
                    ?? new Dictionary<string, Dictionary<string, string>>();
                Android.Util.Log.Info("Userscript", $"Load: scripts={_scripts.Count}");
            }
            catch { _scripts = new List<UserScript>(); _values = new Dictionary<string, Dictionary<string, string>>(); }
        }

        private void Save()
        {
            try
            {
                var p = Prefs;
                if (p == null) return;
                p.Edit()
                    .PutString(KeyScripts, Newtonsoft.Json.JsonConvert.SerializeObject(_scripts))
                    .PutString(KeyValues, Newtonsoft.Json.JsonConvert.SerializeObject(_values))
                    .Apply();
            }
            catch { }
        }

        public List<UserScript> All
        {
            get { lock (_gate) return _scripts.OrderByDescending(s => s.AddedAt).ToList(); }
        }

        public UserScript Get(string id)
        {
            lock (_gate) return _scripts.FirstOrDefault(s => s.Id == id);
        }

        public void Add(UserScript script)
        {
            if (script == null) return;
            if (string.IsNullOrWhiteSpace(script.Id)) script.Id = Guid.NewGuid().ToString("N");
            lock (_gate) { _scripts.Add(script); Save(); }
        }

        public void Update(UserScript script)
        {
            if (script == null) return;
            lock (_gate)
            {
                var i = _scripts.FindIndex(s => s.Id == script.Id);
                if (i >= 0) _scripts[i] = script; else _scripts.Add(script);
                Save();
            }
        }

        /// <summary>生成完整 .user.js 文本（含元数据头），供源码查看/编辑。</summary>
        public static string ToSource(UserScript s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            sb.Append("// ==UserScript==\n");
            sb.Append("// @name         ").Append(s.Name ?? "").Append('\n');
            if (!string.IsNullOrWhiteSpace(s.Namespace)) sb.Append("// @namespace    ").Append(s.Namespace).Append('\n');
            sb.Append("// @version      ").Append(string.IsNullOrWhiteSpace(s.Version) ? "1.0" : s.Version).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.Description)) sb.Append("// @description  ").Append(s.Description).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.Author)) sb.Append("// @author       ").Append(s.Author).Append('\n');
            foreach (var m in s.Matches ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(m)) sb.Append("// @match        ").Append(m).Append('\n');
            foreach (var i in s.Includes ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(i)) sb.Append("// @include      ").Append(i).Append('\n');
            foreach (var x in s.Excludes ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(x)) sb.Append("// @exclude      ").Append(x).Append('\n');
            sb.Append("// @run-at       ")
              .Append(string.IsNullOrWhiteSpace(s.RunAt) ? "document-idle" : s.RunAt.Replace('_', '-'))
              .Append('\n');
            if (s.NoFrames) sb.Append("// @noframes\n");
            sb.Append("// @grant        none\n");
            sb.Append("// ==/UserScript==\n\n");
            sb.Append(s.Code ?? "");
            return sb.ToString();
        }

        /// <summary>从编辑后的完整源码更新脚本：元数据与代码一起重新解析，保留 id/开关/加入时间。</summary>
        public UserScript UpdateFromSource(string id, string text)
        {
            var parsed = Parse(text);
            parsed.Id = id;
            lock (_gate)
            {
                var old = _scripts.FirstOrDefault(s => s.Id == id);
                if (old != null)
                {
                    parsed.Enabled = old.Enabled;
                    parsed.AddedAt = old.AddedAt;
                }
                Update(parsed);
            }
            return parsed;
        }

        public void Remove(string id)
        {
            lock (_gate)
            {
                _scripts.RemoveAll(s => s.Id == id);
                _values.Remove(id);
                Save();
            }
        }

        public void SetEnabled(string id, bool enabled)
        {
            lock (_gate)
            {
                var s = _scripts.FirstOrDefault(x => x.Id == id);
                if (s != null) { s.Enabled = enabled; Save(); }
            }
        }

        /* ---------------- GM_setValue / get ---------------- */

        public void SetValue(string scriptId, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(scriptId) || string.IsNullOrWhiteSpace(key)) return;
            lock (_gate)
            {
                if (!_values.TryGetValue(scriptId, out var d)) { d = new Dictionary<string, string>(); _values[scriptId] = d; }
                d[key] = value ?? "";
                Save();
            }
        }

        public string GetValue(string scriptId, string key)
        {
            if (string.IsNullOrWhiteSpace(scriptId) || string.IsNullOrWhiteSpace(key)) return null;
            lock (_gate)
            {
                if (_values.TryGetValue(scriptId, out var d) && d.TryGetValue(key, out var v)) return v;
                return null;
            }
        }

        public void DeleteValue(string scriptId, string key)
        {
            lock (_gate)
            {
                if (_values.TryGetValue(scriptId, out var d)) { d.Remove(key); Save(); }
            }
        }

        public List<string> ListValues(string scriptId)
        {
            lock (_gate)
            {
                if (_values.TryGetValue(scriptId, out var d)) return d.Keys.ToList();
                return new List<string>();
            }
        }

        /* ---------------- 元数据解析 ---------------- */

        /// <summary>解析 .user.js 文本，提取元数据头与源码。容错：无元数据头也接受（当作 @match *://*/*）。</summary>
        public static UserScript Parse(string text)
        {
            var s = new UserScript { Id = Guid.NewGuid().ToString("N") };
            if (string.IsNullOrWhiteSpace(text)) return s;
            var code = text;
            var m = Regex.Match(text, @"(?m)^\s*//\s*==UserScript==\s*$([\s\S]*?)^\s*//\s*==/UserScript==\s*$");
            if (m.Success)
            {
                code = text.Substring(m.Index + m.Length).TrimStart('\r', '\n');
                foreach (LineMatch lm in ParseMetaLines(m.Groups[1].Value))
                {
                    switch (lm.Key.ToLowerInvariant())
                    {
                        case "name": s.Name = lm.Value; break;
                        case "description": s.Description = lm.Value; break;
                        case "version": s.Version = lm.Value; break;
                        case "author": s.Author = lm.Value; break;
                        case "namespace": s.Namespace = lm.Value; break;
                        case "match": s.Matches.Add(lm.Value); break;
                        case "include": s.Includes.Add(lm.Value); break;
                        case "exclude": s.Excludes.Add(lm.Value); break;
                        case "require": s.Requires.Add(lm.Value); break;
                        case "run-at":
                            var ra = lm.Value.ToLowerInvariant().Replace('_', '-');
                            if (ra == "document-start" || ra == "document-body" || ra == "document-end" || ra == "document-idle")
                                s.RunAt = ra;
                            break;
                        case "noframes": s.NoFrames = true; break;
                    }
                }
            }
            if (s.Matches.Count == 0 && s.Includes.Count == 0) s.Matches.Add("*://*/*");
            s.Code = code ?? "";
            return s;
        }

        private class LineMatch { public string Key; public string Value; }

        private static IEnumerable<LineMatch> ParseMetaLines(string block)
        {
            foreach (var raw in block.Split('\n'))
            {
                var line = raw.Trim();
                var mm = Regex.Match(line, @"^//\s*@(\S+)\s*(.*)$");
                if (mm.Success)
                    yield return new LineMatch { Key = mm.Groups[1].Value, Value = mm.Groups[2].Value.Trim() };
            }
        }

        /* ---------------- URL 匹配 ---------------- */

        public bool Matches(UserScript s, string url)
        {
            if (s == null || !s.Enabled || string.IsNullOrEmpty(url)) return false;
            foreach (var ex in s.Excludes)
                if (GlobMatch(ex, url)) return false;
            foreach (var inc in s.Includes)
                if (GlobMatch(inc, url)) return true;
            foreach (var mt in s.Matches)
                if (MatchPattern(mt, url)) return true;
            return false;
        }

        /* ---------------- @require 依赖脚本缓存 ---------------- */

        private static readonly Dictionary<string, string> _requireCache = new Dictionary<string, string>();
        private static readonly HashSet<string> _requireLoading = new HashSet<string>();

        /// <summary>取已缓存的 @require 源码；没有则返回 null 并触发一次后台下载。</summary>
        public static string GetRequire(string url, Action<string> onLoaded)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            lock (_requireCache)
            {
                if (_requireCache.TryGetValue(url, out var cached)) return cached;
                if (_requireLoading.Contains(url)) return null;
                _requireLoading.Add(url);
            }
            Task.Run(async () =>
            {
                string content = null;
                try
                {
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 AhoChatA/UserScript");
                        var resp = await client.GetStringAsync(url).ConfigureAwait(false);
                        content = resp;
                        Android.Util.Log.Info("Userscript", $"require OK {url} len={content?.Length}");
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Userscript", $"require FAIL {url} : {ex.GetType().Name} {ex.Message}");
                }
                lock (_requireCache)
                {
                    _requireLoading.Remove(url);
                    if (content != null) _requireCache[url] = content;
                }
                if (content != null) { try { onLoaded?.Invoke(content); } catch { } }
            });
            return null;
        }

        public List<UserScript> ScriptsFor(string url, string runAt)
        {
            var target = (runAt ?? "").ToLowerInvariant().Replace('_', '-');
            lock (_gate)
            {
                return _scripts.Where(s => s.Enabled
                    && (s.RunAt ?? "").ToLowerInvariant().Replace('_', '-') == target
                    && Matches(s, url)).ToList();
            }
        }

        /// <summary>油猴 @match 语法：scheme://host/path，支持 * 通配；特殊值 <all_urls>。</summary>
        private static bool MatchPattern(string pattern, string url)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            if (pattern == "<all_urls>" || pattern == "*") return true;
            try
            {
                var p = Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", "\\?");
                return Regex.IsMatch(url, "^" + p + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            catch { return false; }
        }

        /// <summary>@include / @exclude：支持 * 通配或 /regexp/flags。</summary>
        private static bool GlobMatch(string pattern, string url)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            if (pattern.Length > 2 && pattern.StartsWith("/"))
            {
                var lastSlash = pattern.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    var body = pattern.Substring(1, lastSlash - 1);
                    var flags = pattern.Substring(lastSlash + 1);
                    var opt = flags.Contains("i") ? RegexOptions.IgnoreCase : RegexOptions.None;
                    try { return Regex.IsMatch(url, body, opt | RegexOptions.Singleline); } catch { return false; }
                }
            }
            try
            {
                var p = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
                return Regex.IsMatch(url, "^" + p + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            catch { return false; }
        }

        /* ---------------- GM_xmlhttpRequest ---------------- */

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 不能由脚本自定义的请求头：交给底层管理，或放行会导致 responseText 乱码（gzip 不自动解压）
        private static readonly HashSet<string> _blockedRequestHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Connection", "Content-Length", "Expect", "Date", "If-Modified-Since",
            "Range", "Transfer-Encoding", "Proxy-Connection", "Accept-Encoding"
        };

        private static string HeaderValue(Dictionary<string, string> headers, string name)
        {
            if (headers == null) return null;
            foreach (var kv in headers)
                if (kv.Key != null && kv.Key.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(kv.Value)) return kv.Value.Trim();
            return null;
        }

        /// <summary>执行 GM_xmlhttpRequest，完成后把回调 JS 通过 onJs 送回 WebView 执行。</summary>
        public void StartXhr(string scriptId, string callbackId, string requestJson, Action<string> onJs)
        {
            if (onJs == null) return;
            string url = null, method = "GET", body = null, responseType = null;
            Dictionary<string, string> headers = null;
            try
            {
                var req = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(requestJson);
                if (req == null) return;
                if (req.TryGetValue("url", out var u)) url = u?.ToString();
                if (req.TryGetValue("method", out var mt)) method = (mt?.ToString() ?? "GET").ToUpperInvariant();
                if (req.TryGetValue("data", out var d)) body = d?.ToString();
                if (req.TryGetValue("responseType", out var rt)) responseType = rt?.ToString();
                if (req.TryGetValue("headers", out var h) && h != null)
                    headers = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(h.ToString());
            }
            catch { }
            if (string.IsNullOrEmpty(url)) return;
            Android.Util.Log.Info("Userscript", $"GM XHR start url={url} type={responseType} cb={callbackId}");

            _ = Task.Run(async () =>
            {
                string cbJs = null;
                try
                {
                    using (var request = new HttpRequestMessage(new HttpMethod(method), url))
                    {
                        // Content-Type 必须随 Content 走；其余请求头写到当前 request（绝不能写共享 HttpClient 的
                        // DefaultRequestHeaders，那会让一个脚本设的 UA 污染全 App 后续所有请求且并发互相覆盖）
                        var contentType = HeaderValue(headers, "Content-Type");
                        if (!string.IsNullOrEmpty(body))
                            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/octet-stream");
                        if (headers != null)
                            foreach (var kv in headers)
                            {
                                if (string.IsNullOrEmpty(kv.Key)) continue;
                                var key = kv.Key.Trim();
                                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                                if (_blockedRequestHeaders.Contains(key)) continue;
                                try { request.Headers.TryAddWithoutValidation(key, kv.Value); } catch { }
                            }
                        using (var resp = await _http.SendAsync(request))
                        {
                            var text = await resp.Content.ReadAsStringAsync();
                            var respObj = new Dictionary<string, object>
                            {
                                ["status"] = (int)resp.StatusCode,
                                ["statusText"] = resp.ReasonPhrase ?? "",
                                ["responseText"] = text,
                                ["response"] = text,
                                ["readyState"] = 4
                            };
                            // 脚本声明 responseType:'blob' 时回传原始字节的 base64；
                            // WebView 侧 __mchat_gmXhrCallback 会把它还原为真正的 Blob，
                            // 供 FileReader.readAsDataURL(response.response) 使用（图片转 PDF 脚本依赖）。
                            if (string.Equals(responseType, "blob", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(responseType, "arraybuffer", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                                    respObj["__mchatBase64"] = Convert.ToBase64String(bytes);
                                    var mime = resp.Content.Headers.ContentType?.MediaType;
                                    respObj["__mchatMime"] = string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
                                }
                                catch { }
                            }
                            var hs = new Dictionary<string, string>();
                            foreach (var h in resp.Headers) hs[h.Key] = string.Join(", ", h.Value);
                            foreach (var h in resp.Content.Headers) hs[h.Key] = string.Join(", ", h.Value);
                            respObj["headers"] = hs;
                            respObj["responseHeaders"] = hs;
                            var finalUrl = resp.RequestMessage != null && resp.RequestMessage.RequestUri != null
                                ? resp.RequestMessage.RequestUri.ToString() : url;
                            respObj["finalUrl"] = finalUrl;
                            respObj["responseUrl"] = finalUrl;
                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(respObj);
                            Android.Util.Log.Info("Userscript", $"GM XHR done cb={callbackId} status={resp.StatusCode} bytes={text.Length} reqLog={respObj.ContainsKey("__mchatBase64")}");
                            cbJs = "window.__mchat_gmXhrCallback&&window.__mchat_gmXhrCallback("
                                 + JsStr(callbackId) + "," + json + ");";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Userscript", $"GM XHR FAIL cb={callbackId} : {ex.GetType().Name} {ex.Message}");
                    var errObj = new Dictionary<string, object>
                    {
                        ["error"] = ex.Message, ["status"] = 0, ["statusText"] = "",
                        ["responseText"] = "", ["response"] = "", ["readyState"] = 4
                    };
                    cbJs = "window.__mchat_gmXhrCallback&&window.__mchat_gmXhrCallback("
                         + JsStr(callbackId) + "," + Newtonsoft.Json.JsonConvert.SerializeObject(errObj) + ");";
                }
                if (cbJs != null) onJs(cbJs);
            });
        }

        private static string JsStr(string s)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(s ?? "");
        }
    }
}

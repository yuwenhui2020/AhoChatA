using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Android.Content;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>
    /// 网页翻译引擎：MyMemory 公共翻译 API（免费、无需 key）+ 本地缓存。
    /// 只做纯文本翻译（网页 DOM 文本节点由 JS 抽取/回填），不依赖浏览器翻墙。
    /// 缓存落 SharedPreferences，避免重复翻译同一段文本、离线时可复用已翻译内容。
    /// </summary>
    public sealed class TranslateEngine
    {
        private const string PrefsName = "mchat_translate";
        private const string CachePrefix = "c:";
        private const string LangPref = "target_lang";
        private const int MaxTextLen = 480;      // MyMemory 单次 q 上限 500 字符
        private static readonly HttpClient Http = CreateHttp();

        private static readonly Lazy<TranslateEngine> _inst = new Lazy<TranslateEngine>(() => new TranslateEngine());
        public static TranslateEngine Instance => _inst.Value;

        private readonly ISharedPreferences _prefs;
        private readonly Dictionary<string, string> _memoryCache = new Dictionary<string, string>();

        private TranslateEngine()
        {
            try { _prefs = Android.App.Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private); }
            catch { _prefs = null; }
        }

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient();
            try { h.Timeout = TimeSpan.FromSeconds(12); } catch { }
            try { h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android) AhoChatA/1.0"); } catch { }
            return h;
        }

        /// <summary>支持的目标语言（MyMemory langpair 代码 → 本地显示名）。简体中文为默认。</summary>
        public static readonly string[][] Languages = new[]
        {
            new[] { "zh-CN", L10n.T("简体中文") }, new[] { "zh-TW", L10n.T("繁体中文") },
            new[] { "en", L10n.T("英语") }, new[] { "ja", L10n.T("日语") }, new[] { "ko", L10n.T("韩语") },
            new[] { "fr", L10n.T("法语") }, new[] { "de", L10n.T("德语") }, new[] { "es", L10n.T("西班牙语") },
            new[] { "ru", L10n.T("俄语") }, new[] { "pt", L10n.T("葡萄牙语") }, new[] { "it", L10n.T("意大利语") },
            new[] { "ar", L10n.T("阿拉伯语") }, new[] { "th", L10n.T("泰语") }, new[] { "vi", L10n.T("越南语") },
            new[] { "hi", L10n.T("印地语") }, new[] { "id", L10n.T("印尼语") }
        };

        public string TargetLang
        {
            get
            {
                try { return _prefs?.GetString(LangPref, "zh-CN") ?? "zh-CN"; }
                catch { return "zh-CN"; }
            }
            set
            {
                try { _prefs?.Edit()?.PutString(LangPref, value ?? "zh-CN")?.Apply(); } catch { }
            }
        }

        /// <summary>翻译一段文本。sourceLang 传 "auto" 表示自动检测。失败返回原文（不破坏页面）。</summary>
        public async Task<string> TranslateAsync(string text, string targetLang, string sourceLang = "auto")
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            string key = (sourceLang ?? "auto") + ">" + (targetLang ?? "zh-CN") + ">" + text;
            string cached = GetCached(key);
            if (cached != null) return cached;

            string src = string.IsNullOrEmpty(sourceLang) || sourceLang == "auto" ? "" : sourceLang;
            string pair = src + "|" + (string.IsNullOrEmpty(targetLang) ? "zh-CN" : targetLang);
            try
            {
                string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
                           + "&langpair=" + Uri.EscapeDataString(pair);
                string json = await Http.GetStringAsync(url).ConfigureAwait(false);
                var obj = JsonConvert.DeserializeObject<MyMemoryResp>(json);
                string result = obj?.responseData?.translatedText;
                if (string.IsNullOrWhiteSpace(result)) return text;
                result = System.Net.WebUtility.HtmlDecode(result);
                // MyMemory 失败时会返回 "MYMEMORY WARNING: ..." 文本
                if (result.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)) return text;
                SetCached(key, result);
                return result;
            }
            catch { return text; }
        }

        private string GetCached(string key)
        {
            string ck = CachePrefix + Sha1(key);
            lock (_memoryCache)
                if (_memoryCache.TryGetValue(ck, out string v)) return v;
            try
            {
                string v = _prefs?.GetString(ck, null);
                if (v != null) { lock (_memoryCache) _memoryCache[ck] = v; return v; }
            }
            catch { }
            return null;
        }

        private void SetCached(string key, string value)
        {
            string ck = CachePrefix + Sha1(key);
            lock (_memoryCache) _memoryCache[ck] = value;
            try { _prefs?.Edit()?.PutString(ck, value)?.Apply(); } catch { }
        }

        public void ClearCache()
        {
            lock (_memoryCache) _memoryCache.Clear();
            try
            {
                var ed = _prefs?.Edit();
                if (ed == null) return;
                var all = _prefs.All;
                foreach (var k in new List<string>(all.Keys))
                    if (k.StartsWith(CachePrefix, StringComparison.Ordinal)) ed.Remove(k);
                ed.Apply();
            }
            catch { }
        }

        private static string Sha1(string s)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                    var sb = new System.Text.StringBuilder(b.Length * 2);
                    foreach (byte x in b) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).Replace('/', '_').Replace('+', '-'); }
        }

        private sealed class MyMemoryResp
        {
            public ResponseData responseData { get; set; }
        }
        private sealed class ResponseData
        {
            public string translatedText { get; set; }
        }
    }
}

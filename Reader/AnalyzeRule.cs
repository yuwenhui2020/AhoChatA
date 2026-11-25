// ============================================================
// 阅读模块 · 规则求值引擎（legado AnalyzeRule 的 C# 移植）
//
// 完整对齐 legado 的规则语法：
//   · 默认模式（AnalyzeByJSoup）
//       children                 → 子元素
//       class.xxx                → class 包含 xxx 的元素
//       tag.xxx                  → 标签名 xxx
//       id.xxx                   → id = xxx
//       text.xxx                 → 自身文本包含 xxx 的元素
//       其它                     → 标准 CSS 选择器（Fizzler）
//     索引：tag.li.0 / .-1 / !0 / [0,2] / [1:3] / [-1:0]
//   · @ 链式：class.book-list@tag.li@tag.a@text
//   · && 拼接、|| 净化（取首个非空）、%% 交叉合并
//   · ## 正则替换（前半正则、后半替换串，支持 $1..$9）
//   · <js>…</js> / @js:… / 整条 {{…}} 走 Jint
//   · @css: / @xpath: / @json: 强制模式
//   · @put:{json} 存变量、@get:{key} 取变量
//   · jsLib 书源脚本库 + java.* 常用函数（ajax/base64/md5/timeFormat/…）
//
// 详见 sample/legado-master/app/src/main/java/io/legado/app/model/analyzeRule/
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jint.Runtime;
using Jint.Native;
using HtmlAgilityPack;
using Fizzler.Systems.HtmlAgilityPack;
using Jint;
using Newtonsoft.Json.Linq;

namespace AhoChatA.Reader
{
    public class AnalyzeRule
    {
        private object _content;          // HtmlNode | JToken | string
        private string _baseUrl;
        private string _rawText;          // 原始 HTML/JSON（JS 规则的 result 用，legado 同）
        private readonly BookSource _source;

        private readonly Dictionary<string, string> _vars = new Dictionary<string, string>();

        private enum Mode { Default, Js, Json, XPath, Css, Regex }

        private sealed class SourceRule
        {
            public Mode Mode;
            public string Rule = "";
            public SourceRule(string r, Mode m) { Rule = r; Mode = m; }
        }

        private static readonly Regex JsPattern = new Regex(
            "<js>([\\w\\W]*?)</js>|@js:([\\w\\W]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExpPattern = new Regex(
            "\\{\\{([\\w\\W]*?)\\}\\}", RegexOptions.Compiled);
        private static readonly Regex PutPattern = new Regex(
            "@put:(\\{[^}]+?\\})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GetPattern = new Regex(
            "@get:\\{([^}]+?)\\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public AnalyzeRule(BookSource source, string baseUrl = null)
        {
            _source = source;
            _baseUrl = baseUrl;
        }

        public AnalyzeRule SetHtml(string html, string baseUrl = null)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html ?? "");
            _content = doc.DocumentNode;
            _rawText = html ?? "";
            if (baseUrl != null) _baseUrl = baseUrl;
            return this;
        }

        public AnalyzeRule SetJson(string json, string baseUrl = null)
        {
            try { _content = JToken.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
            catch { _content = new JObject(); }
            _rawText = json ?? "";
            if (baseUrl != null) _baseUrl = baseUrl;
            return this;
        }

        public AnalyzeRule SetNode(object node, string baseUrl = null)
        {
            _content = node;
            if (baseUrl != null) _baseUrl = baseUrl;
            return this;
        }

        public AnalyzeRule SetBaseUrl(string baseUrl) { _baseUrl = baseUrl; return this; }

        // ---- 变量 ----
        public void Put(string k, string v) { if (!string.IsNullOrEmpty(k)) _vars[k] = v ?? ""; }

        public string Get(string k)
        {
            if (k == "baseUrl") return _baseUrl ?? "";
            return _vars.TryGetValue(k, out var v) ? v : "";
        }

        // ============================================================
        // 取值：单字符串
        // ============================================================
        public string GetString(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return "";
            var rules = SplitSourceRule(rule.Trim());
            object cur = _content;
            foreach (var sr in rules)
            {
                if (cur == null) break;
                cur = sr.Mode == Mode.Js
                    ? RunJs(sr.Rule, JsInput(cur), NodesOf(cur))
                    : EvalOne(cur, sr.Rule, sr.Mode);
            }
            return cur == null ? "" : cur.ToString().Trim();
        }

        // ============================================================
        // 取值：列表（bookList / chapterList 等）
        // ============================================================
        public List<object> GetList(string rule)
        {
            var outp = new List<object>();
            if (string.IsNullOrWhiteSpace(rule)) return outp;
            var rules = SplitSourceRule(rule.Trim());
            object cur = _content;
            foreach (var sr in rules)
            {
                if (cur == null) break;
                if (sr.Mode == Mode.Js)
                {
                    var v = RunJs(sr.Rule, JsInput(cur), NodesOf(cur));
                    if (v is ElementList el0) cur = el0.Nodes();
                    else if (v is List<HtmlNode> lh) cur = lh;
                    else
                    {
                        var txt = v?.ToString() ?? "";
                        cur = string.IsNullOrWhiteSpace(txt) ? null : ParseLoose(txt);   // JS 返回 [] / "" → 无结果
                    }
                }
                else cur = EvalElements(cur, sr.Rule, sr.Mode);
            }
            if (cur is List<object> lo) return lo;
            if (cur is List<HtmlNode> hn) { foreach (var n in hn) outp.Add(n); return outp; }
            if (cur is List<JToken> jl) { foreach (var n in jl) outp.Add(n); return outp; }
            if (cur is HtmlNode node) outp.Add(node);
            else if (cur is JToken jt) outp.Add(jt);
            else if (cur is string str && !string.IsNullOrWhiteSpace(str)) outp.Add(str);
            return outp;
        }

        // 兼容旧调用
        public List<string> GetStringList(string rule)
        {
            var res = GetList(rule);
            var outp = new List<string>();
            foreach (var o in res)
            {
                if (o is HtmlNode hn) outp.Add(CleanText(hn.InnerText));
                else if (o is JToken jt) outp.Add(jt.ToString().Trim().Trim('"'));
                else if (o is string s) outp.Add(s);
            }
            return outp;
        }

        // ============================================================
        // 规则拆分
        // ============================================================
        private List<SourceRule> SplitSourceRule(string rule)
        {
            var list = new List<SourceRule>();

            // 整条就是 {{js}}
            var t = rule.Trim();
            if (t.Length >= 4 && t.StartsWith("{{", StringComparison.Ordinal) && t.EndsWith("}}", StringComparison.Ordinal))
            {
                var inner = t.Substring(2, t.Length - 4);
                if (!inner.Contains("{{") && !inner.Contains("}}"))
                {
                    list.Add(new SourceRule(inner, Mode.Js));
                    return list;
                }
            }

            int start = 0;
            foreach (Match m in JsPattern.Matches(rule))
            {
                if (m.Index > start)
                {
                    var seg = rule.Substring(start, m.Index - start).Trim();
                    if (seg.Length > 0) list.Add(new SourceRule(seg, Mode.Default));
                }
                var code = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                list.Add(new SourceRule(code ?? "", Mode.Js));
                start = m.Index + m.Length;
            }
            if (start < rule.Length)
            {
                var seg = rule.Substring(start).Trim();
                if (seg.Length > 0) list.Add(new SourceRule(seg, Mode.Default));
            }
            if (list.Count == 0) list.Add(new SourceRule(rule, Mode.Default));
            return list;
        }

        /// <summary>按模式求值一步；返回 string（供串联的后续步骤使用）。</summary>
        private object EvalOne(object content, string rule, Mode mode)
        {
            rule = rule ?? "";
            // @put / @get
            rule = ExtractPutGet(rule, out var getValue);
            if (string.IsNullOrWhiteSpace(rule))
                return getValue ?? "";

            // 顶层 || 必须先于 ## 求值：否则「a@text##rx## || b@text」里的 || 后半段会被
            // 当成替换文本插进正文（手机上表现为规则原文泄露）。
            if (HasTopLevelOr(rule))
            {
                foreach (var part in rule.Split(new[] { "||" }, StringSplitOptions.None))
                {
                    var v = EvalOne(content, part, mode)?.ToString() ?? "";
                    if (v.Trim().Length > 0) return v;
                }
                return "";
            }

            // ## 正则替换：选择器##正则##替换（多组可连写 ## ：a##b##c##d）
            string replaceRx = null;
            var iRx = rule.IndexOf("##", StringComparison.Ordinal);
            if (iRx >= 0)
            {
                replaceRx = rule.Substring(iRx + 2);   // 含替换文本，交给 ApplyReplaceRules 解析
                rule = rule.Substring(0, iRx).Trim();
            }

            // 段内模式前缀（由 @ 链拆分后残留）
            var (m2, rest) = PeelPrefix(rule.Trim());
            if (m2 != Mode.Default) { mode = m2; rule = rest; }

            if (rule.Length == 0)
            {
                if (replaceRx != null) return ApplyReplaceRules(TextOf(content), replaceRx);
                return "";
            }

            if (mode == Mode.Default) mode = GuessMode(rule);

            var result = EvalCore(content, rule, mode);
            if (replaceRx != null && result != null)
                result = ApplyReplaceRules(result.ToString(), replaceRx);
            return result;
        }

        /// <summary>
        /// legado 的替换规则：`正则##替换` 可连写多组（`a##b##c##d`）。
        /// 开头的 `##` 只是与选择器的分隔符；只有一段时表示「删掉匹配内容」。
        /// </summary>
        public static string ApplyReplaceRules(string text, string rule)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(rule)) return text ?? "";
            var segs = rule.Split(new[] { "##" }, StringSplitOptions.None).ToList();
            while (segs.Count > 0 && segs[0].Trim().Length == 0) segs.RemoveAt(0);
            while (segs.Count > 0 && segs[segs.Count - 1].Trim().Length == 0 && segs.Count % 2 == 1)
                segs.RemoveAt(segs.Count - 1);
            for (int i = 0; i < segs.Count; i += 2)
            {
                var rx = segs[i];
                var rep = i + 1 < segs.Count ? segs[i + 1] : "";
                if (rx.Trim().Length == 0) continue;
                try { text = Regex.Replace(text, rx, rep, RegexOptions.Singleline); }
                catch { }
            }
            return text;
        }

        /// <summary>规则里是否出现顶层 ||（JS 代码里的 || 不算）。</summary>
        private static bool HasTopLevelOr(string rule)
        {
            int at = rule.IndexOf("||", StringComparison.Ordinal);
            if (at < 0) return false;
            foreach (var mark in new[] { "@js:", "<js>", "{{" })
            {
                int j = rule.IndexOf(mark, StringComparison.OrdinalIgnoreCase);
                if (j >= 0 && j < at) return false;
            }
            return true;
        }

        private object EvalCore(object content, string rule, Mode mode)
        {
            switch (mode)
            {
                case Mode.Json:
                    if (!(content is JToken)) content = ParseLoose(TextOf(content));
                    if (content is JToken jt) return EvalJsonString(jt, rule);
                    return "";
                case Mode.XPath:
                    if (!(content is HtmlNode)) content = ParseHtml(TextOf(content));
                    if (content is HtmlNode hx) return EvalXPathString(hx, rule);
                    return "";
                case Mode.Regex:
                    return EvalRegex(TextOf(content), rule);
                case Mode.Css:
                    if (!(content is HtmlNode)) content = ParseHtml(TextOf(content));
                    if (content is HtmlNode hc) return EvalCssRaw(hc, rule.Replace("@CSS:", "").Trim());
                    return "";
            }

            // Default：jsoup 语法
            if (!(content is HtmlNode) && !(content is JToken))
                content = ParseLoose(TextOf(content));
            if (content is JToken j2) return EvalJsonString(j2, rule);
            if (content is HtmlNode h) return EvalJsoupString(h, rule);
            return "";
        }

        private static Mode GuessMode(string rule)
        {
            if (rule.StartsWith("$.") || rule.StartsWith("$[")) return Mode.Json;
            if (rule.StartsWith("/") || rule.StartsWith("(/")) return Mode.XPath;
            return Mode.Default;
        }

        private static (Mode, string) PeelPrefix(string rule)
        {
            if (rule.StartsWith("@css:", StringComparison.OrdinalIgnoreCase)) return (Mode.Css, rule.Substring(5).Trim());
            if (rule.StartsWith("css:", StringComparison.OrdinalIgnoreCase)) return (Mode.Css, rule.Substring(4).Trim());
            if (rule.StartsWith("@xpath:", StringComparison.OrdinalIgnoreCase)) return (Mode.XPath, rule.Substring(7).Trim());
            if (rule.StartsWith("xpath:", StringComparison.OrdinalIgnoreCase)) return (Mode.XPath, rule.Substring(6).Trim());
            if (rule.StartsWith("@json:", StringComparison.OrdinalIgnoreCase)) return (Mode.Json, rule.Substring(6).Trim());
            if (rule.StartsWith("json:", StringComparison.OrdinalIgnoreCase)) return (Mode.Json, rule.Substring(5).Trim());
            if (rule.StartsWith("@js:", StringComparison.OrdinalIgnoreCase)) return (Mode.Js, rule.Substring(4));
            return (Mode.Default, rule);
        }

        /// <summary>处理 @put:{...} 与 @get:{key}，返回剩余规则；整条就是 @get 时输出取值。</summary>
        private string ExtractPutGet(string rule, out string getValue)
        {
            getValue = null;
            if (string.IsNullOrEmpty(rule)) return "";

            // 先剥掉 @put:{...}（里面可以写多条 key:rule）
            if (rule.IndexOf("@put:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (Match m in PutPattern.Matches(rule))
                {
                    foreach (var kv in ParsePutJson(m.Groups[1].Value))
                        Put(kv.Key, GetString(kv.Value));
                }
                rule = PutPattern.Replace(rule, "").Trim();
                if (rule.Length == 0) return "";
            }

            // 整条只有一个 @get:{key}：直接返回变量值
            var t = rule.Trim();
            var whole = GetPattern.Match(t);
            if (whole.Success && whole.Index == 0 && whole.Length == t.Length)
            {
                getValue = Get(whole.Groups[1].Value.Trim());
                return "";
            }

            // 混合场景：把 @get:{key} 就地替换成变量值
            if (rule.IndexOf("@get:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var sb = new StringBuilder();
                int last = 0;
                foreach (Match m in GetPattern.Matches(rule))
                {
                    sb.Append(rule, last, m.Index - last);
                    sb.Append(Get(m.Groups[1].Value.Trim()));
                    last = m.Index + m.Length;
                }
                sb.Append(rule.Substring(last));
                rule = sb.ToString().Trim();
            }
            return rule;
        }

        /// <summary>解析 {k:v, k2:v2}（宽松：键值可以不带引号，legado 也是宽松解析）。</summary>
        private static List<KeyValuePair<string, string>> ParsePutJson(string json)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            var s = json.Trim();
            if (s.StartsWith("{")) s = s.Substring(1);
            if (s.EndsWith("}")) s = s.Substring(0, s.Length - 1);

            var items = new List<string>();
            var sb = new StringBuilder();
            int depth = 0; bool inQ = false;
            foreach (char c in s)
            {
                if (c == '"') inQ = !inQ;
                if (!inQ)
                {
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') depth--;
                    else if (c == ',' && depth == 0) { items.Add(sb.ToString()); sb.Clear(); continue; }
                }
                sb.Append(c);
            }
            if (sb.Length > 0) items.Add(sb.ToString());

            foreach (var raw in items)
            {
                int c = raw.IndexOf(':');
                if (c <= 0) continue;
                var k = raw.Substring(0, c).Trim().Trim('"', '\'');
                var v = raw.Substring(c + 1).Trim().Trim('"');
                if (k.Length > 0) list.Add(new KeyValuePair<string, string>(k, v));
            }
            return list;
        }

        // ============================================================
        // 默认模式（jsoup）：GetString
        // ============================================================
        private string EvalJsoupString(HtmlNode root, string rule)
        {
            rule = EvalInlineExp(rule, root);
            if (rule.Length == 0) return "";

            var (type, parts) = SplitRule(rule, "&&", "||", "%%");
            if (parts.Count > 1)
            {
                // legado 先按 && / || / %% 切分，再对每个分支处理 ## 正则替换
                var results = new List<List<string>>();
                foreach (var p0 in parts)
                {
                    var p = p0.Trim();
                    if (p.Length == 0) continue;
                    var s0 = EvalOne(root, p, Mode.Default)?.ToString() ?? "";
                    var lines0 = s0.Length == 0
                        ? new List<string>()
                        : s0.Split('\n').Where(x => x.Trim().Length > 0).ToList();
                    if (lines0.Count > 0)
                    {
                        results.Add(lines0);
                        if (type == "||") break;
                    }
                }
                return JoinResults(type, results);
            }

            var results2 = new List<List<string>>();
            {
                var r = EvalJsoupChainString(root, rule);
                if (r.Count > 0) results2.Add(r);
            }
            return JoinResults(type, results2);
        }

        /// <summary>按 &&（全拼）/ ||（取第一个有结果的）/ %%（交错）合并多个分支的结果。</summary>
        private static string JoinResults(string type, List<List<string>> results)
        {
            if (results.Count == 0) return "";
            if (type == "%%")
            {
                var outp = new List<string>();
                int max = results.Max(x => x.Count);
                for (int i = 0; i < max; i++)
                    foreach (var r in results)
                        if (i < r.Count) outp.Add(r[i]);
                return string.Join("\n", outp);
            }
            var all = new List<string>();
            foreach (var r in results) all.AddRange(r);
            return string.Join("\n", all);
        }

        /// <summary>处理单段 @ 链，返回文本列表。</summary>
        private List<string> EvalJsoupChainString(HtmlNode root, string rule)
        {
            var outp = new List<string>();
            if (rule.Length == 0) { var t = CleanText(root.InnerText); if (t.Length > 0) outp.Add(t); return outp; }

            var (_, segs) = SplitRule(rule, "@");
            // 去掉首尾空白段
            segs = segs.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (segs.Count == 0) return outp;

            if (segs.Count == 1)
            {
                // 单段：既可能是选择器，也可能是属性名（@href / text / html）
                var single = segs[0];
                var prop = AsPropertyName(single);
                if (prop != null)
                {
                    var v = GetResultLast(new List<HtmlNode> { root }, prop);
                    if (v.Length > 0) { outp.Add(v); return outp; }
                }
                var els = SelectSingle(root, single);
                if (els.Count > 0)
                {
                    var t = GetResultLast(els, "text");
                    if (t.Length > 0) outp.Add(t);
                    return outp;
                }
                var v2 = GetResultLast(new List<HtmlNode> { root }, single.TrimStart('@'));
                if (v2.Length > 0) outp.AddRange(v2.Split('\n'));
                return outp;
            }

            var cur = new List<HtmlNode> { root };
            for (int i = 0; i < segs.Count - 1; i++)
            {
                var next = new List<HtmlNode>();
                var seg = segs[i];
                // 段内模式前缀（如 @css: / @xpath:）
                var (pm, pr) = PeelPrefix(seg);
                if (pm == Mode.Css) { foreach (var e in cur) next.AddRange(EvalCssNodes(e, pr)); }
                else foreach (var e in cur) next.AddRange(SelectSingle(e, seg));
                cur = next;
                if (cur.Count == 0) break;
            }
            if (cur.Count == 0) return outp;

            var last = segs[segs.Count - 1];
            var (lm, lr) = PeelPrefix(last);
            if (lm == Mode.Css) last = lr;

            var lastProp = AsPropertyName(last);
            if (lastProp != null)
            {
                var s = GetResultLast(cur, lastProp);
                if (s.Length > 0) { outp.AddRange(s.Split('\n')); return outp; }
            }
            // 最后一段是选择器（不是属性名）
            var moreEls = new List<HtmlNode>();
            foreach (var e in cur) moreEls.AddRange(SelectSingle(e, last));
            if (moreEls.Count > 0)
            {
                var s = GetResultLast(moreEls, "text");
                if (s.Length > 0) outp.AddRange(s.Split('\n'));
                return outp;
            }
            // 兜底：当属性名处理
            var fb = GetResultLast(cur, last.TrimStart('@'));
            if (fb.Length > 0) outp.AddRange(fb.Split('\n'));
            return outp;
        }

        /// <summary>判断一个规则段是否为属性取值（text/html/ownText/textNodes/all/href/…）。</summary>
        private static string AsPropertyName(string rule)
        {
            if (string.IsNullOrEmpty(rule)) return null;
            var r = rule.Trim();
            if (r.StartsWith("@")) return r.Substring(1);
            switch (r.ToLowerInvariant())
            {
                case "text":
                case "textnodes":
                case "owntext":
                case "html":
                case "all":
                    return r.ToLowerInvariant();
            }
            // 纯属性名（href/src/data-src/…），不含选择器特征
            if (r.Length > 0 && r.IndexOfAny(new[] { '.', '#', '[', ' ', '>', ':', '*' }) < 0
                && !r.StartsWith("class") && !r.StartsWith("tag") && !r.StartsWith("id")
                && !r.StartsWith("children") && !r.StartsWith("text"))
            {
                // 形如 href / src / title / data-original
                if (Regex.IsMatch(r, @"^[A-Za-z_][\w:-]*$")) return r;
            }
            return null;
        }

        private string GetResultLast(List<HtmlNode> els, string lastRule)
        {
            var list = new List<string>();
            switch ((lastRule ?? "").ToLowerInvariant())
            {
                case "text":
                    foreach (var e in els) { var t = CleanText(e.InnerText); if (t.Length > 0) list.Add(t); }
                    break;
                case "textnodes":
                    foreach (var e in els)
                    {
                        var tn = e.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Text)
                            .Select(c => CleanText(c.InnerText)).Where(x => x.Length > 0);
                        var s = string.Join("\n", tn);
                        if (s.Length > 0) list.Add(s);
                    }
                    break;
                case "owntext":
                    foreach (var e in els) { var t = OwnText(e); if (t.Length > 0) list.Add(t); }
                    break;
                case "html":
                    foreach (var e in els)
                    {
                        var clone = e.CloneNode(true);
                        foreach (var bad in clone.DescendantsAndSelf()
                                     .Where(x => x.Name == "script" || x.Name == "style").ToList())
                            bad.Remove();
                        var html = clone.OuterHtml;
                        if (!string.IsNullOrWhiteSpace(html)) list.Add(html);
                    }
                    break;
                case "all":
                    foreach (var e in els) list.Add(e.OuterHtml);
                    break;
                default:
                    foreach (var e in els)
                    {
                        var v = e.GetAttributeValue(lastRule, "");
                        if (!string.IsNullOrWhiteSpace(v) && !list.Contains(v)) list.Add(v);
                    }
                    break;
            }
            return string.Join("\n", list);
        }

        // ============================================================
        // 默认模式（jsoup）：GetElements
        // ============================================================
        private object EvalElements(object content, string rule, Mode mode)
        {
            rule = ExtractPutGet(rule ?? "", out _);
            if (rule.Length == 0) return new List<object>();

            var (pm, pr) = PeelPrefix(rule.Trim());
            if (pm != Mode.Default) { mode = pm; rule = pr; }
            if (mode == Mode.Default) mode = GuessMode(rule);

            if (mode == Mode.Json)
            {
                if (!(content is JToken)) content = ParseLoose(TextOf(content));
                if (content is JToken jt) return EvalJsonList(jt, rule);
                return new List<object>();
            }
            if (mode == Mode.Css)
            {
                if (!(content is HtmlNode)) content = ParseHtml(TextOf(content));
                if (content is HtmlNode hc) return EvalCssNodes(hc, rule);
                return new List<object>();
            }
            if (mode == Mode.XPath)
            {
                if (!(content is HtmlNode)) content = ParseHtml(TextOf(content));
                if (content is HtmlNode hx) return EvalXPathNodes(hx, rule);
                return new List<object>();
            }
            if (mode == Mode.Regex)
            {
                var l = new List<object>();
                var s = EvalRegex(TextOf(content), rule);
                if (!string.IsNullOrEmpty(s)) l.Add(s);
                return l;
            }

            if (!(content is HtmlNode) && !(content is JToken)) content = ParseLoose(TextOf(content));
            if (content is JToken j3) return EvalJsonList(j3, rule);
            if (content is HtmlNode h) return EvalJsoupNodes(h, rule);
            return new List<object>();
        }

        private object EvalJsoupNodes(HtmlNode root, string rule)
        {
            rule = EvalInlineExp(rule, root);
            var (type, parts) = SplitRule(rule, "&&", "||", "%%");
            var groups = new List<List<HtmlNode>>();
            foreach (var p in parts)
            {
                var els = EvalJsoupChainNodes(root, p.Trim());
                if (els.Count > 0)
                {
                    groups.Add(els);
                    if (type == "||") break;
                }
            }
            var outp = new List<HtmlNode>();
            if (type == "%%")
            {
                int max = groups.Count == 0 ? 0 : groups.Max(x => x.Count);
                for (int i = 0; i < max; i++)
                    foreach (var g in groups)
                        if (i < g.Count) outp.Add(g[i]);
            }
            else foreach (var g in groups) outp.AddRange(g);
            return outp;
        }

        private List<HtmlNode> EvalJsoupChainNodes(HtmlNode root, string rule)
        {
            var outp = new List<HtmlNode>();
            if (rule.Length == 0) { outp.Add(root); return outp; }

            var (_, segs) = SplitRule(rule, "@");
            segs = segs.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (segs.Count == 0) { outp.Add(root); return outp; }

            var cur = new List<HtmlNode> { root };
            foreach (var seg in segs)
            {
                var (pm, pr) = PeelPrefix(seg);
                var next = new List<HtmlNode>();
                foreach (var e in cur)
                {
                    if (pm == Mode.Css) next.AddRange(EvalCssNodes(e, pr));
                    else next.AddRange(SelectSingle(e, seg));
                }
                cur = next;
                if (cur.Count == 0) break;
            }
            return cur;
        }

        // ============================================================
        // 单段选择（legado ElementsSingle）
        // ============================================================
        private List<HtmlNode> SelectSingle(HtmlNode el, string rule)
        {
            rule = (rule ?? "").Trim();
            if (rule.StartsWith("@")) rule = rule.Substring(1).Trim();
            if (rule.Length == 0) return Children(el);

            var ind = ParseIndexes(rule);
            List<HtmlNode> els;
            var before = ind.Before.Trim();

            if (before.Length == 0) els = Children(el);
            else
            {
                var dot = before.IndexOf('.');
                var head = dot > 0 ? before.Substring(0, dot) : before;
                var tail = dot > 0 ? before.Substring(dot + 1) : "";
                var segs = tail.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                switch (head)
                {
                    case "children":
                        els = Children(el);
                        break;
                    case "class":
                        els = DescendantsAndSelf(el).ToList();
                        foreach (var cls in segs) els = els.Where(n => HasClass(n, cls)).ToList();
                        break;
                    case "tag":
                        els = DescendantsAndSelf(el)
                            .Where(n => n.Name.Equals(segs.Length > 0 ? segs[0] : tail,
                                                       StringComparison.OrdinalIgnoreCase)).ToList();
                        // tag.div.item 这种写法：后面的段按 class 继续过滤
                        for (int j = 1; j < segs.Length; j++)
                        {
                            var cls = segs[j];
                            els = els.Where(n => HasClass(n, cls)).ToList();
                        }
                        break;
                    case "id":
                        els = DescendantsAndSelf(el)
                            .Where(n => n.GetAttributeValue("id", "") == (segs.Length > 0 ? segs[0] : tail)).ToList();
                        break;
                    case "text":
                        var kw = string.Join(".", segs);
                        els = DescendantsAndSelf(el)
                            .Where(n => n.ChildNodes.Any(c => c.NodeType == HtmlNodeType.Text
                                                             && CleanText(c.InnerText).Contains(kw)))
                            .ToList();
                        break;
                    default:
                        els = EvalCssNodes(el, before);
                        break;
                }
            }
            return ApplyIndexes(els, ind);
        }

        private static IEnumerable<HtmlNode> DescendantsAndSelf(HtmlNode n)
        {
            yield return n;
            foreach (var d in n.Descendants()) yield return d;
        }

        private static bool HasClass(HtmlNode n, string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            var v = n.GetAttributeValue("class", "");
            if (string.IsNullOrEmpty(v)) return false;
            foreach (var part in v.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                if (part == cls) return true;
            return false;
        }

        private static List<HtmlNode> Children(HtmlNode el)
        {
            return el.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToList();
        }

        // ---- 索引解析 ----
        private sealed class IndexSpec
        {
            public string Before = "";
            public bool Exclude;
            public readonly List<int> Singles = new List<int>();
            public readonly List<(int? start, int? end, int step)> Ranges = new List<(int?, int?, int)>();
            public readonly List<object> Order = new List<object>();   // int 或 (int?,int?,int)
        }

        private static IndexSpec ParseIndexes(string rule)
        {
            var spec = new IndexSpec { Before = rule };
            var r = rule.Trim();

            // [ ... ] 形式
            if (r.EndsWith("]"))
            {
                int open = FindLastBracket(r);
                if (open > 0)
                {
                    var inside = r.Substring(open + 1, r.Length - open - 2).Trim();
                    if (Regex.IsMatch(inside, @"^!?[\d\s,:\-]+$") && inside.Length > 0)
                    {
                        spec.Before = r.Substring(0, open);
                        spec.Exclude = inside.StartsWith("!");
                        if (spec.Exclude) inside = inside.Substring(1);
                        foreach (var it0 in inside.Split(','))
                        {
                            var it = it0.Trim();
                            if (it.Length == 0) continue;
                            var ps = it.Split(':');
                            if (ps.Length == 1)
                            {
                                if (int.TryParse(ps[0], out int n)) { spec.Singles.Add(n); spec.Order.Add(n); }
                            }
                            else
                            {
                                int? st = ps[0].Length > 0 && int.TryParse(ps[0], out int a) ? a : (int?)null;
                                int? en = ps.Length > 1 && ps[1].Length > 0 && int.TryParse(ps[1], out int b) ? b : (int?)null;
                                int sp = ps.Length > 2 && int.TryParse(ps[2], out int c) ? c : 1;
                                var rg = (st, en, sp);
                                spec.Ranges.Add(rg);
                                spec.Order.Add(rg);
                            }
                        }
                        return spec;
                    }
                }
            }

            // .0 / .-1 / !0 / .1:3 形式（从尾部反扫）
            var nums = new List<int>();
            int i2 = r.Length - 1;
            bool minus = false;
            var buf = new StringBuilder();
            char split = ' ';
            int cut = -1;
            while (i2 >= 0)
            {
                char c = r[i2];
                if (c == ' ') { i2--; continue; }
                if (c >= '0' && c <= '9') { buf.Insert(0, c); i2--; continue; }
                if (c == '-') { minus = true; i2--; continue; }
                if (c == '.' || c == '!' || c == ':')
                {
                    if (buf.Length > 0 || minus)
                    {
                        int v = 0;
                        int.TryParse(buf.ToString(), out v);
                        nums.Add(minus ? -v : v);
                        buf.Clear(); minus = false;
                    }
                    if (c == '.' || c == '!')
                    {
                        split = c;
                        cut = i2;
                        break;
                    }
                    i2--;
                    continue;
                }
                break;
            }
            if (cut > 0 && nums.Count > 0)
            {
                spec.Before = r.Substring(0, cut);
                spec.Exclude = split == '!';
                // nums 是逆向收集的，反转回正序
                for (int k = nums.Count - 1; k >= 0; k--)
                {
                    spec.Singles.Add(nums[k]);
                    spec.Order.Add(nums[k]);
                }
            }
            return spec;
        }

        private static int FindLastBracket(string s)
        {
            int depth = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                if (s[i] == ']') depth++;
                else if (s[i] == '[')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static List<HtmlNode> ApplyIndexes(List<HtmlNode> els, IndexSpec spec)
        {
            if (spec.Order.Count == 0) return els;
            int len = els.Count;
            var picked = new List<int>();

            foreach (var o in spec.Order)
            {
                if (o is int n)
                {
                    int v = n;
                    if (v < 0) v += len;
                    if (v >= 0 && v < len && !picked.Contains(v)) picked.Add(v);
                }
                else if (o is ValueTuple<int?, int?, int> rg)
                {
                    int st = rg.Item1 ?? 0; if (st < 0) st += len;
                    int en = rg.Item2 ?? (len - 1); if (en < 0) en += len;
                    int step = rg.Item3 == 0 ? 1 : Math.Abs(rg.Item3);
                    if ((st < 0 && en < 0) || (st >= len && en >= len)) continue;
                    st = Math.Max(0, Math.Min(st, len - 1));
                    en = Math.Max(0, Math.Min(en, len - 1));
                    if (st <= en)
                        for (int k1 = st; k1 <= en; k1 += step) if (!picked.Contains(k1)) picked.Add(k1);
                    else
                        for (int k2 = st; k2 >= en; k2 -= step) if (!picked.Contains(k2)) picked.Add(k2);
                }
            }

            if (spec.Exclude)
            {
                var keep = new List<HtmlNode>();
                for (int k = 0; k < len; k++) if (!picked.Contains(k)) keep.Add(els[k]);
                return keep;
            }
            var res = new List<HtmlNode>();
            foreach (var k in picked) res.Add(els[k]);
            return res;
        }

        // ============================================================
        // CSS / XPath / JSON / Regex 求值
        // ============================================================
        /// <summary>
        /// CSS 选择。Fizzler 只覆盖 CSS3，而 legado 用的是 jsoup 语义，
        /// 书源里大量出现 <c>:eq(n)</c> / <c>:lt</c> / <c>:gt</c> / <c>:contains()</c> /
        /// <c>:matches()</c> / <c>:has()</c> / <c>:nth-of-type()</c> 等 jsoup 专有伪类，
        /// 这里先摘出来自己算，剩下的交给 Fizzler。
        /// </summary>
        private List<HtmlNode> EvalCssNodes(HtmlNode el, string css)
        {
            var outp = new List<HtmlNode>();
            css = (css ?? "").Trim();
            if (css.Length == 0) return outp;
            foreach (var part0 in SplitTopLevel(css, ','))
            {
                var part = part0.Trim();
                if (part.Length == 0) continue;
                // 尾部 .N / .-N 索引（legado 允许写在 CSS 选择器末尾：`.author a.1`）
                var idx = ParseIndexes(part);
                if (idx.Before.Trim().Length > 0 && idx.Before.Trim() != part)
                {
                    var picked = ApplyIndexes(SelectChain(el, idx.Before.Trim()), idx);
                    foreach (var n in picked)
                        if (!outp.Contains(n)) outp.Add(n);
                    continue;
                }
                var got = SelectChain(el, part);
                // 有些站点 HTML 结构畸形（如 <a><dd>…</dd></a>），我们的解析树与 jsoup 不同，
                // 兄弟组合符会选不到；此时退化为后代匹配再试一次（只在 0 结果时兜底）。
                if (got.Count == 0 && (part.IndexOf('~') >= 0 || part.IndexOf('+') >= 0))
                    got = SelectChain(el, RelaxCombinators(part));
                foreach (var n in got)
                    if (!outp.Contains(n)) outp.Add(n);
            }
            return outp;
        }

        /// <summary>把顶层的兄弟组合符 ~ / + 换成后代空格（容错用）。</summary>
        private static string RelaxCombinators(string sel)
        {
            var sb = new StringBuilder();
            int depth = 0;
            char q = '\0';
            foreach (var c in sel)
            {
                if (q != '\0') { sb.Append(c); if (c == q) q = '\0'; continue; }
                if (c == '\'' || c == '"') { q = c; sb.Append(c); continue; }
                if (c == '(' || c == '[') depth++;
                if (c == ')' || c == ']') { if (depth > 0) depth--; }
                if (depth == 0 && (c == '~' || c == '+')) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>把选择器拆成 [(组合符, 复合选择器)]，组合符：'' 后代 / '&gt;' 子 / '+' 相邻 / '~' 兄弟。</summary>
        private static List<(char comb, string compound)> TokenizeSelector(string sel)
        {
            var toks = new List<(char, string)>();
            var sb = new StringBuilder();
            char comb = '\0';
            int depth = 0;
            char quote = '\0';

            void Flush(char c)
            {
                var comp = sb.ToString().Trim();
                sb.Clear();
                if (comp.Length > 0) toks.Add((toks.Count == 0 ? '\0' : (comb == '\0' ? ' ' : comb), comp));
                // 组合符后面的空白不能把组合符覆盖成后代匹配（`dt ~ a` 曾退化成 `dt a`）
                else if (c == ' ' && comb != '\0' && comb != ' ') return;
                comb = c;
            }

            for (int i = 0; i < sel.Length; i++)
            {
                char c = sel[i];
                if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == '(' || c == '[') { depth++; sb.Append(c); continue; }
                if (c == ')' || c == ']') { if (depth > 0) depth--; sb.Append(c); continue; }
                if (depth > 0) { sb.Append(c); continue; }

                if (c == '>' || c == '+' || c == '~')
                {
                    Flush(c);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    // 空白先记下来，后面紧跟组合符就当组合符处理
                    int j = i;
                    while (j < sel.Length && char.IsWhiteSpace(sel[j])) j++;
                    if (j < sel.Length && (sel[j] == '>' || sel[j] == '+' || sel[j] == '~')) { i = j - 1; continue; }
                    Flush(' ');
                    i = j - 1;
                    continue;
                }
                sb.Append(c);
            }
            Flush('\0');
            return toks;
        }

        /// <summary>按组合符链逐段选择，使 :nth-of-type / :eq 等伪类只作用于所在的那一段。</summary>
        private List<HtmlNode> SelectChain(HtmlNode scope, string sel)
        {
            var toks = TokenizeSelector(sel);
            if (toks.Count == 0) return new List<HtmlNode>();

            // `html > body > …` 这种从文档根写起的选择器：从文档根走，最后限定回当前子树
            var docRoot = scope.OwnerDocument?.DocumentNode;
            bool rooted = RootAnchored(sel) && docRoot != null && docRoot != scope;
            var start = rooted ? docRoot : scope;

            var cur = MatchInScope(start, toks[0].compound);
            for (int i = 1; i < toks.Count; i++)
            {
                var comb = toks[i].comb;
                var comp = toks[i].compound;
                var cand = new List<HtmlNode>();
                switch (comb)
                {
                    case '>':
                        foreach (var p in cur) cand.AddRange(Children(p));
                        break;
                    case '+':
                        foreach (var p in cur)
                        {
                            var s = p.NextSibling;
                            while (s != null && s.NodeType != HtmlNodeType.Element) s = s.NextSibling;
                            if (s != null) cand.Add(s);
                        }
                        break;
                    case '~':
                        foreach (var p in cur)
                        {
                            var s = p.NextSibling;
                            while (s != null)
                            {
                                if (s.NodeType == HtmlNodeType.Element) cand.Add(s);
                                s = s.NextSibling;
                            }
                        }
                        break;
                    default:
                        foreach (var p in cur) cand.AddRange(AllDescendants(p));
                        break;
                }
                var next = new List<HtmlNode>();
                foreach (var n in cand)
                    if (!next.Contains(n) && MatchCompound(n, comp)) next.Add(n);
                // 畸形 HTML 下兄弟顺序可能与 jsoup 不同：~ 选不到时，退回「同父兄弟」匹配
                if (next.Count == 0 && comb == '~')
                {
                    foreach (var p in cur)
                    {
                        var parent = p.ParentNode;
                        if (parent == null) continue;
                        foreach (var s in Children(parent))
                            if (s != p && !next.Contains(s) && MatchCompound(s, comp)) next.Add(s);
                    }
                }
                cur = next;
                if (cur.Count == 0) break;
            }
            if (rooted)
                cur = cur.Where(n => n == scope || IsDescendantOf(n, scope)).ToList();
            return cur;
        }

        private static List<HtmlNode> AllDescendants(HtmlNode n)
        {
            return n.Descendants().Where(d => d.NodeType == HtmlNodeType.Element).ToList();
        }

        /// <summary>在当前节点范围内选一个复合选择器（后代语义）。</summary>
        private List<HtmlNode> MatchInScope(HtmlNode scope, string compound)
        {
            var filters = new List<(string kind, string arg)>();
            var cleaned = ExtractJsoupFilters(compound, filters);
            List<HtmlNode> found;
            if (cleaned.Length == 0)
                found = AllDescendants(scope);
            else
            {
                try { found = scope.QuerySelectorAll(cleaned).ToList(); }
                catch { found = new List<HtmlNode>(); }
                if (found.Count == 0 && RootAnchored(cleaned))
                {
                    var docRoot = scope.OwnerDocument?.DocumentNode;
                    if (docRoot != null && docRoot != scope)
                    {
                        try
                        {
                            found = docRoot.QuerySelectorAll(cleaned)
                                           .Where(n => n == scope || IsDescendantOf(n, scope)).ToList();
                        }
                        catch { }
                    }
                }
            }
            foreach (var f in filters) found = ApplyJsoupFilter(found, f.kind, f.arg);
            return found;
        }

        /// <summary>判断单个元素是否匹配一个复合选择器。</summary>
        private bool MatchCompound(HtmlNode n, string compound)
        {
            var filters = new List<(string kind, string arg)>();
            var cleaned = ExtractJsoupFilters(compound, filters);
            if (cleaned.Length > 0)
            {
                // 还带 CSS 标准伪类（:first-child 等）→ 交给 Fizzler 在父级里判一次
                if (cleaned.IndexOf(':') >= 0)
                {
                    var p = n.ParentNode;
                    if (p == null) return false;
                    if (!EvalCssNodes(p, compound).Contains(n)) return false;
                }
                else if (!MatchSimple(n, cleaned)) return false;
            }
            foreach (var f in filters)
                if (ApplyJsoupFilter(new List<HtmlNode> { n }, f.kind, f.arg).Count == 0) return false;
            return true;
        }

        /// <summary>按顶层分隔符切分（不切括号/引号里的）。</summary>
        internal static List<string> SplitTopLevel(string s, char sep)
        {
            var parts = new List<string>();
            int last = 0, depth = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote && s[i - 1] != '\\') quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(' || c == '[') { depth++; continue; }
                if (c == ')' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0 && c == sep) { parts.Add(s.Substring(last, i - last)); last = i + 1; }
            }
            parts.Add(s.Substring(last));
            return parts;
        }

        private static readonly string[] JsoupPseudos =
        {
            ":eq(", ":lt(", ":gt(", ":contains(", ":containsown(", ":matches(", ":matchesown(",
            ":has(", ":nth-of-type(", ":first-of-type", ":last-of-type", ":not("
        };

        /// <summary>摘出 jsoup 专有伪类，返回可交给 Fizzler 的选择器。</summary>
        private static string ExtractJsoupFilters(string sel, List<(string, string)> bag)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < sel.Length)
            {
                if (sel[i] == ':')
                {
                    string matched = null;
                    foreach (var p in JsoupPseudos)
                        if (i + p.Length <= sel.Length &&
                            string.Compare(sel, i, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) == 0)
                        { matched = p; break; }

                    if (matched != null)
                    {
                        var kind = matched.Substring(1).TrimEnd('(').ToLowerInvariant();
                        if (matched.EndsWith("(", StringComparison.Ordinal))
                        {
                            int start = i + matched.Length;
                            int dep = 1;
                            int j = start;
                            char q = '\0';
                            while (j < sel.Length)
                            {
                                char c = sel[j];
                                if (q != '\0') { if (c == q) q = '\0'; }
                                else if (c == '\'' || c == '"') q = c;
                                else if (c == '(') dep++;
                                else if (c == ')') { dep--; if (dep == 0) break; }
                                j++;
                            }
                            var arg = sel.Substring(start, Math.Max(0, j - start));
                            bag.Add((kind, arg));
                            i = j + 1;
                            continue;
                        }
                        bag.Add((kind, ""));
                        i += matched.Length;
                        continue;
                    }
                }
                sb.Append(sel[i]);
                i++;
            }
            var cleaned = sb.ToString().Trim();
            // 选择器可能只剩下 "a" 之类；末尾多余的空格无所谓
            return cleaned;
        }

        /// <summary>jsoup 元素兄弟序号（只数元素节点）。</summary>
        private static int SiblingIndex(HtmlNode n)
        {
            var p = n.ParentNode;
            if (p == null) return 0;
            int k = 0;
            foreach (var c in p.ChildNodes)
            {
                if (c.NodeType != HtmlNodeType.Element) continue;
                if (c == n) return k;
                k++;
            }
            return k;
        }

        /// <summary>同标签兄弟序号（1 起，对应 :nth-of-type）。</summary>
        private static int TypeIndex(HtmlNode n)
        {
            var p = n.ParentNode;
            if (p == null) return 1;
            int k = 0;
            foreach (var c in p.ChildNodes)
            {
                if (c.NodeType != HtmlNodeType.Element) continue;
                if (!string.Equals(c.Name, n.Name, StringComparison.OrdinalIgnoreCase)) continue;
                k++;
                if (c == n) return k;
            }
            return k;
        }

        private List<HtmlNode> ApplyJsoupFilter(List<HtmlNode> els, string kind, string arg)
        {
            var a = (arg ?? "").Trim().Trim('\'', '"');
            switch (kind)
            {
                case "eq":
                    {
                        if (!int.TryParse(a, out int idx)) return els;
                        if (idx < 0)   // 负数按「倒数第几个」处理（legado 书源里常见 :eq(-1)）
                        {
                            int k = els.Count + idx;
                            return k >= 0 && k < els.Count ? new List<HtmlNode> { els[k] } : new List<HtmlNode>();
                        }
                        return els.Where(n => SiblingIndex(n) == idx).ToList();
                    }
                case "lt":
                    {
                        if (!int.TryParse(a, out int idx)) return els;
                        return els.Where(n => SiblingIndex(n) < idx).ToList();
                    }
                case "gt":
                    {
                        if (!int.TryParse(a, out int idx)) return els;
                        return els.Where(n => SiblingIndex(n) > idx).ToList();
                    }
                case "contains":
                    return els.Where(n => CleanText(n.InnerText).IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                case "containsown":
                    return els.Where(n => OwnText(n).IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                case "matches":
                case "matchesown":
                    {
                        try
                        {
                            var re = new Regex(a, RegexOptions.IgnoreCase);
                            return els.Where(n => re.IsMatch(kind == "matches" ? n.InnerText : OwnText(n))).ToList();
                        }
                        catch { return els; }
                    }
                case "has":
                    {
                        var outp = new List<HtmlNode>();
                        foreach (var n in els)
                        {
                            if (a.Length == 0) { if (n.Descendants().Any()) outp.Add(n); continue; }
                            if (EvalCssNodes(n, a).Count > 0) outp.Add(n);
                        }
                        return outp;
                    }
                case "nth-of-type":
                    {
                        if (!int.TryParse(a, out int idx)) return els;
                        return els.Where(n => TypeIndex(n) == idx).ToList();
                    }
                case "first-of-type":
                    return els.Where(n => TypeIndex(n) == 1).ToList();
                case "last-of-type":
                    return els.Where(n => n.ParentNode != null &&
                        !n.ParentNode.ChildNodes.Any(c => c.NodeType == HtmlNodeType.Element && c != n
                            && string.Equals(c.Name, n.Name, StringComparison.OrdinalIgnoreCase)
                            && TypeIndex(c) > TypeIndex(n))).ToList();
                case "not":
                    {
                        if (a.Length == 0) return els;
                        return els.Where(n => !MatchesSimpleOrScoped(n, a)).ToList();
                    }
                default:
                    return els;
            }
        }

        /// <summary>
        /// 判断元素是否匹配一个「简单选择器」。Fizzler 的 :not() 不支持属性选择器，
        /// 所以 :not(...) 统一自己算：简单选择器直接判定，带组合符/伪类的退回父级求值。
        /// </summary>
        private bool MatchesSimpleOrScoped(HtmlNode n, string sel)
        {
            sel = (sel ?? "").Trim();
            if (sel.Length == 0) return true;
            if (sel.IndexOfAny(new[] { ' ', '>', '+', '~', ',', ':' }) >= 0)
            {
                var p = n.ParentNode;
                if (p == null) return false;
                try { return EvalCssNodes(p, sel).Contains(n); } catch { return false; }
            }
            return MatchSimple(n, sel);
        }

        private static bool MatchSimple(HtmlNode n, string sel)
        {
            // 期望形式：tag / * / .cls / #id / [attr] / 组合
            int i = 0;
            // tag
            if (sel[0] != '.' && sel[0] != '#' && sel[0] != '[')
            {
                int j = 0;
                while (j < sel.Length && (char.IsLetterOrDigit(sel[j]) || sel[j] == '-' || sel[j] == '_' || sel[j] == '*')) j++;
                var tag = sel.Substring(0, j);
                if (tag.Length > 0 && tag != "*" && !n.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)) return false;
                i = j;
            }
            while (i < sel.Length)
            {
                char c = sel[i];
                if (c == '.')
                {
                    int j = i + 1;
                    while (j < sel.Length && sel[j] != '.' && sel[j] != '#' && sel[j] != '[') j++;
                    if (!HasClass(n, sel.Substring(i + 1, j - i - 1))) return false;
                    i = j;
                }
                else if (c == '#')
                {
                    int j = i + 1;
                    while (j < sel.Length && sel[j] != '.' && sel[j] != '#' && sel[j] != '[') j++;
                    if (n.GetAttributeValue("id", "") != sel.Substring(i + 1, j - i - 1)) return false;
                    i = j;
                }
                else if (c == '[')
                {
                    int j = sel.IndexOf(']', i);
                    if (j < 0) return false;
                    var body = sel.Substring(i + 1, j - i - 1).Trim();
                    string name = body, op = null, val = null;
                    foreach (var o in new[] { "^=", "$=", "*=", "~=", "|=", "!=", "=" })
                    {
                        int k = body.IndexOf(o, StringComparison.Ordinal);
                        if (k > 0) { name = body.Substring(0, k).Trim(); op = o; val = body.Substring(k + o.Length).Trim().Trim('\'', '"'); break; }
                    }
                    var actual = n.GetAttributeValue(name, null);
                    if (actual == null) return op == "!=";
                    if (op == null) { }
                    else if (op == "=") { if (actual != val) return false; }
                    else if (op == "^=") { if (!actual.StartsWith(val, StringComparison.Ordinal)) return false; }
                    else if (op == "$=") { if (!actual.EndsWith(val, StringComparison.Ordinal)) return false; }
                    else if (op == "*=") { if (actual.IndexOf(val, StringComparison.Ordinal) < 0) return false; }
                    else if (op == "~=") { if (!(" " + actual + " ").Contains(" " + val + " ")) return false; }
                    else if (op == "|=") { if (!(actual == val || actual.StartsWith(val + "-", StringComparison.Ordinal))) return false; }
                    else if (op == "!=") { if (actual == val) return false; }
                    i = j + 1;
                }
                else i++;
            }
            return true;
        }

        /// <summary>选择器是否从文档根写起（html / body / :root）。</summary>
        private static bool RootAnchored(string sel)
        {
            var s = sel.TrimStart();
            return s.StartsWith("html", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("body", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(":root", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDescendantOf(HtmlNode n, HtmlNode ancestor)
        {
            var p = n.ParentNode;
            while (p != null) { if (p == ancestor) return true; p = p.ParentNode; }
            return false;
        }

        private string EvalCssRaw(HtmlNode el, string rule)
        {
            var (_, segs0) = SplitRule(rule, "@");
            var segs = segs0.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (segs.Count == 0) return "";

            var cur = EvalCssNodes(el, segs[0]);
            string text = null;
            for (int i = 1; i < segs.Count; i++)
            {
                var s = segs[i];
                // 尾链里的 js：`@css:#nr1@text@js:result.replace(...)` 
                if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("<js>", StringComparison.OrdinalIgnoreCase))
                {
                    var code = s.StartsWith("js:", StringComparison.OrdinalIgnoreCase) ? s.Substring(3) : s;
                    var basis = text ?? GetResultLast(cur, "text");
                    text = RunJs(code, basis)?.ToString() ?? "";
                    continue;
                }
                var prop = AsPropertyName(s);
                if (prop != null) { text = GetResultLast(cur, prop); continue; }
                // 还是选择器：继续下钻
                var more = new List<HtmlNode>();
                foreach (var e in cur) more.AddRange(SelectSingle(e, s));
                if (more.Count == 0) return text ?? "";
                cur = more;
                text = null;
            }
            return text ?? GetResultLast(cur, "text");
        }

        private string EvalXPathString(HtmlNode node, string rule)
        {
            var els = EvalXPathNodes(node, rule);
            return string.Join("\n", els.Select(e => CleanText(e.InnerText)).Where(s => s.Length > 0));
        }

        private List<HtmlNode> EvalXPathNodes(HtmlNode node, string rule)
        {
            var outp = new List<HtmlNode>();
            try
            {
                var ns = node.SelectNodes(rule);
                if (ns != null) foreach (var n in ns) outp.Add(n);
            }
            catch { }
            return outp;
        }

        private string EvalJsonString(JToken tok, string rule)
        {
            var (type, parts) = SplitRule(rule, "&&", "||", "%%");
            var results = new List<List<string>>();
            foreach (var p in parts)
            {
                var r = JsonSelect(tok, p.Trim());
                if (r.Count > 0)
                {
                    results.Add(r);
                    if (type == "||") break;
                }
            }
            if (results.Count == 0) return "";
            if (type == "%%")
            {
                var outp = new List<string>();
                int max = results.Max(x => x.Count);
                for (int i = 0; i < max; i++)
                    foreach (var r in results)
                        if (i < r.Count) outp.Add(r[i]);
                return string.Join("\n", outp);
            }
            var all = new List<string>();
            foreach (var r in results) all.AddRange(r);
            return string.Join("\n", all);
        }

        private List<string> JsonSelect(JToken tok, string path)
        {
            var outp = new List<string>();
            if (string.IsNullOrEmpty(path)) return outp;
            try
            {
                foreach (var t in tok.SelectTokens(path))
                {
                    if (t is JValue v) outp.Add(v.Type == JTokenType.Null ? "" : v.ToString());
                    else outp.Add(t.ToString());
                }
            }
            catch { }
            return outp;
        }

        private object EvalJsonList(JToken tok, string rule)
        {
            var (type, parts) = SplitRule(rule, "&&", "||", "%%");
            var groups = new List<List<JToken>>();
            foreach (var p in parts)
            {
                var l = new List<JToken>();
                try { foreach (var t in tok.SelectTokens(p.Trim())) l.Add(t); } catch { }
                if (l.Count > 0)
                {
                    groups.Add(l);
                    if (type == "||") break;
                }
            }
            var outp = new List<object>();
            if (type == "%%")
            {
                int max = groups.Count == 0 ? 0 : groups.Max(x => x.Count);
                for (int i = 0; i < max; i++)
                    foreach (var g in groups)
                        if (i < g.Count) outp.Add(g[i]);
            }
            else foreach (var g in groups) foreach (var t in g) outp.Add(t);
            return outp;
        }

        private string EvalRegex(string text, string rule)
        {
            var (_, parts) = SplitRule(rule.TrimStart(':'), "&&");
            string cur = text ?? "";
            foreach (var p in parts)
            {
                try
                {
                    var m = Regex.Match(cur, p.Trim(), RegexOptions.Singleline);
                    if (!m.Success) return "";
                    cur = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                }
                catch { return ""; }
            }
            return cur;
        }

        // ============================================================
        // 规则切分（尊重引号 / [] / () 的平衡）
        // ============================================================
        private static (string type, List<string> parts) SplitRule(string s, params string[] seps)
        {
            var parts = new List<string>();
            string found = "";
            int depthB = 0, depthP = 0;
            bool inS = false, inD = false;
            int last = 0;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' && !inD) { inS = !inS; continue; }
                if (c == '"' && !inS) { inD = !inD; continue; }
                if (inS || inD) continue;
                if (c == '\\') { i++; continue; }
                if (c == '[') { depthB++; continue; }
                if (c == ']') { if (depthB > 0) depthB--; continue; }
                if (c == '(') { depthP++; continue; }
                if (c == ')') { if (depthP > 0) depthP--; continue; }
                if (depthB > 0 || depthP > 0) continue;

                string hit = null;
                foreach (var sep in seps)
                    if (i + sep.Length <= s.Length && string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0) { hit = sep; break; }
                if (hit == null) continue;
                if (found.Length == 0) found = hit;
                if (found != hit) continue;
                parts.Add(s.Substring(last, i - last));
                i += hit.Length - 1;
                last = i + 1;
            }
            parts.Add(s.Substring(last));
            return (found, parts);
        }

        /** 行内 {{表达式}}：按当前节点求值后替换 */
        private string EvalInlineExp(string rule, HtmlNode node)
        {
            if (rule.IndexOf("{{", StringComparison.Ordinal) < 0) return rule;
            return ExpPattern.Replace(rule, m =>
            {
                var r = RunJs(m.Groups[1].Value, node?.InnerText ?? "");
                return r == null ? "" : r.ToString();
            });
        }

        // ============================================================
        // JS
        // ============================================================
        private object ParseLoose(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? "";
            var t = s.TrimStart();
            if (t.StartsWith("{") || t.StartsWith("["))
            {
                try { return JToken.Parse(s); } catch { }
            }
            return ParseHtml(s);
        }

        private static HtmlNode ParseHtml(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html ?? "");
            return doc.DocumentNode;
        }

        /// <summary>
        /// JS 规则的 result / src 绑定：legado 里 Element 转字符串就是 outerHTML，
        /// 根文档则用原始响应文本（书源里大量 `String(result).match(/href="…"/)` 依赖这一点）。
        /// </summary>
        private string JsInput(object cur)
        {
            if (cur is HtmlNode hn)
            {
                if (hn.NodeType == HtmlNodeType.Document)
                    return string.IsNullOrEmpty(_rawText) ? hn.OuterHtml : _rawText;
                return hn.OuterHtml;
            }
            return TextOf(cur);
        }

        private string CurrentText(object content) => TextOf(content);


        /// <summary>
        /// jsoup Element 的近似物：书源 JS 里大量出现 `result[i].attr("href")`、`.text()`、`.select(...)`，
        /// 直接把 HtmlNode 交给 Jint 会报 “Property 'attr' of object is not a function”。
        /// </summary>
        public sealed class Element
        {
            public readonly HtmlNode Node;
            private readonly AnalyzeRule _owner;
            public Element(HtmlNode n, AnalyzeRule owner = null) { Node = n; _owner = owner; }

            public string attr(string name) { return Node.GetAttributeValue(name ?? "", ""); }
            public string attr(string name, string def) { return Node.GetAttributeValue(name ?? "", def ?? ""); }
            public bool hasAttr(string name) { return Node.GetAttributeValue(name ?? "", null) != null; }
            public string text() { return CleanText(Node.InnerText); }
            public string ownText()
            {
                var sb = new StringBuilder();
                foreach (var c in Node.ChildNodes)
                    if (c.NodeType == HtmlNodeType.Text) sb.Append(c.InnerText);
                return CleanText(sb.ToString());
            }
            public string html() { return Node.InnerHtml; }
            public string outerHtml() { return Node.OuterHtml; }
            public string tagName() { return Node.Name; }
            public Element parent() { return Node.ParentNode == null || Node.ParentNode.NodeType == HtmlNodeType.Document ? null : new Element(Node.ParentNode, _owner); }
            public ElementList children()
            {
                var a = new ElementList(_owner);
                foreach (var c in Node.ChildNodes)
                    if (c.NodeType == HtmlNodeType.Element) a.Add(new Element(c, _owner));
                return a;
            }
            public ElementList select(string rule)
            {
                var a = new ElementList(_owner);
                if (_owner == null) return a;
                try
                {
                    var sub = new AnalyzeRule(_owner._source, _owner._baseUrl).SetNode(Node);
                    foreach (var o in sub.GetList(rule)) if (o is HtmlNode n) a.Add(new Element(n, _owner));
                }
                catch { }
                return a;
            }
            public ElementList toList() { var l = new ElementList(_owner); l.Add(this); return l; }
            public override string ToString() { return Node.OuterHtml; }
        }

        /// <summary>jsoup Elements 的近似物：size()/get()/subList()/text()/length，元素可继续 .attr()。</summary>
        public sealed class ElementList : List<Element>
        {
            private readonly AnalyzeRule _owner;
            public ElementList(AnalyzeRule owner = null) { _owner = owner; }
            public ElementList(IEnumerable<Element> src, AnalyzeRule owner = null) : base(src) { _owner = owner; }

            public int length { get { return Count; } }
            public int size() { return Count; }
            public new Element get(int i) { return At(i); }

            private Element At(int i)
            {
                if (i < 0) i += Count;
                return i >= 0 && i < Count ? this[i] : null;
            }

            public ElementList subList(int a, int b)
            {
                int n = Count;
                a = a < 0 ? Math.Max(0, n + a) : Math.Min(a, n);
                b = b < 0 ? n + b : Math.Min(b, n);
                var r = new ElementList(_owner);
                for (int i = a; i < b && i < n; i++) r.Add(this[i]);
                return r;
            }

            public new Element first() { return Count > 0 ? this[0] : null; }
            public new Element last() { return Count > 0 ? this[Count - 1] : null; }

            public string text() { return string.Join("\n", this.Select(e => e.text()).Where(s => s.Length > 0)); }
            public string html() { return string.Join("\n", this.Select(e => e.html())); }
            public string toString() { return text(); }
            public IEnumerable<Element> iter() { return this; }
            public override string ToString() { return text(); }

            public List<HtmlNode> Nodes() { return this.Select(e => e.Node).ToList(); }
        }

        public ElementList JsElements(string rule)
        {
            var list = new ElementList(this);
            try
            {
                foreach (var o in GetList(rule)) if (o is HtmlNode n) list.Add(new Element(n, this));
            }
            catch { }
            return list;
        }

        public Element JsElement(string rule)
        {
            var l = JsElements(rule);
            return l.Count > 0 ? l[0] : null;
        }

        /// <summary>供 @js 规则用的 java.getStringList：返回字符串数组。</summary>
        public List<string> JsStringList(string rule)
        {
            try { return GetStringList(rule); } catch { return new List<string>(); }
        }

        private static string TextOf(object o)
        {
            if (o is HtmlNode hn) return hn.InnerText;
            if (o is JToken jt) return jt.ToString();
            return o?.ToString() ?? "";
        }

        /// <summary>把上一步结果里的节点挑出来（供 JS 侧 result 绑定成 jsoup Elements）。</summary>
        private static List<HtmlNode> NodesOf(object cur)
        {
            var list = new List<HtmlNode>();
            if (cur is List<HtmlNode> lh) list.AddRange(lh);
            else if (cur is List<object> lo) { foreach (var o in lo) if (o is HtmlNode n) list.Add(n); }
            else if (cur is HtmlNode single) list.Add(single);
            return list.Count > 0 ? list : null;
        }

        private object RunJs(string code, string result) { return RunJs(code, result, null); }

        private object RunJs(string code, string result, List<HtmlNode> nodes)
        {
            try
            {
                string js = code ?? "";
                string lib = _source?.JsLib;
                if (!string.IsNullOrWhiteSpace(lib)) js = lib + "\n" + js;

                var engine = new Engine();
                // result 绑定：文档 → 原始 HTML 字符串（书源大量用 result.match）；
                // 单个元素 → jsoup Element（可 .attr/.text）；多节点 → Elements（可 .length/[i].attr）
                object bind;
                if (nodes == null || nodes.Count == 0
                    || (nodes.Count == 1 && nodes[0].NodeType == HtmlNodeType.Document))
                    bind = result ?? "";
                else if (nodes.Count == 1) bind = new Element(nodes[0], this);
                else bind = new ElementList(nodes.Select(n => new Element(n, this)), this);
                engine.SetValue("result", bind);
                engine.SetValue("baseUrl", _baseUrl ?? "");
                engine.SetValue("title", result ?? "");
                engine.SetValue("src", result ?? "");
                engine.SetValue("java", new JsJavaShim(this));
                engine.SetValue("cookie", new JsCookieShim());
                engine.SetValue("cache", new JsCookieShim());
                engine.SetValue("source", _source);
                var v = engine.Evaluate(js);
                if (v == null || v.IsNull() || v.IsUndefined()) return "";
                if (v.IsString()) return v.AsString();
                if (v.IsNumber()) return v.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (v.IsBoolean()) return v.AsBoolean() ? "true" : "false";
                // @js 规则可以直接返回 jsoup Elements（如 java.getElements(...).subList(...) / 元素数组）
                try
                {
                    var o = v.ToObject();
                    if (o is ElementList) return o;
                    if (o is Element) { var one = new ElementList(this); one.Add((Element)o); return one; }
                    if (o is List<HtmlNode>) return o;
                    if (o is System.Collections.IEnumerable en && !(o is string))
                    {
                        var l = new ElementList(this);
                        foreach (var it in en)
                        {
                            if (it is Element e) l.Add(e);
                            else if (it is HtmlNode hn) l.Add(new Element(hn, this));
                        }
                        if (l.Count > 0) return l;
                    }
                }
                catch { }
                return v.ToString();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "阅读书源 JS 求值失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>legado 的 java.* 垫片：覆盖书源里最常用的那些。</summary>
        public object MakeJava() { return new JsJavaShim(this); }

        /// <summary>legado 的 cookie.* 垫片。</summary>
        public object MakeCookie() { return new JsCookieShim(); }

        private sealed class JsJavaShim
        {
            private readonly AnalyzeRule _owner;
            public JsJavaShim(AnalyzeRule owner) { _owner = owner; }

            public string ajax(string url) { return Ajax(url, null); }
            // legado 的 java.ajax(url, options)：options 是 JSON（method/body/headers/charset），
            // 老写法第二个参数直接是 POST 体。
            public string ajax(string url, string options) { return Ajax(url, options); }
            public string connect(string url) { return Ajax(url, null); }
            public string connect(string url, string options) { return Ajax(url, options); }

            /// <summary>
            /// legado 允许把选项直接拼在 url 结尾：'http://host/,{"method":"POST","body":"…"}'，
            /// 书源里大量使用这种单参写法，必须先拆开再请求。
            /// </summary>
            private string Ajax(string url, string options)
            {
                var o = TryOptions(options);
                if (o == null && !string.IsNullOrEmpty(url))
                {
                    int cut = url.LastIndexOf(",{", StringComparison.Ordinal);
                    if (cut > 0)
                    {
                        var tail = url.Substring(cut + 1);
                        var jo = TryOptions(tail);
                        if (jo != null) { url = url.Substring(0, cut); o = jo; }
                    }
                }
                if (o == null)
                    return string.IsNullOrEmpty(options) ? Http.Get(url) : Http.Post(url, options);
                return Http.Request(url,
                    (string)o["method"] ?? "GET",
                    (string)o["body"],
                    o["headers"] != null ? o["headers"].ToObject<System.Collections.Generic.Dictionary<string, string>>() : null,
                    (string)o["charset"]);
            }

            private static Newtonsoft.Json.Linq.JObject TryOptions(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                var t = s.Trim();
                if (!t.StartsWith("{", StringComparison.Ordinal)) return null;
                return ReaderEngine.ParseOptions(t);
            }

            public string base64Decode(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); } catch { return ""; } }
            public string base64Encode(string s) { try { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); } catch { return ""; } }
            public string md5Encode(string s) { return Md5(s); }
            public string md5Encode16(string s) { var h = Md5(s); return h.Length >= 16 ? h.Substring(8, 16) : h; }
            public string hexDecodeToString(string s) { try { return Encoding.UTF8.GetString(HexToBytes(s)); } catch { return ""; } }
            public string hexEncodeToString(string s) { try { return BitConverter.ToString(Encoding.UTF8.GetBytes(s ?? "")).Replace("-", "").ToLowerInvariant(); } catch { return ""; } }
            public string encodeURI(string s) { try { return Uri.EscapeUriString(s ?? ""); } catch { return s ?? ""; } }
            public string encodeURI(string s, string charset) { return EncodeWith(s, charset, false); }
            public string encodeURIComponent(string s) { try { return Uri.EscapeDataString(s ?? ""); } catch { return s ?? ""; } }
            public string encodeURIComponent(string s, string charset) { return EncodeWith(s, charset, true); }
            public string encodeURI(string s, object charset) { return EncodeWith(s, charset?.ToString(), false); }
            public string encodeURIComponent(string s, object charset) { return EncodeWith(s, charset?.ToString(), true); }

            private static string EncodeWith(string s, string charset, bool component)
            {
                s = s ?? "";
                var cs = (charset ?? "").Trim().ToLowerInvariant();
                if (cs.Length == 0 || cs.StartsWith("utf")) return component ? Uri.EscapeDataString(s) : Uri.EscapeUriString(s);
                try
                {
                    var bytes = EncodingEx.Encode(s, cs);
                    var sb = new StringBuilder();
                    foreach (var b in bytes)
                    {
                        char c = (char)b;
                        bool keep = component
                            ? (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                              || c == '-' || c == '_' || c == '.' || c == '!' || c == '~' || c == '*' || c == '\'' || c == '(' || c == ')'
                            : (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                              || "-_.!~*'();/?:@&=+$,#".IndexOf(c) >= 0;
                        if (keep) sb.Append(c);
                        else sb.Append('%').Append(((int)b).ToString("X2"));
                    }
                    return sb.ToString();
                }
                catch { return Uri.EscapeDataString(s); }
            }

            public string timeFormat(long ts) { return FormatTime(ts); }
            public string timeFormatUTC(long ts) { return FormatTime(ts); }

            public void log(string msg) { Android.Util.Log.Info("MchatJS", msg ?? ""); }
            public void toast(string msg) { }
            public string put(string k, string v) { _owner.Put(k, v); return v; }
            public string get(string k) { return _owner.Get(k); }
            public string getString(string rule) { return _owner.GetString(rule); }
            public string getString(string rule, bool isUrl) { return _owner.GetString(rule); }
            public object getElements(string rule) { return _owner.JsElements(rule); }
            public object getElement(string rule) { return _owner.JsElement(rule); }
            public object getElements(string rule, object content) { return _owner.JsElements(rule); }
            public object getStringList(string rule) { return _owner.JsStringList(rule); }
            public void setContent(string s) { }
            public string htmlFormat(string s) { return AnalyzeRule.CleanText(s); }

            private static string FormatTime(long ts)
            {
                try
                {
                    if (ts > 1000000000000L) { }                     // 毫秒
                    else ts *= 1000L;                                  // 秒
                    return DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm");
                }
                catch { return ""; }
            }

            private static string Md5(string s)
            {
                try
                {
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                    {
                        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                        var sb = new StringBuilder();
                        foreach (var b in bytes) sb.Append(b.ToString("x2"));
                        return sb.ToString();
                    }
                }
                catch { return ""; }
            }

            private static byte[] HexToBytes(string hex)
            {
                hex = (hex ?? "").Replace(" ", "");
                var b = new byte[hex.Length / 2];
                for (int i = 0; i < b.Length; i++)
                    b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return b;
            }
        }

        private sealed class JsCookieShim
        {
            public string getCookie(string url) => ReaderCookies.Get(url);
            public void putCookie(string url, string cookie) => ReaderCookies.Put(url, cookie);
            public string getKey(string k, string def) => def;
            public void put(string k, string v) { }
        }

        /// <summary>同步 HTTP（供 java.ajax 使用）。</summary>
        internal static class Http
        {
            private static readonly System.Net.Http.HttpClient _c = Create();

            private static System.Net.Http.HttpClient Create()
            {
                var h = new System.Net.Http.HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                    UseCookies = true,
                    CookieContainer = ReaderCookies.Jar,
                    UseProxy = false          // 书源直连，不受系统 HTTP_PROXY 影响（legado 同样不走代理）
                };
                var c = new System.Net.Http.HttpClient(h) { Timeout = TimeSpan.FromSeconds(25) };
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110 Mobile Safari/537.36");
                return c;
            }

            public static string Get(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return "";
                try
                {
                    var bytes = Task.Run(() => _c.GetByteArrayAsync(url)).GetAwaiter().GetResult();
                    return EncodingEx.Detect(bytes, null);
                }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "java.ajax 失败: " + ex.Message); return ""; }
            }

            public static string Post(string url, string body)
            {
                return Request(url, "POST", body, null, null);
            }

            public static string Request(string url, string method, string body,
                Dictionary<string, string> headers, string charset, bool allowUaRetry = true)
            {
                if (string.IsNullOrWhiteSpace(url)) return "";
                try
                {
                    using (var req = new System.Net.Http.HttpRequestMessage(
                        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                            ? System.Net.Http.HttpMethod.Post : System.Net.Http.HttpMethod.Get, url))
                    {
                        if (headers != null)
                            foreach (var kv in headers)
                            {
                                if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                                {
                                    req.Headers.Remove("User-Agent");
                                    req.Headers.TryAddWithoutValidation("User-Agent", kv.Value);
                                    continue;
                                }
                                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                            }
                        if (body != null)
                            req.Content = new System.Net.Http.StringContent(body, Encoding.UTF8,
                                "application/x-www-form-urlencoded");
                        var bytes = Task.Run(() => _c.SendAsync(req)
                            .ContinueWith(t => t.Result.Content.ReadAsByteArrayAsync())
                            .Unwrap()).GetAwaiter().GetResult();
                        var text = EncodingEx.Detect(bytes, charset);
                        // 部分站点偶发返回「403 + JS 跳转桩」，补一个 Referer 再试一次
                        if (allowUaRetry && IsBotStub(text))
                        {
                            var h2 = headers == null
                                ? new Dictionary<string, string>()
                                : headers.Where(kv => !kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                                         .ToDictionary(kv => kv.Key, kv => kv.Value);
                            try
                            {
                                var u = new Uri(url);
                                if (!h2.ContainsKey("Referer")) h2["Referer"] = u.Scheme + "://" + u.Host + "/";
                            }
                            catch { }
                            var again = Request(url, method, body, h2, charset, false);
                            if (again.Length > text.Length) return again;
                        }
                        return text;
                    }
                }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "java.ajax 失败: " + ex.Message); return ""; }
            }

            /// <summary>400 级的反爬桩：极短且带 window.location 跳转。</summary>
            private static bool IsBotStub(string text)
            {
                if (string.IsNullOrEmpty(text) || text.Length > 800) return false;
                return text.IndexOf("window.location", StringComparison.OrdinalIgnoreCase) >= 0
                       || text.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        // ============================================================
        // 工具
        // ============================================================
        public static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = System.Net.WebUtility.HtmlDecode(s);
            s = s.Replace('\u00a0', ' ');
            return Regex.Replace(s, @"[ \t\r\n\f\v]+", " ").Trim();
        }

        /// <summary>保留换行的清洗（正文用）。</summary>
        public static string CleanBlock(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = System.Net.WebUtility.HtmlDecode(s);
            s = s.Replace('\u00a0', ' ');
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = s.Split('\n').Select(x => Regex.Replace(x, @"[ \t\u3000]+", " ").Trim());
            return string.Join("\n", lines).Trim();
        }

        /// <summary>节点自身文本（不含子节点文本）。</summary>
        private static string OwnText(HtmlNode node)
        {
            var sb = new StringBuilder();
            foreach (var c in node.ChildNodes)
                if (c.NodeType == HtmlNodeType.Text) sb.Append(c.InnerText);
            return CleanText(sb.ToString());
        }

        /// <summary>去掉 legado URL 规则尾部的 <c>,{"webView":true}</c> 之类选项。</summary>
        public static string StripUrlOptions(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int i = url.LastIndexOf(",{", StringComparison.Ordinal);
            if (i <= 0) return url;
            var tail = url.Substring(i + 1).Trim();
            if (tail.Length < 2 || !tail.StartsWith("{", StringComparison.Ordinal)
                || !tail.EndsWith("}", StringComparison.Ordinal)) return url;
            try { Newtonsoft.Json.Linq.JObject.Parse(tail); return url.Substring(0, i).Trim(); }
            catch { return url; }
        }

        /// <summary>把相对地址解析为绝对地址。</summary>
        public string AbsoluteUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            url = url.Trim();
            // 规则可能是多行结果（如 @href 命中多个节点），URL 只取第一行
            int nl = url.IndexOf('\n');
            if (nl > 0) url = url.Substring(0, nl).Trim();
            url = StripUrlOptions(url.Trim());
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("//")) return "https:" + url;
            try
            {
                if (!string.IsNullOrEmpty(_baseUrl))
                    return new Uri(new Uri(_baseUrl), url).AbsoluteUri;
            }
            catch { }
            try
            {
                var b = _source?.BookSourceUrl;
                if (!string.IsNullOrEmpty(b))
                    return new Uri(new Uri(b.EndsWith("/") ? b : b + "/"), url).AbsoluteUri;
            }
            catch { }
            return url;
        }
    }
}

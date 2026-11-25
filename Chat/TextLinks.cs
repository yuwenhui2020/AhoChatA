using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Android.Graphics;
using Android.Text;
using Android.Text.Method;
using Android.Text.Style;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 把消息正文里的链接与「裸 IP」变成可点击文本。
    ///
    /// 覆盖两种写法：
    ///   · 完整链接：http://…、https://…、www.…（补 http://）；
    ///   · 裸 IP：192.168.1.10、192.168.1.10:8080、10.0.0.2/path
    ///     不带端口时按 80 端口访问（http 默认端口），带端口就用给定端口。
    /// 句末标点（，。！？、引号右括号等）会从链接尾部剔除，避免把标点带进地址。
    /// </summary>
    public static class TextLinks
    {
        // 链接字符集：排除空白、中日韩标点区（\u3000-\u303F、\uFF00-\uFFEF，含「，。！？（）」等）、
        // 以及英文引号/括号，避免把句末中文标点吃进地址里。
        private const string TailBody = @"[^\s\u3000-\u303F\uFF00-\uFFEF<>""'“”‘’()\[\]{}]";

        // 完整链接
        private static readonly Regex RxUrl;

        // 裸 IPv4（前面不能紧挨数字或点，避免从 1.2.3.4.5 里截半截）
        private static readonly Regex RxIp;

        private static readonly bool RxOk;

        static TextLinks()
        {
            try
            {
                RxUrl = new Regex(@"(?i)(?:https?://|www\.)" + TailBody + "+", RegexOptions.Compiled);
                RxIp = new Regex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?:/" + TailBody + @"+)?(?![\d.])",
                                 RegexOptions.Compiled);
                RxOk = true;
            }
            catch
            {
                // 正则出问题时退化为「不识别链接」，绝不让消息渲染因此崩溃
                RxOk = false;
            }
        }

        private static readonly char[] Trailing =
        {
            '.', ',', ';', ':', '!', '?', '。', '，', '、', '；', '：', '！', '？', '…',
            ')', ']', '}', '>', '）', '】', '》', '」', '』', '"', '\'', '”', '’'
        };

        private sealed class Hit
        {
            public int Start;
            public int End;
            public string Url;
        }

        /// <summary>正文里第一个可跳转地址（没有就返回 null）。</summary>
        public static string FirstUrl(string text)
        {
            var hits = Collect(text ?? "");
            return hits.Count > 0 ? hits[0].Url : null;
        }

        /// <summary>
        /// 给 TextView 装可点击链接。返回是否真的找到了链接（没找到时不改动控件）。
        /// 找到时会把控件 MovementMethod 设为 LinkMovementMethod —— 长按仍然走控件自己的
        /// 长按监听（消息菜单），点链接则由 span 自己处理，两者互不影响。
        /// </summary>
        public static bool MakeClickable(TextView tv, string text, int linkColor, Action<string> open)
        {
            if (tv == null || string.IsNullOrEmpty(text) || open == null) return false;
            var hits = Collect(text);
            if (hits.Count == 0) return false;

            var ss = new SpannableString(text);
            foreach (var h in hits)
            {
                try { ss.SetSpan(new LinkSpan(linkColor, h.Url, open), h.Start, h.End, SpanTypes.ExclusiveExclusive); }
                catch { }
            }
            try
            {
                tv.SetText(ss, TextView.BufferType.Spannable);
                tv.MovementMethod = LinkMovementMethod.Instance;
                tv.SetTextIsSelectable(false);
            }
            catch { return false; }
            return true;
        }

        private static List<Hit> Collect(string text)
        {
            var raw = new List<Hit>();
            if (!RxOk || string.IsNullOrEmpty(text)) return raw;

            foreach (Match m in RxUrl.Matches(text))
                AddHit(text, raw, m.Index, m.Length, true);
            foreach (Match m in RxIp.Matches(text))
                AddHit(text, raw, m.Index, m.Length, false);

            raw.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : b.End.CompareTo(a.End);   // 同起点优先取更长的
            });

            // 去掉互相重叠的匹配（例如 http://1.2.3.4 里的 IP 部分不再单独成链）
            var merged = new List<Hit>();
            int lastEnd = -1;
            foreach (var h in raw)
            {
                if (h.Start < lastEnd) continue;
                merged.Add(h);
                lastEnd = h.End;
            }
            return merged;
        }

        private static void AddHit(string text, List<Hit> hits, int start, int len, bool isUrl)
        {
            int end = start + len;
            while (end > start && Array.IndexOf(Trailing, text[end - 1]) >= 0) end--;
            if (end <= start) return;

            string raw = text.Substring(start, end - start);
            string url;
            if (isUrl)
            {
                url = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + raw : raw;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            }
            else
            {
                if (!IsIpv4(raw)) return;
                url = "http://" + raw;    // 裸 IP → 80 端口；带端口沿用原端口
            }
            hits.Add(new Hit { Start = start, End = end, Url = url });
        }

        /// <summary>校验 IPv4 四段的数值范围，避免把 999.1.1.1 这类版本号当地址。</summary>
        private static bool IsIpv4(string s)
        {
            try
            {
                int cut = s.Length;
                int slash = s.IndexOf('/');
                if (slash >= 0) cut = slash;
                int colon = s.IndexOf(':');
                if (colon >= 0 && colon < cut) cut = colon;
                string ip = s.Substring(0, cut);

                var parts = ip.Split('.');
                if (parts.Length != 4) return false;
                foreach (var p in parts)
                {
                    if (p.Length == 0 || p.Length > 3) return false;
                    int v;
                    if (!int.TryParse(p, out v)) return false;
                    if (v < 0 || v > 255) return false;
                }
                int port = 0;
                if (colon >= 0)
                {
                    string ps = s.Substring(colon + 1, (slash > colon ? slash : s.Length) - colon - 1);
                    if (!int.TryParse(ps, out port) || port < 1 || port > 65535) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private sealed class LinkSpan : ClickableSpan
        {
            private readonly int _color;
            private readonly string _url;
            private readonly Action<string> _open;

            public LinkSpan(int color, string url, Action<string> open)
            {
                _color = color;
                _url = url;
                _open = open;
            }

            public override void UpdateDrawState(TextPaint ds)
            {
                base.UpdateDrawState(ds);
                try
                {
                    ds.Color = new Color(_color);
                    ds.UnderlineText = true;
                }
                catch { }
            }

            public override void OnClick(View widget)
            {
                try { _open(_url); } catch { }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// 旧版 PPT（.ppt / .pps，OLE2 复合文档）的轻量文本提取器。
    ///
    /// gander 的前端只有 pptx（OOXML）解析器，对二进制 .ppt 无能为力；本类不追求还原排版，
    /// 目标是把幻灯片里的文字读出来——PPT 的文本记录（TextCharsAtom / TextBytesAtom）
    /// 在 PowerPoint Document 流里以 UTF-16LE（或 ANSI）连续存放，
    /// 直接扫描二进制里的可打印 Unicode 序列即可覆盖绝大多数真实文件。
    ///
    /// 生成的是纯 HTML 片段，由 reader 的 text.html / 一个简单 WebView 页面显示。
    /// </summary>
    public static class OldPptReader
    {
        /// <summary>尝试从 .ppt 提取文本。成功返回 true 并给出 HTML 全文。</summary>
        public static bool TryExtract(string path, out string html)
        {
            html = "";
            try
            {
                var data = File.ReadAllBytes(path);
                if (data.Length < 8) return false;

                var texts = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1) UTF-16LE 文本段（PPT 文本主流存储）
                ScanUtf16(data, texts, seen);
                // 2) 单字节 ANSI 文本段（TextBytesAtom，兼容纯英文老文件）
                ScanAnsi(data, texts, seen);

                if (texts.Count == 0) return false;

                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset='utf-8'>")
                  .Append("<meta name='viewport' content='width=device-width,initial-scale=1'>")
                  .Append(L10n.T("<title>PPT 文本</title><style>"))
                  .Append("body{margin:0;padding:20px;background:#0f1115;color:#e6e8eb;font:16px/1.7 -apple-system,'PingFang SC','Microsoft YaHei',sans-serif}")
                  .Append("h1{font-size:18px;color:#6ea8fe;margin:0 0 14px}")
                  .Append(".t{padding:10px 14px;margin:0 0 8px;background:#1b1f26;border-radius:10px}")
                  .Append(".hint{color:#8b929c;font-size:12px;margin:0 0 16px}")
                  .Append("</style></head><body>")
                  .Append("<h1>").Append(H(Path.GetFileName(path)))
                  .Append(L10n.T("（旧版 PPT · 文本预览）</h1>"))
                  .Append(L10n.T("<p class=hint>此格式为早期二进制 PPT，已提取其中可识别的文字；图片与排版无法还原，如需完整效果请转换为 pptx。</p>"));
                foreach (var t in texts)
                    sb.Append("<div class=t>").Append(H(t)).Append("</div>");
                sb.Append("</body></html>");
                html = sb.ToString();
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "OldPptReader 提取失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>扫描 UTF-16LE 可打印序列（跳过控制字符，允许常见标点与 CJK）。</summary>
        private static void ScanUtf16(byte[] data, List<string> outList, HashSet<string> seen)
        {
            var buf = new StringBuilder();
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                int ch = data[i] | (data[i + 1] << 8);
                if (IsPrintable(ch))
                {
                    buf.Append((char)ch);
                }
                else
                {
                    Flush(buf, outList, seen, 3);
                    buf.Clear();
                }
            }
            Flush(buf, outList, seen, 3);
        }

        /// <summary>扫描单字节可打印序列（英文老文件）。</summary>
        private static void ScanAnsi(byte[] data, List<string> outList, HashSet<string> seen)
        {
            var buf = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                int ch = data[i];
                if (ch >= 0x20 && ch < 0x7F) buf.Append((char)ch);
                else { Flush(buf, outList, seen, 8); buf.Clear(); }
            }
            Flush(buf, outList, seen, 8);
        }

        private static void Flush(StringBuilder buf, List<string> outList, HashSet<string> seen, int minLen)
        {
            if (buf.Length < minLen) return;
            string s = buf.ToString().Trim();
            if (s.Length < minLen) return;
            // 过滤明显不是正文的噪声（纯符号/纯数字 ID）
            int letters = 0;
            foreach (char c in s) if (char.IsLetter(c)) letters++;
            if (letters < Math.Min(2, s.Length)) return;
            if (seen.Add(s)) outList.Add(s);
        }

        private static bool IsPrintable(int ch)
        {
            if (ch < 0x20) return false;
            if (ch == 0x7F || ch == 0xFFFD) return false;
            // 常见控制/保留区
            if (ch >= 0x80 && ch <= 0x9F) return false;
            // CJK / 全角 / 常用标点
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
            if (ch >= 0x3000 && ch <= 0x303F) return true;
            if (ch >= 0xFF00 && ch <= 0xFFEF) return true;
            // 拉丁/数字/常规标点
            if (ch >= 0x20 && ch <= 0x7E) return true;
            if (ch >= 0x00A0 && ch <= 0x024F) return true;
            return false;
        }

        private static string H(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}

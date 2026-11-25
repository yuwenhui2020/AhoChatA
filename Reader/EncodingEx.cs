// ============================================================
// 阅读模块 · 文本编码（HTTP 响应 / 文件字节 → 字符串）
// 从 ReaderEngine 拆出，便于离线自检工程单独引用。
//
// 两个运行环境差异很大：
//   · 桌面 .NET：GetEncoding("gbk") 需要 CodePages 提供程序（反射注册，见 EnsureProvider）
//   · Android：Mono 精简 BCL 没链接 I18N.CJK，GetEncoding("gbk") 抛 NotSupportedException
//     → 用 java.lang.String(byte[], charset) / getBytes(charset) 兜底（#if __ANDROID__）。
// ============================================================
using System;
using System.Reflection;

namespace AhoChatA.Reader
{
    internal static class EncodingEx
    {
        static bool _providerReady;
        internal static string Diag = "";
        private static bool _noted;

        static EncodingEx() { EnsureProvider(); }

        private static void Note(string msg)
        {
            if (_noted) return;
            _noted = true;
            try { Android.Util.Log.Warn("Mchat", "编码诊断: " + msg); } catch { }
        }

        /// <summary>
        /// 桌面 .NET 的 CodePages 提供程序。CodePagesEncodingProvider 只有静态 Instance
        /// （无公共无参构造），必须反射取 Instance，否则 MissingMethodException。
        /// </summary>
        private static void EnsureProvider()
        {
            if (_providerReady) return;
            _providerReady = true;
            try
            {
                var t = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages", false);
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = asm.GetType("System.Text.CodePagesEncodingProvider", false);
                        if (t != null) break;
                    }
                }
                if (t == null)
                {
                    try
                    {
                        var asm = Assembly.Load(new AssemblyName("System.Text.Encoding.CodePages"));
                        t = asm?.GetType("System.Text.CodePagesEncodingProvider", false);
                    }
                    catch { }
                }
                if (t == null) return;
                object inst = null;
                var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (prop != null) inst = prop.GetValue(null);
                if (inst == null)
                {
                    var fld = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (fld != null) inst = fld.GetValue(null);
                }
                if (inst == null) inst = Activator.CreateInstance(t);
                System.Text.Encoding.RegisterProvider((System.Text.EncodingProvider)inst);
            }
            catch (Exception ex) { Diag += " | ctor:" + ex.GetType().Name + ":" + ex.Message; }
        }

        /// <summary>按编码名取 .NET Encoding（取不到返回 null，调用方应改用 Decode/Encode）。</summary>
        public static System.Text.Encoding Get(string charset)
        {
            if (string.IsNullOrWhiteSpace(charset)) return null;
            EnsureProvider();
            try { return System.Text.Encoding.GetEncoding(TrimName(charset)); }
            catch (Exception ex) { Diag += " | get:" + ex.GetType().Name; return null; }
        }

        private static string TrimName(string charset)
        {
            var s = (charset ?? "").Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                s = s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        // ---------------- Java 兜底（Android） ----------------
#if __ANDROID__
        private static string JavaDecode(byte[] bytes, string charset)
        {
            try { return new Java.Lang.String(bytes, charset).ToString(); }
            catch (Exception ex) { Note("java decode " + charset + " : " + ex.GetType().Name); return null; }
        }

        private static byte[] JavaEncode(string text, string charset)
        {
            try { return new Java.Lang.String(text ?? "").GetBytes(charset); }
            catch (Exception ex) { Note("java encode " + charset + " : " + ex.GetType().Name); return null; }
        }
#else
        private static string JavaDecode(byte[] bytes, string charset) { return null; }
        private static byte[] JavaEncode(string text, string charset) { return null; }
#endif

        // ---------------- 对外 ----------------

        /// <summary>按指定编码解码；取不到该编码时退 Java（Android）→ UTF-8。</summary>
        public static string Decode(byte[] bytes, string charset)
        {
            if (bytes == null || bytes.Length == 0) return "";
            var name = TrimName(charset);
            var enc = Get(name);
            if (enc != null) { try { return enc.GetString(bytes); } catch { } }
            if (name.Length > 0)
            {
                var s = JavaDecode(bytes, name);
                if (s != null) return s;
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>按指定编码编码成字节（GBK 站点的 URL 关键字）；退 Java（Android）→ UTF-8。</summary>
        public static byte[] Encode(string text, string charset)
        {
            text = text ?? "";
            var name = TrimName(charset);
            var enc = Get(name);
            if (enc != null) { try { return enc.GetBytes(text); } catch { } }
            if (name.Length > 0)
            {
                var b = JavaEncode(text, name);
                if (b != null) return b;
            }
            return System.Text.Encoding.UTF8.GetBytes(text);
        }

        public static string Detect(byte[] bytes, string charset)
        {
            if (bytes == null || bytes.Length == 0) return "";
            if (!string.IsNullOrEmpty(charset)) return Decode(bytes, charset);

            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            // 合法 UTF-8 就用 UTF-8
            try
            {
                var s = System.Text.Encoding.UTF8.GetString(bytes);
                if (System.Text.Encoding.UTF8.GetByteCount(s) == bytes.Length) return s;
            }
            catch { }
            // 否则按 GBK 解（中文站点最常见的第二种）
            return Decode(bytes, "GBK");
        }
    }
}

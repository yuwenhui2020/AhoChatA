using System;
using System.IO;
using System.Text;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 自动接收任意局域网文件：把收到的字节按原始文件名另存到用户配置目录。
    /// 与 MediaShare 的四类媒体入库并行，覆盖 doc/zip/apk/任意扩展名。
    /// </summary>
    public static class AutoReceive
    {
        /// <summary>把一份收到的文件保存到自动接收目录。返回保存后的完整路径，失败返回空串。</summary>
        public static string SaveIncoming(string originalName, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try
            {
                var dir = AutoReceiveSettings.Dir;
                if (string.IsNullOrEmpty(dir)) dir = AutoReceiveSettings.DefaultDir;
                Directory.CreateDirectory(dir);

                var safe = SafeFileName(originalName);
                var target = Path.Combine(dir, safe);
                target = UniquePath(target);
                File.WriteAllBytes(target, bytes);
                Log.Info("Mchat", "自动接收已保存: " + target + " (" + bytes.Length + " B)");
                return target;
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "自动接收保存失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>去掉路径分隔符与文件系统非法字符，避免文件名穿越/写失败。</summary>
        private static string SafeFileName(string name)
        {
            var s = (name ?? "").Replace('\\', '/');
            var i = s.LastIndexOf('/');
            if (i >= 0) s = s.Substring(i + 1);
            s = s.Trim();
            if (s.Length == 0) s = "file";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c < 32 || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            var result = sb.ToString();
            if (result.Length > 180) result = result.Substring(result.Length - 180);
            return result;
        }

        /// <summary>同名文件不覆盖，追加 (1)(2)…。</summary>
        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var n = 1; n < 10000; n++)
            {
                var candidate = Path.Combine(dir, stem + " (" + n + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }
    }
}

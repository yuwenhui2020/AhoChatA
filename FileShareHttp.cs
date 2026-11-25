using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 局域网文件共享的 HTTP 通道（挂在统一端口的 /file 下）。
    ///
    /// 和 FTP 是同一份目录（LanShareService.ShareRoot），只是给「不想装 FTP 客户端」的人
    /// 一个浏览器入口：列表、点开、下载。FTP 因为协议不同无法合并端口，仍单独监听。
    /// </summary>
    public static class FileShareHttp
    {
        public static void ServeClient(TcpClient client, string subPath)
        {
            try
            {
                var ns = client.GetStream();
                var head = ReadHeader(ns);
                if (head == null)
                {
                    // 客户端还没发完请求就断开（浏览器取消 / 探测连接），正常情况，安静关闭
                    Close(client);
                    return;
                }

                string path = subPath ?? "/";
                string query = "";
                int qi = path.IndexOf('?');
                if (qi >= 0) { query = path.Substring(qi + 1); path = path.Substring(0, qi); }
                if (path == "") path = "/";

                // HEAD 只回头部（部分浏览器/下载器会先探一下）
                bool headOnly = head.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase);
                // 页头显示的地址用「访问者实际用的 Host」，网络变了也不用改配置
                string host = HostOf(head);

                // 共享根按需初始化：FTP 未启动时 ShareRoot 可能还是空
                string root = LanShareService.EnsureShareRoot();
                if (string.IsNullOrEmpty(root))
                {
                    SendText(client, 503, L10n.T("共享目录不可用"));
                    return;
                }
                try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); } catch { }
                if (!Directory.Exists(root))
                {
                    SendText(client, 503, L10n.T("共享目录不可用：") + root);
                    return;
                }

                // /file/raw?p=xxx  → 下载
                if (path.StartsWith("/raw", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = GetQuery(query, "p");
                    string full = SafeJoin(root, rel);
                    if (full == null || !File.Exists(full)) { SendText(client, 404, "文件不存在"); return; }
                    SendFile(client, full, headOnly);
                    return;
                }

                // /file/get/<相对路径> → 下载（便于直接分享链接）
                if (path.StartsWith("/get/", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = Uri.UnescapeDataString(path.Substring(5));
                    string full = SafeJoin(root, rel);
                    if (full == null || !File.Exists(full)) { SendText(client, 404, "文件不存在"); return; }
                    SendFile(client, full, headOnly);
                    return;
                }

                // 列表：/file 或 /file/<相对目录>
                // 链接里的「/」被 EscapeDataString 转成了 %2F，这里必须反解回来，
                // 否则点进子目录会 404（表现为「有时能看有时不能」）。
                string relDir = Uri.UnescapeDataString(path).Trim('/');
                string dir = SafeJoin(root, relDir);
                if (dir == null || !Directory.Exists(dir)) { SendText(client, 404, "目录不存在"); return; }

                SendHtml(client, ListPage(root, dir, relDir, host), headOnly);
            }
            catch (Exception ex)
            {
                Log.Warn("FileShare", "HTTP 文件浏览失败：" + ex.Message);
                try { SendText(client, 500, L10n.T("读取共享目录失败：") + ex.Message); } catch { }
                try { Close(client); } catch { }
            }
        }

        /* ===================== 列表页 ===================== */

        private static string ListPage(string root, string dir, string rel, string host)
        {
            var sb = new StringBuilder();
            string title = string.IsNullOrEmpty(rel) ? L10n.T("文件共享") : rel;
            sb.Append("<!doctype html><html><head><meta charset=utf-8>");
            sb.Append("<meta name=viewport content='width=device-width,initial-scale=1'>");
            sb.Append("<title>").Append(H(title)).Append("</title><style>");
            sb.Append("body{margin:0;background:#0f1115;color:#e6e8eb;font:15px/1.6 -apple-system,'PingFang SC',sans-serif}");
            sb.Append("header{padding:16px 18px 8px;border-bottom:1px solid #23272f}");
            sb.Append("h1{font-size:17px;margin:0}p{margin:4px 0 0;color:#8b929c;font-size:12px}");
            sb.Append("a{display:flex;justify-content:space-between;gap:12px;padding:13px 18px;color:inherit;");
            sb.Append("text-decoration:none;border-bottom:1px solid #1b1f26}");
            sb.Append("a:active{background:#181b21}.n{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}");
            sb.Append(".s{color:#8b929c;font-size:12px;flex:none}.d{color:#6ea8fe}");
            sb.Append(".row{display:flex;align-items:stretch;border-bottom:1px solid #1b1f26}");
            sb.Append(".ra{flex:1;min-width:0;padding:13px 18px}.rd{color:#6ea8fe;font-size:12px;margin-left:8px;flex:none}");
            sb.Append(".dl{display:flex;align-items:center;padding:0 16px;color:#8b929c;font-size:12px;border-left:1px solid #1b1f26}");
            sb.Append("</style></head><body><header><h1>").Append(H(title)).Append("</h1><p>")
              .Append(H(string.IsNullOrEmpty(host)
                    ? "http://" + (DeviceStatus.Ip ?? "127.0.0.1") + ":" + WebServer.ExternalPortActual + "/file"
                    : "http://" + host + "/file"))
              .Append(L10n.T(" · 与 FTP 同一目录</p></header>"));

            if (!string.IsNullOrEmpty(rel))
            {
                string up = rel.Contains("/") ? rel.Substring(0, rel.LastIndexOf('/')) : "";
                sb.Append("<a href='/file/").Append(E(up)).Append(L10n.T("'><span class=d><svg width='15' height='15' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' style='vertical-align:-2px;margin-right:4px'><path d='M12 19V5M5 12l7-7 7 7'/></svg>返回上级</span><span class=s></span></a>"));
            }

            try
            {
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var di = new DirectoryInfo(d);
                    sb.Append("<a href='/file/").Append(E(Join(rel, di.Name))).Append("'>")
                      .Append("<span class='n d'><svg width='15' height='15' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' style='vertical-align:-2px;margin-right:4px'><path d='M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/></svg>").Append(H(di.Name)).Append(L10n.T("</span><span class=s>目录</span></a>"));
                }
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fi = new FileInfo(f);
                    string ext = (fi.Extension ?? "").TrimStart('.').ToLowerInvariant();
                    string relPath = Join(rel, fi.Name);
                    bool readable = ext == "docx" || ext == "doc" || ext == "xlsx" || ext == "xls"
                        || ext == "pptx" || ext == "ppt" || ext == "pdf" || ext == "txt";
                    if (readable)
                    {
                        // 行内两段：左侧文件名点「阅读」（Web 阅读器），右侧大小点「下载」（浏览器直链）
                        string page = ext == "xlsx" || ext == "xls" ? "xlsx.html"
                            : ext == "pptx" ? "pptx.html" : ext == "ppt" ? "unsupported.html"
                            : ext == "doc" ? "prose.html" : ext == "pdf" ? "pdf.html"
                            : ext == "txt" ? "text.html" : "docx.html";
                        sb.Append("<div class='row'><a class='ra' href='/viewer/").Append(page)
                          .Append("?name=").Append(E(fi.Name)).Append("&ext=").Append(ext)
                          .Append("&path=").Append(E("share:" + relPath)).Append("'>")
                          .Append("<span class='n'>").Append(H(fi.Name)).Append("</span>")
                          .Append(L10n.T("<span class='rd'>阅读 ›</span></a>"))
                          .Append("<a class='dl' href='/file/get/").Append(E(relPath)).Append(L10n.T("'>下载</a></div>"));
                    }
                    else
                    {
                        sb.Append("<a href='/file/get/").Append(E(relPath)).Append("'>")
                          .Append("<span class='n'>").Append(H(fi.Name)).Append("</span><span class=s>")
                          .Append(Size(fi.Length)).Append("</span></a>");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append(L10n.T("<p style='padding:16px;color:#e64340'>读取目录失败：")).Append(H(ex.Message)).Append("</p>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string Join(string rel, string name)
        {
            return string.IsNullOrEmpty(rel) ? name : rel + "/" + name;
        }

        /* ===================== 下载 ===================== */

        private static void SendFile(TcpClient client, string full, bool headOnly = false)
        {
            var fi = new FileInfo(full);
            var ns = client.GetStream();
            string head = "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: " + Mime(fi.Extension) + "\r\n"
                        + "Content-Length: " + fi.Length + "\r\n"
                        + "Content-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(fi.Name) + "\r\n"
                        + "Cache-Control: no-store\r\n"
                        + "Connection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            if (!headOnly)
            {
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ns.Write(buf, 0, n);
                    }
                }
            }
            ns.Flush();
            try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            try { client.Close(); } catch { }
        }

        private static string Mime(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".mp4": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".pdf": return "application/pdf";
                case ".txt": case ".log": case ".md": return "text/plain; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".json": return "application/json";
                case ".apk": return "application/vnd.android.package-archive";
                case ".zip": return "application/zip";
                default: return "application/octet-stream";
            }
        }

        /* ===================== 工具 ===================== */

        /// <summary>把用户给的相对路径拼到根目录下，并挡住 ../ 越权。</summary>
        private static string SafeJoin(string root, string rel)
        {
            try
            {
                string clean = (rel ?? "").Replace('\\', '/').TrimStart('/');
                var parts = new List<string>();
                foreach (var seg in clean.Split('/'))
                {
                    if (string.IsNullOrEmpty(seg) || seg == ".") continue;
                    if (seg == "..") return null;
                    parts.Add(seg);
                }
                string full = parts.Count == 0 ? root : Path.Combine(root, string.Join("/", parts.ToArray()));
                string rootFull = Path.GetFullPath(root).TrimEnd('/');
                string target = Path.GetFullPath(full);
                if (!target.Equals(rootFull) && !target.StartsWith(rootFull + "/")) return null;
                return target;
            }
            catch { return null; }
        }

        private static string GetQuery(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";
            foreach (var kv in query.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                if (kv.Substring(0, eq) == key)
                    return Uri.UnescapeDataString(kv.Substring(eq + 1).Replace("+", " "));
            }
            return "";
        }

        private static string ReadHeader(NetworkStream ns)
        {
            var ms = new MemoryStream();
            int state = 0;
            var one = new byte[1];
            while (ms.Length < 16384)
            {
                int n;
                try { n = ns.Read(one, 0, 1); } catch { return null; }
                if (n <= 0) return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()) : null;
                ms.WriteByte(one[0]);
                if ((state == 0 || state == 2) && one[0] == (byte)'\r') state++;
                else if ((state == 1 || state == 3) && one[0] == (byte)'\n') state++;
                else state = 0;
                if (state == 4) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string Size(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0") + " KB";
            if (n < 1024L * 1024 * 1024) return (n / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (n / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        private static string H(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }
        private static string E(string s) { return Uri.EscapeDataString(s ?? ""); }

        private static void SendText(TcpClient client, int code, string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            var head = "HTTP/1.1 " + code + " Error\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: "
                     + body.Length + "\r\nConnection: close\r\n\r\n";
            try
            {
                var ns = client.GetStream();
                var hb = Encoding.ASCII.GetBytes(head);
                ns.Write(hb, 0, hb.Length);
                ns.Write(body, 0, body.Length);
                ns.Flush();
            }
            catch { }
            Close(client);
        }

        private static void SendHtml(TcpClient client, string html, bool headOnly = false)
        {
            var body = Encoding.UTF8.GetBytes(html ?? "");
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                     + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            try
            {
                var ns = client.GetStream();
                var hb = Encoding.ASCII.GetBytes(head);
                ns.Write(hb, 0, hb.Length);
                if (!headOnly) ns.Write(body, 0, body.Length);
                ns.Flush();
            }
            catch { }
            Close(client);
        }

        /* ===================== 供内置回环引擎（HttpListener）复用 ===================== */

        /// <summary>生成目录列表 HTML（rel 为相对目录）。目录不存在/越权返回 null。</summary>
        public static string TryList(string root, string subPath, string host)
        {
            string relDir;
            try { relDir = Uri.UnescapeDataString(subPath ?? "").Trim('/'); }
            catch { relDir = (subPath ?? "").Trim('/'); }
            string dir = SafeJoin(root, relDir);
            if (dir == null || !Directory.Exists(dir)) return null;
            return ListPage(root, dir, relDir, host);
        }

        /// <summary>该子路径是否是「下载」语义（/get/… 或 /raw?…）。</summary>
        public static bool IsDownload(string subPath)
        {
            string p = subPath ?? "";
            return p.StartsWith("/get/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/raw", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>解析下载目标；越权或不存在返回 null。</summary>
        public static string ResolveDownload(string root, string subPath, string query)
        {
            string p = subPath ?? "";
            string rel;
            if (p.StartsWith("/raw", StringComparison.OrdinalIgnoreCase))
                rel = GetQuery(query, "p");
            else if (p.StartsWith("/get/", StringComparison.OrdinalIgnoreCase))
                rel = Uri.UnescapeDataString(p.Substring(5));
            else return null;

            string full = SafeJoin(root, rel);
            return full != null && File.Exists(full) ? full : null;
        }

        /// <summary>按扩展名给出 Content-Type。</summary>
        public static string ContentTypeOf(string file) { return Mime(Path.GetExtension(file)); }

        /// <summary>从请求头里取 Host（用于页面上显示「你现在用的地址」）。</summary>
        public static string HostOf(string head)
        {
            try
            {
                if (string.IsNullOrEmpty(head)) return null;
                foreach (var line in head.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                        return t.Substring(5).Trim();
                }
            }
            catch { }
            return null;
        }

        private static void Close(TcpClient client)
        {
            try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            try { client.Close(); } catch { }
        }
    }
}

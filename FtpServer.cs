using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AhoChatA
{
    /**
     * 极简 FTP 服务（端口 2121，匿名登录）。
     *
     * 目的：让局域网内的 PC（Windows 资源管理器 / FileZilla / 命令行 ftp）能直接
     *       上传与下载手机共享目录中的文件，无需安装额外 App。
     *
     * 已实现命令：
     *   USER / PASS（任意凭据均视为匿名登录）、SYST、FEAT、OPTS、NOOP、TYPE、MODE、STRU
     *   PWD(XPWD) / CWD(XCWD) / CDUP(XCUP)
     *   PASV / EPSV / PORT（数据连接）
     *   LIST / NLST（列目录）、RETR（下载）、STOR / APPE（上传）
     *   DELE（删除）、MKD(XMKD) / RMD(XRMD)、RNFR / RNTO（重命名）
     *   SIZE / MDTM、QUIT
     *
     * 编码统一 UTF-8（控制与数据通道），并在 FEAT 中声明 UTF8，
     * 便于中文文件名在各客户端正常显示。
     */
    public sealed class FtpServer : IDisposable
    {
        public const int Port = 2121;

        private readonly string rootDir;
        private TcpListener listener;
        private CancellationTokenSource cts;
        private Task acceptTask;
        private volatile bool running;

        public bool Running => running;
        public string RootDirectory => rootDir;

        public FtpServer(string rootDir)
        {
            this.rootDir = rootDir;
            try { Directory.CreateDirectory(rootDir); } catch { }
        }

        /* ===================== 生命周期 ===================== */

        public void Start()
        {
            if (running) return;
            running = true;
            cts = new CancellationTokenSource();
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                try
                {
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                catch { }
                listener.Start();
            }
            catch (Exception ex)
            {
                running = false;
                Android.Util.Log.Error("FtpServer", "监听 " + Port + " 失败: " + ex.Message);
                return;
            }
            acceptTask = Task.Run(() => AcceptLoopAsync(cts.Token));
            Android.Util.Log.Info("FtpServer", "FTP 服务已启动 0.0.0.0:" + Port + " root=" + rootDir);
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            try { acceptTask?.Wait(1500); } catch { }
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) break;
                    continue;
                }
                var session = client;
                _ = Task.Run(() => HandleSession(session, token));
            }
        }

        /* ===================== 会话 ===================== */

        private void HandleSession(TcpClient client, CancellationToken token)
        {
            try
            {
                client.ReceiveTimeout = 300000;
                client.SendTimeout = 300000;
                client.NoDelay = true;
            }
            catch { }

            StreamWriter w = null;
            StreamReader r = null;
            TcpListener pasv = null;
            IPEndPoint portEp = null;

            try
            {
                var stream = client.GetStream();
                r = new StreamReader(stream, new UTF8Encoding(false), false);
                w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                w.NewLine = "\r\n";

                string cwd = "/";
                bool loggedIn = false;
                string renameFrom = null;

                Send(w, "220 Mchat FTP Service (anonymous) ready.");

                string line;
                while (!token.IsCancellationRequested && (line = r.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var sp = line.IndexOf(' ');
                    string cmd = (sp < 0 ? line : line.Substring(0, sp)).Trim().ToUpperInvariant();
                    string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

                    switch (cmd)
                    {
                        case "USER":
                            // 匿名服务：任何用户名都接受
                            Send(w, "331 Anonymous login ok, send password.");
                            break;

                        case "PASS":
                            loggedIn = true;
                            Send(w, "230 Login successful. Welcome.");
                            break;

                        case "SYST":
                            Send(w, "215 UNIX Type: L8");
                            break;

                        case "FEAT":
                            Send(w, "211-Features:");
                            Send(w, " UTF8");
                            Send(w, " MDTM");
                            Send(w, " SIZE");
                            Send(w, " PASV");
                            Send(w, " EPSV");
                            Send(w, "211 End");
                            break;

                        case "OPTS":
                            Send(w, "200 OPTS accepted (UTF8 on).");
                            break;

                        case "NOOP":
                            Send(w, "200 NOOP ok.");
                            break;

                        case "TYPE":
                            Send(w, "200 Type set to " + (string.IsNullOrEmpty(arg) ? "I" : arg) + ".");
                            break;

                        case "MODE":
                        case "STRU":
                            Send(w, "200 Command ok.");
                            break;

                        case "CLNT":
                        case "SITE":
                        case "ACCT":
                            Send(w, "200 Command ok.");
                            break;

                        case "PWD":
                        case "XPWD":
                            Send(w, "257 \"" + cwd + "\" is the current directory.");
                            break;

                        case "CWD":
                        case "XCWD":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var target = ResolveVirtual(cwd, arg);
                                var phys = PhysicalOf(target);
                                if (!Directory.Exists(phys)) { Send(w, "550 No such directory."); break; }
                                cwd = target;
                                Send(w, "250 Directory changed to " + cwd);
                            }
                            break;

                        case "CDUP":
                        case "XCUP":
                            if (!RequireLogin(w, loggedIn)) break;
                            cwd = ParentOf(cwd);
                            Send(w, "250 Directory changed to " + cwd);
                            break;

                        case "PASV":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                ClosePasv(ref pasv);
                                var local = LocalAddressOf(client);
                                var l = new TcpListener(local, 0);
                                l.Start();
                                pasv = l;
                                var p = ((IPEndPoint)l.LocalEndpoint).Port;
                                var b = local.GetAddressBytes();
                                Send(w, string.Format(
                                    "227 Entering Passive Mode ({0},{1},{2},{3},{4},{5}).",
                                    b[0], b[1], b[2], b[3], p / 256, p % 256));
                            }
                            break;

                        case "EPSV":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                ClosePasv(ref pasv);
                                var local = LocalAddressOf(client);
                                var l = new TcpListener(local, 0);
                                l.Start();
                                pasv = l;
                                var p = ((IPEndPoint)l.LocalEndpoint).Port;
                                Send(w, "229 Entering Extended Passive Mode (|||" + p + "|).");
                            }
                            break;

                        case "PORT":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var ep = ParsePort(arg);
                                if (ep == null) { Send(w, "501 Bad PORT syntax."); break; }
                                portEp = ep;
                                Send(w, "200 PORT command successful.");
                            }
                            break;

                        case "LIST":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var target = string.IsNullOrEmpty(arg) || arg.StartsWith("-")
                                    ? cwd : ResolveVirtual(cwd, arg);
                                var phys = PhysicalOf(target);
                                if (!Directory.Exists(phys)) { Send(w, "550 No such directory."); break; }

                                // 先回 150 再建立数据连接：部分客户端会等收到 150 才去连 PASV 端口，
                                // 若服务端先阻塞 accept 就会互相等待直到超时。
                                Send(w, "150 Opening ASCII mode data connection for file list.");
                                var dc = OpenData(w, ref pasv, ref portEp);
                                if (dc == null) break;

                                try
                                {
                                    using (dc)
                                    using (var ds = dc.GetStream())
                                    {
                                        var sb = new StringBuilder();
                                        foreach (var d in Directory.GetDirectories(phys))
                                            sb.Append(FormatList(new DirectoryInfo(d))).Append("\r\n");
                                        foreach (var f in Directory.GetFiles(phys))
                                            sb.Append(FormatList(new FileInfo(f))).Append("\r\n");
                                        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                                        ds.Write(bytes, 0, bytes.Length);
                                        ds.Flush();
                                    }
                                    Send(w, "226 Transfer complete.");
                                }
                                catch (Exception ex)
                                {
                                    Send(w, "426 Transfer aborted: " + ex.Message);
                                }
                            }
                            break;

                        case "NLST":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var target = string.IsNullOrEmpty(arg) || arg.StartsWith("-")
                                    ? cwd : ResolveVirtual(cwd, arg);
                                var phys = PhysicalOf(target);
                                if (!Directory.Exists(phys)) { Send(w, "550 No such directory."); break; }

                                Send(w, "150 Opening data connection for name list.");
                                var dc = OpenData(w, ref pasv, ref portEp);
                                if (dc == null) break;

                                try
                                {
                                    using (dc)
                                    using (var ds = dc.GetStream())
                                    {
                                        var sb = new StringBuilder();
                                        foreach (var d in Directory.GetDirectories(phys))
                                            sb.Append(Path.GetFileName(d)).Append("\r\n");
                                        foreach (var f in Directory.GetFiles(phys))
                                            sb.Append(Path.GetFileName(f)).Append("\r\n");
                                        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                                        ds.Write(bytes, 0, bytes.Length);
                                        ds.Flush();
                                    }
                                    Send(w, "226 Transfer complete.");
                                }
                                catch (Exception ex)
                                {
                                    Send(w, "426 Transfer aborted: " + ex.Message);
                                }
                            }
                            break;

                        case "RETR":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                if (!File.Exists(phys)) { Send(w, "550 File not found."); break; }

                                Send(w, "150 Opening BINARY mode data connection for " + Path.GetFileName(phys) + ".");
                                var dc = OpenData(w, ref pasv, ref portEp);
                                if (dc == null) break;

                                try
                                {
                                    using (dc)
                                    using (var ds = dc.GetStream())
                                    using (var fs = File.OpenRead(phys))
                                    {
                                        fs.CopyTo(ds);
                                        ds.Flush();
                                    }
                                    Send(w, "226 Transfer complete.");
                                }
                                catch (Exception ex)
                                {
                                    Send(w, "426 Transfer aborted: " + ex.Message);
                                }
                            }
                            break;

                        case "STOR":
                        case "APPE":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                try
                                {
                                    var dir = Path.GetDirectoryName(phys);
                                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                }
                                catch { }

                                Send(w, "150 Opening BINARY mode data connection for " + Path.GetFileName(phys) + ".");
                                var dc = OpenData(w, ref pasv, ref portEp);
                                if (dc == null) break;

                                try
                                {
                                    bool append = cmd == "APPE" && File.Exists(phys);
                                    using (dc)
                                    using (var ds = dc.GetStream())
                                    using (var fs = new FileStream(phys, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                                    {
                                        var buf = new byte[64 * 1024];
                                        int n;
                                        while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                                            fs.Write(buf, 0, n);
                                        fs.Flush();
                                    }
                                    Send(w, "226 Transfer complete.");
                                }
                                catch (Exception ex)
                                {
                                    Send(w, "426 Transfer aborted: " + ex.Message);
                                }
                            }
                            break;

                        case "DELE":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                if (!File.Exists(phys)) { Send(w, "550 File not found."); break; }
                                try { Send(w, TrashManager.Instance.MoveToTrash(phys, rootDir) ? "250 Deleted successfully." : "550 Could not move file to trash."); }
                                catch (Exception ex) { Send(w, "550 Delete failed: " + ex.Message); }
                            }
                            break;

                        case "MKD":
                        case "XMKD":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var virt = ResolveVirtual(cwd, arg);
                                var phys = PhysicalOf(virt);
                                if (Directory.Exists(phys)) { Send(w, "550 Directory already exists."); break; }
                                try { Directory.CreateDirectory(phys); Send(w, "257 \"" + virt + "\" created."); }
                                catch (Exception ex) { Send(w, "550 Create failed: " + ex.Message); }
                            }
                            break;

                        case "RMD":
                        case "XRMD":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                if (!Directory.Exists(phys)) { Send(w, "550 No such directory."); break; }
                                try { Send(w, TrashManager.Instance.MoveToTrash(phys, rootDir) ? "250 Directory removed." : "550 Could not move directory to trash."); }
                                catch (Exception ex) { Send(w, "550 Remove failed: " + ex.Message); }
                            }
                            break;

                        case "RNFR":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                if (!File.Exists(phys) && !Directory.Exists(phys)) { Send(w, "550 No such file or directory."); break; }
                                renameFrom = phys;
                                Send(w, "350 File exists, ready for destination name.");
                            }
                            break;

                        case "RNTO":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                if (renameFrom == null) { Send(w, "503 Bad sequence of commands."); break; }
                                var to = PhysicalOf(ResolveVirtual(cwd, arg));
                                try
                                {
                                    if (Directory.Exists(renameFrom)) Directory.Move(renameFrom, to);
                                    else File.Move(renameFrom, to);
                                    Send(w, "250 Rename successful.");
                                }
                                catch (Exception ex) { Send(w, "550 Rename failed: " + ex.Message); }
                                renameFrom = null;
                            }
                            break;

                        case "SIZE":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                if (!File.Exists(phys)) { Send(w, "550 File not found."); break; }
                                try { Send(w, "213 " + new FileInfo(phys).Length); }
                                catch { Send(w, "550 Could not get file size."); }
                            }
                            break;

                        case "MDTM":
                            if (!RequireLogin(w, loggedIn)) break;
                            {
                                var phys = PhysicalOf(ResolveVirtual(cwd, arg));
                                if (!File.Exists(phys)) { Send(w, "550 File not found."); break; }
                                Send(w, "213 " + File.GetLastWriteTimeUtc(phys).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                            }
                            break;

                        case "REST":
                            Send(w, "350 Restarting at 0.");
                            break;

                        case "QUIT":
                            Send(w, "221 Goodbye.");
                            return;

                        case "ABOR":
                            Send(w, "226 Abort successful.");
                            break;

                        default:
                            Send(w, "502 Command " + cmd + " not implemented.");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                try { Android.Util.Log.Warn("FtpServer", "会话异常: " + ex.Message); } catch { }
            }
            finally
            {
                try { pasv?.Stop(); } catch { }
                try { w?.Flush(); } catch { }
                try { client?.Close(); } catch { }
            }
        }

        /* ===================== 工具 ===================== */

        private static void Send(StreamWriter w, string text)
        {
            try
            {
                w.Write(text);
                w.Write("\r\n");
                w.Flush();
            }
            catch { }
        }

        private static bool RequireLogin(StreamWriter w, bool loggedIn)
        {
            if (loggedIn) return true;
            Send(w, "530 Please login with USER and PASS.");
            return false;
        }

        private static IPAddress LocalAddressOf(TcpClient client)
        {
            try
            {
                var ep = client.Client.LocalEndPoint as IPEndPoint;
                if (ep != null && !IPAddress.Any.Equals(ep.Address)) return ep.Address;
            }
            catch { }
            return IPAddress.Any;
        }

        private static void ClosePasv(ref TcpListener pasv)
        {
            try { pasv?.Stop(); } catch { }
            pasv = null;
        }

        private static TcpClient OpenData(StreamWriter w, ref TcpListener pasv, ref IPEndPoint portEp)
        {
            try
            {
                if (pasv != null)
                {
                    var l = pasv;
                    pasv = null;
                    try
                    {
                        return l.AcceptTcpClient();
                    }
                    finally { try { l.Stop(); } catch { } }
                }

                if (portEp != null)
                {
                    var c = new TcpClient();
                    c.Connect(portEp);
                    return c;
                }
            }
            catch (Exception ex)
            {
                Send(w, "425 Can't open data connection: " + ex.Message);
                return null;
            }

            Send(w, "425 Use PASV or PORT first.");
            return null;
        }

        private static IPEndPoint ParsePort(string arg)
        {
            try
            {
                var parts = arg.Split(',');
                if (parts.Length < 6) return null;
                var ip = string.Join(".", parts[0], parts[1], parts[2], parts[3]);
                int p = int.Parse(parts[4]) * 256 + int.Parse(parts[5]);
                return new IPEndPoint(IPAddress.Parse(ip), p);
            }
            catch { return null; }
        }

        /// <summary>把 FTP 虚拟路径（绝对或相对 cwd）规整为以 "/" 开头的绝对路径。</summary>
        private static string ResolveVirtual(string cwd, string arg)
        {
            var p = (arg ?? string.Empty).Replace('\\', '/');
            if (!p.StartsWith("/")) p = cwd.TrimEnd('/') + "/" + p;

            var segs = new List<string>();
            foreach (var s in p.Split('/'))
            {
                if (string.IsNullOrEmpty(s) || s == ".") continue;
                if (s == "..") { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); continue; }
                segs.Add(s);
            }
            return "/" + string.Join("/", segs);
        }

        private static string ParentOf(string virt)
        {
            var segs = new List<string>(virt.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            if (segs.Count > 0) segs.RemoveAt(segs.Count - 1);
            return "/" + string.Join("/", segs);
        }

        /// <summary>虚拟路径 → 物理路径，并强制限制在根目录内（防目录穿越）。</summary>
        private string PhysicalOf(string virt)
        {
            var rel = (virt ?? "/").TrimStart('/');
            var phys = string.IsNullOrEmpty(rel)
                ? rootDir
                : Path.Combine(rootDir, rel.Replace('/', Path.DirectorySeparatorChar));

            try { phys = Path.GetFullPath(phys); } catch { }

            var root = rootDir;
            try { root = Path.GetFullPath(rootDir); } catch { }
            if (!phys.StartsWith(root, StringComparison.Ordinal))
                phys = root;

            return phys;
        }

        /// <summary>Unix 风格 LIST 行，兼容资源管理器与 FileZilla。</summary>
        private static string FormatList(FileSystemInfo fi)
        {
            bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;
            long size = 0;
            if (!isDir)
            {
                try { size = ((FileInfo)fi).Length; } catch { }
            }
            var dt = fi.LastWriteTime;
            string date = dt.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

            return string.Format(
                "{0}rw-r--r-- 1 ftp ftp {1,12} {2} {3}",
                isDir ? 'd' : '-', size, date, fi.Name);
        }
    }
}

using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;

namespace AhoChatA
{
    /// <summary>
    /// 局域网文件共享总控：
    ///   1) 维护共享根目录（FTP 与网页共享共用同一个目录）
    ///   2) 启动/停止 2121 端口的匿名 FTP 服务
    ///   3) 读取当前 WiFi 本机 IP，并把「IP + 端口」刷新到常驻通知上
    ///   4) 监听网络变化，WiFi 断开/重连时自动更新通知（未连 WiFi 时不显示 IP）
    /// </summary>
    public static class LanShareService
    {
        public const int FtpPort = FtpServer.Port;
        public const int WebPort = WebServer.Port;

        /// <summary>共享根目录（FTP 与网页共享共用）。</summary>
        public static string ShareRoot { get; private set; }

        /// <summary>
        /// 取共享根目录；若 FTP 服务还没启动过（ShareRoot 为空），按需现场初始化。
        /// 网页端 /file 可能先于「文件共享」被打开，不能因此返回空根目录。
        /// </summary>
        public static string EnsureShareRoot()
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(ShareRoot))
                {
                    try
                    {
                        var ctx = appContext ?? Android.App.Application.Context;
                        if (ctx != null) EnsureRoot(ctx);
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("LanShare", "初始化共享根目录失败：" + ex.Message);
                    }
                }
                return ShareRoot;
            }
        }

        private static FtpServer ftp;
        private static LanShareNetReceiver receiver;
        private static Context appContext;
        private static readonly object Sync = new object();

        /// <summary>启动文件共享（FTP）并刷新常驻通知。</summary>
        public static void Start(Context context)
        {
            try
            {
                appContext = context?.ApplicationContext ?? context;
                if (appContext == null) return;

                EnsureRoot(appContext);

                lock (Sync)
                {
                    if (ftp == null)
                    {
                        ftp = new FtpServer(ShareRoot);
                    }
                    else if (!string.Equals(ftp.RootDirectory, ShareRoot, StringComparison.Ordinal))
                    {
                        // 共享根目录发生变化（例如从无权限回退目录切换到 /sdcard/Mchat/file）：
                        // 停止旧实例并以新根目录重建，FTP 与网页共享共用同一目录。
                        try { ftp.Stop(); } catch { }
                        try { ftp.Dispose(); } catch { }
                        ftp = new FtpServer(ShareRoot);
                    }

                    if (!ftp.Running) ftp.Start();
                }

                RegisterReceiver(appContext);
                RefreshNotification(appContext);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("LanShare", "启动文件共享失败: " + ex.Message);
            }
        }

        /// <summary>停止 FTP 服务并注销网络监听。</summary>
        public static void Stop(Context context = null)
        {
            try
            {
                lock (Sync)
                {
                    if (ftp != null)
                    {
                        ftp.Stop();
                        ftp = null;
                    }
                }

                if (receiver != null && appContext != null)
                {
                    try { appContext.UnregisterReceiver(receiver); } catch { }
                    receiver = null;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LanShare", "停止文件共享异常: " + ex.Message);
            }
        }

        public static bool FtpRunning
        {
            get { lock (Sync) { return ftp != null && ftp.Running; } }
        }

        /* ===================== 共享目录 ===================== */

        /// <summary>用户指定的公共根目录（需求明确：FTP/AhoChatA 共享根 = /sdcard/Mchat/file）。</summary>
        public const string PreferredRoot = "/sdcard/Mchat/file";

        private static void EnsureRoot(Context context)
        {
            // 回退：应用私有外部目录，保证未授权时 FTP/AhoChatA 共享也能正常运行
            string fallback = null;
            try { fallback = context.GetExternalFilesDir(null)?.AbsolutePath; } catch { }
            if (string.IsNullOrEmpty(fallback))
            {
                try { fallback = context.FilesDir?.AbsolutePath; } catch { }
            }
            if (string.IsNullOrEmpty(fallback)) fallback = "/sdcard";
            fallback = Path.Combine(fallback, "share");

            // 已获得存储访问权限、且公共根目录确实可写时，才使用用户指定的 /sdcard/Mchat/file；
            // 否则回退到应用私有目录，避免把共享根设成「看似可用、实则写不进」的路径。
            if (HasAllFilesAccess(context) && TryPrepare(PreferredRoot))
                ShareRoot = PreferredRoot;
            else
                ShareRoot = fallback;

            try { Directory.CreateDirectory(ShareRoot); } catch { }

            // 放一个说明文件，首次连上即可看到共享目录已就绪
            try
            {
                var readme = Path.Combine(ShareRoot, L10n.T("使用说明.txt"));
                if (!File.Exists(readme))
                {
                    var note = HasAllFilesAccess(context)
                        ? ""
                        : L10n.T("（当前未授予存储访问权限，临时使用应用私有目录；授予后自动切换到 ") + PreferredRoot + "）\r\n";
                    File.WriteAllText(readme,
                        L10n.T("本目录为 AhoChat 局域网共享目录（") + FtpPort + L10n.T(" 端口 FTP / ") + WebServer.ExternalPortActual + L10n.T(" 端口网页共享）。\r\n") +
                        L10n.T("· PC 可用 FTP 客户端匿名连接本机的 ") + FtpPort + L10n.T(" 端口上传/下载文件；\r\n") +
                        L10n.T("· 浏览器（同一局域网）访问 http://<本机IP>:") + WebServer.ExternalPortActual + L10n.T("/file 浏览 / 下载。\r\n") + note);
                }
            }
            catch { }
        }

        /// <summary>尝试在指定目录下创建并写入一个临时文件，验证其真实可写；验证后删除。</summary>
        private static bool TryPrepare(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, ".writetest_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(tmp, "ok");
                File.Delete(tmp);
                return true;
            }
            catch { return false; }
        }

        /// <summary>当前是否还需要用户授予存储访问权限才能使用公共共享目录 /sdcard/Mchat/file。</summary>
        public static bool NeedStorageAccess(Context context)
        {
            return !HasAllFilesAccess(context);
        }

        /// <summary>
        /// 是否已获得「所有文件访问」权限。
        /// Android 11+（API 30+）需要 MANAGE_EXTERNAL_STORAGE 才能写入公共存储（如 /sdcard/Mchat/file）；
        /// 低于 Android 11 则只要拥有 WRITE_EXTERNAL_STORAGE 即可。
        /// </summary>
        public static bool HasAllFilesAccess(Context context)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                try { return Android.OS.Environment.IsExternalStorageManager; }
                catch { return false; }
            }
            try
            {
                return context.CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage)
                       == Android.Content.PM.Permission.Granted;
            }
            catch { return false; }
        }

        /// <summary>
        /// 授权状态可能变化（用户在系统设置里授予「所有文件访问」后返回）。
        /// 重新计算共享根目录；若根目录发生变化则热切换 FTP 实例，使 FTP 与网页共享共用同一目录。
        /// </summary>
        public static void ReconfigureRoot(Context context)
        {
            try
            {
                appContext = context?.ApplicationContext ?? context ?? appContext;
                if (appContext == null) return;
                EnsureRoot(appContext);
                lock (Sync)
                {
                    if (ftp != null && !string.Equals(ftp.RootDirectory, ShareRoot, StringComparison.Ordinal))
                    {
                        try { ftp.Stop(); } catch { }
                        try { ftp.Dispose(); } catch { }
                        ftp = new FtpServer(ShareRoot);
                        if (!ftp.Running) ftp.Start();
                    }
                }
                RefreshNotification(appContext);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LanShare", "重配共享根目录异常: " + ex.Message);
            }
        }

        /* ===================== 网络状态 ===================== */

        /// <summary>当前 WiFi 本机 IPv4；未连接 WiFi 时返回 null。</summary>
        public static string GetWifiIp(Context context)
        {
            try
            {
                var ctx = context ?? appContext;
                if (ctx == null) return null;

                // Android 6+：通过 ConnectivityManager 精确判断是否走 WiFi 传输
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    var cm = (ConnectivityManager)ctx.GetSystemService(Context.ConnectivityService);
                    var net = cm?.ActiveNetwork;
                    if (net != null)
                    {
                        var caps = cm.GetNetworkCapabilities(net);
                        if (caps != null && caps.HasTransport(TransportType.Wifi))
                        {
                            var props = cm.GetLinkProperties(net);
                            if (props != null)
                            {
                                foreach (var la in props.LinkAddresses)
                                {
                                    var addr = la?.Address;
                                    if (addr == null) continue;
                                    var s = addr.HostAddress;
                                    if (string.IsNullOrEmpty(s)) continue;
                                    if (s.Contains(":")) continue;          // 跳过 IPv6
                                    if (s.StartsWith("127.")) continue;     // 跳过回环
                                    return s;
                                }
                            }
                        }
                    }
                }

                // 回退：WifiManager（老版本 / 上述取不到时）
                var wifi = (WifiManager)ctx.ApplicationContext.GetSystemService(Context.WifiService);
                var info = wifi?.ConnectionInfo;
                if (info != null)
                {
                    int ip = info.IpAddress;
                    if (ip != 0)
                    {
                        return string.Format("{0}.{1}.{2}.{3}",
                            ip & 0xff, (ip >> 8) & 0xff, (ip >> 16) & 0xff, (ip >> 24) & 0xff);
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LanShare", "获取 WiFi IP 失败: " + ex.Message);
            }
            return null;
        }

        public static bool IsWifiConnected(Context context)
        {
            return !string.IsNullOrEmpty(GetWifiIp(context));
        }

        /* ===================== 通知 ===================== */

        /// <summary>把「IP + 端口」写进常驻通知；未连 WiFi 时不显示 IP。</summary>
        public static void RefreshNotification(Context context = null)
        {
            try
            {
                var ctx = context ?? appContext;
                if (ctx == null) return;

                string ip = GetWifiIp(ctx);
                string text;
                if (!string.IsNullOrEmpty(ip))
                {
                    text = "IP " + ip + " · FTP " + FtpPort + L10n.T(" · 网页 ") + WebServer.ExternalPortActual;
                }
                else
                {
                    // 未连接 WiFi：按需求不显示 IP
                    text = L10n.T("未连接 WiFi · FTP ") + FtpPort + L10n.T(" · 网页 ") + WebServer.ExternalPortActual;
                }

                ChatForegroundService.UpdateNetwork(ctx, text);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LanShare", "刷新共享通知失败: " + ex.Message);
            }
        }

        /// <summary>给前端/调试用的共享信息摘要。</summary>
        public static string Describe(Context context = null)
        {
            var ip = GetWifiIp(context ?? appContext);
            return string.IsNullOrEmpty(ip)
                ? L10n.T("未连接 WiFi")
                : "http://" + ip + ":" + WebServer.ExternalPortActual + "/file (FTP " + ip + ":" + FtpPort + ")";
        }

        /* ===================== 网络变化监听 ===================== */

        private static void RegisterReceiver(Context ctx)
        {
            try
            {
                if (receiver != null) return;
                receiver = new LanShareNetReceiver();
                var filter = new IntentFilter();
                filter.AddAction(ConnectivityManager.ConnectivityAction);
                filter.AddAction("android.net.wifi.WIFI_STATE_CHANGED");
                filter.AddAction("android.net.wifi.STATE_CHANGE");
                ctx.RegisterReceiver(receiver, filter);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LanShare", "注册网络监听失败: " + ex.Message);
            }
        }
    }

    /// <summary>网络变化时刷新常驻通知（WiFi 断开/连上后 IP 会变）。</summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class LanShareNetReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try { LanShareService.RefreshNotification(context); }
            catch { }
        }
    }
}

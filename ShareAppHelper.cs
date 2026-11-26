// ============================================================
// 分享本程序
//
// 三条渠道：
//   1) gh-proxy 加速的 GitHub release 安装链接（文本分享）；
//   2) 系统分享面板发送本机安装包（ROM 提供蓝牙文件共享目标）；
//   3) 把安装包放进局域网共享根目录，网页 /file 路径默认可见。
//
// 说明：本程序的 bitchat 渠道是 BLE GATT mesh 文本隧道，不承载文件；
//       安装包外发走系统蓝牙分享或局域网 HTTP，这与 README 的表述一致。
// ============================================================
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Widget;

namespace AhoChatA
{
    public static class ShareAppHelper
    {
        private const string ApkMime = "application/vnd.android.package-archive";

        /// <summary>gh-proxy 加速的当前版本安装链接。</summary>
        public static string InstallUrl()
        {
            try { return UpdateManager.InstallUrl; }
            catch { return "https://github.com/yuwenhui2020/AhoChatA/releases"; }
        }

        /// <summary>把已安装 APK 复制到可分享位置；toLanRoot=true 时放进网页 /file 的共享根目录。</summary>
        public static string CopyInstaller(Context ctx, bool toLanRoot)
        {
            try
            {
                if (ctx == null) return null;
                var source = ctx.ApplicationInfo != null ? ctx.ApplicationInfo.SourceDir : null;
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;

                string dir;
                if (toLanRoot)
                {
                    dir = LanShareService.EnsureShareRoot();
                }
                else
                {
                    dir = Path.Combine(ctx.FilesDir.AbsolutePath, "media");
                }
                if (string.IsNullOrEmpty(dir)) return null;
                Directory.CreateDirectory(dir);

                var version = SafeName(UpdateManager.CurrentVersion);
                var dest = Path.Combine(dir, "AhoChat-v" + version + ".apk");
                var srcLen = new FileInfo(source).Length;
                if (!File.Exists(dest) || new FileInfo(dest).Length != srcLen)
                    File.Copy(source, dest, true);
                return dest;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("ShareApp", "复制安装包失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>分享安装链接（文本）。系统分享面板里可选择蓝牙发送到其它设备。</summary>
        public static void ShareLink(Activity act)
        {
            try
            {
                var url = InstallUrl();
                var text = L10n.T("AhoChat 安装包（gh-proxy 加速）", "AhoChat installer (gh-proxy accelerated)") + "\n" + url;
                var send = new Intent(Intent.ActionSend);
                send.SetType("text/plain");
                send.PutExtra(Intent.ExtraText, text);
                act.StartActivity(Intent.CreateChooser(send, L10n.T("分享安装链接", "Share install link")));
            }
            catch (Exception ex)
            {
                L10n.Toast(act, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>通过系统分享面板发送本机安装包；蓝牙目标由系统提供。</summary>
        public static void ShareInstaller(Activity act)
        {
            try
            {
                var path = CopyInstaller(act, false);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    L10n.Toast(act, L10n.T("无法读取本机安装包", "Cannot read the local installer"), ToastLength.Short).Show();
                    return;
                }
                InternalViewer.ShareFile(act, path, ApkMime);
            }
            catch (Exception ex)
            {
                L10n.Toast(act, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>把安装包放入局域网共享目录，返回可访问的地址提示。</summary>
        public static string ShareToLan(Activity act)
        {
            var path = CopyInstaller(act, true);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var ip = FirstLanIp();
            var port = WebServer.ExternalPortActual;
            return "http://" + (ip ?? "<本机局域网IP>") + ":" + port + "/file";
        }

        private static string SafeName(string s)
        {
            return string.IsNullOrEmpty(s)
                ? "unknown"
                : new string(s.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray());
        }

        /// <summary>取第一个私网 IPv4，用于拼出可直接在浏览器打开的 /file 地址。</summary>
        private static string FirstLanIp()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var info in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = info.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ip.ToString();
                        if (s == "127.0.0.1") continue;
                        if (s.StartsWith("10.") || s.StartsWith("192.168.") ||
                            (s.StartsWith("172.") && int.Parse(s.Split('.')[1]) >= 16 && int.Parse(s.Split('.')[1]) <= 31))
                            return s;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}

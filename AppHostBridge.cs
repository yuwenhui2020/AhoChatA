using System;
using System.Net;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Net;
using Android.Widget;
using Java.Interop;
using Java.IO;
using Android.Webkit;
using AndroidX.Core.Content;

namespace AhoChatA
{
    /// <summary>
    /// 宿主能力桥：提供退出应用、Toast 提示、剪贴板、后台播放状态同步等
    /// 前端无法自行完成的原生动作。
    /// </summary>
    public class AppHostBridge : Java.Lang.Object
    {
        private readonly Activity activity;

        public AppHostBridge(Activity activity)
        {
            this.activity = activity;
        }

        [Java.Interop.Export("exitApp")]
        [JavascriptInterface]
        public void ExitApp()
        {
            activity.RunOnUiThread(() => activity.FinishAffinity());
        }

        [Java.Interop.Export("toast")]
        [JavascriptInterface]
        public void Toast(string message)
        {
            activity.RunOnUiThread(() =>
            {
                try { L10n.Toast(activity, message ?? "", ToastLength.Short).Show(); }
                catch { }
            });
        }

        /// <summary>写入系统剪贴板。收到无前缀普通消息时由原生侧调用。</summary>
        [Java.Interop.Export("copyToClipboard")]
        [JavascriptInterface]
        public void CopyToClipboard(string text)
        {
            activity.RunOnUiThread(() => CopyText(activity, text));
        }

        /// <summary>
        /// 供原生各处复用的剪贴板写入。Android 10+ 后台应用无法写剪贴板，
        /// 但本应用挂了前台服务，属于「有前台服务的应用」，写入依然生效。
        /// </summary>
        public static void CopyText(Context context, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var cm = (ClipboardManager)context.GetSystemService(Context.ClipboardService);
                cm?.PrimaryClip?.Dispose();
                cm.PrimaryClip = ClipData.NewPlainText("Mchat", text);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Clipboard", $"写入剪贴板失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 同步网页端播放状态到常驻通知，使音乐在后台继续播放且可从通知栏控制。
        /// </summary>
        [Java.Interop.Export("updatePlayback")]
        [JavascriptInterface]
        public void UpdatePlayback(string title, string artist, bool playing, bool active)
        {
            try
            {
                if (active)
                {
                    ChatForegroundService.Update(activity,
                        string.IsNullOrEmpty(title) ? L10n.T("正在播放") : title,
                        string.IsNullOrEmpty(artist) ? L10n.T("聊天") : artist,
                        playing, true);
                }
                else
                {
                    ChatForegroundService.Update(activity, L10n.T("聊天"), L10n.T("正在后台接收消息"), false, false);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Playback", $"更新播放状态失败: {ex.Message}");
            }
        }

        /// <summary>返回网页端访问地址（ip:port），供前端在引导 UI 中展示局域网访问入口。</summary>
        [Java.Interop.Export("getWebEndpoint")]
        [JavascriptInterface]
        public string GetWebEndpoint()
        {
            try
            {
                var ip = LocalIp();
                if (!string.IsNullOrEmpty(ip))
                    return ip + ":" + AhoChatA.WebServer.Port;
            }
            catch { }
            return "";
        }

        /// <summary>获取本机在局域网中的 IPv4 地址（忽略回环与虚拟网卡）。</summary>
        private static string LocalIp()
        {
            try
            {
                using (var sock = new Java.Net.Socket())
                {
                    // 借助连接探测拿到本地出口 IP（不真正发包）
                }
            }
            catch { }

            try
            {
                var interfaces = Java.Net.NetworkInterface.NetworkInterfaces;
                var list = new List<string>();
                while (interfaces.HasMoreElements)
                {
                    var ni = (Java.Net.NetworkInterface)interfaces.NextElement();
                    if (ni.IsLoopback || !ni.IsUp) continue;
                    var adds = ni.InetAddresses;
                    while (adds.HasMoreElements)
                    {
                        var addr = (Java.Net.InetAddress)adds.NextElement();
                        if (addr.IsLoopbackAddress) continue;
                        var host = addr.HostAddress;
                        if (host != null && host.IndexOf(':') < 0 && !host.StartsWith("127."))
                            list.Add(host);
                    }
                }
                // 优先返回 192.168/10./172.16 这类典型内网地址
                list.Sort((a, b) =>
                {
                    int Rank(string s) =>
                        s.StartsWith("192.168") ? 0 :
                        s.StartsWith("10.") ? 1 :
                        s.StartsWith("172.16") ? 2 : 3;
                    return Rank(a).CompareTo(Rank(b));
                });
                if (list.Count > 0) return list[0];
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 让主 WebView 切换到指定虚拟主机 URL（如 https://app.local/ 或 https://web.local/）。
        /// 侧栏固定项「聊天 / 网页端」借此在主框架内切换，而非 iframe 嵌套，
        /// 从而避免两个 SPA 的返回键/侧栏逻辑相互干扰。
        /// </summary>
        [Java.Interop.Export("loadHost")]
        [JavascriptInterface]
        public void LoadHost(string url)
        {
            // 仅网页端（外部浏览器通过 WebServer 访问）使用；App 内已改为纯原生界面，此处不再切换 WebView 主机。
            if (string.IsNullOrEmpty(url)) return;
        }

        /// <summary>是否已在本系统「通知使用权」设置中授权本应用监听通知。</summary>
        [Java.Interop.Export("isNotificationListenerEnabled")]
        [JavascriptInterface]
        public bool IsNotificationListenerEnabled()
        {
            try
            {
                var cn = new ComponentName(activity, Java.Lang.Class.FromType(typeof(NotificationRelayService)));
                var enabled = Android.Provider.Settings.Secure.GetString(
                    activity.ContentResolver,
                    "enabled_notification_listeners");
                return enabled != null &&
                       enabled.Contains(cn.FlattenToString());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>打开系统「通知使用权」设置页，引导用户为本应用授权。</summary>
        [Java.Interop.Export("openNotificationListenerSettings")]
        [JavascriptInterface]
        public void OpenNotificationListenerSettings()
        {
            try
            {
                var intent = new Intent(Android.Provider.Settings.ActionNotificationListenerSettings);
                intent.AddFlags(ActivityFlags.NewTask);
                activity.StartActivity(intent);
            }
            catch
            {
                // 个别 ROM 可能无此 action，退回应用设置页
                try
                {
                    var intent = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings);
                    intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                    intent.AddFlags(ActivityFlags.NewTask);
                    activity.StartActivity(intent);
                }
                catch { }
            }
        }

        /* ===================== 悬浮窗 ===================== */

        /// <summary>是否已授予悬浮窗权限。</summary>
        [Java.Interop.Export("hasOverlayPermission")]
        [JavascriptInterface]
        public bool HasOverlayPermission()
        {
            return FloatingWindowService.CanDrawOverlays(activity);
        }

        /// <summary>跳转系统设置页申请悬浮窗权限。</summary>
        [Java.Interop.Export("requestOverlayPermission")]
        [JavascriptInterface]
        public void RequestOverlayPermission()
        {
            FloatingWindowService.RequestOverlayPermission(activity);
        }

        /// <summary>开启/关闭悬浮窗（状态持久化，下次启动 App 时按偏好恢复）。</summary>
        [Java.Interop.Export("setFloatingWindow")]
        [JavascriptInterface]
        public void SetFloatingWindow(bool on)
        {
            try
            {
                activity.GetSharedPreferences("Mchat", FileCreationMode.Private)
                        .Edit().PutBoolean("floatWindow", on).Apply();

                if (!on)
                {
                    FloatingWindowService.Hide(activity);
                    return;
                }
                if (!FloatingWindowService.CanDrawOverlays(activity))
                {
                    // 未授权：先去申请，授权后由前端复查并再次开启
                    FloatingWindowService.RequestOverlayPermission(activity);
                    return;
                }
                FloatingWindowService.Show(activity);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("FloatWin", $"设置浮窗失败: {ex.Message}");
            }
        }

        /// <summary>悬浮窗当前是否正在运行。</summary>
        [Java.Interop.Export("isFloatingWindowOn")]
        [JavascriptInterface]
        public bool IsFloatingWindowOn()
        {
            return FloatingWindowService.IsRunning;
        }

        /// <summary>
        /// 打开已收发文件（位于应用私有 FilesDir/media 目录）。
        /// 通过 AndroidX FileProvider 把私有文件包装成 content:// URI 并交给系统查看器，
        /// 使 PDF / 文档 / 安装包等任意类型都能被对应 App 打开（符合 Android 7+ 的 file:// 限制）。
        /// 若没有可处理该 MIME 的应用，则只提示、不崩溃。
        /// </summary>
        [Java.Interop.Export("openFile")]
        [JavascriptInterface]
        public void OpenFile(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return;

                // 从 /__media/<name> 或裸文件名中取出真正的存储文件名
                var name = url;
                var slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                var q = name.IndexOf('?');
                if (q >= 0) name = name.Substring(0, q);
                if (string.IsNullOrEmpty(name)) return;

                var dir = System.IO.Path.Combine(activity.FilesDir.AbsolutePath, "media");
                var file = new File(dir, name);
                if (!file.Exists()) { Toast(L10n.T("文件不存在或已被清理")); return; }

                var ext = (System.IO.Path.GetExtension(name) ?? "").ToLowerInvariant().TrimStart('.');

                // apk / apk.1 等安装包：直接转交系统安装器，避免误进阅读器当文档打开
                if (ApkInstallActivity.IsApkName(name) &&
                    ApkInstallActivity.RedirectLocalFileToInstaller(activity, file.AbsolutePath, name))
                    return;

                // 统一交给「阅读」模块：ppt/pptx/doc/docx/xls/xlsx/pdf/txt 等全部用本程序内置查看器，
                // 不依赖系统是否装了 WPS/Office；查看器用 WebView + 前端 JS 解析。
                if (OfficeViewerActivity.IsSupported(ext))
                {
                    activity.RunOnUiThread(() => OfficeViewerActivity.Open(activity, file.AbsolutePath, name));
                    return;
                }

                var mime = MimeTypeMap.Singleton.GetMimeTypeFromExtension(ext);
                if (string.IsNullOrEmpty(mime)) mime = "application/octet-stream";
                var uri = FileProvider.GetUriForFile(activity, "com.Ahochat.android.fileprovider", file);
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, mime);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                activity.RunOnUiThread(() =>
                {
                    try { activity.StartActivity(intent); }
                    catch (Exception) { Toast(L10n.T("没有可打开该文件的应用")); }
                });
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("OpenFile", $"打开文件失败: {ex.Message}");
                Toast(L10n.T("打开文件失败"));
            }
        }
    }
}

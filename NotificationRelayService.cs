using System;
using System.Text;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.Notification;

namespace AhoChatA
{
    /// <summary>
    /// 通知监听服务（NotificationListenerService）。
    ///
    /// 负责自动抓取系统通知栏通知，并格式化为
    ///   "应用名称:消息标题:消息内容"
    /// 后作为一条普通聊天消息直接自动广播到局域网（走 MchatBridge.SendPlainText）。
    ///
    /// 该服务与 app 主进程同进程运行；配合 ChatForegroundService 保活，
    /// 即便本软件退到后台（不在前台界面），只要进程未被系统回收，即可继续抓通知并发送。
    ///
    /// 使用前需用户在系统「通知使用权 / 通知访问」设置中为本应用授权。
    /// </summary>
    [Service(Exported = false, Name = "com.ztwl.NotificationRelayService",
             Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE")]
    [IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
    public class NotificationRelayService : NotificationListenerService
    {
        /// <summary>最近一次发送的内容指纹，用于同一通知的去重，避免连续重复推送。</summary>
        private static string lastFingerprint;
        private static long lastSendTime;

        public override void OnCreate()
        {
            base.OnCreate();
            Android.Util.Log.Info("NotifRelay", "通知监听服务已创建");
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            Android.Util.Log.Info("NotifRelay", "通知监听服务已销毁");
        }

        public override void OnNotificationPosted(StatusBarNotification sbn)
        {
            try
            {
                if (sbn == null) return;

                // 忽略自己 app 发出的通知，避免回环
                var selfPackage = ApplicationContext?.PackageName;
                if (!string.IsNullOrEmpty(selfPackage) &&
                    string.IsNullOrEmpty(sbn.PackageName) == false &&
                    sbn.PackageName == selfPackage)
                {
                    return;
                }

                var notification = sbn.Notification;
                if (notification == null) return;

                // 仅处理优先级足够、且确实对用户可见的通知
                var extras = notification.Extras;
                if (extras == null) return;

                // 应用名称：取包名对应的应用标签，取不到则回退包名
                var appName = AppLabel(sbn.PackageName);

                // 标题与内容
                var title = extras.GetCharSequence(Notification.ExtraTitle)?.ToString() ?? "";
                var textObj = extras.GetCharSequence(Notification.ExtraText)?.ToString()
                              ?? extras.GetCharSequence(Notification.ExtraBigText)?.ToString()
                              ?? "";
                // 有些应用把多行文本放在 EXTRA_TEXT_LINES（CharSequence[]）
                if (string.IsNullOrEmpty(textObj))
                {
                    var lines = extras.GetCharSequenceArray(Notification.ExtraTextLines);
                    if (lines != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var line in lines)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(line?.ToString() ?? "");
                        }
                        textObj = sb.ToString();
                    }
                }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(textObj))
                    return; // 无实质内容的通知（如静默前台服务通知）忽略

                var content = $"{appName}:{title}:{textObj}".Trim();
                if (string.IsNullOrWhiteSpace(content)) return;

                // 去重：相同指纹 1.5 秒内不重复
                var fp = sbn.PackageName + "|" + content;
                var now = Java.Lang.JavaSystem.CurrentTimeMillis();
                if (fp == lastFingerprint && (now - lastSendTime) < 1500)
                    return;
                lastFingerprint = fp;
                lastSendTime = now;

                MchatBridge.Current?.SendPlainText(content);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("NotifRelay", $"处理通知失败: {ex}");
            }
        }

        public override void OnNotificationRemoved(StatusBarNotification sbn)
        {
            // 无需处理移除事件
        }

        private string AppLabel(string packageName)
        {
            try
            {
                if (string.IsNullOrEmpty(packageName)) return "";
                var pm = ApplicationContext.PackageManager;
                var info = pm.GetApplicationInfo(packageName, Android.Content.PM.PackageInfoFlags.MatchAll);
                return pm.GetApplicationLabel(info)?.ToString() ?? packageName;
            }
            catch
            {
                return packageName ?? "";
            }
        }
    }
}

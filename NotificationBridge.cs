using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Webkit;
using AndroidX.Core.App;
using Java.Interop;

namespace AhoChatA
{
    /// <summary>
    /// 本机通知桥接。
    ///
    /// 用于 [notice] 消息：无论是本机发出的还是从局域网收到的，都统一
    /// 通过系统通知栏弹出到手机通知。Android 8.0+ 需要通知渠道，
    /// Android 13+ 需要 POST_NOTIFICATIONS 运行时权限（在 MainActivity 申请）。
    /// </summary>
    public class NotificationBridge : Java.Lang.Object
    {
        public const string ChannelId = "Mchat_notice";
        private const string ChannelName = "通知";

        private readonly Activity context;
        private int notifyId = 1000;

        public NotificationBridge(Activity context)
        {
            this.context = context;
            // 该「通知」渠道已废弃：常驻的「跨设备播放控制」通知已接管后台保活，
            // 不再需要聊天消息通知渠道。直接删除（若已存在）。
            RemoveChannel();
        }

        private void RemoveChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var manager = (NotificationManager)context.GetSystemService(Context.NotificationService);
                // 删除已创建的旧渠道（系统一旦删除，已安装应用不会再显示它）
                if (manager.GetNotificationChannel(ChannelId) != null)
                    manager.DeleteNotificationChannel(ChannelId);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Notice", $"删除通知渠道失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 聊天消息通知入口。原「通知」渠道已移除，为避免改动 JS 调用点，
        /// 这里改为空操作：消息仍会正常进入聊天列表，只是不再弹出系统通知。
        /// （常驻的「跨设备播放控制」通知已可替代其后台保活作用。）
        /// </summary>
        [Java.Interop.Export("notify")]
        [JavascriptInterface]
        public void Notify(string title, string text)
        {
            // 故意不弹通知。
        }
    }
}

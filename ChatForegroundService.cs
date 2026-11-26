using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace AhoChatA
{
    /// <summary>
    /// 常驻前台服务。
    ///
    /// 作用有两个，都要求进程在退到后台后仍然存活：
    ///   1) 音乐后台播放：WebView 里的 &lt;audio&gt; 归属于宿主进程，进程被系统回收
    ///      即停止播放。挂一个前台服务可让进程保持在前台优先级，从而后台继续放歌。
    ///   2) 后台收消息：MchatBridge 的 TCP/UDP 监听线程同样依附于进程，
    ///      前台服务保证锁屏/切后台时不会错漏局域网消息，进而能把无前缀的普通
    ///      消息写入剪贴板。
    ///
    /// 通知内容由前端在播放/暂停时通过 AndroidHost.updatePlayback 刷新。
    /// </summary>
    [Service(Exported = false, Name = "com.ztwl.ChatForegroundService")]
    public class ChatForegroundService : Service
    {
        public const string ChannelId = "Mchat_service";
        private const string ChannelName = "后台运行";
        private const int NotificationId = 2001;

        public const string ActionStart = "com.ztwl.action.START";
        public const string ActionUpdate = "com.ztwl.action.UPDATE";
        public const string ActionUpdateNet = "com.ztwl.action.UPDATE_NET";
        public const string ActionStop = "com.ztwl.action.STOP";

        public const string ActionPlayPause = "com.ztwl.action.PLAY_PAUSE";
        public const string ActionStopMusic = "com.ztwl.action.STOP_MUSIC";

        public const string ExtraTitle = "title";
        public const string ExtraText = "text";
        public const string ExtraPlaying = "playing";
        public const string ExtraShowControls = "controls";
        /// <summary>网络/共享信息行（如 "IP 192.168.1.5 · FTP 2121 · 网页 7790"）。</summary>
        public const string ExtraNet = "net";

        private const string DefaultText = "正在后台接收消息";

        /// <summary>当用户点击通知栏播放控制时触发，由 MainActivity 转发到网页端。</summary>
        public static event Action<string> MediaCommand;

        private string curTitle = L10n.T("聊天");
        private string curText = DefaultText;
        private string curNet = "";
        private bool curPlaying;
        private bool curControls;
        private bool _stopping;   // 主动退出标记：区分L10n.T("用户主动退出")与L10n.T("被系统划掉")，避免主动退出又被拉起

        public override IBinder OnBind(Intent intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            EnsureChannel();
            // Android 要求 startForegroundService 之后 5 秒内必须调用 StartForeground，
            // 因此这里先立刻挂一个占位通知，后续再由 UPDATE 刷新内容。
            StartForegroundCompat();

            // 被系统重启（START_STICKY）或开机自启拉起时，把后台聊天栈也恢复起来，
            // 以便不打开界面也能继续收消息。幂等：正常启动时已在跑则直接返回。
            // 这里必须在子线程做：Service.onCreate 超时只有 5 秒，
            // Bridge 构造要读 SharedPreferences、生成/加载三把身份密钥，
            // 放在主线程会让「前台服务启动」成为一次 ANR 的触发点（真机已复现）。
            CoreHost.PreinitAsync(this);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { CoreHost.EnsureBackground(this); }
                catch (Exception ex) { Android.Util.Log.Warn("Mchat", "前台服务后台初始化失败: " + ex.Message); }
            });
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var action = intent?.Action;

            switch (action)
            {
                case ActionStop:
                    _stopping = true;
                    StopForegroundCompat();
                    StopSelf();
                    return StartCommandResult.NotSticky;

                case ActionPlayPause:
                    MediaCommand?.Invoke("toggle");
                    return StartCommandResult.Sticky;

                case ActionStopMusic:
                    MediaCommand?.Invoke("stop");
                    return StartCommandResult.Sticky;

                case ActionUpdate:
                    curTitle = intent.GetStringExtra(ExtraTitle) ?? curTitle;
                    curText = intent.GetStringExtra(ExtraText) ?? curText;
                    curPlaying = intent.GetBooleanExtra(ExtraPlaying, false);
                    curControls = intent.GetBooleanExtra(ExtraShowControls, false);
                    StartForegroundCompat();
                    return StartCommandResult.Sticky;

                case ActionUpdateNet:
                    curNet = intent.GetStringExtra(ExtraNet) ?? "";
                    StartForegroundCompat();
                    return StartCommandResult.Sticky;
            }

            StartForegroundCompat();
            return StartCommandResult.Sticky;
        }

        /// <summary>
        /// 用户从「最近任务」划掉 App 时，系统回调这里。为了自启保活，
        /// 立即重新拉起前台服务（除非是程序主动 Stop），这样后台收消息/通知不中断。
        /// </summary>
        public override void OnTaskRemoved(Intent rootIntent)
        {
            try
            {
                if (!_stopping)
                {
                    Android.Util.Log.Info("FgService", "任务被划掉，自启前台服务");
                    Start(ApplicationContext);
                    CoreHost.EnsureBackground(ApplicationContext);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("FgService", "任务移除自启失败: " + ex.Message);
            }
            base.OnTaskRemoved(rootIntent);
        }

        private void EnsureChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var manager = (NotificationManager)GetSystemService(NotificationService);
                if (manager.GetNotificationChannel(ChannelId) != null) return;

                // 用 Low 重要度：常驻通知不应每次刷新都发声/横幅打扰用户
                var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
                {
                    Description = L10n.T("保持后台接收消息与音乐播放")
                };
                channel.SetShowBadge(false);
                channel.EnableVibration(false);
                channel.SetSound(null, null);
                manager.CreateNotificationChannel(channel);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FgService", $"创建服务通知渠道失败: {ex.Message}");
            }
        }

        private void StartForegroundCompat()
        {
            try
            {
                var notification = BuildNotification();
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    StartForeground(NotificationId, notification,
                        ForegroundService.TypeMediaPlayback | ForegroundService.TypeDataSync);
                }
                else
                {
                    StartForeground(NotificationId, notification);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FgService", $"StartForeground 失败: {ex.Message}");
                // 退化为普通通知，至少保证进程有可见提示
                try { StartForeground(NotificationId, BuildNotification()); } catch { }
            }
        }

        private void StopForegroundCompat()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                    StopForeground(StopForegroundFlags.Remove);
                else
#pragma warning disable CS0618
                    StopForeground(true);
#pragma warning restore CS0618
            }
            catch { }
        }

        private Notification BuildNotification()
        {
            var contentIntent = new Intent(this, typeof(ChatActivity));
            contentIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            var pending = PendingIntent.GetActivity(this, 0, contentIntent, flags);

            var builder = new NotificationCompat.Builder(this, ChannelId)
                .SetSmallIcon(Resource.Drawable.icon)
                .SetContentTitle(curTitle)
                .SetContentText(ComposeText())
                .SetPriority((int)NotificationPriority.Low)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetShowWhen(false)
                .SetContentIntent(pending);

            if (curControls)
            {
                builder.AddAction(0, curPlaying ? L10n.T("暂停") : L10n.T("播放"), ServiceIntent(ActionPlayPause));
                builder.AddAction(0, L10n.T("停止"), ServiceIntent(ActionStopMusic));
            }

            return builder.Build();
        }

        /// <summary>
        /// 组合通知正文：优先展示网络/共享信息（IP + 端口）；
        /// 若同时有音乐文案，则两者拼接显示。
        /// </summary>
        private string ComposeText()
        {
            bool hasNet = !string.IsNullOrEmpty(curNet);
            bool hasCustom = !string.IsNullOrEmpty(curText) && curText != DefaultText;

            if (hasNet && hasCustom) return curNet + " · " + curText;
            if (hasNet) return curNet;
            return curText ?? DefaultText;
        }

        private PendingIntent ServiceIntent(string action)
        {
            var intent = new Intent(this, typeof(ChatForegroundService));
            intent.SetAction(action);

            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            return PendingIntent.GetService(this, action.GetHashCode() & 0xffff, intent, flags);
        }

        /// <summary>启动（或刷新）前台服务。</summary>
        public static void Start(Context context)
        {
            try
            {
                var intent = new Intent(context, typeof(ChatForegroundService));
                intent.SetAction(ActionStart);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(intent);
                else
                    context.StartService(intent);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FgService", $"启动前台服务失败: {ex.Message}");
            }
        }

        /// <summary>刷新常驻通知的文案与播放控制按钮。</summary>
        public static void Update(Context context, string title, string text, bool playing, bool showControls)
        {
            try
            {
                var intent = new Intent(context, typeof(ChatForegroundService));
                intent.SetAction(ActionUpdate);
                intent.PutExtra(ExtraTitle, title ?? L10n.T("聊天"));
                intent.PutExtra(ExtraText, text ?? "");
                intent.PutExtra(ExtraPlaying, playing);
                intent.PutExtra(ExtraShowControls, showControls);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(intent);
                else
                    context.StartService(intent);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FgService", $"更新前台服务失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 只刷新常驻通知里的「网络/共享」行（WiFi IP 与端口），不影响音乐标题与播放控制。
        /// 未连接 WiFi 时由调用方保证不传入 IP。
        /// </summary>
        public static void UpdateNetwork(Context context, string netText)
        {
            try
            {
                var intent = new Intent(context, typeof(ChatForegroundService));
                intent.SetAction(ActionUpdateNet);
                intent.PutExtra(ExtraNet, netText ?? "");

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(intent);
                else
                    context.StartService(intent);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("FgService", $"更新网络信息失败: {ex.Message}");
            }
        }

        public static void Stop(Context context)
        {
            try
            {
                var intent = new Intent(context, typeof(ChatForegroundService));
                intent.SetAction(ActionStop);
                context.StartService(intent);
            }
            catch { }
        }
    }
}

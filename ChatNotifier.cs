using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace AhoChatA
{
    /// <summary>
    /// 聊天消息通知：收到消息时用「系统自带的横幅（Heads-Up）」提醒，并带原生「回复」输入。
    /// 用户要的"悬浮窗回复提醒"就是这条系统通知横幅；它与 SYSTEM_ALERT_WINDOW 自绘悬浮窗无关。
    /// </summary>
    public static class ChatNotifier
    {
        // 新渠道 ID：老 Mchat_msg 若被用户/ROM 调低 importance，渠道属性一旦创建就无法靠代码提升，
        // 会表现为"允许了悬浮窗却不弹横幅"。一次性迁移到全新 High 渠道可根治。
        public const string ChannelId = "Mchat_msg_v2";
        private const string LegacyChannelId = "Mchat_msg";
        private const string ChannelName = "聊天消息（横幅提醒）";
        private const string MigrationKey = "mchat_notif_channel_v2";

        public const string ReplyAction = "com.ztwl.action.REPLY";
        public const string ExtraConv = "nconv";
        public const string KeyReply = "nreply";

        private static Context appCtx;
        private static readonly object _gate = new object();
        private static int _nextId = 10000;
        private static readonly Dictionary<string, int> _convLastId = new Dictionary<string, int>();
        private static Handler _uiHandler;

        public static void Init(Context ctx)
        {
            try
            {
                appCtx = ctx.ApplicationContext;
                if (_uiHandler == null) _uiHandler = new Handler(Looper.MainLooper);
                EnsureChannel();
            }
            catch { }
        }

        private static void EnsureChannel()
        {
            if (appCtx == null || (int)Build.VERSION.SdkInt < 26) return;
            try
            {
                var mgr = (NotificationManager)appCtx.GetSystemService(Context.NotificationService);
                var prefs = appCtx.GetSharedPreferences("Mchat", FileCreationMode.Private);
                if (!prefs.GetBoolean(MigrationKey, false))
                {
                    // 一次性：删掉可能被降级的老渠道，确保新渠道以 High 创建。
                    try { mgr.DeleteNotificationChannel(LegacyChannelId); } catch { }
                    try { mgr.DeleteNotificationChannel(ChannelId); } catch { }
                    prefs.Edit().PutBoolean(MigrationKey, true).Apply();
                    Android.Util.Log.Info("Notify", "消息通知渠道已迁移到 High 横幅渠道");
                }
                if (mgr.GetNotificationChannel(ChannelId) == null)
                {
                    var ch = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.High)
                    {
                        Description = L10n.T("新消息以屏幕顶部横幅提醒，可直接回复"),
                        LockscreenVisibility = NotificationVisibility.Public
                    };
                    ch.EnableVibration(true);
                    ch.SetVibrationPattern(new long[] { 0, 250, 150, 250 });
                    mgr.CreateNotificationChannel(ch);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Notify", "创建通知渠道失败: " + ex.Message);
            }
        }

        /// <summary>系统通知总开关是否开启（Android 13+ POST_NOTIFICATIONS 也计入）。</summary>
        public static bool Enabled
        {
            get
            {
                try { return appCtx != null && NotificationManagerCompat.From(appCtx).AreNotificationsEnabled(); }
                catch { return false; }
            }
        }

        private static PendingIntentFlags PiFlags()
        {
            // 回复用的广播 PendingIntent 必须 Mutable：RemoteInput 的结果由系统在
            // 用户点发送时写进 intent extras，Immutable 在部分 ROM（MIUI 等）上
            // 会导致结果写入失败，ReplyReceiver 取不到文字、用户点了回复没反应。
            if ((int)Build.VERSION.SdkInt >= 31)
                return PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable;
            if ((int)Build.VERSION.SdkInt >= 23)
                return PendingIntentFlags.UpdateCurrent;
            return PendingIntentFlags.CancelCurrent;
        }

        /// <summary>弹出一条消息横幅通知（带「回复」快捷输入）。</summary>
        public static void NotifyMessage(string conv, string title, string text)
        {
            if (appCtx == null || string.IsNullOrEmpty(conv)) return;
            try
            {
                if (!Enabled)
                {
                    Android.Util.Log.Warn("Notify", "系统通知被关闭，跳过横幅提醒（请在系统设置里允许通知）");
                    return;
                }
                EnsureChannel();

                // 每条消息用全新 id：同会话复用固定 id 时，系统只"更新"旧通知，不重新弹横幅。
                // 新 id 保证每条消息都触发一次 Heads-Up；旧通知延迟取消，避免通知栏堆积。
                int newId, oldId = 0;
                lock (_gate)
                {
                    _convLastId.TryGetValue(conv, out oldId);
                    newId = System.Threading.Interlocked.Increment(ref _nextId);
                    _convLastId[conv] = newId;
                }

                var open = new Intent(appCtx, typeof(ChatActivity));
                open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
                open.PutExtra("openConv", conv);
                var openPending = PendingIntent.GetActivity(appCtx, newId, open, PiFlags());

                var b = new NotificationCompat.Builder(appCtx, ChannelId)
                    .SetSmallIcon(Resource.Drawable.icon)
                    .SetContentTitle(string.IsNullOrEmpty(title) ? L10n.T("新消息") : title)
                    .SetContentText(text ?? "")
                    .SetStyle(new NotificationCompat.BigTextStyle().BigText(text ?? ""))
                    .SetAutoCancel(true)
                    .SetCategory(Notification.CategoryMessage)
                    .SetPriority((int)NotificationPriority.High)
                    .SetDefaults((int)(NotificationDefaults.Sound | NotificationDefaults.Vibrate))
                    .SetVibrate(new long[] { 0, 250, 150, 250 })
                    .SetTimeoutAfter(60000)
                    .SetVisibility(NotificationCompat.VisibilityPublic)
                    .SetContentIntent(openPending);

                // 原生快捷回复
                try
                {
                    var remote = new AndroidX.Core.App.RemoteInput.Builder(KeyReply)
                        .SetLabel(L10n.T("回复…")).Build();
                    var ri = new Intent(appCtx, typeof(ReplyReceiver));
                    ri.SetAction(ReplyAction);
                    ri.PutExtra(ExtraConv, conv);
                    var rp = PendingIntent.GetBroadcast(appCtx, newId + 1, ri, PiFlags());
                    var act = new NotificationCompat.Action.Builder(0, L10n.T("回复"), rp)
                        .AddRemoteInput(remote).SetAllowGeneratedReplies(true).Build();
                    b.AddAction(act);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Notify", "添加回复动作失败: " + ex.Message);
                }

                NotificationManagerCompat.From(appCtx).Notify(newId, b.Build());
                Android.Util.Log.Info("Notify", "弹横幅 id=" + newId + " conv=" + conv + " title=" + title);

                // 延迟取消同一会话的上一条通知：横幅已弹完再清，避免通知栏堆积又不影响本次提醒。
                if (oldId > 0 && _uiHandler != null)
                {
                    int toCancel = oldId;
                    _uiHandler.PostDelayed(() =>
                    {
                        try { NotificationManagerCompat.From(appCtx).Cancel(toCancel); } catch { }
                    }, 1500);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Notify", "消息通知失败: " + ex.Message);
            }
        }

        public static void Cancel(string conv)
        {
            try
            {
                if (appCtx == null || string.IsNullOrEmpty(conv)) return;
                int id;
                lock (_gate)
                {
                    if (!_convLastId.TryGetValue(conv, out id)) return;
                    _convLastId.Remove(conv);
                }
                NotificationManagerCompat.From(appCtx).Cancel(id);
                NotificationManagerCompat.From(appCtx).Cancel(id + 1);
            }
            catch { }
        }
    }

    /// <summary>通知栏「回复」接收器：读出输入文字，直接发回对应会话。</summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    [IntentFilter(new[] { ChatNotifier.ReplyAction })]
    public class ReplyReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try
            {
                Android.Util.Log.Info("Notify", "ReplyReceiver 收到广播 action=" + (intent?.Action ?? "null")
                    + " conv=" + (intent?.GetStringExtra(ChatNotifier.ExtraConv) ?? "null"));
                var conv = intent?.GetStringExtra(ChatNotifier.ExtraConv);
                string text = null;
                try
                {
                    var results = AndroidX.Core.App.RemoteInput.GetResultsFromIntent(intent);
                    if (results != null)
                        text = results.GetCharSequence(ChatNotifier.KeyReply)?.ToString();
                    Android.Util.Log.Info("Notify", "RemoteInput 结果=" + (results == null ? "null" : ("text=" + text)));
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Notify", "读取 RemoteInput 失败: " + ex.Message);
                }
                // 兜底：少数 ROM 的 RemoteInput 结果取不到时，从同名普通 extra 读，
                // 避免用户点了回复却因取不到文字而什么都没发生。
                if (string.IsNullOrWhiteSpace(text))
                {
                    try { text = intent?.GetStringExtra(ChatNotifier.KeyReply); } catch { }
                }

                if (string.IsNullOrEmpty(conv) || string.IsNullOrWhiteSpace(text)) return;

                // 接收器运行在主线程且随时会被回收：用 goAsync 把「拉起后台 + 发送」
                // 挪到后台线程，发完再 finish，避免冷启动时消息被静默丢掉。
                var pending = GoAsync();
                var appCtx = context.ApplicationContext ?? context;
                new System.Threading.Thread(() =>
                {
                    try
                    {
                        // 冷启动兜底：先把整套后台栈拉起来（Preinit 只建 Bridge，不起通道）
                        try { CoreHost.EnsureBackground(appCtx); } catch { }
                        try { ChatForegroundService.Start(appCtx); } catch { }

                        // 优先用 Current（构造即赋值，最可靠），再退到 CoreHost.Bridge。
                        // SendReplyToConv 内部保证「先落库进会话，再尽力发送」，
                        // 因此这里最多重试几次即可，不再傻等 8 秒后丢弃用户回复。
                        bool sent = false;
                        for (int i = 0; i < 12 && !sent; i++)
                        {
                            var bridge = MchatBridge.Current ?? CoreHost.Bridge;
                            try { sent = bridge != null && bridge.SendReplyToConv(conv, text); }
                            catch (Exception ex)
                            {
                                Android.Util.Log.Warn("Notify", "快捷回复尝试失败: " + ex.Message);
                            }
                            if (!sent) System.Threading.Thread.Sleep(250);
                        }
                        Android.Util.Log.Info("Notify", "快捷回复 conv=" + conv
                            + " text=" + text + " => " + (sent ? L10n.T("已入聊天") : L10n.T("失败（已尽力）")));
                        if (sent) { try { ChatNotifier.Cancel(conv); } catch { } }
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn("Notify", "通知回复处理失败: " + ex.Message);
                    }
                    finally
                    {
                        try { pending?.Finish(); } catch { }
                    }
                }) { IsBackground = true, Name = "reply-send" }.Start();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Notify", "通知回复处理失败: " + ex.Message);
            }
        }
    }
}

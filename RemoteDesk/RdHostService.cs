using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using AndroidX.Core.App;

namespace AhoChatA
{
    /// <summary>
    /// 安卓被控端的前台服务壳（与屏幕共享的 ScreenCastService 同思路）。
    ///
    /// 存在的唯一原因：Android 10+ 规定 MediaProjection 只能在
    /// foregroundServiceType=mediaProjection 的前台服务里创建，否则 GetMediaProjection 直接拒绝。
    /// 同时它保证被控期间进程不被系统回收，并在通知栏给用户一个"正在被控"的显式出口
    /// —— 这是安全底线：被控绝不能是"静默进行"的。
    /// </summary>
    [Service(Name = "com.Ahochat.android.RdHostService", Exported = false)]
    public class RdHostService : Service
    {
        public const string ActionStart = "com.Ahochat.android.RD_HOST_START";
        public const string ActionStop = "com.Ahochat.android.RD_HOST_STOP";
        public const string ExtraResultCode = "rc";
        public const string ExtraData = "data";
        public const string ExtraShort = "short";
        public const string ExtraFps = "fps";
        public const string ExtraQuality = "q";

        private const string ChannelId = "Mchat_rd_host";
        private const string ChannelName = "允许被远控";
        private const int NotificationId = 2031;
        private const string Tag = "RdHostService";

        /// <summary>启动结果回调（ok, 说明）。同进程静态事件，避开 Android 13+ 的动态广播限制。</summary>
        public static event Action<bool, string> StartResult;
        private static void Notify(bool ok, string msg)
        {
            var cb = StartResult;
            if (cb != null) { try { cb(ok, msg); } catch { } }
        }

        public override IBinder OnBind(Intent intent) { return null; }

        public override void OnCreate()
        {
            base.OnCreate();
            EnsureChannel();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent != null && intent.Action == ActionStop)
            {
                try { RdHostServer.Instance.Stop(); } catch { }
                try { StopForeground(StopForegroundFlags.Remove); } catch { }
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            // 【为什么 · 先 FGS 再拿投影 + 时序校验】Android 10+ 要求 GetMediaProjection 前
            // 已有 mediaProjection 类型前台服务；旧代码 StartForeground 失败只写日志仍继续，
            // 后面只会拿到一句模糊的「授权无效」。这里确认成功才消耗 token。
            if (!StartForegroundCompat())
            {
                Log.Warn(Tag, "前台服务(mediaProjection)启动失败，未消耗授权 token");
                Notify(false, L10n.T("前台服务启动失败（需 mediaProjection 前台服务权限），请重新授权开启被控"));
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            if (intent == null) { StopSelf(); return StartCommandResult.NotSticky; }

            int rc = intent.GetIntExtra(ExtraResultCode, 0);
            Intent data = null;
#pragma warning disable CS0618
            try { data = intent.GetParcelableExtra(ExtraData) as Intent; } catch { }
#pragma warning restore CS0618
            var s = ScreenShareSettings.Instance(this);
            // 硬编码 720 / 12 / 55 全部改读集中设置；首次低 fps 起步（与 RdSession.InitialFps 一致），
            // 别等查看端 QualitySettings 之前就把采集线程打满（配合 RdHostServer 背压）。
            int shortSide = intent.GetIntExtra(ExtraShort, ScreenShareSettings.FallbackShortSide);
            int fps = intent.GetIntExtra(ExtraFps, Math.Max(5, Math.Min(10, s.MaxFps)));
            int quality = intent.GetIntExtra(ExtraQuality, s.ClampQuality(s.JpegQuality));
            s.LogEffective("RdHostService.OnStartCommand");

            if (data == null || rc != (int)Result.Ok)
            {
                Log.Warn(Tag, "屏幕录制授权无效 rc=" + rc + " data=" + (data != null));
                Notify(false, L10n.T("没有有效的屏幕录制授权（token 一次性），请重新开启被控授权"));
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            new System.Threading.Thread(delegate ()
            {
                bool ok = false;
                string msg = null;
                try
                {
                    ok = RdHostServer.Instance.Start(this, rc, data, shortSide, fps, quality);
                    if (!ok) msg = RdHostServer.Instance.LastError;
                }
                catch (Exception ex) { msg = "启动被控异常：" + ex.GetType().Name + " - " + ex.Message; Log.Warn(Tag, msg); }
                // 异常不再全吞：明确日志 + 回报，让上层知道需重新授权后重试。
                if (!ok) Log.Warn(Tag, "被控启动失败：" + (msg ?? "未知原因") + "（需重新授权后重试）");
                Notify(ok, msg);
                if (!ok) { try { StopSelf(); } catch { } }
            })
            { IsBackground = true, Name = "rd-host-start" }.Start();

            return StartCommandResult.NotSticky;
        }

        public override void OnDestroy()
        {
            try { RdHostServer.Instance.Stop(); } catch { }
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                    StopForeground(StopForegroundFlags.Remove);
            }
            catch { }
            base.OnDestroy();
        }

        /* ---------- 通知 ---------- */

        private bool StartForegroundCompat()
        {
            try
            {
                Notification n;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var builder = new NotificationCompat.Builder(this, ChannelId)
                        .SetContentTitle(L10n.T("本机正在被远控"))
                        .SetContentText(L10n.T("点此可随时停止被控"))
                        .SetSmallIcon(Resource.Drawable.icon)
                        .SetOngoing(true)
                        .SetPriority((int)NotificationPriority.Low);
                    n = builder.Build();
                }
                else
                {
                    var builder = new NotificationCompat.Builder(this)
                        .SetContentTitle(L10n.T("本机正在被远控"))
                        .SetContentText(L10n.T("点此可随时停止被控"))
                        .SetSmallIcon(Resource.Drawable.icon)
                        .SetOngoing(true);
                    n = builder.Build();
                }
                // 点通知直接回到被控设置页
                var pi = PendingIntent.GetActivity(this, 0,
                    new Intent(this, typeof(RemoteHostActivity)).AddFlags(ActivityFlags.SingleTop),
                    PendingIntentFlags.UpdateCurrent);
                n.ContentIntent = pi;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                    StartForeground(NotificationId, n, ForegroundService.TypeMediaProjection);
                else
                    StartForeground(NotificationId, n);
                Log.Warn(Tag, "前台服务已启动（mediaProjection）");
                return true;
            }
            catch (Exception ex) { Log.Warn(Tag, "前台服务启动失败：" + ex.Message); return false; }
        }

        private void EnsureChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var mgr = (NotificationManager)GetSystemService(NotificationService);
                if (mgr == null || mgr.GetNotificationChannel(ChannelId) != null) return;
                var ch = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
                {
                    Description = L10n.T("允许局域网内的设备远程控制本机")
                };
                ch.SetShowBadge(false);
                ch.EnableVibration(false);
                ch.SetSound(null, null);
                mgr.CreateNotificationChannel(ch);
            }
            catch (Exception ex) { Log.Warn(Tag, "创建通知渠道失败：" + ex.Message); }
        }

        /* ---------- 静态启停 ---------- */

        public static void RequestStop(Context ctx)
        {
            try
            {
                var i = new Intent(ctx, typeof(RdHostService));
                i.SetAction(ActionStop);
                ctx.StartService(i);
            }
            catch { }
            try { RdHostServer.Instance.Stop(); } catch { }
        }
    }
}

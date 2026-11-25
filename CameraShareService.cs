using System;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using AndroidX.Core.App;

namespace AhoChatA
{
    /// <summary>
    /// 摄像头共享的前台服务壳（与屏幕共享的 ScreenCastService 同思路；屏幕共享需要 mediaProjection
    /// 类型，摄像头用的是 camera 类型）。
    ///
    /// 作用：
    ///  1) 摄像头共享原本只跑在 App 进程内的后台线程，没有任何前台组件 —— 一旦退出「IP 摄像头」
    ///     界面、进程被系统回收（或 Android 10+ 在后台收回相机），共享就中断。这里把它提升为
    ///     前台服务，退出界面后依然常驻、并能从通知栏一键停止。
    ///  2) 仅持有 Partial WakeLock 保 CPU（不点亮屏幕），锁屏后编码/推流继续。
    ///  3) 通知栏提供"停止共享"，对应用户想结束共享的场景。
    ///
    /// 真正的编码 / HTTP / 信标仍由 IpCameraServer 负责，这里只是宿主。
    /// </summary>
    [Service(Exported = false, Name = "com.ztwl.CameraShareService")]
    public class CameraShareService : Service
    {
        public const string ActionStop = "com.Ahochat.android.CAMERASHARE_STOP";

        private const string ChannelId = "Mchat_camera";
        private const string ChannelName = "摄像头共享";
        private const int NotificationId = 2012;

        private PowerManager.WakeLock _wl;

        public override IBinder OnBind(Intent intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            EnsureChannel();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent != null && intent.Action == ActionStop)
            {
                try { IpCameraServer.Instance.Stop(); } catch { }
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            // 【为什么】StartForeground 失败不再静默吞掉：明确日志，便于定位
            // 「后台被回收 / 相机被系统收走」这类「半天出不来画面」的问题。
            if (!StartForegroundCompat())
                Log.Error("CameraShare", "前台服务(camera)启动失败，摄像头共享可能被系统回收；请检查 FOREGROUND_SERVICE_CAMERA 权限");
            ScreenShareSettings.Instance(this).LogEffective("CameraShareService.OnStartCommand");
            AcquireWakeLock();
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            try
            {
                var srv = IpCameraServer.Instance;
                if (srv.IsRunning && srv.Source == ShareSource.Camera) srv.Stop();
            }
            catch { }
            ReleaseWakeLock();
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                    StopForeground(StopForegroundFlags.Remove);
            }
            catch { }
            base.OnDestroy();
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
                    Description = L10n.T("共享本机摄像头到局域网")
                };
                ch.SetShowBadge(false);
                ch.EnableVibration(false);
                ch.SetSound(null, null);
                mgr.CreateNotificationChannel(ch);
            }
            catch (Exception ex) { Log.Error("CameraShare", "创建通知渠道失败: " + ex.Message); }
        }

        private bool StartForegroundCompat()
        {
            try
            {
                var n = BuildNotification();
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                    StartForeground(NotificationId, n, ForegroundService.TypeCamera);
                else
                    StartForeground(NotificationId, n);
                Log.Warn("CameraShare", "前台服务已启动（camera）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("CameraShare", "StartForeground 失败: " + ex.Message);
                try { StartForeground(NotificationId, BuildNotification()); return true; } catch { return false; }
            }
        }

        private Notification BuildNotification()
        {
            var stop = new Intent(this, typeof(CameraShareService));
            stop.SetAction(ActionStop);
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;
            var pi = PendingIntent.GetService(this, 2012, stop, flags);

            var content = new Intent(this, typeof(IpCameraActivity));
            content.AddFlags(ActivityFlags.SingleTop);
            var contentPi = PendingIntent.GetActivity(this, 2013, content, flags);

            return new NotificationCompat.Builder(this, ChannelId)
                .SetSmallIcon(Resource.Drawable.icon)
                .SetContentTitle(L10n.T("摄像头共享中"))
                .SetContentText(L10n.T("本机摄像头已共享到局域网 · 点「停止共享」结束"))
                .SetOngoing(true)
                .SetPriority((int)NotificationPriority.Low)
                .SetContentIntent(contentPi)
                .AddAction(0, L10n.T("停止共享"), pi)
                .Build();
        }

        /* ---------- 后台保活（只锁 CPU，不点屏幕） ---------- */

        private void AcquireWakeLock()
        {
            try
            {
                if (_wl == null)
                {
                    var pm = (PowerManager)GetSystemService(PowerService);
                    if (pm == null) return;
#pragma warning disable CS0618
                    _wl = pm.NewWakeLock(WakeLockFlags.Partial, "lancam:camcpu");
#pragma warning restore CS0618
                    _wl.SetReferenceCounted(false);
                }
                if (!_wl.IsHeld) _wl.Acquire();
            }
            catch (Exception ex) { Log.Warn("CameraShare", "WakeLock 失败: " + ex.Message); }
        }

        private void ReleaseWakeLock()
        {
            try { if (_wl != null && _wl.IsHeld) _wl.Release(); } catch { }
            try { _wl = null; } catch { }
        }
    }
}

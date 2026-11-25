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
    /// 屏幕共享的前台服务壳。
    ///
    /// 作用：
    ///  1) Android 10 (Q) 起强制要求：调用 MediaProjectionManager.GetMediaProjection() 前，
    ///     进程里必须已经有一个 foregroundServiceType 含 mediaProjection 的前台服务，
    ///     否则系统直接拒绝（抛 SecurityException 或返回 null）。所以授权结果先送到这里，
    ///     由服务 StartForeground 之后再真正开采集。
    ///  2) 屏幕共享期间进程退到后台也不被回收（只持有 Partial WakeLock 保 CPU，
    ///     **不点亮屏幕、也不会把已息屏的屏幕唤醒** —— 用户想息屏就让它息屏）。
    ///  3) 通知栏提供"停止共享"，对应用户在系统里收回共享的场景。
    ///
    /// 真正的编码 / HTTP / 信标仍然由 IpCameraServer 负责，这里只是宿主。
    /// </summary>
    [Service(Exported = false, Name = "com.ztwl.ScreenCastService")]
    public class ScreenCastService : Service
    {
        public const string ActionStart = "com.Ahochat.android.SCREENCAST_START";
        public const string ActionStop = "com.Ahochat.android.SCREENCAST_STOP";
        /// <summary>用进程内备份的授权结果重试（token 未被消费时有效；已消费则需重新授权）。</summary>
        public const string ActionRetry = "com.Ahochat.android.SCREENCAST_RETRY";

        /// <summary>启动结果广播（Activity 收到后提示用户成功/失败原因）。</summary>
        public const string ActionResult = "com.Ahochat.android.SCREENCAST_RESULT";

        public const string ExtraResultCode = "rc";
        public const string ExtraData = "data";
        public const string ExtraShort = "short";
        public const string ExtraFps = "fps";
        public const string ExtraBitrate = "br";
        public const string ExtraOk = "ok";
        public const string ExtraMsg = "msg";

        private const string ChannelId = "Mchat_screen";
        private const string ChannelName = "屏幕共享";
        private const int NotificationId = 2011;

        private PowerManager.WakeLock _wl;

        /// <summary>
        /// 一次性授权 token 的进程内备份。
        /// 【为什么】MediaProjection 的授权 Intent 只能被 GetMediaProjection 消费一次；
        /// 若前台服务还没起来就失败（或 Service 被重建），Intent 里的 token 会丢失，
        /// 用户只能重新弹窗授权。这里在 Activity 侧先备份一份，供未消费前的重试使用。
        /// </summary>
        private static readonly object _pendingLock = new object();
        private static int _pendingRc;
        private static Intent _pendingData;
        private static int _pendingShort, _pendingFps, _pendingBr;

        /// <summary>在 StartForegroundService 之前由 Activity 保存授权结果（正确保存/复用）。</summary>
        public static void SetPendingAuth(int rc, Intent data, int shortSide, int fps, int br)
        {
            lock (_pendingLock)
            {
                _pendingRc = rc; _pendingData = data;
                _pendingShort = shortSide; _pendingFps = fps; _pendingBr = br;
            }
        }

        /// <summary>取出并清除待消费授权（取出即视为即将消费，避免同一 token 被用两次）。</summary>
        private static bool TakePendingAuth(out int rc, out Intent data, out int shortSide, out int fps, out int br)
        {
            lock (_pendingLock)
            {
                rc = _pendingRc; data = _pendingData;
                shortSide = _pendingShort; fps = _pendingFps; br = _pendingBr;
                _pendingRc = 0; _pendingData = null;
                return data != null && rc == (int)Result.Ok;
            }
        }

        private static void ClearPendingAuth()
        {
            lock (_pendingLock) { _pendingRc = 0; _pendingData = null; }
        }

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
                ClearPendingAuth();
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            // 【为什么 · 先 FGS 再拿投影 + 时序校验】Android 10+ 要求 GetMediaProjection 前
            // 进程里已有一个 foregroundServiceType=mediaProjection 的前台服务；旧代码
            // StartForeground 失败被静默 catch 降级后仍继续 StartScreen，结果只会拿到一句
            // 模糊的「授权无效」。这里必须确认 StartForeground 真成功，否则不消耗 token。
            if (!StartForegroundCompat())
            {
                Log.Error("ScreenCast", "前台服务(StartForeground)失败，未消耗授权 token；可重试或重新授权");
                Report(false, L10n.T("前台服务启动失败（需 mediaProjection 前台服务权限），请重新点开始共享授权"));
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
            // 硬编码 720 / 30fps 改读集中设置（保留 bitrate 合理兜底）。
            int shortSide = intent.GetIntExtra(ExtraShort, ScreenShareSettings.FallbackShortSide);
            int fps = intent.GetIntExtra(ExtraFps, s.MaxFps);
            int br = intent.GetIntExtra(ExtraBitrate, 4_000_000);
            s.LogEffective("ScreenCastService.OnStartCommand");

            // Intent 没带（如 ActionRetry）就退回进程内备份的授权结果。
            if (data == null || rc != (int)Result.Ok)
            {
                int prc, pshort, pfps, pbr; Intent pdata;
                if (TakePendingAuth(out prc, out pdata, out pshort, out pfps, out pbr))
                {
                    rc = prc; data = pdata;
                    shortSide = pshort; fps = pfps; br = pbr;
                    Log.Warn("ScreenCast", "用备份授权结果重试启动（token 未被消费）");
                }
            }
            else
            {
                // 正确保存：把本次授权结果备份，供 FGS 失败前/Service 重建时的可重试路径使用。
                SetPendingAuth(rc, data, shortSide, fps, br);
            }

            if (data == null || rc != (int)Result.Ok)
            {
                Log.Error("ScreenCast", "屏幕录制授权无效 rc=" + rc + " data=" + (data != null));
                Report(false, L10n.T("没有有效的屏幕录制授权（授权 token 一次性），请重新点开始共享授权"));
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            AcquireWakeLock();

            new Thread(() =>
            {
                bool ok = false;
                string msg = null;
                try
                {
                    // token 一旦被 GetMediaProjection 消费即作废，成功/失败后都不再复用。
                    ClearPendingAuth();
                    ok = IpCameraServer.Instance.StartScreen(this, rc, data, shortSide, fps, br);
                    if (!ok) msg = IpCameraServer.Instance.LastError;
                }
                catch (Exception ex)
                {
                    // 异常不再全吞：写明确日志，让上层知道是可重试/需重新授权。
                    msg = L10n.T("启动屏幕共享异常：") + ex.GetType().Name + " - " + ex.Message;
                    Log.Error("ScreenCast", msg);
                }

                if (!ok) Log.Error("ScreenCast", "屏幕共享启动失败：" + (msg ?? "未知原因") + "（token 已消耗，需重新授权后重试）");
                Report(ok, msg);
                if (!ok) { try { StopSelf(); } catch { } }
            })
            { IsBackground = true, Name = "screen-start" }.Start();

            return StartCommandResult.NotSticky;
        }

        public override void OnDestroy()
        {
            ClearPendingAuth();
            try
            {
                var srv = IpCameraServer.Instance;
                if (srv.IsRunning && srv.Source == ShareSource.Screen) srv.Stop();
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

        private void Report(bool ok, string msg)
        {
            Log.Warn("ScreenCast", "屏幕共享启动结果 ok=" + ok + " msg=" + (msg ?? ""));
            // 同进程静态事件直接通知 UI 页（不依赖动态广播注册，避开 Android 13+ 的 RECEIVER_* 限制）
            try { IpCameraServer.NotifyScreenStartResult(ok, msg); } catch { }
            try
            {
                var r = new Intent(ActionResult);
                r.SetPackage(PackageName);
                r.PutExtra(ExtraOk, ok);
                r.PutExtra(ExtraMsg, msg ?? "");
                SendBroadcast(r);
            }
            catch { }
        }

        /* ---------- 通知 ---------- */

        private void EnsureChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var mgr = (NotificationManager)GetSystemService(NotificationService);
                if (mgr == null || mgr.GetNotificationChannel(ChannelId) != null) return;
                var ch = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
                {
                    Description = L10n.T("共享本机屏幕到局域网")
                };
                ch.SetShowBadge(false);
                ch.EnableVibration(false);
                ch.SetSound(null, null);
                mgr.CreateNotificationChannel(ch);
            }
            catch (Exception ex) { Log.Error("ScreenCast", "创建通知渠道失败: " + ex.Message); }
        }

        private bool StartForegroundCompat()
        {
            try
            {
                var n = BuildNotification();
                // Q+ 必须带 mediaProjection 类型，否则 GetMediaProjection 会被系统直接拒绝。
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                    StartForeground(NotificationId, n, ForegroundService.TypeMediaProjection);
                else
                    StartForeground(NotificationId, n);
                Log.Warn("ScreenCast", "前台服务已启动（mediaProjection）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("ScreenCast", "StartForeground 失败: " + ex.Message);
                // Q+ 上「无类型」的降级调用拿不到 MediaProjection，明确判失败；
                // 旧版本（<Q）无此要求，降级调用仍可用。
                if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
                {
                    try { StartForeground(NotificationId, BuildNotification()); return true; } catch { }
                }
                return false;
            }
        }

        private Notification BuildNotification()
        {
            var stop = new Intent(this, typeof(ScreenCastService));
            stop.SetAction(ActionStop);
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;
            var pi = PendingIntent.GetService(this, 2011, stop, flags);

            return new NotificationCompat.Builder(this, ChannelId)
                .SetSmallIcon(Resource.Drawable.icon)
                .SetContentTitle(L10n.T("屏幕共享中"))
                .SetContentText(L10n.T("正在把本机屏幕共享到局域网"))
                .SetOngoing(true)
                .SetPriority((int)NotificationPriority.Low)
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
                    // 曾经用 ScreenBright：屏幕共享期间强行让屏幕常亮，用户按电源键息屏
                    // 还会被立刻点亮，看起来像「一遍遍自己唤醒」。
                    // 现在改成 Partial：只保证编码/推流所需的 CPU 不休眠，
                    // **不点亮屏幕、也不会把已息屏的屏幕唤醒**；用户想息屏就让它息屏。
                    _wl = pm.NewWakeLock(WakeLockFlags.Partial, "lancam:cpu");
#pragma warning restore CS0618
                    _wl.SetReferenceCounted(false);
                }
                if (!_wl.IsHeld) _wl.Acquire();
            }
            catch (Exception ex) { Log.Warn("ScreenCast", "WakeLock 失败: " + ex.Message); }
        }

        private void ReleaseWakeLock()
        {
            try { if (_wl != null && _wl.IsHeld) _wl.Release(); } catch { }
            try { _wl = null; } catch { }
        }
    }
}

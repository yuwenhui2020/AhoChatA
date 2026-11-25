using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;

namespace AhoChatA
{
    /// <summary>
    /// 自启动 / 保活助手：集中处理"忽略电池优化"白名单与国产 ROM 自启动设置页跳转。
    ///
    /// 背景：MIUI / ColorOS / EMUI 等 ROM 会把 App 加入省电冻结名单，
    /// 即使声明了 BOOT_COMPLETED、前台服务也照杀，导致开机后或锁屏一段时间后收不到消息、
    /// 弹不出横幅。唯一可靠的办法是引导用户手动把本 App 加入白名单。
    /// 这里按机型给出设置页跳转（能直达就直达，否则退回应用详情），用户勾选后即长期保活。
    /// </summary>
    public static class AutoStartHelper
    {
        /// <summary>是否已加入"忽略电池优化"白名单（Doze 白名单）。</summary>
        public static bool IsIgnoringBatteryOptimizations(Context ctx)
        {
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.M) return true;
                var pm = ctx.GetSystemService(Context.PowerService) as PowerManager;
                return pm != null && pm.IsIgnoringBatteryOptimizations(ctx.PackageName);
            }
            catch { return false; }
        }

        /// <summary>
        /// 引导用户忽略电池优化。优先弹系统标准对话框（REQUEST_IGNORE_BATTERY_OPTIMIZATIONS），
        /// 失败则退回 Doze 白名单设置页。
        /// </summary>
        public static void RequestIgnoreBatteryOptimizations(Activity activity)
        {
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.M) return;
                var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                intent.AddFlags(ActivityFlags.NewTask);
                activity.StartActivity(intent);
            }
            catch
            {
                try
                {
                    var i = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
                    i.AddFlags(ActivityFlags.NewTask);
                    activity.StartActivity(i);
                }
                catch { }
            }
        }

        /// <summary>
        /// 打开厂商"自启动管理"设置页。不同 ROM 的 action 不同，逐个尝试，
        /// 全部失败时退回应用详情页（用户仍可在里面找"自启动/省电"开关）。
        /// </summary>
        public static void OpenAutoStartSettings(Activity activity)
        {
            var tried = new List<string>();
            // 常见国产 ROM 的自启动管理页
            string[][] candidates = {
                new[] { "com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity" },
                new[] { "com.miui.securitycenter", "com.miui.powercenter.PowerSettings" },
                new[] { "com.coloros.safecenter", "com.coloros.safecenter.permission.startup.StartupAppListActivity" },
                new[] { "com.coloros.safecenter", "com.coloros.safecenter.startupapp.StartupAppListActivity" },
                new[] { "com.oppo.safe", "com.oppo.safe.permission.startup.StartupAppListActivity" },
                new[] { "com.htc.oneui", "com.htc.oneui.settings.AutoStartSettings" },
                new[] { "com.samsung.android.lool", "com.samsung.android.sm.ui.battery.BatteryActivity" },
                new[] { "com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity" },
                new[] { "com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity" },
                new[] { "com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.BgStartUpManagerActivity" },
                new[] { "com.android.settings", "com.android.settings.BackgroundDataSettings" }
            };
            foreach (var c in candidates)
            {
                var pkg = c[0];
                var cls = c[1];
                if (tried.Contains(pkg)) continue;
                tried.Add(pkg);
                try
                {
                    var i = new Intent();
                    i.SetComponent(new ComponentName(pkg, cls));
                    i.AddFlags(ActivityFlags.NewTask);
                    activity.StartActivity(i);
                    return;
                }
                catch { }
            }
            // 兜底：应用详情页
            try
            {
                var i = new Intent(Settings.ActionApplicationDetailsSettings);
                i.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                i.AddFlags(ActivityFlags.NewTask);
                activity.StartActivity(i);
            }
            catch { }
        }

        /// <summary>
        /// 一条龙引导：先请求忽略电池优化，再尝试打开 ROM 自启动页。
        /// 用 Handler 延迟打开第二个页面，避免两个 Activity 同时启动被系统合并/拦截。
        /// </summary>
        public static void GuideAutoStart(Activity activity)
        {
            try
            {
                if (!IsIgnoringBatteryOptimizations(activity))
                    RequestIgnoreBatteryOptimizations(activity);
                new Handler(Looper.MainLooper).PostDelayed(() =>
                {
                    try { OpenAutoStartSettings(activity); } catch { }
                }, 900);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("AutoStart", "引导自启动设置失败: " + ex.Message);
            }
        }
    }
}

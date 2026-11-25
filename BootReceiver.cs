using System;
using Android.App;
using Android.Content;

namespace AhoChatA
{
    /// <summary>
    /// 开机自启：系统开机（或应用被清理后重启）时，把后台栈重新拉起来，
    /// 这样即使没打开 App 也能继续接收消息。
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[]
    {
        Intent.ActionBootCompleted,                              // android.intent.action.BOOT_COMPLETED
        "android.intent.action.QUICKBOOT_POWERON",                // HTC / 通用快速开机
        "com.htc.intent.action.QUICKBOOT_POWERON",                // HTC 私有
        "android.intent.action.LOCKED_BOOT_COMPLETED",            // 加密设备解锁后开机
        "android.intent.action.USER_INITIALIZE",                  // 首次创建用户
        "android.intent.action.MY_PACKAGE_REPLACED",              // 自身升级后立即恢复后台
        "android.intent.action.PACKAGE_REPLACED"                  // 部分 ROM 升级广播（带包名 data）
    })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try
            {
                // PACKAGE_REPLACED 是全局广播，只处理本包，避免别的 App 升级也触发
                var action = intent?.Action;
                if (action == Intent.ActionPackageReplaced)
                {
                    var pkg = intent.Data?.SchemeSpecificPart;
                    if (!string.IsNullOrEmpty(pkg) && pkg != context.PackageName) return;
                }
                CoreHost.Preinit(context);
                CoreHost.EnsureBackground(context);
                try { ChatForegroundService.Start(context); } catch { }
                try
                {
                    var dns = DnsCore.DnsManager.Instance;
                    dns.Initialize(context);
                    // Never launch consent UI from a boot broadcast.
                    if (dns.AutoStart && dns.GetConfig().Enabled && !dns.RequiresVpnConsent) dns.StartVpn();
                }
                catch (Exception dnsError) { Android.Util.Log.Warn("Boot", "DNS 自启失败: " + dnsError.Message); }
                Android.Util.Log.Info("Boot", "开机/升级自启：后台已拉起 action=" + action);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Boot", "开机自启失败: " + ex.Message);
            }
        }
    }
}

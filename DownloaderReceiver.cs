using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using AhoChatA.Vod;

namespace AhoChatA
{
    /// <summary>
    /// 下载广播接收器：处理外部应用分享链接/打开方式的 Intent。
    /// 支持 ACTION_SEND (text/plain)、ACTION_VIEW (http/https)、自定义 mchat://download scheme。
    /// 收到 Intent 后解析 URL，调用 DownloaderService 入队，并启动 DownloadActivity 显示进度。
    /// </summary>
    [BroadcastReceiver(
        Name = "com.Ahochat.android.DownloaderReceiver",
        Exported = true,
        Enabled = true,
        Label = "AhoChat Download Receiver")]
    [IntentFilter(new[] { Intent.ActionSend, Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataSchemes = new[] { "http", "https" },
        DataMimeTypes = new[] { "text/plain", "video/*", "audio/*", "application/*" })]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "mchat",
        DataHost = "download")]
    public class DownloaderReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try
            {
                Log.Info("MchatDownloader", "收到外部 Intent: " + intent.Action + ", Type: " + intent.Type + ", Data: " + intent.Data);
                
                // 必须在 UI 线程处理（启动 Activity、Toast）
                if (context is Activity act)
                {
                    HandleIntentOnUiThread(act, intent);
                }
                else
                {
                    // 非 Activity Context（如从系统分享菜单触发），包装到新任务栈
                    var wrapped = new Intent(context, typeof(DownloaderEntryActivity));
                    wrapped.SetAction(intent.Action);
                    wrapped.PutExtras(intent);
                    wrapped.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(wrapped);
                }
            }
            catch (Exception ex)
            {
                Log.Error("MchatDownloader", "OnReceive 失败: " + ex.Message);
            }
        }

        private void HandleIntentOnUiThread(Activity act, Intent intent)
        {
            try
            {
                var taskId = DownloaderService.HandleIntent(act, intent);
                if (!string.IsNullOrEmpty(taskId))
                {
                    // 任务已入队，DownloadActivity 会自动打开（HandleIntent 内部已启动）
                }
            }
            catch (Exception ex)
            {
                Log.Error("MchatDownloader", "UI 线程处理失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 透明入口 Activity：接收 DownloaderReceiver 转发的 Intent，
    /// 在 UI 线程调用 DownloaderService.HandleIntent，然后自动关闭。
    /// 避免在 BroadcastReceiver 中直接做 UI 操作导致 ANR。
    /// </summary>
    [Activity(
        Name = "com.Ahochat.android.DownloaderEntryActivity",
        Theme = "@android:style/Theme.Translucent.NoTitleBar",
        Exported = true,
        NoHistory = true,
        LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize)]
    public class DownloaderEntryActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // 不需要 setContentView，完全透明
            try
            {
                DownloaderService.HandleIntent(this, Intent);
            }
            finally
            {
                Finish();
            }
        }

        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            try
            {
                DownloaderService.HandleIntent(this, intent);
            }
            finally
            {
                Finish();
            }
        }
    }
}
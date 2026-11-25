using System.Threading;
using Android.App;
using Android.Content;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 调试/远程触发入口：发送广播即可在后台拉起 IP 摄像头共享（默认 720p 后置）。
    /// 用途：真机自测、以及为 PC 端/自动化预留“远程启动共享”的控制面。
    /// 仅响应特定 action；调试构建可用，正式发布前可移除或加权限保护。
    /// </summary>
    [BroadcastReceiver(Exported = true)]
    [IntentFilter(new[] { "com.Ahochat.android.IPCAM_TEST" })]
    public class CameraTestReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            Log.Warn("IpCam", "TEST receiver 收到广播 action=" + (intent?.Action ?? "?"));
            var ctx = context?.ApplicationContext ?? context;
            if (ctx == null) { Log.Warn("IpCam", "TEST ctx 为空"); return; }
            new Thread(() =>
            {
                Log.Warn("IpCam", "TEST 后台线程启动，调用 IpCameraServer.Start");
                bool ok = IpCameraServer.Instance.Start(ctx, false, 1280, 720, 30, 4_000_000);
                Log.Warn("IpCam", "TEST 触发启动结果=" + (ok ? "成功" : "失败:" + (IpCameraServer.Instance.LastError ?? "未知")));
            }) { IsBackground = true }.Start();
        }
    }
}

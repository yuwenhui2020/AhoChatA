using System;
using System.Text;
using Android.App;
using Android.Content;

namespace AhoChatA
{
    /// <summary>
    /// 短信转发：收到系统短信后把**全文**自动发到 Mchat 频道。
    ///
    /// 为什么用静态广播接收器：手机在后台时也要能转发，所以由系统在收到 SMS 时直接拉起本接收器
    /// （不依赖 Activity/服务存活）。真正的转发逻辑在 <see cref="SmsForwarder"/>：
    /// 桥没起来时先暂存，等桥启动后补发 —— 老实现直接调用桥、桥为 null 就丢弃，
    /// 这正是「明明收到了短信、频道里却没有」的根因。
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true, Name = "com.ztwl.SmsReceiver")]
    public sealed class SmsReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try
            {
                var action = intent?.Action ?? "";
                Android.Util.Log.Info("SmsForward", "收到广播 action=" + action);

                if (intent == null || action != "android.provider.Telephony.SMS_RECEIVED") return;

                var parts = Android.Provider.Telephony.Sms.Intents.GetMessagesFromIntent(intent);
                if (parts == null || parts.Length == 0)
                {
                    Android.Util.Log.Warn("SmsForward", "短信广播里没有 PDU，忽略");
                    return;
                }

                var sb = new StringBuilder();
                string sender = null;
                foreach (var p in parts)
                {
                    if (p == null) continue;
                    if (string.IsNullOrEmpty(sender)) sender = p.OriginatingAddress;
                    var body = p.DisplayMessageBody;
                    if (body != null) sb.Append(body);
                }

                var text = sb.ToString().Trim();
                if (text.Length == 0) return;

                // 先按「广播已收到」记一笔，再交给转发中枢（去重/暂存/补发都由它负责）
                Android.Util.Log.Info("SmsForward", "收到短信，长度 " + text.Length + "，来自 " + (sender ?? "?"));

                var ctx = context?.ApplicationContext ?? context;
                SmsForwarder.Handle(ctx, sender, text, "broadcast");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("SmsForward", "处理短信广播失败: " + ex.Message);
            }
        }
    }
}

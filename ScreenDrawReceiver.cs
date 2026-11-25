using System;
using Android.Content;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 屏幕画笔开关广播：am broadcast -a com.Ahochat.android.action.SCREEN_DRAW_TOGGLE 即可切换。
    /// </summary>
    [BroadcastReceiver(Exported = false)]
    [Android.App.IntentFilterAttribute(new[] { ScreenDrawService.ActionToggle })]
    public class ScreenDrawReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            try
            {
                ScreenDrawService.Toggle(context);
            }
            catch (Exception ex)
            {
                Log.Error("ScreenDraw", $"广播切换屏幕画笔失败: {ex.Message}");
            }
        }
    }
}

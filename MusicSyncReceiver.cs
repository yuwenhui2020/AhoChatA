// 通知栏「一起播放」动作的广播接收器：取出主播信息后交给 MusicSync.Join 对齐播放。
using Android.Content;
using AhoChatA;

namespace AhoChatA
{
    [BroadcastReceiver(Exported = false)]
    [Android.App.IntentFilterAttribute(new[] { "com.Ahochat.android.musicsync.JOIN" })]
    public class MusicSyncReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != "com.Ahochat.android.musicsync.JOIN") return;
            var ip = intent.GetStringExtra("ip");
            int web = intent.GetIntExtra("web", 7790);
            var path = intent.GetStringExtra("path");
            var title = intent.GetStringExtra("title");
            var artist = intent.GetStringExtra("artist");
            var name = intent.GetStringExtra("name");
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(path)) return;
            MusicSync.Join(ip, web, path, title, artist, name);
        }
    }
}

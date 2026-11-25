using System;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 共享观看页：点频道里的「共享卡片」后在本 App 内<b>全屏</b>观看对方共享的内容。
    ///
    /// 和用浏览器打开的区别：
    ///   · 屏幕画笔：优先走 App 专属加速通道（ShareCast，UDP 直传），
    ///     收不到再自动回退到 HTTP 抓帧，保证一定能看到；
    ///   · 摄像头 / 屏幕共享：直接用系统播放器硬解 live.ts（H.264），
    ///     不走浏览器那套 MJPEG / MSE，延迟和占用都更好。
    /// </summary>
    [Activity(Label = "@string/act_shareview",
              Exported = false,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
              Theme = "@style/AppTheme.NoActionBar")]
    public class ShareViewActivity : BaseActivity
    {
        private string type, url, host, title;
        private ImageView img;
        private VideoView video;
        private TextView statusTxt;
        private ShareCastClient cast;
        private Handler handler;
        private bool alive;
        private bool udpOk;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            try { Window.AddFlags(WindowManagerFlags.Fullscreen); } catch { }
            try { Window.AddFlags(WindowManagerFlags.KeepScreenOn); } catch { }   // 看共享时别息屏
            try { ActionBar?.Hide(); } catch { }

            type = Intent.GetStringExtra("type") ?? "draw";
            url = Intent.GetStringExtra("url") ?? "";
            host = Intent.GetStringExtra("host") ?? "";
            title = Intent.GetStringExtra("title") ?? L10n.T("共享");

            handler = new Handler(Looper.MainLooper);
            alive = true;

            BuildUi();
            if (type == "draw") StartDraw();
            else StartVideo();
        }

        private int Dp(int v) => IosStyle.Dp(this, v);

        private void BuildUi()
        {
            var root = new FrameLayout(this);
            root.SetBackgroundColor(Color.Black);

            if (type == "draw")
            {
                img = new ImageView(this);
                img.SetScaleType(ImageView.ScaleType.FitCenter);
                root.AddView(img, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            }
            else
            {
                video = new VideoView(this);
                root.AddView(video, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                { Gravity = GravityFlags.Center });
            }

            // 顶部信息条（标题 / 通道状态 / 关闭）
            var bar = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            bar.SetBackgroundColor(Color.Argb(170, 0, 0, 0));
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetPadding(Dp(16), 0, Dp(16), 0);
            bar.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(56), GravityFlags.Top);

            var t = new TextView(this) { Text = title, TextSize = 17 };
            t.SetTextColor(Color.White);
            t.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            statusTxt = new TextView(this) { Text = L10n.T("连接中…"), TextSize = 13 };
            statusTxt.SetTextColor(Color.ParseColor("#0A84FF"));
            statusTxt.SetPadding(Dp(10), 0, Dp(10), 0);

            var close = new TextView(this) { Text = L10n.T("关闭"), TextSize = 14, Gravity = GravityFlags.Center };
            close.SetTextColor(Color.White);
            close.SetMinWidth(Dp(44));
            close.SetMinHeight(Dp(44));
            close.Click += (s, e) => Finish();

            bar.AddView(t);
            bar.AddView(statusTxt);
            bar.AddView(close);
            root.AddView(bar);

            SetContentView(root);
        }

        /* ===================== 屏幕画笔 ===================== */

        private void StartDraw()
        {
            // ① 先试 App 专属加速通道（UDP 直传）
            if (!string.IsNullOrEmpty(host))
            {
                cast = new ShareCastClient();
                cast.OnFrame = bytes => RunOnUiThread(() => { udpOk = true; Show(bytes); });
                if (cast.Start(host)) handler.PostDelayed(KeepAliveTick, 8000);
            }

            // ② 兜底：加速通道 3 秒内没画面就改 HTTP 抓帧
            handler.PostDelayed(() => { if (alive && !udpOk) PollSnapshot(); }, 3000);
        }

        private void KeepAliveTick()
        {
            if (!alive) return;
            try { cast?.KeepAlive(); } catch { }
            handler.PostDelayed(KeepAliveTick, 8000);
        }

        private void Show(byte[] jpg)
        {
            try
            {
                if (img == null || jpg == null || jpg.Length == 0) return;
                var bmp = BitmapFactory.DecodeByteArray(jpg, 0, jpg.Length);
                if (bmp == null) return;
                img.SetImageBitmap(bmp);
                if (statusTxt != null && udpOk) statusTxt.Text = L10n.T("加速通道");
                else if (statusTxt != null) statusTxt.Text = "HTTP";
            }
            catch { }
        }

        private void PollSnapshot()
        {
            if (!alive) return;
            new Thread(() =>
            {
                try
                {
                    using (var wc = new System.Net.WebClient())
                    {
                        // 自己是 Mchat 客户端：带上这个头，对端开了双因素认证也不会拦自己
                        try { wc.Headers["X-Mchat-App"] = "1"; } catch { }
                        var bytes = wc.DownloadData(SnapshotUrl());
                        if (bytes != null && bytes.Length > 0 && !udpOk)
                            RunOnUiThread(() => Show(bytes));
                    }
                }
                catch { }
                finally
                {
                    if (alive)
                        handler.PostDelayed(() => { if (alive && !udpOk) PollSnapshot(); }, 1200);
                }
            }) { IsBackground = true }.Start();
        }

        private string SnapshotUrl()
        {
            var u = (url ?? "").TrimEnd('/');
            return u + "/snapshot";
        }

        /* ===================== 摄像头 / 屏幕共享 ===================== */

        private void StartVideo()
        {
            try
            {
                if (video == null) return;
                var uri = Android.Net.Uri.Parse(VideoUrl());
                video.SetVideoURI(uri);
                video.SetOnPreparedListener(new PrepListener(() =>
                {
                    try { video.Start(); } catch { }
                    if (statusTxt != null) statusTxt.Text = L10n.T("App 内硬解");
                }));
                video.SetOnErrorListener(new ErrListener(() =>
                {
                    if (statusTxt != null) statusTxt.Text = L10n.T("播放失败");
                }));
            }
            catch (Exception ex)
            {
                Log.Warn("Mchat", "共享观看启动失败：" + ex.Message);
                if (statusTxt != null) statusTxt.Text = L10n.T("播放失败");
            }
        }

        private string VideoUrl()
        {
            // live.ts 是 H.264 TS 流：App 内直接硬解，比浏览器那套 MJPEG 省得多
            var u = (url ?? "").TrimEnd('/');
            return u + "/live.ts";
        }

        private sealed class PrepListener : Java.Lang.Object, MediaPlayer.IOnPreparedListener
        {
            private readonly Action _a;
            public PrepListener(Action a) { _a = a; }
            public void OnPrepared(MediaPlayer mp) { try { _a?.Invoke(); } catch { } }
        }

        private sealed class ErrListener : Java.Lang.Object, MediaPlayer.IOnErrorListener
        {
            private readonly Action _a;
            public ErrListener(Action a) { _a = a; }
            public bool OnError(MediaPlayer mp, MediaError what, int extra)
            {
                try { _a?.Invoke(); } catch { }
                return true;
            }
        }

        protected override void OnDestroy()
        {
            alive = false;
            try { cast?.Dispose(); } catch { }
            cast = null;
            try { video?.StopPlayback(); } catch { }
            base.OnDestroy();
        }
    }
}

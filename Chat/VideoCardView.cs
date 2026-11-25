// ============================================================
// 聊天里的「视频分享卡片」：瀑布墙封面 + 标题 + 右下角「观看」「一起看」。
//
//   · 观看   → 外源 CDN 真实 m3u8/mp4（或本地视频的 Range 网关流），各播各的；
//   · 一起看 → 宿主机播放代理地址 + 会话 id，VideoActivity 轮询宿主机
//              /api/media/vod/watchtogether，按 ts/seq 时间戳校验后跟随进度。
// 封面来自外源 CDN 海报，优先经宿主机 /api/media/vod/poster 复用其缓存。
// ============================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AhoChatA.Vod;
using Orientation = Android.Widget.Orientation;

namespace AhoChatA
{
    public class VideoCardView : LinearLayout
    {
        private readonly Activity _act;
        private readonly IDictionary<string, object> _msg;

        public VideoCardView(Activity act, IDictionary<string, object> msg) : base(act)
        {
            _act = act;
            _msg = msg;
            Orientation = Orientation.Vertical;
            var bg = new Android.Graphics.Drawables.GradientDrawable();
            bg.SetColor(unchecked((int)0xFFFFFFFF));
            bg.SetCornerRadius(Dp(12));
            bg.SetStroke(1, new Color(unchecked((int)0xFFE8EAEE)));
            Background = bg;
            SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
            Build();
        }

        private int Dp(float v)
        {
            return (int)(v * _act.Resources.DisplayMetrics.Density + 0.5f);
        }

        private void Build()
        {
            string title = Msg.Str(_msg, "title", L10n.T("分享的视频"));
            string cover = Msg.Str(_msg, "cover", "");
            string poster = Msg.Str(_msg, "poster", "");
            string posterProxy = Msg.Str(_msg, "posterProxy", "");
            string watchHost = Msg.Str(_msg, "watchHost", "");
            int w = (int)(_act.Resources.DisplayMetrics.WidthPixels * 0.60);

            var holder = new FrameLayout(_act) { LayoutParameters = new LayoutParams(w, (int)(w * 9.0 / 16.0)) };
            var posterView = new ImageView(_act) { LayoutParameters = new FrameLayout.LayoutParams(-1, -1) };
            posterView.SetScaleType(ImageView.ScaleType.CenterCrop);
            posterView.SetBackgroundColor(new Color(unchecked((int)0xFF1B1F24)));
            holder.AddView(posterView);
            var icon = new IconView(_act, IconKind.Play) { IconColor = unchecked((int)0xFF12B7F5) };
            holder.AddView(icon, new FrameLayout.LayoutParams(Dp(36), Dp(36)) { Gravity = GravityFlags.Center });
            AddView(holder);

            bool shown = false;
            if (!string.IsNullOrEmpty(cover))
            {
                try
                {
                    var bytes = Convert.FromBase64String(cover);
                    var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                    if (bmp != null) { posterView.SetImageBitmap(bmp); shown = true; icon.Visibility = ViewStates.Gone; }
                }
                catch { }
            }
            // 在线影视：异步加载外源 CDN 的瀑布墙海报，优先走宿主机代理复用缓存
            if (!shown) LoadPoster(posterProxy, watchHost, poster, posterView, icon);

            var tv = new TextView(_act) { Text = title, TextSize = 13 };
            tv.SetTextColor(new Color(unchecked((int)0xFF111111)));
            tv.SetSingleLine(true);
            tv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            tv.SetPadding(Dp(2), Dp(8), Dp(2), Dp(4));
            AddView(tv);

            // 右下角两个按钮，与音乐卡片「播放」「一起听」的显示位置和逻辑对齐
            var row = new LinearLayout(_act) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.Right);
            row.SetPadding(0, Dp(2), 0, Dp(2));
            var watch = Pill(L10n.T("观看"), unchecked((int)0xFFE8F7FE), unchecked((int)0xFF12B7F5));
            var together = Pill(L10n.T("一起看"), unchecked((int)0xFFEAFAF1), unchecked((int)0xFF12A05C));
            row.AddView(watch);
            row.AddView(together);
            AddView(row);

            Click += (s, e) => OpenVideo(false);
            watch.Click += (s, e) => OpenVideo(false);
            together.Click += (s, e) => OpenVideo(true);
            posterView.Click += (s, e) => OpenVideo(false);
        }

        private TextView Pill(string text, int bgColor, int fgColor)
        {
            var b = new TextView(_act) { Text = text, TextSize = 12, Gravity = GravityFlags.Center };
            b.SetTextColor(new Color(fgColor));
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor(bgColor);
            d.SetCornerRadius(Dp(14));
            b.Background = d;
            b.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
            var lp = new LayoutParams(-2, -2) { LeftMargin = Dp(6) };
            b.LayoutParameters = lp;
            return b;
        }

        private void LoadPoster(string proxy, string host, string remote, ImageView iv, View icon)
        {
            string url = remote;
            if (!string.IsNullOrEmpty(proxy) && !string.IsNullOrEmpty(host))
                url = host.TrimEnd('/') + (proxy.StartsWith("/") ? proxy : "/" + proxy);
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            Task.Run(() =>
            {
                byte[] bytes = null;
                try { bytes = VodApi.PosterBytes(url); } catch { }
                if (bytes == null || bytes.Length == 0) return;
                _act.RunOnUiThread(() =>
                {
                    try
                    {
                        var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                        if (bmp == null) return;
                        iv.SetImageBitmap(bmp);
                        icon.Visibility = ViewStates.Gone;
                    }
                    catch { }
                });
            });
        }

        private void OpenVideo(bool together)
        {
            try
            {
                string url = together ? VideoShare.WatchPlayUrl(_msg) : VideoShare.PlayUrl(_msg);
                if (string.IsNullOrWhiteSpace(url))
                {
                    L10n.Toast(_act, together
                        ? L10n.T("发起端不在线，无法一起看（需要本机在线）")
                        : L10n.T("卡片里没有播放链接"), ToastLength.Short).Show();
                    return;
                }
                var i = new Intent(_act, typeof(VideoActivity));
                i.PutExtra(VideoActivity.ExtraPath, url);
                i.PutExtra(VideoActivity.ExtraTitle, Msg.Str(_msg, "title", L10n.T("分享的视频")));
                if (together)
                {
                    i.PutExtra(VideoActivity.ExtraWatchCard, Msg.Str(_msg, "watchCard", Msg.Str(_msg, "id", "")));
                    i.PutExtra(VideoActivity.ExtraWatchHost, Msg.Str(_msg, "watchHost", ""));
                }
                _act.StartActivity(i);
            }
            catch (Exception ex)
            {
                L10n.Toast(_act, L10n.T("无法播放：") + ex.Message, ToastLength.Short).Show();
            }
        }
    }
}

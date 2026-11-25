using Android.Content;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>矢量图标小工具：给 TextView 设置左侧图标（替代 emoji 前缀），以及按消息类型映射 IconKind。</summary>
    public static class Icons
    {
        /// <summary>在文字前显示一个矢量图标（sizeDp/gapDp 以 dp 计）。</summary>
        public static void Leading(TextView tv, IconKind kind, int color, float sizeDp = 16f, float gapDp = 5f)
        {
            if (tv == null) return;
            float d = tv.Resources.DisplayMetrics.Density;
            int sz = (int)(sizeDp * d + 0.5f);
            int gap = (int)(gapDp * d + 0.5f);
            tv.SetCompoundDrawablesWithIntrinsicBounds(new IconDrawable(tv.Context, kind, color, sz), null, null, null);
            tv.CompoundDrawablePadding = gap;
        }

        /// <summary>把聊天消息「类型」映射成矢量图标（替代 🖼 🎬 🎵 🔗 🔔 等）。</summary>
        public static IconKind? ForMessageKind(string kind)
        {
            switch (kind)
            {
                case "pic": return IconKind.Photo;
                case "video": return IconKind.Video;
                case "music": return IconKind.Music;
                case "voice": return IconKind.Mic;
                case "file": return IconKind.File;
                case "url": return IconKind.Link;
                case "notice": return IconKind.Bell;
                case "location": return IconKind.Pin;
                default: return null;
            }
        }

        /// <summary>媒体大类 → 矢量图标（替代 ▣ ▶ ♪ ▤）。</summary>
        public static IconKind ForMediaKinds(MediaKinds k)
        {
            switch (k)
            {
                case MediaKinds.Image: return IconKind.Photo;
                case MediaKinds.Video: return IconKind.Video;
                case MediaKinds.Audio: return IconKind.Music;
                default: return IconKind.File;
            }
        }

        /// <summary>分享类别（mlib）→ 矢量图标（替代 📖 ♪ ▣ ▶ ▤）。</summary>
        public static IconKind ForMlib(string mlib)
        {
            switch (mlib)
            {
                case "book": return IconKind.Book;
                case "music": return IconKind.Music;
                case "photo": return IconKind.Photo;
                case "video": return IconKind.Video;
                default: return IconKind.File;
            }
        }
    }
}

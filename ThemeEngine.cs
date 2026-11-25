using System;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 运行时日间 / 夜间配色引擎。
    ///
    /// 本项目各原生界面都用 <c>SetBackgroundColor</c> 画「白卡片 + 浅灰底」的日间配色，
    /// 因此无需逐界面改写——在界面构建完成后遍历视图树：
    ///   · 中性**浅色**背景（白卡片 / 浅灰底 / 浅灰分隔）→ 映射为夜间深色；
    ///   · 中性**深色**文字（近黑 / 中灰）→ 映射为夜间浅色文字；
    ///   · 饱和度高的**强调色 / 状态色**（蓝、青、绿、红）一律不动；
    ///   · 本来就是深色的模块（相册、视频、通话等深底界面）原样保留，不会被破坏。
    ///
    /// 每个 View 上缓存一份「日间原始值」，切回日间时精确还原，避免反复叠加映射。
    /// 之所以能覆盖全部模块：这些界面的调色板字段值高度一致（White / #F2F3F5 / #EEEEF1 / #111111…）。
    /// </summary>
    public static class ThemeEngine
    {
        // 存 Snapshot 的 View.tag 键：放在应用资源 id 段（0x7F…），满足 Android 对
        // setTag(int,Object) 键值的校验；不会与真实资源 id 冲突。
        private const int TagKey = 0x7F0F00E1;

        // 夜间调色板（与网页端 web.css / app.css 的 [data-theme=dark] 对齐）
        private static readonly int NightBg = IosStyle.NightBackground; // 页面底 / 系统栏
        private static readonly int NightCard = IosStyle.NightSurface; // 卡片
        private static readonly int NightChip = IosStyle.NightElevated; // 次级填充 / 中灰分隔
        private static readonly int NightText = IosStyle.NightText; // 主文字
        private static readonly int NightSub = IosStyle.NightSecondary; // 次文字
        private static readonly int DayBar = IosStyle.Background; // 日间状态栏 / 窗口底

        private sealed class Snapshot : Java.Lang.Object
        {
            public bool HasBg; public int Bg;
            public bool HasFg; public int Fg;
            public bool HasLastFg; public int LastFg;
            public bool HasIcon; public int Icon;
            // 非 ColorDrawable 背景（圆角 GradientDrawable 卡片等）：采样到的原始填充色
            public bool HasGrad; public int Grad;
        }

        /// <summary>按当前主题重刷一个 Activity 的全部视图与系统栏（不重建界面）。</summary>
        public static void Apply(Activity a)
        {
            if (a == null) return;
            bool night = ThemeManager.IsNight(a);
            ApplyBars(a, night);
            try
            {
                var decor = a.Window == null ? null : a.Window.DecorView;
                if (decor != null) Walk(decor, night);
            }
            catch { }
        }

        private static void ApplyBars(Activity a, bool night)
        {
            try
            {
                var w = a.Window;
                if (w == null) return;
                w.SetStatusBarColor(new Color(night ? NightBg : DayBar));
                w.SetNavigationBarColor(new Color(night ? NightBg : DayBar));
                try { w.SetBackgroundDrawable(new ColorDrawable(new Color(night ? NightBg : DayBar))); } catch { }
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    var ctrl = w.InsetsController;
                    if (ctrl != null)
                    {
                        // 8 = APPEARANCE_LIGHT_STATUS_BARS（深色图标）；夜间传 0 表示浅色图标
                        ctrl.SetSystemBarsAppearance(night ? 0 : 8, 8);
                    }
                }
            }
            catch { }
        }

        private static void Walk(View v, bool night)
        {
            if (v == null) return;

            var snap = v.GetTag(TagKey) as Snapshot;
            if (snap == null)
            {
                snap = new Snapshot();
                v.SetTag(TagKey, snap);
            }

            // ---- 背景 ----
            try
            {
                var bg = v.Background;
                var cd = bg as ColorDrawable;
                if (cd != null)
                {
                    int c = cd.Color;
                    if (!snap.HasBg) { snap.HasBg = true; snap.Bg = c; }
                    // The saved source value is authoritative in day mode. Reverse-color
                    // inference cannot distinguish a theme-mapped surface from an intentional
                    // dark media canvas (for example, a call/video stage).
                    int baseBg = snap.Bg;
                    int target = night ? MapBg(baseBg) : baseBg;
                    if (c != target) v.SetBackgroundColor(new Color(target));
                }
                else if (bg != null)
                {
                    // 圆角卡片等用 GradientDrawable 画的背景：读不到颜色，改为「采样 + 颜色滤镜」。
                    // 采样只在首次（记录日间原始色）；夜间对中性浅色填充套 SrcIn 滤镜，
                    // 既保留圆角/形状，又把白卡变成夜间卡色。彩色/已深色的一律清除滤镜。
                    if (!snap.HasGrad)
                    {
                        int sampled;
                        if (TrySampleColor(bg, out sampled)) { snap.HasGrad = true; snap.Grad = sampled; }
                    }
                    if (snap.HasGrad)
                    {
                        int baseG = snap.Grad;
                        int mapped = night ? MapBg(baseG) : baseG;
                        if (mapped != baseG)
                            bg.SetColorFilter(new PorterDuffColorFilter(new Color(mapped), PorterDuff.Mode.SrcIn));
                        else
                            bg.ClearColorFilter();
                    }
                }
            }
            catch { }

            // ---- 文字 ----
            try
            {
                var tv = v as TextView;
                if (tv != null)
                {
                    int fg = tv.CurrentTextColor;
                    if (!snap.HasFg)
                    {
                        snap.HasFg = true;
                        snap.Fg = fg;
                    }
                    else if (snap.HasLastFg && fg != snap.LastFg)
                    {
                        // The app changed this TextView since the previous pass (for example,
                        // a live green/orange/red service status). Treat that as the new source
                        // color instead of restoring a stale first-pass snapshot.
                        snap.Fg = fg;
                    }

                    int baseFg = snap.Fg;
                    int target = night ? MapFg(baseFg) : baseFg;
                    if (fg != target) tv.SetTextColor(new Color(target));
                    snap.LastFg = target;
                    snap.HasLastFg = true;
                }
            }
            catch { }

            // ---- 自绘图标 ----
            try
            {
                var iv = v as IconView;
                if (iv != null)
                {
                    int ic = iv.IconColor;
                    if (!snap.HasIcon) { snap.HasIcon = true; snap.Icon = ic; }
                    int baseIc = snap.Icon;
                    int target = night ? MapFg(baseIc) : baseIc;
                    if (ic != target)
                    {
                        iv.IconColor = target;
                        // IconColor 只是普通属性，不会自动触发重绘；不 invalidate 会停留在旧色
                        // （表现为深底上仍是深色图标，看起来「发暗」）。
                        iv.Invalidate();
                    }
                }
            }
            catch { }

            // ---- 子树 ----
            var vg = v as ViewGroup;
            if (vg != null)
            {
                for (int i = 0; i < vg.ChildCount; i++)
                    Walk(vg.GetChildAt(i), night);
            }
        }

        /// <summary>
        /// 把任意 Drawable（GradientDrawable 圆角卡片等）画到一个极小的位图上，取中心像素即其填充色。
        /// 只在前端首次记录原始颜色时调用，开销可接受；失败则放弃重着色（不影响界面）。
        /// </summary>
        private static bool TrySampleColor(Drawable d, out int color)
        {
            color = 0;
            if (d == null) return false;
            Bitmap bmp = null;
            try
            {
                const int sz = 16;
                bmp = Bitmap.CreateBitmap(sz, sz, Bitmap.Config.Argb8888);
                var canvas = new Canvas(bmp);
                var old = new Rect();
                d.CopyBounds(old);
                d.SetBounds(0, 0, sz, sz);
                d.Draw(canvas);
                color = bmp.GetPixel(sz / 2, sz / 2);
                d.SetBounds(old.Left, old.Top, old.Right, old.Bottom);
                return true;
            }
            catch { return false; }
            finally { try { if (bmp != null) bmp.Recycle(); } catch { } }
        }

        /* ---------------- 颜色分类 ---------------- */

        private static float Value(int c)
        {
            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            int max = Math.Max(r, Math.Max(g, b));
            return max / 255f;
        }

        private static float Sat(int c)
        {
            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            return max == 0 ? 0f : (float)(max - min) / max;
        }

        private static int MapBg(int c)
        {
            if (((c >> 24) & 0xFF) == 0) return c;       // 透明 → 不动
            if (Sat(c) >= 0.18f) return c;               // 彩色（强调/状态）→ 不动
            float v = Value(c);
            if (v < 0.55f) return c;                     // 本就深色（相册/视频/通话）→ 保留
            if (v >= 0.985f) return NightCard;           // 纯白卡片
            if (v >= 0.86f) return NightBg;              // 浅灰页面底 / 顶栏
            return NightChip;                            // 中灰填充 / 分隔
        }

        private static int MapFg(int c)
        {
            if (((c >> 24) & 0xFF) == 0) return c;       // 透明 → 不动
            if (Sat(c) >= 0.18f) return c;               // 彩色文字 → 不动
            float v = Value(c);
            if (v < 0.35f) return NightText;             // 近黑 → 主文字
            if (v < 0.62f) return NightSub;              // 中灰 → 次文字
            return c;                                    // 已是浅色（如强调按钮上的白字）→ 保留
        }

    }
}

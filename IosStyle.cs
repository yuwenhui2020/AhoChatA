using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// Shared iOS-inspired native layout and color tokens.
    /// Geometry helpers are opt-in; this class deliberately does not rewrite view trees.
    /// </summary>
    public static class IosStyle
    {
        public const int PageInset = 16;
        public const int CardInset = 16;
        public const int SectionGap = 10;
        public const int RowMinHeight = 46;
        public const int ToolbarHeight = 56;
        public const int ControlHeight = 44;
        public const int CardRadius = 16;
        public const int ControlRadius = 12;

        public const float TitleSize = 17f;
        public const float BodySize = 16f;
        public const float CaptionSize = 13f;
        public const float HeadingSize = 22f;

        public static readonly int Accent = unchecked((int)0xFF007AFF);
        public static readonly int Background = unchecked((int)0xFFF2F2F7);
        public static readonly int Surface = unchecked((int)0xFFFFFFFF);
        public static readonly int Text = unchecked((int)0xFF1C1C1E);
        public static readonly int Secondary = unchecked((int)0xFF8E8E93);
        public static readonly int Separator = unchecked((int)0xFFC6C6C8);
        public static readonly int Success = unchecked((int)0xFF34C759);
        public static readonly int Danger = unchecked((int)0xFFFF3B30);
        public static readonly int Warning = unchecked((int)0xFFE8A33D);

        // Aliases for backward compatibility with existing code
        public static readonly int CardBackground = Surface;
        public static readonly int SecondaryText = Secondary;
        public static readonly int AccentText = Surface;
        public static readonly float FootnoteSize = CaptionSize;

        // Companion neutral palette for explicit night-theme styling.
        public static readonly int NightBackground = unchecked((int)0xFF000000);
        public static readonly int NightSurface = unchecked((int)0xFF1C1C1E);
        public static readonly int NightElevated = unchecked((int)0xFF2C2C2E);
        public static readonly int NightText = unchecked((int)0xFFF2F2F7);
        public static readonly int NightSecondary = unchecked((int)0xFF8E8E93);
        public static readonly int NightSeparator = unchecked((int)0xFF38383A);

        /// <summary>Convert density-independent pixels to physical pixels, rounded to the nearest pixel.</summary>
        public static int Dp(Context context, float value)
        {
            float density = context == null ? 1f : context.Resources.DisplayMetrics.Density;
            return (int)(value * density + 0.5f);
        }

        /// <summary>Create a filled rounded drawable; radius is specified in dp.</summary>
        public static GradientDrawable Rounded(Context context, int color, float radiusDp)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(new Color(color));
            drawable.SetCornerRadius(Dp(context, radiusDp));
            return drawable;
        }

        /// <summary>Apply the shared iOS-style size, neutral text colors, and rounded surface to an input.</summary>
        public static void StyleInput(EditText input)
        {
            if (input == null) return;
            input.SetTextSize(Android.Util.ComplexUnitType.Sp, BodySize);
            input.SetTextColor(new Color(Text));
            input.SetHintTextColor(new Color(Secondary));
            var background = Rounded(input.Context, Surface, ControlRadius);
            background.SetStroke(Dp(input.Context, 1), new Color(Separator));
            input.Background = background;
            input.SetMinimumHeight(Dp(input.Context, ControlHeight));
            input.SetPadding(Dp(input.Context, 12), Dp(input.Context, 8),
                Dp(input.Context, 12), Dp(input.Context, 8));
        }

        /// <summary>递归把视图树里的原生 CheckBox 换成 iOS 圆角勾选框（不改变文字色与布局）。</summary>
        public static void StyleCheckBoxes(View root)
        {
            if (root == null) return;
            try
            {
                var box = root as CheckBox;
                if (box != null)
                {
                    box.SetButtonDrawable(root.Context.Resources.GetDrawable(Resource.Drawable.ios_checkbox));
                    return;
                }
                var group = root as ViewGroup;
                if (group == null) return;
                for (int i = 0; i < group.ChildCount; i++) StyleCheckBoxes(group.GetChildAt(i));
            }
            catch { }
        }

        public static void StyleCheckBoxes(Activity activity)
        {
            if (activity?.Window == null) return;
            StyleCheckBoxes(activity.Window.DecorView);
        }

        /// <summary>Apply the shared blue primary-action treatment to a button.</summary>
        public static void StylePrimaryButton(Button button)
        {
            if (button == null) return;
            button.SetTextSize(Android.Util.ComplexUnitType.Sp, BodySize);
            button.SetTextColor(new Color(Surface));
            button.Background = Rounded(button.Context, Accent, ControlRadius);
            button.SetMinimumHeight(Dp(button.Context, ControlHeight));
            button.SetPadding(Dp(button.Context, 16), button.PaddingTop,
                Dp(button.Context, 16), button.PaddingBottom);
            button.SetAllCaps(false);
        }

        /// <summary>Apply the shared neutral-surface secondary-action treatment to a button.</summary>
        public static void StyleSecondaryButton(Button button)
        {
            if (button == null) return;
            button.SetTextSize(Android.Util.ComplexUnitType.Sp, BodySize);
            button.SetTextColor(new Color(Accent));
            var background = Rounded(button.Context, Surface, ControlRadius);
            background.SetStroke(Dp(button.Context, 1), new Color(Separator));
            button.Background = background;
            button.SetMinimumHeight(Dp(button.Context, ControlHeight));
            button.SetPadding(Dp(button.Context, 16), button.PaddingTop,
                Dp(button.Context, 16), button.PaddingBottom);
            button.SetAllCaps(false);
        }

        /// <summary>Apply the shared surface and minimum height to a toolbar LinearLayout.</summary>
        public static void StyleToolbar(LinearLayout toolbar)
        {
            if (toolbar == null) return;
            toolbar.SetBackgroundColor(new Color(Surface));
            toolbar.SetMinimumHeight(Dp(toolbar.Context, ToolbarHeight));
            toolbar.SetPadding(Dp(toolbar.Context, PageInset), toolbar.PaddingTop,
                Dp(toolbar.Context, PageInset), toolbar.PaddingBottom);
        }

        /// <summary>Apply the shared 17sp title treatment to a TextView.</summary>
        public static void StyleTitle(TextView title)
        {
            if (title == null) return;
            title.SetTextSize(Android.Util.ComplexUnitType.Sp, TitleSize);
            title.SetTextColor(new Color(Text));
            title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        }

        /* ------------------------------------------------------------------ */
        /* 组件工厂：供各页面复用，保证圆角、字号、间距和日夜配色一致。          */
        /* ------------------------------------------------------------------ */

        public sealed class Palette
        {
            public bool Night;
            public int Background;
            public int Surface;
            public int Text;
            public int Secondary;
            public int Separator;
            public int Accent;
            public int Danger;
        }

        public static Palette Colors(Context context)
        {
            bool night = ThemeManager.IsNight(context);
            return new Palette
            {
                Night = night,
                Background = night ? NightBackground : Background,
                Surface = night ? NightSurface : Surface,
                Text = night ? NightText : Text,
                Secondary = night ? NightSecondary : Secondary,
                Separator = night ? NightSeparator : Separator,
                Accent = night ? unchecked((int)0xFF0A84FF) : Accent,
                Danger = night ? unchecked((int)0xFFFF453A) : Danger
            };
        }

        /// <summary>iOS 分组卡片：圆角 + 细描边，避免不同页面自造背景。</summary>
        public static GradientDrawable GroupCardBackground(Context context, Palette palette = null)
        {
            var p = palette ?? Colors(context);
            var d = Rounded(context, p.Surface, CardRadius);
            d.SetStroke(Dp(context, 1), new Color(p.Separator));
            return d;
        }

        public static TextView SectionHeader(Context context, string text, Palette palette = null)
        {
            var p = palette ?? Colors(context);
            var v = new TextView(context) { Text = text, TextSize = CaptionSize };
            v.SetTextColor(new Color(p.Secondary));
            v.SetPadding(Dp(context, PageInset), Dp(context, SectionGap), Dp(context, PageInset), Dp(context, 4));
            return v;
        }

        public static TextView BodyText(Context context, string text, Palette palette = null, float size = BodySize, bool bold = false)
        {
            var p = palette ?? Colors(context);
            var v = new TextView(context) { Text = text, TextSize = size };
            v.SetTextColor(new Color(p.Text));
            if (bold) v.SetTypeface(null, TypefaceStyle.Bold);
            return v;
        }

        public static Button ActionButton(Context context, string text, bool primary, Palette palette = null)
        {
            var p = palette ?? Colors(context);
            var b = new Button(context, null, AndroidRes.Attribute.ButtonStyle) { Text = text };
            b.SetAllCaps(false);
            b.SetTextSize(ComplexUnitType.Sp, BodySize);
            b.SetMinimumHeight(Dp(context, ControlHeight));
            b.SetPadding(Dp(context, 14), 0, Dp(context, 14), 0);
            if (primary)
            {
                b.SetTextColor(new Color(p.Night ? Color.Black.ToArgb() : Color.White.ToArgb()));
                b.Background = Rounded(context, p.Accent, ControlRadius);
            }
            else
            {
                b.SetTextColor(new Color(p.Accent));
                var bg = Rounded(context, p.Surface, ControlRadius);
                bg.SetStroke(Dp(context, 1), new Color(p.Separator));
                b.Background = bg;
            }
            return b;
        }

        /// <summary>多行等宽代码编辑器：用于油猴源码查看/编辑。</summary>
        public static EditText CodeEditor(Context context, string text, Palette palette = null)
        {
            var p = palette ?? Colors(context);
            var input = new EditText(context) { Text = text ?? "" };
            input.SetSingleLine(false);
            input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine;
            input.SetTextSize(ComplexUnitType.Sp, 13f);
            input.SetTextColor(new Color(p.Text));
            input.SetHintTextColor(new Color(p.Secondary));
            input.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            input.Gravity = GravityFlags.Top | GravityFlags.Start;
            input.SetMinLines(20);
            input.SetHorizontallyScrolling(true);
            input.HorizontalScrollBarEnabled = true;
            input.VerticalScrollBarEnabled = true;
            input.ScrollBarStyle = ScrollbarStyles.InsideInset;
            input.SetPadding(Dp(context, 12), Dp(context, 12), Dp(context, 12), Dp(context, 12));
            input.Background = Rounded(context, p.Surface, ControlRadius);
            return input;
        }

        /// <summary>把 AlertDialog 显示为接近 iOS 卡片弹窗的尺寸与底色。调用后无需再 Show。
        /// heightRatio &lt;= 0 时按内容高度自适应，避免按钮下方出现大块空白。</summary>
        public static void ShowCardDialog(AlertDialog dialog, Context context, float widthRatio = 0.94f, float heightRatio = 0.88f)
        {
            if (dialog == null) return;
            var p = Colors(context);
            dialog.Show();
            var w = dialog.Window;
            if (w == null) return;
            var bg = Rounded(context, p.Surface, CardRadius);
            bg.SetPadding(Dp(context, 16), Dp(context, 12), Dp(context, 16), Dp(context, 12));
            w.SetBackgroundDrawable(bg);
            var metrics = context.Resources.DisplayMetrics;
            var lp = w.Attributes;
            lp.Width = System.Math.Max(Dp(context, 280), (int)(metrics.WidthPixels * widthRatio));
            lp.Height = heightRatio <= 0 ? ViewGroup.LayoutParams.WrapContent
                : System.Math.Max(Dp(context, 320), (int)(metrics.HeightPixels * heightRatio));
            w.Attributes = lp;
            StyleCheckBoxes(w.DecorView);
        }

        /// <summary>所有页面共用的 iOS 风格系统栏配色；在主题切换后重复调用即可。</summary>
        public static void ApplySystemBars(Activity activity)
        {
            if (activity?.Window == null) return;
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop) return;
                var p = Colors(activity);
                activity.Window.SetStatusBarColor(new Color(p.Background));
                activity.Window.SetNavigationBarColor(new Color(p.Background));
                var decor = activity.Window.DecorView;
                if (decor == null) return;
                int flags = (int)SystemUiFlags.Visible;
                if (!p.Night) flags |= (int)SystemUiFlags.LightStatusBar;
                decor.SystemUiVisibility = (StatusBarVisibility)flags;
            }
            catch { }
        }

        /// <summary>统一分割线，左边距与分组文字对齐。</summary>
        public static View Divider(Context context, Palette palette = null, float startInsetDp = CardInset)
        {
            var p = palette ?? Colors(context);
            var line = new View(context);
            line.SetBackgroundColor(new Color(p.Separator));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            lp.LeftMargin = Dp(context, startInsetDp);
            line.LayoutParameters = lp;
            return line;
        }

        // AndroidRes alias used by ActionButton without adding a second namespace alias here.
        private static class AndroidRes
        {
            public static class Attribute
            {
                public const int ButtonStyle = global::Android.Resource.Attribute.ButtonStyle;
            }
        }
    }
}

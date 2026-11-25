using System;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidRes = Android.Resource;

namespace AhoChatA
{
    /// <summary>
    /// iOS 18 Design System - 统一设计语言
    /// 基于 8pt 基准网格，语义化颜色，Dynamic Type 支持
    /// </summary>
    public static class Design
    {
        // ===== 间距系统 (8pt 基准) =====
        public const int Space0 = 0;
        public const int Space1 = 2;  // 0.25x
        public const int Space2 = 4;  // 0.5x
        public const int Space3 = 8;  // 1x
        public const int Space4 = 12; // 1.5x
        public const int Space5 = 16; // 2x
        public const int Space6 = 24; // 3x
        public const int Space7 = 32; // 4x
        public const int Space8 = 48; // 6x

        // ===== 圆角系统 =====
        public const int RadiusNone = 0;
        public const int RadiusSm = 6;   // 小圆角
        public const int RadiusMd = 10;  // 中圆角
        public const int RadiusLg = 14;  // 大圆角
        public const int RadiusXl = 20;  // 特大圆角
        public const int RadiusFull = 999; // 胶囊/圆形

        // ===== 控件高度 =====
        public const int HeightXs = 28;  // 小
        public const int HeightSm = 36;  // 小
        public const int HeightMd = 44;  // 标准
        public const int HeightLg = 52;  // 大

        // ===== 字体系统 (Dynamic Type 基准) =====
        public const float TextXs = 11f;   // Caption2
        public const float TextSm = 13f;   // Caption1
        public const float TextMd = 15f;   // Footnote
        public const float TextBase = 17f; // Body
        public const float TextLg = 20f;   // Title3
        public const float TextXl = 24f;   // Title2
        public const float Text2xl = 28f;  // Title1
        public const float Text3xl = 34f;  // Large Title

        public const float LineHeightTight = 1.3f;
        public const float LineHeightNormal = 1.5f;
        public const float LineHeightRelaxed = 1.6f;

        // ===== 阴影 =====
        public const float Elevation0 = 0;
        public const float Elevation1 = 1f;
        public const float Elevation2 = 4f;
        public const float Elevation3 = 8f;
        public const float Elevation4 = 16f;

        // ===== 动画时长 =====
        public const int DurationFast = 150;
        public const int DurationNormal = 250;
        public const int DurationSlow = 350;

        // ===== 颜色语义 (Light) =====
        public static class Light
        {
            // Background
            public static readonly int Background = 0xFFF2F2F7;      // systemBackground
            public static readonly int SecondaryBackground = 0xFFFFFFFF; // secondarySystemBackground
            public static readonly int TertiaryBackground = 0xFFF2F2F7;  // tertiarySystemBackground

            // Content
            public static readonly int Label = 0xFF000000;           // label
            public static readonly int SecondaryLabel = 0xFF8E8E93;  // secondaryLabel
            public static readonly int TertiaryLabel = 0xFFC7C7CC;   // tertiaryLabel
            public static readonly int QuaternaryLabel = 0xFFD1D1D6; // quaternaryLabel

            // Fill
            public static readonly int Fill = 0xFF8E8E93;            // systemFill
            public static readonly int FillSecondary = 0xFFC7C7CC;   // secondarySystemFill
            public static readonly int FillTertiary = 0xFFD1D1D6;    // tertiarySystemFill

            // Separator
            public static readonly int Separator = 0xFFC6C6C8;       // separator
            public static readonly int OpaqueSeparator = 0xFFC6C6C8; // opaqueSeparator

            // Accent
            public static readonly int Accent = 0xFF007AFF;          // systemBlue
            public static readonly int AccentOn = 0xFFFFFFFF;        // onAccent

            // Semantic
            public static readonly int Success = 0xFF34C759;         // systemGreen
            public static readonly int Warning = 0xFFFF9F0A;         // systemOrange
            public static readonly int Danger = 0xFFFF3B30;          // systemRed
            public static readonly int Info = 0xFF5856D6;            // systemPurple
        }

        // ===== 颜色语义 (Dark) =====
        public static class Dark
        {
            public static readonly int Background = 0xFF000000;
            public static readonly int SecondaryBackground = 0xFF1C1C1E;
            public static readonly int TertiaryBackground = 0xFF2C2C2E;

            public static readonly int Label = 0xFFFFFFFF;
            public static readonly int SecondaryLabel = 0xFFEBEBF5; // 60% opacity
            public static readonly int TertiaryLabel = 0xFFEBEBF5;  // 30% opacity
            public static readonly int QuaternaryLabel = 0xFFEBEBF5; // 18% opacity

            public static readonly int Fill = 0xFF3A3A3C;
            public static readonly int FillSecondary = 0xFF48484A;
            public static readonly int FillTertiary = 0xFF58585A;

            public static readonly int Separator = 0xFF38383A;
            public static readonly int OpaqueSeparator = 0xFF545458;

            public static readonly int Accent = 0xFF0A84FF;
            public static readonly int AccentOn = 0xFF000000;

            public static readonly int Success = 0xFF30D158;
            public static readonly int Warning = 0xFFFF9F0A;
            public static readonly int Danger = 0xFFFF453A;
            public static readonly int Info = 0xFFBF5AF2;
        }

        // ===== 辅助方法 =====
        public static int Dp(Context ctx, float dp) => (int)(dp * (ctx?.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);
        public static int Sp(Context ctx, float sp) => (int)(sp * (ctx?.Resources?.DisplayMetrics?.ScaledDensity ?? 1f) + 0.5f);

        public static int Color(bool night, int light, int dark) => night ? dark : light;

        public static GradientDrawable Rounded(Context ctx, int color, int radiusDp)
        {
            var d = new GradientDrawable();
            d.SetColor(new Color(color));
            d.SetCornerRadius(Dp(ctx, radiusDp));
            return d;
        }

        public static GradientDrawable Card(Context ctx, bool night, int radius = RadiusLg, float elevation = Elevation1)
        {
            var bg = Rounded(ctx, Color(night, Light.SecondaryBackground, Dark.SecondaryBackground), RadiusLg);
            if (elevation > 0) bg.SetShadowLayer(Dp(ctx, elevation), 0, Dp(ctx, 2), Color(night, 0x33000000, 0x99000000));
            return bg;
        }

        public static GradientDrawable Surface(Context ctx, bool night, int radius = RadiusMd)
        {
            return Rounded(ctx, Color(night, Light.TertiaryBackground, Dark.TertiaryBackground), radius);
        }

        public static GradientDrawable Outline(Context ctx, bool night, int radius = RadiusMd, int strokeWidth = 1)
        {
            var d = new GradientDrawable();
            d.SetColor(Color(night, Light.Background, Dark.Background));
            d.SetCornerRadius(Dp(ctx, radius));
            d.SetStroke(Dp(ctx, strokeWidth), new Color(Color(night, Light.Separator, Dark.Separator)));
            return d;
        }

        public static GradientDrawable Pill(Context ctx, bool night, bool filled = true)
        {
            var d = new GradientDrawable();
            d.SetCornerRadius(Dp(ctx, RadiusFull));
            if (filled)
            {
                d.SetColor(new Color(Color(night, Light.Accent, Dark.Accent)));
            }
            else
            {
                d.SetStroke(Dp(ctx, 1.5f), new Color(Color(night, Light.Accent, Dark.Accent)));
                d.SetColor(Color(night, Light.Background, Dark.Background));
            }
            return d;
        }

        public static Typeface Mono(Context ctx) => Typeface.Create("monospace", TypefaceStyle.Normal);
        public static Typeface System(Context ctx) => Typeface.Default;
        public static Typeface SystemBold(Context ctx) => Typeface.DefaultBold;
    }

    // ===== 颜色获取器 =====
    public sealed class Colors
    {
        private readonly bool _night;

        public Colors(Context ctx) => _night = ThemeManager.IsNight(ctx);

        // Background
        public int Background => Design.Color(_night, Light.Background, Dark.Background);
        public int SecondaryBackground => Design.Color(_night, Light.SecondaryBackground, Dark.SecondaryBackground);
        public int TertiaryBackground => Design.Color(_night, Light.TertiaryBackground, Dark.TertiaryBackground);

        // Content
        public int Label => Design.Color(_night, Light.Label, Dark.Label);
        public int SecondaryLabel => Design.Color(_night, Light.SecondaryLabel, Dark.SecondaryLabel);
        public int TertiaryLabel => Design.Color(_night, Light.TertiaryLabel, Dark.TertiaryLabel);
        public int QuaternaryLabel => Design.Color(_night, Light.QuaternaryLabel, Dark.QuaternaryLabel);

        // Fill
        public int Fill => Design.Color(_night, Light.Fill, Dark.Fill);
        public int FillSecondary => Design.Color(_night, Light.FillSecondary, Dark.FillSecondary);
        public int FillTertiary => Design.Color(_night, Light.FillTertiary, Dark.FillTertiary);

        // Separator
        public int Separator => Design.Color(_night, Light.Separator, Dark.Separator);
        public int OpaqueSeparator => Design.Color(_night, Light.OpaqueSeparator, Dark.OpaqueSeparator);

        // Accent
        public int Accent => Design.Color(_night, Light.Accent, Dark.Accent);
        public int AccentOn => Design.Color(_night, Light.AccentOn, Dark.AccentOn);

        // Semantic
        public int Success => Design.Color(_night, Light.Success, Dark.Success);
        public int Warning => Design.Color(_night, Light.Warning, Dark.Warning);
        public int Danger => Design.Color(_night, Light.Danger, Dark.Danger);
        public int Info => Design.Color(_night, Light.Info, Dark.Info);

        public bool Night => _night;
    }

    // ===== 组件工厂 =====
    public static class UI
    {
        public static TextView Label(Context ctx, string text, float size = Design.TextBase, bool bold = false, int? color = null)
        {
            var c = new Colors(ctx);
            var v = new TextView(ctx) { Text = text };
            v.SetTextSize(ComplexUnitType.Sp, size);
            v.SetTextColor(new Color(color ?? c.Label));
            if (bold) v.SetTypeface(null, TypefaceStyle.Bold);
            v.SetLineSpacing(0, Design.LineHeightNormal);
            return v;
        }

        public static TextView SecondaryLabel(Context ctx, string text, float size = Design.TextSm)
        {
            var c = new Colors(ctx);
            var v = new TextView(ctx) { Text = text };
            v.SetTextSize(ComplexUnitType.Sp, size);
            v.SetTextColor(new Color(c.SecondaryLabel));
            v.SetLineSpacing(0, Design.LineHeightNormal);
            return v;
        }

        public static TextView TertiaryLabel(Context ctx, string text, float size = Design.TextXs)
        {
            var c = new Colors(ctx);
            var v = new TextView(ctx) { Text = text };
            v.SetTextSize(ComplexUnitType.Sp, size);
            v.SetTextColor(new Color(c.TertiaryLabel));
            v.SetLineSpacing(0, Design.LineHeightNormal);
            return v;
        }

        public static EditText Input(Context ctx, string hint = null, string text = null, bool secure = false)
        {
            var c = new Colors(ctx);
            var v = new EditText(ctx) { Hint = hint, Text = text ?? "" };
            if (secure) v.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword;
            v.SetTextSize(ComplexUnitType.Sp, Design.TextBase);
            v.SetTextColor(new Color(c.Label));
            v.SetHintTextColor(new Color(c.TertiaryLabel));
            v.Background = Design.Rounded(ctx, c.SecondaryBackground, Design.RadiusMd);
            v.SetMinimumHeight(Design.Dp(ctx, Design.HeightMd));
            v.SetPadding(Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space3),
                Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space3));
            return v;
        }

        public static Button PrimaryButton(Context ctx, string text, Action click = null)
        {
            var c = new Colors(ctx);
            var b = new Button(ctx) { Text = text, AllCaps = false };
            b.SetTextSize(ComplexUnitType.Sp, Design.TextBase);
            b.SetTextColor(new Color(c.AccentOn));
            b.Background = Design.Pill(ctx, c.Night, true);
            b.SetMinimumHeight(Design.Dp(ctx, Design.HeightMd));
            b.SetPadding(Design.Dp(ctx, Design.Space6), Design.Dp(ctx, Design.Space3),
                Design.Dp(ctx, Design.Space6), Design.Dp(ctx, Design.Space3));
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        public static Button SecondaryButton(Context ctx, string text, Action click = null)
        {
            var c = new Colors(ctx);
            var b = new Button(ctx) { Text = text, AllCaps = false };
            b.SetTextSize(ComplexUnitType.Sp, Design.TextBase);
            b.SetTextColor(new Color(c.Accent));
            var bg = new GradientDrawable();
            bg.SetColor(new Color(c.Background));
            bg.SetCornerRadius(Design.Dp(ctx, Design.RadiusFull));
            bg.SetStroke(Design.Dp(ctx, 1.5f), new Color(c.Accent));
            b.Background = bg;
            b.SetMinimumHeight(Design.Dp(ctx, Design.HeightMd));
            b.SetPadding(Design.Dp(ctx, Design.Space6), Design.Dp(ctx, Design.Space3),
                Design.Dp(ctx, Design.Space6), Design.Dp(ctx, Design.Space3));
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        public static Button DestructiveButton(Context ctx, string text, Action click = null)
        {
            var c = new Colors(ctx);
            var b = new Button(ctx) { Text = text, AllCaps = false };
            b.SetTextSize(ComplexUnitType.Sp, Design.TextBase);
            b.SetTextColor(new Color(c.Danger));
            var bg = new GradientDrawable();
            bg.SetColor(new Color(c.Background));
            bg.SetCornerRadius(Design.Dp(ctx, Design.RadiusFull));
            bg.SetStroke(Design.Dp(ctx, 1.5f), new Color(c.Danger));
            b.Background = bg;
            b.SetMinimumHeight(Design.Dp(ctx, Design.HeightMd));
            b.SetPadding(Design.Dp(ctx, Design.Space6), Design.Dp(ctx, Design.Space3),
                Design.Dp(ctx, Design.Space6), Design.Dp(ctx, Design.Space3));
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        public static Switch Switch(Context ctx, bool @checked = false, Action<bool> change = null)
        {
            var c = new Colors(ctx);
            var s = new Switch(ctx) { Checked = @checked };
            s.CheckedChange += (s, e) => change?.Invoke(e.IsChecked);
            return s;
        }

        public static LinearLayout Card(Context ctx, bool night, float elevation = Design.Elevation1)
        {
            var v = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            v.Background = Design.Card(ctx, night, Design.RadiusLg, Design.Elevation1);
            v.SetPadding(Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space4),
                Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space4));
            return v;
        }

        public static LinearLayout Row(Context ctx, int gravity = GravityFlags.CenterVertical, int paddingH = Design.Space5, int paddingV = Design.Space3)
        {
            var v = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            v.Gravity = gravity;
            v.SetPadding(Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space3),
                Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space3));
            return v;
        }

        public static LinearLayout Col(Context ctx, int paddingH = Design.Space5, int paddingV = Design.Space4)
        {
            var v = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            v.SetPadding(Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space4),
                Design.Dp(ctx, Design.Space5), Design.Dp(ctx, Design.Space4));
            return v;
        }

        public static View Divider(Context ctx, bool night, int inset = Design.Space5)
        {
            var c = new Colors(ctx);
            var v = new View(ctx);
            v.SetBackgroundColor(new Color(Design.Color(night, Light.Separator, Dark.Separator)));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Design.Dp(ctx, 1));
            lp.LeftMargin = Design.Dp(ctx, inset);
            lp.RightMargin = Design.Dp(ctx, inset);
            v.LayoutParameters = lp;
            return v;
        }

        public static void ApplySystemBars(Activity act)
        {
            if (act?.Window == null) return;
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop) return;
                var c = new Colors(act);
                act.Window.SetStatusBarColor(new Color(c.Background));
                act.Window.SetNavigationBarColor(new Color(c.Background));
                var decor = act.Window.DecorView;
                if (decor == null) return;
                int flags = (int)SystemUiFlags.Visible;
                if (!c.Night) flags |= (int)SystemUiFlags.LightStatusBar;
                decor.SystemUiVisibility = (StatusBarVisibility)flags;
            }
            catch { }
        }
    }

    // ===== 通用 Toast =====
    public static void Toast(Context ctx, string msg, ToastLength len = ToastLength.Short)
    {
        try { L10n.Toast(ctx, msg, len).Show(); }
        catch { }
    }

    public static void Copy(Context ctx, string text, string successMsg = L10n.T("已复制"))
    {
        try
        {
            var cm = (ClipboardManager)ctx.GetSystemService(Context.ClipboardService);
            cm.PrimaryClip = ClipData.NewPlainText("copy", text);
            Toast(ctx, successMsg);
        }
        catch { }
    }
}
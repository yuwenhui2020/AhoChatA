using Android.App;
using Android.Content;
using Android.Content.Res;

namespace AhoChatA
{
    /// <summary>
    /// 全局日间 / 夜间主题管理。
    /// 偏好存于应用级 SharedPreferences（Mchat / theme），取值 day | night | auto。
    /// auto 跟随系统「深色模式」开关（UiMode）。
    /// 所有 Activity 经 BaseActivity 在 OnCreate 里调用 Apply，统一切换框架主题；
    /// 网页端（www / webui）首屏由 WebServer 注入默认值，随后由前端 MTheme 接管。
    /// </summary>
    public static class ThemeManager
    {
        public const string KEY = "Mchat/theme";
        public const string DAY = "day";
        public const string NIGHT = "night";
        public const string AUTO = "auto";

        public static string GetMode(Context ctx)
        {
            try
            {
                var sp = ctx.GetSharedPreferences("Mchat", FileCreationMode.Private);
                return sp.GetString(KEY, AUTO) ?? AUTO;
            }
            catch { return AUTO; }
        }

        public static void SetMode(Context ctx, string mode)
        {
            if (mode != DAY && mode != NIGHT && mode != AUTO) mode = AUTO;
            try
            {
                ctx.GetSharedPreferences("Mchat", FileCreationMode.Private)
                   .Edit().PutString(KEY, mode).Apply();
            }
            catch { }
        }

        public static bool IsNight(Context ctx)
        {
            var m = GetMode(ctx);
            if (m == NIGHT) return true;
            if (m == DAY) return false;
            try
            {
                var ui = ctx.Resources.Configuration.UiMode & UiMode.NightMask;
                return ui == UiMode.NightYes;
            }
            catch { return false; }
        }

        public static int ThemeResId(Context ctx)
        {
            return IsNight(ctx)
                ? Resource.Style.AppTheme_NoActionBar_Night
                : Resource.Style.AppTheme_NoActionBar;
        }

        /// <summary>在 Activity.OnCreate 里、base.OnCreate 之前调用，切换当前窗口主题。</summary>
        public static void Apply(Activity a)
        {
            try { a.SetTheme(ThemeResId(a)); } catch { }
        }

        /// <summary>给网页端注入用的解析值（day / night）。</summary>
        public static string Resolved(Context ctx) => IsNight(ctx) ? NIGHT : DAY;
    }
}

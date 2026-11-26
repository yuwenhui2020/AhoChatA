// ============================================================
// 开屏页（Splash）
//
// 原实现只靠 theme 的 windowBackground（@drawable/splash_bg）在冷启动时
// 画一帧，用户无法关闭或换成自己的图片。这里改成真正的启动 Activity：
//   · 默认显示内置品牌开屏（splash_bg）；
//   · 用户可在「我的 → 其他 → 开屏页」选择自定义图片；
//   · 可整体关闭（关闭后启动直接进主界面，不再展示品牌页）。
//
// 图片存应用私有目录，不需要任何存储权限；设置项与图片路径均持久化。
// ============================================================
using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>开屏页设置：是否启用、自定义图片的读写。</summary>
    public static class SplashSettings
    {
        private const string Pref = "Mchat";
        private const string KeyEnabled = "splashEnabled";
        private const string FileName = "splash_custom.img";

        private static Context Ctx
        {
            get { try { return Android.App.Application.Context; } catch { return null; } }
        }

        /// <summary>是否显示开屏页（默认开启）。</summary>
        public static bool Enabled
        {
            get
            {
                try { return Ctx.GetSharedPreferences(Pref, FileCreationMode.Private).GetBoolean(KeyEnabled, true); }
                catch { return true; }
            }
        }

        public static void SetEnabled(bool on)
        {
            try
            {
                Ctx.GetSharedPreferences(Pref, FileCreationMode.Private)
                   .Edit().PutBoolean(KeyEnabled, on).Apply();
            }
            catch { }
        }

        /// <summary>自定义开屏图片（应用私有文件）。</summary>
        public static string ImagePath
        {
            get
            {
                try { return System.IO.Path.Combine(Ctx.FilesDir.AbsolutePath, FileName); }
                catch { return null; }
            }
        }

        public static bool HasCustomImage
        {
            get
            {
                try
                {
                    var p = ImagePath;
                    return !string.IsNullOrEmpty(p) && File.Exists(p);
                }
                catch { return false; }
            }
        }

        public static bool SaveCustomImage(byte[] data)
        {
            try
            {
                var p = ImagePath;
                if (string.IsNullOrEmpty(p) || data == null || data.Length == 0) return false;
                File.WriteAllBytes(p, data);
                return true;
            }
            catch { return false; }
        }

        public static void ClearCustomImage()
        {
            try
            {
                var p = ImagePath;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) TrashManager.Instance.MoveToTrash(p);
            }
            catch { }
        }
    }

    /// <summary>启动首屏：显示开屏页（可选自定义图片），随后进入主界面。</summary>
    [Activity(Label = "@string/app_name",
              MainLauncher = true,
              Exported = true,
              NoHistory = true,
              Theme = "@style/AppTheme.Splash",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class SplashActivity : Activity
    {
        // 只做很短的停留：主界面本身已预初始化（AhoChatApp.Preinit），
        // 过长会直接拖慢启动观感，这里只保证不出现「一帧闪屏」。
        private const int MinShowMs = 200;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.Apply(this);   // 跟随日/夜主题，避免夜间闪白
            base.OnCreate(savedInstanceState);

            // 关闭开屏页：直接进主界面，不渲染任何开屏内容
            if (!SplashSettings.Enabled)
            {
                LaunchMain();
                return;
            }

            var root = new FrameLayout(this);
            root.SetBackgroundColor(ThemeManager.IsNight(this)
                ? Color.ParseColor("#0E1116")   // 夜间底色（与 AppTheme.NoActionBar.Night 一致）
                : Color.ParseColor("#F2F3F5")); // 与默认开屏底色一致，避免色差跳变

            if (SplashSettings.HasCustomImage)
            {
                var img = new ImageView(this);
                img.SetScaleType(ImageView.ScaleType.CenterCrop);
                var bmp = DecodeSplash(SplashSettings.ImagePath);
                if (bmp != null) img.SetImageBitmap(bmp);
                root.AddView(img, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            }
            else
            {
                // 默认品牌开屏：复用 splash_bg（浅灰底 + 居中品牌标记）
                try { root.SetBackgroundResource(Resource.Drawable.splash_bg); } catch { }
            }

            SetContentView(root);
            new Handler(Looper.MainLooper).PostDelayed(LaunchMain, MinShowMs);
        }

        /// <summary>按屏幕尺寸降采样解码，避免大图 OOM。</summary>
        private static Bitmap DecodeSplash(string path)
        {
            try
            {
                var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, probe);

                int max = 1600;
                int sample = 1;
                while (probe.OutWidth / sample > max || probe.OutHeight / sample > max) sample *= 2;

                var opt = new BitmapFactory.Options { InSampleSize = sample };
                return BitmapFactory.DecodeFile(path, opt);
            }
            catch { return null; }
        }

        private void LaunchMain()
        {
            try
            {
                // 主界面已经存在（例如从后台点图标回来）：直接结束开屏，回到原界面，避免重建状态
                if (ChatActivity.IsAlive)
                {
                    Finish();
                    return;
                }

                StartActivity(new Intent(this, typeof(ChatActivity)));
                try { OverridePendingTransition(Android.Resource.Animation.FadeIn, Android.Resource.Animation.FadeOut); } catch { }
                // 程序更新：一周一次的自动检测（延时在 UpdateManager 内部处理）
                try { UpdateManager.ScheduleAutoCheck(); } catch { }
                // 局域网文件共享默认带上一份本程序安装包（后台复制，不阻塞启动）
                try
                {
                    Task.Run(() => ShareAppHelper.CopyInstaller(ApplicationContext, true));
                }
                catch { }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Splash", "进入主界面失败: " + ex.Message);
            }
            Finish();
        }
    }
}

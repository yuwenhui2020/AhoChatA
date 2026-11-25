using Android.App;
using Android.OS;

namespace AhoChatA
{
    /// <summary>
    /// 统一基类：在 OnCreate 里应用全局日间 / 夜间主题，再交给具体 Activity 构建界面。
    /// 所有界面 Activity 改为继承此类即可自动跟随 ThemeManager 的主题设置。
    ///
    /// 界面构建完成后（Post 到消息循环末尾，此时子类 OnCreate 已把视图加好），
    /// 由 <see cref="ThemeEngine"/> 遍历视图树做「浅色→深色 / 深字→浅字」的运行时重上色，
    /// 这样无需在每个界面里判断主题，也不依赖 Activity 重建（避免切换时闪烁）。
    /// </summary>
    public class BaseActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.Apply(this);
            base.OnCreate(savedInstanceState);
            IosStyle.ApplySystemBars(this);

            var decor = Window == null ? null : Window.DecorView;
            if (decor != null)
            {
                // 先上色、再翻译：界面此时已由子类 OnCreate 建好，一次性把静态文案翻成当前语言。
                decor.Post(new Java.Lang.Runnable(() =>
                {
                    ThemeEngine.Apply(this);
                    L10n.Apply(decor);
                }));
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            // 从设置页改完主题返回时，重新套一遍配色（幂等，不会叠加）。
            ThemeEngine.Apply(this);
            IosStyle.StyleCheckBoxes(this);
            IosStyle.ApplySystemBars(this);
            // 回到前台时再翻一遍：动态文案（在线数、进度等）在 OnResume 之后才刷新，
            // 这里补一次，切换语言后返回也能立即生效。
            if (Window != null) L10n.Apply(Window.DecorView);
        }

        /// <summary>主题在运行时被切换后调用：立即重刷当前界面，不重建 Activity（消除闪烁）。</summary>
        public void ApplyThemeLive()
        {
            ThemeEngine.Apply(this);
            IosStyle.StyleCheckBoxes(this);
            IosStyle.ApplySystemBars(this);
            try { OnThemeChanged(); } catch { }
        }

        /// <summary>
        /// 主题刚被切换（含从网页端/设置页切换）时回调，供子类同步自己的动态元素
        /// （例如顶栏那个「太阳 / 月亮」图标的形态）。
        /// </summary>
        protected virtual void OnThemeChanged() { }
    }
}

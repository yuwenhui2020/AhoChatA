// ============================================================
// L10n —— AhoChat 的运行时多语言层
//
// 为什么不用 Android 的 strings.xml 全量改造？
//   本项目界面几乎全部由 C# 代码动态构建（DesignSystem / IosStyle），
//   文案以中文字面量的形式散落在 200+ 个文件里。改成 @string/xxx 需要
//   把上千个调用点手工改写成 GetString(Resource.String.xxx)，改动量极大
//   且容易回归。这里采用「中文字面量 + 英文词典」的运行时方案：
//
//   · 中文模式：T() 原样返回，行为与改造前完全一致（零风险）；
//   · 英文模式：查词典翻译，查不到就回退中文，不会再抛异常；
//   · 语言跟随系统（zh* → 中文，其余 → English），也可在设置里锁定。
//
// 三处生效点：
//   1. 界面构建完成后遍历视图树（Apply），把静态文案整体翻译一遍；
//   2. Toast / 对话框统一走 L10n.Toast / L10n.Dialog，弹出前翻译；
//   3. 动态刷新的文案通过 L10n.T("中文") 在内联处翻译。
// ============================================================
using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    public static class L10n
    {
        private const string Pref = "Mchat";
        private const string KeyLang = "uiLang";   // ""=跟随系统 / "zh" / "en"

        private static string _override;
        private static bool _loaded;
        private static bool _chinese;
        private static Dictionary<string, string> _map;

        /// <summary>当前是否中文界面。</summary>
        public static bool IsChinese
        {
            get { EnsureLoaded(); return _chinese; }
        }

        /// <summary>当前语言代码：zh / en。</summary>
        public static string Current => IsChinese ? "zh" : "en";

        /// <summary>手动锁定的语言；null 表示跟随系统。</summary>
        public static string Override { get { EnsureLoaded(); return _override; } }

        /// <summary>手动锁定语言（null/"" = 跟随系统）。</summary>
        public static void SetOverride(string lang)
        {
            _override = string.IsNullOrEmpty(lang) ? null : lang;
            try
            {
                Android.App.Application.Context
                    .GetSharedPreferences(Pref, FileCreationMode.Private)
                    .Edit().PutString(KeyLang, _override ?? "").Apply();
            }
            catch { }
            _chinese = ComputeChinese();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                _override = Android.App.Application.Context
                    .GetSharedPreferences(Pref, FileCreationMode.Private)
                    .GetString(KeyLang, "");
                if (string.IsNullOrEmpty(_override)) _override = null;
            }
            catch { _override = null; }
            _chinese = ComputeChinese();
        }

        private static bool ComputeChinese()
        {
            if (_override == "zh") return true;
            if (_override == "en") return false;
            try
            {
                // 跟随系统语言：zh / zh-CN / zh-Hans / zh-TW 都算中文
                var lang = Java.Util.Locale.Default.Language ?? "";
                return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private static Dictionary<string, string> Map
        {
            get { return _map ?? (_map = L10nDict.Build()); }
        }

        /// <summary>翻译一段中文文案；英文模式下查不到词典就原样返回。</summary>
        public static string T(string zh)
        {
            if (string.IsNullOrEmpty(zh) || IsChinese) return zh;
            string en;
            return Map.TryGetValue(zh, out en) ? en : zh;
        }

        /// <summary>成对给出中英文，适合新增代码直接内联。</summary>
        public static string T(string zh, string en)
        {
            if (IsChinese) return zh;
            return string.IsNullOrEmpty(en) ? zh : en;
        }

        /// <summary>带参数的格式化翻译：先翻译模板，再套参数。</summary>
        public static string F(string zhFormat, params object[] args)
        {
            try { return string.Format(T(zhFormat), args); }
            catch { return zhFormat; }
        }

        /// <summary>等价于 Toast.MakeText，但文案会翻译。</summary>
        public static Toast Toast(Context ctx, string text, ToastLength length)
        {
            return Android.Widget.Toast.MakeText(ctx, T(text), length);
        }

        public static Toast Toast(Context ctx, string text)
        {
            return Android.Widget.Toast.MakeText(ctx, T(text), ToastLength.Short);
        }

        /// <summary>等价于 new AlertDialog.Builder(ctx)，弹出前自动翻译整棵视图树。</summary>
        public static AlertDialog.Builder Dialog(Context ctx)
        {
            return new L10nDialogBuilder(ctx);
        }

        /// <summary>翻译一个对话框（用于非 Builder 方式创建的对话框）。</summary>
        public static void ApplyDialog(AlertDialog dlg)
        {
            if (dlg == null || IsChinese) return;
            try
            {
                var win = dlg.Window;
                if (win != null && win.DecorView != null) Apply(win.DecorView);
            }
            catch { }
        }

        /// <summary>把整棵视图树上的静态文案翻译成英文（幂等）。</summary>
        public static void Apply(View root)
        {
            if (root == null || IsChinese) return;
            try { Walk(root, 0); } catch { }
        }

        private static void Walk(View v, int depth)
        {
            if (v == null || depth > 64) return;
            try
            {
                var tv = v as TextView;
                if (tv != null)
                {
                    var text = tv.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var nt = T(text);
                        if (!string.Equals(nt, text, StringComparison.Ordinal)) tv.Text = nt;
                    }
                    var hint = tv.Hint;
                    if (!string.IsNullOrEmpty(hint))
                    {
                        var nh = T(hint);
                        if (!string.Equals(nh, hint, StringComparison.Ordinal)) tv.Hint = nh;
                    }
                    var cd = tv.ContentDescription;
                    if (!string.IsNullOrEmpty(cd))
                    {
                        var nc = T(cd);
                        if (!string.Equals(nc, cd, StringComparison.Ordinal)) tv.ContentDescription = nc;
                    }
                }

                var group = v as ViewGroup;
                if (group != null)
                {
                    int n = group.ChildCount;
                    for (int i = 0; i < n; i++) Walk(group.GetChildAt(i), depth + 1);
                }
            }
            catch { }
        }
    }

    /// <summary>弹出前自动翻译的 AlertDialog.Builder。</summary>
    public class L10nDialogBuilder : AlertDialog.Builder
    {
        public L10nDialogBuilder(Context ctx) : base(ctx) { }

        public override AlertDialog Show()
        {
            var dlg = base.Show();
            L10n.ApplyDialog(dlg);
            return dlg;
        }
    }
}

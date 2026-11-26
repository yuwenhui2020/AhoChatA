// ============================================================
// 聊天里的「程序更新卡片」：版本 + 更新说明 + 下载状态 + 一键安装按钮。
// 结构化数据由 UpdateManager 以 JSON 放在 msg.text 里（加密链路只保留 text/kind）。
// ============================================================
using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace AhoChatA
{
    public class UpdateCardView : LinearLayout
    {
        private readonly Activity _act;
        private readonly IDictionary<string, object> _card;
        private string _apk = "";

        public UpdateCardView(Activity act, IDictionary<string, object> msg) : base(act)
        {
            _act = act;
            _card = UpdateManager.ParseCard(Msg.Str(msg, "text", "")) ?? new Dictionary<string, object>();
            Orientation = Orientation.Vertical;
            var bg = new Android.Graphics.Drawables.GradientDrawable();
            bg.SetColor(unchecked((int)0xFFFFFFFF));
            bg.SetCornerRadius(Dp(12));
            bg.SetStroke(1, new Color(unchecked((int)0xFFE8EAEE)));
            Background = bg;
            SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
            Build();
        }

        private int Dp(float v)
        {
            return (int)(v * _act.Resources.DisplayMetrics.Density + 0.5f);
        }

        private void Build()
        {
            string title = Msg.Str(_card, "title", L10n.T("发现新版本", "New version available"));
            string version = Msg.Str(_card, "version", "");
            string notes = Msg.Str(_card, "notes", "");
            string status = Msg.Str(_card, "status", "");
            _apk = Msg.Str(_card, "apk", "");
            string home = Msg.Str(_card, "home", "https://github.com/yuwenhui2020/AhoChatA");

            var head = new TextView(_act)
            {
                Text = L10n.T("程序更新", "App update") + (string.IsNullOrEmpty(version) ? "" : "  " + version),
                TextSize = 15
            };
            head.SetTextColor(new Color(unchecked((int)0xFF111111)));
            head.SetTypeface(null, TypefaceStyle.Bold);
            AddView(head);

            var t = new TextView(_act) { Text = title, TextSize = 13 };
            t.SetTextColor(new Color(unchecked((int)0xFF007AFF)));
            t.SetPadding(0, Dp(4), 0, Dp(2));
            AddView(t);

            if (!string.IsNullOrEmpty(notes))
            {
                var n = new TextView(_act) { Text = notes, TextSize = 13 };
                n.SetTextColor(new Color(unchecked((int)0xFF555555)));
                n.SetPadding(0, Dp(4), 0, Dp(2));
                AddView(n);
            }
            if (!string.IsNullOrEmpty(status))
            {
                var s = new TextView(_act) { Text = status, TextSize = 12 };
                s.SetTextColor(new Color(unchecked((int)0xFF8E8E93)));
                s.SetPadding(0, Dp(2), 0, Dp(4));
                AddView(s);
            }

            var row = new LinearLayout(_act) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.Right);
            if (!string.IsNullOrEmpty(_apk))
                row.AddView(Pill(L10n.T("安装", "Install"), unchecked((int)0xFFE8F7FE), unchecked((int)0xFF007AFF), () => UpdateManager.InstallApk(_apk)));
            row.AddView(Pill(L10n.T("主页", "Home"), unchecked((int)0xFFF0F0F0), unchecked((int)0xFF555555), () =>
            {
                try { BrowserActivity.Open(_act, home); } catch { }
            }));
            AddView(row);
        }

        private TextView Pill(string text, int bgColor, int fgColor, Action click)
        {
            var b = new TextView(_act) { Text = text, TextSize = 12, Gravity = GravityFlags.Center };
            b.SetTextColor(new Color(fgColor));
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor(bgColor);
            d.SetCornerRadius(Dp(14));
            b.Background = d;
            b.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
            b.Click += (s, e) => click?.Invoke();
            return b;
        }
    }
}

// ============================================================
// iOS 原生化组件库（P0 公共底座）
//
// 与 IosStyle.cs 的分工：
//   IosStyle.cs     —— 设计令牌 + 轻量样式函数（无状态、不改视图树结构）
//   IosComponents.cs —— 有状态的复合控件（分段控件 / 搜索栏 / 导航栏 / 标签栏 / 开关 / 分组行）
//
// 设计原则：
//   1. 所有颜色走 IosStyle.Colors(Context)，夜间模式跟随系统主题；
//      同时因为用 GradientDrawable / TextView，ThemeEngine 的夜间映射也能兜底。
//   2. 控件只提供外观与交互，不耦合任何业务页面；各模块按需替换现有自制控件。
//   3. 尺寸全部经 IosStyle.Dp 转换，适配不同密度。
// ============================================================
using System;
using System.Collections.Generic;
using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>iOS 分段控件（UISegmentedControl）：灰色轨道 + 白色滑块 + 滑动动画。</summary>
    public sealed class IosSegmentedControl : FrameLayout
    {
        private readonly View _thumb;
        private readonly LinearLayout _row;
        private readonly List<TextView> _segments = new List<TextView>();
        private int _selected;
        private int _segmentWidth;
        private int _inset;

        public event EventHandler<int> SelectionChanged;

        public int SelectedIndex
        {
            get { return _selected; }
            set { Select(value, false); }
        }

        public IosSegmentedControl(Context context) : base(context)
        {
            _inset = IosStyle.Dp(context, 2);
            var p = IosStyle.Colors(context);
            Background = IosStyle.Rounded(context, p.Night ? unchecked((int)0xFF3A3A3C) : unchecked((int)0xFFE9E9ED),
                IosStyle.ControlRadius);

            _thumb = new View(context);
            _thumb.Background = IosStyle.Rounded(context, p.Night ? unchecked((int)0xFF636366) : Color.White.ToArgb(),
                IosStyle.ControlRadius - 2);
            AddView(_thumb, new FrameLayout.LayoutParams(0, FrameLayout.LayoutParams.MatchParent));

            _row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            _row.LayoutParameters = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent);
            AddView(_row);
        }

        /// <summary>设置分段标题；可重复调用以跟随语言切换。</summary>
        public void SetItems(IList<string> items)
        {
            _row.RemoveAllViews();
            _segments.Clear();
            if (items == null || items.Count == 0) return;
            int n = items.Count;
            for (int i = 0; i < n; i++)
            {
                var tv = new TextView(Context)
                {
                    Text = items[i] ?? "",
                    Gravity = GravityFlags.Center,
                    TextSize = IosStyle.BodySize
                };
                tv.SetMaxLines(1);
                tv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                tv.SetPadding(IosStyle.Dp(Context, 4), 0, IosStyle.Dp(Context, 4), 0);
                tv.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MatchParent, 1f);
                int index = i;
                tv.Click += (s, e) => Select(index, true);
                _segments.Add(tv);
                _row.AddView(tv);
            }
            if (_selected >= n) _selected = n - 1;
            RequestLayout();
            RefreshText();
        }

        protected override void OnLayout(bool changed, int l, int t, int r, int b)
        {
            base.OnLayout(changed, l, t, r, b);
            int n = _segments.Count;
            if (n == 0) return;
            int usable = Math.Max(0, (r - l) - _inset * 2);
            _segmentWidth = usable / n;
            var lp = _thumb.LayoutParameters as FrameLayout.LayoutParams;
            if (lp != null)
            {
                lp.Width = _segmentWidth;
                lp.Height = FrameLayout.LayoutParams.MatchParent;
                lp.LeftMargin = _inset;
                lp.TopMargin = _inset;
                lp.BottomMargin = _inset;
                _thumb.LayoutParameters = lp;
            }
            _thumb.TranslationX = _selected * _segmentWidth;
        }

        private void Select(int index, bool notify)
        {
            if (_segments.Count == 0) return;
            if (index < 0) index = 0;
            if (index >= _segments.Count) index = _segments.Count - 1;
            int old = _selected;
            _selected = index;
            RefreshText();
            if (_segmentWidth > 0)
            {
                try
                {
                    ObjectAnimator.OfFloat(_thumb, "translationX", old * _segmentWidth, index * _segmentWidth)
                        .SetDuration(180).Start();
                }
                catch { _thumb.TranslationX = index * _segmentWidth; }
            }
            if (notify && old != index)
            {
                try { SelectionChanged?.Invoke(this, index); } catch { }
            }
        }

        private void RefreshText()
        {
            var p = IosStyle.Colors(Context);
            for (int i = 0; i < _segments.Count; i++)
            {
                var tv = _segments[i];
                bool on = i == _selected;
                tv.SetTextColor(new Color(on ? p.Text : p.Secondary));
                tv.SetTypeface(null, on ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            }
        }
    }

    /// <summary>iOS 搜索栏（UISearchBar）：圆角灰底 + 放大镜 + 输入框 + 框内清除。</summary>
    public sealed class IosSearchBar : LinearLayout
    {
        public EditText Input { get; private set; }

        public string Text
        {
            get { return Input == null ? "" : (Input.Text ?? ""); }
            set { if (Input != null) Input.Text = value ?? ""; }
        }

        public event EventHandler<string> TextChanged;

        public IosSearchBar(Context context, string hint) : base(context)
        {
            Orientation = Orientation.Horizontal;
            var p = IosStyle.Colors(context);
            Background = IosStyle.Rounded(context, p.Night ? unchecked((int)0xFF1C1C1E) : unchecked((int)0xFFE9E9ED),
                IosStyle.ControlRadius);
            SetGravity(GravityFlags.CenterVertical);
            SetPadding(IosStyle.Dp(context, 10), 0, IosStyle.Dp(context, 6), 0);
            LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);

            var glass = new IconView(context, IconKind.Search)
            {
                IconColor = p.Secondary,
                Thickness = 1.7f
            };
            AddView(glass, new LinearLayout.LayoutParams(IosStyle.Dp(context, 18), IosStyle.Dp(context, 18))
            {
                RightMargin = IosStyle.Dp(context, 6)
            });

            Input = new EditText(context)
            {
                Hint = hint ?? "",
                TextSize = IosStyle.BodySize,
                Background = null
            };
            Input.SetSingleLine(true);
            Input.SetMinHeight(IosStyle.Dp(context, IosStyle.ControlHeight - 6));
            Input.SetTextColor(new Color(p.Text));
            Input.SetHintTextColor(new Color(p.Secondary));
            Input.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
            Input.TextChanged += (s, e) =>
            {
                try { TextChanged?.Invoke(this, Text); } catch { }
            };
            AddView(Input);

            var clear = new IconView(context, IconKind.Close)
            {
                IconColor = p.Secondary,
                Thickness = 1.7f
            };
            clear.Click += (s, e) => { Input.Text = ""; };
            AddView(clear, new LinearLayout.LayoutParams(IosStyle.Dp(context, 28), IosStyle.Dp(context, 28)));
        }
    }

    /// <summary>iOS 大标题导航栏：返回 + 标题 + 右侧动作区 + 大标题 + 细分隔线。</summary>
    public sealed class IosNavBar : LinearLayout
    {
        private readonly TextView _title;
        private readonly TextView _largeTitle;
        private readonly TextView _subtitle;
        private readonly LinearLayout _actions;
        private readonly View _back;

        public event EventHandler BackClicked;

        public string Title
        {
            get { return _title.Text ?? ""; }
            set { _title.Text = value ?? ""; _largeTitle.Text = value ?? ""; }
        }

        public string Subtitle
        {
            get { return _subtitle.Text ?? ""; }
            set
            {
                _subtitle.Text = value ?? "";
                _subtitle.Visibility = string.IsNullOrEmpty(value) ? ViewStates.Gone : ViewStates.Visible;
            }
        }

        public bool ShowLargeTitle
        {
            get { return _largeTitle.Visibility == ViewStates.Visible; }
            set { _largeTitle.Visibility = value ? ViewStates.Visible : ViewStates.Gone; }
        }

        public LinearLayout Actions { get { return _actions; } }

        public IosNavBar(Context context, string title) : base(context)
        {
            Orientation = Orientation.Vertical;
            var p = IosStyle.Colors(context);
            Background = IosStyle.Rounded(context, p.Surface, 0);

            var top = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            top.SetMinimumHeight(IosStyle.Dp(context, IosStyle.ToolbarHeight));
            top.SetPadding(IosStyle.Dp(context, 8), 0, IosStyle.Dp(context, 8), 0);
            top.LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);

            _back = new IconView(context, IconKind.Back) { IconColor = p.Accent, Thickness = 2f };
            _back.Click += (s, e) => { try { BackClicked?.Invoke(this, EventArgs.Empty); } catch { } };
            top.AddView(_back, new LinearLayout.LayoutParams(IosStyle.Dp(context, 44), IosStyle.Dp(context, 44)));

            _title = new TextView(context)
            {
                Text = title ?? "",
                TextSize = IosStyle.TitleSize,
                Gravity = GravityFlags.Center
            };
            _title.SetMaxLines(1);
            _title.SetTextColor(new Color(p.Text));
            _title.SetTypeface(null, TypefaceStyle.Bold);
            _title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _title.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
            top.AddView(_title);

            _actions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            _actions.SetGravity(GravityFlags.CenterVertical);
            _actions.LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent);
            top.AddView(_actions);
            AddView(top);

            _largeTitle = new TextView(context)
            {
                Text = title ?? "",
                TextSize = 34f
            };
            _largeTitle.SetMaxLines(1);
            _largeTitle.SetTextColor(new Color(p.Text));
            _largeTitle.SetTypeface(null, TypefaceStyle.Bold);
            _largeTitle.SetPadding(IosStyle.Dp(context, IosStyle.PageInset), IosStyle.Dp(context, 2),
                IosStyle.Dp(context, IosStyle.PageInset), IosStyle.Dp(context, 2));
            _largeTitle.LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
            AddView(_largeTitle);

            _subtitle = new TextView(context) { TextSize = IosStyle.CaptionSize };
            _subtitle.SetTextColor(new Color(p.Secondary));
            _subtitle.SetPadding(IosStyle.Dp(context, IosStyle.PageInset), 0,
                IosStyle.Dp(context, IosStyle.PageInset), IosStyle.Dp(context, 6));
            _subtitle.Visibility = ViewStates.Gone;
            _subtitle.LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
            AddView(_subtitle);

            AddView(IosStyle.Divider(context, p, 0));
        }
    }

    /// <summary>iOS 分组表格行：图标 + 主标题 + 副标题 + 右侧值 + 箭头，行间细分隔线。</summary>
    public static class IosRow
    {
        public static LinearLayout Create(Context context, IconKind? icon, string title, string subtitle,
            string trailing, bool chevron, EventHandler onClick)
        {
            var p = IosStyle.Colors(context);
            var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(IosStyle.Dp(context, IosStyle.RowMinHeight));
            row.SetPadding(IosStyle.Dp(context, IosStyle.CardInset), IosStyle.Dp(context, 6),
                IosStyle.Dp(context, IosStyle.CardInset), IosStyle.Dp(context, 6));
            row.LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);

            if (icon.HasValue)
            {
                var box = new LinearLayout(context);
                box.Background = IosStyle.Rounded(context,
                    p.Night ? unchecked((int)0xFF3A3A3C) : unchecked((int)0xFFE9E9ED), 7);
                box.SetGravity(GravityFlags.Center);
                var iv = new IconView(context, icon.Value) { IconColor = p.Accent, Thickness = 1.8f };
                box.AddView(iv, new LinearLayout.LayoutParams(IosStyle.Dp(context, 18), IosStyle.Dp(context, 18)));
                row.AddView(box, new LinearLayout.LayoutParams(IosStyle.Dp(context, 30), IosStyle.Dp(context, 30))
                {
                    RightMargin = IosStyle.Dp(context, 12)
                });
            }

            var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
            texts.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
            var t = new TextView(context)
            {
                Text = title ?? "",
                TextSize = IosStyle.BodySize
            };
            t.SetMaxLines(1);
            t.SetTextColor(new Color(p.Text));
            t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            texts.AddView(t, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));
            if (!string.IsNullOrEmpty(subtitle))
            {
                var s = new TextView(context) { Text = subtitle, TextSize = IosStyle.CaptionSize };
                s.SetMaxLines(1);
                s.SetTextColor(new Color(p.Secondary));
                s.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                texts.AddView(s, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));
            }
            row.AddView(texts);

            if (!string.IsNullOrEmpty(trailing))
            {
                var v = new TextView(context)
                {
                    Text = trailing,
                    TextSize = IosStyle.BodySize,
                    Gravity = GravityFlags.End
                };
                v.SetMaxLines(1);
                v.SetTextColor(new Color(p.Secondary));
                v.LayoutParameters = new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
                {
                    LeftMargin = IosStyle.Dp(context, 8)
                };
                row.AddView(v);
            }

            if (chevron)
            {
                var c = new IconView(context, IconKind.ChevronRight)
                {
                    IconColor = p.Secondary,
                    Thickness = 1.8f
                };
                row.AddView(c, new LinearLayout.LayoutParams(IosStyle.Dp(context, 18), IosStyle.Dp(context, 18))
                {
                    LeftMargin = IosStyle.Dp(context, 6)
                });
            }

            if (onClick != null) row.Click += onClick;
            return row;
        }
    }

    /// <summary>iOS 底部标签栏（UITabBar）：图标 + 文字，选中蓝色，顶部细线。</summary>
    public sealed class IosTabBar : LinearLayout
    {
        private readonly List<TextView> _labels = new List<TextView>();
        private readonly List<IconView> _icons = new List<IconView>();
        private readonly List<View> _tabs = new List<View>();
        private int _selected;

        public event EventHandler<int> TabSelected;

        public int SelectedIndex
        {
            get { return _selected; }
            set { Select(value, false); }
        }

        public IosTabBar(Context context) : base(context)
        {
            Orientation = Orientation.Horizontal;
            var p = IosStyle.Colors(context);
            SetMinimumHeight(IosStyle.Dp(context, 56));
            Background = IosStyle.Rounded(context, p.Surface, 0);
            LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        }

        public void SetTabs(IList<string> titles, IList<IconKind> icons)
        {
            if (titles == null || icons == null || titles.Count != icons.Count) return;
            _labels.Clear(); _icons.Clear(); _tabs.Clear();
            RemoveAllViews();
            var p = IosStyle.Colors(Context);
            for (int i = 0; i < titles.Count; i++)
            {
                var tab = new LinearLayout(Context) { Orientation = Orientation.Vertical };
                tab.SetGravity(GravityFlags.Center);
                tab.SetPadding(0, IosStyle.Dp(Context, 6), 0, IosStyle.Dp(Context, 6));
                tab.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
                var iv = new IconView(Context, icons[i]) { IconColor = p.Secondary, Thickness = 1.7f };
                tab.AddView(iv, new LinearLayout.LayoutParams(IosStyle.Dp(Context, 24), IosStyle.Dp(Context, 24)));
                var tv = new TextView(Context)
                {
                    Text = titles[i],
                    TextSize = IosStyle.CaptionSize,
                    Gravity = GravityFlags.Center
                };
                tv.SetMaxLines(1);
                tv.SetTextColor(new Color(p.Secondary));
                tv.SetPadding(0, IosStyle.Dp(Context, 2), 0, 0);
                tab.AddView(tv, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent));
                int index = i;
                tab.Click += (s, e) => Select(index, true);
                _tabs.Add(tab); _icons.Add(iv); _labels.Add(tv);
                AddView(tab);
            }
            Refresh();
        }

        private void Select(int index, bool notify)
        {
            if (_tabs.Count == 0) return;
            if (index < 0) index = 0;
            if (index >= _tabs.Count) index = _tabs.Count - 1;
            _selected = index;
            Refresh();
            if (notify)
            {
                try { TabSelected?.Invoke(this, index); } catch { }
            }
        }

        private void Refresh()
        {
            var p = IosStyle.Colors(Context);
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = i == _selected;
                var c = on ? p.Accent : p.Secondary;
                _icons[i].IconColor = c;
                _icons[i].Invalidate();
                _labels[i].SetTextColor(new Color(c));
                _labels[i].SetTypeface(null, on ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            }
        }
    }

    /// <summary>iOS 开关（UISwitch）：自绘轨道 + 圆形滑块 + 平移动画。</summary>
    public sealed class IosSwitch : View
    {
        private readonly Paint _track = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _thumb = new Paint(PaintFlags.AntiAlias);
        private bool _checked;
        private float _pos;

        public bool Checked
        {
            get { return _checked; }
            set { SetChecked(value, false); }
        }

        public event EventHandler<bool> CheckedChanged;

        public IosSwitch(Context context) : base(context)
        {
            SetWillNotDraw(false);
            Clickable = true;
            ApplyColor(IosStyle.Colors(context));
            Click += (s, e) => SetChecked(!_checked, true);
        }

        private void ApplyColor(IosStyle.Palette p)
        {
            _track.SetColor(new Color(_checked ? p.Accent
                : (p.Night ? unchecked((int)0xFF39393D) : unchecked((int)0xFFE9E9ED))));
            _track.SetStyle(Paint.Style.Fill);
            _thumb.SetColor(Color.White.ToArgb());
            _thumb.SetStyle(Paint.Style.Fill);
            _thumb.SetShadowLayer(IosStyle.Dp(Context, 2), 0, IosStyle.Dp(Context, 1), unchecked((int)0x33000000));
        }

        private void SetChecked(bool value, bool notify)
        {
            if (_checked == value) return;
            _checked = value;
            ApplyColor(IosStyle.Colors(Context));
            float target = value ? 1f : 0f;
            try
            {
                ObjectAnimator.OfFloat(this, "ThumbPos", _pos, target).SetDuration(180).Start();
            }
            catch { ThumbPos = target; }
            Invalidate();
            if (notify)
            {
                try { CheckedChanged?.Invoke(this, value); } catch { }
            }
        }

        /// <summary>供 ObjectAnimator 使用的属性（0=关，1=开）。</summary>
        public float ThumbPos
        {
            get { return _pos; }
            set { _pos = value; Invalidate(); }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            float r = h / 2f;
            canvas.DrawRoundRect(new RectF(0, 0, w, h), r, r, _track);
            float cx = r + _pos * (w - 2 * r);
            canvas.DrawCircle(cx, r, r - IosStyle.Dp(Context, 2), _thumb);
        }
    }
}

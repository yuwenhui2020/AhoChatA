// ============================================================
// 影音模块 · 图片全屏预览
// 默认沉浸式全屏、无任何 UI；点屏幕正中间展开菜单（删除 / 分享 / 重命名 / 详情），
// 点左右两侧或横向滑动切上一张 / 下一张，双击放大还原。
// 详情展示修改时间、所在目录、文件名称；点文件名称可直接重命名。
// ============================================================
using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AhoChatA.Music;
using IoPath = System.IO.Path;

namespace AhoChatA
{
    [Activity(Label = "@string/act_photo", Exported = false,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize,
              Theme = "@style/AppTheme.NoActionBar")]
    public class PhotoActivity : BaseActivity, View.IOnTouchListener
    {
        /// <summary>收到图片后由 MediaAutoOpen 直投的单文件预览路径。</summary>
        public const string ExtraPath = "photo_path";

        private static List<Photo> _pending;
        private static int _pendingIndex;

        private List<Photo> _items = new List<Photo>();
        private int _index;
        private ImageView _img;
        private LinearLayout _menu, _detail;
        private TextView _menuInfo, _dName, _dTime, _dFolder, _dExtra;
        private Bitmap _bmp;
        private float _downX, _downY;
        private long _downAt, _lastTap;
        private bool _zoom;

        private const int BgPanel = unchecked((int)0xE6101216);
        private const int TxtMain = unchecked((int)0xFFF2F4F8);
        private const int TxtSub = unchecked((int)0xFF9AA2B1);
        private const int AccentBlue = unchecked((int)0xFF007AFF);
        private const int DangerRed = unchecked((int)0xFFFF3B30);

        /// <summary>从图片网格打开预览；列表通过静态字段传递，避免 Intent 大小限制。</summary>
        public static void Show(Activity ctx, List<Photo> items, int index)
        {
            _pending = items ?? new List<Photo>();
            _pendingIndex = index;
            ctx.StartActivity(new Intent(ctx, typeof(PhotoActivity)));
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // 只全屏，不挂 KeepScreenOn：看图不是连续播放，没必要让屏幕一直亮着
            Window.AddFlags(WindowManagerFlags.Fullscreen);

            _items = _pending ?? new List<Photo>();
            _index = _pendingIndex;
            _pending = null;
            // 收到图片直投：单文件全屏预览（_pending 为空但带了 ExtraPath）
            var single = Intent?.GetStringExtra(ExtraPath);
            if ((_items.Count == 0 || _index < 0) && !string.IsNullOrEmpty(single))
            {
                _items = new List<Photo> { new Photo { Path = single, Name = IoPath.GetFileName(single) } };
                _index = 0;
            }
            if (_index < 0 || _index >= _items.Count) _index = 0;

            var root = new FrameLayout(this);
            root.SetBackgroundColor(new Color(unchecked((int)0xFF000000)));
            root.LayoutParameters = new ViewGroup.LayoutParams(-1, -1);

            _img = new ImageView(this);
            _img.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _img.SetScaleType(ImageView.ScaleType.FitCenter);
            _img.SetOnTouchListener(this);
            root.AddView(_img);

            // 菜单：默认完全隐藏，点中央才出现
            _menu = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _menu.SetBackgroundColor(new Color(BgPanel));
            _menu.LayoutParameters = new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom);
            _menu.SetPadding(Dp(12), Dp(12), Dp(12), Dp(18));
            _menu.Visibility = ViewStates.Gone;

            _menuInfo = new TextView(this) { TextSize = 13, Gravity = GravityFlags.Center };
            _menuInfo.SetTextColor(new Color(TxtSub));
            _menuInfo.SetPadding(0, 0, 0, Dp(10));
            _menu.AddView(_menuInfo);

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.AddView(MenuBtn(L10n.T("删除"), DeleteCurrent));
            row.AddView(MenuBtn(L10n.T("分享"), ShareCurrent));
            row.AddView(MenuBtn(L10n.T("分享到频道"), ShareToChannel));
            row.AddView(MenuBtn(L10n.T("重命名"), RenameCurrent));
            row.AddView(MenuBtn(L10n.T("详情"), ShowDetail));
            _menu.AddView(row);
            root.AddView(_menu);

            // 详情面板：文件名可点击直接重命名
            _detail = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _detail.SetBackgroundColor(new Color(BgPanel));
            _detail.LayoutParameters = new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom);
            _detail.SetPadding(Dp(18), Dp(14), Dp(18), Dp(22));
            _detail.Visibility = ViewStates.Gone;
            _detail.Click += (s, e) => { };   // 吃掉点击，避免穿透到图片

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);
            var h = new TextView(this) { Text = L10n.T("详情"), TextSize = 17 };
            h.SetTextColor(new Color(TxtMain));
            head.AddView(h, new LinearLayout.LayoutParams(0, -2, 1f));
            var closeWrap = new FrameLayout(this) { Clickable = true };
            closeWrap.SetMinimumWidth(Dp(44));
            closeWrap.SetMinimumHeight(Dp(44));
            closeWrap.SetPadding(Dp(10), Dp(2), Dp(2), Dp(2));
            var closeIcon = new IconView(this, IconKind.Close) { IconColor = TxtSub };
            closeWrap.AddView(closeIcon, new FrameLayout.LayoutParams(Dp(20), Dp(20)) { Gravity = GravityFlags.Center });
            closeWrap.Click += (s, e) => { _detail.Visibility = ViewStates.Gone; _menu.Visibility = ViewStates.Visible; };
            head.AddView(closeWrap);
            _detail.AddView(head);
            _detail.AddView(Divider());

            _dName = DetailRow(L10n.T("文件名称"), true, out var nameRow);
            nameRow.Click += (s, e) => RenameCurrent();
            nameRow.Background = Underline();
            var tip = new TextView(this) { Text = L10n.T("（点击文件名称可直接重命名）"), TextSize = 13 };
            tip.SetTextColor(new Color(TxtSub));
            tip.SetPadding(Dp(2), Dp(2), 0, Dp(6));
            _detail.AddView(tip);

            _dTime = DetailRow(L10n.T("修改时间"), false, out _);
            _dFolder = DetailRow(L10n.T("所在目录"), false, out _);
            _dExtra = DetailRow(L10n.T("大小 / 尺寸"), false, out _);
            root.AddView(_detail);

            SetContentView(root);
            HideSystemUi();
            ShowCurrent();
        }

        // 已在前台（SingleTop 复用）时收到图片直投：直接切到该张全屏预览
        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            try
            {
                var single = intent?.GetStringExtra(ExtraPath);
                if (string.IsNullOrEmpty(single) || !System.IO.File.Exists(single)) return;
                _items = new List<Photo> { new Photo { Path = single, Name = IoPath.GetFileName(single) } };
                _index = 0;
                ShowCurrent();
                HideSystemUi();
            }
            catch (Exception ex) { Android.Util.Log.Warn("Photo", "OnNewIntent 图片直投失败: " + ex.Message); }
        }

        /* ---------------- UI 构件 ---------------- */

        private TextView MenuBtn(string text, Action click)
        {
            var t = new TextView(this) { Text = text, TextSize = 13, Gravity = GravityFlags.Center };
            t.SetTextColor(new Color(text == "删除" ? DangerRed : AccentBlue));
            t.SetPadding(Dp(6), Dp(8), Dp(6), Dp(8));
            t.SetMinimumHeight(Dp(44));
            t.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            t.Click += (s, e) => click();
            return t;
        }

        /// <summary>详情里的一行：左标签 + 右值；返回值 TextView，out 返回整行容器。</summary>
        private TextView DetailRow(string label, bool clickable, out LinearLayout row)
        {
            row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(2), Dp(8), Dp(2), Dp(8));
            row.SetMinimumHeight(Dp(44));
            var l = new TextView(this) { Text = label, TextSize = 13 };
            l.SetTextColor(new Color(TxtSub));
            l.LayoutParameters = new LinearLayout.LayoutParams(Dp(76), -2);
            row.AddView(l);
            var v = new TextView(this) { TextSize = 16, Gravity = GravityFlags.Right };
            v.SetTextColor(new Color(clickable ? AccentBlue : TxtMain));
            v.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            v.SetSingleLine(!clickable);
            v.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
            row.AddView(v);
            _detail.AddView(row);
            return v;
        }

        private View Divider()
        {
            var v = new View(this);
            v.SetBackgroundColor(new Color(unchecked((int)0x33FFFFFF)));
            v.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(1)) { TopMargin = Dp(8), BottomMargin = Dp(4) };
            return v;
        }

        private Android.Graphics.Drawables.Drawable Underline()
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor(0);
            return d;
        }

        private int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);

        private void HideSystemUi()
        {
            try
            {
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    (int)SystemUiFlags.Fullscreen |
                    (int)SystemUiFlags.HideNavigation |
                    (int)SystemUiFlags.ImmersiveSticky |
                    (int)SystemUiFlags.LayoutStable |
                    (int)SystemUiFlags.LayoutHideNavigation |
                    (int)SystemUiFlags.LayoutFullscreen);
            }
            catch { }
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus) HideSystemUi();
        }

        /* ---------------- 显示当前图片 ---------------- */

        private Photo Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        private void ShowCurrent()
        {
            var p = Current;
            _menu.Visibility = ViewStates.Gone;
            _detail.Visibility = ViewStates.Gone;
            _zoom = false;
            _img.ScaleX = 1f; _img.ScaleY = 1f;

            if (_bmp != null) { try { _bmp.Recycle(); } catch { } _bmp = null; }
            _img.SetImageBitmap(null);
            if (p == null) { _menuInfo.Text = L10n.T("没有可显示的图片"); return; }

            int max = Math.Max(Resources.DisplayMetrics.WidthPixels, Resources.DisplayMetrics.HeightPixels);
            _bmp = PhotoLibrary.LoadFull(p, Math.Max(720, max));
            _img.SetImageBitmap(_bmp);
            _menuInfo.Text = (_index + 1) + " / " + _items.Count + " · " + p.Name;
            _dName.Text = p.Name;
            _dTime.Text = p.ModifiedText;
            _dFolder.Text = string.IsNullOrEmpty(p.Folder) ? "—" : p.Folder;
            _dExtra.Text = p.SizeText + (string.IsNullOrEmpty(p.DimensionText) ? "" : " · " + p.DimensionText);
        }

        private void Step(int dir)
        {
            if (_items.Count == 0) return;
            int next = _index + dir;
            if (next < 0 || next >= _items.Count) return;
            _index = next;
            ShowCurrent();
        }

        /* ---------------- 触摸：中央展开菜单 / 左右切图 ---------------- */

        public bool OnTouch(View v, MotionEvent e)
        {
            if (e.Action == MotionEventActions.Down)
            {
                _downX = e.GetX(); _downY = e.GetY(); _downAt = e.EventTime;
                return true;
            }
            if (e.Action != MotionEventActions.Up) return true;

            float dx = e.GetX() - _downX, dy = e.GetY() - _downY;
            long dt = e.EventTime - _downAt;
            int w = Math.Max(1, v.Width);

            // 横向滑动优先：快速切换上一张 / 下一张
            if (Math.Abs(dx) > Dp(56) && Math.Abs(dx) > Math.Abs(dy) * 1.3f)
            {
                Step(dx < 0 ? 1 : -1);
                return true;
            }
            if (Math.Abs(dx) > Dp(16) || Math.Abs(dy) > Dp(16)) return true;

            // 双击放大 / 还原
            long now = e.EventTime;
            bool dbl = now - _lastTap < 320;
            _lastTap = now;
            if (dbl)
            {
                _zoom = !_zoom;
                _img.PivotX = e.GetX(); _img.PivotY = e.GetY();
                _img.ScaleX = _img.ScaleY = _zoom ? 2f : 1f;
                return true;
            }

            float x = e.GetX();
            if (x < w * 0.3f) { Step(-1); return true; }
            if (x > w * 0.7f) { Step(1); return true; }

            // 正中间：展开 / 收起菜单
            if (_detail.Visibility == ViewStates.Visible)
            {
                _detail.Visibility = ViewStates.Gone;
                _menu.Visibility = ViewStates.Visible;
            }
            else
            {
                _menu.Visibility = _menu.Visibility == ViewStates.Visible
                    ? ViewStates.Gone : ViewStates.Visible;
            }
            return true;
        }

        public override void OnBackPressed()
        {
            if (_detail.Visibility == ViewStates.Visible)
            {
                _detail.Visibility = ViewStates.Gone;
                _menu.Visibility = ViewStates.Visible;
                return;
            }
            if (_menu.Visibility == ViewStates.Visible) { _menu.Visibility = ViewStates.Gone; return; }
            base.OnBackPressed();
        }

        /* ---------------- 菜单动作 ---------------- */

        private void ShowDetail()
        {
            _menu.Visibility = ViewStates.Gone;
            var p = Current;
            if (p == null) return;
            _dName.Text = p.Name;
            _dTime.Text = p.ModifiedText;
            _dFolder.Text = string.IsNullOrEmpty(p.Folder) ? "—" : p.Folder;
            _dExtra.Text = p.SizeText + (string.IsNullOrEmpty(p.DimensionText) ? "" : " · " + p.DimensionText);
            _detail.Visibility = ViewStates.Visible;
        }

        private void RenameCurrent()
        {
            var p = Current;
            if (p == null) return;
            var et = new EditText(this) { Text = p.Name };
            et.SetSingleLine(true);
            et.TextSize = 14;
            et.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            var wrap = new FrameLayout(this);
            wrap.SetPadding(Dp(20), Dp(8), Dp(20), 0);
            wrap.AddView(et);
            L10n.Dialog(this)
                .SetTitle(L10n.T("重命名（可省略扩展名）"))
                .SetView(wrap)
                .SetPositiveButton(L10n.T("确定"), (s, e) =>
                {
                    string err;
                    string dest = PhotoLibrary.Rename(p.Path, et.Text, out err);
                    if (dest == null) { L10n.Toast(this, L10n.T("重命名失败：") + err, ToastLength.Short).Show(); return; }
                    var fresh = PhotoLibrary.Find(dest);
                    if (fresh != null) _items[_index] = fresh;
                    ShowCurrent();
                    L10n.Toast(this, L10n.T("已重命名为 ") + IoPath.GetFileName(dest), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Create().Show();
        }

        private void DeleteCurrent()
        {
            var p = Current;
            if (p == null) return;
            L10n.Dialog(this)
                .SetTitle(L10n.T("删除图片"))
                .SetMessage(L10n.T("确定删除「") + p.Name + L10n.T("」？\n\n此操作会直接删除磁盘上的文件，且不可恢复。"))
                .SetPositiveButton(L10n.T("删除"), (s, e) =>
                {
                    string err;
                    if (!PhotoLibrary.Delete(p.Path, out err))
                    { L10n.Toast(this, L10n.T("删除失败：") + err, ToastLength.Short).Show(); return; }
                    _items.RemoveAt(_index);
                    if (_items.Count == 0) { Finish(); return; }
                    if (_index >= _items.Count) _index = _items.Count - 1;
                    ShowCurrent();
                    L10n.Toast(this, L10n.T("已删除"), ToastLength.Short).Show();
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Create().Show();
        }

        private void ShareCurrent()
        {
            var p = Current;
            if (p == null) return;
            try
            {
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    this, "com.Ahochat.android.fileprovider", new Java.IO.File(p.Path));
                var i = new Intent(Intent.ActionSend);
                i.SetType(PhotoLibrary.Mime(p.Path));
                i.PutExtra(Intent.ExtraStream, uri);
                i.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(Intent.CreateChooser(i, L10n.T("分享图片")));
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>分享到频道：对端点快捷卡片直接进它的图片列表。</summary>
        private void ShareToChannel()
        {
            var p = Current;
            if (p == null) return;
            try
            {
                if (!MediaShare.ShareToChannel(this, p.Name, p.Path))
                    L10n.Toast(this, L10n.T("未能发起分享"), ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        protected override void OnDestroy()
        {
            if (_bmp != null) { try { _bmp.Recycle(); } catch { } _bmp = null; }
            try { _img?.SetImageBitmap(null); } catch { }
            base.OnDestroy();
        }
    }
}

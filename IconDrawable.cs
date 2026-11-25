using System;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;

namespace AhoChatA
{
    /// <summary>
    /// 把 IconView（Canvas 矢量图标）包成 Drawable，方便任意 TextView 用
    /// SetCompoundDrawablesWithIntrinsicBounds 在文字前显示矢量图标，
    /// 用来替代 🖼 🎬 🎵 🔗 🔔 ⚡ 🔋 等各 ROM 渲染不一致的 emoji。
    /// </summary>
    public sealed class IconDrawable : Drawable
    {
        private readonly IconView _v;
        private readonly int _size;
        private Bitmap _bmp;

        public IconDrawable(Context ctx, IconKind kind, int color, int sizePx, float thickness = 1.9f)
        {
            _size = Math.Max(1, sizePx);
            _v = new IconView(ctx, kind) { IconColor = color, Thickness = thickness };
            SetBounds(0, 0, _size, _size);
        }

        public IconKind Kind { get { return _v.Kind; } set { _v.Kind = value; _bmp = null; InvalidateSelf(); } }
        public int IconColor { get { return _v.IconColor; } set { _v.IconColor = value; _bmp = null; InvalidateSelf(); } }

        public override int IntrinsicWidth { get { return _size; } }
        public override int IntrinsicHeight { get { return _size; } }

        public override void Draw(Canvas canvas)
        {
            var b = Bounds;
            int w = b.Width(), h = b.Height();
            if (w <= 0 || h <= 0) return;
            if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
            {
                try { if (_bmp != null) _bmp.Recycle(); } catch { }
                _bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
                var bc = new Canvas(_bmp);
                _v.Measure(View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly),
                           View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly));
                _v.Layout(0, 0, w, h);
                _v.Draw(bc);
            }
            canvas.DrawBitmap(_bmp, b.Left, b.Top, null);
        }

        public override void SetAlpha(int alpha) { }
        public override void SetColorFilter(ColorFilter colorFilter) { }
        public override int Opacity { get { return (int)Android.Graphics.Format.Translucent; } }
    }
}

// ============================================================
// 阅读模块 · PDF 单页视图
//
// 转译自 Areada ui/reader/PdfZoomablePage.kt + PdfReaderScreen.kt 的交互，
// 但按需求严格保持「一页就是一页」：
//   · 一次只在 Canvas 上画一张该页的 Bitmap，绝不把多页纵向拼接成长图；
//   · 页面按 FitCenter 缩放，留白用主题背景填充；
//   · 点击左 / 右 28% 翻到上一页 / 下一页，点中间呼出/收起工具栏；
//   · 双指缩放（1~3×），放大后可拖动；缩放档变化通知 Activity 重渲染高清页。
// ============================================================
using System;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;

namespace AhoChatA.Reader
{
    public sealed class PdfPageView : View
    {
        private Bitmap _bmp;
        private float _zoom = 1f;
        private float _panX, _panY;
        private float _x0, _y0, _lastX, _lastY, _pinch = -1f;
        private long _t0;
        private bool _moved, _pinching;
        private int _bg = unchecked((int)0xFFFDFCFA);
        private readonly Paint _paint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);

        /// <summary>单击回传触点 X（Activity 按视图宽度判断左右/中间）。</summary>
        public Action<float> Tap;
        /// <summary>双指缩放结束：Activity 按新缩放档重新渲染更清晰的页图。</summary>
        public Action ZoomSettled;

        public float Zoom => _zoom;

        public PdfPageView(Context c) : base(c) { }
        public PdfPageView(Context c, IAttributeSet a) : base(c, a) { }

        public void SetPageBg(int argb)
        {
            _bg = argb;
            Invalidate();
        }

        /// <summary>换页时 resetZoom=true 复位缩放；重渲染高清页时 false 保持当前缩放与平移。</summary>
        public void SetBitmap(Bitmap b, bool resetZoom = true)
        {
            var old = _bmp;
            _bmp = b;
            if (resetZoom) { _zoom = 1f; _panX = 0; _panY = 0; }
            Invalidate();
            if (old != null && !ReferenceEquals(old, b) && !old.IsRecycled)
            {
                try { old.Recycle(); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawColor(new Color(_bg));
            var b = _bmp;
            if (b == null || b.IsRecycled || b.Width <= 0 || b.Height <= 0 || Width <= 0 || Height <= 0) return;

            float fit = Math.Min(Width / (float)b.Width, Height / (float)b.Height);
            float scale = fit * _zoom;
            float dw = b.Width * scale, dh = b.Height * scale;
            float dx = (Width - dw) / 2f + _panX;
            float dy = (Height - dh) / 2f + _panY;

            var m = new Matrix();
            m.SetScale(scale, scale);
            m.PostTranslate(dx, dy);
            canvas.DrawBitmap(b, m, _paint);
        }

        private float DrawnW()
        {
            var b = _bmp;
            if (b == null || b.Width <= 0 || Width <= 0) return 0;
            return b.Width * Math.Min(Width / (float)b.Width, Height / (float)b.Height) * _zoom;
        }

        private float DrawnH()
        {
            var b = _bmp;
            if (b == null || b.Height <= 0 || Height <= 0) return 0;
            return b.Height * Math.Min(Width / (float)b.Width, Height / (float)b.Height) * _zoom;
        }

        private void ClampPan()
        {
            float mx = Math.Max(0, (DrawnW() - Width) / 2f);
            float my = Math.Max(0, (DrawnH() - Height) / 2f);
            _panX = Math.Max(-mx, Math.Min(mx, _panX));
            _panY = Math.Max(-my, Math.Min(my, _panY));
        }

        private static float Dist(MotionEvent e)
        {
            float dx = e.GetX(0) - e.GetX(1), dy = e.GetY(0) - e.GetY(1);
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _x0 = _lastX = e.GetX();
                    _y0 = _lastY = e.GetY();
                    _t0 = SystemClock.ElapsedRealtime();
                    _moved = false; _pinching = false; _pinch = -1f;
                    return true;

                case MotionEventActions.PointerDown:
                    if (e.PointerCount >= 2)
                    {
                        _pinch = Dist(e); _pinching = true; _moved = true;
                    }
                    return true;

                case MotionEventActions.Move:
                    if (e.PointerCount >= 2)
                    {
                        float d = Dist(e);
                        if (_pinch > 0 && d > 0)
                        {
                            _zoom = Math.Max(1f, Math.Min(3f, _zoom * (d / _pinch)));
                            ClampPan();
                            Invalidate();
                        }
                        _pinch = d; _pinching = true; _moved = true;
                        return true;
                    }
                    if (Math.Abs(e.GetX() - _x0) > 12 || Math.Abs(e.GetY() - _y0) > 12) _moved = true;
                    if (_moved && _zoom > 1.001f)
                    {
                        _panX += e.GetX() - _lastX;
                        _panY += e.GetY() - _lastY;
                        ClampPan();
                        Invalidate();
                    }
                    _lastX = e.GetX(); _lastY = e.GetY();
                    return true;

                case MotionEventActions.PointerUp:
                    _pinch = -1f;
                    return true;

                case MotionEventActions.Up:
                    long dt = SystemClock.ElapsedRealtime() - _t0;
                    if (!_moved && dt < 320 && !_pinching) Tap?.Invoke(e.GetX());
                    if (_pinching && ZoomSettled != null) ZoomSettled();
                    _pinching = false; _pinch = -1f;
                    return true;

                case MotionEventActions.Cancel:
                    _pinching = false; _pinch = -1f;
                    return true;
            }
            return base.OnTouchEvent(e);
        }

        protected override void OnDetachedFromWindow()
        {
            base.OnDetachedFromWindow();
            try
            {
                if (_bmp != null && !_bmp.IsRecycled) _bmp.Recycle();
                _bmp = null;
            }
            catch { }
        }
    }
}

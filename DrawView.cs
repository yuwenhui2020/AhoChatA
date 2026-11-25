using Android.Content;
using Android.Graphics;
using Android.Views;

namespace AhoChatA
{
    /// <summary>
    /// 透明画布：绘制层叠加在纯色底之上。
    ///   - 画笔：在透明位图上绘制彩色描边；
    ///   - 橡皮擦：用 PorterDuff.Clear 把绘制层像素擦成透明，从而「露出」下方的纯色底——
    ///     因此橡皮擦只能擦掉用户的绘制，永远擦不掉纯色填充本身。
    ///   - 清空：把整张位图清为透明（纯色底仍在）。
    /// </summary>
    public class DrawView : View
    {
        private Bitmap bitmap;
        private Canvas canvas;
        private readonly Paint penPaint;
        private readonly Paint eraserPaint;
        private readonly Paint dotPaint;

        private Color penColor = Color.White;
        private float penWidth = 8f;
        private float eraserWidth = 30f;
        private bool erasing;

        public DrawView(Context context) : base(context)
        {
            penPaint = new Paint
            {
                Color = penColor,
                AntiAlias = true,
                StrokeCap = Paint.Cap.Round,
                StrokeJoin = Paint.Join.Round
            };
            penPaint.SetStyle(Paint.Style.Stroke);
            penPaint.StrokeWidth = penWidth;

            eraserPaint = new Paint
            {
                AntiAlias = true,
                StrokeCap = Paint.Cap.Round,
                StrokeJoin = Paint.Join.Round
            };
            eraserPaint.SetStyle(Paint.Style.Stroke);
            eraserPaint.StrokeWidth = eraserWidth;
            // 关键：橡皮擦把像素变为透明（露出纯色底），而非涂成某种颜色
            eraserPaint.SetXfermode(new PorterDuffXfermode(PorterDuff.Mode.Clear));

            dotPaint = new Paint
            {
                AntiAlias = true,
                Color = penColor
            };
        }

        public void SetPenColor(Color color)
        {
            penColor = color;
            penPaint.Color = color;
            dotPaint.Color = color;
        }

        public void SetPenWidth(float width)
        {
            penWidth = width;
            penPaint.StrokeWidth = width;
        }

        /// <summary>单独设置橡皮擦直径（与画笔粗细解耦，便于高效擦除）。</summary>
        public void SetEraserWidth(float width)
        {
            eraserWidth = width;
            eraserPaint.StrokeWidth = width;
        }

        public void SetErasing(bool value) => erasing = value;

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            if (w <= 0 || h <= 0) return;
            if (bitmap == null || bitmap.Width != w || bitmap.Height != h)
            {
                var old = bitmap;
                bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
                canvas = new Canvas(bitmap);
                if (old != null)
                {
                    canvas.DrawBitmap(old, 0f, 0f, null);
                    old.Recycle();
                }
            }
        }

        protected override void OnDraw(Canvas c)
        {
            base.OnDraw(c);
            if (bitmap != null) c.DrawBitmap(bitmap, 0f, 0f, null);
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            if (canvas == null) return false;
            float x = e.GetX();
            float y = e.GetY();

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    // 落点画一个圆点，避免单点无法留下痕迹
                    if (erasing)
                        canvas.DrawCircle(x, y, eraserPaint.StrokeWidth / 2f, eraserPaint);
                    else
                        canvas.DrawCircle(x, y, penWidth / 2f, dotPaint);
                    Invalidate();
                    LastX = x;
                    LastY = y;
                    return true;

                case MotionEventActions.Move:
                    if (erasing)
                        canvas.DrawLine(LastX, LastY, x, y, eraserPaint);
                    else
                        canvas.DrawLine(LastX, LastY, x, y, penPaint);
                    LastX = x;
                    LastY = y;
                    Invalidate();
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    return true;
            }
            return false;
        }

        public void ClearDrawings()
        {
            if (canvas == null) return;
            canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear);
            Invalidate();
        }

        private float LastX, LastY;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                bitmap?.Recycle();
                bitmap = null;
            }
            base.Dispose(disposing);
        }
    }
}

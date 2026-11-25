using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace AhoChatA
{
    /// <summary>
    /// 歌曲/视频列表右侧竖向 A-Z 索引条。触摸或拖动时按落点回调首字母（'#' + A-Z），
    /// 由宿主把列表滚到第一个匹配项。纯自绘 View，不依赖任何第三方控件。
    /// </summary>
    public sealed class AlphabetIndexBar : View
    {
        private static readonly string[] Letters =
        {
            "#", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"
        };

        private readonly Paint _paint;
        private readonly Paint _activePaint;
        private int _active = -1;

        public event Action<string> LetterSelected;

        public AlphabetIndexBar(Context context, bool night) : base(context)
        {
            SetWillNotDraw(false);
            float density = context.Resources.DisplayMetrics.Density;
            _paint = new Paint(PaintFlags.AntiAlias)
            {
                Color = night ? new Color(0xCC, 0xD8, 0xE2, 0xE8) : new Color(0xCC, 0x33, 0x44, 0x55),
                TextAlign = Paint.Align.Center,
                TextSize = 10.5f * density
            };
            _activePaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = new Color(0xFF, 0x3B, 0x9C, 0xF0),
                TextAlign = Paint.Align.Center,
                TextSize = 14f * density,
                FakeBoldText = true
            };
        }

        /// <summary>高亮当前字母（跳转后由宿主调用）。传 null 清除高亮。</summary>
        public void SetActiveLetter(string letter)
        {
            _active = -1;
            if (!string.IsNullOrEmpty(letter))
            {
                string u = letter.ToUpperInvariant();
                for (int i = 0; i < Letters.Length; i++)
                    if (Letters[i] == u) { _active = i; break; }
            }
            Invalidate();
        }

        private float CellHeight => Math.Max(1f, (float)Height / Letters.Length);

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (Width <= 0 || Height <= 0) return;
            float cell = CellHeight;
            float cx = Width / 2f;
            Paint.FontMetrics fm = _paint.GetFontMetrics();
            float baselineOffset = -(fm.Ascent + fm.Descent) / 2f;
            for (int i = 0; i < Letters.Length; i++)
            {
                Paint p = i == _active ? _activePaint : _paint;
                Paint.FontMetrics fmi = p.GetFontMetrics();
                float y = i * cell + cell / 2f - (fmi.Ascent + fmi.Descent) / 2f;
                canvas.DrawText(Letters[i], cx, y, p);
            }
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            int idx = (int)(e.GetY() / CellHeight);
            if (idx < 0) idx = 0;
            if (idx >= Letters.Length) idx = Letters.Length - 1;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                case MotionEventActions.Move:
                    if (_active != idx) { _active = idx; Invalidate(); }
                    LetterSelected?.Invoke(Letters[idx]);
                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    // 保留高亮，让用户看到刚跳到的字母
                    break;
            }
            return true;   // 拦截，避免底下列表同时收到触摸
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _paint?.Dispose();
                _activePaint?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

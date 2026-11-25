using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace AhoChatA
{
    public enum IconKind
    {
        Chat, Radar, Grid, Person,
        Image, File, Link, Group,
        Mic, Video, Camera, Screen,
        Pen, Pin, Folder, Trash,
        Book,
        Back, More, ChevronRight, Check, Stop, Plus,
        Phone, Speaker, Mute, Rotate,
        Bluetooth, Wifi, P2P,
        Music, MoreVertical, Media,
        /* 播放控制与通用操作（替代 ⏮ ▶ ⏸ ⏭ ☰ ✕ ♡ ♥ ↑ ↓ 等字符，避免各系统字体不一致） */
        Play, Pause, Prev, Next, Queue, Close, Search,
        Heart, HeartFilled, Up, Down, Refresh, Globe, Shuffle, Repeat, RepeatOne,
        /* 替代各色 emoji：🔒 🔓 ⚙ 🔔 ⬇ ↗ 🔋 ⚡ 🖼 🎬 📁 等 */
        Lock, Unlock, Settings, Bell, Download, Share, Battery, Bolt, Photo, Camera2,
        /* 替代文本字形：● ○ ▾ ↘ 📶 等（状态点 / 空心圆 / 展开箭头 / 信号格） */
        Dot, Circle, ChevronDown, Signal,
        /* 日间 / 夜间主题切换 */
        Sun, Moon
    }

    /// <summary>
    /// 自绘线性图标（24×24 视口，描边风格）。
    ///
    /// 为什么不用 emoji：emoji 在不同 ROM 上是彩色位图，大小、基线、风格全都不受控，
    /// 一排图标放在一起会高低不齐、颜色也不统一。这里全部用 Canvas 画，
    /// 线宽/颜色/尺寸都能跟着主题走，观感与 iOS / QQ 的系统图标一致。
    /// </summary>
    public sealed class IconView : View
    {
        /* 说明：Paint 的颜色/线宽会随「选中态」变，所以按实例持有、不共享。
           这里只做一个静态预热：先把原生 Paint/Path 建一遍，让首屏不再有建对象的抖动。 */
        private static Paint _warmPaint;

        /// <summary>启动预热：建一次 Paint + 走一遍路径，避免首屏第一次绘制时的卡顿。</summary>
        public static void Warmup()
        {
            try
            {
                if (_warmPaint == null)
                {
                    _warmPaint = new Paint(PaintFlags.AntiAlias);
                    _warmPaint.SetStyle(Paint.Style.Stroke);
                    _warmPaint.StrokeWidth = 1.7f;
                    _warmPaint.StrokeCap = Paint.Cap.Round;
                    _warmPaint.StrokeJoin = Paint.Join.Round;
                    _warmPaint.SetColor(unchecked((int)0xFF111111));
                }
                // 用一个一次性 Canvas 把所有图标走一遍，把 Path 测量结果捂热
                var bmp = Android.Graphics.Bitmap.CreateBitmap(24, 24, Android.Graphics.Bitmap.Config.Argb8888);
                var c = new Canvas(bmp);
                var probe = new IconView(null, IconKind.Chat) { IconColor = unchecked((int)0xFF111111) };
                foreach (IconKind k in Enum.GetValues(typeof(IconKind)))
                {
                    try { probe.Kind = k; probe.Draw(c); } catch { }
                }
                bmp.Recycle();
            }
            catch { }
        }

        private readonly Paint _p = new Paint(PaintFlags.AntiAlias);

        private IconKind _kind;
        public IconKind Kind
        {
            get { return _kind; }
            set { if (_kind != value) { _kind = value; try { Invalidate(); } catch { } } }
        }
        public int IconColor { get; set; } = unchecked((int)0xFF111111);
        public float Thickness { get; set; } = 1.7f;
        /// <summary>选中态：线条加粗（底部 Tab 选中时用）。</summary>
        public bool Emphasized { get; set; }

        public IconView(Context ctx, IconKind kind) : base(ctx) { Kind = kind; }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            float s = Math.Min(w, h) / 24f;
            canvas.Save();
            canvas.Translate((w - 24f * s) / 2f, (h - 24f * s) / 2f);
            canvas.Scale(s, s);

            _p.Reset();
            _p.AntiAlias = true;
            _p.Color = new Color(IconColor);
            _p.SetStyle(Paint.Style.Stroke);
            _p.StrokeWidth = Emphasized ? Thickness + 0.45f : Thickness;
            _p.StrokeCap = Paint.Cap.Round;
            _p.StrokeJoin = Paint.Join.Round;

            try { Draw(canvas, Kind); }
            catch { }

            canvas.Restore();
        }

        /* ===================== 绘制 ===================== */

        private void Draw(Canvas c, IconKind k)
        {
            switch (k)
            {
                case IconKind.Chat: DrawChat(c); break;
                case IconKind.Radar: DrawRadar(c); break;
                case IconKind.Grid: DrawGrid(c); break;
                case IconKind.Person: DrawPerson(c); break;

                case IconKind.Image: DrawImage(c); break;
                case IconKind.File: DrawFile(c); break;
                case IconKind.Link: DrawLink(c); break;
                case IconKind.Group: DrawGroup(c); break;

                case IconKind.Mic: DrawMic(c); break;
                case IconKind.Video: DrawVideo(c); break;
                case IconKind.Camera: DrawCamera(c); break;
                case IconKind.Screen: DrawScreen(c); break;

                case IconKind.Pen: DrawPen(c); break;
                case IconKind.Pin: DrawPin(c); break;
                case IconKind.Folder: DrawFolder(c); break;
                case IconKind.Book: DrawBook(c); break;
                case IconKind.Trash: DrawTrash(c); break;

                case IconKind.Back: Polyline(c, 15f, 5f, 8f, 12f, 15f, 19f); break;
                case IconKind.More: DrawMore(c); break;
                case IconKind.ChevronRight: Polyline(c, 9.5f, 5f, 16.5f, 12f, 9.5f, 19f); break;
                case IconKind.Check: Polyline(c, 4.5f, 12.5f, 9.5f, 17.5f, 19.5f, 6.5f); break;
                case IconKind.Stop: FillRect(c, 6.5f, 6.5f, 17.5f, 17.5f, 2.5f, true); break;
                case IconKind.Plus:
                    Line(c, 12f, 5.5f, 12f, 18.5f);
                    Line(c, 5.5f, 12f, 18.5f, 12f);
                    break;
                case IconKind.Phone: DrawPhone(c); break;
                case IconKind.Speaker: DrawSpeaker(c); break;
                case IconKind.Mute: DrawMic(c); Line(c, 4.2f, 4.2f, 19.8f, 19.8f); break;
                case IconKind.Rotate: DrawRotate(c); break;
                case IconKind.Bluetooth: DrawBluetooth(c); break;
                case IconKind.Wifi: DrawWifi(c); break;
                case IconKind.P2P: DrawP2P(c); break;
                case IconKind.Music: DrawMusic(c); break;
                case IconKind.MoreVertical: DrawMoreVertical(c); break;
                case IconKind.Media: DrawMedia(c); break;

                /* ---- 播放控制 ---- */
                case IconKind.Play: FillPoly(c, 8f, 5f, 19f, 12f, 8f, 19f); break;
                case IconKind.Pause:
                    FillRect(c, 8.4f, 5f, 11.2f, 19f, 0.9f, true);
                    FillRect(c, 12.8f, 5f, 15.6f, 19f, 0.9f, true);
                    break;
                case IconKind.Prev:
                    FillRect(c, 5f, 5f, 7.3f, 19f, 0.7f, true);
                    FillPoly(c, 19f, 5f, 8.8f, 12f, 19f, 19f);
                    break;
                case IconKind.Next:
                    FillRect(c, 16.7f, 5f, 19f, 19f, 0.7f, true);
                    FillPoly(c, 5f, 5f, 15.2f, 12f, 5f, 19f);
                    break;

                /* ---- 通用操作 ---- */
                case IconKind.Queue:
                    Line(c, 4.5f, 7f, 19.5f, 7f);
                    Line(c, 4.5f, 12f, 19.5f, 12f);
                    Line(c, 4.5f, 17f, 13.5f, 17f);
                    break;
                case IconKind.Close:
                    Line(c, 6.2f, 6.2f, 17.8f, 17.8f);
                    Line(c, 17.8f, 6.2f, 6.2f, 17.8f);
                    break;
                case IconKind.Search:
                    Circle(c, 10.8f, 10.8f, 6.2f);
                    Line(c, 15.4f, 15.4f, 19.6f, 19.6f);
                    break;
                case IconKind.Up: Polyline(c, 6f, 14.6f, 12f, 8.6f, 18f, 14.6f); break;
                case IconKind.Down: Polyline(c, 6f, 9.4f, 12f, 15.4f, 18f, 9.4f); break;
                case IconKind.Heart: DrawHeart(c, false); break;
                case IconKind.HeartFilled: DrawHeart(c, true); break;
                case IconKind.Refresh: DrawRefresh(c); break;
                case IconKind.Globe: DrawGlobe(c); break;
                case IconKind.Shuffle: DrawShuffle(c); break;
                case IconKind.Repeat: DrawRepeat(c, false); break;
                case IconKind.RepeatOne: DrawRepeat(c, true); break;

                /* ---- 替代 emoji ---- */
                case IconKind.Lock: DrawLock(c, false); break;
                case IconKind.Unlock: DrawLock(c, true); break;
                case IconKind.Settings: DrawSettings(c); break;
                case IconKind.Bell: DrawBell(c); break;
                case IconKind.Download: DrawDownload(c); break;
                case IconKind.Share: DrawShare(c); break;
                case IconKind.Battery: DrawBattery(c); break;
                case IconKind.Bolt: DrawBolt(c); break;
                case IconKind.Photo: DrawImage(c); break;
                case IconKind.Camera2: DrawCamera(c); break;

                /* ---- 替代文本字形 ---- */
                case IconKind.Dot: FillCircle(c, 12f, 12f, 5.4f); break;
                case IconKind.Circle: Circle(c, 12f, 12f, 5.4f); break;
                case IconKind.ChevronDown: Polyline(c, 6f, 9.4f, 12f, 15.4f, 18f, 9.4f); break;
                case IconKind.Signal:
                    Line(c, 5f, 19f, 5f, 14f);
                    Line(c, 10f, 19f, 10f, 10f);
                    Line(c, 15f, 19f, 15f, 6f);
                    Line(c, 20f, 19f, 20f, 3f);
                    break;

                /* ---- 日间 / 夜间主题切换 ---- */
                case IconKind.Sun: DrawSun(c); break;
                case IconKind.Moon: DrawMoon(c); break;
            }
        }

        /// <summary>太阳（日间模式）：圆 + 八道光芒。</summary>
        private void DrawSun(Canvas c)
        {
            Circle(c, 12f, 12f, 4.3f);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4.0;
                float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                Line(c, 12f + dx * 7.4f, 12f + dy * 7.4f, 12f + dx * 10.1f, 12f + dy * 10.1f);
            }
        }

        /// <summary>月亮（夜间模式）：两段圆弧拼成的月牙，与其它线性图标同风格。</summary>
        private void DrawMoon(Canvas c)
        {
            var p = new Path();
            // 外弧：以 (12,12) 为心、r=9，从右下约 5° 顺时针扫 260°
            p.ArcTo(new RectF(3f, 3f, 21f, 21f), 5f, 260f, true);
            // 内弧：r=7 的凹弧回接到起点，挖出月牙
            p.ArcTo(new RectF(9.84f, 0.16f, 23.84f, 14.16f), 216.5f, -163f, false);
            p.Close();
            c.DrawPath(p, _p);
        }

        private void DrawLock(Canvas c, bool open)
        {
            RRect(c, 5.2f, 10.6f, 18.8f, 20.4f, 2.6f);
            var p = new Path();
            if (open)
            {
                p.MoveTo(8.4f, 10.6f); p.LineTo(8.4f, 8.2f);
                p.QuadTo(8.4f, 4.6f, 12.2f, 4.6f);
                p.QuadTo(16f, 4.6f, 16f, 8.2f);
                p.LineTo(16f, 9.2f);
            }
            else
            {
                p.MoveTo(8.4f, 10.6f); p.LineTo(8.4f, 8.2f);
                p.QuadTo(8.4f, 4.6f, 12f, 4.6f);
                p.QuadTo(15.6f, 4.6f, 15.6f, 8.2f);
                p.LineTo(15.6f, 10.6f);
            }
            c.DrawPath(p, _p);
            FillCircle(c, 12f, 15.4f, 1.15f);
        }

        private void DrawSettings(Canvas c)
        {
            Circle(c, 12f, 12f, 3.1f);
            for (int i = 0; i < 8; i++)
            {
                double a = Math.PI / 4 * i;
                float x1 = 12f + (float)Math.Cos(a) * 5.4f, y1 = 12f + (float)Math.Sin(a) * 5.4f;
                float x2 = 12f + (float)Math.Cos(a) * 8.2f, y2 = 12f + (float)Math.Sin(a) * 8.2f;
                Line(c, x1, y1, x2, y2);
            }
        }

        private void DrawBell(Canvas c)
        {
            var p = new Path();
            p.MoveTo(6.2f, 16.2f);
            p.LineTo(6.2f, 11f);
            p.QuadTo(6.2f, 5.6f, 12f, 5.6f);
            p.QuadTo(17.8f, 5.6f, 17.8f, 11f);
            p.LineTo(17.8f, 16.2f);
            p.LineTo(19.4f, 17.8f);
            p.LineTo(4.6f, 17.8f);
            p.Close();
            c.DrawPath(p, _p);
            Arc(c, 9.6f, 17.8f, 14.4f, 21.4f, 0, 180);
        }

        private void DrawDownload(Canvas c)
        {
            Line(c, 12f, 3.6f, 12f, 14.4f);
            Polyline(c, 7.6f, 10.2f, 12f, 14.6f, 16.4f, 10.2f);
            Line(c, 4.8f, 19.8f, 19.2f, 19.8f);
        }

        private void DrawShare(Canvas c)
        {
            Circle(c, 6.4f, 12f, 2.4f);
            Circle(c, 17.2f, 6.4f, 2.4f);
            Circle(c, 17.2f, 17.6f, 2.4f);
            Line(c, 8.6f, 10.9f, 15.2f, 7.4f);
            Line(c, 8.6f, 13.1f, 15.2f, 16.6f);
        }

        private void DrawBattery(Canvas c)
        {
            RRect(c, 2.6f, 7.6f, 18.6f, 16.4f, 2.2f);
            FillRect(c, 19.6f, 10.6f, 21.2f, 13.4f, 0.8f, true);
            FillRect(c, 4.6f, 9.8f, 10.6f, 14.4f, 1f, true);
        }

        private void DrawBolt(Canvas c)
        {
            var p = new Path();
            p.MoveTo(13.4f, 3.4f);
            p.LineTo(6.6f, 13.2f);
            p.LineTo(11.2f, 13.2f);
            p.LineTo(10.6f, 20.6f);
            p.LineTo(17.4f, 10.8f);
            p.LineTo(12.8f, 10.8f);
            p.Close();
            _p.SetStyle(Paint.Style.Fill);
            c.DrawPath(p, _p);
            _p.SetStyle(Paint.Style.Stroke);
        }

        /// <summary>影音：圆角屏幕 + 中央播放三角，表示音乐与视频合一的媒体库。</summary>
        private void DrawMedia(Canvas c)
        {
            var p = new Path();
            p.AddRoundRect(new RectF(2.6f, 4.6f, 21.4f, 19.4f), 3f, 3f, Path.Direction.Cw);
            c.DrawPath(p, _p);
            var f = new Paint(_p);
            f.SetStyle(Paint.Style.Fill);
            var tri = new Path();
            tri.MoveTo(10f, 8.6f); tri.LineTo(16.4f, 12f); tri.LineTo(10f, 15.4f);
            tri.Close();
            c.DrawPath(tri, f);
            f.Dispose();
        }

        /// <summary>竖向排列的三个连续圆点（列表行的「更多」菜单按钮）。</summary>
        private void DrawMoreVertical(Canvas c)
        {
            FillCircle(c, 12f, 5.2f, 1.55f);
            FillCircle(c, 12f, 12f, 1.55f);
            FillCircle(c, 12f, 18.8f, 1.55f);
        }

        /// <summary>双音符：两根符杆 + 上横梁 + 两个符头。</summary>
        private void DrawMusic(Canvas c)
        {
            var p = new Path();
            // 符杆
            p.MoveTo(9.4f, 17f); p.LineTo(9.4f, 6.6f);
            p.MoveTo(16.4f, 15f); p.LineTo(16.4f, 4.8f);
            // 上横梁
            p.MoveTo(9.4f, 6.6f); p.LineTo(16.4f, 4.8f);
            c.DrawPath(p, _p);

            // 符头（实心椭圆）
            var f = new Paint(_p);
            f.SetStyle(Paint.Style.Fill);
            c.DrawOval(5.8f, 15.2f, 9.4f, 18.4f, f);
            c.DrawOval(12.8f, 13.2f, 16.4f, 16.4f, f);
            f.Dispose();
        }

        private void DrawChat(Canvas c)
        {
            var p = new Path();
            p.AddRoundRect(new RectF(3f, 4.2f, 21f, 17.2f), 5f, 5f, Path.Direction.Cw);
            p.MoveTo(8f, 17.2f);
            p.LineTo(8f, 21.2f);
            p.LineTo(13.2f, 17.2f);
            c.DrawPath(p, _p);
            // 三个小点，更像"消息"
            Dot(c, 8.2f, 10.7f);
            Dot(c, 12f, 10.7f);
            Dot(c, 15.8f, 10.7f);
        }

        private void DrawRadar(Canvas c)
        {
            Arc(c, 3.4f, 3.4f, 20.6f, 20.6f, -45, 90);
            Arc(c, 6.9f, 6.9f, 17.1f, 17.1f, -45, 90);
            Arc(c, 10.4f, 10.4f, 13.6f, 13.6f, -45, 90);
            FillCircle(c, 12f, 12f, 1.35f);
        }

        private void DrawGrid(Canvas c)
        {
            RRect(c, 3.5f, 3.5f, 10.4f, 10.4f, 2.6f);
            RRect(c, 13.6f, 3.5f, 20.5f, 10.4f, 2.6f);
            RRect(c, 3.5f, 13.6f, 10.4f, 20.5f, 2.6f);
            RRect(c, 13.6f, 13.6f, 20.5f, 20.5f, 2.6f);
        }

        private void DrawPerson(Canvas c)
        {
            Circle(c, 12f, 8.2f, 3.9f);
            var p = new Path();
            p.MoveTo(4.6f, 20.6f);
            p.QuadTo(12f, 13.2f, 19.4f, 20.6f);
            c.DrawPath(p, _p);
        }

        private void DrawImage(Canvas c)
        {
            RRect(c, 3.2f, 4.2f, 20.8f, 19.8f, 3.2f);
            Circle(c, 8.4f, 9.2f, 1.6f);
            var p = new Path();
            p.MoveTo(4.4f, 17.6f);
            p.LineTo(9.6f, 12.4f);
            p.LineTo(13.2f, 16f);
            p.LineTo(16.4f, 12.4f);
            p.LineTo(19.8f, 16.2f);
            c.DrawPath(p, _p);
        }

        private void DrawFile(Canvas c)
        {
            var p = new Path();
            p.MoveTo(6f, 3.2f);
            p.LineTo(13.6f, 3.2f);
            p.LineTo(18.4f, 8f);
            p.LineTo(18.4f, 20.8f);
            p.LineTo(6f, 20.8f);
            p.Close();
            c.DrawPath(p, _p);
            Polyline(c, 13.6f, 3.2f, 13.6f, 8f, 18.4f, 8f);
        }

        private void DrawLink(Canvas c)
        {
            c.Save();
            c.Rotate(45f, 12f, 12f);
            RRect(c, 3.6f, 8.8f, 12.4f, 15.2f, 3.2f);
            RRect(c, 11.6f, 8.8f, 20.4f, 15.2f, 3.2f);
            c.Restore();
        }

        private void DrawGroup(Canvas c)
        {
            Circle(c, 9f, 8.6f, 3.1f);
            var p = new Path();
            p.MoveTo(3.4f, 19.6f);
            p.QuadTo(9f, 14.4f, 14.6f, 19.6f);
            c.DrawPath(p, _p);

            Circle(c, 16.8f, 9.6f, 2.4f);
            var p2 = new Path();
            p2.MoveTo(15f, 19.6f);
            p2.QuadTo(17.2f, 16.2f, 20.8f, 17.6f);
            c.DrawPath(p2, _p);
        }

        private void DrawMic(Canvas c)
        {
            RRect(c, 9.4f, 3f, 14.6f, 13f, 2.6f);
            Arc(c, 6.4f, 7.4f, 17.6f, 18.4f, 0, 180);
            Line(c, 12f, 18.4f, 12f, 21f);
            Line(c, 9f, 21f, 15f, 21f);
        }

        private void DrawVideo(Canvas c)
        {
            RRect(c, 3f, 6.4f, 15.2f, 18.6f, 3f);
            var p = new Path();
            p.MoveTo(15.2f, 11.2f);
            p.LineTo(20.8f, 8.2f);
            p.LineTo(20.8f, 16.8f);
            p.LineTo(15.2f, 13.8f);
            c.DrawPath(p, _p);
        }

        private void DrawCamera(Canvas c)
        {
            RRect(c, 3f, 6.6f, 21f, 19.6f, 3.4f);
            RRect(c, 8.6f, 4f, 15.4f, 7f, 1.6f);
            Circle(c, 12f, 13.1f, 3.9f);
        }

        private void DrawScreen(Canvas c)
        {
            RRect(c, 3f, 4.8f, 21f, 16.4f, 2.6f);
            Line(c, 12f, 16.4f, 12f, 20f);
            Line(c, 8.8f, 20f, 15.2f, 20f);
        }

        private void DrawPen(Canvas c)
        {
            var p = new Path();
            p.MoveTo(4.2f, 19.8f);
            p.LineTo(5.8f, 14.4f);
            p.LineTo(15.4f, 4.8f);
            p.LineTo(19.2f, 8.6f);
            p.LineTo(9.6f, 18.2f);
            p.Close();
            c.DrawPath(p, _p);
            Line(c, 4.2f, 19.8f, 9.6f, 18.2f);
        }

        private void DrawPin(Canvas c)
        {
            Circle(c, 12f, 9.4f, 6.4f);
            FillCircle(c, 12f, 9.4f, 2.1f);
            var p = new Path();
            p.MoveTo(7.4f, 13.6f);
            p.LineTo(12f, 21.2f);
            p.LineTo(16.6f, 13.6f);
            c.DrawPath(p, _p);
        }

        private void DrawFolder(Canvas c)
        {
            RRect(c, 3.2f, 7.4f, 20.8f, 20f, 3f);
            RRect(c, 4.6f, 5.2f, 11.2f, 8.6f, 1.6f);
        }

        private void DrawBook(Canvas c)
        {
            // 摊开的书：左右两页 + 中缝（描边风格，小尺寸下也不糊）
            var left = new Path();
            left.MoveTo(12f, 6.4f);
            left.QuadTo(8.2f, 4.4f, 3.8f, 5.4f);
            left.LineTo(3.8f, 17.8f);
            left.QuadTo(8.2f, 16.9f, 12f, 19.2f);
            left.Close();
            c.DrawPath(left, _p);

            var right = new Path();
            right.MoveTo(12f, 6.4f);
            right.QuadTo(15.8f, 4.4f, 20.2f, 5.4f);
            right.LineTo(20.2f, 17.8f);
            right.QuadTo(15.8f, 16.9f, 12f, 19.2f);
            right.Close();
            c.DrawPath(right, _p);
        }

        private void DrawTrash(Canvas c)
        {
            Line(c, 3.8f, 6.6f, 20.2f, 6.6f);
            Line(c, 9.4f, 4.2f, 14.6f, 4.2f);
            var p = new Path();
            p.MoveTo(6.4f, 6.6f);
            p.LineTo(7.4f, 20.4f);
            p.LineTo(16.6f, 20.4f);
            p.LineTo(17.6f, 6.6f);
            c.DrawPath(p, _p);
            Line(c, 10.4f, 10f, 10.4f, 17f);
            Line(c, 13.6f, 10f, 13.6f, 17f);
        }

        private void DrawMore(Canvas c)
        {
            FillCircle(c, 5.4f, 12f, 1.55f);
            FillCircle(c, 12f, 12f, 1.55f);
            FillCircle(c, 18.6f, 12f, 1.55f);
        }

        private void DrawPhone(Canvas c)
        {
            var p = new Path();
            p.MoveTo(6.6f, 4.6f);
            p.LineTo(9.4f, 4.6f);
            p.LineTo(10.4f, 8.6f);
            p.LineTo(8.4f, 10.6f);
            p.QuadTo(13.4f, 15.6f, 15.4f, 13.6f);
            p.LineTo(19.4f, 14.6f);
            p.LineTo(19.4f, 17.4f);
            p.QuadTo(19.4f, 19.4f, 17.4f, 19.4f);
            p.QuadTo(4.6f, 19.4f, 4.6f, 6.6f);
            p.QuadTo(4.6f, 4.6f, 6.6f, 4.6f);
            p.Close();
            c.DrawPath(p, _p);
        }

        private void DrawSpeaker(Canvas c)
        {
            var p = new Path();
            p.MoveTo(4f, 9.8f);
            p.LineTo(7.6f, 9.8f);
            p.LineTo(12.2f, 6.2f);
            p.LineTo(12.2f, 17.8f);
            p.LineTo(7.6f, 14.2f);
            p.LineTo(4f, 14.2f);
            p.Close();
            c.DrawPath(p, _p);
            Arc(c, 12.6f, 8.6f, 18.6f, 15.4f, -50, 100);
            Arc(c, 14.4f, 6.2f, 20.6f, 17.8f, -50, 100);
        }

        private void DrawRotate(Canvas c)
        {
            Arc(c, 4.6f, 4.6f, 19.4f, 19.4f, 40, 280);
            // 箭头
            Polyline(c, 16.4f, 3.4f, 19.8f, 7.2f, 15.2f, 8.4f);
        }

        /* ===================== 基础绘制 ===================== */

        private void RRect(Canvas c, float l, float t, float r, float b, float rad)
        {
            c.DrawRoundRect(new RectF(l, t, r, b), rad, rad, _p);
        }

        private void DrawBluetooth(Canvas c)
        {
            Line(c, 12f, 3f, 12f, 21f);
            Polyline(c, 7f, 7.5f, 12f, 12f, 7f, 16.5f);
            Polyline(c, 17f, 7.5f, 12f, 12f, 17f, 16.5f);
        }

        private void DrawWifi(Canvas c)
        {
            Polyline(c, 4f, 10.5f, 12f, 4.5f, 20f, 10.5f);
            Polyline(c, 7.2f, 14.2f, 12f, 9.8f, 16.8f, 14.2f);
            Circle(c, 12f, 18f, 1.3f);
        }

        private void DrawP2P(Canvas c)
        {
            Circle(c, 6.5f, 12f, 3f);
            Circle(c, 17.5f, 12f, 3f);
            Line(c, 9.5f, 12f, 14.5f, 12f);
        }

        /// <summary>填充多边形（播放/上一曲/下一曲的实心三角形）。</summary>
        private void FillPoly(Canvas c, params float[] pts)
        {
            if (pts == null || pts.Length < 6) return;
            var p = new Path();
            p.MoveTo(pts[0], pts[1]);
            for (int i = 2; i + 1 < pts.Length; i += 2) p.LineTo(pts[i], pts[i + 1]);
            p.Close();
            _p.SetStyle(Paint.Style.Fill);
            c.DrawPath(p, _p);
            _p.SetStyle(Paint.Style.Stroke);
        }

        /// <summary>心形（收藏）：贝塞尔曲线画，实心/描边两种。</summary>
        private void DrawHeart(Canvas c, bool filled)
        {
            var p = new Path();
            p.MoveTo(12f, 20.2f);
            p.CubicTo(3.4f, 14.2f, 3.8f, 6.2f, 8.3f, 6.2f);
            p.CubicTo(10.4f, 6.2f, 11.4f, 7.9f, 12f, 9.1f);
            p.CubicTo(12.6f, 7.9f, 13.6f, 6.2f, 15.7f, 6.2f);
            p.CubicTo(20.2f, 6.2f, 20.6f, 14.2f, 12f, 20.2f);
            p.Close();
            if (filled)
            {
                _p.SetStyle(Paint.Style.Fill);
                c.DrawPath(p, _p);
                _p.SetStyle(Paint.Style.Stroke);
            }
            else c.DrawPath(p, _p);
        }

        /// <summary>刷新：一段圆弧 + 箭头。</summary>
        private void DrawRefresh(Canvas c)
        {
            Arc(c, 4.6f, 4.6f, 19.4f, 19.4f, 300f, 285f);
            Polyline(c, 17.2f, 3.4f, 19.6f, 8.4f, 14.4f, 8.6f);
        }

        /// <summary>地球：外圆 + 赤道 + 经线椭圆。</summary>
        private void DrawGlobe(Canvas c)
        {
            Circle(c, 12f, 12f, 8f);
            Line(c, 4f, 12f, 20f, 12f);
            Arc(c, 8.6f, 4f, 15.4f, 20f, 0f, 360f);
        }

        /// <summary>随机播放：两条交叉箭头。</summary>
        private void DrawShuffle(Canvas c)
        {
            Polyline(c, 3.6f, 7.6f, 7.2f, 7.6f, 16.4f, 16.4f);
            Polyline(c, 16.4f, 16.4f, 20.4f, 16.4f);
            Polyline(c, 3.6f, 16.4f, 7.2f, 16.4f, 16.4f, 7.6f);
            Polyline(c, 16.4f, 7.6f, 20.4f, 7.6f);
            Polyline(c, 18f, 5.2f, 20.6f, 7.6f, 18f, 10f);
            Polyline(c, 18f, 14f, 20.6f, 16.4f, 18f, 18.8f);
        }

        /// <summary>循环 / 单曲循环：两个首尾相接的箭头，单曲时中间加一条竖线。</summary>
        private void DrawRepeat(Canvas c, bool one)
        {
            Polyline(c, 5.4f, 8f, 17.4f, 8f);
            Polyline(c, 17.4f, 8f, 14.8f, 5.4f);
            Polyline(c, 17.4f, 8f, 14.8f, 10.6f);
            Polyline(c, 18.6f, 16f, 6.6f, 16f);
            Polyline(c, 6.6f, 16f, 9.2f, 13.4f);
            Polyline(c, 6.6f, 16f, 9.2f, 18.6f);
            if (one) Line(c, 12f, 10.6f, 12f, 13.4f);
        }

        private void Line(Canvas c, float x1, float y1, float x2, float y2)
        {
            c.DrawLine(x1, y1, x2, y2, _p);
        }

        private void Circle(Canvas c, float cx, float cy, float r)
        {
            c.DrawCircle(cx, cy, r, _p);
        }

        private void Arc(Canvas c, float l, float t, float r, float b, float start, float sweep)
        {
            c.DrawArc(new RectF(l, t, r, b), start, sweep, false, _p);
        }

        private void FillCircle(Canvas c, float cx, float cy, float r)
        {
            _p.SetStyle(Paint.Style.Fill);
            c.DrawCircle(cx, cy, r, _p);
            _p.SetStyle(Paint.Style.Stroke);
        }

        private void Dot(Canvas c, float x, float y)
        {
            FillCircle(c, x, y, 0.95f);
        }

        private void FillRect(Canvas c, float l, float t, float r, float b, float rad, bool round)
        {
            _p.SetStyle(Paint.Style.Fill);
            c.DrawRoundRect(new RectF(l, t, r, b), rad, rad, _p);
            _p.SetStyle(Paint.Style.Stroke);
        }

        private void Polyline(Canvas c, params float[] pts)
        {
            var p = new Path();
            p.MoveTo(pts[0], pts[1]);
            for (int i = 2; i + 1 < pts.Length; i += 2) p.LineTo(pts[i], pts[i + 1]);
            c.DrawPath(p, _p);
        }
    }
}

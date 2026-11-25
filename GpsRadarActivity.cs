using System;
using System.Collections.Generic;
using System.Globalization;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Newtonsoft.Json.Linq;

namespace AhoChatA
{
    /// <summary>雷达配色：从当前 app 主题（日/夜）取色。</summary>
    public sealed class RadarPalette
    {
        public Color PageBg, CardBg, Line, Text, SubText, Accent, Self, Peer, BatteryBg, BatteryText;

        public static RadarPalette FromTheme(Context ctx)
        {
            bool night = ThemeManager.IsNight(ctx);
            var p = new RadarPalette();
            if (night)
            {
                p.PageBg = new Color(IosStyle.NightBackground);
                p.CardBg = new Color(IosStyle.NightSurface);
                p.Line = new Color(IosStyle.NightSeparator);
                p.Text = new Color(IosStyle.NightText);
                p.SubText = new Color(IosStyle.NightSecondary);
                p.Accent = Color.ParseColor("#0A84FF");
                p.Self = Color.ParseColor("#0A84FF");
                p.Peer = Color.ParseColor("#ffd54f");
                p.BatteryBg = Color.ParseColor("#37455f");
                p.BatteryText = Color.ParseColor("#ffffff");
            }
            else
            {
                p.PageBg = new Color(IosStyle.Background);
                p.CardBg = new Color(IosStyle.Surface);
                p.Line = new Color(IosStyle.Separator);
                p.Text = new Color(IosStyle.Text);
                p.SubText = new Color(IosStyle.Secondary);
                p.Accent = new Color(IosStyle.Accent);
                p.Self = new Color(IosStyle.Accent);
                p.Peer = Color.ParseColor("#c0392b");
                p.BatteryBg = Color.ParseColor("#263445");
                p.BatteryText = Color.ParseColor("#ffffff");
            }
            return p;
        }
    }

    /// <summary>电量百分比 → 颜色。&lt;30 红；30-50 红→黄渐变；50-60 黄；60-80 黄→绿渐变；≥80 绿（≥90 纯绿）。</summary>
    public static class BatteryColor
    {
        private static readonly Color Red = Color.ParseColor("#e74c3c");
        private static readonly Color Yellow = Color.ParseColor("#f5c518");
        private static readonly Color Green = Color.ParseColor("#2ecc71");

        private static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.Rgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>pct&lt;0 表示未知，返回中性灰。</summary>
        public static Color Of(int pct)
        {
            if (pct < 0) return Color.ParseColor("#9aa3af");
            if (pct < 30) return Red;
            if (pct < 50) return Lerp(Red, Yellow, (pct - 30) / 20f);   // 30-50 红→黄
            if (pct < 60) return Yellow;                                  // 50-60 黄
            if (pct < 80) return Lerp(Yellow, Green, (pct - 60) / 20f);   // 60-80 黄→绿
            return Green;                                                 // ≥80 绿（含 ≥90）
        }
    }

    /// <summary>
    /// 位置共享 · 上方 GPS 方位雷达盘 + 下方附近设备信息列表（可上下滑动）。
    /// 雷达：设备按相对本机朝向绘制，屏幕上方=本机正前方；E/S/W/N 随机身朝向旋转；
    ///       每个设备只标注「名称 + 直线距离」。
    /// 列表：设备名称、坐标、电量、直线距离、高度差、定位精度。
    /// 距离/方位用系统 Location.DistanceBetween（WGS84 椭球）计算，比自写 Haversine 更准。
    /// </summary>
    [Activity(Label = "@string/act_gps", Exported = true, Theme = "@style/AppTheme.NoActionBar", ScreenOrientation = Android.Content.PM.ScreenOrientation.Portrait)]
    public class GpsRadarActivity : Activity
    {
        private RadarView radar;
        private TextView status;
        private ScrollView listScroll;
        private LinearLayout listBox;
        private Handler handler;
        private Java.Lang.Runnable tick;
        private RadarPalette palette;
        private bool _paletteNight;        // 当前色板对应的日夜状态，切换主题时用于触发重建
        private string lastListSig = "";   // 列表内容签名：不变则不重建，保证滑动流畅

        // 解析后的一帧定位快照
        sealed class PeerInfo
        {
            public string Name, From;
            public double Lat, Lon, Alt;
            public int Battery;
            public float Accuracy;
            public float Dist;      // 直线距离（米，WGS84 椭球）
            public float Bearing;   // 相对本机的绝对方位角（0=北）
        }

        sealed class GpsSnapshot
        {
            public bool SelfOk;
            public double SelfLat, SelfLon, SelfAlt;
            public float SelfBearing, SelfAcc;
            public readonly List<PeerInfo> Peers = new List<PeerInfo>();
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.Apply(this);
            base.OnCreate(savedInstanceState);
            palette = RadarPalette.FromTheme(this);
            _paletteNight = ThemeManager.IsNight(this);

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(palette.PageBg);

            var titleBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            titleBar.SetGravity(GravityFlags.CenterVertical);
            titleBar.SetBackgroundColor(palette.CardBg);
            var back = new TextView(this) { Text = "‹", TextSize = 28f, Gravity = GravityFlags.Center };
            back.SetTextColor(palette.Accent);
            back.Click += (s, e) => Finish();
            titleBar.AddView(back, new LinearLayout.LayoutParams(Dp(44), Dp(44)));
            var title = new TextView(this) { Text = L10n.T("位置共享 · 方位雷达"), TextSize = 17f, Gravity = GravityFlags.CenterVertical };
            title.SetTextColor(palette.Text);
            title.SetTypeface(Android.Graphics.Typeface.Default, Android.Graphics.TypefaceStyle.Bold);
            title.SetPadding(Dp(8), 0, Dp(16), 0);
            titleBar.AddView(title, new LinearLayout.LayoutParams(0, Dp(56), 1f));
            root.AddView(titleBar, new LinearLayout.LayoutParams(-1, Dp(56)));

            status = new TextView(this) { Text = L10n.T("正在获取本机位置…"), TextSize = 14f, Gravity = GravityFlags.Center };
            status.SetTextColor(palette.SubText);
            status.SetPadding(Dp(12), 0, Dp(12), Dp(8));
            root.AddView(status, new LinearLayout.LayoutParams(-1, -2));

            // 上方：雷达圆盘
            radar = new RadarView(this, palette);
            root.AddView(radar, new LinearLayout.LayoutParams(-1, 0, 1f));

            // 下方：附近设备列表（可上下滑动）
            listScroll = new ScrollView(this);
            listScroll.SetBackgroundColor(palette.PageBg);
            listBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            listBox.SetPadding(Dp(16), Dp(4), Dp(16), Dp(16));
            listScroll.AddView(listBox, new ScrollView.LayoutParams(-1, -2));
            root.AddView(listScroll, new LinearLayout.LayoutParams(-1, 0, 1.15f));

            var tip = new TextView(this)
            {
                Text = L10n.T("打开页面后本机位置会广播给在线设备；对方也需开启位置共享才能互相看到。"),
                TextSize = 12f, Gravity = GravityFlags.Center
            };
            tip.SetTextColor(palette.SubText);
            tip.SetPadding(Dp(16), Dp(4), Dp(16), Dp(12));
            root.AddView(tip, new LinearLayout.LayoutParams(-1, -2));

            SetContentView(root);

            // 服务 GPS 共享：仅靠隐藏频道（gpsshare）双向广播收发。
            // 不再发 devreq 定位请求——那属于「聊天定位」链路，会让对方发聊天消息、
            // 弹 toast（locburst/locresp/locfix）和通知栏提醒，违反「共享页之外不可见、无提示」的约定。
            // 每个进入雷达页的设备都会 StartLocationShare 持续广播自己的位置，
            // 对方只要也在雷达页就能收到；离开页面 OnDestroy 停止收发。
            try { MchatBridge.Current?.StartLocationShare(); } catch { }

            handler = new Handler(Looper.MainLooper);
            tick = new Java.Lang.Runnable(() =>
            {
                try
                {
                    var b = MchatBridge.Current as MchatBridge;
                    if (b != null)
                    {
                        var snap = Parse(b.GetGpsStateJson());
                        radar.SetSnapshot(snap);
                        UpdateStatus(snap);
                        RefreshList(snap);
                    }
                }
                catch { }
                handler.PostDelayed(tick, 1000);
            });
            handler.PostDelayed(tick, 200);
        }

        protected override void OnResume()
        {
            base.OnResume();
            IosStyle.ApplySystemBars(this);
            // App 内切换主题后重建，套用新的日夜色板（雷达画笔/列表配色）。
            if (palette != null && ThemeManager.IsNight(this) != _paletteNight)
                Recreate();
        }

        protected override void OnDestroy()
        {
            try { handler?.RemoveCallbacks(tick); } catch { }
            try { MchatBridge.Current?.StopLocationShare(); } catch { }
            base.OnDestroy();
        }

        /// <summary>解析 JSON 并用 WGS84 椭球算法算距离与方位。</summary>
        private GpsSnapshot Parse(string json)
        {
            var s = new GpsSnapshot();
            try
            {
                var o = JObject.Parse(json);
                if (o["self"] != null && o["self"].Type != JTokenType.Null)
                {
                    s.SelfLat = Num(o["self"]["lat"]);
                    s.SelfLon = Num(o["self"]["lon"]);
                    s.SelfAlt = Num(o["self"]["alt"]);
                    // (0,0) 是未定位时的默认值，不能当成真实坐标，否则距离/方位会算到几内亚湾。
                    s.SelfOk = (System.Math.Abs(s.SelfLat) > 1e-6 || System.Math.Abs(s.SelfLon) > 1e-6)
                               && s.SelfLat >= -90 && s.SelfLat <= 90
                               && s.SelfLon >= -180 && s.SelfLon <= 180;
                }
                s.SelfBearing = (float)Num(o["bearing"]);
                s.SelfAcc = (float)Num(o["self"]?["acc"]);

                if (o["peers"] is JArray arr)
                {
                    foreach (var p in arr)
                    {
                        double lat = Num(p["lat"]), lon = Num(p["lon"]);
                        // 坐标合法性校验（参考 GPSTest：剔除无效坐标，避免污染雷达）
                        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) continue;
                        var info = new PeerInfo
                        {
                            Name = (string)(p["name"] ?? p["fromName"]) ?? L10n.T("设备"),
                            From = (string)(p["from"]) ?? "",
                            Lat = lat, Lon = lon,
                            Alt = Num(p["alt"]),
                            Battery = (int)Num(p["battery"]),
                            Accuracy = (float)Num(p["acc"])
                        };
                        // 系统球面算法：results[0]=距离(米)，results[1]=初始方位角(0=北,顺时针)
                        float[] results = new float[3];
                        if (!s.SelfOk) { info.Dist = 0; info.Bearing = 0; }
                        else
                        {
                            try
                            {
                                Android.Locations.Location.DistanceBetween(
                                    s.SelfLat, s.SelfLon, lat, lon, results);
                                info.Dist = results[0];
                                info.Bearing = results[1];
                            }
                            catch { info.Dist = 0; info.Bearing = 0; }
                        }
                        s.Peers.Add(info);
                    }
                }
            }
            catch { }
            return s;
        }

        private void UpdateStatus(GpsSnapshot s)
        {
            status.Text = (s.SelfOk ? L10n.T("本机定位已就绪") : L10n.T("本机定位尚未获取"))
                        + L10n.T("  ·  朝向 ") + s.SelfBearing.ToString("0", CultureInfo.InvariantCulture) + "°"
                        + L10n.T("  ·  附近设备 ") + s.Peers.Count
                        + (s.SelfAcc > 0 ? L10n.T("  ·  精度 ±") + s.SelfAcc.ToString("0.0", CultureInfo.InvariantCulture) + " m" : "");
        }

        private void RefreshList(GpsSnapshot s)
        {
            // 数据没变化就不重建列表——否则每秒 RemoveAllViews 会打断用户上下滑动
            var ordered = new List<PeerInfo>(s.Peers);
            ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            var sb = new System.Text.StringBuilder();
            foreach (var p in ordered)
                sb.Append(p.Name).Append('|').Append(p.Dist.ToString("0", CultureInfo.InvariantCulture))
                  .Append('|').Append(p.Battery).Append('|')
                  .Append(p.Lat.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Lon.ToString("F6", CultureInfo.InvariantCulture)).Append(';');
            string sig = sb.ToString();
            if (sig == lastListSig) return;
            lastListSig = sig;

            int scrollY = listScroll.ScrollY;
            listBox.RemoveAllViews();

            // iOS 分组标题
            var section = new TextView(this)
            {
                Text = L10n.T("附近设备  ·  ") + ordered.Count,
                TextSize = 13f, Gravity = GravityFlags.CenterVertical
            };
            section.SetTextColor(palette.SubText);
            section.SetPadding(Dp(4), Dp(10), Dp(4), Dp(6));
            listBox.AddView(section, new LinearLayout.LayoutParams(-1, -2));

            if (ordered.Count == 0)
            {
                var empty = new TextView(this)
                {
                    Text = L10n.T("附近暂无设备\n（对方需同时打开「位置共享」才能互相看到）"),
                    TextSize = 14f, Gravity = GravityFlags.Center
                };
                empty.SetTextColor(palette.SubText);
                empty.Background = RoundRect(palette.CardBg, 12, palette.Line, 1);
                empty.SetPadding(Dp(12), Dp(28), Dp(12), Dp(28));
                listBox.AddView(empty, CardLp());
                return;
            }

            foreach (var p in ordered)
            {
                string coords = p.Lat.ToString("F6", CultureInfo.InvariantCulture) + ", "
                              + p.Lon.ToString("F6", CultureInfo.InvariantCulture);
                string batt = p.Battery >= 0 ? p.Battery + "%" : L10n.T("未知");
                string dist = p.Dist >= 1000
                    ? (p.Dist / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " km"
                    : p.Dist.ToString("0", CultureInfo.InvariantCulture) + " m";
                // 高度差：任何一方海拔为 0 视为无效基准，避免 0 基准误算
                string alt;
                if (Math.Abs(p.Alt) < 0.01 || Math.Abs(s.SelfAlt) < 0.01)
                    alt = L10n.T("未知");
                else
                {
                    double d = p.Alt - s.SelfAlt;
                    alt = Math.Abs(d) < 0.5 ? L10n.T("持平")
                        : (d > 0 ? L10n.T("高 ") : L10n.T("低 ")) + Math.Abs(d).ToString("0", CultureInfo.InvariantCulture) + " m";
                }
                string acc = p.Accuracy > 0 ? "±" + p.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + " m" : L10n.T("未知");

                // 卡片主体（iOS 圆角白卡）
                var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
                card.Background = RoundRect(palette.CardBg, 16, palette.Line, 1);
                card.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));

                // 头部：圆形首字母头像 + 设备名 + 电量胶囊
                var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                head.SetGravity(GravityFlags.CenterVertical);
                var avatar = new TextView(this)
                {
                    Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name.Substring(0, 1).ToUpperInvariant(),
                    Gravity = GravityFlags.Center, TextSize = 15f
                };
                avatar.SetTextColor(Color.White);
                avatar.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Bold);
                avatar.Background = Oval(BatteryColor.Of(p.Battery));
                var avLp = new LinearLayout.LayoutParams(Dp(34), Dp(34));
                avLp.SetMargins(0, 0, Dp(10), 0);
                head.AddView(avatar, avLp);

                var nameView = new TextView(this) { Text = p.Name, TextSize = 16f };
                nameView.SetTextColor(palette.Text);
                nameView.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Bold);
                head.AddView(nameView, new LinearLayout.LayoutParams(0, -2, 1f));

                var badge = new TextView(this)
                {
                    Text = "⚡ " + batt, TextSize = 12f, Gravity = GravityFlags.Center
                };
                badge.SetTextColor(Color.White);
                badge.Background = RoundRect(BatteryColor.Of(p.Battery), 10, Color.Transparent, 0);
                badge.SetPadding(Dp(8), Dp(3), Dp(8), Dp(3));
                head.AddView(badge, new LinearLayout.LayoutParams(-2, -2));
                card.AddView(head, new LinearLayout.LayoutParams(-1, -2));

                card.AddView(Divider(), DividerLp());
                card.AddView(IosRow(L10n.T("坐标"), coords, false));
                card.AddView(Divider(), DividerLp());
                card.AddView(IosRow(L10n.T("直线距离"), dist, true));
                card.AddView(Divider(), DividerLp());
                card.AddView(IosRow(L10n.T("高度差"), alt, false));
                card.AddView(Divider(), DividerLp());
                card.AddView(IosRow(L10n.T("定位精度"), acc, false));

                listBox.AddView(card, CardLp());
            }
            listScroll.Post(new Java.Lang.Runnable(() => { try { listScroll.ScrollTo(0, scrollY); } catch { } }));
        }

        /// <summary>iOS 风格信息行：左标签、右数值（可着色加粗）。</summary>
        private LinearLayout IosRow(string label, string value, bool accent)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(8), 0, Dp(8));
            row.SetMinimumHeight(Dp(44));
            var l = new TextView(this) { Text = label, TextSize = 14f };
            l.SetTextColor(palette.SubText);
            row.AddView(l, new LinearLayout.LayoutParams(0, -2, 1f));
            var v = new TextView(this) { Text = value, TextSize = 14f, Gravity = GravityFlags.End };
            v.SetTextColor(accent ? palette.Self : palette.Text);
            if (accent) v.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Bold);
            row.AddView(v, new LinearLayout.LayoutParams(0, -2, 1.4f));
            return row;
        }

        private View Divider()
        {
            var d = new View(this) { Background = new Android.Graphics.Drawables.ColorDrawable(palette.Line) };
            d.Alpha = 0.5f;
            return d;
        }

        private LinearLayout.LayoutParams DividerLp()
        {
            return new LinearLayout.LayoutParams(-1, 1);
        }

        private LinearLayout.LayoutParams CardLp()
        {
            var lp = new LinearLayout.LayoutParams(-1, -2);
            lp.SetMargins(0, 0, 0, Dp(24));
            return lp;
        }

        private Android.Graphics.Drawables.GradientDrawable RoundRect(Color fill, int radiusDp, Color stroke, int strokeDp)
        {
            var gd = new Android.Graphics.Drawables.GradientDrawable();
            gd.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
            gd.SetColor(fill);
            gd.SetCornerRadius(Dp(radiusDp));
            if (strokeDp > 0) gd.SetStroke(Dp(strokeDp), stroke);
            return gd;
        }

        private static Android.Graphics.Drawables.GradientDrawable Oval(Color fill)
        {
            var gd = new Android.Graphics.Drawables.GradientDrawable();
            gd.SetShape(Android.Graphics.Drawables.ShapeType.Oval);
            gd.SetColor(fill);
            return gd;
        }

        private static double Num(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            double d;
            return double.TryParse((string)t, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private int Dp(int v) => IosStyle.Dp(this, v);

        /// <summary>雷达盘：设备机体朝上，ESWN 随机身朝向旋转；只显示设备名 + 距离，不可点击。</summary>
        private sealed class RadarView : View
        {
            private readonly Paint grid, ring, text, selfPaint, peerPaint, dirText, axis;
            private readonly RadarPalette pal;
            private GpsSnapshot snap;

            public RadarView(Context ctx, RadarPalette palette) : base(ctx)
            {
                pal = palette;
                grid = NewPaint(pal.Line, 2f);
                ring = AA(NewPaint(pal.Line, 1.2f));
                axis = NewPaint(pal.Line, 1f);
                text = TextPaint(pal.Text, 13f);
                dirText = TextPaint(pal.Accent, 21f);
                dirText.SetTypeface(Android.Graphics.Typeface.DefaultBold);
                selfPaint = NewPaint(pal.Self, 0f);
                peerPaint = AA(NewPaint(pal.Peer, 0f));
            }

            static Paint NewPaint(Color c, float w)
            {
                var p = new Paint { Color = c, StrokeWidth = w };
                p.SetStyle(Paint.Style.Stroke);
                return p;
            }

            static Paint TextPaint(Color c, float size)
            {
                var p = new Paint { Color = c, TextSize = size, AntiAlias = true };
                p.SetStyle(Paint.Style.Fill);
                p.TextAlign = Paint.Align.Center;
                return p;
            }

            static Paint AA(Paint p) { p.AntiAlias = true; return p; }

            private int Dp(int v) => IosStyle.Dp(Context, v);

            public void SetSnapshot(GpsSnapshot s) { snap = s; Invalidate(); }

            protected override void OnDraw(Canvas canvas)
            {
                base.OnDraw(canvas);
                int w = Width, h = Height;
                if (w <= 0 || h <= 0) return;
                float cx = w / 2f, cy = h / 2f;
                float radius = Math.Min(w, h) / 2f - Dp(44);
                if (radius < 40f) radius = Math.Min(w, h) / 2f - 16f;
                var s = snap;

                // 底盘 + 距离圈
                paintFill(canvas, cx, cy, radius, pal.PageBg);
                for (int i = 1; i <= 3; i++) canvas.DrawCircle(cx, cy, radius * i / 3f, ring);
                canvas.DrawCircle(cx, cy, radius, grid);

                float selfBearing = s == null ? 0f : s.SelfBearing;
                canvas.DrawLine(cx, cy - radius, cx, cy + radius, axis);
                canvas.DrawLine(cx - radius, cy, cx + radius, cy, axis);

                // E/S/W/N 随机身朝向旋转（北的屏幕角度 = 0 - bearing）
                DrawDirection(canvas, "N", 0, selfBearing, cx, cy, radius);
                DrawDirection(canvas, "E", 90, selfBearing, cx, cy, radius);
                DrawDirection(canvas, "S", 180, selfBearing, cx, cy, radius);
                DrawDirection(canvas, "W", 270, selfBearing, cx, cy, radius);

                // 中心朝向指针固定指屏幕正上方
                selfPaint.SetStyle(Paint.Style.Fill);
                float a = Dp(13);
                var path = new Path();
                path.MoveTo(cx, cy - a);
                path.LineTo(cx - a * 0.7f, cy + a * 0.7f);
                path.LineTo(cx + a * 0.7f, cy + a * 0.7f);
                path.Close();
                canvas.DrawPath(path, selfPaint);

                if (s == null || s.Peers.Count == 0)
                {
                    text.TextAlign = Paint.Align.Center;
                    canvas.DrawText(L10n.T("附近暂无设备"), cx, cy + radius + Dp(30), text);
                    return;
                }

                double maxDist = 1;
                foreach (var p in s.Peers) if (p.Dist > maxDist) maxDist = p.Dist;
                foreach (var p in s.Peers)
                {
                    double ratio = maxDist <= 0 ? 0.5 : Math.Min(1.0, p.Dist / (maxDist * 1.15));
                    if (p.Dist <= 0) ratio = 0.35;
                    float r = (float)(radius * (0.18 + 0.75 * ratio));
                    // 相对方位：世界方位 - 机身朝向
                    double rel = (p.Bearing - selfBearing + 360) % 360;
                    double rad = rel * Math.PI / 180.0;
                    float px = cx + (float)(r * Math.Sin(rad));
                    float py = cy - (float)(r * Math.Cos(rad));

                    canvas.DrawLine(cx, cy, px, py, axis);
                    peerPaint.SetStyle(Paint.Style.Fill);
                    peerPaint.Color = BatteryColor.Of(p.Battery);   // 雷达点按电量着色
                    canvas.DrawCircle(px, py, Dp(7), peerPaint);

                    // 只显示设备名 + 距离
                    string distTxt = p.Dist >= 1000
                        ? (p.Dist / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "km"
                        : p.Dist.ToString("0", CultureInfo.InvariantCulture) + "m";
                    string label = p.Name + "  " + distTxt;
                    canvas.DrawText(label, px, py - Dp(16), text);
                }
            }

            void DrawDirection(Canvas c, string label, double worldBearing, float selfBearing,
                               float cx, float cy, float radius)
            {
                double rel = (worldBearing - selfBearing + 360) % 360;
                double rad = rel * Math.PI / 180.0;
                float rr = radius + Dp(22);
                float x = cx + (float)(rr * Math.Sin(rad));
                float y = cy - (float)(rr * Math.Cos(rad));
                dirText.TextAlign = Paint.Align.Center;
                c.DrawText(label, x, y + Dp(5), dirText);
            }

            void paintFill(Canvas c, float cx, float cy, float r, Color color)
            {
                var p = new Paint { Color = color, AntiAlias = true };
                p.SetStyle(Paint.Style.Fill);
                c.DrawCircle(cx, cy, r, p);
            }
        }
    }
}

using System;
using System.Globalization;
using Android.Content;
using Android.Locations;
using Android.OS;

namespace AhoChatA
{
    /// <summary>
    /// 定位上报：收到频道内的「定位请求」后自动开始定位，并把结果按
    /// <c>时间-纬度,经度</c>（例：18:24:12-23.23,109.032）持续发进聊天消息。
    ///
    /// 两种运行模式：
    ///   · 连续模式（burstMs &gt; 0）：被远程请求时使用。1 秒采一次、不设位移阈值，
    ///     从拿到第一个点开始**连续上报 burstMs 毫秒**，到点自动停，避免长期占 GPS。
    ///     若一直没拿到点，最多等 graceMs 毫秒（GPS 冷启动较慢）后自动停。
    ///   · 常驻模式（burstMs &lt;= 0）：用户手动开启的长期共享，20 秒 / 8 米节流。
    /// </summary>
    public sealed class LocationReporter : Java.Lang.Object, ILocationListener
    {
        public static LocationReporter Current { get; private set; }
        public static bool Running { get; private set; }

        /// <summary>当前连续上报模式的时长（秒）；0 表示常驻模式。</summary>
        public static int BurstSeconds { get; private set; }

        /// <summary>上一次上报的时间与坐标（用于常驻模式节流）。</summary>
        private static DateTime _lastAt = DateTime.MinValue;
        private static double _lastLat, _lastLon;

        /// <summary>
        /// 文本格式化覆盖（由 MchatBridge 注入：设备名 / IP / 电量只有桥接层拿得到）。
        /// 未设置时退回 <see cref="Format"/> 的简版。
        /// </summary>
        public static Func<DateTime, double, double, string> Formatter;

        private LocationManager _lm;
        private readonly Action<string> _onFix;      // 参数是格式化好的 L10n.T("HH:mm:ss-纬度,经度")
        private readonly Action<string> _onError;
        private readonly Action _onDone;

        private int _intervalMs = 20000;
        private float _minDistance = 8f;
        private int _burstMs;
        private int _graceMs;
        private long _startedAtMs;
        private long _firstFixAtMs;
        private Handler _timer;
        private bool _finished;

        private LocationReporter(Action<string> onFix, Action<string> onError, Action onDone)
        {
            _onFix = onFix;
            _onError = onError;
            _onDone = onDone;
        }

        /// <summary>系统定位权限是否已授予。</summary>
        public static bool HasPermission()
        {
            try
            {
                var ctx = Android.App.Application.Context;
                if (Build.VERSION.SdkInt < BuildVersionCodes.M) return true;
                return ctx.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted
                    || ctx.CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation) == Android.Content.PM.Permission.Granted;
            }
            catch { return false; }
        }

        /// <summary>
        /// 开始定位。onFix 每拿到一个有效点就回调一次。
        /// </summary>
        /// <param name="intervalMs">采样间隔（毫秒）。</param>
        /// <param name="minDistance">位移阈值（米）；0 表示不设阈值。</param>
        /// <param name="burstMs">连续上报时长（毫秒）；&lt;=0 为常驻模式。</param>
        /// <param name="firstFixGraceMs">连续模式下等待首个定位点的最长时间（毫秒）。</param>
        /// <param name="onDone">连续上报结束时回调（自动停止也会触发）。</param>
        public static bool Start(Action<string> onFix,
                                 Action<string> onError = null,
                                 int intervalMs = 20000,
                                 float minDistance = 8f,
                                 int burstMs = 0,
                                 int firstFixGraceMs = 15000,
                                 Action onDone = null)
        {
            // 重复点击 = 重新计时（而不是被上一次的窗口吞掉）
            if (Running) Stop();

            if (!HasPermission())
            {
                if (onError != null) onError("没有定位权限，请在系统设置里允许本应用访问位置");
                return false;
            }

            try
            {
                var ctx = Android.App.Application.Context;
                var lm = (LocationManager)ctx.GetSystemService(Context.LocationService);
                if (lm == null) { if (onError != null) onError("本机不支持定位"); return false; }

                bool gps = false, net = false;
                try { gps = lm.IsProviderEnabled(LocationManager.GpsProvider); } catch { }
                try { net = lm.IsProviderEnabled(LocationManager.NetworkProvider); } catch { }
                if (!gps && !net)
                {
                    if (onError != null) onError("定位服务未开启，请打开系统定位开关");
                    return false;
                }

                var r = new LocationReporter(onFix, onError, onDone) { _lm = lm };
                r._intervalMs = intervalMs > 0 ? intervalMs : 1000;
                r._minDistance = minDistance;
                r._burstMs = burstMs;
                r._graceMs = firstFixGraceMs > 0 ? firstFixGraceMs : 15000;
                r._startedAtMs = SystemClock.ElapsedRealtime();

                // 必须指定主线程 Looper：本方法常由「收到对方 devreq 定位请求」的后台消息线程调用，
                // 不带 Looper 的重载内部用 Looper.myLooper()，后台线程没有 Looper 会抛
                // "Can't create handler inside thread that has not called Looper.prepare()"，
                // 表现为对方看到「XX 定位失败」的红色报错 Toast。
                Looper looper = Looper.MainLooper;
                if (gps) r._lm.RequestLocationUpdates(LocationManager.GpsProvider, r._intervalMs, r._minDistance, r, looper);
                if (net) r._lm.RequestLocationUpdates(LocationManager.NetworkProvider, r._intervalMs, r._minDistance, r, looper);

                Current = r;
                Running = true;
                BurstSeconds = burstMs > 0 ? (int)Math.Round(burstMs / 1000.0) : 0;
                _lastAt = DateTime.MinValue;

                // 先尝试用最后已知位置立刻回一条，避免等 GPS 冷启动
                try
                {
                    var last = r._lm.GetLastKnownLocation(LocationManager.GpsProvider)
                            ?? r._lm.GetLastKnownLocation(LocationManager.NetworkProvider);
                    if (last != null) r.Emit(last);
                }
                catch { }

                // 连续模式：定时检查窗口是否结束
                if (burstMs > 0)
                {
                    try
                    {
                        r._timer = new Handler(Looper.MainLooper);
                        r._timer.PostDelayed(new Java.Lang.Runnable(r.Tick), 250);
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                if (onError != null) onError("启动定位失败：" + ex.Message);
                return false;
            }
        }

        public static void Stop()
        {
            var r = Current;
            Current = null;
            Running = false;
            BurstSeconds = 0;
            if (r == null) return;

            r._finished = true;
            try { r._timer?.RemoveCallbacksAndMessages(null); } catch { }
            try { r._timer?.Dispose(); } catch { }
            r._timer = null;
            try { if (r._lm != null) r._lm.RemoveUpdates(r); } catch { }
            try { r.Dispose(); } catch { }
            try { r._onDone?.Invoke(); } catch { }
        }

        /// <summary>连续模式定时器：判断 5 秒窗口是否走完（没拿到点则按宽限期收尾）。</summary>
        private void Tick()
        {
            try
            {
                if (_finished || !Running || Current != this) return;
                long now = SystemClock.ElapsedRealtime();
                bool expired = _firstFixAtMs > 0
                    ? now - _firstFixAtMs >= _burstMs
                    : now - _startedAtMs >= _graceMs;
                if (expired)
                {
                    Android.Util.Log.Info("Mchat", "[Loc] burst finished after "
                        + (now - _startedAtMs) + "ms (firstFix=" + (_firstFixAtMs > 0) + ")");
                    Stop();
                    return;
                }
                _timer?.PostDelayed(new Java.Lang.Runnable(Tick), 250);
            }
            catch { }
        }

        /// <summary>把坐标格式化成 "18:24:12-23.23,109.032"（纬度在前，与需求示例一致）。</summary>
        public static string Format(DateTime when, double lat, double lon)
        {
            return when.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                 + "-" + lat.ToString("0.######", CultureInfo.InvariantCulture)
                 + "," + lon.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private void Emit(Location loc)
        {
            try
            {
                if (loc == null) return;
                var now = DateTime.Now;

                bool burst = _burstMs > 0;
                if (!burst)
                {
                    // 常驻模式节流：20 秒内且位移 < 8 米就跳过
                    if (_lastAt != DateTime.MinValue)
                    {
                        var dt = (now - _lastAt).TotalSeconds;
                        float[] dist = new float[1];
                        Location.DistanceBetween(_lastLat, _lastLon, loc.Latitude, loc.Longitude, dist);
                        if (dt < 20 && dist[0] < 8f) return;
                    }
                }

                _lastAt = now;
                _lastLat = loc.Latitude;
                _lastLon = loc.Longitude;
                if (burst && _firstFixAtMs == 0) _firstFixAtMs = SystemClock.ElapsedRealtime();

                var fmt = Formatter;
                var text = fmt != null ? fmt(now, loc.Latitude, loc.Longitude)
                                       : Format(now, loc.Latitude, loc.Longitude);
                if (_onFix != null)
                {
                    try { _onFix(text); } catch { }
                }
            }
            catch { }
        }

        /* ===================== ILocationListener ===================== */

        public void OnLocationChanged(Location location) { Emit(location); }

        public void OnProviderDisabled(string provider) { }

        public void OnProviderEnabled(string provider) { }

        public void OnStatusChanged(string provider, Availability status, Bundle extras) { }
    }
}

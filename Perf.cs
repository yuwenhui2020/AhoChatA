using System;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 轻量启动打点：只做一次 Log.Info，正式包也几乎没有开销。
    /// 用法：Perf.Mark("标签") 会把「距上一个打点」和「距 App 启动」一起打出来，
    /// 冷启动慢的时候按这个顺序看就能定位是哪一步吃掉了时间。
    /// </summary>
    public static class Perf
    {
        private static long _t0;
        private static long _last;
        private static readonly long ProcStart = ReadProcStart();

        private static long ReadProcStart()
        {
            try
            {
                // /proc/self/stat 第 22 项是进程启动时间（时钟滴答），换算成 ms
                var s = System.IO.File.ReadAllText("/proc/self/stat");
                int rp = s.LastIndexOf(')');
                if (rp < 0) return 0;
                var parts = s.Substring(rp + 2).Split(' ');
                if (parts.Length < 20) return 0;
                long ticks = long.Parse(parts[19]);
                long hz = 100; // Android 上 USER_HZ 固定 100
                return ticks * 1000 / hz;
            }
            catch { return 0; }
        }

        /// <summary>当前距进程启动的毫秒数（拿不到就退化为首次调用计时）。</summary>
        public static long SinceProcessStart()
        {
            long now = System.Environment.TickCount;
            if (ProcStart > 0) return now - ProcStart;
            if (_t0 == 0) _t0 = now;
            return now - _t0;
        }

        public static void Mark(string tag)
        {
            try
            {
                long now = System.Environment.TickCount;
                if (_t0 == 0) { _t0 = now; _last = now; }
                Log.Info("Perf", string.Format("[{0,5}ms | +{1,4}ms] {2}",
                    SinceProcessStart(), now - _last, tag));
                _last = now;
            }
            catch { }
        }

        public static void Reset()
        {
            _t0 = 0; _last = 0;
        }
    }
}

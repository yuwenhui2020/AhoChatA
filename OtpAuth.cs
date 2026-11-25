using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Content;

namespace AhoChatA
{
    /// <summary>
    /// 双因素认证（2FA）：每台设备一把独立密钥，按 RFC 6238（TOTP）每 30 秒产生一个 6 位动态口令。
    ///
    /// 设计取舍：
    ///  · **每台设备一个 OTP**：密钥在首次开启时随机生成、只存在本机 SharedPreferences 里，
    ///    不广播、不同步。所以 A 机的口令只能开 A 机的网页功能，B 机不受影响。
    ///  · **口令显示位置**：「设备」页「本机」下方（每 30 秒自动变）。开关在「我的 → 隐私与安全」。
    ///  · **摄像头 / 屏幕共享不校验**：这两路是 VLC、播放器直连的裸流（/cam/...），
    ///    播放器没法输验证码，所以整条 /cam 前缀都不做拦截。
    ///  · **本 App 内部观看放行**：带 X-Mchat-App 头的请求（App 内全屏观看画笔）直接通过，
    ///    否则「自己看自己」也要输口令，体验很怪。
    ///  · **放行后 8 小时内免输**：按来源 IP 记一笔授权，同时下发 Cookie（浏览器刷新也不会被弹）。
    /// </summary>
    public static class OtpAuth
    {
        public const string CookieName = "lcotp";
        public const string SubmitPath = "/__otp";
        public const int PeriodSec = 30;
        public const int Digits = 6;

        private const string PrefsName = "Mchat";
        private const string KeyOn = "otp2fa";
        private const string KeySecret = "otpSecret";
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly object Sync = new object();
        private static string _secret;

        /// <summary>已放行的来源 IP → 到期时间（内存态，重启即清空，够用且不留痕）。</summary>
        private static readonly Dictionary<string, DateTime> Granted = new Dictionary<string, DateTime>();
        private static readonly TimeSpan GrantTtl = TimeSpan.FromHours(8);

        /* ===================== 开关 / 密钥 ===================== */

        public static bool Enabled
        {
            get
            {
                try { return Prefs().GetBoolean(KeyOn, false); }
                catch { return false; }
            }
            set
            {
                try { Prefs().Edit().PutBoolean(KeyOn, value).Apply(); } catch { }
                // 关掉就把所有已放行设备清掉，免得"关了开关网页还能进"
                if (!value) ClearGrants();
            }
        }

        /// <summary>本机密钥（Base32，20 字节随机）。第一次用到时才生成并落盘。</summary>
        public static string Secret
        {
            get
            {
                lock (Sync)
                {
                    if (string.IsNullOrEmpty(_secret))
                    {
                        _secret = LoadOrCreateSecret();
                        if (string.IsNullOrEmpty(_secret)) _secret = NewSecret();
                    }
                    return _secret;
                }
            }
        }

        private static string LoadOrCreateSecret()
        {
            try
            {
                var s = Prefs().GetString(KeySecret, null);
                if (!string.IsNullOrEmpty(s)) return s;
                s = NewSecret();
                Prefs().Edit().PutString(KeySecret, s).Apply();
                return s;
            }
            catch { return null; }
        }

        private static string NewSecret()
        {
            var raw = new byte[20];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            return ToBase32(raw);
        }

        private static ISharedPreferences Prefs()
        {
            return Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        }

        /* ===================== 口令 ===================== */

        /// <summary>当前 6 位口令（设备页显示的就是这个）。</summary>
        public static string CurrentCode
        {
            get { return CodeAt(DateTime.UtcNow); }
        }

        /// <summary>距下次换码还有几秒（用于画进度/提示）。</summary>
        public static int SecondsLeft
        {
            get
            {
                long s = (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds;
                return (int)(PeriodSec - (s % PeriodSec));
            }
        }

        public static string CodeAt(DateTime utcNow)
        {
            long step = (long)(utcNow - UnixEpoch).TotalSeconds / PeriodSec;
            return CodeForStep(step);
        }

        private static string CodeForStep(long step)
        {
            try
            {
                var key = FromBase32(Secret);
                var counter = new byte[8];
                for (int i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }

                byte[] hash;
                using (var h = new HMACSHA1(key)) hash = h.ComputeHash(counter);

                int off = hash[hash.Length - 1] & 0x0F;
                int bin = ((hash[off] & 0x7F) << 24)
                        | ((hash[off + 1] & 0xFF) << 16)
                        | ((hash[off + 2] & 0xFF) << 8)
                        | (hash[off + 3] & 0xFF);
                int code = bin % 1000000;
                return code.ToString("D6");
            }
            catch
            {
                return "------";
            }
        }

        /// <summary>
        /// 校验口令。前后各放宽一个 30 秒窗口：手机和浏览器的时钟差几秒、
        /// 或者用户刚好卡在换码那一瞬间输入，都不该判失败。
        /// </summary>
        public static bool Verify(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            var sb = new StringBuilder();
            foreach (var ch in code)
                if (ch >= '0' && ch <= '9') sb.Append(ch);
            if (sb.Length != Digits) return false;

            string input = sb.ToString();
            long step = (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds / PeriodSec;
            for (long d = -1; d <= 1; d++)
            {
                var c = CodeForStep(step + d);
                if (SlowEquals(c, input)) return true;
            }
            return false;
        }

        /// <summary>定长比较，别让"第几位开始不一样"变成可探测的旁路。</summary>
        private static bool SlowEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /* ===================== 放行状态 ===================== */

        public static void Grant(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            lock (Sync)
            {
                Granted[ip] = DateTime.UtcNow + GrantTtl;
                // 顺手清掉过期项，避免开机很久后字典无限长（局域网设备数有限，主要是卫生）
                if (Granted.Count > 64)
                {
                    var dead = new List<string>();
                    foreach (var kv in Granted)
                        if (kv.Value < DateTime.UtcNow) dead.Add(kv.Key);
                    foreach (var k in dead) Granted.Remove(k);
                }
            }
        }

        public static bool IsGranted(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            lock (Sync)
            {
                DateTime exp;
                if (!Granted.TryGetValue(ip, out exp)) return false;
                if (exp < DateTime.UtcNow) { Granted.Remove(ip); return false; }
                return true;
            }
        }

        public static void ClearGrants()
        {
            lock (Sync) Granted.Clear();
        }

        /// <summary>放行 Cookie 的值：与密钥、来源 IP 绑定，换个 IP 就失效。</summary>
        public static string TokenFor(string ip)
        {
            try
            {
                var raw = Encoding.UTF8.GetBytes(Secret + "|" + (ip ?? ""));
                byte[] h;
                using (var sha = SHA256.Create()) h = sha.ComputeHash(raw);
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
            catch { return "0"; }
        }

        public static bool CookieOk(string value, string ip)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(ip)) return false;
            return SlowEquals(value.Trim(), TokenFor(ip));
        }

        /// <summary>
        /// 这个路径压根不需要口令：验证码提交入口本身，以及摄像头 / 屏幕共享（/cam/...）。
        /// 后者是给 VLC 这类裸播放器直连的，播放器不会输验证码，所以整条前缀放行。
        /// </summary>
        public static bool IsExemptPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path == "/cam" || path.StartsWith("/cam/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /* ===================== Base32 ===================== */

        private static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int bits = 0, value = 0;
            foreach (var b in data)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
            return sb.ToString();
        }

        private static byte[] FromBase32(string s)
        {
            var bits = new List<byte>();
            foreach (var ch in s)
            {
                int v = Base32Alphabet.IndexOf(char.ToUpperInvariant(ch));
                if (v < 0) continue;
                for (int i = 4; i >= 0; i--) bits.Add((byte)((v >> i) & 1));
            }
            var outp = new List<byte>();
            for (int i = 0; i + 8 <= bits.Count; i += 8)
            {
                int b = 0;
                for (int j = 0; j < 8; j++) b = (b << 1) | bits[i + j];
                outp.Add((byte)b);
            }
            return outp.ToArray();
        }
    }
}

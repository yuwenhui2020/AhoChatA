using System;
using System.Collections.Generic;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// 本机状态快照：设备名、IP、电量。
    ///
    /// 这些是 Mchat 频道的「常驻信息」——挂在聊天窗口顶端的状态条里，
    /// 同时也会以 devstat 帧广播给频道内其他设备（不进聊天记录、不渲染气泡）。
    /// </summary>
    public static class DeviceStatus
    {
        /// <summary>自定义设备名（用户可在设置里改），为空则退回机型名。</summary>
        public static string CustomName { get; set; }

        /// <summary>设备显示名：优先自定义名，否则 "品牌 型号"。</summary>
        public static string DeviceName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName.Trim();
                try
                {
                    var brand = Android.OS.Build.Brand ?? "";
                    var model = Android.OS.Build.Model ?? "";
                    if (string.IsNullOrEmpty(brand)) return string.IsNullOrEmpty(model) ? "Android" : model;
                    if (model.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) return model;
                    return brand + " " + model;
                }
                catch { return "Android"; }
            }
        }

        // 网卡枚举有成本（多个页面每 1~2 秒刷新一次 IP），做 1 秒短缓存降低主线程开销。
        private const long IpCacheMs = 1000;
        private static readonly object _ipLock = new object();
        private static string _ipCache;
        private static long _ipCacheAt;
        private static string _ip6Cache;
        private static long _ip6CacheAt;

        /// <summary>本机局域网 IP（优先 wlan 网段），拿不到返回 "0.0.0.0"。</summary>
        public static string Ip
        {
            get
            {
                long now = Android.OS.SystemClock.ElapsedRealtime();
                lock (_ipLock)
                {
                    if (_ipCache != null && now - _ipCacheAt < IpCacheMs) return _ipCache;
                }
                var v = ComputeIp();
                lock (_ipLock) { _ipCache = v; _ipCacheAt = now; }
                return v;
            }
        }

        private static string ComputeIp()
        {
            try
            {
                string best = null;
                var en = Java.Net.NetworkInterface.NetworkInterfaces;
                while (en != null && en.HasMoreElements)
                {
                    var ni = en.NextElement() as Java.Net.NetworkInterface;
                    if (ni == null) continue;
                    try
                    {
                        if (!ni.IsUp || ni.IsLoopback) continue;
                    }
                    catch { continue; }

                    string name = ni.Name ?? "";
                    bool wifiLike = name.StartsWith("wlan") || name.StartsWith("eth") || name.StartsWith("ap");

                    var addrs = ni.InterfaceAddresses;
                    if (addrs == null) continue;
                    foreach (var o in addrs)
                    {
                        var ia = o as Java.Net.InterfaceAddress;
                        if (ia == null) continue;
                        var a4 = ia.Address as Java.Net.Inet4Address;
                        if (a4 == null) continue;
                        var s = a4.HostAddress;
                        if (string.IsNullOrEmpty(s) || s.StartsWith("127.")) continue;
                        if (wifiLike) return s;
                        if (best == null) best = s;
                    }
                }
                return best ?? "0.0.0.0";
            }
            catch { return "0.0.0.0"; }
        }

        /// <summary>
        /// 本机局域网 IPv6 地址（优先全局 / ULA，其次链路本地 fe80::）。
        /// 拿不到返回 "::"。用于界面展示与"填本机 IP 作为中转站"的提示。
        /// </summary>
        public static string Ipv6
        {
            get
            {
                long now = Android.OS.SystemClock.ElapsedRealtime();
                lock (_ipLock)
                {
                    if (_ip6Cache != null && now - _ip6CacheAt < IpCacheMs) return _ip6Cache;
                }
                var v = ComputeIpv6();
                lock (_ipLock) { _ip6Cache = v; _ip6CacheAt = now; }
                return v;
            }
        }

        private static string ComputeIpv6()
        {
            try
            {
                string linkLocal = null;
                var en = Java.Net.NetworkInterface.NetworkInterfaces;
                while (en != null && en.HasMoreElements)
                {
                    var ni = en.NextElement() as Java.Net.NetworkInterface;
                    if (ni == null) continue;
                    try { if (!ni.IsUp || ni.IsLoopback) continue; }
                    catch { continue; }

                    var addrs = ni.InterfaceAddresses;
                    if (addrs == null) continue;
                    foreach (var o in addrs)
                    {
                        var ia = o as Java.Net.InterfaceAddress;
                        if (ia == null) continue;
                        var a6 = ia.Address as Java.Net.Inet6Address;
                        if (a6 == null) continue;
                        var s = a6.HostAddress;
                        if (string.IsNullOrEmpty(s)) continue;

                        // 去掉 scope id（fe80::1%wlan0）
                        int pct = s.IndexOf('%');
                        if (pct > 0) s = s.Substring(0, pct);
                        if (s == "::" || s == "::1") continue;
                        if (s.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) continue;   // IPv4 映射

                        if (s.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                        {
                            // 链路本地地址只有同网段可用，作为兜底
                            if (linkLocal == null) linkLocal = s;
                            continue;
                        }
                        return s;   // 全局 / ULA 优先
                    }
                }
                return linkLocal ?? "::";
            }
            catch { return "::"; }
        }

        /// <summary>本机所有可用 IPv4 地址（排除回环与 127.*；wlan/eth/ap 优先排在前面）。</summary>
        public static List<string> AllIps
        {
            get
            {
                var seen = new HashSet<string>();
                var wifi = new List<string>();
                var others = new List<string>();
                try
                {
                    var en = Java.Net.NetworkInterface.NetworkInterfaces;
                    while (en != null && en.HasMoreElements)
                    {
                        var ni = en.NextElement() as Java.Net.NetworkInterface;
                        if (ni == null) continue;
                        try { if (!ni.IsUp || ni.IsLoopback) continue; }
                        catch { continue; }
                        var name = ni.Name ?? "";
                        var addrs = ni.InterfaceAddresses;
                        if (addrs == null) continue;
                        foreach (var o in addrs)
                        {
                            var ia = o as Java.Net.InterfaceAddress;
                            if (ia == null) continue;
                            var a4 = ia.Address as Java.Net.Inet4Address;
                            if (a4 == null) continue;
                            var s = a4.HostAddress;
                            if (string.IsNullOrEmpty(s) || s.StartsWith("127.")) continue;
                            bool wifiLike = name.StartsWith("wlan") || name.StartsWith("eth") || name.StartsWith("ap");
                            if (wifiLike) { if (seen.Add(s)) wifi.Add(s); }
                            else { if (seen.Add(s)) others.Add(s); }
                        }
                    }
                }
                catch { }
                wifi.AddRange(others);
                if (wifi.Count == 0) wifi.Add("0.0.0.0");
                return wifi;
            }
        }

        /// <summary>电量百分比 0~100，拿不到返回 -1。</summary>
        public static int Battery
        {
            get
            {
                try
                {
                    var ctx = Android.App.Application.Context;
                    var bm = (Android.OS.BatteryManager)ctx.GetSystemService(Android.Content.Context.BatteryService);
                    if (bm == null) return -1;
                    int pct = bm.GetIntProperty((int)Android.OS.BatteryProperty.Capacity);
                    return pct < 0 ? -1 : Math.Min(100, pct);
                }
                catch { return -1; }
            }
        }

        /// <summary>是否正在充电。</summary>
        public static bool Charging
        {
            get
            {
                try
                {
                    var ctx = Android.App.Application.Context;
                    var bm = (Android.OS.BatteryManager)ctx.GetSystemService(Android.Content.Context.BatteryService);
                    if (bm == null) return false;
                    int st = bm.GetIntProperty((int)Android.OS.BatteryProperty.Status);
                    return st == 2 || st == 5;   // Charging / Full
                }
                catch { return false; }
            }
        }

        /// <summary>电量文本（不含 emoji；图标由 UI 用矢量电池/闪电绘制），例如 "78%"。</summary>
        public static string BatteryText
        {
            get
            {
                int pct = Battery;
                if (pct < 0) return "--";
                return pct + "%";
            }
        }

        /// <summary>打包成可广播的状态字典。</summary>
        public static Dictionary<string, object> Snapshot()
        {
            return new Dictionary<string, object>
            {
                ["name"] = DeviceName,
                ["ip"] = Ip,
                ["battery"] = Battery,
                ["charging"] = Charging,
                ["batteryText"] = BatteryText
            };
        }

        /// <summary>一行摘要（顶部状态栏 / 悬浮窗标题用）。</summary>
        public static string Summary()
        {
            return DeviceName + " · " + Ip + " · " + BatteryText;
        }
    }
}

// ============================================================
// 投放到 TV 端（手机 → C:\root\code\TV）
//
// 设计原则（与用户要求一致）：
//   · TV 端（HttpServer.cs 的 /api/web_play）收到「真实直链」后，用自身的 ExoPlayer +
//     PlaybackCache（SimpleCache 1.5GB LRU）自行读取真实 m3u8 / mp4 并缓存播放；
//   · 本机（手机）对 TV 端不做任何代理转发——因此【绝不能】把手机本地回环代理地址
//     （127.0.0.1:8891 / WebServer 代理 StreamUrl）发给 TV，否则 TV 无法解析、必然黑屏；
//   · 这里只负责「发现 TV」+「把真实直链投过去」，流程被极大简化。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Android.Content;
using Android.Util;
using Newtonsoft.Json;

namespace AhoChatA.Vod
{
    /// <summary>一台被发现 / 指定的 TV 端。</summary>
    public class TvDevice
    {
        public string Ip;
        public string Name;
        public override string ToString() => string.IsNullOrEmpty(Name) ? Ip : (Name + " · " + Ip);
    }

    public static class VodCast
    {
        public const int TvPort = 8080;
        const string PrefIp = "vod_tv_ip";

        public static string LastIp(Context ctx)
        {
            try { return ctx.GetSharedPreferences("Mchat", FileCreationMode.Private).GetString(PrefIp, ""); }
            catch { return ""; }
        }

        public static void SaveIp(Context ctx, string ip)
        {
            try { ctx.GetSharedPreferences("Mchat", FileCreationMode.Private).Edit().PutString(PrefIp, ip ?? "").Apply(); }
            catch { }
        }

        /// <summary>给定一个剧集，返回可投给 TV 的「真实直链」：优先已解析的 Real，否则现场嗅探解析出最终 m3u8/mp4。</summary>
        public static string RealCastUrl(VodEpisode ep)
        {
            if (ep == null) return "";
            string u = ep.PlayUrl;                 // Real 非空则取 Real，否则取 Url
            if (VodApi.IsDirectPlayUrl(u)) return u;
            return VodApi.ResolvePlayUrl(u);       // 嗅探型源：解析出最终真实链接（带 7 天缓存）
        }

        /// <summary>局域网扫描 TV 端：对子网每个 IP 探测 8080，再用 /api/info 确认是本程序 TV。</summary>
        public static async Task<List<TvDevice>> ScanAsync()
        {
            var result = new List<TvDevice>();
            try
            {
                string local = LocalIp();
                if (local == null) return result;
                var seg = local.Split('.');
                if (seg.Length != 4 || !int.TryParse(seg[3], out int me)) return result;
                var tasks = new List<Task>();
                for (int i = 1; i <= 254; i++)
                {
                    if (i == me) continue;
                    string ip = seg[0] + "." + seg[1] + "." + seg[2] + "." + i;
                    tasks.Add(Task.Run(() =>
                    {
                        var dev = Probe(ip);
                        if (dev != null) lock (result) result.Add(dev);
                    }));
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { Log.Debug("MchatVod", "scan tv err " + ex.Message); }
            return result;
        }

        static string LocalIp()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 53);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { return null; }
        }

        static TvDevice Probe(string ip)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var ar = client.BeginConnect(ip, TvPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(220)) { try { client.Close(); } catch { } return null; }
                    client.EndConnect(ar);
                }
                // 确认是本程序 TV 端（/api/info 返回含 port 字段的 JSON）
                var info = HttpGet("http://" + ip + ":" + TvPort + "/api/info", 1200);
                if (info != null && info.Contains("\"port\""))
                    return new TvDevice { Ip = ip, Name = L10n.T("电视 · ") + ip };
            }
            catch { }
            return null;
        }

        static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>投放整条线路：先 web_play 起播 startIndex，再 add 其余——TV 自动连播；全部用真实直链。</summary>
        public static bool CastLine(TvDevice tv, List<VodEpisode> eps, List<string> names, int startIndex)
        {
            if (tv == null || eps == null || eps.Count == 0) return false;
            if (startIndex < 0 || startIndex >= eps.Count) startIndex = 0;
            var first = RealCastUrl(eps[startIndex]);
            if (string.IsNullOrWhiteSpace(first)) return false;
            if (!Post(tv, "web_play", first, names[startIndex])) return false;
            for (int i = 0; i < eps.Count; i++)
            {
                if (i == startIndex) continue;
                var u = RealCastUrl(eps[i]);
                if (!string.IsNullOrWhiteSpace(u)) Post(tv, "add", u, names[i]);
            }
            return true;
        }

        /// <summary>投放单个真实直链（清空 TV 队列并起播）。</summary>
        public static bool CastOne(TvDevice tv, string realUrl, string name)
        {
            if (tv == null || string.IsNullOrWhiteSpace(realUrl)) return false;
            return Post(tv, "web_play", realUrl, name);
        }

        /// <summary>仅把真实直链加入 TV 队列（不切换当前播放）。</summary>
        public static bool Enqueue(TvDevice tv, string realUrl, string name)
        {
            if (tv == null || string.IsNullOrWhiteSpace(realUrl)) return false;
            return Post(tv, "add", realUrl, name);
        }

        static bool Post(TvDevice tv, string action, string url, string name)
        {
            try
            {
                var json = "{\"url\":" + JsonConvert.SerializeObject(url)
                         + ",\"name\":" + JsonConvert.SerializeObject(name ?? "") + "}";
                var req = (HttpWebRequest)WebRequest.Create("http://" + tv.Ip + ":" + TvPort + "/api/" + action);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                using (var os = req.GetRequestStream()) os.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception ex)
            {
                Log.Warn("MchatVod", "cast " + action + " -> " + tv.Ip + " 失败: " + ex.Message);
                return false;
            }
        }
    }
}

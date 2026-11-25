// ============================================================
// 局域网「一起播放」同步模块
// · 主播：播放时周期向子网广播信标（设备名/歌名/曲库path/位置/时钟/web端口/IP）。
// · 接收：常驻监听，发现其它设备播放即在通知栏弹「一起播放」。
// · 一起播放：从主播 web 端拉取音乐文件缓存本地 → 与主播做时钟偏移采样 →
//   取精确播放时间 → 定时对齐本地播放，使多设备尽量同时出声。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Newtonsoft.Json.Linq;
using AhoChatA.Music;

namespace AhoChatA
{
    public static class MusicSync
    {
        public const int Port = 45779;
        private const string ActionJoin = "com.Ahochat.android.musicsync.JOIN";
        private const string ChanId = "Mchat_sync";

        private static UdpClient _recv;
        private static Timer _beaconTimer;
        private static Timer _sweepTimer;
        private static bool _running;
        private static readonly object _lk = new object();
        private static readonly Dictionary<string, RemoteHost> _hosts =
            new Dictionary<string, RemoteHost>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _lastNotify =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public class RemoteHost
        {
            public string Id, Name, Title, Artist, Path, Ip;
            public int WebPort;
            public long LastSeen;
        }

        public static IList<RemoteHost> Hosts
        {
            get
            {
                lock (_lk)
                {
                    var l = new List<RemoteHost>();
                    foreach (var h in _hosts.Values) l.Add(h);
                    return l;
                }
            }
        }

        /* ---------------- 生命周期 ---------------- */

        public static void Start()
        {
            if (_running) return;
            _running = true;
            try
            {
                _recv = new UdpClient(Port);          // 绑定全部接口，既收广播也收同步回包
                Task.Run(() => RecvLoop());
                _beaconTimer = new Timer(_ => SendBeacon(), null, 0, 1000);
                _sweepTimer = new Timer(_ => Sweep(), null, 2000, 2000);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", "MusicSync 启动失败: " + ex.Message);
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _beaconTimer?.Dispose(); } catch { }
            try { _sweepTimer?.Dispose(); } catch { }
            try { _recv?.Close(); } catch { }
            _recv = null;
            lock (_lk) { _hosts.Clear(); _lastNotify.Clear(); }
        }

        /* ---------------- 主播：广播信标 ---------------- */

        private static void SendBeacon()
        {
            if (!MusicPlayer.IsPlaying || MusicPlayer.Current == null) return;
            try
            {
                var s = MusicPlayer.Current;
                var obj = new JObject
                {
                    ["v"] = 1,
                    ["k"] = "msync",
                    ["id"] = SelfId(),
                    ["name"] = SelfName(),
                    ["title"] = s.DisplayTitle,
                    ["artist"] = s.ArtistLine,
                    ["path"] = s.Path,
                    ["pos"] = MusicPlayer.Position,
                    ["clk"] = SystemClock.ElapsedRealtime(),
                    ["web"] = WebServer.ExternalPortActual,
                    ["ip"] = LanIp()
                };
                var data = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
                foreach (var b in BroadcastTargets())
                {
                    try
                    {
                        using (var c = new UdpClient())
                        {
                            c.EnableBroadcast = true;
                            c.Send(data, data.Length, b);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static IEnumerable<IPEndPoint> BroadcastTargets()
        {
            var list = new List<IPEndPoint>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        if (ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        if (ip.Length != 4 || mask.Length != 4) continue;
                        var bcast = new byte[4];
                        for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | (~mask[i]));
                        list.Add(new IPEndPoint(new IPAddress(bcast), Port));
                    }
                }
            }
            catch { }
            if (list.Count == 0) list.Add(new IPEndPoint(IPAddress.Broadcast, Port));
            return list;
        }

        /* ---------------- 接收循环 ---------------- */

        private static void RecvLoop()
        {
            while (_running && _recv != null)
            {
                try
                {
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    var res = _recv.Receive(ref ep);
                    var txt = Encoding.UTF8.GetString(res);
                    var obj = JObject.Parse(txt);
                    var k = obj["k"]?.ToString();
                    if (k == "rs")   // 对端的同步时钟请求 → 回 ra
                    {
                        var rep = new JObject
                        {
                            ["k"] = "ra",
                            ["clk"] = SystemClock.ElapsedRealtime(),
                            ["pos"] = MusicPlayer.Position
                        };
                        var rdata = Encoding.UTF8.GetBytes(rep.ToString(Newtonsoft.Json.Formatting.None));
                        try { _recv.Send(rdata, rdata.Length, ep); } catch { }
                        continue;
                    }
                    if (k == "msync")
                    {
                        var id = obj["id"]?.ToString();
                        if (id == SelfId()) continue;   // 忽略自己
                        var host = new RemoteHost
                        {
                            Id = id,
                            Name = obj["name"]?.ToString(),
                            Title = obj["title"]?.ToString(),
                            Artist = obj["artist"]?.ToString(),
                            Path = obj["path"]?.ToString(),
                            WebPort = obj["web"]?.ToObject<int>() ?? 7790,
                            Ip = obj["ip"]?.ToString() ?? ep.Address.ToString(),
                            LastSeen = SystemClock.ElapsedRealtime()
                        };
                        bool isNew;
                        lock (_lk)
                        {
                            isNew = !_hosts.ContainsKey(id);
                            _hosts[id] = host;
                        }
                        ShowJoinNotification(host, isNew);
                    }
                }
                catch
                {
                    if (!_running) break;
                }
            }
        }

        private static void Sweep()
        {
            if (!_running) return;
            var now = SystemClock.ElapsedRealtime();
            List<string> dead = null;
            lock (_lk)
            {
                foreach (var kv in _hosts)
                    if (now - kv.Value.LastSeen > 4000)
                        (dead ?? (dead = new List<string>())).Add(kv.Key);
                if (dead != null)
                    foreach (var d in dead)
                    {
                        _hosts.Remove(d);
                        _lastNotify.Remove(d);
                        CancelNotification(d);
                    }
            }
        }

        /* ---------------- 接收端通知栏 ---------------- */

        private static void ShowJoinNotification(RemoteHost h, bool isNew)
        {
            try
            {
                var now = SystemClock.ElapsedRealtime();
                lock (_lk)
                {
                    if (_lastNotify.TryGetValue(h.Id, out var t) && !isNew && now - t < 2000) return;
                    _lastNotify[h.Id] = now;
                }
                var ctx = Application.Context;
                var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    if (nm.GetNotificationChannel(ChanId) == null)
                    {
                        var ch = new NotificationChannel(ChanId, L10n.T("一起播放"), NotificationImportance.Default);
                        nm.CreateNotificationChannel(ch);
                    }
                }
                int nid = Math.Abs(h.Id.GetHashCode());
                var joinIntent = new Intent(ActionJoin)
                    .SetPackage(ctx.PackageName)
                    .PutExtra("ip", h.Ip).PutExtra("web", h.WebPort)
                    .PutExtra("path", h.Path).PutExtra("title", h.Title)
                    .PutExtra("artist", h.Artist).PutExtra("name", h.Name);
                var joinPi = PendingIntent.GetBroadcast(ctx, nid ^ 0x55, joinIntent,
                    Build.VERSION.SdkInt >= BuildVersionCodes.S
                        ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                        : PendingIntentFlags.UpdateCurrent);
                var open = PendingIntent.GetActivity(ctx, nid,
                    new Intent(ctx, typeof(ChatActivity)).SetFlags(ActivityFlags.SingleTop),
                    PendingIntentFlags.UpdateCurrent);
                var b = Build.VERSION.SdkInt >= BuildVersionCodes.O
                    ? new Notification.Builder(ctx, ChanId)
                    : new Notification.Builder(ctx);
                b.SetContentTitle(L10n.T("一起播放 · ") + (h.Name ?? L10n.T("设备")))
                 .SetContentText((h.Title ?? "") + "  -  " + (h.Artist ?? ""))
                 .SetSmallIcon(Resource.Drawable.icon)
                 .SetContentIntent(open)
                 .SetOngoing(true)
                 .SetAutoCancel(false)
                 .AddAction(Resource.Drawable.icon, "一起播放", joinPi);
                nm.Notify(nid, b.Build());
            }
            catch { }
        }

        private static void CancelNotification(string id)
        {
            try
            {
                var ctx = Application.Context;
                var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService);
                nm.Cancel(Math.Abs(id.GetHashCode()));
            }
            catch { }
        }

        /* ---------------- 一起播放：拉取 + 对齐 ---------------- */

        public static async void Join(string ip, int web, string path, string title, string artist, string name)
        {
            try
            {
                Toast(L10n.T("正在从 ") + (name ?? ip) + L10n.T(" 同步音乐…"));
                var local = await DownloadAsync(ip, web, path);
                if (local == null) { Toast(L10n.T("同步失败：无法获取音乐文件")); return; }

                // 时钟偏移采样（多次取加权均值）
                double offset = 0, rtt = 0;
                bool ok = false;
                for (int i = 0; i < 6; i++)
                {
                    var r = SyncSample(ip);
                    if (r.has)
                    {
                        ok = true;
                        offset = offset * 0.6 + r.offset * 0.4;
                        rtt = rtt * 0.6 + r.rtt * 0.4;
                    }
                    await Task.Delay(30);
                }
                if (!ok) { Toast(L10n.T("同步失败：无法与主播对齐时钟")); return; }

                // 用最后一次采样作为基准位置
                var last = SyncSample(ip);
                long hostPos = last.has ? last.pos : 0;
                long hostClk = last.has ? last.clk : SystemClock.ElapsedRealtime();

                long now = SystemClock.ElapsedRealtime();
                // 给下载/解码/调度留缓冲（至少 1.5s，并叠加实测 RTT）
                long startLocal = now + (long)Math.Max(1500, rtt + 800);
                int seekMs = (int)(hostPos + (startLocal - offset - hostClk));
                if (seekMs < 0) seekMs = 0;

                MusicPlayer.PlayExternal(local, title ?? L10n.T("一起播放"), artist ?? "", startLocal, seekMs);
                Toast(L10n.T("已加入一起播放（时钟偏移约 ") + Math.Abs((int)offset) + " ms）");
            }
            catch (Exception ex)
            {
                Toast(L10n.T("一起播放失败：") + ex.Message);
            }
        }

        private static (bool has, double offset, double rtt, long clk, int pos) SyncSample(string ip)
        {
            try
            {
                using (var c = new UdpClient())
                {
                    c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 800);
                    c.EnableBroadcast = true;
                    var ep = new IPEndPoint(IPAddress.Parse(ip), Port);
                    var req = Encoding.UTF8.GetBytes("{\"k\":\"rs\"}");
                    long t1 = SystemClock.ElapsedRealtime();
                    c.Send(req, req.Length, ep);
                    var res = c.Receive(ref ep);
                    long t4 = SystemClock.ElapsedRealtime();
                    var obj = JObject.Parse(Encoding.UTF8.GetString(res));
                    long clk = obj["clk"]?.ToObject<long>() ?? 0;
                    int pos = obj["pos"]?.ToObject<int>() ?? 0;
                    double offset = clk - (t1 + t4) / 2.0;   // 本机时间 ≈ 主播时间 + offset
                    double rtt = t4 - t1;
                    return (true, offset, rtt, clk, pos);
                }
            }
            catch
            {
                return (false, 0, 0, 0, 0);
            }
        }

        private static async Task<string> DownloadAsync(string ip, int web, string path)
        {
            try
            {
                var url = "http://" + ip + ":" + web + "/api/media/stream?path=" + Uri.EscapeDataString(path);
                using (var cli = new System.Net.WebClient())
                {
                    var bytes = await cli.DownloadDataTaskAsync(url);
                    if (bytes == null || bytes.Length == 0) return null;
                    var dir = Path.Combine(Application.Context.FilesDir.AbsolutePath, "music_sync");
                    Directory.CreateDirectory(dir);
                    var ext = Path.GetExtension(path);
                    if (string.IsNullOrEmpty(ext)) ext = ".mp3";
                    var file = Path.Combine(dir, Math.Abs(path.GetHashCode()) + ext);
                    File.WriteAllBytes(file, bytes);
                    return file;
                }
            }
            catch
            {
                return null;
            }
        }

        /* ---------------- 工具 ---------------- */

        private static string SelfId()
        {
            try
            {
                var p = Application.Context.GetSharedPreferences("mchat", FileCreationMode.Private);
                var id = p.GetString("musicsync_id", "");
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString();
                    p.Edit().PutString("musicsync_id", id).Commit();
                }
                return id;
            }
            catch { return "self"; }
        }

        private static string SelfName()
        {
            try
            {
                var p = Application.Context.GetSharedPreferences("mchat", FileCreationMode.Private);
                var n = p.GetString("musicsync_name", "");
                if (string.IsNullOrEmpty(n))
                {
                    n = "Mchat-" + SelfId().Substring(0, 4);
                    p.Edit().PutString("musicsync_name", n).Commit();
                }
                return n;
            }
            catch { return "Mchat"; }
        }

        private static string LanIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private static void Toast(string s)
        {
            try
            {
                var ctx = Application.Context;
                var h = new Handler(Looper.MainLooper);
                h.Post(() =>
                {
                    try { L10n.Toast(ctx, s, Android.Widget.ToastLength.Short).Show(); }
                    catch { }
                });
            }
            catch { }
        }
    }
}

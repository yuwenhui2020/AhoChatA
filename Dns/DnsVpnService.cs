using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Uri = System.Uri;
using TransportType = Android.Net.TransportType;

namespace AhoChatA.DnsCore
{
    [Service(Name = "AhoChatA.Dns.DnsVpnService", Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
    [IntentFilter(new[] { "android.net.VpnService" })]
    public class DnsVpnService : VpnService
    {
        private const string VirtualDns = "10.77.0.2";
        private readonly object _lifecycleLock = new object();
        private CancellationTokenSource _cts;
        private Task _loop;
        private volatile ParcelFileDescriptor _tun;
        private DnsServer _server;
        private volatile bool _running;
        public static DnsVpnService Instance { get; private set; }
        public bool IsRunning => _running;
        public event Action<string> OnLog;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            DnsManager.Instance.Initialize(this);
            const string channelId = "mchat_dns";
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var manager = (NotificationManager)GetSystemService(NotificationService);
                manager.CreateNotificationChannel(new NotificationChannel(channelId, L10n.T("DNS 代理"), NotificationImportance.Low));
            }
            var notification = new AndroidX.Core.App.NotificationCompat.Builder(this, channelId)
                .SetContentTitle("Mchat DNS")
                .SetContentText(L10n.T("DNS VPN 服务运行中"))
                .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
                .SetOngoing(true).Build();
            StartForeground(2009, notification);
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == "STOP_VPN")
            {
                StopVpn();
                StopSelf();
                return StartCommandResult.NotSticky;
            }
            StartVpn();
            return StartCommandResult.Sticky;
        }

        public void StartVpn()
        {
            lock (_lifecycleLock)
            {
                if (_loop != null && !_loop.IsCompleted) return;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Factory.StartNew(() => Run(token), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        private void Run(CancellationToken token)
        {
            try
            {
                // Only the synthetic DNS address is routed into TUN; other network traffic stays on its normal route.
                var builder = new Builder(this);
                builder.SetSession("Mchat DNS");
                builder.SetMtu(DnsVpnPacket.Mtu);
                builder.AddAddress("10.77.0.1", 32);
                builder.AddDnsServer(VirtualDns);
                builder.AddRoute(VirtualDns, 32);
                // Include this app's WebView DNS. Only explicit upstream sockets/requests bypass the VPN.
                builder.SetBlocking(true);
                var tun = builder.Establish();
                if (tun == null) throw new InvalidOperationException("VPN 未授权或接口创建失败");
                _tun = tun;
                token.ThrowIfCancellationRequested();
                _server = new DnsServer(this, DnsManager.Instance.GetConfig(), Log, new VpnUpstreamNetwork(this));
                DnsManager.Instance.SetDnsServer(_server);
                var exchange = new DnsTunLoopback(_server.ListeningPort, token);
                var clock = Stopwatch.StartNew();
                int ticking = 0;
                using (var input = new Java.IO.FileInputStream(tun.FileDescriptor))
                using (var output = new Java.IO.FileOutputStream(tun.FileDescriptor))
                using (var handler = new DnsTcpTun(exchange.TcpQuery, exchange.UdpQuery, reply =>
                {
                    if (token.IsCancellationRequested) return;
                    try { output.Write(reply, 0, reply.Length); }
                    catch (Exception)
                    {
                        // Unblock the reader so the run can clean up after a failed TUN write.
                        try { tun.Close(); } catch { }
                    }
                }, () => clock.ElapsedMilliseconds))
                using (var timer = new System.Threading.Timer(_ =>
                {
                    if (Interlocked.Exchange(ref ticking, 1) != 0) return;
                    try { handler.Tick(); }
                    finally { Volatile.Write(ref ticking, 0); }
                }, null, 200, 200))
                {
                    _running = !token.IsCancellationRequested;
                    var packet = new byte[65535];
                    while (!token.IsCancellationRequested)
                    {
                        int length = input.Read(packet, 0, packet.Length);
                        if (length <= 0) break;
                        handler.Accept(packet, length);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) Log(L10n.T("VPN 失败: ") + ex.Message);
            }
            finally
            {
                _running = false;
                DnsManager.Instance.SetDnsServer(null);
                try { _server?.Dispose(); } catch { }
                _server = null;
                try { _tun?.Close(); } catch { }
                _tun = null;
                StopSelf();
            }
        }

        public void StopVpn()
        {
            lock (_lifecycleLock)
            {
                _running = false;
                _cts?.Cancel();
                try { _tun?.Close(); } catch { }
            }
        }

        public override void OnRevoke()
        {
            StopVpn();
            StopSelf();
            base.OnRevoke();
        }

        public override void OnDestroy()
        {
            StopVpn();
            Instance = null;
            base.OnDestroy();
        }

        private void Log(string message)
        {
            Android.Util.Log.Info("DnsVpnService", message);
            OnLog?.Invoke(message);
        }

        // Never bind the process: doing so would also bypass the WebView's DNS filtering.
        // Java's Network APIs bind both HTTP connections and their bootstrap DNS to a
        // particular physical network; Protect is required separately for managed sockets.
        private sealed class VpnUpstreamNetwork : IDnsUpstreamNetwork
        {
            private readonly DnsVpnService _vpn;
            private readonly ConnectivityManager _connectivity;

            internal VpnUpstreamNetwork(DnsVpnService vpn)
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
                    throw new NotSupportedException("DNS VPN requires Android 5.0 or newer");
                _vpn = vpn;
                _connectivity = (ConnectivityManager)vpn.GetSystemService(ConnectivityService);
                if (_connectivity == null) throw new IOException("Network service unavailable");
            }

            public void ProtectSocket(Socket socket)
            {
                // Protect the native fd before connect/send. A failed protection is fatal
                // to this attempt; there is deliberately no unprotected retry.
                if (!_vpn.Protect(socket.Handle.ToInt32()))
                    throw new IOException("Unable to protect upstream socket from DNS VPN");
            }

            private Network GetUnderlyingNetwork()
            {
                bool Usable(Network network)
                {
                    if (network == null) return false;
                    using (var caps = _connectivity.GetNetworkCapabilities(network))
                        return caps != null && caps.HasCapability(NetCapability.Internet) &&
                            caps.HasCapability(NetCapability.NotVpn) && !caps.HasTransport(TransportType.Vpn);
                }
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    var active = _connectivity.ActiveNetwork;
                    if (Usable(active)) return active;
                }
                Network best = null;
                int bestScore = -1;
                foreach (var network in _connectivity.GetAllNetworks() ?? Array.Empty<Network>())
                {
                    if (!Usable(network)) continue;
                    using (var caps = _connectivity.GetNetworkCapabilities(network))
                    {
                        if (caps == null) continue;
                        // While the VPN is active it is often ActiveNetwork. Prefer a
                        // validated physical link, then an unmetered one, rather than
                        // accidentally selecting a captive/stale cellular link first.
                        int score = (Build.VERSION.SdkInt >= BuildVersionCodes.M && caps.HasCapability(NetCapability.Validated) ? 2 : 0) +
                            (caps.HasCapability(NetCapability.NotMetered) ? 1 : 0);
                        if (score > bestScore) { best = network; bestScore = score; }
                    }
                }
                return best ?? throw new IOException("No non-VPN Internet network available for upstream DNS");
            }

            public Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var network = GetUnderlyingNetwork();
                // This resolver belongs to the selected non-VPN network, not the app's
                // default VPN resolver. DnsServer bounds waiting by its query deadline.
                return Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return network.GetAllByName(host).Select(address => new IPAddress(address.GetAddress())).ToArray();
                }, token);
            }

            public Task<byte[]> SendHttpAsync(Uri uri, byte[] body, string mediaType, int maxBytes, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var network = GetUnderlyingNetwork();
                return Task.Run(() => SendHttp(network, uri, body, mediaType, maxBytes, token), token);
            }

            private static byte[] SendHttp(Network network, Uri uri, byte[] body, string mediaType, int maxBytes, CancellationToken token)
            {
                for (int redirect = 0; ; redirect++)
                {
                    token.ThrowIfCancellationRequested();
                    using (var url = new Java.Net.URL(uri.AbsoluteUri))
                    using (var connection = network.OpenConnection(url) as Java.Net.HttpURLConnection)
                    {
                        if (connection == null) throw new IOException("Unsupported upstream HTTP connection");
                        connection.ConnectTimeout = 30000;
                        connection.ReadTimeout = 30000;
                        connection.InstanceFollowRedirects = false;
                        connection.UseCaches = false;
                        connection.RequestMethod = body == null ? "GET" : "POST";
                        using (token.Register(() => { try { connection.Disconnect(); } catch { } }))
                        try
                        {
                            if (body != null)
                            {
                                connection.DoOutput = true;
                                connection.SetRequestProperty("Accept", mediaType);
                                connection.SetRequestProperty("Content-Type", mediaType);
                                connection.SetFixedLengthStreamingMode(body.Length);
                                using (var output = connection.OutputStream) output.Write(body, 0, body.Length);
                            }
                            int status = (int)connection.ResponseCode;
                            if (body == null && (status == 301 || status == 302 || status == 303 || status == 307 || status == 308))
                            {
                                string location = connection.GetHeaderField("Location");
                                if (redirect >= 5 || string.IsNullOrEmpty(location)) throw new IOException("Invalid filter-list redirect");
                                var next = new Uri(uri, location);
                                if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) ||
                                    (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
                                    throw new IOException("Unsafe filter-list redirect");
                                uri = next;
                                continue; // Retain the explicit network for the entire redirect chain.
                            }
                            if (status < 200 || status >= 300) throw new IOException("Upstream HTTP status " + status);
                            if (mediaType != null && !string.Equals(connection.ContentType?.Split(';')[0].Trim(), mediaType, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Unexpected DoH response content type");
                            // HTTPS uses Android's default certificate/hostname validation.
                            // DoH redirects are rejected, never downgraded to another endpoint.
                            using (var input = connection.InputStream)
                            using (var result = new MemoryStream())
                            {
                                var buffer = new byte[8192];
                                int count;
                                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (result.Length + count > maxBytes) throw new InvalidDataException("Upstream HTTP response too large");
                                    result.Write(buffer, 0, count);
                                }
                                token.ThrowIfCancellationRequested();
                                return result.ToArray();
                            }
                        }
                        finally { connection.Disconnect(); }
                    }
                }
            }
        }
    }
}

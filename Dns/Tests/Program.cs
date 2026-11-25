using System.Net;
using System.Net.Sockets;
using System.Reflection;
using A = AhoChatA.DnsCore;
using W = MchatW.Dns;

internal static class Program
{
    static int checks;
    static void Assert(bool value, string description) { if (!value) throw new Exception(description); Interlocked.Increment(ref checks); }
    static byte[] Query(ushort id = 0x1234, string name = "example.com") => new A.DnsMessage { Header = new A.DnsMessageHeader { Id = id, RecursionDesired = true, Z = 1 }, Questions = new() { new A.DnsQuestion { Name = name, Type = A.DnsRecordType.A } } }.ToBytes();
    static object Call(object server, string method, params object[] args) => server.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(server, args);
    static async Task<byte[]> Resolve(object server, byte[] query) => await (Task<byte[]>)Call(server, "ProcessQuery", server is A.DnsServer ? A.DnsMessage.FromBytes(query) : W.DnsMessage.FromBytes(query), "127.0.0.1");
    static A.DnsServer CreateAndroidCore(A.DnsServerConfig config, A.IDnsUpstreamNetwork network = null)
    {
        // Windows can assign UDP port 0 from a range reserved for TCP. Retry the
        // harness allocation; production uses its configured fixed listener port.
        for (int attempt = 0; ; attempt++)
        {
            try { return new A.DnsServer(null, config, _ => {}, network); }
            catch (SocketException ex) when (config.Port == 0 && attempt < 20 &&
                (ex.SocketErrorCode == SocketError.AccessDenied || ex.SocketErrorCode == SocketError.AddressAlreadyInUse)) { }
        }
    }
    static async Task Main()
    {
        var q = Query();
        Assert(Convert.ToHexString(q) == "123401100001000000000000076578616D706C6503636F6D0000010001", "network-order query and CD flags");
        Assert(Convert.ToHexString(W.DnsMessage.FromBytes(q).ToBytes()) == Convert.ToHexString(q), "Windows parsed packet preservation");
        foreach (bool android in new[] { true, false })
        {
            var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int upstreamPort = ((IPEndPoint)listener.Client.LocalEndPoint).Port;
            var configA = new A.DnsServerConfig { Port = 0, UpstreamTimeoutMs = 200, UpstreamServers = new() { $"127.0.0.1:{upstreamPort}" } };
            var configW = new W.DnsServerConfig { Port = 0, UpstreamTimeoutMs = 200, UpstreamServers = new() { $"127.0.0.1:{upstreamPort}" } };
            using IDisposable server = android ? CreateAndroidCore(configA) : new W.DnsServer(configW, _ => {});
            object core = server;
            Assert(((IPEndPoint)((UdpClient)core.GetType().GetField("_udpServer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(core)).Client.LocalEndPoint).Address.Equals(IPAddress.Loopback), "loopback binding");
            Assert(A.DnsMessage.FromBytes(await Resolve(core, Query(name: "doubleclick.net"))).Answers.Count == 1, "bundled filter blocks exact domain");
            Assert(A.DnsMessage.FromBytes(await Resolve(core, Query(name: "x.doubleclick.net"))).Answers.Count == 1, "bundled filter blocks child");

            using var embedded = core.GetType().Assembly.GetManifestResourceStream("Mchat.Dns.AdGuardDnsFilter.txt");
            Assert(embedded != null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(embedded)) ==
                "12337AB8A7036634D3723479FD7FADE2DD245734DACFD1D66CB4F0589EF1F04C", "byte-identical offline resource");
            var rules = core.GetType().GetField("_bundledRules", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(core);
            Assert(rules != null, "embedded rules loaded before the first query");
            var ruleType = rules.GetType();
            const BindingFlags instanceFields = BindingFlags.NonPublic | BindingFlags.Instance;
            int blockCount = (int)ruleType.GetProperty("BlockCount", instanceFields).GetValue(rules);
            int allowCount = (int)ruleType.GetProperty("AllowCount", instanceFields).GetValue(rules);
            int skippedCount = (int)ruleType.GetField("Skipped", instanceFields).GetValue(rules);
            int disabledCount = (int)ruleType.GetField("Disabled", instanceFields).GetValue(rules);
            Console.WriteLine($"{core.GetType().Namespace} installed: {blockCount} blocks, {allowCount} exceptions, {disabledCount} disabled, {skippedCount} unsupported");
            Assert(blockCount == 190946 && allowCount == 204 && disabledCount == 1 && skippedCount == 750,
                "installed rule counts match the pinned snapshot");

            bool Blocked(string name) => (bool)Call(core, "IsBlocked", name);
            void Toggles(bool ads, bool trackers, bool malware)
            {
                if (server is A.DnsServer a) a.UpdateConfig(c => { c.BlockAds = ads; c.BlockTrackers = trackers; c.BlockMalware = malware; });
                else ((W.DnsServer)server).UpdateConfig(c => { c.BlockAds = ads; c.BlockTrackers = trackers; c.BlockMalware = malware; });
            }
            Assert(Blocked("PAGEAD2.GOOGLESYNDICATION.COM") && Blocked("x.google-analytics.com") && Blocked("stats.paypal.com"), "representative upstream rules with case and suffix matching");
            Assert(!Blocked("paypal.com") && !Blocked("www.paypal.com") && !Blocked("cloudfront.net") && !Blocked("d111111abcdef8.cloudfront.net"), "benign hosts do not inherit unrelated blocks");
            Assert(Blocked("dvwztnx4ab3fl.cloudfront.net"), "targeted upstream CDN host still blocks");
            Assert(!Blocked("pagead.l.doubleclick.net") && !Blocked("child.pagead.l.doubleclick.net"), "upstream suffix exception beats ordinary block");
            Assert(!Blocked("www3.doubleclick.net") && Blocked("child.www3.doubleclick.net"), "upstream exact exception does not whitelist descendants");
            Assert(Blocked("pixel.wp.pl") && !Blocked("linksprf.com"), "upstream important block and badfilter handled");
            core.GetType().GetMethod("AddBlockedDomain").Invoke(core, new object[] { "pagead.l.doubleclick.net" });
            Assert(Blocked("pagead.l.doubleclick.net"), "explicit local block is independent of upstream exception");
            core.GetType().GetMethod("RemoveBlockedDomain").Invoke(core, new object[] { "pagead.l.doubleclick.net" });
            Toggles(false, false, true);
            Assert(!Blocked("doubleclick.net") && !Blocked("google-analytics.com"), "both switches off; malware alone does not enable feed");
            Toggles(true, false, false);
            Assert(Blocked("doubleclick.net") && Blocked("google-analytics.com"), "ads switch enables combined feed");
            Toggles(false, true, false);
            Assert(Blocked("doubleclick.net") && Blocked("google-analytics.com"), "tracking switch enables combined feed");
            Toggles(true, true, true);
            byte[] responsePacket = null;
            var respond = Task.Run(async () =>
            {
                var incoming = await listener.ReceiveAsync();
                Assert(A.DnsMessage.FromBytes(incoming.Buffer).Questions[0].Name == "example.com", "forwarded question");
                var original = incoming.Buffer;
                var bytes = original.ToList();
                bytes[2] = 0x81; bytes[3] = 0x90; bytes[6] = 0; bytes[7] = 2;
                // CNAME owner points to question; target is alias.example.com. Second RR RDATA points to first RDATA.
                bytes.AddRange(new byte[] { 0xc0,12,0,5,0,1,0,0,0,60,0,8,5,(byte)'a',(byte)'l',(byte)'i',(byte)'a',(byte)'s',0xc0,12 });
                int targetOffset = original.Length + 12;
                bytes.AddRange(new byte[] { 0xc0,12,0,5,0,1,0,0,0,40,0,2,(byte)(0xc0 | targetOffset >> 8),(byte)targetOffset });
                responsePacket = bytes.ToArray();
                await listener.SendAsync(responsePacket, incoming.RemoteEndPoint);
            });
            var first = await Resolve(core, q);
            await respond;
            responsePacket[0] = 0x12; responsePacket[1] = 0x34;
            Assert(first.SequenceEqual(responsePacket), "compressed RDATA stays at original offsets");
            var jobs = Enumerable.Range(1, 40).Select(async n => { var bytes = await Resolve(core, Query((ushort)n)); Assert(bytes[0] == 0 && bytes[1] == n, "concurrent cached transaction ID isolation"); return bytes; });
            await Task.WhenAll(jobs);
            await Task.Delay(1100);
            var aged = A.DnsMessage.FromBytes(await Resolve(core, q));
            Assert(aged.Answers[0].TTL <= 59 && aged.Answers[1].TTL <= 39, "cache ages TTL fields");
            var pending = new[] { "notdoubleclick.net", "paypal.com", "cloudfront.net" }.Select(async name =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = A.DnsMessage.FromBytes(await Resolve(core, Query(name: name)));
                Assert(result.Header.ResponseCode == A.DnsResponseCode.ServerFailure, "safe domain forwarded and timeout SERVFAIL");
                Assert(watch.ElapsedMilliseconds < 2000, "effective async upstream deadline");
            });
            await Task.WhenAll(pending);
            core.GetType().GetMethod("AddBlockedDomain").Invoke(core, new object[] { "blocked.test" });
            Assert(A.DnsMessage.FromBytes(await Resolve(core, Query(name: "blocked.test"))).Answers.Count == 1, "runtime block override");
            core.GetType().GetMethod("RemoveBlockedDomain").Invoke(core, new object[] { "blocked.test" });
            listener.Dispose();
            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            int tcpPort = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            var fallbackUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, tcpPort));
            if (android) { configA.EnableCache = false; configA.UpstreamTimeoutMs = 2000; configA.UpstreamServers = new() { $"127.0.0.1:{tcpPort}" }; }
            else { configW.EnableCache = false; configW.UpstreamTimeoutMs = 2000; configW.UpstreamServers = new() { $"127.0.0.1:{tcpPort}" }; }
            var truncatedReply = Task.Run(async () =>
            {
                var packet = await fallbackUdp.ReceiveAsync();
                var wrong = (byte[])packet.Buffer.Clone(); wrong[0] ^= 0xff; wrong[2] |= 0x80;
                await fallbackUdp.SendAsync(wrong, packet.RemoteEndPoint);
                var truncated = packet.Buffer; truncated[2] |= 0x82;
                await fallbackUdp.SendAsync(truncated, packet.RemoteEndPoint);
            });
            var tcpReply = Task.Run(async () =>
            {
                using var socket = await tcpListener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix);
                var data = new byte[(prefix[0] << 8) | prefix[1]]; await stream.ReadExactlyAsync(data);
                data[2] |= 0x80;
                await stream.WriteAsync(prefix.AsMemory(0, 1));
                await Task.Delay(25);
                await stream.WriteAsync(prefix.AsMemory(1, 1));
                foreach (byte b in data) await stream.WriteAsync(new[] { b });
            });
            Assert((await Resolve(core, Query(name: "tcp-fallback.test")))[2] == 0x81, "mismatched ID ignored, truncated UDP retried via TCP, fragmented prefix read");
            await Task.WhenAll(truncatedReply, tcpReply);
            tcpListener.Stop(); fallbackUdp.Dispose();
            var localUdp = (UdpClient)core.GetType().GetField("_udpServer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(core);
            using var downstream = new TcpClient();
            await downstream.ConnectAsync((IPEndPoint)localUdp.Client.LocalEndPoint);
            var localStream = downstream.GetStream();
            var localQuery = Query(name: "doubleclick.net");
            for (int count = 0; count < 2; count++)
            {
                await localStream.WriteAsync(new byte[] { 0 });
                await Task.Delay(25);
                await localStream.WriteAsync(new byte[] { (byte)localQuery.Length });
                await localStream.WriteAsync(localQuery);
                var prefix = new byte[2]; await localStream.ReadExactlyAsync(prefix);
                var localResponse = new byte[(prefix[0] << 8) | prefix[1]]; await localStream.ReadExactlyAsync(localResponse);
                Assert(A.DnsMessage.FromBytes(localResponse).Answers.Count == 1, "fragmented downstream prefix and persistent TCP connection");
            }
        }
        foreach (byte[] bad in new[] { new byte[0], new byte[11], Convert.FromHexString("123401000001000000000000C00C00010001"), Convert.FromHexString("123401000001000000000000C0FF00010001"), Convert.FromHexString("1234010000010000000000004000010001") })
        {
            bool failedA = false, failedW = false;
            try { A.DnsMessage.FromBytes(bad); } catch (Exception e) when (e is IOException || e is InvalidDataException) { failedA = true; }
            try { W.DnsMessage.FromBytes(bad); } catch (Exception e) when (e is IOException || e is InvalidDataException) { failedW = true; }
            Assert(failedA && failedW, "malformed DNS rejected on both platforms");
        }
        foreach (Type serverType in new[] { typeof(A.DnsServer), typeof(W.DnsServer) })
        {
        var tlsListener = new TcpListener(IPAddress.Loopback, 0);
        tlsListener.Start();
        int tlsPort = ((IPEndPoint)tlsListener.LocalEndpoint).Port;
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var certificateRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(san.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var importedCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
        var tlsServer = Task.Run(async () =>
        {
            using var accepted = await tlsListener.AcceptTcpClientAsync();
            using var ssl = new System.Net.Security.SslStream(accepted.GetStream());
            try { await ssl.AuthenticateAsServerAsync(importedCertificate); } catch (Exception ex) { Console.WriteLine($"Local TLS server: {ex.GetType().Name}: {ex.Message}"); }
        });
        using var androidCore = serverType == typeof(A.DnsServer) ? CreateAndroidCore(new A.DnsServerConfig { Port = 0 }) : null;
        using var tlsDeadline = new CancellationTokenSource(5000);
        bool certificateRejected = false;
        try
        {
            await (Task<byte[]>)serverType.GetMethod("ExchangeTcp", BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic).Invoke(androidCore, new object[] { "localhost", tlsPort, true, Query(), tlsDeadline.Token });
        }
        catch (System.Security.Authentication.AuthenticationException) { certificateRejected = true; }
        Assert(certificateRejected, "Android DoT rejects untrusted certificate even with matching hostname");
        await tlsServer;
        tlsListener.Stop();
        }
        await CheckVpnUpstreamIsolation();
        Console.WriteLine($"PASS: {checks} focused DNS checks against both source files.");
    }

    sealed class TestUpstreamNetwork : A.IDnsUpstreamNetwork
    {
        public int Protections, Lookups, HttpCalls;
        public bool FailProtect, FailLookup, FailHttp, StallLookup, StallHttp;
        public Func<Uri, byte[], string, int, byte[]> OnHttp;
        public void ProtectSocket(Socket socket)
        {
            Interlocked.Increment(ref Protections);
            Assert(!socket.Connected, "upstream socket protected before connect");
            if (FailProtect) throw new IOException("protection denied");
        }
        public Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken token)
        {
            Interlocked.Increment(ref Lookups);
            Assert(host == "bootstrap.invalid", "configured hostname resolved only by injected network");
            if (FailLookup) throw new IOException("no non-VPN network");
            if (StallLookup) return new TaskCompletionSource<IPAddress[]>().Task;
            return Task.FromResult(new[] { IPAddress.Loopback });
        }
        public Task<byte[]> SendHttpAsync(Uri uri, byte[] body, string mediaType, int maxBytes, CancellationToken token)
        {
            Interlocked.Increment(ref HttpCalls);
            if (FailHttp) throw new IOException("non-VPN HTTP unavailable");
            if (StallHttp) return new TaskCompletionSource<byte[]>().Task;
            return Task.FromResult(OnHttp(uri, body, mediaType, maxBytes));
        }
    }

    static async Task CheckVpnUpstreamIsolation()
    {
        var network = new TestUpstreamNetwork();
        var config = new A.DnsServerConfig { Port = 0, EnableCache = false, UpstreamTimeoutMs = 500, UpstreamServers = new() };
        using var server = CreateAndroidCore(config, network);
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        try
        {
            config.UpstreamServers = new() { $"127.0.0.1:{port}" };
            var udpReply = Task.Run(async () =>
            {
                var packet = await udp.ReceiveAsync();
                packet.Buffer[2] |= 0x82; // Force the protected UDP -> TCP retry path.
                await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint);
            });
            async Task ReplyTcp()
            {
                using var accepted = await tcp.AcceptTcpClientAsync();
                using var stream = accepted.GetStream();
                var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix);
                var data = new byte[(prefix[0] << 8) | prefix[1]]; await stream.ReadExactlyAsync(data);
                data[2] |= 0x80;
                await stream.WriteAsync(prefix); await stream.WriteAsync(data);
            }
            var tcpReply = ReplyTcp();
            var response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "protected-udp.test")));
            await Task.WhenAll(udpReply, tcpReply).WaitAsync(TimeSpan.FromSeconds(2));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.NoError && network.Protections == 2 && network.Lookups == 0,
                "UDP and truncated-reply TCP fallback both protected without default DNS");

            config.UpstreamServers = new() { $"tcp://bootstrap.invalid:{port}" };
            tcpReply = ReplyTcp();
            response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "protected-tcp.test")));
            await tcpReply.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.NoError && network.Lookups == 1 && network.Protections == 3,
                "TCP hostname bootstrap uses explicit resolver and protected numeric connect");

            network.FailProtect = true;
            foreach (string endpoint in new[] { $"127.0.0.1:{port}", $"tcp://127.0.0.1:{port}" })
            {
                config.UpstreamServers = new() { endpoint };
                response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "denied-protection.test")));
                Assert(response.Header.ResponseCode == A.DnsResponseCode.ServerFailure && udp.Available == 0 && !tcp.Pending(),
                    "failed protection emits neither UDP nor TCP traffic");
            }
            network.FailProtect = false;
            network.FailLookup = true;
            config.UpstreamServers = new() { $"tls://bootstrap.invalid:{port}" };
            int protectedBefore = network.Protections;
            response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "denied-bootstrap.test")));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.ServerFailure && network.Protections == protectedBefore && !tcp.Pending(),
                "DoT bootstrap failure never falls back to default DNS/connect");
            network.FailLookup = false;

            config.UpstreamServers = new() { "https://bootstrap.invalid/dns-query" };
            network.OnHttp = (uri, body, mediaType, limit) =>
            {
                Assert(uri.Host == "bootstrap.invalid" && body != null && mediaType == "application/dns-message" && limit == 65535,
                    "DoH request, content type and size bound passed to explicit network");
                var result = (byte[])body.Clone(); result[2] |= 0x80; return result;
            };
            response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "protected-doh.test")));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.NoError && network.HttpCalls == 1, "DoH uses injected network");
            network.FailHttp = true;
            response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "denied-doh.test")));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.ServerFailure && network.HttpCalls == 2,
                "DoH network failure does not retry default HttpClient");
            network.FailHttp = false;

            config.UpstreamTimeoutMs = 50;
            network.StallHttp = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "stalled-doh.test")).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.ServerFailure && clock.ElapsedMilliseconds < 1500,
                "stalled explicit-network DoH respects query deadline");
            network.StallHttp = false;
            network.StallLookup = true;
            config.UpstreamServers = new() { $"tcp://bootstrap.invalid:{port}" };
            response = A.DnsMessage.FromBytes(await Resolve(server, Query(name: "stalled-bootstrap.test")).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert(response.Header.ResponseCode == A.DnsResponseCode.ServerFailure && !tcp.Pending(),
                "stalled bootstrap respects deadline without default resolver fallback");
            network.StallLookup = false;

            network.OnHttp = (uri, body, mediaType, limit) =>
            {
                Assert(uri.Host == "filters.invalid" && body == null && mediaType == null && limit == 8 * 1024 * 1024,
                    "filter download also uses explicit-network HTTP with size limit");
                return System.Text.Encoding.UTF8.GetBytes("blocked-by-network-list.test\n");
            };
            var domains = new HashSet<string>();
            await (Task)Call(server, "LoadFilterListFromUrlAsync", "https://filters.invalid/list", domains);
            Assert(domains.Contains("blocked-by-network-list.test"), "explicit-network filter download still parses rules");
        }
        finally { tcp.Stop(); }
    }
}
namespace Android.Content { public class Context {} }
namespace Android.Util { public static class Log { public static int Info(string tag, string text) => 0; } }

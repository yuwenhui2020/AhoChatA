using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace AhoChatA.Downloads
{
    public sealed class HttpDownloadOptions
    {
        public string Url, Cookie, UserAgent, Referer, KeyOverride;
        public bool ForceHls;
    }
    public sealed class HttpDownloadInspection
    {
        public bool IsHls, IsEncrypted, Supported = true;
        public string Summary = "", OutputExtension = ".bin";
        public long TotalBytes = -1;
        public int SegmentCount;
        internal string SuggestedFileName;
        internal HlsPlaylist Playlist;
    }
    public sealed class HttpDownloadProgress
    {
        public long DownloadedBytes, TotalBytes = -1;
        public int Done, Total;
        public string Info;
    }
    // One job owns all request state. Credentials never enter global HttpClient headers/cookies.
    public sealed class HttpDownloadJob : IDisposable
    {
        readonly HttpDownloadOptions options;
        readonly Uri origin;
        readonly HttpClient client;
        public HttpDownloadJob(HttpDownloadOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            origin = HlsPlaylist.Resolve(new Uri(options.Url, UriKind.Absolute), options.Url);
            foreach (var h in new[] { options.Cookie, options.UserAgent, options.Referer }) if (h != null && (h.Contains("\r") || h.Contains("\n"))) throw new ArgumentException("Header values must not contain newlines.");
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None });
            client.Timeout = Timeout.InfiniteTimeSpan;
        }
        static bool SameOrigin(Uri a, Uri b) { return a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port; }
        async Task<HttpResponseMessage> Open(Uri uri, CancellationToken ct)
        {
            for (int redirects = 0; redirects <= 10; redirects++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(options.UserAgent) ? "Mozilla/5.0 MchatDownloader/1.0" : options.UserAgent);
                    if (SameOrigin(origin, uri) && !string.IsNullOrEmpty(options.Cookie)) request.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
                    Uri referer;
                    if (Uri.TryCreate(options.Referer, UriKind.Absolute, out referer) && (referer.Scheme == "https" || referer.Scheme == "http"))
                    {
                        // Browser-compatible cross-origin referer: origin only; never leak URL query/userinfo.
                        if (!(referer.Scheme == "https" && uri.Scheme == "http")) request.Headers.Referrer = new Uri(referer.GetLeftPart(UriPartial.Authority) + "/");
                    }
                    HttpResponseMessage r;
                    using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                        r = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
                    }
                    var code = (int)r.StatusCode;
                    if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                    {
                        var location = r.Headers.Location; r.Dispose();
                        if (location == null) throw new IOException("Redirect has no location.");
                        var next = HlsPlaylist.Resolve(uri, location.ToString());
                        if (uri.Scheme == "https" && next.Scheme == "http") throw new NotSupportedException("HTTPS downgrade redirects are blocked.");
                        uri = next; continue;
                    }
                    if (!r.IsSuccessStatusCode) { r.Dispose(); throw new HttpRequestException("HTTP " + code); }
                    // No range was requested. A partial/unsatisfied or transformed representation cannot be accepted.
                    if (code != 200 || r.Content.Headers.ContentRange != null || r.Content.Headers.ContentEncoding.Count != 0) { r.Dispose(); throw new IOException("Unexpected partial or encoded HTTP response."); }
                    return r;
                }
            }
            throw new IOException("Too many redirects.");
        }
        async Task<T> Retry<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await action(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException))
                { await Task.Delay(250 * (attempt + 1), ct).ConfigureAwait(false); }
            }
        }
        async Task<byte[]> Bytes(Uri uri, int max, CancellationToken ct)
        {
            return await Retry(async token =>
            {
                using (var r = await Open(uri, token).ConfigureAwait(false))
                using (var input = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    await Copy(input, output, r.Content.Headers.ContentLength, max, null, token).ConfigureAwait(false);
                    return output.ToArray();
                }
            }, ct).ConfigureAwait(false);
        }
        static async Task<int> Read(Stream input, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                using (idle.Token.Register(() => input.Dispose()))
                {
                    try { return await input.ReadAsync(buffer, offset, count, idle.Token).ConfigureAwait(false); }
                    catch (Exception) when (idle.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw new IOException("HTTP body read idle timeout."); }
                }
            }
        }
        static async Task<long> Copy(Stream input, Stream output, long? expected, long max, Action<long> progress, CancellationToken ct)
        {
            long n = 0; var buffer = new byte[65536];
            while (true)
            {
                int count = await Read(input, buffer, 0, buffer.Length, ct).ConfigureAwait(false); if (count == 0) break;
                n += count;
                if (n > max || (expected.HasValue && n > expected.Value)) throw new IOException("HTTP body exceeds its declared/allowed length.");
                await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false); progress?.Invoke(n);
            }
            if (expected.HasValue && n != expected.Value) throw new IOException("Truncated HTTP body: content length mismatch.");
            if (n == 0) throw new IOException("Empty HTTP body.");
            return n;
        }
        public async Task<HttpDownloadInspection> InspectAsync(CancellationToken ct)
        {
            try
            {
                // Read only a bounded prefix for direct resources, never buffer a whole movie during inspection.
                var probe = await Retry(async token =>
                {
                    using (var r = await Open(origin, token).ConfigureAwait(false))
                    using (var stream = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var prefix = new byte[1024]; int n = 0;
                        while (n < prefix.Length) { int got = await Read(stream, prefix, n, prefix.Length - n, token).ConfigureAwait(false); if (got == 0) break; n += got; }
                        var text = Encoding.UTF8.GetString(prefix, 0, n).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                        bool hls = options.ForceHls || text.StartsWith("#EXTM3U") || origin.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || (r.Content.Headers.ContentType?.MediaType ?? "").IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0;
                        var disposition = r.Content.Headers.ContentDisposition;
                        string suggested = disposition?.FileNameStar ?? disposition?.FileName;
                        if (string.IsNullOrWhiteSpace(suggested)) suggested = Uri.UnescapeDataString(Path.GetFileName(r.RequestMessage.RequestUri.AbsolutePath));
                        suggested = SafeName((suggested ?? "download").Trim('"'));
                        string ext = Path.GetExtension(suggested);
                        if (ext.Length < 2 || ext.Length > 12) ext = ".bin";
                        foreach (char c in ext) if (!(char.IsLetterOrDigit(c) || c == '.')) { ext = ".bin"; break; }
                        return new HttpDownloadInspection { IsHls = hls, TotalBytes = r.Content.Headers.ContentLength ?? -1, OutputExtension = ext, SuggestedFileName = suggested, Summary = L10n.T("HTTP 文件") };
                    }
                }, ct).ConfigureAwait(false);
                if (!probe.IsHls) return probe;
                Uri uri = origin;
                for (int depth = 0; depth < 8; depth++)
                {
                    var fetched = await Retry(async token =>
                    {
                        using (var r = await Open(uri, token).ConfigureAwait(false))
                        using (var input = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var mem = new MemoryStream())
                        { await Copy(input, mem, r.Content.Headers.ContentLength, 4 * 1024 * 1024, null, token).ConfigureAwait(false); return HlsPlaylist.Parse(Encoding.UTF8.GetString(mem.ToArray()), r.RequestMessage.RequestUri); }
                    }, ct).ConfigureAwait(false);
                    if (fetched.Variant != null) { uri = fetched.Variant; continue; }
                    if (!string.IsNullOrWhiteSpace(options.KeyOverride)) ParseOverride(options.KeyOverride);
                    return new HttpDownloadInspection { IsHls = true, IsEncrypted = fetched.Encrypted, SegmentCount = fetched.Segments.Count, OutputExtension = fetched.Map == null ? ".ts" : ".mp4", Summary = "HLS VOD · " + fetched.Segments.Count + " segments · " + (fetched.Encrypted ? "AES-128 CBC" : "unencrypted"), Playlist = fetched };
                }
                throw new NotSupportedException("HLS master nesting exceeds 8 levels.");
            }
            catch (NotSupportedException ex) { return new HttpDownloadInspection { Supported = false, IsHls = true, Summary = ex.Message }; }
        }
        static byte[] ParseOverride(string value)
        {
            Uri url;
            if (Uri.TryCreate(value, UriKind.Absolute, out url) && (url.Scheme == "http" || url.Scheme == "https")) return null;
            byte[] key;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.Length == 32) key = HlsPlaylist.Hex(value);
            else key = Convert.FromBase64String(value);
            if (key.Length != 16) throw new FormatException("Manual AES key must be 16 raw bytes encoded as hex/base64, or an HTTP(S) key URL.");
            return key;
        }
        public async Task<string> DownloadAsync(string directory, string title, Action<HttpDownloadProgress> progress, CancellationToken ct)
        {
            var inspection = await InspectAsync(ct).ConfigureAwait(false);
            if (!inspection.Supported) throw new NotSupportedException(inspection.Summary);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, ".mchat-" + Guid.NewGuid().ToString("N") + ".partial");
            // Private partials are retained on failure/cancellation; never remove user data.
            if (!inspection.IsHls)
            {
                await Retry(async token =>
                {
                    using (var r = await Open(origin, token).ConfigureAwait(false))
                    using (var input = await r.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        await Copy(input, output, r.Content.Headers.ContentLength, long.MaxValue, n => progress?.Invoke(new HttpDownloadProgress { DownloadedBytes = n, TotalBytes = r.Content.Headers.ContentLength ?? -1, Info = inspection.Summary }), token).ConfigureAwait(false);
                    }
                    return true;
                }, ct).ConfigureAwait(false);
            }
            else
            {
                var p = inspection.Playlist; long written = 0; int done = 0;
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    if (p.Map != null) { var map = await Bytes(p.Map, 16 * 1024 * 1024, ct).ConfigureAwait(false); await output.WriteAsync(map, 0, map.Length, ct).ConfigureAwait(false); written += map.Length; }
                    foreach (var seg in p.Segments)
                    {
                        ct.ThrowIfCancellationRequested();
                        var bytes = await Bytes(seg.Uri, 256 * 1024 * 1024, ct).ConfigureAwait(false);
                        if (seg.Key != null)
                        {
                            byte[] key = string.IsNullOrWhiteSpace(options.KeyOverride) ? null : ParseOverride(options.KeyOverride);
                            // Fetch at every key boundary/segment; a rotating endpoint may reuse its URL.
                            if (key == null) key = await Bytes(string.IsNullOrWhiteSpace(options.KeyOverride) ? seg.Key.Uri : HlsPlaylist.Resolve(origin, options.KeyOverride), 16, ct).ConfigureAwait(false);
                            if (key.Length != 16) throw new IOException("AES-128 key response is not exactly 16 bytes.");
                            var iv = seg.Key.IV;
                            if (iv == null) { iv = new byte[16]; ulong seq = seg.Sequence; for (int i = 15; i >= 8; i--) { iv[i] = (byte)seq; seq >>= 8; } }
                            using (var aes = Aes.Create()) { aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = key; aes.IV = iv; using (var decrypt = aes.CreateDecryptor()) bytes = decrypt.TransformFinalBlock(bytes, 0, bytes.Length); }
                        }
                        if (p.Map == null)
                        {
                            if (bytes.Length < 188 || bytes.Length % 188 != 0) throw new IOException("HLS segment is not a supported MPEG-TS stream (or AES key is incorrect).");
                            for (int offset = 0; offset < bytes.Length; offset += 188) if (bytes[offset] != 0x47) throw new IOException("Invalid MPEG-TS sync bytes; check the AES key and source format.");
                        }
                        else if (bytes.Length < 8 || !(Encoding.ASCII.GetString(bytes, 4, 4) == "styp" || Encoding.ASCII.GetString(bytes, 4, 4) == "moof" || Encoding.ASCII.GetString(bytes, 4, 4) == "sidx" || Encoding.ASCII.GetString(bytes, 4, 4) == "emsg")) throw new IOException("Unsupported fragmented MP4 media segment.");
                        await output.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false); written += bytes.Length; done++;
                        progress?.Invoke(new HttpDownloadProgress { DownloadedBytes = written, Total = p.Segments.Count, Done = done, Info = inspection.Summary });
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            var name = SafeName(string.IsNullOrWhiteSpace(title) ? inspection.SuggestedFileName : title);
            if (!inspection.IsHls && !string.IsNullOrEmpty(Path.GetExtension(name))) inspection.OutputExtension = Path.GetExtension(name);
            if (name.EndsWith(inspection.OutputExtension, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - inspection.OutputExtension.Length);
            for (int suffix = 0; ; suffix++)
            {
                var dest = Path.Combine(directory, name + (suffix == 0 ? "" : " (" + suffix + ")") + inspection.OutputExtension);
                try { File.Move(temp, dest); return dest; } catch (IOException) { if (!File.Exists(dest) && !Directory.Exists(dest)) throw; }
            }
        }
        static string SafeName(string title)
        {
            var b = new StringBuilder(); foreach (char c in title ?? "download") if (!char.IsControl(c) && "\\/:*?\"<>|".IndexOf(c) < 0) b.Append(c);
            var name = b.ToString().Trim().Trim('.'); if (name.Length == 0) name = "download";
            return name.Length > 100 ? name.Substring(0, 100) : name;
        }
        public void Dispose() { client.Dispose(); }
    }
}

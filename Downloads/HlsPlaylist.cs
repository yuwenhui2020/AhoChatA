using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
namespace AhoChatA.Downloads
{
    public sealed class HlsKey { public Uri Uri; public byte[] IV; }
    public sealed class HlsSegment { public Uri Uri; public ulong Sequence; public HlsKey Key; }
    public sealed class HlsPlaylist
    {
        public readonly List<HlsSegment> Segments = new List<HlsSegment>();
        public Uri Variant, Map;
        public bool Encrypted;
        public static Dictionary<string, string> Attributes(string text)
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(text, "([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.IgnoreCase)) r[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
            return r;
        }
        public static Uri Resolve(Uri parent, string value)
        {
            var uri = new Uri(parent, value);
            if ((uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new NotSupportedException("Only HTTP(S) URLs without embedded credentials are supported.");
            return uri;
        }
        public static byte[] Hex(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
            if (text.Length != 32) throw new FormatException("AES key/IV must contain exactly 16 bytes (32 hex digits).");
            var bytes = new byte[16];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        public static byte[] ParseIV(string value)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            if (value.Length == 0 || value.Length > 32) throw new FormatException("HLS IV exceeds 128 bits.");
            return Hex(value.PadLeft(32, '0'));
        }
        public static HlsPlaylist Parse(string text, Uri finalUri)
        {
            if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new FormatException("Invalid HLS playlist.");
            var p = new HlsPlaylist(); HlsKey key = null; ulong sequence = 0;
            bool end = false, pending = false; long bandwidth = -1, best = -1;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:"))
                {
                    var a = Attributes(line.Substring(line.IndexOf(':') + 1));
                    if (a.ContainsKey("AUDIO") || a.ContainsKey("VIDEO")) throw new NotSupportedException("HLS separate audio/video tracks are not supported.");
                    bandwidth = a.ContainsKey("BANDWIDTH") ? long.Parse(a["BANDWIDTH"], CultureInfo.InvariantCulture) : 0; pending = true;
                }
                else if (line.StartsWith("#EXT-X-KEY:"))
                {
                    var a = Attributes(line.Substring(line.IndexOf(':') + 1)); string method; a.TryGetValue("METHOD", out method);
                    if (method == "NONE") { key = null; continue; }
                    if (method != "AES-128" || (a.ContainsKey("KEYFORMAT") && a["KEYFORMAT"] != "identity")) throw new NotSupportedException("Unsupported HLS encryption/DRM: " + method + ". Only identity AES-128 CBC is supported.");
                    if (!a.ContainsKey("URI")) throw new FormatException("AES-128 key URI is missing.");
                    key = new HlsKey { Uri = Resolve(finalUri, a["URI"]), IV = a.ContainsKey("IV") ? ParseIV(a["IV"]) : null }; p.Encrypted = true;
                }
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) sequence = ulong.Parse(line.Substring(line.IndexOf(':') + 1), CultureInfo.InvariantCulture);
                else if (line == "#EXT-X-ENDLIST") end = true;
                else if (line.StartsWith("#EXT-X-BYTERANGE") || line.StartsWith("#EXT-X-DISCONTINUITY") || line.StartsWith("#EXT-X-GAP") || line.StartsWith("#EXT-X-PART") || line.StartsWith("#EXT-X-SKIP")) throw new NotSupportedException("HLS byte ranges, discontinuities, gaps and partial/delta playlists are not supported.");
                else if (line.StartsWith("#EXT-X-SESSION-KEY:")) throw new NotSupportedException("HLS session encryption is not supported.");
                else if (line.StartsWith("#EXT-X-MAP:"))
                {
                    var a = Attributes(line.Substring(line.IndexOf(':') + 1));
                    if (a.ContainsKey("BYTERANGE") || key != null || p.Map != null || p.Segments.Count != 0) throw new NotSupportedException("Only one unencrypted initialization map before all segments is supported.");
                    if (!a.ContainsKey("URI")) throw new FormatException("HLS initialization map URI is missing."); p.Map = Resolve(finalUri, a["URI"]);
                }
                else if (line.Length > 0 && line[0] != '#')
                {
                    if (pending) { if (bandwidth > best) { p.Variant = Resolve(finalUri, line); best = bandwidth; } pending = false; }
                    else p.Segments.Add(new HlsSegment { Uri = Resolve(finalUri, line), Sequence = sequence++, Key = key });
                }
            }
            if (pending) throw new FormatException("HLS variant URI is missing.");
            if (p.Variant != null) { if (p.Segments.Count > 0) throw new FormatException("Mixed master/media playlist."); return p; }
            if (!end) throw new NotSupportedException("Live/event HLS without EXT-X-ENDLIST is not supported; wait until the VOD is complete.");
            if (p.Segments.Count == 0) throw new FormatException("HLS playlist has no media segments.");
            if (p.Map != null && p.Encrypted) throw new NotSupportedException("Encrypted fragmented MP4 is not supported.");
            if (p.Map == null && p.Segments.Any(s => s.Uri.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) || s.Uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))) throw new NotSupportedException("Fragmented MP4 requires an initialization map.");
            return p;
        }
    }
}

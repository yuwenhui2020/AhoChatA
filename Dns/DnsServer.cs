using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Util;

namespace AhoChatA.DnsCore
{
    /// <summary>
    /// DNS 记录类型
    /// </summary>
    public enum DnsRecordType : ushort
    {
        A = 1,
        NS = 2,
        CNAME = 5,
        SOA = 6,
        PTR = 12,
        MX = 15,
        TXT = 16,
        AAAA = 28,
        SRV = 33,
        HTTPS = 65,
        SVCB = 64
    }

    /// <summary>
    /// DNS 响应码
    /// </summary>
    public enum DnsResponseCode : byte
    {
        NoError = 0,
        FormatError = 1,
        ServerFailure = 2,
        NXDomain = 3,
        NotImplemented = 4,
        Refused = 5
    }

    /// <summary>
    /// DNS 资源记录
    /// </summary>
    public class DnsResourceRecord
    {
        public string Name { get; set; } = "";
        public DnsRecordType Type { get; set; }
        public ushort Class { get; set; } = 1;
        public uint TTL { get; set; } = 300;
        internal int TtlOffset { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public string DataAsString
        {
            get
            {
                switch (Type)
                {
                    case DnsRecordType.A:
                        return new IPAddress(Data).ToString();
                    case DnsRecordType.AAAA:
                        return new IPAddress(Data).ToString();
                    case DnsRecordType.CNAME:
                    case DnsRecordType.NS:
                    case DnsRecordType.PTR:
                        return Encoding.ASCII.GetString(Data).TrimEnd('.');
                    case DnsRecordType.TXT:
                        return Encoding.UTF8.GetString(Data);
                    default:
                        return BitConverter.ToString(Data);
                }
            }
        }
    }

    /// <summary>
    /// DNS 消息头
    /// </summary>
    public class DnsMessageHeader
    {
        public ushort Id { get; set; }
        public bool IsResponse { get; set; }
        public byte OpCode { get; set; }
        public bool Authoritative { get; set; }
        public bool Truncated { get; set; }
        public bool RecursionDesired { get; set; }
        public bool RecursionAvailable { get; set; }
        public byte Z { get; set; }
        public DnsResponseCode ResponseCode { get; set; }
        public ushort QuestionCount { get; set; }
        public ushort AnswerCount { get; set; }
        public ushort AuthorityCount { get; set; }
        public ushort AdditionalCount { get; set; }
    }

    /// <summary>
    /// DNS 问题
    /// </summary>
    public class DnsQuestion
    {
        public string Name { get; set; } = "";
        public DnsRecordType Type { get; set; }
        public ushort Class { get; set; } = 1;
    }

    /// <summary>
    /// DNS 消息
    /// </summary>
    public class DnsMessage
    {
        public DnsMessageHeader Header { get; set; } = new DnsMessageHeader();
        public List<DnsQuestion> Questions { get; set; } = new List<DnsQuestion>();
        public List<DnsResourceRecord> Answers { get; set; } = new List<DnsResourceRecord>();
        public List<DnsResourceRecord> Authorities { get; set; } = new List<DnsResourceRecord>();
        public List<DnsResourceRecord> Additionals { get; set; } = new List<DnsResourceRecord>();

        internal byte[] WirePacket { get; private set; }

        public byte[] ToBytes()
        {
            if (WirePacket != null) return CopyWire(Header.Id);
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // Header
                Write16(bw, Header.Id);
                byte flags1 = 0, flags2 = 0;
                if (Header.IsResponse) flags1 |= 0x80;
                flags1 |= (byte)((Header.OpCode & 0x0F) << 3);
                if (Header.Authoritative) flags1 |= 0x04;
                if (Header.Truncated) flags1 |= 0x02;
                if (Header.RecursionDesired) flags1 |= 0x01;
                if (Header.RecursionAvailable) flags2 |= 0x80;
                flags2 |= (byte)((Header.Z & 0x07) << 4);
                flags2 |= (byte)((byte)Header.ResponseCode & 0x0F);
                bw.Write(flags1);
                bw.Write(flags2);
                Write16(bw, checked((ushort)Questions.Count));
                Write16(bw, checked((ushort)Answers.Count));
                Write16(bw, checked((ushort)Authorities.Count));
                Write16(bw, checked((ushort)Additionals.Count));

                // Questions
                foreach (var q in Questions)
                {
                    WriteDomainName(bw, q.Name);
                    Write16(bw, (ushort)q.Type);
                    Write16(bw, q.Class);
                }

                // Answers
                foreach (var a in Answers)
                {
                    WriteResourceRecord(bw, a);
                }

                // Authorities
                foreach (var a in Authorities)
                {
                    WriteResourceRecord(bw, a);
                }

                // Additionals
                foreach (var a in Additionals)
                {
                    WriteResourceRecord(bw, a);
                }

                return ms.ToArray();
            }
        }

        public static DnsMessage FromBytes(byte[] data)
        {
            var msg = new DnsMessage { WirePacket = (byte[])data.Clone() };
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                // Header
                msg.Header.Id = Read16(br);
                byte flags1 = br.ReadByte();
                byte flags2 = br.ReadByte();
                msg.Header.IsResponse = (flags1 & 0x80) != 0;
                msg.Header.OpCode = (byte)((flags1 >> 3) & 0x0F);
                msg.Header.Authoritative = (flags1 & 0x04) != 0;
                msg.Header.Truncated = (flags1 & 0x02) != 0;
                msg.Header.RecursionDesired = (flags1 & 0x01) != 0;
                msg.Header.RecursionAvailable = (flags2 & 0x80) != 0;
                msg.Header.Z = (byte)((flags2 >> 4) & 0x07);
                msg.Header.ResponseCode = (DnsResponseCode)(flags2 & 0x0F);
                msg.Header.QuestionCount = Read16(br);
                msg.Header.AnswerCount = Read16(br);
                msg.Header.AuthorityCount = Read16(br);
                msg.Header.AdditionalCount = Read16(br);

                // Questions
                for (int i = 0; i < msg.Header.QuestionCount; i++)
                {
                    string name = ReadDomainName(br, ms);
                    msg.Questions.Add(new DnsQuestion
                    {
                        Name = name,
                        Type = (DnsRecordType)Read16(br),
                        Class = Read16(br)
                    });
                }

                // Answers
                for (int i = 0; i < msg.Header.AnswerCount; i++)
                {
                    msg.Answers.Add(ReadResourceRecord(br, ms));
                }

                // Authorities
                for (int i = 0; i < msg.Header.AuthorityCount; i++)
                {
                    msg.Authorities.Add(ReadResourceRecord(br, ms));
                }

                // Additionals
                for (int i = 0; i < msg.Header.AdditionalCount; i++)
                {
                    msg.Additionals.Add(ReadResourceRecord(br, ms));
                }

                if (ms.Position != ms.Length) throw new InvalidDataException("Trailing DNS data");
                return msg;
            }
        }

        // A parsed packet keeps its original layout. RDATA can contain pointers into it.
        internal byte[] CopyWire(ushort id)
        {
            var bytes = (byte[])WirePacket.Clone();
            bytes[0] = (byte)(id >> 8);
            bytes[1] = (byte)id;
            return bytes;
        }

        private static ushort Read16(BinaryReader reader) => (ushort)((reader.ReadByte() << 8) | reader.ReadByte());

        private static void Write16(BinaryWriter writer, ushort value)
        {
            writer.Write((byte)(value >> 8));
            writer.Write((byte)value);
        }

        private static void WriteDomainName(BinaryWriter writer, string name)
        {
            if (!string.IsNullOrEmpty(name) && name != ".")
            {
                int expanded = 1;
                foreach (string label in name.TrimEnd('.').Split('.'))
                {
                    var bytes = new List<byte>();
                    for (int i = 0; i < label.Length; i++)
                    {
                        if (label[i] == '\\' && i + 3 < label.Length && byte.TryParse(label.Substring(i + 1, 3), out byte escaped))
                        {
                            bytes.Add(escaped);
                            i += 3;
                        }
                        else if (label[i] <= 127 && label[i] != '\\') bytes.Add((byte)label[i]);
                        else throw new InvalidDataException("Invalid DNS label");
                    }
                    expanded += bytes.Count + 1;
                    if (expanded > 255 || bytes.Count == 0 || bytes.Count > 63) throw new InvalidDataException("Invalid DNS label length");
                    writer.Write((byte)bytes.Count);
                    writer.Write(bytes.ToArray());
                }
            }
            writer.Write((byte)0);
        }

        private static string ReadDomainName(BinaryReader reader, MemoryStream stream)
        {
            var labels = new List<string>();
            long resume = -1;
            int expanded = 1, jumps = 0;
            while (true)
            {
                long position = stream.Position;
                int len = reader.ReadByte();
                if (len == 0) break;
                if ((len & 0xC0) == 0xC0)
                {
                    int offset = ((len & 0x3F) << 8) | reader.ReadByte();
                    if (offset < 12 || offset >= position || ++jumps > 128)
                        throw new InvalidDataException("Invalid DNS compression pointer");
                    if (resume < 0) resume = stream.Position;
                    stream.Position = offset;
                    continue;
                }
                expanded += len + 1;
                if (len > 63 || expanded > 255) throw new InvalidDataException("Invalid DNS name length");
                var label = new StringBuilder(len);
                for (int i = 0; i < len; i++)
                {
                    byte b = reader.ReadByte();
                    if (b >= 33 && b <= 126 && b != '.' && b != '\\') label.Append((char)b);
                    else label.Append('\\').Append(b.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                }
                labels.Add(label.ToString());
            }
            if (resume >= 0) stream.Position = resume;
            return string.Join(".", labels);
        }

        private static void WriteResourceRecord(BinaryWriter bw, DnsResourceRecord rr)
        {
            WriteDomainName(bw, rr.Name);
            Write16(bw, (ushort)rr.Type);
            Write16(bw, rr.Class);
            Write16(bw, (ushort)(rr.TTL >> 16));
            Write16(bw, (ushort)rr.TTL);
            Write16(bw, checked((ushort)rr.Data.Length));
            bw.Write(rr.Data);
        }

        private static DnsResourceRecord ReadResourceRecord(BinaryReader br, MemoryStream ms)
        {
            string name = ReadDomainName(br, ms);
            var type = (DnsRecordType)Read16(br);
            ushort cls = Read16(br);
            int ttlOffset = (int)ms.Position;
            uint ttl = ((uint)Read16(br) << 16) | Read16(br);
            ushort dataLen = Read16(br);
            byte[] data = br.ReadBytes(dataLen);
            if (data.Length != dataLen) throw new EndOfStreamException();
            return new DnsResourceRecord { Name = name, Type = type, Class = cls, TTL = ttl, Data = data, TtlOffset = ttlOffset };
        }
    }

    /// <summary>
    /// DNS 服务器配置
    /// </summary>
    public class DnsServerConfig
    {
        public bool Enabled { get; set; } = true;
        public int Port { get; set; } = 53;
        public List<string> UpstreamServers { get; set; } = new List<string> { "223.5.5.5", "119.29.29.29" };
        public int UpstreamTimeoutMs { get; set; } = 5000;
        public bool EnableCache { get; set; } = true;
        public int CacheSize { get; set; } = 10000;
        public int CacheTtlSeconds { get; set; } = 300;
        public bool BlockAds { get; set; } = true;
        public bool BlockTrackers { get; set; } = true;
        public bool BlockMalware { get; set; } = true;
        public List<string> CustomBlocklists { get; set; } = new List<string>();
        public List<string> CustomAllowlists { get; set; } = new List<string>();
        public List<DnsRewrite> Rewrites { get; set; } = new List<DnsRewrite>();
        public bool LogQueries { get; set; } = true;
        public int MaxLogEntries { get; set; } = 10000;
        // DoH/DoT 配置
        public bool PreferSecureUpstream { get; set; } = false; // 优先使用加密上游
        public string DoHCertPath { get; set; } = ""; // 可选：自定义 CA 证书路径
    }

    /// <summary>
    /// 上游服务器类型
    /// </summary>
    public enum UpstreamType
    {
        UDP,
        TCP,
        DoH,  // DNS over HTTPS
        DoT   // DNS over TLS
    }

    /// <summary>
    /// 上游服务器配置
    /// </summary>
    public class UpstreamServer
    {
        public string Address { get; set; } = ""; // 格式: "8.8.8.8:53", "https://dns.google/dns-query", "tls://1.1.1.1:853"
        public UpstreamType Type { get; set; } = UpstreamType.UDP;
        public bool Enabled { get; set; } = true;
        public string HostName { get; set; } = ""; // 用于 TLS SNI / HTTP Host
        public string Path { get; set; } = "/dns-query"; // DoH 路径
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(); // DoH 自定义头
    }

    /// <summary>
    /// DNS 重写规则
    /// </summary>
    public class DnsRewrite
    {
        public string Domain { get; set; } = "";
        public string Answer { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// DNS 缓存项
    /// </summary>
    internal class CacheEntry
    {
        public DnsMessage Response { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public long StoredTimestamp { get; set; }
    }

    /// <summary>
    /// DNS 查询日志项
    /// </summary>
    public class DnsQueryLog
    {
        public DateTime Timestamp { get; set; }
        public string ClientIp { get; set; } = "";
        public string Domain { get; set; } = "";
        public DnsRecordType Type { get; set; }
        public DnsResponseCode ResponseCode { get; set; }
        public bool Blocked { get; set; }
        public long DurationMs { get; set; }
        public string UpstreamServer { get; set; } = "";
    }

    /// <summary>
    /// DNS 统计信息
    /// </summary>
    public class DnsStats
    {
        public long TotalQueries { get; set; }
        public long BlockedQueries { get; set; }
        public long CachedQueries { get; set; }
        public long UpstreamQueries { get; set; }
        public long Errors { get; set; }
        public Dictionary<string, long> QueriesByType { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> QueriesByClient { get; set; } = new Dictionary<string, long>();
        public List<TopDomain> TopDomains { get; set; } = new List<TopDomain>();
        public List<TopClient> TopClients { get; set; } = new List<TopClient>();
        public long AverageResponseTimeMs { get; set; }
    }

    public class TopDomain
    {
        public string Domain { get; set; } = "";
        public long Count { get; set; }
    }

    public class TopClient
    {
        public string ClientIp { get; set; } = "";
        public long Count { get; set; }
    }

    // The VPN supplies every operation that could otherwise bootstrap through its own DNS.
    // Implementations must fail rather than fall back to the process-default resolver/network.
    public interface IDnsUpstreamNetwork
    {
        void ProtectSocket(Socket socket);
        Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken token);
        Task<byte[]> SendHttpAsync(Uri uri, byte[] body, string mediaType, int maxBytes, CancellationToken token);
    }

    /// <summary>
    /// DNS 服务器核心实现
    /// </summary>
    public class DnsServer : IDisposable
    {
        private readonly DnsServerConfig _config;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>();
        private readonly ConcurrentQueue<DnsQueryLog> _queryLog = new ConcurrentQueue<DnsQueryLog>();
        private readonly object _filterLock = new object();
        private readonly HashSet<string> _blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _allowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DnsRewrite> _rewrites = new List<DnsRewrite>();
        private readonly UdpClient _udpServer;
        private readonly TcpListener _tcpServer;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _udpTask;
        private readonly Task _tcpTask;
        private readonly Task _cleanupTask;
        private readonly Timer _statsTimer;
        private readonly Action<string> _log;
        private long _totalQueries, _blockedQueries, _cachedQueries, _upstreamQueries, _errors;
        private long _responseTimeTotalMs, _responseTimeSamples;
        private readonly ConcurrentDictionary<string, long> _queriesByType = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _queriesByClient = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _domainCounts = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _clientCounts = new ConcurrentDictionary<string, long>();

        private readonly IDnsUpstreamNetwork _upstreamNetwork;

        public DnsServer(Context context, DnsServerConfig config, Action<string> log = null, IDnsUpstreamNetwork upstreamNetwork = null)
        {
            _upstreamNetwork = upstreamNetwork;
            _config = config ?? throw new ArgumentNullException(nameof(config));
            NormalizeConfig();
            _log = log ?? (s => Android.Util.Log.Info("DnsServer", s));

            _udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, _config.Port));
            _tcpServer = new TcpListener(IPAddress.Loopback, ((IPEndPoint)_udpServer.Client.LocalEndPoint).Port);
            try { _tcpServer.Start(); }
            catch { _udpServer.Close(); throw; }

            _ = LoadFilterRulesAsync();
            _udpTask = Task.Run(() => RunUdpLoop(_cts.Token));
            _tcpTask = Task.Run(() => RunTcpLoop(_cts.Token));
            _cleanupTask = Task.Run(() => CleanupLoop(_cts.Token));
            _statsTimer = new Timer(_ => UpdateStats(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _log($"[DnsServer] 启动完成，监听端口 {_config.Port} (UDP/TCP)");
        }

        private void NormalizeConfig()
        {
            _config.Port = Math.Min(65535, Math.Max(0, _config.Port));
            _config.UpstreamTimeoutMs = Math.Max(1, _config.UpstreamTimeoutMs);
            _config.CacheSize = Math.Max(1, _config.CacheSize);
            _config.CacheTtlSeconds = Math.Max(0, _config.CacheTtlSeconds);
            _config.MaxLogEntries = Math.Max(0, _config.MaxLogEntries);
            _config.UpstreamServers = _config.UpstreamServers ?? new List<string>();
            _config.CustomBlocklists = _config.CustomBlocklists ?? new List<string>();
            _config.CustomAllowlists = _config.CustomAllowlists ?? new List<string>();
            _config.Rewrites = _config.Rewrites ?? new List<DnsRewrite>();
        }

        internal int ListeningPort => ((IPEndPoint)_udpServer.Client.LocalEndPoint).Port;

        public DnsServerConfig GetConfig() => _config;

        public void UpdateConfig(Action<DnsServerConfig> updateAction)
        {
            updateAction(_config);
            NormalizeConfig();
            _cache.Clear();
            _ = LoadFilterRulesAsync();
        }

        public DnsStats GetStats()
        {
            var stats = new DnsStats
            {
                TotalQueries = _totalQueries,
                BlockedQueries = _blockedQueries,
                CachedQueries = _cachedQueries,
                UpstreamQueries = _upstreamQueries,
                Errors = _errors,
                QueriesByType = _queriesByType.ToDictionary(k => k.Key, v => v.Value),
                QueriesByClient = _queriesByClient.ToDictionary(k => k.Key, v => v.Value),
                AverageResponseTimeMs = _responseTimeSamples == 0 ? 0 : _responseTimeTotalMs / _responseTimeSamples
            };

            stats.TopDomains = _domainCounts
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => new TopDomain { Domain = kv.Key, Count = kv.Value })
                .ToList();

            stats.TopClients = _clientCounts
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => new TopClient { ClientIp = kv.Key, Count = kv.Value })
                .ToList();

            return stats;
        }

        public List<DnsQueryLog> GetQueryLog(int limit = 100)
        {
            return _queryLog.Reverse().Take(limit).ToList();
        }

        public void AddBlockedDomain(string domain)
        {
            domain = NormalizeDomain(domain);
            if (domain != null) lock (_filterLock) _blockOverrides[domain] = true;
        }

        public void RemoveBlockedDomain(string domain)
        {
            domain = NormalizeDomain(domain);
            if (domain != null) lock (_filterLock) _blockOverrides[domain] = false;
        }

        public void AddAllowedDomain(string domain)
        {
            domain = NormalizeDomain(domain);
            if (domain != null) lock (_filterLock) _allowOverrides.Add(domain);
        }

        public void AddRewrite(DnsRewrite rewrite)
        {
            string domain = NormalizeDomain(rewrite?.Domain);
            if (domain == null || !IPAddress.TryParse(rewrite.Answer, out _)) return;
            lock (_filterLock)
                _rewriteOverrides[domain] = new DnsRewrite { Domain = domain, Answer = rewrite.Answer, Enabled = rewrite.Enabled };
        }

        public void RemoveRewrite(string domain)
        {
            domain = NormalizeDomain(domain);
            if (domain != null) lock (_filterLock) _rewriteOverrides[domain] = new DnsRewrite { Domain = domain, Enabled = false };
        }

        private int _filterGeneration;
        private readonly Dictionary<string, bool> _blockOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _allowOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DnsRewrite> _rewriteOverrides = new Dictionary<string, DnsRewrite>(StringComparer.OrdinalIgnoreCase);

        private const string BundledFilterResource = "Mchat.Dns.AdGuardDnsFilter.txt";
        private static readonly Lazy<BundledRules> BundledFilter = new Lazy<BundledRules>(ReadBundledFilter);
        private BundledRules _bundledRules;

        private sealed class BundledRules
        {
            internal readonly HashSet<string> Block = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> ExactBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> ExactAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> ImportantBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> ImportantExactBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal int BlockCount => Block.Count + ExactBlock.Count + ImportantBlock.Count + ImportantExactBlock.Count;
            internal int AllowCount => Allow.Count + ExactAllow.Count;
            internal int Skipped;
            internal int Disabled;
        }

        private static BundledRules ReadBundledFilter()
        {
            using (var stream = typeof(DnsServer).Assembly.GetManifestResourceStream(BundledFilterResource))
            {
                if (stream == null) throw new InvalidDataException("Missing embedded AdGuard DNS filter");
                using (var reader = new StreamReader(stream))
                {
                    var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (string line; (line = reader.ReadLine()) != null;)
                        if (!line.StartsWith("!", StringComparison.Ordinal) && line.EndsWith("$badfilter", StringComparison.Ordinal))
                            disabled.Add(line.Substring(0, line.Length - "$badfilter".Length));

                    // Resolve $badfilter globally before indexing rules, regardless of line order.
                    stream.Position = 0;
                    reader.DiscardBufferedData();
                    var rules = new BundledRules();
                    for (string line; (line = reader.ReadLine()) != null;)
                    {
                        if (line.Length == 0 || line[0] == '!' || line.EndsWith("$badfilter", StringComparison.Ordinal)) continue;
                        if (disabled.Contains(line)) { rules.Disabled++; continue; }
                        bool allow = line.StartsWith("@@", StringComparison.Ordinal);
                        string pattern = allow ? line.Substring(2) : line;
                        bool important = !allow && pattern.EndsWith("$important", StringComparison.Ordinal);
                        if (important) pattern = pattern.Substring(0, pattern.Length - "$important".Length);
                        bool suffix = pattern.StartsWith("||", StringComparison.Ordinal);
                        bool exact = !suffix && pattern.StartsWith("|", StringComparison.Ordinal);
                        int endLength = pattern.EndsWith("^|", StringComparison.Ordinal) ? 2 :
                            pattern.EndsWith("^", StringComparison.Ordinal) ? 1 : 0;
                        int startLength = suffix ? 2 : 1;
                        if ((!suffix && !exact) || endLength == 0 || pattern.Length <= startLength + endLength)
                        { rules.Skipped++; continue; }
                        string domain = NormalizeDomain(pattern.Substring(startLength, pattern.Length - startLength - endLength));
                        if (domain == null) { rules.Skipped++; continue; }
                        var target = allow ? (suffix ? rules.Allow : rules.ExactAllow) :
                            important ? (suffix ? rules.ImportantBlock : rules.ImportantExactBlock) :
                            (suffix ? rules.Block : rules.ExactBlock);
                        target.Add(domain);
                    }
                    if (rules.BlockCount == 0) throw new InvalidDataException("Empty embedded AdGuard DNS filter");
                    return rules;
                }
            }
        }

        private async Task LoadFilterRulesAsync()
        {
            int generation = Interlocked.Increment(ref _filterGeneration);
            var blocks = (_config.CustomBlocklists ?? new List<string>()).ToArray();
            var allows = (_config.CustomAllowlists ?? new List<string>()).ToArray();
            var rewrites = (_config.Rewrites ?? new List<DnsRewrite>()).Where(r => r != null && r.Enabled)
                .Select(r => new DnsRewrite { Domain = NormalizeDomain(r.Domain), Answer = r.Answer, Enabled = true })
                .Where(r => r.Domain != null && IPAddress.TryParse(r.Answer, out _)).ToList();
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The upstream feed combines ads and tracking; either switch enables the whole feed.
            // BlockMalware does not enable this list: there is no bundled malware feed.
            BundledRules bundled = null;
            if (_config.BlockAds || _config.BlockTrackers)
            {
                try
                {
                    bundled = BundledFilter.Value;
                    _log($"[DnsServer] AdGuard DNS filter ({bundled.BlockCount} block rules, {bundled.AllowCount} exceptions, {bundled.Disabled} disabled, {bundled.Skipped} unsupported)");
                }
                catch (Exception ex) { _log($"[DnsServer] Unable to load embedded AdGuard DNS filter: {ex.Message}"); }
            }
            PublishFilters(generation, bundled, blocked, allowed, rewrites);
            if (blocks.Length == 0 && allows.Length == 0) return;
            foreach (string url in blocks) await LoadFilterListFromUrlAsync(url, blocked).ConfigureAwait(false);
            foreach (string url in allows) await LoadFilterListFromUrlAsync(url, allowed).ConfigureAwait(false);
            PublishFilters(generation, bundled, blocked, allowed, rewrites);
        }

        private void PublishFilters(int generation, BundledRules bundled, HashSet<string> blocked, HashSet<string> allowed, List<DnsRewrite> rewrites)
        {
            lock (_filterLock)
            {
                if (generation != _filterGeneration) return;
                _bundledRules = bundled;
                _blockedDomains.Clear();
                _blockedDomains.UnionWith(blocked);
                _allowedDomains.Clear();
                _allowedDomains.UnionWith(allowed);
                _rewrites.Clear();
                _rewrites.AddRange(rewrites);
            }
        }

        private async Task LoadFilterListFromUrlAsync(string url, HashSet<string> target)
        {
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var bytes = await SendHttpAsync(new Uri(url), null, null, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                    var content = Encoding.UTF8.GetString(bytes);
                    foreach (var raw in content.Split('\n'))
                    {
                        var fields = raw.Split('#')[0].Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length == 0) continue;
                        if (IPAddress.TryParse(fields[0], out _))
                        {
                            for (int i = 1; i < fields.Length; i++) AddFilterDomain(target, fields[i]);
                        }
                        else if (fields.Length == 1)
                        {
                            var rule = fields[0];
                            if (rule.StartsWith("||", StringComparison.Ordinal) && rule.EndsWith("^", StringComparison.Ordinal))
                                rule = rule.Substring(2, rule.Length - 3);
                            AddFilterDomain(target, rule);
                        }
                    }
                }
            }
            catch (Exception ex) { _log($"[DnsServer] 加载过滤列表失败 {url}: {ex.Message}"); }
        }

        private static void AddFilterDomain(HashSet<string> domains, string value)
        {
            string domain = NormalizeDomain(value);
            if (domain != null) domains.Add(domain);
        }

        private static string NormalizeDomain(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string domain = value.Trim().TrimEnd('.');
            try { domain = new System.Globalization.IdnMapping().GetAscii(domain); }
            catch (ArgumentException) { return null; }
            if (domain.Length > 253 || !domain.Contains(".") || IPAddress.TryParse(domain, out _)) return null;
            foreach (string label in domain.Split('.'))
                if (label.Length == 0 || label.Length > 63 || label.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')))
                    return null;
            return domain;
        }

        private static bool MatchesDomain(string name, string suffix) =>
            name.Equals(suffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

        private bool IsBlocked(string name)
        {
            var bundled = _bundledRules;
            bool bundleException = bundled != null &&
                (bundled.ExactAllow.Contains(name) || ContainsSuffix(bundled.Allow, name));
            if (!_blockOverrides.ContainsKey(name) && bundled != null &&
                (bundled.ImportantExactBlock.Contains(name) || (!bundleException && bundled.ExactBlock.Contains(name)))) return true;
            for (string suffix = name; ;)
            {
                if (_blockOverrides.TryGetValue(suffix, out bool isBlocked))
                {
                    if (isBlocked) return true;
                }
                else if (_blockedDomains.Contains(suffix) || (bundled != null &&
                    (bundled.ImportantBlock.Contains(suffix) || (!bundleException && bundled.Block.Contains(suffix))))) return true;
                int dot = suffix.IndexOf('.');
                if (dot < 0) return false;
                suffix = suffix.Substring(dot + 1);
            }
        }

        private static bool ContainsSuffix(HashSet<string> domains, string name)
        {
            for (string suffix = name; ;)
            {
                if (domains.Contains(suffix)) return true;
                int dot = suffix.IndexOf('.');
                if (dot < 0) return false;
                suffix = suffix.Substring(dot + 1);
            }
        }

        private bool IsAllowed(string name)
        {
            for (string suffix = name; ; suffix = suffix.Substring(suffix.IndexOf('.') + 1))
            {
                if (_allowOverrides.Contains(suffix) || _allowedDomains.Contains(suffix)) return true;
                if (suffix.IndexOf('.') < 0) return false;
            }
        }

        private async Task RunUdpLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpServer.ReceiveAsync();
                    _ = Task.Run(() => HandleUdpQuery(result.RemoteEndPoint, result.Buffer), token);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    _log($"[DnsServer] UDP 接收错误: {ex.Message}");
                }
            }
        }

        private async Task RunTcpLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _tcpServer.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleTcpClient(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    _log($"[DnsServer] TCP 接收错误: {ex.Message}");
                }
            }
        }

        private async Task HandleUdpQuery(IPEndPoint clientEndPoint, byte[] queryData)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string clientIp = clientEndPoint.Address.ToString();

            try
            {
                var query = DnsMessage.FromBytes(queryData);
                if (query.Header.IsResponse || query.Questions.Count == 0) return;

                var question = query.Questions[0];
                var responseBytes = await ProcessQuery(query, clientIp).ConfigureAwait(false);
                var opt = query.Additionals.FirstOrDefault(r => (ushort)r.Type == 41);
                if (responseBytes.Length > (opt == null ? 512 : Math.Min(65507, Math.Max(512, (int)opt.Class))))
                {
                    var truncated = new DnsMessage { Questions = new List<DnsQuestion> { question } };
                    truncated.Header.Id = query.Header.Id;
                    truncated.Header.IsResponse = true;
                    truncated.Header.RecursionDesired = query.Header.RecursionDesired;
                    truncated.Header.RecursionAvailable = true;
                    truncated.Header.Truncated = true;
                    truncated.Header.ResponseCode = (DnsResponseCode)(responseBytes[3] & 15);
                    responseBytes = truncated.ToBytes();
                }
                await _udpServer.SendAsync(responseBytes, responseBytes.Length, clientEndPoint);

                // Block decisions are logged in ProcessQuery; this transport does not duplicate them.
            }
            catch (Exception ex)
            {
                _log($"[DnsServer] 处理 UDP 查询失败: {ex.Message}");
                Interlocked.Increment(ref _errors);
            }
        }

        private async Task HandleTcpClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    while (!_cts.IsCancellationRequested)
                    {
                        byte[] queryData;
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                        using (idle.Token.Register(client.Close))
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(15));
                            queryData = await ReadFrame(stream, idle.Token).ConfigureAwait(false);
                        }
                        var query = DnsMessage.FromBytes(queryData);
                        if (query.Header.IsResponse || query.Questions.Count == 0) return;
                        var ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                        var response = await ProcessQuery(query, ip).ConfigureAwait(false);
                        using (var write = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                        using (write.Token.Register(client.Close))
                        {
                            write.CancelAfter(TimeSpan.FromSeconds(15));
                            var length = new byte[] { (byte)(response.Length >> 8), (byte)response.Length };
                            await stream.WriteAsync(length, 0, 2, write.Token).ConfigureAwait(false);
                            await stream.WriteAsync(response, 0, response.Length, write.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (EndOfStreamException) { }
                catch (Exception ex) { if (!_cts.IsCancellationRequested) _log($"[DnsServer] TCP: {ex.Message}"); }
            }
        }

        private async Task<byte[]> ProcessQuery(DnsMessage query, string clientIp)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return await ProcessQueryCore(query, clientIp, timer).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Add(ref _responseTimeTotalMs, timer.ElapsedMilliseconds);
                Interlocked.Increment(ref _responseTimeSamples);
            }
        }

        private async Task<byte[]> ProcessQueryCore(DnsMessage query, string clientIp, System.Diagnostics.Stopwatch timer)
        {
            Interlocked.Increment(ref _totalQueries);
            _queriesByType.AddOrUpdate(query.Questions[0].Type.ToString(), 1, (k, v) => v + 1);
            _queriesByClient.AddOrUpdate(clientIp, 1, (k, v) => v + 1);
            _clientCounts.AddOrUpdate(clientIp, 1, (k, v) => v + 1);
            _domainCounts.AddOrUpdate(query.Questions[0].Name, 1, (k, v) => v + 1);

            var question = query.Questions[0];
            var response = new DnsMessage
            {
                Header = new DnsMessageHeader
                {
                    Id = query.Header.Id,
                    IsResponse = true,
                    OpCode = query.Header.OpCode,
                    RecursionDesired = query.Header.RecursionDesired,
                    RecursionAvailable = true,
                    ResponseCode = DnsResponseCode.NoError,
                    Z = (byte)(query.Header.Z & 1),
                    QuestionCount = 1
                },
                Questions = new List<DnsQuestion> { question }
            };

            if (query.Additionals.Count(r => (ushort)r.Type == 41) > 1 || query.Additionals.Any(r => (ushort)r.Type == 41 && r.Name.Length != 0))
            {
                response.Header.ResponseCode = DnsResponseCode.FormatError;
                return response.ToBytes();
            }
            if (query.Header.OpCode != 0 || query.Questions.Count != 1 || query.Header.Truncated || query.Answers.Count != 0 || query.Authorities.Count != 0 ||
                query.Additionals.Any(r => (ushort)r.Type == 250 || (ushort)r.Type == 24))
            {
                response.Header.ResponseCode = query.Header.OpCode != 0 ? DnsResponseCode.NotImplemented : DnsResponseCode.FormatError;
                return response.ToBytes();
            }

            lock (_filterLock)
            {
                var rewrite = _rewrites.Where(r => !_rewriteOverrides.ContainsKey(r.Domain))
                    .Concat(_rewriteOverrides.Values).Where(r => r.Enabled && MatchesDomain(question.Name, r.Domain))
                    .OrderByDescending(r => r.Domain.Length).FirstOrDefault();

                if (question.Class == 1 && rewrite != null && IPAddress.TryParse(rewrite.Answer, out var ip))
                {
                    var recordType = ip.AddressFamily == AddressFamily.InterNetwork ? DnsRecordType.A : DnsRecordType.AAAA;

                    if (question.Type != recordType) return response.ToBytes();
                    response.Answers.Add(new DnsResourceRecord
                    {
                        Name = question.Name,
                        Type = recordType,
                        Class = 1,
                        TTL = 300,
                        Data = ip.GetAddressBytes()
                    });
                    response.Header.AnswerCount = 1;
                    return response.ToBytes();
                }

                if (question.Class == 1 && !IsAllowed(question.Name) && IsBlocked(question.Name))
                {
                    Interlocked.Increment(ref _blockedQueries);
                    LogQuery(clientIp, question.Name, question.Type, question.Type == DnsRecordType.A || question.Type == DnsRecordType.AAAA ? DnsResponseCode.NoError : DnsResponseCode.NXDomain, true, timer.ElapsedMilliseconds, "");
                    if (question.Type == DnsRecordType.A)
                    {
                        response.Answers.Add(new DnsResourceRecord
                        {
                            Name = question.Name,
                            Type = DnsRecordType.A,
                            Class = 1,
                            TTL = 60,
                            Data = new byte[] { 0, 0, 0, 0 }
                        });
                        response.Header.AnswerCount = 1;
                    }
                    else if (question.Type == DnsRecordType.AAAA)
                    {
                        response.Answers.Add(new DnsResourceRecord
                        {
                            Name = question.Name,
                            Type = DnsRecordType.AAAA,
                            Class = 1,
                            TTL = 60,
                            Data = new byte[16]
                        });
                        response.Header.AnswerCount = 1;
                    }
                    else
                    {
                        response.Header.ResponseCode = DnsResponseCode.NXDomain;
                    }
                    return response.ToBytes();
                }
            }

            // Cache only ordinary queries without EDNS options or other transaction-specific records.
            var opt = query.Additionals.Count == 1 && (ushort)query.Additionals[0].Type == 41 ? query.Additionals[0] : null;
            bool cacheable = _config.EnableCache && (query.Additionals.Count == 0 || opt != null && opt.Data.Length == 0 && (opt.TTL >> 16) == 0);
            string key = cacheable ? Convert.ToBase64String(query.WirePacket, 2, query.WirePacket.Length - 2) : null;
            var now = DateTime.UtcNow;
            if (cacheable && _cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            {
                Interlocked.Increment(ref _cachedQueries);
                var packet = entry.Response.CopyWire(query.Header.Id);
                uint elapsed = (uint)Math.Max(0, (System.Diagnostics.Stopwatch.GetTimestamp() - entry.StoredTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency);
                foreach (var rr in CacheRecords(entry.Response))
                {
                    uint ttl = rr.TTL > elapsed ? rr.TTL - elapsed : 0;
                    int offset = rr.TtlOffset;
                    packet[offset] = (byte)(ttl >> 24);
                    packet[offset + 1] = (byte)(ttl >> 16);
                    packet[offset + 2] = (byte)(ttl >> 8);
                    packet[offset + 3] = (byte)ttl;
                }
                LogQuery(clientIp, question.Name, question.Type, DnsResponseCode.NoError, false, timer.ElapsedMilliseconds, "cache");
                return packet;
            }

            Interlocked.Increment(ref _upstreamQueries);
            var upstream = await QueryUpstream(query).ConfigureAwait(false);
            if (upstream == null)
            {
                Interlocked.Increment(ref _errors);
                response.Header.ResponseCode = DnsResponseCode.ServerFailure;
                LogQuery(clientIp, question.Name, question.Type, DnsResponseCode.ServerFailure, false, timer.ElapsedMilliseconds, "");
                return response.ToBytes();
            }
            if (cacheable && !upstream.Header.Truncated && upstream.Header.ResponseCode == DnsResponseCode.NoError &&
                upstream.Answers.Any(r => (ushort)r.Type != 41) &&
                upstream.Additionals.All(r => (ushort)r.Type != 41 || r.Data.Length == 0 && (r.TTL >> 16) == 0))
            {
                uint ttl = Math.Min((uint)Math.Max(0, _config.CacheTtlSeconds), CacheRecords(upstream).Min(r => r.TTL));
                if (ttl > 0)
                {
                    now = DateTime.UtcNow;
                    _cache[key] = new CacheEntry { Response = upstream, CreatedAt = now, ExpiresAt = now.AddSeconds(ttl), StoredTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
                    if (_cache.Count > _config.CacheSize)
                    {
                        var oldest = _cache.OrderBy(kv => kv.Value.CreatedAt).FirstOrDefault();
                        if (oldest.Key != null) _cache.TryRemove(oldest.Key, out _);
                    }
                }
            }
            LogQuery(clientIp, question.Name, question.Type, upstream.Header.ResponseCode, false, timer.ElapsedMilliseconds, "");
            return upstream.CopyWire(query.Header.Id);
        }

        private static IEnumerable<DnsResourceRecord> CacheRecords(DnsMessage response) =>
            response.Answers.Concat(response.Authorities).Concat(response.Additionals).Where(r => (ushort)r.Type != 41);

        private async Task<DnsMessage> QueryUpstream(DnsMessage query)
        {
            var endpoints = (_config.UpstreamServers ?? new List<string>()).ToArray().Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim());
            if (_config.PreferSecureUpstream) endpoints = endpoints.OrderByDescending(s =>
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("tls://", StringComparison.OrdinalIgnoreCase));
            foreach (string endpoint in endpoints)
            {
                if (_cts.IsCancellationRequested) break;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    deadline.CancelAfter(Math.Max(1, _config.UpstreamTimeoutMs));
                    try
                    {
                        var randomId = new byte[2];
                        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(randomId);
                        ushort id = (ushort)((randomId[0] << 8) | randomId[1]);
                        bool tls = endpoint.StartsWith("tls://", StringComparison.OrdinalIgnoreCase);
                        bool tcp = endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
                        bool doh = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                        if (doh) return ValidateReply(await ExchangeDoh(endpoint, query.CopyWire(0), deadline.Token).ConfigureAwait(false), query, 0);
                        string address = tls || tcp ? endpoint.Substring(6) : endpoint;
                        if (!TryParseEndpoint(address, tls ? 853 : 53, out string host, out int port)) continue;
                        byte[] outbound = query.CopyWire(id);
                        if (tcp || tls)
                            return ValidateReply(await ExchangeTcp(host, port, tls, outbound, deadline.Token).ConfigureAwait(false), query, id);
                        if (!IPAddress.TryParse(host, out var ip)) continue;
                        var response = await ExchangeUdp(ip, port, outbound, query, id, deadline.Token).ConfigureAwait(false);
                        if (!response.Header.Truncated) return response;
                        // A UDP reply with TC set is only a hint to retry over TCP.
                        return ValidateReply(await ExchangeTcp(host, port, false, outbound, deadline.Token).ConfigureAwait(false), query, id);
                    }
                    catch (Exception ex)
                    {
                        if (!_cts.IsCancellationRequested) _log($"[DnsServer] Upstream DNS {endpoint}: {ex.Message}");
                    }
                }
            }
            return null;
        }

        private static bool TryParseEndpoint(string address, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            if (IPAddress.TryParse(address, out var ip) && !address.StartsWith("[", StringComparison.Ordinal))
            {
                host = ip.ToString();
                return true;
            }
            if (!Uri.TryCreate("dns://" + address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Length == 0) return false;
            host = uri.DnsSafeHost.Trim('[', ']');
            port = uri.IsDefaultPort ? defaultPort : uri.Port;
            return port > 0 && port <= 65535;
        }

        private static DnsMessage ValidateReply(byte[] data, DnsMessage query, ushort id)
        {
            var response = DnsMessage.FromBytes(data);
            var question = query.Questions[0];
            if (!response.Header.IsResponse || response.Header.Id != id || response.Header.OpCode != query.Header.OpCode ||
                response.Questions.Count != 1 || !response.Questions[0].Name.Equals(question.Name, StringComparison.OrdinalIgnoreCase) ||
                response.Questions[0].Type != question.Type || response.Questions[0].Class != question.Class ||
                response.Additionals.Any(r => (ushort)r.Type == 250 || (ushort)r.Type == 24))
                throw new InvalidDataException("Unmatched upstream reply");
            return response;
        }

        private async Task<DnsMessage> ExchangeUdp(IPAddress ip, int port, byte[] data, DnsMessage query, ushort id, CancellationToken token)
        {
            using (var udp = new UdpClient(ip.AddressFamily))
            using (token.Register(udp.Close))
            {
                token.ThrowIfCancellationRequested();
                _upstreamNetwork?.ProtectSocket(udp.Client);
                udp.Connect(ip, port);
                await udp.SendAsync(data, data.Length).ConfigureAwait(false);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var packet = await udp.ReceiveAsync().ConfigureAwait(false);
                    try { return ValidateReply(packet.Buffer, query, id); }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { }
                }
            }
        }

        private async Task<byte[]> ExchangeTcp(string host, int port, bool tls, byte[] data, CancellationToken token)
        {
            using (var tcp = await ConnectUpstreamTcp(host, port, token).ConfigureAwait(false))
            using (token.Register(tcp.Close))
            using (var network = tcp.GetStream())
            {
                if (!tls) return await ExchangeFrame(network, data, token).ConfigureAwait(false);
                using (var ssl = new System.Net.Security.SslStream(network, false))
                {
                    // Preserve the configured hostname for SNI and normal certificate validation.
                    await ssl.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                    return await ExchangeFrame(ssl, data, token).ConfigureAwait(false);
                }
            }
        }

        private async Task<TcpClient> ConnectUpstreamTcp(string host, int port, CancellationToken token)
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } :
                await AwaitCancellation(_upstreamNetwork != null ? _upstreamNetwork.ResolveHostAsync(host, token) :
                    System.Net.Dns.GetHostAddressesAsync(host), token).ConfigureAwait(false);
            Exception lastError = null;
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();
                var tcp = new TcpClient(address.AddressFamily);
                try
                {
                    using (token.Register(tcp.Close))
                    {
                        _upstreamNetwork?.ProtectSocket(tcp.Client);
                        await tcp.ConnectAsync(address, port).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        return tcp;
                    }
                }
                catch (Exception ex)
                {
                    tcp.Close();
                    token.ThrowIfCancellationRequested();
                    lastError = ex;
                }
            }
            throw new IOException("No reachable upstream address for " + host, lastError);
        }

        private static async Task<T> AwaitCancellation<T>(Task<T> operation, CancellationToken token)
        {
            var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => expired.TrySetResult(true)))
            {
                if (await Task.WhenAny(operation, expired.Task).ConfigureAwait(false) != operation)
                {
                    _ = operation.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new OperationCanceledException(token);
                }
                token.ThrowIfCancellationRequested();
                return await operation.ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> ExchangeFrame(Stream stream, byte[] data, CancellationToken token)
        {
            var length = new byte[] { (byte)(data.Length >> 8), (byte)data.Length };
            await stream.WriteAsync(length, 0, 2, token).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
            return await ReadFrame(stream, token).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
        {
            var prefix = new byte[2];
            await ReadExact(stream, prefix, token).ConfigureAwait(false);
            int length = (prefix[0] << 8) | prefix[1];
            if (length < 12) throw new InvalidDataException("Invalid DNS frame length");
            var data = new byte[length];
            await ReadExact(stream, data, token).ConfigureAwait(false);
            return data;
        }

        private static async Task ReadExact(Stream stream, byte[] data, CancellationToken token)
        {
            for (int offset = 0; offset < data.Length; )
            {
                int count = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }

        private Task<byte[]> ExchangeDoh(string endpoint, byte[] data, CancellationToken token) =>
            SendHttpAsync(new Uri(endpoint), data, "application/dns-message", 65535, token);

        private async Task<byte[]> SendHttpAsync(Uri uri, byte[] body, string mediaType, int maxBytes, CancellationToken token)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Unsupported upstream URL scheme");
            if (_upstreamNetwork != null)
            {
                // Never retry through the default HttpClient after an explicit-network failure.
                var bytes = await AwaitCancellation(_upstreamNetwork.SendHttpAsync(uri, body, mediaType, maxBytes, token), token).ConfigureAwait(false);
                if (bytes.Length > maxBytes) throw new InvalidDataException("Upstream HTTP response too large");
                return bytes;
            }
            using (var handler = new HttpClientHandler { AllowAutoRedirect = body == null })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri))
            {
                if (body != null)
                {
                    request.Headers.Accept.ParseAdd(mediaType);
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
                }
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (mediaType != null && !string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unexpected DoH response content type");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                        {
                            if (output.Length + count > maxBytes) throw new InvalidDataException("Upstream HTTP response too large");
                            output.Write(buffer, 0, count);
                        }
                        return output.ToArray();
                    }
                }
            }
        }

        private void LogQuery(string clientIp, string domain, DnsRecordType type, DnsResponseCode rc, bool blocked, long durationMs, string upstream)
        {
            if (!_config.LogQueries) return;

            var log = new DnsQueryLog
            {
                Timestamp = DateTime.Now,
                ClientIp = clientIp,
                Domain = domain,
                Type = type,
                ResponseCode = rc,
                Blocked = blocked,
                DurationMs = durationMs,
                UpstreamServer = upstream
            };

            _queryLog.Enqueue(log);
            while (_queryLog.Count > _config.MaxLogEntries && _queryLog.TryDequeue(out _)) { }
        }

        private async Task CleanupLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    var now = DateTime.UtcNow;
                    var expiredKeys = _cache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
                    foreach (var key in expiredKeys) _cache.TryRemove(key, out _);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log($"[DnsServer] 清理任务错误: {ex.Message}");
                }
            }
        }

        private void UpdateStats() { }

        public void Dispose()
        {
            _cts.Cancel();
            _statsTimer?.Dispose();

            try { _udpServer?.Close(); } catch { }
            try { _tcpServer?.Stop(); } catch { }

            Task.WaitAll(new[] { _udpTask, _tcpTask, _cleanupTask }.Where(t => t != null).ToArray(), TimeSpan.FromSeconds(5));

            _cache.Clear();
        }
    }
}
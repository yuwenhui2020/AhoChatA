using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AhoChatA
{
    /* ===================== QUIC varint（RFC 9000 §16） ===================== */

    /// <summary>
    /// QUIC 可变长整数：前 2 位是长度前缀（00=1 字节 / 01=2 / 10=4 / 11=8），
    /// 传输参数、帧类型、长度字段全都用它。
    /// </summary>
    public static class QuicVarint
    {
        public static int Length(ulong v)
        {
            if (v <= 0x3FUL) return 1;
            if (v <= 0x3FFFUL) return 2;
            if (v <= 0x3FFFFFFFUL) return 4;
            return 8;
        }

        public static byte[] Encode(ulong v)
        {
            var b = new byte[Length(v)];
            int pos = 0;
            Write(b, ref pos, v);
            return b;
        }

        public static void Write(System.IO.Stream ms, ulong v)
        {
            var b = Encode(v);
            ms.Write(b, 0, b.Length);
        }

        public static void Write(byte[] buf, ref int pos, ulong v)
        {
            int len = Length(v);
            if (len == 1) { buf[pos] = (byte)v; pos += 1; return; }
            ulong tagged = v;
            if (len == 2) tagged = v | 0x4000UL;
            else if (len == 4) tagged = v | 0x80000000UL;
            else tagged = v | 0xC000000000000000UL;
            for (int i = len - 1; i >= 0; i--) { buf[pos + i] = (byte)(tagged & 0xFF); tagged >>= 8; }
            pos += len;
        }

        public static ulong Read(byte[] buf, ref int pos)
        {
            if (pos >= buf.Length) throw new FormatException(L10n.T("varint 越界"));
            int len = 1 << ((buf[pos] >> 6) & 3);
            if (pos + len > buf.Length) throw new FormatException(L10n.T("varint 越界"));
            ulong v = (ulong)(buf[pos] & 0x3F);
            for (int i = 1; i < len; i++) v = (v << 8) | buf[pos + i];
            pos += len;
            return v;
        }
    }

    /* ===================== 传输参数 ===================== */

    /// <summary>
    /// QUIC 传输参数（RFC 9000 §18）。客户端把自己的放在 ClientHello 的 0x0039 扩展里，
    /// 服务端的放在 EncryptedExtensions 的同一个扩展里。
    /// </summary>
    public sealed class QuicTransportParams
    {
        // 我方参数
        public ulong MaxIdleTimeout = 30000;             // ms，30s 无流量即空闲
        public ulong MaxUdpPayloadSize = 1452;
        public ulong InitialMaxData = 1UL << 20;         // 连接级 1MB
        public ulong InitialMaxStreamDataBidiLocal = 256 * 1024;
        public ulong InitialMaxStreamDataBidiRemote = 256 * 1024;
        public ulong InitialMaxStreamDataUni = 64 * 1024;
        public ulong InitialMaxStreamsBidi = 64;
        public ulong InitialMaxStreamsUni = 0;           // 不开单向流
        public ulong AckDelayExponent = 3;
        public ulong MaxAckDelay = 25;                   // 必须 &lt; 2^14
        public ulong ActiveConnectionIdLimit = 4;
        public byte[] InitialSourceConnectionId;         // 可缺省

        // 对端参数（从 EncryptedExtensions 解出）
        public ulong PeerMaxIdleTimeout;
        public ulong PeerMaxUdpPayloadSize;
        public ulong PeerInitialMaxData;
        public ulong PeerInitialMaxStreamDataBidiLocal;
        public ulong PeerInitialMaxStreamDataBidiRemote;
        public ulong PeerInitialMaxStreamDataUni;
        public ulong PeerInitialMaxStreamsBidi;
        public ulong PeerInitialMaxStreamsUni;
        public ulong PeerAckDelayExponent = 3;
        public ulong PeerMaxAckDelay = 25;
        public ulong PeerActiveConnectionIdLimit = 4;
        public bool PeerDisableActiveMigration;

        private static void Put(MemoryStream ms, ulong id, ulong value)
        {
            var v = QuicVarint.Encode(value);
            var head = QuicVarint.Encode(id);
            ms.Write(head, 0, head.Length);
            var len = QuicVarint.Encode((ulong)v.Length);
            ms.Write(len, 0, len.Length);
            ms.Write(v, 0, v.Length);
        }

        private static void PutRaw(MemoryStream ms, ulong id, byte[] value)
        {
            var head = QuicVarint.Encode(id);
            ms.Write(head, 0, head.Length);
            var len = QuicVarint.Encode((ulong)value.Length);
            ms.Write(len, 0, len.Length);
            ms.Write(value, 0, value.Length);
        }

        public byte[] Encode()
        {
            var ms = new MemoryStream();
            Put(ms, 0x0001, MaxIdleTimeout);
            Put(ms, 0x0003, MaxUdpPayloadSize);
            Put(ms, 0x0004, InitialMaxData);
            Put(ms, 0x0005, InitialMaxStreamDataBidiLocal);
            Put(ms, 0x0006, InitialMaxStreamDataBidiRemote);
            Put(ms, 0x0007, InitialMaxStreamDataUni);
            Put(ms, 0x0008, InitialMaxStreamsBidi);
            Put(ms, 0x0009, InitialMaxStreamsUni);
            Put(ms, 0x000a, AckDelayExponent);
            Put(ms, 0x000b, MaxAckDelay);
            Put(ms, 0x000e, ActiveConnectionIdLimit);
            if (InitialSourceConnectionId != null && InitialSourceConnectionId.Length > 0)
                PutRaw(ms, 0x000f, InitialSourceConnectionId);
            return ms.ToArray();
        }

        /// <summary>解析对端传输参数；发现非法参数时抛出。</summary>
        public static QuicTransportParams Decode(byte[] data)
        {
            var tp = new QuicTransportParams();
            int pos = 0;
            while (pos < data.Length)
            {
                ulong id = QuicVarint.Read(data, ref pos);
                ulong len = QuicVarint.Read(data, ref pos);
                if (len > (ulong)(data.Length - pos)) throw new FormatException(L10n.T("传输参数长度越界"));
                var value = new byte[len];
                Buffer.BlockCopy(data, pos, value, 0, (int)len);
                pos += (int)len;

                switch (id)
                {
                    case 0x0000: break;   // original_destination_connection_id
                    case 0x0001: tp.PeerMaxIdleTimeout = V(value); break;
                    case 0x0002: break;   // stateless_reset_token
                    case 0x0003: tp.PeerMaxUdpPayloadSize = V(value); break;
                    case 0x0004: tp.PeerInitialMaxData = V(value); break;
                    case 0x0005: tp.PeerInitialMaxStreamDataBidiLocal = V(value); break;
                    case 0x0006: tp.PeerInitialMaxStreamDataBidiRemote = V(value); break;
                    case 0x0007: tp.PeerInitialMaxStreamDataUni = V(value); break;
                    case 0x0008: tp.PeerInitialMaxStreamsBidi = V(value); break;
                    case 0x0009: tp.PeerInitialMaxStreamsUni = V(value); break;
                    case 0x000a: tp.PeerAckDelayExponent = V(value); break;
                    case 0x000b: tp.PeerMaxAckDelay = V(value); break;
                    case 0x000c: tp.PeerDisableActiveMigration = true; break;
                    case 0x000e: tp.PeerActiveConnectionIdLimit = V(value); break;
                    default: break;       // 未知参数忽略（QUIC 要求）
                }
            }
            if (tp.PeerMaxUdpPayloadSize > 0 && tp.PeerMaxUdpPayloadSize < 1200)
                throw new FormatException(L10n.T("对端 max_udp_payload_size 小于 1200"));
            if (tp.PeerAckDelayExponent > 20) throw new FormatException(L10n.T("ack_delay_exponent 非法"));
            if (tp.PeerMaxAckDelay >= (1UL << 14)) throw new FormatException(L10n.T("max_ack_delay 非法"));
            return tp;
        }

        private static ulong V(byte[] v)
        {
            int p = 0;
            return QuicVarint.Read(v, ref p);
        }
    }

    /* ===================== 加密级别密钥 ===================== */

    /// <summary>一个加密级别（Initial / Handshake / 1-RTT）的密钥材料。</summary>
    public sealed class TlsKeys
    {
        public byte[] Key;     // AEAD 密钥
        public byte[] Iv;      // 12 字节
        public byte[] Hp;      // 头保护密钥
        public ulong Seq;      // 本方向已用序号

        /// <summary>
        /// 本套密钥用 AES-128-GCM 还是 ChaCha20-Poly1305。
        /// 注意：QUIC Initial 级别永远是 AES-128-GCM，与协商套件无关（RFC 9001 §5.2），
        /// 所以必须按密钥而不是按全局套件判断。
        /// </summary>
        public bool Aes;

        public TlsKeys() { }
        public TlsKeys(byte[] key, byte[] iv, byte[] hp, bool aes) { Key = key; Iv = iv; Hp = hp; Aes = aes; }

        /// <summary>nonce = IV XOR 序号（序号按大端左补到 12 字节，RFC 9001 §5.3）。</summary>
        public byte[] Nonce()
        {
            return NonceFor(Seq);
        }

        /// <summary>按指定包号算 nonce（收包时对端的包号由我们读取得到）。</summary>
        public byte[] NonceFor(ulong pn)
        {
            var n = new byte[12];
            Buffer.BlockCopy(Iv, 0, n, 0, 12);
            for (int i = 0; i < 8; i++) n[11 - i] ^= (byte)(pn >> (8 * i));
            return n;
        }
    }

    /* ===================== TLS 1.3 客户端 ===================== */

    /// <summary>
    /// TLS 1.3 客户端（QUIC 变体：没有记录层，握手消息直接由 QUIC 的 CRYPTO 帧承载）。
    ///
    /// 只实现 libp2p 需要的子集：
    ///   · 密钥交换 X25519
    ///   · 密码套件 TLS_AES_128_GCM_SHA256 / TLS_CHACHA20_POLY1305_SHA256
    ///   · 签名 ecdsa_secp256r1_sha256（libp2p 证书就是 P-256）
    ///   · ALPN "libp2p"
    ///   · QUIC 传输参数扩展 0x0039
    ///   · 双向证书（libp2p 规范要求客户端也出示证书）
    /// </summary>
    public sealed class Tls13Session
    {
        public const ushort TLS_AES_128_GCM_SHA256 = 0x1301;
        public const ushort TLS_CHACHA20_POLY1305_SHA256 = 0x1303;
        public const ushort SIG_ECDSA_SECP256R1_SHA256 = 0x0403;
        public const ushort GROUP_X25519 = 0x001d;
        public const ushort GROUP_SECP256R1 = 0x0017;
        public const ushort EXT_KEY_SHARE = 0x0033;
        public const ushort EXT_SUPPORTED_VERSIONS = 0x002b;
        public const ushort EXT_SIGNATURE_ALGORITHMS = 0x000d;
        public const ushort EXT_SUPPORTED_GROUPS = 0x000a;
        public const ushort EXT_ALPN = 0x0010;
        public const ushort EXT_QUIC_TRANSPORT = 0x0039;
        public const ushort EXT_PSK_MODES = 0x002d;

        private const byte HS_CLIENT_HELLO = 1;
        private const byte HS_SERVER_HELLO = 2;
        private const byte HS_NEW_SESSION_TICKET = 4;
        private const byte HS_ENCRYPTED_EXTENSIONS = 8;
        private const byte HS_CERTIFICATE = 11;
        private const byte HS_CERTIFICATE_REQUEST = 13;
        private const byte HS_CERTIFICATE_VERIFY = 15;
        private const byte HS_FINISHED = 20;

        private readonly Libp2pCert _myCert;
        private readonly MemoryStream _transcript = new MemoryStream();
        private byte[] _x25519Priv;
        private byte[] _x25519Pub;
        private byte[] _serverKeyShare;
        private ushort _cipherSuite;
        private bool _serverHelloSeen;
        private bool _serverFinishedSeen;
        private bool _clientFlightSent;
        private bool _certRequested;

        // 缓存：CH||SH 与 CH..serverFinished 在 transcript 中的结束位置
        private int _shEnd = -1;
        private int _finEnd = -1;

        // 密钥调度缓存
        private byte[] _hsSecret, _cHsTraffic, _sHsTraffic, _cAppTraffic, _sAppTraffic;

        // 排障用：密钥调度中间值（仅在 EnsureHandshakeSecrets 内被填充）
        public byte[] DebugEcdhe { get; private set; }
        public byte[] DebugHsSecret { get { return _hsSecret; } }
        public byte[] DebugThChSh { get; private set; }
        public byte[] DebugTranscript { get { return _transcript == null ? null : _transcript.ToArray(); } }
        public byte[] DebugServerKeyShare { get { return _serverKeyShare; } }
        public byte[] DebugClientHelloMsg { get; private set; }

        /// <summary>协商出的对端证书（已校验自签名 + 主机密钥所有权）。</summary>
        public Libp2pCert PeerCert;
        public QuicTransportParams PeerTransport;
        public string Alpn;
        public string LastCertError;

        /// <summary>
        /// 仅测试用宽松模式：允许对端不发 QUIC 传输参数、允许非 libp2p 证书。
        /// 用于走 TLS 记录层与 OpenSSL 对照跑密钥调度；生产（QUIC）路径恒为 false。
        /// </summary>
        public bool LenientTestMode;

        /// <summary>仅排障用：即使收到 CertificateRequest 也不发客户端证书（用于定位是 CV 还是 Finished 被拒）。</summary>
        public bool SkipClientCertForTest;

        private bool RequireTransportParams { get { return !LenientTestMode; } }

        public TlsKeys ClientHandshake, ServerHandshake;
        public TlsKeys ClientApp, ServerApp;

        /// <summary>协商出的套件是不是 AES-128-GCM（Initial 永远是 AES）。</summary>
        public bool CipherIsAes { get { return _cipherSuite == TLS_AES_128_GCM_SHA256; } }

        /// <summary>协商出的套件名（握手前为 null）。</summary>
        public string CipherSuiteName
        {
            get
            {
                switch (_cipherSuite)
                {
                    case TLS_AES_128_GCM_SHA256: return "TLS_AES_128_GCM_SHA256";
                    case TLS_CHACHA20_POLY1305_SHA256: return "TLS_CHACHA20_POLY1305_SHA256";
                    default: return null;
                }
            }
        }

        /// <summary>客户端选择的 x25519 临时公钥（供日志/调试）。</summary>
        public byte[] ClientX25519Public { get { return _x25519Pub; } }

        /// <summary>客户端 x25519 临时私钥（仅测试排障用）。</summary>
        public byte[] ClientX25519Private { get { return _x25519Priv; } }

        /// <summary>客户端 CRYPTO 流里，Certificate/Flight 之前的字节数（= ClientHello 长度）。</summary>
        public ulong ClientCryptoOffset { get { return (ulong)_clientHello.Length; } }

        private byte[] _clientHello;

        public Tls13Session(Libp2pCert myCert) { _myCert = myCert; }

        private int KeyLen { get { return (_cipherSuite == TLS_AES_128_GCM_SHA256) ? 16 : 32; } }
        private bool UseAes { get { return _cipherSuite == TLS_AES_128_GCM_SHA256; } }

        /* ===================== 密钥调度 ===================== */

        private static byte[] ExpandLabel(byte[] secret, string label, byte[] context, int length)
        {
            var full = Encoding.ASCII.GetBytes("tls13 " + label);
            var buf = new byte[2 + 1 + full.Length + 1 + context.Length];
            buf[0] = (byte)(length >> 8);
            buf[1] = (byte)length;
            buf[2] = (byte)full.Length;
            Buffer.BlockCopy(full, 0, buf, 3, full.Length);
            buf[3 + full.Length] = (byte)context.Length;
            Buffer.BlockCopy(context, 0, buf, 4 + full.Length, context.Length);
            return Hkdf.Expand(secret, buf, length);
        }

        /// <summary>HKDF-Expand-Label 的公开版（QUIC 的 Initial 密钥派生要用）。</summary>
        public static byte[] ExpandLabel(byte[] secret, string label, byte[] context, int length, bool forQuic)
        {
            return ExpandLabel(secret, label, context, length);
        }

        /// <summary>从 TLS traffic secret 派生 QUIC 的 key/iv/hp（标签 "quic key"/"quic iv"/"quic hp"）。</summary>
        public static TlsKeys DeriveQuicKeys(byte[] secret, int keyLen)
        {
            var key = ExpandLabel(secret, "quic key", new byte[0], keyLen);
            var iv = ExpandLabel(secret, "quic iv", new byte[0], 12);
            var hp = ExpandLabel(secret, "quic hp", new byte[0], keyLen);
            // Aes 由调用方设置：MakeKeys 按协商套件，DeriveInitialKeys 恒 true
            return new TlsKeys(key, iv, hp, false);
        }

        /// <summary>Derive-Secret(Secret, Label, Messages) = Expand-Label(Secret, Label, TH(Messages), 32)。</summary>
        private byte[] DeriveSecret(byte[] secret, string label, byte[] messages)
        {
            byte[] th;
            using (var sha = SHA256.Create()) th = sha.ComputeHash(messages ?? new byte[0]);
            return ExpandLabel(secret, label, th, 32);
        }

        /// <summary>
        /// 同上，但入参**已经是 Transcript-Hash**。
        /// 注意别把 TranscriptUpTo() 的返回值再传给 DeriveSecret —— 那样会哈希两次。
        /// </summary>
        private static byte[] DeriveSecretHash(byte[] secret, string label, byte[] th)
        {
            return ExpandLabel(secret, label, th ?? new byte[0], 32);
        }

        private byte[] TranscriptHash()
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(_transcript.ToArray());
        }

        private void TranscriptAppend(byte[] msg)
        {
            _transcript.Write(msg, 0, msg.Length);
        }

        private byte[] TranscriptUpTo(int end)
        {
            var buf = _transcript.ToArray();
            var part = new byte[end];
            Buffer.BlockCopy(buf, 0, part, 0, end);
            using (var sha = SHA256.Create()) return sha.ComputeHash(part);
        }

        private TlsKeys MakeKeys(byte[] secret)
        {
            var k = DeriveQuicKeys(secret, KeyLen);
            k.Aes = UseAes;
            return k;
        }

        /// <summary>
        /// 无 PSK 时的 early secret：HKDF-Extract(salt=0, IKM=0)。
        /// 注意 IKM 必须是 Hash.length(32) 个零字节，不能传空数组——
        /// HMAC(salt, "") 与 HMAC(salt, 32×0x00) 结果完全不同（RFC 8446 §7.1）。
        /// </summary>
        private static byte[] EarlySecret()
        {
            var zeros = new byte[32];
            return Hkdf.Extract(zeros, zeros);
        }

        /// <summary>算出握手级 secret（依赖 ServerHello 的 key_share）。</summary>
        private void EnsureHandshakeSecrets()
        {
            if (_hsSecret != null) return;
            var zero = new byte[0];
            var early = EarlySecret();
            var derived = DeriveSecret(early, "derived", zero);
            var ecdhe = Curve25519.X25519SharedSecret(_x25519Priv, _serverKeyShare);
            _hsSecret = Hkdf.Extract(derived, ecdhe);
            var thCH_SH = TranscriptUpTo(_shEnd);
            DebugEcdhe = ecdhe;
            DebugThChSh = thCH_SH;
            _cHsTraffic = DeriveSecretHash(_hsSecret, "c hs traffic", thCH_SH);
            _sHsTraffic = DeriveSecretHash(_hsSecret, "s hs traffic", thCH_SH);
        }

        /// <summary>各方向 traffic secret（QUIC 派生用；也供测试按 TLS 标签派生记录密钥）。</summary>
        public byte[] ServerHandshakeSecret { get { return _sHsTraffic; } }
        public byte[] ClientHandshakeSecret { get { return _cHsTraffic; } }
        public byte[] ServerAppSecret { get { return _sAppTraffic; } }
        public byte[] ClientAppSecret { get { return _cAppTraffic; } }

        /// <summary>
        /// 按普通 TLS 1.3 标签（"key"/"iv"）从 traffic secret 派生记录层密钥。
        /// 仅测试/排障用——QUIC 走 DeriveQuicKeys 的 "quic key"/"quic iv"/"quic hp"。
        /// </summary>
        public static TlsKeys TlsRecordKeys(byte[] secret, int keyLen)
        {
            var key = ExpandLabel(secret, "key", new byte[0], keyLen);
            var iv = ExpandLabel(secret, "iv", new byte[0], 12);
            return new TlsKeys(key, iv, key, true);
        }

        private void ComputeAppSecrets()
        {
            var zero = new byte[0];
            var early = EarlySecret();
            var d1 = DeriveSecret(early, "derived", zero);
            var ecdhe = Curve25519.X25519SharedSecret(_x25519Priv, _serverKeyShare);
            var hs = Hkdf.Extract(d1, ecdhe);
            var d2 = DeriveSecret(hs, "derived", zero);
            // master_secret = HKDF-Extract(Derive-Secret(hs,"derived",""), 0*32)
            // 同样注意 IKM 是 32 个零字节而非空数组（RFC 8446 §7.1）。
            var master = Hkdf.Extract(d2, new byte[32]);
            var thAll = TranscriptUpTo(_finEnd);
            _cAppTraffic = DeriveSecretHash(master, "c ap traffic", thAll);
            _sAppTraffic = DeriveSecretHash(master, "s ap traffic", thAll);
            ClientApp = MakeKeys(_cAppTraffic);
            ServerApp = MakeKeys(_sAppTraffic);
        }

        /* ===================== ClientHello ===================== */

        /// <summary>
        /// 构造 ClientHello。transportParams 是我方 QUIC 传输参数的 DER。
        /// 返回的字节就是一条完整的握手消息（含 4 字节类型+长度头）。
        /// </summary>
        public byte[] BuildClientHello(byte[] transportParams)
        {
            _x25519Priv = Curve25519.RandomSeed();
            _x25519Pub = Curve25519.X25519PublicKey(_x25519Priv);
            var xPub = _x25519Pub;
            var random = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);

            var ext = new MemoryStream();
            {
                // supported_versions 的列表长度前缀是 1 字节（RFC 8446 §4.2.1 的例外），
                // 不能用 ExtData 的 2 字节长度，否则 rustls 报 decode_error(50)。
                var sv = new MemoryStream();
                sv.WriteByte(0x02);                     // 列表字节数 = 一个 u16 版本
                WriteU16(sv, 0x0304);                   // TLS 1.3
                WriteExt(ext, EXT_SUPPORTED_VERSIONS, sv.ToArray());
            }
            WriteExt(ext, EXT_SIGNATURE_ALGORITHMS, ExtData(SIG_ECDSA_SECP256R1_SHA256));
            WriteExt(ext, EXT_SUPPORTED_GROUPS, ExtData(GROUP_X25519, GROUP_SECP256R1));
            {
                // key_share：extension_data = client_shares<0..2^16-1>（2 字节总长）
                // + 若干 KeyShareEntry{group u16, key_exchange opaque<1..2^16-1>}。
                // 少了外层 2 字节总长，服务端会把密钥数据当成下一条 entry 解析 → decode_error。
                var entry = new MemoryStream();
                WriteU16(entry, GROUP_X25519);
                WriteVec16(entry, xPub);
                var inner = new MemoryStream();
                WriteU16(inner, (ushort)entry.Length);
                var entryBytes = entry.ToArray();
                inner.Write(entryBytes, 0, entryBytes.Length);
                WriteExt(ext, EXT_KEY_SHARE, inner.ToArray());
            }
            {
                var proto = Encoding.ASCII.GetBytes("libp2p");
                var name = new MemoryStream();
                name.WriteByte((byte)proto.Length);       // 协议名 1 字节长度前缀
                name.Write(proto, 0, proto.Length);
                var names = name.ToArray();
                var outer = new MemoryStream();
                WriteU16(outer, (ushort)names.Length);
                outer.Write(names, 0, names.Length);
                WriteExt(ext, EXT_ALPN, outer.ToArray());
            }
            WriteExt(ext, EXT_PSK_MODES, new byte[] { 0x01, 0x00 });   // psk_dhe_ke
            WriteExt(ext, EXT_QUIC_TRANSPORT, transportParams);

            var body = new MemoryStream();
            body.Write(new byte[] { 0x03, 0x03 }, 0, 2);        // legacy_version
            body.Write(random, 0, 32);                          // random
            body.WriteByte(0x00);                               // legacy_session_id（QUIC 必须为空）
            var suites = new ushort[] { TLS_AES_128_GCM_SHA256, TLS_CHACHA20_POLY1305_SHA256 };
            WriteU16(body, (ushort)(suites.Length * 2));
            foreach (var s in suites) WriteU16(body, s);
            body.Write(new byte[] { 0x01, 0x00 }, 0, 2);        // legacy_compression_methods: [null]
            var extBytes = ext.ToArray();
            WriteU16(body, (ushort)extBytes.Length);
            body.Write(extBytes, 0, extBytes.Length);

            var msg = WrapHandshake(HS_CLIENT_HELLO, body.ToArray());
            _clientHello = msg;
            TranscriptAppend(msg);
            return msg;
        }

        /// <summary>重发与首次完全相同的 ClientHello（Retry 后必须逐字节一致）。</summary>
        public byte[] ClientHelloAgain()
        {
            if (_clientHello == null) throw new InvalidOperationException("尚未构造 ClientHello");
            return _clientHello;
        }

        private static byte[] WrapHandshake(byte type, byte[] body)
        {
            var r = new byte[4 + body.Length];
            r[0] = type;
            r[1] = (byte)(body.Length >> 16);
            r[2] = (byte)(body.Length >> 8);
            r[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, r, 4, body.Length);
            return r;
        }

        private static void WriteU16(MemoryStream ms, ushort v)
        {
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }

        private static void WriteU24(MemoryStream ms, uint v)
        {
            ms.WriteByte((byte)(v >> 16));
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }

        private static void WriteVec16(MemoryStream ms, byte[] data)
        {
            WriteU16(ms, (ushort)data.Length);
            ms.Write(data, 0, data.Length);
        }

        private static byte[] ExtData(params ushort[] values)
        {
            var ms = new MemoryStream();
            WriteU16(ms, (ushort)(values.Length * 2));
            foreach (var v in values) WriteU16(ms, v);
            return ms.ToArray();
        }

        private static void WriteExt(MemoryStream outMs, ushort type, byte[] data)
        {
            WriteU16(outMs, type);
            WriteU16(outMs, (ushort)data.Length);
            outMs.Write(data, 0, data.Length);
        }

        /* ===================== 握手消息消费 ===================== */

        /// <summary>
        /// 喂入一条服务端握手消息（ServerHello / EncryptedExtensions / Certificate /
        /// CertificateVerify / Finished / NewSessionTicket），自动分派并维护 transcript。
        /// </summary>
        public void FeedHandshake(byte[] msg)
        {
            if (msg == null || msg.Length < 4) throw new FormatException("握手消息过短");
            byte type = msg[0];
            int len = (msg[1] << 16) | (msg[2] << 8) | msg[3];
            if (4 + len > msg.Length) throw new FormatException(L10n.T("握手消息长度不符"));
            var body = new byte[len];
            Buffer.BlockCopy(msg, 4, body, 0, len);

            switch (type)
            {
                case HS_SERVER_HELLO:
                    // 密钥调度要 TH(CH||SH)，所以先入 transcript 再处理；
                    // 注意 _shEnd 必须在 HandleServerHello 之前设好——它内部就会算握手密钥
                    TranscriptAppend(msg);
                    _shEnd = (int)_transcript.Length;
                    HandleServerHello(body);
                    break;

                case HS_ENCRYPTED_EXTENSIONS:
                    HandleEncryptedExtensions(body);
                    TranscriptAppend(msg);
                    break;

                case HS_CERTIFICATE:
                    HandleCertificate(body);
                    TranscriptAppend(msg);
                    break;

                case HS_CERTIFICATE_VERIFY:
                    // 验签内容 = TH(CH..Certificate)，不含本条
                    HandleCertificateVerify(body);
                    TranscriptAppend(msg);
                    break;

                case HS_CERTIFICATE_REQUEST:
                    // libp2p 必定要求客户端证书；OpenSSL 默认不要求（走 TLS 记录层测试时会遇到）。
                    // 记下这个状态，BuildClientFlight 据此决定是否发 Certificate/CertificateVerify。
                    _certRequested = true;
                    TranscriptAppend(msg);
                    break;

                case HS_FINISHED:
                    HandleFinished(body);      // 内部先验后入 transcript
                    break;

                case HS_NEW_SESSION_TICKET:
                    TranscriptAppend(msg);     // 不用，但仍计入 transcript
                    break;

                default:
                    throw new FormatException(L10n.T("意外的握手消息 ") + type);
            }
        }

        private static string HexDump(byte[] b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>extension_data 是否呈「1 字节名长 + 名字」且恰好占满（ServerHello ALPN 形状）。</summary>
        private static bool data0_1(byte[] ed, int el)
        {
            return ed[0] == el - 1;
        }

        private void HandleServerHello(byte[] body)
        {
            if (_serverHelloSeen) throw new FormatException(L10n.T("重复 ServerHello"));
            _serverHelloSeen = true;
            int p = 2 + 32;                            // legacy_version + random
            if (body.Length < p + 1) throw new FormatException(L10n.T("ServerHello 过短"));
            int sidLen = body[p]; p += 1 + sidLen;
            if (body.Length < p + 3) throw new FormatException(L10n.T("ServerHello 缺少套件"));
            _cipherSuite = (ushort)((body[p] << 8) | body[p + 1]);
            p += 3;                                    // + legacy_compression_method
            if (_cipherSuite != TLS_AES_128_GCM_SHA256 && _cipherSuite != TLS_CHACHA20_POLY1305_SHA256)
                throw new FormatException(L10n.T("服务端选了不支持的套件 0x") + _cipherSuite.ToString("x4"));

            if (p + 2 > body.Length) throw new FormatException(L10n.T("ServerHello 缺少扩展长度"));
            int extTotal = (body[p] << 8) | body[p + 1]; p += 2;
            int extEnd = p + extTotal;
            if (extEnd > body.Length) throw new FormatException(L10n.T("ServerHello 扩展越界"));
            bool sawVersion = false, sawKeyShare = false, sawAlpn = false;
            while (p + 4 <= extEnd)
            {
                ushort et = (ushort)((body[p] << 8) | body[p + 1]); p += 2;
                int el = (body[p] << 8) | body[p + 1]; p += 2;
                if (p + el > extEnd) throw new FormatException(L10n.T("ServerHello 扩展长度越界"));
                var ed = new byte[el];
                Buffer.BlockCopy(body, p, ed, 0, el); p += el;

                if (et == EXT_SUPPORTED_VERSIONS)
                {
                    if (el != 2 || ed[0] != 0x03 || ed[1] != 0x04)
                        throw new FormatException(L10n.T("服务端未选 TLS 1.3"));
                    sawVersion = true;
                }
                else if (et == EXT_KEY_SHARE)
                {
                    if (el < 4) throw new FormatException(L10n.T("key_share 过短"));
                    ushort group = (ushort)((ed[0] << 8) | ed[1]);
                    int klen = (ed[2] << 8) | ed[3];
                    if (4 + klen > el) throw new FormatException(L10n.T("key_share 长度不符"));
                    if (group == GROUP_SECP256R1)
                        throw new FormatException(L10n.T("暂不支持 secp256r1 密钥交换"));
                    if (group != GROUP_X25519)
                        throw new FormatException(L10n.T("服务端用了我们不支持的群 0x") + group.ToString("x4"));
                    if (klen != 32) throw new FormatException("x25519 公钥长度不是 32");
                    _serverKeyShare = new byte[klen];
                    Buffer.BlockCopy(ed, 4, _serverKeyShare, 0, klen);
                    sawKeyShare = true;
                }
                else if (et == EXT_ALPN)
                {
                    // ServerHello 的 ALPN 是「1 字节长度 + 协议名」；容错也认 ClientHello 的
                    // 「2 字节列表长 + 1 字节名长 + 名」形状。qaul 的 quinn 节点干脆不发 ALPN，
                    // 所以这个扩展是可选的（libp2p 规范里 ALPN 只是 SHOULD）。
                    string name = null;
                    if (el >= 1 && data0_1(ed, el))
                    {
                        int nlen = ed[0];
                        if (1 + nlen <= el) name = Encoding.ASCII.GetString(ed, 1, nlen);
                    }
                    else if (el >= 3)
                    {
                        int nlen = ed[2];
                        if (3 + nlen <= el) name = Encoding.ASCII.GetString(ed, 3, nlen);
                    }
                    if (name != null)
                    {
                        Alpn = name;
                        sawAlpn = true;
                    }
                }
            }
            if (!sawVersion || !sawKeyShare)
                throw new FormatException(L10n.T("ServerHello 缺少必要扩展 (version=") + sawVersion +
                    " keyShare=" + sawKeyShare + " alpn=" + sawAlpn + L10n.T(") 扩展总长=") + extTotal +
                    " body=" + HexDump(body));
            if (Alpn != null && Alpn != "libp2p")
                throw new FormatException(L10n.T("服务端选了非 libp2p 的 ALPN: ") + Alpn);

            EnsureHandshakeSecrets();
            ClientHandshake = MakeKeys(_cHsTraffic);
            ServerHandshake = MakeKeys(_sHsTraffic);
        }

        private void HandleEncryptedExtensions(byte[] body)
        {
            if (body.Length < 2) throw new FormatException(L10n.T("EncryptedExtensions 过短"));
            int extTotal = (body[0] << 8) | body[1];
            int p = 2, end = 2 + extTotal;
            if (end > body.Length) throw new FormatException(L10n.T("EncryptedExtensions 扩展越界"));
            while (p + 4 <= end)
            {
                ushort et = (ushort)((body[p] << 8) | body[p + 1]); p += 2;
                int el = (body[p] << 8) | body[p + 1]; p += 2;
                if (p + el > end) throw new FormatException(L10n.T("扩展长度越界"));
                var ed = new byte[el];
                Buffer.BlockCopy(body, p, ed, 0, el); p += el;
                if (et == EXT_QUIC_TRANSPORT) PeerTransport = QuicTransportParams.Decode(ed);
            }
            // 传输参数是 QUIC 的必需项；走 TLS 记录层跑测试时对端（OpenSSL 等）不会发，
            // 所以这里只在 RequireTransportParams=true（QUIC 模式）下强制校验。
            if (PeerTransport == null && RequireTransportParams)
                throw new FormatException(L10n.T("EncryptedExtensions 缺少传输参数"));
        }

        private void HandleCertificate(byte[] body)
        {
            // certificate_request_context<0..2^8-1> + CertificateEntry list<0..2^24-1>
            int p = 0;
            int ctxLen = body[p]; p += 1 + ctxLen;
            if (p + 3 > body.Length) throw new FormatException(L10n.T("Certificate 缺少列表长度"));
            int listLen = (body[p] << 16) | (body[p + 1] << 8) | body[p + 2]; p += 3;
            if (p + listLen > body.Length) throw new FormatException(L10n.T("Certificate 列表越界"));
            if (listLen < 3) throw new FormatException(L10n.T("证书列表为空"));
            int certLen = (body[p] << 16) | (body[p + 1] << 8) | body[p + 2]; p += 3;
            if (certLen <= 0 || p + certLen > body.Length) throw new FormatException(L10n.T("证书长度非法"));
            var der = new byte[certLen];
            Buffer.BlockCopy(body, p, der, 0, certLen);

            string err;
            var parsed = Der.Parse(der, out err);
            if (parsed == null)
            {
                if (Environment.GetEnvironmentVariable("CERTDBG") == "1")
                {
                    try
                    {
                        var certPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "peer_cert.der");
                        System.IO.File.WriteAllBytes(certPath, der);
                        Console.WriteLine(L10n.T("[tls] 对端证书已写出: ") + certPath + " (" + der.Length + L10n.T(" 字节) err=") + err);
                    }
                    catch { }
                }
                // 测试宽松模式：允许对端用普通自签 x509（OpenSSL 对照），只用于验证密钥调度。
                if (LenientTestMode) { PeerCert = null; LastCertError = err; return; }
                throw new FormatException(L10n.T("对端证书无效: ") + err);
            }
            PeerCert = parsed;
        }

        private void HandleCertificateVerify(byte[] body)
        {
            if (body.Length < 4) throw new FormatException(L10n.T("CertificateVerify 过短"));
            ushort alg = (ushort)((body[0] << 8) | body[1]);
            if (alg != SIG_ECDSA_SECP256R1_SHA256) throw new FormatException("不支持的 CertificateVerify 算法");
            int sigLen = (body[2] << 8) | body[3];
            if (4 + sigLen > body.Length) throw new FormatException(L10n.T("CertificateVerify 签名长度不符"));
            var sig = new byte[sigLen];
            Buffer.BlockCopy(body, 4, sig, 0, sigLen);

            // 64×0x20 || "TLS 1.3, server CertificateVerify" || 0x00 || TH(CH..Certificate)
            // 注意标签是 33 字节，别写成 34——多出来的 0 会被当成内容的一部分，验签必失败。
            var label = Encoding.ASCII.GetBytes("TLS 1.3, server CertificateVerify");
            var content = new byte[64 + label.Length + 1 + 32];
            for (int i = 0; i < 64; i++) content[i] = 0x20;
            Buffer.BlockCopy(label, 0, content, 64, label.Length);
            content[64 + label.Length] = 0x00;
            var th = TranscriptHash();
            Buffer.BlockCopy(th, 0, content, 64 + label.Length + 1, 32);

            var pub = PeerCert == null ? null : Der.ExtractEcPublicKey(PeerCert.SubjectPublicKeyInfo);
            if (pub == null)
            {
                if (LenientTestMode) return;      // 宽松模式：无证书可验，跳过
                throw new FormatException(L10n.T("对端 SPKI 异常"));
            }
            if (!P256.Verify(pub, content, sig)) throw new FormatException(L10n.T("CertificateVerify 验签失败"));
        }

        private void HandleFinished(byte[] body)
        {
            if (body.Length != 32) throw new FormatException("Finished 长度不是 32");
            EnsureHandshakeSecrets();
            // verify_data = HMAC(finished_key, TH(CH..CertificateVerify))，不含本条
            var th = TranscriptHash();
            var fk = ExpandLabel(_sHsTraffic, "finished", new byte[0], 32);
            var expect = new HMACSHA256(fk).ComputeHash(th);
            if (!Curve25519.BytesEqual(expect, body)) throw new FormatException(L10n.T("服务端 Finished 校验失败"));
            _serverFinishedSeen = true;

            // 应用密钥用含 server Finished 的 transcript
            TranscriptAppend(WrapHandshake(HS_FINISHED, body));
            _finEnd = (int)_transcript.Length;
            ComputeAppSecrets();
        }

        /* ===================== 客户端自己的飞行 ===================== */

        /// <summary>
        /// 构造客户端飞行：收到 CertificateRequest 时为 Certificate + CertificateVerify + Finished，
        /// 否则只有 Finished（OpenSSL 默认不做客户端认证；libp2p 一定会请求证书）。
        /// 必须在服务端 Finished 处理完之后调用。
        /// </summary>
        public byte[][] BuildClientFlight()
        {
            if (!_serverFinishedSeen) throw new InvalidOperationException(L10n.T("服务端 Finished 尚未处理"));
            if (_clientFlightSent) throw new InvalidOperationException(L10n.T("客户端飞行已发送"));
            _clientFlightSent = true;

            var outMsgs = new System.Collections.Generic.List<byte[]>();

            if (_certRequested && !SkipClientCertForTest)
            {
                // ---- Certificate ----
                var certBody = new MemoryStream();
                certBody.WriteByte(0x00);                       // certificate_request_context 为空
                var entries = new MemoryStream();
                WriteU24(entries, (uint)_myCert.CertDer.Length);
                entries.Write(_myCert.CertDer, 0, _myCert.CertDer.Length);
                WriteU16(entries, 0);                           // extensions 为空
                var entryBytes = entries.ToArray();
                WriteU24(certBody, (uint)entryBytes.Length);
                certBody.Write(entryBytes, 0, entryBytes.Length);
                var certMsg = WrapHandshake(HS_CERTIFICATE, certBody.ToArray());
                TranscriptAppend(certMsg);
                outMsgs.Add(certMsg);

                // ---- CertificateVerify：TH(CH..Certificate) ----
                var cvLabel = Encoding.ASCII.GetBytes("TLS 1.3, client CertificateVerify");
                var content = new byte[64 + cvLabel.Length + 1 + 32];
                for (int i = 0; i < 64; i++) content[i] = 0x20;
                Buffer.BlockCopy(cvLabel, 0, content, 64, cvLabel.Length);
                content[64 + cvLabel.Length] = 0x00;
                var th = TranscriptHash();
                Buffer.BlockCopy(th, 0, content, 64 + cvLabel.Length + 1, 32);
                var sig = _myCert.SignCertificateVerify(content);
                var cvBody = new MemoryStream();
                WriteU16(cvBody, SIG_ECDSA_SECP256R1_SHA256);
                WriteVec16(cvBody, sig);
                var cvMsg = WrapHandshake(HS_CERTIFICATE_VERIFY, cvBody.ToArray());
                TranscriptAppend(cvMsg);
                outMsgs.Add(cvMsg);
            }

            // ---- Finished ----
            // 客户端 Finished 的 transcript 是「CH..服务端 Finished + 自己的 Certificate + CertificateVerify」，
            // 也就是此刻的完整 transcript（不含本条）。截到 _finEnd（服务端 Finished）是错的：
            // 那样会漏掉自己的 Cert/CV，rustls 一律回 decrypt_error (0x133)。
            EnsureHandshakeSecrets();
            var fk = ExpandLabel(_cHsTraffic, "finished", new byte[0], 32);
            var verify = new HMACSHA256(fk).ComputeHash(TranscriptHash());
            var finMsg = WrapHandshake(HS_FINISHED, verify);
            TranscriptAppend(finMsg);
            outMsgs.Add(finMsg);

            return outMsgs.ToArray();
        }

        /* ===================== 应用数据加解密 ===================== */

        public byte[] SealPacket(TlsKeys keys, byte[] aad, byte[] plaintext)
        {
            if (UseAes) return AesGcmCipher.GcmEncrypt(keys.Key, keys.Nonce(), plaintext, aad);
            return ChaCha20Poly1305.Seal(keys.Key, keys.Nonce(), plaintext, aad);
        }

        public byte[] OpenPacket(TlsKeys keys, byte[] aad, byte[] ciphertextWithTag)
        {
            if (UseAes) return AesGcmCipher.GcmDecrypt(keys.Key, keys.Nonce(), ciphertextWithTag, aad);
            return ChaCha20Poly1305.Open(keys.Key, keys.Nonce(), ciphertextWithTag, aad);
        }

        /* ===================== 头保护 ===================== */

        /// <summary>
        /// 生成 5 字节头保护掩码。AES 套件对 16 字节样本做 ECB；
        /// ChaCha20 套件把样本前 4 字节当 counter、后 12 字节当 nonce，加密 5 个零字节。
        /// </summary>
        public static byte[] HeaderProtectionMask(byte[] hpKey, byte[] sample16, bool aes)
        {
            if (sample16 == null || sample16.Length < 16) throw new ArgumentException("sample16");
            if (aes)
            {
                var blk = AesGcmCipher.EcbEncryptBlock(hpKey, Slice(sample16, 0, 16));
                var mask = new byte[5];
                Buffer.BlockCopy(blk, 0, mask, 0, 5);
                return mask;
            }
            else
            {
                var counter = new byte[4];
                Buffer.BlockCopy(sample16, 0, counter, 0, 4);
                var nonce = new byte[12];
                Buffer.BlockCopy(sample16, 4, nonce, 0, 12);
                uint ctr = (uint)(counter[0] | (counter[1] << 8) | (counter[2] << 16) | (counter[3] << 24));
                var block = ChaCha20Poly1305.ChaCha20Block(hpKey, nonce, ctr);
                var mask = new byte[5];
                Buffer.BlockCopy(block, 0, mask, 0, 5);
                return mask;
            }
        }

        private static byte[] Slice(byte[] b, int off, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }
    }
}

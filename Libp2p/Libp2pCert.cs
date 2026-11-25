using System;
using System.Numerics;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// X.509 DER 最小编解码 + libp2p 证书（含 libp2p Public Key Extension）。
    ///
    /// 结构严格对齐 libp2p TLS 规范（specs/tls/tls.md）与 rust-libp2p 的实现：
    ///   · 证书自签，链上**只有一**张证书；
    ///   · 证书密钥用 ECDSA P-256 + SHA-256（rust-libp2p 同款，保证对端接受）；
    ///   · 扩展 OID 1.3.6.1.4.1.53594.1.1，critical = true，
    ///     内容 = DER(SEQUENCE { OCTET STRING protobuf公钥, OCTET STRING 签名 })；
    ///   · 签名内容 = "libp2p-tls-handshake:" ‖ 证书 SPKI 的 DER 原文，
    ///     用 libp2p 主机密钥（我们的 qaul 身份，Ed25519）签。
    /// </summary>
    public static class Der
    {
        /* ===================== 写入 ===================== */

        private static byte[] Tag(byte tag, byte[] content)
        {
            var len = EncodeLength(content.Length);
            var r = new byte[1 + len.Length + content.Length];
            r[0] = tag;
            Buffer.BlockCopy(len, 0, r, 1, len.Length);
            Buffer.BlockCopy(content, 0, r, 1 + len.Length, content.Length);
            return r;
        }

        private static byte[] EncodeLength(int n)
        {
            if (n < 0x80) return new[] { (byte)n };
            // 长形式：0x80|字节数 + 大端长度
            var tmp = new System.Collections.Generic.List<byte>();
            int x = n;
            while (x > 0) { tmp.Insert(0, (byte)(x & 0xff)); x >>= 8; }
            var r = new byte[tmp.Count + 1];
            r[0] = (byte)(0x80 | tmp.Count);
            for (int i = 0; i < tmp.Count; i++) r[i + 1] = tmp[i];
            return r;
        }

        public static byte[] Seq(params byte[][] items) { return Tag(0x30, Concat(items)); }
        public static byte[] Set(params byte[][] items) { return Tag(0x31, Concat(items)); }
        public static byte[] Octet(byte[] data) { return Tag(0x04, data); }
        public static byte[] BitString(byte[] data, int unusedBits)
        {
            var c = new byte[data.Length + 1];
            c[0] = (byte)unusedBits;
            Buffer.BlockCopy(data, 0, c, 1, data.Length);
            return Tag(0x03, c);
        }
        public static byte[] BoolTrue() { return new byte[] { 0x01, 0x01, 0xff }; }
        public static byte[] Utf8(string s) { return Tag(0x0c, Encoding.ASCII.GetBytes(s)); }
        public static byte[] Printable(string s) { return Tag(0x13, Encoding.ASCII.GetBytes(s)); }
        public static byte[] UtcTime(DateTime t)
        {
            var s = string.Format("{0:yyMMddHHmmss}Z", t.ToUniversalTime());
            return Tag(0x17, Encoding.ASCII.GetBytes(s));
        }
        public static byte[] Oid(params byte[] oidBytes) { return Tag(0x06, oidBytes); }
        public static byte[] Integer(BigInteger v)
        {
            var bytes = PositiveBigEndian(v);
            // 最高位为 1 时前面补 0，保证是正数
            if ((bytes[0] & 0x80) != 0)
            {
                var padded = new byte[bytes.Length + 1];
                Buffer.BlockCopy(bytes, 0, padded, 1, bytes.Length);
                bytes = padded;
            }
            return Tag(0x02, bytes);
        }
        public static byte[] Explicit(int tagNo, byte[] inner)
        {
            return Tag((byte)(0xA0 | tagNo), inner);
        }

        private static byte[] PositiveBigEndian(BigInteger v)
        {
            var b = v.ToByteArray();     // 小端
            int len = b.Length;
            // 去掉高位的 0x00（符号字节或多余的零），保证 DER 最小编码
            while (len > 1 && b[len - 1] == 0) len--;
            var r = new byte[len];
            for (int i = 0; i < len; i++) r[i] = b[len - 1 - i];
            return r;
        }

        private static byte[] Concat(byte[][] items)
        {
            int n = 0;
            foreach (var it in items) n += it.Length;
            var r = new byte[n];
            int off = 0;
            foreach (var it in items) { Buffer.BlockCopy(it, 0, r, off, it.Length); off += it.Length; }
            return r;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        public static byte[] EncodeSignature(BigInteger r, BigInteger s)
        {
            return Seq(Integer(r), Integer(s));
        }

        /* ===================== 读取 ===================== */

        /// <summary>
        /// 读取一个完整 TLV（tag+length+内容原文），同时给出内容。
        /// X.509 的签名覆盖的是 tbsCertificate 的**完整 DER**（含 SEQUENCE 头），
        /// 所以验签时必须用 full 而不是 content。
        /// </summary>
        public static bool ReadTlvFull(byte[] data, ref int pos, out byte[] full, out byte[] content)
        {
            full = null; content = null;
            int start = pos;
            byte tag;
            if (!ReadTlv(data, ref pos, out tag, out content)) return false;
            full = new byte[pos - start];
            Buffer.BlockCopy(data, start, full, 0, pos - start);
            return true;
        }

        /// <summary>读取一个 TLV；成功时 pos 前移到内容之后。</summary>
        public static bool ReadTlv(byte[] data, ref int pos, out byte tag, out byte[] content)
        {
            tag = 0; content = null;
            if (pos >= data.Length) return false;
            tag = data[pos++];
            if (pos >= data.Length) return false;
            int len = data[pos++];
            if ((len & 0x80) != 0)
            {
                int n = len & 0x7f;
                if (n == 0 || n > 4 || pos + n > data.Length) return false;
                len = 0;
                for (int i = 0; i < n; i++) len = (len << 8) | data[pos++];
            }
            if (len < 0 || pos + len > data.Length) return false;
            content = new byte[len];
            Buffer.BlockCopy(data, pos, content, 0, len);
            pos += len;
            return true;
        }

        public static bool TryDecodeSignature(byte[] der, out BigInteger r, out BigInteger s)
        {
            r = s = BigInteger.Zero;
            int pos = 0;
            byte tag; byte[] seq;
            if (!ReadTlv(der, ref pos, out tag, out seq) || tag != 0x30) return false;
            int p2 = 0;
            byte t2; byte[] c2;
            if (!ReadTlv(seq, ref p2, out t2, out c2) || t2 != 0x02) return false;
            r = FromBigEndian(c2);
            if (!ReadTlv(seq, ref p2, out t2, out c2) || t2 != 0x02) return false;
            s = FromBigEndian(c2);
            return true;
        }

        /// <summary>
        /// DER 内容（大端、可能高位置 1）→ 正 BigInteger。
        /// 只用到处都有的 BigInteger(byte[])（小端补码）构造，前置 0 字节保证非负。
        /// </summary>
        public static BigInteger FromBigEndian(byte[] b)
        {
            var le = new byte[b.Length + 1];
            for (int i = 0; i < b.Length; i++) le[i] = b[b.Length - 1 - i];
            return new BigInteger(le);
        }

        public static DateTime? ReadTime(byte[] content, byte tag)
        {
            try
            {
                var s = Encoding.ASCII.GetString(content);
                if (tag == 0x17)  // UTCTime YYMMDDHHMMSSZ
                {
                    int year = int.Parse(s.Substring(0, 2));
                    year += year >= 50 ? 1900 : 2000;
                    return new DateTime(year, int.Parse(s.Substring(2, 2)), int.Parse(s.Substring(4, 2)),
                                        int.Parse(s.Substring(6, 2)), int.Parse(s.Substring(8, 2)),
                                        int.Parse(s.Substring(10, 2)), DateTimeKind.Utc);
                }
                if (tag == 0x18)  // GeneralizedTime YYYYMMDDHHMMSSZ
                {
                    return new DateTime(int.Parse(s.Substring(0, 4)), int.Parse(s.Substring(4, 2)), int.Parse(s.Substring(6, 2)),
                                        int.Parse(s.Substring(8, 2)), int.Parse(s.Substring(10, 2)), int.Parse(s.Substring(12, 2)),
                                        DateTimeKind.Utc);
                }
            }
            catch { }
            return null;
        }

        private static bool OidEquals(byte[] oidDer, byte[] expected)
        {
            if (oidDer == null || oidDer.Length != expected.Length) return false;
            for (int i = 0; i < oidDer.Length; i++)
                if (oidDer[i] != expected[i]) return false;
            return true;
        }

        /* ===================== libp2p 常量 ===================== */

        /// <summary>"libp2p-tls-handshake:"。</summary>
        public static readonly byte[] P2pSigningPrefix = Encoding.ASCII.GetBytes("libp2p-tls-handshake:");

        /// <summary>1.3.6.1.4.1.53594.1.1。</summary>
        public static readonly byte[] P2pExtOid = new byte[]
            { 0x06, 0x0a, 0x2b, 0x06, 0x01, 0x04, 0x01, 0x83, 0xa2, 0x5a, 0x01, 0x01 };

        /// <summary>1.2.840.10045.2.1 ecPublicKey。</summary>
        private static readonly byte[] OidEcPublicKey = new byte[]
            { 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01 };

        /// <summary>1.2.840.10045.3.1.7 prime256v1。</summary>
        private static readonly byte[] OidPrime256v1 = new byte[]
            { 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07 };

        /// <summary>1.2.840.10045.4.3.2 ecdsa-with-SHA256。</summary>
        private static readonly byte[] OidEcdsaSha256 = new byte[]
            { 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02 };

        /* ===================== 证书 ===================== */

        /// <summary>生成一张自签证书 + 私钥。</summary>
        public static Libp2pCert Generate(byte[] hostEd25519Seed)
        {
            var kp = P256.GenerateKeyPair();
            var priv = kp.Item1;
            var pub = kp.Item2;                     // 0x04||X||Y

            // SPKI：subjectPublicKey 是完整未压缩点（0x04||X||Y，65 字节）。
            // 早期实现把 0x04 前缀去掉了，导致与 rust-libp2p / quinn 的证书互不兼容，
            // 必须保留前缀才是标准 EC PKIX 编码。
            var spki = Seq(
                Seq(OidEcPublicKey, OidPrime256v1),
                BitString(pub, 0));

            // 主机公钥 + 主机密钥对 SPKI 的签名（rust-libp2p SignedKey 扩展）
            var hostPub = Curve25519.Ed25519PublicKey(hostEd25519Seed);
            var hostSig = Curve25519.Ed25519Sign(hostEd25519Seed, Concat(P2pSigningPrefix, spki));
            var signedKey = Seq(
                Octet(EncodeHostPublicKeyProtobuf(hostPub)),
                Octet(hostSig));

            var ext = Seq(
                P2pExtOid,
                BoolTrue(),                        // critical
                Octet(signedKey));

            var tbs = Seq(
                Explicit(0, new byte[] { 0x02, 0x01, 0x02 }),   // version v3
                Integer(new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x7fffffff)),
                Seq(OidEcdsaSha256),                            // signature 算法
                Seq(),                                           // issuer（空，同 rust-libp2p）
                Seq(UtcTime(DateTime.UtcNow.AddHours(-1)),
                     UtcTime(DateTime.UtcNow.AddDays(30))),      // validity
                Seq(),                                           // subject（空）
                spki,
                Explicit(3, Seq(ext)));

            // 自签：用证书私钥对 TBS 签 ECDSA
            var sig = P256.Sign(priv, tbs);
            var signedTbs = (byte[])tbs.Clone();     // 诊断用
            var signedSig = (byte[])sig.Clone();     // 诊断用
            if (!P256.Verify(pub, tbs, sig))
                throw new InvalidOperationException(L10n.T("Generate 自检失败：刚生成的签名无法验过"));
            var cert = Seq(tbs, Seq(OidEcdsaSha256), BitString(sig, 0));

            var peerIdBytes = PeerIdBytesFromProtobuf(EncodeHostPublicKeyProtobuf(hostPub));
            return new Libp2pCert
            {
                CertDer = cert,
                PrivateKey = priv,
                HostPublicKey = hostPub,
                HostKeySignature = hostSig,
                SubjectPublicKeyInfo = spki,
                PeerIdBytes = peerIdBytes,
                PeerIdBase58 = Base58.Encode(peerIdBytes),
                SignedTbs = signedTbs,
                SignedSig = signedSig
            };
        }

        private static byte[] Slice(byte[] b, int off, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }

        /// <summary>libp2p PublicKey protobuf：{ Type=1(Ed25519), Data }。</summary>
        public static byte[] EncodeHostPublicKeyProtobuf(byte[] ed25519Public)
        {
            var r = new byte[2 + 2 + ed25519Public.Length];
            r[0] = 0x08; r[1] = 0x01;          // field 1 varint 1
            r[2] = 0x12; r[3] = (byte)ed25519Public.Length;
            Buffer.BlockCopy(ed25519Public, 0, r, 4, ed25519Public.Length);
            return r;
        }

        /// <summary>PeerId 二进制 = identity-multihash(protobuf 公钥)。</summary>
        public static byte[] PeerIdBytesFromProtobuf(byte[] protobufKey)
        {
            var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x00);                                   // multihash: identity
            ms.WriteByte((byte)protobufKey.Length);               // 长度 < 128
            ms.Write(protobufKey, 0, protobufKey.Length);
            return ms.ToArray();
        }

        /// <summary>解析对端证书并做完整校验（有效期/自签名/主机密钥所有权）。</summary>
        public static Libp2pCert Parse(byte[] der, out string error)
        {
            error = null;
            try
            {
                int pos = 0;
                byte tag; byte[] certContent;
                if (!ReadTlv(der, ref pos, out tag, out certContent) || tag != 0x30)
                { error = L10n.T("不是 DER SEQUENCE"); return null; }

                int p = 0;
                byte t; byte[] tbs, tbsFull;
                // tbsFull = tbsCertificate 的完整 DER（含 SEQUENCE 头），签名覆盖它而不是仅内容
                if (!ReadTlvFull(certContent, ref p, out tbsFull, out tbs) || tbsFull[0] != 0x30)
                { error = L10n.T("缺少 tbsCertificate"); return null; }
                byte[] sigAlg;
                if (!ReadTlv(certContent, ref p, out t, out sigAlg) || t != 0x30)
                { error = L10n.T("缺少 signatureAlgorithm"); return null; }
                byte[] sigBits;
                if (!ReadTlv(certContent, ref p, out t, out sigBits) || t != 0x03)
                { error = L10n.T("缺少 signatureValue"); return null; }
                var signature = Slice(sigBits, 1, sigBits.Length - 1);

                // ---- 解析 TBSCertificate ----
                int tp = 0;
                byte tt; byte[] tmp;
                byte[] spki = null, spkiFull = null;
                DateTime? notBefore = null, notAfter = null;
                byte[] signedKeyContent = null;

                if (!ReadTlv(tbs, ref tp, out tt, out tmp)) { error = L10n.T("TBS 为空"); return null; }
                // version [0]（可选）：跳过包装；此时 tt/tmp 已是下一个 TLV
                if (tt == 0xA0)
                {
                    if (!ReadTlv(tbs, ref tp, out tt, out tmp)) { error = L10n.T("TBS 损坏"); return null; }
                }
                // serialNumber（有 version 时上面那次读取拿到的就是它）
                if (tt != 0x02) { error = "缺少序列号"; return null; }
                // signature AlgorithmIdentifier
                if (!ReadTlv(tbs, ref tp, out tt, out tmp) || tt != 0x30) { error = "缺少签名算法"; return null; }
                // issuer
                if (!ReadTlv(tbs, ref tp, out tt, out tmp) || tt != 0x30) { error = "缺少 issuer"; return null; }
                // validity
                if (!ReadTlv(tbs, ref tp, out tt, out tmp) || tt != 0x30) { error = "缺少 validity"; return null; }
                {
                    int vp = 0;
                    byte vt; byte[] vc;
                    if (ReadTlv(tmp, ref vp, out vt, out vc)) notBefore = ReadTime(vc, vt);
                    if (ReadTlv(tmp, ref vp, out vt, out vc)) notAfter = ReadTime(vc, vt);
                }
                // subject
                if (!ReadTlv(tbs, ref tp, out tt, out tmp) || tt != 0x30) { error = "缺少 subject"; return null; }
                // subjectPublicKeyInfo（spkiFull = 完整 DER，host key 签名要用它）
                if (!ReadTlvFull(tbs, ref tp, out spkiFull, out spki) || spkiFull[0] != 0x30)
                { error = L10n.T("缺少 SPKI"); return null; }
                // extensions [3]
                while (tp < tbs.Length)
                {
                    if (!ReadTlv(tbs, ref tp, out tt, out tmp)) break;
                    if (tt != 0xA3) continue;
                    int ep = 0;
                    byte et; byte[] exts;
                    if (!ReadTlv(tmp, ref ep, out et, out exts) || et != 0x30) continue;
                    int xp = 0;
                    while (xp < exts.Length)
                    {
                        byte xt; byte[] ext;
                        if (!ReadTlv(exts, ref xp, out xt, out ext) || xt != 0x30) break;
                        int xp2 = 0;
                        byte xt2; byte[] oid;
                        if (!ReadTlv(ext, ref xp2, out xt2, out oid) || xt2 != 0x06) continue;
                        if (!OidEquals(oid, Slice(P2pExtOid, 2, P2pExtOid.Length - 2))) continue;

                        // critical 标志（BOOLEAN，可缺省）
                        if (xp2 < ext.Length && ext[xp2] == 0x01)
                        {
                            byte bt2; byte[] bc;
                            ReadTlv(ext, ref xp2, out bt2, out bc);
                        }
                        // extnValue OCTET STRING
                        byte et2; byte[] value;
                        if (ReadTlv(ext, ref xp2, out et2, out value) && et2 == 0x04)
                            signedKeyContent = value;
                    }
                }

                if (signedKeyContent == null) { error = "证书没有 libp2p 公钥扩展"; return null; }

                // ---- SignedKey ----
                int sp = 0;
                byte st; byte[] signedKey;
                if (!ReadTlv(signedKeyContent, ref sp, out st, out signedKey) || st != 0x30)
                { error = L10n.T("SignedKey 不是 SEQUENCE"); return null; }
                int skp = 0;
                byte skt; byte[] hostKeyProto, hostKeySig;
                if (!ReadTlv(signedKey, ref skp, out skt, out hostKeyProto) || skt != 0x04)
                { error = L10n.T("SignedKey 缺少 publicKey"); return null; }
                if (!ReadTlv(signedKey, ref skp, out skt, out hostKeySig) || skt != 0x04)
                { error = L10n.T("SignedKey 缺少 signature"); return null; }

                // protobuf PublicKey { Type, Data }
                int kt, kd;
                var keyReader = new PbReader(hostKeyProto);
                if (!keyReader.Next(out kt, out kd) || kt != 1 || kd != 0)
                { error = L10n.T("公钥 protobuf 损坏"); return null; }
                int keyType = (int)keyReader.Varint();
                if (!keyReader.Next(out kt, out kd) || kt != 2 || kd != 2)
                { error = L10n.T("公钥 protobuf 缺少 Data"); return null; }
                var hostKeyData = keyReader.Bytes();
                if (keyType != 1 || hostKeyData.Length != 32)
                { error = L10n.T("只支持 Ed25519 主机密钥"); return null; }

                var cert = new Libp2pCert
                {
                    CertDer = der,
                    SubjectPublicKeyInfo = spki,
                    HostPublicKey = hostKeyData,
                    HostKeySignature = hostKeySig,
                    PeerIdBytes = PeerIdBytesFromProtobuf(hostKeyProto),
                    PeerIdBase58 = Base58.Encode(PeerIdBytesFromProtobuf(hostKeyProto)),
                    NotBefore = notBefore,
                    NotAfter = notAfter
                };

                // ---- 校验 ----
                var now = DateTime.UtcNow;
                if (notBefore.HasValue && now < notBefore.Value.AddMinutes(-5))
                { error = L10n.T("证书尚未生效"); return null; }
                if (notAfter.HasValue && now > notAfter.Value.AddMinutes(5))
                { error = L10n.T("证书已过期"); return null; }

                // 自签名：SPKI 公钥验 TBS 的完整 DER（RFC 5280：签名覆盖 tbsCertificate 整体）
                var spkiPub = ExtractEcPublicKey(spki);
                if (spkiPub == null) { error = "SPKI 不是 EC 公钥"; return null; }
                if (!P256.Verify(spkiPub, tbsFull, signature))
                { error = L10n.T("证书自签名无效"); return null; }

                // 主机密钥所有权：libp2p 规范要求签的是 前缀 ‖ SPKI 的**完整 DER**
                var msg = Concat(P2pSigningPrefix, spkiFull);
                if (!Curve25519.Ed25519Verify(hostKeyData, msg, hostKeySig))
                { error = L10n.T("libp2p 主机密钥签名无效"); return null; }

                return cert;
            }
            catch (Exception ex)
            {
                error = L10n.T("解析异常: ") + ex.Message;
                return null;
            }
        }

        /// <summary>从 SPKI（完整 TLV）取出未压缩 EC 点（0x04||X||Y）。</summary>
        public static byte[] ExtractEcPublicKey(byte[] spkiTlv)
        {
            try
            {
                int p = 0;
                byte t; byte[] alg, bits;
                if (!ReadTlv(spkiTlv, ref p, out t, out alg)) return null;
                if (!ReadTlv(spkiTlv, ref p, out t, out bits) || t != 0x03) return null;
                if (bits.Length == 0 || bits[0] != 0) return null;   // unusedBits 必为 0
                // 标准编码：BIT STRING 内容 = 1 字节 unusedBits + 65 字节 0x04||X||Y
                if (bits.Length == 66 && bits[1] == 0x04)
                {
                    var r = new byte[65];
                    Buffer.BlockCopy(bits, 1, r, 0, 65);
                    return r;
                }
                // 容错：早期自产证书省略了 0x04 前缀（仅 64 字节 X||Y）
                if (bits.Length == 65)
                {
                    var r = new byte[65];
                    r[0] = 0x04;
                    Buffer.BlockCopy(bits, 1, r, 1, 64);
                    return r;
                }
                return null;
            }
            catch { return null; }
        }
    }

    /// <summary>一张 libp2p X.509 证书（含解析结果与私钥，若为本机生成）。</summary>
    public sealed class Libp2pCert
    {
        public byte[] CertDer;
        public byte[] PrivateKey;             // 仅本机生成的证书有
        public byte[] HostPublicKey;          // libp2p 主机公钥（Ed25519, 32B）
        public byte[] HostKeySignature;       // 主机密钥对 SPKI 的签名
        public byte[] SubjectPublicKeyInfo;
        public byte[] PeerIdBytes;
        public string PeerIdBase58;
        public DateTime? NotBefore;
        public DateTime? NotAfter;
        public byte[] SignedTbs;    // 诊断用：Generate 实际签名的 TBS 原文
        public byte[] SignedSig;    // 诊断用：Generate 实际得到的签名原文

        /// <summary>CertificateVerify 用本证书私钥签（ECDSA P-256 + SHA-256，DER(r,s)）。</summary>
        public byte[] SignCertificateVerify(byte[] transcriptContent)
        {
            if (PrivateKey == null) throw new InvalidOperationException("对端证书没有私钥");
            return P256.Sign(PrivateKey, transcriptContent);
        }
    }
}

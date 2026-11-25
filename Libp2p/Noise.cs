using System;
using System.IO;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// libp2p 的 Noise 安全通道（Noise_XX_25519_ChaChaPoly_SHA256）。
    ///
    /// qaul 的 LAN/Internet 模块用 libp2p 的 `noise::Config`，所以要和 qaul 节点通信，
    /// 必须逐字节实现同一套握手：
    ///
    ///   -> e                         (32B 临时公钥)
    ///   <- e, ee, s, es, payload     (payload = 身份公钥 + 对静态密钥的签名)
    ///   -> s, se, payload
    ///
    /// 三层细节容易踩错，这里都按实现对齐：
    ///   1. 握手与传输帧都以 **2 字节大端长度** 前缀；
    ///   2. 握手期的 nonce 是**单计数器**，加密解密共用、每次 +1；
    ///   3. 静态密钥可以是任意 X25519 密钥，身份通过 payload 里的 Ed25519 签名绑定，
    ///      签名内容是 `"noise-libp2p-static-key:"` + 握手用的静态公钥。
    /// </summary>
    public sealed class NoiseSession
    {
        private const string ProtocolName = "Noise_XX_25519_ChaChaPoly_SHA256";
        private const string SigPrefix = "noise-libp2p-static-key:";
        private const int TagLen = 16;
        private const int MaxFrame = 65535;

        private readonly Stream io;
        private readonly bool initiator;

        private byte[] sPriv;          // 本机静态 X25519 私钥（种子）
        private byte[] sPub;
        private byte[] ePriv, ePub;
        private byte[] rePub, rsPub;

        private byte[] h;              // 握手哈希
        private byte[] ck;             // chaining key
        private byte[] handshakeKey;   // 握手期 AEAD 密钥（null 表示尚未 mix_key）
        private ulong handshakeNonce;

        private byte[] sendKey, recvKey;
        private ulong sendNonce, recvNonce;

        private readonly byte[] identitySeed;   // Ed25519 身份私钥种子
        private readonly byte[] identityPub;    // Ed25519 身份公钥

        /// <summary>握手完成后得到的对端身份（38 字节 qaul PeerId）与昵称等扩展信息。</summary>
        public byte[] RemotePeerId { get; private set; }
        public string RemotePeerIdBase58 { get; private set; }

        public bool Handshaked { get; private set; }

        private NoiseSession(Stream io, bool initiator, byte[] staticSeed, byte[] idSeed, byte[] idPub)
        {
            this.io = io;
            this.initiator = initiator;
            sPriv = staticSeed;
            sPub = Curve25519.X25519PublicKey(staticSeed);
            identitySeed = idSeed;
            identityPub = idPub;
        }

        /// <summary>发起方握手：远端期望的 PeerId 可为 null（不校验）。</summary>
        public static NoiseSession Dial(Stream io, byte[] staticSeed, byte[] identitySeed,
                                        byte[] identityPub, byte[] expectedRemotePeerId)
        {
            var n = new NoiseSession(io, true, staticSeed, identitySeed, identityPub);
            n.RunInitiator(expectedRemotePeerId);
            return n;
        }

        /// <summary>接受方握手：从 payload 学习对端 PeerId。</summary>
        public static NoiseSession Accept(Stream io, byte[] staticSeed, byte[] identitySeed, byte[] identityPub)
        {
            var n = new NoiseSession(io, false, staticSeed, identitySeed, identityPub);
            n.RunResponder();
            return n;
        }

        /* ===================== 握手 ===================== */

        private void InitSymmetric()
        {
            var name = Encoding.UTF8.GetBytes(ProtocolName);
            // Noise 规范：协议名不足 32 字节右侧补零，超过则取哈希
            h = name.Length <= 32 ? Pad32(name) : LocalIdentity.Sha256(name);
            ck = (byte[])h.Clone();
            handshakeKey = null;
            handshakeNonce = 0;
            // prologue 为空
            MixHash(new byte[0]);
        }

        private static byte[] Pad32(byte[] src)
        {
            var b = new byte[32];
            Buffer.BlockCopy(src, 0, b, 0, src.Length);
            return b;
        }

        private void MixHash(byte[] data)
        {
            var buf = new byte[h.Length + (data == null ? 0 : data.Length)];
            Buffer.BlockCopy(h, 0, buf, 0, h.Length);
            if (data != null && data.Length > 0) Buffer.BlockCopy(data, 0, buf, h.Length, data.Length);
            h = LocalIdentity.Sha256(buf);
        }

        private void MixKey(byte[] ikm)
        {
            byte[] newCk, key;
            Hkdf.DeriveTwo(ck, ikm, out newCk, out key);
            ck = newCk;
            handshakeKey = key;
            handshakeNonce = 0;
        }

        private byte[] HandshakeEncrypt(byte[] plaintext)
        {
            if (handshakeKey == null) return plaintext;   // 尚未 mix_key 时明文
            var ct = ChaCha20Poly1305.Encrypt(handshakeKey, handshakeNonce, plaintext, h);
            handshakeNonce++;
            return ct;
        }

        private byte[] HandshakeDecrypt(byte[] ciphertext)
        {
            if (handshakeKey == null) return ciphertext;
            var pt = ChaCha20Poly1305.Decrypt(handshakeKey, handshakeNonce, ciphertext, h);
            if (pt == null) throw new InvalidDataException("Noise 握手解密失败（AEAD 校验不通过）");
            handshakeNonce++;
            return pt;
        }

        private byte[] EncryptAndHash(byte[] plaintext)
        {
            var ct = HandshakeEncrypt(plaintext);
            MixHash(ct);
            return ct;
        }

        private byte[] DecryptAndHash(byte[] ciphertext)
        {
            var pt = HandshakeDecrypt(ciphertext);
            MixHash(ciphertext);
            return pt;
        }

        private void RunInitiator(byte[] expectedRemotePeerId)
        {
            InitSymmetric();

            // -> e
            ePriv = Curve25519.RandomSeed();
            ePub = Curve25519.X25519PublicKey(ePriv);
            MixHash(ePub);
            var msg1 = EncryptAndHash(new byte[0]);
            var out1 = Concat(ePub, msg1);
            WriteFrame(out1);

            // <- e, ee, s, es
            var msg2 = ReadFrame();
            if (msg2 == null || msg2.Length < 32) throw new EndOfStreamException("Noise: 未收到 responder 的 e");
            rePub = Slice(msg2, 0, 32);
            MixHash(rePub);
            MixKey(Curve25519.X25519SharedSecret(ePriv, rePub));   // ee

            var rest = Slice(msg2, 32, msg2.Length - 32);
            if (rest.Length < 48) throw new InvalidDataException(L10n.T("Noise: responder 静态密钥长度不对"));
            rsPub = DecryptAndHash(Slice(rest, 0, 48));
            MixKey(Curve25519.X25519SharedSecret(ePriv, rsPub));   // es（发起方用 e × rs）

            var payload = DecryptAndHash(Slice(rest, 48, rest.Length - 48));
            VerifyPayload(payload, expectedRemotePeerId);

            // -> s, se
            var encS = EncryptAndHash(sPub);
            MixKey(Curve25519.X25519SharedSecret(sPriv, rePub));   // se（发起方用 s × re）
            var myPayload = EncryptAndHash(BuildPayload());

            WriteFrame(Concat(encS, myPayload));

            FinishSplit();
            Handshaked = true;
        }

        private void RunResponder()
        {
            InitSymmetric();

            // <- e
            var msg1 = ReadFrame();
            if (msg1 == null || msg1.Length < 32) throw new EndOfStreamException("Noise: 未收到 initiator 的 e");
            rePub = Slice(msg1, 0, 32);
            MixHash(rePub);
            DecryptAndHash(Slice(msg1, 32, msg1.Length - 32));   // 无密钥，等于 mix_hash 空

            // -> e, ee, s, es
            ePriv = Curve25519.RandomSeed();
            ePub = Curve25519.X25519PublicKey(ePriv);
            MixHash(ePub);
            MixKey(Curve25519.X25519SharedSecret(ePriv, rePub));   // ee

            var encS = EncryptAndHash(sPub);
            MixKey(Curve25519.X25519SharedSecret(sPriv, rePub));   // es（响应方用 s × re）
            var payload = EncryptAndHash(BuildPayload());

            WriteFrame(Concat(Concat(ePub, encS), payload));

            // <- s, se
            var msg3 = ReadFrame();
            if (msg3 == null || msg3.Length < 48) throw new InvalidDataException("Noise: initiator 静态密钥缺失");
            rsPub = DecryptAndHash(Slice(msg3, 0, 48));
            MixKey(Curve25519.X25519SharedSecret(ePriv, rsPub));   // se（响应方用 e × rs）

            var p3 = DecryptAndHash(Slice(msg3, 48, msg3.Length - 48));
            VerifyPayload(p3, null);

            FinishSplit();
            Handshaked = true;
        }

        private void FinishSplit()
        {
            byte[] k1, k2;
            Hkdf.Split(ck, out k1, out k2);
            if (initiator) { sendKey = k1; recvKey = k2; }
            else { sendKey = k2; recvKey = k1; }
            sendNonce = 0;
            recvNonce = 0;
        }

        /* ===================== 身份 payload ===================== */

        private byte[] BuildPayload()
        {
            // NoiseHandshakePayload { identity_key = 1, identity_sig = 2 }
            var sig = Curve25519.Ed25519Sign(identitySeed, Concat(Encoding.UTF8.GetBytes(SigPrefix), sPub));

            // identity_key 是 **公钥的 protobuf 编码**（08 01 12 20 ‖ 32B），
            // 不是完整的 PeerId：multihash 前缀由接收端自己加，加错就会被判成冒充。
            var keyProto = new byte[36];
            keyProto[0] = 0x08; keyProto[1] = 0x01; keyProto[2] = 0x12; keyProto[3] = 0x20;
            Buffer.BlockCopy(identityPub, 0, keyProto, 4, 32);

            var w = new PbWriter();
            w.Bytes(1, keyProto);
            w.Bytes(2, sig);
            return w.ToArray();
        }

        private void VerifyPayload(byte[] payload, byte[] expectedRemotePeerId)
        {
            if (payload == null || payload.Length == 0)
                throw new InvalidDataException(L10n.T("Noise: 对端未发送身份 payload"));

            var r = new PbReader(payload);
            byte[] keyProto = null, sig = null;
            int field, wire;
            while (r.Next(out field, out wire))
            {
                if (field == 1) keyProto = r.Bytes();
                else if (field == 2) sig = r.Bytes();
                else r.Skip(wire);
            }

            if (keyProto == null || keyProto.Length < 36 || sig == null || sig.Length != 64)
                throw new InvalidDataException(L10n.T("Noise: payload 字段缺失"));

            var pub = Slice(keyProto, keyProto.Length - 32, 32);
            if (!Curve25519.Ed25519Verify(pub, Concat(Encoding.UTF8.GetBytes(SigPrefix), rsPub), sig))
                throw new InvalidDataException(L10n.T("Noise: 身份签名校验失败（可能是冒充）"));

            // PeerId = identity-multihash(keyProto)
            var peerId = new byte[2 + keyProto.Length];
            peerId[0] = 0x00;
            peerId[1] = (byte)keyProto.Length;
            Buffer.BlockCopy(keyProto, 0, peerId, 2, keyProto.Length);

            if (expectedRemotePeerId != null && expectedRemotePeerId.Length > 0 &&
                !Curve25519.BytesEqual(peerId, expectedRemotePeerId))
                throw new InvalidDataException(L10n.T("Noise: 对端 PeerId 与预期不符"));

            RemotePeerId = peerId;
            RemotePeerIdBase58 = Base58.Encode(peerId);
        }

        /* ===================== 传输帧 ===================== */

        /// <summary>调试：导出每个出站帧的明文前 8 字节，用于和抓包对照。</summary>
        public static Action<string> DebugPlaintext;

        public byte[] Encrypt(byte[] plaintext)
        {
            if (!Handshaked) throw new InvalidOperationException(L10n.T("Noise 尚未握手完成"));
            var dbg = DebugPlaintext;
            if (dbg != null)
            {
                try
                {
                    var n = Math.Min(8, plaintext.Length);
                    var sb = new StringBuilder();
                    for (int i = 0; i < n; i++) sb.Append(plaintext[i].ToString("x2")).Append(' ');
                    dbg("noise-tx len=" + plaintext.Length + " head=" + sb.ToString().Trim());
                }
                catch { }
            }
            if (plaintext.Length > MaxFrame - TagLen) throw new ArgumentException(L10n.T("Noise 帧过大"));
            var ct = ChaCha20Poly1305.Encrypt(sendKey, sendNonce, plaintext, new byte[0]);
            sendNonce++;
            return ct;
        }

        public byte[] Decrypt(byte[] ciphertext)
        {
            if (!Handshaked) throw new InvalidOperationException(L10n.T("Noise 尚未握手完成"));
            var pt = ChaCha20Poly1305.Decrypt(recvKey, recvNonce, ciphertext, new byte[0]);
            if (pt == null) throw new InvalidDataException("Noise 传输帧认证失败");
            recvNonce++;
            return pt;
        }

        private void WriteFrame(byte[] payload)
        {
            IoUtil.WriteBE16(io, payload.Length);
            io.Write(payload, 0, payload.Length);
            io.Flush();
        }

        private byte[] ReadFrame()
        {
            int len = IoUtil.ReadBE16(io);
            if (len <= 0) return null;
            var buf = new byte[len];
            if (!IoUtil.ReadExact(io, buf, 0, len)) return null;
            return buf;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static byte[] Slice(byte[] src, int off, int len)
        {
            if (len <= 0) return new byte[0];
            var r = new byte[len];
            Buffer.BlockCopy(src, off, r, 0, len);
            return r;
        }
    }

    /// <summary>
    /// 把 Noise 会话包装成 Stream：读时按 2 字节长度拆帧并解密，写时加密后成帧。
    /// 这样 yamux 可以直接跑在它上面，不必关心加密。
    /// </summary>
    public sealed class NoiseStream : Stream
    {
        private readonly Stream inner;
        private readonly NoiseSession session;
        private byte[] pending = new byte[0];
        private int pendingPos;

        public NoiseStream(Stream inner, NoiseSession session)
        {
            this.inner = inner;
            this.session = session;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush() { inner.Flush(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pendingPos >= pending.Length)
            {
                int len = IoUtil.ReadBE16(inner);
                if (len <= 0) return 0;
                var ct = new byte[len];
                if (!IoUtil.ReadExact(inner, ct, 0, len)) return 0;
                pending = session.Decrypt(ct);
                pendingPos = 0;
            }
            int n = Math.Min(count, pending.Length - pendingPos);
            Buffer.BlockCopy(pending, pendingPos, buffer, offset, n);
            pendingPos += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            var plain = new byte[count];
            Buffer.BlockCopy(buffer, offset, plain, 0, count);
            var ct = session.Encrypt(plain);
            IoUtil.WriteBE16(inner, ct.Length);
            inner.Write(ct, 0, ct.Length);
            inner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}

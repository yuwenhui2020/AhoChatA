using System;
using System.IO;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// qaul 的 Noise_KK_25519_ChaChaPoly_SHA256 会话。
    ///
    /// 这是 qaul **用户对用户** 的端到端加密（和 libp2p 那条链路无关）：
    /// 节点之间传的是已经用 KK 会话加密好的 `Messaging` 负载。
    ///
    /// 必须照抄 qaul 的三个非标准点，否则密钥对不上：
    ///   1. 协议名 "Noise_KK_25519_ChaChaPoly_SHA256"（37 字节 &gt; 32 → h = SHA256(name)）；
    ///   2. 握手前先把 **发起方静态公钥** 和 **响应方静态公钥** 依次 mix_hash；
    ///   3. **第二条消息是从头重建的状态**：qaul 的 decrypt/encrypt_handshake_2 都新建
    ///      HandshakeState 后 set_index(1)，等于跳过 msg1 的 E/ES/SS 对链式密钥的影响。
    ///      这一步看起来像 bug，但两边都这么做，所以必须一致。
    ///
    /// 静态密钥来自用户账号（ed25519 → montgomery），不是节点密钥。
    /// </summary>
    public sealed class QaulKk
    {
        public const string ProtocolName = "Noise_KK_25519_ChaChaPoly_SHA256";

        private byte[] h;
        private byte[] ck;
        private byte[] hsKey;
        private ulong hsNonce;
        private bool hasKey;

        public byte[] LocalStatic;        // x25519 私钥（clamped）
        public byte[] LocalStaticPub;     // x25519 公钥
        public byte[] RemoteStaticPub;    // 对端 x25519 公钥

        /// <summary>本端临时私钥（msg1 发起方 / msg2 响应方使用）。</summary>
        public byte[] EphPrivate;
        public byte[] EphPublic;
        /// <summary>对端临时公钥。</summary>
        public byte[] RemoteEph;

        public byte[] SendKey { get; private set; }
        public byte[] RecvKey { get; private set; }
        public bool IsInitiator { get; private set; }

        private QaulKk() { }

        /// <summary>测试用：把握手的中间量（h/ck/key/ct）吐出来，便于和参考实现逐步比对。</summary>
        public static Action<string, byte[]> DebugTrace;

        private static void Trace(string tag, byte[] data)
        {
            var cb = DebugTrace;
            if (cb != null) { try { cb(tag, data ?? new byte[0]); } catch { } }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>初始化对称状态 + 混入 premessage 的两个静态公钥。</summary>
        private void InitSymmetric(bool initiator, byte[] initiatorStaticPub, byte[] responderStaticPub)
        {
            var name = Encoding.UTF8.GetBytes(ProtocolName);
            var padded = new byte[32];
            if (name.Length <= 32)
            {
                Buffer.BlockCopy(name, 0, padded, 0, name.Length);
                h = padded;
            }
            else
            {
                h = LocalIdentity.Sha256(name);
            }
            ck = (byte[])h.Clone();
            hsKey = null;
            hasKey = false;
            hsNonce = 0;

            Trace("h0", h);
            MixHash(new byte[0]);                 // prologue
            Trace("h_prologue", h);
            MixHash(initiatorStaticPub);          // pre_i: S
            Trace("h_si", h);
            MixHash(responderStaticPub);          // pre_r: S
            Trace("h_sr", h);
        }

        private void MixHash(byte[] data)
        {
            h = LocalIdentity.Sha256(Concat(h, data ?? new byte[0]));
        }

        private void MixKey(byte[] ikm)
        {
            Trace("mixkey_ikm", ikm);
            byte[] newCk, key;
            Hkdf.DeriveTwo(ck, ikm, out newCk, out key);
            ck = newCk;
            hsKey = key;
            hsNonce = 0;
            hasKey = true;
            Trace("mixkey", key);
        }

        private byte[] EncryptAndHash(byte[] plaintext)
        {
            if (!hasKey)
            {
                MixHash(plaintext);
                return plaintext;
            }
            Trace("aead_ad", h);
            var ct = ChaCha20Poly1305.Encrypt(hsKey, hsNonce, plaintext, h);
            Trace("aead_ct", ct);
            hsNonce++;
            MixHash(ct);
            return ct;
        }

        private byte[] DecryptAndHash(byte[] ciphertext)
        {
            byte[] pt;
            if (!hasKey)
            {
                pt = ciphertext;
            }
            else
            {
                pt = ChaCha20Poly1305.Decrypt(hsKey, hsNonce, ciphertext, h);
                if (pt == null) throw new InvalidDataException("KK 解密失败（AEAD 校验不通过）");
                hsNonce++;
            }
            MixHash(ciphertext);
            return pt;
        }

        /* ===================== 发起方 ===================== */

        /// <summary>
        /// 发起握手：返回 msg1（e ‖ 加密载荷）。
        /// 注意 msg1 就带着第一条业务数据，qaul 就是这么设计的。
        /// <paramref name="ephPrivOverride"/> 仅供互操作测试固定临时密钥，生产路径传 null。
        /// </summary>
        public static QaulKk BeginInitiator(byte[] myStaticPriv, byte[] myStaticPub,
                                            byte[] remoteStaticPub, byte[] payload,
                                            out byte[] msg1, byte[] ephPrivOverride = null)
        {
            var kk = new QaulKk
            {
                IsInitiator = true,
                LocalStatic = myStaticPriv,
                LocalStaticPub = myStaticPub,
                RemoteStaticPub = remoteStaticPub
            };

            kk.InitSymmetric(true, myStaticPub, remoteStaticPub);

            kk.EphPrivate = ephPrivOverride ?? Curve25519.RandomSeed();
            kk.EphPublic = Curve25519.X25519PublicKey(kk.EphPrivate);

            // E
            kk.MixHash(kk.EphPublic);
            // ES: e × 对端静态
            kk.MixKey(Curve25519.X25519SharedSecret(kk.EphPrivate, remoteStaticPub));
            // SS: 自己静态 × 对端静态
            kk.MixKey(Curve25519.X25519SharedSecret(myStaticPriv, remoteStaticPub));

            var ct = kk.EncryptAndHash(payload ?? new byte[0]);
            msg1 = Concat(kk.EphPublic, ct);
            return kk;
        }

        /// <summary>发起方读取 msg2 并派生传输密钥。</summary>
        public void CompleteAsInitiator(byte[] msg2)
        {
            if (msg2 == null || msg2.Length < 32 + 16)
                throw new InvalidDataException(L10n.T("KK msg2 长度不对"));

            // 重建状态：qaul 的 decrypt_noise_kk_handshake_2 就是 new + set_index(1)
            InitSymmetric(true, LocalStaticPub, RemoteStaticPub);

            RemoteEph = new byte[32];
            Buffer.BlockCopy(msg2, 0, RemoteEph, 0, 32);
            MixHash(RemoteEph);                                                   // E
            MixKey(Curve25519.X25519SharedSecret(EphPrivate, RemoteEph));          // EE
            MixKey(Curve25519.X25519SharedSecret(LocalStatic, RemoteEph));         // SE

            var rest = new byte[msg2.Length - 32];
            Buffer.BlockCopy(msg2, 32, rest, 0, rest.Length);
            DecryptAndHash(rest);   // 第二跳的载荷（qaul 的 second_handshake），内容不影响密钥

            byte[] c1, c2;
            Hkdf.Split(ck, out c1, out c2);
            SendKey = c1;   // 发起方 → 响应方
            RecvKey = c2;
        }

        /* ===================== 响应方 ===================== */

        /// <summary>响应方读取 msg1，返回其中的业务数据。</summary>
        public static QaulKk ReadAsResponder(byte[] myStaticPriv, byte[] myStaticPub,
                                             byte[] remoteStaticPub, byte[] msg1,
                                             out byte[] payload)
        {
            if (msg1 == null || msg1.Length < 32 + 16)
                throw new InvalidDataException(L10n.T("KK msg1 长度不对"));

            var kk = new QaulKk
            {
                IsInitiator = false,
                LocalStatic = myStaticPriv,
                LocalStaticPub = myStaticPub,
                RemoteStaticPub = remoteStaticPub
            };

            kk.InitSymmetric(false, remoteStaticPub, myStaticPub);

            kk.RemoteEph = new byte[32];
            Buffer.BlockCopy(msg1, 0, kk.RemoteEph, 0, 32);
            kk.MixHash(kk.RemoteEph);                                             // E
            kk.MixKey(Curve25519.X25519SharedSecret(myStaticPriv, kk.RemoteEph));  // ES（响应方用 s × re）
            kk.MixKey(Curve25519.X25519SharedSecret(myStaticPriv, remoteStaticPub)); // SS

            var rest = new byte[msg1.Length - 32];
            Buffer.BlockCopy(msg1, 32, rest, 0, rest.Length);
            payload = kk.DecryptAndHash(rest);
            return kk;
        }

        /// <summary>响应方生成 msg2 并派生传输密钥。</summary>
        public byte[] BuildResponderMessage(byte[] payload)
        {
            // 同样重建状态（跳过 msg1 对链式密钥的影响），与 qaul 一致
            InitSymmetric(false, RemoteStaticPub, LocalStaticPub);

            EphPrivate = Curve25519.RandomSeed();
            EphPublic = Curve25519.X25519PublicKey(EphPrivate);

            MixHash(EphPublic);                                                   // E
            MixKey(Curve25519.X25519SharedSecret(EphPrivate, RemoteEph));          // EE
            // SE 永远是「发起方静态 × 响应方临时」：响应方只有自己的临时私钥
            // 和对方的静态公钥，所以是 e_resp × s_init（写成 s_resp × e_init 就错了）
            MixKey(Curve25519.X25519SharedSecret(EphPrivate, RemoteStaticPub));    // SE

            var ct = EncryptAndHash(payload ?? new byte[0]);

            byte[] c1, c2;
            Hkdf.Split(ck, out c1, out c2);
            RecvKey = c1;    // 响应方收到的是发起方方向
            SendKey = c2;

            return Concat(EphPublic, ct);
        }

        /* ===================== 传输 ===================== */

        public byte[] Encrypt(byte[] plaintext, ulong nonce)
        {
            return ChaCha20Poly1305.Encrypt(SendKey, nonce, plaintext, new byte[0]);
        }

        public byte[] Decrypt(byte[] ciphertext, ulong nonce)
        {
            return ChaCha20Poly1305.Decrypt(RecvKey, nonce, ciphertext, new byte[0]);
        }
    }
}

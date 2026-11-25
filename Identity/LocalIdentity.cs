using System;
using System.Security.Cryptography;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// 本机身份（与 qaul / bitchat 两套指纹格式对齐）。
    ///
    /// 三把密钥各司其职，互不牵连——这一点很重要，因为「改指纹」必须能只改其中一套，
    /// 否则换 qaul 身份会顺带把 bitchat 的联系人也换掉：
    ///
    ///   edSign    Ed25519   qaul 账号密钥。qaul 的 user id 就是 libp2p 的
    ///                       PeerId(ed25519 公钥)，即 identity-multihash 的 Base58。
    ///   xNoise    X25519    bitchat 的 Noise 静态密钥。bitchat 的 peerID =
    ///                       SHA256(noise 公钥) 的前 8 字节（16 个 hex 字符）。
    ///   edSign2   Ed25519   bitchat 的消息签名密钥。bitchat 的「身份指纹」是这把
    ///                       公钥的 SHA256（用于联系人验证，防冒充）。
    ///
    /// Mchat 自身仍在用的 12 位 peerId 也从签名公钥派生，因此「换指纹」会同时换掉
    /// Mchat 局域网身份，避免旧身份继续被关联。
    /// </summary>
    public class LocalIdentity
    {
        // ---- 原始密钥材料（持久化的只有种子） ----
        public byte[] QaulSeed;     // Ed25519, 32B
        public byte[] NoiseSeed;    // X25519,  32B
        public byte[] SignSeed;     // Ed25519, 32B（bitchat 签名）

        // ---- 派生结果 ----
        public byte[] QaulPublic;       // Ed25519 公钥 32B
        public byte[] NoisePublic;      // X25519 公钥 32B
        public byte[] SignPublic;       // Ed25519 公钥 32B

        /// <summary>qaul user id 的二进制形式：identity-multihash 包裹的 Ed25519 公钥（36B）。</summary>
        public byte[] QaulPeerIdBytes;
        /// <summary>qaul user id 的 Base58 文本（形如 12D3KooW…）。</summary>
        public string QaulPeerId;
        /// <summary>qaul 公钥 hex（64 字符，用于跨端核对）。</summary>
        public string QaulKeyHex;

        /// <summary>bitchat peerID：SHA256(Noise 公钥) 前 8 字节，16 个 hex 字符。</summary>
        public string BitchatPeerId;
        /// <summary>bitchat 身份指纹：SHA256(签名公钥)，64 hex 字符。</summary>
        public string BitchatFingerprint;

        /// <summary>Mchat 局域网 peerId（12 hex）。</summary>
        public string MchatId;

        public int Generation;
        public long CreatedAt;

        /// <summary>由三份种子构造身份；MchatId 为空时按签名公钥派生。</summary>
        public static LocalIdentity Create(byte[] qaulSeed, byte[] noiseSeed, byte[] signSeed,
                                           string MchatId = null)
        {
            var id = new LocalIdentity
            {
                QaulSeed = Clone32(qaulSeed),
                NoiseSeed = Clone32(noiseSeed),
                SignSeed = Clone32(signSeed)
            };
            id.Rebuild(MchatId);
            return id;
        }

        public static LocalIdentity Generate(string MchatId = null)
        {
            return Create(Curve25519.RandomSeed(), Curve25519.RandomSeed(), Curve25519.RandomSeed(), MchatId);
        }

        /// <summary>重新计算所有派生值（换种子后调用）。</summary>
        public void Rebuild(string MchatId = null)
        {
            QaulPublic = Curve25519.Ed25519PublicKey(QaulSeed);
            NoisePublic = Curve25519.X25519PublicKey(NoiseSeed);
            SignPublic = Curve25519.Ed25519PublicKey(SignSeed);

            QaulKeyHex = Hex(QaulPublic);
            QaulPeerIdBytes = BuildQaulPeerId(QaulPublic);
            QaulPeerId = Base58.Encode(QaulPeerIdBytes);

            BitchatPeerId = Hex(Sha256(NoisePublic)).Substring(0, 16);
            BitchatFingerprint = Hex(Sha256(SignPublic));

            MchatId = !string.IsNullOrEmpty(MchatId) ? MchatId : DeriveMchatId(SignPublic);
        }

        /// <summary>
        /// libp2p 的 Ed25519 PeerId：identity multihash（0x00）+ 长度 0x24 +
        /// protobuf PublicKey{ Type=Ed25519(1), Data=公钥 }。qaul 的 id 就是它。
        /// </summary>
        public static byte[] BuildQaulPeerId(byte[] edPublic)
        {
            // multihash = code(1) + length(1) + digest(36)
            // digest = protobuf PublicKey{ Type=Ed25519, Data=32B } = 0x08 0x01 0x12 0x20 + 32
            var bytes = new byte[38];
            bytes[0] = 0x00;   // identity multihash
            bytes[1] = 0x24;   // digest 长度 36
            bytes[2] = 0x08;   // protobuf field 1 (Type)
            bytes[3] = 0x01;   // Ed25519
            bytes[4] = 0x12;   // protobuf field 2 (Data)
            bytes[5] = 0x20;   // 32 字节
            Buffer.BlockCopy(edPublic, 0, bytes, 6, 32);
            return bytes;
        }

        public static string DeriveMchatId(byte[] signPublic)
        {
            var input = Encoding.UTF8.GetBytes("Mchat/peer/v1:" + Hex(signPublic));
            return Hex(Sha256(input)).Substring(0, 12);
        }

        /// <summary>指纹分组显示：AB12 CD34 EF56 7890 …（便于人眼比对）。</summary>
        public static string Grouped(string hex, int groupSize = 4, int maxGroups = 8)
        {
            if (string.IsNullOrEmpty(hex)) return "";
            var sb = new StringBuilder();
            int groups = 0;
            for (int i = 0; i + groupSize <= hex.Length && groups < maxGroups; i += groupSize, groups++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(hex.Substring(i, groupSize).ToUpperInvariant());
            }
            if (groups * groupSize < hex.Length) sb.Append(" …");
            return sb.ToString();
        }

        /// <summary>给人看的摘要（原生弹窗 / 网页端共用）。</summary>
        public object ToSummary()
        {
            return new
            {
                MchatId = MchatId,
                generation = Generation,
                createdAt = CreatedAt,
                qaul = new
                {
                    peerId = QaulPeerId,
                    keyHex = QaulKeyHex,
                    fingerprint = Grouped(QaulKeyHex),
                    algo = "ed25519"
                },
                bitchat = new
                {
                    peerId = BitchatPeerId,
                    fingerprint = BitchatFingerprint,
                    fingerprintGrouped = Grouped(BitchatFingerprint),
                    noiseKeyHex = Hex(NoisePublic),
                    signingKeyHex = Hex(SignPublic),
                    algo = "x25519+ed25519"
                }
            };
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data ?? new byte[0]);
        }

        public static string Hex(byte[] data)
        {
            if (data == null) return "";
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] Unhex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                try { bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16); }
                catch { return null; }
            }
            return bytes;
        }

        private static byte[] Clone32(byte[] seed)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            var copy = new byte[32];
            Buffer.BlockCopy(seed, 0, copy, 0, 32);
            return copy;
        }
    }
}

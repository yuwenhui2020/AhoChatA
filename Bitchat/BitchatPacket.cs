using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AhoChatA
{
    /// <summary>bitchat 的线上消息类型（MessageType.swift）。</summary>
    public static class BitchatType
    {
        public const byte Announce = 0x01;
        public const byte Message = 0x02;
        public const byte Leave = 0x03;
        public const byte CourierEnvelope = 0x04;
        public const byte NoiseHandshake = 0x10;
        public const byte NoiseEncrypted = 0x11;
        public const byte Fragment = 0x20;
        public const byte FileTransfer = 0x22;
        public const byte GroupMessage = 0x25;
        public const byte Ping = 0x26;
        public const byte Pong = 0x27;
        public const byte AnnounceV2 = 0x2C;
    }

    /// <summary>
    /// bitchat 的二进制包格式（BinaryProtocol.swift）。
    ///
    /// 固定头 14 字节（v1）：
    ///   version(1) type(1) ttl(1) timestamp(8, big-endian) flags(1) payloadLength(2, big-endian)
    /// 随后：senderID(8) [recipientID(8)] payload [signature(64)]
    /// flags: bit0=有 recipient，bit1=有签名，bit2=payload 被 zlib 压缩。
    ///
    /// 有了这一层 + 下面的 Announce TLV，本应用就具备了与 bitchat 一致的
    /// "身份 + 公告包"接口；真正的 BLE GATT mesh 传输不在本次范围内
    /// （需要 Android BLE 广播/连接与分片重组，属于另一块工程量）。
    /// </summary>
    public class BitchatPacket
    {
        public const int V1HeaderSize = 14;
        public const int SenderIdSize = 8;
        public const int RecipientIdSize = 8;
        public const int SignatureSize = 64;
        public const int MaxPayload = 65535;

        public byte Version = 1;
        public byte Type;
        public byte Ttl = 7;
        public ulong Timestamp;
        public byte[] SenderId = new byte[0];
        public byte[] RecipientId;
        public byte[] Payload = new byte[0];
        public byte[] Signature;
        public bool Compressed;

        public byte[] Encode(bool sign = false, byte[] signSeed = null)
        {
            var payload = Payload ?? new byte[0];
            bool compress = Compressed;
            if (compress && payload.Length > 0) payload = ZlibCompress(payload);

            bool hasRecipient = RecipientId != null && RecipientId.Length == RecipientIdSize;
            byte[] signature = Signature;
            if (sign && signSeed != null)
            {
                // 签名不含 signature 与 ttl（ttl 在转发时会变）
                signature = Curve25519.Ed25519Sign(signSeed, EncodeForSigning(hasRecipient, compress, payload));
            }
            bool hasSignature = signature != null && signature.Length == SignatureSize;

            byte flags = 0;
            if (hasRecipient) flags |= 0x01;
            if (hasSignature) flags |= 0x02;
            if (compress) flags |= 0x04;

            using (var ms = new MemoryStream())
            {
                ms.WriteByte(Version);
                ms.WriteByte(Type);
                ms.WriteByte(Ttl);
                WriteU64Be(ms, Timestamp);
                ms.WriteByte(flags);

                int len = payload.Length;
                if (len > MaxPayload) throw new ArgumentException("payload too large");
                ms.WriteByte((byte)((len >> 8) & 0xFF));
                ms.WriteByte((byte)(len & 0xFF));

                var sender = new byte[SenderIdSize];
                if (SenderId != null) Buffer.BlockCopy(SenderId, 0, sender, 0, Math.Min(SenderIdSize, SenderId.Length));
                ms.Write(sender, 0, SenderIdSize);

                if (hasRecipient) ms.Write(RecipientId, 0, RecipientIdSize);
                if (len > 0) ms.Write(payload, 0, payload.Length);
                if (hasSignature) ms.Write(signature, 0, SignatureSize);

                return ms.ToArray();
            }
        }

        /// <summary>参与签名的字节：无签名、ttl 固定 0。</summary>
        public byte[] EncodeForSigning(bool hasRecipient, bool compressed, byte[] payload)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(Version);
                ms.WriteByte(Type);
                ms.WriteByte(0);            // ttl 固定 0
                WriteU64Be(ms, Timestamp);
                byte flags = 0;
                if (hasRecipient) flags |= 0x01;
                if (compressed) flags |= 0x04;
                ms.WriteByte(flags);
                int len = payload == null ? 0 : payload.Length;
                ms.WriteByte((byte)((len >> 8) & 0xFF));
                ms.WriteByte((byte)(len & 0xFF));

                var sender = new byte[SenderIdSize];
                if (SenderId != null) Buffer.BlockCopy(SenderId, 0, sender, 0, Math.Min(SenderIdSize, SenderId.Length));
                ms.Write(sender, 0, SenderIdSize);
                if (hasRecipient) ms.Write(RecipientId, 0, RecipientIdSize);
                if (len > 0) ms.Write(payload, 0, payload.Length);
                return ms.ToArray();
            }
        }

        public static BitchatPacket Decode(byte[] data)
        {
            if (data == null || data.Length < V1HeaderSize + SenderIdSize) return null;

            var p = new BitchatPacket();
            p.Version = data[0];
            p.Type = data[1];
            p.Ttl = data[2];
            p.Timestamp = ReadU64Be(data, 3);
            byte flags = data[11];
            int length = (data[12] << 8) | data[13];

            int offset = V1HeaderSize;
            p.SenderId = new byte[SenderIdSize];
            Buffer.BlockCopy(data, offset, p.SenderId, 0, SenderIdSize);
            offset += SenderIdSize;

            if ((flags & 0x01) != 0)
            {
                if (offset + RecipientIdSize > data.Length) return null;
                p.RecipientId = new byte[RecipientIdSize];
                Buffer.BlockCopy(data, offset, p.RecipientId, 0, RecipientIdSize);
                offset += RecipientIdSize;
            }

            if (offset + length > data.Length) return null;
            p.Payload = new byte[length];
            Buffer.BlockCopy(data, offset, p.Payload, 0, length);
            offset += length;

            if ((flags & 0x02) != 0)
            {
                if (offset + SignatureSize > data.Length) return null;
                p.Signature = new byte[SignatureSize];
                Buffer.BlockCopy(data, offset, p.Signature, 0, SignatureSize);
                offset += SignatureSize;
            }

            p.Compressed = (flags & 0x04) != 0;
            if (p.Compressed) p.Payload = ZlibDecompress(p.Payload);
            return p;
        }

        /// <summary>用发送者签名公钥校验（bitchat 用 Ed25519）。</summary>
        public bool Verify(byte[] signPublicKey)
        {
            if (Signature == null || Signature.Length != SignatureSize) return false;
            var payload = Compressed ? ZlibDecompress(Payload) : Payload;
            var signed = EncodeForSigning(RecipientId != null, Compressed, payload);
            return Curve25519.Ed25519Verify(signPublicKey, signed, Signature);
        }

        /* ---------- 工具 ---------- */

        private static void WriteU64Be(Stream s, ulong v)
        {
            for (int i = 7; i >= 0; i--) s.WriteByte((byte)((v >> (i * 8)) & 0xFF));
        }

        private static ulong ReadU64Be(byte[] d, int offset)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | d[offset + i];
            return v;
        }

        /// <summary>zlib 压缩（2 字节头 + deflate + adler32），与 bitchat 的 zlib 一致。</summary>
        public static byte[] ZlibCompress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                uint a = 1, b = 0;
                foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                output.WriteByte((byte)((adler >> 24) & 0xFF));
                output.WriteByte((byte)((adler >> 16) & 0xFF));
                output.WriteByte((byte)((adler >> 8) & 0xFF));
                output.WriteByte((byte)(adler & 0xFF));
                return output.ToArray();
            }
        }

        public static byte[] ZlibDecompress(byte[] data)
        {
            if (data == null || data.Length < 2) return new byte[0];
            try
            {
                using (var input = new MemoryStream(data, 2, data.Length - 2))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch
            {
                return data;
            }
        }
    }

    /// <summary>
    /// bitchat 的 announce 包（Packets.swift → AnnouncementPacket）。
    /// 载荷是 TLV 序列：type(1) length(1) value(n)
    ///   0x01 昵称 / 0x02 Noise 公钥(32) / 0x03 签名公钥(32)
    ///   0x04 直连邻居(8*n) / 0x05 能力位 / 0x06 桥接 geohash
    /// </summary>
    public class BitchatAnnounce
    {
        public string Nickname = "";
        public byte[] NoisePublicKey = new byte[32];
        public byte[] SigningPublicKey = new byte[32];
        public List<byte[]> DirectNeighbors = new List<byte[]>();
        public byte[] Capabilities;
        public string BridgeGeohash;

        public byte[] Encode()
        {
            using (var ms = new MemoryStream())
            {
                WriteTlv(ms, 0x01, Encoding.UTF8.GetBytes(Nickname ?? ""));
                WriteTlv(ms, 0x02, NoisePublicKey);
                WriteTlv(ms, 0x03, SigningPublicKey);

                if (DirectNeighbors != null && DirectNeighbors.Count > 0)
                {
                    var buf = new List<byte>();
                    foreach (var n in DirectNeighbors)
                        if (n != null && n.Length == 8) buf.AddRange(n);
                    if (buf.Count > 0) WriteTlv(ms, 0x04, buf.ToArray());
                }

                if (Capabilities != null && Capabilities.Length > 0) WriteTlv(ms, 0x05, Capabilities);
                if (!string.IsNullOrEmpty(BridgeGeohash)) WriteTlv(ms, 0x06, Encoding.UTF8.GetBytes(BridgeGeohash));
                return ms.ToArray();
            }
        }

        public static BitchatAnnounce Decode(byte[] payload)
        {
            if (payload == null) return null;
            var a = new BitchatAnnounce();
            bool hasNickname = false, hasNoise = false, hasSign = false;
            int offset = 0;

            while (offset + 2 <= payload.Length)
            {
                byte type = payload[offset++];
                int len = payload[offset++];
                if (offset + len > payload.Length) return null;
                var value = new byte[len];
                Buffer.BlockCopy(payload, offset, value, 0, len);
                offset += len;

                switch (type)
                {
                    case 0x01:
                        a.Nickname = Encoding.UTF8.GetString(value);
                        hasNickname = true;
                        break;
                    case 0x02:
                        a.NoisePublicKey = value;
                        hasNoise = true;
                        break;
                    case 0x03:
                        a.SigningPublicKey = value;
                        hasSign = true;
                        break;
                    case 0x04:
                        for (int i = 0; i + 8 <= value.Length; i += 8)
                        {
                            var n = new byte[8];
                            Buffer.BlockCopy(value, i, n, 0, 8);
                            a.DirectNeighbors.Add(n);
                        }
                        break;
                    case 0x05:
                        a.Capabilities = value;
                        break;
                    case 0x06:
                        a.BridgeGeohash = Encoding.UTF8.GetString(value);
                        break;
                }
            }

            return (hasNickname && hasNoise && hasSign) ? a : null;
        }

        /// <summary>由 announce 里的 Noise 公钥推导 bitchat peerID（与对方/本机算法一致）。</summary>
        public string PeerId
        {
            get
            {
                if (NoisePublicKey == null || NoisePublicKey.Length != 32) return "";
                return LocalIdentity.Hex(LocalIdentity.Sha256(NoisePublicKey)).Substring(0, 16);
            }
        }

        /// <summary>bitchat 身份指纹：签名公钥的 SHA256。</summary>
        public string Fingerprint
        {
            get
            {
                if (SigningPublicKey == null || SigningPublicKey.Length != 32) return "";
                return LocalIdentity.Hex(LocalIdentity.Sha256(SigningPublicKey));
            }
        }

        private static void WriteTlv(Stream s, byte type, byte[] value)
        {
            if (value == null) return;
            if (value.Length > 255) value = Curve25519.Slice(value, 0, 255);
            s.WriteByte(type);
            s.WriteByte((byte)value.Length);
            s.Write(value, 0, value.Length);
        }
    }
}

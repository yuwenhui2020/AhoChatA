using System;
using System.IO;

namespace AhoChatA
{
    /// <summary>
    /// HKDF-SHA256（RFC 5869）。
    ///
    /// libp2p 的 Noise 握手全程用它做 mix_key / Split，所以必须与 RFC 逐字节一致。
    /// </summary>
    public static class Hkdf
    {
        public static byte[] Extract(byte[] salt, byte[] ikm)
        {
            // salt 为空时按 RFC 用全零
            var s = (salt == null || salt.Length == 0) ? new byte[32] : salt;
            using (var h = new System.Security.Cryptography.HMACSHA256())
            {
                h.Key = s;
                return h.ComputeHash(ikm ?? new byte[0]);
            }
        }

        public static byte[] Expand(byte[] prk, byte[] info, int length)
        {
            var okm = new byte[length];
            var t = new byte[0];
            int pos = 0;
            byte counter = 1;

            using (var h = new System.Security.Cryptography.HMACSHA256())
            {
                h.Key = prk;
                while (pos < length)
                {
                    var input = new byte[t.Length + (info == null ? 0 : info.Length) + 1];
                    Buffer.BlockCopy(t, 0, input, 0, t.Length);
                    if (info != null && info.Length > 0)
                        Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
                    input[input.Length - 1] = counter;

                    t = h.ComputeHash(input);
                    var take = Math.Min(t.Length, length - pos);
                    Buffer.BlockCopy(t, 0, okm, pos, take);
                    pos += take;
                    counter++;
                }
            }
            return okm;
        }

        /// <summary>HKDF(ck, ikm) → 32 字节（Noise 的 mix_key）。</summary>
        public static byte[] Derive(byte[] chainingKey, byte[] inputKeyMaterial)
        {
            var prk = Extract(chainingKey, inputKeyMaterial);
            return Expand(prk, new byte[0], 32);
        }

        /// <summary>
        /// Noise 的 mix_key：一次 HKDF 输出两段 32 字节
        /// （新的 chaining key + 本轮 AEAD 密钥），顺序不可颠倒。
        /// </summary>
        public static void DeriveTwo(byte[] chainingKey, byte[] inputKeyMaterial,
                                     out byte[] newChainingKey, out byte[] tempKey)
        {
            var prk = Extract(chainingKey, inputKeyMaterial);
            var okm = Expand(prk, new byte[0], 64);
            newChainingKey = new byte[32];
            tempKey = new byte[32];
            Buffer.BlockCopy(okm, 0, newChainingKey, 0, 32);
            Buffer.BlockCopy(okm, 32, tempKey, 0, 32);
        }

        /// <summary>Noise 的 Split：从 chaining key 派生两个方向密钥。</summary>
        public static void Split(byte[] chainingKey, out byte[] k1, out byte[] k2)
        {
            var prk = Extract(chainingKey, new byte[0]);
            var okm = Expand(prk, new byte[0], 64);
            k1 = new byte[32];
            k2 = new byte[32];
            Buffer.BlockCopy(okm, 0, k1, 0, 32);
            Buffer.BlockCopy(okm, 32, k2, 0, 32);
        }
    }

    /// <summary>
    /// ChaCha20-Poly1305 AEAD（RFC 8439）。
    ///
    /// 为什么不用系统实现：Mono/Android 的 BCL 没有 ChaCha20-Poly1305，
    /// 而 libp2p 的 Noise 只用这一种 AEAD，纯托管实现约 300 行、可验证性强。
    /// </summary>
    public static class ChaCha20Poly1305
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;

        public static byte[] Encrypt(byte[] key, ulong nonce, byte[] plaintext, byte[] aad)
        {
            var nonceBytes = NonceFromCounter(nonce);
            return Seal(key, nonceBytes, plaintext, aad);
        }

        public static byte[] Decrypt(byte[] key, ulong nonce, byte[] ciphertextWithTag, byte[] aad)
        {
            var nonceBytes = NonceFromCounter(nonce);
            return Open(key, nonceBytes, ciphertextWithTag, aad);
        }

        /// <summary>Noise 用 64 位小端计数器作为 nonce，前 4 字节为 0。</summary>
        public static byte[] NonceFromCounter(ulong counter)
        {
            var n = new byte[12];
            for (int i = 0; i < 8; i++) n[4 + i] = (byte)(counter >> (8 * i));
            return n;
        }

        /* ================= 公开的 RFC 8439 接口 ================= */

        public static byte[] Seal(byte[] key, byte[] nonce12, byte[] plaintext, byte[] aad)
        {
            Check(key, nonce12);
            plaintext = plaintext ?? new byte[0];
            aad = aad ?? new byte[0];

            // ChaChaBlock 输出 64 字节，Poly1305 的 key 只是前 32 字节
            var block0 = new byte[64];
            ChaChaBlock(key, nonce12, 0, block0);
            var polyKey = new byte[32];
            Buffer.BlockCopy(block0, 0, polyKey, 0, 32);

            var ct = new byte[plaintext.Length];
            ChaChaXor(key, nonce12, 1, plaintext, ct);

            var mac = MacData(aad, ct);
            var tag = Poly1305(polyKey, mac);

            var outBuf = new byte[ct.Length + TagSize];
            Buffer.BlockCopy(ct, 0, outBuf, 0, ct.Length);
            Buffer.BlockCopy(tag, 0, outBuf, ct.Length, TagSize);
            return outBuf;
        }

        public static byte[] Open(byte[] key, byte[] nonce12, byte[] ciphertextWithTag, byte[] aad)
        {
            Check(key, nonce12);
            if (ciphertextWithTag == null || ciphertextWithTag.Length < TagSize) return null;
            aad = aad ?? new byte[0];

            int ctLen = ciphertextWithTag.Length - TagSize;
            var ct = new byte[ctLen];
            Buffer.BlockCopy(ciphertextWithTag, 0, ct, 0, ctLen);

            var block0 = new byte[64];
            ChaChaBlock(key, nonce12, 0, block0);
            var polyKey = new byte[32];
            Buffer.BlockCopy(block0, 0, polyKey, 0, 32);

            var mac = MacData(aad, ct);
            var want = Poly1305(polyKey, mac);

            int diff = 0;
            for (int i = 0; i < TagSize; i++)
                diff |= want[i] ^ ciphertextWithTag[ctLen + i];
            if (diff != 0) return null;   // 认证失败（常量时间比较）

            var pt = new byte[ctLen];
            ChaChaXor(key, nonce12, 1, ct, pt);
            return pt;
        }

        private static void Check(byte[] key, byte[] nonce)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("ChaCha20 key must be 32 bytes");
            if (nonce == null || nonce.Length != NonceSize) throw new ArgumentException("ChaCha20 nonce must be 12 bytes");
        }

        /// <summary>AEAD 的 MAC 输入：aad‖pad‖ct‖pad‖len(aad)‖len(ct)（小端 u64）。</summary>
        private static byte[] MacData(byte[] aad, byte[] ct)
        {
            int aadPad = (16 - (aad.Length % 16)) % 16;
            int ctPad = (16 - (ct.Length % 16)) % 16;
            var buf = new byte[aad.Length + aadPad + ct.Length + ctPad + 16];
            int p = 0;
            Buffer.BlockCopy(aad, 0, buf, p, aad.Length); p += aad.Length + aadPad;
            Buffer.BlockCopy(ct, 0, buf, p, ct.Length); p += ct.Length + ctPad;
            ulong al = (ulong)aad.Length, cl = (ulong)ct.Length;
            for (int i = 0; i < 8; i++) buf[p + i] = (byte)(al >> (8 * i));
            for (int i = 0; i < 8; i++) buf[p + 8 + i] = (byte)(cl >> (8 * i));
            return buf;
        }

        /* ================= ChaCha20 ================= */

        private static void ChaChaXor(byte[] key, byte[] nonce, uint counter, byte[] input, byte[] output)
        {
            var block = new byte[64];
            int offset = 0;
            while (offset < input.Length)
            {
                ChaChaBlock(key, nonce, counter, block);
                counter++;
                int n = Math.Min(64, input.Length - offset);
                for (int i = 0; i < n; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
                offset += n;
            }
        }

        private static void ChaChaBlock(byte[] key, byte[] nonce, uint counter, byte[] outBlock)
        {
            uint[] st = new uint[16];
            st[0] = 0x61707865; st[1] = 0x3320646e; st[2] = 0x79622d32; st[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) st[4 + i] = LE32(key, i * 4);
            st[12] = counter;
            for (int i = 0; i < 3; i++) st[13 + i] = LE32(nonce, i * 4);

            var x = (uint[])st.Clone();
            for (int round = 0; round < 10; round++)
            {
                Quarter(x, 0, 4, 8, 12);
                Quarter(x, 1, 5, 9, 13);
                Quarter(x, 2, 6, 10, 14);
                Quarter(x, 3, 7, 11, 15);
                Quarter(x, 0, 5, 10, 15);
                Quarter(x, 1, 6, 11, 12);
                Quarter(x, 2, 7, 8, 13);
                Quarter(x, 3, 4, 9, 14);
            }

            for (int i = 0; i < 16; i++)
            {
                uint v = x[i] + st[i];
                outBlock[i * 4] = (byte)v;
                outBlock[i * 4 + 1] = (byte)(v >> 8);
                outBlock[i * 4 + 2] = (byte)(v >> 16);
                outBlock[i * 4 + 3] = (byte)(v >> 24);
            }
        }

        private static uint LE32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        private static void Quarter(uint[] x, int a, int b, int c, int d)
        {
            unchecked
            {
                x[a] += x[b]; x[d] ^= x[a]; x[d] = Rotl(x[d], 16);
                x[c] += x[d]; x[b] ^= x[c]; x[b] = Rotl(x[b], 12);
                x[a] += x[b]; x[d] ^= x[a]; x[d] = Rotl(x[d], 8);
                x[c] += x[d]; x[b] ^= x[c]; x[b] = Rotl(x[b], 7);
            }
        }

        private static uint Rotl(uint v, int n) { return (v << n) | (v >> (32 - n)); }

        /// <summary>
        /// ChaCha20 单块 keystream（64 字节）。QUIC 头保护用：
        /// RFC 9001 §5.4.4 把样本前 4 字节当 counter、后 12 字节当 nonce，加密 5 个零字节。
        /// </summary>
        public static byte[] ChaCha20Block(byte[] key, byte[] nonce12, uint counter)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("key32");
            if (nonce12 == null || nonce12.Length != 12) throw new ArgumentException("nonce12");
            var block = new byte[64];
            ChaChaBlock(key, nonce12, counter, block);
            return block;
        }

        /* ================= Poly1305（26 位肢，5 肢） ================= */

        public static byte[] Poly1305(byte[] key32, byte[] message)
        {
            // r 的低 128 位，按 RFC 要求 clamp
            uint r0 = (uint)(key32[0] | key32[1] << 8 | key32[2] << 16 | key32[3] << 24);
            uint r1 = (uint)(key32[4] | key32[5] << 8 | key32[6] << 16 | key32[7] << 24);
            uint r2 = (uint)(key32[8] | key32[9] << 8 | key32[10] << 16 | key32[11] << 24);
            uint r3 = (uint)(key32[12] | key32[13] << 8 | key32[14] << 16 | key32[15] << 24);

            const uint mask26 = 0x3ffffff;
            ulong t0 = r0 & mask26;
            ulong t1 = ((r0 >> 26) | (r1 << 6)) & mask26;
            ulong t2 = ((r1 >> 20) | (r2 << 12)) & mask26;
            ulong t3 = ((r2 >> 14) | (r3 << 18)) & mask26;
            ulong t4 = (r3 >> 8) & mask26;

            // clamp
            t0 &= 0x3ffffff;                                  // r0 & 0x3ffffff
            t1 &= 0x3ffff03;                                  // hmm 用标准 clamp 位
            t2 &= 0x3ffc0ff;
            t3 &= 0x3f03fff;
            t4 &= 0x00fffff;

            ulong h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

            int off = 0;
            var block = new byte[17];
            while (off < message.Length)
            {
                int n = Math.Min(16, message.Length - off);
                Array.Clear(block, 0, block.Length);
                Buffer.BlockCopy(message, off, block, 0, n);
                block[n] = 1;                     // 追加 0x01（不足 16 字节也一样）

                // 必须先转 uint 再转 ulong：`byte << 24` 会变成负数，
                // 直接 (ulong) 会符号扩展成 0xFFFF…，Poly1305 立刻算错。
                ulong m0 = (uint)(block[0] | block[1] << 8 | block[2] << 16 | block[3] << 24);
                ulong m1 = (uint)(block[4] | block[5] << 8 | block[6] << 16 | block[7] << 24);
                ulong m2 = (uint)(block[8] | block[9] << 8 | block[10] << 16 | block[11] << 24);
                ulong m3 = (uint)(block[12] | block[13] << 8 | block[14] << 16 | block[15] << 24);

                h0 += m0 & mask26;
                h1 += ((m0 >> 26) | (m1 << 6)) & mask26;
                h2 += ((m1 >> 20) | (m2 << 12)) & mask26;
                h3 += ((m2 >> 14) | (m3 << 18)) & mask26;
                h4 += (m3 >> 8) | ((ulong)block[16] << 24);

                unchecked
                {
                    ulong d0 = h0 * t0 + h1 * (t4 * 5) + h2 * (t3 * 5) + h3 * (t2 * 5) + h4 * (t1 * 5);
                    ulong d1 = h0 * t1 + h1 * t0 + h2 * (t4 * 5) + h3 * (t3 * 5) + h4 * (t2 * 5);
                    ulong d2 = h0 * t2 + h1 * t1 + h2 * t0 + h3 * (t4 * 5) + h4 * (t3 * 5);
                    ulong d3 = h0 * t3 + h1 * t2 + h2 * t1 + h3 * t0 + h4 * (t4 * 5);
                    ulong d4 = h0 * t4 + h1 * t3 + h2 * t2 + h3 * t1 + h4 * t0;

                    uint c;
                    c = (uint)(d0 >> 26); h0 = d0 & mask26;
                    d1 += c; c = (uint)(d1 >> 26); h1 = d1 & mask26;
                    d2 += c; c = (uint)(d2 >> 26); h2 = d2 & mask26;
                    d3 += c; c = (uint)(d3 >> 26); h3 = d3 & mask26;
                    d4 += c; c = (uint)(d4 >> 26); h4 = d4 & mask26;
                    h0 += c * 5; c = (uint)(h0 >> 26); h0 &= mask26; h1 += c;
                }

                off += n;
            }

            // 进位收尾
            uint carry;
            carry = (uint)(h1 >> 26); h1 &= mask26; h2 += carry;
            carry = (uint)(h2 >> 26); h2 &= mask26; h3 += carry;
            carry = (uint)(h3 >> 26); h3 &= mask26; h4 += carry;
            carry = (uint)(h4 >> 26); h4 &= mask26; h0 += carry * 5;
            carry = (uint)(h0 >> 26); h0 &= mask26; h1 += carry;

            // 计算 h - p，若 h >= p 则用结果
            ulong g0 = h0 + 5; carry = (uint)(g0 >> 26); g0 &= mask26;
            ulong g1 = h1 + carry; carry = (uint)(g1 >> 26); g1 &= mask26;
            ulong g2 = h2 + carry; carry = (uint)(g2 >> 26); g2 &= mask26;
            ulong g3 = h3 + carry; carry = (uint)(g3 >> 26); g3 &= mask26;
            ulong g4 = h4 + carry - (1UL << 26);

            bool useG = (g4 & (1UL << 63)) == 0;   // g4 未下溢 ⇒ h >= p
            if (useG) { h0 = g0; h1 = g1; h2 = g2; h3 = g3; h4 = g4 & mask26; }

            ulong f0 = (h0 | (h1 << 26)) & 0xffffffffUL;
            ulong f1 = ((h1 >> 6) | (h2 << 20)) & 0xffffffffUL;
            ulong f2 = ((h2 >> 12) | (h3 << 14)) & 0xffffffffUL;
            ulong f3 = ((h3 >> 18) | (h4 << 8)) & 0xffffffffUL;

            // 加上 key 的高 128 位（s）
            unchecked
            {
                ulong s0 = BitConverter.ToUInt32(key32, 16);
                ulong s1 = BitConverter.ToUInt32(key32, 20);
                ulong s2 = BitConverter.ToUInt32(key32, 24);
                ulong s3 = BitConverter.ToUInt32(key32, 28);

                f0 += s0; f1 += s1 + (f0 >> 32); f0 &= 0xffffffffUL;
                f2 += s2 + (f1 >> 32); f1 &= 0xffffffffUL;
                f3 += s3 + (f2 >> 32); f2 &= 0xffffffffUL;
                f3 &= 0xffffffffUL;
            }

            var tag = new byte[16];
            WriteLE32(tag, 0, (uint)f0);
            WriteLE32(tag, 4, (uint)f1);
            WriteLE32(tag, 8, (uint)f2);
            WriteLE32(tag, 12, (uint)f3);
            return tag;
        }

        private static void WriteLE32(byte[] b, int off, uint v)
        {
            b[off] = (byte)v;
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
        }
    }
}

using System;

namespace AhoChatA
{
    /// <summary>
    /// AES 分组密码 + ECB / GCM 模式（纯 C#，无 BCL 依赖）。
    ///
    /// 为什么需要它：
    ///   · QUIC 的 Initial 包强制用 AES-128-GCM 保护（RFC 9001 §5.2），
    ///     头保护则是 AES-128-ECB 单块加密；
    ///   · TLS 1.3 的 TLS_AES_128_GCM_SHA256 套件。
    /// Xamarin.Android 的 BCL 不提供 AesGcm，引入 BouncyCastle 又会显著增大体积，
    /// 所以按 FIPS-197 / NIST SP 800-38D 手写一份。数据量小（握手几千字节），
    /// 位运算版 GHASH 足够快。
    /// </summary>
    public static class AesGcmCipher
    {
        /* ===================== AES 分组密码 ===================== */

        private static readonly byte[] Sbox =
        {
            0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
            0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
            0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
            0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
            0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
            0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
            0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
            0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
            0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
            0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
            0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
            0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
            0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
            0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
            0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
            0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16
        };

        private static readonly byte[] InvSbox = BuildInvSbox();

        private static readonly byte[] Rcon =
        {
            0x00,0x01,0x02,0x04,0x08,0x10,0x20,0x40,0x80,0x1b,0x36
        };

        private static byte[] BuildInvSbox()
        {
            var inv = new byte[256];
            for (int i = 0; i < 256; i++) inv[Sbox[i]] = (byte)i;
            return inv;
        }

        /// <summary>密钥调度：返回轮密钥（4*(Nr+1) 个字，按列存）。</summary>
        private static uint[] ExpandKey(byte[] key)
        {
            int nk = key.Length / 4;              // 4 (AES-128) 或 8 (AES-256)
            int nr = nk + 6;                      // 10 / 14
            var w = new uint[4 * (nr + 1)];

            for (int i = 0; i < nk; i++)
                w[i] = ((uint)key[4 * i] << 24) | ((uint)key[4 * i + 1] << 16) |
                       ((uint)key[4 * i + 2] << 8) | key[4 * i + 3];

            for (int i = nk; i < w.Length; i++)
            {
                uint t = w[i - 1];
                if (i % nk == 0)
                    t = SubWord(Rotl8(t)) ^ ((uint)Rcon[i / nk] << 24);
                else if (nk > 6 && i % nk == 4)
                    t = SubWord(t);
                w[i] = w[i - nk] ^ t;
            }
            return w;
        }

        private static uint SubWord(uint x)
        {
            return (uint)(Sbox[(x >> 24) & 0xff] << 24 | Sbox[(x >> 16) & 0xff] << 16 |
                          Sbox[(x >> 8) & 0xff] << 8 | Sbox[x & 0xff]);
        }

        private static uint Rotl8(uint x) { return (x << 8) | (x >> 24); }

        private static byte XTime(byte a)
        {
            return (byte)((a << 1) ^ ((a & 0x80) != 0 ? 0x1b : 0));
        }

        private static byte Mul(byte a, byte b)
        {
            byte r = 0;
            while (b != 0)
            {
                if ((b & 1) != 0) r ^= a;
                a = XTime(a);
                b >>= 1;
            }
            return r;
        }

        /// <summary>AES 加密一个 16 字节分组（就地）。</summary>
        private static void EncryptBlock(uint[] w, int nr, byte[] s, int off)
        {
            AddRoundKey(w, 0, s, off);
            for (int round = 1; round < nr; round++)
            {
                SubBytes(s, off, Sbox);
                ShiftRows(s, off);
                MixColumns(s, off);
                AddRoundKey(w, round, s, off);
            }
            SubBytes(s, off, Sbox);
            ShiftRows(s, off);
            AddRoundKey(w, nr, s, off);
        }

        /// <summary>AES 解密一个 16 字节分组（就地）。</summary>
        private static void DecryptBlock(uint[] w, int nr, byte[] s, int off)
        {
            AddRoundKey(w, nr, s, off);
            for (int round = nr - 1; round >= 1; round--)
            {
                InvShiftRows(s, off);
                SubBytes(s, off, InvSbox);
                AddRoundKey(w, round, s, off);
                InvMixColumns(s, off);
            }
            InvShiftRows(s, off);
            SubBytes(s, off, InvSbox);
            AddRoundKey(w, 0, s, off);
        }

        private static void AddRoundKey(uint[] w, int round, byte[] s, int off)
        {
            for (int c = 0; c < 4; c++)
            {
                s[off + 4 * c + 0] ^= (byte)(w[4 * round + c] >> 24);
                s[off + 4 * c + 1] ^= (byte)(w[4 * round + c] >> 16);
                s[off + 4 * c + 2] ^= (byte)(w[4 * round + c] >> 8);
                s[off + 4 * c + 3] ^= (byte)(w[4 * round + c]);
            }
        }

        private static void SubBytes(byte[] s, int off, byte[] box)
        {
            for (int i = 0; i < 16; i++) s[off + i] = box[s[off + i]];
        }

        private static void ShiftRows(byte[] s, int off)
        {
            // 状态按列存：s[4*c + r]
            byte t;
            t = s[off + 1]; s[off + 1] = s[off + 5]; s[off + 5] = s[off + 9]; s[off + 9] = s[off + 13]; s[off + 13] = t;
            t = s[off + 2]; s[off + 2] = s[off + 10]; s[off + 10] = t;
            t = s[off + 6]; s[off + 6] = s[off + 14]; s[off + 14] = t;
            t = s[off + 15]; s[off + 15] = s[off + 11]; s[off + 11] = s[off + 7]; s[off + 7] = s[off + 3]; s[off + 3] = t;
        }

        private static void InvShiftRows(byte[] s, int off)
        {
            byte t;
            t = s[off + 13]; s[off + 13] = s[off + 9]; s[off + 9] = s[off + 5]; s[off + 5] = s[off + 1]; s[off + 1] = t;
            t = s[off + 2]; s[off + 2] = s[off + 10]; s[off + 10] = t;
            t = s[off + 6]; s[off + 6] = s[off + 14]; s[off + 14] = t;
            t = s[off + 3]; s[off + 3] = s[off + 7]; s[off + 7] = s[off + 11]; s[off + 11] = s[off + 15]; s[off + 15] = t;
        }

        private static void MixColumns(byte[] s, int off)
        {
            for (int c = 0; c < 4; c++)
            {
                int i = off + 4 * c;
                byte a0 = s[i], a1 = s[i + 1], a2 = s[i + 2], a3 = s[i + 3];
                s[i]     = (byte)(XTime(a0) ^ (XTime(a1) ^ a1) ^ a2 ^ a3);
                s[i + 1] = (byte)(a0 ^ XTime(a1) ^ (XTime(a2) ^ a2) ^ a3);
                s[i + 2] = (byte)(a0 ^ a1 ^ XTime(a2) ^ (XTime(a3) ^ a3));
                s[i + 3] = (byte)((XTime(a0) ^ a0) ^ a1 ^ a2 ^ XTime(a3));
            }
        }

        private static void InvMixColumns(byte[] s, int off)
        {
            for (int c = 0; c < 4; c++)
            {
                int i = off + 4 * c;
                byte a0 = s[i], a1 = s[i + 1], a2 = s[i + 2], a3 = s[i + 3];
                s[i]     = (byte)(Mul(a0, 14) ^ Mul(a1, 11) ^ Mul(a2, 13) ^ Mul(a3, 9));
                s[i + 1] = (byte)(Mul(a0, 9)  ^ Mul(a1, 14) ^ Mul(a2, 11) ^ Mul(a3, 13));
                s[i + 2] = (byte)(Mul(a0, 13) ^ Mul(a1, 9)  ^ Mul(a2, 14) ^ Mul(a3, 11));
                s[i + 3] = (byte)(Mul(a0, 11) ^ Mul(a1, 13) ^ Mul(a2, 9)  ^ Mul(a3, 14));
            }
        }

        /* ===================== ECB ===================== */

        /// <summary>AES-ECB 加密单个分组（QUIC 头保护用）。</summary>
        public static byte[] EcbEncryptBlock(byte[] key, byte[] block16)
        {
            if (block16 == null || block16.Length != 16) throw new ArgumentException("block16");
            var w = ExpandKey(key);
            var outb = (byte[])block16.Clone();
            EncryptBlock(w, key.Length / 4 + 6, outb, 0);
            return outb;
        }

        /* ===================== GCM ===================== */

        /// <summary>AES-GCM 加密：返回 密文||tag（16 字节）。</summary>
        public static byte[] GcmEncrypt(byte[] key, byte[] nonce12, byte[] plaintext, byte[] aad)
        {
            if (nonce12 == null || nonce12.Length != 12) throw new ArgumentException("nonce12");
            var w = ExpandKey(key);
            int nr = key.Length / 4 + 6;

            // H = E(K, 0)
            var h = new byte[16];
            EncryptBlock(w, nr, h, 0);

            // J0 = nonce || 0x00000001
            var j0 = new byte[16];
            Buffer.BlockCopy(nonce12, 0, j0, 0, 12);
            j0[15] = 1;

            var ct = new byte[plaintext.Length];
            Buffer.BlockCopy(plaintext, 0, ct, 0, plaintext.Length);

            // CTR：计数器从 inc32(J0) 开始
            var ctr = Inc32(j0);
            var ks = new byte[16];
            for (int i = 0; i < ct.Length; i += 16)
            {
                Buffer.BlockCopy(ctr, 0, ks, 0, 16);
                EncryptBlock(w, nr, ks, 0);   // ks = E(K, ctr)
                int n = Math.Min(16, ct.Length - i);
                for (int j = 0; j < n; j++) ct[i + j] ^= ks[j];
                ctr = Inc32(ctr);
            }

            var s = Ghash(h, aad, ct);
            // tag = E(K, J0) XOR S
            var ekj0 = new byte[16];
            Buffer.BlockCopy(j0, 0, ekj0, 0, 16);
            EncryptBlock(w, nr, ekj0, 0);
            var tag = new byte[16];
            for (int i = 0; i < 16; i++) tag[i] = (byte)(ekj0[i] ^ s[i]);

            var result = new byte[ct.Length + 16];
            Buffer.BlockCopy(ct, 0, result, 0, ct.Length);
            Buffer.BlockCopy(tag, 0, result, ct.Length, 16);
            return result;
        }

        /// <summary>AES-GCM 解密：输入 密文||tag；校验失败返回 null。</summary>
        public static byte[] GcmDecrypt(byte[] key, byte[] nonce12, byte[] ciphertextWithTag, byte[] aad)
        {
            if (nonce12 == null || nonce12.Length != 12) throw new ArgumentException("nonce12");
            if (ciphertextWithTag == null || ciphertextWithTag.Length < 16) return null;

            int ctLen = ciphertextWithTag.Length - 16;
            var ct = new byte[ctLen];
            Buffer.BlockCopy(ciphertextWithTag, 0, ct, 0, ctLen);
            var tag = new byte[16];
            Buffer.BlockCopy(ciphertextWithTag, ctLen, tag, 0, 16);

            var w = ExpandKey(key);
            int nr = key.Length / 4 + 6;

            var h = new byte[16];
            EncryptBlock(w, nr, h, 0);

            var j0 = new byte[16];
            Buffer.BlockCopy(nonce12, 0, j0, 0, 12);
            j0[15] = 1;

            var s = Ghash(h, aad, ct);
            var ekj0 = new byte[16];
            Buffer.BlockCopy(j0, 0, ekj0, 0, 16);
            EncryptBlock(w, nr, ekj0, 0);
            var expected = new byte[16];
            for (int i = 0; i < 16; i++) expected[i] = (byte)(ekj0[i] ^ s[i]);

            bool ok = true;
            for (int i = 0; i < 16; i++) ok &= (expected[i] == tag[i]);
            if (!ok) return null;

            var pt = new byte[ctLen];
            Buffer.BlockCopy(ct, 0, pt, 0, ctLen);
            var ctr = Inc32(j0);
            var ks = new byte[16];
            for (int i = 0; i < pt.Length; i += 16)
            {
                Buffer.BlockCopy(ctr, 0, ks, 0, 16);
                EncryptBlock(w, nr, ks, 0);
                int n = Math.Min(16, pt.Length - i);
                for (int j = 0; j < n; j++) pt[i + j] ^= ks[j];
                ctr = Inc32(ctr);
            }
            return pt;
        }

        private static byte[] Inc32(byte[] counter)
        {
            var c = (byte[])counter.Clone();
            for (int i = 15; i >= 12; i--)
            {
                if (++c[i] != 0) break;
            }
            return c;
        }

        /// <summary>GHASH_H(A || pad || C || pad || [len(A)]64 || [len(C)]64)。</summary>
        private static byte[] Ghash(byte[] h, byte[] aad, byte[] ct)
        {
            var y = new byte[16];
            foreach (var block in Blocks(aad)) { XorInto(y, block); GhashMul(y, h); }
            foreach (var block in Blocks(ct)) { XorInto(y, block); GhashMul(y, h); }

            var lenBlock = new byte[16];
            WriteBe64(lenBlock, 0, (ulong)(aad == null ? 0 : aad.Length) * 8);
            WriteBe64(lenBlock, 8, (ulong)ct.Length * 8);
            XorInto(y, lenBlock);
            GhashMul(y, h);
            return y;
        }

        private static void XorInto(byte[] dst, byte[] src)
        {
            for (int i = 0; i < 16; i++) dst[i] ^= src[i];
        }

        /// <summary>
        /// GF(2^128) 乘法：a ← a · H（就地）。
        /// GCM 约定：X 的位从最高位（字节 0 的 bit7）开始用，V 初始为 H，
        /// 每次左移相当于乘 x，低位溢出时异或 R = 0xe1‖0^120。
        /// </summary>
        private static void GhashMul(byte[] a, byte[] h)
        {
            var z = new byte[16];
            var v = (byte[])h.Clone();
            for (int i = 0; i < 128; i++)
            {
                if ((a[i >> 3] & (0x80 >> (i & 7))) != 0)
                    for (int j = 0; j < 16; j++) z[j] ^= v[j];

                bool lsb = (v[15] & 1) != 0;
                ShiftRight(v);
                if (lsb) v[0] ^= 0xe1;
            }
            Buffer.BlockCopy(z, 0, a, 0, 16);
        }

        private static System.Collections.Generic.IEnumerable<byte[]> Blocks(byte[] data)
        {
            if (data == null || data.Length == 0) yield break;
            for (int i = 0; i < data.Length; i += 16)
            {
                var b = new byte[16];
                int n = Math.Min(16, data.Length - i);
                Buffer.BlockCopy(data, i, b, 0, n);
                yield return b;
            }
        }

        private static void WriteBe64(byte[] b, int off, ulong v)
        {
            for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (56 - 8 * i));
        }

        private static void ShiftRight(byte[] v)
        {
            for (int i = 15; i > 0; i--)
            {
                v[i] = (byte)((v[i] >> 1) | ((v[i - 1] & 1) << 7));
            }
            v[0] >>= 1;
        }
    }
}

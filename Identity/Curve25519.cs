using System;
using System.Numerics;
using System.Security.Cryptography;

namespace AhoChatA
{
    /// <summary>
    /// Curve25519 家族（Ed25519 签名 + X25519 密钥交换）的最小实现。
    ///
    /// 为什么要自己写：
    ///   · qaul 的身份就是 libp2p 的 Ed25519 密钥对，user_id = PeerId(Ed25519 公钥)；
    ///   · bitchat 的身份是 X25519 静态密钥（Noise 用）+ Ed25519 签名密钥，
    ///     它的 peerID = SHA256(X25519 公钥) 的前 8 字节。
    ///   Android/Xamarin 的 BCL 没有 Ed25519/X25519，引入 BouncyCastle 又会显著增大体积，
    ///   因此这里用 BigInteger 实现——只用于「生成身份 / 签名 / 验签」，调用频率极低
    ///   （毫秒级），换来的是与两款应用**格式完全一致**的身份指纹。
    ///
    /// 正确性由 RFC 8032 / RFC 7748 官方向量在 tests/QaulCheck 里逐条校验。
    /// </summary>
    public static class Curve25519
    {
        private static readonly BigInteger P = (BigInteger.One << 255) - 19;
        private static readonly BigInteger L =
            (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");

        private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));
        private static readonly BigInteger D2 = Mod(2 * D);
        private static readonly BigInteger SqrtM1 = ModPow(2, (P - 1) / 4);

        private static readonly BigInteger BaseX = BigInteger.Parse(
            "15112221349535400772501151409588531511454012693041857206046113283949847762202");
        private static readonly BigInteger BaseY = Mod(
            BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960"));

        private static readonly Point BasePoint = new Point(BaseX, BaseY, 1, Mod(BaseX * BaseY));

        /* ===================== 基础运算 ===================== */

        private static BigInteger Mod(BigInteger x)
        {
            var r = x % P;
            return r.Sign < 0 ? r + P : r;
        }

        private static BigInteger ModPow(BigInteger b, BigInteger e)
        {
            BigInteger result = 1;
            b = Mod(b);
            while (e > 0)
            {
                if (!e.IsEven) result = Mod(result * b);
                b = Mod(b * b);
                e >>= 1;
            }
            return result;
        }

        private static BigInteger ModInverse(BigInteger a)
        {
            return ModPow(a, P - 2);
        }

        private static BigInteger ModL(BigInteger x)
        {
            var r = x % L;
            return r.Sign < 0 ? r + L : r;
        }

        /* ===================== 点运算（扩展坐标，统一加法公式） ===================== */

        private struct Point
        {
            public BigInteger X, Y, Z, T;
            public Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t)
            {
                X = x; Y = y; Z = z; T = t;
            }
        }

        private static readonly Point Identity = new Point(0, 1, 1, 0);

        private static Point Add(Point p1, Point p2)
        {
            BigInteger a = Mod((p1.Y - p1.X) * (p2.Y - p2.X));
            BigInteger b = Mod((p1.Y + p1.X) * (p2.Y + p2.X));
            BigInteger c = Mod(p1.T * D2 * p2.T);
            BigInteger d = Mod(p1.Z * 2 * p2.Z);
            BigInteger e = b - a;
            BigInteger f = d - c;
            BigInteger g = d + c;
            BigInteger h = b + a;
            return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
        }

        private static Point ScalarMult(BigInteger scalar, Point pt)
        {
            var result = Identity;
            var addend = pt;
            // 注意：**不能**对标量取模 L。Ed25519 的私钥标量是钳位后的完整 256 位值
            // （≥ 2^254，通常大于 L），RFC 8032 要求直接用它做倍乘；
            // 约掉 L 会得到完全不同的点，公钥/签名全部跑偏。
            var k = scalar;
            while (k > 0)
            {
                if (!k.IsEven) result = Add(result, addend);
                addend = Add(addend, addend);
                k >>= 1;
            }
            return result;
        }

        private static BigInteger Mod(BigInteger x, BigInteger m)
        {
            var r = x % m;
            return r.Sign < 0 ? r + m : r;
        }

        /* ===================== 编码 / 解码 ===================== */

        private static byte[] EncodePoint(Point p)
        {
            var zinv = ModInverse(p.Z);
            var x = Mod(p.X * zinv);
            var y = Mod(p.Y * zinv);

            var bytes = ToLe32(y);
            if (!x.IsEven) bytes[31] |= 0x80;   // 最高位放 x 的奇偶
            return bytes;
        }

        private static byte[] ToLe32(BigInteger v)
        {
            var bytes = new byte[32];
            var tmp = v;
            for (int i = 0; i < 32; i++)
            {
                bytes[i] = (byte)(tmp & 0xFF);
                tmp >>= 8;
            }
            return bytes;
        }

        private static BigInteger FromLe(byte[] data, int offset, int length)
        {
            var v = BigInteger.Zero;
            for (int i = length - 1; i >= 0; i--) v = (v << 8) | data[offset + i];
            return v;
        }

        private static bool TryDecodePoint(byte[] encoded, out Point point)
        {
            point = Identity;
            if (encoded == null || encoded.Length != 32) return false;

            var yBytes = (byte[])encoded.Clone();
            bool xOdd = (yBytes[31] & 0x80) != 0;
            yBytes[31] &= 0x7F;

            var y = FromLe(yBytes, 0, 32);
            if (y >= P) return false;

            var y2 = Mod(y * y);
            var u = Mod(y2 - 1);
            var v = Mod(D * y2 + 1);
            var v3 = Mod(v * v * v);
            var v7 = Mod(v3 * v3 * v);

            var x = Mod(u * v3 * ModPow(Mod(u * v7), (P - 5) / 8));
            if (Mod(x * x * v - u).Sign != 0)
            {
                x = Mod(x * SqrtM1);
                if (Mod(x * x * v - u).Sign != 0) return false;
            }
            if (x.IsZero && xOdd) return false;
            // 编码里的最高位是 x 的奇偶：不一致就取负（mod p）
            if ((!x.IsEven) != xOdd) x = Mod(-x);

            point = new Point(x, y, 1, Mod(x * y));
            return true;
        }

        /* ===================== Ed25519 ===================== */

        private static byte[] Clamp(byte[] h)
        {
            var a = new byte[32];
            Buffer.BlockCopy(h, 0, a, 0, 32);
            a[0] &= 248;
            a[31] &= 127;
            a[31] |= 64;
            return a;
        }

        private static BigInteger LittleEndianToInt(byte[] data)
        {
            return FromLe(data, 0, data.Length);
        }

        /// <summary>由 32 字节种子推导 Ed25519 公钥（RFC 8032）。</summary>
        public static byte[] Ed25519PublicKey(byte[] seed)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            var h = SHA512.Create().ComputeHash(seed);
            var a = Clamp(h);
            var aInt = LittleEndianToInt(a);
            return EncodePoint(ScalarMult(aInt, BasePoint));
        }

        /// <summary>Ed25519 签名（RFC 8032 §5.1.6），返回 64 字节。</summary>
        public static byte[] Ed25519Sign(byte[] seed, byte[] message)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            message = message ?? new byte[0];

            var sha = SHA512.Create();
            var h = sha.ComputeHash(seed);
            var a = Clamp(h);

            var prefix = new byte[32];
            Buffer.BlockCopy(h, 32, prefix, 0, 32);

            var pubKey = EncodePoint(ScalarMult(LittleEndianToInt(a), BasePoint));

            // r = H(prefix || M) mod L
            var rInput = new byte[prefix.Length + message.Length];
            Buffer.BlockCopy(prefix, 0, rInput, 0, prefix.Length);
            Buffer.BlockCopy(message, 0, rInput, prefix.Length, message.Length);
            var r = ModL(LittleEndianToInt(sha.ComputeHash(rInput)));

            var rPoint = EncodePoint(ScalarMult(r, BasePoint));

            // k = H(R || A || M) mod L
            var kInput = new byte[32 + 32 + message.Length];
            Buffer.BlockCopy(rPoint, 0, kInput, 0, 32);
            Buffer.BlockCopy(pubKey, 0, kInput, 32, 32);
            Buffer.BlockCopy(message, 0, kInput, 64, message.Length);
            var k = ModL(LittleEndianToInt(sha.ComputeHash(kInput)));

            // S = (r + k*a) mod L
            var s = ModL(r + k * LittleEndianToInt(a));

            var signature = new byte[64];
            Buffer.BlockCopy(rPoint, 0, signature, 0, 32);
            var sBytes = ToLe32(s);
            Buffer.BlockCopy(sBytes, 0, signature, 32, 32);
            return signature;
        }

        /// <summary>Ed25519 验签。</summary>
        public static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != 32) return false;
            if (signature == null || signature.Length != 64) return false;
            message = message ?? new byte[0];

            Point a;
            if (!TryDecodePoint(publicKey, out a)) return false;

            var rBytes = new byte[32];
            Buffer.BlockCopy(signature, 0, rBytes, 0, 32);
            var s = LittleEndianToInt(Slice(signature, 32, 32));
            if (s >= L) return false;

            Point rPoint;
            if (!TryDecodePoint(rBytes, out rPoint)) return false;

            var kInput = new byte[64 + message.Length];
            Buffer.BlockCopy(rBytes, 0, kInput, 0, 32);
            Buffer.BlockCopy(publicKey, 0, kInput, 32, 32);
            Buffer.BlockCopy(message, 0, kInput, 64, message.Length);
            var k = ModL(LittleEndianToInt(SHA512.Create().ComputeHash(kInput)));

            var left = ScalarMult(s, BasePoint);
            var right = Add(rPoint, ScalarMult(k, a));

            var l = EncodePoint(left);
            var r = EncodePoint(right);
            return BytesEqual(l, r);
        }

        /* ===================== Ed25519 → X25519（qaul 的 KK 用） ===================== */

        /// <summary>
        /// Ed25519 私钥 → X25519（Montgomery）私钥。
        ///
        /// qaul 的 `Crypto25519::private_key_to_montgomery` 就是：
        ///   SHA512(seed) 取前 32 字节并 clamp。
        /// </summary>
        public static byte[] Ed25519ToX25519Private(byte[] seed)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            var h = SHA512.Create().ComputeHash(seed);
            return Clamp(h);
        }

        /// <summary>
        /// Ed25519 公钥 → X25519（Montgomery u 坐标）。
        ///
        /// 双有理映射：u = (1 + y) / (1 - y) mod p
        /// （对应 curve25519-dalek 的 `EdwardsPoint::to_montgomery`）。
        /// </summary>
        public static byte[] Ed25519ToX25519Public(byte[] edPublic)
        {
            Point p;
            if (!TryDecodePoint(edPublic, out p)) return null;

            var zinv = ModInverse(p.Z);
            var y = Mod(p.Y * zinv);
            var denom = Mod(1 - y);
            if (denom.Sign == 0) return null;
            var u = Mod((1 + y) * ModInverse(denom));
            return ToLe32(u);
        }

        /* ===================== X25519 ===================== */

        /// <summary>由 32 字节种子推导 X25519 公钥（RFC 7748）。</summary>
        public static byte[] X25519PublicKey(byte[] seed)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            var k = Clamp(seed);
            return X25519Core(k, BaseU());
        }

        /// <summary>X25519 共享密钥（Noise 握手用），双方应得到相同结果。</summary>
        public static byte[] X25519SharedSecret(byte[] seed, byte[] peerPublicKey)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            if (peerPublicKey == null || peerPublicKey.Length != 32) throw new ArgumentException("peer key must be 32 bytes");
            var k = Clamp(seed);
            return X25519Core(k, peerPublicKey);
        }

        private static byte[] BaseU()
        {
            var u = new byte[32];
            u[0] = 9;
            return u;
        }

        private static byte[] X25519Core(byte[] scalar, byte[] uBytes)
        {
            var k = (byte[])scalar.Clone();
            var u = (byte[])uBytes.Clone();
            u[31] &= 0x7F;   // 忽略最高位（RFC 7748）

            var kInt = FromLe(k, 0, 32);
            var x1 = Mod(FromLe(u, 0, 32));

            BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
            int swap = 0;

            for (int t = 254; t >= 0; t--)
            {
                int kt = (int)((kInt >> t) & 1);
                swap ^= kt;
                if (swap == 1) { Swap(ref x2, ref x3); Swap(ref z2, ref z3); }
                swap = kt;

                var a = Mod(x2 + z2);
                var aa = Mod(a * a);
                var b = Mod(x2 - z2);
                var bb = Mod(b * b);
                var e = Mod(aa - bb);
                var c = Mod(x3 + z3);
                var d = Mod(x3 - z3);
                var da = Mod(d * a);
                var cb = Mod(c * b);
                var s1 = Mod(da + cb);
                x3 = Mod(s1 * s1);
                var s2 = Mod(da - cb);
                z3 = Mod(x1 * Mod(s2 * s2));
                x2 = Mod(aa * bb);
                z2 = Mod(e * Mod(aa + Mod(121665 * e)));
            }

            if (swap == 1) { Swap(ref x2, ref x3); Swap(ref z2, ref z3); }

            var result = Mod(x2 * ModInverse(z2));
            return ToLe32(result);
        }

        private static void Swap(ref BigInteger a, ref BigInteger b)
        {
            var t = a; a = b; b = t;
        }

        /* ===================== 工具 ===================== */

        public static byte[] RandomSeed()
        {
            var seed = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(seed);
            return seed;
        }

        public static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static byte[] Slice(byte[] data, int offset, int length)
        {
            var outBytes = new byte[length];
            Buffer.BlockCopy(data, offset, outBytes, 0, length);
            return outBytes;
        }
    }
}

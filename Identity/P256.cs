using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// ECDSA P-256（secp256r1 / prime256v1）最小实现。
    ///
    /// 为什么需要它：libp2p 的 TLS 证书（rust-libp2p 用 rcgen 的
    /// PKCS_ECDSA_P256_SHA256）与 CertificateVerify 签名都是 ECDSA P-256，
    /// 我们作为客户端既要验签（服务器的 CertificateVerify、证书自签名），
    /// 也要签（我们自己的客户端证书与 CertificateVerify）。
    /// Xamarin.Android 的 BCL 不带 ECDsa 的 P-256 裸签名导出，故手写。
    ///
    /// 性能考量：只在建立连接时算几次标量乘法（各约几百次点运算），
    /// 用 BigInteger 完全够快，且比手写 4×64 位肢算术不易出错。
    /// </summary>
    public static class P256
    {
        // secp256r1 域参数（FIPS 186-4 §A.2.2 / SEC 2）
        private static readonly BigInteger P = Parse(
            "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
        private static readonly BigInteger N = Parse(
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
        private static readonly BigInteger B = Parse(
            "5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
        private static readonly BigInteger Gx = Parse(
            "6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
        private static readonly BigInteger Gy = Parse(
            "4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

        /// <summary>a = -3 mod p。</summary>
        private static readonly BigInteger A = P - 3;

        private static BigInteger Parse(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return BigEndian(b);
        }

        /* ===================== 点运算（Jacobian 坐标） ===================== */

        private struct Point
        {
            public BigInteger X, Y, Z;   // 仿射点 Z = 1；无穷远点 Z = 0
            public bool IsInfinity { get { return Z.Sign == 0; } }

            public static Point Affine(BigInteger x, BigInteger y)
            {
                return new Point { X = x, Y = y, Z = BigInteger.One };
            }

            public static readonly Point Infinity = new Point { X = BigInteger.One, Y = BigInteger.One, Z = BigInteger.Zero };
        }

        /// <summary>Jacobian 加法（P + Q），支持两倍/相同点（用倍加公式避免分支错误）。</summary>
        private static Point Add(Point p, Point q)
        {
            if (p.IsInfinity) return q;
            if (q.IsInfinity) return p;

            var z1z1 = Mod(p.Z * p.Z);
            var z2z2 = Mod(q.Z * q.Z);
            var u1 = Mod(p.X * z2z2);
            var u2 = Mod(q.X * z1z1);
            var s1 = Mod(p.Y * z2z2 * q.Z);
            var s2 = Mod(q.Y * z1z1 * p.Z);

            var h = Mod(u2 - u1);
            var r = Mod(s2 - s1);

            if (h.Sign == 0)
            {
                if (r.Sign == 0) return Double(p);
                return Point.Infinity;
            }

            var hh = Mod(h * h);
            var hhh = Mod(hh * h);
            var v = Mod(u1 * hh);
            var x3 = Mod(r * r - hhh - 2 * v);
            var y3 = Mod(r * (v - x3) - s1 * hhh);
            var z3 = Mod(p.Z * q.Z * h);
            return new Point { X = x3, Y = y3, Z = z3 };
        }

        private static Point Double(Point p)
        {
            if (p.IsInfinity || p.Y.Sign == 0) return Point.Infinity;

            var a = A;   // a = -3
            var delta = Mod(p.Z * p.Z);
            var gamma = Mod(p.Y * p.Y);
            var beta = Mod(p.X * gamma);
            var alpha = Mod(3 * Mod(p.X - delta) * Mod(p.X + delta));

            var x3 = Mod(alpha * alpha - 8 * beta);
            var z3 = Mod((p.Y + p.Z) * (p.Y + p.Z) - gamma - delta);
            var y3 = Mod(alpha * (4 * beta - x3) - 8 * Mod(gamma * gamma));
            return new Point { X = x3, Y = y3, Z = z3 };
        }

        private static Point ScalarMul(BigInteger k, Point p)
        {
            var result = Point.Infinity;
            // 简单 double-and-add；k 已归一化到 [1, n-1]
            for (int i = k.ToByteArray().Length * 8 - 1; i >= 0; i--)
            {
                result = Double(result);
                if (((k >> i) & BigInteger.One) == BigInteger.One) result = Add(result, p);
            }
            return result;
        }

        private static Point ToAffine(Point p)
        {
            if (p.IsInfinity) return p;
            var zInv = ModInverse(p.Z, P);
            var zInv2 = Mod(zInv * zInv);
            return Point.Affine(Mod(p.X * zInv2), Mod(p.Y * zInv2 * zInv));
        }

        private static BigInteger Mod(BigInteger x)
        {
            var r = x % P;
            return r.Sign < 0 ? r + P : r;
        }

        private static BigInteger ModN(BigInteger x)
        {
            var r = x % N;
            return r.Sign < 0 ? r + N : r;
        }

        /// <summary>
        /// 模逆（扩展欧几里得）。m 为素数（P 或 N），a 归一化到 [0, m)。
        /// 不变式：r_i ≡ t_i·a (mod m0)，循环结束时 r = gcd，t 即 a⁻¹。
        /// </summary>
        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            BigInteger m0 = m;
            BigInteger t = BigInteger.Zero, newT = BigInteger.One;
            BigInteger r = m, newR = ((a % m) + m) % m;

            while (newR.Sign != 0)
            {
                BigInteger q = r / newR;
                BigInteger tmp;
                tmp = t; t = newT; newT = tmp - q * newT;
                tmp = r; r = newR; newR = tmp - q * newR;
            }
            if (r != BigInteger.One) throw new InvalidOperationException("模逆不存在");
            if (t.Sign < 0) t += m0;
            return t;
        }

        /* ===================== ECDSA ===================== */

        /// <summary>
        /// 用私钥（32 字节标量）对消息签名，返回 DER(SEQUENCE{INTEGER r, INTEGER s})。
        /// </summary>
        public static byte[] Sign(byte[] privateKey32, byte[] message)
        {
            if (privateKey32 == null || privateKey32.Length != 32)
                throw new ArgumentException("privateKey32");

            BigInteger d = BigEndian(privateKey32);
            if (d.Sign <= 0 || d >= N) throw new ArgumentException(L10n.T("私钥超出范围"));

            BigInteger z = HashToInt(message);
            var g = Point.Affine(Gx, Gy);

            byte[] sig = null;
            for (int attempt = 0; attempt < 16 && sig == null; attempt++)
            {
                BigInteger k;
                do
                {
                    var kb = new byte[32];
                    RandomNumberGenerator.Create().GetBytes(kb);
                    kb[0] &= 0x7f;                 // 保证 < n
                    k = BigEndian(kb);
                } while (k.Sign <= 0);

                var rp = ToAffine(ScalarMul(k, g));
                BigInteger r = ModN(rp.X);
                if (r.Sign == 0) continue;
                BigInteger s = ModN(ModInverse(k, N) * ModN(z + ModN(r * d)));
                if (s.Sign == 0) continue;

                sig = Der.EncodeSignature(r, s);
            }
            if (sig == null) throw new InvalidOperationException("ECDSA 签名失败");
            return sig;
        }

        /// <summary>验签：publicKey 为 0x04||X||Y（65 字节），signature 为 DER(r,s)。</summary>
        public static bool Verify(byte[] publicKey65, byte[] message, byte[] signatureDer)
        {
            try
            {
                if (publicKey65 == null || publicKey65.Length != 65 || publicKey65[0] != 4) return false;
                BigInteger qx = BigEndian(Slice(publicKey65, 1, 32));
                BigInteger qy = BigEndian(Slice(publicKey65, 33, 32));
                if (qx.Sign <= 0 || qx >= P || qy.Sign <= 0 || qy >= P) return false;

                // 点在曲线上？
                if (Mod(qy * qy) != Mod(qx * qx * qx + A * qx + B)) return false;

                BigInteger r, s;
                if (!Der.TryDecodeSignature(signatureDer, out r, out s)) return false;
                if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N) return false;

                BigInteger z = HashToInt(message);
                BigInteger w = ModInverse(s, N);
                BigInteger u1 = ModN(z * w);
                BigInteger u2 = ModN(r * w);

                var point = Add(ScalarMul(u1, Point.Affine(Gx, Gy)),
                                ScalarMul(u2, Point.Affine(qx, qy)));
                if (point.IsInfinity) return false;
                var affine = ToAffine(point);
                return ModN(affine.X) == r;
            }
            catch { return false; }
        }

        /// <summary>生成 P-256 密钥对：返回 (私钥32B, 公钥65B)。</summary>
        public static Tuple<byte[], byte[]> GenerateKeyPair()
        {
            byte[] priv;
            BigInteger d;
            do
            {
                priv = new byte[32];
                RandomNumberGenerator.Create().GetBytes(priv);
                priv[0] &= 0x7f;
                d = BigEndian(priv);
            } while (d.Sign <= 0 || d >= N);

            var pub = ToAffine(ScalarMul(d, Point.Affine(Gx, Gy)));
            var pubBytes = new byte[65];
            pubBytes[0] = 4;
            PutBigEndian(pubBytes, 1, pub.X, 32);
            PutBigEndian(pubBytes, 33, pub.Y, 32);
            return Tuple.Create(priv, pubBytes);
        }

        /// <summary>由私钥推导未压缩公钥。</summary>
        public static byte[] PublicKey(byte[] privateKey32)
        {
            BigInteger d = BigEndian(privateKey32);
            if (d.Sign <= 0 || d >= N) throw new ArgumentException("privateKey32");
            var pub = ToAffine(ScalarMul(d, Point.Affine(Gx, Gy)));
            var pubBytes = new byte[65];
            pubBytes[0] = 4;
            PutBigEndian(pubBytes, 1, pub.X, 32);
            PutBigEndian(pubBytes, 33, pub.Y, 32);
            return pubBytes;
        }

        /* ===================== 工具 ===================== */

        /// <summary>SHA-256(message) 取整（P-256 哈希长度 = 阶长度，直接取）。</summary>
        private static BigInteger HashToInt(byte[] message)
        {
            byte[] h;
            using (var sha = SHA256.Create()) h = sha.ComputeHash(message);
            return BigEndian(h);
        }

        /// <summary>
        /// 大端字节数组 → 正 BigInteger。
        /// 只用到处都存在的 <c>BigInteger(byte[])</c>（小端补码）构造：
        /// 先反序成大端缓冲，再在末尾补一个 0 字节，保证结果恒为正。
        /// 首字节 0xFF/0xC9 之类（域参数、私钥、公钥坐标都会这样）不会被解释成负数。
        /// </summary>
        private static BigInteger BigEndian(byte[] b)
        {
            var le = new byte[b.Length + 1];   // 末尾 0x00 → 正数
            for (int i = 0; i < b.Length; i++) le[i] = b[b.Length - 1 - i];
            return new BigInteger(le);
        }

        private static byte[] Slice(byte[] b, int off, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }

        private static void PutBigEndian(byte[] dst, int off, BigInteger v, int len)
        {
            var bytes = v.ToByteArray();          // 小端、无符号正数
            for (int i = 0; i < len; i++)
            {
                int src = len - 1 - i;            // 目标大端第 i 字节 ← 小端第 src 字节
                dst[off + i] = src < bytes.Length ? bytes[src] : (byte)0;
            }
        }
    }
}

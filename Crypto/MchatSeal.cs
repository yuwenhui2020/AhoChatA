using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// Mchat 独有的加密层（LC1，单层）。
    ///
    /// 目的：qaul 只被当作"通信渠道/中继"使用。qaul 自己的单聊/群聊已经是端到端加密的，
    /// 中继节点读不到内容，所以本程序**不需要再叠一层密钥交换 + 棘轮**（那属于双层加密，
    /// 白白增加握手失败、棘轮不同步的风险）。这里只保留一层：
    ///
    ///     频道密码 --PBKDF2--> pwKey --HKDF--> 消息密钥
    ///     每条消息：随机 12 字节 nonce + ChaCha20-Poly1305 AEAD，AAD 绑定频道
    ///
    /// 只要达到一个目标即可：**qaul 侧（中继节点、公网节点、ISP、明文洪泛的 feed）
    /// 永远只能看到密文，看不到明文**。
    ///
    /// 另外提供一把"公开发现密钥"（所有 Mchat 客户端共有、与频道绑定的常量派生密钥）：
    /// 频道没设密码时，中间层的设备发现信标用它加密——这样即使不设密码，
    /// qaul 那边看到的仍然是密文（隔除窥视），而任意一台 Mchat 都能解开信标完成发现。
    ///
    /// 安全边界：
    ///   · 没有频道密码的人只能看到密文，且改不动（Poly1305 认证）。
    ///   · 会话元数据（谁在什么时候发了多大一条）qaul 侧仍然可见，本层不做流量填充。
    /// </summary>
    public static class MchatSeal
    {
        /// <summary>密文在聊天正文里的可识别前缀（"LC1:" + base64）。</summary>
        public const string Prefix = "LC1:";

        /// <summary>单层帧版本：ver(1) | nonce(12) | ct+tag。</summary>
        public const byte Version = 2;
        /// <summary>已废弃的旧版本（ECDH 握手 + 棘轮的双层帧），收到只提示不兼容。</summary>
        public const byte LegacyVersion = 1;

        public const int KeyBytes = 32;
        public const int NonceBytes = 12;
        /// <summary>帧头长度：ver(1) + nonce(12)。</summary>
        public const int HeaderBytes = 13;

        public const int Pbkdf2Iterations = 120000;

        // 这两个域分隔符与旧版保持一致：旧密码恢复后算出的密钥与安全码都不变
        private const string DomainPw = "Mchat/pw/v1|";
        private const string DomainSn = "Mchat/sn/v1|";

        private const string DomainSeal = "Mchat/seal/v2|";
        private const string DomainDisc = "Mchat/disc/v1|";
        private const string PublicDiscoverySeed = "Mchat/public-discovery/v1";

        /* ===================== 密钥派生 ===================== */

        /// <summary>PBKDF2-HMAC-SHA256（自行实现，避免不同 Mono 版本的 API 差异）。</summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int outputBytes)
        {
            if (password == null) password = new byte[0];
            if (salt == null) salt = new byte[0];
            if (outputBytes <= 0) return new byte[0];

            var result = new byte[outputBytes];
            var counter = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, counter, 0, salt.Length);

            using (var h = new HMACSHA256(password))
            {
                int blocks = (outputBytes + 31) / 32;
                for (int i = 1; i <= blocks; i++)
                {
                    counter[salt.Length] = (byte)(i >> 24);
                    counter[salt.Length + 1] = (byte)(i >> 16);
                    counter[salt.Length + 2] = (byte)(i >> 8);
                    counter[salt.Length + 3] = (byte)i;

                    var u = h.ComputeHash(counter);
                    var t = (byte[])u.Clone();
                    for (int j = 1; j < iterations; j++)
                    {
                        u = h.ComputeHash(u);
                        for (int k = 0; k < 32; k++) t[k] ^= u[k];
                    }

                    int off = (i - 1) * 32;
                    int take = Math.Min(32, outputBytes - off);
                    Buffer.BlockCopy(t, 0, result, off, take);
                }
            }
            return result;
        }

        /// <summary>pwKey = PBKDF2(密码, "Mchat/pw/v1|" + 频道)。频道参与派生，跨频道不复用。</summary>
        public static byte[] PasswordKey(string conv, string password)
        {
            var salt = Encoding.UTF8.GetBytes(DomainPw + (conv ?? ""));
            return Pbkdf2Sha256(Encoding.UTF8.GetBytes(password ?? ""), salt, Pbkdf2Iterations, KeyBytes);
        }

        /// <summary>由 pwKey 派生真正上 AEAD 的消息密钥（不直接复用 PBKDF2 输出）。</summary>
        public static byte[] SealKey(string conv, byte[] pwKey)
        {
            var prk = Hkdf.Extract(pwKey, Encoding.UTF8.GetBytes(DomainSeal + (conv ?? "")));
            return Hkdf.Expand(prk, Encoding.UTF8.GetBytes("key"), KeyBytes);
        }

        /// <summary>
        /// 公开发现密钥：所有 Mchat 客户端共有的常量派生密钥，按频道区分。
        /// 它不是密码，只用于把"设备发现信标"变成密文——qaul 侧看不到明文昵称/频道名，
        /// 但任何一台 Mchat 都能解开，从而在没有局域网探测权限时也能完成发现。
        /// </summary>
        public static byte[] DiscoveryKey(string conv)
        {
            var ikm = Sha256(Encoding.UTF8.GetBytes(PublicDiscoverySeed));
            var prk = Hkdf.Extract(Encoding.UTF8.GetBytes(DomainDisc + (conv ?? "")), ikm);
            return Hkdf.Expand(prk, Encoding.UTF8.GetBytes("disc"), KeyBytes);
        }

        /* ===================== 安全码 ===================== */

        /// <summary>
        /// 安全码：由密码派生，不依赖握手。设置完密码就能立刻与对方比对，
        /// 一致才说明两人用的是同一个密码。
        /// </summary>
        public static string SafetyNumber(string conv, byte[] pwKey)
        {
            var input = Encoding.UTF8.GetBytes(DomainSn + (conv ?? ""));
            var buf = new byte[input.Length + (pwKey == null ? 0 : pwKey.Length)];
            Buffer.BlockCopy(input, 0, buf, 0, input.Length);
            if (pwKey != null) Buffer.BlockCopy(pwKey, 0, buf, input.Length, pwKey.Length);

            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(buf);

            var sb = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(hash[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /* ===================== 帧编解码 ===================== */

        public static byte[] RandomNonce()
        {
            var n = new byte[NonceBytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(n);
            return n;
        }

        /// <summary>AAD 绑定版本号与频道，防止跨频道重放与篡改。</summary>
        public static byte[] Aad(string conv)
        {
            var c = Encoding.UTF8.GetBytes(conv ?? "");
            var aad = new byte[c.Length + 1];
            aad[0] = Version;
            Buffer.BlockCopy(c, 0, aad, 1, c.Length);
            return aad;
        }

        public static byte[] Encode(byte[] nonce, byte[] ct)
        {
            var outBuf = new byte[HeaderBytes + (ct == null ? 0 : ct.Length)];
            outBuf[0] = Version;
            Buffer.BlockCopy(nonce, 0, outBuf, 1, NonceBytes);
            if (ct != null && ct.Length > 0) Buffer.BlockCopy(ct, 0, outBuf, HeaderBytes, ct.Length);
            return outBuf;
        }

        public static bool Decode(byte[] raw, out byte[] nonce, out byte[] ct)
        {
            nonce = null; ct = null;
            if (raw == null || raw.Length < HeaderBytes + 16) return false;   // 至少要有 tag
            if (raw[0] != Version) return false;
            nonce = new byte[NonceBytes];
            Buffer.BlockCopy(raw, 1, nonce, 0, NonceBytes);
            ct = new byte[raw.Length - HeaderBytes];
            Buffer.BlockCopy(raw, HeaderBytes, ct, 0, ct.Length);
            return true;
        }

        public static string Armor(byte[] raw)
        {
            return Prefix + Convert.ToBase64String(raw);
        }

        /// <summary>把聊天正文解成 LC1 帧；不是本层密文返回 false。</summary>
        public static bool Disarm(string text, out byte[] raw)
        {
            raw = null;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            try { raw = Convert.FromBase64String(text.Substring(Prefix.Length)); }
            catch { return false; }
            return raw != null && raw.Length >= HeaderBytes;
        }

        /// <summary>是否是旧版（双层）密文：只用于给出更清楚的报错。</summary>
        public static bool IsLegacy(string text, out byte[] raw)
        {
            raw = null;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            try { raw = Convert.FromBase64String(text.Substring(Prefix.Length)); }
            catch { return false; }
            return raw != null && raw.Length > 0 && raw[0] == LegacyVersion;
        }

        public static bool IsSealed(string text)
        {
            return !string.IsNullOrEmpty(text) && text.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /* ===================== 加解密原语 ===================== */

        public static byte[] SealRaw(byte[] key, string conv, byte[] plain)
        {
            var nonce = RandomNonce();
            var ct = ChaCha20Poly1305.Seal(key, nonce, plain ?? new byte[0], Aad(conv));
            return Encode(nonce, ct);
        }

        /// <summary>解开成功返回明文，失败（认证不过）返回 null。</summary>
        public static byte[] OpenRaw(byte[] key, string conv, byte[] nonce, byte[] ct)
        {
            return ChaCha20Poly1305.Open(key, nonce, ct, Aad(conv));
        }

        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data ?? new byte[0]);
        }
    }

    /// <summary>一个频道的 LC1 密钥状态（单层：只有一把消息密钥）。</summary>
    public class MchatChannelKey
    {
        public string Conv;
        public string Password;
        /// <summary>PBKDF2 输出（安全码由它派生）。</summary>
        public byte[] PwKey;
        /// <summary>真正上 AEAD 的消息密钥。</summary>
        public byte[] Seal;
        public long CreatedAt;
    }

    /// <summary>
    /// LC1 管理器：频道密码 → 消息密钥 → ChaCha20-Poly1305。
    /// 线程安全；密码由上层持久化，这里只管内存态与加解密。
    /// </summary>
    public class MchatCrypto
    {
        private readonly Dictionary<string, MchatChannelKey> keys =
            new Dictionary<string, MchatChannelKey>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> discKeys =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly object gate = new object();

        public Action<string> OnLog;

        private void Log(string s)
        {
            try { Android.Util.Log.Info("LC1", s); } catch { }
            try { if (OnLog != null) OnLog(s); } catch { }
        }

        /* ===================== 频道密码管理 ===================== */

        public bool Has(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return false;
            lock (gate) return keys.ContainsKey(conv);
        }

        public string PasswordOf(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return null;
            lock (gate)
            {
                MchatChannelKey k;
                return keys.TryGetValue(conv, out k) ? k.Password : null;
            }
        }

        /// <summary>给频道设置密码（覆盖旧密码）。</summary>
        public MchatChannelKey SetPassword(string conv, string password)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(password)) return null;
            var pwKey = MchatSeal.PasswordKey(conv, password);
            var k = new MchatChannelKey
            {
                Conv = conv,
                Password = password,
                PwKey = pwKey,
                Seal = MchatSeal.SealKey(conv, pwKey),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            lock (gate) keys[conv] = k;
            Log(L10n.T("频道已设置密码（单层 LC1）: ") + conv);
            return k;
        }

        public void Clear(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return;
            lock (gate) keys.Remove(conv);
        }

        public List<string> ProtectedConversations()
        {
            lock (gate) return new List<string>(keys.Keys);
        }

        /// <summary>安全码（用于线下比对，确认双方密码一致）。</summary>
        public string SafetyNumber(string conv)
        {
            lock (gate)
            {
                MchatChannelKey k;
                if (!keys.TryGetValue(conv, out k)) return null;
                return MchatSeal.SafetyNumber(conv, k.PwKey);
            }
        }

        /* ===================== 加密（出站） ===================== */

        /// <summary>
        /// 用频道密码加密；频道没设密码时返回 null（表示这条不加密、按明文走）。
        /// </summary>
        public string Seal(string conv, string payloadJson)
        {
            byte[] key;
            lock (gate)
            {
                MchatChannelKey k;
                if (!keys.TryGetValue(conv, out k)) return null;
                key = k.Seal;
            }
            try
            {
                var raw = MchatSeal.SealRaw(key, conv, Encoding.UTF8.GetBytes(payloadJson ?? ""));
                return MchatSeal.Armor(raw);
            }
            catch (Exception ex) { Log(L10n.T("加密失败: ") + ex.Message); return null; }
        }

        /// <summary>
        /// 加密"设备发现信标"：频道设了密码就用密码（只有成员看得见）；
        /// 没设密码就用公开发现密钥（仍然不是明文，qaul 侧看不到内容）。
        /// </summary>
        public string SealBeacon(string conv, string payloadJson)
        {
            byte[] key;
            lock (gate)
            {
                MchatChannelKey k;
                key = keys.TryGetValue(conv, out k) ? k.Seal : DiscoveryKeyLocked(conv);
            }
            try
            {
                var raw = MchatSeal.SealRaw(key, conv, Encoding.UTF8.GetBytes(payloadJson ?? ""));
                return MchatSeal.Armor(raw);
            }
            catch (Exception ex) { Log(L10n.T("信标加密失败: ") + ex.Message); return null; }
        }

        /* ===================== 解密（入站） ===================== */

        /// <summary>
        /// 尝试解开一条入站正文。
        /// 返回：0=非密文（原样放行）；1=解密成功；-1=是密文但解不开（密码不对/频道不匹配）。
        /// </summary>
        public int TryOpen(string conv, string text, string senderId, out string payloadJson, out string error)
        {
            bool viaPublic;
            return TryOpen(conv, text, senderId, out payloadJson, out error, out viaPublic);
        }

        /// <summary>
        /// 同上，另外给出 <paramref name="viaPublic"/>：
        /// true = 这条是用"公开发现密钥"解开的（中间层的设备信标），不是频道密码解开的。
        /// </summary>
        public int TryOpen(string conv, string text, string senderId, out string payloadJson, out string error,
                           out bool viaPublic)
        {
            payloadJson = null;
            error = null;
            viaPublic = false;

            byte[] raw;
            if (!MchatSeal.Disarm(text, out raw)) return 0;   // 不是本层密文

            if (raw[0] == MchatSeal.LegacyVersion)
            {
                error = L10n.T("旧版密文（双层），请双方升级后重设频道密码");
                return -1;
            }
            if (raw[0] != MchatSeal.Version)
            {
                error = L10n.T("密文版本不支持");
                return -1;
            }

            byte[] nonce, ct;
            if (!MchatSeal.Decode(raw, out nonce, out ct))
            {
                error = L10n.T("密文帧格式错误");
                return -1;
            }

            byte[] channelKey = null;
            byte[] publicKey = null;
            lock (gate)
            {
                MchatChannelKey k;
                if (keys.TryGetValue(conv, out k)) channelKey = k.Seal;
                publicKey = DiscoveryKeyLocked(conv);
            }

            // 先用频道密码；解不开再试公开发现密钥（中间层信标走这条）
            var candidates = new List<byte[]>();
            if (channelKey != null) candidates.Add(channelKey);
            if (publicKey != null) candidates.Add(publicKey);

            for (int i = 0; i < candidates.Count; i++)
            {
                byte[] plain;
                try { plain = MchatSeal.OpenRaw(candidates[i], conv, nonce, ct); }
                catch { plain = null; }
                if (plain == null) continue;

                payloadJson = Encoding.UTF8.GetString(plain);
                viaPublic = (i > 0) || channelKey == null;
                return 1;
            }

            if (channelKey == null) error = "本频道未设置密码";
            else error = L10n.T("解密失败：频道密码可能不一致");
            return -1;
        }

        /// <summary>只尝试用"公开发现密钥"解开（中间层信标专用）。</summary>
        public bool TryOpenBeacon(string conv, string text, out string payloadJson)
        {
            payloadJson = null;
            byte[] raw;
            if (!MchatSeal.Disarm(text, out raw)) return false;
            if (raw[0] != MchatSeal.Version) return false;

            byte[] nonce, ct;
            if (!MchatSeal.Decode(raw, out nonce, out ct)) return false;

            byte[] key;
            lock (gate) key = DiscoveryKeyLocked(conv);
            try
            {
                var plain = MchatSeal.OpenRaw(key, conv, nonce, ct);
                if (plain == null) return false;
                payloadJson = Encoding.UTF8.GetString(plain);
                return true;
            }
            catch { return false; }
        }

        private byte[] DiscoveryKeyLocked(string conv)
        {
            byte[] k;
            if (discKeys.TryGetValue(conv, out k)) return k;
            k = MchatSeal.DiscoveryKey(conv);
            discKeys[conv] = k;
            return k;
        }

        /// <summary>该频道的发现密钥指纹（界面排查用，不含秘密）。</summary>
        public string DiscoveryFingerprint(string conv)
        {
            byte[] k;
            lock (gate) k = DiscoveryKeyLocked(conv);
            var sb = new StringBuilder();
            for (int i = 0; i < 4; i++) sb.Append(k[i].ToString("X2"));
            return sb.ToString();
        }

        /* ===================== 状态 ===================== */

        /// <summary>保留旧字段名：单层加密没有握手，"已启用"即"已协商"。</summary>
        public bool IsHandshaked(string conv)
        {
            return Has(conv);
        }

        /// <summary>界面用的一句话状态。</summary>
        public string StatusText(string conv)
        {
            lock (gate)
            {
                MchatChannelKey k;
                if (!keys.TryGetValue(conv, out k)) return L10n.T("未加密");
                return L10n.T("LC1 端到端加密（单层·频道密码）");
            }
        }
    }
}

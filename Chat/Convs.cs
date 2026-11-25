using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// 会话模型（重构要点）
    ///
    /// 聊天逻辑取自 bitchat：
    ///   - 「房间」= 一个可自由加入/离开的公共频道，用「房间号」标识（bitchat 的 #mesh /
    ///     geohash 频道 → 这里的 room 号码）；默认房间号 mesh，等价于 bitchat 的 Mesh 频道。
    ///   - 「私聊」= 两个节点之间的一对一会话（bitchat 的 /msg @name），会话键由双方
    ///     peerId 排序拼接，任意一端发起都落到同一个会话。
    ///
    /// 协议层对齐 qaul：qaul 的会话标识是 group_id（16 字节 UUID）。
    /// 因此每个会话键都确定性映射成一个 UUID v5 形态的 group_id：
    ///     group_id = UUIDv5(namespace = "Mchat/conv/v1:", name = 会话键)
    /// 同一房间号在所有设备上算出同一个 group_id，无需建群握手即可直接收发；
    /// 私聊会话同理（双方算出相同 group_id），对应 qaul 的 direct chat group。
    /// </summary>
    public static class Convs
    {
        public const string DefaultRoom = "mesh";
        public const string RoomPrefix = "room:";
        public const string DmPrefix = "dm:";

        /// <summary>
        /// 频道（chan:）——统一频道前缀。
        /// 频道是「实际的聊天室」：一个频道名确定一个房间，频道本身不带传输方式，
        /// 走哪几条链路（qaul / 蓝牙 / WLAN Direct / 局域网）由 Channels 的链接集合决定。
        /// </summary>
        public const string ChanPrefix = "chan:";
        /// <summary>与 qaul 用户的私聊（会话键直接带对端 base58 PeerId）。</summary>
        public const string QaulDmPrefix = "qaul:";
        /// <summary>qaul 群组会话（键是 16 字节群 id 的 hex）。</summary>
        public const string QaulGroupPrefix = "qgroup:";

        /// <summary>bitchat 公共频道（公屏）：默认 bitchat:mesh，等价于 bitchat 的 Mesh 频道。</summary>
        public const string BitchatPrefix = "bitchat:";
        /// <summary>bitchat 私聊：bitchatdm:&lt;较小id&gt;|&lt;较大id&gt;（与顺序无关）。</summary>
        public const string BitchatDmPrefix = "bitchatdm:";
        /// <summary>bitchat 群组会话（键是群 id 的 hex）。</summary>
        public const string BitchatGroupPrefix = "bitchatgroup:";
        /// <summary>bitchat 默认公共频道名。</summary>
        public const string BitchatDefaultRoom = "mesh";
        /// <summary>
        /// 网页端独立页（App 内网页端）在会话条里的哨兵键。
        /// 它不是真实聊天会话：点它只是打开单独的 #chat 网页端页面，
        /// 不进聊天历史、不参与消息收发，仅用于顶栏标签的高亮与切换。
        /// </summary>
        public const string WebConv = "#chat";

        private static readonly byte[] NamespaceSeed =
            Encoding.UTF8.GetBytes("Mchat/conv/v1:");

        /// <summary>房间号规整：去空白、去前导 #、小写，仅保留 [a-z0-9_-]，最长 24。</summary>
        public static string NormalizeRoom(string code)
        {
            if (string.IsNullOrEmpty(code)) return DefaultRoom;
            var sb = new StringBuilder(code.Length);
            foreach (var raw in code.Trim())
            {
                var c = char.ToLowerInvariant(raw);
                if (c == '#') continue;
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == ' ' || c == '.' || c == '/')
                    sb.Append('-');
                if (sb.Length >= 24) break;
            }
            var s = sb.ToString().Trim('-');
            return s.Length == 0 ? DefaultRoom : s;
        }

        /// <summary>房间号 → 会话键。</summary>
        public static string Room(string code)
        {
            return RoomPrefix + NormalizeRoom(code);
        }

        /// <summary>两个 peerId → 私聊会话键（与顺序无关）。</summary>
        public static string Dm(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            return string.CompareOrdinal(a, b) <= 0
                ? DmPrefix + a + "|" + b
                : DmPrefix + b + "|" + a;
        }

        public static bool IsDm(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return false;
            return conv.StartsWith(DmPrefix, StringComparison.Ordinal) ||
                   conv.StartsWith(QaulDmPrefix, StringComparison.Ordinal) ||
                   conv.StartsWith(BitchatDmPrefix, StringComparison.Ordinal);
        }

        public static bool IsQaulDm(string conv)
        {
            return !string.IsNullOrEmpty(conv) && conv.StartsWith(QaulDmPrefix, StringComparison.Ordinal);
        }

        public static bool IsQaulGroup(string conv)
        {
            return !string.IsNullOrEmpty(conv) && conv.StartsWith(QaulGroupPrefix, StringComparison.Ordinal);
        }

        /// <summary>qaul 私聊会话里的对端 PeerId。</summary>
        public static string QaulPeerOf(string conv)
        {
            if (!IsQaulDm(conv)) return null;
            return conv.Substring(QaulDmPrefix.Length);
        }

        /// <summary>
        /// qaul 私聊的「对称加密标签」（修复错误1 的关键）。
        ///
        /// 会话的「存储键」仍是各端视角下的 qaul:&lt;remotePeerId&gt;（两端不同），但 LC1 的
        /// 密钥（PBKDF2 盐 / HKDF info）与 AAD 全都绑定 conv——用存储键会导致两端密钥与
        /// AAD 永远对不上。这里给加密层单独一个对称标签：两端各拿 (selfQaulId, remoteQaulId)
        /// 排序后拼接，算出的字符串必然一致，于是两端用同一套密钥与 AAD。
        /// </summary>
        public static string QaulDmCryptoConv(string selfId, string remoteId)
        {
            var a = selfId ?? "";
            var b = remoteId ?? "";
            return QaulDmPrefix + (string.CompareOrdinal(a, b) <= 0 ? (a + "|" + b) : (b + "|" + a));
        }

        /// <summary>qaul 群会话里的群 id（hex）。</summary>
        public static string QaulGroupIdOf(string conv)
        {
            if (!IsQaulGroup(conv)) return null;
            return conv.Substring(QaulGroupPrefix.Length);
        }

        /* ---------- bitchat 会话键 ---------- */

        /// <summary>bitchat 公共频道键（与 qaul 房间同构，确定性映射到 group_id）。</summary>
        public static string BitchatRoom(string code)
        {
            if (string.IsNullOrEmpty(code)) return BitchatPrefix + BitchatDefaultRoom;
            var sb = new StringBuilder(code.Length);
            foreach (var raw in code.Trim())
            {
                var c = char.ToLowerInvariant(raw);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                if (sb.Length >= 24) break;
            }
            var s = sb.ToString().Trim('-');
            return BitchatPrefix + (s.Length == 0 ? BitchatDefaultRoom : s);
        }

        /// <summary>两个 bitchat peerID → 私聊会话键（与顺序无关）。</summary>
        public static string BitchatDm(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            return string.CompareOrdinal(a, b) <= 0
                ? BitchatDmPrefix + a + "|" + b
                : BitchatDmPrefix + b + "|" + a;
        }

        public static bool IsBitchat(string conv)
        {
            return IsBitchatRoom(conv) || IsBitchatDm(conv) || IsBitchatGroup(conv);
        }

        public static bool IsBitchatRoom(string conv)
        {
            return !string.IsNullOrEmpty(conv) && conv.StartsWith(BitchatPrefix, StringComparison.Ordinal);
        }

        /// <summary>bitchat 房间会话里的房间号（非房间会话返回 null）。</summary>
        public static string BitchatRoomCode(string conv)
        {
            if (!IsBitchatRoom(conv)) return null;
            var code = conv.Substring(BitchatPrefix.Length);
            return string.IsNullOrEmpty(code) ? BitchatDefaultRoom : code;
        }

        public static bool IsBitchatDm(string conv)
        {
            return !string.IsNullOrEmpty(conv) && conv.StartsWith(BitchatDmPrefix, StringComparison.Ordinal);
        }

        public static bool IsBitchatGroup(string conv)
        {
            return !string.IsNullOrEmpty(conv) && conv.StartsWith(BitchatGroupPrefix, StringComparison.Ordinal);
        }

        /// <summary>bitchat 私聊会话里的对端 peerID。</summary>
        public static string BitchatPeerOf(string conv)
        {
            if (!IsBitchatDm(conv)) return null;
            return conv.Substring(BitchatDmPrefix.Length);
        }

        /// <summary>bitchat 群会话里的群 id（hex）。</summary>
        public static string BitchatGroupIdOf(string conv)
        {
            if (!IsBitchatGroup(conv)) return null;
            return conv.Substring(BitchatGroupPrefix.Length);
        }

        public static bool IsRoom(string conv)
        {
            return !string.IsNullOrEmpty(conv) &&
                   conv.StartsWith(RoomPrefix, StringComparison.Ordinal);
        }

        /* ---------- 频道（chan:）---------- */

        /// <summary>频道名 → 频道会话键。</summary>
        public static string Chan(string name)
        {
            return ChanPrefix + NormalizeChannel(name);
        }

        /// <summary>频道名规整：与房间号同规则（去 #、小写、仅 [a-z0-9_-]、最长 24）。</summary>
        public static string NormalizeChannel(string name)
        {
            if (string.IsNullOrEmpty(name)) return DefaultRoom;
            var sb = new StringBuilder(name.Length);
            foreach (var raw in name.Trim())
            {
                var c = char.ToLowerInvariant(raw);
                if (c == '#') continue;
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == ' ' || c == '.' || c == '/')
                    sb.Append('-');
                if (sb.Length >= 24) break;
            }
            var s = sb.ToString().Trim('-');
            return s.Length == 0 ? DefaultRoom : s;
        }

        public static bool IsChan(string conv)
        {
            return !string.IsNullOrEmpty(conv) && conv.StartsWith(ChanPrefix, StringComparison.Ordinal);
        }

        /// <summary>频道会话键里的频道名。</summary>
        public static string ChanName(string conv)
        {
            if (!IsChan(conv)) return null;
            var n = conv.Substring(ChanPrefix.Length);
            return string.IsNullOrEmpty(n) ? DefaultRoom : n;
        }

        /// <summary>私聊会话里的对端 peerId；非私聊或解析失败返回 null。</summary>
        public static string DmPeer(string conv, string selfId)
        {
            if (!IsDm(conv)) return null;
            // 三种私聊前缀长度不同，必须按各自的前缀切，不能一律按 "dm:"(3) 切，
            // 否则 qaul:/bitchatdm: 会被切出 "l:xxx" 这类垃圾，导致私聊消息被误判为外来而丢弃。
            string body;
            if (conv.StartsWith(QaulDmPrefix, StringComparison.Ordinal)) body = conv.Substring(QaulDmPrefix.Length);
            else if (conv.StartsWith(BitchatDmPrefix, StringComparison.Ordinal)) body = conv.Substring(BitchatDmPrefix.Length);
            else body = conv.Substring(DmPrefix.Length);
            var idx = body.IndexOf('|');
            if (idx <= 0) return body;
            var a = body.Substring(0, idx);
            var b = body.Substring(idx + 1);
            if (string.Equals(a, selfId, StringComparison.Ordinal)) return b;
            if (string.Equals(b, selfId, StringComparison.Ordinal)) return a;
            return b;
        }

        /// <summary>本机是否为该私聊会话的一方。中转站不是任何一方时返回 false，
        /// 用于让中转站帮忙转发密文私聊帧而不把别人的私聊当自己的消息落库/显示。</summary>
        public static bool IsDmParticipant(string conv, string selfId)
        {
            if (!IsDm(conv) || string.IsNullOrEmpty(selfId)) return false;
            string body;
            if (conv.StartsWith(QaulDmPrefix, StringComparison.Ordinal)) body = conv.Substring(QaulDmPrefix.Length);
            else if (conv.StartsWith(BitchatDmPrefix, StringComparison.Ordinal)) body = conv.Substring(BitchatDmPrefix.Length);
            else body = conv.Substring(DmPrefix.Length);
            var idx = body.IndexOf('|');
            if (idx <= 0) return string.Equals(body, selfId, StringComparison.Ordinal);
            var a = body.Substring(0, idx);
            var b = body.Substring(idx + 1);
            return string.Equals(a, selfId, StringComparison.Ordinal)
                || string.Equals(b, selfId, StringComparison.Ordinal);
        }

        /// <summary>会话键 → 房间号（非房间会话返回 null）。</summary>
        public static string RoomCode(string conv)
        {
            if (!IsRoom(conv)) return null;
            return conv.Substring(RoomPrefix.Length);
        }

        /// <summary>会话键 → 16 字节 group_id（UUID v5 形态，确定性）。</summary>
        public static byte[] GroupId(string conv)
        {
            if (string.IsNullOrEmpty(conv)) conv = Room(DefaultRoom);
            var name = Encoding.UTF8.GetBytes(conv);
            var buf = new byte[NamespaceSeed.Length + name.Length];
            Buffer.BlockCopy(NamespaceSeed, 0, buf, 0, NamespaceSeed.Length);
            Buffer.BlockCopy(name, 0, buf, NamespaceSeed.Length, name.Length);

            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(buf);
                var gid = new byte[16];
                Buffer.BlockCopy(hash, 0, gid, 0, 16);
                gid[6] = (byte)((gid[6] & 0x0F) | 0x50);   // version 5
                gid[8] = (byte)((gid[8] & 0x3F) | 0x80);   // RFC4122 variant
                return gid;
            }
        }

        public static string GroupIdHex(string conv)
        {
            return Hex(GroupId(conv));
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
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return new byte[0];
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        /// <summary>UUID 形式展示（qaul 的 group_id 就是 UUID）。</summary>
        public static string GroupIdText(string conv)
        {
            var h = GroupIdHex(conv);
            if (h.Length != 32) return h;
            return h.Substring(0, 8) + "-" + h.Substring(8, 4) + "-" + h.Substring(12, 4) +
                   "-" + h.Substring(16, 4) + "-" + h.Substring(20, 12);
        }

        /// <summary>会话标题：频道直接显示频道名（不再带 # 前缀），私聊由调用方用在线昵称补齐。</summary>
        public static string TitleOf(string conv)
        {
            if (IsChan(conv))
            {
                var cn = ChanName(conv);
                return string.IsNullOrEmpty(cn) ? DefaultRoom : cn;
            }
            if (IsQaulDm(conv))
            {
                var peer = QaulPeerOf(conv);
                return peer == null ? "qaul 私聊" : "qaul " + (peer.Length > 10 ? peer.Substring(0, 10) + "…" : peer);
            }
            if (IsQaulGroup(conv))
            {
                var gid = QaulGroupIdOf(conv);
                return gid == null ? "qaul 群组" : "群 " + (gid.Length > 8 ? gid.Substring(0, 8) : gid);
            }
            if (IsBitchatDm(conv))
            {
                var peer = BitchatPeerOf(conv);
                return peer == null ? "bitchat 私聊" : "bt " + (peer.Length > 10 ? peer.Substring(0, 10) + "…" : peer);
            }
            if (IsBitchatGroup(conv))
            {
                var gid = BitchatGroupIdOf(conv);
                return gid == null ? "bitchat 群组" : "bt 群 " + (gid.Length > 8 ? gid.Substring(0, 8) : gid);
            }
            if (IsDm(conv))
            {
                var body = conv.Substring(DmPrefix.Length);
                var idx = body.IndexOf('|');
                return idx > 0 ? body.Substring(idx + 1) : body;
            }
            if (IsBitchatRoom(conv))
                return "#bt " + conv.Substring(BitchatPrefix.Length);
            return "#" + (RoomCode(conv) ?? DefaultRoom);
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// libp2p 的 varint（unsigned LEB128）与 multistream-select 1.0.0。
    ///
    /// multistream-select 是所有 libp2p 连接的第一层：先协商 `/multistream/1.0.0`，
    /// 再协商安全通道（`/noise`）、复用器（`/yamux/1.0.0`），最后在每条逻辑流上
    /// 协商业务协议（`/qaul_info/1.0.0` 等）。每条协议名都是
    /// `varint(长度) + 协议名 + "\n"`，长度包含换行符。
    /// </summary>
    public static class Ms
    {
        public const string Header = "/multistream/1.0.0";
        public const string Na = "na";
        public const string Noise = "/noise";
        public const string Yamux = "/yamux/1.0.0";
        public const string Identify = "/ipfs/id/1.0.0";
        public const string Ping = "/ipfs/ping/1.0.0";
        public const string Floodsub = "/floodsub/1.0.0";
        public const string QaulInfo = "/qaul_info/1.0.0";
        public const string QaulMessaging = "/qaul_messaging/1.0.0";

        public static void WriteUvarint(Stream s, ulong value)
        {
            while (value >= 0x80)
            {
                s.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            s.WriteByte((byte)value);
        }

        public static byte[] UvarintBytes(ulong value)
        {
            using (var ms = new MemoryStream())
            {
                WriteUvarint(ms, value);
                return ms.ToArray();
            }
        }

        /// <summary>读取 varint；流结束返回 false。</summary>
        public static bool TryReadUvarint(Stream s, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return false;
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift > 63) return false;
            }
        }

        public static ulong ReadUvarint(Stream s)
        {
            ulong v;
            if (!TryReadUvarint(s, out v)) throw new EndOfStreamException(L10n.T("varint 读取失败"));
            return v;
        }

        /// <summary>写一条带 varint 长度前缀的协议名（含结尾换行）。</summary>
        public static void WriteMsg(Stream s, string text)
        {
            // 必须**一次写完整条消息**：分两次写（先长度、后正文）在 QUIC 上会变成两个
            // STREAM 帧／两个包，任何一个丢了整条消息就永远拼不齐（对 TCP 也只是徒增小包）。
            var buf = MsgBytes(text);
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }

        public static byte[] MsgBytes(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            var prefix = UvarintBytes((ulong)bytes.Length);
            var buf = new byte[prefix.Length + bytes.Length];
            Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
            Buffer.BlockCopy(bytes, 0, buf, prefix.Length, bytes.Length);
            return buf;
        }

        /// <summary>读一条 varint 长度前缀的消息；去掉结尾换行。格式化错误返回 null。</summary>
        public static string ReadMsg(Stream s)
        {
            ulong len;
            if (!TryReadUvarint(s, out len)) return null;
            if (len == 0 || len > 8192) return null;
            var buf = new byte[(int)len];
            if (!IoUtil.ReadExact(s, buf, 0, buf.Length)) return null;
            var text = Encoding.UTF8.GetString(buf);
            return text.EndsWith("\n") ? text.Substring(0, text.Length - 1) : text;
        }

        /// <summary>
        /// 发起端协商：写头部 → 校验回显 → 提议协议 → 判定是否被接受。
        /// </summary>
        public static bool SelectOne(Stream s, string protocol)
        {
            WriteMsg(s, Header);
            var back = ReadMsg(s);
            if (back != Header) return false;

            WriteMsg(s, protocol);
            // 对端可能先回 na（继续列候选）也可能直接回协议名
            while (true)
            {
                var reply = ReadMsg(s);
                if (reply == null) return false;
                if (reply == protocol) return true;
                if (reply == Na) return false;
                // 某些实现会回 `ls` 结果，忽略后继续
            }
        }

        /// <summary>
        /// 接受端协商：读头部 → 回显 → 依次读提议，命中则回协议名并返回。
        /// 未命中回 `na` 继续等下一个提议；返回 null 表示流被对端关闭。
        /// </summary>
        public static string AcceptOne(Stream s, params string[] supported)
        {
            var head = ReadMsg(s);
            if (head != Header)
            {
                // 兼容「懒协商」：对方可能把头部与协议名一起发来，但顺序仍是先头部
                return null;
            }
            WriteMsg(s, Header);

            while (true)
            {
                var proposal = ReadMsg(s);
                if (proposal == null) return null;
                if (proposal == Header) continue;

                bool hit = false;
                foreach (var p in supported)
                {
                    if (string.Equals(p, proposal, StringComparison.Ordinal)) { hit = true; break; }
                }

                if (hit)
                {
                    WriteMsg(s, proposal);
                    return proposal;
                }

                WriteMsg(s, Na);
            }
        }
    }

    /// <summary>二进制读写小工具（大端、定长读取）。</summary>
    public static class IoUtil
    {
        public static bool ReadExact(Stream s, byte[] buf, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n;
                try { n = s.Read(buf, offset + got, count - got); }
                catch (Exception) { return false; }
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public static void WriteBE16(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        public static void WriteBE32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        public static int ReadBE16(Stream s)
        {
            int a = s.ReadByte(), b = s.ReadByte();
            if (a < 0 || b < 0) return -1;
            return (a << 8) | b;
        }

        public static long ReadBE32(Stream s)
        {
            int a = s.ReadByte(), b = s.ReadByte(), c = s.ReadByte(), d = s.ReadByte();
            if (a < 0 || b < 0 || c < 0 || d < 0) return -1;
            return ((long)a << 24) | ((long)b << 16) | ((long)c << 8) | d;
        }

        public static void WriteVarintPrefixed(Stream s, byte[] payload)
        {
            var prefix = Ms.UvarintBytes((ulong)payload.Length);
            s.Write(prefix, 0, prefix.Length);
            s.Write(payload, 0, payload.Length);
            s.Flush();
        }

        public static byte[] ReadVarintPrefixed(Stream s, int maxLen)
        {
            ulong len;
            if (!Ms.TryReadUvarint(s, out len)) return null;
            if (len > (ulong)maxLen) return null;
            var buf = new byte[(int)len];
            if (!ReadExact(s, buf, 0, buf.Length)) return null;
            return buf;
        }
    }
}

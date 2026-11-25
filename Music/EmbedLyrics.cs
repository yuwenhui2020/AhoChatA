// ============================================================
// 音乐模块 · 内嵌歌词提取（Android 的 MediaMetadataRetriever 不暴露歌词）
// · MP3：ID3v2.3/2.4 的 USLT 帧（未同步歌词）
// · FLAC：VORBIS_COMMENT 元数据块里的 LYRICS/UNSYNCEDLYRICS 字段
// · M4A：©lyr 原子（moov.udta.meta.ilst）
// 只做只读解析，遇到不认识的帧直接跳过；失败返回 null（调用方回落同名 .lrc）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace AhoChatA.Music
{
    internal static class EmbedLyrics
    {
        /// <summary>从文件头提取内嵌歌词；找不到返回 null。</summary>
        public static string Extract(string path)
        {
            try
            {
                using (var fs = System.IO.File.OpenRead(path))
                {
                    var head = new byte[16];
                    if (fs.Read(head, 0, 16) >= 4)
                    {
                        fs.Seek(0, System.IO.SeekOrigin.Begin);   // 各解析器从文件头开始读
                        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                            return FromId3(fs);
                        if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
                            return FromFlac(fs);
                        if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
                            return FromM4a(fs);
                    }
                }
            }
            catch { }
            return null;
        }

        /* ===================== MP3 · ID3v2 ===================== */

        private static string FromId3(System.IO.FileStream fs)
        {
            var header = new byte[10];
            if (ReadExactly(fs, header, 10) != 10) return null;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;

            int ver = header[3];                      // 3 = v2.3, 4 = v2.4
            bool unsync = (header[5] & 0x80) != 0;
            long total = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14)
                       | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            if (total <= 0 || total > 32 * 1024 * 1024) return null;

            var body = new byte[total];
            int got = 0;
            while (got < total)
            {
                int r = fs.Read(body, got, (int)total - got);
                if (r <= 0) break;
                got += r;
            }
            if (got <= 0) return null;
            if (unsync) body = DeUnsync(body);

            int frameLen = ver >= 4 ? 4 : 4;          // 帧头都是 4 字节
            int pos = 0;
            string found = null;
            while (pos + frameLen + 4 <= got)
            {
                bool bad = false;
                for (int i = 0; i < 4; i++) if (!IsAlphaNum(body[pos + i])) { bad = true; break; }
                if (bad) break;

                string id = Encoding.ASCII.GetString(body, pos, 4);
                long len;
                if (ver >= 4)   // v2.4 用同步安全整数
                    len = ((body[pos + 4] & 0x7F) << 21) | ((body[pos + 5] & 0x7F) << 14)
                        | ((body[pos + 6] & 0x7F) << 7) | (body[pos + 7] & 0x7F);
                else            // v2.3 普通 32 位
                    len = ((long)body[pos + 4] << 24) | (body[pos + 5] << 16)
                        | (body[pos + 6] << 8) | body[pos + 7];

                if (len <= 0 || pos + 10 + len > got) break;
                var data = new byte[len];
                Array.Copy(body, pos + 10, data, 0, len);
                pos += 10 + (int)len;

                if (id == "USLT" || id == "SYLT")
                {
                    var s = ParseUslt(data);
                    if (!string.IsNullOrWhiteSpace(s)) { found = s; break; }
                }
                // padding：全 0 帧头直接跳出
                if (body[pos - (int)len - 10] == 0) break;
            }
            return found;
        }

        private static bool IsAlphaNum(byte b)
        {
            return (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9');
        }

        /// <summary>USLT 帧：[编码 1B][语言 3B][描述符 00 结尾][歌词文本]</summary>
        private static string ParseUslt(byte[] d)
        {
            if (d.Length < 5) return null;
            int enc = d[0];
            int p = 4;                                // 跳过语言
            // 描述符按编码读完终止符
            if (enc == 1 || enc == 2)                  // UTF-16
            {
                while (p + 1 < d.Length && (d[p] != 0 || d[p + 1] != 0)) p += 2;
                p += 2;
            }
            else
            {
                while (p < d.Length && d[p] != 0) p++;
                p += 1;
            }
            if (p >= d.Length) return null;
            return DecodeText(d, p, enc);
        }

        /// <summary>按 ID3 文本编码字节解码（0=latin1,1=utf16+BOM,2=utf16BE,3=utf8）。</summary>
        private static string DecodeText(byte[] d, int off, int enc)
        {
            try
            {
                int len = d.Length - off;
                switch (enc)
                {
                    case 1: return Encoding.Unicode.GetString(d, off, len);
                    case 2: return Encoding.BigEndianUnicode.GetString(d, off, len);
                    case 3: return Encoding.UTF8.GetString(d, off, len);
                    default:
                        // latin1：多数中文歌实际是 GBK 内容被塞进 ISO-8859-1，按 GBK 试一次更友好
                        try { return Encoding.GetEncoding("GBK").GetString(d, off, len); }
                        catch { return Encoding.GetEncoding(28591).GetString(d, off, len); }
                }
            }
            catch { return null; }
        }

        private static byte[] DeUnsync(byte[] src)
        {
            var list = new List<byte>(src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                list.Add(src[i]);
                if (src[i] == 0xFF && i + 1 < src.Length && src[i + 1] == 0x00) i++;
            }
            return list.ToArray();
        }

        /* ===================== FLAC ===================== */

        private static string FromFlac(System.IO.FileStream fs)
        {
            fs.Seek(4, System.IO.SeekOrigin.Begin);   // 跳过 fLaC
            var h = new byte[4];
            while (ReadExactly(fs, h, 4) == 4)
            {
                bool last = (h[0] & 0x80) != 0;
                int type = h[0] & 0x7F;
                long len = ((long)h[1] << 16) | (h[2] << 8) | h[3];
                if (len <= 0 || len > 8 * 1024 * 1024) return null;

                if (type == 4)                          // VORBIS_COMMENT
                {
                    var data = new byte[len];
                    if (ReadExactly(fs, data, (int)len) != (int)len) return null;
                    return ParseVorbisComment(data);
                }
                if (last) return null;
                fs.Seek(len, System.IO.SeekOrigin.Current);
            }
            return null;
        }

        private static string ParseVorbisComment(byte[] d)
        {
            int p = 0;
            long vlen = ReadLe32(d, ref p);
            p += (int)vlen;
            if (p > d.Length) return null;
            int count = (int)ReadLe32(d, ref p);
            for (int i = 0; i < count && p + 4 <= d.Length; i++)
            {
                long l = ReadLe32(d, ref p);
                if (l <= 0 || p + l > d.Length) break;
                var line = Encoding.UTF8.GetString(d, p, (int)l);
                p += (int)l;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).ToUpperInvariant();
                if (key == "LYRICS" || key == "UNSYNCEDLYRICS" || key == "UNSYNCED LYRICS")
                {
                    var v = line.Substring(eq + 1);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            return null;
        }

        private static long ReadLe32(byte[] d, ref int p)
        {
            if (p + 4 > d.Length) { p = d.Length + 1; return 0; }
            long v = d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);
            p += 4;
            return v & 0xFFFFFFFFL;
        }

        /* ===================== M4A（©lyr） ===================== */

        private static string FromM4a(System.IO.FileStream fs)
        {
            // 在 moov 的 ilst 里找 ©lyr 原子；文件一般 < 50MB，直接限制读前 8MB 足够
            fs.Seek(0, System.IO.SeekOrigin.Begin);
            long limit = Math.Min(fs.Length, 8L * 1024 * 1024);
            var buf = new byte[limit];
            int got = 0;
            while (got < limit)
            {
                int r = fs.Read(buf, got, (int)limit - got);
                if (r <= 0) break;
                got += r;
            }
            // '©lyr' = 0xA9 'l' 'y' 'r'（© 不是 ASCII，不能用 Encoding.ASCII）
            var need = new byte[] { 0xA9, (byte)'l', (byte)'y', (byte)'r' };
            for (int i = 0; i < got - 8; i++)
            {
                if (buf[i] == need[0] && buf[i + 1] == need[1] && buf[i + 2] == need[2] && buf[i + 3] == need[3])
                {
                    // ©lyr 之后：data 原子 → [size 4B]['data' 4B][类型 4B][locale 4B][文本...]
                    int p = i + 4;
                    if (p + 16 > got) return null;
                    if (buf[p + 4] == 'd' && buf[p + 5] == 'a' && buf[p + 6] == 't' && buf[p + 7] == 'a')
                    {
                        int dataLen = (buf[p] << 24) | (buf[p + 1] << 16) | (buf[p + 2] << 8) | buf[p + 3];
                        int textStart = p + 16;
                        int textLen = Math.Min(dataLen - 16, got - textStart);
                        if (textLen > 0)
                        {
                            try { return Encoding.UTF8.GetString(buf, textStart, textLen); }
                            catch { return null; }
                        }
                    }
                    return null;
                }
            }
            return null;
        }

        private static int ReadExactly(System.IO.FileStream fs, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int r = fs.Read(buf, got, count - got);
                if (r <= 0) break;
                got += r;
            }
            return got;
        }
    }
}

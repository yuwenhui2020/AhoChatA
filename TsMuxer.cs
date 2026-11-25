using System;
using System.Collections.Generic;
using System.IO;

namespace AhoChatA
{
    /// <summary>
    /// 极简 MPEG-TS 封装器：把 H.264 Annex-B 帧打成 188 字节 TS 包（PAT + PMT + PES）。
    /// PotPlayer / VLC / ffplay / mpv 等播放器可直接打开 http://ip:port/live.ts。
    /// 只推一路视频（stream_type 0x1B），关键帧带 PCR，每 30 帧重发一次 PAT/PMT。
    /// </summary>
    internal sealed class TsMuxer
    {
        private const int PmtPid = 0x1000;
        private const int VideoPid = 0x0100;
        private const int TsPacketSize = 188;

        private int _ccPat;
        private int _ccPmt;
        private int _ccVid;
        private long _pts;
        private int _sinceTables = 999;

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int j = 0; j < 8; j++)
                    c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : (c << 1);
                t[i] = c;
            }
            return t;
        }

        /// <summary>MPEG-2 系统层 CRC32（初值全 1，末尾不取反）。</summary>
        private static uint Crc32(byte[] d, int off, int len)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++)
                crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ d[off + i]) & 0xFF];
            return crc;
        }

        /// <summary>复位（重新开始共享时调用），避免 PTS/CC 跨会话混乱。</summary>
        public void Reset()
        {
            _ccPat = _ccPmt = _ccVid = 0;
            _pts = 0;
            _sinceTables = 999;
        }

        /* ---------------- PAT / PMT ---------------- */

        public byte[] BuildTables()
        {
            var outp = new List<byte>(TsPacketSize * 2);
            outp.AddRange(PackSection(0x0000, BuildPatSection(), ref _ccPat));
            outp.AddRange(PackSection(PmtPid, BuildPmtSection(), ref _ccPmt));
            return outp.ToArray();
        }

        private static byte[] BuildPatSection()
        {
            var sec = new List<byte>(16);
            sec.Add(0x00);                    // table_id = program_association_section
            sec.Add(0xB0); sec.Add(0x0D);      // section_syntax_indicator=1, section_length=13
            sec.Add(0x00); sec.Add(0x01);      // transport_stream_id
            sec.Add(0xC1);                     // version=0, current_next_indicator=1
            sec.Add(0x00); sec.Add(0x00);      // section_number / last_section_number
            sec.Add(0x00); sec.Add(0x01);      // program_number = 1
            sec.Add((byte)(0xE0 | ((PmtPid >> 8) & 0x1F)));
            sec.Add((byte)(PmtPid & 0xFF));
            uint crc = Crc32(sec.ToArray(), 0, sec.Count);
            sec.Add((byte)(crc >> 24)); sec.Add((byte)(crc >> 16)); sec.Add((byte)(crc >> 8)); sec.Add((byte)crc);
            return sec.ToArray();
        }

        private static byte[] BuildPmtSection()
        {
            var sec = new List<byte>(24);
            sec.Add(0x02);                    // table_id = TS_program_map_section
            sec.Add(0xB0); sec.Add(0x12);      // section_length = 18
            sec.Add(0x00); sec.Add(0x01);      // program_number
            sec.Add(0xC1);                     // version=0, current_next=1
            sec.Add(0x00); sec.Add(0x00);      // section_number / last
            sec.Add((byte)(0xE0 | ((VideoPid >> 8) & 0x1F)));  // PCR_PID
            sec.Add((byte)(VideoPid & 0xFF));
            sec.Add(0xF0); sec.Add(0x00);      // program_info_length = 0
            sec.Add(0x1B);                     // stream_type = H.264
            sec.Add((byte)(0xE0 | ((VideoPid >> 8) & 0x1F)));  // elementary_PID
            sec.Add((byte)(VideoPid & 0xFF));
            sec.Add(0xF0); sec.Add(0x00);      // ES_info_length = 0
            uint crc = Crc32(sec.ToArray(), 0, sec.Count);
            sec.Add((byte)(crc >> 24)); sec.Add((byte)(crc >> 16)); sec.Add((byte)(crc >> 8)); sec.Add((byte)crc);
            return sec.ToArray();
        }

        /// <summary>把一段 PSI section 打成 TS 包（PSI 靠 pointer_field 对齐，单包足够）。</summary>
        private static byte[] PackSection(int pid, byte[] section, ref int cc)
        {
            var pkt = new byte[TsPacketSize];
            for (int i = 0; i < TsPacketSize; i++) pkt[i] = 0xFF;
            pkt[0] = 0x47;
            pkt[1] = (byte)(0x40 | ((pid >> 8) & 0x1F)); // payload_unit_start_indicator = 1
            pkt[2] = (byte)(pid & 0xFF);
            pkt[3] = (byte)(0x10 | (cc & 0x0F));         // payload only
            cc = (cc + 1) & 0x0F;
            pkt[4] = 0x00;                               // pointer_field
            int n = Math.Min(section.Length, TsPacketSize - 5);
            Buffer.BlockCopy(section, 0, pkt, 5, n);
            return pkt;
        }

        /* ---------------- 帧 → PES → TS ---------------- */

        /// <summary>把一帧 Annex-B 打成完整的 TS 包序列（含必要的 PAT/PMT）。</summary>
        public byte[] BuildFrame(byte[] annexb, bool keyFrame, int fps)
        {
            if (annexb == null || annexb.Length == 0) return new byte[0];

            long pts = _pts;
            _pts += 90000L / Math.Max(1, fps);

            var outp = new List<byte>(annexb.Length + 1024);
            if (keyFrame || _sinceTables >= 30)
            {
                outp.AddRange(BuildTables());
                _sinceTables = 0;
            }
            _sinceTables++;

            byte[] pes = BuildPes(annexb, pts);
            WritePesPackets(pes, keyFrame, pts, outp);
            return outp.ToArray();
        }

        private static byte[] BuildPes(byte[] payload, long pts)
        {
            using (var ms = new MemoryStream(payload.Length + 14))
            {
                ms.Write(new byte[] { 0x00, 0x00, 0x01, 0xE0 }, 0, 4); // start code + stream_id
                ms.Write(new byte[] { 0x00, 0x00 }, 0, 2);             // PES_packet_length（视频可填 0）
                ms.WriteByte(0x80);                                    // '10' + PES_scrambling/priority 全 0
                ms.WriteByte(0x80);                                    // PTS_DTS_flags = 10
                ms.WriteByte(0x05);                                    // PES_header_data_length
                ms.Write(PtsBytes(pts), 0, 5);
                ms.Write(payload, 0, payload.Length);
                return ms.ToArray();
            }
        }

        private static byte[] PtsBytes(long pts)
        {
            var b = new byte[5];
            b[0] = (byte)(0x21 | ((pts >> 29) & 0x0E));
            b[1] = (byte)((pts >> 22) & 0xFF);
            b[2] = (byte)(0x01 | ((pts >> 14) & 0xFE));
            b[3] = (byte)((pts >> 7) & 0xFF);
            b[4] = (byte)(0x01 | ((pts << 1) & 0xFE));
            return b;
        }

        private void WritePesPackets(byte[] pes, bool withPcr, long pts, List<byte> outp)
        {
            int off = 0;
            bool first = true;
            while (off < pes.Length)
            {
                var pkt = new byte[TsPacketSize];
                int remain = pes.Length - off;

                int afLen = 0; // 含 1 字节长度字段的完整适配字段长度
                if (first && withPcr) afLen = 8;              // len + flags + 6 字节 PCR
                else if (remain < 184) afLen = 184 - remain;  // 末包用适配字段填充

                pkt[0] = 0x47;
                pkt[1] = (byte)((first ? 0x40 : 0x00) | ((VideoPid >> 8) & 0x1F));
                pkt[2] = (byte)(VideoPid & 0xFF);

                if (afLen == 0)
                {
                    pkt[3] = (byte)(0x10 | (_ccVid & 0x0F));
                }
                else
                {
                    pkt[3] = (byte)(0x30 | (_ccVid & 0x0F)); // adaptation + payload
                    pkt[4] = (byte)(afLen - 1);
                    if (afLen >= 2) pkt[5] = (byte)((first && withPcr) ? 0x10 : 0x00);
                    if (first && withPcr)
                    {
                        long b = pts & 0x1FFFFFFFFL;
                        pkt[6] = (byte)(b >> 25);
                        pkt[7] = (byte)(b >> 17);
                        pkt[8] = (byte)(b >> 9);
                        pkt[9] = (byte)(b >> 1);
                        pkt[10] = (byte)(((b & 1) << 7) | 0x7E);
                        pkt[11] = 0x00;
                    }
                    // 其余填充字节保持 0（规范要求 stuffing 为 0xFF，播放器不敏感，但这里用 0xFF 更标准）
                    for (int i = 6 + ((first && withPcr) ? 6 : 0); i < 4 + afLen; i++) pkt[i] = 0xFF;
                }

                _ccVid = (_ccVid + 1) & 0x0F;

                int payloadOff = 4 + afLen;
                int space = TsPacketSize - payloadOff;
                int n = Math.Min(space, remain);
                if (n > 0) Buffer.BlockCopy(pes, off, pkt, payloadOff, n);
                off += n;
                first = false;
                outp.AddRange(pkt);
            }
        }
    }
}

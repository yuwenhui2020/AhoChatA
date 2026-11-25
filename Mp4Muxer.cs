using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// 极简 fMP4（fragmented MP4）封装器：把 H.264 裸流打成浏览器 MSE 可播放的分片 MP4。
    /// - BuildInit：ftyp + moov（含 avcC），每个查看端会话开头播放一次；
    /// - BuildSegment：moof + mdat，每个分片都以 IDR 关键帧开头。
    /// 只做纯视频、无音轨、单 track，够用且体积小。
    /// </summary>
    internal static class Mp4Muxer
    {
        public const int Timescale = 90000;
        private const int TrackId = 1;

        /* ---------------- 基础工具 ---------------- */

        private static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

        private static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void U32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }
        private static void U64(Stream s, ulong v) { U32(s, (uint)(v >> 32)); U32(s, (uint)v); }

        private static void PatchU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }

        private static byte[] Cat(params byte[][] parts)
        {
            int total = 0;
            for (int i = 0; i < parts.Length; i++) if (parts[i] != null) total += parts[i].Length;
            var r = new byte[total];
            int o = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p == null || p.Length == 0) continue;
                Buffer.BlockCopy(p, 0, r, o, p.Length);
                o += p.Length;
            }
            return r;
        }

        private static byte[] Box(string type, params byte[][] parts)
        {
            using (var ms = new MemoryStream())
            {
                U32(ms, 0);
                var t = Ascii(type);
                ms.Write(t, 0, 4);
                foreach (var p in parts) if (p != null && p.Length > 0) ms.Write(p, 0, p.Length);
                var b = ms.ToArray();
                PatchU32(b, 0, (uint)b.Length);
                return b;
            }
        }

        private static byte[] FullBox(string type, byte ver, uint flags, params byte[][] parts)
        {
            var all = new List<byte[]>(parts.Length + 1);
            all.Add(new byte[] { ver, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags });
            all.AddRange(parts);
            return Box(type, all.ToArray());
        }

        /// <summary>去掉 Annex-B 起始码，返回裸 NAL。</summary>
        public static byte[] StripStartCode(byte[] nal)
        {
            if (nal == null) return null;
            int off = 0;
            if (nal.Length > 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) off = 4;
            else if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) off = 3;
            if (off == 0) return nal;
            var r = new byte[nal.Length - off];
            Buffer.BlockCopy(nal, off, r, 0, r.Length);
            return r;
        }

        private static byte[] IdentityMatrix()
        {
            return new byte[]
            {
                0x00,0x01,0x00,0x00, 0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00, 0x00,0x01,0x00,0x00, 0x00,0x00,0x00,0x00,
                0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00, 0x40,0x00,0x00,0x00
            };
        }

        /// <summary>由 SPS/PPS 生成 avcC（AVCDecoderConfigurationRecord）。</summary>
        private static byte[] BuildAvcC(byte[] sps, byte[] pps)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x01);                              // configurationVersion
                ms.WriteByte(sps.Length > 1 ? sps[1] : (byte)0x42); // AVCProfileIndication
                ms.WriteByte(sps.Length > 2 ? sps[2] : (byte)0x00); // profile_compatibility
                ms.WriteByte(sps.Length > 3 ? sps[3] : (byte)0x1F); // AVCLevelIndication
                ms.WriteByte(0xFF);                              // reserved(6) + lengthSizeMinusOne=3
                ms.WriteByte(0xE1);                              // reserved(3) + numOfSequenceParameterSets=1
                U16(ms, sps.Length); ms.Write(sps, 0, sps.Length);
                ms.WriteByte(0x01);                              // numOfPictureParameterSets
                U16(ms, pps.Length); ms.Write(pps, 0, pps.Length);
                return Box("avcC", ms.ToArray());
            }
        }

        /* ---------------- 初始化段（ftyp + moov） ---------------- */

        public static byte[] BuildInit(int width, int height, byte[] spsAnnexB, byte[] ppsAnnexB, int fps)
        {
            var sps = StripStartCode(spsAnnexB);
            var pps = StripStartCode(ppsAnnexB);
            if (sps == null || pps == null || sps.Length < 5 || pps.Length < 2) return null;

            int sampleDur = Math.Max(1, Timescale / Math.Max(1, fps));

            byte[] ftyp = Box("ftyp", Ascii("isom"), new byte[] { 0x00, 0x00, 0x02, 0x00 },
                Ascii("isom"), Ascii("iso6"), Ascii("avc1"), Ascii("mp41"));

            // mvhd
            byte[] mvhd;
            using (var ms = new MemoryStream())
            {
                U32(ms, 0); U32(ms, 0);                 // creation/modification time
                U32(ms, Timescale);                     // timescale
                U32(ms, 0);                             // duration（分片流填 0）
                U32(ms, 0x00010000);                    // rate 1.0
                U16(ms, 0x0100);                        // volume 1.0
                U16(ms, 0);                             // reserved
                U32(ms, 0); U32(ms, 0);                 // reserved[2]
                ms.Write(IdentityMatrix(), 0, 36);
                for (int i = 0; i < 6; i++) U32(ms, 0); // pre_defined[6]
                U32(ms, 2);                             // next_track_ID
                mvhd = FullBox("mvhd", 0, 0, ms.ToArray());
            }

            // tkhd
            byte[] tkhd;
            using (var ms = new MemoryStream())
            {
                U32(ms, 0); U32(ms, 0);
                U32(ms, TrackId);
                U32(ms, 0);                             // reserved
                U32(ms, 0);                             // duration
                U32(ms, 0); U32(ms, 0);                 // reserved[2]
                U16(ms, 0);                             // layer
                U16(ms, 0);                             // alternate_group
                U16(ms, 0);                             // volume（视频恒 0）
                U16(ms, 0);                             // reserved
                ms.Write(IdentityMatrix(), 0, 36);
                U32(ms, (uint)width << 16);             // width 16.16
                U32(ms, (uint)height << 16);            // height 16.16
                tkhd = FullBox("tkhd", 0, 0x000003, ms.ToArray()); // enabled|in_movie|in_preview
            }

            // mdhd
            byte[] mdhd;
            using (var ms = new MemoryStream())
            {
                U32(ms, 0); U32(ms, 0);
                U32(ms, Timescale);
                U32(ms, 0);
                U16(ms, 0x55C4);                        // language = "und"
                U16(ms, 0);
                mdhd = FullBox("mdhd", 0, 0, ms.ToArray());
            }

            byte[] hdlr = FullBox("hdlr", 0, 0,
                new byte[] { 0, 0, 0, 0 }, Ascii("vide"), new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                Ascii("VideoHandler\0"));

            byte[] vmhd = FullBox("vmhd", 0, 1, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

            byte[] dref = FullBox("dref", 0, 0, new byte[] { 0, 0, 0, 1 }, FullBox("url ", 0, 1));
            byte[] dinf = Box("dinf", dref);

            // avc1
            byte[] avc1;
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[6], 0, 6);            // reserved
                U16(ms, 1);                             // data_reference_index
                U16(ms, 0); U16(ms, 0);                 // pre_defined, reserved
                for (int i = 0; i < 3; i++) U32(ms, 0);  // pre_defined[3]
                U16(ms, width); U16(ms, height);
                U32(ms, 0x00480000); U32(ms, 0x00480000); // 72dpi
                U32(ms, 0);                             // reserved
                U16(ms, 1);                             // frame_count
                var cn = new byte[32]; Encoding.ASCII.GetBytes("Mchat").CopyTo(cn, 0);
                ms.Write(cn, 0, 32);                    // compressorname
                U16(ms, 0x0018);                        // depth
                ms.Write(new byte[] { 0xFF, 0xFF }, 0, 2); // pre_defined = -1
                var avcC = BuildAvcC(sps, pps);
                ms.Write(avcC, 0, avcC.Length);
                avc1 = Box("avc1", ms.ToArray());
            }

            byte[] stsd = FullBox("stsd", 0, 0, new byte[] { 0, 0, 0, 1 }, avc1);
            byte[] stts = FullBox("stts", 0, 0, new byte[] { 0, 0, 0, 0 });
            byte[] stsc = FullBox("stsc", 0, 0, new byte[] { 0, 0, 0, 0 });
            byte[] stsz = FullBox("stsz", 0, 0, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 });
            byte[] stco = FullBox("stco", 0, 0, new byte[] { 0, 0, 0, 0 });
            byte[] stbl = Box("stbl", stsd, stts, stsc, stsz, stco);

            byte[] minf = Box("minf", vmhd, dinf, stbl);
            byte[] mdia = Box("mdia", mdhd, hdlr, minf);
            byte[] trak = Box("trak", tkhd, mdia);

            // mvex/trex：分片流的默认样本参数
            byte[] trex = FullBox("trex", 0, 0, new byte[] { 0, 0, 0, 1 },
                new byte[] { 0, 0, 0, 1 },                            // default_sample_description_index
                U32Bytes((uint)sampleDur),                            // default_sample_duration
                new byte[] { 0, 0, 0, 0 },                            // default_sample_size
                new byte[] { 0, 0, 0, 0 });                           // default_sample_flags
            byte[] mvex = Box("mvex", trex);

            byte[] moov = Box("moov", mvhd, trak, mvex);
            return Cat(ftyp, moov);
        }

        private static byte[] U32Bytes(uint v)
        {
            return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        /* ---------------- 媒体分片（moof + mdat） ---------------- */

        /// <summary>
        /// 生成一个分片。samples 里每个元素是 AVCC 格式（4 字节长度前缀 + NAL），
        /// 且第 0 个样本必须是关键帧。
        /// </summary>
        public static byte[] BuildSegment(IList<byte[]> samples, int sampleDuration, long baseDecodeTime, uint sequenceNumber)
        {
            if (samples == null || samples.Count == 0) return null;
            if (sampleDuration <= 0) sampleDuration = 3000;

            int totalBytes = 0;
            for (int i = 0; i < samples.Count; i++) totalBytes += samples[i].Length;

            byte[] mfhd = FullBox("mfhd", 0, 0, U32Bytes(sequenceNumber));
            byte[] tfhd = FullBox("tfhd", 0, 0x020000, U32Bytes(TrackId)); // default-base-is-moof
            byte[] tfdt = FullBox("tfdt", 1, 0, U64Bytes((ulong)baseDecodeTime));

            using (var ms = new MemoryStream())
            {
                U32(ms, 0);                              // moof 长度占位
                ms.Write(Ascii("moof"), 0, 4);
                ms.Write(mfhd, 0, mfhd.Length);

                long trafStart = ms.Position;
                U32(ms, 0);                              // traf 长度占位
                ms.Write(Ascii("traf"), 0, 4);
                ms.Write(tfhd, 0, tfhd.Length);
                ms.Write(tfdt, 0, tfdt.Length);

                long trunStart = ms.Position;
                U32(ms, 0);                              // trun 长度占位
                ms.Write(Ascii("trun"), 0, 4);
                // flags: data-offset(0x1) + duration(0x100) + size(0x200) + sample-flags(0x400)
                ms.Write(new byte[] { 0, 0, 0x07, 0x01 }, 0, 4);
                U32(ms, (uint)samples.Count);

                long dataOffsetPos = ms.Position;
                U32(ms, 0);                              // data_offset 占位

                for (int i = 0; i < samples.Count; i++)
                {
                    U32(ms, (uint)sampleDuration);
                    U32(ms, (uint)samples[i].Length);
                    // 关键帧：depends_on=2, non_sync=0；普通帧：depends_on=1, non_sync=1
                    U32(ms, i == 0 ? 0x02000000u : 0x01010000u);
                }

                long moofEnd = ms.Position;
                var moof = ms.ToArray();

                PatchU32(moof, 0, (uint)moofEnd);
                PatchU32(moof, (int)trafStart, (uint)(moofEnd - trafStart));
                PatchU32(moof, (int)trunStart, (uint)(moofEnd - trunStart));
                PatchU32(moof, (int)dataOffsetPos, (uint)(moofEnd + 8)); // mdat 头 8 字节

                var mdatHeader = Cat(U32Bytes((uint)(totalBytes + 8)), Ascii("mdat"));
                var outp = new byte[moof.Length + 8 + totalBytes];
                Buffer.BlockCopy(moof, 0, outp, 0, moof.Length);
                Buffer.BlockCopy(mdatHeader, 0, outp, moof.Length, 8);
                int o = moof.Length + 8;
                for (int i = 0; i < samples.Count; i++)
                {
                    Buffer.BlockCopy(samples[i], 0, outp, o, samples[i].Length);
                    o += samples[i].Length;
                }
                return outp;
            }
        }

        private static byte[] U64Bytes(ulong v)
        {
            var r = new byte[8];
            r[0] = (byte)(v >> 56); r[1] = (byte)(v >> 48); r[2] = (byte)(v >> 40); r[3] = (byte)(v >> 32);
            r[4] = (byte)(v >> 24); r[5] = (byte)(v >> 16); r[6] = (byte)(v >> 8); r[7] = (byte)v;
            return r;
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// 极简 protobuf 编码器。
    ///
    /// 为什么不引入 Google.Protobuf / protobuf-net：本次只需要 qaul 的十几个
    /// 消息结构（均为「标量 + 长度分隔」两三种线型），手写编码器零依赖、
    /// 体积小、且完全可控（AOT/裁剪友好），避免为几十行协议引入整套运行时。
    /// </summary>
    public sealed class PbWriter
    {
        private readonly MemoryStream ms = new MemoryStream();

        public void Raw(byte[] data)
        {
            if (data != null && data.Length > 0) ms.Write(data, 0, data.Length);
        }

        public void Tag(int field, int wire)
        {
            Varint((ulong)((field << 3) | wire));
        }

        public void Varint(ulong value)
        {
            while (value >= 0x80)
            {
                ms.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            ms.WriteByte((byte)value);
        }

        /// <summary>varint 字段（int32/int64/uint32/uint64/bool/enum 共用）。</summary>
        public PbWriter Int(int field, long value)
        {
            Tag(field, 0);
            Varint((ulong)value);
            return this;
        }

        public PbWriter UInt(int field, ulong value)
        {
            Tag(field, 0);
            Varint(value);
            return this;
        }

        public PbWriter Bool(int field, bool value)
        {
            Tag(field, 0);
            Varint(value ? 1UL : 0UL);
            return this;
        }

        /// <summary>长度分隔字段（string/bytes/嵌套消息）。</summary>
        public PbWriter Bytes(int field, byte[] value)
        {
            if (value == null) return this;
            Tag(field, 2);
            Varint((ulong)value.Length);
            ms.Write(value, 0, value.Length);
            return this;
        }

        public PbWriter Str(int field, string value)
        {
            if (value == null) return this;
            return Bytes(field, Encoding.UTF8.GetBytes(value));
        }

        public PbWriter Sub(int field, PbWriter sub)
        {
            return Bytes(field, sub.ToArray());
        }

        public byte[] ToArray()
        {
            return ms.ToArray();
        }

        public int Length
        {
            get { return (int)ms.Length; }
        }
    }

    /// <summary>极简 protobuf 解码器（容忍未知字段，按 qaul 前向兼容策略丢弃）。</summary>
    public sealed class PbReader
    {
        private readonly byte[] buf;
        private int pos;
        private readonly int end;

        public PbReader(byte[] data) : this(data, 0, data == null ? 0 : data.Length) { }

        public PbReader(byte[] data, int offset, int length)
        {
            buf = data ?? new byte[0];
            pos = offset;
            end = offset + length;
        }

        public bool Eof { get { return pos >= end; } }

        /// <summary>读取下一个字段头；字段号与线型通过 out 返回。</summary>
        public bool Next(out int field, out int wire)
        {
            field = 0;
            wire = 0;
            if (pos >= end) return false;
            var tag = Varint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 0x7);
            return field > 0;
        }

        public ulong Varint()
        {
            ulong result = 0;
            int shift = 0;
            while (pos < end)
            {
                var b = buf[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 63) break;
            }
            return result;
        }

        public byte[] Bytes()
        {
            var len = (int)Varint();
            if (len < 0) { pos = end; return new byte[0]; }
            // 长度为 0 的字段是合法的（例如空字符串明文 + 后续扩展字段），
            // 此时绝不能把 pos 推到末尾，否则后面的字段会被整段丢弃。
            if (len == 0) return new byte[0];
            if (pos + len > end) { pos = end; return new byte[0]; }
            var data = new byte[len];
            Buffer.BlockCopy(buf, pos, data, 0, len);
            pos += len;
            return data;
        }

        public string Str()
        {
            var bytes = Bytes();
            return bytes.Length == 0 ? "" : Encoding.UTF8.GetString(bytes);
        }

        public PbReader Sub()
        {
            var bytes = Bytes();
            return new PbReader(bytes);
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: pos += 8; break;
                case 2: Bytes(); break;
                case 5: pos += 4; break;
                default: pos = end; break;
            }
            if (pos > end) pos = end;
        }
    }
}

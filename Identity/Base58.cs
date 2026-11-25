using System;
using System.Numerics;
using System.Text;

namespace AhoChatA
{
    /// <summary>
    /// Base58（Bitcoin 字母表）。qaul 的 user id / PeerId 在 UI 与 RPC 里都用 Base58 呈现，
    /// 要和它对齐（例如 `12D3KooW...`）就必须有这一层。
    /// </summary>
    public static class Base58
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        public static string Encode(byte[] data)
        {
            if (data == null || data.Length == 0) return "";

            int zeros = 0;
            while (zeros < data.Length && data[zeros] == 0) zeros++;

            var value = new BigInteger();
            // Base58 是大端整数：data[0] 是最高字节
            for (int i = 0; i < data.Length; i++)
                value = value * 256 + data[i];

            var sb = new StringBuilder();
            while (value > 0)
            {
                var rem = (int)(value % 58);
                value /= 58;
                sb.Insert(0, Alphabet[rem]);
            }
            for (int i = 0; i < zeros; i++) sb.Insert(0, '1');
            return sb.ToString();
        }

        public static byte[] Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return new byte[0];

            var value = BigInteger.Zero;
            foreach (var c in text)
            {
                int idx = Alphabet.IndexOf(c);
                if (idx < 0) throw new FormatException("invalid base58 char: " + c);
                value = value * 58 + idx;
            }

            var bytes = new System.Collections.Generic.List<byte>();
            while (value > 0)
            {
                bytes.Insert(0, (byte)(value & 0xFF));
                value >>= 8;
            }
            int zeros = 0;
            while (zeros < text.Length && text[zeros] == '1') zeros++;
            for (int i = 0; i < zeros; i++) bytes.Insert(0, 0);
            return bytes.ToArray();
        }
    }
}

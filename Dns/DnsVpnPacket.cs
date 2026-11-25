using System;

namespace AhoChatA.DnsCore
{
    [Flags]
    internal enum DnsTcpFlags : byte
    {
        Fin = 1, Syn = 2, Rst = 4, Psh = 8, Ack = 16
    }

    internal struct DnsTunPeer : IEquatable<DnsTunPeer>
    {
        public uint Address;
        public ushort Port;

        public bool Equals(DnsTunPeer other) => Address == other.Address && Port == other.Port;
        public override bool Equals(object other) => other is DnsTunPeer peer && Equals(peer);
        public override int GetHashCode() => unchecked((int)Address * 397 ^ Port);
    }

    internal struct DnsTcpSegment
    {
        public DnsTunPeer Peer;
        public uint Sequence, Ack;
        public DnsTcpFlags Flags;
        public int Window, Mss, PayloadOffset, PayloadLength;
        public bool Has(DnsTcpFlags flag) => (Flags & flag) != 0;
    }

    /// <summary>IPv4 framing for the DNS TUN route, independent of Android and sockets.</summary>
    internal static class DnsVpnPacket
    {
        internal const uint VirtualDns = 0x0a4d0002; // 10.77.0.2
        internal const int Mtu = 1500;
        private static int U16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];
        private static uint U32(byte[] data, int offset) => (uint)U16(data, offset) << 16 | (uint)U16(data, offset + 2);
        private static void Put16(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 8);
            data[offset + 1] = (byte)value;
        }

        private static void Put32(byte[] data, int offset, uint value)
        {
            Put16(data, offset, (int)(value >> 16));
            Put16(data, offset + 2, (int)value);
        }

        public static bool TryReadTcp(byte[] packet, int length, out DnsTcpSegment segment)
        {
            segment = default(DnsTcpSegment);
            if (packet == null || length > packet.Length || length < 40 || packet[0] >> 4 != 4 || packet[9] != 6) return false;
            int ip = (packet[0] & 15) * 4;
            int total = U16(packet, 2);
            if (ip < 20 || total > length || total < ip + 20 || (U16(packet, 6) & 0xbfff) != 0) return false;
            if (U32(packet, 16) != VirtualDns || U16(packet, ip + 2) != 53 || U16(packet, ip) == 0) return false;
            int tcp = (packet[ip + 12] >> 4) * 4;
            if (tcp < 20 || ip + tcp > total || Fold(Sum(packet, 0, ip)) != 65535 ||
                Fold(TcpSum(packet, ip, total - ip)) != 65535) return false;

            int mss = 536; // IPv4 default when the peer does not advertise MSS.
            for (int offset = ip + 20; offset < ip + tcp;)
            {
                int kind = packet[offset++];
                if (kind == 0) break;
                if (kind == 1) continue;
                if (offset >= ip + tcp) return false;
                int size = packet[offset++];
                if (size < 2 || offset + size - 2 > ip + tcp) return false;
                if (kind == 2 && size == 4)
                {
                    int requested = U16(packet, offset);
                    if (requested != 0) mss = Math.Min(requested, Mtu - 40);
                }
                offset += size - 2;
            }
            segment = new DnsTcpSegment
            {
                Peer = new DnsTunPeer { Address = U32(packet, 12), Port = (ushort)U16(packet, ip) },
                Sequence = U32(packet, ip + 4), Ack = U32(packet, ip + 8),
                Flags = (DnsTcpFlags)packet[ip + 13], Window = U16(packet, ip + 14), Mss = mss,
                PayloadOffset = ip + tcp, PayloadLength = total - ip - tcp
            };
            return true;
        }

        public static byte[] CreateTcp(DnsTunPeer peer, uint seq, uint ack, DnsTcpFlags flags, int window,
            byte[] payload = null, int offset = 0, int count = 0)
        {
            int tcp = (flags & DnsTcpFlags.Syn) != 0 ? 24 : 20;
            var reply = new byte[20 + tcp + count];
            WriteIp(reply, 6, VirtualDns, peer.Address);
            Put16(reply, 20, 53);
            Put16(reply, 22, peer.Port);
            Put32(reply, 24, seq);
            Put32(reply, 28, ack);
            reply[32] = (byte)(tcp << 2);
            reply[33] = (byte)flags;
            Put16(reply, 34, window);
            if (tcp == 24)
            {
                reply[40] = 2;
                reply[41] = 4;
                Put16(reply, 42, Mtu - 40);
            }
            if (count != 0) Buffer.BlockCopy(payload, offset, reply, 20 + tcp, count);
            Put16(reply, 36, ~Fold(TcpSum(reply, 20, tcp + count)));
            return reply;
        }

        private static void WriteIp(byte[] packet, byte protocol, uint source, uint destination)
        {
            packet[0] = 0x45;
            Put16(packet, 2, packet.Length);
            if (protocol == 6) Put16(packet, 6, 0x4000); // TCP segments already fit the TUN MTU.
            packet[8] = 64;
            packet[9] = protocol;
            Put32(packet, 12, source);
            Put32(packet, 16, destination);
            Put16(packet, 10, ~Fold(Sum(packet, 0, 20)));
        }

        private static int TcpSum(byte[] packet, int offset, int length) =>
            Sum(packet, 12, 8) + 6 + length + Sum(packet, offset, length);

        private static int Fold(int sum)
        {
            while ((sum >> 16) != 0) sum = (sum & 65535) + (sum >> 16);
            return sum;
        }

        private static int Sum(byte[] data, int offset, int count)
        {
            int sum = 0;
            int end = offset + count;
            for (; offset + 1 < end; offset += 2) sum += U16(data, offset);
            if (offset < end) sum += data[offset] << 8;
            return sum;
        }

        public static bool TryReadUdp(byte[] packet, int length, out DnsTunPeer peer, out byte[] query)
        {
            peer = default(DnsTunPeer);
            query = null;
            if (packet == null || length < 20 || length > packet.Length || U32(packet, 16) != VirtualDns ||
                !TryReadQuery(packet, length, out int header, out query)) return false;
            peer = new DnsTunPeer { Address = U32(packet, 12), Port = (ushort)U16(packet, header) };
            return true;
        }

        public static bool TryReadQuery(byte[] packet, int length, out int header, out byte[] query)
        {
            header = 0;
            query = null;
            if (packet == null || length > packet.Length || length < 28 || packet[0] >> 4 != 4 || packet[9] != 17) return false;
            header = (packet[0] & 15) * 4;
            int total = U16(packet, 2);
            if (header < 20 || total > length || total < header + 8 || (U16(packet, 6) & 0x3fff) != 0) return false;
            int udpLength = U16(packet, header + 4);
            if (U16(packet, header + 2) != 53 || udpLength < 20 || header + udpLength > total) return false;
            query = new byte[udpLength - 8];
            Buffer.BlockCopy(packet, header + 8, query, 0, query.Length);
            return true;
        }

        // The request/header pair must come from a successful TryReadQuery call.
        public static byte[] CreateReply(byte[] packet, int header, byte[] answer)
        {
            return CreateUdpReply(new DnsTunPeer { Address = U32(packet, 12), Port = (ushort)U16(packet, header) }, answer);
        }

        public static byte[] CreateUdpReply(DnsTunPeer peer, byte[] answer)
        {
            if (answer == null) throw new ArgumentNullException(nameof(answer));
            if (answer.Length > 65507) throw new ArgumentOutOfRangeException(nameof(answer));
            var reply = new byte[28 + answer.Length];
            WriteIp(reply, 17, VirtualDns, peer.Address);
            Put16(reply, 20, 53);
            Put16(reply, 22, peer.Port);
            Put16(reply, 24, answer.Length + 8);
            // IPv4 permits a zero UDP checksum.
            Buffer.BlockCopy(answer, 0, reply, 28, answer.Length);
            return reply;
        }

        public static byte[] TruncateUdpAnswer(byte[] query, byte[] answer)
        {
            if (answer.Length <= Mtu - 28) return answer;
            // Preserve a conventional uncompressed question so the resolver can match the TC response
            // to its query. Drop sections wholesale so no partial RR or dangling pointer is emitted.
            int questionEnd = 12;
            if (U16(query, 4) == 1)
            {
                int offset = 12;
                while (offset < query.Length && offset < 12 + 255)
                {
                    int label = query[offset++];
                    if (label == 0)
                    {
                        if (offset + 4 <= query.Length) questionEnd = offset + 4;
                        break;
                    }
                    if (label > 63 || offset + label > query.Length) break;
                    offset += label;
                }
            }
            var truncated = new byte[questionEnd];
            Buffer.BlockCopy(answer, 0, truncated, 0, 4);
            truncated[2] |= 2; // TC
            if (questionEnd > 12)
            {
                truncated[5] = 1;
                Buffer.BlockCopy(query, 12, truncated, 12, questionEnd - 12);
            }
            return truncated;
        }
    }
}

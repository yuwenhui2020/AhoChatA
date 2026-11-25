using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AhoChatA.DnsCore
{
    /// <summary>Local-only exchanges with the DNS server. Socket cancellation closes the socket on legacy Xamarin.</summary>
    internal sealed class DnsTunLoopback
    {
        private readonly int _port;
        private readonly CancellationToken _vpnToken;

        public DnsTunLoopback(int port, CancellationToken vpnToken)
        {
            _port = port;
            _vpnToken = vpnToken;
        }

        public async Task<byte[]> TcpQuery(byte[] query, CancellationToken token)
        {
            using (var timeout = CreateTimeout(token))
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            using (timeout.Token.Register(() => client.Close()))
            {
                await client.ConnectAsync(IPAddress.Loopback, _port).ConfigureAwait(false);
                using (var stream = client.GetStream())
                {
                    var request = new byte[query.Length + 2];
                    request[0] = (byte)(query.Length >> 8);
                    request[1] = (byte)query.Length;
                    Buffer.BlockCopy(query, 0, request, 2, query.Length);
                    await stream.WriteAsync(request, 0, request.Length, timeout.Token).ConfigureAwait(false);
                    var prefix = new byte[2];
                    await ReadExact(stream, prefix, timeout.Token).ConfigureAwait(false);
                    int length = (prefix[0] << 8) | prefix[1];
                    if (length < 12) throw new IOException("Invalid DNS TCP response length");
                    var answer = new byte[length];
                    await ReadExact(stream, answer, timeout.Token).ConfigureAwait(false);
                    return answer;
                }
            }
        }

        public async Task<byte[]> UdpQuery(byte[] query, CancellationToken token)
        {
            using (var timeout = CreateTimeout(token))
            using (var client = new UdpClient(AddressFamily.InterNetwork))
            using (timeout.Token.Register(() => client.Close()))
            {
                client.Connect(IPAddress.Loopback, _port);
                await client.SendAsync(query, query.Length).ConfigureAwait(false);
                return (await client.ReceiveAsync().ConfigureAwait(false)).Buffer;
            }
        }

        private CancellationTokenSource CreateTimeout(CancellationToken token)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(token, _vpnToken);
            source.CancelAfter(15000);
            return source;
        }

        private static async Task ReadExact(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }
    }
}

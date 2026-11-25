using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AhoChatA.DnsCore
{
    /// <summary>
    /// Passive TCP endpoint and UDP dispatcher for 10.77.0.2:53. The TCP endpoint accepts
    /// in-order bytes, uses cumulative ACKs, and keeps at most one output segment in flight.
    /// Gaps are ACKed at RCV.NXT so the peer can recover by retransmitting. No TCP extensions
    /// other than MSS are negotiated. Call Tick periodically even when TUN is idle.
    /// </summary>
    internal sealed class DnsTcpTun : IDisposable
    {
        internal const int MaxFlows = 32;
        internal const int MaxUdpJobs = 32;
        private const int BufferSize = 65535 + 2;
        private const int IdleMs = 30000;
        private const int InitialRetryMs = 500;
        private const int MaxRetries = 6;
        private const int MaxClosed = 128;

        private sealed class Flow
        {
            public DnsTunPeer Peer;
            public uint PeerIsn, RcvNext, SndUna, SndNext, WlSeq, WlAck;
            public int PeerWindow, Mss, Retries;
            public bool Established, PeerFin, SentFin, FinAcked;
            public long Progress, RetryAt;
            public byte[] Input, Reply;
            public int Buffered, ReplyOffset;
            public CancellationTokenSource Job;
            public int Window => PeerFin ? 0 : Math.Min(65535, BufferSize - Buffered);
        }

        private struct ClosedFlow
        {
            public uint Seq, Ack;
            public long Expires;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<DnsTunPeer, Flow> _flows = new Dictionary<DnsTunPeer, Flow>();
        private readonly Dictionary<DnsTunPeer, ClosedFlow> _closed = new Dictionary<DnsTunPeer, ClosedFlow>();
        private readonly Func<byte[], CancellationToken, Task<byte[]>> _tcpQuery, _udpQuery;
        private readonly Action<byte[]> _write;
        private readonly Func<long> _now;
        private readonly CancellationTokenSource _udpCancellation = new CancellationTokenSource();
        private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();
        private readonly byte[] _randomBytes = new byte[4];
        private int _tcpJobs, _udpJobs;
        private bool _disposed;

        public DnsTcpTun(Func<byte[], CancellationToken, Task<byte[]>> tcpQuery,
            Func<byte[], CancellationToken, Task<byte[]>> udpQuery, Action<byte[]> write, Func<long> monotonicMilliseconds)
        {
            _tcpQuery = tcpQuery;
            _udpQuery = udpQuery;
            _write = write;
            _now = monotonicMilliseconds;
        }

        internal int FlowCount { get { lock (_gate) return _flows.Count; } }
        private static int Delta(uint a, uint b) => unchecked((int)(a - b));
        private static uint Advance(uint seq, int count) => unchecked(seq + (uint)count);

        // Packet memory belongs to the caller and may be overwritten immediately after return.
        public void Accept(byte[] packet, int length)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (DnsVpnPacket.TryReadTcp(packet, length, out DnsTcpSegment segment))
                    AcceptTcp(packet, segment, _now());
                else if (_udpJobs < MaxUdpJobs && DnsVpnPacket.TryReadUdp(packet, length, out DnsTunPeer peer, out byte[] query))
                {
                    _udpJobs++;
                    var token = _udpCancellation.Token;
                    _ = Task.Run(() => ForwardUdp(peer, query, token));
                }
            }
        }

        private async Task ForwardUdp(DnsTunPeer peer, byte[] query, CancellationToken token)
        {
            byte[] answer = null;
            try { if (!token.IsCancellationRequested) answer = await _udpQuery(query, token).ConfigureAwait(false); }
            catch (Exception) { } // A failed local exchange drops only this UDP datagram.
            lock (_gate)
            {
                _udpJobs--;
                if (_disposed)
                {
                    if (_udpJobs == 0) _udpCancellation.Dispose();
                }
                else if (answer != null && answer.Length >= 12 && answer.Length <= 65507)
                    _write(DnsVpnPacket.CreateUdpReply(peer, DnsVpnPacket.TruncateUdpAnswer(query, answer)));
            }
        }

        private void AcceptTcp(byte[] packet, DnsTcpSegment segment, long now)
        {
            bool syn = segment.Has(DnsTcpFlags.Syn);
            bool ack = segment.Has(DnsTcpFlags.Ack);
            bool fin = segment.Has(DnsTcpFlags.Fin);
            bool rst = segment.Has(DnsTcpFlags.Rst);
            if (!_flows.TryGetValue(segment.Peer, out Flow flow))
            {
                if (rst) return;
                if (_closed.TryGetValue(segment.Peer, out ClosedFlow closed) && closed.Expires > now)
                {
                    if (fin || segment.PayloadLength != 0)
                        _write(DnsVpnPacket.CreateTcp(segment.Peer, closed.Seq, closed.Ack, DnsTcpFlags.Ack, 0));
                    return;
                }
                if (!syn || ack || fin || _flows.Count >= MaxFlows)
                {
                    SendClosedReset(segment);
                    return;
                }
                _random.GetBytes(_randomBytes);
                uint sequence = BitConverter.ToUInt32(_randomBytes, 0);
                flow = new Flow
                {
                    Peer = segment.Peer, PeerIsn = segment.Sequence, RcvNext = Advance(segment.Sequence, 1),
                    SndUna = sequence, SndNext = Advance(sequence, 1), WlSeq = segment.Sequence,
                    PeerWindow = segment.Window, Mss = segment.Mss, Progress = now
                };
                _flows.Add(flow.Peer, flow);
                ArmRetry(flow, now);
                Send(flow, flow.SndUna, DnsTcpFlags.Syn | DnsTcpFlags.Ack);
                return; // Bare SYN handshake. The peer can retransmit any SYN payload after it.
            }

            if (rst)
            {
                if (segment.Sequence == flow.RcvNext && (flow.Established || !ack || segment.Ack == flow.SndNext))
                    Remove(flow);
                else if (flow.Established && Delta(segment.Sequence, flow.RcvNext) > 0 &&
                         Delta(segment.Sequence, flow.RcvNext) < flow.Window) SendAck(flow);
                return;
            }
            if (syn)
            {
                if (!flow.Established && !ack && !fin && segment.Sequence == flow.PeerIsn)
                    Send(flow, flow.SndUna, DnsTcpFlags.Syn | DnsTcpFlags.Ack);
                else if (flow.Established) SendAck(flow);
                return;
            }
            if (!ack) return;

            int startsAt = Delta(segment.Sequence, flow.RcvNext);
            int length = segment.PayloadLength + (fin ? 1 : 0);
            bool inOrder = startsAt == 0;
            bool overlaps = startsAt < 0 && startsAt >= -length && startsAt + length > 0;
            if (!flow.Established)
            {
                if (!inOrder || segment.Ack != flow.SndNext) return;
                flow.Established = true;
            }
            else if ((!inOrder && !overlaps) || Delta(segment.Ack, flow.SndNext) > 0)
            {
                SendAck(flow);
                return;
            }
            if (flow.SentFin && !flow.PeerFin && segment.PayloadLength > Math.Max(0, -startsAt))
            {
                Abort(flow);
                return;
            }

            int acknowledged = Delta(segment.Ack, flow.SndUna);
            if (acknowledged >= 0 && (Delta(segment.Sequence, flow.WlSeq) > 0 ||
                (segment.Sequence == flow.WlSeq && Delta(segment.Ack, flow.WlAck) >= 0)))
            {
                flow.PeerWindow = segment.Window;
                flow.WlSeq = segment.Sequence;
                flow.WlAck = segment.Ack;
            }
            if (acknowledged > 0)
            {
                flow.SndUna = segment.Ack;
                flow.Progress = now;
                flow.Retries = 0;
                ArmRetry(flow, now);
                if (flow.Reply != null)
                {
                    flow.ReplyOffset += acknowledged;
                    if (flow.ReplyOffset == flow.Reply.Length) flow.Reply = null;
                }
                if (flow.SentFin) flow.FinAcked = true;
            }

            if (length != 0)
                AcceptStreamBytes(flow, packet, segment, Math.Max(0, -startsAt), now);
            Drive(flow, now);
        }

        private void AcceptStreamBytes(Flow flow, byte[] packet, DnsTcpSegment segment, int duplicate, long now)
        {
            int window = flow.Window;
            int available = segment.PayloadLength - duplicate;
            int count = Math.Min(available, window);
            if (count > 0)
            {
                if (flow.Input == null) flow.Input = new byte[BufferSize];
                Buffer.BlockCopy(packet, segment.PayloadOffset + duplicate, flow.Input, flow.Buffered, count);
                flow.Buffered += count;
                flow.RcvNext = Advance(flow.RcvNext, count);
                flow.Progress = now;
            }
            if (!flow.PeerFin && segment.Has(DnsTcpFlags.Fin) && available >= 0 && available == count && count < window)
            {
                flow.PeerFin = true;
                flow.RcvNext = Advance(flow.RcvNext, 1);
                flow.Progress = now;
            }
            SendAck(flow);
        }

        private void Drive(Flow flow, long now)
        {
            if (flow.PeerFin && flow.FinAcked)
            {
                RememberClosed(flow, now);
                Remove(flow);
                return;
            }
            if (flow.Job != null || flow.SentFin || flow.SndUna != flow.SndNext) return;
            if (flow.Reply != null)
            {
                if (flow.PeerWindow > 0) SendReplyBytes(flow, Math.Min(flow.PeerWindow, flow.Mss), now);
                return;
            }
            if (flow.Buffered >= 2)
            {
                int size = (flow.Input[0] << 8) | flow.Input[1];
                if (size < 12) { Abort(flow); return; }
                if (flow.Buffered >= size + 2)
                {
                    if (_tcpJobs >= MaxFlows) return; // Keep backpressure until an old cancelled job unwinds.
                    var query = new byte[size];
                    Buffer.BlockCopy(flow.Input, 2, query, 0, size);
                    flow.Buffered -= size + 2;
                    if (flow.Buffered > 0) Buffer.BlockCopy(flow.Input, size + 2, flow.Input, 0, flow.Buffered);
                    else flow.Input = null;
                    flow.Job = new CancellationTokenSource();
                    _tcpJobs++;
                    var token = flow.Job.Token;
                    SendAck(flow); // Advertise the space freed by removing the completed frame.
                    _ = Task.Run(() => ForwardTcp(flow, query, token));
                    return;
                }
            }
            if (flow.PeerFin)
            {
                if (flow.Buffered != 0) Abort(flow); // FIN in the middle of a DNS frame.
                else if (flow.PeerWindow > 0) SendFin(flow, now);
            }
        }

        private async Task ForwardTcp(Flow flow, byte[] query, CancellationToken token)
        {
            byte[] answer = null;
            try { if (!token.IsCancellationRequested) answer = await _tcpQuery(query, token).ConfigureAwait(false); }
            catch (Exception) { } // A failed local exchange resets only this TCP session.
            lock (_gate)
            {
                _tcpJobs--;
                flow.Job.Dispose();
                flow.Job = null;
                if (_disposed || !_flows.TryGetValue(flow.Peer, out Flow current) || current != flow) return;
                if (answer == null || answer.Length < 12 || answer.Length > 65535 ||
                    answer[0] != query[0] || answer[1] != query[1])
                {
                    Abort(flow);
                    return;
                }
                flow.Reply = new byte[answer.Length + 2];
                flow.Reply[0] = (byte)(answer.Length >> 8);
                flow.Reply[1] = (byte)answer.Length;
                Buffer.BlockCopy(answer, 0, flow.Reply, 2, answer.Length);
                flow.ReplyOffset = 0;
                flow.Progress = _now();
                flow.Retries = 0;
                ArmRetry(flow, flow.Progress);
                Drive(flow, flow.Progress);
            }
        }

        public void Tick()
        {
            lock (_gate)
            {
                if (_disposed) return;
                long now = _now();
                // The dictionaries may change while progressing flows. Both snapshots are bounded.
                foreach (var pair in new List<KeyValuePair<DnsTunPeer, ClosedFlow>>(_closed))
                    if (pair.Value.Expires <= now) _closed.Remove(pair.Key);
                foreach (Flow flow in new List<Flow>(_flows.Values))
                {
                    if (now - flow.Progress >= IdleMs)
                    {
                        if (flow.Established && !flow.SentFin && !flow.PeerFin && flow.PeerWindow > 0 &&
                            flow.Job == null && flow.Reply == null && flow.Buffered == 0)
                        {
                            flow.Progress = now;
                            SendFin(flow, now);
                        }
                        else if (flow.Established) Abort(flow);
                        else Remove(flow);
                        continue;
                    }
                    if (now >= flow.RetryAt && (flow.SndUna != flow.SndNext || flow.Reply != null && flow.PeerWindow == 0))
                        Retransmit(flow, now);
                    else if (flow.Established) Drive(flow, now);
                }
            }
        }

        private void Retransmit(Flow flow, long now)
        {
            if (flow.Retries++ >= MaxRetries)
            {
                if (flow.Established) Abort(flow);
                else Remove(flow);
                return;
            }
            if (!flow.Established) Send(flow, flow.SndUna, DnsTcpFlags.Syn | DnsTcpFlags.Ack);
            else if (flow.SentFin) Send(flow, flow.SndUna, DnsTcpFlags.Fin | DnsTcpFlags.Ack);
            else if (flow.SndUna == flow.SndNext) SendReplyBytes(flow, 1, now); // Zero-window probe.
            else
            {
                int count = Math.Min(Delta(flow.SndNext, flow.SndUna), Math.Max(1, flow.PeerWindow));
                Send(flow, flow.SndUna, DnsTcpFlags.Ack | DnsTcpFlags.Psh, flow.Reply, flow.ReplyOffset, count);
            }
            ArmRetry(flow, now);
        }

        private static void ArmRetry(Flow flow, long now) => flow.RetryAt = now + (InitialRetryMs << Math.Min(3, flow.Retries));

        private void SendReplyBytes(Flow flow, int limit, long now)
        {
            int count = Math.Min(limit, flow.Reply.Length - flow.ReplyOffset);
            Send(flow, flow.SndNext, DnsTcpFlags.Ack | DnsTcpFlags.Psh, flow.Reply, flow.ReplyOffset, count);
            flow.SndNext = Advance(flow.SndNext, count);
            ArmRetry(flow, now);
        }

        private void SendFin(Flow flow, long now)
        {
            flow.SentFin = true;
            flow.Retries = 0;
            Send(flow, flow.SndNext, DnsTcpFlags.Fin | DnsTcpFlags.Ack);
            flow.SndNext = Advance(flow.SndNext, 1);
            ArmRetry(flow, now);
        }

        private void SendAck(Flow flow) => Send(flow, flow.SndNext, DnsTcpFlags.Ack);

        private void Send(Flow flow, uint seq, DnsTcpFlags flags, byte[] data = null, int offset = 0, int count = 0) =>
            _write(DnsVpnPacket.CreateTcp(flow.Peer, seq, flow.RcvNext, flags, flow.Window, data, offset, count));

        private void SendClosedReset(DnsTcpSegment segment)
        {
            bool ack = segment.Has(DnsTcpFlags.Ack);
            int length = segment.PayloadLength + (segment.Has(DnsTcpFlags.Syn) ? 1 : 0) + (segment.Has(DnsTcpFlags.Fin) ? 1 : 0);
            _write(DnsVpnPacket.CreateTcp(segment.Peer, ack ? segment.Ack : 0, ack ? 0 : Advance(segment.Sequence, length),
                ack ? DnsTcpFlags.Rst : DnsTcpFlags.Rst | DnsTcpFlags.Ack, 0));
        }

        private void RememberClosed(Flow flow, long now)
        {
            if (_closed.Count >= MaxClosed)
            {
                DnsTunPeer oldest = default(DnsTunPeer);
                long expiry = long.MaxValue;
                foreach (var pair in _closed)
                    if (pair.Value.Expires < expiry) { oldest = pair.Key; expiry = pair.Value.Expires; }
                _closed.Remove(oldest);
            }
            _closed[flow.Peer] = new ClosedFlow { Seq = flow.SndNext, Ack = flow.RcvNext, Expires = now + IdleMs };
        }

        private void Abort(Flow flow)
        {
            Send(flow, flow.SndNext, DnsTcpFlags.Rst | DnsTcpFlags.Ack);
            Remove(flow);
        }

        private void Remove(Flow flow)
        {
            _flows.Remove(flow.Peer);
            flow.Job?.Cancel();
            flow.Input = null;
            flow.Reply = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (Flow flow in new List<Flow>(_flows.Values)) Remove(flow);
                _closed.Clear();
                _udpCancellation.Cancel();
                if (_udpJobs == 0) _udpCancellation.Dispose();
                _random.Dispose();
            }
        }
    }
}

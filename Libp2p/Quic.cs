using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AhoChatA
{
    /* ===================== 流 ===================== */

    /// <summary>QUIC 双向流（客户端发起的 bidi 流用偶数 ID：0, 4, 8 …）。</summary>
    public sealed class QuicStream
    {
        public readonly uint Id;
        internal readonly QuicConnection Conn;
        private readonly object _gate = new object();
        private readonly List<byte> _buf = new List<byte>();
        private int _read;
        private long _outOffset;
        private bool _finSent, _finRecv, _reset, _resetSent;

        internal QuicStream(QuicConnection conn, uint id) { Conn = conn; Id = id; }

        /// <summary>写数据（自动切 STREAM 帧，超过一个包的尺寸时分帧）。</summary>
        public void Write(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            int off = 0;
            while (off < data.Length)
            {
                int n = Math.Min(data.Length - off, 1100);
                var chunk = new byte[n];
                Buffer.BlockCopy(data, off, chunk, 0, n);
                // 先把偏移"占住"再发：`_outOffset` 原来是在锁外读、锁内加，
                // 同一流上并发写时两段数据会拿到同一个偏移（对端只认第一段）。
                long at;
                lock (_gate) { at = _outOffset; _outOffset += n; }
                Conn.SendStreamFrame(Id, at, chunk, false);
                off += n;
            }
        }

        /// <summary>半关闭：发一个带 FIN 的空 STREAM 帧。</summary>
        public void CloseWrite()
        {
            long at;
            lock (_gate)
            {
                if (_finSent) return;
                _finSent = true;
                at = _outOffset;
            }
            Conn.SendStreamFrame(Id, at, new byte[0], true);
        }

        /// <summary>
        /// 中止流：发 RESET_STREAM，对端立刻把该流丢掉。
        /// 只发 FIN 的话对端会认为「写完了，只是没数据」，还会继续等/继续算，
        /// 协议协商失败时应当用 RESET 明确表达"这条流废了"。
        /// </summary>
        public void Reset(ulong appError = 0)
        {
            long finalSize;
            lock (_gate)
            {
                if (_resetSent) return;
                _resetSent = true;
                finalSize = _outOffset;
            }
            Conn.SendResetStream(Id, appError, finalSize);
        }

        internal void OnData(ulong offset, byte[] data, bool fin)
        {
            lock (_gate)
            {
                if (data != null && data.Length > 0)
                {
                    // 流内偏移：正常情况下 offset 就是「已收到的字节数」；
                    // 若对端重传（offset 小于已收长度）只取未收过的尾部。
                    long have = _buf.Count;
                    if ((long)offset < have)
                    {
                        long skip = have - (long)offset;
                        if (skip < data.Length)
                            for (long i = skip; i < data.Length; i++) _buf.Add(data[i]);
                    }
                    else
                    {
                        _buf.AddRange(data);
                    }
                }
                if (fin) _finRecv = true;
                Monitor.PulseAll(_gate);
            }
        }

        internal void OnReset()
        {
            lock (_gate) { _reset = true; Monitor.PulseAll(_gate); }
        }

        /// <summary>把多读出来的字节放回缓冲区头部（Stream 适配器用）。</summary>
        internal void PushBack(byte[] chunk, int alreadyConsumed)
        {
            lock (_gate)
            {
                // 缓冲区前 _read 个字节已经被上层读走，必须丢掉；
                // 否则把它们重新拼回头部会让下一次读到「旧数据」。
                var rest = new List<byte>();
                for (int i = alreadyConsumed; i < chunk.Length; i++) rest.Add(chunk[i]);
                for (int i = _read; i < _buf.Count; i++) rest.Add(_buf[i]);
                _buf.Clear();
                _buf.AddRange(rest);
                _read = 0;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>读当前可用字节；超时返回 null，对端已关闭返回空数组。</summary>
        public byte[] Read(int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            lock (_gate)
            {
                while (true)
                {
                    if (_buf.Count > _read)
                    {
                        var n = _buf.Count - _read;
                        var r = new byte[n];
                        _buf.CopyTo(_read, r, 0, n);
                        _read += n;
                        if (_read > 65536 && _read == _buf.Count) { _buf.Clear(); _read = 0; }
                        return r;
                    }
                    if (_reset) throw new System.IO.IOException(L10n.T("流被对端重置"));
                    if (_finRecv) return new byte[0];
                    var left = deadline - Environment.TickCount;
                    if (left <= 0) return null;
                    Monitor.Wait(_gate, left);
                }
            }
        }

        public bool HasData { get { lock (_gate) return _buf.Count > _read; } }
        public bool AtEnd { get { lock (_gate) return _finRecv && _buf.Count == _read; } }
    }

    /// <summary>
    /// 把 <see cref="QuicStream"/> 包成 System.IO.Stream，
    /// 让现有的 multistream-select / 消息编解码（都按 Stream 写）直接复用到 QUIC 流上。
    /// 超时（默认 30s）抛 IOException，对端 FIN 表现为 Read 返回 0。
    /// </summary>
    public sealed class QuicStreamAdapter : System.IO.Stream, IMuxStreamInfo
    {
        public readonly QuicStream Inner;

        uint IMuxStreamInfo.StreamId { get { return Inner.Id; } }
        void IMuxStreamInfo.HalfClose() { try { Inner.CloseWrite(); } catch { } }
        void IMuxStreamInfo.Abort() { try { Inner.Reset(); } catch { } }

        public QuicStreamAdapter(QuicStream inner) { Inner = inner; }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentException("bad range");
            if (count == 0) return 0;
            var wait = _readTimeout > 0 ? _readTimeout : 10000;
            var chunk = Inner.Read(wait);
            if (chunk == null) throw new System.IO.IOException("quic 流读超时（" + wait + "ms 无数据）");
            if (chunk.Length == 0) return 0;          // 对端 FIN
            int n = Math.Min(chunk.Length, count);
            Buffer.BlockCopy(chunk, 0, buffer, offset, n);
            if (n < chunk.Length) Inner.PushBack(chunk, n);   // 多读的存回去
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentException("bad range");
            if (count == 0) { Inner.CloseWrite(); return; }
            var chunk = new byte[count];
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
            Inner.Write(chunk);
        }

        public override void Flush() { /* QUIC 帧已即刻发出，无缓冲 */ }
        public override long Seek(long offset, System.IO.SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        private int _readTimeout = 10000;
        public override int ReadTimeout
        {
            get { return _readTimeout; }
            set { _readTimeout = value; }
        }
        public override int WriteTimeout
        {
            get { return -1; }
            set { /* QUIC 写不阻塞 */ }
        }

        /// <summary>半关闭写方向（发 FIN）。</summary>
        public void CloseWrite() { Inner.CloseWrite(); }

        protected override void Dispose(bool disposing) { /* 连接级关闭由 QuicConnection 负责 */ }
    }

    /* ===================== 连接 ===================== */

    /// <summary>
    /// 最小可用的 QUIC v1 客户端，专为 libp2p/qaul 公共节点设计。
    ///
    /// 覆盖：Initial 密钥 + 头保护、Retry、CRYPTO 重组、Handshake/1-RTT 级别切换、
    /// ACK、bidi 流、NEW/RETIRE_CONNECTION_ID、PING、连接关闭。
    /// 刻意精简：不做拥塞控制、不做丢包重传检测（靠对端重传 + 我们的 RTO 兜底）、
    /// 不做路径验证，因为我们的用法是「短连接、一问一答」。
    /// </summary>
    public sealed class QuicConnection : IDisposable
    {
        private const uint QUIC_VERSION_1 = 1;

        // RFC 9001 §5.2 的 v1 initial_salt
        private static readonly byte[] InitialSalt = Hex(
            "38762cf7f55934b34d179ae6a4c80cadccbb7f0a");

        private readonly UdpClient _udp;
        private readonly IPEndPoint _remote;
        private readonly Tls13Session _tls;
        private readonly Action<string> _log;

        private byte[] _localConnId;      // 我们的 SCID（对端发包的 DCID）
        private byte[] _remoteConnId;     // 我们发包的 DCID
        private byte[] _initialDcid;      // 派生 Initial 密钥用的 DCID
        private byte[] _retryToken;

        private TlsKeys _initialSend, _initialRecv;
        private TlsKeys _hsSend, _hsRecv;
        private TlsKeys _appSend, _appRecv;

        private uint _pnInitial, _pnHandshake, _pnApp;

        private enum Level { Initial, Handshake, App }
        private Level _sendLevel = Level.Initial;
        private bool _gotHandshakePacket;     // 收到 Handshake 包后不再发 Initial
        private bool _handshakeComplete;

        // CRYPTO 重组
        // CRYPTO 重组。注意：QUIC 的 CRYPTO 偏移是**按包号空间各自独立**的
        // （Initial / Handshake / 1-RTT 各有一条 crypto 流，偏移都从 0 开始），
        // 所以偏移与乱序缓存都必须按 level 分开记，不能共用一个计数器。
        private readonly System.Collections.Generic.SortedDictionary<long, byte[]>[] _cryptoChunks =
        {
            new System.Collections.Generic.SortedDictionary<long, byte[]>(),
            new System.Collections.Generic.SortedDictionary<long, byte[]>(),
            new System.Collections.Generic.SortedDictionary<long, byte[]>()
        };
        private readonly long[] _cryptoNext = new long[3];
        private readonly List<byte> _hsFrames = new List<byte>();

        // 流
        private readonly Dictionary<uint, QuicStream> _streams = new Dictionary<uint, QuicStream>();
        private uint _nextStreamId = 0;
        /// <summary>
        /// 流表锁。`_streams` 与 `_nextStreamId` 会被两类线程同时碰：
        /// 收包线程（对端拨入的流 → GetOrCreateStream）和业务线程（OpenStream）。
        /// 不加锁时多个线程会**拿到同一个流号**，后写入的覆盖先写入的，
        /// 于是对端的回包落到别人的 QuicStream 上 —— 表现就是「quic 流读超时」，
        /// 而且只在并发开流时偶发（单线程永远复现不出来）。
        /// </summary>
        private readonly object _streamLock = new object();

        /// <summary>握手完成后，首流前的最小等待（毫秒）。服务端装 1-RTT 读密钥需要一点时间。</summary>
        public int HandshakeSettleMs = 200;
        /// <summary>等待握手落定的上限，避免对端不按预期时把调用方拖死。</summary>
        public int HandshakeSettleMaxMs = 2000;
        private long _handshakeDoneMs;

        // 待确认的包号（按加密级别）
        private readonly SortedSet<uint> _ackInitial = new SortedSet<uint>();
        private readonly SortedSet<uint> _ackHandshake = new SortedSet<uint>();
        private readonly SortedSet<uint> _ackApp = new SortedSet<uint>();

        // 连接 ID 池（服务端给的）
        private readonly List<byte[]> _remoteConnIds = new List<byte[]>();
        private ulong _currentConnIdSeq;

        private Thread _rx;
        private volatile bool _closed;
        private readonly IPEndPoint _rxFrom;

        public string PeerIdBase58 { get { return _tls.PeerCert == null ? null : _tls.PeerCert.PeerIdBase58; } }
        public bool IsClosed { get { return _closed; } }

        /// <summary>协商出的 TLS 1.3 套件名（如 TLS_AES_128_GCM_SHA256）。</summary>
        public string CipherSuiteName { get { return _tls.CipherSuiteName; } }

        /// <summary>对端 QUIC 传输参数（EncryptedExtensions 里解出的）。</summary>
        public QuicTransportParams PeerTransportParams { get { return _tls.PeerTransport; } }

        /// <summary>握手期间的软错误（不中断连接的告警）。</summary>
        public string LastError { get; private set; }

        private QuicConnection(IPEndPoint remote, Libp2pCert myCert, Action<string> log)
        {
            _remote = remote;
            // 每条日志带上本地端口前缀：真机日志里同时可能有多条 QUIC 连接，
            // 没有这个前缀就无法把「订阅失败」和「哪条连接」对上。
            _log = m => { if (log != null) log(Tag + m); };
            _tls = new Tls13Session(myCert);
            _udp = new UdpClient(remote.AddressFamily);
            _udp.Connect(remote);
            _udp.Client.ReceiveTimeout = 200;
            _rxFrom = new IPEndPoint(IPAddress.Any, 0);
            // 本地端口是区分「同时存在多条 QUIC 连接」的唯一标识，
            // 排查并发建连问题时日志里没有它几乎无法对账。
            try { LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint).Port; } catch { LocalPort = 0; }
        }

        /// <summary>本连接的本地 UDP 端口（日志/诊断用）。</summary>
        public int LocalPort { get; private set; }

        /// <summary>所有日志都用这个前缀，便于在真机日志里按连接过滤。</summary>
        private string Tag { get { return "[q" + LocalPort + "] "; } }

        /* ===================== 公开 API ===================== */

        /// <summary>连接并完成 TLS/QUIC 握手。</summary>
        public static QuicConnection Connect(IPEndPoint remote, Libp2pCert myCert,
                                             int timeoutMs, Action<string> log)
        {
            var c = new QuicConnection(remote, myCert, log);
            c.Run(timeoutMs);
            return c;
        }

        /// <summary>打开一个新的客户端 bidi 流。</summary>
        public QuicStream OpenStream()
        {
            // 先等握手"落定"：服务端要收到我们的 client Finished、装上 1-RTT 读密钥
            // 之后才会处理我们开出来的流。刚握完手就抢着开流，数据可能被丢。
            WaitHandshakeSettle();

            // 客户端发起的双向流 id 必须是 0,4,8…（bit0=发起方 0，bit1=方向 0）。
            // 若 _nextStreamId 被对端发起的 id 带偏成奇数，对端会回
            // STREAM_STATE_ERROR(0x5)「operation on unopened stream」并直接关连接。
            //
            // 整个「取号 → 建对象 → 入表」必须在锁内完成：
            // 否则并发开流时多个线程会读到同一个 _nextStreamId，拿到同一个流号，
            // 后者把前者从 _streams 里覆盖掉，前一条流就永远收不到回包（读超时）。
            lock (_streamLock)
            {
                if ((_nextStreamId & 3) != 0) _nextStreamId = (_nextStreamId | 3) + 1;
                var st = new QuicStream(this, _nextStreamId);
                _nextStreamId += 4;
                _streams[st.Id] = st;
                return st;
            }
        }

        /// <summary>
        /// 等握手稳定：从握手完成起至少过 <see cref="HandshakeSettleMs"/> 毫秒，
        /// 最多等待 <see cref="HandshakeSettleMaxMs"/>。只对"第一次开流"有实际延迟。
        /// </summary>
        private void WaitHandshakeSettle()
        {
            long done = Interlocked.Read(ref _handshakeDoneMs);
            if (done == 0) return;                       // 尚未完成，上层会拿到明确错误
            int settle = HandshakeSettleMs;
            if (settle <= 0) return;
            long waited = 0;
            while (true)
            {
                long left = settle - (Environment.TickCount - done) - waited;
                if (left <= 0) return;
                if (waited >= HandshakeSettleMaxMs) return;
                int nap = (int)Math.Min(left, 50);
                Thread.Sleep(nap);
                waited += nap;
            }
        }

        public void Ping()
        {
            // PING 的帧类型是 0x01。0x07 是 NEW_TOKEN，后面还要跟 token 长度+内容，
            // 单发一个 0x07 会让对端解析越界（FRAME_ENCODING_ERROR "unexpected end"）。
            SendFrames(new List<byte[]> { Frame(0x01) });
        }

        /// <summary>驱动一次收发（供没有后台线程的场景用）。</summary>
        public void Pump(int waitMs)
        {
            PollOnce(waitMs);
            FlushAcks();
        }

        public void Close()
        {
            if (_closed) return;
            try { SendConnectionClose(0, "bye"); } catch { }
            _closed = true;
            try { _udp.Close(); } catch { }
        }

        public void Dispose() { Close(); }

        /* ===================== 握手主流程 ===================== */

        private void Run(int timeoutMs)
        {
            _localConnId = RandomBytes(8);
            _initialDcid = RandomBytes(8);
            _remoteConnId = (byte[])_initialDcid.Clone();
            DeriveInitialKeys(_initialDcid, out _initialSend, out _initialRecv);

            var tp = new QuicTransportParams();
            tp.InitialSourceConnectionId = _localConnId;
            var ch = _tls.BuildClientHello(tp.Encode());
            var frames = new List<byte[]> { CryptoFrame(0, ch) };
            SendInitial(frames, 1200);

            _rx = new Thread(RxLoop) { IsBackground = true };
            _rx.Start();

            var deadline = Environment.TickCount + timeoutMs;
            while (!_closed && !_handshakeComplete)
            {
                if (Environment.TickCount > deadline)
                    throw new TimeoutException(L10n.T("QUIC 握手超时"));
                _done.WaitOne(100);
                _done.Reset();
            }
            if (_closed) throw new System.IO.IOException(L10n.T("连接已关闭"));
            StartKeepAlive();
        }

        /// <summary>
        /// 保活间隔（毫秒）。公共节点的 idle timeout 只有 10 秒，
        /// 不发 PING 的话空闲几秒就被对端判死：连接一断、订阅流跟着断，
        /// 表现就是「订阅 floodsub 失败 / 消息时有时无」。
        /// </summary>
        public int KeepAliveMs = 5000;
        private Thread _keepAlive;

        private void StartKeepAlive()
        {
            if (KeepAliveMs <= 0 || _keepAlive != null) return;
            _keepAlive = new Thread(() =>
            {
                while (!_closed)
                {
                    Thread.Sleep(KeepAliveMs);
                    if (_closed) break;
                    try
                    {
                        // PING 是 ack-eliciting：既刷新了 NAT 绑定，也让对端把 idle 计时清零，
                        // 顺带还能从 ACK 里看出链路是否还活着。
                        SendFrames(new List<byte[]> { Frame(0x01) });
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "quic-keepalive" };
            _keepAlive.Start();
        }

        private readonly ManualResetEvent _done = new ManualResetEvent(false);

        private void RxLoop()
        {
            while (!_closed)
            {
                try { PollOnce(200); }
                catch (Exception ex) { LastError = ex.Message; _log(L10n.T("接收异常: ") + ex.Message); }
            }
        }

        private void PollOnce(int waitMs)
        {
            _udp.Client.ReceiveTimeout = Math.Max(1, waitMs);
            byte[] data;
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            try { data = _udp.Receive(ref from); }
            catch (SocketException) { FlushAcks(); RetransmitUnacked(); return; }
            catch (ObjectDisposedException) { return; }
            if (data == null || data.Length == 0) { FlushAcks(); return; }
            _log(L10n.T("收到数据报 ") + data.Length + L10n.T(" 字节 首字节=0x") + data[0].ToString("x2") + L10n.T(" 来自 ") + from);
            try { HandleDatagram(data); }
            catch (Exception ex) { _log(L10n.T("处理包异常: ") + ex.Message); }
            FlushAcks();
        }

        /* ===================== 收包 ===================== */

        private void HandleDatagram(byte[] data)
        {
            int p = 0;
            while (p < data.Length)
            {
                int before = p;
                try { p = ParsePacket(data, p); }
                catch (Exception ex) { _log(L10n.T("解析包失败: ") + ex.ToString()); return; }
                if (p <= before) return;
            }
        }

        /// <summary>解析一个包（可能带合并的后续包），返回下一个包的起始偏移。</summary>
        private int ParsePacket(byte[] data, int off)
        {
            int p = off;
            if (p >= data.Length) return data.Length;
            byte b0 = data[p];
            bool longHeader = (b0 & 0x80) != 0;

            if (!longHeader)
            {
                // 短头（1-RTT）：DCID 长度按我们 SCID 的长度取
                p += 1;
                int wantDcidLen = _localConnId.Length;
                if (p + wantDcidLen > data.Length) return data.Length;
                var dcid = Slice(data, p, wantDcidLen); p += wantDcidLen;
                if (!BytesEqual(dcid, _localConnId)) return data.Length;   // 不是给我们的
                int r = ParseShortHeaderPacket(data, off, p);
                return r;
            }

            // 长头
            int type = (b0 >> 4) & 3;
            p += 1;
            if (p + 4 > data.Length) return data.Length;
            uint version = BE32(data, p); p += 4;
            if (version == 0) return data.Length;              // 版本协商，忽略
            if (version != QUIC_VERSION_1) return data.Length;

            int dcidLen = data[p]; p += 1;
            if (p + dcidLen > data.Length) return data.Length;
            var dcid2 = Slice(data, p, dcidLen); p += dcidLen;
            int scidLen = data[p]; p += 1;
            if (p + scidLen > data.Length) return data.Length;
            var scid2 = Slice(data, p, scidLen); p += scidLen;

            if (type == 3)   // Retry
            {
                HandleRetry(scid2, Slice(data, p, data.Length - p));
                return data.Length;
            }

            byte[] token = null;
            if (type == 0)   // Initial：带 token
            {
                int tp = p;
                ulong tlen = QuicVarint.Read(data, ref tp);
                if (tp + (int)tlen > data.Length) return data.Length;
                token = Slice(data, tp, (int)tlen);
                p = tp + (int)tlen;
            }

            ulong length = QuicVarint.Read(data, ref p);
            int pnOffset = p;
            int pnLen = (b0 & 3) + 1;
            if (p + pnLen > data.Length) return data.Length;
            if ((long)p + (long)length > data.Length) return data.Length;

            int end = p + (int)length;
            return ParseLongHeaderPacket(data, off, type, dcid2, scid2, token, pnOffset, pnLen, end);
        }

        private void HandleRetry(byte[] serverScid, byte[] rest)
        {
            if (rest.Length < 17) return;                      // 16 字节 tag + 至少 1 字节 token
            _retryToken = Slice(rest, 0, rest.Length - 16);
            _remoteConnId = (byte[])serverScid.Clone();
            _log(L10n.T("收到 Retry，重发 Initial"));
            // 重发 ClientHello（Initial 密钥沿用原 DCID 派生出来的）
            var tp = new QuicTransportParams();
            tp.InitialSourceConnectionId = _localConnId;
            var frames = new List<byte[]> { CryptoFrame(0, _tls.ClientHelloAgain()) };
            SendInitialWithToken(frames, 1200);
        }

        private int ParseLongHeaderPacket(byte[] data, int pktStart, int type,
                                          byte[] dcid, byte[] scid, byte[] token, int pnOffset,
                                          int pnLen, int end)
        {
            TlsKeys recvKeys;
            Level ackLevel;
            if (type == 0)
            {
                if (!BytesEqual(dcid, _localConnId)) { _log(L10n.T("Initial 包 DCID 不匹配，丢弃")); return end; }
                recvKeys = _initialRecv;
                ackLevel = Level.Initial;
            }
            else if (type == 2)
            {
                recvKeys = _hsRecv;
                ackLevel = Level.Handshake;
                if (recvKeys == null) return end;              // 还没拿到握手密钥
            }
            else return end;                                   // 0-RTT 不用

            // 服务端可能在 Initial/Handshake 响应里换 SCID，之后发包要用新 DCID
            if (scid != null && scid.Length > 0 && !BytesEqual(scid, _remoteConnId))
            {
                _log(L10n.T("服务端 SCID 变更 ") + HexB(_remoteConnId) + " → " + HexB(scid));
                _remoteConnId = (byte[])scid.Clone();
            }

            // 头保护样本：pn_offset + 4
            int sampleOff = pnOffset + 4;
            if (sampleOff + 16 > end) return end;
            var sample = Slice(data, sampleOff, 16);
            bool aes = recvKeys.Aes;
            var mask = Tls13Session.HeaderProtectionMask(recvKeys.Hp, sample, aes);

            var pkt = (byte[])data.Clone();
            // 长头的第一个字节就是包头首字节。注意 pktStart：一个数据报里可以合并多个包，
            // 第 2 个起首字节不在 pkt[0]；pnLen 也必须在去头保护后从未保护字节重读
            // （低 2 位本来就被掩码盖着，提前读等于读随机数）。
            pkt[pktStart] ^= (byte)(mask[0] & 0x0f);
            pnLen = (pkt[pktStart] & 3) + 1;
            for (int i = 0; i < pnLen; i++) pkt[pnOffset + i] ^= mask[1 + i];

            ulong trunc = 0;
            for (int i = 0; i < pnLen; i++) trunc = (trunc << 8) | pkt[pnOffset + i];
            long largestLong = (type == 0) ? _largestInitPn : _largestHsPn;
            ulong pnFull = DecodePacketNumber(pnLen, trunc, largestLong);
            uint pn = (uint)pnFull;

            // AAD = 本包未保护头部（pktStart 起，到 pn 结束），不能把同一数据报里的前一个包算进去
            var aad = Slice(pkt, pktStart, pnOffset + pnLen - pktStart);
            var ct = Slice(pkt, pnOffset + pnLen, end - pnOffset - pnLen);
            byte[] payload;
            if (aes)
                payload = AesGcmCipher.GcmDecrypt(recvKeys.Key, recvKeys.NonceFor(pn), ct, aad);
            else
                payload = ChaCha20Poly1305.Open(recvKeys.Key, recvKeys.NonceFor(pn), ct, aad);
            if (payload == null) { _log("解密失败(type=" + type + " pn=" + pn + " pktStart=" + pktStart + ")"); return end; }
            if (type == 0) { if ((long)pnFull > _largestInitPn) _largestInitPn = (long)pnFull; }
            else { if ((long)pnFull > _largestHsPn) _largestHsPn = (long)pnFull; }
            _log(L10n.T("长头包解密成功 type=") + type + " pn=" + pn + " payload=" + payload.Length + L10n.T(" 字节"));

            AckPacket(ackLevel, pn);
            if (type == 2) { _gotHandshakePacket = true; MergeInitialAcks(); }

            int fp = 0;
            while (fp < payload.Length)
            {
                ulong ftype = QuicVarint.Read(payload, ref fp);
                if (ftype == 0x00) continue;                    // PADDING（Padding 以单字节 0 铺满，日志会淹掉）
                _log(L10n.T("  帧 type=0x") + ftype.ToString("x") + " @fp=" + (fp - 1));
                if (ftype == 0x07) continue;                    // PING
                if (ftype == 0x02 || ftype == 0x03)            // ACK
                {
                    SkipAckFrame(payload, ref fp);
                    continue;
                }
                if (ftype == 0x06)                             // CRYPTO
                {
                    ulong coff = QuicVarint.Read(payload, ref fp);
                    ulong clen = QuicVarint.Read(payload, ref fp);
                    _log("  CRYPTO offset=" + coff + " len=" + clen + " fp=" + fp + "/" + payload.Length);
                    if (fp + (int)clen > payload.Length) { _log(L10n.T("  CRYPTO 越界，弃包")); return end; }
                    var cdata = Slice(payload, fp, (int)clen);
                    fp += (int)clen;
                    AddCrypto(ackLevel, coff, cdata);
                    continue;
                }
                if ((ftype & 0x08) != 0 && (ftype & 0x04) == 0 && (ftype & ~0x0fUL) == 0x08UL)
                {
                    // STREAM（0x08 | flags）
                    ulong sid = QuicVarint.Read(payload, ref fp);
                    ulong soff = 0;
                    if ((ftype & 0x04) != 0) soff = QuicVarint.Read(payload, ref fp);
                    ulong slen;
                    if ((ftype & 0x02) != 0) slen = QuicVarint.Read(payload, ref fp);
                    else slen = (ulong)(payload.Length - fp);
                    if (fp + (int)slen > payload.Length) return end;
                    var sdata = Slice(payload, fp, (int)slen);
                    fp += (int)slen;
                    bool fin = (ftype & 0x01) != 0;
                    GetOrCreateStream((uint)sid).OnData(soff, sdata, fin);
                    continue;
                }
                if (ftype == 0x18)                             // NEW_CONNECTION_ID
                {
                    ulong seq = QuicVarint.Read(payload, ref fp);
                    ulong rpt = QuicVarint.Read(payload, ref fp);
                    ulong clen = QuicVarint.Read(payload, ref fp);
                    if (fp + (int)clen + 16 > payload.Length) return end;
                    var cid = Slice(payload, fp, (int)clen); fp += (int)clen;
                    fp += 16;                                  // stateless reset token
                    if (seq > _currentConnIdSeq && cid.Length > 0 && cid.Length <= 20)
                    {
                        _currentConnIdSeq = seq;
                        _remoteConnId = cid;
                    }
                    continue;
                }
                if (ftype == 0x19)                             // RETIRE_CONNECTION_ID
                {
                    QuicVarint.Read(payload, ref fp);
                    continue;
                }
                if (ftype == 0x1c || ftype == 0x1d)            // CONNECTION_CLOSE
                {
                    ulong errCode = QuicVarint.Read(payload, ref fp);
                    ulong frameType = (ftype == 0x1d) ? QuicVarint.Read(payload, ref fp) : 0;
                    ulong rlen = QuicVarint.Read(payload, ref fp);
                    string reason = "";
                    if (fp + (int)rlen <= payload.Length && rlen > 0)
                        reason = Encoding.ASCII.GetString(payload, fp, (int)rlen);
                    _log(L10n.T("对端关闭连接：error=0x") + errCode.ToString("x") +
                         (ftype == 0x1d ? " frame=0x" + frameType.ToString("x") : "") +
                         (reason.Length > 0 ? " reason=\"" + reason + "\"" : ""));
                    LastError = L10n.T("对端关闭连接 error=0x") + errCode.ToString("x") + " " + reason;
                    _closed = true;
                    return end;
                }
                // 不认识的帧：无法安全跳过（长度未知），只能放弃这个包
                _log(L10n.T("未知帧类型 0x") + ftype.ToString("x"));
                return end;
            }
            return end;
        }

        private int ParseShortHeaderPacket(byte[] data, int pktStart, int afterDcid)
        {
            if (_appRecv == null)
            {
                var dcidPeek = Slice(data, 1, Math.Min(_localConnId.Length, data.Length - 1));
                _log(L10n.T("短头包但应用密钥未就绪，丢弃（首字节=0x") + data[pktStart].ToString("x2") +
                     " DCID=" + HexB(dcidPeek) + L10n.T(" 我方=") + HexB(_localConnId) + "）");
                return data.Length;
            }
            int pnOffset = afterDcid;
            int sampleOff = pnOffset + 4;
            if (sampleOff + 16 > data.Length) return data.Length;
            var sample = Slice(data, sampleOff, 16);
            bool aes = _appRecv.Aes;
            var mask = Tls13Session.HeaderProtectionMask(_appRecv.Hp, sample, aes);

            var pkt = (byte[])data.Clone();
            // 同长头：首字节/AAD 都要相对 pktStart（数据报可以合并多个短头包）
            pkt[pktStart] ^= (byte)(mask[0] & 0x1f);
            int pnLen = (pkt[pktStart] & 3) + 1;
            for (int i = 0; i < pnLen; i++) pkt[pnOffset + i] ^= mask[1 + i];
            ulong trunc2 = 0;
            for (int i = 0; i < pnLen; i++) trunc2 = (trunc2 << 8) | pkt[pnOffset + i];
            ulong pnFull2 = DecodePacketNumber(pnLen, trunc2, _largestAppPn);
            uint pn = (uint)pnFull2;

            var aad = Slice(pkt, pktStart, pnOffset + pnLen - pktStart);
            var ct = Slice(pkt, pnOffset + pnLen, data.Length - pnOffset - pnLen);
            byte[] payload;
            if (aes) payload = AesGcmCipher.GcmDecrypt(_appRecv.Key, _appRecv.NonceFor(pn), ct, aad);
            else payload = ChaCha20Poly1305.Open(_appRecv.Key, _appRecv.NonceFor(pn), ct, aad);
            if (payload == null) { _log("1-RTT 解密失败 pn=" + pn + "（截断=" + trunc2 + " pnLen=" + pnLen + "）"); return data.Length; }
            if ((long)pnFull2 > _largestAppPn) _largestAppPn = (long)pnFull2;

            AckPacket(Level.App, pn);

            int fp = 0;
            while (fp < payload.Length)
            {
                ulong ftype = QuicVarint.Read(payload, ref fp);
                if (Environment.GetEnvironmentVariable("QUICDBG") != null && ftype != 0x00)
                    _log(L10n.T("  1RTT 帧 0x") + ftype.ToString("x") + " @fp=" + (fp - 1));
                if (ftype == 0x00 || ftype == 0x01 || ftype == 0x1e) continue;   // PADDING / PING(0x01) / HANDSHAKE_DONE
                if (ftype == 0x02 || ftype == 0x03) { HandleAckRanges(payload, ref fp); continue; }
                if (ftype == 0x04)                   // RESET_STREAM: id, error, final size
                {
                    ulong rid = QuicVarint.Read(payload, ref fp);
                    ulong rcode = QuicVarint.Read(payload, ref fp);
                    ulong rfs = QuicVarint.Read(payload, ref fp);
                    _log(L10n.T("  对端 RESET_STREAM sid=") + rid + " code=0x" + rcode.ToString("x") + " finalSize=" + rfs);
                    continue;
                }
                if (ftype == 0x05)                   // STOP_SENDING: id, error
                {
                    QuicVarint.Read(payload, ref fp);
                    QuicVarint.Read(payload, ref fp);
                    continue;
                }
                if (ftype == 0x06)                   // CRYPTO（握手后一般不再出现）
                {
                    QuicVarint.Read(payload, ref fp);
                    ulong cl = QuicVarint.Read(payload, ref fp);
                    if (fp + (int)cl > payload.Length) return data.Length;
                    fp += (int)cl;
                    continue;
                }
                if (ftype == 0x07)                   // NEW_TOKEN: len + data
                {
                    ulong tl = QuicVarint.Read(payload, ref fp);
                    if (fp + (int)tl > payload.Length) return data.Length;
                    fp += (int)tl;
                    continue;
                }
                // STREAM：type 0x08..0x0f（低 3 位是 FIN/OFF/LEN 标志）
                if (ftype >= 0x08 && ftype <= 0x0f)
                {
                    ulong sid = QuicVarint.Read(payload, ref fp);
                    ulong soff = 0;
                    if ((ftype & 0x04) != 0) soff = QuicVarint.Read(payload, ref fp);
                    ulong slen;
                    if ((ftype & 0x02) != 0) slen = QuicVarint.Read(payload, ref fp);
                    else slen = (ulong)(payload.Length - fp);
                    if (fp + (int)slen > payload.Length) return data.Length;
                    var sdata = Slice(payload, fp, (int)slen);
                    fp += (int)slen;
                    _log(L10n.T("  收到 STREAM sid=") + sid + " off=" + soff + " len=" + slen +
                         " fin=" + ((ftype & 0x01) != 0) +
                         " data=\"" + Encoding.ASCII.GetString(sdata, 0, Math.Min(sdata.Length, 48)) + "\"");
                    GetOrCreateStream((uint)sid).OnData(soff, sdata, (ftype & 0x01) != 0);
                    continue;
                }
                // 这里开始是一批「不改变我们行为」的控制帧，正确地跳过即可，
                // 关键是不要因为它们出现就整包丢弃（后面的 STREAM 数据就全丢了）。
                if (ftype == 0x10 || ftype == 0x12 || ftype == 0x13 ||
                    ftype == 0x14 || ftype == 0x16 || ftype == 0x17 || ftype == 0x19)
                { QuicVarint.Read(payload, ref fp); continue; }
                if (ftype == 0x11 || ftype == 0x15 || ftype == 0x1a || ftype == 0x1b)
                {
                    if (ftype == 0x11 || ftype == 0x15) QuicVarint.Read(payload, ref fp);  // stream id
                    fp += 8;                                     // 其余字段（varint 或 8 字节）
                    continue;
                }
                if (ftype == 0x18)
                {
                    // NEW_CONNECTION_ID：seq(varint) + retire_prior_to(varint) + length(1 字节) + cid + 16 字节 reset token
                    // 注意 Length 是**单字节**而非 varint。这些 CID 是备用/迁移用的，
                    // 不能直接替换当前 DCID（否则会把收发包路径搞乱），只记下来备查。
                    ulong seq = QuicVarint.Read(payload, ref fp);
                    QuicVarint.Read(payload, ref fp);            // retire_prior_to
                    if (fp >= payload.Length) return data.Length;
                    int clen = payload[fp]; fp += 1;
                    if (fp + clen + 16 > payload.Length) return data.Length;
                    var cid = Slice(payload, fp, clen); fp += clen;
                    fp += 16;                                    // stateless reset token
                    _log(L10n.T("  对端提供备用 CID seq=") + seq + " cid=" + HexB(cid) + L10n.T("（保留当前 DCID）"));
                    continue;
                }
                if (ftype == 0x1c || ftype == 0x1d)
                {
                    ulong code = QuicVarint.Read(payload, ref fp);
                    string reason = null;
                    if (ftype == 0x1c)
                    {
                        QuicVarint.Read(payload, ref fp);        // frame type
                        ulong rl = QuicVarint.Read(payload, ref fp);
                        if (rl > 0 && fp + (int)rl <= payload.Length)
                            reason = Encoding.UTF8.GetString(payload, fp, (int)rl);
                    }
                    else
                    {
                        ulong rl = QuicVarint.Read(payload, ref fp);
                        if (rl > 0 && fp + (int)rl <= payload.Length)
                            reason = Encoding.UTF8.GetString(payload, fp, (int)rl);
                    }
                    LastError = "对端关闭连接 error=0x" + code.ToString("x") + (reason == null ? "" : " reason=\"" + reason + "\"");
                    _log(LastError);
                    _closed = true;
                    return data.Length;
                }
                _log(L10n.T("1-RTT 未知帧 0x") + ftype.ToString("x") + " @fp=" + (fp - 1));
                return data.Length;
            }
            return data.Length;
        }

        private void SkipAckFrame(byte[] buf, ref int p)
        {
            ulong largest = QuicVarint.Read(buf, ref p);
            QuicVarint.Read(buf, ref p);                 // delay
            ulong count = QuicVarint.Read(buf, ref p);   // range count
            ulong first = QuicVarint.Read(buf, ref p);   // first range
            for (ulong i = 0; i < count; i++)
            {
                QuicVarint.Read(buf, ref p);             // gap
                QuicVarint.Read(buf, ref p);             // length
            }
            if (Environment.GetEnvironmentVariable("QUICDBG") != null)
                _log(L10n.T("  对端 ACK largest=") + largest + " first_range=" + first + " ranges=" + count);
        }

        /* ===================== CRYPTO 重组 ===================== */

        private void AddCrypto(Level lvl, ulong offset, byte[] data)
        {
            int L = (int)lvl;
            if (L < 0 || L > 2) L = 0;
            var chunks = _cryptoChunks[L];
            long next = _cryptoNext[L];
            if (offset > (ulong)next)
            {
                if (!chunks.ContainsKey((long)offset))
                    chunks[(long)offset] = data;
                else
                    _log(L10n.T("  CRYPTO 乱序块重复，忽略（level=") + lvl + " off=" + offset + "）");
                return;
            }
            if (offset < (ulong)next)
            {
                // 重叠：只取未收过的部分
                long skip = next - (long)offset;
                if (skip >= data.Length) return;
                var rest = Slice(data, (int)skip, data.Length - (int)skip);
                AppendCrypto(L, rest);
                return;
            }
            AppendCrypto(L, data);
        }

        private void AppendCrypto(int L, byte[] data)
        {
            // data 从 _cryptoNext[L] 开始：先喂给 TLS，再把缓存里紧接着的连续块一路吃下去
            var pending = new List<byte[]> { data };
            while (pending.Count > 0)
            {
                var chunk = pending[0];
                pending.RemoveAt(0);
                _cryptoNext[L] += chunk.Length;
                FeedHandshakeBytes(chunk);
                var chunks = _cryptoChunks[L];
                while (true)
                {
                    byte[] next = null;
                    foreach (var kv in chunks)
                    {
                        if (kv.Key == _cryptoNext[L]) { next = kv.Value; chunks.Remove(kv.Key); break; }
                        if (kv.Key > _cryptoNext[L]) break;
                    }
                    if (next == null) break;
                    pending.Add(next);
                }
            }
        }

        private void FeedHandshakeBytes(byte[] data)
        {
            _hsFrames.AddRange(data);
            _log(L10n.T("  CRYPTO 累计 ") + _hsFrames.Count + L10n.T(" 字节（本次 +") + data.Length + "）");
            while (_hsFrames.Count >= 4)
            {
                // 握手消息头：1 字节类型 + 3 字节大端长度（[0] 是类型，不是长度高位！）
                int len = (_hsFrames[1] << 16) | (_hsFrames[2] << 8) | _hsFrames[3];
                if (_hsFrames.Count < 4 + len) { _log(L10n.T("  等更多字节：需要 ") + (4 + len) + L10n.T(" 现有 ") + _hsFrames.Count); break; }
                var msg = new byte[4 + len];
                _hsFrames.CopyTo(0, msg, 0, 4 + len);
                _hsFrames.RemoveRange(0, 4 + len);
                _log(L10n.T("收到握手消息 type=") + msg[0] + L10n.T(" 长=") + len);
                _tls.FeedHandshake(msg);
                AfterTlsMessage();
            }
        }

        /// <summary>每消费一条握手消息后推进状态机。</summary>
        private void AfterTlsMessage()
        {
            if (_tls.ServerHandshake != null && _hsSend == null)
            {
                _hsSend = _tls.ClientHandshake;
                _hsRecv = _tls.ServerHandshake;
                _sendLevel = Level.Handshake;
                _log(L10n.T("握手密钥就绪（套件 ") + _tls.CipherSuiteName + "）");
                // quinn 客户端此时立刻用一个 Handshake 包 ACK 掉服务端的 Initial（含 SH），
                // 否则服务端在放大限制下不肯发后续 Handshake 飞行，只会反复重传 SH。
                MergeInitialAcks();
                try { FlushAcks(); } catch (Exception ex) { _log(L10n.T("发 ACK 失败: ") + ex.Message); }
            }
            if (_tls.ServerApp != null && !_handshakeComplete)
            {
                _appSend = _tls.ClientApp;
                _appRecv = _tls.ServerApp;
                _handshakeComplete = true;
                // 记下"握手完成"的时刻：OpenStream 会用它在首流前让出一点时间，
                // 等服务端真正装上 1-RTT 读密钥（client Finished 到达 + 处理完）。
                Interlocked.Exchange(ref _handshakeDoneMs, Environment.TickCount);
                // 发客户端自己的 Certificate + CertificateVerify + Finished。
                // 偏移必须是 0：这几条走的是 **Handshake 包号空间**，
                // 该空间的 crypto 流从头开始，与 Initial 空间里的 ClientHello 无关。
                // （用 ClientCryptoOffset 会把偏移写成 CH 长度，服务端会一直等前面的字节。）
                if (Environment.GetEnvironmentVariable("NOCERT") == "1")
                {
                    _tls.SkipClientCertForTest = true;
                    _log(L10n.T("  排障：跳过客户端证书（只发 Finished）"));
                }
                var flight = _tls.BuildClientFlight();
                var frames = new List<byte[]> { CryptoFrame(0, ConcatAll(flight)) };
                SendFrames(frames);
                _sendLevel = Level.App;
                _log(L10n.T("服务端 Finished 已验过，TLS 握手完成，应用密钥启用"));
                _done.Set();
            }
        }

        /* ===================== 发包 ===================== */

        private byte[] CryptoFrame(ulong offset, byte[] data)
        {
            var ms = new System.IO.MemoryStream();
            QuicVarint.Write(ms, 0x06);
            QuicVarint.Write(ms, offset);
            QuicVarint.Write(ms, (ulong)data.Length);
            ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] Frame(ulong type) { return QuicVarint.Encode(type); }

        private void SendFrames(List<byte[]> frames)
        {
            if (_closed) return;
            if (Environment.GetEnvironmentVariable("QUICDBG") == "1")
            {
                var all = ConcatAll(frames);
                _log(L10n.T("发包 level=") + _sendLevel + " body=" + all.Length + "B hex=" +
                     BitConverter.ToString(all, 0, Math.Min(32, all.Length)).Replace("-", ""));
            }
            switch (_sendLevel)
            {
                case Level.Initial: SendInitial(frames, 0); break;
                case Level.Handshake: SendHandshake(frames); break;
                default: SendOneRtt(frames); break;
            }
        }

        private void SendInitial(List<byte[]> frames, int minPayload)
        {
            SendInitialWithToken(frames, minPayload);
        }

        private void SendInitialWithToken(List<byte[]> frames, int minPayload)
        {
            var body = ConcatAll(frames);
            if (minPayload > body.Length)
            {
                var padded = new byte[minPayload];
                Buffer.BlockCopy(body, 0, padded, 0, body.Length);
                body = padded;
            }
            uint pn = _pnInitial++;
            _initialSend.Seq = pn;
            var packet = BuildLongHeader(0, pn, _initialSend, body, _retryToken);
            _udp.Send(packet, packet.Length);
        }

        private void SendHandshake(List<byte[]> frames)
        {
            lock (_sendLock)
            {
                var body = ConcatAll(frames);
                uint pn = _pnHandshake++;
                _hsSend.Seq = pn;
                var packet = BuildLongHeader(2, pn, _hsSend, body, null);
                _udp.Send(packet, packet.Length);
            }
        }

        private void SendOneRtt(List<byte[]> frames)
        {
            var body = ConcatAll(frames);
            // 只有「ack-eliciting」的包才需要重传：
            // 纯 ACK 包对端不会回 ACK（RFC 9000 §13.2.1），放进重传表会无限重发。
            bool elicit = !(frames.Count == 1 && frames[0].Length > 0 &&
                            (frames[0][0] == 0x02 || frames[0][0] == 0x03));
            SendOneRttBody(body, elicit, 0);
        }

        /// <summary>用下一个包号把一段明文帧封成 1-RTT 包发出去（重传也走这里，拿到新包号）。</summary>
        private void SendOneRttBody(byte[] body, bool elicit, int tries)
        {
            lock (_sendLock)
            {
            uint pn = _pnApp++;
            _appSend.Seq = pn;
            var packet = BuildShortHeader(pn, _appSend, body);
            if (Environment.GetEnvironmentVariable("QUICDBG") == "4")
            {
                try
                {
                    var dir4 = System.IO.Path.GetTempPath();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir4, "q1rtt_" + pn + ".bin"), packet);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir4, "q1rtt_body_" + pn + ".bin"), body);
                    var ca4 = _tls.ClientAppSecret;
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir4, "q1rtt_key.txt"),
                        BitConverter.ToString(ca4).Replace("-", "").ToLowerInvariant());
                }
                catch { }
            }
            // 真机没有环境变量可用，前 12 个 1-RTT 包一律落盘（缓存目录，量很小），
            // 出问题时用 adb 取回来就能逐包独立校验——这是唯一能区分
            // 「包本身有问题」和「包没问题、是网络/对端丢的」的办法。
            if (pn < 12)
            {
                try
                {
                    var dirA = System.IO.Path.GetTempPath();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dirA, "q1rtt_" + pn + ".bin"), packet);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dirA, "q1rtt_body_" + pn + ".bin"), body);
                    var caA = _tls.ClientAppSecret;
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "q1rtt_key.txt"),
                        (caA == null ? "none" : BitConverter.ToString(caA).Replace("-", "").ToLowerInvariant()) +
                        "\nkey=" + BitConverter.ToString(_appSend.Key).Replace("-", "").ToLowerInvariant() +
                        "\niv=" + BitConverter.ToString(_appSend.Iv).Replace("-", "").ToLowerInvariant() +
                        "\nhp=" + BitConverter.ToString(_appSend.Hp).Replace("-", "").ToLowerInvariant() +
                        "\nseq=" + _appSend.Seq + " aes=" + _appSend.Aes);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "q1rtt_dir.txt"), dirA);
                }
                catch (Exception ex) { _log(L10n.T("落盘 1-RTT 包失败: ") + ex.Message); }
            }
            try { _udp.Send(packet, packet.Length); }
            catch (Exception ex) { _log(L10n.T("发送 1-RTT 包失败 pn=") + pn + ": " + ex.Message); throw; }
            if (Environment.GetEnvironmentVariable("QUICDBG") == "1" && !_dumped1Rtt)
            {
                _dumped1Rtt = true;
                try
                {
                    var dir = System.IO.Path.GetTempPath();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "quic_first1rtt.bin"), packet);
                    var ca = _tls.ClientAppSecret;
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "quic_capp.txt"),
                        ca == null ? "none" : BitConverter.ToString(ca).Replace("-", "").ToLowerInvariant() +
                        "\nkey=" + BitConverter.ToString(_appSend.Key).Replace("-", "").ToLowerInvariant() +
                        "\niv=" + BitConverter.ToString(_appSend.Iv).Replace("-", "").ToLowerInvariant() +
                        "\nhp=" + BitConverter.ToString(_appSend.Hp).Replace("-", "").ToLowerInvariant() +
                        "\nseq=" + _appSend.Seq + " aes=" + _appSend.Aes);
                    _log(L10n.T("  已导出首包 1-RTT ") + packet.Length + L10n.T("B 与 c_ap 供独立校验"));
                }
                catch { }
            }
            if (_handshakeComplete && elicit)
                _unackedApp[pn] = new SentPacket { Body = body, FirstSentMs = Environment.TickCount, Tries = tries };
            }
        }

        // 每个包号空间已收到的最大 PN（用于把截断的 PN 还原成完整值）
        private long _largestInitPn = -1, _largestHsPn = -1, _largestAppPn = -1;

        /// <summary>
        /// 按 RFC 9000 §17.1 / A.3 把截断的包号还原为完整包号。
        /// 例如对端用 1 字节编码 PN=0x02，而当前最大已收是 300，那么真实 PN 是 258 而不是 2；
        /// 直接用截断值算 nonce 会导致 AEAD 必然失败（现象就是「1-RTT 解密失败 pn=…」）。
        /// </summary>
        private static ulong DecodePacketNumber(int pnLen, ulong truncated, long largest)
        {
            ulong win = 1UL << (pnLen * 8);
            ulong half = win >> 1;
            ulong expected = largest < 0 ? 0UL : (ulong)largest + 1UL;
            ulong candidate = (expected & ~(win - 1)) | truncated;
            if (candidate + half <= expected && candidate + win < ulong.MaxValue - win)
                candidate += win;
            else if (candidate > expected + half && candidate >= win)
                candidate -= win;
            return candidate;
        }

        private bool _dumped1Rtt;

        /// <summary>
        /// 发包锁：包号分配、AEAD 加密封包、写 socket 必须串行。
        /// 否则「重传线程」和「业务线程」可能抢到同一个包号（`_pnApp++` 不是原子的），
        /// 对端会把第二份当成重复包丢掉——恰好吃掉带业务数据的那一份。
        /// </summary>
        private readonly object _sendLock = new object();

        /// <summary>已发出但尚未被对端 ACK 的 1-RTT 包（包号 → 原始字节 + 首次发送时刻）。</summary>
        private readonly System.Collections.Generic.Dictionary<uint, SentPacket> _unackedApp =
            new System.Collections.Generic.Dictionary<uint, SentPacket>();

        private struct SentPacket
        {
            /// <summary>该包承载的明文帧（重传时要用新包号重新封装，不能整包复制）。</summary>
            public byte[] Body;
            public long FirstSentMs;
            public int Tries;
        }

        /// <summary>
        /// 最小重传：超时未确认就原样重发。
        /// 严格说 QUIC 要求用新的包号重发「帧」，但同一份字节重发（同包号）对端只会当成重复包，
        /// 行为等价且实现简单——对 libp2p 这种小消息、重传次数极少（1~2 次）的场景完全够用。
        /// </summary>
        private void RetransmitUnacked()
        {
            if (_closed) return;
            lock (_sendLock)
            {
                if (_unackedApp.Count == 0) return;
                long now = Environment.TickCount;
                var resend = new List<uint>();
                foreach (var kv in _unackedApp)
                {
                    var sp = kv.Value;
                    // 到公共节点的 RTT 常在 250ms 以上，窗口取 700ms，最多重试 5 次，
                    // 否则会在 ACK 到达之前误判丢包，把链路压满。
                    if (now - sp.FirstSentMs >= 700 && sp.Tries < 5) resend.Add(kv.Key);
                }
                foreach (var pn in resend)
                {
                    var sp = _unackedApp[pn];
                    _unackedApp.Remove(pn);
                    if (sp.Body == null) continue;
                    // 关键：必须用**新的包号**重新封包再发。
                    // 原样重发旧包号会被对端当成过期包丢掉（它的包号窗口早已前移），
                    // 这也是之前「一直重传、对端始终不回 ACK」的根因。
                    SendOneRttBody(sp.Body, true, sp.Tries + 1);
                    _log(L10n.T("  重传 1-RTT（旧 pn=") + pn + L10n.T("，第 ") + (sp.Tries + 1) + L10n.T(" 次，已换新包号）"));
                }
            }
        }

        /// <summary>处理对端 ACK：把已确认的包号从重传表里删掉（含 range 展开）。</summary>
        private void HandleAckRanges(byte[] buf, ref int p)
        {
            ulong largest = QuicVarint.Read(buf, ref p);
            QuicVarint.Read(buf, ref p);                    // ack_delay
            ulong count = QuicVarint.Read(buf, ref p);
            ulong first = QuicVarint.Read(buf, ref p);

            ulong lo = largest >= first ? largest - first : 0;
            _log("  1RTT ACK largest=" + largest + " first_range=" + first + " ranges=" + count +
                 L10n.T("（我方未确认包数 ") + _unackedApp.Count + "）");
            RemoveAcked(lo, largest);
            ulong cur = lo;
            for (ulong i = 0; i < count; i++)
            {
                ulong gap = QuicVarint.Read(buf, ref p);
                ulong len = QuicVarint.Read(buf, ref p);
                if (cur < gap + 2) break;                   // 越界，放弃后续
                ulong hi = cur - gap - 2;
                ulong l2 = hi >= len ? hi - len : 0;
                RemoveAcked(l2, hi);
                cur = l2;
            }
        }

        private void RemoveAcked(ulong lo, ulong hi)
        {
            // 发送线程会往 _unackedApp 里写，枚举必须和它互斥，否则会抛「集合已修改」。
            lock (_sendLock)
            {
                if (_unackedApp.Count == 0) return;
                var del = new List<uint>();
                foreach (var kv in _unackedApp)
                    if (kv.Key >= lo && kv.Key <= hi) del.Add(kv.Key);
                foreach (var k in del) _unackedApp.Remove(k);
            }
        }

        private byte[] BuildLongHeader(int type, uint pn, TlsKeys keys, byte[] body, byte[] token)
        {
            var head = new System.IO.MemoryStream();
            int pnLen = 4;
            head.WriteByte((byte)(0x80 | 0x40 | (type << 4) | (pnLen - 1)));
            head.WriteByte((byte)(QUIC_VERSION_1 >> 24));
            head.WriteByte((byte)(QUIC_VERSION_1 >> 16));
            head.WriteByte((byte)(QUIC_VERSION_1 >> 8));
            head.WriteByte((byte)QUIC_VERSION_1);
            head.WriteByte((byte)_remoteConnId.Length);
            head.Write(_remoteConnId, 0, _remoteConnId.Length);
            head.WriteByte((byte)_localConnId.Length);
            head.Write(_localConnId, 0, _localConnId.Length);
            // Token 字段只存在于 Initial 包（RFC 9000 §17.2.2）。
            // Handshake / 0-RTT 都没有这个字段——多写一个 0 会让对端从错位处解析 Length，
            // 整个包直接被丢弃（现象：对端一直重传自己的飞行，却收不到我们的）。
            if (type == 0)
            {
                if (token != null)
                {
                    QuicVarint.Write(head, (ulong)token.Length);
                    head.Write(token, 0, token.Length);
                }
                else
                {
                    QuicVarint.Write(head, 0);
                }
            }
            QuicVarint.Write(head, (ulong)(pnLen + body.Length + 16));

            var headBytes = head.ToArray();
            int pnOffset = headBytes.Length;
            var aad = new byte[pnOffset + pnLen];
            Buffer.BlockCopy(headBytes, 0, aad, 0, pnOffset);
            aad[pnOffset + 0] = (byte)(pn >> 24);
            aad[pnOffset + 1] = (byte)(pn >> 16);
            aad[pnOffset + 2] = (byte)(pn >> 8);
            aad[pnOffset + 3] = (byte)pn;

            var sealedBody = AesOrChaCha(keys, body, aad, true);
            var packet = new byte[pnOffset + pnLen + sealedBody.Length];
            Buffer.BlockCopy(aad, 0, packet, 0, aad.Length);
            Buffer.BlockCopy(sealedBody, 0, packet, pnOffset + pnLen, sealedBody.Length);

            // 头保护
            int sampleOff = pnOffset + 4;
            if (sampleOff + 16 <= packet.Length)
            {
                var sample = Slice(packet, sampleOff, 16);
                var mask = Tls13Session.HeaderProtectionMask(keys.Hp, sample, keys.Aes);
                packet[0] ^= (byte)(mask[0] & 0x0f);
                for (int i = 0; i < pnLen; i++) packet[pnOffset + i] ^= mask[1 + i];
            }
            return packet;
        }

        private byte[] BuildShortHeader(uint pn, TlsKeys keys, byte[] body)
        {
            int pnLen = 4;
            int dcidLen = _remoteConnId.Length;
            int pnOffset = 1 + dcidLen;                  // 短头没有 SCID/Token，PN 紧跟 DCID
            var aad = new byte[pnOffset + pnLen];
            aad[0] = (byte)(0x40 | (pnLen - 1));
            Buffer.BlockCopy(_remoteConnId, 0, aad, 1, dcidLen);
            aad[pnOffset + 0] = (byte)(pn >> 24);
            aad[pnOffset + 1] = (byte)(pn >> 16);
            aad[pnOffset + 2] = (byte)(pn >> 8);
            aad[pnOffset + 3] = (byte)pn;

            var sealedBody = AesOrChaCha(keys, body, aad, true);
            var packet = new byte[aad.Length + sealedBody.Length];
            Buffer.BlockCopy(aad, 0, packet, 0, aad.Length);
            Buffer.BlockCopy(sealedBody, 0, packet, aad.Length, sealedBody.Length);
            if (Environment.GetEnvironmentVariable("QUICDBG") == "2")
                _log(L10n.T("  1-RTT 明帧=") + BitConverter.ToString(body).Replace("-", "") +
                     " aad=" + BitConverter.ToString(aad).Replace("-", "") +
                     " key=" + BitConverter.ToString(keys.Key).Replace("-", "") +
                     " iv=" + BitConverter.ToString(keys.Iv).Replace("-", "") +
                     " pn=" + pn);

            // 头保护样本从 PN 起始处 +4 开始（RFC 9001 §5.4.2），
            // 也就是 aad.Length —— 别再 +4，那会整体错开 4 字节，对端去保护后 pn 全乱、AEAD 必挂。
            int sampleOff = pnOffset + 4;
            if (sampleOff + 16 <= packet.Length)
            {
                var sample = Slice(packet, sampleOff, 16);
                var mask = Tls13Session.HeaderProtectionMask(keys.Hp, sample, keys.Aes);
                packet[0] ^= (byte)(mask[0] & 0x1f);
                for (int i = 0; i < pnLen; i++) packet[pnOffset + i] ^= mask[1 + i];
            }
            return packet;
        }

        private byte[] AesOrChaCha(TlsKeys keys, byte[] body, byte[] aad, bool seal)
        {
            bool aes = keys.Aes;          // Initial 恒 AES；握手/应用级按协商套件
            var nonce = keys.NonceFor(pn: keys.Seq);
            if (seal)
            {
                if (aes) return AesGcmCipher.GcmEncrypt(keys.Key, nonce, body, aad);
                return ChaCha20Poly1305.Seal(keys.Key, nonce, body, aad);
            }
            if (aes) return AesGcmCipher.GcmDecrypt(keys.Key, nonce, body, aad);
            return ChaCha20Poly1305.Open(keys.Key, nonce, body, aad);
        }

        /* ===================== ACK ===================== */

        private void AckPacket(Level level, uint pn)
        {
            if (level == Level.Initial) _ackInitial.Add(pn);
            else if (level == Level.Handshake) _ackHandshake.Add(pn);
            else _ackApp.Add(pn);
        }

        private void MergeInitialAcks()
        {
            // 历史遗留：早期把 Initial 的 ACK 合并进 Handshake 包发送，这是**协议违规**
            // （ACK 只能在同一个包号空间里确认该空间的包，quinn 会直接 PROTOCOL_VIOLATION 关连接）。
            // 现在 Initial 的 ACK 一律留在 _ackInitial，由 FlushAcks 用 Initial 包发出。
        }

        /// <summary>把待确认的包号做成 ACK 帧发出去（严格按包号空间分层）。</summary>
        private void FlushAcks()
        {
            if (_closed) return;
            if (Environment.GetEnvironmentVariable("NOACK") == "1") return;   // 排障开关
            // 握手一旦完成，对端（quinn）会立刻丢弃 Initial / Handshake 空间与密钥。
            // 此时再往这两个空间发包，对端解密不了，会当成协议违规（PROTOCOL_VIOLATION）关连接。
            if (_handshakeComplete)
            {
                _ackInitial.Clear();
                _ackHandshake.Clear();
            }
            // Initial 空间：任何时候都补一个 Initial 包，且必须填充到 1200 字节
            if (_ackInitial.Count > 0)
            {
                var frames = new List<byte[]> { AckFrames(_ackInitial) };
                _ackInitial.Clear();
                SendInitial(frames, 1200);
            }
            if (_ackHandshake.Count > 0 && _sendLevel != Level.Initial)
            {
                var frames = new List<byte[]> { AckFrames(_ackHandshake) };
                _ackHandshake.Clear();
                if (_sendLevel == Level.Handshake) SendHandshake(frames);
                else SendOneRtt(frames);
            }
            if (_ackApp.Count > 0 && _sendLevel == Level.App)
            {
                var frames = new List<byte[]> { AckFrames(_ackApp) };
                _ackApp.Clear();
                SendOneRtt(frames);
            }
        }

        private static byte[] AckFrames(SortedSet<uint> pns)
        {
            // 切成连续段，每段一条 ACK 帧
            var ms = new System.IO.MemoryStream();
            var list = new List<uint>(pns);
            int i = 0;
            while (i < list.Count)
            {
                int j = i;
                while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
                uint largest = list[j];
                uint first = largest - list[i];
                var f = new System.IO.MemoryStream();
                QuicVarint.Write(f, 0x02);
                QuicVarint.Write(f, largest);
                QuicVarint.Write(f, 0);          // ack_delay（指数由对端定，0 最安全）
                QuicVarint.Write(f, 0);          // ack_range_count
                QuicVarint.Write(f, first);
                var fb = f.ToArray();
                ms.Write(fb, 0, fb.Length);
                i = j + 1;
            }
            return ms.ToArray();
        }

        /* ===================== 流帧 ===================== */

        internal void SendStreamFrame(uint streamId, long offset, byte[] data, bool fin)
        {
            var ms = new System.IO.MemoryStream();
            ulong type = 0x08 | 0x02;                 // STREAM + LEN
            if (offset > 0) type |= 0x04;
            if (fin) type |= 0x01;
            QuicVarint.Write(ms, type);
            QuicVarint.Write(ms, streamId);
            if (offset > 0) QuicVarint.Write(ms, (ulong)offset);
            QuicVarint.Write(ms, (ulong)data.Length);
            ms.Write(data, 0, data.Length);
            // 一行紧凑日志（每条流操作最多几条），排查「发出去但没回」时必不可少
            if (_streamTrace) _log("  → STREAM sid=" + streamId + " off=" + offset + " len=" + data.Length + (fin ? " fin" : ""));
            SendFrames(new List<byte[]> { ms.ToArray() });
        }

        /// <summary>打印每条 STREAM 帧的收发明细（默认关）。</summary>
        public static bool _streamTrace;   // 用 /api/qaul/trace?on=1 打开（真机没环境变量）

        /// <summary>RESET_STREAM：流 id + 应用错误码 + 最终大小。</summary>
        internal void SendResetStream(uint streamId, ulong appError, long finalSize)
        {
            var ms = new System.IO.MemoryStream();
            QuicVarint.Write(ms, 0x04);
            QuicVarint.Write(ms, streamId);
            QuicVarint.Write(ms, appError);
            QuicVarint.Write(ms, (ulong)finalSize);
            SendFrames(new List<byte[]> { ms.ToArray() });
        }

        private QuicStream GetOrCreateStream(uint id)
        {
            QuicStream st;
            bool created = false;
            // 与 OpenStream 共用 _streamLock：这张表是收发线程共享的，
            // Dictionary 并发读写会损坏甚至死循环，流号也可能被踩成重复值。
            lock (_streamLock)
            {
                if (!_streams.TryGetValue(id, out st))
                {
                    st = new QuicStream(this, id);
                    _streams[id] = st;
                    created = true;
                    // 只有「客户端发起的双向流」(id % 4 == 0) 才会影响我方下次分配的流号；
                    // 对端发起的 id（奇数）绝不能被当成本方流号，否则会发出非法流。
                    if ((id & 3) == 0 && id >= _nextStreamId) _nextStreamId = id + 4;
                }
            }
            // 对端发起的流（id 最低位为 1）要交给上层「接受」，
            // 否则 qaul 主动拨过来的 /qaul_info、ping、id 这些流没人应答。
            if (created && (id & 1) != 0) lock (_acceptLock) _acceptQueue.Enqueue(st);
            return st;
        }

        private readonly Queue<QuicStream> _acceptQueue = new Queue<QuicStream>();
        private readonly object _acceptLock = new object();

        /// <summary>取出一个对端发起的流（没有则返回 false）。</summary>
        public bool TryAcceptStream(out QuicStream stream)
        {
            lock (_acceptLock)
            {
                if (_acceptQueue.Count > 0) { stream = _acceptQueue.Dequeue(); return true; }
            }
            stream = null;
            return false;
        }

        /* ===================== 关闭 ===================== */

        private void SendConnectionClose(ulong errorCode, string reason)
        {
            try
            {
                var ms = new System.IO.MemoryStream();
                QuicVarint.Write(ms, 0x1c);
                QuicVarint.Write(ms, errorCode);
                var r = Encoding.ASCII.GetBytes(reason ?? "");
                QuicVarint.Write(ms, (ulong)r.Length);
                ms.Write(r, 0, r.Length);
                SendFrames(new List<byte[]> { ms.ToArray() });
            }
            catch { }
        }

        /* ===================== 小工具 ===================== */

        private void DeriveInitialKeys(byte[] dcid, out TlsKeys client, out TlsKeys server)
        {
            var initialSecret = Hkdf.Extract(InitialSalt, dcid);
            var c = Tls13Session.ExpandLabel(initialSecret, "client in", new byte[0], 32, false);
            var s = Tls13Session.ExpandLabel(initialSecret, "server in", new byte[0], 32, false);
            client = Tls13Session.DeriveQuicKeys(c, 16);
            server = Tls13Session.DeriveQuicKeys(s, 16);
            // Initial 级别固定 AES-128-GCM（RFC 9001 §5.2），与协商套件无关
            client.Aes = true;
            server.Aes = true;
        }

        private static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        private static string HexB(byte[] b)
        {
            if (b == null) return "(null)";
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        private static byte[] Slice(byte[] b, int off, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }

        private static byte[] ConcatAll(List<byte[]> parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int off = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, off, p.Length); off += p.Length; }
            return r;
        }

        private static byte[] ConcatAll(byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int off = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, off, p.Length); off += p.Length; }
            return r;
        }

        private static uint BE32(byte[] b, int off)
        {
            return (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// 把一条 QUIC 连接包装成 libp2p 的 mux 抽象（IStreamMux）。
    ///
    /// QUIC 本身自带多路复用，所以这里没有 yamux 那样的窗口/帧逻辑：
    /// 开流 = 分配一个「客户端发起的双向流」；对端开流 = QUIC 的 STREAM 帧自动创建。
    /// 这样 QaulNode 里那套 /qaul_info、/qaul_messaging、floodsub 的代码
    /// 不用改一行就能跑在 QUIC 上（真正走 qaul 中继、不落明文）。
    /// </summary>
    public sealed class QuicMux : IStreamMux, IDisposable
    {
        private readonly QuicConnection _conn;
        private readonly Thread _accept;
        private volatile bool _closed;

        public QuicMux(QuicConnection conn)
        {
            if (conn == null) throw new ArgumentNullException("conn");
            _conn = conn;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "quic-accept" };
            _accept.Start();
        }

        public Action<Stream> OnIncomingStream { get; set; }

        public bool Closed { get { return _closed || _conn.IsClosed; } }

        public Stream OpenStream()
        {
            return new QuicStreamAdapter(_conn.OpenStream());
        }

        /// <summary>QUIC 的流由实现自身回收，没有 yamux 那种需要显式 Forget 的窗口状态。</summary>
        public void Forget(uint id) { }

        public void Close()
        {
            _closed = true;
            try { _conn.Close(); } catch { }
        }

        public void Dispose() { Close(); }

        private void AcceptLoop()
        {
            while (!_closed && !_conn.IsClosed)
            {
                QuicStream st;
                if (!_conn.TryAcceptStream(out st)) { Thread.Sleep(50); continue; }
                // 业务层可能在构造完 mux 之后才挂上回调（Attach 里赋值），
                // 这里等一小会儿，避免刚建连时对端拨进来的流被直接丢掉。
                var cb = OnIncomingStream;
                int waited = 0;
                while (cb == null && !_closed && waited < 3000)
                {
                    Thread.Sleep(100); waited += 100;
                    cb = OnIncomingStream;
                }
                if (cb == null) { try { st.CloseWrite(); } catch { } continue; }
                var adapter = new QuicStreamAdapter(st);
                var t = new Thread(() =>
                {
                    try { cb(adapter); }
                    catch { try { st.CloseWrite(); } catch { } }
                }) { IsBackground = true, Name = "quic-stream" };
                t.Start();
            }
        }
    }
}

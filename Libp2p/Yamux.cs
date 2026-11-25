using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AhoChatA
{
    /// <summary>
    /// yamux 多路复用（spec: hashicorp/yamux，libp2p 的默认 muxer）。
    ///
    /// 12 字节头：version(1) type(1) flags(2,BE) streamID(4,BE) length(4,BE)。
    /// 只有 Data 帧后面跟 length 字节载荷；WindowUpdate/Ping/GoAway 的 length 是数值。
    ///
    /// libp2p 里「开一条流」= 发一个带 SYN 标志的 WindowUpdate(length=0)，
    /// 而不是先发数据，这点和直觉不同，必须照做否则对方不会创建流。
    /// </summary>
    public sealed class YamuxSession : IStreamMux
    {
        private const byte TypeData = 0;
        private const byte TypeWindowUpdate = 1;
        private const byte TypePing = 2;
        private const byte TypeGoAway = 3;

        private const ushort FlagSyn = 1;
        private const ushort FlagAck = 2;
        private const ushort FlagFin = 4;
        private const ushort FlagRst = 8;

        private const int InitialWindow = 256 * 1024;
        private const int MaxFramePayload = 16 * 1024;

        private readonly Stream io;
        private readonly bool isClient;
        private readonly object writeLock = new object();
        private readonly Dictionary<uint, YamuxStream> streams = new Dictionary<uint, YamuxStream>();
        private readonly object streamsLock = new object();

        private uint nextId;
        private volatile bool closed;
        private Thread reader;

        /// <summary>对端新开了一条流（业务层在这里协商协议）。</summary>
        public Action<Stream> OnIncomingStream { get; set; }

        public bool Closed { get { return closed; } }

        public YamuxSession(Stream io, bool isClient)
        {
            this.io = io;
            this.isClient = isClient;
            nextId = isClient ? 1u : 2u;

            reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "yamux-reader";
            reader.Start();
        }

        /* ===================== 帧编解码 ===================== */

        private void WriteFrame(byte type, ushort flags, uint streamId, uint length, byte[] payload)
        {
            lock (writeLock)
            {
                if (closed) return;
                try
                {
                    var head = new byte[12];
                    head[0] = 0;                       // version
                    head[1] = type;
                    head[2] = (byte)(flags >> 8);
                    head[3] = (byte)flags;
                    head[4] = (byte)(streamId >> 24);
                    head[5] = (byte)(streamId >> 16);
                    head[6] = (byte)(streamId >> 8);
                    head[7] = (byte)streamId;
                    head[8] = (byte)(length >> 24);
                    head[9] = (byte)(length >> 16);
                    head[10] = (byte)(length >> 8);
                    head[11] = (byte)length;

                    io.Write(head, 0, 12);
                    if (payload != null && payload.Length > 0)
                        io.Write(payload, 0, payload.Length);
                    io.Flush();
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Yamux", "写帧失败: " + ex.Message);
                    Close();
                }
            }
        }

        private void ReadLoop()
        {
            var head = new byte[12];
            try
            {
                while (!closed)
                {
                    if (!IoUtil.ReadExact(io, head, 0, 12)) break;
                    if (head[0] != 0) { Android.Util.Log.Warn("Yamux", "协议版本不支持: " + head[0]); break; }

                    byte type = head[1];
                    ushort flags = (ushort)((head[2] << 8) | head[3]);
                    uint streamId = ((uint)head[4] << 24) | ((uint)head[5] << 16) | ((uint)head[6] << 8) | head[7];
                    uint length = ((uint)head[8] << 24) | ((uint)head[9] << 16) | ((uint)head[10] << 8) | head[11];

                    if (type == TypeData)
                    {
                        if (length > 4 * 1024 * 1024) break;
                        var payload = new byte[length];
                        if (length > 0 && !IoUtil.ReadExact(io, payload, 0, (int)length)) break;

                        var st = Find(streamId);
                        if (st == null && (flags & FlagSyn) != 0)
                        {
                            st = CreateInbound(streamId);
                        }
                        if (st != null)
                        {
                            st.OnData(payload, (flags & FlagFin) != 0);
                            if (length > 0) st.WindowReplenish((int)length);
                        }
                        continue;
                    }

                    if (type == TypeWindowUpdate)
                    {
                        if ((flags & FlagSyn) != 0)
                        {
                            var st = Find(streamId) ?? CreateInbound(streamId);
                            // 回 ACK，告知对方流已建立
                            WriteFrame(TypeWindowUpdate, FlagAck, streamId, 0, null);
                            if (st != null) st.MarkAcked();
                        }
                        else
                        {
                            var st = Find(streamId);
                            if (st != null)
                            {
                                if ((flags & FlagFin) != 0) st.OnRemoteFin();
                                if ((flags & FlagRst) != 0) st.OnRemoteReset();
                                if (length > 0) st.OnWindowUpdate((int)length);
                            }
                        }
                        continue;
                    }

                    if (type == TypePing)
                    {
                        if ((flags & FlagSyn) != 0)
                            WriteFrame(TypePing, FlagAck, 0, length, null);
                        continue;
                    }

                    if (type == TypeGoAway)
                    {
                        Android.Util.Log.Info("Yamux", "收到 GoAway，会话关闭");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Yamux", "读取循环异常: " + ex.Message);
            }

            Close();
        }

        private YamuxStream Find(uint id)
        {
            lock (streamsLock)
            {
                YamuxStream s;
                return streams.TryGetValue(id, out s) ? s : null;
            }
        }

        private YamuxStream CreateInbound(uint id)
        {
            var st = new YamuxStream(this, id, false);
            lock (streamsLock)
            {
                if (streams.ContainsKey(id)) return streams[id];
                streams[id] = st;
            }
            var cb = OnIncomingStream;
            if (cb != null)
            {
                var t = new Thread(() =>
                {
                    try { cb(st); }
                    catch (Exception ex) { Android.Util.Log.Warn("Yamux", "处理入站流失败: " + ex.Message); }
                });
                t.IsBackground = true;
                t.Start();
            }
            return st;
        }

        /* ===================== 对外 API ===================== */

        public Stream OpenStream()
        {
            uint id = nextId;
            nextId += 2;
            var st = new YamuxStream(this, id, true);
            lock (streamsLock) { streams[id] = st; }

            WriteFrame(TypeWindowUpdate, FlagSyn, id, 0, null);
            return st;
        }

        internal void SendData(uint id, byte[] data, int offset, int count, bool fin)
        {
            var payload = new byte[count];
            Buffer.BlockCopy(data, offset, payload, 0, count);
            ushort flags = fin ? FlagFin : (ushort)0;
            WriteFrame(TypeData, flags, id, (uint)count, payload);
        }

        internal void SendFin(uint id, bool rst)
        {
            WriteFrame(TypeWindowUpdate, rst ? FlagRst : FlagFin, id, 0, null);
        }

        internal void SendWindowUpdate(uint id, int delta)
        {
            if (delta <= 0) return;
            WriteFrame(TypeWindowUpdate, 0, id, (uint)delta, null);
        }

        internal void RemoveStream(uint id)
        {
            lock (streamsLock) { streams.Remove(id); }
        }

        /// <summary>只删本地流表项，不发任何帧（用于对端已读完的一次性协议流）。</summary>
        public void Forget(uint id)
        {
            lock (streamsLock) { streams.Remove(id); }
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { WriteFrame(TypeGoAway, 0, 0, 0, null); } catch { }
            try { io.Dispose(); } catch { }
        }

        public const int WindowSize = InitialWindow;
        public const int FramePayload = MaxFramePayload;
    }

    /// <summary>yamux 上的一条逻辑流（可读可写，Read 阻塞直到有数据或对端关闭）。</summary>
    public sealed class YamuxStream : Stream, IMuxStreamInfo
    {
        private readonly YamuxSession session;
        private readonly uint id;
        private readonly Queue<byte[]> incoming = new Queue<byte[]>();
        private byte[] current = new byte[0];
        private int currentPos;
        private bool remoteClosed;
        private bool localClosed;
        private int sendWindow = YamuxSession.WindowSize;
        private readonly object gate = new object();

        public uint Id { get { return id; } }

        uint IMuxStreamInfo.StreamId { get { return id; } }
        void IMuxStreamInfo.HalfClose() { try { CloseWrite(); } catch { } }
        void IMuxStreamInfo.Abort() { try { Reset(); } catch { } }
        public bool Inbound { get; private set; }

        internal YamuxStream(YamuxSession session, uint id, bool outbound)
        {
            this.session = session;
            this.id = id;
            Inbound = !outbound;
        }

        internal void OnData(byte[] data, bool fin)
        {
            lock (gate)
            {
                if (data != null && data.Length > 0) incoming.Enqueue(data);
                if (fin) remoteClosed = true;
                Monitor.PulseAll(gate);
            }
        }

        internal void OnRemoteFin()
        {
            lock (gate) { remoteClosed = true; Monitor.PulseAll(gate); }
        }

        internal void OnRemoteReset()
        {
            lock (gate) { remoteClosed = true; incoming.Clear(); Monitor.PulseAll(gate); }
        }

        internal void OnWindowUpdate(int delta)
        {
            lock (gate) { sendWindow += delta; Monitor.PulseAll(gate); }
        }

        internal void MarkAcked() { }

        internal void WindowReplenish(int consumed)
        {
            session.SendWindowUpdate(id, consumed);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                while (true)
                {
                    if (currentPos < current.Length)
                    {
                        int n = Math.Min(count, current.Length - currentPos);
                        Buffer.BlockCopy(current, currentPos, buffer, offset, n);
                        currentPos += n;
                        return n;
                    }

                    if (incoming.Count > 0)
                    {
                        current = incoming.Dequeue();
                        currentPos = 0;
                        continue;
                    }

                    if (remoteClosed) return 0;

                    if (!Monitor.Wait(gate, 120000)) return 0;   // 超时当作结束，避免线程永久挂住
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int sent = 0;
            while (sent < count)
            {
                int chunk = Math.Min(YamuxSession.FramePayload, count - sent);

                lock (gate)
                {
                    while (sendWindow < chunk && !remoteClosed)
                    {
                        if (!Monitor.Wait(gate, 30000)) break;
                    }
                    if (remoteClosed) return;
                    sendWindow -= chunk;
                }

                session.SendData(id, buffer, offset + sent, chunk, false);
                sent += chunk;
            }
        }

        /// <summary>半关闭：告诉对端不再发送数据（仍可继续读）。</summary>
        public void CloseWrite()
        {
            if (localClosed) return;
            localClosed = true;
            session.SendFin(id, false);
        }

        /// <summary>立即重置。</summary>
        public void Reset()
        {
            localClosed = true;
            session.SendFin(id, true);
            session.RemoveStream(id);
            lock (gate) { remoteClosed = true; Monitor.PulseAll(gate); }
        }

        public override void Close()
        {
            CloseWrite();
            session.RemoveStream(id);
            lock (gate) { Monitor.PulseAll(gate); }
        }

        public override void Flush() { }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return !localClosed; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}

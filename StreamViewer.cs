using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 单个查看端的发送器：有界帧队列 + 独立发送线程。
    ///
    /// 为什么必须这样：以前是在编码线程里直接同步写每个 socket，
    /// 只要有一个客户端读得慢（VLC 缓冲、网页切后台、Wi-Fi 抖一下），
    /// 编码线程就被 send timeout 卡住 5 秒 → 输出缓冲不释放 → 编码器反压 →
    /// 所有人都卡，然后写超时抛异常 → 直接把该客户端踢掉 = "播一会就掉线"。
    ///
    /// 现在的策略：
    ///   · 编码线程只做 Enqueue（非阻塞，几十微秒）；
    ///   · 每个客户端自己一个发送线程，互不影响；
    ///   · 队列拥塞时不杀连接，而是"丢到下一个关键帧"（直播标准做法），
    ///     同时向服务器上报拥塞信号，触发码率/分辨率自适应降档。
    /// </summary>
    internal sealed class StreamViewer
    {
        private struct Frame
        {
            public byte[] Data;
            public bool Key;
        }

        private readonly TcpClient _client;
        private readonly Stream _ns;
        private readonly object _q = new object();
        private readonly LinkedList<Frame> _queue = new LinkedList<Frame>();
        private readonly ManualResetEvent _done = new ManualResetEvent(false);
        private readonly int _maxFrames;
        private readonly long _maxBytes;

        private long _queuedBytes;
        private bool _awaitKey;
        private bool _closed;
        private int _errors;
        private Thread _thread;

        private long _dropped;
        private long _sent;

        /// <summary>丢帧 / 写超时等拥塞信号（用于自适应降档）。</summary>
        public Action<StreamViewer> OnCongestion;

        public string Remote { get; private set; }

        public StreamViewer(TcpClient client, Stream ns, int maxFrames, long maxBytes, string remote)
        {
            _client = client;
            _ns = ns;
            _maxFrames = Math.Max(2, maxFrames);
            _maxBytes = Math.Max(64 * 1024, maxBytes);
            Remote = remote;
        }

        public bool IsClosed { get { lock (_q) return _closed; } }

        public int QueueDepth { get { lock (_q) return _queue.Count; } }

        public long DroppedFrames { get { return Interlocked.Read(ref _dropped); } }

        public long SentFrames { get { return Interlocked.Read(ref _sent); } }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "cam-sender" };
            _thread.Start();
        }

        /// <summary>非阻塞入队。队列满 → 清空并只等下一个关键帧，绝不阻塞调用方（编码线程）。</summary>
        /// <summary>设为 true 后，只有关键帧才会入队——新接入的查看端从 IDR 开始，避免解码器缺参考帧。</summary>
        public bool AwaitKey { set { lock (_q) _awaitKey = value; } }

        public void Enqueue(byte[] data, bool isKey)
        {
            if (data == null || data.Length == 0) return;
            bool congested = false;

            lock (_q)
            {
                if (_closed) return;

                if (_awaitKey)
                {
                    if (!isKey) { Interlocked.Increment(ref _dropped); return; }
                    _awaitKey = false;
                    _queue.Clear();
                    _queuedBytes = 0;
                }

                _queue.AddLast(new Frame { Data = data, Key = isKey });
                _queuedBytes += data.Length;

                if (_queue.Count > _maxFrames || _queuedBytes > _maxBytes)
                {
                    // 已经跟不上了：丢掉积压，从下一个关键帧重新对齐
                    _queue.Clear();
                    _queuedBytes = 0;
                    if (isKey)
                    {
                        _queue.AddLast(new Frame { Data = data, Key = true });
                        _queuedBytes = data.Length;
                        _awaitKey = false;
                    }
                    else
                    {
                        _awaitKey = true;
                    }
                    Interlocked.Increment(ref _dropped);
                    congested = true;
                }
                Monitor.Pulse(_q);
            }

            if (congested) RaiseCongestion();
        }

        public void Close()
        {
            lock (_q)
            {
                if (_closed) return;
                _closed = true;
                _queue.Clear();
                _queuedBytes = 0;
                Monitor.PulseAll(_q);
            }
            try { _ns.Close(); } catch { }
            try { _client.Close(); } catch { }
            _done.Set();
        }

        /// <summary>等待该查看端结束（用于 HTTP 处理线程挂住连接）。</summary>
        public void WaitClosed(int millisecondsTimeout)
        {
            _done.WaitOne(millisecondsTimeout);
        }

        private void RaiseCongestion()
        {
            var cb = OnCongestion;
            if (cb == null) return;
            try { cb(this); } catch { }
        }

        private void Loop()
        {
            try
            {
                while (true)
                {
                    byte[] data = null;

                    lock (_q)
                    {
                        while (!_closed && _queue.Count == 0)
                        {
                            if (!Monitor.Wait(_q, 300) && _closed) break;
                        }
                        if (_closed) break;
                        if (_queue.Count > 0)
                        {
                            var f = _queue.First.Value;
                            _queue.RemoveFirst();
                            _queuedBytes -= f.Data.Length;
                            data = f.Data;
                        }
                    }

                    if (data == null) continue;

                    try
                    {
                        _ns.Write(data, 0, data.Length);
                        Interlocked.Increment(ref _sent);
                        _errors = 0;
                    }
                    catch (Exception ex)
                    {
                        if (_closed) break;

                        if (IsHardClose(ex))
                        {
                            Log.Warn("IpCam", "查看端断开：" + Remote + "（" + ex.Message + "）");
                            break;
                        }

                        // 只是这一下写不出去（对端缓冲满）：丢积压、等下一个关键帧，连接保留
                        lock (_q)
                        {
                            _queue.Clear();
                            _queuedBytes = 0;
                            _awaitKey = true;
                        }
                        Interlocked.Increment(ref _dropped);
                        RaiseCongestion();

                        _errors++;
                        if (_errors >= 6)
                        {
                            Log.Warn("IpCam", "查看端连续写失败，放弃：" + Remote);
                            break;
                        }
                        try { Thread.Sleep(40); } catch (ThreadInterruptedException) { break; }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("IpCam", "发送线程异常：" + ex.Message);
            }
            finally
            {
                Close();
            }
        }

        /// <summary>写超时属于"暂时跟不上"，不该断开；其余（连接重置/已关闭）才视为真的断开。</summary>
        private static bool IsHardClose(Exception ex)
        {
            if (ex is ObjectDisposedException) return true;

            var se = ex as SocketException ?? ex.InnerException as SocketException;
            if (se == null) return true;

            switch (se.SocketErrorCode)
            {
                case SocketError.TimedOut:
                case SocketError.WouldBlock:
                case SocketError.IOPending:
                case SocketError.TryAgain:
                    return false;
                default:
                    return true;
            }
        }
    }
}

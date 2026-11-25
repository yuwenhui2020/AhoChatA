using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AhoChatA
{
    /// <summary>
    /// qaul 消息端口（「qaul 渠道」）。
    ///
    /// 直接对接 qaul 的传输层约定，而不是重新发明一套：
    ///   · 分帧：u16 大端长度前缀 + 裸消息（qauld-rpc 的 LengthDelimitedCodec
    ///     就是这么配的：length_field_type = u16、big-endian、无调整量）；
    ///   · 载荷：QaulRpc 信封（module / request_id / user_id / data）；
    ///   · 语义：data 里装 qaul 的 chat 模块消息（ChatMessageSend / conversation_list），
    ///     群组/房间用 qaul 的 group_id（16 字节 UUID）标识。
    ///
    /// 为什么用「对等直连」而不是只连本机 qauld 守护进程：
    ///   Android 上并没有随包分发 qauld，若只做 RPC 客户端，手机之间根本没有
    ///   可连的总线；因此这里把同一套 qaul 帧直接跑在局域网点对点连接上
    ///   （端口沿用 qaul RPC 的 9199）。同一份编解码以后接真实 qauld 时无需改协议。
    ///
    /// 与 Mchat 原有 TCP JSON 渠道共存：两者互不干扰、各自监听各自的端口。
    /// </summary>
    public class QaulChannel : IDisposable
    {
        public const int DefaultPort = 9199;
        private const int PortScanCount = 20;
        private const int MaxFrameBytes = 65535;   // u16 长度前缀的上限
        private const int ConnectTimeoutMs = 2500;
        private const int SendTimeoutMs = 8000;

        private TcpListener listener;
        private CancellationTokenSource cts;
        private volatile bool running;

        private long rxCount;
        private long txCount;
        private long rxBytes;
        private long txBytes;

        /// <summary>实际监听端口（0 表示未启动）。</summary>
        public int Port { get; private set; }

        public bool Running { get { return running; } }

        public long RxCount { get { return Interlocked.Read(ref rxCount); } }
        public long TxCount { get { return Interlocked.Read(ref txCount); } }
        public long RxBytes { get { return Interlocked.Read(ref rxBytes); } }
        public long TxBytes { get { return Interlocked.Read(ref txBytes); } }

        /// <summary>收到一条 qaul 聊天消息（已解析出会话与正文）。</summary>
        public Action<QaulChatMessage, QaulEnvelope, string> OnMessage;

        /// <summary>底层异常/日志回调（可选）。</summary>
        public Action<string> OnLog;

        /* ===================== 生命周期 ===================== */

        public bool Start(int preferredPort = DefaultPort)
        {
            if (running) return true;
            cts = new CancellationTokenSource();

            for (int p = preferredPort; p < preferredPort + PortScanCount; p++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Any, p);
                    listener.Start();
                    Port = p;
                    break;
                }
                catch (SocketException)
                {
                    listener = null;
                }
            }

            if (listener == null)
            {
                Log(L10n.T("qaul 消息端口无法监听（9199-9219 均被占用），qaul 渠道不可用"));
                return false;
            }

            running = true;
            Log(L10n.T("qaul 消息端口监听 0.0.0.0:") + Port);

            var token = cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        var ignored = Task.Run(() => HandleClient(client), token);
                    }
                    catch (Exception)
                    {
                        if (token.IsCancellationRequested) break;
                        await Task.Delay(300).ConfigureAwait(false);
                    }
                }
            }, token);

            return true;
        }

        public void Stop()
        {
            running = false;
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            listener = null;
            Port = 0;
        }

        public void Dispose()
        {
            Stop();
        }

        /* ===================== 接收 ===================== */

        private void HandleClient(TcpClient client)
        {
            string addr = "";
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 30000;
                    addr = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "";

                    // 一条连接上可以连续来了多帧，逐帧解析直到对端关闭
                    while (running)
                    {
                        byte[] raw;
                        if (!ReadFrame(stream, out raw)) break;
                        if (raw == null || raw.Length == 0) continue;

                        Interlocked.Increment(ref rxCount);
                        Interlocked.Add(ref rxBytes, raw.Length);

                        var env = QaulEnvelope.Decode(raw);
                        if (env.Module != QaulModule.Chat) continue;

                        List<QaulChatMessage> messages;
                        try { messages = QaulWire.DecodeChat(env.Data); }
                        catch (Exception ex) { Log(L10n.T("解析 qaul 帧失败: ") + ex.Message); continue; }

                        foreach (var m in messages)
                        {
                            try { OnMessage?.Invoke(m, env, addr); }
                            catch (Exception ex) { Log(L10n.T("处理 qaul 消息失败: ") + ex.Message); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(L10n.T("qaul 连接异常(") + addr + "): " + ex.Message);
            }
        }

        /// <summary>读取一帧：2 字节大端长度 + 载荷。</summary>
        private static bool ReadFrame(Stream stream, out byte[] payload)
        {
            payload = null;
            var header = new byte[2];
            if (!ReadExact(stream, header, 0, 2)) return false;
            int len = (header[0] << 8) | header[1];
            if (len <= 0 || len > MaxFrameBytes) return false;
            var body = new byte[len];
            if (!ReadExact(stream, body, 0, len)) return false;
            payload = body;
            return true;
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        /* ===================== 发送 ===================== */

        /// <summary>把一帧投递到指定的 (ip, port) 列表，任一对端失败不影响其它对端。</summary>
        public int SendFrame(IEnumerable<IPEndPoint> targets, byte[] frame)
        {
            if (frame == null || frame.Length == 0 || frame.Length > MaxFrameBytes) return 0;
            int ok = 0;
            foreach (var ep in targets)
            {
                if (ep == null) continue;
                try
                {
                    using (var client = new TcpClient())
                    {
                        var task = client.ConnectAsync(ep.Address, ep.Port);
                        if (!task.Wait(ConnectTimeoutMs))
                        {
                            Log(L10n.T("qaul 投递超时: ") + ep);
                            continue;
                        }

                        client.SendTimeout = SendTimeoutMs;
                        using (var stream = client.GetStream())
                        {
                            var header = new byte[2];
                            header[0] = (byte)((frame.Length >> 8) & 0xFF);
                            header[1] = (byte)(frame.Length & 0xFF);
                            stream.Write(header, 0, 2);
                            stream.Write(frame, 0, frame.Length);
                            stream.Flush();
                        }
                    }
                    ok++;
                    Interlocked.Increment(ref txCount);
                    Interlocked.Add(ref txBytes, frame.Length);
                }
                catch (Exception ex)
                {
                    Log(L10n.T("qaul 投递失败 ") + ep + ": " + ex.Message);
                }
            }
            return ok;
        }

        public void ResetStats()
        {
            Interlocked.Exchange(ref rxCount, 0);
            Interlocked.Exchange(ref txCount, 0);
            Interlocked.Exchange(ref rxBytes, 0);
            Interlocked.Exchange(ref txBytes, 0);
        }

        private void Log(string text)
        {
            try { Android.Util.Log.Info("Qaul", text); } catch { }
            try { OnLog?.Invoke(text); } catch { }
        }
    }
}

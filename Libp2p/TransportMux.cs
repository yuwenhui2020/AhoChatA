using System;
using System.IO;
using System.Threading;

namespace AhoChatA
{
    /// <summary>
    /// 流上的「多路复用器特有」操作：编号、半关闭、中止。
    /// yamux 与 QUIC 的流实现都提供它，业务层据此不必区分具体传输。
    /// </summary>
    public interface IMuxStreamInfo
    {
        uint StreamId { get; }
        /// <summary>半关闭写方向（发 FIN），读方向继续可用。</summary>
        void HalfClose();
        /// <summary>中止这条流（不保证对端收到已发出的数据）。</summary>
        void Abort();
    }

    /// <summary>按具体实现分派流的编号/半关闭/中止操作。</summary>
    public static class QStreams
    {
        public static uint Id(Stream s)
        {
            var m = s as IMuxStreamInfo;
            return m == null ? 0 : m.StreamId;
        }

        public static void CloseWrite(Stream s)
        {
            var m = s as IMuxStreamInfo;
            if (m != null) { try { m.HalfClose(); } catch { } return; }
            try { s.Close(); } catch { }
        }

        public static void Reset(Stream s)
        {
            var m = s as IMuxStreamInfo;
            if (m != null) { try { m.Abort(); } catch { } return; }
            try { s.Close(); } catch { }
        }
    }

    /// <summary>
    /// 流多路复用器的统一抽象：yamux（TCP + Noise）与 QUIC（原生多流）都实现它。
    /// 业务层（QaulNode）只依赖这层，因此同一条代码路径既能走 TCP 也能走 QUIC。
    /// </summary>
    public interface IStreamMux
    {
        bool Closed { get; }
        /// <summary>开一条新的双向流（已建立协议协商前的裸流）。</summary>
        Stream OpenStream();
        /// <summary>对端新开了一条流（业务层在这里协商协议）。</summary>
        Action<Stream> OnIncomingStream { get; set; }
        /// <summary>丢弃指定的流（yamux 需要显式 Forget 释放窗口；QUIC 可忽略）。</summary>
        void Forget(uint id);
        void Close();
    }
}

// ============================================================
// 局域网「双向互通」确认门
//
// 需求约定：只有能双向互通的设备才算同一局域网。
// 实现复用现有发现协议：收到 hello → 回一帧 probe → 对方回 hello
// 即视为双向可达；未回包的设备不会拿到 mesh 频道消息。
//
// 这一层只影响 mesh 频道的可达判定，不改变其它聊天的设备列表行为，
// 因此老设备/非回应设备仍能正常使用私聊与其它频道。
// ============================================================
using System;
using System.Collections.Generic;
using System.Net;

namespace AhoChatA
{
    public static class LanPeerGate
    {
        // probe 回包窗口：超过窗口的 hello 不能证明双向可达
        private const long WindowMs = 20000;
        // 确认有效期：超时后需要重新握手（peer 本身还有独立的 LastSeen 超时）
        private const long FreshMs = 180000;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> Pending = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> Confirmed = new Dictionary<string, long>(StringComparer.Ordinal);

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>收到一台设备的 hello 时调用；sendProbe 用于向该设备回发 probe。</summary>
        public static void OnHello(string id, IPAddress address, Action<IPAddress> sendProbe)
        {
            if (string.IsNullOrEmpty(id) || address == null) return;
            var now = NowMs();
            bool needProbe = false;
            lock (Gate)
            {
                if (Confirmed.TryGetValue(id, out var at))
                {
                    if (now - at < FreshMs)
                    {
                        Confirmed[id] = now;      // 续期，不必重复探测
                        return;
                    }
                    Confirmed.Remove(id);
                }

                if (Pending.TryGetValue(id, out var sent) && now - sent <= WindowMs)
                {
                    // 我们探测过它，它也回了 hello → 双向互通成立
                    Confirmed[id] = now;
                    Pending.Remove(id);
                    return;
                }

                Pending[id] = now;
                needProbe = true;
            }
            if (needProbe)
            {
                try { sendProbe?.Invoke(address); } catch { }
            }
        }

        /// <summary>只有完成 hello ↔ probe ↔ hello 握手的设备才算局域网可达。</summary>
        public static bool IsConfirmed(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (Gate)
            {
                if (!Confirmed.TryGetValue(id, out var at)) return false;
                if (NowMs() - at > FreshMs) { Confirmed.Remove(id); return false; }
                return true;
            }
        }

        /// <summary>
        /// 直接确认：WLAN Direct 的 TCP 连接本身即双向互通的证明
        /// （我们连上对方，对方回 hello），无需再走 UDP probe 握手。
        /// </summary>
        public static void ConfirmDirect(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (Gate)
            {
                Confirmed[id] = NowMs();
                Pending.Remove(id);
            }
        }

        public static void Forget(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (Gate) { Pending.Remove(id); Confirmed.Remove(id); }
        }

        public static void Clear()
        {
            lock (Gate) { Pending.Clear(); Confirmed.Clear(); }
        }
    }
}

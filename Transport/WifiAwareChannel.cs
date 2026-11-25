using System;
using System.Collections.Concurrent;
using System.Linq;
using Android.Content;
using Android.Net.Wifi.Aware;
using Android.OS;

namespace AhoChatA
{
    /// <summary>
    /// Wi-Fi Aware（NAN）发现渠道 —— 链路层**真对等**：没有 GO、没有软接入点，
    /// 按服务名 publish/subscribe 互相发现（Android 8+）。
    ///
    /// 现状与限制（重要，勿删）：
    ///   本工程的 Xamarin.Android(v13.0) 绑定把 `WifiAwareSession.Publish()` / `Subscribe()`
    ///   的返回值绑成了 void，而 `PublishDiscoverySession` / `SubscribeDiscoverySession` 正是
    ///   构造 `WifiAwareNetworkSpecifier`（NAN 数据通道）与调用 `SendMessage`（互换地址）的必需对象。
    ///   因此**当前构建下 NAN 只能做发现/在场（discovery/presence）**，数据面仍走
    ///   WLAN Direct（Wi-Fi P2P）与蓝牙；要启用 NAN 原生数据通道，需迁移到 .NET for Android
    ///   （Microsoft.Android 绑定会正确返回 DiscoverySession）。
    ///
    /// 全部方法 try/catch 降级：设备不支持 NAN、未授予附近设备权限时静默不可用，绝不冒异常到主线程。
    /// </summary>
    public class WifiAwareChannel
    {
        private readonly Context context;
        private WifiAwareManager manager;
        private WifiAwareSession session;
        private volatile bool enabled;
        private string lastError = "";

        /// <summary>NAN 服务名（要互相发现，publish/subscribe 必须一致）。</summary>
        public const string ServiceName = "Mchat-nan";

        public Action OnPeersChanged;
        public Action<bool> OnStateChanged;
        public Action<string> OnLog;

        /// <summary>发现到的对端（PeerHandle 字符串 → 最近一次发现时刻 ms）。</summary>
        private readonly ConcurrentDictionary<string, long> peers =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        public bool Enabled => enabled;
        public string LastError => lastError;

        /// <summary>近 2 分钟内仍在场的 NAN 对端数。</summary>
        public int LinkedCount
        {
            get
            {
                long now = Now();
                return peers.Count(kv => now - kv.Value < 120000);
            }
        }

        public WifiAwareChannel(Context context)
        {
            this.context = context;
        }

        private void Log(string m)
        {
            lastError = m;
            try { OnLog?.Invoke(m); } catch { }
            Android.Util.Log.Info("WifiAware", m);
        }

        /* ===================== 启停 ===================== */

        public bool Enable()
        {
            if (enabled) return true;
            try
            {
                if ((int)Build.VERSION.SdkInt < 26)   // Android 8.0
                {
                    Log(L10n.T("系统低于 Android 8，无 Wi-Fi Aware"));
                    return false;
                }
                manager = (WifiAwareManager)context.GetSystemService(Context.WifiAwareService);
                if (manager == null || !manager.IsAvailable)
                {
                    Log(L10n.T("本机不支持 Wi-Fi Aware（NAN）"));
                    return false;
                }
                enabled = true;
                manager.Attach(new AttachCb(this), new Handler(Looper.MainLooper));
                Log(L10n.T("Wi-Fi Aware 渠道已启用，正在附着…"));
                return true;
            }
            catch (Exception ex)
            {
                Log(L10n.T("启用 Wi-Fi Aware 失败: ") + ex.Message);
                enabled = false;
                return false;
            }
        }

        public void Disable()
        {
            try { session?.Close(); } catch { }
            session = null;
            enabled = false;
            peers.Clear();
            Log(L10n.T("Wi-Fi Aware 渠道已关闭"));
            try { OnStateChanged?.Invoke(false); } catch { }
            try { OnPeersChanged?.Invoke(); } catch { }
        }

        /* ===================== 发现（publish/subscribe） ===================== */

        private void OnAttached(WifiAwareSession s)
        {
            session = s;
            Log(L10n.T("Wi-Fi Aware 已附着"));
            try { OnStateChanged?.Invoke(true); } catch { }
            try
            {
                // 同时扮演 publish 与 subscribe：对等网络里每个节点都能被找到、也能找别人。
                var pc = new PublishConfig.Builder().SetServiceName(ServiceName).Build();
                session.Publish(pc, new DiscoverCb(this), new Handler(Looper.MainLooper));
                var sc = new SubscribeConfig.Builder().SetServiceName(ServiceName).Build();
                session.Subscribe(sc, new DiscoverCb(this), new Handler(Looper.MainLooper));
                Log(L10n.T("Wi-Fi Aware 开始 publish/subscribe: ") + ServiceName);
            }
            catch (Exception ex)
            {
                Log(L10n.T("publish/subscribe 失败: ") + ex.Message);
            }
        }

        private void OnServiceDiscovered(PeerHandle ph)
        {
            try
            {
                if (ph == null) return;
                var key = ph.ToString();
                peers[key] = Now();
                try { OnPeersChanged?.Invoke(); } catch { }
                Log(L10n.T("NAN 发现对端 ") + key);
            }
            catch (Exception ex)
            {
                Log(L10n.T("处理 NAN 发现失败: ") + ex.Message);
            }
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /* ===================== 系统回调适配 ===================== */

        private class AttachCb : AttachCallback
        {
            private readonly WifiAwareChannel owner;
            public AttachCb(WifiAwareChannel o) { owner = o; }
            public override void OnAttached(WifiAwareSession session) { owner.OnAttached(session); }
            public override void OnAttachFailed() { owner.Log(L10n.T("Wi-Fi Aware 附着失败（可能未授予附近设备权限）")); }
        }

        private class DiscoverCb : DiscoverySessionCallback
        {
            private readonly WifiAwareChannel owner;
            public DiscoverCb(WifiAwareChannel o) { owner = o; }

            public override void OnServiceDiscovered(PeerHandle peerHandle, byte[] serviceSpecificInfo, System.Collections.Generic.IList<byte[]> matchFilter)
            {
                owner.OnServiceDiscovered(peerHandle);
            }

            public override void OnSessionConfigFailed()
            {
                owner.Log(L10n.T("NAN 会话配置失败"));
            }
        }
    }
}

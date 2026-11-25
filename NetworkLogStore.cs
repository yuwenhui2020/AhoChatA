using System;
using System.Collections.Generic;
using System.Linq;

namespace AhoChatA
{
    /// <summary>一条网络请求日志。</summary>
    public sealed class NetLogEntry
    {
        public long TimeMs { get; set; }
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "";
        public string Type { get; set; } = "other"; // document/script/stylesheet/image/font/xhr/media/other
        public string Host { get; set; } = "";
        public bool Blocked { get; set; }
        public int TabId { get; set; }
        public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(TimeMs).ToLocalTime().ToString("HH:mm:ss.fff");
    }

    /// <summary>
    /// 浏览器网络日志环形缓冲（最多 500 条）。在 ShouldInterceptRequest 里记录。
    /// 单例，仅内存，退出即清空，不写盘。
    /// </summary>
    public sealed class NetworkLogStore
    {
        private static readonly NetworkLogStore _inst = new NetworkLogStore();
        public static NetworkLogStore Instance => _inst;

        private const int Capacity = 500;
        private readonly object _gate = new object();
        private readonly List<NetLogEntry> _entries = new List<NetLogEntry>();
        private bool _enabled;
        public bool Enabled { get => _enabled; set { lock (_gate) _enabled = value; } }

        public void Add(string url, string method, string type, bool blocked, int tabId)
        {
            if (!_enabled || string.IsNullOrEmpty(url)) return;
            string host = "";
            try { host = new Uri(url).Host; } catch { }
            var e = new NetLogEntry
            {
                TimeMs = Java.Lang.JavaSystem.CurrentTimeMillis(),
                Method = method ?? "GET",
                Url = url,
                Type = type ?? "other",
                Host = host,
                Blocked = blocked,
                TabId = tabId
            };
            lock (_gate)
            {
                _entries.Add(e);
                if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
            }
        }

        public List<NetLogEntry> Snapshot()
        {
            lock (_gate) return _entries.ToList();
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        public int Count { get { lock (_gate) return _entries.Count; } }
    }
}

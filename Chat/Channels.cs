using System;
using System.Collections.Generic;
using System.Linq;
using Android.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AhoChatA
{
    /// <summary>
    /// 传输链路（渠道）。**链路只影响消息怎么传，不影响聊天室本身**：
    /// 一个频道就是一个聊天室，它可以同时挂载多条链路，任一链路收到的消息都进同一个频道。
    ///
    ///   · qaul   —— 主链路。qaul 有公共节点，能中继到公网，跨网段可达。
    ///   · bitchat—— 蓝牙 BLE mesh。**默认关闭**，只在切换进用到它的频道时才启用。
    ///   · p2p    —— WLAN Direct（Wi-Fi P2P），点对点直连，可跨公网。
    ///   · lan    —— 局域网 TCP/UDP 组播。
    /// </summary>
    public static class ChanLink
    {
        public const string Qaul = "qaul";
        public const string Bitchat = "bitchat";
        public const string P2p = "p2p";
        public const string Lan = "lan";

        /// <summary>
        /// 频道可选的接入方式。
        /// 注意：蓝牙（bitchat）**不再是一种频道链路**——它降级为全局的通信渠道开关，
        /// 在「通信渠道设置」里统一控制，也不再对接 bitchat 官方 App、不显示其身份。
        /// 常量 Bitchat 仍保留，仅用于兼容旧频道数据里已存的链路名。
        /// </summary>
        public static readonly string[] All = { Qaul, P2p, Lan };

        /// <summary>
        /// 新建频道时默认启用的链路：qaul（主链路） + 局域网 TCP。
        ///
        /// 为什么还留着局域网 TCP：电脑端（Windows）目前只有这条链路能连进来，
        /// 少了它「手机频道消息」到不了电脑。它现在不再是「另一套通信」，
        /// 而是同一套网格里的一个传输载体（进出都在 MeshRouter 里统一去重/中继）。
        /// </summary>
        public static readonly string[] DefaultLinks = { Qaul, Lan };

        /// <summary>单个默认链路（兼容旧代码：老数据里只有一个值时按 qaul 处理）。</summary>
        public const string DefaultLink = Qaul;

        public static string Label(string link)
        {
            switch (link)
            {
                case Qaul: return "qaul（公网通信 · 可经公共节点跨网）";
                case Bitchat: return "蓝牙（近场）";
                case P2p: return "WLAN Direct（点对点直连）";
                case Lan: return "局域网（同一 Wi-Fi）";
                default: return link;
            }
        }

        public static string ShortLabel(string link)
        {
            switch (link)
            {
                case Qaul: return "qaul";
                case Bitchat: return "蓝牙";
                case P2p: return "WLAN Direct";
                case Lan: return "局域网";
                default: return link;
            }
        }

        public static bool IsKnown(string link)
        {
            return link == Qaul || link == Bitchat || link == P2p || link == Lan;
        }
    }

    /// <summary>一个频道：名字 + 标题 + 启用的链路集合。</summary>
    public class Channel
    {
        public string Name;
        public string Title;

        /// <summary>启用的链路；为空时按默认（仅 qaul）处理。</summary>
        public HashSet<string> Links = new HashSet<string>();

        /// <summary>是否由用户主动加入（未加入的频道不出现在频道列表里，也不会被对端「拉」进来）。</summary>
        public bool Joined;

        public string Conv { get { return Convs.Chan(Name); } }

        public bool Has(string link)
        {
            if (Links == null || Links.Count == 0) return System.Array.IndexOf(ChanLink.DefaultLinks, link) >= 0;
            return Links.Contains(link);
        }

        public void EnsureDefaultLinks()
        {
            if (Links == null) Links = new HashSet<string>();
            if (Links.Count == 0) Links.UnionWith(ChanLink.DefaultLinks);
            // 过滤掉不认识的链路（老版本遗留）
            foreach (var l in Links.ToList())
                if (!ChanLink.IsKnown(l)) Links.Remove(l);
            if (Links.Count == 0) Links.UnionWith(ChanLink.DefaultLinks);
        }

        public List<string> LinkList()
        {
            EnsureDefaultLinks();
            return ChanLink.All.Where(Has).ToList();
        }

        /// <summary>顶栏/频道列表用的简短标记，例如「#mesh [qaul+蓝牙]」。</summary>
        public string Badge()
        {
            var links = LinkList();
            if (links.Count == 0) return "";
            var s = string.Join("+", links.Select(ChanLink.ShortLabel));
            return " [" + s + "]";
        }
    }

    /// <summary>
    /// 频道注册表：频道名 → 启用链路，持久化在 SharedPreferences("Mchat"/"channels")。
    ///
    /// 关键约定：
    ///   · 新建/加入频道默认只启用 qaul（需求：默认只监听 qaul 频道）；
    ///   · 只有被用户主动 Join 过的频道才进列表；
    ///   · 频道 conv = "chan:" + name，与 qaul 的 group_id 一一确定性映射（见 Convs.GroupId）。
    /// </summary>
    public class ChannelRegistry
    {
        private readonly Context ctx;
        private readonly object gate = new object();
        private readonly Dictionary<string, Channel> map = new Dictionary<string, Channel>(StringComparer.Ordinal);

        public const string PrefKey = "channels";
        public const string DefaultChannelName = "mesh";

        public ChannelRegistry(Context context)
        {
            ctx = context;
            Load();
            // 默认频道始终存在（只监听 qaul），并且默认就是「已加入」
            Ensure(DefaultChannelName, DefaultChannelName, joined: true);
        }

        private void Load()
        {
            // 需求：加入的所有频道退出程序即不保存。
            // 启动时清掉历史落盘数据，本次运行只在内存里维护频道；
            // 下次启动只有内置 mesh 默认频道会被重建。
            try
            {
                var prefs = ctx.GetSharedPreferences("Mchat", FileCreationMode.Private);
                if (!string.IsNullOrEmpty(prefs.GetString(PrefKey, null)))
                    prefs.Edit().Remove(PrefKey).Apply();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Channels", "清理历史频道失败: " + ex.Message);
            }
        }

        private void SaveLocked()
        {
            // 需求：频道不跨进程保存。保留方法以维持原有调用点，但不再写 SharedPreferences。
        }

        /// <summary>该 conv 是否为已加入频道（中继模式下只允许已加入频道聊天）。</summary>
        public bool IsJoinedConv(string conv)
        {
            var n = Convs.ChanName(conv ?? "");
            if (string.IsNullOrEmpty(n)) return false;
            lock (gate) return map.TryGetValue(n, out var c) && c.Joined;
        }

        /// <summary>退出前清空内存中的非默认频道（默认 mesh 每次启动都会重建）。</summary>
        public void ClearForExit()
        {
            lock (gate)
            {
                var def = Convs.NormalizeChannel(DefaultChannelName);
                foreach (var key in map.Keys.Where(k => !string.Equals(k, def, StringComparison.OrdinalIgnoreCase)).ToList())
                    map.Remove(key);
                if (map.TryGetValue(def, out var d)) d.Joined = true;
            }
        }

        /// <summary>老版本把频道标题存成「#mesh」，显示前统一去掉 # 前缀。</summary>
        public static string Strip(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            return title.TrimStart('#').Trim();
        }

        /// <summary>
        /// 取（或创建）一个频道。默认只启用 qaul 链路。
        /// <para>
        /// joined 默认 false —— 这一点很关键：对端在某个频道里说话时，我们会按需把这个频道
        /// 建出来（这样才能按频道的链路设置去投递/回执），但**不能**顺手把它标成已加入，
        /// 否则「别人进了新频道，我这边的频道列表里也自动多一个」。
        /// 只有用户在「加入 / 新建频道」里主动确认过，才走 MarkJoined。
        /// </para>
        /// </summary>
        public Channel Ensure(string name, string title = null, bool joined = false)
        {
            var n = Convs.NormalizeChannel(name);
            lock (gate)
            {
                if (map.TryGetValue(n, out var c))
                {
                    if (!string.IsNullOrEmpty(title) && c.Title != Strip(title)) { c.Title = Strip(title); SaveLocked(); }
                    c.EnsureDefaultLinks();
                    return c;
                }
                c = new Channel { Name = n, Title = string.IsNullOrEmpty(title) ? n : Strip(title), Joined = joined };
                c.EnsureDefaultLinks();
                map[n] = c;
                SaveLocked();
                return c;
            }
        }

        /// <summary>按 qaul 群 id（16 字节 hex）反查已加入的频道；找不到返回 null。</summary>
        public Channel ByGroupHex(string groupHex)
        {
            if (string.IsNullOrEmpty(groupHex) || groupHex.Length != 32) return null;
            lock (gate)
            {
                foreach (var c in map.Values)
                {
                    try
                    {
                        if (string.Equals(Convs.GroupIdHex(c.Conv), groupHex, StringComparison.OrdinalIgnoreCase))
                            return c;
                    }
                    catch { }
                }
                return null;
            }
        }

        public Channel Get(string name)
        {
            var n = Convs.NormalizeChannel(name);
            lock (gate) { return map.TryGetValue(n, out var c) ? c : null; }
        }

        /// <summary>按 conv 反查频道（chan:xxx → Channel）。</summary>
        public Channel ByConv(string conv)
        {
            var n = Convs.ChanName(conv);
            return string.IsNullOrEmpty(n) ? null : Get(n);
        }

        /// <summary>该频道是否启用了某条链路。</summary>
        public bool HasLink(string nameOrConv, string link)
        {
            var ch = Convs.IsChan(nameOrConv ?? "") ? ByConv(nameOrConv) : Get(nameOrConv);
            return ch != null && ch.Has(link);
        }

        /// <summary>设置某频道启用的链路集合。</summary>
        public void SetLinks(string name, IEnumerable<string> links)
        {
            var n = Convs.NormalizeChannel(name);
            lock (gate)
            {
                if (!map.TryGetValue(n, out var c)) c = Ensure(n);
                c.Links = new HashSet<string>(links ?? Enumerable.Empty<string>());
                c.EnsureDefaultLinks();
                SaveLocked();
            }
        }

        public List<string> LinksOf(string nameOrConv)
        {
            var ch = Convs.IsChan(nameOrConv ?? "") ? ByConv(nameOrConv) : Get(nameOrConv);
            return ch == null ? new List<string>() : ch.LinkList();
        }

        /// <summary>所有已加入的频道（频道列表 UI 用）。</summary>
        public List<Channel> Joined()
        {
            lock (gate) return map.Values.Where(c => c.Joined).OrderBy(c => c.Name).ToList();
        }

        /// <summary>标记为已加入（用户从菜单加入频道时调用）。</summary>
        public void MarkJoined(string name, string title = null)
        {
            var n = Convs.NormalizeChannel(name);
            lock (gate)
            {
                var c = Ensure(n, title);
                c.Joined = true;
                SaveLocked();
            }
        }

        /// <summary>取消加入（仅隐藏入口，不删消息）。</summary>
        public bool Leave(string name)
        {
            var n = Convs.NormalizeChannel(name);
            lock (gate)
            {
                if (!map.TryGetValue(n, out var c)) return false;
                c.Joined = false;
                SaveLocked();
                return true;
            }
        }
    }
}
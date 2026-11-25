using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>会话摘要（会话条 / 侧栏用）。</summary>
    public class Conversation
    {
        public string Id;
        public bool IsDm;
        public string Title;
        public string Subtitle;
        public long UpdatedAt;
        public int Unread;
        public string LastText;
        public string LastFrom;
    }

    /// <summary>
    /// 消息仓库：JSONL 追加式持久化 + 内存索引。
    ///
    /// 设计目标（合并 Mchat 的兼容性与 bitchat 的聊天逻辑）：
    ///   1) 会话隔离：每条消息带 conv 键，房间与私聊互不串台；
    ///   2) 删除能力：逐条删除 / 清空会话 / 全部清空，删除以「墓碑」形式落盘并
    ///      可通过控制帧广播给对端（对端同步删除），重启后依然生效；
    ///   3) 崩溃安全：只追加不改写，加载时按行回放；行数过多时压缩重写。
    ///
    /// 行格式：
    ///   {"t":"m","m":{消息}}              新增消息
    ///   {"t":"d","i":"消息id"}            删除单条
    ///   {"t":"c","c":"会话键"}            清空会话
    ///   {"t":"x"}                         全部清空
    ///   {"t":"r","c":"会话键","v":毫秒}   已读时间点
    ///   {"t":"v","c":"会话键","n":"标题"} 会话标题（私聊用昵称补齐）
    /// </summary>
    public class ChatStore
    {
        private const int MaxMessages = 3000;       // 内存中保留的最大消息数
        private const int CompactThreshold = 4000;  // 追加行数超过该值触发压缩

        private readonly string filePath;
        /// <summary>
        /// 是否把消息落盘。本应用定位为「即时通信」而非「留言板」：消息只在本次运行
        /// 期间存在于内存，进程退出即清空，启动时也不回放历史——这正是「不保存消息、
        /// 每次启动/关闭都清除消息」的设计。置 false 时 Load() 不回放、AppendLine 不写盘，
        /// 并在构造期顺手删掉上一版遗留的 store.jsonl，避免旧消息「复活」。
        /// 内存部分的去重（knownIds）/ 会话索引 / 删除墓碑照常工作，会话内可靠性不受影响。
        /// </summary>
        private readonly bool persist;
        private readonly object gate = new object();

        private readonly List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
        private readonly HashSet<string> deletedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> knownIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> readAt = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.Ordinal);

        private int appendedLines;
        private bool dirtyCompact;
        private int compactScheduled;

        /// <summary>任何变化（新增/删除/清空/已读）后触发，用于刷新原生界面与网页端。</summary>
        public event Action Changed;

        public ChatStore(string dir, bool persist = true)
        {
            this.persist = persist;
            try
            {
                Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, "store.jsonl");
                // 即时通信模式：启动即清空上一版遗留的落盘消息，避免旧记录「复活」。
                if (!persist && filePath != null && File.Exists(filePath))
                {
                    try { File.Delete(filePath); } catch { }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("ChatStore", "初始化失败: " + ex.Message);
                filePath = null;
            }
        }

        /* ===================== 加载 / 持久化 ===================== */

        public void Load()
        {
            lock (gate)
            {
                items.Clear();
                deletedIds.Clear();
                knownIds.Clear();
                readAt.Clear();
                titles.Clear();
                appendedLines = 0;

                // 即时通信模式：不回放历史，每次启动都是干净的会话。
                if (!persist) return;
                if (filePath == null || !File.Exists(filePath)) return;
                try
                {
                    foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        ApplyLine(line);
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("ChatStore", "加载历史失败: " + ex.Message);
                }
                Trim();
            }
        }

        private void ApplyLine(string line)
        {
            Dictionary<string, object> row;
            try { row = JsonConvert.DeserializeObject<Dictionary<string, object>>(line); }
            catch { return; }
            if (row == null) return;

            var t = Msg.Str(row, "t");
            if (t == "m")
            {
                object raw;
                if (!row.TryGetValue("m", out raw) || raw == null) return;
                var msg = raw as Dictionary<string, object>;
                if (msg == null)
                {
                    try { msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(raw.ToString()); }
                    catch { return; }
                }
                if (msg == null) return;
                var id = Msg.Str(msg, "id");
                if (string.IsNullOrEmpty(id) || knownIds.Contains(id)) return;
                knownIds.Add(id);
                items.Add(msg);
            }
            else if (t == "d")
            {
                var id = Msg.Str(row, "i");
                if (!string.IsNullOrEmpty(id)) deletedIds.Add(id);
            }
            else if (t == "c")
            {
                var conv = Msg.Str(row, "c");
                if (string.IsNullOrEmpty(conv)) return;
                foreach (var m in items) if (Msg.Conv(m) == conv) deletedIds.Add(Msg.Str(m, "id"));
            }
            else if (t == "u")
            {
                // 移除会话：消息墓碑 + 忘掉标题与已读进度。
                // 光ius清消息（"c"）不够 —— 会话标题存在 titles 里，
                // load 时又会按 titles 把这个会话「长回来」，界面上就是「删不掉」。
                var conv = Msg.Str(row, "c");
                if (string.IsNullOrEmpty(conv)) return;
                foreach (var m in items) if (Msg.Conv(m) == conv) deletedIds.Add(Msg.Str(m, "id"));
                titles.Remove(conv);
                readAt.Remove(conv);
            }
            else if (t == "x")
            {
                foreach (var m in items) deletedIds.Add(Msg.Str(m, "id"));
            }
            else if (t == "r")
            {
                var conv = Msg.Str(row, "c");
                if (!string.IsNullOrEmpty(conv)) readAt[conv] = Msg.Num(row, "v");
            }
            else if (t == "v")
            {
                var conv = Msg.Str(row, "c");
                var name = Msg.Str(row, "n");
                if (!string.IsNullOrEmpty(conv) && !string.IsNullOrEmpty(name)) titles[conv] = name;
            }
        }

        private void AppendLine(string line)
        {
            if (!persist || filePath == null) return;
            try
            {
                File.AppendAllText(filePath, line + "\n", Encoding.UTF8);
                appendedLines++;
                if (appendedLines > CompactThreshold) { dirtyCompact = true; ScheduleCompact(); }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("ChatStore", "写入失败: " + ex.Message);
            }
        }

        /// <summary>压缩：把当前内存状态整体重写为一份最小日志。</summary>
        public void Compact()
        {
            if (filePath == null) return;
            lock (gate)
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var conv in titles.Keys)
                    {
                        sb.Append(JsonConvert.SerializeObject(new { t = "v", c = conv, n = titles[conv] })).Append('\n');
                    }
                    foreach (var kv in readAt)
                    {
                        sb.Append(JsonConvert.SerializeObject(new { t = "r", c = kv.Key, v = kv.Value })).Append('\n');
                    }
                    foreach (var m in items)
                    {
                        var dead = deletedIds.Contains(Msg.Str(m, "id"));
                        if (dead) continue;
                        sb.Append(JsonConvert.SerializeObject(new { t = "m", m })).Append('\n');
                    }
                    var tmp = filePath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                    try { File.Delete(filePath); } catch { }
                    File.Move(tmp, filePath);
                    appendedLines = 0;
                    dirtyCompact = false;
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("ChatStore", "压缩失败: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 防抖调度一次压缩。
        ///
        /// 原实现里 <see cref="dirtyCompact"/> 只被置位、从来没被读过——
        /// 也就是说「行数超过 4000 就压缩」这条规则实际从未生效，追加日志会一直涨：
        /// 每次启动都要多读几万行、内存里 deletedIds / knownIds 也一起变大。
        /// 这里把标志真正接上：攒 5 秒（合并连续写入）后在后台压一次，
        /// 只占一个线程，且不与前台写入抢锁（Compact 内部自己拿 gate）。
        /// </summary>
        private void ScheduleCompact()
        {
            if (Interlocked.CompareExchange(ref compactScheduled, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try { await Task.Delay(5000).ConfigureAwait(false); } catch { }
                try
                {
                    // 压的过程中可能又被写脏，压到干净为止（正常情况下一次就够）
                    while (dirtyCompact)
                    {
                        try { Compact(); } catch { break; }
                    }
                }
                finally { Interlocked.Exchange(ref compactScheduled, 0); }
            });
        }

        private void Trim()
        {
            if (items.Count <= MaxMessages) return;
            items.RemoveRange(0, items.Count - MaxMessages);
            dirtyCompact = true;
            ScheduleCompact();
        }

        private void Notify()
        {
            try { Changed?.Invoke(); } catch { }
        }

        /* ===================== 写入 ===================== */

        /// <summary>新增一条消息；重复 id 或已删除的 id 会被忽略（跨渠道去重）。</summary>
        public bool Add(IDictionary<string, object> msg, bool persist = true)
        {
            if (msg == null) return false;
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return false;

            bool added = false;
            lock (gate)
            {
                if (knownIds.Contains(id) || deletedIds.Contains(id)) return false;
                knownIds.Add(id);
                items.Add(new Dictionary<string, object>(msg));
                if (persist)
                {
                    AppendLine(JsonConvert.SerializeObject(new { t = "m", m = msg }));
                }
                Trim();
            }
            added = true;
            if (added) Notify();
            return true;
        }

        /// <summary>
        /// 用新内容**替换**一条已存在的消息（按 id 原地覆盖）。
        ///
        /// 用途：加密消息先到、密钥后到时会先落一条「[无法解密]」占位；等密钥就绪、
        /// 同一条消息被重传过来且解密成功，就调用这里把占位换成真正的正文。
        /// 不能先 Delete 再 Add —— Delete 会留下墓碑，同 id 之后再也加不进来。
        /// </summary>
        public bool Replace(IDictionary<string, object> msg)
        {
            if (msg == null) return false;
            var id = Msg.Str(msg, "id");
            if (string.IsNullOrEmpty(id)) return false;
            lock (gate)
            {
                if (deletedIds.Contains(id)) return false;
                for (int i = 0; i < items.Count; i++)
                {
                    if (Msg.Str(items[i], "id") != id) continue;
                    items[i] = new Dictionary<string, object>(msg);
                    if (persist) AppendLine(JsonConvert.SerializeObject(new { t = "m", m = msg }));
                    break;
                }
            }
            Notify();
            return true;
        }

        /// <summary>删除单条消息（写墓碑，重启后仍然删除）。</summary>
        public bool Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (gate)
            {
                if (!deletedIds.Add(id)) return false;
                AppendLine(JsonConvert.SerializeObject(new { t = "d", i = id }));
            }
            Notify();
            return true;
        }

        /// <summary>就地修改一条消息的正文（用于「编辑消息」）。</summary>
        public bool UpdateText(string id, string text)
        {
            if (string.IsNullOrEmpty(id)) return false;
            bool ok = false;
            lock (gate)
            {
                if (deletedIds.Contains(id)) return false;
                for (int i = 0; i < items.Count; i++)
                {
                    if (Msg.Str(items[i], "id") != id) continue;
                    var copy = new Dictionary<string, object>(items[i]);
                    copy["text"] = text ?? "";
                    copy["edited"] = true;
                    items[i] = copy;
                    if (persist) AppendLine(JsonConvert.SerializeObject(new { t = "m", m = copy }));
                    ok = true;
                    break;
                }
            }
            if (ok) Notify();
            return ok;
        }

        /// <summary>清空某个会话的全部消息。</summary>
        public int Clear(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return 0;
            int count = 0;
            lock (gate)
            {
                foreach (var m in items)
                {
                    if (Msg.Conv(m) != conv) continue;
                    if (deletedIds.Add(Msg.Str(m, "id"))) count++;
                }
                AppendLine(JsonConvert.SerializeObject(new { t = "c", c = conv }));
                readAt[conv] = Now();
            }
            if (count > 0) Notify();
            return count;
        }

        /// <summary>清空全部会话。</summary>
        public int ClearAll()
        {
            int count = 0;
            lock (gate)
            {
                foreach (var m in items)
                {
                    if (deletedIds.Add(Msg.Str(m, "id"))) count++;
                }
                AppendLine(JsonConvert.SerializeObject(new { t = "x" }));
            }
            if (count > 0) Notify();
            return count;
        }

        /// <summary>
        /// 彻底移除一个会话：消息全部墓碑化，并删除会话标题与已读进度。
        /// 与 Clear 不同 —— Clear 只清消息、保留标题，load 时又会按标题把会话「长回来」，
        /// 顶栏就表现为「删不掉」。移除后该会话在顶栏彻底消失。
        /// </summary>
        public int RemoveConversation(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return 0;
            int count = 0;
            lock (gate)
            {
                foreach (var m in items)
                {
                    if (Msg.Conv(m) != conv) continue;
                    if (deletedIds.Add(Msg.Str(m, "id"))) count++;
                }
                AppendLine(JsonConvert.SerializeObject(new { t = "u", c = conv }));
            }
            Notify();
            return count;
        }

        public void MarkRead(string conv)
        {
            if (string.IsNullOrEmpty(conv)) return;
            lock (gate)
            {
                var now = Now();
                long old;
                // 5 秒内重复标记不重复写盘（OnResume / 切会话会频繁触发）
                if (readAt.TryGetValue(conv, out old) && now - old < 5000) { readAt[conv] = now; return; }
                readAt[conv] = now;
                AppendLine(JsonConvert.SerializeObject(new { t = "r", c = conv, v = now }));
            }
            Notify();
        }

        /// <summary>
        /// 更新某会话某发送者的已读时间点（来自对端 read_receipt 帧）。
        /// 仅内存更新，不写盘、不触发 Notify（由调用方决定是否刷新 UI）。
        /// 返回是否有更新。
        /// </summary>
        public bool MarkReadUpTo(string conv, string fromPeerId, long readUpToMs)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(fromPeerId) || readUpToMs <= 0) return false;
            string key = conv + "|" + fromPeerId;
            lock (gate)
            {
                if (readAt.TryGetValue(key, out long old) && readUpToMs <= old) return false;
                readAt[key] = readUpToMs;
            }
            return true;
        }

        /// <summary>获取某会话某发送者的已读时间点（毫秒），0 表示无记录。</summary>
        public long GetReadUpTo(string conv, string fromPeerId)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(fromPeerId)) return 0;
            string key = conv + "|" + fromPeerId;
            lock (gate)
            {
                readAt.TryGetValue(key, out long v);
                return v;
            }
        }

        public void SetTitle(string conv, string title)
        {
            if (string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(title)) return;
            lock (gate)
            {
                string old;
                if (titles.TryGetValue(conv, out old) && old == title) return;
                titles[conv] = title;
                AppendLine(JsonConvert.SerializeObject(new { t = "v", c = conv, n = title }));
            }
            Notify();
        }

        /* ===================== 读取 ===================== */

        /// <summary>按 id 取一条消息（返回副本，改它不会影响仓库）。</summary>
        public Dictionary<string, object> Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (gate)
            {
                foreach (var m in items)
                    if (Msg.Str(m, "id") == id) return new Dictionary<string, object>(m);
            }
            return null;
        }

        public bool IsDeleted(string id)        {
            lock (gate) { return deletedIds.Contains(id); }
        }

        public bool Has(string id)
        {
            lock (gate) { return knownIds.Contains(id); }
        }

        /// <summary>某会话的消息（按时间正序）。</summary>
        public List<Dictionary<string, object>> Messages(string conv, int limit)
        {
            var list = new List<Dictionary<string, object>>();
            lock (gate)
            {
                foreach (var m in items)
                {
                    if (Msg.Conv(m) != conv) continue;
                    if (deletedIds.Contains(Msg.Str(m, "id"))) continue;
                    list.Add(m);
                }
            }
            list.Sort((a, b) => Msg.Num(a, "time").CompareTo(Msg.Num(b, "time")));
            if (limit > 0 && list.Count > limit) list = list.Skip(list.Count - limit).ToList();
            return list;
        }

        /// <summary>最近的若干条消息（悬浮窗预览用，跨会话）。</summary>
        public List<Dictionary<string, object>> Recent(int limit)
        {
            var list = new List<Dictionary<string, object>>();
            lock (gate)
            {
                for (int i = items.Count - 1; i >= 0 && list.Count < limit; i--)
                {
                    var m = items[i];
                    if (deletedIds.Contains(Msg.Str(m, "id"))) continue;
                    list.Add(m);
                }
            }
            list.Reverse();
            return list;
        }

        /// <summary>会话摘要列表：房间 + 私聊，按最后活跃时间倒序。</summary>
        public List<Conversation> Conversations()
        {
            var map = new Dictionary<string, Conversation>(StringComparer.Ordinal);
            lock (gate)
            {
                foreach (var m in items)
                {
                    var id = Msg.Str(m, "id");
                    if (deletedIds.Contains(id)) continue;
                    var conv = Msg.Conv(m);
                    // qaul 现在只作为通信渠道：它自己的「公开信息 feed」「qaul 群组」
                    // 不再作为独立会话出现在列表里（消息仍照常收发，只是不建会话条目）。
                    if (conv != null && (conv.StartsWith("feed:", StringComparison.Ordinal)
                                      || conv.StartsWith(Convs.QaulGroupPrefix, StringComparison.Ordinal))) continue;
                    Conversation c;
                    if (!map.TryGetValue(conv, out c))
                    {
                        c = new Conversation { Id = conv, IsDm = Convs.IsDm(conv) };
                        map[conv] = c;
                    }
                    // 取最大值：跨设备时钟偏差或延迟消息乱序到达时，避免较旧消息把会话时间"改老"
                    c.UpdatedAt = System.Math.Max(c.UpdatedAt, Msg.Num(m, "time"));
                    c.LastFrom = Msg.Str(m, "fromName");
                    var text = Msg.Str(m, "text");
                    if (string.IsNullOrEmpty(text)) text = PreviewOf(m);
                    c.LastText = text;

                    long at;
                    if (!readAt.TryGetValue(conv, out at)) at = 0;
                    // 已读时间之前 / 自己发出的不算未读
                    var self = Msg.Str(m, "from");
                    if (at == 0 || Msg.Num(m, "time") > at) { /* 计入 */ }
                    if (Msg.Num(m, "time") > at && !IsSelf(m)) c.Unread++;
                }

                // 已知但没有消息的会话（刚加入的房间 / 新开的私聊）也要显示
                foreach (var conv in titles.Keys)
                {
                    if (map.ContainsKey(conv)) continue;
                    map[conv] = new Conversation
                    {
                        Id = conv,
                        IsDm = Convs.IsDm(conv),
                        Title = titles[conv],
                        UpdatedAt = 0
                    };
                }

                foreach (var c in map.Values)
                {
                    if (string.IsNullOrEmpty(c.Title))
                    {
                        string t;
                        c.Title = titles.TryGetValue(c.Id, out t) && !string.IsNullOrEmpty(t)
                            ? t
                            : Convs.TitleOf(c.Id);
                    }
                }
            }

            var list = map.Values.ToList();
            list.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return list;
        }

        private Func<IDictionary<string, object>, bool> selfPredicate;

        /// <summary>由外部注入「是否本机发出」的判定（本机 peerId 在 Bridge 里才知道）。</summary>
        public void SetSelfPredicate(Func<IDictionary<string, object>, bool> predicate)
        {
            selfPredicate = predicate;
        }

        private bool IsSelf(IDictionary<string, object> m)
        {
            try { return selfPredicate != null && selfPredicate(m); }
            catch { return false; }
        }

        private static string PreviewOf(IDictionary<string, object> m)
        {
            var kind = Msg.Kind(m);
            switch (kind)
            {
                case "pic": return "[图片]";
                case "video": return "[视频]";
                case "music": return "[音乐]";
                case "url": return "[链接]";
                case "voice": return "[语音]";
                case "notice": return "[通知]";
                case "location": return "[位置]";
                case "control": return "[指令]";
                case "musiccard": return "【音乐播放共享】";
                default: return "";
            }
        }

        public int UnreadTotal()
        {
            return Conversations().Sum(c => c.Unread);
        }

        public int Count(string conv)
        {
            lock (gate)
            {
                int n = 0;
                foreach (var m in items)
                {
                    if (conv != null && Msg.Conv(m) != conv) continue;
                    if (deletedIds.Contains(Msg.Str(m, "id"))) continue;
                    n++;
                }
                return n;
            }
        }

        private static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}

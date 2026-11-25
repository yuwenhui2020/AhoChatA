using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>控制帧语义（「指令渠道」）。</summary>
    public static class MsgOp
    {
        /// <summary>功能控制指令（音量/播放/上下曲…），接收端模拟输入。</summary>
        public const string Cmd = "cmd";
        /// <summary>删除单条消息（墓碑，双方同步删除）。</summary>
        public const string Del = "del";
        /// <summary>清空某个会话的全部消息。</summary>
        public const string Clear = "clear";
        /// <summary>上线握手（告知昵称与所在房间），用于私聊标题补全。</summary>
        public const string Hello = "hello";
        /// <summary>重命名会话（text=新标题）：本机生效并广播，对端同步改名。</summary>
        public const string Rename = "rename";
        /// <summary>撤回消息（target=被撤回消息 id）：本机立即删除并广播，对端同步删除。</summary>
        public const string Recall = "recall";
        /// <summary>编辑消息（target=被编辑消息 id，text=新正文）：本机改字并广播，对端同步改字。</summary>
        public const string Edit = "edit";
        /// <summary>
        /// 整条消息原地替换（target=被替换消息 id，其余字段即新消息内容）。
        /// 与 Edit 只改正文不同，Patch 会连附件/状态字段一起换 —— 音乐卡片切歌就靠它，
        /// 不用为了换一首歌再发一张新卡片。
        /// </summary>
        public const string Patch = "patch";
    }

    /// <summary>
    /// 消息字典的统一包装。
    ///
    /// 为什么仍然用字典：Mchat 全链路（原生 / WebView 桥 / 网页端 HTTP / 悬浮窗）
    /// 都以 Dictionary&lt;string,object&gt; 作为消息载体，保持不变可以零成本兼容既有
    /// 的全部渲染与转发代码；新增的字段（conv 等）只是附加键。
    /// </summary>
    public static class Msg
    {
        public const string TextKind = "text";
        public const string ControlKind = "control";

        /// <summary>
        /// 附件消息的正文：**不显示文件名**，只给类型与体积，例如「[文件] 3.00 MB」。
        ///
        /// 为什么不再用文件名当正文：文件名会出现在会话列表预览、通知、中继日志里，
        /// 长文件名会把一行撑爆，而且同一张图转几次就变成一屏「IMG_20261027_...jpg」。
        /// 真正的文件名仍保存在 name 字段里，只用于下载时保存成原名。
        /// </summary>
        public static string FileLabel(string kind, long bytes)
        {
            string tag;
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "pic": case "image": case "photo": tag = "[图片]"; break;
                case "video": tag = "[视频]"; break;
                case "audio": case "voice": case "music": tag = "[音频]"; break;
                case "whiteboard.stroke": case "whiteboard": tag = "[白板笔迹]"; break;
                default: tag = L10n.T("[文件]"); break;
            }
            string size = SizeText(bytes);
            return size.Length == 0 ? tag : tag + " " + size;
        }

        /// <summary>人类可读体积（1.23 MB / 456 KB），0 返回空串。</summary>
        public static string SizeText(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        public static string Str(IDictionary<string, object> d, string key, string def = null)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            return v.ToString();
        }

        public static long Num(IDictionary<string, object> d, string key, long def = 0)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            long n;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is double) return (long)(double)v;
            return long.TryParse(v.ToString(), out n) ? n : def;
        }

        public static bool Bool(IDictionary<string, object> d, string key)
        {
            if (d == null) return false;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(v.ToString(), out b) && b;
        }

        /// <summary>消息所属会话；老版本对端不带 conv 时统一归入默认房间。</summary>
        public static string Conv(IDictionary<string, object> d)
        {
            var c = Str(d, "conv");
            if (string.IsNullOrEmpty(c)) return Convs.Room(Convs.DefaultRoom);
            return c;
        }

        public static string Kind(IDictionary<string, object> d)
        {
            var k = Str(d, "kind");
            return string.IsNullOrEmpty(k) ? TextKind : k;
        }

        public static bool IsControl(IDictionary<string, object> d)
        {
            return Kind(d) == ControlKind || !string.IsNullOrEmpty(Str(d, "op"));
        }

        public static string Op(IDictionary<string, object> d)
        {
            return Str(d, "op");
        }

        /// <summary>构造一条新消息。</summary>
        public static Dictionary<string, object> New(string conv, string from, string fromName,
                                                     string kind, string text)
        {
            var msg = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["conv"] = string.IsNullOrEmpty(conv) ? Convs.Room(Convs.DefaultRoom) : conv,
                ["kind"] = string.IsNullOrEmpty(kind) ? TextKind : kind,
                ["text"] = text ?? "",
                ["from"] = from ?? "",
                ["fromName"] = fromName ?? "",
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                // 每条消息从出生就带 hop=0；中继时每跳 +1，到上限即停止转发。
                // 显式带上，避免"缺失字段=0"的隐式假设，也便于链路统计/排障。
                ["hop"] = 0
            };
            return msg;
        }

        public static Dictionary<string, object> Control(string conv, string op)
        {
            var msg = New(conv, "", "", ControlKind, "");
            msg["op"] = op;
            return msg;
        }

        /* ============ qaul 侧元数据（附加在 ChatContent 的扩展字段里） ============
         * qaul 的 ChatContent 只有 text 一个字段，纯文本消息互操作友好；
         * 但 Mchat 需要额外的会话键、昵称、富媒体类型等信息，因此把它们放进
         * ChatContent 的扩展字段 100（protobuf 未知字段会被标准 qaul 客户端忽略）。
         * 键名刻意取短，控制帧体积尽量小。
         */
        public static string MetaJson(IDictionary<string, object> d,
                                      IDictionary<string, object> extra = null)
        {
            var meta = new Dictionary<string, object>();
            meta["ck"] = Conv(d);

            // lid：Mchat 的路由 id。qaul 的 user_id 用 qaul 自己的 PeerId，
            // 因此必须另带一个 id 才能让房间/私聊的对端匹配继续工作。
            var lid = Str(d, "from");
            if (!string.IsNullOrEmpty(lid)) meta["lid"] = lid;
            var name = Str(d, "fromName");
            if (!string.IsNullOrEmpty(name)) meta["n"] = name;

            var kind = Kind(d);
            if (kind != TextKind) meta["k"] = kind;

            var url = Str(d, "url");
            if (!string.IsNullOrEmpty(url)) meta["u"] = url;

            var file = Str(d, "media");
            if (!string.IsNullOrEmpty(file)) meta["f"] = file;

            var op = Str(d, "op");
            if (!string.IsNullOrEmpty(op)) meta["o"] = op;

            var cmd = Str(d, "cmd");
            if (!string.IsNullOrEmpty(cmd)) meta["c"] = cmd;

            var target = Str(d, "target");
            if (!string.IsNullOrEmpty(target)) meta["t"] = target;

            var reply = Str(d, "replyTo");
            if (!string.IsNullOrEmpty(reply)) meta["q"] = reply;

            long dur = Num(d, "duration");
            if (dur > 0) meta["d"] = dur;

            if (Bool(d, "bot")) meta["b"] = 1;

            // 音乐卡片（musiccard 与 op=patch 都带 songKey）：
            // 这些字段不在 LC1 加密的正文里（正文只有 text），必须走 meta，
            // 否则 qaul 对端只会收到一个「kind=musiccard 但没内容」的空壳。
            if (d.ContainsKey("songKey"))
            {
                void Put(string key, string val)
                {
                    if (!string.IsNullOrEmpty(val)) meta[key] = val;
                }
                Put("mt", Str(d, "title"));
                Put("ma", Str(d, "artist"));
                Put("mb", Str(d, "album"));
                Put("mk", Str(d, "songKey"));
                Put("mc", Str(d, "cover"));
                Put("ml", Str(d, "lrc"));
                // 歌词归属的曲目：patch 合并会沿用上一条卡片的 lrc，
                // 没有它就无法判断这段歌词是不是当前这首歌的。
                Put("mlk", Str(d, "lrcKey"));
                long mp = Num(d, "position");
                // position=0（切歌回到开头 / 拖到起点）也必须带，否则对端会沿用旧的进度
                if (d.ContainsKey("position")) meta["mp"] = mp;
                meta["mg"] = Bool(d, "playing") ? 1 : 0;
                long mz = Num(d, "ts");
                if (mz > 0) meta["mz"] = mz;
                long ms = Num(d, "seq");
                if (ms > 0) meta["ms"] = ms;
                var pk = Str(d, "patchKind");
                if (!string.IsNullOrEmpty(pk)) meta["pk"] = pk;

                // 「一起听」播放列表：随音乐卡片一起走 qaul。列表可能几 KB，
                // 单帧有 65535 上限，超过则只给局域网直投（那边是完整 JSON），
                // qaul 侧退化为「仍能看到卡片但无列表」。
                var pl = Str(d, "playlist");
                if (!string.IsNullOrEmpty(pl) && pl.Length <= 16000) meta["mq"] = pl;
                var lh = Str(d, "ltHost");
                if (!string.IsNullOrEmpty(lh)) meta["mh"] = lh;
                long li = Num(d, "ltIndex");
                // ltIndex=0（切回歌单第一首）必须带上：以前只在 >0 时发，
                // qaul 对端会沿用旧索引，导致「切歌不同步」。
                if (d.ContainsKey("ltIndex")) meta["mx"] = li;
                if (Num(d, "lt") > 0) meta["mlt"] = 1;
            }

            // 「同步听」时钟对时帧：qaul 的 ChatContent 只带 text + meta，
            // 四时间戳必须显式进 meta，否则跨 qaul 中继后整帧会退化成空壳。
            if (kind == SyncClock.KindPing || kind == SyncClock.KindPong)
            {
                if (d.ContainsKey("q")) meta["sq"] = Num(d, "q");
                if (d.ContainsKey("t1")) meta["s1"] = Num(d, "t1");
                if (d.ContainsKey("t2")) meta["s2"] = Num(d, "t2");
                if (d.ContainsKey("t3")) meta["s3"] = Num(d, "t3");
                var sto = Str(d, "to");
                if (!string.IsNullOrEmpty(sto)) meta["st"] = sto;
            }

            // 视频分享卡片（videocard）：让 qaul 对端也能看到封面、标题与播放地址
            if (Kind(d) == "videocard")
            {
                void PutV(string key, string val)
                {
                    if (!string.IsNullOrEmpty(val)) meta[key] = val;
                }
                PutV("vt", Str(d, "title"));
                PutV("vc", Str(d, "cover"));
                PutV("vp", Str(d, "poster"));
                if (Bool(d, "local")) meta["vl"] = 1;
                // 「一起看」字段：地址较长，超长则不进 qaul meta（局域网渠道仍带全量字段）
                PutV("vq", Str(d, "posterProxy"));
                PutV("vk", Str(d, "vkey"));
                PutV("wk", Str(d, "watchCard"));
                PutV("wh", Str(d, "watchHost"));
                var wu = Str(d, "watchUrl");
                if (!string.IsNullOrEmpty(wu) && wu.Length <= 1200) PutV("wu", wu);
            }

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    if (kv.Value != null) meta[kv.Key] = kv.Value;
                }
            }

            return JsonConvert.SerializeObject(meta);
        }

        /// <summary>把 qaul 侧元数据回填到消息字典。</summary>
        public static void ApplyMeta(IDictionary<string, object> msg, string metaJson)
        {
            if (msg == null || string.IsNullOrEmpty(metaJson)) return;
            try
            {
                var meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(metaJson);
                if (meta == null) return;
                object v;
                if (meta.TryGetValue("ck", out v) && v != null && !msg.ContainsKey("conv"))
                    msg["conv"] = v.ToString();
                // lid：对端的 Mchat 路由 id，优先于 qaul PeerId 作为 from
                if (meta.TryGetValue("lid", out v) && v != null) msg["from"] = v.ToString();
                if (meta.TryGetValue("n", out v) && v != null) msg["fromName"] = v.ToString();
                if (meta.TryGetValue("k", out v) && v != null) msg["kind"] = v.ToString();
                if (meta.TryGetValue("u", out v) && v != null) msg["url"] = v.ToString();
                if (meta.TryGetValue("f", out v) && v != null) msg["media"] = v.ToString();
                if (meta.TryGetValue("o", out v) && v != null) msg["op"] = v.ToString();
                if (meta.TryGetValue("c", out v) && v != null) msg["cmd"] = v.ToString();
                if (meta.TryGetValue("t", out v) && v != null) msg["target"] = v.ToString();
                if (meta.TryGetValue("q", out v) && v != null) msg["replyTo"] = v.ToString();
                if (meta.TryGetValue("d", out v) && v != null) msg["duration"] = v;
                // sig：qaul 帧的 Ed25519 签名（hex），验签逻辑在 MchatBridge.OnQaulMessage
                if (meta.TryGetValue("sig", out v) && v != null) msg["sig"] = v.ToString();
                if (meta.TryGetValue("b", out v) && v != null) msg["bot"] = true;

                // 音乐卡片字段还原
                if (meta.TryGetValue("mt", out v) && v != null) msg["title"] = v.ToString();
                if (meta.TryGetValue("ma", out v) && v != null) msg["artist"] = v.ToString();
                if (meta.TryGetValue("mb", out v) && v != null) msg["album"] = v.ToString();
                if (meta.TryGetValue("mk", out v) && v != null) msg["songKey"] = v.ToString();
                if (meta.TryGetValue("mc", out v) && v != null) msg["cover"] = v.ToString();
                if (meta.TryGetValue("ml", out v) && v != null) msg["lrc"] = v.ToString();
                if (meta.TryGetValue("mlk", out v) && v != null) msg["lrcKey"] = v.ToString();
                if (meta.TryGetValue("mp", out v) && v != null) msg["position"] = v;
                if (meta.TryGetValue("mg", out v) && v != null) msg["playing"] = Convert.ToInt64(v) != 0;
                if (meta.TryGetValue("mz", out v) && v != null) msg["ts"] = v;
                if (meta.TryGetValue("ms", out v) && v != null) msg["seq"] = v;
                if (meta.TryGetValue("pk", out v) && v != null) msg["patchKind"] = v.ToString();
                // 「一起听」播放列表还原
                if (meta.TryGetValue("mq", out v) && v != null) msg["playlist"] = v.ToString();
                if (meta.TryGetValue("mh", out v) && v != null) msg["ltHost"] = v.ToString();
                if (meta.TryGetValue("mx", out v) && v != null) msg["ltIndex"] = v;
                if (meta.TryGetValue("mlt", out v) && v != null) msg["lt"] = Convert.ToInt64(v);
                // 「同步听」时钟对时帧还原
                if (meta.TryGetValue("sq", out v) && v != null) msg["q"] = v;
                if (meta.TryGetValue("s1", out v) && v != null) msg["t1"] = v;
                if (meta.TryGetValue("s2", out v) && v != null) msg["t2"] = v;
                if (meta.TryGetValue("s3", out v) && v != null) msg["t3"] = v;
                if (meta.TryGetValue("st", out v) && v != null) msg["to"] = v.ToString();
                // 视频分享卡片还原
                if (meta.TryGetValue("vt", out v) && v != null) msg["title"] = v.ToString();
                if (meta.TryGetValue("vc", out v) && v != null) msg["cover"] = v.ToString();
                if (meta.TryGetValue("vp", out v) && v != null) msg["poster"] = v.ToString();
                if (meta.TryGetValue("vl", out v) && v != null) msg["local"] = Convert.ToInt64(v) != 0;
                if (meta.TryGetValue("vq", out v) && v != null) msg["posterProxy"] = v.ToString();
                if (meta.TryGetValue("vk", out v) && v != null) msg["vkey"] = v.ToString();
                if (meta.TryGetValue("wk", out v) && v != null) msg["watchCard"] = v.ToString();
                if (meta.TryGetValue("wh", out v) && v != null) msg["watchHost"] = v.ToString();
                if (meta.TryGetValue("wu", out v) && v != null) msg["watchUrl"] = v.ToString();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Qaul", "解析消息元数据失败: " + ex.Message);
            }
        }

        public static string FormatTime(long ms)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
                       .ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }
    }
}

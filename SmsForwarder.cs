using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.Content;
using Newtonsoft.Json;

namespace AhoChatA
{
    /// <summary>
    /// 短信转发中枢：把「收到的短信」送进 Mchat 频道。
    ///
    /// 为什么需要它（而不是让接收器直接调桥）：
    ///   1. 系统可能把短信广播投给一个**刚被拉起、桥还没建好**的进程 —— 此时直接转发等于丢弃。
    ///      这里改为先写进 FilesDir 下的待发队列，桥启动后自动补发（FlushPending）。
    ///   2. 同一条短信可能从两条路进来（系统广播 + 收件箱监听），必须去重，否则频道里出现两份。
    ///   3. 统一记录状态，菜单里能一眼看到「最近一次转发成功与否」，不用连电脑抓日志。
    /// </summary>
    public static class SmsForwarder
    {
        private const string PendingFileName = "sms_pending.jsonl";
        private const int MaxPending = 200;
        private const int DedupWindowMs = 10 * 60 * 1000;

        private static readonly object gate = new object();
        private static readonly Dictionary<string, long> recent = new Dictionary<string, long>();

        /// <summary>最近一次处理的结论（菜单里展示，便于自查）。</summary>
        public static string LastStatus { get; private set; } = L10n.T("尚未收到短信");

        /// <summary>最近一次短信的发送方与正文预览。</summary>
        public static string LastPreview { get; private set; } = "";

        /// <summary>本次进程内累计成功转发条数。</summary>
        public static int ForwardedCount { get; private set; }

        /// <summary>
        /// 处理一条收到的短信：去重 → 立刻转发（桥就绪）或暂存（桥不可用）。
        /// source 仅用于日志/状态（"broadcast" / "inbox" / "test"）。
        /// </summary>
        public static void Handle(Context ctx, string sender, string body, string source)
        {
            body = (body ?? "").Trim();
            if (body.Length == 0) return;

            var key = (sender ?? "") + "|" + body;
            lock (gate)
            {
                PruneRecent();
                if (recent.ContainsKey(key))
                {
                    Android.Util.Log.Info("SmsForward", "重复短信，忽略（" + source + "）");
                    return;
                }
                recent[key] = Now();
            }

            LastPreview = (string.IsNullOrEmpty(sender) ? L10n.T("未知号码") : sender) + "：" + Preview(body);

            var b = MchatBridge.Current;
            if (b == null || !b.IsStarted)
            {
                Queue(ctx, sender, body);
                LastStatus = L10n.T("应用未运行，已暂存（共 ") + PendingCount(ctx) + L10n.T(" 条），下次启动自动补发");
                Android.Util.Log.Info("SmsForward", "桥未就绪，短信已暂存（" + source + "）");
                EmitStatus("queued", sender, body, LastStatus);
                return;
            }

            try
            {
                b.ForwardSms(sender, body);
                ForwardedCount++;
                LastStatus = L10n.T("已转发 ") + DateTime.Now.ToString("HH:mm:ss") + "（" + source + "）";
                Android.Util.Log.Info("SmsForward", "短信已转发到频道（" + source + "）");
                EmitStatus("sent", sender, body, LastStatus);
            }
            catch (Exception ex)
            {
                Queue(ctx, sender, body);
                LastStatus = L10n.T("转发失败已暂存：") + ex.Message;
                Android.Util.Log.Warn("SmsForward", "转发失败: " + ex.Message);
                EmitStatus("failed", sender, body, LastStatus);
            }
        }

        /// <summary>桥启动后把暂存的短信补发出去；返回补发条数。</summary>
        public static int FlushPending(MchatBridge bridge)
        {
            if (bridge == null) return 0;
            var ctx = Android.App.Application.Context;
            string path;
            lock (gate) { path = PendingPath(ctx); }

            List<KeyValuePair<string, string>> items;
            lock (gate)
            {
                items = ReadPending(path);
                if (items.Count == 0) return 0;
                try { File.Delete(path); } catch { }
            }

            int ok = 0;
            foreach (var kv in items)
            {
                try
                {
                    bridge.ForwardSms(kv.Key, kv.Value);
                    ok++;
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("SmsForward", "补发短信失败: " + ex.Message);
                    lock (gate) { AppendPending(path, kv.Key, kv.Value); }
                }
            }
            if (ok > 0)
            {
                ForwardedCount += ok;
                LastStatus = L10n.T("补发 ") + ok + L10n.T(" 条暂存短信 ") + DateTime.Now.ToString("HH:mm:ss");
                Android.Util.Log.Info("SmsForward", "补发暂存短信 " + ok + " 条");
                EmitStatus("flushed", null, null, LastStatus);
            }
            return ok;
        }

        /// <summary>当前暂存的待发短信条数。</summary>
        public static int PendingCount(Context ctx)
        {
            try
            {
                lock (gate) { return ReadPending(PendingPath(ctx)).Count; }
            }
            catch { return 0; }
        }

        /// <summary>清空暂存队列（菜单里可手动清）。</summary>
        public static void ClearPending(Context ctx)
        {
            try
            {
                lock (gate)
                {
                    var p = PendingPath(ctx);
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }

        /* ===================== 内部实现 ===================== */

        private static void EmitStatus(string state, string sender, string body, string message)
        {
            try
            {
                MchatBridge.Current?.Emit("smsforward", new
                {
                    state,
                    sender = sender ?? "",
                    chars = body == null ? 0 : body.Length,
                    message = message ?? ""
                });
            }
            catch { }
        }

        private static string Preview(string body)
        {
            if (body.Length <= 24) return body;
            return body.Substring(0, 24) + "…";
        }

        private static string PendingPath(Context ctx)
        {
            var dir = ctx.FilesDir.AbsolutePath;
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, PendingFileName);
        }

        private static void Queue(Context ctx, string sender, string body)
        {
            try
            {
                lock (gate)
                {
                    var path = PendingPath(ctx);
                    var list = ReadPending(path);
                    if (list.Count >= MaxPending) list.RemoveAt(0);   // 丢最旧的，防止无限增长
                    list.Add(new KeyValuePair<string, string>(sender ?? "", body));
                    var sb = new StringBuilder();
                    foreach (var kv in list)
                    {
                        sb.AppendLine(JsonConvert.SerializeObject(new { s = kv.Key, b = kv.Value, t = Now() }));
                    }
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("SmsForward", "暂存短信失败: " + ex.Message);
            }
        }

        private static void AppendPending(string path, string sender, string body)
        {
            try
            {
                File.AppendAllText(path,
                    JsonConvert.SerializeObject(new { s = sender ?? "", b = body, t = Now() }) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        private static List<KeyValuePair<string, string>> ReadPending(string path)
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                if (!File.Exists(path)) return list;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var o = JsonConvert.DeserializeObject<Dictionary<string, object>>(line);
                        if (o == null) continue;
                        object s, b;
                        o.TryGetValue("s", out s);
                        o.TryGetValue("b", out b);
                        var body = b?.ToString();
                        if (string.IsNullOrEmpty(body)) continue;
                        list.Add(new KeyValuePair<string, string>(s?.ToString() ?? "", body));
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static void PruneRecent()
        {
            try
            {
                if (recent.Count < 64) return;
                var now = Now();
                var dead = new List<string>();
                foreach (var kv in recent)
                    if (now - kv.Value > DedupWindowMs) dead.Add(kv.Key);
                foreach (var k in dead) recent.Remove(k);
            }
            catch { }
        }

        private static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}

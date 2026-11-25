using System;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;

namespace AhoChatA
{
    /// <summary>
    /// 短信收件箱监听（广播之外的第二条路）。
    ///
    /// 为什么需要它：部分 ROM（MIUI / ColorOS 等）会把 SMS_RECEIVED 广播限制给
    /// 「默认短信应用」或需要在系统里额外允许「通知类短信」，导致普通应用收不到广播。
    /// 这里改用内容观察者盯 content://sms/inbox，只要有新短信入库就能发现；
    /// 与广播同时命中时由 SmsForwarder 内部去重，不会重复转发。
    ///
    /// 需要 READ_SMS 权限；未授权时 EnsureStarted 返回 false，菜单里会提示去授权。
    /// </summary>
    public sealed class SmsInboxWatcher : Java.Lang.Object
    {
        private static SmsInboxWatcher current;
        private static readonly object gate = new object();

        private Context appCtx;
        private ContentResolver resolver;
        private Observer observer;
        private long lastId;

        /// <summary>收件箱监听是否已在运行。</summary>
        public static bool Running { get { return current != null; } }

        /// <summary>是否已拿到 READ_SMS。</summary>
        public static bool HasReadSms(Context ctx)
        {
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.M) return true;
                return ctx.CheckSelfPermission("android.permission.READ_SMS")
                       == Android.Content.PM.Permission.Granted;
            }
            catch { return false; }
        }

        /// <summary>幂等启动；没有 READ_SMS 时返回 false（由界面引导授权）。</summary>
        public static bool EnsureStarted(Context ctx)
        {
            try
            {
                if (ctx == null || !HasReadSms(ctx)) return false;
                lock (gate)
                {
                    if (current != null) return true;
                    var w = new SmsInboxWatcher();
                    w.appCtx = ctx.ApplicationContext ?? ctx;
                    w.resolver = w.appCtx.ContentResolver;
                    if (w.resolver == null) return false;
                    w.lastId = w.MaxId();          // 只关注「从此刻起的新短信」，不回放历史
                    w.observer = new Observer(w);
                    w.resolver.RegisterContentObserver(Android.Provider.Telephony.Sms.Inbox.ContentUri, true, w.observer);
                    current = w;
                    Android.Util.Log.Info("SmsForward", "收件箱监听已启动，起始 _id=" + w.lastId);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("SmsForward", "启动收件箱监听失败: " + ex.Message);
                return false;
            }
        }

        public static void Stop()
        {
            lock (gate)
            {
                var w = current;
                current = null;
                if (w == null) return;
                try { if (w.resolver != null && w.observer != null) w.resolver.UnregisterContentObserver(w.observer); } catch { }
                try { w.observer?.Dispose(); } catch { }
                try { w.Dispose(); } catch { }
            }
        }

        /* ===================== 内部实现 ===================== */

        private long MaxId()
        {
            long max = 0;
            ICursor c = null;
            try
            {
                c = resolver.Query(Telephony.Sms.Inbox.ContentUri,
                    new[] { "_id" }, null, null, "_id DESC");
                if (c != null && c.MoveToFirst()) max = c.GetLong(0);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("SmsForward", "读取收件箱最大 _id 失败: " + ex.Message);
            }
            finally { try { c?.Close(); } catch { } }
            return max;
        }

        /// <summary>拉取比 lastId 更新的短信并交给 SmsForwarder。</summary>
        private void Poll()
        {
            ICursor c = null;
            try
            {
                c = resolver.Query(Telephony.Sms.Inbox.ContentUri,
                    new[] { "_id", "address", "body" },
                    "_id > ?", new[] { lastId.ToString() }, "_id ASC");
                if (c == null) return;

                int n = 0;
                while (c.MoveToNext() && n < 10)
                {
                    long id = 0;
                    try { id = c.GetLong(0); } catch { }
                    string addr = null, body = null;
                    try { addr = c.GetString(1); } catch { }
                    try { body = c.GetString(2); } catch { }
                    if (id > lastId) lastId = id;
                    if (string.IsNullOrWhiteSpace(body)) continue;

                    Android.Util.Log.Info("SmsForward", "收件箱发现新短信 #" + id + " 来自 " + (addr ?? "?"));
                    SmsForwarder.Handle(appCtx, addr, body, "inbox");
                    n++;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("SmsForward", "读收件箱失败: " + ex.Message);
            }
            finally { try { c?.Close(); } catch { } }
        }

        private sealed class Observer : ContentObserver
        {
            private readonly SmsInboxWatcher owner;

            public Observer(SmsInboxWatcher o) : base(new Handler(Looper.MainLooper))
            {
                owner = o;
            }

            public override void OnChange(bool selfChange)
            {
                base.OnChange(selfChange);
                try { owner?.Poll(); } catch { }
            }

            public override void OnChange(bool selfChange, Android.Net.Uri uri)
            {
                base.OnChange(selfChange, uri);
                try { owner?.Poll(); } catch { }
            }
        }
    }
}

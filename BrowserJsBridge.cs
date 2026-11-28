using System;
using System.Collections.Generic;
using Android.App;
using Android.Webkit;
using Java.Interop;

namespace AhoChatA
{
    /// <summary>
    /// 内置浏览器注入到网页的 JS 桥（对象名 MchatJs）。
    /// 承载：广告元素标记、油猴脚本 GM_* API、blob 下载等原生能力。
    /// 只通过字符串与 JS 通信；所有 UI 操作回到主线程。
    /// </summary>
    public sealed class BrowserJsBridge : Java.Lang.Object
    {
        private readonly WeakReference<BrowserActivity> _ownerRef;
        private readonly WebView _web;

        public BrowserJsBridge(BrowserActivity owner, WebView web)
        {
            _ownerRef = new WeakReference<BrowserActivity>(owner);
            _web = web;
        }

        private bool TryGetOwner(out BrowserActivity owner)
        {
            return _ownerRef.TryGetTarget(out owner) && owner != null && !owner.IsActivityDestroyed();
        }

        /* ---------------- 广告元素标记 ---------------- */

        [Export("pickAdElement")]
        [JavascriptInterface]
        public void PickAdElement(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return;
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.OnAdElementPicked(_web, selector));
        }

        [Export("cancelPickAd")]
        [JavascriptInterface]
        public void CancelPickAd()
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.ToastShort(L10n.T("已取消标记")));
        }

        /// <summary>长按选中网页文本后，页面回传文本/元素选择器/URL，由原生弹出屏蔽规则菜单。</summary>
        [Export("adBlockPicked")]
        [JavascriptInterface]
        public void AdBlockPicked(string text, string selector, string url)
        {
            // 兼容旧注入脚本：统一并到同一个长按菜单入口，避免弹出第二个窗口。
            ContentPicked(text, selector, url, "", false);
        }

        /// <summary>
        /// 统一的长按内容回调：一次回传触点处的链接/图片地址、文字、元素选择器。
        /// 原生据此只弹一个菜单（标签页操作 → 下载/复制/分享 → 文字操作 → 屏蔽元素）。
        /// </summary>
        [Export("contentPicked")]
        [JavascriptInterface]
        public void ContentPicked(string text, string selector, string pageUrl, string linkUrl, bool isImage)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.OnContentPicked(text, selector, pageUrl, linkUrl, isImage));
        }

        /* ---------------- 网页长截图 ---------------- */

        /// <summary>长截图：JS 把 html2canvas 产出的 PNG dataURL(base64) 回传，原生落盘并弹保存/分享。</summary>
        [Export("longShotReady")]
        [JavascriptInterface]
        public void LongShotReady(string base64Data)
        {
            if (string.IsNullOrWhiteSpace(base64Data)) return;
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.OnLongShotReady(base64Data));
        }

        /* ---------------- 网页翻译 ---------------- */

        /// <summary>网页翻译：JS 传待译文本/目标语言/回调 id，原生翻译后回调 window.__mchatTranslateCb。</summary>
        [Export("translate")]
        [JavascriptInterface]
        public void Translate(string text, string targetLang, string callbackId)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                string result;
                try { result = await TranslateEngine.Instance.TranslateAsync(text, targetLang).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("BrowserJsBridge", "translate 失败: " + ex.Message);
                    result = text ?? "";
                }
                string js = "(function(){try{if(window.__mchatTranslateCb)window.__mchatTranslateCb("
                          + Json(callbackId) + "," + Json(result) + ");}catch(e){}})()";
                // 与 gmXhr 同理：翻译在后台线程完成，但 evaluateJavascript 必须在 UI 线程执行，
                // 否则 CalledFromWrongThreadException 会被吞掉，页面上的回调永远不触发。
                if (!TryGetOwner(out var owner)) return;
                owner.RunOnUiThread(() =>
                {
                    try { _web.EvaluateJavascript(js, null); }
                    catch (Exception ex) { Android.Util.Log.Warn("BrowserJsBridge", "translate eval: " + ex.Message); }
                });
            });
        }

        /// <summary>读取/设置当前翻译目标语言代码（如 zh-CN）。</summary>
        [Export("getTranslateLang")]
        [JavascriptInterface]
        public string GetTranslateLang()
        {
            return TranslateEngine.Instance.TargetLang;
        }

        private static string Json(string s)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(s ?? "");
        }

        /* ---------------- 油猴脚本 GM_* API（后续批次实现逻辑，接口先就位） ---------------- */

        [Export("gmLog")]
        [JavascriptInterface]
        public void GmLog(string message)
        {
            Android.Util.Log.Debug("Userscript", message ?? "");
        }

        [Export("gmToast")]
        [JavascriptInterface]
        public void GmToast(string message)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.ToastShort(message ?? ""));
        }

        [Export("gmSetValue")]
        [JavascriptInterface]
        public void GmSetValue(string scriptId, string key, string value)
        {
            UserScriptManager.Instance.SetValue(scriptId, key, value);
        }

        [Export("gmGetValue")]
        [JavascriptInterface]
        public string GmGetValue(string scriptId, string key)
        {
            return UserScriptManager.Instance.GetValue(scriptId, key);
        }

        [Export("gmDeleteValue")]
        [JavascriptInterface]
        public void GmDeleteValue(string scriptId, string key)
        {
            UserScriptManager.Instance.DeleteValue(scriptId, key);
        }

        [Export("gmListValues")]
        [JavascriptInterface]
        public string GmListValues(string scriptId)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(UserScriptManager.Instance.ListValues(scriptId));
        }

        /// <summary>GM_xmlhttpRequest：原生异步请求，完成后回调页面里的 window.__mchat_gmXhrCallback。</summary>
        [Export("gmXhr")]
        [JavascriptInterface]
        public void GmXhr(string scriptId, string callbackId, string requestJson)
        {
            UserScriptManager.Instance.StartXhr(scriptId, callbackId, requestJson, js =>
            {
                // StartXhr 在后台线程完成：WebView.EvaluateJavascript 必须在 UI 线程调用，
                // 否则 Android 抛 CalledFromWrongThreadException 被静默吞掉，脚本回调永远不来。
                if (!TryGetOwner(out var owner)) return;
                owner.RunOnUiThread(() =>
                {
                    try { _web.EvaluateJavascript(js, null); }
                    catch (Exception ex) { Android.Util.Log.Warn("Userscript", "gmXhr eval: " + ex.Message); }
                });
            });
        }

        /// <summary>GM_download / blob 下载：把 base64 数据交给原生下载器落盘。</summary>
        [Export("gmDownload")]
        [JavascriptInterface]
        public void GmDownload(string base64Data, string fileName, string mimeType)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.SaveBlobDownload(base64Data, fileName, mimeType));
        }

        [Export("gmOpenTab")]
        [JavascriptInterface]
        public void GmOpenTab(string url)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.OpenTabFromScript(url));
        }

        [Export("gmCopy")]
        [JavascriptInterface]
        public void GmCopy(string text)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.CopyTextFromScript(text));
        }

        /// <summary>油猴脚本注册菜单命令后，页面把命令名列表回传给原生。</summary>
        [Export("gmMenuResult")]
        [JavascriptInterface]
        public void GmMenuResult(string json)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.ShowScriptCommandMenu(_web, json));
        }

        /// <summary>油猴脚本下载普通 http(s) 文件。</summary>
        [Export("gmDownloadUrl")]
        [JavascriptInterface]
        public void GmDownloadUrl(string url, string fileName)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.DownloadUrlFromScript(url, fileName));
        }

        /// <summary>油猴脚本 / 页面请求用内置 pdf.js 打开 PDF 链接。</summary>
        [Export("openPdfViewer")]
        [JavascriptInterface]
        public void OpenPdfViewer(string url, string name)
        {
            if (TryGetOwner(out var owner))
                owner.RunOnUiThread(() => owner.OpenPdfInViewer(url, name));
        }
    }
}

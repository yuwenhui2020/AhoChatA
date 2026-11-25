using System;
using System.IO;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 「阅读」模块的 Office / 文档查看器：
    ///   ppt / pptx / doc / docx / xls / xlsx / pdf / txt 统一在此阅读，
    ///   用 WebView 加载 Assets/viewer 里的前端页面，由 JS 库直接解析文档
    ///   （docx-preview / SheetJS / PPTXjs / prose.js 自研 doc 阅读器），不依赖 Apache POI / 在线转换。
    ///
    /// 供流：拦截 https://mchat.doc/ 下的请求：
    ///   /assets/viewer/*  → APK Assets 里的 html/js/css/字体
    ///   /doc/file.ext     → 本次要查看的文档字节（已复制到私有目录）
    /// 用虚拟 https 域名而非 file://，规避 WebView 对 file 下 fetch 的 CORS 限制。
    /// 同时重写新旧两个 ShouldInterceptRequest 重载，兼容所有 WebView 版本（旧版只重写 string 会白屏）。
    /// </summary>
    [Activity(Label = "@string/act_reader", Exported = true,
        Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                               | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataSchemes = new[] { "content", "file" },
        DataMimeTypes = new[] {
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "application/msword",
            "application/vnd.ms-excel",
            "application/vnd.ms-powerpoint",
            "application/pdf", "text/plain", "application/octet-stream"
        })]
    // ACTION_SEND 统一由 ShareRouterActivity 分发（提供"阅读/发到聊天"选择），
    // 本 Activity 仍可通过显式 Intent 或 ACTION_VIEW 的文档类型直接打开。
    public class OfficeViewerActivity : BaseActivity
    {
        public const string ExtraPath = "docPath";
        public const string ExtraName = "docName";

        // 用 AndroidX WebKit 生态约定的 assets 域名（gander 实测可用）；.doc 不是合法 TLD 部分 ROM 会拒解析
        private const string Host = "appassets.androidplatform.net";
        private WebView _web;
        private string _docPath;
        private string _ext;
        private string _displayName;
        private ProgressBar _progress;

        // 阅读模块管理的全部格式（含新旧 Office + 文本 + PDF）
        public static readonly string[] SupportedExt = {
            "ppt", "pptx", "doc", "docx", "xls", "xlsx", "pdf", "txt", "text",
            "pps", "ppsx", "pptm", "docm", "dotx", "xlsm", "xlsb", "xltx", "csv"
        };

        public static bool IsSupported(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.TrimStart('.').ToLowerInvariant();
            foreach (var e in SupportedExt) if (e == ext) return true;
            return false;
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            string path = Intent?.GetStringExtra(ExtraPath);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _docPath = path;
                _displayName = Intent.GetStringExtra(ExtraName) ?? Path.GetFileName(path);
                _ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
            }
            else if (!ResolveIncomingIntent(Intent))
            {
                L10n.Toast(this, L10n.T("无法读取该文档"), ToastLength.Long).Show();
                Finish();
                return;
            }

            // 误选到"阅读"的 apk / apk.1 等安装包：直接转交系统安装程序，
            // 不再当 docx 渲染（此前 DetectExt 会把未知扩展名默认成 docx）。
            if (ApkInstallActivity.RedirectLocalFileToInstaller(this, _docPath, _displayName))
            {
                Finish();
                return;
            }

            SetContentView(Resource.Layout.office_viewer);
            _progress = FindViewById<ProgressBar>(Resource.Id.officeProgress);
            _web = FindViewById<WebView>(Resource.Id.officeWeb);
            ConfigureWebView();

            // 旧版二进制 PPT：gander 前端无解析器，走文本提取 preview（能读文字，排版尽力而为）
            if (_ext == "ppt" || _ext == "pps" || _ext == "pot")
            {
                if (OldPptReader.TryExtract(_docPath, out string pptHtml))
                {
                    _web.LoadDataWithBaseURL("https://" + Host + "/", pptHtml, "text/html", "utf-8", null);
                    if (_progress != null) _progress.Visibility = ViewStates.Gone;
                    return;
                }
            }

            string page = PageOf(_ext);
            string name = System.Uri.EscapeDataString(_displayName ?? "document." + _ext);
            string url = "https://" + Host + "/assets/viewer/" + page
                       + "?name=" + name + "&ext=" + System.Uri.EscapeDataString(_ext);
            // 注：WebView 拦截路径拿不到 Range 请求头，无法回 206，故原生路径不启用 ranged；
            // 文档字节由 Intercept 用 FileStream 流式提供（非整本读内存）。
            // webui/App 内嵌路径走 WebServer /doc，那里已支持 Range 并可带 ranged=1。
            Android.Util.Log.Info("Mchat", "OfficeViewer 加载：" + url + " ext=" + _ext);
            _web.LoadUrl(url);
        }

        /// <summary>供外部（阅读/聊天/Web）调用的静态打开入口。</summary>
        public static void Open(Context ctx, string path, string name)
        {
            try
            {
                var i = new Intent(ctx, typeof(OfficeViewerActivity));
                i.PutExtra(ExtraPath, path);
                i.PutExtra(ExtraName, name ?? Path.GetFileName(path));
                i.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(i);
            }
            catch (Exception ex)
            {
                L10n.Toast(ctx, L10n.T("打开文档失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private bool ResolveIncomingIntent(Intent intent)
        {
            if (intent == null) return false;
            Android.Net.Uri uri = null;
            if (Intent.ActionSend.Equals(intent.Action))
                uri = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
            if (uri == null) uri = intent.Data;
            if (uri == null) return false;

            try
            {
                _displayName = QueryDisplayName(uri) ?? "document";
                _ext = DetectExt(_displayName, intent.Type, uri);

                string dir = Path.Combine(FilesDir.AbsolutePath, "office-view");
                Directory.CreateDirectory(dir);
                _docPath = Path.Combine(dir, "current." + _ext);

                using (var src = ContentResolver.OpenInputStream(uri))
                {
                    if (src == null) return false;
                    using (var dst = new FileStream(_docPath, FileMode.Create, FileAccess.Write))
                        src.CopyTo(dst, 64 * 1024);
                }
                return new FileInfo(_docPath).Length > 0;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("Mchat", "OfficeViewer 读取文档失败：" + ex.Message);
                return false;
            }
        }

        private string QueryDisplayName(Android.Net.Uri uri)
        {
            try
            {
                string[] proj = { Android.Provider.OpenableColumns.DisplayName };
                using (var c = ContentResolver.Query(uri, proj, null, null, null))
                {
                    if (c != null && c.MoveToFirst())
                    {
                        int idx = c.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                        if (idx >= 0) return c.GetString(idx);
                    }
                }
            }
            catch { }
            string last = uri.LastPathSegment;
            if (!string.IsNullOrEmpty(last) && last.IndexOf('.') > 0) return last;
            return null;
        }

        private string DetectExt(string name, string mime, Android.Net.Uri uri)
        {
            if (!string.IsNullOrEmpty(name))
            {
                int dot = name.LastIndexOf('.');
                if (dot >= 0)
                {
                    string e = name.Substring(dot + 1).ToLowerInvariant();
                    if (IsSupported(e)) return Normalize(e);
                }
            }
            if (!string.IsNullOrEmpty(mime))
            {
                string m = mime.ToLowerInvariant();
                if (m.Contains("wordprocessingml") || m.Contains("msword")) return "docx";
                if (m.Contains("spreadsheetml") || m.Contains("ms-excel")) return "xlsx";
                if (m.Contains("presentationml") || m.Contains("ms-powerpoint")) return "pptx";
                if (m.Contains("pdf")) return "pdf";
                if (m.Contains("text/plain")) return "txt";
            }
            try
            {
                string p = (uri.Path ?? "").ToLowerInvariant();
                foreach (var e in SupportedExt) if (p.EndsWith("." + e)) return Normalize(e);
            }
            catch { }
            return "docx";
        }

        private static string Normalize(string e)
        {
            if (e == "docm" || e == "dotx") return "docx";
            if (e == "xlsm" || e == "xlsb" || e == "xltx" || e == "csv") return "xlsx";
            if (e == "pptm" || e == "ppsx" || e == "pps" || e == "potx") return "pptx";
            if (e == "text") return "txt";
            return e;
        }

        /// <summary>扩展名 → viewer 页面。doc 走 prose（二进制 Word 阅读器），xls 走 SheetJS，ppt 走提示页。</summary>
        private static string PageOf(string ext)
        {
            switch (ext)
            {
                case "xlsx": case "xls": return "xlsx.html";
                case "pptx": return "pptx.html";
                case "doc": case "rtf": case "odt": return "prose.html";
                case "pdf": return "pdf.html";
                case "txt": case "text": return "text.html";
                default:
                    // ppt/pps/pot 旧二进制格式：gander 也无解析器，用提示页（可尝试按文本打开）
                    if (ext == "ppt" || ext == "pps" || ext == "pot") return "unsupported.html";
                    return "docx.html";
            }
        }

        private void ConfigureWebView()
        {
            _web.Settings.JavaScriptEnabled = true;
            _web.Settings.DomStorageEnabled = true;
            _web.Settings.AllowFileAccess = true;
            _web.Settings.BuiltInZoomControls = true;
            _web.Settings.DisplayZoomControls = false;
            _web.Settings.UseWideViewPort = true;
            _web.Settings.LoadWithOverviewMode = true;
            _web.Settings.CacheMode = CacheModes.NoCache;
            _web.Settings.SetAppCacheEnabled(false);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                _web.Settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.JellyBeanMr2)
            {
                try { _web.Settings.UserAgentString = _web.Settings.UserAgentString + " MchatReader/1.0"; } catch { }
            }

            _web.SetWebViewClient(new DocClient(this));
            _web.SetWebChromeClient(new DocChrome());
            _web.LongClickable = false;
        }

        /// <summary>拦截虚拟域名请求。同时提供新旧两个重载——现代 WebView 只调 IWebResourceRequest 版本。</summary>
        private class DocClient : WebViewClient
        {
            private readonly OfficeViewerActivity _act;
            public DocClient(OfficeViewerActivity act) { _act = act; }

            // 新重载（API 21+，现代 WebView 实际走这里）
            public override WebResourceResponse ShouldInterceptRequest(WebView view, IWebResourceRequest request)
            {
                var u = request?.Url;
                return u == null ? null : _act.Intercept(u.ToString());
            }

            // 旧重载（兼容低版本 / 部分 ROM）
            public override WebResourceResponse ShouldInterceptRequest(WebView view, string url)
            {
                return _act.Intercept(url);
            }

            public override void OnPageFinished(WebView view, string url)
            {
                base.OnPageFinished(view, url);
                Android.Util.Log.Info("Mchat", "OfficeViewer 页面完成：" + url + " title=" + view.Title);
                if (_act._progress != null) _act._progress.Visibility = ViewStates.Gone;
            }

            public override void OnReceivedError(WebView view, [GeneratedEnum] ClientError errorCode, string description, string failingUrl)
            {
                base.OnReceivedError(view, errorCode, description, failingUrl);
                Android.Util.Log.Warn("Mchat", "OfficeViewer 页面错误 " + errorCode + " " + description + " " + failingUrl);
            }

            public override bool ShouldOverrideUrlLoading(WebView view, string url)
            {
                if (url != null && (url.Contains("://" + Host) || url.StartsWith("data:") || url.StartsWith("blob:")))
                    return false;
                try { _act.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url))); }
                catch { }
                return true;
            }
        }

        /// <summary>把前端 JS 的 console/error 打到 logcat，便于诊断渲染失败。</summary>
        private class DocChrome : WebChromeClient
        {
            public override void OnConsoleMessage(string message, int lineNumber, string sourceID)
            {
                Android.Util.Log.Info("MchatJS", sourceID + ":" + lineNumber + " " + message);
                base.OnConsoleMessage(message, lineNumber, sourceID);
            }
        }

        private WebResourceResponse Intercept(string url)
        {
            try
            {
                var u = new System.Uri(url);
                if (!Host.Equals(u.Host, StringComparison.OrdinalIgnoreCase)) return null;
                string path = u.AbsolutePath;

                if (path.StartsWith("/assets/", StringComparison.Ordinal))
                {
                    string asset = path.Substring("/assets/".Length).TrimStart('/');
                    if (string.IsNullOrEmpty(asset)) return null;
                    try
                    {
                        var stream = Application.Context.Assets.Open(asset);
                        // 关键：html/js/css 必须声明 utf-8，否则中文文档乱码（字体"读不出来"的根因之一）
                        Android.Util.Log.Info("Mchat", "OfficeViewer 拦截 assets：" + asset);
                        if (asset.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        {
                            using (stream)
                            using (var r = new StreamReader(stream, Encoding.UTF8))
                            {
                                var injected = ReaderFonts.Inject(r.ReadToEnd());
                                var ms = new MemoryStream(Encoding.UTF8.GetBytes(injected));
                                return new WebResourceResponse(MimeOf(asset), "utf-8", ms);
                            }
                        }
                        return new WebResourceResponse(MimeOf(asset), "utf-8", stream);
                    }
                    catch (Exception aex)
                    {
                        Android.Util.Log.Warn("Mchat", "OfficeViewer 无此 assets：" + asset + " " + aex.Message);
                        return null;
                    }
                }

                if (path.StartsWith("/doc/", StringComparison.Ordinal))
                {
                    Android.Util.Log.Info("Mchat", "OfficeViewer 拦截文档：" + path + " -> " + _docPath);
                    if (!File.Exists(_docPath)) return null;
                    var stream = new FileStream(_docPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new WebResourceResponse("application/octet-stream", null, stream);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "OfficeViewer 拦截失败 " + url + "：" + ex.Message);
            }
            return null;
        }

        private static string MimeOf(string asset)
        {
            string low = asset.ToLowerInvariant();
            if (low.EndsWith(".html") || low.EndsWith(".htm")) return "text/html";
            if (low.EndsWith(".js") || low.EndsWith(".mjs")) return "application/javascript";
            if (low.EndsWith(".css")) return "text/css";
            if (low.EndsWith(".wasm")) return "application/wasm";
            if (low.EndsWith(".svg")) return "image/svg+xml";
            if (low.EndsWith(".png")) return "image/png";
            if (low.EndsWith(".json")) return "application/json";
            if (low.EndsWith(".bcmap")) return "application/octet-stream";
            return "application/octet-stream";
        }

        public override void OnBackPressed()
        {
            if (_web != null && _web.CanGoBack()) _web.GoBack();
            else base.OnBackPressed();
        }

        protected override void OnDestroy()
        {
            try
            {
                if (_web != null) { _web.StopLoading(); _web.Destroy(); _web = null; }
            }
            catch { }
            base.OnDestroy();
        }
    }
}

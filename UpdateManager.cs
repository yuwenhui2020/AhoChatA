// ============================================================
// 程序更新管理器
//
// 职责：
//   · 从 gh-proxy 加速的 GitHub 仓库读取 version.md / release.md；
//   · 按"分段数值比较"判断远端版本是否新于本机内嵌版本；
//   · 有新版本时下载 APK 到应用私有目录，并提供一键安装；
//   · 在 mesh 频道发一张带安装按钮的更新卡片（结构化数据放 text 里的 JSON）；
//   · 一周自动检测一次（SharedPreferences 时间戳），手动检测不受此限制。
//
// 设计约束（实测）：
//   · 用户给的 .../blob/main/version.md 经 gh-proxy 返回的是 GitHub 的 HTML 页面，
//     且目前仓库尚未推送这两个文件（线上 404）。因此读取时同时尝试 blob 与 raw
//     两种地址，并对 HTML 做纯文本兜底解析；拿不到就静默跳过，绝不打扰用户。
//   · 版本比较规则（用户定义）：按 '.' 分段逐段数值比较，缺失段视为 -1（比 0 小），
//     故 1.0 < 1.0.0 < 1.0.1 < 1.1 < 1.9 < 1.10 < 1.10.0。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AhoChatA.Vod;

namespace AhoChatA
{
    public static class UpdateManager
    {
        private const string Tag = "MchatUpdate";

        // gh-proxy 加速前缀 + 仓库原始地址
        private const string Proxy = "https://gh-proxy.org/";
        private const string RepoBlob = "https://github.com/yuwenhui2020/AhoChatA/blob/main/";
        private const string RepoRaw = "https://github.com/yuwenhui2020/AhoChatA/raw/main/";
        private const string ReleaseDownload = "https://github.com/yuwenhui2020/AhoChatA/releases/download/";
        private const string ApkAsset = "com.Ahochat.android.apk";
        private const string CardSchema = "mchat.update.v1";

        // 一周一次的自动检测时间戳
        private const string Pref = "Mchat";
        private const string KeyLastCheck = "update_last_check_ticks";
        private static readonly TimeSpan AutoInterval = TimeSpan.FromDays(7);

        // 防止并发点击/自动与手动同时触发
        private static readonly object Gate = new object();
        private static bool _running;

        private static Context Ctx
        {
            get { try { return Android.App.Application.Context; } catch { return null; } }
        }

        /// <summary>本机内嵌版本：优先取清单 versionName，失败回退到常量。</summary>
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var ctx = Ctx;
                    if (ctx != null)
                    {
                        var pi = ctx.PackageManager.GetPackageInfo(ctx.PackageName, 0);
                        if (!string.IsNullOrEmpty(pi.VersionName)) return pi.VersionName.Trim();
                    }
                }
                catch { }
                return "1.0.3";
            }
        }

        /// <summary>gh-proxy 加速的当前版本安装链接（关于页「分享本程序」复用）。</summary>
        public static string InstallUrl
        {
            get { return Proxy + ReleaseDownload + CurrentVersion + "/" + ApkAsset; }
        }

        /// <summary>已下载的最新 APK 路径（供卡片安装按钮使用）。</summary>
        private static string UpdateDir
        {
            get
            {
                var ctx = Ctx;
                var root = ctx != null ? ctx.FilesDir.AbsolutePath : System.IO.Path.GetTempPath();
                return System.IO.Path.Combine(root, "update");
            }
        }

        private static string ApkPath(string version)
        {
            var safe = Regex.Replace(version ?? "", @"[^\w.\-]", "_");
            return System.IO.Path.Combine(UpdateDir, safe + ".apk");
        }

        // ------------------------------------------------------------
        // 版本比较
        // ------------------------------------------------------------

        /// <summary>
        /// 比较 a 与 b：a 比 b 新返回正数，旧返回负数，相等返回 0。
        /// 分段按数值比较；缺失段视为 -1（比 0 小），满足"空 < 0 < ... < 10"。
        /// </summary>
        public static int CompareVersion(string a, string b)
        {
            var sa = SplitVersion(a);
            var sb = SplitVersion(b);
            var n = Math.Max(sa.Length, sb.Length);
            for (int i = 0; i < n; i++)
            {
                int av = i < sa.Length ? sa[i] : -1;
                int bv = i < sb.Length ? sb[i] : -1;
                if (av != bv) return av > bv ? 1 : -1;
            }
            return 0;
        }

        private static int[] SplitVersion(string v)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(v)) return result.ToArray();
            // 只取第一段连续的数字点分序列，忽略 "v1.0" 前缀与 "-beta" 后缀
            var m = Regex.Match(v.Trim(), @"(\d+(?:\.\d+)*)");
            if (!m.Success) return result.ToArray();
            foreach (var part in m.Groups[1].Value.Split('.'))
            {
                int x;
                if (int.TryParse(part, out x)) result.Add(x);
                else result.Add(0);
            }
            return result.ToArray();
        }

        // ------------------------------------------------------------
        // 远端文本读取（blob HTML 兜底）
        // ------------------------------------------------------------

        /// <summary>
        /// 读取 version.md / release.md。先试 gh-proxy + blob，若是 HTML 再试 raw，
        /// 全部失败返回 null（静默，不打扰用户）。
        /// </summary>
        private static string FetchText(string fileName)
        {
            var candidates = new[]
            {
                Proxy + RepoBlob + fileName,
                Proxy + RepoRaw + fileName,
                RepoRaw + fileName,
            };
            foreach (var url in candidates)
            {
                var text = VodApi.HttpGetText(url, 9000);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var cleaned = CleanRemoteText(text);
                if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
            }
            return null;
        }

        /// <summary>把 GitHub blob 页的 HTML / markdown 清洗成纯文本版本号或说明。</summary>
        private static string CleanRemoteText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            // HTML 页面：GitHub blob 会把文件内容放进 <table class="blob-code"> 的 <td> 中。
            // 用正则把所有 td 文本拼起来，再去掉标签与实体。
            if (s.StartsWith("<", StringComparison.Ordinal) || s.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var sb = new StringBuilder();
                foreach (Match td in Regex.Matches(s, @"<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var t = Regex.Replace(td.Groups[1].Value, @"<[^>]+>", "");
                    t = System.Net.WebUtility.HtmlDecode(t);
                    if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t.Trim());
                }
                s = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(s)) return null;
            }

            // markdown 代码围栏 / 标题符号
            s = Regex.Replace(s, @"^```[a-zA-Z]*\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"```\s*$", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*#+\s*", "", RegexOptions.Multiline);
            return s.Trim();
        }

        /// <summary>从文本里抽出第一处版本号（按点分数字），没有就是 null。</summary>
        private static string FirstVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, @"(\d+(?:\.\d+){0,5})");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ------------------------------------------------------------
        // 自动检测调度
        // ------------------------------------------------------------

        private static bool ShouldAutoCheck()
        {
            try
            {
                var prefs = Ctx.GetSharedPreferences(Pref, FileCreationMode.Private);
                long last = prefs.GetLong(KeyLastCheck, 0);
                if (last <= 0) return true;
                var elapsed = DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);
                return elapsed >= AutoInterval;
            }
            catch { return false; }
        }

        private static void MarkChecked()
        {
            try
            {
                Ctx.GetSharedPreferences(Pref, FileCreationMode.Private)
                   .Edit()
                   .PutLong(KeyLastCheck, DateTime.UtcNow.Ticks)
                   .Apply();
            }
            catch { }
        }

        /// <summary>App 启动后调用：满足"一周一次"时后台检测。手动检测请调 CheckAsync(true)。</summary>
        public static void ScheduleAutoCheck()
        {
            try
            {
                if (!ShouldAutoCheck()) return;
                // 延时几秒，避开冷启动的高峰，避免拖慢首屏
                new Handler(Looper.MainLooper).PostDelayed(() =>
                {
                    _ = CheckAsync(false);
                }, 4000);
            }
            catch { }
        }

        // ------------------------------------------------------------
        // 主流程
        // ------------------------------------------------------------

        /// <summary>
        /// 检测更新。force=true 表示手动检测（不受一周一次限制，且无论有没有新版本都给反馈）。
        /// </summary>
        public static async Task CheckAsync(bool force)
        {
            lock (Gate)
            {
                if (_running) return;
                _running = true;
            }
            try
            {
                var current = CurrentVersion;
                var versionText = FetchText("version.md");
                if (string.IsNullOrWhiteSpace(versionText))
                {
                    if (force) Toast("暂时无法连接更新服务器，请稍后再试");
                    return;
                }
                var remote = FirstVersion(versionText);
                if (string.IsNullOrEmpty(remote))
                {
                    if (force) Toast("更新服务器返回的内容无法识别");
                    return;
                }

                if (CompareVersion(remote, current) <= 0)
                {
                    MarkChecked();
                    if (force) Toast("已是最新版本（" + current + "）");
                    return;
                }

                // 确有新版本：现在才去取更新说明（避免无谓请求）
                var notes = FetchText("release.md");
                if (string.IsNullOrWhiteSpace(notes)) notes = L10n.T("新版已发布，详见项目主页。", "A new version is available. See the project home for details.");
                if (notes.Length > 400) notes = notes.Substring(0, 400) + "…";

                MarkChecked();

                // 下载 APK（后台），完成后发 mesh 卡片并弹窗
                var apk = await DownloadApkAsync(remote);
                var status = !string.IsNullOrEmpty(apk)
                    ? L10n.T("已下载完成，可点击安装。", "Downloaded. Tap to install.")
                    : L10n.T("自动下载失败，可打开项目主页手动下载。", "Auto-download failed. Download manually from the project home.");

                PublishUpdateCard(remote, notes, status, apk);
                ShowUpdateDialog(remote, notes, status, apk);
            }
            catch (Exception ex)
            {
                try { Android.Util.Log.Warn(Tag, "检查更新失败: " + ex.Message); } catch { }
                if (force) Toast(L10n.T("检查更新失败：", "Update check failed: ") + ex.Message);
            }
            finally
            {
                lock (Gate) { _running = false; }
            }
        }

        private static async Task<string> DownloadApkAsync(string version)
        {
            try
            {
                var dir = UpdateDir;
                Directory.CreateDirectory(dir);
                var path = ApkPath(version);
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) return path;

                var url = Proxy + ReleaseDownload + version + "/" + ApkAsset;
                var opts = new AhoChatA.Downloads.HttpDownloadOptions
                {
                    Url = url,
                    UserAgent = VodApi.UserAgent,
                };
                using (var job = new AhoChatA.Downloads.HttpDownloadJob(opts))
                {
                    // title 必须明确以 .apk 结尾：DownloadAsync 取 title 的扩展名落地，
                    // 若写成 "...1.1" 会被当成扩展名 .1，产出无 .apk 后缀的文件导致安装失败。
                    var title = "com.Ahochat.android.apk";
                    var produced = await job.DownloadAsync(dir, title, null, System.Threading.CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(produced) && File.Exists(produced))
                    {
                        if (!string.Equals(produced, path, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Copy(produced, path, true); } catch { return produced; }
                        }
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                try { Android.Util.Log.Warn(Tag, "下载 APK 失败: " + ex.Message); } catch { }
            }
            return null;
        }

        // ------------------------------------------------------------
        // mesh 频道卡片
        // ------------------------------------------------------------

        private static void PublishUpdateCard(string version, string notes, string status, string apkPath)
        {
            try
            {
                var card = new Dictionary<string, object>
                {
                    { "schema", CardSchema },
                    { "version", version },
                    { "title", L10n.T("发现新版本", "New version available") },
                    { "notes", notes ?? "" },
                    { "status", status ?? "" },
                    { "apk", string.IsNullOrEmpty(apkPath) ? "" : Path.GetFileName(apkPath) },
                    { "home", "https://github.com/yuwenhui2020/AhoChatA" },
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(card);
                var conv = Convs.Chan(ChannelRegistry.DefaultChannelName);
                var bridge = CoreHost.Bridge ?? MchatBridge.Current;
                bridge?.SendInConv(conv, json, "update");
            }
            catch (Exception ex)
            {
                try { Android.Util.Log.Warn(Tag, "发更新卡片失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>解析卡片 text 里的 JSON（供渲染端调用）。</summary>
        public static Dictionary<string, object> ParseCard(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(text);
                if (obj == null) return null;
                object schema;
                if (!obj.TryGetValue("schema", out schema) || !string.Equals(schema as string, CardSchema))
                    return null;
                return obj;
            }
            catch { return null; }
        }

        /// <summary>卡片安装按钮调用：安装已下载的 APK。</summary>
        public static bool InstallApk(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName)) return false;
                var name = Path.GetFileName(fileName);
                var path = Path.Combine(UpdateDir, name);
                if (!File.Exists(path))
                {
                    Toast(L10n.T("安装包不存在，请重新检测更新", "Installer not found, please check for updates again"));
                    return false;
                }
                return ApkInstallActivity.RedirectLocalFileToInstaller(Ctx, path, name);
            }
            catch (Exception ex)
            {
                Toast(L10n.T("安装失败：", "Install failed: ") + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------
        // 弹窗 / Toast
        // ------------------------------------------------------------

        private static void ShowUpdateDialog(string version, string notes, string status, string apkPath)
        {
            var activity = CoreHost.Host;
            if (activity == null || activity.IsFinishing) return;
            activity.RunOnUiThread(() =>
            {
                try
                {
                    var msg = L10n.T("新版本：", "New version: ") + version + "\n\n"
                              + notes + "\n\n" + status;
                    var dlg = new AlertDialog.Builder(activity)
                        .SetTitle(L10n.T("程序更新", "App update"))
                        .SetMessage(msg);
                    if (!string.IsNullOrEmpty(apkPath))
                    {
                        dlg.SetPositiveButton(L10n.T("立即安装", "Install now"), (s, e) => InstallApk(Path.GetFileName(apkPath)));
                    }
                    else
                    {
                        dlg.SetPositiveButton(L10n.T("前往主页", "Open home"), (s, e) =>
                        {
                            try { BrowserActivity.Open(activity, "https://github.com/yuwenhui2020/AhoChatA/releases"); } catch { }
                        });
                    }
                    dlg.SetNegativeButton(L10n.T("稍后", "Later"), (s, e) => { });
                    dlg.Show();
                }
                catch { }
            });
        }

        private static void Toast(string text)
        {
            try
            {
                var ctx = Ctx;
                if (ctx != null) L10n.Toast(ctx, text, ToastLength.Long).Show();
            }
            catch { }
        }
    }
}

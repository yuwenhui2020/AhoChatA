using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Widget;
using AndroidX.Core.Content;

namespace AhoChatA
{
    /// <summary>
    /// 打开「apk.1」这类被 QQ / 微信重命名过的安装包：
    /// 这些文件被改成 application/octet-stream，系统不会当作 apk。本 Activity 接收
    /// VIEW / SEND 的 content:// 或 file://，按显示名或字节魔数确认它是 APK，
    /// 复制为 FilesDir/media 下的 .apk 文件，再用 FileProvider + ACTION_INSTALL_PACKAGE
    /// 转接系统安装器完成安装。非 apk 的文件交回「阅读」模块处理。
    /// </summary>
    [Activity(Label = "@string/act_apk_install", Exported = true, Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataSchemes = new[] { "content", "file" },
        DataMimeTypes = new[] {
            "application/vnd.android.package-archive",
            "application/octet-stream",
            "application/xapk-package-archive"
        })]
    // ACTION_SEND 统一由 ShareRouterActivity 分发（提供"安装/发到聊天"选择），
    // 本 Activity 仍可通过显式 Intent 或 ACTION_VIEW 的 apk 类型直接打开。
    public class ApkInstallActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            var intent = Intent;
            Android.Net.Uri uri = null;

            if (intent.Action == Intent.ActionSend)
                uri = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
            if (uri == null)
                uri = intent.Data;

            if (uri == null)
            {
                L10n.Toast(this, L10n.T("没有收到文件"), ToastLength.Short).Show();
                Finish();
                return;
            }

            var displayName = QueryDisplayName(uri) ?? uri.LastPathSegment ?? "apk.1";
            string targetApk;

            try
            {
                if (!IsApkCandidate(displayName, uri))
                {
                    // 不是 apk：交回阅读模块（docx/xlsx/pptx/pdf/txt...）
                    ForwardToReader(intent, uri, displayName);
                    Finish();
                    return;
                }

                var destDir = Path.Combine(FilesDir.AbsolutePath, "media");
                Directory.CreateDirectory(destDir);
                var baseName = NormalizeApkName(displayName);
                var dest = Path.Combine(destDir, baseName);
                using (var input = ContentResolver.OpenInputStream(uri))
                {
                    if (input == null) throw new IOException("无法读取该文件的输入流");
                    using (var output = File.Create(dest))
                        input.CopyTo(output, 64 * 1024);
                }

                if (!HasZipSignature(dest))
                {
                    // 魔数不是 ZIP：不是有效 apk，避免误安装
                    try { TrashManager.Instance.MoveToTrash(dest); } catch { }
                    ForwardToReader(intent, uri, displayName);
                    Finish();
                    return;
                }

                targetApk = dest;
                Install(targetApk);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("打开安装包失败：") + ex.Message, ToastLength.Long).Show();
            }
            Finish();
        }

        private void Install(string apkPath)
        {
            var file = new Java.IO.File(apkPath);
            Android.Net.Uri contentUri;
            try
            {
                contentUri = FileProvider.GetUriForFile(this, "com.Ahochat.android.fileprovider", file);
            }
            catch (Exception)
            {
                // 老 ROM 上 ACTION_INSTALL_PACKAGE 也接受 file://
                contentUri = Android.Net.Uri.FromFile(file);
            }

            var install = new Intent(Intent.ActionInstallPackage);
            install.SetDataAndType(contentUri, "application/vnd.android.package-archive");
            install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            try
            {
                StartActivity(install);
            }
            catch (Exception)
            {
                // ACTION_INSTALL_PACKAGE 在部分 ROM 被禁：退回 ACTION_VIEW 让系统安装器接管
                try
                {
                    var view = new Intent(Intent.ActionView);
                    view.SetDataAndType(contentUri, "application/vnd.android.package-archive");
                    view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                    StartActivity(view);
                }
                catch (Exception ex2)
                {
                    L10n.Toast(this, L10n.T("无法调起安装器：") + ex2.Message, ToastLength.Long).Show();
                }
            }
        }

        private void ForwardToReader(Intent intent, Android.Net.Uri uri, string name)
        {
            try
            {
                var reader = new Intent(this, typeof(OfficeViewerActivity));
                reader.SetAction(Intent.ActionView);
                reader.SetData(uri);
                reader.PutExtra(OfficeViewerActivity.ExtraName, name);
                reader.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                StartActivity(reader);
            }
            catch (Exception)
            {
                L10n.Toast(this, L10n.T("该文件不是安装包，且没有可打开它的应用"), ToastLength.Long).Show();
            }
        }

        /// <summary>是否像 apk：显示名以 .apk 或 .apk.N（N 为数字，QQ/微信常见）结尾，或 MIME 明确是安装包。</summary>
        private bool IsApkCandidate(string name, Android.Net.Uri uri)
        {
            return IsApkName(name);
        }

        /// <summary>仅按文件名判定是否 apk（含 apk.1 这类被重命名的包）。供外部入口复用。</summary>
        public static bool IsApkName(string name)
        {
            var lower = (name ?? "").ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(lower, @"\.apk(\.\d+)?$");
        }

        /// <summary>
        /// 从 App 内部（阅读/聊天/Web）打开文件时调用：若名字是 apk/apk.1，
        /// 就从本地路径调起系统安装器并返回 true；否则返回 false 交给阅读器。
        /// </summary>
        public static bool RedirectLocalFileToInstaller(Context ctx, string path, string name)
        {
            if (ctx == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            if (!IsApkName(name) && !IsApkName(Path.GetFileName(path))) return false;
            try
            {
                // 已在 FilesDir 下的文件直接用；否则复制一份归一化的 .apk 再安装
                string apk = path;
                if (!apk.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    var destDir = Path.Combine(ctx.FilesDir.AbsolutePath, "media");
                    Directory.CreateDirectory(destDir);
                    var baseName = System.Text.RegularExpressions.Regex.Replace(
                        Path.GetFileName(name ?? path), @"\.apk\.\d+$", ".apk",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!baseName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) baseName += ".apk";
                    apk = Path.Combine(destDir, baseName);
                    using (var src = File.OpenRead(path))
                    using (var dst = File.Create(apk))
                        src.CopyTo(dst, 64 * 1024);
                }
                var file = new Java.IO.File(apk);
                Android.Net.Uri contentUri;
                try { contentUri = FileProvider.GetUriForFile(ctx, "com.Ahochat.android.fileprovider", file); }
                catch { contentUri = Android.Net.Uri.FromFile(file); }
                var install = new Intent(Intent.ActionInstallPackage);
                install.SetDataAndType(contentUri, "application/vnd.android.package-archive");
                install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                try { ctx.StartActivity(install); }
                catch
                {
                    var view = new Intent(Intent.ActionView);
                    view.SetDataAndType(contentUri, "application/vnd.android.package-archive");
                    view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                    ctx.StartActivity(view);
                }
                L10n.Toast(ctx, L10n.T("已转交系统安装程序"), ToastLength.Short).Show();
                return true;
            }
            catch (Exception ex)
            {
                L10n.Toast(ctx, L10n.T("调起安装程序失败：") + ex.Message, ToastLength.Long).Show();
                return true;   // 已经是 apk，别再交给阅读器当 docx 打开
            }
        }

        /// <summary>把 apk.1 / xxx.apk.1 归一化为 xxx.apk 作为落地文件名。</summary>
        private string NormalizeApkName(string name)
        {
            var safe = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/'));
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\.apk\.\d+$", ".apk", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!safe.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                safe += ".apk";
            // 去掉文件系统不允许的字符
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return safe;
        }

        /// <summary>APK 本质是 ZIP，前两个字节为 PK。用来兜底确认不是被重命名的其它文件。</summary>
        private bool HasZipSignature(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Length < 2) return false;
                    return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
                }
            }
            catch { return false; }
        }

        private string QueryDisplayName(Android.Net.Uri uri)
        {
            try
            {
                if (uri.Scheme == "content")
                {
                    var projection = new[] { OpenableColumns.DisplayName };
                    using (var cursor = ContentResolver.Query(uri, projection, null, null, null))
                    {
                        if (cursor != null && cursor.MoveToFirst())
                        {
                            var idx = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                            if (idx >= 0) return cursor.GetString(idx);
                        }
                    }
                }
            }
            catch { }
            var seg = uri.LastPathSegment;
            if (!string.IsNullOrEmpty(seg))
            {
                var q = seg.IndexOf('?');
                if (q >= 0) seg = seg.Substring(0, q);
            }
            return seg;
        }
    }
}

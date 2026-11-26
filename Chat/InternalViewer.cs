using System;
using Android.App;
using Android.Content;
using Android.Widget;
using AndroidX.Core.Content;
using IoPath = System.IO.Path;

namespace AhoChatA
{
    /// <summary>
    /// 聊天媒体的内置打开与系统分享统一入口。
    /// 图片 / 视频 / 音乐 / 书籍点击附件卡片后直接进入应用内查看器
    /// （全屏看图、全屏播放、音乐播放页、内置阅读器），
    /// 查看界面里都有「系统分享」，可转发给微信 / QQ / 网盘等其它应用。
    /// 其余类型返回 false，由调用方按原逻辑交给系统打开。
    /// </summary>
    public static class InternalViewer
    {
        /// <summary>
        /// 尝试用内置查看器打开本地文件。返回 false 表示不属于可内置打开的类型
        /// （或启动失败），调用方走系统 ACTION_VIEW。
        /// </summary>
        public static bool TryOpen(Activity act, string path, string title = null)
        {
            if (act == null || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return false;
            try
            {
                var name = string.IsNullOrEmpty(title) ? IoPath.GetFileName(path) : title;
                switch (MediaShare.Classify(path))
                {
                    case "photo":
                        var pi = new Intent(act, typeof(PhotoActivity));
                        pi.PutExtra(PhotoActivity.ExtraPath, path);
                        act.StartActivity(pi);
                        return true;
                    case "video":
                        var vi = new Intent(act, typeof(VideoActivity));
                        vi.PutExtra(VideoActivity.ExtraPath, path);
                        vi.PutExtra(VideoActivity.ExtraTitle, name);
                        act.StartActivity(vi);
                        return true;
                    case "music":
                        var mi = new Intent(act, typeof(MusicActivity));
                        mi.PutExtra(MusicActivity.ExtraPath, path);
                        act.StartActivity(mi);
                        return true;
                    case "book":
                        var bi = new Intent(act, typeof(ReaderActivity));
                        bi.PutExtra(ReaderActivity.ExtraBook, path);
                        bi.PutExtra(ReaderActivity.ExtraBookName, name);
                        act.StartActivity(bi);
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("InternalViewer", "内置打开失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>用系统分享面板把本地文件分享给其它应用。</summary>
        public static void ShareFile(Activity act, string path, string mime = null)
        {
            if (act == null || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            try
            {
                var uri = FileProvider.GetUriForFile(act, "com.Ahochat.android.fileprovider", new Java.IO.File(path));
                var send = new Intent(Intent.ActionSend);
                send.SetType(string.IsNullOrEmpty(mime) ? MimeOf(path) : mime);
                send.PutExtra(Intent.ExtraStream, uri);
                send.AddFlags(ActivityFlags.GrantReadUriPermission);
                act.StartActivity(Intent.CreateChooser(send, L10n.T("分享文件")));
            }
            catch (Exception ex)
            {
                try { L10n.Toast(act, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show(); } catch { }
            }
        }

        /// <summary>网页端从后台服务发起系统分享：走 FileProvider，并加 NEW_TASK 允许从非 Activity 上下文启动。</summary>
        public static void ShareFileFromService(Context ctx, string path, string mime = null)
        {
            if (ctx == null || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            try
            {
                var uri = FileProvider.GetUriForFile(ctx, "com.Ahochat.android.fileprovider", new Java.IO.File(path));
                var send = new Intent(Intent.ActionSend);
                send.SetType(string.IsNullOrEmpty(mime) ? MimeOf(path) : mime);
                send.PutExtra(Intent.ExtraStream, uri);
                send.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                var chooser = Intent.CreateChooser(send, L10n.T("分享文件"));
                chooser.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(chooser);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("InternalViewer", "网页端外部分享失败: " + ex.Message);
            }
        }

        /// <summary>按扩展名推断常见 MIME 类型（与聊天附件卡片一致的风格）。</summary>
        public static string MimeOf(string path)
        {
            switch ((IoPath.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".mp4": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".avi": return "video/x-msvideo";
                case ".mov": return "video/quicktime";
                case ".webm": return "video/webm";
                case ".3gp": return "video/3gpp";
                case ".mp3": return "audio/mpeg";
                case ".m4a": return "audio/mp4";
                case ".flac": return "audio/flac";
                case ".wav": return "audio/wav";
                case ".aac": return "audio/aac";
                case ".ogg": return "audio/ogg";
                case ".opus": return "audio/ogg";
                case ".txt": case ".md": case ".log": return "text/plain";
                case ".pdf": return "application/pdf";
                case ".epub": return "application/epub+zip";
                case ".zip": return "application/zip";
                case ".apk": return "application/vnd.android.package-archive";
                default: return "application/octet-stream";
            }
        }
    }
}

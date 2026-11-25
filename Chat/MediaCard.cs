using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
// Android.Graphics 里也有一个 Path，和 System.IO.Path 撞名
using Path = System.IO.Path;

namespace AhoChatA
{
    /// <summary>附件消息的四种形态。判定规则与电脑端 MediaUtil 完全一致。</summary>
    public enum MediaKinds
    {
        None = 0,
        Image,
        Video,
        Audio,
        File
    }

    /// <summary>
    /// 附件消息的卡片渲染 + 下载/打开。
    ///
    /// 需求（用户原话）：图片直接显示图片、视频显示封面、文件显示文件卡片；
    /// 点一下卡片是下载，再点一下才打开。以前所有附件都只渲染成一行
    /// 「🖼 文件名」的文字气泡，图片根本看不到。
    ///
    /// kind 词汇沿用本 App 既有的 pic / video / audio / file；
    /// 电脑端老版本发的是 image，这里也认。
    /// </summary>
    internal sealed class MediaCard
    {
        private readonly Activity _act;
        private readonly float _density;
        private readonly int _accent;
        private readonly int _textMain;
        private readonly int _textSub;
        private readonly int _stroke;

        public MediaCard(Activity act, int accent, int textMain, int textSub, int stroke)
        {
            _act = act;
            _density = act.Resources.DisplayMetrics.Density;
            _accent = accent;
            _textMain = textMain;
            _textSub = textSub;
            _stroke = stroke;
        }

        private int Dp(float v)
        {
            return (int)(v * _density + 0.5f);
        }

        /* ===================== 类型判定 ===================== */

        public static MediaKinds KindOf(string kind, string name)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "pic" || k == "image" || k == "photo" || k == "img") return MediaKinds.Image;
            if (k == "video" || k == "movie") return MediaKinds.Video;
            if (k == "audio" || k == "voice" || k == "music") return MediaKinds.Audio;
            return KindByExtension(name);
        }

        public static MediaKinds KindByExtension(string name)
        {
            string ext = (Path.GetExtension(name) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".webp":
                case ".bmp": case ".heic": case ".heif": case ".avif":
                    return MediaKinds.Image;
                case ".mp4": case ".mov": case ".m4v": case ".3gp": case ".webm":
                case ".mkv": case ".avi": case ".flv": case ".wmv":
                    return MediaKinds.Video;
                case ".mp3": case ".wav": case ".ogg": case ".m4a": case ".aac":
                case ".flac": case ".amr": case ".opus":
                    return MediaKinds.Audio;
            }
            return MediaKinds.File;
        }

        public static bool IsMediaKind(MediaKinds k)
        {
            return k != MediaKinds.None;
        }

        /* ===================== 卡片构建 ===================== */

        /// <summary>
        /// 建一张附件卡片。返回的 View 直接放进消息行。
        /// </summary>
        /// <param name="msg">消息字典（读 media / mediaUrl / name / size）</param>
        /// <param name="isSelf">是不是自己发的（决定卡片色调）</param>
        /// <param name="mediaDir">应用私有 media 目录</param>
        /// <param name="maxW">卡片最大宽度（像素）</param>
        public View Create(IDictionary<string, object> msg, bool isSelf, string mediaDir, int maxW)
        {
            string kind = Msg.Kind(msg);
            string fileName = Msg.Str(msg, "media", "");
            if (string.IsNullOrEmpty(fileName)) fileName = Msg.Str(msg, "mediaUrl", "");
            int slash = fileName.LastIndexOf('/');
            if (slash >= 0) fileName = fileName.Substring(slash + 1);

            // 原始文件名（只用于下载时保存成原名，界面上不显示）
            string display = Msg.Str(msg, "name", "");
            if (string.IsNullOrEmpty(display)) display = Msg.Str(msg, "media", "");
            if (string.IsNullOrEmpty(display)) display = Msg.Str(msg, "text", "");
            if (string.IsNullOrEmpty(display)) display = fileName;

            string path = string.IsNullOrEmpty(fileName) ? "" : Path.Combine(mediaDir, fileName);
            bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
            long size = Msg.Num(msg, "size");
            if (size <= 0 && exists)
            {
                try { size = new FileInfo(path).Length; } catch { }
            }

            MediaKinds mk = KindOf(kind, string.IsNullOrEmpty(display) ? fileName : display);
            if (mk == MediaKinds.None) mk = MediaKinds.File;

            // 媒体快捷卡片：仅限「分享到频道」显式带 mlib 标记的消息（阅读/音乐/图片/视频），
            // 普通聊天附件不带标记，保持原来的图片预览/播放体验；点击 = 添加到本机媒体库
            string mlib = Msg.Str(msg, "mlib", "");
            if (!string.IsNullOrEmpty(mlib))
                return CreateQuickCard(msg, isSelf, mediaDir, maxW, mlib, fileName, display, size);

            // 卡片标题用「[文件] 3.00 MB」这种类型 + 体积，不显示文件名
            string title = Msg.FileLabel(kind, size);

            var card = new LinearLayout(_act) { Orientation = Orientation.Vertical };
            card.SetBackgroundDrawable(RoundBg(exists ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFFF7F7F9), 14, _stroke));
            card.LayoutParameters = new LinearLayout.LayoutParams(
                Math.Min(maxW, Dp(mk == MediaKinds.File ? 250 : 210)), ViewGroup.LayoutParams.WrapContent);

            var status = new TextView(_act) { Text = HintText(size, false), TextSize = 11 };
            status.SetTextColor(new Color(_textSub));
            status.SetPadding(Dp(10), Dp(6), Dp(10), Dp(8));

            string savedPath = null;
            Android.Net.Uri savedUri = null;
            string mime = MimeOf(display);

            if (mk == MediaKinds.Image || mk == MediaKinds.Video)
            {
                // 媒体区：先按 4:3 占位，解码完成后按真实比例改成正确高度
                var host = new FrameLayout(_act);
                int w = Math.Min(maxW, Dp(210));
                int h = (int)(w * 0.75);
                host.LayoutParameters = new LinearLayout.LayoutParams(w, h);
                var img = new ImageView(_act);
                img.LayoutParameters = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
                img.SetScaleType(ImageView.ScaleType.CenterCrop);
                host.AddView(img);

                if (mk == MediaKinds.Video)
                {
                    var badge = new FrameLayout(_act);
                    badge.SetBackgroundDrawable(BadgeBg());
                    var bIcon = new IconView(_act, IconKind.Play) { IconColor = unchecked((int)0xFFFFFFFF) };
                    int bs = Dp(24);
                    badge.AddView(bIcon, new FrameLayout.LayoutParams(bs, bs) { Gravity = GravityFlags.Center });
                    var blp = new FrameLayout.LayoutParams(Dp(46), Dp(46));
                    blp.Gravity = GravityFlags.Center;
                    badge.LayoutParameters = blp;
                    host.AddView(badge);
                }

                card.AddView(host);
                var name = new TextView(_act) { Text = title, TextSize = 13 };
                name.SetTextColor(new Color(_textMain));
                name.SetTypeface(null, TypefaceStyle.Bold);
                name.SetPadding(Dp(10), Dp(8), Dp(10), 0);
                name.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
                name.SetSingleLine(true);
                card.AddView(name);
                card.AddView(status);

                if (exists)
                {
                    int target = w;
                    Task.Run(() =>
                    {
                        Bitmap bmp = LoadThumb(path, mk == MediaKinds.Video, target);
                        if (bmp == null) return;
                        _act.RunOnUiThread(() =>
                        {
                            try
                            {
                                if (img.Handle == IntPtr.Zero) { bmp.Recycle(); return; }
                                img.SetImageBitmap(bmp);
                                int hh = (int)((float)bmp.Height / bmp.Width * w);
                                int cap = Dp(360);
                                if (hh > cap) hh = cap;
                                if (hh < Dp(80)) hh = Dp(80);
                                host.LayoutParameters = new LinearLayout.LayoutParams(w, hh);
                            }
                            catch { }
                        });
                    });
                }
            }
            else
            {
                // 文件卡片：图标 + 文件名 + 大小
                var row = new LinearLayout(_act) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(12), Dp(12), Dp(12), Dp(6));

                var icon = new FrameLayout(_act);
                icon.SetBackgroundDrawable(RoundBg(unchecked((int)0xFFEAF6FE), 10, 0));
                var ic = new IconView(_act, Icons.ForMediaKinds(mk)) { IconColor = _accent };
                int isz = Dp(22);
                icon.AddView(ic, new FrameLayout.LayoutParams(isz, isz) { Gravity = GravityFlags.Center });
                icon.LayoutParameters = new LinearLayout.LayoutParams(Dp(40), Dp(40));
                row.AddView(icon);

                var col = new LinearLayout(_act) { Orientation = Orientation.Vertical };
                var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                lp.SetMargins(Dp(10), 0, 0, 0);
                col.LayoutParameters = lp;
                var name = new TextView(_act) { Text = title, TextSize = 14 };
                name.SetTextColor(new Color(_textMain));
                name.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
                name.SetSingleLine(true);
                col.AddView(name);
                row.AddView(col);
                card.AddView(row);
                card.AddView(status);
            }

            // 点击：图片/视频/音乐/书籍直接内置打开；其余文件一次保存、再次打开
            card.Click += (s, e) =>
            {
                try
                {
                    if (!exists)
                    {
                        L10n.Toast(_act, L10n.T("文件不在这台手机上（可能已被清理）"), ToastLength.Short).Show();
                        return;
                    }
                    // 图片 / 视频 / 音乐 / 书籍：本地已有文件，直接进应用内查看器（可再系统分享）
                    if (InternalViewer.TryOpen(_act, path, display)) return;
                    if (savedPath == null && savedUri == null)
                    {
                        // 同一附件重复点击时复用上次的保存结果：savedPath 是 Create() 的局部变量，
                        // 每次重建都是 null，不查缓存就会每点一次往下载目录多落一份副本
                        // （MediaStore 遇同名会自动改名成 xxx (1).txt）。
                        lock (_savedLock)
                        {
                            if (_savedCache.TryGetValue(path, out var hit) && !string.IsNullOrEmpty(hit.Path))
                            {
                                savedPath = hit.Path;
                                savedUri = hit.Uri;
                            }
                        }
                    }
                    if (savedPath == null && savedUri == null)
                    {
                        Android.Net.Uri uri;
                        string saved = SaveToDownloads(_act, path, display, out uri);
                        if (string.IsNullOrEmpty(saved))
                        {
                            L10n.Toast(_act, L10n.T("保存失败，请检查存储权限"), ToastLength.Long).Show();
                            return;
                        }
                        savedPath = saved;
                        savedUri = uri;
                        lock (_savedLock)
                        {
                            if (_savedCache.Count > 400) _savedCache.Clear();   // 简单封顶，防长会话撑大
                            _savedCache[path] = (savedPath, savedUri);
                        }
                        status.Text = HintText(size, true);
                        L10n.Toast(_act, L10n.T("已保存到 下载/Mchat"), ToastLength.Short).Show();
                        return;
                    }
                    OpenFile(savedUri, savedPath ?? path, mime);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "附件点击失败: " + ex.Message);
                }
            };

            return card;
        }

        private string HintText(long size, bool saved)
        {
            // 体积已经在标题里（[文件] 1.4 KB），这里只写动作，避免同一行重复两次大小
            _ = size;
            return saved ? L10n.T("已保存到 下载/Mchat · 点击打开") : L10n.T("点击下载");
        }

        /* ===================== 媒体快捷卡片（频道分享） ===================== */

        /// <summary>
        /// 频道分享的阅读/音乐/图片/视频快捷卡片：类型徽标 + 文件名 + 大小 + 「点击添加」。
        /// 点击 = 落盘 /sdcard/file/&lt;类型&gt;/ 并加入本机对应媒体库（幂等，重复点击只提示）。
        /// </summary>
        private View CreateQuickCard(IDictionary<string, object> msg, bool isSelf, string mediaDir,
                                     int maxW, string mlib, string fileName, string display, long size)
        {
            _ = msg; _ = isSelf;
            string name = string.IsNullOrEmpty(display) ? fileName : display;
            if (string.IsNullOrEmpty(name)) name = L10n.T("未命名");
            string path = string.IsNullOrEmpty(fileName) ? "" : Path.Combine(mediaDir, fileName);
            bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);

            var card = new LinearLayout(_act) { Orientation = Orientation.Vertical };
            card.SetBackgroundDrawable(RoundBg(unchecked((int)0xFFFFFFFF), 14, _stroke));
            card.LayoutParameters = new LinearLayout.LayoutParams(
                Math.Min(maxW, Dp(250)), ViewGroup.LayoutParams.WrapContent);

            var row = new LinearLayout(_act) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(12), Dp(12), Dp(12), Dp(6));

            // 类型徽标
            var icon = new FrameLayout(_act);
            icon.SetBackgroundDrawable(RoundBg(unchecked((int)0xFFEAF6FE), 10, 0));
            var ic = new IconView(_act, Icons.ForMlib(mlib)) { IconColor = _accent };
            int isz = Dp(22);
            icon.AddView(ic, new FrameLayout.LayoutParams(isz, isz) { Gravity = GravityFlags.Center });
            icon.LayoutParameters = new LinearLayout.LayoutParams(Dp(40), Dp(40));
            row.AddView(icon);

            var col = new LinearLayout(_act) { Orientation = Orientation.Vertical };
            var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lp.SetMargins(Dp(10), 0, 0, 0);
            col.LayoutParameters = lp;

            var tag = new TextView(_act) { Text = MediaShare.LabelOf(mlib), TextSize = 10.5f };
            tag.SetTextColor(new Color(_accent));
            tag.SetTypeface(null, TypefaceStyle.Bold);
            col.AddView(tag);

            var nameTv = new TextView(_act) { Text = name, TextSize = 13.5f };
            nameTv.SetTextColor(new Color(_textMain));
            nameTv.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
            nameTv.SetSingleLine(true);
            col.AddView(nameTv);
            row.AddView(col);
            card.AddView(row);

            var status = new TextView(_act) { Text = QuickHint(exists, size), TextSize = 11 };
            status.SetTextColor(new Color(_textSub));
            status.SetPadding(Dp(12), Dp(2), Dp(12), Dp(8));
            card.AddView(status);

            card.Click += (s, e) =>
            {
                try
                {
                    string tip = MediaShare.ImportReceived(_act, mlib, string.IsNullOrEmpty(display) ? fileName : display, path);
                    status.Text = tip;
                    L10n.Toast(_act, tip, ToastLength.Short).Show();
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("Mchat", "快捷卡片点击失败: " + ex.Message);
                }
            };

            return card;
        }

        private static string QuickHint(bool exists, long size)
        {
            string sz = size > 0 ? " · " + Msg.SizeText(size) : "";
            return exists ? L10n.T("点击添加到本机") + sz : L10n.T("文件不在这台设备") + sz;
        }

        private static string SizeText(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#") + " MB";
            return (bytes / 1073741824.0).ToString("0.##") + " GB";
        }

        private Android.Graphics.Drawables.GradientDrawable RoundBg(long color, int radius, long stroke)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor(unchecked((int)color));
            d.SetCornerRadius(Dp(radius));
            if (stroke != 0) d.SetStroke(Math.Max(1, Dp(1)), new Color(unchecked((int)stroke)));
            return d;
        }

        private Android.Graphics.Drawables.GradientDrawable BadgeBg()
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetShape(Android.Graphics.Drawables.ShapeType.Oval);
            d.SetColor(unchecked((int)0x8C000000));
            return d;
        }

        /* ===================== 缩略图 ===================== */

        private Bitmap LoadThumb(string path, bool video, int targetW)
        {
            try
            {
                if (video)
                {
                    var mmr = new Android.Media.MediaMetadataRetriever();
                    try
                    {
                        mmr.SetDataSource(path);
                        // 取 0.5 秒处的帧：0 秒在很多机型上是黑帧
                        Bitmap frame = mmr.GetFrameAtTime(500000, Android.Media.Option.ClosestSync);
                        return frame;
                    }
                    finally { mmr.Release(); }
                }

                var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, probe);
                if (probe.OutWidth <= 0) return null;
                int sample = 1;
                while (probe.OutWidth / (sample * 2) >= targetW) sample *= 2;
                var opts = new BitmapFactory.Options { InSampleSize = sample };
                return BitmapFactory.DecodeFile(path, opts);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "生成缩略图失败: " + ex.Message);
                return null;
            }
        }

        /* ===================== 下载 / 打开 ===================== */

        private static string MimeOf(string name)
        {
            string ext = (Path.GetExtension(name) ?? "").ToLowerInvariant().TrimStart('.');
            string mime = Android.Webkit.MimeTypeMap.Singleton.GetMimeTypeFromExtension(ext);
            return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return L10n.T("附件");
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            // 冒号、问号这些在 FAT 上也不合法
            name = name.Replace(':', '_').Replace('?', '_').Replace('*', '_').Replace('"', '_');
            name = name.Replace('<', '_').Replace('>', '_').Replace('|', '_');
            return name.Length > 90 ? name.Substring(name.Length - 90) : name;
        }

        /// <summary>源文件 → 已保存到「下载/Mchat」的结果（进程内缓存，避免重复落盘）。</summary>
        private static readonly Dictionary<string, (string Path, Android.Net.Uri Uri)> _savedCache =
            new Dictionary<string, (string, Android.Net.Uri)>(StringComparer.Ordinal);
        private static readonly object _savedLock = new object();

        /// <summary>
        /// 保存到系统「下载/Mchat」。返回展示用路径，uri 在 Q 及以上是有内容的 content://，
        /// 后续打开直接用它（比重新拼 file:// 稳）。
        /// </summary>
        public static string SaveToDownloads(Context ctx, string src, string displayName, out Android.Net.Uri uri)
        {
            uri = null;
            try
            {
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return null;
                string name = SafeName(displayName);
                string mime = MimeOf(name);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    var values = new ContentValues();
                    values.Put("_display_name", name);
                    values.Put("mime_type", mime);
                    values.Put("relative_path", "Download/Mchat");
                    values.Put("is_pending", 1);
                    uri = ctx.ContentResolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
                    if (uri == null) return null;
                    using (var output = ctx.ContentResolver.OpenOutputStream(uri))
                    using (var input = File.OpenRead(src))
                    {
                        if (output == null) return null;
                        input.CopyTo(output);
                    }
                    var done = new ContentValues();
                    done.Put("is_pending", 0);
                    ctx.ContentResolver.Update(uri, done, null, null);
                    string dir = Android.OS.Environment.GetExternalStoragePublicDirectory(
                        Android.OS.Environment.DirectoryDownloads).AbsolutePath;
                    return Path.Combine(Path.Combine(dir, "Mchat"), name);
                }

                // Android 9 及以下：直接写公共下载目录（需要存储权限）
                var target = new Java.IO.File(
                    Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads),
                    "Mchat");
                target.Mkdirs();
                var dst = new Java.IO.File(target, name);
                using (var input = File.OpenRead(src))
                using (var output = File.Create(dst.AbsolutePath))
                {
                    input.CopyTo(output);
                }
                return dst.AbsolutePath;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Mchat", "保存到下载目录失败: " + ex.Message);
                return null;
            }
        }

        private void OpenFile(Android.Net.Uri uri, string path, string mime)
        {
            try
            {
                Intent intent = new Intent(Intent.ActionView);
                if (uri != null)
                {
                    intent.SetDataAndType(uri, mime);
                }
                else
                {
                    var file = new Java.IO.File(path);
                    if (!file.Exists()) { L10n.Toast(_act, L10n.T("文件不存在"), ToastLength.Short).Show(); return; }
                    var furi = FileProvider.GetUriForFile(_act, "com.Ahochat.android.fileprovider", file);
                    intent.SetDataAndType(furi, mime);
                }
                intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                _act.StartActivity(intent);
            }
            catch (Exception)
            {
                L10n.Toast(_act, L10n.T("没有可打开该文件的应用"), ToastLength.Long).Show();
            }
        }
    }
}

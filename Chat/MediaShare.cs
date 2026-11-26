using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Widget;
using AhoChatA.Music;
using AhoChatA.Reader;
using Path = System.IO.Path;

namespace AhoChatA
{
    /// <summary>
    /// 媒体「分享到频道」与「点击添加到本机媒体库」的统一入口。
    ///
    /// 需求（用户原话）：为阅读 / 视频 / 音乐 / 图片添加分享到频道功能，分享的媒体作为
    /// 快捷卡片显示，点击后直接添加到另一台设备的阅读 / 影音 / 图片列表；
    /// 所有文件下载到 /sdcard/file，并按类型（阅读 / 音乐 / 图片 / 视频）分四个文件夹。
    ///
    /// 传输完全复用既有唯一出口 SendQaulFileBytes（qaul + 局域网直投），不引入第二套传输；
    /// 接收端按文件扩展名判定媒体类别（两条链路 —— qaul 分片与局域网 JSON —— 都只有
    /// 文件名可靠可用，扩展名判定天然与四类下载文件夹一一对应）。
    /// </summary>
    public static class MediaShare
    {
        /// <summary>下载根目录：/sdcard/file（/storage/emulated/0/file）。</summary>
        public const string RootDir = "/storage/emulated/0/file";

        /// <summary>四类媒体：book=阅读 music=音乐 photo=图片 video=视频。</summary>
        public static readonly string[] Kinds = { "book", "music", "photo", "video" };

        private static readonly string[] BookExt =
            { ".txt", ".epub", ".pdf", ".fb2", ".fbz", ".md", ".markdown" };
        private static readonly string[] MusicExt =
            { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".ape", ".wma" };
        private static readonly string[] PhotoExt =
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".heic", ".heif", ".avif" };
        private static readonly string[] VideoExt =
            { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".3gp", ".flv", ".wmv" };

        /* ===================== 类别判定 ===================== */

        /// <summary>按扩展名判定媒体类别；不属于四类返回 null（普通文件照旧走附件卡片）。</summary>
        public static string Classify(string fileName)
        {
            string ext = (Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant();
            if (BookExt.Contains(ext)) return "book";
            if (MusicExt.Contains(ext)) return "music";
            if (PhotoExt.Contains(ext)) return "photo";
            if (VideoExt.Contains(ext)) return "video";
            return null;
        }

        /// <summary>类别 → 中文标签（阅读 / 音乐 / 图片 / 视频）。</summary>
        public static string LabelOf(string mlib)
        {
            switch (mlib)
            {
                case "book": return "阅读";
                case "music": return "音乐";
                case "photo": return "图片";
                case "video": return "视频";
                default: return L10n.T("媒体");
            }
        }

        /// <summary>类别 → 下载子文件夹名（阅读 / 音乐 / 图片 / 视频）。</summary>
        public static string FolderOf(string mlib) => LabelOf(mlib);

        /// <summary>类别 → 该类文件落地的完整目录（自动创建）。</summary>
        public static string DirOf(string mlib)
        {
            var dir = Path.Combine(RootDir, FolderOf(mlib));
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /* ===================== 发送：分享到频道 ===================== */

        /// <summary>
        /// 弹出会话选择器（频道 / qaul 私聊 / qaul 群），把本地文件分享过去。
        /// 返回是否发起了发送（文件不存在或没有可用会话时为 false）。
        /// </summary>
        public static bool ShareToChannel(Activity act, string displayName, string filePath)
        {
            try
            {
                if (act == null || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    L10n.Toast(act, L10n.T("文件不存在，无法分享"), ToastLength.Short).Show();
                    return false;
                }
                var bridge = CoreHost.Bridge;
                if (bridge == null)
                {
                    L10n.Toast(act, L10n.T("聊天服务尚未就绪"), ToastLength.Short).Show();
                    return false;
                }

                // 只列支持文件传输的会话：频道（chan:）/ qaul 私聊 / qaul 群
                var convs = new List<Conversation>();
                foreach (var c in bridge.GetConversations())
                {
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    if (Convs.IsChan(c.Id) || Convs.IsQaulDm(c.Id) || Convs.IsQaulGroup(c.Id))
                        convs.Add(c);
                }
                if (convs.Count == 0)
                {
                    L10n.Toast(act, L10n.T("还没有可分享的频道或会话"), ToastLength.Short).Show();
                    return false;
                }
                convs.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

                var titles = new string[convs.Count];
                for (int i = 0; i < convs.Count; i++)
                    titles[i] = convs[i].Title + (Convs.IsChan(convs[i].Id) ? L10n.T("（频道）") : "");

                string file = filePath, name = string.IsNullOrEmpty(displayName)
                    ? Path.GetFileName(filePath) : displayName;
                // 按扩展名决定快捷卡片类别（阅读/音乐/图片/视频）；判不出就不带标记（普通附件卡片）
                string mlibKind = Classify(name);
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { }

                L10n.Dialog(act)
                    .SetTitle(L10n.T("分享到频道"))
                    .SetMessage(name + "（" + Msg.SizeText(size) + "）")
                    .SetItems(titles, (s, e) =>
                    {
                        var conv = convs[e.Which].Id;
                        Task.Run(() =>
                        {
                            try
                            {
                                var bytes = File.ReadAllBytes(file);
                                bool ok = bridge.SendMediaShareBytes(conv, name, bytes, mlibKind);
                                act.RunOnUiThread(() =>
                                    L10n.Toast(act, ok
                                        ? L10n.T("已分享到「") + convs[e.Which].Title + "」"
                                        : L10n.T("分享失败：附近没有在线设备"), ToastLength.Short).Show());
                            }
                            catch (Exception ex)
                            {
                                Android.Util.Log.Warn("MediaShare", "分享失败: " + ex.Message);
                                act.RunOnUiThread(() =>
                                    L10n.Toast(act, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show());
                            }
                        });
                    })
                    .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                    .Show();
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaShare", "ShareToChannel 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 网页端「分享到聊天频道」入口：已由前端选好会话，这里不再弹原生选择器。
        /// 复用同一条媒体分享传输链路，保持接收端卡片与原生分享完全一致。
        /// </summary>
        public static bool ShareToChannel(Context ctx, string conv, string displayName, string filePath)
        {
            try
            {
                if (ctx == null || string.IsNullOrEmpty(conv) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return false;
                var bridge = MchatBridge.Current ?? CoreHost.Bridge;
                if (bridge == null) return false;

                string name = string.IsNullOrEmpty(displayName) ? Path.GetFileName(filePath) : displayName;
                // 必须保留扩展名，接收端按扩展名决定落到 阅读/音乐/图片/视频 哪个目录。
                if (MediaShare.Classify(name) == null)
                {
                    var ext = Path.GetExtension(filePath);
                    if (!string.IsNullOrEmpty(ext) && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
                }
                string mlibKind = Classify(name);
                return bridge.SendMediaShareBytes(conv, name, File.ReadAllBytes(filePath), mlibKind);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaShare", "网页分享到频道失败: " + ex.Message);
                return false;
            }
        }

        /* ===================== 接收：点击卡片导入本机 ===================== */

        /// <summary>
        /// 点快捷卡片：把收到的文件落到 /sdcard/file/&lt;类型&gt;/ 并添加进对应媒体库。
        /// 幂等：同名且同大小的文件已存在时跳过拷贝与导入，只提示「已添加」。
        /// </summary>
        /// <param name="mlib">媒体类别（book/music/photo/video）</param>
        /// <param name="displayName">原始文件名（保存用）</param>
        /// <param name="srcPath">已收到的文件在应用 media 目录里的路径</param>
        /// <returns>给用户看的提示文案</returns>
        public static string ImportReceived(Context act, string mlib, string displayName, string srcPath)
        {
            try
            {
                if (string.IsNullOrEmpty(mlib)) mlib = Classify(displayName);
                if (string.IsNullOrEmpty(mlib)) return L10n.T("暂不支持自动添加该类型");
                if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
                    return L10n.T("文件不在这台设备上（可能已被清理）");

                string name = string.IsNullOrEmpty(displayName) ? Path.GetFileName(srcPath) : displayName;
                string safe = SafeName(name);
                var dir = DirOf(mlib);
                var dest = Path.Combine(dir, safe);

                long size = 0;
                try { size = new FileInfo(srcPath).Length; } catch { }

                if (File.Exists(dest))
                {
                    long existLen = 0;
                    try { existLen = new FileInfo(dest).Length; } catch { }
                    if (existLen == size && size > 0)
                        return L10n.T("已在") + LabelOf(mlib) + L10n.T("列表中（") + FolderOf(mlib) + "）";
                    // 同名不同内容：加序号避免覆盖
                    dest = Path.Combine(dir, UniqueName(dir, safe));
                }

                File.Copy(srcPath, dest, true);

                switch (mlib)
                {
                    case "book":
                        ImportBook(act, displayName, dest);
                        return L10n.T("已添加到阅读 · ") + FolderOf(mlib) + "/" + Path.GetFileName(dest);
                    case "music":
                    case "video":
                        RescanMusic();
                        return L10n.T("已添加到影音 · ") + FolderOf(mlib) + "/" + Path.GetFileName(dest);
                    case "photo":
                        RescanMusic();   // 图片与音乐/视频共用一次扫描（PhotoLibrary.Scan）
                        return L10n.T("已添加到图片 · ") + FolderOf(mlib) + "/" + Path.GetFileName(dest);
                }
                return L10n.T("已保存到 ") + FolderOf(mlib);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaShare", "导入失败: " + ex.Message);
                return L10n.T("添加失败：") + ex.Message;
            }
        }

        /// <summary>供网页端桥接调用：从应用 media 目录按文件名导入。</summary>
        public static string ImportFromMediaDir(Context act, string mediaName)
        {
            try
            {
                if (string.IsNullOrEmpty(mediaName)) return L10n.T("参数为空");
                int slash = mediaName.LastIndexOf('/');
                if (slash >= 0) mediaName = mediaName.Substring(slash + 1);
                var bridge = CoreHost.Bridge;
                string src = bridge != null ? Path.Combine(bridge.MediaDirectory, mediaName) : null;
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return L10n.T("文件不存在");
                return ImportReceived(act, Classify(mediaName), mediaName, src);
            }
            catch (Exception ex)
            {
                return L10n.T("添加失败：") + ex.Message;
            }
        }

        private static void ImportBook(Context act, string displayName, string destFile)
        {
            try
            {
                string name = displayName;
                if (string.IsNullOrEmpty(name)) name = Path.GetFileName(destFile);
                var bytes = File.ReadAllBytes(destFile);
                string err;
                var item = ReaderLibrary.Import(name, bytes, out err);
                if (item == null) Android.Util.Log.Warn("MediaShare", "加入书架失败: " + err);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaShare", "加入书架异常: " + ex.Message);
            }
        }

        private static void RescanMusic()
        {
            try
            {
                // 目录配置里的 file/阅读 音乐 图片 视频 内置目录默认启用；
                // 标脏后后台重扫一次，扫完列表自然出现新文件。
                MusicLibrary.Dirty = true;
                Task.Run(async () =>
                {
                    try { await MusicLibrary.ScanAsync(); } catch { }
                });
            }
            catch { }
        }

        /* ===================== 文件名工具 ===================== */

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return L10n.T("文件");
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace(':', '_').Replace('?', '_').Replace('*', '_').Replace('"', '_');
            name = name.Replace('<', '_').Replace('>', '_').Replace('|', '_');
            return name.Length > 90 ? name.Substring(name.Length - 90) : name;
        }

        private static string UniqueName(string dir, string fileName)
        {
            string ext = Path.GetExtension(fileName);
            string stem = ext.Length > 0 ? fileName.Substring(0, fileName.Length - ext.Length) : fileName;
            for (int i = 1; i < 999; i++)
            {
                var cand = Path.Combine(dir, stem + "-" + i + ext);
                if (!File.Exists(cand)) return Path.GetFileName(cand);
            }
            return Path.GetFileName(fileName);
        }
    }
}

// ============================================================
// 音乐模块 · 数据模型
// 一个 Song 对应一个音频/视频文件；标签与封面在扫描时提取并缓存。
// ============================================================
using System;

namespace AhoChatA.Music
{
    public class Song
    {
        public string Path = "";        // 绝对路径
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public int DurationMs;
        public long Size;
        public long Modified;           // 文件 mtime（unix 秒），缓存失效用
        public bool IsVideo;

        /* ---------- 在线音乐（搜索结果，无本地文件） ---------- */
        // 播放走本地代理流地址（Path = http://127.0.0.1:6789/api/media/online/stream?...），
        // 封面走远程 URL（OnlineCover）；下载后由 OnlineMusic 落盘到本地曲库。
        public bool IsOnline;
        public bool IsDivider;          // 列表里的「在线音乐」分区标题，不可点击
        public string OnlineCover = ""; // 远程封面 URL
        public string OnlineId = "";
        public string OnlinePlat = "";
        public string OnlineHqId = "";
        public string OnlineSqId = "";
        public string CachePath = "";   // 已缓存到本地的音频文件（非空则可直接播放，秒开）

        // 扫描时解析出的资源（可为空）
        public string CoverPath = "";   // 内嵌封面导出的缓存文件，或同名图片
        public string LrcPath = "";     // 同名 .lrc，或内嵌歌词导出的缓存文件
        public bool CoverChecked;       // 已经尝试过找封面（避免无封面歌曲每次进页面都重扫）

        /* ---------- 局域网共享（一起听 / 同步听） ---------- */
        // 对端列表项里带的远程封面与歌词原文：对端没有本地文件，不能走 CoverPath/LrcPath，
        // 否则加入之后封面和歌词全是空的（旧实现的 bug）。
        public string CoverUrl = "";    // http 封面（远程图片或发起端 /api/media/art）
        public string LrcText = "";     // LRC 原文（无本地 .lrc 时直接解析它）
        public string LrcUrl = "";      // 完整歌词的取词地址（列表里只带开头几句，后台补齐）
        public string SharedKey = "";   // 发起端的 songKey：切歌按它定位，不再只靠 ltIndex

        /* ---------- 派生属性 ---------- */

        public string FileName
        {
            get
            {
                try { return System.IO.Path.GetFileName(Path); }
                catch { return Path ?? ""; }
            }
        }

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FileName : Title;

        public string ArtistLine
        {
            get
            {
                var a = string.IsNullOrWhiteSpace(Artist) ? L10n.T("未知歌手") : Artist;
                if (!string.IsNullOrWhiteSpace(Album)) return a + " · " + Album;
                return a;
            }
        }

        public static string FormatMs(int ms)
        {
            if (ms <= 0) return "00:00";
            var t = TimeSpan.FromMilliseconds(ms);
            return (int)t.TotalHours > 0
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }
    }
}

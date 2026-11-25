using System;
using System.IO;
using Android.Graphics;
using Path = System.IO.Path;

namespace AhoChatA
{
    /// <summary>
    /// 聊天附件发送前的媒体压缩。
    ///
    /// 背景：mesh/局域网直投走的是「整帧广播」，原图（4K 照片 8MB+、长视频上百 MB）
    /// 又会以 byte[] 常驻内存并再复制一份进 JSON 帧，小内存机型直接 OOM。
    /// 业界（WiFi Direct 文件传输）通行做法是发送侧先做尺寸/质量降级：
    ///   · 长边限制 MaxDim，按 InSampleSize 整数倍降采样（保清晰度又省内存）；
    ///   · JPEG 质量阶梯逐级尝试，直到落进 TargetBytes；
    ///   · 压缩后反而更大的（已被优化过的小图）直接发原图。
    /// GIF 不动（动图压成静帧就废了）；小 PNG 不动（体积本就不大）。
    /// </summary>
    public static class MediaCompress
    {
        /// <summary>图片长边上限（像素）。聊天气泡 210dp 宽，1600px 已够 2 倍余量。</summary>
        public const int MaxDim = 1600;
        /// <summary>压缩目标体积（约 1MB，mesh 网络上秒级可传完）。</summary>
        public const long TargetBytes = 1024 * 1024;
        /// <summary>小于这个体积的 PNG 不碰（可能是图标/小贴图）。</summary>
        public const long PngSkipBytes = 300 * 1024;

        public static bool IsImageName(string name)
        {
            string ext = (Path.GetExtension(name ?? "") ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg"
                || ext == ".webp" || ext == ".bmp" || ext == ".heic" || ext == ".heif";
        }

        public static bool IsVideoName(string name)
        {
            string ext = (Path.GetExtension(name ?? "") ?? "").ToLowerInvariant();
            return ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".3gp"
                || ext == ".webm" || ext == ".mkv" || ext == ".avi" || ext == ".flv" || ext == ".wmv";
        }

        /// <summary>可读体积文本。</summary>
        public static string SizeText(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#") + " MB";
            return (bytes / 1073741824.0).ToString("0.##") + " GB";
        }

        /// <summary>
        /// 压缩图片。返回处理后的字节（失败/不值得压时原样返回 src）；
        /// note 写「已压缩 8.2 MB → 940 KB」或跳过原因，供 Toast 告知用户。
        /// </summary>
        public static byte[] CompressImage(string name, byte[] src, out string note)
        {
            note = "";
            if (src == null || src.Length == 0) return src;

            string ext = (Path.GetExtension(name ?? "") ?? "").ToLowerInvariant();
            if (ext == ".gif") { note = "GIF 保持原样"; return src; }
            if (ext == ".png" && src.Length < PngSkipBytes) { note = ""; return src; }

            Bitmap bmp = null;
            try
            {
                var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
                using (var ms0 = new MemoryStream(src)) BitmapFactory.DecodeStream(ms0, null, probe);
                if (probe.OutWidth <= 0 || probe.OutHeight <= 0) { note = ""; return src; }

                // 第一级：整数倍降采样到 MaxDim 以内（保清晰度且省内存）
                int sample = 1;
                while (Math.Max(probe.OutWidth, probe.OutHeight) / (sample * 2) >= MaxDim) sample *= 2;
                var opts = new BitmapFactory.Options { InSampleSize = sample };
                using (var ms1 = new MemoryStream(src)) bmp = BitmapFactory.DecodeStream(ms1, null, opts);
                if (bmp == null) { note = ""; return src; }

                // 第二级：JPEG 质量阶梯，直到落进 TargetBytes
                byte[] best = null;
                foreach (int q in new[] { 88, 75, 60, 45, 32 })
                {
                    using (var ms = new MemoryStream())
                    {
                        bmp.Compress(Bitmap.CompressFormat.Jpeg, q, ms);
                        best = ms.ToArray();
                    }
                    if (best.Length <= TargetBytes) break;
                }

                // 压缩后反而更大：原图已是优化过的，直接发原图
                if (best == null || best.Length >= src.Length)
                {
                    note = "";
                    return src;
                }

                note = L10n.T("已压缩 ") + SizeText(src.Length) + " → " + SizeText(best.Length);
                return best;
            }
            catch (Exception)
            {
                // 压缩失败绝不阻塞发送：原图照发
                note = "";
                return src;
            }
            finally
            {
                try { bmp?.Recycle(); } catch { }
            }
        }
    }
}
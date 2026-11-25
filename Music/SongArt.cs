// ============================================================
// 共享曲目（一起听 / 同步听）的封面辅助。
//
// 对端拿到的 Song.Path 是一个 http 地址，没有本地 CoverPath，封面只能走
// CoverUrl（远程图片或发起端 /api/media/art?path=）。这里统一负责：
//   · 取 URL、内存缓存、异步下载回填 ImageView；
//   · 无封面时给「灰底 + 双音符」占位图，避免各页面再显示程序图标。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using Android.Graphics;
using Android.Widget;

namespace AhoChatA.Music
{
    public static class SongArt
    {
        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        private static readonly HashSet<string> _loading = new HashSet<string>();

        public static string Url(Song s)
        {
            if (s == null) return "";
            if (!string.IsNullOrEmpty(s.CoverUrl)) return s.CoverUrl;
            return s.OnlineCover ?? "";
        }

        public static Bitmap Placeholder(bool small = true)
        {
            try { return AhoChatA.MusicActivity.PlaceholderBitmap(small); }
            catch { return null; }
        }

        public static Bitmap Cached(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (_cache)
            {
                Bitmap b;
                if (_cache.TryGetValue(url, out b) && b != null && !b.IsRecycled) return b;
            }
            return null;
        }

        public static void Put(string url, Bitmap bmp)
        {
            if (string.IsNullOrEmpty(url) || bmp == null) return;
            lock (_cache) _cache[url] = bmp;
        }

        /// <summary>把远程封面下载下来并回填 ImageView；已经缓存过就直接设置。</summary>
        public static void LoadInto(ImageView iv, Song s, int px = 128)
        {
            var url = Url(s);
            if (iv == null || string.IsNullOrEmpty(url)) return;

            var hit = Cached(url);
            if (hit != null)
            {
                try { iv.SetImageBitmap(hit); iv.SetScaleType(ImageView.ScaleType.CenterCrop); } catch { }
                return;
            }

            try { iv.SetImageBitmap(Placeholder(true)); } catch { }
            iv.Tag = url;
            lock (_loading) { if (!_loading.Add(url)) return; }

            System.Threading.Tasks.Task.Run(() =>
            {
                Bitmap bmp = null;
                try
                {
                    var conn = (Java.Net.HttpURLConnection)new Java.Net.URL(url).OpenConnection();
                    conn.ConnectTimeout = 8000;
                    conn.ReadTimeout = 8000;
                    conn.DoInput = true;
                    using (var src = conn.InputStream)
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[8192];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        // 在线封面尺寸不可控：先量尺寸再按 1024 上限采样解码，避免大图 OOM
                        ms.Position = 0;
                        var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
                        BitmapFactory.DecodeStream(ms, null, probe);
                        int sample = 1;
                        while (probe.OutWidth > 0 && probe.OutWidth / (sample * 2) >= 1024) sample *= 2;
                        ms.Position = 0;
                        bmp = BitmapFactory.DecodeStream(ms, null, new BitmapFactory.Options { InSampleSize = sample });
                    }
                    if (bmp != null) Put(url, bmp);
                }
                catch { }
                finally { lock (_loading) _loading.Remove(url); }

                if (bmp == null) return;
                var captured = iv;
                captured.Post(() =>
                {
                    try
                    {
                        var tag = captured.Tag != null ? captured.Tag.ToString() : "";
                        if (tag == url && !bmp.IsRecycled)
                        {
                            captured.SetImageBitmap(bmp);
                            captured.SetScaleType(ImageView.ScaleType.CenterCrop);
                        }
                    }
                    catch { }
                });
            });
        }
    }
}

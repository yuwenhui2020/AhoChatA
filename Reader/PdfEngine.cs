// ============================================================
// 阅读模块 · PDF 逐页渲染引擎
//
// 转译自 Areada 1.1.4  reader/pdf/PdfPageRenderer.kt（Kotlin → C#/Xamarin.Android）
// 原始实现：app.areada.reader.pdf.PdfPageRenderer
//
// 关键行为（与用户要求一致）：
//   · 用 Android 系统 android.graphics.pdf.PdfRenderer，一页渲染成一张 Bitmap；
//   · 一页就是一页，绝不把多页拼成一条长图，也不做文本重排；
//   · 渲染尺寸按「目标宽度 × 倍率」计算，并受最大宽度 / 最大像素数双重约束，
//     避免大开本 PDF 直接 OOM（对应 Areada 的 createPdfBitmapTarget）；
//   · 白底先擦再画，避免透明区域在深色主题下发黑。
//
// Areada 里为 Android 15+ / S 扩展准备的 Modern/Extension 后端在
// Xamarin.Android（compileSdk 33）里没有对应绑定，这里统一走 Legacy 后端，
// 使用系统 PdfRenderer + RENDER_MODE_FOR_DISPLAY，功能等价。
// ============================================================
using System;
using System.IO;
using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;

namespace AhoChatA.Reader
{
    public sealed class PdfEngine : IDisposable
    {
        // —— 与 Areada PdfPageRenderer.kt 顶部常量对齐 ——
        private const float BaseRenderScale = 1.5f;          // PdfRenderScale
        private const int MaxRenderWidthPx = 3200;           // PdfMaxRenderWidthPx
        private const double MaxBitmapPixels = 8500000d;     // PdfMaxBitmapPixels
        private const int WhiteArgb = unchecked((int)0xFFFFFFFF);

        private readonly ParcelFileDescriptor _pfd;
        private readonly PdfRenderer _renderer;
        private readonly object _gate = new object();
        private bool _closed;

        public string Path { get; private set; }

        public PdfEngine(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException(L10n.T("PDF 文件不存在"), path);
            Path = path;
            ParcelFileDescriptor pfd = null;
            try
            {
                pfd = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly);
                if (pfd == null) throw new IOException("无法打开 PDF 文件描述符");
                _pfd = pfd;
                _renderer = new PdfRenderer(_pfd);
            }
            catch
            {
                try { pfd?.Close(); } catch { }
                throw;
            }
        }

        /// <summary>总页数（= 阅读器的章节数）。</summary>
        public int PageCount
        {
            get { lock (_gate) { return _closed ? 0 : _renderer.PageCount; } }
        }

        public static int CountPages(string path)
        {
            using (var e = new PdfEngine(path)) return e.PageCount;
        }

        /// <summary>某页的原始宽高比（PDF point，1/72 英寸）。</summary>
        public double PageAspect(int index)
        {
            lock (_gate)
            {
                CheckOpen();
                if (index < 0 || index >= _renderer.PageCount) return 1.4;
                var page = _renderer.OpenPage(index);
                try { return page.Width <= 0 ? 1.4 : page.Height / (double)page.Width; }
                finally { try { page.Close(); } catch { } }
            }
        }

        /// <summary>
        /// 把第 index 页单独渲染成一张 Bitmap（调用方负责 Recycle/Dispose）。
        /// widthPx 是显示区域宽度；scale 是缩放档（1 / 1.5 / 2 / 3，见 PdfCache.ScaleBucket）。
        /// 对应 Areada：renderPage(pageIndex, widthPx, renderScale)。
        /// </summary>
        public Bitmap RenderPage(int index, int widthPx, float scale = 1f)
        {
            lock (_gate)
            {
                CheckOpen();
                if (index < 0 || index >= _renderer.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(index));

                var page = _renderer.OpenPage(index);
                try
                {
                    int w, h;
                    TargetSize(page.Width, page.Height, widthPx, scale, out w, out h);
                    var bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
                    if (bmp == null) throw new OutOfMemoryException("无法创建 PDF 页位图");
                    bmp.EraseColor(WhiteArgb);
                    try
                    {
                        page.Render(bmp, new Rect(0, 0, w, h), null, PdfRenderMode.ForDisplay);
                    }
                    catch
                    {
                        bmp.Recycle();
                        bmp.Dispose();
                        throw;
                    }
                    return bmp;
                }
                finally { try { page.Close(); } catch { } }
            }
        }

        /// <summary>渲染成 JPEG 字节，供封面缓存 / 网页端使用。</summary>
        public byte[] RenderJpeg(int index, int widthPx, int quality = 86, float scale = 1f)
        {
            using (var bmp = RenderPage(index, widthPx, scale))
            {
                using (var ms = new MemoryStream())
                {
                    if (!bmp.Compress(Bitmap.CompressFormat.Jpeg, quality, ms))
                        throw new IOException(L10n.T("PDF 页 JPEG 编码失败"));
                    return ms.ToArray();
                }
            }
        }

        /// <summary>Areada createPdfBitmapTarget 的宽度/像素收敛算法。</summary>
        private static void TargetSize(int pageW, int pageH, int widthPx, float scale, out int w, out int h)
        {
            double aspect = pageW <= 0 ? 1.4 : pageH / (double)pageW;
            if (aspect < 0.05) aspect = 0.05;
            if (aspect > 20) aspect = 20;

            int safeW = Math.Max(widthPx, 1);
            float s = Math.Max(1f, Math.Min(3f, scale));
            int targetW = (int)Math.Round(safeW * BaseRenderScale * s, MidpointRounding.AwayFromZero);
            targetW = Math.Max(safeW, Math.Min(targetW, MaxRenderWidthPx));

            int pixelCap = (int)Math.Round(Math.Sqrt(MaxBitmapPixels / aspect), MidpointRounding.AwayFromZero);
            pixelCap = Math.Max(1, pixelCap);
            w = Math.Max(1, Math.Min(targetW, pixelCap));
            h = Math.Max(1, (int)Math.Round(w * aspect, MidpointRounding.AwayFromZero));
        }

        private void CheckOpen()
        {
            if (_closed) throw new ObjectDisposedException(nameof(PdfEngine));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                try { _renderer?.Close(); } catch { }
                try { _pfd?.Close(); } catch { }
            }
        }
    }
}

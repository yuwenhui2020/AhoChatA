using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Pdf;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 把多张图片按 A4 纵向合成一个 PDF 文件。
    ///
    /// 用系统自带的 android.graphics.pdf.PdfDocument（API 19+，无需第三方库）。
    /// 每张图逐张解码、按 inSampleSize 采样到长边不超过 2000px（A4 150dpi 足够），
    /// 画完立即 Recycle，避免多图批量合成时 OOM；页边距 36pt，等比居中不裁剪。
    /// </summary>
    public static class ImagePdfExporter
    {
        private const int PageWidth = 595;   // A4 宽（pt，72dpi）
        private const int PageHeight = 842;  // A4 高（pt）
        private const int Margin = 36;       // 页边距（pt）
        private const int MaxDecodeDim = 2000;
        private const string Tag = "ImgPdf";

        /// <summary>合成 PDF，返回输出文件路径。任何异常都会抛出，由调用方提示用户。</summary>
        public static string Export(ContentResolver resolver, IList<Android.Net.Uri> uris, string outputPath)
        {
            if (resolver == null || uris == null || uris.Count == 0) throw new ArgumentException("没有可导出的图片");

            using (var doc = new PdfDocument())
            {
                int pageNo = 1;
                foreach (var uri in uris)
                {
                    Bitmap bmp = null;
                    try
                    {
                        // 第一次：只读尺寸
                        var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
                        using (var s0 = resolver.OpenInputStream(uri))
                        {
                            if (s0 == null) continue;
                            BitmapFactory.DecodeStream(s0, null, probe);
                        }
                        if (probe.OutWidth <= 0 || probe.OutHeight <= 0) continue;

                        int sample = 1;
                        while (Math.Max(probe.OutWidth, probe.OutHeight) / (sample * 2) >= MaxDecodeDim) sample *= 2;
                        var opts = new BitmapFactory.Options { InSampleSize = sample };
                        using (var s1 = resolver.OpenInputStream(uri))
                        {
                            if (s1 == null) continue;
                            bmp = BitmapFactory.DecodeStream(s1, null, opts);
                        }
                        if (bmp == null) continue;

                        var pageInfo = new PdfDocument.PageInfo.Builder(PageWidth, PageHeight, pageNo).Create();
                        var page = doc.StartPage(pageInfo);
                        var canvas = page.Canvas;
                        canvas.DrawColor(Color.White);

                        float availW = PageWidth - Margin * 2f;
                        float availH = PageHeight - Margin * 2f;
                        float scale = Math.Min(availW / bmp.Width, availH / bmp.Height);
                        float w = bmp.Width * scale, h = bmp.Height * scale;
                        float x = (PageWidth - w) / 2f, y = (PageHeight - h) / 2f;
                        var src = new Rect(0, 0, bmp.Width, bmp.Height);
                        var dst = new Rect((int)x, (int)y, (int)(x + w), (int)(y + h));
                        canvas.DrawBitmap(bmp, src, dst, null);
                        doc.FinishPage(page);
                        pageNo++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(Tag, "跳过一张图片：" + ex.Message);
                    }
                    finally
                    {
                        try { bmp?.Recycle(); } catch { }
                    }
                }

                if (pageNo <= 1) throw new InvalidOperationException(L10n.T("没有可写入的图片（可能图片格式不支持或授权已失效）"));
                using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    doc.WriteTo(fs);
                    fs.Flush();
                }
                return outputPath;
            }
        }
    }
}

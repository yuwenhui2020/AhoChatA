using System;
using System.Collections.Generic;
using System.IO;
using Android.Media;
using Java.Nio;

namespace AhoChatA.Vod
{
    /// <summary>
    /// 内置的 TS→MP4 合并器：基于 Android 框架的 MediaExtractor / MediaMuxer，
    /// 不依赖任何外部 ffmpeg 二进制。把若干 HLS 切片(.ts) 重新封装为单个 .mp4，
    /// 音视频流格式不变（H.264 / AAC 直接 copy，不重编码），速度无损。
    ///
    /// 这是「下载在线视频」功能的核心：先把某集的所有 .ts 切片下载到临时目录，
    /// 全部就绪后调用 Merge 合并为 &lt;标题&gt;.mp4，再删掉切片。
    /// </summary>
    public static class TsToMp4
    {
        /// <summary>
        /// 合并一组 .ts 切片为单个 mp4。成功返回 true（outMp4 已生成）；失败返回 false。
        /// tsFiles 顺序即为最终 mp4 中的时间顺序。
        /// </summary>
        public static bool Merge(List<string> tsFiles, string outMp4, Action<string> log = null)
        {
            if (tsFiles == null || tsFiles.Count == 0) { log?.Invoke("无切片可合并"); return false; }
            tsFiles.RemoveAll(f => !System.IO.File.Exists(f));
            if (tsFiles.Count == 0) { log?.Invoke("切片文件不存在"); return false; }

            MediaFormat vFmt = null, aFmt = null;
            string vMime = null, aMime = null;

            // 1) 先扫描第一段确定轨道格式（HLS 各段格式一致）
            try
            {
                using (var probe = new MediaExtractor())
                {
                    probe.SetDataSource(tsFiles[0]);
                    for (int i = 0; i < probe.TrackCount; i++)
                    {
                        var f = probe.GetTrackFormat(i);
                        string mime = f.GetString(MediaFormat.KeyMime);
                        if (vFmt == null && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                        { vFmt = f; vMime = mime; }
                        else if (aFmt == null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                        { aFmt = f; aMime = mime; }
                    }
                }
            }
            catch (Exception ex) { log?.Invoke(L10n.T("探测轨道失败: ") + ex.Message); return false; }

            if (vFmt == null && aFmt == null) { log?.Invoke("无可用音视频轨道"); return false; }

            // 2) 建 muxer、加轨道、逐段写样本
            Java.IO.FileOutputStream fos = null;
            MediaMuxer muxer = null;
            try
            {
                fos = new Java.IO.FileOutputStream(outMp4);
                muxer = new MediaMuxer(fos.FD, MuxerOutputType.Mpeg4);
                int vMux = -1, aMux = -1;
                if (vFmt != null) vMux = muxer.AddTrack(vFmt);
                if (aFmt != null) aMux = muxer.AddTrack(aFmt);
                muxer.Start();

                long lastV = -1, lastA = -1;     // 各轨道已写入的最大时间戳（保证严格单调递增）
                foreach (var seg in tsFiles)
                {
                    using (var ex = new MediaExtractor())
                    {
                        ex.SetDataSource(seg);
                        int vTrack = -1, aTrack = -1;
                        for (int i = 0; i < ex.TrackCount; i++)
                        {
                            var f = ex.GetTrackFormat(i);
                            string mime = f.GetString(MediaFormat.KeyMime);
                            if (vTrack < 0 && mime == vMime) vTrack = i;
                            else if (aTrack < 0 && mime == aMime) aTrack = i;
                        }
                        if (vTrack >= 0)
                        {
                            ex.SelectTrack(vTrack);
                            ex.SeekTo(0, MediaExtractorSeekTo.PreviousSync);
                            WriteTrack(ex, muxer, vMux, ref lastV);
                            ex.UnselectTrack(vTrack);
                        }
                        if (aTrack >= 0)
                        {
                            ex.SelectTrack(aTrack);
                            ex.SeekTo(0, MediaExtractorSeekTo.PreviousSync);
                            WriteTrack(ex, muxer, aMux, ref lastA);
                            ex.UnselectTrack(aTrack);
                        }
                    }
                }
                muxer.Stop();
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke(L10n.T("合并失败: ") + ex.Message);
                try { if (System.IO.File.Exists(outMp4)) System.IO.File.Delete(outMp4); } catch { }
                return false;
            }
            finally
            {
                try { muxer?.Release(); } catch { }
                try { fos?.Close(); } catch { }
            }
        }

        private static void WriteTrack(MediaExtractor ex, MediaMuxer muxer, int muxIdx, ref long lastUs)
        {
            var info = new MediaCodec.BufferInfo();
            var buf = ByteBuffer.Allocate(4 * 1024 * 1024);
            try
            {
                while (true)
                {
                    int n = ex.ReadSampleData(buf, 0);
                    if (n < 0) break;
                    long pts = ex.SampleTime;
                    if (pts < 0) pts = 0;
                    // MediaMuxer 要求同一轨道时间戳严格单调递增；跨段时强制 +1 兜底
                    if (pts <= lastUs) pts = lastUs + 1;
                    lastUs = pts;

                    info.Offset = 0;
                    info.Size = n;
                    info.PresentationTimeUs = pts;
                    var flags = ex.SampleFlags;
                    info.Flags = ((flags & MediaExtractorSampleFlags.Sync) != 0)
                        ? MediaCodecBufferFlags.KeyFrame
                        : MediaCodecBufferFlags.None;

                    buf.Position(0);
                    muxer.WriteSampleData(muxIdx, buf, info);
                    if (!ex.Advance()) break;
                }
            }
            finally
            {
                try { buf.Dispose(); } catch { }
                try { info.Dispose(); } catch { }
            }
        }
    }
}

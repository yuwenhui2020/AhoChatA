using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 收到的聊天媒体（文本文档 / PDF / 音乐 / 图片 / 视频）自动处理协调器。
    ///
    /// 行为（用户需求）：
    ///   1) 收到这类媒体（不管是「分享到频道」的 mlib 内部链接，还是直接发的文件附件）
    ///      都自动加载进本机媒体库，并切换到本程序前台、自动播放 / 自动显示；
    ///   2) 批量发送（短时间内连收多个）则忽略自动显示——检测窗口内若收到第二个文件，
    ///      取消正在排队的「自动打开」任务，只静默入库；
    ///   3) 所有收到的通知都走系统通知栏（见 MchatBridge 统一调用 ChatNotifier），不只用 Toast。
    ///
    /// 实现要点：
    ///   - 收到第一个媒体后启动一个检测窗口（WindowMs）；窗口内若没有第二个媒体到来，
    ///     窗口结束时执行「入库 + 前台自动播放」；若窗口内来了第二个，则判定为批量，
    ///     取消自动打开任务，仅把全部媒体入库。
    ///   - 自动播放统一用文件在应用私有 media 目录里的真实路径（一定存在且本应用可读取），
    ///     各媒体 Activity 新增了「按路径直接打开 / 播放」的入口（Extra*），不依赖库扫描是否完成。
    /// </summary>
    public static class MediaAutoOpen
    {
        /// <summary>检测窗口：收到第一个媒体后等待这么久，若期间无第二个媒体才自动打开。</summary>
        private const int WindowMs = 1500;

        private static readonly object Gate = new object();
        private sealed class Pending
        {
            public Context Ctx;
            public string Kind;        // book / music / photo / video
            public string Name;
            public string SrcPath;     // 应用私有 media 目录里的真实文件
            public Timer Timer;
        }
        // 队列里只保留「当前这一个」；一旦来第二个就判定批量并清空
        private static Pending _current;
        // 批量判定后，这段时间内的任何媒体都静默入库、不再自动打开（防止 3 个以上仍误触发）
        private static long _batchUntilTicks;

        /// <summary>
        /// 由入站消息处理（MchatBridge.IngestIncoming / qaul 文件接收）在确认收到一条媒体消息后调用。
        /// </summary>
        /// <param name="ctx">ApplicationContext（从后台服务启动 Activity 必须用它）</param>
        /// <param name="kind">媒体类别（book/music/photo/video）</param>
        /// <param name="displayName">原始文件名（入库/展示用）</param>
        /// <param name="srcPath">应用私有 media 目录中的真实文件路径</param>
        public static void OnMedia(Context ctx, string kind, string displayName, string srcPath)
        {
            if (ctx == null || string.IsNullOrEmpty(kind)) return;
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return;

            var appCtx = ctx.ApplicationContext ?? ctx;
            var name = string.IsNullOrEmpty(displayName) ? Path.GetFileName(srcPath) : displayName;

            Pending toImportSilently = null;
            lock (Gate)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now < _batchUntilTicks)
                {
                    // 已进入批量窗口：这条也静默入库，绝不自动打开
                    toImportSilently = new Pending { Ctx = appCtx, Kind = kind, Name = name, SrcPath = srcPath };
                }
                else if (_current == null)
                {
                    // 第一个媒体：排队，启动检测窗口
                    _current = new Pending
                    {
                        Ctx = appCtx,
                        Kind = kind,
                        Name = name,
                        SrcPath = srcPath
                    };
                    _current.Timer = new Timer(OnWindowElapsed, null, WindowMs, Timeout.Infinite);
                }
                else
                {
                    // 第二个媒体：判定为批量 —— 取消自动打开，前一个 + 这一个都静默入库，
                    // 并开启批量窗口，让后续连发的也都静默入库。
                    var batch = _current;
                    _current = null;
                    try { batch.Timer?.Dispose(); } catch { }
                    toImportSilently = batch;
                    LoadIntoLibrary(new Pending { Ctx = appCtx, Kind = kind, Name = name, SrcPath = srcPath });
                    _batchUntilTicks = DateTime.UtcNow.AddMilliseconds(WindowMs).Ticks;
                }
            }
            // 在锁外执行（导入有文件 IO，避免阻塞其它线程）
            if (toImportSilently != null) LoadIntoLibrary(toImportSilently);
        }

        private static void OnWindowElapsed(object state)
        {
            Pending job;
            lock (Gate)
            {
                job = _current;
                _current = null;
                if (job != null) { try { job.Timer?.Dispose(); } catch { } job.Timer = null; }
            }
            if (job == null) return;

            try
            {
                // 1) 先入库（落到 /sdcard/file/&lt;类型&gt; 并加入对应媒体库）
                LoadIntoLibrary(job);
                // 2) 只有屏幕本来就亮着才切前台自动播放。
                //    息屏时启动 Activity 会把用户的屏幕点亮 —— 用户明确要求「不要自己一次次唤醒屏幕」，
                //    所以息屏状态下只静默入库 + 通知栏提示，等用户自己点开。
                if (!ScreenOn())
                {
                    Android.Util.Log.Info("MediaAuto", "屏幕已息，仅入库不自动打开：" + job.Name);
                    return;
                }
                // 3) 切前台 + 自动播放 / 自动显示
                AutoPlay(job);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaAuto", "自动打开失败: " + ex.Message);
            }
        }

        /// <summary>把媒体文件入库：复制到 /sdcard/file/&lt;类型&gt; 并加入对应媒体库（幂等）。</summary>
        private static void LoadIntoLibrary(Pending p)
        {
            try { MediaShare.ImportReceived(p.Ctx, p.Kind, p.Name, p.SrcPath); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaAuto", "入库失败: " + ex.Message);
            }
        }

        /// <summary>屏幕是否处于点亮/解锁可交互状态。拿不到时按「亮着」处理，避免误伤正常流程。</summary>
        private static bool ScreenOn()
        {
            try
            {
                var ctx = Android.App.Application.Context;
                if (ctx == null) return true;
                var pm = (PowerManager)ctx.GetSystemService(Context.PowerService);
                return pm == null || pm.IsInteractive;
            }
            catch { return true; }
        }

        /// <summary>切到前台并自动播放 / 自动显示对应的媒体。</summary>
        private static void AutoPlay(Pending p)
        {
            try
            {
                Intent i;
                switch (p.Kind)
                {
                    case "book":
                        i = new Intent(p.Ctx, typeof(ReaderActivity));
                        i.PutExtra(ReaderActivity.ExtraBook, p.SrcPath);
                        i.PutExtra(ReaderActivity.ExtraBookName, p.Name);
                        break;
                    case "music":
                        i = new Intent(p.Ctx, typeof(MusicActivity));
                        i.PutExtra(MusicActivity.ExtraPath, p.SrcPath);
                        break;
                    case "photo":
                        i = new Intent(p.Ctx, typeof(PhotoActivity));
                        i.PutExtra(PhotoActivity.ExtraPath, p.SrcPath);
                        break;
                    default: // video
                        i = new Intent(p.Ctx, typeof(VideoActivity));
                        i.PutExtra(VideoActivity.ExtraPath, p.SrcPath);
                        break;
                }
                i.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
                p.Ctx.StartActivity(i);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MediaAuto", "启动播放器失败: " + ex.Message);
            }
        }
    }
}

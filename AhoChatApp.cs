using Android.App;
using Android.Runtime;
using System;
using Android.Util;

namespace AhoChatA
{
    /// <summary>
    /// 进程级入口：把最耗时的一步（Bridge 构造，实测 290ms）提前到**进程启动时**就开始，
    /// 与 Activity 的创建并行，等 ChatActivity.OnCreate 跑到这里时 Bridge 已经建好。
    ///
    /// 这是「预加载界面」在数据侧的对偶做法：
    ///   · windowBackground（splash_bg）解决「视觉上立刻有反馈」；
    ///   · 这里解决「Activity 一上来就有数据可渲染」。
    /// 两者叠加，冷启动的主观感受接近秒开。
    ///
    /// 注意：Application.onCreate 在主线程执行，所以只做「必须同步」的部分（Bridge），
    /// 服务（WebServer / 端口 / 发现）仍旧由 CoreHost 在后台线程拉起。
    /// </summary>
    [Application]
    public class AhoChatApp : Application
    {
        public AhoChatApp(IntPtr handle, JniHandleOwnership transfer)
            : base(handle, transfer)
        {
        }

        public override void OnCreate()
        {
            base.OnCreate();
            Perf.Mark("Application.OnCreate");
            try
            {
                // 只预建 Bridge：聊天列表、身份、会话仓库都要它，但不起任何服务
                CoreHost.Preinit(this);
                // 设置下载中心上下文，用于通知栏进度显示
                AhoChatA.Vod.DownloadCenter.AppContext = this;
            }
            catch (System.Exception ex)
            {
                Log.Warn("Mchat", "Application 预初始化失败（退回 Activity 内同步创建）：" + ex.Message);
            }
            // 非持久的视频缓冲缓存：进程启动即清空，关闭程序后不残留。
            // 目录遍历 + 递归删除放到后台，别占着 UI 主线程拖慢首帧。
            System.Threading.Tasks.Task.Run(() =>
            {
                try { AhoChatA.Vod.VodConfig.EnsureBufferCleared(); } catch { }
                // 曲库预加载：主界面 / 聊天页 / 阅读页的底栏在「没进过影音」时也要有张待播卡片，
                // 后台先读好 library.json 并挑好曲目，界面一出现就是完整的一行。
                try
                {
                    AhoChatA.Music.MusicLibrary.EnsureLoaded(this);
                    AhoChatA.Music.MusicPlayer.EnsurePending();
                    // 耳机/蓝牙断开监听：进程启动即注册，蓝牙耳机断开或有线耳机拔出时暂停音乐。
                    // 只作用于音乐引擎，不影响视频播放。
                    AhoChatA.Music.MusicPlayer.InitNoiseListener();
                }
                catch { }
            });
            Perf.Mark("Application.OnCreate 完成");
        }
    }
}

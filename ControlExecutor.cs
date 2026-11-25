using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
// Android.Widget.MediaController 与 Android.Media.Session.MediaController 同名，
// 本类要用的始终是后者（控制外部播放器的媒体会话），故取别名消除歧义。
using MediaController = Android.Media.Session.MediaController;

namespace AhoChatA
{
    /**
     * 功能控制执行器。
     *
     * 当本机「接收到」形如 (命令) 的消息（圆括号包裹且括号内不含其他圆括号、
     * 括号外无其它内容）时，视为功能控制指令并模拟对应输入。
     *
     * 设计要点：
     *   - 媒体控制（播放/暂停/上一曲/下一曲）走系统「当前活动媒体会话」
     *     (MediaSessionManager.GetActiveSessions)，效果等同于蓝牙耳机的媒体按键，
     *     可控制手机上任意正在播放的 App（无需 INJECT_EVENTS 权限，因为本应用已具备
     *     通知监听服务，授权后可读取活动媒体会话）。
     *   - 音量加/减/静音走 AudioManager（全局生效）。静音 = 音乐流音量置 0。
     *   - 全屏放映/退出/上一页/下一页/截屏走按键注入（Instrumentation）或系统截图，
     *     这些等同于蓝牙翻页笔/键盘按键，作用于前台 App；若设备未授予输入注入权限
     *     则退化为对本 App 自身执行（如切换播放器全屏、滚动聊天列表）。
     *
     * 注意：指令只在「接收侧」执行；发送方不会被自己发出的指令控制。
     */
    public sealed class ControlExecutor
    {
        // 匹配：整条消息被一对圆括号包裹，括号内不含圆括号，括号外无其它字符
        private static readonly System.Text.RegularExpressions.Regex CmdRegex =
            new System.Text.RegularExpressions.Regex(@"^\(([^()]*)\)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly Context context;
        private readonly ComponentName notifListener;
        private int lastMusicVolume = -1;   // 静音前音量，用于取消静音恢复

        public ControlExecutor(Context context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            try { notifListener = new ComponentName(context, Java.Lang.Class.FromType(typeof(NotificationRelayService))); }
            catch { notifListener = null; }
        }

        /// <summary>判断一条消息文本是否为功能控制指令；是则返回命令名，否则返回 null。</summary>
        public static string ExtractCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = CmdRegex.Match(text);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>执行功能控制指令（命令名已提取，可为空 -> 忽略）。</summary>
        public void Exec(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            var key = Normalize(cmd);
            try
            {
                switch (key)
                {
                    // ---- 音量（全局） ----
                    case "volup":
                    case "音量加":
                        AdjustVolume(Adjust.Raise);
                        break;
                    case "voldown":
                    case "音量减":
                        AdjustVolume(Adjust.Lower);
                        break;
                    case "mute":
                    case "静音":
                        ToggleMute();
                        break;

                    // ---- 媒体控制（等效蓝牙耳机按键）----
                    // 先试系统媒体会话（准确、可指定正在播放的那个 App），
                    // 拿不到会话（通知使用权未授权 / 播放器不注册会话）时自动回退为
                    // 媒体键注入 —— 后者由系统路由给当前播放器，几乎总有效，
                    // 不会再出现「明明在放歌却提示未在播放媒体」。
                    case "play":
                    case "播放":
                        MediaAction(Keycode.MediaPlay, c => c.Play());
                        break;
                    case "pause":
                    case "暂停":
                        MediaAction(Keycode.MediaPause, c => c.Pause());
                        break;
                    case "playpause":
                    case "播放暂停":
                        MediaToggle();
                        break;
                    case "prev":
                    case "上一曲":
                        MediaAction(Keycode.MediaPrevious, c => c.SkipToPrevious());
                        break;
                    case "next":
                    case "下一曲":
                        MediaAction(Keycode.MediaNext, c => c.SkipToNext());
                        break;

                    default:
                        Toast(L10n.T("未知功能控制：") + cmd);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                try { Android.Util.Log.Error("Mchat", "功能控制执行失败: " + ex.Message); } catch { }
            }
        }

        /* ===================== 音量 ===================== */

        private void AdjustVolume(Adjust direction)
        {
            var am = (AudioManager)context.GetSystemService(Context.AudioService);
            if (am == null) return;
            am.AdjustVolume(direction, VolumeNotificationFlags.ShowUi | VolumeNotificationFlags.PlaySound);
        }

        private void ToggleMute()
        {
            var am = (AudioManager)context.GetSystemService(Context.AudioService);
            if (am == null) return;
            Android.Media.Stream stream = Android.Media.Stream.Music;
            int cur = am.GetStreamVolume(stream);
            if (cur > 0)
            {
                lastMusicVolume = cur;
                am.SetStreamVolume(stream, 0, VolumeNotificationFlags.ShowUi);
                Toast(L10n.T("已静音"));
            }
            else
            {
                int restore = lastMusicVolume > 0 ? lastMusicVolume : System.Math.Max(1, am.GetStreamMaxVolume(stream) / 2);
                am.SetStreamVolume(stream, restore, VolumeNotificationFlags.ShowUi);
                Toast(L10n.T("已取消静音"));
            }
        }

        /* ===================== 媒体控制（活动会话） ===================== */

        /// <summary>
        /// 执行一个媒体动作：优先作用于「正在播放」的系统媒体会话；
        /// 读不到会话（通知使用权未授权、或播放器不注册会话，如网页/内置播放器）时，
        /// 退化为向系统注入媒体键 —— 效果等同按蓝牙耳机按键，由系统路由给当前播放器。
        /// </summary>
        private void MediaAction(Keycode fallbackKey, Action<MediaController.TransportControls> sessionAction)
        {
            var sessions = GetActiveSessions();
            if (sessions != null && sessions.Count > 0)
            {
                MediaController target = null;
                foreach (var item in sessions)
                {
                    try
                    {
                        if (item.PlaybackState != null && item.PlaybackState.State == PlaybackStateCode.Playing)
                        { target = item; break; }
                    }
                    catch { }
                }
                target = target ?? sessions[0];
                try
                {
                    sessionAction(target.GetTransportControls());
                    Log(L10n.T("媒体会话直控 ") + target.PackageName);
                    return;
                }
                catch (System.Exception ex) { Log(L10n.T("媒体会话控制失败，回退媒体键：") + ex.Message); }
            }
            MediaKey(fallbackKey);
        }

        /// <summary>注入一次媒体键（按下 + 抬起），等同于蓝牙耳机/耳机线控按键。</summary>
        private void MediaKey(Keycode code)
        {
            try
            {
                var am = (AudioManager)context.GetSystemService(Context.AudioService);
                if (am == null) { Toast(L10n.T("无法访问音频服务")); return; }
                long now = SystemClock.UptimeMillis();
                am.DispatchMediaKeyEvent(new KeyEvent(now, now, KeyEventActions.Down, code, 0));
                am.DispatchMediaKeyEvent(new KeyEvent(now, now, KeyEventActions.Up, code, 0));
                Log(L10n.T("媒体键注入 ") + code);
            }
            catch (System.Exception ex)
            {
                Log(L10n.T("媒体键注入失败：") + ex.Message);
                Toast(L10n.T("媒体控制失败，请检查是否有正在播放的媒体"));
            }
        }

        /// <summary>
        /// 取得当前活动媒体会话列表，并剔除本应用自己创建的会话。
        ///
        /// 本应用为「跨设备播放控制」常驻通知会自建一个 MediaSession（RemoteMediaBridge），
        /// 它并不真正播放声音，只是占用播放通道。若不剔除，媒体控制指令（上一曲/下一曲/
        /// 播放暂停）会作用于这个假会话，导致真正的播放器收不到指令 —— 表现为
        /// 「点通知栏不会切歌/暂停」。
        /// </summary>
        private IList<MediaController> GetExternalActiveSessions(MediaSessionManager msm)
        {
            IList<MediaController> raw;
            try
            {
                raw = notifListener != null
                    ? msm.GetActiveSessions(notifListener)
                    : msm.GetActiveSessions(null);
            }
            catch
            {
                return null;
            }
            if (raw == null) return null;

            var selfPkg = context.PackageName;
            var result = new List<MediaController>();
            foreach (var s in raw)
            {
                try
                {
                    // 跳过本应用自己的会话（假播放占用）
                    if (s.PackageName != null && s.PackageName.Equals(selfPkg, StringComparison.Ordinal))
                        continue;
                }
                catch { }
                result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// 取得当前活动媒体会话列表（已剔除本应用自身会话）；无权限/无会话时返回 null 或空列表。
        /// </summary>
        private IList<MediaController> GetActiveSessions()
        {
            var msm = (MediaSessionManager)context.GetSystemService(Context.MediaSessionService);
            if (msm == null) return null;
            return GetExternalActiveSessions(msm);
        }

        /// <summary>
        /// 合并的播放/暂停：由接收方根据自身媒体状态决定 ——
        /// 有正在播放的会话就全部暂停，否则全部播放；
        /// 一个会话都读不到（最常见：通知使用权没给）时注入 PLAY_PAUSE 媒体键。
        /// </summary>
        private void MediaToggle()
        {
            var sessions = GetActiveSessions();
            if (sessions == null || sessions.Count == 0)
            {
                MediaKey(Keycode.MediaPlayPause);
                return;
            }

            bool playing = false;
            foreach (var s in sessions)
            {
                try
                {
                    if (s.PlaybackState != null && s.PlaybackState.State == PlaybackStateCode.Playing)
                    { playing = true; break; }
                }
                catch { }
            }

            foreach (var s in sessions)
            {
                try
                {
                    var tc = s.GetTransportControls();
                    if (playing) tc.Pause(); else tc.Play();
                }
                catch (System.Exception ex) { Log(L10n.T("媒体指令失败：") + ex.Message); }
            }
        }

        private void Log(string msg)
        {
            try { Android.Util.Log.Info("Mchat", "[Media] " + msg); } catch { }
        }

        private void Toast(string msg)
        {
            if (context is Activity act)
                act.RunOnUiThread(() =>
                    L10n.Toast(context, msg, Android.Widget.ToastLength.Short).Show());
            else
                L10n.Toast(context, msg, Android.Widget.ToastLength.Short).Show();
        }

        /// <summary>把命令归一化：去掉首尾空白；ASCII 部分转小写，中文保持原样。</summary>
        private static string Normalize(string cmd)
        {
            var s = cmd.Trim();
            bool isAscii = true;
            foreach (var c in s)
                if (c > 127) { isAscii = false; break; }
            return isAscii ? s.ToLowerInvariant() : s;
        }
    }
}

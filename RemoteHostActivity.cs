using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media.Projection;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using DeskShare;

namespace AhoChatA
{
    /// <summary>
    /// 「允许被远控」页：让本机成为一台可被 Windows / 安卓 / 网页控制的设备。
    ///
    /// 三件事必须由用户亲手做，且都在本页给出入口：
    ///   1. 开启无障碍服务 —— 没有它只能看画面、点不动（系统权限，无法代劳）；
    ///   2. 授予屏幕录制 —— 每次开机/每次开启都要重新授权（系统安全限制）；
    ///   3. 记住设备码 + 连接密码 —— 控制端要用这两个值登录。
    ///
    /// 界面全部代码构建：与 RemoteDeskActivity 保持同一套配色与卡片风格，
    /// 不再出现"一半原生一半系统对话框"的割裂感。
    /// </summary>
    [Activity(Label = "@string/act_remotehost", Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.KeyboardHidden)]
    public class RemoteHostActivity : BaseActivity
    {
        private const int ReqProjection = 2101;
        private const int ReqPickFile = 2102;

        private Button sendFileBtn;

        private TextView statusBig;
        private TextView statusSub;
        private TextView accState;
        private Button powerBtn;
        private LinearLayout infoCard;
        private int preset = 1;   // 0 流畅 / 1 均衡 / 2 高清

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(new Color(IosStyle.Background));
            SetContentView(root);

            root.AddView(BuildTopBar());

            var scroll = new ScrollView(this);
            root.AddView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.SetPadding(Dp(IosStyle.PageInset), 0, Dp(IosStyle.PageInset), Dp(IosStyle.PageInset));
            scroll.AddView(body);

            BuildStatusCard(body);
            BuildAccessibilityCard(body);
            BuildInfoCard(body);
            BuildSendFileCard(body);
            BuildQualityCard(body);
            BuildHelpCard(body);

            // 服务与服务器的状态都回灌到界面
            RdHostService.StartResult += OnStartResult;
            RdHostServer.StatusChanged += OnStatus;

            // 每秒轮询一次：StatusChanged 只在服务端打点时才来，
            // 查看端连上/断开的瞬间不一定都能触发到（尤其是在后台被折叠时），
            // 轮询能保证"正在被远控"这个状态不会停在过期的"等待连接"上。
            var tick = new Handler(Looper.MainLooper);
            Action poll = null;
            poll = () =>
            {
                if (IsFinishing || IsDestroyed) return;
                RefreshStatus();
                tick.PostDelayed(new Java.Lang.Runnable(poll), 1000);
            };
            tick.PostDelayed(new Java.Lang.Runnable(poll), 1000);
        }

        protected override void OnResume()
        {
            base.OnResume();
            RefreshAccessibility();
            RefreshStatus();
            StartClipWatch();
        }

        protected override void OnPause()
        {
            StopClipWatch();
            base.OnPause();
        }

        /* ---------- 剪贴板：手机 → 电脑 ---------- */
        // Android 10+ 只允许"前台应用"读剪贴板，所以主动监听放在本页的 Resumed 期间，
        // 而不是扔到后台服务里（放后台既读不到，也会被系统判成隐私违规）。
        private Handler _clipHandler;
        private Action _clipPoll;

        private void StartClipWatch()
        {
            try
            {
                if (_clipHandler == null) _clipHandler = new Handler(Looper.MainLooper);
                _clipPoll = delegate
                {
                    try
                    {
                        var srv = RdHostServer.Instance;
                        if (srv.IsRunning && srv.HasViewer)
                        {
                            var cm = (Android.Content.ClipboardManager)
                                GetSystemService(Android.Content.Context.ClipboardService);
                            var clip = cm != null ? cm.PrimaryClip : null;
                            if (clip != null && clip.ItemCount > 0)
                            {
                                var item = clip.GetItemAt(0);
                                string t = item != null ? (item.Text ?? "") : "";
                                if (!string.IsNullOrEmpty(t)) srv.SendClipboardText(t);
                            }
                        }
                    }
                    catch { }
                    try { _clipHandler.PostDelayed(new Java.Lang.Runnable(_clipPoll), 1500); } catch { }
                };
                _clipHandler.PostDelayed(new Java.Lang.Runnable(_clipPoll), 1500);
            }
            catch { }
        }

        private void StopClipWatch()
        {
            try { if (_clipHandler != null) _clipHandler.RemoveCallbacksAndMessages(null); } catch { }
        }

        protected override void OnDestroy()
        {
            try { RdHostService.StartResult -= OnStartResult; } catch { }
            try { RdHostServer.StatusChanged -= OnStatus; } catch { }
            base.OnDestroy();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == ReqPickFile)
            {
                if (resultCode != Result.Ok || data == null || data.Data == null) return;
                try
                {
                    var uri = data.Data;
                    string name; long size;
                    ResolveNameAndSize(uri, out name, out size);
                    // 交给被控端：它会先把内容落地成缓存文件再发送（见 SendUriToViewer 注释）
                    bool ok = RdHostServer.Instance.SendUriToViewer(ContentResolver, uri, name);
                    if (!ok) ToastIt(L10n.T("当前没有控制端连接"));
                }
                catch (Exception ex) { ToastIt(L10n.T("发送失败：") + ex.Message); }
                return;
            }
            if (requestCode != ReqProjection) return;
            if (resultCode != Result.Ok || data == null)
            {
                SetStatus(L10n.T("已取消"), L10n.T("没有屏幕录制授权，无法开启被控"), IosStyle.Warning);
                return;
            }
            StartHostService(data);
        }

        /* ===================== 卡片 ===================== */

        private View BuildTopBar()
        {
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            IosStyle.StyleToolbar(bar);
            bar.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(56));

            var back = new TextView(this) { Text = "‹", TextSize = 26 };
            back.SetTextColor(new Color(IosStyle.Accent));
            back.SetTextSize(Android.Util.ComplexUnitType.Sp, 24);
            back.Gravity = GravityFlags.Center;
            back.SetPadding(0, 0, Dp(10), 0);
            back.SetMinWidth(Dp(44));
            back.SetMinHeight(Dp(44));
            back.Click += (s, e) => Finish();
            bar.AddView(back);

            var title = new TextView(this) { Text = L10n.T("允许被远控"), TextSize = 17 };
            IosStyle.StyleTitle(title);
            title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            bar.AddView(title);
            return bar;
        }

        private void BuildStatusCard(LinearLayout parent)
        {
            var card = Card_(parent, L10n.T("本机状态"));
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(IosStyle.PageInset), Dp(4), Dp(IosStyle.PageInset), Dp(16));

            statusBig = new TextView(this) { Text = L10n.T("未开启"), TextSize = IosStyle.TitleSize };
            statusBig.SetTypeface(null, TypefaceStyle.Bold);
            statusBig.SetTextColor(new Color(IosStyle.Text));
            box.AddView(statusBig);

            statusSub = new TextView(this) { Text = L10n.T("开启后，局域网内的电脑可以在「设备」页看到本机并远程控制"), TextSize = IosStyle.CaptionSize };
            statusSub.SetTextColor(new Color(IosStyle.Secondary));
            statusSub.SetPadding(0, Dp(6), 0, Dp(12));
            box.AddView(statusSub);

            powerBtn = new Button(this) { Text = L10n.T("开启被远控") };
            IosStyle.StylePrimaryButton(powerBtn);
            powerBtn.Click += (s, e) => OnPowerClick();
            box.AddView(powerBtn);

            card.AddView(box);
        }

        private void BuildAccessibilityCard(LinearLayout parent)
        {
            var card = Card_(parent, L10n.T("无障碍服务（远程点击/输入需要）"));
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.PageInset), Dp(8), Dp(IosStyle.PageInset), Dp(8));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            accState = new TextView(this) { Text = L10n.T("检测中…"), TextSize = IosStyle.BodySize };
            accState.SetTextColor(new Color(IosStyle.Text));
            accState.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            row.AddView(accState);

            var go = new TextView(this) { Text = L10n.T("去开启 ›"), TextSize = IosStyle.BodySize };
            go.SetTextColor(new Color(IosStyle.Accent));
            go.SetPadding(Dp(8), Dp(6), 0, Dp(6));
            go.Click += (s, e) => RdInputService.OpenSettings(this);
            row.AddView(go);

            card.AddView(row);
            var tip = new TextView(this)
            {
                Text = L10n.T("路径：设置 → 无障碍 → 已下载的服务 → AhoChat 远程输入。开启后本页无需重启即可生效。"),
                TextSize = IosStyle.CaptionSize
            };
            tip.SetTextColor(new Color(IosStyle.Secondary));
            tip.SetPadding(Dp(IosStyle.PageInset - 2), 0, Dp(IosStyle.PageInset - 2), Dp(12));
            card.AddView(tip);
        }

        private void BuildInfoCard(LinearLayout parent)
        {
            infoCard = Card_(parent, L10n.T("连接信息"));
            RefreshInfo();
        }

        /// <summary>
        /// 手机 → 电脑传文件。只在有控制端连着时可用（没有连接对象就无处可发）。
        /// </summary>
        private void BuildSendFileCard(LinearLayout parent)
        {
            var card = Card_(parent, L10n.T("传文件"));
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(IosStyle.PageInset - 2), Dp(6), Dp(IosStyle.PageInset - 2), Dp(14));

            sendFileBtn = new Button(this) { Text = L10n.T("发送文件给控制端") };
            IosStyle.StylePrimaryButton(sendFileBtn);
            sendFileBtn.Click += (s, e) => PickAndSendFile();
            box.AddView(sendFileBtn);

            var tip = new TextView(this) { Text = L10n.T("电脑上会弹窗询问是否接收；接收后文件落在电脑“下载”文件夹。"), TextSize = IosStyle.CaptionSize };
            tip.SetTextColor(new Color(IosStyle.Secondary));
            tip.SetPadding(0, Dp(8), 0, 0);
            box.AddView(tip);
            card.AddView(box);
        }

        private void PickAndSendFile()
        {
            var srv = RdHostServer.Instance;
            if (!srv.IsRunning || !srv.HasViewer)
            {
                ToastIt(L10n.T("当前没有设备在控制本机，先让对方连上来"));
                return;
            }
            try
            {
                var i = new Intent(Intent.ActionGetContent);
                i.SetType("*/*");
                i.AddCategory(Intent.CategoryOpenable);
                StartActivityForResult(i, ReqPickFile);
            }
            catch (Exception ex) { ToastIt(L10n.T("无法打开文件选择器：") + ex.Message); }
        }

        /// <summary>从 content:// 里取文件名与大小（OpenableColumns 是系统标准列）。</summary>
        private void ResolveNameAndSize(Android.Net.Uri uri, out string name, out long size)
        {
            name = "file.bin";
            size = 0;
            try
            {
                using (var c = ContentResolver.Query(uri, null, null, null, null))
                {
                    if (c != null && c.MoveToFirst())
                    {
                        int ni = c.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                        int si = c.GetColumnIndex(Android.Provider.OpenableColumns.Size);
                        if (ni >= 0 && !c.IsNull(ni)) name = c.GetString(ni);
                        if (si >= 0 && !c.IsNull(si)) size = c.GetLong(si);
                    }
                }
            }
            catch { }
            if (size <= 0)
            {
                try { using (var s = ContentResolver.OpenInputStream(uri)) { size = s.Length; } } catch { }
            }
        }

        private void BuildQualityCard(LinearLayout parent)
        {
            var card = Card_(parent, L10n.T("画质（被控画面）"));
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(Dp(IosStyle.PageInset - 2), Dp(10), Dp(IosStyle.PageInset - 2), Dp(14));

            string[] names = new string[] { L10n.T("流畅"), L10n.T("均衡"), L10n.T("高清") };
            string[] descs = new string[] { "480p · 10fps", "720p · 12fps", "1080p · 15fps" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new TextView(this)
                {
                    Text = names[i] + "\n" + descs[i],
                    TextSize = IosStyle.CaptionSize,
                    Gravity = GravityFlags.Center
                };
                b.SetPadding(Dp(4), Dp(8), Dp(4), Dp(8));
                b.SetTextColor(new Color(idx == preset ? IosStyle.Surface : IosStyle.Text));
                b.SetBackgroundColor(new Color(idx == preset ? IosStyle.Accent : IosStyle.Separator));
                var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                lp.SetMargins(Dp(2), 0, Dp(2), 0);
                b.LayoutParameters = lp;
                b.Click += (s, e) =>
                {
                    preset = idx;
                    SetPresetColor(row);
                    ApplyPresetLive();
                };
                row.AddView(b);
            }
            card.AddView(row);

            // 【最简设置控件】一个 SeekBar 调 JPEG 质量 + 一个 AutoFps 开关；
            // 其余设置（缩放/关键帧/首帧超时）从代码 API 可调并在日志打印生效值。
            // 不为 UI 大改布局：本页代码构建 UI，追加几行即可。
            var s = ScreenShareSettings.Instance(this);
            var tip = new TextView(this) { Text = L10n.T("高级：JPEG 质量（参考 ScreenStream 默认 80）"), TextSize = IosStyle.CaptionSize };
            tip.SetTextColor(new Color(IosStyle.Secondary));
            tip.SetPadding(Dp(IosStyle.PageInset - 2), Dp(6), Dp(IosStyle.PageInset - 2), 0);
            card.AddView(tip);

            var val = new TextView(this) { Text = s.Describe(), TextSize = IosStyle.CaptionSize };
            val.SetTextColor(new Color(IosStyle.Secondary));
            val.SetPadding(Dp(IosStyle.PageInset - 2), 0, Dp(IosStyle.PageInset - 2), Dp(4));
            card.AddView(val);

            var seek = new SeekBar(this) { Max = ScreenShareSettings.MaxJpegQuality - ScreenShareSettings.MinJpegQuality };
            seek.Progress = s.JpegQuality - ScreenShareSettings.MinJpegQuality;
            seek.ProgressChanged += (s2, e) =>
            {
                int q = ScreenShareSettings.MinJpegQuality + seek.Progress;
                // 只写设置（持久化 + Apply 发事件），消费方只改编码参数，不重建 VirtualDisplay。
                s.JpegQuality = q;
                val.Text = s.Describe();
                // 运行中也立刻生效：只改 JPEG 质量，不重启采集（对齐 ScreenStream）。
                var g = RdHostServer.Instance.Grabber;
                if (g != null) g.SetQuality(q);
            };
            card.AddView(seek);

            var auto = new CheckBox(this) { Text = L10n.T("帧率自动（不主动限帧，参考 ScreenStream FrameRate.Auto）"), Checked = s.AutoFps };
            auto.CheckedChange += (s2, e) => { s.AutoFps = auto.Checked; val.Text = s.Describe(); };
            card.AddView(auto);
        }

        private void SetPresetColor(LinearLayout row)
        {
            for (int i = 0; i < row.ChildCount; i++)
            {
                var v = row.GetChildAt(i) as TextView;
                if (v == null) continue;
                v.SetTextColor(new Color(i == preset ? IosStyle.Surface : IosStyle.Text));
                v.SetBackgroundColor(new Color(i == preset ? IosStyle.Accent : IosStyle.Separator));
            }
        }

        private void ApplyPresetLive()
        {
            var srv = RdHostServer.Instance;
            if (!srv.IsRunning || srv.Grabber == null) return;
            GetPreset(out int shortSide, out int fps, out int q);
            srv.Grabber.Configure(fps, q, shortSide);
        }

        private void GetPreset(out int shortSide, out int fps, out int quality)
        {
            var s = ScreenShareSettings.Instance(this);
            // 三档围绕集中设置派生（不再写死 480/720/1080、10/12/15、45/55/65）。
            // 【为什么】改这些值只改编码参数，绝不重建 VirtualDisplay（对齐 ScreenStream）。
            int baseShort = ScreenShareSettings.FallbackShortSide;
            int baseQ = s.ClampQuality(s.JpegQuality);
            int cap = s.ClampFps(s.MaxFps);
            if (preset == 0) { shortSide = Math.Max(360, baseShort * 2 / 3); fps = Math.Max(5, cap / 3); quality = Math.Max(ScreenShareSettings.MinJpegQuality, baseQ - 20); }
            else if (preset == 2) { shortSide = Math.Min(1080, baseShort * 3 / 2); fps = Math.Min(30, cap); quality = Math.Min(ScreenShareSettings.MaxJpegQuality, baseQ + 5); }
            else { shortSide = baseShort; fps = Math.Max(5, Math.Min(12, cap / 2)); quality = baseQ; }
        }

        private void BuildHelpCard(LinearLayout parent)
        {
            var card = Card_(parent, L10n.T("怎么被控"));
            var t = new TextView(this)
            {
                Text = L10n.T("电脑端（AhoChat）：设备页会把这台手机列为「可远控」，点「远程控制」输入设备码与连接密码即可。\n\n")
                     + L10n.T("另一台手机（AhoChat）：远控页「可被远控的设备」里会直接出现本机。\n\n")
                     + L10n.T("网页端：打开电脑上的 web 页面，在「远程设备」里填本机 IP + 端口 + 设备码 + 密码。\n\n")
                     + L10n.T("安全：连接全程加密（临时 X25519 握手）；本页与通知栏随时可停止被控。"),
                TextSize = IosStyle.CaptionSize
            };
            t.SetTextColor(new Color(IosStyle.Secondary));
            t.SetPadding(Dp(IosStyle.PageInset - 2), Dp(8), Dp(IosStyle.PageInset - 2), Dp(14));
            card.AddView(t);
        }

        /* ===================== 行为 ===================== */

        private void OnPowerClick()
        {
            var srv = RdHostServer.Instance;
            if (srv.IsRunning)
            {
                RdHostService.RequestStop(this);
                SetStatus(L10n.T("已停止"), L10n.T("本机不再接受远程控制"), IosStyle.Secondary);
                return;
            }

            if (!RdInputService.Ensure())
            {
                // 允许"只看不控"，但必须明确告知 —— 否则用户会以为程序坏了
                SetStatus(L10n.T("需要无障碍服务"), L10n.T("未开启无障碍：对方能看到画面但点不动。请先在下面开启。"), IosStyle.Warning);
                RdInputService.OpenSettings(this);
                return;
            }

            RequestProjection();
        }

        private void RequestProjection()
        {
            try
            {
                var mgr = (MediaProjectionManager)GetSystemService(Context.MediaProjectionService);
                if (mgr == null) { SetStatus("不支持", "本机系统不支持屏幕录制", IosStyle.Warning); return; }
                StartActivityForResult(mgr.CreateScreenCaptureIntent(), ReqProjection);
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("无法开启"), L10n.T("调起屏幕录制失败：") + ex.Message, IosStyle.Warning);
            }
        }

        private void StartHostService(Intent data)
        {
            GetPreset(out int shortSide, out int fps, out int quality);
            ScreenShareSettings.Instance(this).LogEffective("RemoteHostActivity.StartHostService");
            try
            {
                var i = new Intent(this, typeof(RdHostService));
                i.SetAction(RdHostService.ActionStart);
                i.PutExtra(RdHostService.ExtraResultCode, (int)Result.Ok);
                i.PutExtra(RdHostService.ExtraData, data);
                i.PutExtra(RdHostService.ExtraShort, shortSide);
                i.PutExtra(RdHostService.ExtraFps, fps);
                i.PutExtra(RdHostService.ExtraQuality, quality);
                StartService(i);
                SetStatus(L10n.T("正在开启…"), L10n.T("已获得屏幕录制授权，正在启动被控服务"), IosStyle.Accent);
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("开启失败"), ex.Message, IosStyle.Warning);
            }
        }

        private void OnStartResult(bool ok, string msg)
        {
            RunOnUiThread(() =>
            {
                if (ok) RefreshStatus();
                else SetStatus(L10n.T("开启失败"), msg ?? L10n.T("未知原因"), IosStyle.Warning);
            });
        }

        private void OnStatus(string s)
        {
            RunOnUiThread(() => { if (RdHostServer.Instance.IsRunning) RefreshStatus(s); });
        }

        private void RefreshStatus(string note = null)
        {
            var srv = RdHostServer.Instance;
            if (srv.IsRunning)
            {
                if (srv.HasViewer)
                {
                    SetStatus(L10n.T("正在被远控"), note ?? L10n.T("有设备正在控制本机，可随时停止"), IosStyle.Success);
                    powerBtn.Text = L10n.T("停止被远控");
                    powerBtn.Background = IosStyle.Rounded(this, IosStyle.Danger, IosStyle.ControlRadius);
                }
                else
                {
                    SetStatus(L10n.T("等待连接"), L10n.T("被控已开启，等待控制端接入 · 端口 ") + srv.Port, IosStyle.Accent);
                    powerBtn.Text = L10n.T("停止被远控");
                    powerBtn.Background = IosStyle.Rounded(this, IosStyle.Danger, IosStyle.ControlRadius);
                }
            }
            else
            {
                SetStatus(L10n.T("未开启"), L10n.T("开启后，局域网内的电脑可以在「设备」页看到本机并远程控制"), IosStyle.Secondary);
                powerBtn.Text = L10n.T("开启被远控");
                powerBtn.Background = IosStyle.Rounded(this, IosStyle.Accent, IosStyle.ControlRadius);
            }
            RefreshAccessibility();
            RefreshInfo();
            try { if (sendFileBtn != null) sendFileBtn.Enabled = srv.HasViewer; } catch { }
        }

        private void SetStatus(string big, string sub, int color)
        {
            statusBig.Text = big;
            statusBig.SetTextColor(new Color(color));
            statusSub.Text = sub;
        }

        private void RefreshAccessibility()
        {
            bool ok = RdInputService.Ensure();
            accState.Text = ok ? L10n.T("已开启 · 可远程点击与输入") : L10n.T("未开启 · 只能看画面，不能操作");
            accState.SetTextColor(new Color(ok ? IosStyle.Success : IosStyle.Warning));
        }

        private void RefreshInfo()
        {
            if (infoCard == null) return;
            infoCard.RemoveAllViews();
            var srv = RdHostServer.Instance;
            infoCard.AddView(Row(L10n.T("设备码"), RdDeviceCode.Format(srv.DeviceCode), L10n.T("重新生成"), () =>
            {
                srv.RegenerateDeviceCode();
                RefreshInfo();
            }));
            infoCard.AddView(Divider());
            string pw = srv.Password;
            infoCard.AddView(Row(L10n.T("连接密码"),
                string.IsNullOrEmpty(pw) ? L10n.T("未设密码（谁都能连）") : pw,
                L10n.T("修改"), EditPassword,
                string.IsNullOrEmpty(pw) ? IosStyle.Warning : IosStyle.Text));
            infoCard.AddView(Divider());
            var addr4 = LocalIp() + " : " + srv.Port;
            var row4 = Row(L10n.T("本机地址"), addr4, null, null);
            row4.Click += (s, e) => Copy(LocalIp() + ":" + srv.Port);
            infoCard.AddView(row4);

            infoCard.AddView(Divider());
            var ip6 = DeviceStatus.Ipv6;
            string addr6 = ip6 == "::" || string.IsNullOrEmpty(ip6)
                ? L10n.T("未获取")
                : MchatBridge.FormatHostPort(ip6, srv.Port);
            var row6 = Row(L10n.T("本机 IPv6"), addr6, null, null);
            if (addr6 != "未获取") row6.Click += (s, e) => Copy(MchatBridge.FormatHostPort(DeviceStatus.Ipv6, srv.Port));
            infoCard.AddView(row6);
        }

        private void Copy(string text)
        {
            try
            {
                var cm = (Android.Content.ClipboardManager)GetSystemService(Android.Content.Context.ClipboardService);
                if (cm != null) cm.PrimaryClip = Android.Content.ClipData.NewPlainText("mchat", text ?? "");
                L10n.Toast(this, L10n.T("已复制：") + text, ToastLength.Short).Show();
            }
            catch { }
        }

        private View Row(string label, string value, string action, Action onClick, int valueColor = unchecked((int)0xFF1C1C1E))
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.PageInset), Dp(8), Dp(IosStyle.PageInset), Dp(8));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var lab = new TextView(this) { Text = label, TextSize = IosStyle.CaptionSize };
            lab.SetTextColor(new Color(IosStyle.Secondary));
            lab.LayoutParameters = new LinearLayout.LayoutParams(Dp(72), ViewGroup.LayoutParams.WrapContent);
            row.AddView(lab);

            var val = new TextView(this) { Text = value, TextSize = IosStyle.BodySize };
            val.SetTextColor(new Color(valueColor));
            val.SetTypeface(null, TypefaceStyle.Bold);
            val.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            row.AddView(val);

            if (action != null)
            {
                var a = new TextView(this) { Text = action, TextSize = IosStyle.CaptionSize };
                a.SetTextColor(new Color(IosStyle.Accent));
                a.SetPadding(Dp(8), Dp(4), 0, Dp(4));
                a.Click += (s, e) => { try { onClick?.Invoke(); } catch { } };
                row.AddView(a);
            }
            return row;
        }

        /// <summary>
        /// 修改连接密码：留空即"不设密码"（局域网内谁都能连，界面上会用橙色标出来提醒）。
        /// </summary>
        private void EditPassword()
        {
            var srv = RdHostServer.Instance;
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(10), Dp(18), 0);

            var ed = new EditText(this) { Text = srv.Password };
            ed.Hint = L10n.T("留空 = 不设密码");
            ed.SetTextColor(new Color(IosStyle.Text));
            box.AddView(ed);

            var gen = new TextView(this) { Text = L10n.T("随机生成一个") };
            gen.TextSize = IosStyle.CaptionSize;
            gen.SetTextColor(new Color(IosStyle.Accent));
            gen.SetPadding(0, Dp(8), 0, Dp(8));
            gen.Click += (s, e) => { ed.Text = srv.RegeneratePassword(); };
            box.AddView(gen);

            L10n.Dialog(this)
                .SetTitle(L10n.T("连接密码"))
                .SetView(box)
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .SetPositiveButton(L10n.T("保存"), (s, e) =>
                {
                    srv.Password = (ed.Text ?? "").Trim();
                    RefreshInfo();
                })
                .Show();
        }

        private string LocalIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return L10n.T("未联网");
        }

        /* ===================== 小工具 ===================== */

        private LinearLayout Card_(LinearLayout parent, string title)
        {
            var t = new TextView(this) { Text = title, TextSize = IosStyle.CaptionSize };
            t.SetTextColor(new Color(IosStyle.Secondary));
            t.SetPadding(Dp(IosStyle.PageInset), Dp(16), Dp(IosStyle.PageInset), Dp(8));
            parent.AddView(t);

            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = IosStyle.Rounded(this, IosStyle.Surface, IosStyle.CardRadius);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(0, 0, 0, Dp(24));
            parent.AddView(card, lp);
            return card;
        }

        private View Divider()
        {
            var v = new View(this);
            v.SetBackgroundColor(new Color(IosStyle.Separator));
            v.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            return v;
        }

        private void ToastIt(string msg)
        {
            try { L10n.Toast(this, msg, ToastLength.Short).Show(); } catch { }
        }

        private int Dp(int v) => IosStyle.Dp(this, v);
    }
}

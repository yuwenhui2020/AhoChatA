using System;
using System.Collections.Generic;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Media.Projection;

namespace AhoChatA
{
    /// <summary>
    /// IP 摄像头中枢页：
    ///  · 一键把本机摄像头 / 整个屏幕共享到局域网（硬编码 H.264 + HTTP 流 + UDP 信标）；
    ///  · 嗅探局域网内其它设备共享的画面，生成卡片，点开实时查看；
    ///  · 画质 / 前后摄 / 共享来源可切换；PC 接口说明一并给出。
    /// </summary>
    [Activity(Label = "@string/act_ipcam", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class IpCameraActivity : BaseActivity
    {
        private const int ReqCamera = 7701;
        private const int ReqProjection = 7702;

        private IpCameraDiscoverer _disc;
        // 短边默认值改读集中设置（ScreenStream 50% 缩放对 1440 短边 ≈ 720），不再写死。
        private int _quality = ScreenShareSettings.FallbackShortSide;   // 720 / 1080（短边）
        private bool _front;
        private bool _screenMode;     // false=共享摄像头，true=共享屏幕
        private bool _pendingStart;

        private TextView _status, _empty, _preview;
        private LinearLayout _camList;
        private System.Threading.Timer _uiTimer;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_ipcamera);

            // 返回：矢量箭头（与其他页面一致）
            var backBox = FindViewById<FrameLayout>(Resource.Id.ipBack);
            if (backBox != null)
            {
                backBox.AddView(new IconView(this, IconKind.Back) { IconColor = unchecked((int)0xFF007AFF) },
                    new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
                backBox.Click += (s, e) => Finish();
            }
            _status = FindViewById<TextView>(Resource.Id.ipStatus);
            _empty = FindViewById<TextView>(Resource.Id.ipEmpty);
            _camList = FindViewById<LinearLayout>(Resource.Id.ipCamList);
            var shareCard = FindViewById<LinearLayout>(Resource.Id.ipShareCard);
            var addressCard = FindViewById<LinearLayout>(Resource.Id.ipAddressCard);
            if (shareCard != null) shareCard.Background = IosStyle.Rounded(this, IosStyle.Surface, IosStyle.CardRadius);
            if (addressCard != null) addressCard.Background = IosStyle.Rounded(this, IosStyle.Surface, IosStyle.CardRadius);
            if (_camList != null) _camList.Background = IosStyle.Rounded(this, IosStyle.Surface, IosStyle.CardRadius);
            _preview = FindViewById<TextView>(Resource.Id.ipPreview);
            _preview.Click += (s, e) => OpenLocalPreview();

            FindViewById<TextView>(Resource.Id.ipQ720).Click += (s, e) => SelectQuality(720);
            FindViewById<TextView>(Resource.Id.ipQ1080).Click += (s, e) => SelectQuality(1080);
            FindViewById<TextView>(Resource.Id.ipFacing).Click += (s, e) => ToggleFacing();
            FindViewById<TextView>(Resource.Id.ipSrcCamera).Click += (s, e) => SelectSource(false);
            FindViewById<TextView>(Resource.Id.ipSrcScreen).Click += (s, e) => SelectSource(true);
            FindViewById<TextView>(Resource.Id.ipShare).Click += (s, e) => ToggleShare();

            _disc = new IpCameraDiscoverer();
            _disc.OnCamerasChanged += list => RunOnUiThread(() => Repopulate(list));

            RegisterResultReceiver();

            // 恢复现场：若共享仍在运行（例如旋转后重建了本页），按实际共享来源回填选中态
            var srvNow = IpCameraServer.Instance;
            _screenMode = srvNow.IsRunning && srvNow.Source == ShareSource.Screen;
            if (srvNow.IsRunning && srvNow.Source == ShareSource.Camera) _quality = srvNow.Width > 1280 ? 1080 : 720;

            // 从聊天页「+」直接进来时可以指定来源（共享摄像头 / 共享屏幕）
            if (!srvNow.IsRunning && Intent != null && Intent.HasExtra("screen"))
            {
                _screenMode = Intent.GetBooleanExtra("screen", false);
            }

            SelectQuality(_quality, false);
            ApplySourceUi(_screenMode);
            SyncShareUi();

            // 从「共享屏幕」进来的，直接把授权流程走起来，少一次点击
            if (!srvNow.IsRunning && _screenMode)
            {
                new Handler(Looper.MainLooper).PostDelayed(() =>
                {
                    try { if (!IpCameraServer.Instance.IsRunning && _screenMode) ToggleShare(); } catch { }
                }, 350);
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            try { _disc.Start(); } catch (Exception ex) { L10n.Toast(this, L10n.T("嗅探启动失败：") + ex.Message, ToastLength.Short).Show(); }
            SyncShareUi();
            // 共享中时刷新状态（自适应切档后分辨率/码率会实时变化）
            _uiTimer = new System.Threading.Timer(_ =>
            {
                try { RunOnUiThread(SyncShareUi); } catch { }
            }, null, 1200, 1500);
        }

        protected override void OnPause()
        {
            try { _disc.Stop(); } catch { }
            try { _uiTimer?.Dispose(); } catch { }
            _uiTimer = null;
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            try { _disc.Stop(); } catch { }
            UnregisterResultReceiver();
            base.OnDestroy();
        }

        /* ---------------- 共享来源 / 画质 ---------------- */

        private void SelectSource(bool screen)
        {
            ApplySourceUi(screen);

            var srv = IpCameraServer.Instance;
            if (srv.IsRunning)
            {
                // 换来源必须重新开始（屏幕共享还要重新授权）
                srv.Stop();
                SyncShareUi();
                if (!screen) StartCameraShare();
                else L10n.Toast(this, L10n.T("已切换为屏幕共享，点「开始共享」后授权即可"), ToastLength.Short).Show();
            }
        }

        /// <summary>只刷「共享内容」两个 chip 的选中态与摄像头相关控件可见性，不触发任何重启。</summary>
        private void ApplySourceUi(bool screen)
        {
            _screenMode = screen;

            var c = FindViewById<TextView>(Resource.Id.ipSrcCamera);
            var s = FindViewById<TextView>(Resource.Id.ipSrcScreen);
            c.SetBackgroundResource(screen ? Resource.Drawable.chip_normal : Resource.Drawable.chip_active);
            c.SetTextColor(Color(screen ? 0xFF8E8E93 : 0xFF007AFF));
            s.SetBackgroundResource(screen ? Resource.Drawable.chip_active : Resource.Drawable.chip_normal);
            s.SetTextColor(Color(screen ? 0xFF007AFF : 0xFF8E8E93));

            // 屏幕共享没有"前后摄像头"这回事
            FindViewById<TextView>(Resource.Id.ipFacing).Visibility = screen ? ViewStates.Gone : ViewStates.Visible;
            FindViewById<TextView>(Resource.Id.ipFacingLabel).Visibility = screen ? ViewStates.Gone : ViewStates.Visible;
        }

        private void SelectQuality(int q) => SelectQuality(q, true);

        private void SelectQuality(int q, bool applyNow)
        {
            _quality = q;
            var c720 = FindViewById<TextView>(Resource.Id.ipQ720);
            var c1080 = FindViewById<TextView>(Resource.Id.ipQ1080);
            bool hi = q == 1080;
            c720.SetBackgroundResource(hi ? Resource.Drawable.chip_normal : Resource.Drawable.chip_active);
            c720.SetTextColor(Color(hi ? 0xFF8E8E93 : 0xFF007AFF));
            c1080.SetBackgroundResource(hi ? Resource.Drawable.chip_active : Resource.Drawable.chip_normal);
            c1080.SetTextColor(Color(hi ? 0xFF007AFF : 0xFF8E8E93));

            if (!applyNow) return;
            var srv = IpCameraServer.Instance;
            if (!srv.IsRunning) return;
            if (srv.Source == ShareSource.Screen)
            {
                // 屏幕共享不能重启（授权 token 一次性），走平滑切档
                srv.SetQuality(q);
            }
            else
            {
                RestartShare();
            }
        }

        private void ToggleFacing()
        {
            _front = !_front;
            FindViewById<TextView>(Resource.Id.ipFacing).Text = _front ? "前置摄像头" : "后置摄像头";
            if (IpCameraServer.Instance.IsRunning) RestartShare();
        }

        private void RestartShare()
        {
            IpCameraServer.Instance.Stop();
            StartCameraShare();
        }

        /* ---------------- 开始 / 停止共享 ---------------- */

        private void ToggleShare()
        {
            var srv = IpCameraServer.Instance;
            if (srv.IsRunning)
            {
                StopShare();
                return;
            }
            if (_screenMode) { RequestProjection(); return; }
            StartCameraShare();
        }

        private void StopShare()
        {
            try { IpCameraServer.Instance.Stop(); } catch { }
            try { StopService(new Intent(this, typeof(ScreenCastService))); } catch { }
            try { StopService(new Intent(this, typeof(CameraShareService))); } catch { }
            SyncShareUi();
            L10n.Toast(this, L10n.T("已停止共享"), ToastLength.Short).Show();
        }

        /// <summary>
        /// 共享启动成功后，把「用什么地址看」一次性讲清楚：网页 / 播放器 / 状态接口，
        /// 全部挂在同一个端口上，只有路径不同。
        /// </summary>
        private void ShowShareReady(bool screen)
        {
            var ip = DeviceStatus.Ip;
            int port = IpCameraServer.Instance.UsedHttpPort;
            var web = "http://" + ip + ":" + port + "/cam";
            var https = WebServer.HttpsRunning ? WebServer.HttpsBaseUrl + "/cam/" : "";
            var msg = (screen ? L10n.T("屏幕") : L10n.T("摄像头")) + L10n.T("共享已开始\n\n")
                    + L10n.T("① 浏览器观看（手机 / 电脑都行）\n") + web + "\n\n"
                    + L10n.T("② VLC / PotPlayer / ffplay 直接播放\n")
                    + L10n.T("低延迟：") + web + "/live.ts\n"
                    + L10n.T("裸  流：") + web + "/stream.h264\n\n"
                    + L10n.T("③ 状态信息\n") + web + "/info\n\n"
                    + (string.IsNullOrEmpty(https) ? "" :
                        L10n.T("④ 超低延迟网页（约 150ms，逐帧直推）\n") + https
                        + L10n.T("\n   自签名证书，首次需点「高级 → 继续访问」\n\n"))
                    + L10n.T("所有服务共用一个端口，只有路径不同。");

            L10n.Dialog(this)
                .SetTitle(L10n.T("共享已开始"))
                .SetMessage(msg)
                .SetPositiveButton(L10n.T("复制网页地址"), (s, e) =>
                {
                    try
                    {
                        var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                        cm.Text = web;
                        L10n.Toast(this, L10n.T("已复制：") + web, ToastLength.Short).Show();
                    }
                    catch { }
                })
                .SetNeutralButton(L10n.T("本机预览"), (s, e) => OpenLocalPreview())
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Show();
        }

        private void StartCameraShare()
        {
            // 检查权限
            if (CheckSelfPermission("android.permission.CAMERA") != Android.Content.PM.Permission.Granted)
            {
                _pendingStart = true;
                RequestPermissions(new string[] { "android.permission.CAMERA" }, ReqCamera);
                return;
            }
            StartServer();
        }

        private void StartServer()
        {
            int w = _quality == 1080 ? 1920 : 1280;
            int h = _quality == 1080 ? 1080 : 720;
            // 硬编码 30fps 改读集中设置（参考 ScreenStream fps Auto→标称 MaxFps）。
            var s = ScreenShareSettings.Instance(this);
            int fps = s.MaxFps;
            int br = _quality == 1080 ? 8_000_000 : 4_000_000;
            s.LogEffective("IpCameraActivity.StartServer");
            L10n.Toast(this, L10n.T("正在启动共享…"), ToastLength.Short).Show();
            // 摄像头打开可能耗时，放到后台避免阻塞 UI（ANR）
            var ctx = this;
            var front = _front;
            TryStartCameraService();   // 先拉前台服务保活：否则退出本界面、进程被回收后共享即中断
            new Thread(() =>
            {
                bool ok = IpCameraServer.Instance.Start(ctx, front, w, h, fps, br);
                RunOnUiThread(() =>
                {
                    if (ok) ShowShareReady(false);
                    else
                    {
                        try { StopService(new Intent(ctx, typeof(CameraShareService))); } catch { }
                        L10n.Toast(ctx, L10n.T("共享失败：") + (IpCameraServer.Instance.LastError ?? L10n.T("未知")), ToastLength.Long).Show();
                    }
                    SyncShareUi();
                });
            }) { IsBackground = true }.Start();
        }

        /// <summary>把摄像头共享提升为前台服务：保活 + 通知栏「停止共享」。仅保活用，真正编码仍在 IpCameraServer。</summary>
        private void TryStartCameraService()
        {
            try
            {
                var i = new Intent(this, typeof(CameraShareService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(i);
                else StartService(i);
            }
            catch { }
        }

        /// <summary>向系统申请屏幕录制授权（授权 token 一次性，每次共享都要重新点同意）。</summary>
        private void RequestProjection()
        {
            try
            {
                var mgr = (MediaProjectionManager)GetSystemService(Context.MediaProjectionService);
                if (mgr == null) { L10n.Toast(this, L10n.T("系统不支持屏幕录制"), ToastLength.Long).Show(); return; }
                StartActivityForResult(mgr.CreateScreenCaptureIntent(), ReqProjection);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("申请屏幕录制失败：") + ex.Message, ToastLength.Long).Show();
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ReqProjection) return;

            if (resultCode != Result.Ok || data == null)
            {
                // 明确日志：授权被拒/数据丢失时便于定位，不再只弹一个 Toast。
                Android.Util.Log.Warn("IpCamAct", "屏幕录制授权无效 resultCode=" + resultCode + " data=" + (data != null));
                L10n.Toast(this, L10n.T("未授权屏幕录制，无法共享屏幕"), ToastLength.Short).Show();
                return;
            }
            StartScreenService((int)resultCode, data);
        }

        private void StartScreenService(int rc, Intent data)
        {
            // 硬编码 30fps 改读集中设置；token 一次性，先备份到 Service 供失败前重试。
            var s = ScreenShareSettings.Instance(this);
            int shortSide = _quality;
            int fps = s.MaxFps;
            int br = _quality == 1080 ? 8_000_000 : 4_000_000;
            s.LogEffective("IpCameraActivity.StartScreenService");

            try
            {
                // 先 FGS 再拿投影：把一次性授权结果正确保存/复用（ScreenCastService 侧备份）。
                ScreenCastService.SetPendingAuth(rc, data, shortSide, fps, br);
                var i = new Intent(this, typeof(ScreenCastService));
                i.SetAction(ScreenCastService.ActionStart);
                i.PutExtra(ScreenCastService.ExtraResultCode, rc);
                i.PutExtra(ScreenCastService.ExtraData, data);
                i.PutExtra(ScreenCastService.ExtraShort, shortSide);
                i.PutExtra(ScreenCastService.ExtraFps, fps);
                i.PutExtra(ScreenCastService.ExtraBitrate, br);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(i);
                else StartService(i);

                L10n.Toast(this, L10n.T("正在启动屏幕共享…"), ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("启动屏幕共享失败：") + ex.Message, ToastLength.Long).Show();
            }
        }

        /* ---------------- 启动结果广播 ---------------- */

        private void RegisterResultReceiver()
        {
            // 屏幕共享启动结果：服务先 StartForeground 再开采集，只能异步回报，用同进程事件回传
            IpCameraServer.ScreenStartResult -= OnScreenStartResult;
            IpCameraServer.ScreenStartResult += OnScreenStartResult;
        }

        private void UnregisterResultReceiver()
        {
            IpCameraServer.ScreenStartResult -= OnScreenStartResult;
        }

        private void OnScreenStartResult(bool ok, string msg) => OnCastResult(ok, msg);

        internal void OnCastResult(bool ok, string msg)
        {
            RunOnUiThread(() =>
            {
                if (ok) ShowShareReady(true);
                else L10n.Toast(this, L10n.T("屏幕共享失败：") + (string.IsNullOrEmpty(msg) ? L10n.T("未知原因") : msg)
                    + L10n.T("\n授权 token 一次性，请重新点「开始共享」授权后重试"), ToastLength.Long).Show();
                SyncShareUi();
            });
        }

        /* ---------------- 状态栏 ---------------- */

        private void SyncShareUi()
        {
            var srv = IpCameraServer.Instance;
            var running = srv.IsRunning;
            string src = srv.Source == ShareSource.Screen ? "屏幕" : "摄像头";
            _status.Text = running
                ? L10n.T("共享中 · ") + src + " · " + srv.Width + "x" + srv.Height
                  + " · " + (srv.Bitrate / 1000) + L10n.T(" kbps · 查看 ") + srv.ViewerCount
                : L10n.T("未共享 · 点右侧按钮开始");
            FindViewById<TextView>(Resource.Id.ipShare).Text = running ? "停止共享" : "开始共享";

            // 访问地址区：共享中给完整链接，未共享给说明
            var pc = FindViewById<TextView>(Resource.Id.ipPc);
            if (pc != null)
            {
                pc.Text = running
                    ? L10n.T("浏览器观看（手机 / 电脑）\n") + srv.LocalUrl
                      + L10n.T("\n\n播放器直接播（VLC / PotPlayer / ffplay）\n低延迟：") + srv.LocalUrl + "/live.ts"
                      + L10n.T("\n裸流：") + srv.LocalUrl + "/stream.h264"
                    : L10n.T("开启共享后，这里会显示完整访问地址。\n所有网页服务共用一个端口，只有路径不同。");
            }
            // 本机正在共享摄像头时显示"本机预览"入口；屏幕共享时预览会画面套画面，不给入口
            _preview.Visibility = (running && srv.Source == ShareSource.Camera) ? ViewStates.Visible : ViewStates.Gone;
            // 共享期间保持屏幕常亮：息屏后 MIUI 会回收相机 / 屏幕采集会变黑，导致查看端"播一会就掉"
            try
            {
                if (running) Window.AddFlags(WindowManagerFlags.KeepScreenOn);
                else Window.ClearFlags(WindowManagerFlags.KeepScreenOn);
            }
            catch { }
        }

        /// <summary>本机预览：直接连本机 HTTP 流地址，无需另一台设备，便于单机自测。</summary>
        private void OpenLocalPreview()
        {
            var srv = IpCameraServer.Instance;
            if (!srv.IsRunning) return;
            var intent = new Intent(this, typeof(CameraViewActivity));
            intent.PutExtra("ip", DeviceStatus.Ip);
            intent.PutExtra("port", srv.UsedHttpPort);
            intent.PutExtra("rot", srv.Rotation);
            intent.PutExtra("name", DeviceStatus.DeviceName + L10n.T("（本机）"));
            intent.PutExtra("w", srv.Width);
            intent.PutExtra("h", srv.Height);
            StartActivity(intent);
        }

        /* ---------------- 局域网发现列表 ---------------- */

        private void Repopulate(List<CameraInfo> list)
        {
            _camList.RemoveAllViews();
            if (list == null || list.Count == 0)
            {
                _empty.Visibility = ViewStates.Visible;
                return;
            }
            _empty.Visibility = ViewStates.Gone;

            for (int i = 0; i < list.Count; i++)
            {
                var cam = list[i];
                if (i > 0) _camList.AddView(Divider());

                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
                row.SetMinimumHeight(Dp(52));

                var box = new FrameLayout(this);
                box.Background = RoundBg(unchecked((int)0xFFEAF6FE), 11);
                box.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
                var ic = new IconView(this, cam.IsScreen ? IconKind.Screen : IconKind.Camera)
                {
                    IconColor = unchecked((int)0xFF007AFF)
                };
                box.AddView(ic, new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
                row.AddView(box);

                var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                col.SetPadding(Dp(12), 0, Dp(6), 0);
                col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

                var t1 = new TextView(this) { Text = cam.Name, TextSize = 16 };
                t1.SetTextColor(Color(0xFF111111));
                col.AddView(t1);

                var t2 = new TextView(this)
                {
                    Text = (cam.IsScreen ? L10n.T("屏幕共享 · ") : "") + cam.Ip + " · " + cam.Width + "x" + cam.Height
                           + " · " + cam.Fps + "fps" + (cam.AgeSec <= 2 ? "" : L10n.T(" · 信号弱")),
                    TextSize = 13
                };
                t2.SetTextColor(Color(0xFF8E9099));
                t2.SetPadding(0, Dp(3), 0, 0);
                col.AddView(t2);
                row.AddView(col);

                var chev = new IconView(this, IconKind.ChevronRight)
                {
                    IconColor = unchecked((int)0xFFC7C9CE),
                    Thickness = 1.5f
                };
                row.AddView(chev, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

                row.Click += (s, e) => OpenViewer(cam);
                _camList.AddView(row);
            }
        }

        private View Divider()
        {
            var v = new View(this);
            v.SetBackgroundColor(Color(0xFFEEEEF1));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            lp.LeftMargin = Dp(62);
            v.LayoutParameters = lp;
            return v;
        }

        private Android.Graphics.Drawables.GradientDrawable RoundBg(long color, int radiusDp)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor((int)color);
            d.SetCornerRadius(Dp(radiusDp));
            return d;
        }

        private void OpenViewer(CameraInfo cam)
        {
            var intent = new Intent(this, typeof(CameraViewActivity));
            intent.PutExtra("ip", cam.Ip);
            intent.PutExtra("port", cam.Port);
            intent.PutExtra("rot", cam.Rotation);
            intent.PutExtra("name", cam.Name);
            intent.PutExtra("w", cam.Width);
            intent.PutExtra("h", cam.Height);
            intent.PutExtra("kind", cam.IsScreen ? "screen" : "camera");
            StartActivity(intent);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == ReqCamera && grantResults.Length > 0 &&
                grantResults[0] == Android.Content.PM.Permission.Granted && _pendingStart)
            {
                _pendingStart = false;
                StartServer();
            }
            else if (requestCode == ReqCamera)
            {
                _pendingStart = false;
                L10n.Toast(this, L10n.T("需要摄像头权限才能共享"), ToastLength.Short).Show();
            }
        }

        private static Android.Graphics.Color Color(uint argb) => new Android.Graphics.Color(unchecked((int)argb));

        private int Dp(int v) => IosStyle.Dp(this, v);
    }
}

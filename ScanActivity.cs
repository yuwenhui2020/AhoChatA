using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware;
using Android.OS;
using AndroidX.Core.Content;
using Android.Views;
using Android.Widget;
using ZXing;
using ZXing.Common;
using Camera = Android.Hardware.Camera;

namespace AhoChatA
{
    /// <summary>
    /// 二维码 / 条码扫描（Camera1 + ZXing.Net）。
    /// 扫描成功：URL 直接交给内置浏览器打开；普通文本弹出结果（可复制 / 作为网址打开）。
    /// </summary>
    [Activity(Label = "@string/act_scan", Theme = "@style/AppTheme.NoActionBar", Exported = false)]
    public class ScanActivity : BaseActivity, Camera.IPreviewCallback
    {
        private const int ReqCamera = 8301;
        private Camera _camera;
        private CameraPreview _preview;
        private TextView _hint;
        private bool _decoding;
        private bool _found;
        private bool _frontCamera = false;
        private int _cameraOrientation = 90;
        private readonly BarcodeReaderGeneric _reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { TryHarder = false, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
        };

        public static void Open(Context ctx)
        {
            if (ctx == null) return;
            var intent = new Intent(ctx, typeof(ScanActivity));
            if (!(ctx is Activity)) intent.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Title = L10n.T("扫一扫");
            var root = new FrameLayout(this);
            root.SetBackgroundColor(Color.Black);

            _preview = new CameraPreview(this);
            root.AddView(_preview, new FrameLayout.LayoutParams(-1, -1));

            // 中间取景框（UI 提示）
            var frame = new View(this) { Background = new ColorDrawable(Color.ParseColor("#66FFFFFF")) };
            var fp = new FrameLayout.LayoutParams(Dp(240), Dp(240));
            fp.Gravity = GravityFlags.Center;
            root.AddView(frame, fp);

            _hint = new TextView(this)
            {
                Text = L10n.T("将二维码放入框内，即可自动扫描"),
                Gravity = GravityFlags.Center,
                TextSize = 15
            };
            _hint.SetTextColor(Color.White);
            _hint.SetBackgroundColor(Color.ParseColor("#99000000"));
            _hint.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            var hp = new FrameLayout.LayoutParams(-1, -2);
            hp.Gravity = GravityFlags.Bottom;
            hp.BottomMargin = Dp(80);
            root.AddView(_hint, hp);

            var close = new Button(this) { Text = L10n.T("关闭") };
            close.Click += (s, e) => Finish();
            var cp = new FrameLayout.LayoutParams(Dp(120), Dp(44));
            cp.Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal;
            cp.BottomMargin = Dp(16);
            root.AddView(close, cp);

            // 切换摄像头按钮
            var switchCam = new TextView(this);
            switchCam.Text = "🔄";
            switchCam.TextSize = 24;
            switchCam.SetTextColor(Color.White);
            switchCam.Background = new ColorDrawable(Color.ParseColor("#88000000"));
            switchCam.Gravity = GravityFlags.Center;
            switchCam.Click += (s, e) =>
            {
                _frontCamera = !_frontCamera;
                StartCamera();
            };
            var scp = new FrameLayout.LayoutParams(Dp(48), Dp(48));
            scp.Gravity = GravityFlags.Top | GravityFlags.End;
            scp.TopMargin = Dp(60);
            scp.RightMargin = Dp(16);
            root.AddView(switchCam, scp);

            SetContentView(root);

            // 异步检查相机权限，不阻塞 UI
            Task.Run(async () =>
            {
                await Task.Yield();
                if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.Camera) == Permission.Granted)
                {
                    RunOnUiThread(StartCamera);
                }
                else
                {
                    RunOnUiThread(() =>
                    {
                        RequestPermissions(new[] { Android.Manifest.Permission.Camera }, ReqCamera);
                    });
                }
            });
        }

        private int Dp(float v) => (int)(v * Resources.DisplayMetrics.Density + 0.5f);

        protected override void OnResume()
        {
            base.OnResume();
            if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.Camera) == Permission.Granted && _camera == null)
                StartCamera();
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == ReqCamera)
            {
                if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) StartCamera();
                else { L10n.Toast(this, L10n.T("需要相机权限才能扫码"), ToastLength.Short).Show(); Finish(); }
            }
        }

        private void StartCamera()
        {
            try
            {
                if (_camera != null)
                {
                    try { _camera.SetPreviewCallbackWithBuffer(null); _camera.StopPreview(); _camera.Release(); } catch { }
                    _camera = null;
                }

                int cameraId = _frontCamera ? GetFrontCameraId() : GetBackCameraId();
                if (cameraId < 0)
                {
                    L10n.Toast(this, L10n.T("无法找到摄像头"), ToastLength.Short).Show();
                    Finish();
                    return;
                }

                _camera = Camera.Open(cameraId);
                var p = _camera.GetParameters();
                p.PreviewFormat = Android.Graphics.ImageFormatType.Nv21;
                int previewW = 0, previewH = 0;
                var sizes = p.SupportedPreviewSizes;
                if (sizes != null && sizes.Count > 0)
                {
                    Camera.Size pick = sizes[0];
                    foreach (var sz in sizes)
                        if (sz.Width <= 1280 && sz.Height <= 1280 && sz.Width >= pick.Width) pick = sz;
                    previewW = pick.Width;
                    previewH = pick.Height;
                    p.SetPreviewSize(previewW, previewH);
                }

                // 计算预览画面需要旋转的角度（相对设备自然方向）。
                int displayRotation = (int)WindowManager.DefaultDisplay.Rotation;
                int degrees = 0;
                switch (displayRotation)
                {
                    case 0: degrees = 0; break;       // SurfaceOrientation.Rotation0
                    case 1: degrees = 90; break;      // SurfaceOrientation.Rotation90
                    case 2: degrees = 180; break;     // SurfaceOrientation.Rotation180
                    case 3: degrees = 270; break;     // SurfaceOrientation.Rotation270
                }

                Camera.CameraInfo info = new Camera.CameraInfo();
                Camera.GetCameraInfo(cameraId, info);
                int result;
                if (info.Facing == CameraFacing.Front)
                {
                    result = (info.Orientation + degrees) % 360;
                    result = (360 - result) % 360; // 前置摄像头镜像
                }
                else
                {
                    result = (info.Orientation - degrees + 360) % 360;
                }
                // 关键：让“预览画面”真正旋转必须调用 SetDisplayOrientation；
                // p.SetRotation 只影响拍照/录像文件的 EXIF 方向，不会旋转 SurfaceView 预览。
                _camera.SetDisplayOrientation(result);
                _cameraOrientation = result;

                _camera.SetParameters(p);
                // 把预览的原始宽高与旋转角交给 Surface，让它按正确宽高比铺满，避免竖屏下画面被拉伸变形。
                _preview.SetCamera(_camera, previewW, previewH, result);
                _camera.SetPreviewCallbackWithBuffer(this);
                var buffer = p.PreviewSize;
                if (buffer != null) _camera.AddCallbackBuffer(new byte[buffer.Width * buffer.Height * 3 / 2]);
                _camera.StartPreview();
            }
            catch (System.Exception ex)
            {
                L10n.Toast(this, L10n.T("无法打开相机：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private int GetFrontCameraId()
        {
            int numberOfCameras = Camera.NumberOfCameras;
            for (int i = 0; i < numberOfCameras; i++)
            {
                Camera.CameraInfo info = new Camera.CameraInfo();
                Camera.GetCameraInfo(i, info);
                if (info.Facing == CameraFacing.Front) return i;
            }
            return -1;
        }

        private int GetBackCameraId()
        {
            int numberOfCameras = Camera.NumberOfCameras;
            for (int i = 0; i < numberOfCameras; i++)
            {
                Camera.CameraInfo info = new Camera.CameraInfo();
                Camera.GetCameraInfo(i, info);
                if (info.Facing == CameraFacing.Back) return i;
            }
            return -1;
        }

        public void OnPreviewFrame(byte[] data, Camera camera)
        {
            if (_found || _decoding || data == null) { ReturnBuffer(data); return; }
            _decoding = true;
            try
            {
                var p = camera.GetParameters();
                var size = p.PreviewSize;
                if (size == null) { ReturnBuffer(data); return; }
                var w = size.Width; var h = size.Height;
                // NV21 前 w*h 字节是 Y（灰度）平面，直接拿来按 Gray8 解码
                var y = new byte[w * h];
                System.Buffer.BlockCopy(data, 0, y, 0, y.Length);
                
                // 根据摄像头朝向旋转图像以正确解码
                var luminance = new RGBLuminanceSource(y, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
                if (_cameraOrientation != 0)
                {
                    // 旋转灰度图像以匹配摄像头方向
                    int rotations = (_cameraOrientation / 90) % 4;
                    for (int i = 0; i < rotations; i++)
                    {
                        luminance = (RGBLuminanceSource)luminance.rotateCounterClockwise();
                    }
                }
                var result = _reader.Decode(luminance);
                if (result != null && !string.IsNullOrEmpty(result.Text))
                {
                    _found = true;
                    var text = result.Text;
                    RunOnUiThread(() => HandleResult(text));
                }
            }
            catch { }
            finally { _decoding = false; ReturnBuffer(data); }
        }

        private void ReturnBuffer(byte[] data)
        {
            if (data == null || _camera == null) return;
            try { _camera.AddCallbackBuffer(data); } catch { }
        }

        private void HandleResult(string text)
        {
            try { _camera?.StopPreview(); } catch { }
            Vibrate();
            if (IsWebUrl(text))
            {
                BrowserActivity.Open(this, text);
                Finish();
                return;
            }
            L10n.Dialog(this).SetTitle(L10n.T("扫描结果"))
                .SetMessage(text)
                .SetPositiveButton(L10n.T("复制"), (s, e) => { AppHostBridge.CopyText(this, text); Finish(); })
                .SetNeutralButton(L10n.T("作为网址打开"), (s, e) =>
                {
                    var url = text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : "http://" + text;
                    BrowserActivity.Open(this, url); Finish();
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => Finish())
                .Show();
        }

        private static bool IsWebUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
            // 裸 IP[:port] 或域名
            return System.Text.RegularExpressions.Regex.IsMatch(t,
                @"^(\d{1,3}\.){3}\d{1,3}(:\d+)?(/.*)?$|^[a-zA-Z0-9-]+(\.[a-zA-Z0-9-]+)+(:\d+)?(/.*)?$");
        }

        private void Vibrate()
        {
            try
            {
                var v = (Vibrator)GetSystemService(VibratorService);
                if (v != null && v.HasVibrator) v.Vibrate(VibrationEffect.CreateOneShot(80, VibrationEffect.DefaultAmplitude));
            }
            catch { }
        }

        protected override void OnPause()
        {
            try { _camera?.SetPreviewCallbackWithBuffer(null); _camera?.StopPreview(); _camera?.Release(); } catch { }
            _camera = null;
            base.OnPause();
        }

        /// <summary>
        /// 相机预览 Surface。关键：Surface 的宽高比必须等于「旋转后」的预览宽高比，
        /// 否则竖屏手机会把横屏预览流拉伸铺满整屏，画面变形。这里按屏幕尺寸做等比缩放
        /// （cover：填满屏幕、居中裁切），保证画面不拉伸。
        /// </summary>
        private sealed class CameraPreview : SurfaceView, ISurfaceHolderCallback
        {
            private Camera _camera;
            private int _frameW, _frameH;   // 预览原始帧尺寸（通常横向）
            private int _rotation;          // SetDisplayOrientation 的角度

            public CameraPreview(Context ctx) : base(ctx)
            {
                Holder.AddCallback(this);
            }

            public void SetCamera(Camera camera, int frameW, int frameH, int rotation)
            {
                _camera = camera;
                _frameW = frameW;
                _frameH = frameH;
                _rotation = rotation;
                try { _camera?.SetPreviewDisplay(Holder); } catch { }
                FitSurface();
            }

            protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
            {
                base.OnSizeChanged(w, h, oldw, oldh);
                FitSurface();
            }

            /// <summary>按旋转后的宽高比等比放大到铺满屏幕（cover），居中显示。</summary>
            private void FitSurface()
            {
                if (_frameW <= 0 || _frameH <= 0) return;
                var parent = Parent as View;
                int boxW = parent != null && parent.Width > 0 ? parent.Width : Width;
                int boxH = parent != null && parent.Height > 0 ? parent.Height : Height;
                if (boxW <= 0 || boxH <= 0) return;

                // 90/270 度时宽高互换，得到用户实际看到的画面方向。
                bool swapped = ((_rotation / 90) % 2) == 1;
                float aspect = swapped
                    ? (float)_frameH / _frameW   // 竖屏：宽=原高，高=原宽
                    : (float)_frameW / _frameH;  // 横屏：宽=原宽，高=原高

                int newW, newH;
                if ((float)boxW / boxH > aspect)
                {
                    // 容器更“宽”：以宽度填满，高度按比例（会超过容器，形成上下裁切）
                    newW = boxW;
                    newH = (int)(boxW / aspect + 0.5f);
                }
                else
                {
                    // 容器更“高”：以高度填满，宽度按比例（左右裁切）
                    newH = boxH;
                    newW = (int)(boxH * aspect + 0.5f);
                }

                var lp = LayoutParameters as FrameLayout.LayoutParams;
                if (lp == null)
                    lp = new FrameLayout.LayoutParams(newW, newH);
                lp.Width = newW;
                lp.Height = newH;
                lp.Gravity = GravityFlags.Center;
                LayoutParameters = lp;
            }

            public void SurfaceCreated(ISurfaceHolder holder) { if (_camera != null) { try { _camera.SetPreviewDisplay(holder); _camera.StartPreview(); } catch { } } }
            public void SurfaceChanged(ISurfaceHolder holder, Format f, int w, int h)
            {
                FitSurface();
                if (_camera != null) { try { _camera.StopPreview(); _camera.SetPreviewDisplay(holder); _camera.StartPreview(); } catch { } }
            }
            public void SurfaceDestroyed(ISurfaceHolder holder) { }
        }
    }
}

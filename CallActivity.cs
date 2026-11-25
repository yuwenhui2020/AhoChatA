using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 通话界面（语音 / 视频共用）。
    ///
    /// 视频布局：对端画面铺满，本地预览右上角小窗；
    /// 语音布局：隐藏画面，显示头像 + 名字 + 状态。
    /// 媒体全部由 VoiceCallManager 掌管，这里只负责把 Surface 交出去 + 显示状态。
    /// </summary>
    [Activity(Label = "@string/act_call", Theme = "@style/AppTheme.NoActionBar",
              LaunchMode = LaunchMode.SingleTop,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class CallActivity : BaseActivity
    {
        private const int ReqMic = 7401;
        private const int ReqCamera = 7402;

        private VoiceCallManager _call;
        private TextureView _remote, _local;
        private View _placeholder, _localBox, _incomingBar, _buttons;
        private TextView _peer, _note, _title, _timer;
        private FrameLayout _avatar;
        private FrameLayout _mute, _speaker, _switch, _hangup, _accept, _reject;
        private IconView _icMute, _icSpeaker, _icSwitch, _icHangup, _icAccept, _icReject;

        private Surface _remoteSurface;
        private Handler _ui;
        private bool _subscribed;
        private bool _cameraPermissionAsked;
        private bool _cameraPermissionDenied;
        private Action _pendingAfterCamera;
        private SurfaceTexture _localTexture;
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        private int Dp(int v) => IosStyle.Dp(this, v);

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_call);
            try { PrivacyActivity.ApplySecureFlag(this); } catch { }
            try { Window.AddFlags(WindowManagerFlags.KeepScreenOn); } catch { }
            try
            {
                Window.SetStatusBarColor(Color.ParseColor("#0b0d10"));
                Window.SetNavigationBarColor(Color.ParseColor("#0b0d10"));
            }
            catch { }

            _ui = new Handler(Looper.MainLooper);

            _remote = FindViewById<TextureView>(Resource.Id.callRemote);
            _local = FindViewById<TextureView>(Resource.Id.callLocal);
            _placeholder = FindViewById(Resource.Id.callPlaceholder);
            _localBox = FindViewById(Resource.Id.callLocalBox);
            _incomingBar = FindViewById(Resource.Id.callIncomingBar);
            _buttons = FindViewById(Resource.Id.callButtons);
            _avatar = FindViewById<FrameLayout>(Resource.Id.callAvatar);
            if (_avatar != null)
            {
                var person = new IconView(this, IconKind.Person) { IconColor = unchecked((int)0xFF9AA0A6), Thickness = 1.5f };
                _avatar.AddView(person, new FrameLayout.LayoutParams(Dp(52), Dp(52)) { Gravity = GravityFlags.Center });
            }
            _peer = FindViewById<TextView>(Resource.Id.callPeer);
            _note = FindViewById<TextView>(Resource.Id.callNote);
            _title = FindViewById<TextView>(Resource.Id.callTitle);
            _timer = FindViewById<TextView>(Resource.Id.callTimer);
            _mute = FindViewById<FrameLayout>(Resource.Id.callMute);
            _speaker = FindViewById<FrameLayout>(Resource.Id.callSpeaker);
            _switch = FindViewById<FrameLayout>(Resource.Id.callSwitch);
            _hangup = FindViewById<FrameLayout>(Resource.Id.callHangup);
            _accept = FindViewById<FrameLayout>(Resource.Id.callAccept);
            _reject = FindViewById<FrameLayout>(Resource.Id.callReject);

            _icMute = AddIcon(_mute, IconKind.Mic);
            _icSpeaker = AddIcon(_speaker, IconKind.Speaker);
            _icSwitch = AddIcon(_switch, IconKind.Rotate);
            _icHangup = AddIcon(_hangup, IconKind.Phone);
            _icAccept = AddIcon(_accept, IconKind.Phone);
            _icReject = AddIcon(_reject, IconKind.Phone);

            _remote.SurfaceTextureListener = new TexListener(
                (tex, w, h) =>
                {
                    _remoteSurface = new Surface(tex);
                    _call?.AttachRemoteSurface(_remoteSurface);
                },
                null,
                () => { try { _remoteSurface?.Release(); } catch { } _remoteSurface = null; });

            _local.SurfaceTextureListener = new TexListener(
                (tex, w, h) =>
                {
                    _localTexture = tex;
                    if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted)
                        _call?.AttachLocalPreview(tex);
                },
                null,
                null);

            _hangup.SetBackgroundResource(Resource.Drawable.call_btn_danger);
            _reject.SetBackgroundResource(Resource.Drawable.call_btn_danger);
            _accept.SetBackgroundResource(Resource.Drawable.call_btn_ok);

            _hangup.Click += (s, e) => { _call?.Hangup(L10n.T("已挂断"), true); Finish(); };
            _reject.Click += (s, e) => { _call?.Hangup(L10n.T("已拒接"), true); Finish(); };
            _accept.Click += (s, e) => AskMicThen(() =>
            {
                var snap = _call?.Snapshot;
                if (snap != null && snap.Mode == CallMode.Video)
                    AskCameraThen(() => _call?.Accept());
                else _call?.Accept();
            });

            _mute.Click += (s, e) =>
            {
                var snap = _call?.Snapshot;
                if (snap == null) return;
                _call.SetMuted(!snap.Muted);
            };
            _speaker.Click += (s, e) =>
            {
                var snap = _call?.Snapshot;
                if (snap == null) return;
                _call.SetSpeaker(!snap.Speaker);
            };
            _switch.Click += (s, e) => _call?.SwitchCamera();

            // 来电页可能被系统回收后重建：靠 Intent 标记恢复"正在来电"的界面形态
            bool incoming = Intent != null && Intent.GetBooleanExtra("incoming", false);
            if (incoming) ShowIncoming(true);
        }

        protected override void OnResume()
        {
            base.OnResume();
            // 通话页是沉浸式黑底：BaseActivity 会把系统栏刷成主题色，这里覆盖回深色。
            try
            {
                Window.SetStatusBarColor(Color.ParseColor("#0b0d10"));
                Window.SetNavigationBarColor(Color.ParseColor("#0b0d10"));
                if (Window.DecorView != null)
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)SystemUiFlags.Visible;
            }
            catch { }
            Bind();
        }

        private void Bind()
        {
            _call = VoiceCallManager.Current;
            if (_call == null)
            {
                L10n.Toast(this, L10n.T("通话模块未就绪"), ToastLength.Short).Show();
                Finish();
                return;
            }
            if (_remoteSurface != null) _call.AttachRemoteSurface(_remoteSurface);
            if (_localTexture != null && CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted)
                _call.AttachLocalPreview(_localTexture);
            if (!_subscribed)
            {
                _call.StateChanged += OnCallState;
                _subscribed = true;
            }
            Apply(_call.Snapshot);
            _ui.RemoveCallbacks(Tick);
            _ui.PostDelayed(Tick, 500);
        }

        protected override void OnPause()
        {
            // 通话仍在进行时不取消订阅：界面退到后台也要能感知挂断
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            try { _ui?.RemoveCallbacks(Tick); } catch { }
            if (_subscribed && _call != null) _call.StateChanged -= OnCallState;
            _subscribed = false;
            try { _remoteSurface?.Release(); } catch { }
            _remoteSurface = null;

            // 界面销毁但通话还在（比如来电时被返回键关掉）→ 不主动挂断，
            // 用户可以从通知栏/重新进入继续；只有 Idle 才真正结束。
            if (_call != null && _call.Snapshot.Phase == CallPhase.Idle) { /* nothing */ }
            base.OnDestroy();
        }

        public override void OnBackPressed()
        {
            // 通话中按返回 = 挂断（比"藏起来但还在响"更符合预期）
            if (_call != null && _call.Snapshot.Phase != CallPhase.Idle)
            {
                _call.Hangup(L10n.T("已挂断"), true);
            }
            base.OnBackPressed();
        }

        private void OnCallState(CallSnapshot s)
        {
            RunOnUiThread(() => Apply(s));
        }

        private void Apply(CallSnapshot s)
        {
            if (s == null) return;

            if (s.Phase == CallPhase.Idle)
            {
                Finish();
                return;
            }

            bool video = s.Mode == CallMode.Video;
            _title.Text = (s.FromMe ? L10n.T("去电 · ") : L10n.T("来电 · ")) + (video ? L10n.T("视频通话") : L10n.T("语音通话"));
            _peer.Text = s.PeerName ?? s.PeerId ?? "";
            _note.Text = s.Note ?? "";
            _note.Visibility = s.Phase == CallPhase.Active ? ViewStates.Gone : ViewStates.Visible;
            _placeholder.Visibility = (s.Phase == CallPhase.Active && video) ? ViewStates.Gone : ViewStates.Visible;
            _localBox.Visibility = (s.Phase == CallPhase.Active && video) ? ViewStates.Visible : ViewStates.Gone;
            _switch.Visibility = video ? ViewStates.Visible : ViewStates.Gone;

            bool incoming = s.Phase == CallPhase.Ringing;
            ShowIncoming(incoming);
            if (_buttons != null) _buttons.Visibility = incoming ? ViewStates.Gone : ViewStates.Visible;

            if (_icMute != null)
            {
                _icMute.Kind = s.Muted ? IconKind.Mute : IconKind.Mic;
                _icMute.IconColor = s.Muted ? unchecked((int)0xFFFF6B6B) : unchecked((int)0xFFFFFFFF);
                _icMute.Invalidate();
            }
            if (_icSpeaker != null)
            {
                _icSpeaker.IconColor = s.Speaker ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFF8E9099);
                _icSpeaker.Invalidate();
            }

            if (s.Phase == CallPhase.Active)
            {
                if (!_watch.IsRunning) _watch.Start();
            }
            else
            {
                _watch.Reset();
                _timer.Text = video ? L10n.T("视频通话") : L10n.T("语音通话");
            }

            if (s.Phase == CallPhase.Active && video)
            {
                try { _local.Rotation = 0; } catch { }
                try { _local.ScaleX = -1f; } catch { }   // 前置预览镜像，符合直觉
                if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted)
                {
                    _cameraPermissionDenied = false;
                    _call?.TryStartVideo();
                }
                else if (!_cameraPermissionAsked)
                {
                    AskCameraThen(() => _call?.TryStartVideo());
                }
                else if (_cameraPermissionDenied)
                {
                    _note.Text = L10n.T("视频不可用：未授予摄像头权限");
                    _note.Visibility = ViewStates.Visible;
                }
            }
        }

        private void ShowIncoming(bool on)
        {
            _incomingBar.Visibility = on ? ViewStates.Visible : ViewStates.Gone;
        }

        private void Tick()
        {
            var s = _call?.Snapshot;
            if (s == null) return;
            if (s.Phase == CallPhase.Idle) { Finish(); return; }
            if (s.Phase == CallPhase.Active)
            {
                var t = _watch.Elapsed;
                _timer.Text = string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            }
            else if (s.Phase == CallPhase.Dialing)
            {
                _timer.Text = L10n.T("正在连接…");
            }
            else if (s.Phase == CallPhase.Ringing)
            {
                _timer.Text = L10n.T("邀请你通话");
            }
            _ui.PostDelayed(Tick, 500);
        }

        private IconView AddIcon(FrameLayout box, IconKind kind)
        {
            if (box == null) return null;
            box.RemoveAllViews();
            var ic = new IconView(this, kind) { IconColor = unchecked((int)0xFFFFFFFF), Thickness = 1.8f };
            box.AddView(ic, new FrameLayout.LayoutParams(Dp(26), Dp(26)) { Gravity = GravityFlags.Center });
            return ic;
        }

        private void AskMicThen(Action act)
        {
            if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted)
            {
                act();
                return;
            }
            _pendingAfterMic = act;
            RequestPermissions(new[] { Android.Manifest.Permission.RecordAudio }, ReqMic);
        }

        private Action _pendingAfterMic;

        private void AskCameraThen(Action act)
        {
            if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted)
            {
                act?.Invoke();
                return;
            }
            _pendingAfterCamera = act;
            _cameraPermissionAsked = true;
            RequestPermissions(new[] { Android.Manifest.Permission.Camera }, ReqCamera);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == ReqCamera)
            {
                bool cameraOk = grantResults != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted;
                var pending = _pendingAfterCamera;
                _pendingAfterCamera = null;
                if (cameraOk)
                {
                    _cameraPermissionDenied = false;
                    pending?.Invoke();
                    if (_localTexture != null) _call?.AttachLocalPreview(_localTexture);
                    return;
                }
                _cameraPermissionDenied = true;
                if (_call?.Snapshot?.Phase == CallPhase.Ringing) pending?.Invoke();
                if (_note != null)
                {
                    _note.Text = L10n.T("视频不可用：未授予摄像头权限；语音通话会继续");
                    _note.Visibility = ViewStates.Visible;
                }
                L10n.Toast(this, L10n.T("未获得摄像头权限，语音通话继续"), ToastLength.Short).Show();
                return;
            }
            if (requestCode != ReqMic) return;
            bool ok = grantResults != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            if (ok)
            {
                var a = _pendingAfterMic;
                _pendingAfterMic = null;
                a?.Invoke();
            }
            else
            {
                L10n.Toast(this, L10n.T("没有麦克风权限，无法通话"), ToastLength.Short).Show();
                _call?.Hangup(L10n.T("已取消"), true);
                Finish();
            }
        }

        /// <summary>把 TextureView 的三个回调拆成三个委托，省得为两个视图写两个类。</summary>
        private sealed class TexListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
        {
            private readonly Action<SurfaceTexture, int, int> _avail;
            private readonly Action<int, int> _size;
            private readonly Action _destroyed;

            public TexListener(Action<SurfaceTexture, int, int> avail, Action<int, int> size, Action destroyed)
            { _avail = avail; _size = size; _destroyed = destroyed; }

            public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) { _avail?.Invoke(surface, width, height); }
            public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { _size?.Invoke(width, height); }
            public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) { _destroyed?.Invoke(); return true; }
            public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
        }
    }
}

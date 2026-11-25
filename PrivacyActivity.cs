using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace AhoChatA
{
    /// <summary>
    /// 隐私与安全。
    ///
    /// 说明一下能做到什么、做不到什么（不吹牛）：
    ///  · **能**：实时看到本机有没有在共享摄像头/屏幕/麦克风/画笔，一键全停；
    ///         检测系统层面当前是否有录音会话、是否有相机被占用；
    ///         列出本机所有申请了「录音 / 相机 / 悬浮窗 / 无障碍 / 短信 / 全盘文件」的应用，
    ///         点进去就能去系统里关掉——这是防间谍应用真正有效的一步；
    ///         给本 App 打开防截屏（FLAG_SECURE），别人录屏/截屏只能拿到黑屏。
    ///  · **不能**：Android 不允许普通应用查询"是哪个包名正在录屏/录音"。
    ///         系统只给到"当前存在录音会话"这种匿名信息，包名要系统权限才拿得到。
    ///         所以这里给的是"占用状态 + 可疑权限清单"，而不是"某某 App 正在录你"。
    /// </summary>
    [Activity(Label = "@string/act_privacy", Theme = "@style/AppTheme.NoActionBar",
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class PrivacyActivity : BaseActivity
    {
        private static readonly Color BgColor = new Color(IosStyle.Background);
        private static readonly Color CardColor = new Color(IosStyle.Surface);
        private static readonly Color TextMain = new Color(IosStyle.Text);
        private static readonly Color TextSub = new Color(IosStyle.Secondary);
        private static readonly Color LineColor = new Color(IosStyle.Separator);
        private static readonly Color Accent = new Color(IosStyle.Accent);
        private static readonly Color Warn = new Color(IosStyle.Danger);

        private LinearLayout _statusBox, _appsBox;
        private TextView _micState, _camState, _screenState, _appsHint;
        private Switch _secureSw, _otpSw;
        private Timer _timer;
        private bool _destroyed;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            BuildUi();
            RefreshStatus();

            _timer = new Timer(_ => RunOnUiThread(() => { if (!_destroyed && !IsFinishing) RefreshStatus(); }), null, 2000, 2000);

            // 扫描已安装应用比较慢，放后台
            new Thread(LoadApps) { IsBackground = true }.Start();
        }

        protected override void OnDestroy()
        {
            _destroyed = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            base.OnDestroy();
        }

        /* ===================== 界面 ===================== */

        private void BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(BgColor);

            // 顶栏
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetBackgroundColor(CardColor);
            bar.SetPadding(Dp(4), 0, Dp(8), 0);

            var back = new FrameLayout(this) { ContentDescription = L10n.T("返回") };
            back.LayoutParameters = new LinearLayout.LayoutParams(Dp(44), Dp(44));
            var bi = new IconView(this, IconKind.Back) { IconColor = unchecked((int)0xFF111111) };
            back.AddView(bi, new FrameLayout.LayoutParams(Dp(24), Dp(24)) { Gravity = GravityFlags.Center });
            back.Click += (s, e) => Finish();
            bar.AddView(back);

            var title = new TextView(this) { Text = L10n.T("隐私与安全"), TextSize = IosStyle.TitleSize };
            title.SetTextColor(TextMain);
            title.SetTypeface(null, TypefaceStyle.Bold);
            title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            bar.AddView(title);
            bar.SetMinimumHeight(Dp(IosStyle.ToolbarHeight));
            root.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var scroll = new ScrollView(this);
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.SetPadding(0, 0, 0, Dp(IosStyle.PageInset));

            /* ---- 本机占用状态 ---- */
            body.AddView(SectionLabel(L10n.T("本机共享状态")));
            _statusBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var statusCard = Card();
            statusCard.AddView(Row(L10n.T("摄像头"), out _camState));
            statusCard.AddView(Divider());
            statusCard.AddView(Row(L10n.T("屏幕（录屏）"), out _screenState));
            statusCard.AddView(Divider());
            statusCard.AddView(Row(L10n.T("麦克风"), out _micState));
            statusCard.AddView(Divider());

            var stopAll = new TextView(this) { Text = L10n.T("停止本机全部共享"), TextSize = IosStyle.BodySize, Gravity = GravityFlags.Center };
            stopAll.SetTextColor(Warn);
            stopAll.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            stopAll.SetMinimumHeight(Dp(IosStyle.RowMinHeight));
            stopAll.Click += (s, e) => StopAllShares();
            statusCard.AddView(stopAll);
            body.AddView(statusCard, CardLp());

            /* ---- 防护 ---- */
            body.AddView(SectionLabel(L10n.T("防护")));
            var guard = Card();
            var secureRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            secureRow.SetGravity(GravityFlags.CenterVertical);
            secureRow.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            secureRow.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var secureCol = new LinearLayout(this) { Orientation = Orientation.Vertical };
            secureCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            var st = new TextView(this) { Text = L10n.T("防截屏 / 防录屏"), TextSize = IosStyle.BodySize };
            st.SetTextColor(TextMain);
            secureCol.AddView(st);
            var sd = new TextView(this) { Text = L10n.T("开启后，其他应用录屏或截屏时，聊天与通话画面会是黑屏"), TextSize = IosStyle.CaptionSize };
            sd.SetTextColor(TextSub);
            sd.SetPadding(0, Dp(3), Dp(8), 0);
            secureCol.AddView(sd);
            secureRow.AddView(secureCol);

            _secureSw = new Switch(this) { Checked = LoadSecure() };
            _secureSw.CheckedChange += (s, e) =>
            {
                SaveSecure(e.IsChecked);
                ApplySecureToWindows();
                L10n.Toast(this, e.IsChecked ? L10n.T("已开启防截屏") : L10n.T("已关闭防截屏"), ToastLength.Short).Show();
            };
            _secureSw.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            _secureSw.SetMinimumWidth(Dp(IosStyle.ControlHeight));
            secureRow.AddView(_secureSw);
            guard.AddView(secureRow);
            body.AddView(guard, CardLp());

            var note = new TextView(this)
            {
                Text = L10n.T("开启后需要重新进入聊天页/通话页才会生效。"),
                TextSize = IosStyle.CaptionSize
            };
            note.SetTextColor(TextSub);
            note.SetPadding(Dp(24), Dp(6), Dp(24), 0);
            body.AddView(note);

            /* ---- 双因素认证 ---- */
            body.AddView(SectionLabel(L10n.T("双因素认证（2FA）")));
            var otpCard = Card();
            var otpRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            otpRow.SetGravity(GravityFlags.CenterVertical);
            otpRow.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            otpRow.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var otpCol = new LinearLayout(this) { Orientation = Orientation.Vertical };
            otpCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            var ot = new TextView(this) { Text = L10n.T("网页访问动态口令"), TextSize = IosStyle.BodySize };
            ot.SetTextColor(TextMain);
            otpCol.AddView(ot);
            var od = new TextView(this)
            {
                Text = L10n.T("开启后，浏览器访问本机网页端 / 文件 / 画笔前要先输 6 位口令；口令显示在")
                     + L10n.T("「设备」页「本机」下方，每 30 秒变一次，每台设备各有一套。")
                     + L10n.T("摄像头与屏幕共享不校验（播放器输不了验证码）。"),
                TextSize = IosStyle.CaptionSize
            };
            od.SetTextColor(TextSub);
            od.SetPadding(0, Dp(3), Dp(8), 0);
            od.SetLineSpacing(0, 1.3f);
            otpCol.AddView(od);
            otpRow.AddView(otpCol);

            _otpSw = new Switch(this) { Checked = OtpAuth.Enabled };
            _otpSw.CheckedChange += (s, e) =>
            {
                OtpAuth.Enabled = e.IsChecked;
                L10n.Toast(this, e.IsChecked ? L10n.T("已开启：口令见「设备」页") : L10n.T("已关闭：浏览器可直接访问"),
                    ToastLength.Short).Show();
            };
            _otpSw.SetMinimumHeight(Dp(IosStyle.ControlHeight));
            _otpSw.SetMinimumWidth(Dp(IosStyle.ControlHeight));
            otpRow.AddView(_otpSw);
            otpCard.AddView(otpRow);
            body.AddView(otpCard, CardLp());

            /* ---- 应用权限审计 ---- */
            body.AddView(SectionLabel(L10n.T("会录音 / 拍照 / 录屏 / 读文件的应用")));
            _appsHint = new TextView(this) { Text = L10n.T("正在扫描已安装应用…"), TextSize = IosStyle.CaptionSize };
            _appsHint.SetTextColor(TextSub);
            _appsHint.SetPadding(Dp(24), 0, Dp(24), Dp(7));
            body.AddView(_appsHint);

            _appsBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.AddView(_appsBox);

            /* ---- 说明 ---- */
            var explain = new TextView(this)
            {
                Text = L10n.T("为什么不能直接显示「哪个 App 正在录音」？\n") +
                       L10n.T("Android 只允许系统组件（设置里的隐私指示器）知道具体包名，普通应用最多只能拿到") +
                       L10n.T("「当前存在录音会话」这类匿名状态。所以这里做的是两件事：\n") +
                       L10n.T("① 实时告诉你本机的摄像头 / 麦克风 / 屏幕现在有没有被占用；\n") +
                       L10n.T("② 把系统里申请了这些敏感权限的应用列出来，你点进去就能关掉权限。\n") +
                       L10n.T("看到麦克风被占用、本机又没在通话时，按上面的清单逐个排查最有效。"),
                TextSize = IosStyle.CaptionSize
            };
            explain.SetTextColor(TextSub);
            explain.SetPadding(Dp(24), Dp(16), Dp(24), Dp(10));
            explain.SetLineSpacing(0, 1.35f);
            body.AddView(explain);

            scroll.AddView(body);
            root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            SetContentView(root);

            ApplySecureToWindows();
        }

        private View Row(string label, out TextView value)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var t = new TextView(this) { Text = label, TextSize = IosStyle.BodySize };
            t.SetTextColor(TextMain);
            t.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            row.AddView(t);

            value = new TextView(this) { Text = "—", TextSize = IosStyle.CaptionSize };
            value.SetTextColor(TextSub);
            value.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            value.Gravity = GravityFlags.End;
            row.AddView(value);
            return row;
        }

        private LinearLayout Card()
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.Background = RoundBg(CardColor, IosStyle.CardRadius);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.LeftMargin = Dp(IosStyle.PageInset); lp.RightMargin = Dp(IosStyle.PageInset);
            return card;
        }

        private LinearLayout.LayoutParams CardLp()
        {
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.LeftMargin = Dp(IosStyle.PageInset); lp.RightMargin = Dp(IosStyle.PageInset);
            return lp;
        }

        private void AddCardBody(LinearLayout body, LinearLayout card)
        {
            body.AddView(card, CardLp());
        }

        private TextView SectionLabel(string text)
        {
            var t = new TextView(this) { Text = text, TextSize = IosStyle.CaptionSize };
            t.SetTextColor(TextSub);
            t.SetPadding(Dp(IosStyle.PageInset), Dp(IosStyle.SectionGap), Dp(IosStyle.PageInset), Dp(8));
            return t;
        }

        private View Divider()
        {
            var v = new View(this);
            v.SetBackgroundColor(LineColor);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1);
            lp.LeftMargin = Dp(16);
            v.LayoutParameters = lp;
            return v;
        }

        private Android.Graphics.Drawables.GradientDrawable RoundBg(Color c, int radiusDp)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetColor(c.ToArgb());
            d.SetCornerRadius(Dp(radiusDp));
            return d;
        }

        /* ===================== 状态 ===================== */

        private void RefreshStatus()
        {
            var cam = IpCameraServer.Instance;
            bool camOn = cam != null && cam.IsRunning && cam.Source == ShareSource.Camera;
            bool scrOn = cam != null && cam.IsRunning && cam.Source == ShareSource.Screen;
            var call = VoiceCallManager.Current;
            bool micOn = call != null && call.IsActive();

            SetState(_camState, camOn, L10n.T("本 App 正在共享"));
            SetState(_screenState, scrOn, L10n.T("本 App 正在共享"));

            // 麦克风：本 App 通话优先；否则看系统层面有没有别的录音会话
            if (micOn)
            {
                _micState.Text = L10n.T("本 App 通话中");
                _micState.SetTextColor(Accent);
            }
            else
            {
                int n = SystemRecordingCount();
                if (n > 0)
                {
                    _micState.Text = L10n.T("系统有 ") + n + L10n.T(" 个录音会话");
                    _micState.SetTextColor(Warn);
                }
                else
                {
                    _micState.Text = L10n.T("空闲");
                    _micState.SetTextColor(TextSub);
                }
            }

            // 相机：尝试系统接口（多数 ROM 不开放给普通应用，失败就只在共享时提示）
            try
            {
                if (!camOn)
                {
                    string c = ActiveCameraSummary();
                    if (!string.IsNullOrEmpty(c))
                    {
                        _camState.Text = c;
                        _camState.SetTextColor(Warn);
                    }
                }
            }
            catch { }
        }

        private void SetState(TextView t, bool on, string onText)
        {
            if (t == null) return;
            t.Text = on ? onText : L10n.T("未占用");
            t.SetTextColor(on ? Accent : TextSub);
        }

        /// <summary>系统里当前有几路录音会话（不含本 App）。拿不到就返回 0。</summary>
        private int SystemRecordingCount()
        {
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.N) return 0;
                var am = (Android.Media.AudioManager)GetSystemService(AudioService);
                var configs = am?.ActiveRecordingConfigurations;
                if (configs == null) return 0;
                return configs.Count;
            }
            catch { return 0; }
        }

        /// <summary>反射拿"正在使用的摄像头"，拿不到返回空串。</summary>
        private string ActiveCameraSummary()
        {
            try
            {
                var cm = (CameraManager)GetSystemService(CameraService);
                if (cm == null) return "";
                var m = cm.Class.GetMethod("getActiveCameraIds");
                var r = m.Invoke(cm);
                var arr = r as System.Collections.ICollection;
                if (arr == null || arr.Count == 0) return "";
                return L10n.T("系统有 ") + arr.Count + L10n.T(" 个相机被占用");
            }
            catch { return ""; }
        }

        private void StopAllShares()
        {
            try { IpCameraServer.Instance.Stop(); } catch { }
            try { StopService(new Intent(this, typeof(ScreenCastService))); } catch { }
            try { ScreenDrawService.Hide(this); } catch { }
            try { VoiceCallManager.Current?.Hangup(L10n.T("已结束通话"), true); } catch { }
            L10n.Toast(this, L10n.T("已停止本机全部共享"), ToastLength.Short).Show();
            RefreshStatus();
        }

        /* ===================== 防截屏 ===================== */

        private static bool LoadSecure()
        {
            try
            {
                var ctx = Android.App.Application.Context;
                return ctx.GetSharedPreferences("Mchat", FileCreationMode.Private)
                          .GetBoolean("secureScreen", false);
            }
            catch { return false; }
        }

        private static void SaveSecure(bool on)
        {
            try
            {
                var ctx = Android.App.Application.Context;
                ctx.GetSharedPreferences("Mchat", FileCreationMode.Private)
                   .Edit().PutBoolean("secureScreen", on).Apply();
            }
            catch { }
        }

        /// <summary>当前页立刻生效（其他页面下次进入时由各自读取设置）。</summary>
        private void ApplySecureToWindows()
        {
            try
            {
                if (LoadSecure()) Window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
                else Window.ClearFlags(WindowManagerFlags.Secure);
            }
            catch { }
        }

        public static void ApplySecureFlag(Activity act)
        {
            try
            {
                if (act == null) return;
                if (LoadSecure()) act.Window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
                else act.Window.ClearFlags(WindowManagerFlags.Secure);
            }
            catch { }
        }

        /* ===================== 权限审计 ===================== */

        private static readonly string[] DangerPerms =
        {
            "android.permission.RECORD_AUDIO",
            "android.permission.CAMERA",
            "android.permission.SYSTEM_ALERT_WINDOW",
            "android.permission.BIND_ACCESSIBILITY_SERVICE",
            "android.permission.READ_SMS",
            "android.permission.RECEIVE_SMS",
            "android.permission.READ_CALL_LOG",
            "android.permission.ACCESS_FINE_LOCATION",
            "android.permission.MANAGE_EXTERNAL_STORAGE",
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.PACKAGE_USAGE_STATS"
        };

        private static readonly Dictionary<string, string> PermLabel = new Dictionary<string, string>
        {
            ["android.permission.RECORD_AUDIO"] = L10n.T("录音"),
            ["android.permission.CAMERA"] = L10n.T("相机"),
            ["android.permission.SYSTEM_ALERT_WINDOW"] = L10n.T("悬浮窗"),
            ["android.permission.BIND_ACCESSIBILITY_SERVICE"] = L10n.T("无障碍"),
            ["android.permission.READ_SMS"] = L10n.T("读短信"),
            ["android.permission.RECEIVE_SMS"] = L10n.T("收短信"),
            ["android.permission.READ_CALL_LOG"] = L10n.T("通话记录"),
            ["android.permission.ACCESS_FINE_LOCATION"] = L10n.T("定位"),
            ["android.permission.MANAGE_EXTERNAL_STORAGE"] = L10n.T("全盘文件"),
            ["android.permission.READ_EXTERNAL_STORAGE"] = L10n.T("读文件"),
            ["android.permission.PACKAGE_USAGE_STATS"] = L10n.T("使用情况")
        };

        private sealed class AppRisk
        {
            public string Pkg;
            public string Name;
            public List<string> Tags = new List<string>();
        }

        private void LoadApps()
        {
            var list = new List<AppRisk>();
            try
            {
                var pm = PackageManager;
                var pkgs = pm.GetInstalledPackages(PackageInfoFlags.Permissions);
                foreach (var pi in pkgs)
                {
                    if (pi.RequestedPermissions == null) continue;
                    var tags = new List<string>();
                    foreach (var p in pi.RequestedPermissions)
                    {
                        if (Array.IndexOf(DangerPerms, p) < 0) continue;
                        string label;
                        if (PermLabel.TryGetValue(p, out label)) tags.Add(label);
                    }
                    if (tags.Count == 0) continue;

                    string name = pi.ApplicationInfo != null
                        ? pm.GetApplicationLabel(pi.ApplicationInfo).ToString()
                        : pi.PackageName;

                    list.Add(new AppRisk { Pkg = pi.PackageName, Name = name, Tags = tags });
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Privacy", "扫描应用失败：" + ex.Message);
            }

            list.Sort((a, b) => b.Tags.Count.CompareTo(a.Tags.Count));

            RunOnUiThread(() =>
            {
                if (_destroyed || IsFinishing || _appsBox == null) return;
                _appsBox.RemoveAllViews();

                if (list.Count == 0)
                {
                    _appsHint.Text = L10n.T("没有发现申请敏感权限的应用。");
                    return;
                }

                _appsHint.Text = L10n.T("共 ") + list.Count + L10n.T(" 个") + (list.Count > 60 ? L10n.T("，显示前 60 个") : "")
                    + L10n.T("。点任意一项可去系统里关掉它的权限。");

                var card = Card();
                int shown = 0;
                foreach (var a in list)
                {
                    if (shown++ > 0) card.AddView(Divider());
                    card.AddView(AppRow(a));
                    if (shown >= 60) break;   // 最多列 60 个，避免卡顿
                }
                AddCardBody(_appsBox, card);
                if (ThemeManager.IsNight(this)) ApplyThemeLive();
            });
        }

        private View AppRow(AppRisk a)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(IosStyle.CardInset), Dp(10), Dp(IosStyle.CardInset), Dp(10));
            row.SetMinimumHeight(Dp(IosStyle.RowMinHeight));

            var av = new TextView(this)
            {
                Text = (string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1)),
                Gravity = GravityFlags.Center,
                TextSize = IosStyle.BodySize
            };
            av.SetTextColor(Color.White);
            av.SetTypeface(null, TypefaceStyle.Bold);
            av.Background = RoundBg(Color.Rgb(0x8E, 0x90, 0x99), 11);
            av.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38));
            row.AddView(av);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(6), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

            var nm = new TextView(this) { Text = a.Name, TextSize = IosStyle.BodySize };
            nm.SetTextColor(TextMain);
            col.AddView(nm);

            var tags = new TextView(this) { Text = string.Join(" · ", a.Tags.ToArray()), TextSize = IosStyle.CaptionSize };
            tags.SetTextColor(a.Tags.Contains(L10n.T("录音")) || a.Tags.Contains(L10n.T("相机")) ? Warn : TextSub);
            tags.SetPadding(0, Dp(3), 0, 0);
            col.AddView(tags);
            row.AddView(col);

            var chev = new IconView(this, IconKind.ChevronRight) { IconColor = unchecked((int)0xFFC7C9CE), Thickness = 1.5f };
            row.AddView(chev, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

            row.Click += (s, e) => OpenAppInfo(a.Pkg);
            return row;
        }

        private void OpenAppInfo(string pkg)
        {
            try
            {
                var i = new Intent(Settings.ActionApplicationDetailsSettings,
                                   Android.Net.Uri.Parse("package:" + pkg));
                i.AddFlags(ActivityFlags.NewTask);
                StartActivity(i);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("打不开应用信息：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private int Dp(int v) { return IosStyle.Dp(this, v); }
    }
}

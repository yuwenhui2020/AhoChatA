using System;
using System.Collections.Generic;
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Views.Accessibility;
using desk;

namespace AhoChatA
{
    /// <summary>
    /// 安卓被控端的输入注入：把查看端发来的鼠标/键盘事件翻译成系统手势与文本编辑。
    ///
    /// 参考 rustdesk 的 InputService（它是 droidVNC-NG 的思路）做两件事：
    ///   1. **手势续接**（Android 8+ 的 StrokeDescription.continueStroke）：按下时先不派发，
    ///      移动/长按定时器触发时用"续接上一段笔画"的方式派发，
    ///      这样一次按下能被拆成很多段连续笔画，拖动、长按、点击三种语义都能正确识别——
    ///      如果每移动一次就 dispatch 一个独立短划，系统会当成"反复轻点"，永远拖不动。
    ///   2. **右键 → 长按**：手机没有右键，长按才是"上下文菜单"，
    ///      所以查看端的右键在这里被翻译成一次时长=长按时长的定点手势。
    ///
    /// 键盘走"聚焦输入框改文本"的路子（ACTION_SET_TEXT），不依赖 Android 13 才有的
    /// InputMethod 接口，兼容面更广；非输入框场景只处理返回/主页/最近任务/音量。
    /// </summary>
    [Service(Name = "com.Ahochat.android.RdInputService",
        Exported = true,
        Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
        Label = "AhoChat Remote Input")]
    [IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
    [MetaData("android.accessibilityservice", Resource = "@xml/rd_accessibility_config")]
    public class RdInputService : AccessibilityService
    {
        private const string Tag = "RdInput";
        private const int ActionSetText = 0x00200000;   // AccessibilityNodeInfo.ACTION_SET_TEXT
        private const string ArgSetText = "ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE";

        private static RdInputService _instance;
        public static RdInputService Instance { get { return _instance; } }
        public static bool IsReady { get { return _instance != null; } }

        private Handler _ui;
        private readonly object _gate = new object();

        /* ---------- 手势状态 ---------- */
        private Path _path;
        private GestureDescription.StrokeDescription _stroke;
        private long _strokeStartAt;
        private bool _leftDown;
        private bool _longPressFired;
        private bool _longPressPending;
        private int _x, _y;
        private int _downX, _downY;
        private long _longPressAt;
        private int _longPressDelay = 600;

        /* ---------- 滚轮队列（一次手势没结束不能派发下一个） ---------- */
        private readonly Queue<GestureDescription> _wheelQueue = new Queue<GestureDescription>();
        private bool _wheelBusy;

        /* ---------- 修饰键本地状态（查看端只发 VK 码，不给字符） ---------- */
        private bool _shift;
        private bool _caps;

        /* ===================== 生命周期 ===================== */

        public override void OnCreate()
        {
            base.OnCreate();
            _ui = new Handler(Looper.MainLooper);
            try { _longPressDelay = ViewConfiguration.LongPressTimeout + ViewConfiguration.TapTimeout; } catch { _longPressDelay = 600; }
            _instance = this;
            Log.Warn(Tag, "无障碍输入服务已连接");
        }

        public override void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            base.OnDestroy();
        }

        public override void OnInterrupt() { }

        public override void OnAccessibilityEvent(AccessibilityEvent e) { }

        /* ===================== 供被控服务调用 ===================== */

        public static bool Ensure()
        {
            if (_instance != null) return true;
            try
            {
                // 直接查系统设置里的"已启用无障碍服务"列表：本服务已勾选但实例尚未连上时也返回 true，
                // 免得界面反复催用户去设置里开。
                var cr = Application.Context.ContentResolver;
                var val = Android.Provider.Settings.Secure.GetString(cr,
                    Android.Provider.Settings.Secure.EnabledAccessibilityServices);
                return val != null && val.Contains("RdInputService");
            }
            catch { }
            return false;
        }

        /// <summary>打开系统的无障碍设置页（用户手动开启本服务）。</summary>
        public static void OpenSettings(Context ctx)
        {
            try
            {
                var i = new Intent(Android.Provider.Settings.ActionAccessibilitySettings);
                i.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(i);
            }
            catch (Exception ex) { Log.Warn(Tag, "打开无障碍设置失败：" + ex.Message); }
        }

        /* ===================== 鼠标 ===================== */

        /// <summary>查看端坐标是 0..65535 的归一化值，这里换成当前屏幕像素。</summary>
        private void ToScreenPixels(int nx, int ny, out int px, out int py)
        {
            int w = 1080, h = 1920;
            try
            {
                // 用 DisplayManager 取"整块屏幕"（含状态栏/导航栏）的像素：
                // 手势坐标是屏幕坐标系，用 Resources.DisplayMetrics 会漏掉导航栏，
                // 导致点屏幕最下方时坐标整体偏上、点不准。
                var dm = (Android.Hardware.Display.DisplayManager)GetSystemService(Context.DisplayService);
                var disps = dm != null ? dm.GetDisplays() : null;
                var disp = (disps != null && disps.Length > 0) ? disps[0] : null;
                if (disp != null)
                {
                    var m = new DisplayMetrics();
                    disp.GetRealMetrics(m);
                    if (m.WidthPixels > 0) w = m.WidthPixels;
                    if (m.HeightPixels > 0) h = m.HeightPixels;
                }
            }
            catch { }
            px = (int)Math.Round(Math.Max(0, Math.Min(65535, nx)) * (double)w / 65535.0);
            py = (int)Math.Round(Math.Max(0, Math.Min(65535, ny)) * (double)h / 65535.0);
            if (px < 0) px = 0;
            if (py < 0) py = 0;
            if (px > w - 1) px = w - 1;
            if (py > h - 1) py = h - 1;
        }

        public void OnMouse(int type, int nx, int ny, int button, int wheel)
        {
            int px, py;
            ToScreenPixels(nx, ny, out px, out py);
            switch (type)
            {
                case 0: Move(px, py); break;                       // 移动
                case 1: ButtonDown(px, py, button); break;          // 按下
                case 2: ButtonUp(px, py, button); break;            // 抬起
                case 3: Wheel(wheel); break;                        // 滚轮
                case 4: Move(px, py); break;                        // 相对移动：被控端统一当绝对处理
            }
        }

        public void Move(int px, int py)
        {
            lock (_gate)
            {
                _x = px; _y = py;
                if (!_leftDown) return;
                // 手指按下后移动超过阈值：不再等长按，直接把这段笔画续接出去
                if (_longPressPending && Math.Abs(px - _downX) + Math.Abs(py - _downY) > 8)
                {
                    _longPressPending = false;
                    _longPressFired = false;
                }
                if (!_longPressPending) ContinueGesture(px, py);
            }
        }

        public void ButtonDown(int px, int py, int button)
        {
            lock (_gate)
            {
                _x = px; _y = py;

                // 按键编码以 Windows 侧为准：1=左、2=右、3=中（见 Protocol.MouseButton）。
                // 早期这里按 0=左/1=右 解析，结果电脑上每一次左键都被当成右键长按，
                // 表现就是"点一下变成按住不放、想拖也拖不动"。
                if (button == Protocol.MouseButton.Right)
                {
                    // 右键 = 手机上的长按（上下文菜单）
                    LongPressAsync(px, py);
                    return;
                }
                // 0 是旧版安卓查看端的历史编码，按左键兼容处理，避免新旧端互掐
                bool isLeft = button == Protocol.MouseButton.Left || button == 0;
                if (!isLeft) return;
                if (_leftDown)
                {
                    // 上一次的"抬起"没送到（对端把鼠标拖出画面再松手、或网络丢了一帧），
                    // 状态就会卡在"一直按着"，下一次点击被当成拖拽。
                    // 这里自愈：收到新的按下就先把上一笔收尾，再开新的。
                    _leftDown = false;
                    _longPressPending = false;
                    EndGesture(_x, _y);
                }
                // 记一条"点击到达"，排障时一眼能看出是网络没送到、还是无障碍没接住
                Log.Warn(Tag, "点击 " + px + "," + py + " btn=" + button);
                _leftDown = true;
                _longPressFired = false;
                _longPressPending = true;
                _downX = px; _downY = py;
                StartPath(px, py);
                // 长按定时器：到点还没抬手就"续接"下去，系统据此判定长按
                Handler h = _ui;
                if (h != null)
                {
                    h.PostDelayed(delegate
                    {
                        lock (_gate)
                        {
                            if (!_leftDown || !_longPressPending) return;
                            _longPressPending = false;
                            _longPressFired = true;
                            ContinueGesture(px, py);
                        }
                    }, _longPressDelay);
                }
            }
        }

        public void ButtonUp(int px, int py, int button)
        {
            lock (_gate)
            {
                _x = px; _y = py;
                bool isLeft = button == Protocol.MouseButton.Left || button == 0;
                if (!isLeft || !_leftDown) return;
                _leftDown = false;
                _longPressPending = false;
                EndGesture(px, py);
            }
        }

        private void StartPath(int px, int py)
        {
            _path = new Path();
            _path.MoveTo(px, py);
            _stroke = null;
            _strokeStartAt = System.Environment.TickCount;
        }

        /// <summary>续接笔画：Android 8+ 用 continueStroke，7 及以下只能整段重发。</summary>
        private void ContinueGesture(int px, int py)
        {
            if (_path == null) StartPath(px, py);
            int dur = (int)Math.Max(1, System.Environment.TickCount - _strokeStartAt);
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    _path.LineTo(px, py);
                    GestureDescription.StrokeDescription next = (_stroke == null)
                        ? new GestureDescription.StrokeDescription(_path, 0, dur, true)
                        : _stroke.ContinueStroke(_path, 0, dur, true);
                    _stroke = next;
                    var b = new GestureDescription.Builder();
                    b.AddStroke(next);
                    DispatchGesture(b.Build(), null, null);
                    // 续接后必须重置路径：下一段要从当前点重新起笔
                    _path = new Path();
                    _path.MoveTo(px, py);
                    _strokeStartAt = System.Environment.TickCount;
                }
                else
                {
                    _path.LineTo(px, py);
                    var b = new GestureDescription.Builder();
                    b.AddStroke(new GestureDescription.StrokeDescription(_path, 0, dur));
                    DispatchGesture(b.Build(), null, null);
                    _path = new Path();
                    _path.MoveTo(px, py);
                }
            }
            catch (Exception ex) { Log.Warn(Tag, "续接手势失败：" + ex.Message); }
        }

        private void EndGesture(int px, int py)
        {
            if (_path == null) { StartPath(px, py); }
            // 纯轻点时整段手势只有按下到抬起这么长，若用户点得极快（十几毫秒），
            // 系统会认为"手势时间非法"直接丢弃——表现就是点了没反应。
            // 给一个 40ms 下限，既保证被识别成一次点击，又不会拖慢连点手感。
            int dur = (int)Math.Max(_stroke == null ? 40 : 1, System.Environment.TickCount - _strokeStartAt);
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O && _stroke != null)
                {
                    _path.LineTo(px, py);
                    var last = _stroke.ContinueStroke(_path, 0, dur, false);
                    var b = new GestureDescription.Builder();
                    b.AddStroke(last);
                    DispatchGesture(b.Build(), null, null);
                }
                else
                {
                    _path.LineTo(px, py);
                    var b = new GestureDescription.Builder();
                    b.AddStroke(new GestureDescription.StrokeDescription(_path, 0, dur));
                    DispatchGesture(b.Build(), null, null);
                }
            }
            catch (Exception ex) { Log.Warn(Tag, "结束手势失败：" + ex.Message); }
            finally { _path = null; _stroke = null; }
        }

        private void LongPressAsync(int px, int py)
        {
            try
            {
                var p = new Path();
                p.MoveTo(px, py);
                var b = new GestureDescription.Builder();
                b.AddStroke(new GestureDescription.StrokeDescription(p, 0, _longPressDelay));
                DispatchGesture(b.Build(), null, null);
            }
            catch (Exception ex) { Log.Warn(Tag, "长按失败：" + ex.Message); }
        }

        /// <summary>滚轮：手机没有滚轮事件，用竖直短划模拟一次滑动（rustdesk 同法）。</summary>
        private void Wheel(int delta)
        {
            if (delta == 0) return;
            int step = 120;
            int dir = delta > 0 ? 1 : -1;
            int x, y;
            lock (_gate) { x = _x; y = _y; }
            try
            {
                var p = new Path();
                p.MoveTo(x, y);
                p.LineTo(x, y + dir * step);
                var b = new GestureDescription.Builder();
                b.AddStroke(new GestureDescription.StrokeDescription(p, 0, 60));
                lock (_wheelQueue) _wheelQueue.Enqueue(b.Build());
                PumpWheel();
            }
            catch { }
        }

        private void PumpWheel()
        {
            GestureDescription next = null;
            lock (_wheelQueue)
            {
                if (_wheelBusy) return;
                if (_wheelQueue.Count == 0) return;
                _wheelBusy = true;
                next = _wheelQueue.Dequeue();
            }
            try { DispatchGesture(next, null, null); } catch { }
            Handler h = _ui;
            if (h != null) h.PostDelayed(delegate { lock (_wheelQueue) { _wheelBusy = false; } PumpWheel(); }, 80);
        }

        /* ===================== 键盘 ===================== */

        /// <summary>VK 码（Windows 虚拟键，desk 协议原样传来）→ 实际输入。</summary>
        public void OnKey(int vk, bool down)
        {
            // 修饰键先记账：查看端不字发字符，大小写只能靠自己还原
            if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) { if (down) _shift = true; else if (vk == 0x10) _shift = false; return; }
            if (vk == 0xA2 || vk == 0xA3) return;                       // Ctrl
            if (vk == 0x14) { if (down) _caps = !_caps; return; }        // CapsLock
            if (!down) return;

            switch (vk)
            {
                case 0x08: Backspace(); return;               // Backspace
                case 0x0D: CommonText("\n"); return;          // Enter
                case 0x1B: Global(GlobalBack); return; // Esc = 返回
                case 0x2E: Global(GlobalBack); return; // Delete：手机没有删除键，当返回更实用
                case 0x5B: Global(GlobalHome); return; // Win = 主页
                case 0xAF: Volume(true); return;
                case 0xAE: Volume(false); return;
            }

            char? ch = VkToChar(vk);
            if (ch.HasValue) { CommonText(ch.Value.ToString()); }
        }

        private static char? VkToChar(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return (char)vk;                  // A-Z（大小写在 CommonText 里处理）
            if (vk >= 0x30 && vk <= 0x39) return (char)vk;                  // 0-9
            switch (vk)
            {
                case 0x20: return ' ';
                case 0xBA: return ';';
                case 0xBB: return '=';
                case 0xBC: return ',';
                case 0xBD: return '-';
                case 0xBE: return '.';
                case 0xBF: return '/';
                case 0xC0: return '`';
                case 0xDB: return '[';
                case 0xDC: return '\\';
                case 0xDD: return ']';
                case 0xDE: return '\'';
                case 0x6A: return '*';   // 小键盘 *
                case 0x6B: return '+';
                case 0x6D: return '-';
                case 0x6E: return '.';
                case 0x6F: return '/';
            }
            if (vk >= 0x60 && vk <= 0x69) return (char)('0' + (vk - 0x60)); // 小键盘数字
            return null;
        }

        private static readonly Dictionary<char, char> ShiftMap = new Dictionary<char, char>
        {
            { '1','!' }, { '2','@' }, { '3','#' }, { '4','$' }, { '5','%' },
            { '6','^' }, { '7','&' }, { '8','*' }, { '9','(' }, { '0',')' },
            { '-','_' }, { '=','+' }, { ';',':' }, { '\'','"' }, { ',','<' },
            { '.','>' }, { '/','?' }, { '\\','|' }, { '[','{' }, { ']','}' }, { '`','~' }
        };

        /// <summary>按 VK 打一个普通字符（考虑 Shift/CapsLock）。</summary>
        private void CommonText(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            char c = s[0];
            bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            if (letter)
            {
                bool upper = _shift ^ _caps;
                c = upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
            }
            else if (_shift && ShiftMap.ContainsKey(c))
            {
                c = ShiftMap[c];
            }
            InsertText(c.ToString());
        }

        /* ---------- 文本编辑（不依赖 Android 13 的 InputMethod） ---------- */

        private AccessibilityNodeInfo FindEditable()
        {
            try
            {
                var root = GetRootInActiveWindow(0);
                if (root == null) return null;
                // 先找"正在聚焦"的可编辑框，再退一步找任意可编辑框
                var hit = FindEditableIn(root, 0, true);
                if (hit == null) hit = FindEditableIn(root, 0, false);
                return hit;
            }
            catch { }
            return null;
        }

        private AccessibilityNodeInfo FindEditableIn(AccessibilityNodeInfo node, int depth, bool needFocused)
        {
            if (node == null || depth > 14) return null;
            try
            {
                if (node.Editable && (!needFocused || node.Focused)) return node;
                int n = node.ChildCount;
                for (int i = 0; i < n; i++)
                {
                    var child = node.GetChild(i);
                    if (child == null) continue;
                    var r = FindEditableIn(child, depth + 1, needFocused);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>在光标处插入文字；没有输入框就什么都不做（不弹 toast，避免打扰）。</summary>
        public void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var node = FindEditable();
            if (node == null) return;
            try
            {
                string old = node.Text ?? "";
                int s = node.TextSelectionStart;
                int e = node.TextSelectionEnd;
                if (s < 0 || s > old.Length) s = old.Length;
                if (e < s) e = s;
                if (e > old.Length) e = old.Length;
                string merged = old.Substring(0, s) + text + old.Substring(e);
                int caret = s + text.Length;
                SetNodeText(node, merged, caret);
            }
            catch (Exception ex) { Log.Warn(Tag, "插入文字失败：" + ex.Message); }
        }

        private void SetNodeText(AccessibilityNodeInfo node, string text, int caret)
        {
            try
            {
                var args = new Bundle();
                args.PutCharSequence(ArgSetText, new Java.Lang.String(text));
                node.PerformAction((Android.Views.Accessibility.Action)ActionSetText, args);
                // 光标位置由系统按 setText 后的默认行为放在末尾；需要精确落点时再补一次选择
                if (caret >= 0 && caret != text.Length)
                {
                    var sel = new Bundle();
                    sel.PutInt("ACTION_ARGUMENT_SELECTION_START_INT", caret);
                    sel.PutInt("ACTION_ARGUMENT_SELECTION_END_INT", caret);
                    node.PerformAction((Android.Views.Accessibility.Action)0x00020000, sel);
                }
            }
            catch { }
        }

        private void Backspace()
        {
            var node = FindEditable();
            if (node == null) { Global(GlobalBack); return; }
            try
            {
                string old = node.Text ?? "";
                int s = node.TextSelectionStart;
                int e = node.TextSelectionEnd;
                if (s < 0 || s > old.Length) s = old.Length;
                if (e < 0 || e > old.Length) e = old.Length;
                if (e > s)
                {
                    SetNodeText(node, old.Remove(s, e - s), s);
                }
                else if (s > 0)
                {
                    SetNodeText(node, old.Remove(s - 1, 1), s - 1);
                }
            }
            catch (Exception ex) { Log.Warn(Tag, "退格失败：" + ex.Message); }
        }

        /// <summary>查看端"输入文字"整段下发（支持中文/大写/符号，绕开 VK 码没有字符的问题）。</summary>
        public void CommitText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text == "\b") { Backspace(); return; }
            if (text == "\n") { InsertText("\n"); return; }
            InsertText(text);
        }

        /* ===================== 全局动作 / 音量 ===================== */

        // 系统全局动作的整数取值（AccessibilityService 的 GLOBAL_ACTION_* 常量）。
        // 这里直接用 int 而不依赖枚举绑定：不同 Xamarin 版本对这几个常量的绑定名不一致，
        // 用整数反而绝对稳。
        public const int GlobalBack = 1;
        public const int GlobalHome = 2;
        public const int GlobalRecents = 3;
        public const int GlobalNotifications = 4;
        public const int GlobalQuickSettings = 5;
        public const int GlobalPowerDialog = 6;
        public const int GlobalLockScreen = 8;

        public void Global(int action)
        {
            // 绑定里 PerformGlobalAction 收的是 GlobalAction 枚举，枚举值与系统的
            // GLOBAL_ACTION_* 整数一致（Back=1、Home=2、Recents=3…），直接转换即可
            try { PerformGlobalAction((GlobalAction)action); }
            catch (Exception ex) { Log.Warn(Tag, "全局动作失败：" + ex.Message); }
        }

        public void Volume(bool up)
        {
            try
            {
                var am = (AudioManager)GetSystemService(AudioService);
                if (am == null) return;
                am.AdjustStreamVolume(Stream.Music, up ? Adjust.Raise : Adjust.Lower, VolumeNotificationFlags.ShowUi);
            }
            catch { }
        }

        public void PowerDialog()
        {
            Global(GlobalPowerDialog);
        }
    }
}

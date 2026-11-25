using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace AhoChatA
{
    /// <summary>
    /// 系统分享统一分发页（iOS 风格）。
    /// 其他 App 通过「分享」把文本/文件发到 AhoChatA 时，先进这里：
    ///   · 文本（含 http/https）→ 选择「浏览器打开/下载/发到聊天频道」或普通文本发送
    ///   · 文件 → 按类型（文档/apk/视频/图片/音乐）选择「打开方式」或「发到聊天频道」
    /// 所有配色取自 IosStyle.Colors（跟随 ThemeManager 日夜切换）。
    /// </summary>
    [Activity(Label = "@string/act_share", Exported = true,
        Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation
            | Android.Content.PM.ConfigChanges.ScreenSize)]
    [IntentFilter(new[] { Intent.ActionSend },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeTypes = new[] { "text/plain" })]
    [IntentFilter(new[] { Intent.ActionSend },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeTypes = new[] { "*/*" })]
    [IntentFilter(new[] { Intent.ActionSendMultiple },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeTypes = new[] { "*/*" })]
    public class ShareRouterActivity : BaseActivity
    {
        private enum Kind { Text, Url, Doc, Apk, Video, Image, Audio, Other }
        private enum Act { Browser, Download, SendChat, Copy, OpenReader, Install, Play, Preview, ShareOut }

        private sealed class Option
        {
            public Act Action;
            public string Label;
            public string Detail;
        }

        private IosStyle.Palette _p;
        private LinearLayout _root;
        private string _text;
        private readonly List<Android.Net.Uri> _uris = new List<Android.Net.Uri>();
        private readonly Dictionary<Android.Net.Uri, string> _names = new Dictionary<Android.Net.Uri, string>();
        private Kind _kind;
        private string _displayName;
        private long _size;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            ThemeManager.Apply(this);
            _p = IosStyle.Colors(this);
            ParseIntent(Intent);
            BuildUi();
            IosStyle.ApplySystemBars(this);
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (_p == null) return;
            var now = IosStyle.Colors(this);
            if (now.Night != _p.Night) { Recreate(); return; }
            IosStyle.ApplySystemBars(this);
        }

        private void ParseIntent(Intent intent)
        {
            if (intent == null) return;
            try
            {
                if (intent.Action == Intent.ActionSend || intent.Action == Intent.ActionSendMultiple)
                {
                    _text = intent.GetStringExtra(Intent.ExtraText) ?? intent.GetStringExtra(Intent.ExtraHtmlText);
                    if (intent.ClipData != null)
                    {
                        for (int i = 0; i < intent.ClipData.ItemCount; i++)
                        {
                            var u = intent.ClipData.GetItemAt(i)?.Uri;
                            if (u != null) { _uris.Add(u); _names[u] = QueryDisplayName(u, intent.Type); }
                        }
                    }
                    var one = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
                    if (one != null && !_uris.Contains(one)) { _uris.Add(one); _names[one] = QueryDisplayName(one, intent.Type); }
                }
                if (_uris.Count == 0 && intent.Data != null)
                {
                    _uris.Add(intent.Data);
                    _names[intent.Data] = QueryDisplayName(intent.Data, intent.Type);
                }
            }
            catch (Exception ex) { Android.Util.Log.Warn("ShareRouter", "解析 Intent 失败: " + ex.Message); }
        }

        private string QueryDisplayName(Android.Net.Uri uri, string mime)
        {
            string name = null;
            try
            {
                var cursor = ContentResolver.Query(uri, new[] { OpenableColumns.DisplayName, OpenableColumns.Size }, null, null, null);
                if (cursor != null)
                {
                    using (cursor)
                    {
                        if (cursor.MoveToFirst())
                        {
                            int ni = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                            int si = cursor.GetColumnIndex(OpenableColumns.Size);
                            if (ni >= 0) name = cursor.GetString(ni);
                            if (si >= 0) long.TryParse(cursor.GetString(si), out _size);
                        }
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(name)) name = uri.LastPathSegment;
            if (string.IsNullOrEmpty(name)) name = L10n.T("文件");
            return name;
        }

        private Kind DetectKind()
        {
            if (_uris.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(_text)) return Kind.Other;
                string t = _text.Trim();
                return (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? Kind.Url : Kind.Text;
            }
            _displayName = _names.Values.FirstOrDefault() ?? L10n.T("文件");
            string ext = System.IO.Path.GetExtension(_displayName).TrimStart('.').ToLowerInvariant();
            if (OfficeViewerActivity.SupportedExt.Contains(ext)) return Kind.Doc;
            if (ext == "apk" || ext == "apk1") return Kind.Apk;
            if (new[] { "mp4", "mkv", "mov", "webm", "flv", "avi", "m4v", "3gp" }.Contains(ext)) return Kind.Video;
            if (new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic" }.Contains(ext)) return Kind.Image;
            if (new[] { "mp3", "m4a", "aac", "flac", "wav", "ogg", "opus" }.Contains(ext)) return Kind.Audio;
            // 没有扩展名时按 MIME 猜
            string mime = (Intent?.Type ?? "").ToLowerInvariant();
            if (mime.StartsWith("video/")) return Kind.Video;
            if (mime.StartsWith("image/")) return Kind.Image;
            if (mime.StartsWith("audio/")) return Kind.Audio;
            if (mime.Contains("package-archive") || mime.Contains("apk")) return Kind.Apk;
            return Kind.Other;
        }

        private List<Option> BuildOptions()
        {
            var list = new List<Option>();
            switch (_kind)
            {
                case Kind.Url:
                    list.Add(new Option { Action = Act.Browser, Label = L10n.T("在浏览器打开"), Detail = L10n.T("用内置浏览器访问该链接") });
                    list.Add(new Option { Action = Act.Download, Label = L10n.T("下载内容"), Detail = L10n.T("加入内置下载器") });
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("发送到聊天频道"), Detail = L10n.T("选择会话后发送链接") });
                    break;
                case Kind.Text:
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("发送到聊天频道"), Detail = L10n.T("选择会话后发送文本") });
                    list.Add(new Option { Action = Act.Copy, Label = L10n.T("复制到剪贴板"), Detail = "" });
                    list.Add(new Option { Action = Act.ShareOut, Label = L10n.T("分享到其他程序"), Detail = L10n.T("使用系统分享窗口") });
                    break;
                case Kind.Doc:
                    list.Add(new Option { Action = Act.OpenReader, Label = L10n.T("作为阅读材料打开"), Detail = L10n.T("在阅读器中查看文档") });
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("作为文件发送到聊天频道"), Detail = L10n.T("选择会话后发送文件") });
                    break;
                case Kind.Apk:
                    list.Add(new Option { Action = Act.Install, Label = L10n.T("作为安装包安装"), Detail = L10n.T("调用系统安装器") });
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("作为文件分享到聊天频道"), Detail = L10n.T("选择会话后发送安装包") });
                    break;
                case Kind.Video:
                    list.Add(new Option { Action = Act.Play, Label = L10n.T("播放视频"), Detail = L10n.T("在视频播放器中打开") });
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("发送到聊天频道"), Detail = L10n.T("选择会话后发送视频") });
                    break;
                case Kind.Image:
                    list.Add(new Option { Action = Act.Preview, Label = L10n.T("打开预览"), Detail = L10n.T("在图片查看器中打开") });
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("发送到聊天频道"), Detail = L10n.T("选择会话后发送图片") });
                    break;
                case Kind.Audio:
                    list.Add(new Option { Action = Act.Play, Label = L10n.T("播放音乐"), Detail = L10n.T("在音乐播放器中打开") });
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("发送到聊天频道"), Detail = L10n.T("选择会话后发送音乐") });
                    break;
                default:
                    list.Add(new Option { Action = Act.SendChat, Label = L10n.T("发送到聊天频道"), Detail = L10n.T("选择会话后发送文件") });
                    list.Add(new Option { Action = Act.ShareOut, Label = L10n.T("分享到其他程序"), Detail = L10n.T("使用系统分享窗口") });
                    break;
            }
            return list;
        }

        private void BuildUi()
        {
            _kind = DetectKind();
            _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _root.SetBackgroundColor(new Color(_p.Background));
            var scroll = new ScrollView(this);
            scroll.SetBackgroundColor(new Color(_p.Background));
            scroll.AddView(_root, new ViewGroup.LayoutParams(-1, -2));
            SetContentView(scroll);

            // 标题栏
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.SetBackgroundColor(new Color(_p.Surface));
            bar.SetPadding(IosStyle.Dp(this, 16), IosStyle.Dp(this, 12), IosStyle.Dp(this, 16), IosStyle.Dp(this, 12));
            bar.LayoutParameters = new LinearLayout.LayoutParams(-1, IosStyle.Dp(this, 56));
            var title = new TextView(this) { Text = L10n.T("分享到 AhoChat"), TextSize = IosStyle.HeadingSize };
            title.SetTextColor(new Color(_p.Text));
            title.SetTypeface(null, TypefaceStyle.Bold);
            title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            bar.AddView(title);
            var close = new TextView(this) { Text = L10n.T("取消"), TextSize = IosStyle.BodySize, Gravity = GravityFlags.Center };
            close.SetTextColor(new Color(_p.Accent));
            close.SetPadding(IosStyle.Dp(this, 10), IosStyle.Dp(this, 6), IosStyle.Dp(this, 4), IosStyle.Dp(this, 6));
            close.Click += (s, e) => Finish();
            bar.AddView(close);
            _root.AddView(bar);

            // 内容预览卡片
            var preview = new LinearLayout(this) { Orientation = Orientation.Vertical };
            preview.Background = IosStyle.GroupCardBackground(this, _p);
            var plp = new LinearLayout.LayoutParams(-1, -2);
            plp.SetMargins(IosStyle.Dp(this, 16), IosStyle.Dp(this, 16), IosStyle.Dp(this, 16), IosStyle.Dp(this, 8));
            _root.AddView(preview, plp);

            string kindName = _kind == Kind.Text ? "文本" : _kind == Kind.Url ? "链接" :
                _kind == Kind.Doc ? "文档" : _kind == Kind.Apk ? "安装包" :
                _kind == Kind.Video ? "视频" : _kind == Kind.Image ? "图片" :
                _kind == Kind.Audio ? "音乐" : "文件";
            preview.AddView(IosStyle.SectionHeader(this, L10n.T("分享内容 · ") + kindName, _p));
            string previewText = _uris.Count > 0
                ? _displayName + (_size > 0 ? "\n" + FormatSize(_size) : "")
                : (_text?.Length > 200 ? _text.Substring(0, 200) + "…" : _text);
            var content = IosStyle.BodyText(this, string.IsNullOrWhiteSpace(previewText) ? L10n.T("（空内容）") : previewText, _p, IosStyle.BodySize);
            content.SetPadding(IosStyle.Dp(this, 16), IosStyle.Dp(this, 4), IosStyle.Dp(this, 16), IosStyle.Dp(this, 14));
            preview.AddView(content);

            // 操作分组卡片
            var actions = new LinearLayout(this) { Orientation = Orientation.Vertical };
            actions.Background = IosStyle.GroupCardBackground(this, _p);
            var alp = new LinearLayout.LayoutParams(-1, -2);
            alp.SetMargins(IosStyle.Dp(this, 16), IosStyle.Dp(this, 8), IosStyle.Dp(this, 16), IosStyle.Dp(this, 24));
            _root.AddView(actions, alp);

            var options = BuildOptions();
            for (int i = 0; i < options.Count; i++)
            {
                if (i > 0) actions.AddView(IosStyle.Divider(this, _p));
                var opt = options[i];
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetMinimumHeight(IosStyle.Dp(this, IosStyle.RowMinHeight));
                row.SetPadding(IosStyle.Dp(this, 16), IosStyle.Dp(this, 10), IosStyle.Dp(this, 16), IosStyle.Dp(this, 10));
                row.Click += (s, e) => Execute(opt.Action);

                var label = new TextView(this) { Text = opt.Label, TextSize = IosStyle.TitleSize };
                label.SetTextColor(new Color(_p.Text));
                label.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                row.AddView(label);
                if (!string.IsNullOrEmpty(opt.Detail))
                {
                    var detail = new TextView(this) { Text = opt.Detail, TextSize = IosStyle.CaptionSize, Gravity = GravityFlags.End };
                    detail.SetTextColor(new Color(_p.Secondary));
                    detail.SetMaxWidth(IosStyle.Dp(this, 180));
                    row.AddView(detail);
                }
                var chevron = new TextView(this) { Text = "›", TextSize = 24, Gravity = GravityFlags.Center };
                chevron.SetTextColor(new Color(_p.Secondary));
                chevron.SetPadding(IosStyle.Dp(this, 8), 0, 0, 0);
                row.AddView(chevron);
                actions.AddView(row);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }

        private void Execute(Act action)
        {
            switch (action)
            {
                case Act.Browser:
                    var i = new Intent(this, typeof(BrowserActivity));
                    i.PutExtra(BrowserActivity.ExtraUrl, _text?.Trim());
                    i.AddFlags(ActivityFlags.NewTask);
                    StartActivity(i); Finish(); break;
                case Act.Download:
                    var d = new Intent(this, typeof(DownloaderEntryActivity));
                    d.SetAction(Intent.ActionView);
                    d.SetData(Android.Net.Uri.Parse(_text.Trim()));
                    d.PutExtra(Intent.ExtraText, _text.Trim());
                    d.AddFlags(ActivityFlags.NewTask);
                    StartActivity(d); Finish(); break;
                case Act.SendChat:
                    ShowConvPicker(); break;
                case Act.Copy:
                    var cm = (ClipboardManager)GetSystemService(ClipboardService);
                    cm.PrimaryClip = ClipData.NewPlainText("share", _text);
                    L10n.Toast(this, L10n.T("已复制"), ToastLength.Short).Show(); Finish(); break;
                case Act.OpenReader:
                case Act.Install:
                case Act.Play:
                case Act.Preview:
                    ForwardToOpen(action); break;
                case Act.ShareOut:
                    ShareOut(); break;
            }
        }

        /// <summary>把原始 content:// 直接转给对应打开 Activity（保留 URI 权限）。</summary>
        private void ForwardToOpen(Act action)
        {
            try
            {
                var uri = _uris.FirstOrDefault();
                if (uri == null) { L10n.Toast(this, L10n.T("没有文件"), ToastLength.Short).Show(); return; }
                Intent target;
                switch (action)
                {
                    case Act.Install:
                        target = new Intent(this, typeof(ApkInstallActivity));
                        break;
                    case Act.Play:
                        target = _kind == Kind.Audio
                            ? new Intent(this, typeof(MusicActivity))
                            : new Intent(this, typeof(VideoActivity));
                        break;
                    case Act.Preview:
                        target = new Intent(this, typeof(PhotoActivity));
                        break;
                    default:
                        target = new Intent(this, typeof(OfficeViewerActivity));
                        break;
                }
                target.SetAction(Intent.ActionView);
                target.SetDataAndType(uri, Intent?.Type ?? "*/*");
                target.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
                StartActivity(target);
                Finish();
            }
            catch (Exception ex) { L10n.Toast(this, L10n.T("打开失败: ") + ex.Message, ToastLength.Long).Show(); }
        }

        /// <summary>选择会话（iOS 风格分组列表）。</summary>
        private void ShowConvPicker()
        {
            var convs = new List<Conversation>();
            try { convs = CoreHost.Bridge?.GetConversations() ?? new List<Conversation>(); } catch { }
            if (convs.Count == 0) { L10n.Toast(this, L10n.T("暂无会话"), ToastLength.Short).Show(); return; }

            var items = convs.Select(c => c.Title ?? c.Id).ToArray();
            var dialog = L10n.Dialog(this)
                .SetTitle(L10n.T("发送到聊天频道"))
                .SetItems(items, async (s, e) =>
                {
                    if (e.Which < 0 || e.Which >= convs.Count) return;
                    var conv = convs[e.Which].Id;
                    await SendToConversation(conv);
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Create();
            IosStyle.ShowCardDialog(dialog, this, 0.94f, 0);
        }

        private async System.Threading.Tasks.Task SendToConversation(string conv)
        {
            try
            {
                if (_uris.Count > 0)
                {
                    foreach (var uri in _uris)
                    {
                        string name = _names.TryGetValue(uri, out var n) ? n : (uri.LastPathSegment ?? L10n.T("文件"));
                        byte[] bytes;
                        using (var stream = ContentResolver.OpenInputStream(uri))
                        using (var ms = new MemoryStream())
                        {
                            if (stream == null) continue;
                            await stream.CopyToAsync(ms);
                            bytes = ms.ToArray();
                        }
                        CoreHost.Bridge?.SendFileBytes(conv, name, bytes);
                    }
                    L10n.Toast(this, L10n.T("已发送 ") + _uris.Count + L10n.T(" 个文件"), ToastLength.Short).Show();
                }
                else if (!string.IsNullOrWhiteSpace(_text))
                {
                    string kind = _kind == Kind.Url ? "url" : "text";
                    CoreHost.Bridge?.SendInConv(conv, _text.Trim(), kind, _kind == Kind.Url ? _text.Trim() : null);
                    L10n.Toast(this, L10n.T("已发送"), ToastLength.Short).Show();
                }
                Finish();
            }
            catch (Exception ex) { L10n.Toast(this, L10n.T("发送失败: ") + ex.Message, ToastLength.Long).Show(); }
        }

        /// <summary>系统分享窗口（把收到的内容再分享出去）。</summary>
        private void ShareOut()
        {
            try
            {
                var send = new Intent(Intent.ActionSend);
                if (_uris.Count > 0)
                {
                    var uri = _uris.First();
                    send.SetType(Intent?.Type ?? "*/*");
                    send.PutExtra(Intent.ExtraStream, uri);
                    send.AddFlags(ActivityFlags.GrantReadUriPermission);
                }
                else
                {
                    send.SetType("text/plain");
                    send.PutExtra(Intent.ExtraText, _text);
                }
                StartActivity(Intent.CreateChooser(send, L10n.T("分享到")));
                Finish();
            }
            catch (Exception ex) { L10n.Toast(this, L10n.T("分享失败: ") + ex.Message, ToastLength.Long).Show(); }
        }
    }
}

// ============================================================
// 影音模块 · 原生播放器 Activity（C# / Xamarin.Android）
//
// 入口：ChatActivity「服务」页 → 媒体 → 影音（与「阅读」平级）。
// 曲库页：分区导航（音乐 / 视频 / 歌单 / 喜欢）、搜索、检索目录配置、迷你播放条。
//        视频为 Jellyfin 风格海报卡片网格，音乐/喜欢为列表 + 竖向三点菜单。
// 歌单页：我喜欢的音乐 + 自建歌单（创建/重命名/删除/加歌/移歌）、系统分享。
// 播放页：竖屏 = 上方正方形封面 + 下方歌词；横屏 = 左封面、右滚动歌词（参考 Salt Player）。
// 视频播放：VideoActivity（参考 jellyfin-android 播放器控制层与手势）。
// 封面：内嵌图 → 同名图片 → 目录 cover/folder 图 → 音乐符号占位图。
// 歌词：同名 .lrc → 内嵌（ID3 USLT / FLAC VORBIS / M4A ©lyr）；无歌词显示提示。
// 后台播放与通知栏/锁屏控制见 MusicService。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AhoChatA.Music;
using AhoChatA.Vod;
using IoPath = System.IO.Path;

namespace AhoChatA
{
    [Activity(Label = "@string/act_music", Exported = false,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize,
              Theme = "@style/AppTheme.NoActionBar")]
    public partial class MusicActivity : BaseActivity
    {
        /// <summary>收到媒体自动播放时，由 MediaAutoOpen 传入的单文件直投路径。</summary>
        public const string ExtraPath = "music_path";

        /// <summary>外部入口（阅读页迷你播放条）指定落地界面。</summary>
        public const string ExtraShow = "music_show";
        /// <summary>曲库页：分区导航 + 搜索（本机 / 在线音乐）。</summary>
        public const string EntryLibrary = "library";
        /// <summary>播放详情页：大封面 + 滚动歌词。</summary>
        public const string EntryPlayer = "player";

        /// <summary>从阅读页跳入：返回键一键回到阅读界面继续阅读，而不是层层退回。</summary>
        private bool _fromReader;

        /* ---------------- iOS 风格配色（systemBlue / groupedBackground / label） ---------------- */
        private const int Accent = unchecked((int)0xFF007AFF);      // iOS systemBlue
        private const int TextMain = unchecked((int)0xFF1C1C1E);    // iOS label
        private const int TextSub = unchecked((int)0xFF8E8E93);     // iOS secondaryLabel
        private const int PageBg = unchecked((int)0xFFF2F2F7);      // iOS groupedBackground
        private const int CardBg = unchecked((int)0xFFFFFFFF);
        private const int White = unchecked((int)0xFFFFFFFF);
        private const int ChipBg = unchecked((int)0xFFF2F2F7);      // iOS tertiarySystemFill
        private const int ChipText = unchecked((int)0xFF007AFF);
        private const int Line = unchecked((int)0xFFC6C6C8);        // iOS separator
        private const int IosFill = unchecked((int)0xFFE9E9ED);     // iOS searchBar 底色
        private const int IosRed = unchecked((int)0xFFFF3B30);
        private const int IosGreen = unchecked((int)0xFF34C759);

        private LinearLayout _libPage;
        private LinearLayout _playPage;
        private LinearLayout _plPage, _plBox;
        private bool _inPlaylists;
        private TextView _subText;
        private ListView _list;
        private SongAdapter _adapter;
        private AlphabetIndexBar _musicIndex;   // 右侧竖向 A-Z 首字母索引（音乐列表）
        private LinearLayout _mini;
        private View _miniDivider;
        private ImageView _miniCover;
        private TextView _miniTitle, _miniArtist;
        private IconView _miniToggle, _miniNext;
        private TextView _miniMode;
        private TextView _miniLyric;

        private bool _showVideos;
        private bool _scanning;

        /* 影音分区（参考 Jellyfin 媒体库左侧导航）：音乐 / 视频 / 图片 / 歌单 / 喜欢 */
        private const int SecAudio = 0, SecVideo = 1, SecPhoto = 2, SecPlaylist = 3, SecFav = 4;
        private int _section;
        private string _query = "";
        private EditText _search;
        private LinearLayout _navRow, _navSeg;
        private LinearLayout _entryBack;   // 从阅读页跳进来时顶部显示的「返回阅读」
        private LinearLayout _musicSubBar;
        private TextView _subSongs, _subPlaylists, _subFavorites;

        /* 在线音乐聚合搜索（仅在「音乐」分区有效） */
        private List<Song> _displayList = new List<Song>();
        private List<Song> _onlineSongs = new List<Song>();
        private int _onlineToken;
        private bool _onlineSearching;
        private readonly Song _onlineDivider = new Song
        {
            IsDivider = true,
            Title = L10n.T("在线音乐 · 点击即播（无需本机文件）"),
        };
        private static readonly Dictionary<string, Bitmap> _onlineCoverCache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TextView> _navBtns = new List<TextView>();
        private GridView _grid;
        private VideoGridAdapter _gridAdapter;

        /* 图片分区：0 按文件夹 / 1 全部图片；默认展示全部图片 */
        private const int PhotoByFolder = 0, PhotoAll = 1;
        private int _photoMode = PhotoAll;
        private string _photoFolder;
        private LinearLayout _photoBar;
        private TextView _photoCrumb, _photoModeFolder, _photoModeAll;
        private GridView _photoGrid;
        private PhotoGridAdapter _photoAdapter;

        /* 播放页控件（旋转后重建） */
        private LinearLayout _playerTop, _playerBottom;
        private ImageView _cover;
        private TextView _pTitle, _pArtist, _tCur, _tTotal, _speedBtn;
        private SeekBar _seek;
        private IconView _btnPlay;
        private LinearLayout _btnPrev, _btnNext;
        private TextView _btnRepeat, _btnShuffle, _btnQueue;
        private IconView _btnFav;
        private LinearLayout _queuePanel;
        private ListView _queueList;
        private QueueAdapter _queueAdapter;
        private TextView _queueTitle;
        private readonly List<TextView> _modeChips = new List<TextView>();
        private IconView _btnModeIcon, _btnQueueIcon;
        private ScrollView _lyricScroll;
        private LinearLayout _lyricPanel;
        private View _lyricEmpty;
        private readonly List<TextView> _lyricViews = new List<TextView>();
        private Lyrics _lyrics;
        private int _lyricIdx = -2;
        private int _miniLyricIdx = -2;
        private TextView _lyricEmptyTitle;
        private static readonly HashSet<string> _lyricFetching = new HashSet<string>();

        private bool _seeking;
        private static Bitmap _placeholderBig, _placeholderSmall;
        // 歌曲封面缓存：列表行与歌单卡片共用，避免滑动 / 刷新反复解码
        private static readonly Dictionary<string, Bitmap> _coverCache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            MusicLibrary.Init(this);
            PlaylistStore.Init();
            try { OnlineMusic.Init(); } catch { }

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            root.SetBackgroundColor(new Color(PageBg));

            _libPage = BuildLibraryPage();
            _plPage = BuildPlaylistPage();
            _plPage.Visibility = ViewStates.Gone;
            _playPage = BuildPlayerPage();
            root.AddView(_libPage);
            root.AddView(_plPage);
            root.AddView(_playPage);
            _playPage.Visibility = ViewStates.Gone;

            SetContentView(root);
            ReloadList();
            RefreshPlaylists();
            SyncMini();

            // 媒体自动播放：收到音乐/视频文件后由 MediaAutoOpen 直投单文件，打开即播放
            try
            {
                var auto = Intent?.GetStringExtra(ExtraPath);
                if (!string.IsNullOrEmpty(auto) && System.IO.File.Exists(auto))
                {
                    var song = new Song { Path = auto, Title = System.IO.Path.GetFileName(auto) };
                    MusicPlayer.PlayAll(new[] { song }, 0);
                    ShowPlayer(true);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Music", "自动播放失败: " + ex.Message);
            }

            // 阅读页迷你条跳进来：定位到曲库页或播放详情页
            ApplyEntry(Intent);
        }

        /// <summary>
        /// 外部入口：阅读页点封面/歌名 → 曲库页（顶部带「搜索在线音乐」）；
        /// 点歌词 → 播放详情页。落地后返回键一键回阅读界面。
        /// </summary>
        private void ApplyEntry(Intent intent)
        {
            try
            {
                var v = intent?.GetStringExtra(ExtraShow);
                if (string.IsNullOrEmpty(v)) return;
                _fromReader = true;
                if (_entryBack != null) _entryBack.Visibility = ViewStates.Visible;

                if (string.Equals(v, EntryPlayer, StringComparison.OrdinalIgnoreCase) && MusicPlayer.Current != null)
                {
                    _inPlaylists = false;
                    ShowPlayer(true);
                    return;
                }

                // 曲库页：收起播放页与歌单页，定位到「音乐」分区
                ShowPlayer(false);
                ShowPlaylists(false);
                _inPlaylists = false;
                SetSection(SecAudio);
                ReloadList();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("Music", "外部入口处理失败: " + ex.Message);
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            MusicPlayer.StateChanged += OnStateChanged;
            MusicPlayer.SongChanged += OnSongChanged;
            MusicPlayer.PositionTick += OnTick;
            MusicPlayer.QueueChanged += OnQueueChanged;
            OnStateChanged();
            OnSongChanged();
            OnQueueChanged();
            EnsureStorageThenScan(false);
            ReloadList();
        }

        protected override void OnPause()
        {
            MusicPlayer.StateChanged -= OnStateChanged;
            MusicPlayer.SongChanged -= OnSongChanged;
            MusicPlayer.PositionTick -= OnTick;
            MusicPlayer.QueueChanged -= OnQueueChanged;
            base.OnPause();
        }

        // 已在前台（SingleTop 复用）时收到音乐直投：复用 OnCreate 的自动播放逻辑
        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            try
            {
                var auto = intent?.GetStringExtra(ExtraPath);
                if (!string.IsNullOrEmpty(auto) && System.IO.File.Exists(auto))
                {
                    var song = new Song { Path = auto, Title = System.IO.Path.GetFileName(auto) };
                    MusicPlayer.PlayAll(new[] { song }, 0);
                    ShowPlayer(true);
                }
            }
            catch (Exception ex) { Android.Util.Log.Warn("Music", "OnNewIntent 音乐直投失败: " + ex.Message); }
            ApplyEntry(intent);
        }

        /* ============================================================
         * 曲库页
         * ============================================================ */

        private LinearLayout BuildLibraryPage()
        {
            // 必须撑满父容器，否则列表的 weight 失效、迷你播放条会紧跟列表而不是贴底
            var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
            page.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

            // 从阅读页跳进来时，顶部给一个明确的「返回阅读」入口（否则只能靠系统返回键）
            _entryBack = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _entryBack.SetGravity(GravityFlags.CenterVertical);
            _entryBack.SetPadding(Dp(2), Dp(4), Dp(12), 0);
            _entryBack.Visibility = ViewStates.Gone;
            _entryBack.AddView(IconBtn(IconKind.Back, () => Finish()));
            var entryTip = new TextView(this) { Text = L10n.T("返回阅读"), TextSize = 12.5f };
            entryTip.SetTextColor(new Color(TextSub));
            entryTip.Gravity = GravityFlags.CenterVertical;
            entryTip.Click += (s, e) => Finish();
            _entryBack.AddView(entryTip);
            page.AddView(_entryBack);

            // 分区导航（iOS 分段控件 segmented control 风格）：音乐 / 视频 / 图片 / 设置
            // 原「影音」大标题栏已移除；歌单/喜欢并入「音乐」栏目；目录/刷新/网络收进「设置」浮窗。
            _navSeg = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _navSeg.SetPadding(Dp(3), Dp(2), Dp(3), Dp(2));
            _navSeg.Background = RoundBg(unchecked((int)0xFFE3E3E8), 10);

            _navRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _navRow.SetPadding(Dp(12), Dp(6), Dp(12), Dp(3));
            AddNav(L10n.T("音乐"), SecAudio); AddNav(L10n.T("视频"), SecVideo); AddNav(L10n.T("图片"), SecPhoto);
            AddActionNav(L10n.T("设置"), ShowSettingsMenu);
            _navSeg.AddView(_navRow, new LinearLayout.LayoutParams(-1, -2));
            page.AddView(_navSeg);

            // 音乐栏目内的子切换：歌曲 / 歌单 / 喜欢（节省顶栏空间）
            _musicSubBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _musicSubBar.SetPadding(Dp(12), Dp(1), Dp(12), Dp(1));
            _subSongs = ChipBtn(L10n.T("歌曲"), () => SetSection(SecAudio));
            _subPlaylists = ChipBtn(L10n.T("歌单"), () => SetSection(SecPlaylist));
            _subFavorites = ChipBtn(L10n.T("喜欢"), () => SetSection(SecFav));
            _musicSubBar.AddView(_subSongs); _musicSubBar.AddView(Sp(6));
            _musicSubBar.AddView(_subPlaylists); _musicSubBar.AddView(Sp(6));
            _musicSubBar.AddView(_subFavorites);
            page.AddView(_musicSubBar);

            // 搜索框：iOS 搜索栏风格（浅灰底、圆角 10、无边框）
            var searchRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            searchRow.SetPadding(Dp(12), Dp(3), Dp(12), Dp(2));
            searchRow.SetGravity(GravityFlags.CenterVertical);

            // 搜索条整体做成 iOS searchBar：灰底圆角容器 + 放大镜 + 输入框 + 框内清除
            var searchWrap = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            searchWrap.SetGravity(GravityFlags.CenterVertical);
            searchWrap.Background = RoundBg(IosFill, 10);
            searchWrap.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            searchWrap.SetPadding(Dp(10), 0, Dp(6), 0);

            var glass = new IconView(this, IconKind.Search) { IconColor = TextSub, Thickness = 1.7f };
            searchWrap.AddView(glass, new LinearLayout.LayoutParams(Dp(18), Dp(18)) { RightMargin = Dp(6) });

            _search = new EditText(this) { Hint = L10n.T("搜索本机 / 在线音乐") };
            _search.TextSize = 16;
            _search.SetSingleLine(true);
            _search.SetMinHeight(Dp(38));
            _search.Background = null;               // 圆角由外层容器提供
            _search.SetPadding(0, Dp(8), Dp(4), Dp(8));
            _search.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            _search.Visibility = ViewStates.Visible;
            _search.TextChanged += (s, e) =>
            {
                _query = _search.Text ?? "";
                ReloadList();
                ScheduleOnlineSearch(_query);
                ScheduleVodSearch(_query);
            };
            searchWrap.AddView(_search);

            var clearBtn = new IconView(this, IconKind.Close) { IconColor = TextSub, Thickness = 1.7f };
            clearBtn.LayoutParameters = new LinearLayout.LayoutParams(Dp(30), Dp(30));
            clearBtn.Click += (s, e) => { _search.Text = ""; };
            searchWrap.AddView(clearBtn);

            searchRow.AddView(searchWrap);
            page.AddView(searchRow);

            // 图片分区专用工具条：按文件夹 / 全部图片，以及文件夹内的返回
            _photoBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _photoBar.SetGravity(GravityFlags.CenterVertical);
            _photoBar.SetPadding(Dp(14), 0, Dp(14), Dp(6));
            _photoBar.Visibility = ViewStates.Gone;

            _photoCrumb = ChipBtn(L10n.T("文件夹"), () => { _photoFolder = null; ReloadList(); });
            Icons.Leading(_photoCrumb, IconKind.Back, ChipText, 12f, 3f);
            _photoCrumb.TextSize = 12;
            _photoBar.AddView(_photoCrumb);
            _photoBar.AddView(Sp(6));

            _photoModeFolder = ChipBtn(L10n.T("按文件夹"), () => { _photoMode = PhotoByFolder; ReloadList(); });
            _photoModeFolder.TextSize = 12;
            _photoBar.AddView(_photoModeFolder);
            _photoBar.AddView(Sp(6));

            _photoModeAll = ChipBtn(L10n.T("全部图片"), () => { _photoMode = PhotoAll; ReloadList(); });
            _photoModeAll.TextSize = 12;
            _photoBar.AddView(_photoModeAll);
            page.AddView(_photoBar);

            // 状态行
            _subText = new TextView(this) { TextSize = 12 };
            _subText.SetTextColor(new Color(TextSub));
            _subText.SetPadding(Dp(16), Dp(3), Dp(16), Dp(4));
            page.AddView(_subText);

            // 内容容器：音乐/喜欢走列表，视频走海报卡片网格
            var content = new FrameLayout(this);
            content.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);

            // 列表：iOS 风格——白底、1px 分隔线且从封面右侧缩进（inset separator）
            int hairline = Math.Max(1, (int)(Resources.DisplayMetrics.Density * 0.5f));
            _list = new ListView(this)
            {
                Divider = new Android.Graphics.Drawables.InsetDrawable(
                    new ColorDrawable(new Color(Line)), Dp(76), 0, 0, 0),
                DividerHeight = hairline,
            };
            _list.SetBackgroundColor(new Color(CardBg));
            _adapter = new SongAdapter(this);
            _list.Adapter = _adapter;
            _list.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _list.ItemClick += (s, e) =>
            {
                if (e.Position < 0 || e.Position >= _displayList.Count) return;
                var song = _displayList[e.Position];
                if (song.IsDivider) return;
                if (song.IsVideo) { OpenVideo(song); return; }
                if (song.IsOnline)
                {
                    // 点的就是当前这首：只打开播放页，不重头再加载
                    if (MusicPlayer.Current != null &&
                        string.Equals(MusicPlayer.Current.Path, song.Path, StringComparison.OrdinalIgnoreCase))
                    { ShowPlayer(true); return; }

                    // 在线歌曲：把本页所有在线结果作为一组队列播放
                    var online = _displayList.FindAll(x => x.IsOnline);
                    int idx = online.FindIndex(x => x.Path == song.Path);
                    MusicPlayer.PlayAll(online, Math.Max(0, idx));
                    ShowPlayer(true);
                    // 当前曲 + 后续 2 首后台预缓存，轮到时就是本地秒开
                    try { OnlineMusic.PrefetchQueue(online.Skip(Math.Max(0, idx)).Take(3)); } catch { }
                    return;
                }
                var local = _displayList.FindAll(x => !x.IsOnline && !x.IsDivider);
                int li = local.FindIndex(x => x.Path == song.Path);
                if (MusicPlayer.Current != null &&
                    MusicPlayer.Queue.Count == local.Count &&
                    MusicPlayer.Current.Path == song.Path)
                { ShowPlayer(true); return; }
                MusicPlayer.PlayAll(local, Math.Max(0, li));
                ShowPlayer(true);   // 点歌后立刻进播放页（之前漏了这句，导致「播了但不切页」）
            };
            content.AddView(_list);

            // 右侧 A-Z 索引条：触摸/拖动直接跳到该拼音首字母的第一首歌
            _musicIndex = new AlphabetIndexBar(this, ThemeManager.IsNight(this));
            var azLp = new FrameLayout.LayoutParams(Dp(26), -1, GravityFlags.Right | GravityFlags.CenterVertical);
            azLp.SetMargins(0, Dp(10), Dp(2), Dp(10));
            _musicIndex.LayoutParameters = azLp;
            _musicIndex.LetterSelected += letter => JumpToMusicLetter(letter);
            content.AddView(_musicIndex);

            _grid = new GridView(this) { NumColumns = 2 };
            _grid.SetBackgroundColor(new Color(PageBg));
            _grid.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
            _grid.SetHorizontalSpacing(Dp(8));
            _grid.SetVerticalSpacing(Dp(8));
            _grid.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _gridAdapter = new VideoGridAdapter(this);
            _grid.Adapter = _gridAdapter;
            _grid.ItemClick += (s, e) =>
            {
                var items = CurrentList();
                if (e.Position >= 0 && e.Position < items.Count) OpenVideo(items[e.Position]);
            };
            _grid.Visibility = ViewStates.Gone;
            content.AddView(_grid);

            // 图片分区：3 列缩略图网格，文件夹卡片与图片卡片共用
            _photoGrid = new GridView(this) { NumColumns = 3 };
            _photoGrid.SetBackgroundColor(new Color(PageBg));
            _photoGrid.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
            _photoGrid.SetHorizontalSpacing(Dp(6));
            _photoGrid.SetVerticalSpacing(Dp(6));
            _photoGrid.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _photoAdapter = new PhotoGridAdapter(this);
            _photoGrid.Adapter = _photoAdapter;
            _photoGrid.ItemClick += (s, e) =>
            {
                var items = CurrentPhotoItems();
                if (e.Position < 0 || e.Position >= items.Count) return;
                var it = items[e.Position];
                if (it.Folder != null)
                {
                    _photoFolder = it.Folder.Path;
                    ReloadList();
                    return;
                }
                if (it.Photo != null)
                {
                    var photos = items.Where(x => x.Photo != null).Select(x => x.Photo).ToList();
                    int idx = photos.FindIndex(p => p.Path == it.Photo.Path);
                    PhotoActivity.Show(this, photos, Math.Max(0, idx));
                }
            };
            _photoGrid.Visibility = ViewStates.Gone;
            content.AddView(_photoGrid);
            // 在线影视：TVBox 多渠道聚合海报墙（工具行 + 网格），逻辑见 MusicActivity.Vod.cs
            content.AddView(BuildVodPage());
            page.AddView(content);

            // 迷你播放条（iOS tab bar / mini player 风格：白底 + 顶部 1px 分隔线）
            _mini = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _mini.SetGravity(GravityFlags.CenterVertical);
            _mini.SetPadding(Dp(12), Dp(8), Dp(10), Dp(8));
            _mini.SetBackgroundColor(new Color(CardBg));
            _mini.Visibility = ViewStates.Gone;
            _mini.Click += (s, e) => ShowPlayer(true);

            var coverBox = new FrameLayout(this);
            coverBox.LayoutParameters = new LinearLayout.LayoutParams(Dp(44), Dp(44));
            _miniCover = new ImageView(this) { };
            _miniCover.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _miniCover.SetScaleType(ImageView.ScaleType.CenterCrop);
            _miniCover.Background = RoundBg(IosFill, 10);
            coverBox.AddView(_miniCover);
            _mini.AddView(coverBox);

            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(Dp(12), 0, Dp(8), 0);
            col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            _miniTitle = new TextView(this) { TextSize = 14 };
            _miniTitle.SetTextColor(new Color(TextMain));
            _miniTitle.SetSingleLine(true);
            _miniTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _miniArtist = new TextView(this) { TextSize = 12 };
            _miniArtist.SetTextColor(new Color(TextSub));
            _miniArtist.SetSingleLine(true);
            _miniArtist.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            // 第三行：当前歌词（本地/在线一致，随播放实时滚动）
            _miniLyric = new TextView(this) { TextSize = 11 };
            _miniLyric.SetTextColor(new Color(Accent));
            _miniLyric.SetSingleLine(true);
            _miniLyric.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _miniLyric.Visibility = ViewStates.Gone;
            col.AddView(_miniTitle);
            col.AddView(_miniArtist);
            col.AddView(_miniLyric);
            _mini.AddView(col);

            _miniToggle = new IconView(this, IconKind.Play) { IconColor = TextMain, Thickness = 1.7f };
            _miniToggle.LayoutParameters = new LinearLayout.LayoutParams(Dp(40), Dp(40));
            _miniToggle.Click += (s, e) => MusicPlayer.Toggle();
            _mini.AddView(_miniToggle);

            // 迷你条上也能直接切模式和看队列，不必先进播放页
            _miniMode = new TextView(this) { TextSize = 11, Gravity = GravityFlags.Center };
            _miniMode.SetTextColor(new Color(ChipText));
            _miniMode.Background = RoundBg(ChipBg, 7);
            _miniMode.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(30)) { RightMargin = Dp(6) };
            _miniMode.Click += (s, e) =>
            {
                MusicPlayer.CycleMode();
                SyncModes();
                L10n.Toast(this, MusicPlayer.ModeName, ToastLength.Short).Show();
            };
            _mini.AddView(_miniMode);

            var miniQueue = new IconView(this, IconKind.Queue) { IconColor = ChipText, Thickness = 1.7f };
            miniQueue.Background = RoundBg(ChipBg, 7);
            miniQueue.LayoutParameters = new LinearLayout.LayoutParams(Dp(34), Dp(30)) { RightMargin = Dp(4) };
            miniQueue.Click += (s, e) => ShowQueueDialog();
            _mini.AddView(miniQueue);

            _miniNext = new IconView(this, IconKind.Next) { IconColor = TextMain, Thickness = 1.7f };
            _miniNext.LayoutParameters = new LinearLayout.LayoutParams(Dp(36), Dp(36));
            _miniNext.Click += (s, e) => MusicPlayer.Next();
            _mini.AddView(_miniNext);

            // 底部直接贴边：不为全面屏手势条预留额外空白
            _miniDivider = HLine();
            _miniDivider.Visibility = ViewStates.Gone;
            page.AddView(_miniDivider);
            page.AddView(_mini);

            return page;
        }

        /// <summary>分区导航按钮（参考 Jellyfin 媒体库顶部导航）。</summary>
        private void AddNav(string text, int section)
        {
            var t = new TextView(this) { Text = text, TextSize = 13, Gravity = GravityFlags.Center };
            t.SetTypeface(null, TypefaceStyle.Bold);
            t.SetPadding(Dp(4), Dp(5), Dp(4), Dp(5));
            t.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f) { LeftMargin = Dp(2), RightMargin = Dp(2) };
            t.Click += (s, e) => SetSection(section);
            _navBtns.Add(t);
            _navRow.AddView(t);
        }

        /// <summary>动作型导航按钮（如「设置」）：不切换栏目，只弹浮窗。</summary>
        private void AddActionNav(string text, Action onClick)
        {
            var t = new TextView(this) { Text = text, TextSize = 13, Gravity = GravityFlags.Center };
            t.SetTypeface(null, TypefaceStyle.Bold);
            t.SetPadding(Dp(4), Dp(5), Dp(4), Dp(5));
            t.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f) { LeftMargin = Dp(2), RightMargin = Dp(2) };
            t.Click += (s, e) => { try { onClick(); } catch { } };
            _navBtns.Add(t);
            _navRow.AddView(t);
        }

        /// <summary>「设置」浮窗：目录设置 / 刷新媒体库 / 打开网络音乐页 / 一起听开关。</summary>
        private void ShowSettingsMenu()
        {
            try
            {
                bool lt = ListenTogether.Enabled;
                var items = new[]
                {
                    (IconKind.Music, L10n.T("一起听：") + (lt ? L10n.T("已开启（点击关闭）") : L10n.T("已关闭（点击开启）")), false, new Action(() =>
                    {
                        // 关闭后音乐卡片照常发送/更新，只是不再附带播放列表
                        ListenTogether.Enabled = !lt;
                        try
                        {
                            L10n.Toast(this, ListenTogether.Enabled
                                ? L10n.T("一起听已开启：本机播放时会把播放列表分享给局域网")
                                : L10n.T("一起听已关闭：本机音乐卡片不再附带播放列表"), ToastLength.Short).Show();
                        }
                        catch { }
                        ShowSettingsMenu();   // 重开一次让用户立刻看到新状态
                    })),
                    (IconKind.Folder, L10n.T("目录设置（检索范围）"), false, new Action(OpenDirsDialog)),
                    (IconKind.Refresh, L10n.T("刷新媒体库"), false, new Action(() => EnsureStorageThenScan(true))),
                    (IconKind.Globe, L10n.T("打开网络音乐页"), false, new Action(OpenWebMedia))
                };
                ShowActionSheet(L10n.T("设置"), items);
            }
            catch { }
        }

        /// <summary>打开网页端媒体页（与局域网网页端同一套后端，含在线搜索 / 播放 / 下载）。</summary>
        private void OpenWebMedia()
        {
            try
            {
                var uri = Android.Net.Uri.Parse("http://127.0.0.1:6800/media");
                StartActivity(new Intent(Intent.ActionView, uri));
            }
            catch
            {
                try { L10n.Toast(this, L10n.T("无法打开网络音乐页"), ToastLength.Short).Show(); } catch { }
            }
        }

        /// <summary>同步「歌曲/歌单/喜欢」子切换的可见性与选中态。</summary>
        private void SyncMusicSub()
        {
            if (_musicSubBar == null) return;
            bool musicCtx = NavIndexOf(_section) == 0;
            _musicSubBar.Visibility = musicCtx ? ViewStates.Visible : ViewStates.Gone;
            if (!musicCtx) return;
            HighlightChip(_subSongs, _section == SecAudio);
            HighlightChip(_subPlaylists, _section == SecPlaylist);
            HighlightChip(_subFavorites, _section == SecFav);
        }

        private void HighlightChip(TextView t, bool on)
        {
            if (t == null) return;
            t.SetTextColor(new Color(on ? Accent : ChipText));
            t.Background = RoundBg(on ? unchecked((int)0xFFE7F6FE) : ChipBg, 9);
        }

        private void SetSection(int section)
        {
            _section = section;
            if (section != SecAudio) { _onlineSongs.Clear(); _onlineSearching = false; }
            if (section == SecPhoto) { _photoMode = PhotoAll; _photoFolder = null; }  // 每次进入图片默认全部
            ShowPlaylists(section == SecPlaylist);
            ReloadList();
            if (section == SecVideo) OnEnterVideoTab();
        }

        /// <summary>顶栏索引：音乐(含歌单/喜欢) = 0，视频 = 1，图片 = 2，设置 = 3（不参与选中）。</summary>
        private static int NavIndexOf(int section)
        {
            if (section == SecVideo) return 1;
            if (section == SecPhoto) return 2;
            return 0;
        }

        private void SyncNav()
        {
            for (int i = 0; i < _navBtns.Count; i++)
            {
                bool on = i == NavIndexOf(_section);
                var t = _navBtns[i];
                t.SetTextColor(new Color(on ? TextMain : TextSub));
                // 选中项：白色胶囊 + 轻阴影（iOS segmented control）
                if (on)
                {
                    var d = new GradientDrawable();
                    d.SetColor(White);
                    d.SetCornerRadius(Dp(8));
                    t.Background = d;
                    t.Elevation = Dp(1);
                }
                else
                {
                    t.Background = RoundBg(unchecked((int)0x00FFFFFF), 8);
                    t.Elevation = 0;
                }
            }
        }

        /// <summary>当前分区要显示的条目（含搜索过滤）。</summary>
        private List<Song> CurrentList()
        {
            List<Song> items;
            switch (_section)
            {
                case SecVideo: items = MusicLibrary.Videos(); break;
                case SecPhoto:
                case SecPlaylist: items = new List<Song>(); break;
                case SecFav: items = PlaylistStore.SongsOf(PlaylistStore.FavoritesId)
                                    .Where(x => !x.IsVideo).ToList(); break;
                default: items = MusicLibrary.Audio(); break;
            }
            if (!string.IsNullOrWhiteSpace(_query))
            {
                var q = _query.Trim();
                items = items.FindAll(s =>
                    (s.DisplayTitle ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (s.Artist ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (s.Album ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (s.FileName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return items;
        }

        /// <summary>图片分区当前要显示的卡片：文件夹卡片，或（某个文件夹 / 全部）图片卡片。</summary>
        private List<PhotoItem> CurrentPhotoItems()
        {
            var list = new List<PhotoItem>();
            bool byFolder = _photoMode == PhotoByFolder;
            if (byFolder && string.IsNullOrEmpty(_photoFolder))
            {
                foreach (var f in PhotoLibrary.Folders())
                    list.Add(new PhotoItem { Folder = f });
            }
            else
            {
                var photos = byFolder ? PhotoLibrary.InFolder(_photoFolder) : PhotoLibrary.All();
                if (!string.IsNullOrWhiteSpace(_query))
                {
                    var q = _query.Trim();
                    photos = photos.FindAll(p =>
                        (p.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (p.Folder ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                foreach (var p in photos) list.Add(new PhotoItem { Photo = p });
            }
            return list;
        }

        private void ReloadList()
        {
            var items = CurrentList();
            // 视频栏目 = 本地视频 + 在线影视合并（统一由 _vodPage 的合并网格展示，角标区分本地/在线）
            bool videoTab = _section == SecVideo;
            bool photoGrid = _section == SecPhoto;
            bool anyGrid = photoGrid;   // 旧本地视频网格已并入 _vodPage，不再单独显示

            _displayList = BuildDisplayList(items);
            _adapter.Replace(anyGrid ? new List<Song>() : _displayList);
            _gridAdapter.Replace(new List<Song>());   // 本地视频并入合并网格
            _photoAdapter.Replace(photoGrid ? CurrentPhotoItems() : new List<PhotoItem>());
            _list.Visibility = (anyGrid || videoTab) ? ViewStates.Gone : ViewStates.Visible;
            _grid.Visibility = ViewStates.Gone;       // 本地视频并入 _vodPage
            _photoGrid.Visibility = photoGrid ? ViewStates.Visible : ViewStates.Gone;
            if (_vodPage != null) _vodPage.Visibility = videoTab ? ViewStates.Visible : ViewStates.Gone;
            if (_musicIndex != null)
                _musicIndex.Visibility = (!anyGrid && !photoGrid && !videoTab && _displayList.Count > 0)
                    ? ViewStates.Visible : ViewStates.Gone;
            SyncNav();
            SyncMusicSub();

            // 图片视图工具条：进入某个文件夹后才显示「← 文件夹」
            _photoBar.Visibility = photoGrid ? ViewStates.Visible : ViewStates.Gone;
            bool insideFolder = photoGrid && _photoMode == PhotoByFolder && !string.IsNullOrEmpty(_photoFolder);
            _photoCrumb.Visibility = insideFolder ? ViewStates.Visible : ViewStates.Gone;
            bool folderMode = _photoMode == PhotoByFolder;
            _photoModeFolder.SetTextColor(new Color(folderMode ? Accent : ChipText));
            _photoModeFolder.Background = RoundBg(folderMode ? unchecked((int)0xFFE7F6FE) : ChipBg, 9);
            _photoModeAll.SetTextColor(new Color(folderMode ? ChipText : Accent));
            _photoModeAll.Background = RoundBg(folderMode ? ChipBg : unchecked((int)0xFFE7F6FE), 9);

            var audio = MusicLibrary.Audio().Count;
            var video = MusicLibrary.Videos().Count;
            var photos = PhotoLibrary.Count;
            _subText.Text =
                (MusicLibrary.ExcludedCount > 0 ? $" · 已移出 {MusicLibrary.ExcludedCount}" : "") +
                (MusicLibrary.LastScan == default ? " · 未扫描" : $"")
                + (_section == SecAudio && !string.IsNullOrWhiteSpace(_query)
                    ? (_onlineSearching ? L10n.T(" · 在线搜索中…")
                       : _onlineSongs.Count > 0 ? $" ·  {_onlineSongs.Count} 首在线音乐"
                       : "") : "");
            if (_section == SecVideo) _subText.Text = VodStatusLine();
        }

        /// <summary>点右侧字母：滚动到第一首标题首字/拼音首字母匹配的歌曲。</summary>
        private void JumpToMusicLetter(string letter)
        {
            if (_list == null || _displayList == null || _displayList.Count == 0) return;
            string target = (letter ?? "").ToUpperInvariant();
            for (int i = 0; i < _displayList.Count; i++)
            {
                var s = _displayList[i];
                if (s.IsDivider) continue;
                if (Pinyin.FirstLetter(s.DisplayTitle).ToString().Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    _list.SetSelection(i);
                    _musicIndex?.SetActiveLetter(target);
                    return;
                }
            }
            // 没有该字母的歌：只高亮，不跳动列表，避免跳到无关位置
            _musicIndex?.SetActiveLetter(null);
        }

        /// <summary>在「音乐」分区 + 有搜索词时，于本地结果之后追加「在线音乐」分区标题与在线结果。</summary>
        private List<Song> BuildDisplayList(List<Song> local)
        {
            var list = new List<Song>(local);
            if (_section == SecAudio && !string.IsNullOrWhiteSpace(_query) && _onlineSongs.Count > 0)
            {
                list.Add(_onlineDivider);
                list.AddRange(_onlineSongs);
            }
            return list;
        }

        /// <summary>本地 + 在线聚合搜索：本地即时过滤；在线走酷狗搜索（防抖 + 丢弃过期结果）。</summary>
        private int _onlineTier = 1;          // 当前音源层级（仅搜索失败时升级；UI 不暴露具体音源名）
        private string _lastOnlineQuery = "";  // 上次搜索词，换词时重置层级

        private void ScheduleOnlineSearch(string q)
        {
            if (_section != SecAudio || string.IsNullOrWhiteSpace(q))
            {
                if (_onlineSongs.Count > 0 || _onlineSearching)
                { _onlineSongs.Clear(); _onlineSearching = false; ReloadList(); }
                return;
            }
            // 换词则回到默认音源重新搜索（层级只随“同一词的失败”上升）
            if (!string.Equals(q.Trim(), _lastOnlineQuery, StringComparison.Ordinal))
            {
                _onlineTier = 1;
                _lastOnlineQuery = q.Trim();
            }
            int token = ++_onlineToken;
            bool wasSearching = _onlineSearching;
            _onlineSearching = true;
            // 每次按键都会走这里，只有状态真正变化时才重刷列表（原来每个字符刷两次）
            if (!wasSearching) ReloadList();
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250);   // 防抖：比之前 350ms 更跟手；命中缓存时直接返回不联网
                    if (token != _onlineToken) return;
                    var list = await MusicSources.SearchAsync(q.Trim(), 1, _onlineTier);
                    if (token != _onlineToken) return;
                    var songs = list.Select(ToOnlineSong).ToList();
                    songs.Sort((a, b) => Pinyin.Compare(a.DisplayTitle, b.DisplayTitle));   // 在线结果同样 A-Z
                    RunOnUiThread(() =>
                    {
                        if (token != _onlineToken) return;
                        _onlineSongs = songs;
                        _onlineSearching = false;
                        ReloadList();
                        // 后台预热前几首的直链，点播时省掉代理解析的 2~3 秒
                        try { OnlineMusic.Warmup(songs, 4); } catch { }
                        // 默认音源无结果且还有兜底音源 → 弹一句无具体音源名的通用提示
                        if (songs.Count == 0 && !string.IsNullOrWhiteSpace(q)
                            && MusicSources.HasFallback(_onlineTier))
                        {
                            OfferMusicSourceFallback(q.Trim());
                        }
                    });
                }
                catch
                {
                    RunOnUiThread(() =>
                    {
                        if (token != _onlineToken) return;
                        _onlineSearching = false;
                        ReloadList();
                    });
                }
            });
        }

        /// <summary>默认音源无结果时，弹一句无具体音源名的通用提示，让用户决定是否尝试其它音源（兜底方案）。</summary>
        private void OfferMusicSourceFallback(string q)
        {
            try
            {
                var dlg = L10n.Dialog(this);
                dlg.SetTitle(L10n.T("没找到在线歌曲"));
                dlg.SetMessage(L10n.T("没有在默认音乐源找到「") + q + L10n.T("」。是否尝试其它音乐源再搜一次？"));
                dlg.SetPositiveButton(L10n.T("试试其它音源"), (s, e) =>
                {
                    _onlineTier++;
                    _onlineSearching = true;
                    ReloadList();
                    ScheduleOnlineSearch(q);   // 用更高层级音源重新搜索
                });
                dlg.SetNegativeButton(L10n.T("不用了"), (s, e) => { });
                dlg.Show();
            }
            catch { }
        }

        /// <summary>把在线搜索结果映射成可播放的 Song：Path 设为本地代理流地址，封面用远程 URL。</summary>
        private Song ToOnlineSong(OnlineSong o)
        {
            var s = new Song
            {
                IsOnline = true,
                Title = o.Title,
                Artist = o.Artist,
                Album = o.Album,
                DurationMs = o.DurationMs,
                OnlineId = o.Id,
                OnlinePlat = o.Plat,
                OnlineHqId = o.HqId,
                OnlineSqId = o.SqId,
                OnlineCover = OnlineMusic.ResolveCoverUrl(o),
                Path = "http://127.0.0.1:6789/api/media/online/stream?plat=" + Uri.EscapeDataString(o.Plat)
                     + "&id=" + Uri.EscapeDataString(o.Id) + "&q=standard",
            };
            // 若之前已经缓存过，直接标记，播放时本地秒开
            try
            {
                var c = OnlineMusic.ExistingCache(s, "standard");
                if (c != null) s.CachePath = c;
            }
            catch { }
            return s;
        }

        private void OpenVideo(Song s)
        {
            try
            {
                var i = new Intent(this, typeof(VideoActivity));
                i.PutExtra(VideoActivity.ExtraPath, s.Path);
                StartActivity(i);
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("无法播放视频：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /* ---------------- 目录管理 ---------------- */

        /// <summary>目录弹窗里三个媒体开关各自独立修改，互不覆盖。</summary>
        private static void SetKind3(string path, bool? audio, bool? video, bool? photo)
        {
            var d = MusicLibrary.Dirs.FirstOrDefault(x => x.Path == path);
            if (d == null) return;
            MusicLibrary.SetDirKind(path, audio ?? d.ForAudio, video ?? d.ForVideo, photo ?? d.ForPhoto);
        }

        private void OpenDirsDialog()
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(8), Dp(18), Dp(2));

            var hint = new TextView(this)
            {
                Text = L10n.T("只有勾选「检索」的目录会被扫描；音乐 / 视频 / 图片可分别指定，\n避免把下载、相机里的无关文件加入列表。"),
                TextSize = 12
            };
            hint.SetTextColor(new Color(TextSub));
            box.AddView(hint);

            var scroll = new ScrollView(this);
            scroll.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(300));
            var dirBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            dirBox.SetPadding(0, Dp(8), 0, 0);
            scroll.AddView(dirBox);
            box.AddView(scroll);

            void Rebuild()
            {
                dirBox.RemoveAllViews();
                foreach (var d in MusicLibrary.Dirs.ToList())
                {
                    var path = d.Path;
                    var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
                    card.Background = RoundBg(CardBg, 10);
                    card.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
                    card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(6) };

                    var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    head.SetGravity(GravityFlags.CenterVertical);
                    var nameCol = new LinearLayout(this) { Orientation = Orientation.Vertical };
                    nameCol.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    var nm = new TextView(this) { Text = d.Label, TextSize = 14 };
                    nm.SetTextColor(new Color(TextMain));
                    nm.SetSingleLine(true);
                    nm.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    var pa = new TextView(this) { Text = d.Path, TextSize = 10 };
                    pa.SetTextColor(new Color(TextSub));
                    pa.SetSingleLine(true);
                    pa.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
                    nameCol.AddView(nm);
                    nameCol.AddView(pa);
                    head.AddView(nameCol);

                    var en = new CheckBox(this) { Text = L10n.T("检索"), Checked = d.Enabled, TextSize = 12 };
                    en.CheckedChange += (s, e) =>
                    {
                        MusicLibrary.SetDirEnabled(path, e.IsChecked);
                        ReloadList();
                    };
                    head.AddView(en);
                    card.AddView(head);

                    var kinds = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    kinds.SetGravity(GravityFlags.CenterVertical);
                    var ca = new CheckBox(this) { Text = L10n.T("音乐"), Checked = d.ForAudio, TextSize = 12 };
                    ca.CheckedChange += (s, e) => SetKind3(path, e.IsChecked, null, null);
                    kinds.AddView(ca);
                    var cv = new CheckBox(this) { Text = L10n.T("视频"), Checked = d.ForVideo, TextSize = 12 };
                    cv.CheckedChange += (s, e) => SetKind3(path, null, e.IsChecked, null);
                    kinds.AddView(cv);
                    var cp = new CheckBox(this) { Text = L10n.T("图片"), Checked = d.ForPhoto, TextSize = 12 };
                    cp.CheckedChange += (s, e) => SetKind3(path, null, null, e.IsChecked);
                    kinds.AddView(cp);
                    kinds.AddView(new View(this) { LayoutParameters = new LinearLayout.LayoutParams(0, 1, 1f) });
                    if (!d.BuiltIn)
                    {
                        var del = new TextView(this) { Text = L10n.T("移除"), TextSize = 12 };
                        del.SetTextColor(new Color(unchecked((int)0xFFE5484D)));
                        del.SetPadding(Dp(10), Dp(4), Dp(2), Dp(4));
                        del.Click += (s, e) =>
                        {
                            MusicLibrary.RemoveDir(path);
                            Rebuild();
                            ReloadList();
                        };
                        kinds.AddView(del);
                    }
                    card.AddView(kinds);
                    dirBox.AddView(card);
                }
            }
            Rebuild();

            var f1 = new CheckBox(this)
            { Text = L10n.T("过滤过短 / 过小的音频（铃声、提示音）"), Checked = MusicLibrary.FilterShortAudio, TextSize = 12 };
            f1.CheckedChange += (s, e) => { MusicLibrary.FilterShortAudio = e.IsChecked; MusicLibrary.SaveDirs(); };
            box.AddView(f1);
            var f2 = new CheckBox(this)
            { Text = L10n.T("跳过系统音与隐藏目录"), Checked = MusicLibrary.SkipSystemSounds, TextSize = 12 };
            f2.CheckedChange += (s, e) => { MusicLibrary.SkipSystemSounds = e.IsChecked; MusicLibrary.SaveDirs(); };
            box.AddView(f2);

            var input = new EditText(this) { Hint = L10n.T("添加目录：/storage/emulated/0/Music") };
            input.TextSize = 13;
            input.Background = RoundBg(ChipBg, 10);
            input.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            input.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6) };
            box.AddView(input);

            // 添加时就确定这个目录扫描哪些类型（默认全开，按需取消）
            var kindsRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            kindsRow.SetPadding(Dp(2), Dp(4), Dp(2), Dp(2));
            var cbAudio = new CheckBox(this) { Text = L10n.T("音乐"), Checked = true, TextSize = 12 };
            var cbVideo = new CheckBox(this) { Text = L10n.T("视频"), Checked = true, TextSize = 12 };
            var cbPhoto = new CheckBox(this) { Text = L10n.T("图片"), Checked = true, TextSize = 12 };
            kindsRow.AddView(cbAudio);
            kindsRow.AddView(cbVideo);
            kindsRow.AddView(cbPhoto);
            box.AddView(kindsRow);

            var quick = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            quick.SetPadding(0, Dp(6), 0, Dp(2));
            foreach (var q in new[] { "/storage/emulated/0/Music", "/storage/emulated/0/Movies",
                                      "/storage/emulated/0/Pictures", "/storage/emulated/0/Download" })
            {
                quick.AddView(ChipBtn(IoPath.GetFileName(q), () => { input.Text = q; }));
                quick.AddView(Sp(6));
            }
            box.AddView(quick);

            var exBtn = ChipBtn(L10n.T("已移出的文件（") + MusicLibrary.ExcludedCount + "）", OpenExcludedDialog);
            exBtn.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(6) };
            box.AddView(exBtn);

            L10n.Dialog(this)
                .SetTitle(L10n.T("默认目录与检索范围"))
                .SetView(box)
                .SetPositiveButton(L10n.T("添加并扫描"), (s, e) =>
                {
                    var v = (input.Text ?? "").Trim();
                    if (v.Length > 0) MusicLibrary.AddDir(v, cbAudio.Checked, cbVideo.Checked, cbPhoto.Checked);
                    EnsureStorageThenScan(true);
                })
                .SetNeutralButton(L10n.T("恢复默认"), (s, e) =>
                {
                    MusicLibrary.ResetDefaults();
                    ReloadList();
                    EnsureStorageThenScan(true);
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) =>
                {
                    ReloadList();
                    // 勾选/新增目录后离开弹窗时自动重扫一次，图片开关立刻生效
                    if (MusicLibrary.Dirty) EnsureStorageThenScan(false);
                })
                .Create().Show();
        }

        /// <summary>查看 / 恢复被「移出列表」屏蔽的文件（磁盘文件始终保留）。</summary>
        private void OpenExcludedDialog()
        {
            var paths = MusicLibrary.Excluded;
            if (paths.Count == 0)
            {
                L10n.Toast(this, L10n.T("没有被移出的文件"), ToastLength.Short).Show();
                return;
            }
            var names = paths.Select(p => IoPath.GetFileName(p)).ToArray();
            L10n.Dialog(this)
                .SetTitle(L10n.T("已移出的文件（点击恢复到列表）"))
                .SetItems(names, (s, e) =>
                {
                    MusicLibrary.Restore(paths[e.Which]);
                    EnsureStorageThenScan(true);
                })
                .SetPositiveButton(L10n.T("全部恢复"), (s, e) =>
                {
                    MusicLibrary.RestoreAll();
                    EnsureStorageThenScan(true);
                })
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Create().Show();
        }

        /* ---------------- 存储权限与扫描 ---------------- */

        private void EnsureStorageThenScan(bool explicitAsk)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                bool all;
                try { all = Android.OS.Environment.IsExternalStorageManager; }
                catch { all = true; }
                if (!all)
                {
                    _subText.Text = L10n.T("需要「所有文件访问」权限才能扫描音乐目录");
                    if (explicitAsk)
                    {
                        try
                        {
                            StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                                Android.Net.Uri.Parse("package:" + PackageName)));
                        }
                        catch
                        {
                            try { StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission)); }
                            catch { L10n.Toast(this, L10n.T("请在系统设置里授予「所有文件访问」"), ToastLength.Short).Show(); }
                        }
                    }
                    return;
                }
            }
            else
            {
                // Android 10 及以下：传统运行时存储权限（配合 manifest 的 requestLegacyExternalStorage）
                bool granted;
                try
                {
                    granted = Build.VERSION.SdkInt < BuildVersionCodes.M ||
                              CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage)
                                == Permission.Granted;
                }
                catch { granted = true; }
                if (!granted)
                {
                    _subText.Text = L10n.T("需要存储权限才能扫描音乐目录");
                    if (explicitAsk && Build.VERSION.SdkInt >= BuildVersionCodes.M)
                        RequestPermissions(new[] { Android.Manifest.Permission.ReadExternalStorage }, 3001);
                    return;
                }
            }

            if (_scanning) return;
            // 静默进入：已扫过、且没有「旧缓存里没找过封面」的条目时不重复扫
            if (!explicitAsk && !MusicLibrary.Dirty && MusicLibrary.Songs.Count > 0 &&
                !MusicLibrary.Songs.Any(s => !s.IsVideo && !s.CoverChecked))
                return;
            StartScan();
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == 3001 && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                StartScan();
        }

        private void StartScan()
        {
            if (_scanning) return;
            _scanning = true;
            _subText.Text = L10n.T("正在扫描…");
            var prog = new Progress<string>(msg => RunOnUiThread(() => _subText.Text = L10n.T("正在扫描… ") + msg));
            MusicLibrary.ScanAsync(prog).ContinueWith(t =>
            {
                RunOnUiThread(() =>
                {
                    _scanning = false;
                    ReloadList();
                    if (t.IsFaulted)
                    {
                        _subText.Text = L10n.T("扫描失败：") + t.Exception?.GetBaseException()?.Message;
                        return;
                    }
                    var r = t.Result;
                    var msg = $"扫描完成：歌曲 {r.Total - r.Videos} · 视频 {r.Videos} · 图片 {r.Photos}" +
                              (r.Errors.Count > 0 ? $"（{r.Errors.Count} 个目录出错）" : "");
                    _subText.Text = msg;
                    if (r.Total == 0 && r.Photos == 0 && MusicLibrary.Dirs.Count > 0)
                        L10n.Toast(this, L10n.T("目录里没有找到可检索的媒体文件"), ToastLength.Short).Show();
                });
            });
        }

        /* ============================================================
         * 歌单 / 喜欢 / 三点菜单 / 系统分享
         * ============================================================ */

        private LinearLayout BuildPlaylistPage()
        {
            var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
            page.LayoutParameters = new LinearLayout.LayoutParams(-1, -1);
            page.SetBackgroundColor(new Color(PageBg));

            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            top.SetPadding(Dp(10), Dp(10), Dp(14), Dp(10));
            top.SetBackgroundColor(new Color(CardBg));
            top.AddView(IconBtn(IconKind.Back, () => SetSection(0)));
            var title = new TextView(this) { Text = L10n.T("歌单"), TextSize = 19 };
            title.SetTextColor(new Color(TextMain));
            top.AddView(title);
            top.AddView(new View(this) { LayoutParameters = new LinearLayout.LayoutParams(0, 1, 1f) });
            var newPlBtn = ChipBtn(L10n.T("新建"), () => PromptText(L10n.T("新建歌单"), "", n =>
            {
                PlaylistStore.Create(n);
                RefreshPlaylists();
            }));
            Icons.Leading(newPlBtn, IconKind.Plus, ChipText, 12f, 3f);
            top.AddView(newPlBtn);
            page.AddView(top);

            var hint = new TextView(this)
            {
                Text = L10n.T("在歌曲列表右侧的「更多」里可「设为喜欢 / 加入歌单 / 分享 / 从列表移除」。"),
                TextSize = 11
            };
            hint.SetTextColor(new Color(TextSub));
            hint.SetPadding(Dp(18), Dp(10), Dp(18), Dp(4));
            page.AddView(hint);

            var scroll = new ScrollView(this);
            scroll.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);
            _plBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _plBox.SetPadding(Dp(14), Dp(8), Dp(14), Dp(24));
            scroll.AddView(_plBox);
            page.AddView(scroll);
            return page;
        }

        private void ShowPlaylists(bool show)
        {
            _inPlaylists = show;
            if (show) RefreshPlaylists();
            _plPage.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _libPage.Visibility = show ? ViewStates.Gone : ViewStates.Visible;
            if (!show && _section == SecPlaylist) { _section = SecAudio; SyncNav(); }
        }

        private void RefreshPlaylists()
        {
            if (_plBox == null) return;
            _plBox.RemoveAllViews();
            foreach (var pl in PlaylistStore.AllLists)
            {
                var id = pl.Id;
                var card = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                card.SetGravity(GravityFlags.CenterVertical);
                card.Background = RoundBg(CardBg, 12);
                card.SetPadding(Dp(16), Dp(14), Dp(10), Dp(14));
                card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(8) };

                // 封面：取该歌单最新添加且含封面的歌曲（无则音符占位）
                var coverBox = new FrameLayout(this);
                coverBox.LayoutParameters = new LinearLayout.LayoutParams(Dp(46), Dp(46))
                { RightMargin = Dp(14) };
                var cover = new ImageView(this);
                cover.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
                cover.SetScaleType(ImageView.ScaleType.CenterCrop);
                cover.Background = RoundBg(ChipBg, 8);
                coverBox.AddView(cover);
                card.AddView(coverBox);
                var pcb = CachedCover(PlaylistCoverSong(id), 96);
                if (pcb != null && !pcb.IsRecycled)
                {
                    cover.SetImageBitmap(pcb);
                    cover.SetScaleType(ImageView.ScaleType.CenterCrop);
                }
                else
                {
                    cover.SetImageBitmap(PlaceholderBitmap(true));
                    cover.SetScaleType(ImageView.ScaleType.Center);
                }

                var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                var nm = new TextView(this) { Text = pl.Name, TextSize = 16 };
                nm.SetTextColor(new Color(id == PlaylistStore.FavoritesId
                    ? unchecked((int)0xFFE5484D) : TextMain));
                nm.SetSingleLine(true);
                var sub = new TextView(this)
                { Text = PlaylistStore.CountOf(id) + L10n.T(" 首歌曲"), TextSize = 12 };
                sub.SetTextColor(new Color(TextSub));
                col.AddView(nm);
                col.AddView(sub);
                card.AddView(col);

                var more = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                more.SetGravity(GravityFlags.Center);
                more.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                more.AddView(new IconView(this, IconKind.MoreVertical) { IconColor = TextSub },
                    new LinearLayout.LayoutParams(Dp(22), Dp(22)));
                more.Click += (s, e) => ShowPlaylistMenu(more, id);
                card.AddView(more);

                card.Click += (s, e) => OpenPlaylistDetail(id);
                _plBox.AddView(card);
            }
        }

        private void ShowPlaylistMenu(View anchor, string id)
        {
            var items = new System.Collections.Generic.List<(IconKind, string, bool, Action)>
            {
                (IconKind.Play, L10n.T("播放全部"), false, () => PlayWholeList(id)),
                (IconKind.Queue, L10n.T("查看歌曲"), false, () => OpenPlaylistDetail(id))
            };
            if (id != PlaylistStore.FavoritesId)
            {
                items.Add((IconKind.Pen, L10n.T("重命名"), false, () =>
                    PromptText(L10n.T("重命名歌单"), PlaylistStore.NameOf(id), n =>
                    {
                        PlaylistStore.Rename(id, n);
                        RefreshPlaylists();
                    })));
                items.Add((IconKind.Trash, L10n.T("删除歌单"), true, () =>
                    L10n.Dialog(this)
                        .SetTitle(L10n.T("删除歌单"))
                        .SetMessage(L10n.T("确定删除「") + PlaylistStore.NameOf(id) + L10n.T("」？文件不会被删除。"))
                        .SetPositiveButton(L10n.T("删除"), (a, b) =>
                        {
                            PlaylistStore.Delete(id);
                            RefreshPlaylists();
                        })
                        .SetNegativeButton(L10n.T("取消"), (a, b) => { })
                        .Create().Show()));
            }
            ShowActionSheet(PlaylistStore.NameOf(id), items.ToArray());
        }

        private void PlayWholeList(string id)
        {
            var songs = PlaylistStore.SongsOf(id).Where(x => !x.IsVideo).ToList();
            if (songs.Count == 0)
            {
                L10n.Toast(this, L10n.T("这个歌单里还没有可播放的歌曲"), ToastLength.Short).Show();
                return;
            }
            MusicPlayer.PlayAll(songs, 0);
            ShowPlayer(true);
        }

        /// <summary>歌单详情：逐首播放 / 移出歌单 / 分享 / 从曲库列表移除。</summary>
        private void OpenPlaylistDetail(string id)
        {
            var pl = PlaylistStore.Get(id);
            if (pl == null) return;
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(10), Dp(14), Dp(2));
            var tip = new TextView(this) { TextSize = 11 };
            tip.SetTextColor(new Color(TextSub));
            box.AddView(tip);

            var scroll = new ScrollView(this);
            scroll.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(360));
            var rows = new LinearLayout(this) { Orientation = Orientation.Vertical };
            scroll.AddView(rows);
            box.AddView(scroll);

            var dlg = L10n.Dialog(this)
                .SetTitle(pl.Name)
                .SetView(box)
                .SetPositiveButton(L10n.T("播放全部"), (s, e) => PlayWholeList(id))
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Create();

            void Rebuild()
            {
                rows.RemoveAllViews();
                var songs = PlaylistStore.SongsOf(id);
                tip.Text = songs.Count == 0
                    ? L10n.T("还没有歌曲。到歌曲列表点右侧「更多」→「加入歌单」。")
                    : L10n.T("共 ") + songs.Count + L10n.T(" 首 · 点击播放，右侧「更多」可移出或分享");
                for (int k = 0; k < songs.Count; k++)
                {
                    var song = songs[k];
                    int idx = k;
                    var r = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    r.SetGravity(GravityFlags.CenterVertical);
                    r.SetPadding(0, Dp(9), 0, Dp(9));
                    var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                    col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    var t = new TextView(this) { Text = song.DisplayTitle, TextSize = 14 };
                    t.SetTextColor(new Color(TextMain));
                    t.SetSingleLine(true);
                    t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    var sub = new TextView(this) { Text = song.ArtistLine, TextSize = 11 };
                    sub.SetTextColor(new Color(TextSub));
                    sub.SetSingleLine(true);
                    col.AddView(t); col.AddView(sub);
                    r.AddView(col);

                    var more = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    more.SetGravity(GravityFlags.Center);
                    more.SetPadding(Dp(12), Dp(8), Dp(4), Dp(8));
                    more.AddView(new IconView(this, IconKind.MoreVertical) { IconColor = TextSub },
                        new LinearLayout.LayoutParams(Dp(22), Dp(22)));
                    more.Click += (s2, e2) => ShowSongMenu(more, song, Rebuild, id);
                    r.AddView(more);

                    r.Click += (s2, e2) =>
                    {
                        if (song.IsVideo) { OpenVideo(song); return; }
                        var list = PlaylistStore.SongsOf(id).Where(x => !x.IsVideo).ToList();
                        int at = list.FindIndex(x => x.Path == song.Path);
                        if (at < 0) at = 0;
                        MusicPlayer.PlayAll(list, at);
                        dlg.Dismiss();
                        ShowPlayer(true);
                    };
                    rows.AddView(r);
                    rows.AddView(new View(this)
                    {
                        LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(1)),
                        Background = new ColorDrawable(new Color(Line))
                    });
                }
            }
            Rebuild();
            dlg.Show();
        }

        /// <summary>歌曲右侧竖向三点展开的菜单。</summary>
        private void ShowSongMenu(View anchor, Song s, Action refresh = null, string fromListId = null)
        {
            var items = new System.Collections.Generic.List<(IconKind, string, bool, Action)>();
            if (s.IsOnline)
            {
                // 在线歌曲：播放 / 下一曲 / 加入播放列表 / 下载到本地（无本地文件，不能收藏、分享、移出）
                items.Add((IconKind.Play, L10n.T("播放"), false, () => PlaySongFromList(s)));
                items.Add((IconKind.Next, L10n.T("下一曲播放"), false, () =>
                {
                    MusicPlayer.PlayNext(s);
                    L10n.Toast(this, L10n.T("已设为下一曲播放"), ToastLength.Short).Show();
                }));
                items.Add((IconKind.Queue, L10n.T("加入播放列表（自动缓存）"), false, () =>
                {
                    MusicPlayer.AddToQueue(s);
                    L10n.Toast(this, L10n.T("已加入播放列表，后台缓存中…"), ToastLength.Short).Show();
                    OnlineMusic.PrefetchQueue(new List<Song> { s });
                }));
                items.Add((IconKind.Download, L10n.T("下载到本地"), false, () => DownloadOnline(s)));
            }
            else
            {
                items.Add((IconKind.Play, L10n.T("播放"), false, () => PlaySongFromList(s)));
                items.Add((IconKind.Next, L10n.T("下一曲播放"), false, () =>
                {
                    MusicPlayer.PlayNext(s);
                    L10n.Toast(this, L10n.T("已设为下一曲播放"), ToastLength.Short).Show();
                }));
                bool fav = PlaylistStore.IsFavorite(s.Path);
                items.Add((fav ? IconKind.HeartFilled : IconKind.Heart,
                    fav ? L10n.T("取消喜欢") : L10n.T("设为喜欢"), false, () =>
                    {
                        bool on = PlaylistStore.ToggleFavorite(s.Path);
                        L10n.Toast(this, on ? L10n.T("已加到我喜欢的音乐") : L10n.T("已取消喜欢"), ToastLength.Short).Show();
                        SyncFav();
                        RefreshPlaylists();
                    }));
                items.Add((IconKind.Plus, L10n.T("加入歌单"), false, () => OpenAddToList(s, refresh)));
                items.Add((IconKind.Share, L10n.T("分享"), false, () => ShareSong(s)));
                items.Add((IconKind.Group, L10n.T("分享到频道"), false, () => ShareSongToChannel(s)));
                if (fromListId != null) items.Add((IconKind.Close, L10n.T("从歌单移出"), false, () =>
                {
                    PlaylistStore.RemoveFrom(fromListId, s.Path);
                    L10n.Toast(this, L10n.T("已从歌单移出"), ToastLength.Short).Show();
                    RefreshPlaylists();
                }));
                items.Add((IconKind.Trash, L10n.T("从列表移除"), true, () =>
                {
                    MusicLibrary.Exclude(s.Path);
                    L10n.Toast(this, L10n.T("已从列表移出（文件未删除，可在「目录」里恢复）"), ToastLength.Long).Show();
                    ReloadList();
                }));
            }
            ShowActionSheet(s.DisplayTitle, items.ToArray(), refresh);
        }

        private void PlaySongFromList(Song s)
        {
            if (s.IsVideo) { OpenVideo(s); return; }
            if (s.IsOnline)
            {
                if (MusicPlayer.Current != null &&
                    string.Equals(MusicPlayer.Current.Path, s.Path, StringComparison.OrdinalIgnoreCase))
                { ShowPlayer(true); return; }
                MusicPlayer.PlayAll(new List<Song> { s }, 0);
                ShowPlayer(true);
                try { OnlineMusic.PrefetchQueue(new List<Song> { s }); } catch { }
                return;
            }
            var list = CurrentList();
            int i = list.FindIndex(x => x.Path == s.Path);
            if (i < 0) { list = new List<Song> { s }; i = 0; }
            MusicPlayer.PlayAll(list, i);
            ShowPlayer(true);
        }

        /// <summary>
        /// 把在线歌曲加入「下载中心」（统一管理：进度 / 失败重试 / 保存路径）。
        /// 关键：必须用源 Song 的完整元数据构造 OnlineSong（标题 / 歌手 / 封面 / 时长），
        /// 否则 DownloadAsync 拿不到 s.Title / s.Cover，封面与歌词就下不到。
        /// </summary>
        private void DownloadOnline(Song s)
        {
            if (!s.IsOnline) return;
            var o = new OnlineSong
            {
                Plat = s.OnlinePlat,
                Id = s.OnlineId,
                HqId = s.OnlineHqId,
                SqId = s.OnlineSqId,
                Title = s.Title,
                Artist = s.Artist,
                Album = s.Album,
                DurationMs = s.DurationMs,
                Cover = s.OnlineCover,
                FileName = s.FileName,
            };
            DownloadCenter.QueueMusic(o, "standard", s.Title);
            L10n.Toast(this, L10n.T("已加入下载队列，可在「下载」中查看进度"), ToastLength.Long).Show();
        }

        private void OpenAddToList(Song s, Action refresh = null)
        {
            var lists = PlaylistStore.AllLists;
            var names = lists.Select(x => x.Name + "（" + PlaylistStore.CountOf(x.Id) + "）")
                             .Concat(new[] { L10n.T("新建歌单") }).ToArray();
            L10n.Dialog(this)
                .SetTitle(L10n.T("加入歌单"))
                .SetItems(names, (sd, e) =>
                {
                    if (e.Which == lists.Count)
                    {
                        PromptText(L10n.T("新建歌单"), "", n =>
                        {
                            var p = PlaylistStore.Create(n);
                            PlaylistStore.AddTo(p.Id, s.Path);
                            L10n.Toast(this, L10n.T("已加入「") + p.Name + "」", ToastLength.Short).Show();
                            RefreshPlaylists();
                            refresh?.Invoke();
                        });
                    }
                    else
                    {
                        var p = lists[e.Which];
                        PlaylistStore.AddTo(p.Id, s.Path);
                        L10n.Toast(this, L10n.T("已加入「") + p.Name + "」", ToastLength.Short).Show();
                        RefreshPlaylists();
                        refresh?.Invoke();
                    }
                })
                .SetNegativeButton(L10n.T("取消"), (sd, e) => { })
                .Create().Show();
        }

        /// <summary>调用系统分享，把原始文件经 FileProvider 以 content:// 发出去。</summary>
        private void ShareSong(Song s)
        {
            try
            {
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    this, "com.Ahochat.android.fileprovider", new Java.IO.File(s.Path));
                var i = new Intent(Intent.ActionSend);
                i.SetType(s.IsVideo ? "video/*" : "audio/*");
                i.PutExtra(Intent.ExtraStream, uri);
                i.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(Intent.CreateChooser(i, L10n.T("分享文件")));
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        /// <summary>分享到频道：对端点快捷卡片直接进它的影音列表（音乐 / 视频通用）。</summary>
        private void ShareSongToChannel(Song s)
        {
            try
            {
                if (!MediaShare.ShareToChannel(this, System.IO.Path.GetFileName(s.Path), s.Path))
                    L10n.Toast(this, L10n.T("未能发起分享"), ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                L10n.Toast(this, L10n.T("分享失败：") + ex.Message, ToastLength.Short).Show();
            }
        }

        private void PromptText(string title, string initial, Action<string> ok)
        {
            var et = new EditText(this) { Text = initial ?? "" };
            et.SetSingleLine(true);
            et.TextSize = 14;
            et.Background = RoundBg(ChipBg, 10);
            et.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            var wrap = new FrameLayout(this);
            wrap.SetPadding(Dp(20), Dp(12), Dp(20), 0);
            wrap.AddView(et);
            L10n.Dialog(this)
                .SetTitle(title)
                .SetView(wrap)
                .SetPositiveButton(L10n.T("确定"), (s, e) =>
                {
                    var v = (et.Text ?? "").Trim();
                    if (v.Length > 0) ok(v);
                })
                .SetNegativeButton(L10n.T("取消"), (s, e) => { })
                .Create().Show();
        }

        /* ============================================================
         * 播放页
         * ============================================================ */

        private LinearLayout BuildPlayerPage()
        {
            _lyricViews.Clear();
            _lyricIdx = -2;
            bool landscape = Resources.Configuration.Orientation == Android.Content.Res.Orientation.Landscape;

            var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
            // 必须显式撑满：父容器是 WrapContent 时，子项 weight 会被算成 0 高
            page.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            page.SetBackgroundColor(new Color(PageBg));

            /* --- 顶栏 --- */
            _playerTop = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _playerTop.SetGravity(GravityFlags.CenterVertical);
            _playerTop.SetPadding(Dp(4), Dp(8), Dp(10), Dp(8));
            _playerTop.SetBackgroundColor(new Color(CardBg));

            _playerTop.AddView(IconBtn(IconKind.Back, () => { if (_fromReader) Finish(); else ShowPlayer(false); }));
            _pTitle = new TextView(this) { TextSize = 17 };
            _pTitle.SetTypeface(null, TypefaceStyle.Bold);
            _pTitle.SetTextColor(new Color(TextMain));
            _pTitle.SetSingleLine(true);
            _pTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _pTitle.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            _pTitle.SetPadding(Dp(8), 0, Dp(8), 0);
            _playerTop.AddView(_pTitle);
            _speedBtn = ChipBtn("1.0x", () => { var v = MusicPlayer.CycleSpeed(); _speedBtn.Text = v.ToString("0.0#") + "x"; });
            _playerTop.AddView(_speedBtn);
            _playerTop.AddView(Sp(6));
            _btnFav = new IconView(this, IconKind.Heart) { IconColor = ChipText, Thickness = 1.7f };
            _btnFav.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38)) { RightMargin = Dp(4) };
            _btnFav.Click += (s, e) =>
            {
                var s2 = MusicPlayer.Current;
                if (s2 == null) return;
                bool on = PlaylistStore.ToggleFavorite(s2.Path);
                L10n.Toast(this, on ? L10n.T("已加到我喜欢的音乐") : L10n.T("已取消喜欢"), ToastLength.Short).Show();
                SyncFav();
                RefreshPlaylists();
            };
            _playerTop.AddView(_btnFav);
            // 系统分享：把当前这首歌原样转发给其它应用（微信 / QQ / 网盘等）
            var sysShare = new IconView(this, IconKind.Share) { IconColor = ChipText, Thickness = 1.7f };
            sysShare.LayoutParameters = new LinearLayout.LayoutParams(Dp(38), Dp(38)) { RightMargin = Dp(4) };
            sysShare.Click += (s, e) =>
            {
                var cur = MusicPlayer.Current;
                if (cur == null || cur.IsOnline || string.IsNullOrEmpty(cur.Path) || !System.IO.File.Exists(cur.Path))
                {
                    L10n.Toast(this, L10n.T("在线歌曲请先「下载到本地」后再分享"), ToastLength.Short).Show();
                    return;
                }
                ShareSong(cur);
            };
            _playerTop.AddView(sysShare);
            page.AddView(_playerTop);
            page.AddView(HLine());

            /* --- 中部：正方形封面 / 歌词 --- */
            var center = new LinearLayout(this)
            {
                Orientation = landscape ? Orientation.Horizontal : Orientation.Vertical
            };
            center.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);

            // 封面区：外层 FrameLayout 占位，内层 SquareFrame 始终量成正方形并居中
            var coverBox = new FrameLayout(this);
            coverBox.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);
            coverBox.SetPadding(landscape ? Dp(12) : Dp(24), Dp(10),
                                landscape ? Dp(6) : Dp(24), Dp(6));

            var square = new SquareFrame(this);
            square.LayoutParameters = new FrameLayout.LayoutParams(-2, -2)
            { Gravity = GravityFlags.Center };
            square.Background = RoundBg(unchecked((int)0xFFE9EDF4), 20);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                square.OutlineProvider = new RoundOutlineProvider(Dp(20));
                square.ClipToOutline = true;
                square.Elevation = Dp(6);       // iOS 风格：封面浮起
            }

            _cover = new ImageView(this);
            _cover.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            _cover.SetScaleType(ImageView.ScaleType.CenterCrop);
            square.AddView(_cover);
            coverBox.AddView(square);

            // 歌手行（封面下方，单行不占权重）
            _pArtist = new TextView(this) { TextSize = 12 };
            _pArtist.SetTextColor(new Color(TextSub));
            _pArtist.SetSingleLine(true);
            _pArtist.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _pArtist.Gravity = GravityFlags.Center;
            _pArtist.SetPadding(Dp(18), 0, Dp(18), 0);

            if (landscape)
            {
                // 横屏：左列（封面 + 歌手）
                var left = new LinearLayout(this) { Orientation = Orientation.Vertical };
                left.LayoutParameters = new LinearLayout.LayoutParams(0, -1, 0.46f);
                left.AddView(coverBox);
                left.AddView(_pArtist, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(6) });
                center.AddView(left);
            }
            else
            {
                // 竖屏：上封面、下歌词
                center.AddView(coverBox);
                center.AddView(_pArtist, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(6) });
            }

            // 歌词区：横屏在右，竖屏在下
            _lyricScroll = new ScrollView(this);
            _lyricPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _lyricPanel.SetPadding(Dp(24), Dp(16), Dp(24), Dp(24));
            _lyricScroll.AddView(_lyricPanel);

            var emptyBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
            emptyBox.SetGravity(GravityFlags.Center);
            emptyBox.SetPadding(Dp(24), Dp(12), Dp(24), Dp(12));
            var emptyIcon = new IconView(this, IconKind.Music)
            { IconColor = unchecked((int)0xFFC4CCD8), Thickness = 1.3f };
            emptyBox.AddView(emptyIcon, new LinearLayout.LayoutParams(Dp(36), Dp(36))
            { Gravity = GravityFlags.CenterHorizontal, BottomMargin = Dp(8) });
            var emptyTitle = new TextView(this)
            { Text = L10n.T("无歌词"), TextSize = 16, Gravity = GravityFlags.Center };
            emptyTitle.SetTextColor(new Color(TextSub));
            emptyBox.AddView(emptyTitle);
            _lyricEmptyTitle = emptyTitle;
            _lyricEmpty = emptyBox;

            var lyricBox = new FrameLayout(this);
            lyricBox.AddView(_lyricScroll, new FrameLayout.LayoutParams(-1, -1));
            lyricBox.AddView(_lyricEmpty, new FrameLayout.LayoutParams(-1, -2)
            { Gravity = GravityFlags.Center });
            if (landscape)
            {
                lyricBox.LayoutParameters = new LinearLayout.LayoutParams(0, -1, 0.54f);
                lyricBox.SetPadding(Dp(6), Dp(8), Dp(12), 0);
                center.AddView(lyricBox);
            }
            else
            {
                lyricBox.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1.12f);
                center.AddView(lyricBox);
            }
            page.AddView(center);

            /* --- 内嵌播放队列（默认收起，点「播放列表」展开） --- */
            _queuePanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _queuePanel.SetBackgroundColor(new Color(PageBg));
            _queuePanel.Visibility = ViewStates.Gone;

            _queueTitle = new TextView(this) { TextSize = 12 };
            _queueTitle.SetTextColor(new Color(TextSub));
            _queueTitle.SetPadding(Dp(16), Dp(8), Dp(16), Dp(6));
            _queuePanel.AddView(_queueTitle);

            // 用 ListView + Adapter：只为可见行建 View，点击时立刻就能显示。
            // 之前是 ScrollView + 一次性构建全部行（每行 5 个控件），歌一多就卡住主线程，
            // 表现就是「点了播放列表要等一会儿才弹出来」。
            _queueList = new ListView(this);
            _queueList.LayoutParameters = new LinearLayout.LayoutParams(-1, 0, 1f);
            _queueList.SetBackgroundColor(new Color(CardBg));
            _queueList.Divider = new ColorDrawable(new Color(Line));
            _queueList.DividerHeight = Math.Max(1, (int)(Resources.DisplayMetrics.Density * 0.5f));
            _queueList.CacheColorHint = new Color(CardBg);

            // 底部留白：让「当前曲」即使排在后几首也能被滚到面板最上方，
            // 同时不去动队列本身（前面已经播过的歌仍然留在上面，往上滑就能看到）。
            var qSpacer = new View(this);
            qSpacer.LayoutParameters = new Android.Widget.AbsListView.LayoutParams(-1, Dp(176));
            _queueList.AddFooterView(qSpacer, null, false);

            _queueAdapter = new QueueAdapter(this);
            _queueList.Adapter = _queueAdapter;
            _queueList.ItemClick += (s, e) =>
            {
                var q = MusicPlayer.Queue;
                if (e.Position < 0 || e.Position >= q.Count) return;
                MusicPlayer.PlayAt(e.Position);
                RebuildQueuePanel();
            };

            _queuePanel.AddView(_queueList);
            page.AddView(_queuePanel, new LinearLayout.LayoutParams(-1, Dp(230)));

            /* --- 底部：进度条 + 控制条 --- */
            _playerBottom = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _playerBottom.SetBackgroundColor(new Color(CardBg));
            _playerBottom.SetPadding(Dp(18), Dp(8), Dp(18), Dp(8));

            var seekRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            seekRow.SetGravity(GravityFlags.CenterVertical);
            _tCur = new TextView(this) { Text = "00:00", TextSize = 11 };
            _tCur.SetTextColor(new Color(TextSub));
            seekRow.AddView(_tCur);

            _seek = new SeekBar(this);
            _seek.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f)
            { LeftMargin = Dp(8), RightMargin = Dp(8) };
            _seek.Max = 1000;
            _seek.StartTrackingTouch += (s, e) => _seeking = true;
            _seek.StopTrackingTouch += (s, e) =>
            {
                _seeking = false;
                MusicPlayer.SeekTo((int)(_seek.Progress / 1000.0 * MusicPlayer.Duration));
            };
            seekRow.AddView(_seek);

            _tTotal = new TextView(this) { Text = "00:00", TextSize = 11 };
            _tTotal.SetTextColor(new Color(TextSub));
            seekRow.AddView(_tTotal);
            _playerBottom.AddView(seekRow);



            var ctrl = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            ctrl.SetGravity(GravityFlags.Center);
            ctrl.SetPadding(0, Dp(2), 0, Dp(2));

            _btnRepeat = null;    // 旧的两态芯片由模式图标按钮取代
            _btnShuffle = null;

            // [循环模式] [上一曲] [播放] [下一曲] [播放列表] —— 与 iOS 音乐控件一致
            _btnModeIcon = new IconView(this, IconKind.Repeat) { IconColor = Accent, Thickness = 1.8f };
            _btnModeIcon.LayoutParameters = new LinearLayout.LayoutParams(Dp(46), Dp(46));
            _btnModeIcon.Click += (s, e) =>
            {
                MusicPlayer.CycleMode();
                SyncModes();
                L10n.Toast(this, MusicPlayer.ModeName, ToastLength.Short).Show();
            };
            ctrl.AddView(_btnModeIcon);
            ctrl.AddView(Sp(12));

            _btnPrev = IconBtn(IconKind.Prev, MusicPlayer.Prev);
            ctrl.AddView(_btnPrev);
            ctrl.AddView(Sp(8));

            _btnPlay = new IconView(this, IconKind.Play) { IconColor = TextMain, Thickness = 1.6f };
            _btnPlay.LayoutParameters = new LinearLayout.LayoutParams(Dp(64), Dp(64));
            _btnPlay.Click += (s, e) => MusicPlayer.Toggle();
            ctrl.AddView(_btnPlay);
            ctrl.AddView(Sp(8));

            _btnNext = IconBtn(IconKind.Next, () => MusicPlayer.Next());
            ctrl.AddView(_btnNext);
            ctrl.AddView(Sp(12));

            _btnQueueIcon = new IconView(this, IconKind.Queue) { IconColor = TextSub, Thickness = 1.8f };
            _btnQueueIcon.LayoutParameters = new LinearLayout.LayoutParams(Dp(46), Dp(46));
            _btnQueueIcon.Click += (s, e) => ToggleQueuePanel();
            ctrl.AddView(_btnQueueIcon);

            _playerBottom.AddView(ctrl);
            page.AddView(_playerBottom);

            SyncModes();
            OnStateChanged();
            return page;
        }

        /// <summary>显示/隐藏播放页。</summary>
        private void ShowPlayer(bool show)
        {
            if (show && MusicPlayer.Current == null)
            {
                L10n.Toast(this, L10n.T("先播放一首歌吧"), ToastLength.Short).Show();
                return;
            }
            if (_playPage.Visibility == (show ? ViewStates.Visible : ViewStates.Gone)) return;
            _playPage.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _libPage.Visibility = show || _inPlaylists ? ViewStates.Gone : ViewStates.Visible;
            _plPage.Visibility = !show && _inPlaylists ? ViewStates.Visible : ViewStates.Gone;
            if (show)
            {
                // 播放页已在 OnCreate 建好，这里只刷新状态。
                // 之前每次进入都整页重建（封面/歌词/控件全重建），是「点一下卡半天」的主因之一。
                OnSongChanged();
                OnStateChanged();
                OnTick(MusicPlayer.Position);
                SyncModes();
            }
        }

        private void BuildPlayerPageReplace()
        {
            var parent = (ViewGroup)_playPage.Parent;
            int idx = parent.IndexOfChild(_playPage);
            parent.RemoveView(_playPage);
            _playPage = BuildPlayerPage();
            parent.AddView(_playPage, idx);
            _playPage.Visibility = ViewStates.Visible;
            OnSongChanged();
            OnStateChanged();
            OnTick(MusicPlayer.Position);
        }

        public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            if (_playPage.Visibility == ViewStates.Visible)
                BuildPlayerPageReplace();
        }

        private void SyncModes()
        {
            // 模式图标：顺序 / 列表 / 单曲 / 随机，一眼看出当前状态
            if (_btnModeIcon != null)
            {
                switch (MusicPlayer.Mode)
                {
                    case 1: _btnModeIcon.Kind = IconKind.Repeat; _btnModeIcon.IconColor = Accent; break;
                    case 2: _btnModeIcon.Kind = IconKind.RepeatOne; _btnModeIcon.IconColor = Accent; break;
                    case 3: _btnModeIcon.Kind = IconKind.Shuffle; _btnModeIcon.IconColor = Accent; break;
                    default: _btnModeIcon.Kind = IconKind.ChevronRight; _btnModeIcon.IconColor = TextSub; break;
                }
                _btnModeIcon.Invalidate();
            }
            if (_btnQueueIcon != null)
            {
                _btnQueueIcon.IconColor = (_queuePanel != null && _queuePanel.Visibility == ViewStates.Visible)
                    ? Accent : TextSub;
                _btnQueueIcon.Invalidate();
            }
            if (_btnQueue != null)
            {
                int n = MusicPlayer.Queue.Count;
                _btnQueue.Text = n > 0 ? L10n.T("列表 ") + n : L10n.T("列表");
                _btnQueue.SetTextColor(new Color(MusicPlayer.Queue.Count > 0 ? Accent : ChipText));
            }
            if (_miniMode != null)
            {
                _miniMode.Text = MusicPlayer.ModeName.Substring(0, 2);
                _miniMode.SetTextColor(new Color(MusicPlayer.Mode > 0 ? Accent : ChipText));
            }
            SyncFav();
        }

        private void SyncFav()
        {
            if (_btnFav == null) return;
            var s = MusicPlayer.Current;
            bool on = s != null && PlaylistStore.IsFavorite(s.Path);
            _btnFav.Kind = on ? IconKind.HeartFilled : IconKind.Heart;
            _btnFav.IconColor = on ? unchecked((int)0xFFE5484D) : ChipText;
            _btnFav.Invalidate();
        }

        /// <summary>返回键：关闭当前页返回上一页，而不是直接退回服务页。
        /// 播放页 → 回到歌单/音乐库；歌单列表 → 回到音乐库；其余（已在音乐库根页）才退出。
        /// 从阅读页跳进来的情况例外：一次返回就直接回到阅读界面继续读。</summary>
        public override void OnBackPressed()
        {
            if (_fromReader)
            {
                // 播放页里若展开了待播放列表，先收起它，再按一次才回阅读
                if (_queuePanel != null && _queuePanel.Visibility == ViewStates.Visible)
                {
                    ToggleQueuePanel();
                    return;
                }
                Finish();
                return;
            }
            if (_playPage != null && _playPage.Visibility == ViewStates.Visible)
            {
                ShowPlayer(false);
                return;
            }
            if (_inPlaylists)
            {
                ShowPlaylists(false);
                return;
            }
            base.OnBackPressed();
        }

        /* ============================================================
         * 状态同步
         * ============================================================ */

        private void OnStateChanged()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    bool playing = MusicPlayer.IsPlaying;
                    if (_miniToggle != null)
                        _miniToggle.Kind = playing ? IconKind.Pause : IconKind.Play;
                    if (_btnPlay != null)
                        _btnPlay.Kind = playing ? IconKind.Pause : IconKind.Play;
                    _mini.Visibility = MusicPlayer.Current == null ? ViewStates.Gone : ViewStates.Visible;
                    if (_miniDivider != null)
                        _miniDivider.Visibility = _mini.Visibility;
                    // 网络流首次加载时给出「缓冲中…」反馈（本地文件几乎瞬间完成，看不到）
                    var curSong = MusicPlayer.Current;
                    if (_pArtist != null && curSong != null)
                        _pArtist.Text = curSong.ArtistLine + (MusicPlayer.IsBuffering ? L10n.T(" · 缓冲中…") : "");
                    SyncMini();
                    SyncModes();
                }
                catch { }
            });
        }

        private void OnSongChanged()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    var s = MusicPlayer.Current;
                    SyncMini();
                    if (s == null)
                    {
                        _playPage.Visibility = ViewStates.Gone;
                        _libPage.Visibility = _inPlaylists ? ViewStates.Gone : ViewStates.Visible;
                        _plPage.Visibility = _inPlaylists ? ViewStates.Visible : ViewStates.Gone;
                        return;
                    }
                    if (_pTitle != null) _pTitle.Text = s.DisplayTitle;
                    if (_pArtist != null) _pArtist.Text = s.ArtistLine;
                    if (_tTotal != null) _tTotal.Text = Song.FormatMs(MusicPlayer.Duration);
                    LoadCoverInto(_cover, s);
                    _adapter.NotifyDataSetChanged();
                    SyncFav();

                    _lyrics = MusicLibrary.LoadLyrics(s);
                    BuildLyricViews();
                    _lyricIdx = -2;
                    _miniLyricIdx = -2;
                    UpdateMiniLyric(SyncListen.InSync ? SyncListen.DisplayPosition : MusicPlayer.Position);
                    bool hasLyric = _lyrics != null && _lyrics.IsValid;
                    if (_lyricEmpty != null)
                        _lyricEmpty.Visibility = hasLyric ? ViewStates.Gone : ViewStates.Visible;
                    if (_lyricScroll != null)
                        _lyricScroll.Visibility = hasLyric ? ViewStates.Visible : ViewStates.Gone;
                    if (_lyricEmptyTitle != null)
                        _lyricEmptyTitle.Text = s.IsOnline ? L10n.T("歌词加载中…") : L10n.T("无歌词");
                    // 在线歌曲：本地没有歌词就去酷狗取一次（结果落成 .lrc，之后再进直接命中）
                    if (!hasLyric && s.IsOnline) FetchOnlineLyrics(s);
                }
                catch { }
            });
        }

        /// <summary>在线歌曲异步取词：成功则落 .lrc 并刷新歌词区；失败把提示改回「无歌词」。</summary>
        private void FetchOnlineLyrics(Song s)
        {
            if (s == null || !s.IsOnline) return;
            if (!string.IsNullOrEmpty(s.LrcPath) && System.IO.File.Exists(s.LrcPath)) return;

            string key = (s.OnlinePlat ?? "kg") + ":" + (s.OnlineId ?? "");
            lock (_lyricFetching) { if (!_lyricFetching.Add(key)) return; }

            Task.Run(async () =>
            {
                string path = null;
                try { path = await OnlineMusic.FetchLyricsAsync(s); } catch { }
                RunOnUiThread(() =>
                {
                    lock (_lyricFetching) { _lyricFetching.Remove(key); }
                    var cur = MusicPlayer.Current;
                    if (cur == null || !ReferenceEquals(cur, s)) return;   // 已切歌，丢弃

                    if (!string.IsNullOrEmpty(path))
                    {
                        _lyrics = MusicLibrary.LoadLyrics(s);
                        BuildLyricViews();
                        _lyricIdx = -2;
                        _miniLyricIdx = -2;
                        UpdateMiniLyric(SyncListen.InSync ? SyncListen.DisplayPosition : MusicPlayer.Position);
                        bool has = _lyrics != null && _lyrics.IsValid;
                        if (_lyricEmpty != null) _lyricEmpty.Visibility = has ? ViewStates.Gone : ViewStates.Visible;
                        if (_lyricScroll != null) _lyricScroll.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
                        if (_lyricEmptyTitle != null && !has) _lyricEmptyTitle.Text = L10n.T("无歌词");
                    }
                    else if (_lyricEmptyTitle != null)
                    {
                        _lyricEmptyTitle.Text = L10n.T("无歌词");
                    }
                });
            });
        }

        private void OnTick(int pos)
        {
            RunOnUiThread(() =>
            {
                try
                {
                    int dur = MusicPlayer.Duration;
                    // 同步听跟随端：歌词/进度条走「发起端权威时间轴」，
                    // 否则每次 hard seek 校正都会让歌词整段闪跳，看起来永远对不上。
                    int shown = SyncListen.InSync ? SyncListen.DisplayPosition : pos;
                    if (!_seeking && _seek != null && dur > 0)
                        _seek.Progress = (int)(shown / (double)dur * 1000);
                    if (_tCur != null) _tCur.Text = Song.FormatMs(shown);
                    UpdateLyric(shown);
                    UpdateMiniLyric(shown);
                }
                catch { }
            });
        }

        private void SyncMini()
        {
            var s = MusicPlayer.Current;
            if (s == null) return;
            _miniTitle.Text = s.DisplayTitle;
            _miniArtist.Text = s.ArtistLine;
            LoadCoverInto(_miniCover, s, small: true);
            UpdateMiniLyric(MusicPlayer.Position);
        }

        /// <summary>底栏歌词行：跟随播放位置滚动；没有歌词或当前行是空白就隐藏。</summary>
        private void UpdateMiniLyric(int pos)
        {
            if (_miniLyric == null) return;
            if (_lyrics == null || !_lyrics.IsValid)
            {
                if (_miniLyric.Visibility != ViewStates.Gone) _miniLyric.Visibility = ViewStates.Gone;
                _miniLyricIdx = -2;
                return;
            }
            int idx = _lyrics.LineAt(pos + 300);
            if (idx == _miniLyricIdx) return;
            _miniLyricIdx = idx;
            string text = idx >= 0 && idx < _lyrics.Lines.Count ? _lyrics.Lines[idx].Text : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                _miniLyric.Visibility = ViewStates.Gone;
                return;
            }
            _miniLyric.Text = text;
            _miniLyric.Visibility = ViewStates.Visible;
        }

        private void LoadCoverInto(ImageView iv, Song s, bool small = false)
        {
            if (iv == null || s == null) return;
            int px = small ? 128 : 512;

            // 1) 本地文件优先：本地歌曲的内嵌/同名封面，或在线歌曲已经落盘的封面 jpg。
            //    （之前在线分支只看远程内存缓存，封面落盘后也不回头读，导致一直是占位图）
            var local = MusicLibrary.LoadCover(s, px);
            if (local != null && !local.IsRecycled)
            {
                iv.SetImageBitmap(local);
                iv.SetScaleType(ImageView.ScaleType.CenterCrop);
                return;
            }

            // 2) 在线歌曲 / 一起听共享曲目：先用内存里的远程封面，没有就占位图 + 异步拉取
            if (s.IsOnline || !string.IsNullOrEmpty(ArtUrlOf(s)))
            {
                var onlineBitmap = CachedOnlineCover(s, px);
                if (onlineBitmap != null && !onlineBitmap.IsRecycled)
                {
                    iv.SetImageBitmap(onlineBitmap);
                    iv.SetScaleType(ImageView.ScaleType.CenterCrop);
                }
                else
                {
                    iv.SetImageBitmap(PlaceholderBitmap(small));
                    iv.SetScaleType(ImageView.ScaleType.Center);
                    LoadOnlineCover(iv, s);
                }

                // 落盘一次，之后列表 / 底栏 / 播放页 / 通知栏 / 锁屏都直接读本地文件。
                // 共享曲目（CoverUrl）不是平台在线歌曲，没有 plat/id，不能走 FetchCoverAsync。
                if (s.IsOnline && string.IsNullOrEmpty(s.CoverPath))
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var p = await OnlineMusic.FetchCoverAsync(s);
                            if (string.IsNullOrEmpty(p)) return;
                            RunOnUiThread(() =>
                            {
                                try
                                {
                                    if (ReferenceEquals(MusicPlayer.Current, s))
                                    {
                                        LoadCoverInto(_cover, s);
                                        LoadCoverInto(_miniCover, s, true);
                                        // 通知栏大视图按 CoverPath 重建，这里推一次让它换上新封面
                                        MusicPlayer.RefreshNotification();
                                    }
                                    _adapter?.NotifyDataSetChanged();
                                }
                                catch { }
                            });
                        }
                        catch { }
                    });
                }
                return;
            }

            // 3) 本地歌曲没有封面：占位图
            iv.SetImageBitmap(PlaceholderBitmap(small));
            iv.SetScaleType(ImageView.ScaleType.Center);
        }

        /// <summary>带缓存地取歌曲封面；没有封面时返回 null（调用方显示占位图）。</summary>
        private static Bitmap CachedCover(Song s, int px)
        {
            if (s == null || string.IsNullOrEmpty(s.CoverPath)) return null;
            Bitmap b;
            if (_coverCache.TryGetValue(s.Path, out b) && b != null && !b.IsRecycled) return b;
            b = MusicLibrary.LoadCover(s, px);
            if (b != null) _coverCache[s.Path] = b;
            return b;
        }

        /// <summary>共享曲目（一起听/同步听）的封面走 CoverUrl，在线曲目走 OnlineCover。</summary>
        private static string ArtUrlOf(Song s)
        {
            if (s == null) return "";
            return string.IsNullOrEmpty(s.CoverUrl) ? (s.OnlineCover ?? "") : s.CoverUrl;
        }

        /// <summary>在线/共享封面缓存（按远程 URL 缓存，避免列表滑动反复下载）。</summary>
        private static Bitmap CachedOnlineCover(Song s, int px)
        {
            var url = ArtUrlOf(s);
            if (s == null || string.IsNullOrEmpty(url)) return null;
            Bitmap b;
            lock (_onlineCoverCache)
            {
                if (_onlineCoverCache.TryGetValue(url, out b) && b != null && !b.IsRecycled) return b;
            }
            return null;
        }

        /// <summary>异步拉取在线/共享封面并回填到 ImageView（带回收/错位保护）。</summary>
        private static void LoadOnlineCover(ImageView iv, Song s)
        {
            var url = ArtUrlOf(s);
            if (iv == null || s == null || string.IsNullOrEmpty(url)) return;
            iv.Tag = url;
            Task.Run(() =>
            {
                try
                {
                    var conn = (Java.Net.HttpURLConnection)new Java.Net.URL(url).OpenConnection();
                    conn.ConnectTimeout = 10000;
                    conn.ReadTimeout = 10000;
                    conn.DoInput = true;
                    using (var src = conn.InputStream)
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[8192];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        // 在线封面尺寸不可控：先量尺寸再按 1024 上限采样解码，避免大图 OOM
                        ms.Position = 0;
                        var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
                        BitmapFactory.DecodeStream(ms, null, probe);
                        int sample = 1;
                        while (probe.OutWidth > 0 && probe.OutWidth / (sample * 2) >= 1024) sample *= 2;
                        ms.Position = 0;
                        var bmp = BitmapFactory.DecodeStream(ms, null, new BitmapFactory.Options { InSampleSize = sample });
                        if (bmp == null) return;
                        lock (_onlineCoverCache) _onlineCoverCache[url] = bmp;
                        try { AhoChatA.Music.SongArt.Put(url, bmp); } catch { }   // 通知栏/迷你条共用
                        var captured = iv;
                        var key = url;
                        captured.Post(() =>
                        {
                            try
                            {
                                var tagStr = captured.Tag != null ? captured.Tag.ToString() : null;
                                if (tagStr == key && !bmp.IsRecycled)
                                {
                                    captured.SetImageBitmap(bmp);
                                    captured.SetScaleType(ImageView.ScaleType.CenterCrop);
                                }
                            }
                            catch { }
                        });
                    }
                }
                catch { }
            });
        }

        /// <summary>取歌单封面用的「最新添加且含封面」的歌曲（Paths 末尾优先）。</summary>
        private static Song PlaylistCoverSong(string id)
        {
            var songs = PlaylistStore.SongsOf(id);
            for (int k = songs.Count - 1; k >= 0; k--)
            {
                if (!songs[k].IsVideo && !string.IsNullOrEmpty(songs[k].CoverPath)) return songs[k];
            }
            return null;
        }

        /// <summary>无封面占位图：浅灰圆角方块 + 柔光圆 + 居中双音符。
        /// public 是为了让聊天迷你条、通知栏、音乐卡片共用同一张图，别再显示程序图标。</summary>
        public static Bitmap PlaceholderBitmap(bool small)
        {
            var cached = small ? _placeholderSmall : _placeholderBig;
            if (cached != null && !cached.IsRecycled) return cached;

            int px = small ? 128 : 512;
            var bmp = Bitmap.CreateBitmap(px, px, Bitmap.Config.Argb8888);
            try
            {
                var c = new Canvas(bmp);
                var bg = new Paint(PaintFlags.AntiAlias);
                bg.Color = new Color(unchecked((int)0xFFE9EDF4));
                c.DrawRoundRect(new RectF(0, 0, px, px), px * 0.10f, px * 0.10f, bg);

                float cx = px / 2f, cy = px / 2f;
                var halo = new Paint(PaintFlags.AntiAlias);
                halo.Color = new Color(unchecked((int)0xFFDCE4F0));
                c.DrawCircle(cx, cy, px * 0.24f, halo);

                var stroke = new Paint(PaintFlags.AntiAlias);
                stroke.Color = new Color(unchecked((int)0xFF93A2B9));
                stroke.SetStyle(Paint.Style.Stroke);
                stroke.StrokeWidth = 1.9f;
                stroke.StrokeCap = Paint.Cap.Round;
                stroke.StrokeJoin = Paint.Join.Round;
                var fill = new Paint(stroke);
                fill.SetStyle(Paint.Style.Fill);

                // 24 单位视口（与 IconView.DrawMusic 相同的双音符路径）
                float sc = px * 0.30f / 24f;
                c.Save();
                c.Translate(cx - 12f * sc, cy - 12f * sc);
                c.Scale(sc, sc);
                var path = new Android.Graphics.Path();
                path.MoveTo(9.4f, 17f); path.LineTo(9.4f, 6.6f);
                path.MoveTo(16.4f, 15f); path.LineTo(16.4f, 4.8f);
                path.MoveTo(9.4f, 6.6f); path.LineTo(16.4f, 4.8f);
                c.DrawPath(path, stroke);
                c.DrawOval(5.8f, 15.2f, 9.4f, 18.4f, fill);
                c.DrawOval(12.8f, 13.2f, 16.4f, 16.4f, fill);
                c.Restore();

                bg.Dispose(); halo.Dispose(); stroke.Dispose();
                fill.Dispose(); path.Dispose();
            }
            catch { }

            if (small) _placeholderSmall = bmp; else _placeholderBig = bmp;
            return bmp;
        }

        /// <summary>始终量成正方形的封面容器（取可用宽高的较小值）。</summary>
        private sealed class SquareFrame : FrameLayout
        {
            public SquareFrame(Context ctx) : base(ctx) { }

            protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
            {
                int w = View.MeasureSpec.GetSize(widthMeasureSpec);
                int h = View.MeasureSpec.GetSize(heightMeasureSpec);
                int size = Math.Min(w, h);
                if (size <= 0)
                {
                    base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
                    return;
                }
                int spec = View.MeasureSpec.MakeMeasureSpec(size, MeasureSpecMode.Exactly);
                base.OnMeasure(spec, spec);
            }
        }

        /// <summary>圆角裁剪，让正方形封面的四角也是圆的。</summary>
        private sealed class RoundOutlineProvider : ViewOutlineProvider
        {
            private readonly int _r;
            public RoundOutlineProvider(int r) { _r = r; }
            public override void GetOutline(View view, Outline outline)
            {
                outline.SetRoundRect(0, 0, view.Width, view.Height, _r);
            }
        }

        /* ---------------- 歌词 ---------------- */

        private void BuildLyricViews()
        {
            _lyricPanel.RemoveAllViews();
            _lyricViews.Clear();
            if (_lyrics == null || !_lyrics.IsValid)
            {
                if (_lyricEmpty != null) _lyricEmpty.Visibility = ViewStates.Visible;
                if (_lyricScroll != null) _lyricScroll.Visibility = ViewStates.Gone;
                return;
            }
            for (int i = 0; i < _lyrics.Lines.Count; i++)
            {
                var line = _lyrics.Lines[i];
                // 双语歌词：同时间戳组内的第二行及以后为译文，字号更小、与上行贴紧
                bool trans = _lyrics.IsGrouped(i) && _lyrics.GroupStart(i) != i;
                var t = new TextView(this)
                {
                    Text = line.Text.Length == 0 ? "…" : line.Text,
                    TextSize = trans ? 13 : (_lyrics.Lines.Count <= 8 ? 17 : 15),
                };
                t.SetTextColor(new Color(TextSub));
                t.SetSingleLine(false);
                t.SetPadding(0, Dp(trans ? 2 : 8), 0, Dp(8));
                _lyricPanel.AddView(t);
                _lyricViews.Add(t);
            }
        }

        private void UpdateLyric(int pos)
        {
            if (_lyrics == null || !_lyrics.IsValid || _lyricViews.Count == 0) return;
            if (_playPage.Visibility != ViewStates.Visible) return;
            int idx = _lyrics.LineAt(pos + 300);
            if (idx == _lyricIdx) return;
            _lyricIdx = idx;

            // 双语歌词：同时间戳整组一起高亮
            int gs = idx >= 0 ? _lyrics.GroupStart(idx) : -1;
            int ge = idx >= 0 ? _lyrics.GroupEnd(idx) : -1;

            for (int i = 0; i < _lyricViews.Count; i++)
            {
                var v = _lyricViews[i];
                bool trans = _lyrics.IsGrouped(i) && _lyrics.GroupStart(i) != i;
                int baseSize = trans ? 13 : (_lyrics.Lines.Count <= 8 ? 17 : 15);
                if (i >= gs && i <= ge && gs >= 0)
                {
                    v.SetTextColor(new Color(Accent));
                    v.TextSize = trans ? 15 : 17;
                }
                else
                {
                    v.SetTextColor(new Color(TextSub));
                    v.TextSize = baseSize;
                }
            }
            if (idx >= 0)
            {
                // 布局完成后滚动到当前组的中部（下一帧）
                int midIdx = gs >= 0 ? (gs + ge) / 2 : idx;
                _lyricScroll.Post(() =>
                {
                    try
                    {
                        if (midIdx < 0 || midIdx >= _lyricViews.Count) return;
                        var child = _lyricViews[midIdx];
                        int target = child.Top + child.Height / 2 - _lyricScroll.Height / 2;
                        _lyricScroll.SmoothScrollTo(0, Math.Max(0, target));
                    }
                    catch { }
                });
            }
        }

        /* ---------------- 播放列表 ---------------- */

        /// <summary>队列变化：刷新播放页内的队列面板与「列表 N」角标。</summary>
        private void OnQueueChanged()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    if (_queuePanel != null && _queuePanel.Visibility == ViewStates.Visible)
                        RebuildQueuePanel();
                    SyncModes();
                }
                catch { }
            });
        }

        /// <summary>
        /// 展开 / 收起播放页内的队列面板。
        /// 点下去要「立刻」看到东西，所以顺序是：先灌数据（只渲染可见行）→ 立刻显示 → 下一帧再把当前曲顶上去。
        /// </summary>
        private void ToggleQueuePanel()
        {
            if (_queuePanel == null) return;
            bool show = _queuePanel.Visibility != ViewStates.Visible;
            if (show)
            {
                RebuildQueuePanel();                                     // 数据先行（快）
                _queuePanel.Visibility = ViewStates.Visible;             // 立刻可见
                if (_queueList != null)
                    _queueList.Post(() => { try { ScrollQueueToCurrent(); } catch { } });
            }
            else
            {
                _queuePanel.Visibility = ViewStates.Gone;
            }
            if (_btnQueueIcon != null)
            {
                _btnQueueIcon.IconColor = show ? Accent : TextSub;
                _btnQueueIcon.Invalidate();
            }
        }

        /// <summary>把「当前播放的这首歌」滚到面板最上方（队列顺序本身不变）。</summary>
        private void ScrollQueueToCurrent()
        {
            if (_queueList == null || _queueAdapter == null) return;
            var q = MusicPlayer.Queue;
            var cur = MusicPlayer.Current;
            int idx = cur == null ? 0
                : q.FindIndex(x => string.Equals(x.Path, cur.Path, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            // SetSelectionFromTop：强制把这一项贴到面板最上方（SetSelection 只是「保证可见」，
            // 在项较少或已经在可见区域内时不会滚动）。
            try { _queueList.SetSelectionFromTop(idx, 0); } catch { }
        }

        /// <summary>刷新队列面板数据（切歌 / 调序 / 移出都会走这里）。</summary>
        private void RebuildQueuePanel()
        {
            if (_queueList == null || _queueAdapter == null) return;
            var q = MusicPlayer.Queue;
            _queueAdapter.Replace(q, MusicPlayer.Current?.Path ?? "");
            if (_queueTitle != null)
                _queueTitle.Text = q.Count == 0 ? L10n.T("播放列表为空")
                    : L10n.T("播放列表 · ") + q.Count + L10n.T(" 首（当前曲在顶部，向上滑可看前面的歌）");
            if (_queuePanel != null && _queuePanel.Visibility == ViewStates.Visible)
                _queueList.Post(() => { try { ScrollQueueToCurrent(); } catch { } });
        }

        /// <summary>播放页内嵌队列的适配器：只为可见行建 View，行内提供 ↑ ↓ × 。</summary>
        private class QueueAdapter : BaseAdapter
        {
            private readonly MusicActivity _a;
            private List<Song> _items = new List<Song>();
            private string _curPath = "";

            public QueueAdapter(MusicActivity a) { _a = a; }

            public void Replace(List<Song> items, string curPath)
            {
                _items = items ?? new List<Song>();
                _curPath = curPath ?? "";
                NotifyDataSetChanged();
            }

            public override int Count => _items.Count;
            public override Java.Lang.Object GetItem(int position) => null;
            public override long GetItemId(int position) => position;

            private class Holder : Java.Lang.Object
            {
                public TextView Idx, Title, Sub;
                public View Row;
                public int Pos;
            }

            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                Holder h;
                View v = convertView;
                if (v == null || !(v.Tag is Holder))
                {
                    var r = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
                    r.SetGravity(GravityFlags.CenterVertical);
                    r.SetPadding(_a.Dp(14), _a.Dp(7), _a.Dp(4), _a.Dp(7));

                    var idx = new TextView(_a) { TextSize = 11, Gravity = GravityFlags.Center };
                    idx.LayoutParameters = new LinearLayout.LayoutParams(_a.Dp(24), -2);
                    r.AddView(idx);

                    var col = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                    col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    var t = new TextView(_a) { TextSize = 14 };
                    t.SetSingleLine(true);
                    t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    var sub = new TextView(_a) { TextSize = 11 };
                    sub.SetSingleLine(true);
                    sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    col.AddView(t);
                    col.AddView(sub);
                    r.AddView(col);

                    h = new Holder { Idx = idx, Title = t, Sub = sub, Row = r };
                    // 闭包捕获的是 Holder 对象本身，行被复用时 Pos 会跟着更新，
                    // 所以这几个按钮永远作用于「当前这一行」。
                    r.AddView(_a.SmallIconChip(IconKind.Up, () => { MusicPlayer.MoveInQueue(h.Pos, -1); _a.RebuildQueuePanel(); }));
                    r.AddView(_a.SmallIconChip(IconKind.Down, () => { MusicPlayer.MoveInQueue(h.Pos, 1); _a.RebuildQueuePanel(); }));
                    r.AddView(_a.SmallIconChip(IconKind.Close, () => { MusicPlayer.RemoveAt(h.Pos); _a.RebuildQueuePanel(); }));

                    r.Tag = h;
                    v = r;
                }
                else h = (Holder)v.Tag;

                h.Pos = position;
                var song = _items[position];
                bool now = !string.IsNullOrEmpty(_curPath) &&
                           string.Equals(_curPath, song.Path, StringComparison.OrdinalIgnoreCase);

                h.Idx.Text = (position + 1).ToString();
                h.Idx.SetTextColor(new Color(now ? Accent : TextSub));
                h.Title.Text = song.DisplayTitle;
                h.Title.SetTypeface(null, now ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                h.Title.SetTextColor(new Color(now ? Accent : TextMain));
                h.Sub.Text = song.ArtistLine + (song.IsOnline ? L10n.T(" · 在线") : "");
                h.Sub.SetTextColor(new Color(now ? Accent : TextSub));
                h.Row.SetBackgroundColor(new Color(now ? unchecked((int)0x14007AFF) : CardBg));
                return v;
            }
        }

        private TextView SmallChip(string text, Action click)
        {
            var t = new TextView(this) { Text = text, TextSize = 13, Gravity = GravityFlags.Center };
            t.SetTextColor(new Color(ChipText));
            t.Background = RoundBg(ChipBg, 7);
            t.LayoutParameters = new LinearLayout.LayoutParams(Dp(30), Dp(30)) { RightMargin = Dp(4) };
            t.Click += (s, e) => click();
            return t;
        }

        /// <summary>矢量图标小按钮（↑ ↓ × 这类操作用，避免字体图标在各系统不一致）。</summary>
        private View SmallIconChip(IconKind kind, Action click, int size = 30)
        {
            var box = new FrameLayout(this);
            box.Background = RoundBg(ChipBg, 7);
            box.LayoutParameters = new LinearLayout.LayoutParams(Dp(size), Dp(size)) { RightMargin = Dp(4) };
            var ic = new IconView(this, kind) { IconColor = ChipText, Thickness = 1.8f };
            ic.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
            ic.SetPadding(Dp(7), Dp(7), Dp(7), Dp(7));
            box.AddView(ic);
            box.Click += (s, e) => click();
            return box;
        }

        private void ShowQueueDialog()
        {
            if (MusicPlayer.Queue.Count == 0)
            {
                L10n.Toast(this, L10n.T("播放列表为空"), ToastLength.Short).Show();
                return;
            }
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(8), Dp(12), Dp(2));
            var tip = new TextView(this)
            {
                Text = L10n.T("点击歌曲切换；右侧 × 把该项移出播放列表（不会删除文件）"),
                TextSize = 11
            };
            tip.SetTextColor(new Color(TextSub));
            box.AddView(tip);

            var scroll = new ScrollView(this);
            scroll.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(380));
            var rows = new LinearLayout(this) { Orientation = Orientation.Vertical };
            scroll.AddView(rows);
            box.AddView(scroll);

            var dlg = L10n.Dialog(this)
                .SetTitle(L10n.T("播放列表"))
                .SetView(box)
                .SetNegativeButton(L10n.T("关闭"), (s, e) => { })
                .Create();

            void Rebuild()
            {
                rows.RemoveAllViews();
                var q = MusicPlayer.Queue.ToList();
                var cur = MusicPlayer.Current;
                dlg.SetTitle(L10n.T("播放列表（") + q.Count + "）");
                for (int k = 0; k < q.Count; k++)
                {
                    var song = q[k];
                    int qi = k;
                    var r = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    r.SetGravity(GravityFlags.CenterVertical);
                    r.SetPadding(0, Dp(10), 0, Dp(10));

                    var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                    col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    bool now = cur != null && cur.Path == song.Path;
                    var t = new TextView(this)
                    { Text = (now ? "• " : "") + (qi + 1) + ". " + song.DisplayTitle, TextSize = 14 };
                    t.SetTextColor(new Color(now ? Accent : TextMain));
                    t.SetSingleLine(true);
                    t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    var sub = new TextView(this) { Text = song.ArtistLine, TextSize = 11 };
                    sub.SetTextColor(new Color(TextSub));
                    sub.SetSingleLine(true);
                    col.AddView(t); col.AddView(sub);
                    r.AddView(col);

                    var del = SmallIconChip(IconKind.Close, () =>
                    {
                        MusicPlayer.RemoveAt(qi);
                        if (MusicPlayer.Queue.Count == 0) { dlg.Dismiss(); return; }
                        Rebuild();
                    }, 36);
                    r.AddView(SmallIconChip(IconKind.Up, () => { MusicPlayer.MoveInQueue(qi, -1); Rebuild(); }));
                    r.AddView(SmallIconChip(IconKind.Down, () => { MusicPlayer.MoveInQueue(qi, 1); Rebuild(); }));
                    r.AddView(del);
                    r.Click += (s2, e2) => { MusicPlayer.PlayAt(qi); dlg.Dismiss(); };
                    rows.AddView(r);
                    rows.AddView(new View(this)
                    {
                        LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(1)),
                        Background = new ColorDrawable(new Color(Line))
                    });
                }
            }
            Rebuild();
            dlg.Show();
        }

        /* ============================================================
         * 通用 UI 小件
         * ============================================================ */

        private View Sp(int w) => new View(this)
        { LayoutParameters = new LinearLayout.LayoutParams(Dp(w), 1) };

        private TextView ChipBtn(string text, Action click)
        {
            var t = new TextView(this) { Text = text, TextSize = 13 };
            t.SetTextColor(new Color(Accent));
            t.Gravity = GravityFlags.Center;
            t.Background = RoundBg(IosFill, 14);      // iOS 胶囊按钮
            t.SetPadding(Dp(14), Dp(7), Dp(14), Dp(7));
            t.Click += (s, e) => click();
            return t;
        }

        /// <summary>
        /// iOS 风格动作菜单（Action Sheet）：底部弹出、圆角卡片、图标+文字行、
        /// 破坏性操作标红、独立「取消」按钮。替代系统原生 PopupMenu。
        /// </summary>
        private void ShowActionSheet(string title,
            (IconKind Icon, string Label, bool Destructive, Action Run)[] items, Action after = null)
        {
            try
            {
                Dialog dlg = null;
                var sheet = new LinearLayout(this) { Orientation = Orientation.Vertical };
                sheet.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));

                var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
                card.Background = RoundBg(White, 14);
                card.ClipToOutline = true;

                if (!string.IsNullOrEmpty(title))
                {
                    var head = new TextView(this) { Text = title, TextSize = 12, Gravity = GravityFlags.Center };
                    head.SetTextColor(new Color(TextSub));
                    head.SetPadding(Dp(12), Dp(10), Dp(12), Dp(8));
                    card.AddView(head, new LinearLayout.LayoutParams(-1, -2));
                }

                for (int i = 0; i < items.Length; i++)
                {
                    var it = items[i];
                    var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    row.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
                    row.Click += (s, e) =>
                    {
                        try { dlg?.Dismiss(); } catch { }
                        try { it.Run(); } catch { }
                        try { after?.Invoke(); } catch { }
                    };
                    var ic = new IconView(this, it.Icon)
                    {
                        IconColor = unchecked((int)(it.Destructive ? 0xFFFF3B30 : 0xFF007AFF))
                    };
                    row.AddView(ic, new LinearLayout.LayoutParams(Dp(22), Dp(22)) { RightMargin = Dp(14) });
                    var lab = new TextView(this) { Text = it.Label, TextSize = 15 };
                    lab.SetTextColor(new Color(it.Destructive ? IosRed : Accent));
                    lab.SetSingleLine(true);
                    lab.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    lab.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    row.AddView(lab);
                    card.AddView(row);
                    if (i < items.Length - 1) card.AddView(HLine(Dp(50)));
                }
                sheet.AddView(card, new LinearLayout.LayoutParams(-1, -2));

                var cancel = new TextView(this)
                { Text = L10n.T("取消"), TextSize = 15, Gravity = GravityFlags.Center };
                cancel.SetTextColor(new Color(Accent));
                cancel.SetTypeface(null, TypefaceStyle.Bold);
                cancel.Background = RoundBg(White, 14);
                cancel.SetPadding(Dp(12), Dp(13), Dp(12), Dp(13));
                cancel.Click += (s, e) => { try { dlg?.Dismiss(); } catch { } };
                sheet.AddView(cancel, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });

                dlg = new Dialog(this);
                dlg.RequestWindowFeature((int)WindowFeatures.NoTitle);
                dlg.SetCanceledOnTouchOutside(true);
                dlg.SetContentView(sheet);
                dlg.Show();
                var w = dlg.Window;
                if (w != null)
                {
                    w.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
                    w.SetGravity(GravityFlags.Bottom | GravityFlags.CenterHorizontal);
                    w.SetDimAmount(0.32f);
                    w.SetLayout(System.Math.Min(Dp(420), Resources.DisplayMetrics.WidthPixels - Dp(16)), -2);
                }
            }
            catch { }
        }

        /// <summary>iOS 风格的 1px 分隔线。</summary>
        private View HLine(int leftInsetDp = 0)
        {
            var v = new View(this);
            v.SetBackgroundColor(new Color(Line));
            int h = Math.Max(1, (int)(Resources.DisplayMetrics.Density * 0.5f));
            var lp = new LinearLayout.LayoutParams(-1, h);
            lp.LeftMargin = Dp(leftInsetDp);
            v.LayoutParameters = lp;
            return v;
        }

        private LinearLayout IconBtn(IconKind kind, Action click)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.Center);
            row.Click += (s, e) => click();
            row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
            var ic = new IconView(this, kind) { IconColor = TextMain };
            row.AddView(ic, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
            return row;
        }

        private int Dp(int v) => (int)(v * Resources.DisplayMetrics.Density);

        private Drawable RoundBg(int color, int radiusDp)
        {
            var d = new GradientDrawable();
            d.SetColor(color);
            d.SetCornerRadius(Dp(radiusDp));
            return d;
        }

        /* ============================================================
         * 图片网格：文件夹卡片 / 图片卡片共用 3 列 GridView
         * ============================================================ */

        private sealed class PhotoItem
        {
            public Photo Photo;
            public PhotoFolder Folder;
        }

        private sealed class PhotoGridAdapter : BaseAdapter<PhotoItem>
        {
            private readonly MusicActivity _a;
            private List<PhotoItem> _items = new List<PhotoItem>();
            private readonly Dictionary<string, Bitmap> _bmps = new Dictionary<string, Bitmap>();

            public PhotoGridAdapter(MusicActivity a) { _a = a; }

            public void Replace(List<PhotoItem> items)
            {
                _items = items ?? new List<PhotoItem>();
                _bmps.Clear();          // 旧位图交给 GC，避免 Recycle 后仍被 GridView 引用
                NotifyDataSetChanged();
            }

            public override int Count => _items.Count;
            public override PhotoItem this[int i] => _items[i];
            public override long GetItemId(int i) => i;

            private sealed class Holder : Java.Lang.Object
            {
                public ImageView Img;
                public TextView Title, Sub;
            }

            public override View GetView(int i, View cv, ViewGroup parent)
            {
                Holder h;
                View v = cv;
                if (v == null)
                {
                    int cell = Math.Max(_a.Dp(84),
                        _a.Resources.DisplayMetrics.WidthPixels / 3 - _a.Dp(12));

                    var col = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                    col.SetPadding(_a.Dp(2), _a.Dp(2), _a.Dp(2), _a.Dp(4));
                    col.Background = _a.RoundBg(CardBg, 8);

                    var frame = new FrameLayout(_a);
                    frame.LayoutParameters = new LinearLayout.LayoutParams(-1, cell);
                    var img = new ImageView(_a);
                    img.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
                    img.SetScaleType(ImageView.ScaleType.CenterCrop);
                    img.SetBackgroundColor(new Color(ChipBg));
                    frame.AddView(img);
                    col.AddView(frame);

                    var title = new TextView(_a) { TextSize = 11 };
                    title.SetTextColor(new Color(TextMain));
                    title.SetSingleLine(true);
                    title.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
                    title.SetPadding(_a.Dp(6), _a.Dp(4), _a.Dp(6), 0);
                    col.AddView(title);

                    var sub = new TextView(_a) { TextSize = 9 };
                    sub.SetTextColor(new Color(TextSub));
                    sub.SetSingleLine(true);
                    sub.SetPadding(_a.Dp(6), 0, _a.Dp(6), _a.Dp(5));
                    col.AddView(sub);

                    v = col;
                    h = new Holder { Img = img, Title = title, Sub = sub };
                    v.Tag = h;
                }
                else h = (Holder)v.Tag;

                var it = _items[i];
                bool folder = it.Folder != null;
                string key = folder ? it.Folder.CoverPath : it.Photo?.Path ?? "";
                h.Title.Text = folder ? it.Folder.Name : it.Photo?.Name ?? "";
                h.Sub.Text = folder ? it.Folder.Count + L10n.T(" 张")
                                    : it.Photo?.ModifiedText ?? "";

                Bitmap bmp = null;
                if (!string.IsNullOrEmpty(key) && !_bmps.TryGetValue(key, out bmp))
                {
                    var target = folder ? PhotoLibrary.Find(it.Folder.CoverPath) : it.Photo;
                    bmp = PhotoLibrary.LoadThumb(target, 240);
                    _bmps[key] = bmp;
                }
                h.Img.SetImageBitmap(bmp);
                return v;
            }
        }

        /* ============================================================
         * 视频海报卡片网格（参考 Jellyfin 媒体库卡片）
         * ============================================================ */

        private class VideoGridAdapter : BaseAdapter<Song>
        {
            private readonly MusicActivity _a;
            private List<Song> _items = new List<Song>();
            private readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

            public VideoGridAdapter(MusicActivity a) { _a = a; }

            public void Replace(List<Song> items)
            {
                _items = items ?? new List<Song>();
                NotifyDataSetChanged();
            }

            public override int Count => _items.Count;
            public override Song this[int i] => _items[i];
            public override long GetItemId(int i) => i;

            private class Holder : Java.Lang.Object
            {
                public ImageView Cover;
                public TextView Title, Sub, Dur;
                public View More;
                public Song Song;
            }

            public override View GetView(int i, View cv, ViewGroup parent)
            {
                var s = _items[i];
                LinearLayout card;
                Holder h;
                if (cv is LinearLayout l && l.Tag is Holder old)
                {
                    card = l; h = old;
                }
                else
                {
                    card = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                    card.SetBackgroundColor(new Color(CardBg));
                    card.Background = _a.RoundBg(CardBg, 12);
                    card.SetPadding(_a.Dp(8), _a.Dp(8), _a.Dp(8), _a.Dp(8));

                    var poster = new FrameLayout(_a);
                    poster.LayoutParameters = new LinearLayout.LayoutParams(-1, _a.Dp(112));
                    var iv = new ImageView(_a);
                    iv.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
                    iv.SetScaleType(ImageView.ScaleType.CenterCrop);
                    iv.SetBackgroundColor(new Color(ChipBg));
                    poster.AddView(iv);

                    var dur = new TextView(_a) { TextSize = 11 };
                    dur.SetTextColor(new Color(White));
                    dur.Background = _a.RoundBg(unchecked((int)0x99000000), 6);
                    dur.SetPadding(_a.Dp(5), _a.Dp(2), _a.Dp(5), _a.Dp(2));
                    poster.AddView(dur, new FrameLayout.LayoutParams(-2, -2,
                        GravityFlags.Bottom | GravityFlags.Right) { RightMargin = _a.Dp(6), BottomMargin = _a.Dp(6) });
                    card.AddView(poster);

                    var title = new TextView(_a) { TextSize = 13 };
                    title.SetTextColor(new Color(TextMain));
                    title.SetSingleLine(true);
                    title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    title.SetPadding(_a.Dp(2), _a.Dp(7), 0, 0);
                    card.AddView(title);

                    var bottom = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
                    bottom.SetGravity(GravityFlags.CenterVertical);
                    var sub = new TextView(_a) { TextSize = 11 };
                    sub.SetTextColor(new Color(TextSub));
                    sub.SetSingleLine(true);
                    sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    sub.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f) { LeftMargin = _a.Dp(2) };
                    bottom.AddView(sub);
                    var more = new IconView(_a, IconKind.MoreVertical) { IconColor = TextSub, Thickness = 1.7f };
                    more.SetPadding(_a.Dp(8), _a.Dp(4), _a.Dp(2), _a.Dp(4));
                    more.LayoutParameters = new LinearLayout.LayoutParams(_a.Dp(30), _a.Dp(30));
                    bottom.AddView(more);
                    card.AddView(bottom);

                    h = new Holder { Cover = iv, Title = title, Sub = sub, Dur = dur, More = more };
                    card.Tag = h;
                    more.Click += (sd, e) =>
                    {
                        if (h.Song == null) return;
                        _a.ShowSongMenu((View)sd, h.Song, () => _a.ReloadList());
                    };
                }

                h.Song = s;
                h.Title.Text = s.DisplayTitle;
                h.Sub.Text = (s.Size / (1024 * 1024)) + " MB";
                h.Dur.Text = Song.FormatMs(s.DurationMs);

                Bitmap bmp;
                if (!_cache.TryGetValue(s.Path, out bmp))
                {
                    bmp = MusicLibrary.VideoThumb(s, 360);
                    _cache[s.Path] = bmp;
                }
                if (bmp != null && !bmp.IsRecycled)
                {
                    h.Cover.SetImageBitmap(bmp);
                    h.Cover.SetScaleType(ImageView.ScaleType.CenterCrop);
                }
                else
                {
                    h.Cover.SetImageBitmap(PlaceholderBitmap(true));
                    h.Cover.SetScaleType(ImageView.ScaleType.Center);
                }
                return card;
            }
        }

        /* ============================================================
         * 列表适配器
         * ============================================================ */

        private class SongAdapter : BaseAdapter<Song>
        {
            private readonly MusicActivity _a;
            private List<Song> _items = new List<Song>();

            public SongAdapter(MusicActivity a) { _a = a; }

            public void Replace(List<Song> items)
            {
                _items = items ?? new List<Song>();
                NotifyDataSetChanged();
            }

            public override int Count => _items.Count;
            public override Song this[int i] => _items[i];
            public override long GetItemId(int i) => i;

            /// <summary>复用行时把当前歌曲放进 Holder，三点菜单只绑定一次事件。</summary>
            private class Holder : Java.Lang.Object
            {
                public TextView Title, Sub, Dur;
                public ImageView Cover;
                public Song Song;
            }

            public override View GetView(int i, View cv, ViewGroup parent)
            {
                var s = _items[i];
                if (s.IsDivider)
                {
                    var header = new TextView(_a) { Text = s.Title, TextSize = 12 };
                    header.SetTextColor(new Color(Accent));
                    header.SetTypeface(null, TypefaceStyle.Bold);
                    header.SetPadding(_a.Dp(18), _a.Dp(12), _a.Dp(18), _a.Dp(4));
                    return header;
                }
                LinearLayout row;
                Holder h;

                if (cv is LinearLayout l && l.Tag is Holder old)
                {
                    row = l;
                    h = old;
                }
                else
                {
                    row = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
                    row.SetGravity(GravityFlags.CenterVertical);
                    // 右侧预留 A-Z 索引条的宽度（索引条 26dp + 右外边距 2dp = 28dp），
                    // 再加 6dp 空隙：让时长与「三个点」详情菜单整体左移，避免被索引条遮挡、
                    // 且菜单的点击区域不与索引条的触摸拦截区重叠。
                    row.SetPadding(_a.Dp(16), _a.Dp(10), _a.Dp(34), _a.Dp(10));
                    row.SetBackgroundColor(new Color(CardBg));

                    // 左侧封面缩略图（无封面时回落音符占位图）
                    var coverBox = new FrameLayout(_a);
                    coverBox.LayoutParameters = new LinearLayout.LayoutParams(_a.Dp(48), _a.Dp(48))
                    { RightMargin = _a.Dp(12) };
                    var cov = new ImageView(_a);
                    cov.LayoutParameters = new FrameLayout.LayoutParams(-1, -1);
                    cov.SetScaleType(ImageView.ScaleType.CenterCrop);
                    cov.Background = _a.RoundBg(IosFill, 10);
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                    {
                        cov.OutlineProvider = new RoundOutlineProvider(_a.Dp(10));
                        cov.ClipToOutline = true;      // 封面按 iOS 圆角裁切，不再是直角方块
                    }
                    coverBox.AddView(cov);
                    row.AddView(coverBox);

                    var col = new LinearLayout(_a) { Orientation = Orientation.Vertical };
                    col.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
                    var title = new TextView(_a) { TextSize = 16 };
                    title.SetTextColor(new Color(TextMain));
                    title.SetSingleLine(true);
                    title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    var sub = new TextView(_a) { TextSize = 13 };
                    sub.SetTextColor(new Color(TextSub));
                    sub.SetSingleLine(true);
                    sub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    sub.SetPadding(0, _a.Dp(2), 0, 0);
                    col.AddView(title);
                    col.AddView(sub);
                    row.AddView(col);

                    var dur = new TextView(_a) { TextSize = 11 };
                    dur.SetTextColor(new Color(TextSub));
                    dur.SetPadding(_a.Dp(6), 0, 0, 0);
                    row.AddView(dur);

                    var more = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
                    more.SetGravity(GravityFlags.Center);
                    more.SetPadding(_a.Dp(10), _a.Dp(10), _a.Dp(4), _a.Dp(10));
                    more.AddView(new IconView(_a, IconKind.MoreVertical) { IconColor = TextSub },
                        new LinearLayout.LayoutParams(_a.Dp(20), _a.Dp(20)));
                    row.AddView(more);

                    h = new Holder { Title = title, Sub = sub, Dur = dur, Cover = cov };
                    row.Tag = h;
                    more.Click += (sd, e) =>
                    {
                        if (h.Song == null) return;
                        _a.ShowSongMenu((View)sd, h.Song, () => _a.ReloadList());
                    };
                }

                h.Song = s;
                bool now = MusicPlayer.Current != null && MusicPlayer.Current.Path == s.Path;
                // iOS 风格：正在播放不做字符前缀，用强调色 + 加粗表示
                h.Title.Text = s.DisplayTitle;
                h.Title.SetTextColor(new Color(now ? Accent : TextMain));
                h.Title.SetTypeface(null, now ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                h.Sub.Text = s.ArtistLine + (s.IsVideo ? L10n.T("（视频）") : (s.IsOnline ? L10n.T(" · 在线") : ""));
                h.Sub.SetTextColor(new Color(now ? Accent : TextSub));
                h.Dur.Text = Song.FormatMs(s.DurationMs);

                // 封面（带缓存）：已落盘的本地封面优先（本地歌曲 / 已缓存封面文件的在线歌曲），
                // 其次才用在线歌曲的远程内存缓存，最后占位图。
                var cb0 = CachedCover(s, 96);
                if (cb0 != null && !cb0.IsRecycled)
                {
                    h.Cover.SetImageBitmap(cb0);
                    h.Cover.SetScaleType(ImageView.ScaleType.CenterCrop);
                }
                else if (s.IsOnline && !string.IsNullOrEmpty(s.OnlineCover))
                {
                    var oc = CachedOnlineCover(s, 96);
                    if (oc != null && !oc.IsRecycled)
                    {
                        h.Cover.SetImageBitmap(oc);
                        h.Cover.SetScaleType(ImageView.ScaleType.CenterCrop);
                    }
                    else
                    {
                        h.Cover.SetImageBitmap(PlaceholderBitmap(true));
                        h.Cover.SetScaleType(ImageView.ScaleType.Center);
                        LoadOnlineCover(h.Cover, s);
                    }
                }
                else
                {
                    h.Cover.SetImageBitmap(PlaceholderBitmap(true));
                    h.Cover.SetScaleType(ImageView.ScaleType.Center);
                }
                return row;
            }
        }
    }
}

// ============================================================
// 音乐模块 · 歌单与「我喜欢的音乐」
// · 数据存 files/music/playlists.json，与曲库扫描解耦
// · 歌单按文件路径保存；曲库中已删除的文件在读取时自动忽略
// · 支持创建 / 重命名 / 删除 / 加歌 / 移歌
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using IoPath = System.IO.Path;

namespace AhoChatA.Music
{
    public class Playlist
    {
        public string Id = "";
        public string Name = "";
        public List<string> Paths = new List<string>();
    }

    public static class PlaylistStore
    {
        public const string FavoritesId = "__favorites__";
        public const string FavoritesName = "我喜欢的音乐";

        private static readonly object _io = new object();
        private static Store _s = new Store();

        private class Store
        {
            public List<string> Favorites = new List<string>();
            public List<Playlist> Playlists = new List<Playlist>();
        }

        private static string FilePath =>
            IoPath.Combine(MusicLibrary.PlatformFilesDir(), "music", "playlists.json");

        public static void Init()
        {
            lock (_io)
            {
                try
                {
                    Directory.CreateDirectory(IoPath.GetDirectoryName(FilePath));
                    if (File.Exists(FilePath))
                        _s = JsonConvert.DeserializeObject<Store>(File.ReadAllText(FilePath)) ?? new Store();
                }
                catch { _s = new Store(); }
                if (_s.Favorites == null) _s.Favorites = new List<string>();
                if (_s.Playlists == null) _s.Playlists = new List<Playlist>();
            }
        }

        private static void Save()
        {
            lock (_io)
            {
                try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(_s, Formatting.Indented)); }
                catch { }
            }
        }

        /* ---------------- 读取 ---------------- */

        public static List<Playlist> Playlists
        {
            get { lock (_io) return _s.Playlists.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        }

        public static List<string> Favorites
        {
            get { lock (_io) return _s.Favorites.ToList(); }
        }

        /// <summary>「我喜欢的音乐」当作一个不可删除的特殊歌单。</summary>
        public static Playlist FavoritesList => new Playlist
        { Id = FavoritesId, Name = FavoritesName, Paths = Favorites };

        public static List<Playlist> AllLists
        {
            get
            {
                var l = new List<Playlist> { FavoritesList };
                l.AddRange(Playlists);
                return l;
            }
        }

        public static Playlist Get(string id) =>
            id == FavoritesId ? FavoritesList : Playlists.FirstOrDefault(p => p.Id == id);

        public static string NameOf(string id) => Get(id)?.Name ?? L10n.T("歌单");

        public static List<string> PathsOf(string id)
        {
            if (id == FavoritesId) return Favorites;
            lock (_io)
            {
                var p = _s.Playlists.FirstOrDefault(x => x.Id == id);
                return p?.Paths?.ToList() ?? new List<string>();
            }
        }

        public static int CountOf(string id) => PathsOf(id).Count;

        public static bool Contains(string id, string path)
        {
            lock (_io)
                return PathsOf(id).Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把路径解析成当前曲库里的 Song（文件已不在曲库时跳过）。</summary>
        public static List<Song> SongsOf(string id)
        {
            var map = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in MusicLibrary.Songs.ToList())
                if (!map.ContainsKey(s.Path)) map[s.Path] = s;
            var res = new List<Song>();
            foreach (var p in PathsOf(id))
                if (map.TryGetValue(p, out var s)) res.Add(s);
            return res;
        }

        /* ---------------- 喜欢 ---------------- */

        public static bool IsFavorite(string path)
        {
            lock (_io) return _s.Favorites.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>切换喜欢状态，返回切换后的状态。</summary>
        public static bool ToggleFavorite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_io)
            {
                int i = _s.Favorites.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) _s.Favorites.RemoveAt(i);
                else _s.Favorites.Add(path);
                Save();
                return i < 0;
            }
        }

        public static void AddFavorite(string path) { if (!IsFavorite(path)) ToggleFavorite(path); }

        /* ---------------- 歌单增删改 ---------------- */

        public static Playlist Create(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = L10n.T("新建歌单");
            var p = new Playlist { Id = Guid.NewGuid().ToString("N"), Name = name.Trim() };
            lock (_io) { _s.Playlists.Add(p); Save(); }
            return p;
        }

        public static void Rename(string id, string name)
        {
            if (id == FavoritesId || string.IsNullOrWhiteSpace(name)) return;
            lock (_io)
            {
                var p = _s.Playlists.FirstOrDefault(x => x.Id == id);
                if (p == null) return;
                p.Name = name.Trim();
                Save();
            }
        }

        public static void Delete(string id)
        {
            if (id == FavoritesId) return;
            lock (_io)
            {
                var p = _s.Playlists.FirstOrDefault(x => x.Id == id);
                if (p == null) return;
                _s.Playlists.Remove(p);
                Save();
            }
        }

        /* ---------------- 歌单内条目 ---------------- */

        public static void AddTo(string id, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_io)
            {
                List<string> list;
                if (id == FavoritesId) list = _s.Favorites;
                else
                {
                    var p = _s.Playlists.FirstOrDefault(x => x.Id == id);
                    if (p == null) return;
                    if (p.Paths == null) p.Paths = new List<string>();
                    list = p.Paths;
                }
                if (list.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(path);
                Save();
            }
        }

        public static void RemoveFrom(string id, string path)
        {
            lock (_io)
            {
                List<string> list = id == FavoritesId
                    ? _s.Favorites
                    : _s.Playlists.FirstOrDefault(x => x.Id == id)?.Paths;
                if (list == null) return;
                int i = list.FindIndex(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { list.RemoveAt(i); Save(); }
            }
        }
    }
}

/* ===================== 阅读模块 · 数据存储 =====================
 * 本地书籍原文（可能很大）存 IndexedDB；
 * 书架元数据、阅读进度、书签、书源、设置存 localStorage。
 */
(function () {
    'use strict';

    var DB_NAME = 'Mchat_reader';
    var DB_VER = 1;
    var dbPromise = null;

    function openDB() {
        if (dbPromise) return dbPromise;
        dbPromise = new Promise(function (resolve, reject) {
            if (!window.indexedDB) { reject(new Error('当前环境不支持 IndexedDB')); return; }
            var req = indexedDB.open(DB_NAME, DB_VER);
            req.onupgradeneeded = function () {
                var db = req.result;
                if (!db.objectStoreNames.contains('files')) db.createObjectStore('files', { keyPath: 'id' });
                if (!db.objectStoreNames.contains('cache')) db.createObjectStore('cache');
            };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
        return dbPromise;
    }

    function tx(store, mode) {
        return openDB().then(function (db) {
            return db.transaction(store, mode).objectStore(store);
        });
    }

    function idbPut(store, value, key) {
        return tx(store, 'readwrite').then(function (s) {
            return new Promise(function (res, rej) {
                var r = key === undefined ? s.put(value) : s.put(value, key);
                r.onsuccess = function () { res(); };
                r.onerror = function () { rej(r.error); };
            });
        });
    }

    function idbGet(store, key) {
        return tx(store, 'readonly').then(function (s) {
            return new Promise(function (res, rej) {
                var r = s.get(key);
                r.onsuccess = function () { res(r.result); };
                r.onerror = function () { rej(r.error); };
            });
        });
    }

    function idbDelete(store, key) {
        return tx(store, 'readwrite').then(function (s) {
            return new Promise(function (res, rej) {
                var r = s.delete(key);
                r.onsuccess = function () { res(); };
                r.onerror = function () { rej(r.error); };
            });
        });
    }

    function lsGet(key, def) {
        try { var v = localStorage.getItem(key); return v == null ? def : JSON.parse(v); }
        catch (e) { return def; }
    }
    function lsSet(key, val) {
        try { localStorage.setItem(key, JSON.stringify(val)); return true; } catch (e) { return false; }
    }

    var LIB_KEY = 'Mchat.reader.library';
    var SET_KEY = 'Mchat.reader.settings';
    var SRC_KEY = 'Mchat.reader.sources';
    var CLEAN_KEY = 'Mchat.reader.clean';

    var defaultSettings = {
        theme: 'day', fontSize: 19, lineHeight: 1.9, para: 0.6,
        margin: 22, justify: true, mode: 'scroll',
        font: '', clean: []
    };

    window.ReaderStore = {
        // ---- 原始书籍文件（本地导入） ----
        saveFile: function (rec) { return idbPut('files', rec); },
        getFile: function (id) { return idbGet('files', id); },
        deleteFile: function (id) { return idbDelete('files', id); },

        // ---- 章节内容缓存（在线书源） ----
        cachePut: function (key, text) { return idbPut('cache', text, key); },
        cacheGet: function (key) { return idbGet('cache', key).then(function (r) { return r || null; }); },

        // ---- 书架 ----
        getLibrary: function () { return lsGet(LIB_KEY, []); },
        setLibrary: function (list) { lsSet(LIB_KEY, list); },
        addBook: function (book) {
            var lib = this.getLibrary();
            var i = lib.findIndex(function (b) { return b.id === book.id; });
            if (i >= 0) lib[i] = Object.assign(lib[i], book);
            else lib.unshift(book);
            this.setLibrary(lib);
            return book;
        },
        removeBook: function (id) {
            var lib = this.getLibrary().filter(function (b) { return b.id !== id; });
            this.setLibrary(lib);
            this.deleteFile(id);
            localStorage.removeItem('Mchat.reader.progress.' + id);
            localStorage.removeItem('Mchat.reader.bookmarks.' + id);
        },
        getBook: function (id) { return this.getLibrary().find(function (b) { return b.id === id; }) || null; },

        // ---- 进度 ----
        getProgress: function (id) { return lsGet('Mchat.reader.progress.' + id, { ci: 0, pos: 0, pct: 0 }); },
        setProgress: function (id, p) { lsSet('Mchat.reader.progress.' + id, p); },

        // ---- 书签 ----
        getBookmarks: function (id) { return lsGet('Mchat.reader.bookmarks.' + id, []); },
        addBookmark: function (id, bm) {
            var list = this.getBookmarks(id);
            list.unshift(bm);
            lsSet('Mchat.reader.bookmarks.' + id, list);
            return list;
        },
        removeBookmark: function (id, idx) {
            var list = this.getBookmarks(id);
            list.splice(idx, 1);
            lsSet('Mchat.reader.bookmarks.' + id, list);
            return list;
        },

        // ---- 设置 ----
        getSettings: function () {
            var s = lsGet(SET_KEY, {});
            return Object.assign({}, defaultSettings, s);
        },
        setSettings: function (s) {
            var cur = this.getSettings();
            var merged = Object.assign({}, cur, s);
            lsSet(SET_KEY, merged);
            return merged;
        },

        // ---- 书源 ----
        getSources: function () { return lsGet(SRC_KEY, []); },
        setSources: function (list) { lsSet(SRC_KEY, list); },
        addSource: function (src) {
            var list = this.getSources();
            var i = list.findIndex(function (s) { return s.bookSourceUrl === src.bookSourceUrl; });
            if (i >= 0) list[i] = src; else list.push(src);
            this.setSources(list);
            return src;
        },
        removeSource: function (url) {
            var list = this.getSources().filter(function (s) { return s.bookSourceUrl !== url; });
            this.setSources(list);
        },
        updateSource: function (url, patch) {
            var list = this.getSources();
            var i = list.findIndex(function (s) { return s.bookSourceUrl === url; });
            if (i >= 0) { list[i] = Object.assign({}, list[i], patch); this.setSources(list); }
        }
    };
})();

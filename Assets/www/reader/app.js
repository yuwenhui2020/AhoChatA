/* ===================== 阅读模块 · 主控制器 ===================== */
(function () {
    'use strict';

    var S = window.ReaderStore;
    var E = window.BookSourceEngine;
    var P = window.Parser;

    var state = {
        view: 'shelf',
        currentBook: null,
        chapters: [],
        ci: 0,
        scrollSaveTimer: null,
        speaking: false
    };

    function $(id) { return document.getElementById(id); }
    function el(tag, cls, html) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (html != null) e.innerHTML = html;
        return e;
    }
    function toast(msg) {
        var t = $('readerToast');
        t.textContent = msg; t.classList.add('show');
        clearTimeout(t._t); t._t = setTimeout(function () { t.classList.remove('show'); }, 1600);
    }

    /* ---------- 设置应用 ---------- */
    function applySettings(s) {
        var app = $('app');
        app.setAttribute('data-theme', s.theme);
        app.style.setProperty('--r-font-size', s.fontSize + 'px');
        app.style.setProperty('--r-line-height', s.lineHeight);
        app.style.setProperty('--r-para', s.para + 'em');
        app.style.setProperty('--r-margin', s.margin + 'px');
        app.style.setProperty('--r-justify', s.justify ? 'justify' : 'left');
        app.style.setProperty('--r-font', s.font || '-apple-system,"PingFang SC","Microsoft YaHei",sans-serif');
    }

    /* ---------- 视图切换 ---------- */
    function showView(name) {
        state.view = name;
        $('shelfView').classList.toggle('hidden', name !== 'shelf');
        $('readerView').classList.toggle('hidden', name !== 'reader');
        $('sourcesView').classList.toggle('hidden', name !== 'sources');
        var titles = { shelf: '书架', reader: (state.currentBook && state.currentBook.name) || '阅读', sources: '书源管理' };
        $('rTitle').textContent = titles[name] || '阅读';
        closeDrawers();
    }

    function closeDrawers() {
        ['settingsDrawer', 'tocDrawer', 'bookmarkDrawer', 'sourceModal', 'shareModal'].forEach(function (id) {
            $(id).classList.add('hidden');
        });
        $('rMask').classList.add('hidden');
    }

    /* ---------- 书架 ---------- */
    function renderShelf() {
        var lib = S.getLibrary();
        var grid = $('bookGrid');
        grid.innerHTML = '';
        var empty = $('shelfEmpty');
        if (!lib.length) { empty.classList.remove('hidden'); return; }
        empty.classList.add('hidden');
        lib.forEach(function (book) {
            var card = el('div', 'r-book');
            var inner = el('div', 'r-book-inner');
            var cover = el('div', 'r-book-cover');
            if (book.cover) {
                var img = el('img'); img.src = book.cover; cover.appendChild(img);
            } else {
                cover.textContent = (book.name || '书').slice(0, 6);
            }
            var del = el('button', 'r-book-del', '×');
            del.onclick = function (e) { e.stopPropagation(); removeBook(book.id); };
            cover.appendChild(del);
            var meta = el('div', 'r-book-meta');
            var prog = S.getProgress(book.id);
            meta.appendChild(el('div', 'r-book-title', escapeHtml(book.name || '未命名')));
            meta.appendChild(el('div', 'r-book-author', escapeHtml(book.author || (book.kind === 'online' ? '在线书源' : '本地'))));
            meta.appendChild(el('div', 'r-book-author', '阅读进度 ' + Math.round((prog.pct || 0) * 100) + '%'));
            inner.appendChild(cover); inner.appendChild(meta);
            card.appendChild(inner);
            card.onclick = function () { openBook(book.id); };
            grid.appendChild(card);
        });
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
            return c === '&' ? '&amp;' : c === '<' ? '&lt;' : c === '>' ? '&gt;' : '&quot;';
        });
    }

    function removeBook(id) {
        if (!confirm('确定从书架移除该书？')) return;
        S.removeBook(id);
        renderShelf();
        toast('已移除');
    }

    /* ---------- 导入本地书籍 ---------- */
    function importFiles(fileList) {
        var files = Array.prototype.slice.call(fileList);
        if (!files.length) return;
        toast('正在导入 ' + files.length + ' 本…');
        var seq = Promise.resolve();
        files.forEach(function (file) {
            seq = seq.then(function () { return importOne(file); });
        });
        seq.then(function () { renderShelf(); toast('导入完成'); }).catch(function (e) {
            renderShelf(); toast('导入失败：' + e.message);
        });
    }

    function importOne(file) {
        var lower = file.name.toLowerCase();
        var id = 'b_' + Date.now() + '_' + Math.floor(Math.random() * 1e6);
        if (lower.endsWith('.epub')) {
            return file.arrayBuffer().then(function (buf) {
                return P.parseEpub(buf);
            }).then(function (parsed) {
                return finishLocal(id, file.name, parsed, 'epub');
            });
        } else {
            return file.text().then(function (text) {
                return P.parseTxt(text);
            }).then(function (parsed) {
                return finishLocal(id, file.name, parsed, 'txt');
            });
        }
    }

    function finishLocal(id, name, parsed, type) {
        var book = {
            id: id, name: parsed.title || name.replace(/\.(txt|epub|text)$/i, ''),
            author: parsed.author || '佚名', type: type, kind: 'local',
            chaptersCount: parsed.chapters.length, addedAt: Date.now(),
            cover: null, lastRead: Date.now()
        };
        // 章节内容存 IDB（本地书无需每次重解析）
        return S.saveFile({ id: id, type: type, chapters: parsed.chapters }).then(function () {
            S.addBook(book);
            S.setProgress(id, { ci: 0, pos: 0, pct: 0 });
            return book;
        });
    }

    /* ---------- 从共享目录导入 ---------- */
    var SHARE_BASE = 'http://127.0.0.1:6789';
    var shareCur = '/';
    function openShare() {
        shareCur = '/';
        $('shareModal').classList.remove('hidden');
        $('rMask').classList.remove('hidden');
        loadShare('/');
    }
    function loadShare(path) {
        shareCur = path;
        $('sharePath').textContent = path || '/';
        fetch(SHARE_BASE + '/api/share/list?path=' + encodeURIComponent(path || ''))
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var list = $('shareList'); list.innerHTML = '';
                (data.items || []).forEach(function (it) {
                    var row = el('div', 'r-share-item');
                    var sico = el('span', 'si-ico'); sico.innerHTML = WI.svg(it.dir ? 'folder' : 'file'); row.appendChild(sico);
                    row.appendChild(el('span', 'si-name', escapeHtml(it.name)));
                    row.onclick = function () {
                        if (it.dir) loadShare((path ? path.replace(/\/$/, '') + '/' : '/') + it.name);
                        else pickShareFile((path ? path.replace(/\/$/, '') + '/' : '/') + it.name, it.name);
                    };
                    list.appendChild(row);
                });
                if (!data.items || !data.items.length) list.appendChild(el('div', 'r-empty-sub', '空目录'));
            })
            .catch(function () { $('shareList').innerHTML = '<div class="r-empty-sub">无法访问共享目录（需先在 Mchat 授予文件访问权限）</div>'; });
    }
    function pickShareFile(rel, name) {
        if (!/\.(txt|epub|text)$/i.test(name)) { toast('仅支持 TXT / EPUB'); return; }
        toast('正在读取 ' + name + ' …');
        fetch(SHARE_BASE + '/api/share/raw?path=' + encodeURIComponent(rel))
            .then(function (r) { return (name.toLowerCase().endsWith('.epub') ? r.arrayBuffer().then(P.parseEpub) : r.text().then(P.parseTxt)); })
            .then(function (parsed) {
                var id = 'b_' + Date.now();
                return finishLocal(id, name, parsed, name.toLowerCase().endsWith('.epub') ? 'epub' : 'txt').then(function (b) {
                    closeDrawers(); renderShelf(); openBook(b.id);
                });
            })
            .catch(function (e) { toast('读取失败：' + e.message); });
    }

    /* ---------- 打开书籍 / 阅读 ---------- */
    function openBook(id) {
        var book = S.getBook(id);
        if (!book) return;
        state.currentBook = book;
        S.addBook(Object.assign({}, book, { lastRead: Date.now() }));
        showView('reader');
        if (book.kind === 'local') {
            S.getFile(id).then(function (rec) {
                state.chapters = (rec && rec.chapters) || [];
                var prog = S.getProgress(id);
                renderChapter(prog.ci || 0, true);
            });
        } else {
            // 在线书：优先用缓存目录
            if (book.toc && book.toc.length) {
                state.chapters = book.toc;
                var p2 = S.getProgress(id);
                renderChapter(p2.ci || 0, true);
            } else {
                $('readerContent').innerHTML = '<div class="r-loading">正在获取目录…</div>';
                E.getToc({ bookSourceUrl: book.sourceUrl }, book.tocUrl).then(function (toc) {
                    state.chapters = toc;
                    book.toc = toc; S.addBook(book);
                    renderChapter(0, true);
                }).catch(function (e) { $('readerContent').innerHTML = '<div class="r-loading">获取目录失败：' + escapeHtml(e.message) + '</div>'; });
            }
        }
    }

    function renderChapter(ci, restore) {
        if (ci < 0) ci = 0;
        if (ci >= state.chapters.length) ci = state.chapters.length - 1;
        state.ci = ci;
        var ch = state.chapters[ci];
        var box = $('readerContent');
        if (!ch) { box.innerHTML = '<div class="r-loading">目录为空，该书源规则可能未匹配到章节</div>'; return; }
        var title = ch.title || ('第 ' + (ci + 1) + ' 章');
        box.innerHTML = '<h2 class="r-chapter-title">' + escapeHtml(title) + '</h2><div class="r-ch-body r-loading">加载中…</div>';
        $('readerScroll').scrollTop = 0;

        function fill(html) {
            var body = box.querySelector('.r-ch-body');
            if (body) { body.className = 'r-ch-body'; body.innerHTML = html; }
            updateProgress();
            if (restore) {
                var prog = S.getProgress(state.currentBook.id);
                if (prog.ci === ci && prog.pos) {
                    var sc = $('readerScroll');
                    sc.scrollTop = (sc.scrollHeight - sc.clientHeight) * prog.pos;
                }
            }
            updateTocCurrent();
        }

        if (state.currentBook.kind === 'local' || ch.content) {
            fill(ch.content || '');
        } else {
            var settings = S.getSettings();
            E.cacheGet(ch.url).then(function (cached) {
                if (cached) { fill(cached); return; }
                var src = findSource(state.currentBook.sourceUrl);
                E.getContent(src, ch.url, settings.clean).then(function (html) {
                    E.cachePut(ch.url, html);
                    fill(html);
                }).catch(function (e) { fill('<p style="color:#c0392b">本章加载失败：' + escapeHtml(e.message) + '</p>'); });
            });
        }
        $('rTitle').textContent = (state.currentBook.name || '阅读');
    }

    function findSource(url) {
        return S.getSources().find(function (s) { return s.bookSourceUrl === url; }) || {};
    }

    function nextChapter() { if (state.ci < state.chapters.length - 1) renderChapter(state.ci + 1, false); else toast('已是最后一章'); }
    function prevChapter() { if (state.ci > 0) renderChapter(state.ci - 1, false); else toast('已是第一章'); }

    /* ---------- 进度 ---------- */
    function updateProgress() {
        var sc = $('readerScroll');
        var denom = sc.scrollHeight - sc.clientHeight;
        var pos = denom > 0 ? sc.scrollTop / denom : 0;
        var pct = state.chapters.length ? (state.ci + pos) / state.chapters.length : 0;
        $('readerProgress').textContent = Math.round(pct * 100) + '%';
        if (state.currentBook) {
            S.setProgress(state.currentBook.id, { ci: state.ci, pos: pos, pct: pct });
        }
    }

    /* ---------- 目录 ---------- */
    function renderToc() {
        var list = $('tocList'); list.innerHTML = '';
        state.chapters.forEach(function (ch, i) {
            var item = el('div', 'r-toc-item' + (i === state.ci ? ' current' : ''), escapeHtml(ch.title || ('第 ' + (i + 1) + ' 章')));
            item.onclick = function () { closeDrawers(); renderChapter(i, false); };
            list.appendChild(item);
        });
    }
    function updateTocCurrent() {
        var items = $('tocList').children;
        for (var i = 0; i < items.length; i++) items[i].classList.toggle('current', i === state.ci);
    }

    /* ---------- 书签 ---------- */
    function addBookmark() {
        if (!state.currentBook) return;
        var sc = $('readerScroll');
        var ch = state.chapters[state.ci];
        var pos = sc.scrollHeight > 0 ? sc.scrollTop / (sc.scrollHeight - sc.clientHeight || 1) : 0;
        var body = $('readerContent').innerText || '';
        var snippet = body.replace(/\s+/g, ' ').slice(0, 60);
        S.addBookmark(state.currentBook.id, { ci: state.ci, pos: pos, text: snippet, time: Date.now(), title: ch.title });
        toast('已添加书签');
    }
    function renderBookmarks() {
        var list = $('bookmarkList'); list.innerHTML = '';
        var bms = S.getBookmarks(state.currentBook.id);
        if (!bms.length) { list.appendChild(el('div', 'r-empty-sub', '暂无书签')); return; }
        bms.forEach(function (bm, i) {
            var item = el('div', 'r-bookmark-item');
            item.appendChild(el('div', 'bm-text', escapeHtml(bm.text || '')));
            var meta = el('div', 'bm-meta');
            meta.appendChild(el('span', null, (bm.title || ('第' + (bm.ci + 1) + '章'))));
            var del = el('span', 'bm-del', '删除');
            del.onclick = function (e) { e.stopPropagation(); S.removeBookmark(state.currentBook.id, i); renderBookmarks(); };
            meta.appendChild(del);
            item.appendChild(meta);
            item.onclick = function () { closeDrawers(); renderChapter(bm.ci, false); var sc = $('readerScroll'); setTimeout(function () { sc.scrollTop = (sc.scrollHeight - sc.clientHeight) * bm.pos; }, 50); };
            list.appendChild(item);
        });
    }

    /* ---------- 朗读（TTS） ---------- */
    function toggleSpeak() {
        if (state.speaking) { window.speechSynthesis.cancel(); state.speaking = false; $('btnSpeak').textContent = '朗读'; return; }
        if (!window.speechSynthesis) { toast('当前环境不支持朗读'); return; }
        var text = ($('readerContent').innerText || '').replace(/\s+/g, ' ').trim();
        if (!text) return;
        var u = new SpeechSynthesisUtterance(text);
        u.lang = 'zh-CN'; u.rate = 1; u.pitch = 1;
        var voices = window.speechSynthesis.getVoices();
        var zh = voices.find(function (v) { return /zh|cmn/i.test(v.lang); });
        if (zh) u.voice = zh;
        u.onend = function () { state.speaking = false; $('btnSpeak').textContent = '朗读'; };
        window.speechSynthesis.cancel();
        window.speechSynthesis.speak(u);
        state.speaking = true; $('btnSpeak').textContent = '停止';
    }

    /* ---------- 书源管理 ---------- */
    function renderSources() {
        var list = $('sourceList'); list.innerHTML = '';
        var srcs = S.getSources();
        if (!srcs.length) list.appendChild(el('div', 'r-empty-sub', '暂无书源，点击「新建书源」或「导入书源 JSON」'));
        srcs.forEach(function (s) {
            var row = el('div', 'r-source');
            var info = el('div', 'rs-info');
            info.appendChild(el('div', 'rs-name', escapeHtml(s.bookSourceName || '未命名') + ' <span class="rs-type">' + typeName(s.bookSourceType) + '</span>'));
            info.appendChild(el('div', 'rs-url', escapeHtml(s.bookSourceUrl || '')));
            row.appendChild(info);
            var acts = el('div', 'rs-actions');
            var sw = el('label', 'r-switch');
            sw.innerHTML = '<input type="checkbox" ' + (s.enabled ? 'checked' : '') + '><span class="track"></span><span class="knob"></span>';
            sw.querySelector('input').onchange = function () { S.updateSource(s.bookSourceUrl, { enabled: this.checked }); toast('已' + (this.checked ? '启用' : '停用')); };
            acts.appendChild(sw);
            var edit = el('button', 'rs-btn', '编辑');
            edit.onclick = function () { openSourceModal(s); };
            var del = el('button', 'rs-btn', '删除');
            del.onclick = function () { if (confirm('删除该书源？')) { S.removeSource(s.bookSourceUrl); renderSources(); } };
            acts.appendChild(edit); acts.appendChild(del);
            row.appendChild(acts);
            list.appendChild(row);
        });
    }
    function typeName(t) { return ({ 0: '文本', 1: '音频', 2: '图片', 3: '文件' })[t] || '文本'; }

    function openSourceModal(src) {
        $('sourceModalTitle').textContent = src ? '编辑书源' : '新建书源';
        $('sourceJson').value = src ? JSON.stringify(src, null, 2) : JSON.stringify({
            bookSourceName: '', bookSourceUrl: '', bookSourceType: 0, enabled: true,
            searchUrl: '', ruleSearch: { bookList: '', name: '', author: '', bookUrl: '', intro: '', coverUrl: '', lastChapter: '' },
            ruleBookInfo: { name: '', author: '', intro: '', coverUrl: '', tocUrl: '', lastChapter: '' },
            ruleToc: { chapterList: '', chapterName: '', chapterUrl: '' },
            ruleContent: { content: '', replaceRegex: '', webJs: '' }
        }, null, 2);
        $('sourceModal').classList.remove('hidden');
        $('rMask').classList.remove('hidden');
    }

    function saveSourceFromModal() {
        var text = $('sourceJson').value.trim();
        if (!text) { toast('内容为空'); return; }
        var arr;
        try { arr = JSON.parse(text); } catch (e) { toast('JSON 解析失败：' + e.message); return; }
        if (!Array.isArray(arr)) arr = [arr];
        arr.forEach(function (s) { if (s && s.bookSourceUrl) S.addSource(s); });
        closeDrawers(); renderSources(); toast('已保存 ' + arr.length + ' 个书源');
    }

    function importSourceFile(file) {
        file.text().then(function (t) {
            try {
                var arr = JSON.parse(t); if (!Array.isArray(arr)) arr = [arr];
                arr.forEach(function (s) { if (s && s.bookSourceUrl) S.addSource(s); });
                renderSources(); toast('已导入 ' + arr.length + ' 个书源');
            } catch (e) { toast('导入失败：' + e.message); }
        });
    }

    function exportSources() {
        var data = JSON.stringify(S.getSources(), null, 2);
        var blob = new Blob([data], { type: 'application/json' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob); a.download = 'Mchat_book_sources.json';
        a.click(); setTimeout(function () { URL.revokeObjectURL(a.href); }, 2000);
    }

    /* ---------- 在线搜索 ---------- */
    function doSearch(keyword) {
        keyword = (keyword || '').trim();
        if (!keyword) return;
        var sources = S.getSources().filter(function (s) { return s.enabled && s.searchUrl; });
        if (!sources.length) { toast('请先在「书源」启用带搜索的书源'); return; }
        var box = $('searchResults');
        box.classList.remove('hidden');
        box.innerHTML = '<div class="r-loading">正在从 ' + sources.length + ' 个书源搜索…</div>';
        Promise.all(sources.map(function (s) {
            return E.search(s, keyword).then(function (r) { return r || []; }).catch(function () { return []; });
        })).then(function (ress) {
            var all = [].concat.apply([], ress);
            box.innerHTML = '';
            if (!all.length) { box.appendChild(el('div', 'r-empty-sub', '未找到结果，或书源规则需调整')); return; }
            all.forEach(function (b) {
                var item = el('div', 'r-search-item');
                item.appendChild(el('div', 'si-name', escapeHtml(b.name || '未知')));
                item.appendChild(el('div', 'si-author', escapeHtml(b.author || '') + (b.kind ? ' · ' + escapeHtml(b.kind) : '')));
                item.appendChild(el('div', 'si-intro', escapeHtml(b.intro || '')));
                item.appendChild(el('div', 'si-src', escapeHtml(b.sourceName || '')));
                item.onclick = function () { openSearchResult(b); };
                box.appendChild(item);
            });
        });
    }

    function openSearchResult(b) {
        toast('正在获取详情…');
        var src = findSource(b.sourceUrl);
        E.getBookInfo(src, b.bookUrl).then(function (info) {
            var id = 'o_' + Date.now();
            var book = {
                id: id, name: info.name || b.name, author: info.author || b.author,
                cover: info.coverUrl || b.coverUrl, kind: 'online',
                sourceUrl: b.sourceUrl, tocUrl: info.tocUrl,
                addedAt: Date.now(), lastRead: Date.now()
            };
            S.addBook(book);
            box_hide_search();
            openBook(id);
        }).catch(function (e) { toast('获取详情失败：' + e.message); });
    }
    function box_hide_search() { $('searchResults').classList.add('hidden'); $('searchInput').value = ''; $('searchRow').classList.add('hidden'); }

    /* ---------- 设置抽屉 ---------- */
    function bindSettings() {
        var s = S.getSettings();
        applySettings(s);
        $('setFontSize').value = s.fontSize; $('setFontSizeVal').textContent = s.fontSize + 'px';
        $('setLineHeight').value = s.lineHeight; $('setLineHeightVal').textContent = s.lineHeight;
        $('setPara').value = s.para; $('setParaVal').textContent = s.para + 'em';
        $('setMargin').value = s.margin; $('setMarginVal').textContent = s.margin + 'px';
        $('setJustify').checked = s.justify;
        $('setMode').querySelectorAll('button').forEach(function (b) { b.classList.toggle('active', b.dataset.mode === s.mode); });
        $('setTheme').querySelectorAll('button').forEach(function (b) { b.classList.toggle('active', b.dataset.theme === s.theme); });
        $('setFont').value = s.font || '';
        $('setClean').value = (s.clean || []).map(function (r) { return r.pattern + (r.replacement ? ('→' + r.replacement) : ''); }).join('\n');

        function live(input, key, fmt) {
            input.oninput = function () {
                var v = parseFloat(input.value); if (isNaN(v)) return;
                var ns = {}; ns[key] = v; S.setSettings(ns); applySettings(S.getSettings());
                if (fmt) fmt(input.value);
            };
        }
        live($('setFontSize'), 'fontSize', function (v) { $('setFontSizeVal').textContent = v + 'px'; });
        live($('setLineHeight'), 'lineHeight', function (v) { $('setLineHeightVal').textContent = v; });
        live($('setPara'), 'para', function (v) { $('setParaVal').textContent = v + 'em'; });
        live($('setMargin'), 'margin', function (v) { $('setMarginVal').textContent = v + 'px'; });
        $('setJustify').onchange = function () { S.setSettings({ justify: this.checked }); applySettings(S.getSettings()); };
        $('setMode').querySelectorAll('button').forEach(function (b) {
            b.onclick = function () { $('setMode').querySelectorAll('button').forEach(function (x) { x.classList.remove('active'); }); b.classList.add('active'); S.setSettings({ mode: b.dataset.mode }); };
        });
        $('setTheme').querySelectorAll('button').forEach(function (b) {
            b.onclick = function () { $('setTheme').querySelectorAll('button').forEach(function (x) { x.classList.remove('active'); }); b.classList.add('active'); S.setSettings({ theme: b.dataset.theme }); applySettings(S.getSettings()); };
        });
        $('setFont').onchange = function () { S.setSettings({ font: this.value }); applySettings(S.getSettings()); };
        $('btnSaveClean').onclick = function () {
            var lines = $('setClean').value.split('\n').map(function (l) { return l.trim(); }).filter(Boolean);
            var rules = lines.map(function (l) {
                var m = l.split('→'); return { pattern: (m[0] || '').trim(), replacement: (m[1] || '').trim(), flags: 'g' };
            }).filter(function (r) { return r.pattern; });
            S.setSettings({ clean: rules }); toast('已保存 ' + rules.length + ' 条净化规则');
        };
    }

    /* ---------- 事件绑定 ---------- */
    function bindEvents() {
        $('btnBack').onclick = function () {
            if (state.view === 'reader' || state.view === 'sources') showView('shelf');
            else window.location.href = '/';
        };
        $('btnImport').onclick = function () { $('fileInput').value = ''; $('fileInput').click(); };
        $('fileInput').onchange = function () { importFiles(this.files); };
        $('btnImportShare').onclick = openShare;
        $('btnShareUp').onclick = function () { var p = shareCur.split('/'); p.pop(); loadShare(p.join('/') || '/'); };
        $('shelfToggle').querySelectorAll('button').forEach(function (b) {
            b.onclick = function () { $('shelfToggle').querySelectorAll('button').forEach(function (x) { x.classList.remove('active'); }); b.classList.add('active'); $('bookGrid').classList.toggle('list', b.dataset.mode === 'list'); };
        });

        $('btnSearch').onclick = function () { $('searchRow').classList.toggle('hidden'); if (!$('searchRow').classList.contains('hidden')) $('searchInput').focus(); };
        $('btnSearchGo').onclick = function () { doSearch($('searchInput').value); };
        $('searchInput').addEventListener('keydown', function (e) { if (e.key === 'Enter') doSearch(this.value); });

        $('btnSources').onclick = function () { renderSources(); showView('sources'); };
        $('btnSettings').onclick = function () { $('settingsDrawer').classList.remove('hidden'); $('rMask').classList.remove('hidden'); };
        $('btnAddSource').onclick = function () { openSourceModal(null); };
        $('btnImportSource').onclick = function () { var inp = document.createElement('input'); inp.type = 'file'; inp.accept = '.json,.txt'; inp.onchange = function () { if (this.files[0]) importSourceFile(this.files[0]); }; inp.click(); };
        $('btnExportSource').onclick = exportSources;
        $('btnSaveSource').onclick = saveSourceFromModal;

        $('btnToc').onclick = function () { renderToc(); $('tocDrawer').classList.remove('hidden'); $('rMask').classList.remove('hidden'); };
        $('btnBookmark').onclick = function () { renderBookmarks(); $('bookmarkDrawer').classList.remove('hidden'); $('rMask').classList.remove('hidden'); };
        $('btnSpeak').onclick = toggleSpeak;
        $('btnReaderSettings').onclick = function () { $('settingsDrawer').classList.remove('hidden'); $('rMask').classList.remove('hidden'); };

        $('readerScroll').addEventListener('scroll', function () {
            clearTimeout(state.scrollSaveTimer);
            state.scrollSaveTimer = setTimeout(updateProgress, 200);
        });
        // 翻页：底部上滑下一章，顶部下滑上一章（滚动模式）
        var startY = 0;
        $('readerScroll').addEventListener('touchstart', function (e) { startY = e.touches[0].clientY; }, { passive: true });
        $('readerScroll').addEventListener('touchend', function (e) {
            var dy = e.changedTouches[0].clientY - startY;
            var sc = $('readerScroll');
            if (dy < -60 && sc.scrollTop + sc.clientHeight >= sc.scrollHeight - 4) nextChapter();
            else if (dy > 60 && sc.scrollTop <= 0) prevChapter();
        }, { passive: true });

        // 关闭按钮 / 遮罩
        document.querySelectorAll('[data-close]').forEach(function (b) {
            b.onclick = function () { var id = this.getAttribute('data-close'); $(id).classList.add('hidden'); $('rMask').classList.add('hidden'); };
        });
        $('rMask').onclick = closeDrawers;
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeDrawers(); });
    }

    /* ---------- 启动 ---------- */
    function init() {
        // 首次运行写入示例书源（默认关闭）
        if (!S.getSources().length && window.DefaultSources) {
            window.DefaultSources.forEach(function (s) { S.addSource(s); });
        }
        bindSettings();
        bindEvents();
        applySettings(S.getSettings());
        renderShelf();
        showView('shelf');
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();

/* ============================================================
   网页端阅读器
   · 书架 / 进度 / 排版偏好 与手机 App 共用（都走 /api/reader/*）
   · 支持上传 TXT / EPUB（上传后 App 书架立即可见）
   · 书源导入、搜书、在线书加入书架
   ============================================================ */
(function () {
    'use strict';

    var $ = function (s) { return document.querySelector(s); };
    var THEMES = [
        { key: 'light', label: '白',   color: '#FDFCFA' },
        { key: 'sepia', label: '米黄', color: '#F4EAD5' },
        { key: 'green', label: '护眼', color: '#CCE8CF' },
        { key: 'gray',  label: '浅灰', color: '#E7E9EC' },
        { key: 'dark',  label: '深灰', color: '#1B1E22' },
        { key: 'black', label: '黑',   color: '#000000' }
    ];

    var state = {
        shelf: [],
        book: null,
        i: 0,
        total: 0,
        pct: 0,
        prefs: {
            fontSize: 19, lineHeight: 1.85, justify: true, theme: 'light', margin: 22,
            paraSpace: 14, indent: 2, font: 'serif', bold: false
        },
        ex: { sources: [], src: null, cats: [], cat: 0, page: 1 },
        replaceId: null,
        restoring: false,
        // 连读：读完本章自动把下一章拼到文末（同一份文档里无缝续接）
        docChaps: [],
        appending: false,
        appendStop: false,
        autoAppend: true,
        pdf: false             // 当前是 PDF：一页一屏的图片模式
    };

    var FONTS = {
        serif: '"Noto Serif CJK SC", "Source Han Serif SC", "Songti SC", Georgia, serif',
        sans: '"PingFang SC", "Microsoft YaHei", "Heiti SC", "Noto Sans CJK SC", sans-serif',
        system: '-apple-system, BlinkMacSystemFont, "PingFang SC", "Microsoft YaHei", sans-serif'
    };

    /* ---------------- 基础 ---------------- */
    function toast(msg, ms) {
        var el = $('#toast');
        el.textContent = msg;
        el.classList.add('show');
        clearTimeout(el._t);
        el._t = setTimeout(function () { el.classList.remove('show'); }, ms || 2000);
    }

    function api(path, opts) {
        return fetch(path, opts).then(function (r) {
            return r.json().catch(function () { return { ok: false, error: '服务返回异常（' + r.status + '）' }; });
        }).then(function (j) {
            if (!j || j.ok !== true) throw new Error((j && j.error) || '请求失败');
            return j;
        });
    }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function enc(s) { return encodeURIComponent(s == null ? '' : s); }

    /* ---------------- 排版 ---------------- */
    function applyPrefs(p) {
        state.prefs = p;
        document.body.className = 'theme-' + (p.theme || 'light');
        var root = document.documentElement.style;
        root.setProperty('--fs', p.fontSize + 'px');
        root.setProperty('--lh', p.lineHeight);
        root.setProperty('--mg', p.margin + 'px');
        root.setProperty('--ps', (p.paraSpace == null ? 14 : p.paraSpace) + 'px');
        root.setProperty('--ind', (p.indent == null ? 2 : p.indent) + 'em');
        root.setProperty('--ff', FONTS[p.font] || FONTS.serif);
        root.setProperty('--fw', p.bold ? 600 : 400);
        $('#content').classList.toggle('justify', !!p.justify);

        $('#fontVal').textContent = p.fontSize;
        buildThemeDots();
        markSeg('#lineSeg', String(p.lineHeight));
        markSeg('#marginSeg', String(p.margin));
        markSeg('#justifySeg', p.justify ? '1' : '0');
        markSeg('#paraSeg', String(p.paraSpace == null ? 14 : p.paraSpace));
        markSeg('#indentSeg', String(p.indent == null ? 2 : p.indent));
        markSeg('#fontSeg', p.font || 'serif');
        markSeg('#boldSeg', p.bold ? '1' : '0');
    }

    function markSeg(sel, val) {
        Array.prototype.forEach.call(document.querySelectorAll(sel + ' button'), function (b) {
            b.classList.toggle('sel', b.getAttribute('data-v') === val);
        });
    }

    /** 把一组分段按钮绑到某个偏好字段上 */
    function segBind(sel, key, cast) {
        Array.prototype.forEach.call(document.querySelectorAll(sel + ' button'), function (b) {
            b.onclick = function () {
                setPref(key, cast(b.getAttribute('data-v')));
            };
        });
    }

    function buildThemeDots() {
        var box = $('#themeDots');
        box.innerHTML = '';
        THEMES.forEach(function (t) {
            var d = document.createElement('div');
            d.className = 'theme-dot' + (state.prefs.theme === t.key ? ' sel' : '');
            d.style.background = t.color;
            d.innerHTML = '<span>' + t.label + '</span>';
            d.onclick = function () {
                setPref('theme', t.key);
            };
            box.appendChild(d);
        });
    }

    function savePrefs() {
        var p = state.prefs;
        fetch('/api/reader/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                fontSize: p.fontSize, lineHeight: p.lineHeight,
                justify: p.justify, theme: p.theme, margin: p.margin,
                paraSpace: p.paraSpace, indent: p.indent,
                font: p.font, bold: p.bold
            })
        }).catch(function () { });
    }

    function setPref(key, val) {
        state.prefs[key] = val;
        savePrefs();
        applyPrefs(state.prefs);
    }

    /* ---------------- 书架 ---------------- */
    var PALETTE = ['#5B8DEF', '#7C6BF5', '#E0685F', '#2FB6A6', '#E2953F', '#4E9FD1', '#B36BD6', '#3F8F6E'];

    function coverColor(name) {
        var h = 0;
        for (var i = 0; i < (name || '').length; i++) h = (h * 31 + name.charCodeAt(i)) & 0x7fffffff;
        return PALETTE[h % PALETTE.length];
    }

    function initial(name) {
        for (var i = 0; i < (name || '').length; i++) {
            var c = name.charAt(i);
            if (/[a-zA-Z0-9\u2E80-\u9FFF]/.test(c)) return c.toUpperCase();
        }
        return '书';
    }

    function loadShelf() {
        return api('/api/reader/shelf').then(function (j) {
            state.shelf = j.items || [];
            renderShelf();
        }).catch(function (e) {
            $('#shelfSub').textContent = e.message;
        });
    }

    function renderShelf() {
        var box = $('#shelfList');
        box.innerHTML = '';

        var local = state.shelf.filter(function (b) { return b.type === 'local'; }).length;
        var online = state.shelf.length - local;
        $('#shelfSub').textContent = state.shelf.length === 0
            ? '本地与网络书籍都放这里'
            : '共 ' + state.shelf.length + ' 本 · 本地 ' + local + (online ? ' · 在线 ' + online : '');

        if (!state.shelf.length) {
            box.innerHTML = '<div class="empty-shelf"><span class="big">' + WI.svg('books') + '</span>书架还是空的<br>点「上传」选 TXT / EPUB / PDF / FB2 / MD，<br>或在「书源」里搜网络小说</div>';
            return;
        }

        state.shelf.forEach(function (b) {
            var card = document.createElement('div');
            card.className = 'book-card' + (state.book && state.book.id === b.id ? ' active' : '');
            card.setAttribute('data-id', b.id);

            var sub = (b.author ? b.author : (b.type === 'local' ? '本地书籍' : '网络书籍'));
            if (b.progress > 0.001) sub += ' · ' + Math.round(b.progress * 100) + '%';

            // PDF 用第一页作封面；服务端已提前缓存，这里只是一次普通的图片请求
            var coverInner = b.cover
                ? '<img class="cover-img" src="' + esc(b.cover) + '" alt="" onerror="this.remove()">'
                : esc(initial(b.name));
            card.innerHTML =
                '<div class="book-cover" style="background:linear-gradient(160deg,' + coverColor(b.name) + ',' + shade(coverColor(b.name)) + ')">'
                + coverInner
                + '<span class="bar"><i style="width:' + Math.round((b.progress || 0) * 100) + '%"></i></span>'
                + '</div>'
                + '<div class="book-meta"><div class="book-name">' + esc(b.name) + '</div>'
                + '<div class="book-sub">' + esc(sub) + '</div></div>'
                + '<div class="book-more" title="更多">⋯</div>';

            card.onclick = function (ev) {
                if (ev.target.classList.contains('book-more')) {
                    ev.stopPropagation();
                    bookMenu(b);
                    return;
                }
                openBook(b.id, true);
            };
            box.appendChild(card);
        });
    }

    function shade(hex) {
        var n = parseInt(hex.slice(1), 16);
        var r = Math.round(((n >> 16) & 255) * 0.72);
        var g = Math.round(((n >> 8) & 255) * 0.72);
        var b = Math.round((n & 255) * 0.72);
        return 'rgb(' + r + ',' + g + ',' + b + ')';
    }

    function bookMenu(b) {
        var acts = [{ t: '重命名', k: 'ren' }];
        if (b.type === 'online') acts.push({ t: '换源（同名书换一个书源）', k: 'chg' });
        if (state.book && state.book.id === b.id) acts.push({ t: '书签', k: 'mk' });
        acts.push({ t: '删除', k: 'del', danger: true });

        menuBox(b.name, acts).then(function (k) {
            if (k === 'chg') { startChangeSource(b); return; }
            if (k === 'mk') { openPanel('marksPanel'); return; }
            if (k === 'ren') {
                ask('重命名', b.name).then(function (name) {
                    if (name === null) return;
                    name = name.trim();
                    if (!name) { toast('书名不能为空'); return; }
                    return api('/api/reader/rename?id=' + enc(b.id) + '&name=' + enc(name), { method: 'POST' })
                        .then(function () {
                            if (state.book && state.book.id === b.id) {
                                state.book.name = name;
                                $('#bookTitle').textContent = name;
                            }
                            toast('已重命名');
                            return loadShelf();
                        });
                }).catch(function (e) { toast(e.message); });
            } else if (k === 'del') {
                confirmBox('删除书籍', '确定删除「' + b.name + '」吗？' + (b.type === 'local' ? '\n本地文件副本也会删除。' : ''), '删除')
                    .then(function (yes) {
                        if (!yes) return;
                        return api('/api/reader/delete?id=' + enc(b.id), { method: 'POST' }).then(function () {
                            if (state.book && state.book.id === b.id) resetReader();
                            toast('已删除');
                            return loadShelf();
                        });
                    }).catch(function (e) { toast(e.message); });
            }
        });
    }

    /* ---------------- 打开 / 阅读 ---------------- */
    function openBook(id, restore) {
        toast('正在打开…', 1200);
        api('/api/reader/open?id=' + enc(id)).then(function (j) {
            // 文档书（docx/xlsx/pptx...）：交给 viewer 打开，不进章节阅读器
            if (j.type === 'viewer') {
                var page = docViewerPage(j.format, false);
                openViewer(page, j.name, j.format, 'book:' + j.id, false);
                return;
            }
            state.book = j;
            state.pdf = j.format === 'pdf';
            state.total = j.total;
            state.i = j.chapter;
            $('#bookTitle').textContent = j.name;
            $('#bookTitle').title = j.name;
            renderToc(j.chapters);
            highlightShelf();
            closeSidebar();
            return loadChapter(j.chapter, restore ? j.pct : 0);
        }).catch(function (e) { toast(e.message, 3000); });
    }

    function highlightShelf() {
        Array.prototype.forEach.call(document.querySelectorAll('.book-card'), function (c) {
            c.classList.toggle('active', state.book && c.getAttribute('data-id') === state.book.id);
        });
    }

    function loadChapter(i, pct) {
        if (!state.book) return Promise.resolve();
        if (state.pdf) return loadPdfPage(i, pct);
        // 从 PDF 切回文本书：恢复正文视图
        $('#content').hidden = false;
        $('#pdfView').hidden = true;
        $('#reader').classList.remove('pdf-mode');
        var box = $('#content');
        box.innerHTML = '<p style="text-indent:0;color:var(--sub);text-align:center;padding:40px 0">正在加载…</p>';

        return api('/api/reader/chapter?id=' + enc(state.book.id) + '&i=' + i).then(function (j) {
            state.i = j.i;
            state.total = j.total;
            state.docChaps = [j.i];
            state.appendStop = false;
            $('#chapterTitle').textContent = j.title;
            renderContent(j.title, j.text, j.i);
            updateNav();
            if (pct > 0.002) {
                state.restoring = true;
                requestAnimationFrame(function () {
                    scrollToPct(pct);
                    setTimeout(function () { state.restoring = false; }, 160);
                });
            } else {
                $('#reader').scrollTop = 0;
            }
            state.pct = pct || 0;
            updateProgressBar();
            saveProgress();
            // 本章不足一屏（无法滚动）时直接续下一章
            var r = $('#reader');
            if (r.scrollHeight <= r.clientHeight + 8) setTimeout(maybeAppend, 80);
        }).catch(function (e) {
            box.innerHTML = '<p style="text-indent:0;color:var(--sub);text-align:center;padding:40px 0">' + esc(e.message) + '</p>';
        });
    }

    /* ---------------- PDF：逐页无缝纵向拼接 ---------------- */
    /**
     * PDF 与 App 端一致：把每一页按服务端渲染的 JPEG 顺序纵向拼接，
     * 上下滚动即连续翻页。页码 = 章号，进度按当前可见页写回同一份 shelf.json。
     * 为避免一次性渲染上千页，先渲染起始页起的一小批，接近底部时再续拼。
     */
    var PDF_BATCH = 4;                 // 每批拼页数
    var pdfRenderedFrom = -1, pdfRenderedTo = -1, pdfAppending = false;

    function pdfPageUrl(i) {
        var w = Math.max(480, Math.min(1600, Math.round($('#reader').clientWidth || 900)));
        return '/api/reader/pdf?id=' + enc(state.book.id) + '&i=' + i + '&w=' + w;
    }

    function appendPdfPages(from, count) {
        var box = $('#pdfPages');
        var end = Math.min(state.total, from + count);
        for (var i = from; i < end; i++) {
            var img = document.createElement('img');
            img.className = 'pdf-page';
            img.alt = '第 ' + (i + 1) + ' 页';
            img.setAttribute('data-i', i);
            img.loading = i > from + 1 ? 'lazy' : 'eager';
            img.src = pdfPageUrl(i);
            box.appendChild(img);
        }
        pdfRenderedFrom = pdfRenderedFrom < 0 ? from : Math.min(pdfRenderedFrom, from);
        pdfRenderedTo = Math.max(pdfRenderedTo, end);
    }

    function maybeAppendPdf() {
        if (!state.book || !state.pdf || pdfAppending) return;
        if (pdfRenderedTo >= state.total) return;
        var r = readerBox();
        // 接近已拼接内容底部时续拼下一批
        if (r.scrollTop + r.clientHeight < r.scrollHeight - r.clientHeight * 0.8) return;
        pdfAppending = true;
        appendPdfPages(pdfRenderedTo, PDF_BATCH);
        pdfAppending = false;
    }

    function syncPdfFromScroll() {
        if (!state.book || !state.pdf) return;
        var r = readerBox();
        var y = r.scrollTop;
        var pages = $('#pdfPages').querySelectorAll('.pdf-page');
        var cur = pdfRenderedFrom < 0 ? state.i : pdfRenderedFrom;
        var tol = 40;
        for (var k = 0; k < pages.length; k++) {
            var top = pages[k].offsetTop;
            if (top <= y + tol) cur = parseInt(pages[k].getAttribute('data-i'), 10);
        }
        if (!isNaN(cur) && cur !== state.i) {
            state.i = cur;
            $('#chapterTitle').textContent = chapterTitleOf(cur) || ('第 ' + (cur + 1) + ' 页');
            updateNav();
            renderTocMarks();
        }
        var page = pages.length ? pages[Math.max(0, cur - pdfRenderedFrom)] : null;
        if (page) {
            var top = page.offsetTop, h = Math.max(1, page.offsetHeight);
            state.pct = Math.max(0, Math.min(1, (y - top) / h));
        }
        updateProgressBar();
        maybeAppendPdf();
    }

    function loadPdfPage(i, pct) {
        if (!state.book) return Promise.resolve();
        i = Math.max(0, Math.min(i, state.total - 1));
        state.i = i;
        state.pct = pct || 0;
        state.docChaps = [i];
        state.appendStop = false;

        $('#content').hidden = true;
        var pv = $('#pdfView');
        pv.hidden = false;
        $('#reader').classList.add('pdf-mode');

        // 重建拼接：从起始页渲染一批；pct>0 表示恢复进度，定位到该页相应位置
        var from = i;
        if (pct && pct > 0.05 && i > 0) from = i - 1;   // 带上前一页，便于向上连贯滚动
        pdfRenderedFrom = -1; pdfRenderedTo = -1; pdfAppending = false;
        $('#pdfPages').innerHTML = '';
        appendPdfPages(from, PDF_BATCH);

        $('#chapterTitle').textContent = chapterTitleOf(i) || ('第 ' + (i + 1) + ' 页');
        updateNav();
        updateProgressBar();
        renderTocMarks();
        saveProgress();

        // 图片加载完成后按 pct 定位滚动位置（PDF 按页进度）
        setTimeout(function () {
            var page = $('#pdfPages').querySelector('.pdf-page[data-i="' + i + '"]');
            var r = readerBox();
            if (page) r.scrollTop = page.offsetTop + Math.round((pct || 0) * page.offsetHeight) - 4;
            else r.scrollTop = 0;
        }, 60);
        return Promise.resolve();
    }

    function renderContent(title, text, i) {
        $('#content').innerHTML = chapterHtml(title, text, i);
    }

    /// 单章正文 HTML（章名 + 段落），连读时逐章追加成一节
    function chapterHtml(title, text, i) {
        var lines = String(text || '').split('\n');
        var first = '';
        for (var k = 0; k < lines.length; k++) if (lines[k].trim()) { first = lines[k].trim(); break; }

        var html = '';
        var titleInBody = title && first && first.replace(/\s/g, '').indexOf(title.replace(/\s/g, '')) === 0;
        if (!titleInBody && title) {
            html += '<div class="chapter-head"><div class="t">' + esc(title) + '</div><div class="l"></div></div>';
        }
        var count = 0;
        for (var m = 0; m < lines.length; m++) {
            var p = lines[m].replace(/^[\s\u3000]+|[\s\u3000]+$/g, '');
            if (!p) continue;
            html += '<p>' + esc(p) + '</p>';
            count++;
        }
        if (!count) html += '<p style="text-indent:0;color:var(--sub)">本章暂无正文，可能是付费章节或书源规则失效</p>';
        return '<section class="ch" data-i="' + i + '">' + html + '</section>';
    }

    /* ---------------- 连读：章尾自动拼接 ---------------- */

    function readerBox() { return $('#reader'); }

    function secTop(el) {
        var r = readerBox();
        return el.getBoundingClientRect().top - r.getBoundingClientRect().top + r.scrollTop;
    }

    function chapterTitleOf(i) {
        var list = (state.book && state.book.chapters) || [];
        for (var k = 0; k < list.length; k++)
            if (list[k].i === i) return list[k].title || list[k].t || '';
        return '';
    }

    /// 按滚动位置反推「当前在第几章 + 章内比例」，并触发续章
    function syncFromScroll() {
        if (!state.book) return;
        if (state.pdf) { syncPdfFromScroll(); return; }   // PDF：按可见页同步进度并续拼
        var r = readerBox(), y = r.scrollTop;
        var secs = Array.prototype.slice.call($('#content').querySelectorAll('.ch'));
        if (!secs.length) return;

        var tops = secs.map(function (s) { return secTop(s); });
        var tol = Math.max(40, Math.round(r.clientHeight / 14));   // 章名已到屏幕上方时算作新章
        var pos = 0;
        for (var k = 0; k < tops.length; k++) if (tops[k] <= y + tol) pos = k;

        var top = tops[pos];
        var nextTop = pos + 1 < tops.length ? tops[pos + 1] : r.scrollHeight;
        var span = Math.max(1, nextTop - top);
        state.pct = Math.max(0, Math.min(1, (y - top) / span));

        var ci = parseInt(secs[pos].getAttribute('data-i'), 10);
        if (!isNaN(ci) && ci !== state.i) {
            state.i = ci;
            $('#chapterTitle').textContent = chapterTitleOf(ci) || $('#chapterTitle').textContent;
            updateNav();
            renderTocMarks();
        }
        updateProgressBar();

        if (!state.restoring && y + r.clientHeight > r.scrollHeight - r.clientHeight * 1.2) maybeAppend();
    }

    function maybeAppend() {
        if (state.pdf) return;   // PDF 续拼接管交给 syncPdfFromScroll，避免重复追加
        if (!state.autoAppend || state.appending || state.appendStop || !state.book) return;
        // 一份文档最多拼 12 章，到上限就从当前章重新开文档，避免 DOM 无限膨胀
        if (state.docChaps.length >= 12) {
            if (state.i < state.total - 1) goChapter(state.i + 1);
            else state.appendStop = true;
            return;
        }
        var last = state.docChaps.length ? state.docChaps[state.docChaps.length - 1] : state.i;
        var nxt = last + 1;
        if (nxt < 0 || nxt >= state.total) return;
        if (state.docChaps.indexOf(nxt) >= 0) return;

        state.appending = true;
        api('/api/reader/chapter?id=' + enc(state.book.id) + '&i=' + nxt).then(function (j) {
            $('#content').insertAdjacentHTML('beforeend', chapterHtml(j.title, j.text, j.i));
            state.docChaps.push(j.i);
            if (j.total) state.total = j.total;
            state.appending = false;
            updateNav();
            var r = readerBox();
            if (r.scrollHeight <= r.clientHeight + 8) setTimeout(maybeAppend, 60);
        }).catch(function () {
            state.appending = false;
            state.appendStop = true;   // 失败就停，别反复请求
        });
    }

    function updateNav() {
        $('#progressText').textContent = state.total ? (state.i + 1) + ' / ' + state.total : '— / —';
        $('#btnPrev').textContent = state.pdf ? '上一页' : '上一章';
        $('#btnNext').textContent = state.pdf ? '下一页' : '下一章';
        $('#btnPrev').disabled = state.i <= 0;
        $('#btnNext').disabled = state.i >= state.total - 1;
    }

    function updateProgressBar() {
        var p = state.total ? Math.min(1, (state.i + state.pct) / state.total) : 0;
        $('#progressBar').style.width = Math.round(p * 100) + '%';
    }

    function scrollToPct(pct) {
        var r = $('#reader');
        var max = r.scrollHeight - r.clientHeight;
        if (max <= 0) return;
        r.scrollTop = Math.round(max * Math.max(0, Math.min(1, pct)));
    }

    function currentPct() {
        var r = $('#reader');
        var max = r.scrollHeight - r.clientHeight;
        if (max <= 0) return 0;
        return Math.max(0, Math.min(1, r.scrollTop / max));
    }

    var saveTimer = null;
    function saveProgress() {
        if (!state.book) return;
        var body = '?id=' + enc(state.book.id) + '&i=' + state.i + '&pct=' + state.pct.toFixed(4) + '&total=' + state.total;
        var url = '/api/reader/progress' + body;
        if (navigator.sendBeacon) {
            try { navigator.sendBeacon(url); return; } catch (e) { }
        }
        fetch(url, { method: 'POST' }).catch(function () { });
    }

    function resetReader() {
        state.book = null; state.i = 0; state.total = 0; state.pct = 0;
        state.docChaps = []; state.appendStop = false; state.appending = false;
        state.pdf = false;
        $('#content').hidden = false;
        $('#pdfView').hidden = true;
        $('#reader').classList.remove('pdf-mode');
        $('#bookTitle').textContent = '选择一本书开始阅读';
        $('#chapterTitle').textContent = '';
        $('#content').innerHTML = '<div class="welcome"><div class="welcome-ico">' + WI.svg('book') + '</div><h2>网页端阅读器</h2><p>从左侧书架选一本继续读。</p></div>';
        updateNav();
        $('#progressBar').style.width = '0';
    }

    function goChapter(i) {
        if (!state.book) return;
        i = Math.max(0, Math.min(i, state.total - 1));
        if (i === state.i) return;
        // PDF 连续拼接：目标页已在已渲染批次内 → 直接滚动过去，不重建 DOM
        if (state.pdf && i >= pdfRenderedFrom && i < pdfRenderedTo) {
            var target = $('#pdfPages').querySelector('.pdf-page[data-i="' + i + '"]');
            if (target) {
                readerBox().scrollTop = Math.max(0, target.offsetTop - 2);
                state.i = i; state.pct = 0;
                $('#chapterTitle').textContent = chapterTitleOf(i) || ('第 ' + (i + 1) + ' 页');
                updateNav(); updateProgressBar(); renderTocMarks(); saveProgress();
                return;
            }
        }
        loadChapter(i, 0).then(function () {
            renderTocMarks();
            if ($('#tocPanel').classList.contains('open')) {
                var el = document.querySelector('.toc-item.cur');
                if (el) el.scrollIntoView({ block: 'center' });
            }
        });
    }

    /* ---------------- 目录 ---------------- */
    var tocData = [];
    function renderToc(list) {
        tocData = list || [];
        $('#tocSearch').placeholder = state.pdf ? '输入页码，如 12' : '搜索章节名';
        drawToc('');
    }

    function drawToc(kw) {
        var box = $('#tocList');
        box.innerHTML = '';
        var k = (kw || '').trim();
        var frag = document.createDocumentFragment();
        // 上千页的 PDF 只渲染当前页附近的一段，避免一次性建几千个 DOM 节点
        var from = 0, to = tocData.length;
        if (state.pdf && !k && tocData.length > 400) {
            from = Math.max(0, state.i - 120);
            to = Math.min(tocData.length, state.i + 280);
        }
        for (var idx = from; idx < to; idx++) {
            var c = tocData[idx];
            if (k && String(c.t).toLowerCase().indexOf(k.toLowerCase()) < 0) continue;
            var row = document.createElement('div');
            row.className = 'toc-item' + (c.i === state.i ? ' cur' : '');
            row.innerHTML = '<span class="n">' + (c.i + 1) + '</span><span class="t">' + esc(c.t) + '</span>';
            row.onclick = (function (n) {
                return function () { closeDrawers(); goChapter(n); };
            })(c.i);
            frag.appendChild(row);
        }
        box.appendChild(frag);
    }

    function renderTocMarks() {
        Array.prototype.forEach.call(document.querySelectorAll('.toc-item'), function (el, idx) {
            el.classList.remove('cur');
        });
        drawToc($('#tocSearch').value);
    }

    /* ---------------- 上传 ---------------- */
    function uploadFiles(files) {
        if (!files || !files.length) return;
        var list = Array.prototype.slice.call(files);
        (function next() {
            var f = list.shift();
            if (!f) { loadShelf().then(function () { toast('上传完成'); }); return; }
            toast('正在上传 ' + f.name + '…', 60000);
            fetch('/api/reader/upload?name=' + enc(f.name), {
                method: 'POST',
                headers: { 'Content-Type': 'application/octet-stream' },
                body: f
            }).then(function (r) { return r.json(); }).then(function (j) {
                if (!j.ok) toast(f.name + ' 上传失败：' + j.error, 3000);
                else toast('已加入书架：' + j.name);
                next();
            }).catch(function (e) {
                toast(f.name + ' 上传失败：' + e.message, 3000);
                next();
            });
        })();
    }

    /* ---------------- 书源 ---------------- */
    function loadSources() {
        return api('/api/reader/sources').then(function (j) {
            var box = $('#srcList');
            var items = j.items || [];
            box.innerHTML = '';
            if (!items.length) {
                box.innerHTML = '<div style="color:var(--sub);font-size:12.5px;line-height:1.9;padding:8px 2px">还没有书源。<br>把 legado 书源 JSON 粘到下面，或选一个 .json 文件导入。</div>';
                return;
            }
            items.forEach(function (s) {
                var row = document.createElement('div');
                row.className = 'src-item';
                row.innerHTML = '<div class="nm">' + esc(s.name) + (s.group ? ' <span class="gp">(' + esc(s.group) + ')</span>' : '') + '</div>'
                    + '<div class="switch' + (s.enabled ? ' on' : '') + '"><i></i></div>'
                    + '<div class="del">删除</div>';
                row.querySelector('.switch').onclick = function () {
                    api('/api/reader/source/toggle?id=' + enc(s.url) + '&on=' + (s.enabled ? 0 : 1), { method: 'POST' })
                        .then(loadSources).catch(function (e) { toast(e.message); });
                };
                row.querySelector('.del').onclick = function () {
                    api('/api/reader/source/delete?id=' + enc(s.url), { method: 'POST' })
                        .then(function () { toast('已删除'); return loadSources(); })
                        .catch(function (e) { toast(e.message); });
                };
                box.appendChild(row);
            });
        }).catch(function (e) { toast(e.message); });
    }

    function importSources(text) {
        if (!text || !text.trim()) { toast('内容为空'); return; }
        toast('正在导入…', 5000);
        fetch('/api/reader/source', { method: 'POST', body: text })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (!j.ok) { toast(j.error || '导入失败', 3500); return; }
                toast('已导入/更新 ' + j.count + ' 个书源');
                $('#srcText').value = '';
                loadSources();
            }).catch(function (e) { toast(e.message, 3000); });
    }

    /* ---------------- 搜索 ---------------- */
    function doSearch() {
        var key = $('#searchKey').value.trim();
        if (!key) { toast('请输入关键词'); return; }
        var box = $('#searchList');
        box.innerHTML = '<div style="color:var(--sub);font-size:12.5px;padding:10px 2px">正在搜索…（书源多时可能要十几秒）</div>';
        fetch('/api/reader/search?key=' + enc(key), { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (!j.ok) { box.innerHTML = '<div style="color:var(--sub);font-size:12.5px;padding:10px 2px">' + esc(j.error) + '</div>'; return; }
                var items = j.items || [];
                if (!items.length) { box.innerHTML = '<div style="color:var(--sub);font-size:12.5px;padding:10px 2px">没有找到结果</div>'; return; }
                box.innerHTML = '';
                items.forEach(function (b) {
                    var rep = !!state.replaceId;
                    var row = document.createElement('div');
                    row.className = 'src-item ex-item';
                    row.innerHTML = '<div class="nm">' + esc(b.name)
                        + '<span class="gp">' + esc(b.author || '佚名') + ' · ' + esc(b.sourceName) + '</span></div>'
                        + '<div class="act">' + (rep ? '换到这本' : '加入') + '</div>';
                    row.onclick = function () {
                        if (rep) changeSourceTo(b); else addBook(b);
                    };
                    box.appendChild(row);
                });
            }).catch(function (e) { box.innerHTML = '<div style="padding:10px">' + esc(e.message) + '</div>'; });
    }

    /* ---------------- 加入书架（搜索 / 发现共用） ---------------- */
    function addBook(b) {
        return api('/api/reader/add?source=' + enc(b.source) + '&url=' + enc(b.url)
            + '&name=' + enc(b.name) + '&author=' + enc(b.author || ''), { method: 'POST' })
            .then(function (r) {
                closeModals(); toast('已加入书架');
                return loadShelf().then(function () { openBook(r.id, false); });
            }).catch(function (e) { toast(e.message); });
    }

    /* ---------------- 换源 ---------------- */
    function startChangeSource(b) {
        state.replaceId = b.id;
        $('#searchKey').value = b.name || '';
        openPanel('searchPanel');
        $('#searchList').innerHTML = '<div class="ex-hint">已在「换源」模式：搜索后点一本同名书即可把当前书切到那个源（保留书架位置与进度）。</div>';
        if ((b.name || '').trim()) doSearch();
    }

    function changeSourceTo(b) {
        var id = state.replaceId;
        if (!id) return;
        api('/api/reader/change?id=' + enc(id) + '&source=' + enc(b.source) + '&url=' + enc(b.url)
            + '&name=' + enc(b.name) + '&author=' + enc(b.author || ''), { method: 'POST' })
            .then(function () {
                state.replaceId = null;
                closeModals(); toast('已换源');
                return loadShelf().then(function () {
                    if (state.book && state.book.id === id) openBook(id, true);
                });
            }).catch(function (e) { toast(e.message); });
    }

    /* ---------------- 发现（探索分类） ---------------- */
    function loadExplore() {
        var box = $('#exList');
        $('#exTitle').textContent = '发现';
        $('#exCrumbs').innerHTML = '';
        box.innerHTML = '<div class="ex-hint">正在读取书源的发现分类…</div>';
        state.ex = { sources: [], src: null, cats: [], cat: 0, page: 1 };

        api('/api/reader/explore').then(function (j) {
            state.ex.sources = j.sources || [];
            if (!state.ex.sources.length) {
                box.innerHTML = '<div class="ex-hint">当前书源都没有配置「发现」分类。<br>导入带 exploreUrl 的书源后这里会自动出现。</div>';
                return;
            }
            box.innerHTML = '';
            drawExploreCrumbs();
            state.ex.sources.forEach(function (s) {
                var row = document.createElement('div');
                row.className = 'src-item ex-item';
                row.innerHTML = '<div class="nm">' + esc(s.name)
                    + '<span class="gp">' + (s.cats || []).length + ' 个分类</span></div>'
                    + '<div class="act">进入 ›</div>';
                row.onclick = function () { openExploreSource(s); };
                box.appendChild(row);
            });
        }).catch(function (e) {
            box.innerHTML = '<div class="ex-hint">' + esc(e.message) + '</div>';
        });
    }

    function drawExploreCrumbs() {
        var bar = $('#exCrumbs');
        bar.innerHTML = '';
        var ex = state.ex;

        var home = document.createElement('button');
        home.className = 'ex-crumb' + (ex.src ? '' : ' sel');
        home.textContent = '书源';
        home.onclick = function () { loadExplore(); };
        bar.appendChild(home);

        if (!ex.src) return;

        (ex.cats || []).forEach(function (c, idx) {
            var b = document.createElement('button');
            b.className = 'ex-crumb' + (ex.cat === idx && ex.page === 1 ? ' sel' : '');
            b.textContent = c.title;
            b.title = c.title;
            b.onclick = function () { loadExploreBooks(idx, 1); };
            bar.appendChild(b);
        });
    }

    function openExploreSource(s) {
        state.ex.src = s;
        state.ex.cats = s.cats || [];
        state.ex.cat = 0;
        state.ex.page = 1;
        $('#exTitle').textContent = s.name;
        drawExploreCrumbs();
        if (state.ex.cats.length) loadExploreBooks(0, 1);
        else $('#exList').innerHTML = '<div class="ex-hint">该源没有分类。</div>';
    }

    function loadExploreBooks(catIdx, page) {
        var ex = state.ex;
        if (!ex.src) return;
        ex.cat = catIdx;
        ex.page = page || 1;
        drawExploreCrumbs();
        var box = $('#exList');
        box.innerHTML = '<div class="ex-hint">正在加载第 ' + ex.page + ' 页…</div>';

        api('/api/reader/explore/books?source=' + enc(ex.src.url) + '&cat=' + catIdx + '&page=' + ex.page)
            .then(function (j) {
                box.innerHTML = '';
                var items = j.items || [];
                if (!items.length) {
                    box.innerHTML = '<div class="ex-hint">这一页没有书（可能已到最后一页）。</div>';
                    return;
                }
                items.forEach(function (b) {
                    var row = document.createElement('div');
                    row.className = 'src-item ex-item';
                    var sub = esc(b.author || '佚名');
                    if (b.kind) sub += ' · ' + esc(b.kind);
                    if (b.lastChapter) sub += ' · ' + esc(b.lastChapter);
                    row.innerHTML = '<div class="nm">' + esc(b.name) + '<span class="gp">' + sub + '</span></div>'
                        + '<div class="act">加入</div>';
                    row.querySelector('.act').onclick = function (ev) { ev.stopPropagation(); addBook(b); };
                    row.onclick = function () { addBook(b); };
                    box.appendChild(row);
                });
                if (items.length >= 10) {
                    var more = document.createElement('div');
                    more.className = 'ex-more';
                    more.textContent = '下一页 ›';
                    more.onclick = function () { loadExploreBooks(catIdx, ex.page + 1); };
                    box.appendChild(more);
                }
            }).catch(function (e) {
                box.innerHTML = '<div class="ex-hint">' + esc(e.message) + '</div>';
            });
    }

    /* ---------------- 书签 ---------------- */
    function loadMarks() {
        var box = $('#marksList');
        if (!state.book) {
            box.innerHTML = '<div class="ex-hint">先打开一本书，再添加书签。</div>';
            return;
        }
        box.innerHTML = '<div class="ex-hint">正在加载…</div>';
        api('/api/reader/marks?id=' + enc(state.book.id)).then(function (j) {
            var items = j.items || [];
            box.innerHTML = '';
            if (!items.length) {
                box.innerHTML = '<div class="ex-hint">还没有书签。<br>点上面的按钮把当前章节位置记下来。</div>';
                return;
            }
            items.forEach(function (m) {
                var row = document.createElement('div');
                row.className = 'src-item mk-item';
                row.innerHTML = '<div class="nm">' + esc(m.title || ('第 ' + (m.chapter + 1) + ' 章'))
                    + '<span class="gp">' + esc(m.created) + ' · ' + Math.round((m.pct || 0) * 100) + '%'
                    + (m.text ? '<br>' + esc(m.text) : '') + '</span></div>'
                    + '<div class="mk-del">删除</div>';
                row.onclick = function () {
                    closeModals();
                    if (m.chapter === state.i) { scrollToPct(m.pct || 0); }
                    else goChapter(m.chapter).then(function () { scrollToPct(m.pct || 0); });
                };
                row.querySelector('.mk-del').onclick = function (ev) {
                    ev.stopPropagation();
                    api('/api/reader/mark/del?id=' + enc(state.book.id) + '&mid=' + enc(m.id), { method: 'POST' })
                        .then(function () { toast('已删除书签'); loadMarks(); })
                        .catch(function (e) { toast(e.message); });
                };
                box.appendChild(row);
            });
        }).catch(function (e) {
            box.innerHTML = '<div class="ex-hint">' + esc(e.message) + '</div>';
        });
    }

    function addMark() {
        if (!state.book) { toast('先打开一本书'); return; }
        var sel = window.getSelection();
        var picked = sel && !sel.isCollapsed ? String(sel).trim().slice(0, 90) : '';
        syncFromScroll();
        var title = $('#chapterTitle').textContent || '';
        var pct = state.pct.toFixed(4);
        api('/api/reader/mark/add?id=' + enc(state.book.id) + '&i=' + state.i
            + '&pct=' + pct + '&title=' + enc(title) + '&text=' + enc(picked), { method: 'POST' })
            .then(function () { toast('已添加书签'); loadMarks(); })
            .catch(function (e) { toast(e.message); });
    }


    /* ---------------- 打开浏览器本地文档（docx/xlsx/pptx/doc/xls/ppt/pdf/txt） ---------------- */
    function docViewerPage(ext, slide) {
        ext = (ext || '').toLowerCase();
        if (ext === 'xlsx' || ext === 'xls' || ext === 'xlsm') return 'xlsx.html';
        if (ext === 'pptx' || ext === 'pptm') return 'pptx.html';   // 放映用同一页面 + ?slide=1
        if (ext === 'ppt' || ext === 'pps') return 'text.html';     // 旧二进制 PPT 按文本读
        if (ext === 'doc' || ext === 'dot') return 'prose.html';
        if (ext === 'pdf') return 'pdf.html';
        if (ext === 'txt' || ext === 'text' || ext === 'md') return 'text.html';
        return 'docx.html';
    }

    // 在本页面内嵌打开 viewer：本地文件把字节通过 postMessage 直接交给前端渲染，
    // 不上传、不生成 blob URL、不新开标签。书架文档(buffer=null)则让 iframe 内部按 path 从服务端取。
    var _docIframeReady = false;
    var _pendingDoc = null;
    function openViewer(page, name, ext, docUrl, slide, buffer) {
        var frame = $('#docFrame');
        var overlay = $('#docViewer');
        if (!frame || !overlay) return;
        $('#dvTitle').textContent = name || ext || '文档';
        overlay.hidden = false;
        var url = '/viewer/' + page + '?name=' + encodeURIComponent(name)
            + '&ext=' + encodeURIComponent(ext);
        if (buffer) {
            // 本地文件：inline=1 标记让 viewer 等 postMessage；字节在 iframe load 后送入。
            url += '&inline=1';
            _pendingDoc = buffer;
        } else {
            // 书架/共享文档：iframe 内部按 path 从服务端 /doc 取数，不等待 postMessage。
            _pendingDoc = null;
            url += '&path=' + encodeURIComponent(docUrl);
            // 大 PDF 走 HTTP Range 分片：服务端 /doc 已支持 206，pdf.mjs 见 ranged=1
            // 就按需加载，不再整本读进内存——大文件打开速度提升的关键。
            if ((ext || '').toLowerCase() === 'pdf') url += '&ranged=1';
        }
        if (slide) url += '&slide=1';
        _docIframeReady = false;
        frame.onload = function () {
            _docIframeReady = true;
            if (_pendingDoc) {
                try {
                    frame.contentWindow.postMessage({ __vwDoc: 1, buffer: _pendingDoc }, location.origin, [_pendingDoc]);
                } catch (e) {
                    // 老浏览器不支持 transfer list：退化为不转移（结构化克隆仍可传 ArrayBuffer）
                    try { frame.contentWindow.postMessage({ __vwDoc: 1, buffer: _pendingDoc }, location.origin); }
                    catch (e2) { toast('文档传递失败：' + e2.message, 3000); }
                }
                _pendingDoc = null;
            }
        };
        frame.src = url;
        try { overlay.scrollIntoView && overlay.scrollIntoView(); } catch (e) {}
    }

    function closeViewer() {
        var overlay = $('#docViewer'), frame = $('#docFrame');
        if (overlay) overlay.hidden = true;
        if (frame) frame.src = 'about:blank';
        if (overlay) overlay.classList.remove('dv-fullscreen');
        _pendingDoc = null; _docIframeReady = false;
    }

    function initDocViewerUI() {
        if ($('#dvClose')) $('#dvClose').onclick = closeViewer;
        if ($('#dvBack')) $('#dvBack').onclick = closeViewer;
        if ($('#dvFull')) $('#dvFull').onclick = function () {
            var overlay = $('#docViewer');
            if (!overlay) return;
            if (!document.fullscreenElement) { try { overlay.requestFullscreen(); } catch (e) {} }
            else { try { document.exitFullscreen(); } catch (e) {} }
        };
    }

    function uploadDocToShelf(f) {
        return fetch('/api/reader/upload?name=' + enc(f.name), {
            method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body: f
        }).then(function (r) { return r.json(); });
    }

    function readFileBuffer(f) {
        return new Promise(function (resolve, reject) {
            var fr = new FileReader();
            fr.onload = function () { resolve(fr.result); };
            fr.onerror = function () { reject(fr.error || new Error('读取失败')); };
            fr.readAsArrayBuffer(f);
        });
    }

    function openLocalDoc(files) {
        if (!files || !files.length) return;
        var f = files[0];
        var ext = (f.name.split('.').pop() || '').toLowerCase();
        var isPpt = (ext === 'pptx' || ext === 'pptm' || ext === 'ppt');
        var proceed = function (wantShelf, wantSlide) {
            toast('正在打开 ' + f.name + '…', 4000);
            readFileBuffer(f).then(function (buffer) {
                var page = docViewerPage(ext, wantSlide);
                // 把字节直接交给本页 iframe 渲染，不上传、不用 blob、不新开页面
                openViewer(page, f.name, ext, null, wantSlide, buffer);
                // 「特地要求」才存入书架：异步上传，成功仅刷新书架，不影响当前阅读
                if (wantShelf) {
                    uploadDocToShelf(f).then(function (sj) {
                        if (sj && sj.ok && sj.id) { loadShelf(); toast('已存入书架，可在书架中长期阅读'); }
                        else toast('未能存入书架：' + ((sj && sj.error) || '未知'), 3500);
                    }).catch(function () { toast('未能存入书架', 3000); });
                }
            }).catch(function (e) { toast('读取文件失败：' + e.message, 3000); });
        };
        if (isPpt) {
            confirmBox('PPT 放映', '是否进入全屏放映模式？（取消则普通阅读）', '开始放映').then(function (slide) {
                confirmBox('存入书架？', '确定=保存到此设备书架持续阅读；取消=仅本次阅读', '存入书架').then(function (shelf) {
                    proceed(shelf, slide);
                });
            });
        } else {
            confirmBox('存入书架？', '确定=保存到此设备书架持续阅读；取消=仅本次阅读', '存入书架').then(function (shelf) {
                proceed(shelf, false);
            });
        }
    }

    /* ---------------- 通用弹窗 ---------------- */
    function ask(title, value) {
        return new Promise(function (resolve) {
            var wrap = document.createElement('div');
            wrap.className = 'modal open';
            wrap.innerHTML = '<div class="modal-card" style="width:380px">'
                + '<div class="drawer-head"><h2>' + esc(title) + '</h2></div>'
                + '<input class="search-input" id="askInput">'
                + '<div style="display:flex;gap:8px;padding:6px 16px 16px">'
                + '<button class="pill" style="flex:1;padding:11px" data-a="0">取消</button>'
                + '<button class="primary-btn" style="flex:1;margin:0" data-a="1">确定</button></div></div>';
            document.body.appendChild(wrap);
            var input = wrap.querySelector('#askInput');
            input.value = value || '';
            setTimeout(function () { input.focus(); input.select(); }, 30);
            wrap.querySelector('[data-a="0"]').onclick = function () { wrap.remove(); resolve(null); };
            wrap.querySelector('[data-a="1"]').onclick = function () { var v = input.value; wrap.remove(); resolve(v); };
            input.onkeydown = function (e) { if (e.key === 'Enter') { var v = input.value; wrap.remove(); resolve(v); } };
        });
    }

    function menuBox(title, actions) {
        return new Promise(function (resolve) {
            var wrap = document.createElement('div');
            wrap.className = 'modal open';
            var rows = actions.map(function (a, i) {
                return '<div class="menu-row' + (a.danger ? ' danger' : '') + '" data-i="' + i + '">' + esc(a.t) + '</div>';
            }).join('');
            wrap.innerHTML = '<div class="modal-card" style="width:340px">'
                + '<div class="drawer-head"><h2 style="font-size:14px;font-weight:500;color:var(--sub)">' + esc(title) + '</h2></div>'
                + rows + '<div style="padding:8px 16px 16px"><button class="pill" style="width:100%;padding:11px" data-c="1">取消</button></div></div>';
            document.body.appendChild(wrap);
            wrap.querySelectorAll('.menu-row').forEach(function (r) {
                r.onclick = function () {
                    var k = actions[parseInt(r.getAttribute('data-i'), 10)].k;
                    wrap.remove(); resolve(k);
                };
            });
            wrap.querySelector('[data-c="1"]').onclick = function () { wrap.remove(); resolve(null); };
        });
    }

    function confirmBox(title, msg, okText) {
        return new Promise(function (resolve) {
            var wrap = document.createElement('div');
            wrap.className = 'modal open';
            wrap.innerHTML = '<div class="modal-card" style="width:360px">'
                + '<div class="drawer-head"><h2>' + esc(title) + '</h2></div>'
                + '<div style="padding:0 18px 14px;font-size:12.5px;color:var(--sub);line-height:1.9;white-space:pre-wrap">' + esc(msg) + '</div>'
                + '<div style="display:flex;gap:8px;padding:0 16px 16px">'
                + '<button class="pill" style="flex:1;padding:11px" data-a="0">取消</button>'
                + '<button class="primary-btn" style="flex:1;margin:0;background:#E0564B" data-a="1">' + esc(okText || '确定') + '</button></div></div>';
            document.body.appendChild(wrap);
            wrap.querySelector('[data-a="0"]').onclick = function () { wrap.remove(); resolve(false); };
            wrap.querySelector('[data-a="1"]').onclick = function () { wrap.remove(); resolve(true); };
        });
    }

    /* ---------------- 面板控制 ---------------- */
    function openPanel(id) {
        closeDrawers();
        var el = document.getElementById(id);
        if (!el) return;
        if (el.classList.contains('drawer')) {
            el.classList.add('open');
            $('#mask').classList.add('open');
        } else {
            el.classList.add('open');
            if (id === 'srcPanel') loadSources();
            else if (id === 'explorePanel') loadExplore();
            else if (id === 'marksPanel') loadMarks();
        }
    }

    function closeDrawers() {
        document.querySelectorAll('.drawer.open').forEach(function (d) { d.classList.remove('open'); });
        $('#mask').classList.remove('open');
        closeSidebar();
    }

    function closeModals() {
        document.querySelectorAll('.modal.open').forEach(function (m) { m.classList.remove('open'); });
    }

    function closeSidebar() { $('#sidebar').classList.remove('open'); }

    function toggleBars(hide) {
        var h = hide == null ? !$('#topbar').classList.contains('hidden') : hide;
        $('#topbar').classList.toggle('hidden', h);
        $('#bottombar').classList.toggle('hidden', h);
    }

    /* ---------------- 事件绑定 ---------------- */
    function bind() {
        $('#btnUpload').onclick = function () { $('#fileInput').click(); };
        $('#fileInput').onchange = function (e) {
            uploadFiles(e.target.files);
            e.target.value = '';
        };
        if ($('#btnOpenLocal')) $('#btnOpenLocal').onclick = function () { $('#localDocInput').click(); };
        if ($('#localDocInput')) $('#localDocInput').onchange = function (e) {
            openLocalDoc(e.target.files);
            e.target.value = '';
        };
        $('#btnSources').onclick = function () { openPanel('srcPanel'); };
        $('#btnExplore').onclick = function () { openPanel('explorePanel'); };
        $('#btnMarks').onclick = function () {
            if (!state.book) { toast('先打开一本书'); return; }
            openPanel('marksPanel');
        };
        $('#btnAddMark').onclick = addMark;
        $('#btnSearch').onclick = function () {
            state.replaceId = null;
            openPanel('searchPanel');
        };
        $('#btnToc').onclick = function () { openPanel('tocPanel'); };
        $('#btnAa').onclick = function () { openPanel('setPanel'); };
        $('#btnDownloadBook').onclick = function () {
            if (!state.book || !state.book.id) { toast('请先打开一本书'); return; }
            const id = state.book.id;
            const title = state.book.name || state.book.title || '小说';
            fetch('/api/media/vod/download?op=start&type=novel&id=' + encodeURIComponent(id) + '&title=' + encodeURIComponent(title))
                .then(function (r) { return r.json(); }).then(function (d) {
                    if (d && d.ok) toast('已加入下载队列，可在「下载」中心查看进度');
                    else toast('下载失败：' + ((d && d.error) || '未知'));
                }).catch(function () { toast('下载请求失败'); });
        };
        $('#btnBack').onclick = function () { resetReader(); loadShelf(); };
        $('#btnOpenSide').onclick = function () {
            $('#sidebar').classList.add('open');
            $('#mask').classList.add('open');
        };
        $('#btnCloseSide').onclick = closeSidebar;
        $('#mask').onclick = closeDrawers;

        // 点弹窗外壳关闭
        document.querySelectorAll('.modal').forEach(function (m) {
            m.addEventListener('click', function (e) { if (e.target === m) m.classList.remove('open'); });
        });

        document.querySelectorAll('[data-close]').forEach(function (b) {
            b.onclick = function () {
                document.getElementById(b.getAttribute('data-close')).classList.remove('open');
                closeDrawers();
            };
        });

        $('#btnPrev').onclick = function () { goChapter(state.i - 1); };
        $('#btnNext').onclick = function () { goChapter(state.i + 1); };

        $('#tocSearch').oninput = function (e) { drawToc(e.target.value); };

        $('#btnFontMinus').onclick = function () {
            setPref('fontSize', Math.max(13, state.prefs.fontSize - 1));
        };
        $('#btnFontPlus').onclick = function () {
            setPref('fontSize', Math.min(32, state.prefs.fontSize + 1));
        };

        segBind('#lineSeg', 'lineHeight', parseFloat);
        segBind('#marginSeg', 'margin', parseInt);
        segBind('#paraSeg', 'paraSpace', parseInt);
        segBind('#indentSeg', 'indent', parseInt);
        segBind('#justifySeg', 'justify', function (v) { return v === '1'; });
        segBind('#boldSeg', 'bold', function (v) { return v === '1'; });
        segBind('#fontSeg', 'font', function (v) { return v; });

        $('#btnPasteSrc').onclick = function () {
            if (navigator.clipboard && navigator.clipboard.readText) {
                navigator.clipboard.readText().then(function (t) { $('#srcText').value = t; toast('已粘贴'); })
                    .catch(function () { toast('无法读取剪贴板，请手动粘贴'); });
            } else toast('请手动粘贴到下面的输入框');
        };
        $('#btnImportSrc').onclick = function () { importSources($('#srcText').value); };
        $('#srcFile').onchange = function (e) {
            var f = e.target.files[0];
            if (!f) return;
            var reader = new FileReader();
            reader.onload = function () { importSources(String(reader.result || '')); };
            reader.readAsText(f);
            e.target.value = '';
        };
        $('#btnDoSearch').onclick = doSearch;
        $('#searchKey').onkeydown = function (e) { if (e.key === 'Enter') doSearch(); };

        // 滚动：反推当前章节与章内进度（防抖保存），接近章尾自动续下一章
        $('#reader').addEventListener('scroll', function () {
            syncFromScroll();
            if (state.restoring) return;
            clearTimeout(saveTimer);
            saveTimer = setTimeout(saveProgress, 700);
        }, { passive: true });

        // 点击正文中间 → 隐藏/显示工具栏（选中文字时不触发）
        var downX = 0, downY = 0, downT = 0;
        $('#reader').addEventListener('mousedown', function (e) {
            downX = e.clientX; downY = e.clientY; downT = Date.now();
        });
        $('#reader').addEventListener('mouseup', function (e) {
            if (Date.now() - downT > 320) return;
            if (Math.abs(e.clientX - downX) > 6 || Math.abs(e.clientY - downY) > 6) return;
            var sel = window.getSelection();
            if (sel && !sel.isCollapsed) return;
            var r = $('#reader').getBoundingClientRect();
            var rel = (e.clientX - r.left) / r.width;
            if (rel < 0.25) pageTurn(-1);
            else if (rel > 0.75) pageTurn(1);
            else toggleBars();
        });

        document.addEventListener('keydown', function (e) {
            if (/^(INPUT|TEXTAREA)$/.test(document.activeElement.tagName)) return;
            if (e.key === 'ArrowRight') pageTurn(1);
            else if (e.key === 'ArrowLeft') pageTurn(-1);
            else if (e.key === 'Escape') { closeDrawers(); closeModals(); }
        });

        window.addEventListener('beforeunload', saveProgress);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) loadShelf();
            else saveProgress();
        });
    }

    function pageTurn(dir) {
        if (state.pdf) { goChapter(state.i + dir); return; }   // PDF：一次翻一整页
        var r = $('#reader');
        var step = r.clientHeight * 0.9;
        var max = r.scrollHeight - r.clientHeight;
        var target = r.scrollTop + dir * step;

        if (dir < 0 && target <= 0) {
            var prev = $('#content').querySelector('.ch[data-i="' + (state.i - 1) + '"]');
            if (prev) { r.scrollTop = secTop(prev); return; }      // 已连读：回到上一章开头
            if (state.i > 0) { goChapter(state.i - 1).then(function () { scrollToPct(1); }); }
            else r.scrollTop = 0;
            return;
        }
        if (dir > 0 && target >= max) {
            var next = $('#content').querySelector('.ch[data-i="' + (state.i + 1) + '"]');
            if (next) { r.scrollTop = secTop(next); return; }      // 已连读：直接翻到下一章
            if (state.i < state.total - 1) goChapter(state.i + 1);
            else r.scrollTop = max;
            return;
        }
        r.scrollTop = Math.max(0, Math.min(max, target));
    }

    /* ---------------- 启动 ---------------- */
    function start() {
        bind();
        initDocViewerUI();
        applyPrefs(state.prefs);

        api('/api/reader/settings').then(function (j) {
            applyPrefs({
                fontSize: j.fontSize, lineHeight: j.lineHeight,
                justify: j.justify, theme: j.theme, margin: j.margin,
                paraSpace: j.paraSpace, indent: j.indent, font: j.font, bold: j.bold
            });
        }).catch(function () { });

        loadShelf();

        // 支持 ?book=<id>&i=<章> 直接打开（手机端"在网页打开"用）
        var q = new URLSearchParams(location.search);
        if (q.get('book')) {
            setTimeout(function () { openBook(q.get('book'), true); }, 260);
        }
        if (q.get('panel')) {
            setTimeout(function () { openPanel(q.get('panel')); }, 320);
        }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();

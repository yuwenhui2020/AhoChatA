/* ===================== 阅读模块 · 书籍解析 =====================
 * - parseTxt(text): 本地 TXT 章节切分（兼容常见「第X章/回/卷/节」等标记）
 * - parseEpub(arrayBuffer): 轻量 EPUB 解析（自实现 ZIP + deflate-raw 解压，无需第三方库）
 */
(function () {
    'use strict';

    function decode(bytes, enc) {
        try { return new TextDecoder(enc || 'utf-8').decode(bytes); }
        catch (e) { return new TextDecoder('utf-8').decode(bytes); }
    }

    /* ---------- TXT ---------- */
    var TXT_PATTERNS = [
        /^\s*第\s*[0-9零一二三四五六七八九十百千两]+\s*[章回卷节部篇集幕][：:．.　 ]?.*$/,
        /^\s*[卷]\s*[0-9零一二三四五六七八九十百千两]+[：:．.　 ]?.*$/,
        /^\s*Chapter\s+[0-9]+[：:．.　 ]?.*$/i,
        /^\s*CHAPTER\s+[0-9]+[：:．.　 ]?.*$/i,
        /^\s*[0-9]+\s*[\.、]\s*\S+/,
        /^\s*[（(][0-9零一二三四五六七八九十百千两]+[)）][：:．.　 ]?\S+/
    ];

    function isChapterLine(line) {
        var t = line.trim();
        if (t.length === 0 || t.length > 60) return false;
        for (var i = 0; i < TXT_PATTERNS.length; i++) if (TXT_PATTERNS[i].test(t)) return true;
        return false;
    }

    function textToHtml(text) {
        var lines = text.replace(/\r\n?/g, '\n').split('\n');
        var paras = [];
        var buf = [];
        function flush() {
            if (buf.length) {
                var p = buf.join('<br>').trim();
                if (p) paras.push('<p>' + p + '</p>');
                buf = [];
            }
        }
        for (var i = 0; i < lines.length; i++) {
            var ln = lines[i].trim();
            if (ln === '') flush();
            else buf.push(escapeHtml(ln));
        }
        flush();
        return paras.join('') || '<p>' + escapeHtml(text.trim()) + '</p>';
    }

    function escapeHtml(s) {
        return s.replace(/[&<>]/g, function (c) {
            return c === '&' ? '&amp;' : c === '<' ? '&lt;' : '&gt;';
        });
    }

    function parseTxt(text) {
        var raw = text.replace(/\r\n?/g, '\n');
        var lines = raw.split('\n');
        var markers = [];
        for (var i = 0; i < lines.length; i++) {
            if (isChapterLine(lines[i])) markers.push({ idx: i, title: lines[i].trim() });
        }
        var chapters = [];
        if (markers.length >= 2) {
            for (var m = 0; m < markers.length; m++) {
                var start = markers[m].idx + 1;
                var end = (m + 1 < markers.length) ? markers[m + 1].idx : lines.length;
                var body = lines.slice(start, end).join('\n');
                chapters.push({ title: markers[m].title, content: textToHtml(body) });
            }
        } else {
            // 未识别到明显分章：整体作为一卷，按空行/换行切块
            chapters.push({ title: '正文', content: textToHtml(raw) });
        }
        return { title: '', author: '', chapters: chapters };
    }

    /* ---------- EPUB (ZIP) ---------- */
    function inflateRaw(bytes) {
        if (typeof DecompressionStream === 'undefined') {
            return Promise.reject(new Error('当前 WebView 不支持 DecompressionStream，无法解压 EPUB'));
        }
        var ds = new DecompressionStream('deflate-raw');
        var writer = ds.writable.getWriter();
        writer.write(bytes); writer.close();
        var reader = ds.readable.getReader();
        var chunks = [], total = 0;
        return reader.read().then(function step(r) {
            if (r.done) {
                var out = new Uint8Array(total); var off = 0;
                for (var i = 0; i < chunks.length; i++) { out.set(chunks[i], off); off += chunks[i].length; }
                return out;
            }
            if (r.value) { chunks.push(r.value); total += r.value.length; }
            return reader.read().then(step);
        });
    }

    function readUint32LE(view, off) { return view.getUint32(off, true); }
    function readUint16LE(view, off) { return view.getUint16(off, true); }

    function findEOCD(buf) {
        // 从尾部向前搜索 EOCD 签名 50 4B 05 06
        for (var i = buf.byteLength - 22; i >= 0; i--) {
            if (buf[i] === 0x50 && buf[i + 1] === 0x4b && buf[i + 2] === 0x05 && buf[i + 3] === 0x06)
                return i;
        }
        return -1;
    }

    function parseZip(buf) {
        var eocd = findEOCD(buf);
        if (eocd < 0) throw new Error('不是有效的 EPUB/ZIP 文件');
        var view = new DataView(buf);
        var cdCount = readUint16LE(view, eocd + 10);
        var cdOffset = readUint32LE(view, eocd + 16);
        var entries = {};
        var p = cdOffset;
        for (var n = 0; n < cdCount; n++) {
            if (readUint32LE(view, p) !== 0x02014b50) break;
            var method = readUint16LE(view, p + 10);
            var compSize = readUint32LE(view, p + 20);
            var nameLen = readUint16LE(view, p + 28);
            var extraLen = readUint16LE(view, p + 30);
            var commentLen = readUint16LE(view, p + 32);
            var localOffset = readUint32LE(view, p + 42);
            var nameBytes = new Uint8Array(buf, p + 46, nameLen);
            var name = decode(nameBytes);
            p += 46 + nameLen + extraLen + commentLen;
            entries[name] = {
                method: method, compSize: compSize, localOffset: localOffset,
                dataStart: -1, name: name
            };
        }
        // 计算各条目数据起点
        for (var k in entries) {
            var e = entries[k];
            var lv = new DataView(buf, e.localOffset);
            if (readUint32LE(lv, 0) !== 0x04034b50) { e.dataStart = -1; continue; }
            var lnl = readUint16LE(lv, 26);
            var lel = readUint16LE(lv, 28);
            e.dataStart = e.localOffset + 30 + lnl + lel;
        }
        return entries;
    }

    function resolvePath(base, rel) {
        // 以 OPF 目录为 base，相对解析条目路径
        var b = base.split('/'); b.pop();
        if (b.length && b[b.length - 1] === '') b.pop();
        var segs = (base.indexOf('/') >= 0 ? b : []).concat(rel.split('/'));
        var out = [];
        for (var i = 0; i < segs.length; i++) {
            var s = segs[i];
            if (s === '' || s === '.') continue;
            if (s === '..') { if (out.length) out.pop(); }
            else out.push(s);
        }
        return out.join('/');
    }

    function readEntry(entries, buf, name) {
        var e = entries[name];
        if (!e || e.dataStart < 0) return Promise.resolve(null);
        var slice = new Uint8Array(buf, e.dataStart, e.compSize);
        if (e.method === 0) return Promise.resolve(slice);
        if (e.method === 8) return inflateRaw(slice);
        return Promise.reject(new Error('不支持的压缩方式: ' + e.method));
    }

    function xmlText(node, local) {
        var ns = node.ownerDocument;
        var el = node.getElementsByTagNameNS && node.getElementsByTagNameNS('*', local)[0];
        if (!el) el = node.getElementsByTagName && node.getElementsByTagName(local)[0];
        return el ? (el.textContent || '').trim() : '';
    }

    function xhtmlToHtml(bufBytes) {
        var html = decode(bufBytes, 'utf-8');
        var doc = new DOMParser().parseFromString(html, 'text/html');
        var body = doc.body || doc.documentElement;
        var paras = [];
        function walk(node) {
            for (var i = 0; i < node.childNodes.length; i++) {
                var c = node.childNodes[i];
                if (c.nodeType === 3) { var t = c.textContent; if (t && t.trim()) paras.push(escapeHtml(t.trim())); }
                else if (c.nodeType === 1) {
                    var tag = c.tagName.toLowerCase();
                    if (tag === 'script' || tag === 'style' || tag === 'head' || tag === 'meta') continue;
                    if (tag === 'br') { paras.push('<br>'); continue; }
                    if (tag === 'img') {
                        var src = c.getAttribute('src') || c.getAttribute('xlink:href') || '';
                        if (src) paras.push('<img src="' + escapeAttr(src) + '">');
                        continue;
                    }
                    if (tag === 'p' || tag === 'div' || tag === 'h1' || tag === 'h2' || tag === 'h3' ||
                        tag === 'h4' || tag === 'li' || tag === 'blockquote' || tag === 'section' || tag === 'tr' || tag === 'td') {
                        var inner = [];
                        for (var j = 0; j < c.childNodes.length; j++) {
                            var w = walkInline(c.childNodes[j]);
                            if (w) inner.push(w);
                        }
                        var txt = inner.join('');
                        if (txt) paras.push('<p>' + txt + '</p>');
                    } else {
                        walk(c);
                    }
                }
            }
        }
        function walkInline(node) {
            if (node.nodeType === 3) { var t = node.textContent; return t ? escapeHtml(t.trim()) : ''; }
            if (node.nodeType !== 1) return '';
            var tag = node.tagName.toLowerCase();
            if (tag === 'br') return '<br>';
            if (tag === 'img') {
                var src = node.getAttribute('src') || node.getAttribute('xlink:href') || '';
                return src ? '<img src="' + escapeAttr(src) + '">' : '';
            }
            var out = '';
            for (var i = 0; i < node.childNodes.length; i++) out += walkInline(node.childNodes[i]);
            return out;
        }
        walk(body);
        return paras.join('') || '<p>（本章无正文）</p>';
    }

    function escapeAttr(s) { return s.replace(/"/g, '&quot;'); }

    function parseEpub(arrayBuffer) {
        var buf = arrayBuffer instanceof ArrayBuffer ? arrayBuffer : arrayBuffer.buffer;
        var entries;
        try { entries = parseZip(buf); } catch (e) { return Promise.reject(e); }

        // 找 OPF
        var container = entries['META-INF/container.xml'];
        if (!container) return Promise.reject(new Error('EPUB 缺少 container.xml'));
        return readEntry(entries, buf, 'META-INF/container.xml').then(function (cBytes) {
            var cdoc = new DOMParser().parseFromString(decode(cBytes, 'utf-8'), 'application/xml');
            var rootfile = cdoc.getElementsByTagNameNS && cdoc.getElementsByTagNameNS('*', 'rootfile')[0];
            if (!rootfile) rootfile = cdoc.getElementsByTagName('rootfile')[0];
            var opfPath = rootfile && rootfile.getAttribute('full-path');
            if (!opfPath) return Promise.reject(new Error('无法定位 OPF'));
            return readEntry(entries, buf, opfPath).then(function (oBytes) {
                var opf = new DOMParser().parseFromString(decode(oBytes, 'utf-8'), 'application/xml');
                var metadata = opf.getElementsByTagName('metadata')[0] || opf.documentElement;
                var title = xmlText(metadata, 'title');
                var author = xmlText(metadata, 'creator');
                var manifest = opf.getElementsByTagName('manifest')[0] || opf.documentElement;
                var items = manifest.getElementsByTagName('item');
                var idToHref = {};
                for (var i = 0; i < items.length; i++) {
                    var id = items[i].getAttribute('id');
                    var href = items[i].getAttribute('href');
                    if (id && href) idToHref[id] = resolvePath(opfPath, href);
                }
                var spine = opf.getElementsByTagName('spine')[0] || opf.documentElement;
                var refs = spine.getElementsByTagName('itemref');
                var chapterPaths = [];
                for (var r = 0; r < refs.length; r++) {
                    var idref = refs[r].getAttribute('idref');
                    if (idToHref[idref]) chapterPaths.push(idToHref[idref]);
                }
                // 若没有 spine，退化为所有 xhtml
                if (!chapterPaths.length) {
                    for (var k in idToHref) {
                        var p = idToHref[k];
                        if (/\.x?html?$/i.test(p)) chapterPaths.push(p);
                    }
                }
                var chapters = [];
                var seq = Promise.resolve();
                chapterPaths.forEach(function (path, idx) {
                    seq = seq.then(function () {
                        return readEntry(entries, buf, path).then(function (bytes) {
                            if (!bytes) return;
                            var t = path.split('/').pop().replace(/\.x?html?$/i, '');
                            chapters.push({
                                title: '第 ' + (idx + 1) + ' 节',
                                content: xhtmlToHtml(bytes)
                            });
                        });
                    });
                });
                return seq.then(function () {
                    if (!chapters.length) return Promise.reject(new Error('EPUB 未解析到章节'));
                    return { title: title, author: author, chapters: chapters };
                });
            });
        });
    }

    window.Parser = {
        parseTxt: parseTxt,
        parseEpub: parseEpub,
        textToHtml: textToHtml
    };
})();

/* ===================== 阅读模块 · 书源引擎（legado 兼容） =====================
 * 复刻 legado 的书源规则：searchUrl(+选项JSON) / ruleSearch / ruleBookInfo /
 * ruleToc / ruleContent；支持 CSS 选择器、@js/<js> JS 求值（java.connect 同步中转）、
 * 正则、## 替换、{{}} 模板、&&/|| 合并。所有联网抓取经本机 /api/proxy，不依赖 legado 服务器。
 */
(function () {
    'use strict';

    /* ---------- 代理请求（GET，参数走 query） ---------- */
    function proxyUrl(targetUrl, method, headers, body, contentType) {
        var q = '/api/proxy?url=' + encodeURIComponent(targetUrl);
        if (method && method !== 'GET') q += '&method=' + encodeURIComponent(method);
        if (headers) q += '&headers=' + encodeURIComponent(JSON.stringify(headers));
        if (body != null) q += '&body=' + encodeURIComponent(body);
        if (contentType) q += '&contentType=' + encodeURIComponent(contentType);
        return q;
    }

    function decodeText(buf, ct) {
        var enc = 'utf-8';
        if (ct) { var m = ct.match(/charset=([^;]+)/i); if (m) enc = m[1].trim().replace(/["']/g, ''); }
        try { return new TextDecoder(enc).decode(buf); }
        catch (e) { return new TextDecoder('utf-8').decode(buf); }
    }

    function asyncFetch(targetUrl, opts) {
        opts = opts || {};
        var q = proxyUrl(targetUrl, opts.method, opts.headers, opts.body, opts.contentType);
        return fetch(q).then(function (r) {
            return r.arrayBuffer().then(function (buf) {
                return { status: r.status, text: decodeText(buf, r.headers.get('content-type')) };
            });
        });
    }

    // 同步 XHR：供 JS 规则里的 java.connect 使用（legado 的 java.connect 是同步阻塞的）
    function syncFetch(targetUrl, opts) {
        opts = opts || {};
        var q = proxyUrl(targetUrl, opts.method, opts.headers, opts.body, opts.contentType);
        var xhr = new XMLHttpRequest();
        xhr.open('GET', q, false);
        xhr.setRequestHeader('Accept', '*/*');
        xhr.send(null);
        return xhr.responseText;
    }

    /* ---------- URL 解析（url,选项JSON 逗号拼接 + 占位符） ---------- */
    function splitUrlOptions(spec) {
        spec = (spec || '').trim();
        var idx = spec.indexOf(',{');
        if (idx < 0) return { url: spec, method: 'GET', headers: {}, body: null, contentType: null };
        var url = spec.slice(0, idx);
        var optStr = spec.slice(idx + 1);
        var opts = {};
        try { opts = JSON.parse(optStr); } catch (e) { opts = {}; }
        var body = opts.body;
        var contentType = opts.contentType || null;
        if (body != null && typeof body !== 'string') { body = JSON.stringify(body); contentType = contentType || 'application/json; charset=utf-8'; }
        return {
            url: url, method: (opts.method || 'GET').toUpperCase(),
            headers: opts.headers || {}, body: body, contentType: contentType
        };
    }

    function replaceTemplates(str, scope) {
        return str.replace(/\{\{([\s\S]*?)\}\}/g, function (_, code) {
            try {
                var fn = new Function('baseUrl', 'searchKey', 'page', 'searchPage', 'source', 'java',
                    'try{return (' + code + ');}catch(e){return "";}');
                return fn(scope.baseUrl, scope.searchKey, scope.page, scope.searchPage, scope.source, scope.java);
            } catch (e) { return ''; }
        });
    }

    function fillPlaceholders(spec, kv) {
        var parts = splitUrlOptions(spec);
        var scope = { baseUrl: parts.url, searchKey: kv.searchKey || '', page: kv.page || 1, searchPage: kv.searchPage || kv.page || 1, source: kv.source || {}, java: makeJava() };
        var url = replaceTemplates(parts.url, scope).replace(/searchKey/g, encodeURIComponent(kv.searchKey || ''));
        url = url.replace(/\{\{searchPage\}\}/g, kv.searchPage || kv.page || 1)
                 .replace(/\{\{page\}\}/g, kv.page || 1);
        var body = parts.body;
        if (body != null) {
            body = body.replace(/searchKey/g, kv.searchKey || '')
                       .replace(/\{\{searchPage\}\}/g, kv.searchPage || kv.page || 1)
                       .replace(/\{\{page\}\}/g, kv.page || 1);
            body = replaceTemplates(body, scope);
        }
        var headers = parts.headers;
        return { url: url, method: parts.method, headers: headers, body: body, contentType: parts.contentType };
    }

    /* ---------- JS 沙箱（java.connect 同步中转） ---------- */
    function makeJava() {
        return {
            connect: function (spec) {
                var p = splitUrlOptions(spec);
                var text = syncFetch(p.url, { method: p.method, headers: p.headers, body: p.body, contentType: p.contentType });
                return {
                    statusCode: 200,
                    body: function () { return text; },
                    headers: function () { return ''; }
                };
            }
        };
    }

    function evalJs(code, scope) {
        code = (code || '').trim();
        if (code.indexOf('<js>') >= 0) {
            var m = code.match(/<js>([\s\S]*?)<\/js>/);
            if (m) code = m[1];
        } else if (code.indexOf('@js:') === 0) {
            code = code.slice(4);
        }
        var names = ['document', 'baseUrl', 'result', 'book', 'source', 'java', 'cookie', 'cache'];
        var vals = [scope.document, scope.baseUrl, scope.result, scope.book || {}, scope.source || {}, scope.java || makeJava(), '', {}];
        try {
            var fn = new Function(names.join(','), 'try{' + code + '}catch(e){return "";}');
            var r = fn.apply(null, vals);
            if (r === undefined) {
                var fn2 = new Function(names.join(','), 'return (' + code + ');');
                r = fn2.apply(null, vals);
            }
            return (r == null) ? '' : String(r);
        } catch (e) { return ''; }
    }

    /* ---------- CSS 选择器 ---------- */
    function qsa(root, sel) {
        sel = (sel || '').trim();
        var index = null;
        var im = sel.match(/\.(-?\d+)$/);
        if (im) { index = parseInt(im[1], 10); sel = sel.slice(0, im.index).trim(); }
        var out = [];
        try {
            var nl = root.querySelectorAll ? root.querySelectorAll(sel) : [];
            for (var i = 0; i < nl.length; i++) out.push(nl[i]);
        } catch (e) { return []; }
        if (index != null) {
            if (index < 0) index = out.length + index;
            return index >= 0 && index < out.length ? [out[index]] : [];
        }
        return out;
    }

    function isAttrName(seg) {
        if (/^(href|src|content|alt|title|value|class|name|id|srcset|rel|datetime|data-[a-z0-9-]+)$/i.test(seg)) return true;
        if (seg.indexOf(':') >= 0 && /^[a-zA-Z][a-zA-Z0-9_:-]*$/.test(seg)) return true;
        return false;
    }

    function toText(n) {
        if (n == null) return '';
        if (typeof n === 'string') return n;
        return n.textContent || '';
    }
    function concatText(items) { return items.map(toText).join(''); }

    /* ---------- 规则求值：返回 {kind:'el'|'str', items:[]} ---------- */
    function evalRuleCore(rule, root, baseUrl, doc) {
        rule = (rule || '').trim();
        if (rule === '') return { kind: 'str', items: [''] };
        if (rule.indexOf('<js>') >= 0 || rule.indexOf('@js:') === 0) {
            return { kind: 'str', items: [evalJs(rule, { document: doc, baseUrl: baseUrl, result: root, book: {}, source: {} })] };
        }
        var segs = rule.split('@');
        var kind = 'el';
        var items = [root];

        for (var i = 0; i < segs.length; i++) {
            var seg = segs[i];

            // ## 替换：inner##match##replace（### 表示仅替换首个）
            if (seg.indexOf('##') >= 0) {
                var h = seg.split('##');
                var inner = h[0];
                var match = h[1] || '';
                var repl = (h.length >= 3) ? h[2] : '';
                var firstOnly = seg.endsWith('###') || h.length > 3;
                var innerStr = evalRuleStr(inner, root, baseUrl, doc);
                var re = new RegExp(match, firstOnly ? '' : 'g');
                items = [innerStr.replace(re, repl)];
                kind = 'str';
                continue;
            }

            if (seg === 'text' || seg === 'ownText') { items = items.map(toText); kind = 'str'; continue; }
            if (seg === 'html' || seg === 'all' || seg === 'outerHtml') {
                items = items.map(function (n) { return (n && n.innerHTML != null) ? n.innerHTML : (n && n.outerHTML ? n.outerHTML : ''); });
                kind = 'str'; continue;
            }

            // 正则段：以 : 开头
            if (seg.charAt(0) === ':') {
                var pat = seg.slice(1);
                var text = concatText(items);
                var mm = text.match(new RegExp(pat));
                items = [mm ? (mm[1] !== undefined ? mm[1] : mm[0]) : ''];
                kind = 'str'; continue;
            }

            if (isAttrName(seg) && kind === 'el') {
                items = items.map(function (n) { return (n && n.getAttribute) ? (n.getAttribute(seg) || '') : ''; });
                kind = 'str'; continue;
            }

            if (kind === 'el') {
                var nx = [];
                items.forEach(function (n) { if (n && n.querySelectorAll) qsa(n, seg).forEach(function (e) { nx.push(e); }); });
                items = nx;
            }
            // 字符串态下其余段忽略
        }
        return { kind: kind, items: items };
    }

    function evalRuleStr(rule, root, baseUrl, doc) {
        var core = evalRuleCore(rule, root, baseUrl, doc);
        if (core.kind === 'str') {
            for (var i = 0; i < core.items.length; i++) if (core.items[i]) return core.items[i];
            return '';
        }
        // 元素是：取首个非空文本
        for (var j = 0; j < core.items.length; j++) {
            var t = toText(core.items[j]).trim();
            if (t) return t;
        }
        return '';
    }

    function evalRuleList(rule, root, baseUrl, doc) {
        var core = evalRuleCore(rule, root, baseUrl, doc);
        if (core.kind === 'el') return core.items;
        return [];
    }

    function absUrl(rel, base) {
        try { return new URL(rel, base).href; } catch (e) { return rel; }
    }

    /* ---------- 规则对象归一（支持对象或 JSON 字符串） ---------- */
    function asRule(v) {
        if (v == null) return {};
        if (typeof v === 'string') { try { return JSON.parse(v); } catch (e) { return {}; } }
        return v;
    }

    /* ---------- 流程 ---------- */
    function search(source, keyword, page) {
        page = page || 1;
        var spec = fillPlaceholders(source.searchUrl, { searchKey: keyword, page: page, searchPage: page, source: source });
        return asyncFetch(spec.url, { method: spec.method, headers: spec.headers, body: spec.body, contentType: spec.contentType })
            .then(function (resp) {
                var doc = new DOMParser().parseFromString(resp.text, 'text/html');
                var rs = asRule(source.ruleSearch);
                var list = evalRuleList(rs.bookList, doc, spec.url, doc);
                var out = [];
                list.forEach(function (el) {
                    var bookUrl = evalRuleStr(rs.bookUrl, el, spec.url, doc);
                    if (!bookUrl) return;
                    out.push({
                        name: evalRuleStr(rs.name, el, spec.url, doc),
                        author: evalRuleStr(rs.author, el, spec.url, doc),
                        coverUrl: absUrl(evalRuleStr(rs.coverUrl, el, spec.url, doc), spec.url),
                        bookUrl: absUrl(bookUrl, spec.url),
                        intro: evalRuleStr(rs.intro, el, spec.url, doc),
                        kind: evalRuleStr(rs.kind, el, spec.url, doc),
                        lastChapter: evalRuleStr(rs.lastChapter, el, spec.url, doc),
                        sourceUrl: source.bookSourceUrl,
                        sourceName: source.bookSourceName
                    });
                });
                return out;
            });
    }

    function getBookInfo(source, bookUrl) {
        var spec = fillPlaceholders(bookUrl, { source: source });
        return asyncFetch(spec.url, { method: spec.method, headers: spec.headers, body: spec.body, contentType: spec.contentType })
            .then(function (resp) {
                var doc = new DOMParser().parseFromString(resp.text, 'text/html');
                var ri = asRule(source.ruleBookInfo);
                var info = {
                    name: evalRuleStr(ri.name, doc, spec.url, doc),
                    author: evalRuleStr(ri.author, doc, spec.url, doc),
                    coverUrl: absUrl(evalRuleStr(ri.coverUrl, doc, spec.url, doc), spec.url),
                    intro: evalRuleStr(ri.intro, doc, spec.url, doc),
                    kind: evalRuleStr(ri.kind, doc, spec.url, doc),
                    lastChapter: evalRuleStr(ri.lastChapter, doc, spec.url, doc),
                    tocUrl: absUrl(evalRuleStr(ri.tocUrl, doc, spec.url, doc), spec.url),
                    bookUrl: spec.url
                };
                if (!info.tocUrl) info.tocUrl = spec.url;
                return info;
            });
    }

    function getToc(source, tocUrl) {
        var all = [];
        function loop(url) {
            if (!url) return Promise.resolve(all);
            var spec = fillPlaceholders(url, { source: source });
            return asyncFetch(spec.url, { method: spec.method, headers: spec.headers, body: spec.body, contentType: spec.contentType })
                .then(function (resp) {
                    var doc = new DOMParser().parseFromString(resp.text, 'text/html');
                    var rt = asRule(source.ruleToc);
                    var list = evalRuleList(rt.chapterList, doc, spec.url, doc);
                    list.forEach(function (el) {
                        var cUrl = evalRuleStr(rt.chapterUrl, el, spec.url, doc);
                        var cName = evalRuleStr(rt.chapterName, el, spec.url, doc);
                        if (cUrl) all.push({ title: cName || ('第 ' + (all.length + 1) + ' 章'), url: absUrl(cUrl, spec.url) });
                    });
                    var next = evalRuleStr(rt.nextTocUrl, doc, spec.url, doc);
                    if (next && next !== url) return loop(absUrl(next, spec.url));
                    return all;
                });
        }
        return loop(tocUrl);
    }

    function getContent(source, chapterUrl, cleanRules) {
        var html = '';
        function loop(url) {
            if (!url) return Promise.resolve(html);
            var spec = fillPlaceholders(url, { source: source });
            return asyncFetch(spec.url, { method: spec.method, headers: spec.headers, body: spec.body, contentType: spec.contentType })
                .then(function (resp) {
                    var doc = new DOMParser().parseFromString(resp.text, 'text/html');
                    var rc = asRule(source.ruleContent);
                    var part = evalRuleStr(rc.content, doc, spec.url, doc);
                    // 正文净化（全局规则）
                    if (cleanRules && cleanRules.length) part = applyClean(part, cleanRules);
                    html += part;
                    // 翻页
                    var nx = evalRuleStr(rc.nextContentUrl, doc, spec.url, doc);
                    if (nx && nx !== url) return loop(absUrl(nx, spec.url));
                    return html;
                });
        }
        return loop(chapterUrl).then(function (txt) {
            txt = txt || '';
            // 若书源正文已是含标签的 HTML，则原样返回；否则按换行切分段落
            if (/<\s*(p|br|div|span|h[1-6]|a|img)\b/i.test(txt)) return txt;
            return '<p>' + txt.replace(/\n+/g, '</p><p>') + '</p>';
        });
    }

    function applyClean(text, rules) {
        rules.forEach(function (r) {
            try {
                var re = new RegExp(r.pattern, r.flags || 'g');
                text = text.replace(re, r.replacement || '');
            } catch (e) { }
        });
        return text;
    }

    window.BookSourceEngine = {
        search: search,
        getBookInfo: getBookInfo,
        getToc: getToc,
        getContent: getContent,
        proxyFetch: asyncFetch
    };
})();

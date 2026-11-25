/**
 * 内置浏览器与全屏侧栏。
 *
 * 侧栏结构：
 *   顶部  —— 地址栏（输入网址直达；输入非链接内容用必应搜索）
 *   第 1 项 —— 聊天界面
 *   第 2 项起 —— 已打开的网页标签
 *
 * 页面渲染在 <iframe> 中；每个标签维护自己的历史栈，返回键先在标签内后退。
 */
window.Browser = (function () {
    'use strict';

    var sidebar, listEl, omniInput, omniForm;
    var view, frame, urlLabel;
    var tabs = [];            // { id, title, url, history: [], index }
    var activeTabId = null;   // null 表示当前在聊天界面
    var seq = 0;

    /* ==================== 地址解析 ==================== */

    var BING = 'https://cn.bing.com/search?q=';

    /** 判断输入是网址还是搜索词；返回可导航的完整 URL。 */
    function resolveInput(text) {
        var s = (text || '').trim();
        if (!s) return null;

        // 已带协议
        if (/^https?:\/\//i.test(s)) return s;
        if (/^(file|ftp|about|data):/i.test(s)) return s;

        // localhost / IP[:port] / 含点且无空格的域名 → 视为网址
        var looksLikeHost =
            /^localhost(:\d+)?(\/|$)/i.test(s) ||
            /^\d{1,3}(\.\d{1,3}){3}(:\d+)?(\/|$)/.test(s) ||
            (!/\s/.test(s) && /^[\w-]+(\.[\w-]+)+(:\d+)?(\/.*)?$/.test(s));

        if (looksLikeHost) return 'https://' + s;

        return BING + encodeURIComponent(s);
    }

    function hostOf(url) {
        try { return new URL(url).hostname.replace(/^www\./, ''); } catch (e) { return url; }
    }

    /* ==================== 标签管理 ==================== */

    function createTab(url) {
        var tab = {
            id: 'tab' + (++seq),
            url: url,
            title: hostOf(url),
            history: [url],
            index: 0
        };
        tabs.push(tab);
        return tab;
    }

    function findTab(id) {
        for (var i = 0; i < tabs.length; i++) if (tabs[i].id === id) return tabs[i];
        return null;
    }

    function closeTab(id) {
        var idx = -1;
        for (var i = 0; i < tabs.length; i++) if (tabs[i].id === id) { idx = i; break; }
        if (idx < 0) return;
        tabs.splice(idx, 1);
        if (activeTabId === id) showChat();
        renderSidebar();
    }

    /* ==================== 视图切换 ==================== */

    function showChat() {
        activeTabId = null;
        view.classList.remove('show');
        frame.src = 'about:blank';
        renderSidebar();
    }

    function showTab(id) {
        var tab = findTab(id);
        if (!tab) return;
        activeTabId = id;
        view.classList.add('show');
        navigateFrame(tab.history[tab.index]);
        renderSidebar();
    }

    function navigateFrame(url) {
        urlLabel.textContent = url;
        // 外部站点（http/https）经同源 /__proxy/ 加载，使 iframe 与当前站点同源，
        // 从而原生层能把 Web Bluetooth Polyfill 注入进去，让普通网页也能用 GATT。
        // 使用相对路径，自动跟随当前 origin（回环 127.0.0.1 或兼容旧版 app.local）。
        if (/^https?:\/\//i.test(url) && url.indexOf('app.local') < 0) {
            frame.src = '/__proxy/' + encodeURIComponent(url);
        } else if (/^(about:blank|about:blank#|javascript:)/i.test(url)) {
            frame.src = url;
        } else if (!/^https?:\/\//i.test(url)) {
            // 非 http(s) 协议（file:/ftp:/tel:/mailto: 等）无法经代理浏览，给出提示并停留当前页
            urlLabel.textContent = '不支持的地址（仅支持 http/https）：' + url;
            if (window.ChatUI && window.ChatUI.toast) window.ChatUI.toast('仅支持浏览 http/https 网页');
        } else {
            frame.src = url;
        }
    }

    /** 内置虚拟主机（由原生 ShouldInterceptRequest 拦截，映射到 APK assets）。
     *  目前仅 app.local（主聊天框架自身）走虚拟主机；网页端已改为真实 HTTP 地址。 */
    var INTERNAL_HOSTS = ['app.local'];

    function isInternalUrl(url) {
        return INTERNAL_HOSTS.some(function (h) {
            return url.indexOf('://' + h + '/') >= 0 || url.indexOf('://' + h) >= 0;
        });
    }

    /** 在新标签中打开链接（[url] 卡片点击 / 地址栏跳转都走这里） */
    function openInNewTab(url) {
        var real = resolveInput(url);
        if (!real) return;
        if (isInternalUrl(real)) { openInternal(real); return; }
        var tab = createTab(real);
        closeSidebar();
        showTab(tab.id);
    }

    /** 在当前标签内导航（保留历史） */
    function navigateActive(url) {
        var tab = findTab(activeTabId);
        if (!tab) { openInNewTab(url); return; }
        tab.history = tab.history.slice(0, tab.index + 1);
        tab.history.push(url);
        tab.index = tab.history.length - 1;
        tab.url = url;
        tab.title = hostOf(url);
        navigateFrame(url);
        renderSidebar();
    }

    /** 标签内后退；返回 true 表示已消费返回事件 */
    function goBack() {
        var tab = findTab(activeTabId);
        if (!tab || tab.index <= 0) return false;
        tab.index--;
        navigateFrame(tab.history[tab.index]);
        return true;
    }

    /* ==================== 侧栏 ==================== */

    function openSidebar() {
        sidebar.classList.add('open');
        renderSidebar();
    }

    function closeSidebar() {
        sidebar.classList.remove('open');
        omniInput.blur();
    }

    function isSidebarOpen() {
        return sidebar.classList.contains('open');
    }

    function renderSidebar() {
        listEl.innerHTML = '';

        // 固定项 1：聊天主界面（app.local），不可删除、不可关闭
        listEl.appendChild(buildItem({
            icon: 'chat',
            title: '聊天',
            sub: '局域网消息',
            active: activeTabId === null && location.pathname.indexOf('/reader') !== 0,
            pinned: true,
            onClick: function () { closeSidebar(); showChat(); }
        }));

        // 固定项 2：阅读（融合 legado 的本地阅读 + 书源功能，离线可用）
        var inReader = location.pathname.indexOf('/reader') === 0;
        if (!inReader) {
            listEl.appendChild(buildItem({
                icon: 'books',
                title: '阅读',
                sub: '小说 · 书源 · 书架',
                pinned: true,
                onClick: function () { closeSidebar(); window.location.href = '/reader/'; }
            }));
        }

        // 固定项 3：影音（音乐 + 视频，与 App 共用曲库 / 歌单 / 喜欢）
        var inMedia = location.pathname.indexOf('/media') === 0;
        if (!inMedia) {
            listEl.appendChild(buildItem({
                icon: 'video',
                title: '影音',
                sub: '音乐 · 视频 · 歌单',
                pinned: true,
                onClick: function () { closeSidebar(); window.location.href = '/media'; }
            }));
        }

        // 网页版 GPS 页面已暂移入回收站（App 改用原生 GPS 实现），此入口一并停用。
        // 恢复时取消下面这段注释，并把 gps.html / js/gps.js 从回收站还原：
        /*
        var inGps = location.pathname.indexOf('/gps') === 0;
        if (!inGps) {
            listEl.appendChild(buildItem({
                icon: 'location',
                title: 'GPS 共享位置',
                sub: '多人同屏 · 距离/高度 · 朝向',
                pinned: true,
                onClick: function () { closeSidebar(); window.location.href = '/gps'; }
            }));
        }
        */

        // 网页端固定项已随 Web 服务器功能一并移除。

        var section = document.createElement('div');
        section.className = 'sidebar-section';
        section.textContent = tabs.length ? '网页（' + tabs.length + '）' : '网页（在上方输入网址或关键词打开）';
        listEl.appendChild(section);

        tabs.forEach(function (tab) {
            listEl.appendChild(buildItem({
                icon: 'window',
                title: tab.title || hostOf(tab.url),
                sub: tab.history[tab.index],
                active: activeTabId === tab.id,
                onClick: function () { closeSidebar(); showTab(tab.id); },
                onClose: function () { closeTab(tab.id); }
            }));
        });

        // 本地 Web 服务器管理已移除（功能随 Web 服务器一并下线）。
    }


    /** 打开内置虚拟主机页面（仅 app.local，即本聊天框架自身，一般不会被显式调用）。 */
    function openInternal(url) {
        var real = resolveInput(url);
        if (!real) return;
        var tab = createTab(real);
        closeSidebar();
        showTab(tab.id);
    }

    function buildItem(opt) {
        var item = document.createElement('div');
        item.className = 'side-item' + (opt.active ? ' active' : '');

        var ico = document.createElement('div');
        ico.className = 'si-ico';
        ico.innerHTML = WI.svg(opt.icon);
        item.appendChild(ico);

        var body = document.createElement('div');
        body.className = 'si-body';
        var t = document.createElement('div');
        t.className = 'si-title';
        t.textContent = opt.title;
        body.appendChild(t);
        if (opt.sub) {
            var s = document.createElement('div');
            s.className = 'si-sub';
            s.textContent = opt.sub;
            body.appendChild(s);
        }
        item.appendChild(body);

        if (opt.onClose) {
            var close = document.createElement('button');
            close.className = 'si-close';
            close.innerHTML = WI.svg('close');
            close.addEventListener('click', function (e) {
                e.stopPropagation();
                opt.onClose();
            });
            item.appendChild(close);
        }

        item.addEventListener('click', opt.onClick);
        return item;
    }

    /* ==================== 初始化 ==================== */

    function init() {
        sidebar = document.getElementById('sidebar');
        listEl = document.getElementById('sidebarList');
        omniInput = document.getElementById('omniInput');
        omniForm = document.getElementById('omniForm');

        view = document.getElementById('browserView');
        frame = document.getElementById('browserFrame');
        urlLabel = document.getElementById('browserUrl');

        document.getElementById('btnMenu').addEventListener('click', openSidebar);
        document.getElementById('btnBrowserMenu').addEventListener('click', openSidebar);
        document.getElementById('btnCloseSidebar').addEventListener('click', closeSidebar);

        document.getElementById('btnBrowserBack').addEventListener('click', function () {
            if (!goBack()) showChat();
        });
        document.getElementById('btnBrowserReload').addEventListener('click', function () {
            var tab = findTab(activeTabId);
            if (tab) navigateFrame(tab.history[tab.index]);
        });

        omniForm.addEventListener('submit', function (e) {
            e.preventDefault();
            var target = resolveInput(omniInput.value);
            if (!target) return;
            omniInput.value = '';
            openInNewTab(target);
        });

        renderSidebar();
    }

    return {
        init: init,
        openInNewTab: openInNewTab,
        navigateActive: navigateActive,
        openSidebar: openSidebar,
        closeSidebar: closeSidebar,
        isSidebarOpen: isSidebarOpen,
        goBack: goBack,
        showChat: showChat,
        inBrowser: function () { return activeTabId !== null; },
        resolveInput: resolveInput
    };
})();

/**
 * 工具面板：文件管理器 / qaul 群组 / 身份与权限 / 频道与双通道。
 *
 * 全部走 window.Bridge 的原生接口，与 app 端一致：
 *  - 文件：listShare / uploadFile + /file/get 下载（与 FTP 同一共享目录）
 *  - 群组：createQaulGroup / getQaulGroups / openQaulDm / removeFromQaulGroup / renameConversation
 *  - 身份：getIdentity / rotateIdentity / requestAllFilesAccess
 *  - 频道：getChannelStatus / setDualChannel
 */
(function () {
    'use strict';

    var overlay, tabs, sections;
    var fileRel = '';

    function $(id) { return document.getElementById(id); }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function toast(m) { if (window.Bridge) window.Bridge.toast(m); }
    function sizeText(n) {
        n = Number(n) || 0;
        if (n < 1024) return n + ' B';
        if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
        if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
        return (n / 1073741824).toFixed(2) + ' GB';
    }

    function open(tab) {
        if (!overlay) return;
        overlay.classList.remove('hidden');
        if (tab) switchTab(tab);
        else switchTab(currentTab());
    }
    function close() { if (overlay) overlay.classList.add('hidden'); }

    function currentTab() {
        var a = tabs ? tabs.querySelector('.tools-tab.active') : null;
        return a ? a.getAttribute('data-tab') : 'files';
    }

    function switchTab(name) {
        if (!tabs) return;
        var btns = tabs.querySelectorAll('.tools-tab');
        for (var i = 0; i < btns.length; i++) {
            btns[i].classList.toggle('active', btns[i].getAttribute('data-tab') === name);
        }
        var secs = overlay.querySelectorAll('.tools-section');
        for (var j = 0; j < secs.length; j++) {
            secs[j].classList.toggle('active', secs[j].getAttribute('data-sec') === name);
        }
        if (name === 'files') loadFiles(fileRel);
        else if (name === 'groups') loadGroups();
        else if (name === 'identity') loadIdentity();
        else if (name === 'channel') loadChannel();
    }

    /* ===================== 文件管理器 ===================== */

    function loadFiles(rel) {
        fileRel = rel || '';
        var box = $('fileList');
        var pathEl = $('filePath');
        if (!box) return;
        if (pathEl) pathEl.textContent = '/' + fileRel;
        if (!window.Bridge) { box.innerHTML = '<div class="tools-empty">当前环境不支持</div>'; return; }
        var data = window.Bridge.listShare(fileRel);
        var items = (data && data.items) || [];
        if (!items.length) { box.innerHTML = '<div class="tools-empty">（空目录）</div>'; return; }

        // 目录在前，文件在后
        items.sort(function (a, b) {
            if (a.isDir !== b.isDir) return a.isDir ? -1 : 1;
            return String(a.name).localeCompare(String(b.name));
        });

        box.innerHTML = '';
        items.forEach(function (it) {
            var row = document.createElement('div');
            row.className = 'tools-item';
            var icon = it.isDir ? WI.svg('folder') : WI.svg('file');
            row.innerHTML = '<span class="ti-name">' + icon + ' ' + esc(it.name) + '</span>' +
                            '<span class="ti-meta">' + (it.isDir ? '目录' : sizeText(it.size)) + '</span>';
            row.addEventListener('click', function () {
                if (it.isDir) {
                    loadFiles(it.rel);
                } else {
                    var url = location.origin + '/file/get/' + encodeURIComponent(it.rel);
                    window.open(url, '_blank');
                }
            });
            box.appendChild(row);
        });
    }

    function goUp() {
        if (!fileRel) return;
        var idx = fileRel.lastIndexOf('/');
        fileRel = idx >= 0 ? fileRel.substring(0, idx) : '';
        loadFiles(fileRel);
    }

    function uploadFile(file) {
        if (!file || !window.Bridge) return;
        var reader = new FileReader();
        reader.onload = function () {
            var res = String(reader.result || '');
            var comma = res.indexOf(',');
            var b64 = comma >= 0 ? res.substring(comma + 1) : res;
            var ok = window.Bridge.uploadFile(b64, file.name);
            toast(ok ? '已上传：' + file.name : '上传失败');
            if (ok) loadFiles(fileRel);
        };
        reader.readAsDataURL(file);
    }

    /* ===================== 群组 ===================== */

    function loadGroups() {
        var box = $('groupList');
        if (!box) return;
        if (!window.Bridge) { box.innerHTML = '<div class="tools-empty">当前环境不支持</div>'; return; }
        var groups = window.Bridge.getQaulGroups() || [];
        if (!groups.length) { box.innerHTML = '<div class="tools-empty">暂无群组</div>'; return; }
        box.innerHTML = '';
        groups.forEach(function (g) {
            var card = document.createElement('div');
            card.className = 'tools-card';
            var head = document.createElement('div');
            head.className = 'tools-card-head';
            head.innerHTML = '<b>' + esc(g.name || g.id) + '</b><span class="ti-meta">' + (g.members || 0) + ' 人</span>';
            card.appendChild(head);

            // 成员
            var members = g.memberList || [];
            if (members.length) {
                var ml = document.createElement('div');
                ml.className = 'tools-members';
                members.forEach(function (m) {
                    var chip = document.createElement('button');
                    chip.className = 'tools-member';
                    chip.textContent = (m.name || m.id) + (m.self ? '（我）' : '');
                    chip.addEventListener('click', function () {
                        if (!m.self && m.id) {
                            var res = window.Bridge.openQaulDm(m.id);
                            if (res && res.conv && window.Chat) { close(); window.Chat.setConv(res.conv); }
                        }
                    });
                    ml.appendChild(chip);
                });
                card.appendChild(ml);
            }

            // 操作
            var acts = document.createElement('div');
            acts.className = 'tools-row';
            var enter = document.createElement('button');
            enter.className = 'tools-btn';
            enter.textContent = '进入群聊';
            enter.addEventListener('click', function () {
                if (g.conv && window.Chat) { close(); window.Chat.setConv(g.conv); }
            });
            acts.appendChild(enter);

            var rename = document.createElement('button');
            rename.className = 'tools-btn ghost';
            rename.textContent = '改名';
            rename.addEventListener('click', function () {
                var name = window.prompt('群组新名称', g.name || '');
                if (name && name.trim()) {
                    window.Bridge.renameConversation(g.conv, name.trim());
                    loadGroups();
                }
            });
            acts.appendChild(rename);

            var leave = document.createElement('button');
            leave.className = 'tools-btn warn';
            leave.textContent = '退群';
            leave.addEventListener('click', function () {
                if (!window.confirm('退出群组「' + (g.name || g.id) + '」？')) return;
                // removeFromQaulGroup 传自身 id 表示离开（与 app 端一致时由原生处理）
                var selfInfo = window.Bridge.getSelfInfo ? window.Bridge.getSelfInfo() : null;
                window.Bridge.removeFromQaulGroup(g.id, (selfInfo && selfInfo.qaulPeerId) || '');
                loadGroups();
            });
            acts.appendChild(leave);
            card.appendChild(acts);
            box.appendChild(card);
        });
    }

    function createGroup() {
        var input = $('groupName');
        var name = input ? input.value.trim() : '';
        if (!name) { toast('请输入群组名称'); return; }
        var res = window.Bridge.createQaulGroup(name);
        if (res) { if (input) input.value = ''; toast('群组已创建'); loadGroups(); }
        else toast('创建失败（需 qaul 在线）');
    }

    /* ===================== 身份与权限 ===================== */

    function loadIdentity() {
        if (!window.Bridge) return;
        var id = window.Bridge.getIdentity() || {};
        var q = id.qaul || {};
        var finger = $('idFinger');
        if (finger) finger.textContent = q.fingerprint || q.peerId || '—';
        var nameEl = $('idName');
        if (nameEl) {
            var selfInfo = window.Bridge.getSelfInfo ? window.Bridge.getSelfInfo() : null;
            nameEl.textContent = (selfInfo && selfInfo.name) || '—';
        }
    }

    function rotateIdentity() {
        if (!window.confirm('轮换身份会生成新的 qaul 密钥，确定继续？')) return;
        var res = window.Bridge.rotateIdentity('qaul');
        toast(res ? '身份已轮换' : '轮换失败');
        loadIdentity();
    }

    /* ===================== 频道与双通道 ===================== */

    function loadChannel() {
        var box = $('channelStatus');
        var dualEl = $('dualState');
        var listenEl = $('listenState');
        if (!window.Bridge) return;
        if (listenEl) {
            var lt = true;
            try { lt = window.Bridge.getListenTogether(); } catch (e) {}
            listenEl.textContent = lt ? '已开启' : '已关闭';
        }
        var st = window.Bridge.getChannelStatus();
        if (!st) { if (box) box.innerHTML = '<div class="tools-empty">频道未就绪</div>'; return; }
        if (dualEl) dualEl.textContent = st.dual ? '已开启' : '已关闭';
        if (!box) return;
        var rows = [];
        rows.push(['qaul 端口', st.qaulPort || '-']);
        rows.push(['qaul 运行', st.qaulRunning ? '是' : '否']);
        rows.push(['qaul 收/发', (st.qaulRx || 0) + ' / ' + (st.qaulTx || 0)]);
        rows.push(['局域网端口', st.lanPort || '-']);
        rows.push(['qaul 节点', st.nodeRunning ? '在线' : '离线']);
        rows.push(['邻居数', st.neighbours || 0]);
        var html = '';
        rows.forEach(function (r) {
            html += '<div class="tools-kv"><span>' + esc(r[0]) + '</span><b>' + esc(r[1]) + '</b></div>';
        });
        // 各链路健康度
        var links = st.links || [];
        if (links.length) {
            html += '<div class="tools-sub">链路</div>';
            links.forEach(function (l) {
                var label = l.name || l.link || l.id || '链路';
                var txt = (l.enabled === false ? '关闭' : (l.running ? '运行' : '待机')) +
                          ' · 收' + (l.rx || 0) + ' 发' + (l.tx || 0);
                html += '<div class="tools-kv"><span>' + esc(label) + '</span><b>' + esc(txt) + '</b></div>';
            });
        }
        box.innerHTML = html;
    }

    function toggleDual() {
        if (!window.Bridge) return;
        var st = window.Bridge.getChannelStatus();
        var next = !(st && st.dual);
        window.Bridge.setDualChannel(next);
        toast(next ? '双通道已开启' : '双通道已关闭');
        setTimeout(loadChannel, 150);
    }

    function toggleListenTogether() {
        if (!window.Bridge) return;
        var cur = true;
        try { cur = window.Bridge.getListenTogether(); } catch (e) {}
        var next = !cur;
        try { window.Bridge.setListenTogether(next); } catch (e) { return; }
        toast(next ? '一起听已开启' : '一起听已关闭');
        loadChannel();
    }

    /* ===================== 初始化 ===================== */

    function init() {
        overlay = $('toolsOverlay');
        tabs = $('toolsTabs');
        if (!overlay) return;

        // 打开入口
        var btn = $('btnTools');
        if (btn) btn.addEventListener('click', function () { open('files'); });

        // 关闭
        overlay.querySelectorAll('[data-close="toolsOverlay"], .tools-close').forEach(function (b) {
            b.addEventListener('click', close);
        });
        overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });

        // 标签切换
        if (tabs) {
            tabs.addEventListener('click', function (e) {
                var t = e.target.closest ? e.target.closest('.tools-tab') : null;
                if (t) switchTab(t.getAttribute('data-tab'));
            });
        }

        // 文件
        var up = $('fileUp'); if (up) up.addEventListener('click', goUp);
        var fi = $('fileUploadInput');
        if (fi) fi.addEventListener('change', function () {
            if (fi.files && fi.files[0]) uploadFile(fi.files[0]);
            fi.value = '';
        });

        // 群组
        var gc = $('groupCreate'); if (gc) gc.addEventListener('click', createGroup);

        // 身份
        var rot = $('idRotate'); if (rot) rot.addEventListener('click', rotateIdentity);
        var fil = $('idFiles'); if (fil) fil.addEventListener('click', function () {
            window.Bridge.requestAllFilesAccess(); toast('已跳转授权页');
        });

        // 频道
        var dt = $('dualToggle'); if (dt) dt.addEventListener('click', toggleDual);
        var lt = $('listenToggle'); if (lt) lt.addEventListener('click', toggleListenTogether);
    }

    window.Tools = { init: init, open: open, close: close };

    document.addEventListener('DOMContentLoaded', init);
    if (document.readyState !== 'loading') init();
})();

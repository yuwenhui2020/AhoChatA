/**
 * 聊天模块：消息解析、渲染、收发。
 *
 * 支持的特定格式（写在输入框任意位置，独立成段即整条消息为该类型）：
 *   [url]https://example.com          网页链接 → 点击在内置浏览器新建页面打开
 *   [music]https://.../song.mp3       音乐     → 铺满宽度的音乐卡片，点击进播放器
 *   [video]https://.../movie.mp4      视频     → 黑底白播放键，点击进内置播放器
 *   [pic]https://.../image.png        图片     → 自动加载显示，最大宽度不超过聊天区
 *   [notice]要提醒的内容               通知     → 发到本机系统通知（收发双方都弹）
 */
window.Chat = (function () {
    'use strict';
    var showgps = true;
    var listEl, inputEl, sendBtn, statusEl;
    var self = { peerId: '', name: '' };
    var messages = [];
    // 当前会话（房间号 / 私聊）；会话键由原生侧给出，默认公共房间 mesh
    var currentConv = 'room:mesh';
    // 正在编辑的消息 id（非空 = 编辑态，发送时改为更新该消息）
    var editingId = null;

    var TAG_RE = /^\s*\[(url|music|video|pic|notice)\]\s*([\s\S]*)$/i;

    /* ==================== 解析 ==================== */

    /** 把原始文本解析为消息体 {kind, text, url} */
    function parseContent(raw) {
        var m = TAG_RE.exec(raw);
        if (!m) return { kind: 'text', text: raw };

        var kind = m[1].toLowerCase();
        var body = (m[2] || '').trim();
        if (kind === 'notice') return { kind: 'notice', text: body };
        return { kind: kind, url: body, text: body };
    }

    function fileNameOf(url) {
        if (!url) return '';
        try {
            var clean = url.split('?')[0].split('#')[0];
            var parts = clean.split('/');
            return decodeURIComponent(parts[parts.length - 1] || '');
        } catch (e) { return ''; }
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    /** 解析出消息真正可用的资源地址：优先原生落地的 mediaUrl */
    function resourceOf(msg) {
        return msg.mediaUrl || msg.url || '';
    }

    /* ==================== 渲染 ==================== */

    function renderMessage(msg) {
        var isOut = msg.from === self.peerId;

        // 跨设备媒体同步消息（播放状态广播 / 控制指令）不渲染气泡：
        // 它们由 js/remote-media.js 处理，对用户不可见，避免刷屏。
        if (msg.kind === 'mediaplayer' || msg.kind === 'mediacontrol') return;

        // 时间戳独立成行，显示在消息上方并整行居中（不再挂在气泡下方左对齐）
        var timeRow = document.createElement('div');
        timeRow.className = 'msg-time-row';
        timeRow.textContent = formatClock(msg.time);
        listEl.appendChild(timeRow);

        var row = document.createElement('div');
        row.className = 'msg-row ' + (isOut ? 'out' : 'in');
        if (msg.id) row.setAttribute('data-mid', msg.id);

        var avatar = document.createElement('div');
        avatar.className = 'avatar';
        avatar.textContent = (msg.fromName || '?').charAt(0).toUpperCase();
        row.appendChild(avatar);

        var main = document.createElement('div');
        main.className = 'msg-main' + (msg.kind === 'music' ? ' wide' : '');

        if (!isOut) {
            var sender = document.createElement('div');
            sender.className = 'msg-sender';
            sender.textContent = msg.fromName || '未知设备';
            main.appendChild(sender);
        }

        var bubble = document.createElement('div');
        bubble.className = 'bubble';
        buildBody(bubble, msg);
        bindLongPress(bubble, function () { showMsgMenu(msg); });
        main.appendChild(bubble);

        row.appendChild(main);
        listEl.appendChild(row);
        scrollToBottom();
    }

    function buildBody(bubble, msg) {
        var url = resourceOf(msg);

        /* 媒体快捷卡片：频道分享的阅读/音乐/图片/视频，点击直接添加到本机媒体库
         * （文件落 /sdcard/file/<类型>/，经原生 importMlib 桥导入对应列表） */
        var mlib = mlibOf(msg);
        if (mlib && msg.mediaUrl) { renderMlibCard(bubble, msg, mlib); return; }

        // 局域网音乐卡片（播放端下发，切歌时原地 patch，不新增气泡）
        if (msg.kind === 'musiccard') { renderMusicCard(bubble, msg); return; }

        // 视频分享卡片（播放器里手动点「分享」生成，点击全屏播放）
        if (msg.kind === 'videocard') { renderVideoShareCard(bubble, msg); return; }

        // 程序更新卡片（带一键安装按钮）
        if (msg.kind === 'update') { renderUpdateCard(bubble, msg); return; }

        switch (msg.kind) {
            case 'voice': {
                bubble.classList.add('plain');
                var dur = msg.duration ? Number(msg.duration) : 0;
                var wrap = document.createElement('div');
                wrap.className = 'voice-msg';
                var playBtn = document.createElement('button');
                playBtn.className = 'voice-play';
                playBtn.innerHTML = WI.svg('play');
                var bar = document.createElement('div');
                bar.className = 'voice-bar';
                var inner = document.createElement('div');
                inner.className = 'voice-bar-inner';
                bar.appendChild(inner);
                var time = document.createElement('span');
                time.className = 'voice-dur';
                time.textContent = (dur > 0 ? dur : 0) + '″';
                wrap.appendChild(playBtn);
                wrap.appendChild(bar);
                wrap.appendChild(time);

                var audio = new Audio();
                audio.src = url;
                var playing = false;
                playBtn.addEventListener('click', function () {
                    if (playing) { audio.pause(); return; }
                    audio.play().catch(function () { window.Bridge.toast('播放失败'); });
                });
                audio.addEventListener('play', function () { playing = true; playBtn.innerHTML = WI.svg('pause'); });
                audio.addEventListener('pause', function () { playing = false; playBtn.innerHTML = WI.svg('play'); });
                audio.addEventListener('ended', function () { playing = false; playBtn.innerHTML = WI.svg('play'); inner.style.width = '0%'; });
                audio.addEventListener('timeupdate', function () {
                    if (audio.duration) inner.style.width = (audio.currentTime / audio.duration * 100) + '%';
                });
                bubble.appendChild(wrap);
                break;
            }

            case 'pic': {
                // 统一使用 image 格式：缩略图 + 标题，点击放大
                // 复用 image 分支的渲染逻辑，避免重复代码
                var imgWrap = document.createElement('div');
                imgWrap.className = 'img-msg';
                var img = document.createElement('img');
                img.className = 'img-thumb';
                img.loading = 'lazy';
                img.src = url;
                img.alt = '图片';
                imgWrap.appendChild(img);
                var openImg = function () { window.Media.viewImage(url); };
                img.addEventListener('click', openImg);
                img.addEventListener('load', scrollToBottom);
                bubble.classList.add('plain');
                bubble.appendChild(imgWrap);
                break;
            }

            case 'video': {
                bubble.classList.add('plain');
                var card = document.createElement('div');
                card.className = 'video-card';
                card.innerHTML = '<div class="play-triangle"></div>';
                var label = document.createElement('div');
                label.className = 'vc-label';
                label.textContent = msg.title || fileNameOf(url) || '视频';
                card.appendChild(label);
                card.addEventListener('click', function () {
                    window.Media.playVideo(url, label.textContent);
                });
                bubble.appendChild(card);
                break;
            }

            case 'music':
            case 'audio': {
                renderAudioCard(bubble, url, msg.title || fileNameOf(url) || '音频');
                break;
            }

            case 'url': {
                bubble.classList.add('plain');
                var lc = document.createElement('div');
                lc.className = 'link-card';
                lc.innerHTML =
                    '<div class="lc-ico">' + WI.svg('link') + '</div>' +
                    '<div class="lc-body"><div class="lc-title"></div><div class="lc-url"></div></div>';
                lc.querySelector('.lc-title').textContent = msg.title || hostOf(url) || '网页链接';
                lc.querySelector('.lc-url').textContent = url;
                lc.addEventListener('click', function () {
                    window.Browser.openInNewTab(url);
                });
                bubble.appendChild(lc);
                break;
            }

            case 'notice': {
                bubble.classList.add('plain');
                var nc = document.createElement('div');
                nc.className = 'notice-card';
                nc.innerHTML = '<span class="nc-ico">' + WI.svg('bell') + '</span><span class="nc-text"></span>';
                nc.querySelector('.nc-text').textContent = msg.text || '';
                bubble.appendChild(nc);
                break;
            }

            case 'image': {
                // 图片气泡：直接显示缩略图，点开大图。
                // 以前这里没有 image 分支，kind=image 的消息会掉进 default 变成一串文件名 ——
                // 电脑端发来的图片在手机上"只看到文字"就是这个原因。
                bubble.classList.add('plain');
                var imgWrap = document.createElement('div');
                imgWrap.className = 'img-msg';
                var img = document.createElement('img');
                img.className = 'img-thumb';
                img.loading = 'lazy';
                img.src = url || '';
                img.alt = msg.name || '图片';
                imgWrap.appendChild(img);
                var cap = document.createElement('div');
                cap.className = 'img-cap';
                cap.textContent = msg.name || '';
                imgWrap.appendChild(cap);
                var openImg = function () { openFileOf(url, msg.name || '图片', extOf(msg.name || url)); };
                img.addEventListener('click', openImg);
                cap.addEventListener('click', openImg);
                bubble.appendChild(imgWrap);
                break;
            }

            case 'file': {
                bubble.classList.add('plain');
                var fExt = extOf(msg.name || url);
                var fName = msg.name || fileNameOf(url) || '文件';
                var fSize = msg.size ? humanSize(Number(msg.size)) : '';
                var card = document.createElement('div');
                card.className = 'file-card';
                card.innerHTML =
                    '<div class="fc-ico"></div>' +
                    '<div class="fc-body">' +
                    '<div class="fc-name"></div>' +
                    '<div class="fc-meta"></div>' +
                    '</div>' +
                    '<div class="fc-act">打开</div>';
                card.querySelector('.fc-ico').innerHTML = WI.svg(fileIcon(fExt));
                card.querySelector('.fc-name').textContent = fName;
                card.querySelector('.fc-meta').textContent =
                    (fExt ? fExt.toUpperCase() + ' · ' : '') + (fSize || '文件');
                var openFn = function () { openFileOf(url, fName, fExt); };
                // 只在「打开」按钮上绑定点击，避免整张卡片点击穿透干扰气泡长按菜单
                card.querySelector('.fc-act').addEventListener('click', function (e) {
                    e.stopPropagation();
                    openFn();
                });
                bubble.appendChild(card);
                break;
            }

            default: {
                bubble.innerHTML = linkify(msg.text || '');
                if (msg.edited) {
                    var ed = document.createElement('span');
                    ed.className = 'msg-edited';
                    ed.textContent = ' (已编辑)';
                    bubble.appendChild(ed);
                }
                bubble.querySelectorAll('a[data-href]').forEach(function (a) {
                    a.addEventListener('click', function (e) {
                        e.preventDefault();
                        window.Browser.openInNewTab(a.getAttribute('data-href'));
                    });
                });
            }
        }
    }

    /** 纯文本中的裸链接转为可点击（在内置浏览器打开） */
    function linkify(text) {
        var escaped = escapeHtml(text);
        return escaped.replace(/(https?:\/\/[^\s<]+)/g, function (u) {
            return '<a href="#" data-href="' + u + '" style="color:#576b95">' + u + '</a>';
        });
    }

    function hostOf(url) {
        try { return new URL(url).hostname; } catch (e) { return ''; }
    }

    function formatClock(ts) {
        var d = new Date(ts || Date.now());
        var h = d.getHours(), m = d.getMinutes();
        return (h < 10 ? '0' : '') + h + ':' + (m < 10 ? '0' : '') + m;
    }

    /* ==================== 音乐 / 音频卡片（music 与 audio 共用） ==================== */

    function renderAudioCard(bubble, url, fallback) {
        var mc = document.createElement('div');
        mc.className = 'music-card';
        mc.innerHTML =
            '<div class="mc-cover">' + WI.svg('music') + '</div>' +
            '<div class="mc-info">' +
            '<div class="mc-name"></div>' +
            '<div class="mc-artist">点击播放</div>' +
            '</div>' +
            '<div class="mc-play">' + WI.svg('play') + '</div>';
        mc.querySelector('.mc-name').textContent = fallback;

        mc.addEventListener('click', function () {
            window.Media.playMusic(url, fallback);
        });
        bubble.appendChild(mc);

        // 登记到播放列表：跨设备远程控制的「上一曲/下一曲」按此顺序切换
        if (window.Media && window.Media.registerTrack) {
            window.Media.registerTrack(url, fallback);
        }

        // 异步补齐封面/歌名，不阻塞渲染
        window.Media.readAudioMeta(url, fallback).then(function (meta) {
            mc.querySelector('.mc-name').textContent = meta.title;
            mc.querySelector('.mc-artist').textContent = meta.artist;
            if (meta.cover) {
                var box = mc.querySelector('.mc-cover');
                box.innerHTML = '';
                var cimg = document.createElement('img');
                cimg.src = meta.cover;
                box.appendChild(cimg);
            }
        });
    }

    /* ==================== 文件卡片辅助 ==================== */

    /** 媒体快捷卡片的类别：只认「分享到频道」显式带的 mlib 标记，
     *  普通聊天附件不带标记，保持原有的图片预览/播放体验（与原生 MediaCard 一致） */
    function mlibOf(msg) {
        return msg.mlib || '';
    }

    function mlibMeta(mlib) {
        switch (mlib) {
            case 'book': return ['book', '阅读'];
            case 'music': return ['music', '音乐'];
            case 'photo': return ['image', '图片'];
            case 'video': return ['video', '视频'];
            default: return ['media', '媒体'];
        }
    }

    /** 媒体快捷卡片：类型徽标 + 文件名 + 大小，点击 = 添加到本机对应媒体库 */
    /* ---------------- 局域网音乐卡片 ---------------- */

    /* 同步听时钟：NTP 四时间戳（T1..T4）求发起端与本机的时钟差，
       offset=((T2-T1)+(T3-T4))/2、delay=(T4-T1)-(T3-T2)，取最小 RTT 样本。
       卡片 ts 是发起端时间，换算后才知道此刻真的播到哪，不会慢半拍。 */
    var sclk = { off: 0, rtt: 0, samples: [] };
    function probeClock(base) {
        if (!base) return;
        var t1 = Date.now();
        var xhr = new XMLHttpRequest();
        xhr.open('GET', String(base).replace(/\/$/, '') + '/api/sync/time', true);
        xhr.timeout = 1200;
        xhr.onload = function () {
            var t4 = Date.now();
            try {
                var j = JSON.parse(xhr.responseText);
                var t2 = Number(j.t2) || 0, t3 = Number(j.t3) || 0;
                if (!t2 || !t3) return;
                var rtt = (t4 - t1) - (t3 - t2);
                if (rtt < 0 || rtt > 5000) return;
                var off = ((t2 - t1) + (t3 - t4)) / 2;
                sclk.samples.push({ o: off, r: rtt });
                if (sclk.samples.length > 8) sclk.samples.shift();
                var best = sclk.samples.slice().sort(function (a, b) { return a.r - b.r; })[0];
                sclk.off = best.o; sclk.rtt = best.r;
            } catch (e) {}
        };
        try { xhr.send(); } catch (e) {}
    }
    function anchorAge(ts, atLocal) {
        if (sclk.rtt > 0 && ts > 0)
            return Math.max(0, Math.min(15000, Date.now() + sclk.off - ts));
        return Math.max(0, (window.performance ? performance.now() : Date.now()) - atLocal)
            + Math.max(30, sclk.rtt / 2);
    }

    function parseLrc(lrc) {
        var out = [];
        if (!lrc) return out;
        var lines = String(lrc).split('\n');
        for (var i = 0; i < lines.length; i++) {
            var m = lines[i].match(/^\[(\d+):(\d+(?:\.\d+)?)\]\s*(.*)$/);
            if (!m) continue;
            var t = (parseInt(m[1], 10) * 60 + parseFloat(m[2])) * 1000;
            if (m[3] && m[3].trim()) out.push({ t: t, s: m[3].trim() });
        }
        return out;
    }

    function lyricAt(list, pos) {
        var best = '';
        for (var i = 0; i < list.length; i++) {
            if (list[i].t <= pos) best = list[i].s; else break;
        }
        return best;
    }

    function fmtMs(ms) {
        if (!ms || ms < 0) return '00:00';
        var s = Math.floor(ms / 1000);
        var m = Math.floor(s / 60);
        s = s % 60;
        return (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    }

    /** 视频分享卡片：外源瀑布墙封面 + 右下角「观看」「一起看」。
        「观看」用外源 CDN 真实 m3u8；「一起看」用宿主机代理并跟随宿主机进度。 */
    function renderVideoShareCard(bubble, msg) {
        bubble.classList.add('plain');
        var card = document.createElement('div');
        card.style.cssText = 'width:250px;border-radius:12px;overflow:hidden;background:#111;cursor:pointer;position:relative;box-shadow:0 1px 3px rgba(0,0,0,.12)';

        var host = String(msg.watchHost || '').replace(/\/$/, '');

        // 构建封面 URL 降级链：按优先级尝试每个源，失败自动降级到下一个
        var coverSources = [];
        
        // 1. 内嵌 base64 封面（最高优先级，无需网络请求）
        if (msg.cover) {
            coverSources.push('data:image/jpeg;base64,' + msg.cover);
        }
        
        // 2. posterProxy（相对路径，需拼 host）
        var pp = msg.posterProxy || '';
        if (pp && host) {
            coverSources.push(host + (pp.charAt(0) === '/' ? pp : '/' + pp));
        }
        if (pp && /^https?:/i.test(pp)) {
            coverSources.push(pp);
        }
        
        // 3. 外源海报经宿主机代理（防跨域/防盗链）
        if (msg.poster && /^https?:/i.test(msg.poster)) {
            var rel = '/api/media/vod/poster?u=' + encodeURIComponent(msg.poster);
            coverSources.push(host ? host + rel : rel);
        } else if (msg.poster) {
            coverSources.push(msg.poster);
        }
        
        // 4. 生成兜底海报（按标题生成）
        var gen = '/api/media/vod/poster?n=' + encodeURIComponent(msg.title || msg.name || '视频');
        coverSources.push(host ? host + gen : gen);

        // 异步尝试加载封面，失败自动降级
        var img = document.createElement('img');
        img.alt = '';
        img.style.cssText = 'display:block;width:100%;height:140px;object-fit:cover;opacity:.85';
        
        function tryLoadSource(index) {
            if (index >= coverSources.length) {
                // 所有源都失败，显示占位图
                showPlaceholder();
                return;
            }
            var src = coverSources[index];
            img.src = src;
            img.onload = function () {
                card.insertBefore(img, card.firstChild); // 插入到卡片最前面
            };
            img.onerror = function () {
                tryLoadSource(index + 1); // 失败则尝试下一个源
            };
        }
        
        function showPlaceholder() {
            var ph = document.createElement('div');
            ph.style.cssText = 'height:140px;background:linear-gradient(135deg,#1b1f24,#2b323b);display:flex;align-items:flex-start;justify-content:flex-start;padding:12px;color:#6b7480';
            ph.innerHTML = WI.svg('video', 26);
            card.insertBefore(ph, card.firstChild);
        }
        
        tryLoadSource(0);

        var tri = document.createElement('div');
        tri.style.cssText = 'position:absolute;top:52px;left:50%;transform:translateX(-50%);width:44px;height:44px;border-radius:50%;background:rgba(0,0,0,.55);color:#fff;display:flex;align-items:center;justify-content:center';
        tri.innerHTML = WI.svg('play', 24);
        card.appendChild(tri);

        var label = document.createElement('div');
        label.style.cssText = 'padding:9px 10px 0;color:#fff;font-size:13px;line-height:1.35;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
        label.textContent = msg.title || msg.name || '分享的视频';
        card.appendChild(label);

        function openPlain() {
            var u = msg.url || msg.mediaUrl || '';
            if (!u) return;
            var name = msg.title || '分享的视频';
            var ok = false;
            try { ok = window.Bridge.openSharedVideo(u, name); } catch (e) {}
            if (!ok) { try { window.Media.playVideo(u, name); } catch (e) {} }
        }
        function openTogether() {
            var rel = msg.watchUrl || '';
            if (!host || !rel) { tip('发起端不在线，无法一起看'); return; }
            var u = /^https?:\/\//i.test(rel) ? rel : host + (rel.charAt(0) === '/' ? rel : '/' + rel);
            var name = msg.title || '分享的视频';
            var cardId = msg.watchCard || msg.id || '';
            var ok = false;
            try { ok = window.Bridge.openWatchTogether(u, name, cardId, host); } catch (e) {}
            if (!ok) {  // 旧版桥退化成自己播放宿主机代理（无进度跟随）
                try { ok = window.Bridge.openSharedVideo(u, name); } catch (e) {}
                if (!ok) { try { window.Media.playVideo(u, name); } catch (e) {} }
            }
        }
        function tip(t) { try { window.Bridge.toast(t); } catch (e) {} }

        var row = document.createElement('div');
        row.style.cssText = 'display:flex;justify-content:flex-end;gap:6px;padding:8px 10px 10px';
        function mkBtn(text, bg, fg, fn) {
            var b = document.createElement('button');
            b.type = 'button';
            b.textContent = text;
            b.style.cssText = 'border:0;background:' + bg + ';color:' + fg + ';border-radius:10px;padding:5px 10px;font-size:11px;cursor:pointer';
            b.onclick = function (ev) { ev.stopPropagation(); fn(); };
            return b;
        }
        row.appendChild(mkBtn('观看', '#e8f7fe', '#12b7f5', openPlain));
        row.appendChild(mkBtn('一起看', '#eafaf1', '#12a05c', openTogether));
        card.appendChild(row);
        card.addEventListener('click', openPlain);
        bubble.appendChild(card);
    }

    /** 程序更新卡片：解析 msg.text 里的 JSON，显示版本/说明/状态 + 安装/主页按钮。 */
    function renderUpdateCard(bubble, msg) {
        var card = null;
        try { card = JSON.parse(msg.text || ''); } catch (e) {}
        if (!card || card.schema !== 'mchat.update.v1') {
            var t = document.createElement('div');
            t.textContent = '程序更新';
            bubble.appendChild(t);
            return;
        }
        var box = document.createElement('div');
        box.style.cssText = 'background:var(--card,#fff);border:1px solid var(--line,#e8eaee);border-radius:12px;padding:12px;min-width:220px;max-width:300px';
        function line(txt, color, size, bold) {
            var d = document.createElement('div');
            d.textContent = txt;
            d.style.cssText = 'color:' + color + ';font-size:' + size + 'px' + (bold ? ';font-weight:600' : '') + ';margin:2px 0';
            return d;
        }
        box.appendChild(line('程序更新  ' + (card.version || ''), 'var(--text,#111)', 15, true));
        box.appendChild(line(card.title || '', 'var(--accent,#007aff)', 13, false));
        if (card.notes) box.appendChild(line(card.notes, 'var(--sub,#555)', 13, false));
        if (card.status) box.appendChild(line(card.status, 'var(--sub,#8e8e93)', 12, false));

        var row = document.createElement('div');
        row.style.cssText = 'display:flex;justify-content:flex-end;gap:6px;margin-top:8px';
        function mk(text, bg, fg, fn) {
            var b = document.createElement('button');
            b.type = 'button'; b.textContent = text;
            b.style.cssText = 'border:0;background:' + bg + ';color:' + fg + ';border-radius:10px;padding:5px 12px;font-size:12px;cursor:pointer';
            b.onclick = function (ev) { ev.stopPropagation(); fn(); };
            return b;
        }
        if (card.apk) row.appendChild(mk('安装', '#e8f7fe', '#007aff', function () {
            try {
                var ok = window.Bridge && window.Bridge.installApk && window.Bridge.installApk(card.apk);
                if (!ok) window.Bridge && window.Bridge.toast && window.Bridge.toast('安装包不存在，请重新检测更新');
            } catch (e) {}
        }));
        row.appendChild(mk('主页', '#f0f0f0', '#555', function () {
            try { window.Browser && window.Browser.openInNewTab && window.Browser.openInNewTab(card.home || 'https://github.com/yuwenhui2020/AhoChatA'); } catch (e) {}
        }));
        box.appendChild(row);
        bubble.appendChild(box);
    }

    /** 无封面占位：浅灰圆角方块 + 柔光圆 + 居中双音符（与 App 端一致，不显示程序图标）。 */
    var NO_COVER_URI = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 54 54">'
        + '<rect width="54" height="54" rx="10" fill="#e9edf4"/>'
        + '<circle cx="27" cy="27" r="13" fill="#dce4f0"/>'
        + '<path d="M23 36 V19 L34 16 V33" fill="none" stroke="#93a2b9" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
        + '<ellipse cx="20.4" cy="36" rx="3.5" ry="2.6" fill="#93a2b9"/>'
        + '<ellipse cx="31.4" cy="33" rx="3.5" ry="2.6" fill="#93a2b9"/>'
        + '</svg>');

    function renderMusicCard(bubble, msg) {
        bubble.classList.add('plain');
        var card = document.createElement('div');
        card.setAttribute('data-musiccard', '1');
        card.style.cssText = 'width:260px;padding:10px;border-radius:14px;background:#fff;border:1px solid #e8eaee;box-shadow:0 1px 3px rgba(0,0,0,.06);font-size:13px;color:#111';

        var top = document.createElement('div');
        top.style.cssText = 'display:flex;align-items:center;gap:10px';
        var img = document.createElement('img');
        img.style.cssText = 'width:54px;height:54px;border-radius:10px;object-fit:cover;background:#e9edf4';
        img.alt = '';
        img.src = msg.cover ? ('data:image/jpeg;base64,' + msg.cover) : NO_COVER_URI;
        img.onerror = function () { img.onerror = null; img.src = NO_COVER_URI; };
        top.appendChild(img);

        var info = document.createElement('div');
        info.style.cssText = 'flex:1;min-width:0';
        var title = document.createElement('div');
        title.style.cssText = 'font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
        title.textContent = msg.title || '未知歌曲';
        var artist = document.createElement('div');
        artist.style.cssText = 'font-size:11px;color:#8e9099;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
        artist.textContent = [msg.artist, msg.album].filter(Boolean).join(' · ');
        info.appendChild(title);
        info.appendChild(artist);
        top.appendChild(info);
        card.appendChild(top);

        var lyric = document.createElement('div');
        lyric.style.cssText = 'font-size:12px;color:#12b7f5;margin:8px 0 4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
        lyric.innerHTML = WI.svg('note');
        card.appendChild(lyric);

        var bar = document.createElement('div');
        bar.style.cssText = 'height:3px;background:#eef0f3;border-radius:2px;overflow:hidden';
        var fill = document.createElement('div');
        fill.style.cssText = 'height:100%;width:0;background:#12b7f5';
        bar.appendChild(fill);
        card.appendChild(bar);

        var row = document.createElement('div');
        row.style.cssText = 'display:flex;align-items:center;gap:6px;margin-top:6px';
        var time = document.createElement('div');
        time.style.cssText = 'font-size:10px;color:#8e9099;flex:1';
        var SVG_PREV = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="2.6" height="14" rx="1"/><path d="M19 5v14l-10-7z"/></svg>';
        var SVG_NEXT = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true"><rect x="16.4" y="5" width="2.6" height="14" rx="1"/><path d="M5 5v14l10-7z"/></svg>';
        var SVG_PLAY = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true"><path d="M8 5l11 7-11 7z"/></svg>';
        var SVG_PAUSE = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true"><rect x="8" y="5" width="3.2" height="14" rx="1"/><rect x="12.8" y="5" width="3.2" height="14" rx="1"/></svg>';

        var btn = function (svg, cmd) {
            var b = document.createElement('button');
            b.innerHTML = svg;
            b.style.cssText = 'border:0;background:transparent;padding:0 6px;cursor:pointer;color:#111;line-height:0;display:inline-flex;align-items:center';
            b.onclick = function () {
                try {
                    window.Bridge.send({
                        id: 'mc_' + Date.now() + '_' + Math.random().toString(36).slice(2, 6),
                        kind: 'mediacontrol',
                        text: '',
                        state: { cmd: cmd, position: -1, target: msg.from }
                    });
                } catch (e) {}
            };
            return b;
        };
        row.appendChild(btn(SVG_PREV, 'prev'));
        var playBtn = btn(SVG_PLAY, 'toggle');
        row.appendChild(playBtn);
        row.appendChild(btn(SVG_NEXT, 'next'));
        row.appendChild(time);
        card.appendChild(row);

        // 「一起听」共享歌单各播各的；「同步听」严格跟随同一首歌的进度与切歌。
        // patch 帧不带 playlist 时，msgupd 会把上一帧的列表回填到 msg 上。
        var plJson = msg.playlist || '';
        if (plJson) {
            try {
                if (typeof plJson !== 'string') plJson = JSON.stringify(plJson);
                card._playlist = plJson;
                card._ltIndex = Number(msg.ltIndex) || 0;
                card._cardId = msg.id || '';
                var joinRow = document.createElement('div');
                joinRow.style.cssText = 'display:flex;justify-content:flex-end;gap:6px;margin-top:8px;flex-wrap:wrap';

                function mkBtn(label, ico, bg, fg) {
                    var b = document.createElement('button');
                    b.type = 'button';
                    b.style.cssText = 'border:0;background:' + bg + ';color:' + fg + ';border-radius:10px;padding:5px 10px;font-size:11px;cursor:pointer;display:inline-flex;align-items:center;gap:4px';
                    b.innerHTML = WI.svg(ico, 14) + ' ' + label;
                    return b;
                }
                function tip(t) { try { window.Bridge.toast(t); } catch (e) {} }

                var jb = mkBtn('播放', 'play', '#e8f7fe', '#12b7f5');
                jb.setAttribute('data-share', 'playlist');
                jb.onclick = function (ev) {
                    ev.stopPropagation();
                    var idx = Number(msg.ltIndex) || 0;
                    var ok = false;
                    try { ok = window.Bridge.startSharedPlaylist(card._playlist, idx, card._cardId, msg.from || ''); } catch (e) {}
                    if (!ok) { try { ok = window.Bridge.joinListen(card._playlist, idx); } catch (e) {} }
                    if (!ok) {
                        // 调试环境无原生桥：退化为在网页里播放该列表的第一首
                        try {
                            var arr = JSON.parse(card._playlist);
                            var first = arr[idx] || arr[0];
                            if (first && first.u && window.Media) window.Media.playMusic(first.u, first.t || '');
                        } catch (e) {}
                    }
                    tip(ok ? '已播放共享歌单（可自由切歌）' : '已在网页端播放共享列表');
                };

                var st = null;
                try { st = window.Bridge.getSyncListen && window.Bridge.getSyncListen(); } catch (e) {}
                var syncing = !!(st && st.mode === 2 &&
                    (!st.cardId || !card._cardId || st.cardId === card._cardId));
                var sb = mkBtn(syncing ? '退出一起听' : '一起听', syncing ? 'close' : 'link',
                    syncing ? '#fff0f0' : '#eafaf1', syncing ? '#d9534f' : '#12a05c');
                sb.setAttribute('data-share', 'sync');
                sb.onclick = function (ev) {
                    ev.stopPropagation();
                    if (syncing) {
                        try { window.Bridge.exitSyncListen(); } catch (e) {}
                        tip('已退出一起听');
                        bubble.innerHTML = '';
                        bubble.className = 'bubble';
                        buildBody(bubble, msg);
                        return;
                    }
                    var ok = false;
                    try {
                        ok = window.Bridge.startSyncListen(
                            card._playlist, Number(msg.ltIndex) || 0, card._cardId, msg);
                    } catch (e) {}
                    tip(ok ? '已加入一起听，正在对齐播放进度…' : '一起听不可用');
                };
                joinRow.appendChild(jb);
                joinRow.appendChild(sb);
                card.appendChild(joinRow);
            } catch (e) {}
        }

        card._lrcKey = msg.lrcKey || '';
        // 歌词必须归属当前曲目：patch 合并可能把上一首的 lrc 带过来
        var lrcText = msg.lrc || '';
        if (lrcText && msg.lrcKey && msg.songKey && msg.lrcKey !== msg.songKey) lrcText = '';
        try { probeClock(msg.peerHost || msg.ltHost || ''); } catch (e) {}
        card._st = {
            duration: Number(msg.duration) || 0,
            position: Number(msg.position) || 0,
            playing: !!msg.playing,
            ts: Number(msg.ts) || 0,
            atLocal: window.performance ? performance.now() : Date.now(),
            lrc: parseLrc(lrcText),
            peek: 0
        };
        card._els = { lyric: lyric, fill: fill, time: time, play: playBtn, playSvg: SVG_PLAY, pauseSvg: SVG_PAUSE };
        tickMusicCard(card);
        // 订阅远程位置更新，实现实时进度同步
        if (!card._posUnsub) {
            card._posUnsub = subscribeRemotePosition(card);
        }
        if (!card._timer) card._timer = setInterval(function () { tickMusicCard(card); }, 250);
        bubble.appendChild(card);
    }

    function tickMusicCard(card) {
        try {
            var st = card._st;
            if (!st) return;
            var pos = st.position;
            // 用对时后的发起端时间轴推算，自动补偿投递时延与两机时钟差
            if (st.playing) pos += anchorAge(st.ts, st.atLocal);
            if (st.duration > 0) pos = Math.min(pos, st.duration);
            if (pos < 0) pos = 0;
            card._els.time.textContent = fmtMs(pos) + ' / ' + fmtMs(st.duration);
            card._els.fill.style.width = (st.duration > 0 ? Math.min(100, pos / st.duration * 100) : 0) + '%';
            card._els.play.innerHTML = st.playing ? card._els.pauseSvg : card._els.playSvg;
            var line = lyricAt(st.lrc, pos);
            if (line) { card._els.lyric.textContent = line; }
            else { card._els.lyric.innerHTML = WI.svg('note') + (st.playing ? ' 正在播放' : ' 已暂停'); }
        } catch (e) {}
    }

    // 订阅远程播放位置更新，实时同步聊天卡片进度
    function subscribeRemotePosition(card) {
        if (!window.RemoteMedia) return;
        var unsub = window.RemoteMedia.subscribePosition ?
            window.RemoteMedia.subscribePosition(function (position, playing, duration) {
                if (!card._st) return;
                // 仅当播放状态或时长变化时更新，避免频繁重绘
                var st = card._st;
                var changed = (st.playing !== playing) || (st.duration !== duration);
                if (changed) {
                    st.playing = playing;
                    st.duration = duration || st.duration;
                    st.position = position;
                    st.atLocal = window.performance ? performance.now() : Date.now();
                    st.ts = position; // 重置时间戳基准
                    // 立即重绘
                    tickMusicCard(card);
                } else if (playing) {
                    // 仅位置更新：直接更新位置并重绘进度条/歌词
                    st.position = position;
                    st.atLocal = window.performance ? performance.now() : Date.now();
                    st.ts = position;
                    card._els.time.textContent = fmtMs(position) + ' / ' + fmtMs(st.duration);
                    card._els.fill.style.width = (st.duration > 0 ? Math.min(100, position / st.duration * 100) : 0) + '%';
                    var line = lyricAt(st.lrc, position);
                    if (line) { card._els.lyric.textContent = line; }
                }
            }) : null;
        return unsub;
    }

    function renderMlibCard(bubble, msg, mlib) {
        bubble.classList.add('plain');
        var mm = mlibMeta(mlib);
        var url = resourceOf(msg);
        var fName = msg.name || fileNameOf(url) || '未命名';
        var fSize = msg.size ? humanSize(Number(msg.size)) : '';
        var card = document.createElement('div');
        card.className = 'file-card mlib-card';
        card.innerHTML =
            '<div class="fc-ico"></div>' +
            '<div class="fc-body">' +
            '<div class="fc-tag"></div>' +
            '<div class="fc-name"></div>' +
            '<div class="fc-meta"></div>' +
            '</div>' +
            '<div class="fc-act">添加</div>';
        card.querySelector('.fc-ico').innerHTML = WI.svg(mm[0]);
        card.querySelector('.fc-tag').textContent = mm[1];
        card.querySelector('.fc-name').textContent = fName;
        card.querySelector('.fc-meta').textContent =
            '点击添加到本机' + (fSize ? ' · ' + fSize : '');
        card.addEventListener('click', function () {
            if (window.Bridge && window.Bridge.importMlib) {
                var tip = window.Bridge.importMlib(msg.media || fName);
                if (window.Bridge.toast && tip) window.Bridge.toast(tip);
                if (card.querySelector('.fc-meta')) {
                    card.querySelector('.fc-meta').textContent = tip || '已处理';
                }
            } else if (window.Bridge && window.Bridge.toast) {
                window.Bridge.toast('当前环境不支持导入');
            }
        });
        bubble.appendChild(card);
    }

    function extOf(name) {
        if (!name) return '';
        var clean = String(name).split('?')[0].split('#')[0];
        var dot = clean.lastIndexOf('.');
        return dot >= 0 ? clean.substring(dot + 1).toLowerCase() : '';
    }

    function fileIcon(ext) {
        if ('jpg,jpeg,png,gif,webp,bmp,heic,heif,avif'.indexOf(ext) >= 0) return 'image';
        if ('mp4,mov,m4v,3gp,webm,mkv,avi,flv'.indexOf(ext) >= 0) return 'video';
        if ('mp3,wav,ogg,m4a,aac,flac,amr,opus'.indexOf(ext) >= 0) return 'music';
        if ('zip,rar,7z,tar,gz'.indexOf(ext) >= 0) return 'package';
        if (ext === 'apk') return 'package';
        return 'file';
    }

    function humanSize(bytes) {
        if (!bytes || bytes <= 0) return '';
        var units = ['B', 'KB', 'MB', 'GB'];
        var i = 0, n = bytes;
        while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
        return (i === 0 ? n : n.toFixed(1)) + ' ' + units[i];
    }

    /** 打开文件：图片/视频/音频用内置查看器，其余（PDF/文档/安装包…）交原生系统查看器。 */
    function openFileOf(url, name, ext) {
        if (!url) return;
        if ('jpg,jpeg,png,gif,webp,bmp,heic,heif,avif'.indexOf(ext) >= 0) { window.Media.viewImage(url); return; }
        if ('mp4,mov,m4v,3gp,webm,mkv,avi,flv'.indexOf(ext) >= 0) { window.Media.playVideo(url, name); return; }
        if ('mp3,wav,ogg,m4a,aac,flac,amr,opus'.indexOf(ext) >= 0) { window.Media.playMusic(url, name); return; }
        if (window.Bridge && window.Bridge.openFile) window.Bridge.openFile(url);
        else if (window.Bridge && window.Bridge.toast) window.Bridge.toast('当前环境不支持打开该文件');
    }

    function scrollToBottom() {
        listEl.scrollTop = listEl.scrollHeight;
    }

    function addSystemTip(text) {
        var wrap = document.createElement('div');
        wrap.className = 'sys-tip';
        var span = document.createElement('span');
        span.textContent = text;
        wrap.appendChild(span);
        listEl.appendChild(wrap);
        scrollToBottom();
    }

    /* ==================== 收发 ==================== */

    /* ==================== 会话条（房间 / 私聊）==================== */

    function convBox() { return document.getElementById('convChips'); }

    /** 重绘会话条：房间显示 #号，私聊显示 昵称，带未读数
     * 使用增量 DOM 更新：仅创建/移除/更新变化的元素，保留滚动位置和焦点
     */
    function refreshConvs() {
        var box = convBox();
        if (!box || !window.Bridge.getConversations) return;

        var data = window.Bridge.getConversations() || {};
        var items = data.items || [];
        var active = data.conv || currentConv;
        if (active && active !== currentConv) currentConv = active;

        // 内容没变就不重绘，避免定时刷新复位会话条滚动位置。
        // 必须把 updatedAt/last 拼进去：否则已读会话来新消息时预览与排序一直停在旧状态。
        var sig = currentConv + '|' + items.map(function (c) {
            return c.id + ':' + c.title + ':' + (c.unread || 0)
                 + ':' + (c.updatedAt || 0) + ':' + encodeURIComponent(c.last || '');
        }).join(',');
        if (box.getAttribute('data-sig') === sig) return;
        box.setAttribute('data-sig', sig);

        // 增量更新：建立现有 chip 映射
        var existingChips = {};
        var children = box.children;
        for (var i = 0; i < children.length; i++) {
            var child = children[i];
            var cid = child.getAttribute('data-conv-id');
            if (cid) existingChips[cid] = child;
        }

        var newChipsOrder = [];

        items.forEach(function (c) {
            var chip = existingChips[c.id];
            newChipsOrder.push(c.id);

            if (chip) {
                // 更新现有元素
                chip.className = 'conv-chip' + (c.id === currentConv ? ' active' : '');
                chip.innerHTML = c.kind === 'dm' ? ('<span class="wico">' + WI.svg('chat') + '</span> ' + escapeHtml(c.title)) : escapeHtml(c.title);
                
                // 更新/移除未读徽章
                var badge = chip.querySelector('.badge');
                if (c.unread > 0) {
                    if (!badge) {
                        badge = document.createElement('span');
                        badge.className = 'badge';
                        chip.appendChild(badge);
                    }
                    badge.textContent = c.unread > 99 ? '99+' : c.unread;
                } else if (badge) {
                    badge.remove();
                }
                
                delete existingChips[c.id];
            } else {
                // 创建新元素
                chip = document.createElement('button');
                chip.className = 'conv-chip' + (c.id === currentConv ? ' active' : '');
                chip.setAttribute('data-conv-id', c.id);
                chip.innerHTML = c.kind === 'dm' ? ('<span class="wico">' + WI.svg('chat') + '</span> ' + escapeHtml(c.title)) : escapeHtml(c.title);
                if (c.unread > 0) {
                    var badge = document.createElement('span');
                    badge.className = 'badge';
                    badge.textContent = c.unread > 99 ? '99+' : c.unread;
                    chip.appendChild(badge);
                }
                chip.addEventListener('click', function () { setConv(c.id); });
                var timer = null;
                chip.addEventListener('touchstart', function () {
                    timer = setTimeout(function () { askClear(c); }, 550);
                }, { passive: true });
                chip.addEventListener('touchend', function () { clearTimeout(timer); });
                chip.addEventListener('touchmove', function () { clearTimeout(timer); });
            }
        });

        // 移除不再存在的 chip
        Object.keys(existingChips).forEach(function (cid) {
            existingChips[cid].remove();
        });

        // 重新排序 DOM（保持 items 顺序）
        newChipsOrder.forEach(function (cid) {
            var chip = box.querySelector('[data-conv-id="' + cid + '"]');
            if (chip && chip.parentNode === box) {
                box.appendChild(chip); // 移动到末尾，保持顺序
            }
        });

        // 确保「＋ 房间」按钮在最后
        var addBtn = box.querySelector('.conv-chip.add');
        if (!addBtn) {
            addBtn = document.createElement('button');
            addBtn.className = 'conv-chip add';
            addBtn.textContent = '＋ 房间';
            addBtn.addEventListener('click', askJoinRoom);
        }
        box.appendChild(addBtn);
    }

    /* ---- 按会话保存草稿：避免切会话时把没发完的字带到别的会话（误发/隐私风险）---- */
    function draftKey(conv) { return 'mchat.draft.' + (conv || ''); }
    function saveDraft(conv) {
        try {
            if (!inputEl) return;
            if (inputEl.value.trim()) sessionStorage.setItem(draftKey(conv), inputEl.value);
            else sessionStorage.removeItem(draftKey(conv));
        } catch (e) { }
    }
    function loadDraft(conv) {
        try { return sessionStorage.getItem(draftKey(conv)) || ''; } catch (e) { return ''; }
    }

    /** 切换到某个会话：先存旧草稿 → 清屏 → 载入该会话历史 → 恢复新会话草稿 */
    function setConv(conv) {
        if (!conv || conv === currentConv) return;
        saveDraft(currentConv);              // 存旧会话没发完的字
        currentConv = conv;
        editingId = null;
        messages = [];
        if (listEl) listEl.innerHTML = '';
        if (inputEl) { inputEl.value = loadDraft(conv); autoGrow(); }  // 恢复新会话草稿
        window.Bridge.selectConv(conv);
        loadHistory();
        refreshConvs();
    }

    function loadHistory() {
        if (!window.Bridge.getMessages) return;
        var list = window.Bridge.getMessages(currentConv, 300) || [];
        list.forEach(function (m) {
            if (!m || (m.id && messages.some(function (x) { return x.id === m.id; }))) return;
            if (m.kind === 'control' || m.op) return;
            messages.push(m);
            renderMessage(m);
        });
        scrollToBottom();
    }

    /**
     * 加入房间：用自绘对话框而不是 window.prompt
     * （Android WebView 默认不实现 OnJsPrompt，prompt 会直接返回 null）。
     */
    function askJoinRoom() {
        var mask = document.createElement('div');
        mask.style.cssText = 'position:fixed;left:0;top:0;right:0;bottom:0;background:rgba(0,0,0,.35);display:flex;align-items:center;justify-content:center;z-index:3000;';

        var box = document.createElement('div');
        box.style.cssText = 'width:78%;max-width:340px;background:#fff;border-radius:12px;overflow:hidden;box-shadow:0 10px 30px rgba(0,0,0,.2);';

        var title = document.createElement('div');
        title.style.cssText = 'padding:16px 16px 6px;font-size:15px;font-weight:600;color:#1a1a1a;';
        title.textContent = '加入房间';
        box.appendChild(title);

        var body = document.createElement('div');
        body.style.cssText = 'padding:0 16px 14px;color:#8a8a8a;font-size:13px;line-height:1.5;';
        body.textContent = '输入房间号即可进入同一个频道（字母/数字/下划线），例如 mesh、501。';

        var input = document.createElement('input');
        input.type = 'text';
        input.placeholder = '房间号';
        input.style.cssText = 'width:100%;box-sizing:border-box;margin-top:10px;padding:9px 12px;border:1px solid #dcdcdc;border-radius:8px;font-size:15px;outline:none;';
        body.appendChild(input);
        box.appendChild(body);

        var actions = document.createElement('div');
        actions.style.cssText = 'display:flex;border-top:1px solid #dcdcdc;';

        function close() { if (mask.parentNode) document.body.removeChild(mask); }

        var cancel = document.createElement('button');
        cancel.textContent = '取消';
        cancel.style.cssText = 'flex:1;padding:13px 0;font-size:15px;background:#fff;border:none;color:#1a1a1a;';
        cancel.addEventListener('click', close);
        actions.appendChild(cancel);

        var ok = document.createElement('button');
        ok.textContent = '进入';
        ok.style.cssText = 'flex:1;padding:13px 0;font-size:15px;background:#fff;border:none;border-left:1px solid #dcdcdc;color:#07c160;font-weight:600;';
        ok.addEventListener('click', function () {
            var code = (input.value || '').trim();
            close();
            if (!code) return;
            var res = window.Bridge.joinRoom(code);
            if (res && res.conv) {
                currentConv = res.conv;
                messages = [];
                if (listEl) listEl.innerHTML = '';
                loadHistory();
                refreshConvs();
                addSystemTip('已进入房间 ' + (res.title || res.conv));
            }
        });
        actions.appendChild(ok);

        box.appendChild(actions);
        mask.appendChild(box);
        document.body.appendChild(mask);
        setTimeout(function () { input.focus(); }, 30);
    }

    function askClear(c) {
        if (!window.confirm('清空「' + (c.title || c.id) + '」的全部消息？在线设备会同步删除。')) return;
        window.Bridge.clearConversation(c.id);
        if (c.id === currentConv) {
            messages = [];
            if (listEl) listEl.innerHTML = '';
        }
        refreshConvs();
    }

    /** 长按/右键气泡菜单：跨平台统一交互
     * - 移动端：长按 350ms 触发，含触觉反馈 + 视觉放大动画
     * - 桌面端：右键点击即时触发
     * - 菜单定位在气泡上方/下方自适应，带入场动画
     */
    function bindLongPress(el, fn) {
        var timer = null;
        var pressStart = 0;
        var HOLD_MS = 350;  // 从 550ms 降低到 350ms，体感更快
        var hasTriggered = false;

        function trigger() {
            if (hasTriggered) return;
            hasTriggered = true;
            // 触觉反馈（移动端）
            try { navigator.vibrate && navigator.vibrate(10); } catch (e) {}
            // 视觉反馈：轻微放大
            el.style.transition = 'transform 0.1s ease';
            el.style.transform = 'scale(1.02)';
            setTimeout(function () { el.style.transform = ''; }, 100);
            fn();
        }

        function clear() {
            if (timer) { clearTimeout(timer); timer = null; }
        }

        // 触摸端：长按触发
        el.addEventListener('pointerdown', function (e) {
            // 仅主按钮/单指触摸
            if (e.button !== 0 && e.pointerType !== 'touch') return;
            pressStart = Date.now();
            hasTriggered = false;
            timer = setTimeout(trigger, HOLD_MS);
        }, { passive: true });

        el.addEventListener('pointerup', function (e) {
            clear();
            // 未触发长按时的 pointerup：可能是点击，不处理
        });

        el.addEventListener('pointercancel', clear);
        el.addEventListener('pointerleave', function (e) {
            // 指针滑出元素：取消长按判定
            clear();
        });

        // 桌面端：右键即时触发
        el.addEventListener('contextmenu', function (e) {
            e.preventDefault();
            // 右键不需要长按延迟，直接触发
            clear();
            trigger();
        });
    }

    function askDelete(msg) {
        if (!window.confirm('删除这条消息？双方都会删除。')) return;
        if (window.Bridge.deleteMessage(msg.id)) removeBubble(msg.id);
    }

    /** 长按气泡菜单：自己的消息可撤回/编辑，所有消息可复制/删除。
     * 菜单定位在气泡附近，带入场动画，点击空白处关闭。
     */
    function showMsgMenu(msg) {
        var isOut = msg.from === self.peerId;
        var isText = !msg.kind || msg.kind === 'text';
        var opts = [];
        opts.push({ label: '复制文本', act: function () {
            try { navigator.clipboard && navigator.clipboard.writeText(msg.text || ''); } catch (e) {}
            window.Bridge.toast('已复制');
        } });
        if (isOut) opts.push({ label: '撤回', act: function () {
            if (!window.confirm('撤回这条消息？双方都会删除。')) return;
            if (window.Bridge.recallMessage(msg.id)) removeBubble(msg.id);
        } });
        if (isOut && isText) opts.push({ label: '编辑', act: function () { beginEdit(msg); } });
        opts.push({ label: '删除(仅本机)', act: function () {
            if (window.Bridge.deleteMessage(msg.id)) removeBubble(msg.id);
        } });

        var mask = document.createElement('div');
        mask.className = 'msg-menu-mask';
        var box = document.createElement('div');
        box.className = 'msg-menu-box';
        // 入场动画
        box.style.opacity = '0';
        box.style.transform = 'translateY(10px) scale(0.95)';
        box.style.transition = 'opacity 0.15s ease, transform 0.15s ease';
        
        opts.forEach(function (o) {
            var b = document.createElement('button');
            b.className = 'msg-menu-item';
            b.textContent = o.label;
            b.addEventListener('click', function () { mask.remove(); o.act(); });
            box.appendChild(b);
        });
        var cancel = document.createElement('button');
        cancel.className = 'msg-menu-item cancel';
        cancel.textContent = '取消';
        cancel.addEventListener('click', function () { mask.remove(); });
        box.appendChild(cancel);
        mask.appendChild(box);
        
        // 触发重排以触发动画
        requestAnimationFrame(function () {
            box.style.opacity = '1';
            box.style.transform = 'translateY(0) scale(1)';
        });
        
        mask.addEventListener('click', function (e) { if (e.target === mask) mask.remove(); });
        document.body.appendChild(mask);
    }

    /** 进入编辑态：把原文填回输入框，发送时改为更新该消息。 */
    function beginEdit(msg) {
        editingId = msg.id;
        inputEl.value = msg.text || '';
        inputEl.focus();
        try { inputEl.setSelectionRange(inputEl.value.length, inputEl.value.length); } catch (e) {}
        autoGrow();
        window.Bridge.toast('编辑中：修改后发送即更新该消息');
    }

    /** 就地更新某条消息气泡的正文（编辑后即时生效）。 */
    function updateBubbleText(id, text) {
        if (!listEl || !id) return;
        var nodes = listEl.querySelectorAll('[data-mid]');
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].getAttribute('data-mid') !== id) continue;
            var bubble = nodes[i].querySelector('.bubble');
            if (bubble) {
                bubble.innerHTML = linkify(text);
                var ed = document.createElement('span');
                ed.className = 'msg-edited';
                ed.textContent = ' (已编辑)';
                bubble.appendChild(ed);
            }
            break;
        }
        for (var j = 0; j < messages.length; j++) {
            if (messages[j].id === id) { messages[j].text = text; messages[j].edited = true; break; }
        }
    }

    /** 从界面移除一条消息（含其上方的时间行） */
    function removeBubble(id) {
        if (!listEl || !id) return;
        var nodes = listEl.querySelectorAll('[data-mid]');
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].getAttribute('data-mid') !== id) continue;
            var prev = nodes[i].previousSibling;
            if (prev && prev.classList && prev.classList.contains('msg-time-row')) {
                listEl.removeChild(prev);
            }
            listEl.removeChild(nodes[i]);
            messages = messages.filter(function (m) { return m.id !== id; });
            return;
        }
    }

    function acceptMessage(msg) {
        if (!msg) return;

        // 控制帧：删除 / 撤回 / 编辑 / 清空，不渲染气泡
        if (msg.kind === 'control' || msg.op) {
            if ((msg.op === 'del' || msg.op === 'recall') && msg.target) removeBubble(msg.target);
            else if (msg.op === 'edit' && msg.target) updateBubbleText(msg.target, msg.text || '');
            else if (msg.op === 'clear' && (msg.conv || currentConv) === currentConv) {
                messages = [];
                listEl.innerHTML = '';
            }
            return;
        }

        // 只渲染当前会话的消息；其它会话的变化只更新会话条
        if (msg.conv && msg.conv !== currentConv) {
            refreshConvs();
            return;
        }

        // 机器人流式回复：最终消息到达时撤掉流式气泡，由下面常规渲染生成正式气泡
        if (msg.bot && msg.id && botStreams[msg.id]) removeBotStream(msg.id);

        messages.push(msg);
        renderMessage(msg);

        // [notice] 无论自己发的还是收到的，都推送到本机系统通知
        if (msg.kind === 'notice') {
            var title = (msg.from === self.peerId) ? '我发送的通知' : (msg.fromName || '新通知');
            window.Bridge.notify(title, msg.text || '');
        }

        // 收到他人发来的「无前缀普通消息」自动复制到剪贴板。
        // 原生侧在后台已做同样处理，这里覆盖 App 在前台时来自网页访客的消息。
        // 机器人自己的回复不进剪贴板（避免复读机效果）。
        var isIncoming = msg.from !== self.peerId;
        var isPlain = !msg.kind || msg.kind === 'text';
        if (isIncoming && isPlain && msg.text && !msg.bot) {
            window.Bridge.copyToClipboard(msg.text);
        }
    }

    /* ==================== 机器人流式回复（逐字显示） ==================== */

    var botStreams = {};   // id -> {row, bubble, textEl, text, done}

    function createBotStreamBubble(id) {
        var row = document.createElement('div');
        row.className = 'msg-row in bot-stream';

        var avatar = document.createElement('div');
        avatar.className = 'avatar';
        avatar.innerHTML = WI.svg('bot');

        var main = document.createElement('div');
        main.className = 'msg-main';

        var bubble = document.createElement('div');
        bubble.className = 'bubble bot-bubble streaming';

        var textEl = document.createElement('span');
        textEl.className = 'bot-stream-text';
        bubble.appendChild(textEl);

        var cursor = document.createElement('span');
        cursor.className = 'bot-cursor';
        cursor.textContent = '▍';
        bubble.appendChild(cursor);

        main.appendChild(bubble);
        row.appendChild(avatar);
        row.appendChild(main);
        listEl.appendChild(row);
        scrollToBottom();

        return { row: row, bubble: bubble, textEl: textEl, text: '', done: false };
    }

    function removeBotStream(id) {
        var s = botStreams[id];
        if (!s) return;
        delete botStreams[id];
        try { s.row.remove(); } catch (e) {}
    }

    /** 原生推送的流式增量：{id, started, delta, done, ok} */
    function handleBotStream(p) {
        if (!p || !p.id) return;
        var s = botStreams[p.id];
        if (p.started) {
            if (s) return;
            botStreams[p.id] = createBotStreamBubble(p.id);
            return;
        }
        if (!s) s = botStreams[p.id] = createBotStreamBubble(p.id);

        if (p.delta) {
            s.text += p.delta;
            s.textEl.textContent = s.text;
            scrollToBottom();
        }
        if (p.done) {
            s.done = true;
            s.bubble.classList.remove('streaming');
            // 最终正式消息紧随其后到达（acceptMessage 会撤掉本气泡），
            // 兜底：若 1.5 秒后仍未到达则保留当前全文作为正式展示。
            setTimeout(function () {
                var cur = botStreams[p.id];
                if (cur) {
                    cur.bubble.classList.remove('streaming');
                    delete botStreams[p.id];
                }
            }, 1500);
        }
    }

    function sendText() {
        var raw = inputEl.value;
        if (!raw.trim()) return;

        // 编辑态：更新已有消息而非发送新消息
        if (editingId) {
            var eid = editingId;
            var ok = window.Bridge.editMessage(currentConv, eid, raw.trim());
            if (ok) {
                editingId = null;
                updateBubbleText(eid, raw.trim());
                inputEl.value = '';
                autoGrow();
            } else {
                window.Bridge.toast('编辑失败，内容已保留');   // 失败不清空、不退出编辑态
            }
            return;
        }

        var parsed = parseContent(raw);
        var sent = window.Bridge.send(parsed);
        if (sent) {
            acceptMessage(sent);
            inputEl.value = '';        // 只有发送成功才清空，失败保留原文可重试
            saveDraft(currentConv);    // 同步清掉草稿
            autoGrow();
        } else {
            window.Bridge.toast('发送失败，内容已保留，点发送可重试');
        }
    }

    /**
     * 发送本地文件（图片/视频/音乐）。文件被读为 base64 交给原生：
     * 原生写入私有媒体目录并把二进制随帧同步到局域网内所有设备。
     */
    function sendFile(file, kind) {
        if (!file) return;
        // 文件传输统一走 qaul：qaul 私聊 / qaul 群 / 频道（chan）支持，
        // 与 app 端 SendMessage 的判定（IsQaulDm || IsQaulGroup || IsChan）保持一致。
        var conv = window.Bridge.conv || '';
        if (conv.indexOf('qaul:') !== 0 && conv.indexOf('qgroup:') !== 0 && conv.indexOf('chan:') !== 0) {
            window.Bridge.toast('文件传输仅支持 qaul 会话（qaul 私聊 / qaul 群 / 频道）');
            return;
        }
        var reader = new FileReader();
        reader.onload = function () {
            var base64 = String(reader.result).split(',')[1] || '';
            var sent = window.Bridge.send(
                { kind: kind, title: file.name, text: file.name },
                base64,
                file.name
            );
            if (sent) acceptMessage(sent);
            else window.Bridge.toast('发送失败');
        };
        reader.onerror = function () { window.Bridge.toast('读取文件失败'); };
        reader.readAsDataURL(file);
    }

    function autoGrow() {
        inputEl.style.height = 'auto';
        inputEl.style.height = Math.min(inputEl.scrollHeight, 120) + 'px';
    }

    function updatePeers(peers) {
        // 移动数据/无网络时不扫描局域网，给出明确提示而非"没有设备"
        var net = window.Bridge.getNetworkState();
        if (!net || !net.scan) {
            if (net && net.type === 'Mobile') statusEl.textContent = '移动数据·未扫描局域网';
            else statusEl.textContent = '无网络·未扫描局域网';
            return;
        }
        if (!peers || !peers.length) {
            statusEl.textContent = '当前局域网内没有其他设备';
            return;
        }
        statusEl.textContent = '在线 ' + peers.length + ' 台：' +
            peers.map(function (p) { return p.name; }).join('、');
    }

    /* ==================== 语音录制（原生 AudioRecord，规避 WebRTC 限制） ==================== */

    var Voice = (function () {
        var startTime = 0;
        var timerId = null;
        var recording = false;

        function supported() {
            return window.Bridge.available;   // 仅 App 内（含原生桥）支持录音
        }

        function start(onTick) {
            if (recording) return false;
            if (!supported()) { window.Bridge.toast('当前环境不支持录音'); return false; }
            recording = true;
            startTime = Date.now();
            window.Bridge.startVoiceRecord();
            if (onTick) {
                timerId = setInterval(function () {
                    onTick(Math.round((Date.now() - startTime) / 1000));
                }, 200);
            }
            return true;
        }

        function stop() {
            if (!recording) return;
            recording = false;
            if (timerId) { clearInterval(timerId); timerId = null; }
            window.Bridge.stopVoiceRecord();
        }

        /** 原生录音完成回调：拿到 WAV base64 后发送为语音消息 */
        function onResult(res) {
            recording = false;
            if (timerId) { clearInterval(timerId); timerId = null; }
            if (!res) return;
            if (res.error) { window.Bridge.toast(res.error); return; }
            var dur = res.duration ? Number(res.duration) : Math.max(1, Math.round((Date.now() - startTime) / 1000));
            var sent = window.Bridge.send(
                { kind: 'voice', text: '语音', duration: dur }, res.base64, res.name || 'voice.wav');
            if (sent) acceptMessage(sent);
            else window.Bridge.toast('发送失败');
        }

        return { start: start, stop: stop, supported: supported, onResult: onResult };
    })();

    /* ==================== 语音通话（原生 AudioRecord/AudioTrack，不走 WebRTC） ==================== */

    var Call = (function () {
        var overlay = null;
        var minBubble = null;
        var stateEl = null;
        var nameEl = null;
        var avatarEl = null;
        var acceptBtn = null;
        var hangupBtn = null;
        var minBtn = null;
        var currentPeer = null;     // {id, name}
        var closeTimer = null;      // 挂断后延时关闭计时器
        var isMinimized = false;    // 通话是否最小化

        function el(id) { return document.getElementById(id); }

        function show() { if (overlay) overlay.classList.add('show'); }
        function hide() { if (overlay) overlay.classList.remove('show'); }
        function setState(s) { if (stateEl) stateEl.textContent = s; }
        function setName(n) { if (nameEl) nameEl.textContent = n || '通话'; }
        function showBubble(v) { if (minBubble) minBubble.classList.toggle('hidden', !v); }
        
        function updateMinimizeButton() {
            if (minBtn) {
                minBtn.setAttribute('aria-pressed', isMinimized ? 'true' : 'false');
                minBtn.title = isMinimized ? '恢复通话' : '最小化通话';
            }
        }

        function resetAll() {
            currentPeer = null;
            if (acceptBtn) acceptBtn.classList.add('hidden');
            if (hangupBtn) hangupBtn.classList.add('hidden');
            showBubble(false);
            isMinimized = false;
            updateMinimizeButton();
        }

        // 挂断/结束后：立即重置界面状态并隐藏，双方均可再次拨打，不滞留任何状态
        function scheduleClose(msg) {
            if (msg) setState(msg);
            if (closeTimer) clearTimeout(closeTimer);
            closeTimer = null;
            // 直接重置：清空对端、隐藏按钮、收起界面，避免停留在「对方已挂断」等文字
            // 如果是最小化状态，先隐藏气泡
            if (isMinimized) {
                showBubble(false);
                isMinimized = false;
                updateMinimizeButton();
            }
            resetAll();
            hide();
        }

        /** 发起呼叫到指定设备（拨打方：不显示接听按钮） */
        function call(peer) {
            if (!peer) return;
            if (closeTimer) { clearTimeout(closeTimer); closeTimer = null; }
            currentPeer = peer;
            showBubble(false);
            show();
            setName(peer.name || peer.id);
            setState('正在呼叫 ' + (peer.name || peer.id) + '…');
            if (avatarEl) avatarEl.innerHTML = WI.svg('phone');
            if (acceptBtn) acceptBtn.classList.add('hidden');
            if (hangupBtn) hangupBtn.classList.remove('hidden');
            window.Bridge.callInvite(peer);
        }

        function accept() {
            if (acceptBtn) acceptBtn.classList.add('hidden');
            setState('连接中…');
            window.Bridge.callAccept();
        }

        function hangup() {
            if (closeTimer) { clearTimeout(closeTimer); closeTimer = null; }
            // 立即隐藏界面并通知原生挂断；原生会回推 onCallEnded 再次确保重置
            // 如果是最小化状态，先隐藏气泡
            if (isMinimized) {
                showBubble(false);
                isMinimized = false;
                updateMinimizeButton();
            }
            resetAll();
            hide();
            window.Bridge.callHangup();
        }

        /** 最小化：仅隐藏通话界面，通话仍在后台进行，可点悬浮气泡恢复 */
        function minimize() {
            hide();
            showBubble(true);
            isMinimized = true;
            updateMinimizeButton();
        }
        function restore() {
            showBubble(false);
            show();
            isMinimized = false;
            updateMinimizeButton();
            // 恢复时刷新联系人列表（对方可能已挂断/新来电）
            if (currentPeer && window.Bridge && window.Bridge.getPeers) {
                renderCallPeers(window.Bridge.getPeers());
            }
        }

        /** 原生推送的通话状态文本（来自 VoiceCallManager） */
        function onCallState(s) {
            if (!s) return;
            setState(s.state || '');
            if (acceptBtn) acceptBtn.classList.toggle('hidden', !s.showAccept);
            if (hangupBtn) hangupBtn.classList.toggle('hidden', !s.showHangup);
        }

        /** 原生推送的通话阶段：idle / incoming / active */
        function onCallPhase(phase, name) {
            if (phase === 'incoming') {
                show();
                showBubble(false);
                setName(name || '对端');
                setState('来电…');
                if (avatarEl) avatarEl.innerHTML = WI.svg('phone');
                if (acceptBtn) acceptBtn.classList.remove('hidden');
                if (hangupBtn) hangupBtn.classList.remove('hidden');
            } else if (phase === 'idle') {
                // 由 onCallEnded 统一处理延时关闭
            } else if (phase === 'active') {
                setState('通话中');
                if (avatarEl) avatarEl.innerHTML = WI.svg('phone');
                if (acceptBtn) acceptBtn.classList.add('hidden');
                if (hangupBtn) hangupBtn.classList.remove('hidden');
            }
        }

        /** 原生推送通话结束（含超时/对方挂断），2 秒后彻底关闭并重置 */
        function onCallEnded(reason) {
            // 如果是最小化状态，先隐藏气泡
            if (isMinimized) {
                showBubble(false);
                isMinimized = false;
                updateMinimizeButton();
            }
            scheduleClose(reason || '通话已结束');
        }

        function init() {
            overlay = el('callOverlay');
            minBubble = el('callMinBubble');
            stateEl = el('callState');
            nameEl = el('callName');
            avatarEl = el('callAvatar');
            acceptBtn = el('callAccept');
            hangupBtn = el('callHangup');
            minBtn = el('callMinimize');

            if (hangupBtn) hangupBtn.addEventListener('click', hangup);
            if (acceptBtn) acceptBtn.addEventListener('click', accept);
            if (minBtn) {
                minBtn.addEventListener('click', function () {
                    if (isMinimized) restore();
                    else minimize();
                });
            }
            // 初始化按钮状态
            updateMinimizeButton();
            if (minBubble) minBubble.addEventListener('click', restore);

            // 原生通过这几个全局回调驱动 UI（由 VoiceCallManager 调用）
            window.__lanCallState = onCallState;
            window.__lanCall = onCallPhase;
            window.__lanCallEnded = onCallEnded;
        }

        return { init: init, call: call, hangup: hangup };
    })();

    /* ===================== 通知转发 ===================== */
    function initNotifRelay() {
        var bar = document.getElementById('notifRelayBar');
        var btn = document.getElementById('btnEnableNotif');
        if (!bar) return;

        function refresh() {
            var enabled = false;
            try { enabled = window.Bridge.isNotificationListenerEnabled(); } catch (e) {}
            bar.classList.toggle('hidden', enabled);
        }

        if (btn) {
            btn.addEventListener('click', function () {
                try { window.Bridge.openNotificationSettings(); } catch (e) {}
                // 设置页是外部应用，返回后无法自动感知；1.5 秒后复查一次
                setTimeout(refresh, 1500);
            });
        }

        refresh();
        // 进入前台时复查（从设置页返回）
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) refresh();
        });
    }

    /* ==================== 初始化 ==================== */

    /** 通知转发：检测系统「通知使用权」授权状态，未授权时显示引导条。 */
    function initNotifRelay() {
        var bar = document.getElementById('notifRelayBar');
        var btn = document.getElementById('btnEnableNotif');
        if (!bar) return;

        function refresh() {
            var enabled = false;
            try { enabled = window.Bridge.isNotificationListenerEnabled(); } catch (e) {}
            bar.classList.toggle('hidden', enabled);
        }

        if (btn) {
            btn.addEventListener('click', function () {
                try { window.Bridge.openNotificationSettings(); } catch (e) {}
                // 设置页是外部应用，返回后无法自动感知；1.5 秒后复查一次
                setTimeout(refresh, 1500);
            });
        }

        refresh();
        // 进入前台时复查（从设置页返回）
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) refresh();
        });
    }

    /* ===================== 功能控制菜单 ===================== */
    // 这些指令以 (命令) 形式发送；接收方识别后会模拟对应输入。
    // 圆环上已包含的指令（音量加/减、上一曲/下一曲、播放暂停）不在此重复出现。
    var CONTROL_COMMANDS = [
        '静音'
    ];

    function sendControl(cmd) {
        try { if (window.Bridge.sendControl) window.Bridge.sendControl(cmd); } catch (e) {}
    }

    function initControlMenu() {
        var btn = document.getElementById('btnControl');
        var menu = document.getElementById('controlMenu');
        var grid = document.getElementById('controlGrid');
        var closeBtn = document.getElementById('btnControlClose');
        if (!btn || !menu || !grid) return;

        // 统一的面板管理器
        var panels = {
            attach: document.getElementById('attachBar'),
            control: document.getElementById('controlMenu'),
            location: document.getElementById('locationCard')
        };

        function closeAllPanels(except) {
            Object.keys(panels).forEach(function (key) {
                if (key !== except && panels[key]) {
                    panels[key].classList.add('hidden');
                    panels[key].setAttribute('aria-hidden', 'true');
                }
            });
        }

        // 圆环上的按钮：直接读取 data-cmd（已在 HTML 中定义）
        if (!menu.dataset.dialBound) {
            var dialBtns = menu.querySelectorAll('[data-cmd]');
            dialBtns.forEach(function (b) {
                b.addEventListener('click', function () {
                    sendControl(b.getAttribute('data-cmd'));
                });
            });
            menu.dataset.dialBound = '1';
        }

        // 其余功能：另起一行，左对齐铺开
        if (!grid.dataset.built) {
            CONTROL_COMMANDS.forEach(function (cmd) {
                var item = document.createElement('button');
                item.className = 'control-item';
                item.textContent = cmd;
                item.addEventListener('click', function () {
                    sendControl(cmd);
                });
                grid.appendChild(item);
            });
            grid.dataset.built = '1';
        }

        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            closeAllPanels('control');
            menu.classList.toggle('hidden');
            menu.setAttribute('aria-hidden', menu.classList.contains('hidden') ? 'true' : 'false');
        });

        // 点击外部关闭
        document.addEventListener('click', function (e) {
            if (!menu.classList.contains('hidden') && 
                !menu.contains(e.target) && 
                e.target !== document.getElementById('btnControl')) {
                menu.classList.add('hidden');
                menu.setAttribute('aria-hidden', 'true');
            }
        });

        // ESC 键关闭
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !menu.classList.contains('hidden')) {
                menu.classList.add('hidden');
                menu.setAttribute('aria-hidden', 'true');
            }
        });

        if (closeBtn) {
            closeBtn.addEventListener('click', function () {
                menu.classList.add('hidden');
                menu.setAttribute('aria-hidden', 'true');
            });
        }
    }

    /* ===================== 位置共享 ===================== */
    // 入口：语音输入右、功能控制左的绿色「位置」按钮。
    // 开启后向系统申请 GPS + 朝向，并把自身位置周期广播给局域网其他设备；
    // 地图为纯白底，本机绿点+朝向箭头，其他设备蓝点，点击蓝点显示三坐标差（米，高度带正负）。
    function initLocationShare() {
        var btn = document.getElementById('btnLocation');
        var card = document.getElementById('locationCard');
        var map = document.getElementById('locationMap');
        var hint = document.getElementById('locationHint');
        var detail = document.getElementById('locationDetail');
        var selfEl = document.getElementById('locationSelf');
        var topToggle = document.getElementById('btnLocShare');
        if (!btn || !card || !map) return;

        var sharing = false;
        var selfLoc = null;          // {lat,lon,alt,bearing}
        var peers = {};              // from -> {lat,lon,alt,name,bearing}
        var selected = null;         // 当前选中查看的 peer from

        function project() {
            // 清除旧点
            var olds = map.querySelectorAll('.loc-dot, .loc-arrow');
            olds.forEach(function (n) { n.remove(); });
            if (!selfLoc) { hint.classList.remove('hidden'); return; }
            hint.classList.add('hidden');

            // 计算各点相对本机的米偏移（北、东）
            var lat0 = selfLoc.lat * Math.PI / 180;
            var cos = Math.cos(lat0);
            var pts = [{ from: 'self', lat: selfLoc.lat, lon: selfLoc.lon, alt: selfLoc.alt, self: true }];
            Object.keys(peers).forEach(function (k) {
                pts.push({ from: k, lat: peers[k].lat, lon: peers[k].lon, alt: peers[k].alt, self: false, name: peers[k].name });
            });

            var maxR = 5;   // 最小 5 米，避免点过于接近时缩放过大
            pts.forEach(function (p) {
                p.dn = (p.lat - selfLoc.lat) * 111320;
                p.de = (p.lon - selfLoc.lon) * 111320 * cos;
                var r = Math.sqrt(p.dn * p.dn + p.de * p.de);
                if (r > maxR) maxR = r;
            });

            // 自适应：让最远的点刚好落在白底区域 45% 半径处，确保所有点都装得下
            var W = map.clientWidth, H = map.clientHeight;
            var scale = (Math.min(W, H) * 0.45) / maxR;

            // 画布固定为北向上，不随设备旋转；朝向由箭头实时指示
            map.style.transform = 'none';

            pts.forEach(function (p) {
                var x = W / 2 + p.de * scale;
                var y = H / 2 - p.dn * scale;
                if (p.self) {
                    // 自身箭头：始终指向正上方（画布不旋转，本机朝向固定为上）
                    var arrow = document.createElement('div');
                    arrow.className = 'loc-arrow self';
                    arrow.style.left = x + 'px';
                    arrow.style.top = y + 'px';
                    arrow.style.transform = 'translate(-50%, -100%) rotate(0deg)';
                    map.appendChild(arrow);
                    var dot = document.createElement('div');
                    dot.className = 'loc-dot self';
                    dot.style.left = x + 'px'; dot.style.top = y + 'px';
                    map.appendChild(dot);
                } else {
                    var dot = document.createElement('div');
                    dot.className = 'loc-dot peer' + (selected === p.from ? ' selected' : '');
                    dot.style.left = x + 'px'; dot.style.top = y + 'px';
                    dot.title = p.name || p.from;
                    dot.addEventListener('click', function (e) {
                        e.stopPropagation();
                        selected = (selected === p.from) ? null : p.from;
                        showDetail(p.from);
                        project();
                    });
                    map.appendChild(dot);
                    // 其他设备的朝向箭头：显示其真实朝向（不固定为正上方）
                    var parrow = document.createElement('div');
                    parrow.className = 'loc-arrow peer';
                    parrow.style.left = x + 'px';
                    parrow.style.top = y + 'px';
                    parrow.style.transform = 'translate(-50%, -100%) rotate(' + (peers[p.from] ? (peers[p.from].bearing || 0) : 0) + 'deg)';
                    map.appendChild(parrow);
                }
            });
        }

        // 平面坐标用方位词描述：正值=更北/更东，负值=更南/更西
        function dirText(abs, posWord, negWord) {
            var v = Math.abs(abs);
            if (v < 0.05) return '基本同轴（0 m）';
            return (abs > 0 ? posWord : negWord) + ' ' + v.toFixed(1) + ' m';
        }

        function showDetail(from) {
            var p = peers[from];
            if (!p || !selfLoc) { detail.classList.add('hidden'); return; }
            var dn = (p.lat - selfLoc.lat) * 111320;            // 北向差（米，正值=更北）
            var de = (p.lon - selfLoc.lon) * 111320 * Math.cos(selfLoc.lat * Math.PI / 180); // 东向差
            var dh = p.alt - selfLoc.alt;                       // 高度差（米，正=高于自己）
            detail.classList.remove('hidden');
            detail.innerHTML =
                '<b>' + (p.name || from) + '</b><br>' +
                '南北：' + dirText(dn, '向北', '向南') + '<br>' +
                '东西：' + dirText(de, '向东', '向西') + '<br>' +
                '高度差：' + (dh >= 0 ? '+' : '') + dh.toFixed(1) + ' m';
        }

        function fmtLoc(l) {
            if (!l) return '定位中…';
            var s = '纬度 ' + l.lat.toFixed(5) + '，经度 ' + l.lon.toFixed(5) +
                   '，海拔 ' + l.alt.toFixed(1) + ' m';
            // 定位精度（米）：由原生按 provider 择优后上报，>50m 时提示精度偏低
            if (l.acc && l.acc > 0) {
                var a = l.acc < 10 ? l.acc.toFixed(1) : String(Math.round(l.acc));
                s += '（精度 ±' + a + ' m' + (l.acc > 50 ? '，偏低' : '') + '）';
            }
            return s;
        }

        // 开启/关闭位置共享：关闭后停止刷新本机坐标与朝向（本机视为静止），
        // 但不清除已显示的本机数据，也不影响继续接收其他设备的位置更新。
        // hideCard=true 时一并隐藏卡片（位置按钮切换用）；否则仅停止共享、保留卡片。
        function setSharing(on, hideCard) {
            if (on === sharing) return;
            sharing = on;
            btn.classList.toggle('active', on);
            if (topToggle) { topToggle.textContent = on ? '关闭' : '共享'; topToggle.classList.toggle('sharing', on); }
            if (on) {
                card.classList.remove('hidden');
                card.setAttribute('aria-hidden', 'false');
                peers = {}; selected = null;
                detail.classList.add('hidden');
                hint.classList.remove('hidden');
                hint.textContent = '正在获取 GPS 定位…';
                if (window.AndroidMchat && AndroidMchat.startLocationShare) AndroidMchat.startLocationShare();
            } else {
                if (window.AndroidMchat && AndroidMchat.stopLocationShare) AndroidMchat.stopLocationShare();
                if (hideCard) {
                    card.classList.add('hidden');
                    card.setAttribute('aria-hidden', 'true');
                } else {
                    hint.classList.remove('hidden');
                    hint.textContent = '已停止共享（保留上次数据，仍可接收他人位置）';
                }
            }
            project();
        }

        // 位置按钮：展开卡片即自动开启定位；收起卡片即自动关闭定位。
        // 已展开（共享中）时点击则隐藏卡片并停止共享；未展开时点击展开并自动开始定位。
        btn.addEventListener('click', function () {
            if (sharing) {
                setSharing(false, true);   // 收起并停止定位
            } else {
                setSharing(true, false);   // 展开并自动开启定位
            }
        });

        // 标题栏 共享/关闭 按钮
        if (topToggle) topToggle.addEventListener('click', function () {
            setSharing(!sharing);
        });

        // 点击地图空白处隐藏坐标差
        map.addEventListener('click', function () {
            if (selected) { selected = null; detail.classList.add('hidden'); project(); }
        });

        // 每秒重绘一次：保持他人位置实时刷新（本机停止共享后冻结，但仍重绘）
        var redrawTimer = setInterval(function () { project(); }, 1000);

        // 原生推送：自身位置更新（仅在共享开启时更新，关闭后本机视为静止）
        window.Bridge.on('selflocation', function (p) {
            if (!sharing) return;
            selfLoc = { lat: p.lat, lon: p.lon, alt: p.alt, bearing: p.bearing, acc: p.acc || 0 };
            selfEl.textContent = fmtLoc(selfLoc);
            project();
        });
        // 原生推送：其他设备位置更新（无论本机是否共享都接收）
        window.Bridge.on('location', function (p) {
            peers[p.from] = { lat: p.lat, lon: p.lon, alt: p.alt, name: p.name, bearing: p.bearing, time: p.time || Date.now() };
            if (selected === p.from) showDetail(p.from);
            project();
        });
        // 清理 20 秒未更新的对端，避免离线设备残留（始终运行）
        setInterval(function () {
            var now = Date.now();
            Object.keys(peers).forEach(function (k) {
                if (!peers[k].time || now - peers[k].time > 20000) delete peers[k];
            });
            project();
        }, 5000);
        window.Bridge.on('locationError', function (e) {
            hint.classList.remove('hidden');
            hint.textContent = (e && e.message) ? e.message : '定位不可用';
        });
    }

    function init() {
        listEl = document.getElementById('messageList');
        inputEl = document.getElementById('msgInput');
        sendBtn = document.getElementById('btnSend');
        statusEl = document.getElementById('peerStatus');

        sendBtn.addEventListener('click', sendText);
        inputEl.addEventListener('input', autoGrow);
        inputEl.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendText(); }
        });
        inputEl.addEventListener('focus', function () { setTimeout(scrollToBottom, 250); });

        self = window.Bridge.getSelfInfo() || self;

        window.Bridge.on('ready', function (info) {
            if (info) { self.peerId = info.peerId; self.name = info.name; }
            document.getElementById('selfName').textContent =
                '本机：' + (self.name || '未知') + '（' + (self.peerId || '-') + '）';
            addSystemTip('已加入局域网，设备名：' + (self.name || '未知'));
        });

        window.Bridge.on('message', acceptMessage);
        window.Bridge.on('msgdel', function (m) { if (m && m.target) removeBubble(m.target); });
        window.Bridge.on('msgedit', function (m) { if (m && m.target) updateBubbleText(m.target, m.text || ''); });

        // 消息被整条原地替换：音乐卡片切歌走这条，只重画那一个气泡，不重建整屏
        window.Bridge.on('msgupd', function (m) {
            if (!m || !m.id) return;
            var row = listEl.querySelector('[data-mid="' + m.id + '"]');
            if (!row) return;
            var bubble = row.querySelector('.bubble');
            if (!bubble) return;
            var olds = bubble.querySelectorAll('[data-musiccard]');
            for (var i = 0; i < olds.length; i++) {
                if (olds[i]._timer) { clearInterval(olds[i]._timer); olds[i]._timer = null; }
                // patch 帧不带列表：沿用上一帧的共享列表，别让「一起听」按钮闪没
                if (m.kind === 'musiccard' && !m.playlist && olds[i]._playlist) {
                    m.playlist = olds[i]._playlist;
                    if (m.ltIndex === undefined) m.ltIndex = olds[i]._ltIndex || 0;
                }
                // 换曲后不沿用上一首的歌词：lrcKey 变了就丢掉旧 lrc
                if (m.kind === 'musiccard' && !m.lrc && olds[i]._lrcKey
                    && m.lrcKey === undefined) m.lrcKey = olds[i]._lrcKey;
                if (m.kind === 'musiccard' && m.lrcKey && olds[i]._lrcKey
                    && m.lrcKey !== olds[i]._lrcKey) delete m.lrc;
            }
            try { probeClock(m.peerHost || m.ltHost || ''); } catch (e) {}
            bubble.innerHTML = '';
            bubble.className = 'bubble';
            buildBody(bubble, m);
        });
        window.Bridge.on('convclear', function (m) {
            if (!m || (m.conv || currentConv) !== currentConv) return;
            messages = [];
            listEl.innerHTML = '';
        });
        window.Bridge.on('peers', updatePeers);
        // 机器人：流式逐字回复 / 本地提示（指令回执等）
        window.Bridge.on('botstream', handleBotStream);
        window.Bridge.on('botnotice', function (p) {
            if (p && p.text) addSystemTip(p.text);
        });
        window.Bridge.on('error', function (e) {
            addSystemTip('网络错误：' + (e && e.message ? e.message : '未知'));
        });

        Call.init();
        window.Bridge.on('voiceResult', Voice.onResult);

        initNotifRelay();
        initControlMenu();
        initLocationShare();
        window.Bridge.on('peers', renderCallPeers);

        // 定时刷新在线设备（补足事件驱动可能的遗漏）
        setInterval(function () { updatePeers(window.Bridge.getPeers()); }, 4000);

        // 会话条：事件驱动更新为主，仅保留 30s 兜底轮询防极端情况漏刷
        var convsPollTimer = setInterval(refreshConvs, 30000);
        // 页面重新可见时主动刷新（切回前台、切 Tab 回来等）
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) refreshConvs();
        });
        // 页面获得焦点时也刷新（覆盖部分 visibilitychange 不触发的场景）
        window.addEventListener('focus', refreshConvs);

        // 虚拟列表：导出对象里的 initVirtualListForChat 依赖未接入（其 this.loadHistory 不存在），
        // 这里不做裸调用；消息渲染仍走下方 loadHistory() 的经典路径。
        // Virtual list export exists but is not wired (its this.loadHistory is undefined);
        // rendering keeps using the classic loadHistory() path below.

        // 启动后切到默认房间并载入历史
        var bootConv = (window.Bridge.getConversations() || {}).conv || 'room:mesh';
        currentConv = bootConv;
        window.Bridge.selectConv(bootConv);
        loadHistory();
        refreshConvs();
    }

    /** 把在线设备渲染成可点击拨打的列表，放进通话浮层的联系人区 */
    function renderCallPeers(peers) {
        var box = document.getElementById('callPeers');
        if (!box) return;
        box.innerHTML = '';
        if (!peers || !peers.length) {
            var tip = document.createElement('div');
            tip.className = 'call-peer-tip';
            tip.textContent = '暂无其他在线设备';
            box.appendChild(tip);
            return;
        }
        peers.forEach(function (p) {
            if (p.self) return;
            var item = document.createElement('button');
            item.className = 'call-peer-item';
            item.innerHTML = '<span class="wico">' + WI.svg('phone') + '</span> ' + escapeHtml(p.name || p.id);
            item.addEventListener('click', function () {
                Call.call({ id: p.id, name: p.name });
            });
            box.appendChild(item);
        });
    }

    return {
        init: init,
        sendFile: sendFile,
        setConv: setConv,
        conv: function () { return currentConv; },
        refreshConvs: refreshConvs,
        deleteMessage: function (id) { if (window.Bridge.deleteMessage(id)) removeBubble(id); },
        clearConv: function (conv) { window.Bridge.clearConversation(conv || currentConv); },
        voice: Voice,
        /** 在输入框插入格式前缀，已有前缀时替换掉，保留正文 */
        insertTag: function (tag) {
            var m = TAG_RE.exec(inputEl.value);
            var body = m ? (m[2] || '') : inputEl.value;
            inputEl.value = '[' + tag + ']' + body;
            inputEl.focus();
            inputEl.setSelectionRange(inputEl.value.length, inputEl.value.length);
            autoGrow();
        },
        focusInput: function () { inputEl.focus(); },
        scrollToBottom: function () { if (listEl) scrollToBottom(); },
        /**
         * 供通知栏输入框调用：直接把文字广播给局域网内其他设备，
         * 等价于在应用内输入框回车发送（无需打开本程序）。
         */
        sendTextExternal: function (text) {
            if (!text || !text.trim()) return;
            var sent = window.Bridge.send(parseContent(text));
            if (sent) acceptMessage(sent);
        },

        // ==================== 虚拟列表 / 懒加载 ====================

        // 虚拟列表配置
        VIRTUAL_LIST_CONFIG: {
            BATCH_SIZE: 50,
            BUFFER_SIZE: 20,
            LOAD_THRESHOLD_PX: 500,
            MAX_CACHED_MESSAGES: 500
        },

        // 虚拟列表状态
        _virtualListState: {
            allMessages: [],
            renderStart: 0,
            renderEnd: 0,
            isLoading: false,
            hasMore: true,
            topPlaceholder: null,
            bottomPlaceholder: null,
            scrollThrottle: null
        },

        // 初始化虚拟列表
        initVirtualList: function() {
            var self = this;
            this._virtualListState.topPlaceholder = document.createElement('div');
            this._virtualListState.topPlaceholder.className = 'virtual-placeholder virtual-top';
            this._virtualListState.topPlaceholder.style.height = '0px';

            this._virtualListState.bottomPlaceholder = document.createElement('div');
            this._virtualListState.bottomPlaceholder.className = 'virtual-placeholder virtual-bottom';
            this._virtualListState.bottomPlaceholder.style.height = '0px';

            if (listEl) {
                listEl.insertBefore(this._virtualListState.topPlaceholder, listEl.firstChild);
                listEl.appendChild(this._virtualListState.bottomPlaceholder);
            }

            listEl.addEventListener('scroll', this.onListScroll.bind(this));
            window.addEventListener('resize', this.onWindowResize.bind(this));
        },

        destroyVirtualList: function() {
            listEl.removeEventListener('scroll', this.onListScroll.bind(this));
            window.removeEventListener('resize', this.onWindowResize.bind(this));
            if (this._virtualListState.topPlaceholder) this._virtualListState.topPlaceholder.remove();
            if (this._virtualListState.bottomPlaceholder) this._virtualListState.bottomPlaceholder.remove();
            this._virtualListState = {
                allMessages: [], renderStart: 0, renderEnd: 0,
                isLoading: false, hasMore: true,
                topPlaceholder: null, bottomPlaceholder: null, scrollThrottle: null
            };
        },

        onListScroll: function() {
            if (this._virtualListState.scrollThrottle) return;
            this._virtualListState.scrollThrottle = setTimeout(function() {
                this._virtualListState.scrollThrottle = null;
                this.checkScrollPosition();
            }.bind(this), 16);
        },

        checkScrollPosition: function() {
            var scrollTop = listEl.scrollTop;
            var scrollHeight = listEl.scrollHeight;
            var clientHeight = listEl.clientHeight;
            var scrollBottom = scrollHeight - scrollTop - clientHeight;

            if (scrollBottom < 500 && this._virtualListState.hasMore && !this._virtualListState.isLoading) {
                this.loadMoreMessages();
            }
            this.updateRenderRange();
        },

        updateRenderRange: function() {
            var scrollTop = listEl.scrollTop;
            var itemHeight = this.estimateItemHeight();
            var visibleStart = Math.floor(listEl.scrollTop / itemHeight);
            var visibleEnd = Math.min(
                this._virtualListState.allMessages.length,
                visibleStart + Math.ceil(listEl.clientHeight / this.estimateItemHeight()) + 20
            );

            var newStart = Math.max(0, visibleStart - 20);
            var newEnd = Math.min(this._virtualListState.allMessages.length, visibleEnd);

            if (newStart !== this._virtualListState.renderStart || newEnd !== this._virtualListState.renderEnd) {
                this._virtualListState.renderStart = newStart;
                this._virtualListState.renderEnd = newEnd;
                this.updatePlaceholderHeights();
                this.renderVisibleRange();
            }
        },

        estimatedItemHeight: 100,
        estimateItemHeight: function() {
            var items = listEl.querySelectorAll('.msg-row');
            if (items.length > 0) {
                var totalHeight = 0, count = 0;
                for (var i = 0; i < Math.min(items.length, 20); i++) {
                    totalHeight += items[i].offsetHeight;
                    count++;
                }
                this.estimatedItemHeight = totalHeight / count;
            }
            return this.estimatedItemHeight;
        },

        estimateItemHeight: function() { return this.estimatedItemHeight; },

        updatePlaceholderHeights: function() {
            var itemHeight = this.estimateItemHeight();
            this._virtualListState.topPlaceholder.style.height = (this._virtualListState.renderStart * this.estimateItemHeight()) + 'px';
            this._virtualListState.bottomPlaceholder.style.height = 
                ((this._virtualListState.allMessages.length - this._virtualListState.renderEnd) * this.estimateItemHeight()) + 'px';
        },

        renderVisibleRange: function() {
            var messages = this._virtualListState.allMessages;
            var fragment = document.createDocumentFragment();

            var renderedItems = listEl.querySelectorAll('.msg-row');
            for (var i = 0; i < renderedItems.length; i++) {
                var item = renderedItems[i];
                var index = parseInt(item.dataset.virtualIndex);
                if (index < this._virtualListState.renderStart || index >= this._virtualListState.renderEnd) {
                    item.remove();
                }
            }

            var fragment = document.createDocumentFragment();
            for (var i = this._virtualListState.renderStart; i < this._virtualListState.renderEnd; i++) {
                var msg = this._virtualListState.allMessages[i];
                if (!msg) continue;
                var existing = listEl.querySelector('[data-virtual-index="' + i + '"]');
                if (existing) continue;
                var row = this.createMessageElement(msg, i);
                fragment.appendChild(row);
            }

            if (fragment.children.length > 0) {
                listEl.insertBefore(fragment, this._virtualListState.bottomPlaceholder);
            }
        },

        createMessageElement: function(msg, virtualIndex) {
            var isOut = msg.from === self.peerId;
            var row = document.createElement('div');
            row.className = 'msg-row ' + (isOut ? 'out' : 'in');
            if (msg.id) row.setAttribute('data-mid', msg.id);
            row.setAttribute('data-virtual-index', virtualIndex);

            var avatar = document.createElement('div');
            avatar.className = 'avatar';
            avatar.textContent = (msg.fromName || '?').charAt(0).toUpperCase();

            var main = document.createElement('div');
            main.className = 'msg-main' + (msg.kind === 'music' ? ' wide' : '');

            if (!isOut) {
                var sender = document.createElement('div');
                sender.className = 'msg-sender';
                sender.textContent = msg.fromName || '未知设备';
                main.appendChild(sender);
            }

            var bubble = document.createElement('div');
            bubble.className = 'bubble';
            this.buildBody(bubble, msg);
            this.bindLongPress(bubble, function() { this.showMsgMenu(msg); }.bind(this));
            main.appendChild(bubble);

            var row = document.createElement('div');
            row.appendChild(avatar);
            row.appendChild(main);
            return row;
        },

        addMessageToVirtualList: function(msg) {
            this._virtualListState.allMessages.push(msg);
            this._virtualListState.hasMore = true;

            var newIndex = this._virtualListState.allMessages.length - 1;
            if (newIndex >= this._virtualListState.renderStart && newIndex < this._virtualListState.renderEnd) {
                var row = this.createMessageElement(msg, newIndex);
                listEl.insertBefore(row, this._virtualListState.bottomPlaceholder);
            } else if (newIndex >= this._virtualListState.renderEnd - 20) {
                this._virtualListState.renderEnd = Math.min(this._virtualListState.allMessages.length, this._virtualListState.renderEnd + 50);
                this.updatePlaceholderHeights();
                this.renderVisibleRange();
            }
            this.updatePlaceholderHeights();
        },

        loadMoreMessages: function() {
            if (this._virtualListState.isLoading || !this._virtualListState.hasMore) return;
            this._virtualListState.isLoading = true;
            this._virtualListState.bottomPlaceholder.textContent = '加载更多...';

            var beforeId = this._virtualListState.allMessages[this._virtualListState.allMessages.length - 1]?.id;
            var list = window.Bridge.getMessages(currentConv, 50, beforeId) || [];

            var newMessages = list.filter(function(m) {
                return m && !this._virtualListState.allMessages.some(function(x) { return x.id === m.id; });
            }, this);

            if (newMessages.length > 0) {
                this._virtualListState.allMessages = newMessages.concat(this._virtualListState.allMessages);
                this._virtualListState.renderStart += newMessages.length;
                this._virtualListState.renderEnd += newMessages.length;
                this.updatePlaceholderHeights();
                this.renderVisibleRange();
            }
            this._virtualListState.hasMore = newMessages.length > 0;
            this._virtualListState.isLoading = false;
            this._virtualListState.bottomPlaceholder.textContent = '';
            this.updatePlaceholderHeights();
        },

        onWindowResize: function() {
            this.updatePlaceholderHeights();
            this.updateRenderRange();
        },

        initVirtualListForChat: function() {
            this.initVirtualList();
            this.loadHistory();
            listEl.addEventListener('scroll', this.onListScroll.bind(this));
            window.addEventListener('resize', this.onWindowResize.bind(this));
        },

        destroyVirtualListForChat: function() {
            this.destroyVirtualList();
            this._virtualListState = {
                allMessages: [], renderStart: 0, renderEnd: 0,
                isLoading: false, hasMore: true,
                topPlaceholder: null, bottomPlaceholder: null, scrollThrottle: null
            };
        }
    };
})();

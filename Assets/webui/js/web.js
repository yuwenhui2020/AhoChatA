/**
 * 网页端聊天客户端（浏览器侧，无原生 Bridge）。
 *
 * 与宿主 app 的交互走内置 HTTP 服务（WebServer.cs，监听 6789 端口）：
 * 局域网浏览器访问  http://手机IP:6789/  即可打开本页面。
 *   GET  /api/session            领取访客身份（服务端按浏览器 IP 生成「网页端+IP」）
 *   GET  /api/messages?since=N   长轮询拉取消息（since=0 时回放历史）
 *   POST /api/send               发送消息
 *   GET  /api/peers              在线设备列表
 *
 * 渲染风格与 app 内 Assets/www 保持一致：时间戳独立成行居中显示在气泡上方，
 * 图片/视频/音乐/链接/通知均为卡片，点击进内置播放器。
 * 网页端不含侧栏，只有聊天界面。
 */
(function () {
    'use strict';

    var TAG_RE = /^\s*\[(url|music|video|pic|notice)\]\s*([\s\S]*)$/i;

    var els = {};
    var self = { guestId: '', name: '', ip: '' };
    var editingId = null;   // 正在编辑的消息 id（非空 = 编辑态）
    var since = 0;
    var polling = false;
    var renderedIds = Object.create(null);
    // 上一条插入时间分隔行的时间戳：与原生 App 一样，间隔超过 5 分钟才显示时间
    var lastTimeMark = 0;
    var TIME_MARK_GAP = 5 * 60 * 1000;

    // 当前房间（会话）：「房间号」即频道键，默认 mesh，与 App 端的房间逻辑一致。
    var conv = '';
    var convList = [];

    /* ==================== 基础工具 ==================== */

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function fileNameOf(url) {
        if (!url) return '';
        try {
            var clean = url.split('?')[0].split('#')[0];
            var parts = clean.split('/');
            return decodeURIComponent(parts[parts.length - 1] || '');
        } catch (e) { return ''; }
    }

    function hostOf(url) {
        try { return new URL(url, location.href).hostname; } catch (e) { return ''; }
    }

    function formatClock(ts) {
        var d = new Date(ts || Date.now());
        var h = d.getHours(), m = d.getMinutes();
        return (h < 10 ? '0' : '') + h + ':' + (m < 10 ? '0' : '') + m;
    }

    // 与 ChatActivity.Initial / AvatarColor 保持同一套规则：
    // 取第一个字母、数字或汉字；头像颜色按 UTF-16 码元哈希到 8 色调色板。
    function initialOf(s) {
        var str = String(s || '');
        for (var i = 0; i < str.length; i++) {
            var ch = str.charAt(i), code = str.charCodeAt(i);
            if (/[0-9A-Za-z]/.test(ch) || code > 0x2E80) {
                return ch.toUpperCase();
            }
        }
        return '?';
    }

    function avatarColor(s) {
        var palette = [
            '#4C9AFF', '#34C759', '#FF9F0A', '#9B59F6',
            '#FF3B30', '#32ADE6', '#AF52DE', '#30B0C7'
        ];
        var str = String(s || '');
        if (!str) return palette[0];
        var h = 0;
        for (var i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) & 0x7FFFFFFF;
        return palette[h % palette.length];
    }

    function formatTime(sec) {
        if (!isFinite(sec) || sec < 0) sec = 0;
        var m = Math.floor(sec / 60), s = Math.floor(sec % 60);
        return (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    }

    /** 消息里的媒体地址是 /__media/xxx，宿主 HTTP 服务同样提供该路径，直接可用 */
    function resourceOf(msg) {
        return msg.mediaUrl || msg.url || '';
    }

    /**
     * 把所有「外部绝对地址」（与网页端不同源的 http(s)）改写为 app 代理 /api/media/remote?url=，
     * 让 CDN 只看到本机 app，看不到浏览器客户端；同域（/__media、/api/media 等）原样返回。
     */
    function appProxy(u) {
        if (!u) return u;
        try {
            if (/^https?:\/\//i.test(u)) {
                var o = new URL(u, location.href);
                if (o.origin !== location.origin) return '/api/media/remote?url=' + encodeURIComponent(u);
            }
        } catch (e) {}
        return u;
    }

    /* ============ 音频元数据解析（ID3v2 / FLAC，纯前端无依赖） ============ */

    // 元数据都在文件头部，只取前若干字节，避免为读封面下载整首歌
    var META_HEAD_BYTES = 3 * 1024 * 1024;

    function fetchBuffer(url, maxBytes) {
        var opts = maxBytes ? { headers: { Range: 'bytes=0-' + (maxBytes - 1) } } : undefined;
        return fetch(url, opts).then(function (r) {
            if (!r.ok && r.status !== 206) throw new Error('加载失败 ' + r.status);
            return r.arrayBuffer();
        });
    }

    function decodeText(bytes, encoding) {
        // ID3 文本编码：0=ISO-8859-1, 1=UTF-16(带BOM), 2=UTF-16BE, 3=UTF-8
        try {
            if (encoding === 1) return new TextDecoder('utf-16').decode(bytes);
            if (encoding === 2) return new TextDecoder('utf-16be').decode(bytes);
            if (encoding === 3) return new TextDecoder('utf-8').decode(bytes);
            return new TextDecoder('iso-8859-1').decode(bytes);
        } catch (e) {
            return new TextDecoder('utf-8').decode(bytes);
        }
    }

    function stripNull(s) {
        return (s || '').replace(/\u0000+$/g, '').replace(/^\u0000+/, '').trim();
    }

    function bytesToBase64(bytes) {
        var CHUNK = 0x8000, out = '';
        for (var i = 0; i < bytes.length; i += CHUNK) {
            out += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
        }
        return btoa(out);
    }

    function parseId3(buf) {
        var view = new DataView(buf);
        var u8 = new Uint8Array(buf);
        if (u8.length < 10) return null;
        if (u8[0] !== 0x49 || u8[1] !== 0x44 || u8[2] !== 0x33) return null;  // 'ID3'

        var major = u8[3];
        var flags = u8[5];
        var size = ((u8[6] & 0x7f) << 21) | ((u8[7] & 0x7f) << 14) | ((u8[8] & 0x7f) << 7) | (u8[9] & 0x7f);

        var offset = 10;
        if (flags & 0x40) {   // 扩展头
            if (offset + 4 <= u8.length) {
                var extSize = major >= 4
                    ? (((u8[offset] & 0x7f) << 21) | ((u8[offset + 1] & 0x7f) << 14) | ((u8[offset + 2] & 0x7f) << 7) | (u8[offset + 3] & 0x7f))
                    : view.getUint32(offset);
                offset += extSize + (major >= 4 ? 0 : 4);
            }
        }

        var end = Math.min(10 + size, u8.length);
        var meta = { title: '', artist: '', album: '', lyrics: '', cover: null };
        var frameHeaderSize = major === 2 ? 6 : 10;

        while (offset + frameHeaderSize <= end) {
            var id, frameSize;
            if (major === 2) {
                id = String.fromCharCode(u8[offset], u8[offset + 1], u8[offset + 2]);
                frameSize = (u8[offset + 3] << 16) | (u8[offset + 4] << 8) | u8[offset + 5];
            } else {
                id = String.fromCharCode(u8[offset], u8[offset + 1], u8[offset + 2], u8[offset + 3]);
                if (major === 4) {
                    frameSize = ((u8[offset + 4] & 0x7f) << 21) | ((u8[offset + 5] & 0x7f) << 14) |
                                ((u8[offset + 6] & 0x7f) << 7) | (u8[offset + 7] & 0x7f);
                } else {
                    frameSize = view.getUint32(offset + 4);
                }
            }

            if (!/^[A-Z0-9]{3,4}$/.test(id) || frameSize <= 0) break;
            var dataStart = offset + frameHeaderSize;
            var dataEnd = Math.min(dataStart + frameSize, end);
            if (dataEnd <= dataStart) break;
            var data = u8.subarray(dataStart, dataEnd);

            try {
                if (id === 'TIT2' || id === 'TT2') meta.title = stripNull(decodeText(data.subarray(1), data[0]));
                else if (id === 'TPE1' || id === 'TP1') meta.artist = stripNull(decodeText(data.subarray(1), data[0]));
                else if (id === 'TALB' || id === 'TAL') meta.album = stripNull(decodeText(data.subarray(1), data[0]));
                else if (id === 'USLT' || id === 'ULT') meta.lyrics = parseUslt(data);
                else if (id === 'SYLT' || id === 'SLT') {
                    // 同步歌词帧：优先级高于 USLT，因为自带精确时间戳
                    var sy = parseSylt(data);
                    if (sy) meta.lyrics = sy;
                }
                else if (id === 'APIC' || id === 'PIC') {
                    var pic = parseApic(data, id === 'PIC');
                    if (pic && !meta.cover) meta.cover = pic;
                }
            } catch (e) { /* 单帧异常不影响其他帧 */ }

            offset = dataStart + frameSize;
        }
        return meta;
    }

    // USLT: encoding(1) + language(3) + descriptor(null结尾) + lyrics
    function parseUslt(data) {
        var enc = data[0];
        var p = 4;
        var nullLen = (enc === 1 || enc === 2) ? 2 : 1;
        while (p < data.length) {
            if (nullLen === 1) {
                if (data[p] === 0) { p += 1; break; }
                p += 1;
            } else {
                if (data[p] === 0 && data[p + 1] === 0) { p += 2; break; }
                p += 2;
            }
        }
        return stripNull(decodeText(data.subarray(p), enc));
    }

    /**
     * SYLT 同步歌词: encoding(1)+language(3)+timeFormat(1)+contentType(1)
     *                +descriptor(null结尾) + N×[文本(null结尾) + 时间戳(4字节)]
     * 统一转换成 LRC 文本，交给 parseLyrics 走同一条渲染路径。
     */
    function parseSylt(data) {
        var enc = data[0];
        var timeFormat = data[4];
        if (timeFormat !== 2) return '';        // 2 = 毫秒；1 = MPEG 帧号，无法换算
        var view = new DataView(data.buffer, data.byteOffset, data.byteLength);
        var nullLen = (enc === 1 || enc === 2) ? 2 : 1;

        var p = 6;
        // 跳过 descriptor
        while (p < data.length) {
            if (nullLen === 1) {
                if (data[p] === 0) { p += 1; break; }
                p += 1;
            } else {
                if (data[p] === 0 && data[p + 1] === 0) { p += 2; break; }
                p += 2;
            }
        }

        var out = '';
        while (p + 4 < data.length) {
            var start = p;
            while (p < data.length) {
                if (nullLen === 1) {
                    if (data[p] === 0) break;
                    p += 1;
                } else {
                    if (data[p] === 0 && data[p + 1] === 0) break;
                    p += 2;
                }
            }
            var text = stripNull(decodeText(data.subarray(start, p), enc));
            p += nullLen;
            if (p + 4 > data.length) break;
            var ms = view.getUint32(p); p += 4;

            if (text) {
                var total = ms / 1000;
                var mm = Math.floor(total / 60);
                var ss = total - mm * 60;
                out += '[' + (mm < 10 ? '0' : '') + mm + ':' +
                       (ss < 10 ? '0' : '') + ss.toFixed(2) + ']' + text + '\n';
            }
        }
        return out;
    }

    // APIC: encoding(1) + mime(null结尾) + picType(1) + desc(null结尾) + 图片数据
    function parseApic(data, isV2) {
        var enc = data[0];
        var p = 1;
        var mime;
        if (isV2) {
            mime = 'image/' + String.fromCharCode(data[1], data[2], data[3]).toLowerCase();
            p = 4;
        } else {
            var mimeStart = p;
            while (p < data.length && data[p] !== 0) p++;
            mime = decodeText(data.subarray(mimeStart, p), 0) || 'image/jpeg';
            p++;
        }
        p++;   // picture type

        var nullLen = (enc === 1 || enc === 2) ? 2 : 1;
        while (p < data.length) {
            if (nullLen === 1) {
                if (data[p] === 0) { p += 1; break; }
                p += 1;
            } else {
                if (data[p] === 0 && data[p + 1] === 0) { p += 2; break; }
                p += 2;
            }
        }
        if (p >= data.length) return null;
        return 'data:' + mime + ';base64,' + bytesToBase64(data.subarray(p));
    }

    function parseFlac(buf) {
        var u8 = new Uint8Array(buf);
        if (u8.length < 4) return null;
        if (String.fromCharCode(u8[0], u8[1], u8[2], u8[3]) !== 'fLaC') return null;

        var meta = { title: '', artist: '', album: '', lyrics: '', cover: null };
        var view = new DataView(buf);
        var p = 4;

        while (p + 4 <= u8.length) {
            var last = (u8[p] & 0x80) !== 0;
            var type = u8[p] & 0x7f;
            var len = (u8[p + 1] << 16) | (u8[p + 2] << 8) | u8[p + 3];
            var start = p + 4;
            if (start + len > u8.length) break;

            if (type === 4) parseVorbisComment(u8.subarray(start, start + len), meta);
            else if (type === 6 && !meta.cover) parseFlacPicture(view, u8, start, len, meta);

            p = start + len;
            if (last) break;
        }
        return meta;
    }

    function parseVorbisComment(block, meta) {
        var dv = new DataView(block.buffer, block.byteOffset, block.byteLength);
        var p = 0;
        var vendorLen = dv.getUint32(p, true); p += 4 + vendorLen;
        var count = dv.getUint32(p, true); p += 4;
        for (var i = 0; i < count && p + 4 <= block.length; i++) {
            var len = dv.getUint32(p, true); p += 4;
            var text = new TextDecoder('utf-8').decode(block.subarray(p, p + len));
            p += len;
            var eq = text.indexOf('=');
            if (eq < 0) continue;
            var key = text.substring(0, eq).toUpperCase();
            var val = text.substring(eq + 1);
            if (key === 'TITLE') meta.title = val;
            else if (key === 'ARTIST') meta.artist = val;
            else if (key === 'ALBUM') meta.album = val;
            else if (key === 'LYRICS' || key === 'UNSYNCEDLYRICS') meta.lyrics = val;
        }
    }

    function parseFlacPicture(view, u8, start, len, meta) {
        var p = start + 4;                      // 跳过 picture type
        var mimeLen = view.getUint32(p); p += 4;
        var mime = new TextDecoder('ascii').decode(u8.subarray(p, p + mimeLen)); p += mimeLen;
        var descLen = view.getUint32(p); p += 4 + descLen;
        p += 16;                                // 宽/高/色深/索引色数
        var dataLen = view.getUint32(p); p += 4;
        if (p + dataLen > u8.length) return;
        meta.cover = 'data:' + (mime || 'image/jpeg') + ';base64,' + bytesToBase64(u8.subarray(p, p + dataLen));
    }

    var metaCache = {};

    /** 读取音频元数据（含封面与内嵌歌词），带缓存；失败返回空元数据不抛异常 */
    function readAudioMeta(url, fallbackName) {
        if (metaCache[url]) return Promise.resolve(metaCache[url]);

        return fetchBuffer(url, META_HEAD_BYTES).then(function (buf) {
            var meta = null;
            try { meta = parseId3(buf) || parseFlac(buf); } catch (e) { meta = null; }
            meta = meta || { title: '', artist: '', album: '', lyrics: '', cover: null };
            if (!meta.title) meta.title = fallbackName || '未知歌曲';
            if (!meta.artist) meta.artist = '未知艺术家';
            metaCache[url] = meta;
            return meta;
        }).catch(function () {
            var meta = { title: fallbackName || '未知歌曲', artist: '未知艺术家', album: '', lyrics: '', cover: null };
            metaCache[url] = meta;
            return meta;
        });
    }

    /** 解析 LRC 为 [{time, text}]；无时间戳的行 time=-1（按纯文本展示，不做滚动） */
    function parseLyrics(raw) {
        if (!raw) return [];
        var lines = raw.split(/\r?\n/);
        var out = [];
        var re = /\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g;

        for (var i = 0; i < lines.length; i++) {
            var line = lines[i];
            var matches = [];
            var m;
            re.lastIndex = 0;
            while ((m = re.exec(line)) !== null) matches.push(m);

            var text = line.replace(re, '').trim();
            if (!matches.length) {
                if (text) out.push({ time: -1, text: text });
                continue;
            }
            if (!text) continue;
            // 一行可带多个时间戳（如 [00:12.00][01:20.00]副歌）
            for (var j = 0; j < matches.length; j++) {
                var mm = matches[j];
                var frac = mm[3] ? parseInt(mm[3], 10) / Math.pow(10, mm[3].length) : 0;
                out.push({ time: parseInt(mm[1], 10) * 60 + parseInt(mm[2], 10) + frac, text: text });
            }
        }
        out.sort(function (a, b) { return a.time - b.time; });
        return out;
    }

    function parseContent(raw) {
        var m = TAG_RE.exec(raw);
        if (!m) return { kind: 'text', text: raw };
        var kind = m[1].toLowerCase();
        var body = (m[2] || '').trim();
        if (kind === 'notice') return { kind: 'notice', text: body };
        return { kind: kind, url: body, text: body };
    }

    function scrollToBottom() {
        if (els.list) els.list.scrollTop = els.list.scrollHeight;
    }

    /* ==================== 渲染 ==================== */

    /* ---------- 轻量对话框：房间号输入 / 删除确认 ---------- */

    function dialog(opts) {
        var mask = document.createElement('div');
        mask.className = 'lc-dialog-mask';

        var box = document.createElement('div');
        box.className = 'lc-dialog';

        var title = document.createElement('div');
        title.className = 'lc-dialog-title';
        title.textContent = opts.title || '';
        box.appendChild(title);

        var body = document.createElement('div');
        body.className = 'lc-dialog-body';
        if (opts.text) body.textContent = opts.text;

        var input = null;
        if (opts.input) {
            input = document.createElement('input');
            input.type = 'text';
            input.placeholder = opts.placeholder || '';
            input.value = opts.value || '';
            body.appendChild(input);
        }
        box.appendChild(body);

        var actions = document.createElement('div');
        actions.className = 'lc-dialog-actions';

        var cancel = document.createElement('button');
        cancel.textContent = opts.cancelText || '取消';
        cancel.addEventListener('click', function () {
            document.body.removeChild(mask);
            if (opts.onCancel) opts.onCancel();
        });
        actions.appendChild(cancel);

        var ok = document.createElement('button');
        ok.className = opts.danger ? 'danger' : 'primary';
        ok.textContent = opts.okText || '确定';
        ok.addEventListener('click', function () {
            var value = input ? input.value : true;
            document.body.removeChild(mask);
            if (opts.onOk) opts.onOk(value);
        });
        actions.appendChild(ok);

        box.appendChild(actions);
        mask.appendChild(box);
        document.body.appendChild(mask);
        if (input) setTimeout(function () { input.focus(); }, 30);
    }

    /* ---------- 房间条 ---------- */

    function loadConvs() {
        return api('/api/conv').then(function (res) {
            convList = (res && res.items) || [];
            if (!conv && res && res.conv) conv = res.conv;
            if (!conv) conv = 'room:mesh';
            renderConvs();
        }).catch(function () {});
    }

    function renderConvs() {
        var box = els.convChips;
        if (!box) return;

        // 内容没变就不重绘，避免 5 秒轮询把会话条的滚动位置复位
        var sig = conv + '|' + convList.map(function (c) {
            return c.id + ':' + c.title + ':' + (c.unread || 0);
        }).join(',');
        if (box.getAttribute('data-sig') === sig) return;
        box.setAttribute('data-sig', sig);

        box.innerHTML = '';

        convList.forEach(function (c) {
            var chip = document.createElement('div');
            chip.className = 'conv-chip' + (c.id === conv ? ' active' : '');
            chip.innerHTML = c.kind === 'dm' ? ('<span class="wico">' + WI.svg('chat') + '</span> ' + esc(c.title)) : esc(c.title);
            if (c.unread > 0) {
                var badge = document.createElement('span');
                badge.className = 'badge';
                badge.textContent = c.unread > 99 ? '99+' : c.unread;
                chip.appendChild(badge);
            }
            chip.addEventListener('click', function () { switchConv(c.id); });
            chip.addEventListener('contextmenu', function (e) {
                e.preventDefault();
                convMenu(c);
            });
            // 长按：房间/私聊的操作菜单（清空该会话）
            var timer = null;
            chip.addEventListener('touchstart', function () {
                timer = setTimeout(function () { convMenu(c); }, 550);
            }, { passive: true });
            chip.addEventListener('touchend', function () { clearTimeout(timer); });
            chip.addEventListener('touchmove', function () { clearTimeout(timer); });
            box.appendChild(chip);
        });

        var add = document.createElement('div');
        add.className = 'conv-chip add';
        add.textContent = '＋ 房间';
        add.addEventListener('click', function () {
            dialog({
                title: '加入房间',
                text: '输入房间号即可进入同一个频道（任意字母/数字/下划线），例如 mesh、501。',
                input: true,
                placeholder: '房间号',
                okText: '进入',
                onOk: function (code) {
                    code = (code || '').trim();
                    if (!code) return;
                    api('/api/conv/join?code=' + encodeURIComponent(code), { method: 'POST' })
                        .then(function (res) {
                            if (res && res.conv) {
                                conv = res.conv;
                                resetAndReload();
                            }
                        });
                }
            });
        });
        box.appendChild(add);
    }

    function convMenu(c) {
        dialog({
            title: c.title,
            text: '清空该会话的全部消息？在线设备会同步删除。',
            okText: '清空',
            danger: true,
            onOk: function () {
                api('/api/clear?conv=' + encodeURIComponent(c.id), { method: 'POST' })
                    .then(function () {
                        if (c.id === conv) resetAndReload();
                        loadConvs();
                    });
            }
        });
    }

    function switchConv(id) {
        if (!id || id === conv) return;
        conv = id;
        resetAndReload();
    }

    function resetAndReload() {
        since = 0;
        renderedIds = Object.create(null);
        lastTimeMark = 0;
        if (els.list) els.list.innerHTML = '';
        renderConvs();
        loadConvs();
    }

    /* ---------- 身份与指纹（qaul / bitchat 两套格式，可更换） ---------- */

    function identityBody(id) {
        if (!id) return '身份尚未就绪';
        return 'AhoChat 设备 id\n' + id.MchatId + '（第 ' + id.generation + ' 代）\n\n' +
               'qaul PeerId（Ed25519 / libp2p）\n' + id.qaul.peerId + '\n\n' +
               'qaul 公钥指纹\n' + id.qaul.fingerprint + '\n\n' +
               'bitchat peerID\n' + id.bitchat.peerId + '\n\n' +
               'bitchat 身份指纹\n' + id.bitchat.fingerprintGrouped;
    }

    function openIdentityDialog() {
        api('/api/identity').then(function (id) {
            var mask = document.createElement('div');
            mask.className = 'lc-dialog-mask';

            var box = document.createElement('div');
            box.className = 'lc-dialog';

            var title = document.createElement('div');
            title.className = 'lc-dialog-title';
            title.textContent = '身份与指纹';
            box.appendChild(title);

            var body = document.createElement('div');
            body.className = 'lc-dialog-body';
            var pre = document.createElement('div');
            pre.style.cssText = 'white-space:pre-wrap;word-break:break-all;font-size:12px;line-height:1.55;max-height:46vh;overflow:auto;';
            pre.textContent = identityBody(id);
            body.appendChild(pre);

            var tip = document.createElement('div');
            tip.style.cssText = 'margin-top:10px;color:#a06a00;font-size:11px;';
            tip.textContent = '换指纹后旧身份立即作废并重新广播，对端会把你当成新设备；聊天记录仍保留。';
            body.appendChild(tip);
            box.appendChild(body);

            var actions = document.createElement('div');
            actions.className = 'lc-dialog-actions';

            function close() { if (mask.parentNode) document.body.removeChild(mask); }

            var cancel = document.createElement('button');
            cancel.textContent = '关闭';
            cancel.addEventListener('click', close);
            actions.appendChild(cancel);

            var rotate = document.createElement('button');
            rotate.className = 'danger';
            rotate.textContent = '更换全部指纹';
            rotate.addEventListener('click', function () {
                if (!window.confirm('更换后旧身份立即作废，对端会看到一台新设备。确认更换？')) return;
                api('/api/identity/rotate?scope=all', { method: 'POST' })
                    .then(function () {
                        close();
                        toast('指纹已更换');
                        loadConvs();
                    });
            });
            actions.appendChild(rotate);

            box.appendChild(actions);
            mask.appendChild(box);
            document.body.appendChild(mask);
        }).catch(function () { toast('读取身份失败'); });
    }

    /* ---------- qaul 网络节点（libp2p 邻居） ---------- */

    function refreshQaul() {
        var btn = document.getElementById('btnQaul');
        if (!btn) return;
        api('/api/qaul').then(function (st) {
            if (!st) return;
            btn.textContent = 'qaul ' + (st.running ? (st.neighbours || 0) : '—');
            btn.title = 'qaul 节点：' + (st.running ? '运行中' : '未运行') +
                        ' · 端口 ' + (st.port || 0) +
                        ' · 邻居 ' + (st.neighbours || 0) +
                        ' · 互认 ' + (st.infoRx || 0);
        }).catch(function () { });
    }

    function openQaulDialog() {
        api('/api/qaul').then(function (st) {
            var mask = document.createElement('div');
            mask.className = 'lc-dialog-mask';
            var box = document.createElement('div');
            box.className = 'lc-dialog';

            var title = document.createElement('div');
            title.className = 'lc-dialog-title';
            title.textContent = 'qaul 节点';
            box.appendChild(title);

            var body = document.createElement('div');
            body.className = 'lc-dialog-body';
            var pre = document.createElement('div');
            pre.style.cssText = 'white-space:pre-wrap;word-break:break-all;font-size:12px;line-height:1.55;max-height:46vh;overflow:auto;';

            var lines = [];
            lines.push('libp2p 节点：' + (st.running ? '运行中' : '未运行'));
            lines.push('监听端口：' + (st.port || 0));
            lines.push('本机 PeerId：');
            lines.push(st.peerId || '-');
            lines.push('');
            lines.push('邻居：' + (st.neighbours || 0) + '    互认路由表：' + (st.infoRx || 0));
            lines.push('');
            (st.items || []).forEach(function (it) {
                lines.push('• ' + it.peer + (it.addr ? '  ' + it.addr : ''));
            });
            if (!st.items || st.items.length === 0) lines.push('（暂无邻居）');

            pre.textContent = lines.join('\n');
            body.appendChild(pre);
            box.appendChild(body);

            var actions = document.createElement('div');
            actions.className = 'lc-dialog-actions';
            function close() { if (mask.parentNode) document.body.removeChild(mask); }
            var ok = document.createElement('button');
            ok.textContent = '关闭';
            ok.addEventListener('click', close);
            actions.appendChild(ok);
            box.appendChild(actions);

            mask.appendChild(box);
            document.body.appendChild(mask);
        }).catch(function () { toast('读取 qaul 状态失败'); });
    }

    function addSystemTip(text) {
        var wrap = document.createElement('div');
        wrap.className = 'sys-tip';
        var span = document.createElement('span');
        span.textContent = text;
        wrap.appendChild(span);
        els.list.appendChild(wrap);
        scrollToBottom();
    }

    function renderMessage(msg) {
        // 控制帧（指令/删除/清空）不渲染成气泡：
        //   del  → 从界面上移除对应消息
        //   clear→ 清空当前会话的已渲染内容
        if (msg && (msg.kind === 'control' || msg.op)) {
            handleControl(msg);
            return;
        }

        if (msg && msg.id) renderedIds[msg.id] = true;

        var isOut = msg.from === self.guestId;

        // 间隔超过 5 分钟才插入居中时间行，和原生 App 的 AppendMessage 一致
        var ts = Number(msg.time) || Date.now();
        if (ts - lastTimeMark > TIME_MARK_GAP) {
            var timeRow = document.createElement('div');
            timeRow.className = 'msg-time-row';
            timeRow.textContent = formatClock(ts);
            els.list.appendChild(timeRow);
            lastTimeMark = ts;
        }

        var row = document.createElement('div');
        row.className = 'msg-row ' + (isOut ? 'out' : 'in');
        if (msg.id) row.setAttribute('data-mid', msg.id);

        var avatar = document.createElement('div');
        avatar.className = 'avatar';
        var who = msg.fromName || (isOut ? self.name : '') || '?';
        avatar.textContent = initialOf(who);
        avatar.style.background = avatarColor(who);
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

        // 长按气泡 → 消息菜单（撤回/编辑/复制/删除）
        bindLongPress(bubble, function () { showMsgMenu(msg); });

        main.appendChild(bubble);

        row.appendChild(main);
        els.list.appendChild(row);
        scrollToBottom();
    }

    function handleControl(msg) {
        var op = msg.op;
        if (op === 'patch' && msg.target) {
            patchBubble(msg);
        } else if ((op === 'del' || op === 'recall') && msg.target) {
            removeBubble(msg.target);
        } else if (op === 'edit' && msg.target) {
            updateBubble(msg.target, msg.text || '');
        } else if (op === 'clear') {
            if (els.list) els.list.innerHTML = '';
        }
    }

    /** 整条原地替换（音乐卡片切歌 / 进度校正走这条，不新增气泡）。 */
    function patchBubble(msg) {
        var nodes = document.querySelectorAll('[data-mid]');
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].getAttribute('data-mid') !== msg.target) continue;
            var bubble = nodes[i].querySelector('.bubble');
            if (!bubble) return;
            var body = {};
            for (var k in msg) if (Object.prototype.hasOwnProperty.call(msg, k)) body[k] = msg[k];
            body.id = msg.target;
            body.kind = msg.patchKind || msg.kind || 'text';
            delete body.op;
            delete body.target;
            delete body.patchKind;

            var old = bubble.querySelector('[data-musiccard]');
            if (old) {
                if (old._timer) { clearInterval(old._timer); old._timer = null; }
                // patch 不带大字段时沿用旧卡片，避免封面/共享列表被抹掉
                if (!body.playlist && old._playlist) {
                    body.playlist = old._playlist;
                    if (body.ltIndex === undefined) body.ltIndex = old._ltIndex || 0;
                }
                if (!body.cover && old._coverB64) body.cover = old._coverB64;
                // 歌词只在「归属同一首歌」时沿用，否则切歌后会显示上一首的词
                if (!body.lrc && old._lrc && (!body.lrcKey || body.lrcKey === old._lrcKey))
                    body.lrc = old._lrc;
                if (!body.lrcKey && old._lrcKey) body.lrcKey = old._lrcKey;
            }
            try { probeClock(body.peerHost || body.ltHost || ''); } catch (e) {}
            bubble.innerHTML = '';
            bubble.className = 'bubble';
            buildBody(bubble, body);
            // 同步听：切歌 / 进度 / 播放态都随 patch 帧落到网页播放器上
            try { if (Player.syncFromCard) Player.syncFromCard(body); } catch (e) {}
            return;
        }
    }

    /** 长按菜单：自己的消息可撤回/编辑，所有消息可复制/删除。 */
    function showMsgMenu(msg) {
        var isOut = msg.from === self.guestId;
        var isText = !msg.kind || msg.kind === 'text';
        var mask = document.createElement('div');
        mask.className = 'lc-menu-mask';
        var box = document.createElement('div');
        box.className = 'lc-menu-box';
        function addItem(label, fn) {
            var b = document.createElement('button');
            b.className = 'lc-menu-item';
            b.textContent = label;
            b.addEventListener('click', function () { mask.parentNode && mask.parentNode.removeChild(mask); fn(); });
            box.appendChild(b);
        }
        addItem('复制文本', function () {
            try { navigator.clipboard && navigator.clipboard.writeText(msg.text || ''); } catch (e) {}
            toast('已复制');
        });
        if (isOut) addItem('撤回', function () { askDelete(msg); });
        if (isOut && isText) addItem('编辑', function () { beginEdit(msg); });
        addItem('删除(仅本机)', function () {
            api('/api/delete', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ id: msg.id })
            }).then(function () { removeBubble(msg.id); }).catch(function () {});
        });
        var cancel = document.createElement('button');
        cancel.className = 'lc-menu-item cancel';
        cancel.textContent = '取消';
        cancel.addEventListener('click', function () { mask.parentNode && mask.parentNode.removeChild(mask); });
        box.appendChild(cancel);
        mask.appendChild(box);
        mask.addEventListener('click', function (e) { if (e.target === mask) mask.parentNode && mask.parentNode.removeChild(mask); });
        document.body.appendChild(mask);
    }

    function beginEdit(msg) {
        editingId = msg.id;
        els.input.value = msg.text || '';
        els.input.focus();
        try { els.input.setSelectionRange(els.input.value.length, els.input.value.length); } catch (e) {}
        autoGrow();
        toast('编辑中：修改后发送即更新该消息');
    }

    /** 就地更新气泡正文（编辑/撤销编辑后即时生效）。 */
    function updateBubble(id, text) {
        if (!els.list) return;
        var nodes = els.list.querySelectorAll('[data-mid]');
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].getAttribute('data-mid') !== id) continue;
            var bubble = nodes[i].querySelector('.bubble');
            if (bubble) {
                bubble.innerHTML = escapeHtml(text) + '<span class="lc-edited"> (已编辑)</span>';
            }
            break;
        }
    }

    function removeBubble(id) {
        if (!els.list) return;
        var nodes = els.list.querySelectorAll('[data-mid]');
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].getAttribute('data-mid') === id) {
                var prev = nodes[i].previousSibling;
                if (prev && prev.classList && prev.classList.contains('msg-time-row')) {
                    els.list.removeChild(prev);
                }
                els.list.removeChild(nodes[i]);
                delete renderedIds[id];
                return;
            }
        }
    }

    function bindLongPress(el, fn) {
        var timer = null;
        el.addEventListener('touchstart', function () {
            timer = setTimeout(fn, 550);
        }, { passive: true });
        el.addEventListener('touchend', function () { clearTimeout(timer); });
        el.addEventListener('touchmove', function () { clearTimeout(timer); });
        el.addEventListener('contextmenu', function (e) { e.preventDefault(); fn(); });
    }

    function askDelete(msg) {
        dialog({
            title: '删除这条消息？',
            text: '双方都会删除（本地立即生效，并通知在线设备同步）。',
            okText: '删除',
            danger: true,
            onOk: function () {
                api('/api/delete', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ id: msg.id })
                }).then(function () { removeBubble(msg.id); })
                  .catch(function () { toast('删除失败'); });
            }
        });
    }

    function buildBody(bubble, msg) {
        var url = resourceOf(msg);

        switch (msg.kind) {
            // 局域网音乐卡片（播放端下发，切歌时用 op=patch 原地更新）
            case 'musiccard': { renderSharedMusicCard(bubble, msg); break; }

            // 视频分享卡片（播放器里手动点「分享」生成）
            case 'videocard': { renderVideoShareCard(bubble, msg); break; }

            // 程序更新卡片（网页端无原生桥，仅展示信息 + 主页链接）
            case 'update': { renderUpdateCard(bubble, msg); break; }

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
                    audio.play().catch(function () { toast('播放失败'); });
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
                bubble.classList.add('plain');
                var img = document.createElement('img');
                img.className = 'msg-image';
                img.src = appProxy(url);
                img.alt = '图片';
                img.loading = 'lazy';
                img.addEventListener('click', function () { Player.viewImage(appProxy(url)); });
                img.addEventListener('load', scrollToBottom);
                bubble.appendChild(img);
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
                    Player.playVideo(appProxy(url), label.textContent);
                });
                bubble.appendChild(card);
                break;
            }

            case 'music': {
                bubble.classList.add('plain');
                var mc = document.createElement('div');
                mc.className = 'music-card';
                var fallback = msg.title || fileNameOf(url) || '音乐';

                mc.innerHTML =
                    '<div class="mc-cover">' + WI.svg('music') + '</div>' +
                    '<div class="mc-info">' +
                    '<div class="mc-name"></div>' +
                    '<div class="mc-artist">点击播放</div>' +
                    '</div>' +
                    '<div class="mc-play">' + WI.svg('play') + '</div>';
                mc.querySelector('.mc-name').textContent = fallback;
                mc.addEventListener('click', function () { Player.playMusic(appProxy(url), fallback); });
                bubble.appendChild(mc);
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
                lc.addEventListener('click', function () { window.open(url, '_blank'); });
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

            default: {
                bubble.innerHTML = linkify(msg.text || '');
                bubble.querySelectorAll('a[data-href]').forEach(function (a) {
                    a.addEventListener('click', function (e) {
                        e.preventDefault();
                        window.open(a.getAttribute('data-href'), '_blank');
                    });
                });
            }
        }
    }

    /* ==================== 一起听 / 视频分享卡片（局域网共享） ==================== */

    /* ── 同步听时钟：NTP 四时间戳（T1..T4）求发起端与本机的时钟差 ──────────────
       offset = ((T2-T1)+(T3-T4))/2，delay = (T4-T1)-(T3-T2)。
       取最近 8 次里 RTT 最小的一组（排队只会让 RTT 变大）。拿到 offset 后
       卡片里的 ts（发起端时间）就能换算成"此刻发起端播到哪"，不再慢半拍。 */
    var sclk = { off: 0, rtt: 0, base: '', samples: [], timer: 0 };
    function probeClock(base) {
        if (!base || typeof fetch !== 'function') return;
        try { sclk.base = String(base).replace(/\/$/, ''); } catch (e) { return; }
        var t1 = Date.now();
        fetch(sclk.base + '/api/sync/time', { cache: 'no-store' })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                var t4 = Date.now();
                var t2 = Number(j.t2) || 0, t3 = Number(j.t3) || 0;
                if (!t2 || !t3) return;
                var rtt = (t4 - t1) - (t3 - t2);
                if (rtt < 0 || rtt > 5000) return;
                var off = ((t2 - t1) + (t3 - t4)) / 2;
                sclk.samples.push({ o: off, r: rtt });
                if (sclk.samples.length > 8) sclk.samples.shift();
                var best = sclk.samples.slice().sort(function (a, b) { return a.r - b.r; })[0];
                sclk.off = best.o; sclk.rtt = best.r;
            }).catch(function () {});
    }
    function clockProbeStart(base, often) {
        if (!base) return;
        probeClock(base);
        if (sclk.timer) clearInterval(sclk.timer);
        sclk.timer = setInterval(function () { probeClock(base); }, often ? 3000 : 15000);
    }
    function hostNow() { return Date.now() + sclk.off; }
    /** 卡片 ts 到"此刻"的年龄（毫秒）；未对时退化为本地单调时钟 + 估计单程时延。 */
    function anchorAge(ts, atLocal) {
        if (sclk.rtt > 0 && ts > 0) {
            var a = hostNow() - ts;
            return Math.max(0, Math.min(15000, a));
        }
        return Math.max(0, (window.performance ? performance.now() : Date.now()) - atLocal)
            + Math.max(30, sclk.rtt / 2);
    }

    /* ── 一起看：跟随端状态机 ────────────────────────────────────────────
       视频源只用宿主机代理（watchHost + watchUrl），不碰外源 CDN；
       进度来自宿主机状态镜像，ts/seq 时间戳校验 + /api/sync/time 对时 +
       250ms 闭环校正，与「同步听」是同一套判据。宿主机 15 秒无帧即离线。 */
    var watch = {
        active: false, cardId: '', host: '', base: '', url: '', title: '',
        ts: 0, seq: 0, pos: 0, dur: 0, playing: false, atLocal: 0, age: 0,
        epIndex: -1, vkey: '', pollTimer: 0, corrTimer: 0, lastSeek: 0,
        offline: false, ready: false, first: true
    };

    function watchStop() {
        if (!watch.active) return;
        watch.active = false;
        watch.ready = false;
        if (watch.pollTimer) clearInterval(watch.pollTimer);
        if (watch.corrTimer) clearInterval(watch.corrTimer);
        watch.pollTimer = watch.corrTimer = 0;
    }

    /* 宿主机 IP 探测失败时卡片里可能写着 127.0.0.1，对局域网设备毫无意义；
       能收到卡片/发现会话就说明当前页面所在网关连着宿主机，退回当前源即可。 */
    function watchHostOf(h) {
        var s = String(h || '').replace(/\/$/, '');
        if (!s) return location.origin;
        try {
            var u = new URL(s, location.href);
            var loop = (u.hostname === '127.0.0.1' || u.hostname === 'localhost' || u.hostname === '::1');
            if (loop && location.hostname !== u.hostname) return location.origin;
        } catch (e) { return location.origin; }
        return s;
    }

    /* 网关转发会改写 Host，显式带 h= 让 VodStream 把清单里的分片也写成宿主机绝对地址 */
    function withProxyHost(url, host) {
        if (!url || !host) return url;
        return url + (url.indexOf('?') >= 0 ? '&' : '?') + 'h=' + encodeURIComponent(host);
    }

    function watchStart(msg) {
        var host = watchHostOf(msg.watchHost);
        var rel = msg.watchUrl || '';
        if (!rel) { toast('发起端不在线，无法一起看'); return; }
        watchStop();
        watch.active = true;
        watch.cardId = msg.watchCard || msg.id || '';
        watch.host = host;
        watch.base = host + '/api/media/vod/watchtogether';
        watch.url = withProxyHost(
            /^https?:\/\//i.test(rel) ? rel : host + (rel.charAt(0) === '/' ? rel : '/' + rel), host);
        watch.title = msg.title || msg.name || '一起看';
        watch.ts = 0; watch.seq = 0; watch.pos = 0; watch.dur = 0;
        watch.playing = false; watch.atLocal = Date.now(); watch.age = 0;
        watch.epIndex = Number(msg.episodeIndex) || 0;
        watch.vkey = msg.vkey || '';
        watch.offline = false; watch.lastSeek = 0; watch.ready = false; watch.first = true;
        clockProbeStart(host, true);
        try { Player.playVideo(watch.url, watch.title); } catch (e) {}
        watchPoll();
        watch.pollTimer = setInterval(watchPoll, 1200);
        watch.corrTimer = setInterval(watchCorrect, 250);
        toast('已加入一起看，正在对齐宿主机进度…');
    }

    function watchPoll() {
        if (!watch.active) return;
        fetch(watch.base, { cache: 'no-store' })
            .then(function (r) { return r.json(); })
            .then(watchAbsorb).catch(function () {});
    }

    function watchAbsorb(st) {
        if (!watch.active || !st) return;
        if (!st.online) {
            if (!watch.offline) { watch.offline = true; toast('发起端不在线，一起看已暂停'); }
            return;
        }
        watch.offline = false;
        if (st.cardId && watch.cardId && st.cardId !== watch.cardId) {
            if (!watch.offline) { watch.offline = true; toast('该一起看会话已结束'); }
            return;
        }

        // 时间戳校验：双渠道迟到帧 / 重放帧不得把已追上的进度拉回去
        var inTs = Number(st.ts) || 0, inSeq = Number(st.seq) || 0;
        if (inTs > 0 && watch.ts > 0 && inTs < watch.ts - 1000) return;
        if (inSeq > 0 && watch.seq > 0 && inSeq < watch.seq && inTs <= watch.ts) return;
        watch.ts = inTs; watch.seq = inSeq;

        // 宿主机地址变化时跟着切换：视频源与状态帧始终落在它的代理上，
        // 才能命中宿主机自己看片时落盘的 3 天缓存
        if (st.watchHost) {
            var h = watchHostOf(st.watchHost);
            if (h && h !== watch.host) {
                watch.host = h;
                watch.base = h + '/api/media/vod/watchtogether';
            }
        }

        var age = Number(st.serverTime) - inTs;
        if (sclk.rtt > 0 && inTs > 0) age = hostNow() - inTs;
        if (!isFinite(age) || age < 0) age = 0;
        if (age > 15000) age = 15000;

        var pos = Number(st.position) || 0;
        var rel = st.watchUrl || '';
        var newUrl = withProxyHost(/^https?:\/\//i.test(rel) ? rel
            : watch.host + (rel.charAt(0) === '/' ? rel : '/' + rel), watch.host);
        var changed = (st.vkey && st.vkey !== watch.vkey)
            || (Number(st.episodeIndex) || 0) !== watch.epIndex
            || newUrl !== watch.url;
        if (watch.first) {
            // 首帧只建立基线：播放器已按卡片里的宿主机代理地址起播
            watch.first = false;
            watch.vkey = st.vkey || '';
            watch.epIndex = Number(st.episodeIndex) || 0;
            if (rel && newUrl) watch.url = newUrl;
        } else if (changed && rel) {
            watch.vkey = st.vkey || '';
            watch.epIndex = Number(st.episodeIndex) || 0;
            watch.url = newUrl;
            try {
                Player.playVideo(newUrl, st.title || watch.title);
                var v0 = Player.videoEl();
                if (v0) v0.addEventListener('loadedmetadata', function once() {
                    v0.removeEventListener('loadedmetadata', once);
                    try { v0.currentTime = Math.max(0, (pos + (st.playing ? age : 0)) / 1000); } catch (e) {}
                });
            } catch (e) {}
        }

        watch.pos = pos;
        watch.dur = Number(st.duration) || 0;
        watch.playing = !!st.playing;
        watch.age = age;
        watch.atLocal = Date.now();
        watch.ready = true;
    }

    function watchCorrect() {
        if (!watch.active || watch.offline || !watch.ready) return;
        var v = Player.videoEl && Player.videoEl();
        if (!v) return;
        var elapsed = Date.now() - watch.atLocal;
        if (elapsed < 0) elapsed = 0;
        var want = watch.pos + (watch.playing ? watch.age + elapsed : 0);
        if (watch.dur > 0) want = Math.min(want, watch.dur);
        if (want < 0) want = 0;
        if (watch.playing && v.paused) v.play().catch(function () {});
        else if (!watch.playing && !v.paused) { v.pause(); v.playbackRate = 1; return; }
        var d = want - (v.currentTime || 0) * 1000;
        var now = Date.now();
        if (Math.abs(d) > 1500 && now - watch.lastSeek > 1200) {
            watch.lastSeek = now;
            v.playbackRate = 1;
            try { v.currentTime = Math.max(0, want / 1000); } catch (e) {}
        } else if (Math.abs(d) > 120) {
            v.playbackRate = Math.max(0.94, Math.min(1.06, 1 + d / 6000));
        } else v.playbackRate = 1;
    }

    function fmtCardMs(ms) {
        if (!ms || ms < 0) ms = 0;
        var s = Math.floor(ms / 1000), m = Math.floor(s / 60);
        s = s % 60;
        return (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
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

    /** 外部网页端的音乐卡片：显示对端播放状态，并可用「一起听」按共享列表连续播放。 */
    function renderSharedMusicCard(bubble, msg) {
        bubble.classList.add('plain');
        var card = document.createElement('div');
        card.setAttribute('data-musiccard', '1');
        card._coverB64 = msg.cover || '';
        card._lrc = msg.lrc || '';
        card._lrcKey = msg.lrcKey || '';
        // 看到卡片就顺手对一次时，网页端不需要真的加入同步也能显示准确进度
        try { probeClock(msg.peerHost || msg.ltHost || ''); } catch (e) {}
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
        lyric.textContent = msg.lrc ? String(msg.lrc).replace(/\[[^\]]*\]/g, '').trim().split('\n')[0] : '正在播放';
        card.appendChild(lyric);

        var bar = document.createElement('div');
        bar.style.cssText = 'height:3px;background:#eef0f3;border-radius:2px;overflow:hidden';
        var fill = document.createElement('div');
        fill.style.cssText = 'height:100%;width:0;background:#12b7f5';
        bar.appendChild(fill);
        card.appendChild(bar);

        var row = document.createElement('div');
        row.style.cssText = 'display:flex;align-items:center;margin-top:6px;font-size:10px;color:#8e9099';
        var time = document.createElement('span');
        time.style.cssText = 'flex:1';
        row.appendChild(time);
        var from = document.createElement('span');
        from.textContent = msg.fromName ? ('来自 ' + msg.fromName) : '局域网';
        row.appendChild(from);
        card.appendChild(row);

        // 进度锚点：position 是发起端在 ts 时刻的播放位置。
        // 用对时后的发起端时间轴推算，自动补偿投递时延与两机时钟差。
        var st = {
            duration: Number(msg.duration) || 0,
            position: Number(msg.position) || 0,
            playing: !!msg.playing,
            ts: Number(msg.ts) || 0,
            atLocal: window.performance ? performance.now() : Date.now()
        };
        function tick() {
            var pos = st.position + (st.playing ? anchorAge(st.ts, st.atLocal) : 0);
            if (st.duration > 0) pos = Math.min(pos, st.duration);
            time.textContent = fmtCardMs(pos) + ' / ' + fmtCardMs(st.duration);
            fill.style.width = (st.duration > 0 ? Math.min(100, pos / st.duration * 100) : 0) + '%';
        }
        tick();
        card._timer = setInterval(tick, 250);

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
                function parseList() {
                    var arr = [];
                    try { arr = JSON.parse(card._playlist); } catch (e) {}
                    return arr;
                }

                var jb = mkBtn('播放', 'play', '#e8f7fe', '#12b7f5');
                jb.setAttribute('data-share', 'playlist');
                jb.onclick = function (ev) {
                    ev.stopPropagation();
                    try { if (Player.syncExit) Player.syncExit(); } catch (e) {}
                    var arr = parseList();
                    if (!arr.length) { toast('共享列表不可用'); return; }
                    if (Player.playSharedList) Player.playSharedList(arr, card._ltIndex);
                    toast('已播放共享歌单（可自由切歌）');
                };

                var sst = null;
                try { sst = Player.syncState && Player.syncState(); } catch (e) {}
                var syncing = !!(sst && sst.active &&
                    (!sst.cardId || !card._cardId || sst.cardId === card._cardId));
                var sb = mkBtn(syncing ? '退出一起听' : '一起听', syncing ? 'close' : 'link',
                    syncing ? '#fff0f0' : '#eafaf1', syncing ? '#d9534f' : '#12a05c');
                sb.setAttribute('data-share', 'sync');
                sb.onclick = function (ev) {
                    ev.stopPropagation();
                    if (syncing) {
                        try { Player.syncExit(); } catch (e) {}
                        toast('已退出一起听');
                        bubble.innerHTML = '';
                        bubble.className = 'bubble';
                        buildBody(bubble, msg);
                        return;
                    }
                    var arr = parseList();
                    if (!arr.length) { toast('共享列表不可用'); return; }
                    if (Player.syncStart) Player.syncStart(arr, card._ltIndex, card._cardId, msg);
                    toast('已加入一起听，正在对齐播放进度…');
                };
                joinRow.appendChild(jb);
                joinRow.appendChild(sb);
                card.appendChild(joinRow);
            } catch (e) {}
        }
        bubble.appendChild(card);
    }

    /** 外部网页端的视频分享卡片：瀑布墙封面 + 右下角「观看」「一起看」双按钮。
        「观看」走外源 CDN 真实 m3u8；「一起看」走宿主机代理并跟随其进度。 */
    function renderVideoShareCard(bubble, msg) {
        bubble.classList.add('plain');
        var card = document.createElement('div');
        card.style.cssText = 'width:250px;border-radius:12px;overflow:hidden;background:#111;cursor:pointer;position:relative;box-shadow:0 1px 3px rgba(0,0,0,.12)';

        var host = String(msg.watchHost || '').replace(/\/$/, '');
        var pp = msg.posterProxy || '';
        var src = msg.cover ? ('data:image/jpeg;base64,' + msg.cover) : '';
        if (!src && pp && host) src = host + (pp.charAt(0) === '/' ? pp : '/' + pp);
        if (!src && pp && /^https?:/i.test(pp)) src = pp;
        // 优先复用宿主机海报代理（3 天缓存 + 生成兜底），拿不到 host 才退回 remote 代理
        if (!src && msg.poster) {
            var rawPoster = String(msg.poster);
            src = (host && /^https?:/i.test(rawPoster))
                ? host + '/api/media/vod/poster?u=' + encodeURIComponent(rawPoster)
                : appProxy(rawPoster);
        }
        if (!src) src = '/api/media/vod/poster?n=' + encodeURIComponent(msg.title || msg.name || '视频');
        if (src) {
            var img = document.createElement('img');
            img.src = src;
            img.alt = '';
            img.style.cssText = 'display:block;width:100%;height:140px;object-fit:cover;opacity:.85';
            img.onerror = function () { img.style.display = 'none'; };
            card.appendChild(img);
        } else {
            var ph = document.createElement('div');
            ph.style.cssText = 'height:140px;background:linear-gradient(135deg,#1b1f24,#2b323b);display:flex;align-items:flex-start;justify-content:flex-start;padding:12px;color:#6b7480';
            ph.innerHTML = WI.svg('video', 26);
            card.appendChild(ph);
        }
        var tri = document.createElement('div');
        tri.style.cssText = 'position:absolute;top:52px;left:50%;transform:translateX(-50%);width:44px;height:44px;border-radius:50%;background:rgba(0,0,0,.55);color:#fff;display:flex;align-items:center;justify-content:center';
        tri.innerHTML = WI.svg('play', 24);
        card.appendChild(tri);
        var label = document.createElement('div');
        label.style.cssText = 'padding:9px 10px 0;color:#fff;font-size:13px;line-height:1.35;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
        label.textContent = msg.title || msg.name || '分享的视频';
        card.appendChild(label);

        var row = document.createElement('div');
        row.style.cssText = 'display:flex;justify-content:flex-end;gap:6px;padding:8px 10px 10px';
        function mkBtn(label, bg, fg, fn) {
            var b = document.createElement('button');
            b.type = 'button';
            b.textContent = label;
            b.style.cssText = 'border:0;background:' + bg + ';color:' + fg + ';border-radius:10px;padding:5px 10px;font-size:11px;cursor:pointer';
            b.onclick = function (ev) { ev.stopPropagation(); fn(); };
            return b;
        }
        var plain = function () {
            watchStop();
            var u = appProxy(msg.url || msg.mediaUrl || '');
            if (u) Player.playVideo(u, msg.title || '分享的视频');
        };
        row.appendChild(mkBtn('观看', '#e8f7fe', '#12b7f5', plain));
        row.appendChild(mkBtn('一起看', '#eafaf1', '#12a05c', function () { watchStart(msg); }));
        card.appendChild(row);
        card.addEventListener('click', plain);
        bubble.appendChild(card);
    }

    /** 程序更新卡片：网页端无原生桥，展示版本/说明/状态 + 主页链接；安装请在 App 内点击。 */
    function renderUpdateCard(bubble, msg) {
        bubble.classList.add('plain');
        var card = document.createElement('div');
        card.style.cssText = 'width:250px;border-radius:12px;padding:12px;background:#fff;color:#111;box-shadow:0 1px 3px rgba(0,0,0,.12);font-size:13px';
        var data = null;
        try { data = JSON.parse(msg.text || ''); } catch (e) {}
        if (!data || data.schema !== 'mchat.update.v1') {
            card.textContent = '程序更新';
            bubble.appendChild(card);
            return;
        }
        function line(txt, color, size, bold) {
            var d = document.createElement('div');
            d.textContent = txt;
            d.style.cssText = 'color:' + color + ';font-size:' + size + 'px' + (bold ? ';font-weight:600' : '') + ';margin:2px 0';
            return d;
        }
        card.appendChild(line('程序更新  ' + (data.version || ''), '#111', 15, true));
        card.appendChild(line(data.title || '', '#007aff', 13, false));
        if (data.notes) card.appendChild(line(data.notes, '#555', 13, false));
        if (data.status) card.appendChild(line(data.status, '#8e8e93', 12, false));
        var tip = line('请在 App 内点击安装', '#8e8e93', 11, false);
        card.appendChild(tip);
        card.style.cursor = 'pointer';
        card.addEventListener('click', function () {
            var u = data.home || 'https://github.com/yuwenhui2020/AhoChatA';
            try { window.open(u, '_blank'); } catch (e) {}
        });
        bubble.appendChild(card);
    }

    function linkify(text) {
        // 先转义再拼链接；颜色交给 CSS，蓝底气泡内自动变白字。
        return escapeHtml(text).replace(/(https?:\/\/[^\s<]+)/g, function (u) {
            return '<a href="#" class="msg-bubble-link" data-href="' + u + '">' + u + '</a>';
        });
    }

    /* ==================== 播放器（视频 / 图片 / 音乐） ==================== */

    var Player = (function () {
        var p = {};
        // 当前曲目的歌词行与高亮下标，用于随播放进度滚动
        var lyricState = { lines: [], activeIndex: -1 };

        function openOverlay(id) { document.getElementById(id).classList.add('show'); }

        function closeOverlay(id) {
            var node = document.getElementById(id);
            if (!node) return;
            node.classList.remove('show');
            if (id === 'videoPlayer') {
                p.video.pause();
                if (p.hls) { try { p.hls.destroy(); } catch (e) {} p.hls = null; }
                p.video.removeAttribute('src');
                p.video.load();
                try { watchStop(); } catch (e) {}
            } else if (id === 'musicPlayer') {
                // 网页端与 app 保持一致：收起界面不打断播放
            }
        }

        p.init = function () {
            p.videoOverlay = document.getElementById('videoPlayer');
            p.video = document.getElementById('videoEl');
            p.videoTitle = document.getElementById('videoTitle');
            p.image = document.getElementById('imageEl');
            p.audio = document.getElementById('audioEl');
            p.cover = document.getElementById('musicCover');
            p.name = document.getElementById('musicName');
            p.artist = document.getElementById('musicArtist');
            p.lyricBox = document.getElementById('lyricBox');
            p.cur = document.getElementById('musicCur');
            p.dur = document.getElementById('musicDur');
            p.bar = document.getElementById('musicBar');
            p.fill = document.getElementById('musicFill');
            p.toggle = document.getElementById('musicToggle');

            document.querySelectorAll('[data-close]').forEach(function (btn) {
                btn.addEventListener('click', function () { closeOverlay(btn.getAttribute('data-close')); });
            });

            p.toggle.addEventListener('click', function () {
                if (p.audio.paused) p.audio.play().catch(function () {}); else p.audio.pause();
            });
            p.audio.addEventListener('play', function () { p.toggle.innerHTML = WI.svg('pause'); });
            p.audio.addEventListener('pause', function () { p.toggle.innerHTML = WI.svg('play'); });
            p.audio.addEventListener('loadedmetadata', function () {
                p.dur.textContent = formatTime(p.audio.duration);
            });
            p.audio.addEventListener('timeupdate', onAudioTime);
            // 拖动进度后立即把歌词跳到对应位置，不必等下一次 timeupdate
            p.audio.addEventListener('seeked', onAudioTime);

            var seeking = false;
            function seekAt(clientX) {
                var rect = p.bar.getBoundingClientRect();
                var ratio = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
                if (isFinite(p.audio.duration)) p.audio.currentTime = ratio * p.audio.duration;
                p.fill.style.width = (ratio * 100) + '%';
            }
            p.bar.addEventListener('touchstart', function (e) { seeking = true; seekAt(e.touches[0].clientX); }, { passive: true });
            p.bar.addEventListener('touchmove', function (e) { if (seeking) seekAt(e.touches[0].clientX); }, { passive: true });
            p.bar.addEventListener('touchend', function () { seeking = false; });
            p.bar.addEventListener('click', function (e) { seekAt(e.clientX); });
        };

        /** 进度更新：刷新时间/进度条，并把当前歌词行高亮居中 */
        function onAudioTime() {
            var a = p.audio;
            p.cur.textContent = formatTime(a.currentTime);
            var ratio = (isFinite(a.duration) && a.duration > 0) ? a.currentTime / a.duration : 0;
            p.fill.style.width = (ratio * 100) + '%';

            var lines = lyricState.lines;
            // 无歌词，或歌词没有时间戳（纯文本）时不做滚动高亮
            if (!lines.length || lines[0].time < 0) return;

            var idx = -1;
            for (var i = 0; i < lines.length; i++) {
                if (lines[i].time <= a.currentTime) idx = i; else break;
            }
            if (idx === lyricState.activeIndex) return;
            lyricState.activeIndex = idx;

            var nodes = p.lyricBox.children;
            for (var j = 0; j < nodes.length; j++) nodes[j].classList.toggle('active', j === idx);
            if (idx >= 0 && nodes[idx]) {
                p.lyricBox.scrollTo({
                    top: nodes[idx].offsetTop - p.lyricBox.clientHeight / 2 + nodes[idx].clientHeight / 2,
                    behavior: 'smooth'
                });
            }
        }

        function renderLyrics(list) {
            p.lyricBox.innerHTML = '';
            lyricState.lines = list;
            lyricState.activeIndex = -1;

            if (!list.length) {
                var tip = document.createElement('div');
                tip.className = 'lyric-empty';
                tip.textContent = '暂无内嵌歌词';
                p.lyricBox.appendChild(tip);
                return;
            }
            for (var i = 0; i < list.length; i++) {
                var d = document.createElement('div');
                d.className = 'lyric-line';
                d.textContent = list[i].text;
                p.lyricBox.appendChild(d);
            }
        }

        p.playVideo = function (url, title) {
            p.videoTitle.textContent = title || '';
            p.video.pause();
            if (p.hls) { try { p.hls.destroy(); } catch (e) {} p.hls = null; }
            p.video.removeAttribute('src');
            var isM3u8 = /\.m3u8/i.test(url || '') || /\/api\/media\/vod\/stream/i.test(url || '');
            if (isM3u8 && window.Hls && window.Hls.isSupported()) {
                // 宿主机代理 + 预取后分片命中很快，缓冲窗口收小可以更快起播；
                // 放宽 loading/starvation 容忍，避免局域网偶发抖动触发整段重缓冲
                p.hls = new window.Hls({
                    enableWorker: true, lowLatencyMode: false,
                    maxBufferLength: 20, backBufferLength: 30,
                    maxBufferSize: 40 * 1000 * 1000,
                    maxLoadingDelay: 2, maxStarvationDelay: 4,
                    fragLoadingMaxRetry: 2, manifestLoadingMaxRetry: 2,
                    fragLoadingTimeOut: 12000, manifestLoadingTimeOut: 12000
                });
                p.hls.loadSource(url);
                p.hls.attachMedia(p.video);
                p.hls.on(window.Hls.Events.MANIFEST_PARSED, function () {
                    p.video.play().catch(function () {});
                });
            } else {
                p.video.src = url;
                p.video.load();
            }
            openOverlay('videoPlayer');
            p.video.play().catch(function () {});
        };

        p.videoEl = function () { return p.video; };

        p.viewImage = function (url) {
            p.image.src = url;
            openOverlay('imageViewer');
        };

        p.playMusic = function (url, fallbackName) {
            p.name.textContent = fallbackName || '加载中…';
            p.artist.textContent = '';
            p.cover.removeAttribute('src');
            renderLyrics([]);
            openOverlay('musicPlayer');

            p.audio.src = url;
            p.audio.play().catch(function () {});

            // 异步补齐封面/歌名/内嵌歌词，不阻塞起播
            readAudioMeta(url, fallbackName).then(function (meta) {
                p.name.textContent = meta.title;
                p.artist.textContent = meta.album ? (meta.artist + ' · ' + meta.album) : meta.artist;
                if (meta.cover) p.cover.src = meta.cover;
                // 同步听：歌词以发起端卡片为准，别让本地流媒体元数据把它冲掉
                if (!syncCard) renderLyrics(parseLyrics(meta.lyrics));
                onAudioTime();
            });
        };

        // 「一起听」：外部网页端没有原生播放器，用页面自己的 audio 元素按共享列表连播
        var sharedQueue = [], sharedIndex = 0, sharedBound = false;
        function playSharedAt() {
            var it = sharedQueue[sharedIndex];
            if (!it || !it.u) return;
            p.playMusic(it.u, it.t || '共享歌曲');
            p.artist.textContent = [it.a, it.b].filter(Boolean).join(' · ');
            if (it.c) p.cover.src = it.c;
            if (it.l) renderLyrics(parseLyrics(it.l));
        }
        function ensureSharedBound() {
            if (sharedBound) return;
            sharedBound = true;
            p.audio.addEventListener('ended', function () {
                // 同步听跟随端：切歌只能由发起端卡片驱动，绝不自动续播
                if (syncCard) return;
                if (!sharedQueue.length) return;
                sharedIndex++;
                if (sharedIndex >= sharedQueue.length) { sharedQueue = []; return; }
                playSharedAt();
            });
        }
        p.playSharedList = function (list, index) {
            sharedQueue = list || [];
            sharedIndex = Math.max(0, Math.min(Number(index) || 0, sharedQueue.length - 1));
            if (!sharedQueue.length) return;
            ensureSharedBound();
            playSharedAt();
        };

        /* 「同步听」：发起端是唯一权威时钟。
           卡片是锚点 (position, ts, playing)，目标进度 = position +（对时后的发起端此刻 - ts），
           自动补偿投递时延与两机时钟差。250ms 闭环：|drift|<120ms 不动，
           120~1500ms 用 ±6% 变速渐进追上，>1500ms 才 hard seek，避免每秒一拖。 */
        var syncCard = '', syncKey = '', syncPos = 0, syncDur = 0;
        var syncPlaying = false, syncTs = 0, syncAtLocal = 0, syncBooted = false;
        function syncApply() {
            if (!syncCard) return;
            var a = p.audio;
            if (!a) return;
            var target = syncPos + (syncPlaying ? anchorAge(syncTs, syncAtLocal) : 0);
            if (syncDur > 0) target = Math.min(target, syncDur);
            if (target < 0) target = 0;
            if (syncPlaying && a.paused) {
                try { var pr = a.play(); if (pr && pr.catch) pr.catch(function () {}); } catch (e) {}
            } else if (!syncPlaying && !a.paused) {
                try { a.pause(); } catch (e) {}
            }
            if (a.readyState < 1) { try { a.playbackRate = 1; } catch (e) {} return; }
            var drift = target - (a.currentTime || 0) * 1000;
            if (Math.abs(drift) > 1500) {
                try { a.currentTime = Math.max(0, target / 1000); } catch (e) {}
                try { a.playbackRate = 1; } catch (e) {}
            } else if (Math.abs(drift) > 120) {
                try { a.playbackRate = Math.max(0.94, Math.min(1.06, 1 + drift / 6000)); } catch (e) {}
            } else {
                try { a.playbackRate = 1; } catch (e) {}
            }
        }
        p.syncStart = function (list, index, cardId, state) {
            syncCard = cardId || 'sync';
            syncKey = '';
            sharedQueue = list || [];
            sharedIndex = Math.max(0, Math.min(Number(index) || 0, Math.max(0, sharedQueue.length - 1)));
            syncPos = 0; syncDur = 0; syncPlaying = true; syncTs = 0;
            syncAtLocal = window.performance ? performance.now() : Date.now();
            syncBooted = false;
            ensureSharedBound();
            if (p._syncTimer) clearInterval(p._syncTimer);
            p._syncTimer = setInterval(syncApply, 250);
            try { clockProbeStart((state && (state.peerHost || state.ltHost)) || '', true); } catch (e) {}
            // 第一帧必须真正起播（不能只吸收状态），之后再由 patch 校正
            if (state) p.syncFromCard(state); else { playSharedAt(); syncBooted = true; }
        };
        p.syncFromCard = function (m) {
            if (!syncCard || !m) return;
            if (m.id && syncCard !== 'sync' && m.id !== syncCard) return;
            // 双渠道可能把旧 patch 晚送到：用旧进度回锚会把已经追上的播放拉回去
            var inTs = Number(m.ts) || 0;
            if (inTs > 0 && syncTs > 0 && inTs < syncTs - 1000) return;
            if (m.playlist) {
                var arr = [];
                try { arr = typeof m.playlist === 'string' ? JSON.parse(m.playlist) : m.playlist; }
                catch (e) {}
                if (arr.length) sharedQueue = arr;
            }
            var key = m.songKey || '';
            var idx = Number(m.ltIndex) || 0;
            // 优先按发起端 songKey（列表项 k）定位，ltIndex 只作兜底
            if (key) {
                for (var i = 0; i < sharedQueue.length; i++) {
                    if (sharedQueue[i] && sharedQueue[i].k === key) { idx = i; break; }
                }
            }
            var changed = !syncBooted || (key && key !== syncKey) || idx !== sharedIndex;
            syncKey = key;
            syncPos = Number(m.position) || 0;
            syncDur = Number(m.duration) || 0;
            syncPlaying = !!m.playing;
            syncTs = Number(m.ts) || 0;
            syncAtLocal = window.performance ? performance.now() : Date.now();
            if (changed && sharedQueue.length) {
                sharedIndex = Math.max(0, Math.min(idx, sharedQueue.length - 1));
                try { p.audio.playbackRate = 1; } catch (e) {}
                playSharedAt();
                // 卡片带了属于本曲的完整歌词就直接用，不必等列表里那几句
                if (m.lrc && (!m.lrcKey || m.lrcKey === key)) {
                    try { renderLyrics(parseLyrics(m.lrc)); } catch (e) {}
                }
            }
            syncBooted = true;
            syncApply();
        };
        p.syncExit = function () {
            syncCard = '';
            syncBooted = false;
            if (p._syncTimer) { clearInterval(p._syncTimer); p._syncTimer = 0; }
            if (sclk.timer) { clearInterval(sclk.timer); sclk.timer = 0; }
            try { p.audio.playbackRate = 1; } catch (e) {}
        };
        p.syncState = function () { return { active: !!syncCard, cardId: syncCard }; };
        // 只读调试句柄：查看对时结果（offset/RTT），便于控制台或离线校验排查延时
        p.syncClockState = function () { return { offset: sclk.off, rtt: sclk.rtt, samples: sclk.samples.length }; };
        p.anchorAge = anchorAge;

        // 暴露一个只读调试句柄，便于控制台 / CDP 排查同步听状态
        try { window.MchatPlayer = p; } catch (e) {}

        p.closeTop = function () {
            var ids = ['musicPlayer', 'videoPlayer', 'imageViewer'];
            for (var i = 0; i < ids.length; i++) {
                if (document.getElementById(ids[i]).classList.contains('show')) {
                    closeOverlay(ids[i]);
                    return true;
                }
            }
            return false;
        };

        return p;
    })();

    /* ==================== 与宿主通信 ==================== */

    // API 基地址：本应走宿主内置 HTTP 服务（WebServer）注入的 window.__webEndpoint
    // （形如 192.168.1.8:6789）。注意：Web 服务器功能已移除，__webEndpoint 不再注入，
    // 此时 apiBase 返回空串，fallback 到相对路径（普通浏览器静态托管场景）。
    function apiBase() {
        return (typeof window.__webEndpoint !== 'undefined' && window.__webEndpoint)
            ? 'http://' + window.__webEndpoint : '';
    }

    // 双因素认证：接口返回 401 = 本机开了动态口令，要输「设备」页「本机」下方的 6 位数字。
    // 验证通过后服务端下发 Cookie，之后这段时间的请求自动放行，不用反复输。
    function askOtp() {
        return new Promise(function (resolve, reject) {
            var code = window.prompt('这台设备开启了双因素认证。\n'
                + '请输入 AhoChat「设备」页「本机」下方的 6 位动态口令：');
            if (!code) { reject(new Error('需要动态口令')); return; }
            fetch(apiBase() + '/__otp?code=' + encodeURIComponent(String(code).replace(/\D/g, '')) + '&r=/',
                { credentials: 'same-origin' })
                .then(function (r) { (r.ok || r.type === 'opaqueredirect') ? resolve(true) : reject(new Error('口令不正确')); })
                .catch(function () { reject(new Error('口令验证失败')); });
        });
    }

    var otpBusy = false;
    function api(path, options) {
        return fetch(apiBase() + path, options).then(function (r) {
            if (r.status !== 401) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            }
            if (otpBusy) throw new Error('需要动态口令');
            otpBusy = true;
            return askOtp().then(function () {
                otpBusy = false;
                return fetch(apiBase() + path, options).then(function (r2) {
                    if (!r2.ok) throw new Error('HTTP ' + r2.status);
                    return r2.json();
                });
            }, function (e) { otpBusy = false; throw e; });
        });
    }

    function toast(msg) {
        try {
            var t = document.createElement('div');
            t.textContent = msg;
            t.style.cssText = 'position:fixed;left:50%;bottom:90px;transform:translateX(-50%);' +
                'background:rgba(0,0,0,.8);color:#fff;padding:8px 14px;border-radius:8px;' +
                'font-size:13px;z-index:999;max-width:80vw;text-align:center;';
            document.body.appendChild(t);
            setTimeout(function () { t.remove(); }, 1600);
        } catch (e) {}
    }

    /* ==================== 语音录制（访客身份经 HTTP 发送） ==================== */

    var Voice = (function () {
        var rec = null, chunks = [], stream = null, startTime = 0, timerId = null;

        function supported() {
            return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia &&
                     (window.MediaRecorder || window.webkitMediaRecorder));
        }

        function start(onTick) {
            if (rec) return false;
            if (!supported()) { toast('当前环境不支持录音'); return false; }
            navigator.mediaDevices.getUserMedia({ audio: true }).then(function (s) {
                stream = s;
                var MR = window.MediaRecorder || window.webkitMediaRecorder;
                chunks = [];
                rec = new MR(stream);
                rec.ondataavailable = function (e) { if (e.data && e.data.size) chunks.push(e.data); };
                rec.onstop = function () {
                    stopStream();
                    var blob = new Blob(chunks, { type: rec.mimeType || 'audio/webm' });
                    var reader = new FileReader();
                    reader.onload = function () {
                        var base64 = String(reader.result).split(',')[1] || '';
                        var dur = Math.max(1, Math.round((Date.now() - startTime) / 1000));
                        var ext = (rec.mimeType || '').indexOf('ogg') >= 0 ? 'ogg'
                                : (rec.mimeType || '').indexOf('mp4') >= 0 ? 'm4a' : 'webm';
                        sendVoice(base64, 'voice.' + ext, dur);
                    };
                    reader.readAsDataURL(blob);
                };
                startTime = Date.now();
                rec.start();
                if (onTick) timerId = setInterval(function () { onTick(Math.round((Date.now() - startTime) / 1000)); }, 200);
            }).catch(function () { toast('无法访问麦克风'); });
            return true;
        }

        function stop() {
            if (timerId) { clearInterval(timerId); timerId = null; }
            if (rec && rec.state !== 'inactive') { try { rec.stop(); } catch (e) {} }
            rec = null;
        }

        function stopStream() {
            if (stream) { stream.getTracks().forEach(function (t) { t.stop(); }); stream = null; }
        }

        return { start: start, stop: stop, supported: supported };
    })();

    /** 以访客身份发送语音消息（base64 经 /api/send 落地） */
    function sendVoice(base64, name, duration) {
        api('/api/send', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ kind: 'voice', text: '语音', duration: duration, base64: base64, name: name, conv: conv })
        }).then(function (sent) {
            if (sent && sent.id && !renderedIds[sent.id]) {
                renderedIds[sent.id] = true;
                renderMessage(sent);
            }
        }).catch(function () { toast('发送失败'); });
    }

    function loadSession() {
        return api('/api/session').then(function (s) {
            self.guestId = s.guestId;
            self.name = s.name;
            self.ip = s.ip;
            els.guestTag.textContent = s.name || '网页端';
            els.status.textContent = '已连接 ' + (s.host || '宿主设备');
            addSystemTip('已以访客身份加入：' + (s.name || '网页端'));
        });
    }

    /**
     * 长轮询循环：服务端在没有新消息时最多挂起 25 秒，
     * 因此这里是一个「请求 → 渲染 → 立刻再请求」的连续循环。
     */
    function pollLoop() {
        if (polling) return;
        polling = true;

        function next(delay) {
            // 后台不再续链：让这一轮请求自然结束，等回到前台再重新起链
            if (document.hidden) { polling = false; return; }
            setTimeout(function () { polling = false; pollLoop(); }, delay || 0);
        }

        var reqConv = conv;
        api('/api/messages?since=' + since + '&conv=' + encodeURIComponent(reqConv)).then(function (res) {
            // 请求期间用户切了会话：这一批结果属于旧会话，直接丢弃
            if (reqConv !== conv) { next(0); return; }
            if (res && res.items) {
                res.items.forEach(function (msg) {
                    if (!msg) return;
                    // 服务端会把访客自己发的消息也回放回来，这里按 id 去重
                    if (msg.id && renderedIds[msg.id]) return;
                    if (msg.id) renderedIds[msg.id] = true;
                    renderMessage(msg);
                });
            }
            if (res && typeof res.seq === 'number') since = res.seq;
            // 首次快照立即返回（空房间也秒开）：没拿到消息时稍作停顿再轮询，
            // 避免 seq 仍为 0 时空转；一旦有了消息，since>0 会走 25s 长轮询。
            var got = (res && res.items) ? res.items.length : 0;
            next(got > 0 ? 0 : 400);
        }).catch(function () {
            els.status.textContent = '与宿主断开，正在重连…';
            next(2000);
        });
    }

    function refreshPeers() {
        loadConvs();
        api('/api/peers').then(function (list) {
            if (!list || !list.length) return;
            var names = list.map(function (p) { return p.name; });
            els.status.textContent = '在线 ' + names.length + ' 台：' + names.join('、');
        }).catch(function () {});
    }

    function sendText() {
        var raw = els.input.value;
        if (!raw.trim()) return;

        // 编辑态：更新已有消息而非发送新消息
        if (editingId) {
            var eid = editingId;
            editingId = null;
            els.input.value = '';
            autoGrow();
            api('/api/edit', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ conv: conv, id: eid, text: raw.trim() })
            }).then(function (r) {
                if (r && r.ok) updateBubble(eid, raw.trim()); else toast('编辑失败');
            }).catch(function () { toast('编辑失败'); });
            return;
        }

        var parsed = parseContent(raw);
        parsed.conv = conv;
        els.input.value = '';
        autoGrow();

        api('/api/send', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(parsed)
        }).then(function (sent) {
            // 立即本地回显，长轮询回放时会因 id 去重而不重复
            if (sent && sent.id && !renderedIds[sent.id]) {
                renderedIds[sent.id] = true;
                renderMessage(sent);
            }
        }).catch(function () {
            addSystemTip('发送失败，请检查与宿主的连接');
        });
    }

    function autoGrow() {
        els.input.style.height = 'auto';
        els.input.style.height = Math.min(els.input.scrollHeight, 120) + 'px';
    }

    /* ==================== 功能控制菜单 ==================== */
    // 圆环上已包含：音量加/减、上一曲/下一曲、播放暂停，这里只列其余功能。
    var CONTROL_COMMANDS = [
        '静音'
    ];

    function sendControl(cmd) {
        api('/api/send', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ kind: 'text', text: '(' + cmd + ')', conv: conv })
        }).catch(function () {});
    }

    function initControlMenu() {
        var btn = document.getElementById('btnControl');
        var menu = document.getElementById('controlMenu');
        var grid = document.getElementById('controlGrid');
        var closeBtn = document.getElementById('btnControlClose');
        if (!btn || !menu || !grid) return;

        // 圆环按钮：直接读取 data-cmd
        if (!menu.dataset.dialBound) {
            var dialBtns = menu.querySelectorAll('[data-cmd]');
            dialBtns.forEach(function (b) {
                b.addEventListener('click', function () {
                    sendControl(b.getAttribute('data-cmd'));
                });
            });
            menu.dataset.dialBound = '1';
        }

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

        btn.addEventListener('click', function () {
            var hidden = menu.classList.toggle('hidden');
            menu.setAttribute('aria-hidden', hidden ? 'true' : 'false');
        });
        if (closeBtn) {
            closeBtn.addEventListener('click', function () {
                menu.classList.add('hidden');
                menu.setAttribute('aria-hidden', 'true');
            });
        }
    }

    function insertTag(tag) {
        var m = TAG_RE.exec(els.input.value);
        var body = m ? (m[2] || '') : els.input.value;
        els.input.value = '[' + tag + ']' + body;
        els.input.focus();
        els.input.setSelectionRange(els.input.value.length, els.input.value.length);
        autoGrow();
    }

    /* ==================== 输入法适配 ==================== */

    /**
     * 手机浏览器唤起输入法时，用 visualViewport 计算被键盘遮挡的高度，
     * 通过 CSS 变量 --kb 把整个聊天视图上移，使输入框贴在键盘顶部。
     */
    function initKeyboardAdapt() {
        var vv = window.visualViewport;
        if (!vv) return;

        function apply() {
            var overlap = Math.max(0, window.innerHeight - vv.height - vv.offsetTop);
            document.documentElement.style.setProperty('--kb', overlap + 'px');
            document.body.classList.toggle('kb-open', overlap > 80);
            if (overlap > 80) scrollToBottom();
        }

        vv.addEventListener('resize', apply);
        vv.addEventListener('scroll', apply);
        apply();
    }

    /* ==================== 省电调度 ==================== */

    // 页面不可见（App 进后台 / 灭屏）时暂停一切轮询，回到前台立刻补一次。
    // WebView 里的 setInterval 不会因为 Activity 暂停而停止，
    // 后台仍然每几秒打一次本机 HTTP，是纯粹的耗电空转。
    var powerTasks = [];

    function scheduleAdaptive(fn, intervalMs) {
        var id = null;
        var task = {
            fn: fn,
            interval: intervalMs,
            stop: function () { if (id !== null) { clearInterval(id); id = null; } },
            start: function () {
                if (id !== null) return;
                id = setInterval(function () {
                    if (document.hidden) return;   // 隐藏时跳过这一次
                    try { fn(); } catch (e) { }
                }, task.interval);
            }
        };
        powerTasks.push(task);
        task.start();
        return task;
    }

    function onVisibilityChanged() {
        if (document.hidden) {
            powerTasks.forEach(function (t) { t.stop(); });
            polling = false;                      // 让 pollLoop 的链条自然断掉
        } else {
            powerTasks.forEach(function (t) { t.start(); });
            powerTasks.forEach(function (t) { try { t.fn(); } catch (e) { } });
            try { pollLoop(); } catch (e) { }
        }
    }

    /* ==================== 初始化 ==================== */

    /* ===================== 工具面板：文件 / 群组 / 频道 ===================== */

    var toolsRel = '';

    function toolsOpen() {
        var ov = document.getElementById('toolsOverlay');
        if (!ov) return;
        ov.classList.remove('hidden');
        switchToolsTab(currentToolsTab());
    }
    function toolsClose() {
        var ov = document.getElementById('toolsOverlay');
        if (ov) ov.classList.add('hidden');
    }
    function currentToolsTab() {
        var a = document.querySelector('#toolsTabs .lc-tools-tab.active');
        return a ? a.getAttribute('data-tab') : 'files';
    }
    function switchToolsTab(name) {
        document.querySelectorAll('#toolsTabs .lc-tools-tab').forEach(function (b) {
            b.classList.toggle('active', b.getAttribute('data-tab') === name);
        });
        document.querySelectorAll('#toolsOverlay .lc-tools-sec').forEach(function (s) {
            s.classList.toggle('active', s.getAttribute('data-sec') === name);
        });
        if (name === 'files') loadFiles(toolsRel);
        else if (name === 'groups') loadGroups();
        else if (name === 'channel') loadChannel();
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function loadFiles(rel) {
        toolsRel = rel || '';
        var box = document.getElementById('fileList');
        var pathEl = document.getElementById('filePath');
        if (!box) return;
        if (pathEl) pathEl.textContent = '/' + toolsRel;
        api('/api/share/list?path=' + encodeURIComponent(toolsRel)).then(function (d) {
            var items = (d && d.items) || [];
            getShareRootInfo().then(function (ri) {
                var notice = '';
                if (ri && ri.root && ri.preferredRoot && ri.root !== ri.preferredRoot) {
                    notice = '<div class="lc-tools-empty" style="color:#e6912f">当前共享目录是 ' + ri.root +
                        '（未使用 ' + ri.preferredRoot + '）。授予存储权限后会自动切换到该目录。</div>';
                }
                if (!items.length) { box.innerHTML = notice + '<div class="lc-tools-empty">（空目录）</div>'; return; }
                items.sort(function (a, b) {
                    if (a.dir !== b.dir) return a.dir ? -1 : 1;
                    return String(a.name).localeCompare(String(b.name));
                });
                box.innerHTML = notice;
                items.forEach(function (it) {
                    var row = document.createElement('div');
                    row.className = 'lc-tools-item';
                    var sizeText = it.dir ? '目录' : (function (n) {
                        n = Number(n) || 0;
                        if (n < 1024) return n + ' B';
                        if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
                        if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
                        return (n / 1073741824).toFixed(2) + ' GB';
                    })(it.size);
                    row.innerHTML = '<span class="li-name">' + (it.dir ? WI.svg('folder') : WI.svg('file')) + ' ' + esc(it.name) +
                                    '</span><span class="li-meta">' + sizeText + '</span>';
                    row.addEventListener('click', function () {
                        var childRel = toolsRel ? toolsRel + '/' + it.name : it.name;
                        if (it.dir) loadFiles(childRel);
                        else window.open(apiBase() + '/api/share/download?path=' + encodeURIComponent(childRel), '_blank');
                    });
                    box.appendChild(row);
                });
            });
        }).catch(function () { box.innerHTML = '<div class="lc-tools-empty">读取失败</div>'; });
    }

    var _rootInfoPromise = null;
    function getShareRootInfo() {
        if (_rootInfoPromise) return _rootInfoPromise;
        _rootInfoPromise = api('/api/share/rootinfo').catch(function () { return null; });
        return _rootInfoPromise;
    }

    function fileGoUp() {
        if (!toolsRel) return;
        var i = toolsRel.lastIndexOf('/');
        toolsRel = i >= 0 ? toolsRel.substring(0, i) : '';
        loadFiles(toolsRel);
    }

    function uploadShareFile(file) {
        if (!file) return;
        var reader = new FileReader();
        reader.onload = function () {
            var dataUrl = String(reader.result || '');
            var comma = dataUrl.indexOf(',');
            var bytes = atob(comma >= 0 ? dataUrl.substring(comma + 1) : dataUrl);
            var arr = new Uint8Array(bytes.length);
            for (var i = 0; i < bytes.length; i++) arr[i] = bytes.charCodeAt(i);
            var url = '/api/share/upload?path=' + encodeURIComponent(toolsRel) +
                      '&name=' + encodeURIComponent(file.name);
            fetch(apiBase() + url, { method: 'POST', body: arr })
                .then(function (r) { return r.json(); })
                .then(function () { toast('已上传：' + file.name); loadFiles(toolsRel); })
                .catch(function () { toast('上传失败'); });
        };
        reader.readAsDataURL(file);
    }

    function loadGroups() {
        var box = document.getElementById('groupList');
        if (!box) return;
        api('/api/qaul').then(function (d) {
            var groups = (d && d.groups) || [];
            if (!groups.length) { box.innerHTML = '<div class="lc-tools-empty">暂无群组</div>'; return; }
            box.innerHTML = '';
            groups.forEach(function (g) {
                var card = document.createElement('div');
                card.className = 'lc-tools-card';
                var head = document.createElement('div');
                head.className = 'lc-tools-card-head';
                head.innerHTML = '<b>' + esc(g.name || g.id) + '</b><span class="li-meta">' + (g.members || 0) + ' 人</span>';
                card.appendChild(head);

                var ml = document.createElement('div');
                ml.className = 'lc-tools-members';
                (g.memberList || []).forEach(function (m) {
                    var chip = document.createElement('button');
                    chip.className = 'lc-tools-member';
                    chip.textContent = (m.name || m.id) + (m.self ? '（我）' : '');
                    chip.addEventListener('click', function () {
                        if (!m.self) toast('成员：' + (m.name || m.id));
                    });
                    ml.appendChild(chip);
                });
                card.appendChild(ml);

                var acts = document.createElement('div');
                acts.className = 'lc-tools-row';
                var enter = document.createElement('button');
                enter.className = 'lc-tools-btn';
                enter.textContent = '进入群聊';
                enter.addEventListener('click', function () {
                    if (g.conv) { toolsClose(); switchConv(g.conv); }
                });
                acts.appendChild(enter);

                var leave = document.createElement('button');
                leave.className = 'lc-tools-btn warn';
                leave.textContent = '退群';
                leave.addEventListener('click', function () {
                    if (!confirm('退出群组「' + (g.name || g.id) + '」？')) return;
                    fetch(apiBase() + '/api/qaul/leave?group=' + encodeURIComponent(g.id), { method: 'POST' })
                        .then(function () { toast('已退群'); loadGroups(); })
                        .catch(function () { toast('退群失败'); });
                });
                acts.appendChild(leave);
                card.appendChild(acts);
                box.appendChild(card);
            });
        }).catch(function () { box.innerHTML = '<div class="lc-tools-empty">读取失败</div>'; });
    }

    function createGroup() {
        var input = document.getElementById('groupName');
        var name = input ? input.value.trim() : '';
        if (!name) { toast('请输入群组名称'); return; }
        fetch(apiBase() + '/api/qaul/group?name=' + encodeURIComponent(name), { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (res) {
                if (input) input.value = '';
                toast('群组已创建');
                loadGroups();
            })
            .catch(function () { toast('创建失败（需 qaul 在线）'); });
    }

    function loadChannel() {
        var box = document.getElementById('channelStatus');
        var dualEl = document.getElementById('dualState');
        api('/api/channel').then(function (st) {
            if (!st) return;
            if (dualEl) dualEl.textContent = st.dual ? '已开启' : '已关闭';
            if (!box) return;
            var rows = [
                ['qaul 端口', st.qaulPort || '-'],
                ['qaul 运行', st.qaulRunning ? '是' : '否'],
                ['qaul 收/发', (st.qaulRx || 0) + ' / ' + (st.qaulTx || 0)],
                ['局域网端口', st.lanPort || '-'],
                ['qaul 节点', st.nodeRunning ? '在线' : '离线'],
                ['邻居数', st.neighbours || 0]
            ];
            var html = '';
            rows.forEach(function (r) {
                html += '<div class="lc-tools-kv"><span>' + esc(r[0]) + '</span><b>' + esc(r[1]) + '</b></div>';
            });
            var links = st.links || [];
            if (links.length) {
                html += '<div class="lc-tools-sub">链路</div>';
                links.forEach(function (l) {
                    var label = l.name || l.link || l.id || '链路';
                    var txt = (l.enabled === false ? '关闭' : (l.running ? '运行' : '待机')) +
                              ' · 收' + (l.rx || 0) + ' 发' + (l.tx || 0);
                    html += '<div class="lc-tools-kv"><span>' + esc(label) + '</span><b>' + esc(txt) + '</b></div>';
                });
            }
            box.innerHTML = html;
        }).catch(function () { if (box) box.innerHTML = '<div class="lc-tools-empty">频道未就绪</div>'; });
    }

    function toggleDual() {
        api('/api/channel').then(function (st) {
            var next = !(st && st.dual);
            fetch(apiBase() + '/api/channel?dual=' + (next ? '1' : '0'), { method: 'POST' })
                .then(function () { toast(next ? '双通道已开启' : '双通道已关闭'); loadChannel(); })
                .catch(function () {});
        }).catch(function () {});
    }

    function setThemeIcon(btn, t) {
        try { btn.innerHTML = (window.WI ? window.WI.svg(t === 'dark' ? 'sun' : 'moon') : (t === 'dark' ? '☀' : '☾')); } catch (e) {}
    }

    function initTools() {
        var btn = document.getElementById('btnTools');
        if (btn) btn.addEventListener('click', toolsOpen);
        var close = document.getElementById('toolsClose');
        if (close) close.addEventListener('click', toolsClose);
        var ov = document.getElementById('toolsOverlay');
        if (ov) ov.addEventListener('click', function (e) { if (e.target === ov) toolsClose(); });
        var tabs = document.getElementById('toolsTabs');
        if (tabs) tabs.addEventListener('click', function (e) {
            var t = e.target.closest ? e.target.closest('.lc-tools-tab') : null;
            if (t) switchToolsTab(t.getAttribute('data-tab'));
        });
        var up = document.getElementById('fileUp'); if (up) up.addEventListener('click', fileGoUp);
        var fu = document.getElementById('fileUpload');
        if (fu) fu.addEventListener('change', function () { if (fu.files[0]) uploadShareFile(fu.files[0]); fu.value = ''; });
        var gc = document.getElementById('groupCreate'); if (gc) gc.addEventListener('click', createGroup);
        var dt = document.getElementById('dualToggle'); if (dt) dt.addEventListener('click', toggleDual);
    }

    function init() {
        els.list = document.getElementById('messageList');
        els.input = document.getElementById('msgInput');
        els.send = document.getElementById('btnSend');
        els.attach = document.getElementById('btnAttach');
        els.attachBar = document.getElementById('attachBar');
        els.status = document.getElementById('peerStatus');
        els.guestTag = document.getElementById('guestTag');
        var btnIdentity = document.getElementById('btnIdentity');
        if (btnIdentity) btnIdentity.addEventListener('click', openIdentityDialog);
        var btnQaul = document.getElementById('btnQaul');
        if (btnQaul) btnQaul.addEventListener('click', openQaulDialog);

        // 日间 / 夜间主题切换（与 app 端统一）
        var btnTheme = document.getElementById('btnTheme');
        if (btnTheme) {
            btnTheme.addEventListener('click', function () {
                var t = window.MTheme ? window.MTheme.toggle() : 'light';
                setThemeIcon(btnTheme, t);
            });
            setThemeIcon(btnTheme, window.MTheme ? window.MTheme.resolve() : 'light');
        }
        // 左上角菜单：网页端没有侧栏，点开后等价于「工具」面板
        var btnMenu = document.getElementById('btnMenu');
        if (btnMenu) btnMenu.addEventListener('click', toolsOpen);
        initTools();
        refreshQaul();
        scheduleAdaptive(refreshQaul, 20000);   // 原 8s：qaul 状态变化慢，20s 足够
        els.convChips = document.getElementById('convChips');

        Player.init();
        initKeyboardAdapt();

        els.send.addEventListener('click', sendText);
        els.input.addEventListener('input', autoGrow);
        els.input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendText(); }
        });
        els.input.addEventListener('focus', function () { setTimeout(scrollToBottom, 250); });

        els.attach.addEventListener('click', function () {
            els.attachBar.classList.toggle('hidden');
        });
        initControlMenu();
        els.attachBar.querySelectorAll('.attach-item').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var act = btn.getAttribute('data-act');
                if (act === 'voice') {
                    if (!Voice.supported()) { toast('当前环境不支持录音'); return; }
                    if (btn.classList.contains('recording')) {
                        btn.classList.remove('recording');
                        var lbl = btn.querySelector('span:last-child'); if (lbl) lbl.textContent = '语音';
                        Voice.stop();
                    } else {
                        btn.classList.add('recording');
                        els.attachBar.classList.add('hidden');
                        Voice.start(function (sec) {
                            var lbl = btn.querySelector('span:last-child');
                            if (lbl) lbl.textContent = sec + '″';
                        });
                    }
                    return;
                }
                insertTag(act);
                els.attachBar.classList.add('hidden');
            });
        });

        // 浏览器返回键优先关闭播放器覆盖层
        window.addEventListener('popstate', function () {
            if (Player.closeTop()) history.pushState(null, '', location.href);
        });
        history.pushState(null, '', location.href);

        loadSession()
            .catch(function () { addSystemTip('无法连接宿主 app，请确认它正在运行'); })
            .then(function () {
                loadConvs().then(function () {
                    pollLoop();
                    refreshPeers();
                    scheduleAdaptive(refreshPeers, 15000);   // 原 5s：在线列表不需要这么快

                    // 注册可见性监听：进后台停轮询，回前台补一轮
                    document.addEventListener('visibilitychange', onVisibilityChanged);
                });
            });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

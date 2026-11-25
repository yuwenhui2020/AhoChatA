/**
 * 媒体模块：
 *  - 纯前端解析音频文件内嵌元数据（ID3v2 / FLAC Vorbis Comment）：
 *    封面图、歌曲名、艺术家、内嵌歌词（USLT / LYRICS 字段）。
 *    不依赖任何第三方库与 CDN，全部本地实现。
 *  - 内置视频播放器、图片查看器、音乐播放器（封面 + 歌词 + 进度条）。
 */
window.Media = (function () {
    'use strict';

    /* ==================== 通用工具 ==================== */

    // 元数据（ID3 头 / FLAC 元数据块）都位于文件开头，因此只取前若干字节即可，
    // 避免为了读封面而把整首几十 MB 的歌曲下载进内存。
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

    /* ==================== ID3v2 解析（MP3） ==================== */

    function parseId3(buf) {
        var view = new DataView(buf);
        var u8 = new Uint8Array(buf);
        if (u8.length < 10) return null;
        if (u8[0] !== 0x49 || u8[1] !== 0x44 || u8[2] !== 0x33) return null;  // 'ID3'

        var major = u8[3];
        var flags = u8[5];
        // synchsafe integer
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
                    // 同步歌词帧：自带精确时间戳，优先于 USLT 使用
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

    // USLT: encoding(1) + language(3) + descriptor(以 null 结尾) + lyrics
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
     * 统一转成 LRC 文本，交给 parseLyrics 走同一条渲染路径。
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

    /* ==================== FLAC 解析 ==================== */

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

    /* ==================== 元数据入口 ==================== */

    var metaCache = {};

    /** 读取音频文件元数据（带缓存）。失败时返回空元数据，不抛异常。 */
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

    /** 解析 LRC 歌词为 [{time, text}]，无时间戳则按纯文本行返回 time=-1。 */
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
            for (var j = 0; j < matches.length; j++) {
                var mm = matches[j];
                var frac = mm[3] ? parseInt(mm[3], 10) / Math.pow(10, mm[3].length) : 0;
                out.push({ time: parseInt(mm[1], 10) * 60 + parseInt(mm[2], 10) + frac, text: text });
            }
        }
        out.sort(function (a, b) { return a.time - b.time; });
        return out;
    }

    function formatTime(sec) {
        if (!isFinite(sec) || sec < 0) sec = 0;
        var m = Math.floor(sec / 60), s = Math.floor(sec % 60);
        return (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    }

    /* ==================== 播放器 UI ==================== */

    var els = {};
    var audioState = { lyrics: [], activeIndex: -1 };
    // 当前曲目信息，用于同步到常驻通知与系统媒体中心
    var nowPlaying = { title: '', artist: '', active: false };

    // 播放列表：聊天中的音乐消息在渲染时会注册进来，
    // 供跨设备远程控制的「上一曲 / 下一曲」按列表顺序切换。
    var playlist = [];
    var playIndex = -1;

    /** 把曲目登记进播放列表（按 url 去重）。 */
    function registerTrack(url, name) {
        if (!url) return;
        for (var i = 0; i < playlist.length; i++) {
            if (playlist[i].url === url) {
                if (name) playlist[i].name = name;
                return;
            }
        }
        playlist.push({ url: url, name: name || '' });
    }

    function indexOfTrack(url) {
        for (var i = 0; i < playlist.length; i++) {
            if (playlist[i].url === url) return i;
        }
        return -1;
    }

    /**
     * 把播放状态同步给原生：常驻前台服务据此刷新通知栏并保持进程存活，
     * 从而实现「切到后台 / 锁屏后音乐继续播放」。
     * @param {boolean} playing 是否正在播放
     * @param {boolean} [release] 是否结束播放会话（关闭播放器 / 停止）
     */
    function syncPlayback(playing, release) {
        if (release) nowPlaying.active = false;

        window.Bridge.updatePlayback(
            nowPlaying.title, nowPlaying.artist, playing, nowPlaying.active);

        if ('mediaSession' in navigator) {
            try {
                navigator.mediaSession.playbackState =
                    nowPlaying.active ? (playing ? 'playing' : 'paused') : 'none';
            } catch (e) {}
        }

        // 跨设备媒体同步：每次本机播放状态变化（播放/暂停/停止/换曲）都主动
        // 广播一次当前状态。remote-media.js 内部会节流为 ≥10 秒一次，并在
        // 停止/不活跃时发送 playing:false 让对端收起通知栏播放器。
        if (window.RemoteMedia && window.RemoteMedia.publish) {
            try { window.RemoteMedia.publish(); } catch (e) {}
        }
    }

    /** 更新系统媒体中心展示的曲目信息（锁屏封面/歌名） */
    function updateMediaSessionMeta(meta) {
        nowPlaying.title = meta.title || '未知歌曲';
        nowPlaying.artist = meta.artist || '';

        if ('mediaSession' in navigator && window.MediaMetadata) {
            try {
                navigator.mediaSession.metadata = new MediaMetadata({
                    title: nowPlaying.title,
                    artist: nowPlaying.artist,
                    album: meta.album || '',
                    artwork: meta.cover ? [{ src: meta.cover }] : []
                });
            } catch (e) {}
        }
        syncPlayback(!els.audio.paused);
    }

    function initPlayers() {
        els.videoOverlay = document.getElementById('videoPlayer');
        els.video = document.getElementById('videoEl');
        els.videoTitle = document.getElementById('videoTitle');

        els.imageOverlay = document.getElementById('imageViewer');
        els.image = document.getElementById('imageEl');

        els.musicOverlay = document.getElementById('musicPlayer');
        els.audio = document.getElementById('audioEl');
        els.cover = document.getElementById('musicCover');
        els.name = document.getElementById('musicName');
        els.artist = document.getElementById('musicArtist');
        els.lyricBox = document.getElementById('lyricBox');
        els.cur = document.getElementById('musicCur');
        els.dur = document.getElementById('musicDur');
        els.bar = document.getElementById('musicBar');
        els.fill = document.getElementById('musicFill');
        els.toggle = document.getElementById('musicToggle');

        // 关闭按钮
        document.querySelectorAll('[data-close]').forEach(function (btn) {
            btn.addEventListener('click', function () { closeOverlay(btn.getAttribute('data-close')); });
        });

        els.toggle.addEventListener('click', function () {
            if (els.audio.paused) els.audio.play(); else els.audio.pause();
        });
        els.audio.addEventListener('play', function () {
            els.toggle.innerHTML = WI.svg('pause');
            syncPlayback(true);
        });
        els.audio.addEventListener('pause', function () {
            els.toggle.innerHTML = WI.svg('play');
            syncPlayback(false);
        });
        els.audio.addEventListener('ended', function () { syncPlayback(false); });

        // 通知栏播放控制回调（由原生 ChatForegroundService 转发）
        window.__mediaCommand = function (cmd) {
            if (cmd === 'toggle') {
                if (els.audio.paused) els.audio.play().catch(function () {});
                else els.audio.pause();
            } else if (cmd === 'stop') {
                els.audio.pause();
                els.audio.currentTime = 0;
                syncPlayback(false, true);
            }
        };

        // ============ 功能控制（由原生 ControlExecutor 经 (命令) 消息触发） ============
        // 播放 / 暂停 / 重播(上一曲) / 停止(下一曲) / 全屏放映
        window.__mediaPlay = function () {
            if (els.audio.paused) els.audio.play().catch(function () {});
        };
        window.__mediaPause = function () {
            if (!els.audio.paused) els.audio.pause();
        };
        window.__mediaRestart = function () {   // 上一曲：重播当前曲目
            try { els.audio.currentTime = 0; els.audio.play().catch(function () {}); } catch (e) {}
        };
        window.__mediaStop = function () {       // 下一曲：停止当前曲目
            try { els.audio.pause(); els.audio.currentTime = 0; syncPlayback(false, true); } catch (e) {}
        };
        window.__mediaFullscreen = function () { // 全屏放映：把音乐播放器全屏
            var node = els.musicOverlay;
            if (!node) return;
            try {
                if (!document.fullscreenElement) {
                    if (node.requestFullscreen) node.requestFullscreen();
                    else if (node.webkitRequestFullscreen) node.webkitRequestFullscreen();
                } else {
                    if (document.exitFullscreen) document.exitFullscreen();
                    else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
                }
            } catch (e) {}
        };

        // 系统媒体控制中心（锁屏 / 耳机按键）
        if ('mediaSession' in navigator) {
            try {
                navigator.mediaSession.setActionHandler('play', function () {
                    els.audio.play().catch(function () {});
                });
                navigator.mediaSession.setActionHandler('pause', function () { els.audio.pause(); });
                navigator.mediaSession.setActionHandler('stop', function () {
                    els.audio.pause();
                    syncPlayback(false, true);
                });
            } catch (e) {}
        }
        els.audio.addEventListener('loadedmetadata', function () {
            els.dur.textContent = formatTime(els.audio.duration);
        });
        els.audio.addEventListener('timeupdate', onAudioTime);
        // 拖动进度后立即把歌词跳到对应位置，不必等下一次 timeupdate
        els.audio.addEventListener('seeked', onAudioTime);

        // 进度条拖动/点击跳转
        var seeking = false;
        function seekAt(clientX) {
            var rect = els.bar.getBoundingClientRect();
            var ratio = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
            if (isFinite(els.audio.duration)) els.audio.currentTime = ratio * els.audio.duration;
            els.fill.style.width = (ratio * 100) + '%';
        }
        els.bar.addEventListener('touchstart', function (e) { seeking = true; seekAt(e.touches[0].clientX); }, { passive: true });
        els.bar.addEventListener('touchmove', function (e) { if (seeking) seekAt(e.touches[0].clientX); }, { passive: true });
        els.bar.addEventListener('touchend', function () { seeking = false; });
        els.bar.addEventListener('click', function (e) { seekAt(e.clientX); });
    }

    function onAudioTime() {
        var a = els.audio;
        els.cur.textContent = formatTime(a.currentTime);
        var ratio = (isFinite(a.duration) && a.duration > 0) ? a.currentTime / a.duration : 0;
        els.fill.style.width = (ratio * 100) + '%';

        var lyrics = audioState.lyrics;
        if (!lyrics.length || lyrics[0].time < 0) return;

        var idx = -1;
        for (var i = 0; i < lyrics.length; i++) {
            if (lyrics[i].time <= a.currentTime) idx = i; else break;
        }
        if (idx === audioState.activeIndex) return;
        audioState.activeIndex = idx;

        var nodes = els.lyricBox.children;
        for (var j = 0; j < nodes.length; j++) nodes[j].classList.toggle('active', j === idx);
        if (idx >= 0 && nodes[idx]) {
            els.lyricBox.scrollTo({
                top: nodes[idx].offsetTop - els.lyricBox.clientHeight / 2 + nodes[idx].clientHeight / 2,
                behavior: 'smooth'
            });
        }
    }

    function renderLyrics(list) {
        els.lyricBox.innerHTML = '';
        audioState.lyrics = list;
        audioState.activeIndex = -1;

        if (!list.length) {
            var tip = document.createElement('div');
            tip.className = 'lyric-empty';
            tip.textContent = '暂无内嵌歌词';
            els.lyricBox.appendChild(tip);
            return;
        }
        for (var i = 0; i < list.length; i++) {
            var d = document.createElement('div');
            d.className = 'lyric-line';
            d.textContent = list[i].text;
            els.lyricBox.appendChild(d);
        }
    }

    function openOverlay(id) {
        document.getElementById(id).classList.add('show');
        window.App && window.App.pushOverlay(id);
    }

    function closeOverlay(id) {
        var node = document.getElementById(id);
        if (!node || !node.classList.contains('show')) return;
        node.classList.remove('show');
        if (id === 'videoPlayer') { els.video.pause(); els.video.removeAttribute('src'); els.video.load(); }
        // 音乐播放器只收起界面、不停止播放：关闭后仍在后台继续放，
        // 由常驻通知栏按钮或再次点击音乐卡片控制。
        window.App && window.App.popOverlay(id);
    }

    /* ==================== 公开接口 ==================== */

    return {
        init: initPlayers,
        readAudioMeta: readAudioMeta,
        parseLyrics: parseLyrics,
        formatTime: formatTime,
        closeOverlay: closeOverlay,

        /** 打开内置视频播放器 */
        playVideo: function (url, title) {
            els.videoTitle.textContent = title || '';
            els.video.src = url;
            openOverlay('videoPlayer');
            els.video.play().catch(function () {});
        },

        /** 打开图片查看器 */
        viewImage: function (url) {
            els.image.src = url;
            openOverlay('imageViewer');
        },

        /** 打开音乐播放器：加载封面/歌名/内嵌歌词并播放 */
        playMusic: function (url, fallbackName) {
            els.name.textContent = fallbackName || '加载中…';
            els.artist.textContent = '';
            els.cover.removeAttribute('src');
            renderLyrics([]);
            openOverlay('musicPlayer');

            // 标记播放会话激活：此后即使关闭播放器界面/切到后台也继续播放
            nowPlaying.active = true;
            nowPlaying.title = fallbackName || '正在播放';
            nowPlaying.artist = '';

            // 登记到播放列表并定位索引，供远程控制的「上一曲/下一曲」切换
            registerTrack(url, fallbackName);
            playIndex = indexOfTrack(url);

            els.audio.src = url;
            els.audio.play().catch(function () {});

            readAudioMeta(url, fallbackName).then(function (meta) {
                els.name.textContent = meta.title;
                els.artist.textContent = meta.album ? (meta.artist + ' · ' + meta.album) : meta.artist;
                if (meta.cover) els.cover.src = meta.cover;
                renderLyrics(parseLyrics(meta.lyrics));
                // 歌词是异步加载的，加载完立刻按当前播放位置高亮，避免首行不亮
                onAudioTime();
                updateMediaSessionMeta(meta);
            });
        },

        /** 有覆盖层处于打开状态时返回其 id，否则返回 null */
        topOverlay: function () {
            var ids = ['musicPlayer', 'videoPlayer', 'imageViewer'];
            for (var i = 0; i < ids.length; i++) {
                if (document.getElementById(ids[i]).classList.contains('show')) return ids[i];
            }
            return null;
        },

        /** 登记曲目到播放列表（聊天中的音乐消息渲染时调用）。 */
        registerTrack: function (url, name) {
            registerTrack(url, name);
        },

        /**
         * 当前播放状态快照，供跨设备同步模块广播给其它设备。
         * @returns {{title:string,artist:string,album:string,duration:number,
         *            position:number,playing:boolean,active:boolean}}
         */
        getPlayState: function () {
            var a = els.audio;
            return {
                title: nowPlaying.title || '',
                artist: nowPlaying.artist || '',
                album: '',
                duration: (a && isFinite(a.duration)) ? Math.round(a.duration * 1000) : 0,
                position: (a && a.currentTime) ? Math.round(a.currentTime * 1000) : 0,
                playing: !!(a && !a.paused && nowPlaying.active),
                active: !!nowPlaying.active,
                // 封面地址：跨设备同步时由 remote-media.js 在换曲时取一次并转为 base64
                coverUrl: (els.cover && els.cover.getAttribute('src')) || ''
            };
        },

        /**
         * 远程控制入口：执行来自其它设备的播放指令。
         * @param {string} cmd  play | pause | next | prev | stop | seek
         * @param {number} [position] seek 的目标位置（毫秒）
         */
        remoteControl: function (cmd, position) {
            var a = els.audio;
            if (!a) return;
            switch (cmd) {
                case 'play':
                    a.play().catch(function () {});
                    break;
                case 'pause':
                    a.pause();
                    break;
                case 'stop':
                    try { a.pause(); a.currentTime = 0; } catch (e) {}
                    syncPlayback(false, true);
                    break;
                case 'seek':
                    if (typeof position === 'number' && position >= 0) {
                        try { a.currentTime = position / 1000; } catch (e) {}
                    }
                    // Seek 后立即广播新位置，让聊天卡片/受控端快速校准
                    if (window.RemoteMedia && window.RemoteMedia.publish) {
                        setTimeout(function () {
                            var s = window.Media.getPlayState();
                            if (s && s.active) window.RemoteMedia.publish(s, true);
                        }, 50);
                    }
                    break;
                case 'next':
                case 'prev': {
                    // 按聊天中出现的音乐消息顺序切换；列表为空时忽略
                    if (!playlist.length) break;
                    var delta = (cmd === 'next') ? 1 : -1;
                    var idx = (playIndex < 0)
                        ? 0
                        : (playIndex + delta + playlist.length) % playlist.length;
                    var t = playlist[idx];
                    if (t) this.playMusic(t.url, t.name);
                    break;
                }
                case 'volume_up':
                case 'volume_down': {
                    // 远端通知栏音量按钮：调整本机（播放端 A）的实际音量，
                    // 随后重新广播状态，使 B 端「音量已调整」的反馈与 A 真实音量保持一致。
                    try {
                        var step = (cmd === 'volume_up') ? 0.1 : -0.1;
                        var v = Math.min(1, Math.max(0, (isFinite(a.volume) ? a.volume : 1) + step));
                        a.volume = v;
                        if (window.RemoteMedia && window.RemoteMedia.publish) window.RemoteMedia.publish();
                    } catch (e) {}
                    break;
                }
            }
        }
    };
})();

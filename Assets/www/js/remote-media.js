/**
 * 跨设备媒体同步（RemoteMedia）。
 *
 * 场景：同一局域网内多台设备都装了本应用。
 *
 *   播放端 A：用本应用放音乐时，周期性广播播放状态
 *             （歌名 / 艺术家 / 总时长 / 当前进度 / 是否播放）。
 *   受控端 B：收到广播后在本机建立「虚拟播放」——占用播放通道
 *             （MediaSession + 音频焦点，令其它音乐 App 让路）并在通知栏
 *             常驻一个播放器，含上一曲 / 播放暂停 / 下一曲 与可拖动的进度条，
 *             但 B 自身并不出声。
 *
 * B 在通知栏的每次操作都不作用于本机，而是回调到 window.RemoteMedia.onCommand，
 * 由本模块把指令发回给 A，由 A 真正执行；进度条拖动同理（seek）。
 *
 * 消息走 Mchat 的独立 kind：
 *   mediaplayer   —— A 广播播放状态
 *   mediacontrol  —— B 回传控制指令（带 target，仅目标设备响应）
 * 二者由原生直接以同名事件交给前端，不进聊天历史、不渲染气泡。
 */
window.RemoteMedia = (function () {
    'use strict';

    // 周期广播间隔：10 秒。曲目切换或播放/暂停会立即发一次，
    // 保证响应及时；其余时刻按此间隔心跳，避免频繁广播造成过载。
    var BROADCAST_INTERVAL_MS = 10000;
    // 受控端等待超时：超过该时长没再收到播放端广播，认为对方已停止播放并撤销占用
    var REMOTE_TIMEOUT_MS = 30000;
    // 本地轮询间隔（驱动广播与超时清理）
    var TICK_MS = 2000;

    var selfId = null;
    var started = false;

    // 播放端节流用
    var lastSentAt = 0;
    var lastSignature = '';
    var lastCoverTitle = '';
    var wasActive = false;

    // 受控端跟踪的播放端
    var sourceId = '';
    var lastSeenAt = 0;

    function ensureSelfId() {
        if (selfId) return selfId;
        try {
            var info = window.Bridge && window.Bridge.getSelfInfo ? window.Bridge.getSelfInfo() : null;
            if (info && info.peerId) selfId = info.peerId;
        } catch (e) {}
        return selfId;
    }

    /** 把封面 URL 转成 base64（仅曲目切换时调用一次，避免频繁读取大图）。 */
    function fetchCoverBase64(url) {
        if (!url) return Promise.resolve('');
        // dataURL 直接取逗号后部分；同源 /__media/ 或 http(s) 地址则需下载
        if (url.indexOf('data:') === 0) {
            var comma = url.indexOf(',');
            return Promise.resolve(comma > 0 ? url.slice(comma + 1) : '');
        }
        return fetch(url)
            .then(function (r) { return r.blob(); })
            .then(function (blob) {
                return new Promise(function (resolve) {
                    try {
                        var fr = new FileReader();
                        fr.onloadend = function () {
                            var s = String(fr.result || '');
                            var i = s.indexOf(',');
                            resolve(i > 0 ? s.slice(i + 1) : '');
                        };
                        fr.onerror = function () { resolve(''); };
                        fr.readAsDataURL(blob);
                    } catch (e) { resolve(''); }
                });
            })
            .catch(function () { return ''; });
    }

    /**
     * 广播播放状态。
     * @param {Object} st  Media.getPlayState() 的快照
     * @param {boolean} [force] 是否跳过节流立即发送（响应控制指令时用）
     */
    function publish(st, force) {
        if (!st || !window.Bridge || !window.Bridge.send) return;

        var sig = (st.title || '') + '|' + (!!st.playing) + '|' + (st.duration || 0);
        var now = Date.now();
        var changed = (sig !== lastSignature);

        // 状态未变且未到心跳间隔 -> 节流丢弃（防止过载）
        if (!force && !changed && (now - lastSentAt) < BROADCAST_INTERVAL_MS) return;

        lastSignature = sig;
        lastSentAt = now;

        var payload = {
            id: 'mp_' + now + '_' + Math.random().toString(36).slice(2, 7),
            kind: 'mediaplayer',
            text: '',
            state: {
                title: st.title || '',
                artist: st.artist || '',
                album: st.album || '',
                duration: st.duration || 0,
                position: st.position || 0,
                playing: !!st.playing
            }
        };

        // 封面只在换曲时附带，避免每 10 秒重传一张大图
        var needCover = changed && (st.title || '') !== lastCoverTitle;
        if (needCover && st.coverUrl) {
            lastCoverTitle = st.title || '';
            fetchCoverBase64(st.coverUrl).then(function (b64) {
                // 封面过大时放弃传输：单帧过大会拖慢局域网同步，
                // 没有封面也并不影响通知栏的播放与进度控制。
                if (b64 && b64.length <= 512 * 1024) payload.state.cover = b64;
                window.Bridge.send(payload);
            });
        } else {
            window.Bridge.send(payload);
        }
    }

    /** 本机作为播放端：驱动状态广播。 */
    function tickAsSource() {
        var st = window.Media && window.Media.getPlayState ? window.Media.getPlayState() : null;
        if (!st || !st.active) {
            // 播放会话结束：补发一次“已停止”，让受控端立刻撤销占用
            if (wasActive) {
                wasActive = false;
                lastSignature = '';
                lastCoverTitle = '';
                publish({ title: st ? st.title : '', artist: '', duration: 0, position: 0, playing: false }, true);
            }
            return;
        }
        wasActive = true;
        publish(st, false);
    }

    /** 本机作为受控端：清理超时未更新的远端占用。 */
    function tickAsReceiver() {
        if (!sourceId) return;
        if (Date.now() - lastSeenAt <= REMOTE_TIMEOUT_MS) return;
        try { window.Bridge.hideRemoteMedia(); } catch (e) {}
        sourceId = '';
    }

    function tick() {
        ensureSelfId();
        tickAsSource();
        tickAsReceiver();
    }

    /** 收到播放端广播：在本机建立/刷新虚拟播放占用。 */
    function onRemoteState(msg) {
        if (!msg) return;
        ensureSelfId();
        // 忽略自己发出的广播
        if (selfId && msg.from === selfId) return;

        // 本机正在播放时不接管远端，避免两套播放互相打断
        var local = window.Media && window.Media.getPlayState ? window.Media.getPlayState() : null;
        if (local && local.active && local.playing) return;

        var st = msg.state || {};
        // 对方已停止播放：撤销占用
        if (!st.playing && !st.title) {
            if (sourceId && sourceId === msg.from) {
                try { window.Bridge.hideRemoteMedia(); } catch (e) {}
                sourceId = '';
            }
            return;
        }

        sourceId = msg.from || '';
        lastSeenAt = Date.now();

        try {
            window.Bridge.showRemoteMedia({
                title: st.title || '未知歌曲',
                artist: st.artist || '',
                album: st.album || '',
                duration: st.duration || 0,
                position: st.position || 0,
                playing: !!st.playing,
                sourceId: msg.from || '',
                sourceName: msg.fromName || '',
                cover: st.cover || ''
            });
        } catch (e) {}
    }

    /** 收到控制指令：仅当 target 指向本机时才执行。 */
    function onRemoteControl(msg) {
        if (!msg) return;
        ensureSelfId();
        var st = msg.state || {};
        if (st.target && selfId && st.target !== selfId) return;   // 不是发给我的
        if (!window.Media || !window.Media.remoteControl) return;

        window.Media.remoteControl(st.cmd, st.position);

        // 执行后立刻广播一次新状态，让受控端（含发起方）快速校准
        setTimeout(function () {
            var s = window.Media.getPlayState();
            if (s && s.active) publish(s, true);
        }, 150);
    }

    /**
     * 原生回调入口：受控端通知栏的上一曲 / 下一曲 / 暂停 / 进度拖动都会走到这里。
     * 这里不操作本机播放器，而是把指令发回给真正的播放端。
     * @param {string} cmd  play | pause | next | prev | stop | seek
     * @param {number} position seek 的目标位置（毫秒），非 seek 时为 -1
     */
    function onCommand(cmd, position) {
        if (!sourceId) return;
        try {
            window.Bridge.send({
                id: 'mc_' + Date.now() + '_' + Math.random().toString(36).slice(2, 7),
                kind: 'mediacontrol',
                text: '',
                state: {
                    cmd: cmd,
                    position: (typeof position === 'number' ? position : -1),
                    target: sourceId
                }
            });
        } catch (e) {}
    }

    // 位置更新回调列表：聊天卡片可订阅实时位置更新
    var positionCallbacks = [];

    function notifyPositionUpdate(position, playing, duration) {
        positionCallbacks.forEach(function (cb) {
            try { cb(position, playing, duration); } catch (e) {}
        });
    }

    // 监听播放端广播的位置更新，分发给订阅者
    var originalOnRemoteState = onRemoteState;
    onRemoteState = function (msg) {
        originalOnRemoteState(msg);
        if (!msg || !msg.state) return;
        var st = msg.state || {};
        if (st.position !== undefined) {
            notifyPositionUpdate(st.position, !!st.playing, st.duration || 0);
        }
    };

    function init() {
        if (started) return;
        started = true;

        if (!window.Bridge) return;

        window.Bridge.on('ready', function (info) {
            if (info && info.peerId) selfId = info.peerId;
            ensureSelfId();
        });
        window.Bridge.on('mediaplayer', onRemoteState);
        window.Bridge.on('mediacontrol', onRemoteControl);

        setInterval(tick, TICK_MS);
    }

    // 位置更新订阅：聊天卡片可调用此方法订阅实时位置
    function subscribePosition(callback) {
        positionCallbacks.push(callback);
        // 返回取消订阅函数
        return function () {
            var idx = positionCallbacks.indexOf(callback);
            if (idx >= 0) positionCallbacks.splice(idx, 1);
        };
    }

    // 立即初始化：onCommand 需要尽早可用，以便原生随时回调
    init();

    return {
        init: init,
        onCommand: onCommand,
        /** 主动撤销受控端的虚拟播放占用（例如退出聊天界面时）。 */
        releaseRemote: function () {
            try { window.Bridge.hideRemoteMedia(); } catch (e) {}
            sourceId = '';
        },
        /** 当前是否正在被远端播放占用（供调试/界面提示）。 */
        isRemoteActive: function () { return !!sourceId; }
    };
})();

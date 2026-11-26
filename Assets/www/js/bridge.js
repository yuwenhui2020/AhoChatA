/**
 * 原生桥接层。
 *
 * 与 BLEWEBAPP 的 Web Bluetooth polyfill 采用同一范式：
 * 原生通过 addJavascriptInterface 注入对象，网页只调用同步方法拿字符串，
 * 异步事件由原生 evaluateJavascript 回调 window.__lanEvent 推送。
 *
 * 若在桌面浏览器中打开（无原生对象），自动降级为本地回环模拟，
 * 便于调试页面而不至于报错。
 */
window.Bridge = (function () {
    'use strict';

    var listeners = {};
    var hasLan = typeof AndroidMchat !== 'undefined';
    var hasNotifier = typeof AndroidNotifier !== 'undefined';
    var hasHost = typeof AndroidHost !== 'undefined';
    var hasMedia = typeof AndroidMedia !== 'undefined';

    function on(type, fn) {
        (listeners[type] || (listeners[type] = [])).push(fn);
    }

    function emit(type, data) {
        var arr = listeners[type];
        if (!arr) return;
        for (var i = 0; i < arr.length; i++) {
            try { arr[i](data); } catch (e) { console.error('[bridge]', type, e); }
        }
    }

    // 原生事件入口
    window.__lanEvent = function (type, payloadJson) {
        var data = null;
        try { data = payloadJson ? JSON.parse(payloadJson) : null; } catch (e) {}
        emit(type, data);
    };

    function parse(json, fallback) {
        try { return json ? JSON.parse(json) : fallback; } catch (e) { return fallback; }
    }

    return {
        available: hasLan,

        on: on,

        start: function () {
            if (hasLan) AndroidMchat.start();
            else setTimeout(function () { emit('ready', { peerId: 'web-debug', name: '调试端' }); }, 0);
        },

        getSelfInfo: function () {
            if (!hasLan) return { peerId: 'web-debug', name: '调试端', port: 0 };
            return parse(AndroidMchat.getSelfInfo(), {});
        },

        getPeers: function () {
            if (!hasLan) return [];
            return parse(AndroidMchat.getPeers(), []);
        },

        // 当前网络类型与是否正在扫描局域网（用于界面状态提示）。
        getNetworkState: function () {
            if (!hasLan) return { type: 'Wifi', scan: true };  // 调试环境当作可扫描
            return parse(AndroidMchat.getNetworkState(), { type: 'None', scan: false });
        },

        setDisplayName: function (name) {
            if (hasLan) AndroidMchat.setDisplayName(name);
        },

        /**
         * 发送消息。
         * @param {Object} message  消息对象 {id, kind, text, ...}
         * @param {string} [base64] 附件二进制的 base64
         * @param {string} [name]   附件原始文件名（用于取扩展名）
         * @returns {Object|null}   原生补全后的完整消息（含 mediaUrl）
         */
        send: function (message, base64, name) {
            if (!hasLan) {
                // 调试降级：直接回显
                message.from = 'web-debug';
                message.fromName = '调试端';
                message.time = Date.now();
                return message;
            }
            // 会话归属：消息总是发到当前房间/私聊
            if (!message.conv && Bridge.conv) message.conv = Bridge.conv;
            var res = AndroidMchat.sendMessage(JSON.stringify(message), base64 || '', name || '');
            return parse(res, null);
        },

        /* ===== 会话（房间 / 私聊）与消息删除 ===== */

        conv: 'room:mesh',

        getConversations: function () {
            if (!hasLan) return { items: [], conv: Bridge.conv, self: 'web-debug' };
            return parse(AndroidMchat.getConversations(), { items: [], conv: Bridge.conv });
        },

        getMessages: function (conv, limit) {
            if (!hasLan) return [];
            return parse(AndroidMchat.getMessages(conv || Bridge.conv, limit || 300), []);
        },

        selectConv: function (conv) {
            if (!conv) return;
            Bridge.conv = conv;
            if (hasLan) AndroidMchat.selectConv(conv);
        },

        joinRoom: function (code) {
            if (!hasLan) return { conv: 'room:' + code };
            var res = parse(AndroidMchat.joinRoom(code || ''), null);
            if (res && res.conv) Bridge.conv = res.conv;
            return res;
        },

        startDm: function (peerId) {
            if (!hasLan) return { conv: null };
            var res = parse(AndroidMchat.startDm(peerId || ''), null);
            if (res && res.conv) Bridge.conv = res.conv;
            return res;
        },

        deleteMessage: function (id) {
            if (!hasLan || !id) return false;
            try { return AndroidMchat.deleteMessage(id) === true; } catch (e) { return false; }
        },

        clearConversation: function (conv) {
            if (!hasLan) return 0;
            try { return Number(AndroidMchat.clearConversationJs(conv || Bridge.conv)) || 0; }
            catch (e) { return 0; }
        },

        clearAllMessages: function () {
            if (!hasLan) return 0;
            try { return Number(AndroidMchat.clearAllMessages()) || 0; } catch (e) { return 0; }
        },

        channelStatus: function () {
            if (!hasLan) return null;
            return parse(AndroidMchat.getChannelStatus(), null);
        },

        // tools.js 的「频道」页一直用这个名字；保留别名，避免该页一打开就抛异常
        getChannelStatus: function () {
            return Bridge.channelStatus();
        },

        setDualChannel: function (on) {
            if (hasLan) AndroidMchat.setDualChannel(!!on);
        },

        saveMedia: function (base64, name) {
            if (!hasLan) return null;
            return AndroidMchat.saveMedia(base64, name || '');
        },

        notify: function (title, text) {
            if (hasNotifier) AndroidNotifier.notify(title || '通知', text || '');
        },

        toast: function (msg) {
            if (hasHost) AndroidHost.toast(msg);
            else console.log('[toast]', msg);
        },

        /** 写入系统剪贴板（收到无前缀普通消息时调用） */
        copyToClipboard: function (text) {
            if (!text) return;
            if (hasHost) { AndroidHost.copyToClipboard(text); return; }
            try { navigator.clipboard && navigator.clipboard.writeText(text); } catch (e) {}
        },

        /**
         * 打开已收发文件（位于应用私有 media 目录）。由原生经 FileProvider 调起
         * 系统查看器，从而 PDF/文档/安装包等任意类型都能被对应 App 打开。
         * @param {string} url  形如 /__media/<文件名> 的媒体地址
         */
        openFile: function (url) {
            if (hasHost && AndroidHost.openFile) { AndroidHost.openFile(url || ''); return true; }
            return false;
        },

        /**
         * 把播放状态同步给原生常驻通知，使音乐切后台后继续播放且可从通知栏控制。
         * @param {string} title  歌名
         * @param {string} artist 艺术家
         * @param {boolean} playing 是否正在播放
         * @param {boolean} active  播放器是否处于激活状态（关闭时传 false）
         */
        updatePlayback: function (title, artist, playing, active) {
            if (hasHost && AndroidHost.updatePlayback) {
                AndroidHost.updatePlayback(title || '', artist || '', !!playing, !!active);
            }
        },

        /**
         * 在受控端建立 / 刷新「虚拟播放」：占用本机播放通道（MediaSession + 音频焦点）
         * 并在通知栏常驻播放器，但本机并不真正出声。
         * @param {Object} state {title, artist, album, duration, position, playing,
         *                        sourceId, sourceName, cover}
         */
        showRemoteMedia: function (state) {
            if (hasMedia) AndroidMedia.showRemoteMedia(JSON.stringify(state || {}));
        },

        /** 撤销受控端的虚拟播放占用：取消通知并释放音频焦点。 */
        hideRemoteMedia: function () {
            if (hasMedia) AndroidMedia.hideRemoteMedia();
        },

        exitApp: function () {
            if (hasHost) AndroidHost.exitApp();
        },

        /** 广播通话信令（call-invite/accept/bye），由原生经局域网 TCP 转发并自处理媒体。
         *  前端不再使用此方法建立 WebRTC，信令改由原生内部生成。 */
        sendCallSignal: function (signalJson) {
            if (hasLan) AndroidMchat.sendCallSignal(signalJson);
        },

        /** 发起原生语音通话（点对点 UDP PCM，不依赖 WebRTC）。peer: {id, name} */
        callInvite: function (peer) {
            if (!hasLan) { emit('callerror', { message: '仅在 App 内支持通话' }); return; }
            AndroidMchat.callInvite(JSON.stringify({ id: peer.id, name: peer.name }));
        },

        /** 接听来电 */
        callAccept: function () {
            if (hasLan) AndroidMchat.callAccept();
        },

        /** 挂断通话 */
        callHangup: function () {
            if (hasLan) AndroidMchat.callHangup();
        },

        /**
         * 发送功能控制指令（音量/播放/上下曲/静音等）。
         * 走独立 control 帧：不进聊天历史、不渲染消息、不弹通知、不进剪贴板，
         * 接收端仅执行对应模拟输入。
         * @param {string} cmd 指令名，如 "音量加" / "播放暂停" / "上一曲" ...
         */
        sendControl: function (cmd) {
            if (!hasLan) return;   // 调试环境无原生对象，静默忽略
            AndroidMchat.sendControl(cmd);
        },

        /* ============ 屏幕画笔叠加层 ============ */

        /** 切换屏幕画笔叠加层（开/关）。 */
        toggleScreenDraw: function () {
            if (hasLan) AndroidMchat.toggleScreenDraw();
        },

        /** 屏幕画笔叠加层是否正在运行。 */
        isScreenDrawOn: function () {
            if (hasLan) { try { return AndroidMchat.isScreenDrawOn(); } catch (e) { return false; } }
            return false;
        },

        /* ============ 局域网文件共享 ============ */

        /** 文件共享访问地址；未连接 WiFi 时返回 "未连接 WiFi"。 */
        getShareInfo: function () {
            if (hasLan) { try { return AndroidMchat.getShareInfo() || '未连接 WiFi'; } catch (e) { return '未连接 WiFi'; } }
            return '未连接 WiFi';
        },

        /* ============ 悬浮窗 ============ */

        /** 是否已授予悬浮窗权限。 */
        hasOverlayPermission: function () {
            if (hasHost && AndroidHost.hasOverlayPermission) {
                try { return AndroidHost.hasOverlayPermission(); } catch (e) { return false; }
            }
            return false;
        },

        /** 跳转系统设置页申请悬浮窗权限。 */
        requestOverlayPermission: function () {
            if (hasHost && AndroidHost.requestOverlayPermission) AndroidHost.requestOverlayPermission();
        },

        /** 开启/关闭悬浮窗（持久化，重启 App 自动恢复）。 */
        setFloatingWindow: function (on) {
            if (hasHost && AndroidHost.setFloatingWindow) AndroidHost.setFloatingWindow(!!on);
        },

        /** 悬浮窗是否正在运行。 */
        isFloatingWindowOn: function () {
            if (hasHost && AndroidHost.isFloatingWindowOn) {
                try { return AndroidHost.isFloatingWindowOn(); } catch (e) { return false; }
            }
            return false;
        },

        /** 开始语音消息录音（原生 AudioRecord，结果经 voiceResult 事件回传） */
        startVoiceRecord: function () {
            if (hasLan) AndroidMchat.startVoiceRecord();
            else emit('voiceResult', { error: '仅在 App 内支持录音' });
        },

        /** 停止语音消息录音 */
        stopVoiceRecord: function () {
            if (hasLan) AndroidMchat.stopVoiceRecord();
        },

        /* ============ 系统通知自动转发 ============ */
        /** 是否已授权本应用监听通知（系统「通知使用权」）。 */
        isNotificationListenerEnabled: function () {
            if (hasHost && AndroidHost.isNotificationListenerEnabled) {
                try { return AndroidHost.isNotificationListenerEnabled(); } catch (e) { return false; }
            }
            return false;
        },

        /** 打开系统设置页，引导用户授权通知监听。 */
        openNotificationSettings: function () {
            if (hasHost && AndroidHost.openNotificationListenerSettings) {
                AndroidHost.openNotificationListenerSettings();
            }
        },

        /* ============ 消息撤回 / 编辑 ============ */

        /** 撤回一条消息（双向同步删除）。id 为本机或对端消息 id。 */
        recallMessage: function (id) {
            if (!hasLan || !id) return false;
            try { return AndroidMchat.deleteMessage(id) === true; } catch (e) { return false; }
        },

        /** 编辑自己发出的一条消息：newText 为新正文。 */
        editMessage: function (conv, id, newText) {
            if (!hasLan || !id) return false;
            try { return AndroidMchat.editMessage(conv || Bridge.conv, id, newText || '') === true; }
            catch (e) { return false; }
        },

        /* ============ qaul 群组 ============ */

        createQaulGroup: function (name) {
            if (!hasLan) return null;
            return parse(AndroidMchat.createQaulGroup(name || ''), null);
        },

        getQaulGroups: function () {
            if (!hasLan) return [];
            return parse(AndroidMchat.getQaulGroups(), []);
        },

        openQaulDm: function (peer) {
            if (!hasLan) return null;
            var res = parse(AndroidMchat.openQaulDm(peer || ''), null);
            if (res && res.conv) Bridge.conv = res.conv;
            return res;
        },

        removeFromQaulGroup: function (groupId, peer) {
            if (!hasLan) return false;
            try { return AndroidMchat.removeFromQaulGroup(groupId || '', peer || '') === true; }
            catch (e) { return false; }
        },

        renameConversation: function (conv, title) {
            if (hasLan) AndroidMchat.renameConversation(conv || Bridge.conv, title || '');
        },

        getQaulUsers: function () {
            if (!hasLan) return [];
            return parse(AndroidMchat.getQaulUsers(), []);
        },

        publishQaulFeed: function (text) {
            if (!hasLan) return false;
            try { return AndroidMchat.publishQaulFeed(text || '') === true; } catch (e) { return false; }
        },

        /* ============ 身份 / 权限 ============ */

        getIdentity: function () {
            if (!hasLan) return {};
            return parse(AndroidMchat.getIdentity(), {});
        },

        rotateIdentity: function (scope) {
            if (!hasLan) return null;
            return parse(AndroidMchat.rotateIdentity(scope || ''), null);
        },

        requestAllFilesAccess: function () {
            if (hasLan) AndroidMchat.requestAllFilesAccess();
        },

        getQaulStatus: function () {
            if (!hasLan) return null;
            return parse(AndroidMchat.getQaulStatus(), null);
        },

        /* ============ 文件管理器 ============ */

        /** 列出共享目录条目。rel 为相对路径（可空）。 */
        listShare: function (rel) {
            if (!hasLan) return { rel: '', items: [] };
            return parse(AndroidMchat.listShare(rel || ''), { rel: '', items: [] });
        },

        /** 上传文件（base64 字符串 + 文件名）。 */
        uploadFile: function (base64, name) {
            if (!hasLan) return false;
            try { return AndroidMchat.uploadFile(base64, name || '') === true; } catch (e) { return false; }
        },

        /* ============ 一起听 / 视频分享卡片 ============ */

        /** 把对端音乐卡片里的共享播放列表装进本机播放器并起播。 */
        joinListen: function (playlist, index) {
            if (!hasLan || !playlist) return false;
            try {
                return AndroidMchat.joinListen(
                    typeof playlist === 'string' ? playlist : JSON.stringify(playlist),
                    Number(index) || 0) === true;
            } catch (e) { return false; }
        },

        /** 一起听：只共享同一个歌单，本机各播各的。 */
        startSharedPlaylist: function (playlist, index, cardId, host) {
            if (!hasLan || !playlist) return false;
            try {
                return AndroidMchat.startSharedPlaylist(
                    typeof playlist === 'string' ? playlist : JSON.stringify(playlist),
                    Number(index) || 0, cardId || '', host || '') === true;
            } catch (e) { return false; }
        },

        /** 同步听：同一时刻同一首歌，跟随发起端切歌/进度/播放态。 */
        startSyncListen: function (playlist, index, cardId, state) {
            if (!hasLan || !playlist) return false;
            try {
                var st = typeof state === 'string' ? state : JSON.stringify(state || {});
                return AndroidMchat.startSyncListen(
                    typeof playlist === 'string' ? playlist : JSON.stringify(playlist),
                    Number(index) || 0, cardId || '', st) === true;
            } catch (e) { return false; }
        },

        exitSyncListen: function () {
            if (hasLan) { try { AndroidMchat.exitSyncListen(); } catch (e) {} }
        },

        getSyncListen: function () {
            if (!hasLan) return { mode: 0 };
            try { return JSON.parse(AndroidMchat.getSyncListen() || '{"mode":0}'); }
            catch (e) { return { mode: 0 }; }
        },

        /** 「一起听」总开关（默认打开）。关闭后本机发出的音乐卡片不再带播放列表。 */
        getListenTogether: function () {
            if (!hasLan) return true;
            try { return AndroidMchat.getListenTogether() === true; } catch (e) { return true; }
        },

        setListenTogether: function (on) {
            if (hasLan) { try { AndroidMchat.setListenTogether(!!on); } catch (e) {} }
        },

        /** 点击视频分享卡片：调起本机 VideoActivity 全屏播放。 */
        openSharedVideo: function (url, title) {
            if (!hasLan || !url) return false;
            try { return AndroidMchat.openSharedVideo(url, title || '') === true; }
            catch (e) { return false; }
        },

        /** 视频卡片「一起看」：宿主机代理地址 + 会话 id，跟随宿主机进度。 */
        openWatchTogether: function (url, title, cardId, host) {
            if (!hasLan || !url) return false;
            try {
                return AndroidMchat.openWatchTogether(url, title || '', cardId || '', host || '') === true;
            } catch (e) { return false; }
        },

        /** 程序更新卡片：安装 UpdateManager 已下载的 APK。 */
        installApk: function (name) {
            if (!hasLan || !name) return false;
            try { return AndroidMchat.installApk(String(name)) === true; }
            catch (e) { return false; }
        },
    };
})();

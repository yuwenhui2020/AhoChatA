/**
 * 应用入口：串联桥接、聊天、浏览器、播放器，并接管系统返回键。
 */
(function () {
    'use strict';

    var overlayStack = [];

    window.App = {
        pushOverlay: function (id) {
            if (overlayStack.indexOf(id) < 0) overlayStack.push(id);
        },
        popOverlay: function (id) {
            var i = overlayStack.indexOf(id);
            if (i >= 0) overlayStack.splice(i, 1);
        }
    };

    /**
     * 跨设备「功能控制」指令的统一执行入口（由原生 MchatBridge 调用）。
     *
     * 决策逻辑：
     *   1) 本机正在用 App 内置播放器播放 → 直接控制内置播放器（真正切歌/暂停）；
     *   2) 否则 → 回调原生 execSystemControl，由 ControlExecutor 控制外部播放器/系统音量。
     *
     * 之所以要区分：内置播放器是 audio 元素，不会注册成系统原生 MediaSession，
     * 若一律交给系统媒体会话控制，就切不动本 App 正在播放的歌曲。
     */
    window.MchatRemoteExec = function (cmd) {
        var MEDIA_CMD = {
            '上一曲': 'prev',
            '下一曲': 'next',
            '播放暂停': 'playpause',
            '播放': 'play',
            '暂停': 'pause'
        };

        var internal = MEDIA_CMD[cmd];
        if (internal && window.Media && window.Media.getPlayState) {
            var st = window.Media.getPlayState();
            // 只有本机会话处于激活状态（正在播放或暂停但仍是当前播放源）才接管
            if (st && st.active) {
                // 内置遥控器的 playpause 需根据当前是否在播放决定
                var real = internal;
                if (internal === 'playpause') real = st.playing ? 'pause' : 'play';
                window.Media.remoteControl(real);
                return true;
            }
        }

        // 本机内置播放器不在播放：交给原生控制外部播放器 / 系统音量
        try {
            AndroidMchat.execSystemControl(cmd);
        } catch (e) {
            try { window.Bridge.execSystemControl && window.Bridge.execSystemControl(cmd); } catch (e2) {}
        }
        return false;
    };

    /**
     * 系统返回键统一入口（由原生 MainActivity.OnBackPressed 调用）。
     * 返回 true 表示前端已消费，false 则由原生退出应用。
     * 优先级：覆盖层 > 侧栏 > 浏览器标签历史 > 回到聊天。
     */
    window.__appHandleBack = function () {
        var top = window.Media.topOverlay();
        if (top) { window.Media.closeOverlay(top); return true; }

        var floatOv = document.getElementById('botOverlay');
        if (floatOv && !floatOv.classList.contains('hidden')) {
            if (window.FloatPanel) window.FloatPanel.close();
            else floatOv.classList.add('hidden');
            return true;
        }

        var toolsOv = document.getElementById('toolsOverlay');
        if (toolsOv && !toolsOv.classList.contains('hidden')) {
            if (window.Tools) window.Tools.close();
            else toolsOv.classList.add('hidden');
            return true;
        }

        var callOv = document.getElementById('callOverlay');
        if (callOv && callOv.classList.contains('show')) { callOv.classList.remove('show'); return true; }

        if (window.Browser.isSidebarOpen()) { window.Browser.closeSidebar(); return true; }

        if (window.Browser.inBrowser()) {
            if (window.Browser.goBack()) return true;
            window.Browser.showChat();
            return true;
        }
        return false;
    };

    /**
     * 功能控制「退出 / esc」：关闭所有覆盖层并退出全屏（等效于按返回键到底）。
     */
    window.__closeOverlays = function () {
        try {
            var top = window.Media && window.Media.topOverlay && window.Media.topOverlay();
            if (top) window.Media.closeOverlay(top);
            if (window.Browser && window.Browser.isSidebarOpen()) window.Browser.closeSidebar();
            if (document.fullscreenElement || document.webkitFullscreenElement) {
                if (document.exitFullscreen) document.exitFullscreen();
                else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
            }
        } catch (e) {}
    };

    /* ==================== 附件栏 ==================== */

    function initAttachBar() {
        var bar = document.getElementById('attachBar');
        var btn = document.getElementById('btnAttach');
        var picker = document.getElementById('filePicker');
        var pendingKind = null;

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

        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            closeAllPanels('attach');
            bar.classList.toggle('hidden');
            bar.setAttribute('aria-hidden', bar.classList.contains('hidden') ? 'true' : 'false');
        });

        // 点击输入框时收起附件栏
        document.getElementById('msgInput').addEventListener('focus', function () {
            bar.classList.add('hidden');
            bar.setAttribute('aria-hidden', 'true');
        });

        // 点击外部关闭附件栏
        document.addEventListener('click', function (e) {
            if (!bar.classList.contains('hidden') && 
                !bar.contains(e.target) && 
                e.target !== btn) {
                bar.classList.add('hidden');
                bar.setAttribute('aria-hidden', 'true');
            }
        });

        // ESC 键关闭
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !bar.classList.contains('hidden')) {
                bar.classList.add('hidden');
                bar.setAttribute('aria-hidden', 'true');
            }
        });

        bar.querySelectorAll('.attach-item').forEach(function (item) {
            item.addEventListener('click', function () {
                var act = item.getAttribute('data-act');

                bar.classList.add('hidden');
                bar.setAttribute('aria-hidden', 'true');

                if (act === 'url' || act === 'notice') {
                    window.Chat.insertTag(act);
                    return;
                }

                pendingKind = act;
                picker.accept = act === 'pic' ? 'image/*'
                              : act === 'video' ? 'video/*'
                              : act === 'file' ? '*/*'
                              : 'audio/*';
                picker.value = '';
                picker.click();
            });
        });

        initVoiceButton();

        picker.addEventListener('change', function () {
            var file = picker.files && picker.files[0];
            if (file && pendingKind) window.Chat.sendFile(file, pendingKind);
            pendingKind = null;
            picker.value = '';
        });
    }

    /**
     * 语音输入长条按钮：
     * - 短按（轻点）：开始录制 → 再次点击停止发送
     * - 长按（按住≥250ms）：开始录制 → 松手停止发送
     * 交互优化：
     *   - 长按阈值前显示「按住说话」+ 轻微放大动画提示
     *   - 进入录制态显示倒计时 + 红色脉冲动画
     *   - 指针滑出按钮区域仅取消「长按预览」，不中断已锁定的录制
     *   - 完整的触觉/视觉反馈 + 无障碍支持
     */
    function initVoiceButton() {
        var btn = document.getElementById('btnVoice');
        var label = document.getElementById('btnVoiceLabel');
        if (!btn || !label) return;

        var voice = window.Chat.voice;
        var active = false;           // 当前是否正在录制（含短按锁定）
        var downTime = 0;             // 本次按下的时间戳
        var holdTimer = null;         // 长按判定定时器
        var HOLD_MS = 250;            // 超过此值视为长按
        var isLongPress = false;      // 标记是否已触发长按
        var holdThresholdReached = false; // 长按阈值是否已达成（用于视觉反馈）

        function setLabel(t) { label.textContent = t; }

        function setHoldPreview(reached) {
            holdThresholdReached = reached;
            btn.classList.toggle('hold-ready', reached);
            if (reached) {
                setLabel('松手发送');
            } else if (!active) {
                setLabel('按住说话');
            }
        }

        function startRec() {
            if (!voice || !voice.supported()) {
                window.Bridge.toast('当前环境不支持录音');
                return false;
            }
            if (voice.start(function (sec) { 
                setLabel('录制中 ' + sec + '″（点击/松手发送）'); 
            })) {
                active = true;
                isLongPress = false; // 重置长按标记
                btn.classList.add('recording');
                btn.classList.remove('hold-ready');
                return true;
            }
            return false;
        }

        function stopRec() {
            if (!active) return;
            active = false;
            isLongPress = false;
            btn.classList.remove('recording', 'hold-ready');
            setLabel('按住说话');
            voice.stop();   // 原生回调 voiceResult 会发送消息
        }

        function cancelHoldTimer() {
            if (holdTimer) {
                clearTimeout(holdTimer);
                holdTimer = null;
            }
        }

        btn.addEventListener('pointerdown', function (e) {
            // 仅主按钮（左键/单指触摸）响应
            if (e.button !== 0 && e.pointerType !== 'touch') return;
            e.preventDefault();
            
            downTime = Date.now();
            isLongPress = false;
            holdThresholdReached = false;
            btn.classList.remove('hold-ready');
            
            if (!active) {
                // 未在录制：启动长按判定定时器
                holdTimer = setTimeout(function () {
                    holdTimer = null;
                    isLongPress = true;
                    holdThresholdReached = true;
                    btn.classList.add('hold-ready');
                    // 长按直接开始录制
                    startRec();
                }, HOLD_MS);
            } else {
                // 已在录制（短按锁定中）：再次按下即停止并发送
                cancelHoldTimer();
                stopRec();
            }
        });

        function onRelease(e) {
            // 仅主按钮响应
            if (e.button !== 0 && e.pointerType !== 'touch') return;
            
            cancelHoldTimer();
            
            if (!active) return;
            
            if (isLongPress) {
                // 长按模式：松手即停止并发送
                stopRec();
            } else {
                // 短按模式：未达到长按阈值时的松手
                // 此时可能已触发 startRec（若按下时间>250ms但未等到定时器？实际上定时器会触发startRec）
                // 如果未触发长按，说明是轻点（tap），保持录制等待再次点击
                // 不做任何事，维持录制状态
            }
        }

        btn.addEventListener('pointerup', onRelease);
        btn.addEventListener('pointercancel', function (e) {
            cancelHoldTimer();
            // pointercancel 通常是系统打断（来电、通知栏下拉等）
            // 如果是长按已触发录制，不应停止；仅清理预览状态
            if (!isLongPress) {
                btn.classList.remove('hold-ready');
            }
        });
        
        btn.addEventListener('pointerleave', function (e) {
            // 指针滑出按钮区域：仅取消「长按预览」，不中断已锁定的录制
            cancelHoldTimer();
            if (!isLongPress) {
                btn.classList.remove('hold-ready');
                if (!active) setLabel('按住说话');
            }
        });

        // 触摸时阻止默认（避免选中/滚动/上下文菜单）
        btn.addEventListener('touchstart', function (e) { e.preventDefault(); }, { passive: false });
        btn.addEventListener('contextmenu', function (e) { e.preventDefault(); });

        // 无障碍支持：键盘操作（空格/回车）模拟长按
        btn.addEventListener('keydown', function (e) {
            if ((e.key === ' ' || e.key === 'Enter') && !e.repeat) {
                e.preventDefault();
                // 模拟 pointerdown
                btn.dispatchEvent(new PointerEvent('pointerdown', { 
                    button: 0, 
                    pointerType: 'keyboard',
                    isPrimary: true,
                    bubbles: true,
                    cancelable: true
                }));
            }
        });
        btn.addEventListener('keyup', function (e) {
            if (e.key === ' ' || e.key === 'Enter') {
                e.preventDefault();
                btn.dispatchEvent(new PointerEvent('pointerup', { 
                    button: 0, 
                    pointerType: 'keyboard',
                    isPrimary: true,
                    bubbles: true,
                    cancelable: true
                }));
            }
        });

        // ARIA 无障碍属性
        btn.setAttribute('role', 'button');
        btn.setAttribute('aria-label', '语音输入');
        btn.setAttribute('aria-pressed', 'false');
        
        // 监听录制状态变化同步 ARIA
        var originalStartRec = startRec;
        var originalStopRec = stopRec;
        
        // 注入到全局以便 voiceResult 回调能访问
        window.__voiceButton = {
            setRecording: function(recording) {
                btn.setAttribute('aria-pressed', recording ? 'true' : 'false');
                btn.classList.toggle('recording', recording);
                if (!recording) btn.classList.remove('hold-ready');
            }
        };
    }

    /* ==================== 启动 ==================== */

    /**
     * 输入法适配（增强版）。
     *
     * 目标：唤起输入法时，底部输入框整体上移并紧贴键盘顶部，
     * 让用户随时看得见自己输入了什么。
     *
     * 增强点：
     *  - 支持 VirtualKeyboard API（Chrome 94+ / Edge 94+ / Safari 16.4+）
     *  - 防抖处理 resize/scroll 事件，避免布局抖动
     *  - iOS WKWebView 兼容：visualViewport 在某些情况下不可靠，回退到 resize 估算
     *  - 输入框聚焦/失焦时强制同步，防止焦点丢失导致布局不同步
     *  - 键盘收起时恢复滚动位置
     *  - 支持 VirtualKeyboard.overlaysContent 设置
     */
    function initKeyboardAdapt() {
        var vv = window.visualViewport;
        var root = document.documentElement;
        var composer = document.querySelector('.composer');
        var msgInput = document.getElementById('msgInput');
        
        // 防抖定时器
        var applyTimer = null;
        var lastOverlap = 0;
        var scrollPosBeforeKb = 0;
        var isComposerFocused = false;

        function apply(overlap, immediate) {
            if (!isFinite(overlap) || overlap < 0) overlap = 0;
            
            // 只有变化超过 2px 才更新，避免微小抖动
            if (!immediate && Math.abs(overlap - lastOverlap) < 2) return;
            lastOverlap = overlap;

            root.style.setProperty('--kb', overlap + 'px');
            document.body.classList.toggle('kb-open', overlap > 80);

            // 只有键盘真正弹起时才滚动到底部
            if (overlap > 80 && overlap > lastOverlap) {
                window.Chat.scrollToBottom();
            }
        }

        function computeOverlap() {
            if (!vv) return 0;
            // 键盘占据的高度 = 布局视口高度 - 可视视口高度 - 视口上偏移
            var overlap = window.innerHeight - vv.height - vv.offsetTop;
            return Math.max(0, overlap);
        }

        function onVisualViewportChange() {
            if (applyTimer) clearTimeout(applyTimer);
            applyTimer = setTimeout(function () {
                apply(computeOverlap());
            }, 16); // ~1 帧防抖
        }

        function onFocus() {
            isComposerFocused = true;
            // 记录键盘弹起前的滚动位置
            var listEl = document.getElementById('messageList');
            if (listEl) scrollPosBeforeKb = listEl.scrollTop;
            
            // 强制同步一次
            apply(computeOverlap(), true);
            
            // 启用 VirtualKeyboard overlaysContent（如果支持）
            if (navigator.virtualKeyboard) {
                try { navigator.virtualKeyboard.overlaysContent = true; } catch (e) {}
            }
        }

        function onBlur() {
            isComposerFocused = false;
            // 键盘收起时恢复滚动位置（可选，视产品需求）
            // 这里仅重置 kb 变量
            apply(0, true);
            
            // 禁用 overlaysContent
            if (navigator.virtualKeyboard) {
                try { navigator.virtualKeyboard.overlaysContent = false; } catch (e) {}
            }
        }

        // VisualViewport 事件（现代浏览器）
        if (vv) {
            vv.addEventListener('resize', onVisualViewportChange, { passive: true });
            vv.addEventListener('scroll', onVisualViewportChange, { passive: true });
            
            // VirtualKeyboard API 事件（Chrome 94+ / Safari 16.4+）
            if (navigator.virtualKeyboard) {
                navigator.virtualKeyboard.addEventListener('geometrychange', function () {
                    onVisualViewportChange();
                });
            }
        }

        // 输入框焦点事件：强制同步
        if (msgInput) {
            msgInput.addEventListener('focus', onFocus);
            msgInput.addEventListener('blur', onBlur);
        }

        // 老 WebView / 无 visualViewport：退化为窗口 resize 估算 + 防抖
        var resizeTimer = null;
        var baseHeight = window.innerHeight;
        window.addEventListener('resize', function () {
            if (resizeTimer) clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () {
                if (vv) return; // 有 visualViewport 时由其处理
                var overlap = baseHeight - window.innerHeight;
                if (overlap < 0) { baseHeight = window.innerHeight; overlap = 0; }
                apply(overlap);
            }, 16);
        }, { passive: true });

        // 页面可见性变化时同步（切回前台时键盘可能已收起）
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden && !isComposerFocused) {
                apply(0, true);
            }
        });

        // 初始化
        apply(0);
    }

   // ==================== 按键大全功能 ====================
	// ==================== 按键大全功能 ====================
function initKeypad() {
	const KEY_LIST = [
		// 功能键
		{ label: '桌面1', value: 'ctrl+1' },
		{ label: '桌面2', value: 'ctrl+2' },
		{ label: '桌面3', value: 'ctrl+3' },
		{ label: '桌面4', value: 'ctrl+4' },
		{ label: '移动1', value: 'ctrl+alt+1' },
		{ label: '移动2', value: 'ctrl+alt+2' },
		{ label: '移动3', value: 'ctrl+alt+3' },
		{ label: '移动4', value: 'ctrl+alt+4' },
		{ label: '放映', value: 'f5' },
		{ label: '退出', value: 'esc' },
		{ label: '上页', value: 'left' },
		{ label: '下页', value: 'right' },
		
		// 控制键
		{ label: '剪切', value: 'ctrl+x' },
		{ label: '复制', value: 'ctrl+c' },
		{ label: '粘贴', value: 'ctrl+v' },
		{ label: '打印', value: 'ctrl+p' },
		{ label: '桌面', value: 'win+d' },
		{ label: '切屏', value: 'alt+tab' },
		{ label: '关闭', value: 'alt+f4' },
		{ label: '确认', value: 'enter' },
		
		// 方向键
		{ label: '↑', value: 'up' },
		{ label: '↓', value: 'down' },
		{ label: '←', value: 'left' },
		{ label: '→', value: 'right' },
		
		// 其他
		{ label: '空格', value: 'space' },
		{ label: 'Del', value: 'delete' },
		{ label: '插入', value: 'insert' },
		{ label: '主页', value: 'home' },
		{ label: '尾页', value: 'end' },
		{ label: '上页', value: 'pageup' },
		{ label: '下页', value: 'pagedown' }
	];

	const keypadBtn = document.getElementById('btnKeypad');
	const keypadOverlay = document.getElementById('keypadOverlay');
	const keypadClose = document.getElementById('keypadClose');
	const keypadGrid = document.getElementById('keypadGrid');

	if (!keypadBtn || !keypadOverlay || !keypadGrid) {
		console.warn('[Keypad] 按键大全元素不存在，跳过初始化');
		return;
	}

	// ---------- 组合键状态 ----------
	let pressedKeys = new Set();           // 当前按下的键（实时）
	let currentComboSnapshot = new Set();  // 本次组合的完整快照（用于最终发送）
	let keypadOpen = false;
	let sendTimer = null;                  // 发送延迟定时器
	
	// 键盘映射：标准化 key 值
	const KEY_MAP = {
		'F1': 'f1', 'F2': 'f2', 'F3': 'f3', 'F4': 'f4',
		'F5': 'f5', 'F6': 'f6', 'F7': 'f7', 'F8': 'f8',
		'F9': 'f9', 'F10': 'f10', 'F11': 'f11', 'F12': 'f12',
		'Enter': 'enter', 'Escape': 'esc', 'Backspace': 'backspace',
		'Tab': 'tab', 'CapsLock': 'capslock',
		'Shift': 'shift', 'Control': 'ctrl', 'Alt': 'alt', 'Meta': 'meta',
		'ArrowUp': 'up', 'ArrowDown': 'down',
		'ArrowLeft': 'left', 'ArrowRight': 'right',
		' ': 'space', 'Space': 'space',
		'Delete': 'delete', 'Insert': 'insert',
		'Home': 'home', 'End': 'end',
		'PageUp': 'pageup', 'PageDown': 'pagedown',
		'ContextMenu': 'contextmenu'
	};

	// 修饰键集合（用于排序显示：修饰键在前）
	const MODIFIER_KEYS = new Set(['ctrl', 'shift', 'alt', 'meta']);

	// ---------- 更新状态显示 ----------
	function updateStatus(text) {
		const statusEl = document.getElementById('keypadStatus');
		if (statusEl) {
			statusEl.textContent = text;
		}
	}

	// ---------- 发送按键指令 ----------
	function sendKeyCommand(key) {
		console.log('[Keypad] 发送按键: ' + key);
		
		if (typeof window.Bridge === 'undefined') {
			console.error('[Keypad] window.Bridge 未定义');
			return;
		}
		
		if (typeof window.Bridge.send !== 'function') {
			console.error('[Keypad] window.Bridge.send 不是函数');
			return;
		}
		
		try {
			// 构造与 sendText 相同的消息格式
			const parsed = {
				kind: 'text',
				text: '"' + key + '"'
			};
			
			console.log('[Keypad] 发送内容:', parsed);
			
			const result = window.Bridge.send(parsed);
			console.log('[Keypad] Bridge.send 返回:', result);
		} catch (err) {
			console.error('[Keypad] Bridge.send 异常:', err);
		}
	}

	// ---------- 键盘事件处理 ----------
	function handleKeyDown(event) {
		if (!keypadOpen) return;
		
		const rawKey = event.key;
		// 忽略特殊键
		if (['OS', 'ContextMenu'].includes(rawKey)) return;
		
		const mappedKey = KEY_MAP[rawKey] || rawKey.toLowerCase();
		
		// 记录到快照（用于最终发送）
		currentComboSnapshot.add(mappedKey);
		// 记录到实时按下集合
		pressedKeys.add(mappedKey);
		
		// 更新状态显示（按固定顺序：修饰键在前）
		const sortedKeys = Array.from(pressedKeys).sort((a, b) => {
			const aMod = MODIFIER_KEYS.has(a) ? 0 : 1;
			const bMod = MODIFIER_KEYS.has(b) ? 0 : 1;
			return aMod - bMod || a.localeCompare(b);
		});
		updateStatus('按下的键: ' + sortedKeys.join(' + '));
		
		// 取消之前的发送定时器
		if (sendTimer) {
			clearTimeout(sendTimer);
			sendTimer = null;
		}
		
		event.preventDefault();
		event.stopPropagation();
	}

	function handleKeyUp(event) {
		if (!keypadOpen) return;
		
		const rawKey = event.key;
		if (['OS', 'ContextMenu'].includes(rawKey)) return;
		
		const mappedKey = KEY_MAP[rawKey] || rawKey.toLowerCase();
		
		// 从实时集合移除
		pressedKeys.delete(mappedKey);
		
		// 如果还有键按着，更新显示并继续等待
		if (pressedKeys.size > 0) {
			const sortedKeys = Array.from(pressedKeys).sort((a, b) => {
				const aMod = MODIFIER_KEYS.has(a) ? 0 : 1;
				const bMod = MODIFIER_KEYS.has(b) ? 0 : 1;
				return aMod - bMod || a.localeCompare(b);
			});
			updateStatus('按下的键: ' + sortedKeys.join(' + '));
			return;
		}
		
		// 所有键都已松开，使用快照发送组合键
		if (currentComboSnapshot.size > 0) {
			if (sendTimer) {
				clearTimeout(sendTimer);
			}
			sendTimer = setTimeout(() => {
				sendTimer = null;
				
				const sortedKeys = Array.from(currentComboSnapshot).sort((a, b) => {
					const aMod = MODIFIER_KEYS.has(a) ? 0 : 1;
					const bMod = MODIFIER_KEYS.has(b) ? 0 : 1;
					return aMod - bMod || a.localeCompare(b);
				});
				const comboStr = sortedKeys.join('+');
				sendKeyCommand(comboStr);
				
				// 清空快照
				currentComboSnapshot.clear();
				
				// 延迟恢复提示
				setTimeout(() => {
					if (keypadOpen) {
						updateStatus('点击按键或按下键盘组合键');
					}
				}, 1500);
			}, 50);
		}
		
		event.preventDefault();
		event.stopPropagation();
	}

	// ---------- 渲染按键 ----------
	function renderKeypad() {
		if (!keypadGrid) return;
		keypadGrid.innerHTML = '';
		
		// 状态显示
		const statusDiv = document.createElement('div');
		statusDiv.id = 'keypadStatus';
		statusDiv.className = 'keypad-status';
		statusDiv.textContent = '点击按键或按下键盘组合键';
		keypadGrid.appendChild(statusDiv);

		// 分隔线
		const divider = document.createElement('div');
		divider.className = 'keypad-divider';
		divider.style.cssText = 'grid-column: 1 / -1; height: 1px; background: var(--border-color, #e0e0e0); margin: 2px 0 6px 0;';
		keypadGrid.appendChild(divider);

		// 渲染普通按键
		KEY_LIST.forEach(({ label, value }) => {
			const btn = document.createElement('button');
			btn.className = 'key-btn';
			btn.textContent = label;
			btn.dataset.value = value;
			btn.title = '发送: "' + value + '"';
			btn.addEventListener('click', function(e) {
				const val = this.dataset.value;
				// 点击按钮时清空组合键状态
				pressedKeys.clear();
				currentComboSnapshot.clear();
				if (sendTimer) {
					clearTimeout(sendTimer);
					sendTimer = null;
				}
				sendKeyCommand(val);
				setTimeout(() => {
					if (keypadOpen) {
						updateStatus('点击按键或按下键盘组合键');
					}
				}, 1000);
			});
			keypadGrid.appendChild(btn);
		});
	}

	// ---------- 打开/关闭按键大全 ----------
	function openKeypad() {
		if (keypadOverlay) {
			keypadOverlay.classList.remove('hidden');
			keypadOpen = true;
			// 重置状态
			pressedKeys.clear();
			currentComboSnapshot.clear();
			if (sendTimer) {
				clearTimeout(sendTimer);
				sendTimer = null;
			}
			updateStatus('点击按键或按下键盘组合键');
			// 添加键盘监听
			document.addEventListener('keydown', handleKeyDown);
			document.addEventListener('keyup', handleKeyUp);
		}
	}

	function closeKeypad() {
		if (keypadOverlay) {
			keypadOverlay.classList.add('hidden');
			keypadOpen = false;
			// 移除键盘监听
			document.removeEventListener('keydown', handleKeyDown);
			document.removeEventListener('keyup', handleKeyUp);
			pressedKeys.clear();
			currentComboSnapshot.clear();
			if (sendTimer) {
				clearTimeout(sendTimer);
				sendTimer = null;
			}
		}
	}

	// ---------- 事件绑定 ----------
	keypadBtn.addEventListener('click', openKeypad);

	if (keypadClose) {
		keypadClose.addEventListener('click', closeKeypad);
	}

	keypadOverlay.addEventListener('click', function(e) {
		if (e.target === this) {
			closeKeypad();
		}
	});

	document.addEventListener('keydown', function(e) {
		if (e.key === 'Escape' && !keypadOverlay.classList.contains('hidden')) {
			closeKeypad();
		}
	});

	renderKeypad();

	window.keypad = {
		open: openKeypad,
		close: closeKeypad,
		send: sendKeyCommand
	};

	console.log('[Keypad] 按键大全已初始化，支持自动组合键检测');
}

    function boot() {
        // 初始化时强制回到聊天主界面，清除可能残留的覆盖层状态，
        // 避免"网页端视图 / 通话浮层"盖住聊天顶栏导致顶栏、侧栏不可见。
        var cv = document.getElementById('chatView');
        if (cv) cv.style.display = 'flex';
        var bv = document.getElementById('browserView');
        if (bv) bv.classList.remove('show');
        var co = document.getElementById('callOverlay');
        if (co) co.classList.remove('show');
        var sb = document.getElementById('sidebar');
        if (sb) sb.classList.remove('open');

        window.Media.init();
        window.Browser.init();
        window.Chat.init();
        if (window.FloatPanel) window.FloatPanel.init();
        initAttachBar();
        initKeyboardAdapt();

        // 初始化按键大全
        initKeypad();

        // 顶栏日间 / 夜间切换：紧挨菜单按钮；点一下即时换肤（不重建），并同步原生界面
        var themeBtn = document.getElementById('btnTheme');
        if (themeBtn) {
            // 创建两个图标元素，通过 opacity 切换实现无闪烁切换
            var iconSun = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            var iconMoon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            
            if (window.WI) {
                iconSun.innerHTML = window.WI.svg('sun', 22);
                iconMoon.innerHTML = window.WI.svg('moon', 22);
            }
            
            [iconSun, iconMoon].forEach(function (icon) {
                icon.style.cssText = 'width: 22px; height: 22px; position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); transition: opacity 0.2s ease;';
                themeBtn.appendChild(icon);
            });
            
            var paintThemeIcon = function (t) {
                var isDark = t === 'dark';
                iconSun.style.opacity = isDark ? '1' : '0';
                iconMoon.style.opacity = isDark ? '0' : '1';
                iconSun.style.pointerEvents = isDark ? 'none' : 'auto';
                iconMoon.style.pointerEvents = isDark ? 'auto' : 'none';
            };
            
            themeBtn.addEventListener('click', function () {
                var t = window.MTheme ? window.MTheme.toggle() : 'light';
                paintThemeIcon(t);
                try {
                    if (typeof AndroidMchat !== 'undefined' && AndroidMchat.setTheme) {
                        AndroidMchat.setTheme(t === 'dark' ? 'night' : 'day');
                    }
                } catch (e) { }
            });
            paintThemeIcon(window.MTheme ? window.MTheme.resolve() : 'light');
        }

        var callBtn = document.getElementById('btnCall');
        if (callBtn) {
            callBtn.addEventListener('click', function () {
                var ov = document.getElementById('callOverlay');
                if (ov) ov.classList.add('show');
            });
        }

        // 屏幕画笔叠加层：顶栏 按钮开/关，并根据运行状态高亮
        var drawBtn = document.getElementById('btnDraw');
        function syncDrawBtn() {
            if (!drawBtn) return;
            var on = false;
            try { on = window.Bridge.isScreenDrawOn(); } catch (e) {}
            drawBtn.classList.toggle('on', !!on);
        }
        if (drawBtn) {
            drawBtn.addEventListener('click', function () {
                try { window.Bridge.toggleScreenDraw(); } catch (e) {}
                setTimeout(syncDrawBtn, 500);   // 服务启停有延迟，稍后复查
            });
            document.addEventListener('visibilitychange', function () {
                if (!document.hidden) syncDrawBtn();
            });
            syncDrawBtn();
        }

        // 局域网文件共享地址：在设置面板帮助区直接显示访问方式
        var shareEl = document.getElementById('shareAddr');
        function syncShareAddr() {
            if (!shareEl) return;
            var txt = '';
            try { txt = window.Bridge.getShareInfo(); } catch (e) {}
            shareEl.textContent = txt || '未连接 WiFi';
        }
        if (shareEl) {
            syncShareAddr();
            document.addEventListener('visibilitychange', function () {
                if (!document.hidden) syncShareAddr();
            });
        }

        window.Bridge.start();

        // 网络状态感知：根据原生推送的 network 事件更新顶栏提示，
        // 让用户在移动数据/无网络时清楚为何看不到其他设备。
        initNetworkStatus();
    }

    // 网络状态展示：移动数据/无网络时提示"不在局域网"，WiFi/热点时显示已发现设备。
    function initNetworkStatus() {
        var statusEl = document.getElementById('peerStatus');
        var networkPollTimer = null;
        var lastNetworkState = null;
        var scanRetryCount = 0;
        var MAX_SCAN_RETRIES = 3;

        function render(state, peerCount) {
            if (!statusEl) return;
            if (!state) return;
            var type = state.type;
            if (type === 'Mobile') {
                statusEl.textContent = '移动数据·未扫描局域网';
                statusEl.className = 'topbar-status warn';
            } else if (type === 'None' || !state.scan) {
                statusEl.textContent = '无网络·未扫描局域网';
                statusEl.className = 'topbar-status warn';
            } else {
                var label = (type === 'Hotspot') ? '热点局域网' : 'WiFi局域网';
                statusEl.textContent = label + '·发现 ' + (peerCount || 0) + ' 台设备';
                statusEl.className = 'topbar-status ok';
            }
        }

        function getNetworkState() {
            try {
                return window.Bridge.getNetworkState();
            } catch (e) {
                return null;
            }
        }

        function getPeers() {
            try {
                return window.Bridge.getPeers() || [];
            } catch (e) {
                return [];
            }
        }

        function refreshNetworkState() {
            var state = getNetworkState();
            var peers = getPeers();
            render(state, peers.length);
            
            // 检测网络类型变化，主动触发扫描
            if (lastNetworkState && state && lastNetworkState.type !== state.type) {
                scanRetryCount = 0;
                triggerScan();
            }
            lastNetworkState = state;
        }

        function triggerScan() {
            if (scanRetryCount >= MAX_SCAN_RETRIES) return;
            scanRetryCount++;
            try {
                if (window.Bridge && window.Bridge.startScan) {
                    window.Bridge.startScan();
                }
            } catch (e) {}
        }

        // 原生网络事件推送
        window.Bridge.on('network', function (state) {
            scanRetryCount = 0; // 重置重试计数
            refreshNetworkState();
        });

        // 兜底轮询：每 10 秒主动查询一次，防止事件丢失
        networkPollTimer = setInterval(refreshNetworkState, 10000);

        // 页面可见性变化时强制刷新
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                scanRetryCount = 0;
                refreshNetworkState();
            }
        });

        // 初始查询
        refreshNetworkState();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
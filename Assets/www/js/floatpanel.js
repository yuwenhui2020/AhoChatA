/**
 * 悬浮窗设置面板（原 bot.js 中的浮窗部分，机器人功能已移除）。
 *
 * 只负责：顶栏浮窗按钮 → 打开面板；开关悬浮窗；权限引导；从系统设置返回后复查。
 */
window.FloatPanel = (function () {
    'use strict';

    var overlay, toggleBtn, permBtn, hint;

    function el(id) { return document.getElementById(id); }

    function renderFloat() {
        if (!toggleBtn) return;
        var running = false;
        try { running = window.Bridge.isFloatingWindowOn(); } catch (e) { }
        var hasPerm = false;
        try { hasPerm = window.Bridge.hasOverlayPermission(); } catch (e) { }

        toggleBtn.classList.toggle('on', running);
        if (running) {
            hint.textContent = '悬浮窗运行中：可拖拽浮标，点击展开面板';
            permBtn.classList.add('hidden');
        } else if (hasPerm) {
            hint.textContent = '已授予悬浮窗权限，打开开关即可显示';
            permBtn.classList.add('hidden');
        } else {
            hint.textContent = '尚未授予悬浮窗权限，请先授权';
            permBtn.classList.remove('hidden');
        }
        // 更新开关的 aria 状态
        toggleBtn.setAttribute('aria-pressed', running ? 'true' : 'false');
    }

    function setFloat(on) {
        try { window.Bridge.setFloatingWindow(on); } catch (e) { }
        setTimeout(renderFloat, 600);
    }

    function open() {
        if (!overlay) return;
        overlay.classList.remove('hidden');
        overlay.classList.add('show');
        try { window.App && window.App.pushOverlay && window.App.pushOverlay('botOverlay'); } catch (e) { }
        renderFloat();
    }

    function close() {
        if (!overlay) return;
        overlay.classList.add('hidden');
        overlay.classList.remove('show');
        try { window.App && window.App.popOverlay && window.App.popOverlay('botOverlay'); } catch (e) { }
    }

    function init() {
        overlay = el('botOverlay');
        toggleBtn = el('floatToggleBtn');
        permBtn = el('floatPermBtn');
        hint = el('floatPermHint');

        var topBtn = el('btnFloat');
        if (topBtn) topBtn.addEventListener('click', open);

        var closeBtn = el('botClose');
        if (closeBtn) closeBtn.addEventListener('click', close);

        if (toggleBtn) toggleBtn.addEventListener('click', function () {
            setFloat(!toggleBtn.classList.contains('on'));
        });

        if (permBtn) permBtn.addEventListener('click', function () {
            try { window.Bridge.requestOverlayPermission(); } catch (e) { }
            var tries = 0;
            var timer = setInterval(function () {
                tries++;
                var ok = false;
                try { ok = window.Bridge.hasOverlayPermission(); } catch (e) { }
                if (ok) { clearInterval(timer); setFloat(true); }
                else if (tries > 20) { clearInterval(timer); }
            }, 1000);
        });

        // 面板打开期间高频轮询（每 500ms），关闭后停止
        var pollTimer = null;
        var isPanelOpen = false;

        function startPoll() {
            if (pollTimer) return;
            pollTimer = setInterval(function () {
                if (isPanelOpen) renderFloat();
            }, 500);
        }

        function stopPoll() {
            if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
        }

        // 监听面板显示/隐藏
        var originalOpen = open;
        open = function () {
            originalOpen();
            isPanelOpen = true;
            startPoll();
        };

        var originalClose = close;
        close = function () {
            originalClose();
            isPanelOpen = false;
            stopPoll();
        };

        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) renderFloat();
        });

        renderFloat();
    }

    return { init: init, open: open, close: close, render: renderFloat };
})();

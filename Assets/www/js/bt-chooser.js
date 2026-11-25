/*
 * BLE 设备选择弹窗（主框架 app.local 内运行）
 *
 * polyfill（任意网页）调用 navigator.bluetooth.requestDevice 时，最终会在主框架
 * 触发原生 AndroidBluetoothBridge.requestDevice，原生再调用本文件暴露的
 * window.__BtOpenChooser(reqId) 显示设备列表；扫描到的设备通过
 * window.__btAddDevice 累积，用户点选后调用 AndroidBluetoothBridge.selectDevice
 * 完成选择。整个过程对网页透明，符合标准 Web Bluetooth 体验。
 */
(function () {
    'use strict';

    var devices = {};      // id -> {id,name}
    var currentReqId = null;

    function el(id) { return document.getElementById(id); }

    function render() {
        var list = el('btDeviceList');
        if (!list) return;
        var ids = Object.keys(devices);
        if (!ids.length) {
            list.innerHTML = '<li class="bt-empty">未发现设备，请稍候…</li>';
            return;
        }
        list.innerHTML = '';
        ids.forEach(function (id) {
            var d = devices[id];
            var li = document.createElement('li');
            li.className = 'bt-item';
            li.innerHTML = '<span class="bt-name">' + escapeHtml(d.name || '(未知)') +
                '</span><span class="bt-addr">' + escapeHtml(id) + '</span>';
            li.addEventListener('click', function () {
                choose(d.id);
            });
            list.appendChild(li);
        });
    }

    function openChooser(reqId) {
        currentReqId = reqId;
        devices = {};
        var overlay = el('btChooser');
        if (overlay) { overlay.classList.remove('hidden'); }
        var hint = el('btScanHint');
        if (hint) hint.textContent = '正在扫描…';
        render();
    }

    function addDevice(info) {
        if (!info || !info.id) return;
        devices[info.id] = { id: info.id, name: info.name || '' };
        render();
    }

    function scanDone() {
        var hint = el('btScanHint');
        if (hint) hint.textContent = '扫描结束，点击设备连接（可重新扫描）';
    }

    function choose(deviceId) {
        var reqId = currentReqId;
        closeChooser();
        if (reqId && typeof AndroidBluetoothBridge !== 'undefined') {
            try { AndroidBluetoothBridge.selectDevice(reqId, deviceId); }
            catch (e) { /* ignore */ }
        }
    }

    function cancel() {
        var reqId = currentReqId;
        closeChooser();
        if (reqId && typeof AndroidBluetoothBridge !== 'undefined') {
            try { AndroidBluetoothBridge.selectDevice(reqId, ''); }
            catch (e) { /* ignore */ }
        }
    }

    function closeChooser() {
        currentReqId = null;
        var overlay = el('btChooser');
        if (overlay) overlay.classList.add('hidden');
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    // 供原生 WebBluetoothBridge 调用
    window.__BtOpenChooser = openChooser;
    window.__btAddDevice = addDevice;
    window.__btScanDone = scanDone;

    // 绑定弹窗内按钮
    document.addEventListener('DOMContentLoaded', function () {
        var rescan = el('btRescan');
        var cancelBtn = el('btCancel');
        if (rescan) rescan.addEventListener('click', function () {
            if (currentReqId && typeof AndroidBluetoothBridge !== 'undefined') {
                try {
                    devices = {};
                    render();
                    var hint = el('btScanHint');
                    if (hint) hint.textContent = '正在扫描…';
                    AndroidBluetoothBridge.requestDevice(currentReqId, '{}');
                } catch (e) {}
            }
        });
        if (cancelBtn) cancelBtn.addEventListener('click', cancel);
        var overlay = el('btChooser');
        if (overlay) overlay.addEventListener('click', function (e) {
            if (e.target === overlay) cancel();
        });
    });
})();

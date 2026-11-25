/*
 * Web Bluetooth Polyfill（标准 navigator.bluetooth 实现）
 *
 * 设计目标：让"任意加载的网页"（主框架、内置浏览器 iframe、外部在线网页）都能
 * 直接使用标准的 Web Bluetooth 接口（requestDevice / GATT 读写 / 通知），
 * 行为与 Chrome / Edge 中一致。
 *
 * 通信方式：
 *  - 顶层框架（window === window.parent）且存在原生 AndroidBluetoothBridge 时，
 *    直接调用原生接口（最快、最稳）。
 *  - 子框架（在线网页通常加载在 iframe 中，无法访问 @JavascriptInterface）通过
 *    parent.postMessage 把请求转发给顶层框架，由顶层框架代理调用原生并把结果回传。
 *
 * 该脚本由原生侧通过 <script src="/__btpolyfill__.js"> 注入到每个文档的 <head>，
 * 因此每个 frame（含跨域 iframe，只要 HTML 经代理注入）都能拿到真正的
 * navigator.bluetooth，无需后续 eval 扫描，也不受 CSP 限制。
 */
(function () {
    'use strict';

    if (window.navigator && window.navigator.bluetooth) {
        // 已经是标准环境或已被注入，避免重复
        return;
    }

    var isTop = (window === window.parent);
    var hasNative = (typeof AndroidBluetoothBridge !== 'undefined') && !!AndroidBluetoothBridge;
    // 子框架或原生接口不可用时，借助顶层框架代理
    var useProxy = (!isTop) || (!hasNative);

    var pending = {};
    var seq = 0;
    function nextId() { return 'wb_' + (++seq) + '_' + Date.now(); }

    // 主框架：直接调用原生接口；子框架：把调用转发给 parent
    function invoke(method, args) {
        if (useProxy) {
            return new Promise(function (resolve, reject) {
                var id = nextId();
                pending[id] = { resolve: resolve, reject: reject };
                try {
                    parent.postMessage({
                        __wbProxy: true, kind: 'invoke', id: id,
                        method: method, args: args || []
                    }, '*');
                } catch (e) {
                    delete pending[id];
                    reject(new DOMException('转发到宿主失败', 'NetworkError'));
                }
            });
        }
        return new Promise(function (resolve, reject) {
            var id = nextId();
            pending[id] = { resolve: resolve, reject: reject };
            try {
                AndroidBluetoothBridge[method].apply(AndroidBluetoothBridge, [id].concat(args || []));
            } catch (e) {
                delete pending[id];
                reject(new DOMException((e && e.message) || String(e), 'NetworkError'));
            }
        });
    }

    // 原生侧通过 EvaluateJavascript 在顶层框架调用这些全局函数兑现 Promise
    window.__wbResolve = function (id, payloadStr) {
        var p = pending[id]; if (!p) return; delete pending[id];
        var data = null;
        try { data = payloadStr ? JSON.parse(payloadStr) : null; } catch (e) {}
        p.resolve(data);
    };
    window.__wbReject = function (id, payloadStr) {
        var p = pending[id]; if (!p) return; delete pending[id];
        var err;
        try {
            var o = JSON.parse(payloadStr);
            err = new DOMException(o.message || '操作失败', o.name || 'NetworkError');
        } catch (e) {
            err = new DOMException('操作失败', 'NetworkError');
        }
        p.reject(err);
    };

    // 特征值注册表: deviceId -> (charUuid -> Set<BluetoothRemoteGATTCharacteristic>)
    var charRegistry = {};
    function registerChar(deviceId, ch) {
        if (!charRegistry[deviceId]) charRegistry[deviceId] = {};
        var m = charRegistry[deviceId];
        if (!m[ch.uuid]) m[ch.uuid] = new Set();
        m[ch.uuid].add(ch);
    }
    var deviceRegistry = {};

    function base64ToBuffer(b64) {
        var bin = atob(b64 || '');
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes.buffer;
    }
    function bufferToBase64(value) {
        var bytes;
        if (value instanceof ArrayBuffer) bytes = new Uint8Array(value);
        else if (ArrayBuffer.isView(value)) bytes = new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
        else throw new DOMException('无效的数据类型', 'TypeError');
        var bin = '';
        for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        return btoa(bin);
    }
    function normUuid(x) {
        if (typeof x === 'number') return '0x' + x.toString(16);
        return String(x);
    }

    function doNotify(deviceId, charUuid, base64) {
        var m = charRegistry[deviceId]; if (!m) return;
        var set = m[charUuid]; if (!set) return;
        var buf = base64ToBuffer(base64);
        set.forEach(function (ch) {
            ch.value = new DataView(buf.slice(0));
            ch.dispatchEvent(new Event('characteristicvaluechanged'));
        });
    }
    function doDisconnected(deviceId) {
        var d = deviceRegistry[deviceId];
        if (d) {
            d.gatt.connected = false;
            d.dispatchEvent(new Event('gattserverdisconnected'));
        }
    }

    // 顶层框架把通知 / 断开事件广播给所有子 iframe（在线网页）
    function broadcastNotify(deviceId, charUuid, base64) {
        if (!(isTop && hasNative)) return;
        var frames = document.querySelectorAll('iframe');
        for (var fi = 0; fi < frames.length; fi++) {
            try {
                frames[fi].contentWindow.postMessage({
                    __wbProxy: true, kind: 'notify', deviceId: deviceId, charUuid: charUuid, base64: base64
                }, '*');
            } catch (e) {}
        }
    }
    function broadcastDisconnected(deviceId) {
        if (!(isTop && hasNative)) return;
        var frames = document.querySelectorAll('iframe');
        for (var fi = 0; fi < frames.length; fi++) {
            try {
                frames[fi].contentWindow.postMessage({
                    __wbProxy: true, kind: 'disconnected', deviceId: deviceId
                }, '*');
            } catch (e) {}
        }
    }

    window.__wbNotify = function (deviceId, charUuid, base64) {
        doNotify(deviceId, charUuid, base64);
        broadcastNotify(deviceId, charUuid, base64);
    };
    window.__wbNotifyBatch = function (items) {
        if (!items || !items.length) return;
        for (var i = 0; i < items.length; i++) {
            var it = items[i];
            if (it && it.length === 3) window.__wbNotify(it[0], it[1], it[2]);
        }
    };
    window.__wbDisconnected = function (deviceId) {
        doDisconnected(deviceId);
        broadcastDisconnected(deviceId);
    };

    // 顶层框架代理：接收子 frame 转发的 GATT 调用，用原生 bridge 执行并回传
    if (isTop && hasNative) {
        window.addEventListener('message', function (e) {
            var d = e.data;
            if (!d || !d.__wbProxy || d.kind !== 'invoke') return;
            var nid = nextId();
            pending[nid] = {
                resolve: function (v) {
                    var payload = (v == null) ? '' : JSON.stringify(v);
                    try { e.source && e.source.postMessage({ __wbProxy: true, kind: 'resp', id: d.id, ok: true, payload: payload }, '*'); } catch (err) {}
                },
                reject: function (err) {
                    var payload = JSON.stringify({ name: (err && err.name) || 'NetworkError', message: (err && err.message) || String(err) });
                    try { e.source && e.source.postMessage({ __wbProxy: true, kind: 'resp', id: d.id, ok: false, payload: payload }, '*'); } catch (er2) {}
                }
            };
            try {
                AndroidBluetoothBridge[d.method].apply(AndroidBluetoothBridge, [nid].concat(d.args || []));
            } catch (err) {
                delete pending[nid];
                var payload = JSON.stringify({ name: 'NetworkError', message: (err && err.message) || String(err) });
                try { e.source && e.source.postMessage({ __wbProxy: true, kind: 'resp', id: d.id, ok: false, payload: payload }, '*'); } catch (er2) {}
            }
        });
    }

    // 子框架：接收顶层代理回传的结果与通知/断开事件
    if (useProxy) {
        window.addEventListener('message', function (e) {
            var d = e.data;
            if (!d || !d.__wbProxy) return;
            if (d.kind === 'resp') {
                var p = pending[d.id]; if (!p) return; delete pending[d.id];
                if (d.ok) {
                    var data = null; try { data = d.payload ? JSON.parse(d.payload) : null; } catch (ex) {}
                    p.resolve(data);
                } else {
                    var o = null; try { o = JSON.parse(d.payload); } catch (ex) {}
                    p.reject(new DOMException((o && o.message) || '操作失败', (o && o.name) || 'NetworkError'));
                }
            } else if (d.kind === 'notify') {
                doNotify(d.deviceId, d.charUuid, d.base64);
            } else if (d.kind === 'disconnected') {
                doDisconnected(d.deviceId);
            }
        });
    }

    function BluetoothRemoteGATTCharacteristic(service, info) {
        EventTarget.call(this);
        this.service = service;
        this.uuid = info.uuid;
        this.properties = info.properties || {};
        this.value = null;
        registerChar(service.device.id, this);
    }
    BluetoothRemoteGATTCharacteristic.prototype = Object.create(EventTarget.prototype);
    BluetoothRemoteGATTCharacteristic.prototype.constructor = BluetoothRemoteGATTCharacteristic;
    BluetoothRemoteGATTCharacteristic.prototype.readValue = function () {
        var self = this;
        return invoke('readValue', [this.service.device.id, this.service.uuid, this.uuid]).then(function (r) {
            var buf = base64ToBuffer(r.value);
            self.value = new DataView(buf);
            return self.value;
        });
    };
    BluetoothRemoteGATTCharacteristic.prototype.writeValue = function (value) {
        return invoke('writeValue', [this.service.device.id, this.service.uuid, this.uuid, bufferToBase64(value), true]);
    };
    BluetoothRemoteGATTCharacteristic.prototype.writeValueWithResponse = function (value) {
        return invoke('writeValue', [this.service.device.id, this.service.uuid, this.uuid, bufferToBase64(value), true]);
    };
    BluetoothRemoteGATTCharacteristic.prototype.writeValueWithoutResponse = function (value) {
        return invoke('writeValue', [this.service.device.id, this.service.uuid, this.uuid, bufferToBase64(value), false]);
    };
    BluetoothRemoteGATTCharacteristic.prototype.startNotifications = function () {
        var self = this;
        return invoke('startNotifications', [this.service.device.id, this.service.uuid, this.uuid]).then(function () { return self; });
    };
    BluetoothRemoteGATTCharacteristic.prototype.stopNotifications = function () {
        var self = this;
        return invoke('stopNotifications', [this.service.device.id, this.service.uuid, this.uuid]).then(function () { return self; });
    };

    function BluetoothRemoteGATTService(device, info) {
        this.device = device;
        this.uuid = info.uuid;
        this.isPrimary = info.isPrimary;
    }
    BluetoothRemoteGATTService.prototype.getCharacteristic = function (characteristic) {
        var self = this;
        var u = normUuid(characteristic);
        return invoke('getCharacteristics', [this.device.id, this.uuid, u]).then(function (list) {
            if (!list || !list.length) throw new DOMException('Characteristic not found', 'NotFoundError');
            return new BluetoothRemoteGATTCharacteristic(self, list[0]);
        });
    };
    BluetoothRemoteGATTService.prototype.getCharacteristics = function (characteristic) {
        var self = this;
        var u = characteristic ? normUuid(characteristic) : '';
        return invoke('getCharacteristics', [this.device.id, this.uuid, u]).then(function (list) {
            return (list || []).map(function (i) { return new BluetoothRemoteGATTCharacteristic(self, i); });
        });
    };

    function BluetoothRemoteGATTServer(device) {
        this.device = device;
        this.connected = false;
    }
    BluetoothRemoteGATTServer.prototype.connect = function () {
        var self = this;
        return invoke('connect', [this.device.id]).then(function () {
            self.connected = true;
            return self;
        });
    };
    BluetoothRemoteGATTServer.prototype.disconnect = function () {
        this.connected = false;
        invoke('disconnect', [this.device.id]);
    };
    BluetoothRemoteGATTServer.prototype.getPrimaryService = function (service) {
        var self = this;
        var u = normUuid(service);
        return invoke('getPrimaryServices', [this.device.id, u]).then(function (list) {
            if (!list || !list.length) throw new DOMException('Service not found', 'NotFoundError');
            return new BluetoothRemoteGATTService(self.device, list[0]);
        });
    };
    BluetoothRemoteGATTServer.prototype.getPrimaryServices = function (service) {
        var self = this;
        var u = service ? normUuid(service) : '';
        return invoke('getPrimaryServices', [this.device.id, u]).then(function (list) {
            return (list || []).map(function (i) { return new BluetoothRemoteGATTService(self.device, i); });
        });
    };

    function BluetoothDevice(info) {
        EventTarget.call(this);
        this.id = info.id;
        this.name = info.name || undefined;
        this.gatt = new BluetoothRemoteGATTServer(this);
        deviceRegistry[this.id] = this;
    }
    BluetoothDevice.prototype = Object.create(EventTarget.prototype);
    BluetoothDevice.prototype.constructor = BluetoothDevice;

    var bluetoothAPI = {
        requestDevice: function (options) {
            return invoke('requestDevice', [JSON.stringify(options || {})]).then(function (info) {
                return new BluetoothDevice(info);
            });
        },
        getAvailability: function () { return Promise.resolve(true); },
        getDevices: function () {
            return Promise.resolve(Object.keys(deviceRegistry).map(function (k) { return deviceRegistry[k]; }));
        },
        addEventListener: function () {},
        removeEventListener: function () {}
    };

    try {
        Object.defineProperty(navigator, 'bluetooth', {
            value: bluetoothAPI,
            configurable: true,
            enumerable: true
        });
        console.log('Web Bluetooth Polyfill: navigator.bluetooth 已就绪');
        window.dispatchEvent(new CustomEvent('bluetoothpolyfillready'));
    } catch (e) {
        // navigator.bluetooth 可能被只读属性锁定，此时尝试直接赋值
        try { navigator.bluetooth = bluetoothAPI; } catch (e2) {}
    }
})();

/* GPS 共享位置地图（Android WebView 版 / 局域网网页版，数据源 = /api/gps/state）。
   渲染算法与手机端原生雷达一致：以"自己"朝向为正前方、整图随朝向旋转，显示直线距离/高度差/双方朝向。
   电脑端无本机 GPS：self 为空时，以第一个上报位置的手机作为"虚拟自己"（显示锚点）。
   点"请求位置"可让在线手机连续上报位置。 */
(function () {
    'use strict';
    var state = { self: null, peers: {}, selected: null, sharing: false, scale: 1, autoFit: true };
    var el = {};
    function $(id) { return document.getElementById(id); }

    var API = '/api/gps';
    var Source = {
        start: function () { fetch(API + '/start', { method: 'POST' }).catch(function () {}); },
        stop: function () { state.autoFit = true; draw(); },
        bind: function (onSelf, onPeer, onError) {
            var anchorNotified = false;
            function poll() {
                fetch(API + '/state').then(function (r) { return r.json(); }).then(function (s) {
                    if (!s || s.error) { onError({ message: (s && s.error) || '位置接口不可用' }); return; }
                    var peers = s.peers || [];
                    state.peers = {};
                    var self = s.self;
                    if (!self && peers.length > 0) {
                        self = peers[0];
                        if (!anchorNotified) { anchorNotified = true; el.hint && (el.hintText.textContent = '本机无 GPS · 以 ' + (self.name || self.from) + ' 为参照中心'); }
                    }
                    if (self) onSelf(self); else onError({ message: '等待手机上报位置…点击"开启共享"或"请求位置"' });
                    peers.forEach(function (p) { if (!self || p.from !== self.from) onPeer(p); });
                    draw();
                }).catch(function (e) { onError({ message: '无法连接位置服务：' + e.message }); });
            }
            poll(); setInterval(poll, 1500); return true;
        }
    };
    if (window.GpsSource) { for (var k in window.GpsSource) { if (window.GpsSource.hasOwnProperty(k)) Source[k] = window.GpsSource[k]; } }

    function bearingOf(self, peer) {
        var dn = (peer.lat - self.lat) * 111320, de = (peer.lon - self.lon) * 111320 * Math.cos(self.lat * Math.PI / 180);
        return (Math.atan2(de, dn) * 180 / Math.PI + 360) % 360;
    }
    function metrics(self, peer) {
        var dn = (peer.lat - self.lat) * 111320, de = (peer.lon - self.lon) * 111320 * Math.cos(self.lat * Math.PI / 180);
        var dh = (peer.alt || 0) - (self.alt || 0);
        return { dn: dn, de: de, dh: dh, horiz: Math.sqrt(dn * dn + de * de),
            straight: Math.sqrt(dn * dn + de * de + dh * dh), bearing: bearingOf(self, peer) };
    }
    function toSelfFrame(dn, de, h) { var t = h * Math.PI / 180, c = Math.cos(t), s = Math.sin(t); return { forward: dn * c + de * s, right: -dn * s + de * c }; }

    var BC = { red: [231, 76, 60], yellow: [245, 197, 24], green: [46, 204, 113], gray: [154, 163, 175] };
    function mix(a, b, t) { t = Math.max(0, Math.min(1, t)); return [Math.round(a[0] + (b[0] - a[0]) * t), Math.round(a[1] + (b[1] - a[1]) * t), Math.round(a[2] + (b[2] - a[2]) * t)]; }
    function rgb(c) { return 'rgb(' + c[0] + ',' + c[1] + ',' + c[2] + ')'; }
    function batteryColor(pct) {
        if (pct === undefined || pct === null || pct < 0 || !isFinite(pct)) return rgb(BC.gray);
        if (pct < 30) return rgb(BC.red);
        if (pct < 50) return rgb(mix(BC.red, BC.yellow, (pct - 30) / 20));
        if (pct < 60) return rgb(BC.yellow);
        if (pct < 80) return rgb(mix(BC.yellow, BC.green, (pct - 60) / 20));
        return rgb(BC.green);
    }

    function draw() {
        var cv = el.canvas, ctx = cv.getContext('2d');
        var dpr = window.devicePixelRatio || 1, W = cv.clientWidth, H = cv.clientHeight;
        if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, W, H);
        if (!state.self) { el.hint.style.display = 'flex'; return; }
        el.hint.style.display = 'none';
        var cx = W / 2, cy = H / 2, heading = state.self.bearing || 0;
        var maxR = Math.min(cx, cy) - 20;
        var rings = 4; var ringR = maxR / rings;
        ctx.strokeStyle = '#2a3444'; ctx.lineWidth = 1;
        for (var i = 1; i <= rings; i++) { ctx.beginPath(); ctx.arc(cx, cy, i * ringR, 0, 2 * Math.PI); ctx.stroke(); }
        ctx.beginPath(); ctx.moveTo(cx, cy - maxR); ctx.lineTo(cx, cy + maxR); ctx.stroke();
        ctx.beginPath(); ctx.moveTo(cx - maxR, cy); ctx.lineTo(cx + maxR, cy); ctx.stroke();
        ctx.fillStyle = '#4a5568'; ctx.font = '10px sans-serif'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ['N','E','S','W'].forEach(function(dir,i){ var a=i*Math.PI/2-heading*Math.PI/180; var r=maxR+14; ctx.fillText(dir,cx+Math.sin(a)*r,cy-Math.cos(a)*r); });
        Object.keys(state.peers).forEach(function (k) {
            var p = state.peers[k], m = metrics(state.self, p), f = toSelfFrame(m.dn, m.de, heading);
            var px = cx + f.right * state.scale, py = cy - f.forward * state.scale;
            var isSelf = (state.self.from && p.from === state.self.from);
            ctx.beginPath(); ctx.arc(px, py, isSelf ? 10 : 8, 0, 2 * Math.PI);
            ctx.fillStyle = isSelf ? '#7dd8ff' : '#ffd54f'; ctx.fill();
            ctx.strokeStyle = isSelf ? '#5ab0d8' : '#d4a83a'; ctx.lineWidth = 2; ctx.stroke();
            if (state.selected === k) { ctx.strokeStyle = '#fff'; ctx.lineWidth = 3; ctx.stroke(); }
            if (m.bearing !== undefined && m.bearing !== null) {
                var bh = (m.bearing - heading + 360) % 360;
                var br = isSelf ? 14 : 12;
                var bx = px + Math.sin(bh * Math.PI / 180) * br;
                var by = py - Math.cos(bh * Math.PI / 180) * br;
                ctx.beginPath(); ctx.moveTo(px, py); ctx.lineTo(bx, by); ctx.strokeStyle = isSelf ? '#7dd8ff' : '#ffd54f'; ctx.lineWidth = 2; ctx.stroke();
            }
        });
    }

    function showDetail(k) {
        var p = state.peers[k]; if (!p) return;
        var m = metrics(state.self, p);
        var html = '<div style="padding:12px;line-height:1.8">';
        html += '<div><b>' + (p.name || p.from) + '</b> ' + (p.from === state.self.from ? '<span style="color:#7dd8ff">(自己)</span>' : '') + '</div>';
        html += '<div>坐标: ' + p.lat.toFixed(6) + ', ' + p.lon.toFixed(6) + (p.alt ? ' (' + Math.round(p.alt) + 'm)' : '') + '</div>';
        html += '<div>水平距离: ' + (m.horiz < 1000 ? m.horiz.toFixed(0) + 'm' : (m.horiz / 1000).toFixed(2) + 'km') + '</div>';
        if (p.alt) html += '<div>高度差: ' + (m.dh >= 0 ? '+' : '') + m.dh.toFixed(0) + 'm</div>';
        html += '<div>直线距离: ' + (m.straight < 1000 ? m.straight.toFixed(0) + 'm' : (m.straight / 1000).toFixed(2) + 'km') + '</div>';
        html += '<div>相对方位: ' + Math.round(m.bearing) + '°</div>';
        if (p.bearing) html += '<div>对方朝向: ' + Math.round(p.bearing) + '°</div>';
        if (p.battery >= 0) html += '<div>电量: ' + p.battery + '%</div>';
        html += '<div class="muted">精度: ' + (p.acc ? Math.round(p.acc) + 'm' : '未知') + ' · 更新: ' + new Date(p.time).toLocaleTimeString() + '</div>';
        html += '</div>';
        el.detail.innerHTML = html; el.detail.style.display = 'block';
    }

    function init() {
        el.canvas = $('canvas'); el.hint = $('hint'); el.hintText = $('hintText');
        el.btnShare = $('btnShare'); el.btnRequest = $('btnRequest');
        el.status = $('status'); el.peersPanel = $('peersPanel');
        el.detail = document.createElement('div'); el.detail.style.cssText = 'position:fixed;bottom:0;left:0;right:0;background:#1c2430;border-top:1px solid #3a4557;padding:0;max-height:40vh;overflow:auto;display:none;z-index:10'; document.body.appendChild(el.detail);

        Source.bind(
            function (self) { state.self = self; renderPeers(); },
            function (peer) { state.peers[peer.from] = peer; renderPeers(); },
            function (err) { el.status.textContent = err.message; el.status.className = 'status err'; }
        );

        el.btnShare.onclick = function () {
            if (state.sharing) { Source.stop(); state.sharing = false; el.btnShare.textContent = '开启共享'; el.btnShare.classList.remove('primary'); }
            else { Source.start(); state.sharing = true; el.btnShare.textContent = '关闭共享'; el.btnShare.classList.add('primary'); }
        };
        el.btnRequest.onclick = function () { fetch(API + '/request', { method: 'POST' }).catch(function () {}); };

        el.canvas.addEventListener('click', function (e) {
            if (!state.self) return; var r = el.canvas.getBoundingClientRect(), mx = e.clientX - r.left, my = e.clientY - r.top;
            var cx = el.canvas.clientWidth / 2, cy = el.canvas.clientHeight / 2, heading = state.self.bearing || 0, best = null, bestD = 34;
            Object.keys(state.peers).forEach(function (k) { var p = state.peers[k], m = metrics(state.self, p), f = toSelfFrame(m.dn, m.de, heading);
                var px = cx + f.right * state.scale, py = cy - f.forward * state.scale, d = Math.sqrt((px - mx) * (px - mx) + (py - my) * (py - my));
                if (d < bestD) { bestD = d; best = k; } });
            state.selected = best; if (best) showDetail(best); else el.detail.style.display = 'none'; draw();
        });
        addEventListener('resize', draw); draw();
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
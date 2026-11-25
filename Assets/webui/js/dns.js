// DNS 管理页面：API 使用 { ok, config/stats/logs }，兼容现有 PascalCase 字段。
let currentConfig = null;
let editingUpstreamIndex = -1;
let configBusy = false;
let otpRequest = null;

function showToast(msg) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.classList.add('show');
  setTimeout(() => t.classList.remove('show'), 2500);
}

function normalizeFields(value) {
  if (Array.isArray(value)) return value.map(normalizeFields);
  if (!value || typeof value !== 'object') return value;
  const result = Object.create(null);
  Object.keys(value).forEach(key => {
    result[key.charAt(0).toLowerCase() + key.slice(1)] = normalizeFields(value[key]);
  });
  return result;
}

async function api(path, opts = {}) {
  const options = { method: opts.method || 'GET', credentials: 'same-origin', cache: 'no-store' };
  // HttpListener 在 POST 无请求体时会返回 411；统一带上 JSON 体。
  if ((options.method || 'GET').toUpperCase() !== 'GET') {
    options.headers = { 'Content-Type': 'application/json' };
    options.body = opts.body !== undefined ? JSON.stringify(opts.body) : '{}';
  }
  let response = await fetch('/api/dns' + path, options);
  if (response.status === 401) {
    if (!otpRequest) {
      otpRequest = (async () => {
        const code = window.prompt('请输入 AhoChat「设备」页「本机」下方的 6 位动态口令：');
        if (!code) throw new Error('需要动态口令');
        const auth = await fetch('/__otp?code=' + encodeURIComponent(String(code).replace(/\D/g, '')) + '&r=/dns', { credentials: 'same-origin' });
        if (!auth.ok) throw new Error('口令验证失败');
      })().finally(() => { otpRequest = null; });
    }
    await otpRequest;
    response = await fetch('/api/dns' + path, options);
  }
  const data = normalizeFields(await response.json());
  if (!response.ok || data.ok === false) throw new Error(data.error || ('HTTP ' + response.status));
  return data;
}

function setSwitch(id, on) { document.getElementById(id).classList.toggle('on', !!on); }
function toggleSwitch(id, key) {
  if (!currentConfig || configBusy) return;
  const on = !document.getElementById(id).classList.contains('on');
  updateConfig({ [key]: on });
}

function acceptConfig(data) {
  if (!data.config) throw new Error('服务器未返回配置');
  currentConfig = data.config;
  if (typeof data.autoStart === 'boolean') currentConfig.autoStart = data.autoStart;
  applyConfig(currentConfig);
}

async function updateConfig(patch) {
  if (!currentConfig || configBusy) { showToast('请等待配置加载或保存完成'); return false; }
  configBusy = true;
  try {
    acceptConfig(await api('/config', { method: 'POST', body: patch }));
    await refreshStatus();
    showToast('配置已更新');
    return true;
  } catch (e) {
    showToast('更新失败: ' + e.message);
    await loadConfig();
    return false;
  } finally { configBusy = false; }
}

async function loadConfig() {
  try { acceptConfig(await api('/config')); }
  catch (e) { showToast('加载配置失败: ' + e.message); }
}

function applyConfig(cfg) {
  const switches = { cfgEnabled: 'enabled', cfgCache: 'enableCache', cfgLog: 'logQueries', cfgBlockAds: 'blockAds', cfgBlockTrackers: 'blockTrackers', cfgBlockMalware: 'blockMalware', cfgPreferSecure: 'preferSecureUpstream' };
  Object.keys(switches).forEach(id => setSwitch(id, cfg[switches[id]]));
  const fields = { cfgPort: ['port', 53], cfgTimeout: ['upstreamTimeoutMs', 5000], cfgCacheSize: ['cacheSize', 10000], cfgCacheTtl: ['cacheTtlSeconds', 300], cfgMaxLog: ['maxLogEntries', 10000] };
  Object.keys(fields).forEach(id => { const [key, fallback] = fields[id]; document.getElementById(id).value = cfg[key] ?? fallback; });
  document.getElementById('preferSecureRow').hidden = typeof cfg.preferSecureUpstream !== 'boolean';
  document.getElementById('autoStartRow').hidden = typeof cfg.autoStart !== 'boolean';
  document.getElementById('cfgAutoStart').checked = !!cfg.autoStart;
  renderUpstreams(cfg.upstreamServers || []);
  renderBlocklists(cfg.customBlocklists || []);
  renderAllowlists(cfg.customAllowlists || []);
  renderRewrites(cfg.rewrites || []);
}

// 数据只进入 textContent；操作通过事件绑定，避免地址、域名或日志成为 HTML/脚本。
function element(tag, text, className) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = String(text);
  if (className) el.className = className;
  return el;
}
function listContainer(id, emptyText, list, render) {
  const container = document.getElementById(id);
  container.replaceChildren();
  if (!list.length) container.appendChild(element('div', emptyText, 'empty'));
  list.forEach((item, index) => {
    const row = element('div', undefined, 'upstream-item');
    render(row, item, index);
    container.appendChild(row);
  });
}
function listText(row, text) {
  const span = element('span', text);
  span.style.cssText = 'flex:1;font-size:13px;color:#333;word-break:break-all;';
  row.appendChild(span);
}
function action(row, text, callback, danger = false) {
  const button = element('button', text, danger ? 'ghost danger' : 'ghost');
  button.style.cssText = 'padding:4px 8px;font-size:11px;';
  button.addEventListener('click', callback);
  row.appendChild(button);
}
function renderUpstreams(list) {
  listContainer('upstreamList', '暂无上游服务器', list, (row, upstream, i) => {
    const u = String(upstream);
    let type = 'udp', addr = u;
    if (u.startsWith('https://')) type = 'doh';
    else if (u.startsWith('tls://')) { type = 'dot'; addr = u.substring(6); }
    else if (u.startsWith('tcp://')) { type = 'tcp'; addr = u.substring(6); }
    row.appendChild(element('span', { udp: 'UDP', tcp: 'TCP', doh: 'DoH', dot: 'DoT' }[type], 'upstream-type ' + type));
    listText(row, addr);
    action(row, '编辑', () => editUpstream(i, type, addr, '', '/dns-query'));
    action(row, '删除', () => removeUpstream(i), true);
  });
}
function renderBlocklists(list) {
  listContainer('blocklistList', '暂无自定义屏蔽列表', list, (row, u, i) => { listText(row, u); action(row, '删除', () => removeBlocklist(i), true); });
}
function renderAllowlists(list) {
  listContainer('allowlistList', '暂无自定义允许列表', list, (row, u, i) => { listText(row, u); action(row, '删除', () => removeAllowlist(i), true); });
}
function renderRewrites(list) {
  listContainer('rewriteList', '暂无重写规则', list, (row, r, i) => {
    listText(row, r.domain + ' → ' + r.answer);
    row.appendChild(element('span', r.enabled ? '启用' : '禁用', 'badge ' + (r.enabled ? 'allowed' : 'blocked')));
    action(row, r.enabled ? '禁用' : '启用', () => toggleRewrite(i, !r.enabled));
    action(row, '删除', () => removeRewrite(i), true);
  });
}

async function saveConfig() {
  const patch = {};
  const fields = { cfgPort: 'port', cfgTimeout: 'upstreamTimeoutMs', cfgCacheSize: 'cacheSize', cfgCacheTtl: 'cacheTtlSeconds', cfgMaxLog: 'maxLogEntries' };
  for (const id of Object.keys(fields)) {
    const input = document.getElementById(id);
    if (!input.checkValidity() || !input.value || !Number.isInteger(Number(input.value))) { input.reportValidity(); showToast('请填写有效的整数配置'); return; }
    patch[fields[id]] = Number(input.value);
  }
  await updateConfig(patch);
}
function addListValue(key, inputId, label) {
  if (!currentConfig || configBusy) return;
  const input = document.getElementById(inputId), value = input.value.trim();
  if (!value) { showToast(label); return; }
  updateConfig({ [key]: (currentConfig[key] || []).concat(value) }).then(ok => { if (ok) input.value = ''; });
}
function removeListValue(key, index) {
  if (!currentConfig || configBusy) return;
  updateConfig({ [key]: (currentConfig[key] || []).filter((_, i) => i !== index) });
}
function addUpstream() { addListValue('upstreamServers', 'newUpstream', '请输入上游地址'); }
function removeUpstream(i) { removeListValue('upstreamServers', i); }
function addBlocklist() { addListValue('customBlocklists', 'newBlocklist', '请输入 URL'); }
function removeBlocklist(i) { removeListValue('customBlocklists', i); }
function addAllowlist() { addListValue('customAllowlists', 'newAllowlist', '请输入 URL'); }
function removeAllowlist(i) { removeListValue('customAllowlists', i); }
function editUpstream(index, type, addr, host, path) {
  editingUpstreamIndex = index;
  document.getElementById('modalUpstreamType').value = type;
  document.getElementById('modalUpstreamAddr').value = addr;
  document.getElementById('modalUpstreamPath').value = path || '/dns-query';
  document.getElementById('modalUpstream').classList.add('show');
}
function closeModal(id) { document.getElementById(id).classList.remove('show'); editingUpstreamIndex = -1; }
async function saveUpstream() {
  if (!currentConfig || configBusy) return;
  const type = document.getElementById('modalUpstreamType').value;
  const addr = document.getElementById('modalUpstreamAddr').value.trim();
  const path = document.getElementById('modalUpstreamPath').value.trim() || '/dns-query';
  if (!addr) { showToast('请输入地址'); return; }
  let fullAddr = addr;
  if (type === 'tcp') fullAddr = 'tcp://' + addr;
  else if (type === 'doh') fullAddr = addr.startsWith('https://') ? addr : 'https://' + addr + path;
  else if (type === 'dot') fullAddr = 'tls://' + addr;
  const upstreams = [...(currentConfig.upstreamServers || [])];
  if (editingUpstreamIndex >= 0) upstreams[editingUpstreamIndex] = fullAddr;
  else upstreams.push(fullAddr);
  if (await updateConfig({ upstreamServers: upstreams })) closeModal('modalUpstream');
}
function addRewrite() {
  if (!currentConfig || configBusy) return;
  const domain = document.getElementById('newRewriteDomain').value.trim();
  const answer = document.getElementById('newRewriteAnswer').value.trim();
  if (!domain || !answer) { showToast('请填写域名和 IP'); return; }
  updateConfig({ rewrites: (currentConfig.rewrites || []).concat({ domain, answer, enabled: true }) }).then(ok => {
    if (ok) { document.getElementById('newRewriteDomain').value = ''; document.getElementById('newRewriteAnswer').value = ''; }
  });
}
function removeRewrite(i) { removeListValue('rewrites', i); }
function toggleRewrite(index, enabled) {
  if (!currentConfig || configBusy) return;
  const list = (currentConfig.rewrites || []).map((r, i) => i === index ? { ...r, enabled } : r);
  updateConfig({ rewrites: list });
}
async function refreshStats() {
  try {
    const data = await api('/stats');
    if (data.stats) {
      const fields = { statTotal: 'totalQueries', statBlocked: 'blockedQueries', statCached: 'cachedQueries', statUpstream: 'upstreamQueries', statErrors: 'errors', statAvgMs: 'averageResponseTimeMs' };
      Object.keys(fields).forEach(id => { document.getElementById(id).textContent = data.stats[fields[id]] ?? 0; });
    } else if (data.error) {
      // DNS not started - show zeros and hint
      const fields = ['statTotal', 'statBlocked', 'statCached', 'statUpstream', 'statErrors', 'statAvgMs'];
      fields.forEach(id => { document.getElementById(id).textContent = '0'; });
      showToast(data.error);
    }
  } catch (e) {
    if (/DNS 服务未启动/.test(e.message || '')) {
      ['statTotal', 'statBlocked', 'statCached', 'statUpstream', 'statErrors', 'statAvgMs'].forEach(id => { document.getElementById(id).textContent = '0'; });
      return;
    }
    showToast('获取统计失败: ' + e.message);
  }
}
async function refreshStatus() {
  try {
    const status = await api('/status');
    const badge = document.getElementById('statusBadge'), button = document.getElementById('btnToggle');
    badge.textContent = status.running ? '运行中' : '已停止';
    badge.className = 'status-badge ' + (status.running ? 'running' : 'stopped');
    button.textContent = status.running ? '停止' : '启动';
    button.className = status.running ? 'danger' : 'ghost';
    button.dataset.running = String(!!status.running);
    document.getElementById('portDisplay').textContent = status.port ?? currentConfig?.port ?? 53;
    document.getElementById('modeDisplay').textContent = status.mode || 'Android VPN';
  } catch (e) { showToast('获取状态失败: ' + e.message); }
}
async function toggleDns() {
  const button = document.getElementById('btnToggle');
  if (button.disabled) return;
  button.disabled = true;
  try {
    await api(button.dataset.running === 'true' ? '/stop' : '/start', { method: 'POST' });
    // VPN 建立是异步的，状态立即读可能还是旧值，短暂轮询到稳定状态。
    for (let i = 0; i < 6; i++) {
      const s = await api('/status');
      if ((button.dataset.running !== 'true' && s.running) || (button.dataset.running === 'true' && !s.running)) break;
      await new Promise(resolve => setTimeout(resolve, 400));
    }
    await refreshStatus();
    await loadConfig();
  } catch (e) { showToast('操作失败: ' + e.message); }
  finally { button.disabled = false; }
}
async function refreshLog() {
  const limit = Number(document.getElementById('logLimit').value) || 100;
  try {
    const data = await api('/log?limit=' + limit);
    const logs = data.logs || [];
    const tbody = document.getElementById('logTableBody');
    tbody.replaceChildren();
    const emptyEl = document.getElementById('logEmpty');
    emptyEl.style.display = logs.length ? 'none' : 'block';
    if (data.error && !logs.length) {
      emptyEl.textContent = data.error;
    }
    const types = { 1: 'A', 2: 'NS', 5: 'CNAME', 6: 'SOA', 12: 'PTR', 15: 'MX', 16: 'TXT', 28: 'AAAA', 33: 'SRV', 65: 'HTTPS' };
    const codes = { 0: 'NoError', 1: 'FormatError', 2: 'ServerFailure', 3: 'NXDomain', 4: 'NotImplemented', 5: 'Refused' };
    logs.forEach(log => {
      const code = codes[log.responseCode] ?? log.responseCode;
      const status = log.blocked ? '已拦截' : code === 'NoError' ? '成功' : '错误';
      const row = element('tr');
      [new Date(log.timestamp).toLocaleTimeString(), log.clientIp, log.domain, types[log.type] ?? log.type, code, status, (log.durationMs ?? 0) + 'ms', log.upstreamServer || '-'].forEach(text => row.appendChild(element('td', text ?? '')));
      tbody.appendChild(row);
    });
  } catch (e) {
    const tbody = document.getElementById('logTableBody');
    if (tbody) tbody.replaceChildren();
    const emptyEl = document.getElementById('logEmpty');
    if (emptyEl) {
      emptyEl.style.display = 'block';
      emptyEl.textContent = /DNS 服务未启动/.test(e.message || '') ? (e.message || 'DNS 服务未启动') : ('获取日志失败: ' + e.message);
    }
    if (!/DNS 服务未启动/.test(e.message || '')) showToast('获取日志失败: ' + e.message);
  }
}

document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', () => {
  document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
  document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
  tab.classList.add('active');
  document.getElementById('panel-' + tab.dataset.tab).classList.add('active');
}));
async function init() {
  await loadConfig();
  await refreshStatus();
  await refreshStats();
  await refreshLog();
  setInterval(refreshStatus, 5000);
  setInterval(refreshStats, 10000);
  setInterval(refreshLog, 15000);
}
// 脚本位于 body 末尾；直接初始化，避免某些 WebView 下 DOMContentLoaded 已错过导致整页按钮失效。
// 脚本位于 body 末尾，DOM 已就绪；部分内置 WebView 下 DOMContentLoaded 不再触发，直接初始化。
init().catch(e => showToast('初始化失败: ' + (e && e.message ? e.message : e)));

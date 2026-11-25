/* ============================================================
   在线影视 · 网页端（TVBox 多渠道聚合）
   数据来自 /api/media/vod/*，与 App 端共用同一套 VodApi。
   m3u8 用本地 hls.min.js（离线可用），mp4 用原生 video；
   播放地址统一走本地代理，避开 CDN 的 UA/Referer 校验与浏览器 CORS。
   ============================================================ */
(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  const enc = encodeURIComponent;
  const M = () => window.LCMedia;
  const A = (p) => M().api(p);
  const toast = (m) => { try { M().toast(m); } catch (e) {} };

  const SVG = {
    film: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 5v14M17 5v14M3 9h4M3 15h4M17 9h4M17 15h4"/></svg>',
    grid: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/></svg>',
    gear: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="3.2"/><path d="M12 3.2v2.4M12 18.4v2.4M3.2 12h2.4M18.4 12h2.4M5.8 5.8l1.7 1.7M16.5 16.5l1.7 1.7M18.2 5.8l-1.7 1.7M7.5 16.5l-1.7 1.7"/></svg>',
    refresh: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M19.4 12a7.4 7.4 0 10-2.6 5.6"/><path d="M19.8 6.6v5.2h-5.2"/></svg>',
    history: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="8"/><path d="M12 7.5V12l3 2"/></svg>',
    folder: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"><path d="M3.5 6.5h6l2 2h9v10h-17z"/></svg>'
  };

  let items = [], cats = [], cfg = null, status = null;
  // 默认展示「电视剧」；分类列表顺序由后端固定为 电视剧 → 电影 → 其它
  let query = '', cat = '电视剧', page = 1, loading = false, cfgOpen = false, searchTimer = 0;
  let catsPartial = false, catsRetry = 0, warmTimer = 0, lastLoad = 0, loadAbort = 0;
  let detail = null, sources = [], episodes = [], line = 0, curSite = '', curId = '';
  let hls = null, playList = [], playIndex = -1, historyKey = '';
  // 观看记录瀑布墙（带封面），以及「一起看」宿主机上报状态
  let historyView = false, historyItems = [];
  let shareCardId = '', shareMeta = null, shareTimer = 0;

  /* ---------------- 视图渲染 ---------------- */

  function sub() {
    const s = status || {};
    return `${s.EnabledCount || 0}/${s.SiteCount || 0} 个源 · 已缓存 ${s.CachedItems || 0} 部`
      + (cat ? ' · ' + cat : '') + (query ? ' · 搜索「' + query + '」' : '');
  }

  function render() {
    if (historyView) return historyHtml();
    const tools = `<div class="vod-tools">
      <button data-vodact="history">${SVG.history} 观看历史</button>
      <button data-vodact="quick" data-cat="电视剧" class="${cat === '电视剧' ? 'on' : ''}">电视剧</button>
      <button data-vodact="quick" data-cat="电影" class="${cat === '电影' ? 'on' : ''}">电影</button>
      <button data-vodact="cats" class="${cat === '' ? 'on' : ''}">${SVG.folder} 全部</button>
      <span class="vod-stat">${esc(sub())}</span>
    </div>`;
    const panel = cfgOpen ? cfgHtml() : '';
    let body;
    if (loading && !items.length) body = '<div class="vod-empty">正在聚合多个影视源…</div>';
    else if (!items.length) body = `<div class="vod-empty">${query ? '没有找到「' + esc(query) + '」' : '下拉顶部刷新，或到「设置 → 源管理」启用更多渠道'}</div>`;
    else body = `<div class="vod-grid">${items.map(card).join('')}</div>`;
    // 顶部下拉刷新 / 底部上拉加载的提示条，由手势逻辑填充文字
    return tools + panel + body + `<div id="vodPtr" class="vod-ptr"></div><div id="vodPul" class="vod-pul"></div>`;
  }

  /* 观看记录：与影片列表同样的对齐卡片海报墙（封面来自外源 CDN，走宿主机海报代理） */
  function historyHtml() {
    const tools = `<div class="vod-tools">
      <button data-vodact="historyback">← 返回影片</button>
      <button data-vodact="historyclear">清空记录</button>
      <span class="vod-stat">共 ${historyItems.length} 条观看记录</span>
    </div>`;
    if (!historyItems.length) return tools + '<div class="vod-empty">还没有观看记录</div>';
    return tools + `<div class="vod-grid">${historyItems.map(historyCard).join('')}</div>`;
  }

  function historyCard(r, i) {
    const src = r.poster || (r.pic ? A('vod/poster?u=' + enc(r.pic)) : '');
    const img = src ? `<img loading="lazy" src="${esc(src)}" alt="" onerror="this.style.display='none'">` : '';
    const sub = [r.siteName, r.episodeName,
      r.positionMs > 0 ? '续播 ' + fmtMs(r.positionMs) : ''].filter(Boolean).join(' · ');
    return `<div class="vod-card" data-vodhis="${i}">
      <div class="vod-art">${img}
        <div class="ph">${SVG.film}</div>
        ${r.episodeName ? `<span class="tag">${esc(r.episodeName)}</span>` : ''}
      </div>
      <div class="vod-body">
        <div class="vod-name" title="${esc(r.name)}">${esc(r.name)}</div>
        <div class="vod-sub">${esc(sub)}</div>
      </div>
    </div>`;
  }

  function fmtMs(ms) {
    const s = Math.max(0, Math.floor(Number(ms || 0) / 1000));
    return String(Math.floor(s / 60)).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0');
  }

  function card(it, i) {
    const idx = items.indexOf(it);
    const tag = (it.variants && it.variants.length > 1) ? it.variants.length + ' 源'
      : (it.remarks || it.type || '');
    // 服务端对无海报 / CDN 失败会返回生成封面，所以每张卡都渲染图片
    const poster = it.poster || A('vod/poster?n=' + enc(it.name || ''));
    const img = `<img loading="lazy" src="${esc(poster)}" alt="" onerror="this.style.display='none'">`;
    return `<div class="vod-card" data-vodcard="${idx}">
      <div class="vod-art">${img}
        <div class="ph">${SVG.film}</div>
        ${tag ? `<span class="tag">${esc(tag)}</span>` : ''}
      </div>
      <div class="vod-body">
        <div class="vod-name" title="${esc(it.name)}">${esc(it.name)}</div>
        <div class="vod-sub">${esc([it.remarks, it.year, it.siteName].filter(Boolean).join(' · '))}</div>
      </div>
    </div>`;
  }

  function cfgHtml() {
    const c = cfg || {};
    const subs = c.subs || [];
    const sites = (status && status.Sites) || [];
    return `<div class="vod-cfg">
      <h4>源管理 · 多渠道聚合</h4>
      <div class="tip">视频源来自 TVBox 类公开订阅配置，仅接入 type 0/1 的苹果CMS JSON 采集站；
        多仓 urls[] 自动展开；订阅失败自动回退内置实测源。启用哪些源由你决定。</div>
      <div class="row">
        ${['builtin:内置', 'subs:订阅', 'custom:自定义'].map(x => {
          const [v, n] = x.split(':');
          return `<button data-vodact="mode" data-v="${v}" class="${c.mode === v ? 'on' : ''}">${n}</button>`;
        }).join('')}
        <button data-vodact="refreshsubs">${SVG.refresh} 立即刷新订阅</button>
        <button data-vodact="clearcache">清空缓存</button>
      </div>
      <div class="row">
        <input type="text" id="vodSubUrl" placeholder="粘贴 TVBox 订阅 JSON 地址">
        <button data-vodact="addsub">添加订阅</button>
      </div>
      <div class="row">
        <input type="text" id="vodCustomJson" placeholder="或直接粘贴整份 TVBox 配置 JSON">
        <button data-vodact="setcustom">保存为自定义配置</button>
      </div>
      <h4 style="margin-top:14px">订阅渠道</h4>
      ${subs.length ? subs.map((s, i) => `<div class="row">
        <label><input type="checkbox" data-sub="${i}" ${s.Enabled ? 'checked' : ''}><b>${esc(s.Name)}</b></label>
        <span style="font-size:11px;color:var(--sub);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(s.Url)}</span>
        <button data-vodact="delsub" data-i="${i}">移除</button>
      </div>`).join('') : '<div class="tip">暂无订阅</div>'}
      <h4 style="margin-top:14px">采集站点（${sites.length}）</h4>
      <div class="sites">
        ${sites.map((s, i) => `<label title="${esc(s.Api)}"><input type="checkbox" data-site="${i}" ${s.Enabled ? 'checked' : ''}><b>${esc(s.Name)}</b> · ${esc(s.Origin || '')}</label>`).join('')}
      </div>
    </div>`;
  }

  function bind() {
    const c = $('content');
    c.onclick = (e) => {
      const btn = e.target.closest('[data-vodact]');
      if (btn) { e.preventDefault(); onAction(btn.dataset.vodact, btn); return; }
      const cardEl = e.target.closest('[data-vodcard]');
      if (cardEl) { openDetail(items[Number(cardEl.dataset.vodcard)]); return; }
      const hisEl = e.target.closest('[data-vodhis]');
      if (hisEl) {
        const r = historyItems[Number(hisEl.dataset.vodhis)];
        if (r) openDetail({ site: r.siteKey, id: (r.key || '').split('|').pop(), name: r.name });
        return;
      }
    };
    c.onchange = (e) => {
      const t = e.target;
      if (t.dataset && t.dataset.sub !== undefined) {
        const s = (cfg.subs || [])[Number(t.dataset.sub)];
        if (s) postCfg('sub&url=' + enc(s.Url) + '&name=' + enc(s.Name) + '&on=' + (t.checked ? '1' : '0'));
      } else if (t.dataset && t.dataset.site !== undefined) {
        const s = ((status && status.Sites) || [])[Number(t.dataset.site)];
        if (s) postCfg('site&key=' + enc(s.Key) + '&on=' + (t.checked ? '1' : '0'));
      }
    };
    if (!cfg) loadCfg();
    bindPtr();
  }

  /* ---------------- 顶部下拉刷新 / 底部上拉加载 ---------------- */

  function toggleCfg() {
    cfgOpen = !cfgOpen;
    if (cfgOpen && !cfg) loadCfg();
    M().refresh();
  }

  let ptrBound = false;
  function bindPtr() {
    if (ptrBound) return;
    ptrBound = true;
    const el = $('content');
    if (!el) return;
    const setTop = (t) => { const e = $('vodPtr'); if (e) e.textContent = t || ''; };
    const setBot = (t) => { const e = $('vodPul'); if (e) e.textContent = t || ''; };
    let startY = 0, pulling = 0, mode = 0; // mode: 0 无 / 1 顶部下拉 / 2 底部上拉
    const TH = 60; // 触发阈值

    el.addEventListener('touchstart', (e) => {
      if (loading || historyView) return;
      startY = e.touches[0].clientY;
      const atTop = el.scrollTop <= 0;
      const atBot = el.scrollTop + el.clientHeight >= el.scrollHeight - 4;
      mode = atTop ? 1 : (atBot ? 2 : 0);
      if (atTop && atBot) mode = 1; // 内容不足一屏时优先刷新
      pulling = 0;
    }, { passive: true });

    el.addEventListener('touchmove', (e) => {
      if (!mode || loading) return;
      const dy = e.touches[0].clientY - startY;
      if (mode === 1 && dy > 0) {
        pulling = Math.min(dy, 140);
        setTop(pulling >= TH ? '释放立即刷新' : '下拉刷新');
        if (e.cancelable) e.preventDefault();
      } else if (mode === 2 && dy < 0) {
        pulling = Math.min(-dy, 140);
        setBot(pulling >= TH ? '释放加载更多' : '上拉加载更多');
        if (e.cancelable) e.preventDefault();
      } else {
        pulling = 0; setTop(''); setBot('');
      }
    }, { passive: false });

    const end = () => {
      if (!mode || loading) { pulling = 0; mode = 0; return; }
      const trig = pulling >= TH;
      const m = mode; pulling = 0; mode = 0;
      if (trig) {
        if (m === 1) { setTop('刷新中…'); lastLoad = Date.now(); refresh(); }
        else { setBot('加载中…'); lastLoad = Date.now(); load(false); }
      } else { setTop(''); setBot(''); }
    };
    el.addEventListener('touchend', end, { passive: true });
    el.addEventListener('touchcancel', end, { passive: true });

    // 桌面端兜底：滚动到底部自动加载更多（2 秒冷却，避免连环请求）
    el.addEventListener('scroll', () => {
      if (loading || mode || historyView) return;
      if (!items.length) return;
      const nearBot = el.scrollTop + el.clientHeight >= el.scrollHeight - 120;
      if (nearBot && Date.now() - lastLoad > 2000) {
        lastLoad = Date.now(); setBot('加载中…'); load(false);
      }
    });
  }

  function onAction(act, btn) {
    switch (act) {
      case 'quick': cat = btn.dataset.cat || ''; refresh(); break;
      case 'cats': showCats(); break;
      case 'history': showHistory(); break;
      case 'historyback': historyView = false; M().refresh(); break;
      case 'historyclear':
        fetch(A('vod/history?op=clear'), { method: 'POST' }).then(() => showHistory());
        break;
      case 'refresh': refresh(); break;
      case 'mode': postCfg('mode&v=' + enc(btn.dataset.v)); break;
      case 'refreshsubs': toast('正在后台刷新订阅…'); postCfg('refresh'); setTimeout(() => { loadCfg(); load(true); }, 2500); break;
      case 'clearcache': postCfg('clear'); setTimeout(() => load(true), 400); break;
      case 'addsub': {
        const u = ($('vodSubUrl') || {}).value || '';
        if (!/^https?:\/\//i.test(u.trim())) { toast('请填写 http(s) 开头的订阅地址'); return; }
        postCfg('sub&url=' + enc(u.trim()) + '&name=' + enc('自定义订阅') + '&on=1');
        toast('已添加订阅，点「立即刷新订阅」生效'); setTimeout(loadCfg, 500);
        break;
      }
      case 'setcustom': {
        const j = ($('vodCustomJson') || {}).value || '';
        if (!j.trim()) { toast('请先粘贴配置 JSON'); return; }
        postCfgRaw('custom', j).then(() => { toast('已保存自定义配置'); loadCfg(); load(true); });
        break;
      }
      case 'delsub': {
        const s = (cfg.subs || [])[Number(btn.dataset.i)];
        if (s) postCfg('subdel&url=' + enc(s.url)).then(loadCfg);
        break;
      }
    }
  }

  /* ---------------- 数据 ---------------- */

  async function postCfg(q) { return postCfgRaw(q, ''); }

  async function postCfgRaw(q, body) {
    try {
      const r = await fetch(A('vod/config?op=' + q), {
        method: 'POST', headers: { 'Content-Type': 'text/plain;charset=utf-8' }, body: body || ''
      });
      const d = await r.json();
      if (d.status) status = d.status;
      return d;
    } catch (e) { toast('配置保存失败'); return {}; }
  }

  async function loadCfg() {
    try {
      const r = await fetch(A('vod/config'));
      const d = await r.json();
      cfg = { mode: d.mode, subs: d.subs || [], custom: d.custom || '' };
      status = d.status || status;
    } catch (e) { cfg = { mode: 'builtin', subs: [] }; }
    if (cfgOpen) M().refresh();
  }

  function search(q) {
    query = (q || '').trim();
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => load(true), 250);
  }

  async function load(first) {
    // 连打关键词时取消上一次在途请求：旧结果不能覆盖新结果，也不该白占服务端线程
    if (loadAbort) { try { loadAbort.abort(); } catch (e) {} }
    const ac = window.AbortController ? new AbortController() : null;
    loadAbort = ac;
    loading = true;
    if (first) page = 1; else page++;
    if (first && M().getView() === 'vod') {
      const c = $('content');
      if (c && !items.length) c.innerHTML = '<div class="vod-empty">正在聚合多个影视源…</div>';
    }
    try {
      let url;
      if (query) url = A('vod/search?wd=' + enc(query) + '&page=' + page);
      else url = A('vod/home?page=' + page + '&cat=' + enc(cat));
      const r = await fetch(url, ac ? { signal: ac.signal } : undefined);
      const d = await r.json();
      if (ac && loadAbort !== ac) return;
      const list = d.items || [];
      if (first) items = list;
      else items = items.concat(list.filter(x => !items.some(y => y.name === x.name && y.site === x.site)));
      // 分类栏与关键词无关，搜索时别再并发拉一次 vod/cats 拖慢首屏
      if (first && !query) loadCats();
      // 首屏走「快速返回」，剩余慢源在后台继续并入缓存。等几秒静默再取一次，
      // 让海报墙自动长出来，用户无需手动刷新，也不会感到卡顿。
      if (first && !query && !d.cached) {
        clearTimeout(warmTimer);
        warmTimer = setTimeout(async () => {
          try {
            const r2 = await fetch(A('vod/home?page=1&cat=' + enc(cat)));
            const d2 = await r2.json();
            const more = d2.items || [];
            let added = 0;
            more.forEach(x => {
              if (!items.some(y => y.name === x.name && y.site === x.site)) { items.push(x); added++; }
            });
            if (added && M().getView() === 'vod') M().refresh();
          } catch (e) {}
        }, 5000);
      }
    } catch (e) {
      if (e && e.name === 'AbortError') return;
      toast('加载失败，请点「刷新」重试');
    } finally {
      if (!ac || loadAbort === ac) {
        loadAbort = 0; loading = false;
        if (M().getView() === 'vod') M().refresh();
      }
    }
  }

  async function loadCats() {
    if (cats.length && !catsPartial) return;
    try {
      const r = await fetch(A('vod/cats'));
      const d = await r.json();
      cats = d.cats || [];
      catsPartial = !!d.partial;
      // 拿到的是骨架时，后台聚合完成后再静默重拉一次拿到各站真实分类
      if (catsPartial && catsRetry++ < 4) setTimeout(() => loadCats(), 4500);
    } catch (e) {}
  }

  function refresh() {
    items = []; page = 1; load(true);
  }

  function showCats() {
    loadCats().then(() => {
      // 固定顺序：电视剧 → 电影 → 其它分类 → 最后才是「全部分类」
      const opts = cats.map(x => x.name + (x.hits > 0 ? ' (' + x.hits + ')' : ''));
      opts.push('全部分类');
      M().dialog('选择分类',
        `<div style="max-height:50vh;overflow:auto">${opts.map((n, i) =>
          `<button class="vod-chip" data-cat="${i}" style="display:block;width:100%;margin:4px 0;padding:9px">${esc(n)}</button>`).join('')}</div>`,
        null);
      // dialog 的确定按钮隐藏，用自定义按钮直接选
      const body = $('dlgBody');
      if (body) body.onclick = (e) => {
        const b = e.target.closest('[data-cat]');
        if (!b) return;
        const i = Number(b.dataset.cat);
        cat = i < cats.length ? cats[i].name : '';
        M().closeDialog(); refresh();
      };
    });
  }

  /* ---------------- 详情 ---------------- */

  async function openDetail(it) {
    if (!it) return;
    curSite = it.site; curId = it.id;
    $('vodOverlay').hidden = false;
    $('vodName').textContent = it.name || '';
    $('vodMeta').textContent = '正在读取影片信息…';
    $('vodDesc').textContent = '';
    $('vodEpisodes').innerHTML = '<div class="vod-empty">正在解析播放线路…</div>';
    try {
      const r = await fetch(A('vod/detail?site=' + enc(it.site) + '&id=' + enc(it.id)
        + '&name=' + enc(it.name) + '&line=0'));
      const d = await r.json();
      detail = d.item || it;
      sources = d.sources || [];
      episodes = d.episodes || [];
      line = d.line || 0;
      renderDetail();
    } catch (e) { $('vodMeta').textContent = '加载失败，可换一个源重试'; }
  }

  function renderDetail() {
    if (!detail) return;
    const d = detail;
    const meta = [d.remarks, d.year, d.area, d.type, d.score ? '评分 ' + d.score : '', d.siteName]
      .filter(Boolean).join(' · ');
    $('vodName').textContent = d.name || '';
    $('vodMeta').textContent = meta;
    $('vodDesc').textContent = (d.actor ? '主演：' + d.actor + '\n' : '') + (d.desc || '暂无简介');
    const img = $('vodPoster');
    img.src = d.poster || '';
    img.style.display = d.poster ? 'block' : 'none';
    const vs = d.variants || [];
    $('vodVariants').innerHTML = vs.map((v, i) =>
      `<button class="vod-chip ${v.site === curSite && v.id === curId ? 'on' : ''}" data-vodvariant="${i}">${esc(v.siteName)}</button>`).join('');
    $('btnVodLine').textContent = sources.length ? (sources[line].name + ' · ' + sources[line].count + ' 集') : '无线路';
    $('vodEpisodes').innerHTML = episodes.length
      ? episodes.map((e, i) => `<button class="vod-ep" data-vodep="${i}" title="${esc(e.name)}">${esc(epLabel(e.name))}</button>`).join('')
      : '<div class="vod-empty">该源没有可用播放地址，点上方其它站点换源</div>';
  }

  function epLabel(n) {
    const m = String(n || '').match(/(\d{1,3})/);
    return m ? m[1] : (n || '?');
  }

  async function switchVariant(i) {
    const v = (detail && detail.variants || [])[i];
    if (!v) return;
    openDetail({ site: v.site, id: v.id, name: v.name });
  }

  async function switchLine(i) {
    if (!detail || !sources[i]) return;
    line = i;
    $('vodEpisodes').innerHTML = '<div class="vod-empty">正在解析线路…</div>';
    try {
      const r = await fetch(A('vod/detail?site=' + enc(curSite) + '&id=' + enc(curId)
        + '&name=' + enc(detail.name) + '&line=' + i));
      const d = await r.json();
      episodes = d.episodes || []; sources = d.sources || []; line = d.line || 0;
      renderDetail();
    } catch (e) { toast('线路解析失败'); }
  }

  /* ---------------- 播放 ---------------- */

  function playEpisode(i) {
    if (!episodes[i] || !detail) return;
    wtStop();   // 手动点剧集即退出一起看，改由本机自主播放
    playList = episodes.slice();
    playIndex = i;
    historyKey = curSite + '|' + curId;
    const ep = episodes[i];
    $('vodEpisodes').querySelectorAll('.vod-ep').forEach((b, k) => b.classList.toggle('on', k === i));
    saveHistory(i, 0);
    // 换集后一起看的跟随端也要跟着换集（视频源仍是宿主机代理）
    if (shareCardId && shareMeta) {
      shareMeta.watchUrl = ep.play;
      shareMeta.episodeName = ep.name;
      shareMeta.episodeIndex = i;
      shareMeta.title = detail.name + ' · ' + ep.name;
      publishWatch();
    }
    playVod(ep.play, detail.name + ' · ' + ep.name, ep.url);
  }

  function playVod(url, title, rawUrl) {
    const v = $('videoEl');
    const jb = $('joinBar');
    if (jb) jb.hidden = true;
    v.pause();
    destroyHls();
    v.dataset.vod = '1';
    $('videoTitle').textContent = title || '';
    $('videoOverlay').hidden = false;
    try { if (window.LCMedia && LCMedia.showVideoControls) LCMedia.showVideoControls(false); } catch (e) {}
    $('videoLoading').hidden = false;
    const isM3u8 = /\.m3u8/i.test(rawUrl || url);
    if (isM3u8 && window.Hls && window.Hls.isSupported()) {
      hls = new window.Hls({
        enableWorker: true, lowLatencyMode: false,
        maxBufferLength: 20, backBufferLength: 30,
        maxBufferSize: 40 * 1000 * 1000,
        maxLoadingDelay: 2, maxStarvationDelay: 4,
        fragLoadingMaxRetry: 2, manifestLoadingMaxRetry: 2,
        fragLoadingTimeOut: 12000, manifestLoadingTimeOut: 12000
      });
      hls.loadSource(url);
      hls.attachMedia(v);
      hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
        $('videoLoading').hidden = true;
        v.play().catch(() => toast('浏览器阻止了自动播放，点一下播放按钮'));
      });
      hls.on(window.Hls.Events.ERROR, (evt, data) => {
        if (!data || !data.fatal) return;
        $('videoLoading').hidden = true;
        toast('播放失败：可切换线路或换其它源');
      });
    } else {
      v.src = url; v.load();
      v.play().catch(() => toast('浏览器阻止了自动播放，点一下播放按钮'));
    }
  }

  function destroyHls() {
    if (hls) { try { hls.destroy(); } catch (e) {} hls = null; }
    try { $('videoEl').dataset.vod = ''; } catch (e) {}
  }

  async function saveHistory(index, pos) {
    if (!detail || !historyKey) return;
    try {
      await fetch(A('vod/history'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          key: historyKey, name: detail.name, pic: detail.pic || '',
          siteKey: curSite, siteName: detail.siteName || '', remarks: detail.remarks || '',
          episodeName: playList[index] ? playList[index].name : '', episodeIndex: index,
          positionMs: pos, durationMs: 0,
          urls: playList.map(x => x.play), names: playList.map(x => detail.name + ' · ' + x.name)
        })
      });
    } catch (e) {}
  }

  /** 观看记录不再是弹窗菜单：切成一整页与影片列表对齐的封面卡片瀑布墙。 */
  function showHistory() {
    fetch(A('vod/history')).then(r => r.json()).then(d => {
      historyItems = (d.items || []).map(x => {
        const pic = x.pic || x.Pic || '';
        return {
          key: x.key || x.Key || '',
          name: x.name || x.Name || '',
          pic: pic,
          poster: x.poster || (pic ? A('vod/poster?u=' + enc(pic)) : ''),
          siteKey: x.siteKey || x.SiteKey || '',
          siteName: x.siteName || x.SiteName || '',
          episodeName: x.episodeName || x.EpisodeName || '',
          episodeIndex: x.episodeIndex != null ? x.episodeIndex : (x.EpisodeIndex || 0),
          positionMs: x.positionMs != null ? x.positionMs : (x.PositionMs || 0)
        };
      });
      historyView = true;
      if ($('vodOverlay')) $('vodOverlay').hidden = true;
      M().refresh();
    }).catch(() => toast('读取历史失败'));
  }

  /* ---------------- 分享 + 一起看宿主机上报 ---------------- */

  /** 一次分享同时给出「观看」（外源 CDN 真实 m3u8）与「一起看」（本机代理）。 */
  async function shareCurrent() {
    if (!detail) { toast('请先打开影片详情'); return; }
    const idx = playIndex >= 0 ? playIndex : 0;
    const ep = episodes[idx];
    if (!ep) { toast('请先选择一集再分享'); return; }
    try {
      let real = ep.url || '';
      let proxy = ep.play || '';
      // 播放页地址先解析成真实外源直链，普通分享必须拿到 CDN 的真实 m3u8
      if (real && !/\.(m3u8|mp4|flv)(\?|$)/i.test(real)) {
        const d = await (await fetch(A('vod/resolve?u=' + enc(real)))).json();
        if (d.real) real = d.real;
        if (d.play) proxy = d.play;
      }
      if (!proxy) proxy = A('vod/stream?u=' + enc(real));
      const body = {
        title: detail.name + ' · ' + ep.name,
        url: real,
        poster: detail.pic || '',
        watchUrl: proxy,
        vkey: curSite + '|' + curId,
        episodeName: ep.name,
        episodeIndex: idx,
        duration: 0
      };
      const r = await fetch(A('vod/share'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      const d = await r.json();
      if (!d.ok) { toast(d.error || '分享失败'); return; }
      wtStop();   // 本页从跟随端切换成发起端，避免两套时间轴互相打架
      shareCardId = d.cardId || '';
      shareMeta = body;
      toast('已分享：观看 + 一起看');
      clearInterval(shareTimer);
      shareTimer = setInterval(publishWatch, 1000);
      publishWatch();
    } catch (e) { toast('分享失败'); }
  }

  /** 宿主机上报权威进度；服务端统一打 UTC 时间戳与 seq，供跟随端做时间戳校验。 */
  function publishWatch() {
    if (!shareCardId || !shareMeta) return;
    const v = $('videoEl');
    if (!v) return;
    const dur = Number(v.duration);
    fetch(A('vod/watchtogether'), {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        cardId: shareCardId,
        vkey: shareMeta.vkey,
        title: shareMeta.title,
        poster: shareMeta.poster,
        watchUrl: shareMeta.watchUrl,
        episodeName: shareMeta.episodeName,
        episodeIndex: playIndex >= 0 ? playIndex : shareMeta.episodeIndex,
        position: Math.round((v.currentTime || 0) * 1000),
        duration: isFinite(dur) ? Math.round(dur * 1000) : 0,
        playing: !v.paused && !v.ended && (v.currentTime || 0) >= 0,
        ts: Date.now(),
        seq: 0
      })
    }).catch(() => {});
  }

  function stopShare() {
    if (!shareCardId) return;
    fetch(A('vod/watchtogether?op=stop'), {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cardId: shareCardId })
    }).catch(() => {});
    clearInterval(shareTimer); shareTimer = 0;
    shareCardId = ''; shareMeta = null;
  }

  /* ---------------- 绑定弹层 ---------------- */

  $('btnCloseVod').onclick = () => { $('vodOverlay').hidden = true; };
  // 详情弹层在 #content 之外，剧集 / 换源按钮必须单独委托，不能挂在 #content 上
  $('vodOverlay').onclick = (e) => {
    if (e.target.id === 'vodOverlay') { $('vodOverlay').hidden = true; return; }
    const ep = e.target.closest('[data-vodep]');
    if (ep) { playEpisode(Number(ep.dataset.vodep)); return; }
    const chip = e.target.closest('[data-vodvariant]');
    if (chip) { switchVariant(Number(chip.dataset.vodvariant)); return; }
  };
  $('btnVodLine').onclick = () => {
    if (!sources.length) return;
    M().dialog('选择播放线路',
      `<div style="max-height:50vh;overflow:auto">${sources.map((s, i) =>
        `<button class="vod-chip" data-line="${i}" style="display:block;width:100%;margin:4px 0;padding:9px;text-align:left">
          ${esc(s.name)} · ${s.count} 集${s.direct ? '（直链 ' + s.direct + '）' : ''}</button>`).join('')}</div>`, null);
    const body = $('dlgBody');
    if (body) body.onclick = (e) => {
      const b = e.target.closest('[data-line]');
      if (!b) return;
      M().closeDialog(); switchLine(Number(b.dataset.line));
    };
  };
  $('btnVodHistory').onclick = showHistory;
  if ($('btnVodShare')) $('btnVodShare').onclick = shareCurrent;
  // 「下载本集」：把当前选集的原始 m3u8 交给下载中心，切片下完自动合并为 mp4
  if ($('btnVodDownload')) $('btnVodDownload').onclick = () => {
    if (playIndex < 0 || !episodes[playIndex] || !detail) { toast('请先选择并播放一集'); return; }
    const ep = episodes[playIndex];
    const title = detail.name + ' · ' + ep.name;
    const url = ep.url;
    if (!url) { toast('该集无可用播放地址'); return; }
    fetch(A('vod/download?op=start&type=video&url=' + enc(url) + '&title=' + enc(title)))
      .then(r => r.json()).then(d => {
        if (d && d.ok) toast('已加入下载队列，可在「下载」中心查看进度');
        else toast('下载失败：' + ((d && d.error) || '未知错误'));
      }).catch(() => toast('下载请求失败'));
  };

  // 覆盖关闭按钮：先销毁 hls 实例，否则 stopVideo 之后 hls 还会继续拉流；
  // 同时结束一起看会话 —— 只有本机在线才能一起看。
  $('btnCloseVideo').addEventListener('click', () => { destroyHls(); stopShare(); wtStop(); });
  ['play', 'pause', 'seeked', 'timeupdate'].forEach(ev => {
    $('videoEl').addEventListener(ev, () => { if (shareCardId && ev !== 'timeupdate') publishWatch(); });
  });
  // 播完自动下一集（本地视频的 ended 逻辑由 media.js 在 dataset.vod 时跳过）
  $('videoEl').addEventListener('ended', () => {
    if ($('videoEl').dataset.vod !== '1') return;
    if (playIndex >= 0 && playIndex < playList.length - 1) {
      const next = playIndex + 1;
      playIndex = next;
      saveHistory(next, 0);
      if (shareCardId && shareMeta) {
        shareMeta.watchUrl = playList[next].play;
        shareMeta.episodeName = playList[next].name;
        shareMeta.episodeIndex = next;
        shareMeta.title = (detail ? detail.name : '') + ' · ' + playList[next].name;
        publishWatch();
      }
      playVod(playList[next].play, (detail ? detail.name : '') + ' · ' + playList[next].name, playList[next].url);
      $('vodEpisodes').querySelectorAll('.vod-ep').forEach((b, k) => b.classList.toggle('on', k === next));
    } else {
      destroyHls();
      saveHistory(Math.max(0, playIndex), 0);
    }
  });

  /* ================= 一起看：/media 网页端跟随端 =================
     宿主机publish → 本页自动发现 → 点「确定」进放映界面：
       · 视频源是发起端本机代理(/api/media/vod/stream?u=...)，复用其已加载的分片缓存；
       · 1.2s 拉一次状态镜像，ts/seq 做时间戳校验（旧帧/重放帧丢弃）；
       · 250ms 闭环校正：>1.5s 硬 seek、>120ms 用 ±6% 变速渐进追平，播放/暂停跟随宿主机。 */
  const wt = {
    active: false, cardId: '', host: '', url: '', base: '',
    pos: 0, dur: 0, playing: false, age: 0, at: 0,
    vkey: '', epIndex: -1, ts: 0, seq: 0,
    poll: 0, corr: 0, ready: false, first: true, offline: false, lastSeek: 0
  };

  /* 一起看的所有请求都必须打在「App 宿主机」网关上：
     只有宿主机的 /api/media/vod/stream 才有它自己看片时落盘的 3 天分片缓存，
     拼成同源相对路径会让请求落到 Web 端自己的网关（等于重新向 CDN 拉流）。
     watchHost 偶发退化成 127.0.0.1 时，非同机页面才退回当前源。 */
  function wtHost(host) {
    let h = String(host || '').replace(/\/$/, '');
    if (!/^https?:\/\//i.test(h)) return location.origin;
    try {
      const u = new URL(h);
      const loop = (u.hostname === '127.0.0.1' || u.hostname === 'localhost' || u.hostname === '::1');
      if (loop && location.hostname !== u.hostname) return location.origin;
    } catch (e) {}
    return h;
  }

  function wtUrl(rel, host) {
    if (!rel) return '';
    if (/^https?:\/\//i.test(rel)) return rel;
    return wtHost(host) + (rel.charAt(0) === '/' ? rel : '/' + rel);
  }

  /* 网关转发会把 Host 头改写成 127.0.0.1:6789，服务端无从得知「外部访问宿主机用的地址」。
     显式带上 h=，VodStream 才能把 m3u8 里的分片也写成宿主机绝对地址，
     保证清单和每一个分片都落在 App 宿主机代理、命中它 3 天的分片缓存。 */
  function wtProxyUrl(url, host) {
    if (!url || !host || !/^https?:\/\//i.test(host)) return url;
    return url + (url.indexOf('?') >= 0 ? '&' : '?') + 'h=' + encodeURIComponent(host);
  }

  function wtStop() {
    wt.active = false; wt.ready = false;
    clearInterval(wt.poll); clearInterval(wt.corr);
    wt.poll = wt.corr = 0;
  }

  function joinWatch(st) {
    wtStop();
    wt.active = true;
    wt.cardId = st.cardId || '';
    wt.host = wtHost(st.watchHost);
    wt.url = wtProxyUrl(wtUrl(st.watchUrl || '', wt.host), wt.host);
    // 状态帧同样只认宿主机：Web 端自己网关上的镜像是空的，轮询它会立刻误判离线
    wt.base = wt.host + '/api/media/vod/watchtogether';
    wt.vkey = st.vkey || '';
    wt.epIndex = Number(st.episodeIndex) || 0;
    wt.ts = 0; wt.seq = 0; wt.lastSeek = 0;
    wt.first = true; wt.ready = false; wt.offline = false;
    if (!wt.url) { toast('发起端没有可用的视频源'); wtStop(); return; }
    playVod(wt.url, st.title || '一起看', wt.url);
    wtAbsorb(st);
    wt.poll = setInterval(wtPoll, 1200);
    wt.corr = setInterval(wtCorrect, 250);
    toast('已加入一起看，正在对齐宿主机进度…');
  }

  function wtPoll() {
    if (!wt.active) return;
    fetch(wt.base, { cache: 'no-store' })
      .then(r => r.ok ? r.json() : null)
      .then(st => { if (st) wtAbsorb(st); })
      .catch(() => {});
  }

  function wtAbsorb(st) {
    if (!wt.active || !st) return;
    if (!st.online) {
      if (!wt.offline) { wt.offline = true; toast('发起端不在线，一起看已暂停'); }
      wt.playing = false;
      return;
    }
    if (st.cardId && wt.cardId && st.cardId !== wt.cardId) {
      if (!wt.offline) { wt.offline = true; toast('该一起看会话已结束'); }
      wt.playing = false;
      return;
    }
    wt.offline = false;

    // 时间戳校验：迟到帧 / 重放帧一律丢弃，不能把已追上的进度拉回去
    const ts = Number(st.ts) || 0, seq = Number(st.seq) || 0;
    if (ts && wt.ts && ts < wt.ts - 1000) return;
    if (seq && wt.seq && seq < wt.seq && ts <= wt.ts) return;
    wt.ts = ts; wt.seq = seq;

    // serverTime 与 ts 都出自宿主机同一时钟，不受两端墙上时钟偏差影响
    let age = (Number(st.serverTime) || 0) - ts;
    if (!isFinite(age) || age < 0) age = 0;
    if (age > 15000) age = 15000;

    const pos = Number(st.position) || 0;
    // 宿主机换 IP / 端口时跟着切换，视频源始终落在它的代理上
    if (st.watchHost) {
      const h = wtHost(st.watchHost);
      if (h !== wt.host) { wt.host = h; wt.base = h + '/api/media/vod/watchtogether'; }
    }
    const rel = st.watchUrl || '';
    const newUrl = wtProxyUrl(wtUrl(rel, wt.host), wt.host);
    const vkey = st.vkey || '', ep = Number(st.episodeIndex) || 0;

    if (wt.first) {
      // 首帧只建立基线：上面 playVod 已经按卡片地址起播，不要重复装载
      wt.first = false;
      wt.vkey = vkey; wt.epIndex = ep;
      if (newUrl) wt.url = newUrl;
    } else if (rel && newUrl && (vkey !== wt.vkey || ep !== wt.epIndex || newUrl !== wt.url)) {
      wt.vkey = vkey; wt.epIndex = ep; wt.url = newUrl;
      playVod(newUrl, st.title || '一起看', newUrl);
      const v = $('videoEl');
      if (v) v.addEventListener('loadedmetadata', function once() {
        v.removeEventListener('loadedmetadata', once);
        try { v.currentTime = Math.max(0, (pos + (st.playing ? age : 0)) / 1000); } catch (e) {}
      });
    }

    wt.pos = pos;
    wt.dur = Number(st.duration) || 0;
    wt.playing = !!st.playing;
    wt.age = age;
    wt.at = Date.now();
    wt.ready = true;
  }

  function wtCorrect() {
    if (!wt.active || wt.offline || !wt.ready) return;
    const v = $('videoEl');
    if (!v || v.readyState < 1) return;   // 元数据未就绪时别乱 seek
    let want = wt.pos + (wt.playing ? wt.age + (Date.now() - wt.at) : 0);
    if (wt.dur > 0) want = Math.min(want, wt.dur);
    if (want < 0) want = 0;
    if (wt.playing && v.paused) v.play().catch(() => {});
    else if (!wt.playing && !v.paused) { v.pause(); v.playbackRate = 1; return; }
    const cur = (v.currentTime || 0) * 1000;
    const d = want - cur, now = Date.now();
    if (Math.abs(d) > 1500 && now - wt.lastSeek > 1200) {
      wt.lastSeek = now; v.playbackRate = 1;
      try { v.currentTime = Math.max(0, want / 1000); } catch (e) {}
    } else if (Math.abs(d) > 120) {
      v.playbackRate = Math.max(0.94, Math.min(1.06, 1 + d / 6000));
    } else {
      v.playbackRate = 1;
    }
  }

  window.LCVod = {
    render, bind, search, sub, load, toggleCfg, bindPtr, joinWatch,
    get loading() { return loading; },
    get cfgOpen() { return cfgOpen; },
    get shareCardId() { return shareCardId; },
    get watchActive() { return wt.active; }
  };
})();

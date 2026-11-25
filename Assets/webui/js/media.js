/* ============================================================
   影音 · 网页端逻辑（参考 jellyfin-web 的媒体库 / nowPlayingBar 交互）
   数据源与 App 共用：/api/media/* 直接读写 files/music 下的曲库与歌单。
   ============================================================ */
(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  const enc = encodeURIComponent;
  const api = (p) => '/api/media/' + p;
  const fmt = (ms) => {
    ms = Math.max(0, Math.floor((ms || 0) / 1000));
    const h = Math.floor(ms / 3600), m = Math.floor(ms % 3600 / 60), s = ms % 60;
    const p = (n) => String(n).padStart(2, '0');
    return h ? h + ':' + p(m) + ':' + p(s) : p(m) + ':' + p(s);
  };
  const artUrl = (s) => api('art?path=' + enc(s.path));
  const streamUrl = (s) => api('stream?path=' + enc(s.path));

  /* ---- 内联 SVG 图标：不依赖系统字体/emoji，保证各平台按钮长得一样 ---- */
  const SVG = {
    play: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true"><path d="M8 5l11 7-11 7z"/></svg>',
    pause: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true"><rect x="8" y="5" width="3.2" height="14" rx="1"/><rect x="12.8" y="5" width="3.2" height="14" rx="1"/></svg>',
    prev: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="2.6" height="14" rx="1"/><path d="M19 5v14l-10-7z"/></svg>',
    next: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true"><rect x="16.4" y="5" width="2.6" height="14" rx="1"/><path d="M5 5v14l10-7z"/></svg>',
    heart: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" aria-hidden="true"><path d="M12 20.2C3.4 14.2 3.8 6.2 8.3 6.2c2.1 0 3.1 1.7 3.7 2.9.6-1.2 1.6-2.9 3.7-2.9 4.5 0 4.9 8 .3 14z"/></svg>',
    heartFill: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true"><path d="M12 20.2C3.4 14.2 3.8 6.2 8.3 6.2c2.1 0 3.1 1.7 3.7 2.9.6-1.2 1.6-2.9 3.7-2.9 4.5 0 4.9 8 .3 14z"/></svg>',
    close: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>',
    menu: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16"/></svg>',
    queue: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h10"/></svg>',
    shuffle: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3.5 7.5h3.6l9 9h4.2M3.5 16.5h3.6l9-9h4.2M18 4.8l2.6 2.7L18 10.2M18 13.8l2.6 2.7L18 19.2"/></svg>',
    download: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 4v11M7.5 10.5L12 15l4.5-4.5M5 19h14"/></svg>',
    music: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true"><circle cx="8" cy="17" r="2.6"/><circle cx="17" cy="15" r="2.6"/><path d="M10.6 17V7.2l9-2v9.8"/></svg>',
    volume: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 9.5h3.5L13 5.5v13L7.5 14.5H4z"/><path d="M16.4 9a4 4 0 010 6"/></svg>',
    mute: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 9.5h3.5L13 5.5v13L7.5 14.5H4z"/><path d="M16.5 9.5l4 5M20.5 9.5l-4 5"/></svg>',
    note: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true"><path d="M10 17.5V5.5l7-1.5V16"/><circle cx="7.2" cy="17.6" r="2.6" fill="currentColor" stroke="none"/><circle cx="14.2" cy="16.1" r="2.1" fill="currentColor" stroke="none"/></svg>',
    image: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3.2" y="4.2" width="17.6" height="15.6" rx="3.2"/><circle cx="8.4" cy="9.2" r="1.6"/><path d="M4.4 17.6l5.2-5.2 3.6 3.6 3.2-3.2 3.4 3.2"/></svg>',
    cloud: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7.5 18a4.5 4.5 0 01-.6-9 6 6 0 0111.3 1.4A3.8 3.8 0 0117 18z"/></svg>',
    trash: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3.8 6.6h16M9.4 4.2h5.2"/><path d="M6.4 6.6l1 14h9.2l1-14"/><path d="M10.4 10v7M13.6 10v7"/></svg>',
    pen: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4.2 19.8L5.8 14.4 15.4 4.8l3.8 3.8-9.6 9.6z"/><path d="M4.2 19.8l5.4-1.8"/></svg>',
    moon: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 14.5A8 8 0 019.5 4 7 7 0 1020 14.5z"/></svg>',
    sun: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="4.2"/><path d="M12 2.5v2.4M12 19.1v2.4M2.5 12h2.4M19.1 12h2.4M5 5l1.7 1.7M17.3 17.3 19 19M19 5l-1.7 1.7M6.7 17.3 5 19"/></svg>'
  };
  const icon = (name) => SVG[name] || '';
  window.MIcon = icon; // 暴露给 onerror/onload 等全局作用域的处理器

  let S = { audio: [], videos: [], photos: [], photoFolders: [], playlists: [], favorites: [], dirs: [], stats: {} };
  let view = 'home', listId = '', query = '';
  let queue = [], qpos = -1, repeat = 0, shuffle = false;
  let lyrics = [], lyricIdx = -1, nowLyricIdx = -2;
  let menuSong = null, toastTimer = 0;
  // 图片分区：folder=按文件夹 / all=全部；photoFolder 为空表示停在文件夹列表
  let photoMode = 'folder', photoFolder = '';
  let photoList = [], photoIdx = -1;
  let photoTouchMoved = false;
  // 在线音乐：搜索结果列表与当前搜索词（与本地曲库解耦，不关心本机有没有这首歌）
  let onlineList = [], onlinePage = 1, onlineQuery = '', onlineLoading = false;

  const audio = $('audioEl');
  const videoEl = $('videoEl');

  /* ---------------- 数据 ---------------- */

  async function post(p) {
    try {
      const r = await fetch(api(p), { method: 'POST', headers: { 'Content-Length': '0' } });
      return await r.json();
    } catch (e) { return { ok: false, error: String(e) }; }
  }

  async function load(render = true) {
    try {
      const r = await fetch(api('state'));
      S = await r.json();
      if (!S.ok) throw new Error('state');
      // 音乐与视频统一按标题拼音 A-Z 排列；与 App 端共用同一张首字母表
      const byTitle = (a, b) => window.MPinyin
        ? MPinyin.compare(a.title || a.fileName || '', b.title || b.fileName || '')
        : String(a.title || '').localeCompare(String(b.title || ''), 'zh-CN');
      S.audio.sort(byTitle);
      S.videos.sort(byTitle);
    } catch (e) {
      toast('读取影音库失败，请确认 App 已启动');
      return;
    }
    $('cntAudio').textContent = S.audio.length;
    $('cntVideo').textContent = S.videos.length;
    $('cntPhoto').textContent = (S.photos || []).length;
    $('cntFav').textContent = S.favorites.length;
    $('sideStats').innerHTML =
      `音乐 ${S.stats.audio} · 视频 ${S.stats.videos} · 图片 ${S.stats.photos || 0}<br>` +
      `检索目录 ${S.dirs.filter(d => d.enabled).length} 个` +
      (S.stats.lastScan ? ' · 扫描 ' + esc(S.stats.lastScan) : '');
    renderPlaylistNav();
    if (render) renderView();
    syncFav();
  }

  function allSongs() { return S.audio.concat(S.videos); }
  function byPath(p) { return allSongs().find(s => s.path === p); }
  function favSet() { return new Set(S.favorites || []); }

  /* ---------------- 导航 ---------------- */

  function renderPlaylistNav() {
    const box = $('playlistNav');
    box.innerHTML = S.playlists.map(pl => `
      <button class="nav-item pl-item ${view === 'playlist' && listId === pl.id ? 'active' : ''}"
              data-id="${esc(pl.id)}">
        ${pl.cover
          ? `<img class="pl-thumb" loading="lazy" src="${esc(artUrl({ path: pl.cover }))}" alt=""
               onerror="this.style.display='none'">`
          : `<i>${pl.isFavorites ? icon('heartFill') : icon('queue')}</i>`}<span>${esc(pl.name)}</span><em>${pl.count}</em>
      </button>`).join('');
    box.querySelectorAll('.pl-item').forEach(b => b.onclick = () => {
      view = 'playlist'; listId = b.dataset.id;
      closeSide(); syncNav(); renderView();
    });
  }

  function syncNav() {
    document.querySelectorAll('#nav .nav-item[data-view]').forEach(b => {
      b.classList.toggle('active', b.dataset.view === view);
    });
    renderPlaylistNav();
  }

  function setView(v, id) {
    view = v; listId = id || '';
    // 切换视图时清理对方上下文，避免本地过滤词与在线搜索词互相串台
    if (v === 'online' || v === 'vod') { query = ''; $('search').value = ''; $('btnClearSearch').hidden = true; }
    else { onlineQuery = ''; }
    if (v === 'vod') $('search').placeholder = '搜索片名（在线多源 + 本地片库）…';
    else $('search').placeholder = '搜索标题、歌手、专辑或文件名…';
    closeSide(); syncNav(); renderView();
    if (v === 'online' || v === 'vod') { setTimeout(() => { try { $('search').focus(); } catch (e) {} }, 60); }
  }

  document.querySelectorAll('#nav .nav-item[data-view]').forEach(b => {
    b.onclick = () => setView(b.dataset.view);
  });

  /* ---------------- 视图渲染 ---------------- */

  function currentItems() {
    let items;
    switch (view) {
      case 'home': items = S.audio.slice(); break;   // 首页显示全部歌曲，搜索框同样生效
      case 'audio': items = S.audio.slice(); break;
      case 'video': items = S.videos.slice(); break;
      case 'favorites': {
        const f = favSet();
        items = S.audio.filter(s => f.has(s.path));
        break;
      }
      case 'playlist': {
        const f = favSet();
        const pl = S.playlists.find(p => p.id === listId);
        const paths = pl ? (pl.isFavorites ? S.favorites : null) : null;
        const set = new Set(paths || []);
        items = pl && pl.isFavorites ? S.audio.filter(s => f.has(s.path)) : [];
        // 自建歌单的歌曲通过 detail 接口异步填充，先留空由 loadDetail 补
        if (pl && !pl.isFavorites) items = window.__detailSongs || [];
        break;
      }
      default: items = [];
    }
    if (query.trim()) {
      const q = query.trim().toLowerCase();
      items = items.filter(s =>
        (s.title || '').toLowerCase().includes(q) ||
        (s.artist || '').toLowerCase().includes(q) ||
        (s.album || '').toLowerCase().includes(q) ||
        (s.fileName || '').toLowerCase().includes(q));
    }
    return items;
  }

  /// 卡片点击时的播放上下文：首页用全部音乐，其它视图用当前（含搜索过滤）列表。
  function playContext(s) {
    let items;
    if (view === 'home') items = S.audio.slice();
    else items = currentItems().filter(x => !x.isVideo);
    if (!items.some(x => x.path === s.path)) items = items.concat([s]);
    return items;
  }

  function viewMeta() {
    switch (view) {
      case 'audio': return ['音乐', `${S.audio.length} 首歌曲`];
      case 'video': return ['视频', `${S.videos.length} 个视频`];
      case 'photo': return ['图片', `${(S.photos || []).length} 张图片`];
      case 'favorites': return ['我喜欢的', `${S.favorites.length} 首歌曲`];
      case 'online': return ['网络音乐', onlineQuery ? `「${onlineQuery}」的在线搜索结果` : '搜索本机没有的歌曲'];
      case 'vod': return ['在线影视', window.LCVod ? window.LCVod.sub() : '多渠道聚合电影 / 电视剧'];
      case 'playlist': {
        const pl = S.playlists.find(p => p.id === listId);
        return [pl ? pl.name : '歌单', pl ? `${pl.count} 首歌曲` : ''];
      }
      default: return ['首页', `音乐 ${S.stats.audio} · 视频 ${S.stats.videos}`];
    }
  }

  async function loadDetail() {
    if (view !== 'playlist') return;
    const pl = S.playlists.find(p => p.id === listId);
    if (!pl || pl.isFavorites) { window.__detailSongs = null; return; }
    try {
      const r = await fetch(api('playlist?op=detail&id=' + enc(listId)),
        { method: 'POST', headers: { 'Content-Length': '0' } });
      const d = await r.json();
      window.__detailSongs = d.songs || [];
    } catch (e) { window.__detailSongs = []; }
  }

  async function renderView() {
    await loadDetail();
    const [title, sub] = viewMeta();
    $('viewTitle').textContent = title;
    $('viewSub').textContent = query ? `搜索「${query}」` + (sub ? ' · ' + sub : '') : (sub || '');
    // 视频逐个全屏播放、图片是独立灯箱、网络音乐用独立播放上下文，都没有本地音频队列
    const noAudio = view === 'video' || view === 'photo' || view === 'online' || view === 'vod';
    $('btnPlayAll').style.display = noAudio ? 'none' : '';
    $('btnShuffleAll').style.display = noAudio ? 'none' : '';
    // 「设置」按钮只在在线影视页显示，用于收纳源管理（默认隐藏）
    const seBtn = $('btnVodSettings');
    if (seBtn) seBtn.hidden = true;
    const c = $('content');

    if (view === 'vod' && window.LCVod) {
      c.innerHTML = window.LCVod.render();
      window.LCVod.bind();
      window.LCVod.bindPtr();
      if (seBtn) {
        seBtn.hidden = false;
        seBtn.classList.toggle('on', !!window.LCVod.cfgOpen);
        seBtn.onclick = () => window.LCVod.toggleCfg();
      }
      if (!window.LCVodStarted) { window.LCVodStarted = true; window.LCVod.load(true); }
      return;
    }

    if (view === 'home') {
      c.innerHTML = renderHome();
    } else if (view === 'photo') {
      c.innerHTML = renderPhotos();
    } else if (view === 'online') {
      c.innerHTML = renderOnline();
    } else {
      const items = currentItems();
      c.innerHTML = items.length
        ? `<div class="grid ${view === 'video' ? 'video' : ''}">${items.map(cardHtml).join('')}</div>`
        : emptyHtml(items);
    }
    bindCards();
    bindPhotoTiles();
    bindOnline();
    syncAzIndex();
    const cur = queue[qpos];
    c.querySelectorAll('.card').forEach(el => {
      if (cur && el.dataset.path === cur.path) el.classList.add('playing');
    });
  }

  /* ---------------- 右侧竖向 A-Z 首字母索引 ---------------- */
  const AZ = '#ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
  let azReady = false, azBubbleTimer = 0;

  function initAzIndex() {
    if (azReady) return;
    const bar = $('azIndex');
    if (!bar) return;
    bar.innerHTML = AZ.map(ch => `<b data-az="${ch}">${ch}</b>`).join('');
    // 触摸滑动：按落点 Y 命中字母（click 也可）
    const pick = (clientY) => {
      const r = bar.getBoundingClientRect();
      const i = Math.floor((clientY - r.top) / (r.height / AZ.length));
      return AZ[Math.max(0, Math.min(AZ.length - 1, i))];
    };
    bar.addEventListener('click', e => {
      const b = e.target.closest('b[data-az]');
      if (b) jumpToLetter(b.dataset.az);
    });
    bar.addEventListener('touchstart', e => { e.preventDefault(); jumpToLetter(pick(e.touches[0].clientY)); }, { passive: false });
    bar.addEventListener('touchmove', e => { e.preventDefault(); jumpToLetter(pick(e.touches[0].clientY)); }, { passive: false });
    azReady = true;
  }

  function syncAzIndex() {
    const bar = $('azIndex');
    if (!bar) return;
    initAzIndex();
    const hasList = ['home', 'audio', 'video', 'favorites', 'playlist'].indexOf(view) >= 0;
    const n = $('content').querySelectorAll('.card[data-letter]').length;
    bar.hidden = !(hasList && n > 0);
    $('content').classList.toggle('with-az', !bar.hidden);
  }

  /** 跳到第一个首字/拼音首字母匹配的卡片；无匹配时只显示气泡提示。 */
  function jumpToLetter(letter) {
    const bubble = $('azBubble');
    if (bubble) {
      bubble.textContent = letter;
      bubble.hidden = false;
      clearTimeout(azBubbleTimer);
      azBubbleTimer = setTimeout(() => { bubble.hidden = true; }, 700);
    }
    const bar = $('azIndex');
    if (bar) bar.querySelectorAll('b[data-az]').forEach(b =>
      b.classList.toggle('active', b.dataset.az === letter));
    const c = $('content');
    // 首页优先定位「全部音乐」网格，而不是顶部那栏视频（视频视图则定位自己的网格）
    const scope = c.querySelector('.home-songs') || c.querySelector('.grid') || c;
    const target = Array.prototype.find.call(scope.querySelectorAll('.card[data-letter]'),
      el => el.dataset.letter === letter);
    if (!target) return;
    const top = target.getBoundingClientRect().top - c.getBoundingClientRect().top + c.scrollTop - 10;
    try { c.scrollTo({ top: Math.max(0, top), behavior: 'smooth' }); }
    catch (e) { c.scrollTop = Math.max(0, top); }
  }

  function emptyHtml() {
    const q = query ? `没有找到与「${esc(query)}」匹配的内容` : '这里还没有内容';
    return `<div class="empty-state"><div class="ico">${icon('note')}</div><h3>${q}</h3>
      <p>在 App 的「影音 → 目录」里勾选参与检索的文件夹，然后扫描。</p></div>`;
  }

  function renderHome() {
    // 首页铺满：有视频给一栏视频，歌曲不再只取前 12 首，而是全部 A-Z 铺开；
    // 已删除「最近添加」区块（它其实只是排序后数组的前 12 首，名不副实）。
    const songs = currentItems();
    const videos = S.videos.slice();
    const photos = (S.photos || []).slice();
    let html = '';
    if (videos.length) html += `<div class="section-title">视频 <small>${videos.length} 个</small></div>
      <div class="row-scroll video">${videos.map(cardHtml).join('')}</div>`;
    if (photos.length) html += `<div class="section-title">图片 <small>${photos.length} 张</small></div>
      <div class="row-scroll">${photos.map(photoTileHtml).join('')}</div>`;
    html += `<div class="section-title">全部音乐 <small>${songs.length} 首 · A-Z</small></div>`;
    html += songs.length
      ? `<div class="grid home-songs">${songs.map(cardHtml).join('')}</div>`
      : emptyHtml(songs);
    if (S.playlists.length) {
      html += `<div class="section-title">歌单</div><div class="grid">` +
        S.playlists.map(pl => `
          <div class="card" data-list="${esc(pl.id)}">
            <div class="card-art">
              ${pl.cover
                ? `<img loading="lazy" src="${esc(artUrl({ path: pl.cover }))}" alt=""
                     onerror="this.style.display='none'"
                     onload="if(this.naturalWidth<=1)this.style.display='none'">`
                : `<div class="ph" style="font-size:34px">${pl.isFavorites ? icon('heartFill') : icon('queue')}</div>`}
              <div class="play-fab"><span>${icon('play')}</span></div>
            </div>
            <div class="card-body"><div class="card-meta">
              <div class="card-title">${esc(pl.name)}</div>
              <div class="card-sub">${pl.count} 首</div>
            </div></div>
          </div>`).join('') + `</div>`;
    }
    return html;
  }

  function cardHtml(s) {
    // 所有卡片都渲染封面：服务端 /api/media/art 对无内嵌封面、视频缩略图失败的条目
    // 会返回按标题生成的 SVG 封面，不会再出现「视频/音频光秃秃一个音符占位」。
    const img = `<img loading="lazy" src="${esc(artUrl(s))}" alt=""
          onerror="this.style.display='none'">`;
    // data-letter 供右侧 A-Z 索引条定位；无拼音的（数字/符号开头）归 '#'
    const azLetter = window.MPinyin
      ? MPinyin.firstLetter(s.title || s.fileName || '')
      : String(s.title || '').charAt(0).toUpperCase();
    return `<div class="card" data-path="${esc(s.path)}" data-letter="${esc(azLetter)}">
      <div class="card-art">
        ${img}
        <div class="ph">${icon('note')}</div>
        ${s.duration ? `<span class="dur">${esc(s.durationText)}</span>` : ''}
        <div class="play-fab"><span>${icon('play')}</span></div>
      </div>
      <div class="card-body">
        <div class="card-meta">
          <div class="card-title">${esc(s.title)}</div>
          <div class="card-sub">${s.isVideo ? '视频 · ' + (s.size / 1048576).toFixed(1) + ' MB'
                                            : esc(s.artist + (s.album ? ' · ' + s.album : ''))}</div>
        </div>
        <button class="card-info" title="详情 / 歌词">详情</button>
        <button class="card-more" title="更多">⋮</button>
      </div>
    </div>`;
  }

  function bindCards() {
    document.querySelectorAll('#content .card[data-path]').forEach(el => {
      el.onclick = (e) => {
        if (e.target.closest('.card-more') || e.target.closest('.card-info')) return;
        const s = byPath(el.dataset.path);
        if (!s) return;
        if (s.isVideo) openVideo(s);
        else playList(playContext(s), s.path, true);
      };
      const more = el.querySelector('.card-more');
      if (more) more.onclick = (e) => {
        e.stopPropagation();
        const s = byPath(el.dataset.path);
        if (s) openMenu(e.clientX, e.clientY, s);
      };
      const info = el.querySelector('.card-info');
      if (info) info.onclick = (e) => {
        e.stopPropagation();
        const s = byPath(el.dataset.path);
        if (s) openDetail(s);
      };
    });
    document.querySelectorAll('#content .card[data-list]').forEach(el => {
      el.onclick = () => setView('playlist', el.dataset.list);
    });
  }

  /* ---------------- 歌曲详情页（封面 / 信息 / 歌词） ---------------- */

  let detailSong = null, detailLyrics = [], detailLyricIdx = -1;

  /// 详情页歌词随播放实时高亮并滚动（仅当详情页打开且正好是当前播放曲目时）。
  function updateDetailLyric(pos) {
    if ($('detailOverlay').hidden) return;
    if (!detailSong) return;
    if (detailSong._sync) {
      // 一起听曲目：只在仍在共享听时高亮
      if (!sync || !sync.active) return;
    } else {
      const cur = queue[qpos];
      if (!cur || detailSong.path !== cur.path) return;
    }
    if (!detailLyrics.length) return;
    let i = -1;
    for (let k = 0; k < detailLyrics.length; k++) if (detailLyrics[k].t <= pos) i = k; else break;
    if (i === detailLyricIdx) return;
    detailLyricIdx = i;
    const el = highlightGroup($('detailLyrics'), detailLyrics, i);
    if (el) $('detailLyrics').scrollTo({ top: el.offsetTop - $('detailLyrics').clientHeight / 2, behavior: 'smooth' });
  }

  function openDetail(s) {
    if (!s) return;
    detailSong = s;
    detailLyrics = []; detailLyricIdx = -1;
    $('detailTitle').textContent = s.title;
    $('detailSub').textContent = s.artist + (s.album ? ' · ' + s.album : '');
    const art = $('detailArt');
    art.style.display = '';
    art.onerror = () => { art.style.display = 'none'; };
    // 在线歌曲优先用远程封面（经 App 代理），本地歌曲走 /api/media/art
    art.src = s.cover ? appProxy(s.cover) : artUrl(s);
    // 一起听曲目无本地路径，隐藏收藏按钮（收藏按本地路径记录）
    const favBtn = $('btnDetailFav');
    if (s._sync) {
      favBtn.hidden = true;
    } else {
      favBtn.hidden = false;
      const fav = favSet().has(s.path);
      favBtn.innerHTML = fav ? icon('heartFill') : icon('heart');
      favBtn.classList.toggle('on', fav);
    }
    // 歌词：本地读 .lrc，在线向 App 网关按需取；与播放条滚动歌词共用解析逻辑
    $('detailLyrics').innerHTML = '<p class="empty">正在读取歌词…</p>';
    $('detailOverlay').hidden = false;
    fetchLyricsText(s).then(text => {
      const L = parseLrc(text || '');
      detailLyrics = L;
      detailLyricIdx = -1;
      $('detailLyrics').innerHTML = L.length
        ? L.map((l, i) => `<p data-i="${i}"${l.bi ? ' class="bi"' : ''}>${l.text ? esc(l.text) : icon('note')}</p>`).join('')
        : '<p class="empty">暂无歌词</p>';
      updateDetailLyric(s._sync ? (syncAudio.currentTime || 0) : (audio.currentTime || 0));
    }).catch(() => { $('detailLyrics').innerHTML = '<p class="empty">歌词读取失败</p>'; });
  }

  $('btnCloseDetail').onclick = () => { $('detailOverlay').hidden = true; };
  $('detailOverlay').onclick = (e) => { if (e.target.id === 'detailOverlay') $('detailOverlay').hidden = true; };
  $('btnDetailPlay').onclick = () => {
    if (!detailSong) return;
    if (detailSong._sync) {
      // 一起听曲目：播放/暂停本机共享听音频
      if (syncAudio.paused) {
        sync.localPaused = false;
        const pr = syncAudio.play(); if (pr && pr.catch) pr.catch(() => {});
      } else {
        sync.localPaused = true;
        try { syncAudio.pause(); } catch (e) {}
      }
      syncUpdateUI();
      return;
    }
    playList(playContext(detailSong), detailSong.path, true);
  };
  $('btnDetailFav').onclick = () => { if (detailSong && !detailSong._sync) toggleFav(detailSong.path); };

  // 点击底部播放条（封面 / 歌名 / 歌词 / 空白处）打开详情页：大封面 + 实时滚动歌词
  // 一起听激活时，正在播放的是共享曲目，优先打开共享听详情
  const openNowDetail = () => {
    if (sync && sync.active && (sync.name || sync.streamUrl)) { openSyncDetail(); return; }
    const s = queue[qpos]; if (s) openDetail(s);
  };
  $('nowMeta').onclick = openNowDetail;
  $('nowArt').onclick = openNowDetail;
  $('nowbar').addEventListener('click', (e) => {
    // 控件区域（播放键 / 进度条 / 音量 / 右侧按钮）不触发详情
    if (e.target.closest('button, input, .now-center, .now-right, .now-seek, .now-art, .now-meta')) return;
    openNowDetail();
  });

  /// 一起听状态下：点击共享听状态条的封面 / 歌名，打开全屏歌曲详情页（排版同客户端）。
  function openSyncDetail() {
    if (!sync.active || (!sync.name && !sync.streamUrl)) { toast('等待 App 发起一起听'); return; }
    openDetail({
      title: sync.name || '一起听',
      artist: sync.artist || '',
      album: '',
      cover: sync.coverUrl ? mediaUrl(sync.coverUrl) : '',
      path: '',
      _sync: true,
      _lrc: sync.lrc || ''
    });
  }
  // 共享听状态条：点封面 / 标题 / 副标题即打开详情；控件区（播放键/进度/歌单）除外
  if ($('syncBar')) $('syncBar').addEventListener('click', (e) => {
    if (e.target.closest('button, .sync-ctrl, .sync-seek, #syncListPanel')) return;
    if (e.target.closest('#syncArt, .sync-meta, #syncTitle')) openSyncDetail();
  });

  /* ---------------- 图片分区 / 全屏灯箱 ---------------- */

  const photoImageUrl = (p, w) => api('image?path=' + enc(p.path) + (w ? '&w=' + w : ''));

  function photoTileHtml(p) {
    return `<div class="photo-tile" data-photo="${esc(p.path)}">
      <div class="art"><img loading="lazy" src="${esc(photoImageUrl(p, 360))}" alt=""
        onerror="this.style.display='none'">${icon('image')}</div>
      <div class="cap" title="${esc(p.name)}">${esc(p.name)}</div>
      <div class="sub">${esc(p.modifiedText || '')}</div>
    </div>`;
  }

  function currentPhotoList() {
    const all = S.photos || [];
    let list = photoMode === 'folder' && photoFolder
      ? all.filter(p => p.folder === photoFolder) : all.slice();
    if (query.trim()) {
      const q = query.trim().toLowerCase();
      list = list.filter(p => (p.name || '').toLowerCase().includes(q) ||
                              (p.folder || '').toLowerCase().includes(q));
    }
    return list;
  }

  function renderPhotos() {
    let bar = `<div class="photo-view-bar">`;
    if (photoMode === 'folder' && photoFolder)
      bar += `<button class="chip" data-pa="back">← 全部文件夹</button>`;
    bar += `<button class="chip ${photoMode === 'folder' ? 'on' : ''}" data-pa="folder">按文件夹</button>
            <button class="chip ${photoMode === 'all' ? 'on' : ''}" data-pa="all">全部图片</button>`;
    if (photoMode === 'folder' && photoFolder)
      bar += `<span class="crumb">${esc(photoFolder)}</span>`;
    bar += `</div>`;

    let tiles = '';
    if (photoMode === 'folder' && !photoFolder) {
      const folders = (S.photoFolders || []).filter(f =>
        !query.trim() || (f.name + ' ' + f.path).toLowerCase().includes(query.trim().toLowerCase()));
      tiles = folders.map(f => `<div class="photo-tile" data-folder="${esc(f.path)}">
        <div class="art"><img loading="lazy" src="${esc(photoImageUrl({ path: f.cover }, 360))}" alt=""
          onerror="this.style.display='none'">${icon('image')}
          <span class="badge">${f.count} 张</span></div>
        <div class="cap" title="${esc(f.path)}">${esc(f.name)}</div>
        <div class="sub">${esc(f.path)}</div>
      </div>`).join('');
    } else {
      const list = currentPhotoList();
      tiles = list.map(photoTileHtml).join('');
    }
    if (!tiles) return bar + `<div class="empty-state"><div class="ico">${icon('image')}</div>
      <h3>${query ? '没有找到匹配的图片' : '这里还没有图片'}</h3>
      <p>在「检索目录」里给目录勾选「图片」，然后重新扫描。</p></div>`;
    return bar + `<div class="photo-grid">${tiles}</div>`;
  }

  function bindPhotoTiles() {
    document.querySelectorAll('#content .photo-view-bar .chip').forEach(b => b.onclick = () => {
      const a = b.dataset.pa;
      if (a === 'back') photoFolder = '';
      else if (a === 'folder') { photoMode = 'folder'; photoFolder = ''; }
      else { photoMode = 'all'; photoFolder = ''; }
      renderView();
    });
    document.querySelectorAll('#content .photo-tile').forEach(el => el.onclick = () => {
      if (el.dataset.folder) { photoFolder = el.dataset.folder; renderView(); return; }
      const p = (S.photos || []).find(x => x.path === el.dataset.photo);
      if (p) openPhoto(p, currentPhotoList());
    });
  }

  /* ---------- 灯箱：默认全屏无 UI，点正中间展开菜单 ---------- */

  function openPhoto(p, list) {
    photoList = (list && list.length ? list : [p]).slice();
    photoIdx = Math.max(0, photoList.findIndex(x => x.path === p.path));
    $('photoOverlay').hidden = false;
    showPhotoAt(photoIdx);
    // 首次打开灯箱时短暂提示「可滑动切换」，2 秒后淡出
    const hint = $('photoHint');
    if (hint) {
      hint.hidden = false; hint.style.opacity = '1';
      clearTimeout(photoHintTimer);
      photoHintTimer = setTimeout(() => {
        hint.style.opacity = '0';
        setTimeout(() => { hint.hidden = true; }, 450);
      }, 2000);
    }
  }

  let photoHintTimer = 0;

  function showPhotoAt(i) {
    if (!photoList.length) { closePhoto(); return; }
    photoIdx = Math.max(0, Math.min(i, photoList.length - 1));
    const p = photoList[photoIdx];
    $('photoImg').src = photoImageUrl(p, 0);
    $('photoMenuInfo').textContent = (photoIdx + 1) + ' / ' + photoList.length + ' · ' + p.name;
    $('photoMenu').hidden = true;
    $('photoDetail').hidden = true;
    fillPhotoDetail(p);
    preloadAround(photoIdx);   // 预加载相邻图片，划屏 / 翻页更顺滑
  }

  // 预加载上一张与下一张（仅原图，缩略图已在列表里加载过），image 接口带白名单校验。
  function preloadAround(i) {
    if (i > 0) { const a = new Image(); a.src = photoImageUrl(photoList[i - 1], 0); }
    if (i < photoList.length - 1) { const b = new Image(); b.src = photoImageUrl(photoList[i + 1], 0); }
  }

  function closePhoto() {
    $('photoOverlay').hidden = true;
    $('photoImg').removeAttribute('src');
    $('photoMenu').hidden = true;
    $('photoDetail').hidden = true;
  }

  function photoStep(d) { showPhotoAt(photoIdx + d); }

  function fillPhotoDetail(p) {
    if (!p) return;
    $('pdName').textContent = p.name || '—';
    $('pdTime').textContent = p.modifiedText || '—';
    $('pdFolder').textContent = p.folder || '—';
    $('pdExtra').textContent = (p.sizeText || '') + (p.dimensions ? ' · ' + p.dimensions : '');
  }

  function photoRename(p) {
    if (!p) return;
    promptText('重命名（可省略扩展名）', p.name, async (name) => {
      if (!name.trim()) return;
      const r = await post('photo?op=rename&path=' + enc(p.path) + '&name=' + enc(name.trim()));
      if (!r.ok) { toast(r.error || '重命名失败'); return; }
      const patch = (x) => x.path === p.path ? Object.assign({}, x, { path: r.path, name: r.name }) : x;
      S.photos = (S.photos || []).map(patch);
      photoList = photoList.map(patch);
      await load(false);
      showPhotoAt(photoIdx);
      renderView();
      toast('已重命名为 ' + r.name);
    });
  }

  async function photoShare(p) {
    const url = location.origin + api('image?path=' + enc(p.path));
    if (navigator.share) {
      try { await navigator.share({ title: p.name, url }); return; }
      catch (e) { if (e.name === 'AbortError') return; }
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      try { await navigator.clipboard.writeText(url); toast('已复制图片链接'); return; } catch (e) {}
    }
    window.open(api('image?path=' + enc(p.path) + '&dl=1'), '_blank');
    toast('已打开图片下载链接');
  }

  async function photoAction(act) {
    const p = photoList[photoIdx];
    if (!p) return;
    if (act === 'detail') {
      $('photoMenu').hidden = true;
      fillPhotoDetail(p);
      $('photoDetail').hidden = false;
      return;
    }
    if (act === 'rename') { photoRename(p); return; }
    if (act === 'share') { photoShare(p); return; }
    if (act === 'delete') {
      confirmText('删除图片', `确定删除「${p.name}」？此操作会直接删除磁盘上的文件，且不可恢复。`, async () => {
        const r = await post('photo?op=delete&path=' + enc(p.path));
        if (!r.ok) { toast(r.error || '删除失败'); return; }
        S.photos = (S.photos || []).filter(x => x.path !== p.path);
        photoList = photoList.filter(x => x.path !== p.path);
        if (!photoList.length) closePhoto();
        else showPhotoAt(Math.min(photoIdx, photoList.length - 1));
        toast('已删除');
        await load(false);
        renderView();
      });
    }
  }

  $('photoOverlay').onclick = (e) => {
    if (suppressClick) { suppressClick = false; return; }  // 划屏不触发点击导航
    if (e.target.closest('.photo-panel')) return;
    const x = e.clientX, w = window.innerWidth;
    if (x < w * 0.3) { photoStep(-1); return; }
    if (x > w * 0.7) { photoStep(1); return; }
    // 屏幕正中间：展开 / 收起菜单
    if (!$('photoDetail').hidden) {
      $('photoDetail').hidden = true;
      $('photoMenu').hidden = false;
    } else {
      $('photoMenu').hidden = !$('photoMenu').hidden;
    }
  };

  /* 触摸划屏：左滑下一张、右滑上一张；上下滑 / 面板打开 / 多指 时交给原生滚动与点击。
     photoTouchMoved 仅标记「本次手势发生了位移」（区分点击与拖拽）；真正的划屏判定在 touchend
     里做，并仅在确认是划屏时置 suppressClick，用于吞掉随后浏览器补发的 click，避免重复导航。 */
  let ptX = 0, ptY = 0, ptT = 0, suppressClick = false;
  const photoOv = $('photoOverlay');
  photoOv.addEventListener('touchstart', (e) => {
    if (e.touches.length !== 1) { photoTouchMoved = true; return; }
    ptX = e.touches[0].clientX; ptY = e.touches[0].clientY; ptT = Date.now();
    photoTouchMoved = false; suppressClick = false;
  }, { passive: true });
  photoOv.addEventListener('touchmove', (e) => {
    if (e.touches.length !== 1) { photoTouchMoved = true; return; }
    const dx = e.touches[0].clientX - ptX, dy = e.touches[0].clientY - ptY;
    if (Math.abs(dx) > 12 || Math.abs(dy) > 12) photoTouchMoved = true;
  }, { passive: true });
  photoOv.addEventListener('touchend', (e) => {
    if (!photoTouchMoved) return;  // 纯点击 → 交给 click 处理三分区
    if (!$('photoMenu').hidden || !$('photoDetail').hidden) return;  // 面板打开时不划屏
    const t = e.changedTouches[0];
    const dx = t.clientX - ptX, dy = t.clientY - ptY;
    const dt = Date.now() - ptT;
    // 快速、明显水平、且水平分量占优 → 视为一次划屏
    if (dt < 700 && Math.abs(dx) > 45 && Math.abs(dx) > Math.abs(dy) * 1.6) {
      if (dx < 0) photoStep(1); else photoStep(-1);
      suppressClick = true;  // 阻止随后触发的 click 再导航一次
    }
  }, { passive: true });
  document.querySelectorAll('#photoMenu .photo-actions button').forEach(b => b.onclick = (e) => {
    e.stopPropagation();
    photoAction(b.dataset.act);
  });
  $('btnClosePhotoDetail').onclick = (e) => {
    e.stopPropagation();
    $('photoDetail').hidden = true;
    $('photoMenu').hidden = false;
  };
  $('pdName').onclick = (e) => { e.stopPropagation(); photoRename(photoList[photoIdx]); };

  /* ---------------- 在线音乐（搜索 / 播放 / 下载） ---------------- */

  function renderOnline() {
    const hint = onlineList.length
      ? `<div class="section-title">「${esc(onlineQuery)}」的在线结果 <small>${onlineList.length} 首</small></div>`
      : (onlineQuery
          ? `<div class="empty-state"><div class="ico">${icon('cloud')}</div><h3>没有找到在线歌曲</h3>
             ${onlineHasFallback
               ? `<p>换个关键词，或</p><button id="btnOnlineFallback" class="primary">试试其它音乐源</button>`
               : `<p>换个关键词，或确认手机能访问外网。</p>`}</div>`
          : `<div class="empty-state"><div class="ico">${icon('cloud')}</div><h3>网络音乐</h3>
             <p>在上方搜索框输入歌名 / 歌手，搜索本机没有的歌曲，可在线播放或下载到本地。</p></div>`);
    const grid = onlineList.length
      ? `<div class="grid">${onlineList.map(onlineCardHtml).join('')}</div>` : '';
    return hint + grid;
  }

  function onlineCardHtml(s) {
    const i = onlineList.indexOf(s);
    // 无远程封面时也走 art 接口拿生成封面，不再留一个空云占位
    const cover = s.cover ? appProxy(s.cover) : artUrl({ path: s.title || s.path || '' });
    const img = `<img loading="lazy" src="${esc(cover)}" alt="" onerror="this.style.display='none'">`;
    return `<div class="card" data-online="${i}">
      <div class="card-art">
        ${img}
        <div class="ph">${icon('cloud')}</div>
        ${s.durationText ? `<span class="dur">${esc(s.durationText)}</span>` : ''}
        <div class="play-fab"><span>${icon('play')}</span></div>
      </div>
      <div class="card-body">
        <div class="card-meta">
          <div class="card-title">${esc(s.title)}</div>
          <div class="card-sub">${esc(s.artist + (s.album ? ' · ' + s.album : ''))}</div>
        </div>
        <button class="card-info" title="下载到本地" data-dl="${i}">下载</button>
        <button class="card-more" title="更多">⋮</button>
      </div>
    </div>`;
  }

  function bindOnline() {
    const fb = $('btnOnlineFallback');
    if (fb) fb.onclick = () => onlineSearch(onlineQuery, 1, onlineTier + 1);
    document.querySelectorAll('#content .card[data-online]').forEach(el => {
      el.onclick = (e) => {
        if (e.target.closest('.card-more') || e.target.closest('.card-info')) return;
        const i = Number(el.dataset.online);
        if (onlineList[i]) playOnline(onlineList, i);
      };
      const dl = el.querySelector('.card-info');
      if (dl) dl.onclick = (e) => {
        e.stopPropagation();
        const s = onlineList[Number(dl.dataset.dl)];
        if (s) onlineDownload(s);
      };
      const more = el.querySelector('.card-more');
      if (more) more.onclick = (e) => {
        e.stopPropagation();
        const s = onlineList[Number(el.dataset.online)];
        if (s) openOnlineMenu(e.clientX, e.clientY, s);
      };
    });
  }

  var onlineTier = 1;          // 当前音源层级（仅失败时升级；UI 不暴露音源名）
  var onlineHasFallback = false;
  async function onlineSearch(q, page, tier) {
    if (tier) onlineTier = tier;
    else if (page === 1) onlineTier = 1;   // 全新搜索回到默认音源
    onlineQuery = (q || '').trim();
    onlinePage = page || 1;
    const c = $('content');
    c.innerHTML = `<div class="empty-state"><div class="ico">${icon('cloud')}</div><h3>正在搜索在线音乐…</h3></div>`;
    try {
      const r = await fetch(api('online/search?q=' + enc(onlineQuery) + '&page=' + onlinePage + '&tier=' + onlineTier));
      const d = await r.json();
      onlineList = (d.items || []);
      onlineHasFallback = !!d.hasMoreFallback;
    } catch (e) { onlineList = []; onlineHasFallback = false; toast('在线搜索失败，请检查网络'); }
    if (view === 'online') renderView();
  }

  // 在线音乐下载统一走「下载中心」（任务管理 / 失败重试 / 目录提醒）。
  async function onlineDownload(s) {
    if (!s) return;
    try {
      const r = await fetch('/api/media/vod/download?op=start&type=music&plat=' + enc(s.plat) + '&id=' + enc(s.id)
        + '&name=' + enc(s.title) + '&artist=' + enc(s.artist || '') + '&duration=' + enc(s.duration || 0)
        + '&cover=' + enc(s.cover || s.pic || ''));
      const d = await r.json();
      if (d && d.ok) toast('已加入下载队列，可在「下载」中心查看进度');
      else toast('下载失败：' + ((d && d.error) || '未知'));
    } catch (e) { toast('下载失败：' + e.message); }
  }

  /// 在线播放：把整页搜索结果当作播放队列，复用底部播放条与下一曲逻辑。
  function playOnline(list, idx) {
    queue = list.slice();
    qpos = idx;
    loadCurrent(true);
    renderQueue();
  }

  function openOnlineMenu(x, y, s) {
    const m = $('itemMenu');
    m.innerHTML = `
      <button data-act="play">${icon('play')} 播放</button>
      <button data-act="download">${icon('download')} 下载到本地</button>`;
    m.hidden = false;
    const w = 190, h = m.offsetHeight;
    m.style.left = Math.min(x, innerWidth - w - 10) + 'px';
    m.style.top = Math.min(y, innerHeight - h - 10) + 'px';
    m.querySelectorAll('button').forEach(b => b.onclick = async () => {
      m.hidden = true;
      if (b.dataset.act === 'play') {
        const i = onlineList.indexOf(s);
        playOnline(onlineList, i >= 0 ? i : 0);
      } else if (b.dataset.act === 'download') {
        onlineDownload(s);
      }
    });
  }

  /* ---------------- 三点菜单 ---------------- */

  function openMenu(x, y, s) {
    menuSong = s;
    const m = $('itemMenu');
    const fav = favSet().has(s.path);
    const lists = S.playlists.filter(p => !p.isFavorites);
    m.innerHTML = `
      <button data-act="play">${icon('play')} 播放</button>
      ${s.isVideo ? '' : '<button data-act="next">' + icon('queue') + ' 下一曲播放</button>'}
      <button data-act="fav">${fav ? icon('heartFill') + ' 取消喜欢' : icon('heart') + ' 设为喜欢'}</button>
      ${s.isVideo ? '' : `<hr>${lists.length
        ? lists.map(p => `<button data-act="add" data-id="${esc(p.id)}">＋ 加入「${esc(p.name)}」</button>`).join('')
        : '<button data-act="newlist">＋ 新建歌单并加入</button>'}
        ${view === 'playlist' && listId && !S.playlists.find(p => p.id === listId)?.isFavorites
          ? '<button data-act="removelist">－ 从本歌单移出</button>' : ''}`}
      <hr><button data-act="exclude" class="danger">从列表移除</button>`;
    m.hidden = false;
    const w = 190, h = m.offsetHeight;
    m.style.left = Math.min(x, innerWidth - w - 10) + 'px';
    m.style.top = Math.min(y, innerHeight - h - 10) + 'px';
    m.querySelectorAll('button').forEach(b => b.onclick = async () => {
      m.hidden = true;
      await menuAction(b.dataset.act, b.dataset.id, s);
    });
  }

  async function menuAction(act, id, s) {
    switch (act) {
      case 'play':
        if (s.isVideo) openVideo(s);
        else playList([s], s.path, true);
        break;
      case 'next': queueInsertNext(s); break;
      case 'fav': await toggleFav(s.path); break;
      case 'add':
        await post('playlist?op=add&id=' + enc(id) + '&path=' + enc(s.path));
        toast('已加入歌单'); await load(false); renderView(); break;
      case 'newlist': promptText('新建歌单', '', async (name) => {
        const r = await post('playlist?op=create&name=' + enc(name));
        if (r.ok) await post('playlist?op=add&id=' + enc(r.id) + '&path=' + enc(s.path));
        toast('已创建并加入'); await load();
      }); break;
      case 'removelist':
        await post('playlist?op=remove&id=' + enc(listId) + '&path=' + enc(s.path));
        toast('已从歌单移出'); await load(); break;
      case 'exclude':
        confirmText('从列表移除', '只移出列表，不会删除磁盘文件。可在「检索目录」里恢复。',
          async () => { await post('exclude?op=add&path=' + enc(s.path)); toast('已移出列表'); await load(); });
        break;
    }
  }

  document.addEventListener('click', (e) => {
    if (!e.target.closest('#itemMenu') && !e.target.closest('.card-more')) $('itemMenu').hidden = true;
  });

  /* ---------------- 音频播放 ---------------- */

  function playList(items, path, autoplay) {
    if (!items || !items.length) return;
    queue = items.slice();
    onlineList = [];   // 切到本地播放时清空在线上下文，避免串台
    if (shuffle) shuffleArray(queue);
    qpos = Math.max(0, queue.findIndex(s => s.path === path));
    loadCurrent(autoplay !== false);
    renderQueue();
  }

  function shuffleArray(a) {
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [a[i], a[j]] = [a[j], a[i]];
    }
  }

  function queueInsertNext(s) {
    if (qpos < 0) { playList([s], s.path, false); toast('已加入播放队列'); return; }
    queue.splice(qpos + 1, 0, s);
    toast('下一曲播放：' + s.title);
    renderQueue();
  }

  function loadCurrent(autoplay) {
    const s = queue[qpos];
    if (!s) return;
    stopVideo();
    audio.src = s.stream || streamUrl(s);
    audio.load();
    $('nowTitle').textContent = s.title;
    $('nowSub').textContent = s.artist + (s.album ? ' · ' + s.album : '');
    // 在线歌曲的封面是第三方远程地址，统一经 App 网关二次代理，浏览器不直接接触 CDN
    const coverSrc = s.cover ? appProxy(s.cover) : (s.path ? artUrl(s) : '');
    $('nowArt').innerHTML = coverSrc
      ? `<img src="${esc(coverSrc)}" alt="" onerror="this.parentNode.innerHTML='<span>' + MIcon('note') + '</span>'"
             onload="if(this.naturalWidth<=1)this.parentNode.innerHTML='<span>' + MIcon('note') + '</span>'">`
      : `<span>${icon('note')}</span>`;
    $('nowLyric').hidden = true; $('nowLyric').textContent = '';
    syncFav();
    loadLyrics(s);
    if (autoplay) audio.play().catch(() => toast('浏览器阻止了自动播放，点播放键继续'));
    syncPlayIcon();
    document.querySelectorAll('#content .card').forEach(el =>
      el.classList.toggle('playing', el.dataset.path === s.path));
  }

  function syncPlayIcon() {
    $('btnPlay').innerHTML = audio.paused ? icon('play') : icon('pause');
  }

  function nextSong(auto) {
    if (!queue.length) return;
    if (repeat === 2 && auto) { audio.currentTime = 0; audio.play(); return; }
    if (qpos < queue.length - 1) qpos++;
    else if (repeat === 1 || !auto || shuffle) { qpos = 0; if (shuffle) shuffleArray(queue); }
    else { qpos = queue.length - 1; return; }
    loadCurrent(true);
    renderQueue();
  }

  audio.addEventListener('play', syncPlayIcon);
  audio.addEventListener('pause', syncPlayIcon);
  audio.addEventListener('ended', () => nextSong(true));
  audio.addEventListener('timeupdate', () => {
    const d = audio.duration || 0, p = audio.currentTime || 0;
    $('seekFill').style.width = (d ? p / d * 100 : 0) + '%';
    $('nowTime').textContent = fmt(p * 1000) + ' / ' + fmt(d * 1000);
    updateLyric(p);
    updateDetailLyric(p);
    updateNowLyric(p);
  });
  audio.addEventListener('error', () => {
    if (audio.src) toast('无法播放该文件，可能格式不受浏览器支持');
  });

  $('btnPlay').onclick = () => {
    if (!queue.length) { toast('先选一首歌吧'); return; }
    if (audio.paused) audio.play(); else audio.pause();
  };
  $('btnPrev').onclick = () => {
    if (!queue.length) return;
    if (audio.currentTime > 3) { audio.currentTime = 0; return; }
    qpos = qpos > 0 ? qpos - 1 : queue.length - 1;
    loadCurrent(true); renderQueue();
  };
  $('btnNext').onclick = () => nextSong(false);
  $('seekWrap').onclick = (e) => {
    if (!audio.duration) return;
    const r = $('seekWrap').getBoundingClientRect();
    audio.currentTime = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)) * audio.duration;
  };
  $('btnRepeat').onclick = () => {
    repeat = (repeat + 1) % 3;
    $('btnRepeat').textContent = ['顺序', '列表', '单曲'][repeat];
    $('btnRepeat').classList.toggle('on', repeat > 0);
  };
  $('btnShuffle').onclick = () => {
    shuffle = !shuffle;
    $('btnShuffle').classList.toggle('on', shuffle);
    toast(shuffle ? '已开启随机播放' : '已关闭随机播放');
  };
  $('volume').oninput = (e) => { audio.volume = Number(e.target.value); audio.muted = false; };
  $('btnMute').onclick = () => { audio.muted = !audio.muted; $('btnMute').innerHTML = audio.muted ? icon('mute') : icon('volume'); };
  $('btnFav').onclick = () => { const s = queue[qpos]; if (s) toggleFav(s.path); };
  function syncFav() {
    const s = queue[qpos];
    const on = s && favSet().has(s.path);
    $('btnFav').innerHTML = on ? icon('heartFill') : icon('heart');
    $('btnFav').classList.toggle('on', !!on);
  }
  async function toggleFav(p) {
    const r = await post('favorite?path=' + enc(p));
    if (r.ok) {
      const set = favSet();
      if (r.favorite) set.add(p); else set.delete(p);
      S.favorites = [...set];
      syncFav(); await load(false); renderView();
    }
  }

  $('btnPlayAll').onclick = () => {
    const items = view === 'home' ? S.audio.slice() : currentItems().filter(s => !s.isVideo);
    if (!items.length) { toast('这里没有可播放的音乐'); return; }
    playList(items, items[0].path, true);
  };
  $('btnShuffleAll').onclick = () => {
    const items = view === 'home' ? S.audio.slice() : currentItems().filter(s => !s.isVideo);
    if (!items.length) { toast('这里没有可播放的音乐'); return; }
    shuffle = true; $('btnShuffle').classList.add('on');
    playList(items, items[Math.floor(Math.random() * items.length)].path, true);
  };

  /* ---------------- 视频播放（流式 + 缓冲指示） ---------------- */

  let curVideoPath = '';
  const showVideoLoading = (on) => { const e = $('videoLoading'); if (e) e.hidden = !on; };

  function openVideo(s) {
    audio.pause();
    // 进入放映界面时收起加入提示，避免浮在画面上（放映容器本身已是最高层级）
    try { joinHide(); } catch (e) {}
    curVideoPath = s.path;
    $('videoTitle').textContent = s.title;
    { const vo = $('videoOverlay'); if (vo) vo.hidden = false; }
    try { showVideoControls(false); showVideoHud(''); } catch (e) {}   // 进入播放默认不显示控件
    showVideoLoading(true);
    videoEl.src = streamUrl(s);
    // 等浏览器缓冲到可播放再播，避免「一点就黑屏/卡住」：
    // 已就绪（HAVE_FUTURE_DATA）就直接播，否则等 canplay / loadeddata 事件再播。
    if (videoEl.readyState >= 3) {
      videoEl.play().catch(() => toast('浏览器阻止了自动播放，点一下播放按钮'));
    } else {
      videoEl.load();
      const onCan = () =>
        videoEl.play().catch(() => toast('浏览器阻止了自动播放，点一下播放按钮'));
      videoEl.addEventListener('canplay', onCan, { once: true });
    }
  }
  function stopVideo() {
    const vo = $('videoOverlay');
    if (vo && !vo.hidden) {
      videoEl.pause();
      videoEl.removeAttribute('src');
      videoEl.load();
    }
    showVideoLoading(false);
    try { showVideoControls(false); showVideoHud(''); } catch (e) {}
    if (vo) vo.hidden = true;
  }
  $('btnCloseVideo').onclick = stopVideo;
  // 缓冲状态：waiting / stalled 显示转圈，playing / canplay / loadeddata 隐藏。
  videoEl.addEventListener('loadeddata', () => showVideoLoading(false));
  videoEl.addEventListener('canplay', () => showVideoLoading(false));
  videoEl.addEventListener('playing', () => showVideoLoading(false));
  videoEl.addEventListener('waiting', () => showVideoLoading(true));
  videoEl.addEventListener('stalled', () => showVideoLoading(true));
  videoEl.addEventListener('error', () => {
    showVideoLoading(false);
    // 在线影视由 hls.js 自己报错并提示「切换线路」，这里不重复弹「格式不支持」
    if (videoEl.dataset.vod === '1') return;
    toast('视频加载失败：可能是格式不受浏览器支持或网络中断');
  });
  videoEl.addEventListener('ended', () => {
    // 在线影视的自动下一集由 vod.js 接管
    if (videoEl.dataset.vod === '1') return;
    const list = S.videos, i = list.findIndex(x => x.path === curVideoPath);
    if (i >= 0 && i < list.length - 1) openVideo(list[i + 1]); else stopVideo();
  });

  /* ---------------- 视频自定义控件层 ---------------- */
  // 进入播放默认隐藏；仅点击画面中间唤起；拖拽进度时只显示时间提示 HUD，不显示控件。
  const videoCtl = $('videoCtl'), videoHud = $('videoHud');
  function showVideoControls(on) {
    if (videoCtl) {
      videoCtl.hidden = !on;
      if (on) videoCtl.classList.remove('seeking');
    }
    updateVideoSeek();
  }
  function showVideoHud(text) {
    if (!videoHud) return;
    videoHud.textContent = text || '';
    videoHud.hidden = !text;
  }
  function updateVideoSeek() {
    const d = videoEl.duration || 0, p = videoEl.currentTime || 0;
    const fill = $('videoSeekFill');
    if (fill) fill.style.width = (d ? (p / d * 100) : 0) + '%';
    const t = $('videoTime');
    if (t) t.textContent = fmt(p * 1000) + ' / ' + fmt(d * 1000);
    const pb = $('btnVideoPlay');
    if (pb) pb.textContent = videoEl.paused ? '播放' : '暂停';
  }
  videoEl.addEventListener('timeupdate', updateVideoSeek);
  videoEl.addEventListener('play', () => updateVideoSeek());
  videoEl.addEventListener('pause', () => updateVideoSeek());

  if ($('btnVideoPlay')) $('btnVideoPlay').onclick = () => {
    if (videoEl.paused) videoEl.play().catch(() => {}); else videoEl.pause();
  };
  if ($('btnVideoShare')) $('btnVideoShare').onclick = () => {
    const title = ($('videoTitle') && $('videoTitle').textContent) || '视频';
    const url = location.href;
    if (navigator.share) { navigator.share({ title, url }).catch(() => {}); return; }
    const done = () => toast('链接已复制');
    if (navigator.clipboard && navigator.clipboard.writeText)
      navigator.clipboard.writeText(url).then(done, () => toast(url));
    else toast(url);
  };

  // 进度条拖拽：拖动期间隐藏控件、仅显示时间 HUD
  let vSeeking = false;
  function vSeekRatio(e) {
    const r = $('videoSeekWrap').getBoundingClientRect();
    return Math.max(0, Math.min(1, (e.clientX - r.left) / Math.max(1, r.width)));
  }
  if ($('videoSeekWrap')) $('videoSeekWrap').addEventListener('pointerdown', (e) => {
    if (!videoEl.duration) return;
    vSeeking = true;
    // 拖拽期间只收起上下信息栏（加 seeking 类），保留进度条本身以继续接收指针事件
    if (videoCtl) videoCtl.classList.add('seeking');
    const d = videoEl.duration, ratio = vSeekRatio(e);
    showVideoHud(fmt(ratio * d * 1000) + ' / ' + fmt(d * 1000));
    try { $('videoSeekWrap').setPointerCapture(e.pointerId); } catch (err) {}
  });
  if ($('videoSeekWrap')) $('videoSeekWrap').addEventListener('pointermove', (e) => {
    if (!vSeeking || !videoEl.duration) return;
    const d = videoEl.duration, ratio = vSeekRatio(e);
    showVideoHud(fmt(ratio * d * 1000) + ' / ' + fmt(d * 1000));
    const fill = $('videoSeekFill');
    if (fill) fill.style.width = (ratio * 100) + '%';
  });
  if ($('videoSeekWrap')) $('videoSeekWrap').addEventListener('pointerup', (e) => {
    if (!vSeeking) return;
    vSeeking = false;
    if (videoCtl) videoCtl.classList.remove('seeking');
    try { videoEl.currentTime = vSeekRatio(e) * (videoEl.duration || 0); } catch (err) {}
    showVideoHud('');
    updateVideoSeek();
    showVideoControls(false);   // 松手后不保留控件，回到干净画面
  });

  // 点击画面：只有中间区域切换控件，其余区域一律隐藏控件
  if ($('videoOverlay')) $('videoOverlay').addEventListener('click', (e) => {
    // 控件自身交互不参与
    if (e.target.closest('#videoCtl, #videoHud')) return;
    if (vSeeking) return;
    const r = $('videoOverlay').getBoundingClientRect();
    const x = e.clientX - r.left, y = e.clientY - r.top;
    const center = x > r.width * 0.3 && x < r.width * 0.7
                && y > r.height * 0.25 && y < r.height * 0.75;
    if (center) showVideoControls(videoCtl ? videoCtl.hidden : false);
    else showVideoControls(false);
  });

  // Esc 关闭放映（控件默认隐藏时也能退出）
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    const vo = $('videoOverlay');
    if (vo && !vo.hidden) { stopVideo(); }
  });

  /* ---------------- 歌词 ---------------- */

  // 取歌词文本：本地歌曲读同名 .lrc；在线歌曲向 App 网关按需向酷狗取 LRC。
  async function fetchLyricsText(s) {
    try {
      // 一起听：发起端状态帧已带回歌词文本，直接使用
      if (s._lrc) return s._lrc;
      if (s.hasLyrics && s.path) {
        const r = await fetch(api('lyrics?path=' + enc(s.path)));
        const d = await r.json();
        return d.text || '';
      }
      if (s.plat && s.id) {
        const r = await fetch(api('online/lyrics?plat=' + enc(s.plat) + '&id=' + enc(s.id)
          + '&name=' + enc(s.title || '') + '&artist=' + enc(s.artist || '')
          + '&duration=' + (s.duration || 0)));
        const d = await r.json();
        return d.text || '';
      }
    } catch (e) {}
    return '';
  }

  async function loadLyrics(s) {
    lyricIdx = -1; lyrics = []; nowLyricIdx = -2;
    $('nowLyric').hidden = true; $('nowLyric').textContent = '';
    $('lyricsBody').innerHTML = '<p class="empty">正在读取歌词…</p>';
    const text = await fetchLyricsText(s);
    if (!text) { $('lyricsBody').innerHTML = '<p class="empty">暂无歌词</p>'; return; }
    lyrics = parseLrc(text);
    nowLyricIdx = -2;
    $('lyricsBody').innerHTML = lyrics.length
      ? lyrics.map((l, i) => `<p data-i="${i}"${l.bi ? ' class="bi"' : ''}>${l.text ? esc(l.text) : icon('note')}</p>`).join('')
      : '<p class="empty">暂无歌词</p>';
    updateNowLyric(audio.currentTime || 0);
  }

  // 解析 LRC。**同一时间戳出现连续多行 → 视为双语歌词**（原文 + 译文），
  // 组内第二行及以后标记 bi=true，随组一起高亮；时间与文本都相同的行才去重。
  function parseLrc(text) {
    const raw = [];
    text.split(/\r?\n/).forEach(line => {
      const re = /\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g;
      let m, times = [];
      while ((m = re.exec(line))) {
        const sec = Number(m[1]) * 60 + Number(m[2]) +
          (m[3] ? Number('0.' + m[3]) : 0);
        times.push(sec);
      }
      if (!times.length) return;
      const t = line.replace(re, '').trim();
      times.forEach(sec => raw.push({ t: sec, text: t }));
    });
    raw.sort((a, b) => a.t - b.t);

    const out = [];
    let gT = null, gStart = 0;
    for (const l of raw) {
      if (gT === null || Math.abs(l.t - gT) > 0.01) {
        gT = l.t; gStart = out.length;
        out.push(l);
      } else {
        // 同组：完全相同的文本视为重复行丢弃，其余作为双语副行
        let dup = false;
        for (let k = gStart; k < out.length; k++) {
          if (out[k].text === l.text) { dup = true; break; }
        }
        if (dup) continue;
        l.bi = true;
        out.push(l);
      }
    }
    return out;
  }

  // 当前行所在的双语组（同时间戳的行索引数组）
  function lyricGroup(list, i) {
    if (i < 0 || !list[i]) return [];
    const t = list[i].t, g = [];
    let k = i;
    while (k > 0 && Math.abs(list[k - 1].t - t) <= 0.01) k--;
    for (; k < list.length && Math.abs(list[k].t - t) <= 0.01; k++) g.push(k);
    return g;
  }

  function highlightGroup(box, list, i) {
    box.querySelectorAll('p').forEach(p => p.classList.remove('cur'));
    const g = lyricGroup(list, i);
    if (!g.length) return;
    let mid = null;
    g.forEach(k => {
      const el = box.querySelector('p[data-i="' + k + '"]');
      if (el) { el.classList.add('cur'); mid = el; }
    });
    return mid;
  }

  function updateLyric(pos) {
    if (!lyrics.length) return;
    let i = -1;
    for (let k = 0; k < lyrics.length; k++) if (lyrics[k].t <= pos) i = k; else break;
    if (i === lyricIdx) return;
    lyricIdx = i;
    const box = $('lyricsBody');
    const mid = highlightGroup(box, lyrics, i);
    if (mid && !$('lyricsPanel').hidden)
      box.scrollTo({ top: mid.offsetTop - box.clientHeight / 2, behavior: 'smooth' });
  }

  // 播放条上的滚动歌词：取当前时间对应的歌词行，逐句淡入（过长自动省略）
  function updateNowLyric(pos) {
    const el = $('nowLyric');
    if (!el) return;
    if (!lyrics.length) { el.hidden = true; return; }
    let i = -1;
    for (let k = 0; k < lyrics.length; k++) if (lyrics[k].t <= pos) i = k; else break;
    if (i === nowLyricIdx) return;
    nowLyricIdx = i;
    const line = i >= 0 ? lyrics[i] : null;
    const txt = line && line.text ? line.text : '';
    if (!txt) { el.hidden = true; el.textContent = ''; return; }
    el.hidden = false;
    el.textContent = txt;
    el.classList.remove('flash');
    void el.offsetWidth;   // 强制重排以重放动画
    el.classList.add('flash');
  }

  $('btnLyrics').onclick = () => {
    const p = $('lyricsPanel');
    p.hidden = !p.hidden;
    if (!p.hidden && lyricIdx >= 0) {
      const cur = $('lyricsBody').querySelector('p.cur');
      if (cur) $('lyricsBody').scrollTop = cur.offsetTop - $('lyricsBody').clientHeight / 2;
    }
  };
  $('btnCloseLyrics').onclick = () => $('lyricsPanel').hidden = true;

  /* ---------------- 播放队列 ---------------- */

  function openDrawer(title) { $('drawerTitle').textContent = title; $('drawer').hidden = false; $('mask').hidden = false; }
  function closeDrawer() { $('drawer').hidden = true; $('mask').hidden = true; }
  function renderQueue() {
    $('drawerBody').innerHTML = queue.length ? queue.map((s, i) => `
      <div class="q-row ${i === qpos ? 'cur' : ''}" data-i="${i}">
        <span class="q-idx">${i === qpos ? icon('play') : i + 1}</span>
        <div class="q-meta"><div class="q-title">${esc(s.title)}</div>
          <div class="q-sub">${esc(s.artist)}</div></div>
        <button class="q-x" data-x="${i}" title="移出队列">${icon('close')}</button>
      </div>`).join('') : '<p class="empty" style="text-align:center;margin-top:30px">播放队列为空</p>';
    $('drawerBody').querySelectorAll('.q-row').forEach(r => r.onclick = (e) => {
      if (e.target.dataset.x !== undefined) return;
      qpos = Number(r.dataset.i); loadCurrent(true); renderQueue();
    });
    $('drawerBody').querySelectorAll('[data-x]').forEach(b => b.onclick = (e) => {
      e.stopPropagation();
      const i = Number(b.dataset.x);
      queue.splice(i, 1);
      if (i < qpos) qpos--;
      else if (i === qpos) { qpos = Math.min(qpos, queue.length - 1); loadCurrent(queue.length > 0); }
      renderQueue();
    });
  }
  $('btnQueue').onclick = () => { renderQueue(); openDrawer('播放队列'); };
  $('btnQueueTop').onclick = () => { renderQueue(); openDrawer('播放队列'); };
  $('btnCloseDrawer').onclick = closeDrawer;
  $('mask').onclick = () => { closeDrawer(); closeSide(); };

  /* ---------------- 歌单 / 目录 / 弹窗 ---------------- */

  let dlgOk = null;
  function showDialog(title, bodyHtml, onOk, okText) {
    $('dlgTitle').textContent = title;
    $('dlgBody').innerHTML = bodyHtml;
    $('dlgOk').textContent = okText || '确定';
    $('dlgOk').style.display = onOk ? '' : 'none';
    dlgOk = onOk;
    $('dialog').hidden = false;
  }
  function closeDialog() { $('dialog').hidden = true; dlgOk = null; }
  $('dlgCancel').onclick = closeDialog;
  $('dlgOk').onclick = () => { const f = dlgOk; const v = $('dlgInput') ? $('dlgInput').value : ''; closeDialog(); if (f) f(v); };
  $('dialog').onclick = (e) => { if (e.target.id === 'dialog') closeDialog(); };

  function promptText(title, initial, onOk) {
    showDialog(title, `<input id="dlgInput" type="text" value="${esc(initial)}" placeholder="输入名称">`, onOk);
    setTimeout(() => { const i = $('dlgInput'); if (i) { i.focus(); i.select(); i.onkeydown = (e) => { if (e.key === 'Enter') $('dlgOk').click(); }; } }, 30);
  }
  function confirmText(title, msg, onOk) {
    showDialog(title, `<div class="dialog-tip">${esc(msg)}</div>`, onOk, '确定');
  }

  $('btnNewList').onclick = () => promptText('新建歌单', '', async (name) => {
    if (!name.trim()) return;
    await post('playlist?op=create&name=' + enc(name.trim()));
    toast('歌单已创建'); await load();
  });

  // 歌单卡片右上角管理：长按/右键触发
  $('content').addEventListener('contextmenu', (e) => {
    const card = e.target.closest('.card[data-list]');
    if (!card) return;
    e.preventDefault();
    const id = card.dataset.list;
    const pl = S.playlists.find(p => p.id === id);
    if (!pl || pl.isFavorites) return;
    const m = $('itemMenu');
    m.innerHTML = `<button data-act="rename">${icon('pen')} 重命名</button>
      <button data-act="delete" class="danger">${icon('trash')} 删除歌单</button>`;
    m.hidden = false;
    m.style.left = Math.min(e.clientX, innerWidth - 190) + 'px';
    m.style.top = Math.min(e.clientY, innerHeight - 100) + 'px';
    m.querySelectorAll('button').forEach(b => b.onclick = async () => {
      m.hidden = true;
      if (b.dataset.act === 'rename') {
        promptText('重命名歌单', pl.name, async (n) => {
          if (!n.trim()) return;
          await post('playlist?op=rename&id=' + enc(id) + '&name=' + enc(n.trim()));
          await load();
        });
      } else {
        confirmText('删除歌单', `确定删除「${pl.name}」？歌曲文件不会被删除。`, async () => {
          await post('playlist?op=delete&id=' + enc(id));
          if (view === 'playlist' && listId === id) setView('audio');
          else await load();
        });
      }
    });
  });

  $('btnDirs').onclick = async () => {
    await load(false);
    const rows = S.dirs.map(d => `
      <div class="dir-row" data-dir="${esc(d.path)}">
        <span class="path"><b>${esc(d.label || d.path)}</b><br><small style="color:var(--sub)">${esc(d.path)}</small></span>
        <label><input type="checkbox" class="d-en" ${d.enabled ? 'checked' : ''}> 检索</label>
        <label><input type="checkbox" class="d-a" ${d.audio ? 'checked' : ''}> 音乐</label>
        <label><input type="checkbox" class="d-v" ${d.video ? 'checked' : ''}> 视频</label>
        <label><input type="checkbox" class="d-p" ${d.photo ? 'checked' : ''}> 图片</label>
        ${d.builtIn ? '' : '<button class="rm">移除</button>'}
      </div>`).join('');
    showDialog('检索目录',
      `<div class="dialog-tip">只有勾选「检索」的目录会被扫描；音乐 / 视频 / 图片可分别指定，避免无关文件进入列表。</div>
       ${rows || '<div class="dialog-tip">还没有目录</div>'}
       <div style="display:flex;gap:8px;margin-top:14px">
         <input id="dlgInput" type="text" placeholder="/storage/emulated/0/Music">
         <button id="btnAddDir" class="ghost-btn">添加</button>
       </div>
       <div class="dialog-tip" style="margin-top:6px">新目录扫描类型：
         <label><input id="newAudio" type="checkbox" checked> 音乐</label>
         <label><input id="newVideo" type="checkbox" checked> 视频</label>
         <label><input id="newPhoto" type="checkbox" checked> 图片</label>
       </div>`, null);
    $('dlgOk').style.display = 'none';
    $('dlgCancel').textContent = '关闭';
    // 目录 / 媒体类型改动后统一触发一次重新扫描，让图片开关立即生效
    const closeDirsAndRescan = async () => {
      $('dlgCancel').textContent = '取消';
      closeDialog();
      await post('scan');
      toast('目录已更新，正在重新扫描…');
      setTimeout(() => load(), 1800);
    };
    $('btnAddDir').onclick = async () => {
      const v = $('dlgInput').value.trim();
      if (v) {
        const na = $('newAudio').checked ? 1 : 0, nv = $('newVideo').checked ? 1 : 0, np = $('newPhoto').checked ? 1 : 0;
        await post('dirs?op=add&dir=' + enc(v) + '&audio=' + na + '&video=' + nv + '&photo=' + np);
        closeDirsAndRescan();
      }
    };
    document.querySelectorAll('.dir-row').forEach(row => {
      const dir = row.dataset.dir;
      const q = (cls) => row.querySelector(cls);
      q('.d-en').onchange = async (e) => { await post('dirs?op=enable&dir=' + enc(dir) + '&on=' + (e.target.checked ? 1 : 0)); };
      const kind = async () => {
        await post('dirs?op=kind&dir=' + enc(dir) +
          '&audio=' + (q('.d-a').checked ? 1 : 0) +
          '&video=' + (q('.d-v').checked ? 1 : 0) +
          '&photo=' + (q('.d-p').checked ? 1 : 0));
      };
      q('.d-a').onchange = kind; q('.d-v').onchange = kind; q('.d-p').onchange = kind;
      const rm = row.querySelector('.rm');
      if (rm) rm.onclick = async () => { await post('dirs?op=remove&dir=' + enc(dir)); closeDirsAndRescan(); };
    });
    $('dlgCancel').onclick = () => { closeDirsAndRescan(); };
  };

  $('btnScan').onclick = async () => {
    toast('正在扫描，稍候刷新…');
    await post('scan');
    setTimeout(() => load(), 2500);
  };

  $('btnTheme').onclick = () => {
    const t = window.MTheme
      ? window.MTheme.toggle()
      : (document.body.classList.toggle('theme-light') ? 'light' : 'dark');
    setThemeIcon(t);
  };
  (function initTheme() {
    let t = 'dark';
    try {
      t = window.MTheme
        ? window.MTheme.apply()
        : (document.body.classList.contains('theme-light') ? 'light' : 'dark');
    } catch (e) {}
    setThemeIcon(t);
  })();
  function setThemeIcon(t) {
    const b = $('btnTheme');
    if (b) b.innerHTML = icon(t === 'dark' ? 'sun' : 'moon');
  }

  /* ---------------- 搜索 / 侧栏 / 工具 ---------------- */

  let searchTimer = 0;
  $('search').oninput = (e) => {
    $('btnClearSearch').hidden = !e.target.value;
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      if (view === 'online') { onlineSearch(e.target.value, 1); }
      else if (view === 'vod' && window.LCVod) { window.LCVod.search(e.target.value); }
      else { query = e.target.value; renderView(); }
    }, 180);
  };
  $('btnClearSearch').onclick = () => {
    $('search').value = ''; $('btnClearSearch').hidden = true;
    if (view === 'online') { onlineQuery = ''; onlineSearch('', 1); }
    else if (view === 'vod' && window.LCVod) { window.LCVod.search(''); }
    else { query = ''; renderView(); }
  };
  $('btnOpenSide').onclick = () => { $('sidebar').classList.add('open'); $('mask').hidden = false; };
  $('btnCloseSide').onclick = closeSide;
  function closeSide() { $('sidebar').classList.remove('open'); $('mask').hidden = true; }

  function toast(msg) {
    const t = $('toast');
    t.textContent = msg; t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.hidden = true, 2200);
  }

  document.addEventListener('keydown', (e) => {
    // 图片灯箱打开时：Esc 关闭，左右方向键切图，空格不触发音频播放
    if (!$('photoOverlay').hidden) {
      if (e.key === 'Escape') { closePhoto(); return; }
      if (e.key === 'ArrowLeft') { e.preventDefault(); photoStep(-1); return; }
      if (e.key === 'ArrowRight') { e.preventDefault(); photoStep(1); return; }
      return;
    }
    if (e.key === 'Escape') {
      stopVideo(); closeDrawer(); closeDialog(); closeSide();
      $('itemMenu').hidden = true; $('lyricsPanel').hidden = true; $('detailOverlay').hidden = true;
    }
    if (e.key === ' ' && !/INPUT|TEXTAREA/.test(document.activeElement.tagName)) {
      e.preventDefault(); $('btnPlay').click();
    }
  });

  /* 在线影视（js/vod.js）复用这里的工具，避免重复实现一套 HTTP / 弹窗 / 播放层 */
  window.LCMedia = {
    api, enc, esc, toast,
    dialog: showDialog, closeDialog,
    stopVideo, refresh: renderView,
    showVideoControls, updateVideoSeek,
    getView: () => view
  };

  /* ---------------- 同步听（网页端跟随 App 发起端同步播放） ----------------
     浏览器作为「跟随端」：轮询 GET /api/media/synclisten，拿到发起端的权威曲目、
     进度与播放态，再用「本机收到帧的时刻」重锚进度，避免跨设备时钟不一致造成的漂移。
     所有媒体/封面都走 App 网关（streamUrl 同源；远程封面经 /api/media/remote 二次代理），
     浏览器全程不直接接触 CDN。 */
  function appProxy(u) {
    if (!u) return u;
    try {
      if (/^https?:\/\//i.test(u)) {
        var o = new URL(u, location.href);
        if (o.origin !== location.origin) return api('remote?url=' + encodeURIComponent(u));
      }
    } catch (e) {}
    return u;
  }

  // 歌单地址由 App 以绝对 LAN 地址下发；先转成同源相对路径，
  // 否则经 localhost / adb forward 访问时会被当成跨源送进 remote 代理（可能被 SSRF 防护挡掉）。
  function mediaUrl(u) {
    if (!u) return '';
    var i = u.indexOf('/api/media/');
    if (/^https?:\/\//i.test(u) && i > 0) u = u.substring(i);
    return appProxy(u);
  }

  var sync = {
    following: false, active: false, localPaused: false,
    mode: 0, role: '', manual: false,
    playing: false, pos: 0, dur: 0, anchorLocal: 0,
    songKey: '', streamUrl: '', coverUrl: '', name: '', artist: '', lrc: '',
    list: [], listRaw: '', lastStatus: null, pendingSeek: null, fails: 0,
    timer: 0, corr: 0
  };
  var syncAudio = $('syncAudio');

  function syncShow(on) {
    var bar = $('syncBar');
    if (bar) bar.hidden = !on;
  }

  function syncNow() {
    // 一起听「各播各的」：进度只看本机播放器；同步听：按发起端时间轴推算
    if (sync.mode === 1) {
      var lp = (syncAudio.currentTime || 0) * 1000;
      if (sync.dur > 0 && lp > sync.dur) lp = sync.dur;
      return Math.max(0, lp);
    }
    var p = sync.pos + (sync.playing && !sync.localPaused ? (Date.now() - sync.anchorLocal) : 0);
    if (sync.dur > 0 && p > sync.dur) p = sync.dur;
    if (p < 0) p = 0;
    return p;
  }

  function syncUpdateUI() {
    try {
      $('syncTitle').textContent = sync.name || '—';
      $('syncSub').textContent = sync.artist
        ? sync.artist + (sync.mode === 1 && sync.manual ? ' · 已自选曲目' : '')
        : (sync.active ? (sync.mode === 1 ? '已播放共享歌单' : '正在一起听')
                       : '等待 App 发起播放 / 一起听');
      var d = sync.dur || 0, p = syncNow();
      var fill = $('syncFill');
      if (fill) fill.style.width = (d ? p / d * 100 : 0) + '%';
      $('syncTime').textContent = fmt(p) + ' / ' + fmt(d);
      $('btnSyncPlay').innerHTML = (syncAudio.paused || sync.localPaused) ? icon('play') : icon('pause');
      var badge = $('syncBadge');
      if (badge) {
        badge.textContent = sync.active
          ? (sync.mode === 1
              ? (sync.role === 'host' ? '一起听 · App 发起' : (sync.playing ? '共享歌单播放中' : '共享歌单已暂停'))
              : (sync.playing ? '一起听播放中' : '已暂停'))
          : '未开启';
        badge.classList.toggle('idle', !sync.active);
      }
      var lb = $('btnSyncList');
      if (lb) lb.hidden = !(sync.active && sync.mode === 1 && sync.list.length);
      if (!lb || lb.hidden) $('syncListPanel').hidden = true;
      // 一起听时让底部播放条也反映共享曲目，点封面/歌名即可打开详情页
      if (sync.active) syncNowbar();
    } catch (e) {}
  }

  /// 把共享听当前曲目同步到底部播放条（标题 / 副标题 / 封面）。
  function syncNowbar() {
    try {
      if (!sync.name && !sync.streamUrl) return;
      $('nowTitle').textContent = sync.name || '一起听';
      $('nowSub').textContent = sync.artist || (sync.mode === 1 ? '共享歌单' : '正在一起听');
      var cover = sync.coverUrl ? mediaUrl(sync.coverUrl) : '';
      $('nowArt').innerHTML = cover
        ? '<img src="' + esc(cover) + '" alt="" onerror="this.parentNode.innerHTML=\'<span>\'+MIcon(\'note\')+\'</span>\'">'
        : '<span>' + icon('note') + '</span>';
    } catch (e) {}
  }

  /// 退出共享听后，底部播放条恢复显示本机队列当前曲目。
  function restoreNowbar() {
    try {
      var s = queue[qpos];
      if (!s) {
        $('nowTitle').textContent = '未在播放';
        $('nowSub').textContent = '选一首歌或一部视频开始';
        $('nowArt').innerHTML = '<span>' + icon('note') + '</span>';
        return;
      }
      $('nowTitle').textContent = s.title;
      $('nowSub').textContent = s.artist + (s.album ? ' · ' + s.album : '');
      var coverSrc = s.cover ? appProxy(s.cover) : (s.path ? artUrl(s) : '');
      $('nowArt').innerHTML = coverSrc
        ? '<img src="' + esc(coverSrc) + '" alt="" onerror="this.parentNode.innerHTML=\'<span>\'+MIcon(\'note\')+\'</span>\'">'
        : '<span>' + icon('note') + '</span>';
    } catch (e) {}
  }

  function syncSetArt(u) {
    var art = $('syncArt');
    if (!art) return;
    art.innerHTML = '';
    if (u) {
      var im = document.createElement('img');
      im.src = mediaUrl(u);
      im.onerror = function () { art.innerHTML = '<span></span>'; };
      art.appendChild(im);
    } else {
      art.innerHTML = '<span></span>';
    }
  }

  function syncSetTrack(t, seekMs) {
    sync.songKey = t.songKey || '';
    sync.streamUrl = t.streamUrl || '';
    sync.coverUrl = t.coverUrl || '';
    sync.name = t.name || '';
    sync.artist = t.artist || '';
    sync.lrc = t.lrc || '';
    sync.dur = t.duration || 0;
    syncSetArt(sync.coverUrl);
    if (sync.streamUrl) {
      syncAudio.src = mediaUrl(sync.streamUrl);
      sync.pendingSeek = Math.max(0, seekMs || 0) / 1000;
      syncAudio.load();
      syncAudio.playbackRate = 1;
    }
    syncRenderList();
  }

  /* ---- 一起听共享歌单 ---- */
  function syncParseList(raw) {
    var arr = [];
    if (typeof raw === 'string' && raw.length > 2) {
      try { arr = JSON.parse(raw) || []; } catch (e) { arr = []; }
    } else if (Array.isArray(raw)) arr = raw;
    if (!Array.isArray(arr)) arr = [];
    sync.list = arr.map(function (x) {
      return {
        songKey: x.k || '', streamUrl: x.u || '', coverUrl: x.c || '',
        name: x.t || '未命名', artist: x.a || '', duration: Number(x.d) || 0
      };
    });
  }

  function syncCurrentIndex() {
    for (var i = 0; i < sync.list.length; i++) {
      var it = sync.list[i];
      if ((it.songKey && it.songKey === sync.songKey) ||
          (!it.songKey && it.streamUrl === sync.streamUrl)) return i;
    }
    return -1;
  }

  function syncRenderList() {
    var box = $('syncListBody');
    if (!box) return;
    box.innerHTML = sync.list.length ? sync.list.map(function (it, i) {
      var cur = (it.songKey && it.songKey === sync.songKey) ||
                (!it.songKey && it.streamUrl === sync.streamUrl);
      return '<div class="q-row' + (cur ? ' cur' : '') + '" data-i="' + i + '">' +
        '<span class="q-idx">' + (cur ? icon('play') : (i + 1)) + '</span>' +
        '<div class="q-meta"><div class="q-title">' + esc(it.name) + '</div>' +
        '<div class="q-sub">' + esc(it.artist) + '</div></div>' +
        (cur ? '<span class="q-tag">当前</span>' : '') + '</div>';
    }).join('') : '<p class="empty" style="text-align:center;margin-top:24px">共享歌单为空</p>';
    box.querySelectorAll('.q-row').forEach(function (r) {
      r.onclick = function () { syncPlayIndex(Number(r.dataset.i)); };
    });
  }

  /** 一起听点选曲目：各播各的，切到自选曲目后不再被发起端的切歌覆盖。 */
  function syncPlayIndex(i) {
    var it = sync.list[i];
    if (!it) return;
    sync.manual = true;
    sync.localPaused = false;
    sync.playing = true;
    sync.pos = 0; sync.anchorLocal = Date.now();
    syncSetTrack(it, 0);
    var pr = syncAudio.play();
    if (pr && pr.catch) pr.catch(function () {});
    syncUpdateUI();
    toast('已切到：' + it.name);
  }

  function syncApply(st, force) {
    if (!st) return;
    sync.lastStatus = st;
    sync.mode = st.mode || 0;
    sync.role = st.role || '';
    // mode 1 = 一起听（共享歌单、各播各的）；mode 2 = 同步听（严格跟随进度）
    sync.active = (sync.mode === 1 || sync.mode === 2);
    var raw = st.playlist || '';
    if (raw !== sync.listRaw) { sync.listRaw = raw; syncParseList(raw); syncRenderList(); }
    if (sync.mode === 2) sync.manual = false;

    if (!sync.active) {
      sync.playing = false;
      sync.manual = false;
      try { syncAudio.pause(); } catch (e) {}
      syncUpdateUI();
      return;
    }

    var auto = (sync.mode === 2 || !sync.manual);
    if (st.lrc) sync.lrc = st.lrc;
    var track = {
      songKey: st.songKey || '', streamUrl: st.streamUrl || '', coverUrl: st.coverUrl || '',
      name: st.name || '', artist: st.artist || '', duration: st.duration || 0, lrc: st.lrc || ''
    };
    var changed = (st.songKey !== sync.songKey || st.streamUrl !== sync.streamUrl);

    if (auto) {
      if (changed || force) {
        // 两者都从发起端当前进度起播：一起听之后各播各的；同步听再由 syncCorrect 严格校正
        syncSetTrack(track, st.position || 0);
        sync.localPaused = false;
      } else {
        if (st.name) sync.name = st.name;
        if (st.artist) sync.artist = st.artist;
        if (st.coverUrl && !$('syncArt').querySelector('img')) syncSetArt(st.coverUrl);
      }
      sync.pos = st.position || 0;
      sync.anchorLocal = Date.now();
      sync.playing = !!st.playing;
      if (st.duration) sync.dur = st.duration;
      if (sync.streamUrl && !sync.localPaused) {
        if (sync.playing && syncAudio.paused) {
          var pr = syncAudio.play();
          if (pr && pr.catch) pr.catch(function () {});
        } else if (!sync.playing && sync.mode === 2 && !syncAudio.paused) {
          try { syncAudio.pause(); } catch (e) {}
        }
      }
    } else {
      // 一起听已自选曲目：跟随发起端的播放/暂停意图，但不切歌、不重锚进度
      sync.playing = !!st.playing;
      if (sync.playing && !sync.localPaused && syncAudio.paused && sync.streamUrl) {
        var pr2 = syncAudio.play();
        if (pr2 && pr2.catch) pr2.catch(function () {});
      }
    }
    syncUpdateUI();
  }

  /** 轮询：页面隐藏时降频，失败指数退避，避免后台标签页空转刷网关。 */
  function syncTick() {
    if (!sync.following) return;
    if (document.hidden) {
      sync.timer = setTimeout(syncTick, 3000);
      return;
    }
    var delay = sync.active ? 1500 : 3000;
    fetch(api('synclisten'), { cache: 'no-store' })
      .then(function (r) { if (!r.ok) throw new Error('http ' + r.status); return r.json(); })
      .then(function (st) { sync.fails = 0; syncApply(st); })
      .catch(function () {
        sync.fails++;
        delay = Math.min(8000, 1500 * Math.pow(2, Math.min(sync.fails, 3)));
      })
      .then(function () {
        if (sync.following) sync.timer = setTimeout(syncTick, delay);
      });
  }

  function syncCorrect() {
    // 仅同步听(mode 2)做严格进度校正；一起听(mode 1)各播各的，只反映播放/暂停
    if (sync.following && sync.mode === 2 && sync.active && sync.playing && !sync.localPaused && !syncAudio.paused) {
      var want = syncNow();
      var cur = (syncAudio.currentTime || 0) * 1000;
      var d = want - cur;
      if (Math.abs(d) > 1500) {
        try { syncAudio.currentTime = Math.max(0, want / 1000); syncAudio.playbackRate = 1; } catch (e) {}
      } else if (Math.abs(d) > 300) {
        syncAudio.playbackRate = 1 + d / 6000;
      } else {
        syncAudio.playbackRate = 1;
      }
      syncUpdateUI();
    } else if (sync.following) {
      try { syncAudio.playbackRate = 1; } catch (e) {}
      // 一起听的进度取自本机播放器，也要每秒刷新一次状态条
      if (sync.mode === 1) syncUpdateUI();
    }
    if (sync.following) sync.corr = setTimeout(syncCorrect, 1000);
  }

  function syncStart(seed) {
    if (sync.following) return;
    sync.following = true;
    sync.localPaused = false;
    sync.manual = false;
    // 重新加入时清掉上一轮的曲目，确保第一帧一定会重新加载音频
    sync.songKey = ''; sync.streamUrl = ''; sync.listRaw = '';
    sync.lastStatus = null; sync.pendingSeek = null;
    var bs = $('btnSync'); if (bs) bs.classList.add('on');
    syncShow(true);
    // 共享听接管时暂停本机普通播放器，避免双音
    try { if (!audio.paused) audio.pause(); } catch (e) {}
    toast('已开启共享听，等待 App 发起播放 / 一起听…');
    syncTick();
    syncCorrect();
    // 自动发现点「确定」时把当前状态帧直接应用，立刻起播并对齐进度
    if (seed) syncApply(seed, true);
  }

  function syncStop() {
    sync.following = false;
    sync.active = false;
    sync.playing = false;
    sync.manual = false;
    sync.listRaw = ''; sync.list = []; sync.lastStatus = null;
    clearTimeout(sync.timer); clearTimeout(sync.corr);
    try { syncAudio.pause(); } catch (e) {}
    // 退出后允许再次弹出「是否加入一起听」提示
    try { joinReset('music'); } catch (e) {}
    $('syncListPanel').hidden = true;
    var bs = $('btnSync'); if (bs) bs.classList.remove('on');
    syncShow(false);
    restoreNowbar();
  }

  $('btnSync').onclick = function () {
    if (sync.following) syncStop(); else syncStart();
  };
  $('btnSyncClose').onclick = function () { syncStop(); };
  $('btnSyncPlay').onclick = function () {
    if (!sync.active || !sync.streamUrl) { toast('App 尚未发起播放 / 一起听'); return; }
    if (syncAudio.paused) {
      sync.localPaused = false;
      var pr = syncAudio.play();
      if (pr && pr.catch) pr.catch(function () {});
    } else {
      sync.localPaused = true;
      try { syncAudio.pause(); } catch (e) {}
    }
    syncUpdateUI();
  };
  /* ---- 一起听歌单浮层 ---- */
  $('btnSyncList').onclick = function (e) {
    e.stopPropagation();
    var p = $('syncListPanel');
    p.hidden = !p.hidden;
    if (!p.hidden) syncRenderList();
  };
  $('btnSyncListClose').onclick = function () { $('syncListPanel').hidden = true; };
  $('btnSyncFollow').onclick = function () {
    sync.manual = false;
    $('syncListPanel').hidden = true;
    toast('已恢复跟随发起端切歌');
    if (sync.lastStatus) syncApply(sync.lastStatus, true);
  };
  document.addEventListener('click', function (e) {
    var p = $('syncListPanel');
    if (!p || p.hidden) return;
    if (p.contains(e.target) || (e.target.closest && e.target.closest('#btnSyncList'))) return;
    p.hidden = true;
  });

  syncAudio.addEventListener('loadedmetadata', function () {
    if (!sync.following || !sync.active || !sync.streamUrl) return;
    var seek = sync.pendingSeek;
    sync.pendingSeek = null;
    if (seek != null && isFinite(syncAudio.duration) && syncAudio.duration > 0) {
      try { syncAudio.currentTime = Math.min(seek, Math.max(0, syncAudio.duration - 0.25)); } catch (e) {}
    } else if (sync.mode === 2) {
      try { syncAudio.currentTime = Math.max(0, syncNow() / 1000); } catch (e) {}
    }
  });
  // 一起听：驱动详情页歌词高亮（仅当详情页展示的是共享听当前曲目）
  syncAudio.addEventListener('timeupdate', function () {
    if (detailSong && detailSong._sync && !$('detailOverlay').hidden)
      updateDetailLyric(syncAudio.currentTime || 0);
  });

  syncAudio.addEventListener('ended', function () {
    // 一起听已自选曲目：本地顺序续播共享歌单；其余情况等发起端下一张卡片
    if (sync.mode === 1 && sync.manual && sync.list.length) {
      var i = syncCurrentIndex();
      if (i >= 0 && i + 1 < sync.list.length) { syncPlayIndex(i + 1); return; }
    }
    sync.playing = false; syncUpdateUI();
  });

  /* ================= 一起听 / 一起看：自动发现 App 发起端 =================
     旧版 /media 必须手动点顶栏「共享听」才会开始轮询，App 在放歌时页面毫无反应；
     在线影视更是完全没有一起看入口。这里页面一打开就同时问两条状态镜像：
       · /api/media/synclisten       App 没关「一起听」且有曲目在播 → 提示加入一起听
       · /api/media/vod/watchtogether 有一场在线放映                  → 提示加入观看
     文案统一「正在播放xxxx，是否加入…?」，右侧确定 / 取消；取消只隐藏本轮邀请，
     换歌 / 换集 / 换会话会重新提示。确定后立即对齐发起端进度。 */
  var join = { kind: '', key: '', onOk: null, dismissed: {}, timer: 0 };

  function joinShow(kind, text, sub, art, key, onOk) {
    var bar = $('joinBar');
    if (!bar) return;
    join.kind = kind; join.key = key; join.onOk = onOk;
    $('joinText').textContent = text;
    $('joinSub').textContent = sub || '';
    var im = $('joinArt');
    if (art) { try { im.src = mediaUrl(art); } catch (e) {} im.hidden = false; }
    else { im.removeAttribute('src'); im.hidden = true; }
    bar.hidden = false;
  }

  function joinHide(kind) {
    if (kind && join.kind && join.kind !== kind) return;
    var bar = $('joinBar'); if (bar) bar.hidden = true;
    join.kind = ''; join.key = ''; join.onOk = null;
  }

  function joinReset(kind) {
    Object.keys(join.dismissed).forEach(function (k) {
      if (k.indexOf(kind + ':') === 0) delete join.dismissed[k];
    });
  }

  $('joinCancel').onclick = function () {
    // 记住这一轮（按曲目 / 会话），否则 2.5 秒后又会弹回来
    if (join.key) join.dismissed[join.kind + ':' + join.key] = true;
    var keys = Object.keys(join.dismissed);
    if (keys.length > 40) delete join.dismissed[keys[0]];
    joinHide();
  };
  $('joinOk').onclick = function () {
    var f = join.onOk;
    joinHide();
    if (f) { try { f(); } catch (e) { toast('加入失败：' + (e && e.message || e)); } }
  };

  function joinMusic(st) {
    if (!st || !st.mode || !st.name) return;
    if (sync.following) return;                     // 已经在一起听 / 同步听
    { const vo = $('videoOverlay'); if (vo && !vo.hidden) return; }          // 正在放映，不打扰
    if (join.kind === 'watch') return;              // 视频邀请优先
    var key = st.songKey || st.name;
    if (join.dismissed['music:' + key]) return;
    joinShow('music', '正在播放' + st.name + '，是否加入一起听？',
      (st.artist || '') + (st.playing ? ' · 播放中' : ' · 已暂停'),
      st.coverUrl || '', key, function () {
        syncStart(st);
        toast('已加入一起听，正在对齐播放进度…');
      });
  }

  function joinWatchOffer(st) {
    if (!st || !st.online || !st.cardId) return;
    var V = window.LCVod;
    if (V && V.shareCardId && V.shareCardId === st.cardId) return; // 本页就是发起端
    if (V && V.watchActive) return;                                // 已在跟随
    { const vo = $('videoOverlay'); if (vo && !vo.hidden) return; }                          // 已经在放映
    var key = st.cardId + '|' + (st.vkey || '') + '|' + (st.episodeIndex || 0) + '|' + (st.title || '');
    if (join.dismissed['watch:' + key]) return;
    if (join.kind === 'music') joinHide('music');
    joinShow('watch', '正在播放' + (st.title || '视频') + '，是否加入观看？',
      (st.playing ? '播放中' : '已暂停') + ' · 宿主机代理缓存（3 天）',
      (st.poster && /^https?:\/\//i.test(st.poster)
        ? api('vod/poster?u=' + encodeURIComponent(st.poster)) : st.poster) || '', key, function () {
        var v = window.LCVod;
        if (v && v.joinWatch) v.joinWatch(st);
        else toast('在线影视模块未就绪');
      });
  }

  function joinDiscover() {
    clearTimeout(join.timer);
    var q = function (p) {
      var req = fetch(api(p), { cache: 'no-store' })
        .then(function (r) { return r.ok ? r.json() : null; });
      // 网关偶发挂起时也要保证发现循环能继续，不能把提示永远卡死
      var guard = new Promise(function (done) { setTimeout(function () { done(null); }, 3000); });
      return Promise.race([req, guard]).catch(function () { return null; });
    };
    Promise.all([q('synclisten'), q('vod/watchtogether')]).then(function (res) {
      var m = res[0], w = res[1];
      // 会话结束就收起过期邀请，避免挂着一条点不动的提示
      if ((!m || !m.mode) && join.kind === 'music') joinHide('music');
      if ((!w || !w.online) && join.kind === 'watch') joinHide('watch');
      if (w && w.online) joinWatchOffer(w);       // 视频优先
      else if (m && m.mode) joinMusic(m);
      join.timer = setTimeout(joinDiscover, document.hidden ? 6000 : 2500);
    });
  }

  joinDiscover();
  initAzIndex();

  load();
})();

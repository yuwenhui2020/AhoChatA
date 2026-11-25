/* ============================================================
   网页端内联 SVG 图标（替代 emoji）
   各 ROM 对 emoji 的渲染差异巨大（彩色位图、基线不齐、甚至缺字），
   这里统一用 24×24 视口的矢量图标，颜色跟随 currentColor，尺寸跟随字号，
   保证网页端在手机/桌面/不同浏览器下长得一致。
   用法：
     · <span data-ico="play"></span>  —— 由本脚本在 DOMContentLoaded 自动填充
     · WI.svg('play')                 —— 在 JS 拼 HTML 时直接插入
   ============================================================ */
(function () {
  'use strict';

  var S = 18; // 默认尺寸（px），用 data-ico 时也可被 .wico 的 CSS 覆盖

  // 统一外壳
  var L = 'fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"';
  var F = 'fill="currentColor"';

  // 每个图标都是完整 <svg> 字符串，{S} 会被替换成尺寸
  var SVG = {
    play:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + F + ' aria-hidden="true"><path d="M8 5l11 7-11 7z"/></svg>',
    pause:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + F + ' aria-hidden="true"><rect x="8" y="5" width="3.2" height="14" rx="1"/><rect x="12.8" y="5" width="3.2" height="14" rx="1"/></svg>',
    prev:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + F + ' aria-hidden="true"><rect x="5" y="5" width="2.6" height="14" rx="1"/><path d="M19 5v14l-10-7z"/></svg>',
    next:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + F + ' aria-hidden="true"><rect x="16.4" y="5" width="2.6" height="14" rx="1"/><path d="M5 5v14l10-7z"/></svg>',
    playpause:  '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><circle cx="12" cy="12" r="8.6"/><path d="M10 8.4l6.4 3.6-6.4 3.6z" ' + F + ' stroke="none"/></svg>',
    heart:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 20.2C3.4 14.2 3.8 6.2 8.3 6.2c2.1 0 3.1 1.7 3.7 2.9.6-1.2 1.6-2.9 3.7-2.9 4.5 0 4.9 8 .3 14z"/></svg>',
    heartFill:  '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + F + ' aria-hidden="true"><path d="M12 20.2C3.4 14.2 3.8 6.2 8.3 6.2c2.1 0 3.1 1.7 3.7 2.9.6-1.2 1.6-2.9 3.7-2.9 4.5 0 4.9 8 .3 14z"/></svg>',
    close:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>',
    check:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7" stroke-width="2"/></svg>',
    warn:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 3.5l9.5 16.5H2.5z"/><path d="M12 9v5.5" stroke-width="2"/><circle cx="12" cy="17" r="1" ' + F + ' stroke="none"/></svg>',
    menu:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16"/></svg>',
    queue:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h11"/></svg>',
    shuffle:    '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M3.5 7.5h3.6l9 9h4.2M3.5 16.5h3.6l9-9h4.2M18 4.8l2.6 2.7L18 10.2M18 13.8l2.6 2.7L18 19.2"/></svg>',
    download:   '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 4v11M7.5 10.5L12 15l4.5-4.5M5 19h14"/></svg>',
    music:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><circle cx="8" cy="17" r="2.6"/><circle cx="17" cy="15" r="2.6"/><path d="M10.6 17V7.2l9-2v9.8"/></svg>',
    volume:     '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4 9.5h3.5L13 5.5v13L7.5 14.5H4z"/><path d="M16.4 9a4 4 0 010 6"/></svg>',
    mute:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4 9.5h3.5L13 5.5v13L7.5 14.5H4z"/><path d="M16.5 9.5l4 5M20.5 9.5l-4 5"/></svg>',
    speaker:    '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4 9.5h3.5L13 5.5v13L7.5 14.5H4z"/><path d="M16.4 9a4 4 0 010 6"/></svg>',
    plus:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 5v14M5 12h14" stroke-width="2"/></svg>',
    minus:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M6 12h12" stroke-width="2"/></svg>',
    up:         '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 19V6M7 11l5-5 5 5"/></svg>',
    down:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 5v13M7 13l5 5 5-5"/></svg>',
    search:     '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><circle cx="10.8" cy="10.8" r="6.2"/><path d="M15.4 15.4L19.6 19.6"/></svg>',
    pen:        '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4.2 19.8L5.8 14.4 15.4 4.8l3.8 3.8-9.6 9.6z"/><path d="M4.2 19.8l5.4-1.8"/></svg>',
    trash:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M3.8 6.6h16M9.4 4.2h5.2"/><path d="M6.4 6.6l1 14h9.2l1-14"/><path d="M10.4 10v7M13.6 10v7"/></svg>',
    folder:     '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M3.2 7.4h5.4l2 2.6h10.2v9.6a3 3 0 01-3 3H6.2a3 3 0 01-3-3z"/></svg>',
    file:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M6 3.2h7.6l4.8 4.8v13.6H6z"/><path d="M13.6 3.2v5h5"/></svg>',
    books:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M5 4h6v16H5zM13 3h6v17h-6z"/><path d="M8 8v8M16 7v9"/></svg>',
    book:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 6.4C8.2 4.4 3.8 5.4 3.8 5.4v12.4C8.2 16.9 12 19.2 12 19.2z"/><path d="M12 6.4C15.8 4.4 20.2 5.4 20.2 5.4v12.4C15.8 16.9 12 19.2 12 19.2z"/></svg>',
    bell:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M6.2 16.2v-5.2C6.2 5.6 12 5.6 12 5.6s5.8 0 5.8 5.4v5.2l1.6 1.6H4.6z"/><path d="M9.4 17.8a2.6 2.6 0 005.2 0"/></svg>',
    link:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><g transform="rotate(45 12 12)"><rect x="3.6" y="8.8" width="8.8" height="6.4" rx="3.2"/><rect x="11.6" y="8.8" width="8.8" height="6.4" rx="3.2"/></g></svg>',
    image:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="3.2" y="4.2" width="17.6" height="15.6" rx="3.2"/><circle cx="8.4" cy="9.2" r="1.6"/><path d="M4.4 17.6l5.2-5.2 3.6 3.6 3.2-3.2 3.4 3.2"/></svg>',
    mic:        '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="9.4" y="3" width="5.2" height="10" rx="2.6"/><path d="M7 11a5 5 0 0010 0"/><path d="M12 16v5M9 21h6"/></svg>',
    video:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="3" y="6.4" width="12.2" height="12.2" rx="3"/><path d="M15.2 11.2l5.6-3 0 8z" ' + F + ' stroke="none"/></svg>',
    media:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="2.6" y="4.6" width="18.8" height="14.8" rx="3"/><path d="M10 8.6l6.4 3.4-6.4 3.4z" ' + F + ' stroke="none"/></svg>',
    key:        '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><circle cx="8" cy="8" r="4"/><path d="M11 11l9 9M16.5 16.5l2.5-2.5M14 14l2.5-2.5"/></svg>',
    tools:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="3" y="8" width="18" height="12" rx="2"/><path d="M3 11.5h18M9.5 5.5h5v4h-5z"/></svg>',
    cloud:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M7.5 18a4.5 4.5 0 01-.6-9 6 6 0 0111.3 1.4A3.8 3.8 0 0117 18z"/></svg>',
    chat:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M4 5a4 4 0 014-4h12a4 4 0 014 4v9a4 4 0 01-4 4h-7l-5 4v-4H8a4 4 0 01-4-4z"/><circle cx="8" cy="9.5" r="0.9" ' + F + ' stroke="none"/><circle cx="12" cy="9.5" r="0.9" ' + F + ' stroke="none"/><circle cx="16" cy="9.5" r="0.9" ' + F + ' stroke="none"/></svg>',
    /* ---- 以下为新增：覆盖更多 emoji（🤖📞📲🪟⌨️💡⚙♪📦 等） ---- */
    bot:        '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="5.5" y="8.5" width="13" height="10" rx="2.5"/><path d="M12 8.5V5.5M12 5.5l-2.4-2.4M12 5.5l2.4-2.4"/><circle cx="9.7" cy="13.3" r="1.2" ' + F + ' stroke="none"/><circle cx="14.3" cy="13.3" r="1.2" ' + F + ' stroke="none"/></svg>',
    phone:      '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M6.6 4.6h2.8l1 3.8-2 1.6a11 11 0 004.6 4.6l1.6-2 3.8 1v2.8a2 2 0 01-2 2A16 16 0 014.6 6.6a2 2 0 010-2z"/></svg>',
    window:     '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M12 4v16M4 9.3h16"/></svg>',
    keyboard:   '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><rect x="2.5" y="7" width="19" height="10" rx="2"/><path d="M5.5 10h.01M8.5 10h.01M11.5 10h.01M14.5 10h.01M17.5 10h.01M7 14h10"/></svg>',
    bulb:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M9.2 16.5h5.6M10.2 19h3.6"/><path d="M12 3.2a6 6 0 00-3.4 10.9c.6.5 1 1.2 1 2v.1h4.8v-.1c0-.8.4-1.5 1-2A6 6 0 0012 3.2z"/></svg>',
    gear:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><circle cx="12" cy="12" r="3"/><path d="M12 4.2v2.5M12 17.3v2.5M4.2 12h2.5M17.3 12h2.5M6.4 6.4l1.8 1.8M15.8 15.8l1.8 1.8M17.6 6.4l-1.8 1.8M8.2 15.8l-1.8 1.8"/></svg>',
    note:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M10 17.5V5.5l7-1.5V16"/><circle cx="7.2" cy="17.6" r="2.6" ' + F + ' stroke="none"/><circle cx="14.2" cy="16.1" r="2.1" ' + F + ' stroke="none"/></svg>',
    package:    '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M3.5 7.5L12 3l8.5 4.5v9L12 21l-8.5-4.5z"/><path d="M3.5 7.5L12 12l8.5-4.5M12 12v9"/></svg>',
    moon:       '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M20 14.5A8 8 0 019.5 4 7 7 0 1020 14.5z"/></svg>',
    sun:        '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><circle cx="12" cy="12" r="4.2"/><path d="M12 2.5v2.4M12 19.1v2.4M2.5 12h2.4M19.1 12h2.4M5 5l1.7 1.7M17.3 17.3 19 19M19 5l-1.7 1.7M6.7 17.3 5 19"/></svg>',
    location:   '<svg viewBox="0 0 24 24" width="{S}" height="{S}" ' + L + ' aria-hidden="true"><path d="M12 21.5s7-6.1 7-12a7 7 0 10-14 0c0 5.9 7 12 7 12z"/><circle cx="12" cy="9.4" r="2.6"/></svg>'
  };

  function svg(name, size) {
    var t = SVG[name];
    if (!t) return '';
    return t.replace(/\{S\}/g, String(size || S));
  }

  function has(name) { return !!SVG[name]; }

  // 自动把 <span data-ico="play"></span> 之类占位填成 SVG
  function apply(root) {
    var scope = root || document;
    var nodes = scope.querySelectorAll('[data-ico]');
    for (var i = 0; i < nodes.length; i++) {
      var el = nodes[i];
      var sz = el.getAttribute('data-ico-size');
      el.innerHTML = svg(el.getAttribute('data-ico'), sz ? parseInt(sz, 10) : undefined);
      el.classList.add('wico');
    }
  }

  // 注入基础样式：保证图标与文字垂直居中，且尺寸可被 CSS 覆盖
  (function injectCss() {
    try {
      var st = document.createElement('style');
      st.textContent = '.wico{display:inline-block;vertical-align:-0.15em;line-height:1}' +
        '.wico svg{display:block}' +
        'button .wico, .guest-tag .wico, .attach-item .wico{vertical-align:-0.18em}' +
        '.ai-ico .wico{width:20px;height:20px}';
      document.head.appendChild(st);
    } catch (e) {}
  })();

  window.WI = { svg: svg, apply: apply, has: has };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () { apply(); });
  } else {
    apply();
  }
})();

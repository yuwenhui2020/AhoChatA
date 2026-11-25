(function () {
  'use strict';
  var sw = document.getElementById('arSwitch'), dir = document.getElementById('arDir');
  var save = document.getElementById('arSave'), def = document.getElementById('arDefault');
  var hint = document.getElementById('arHint'), toastEl = document.getElementById('toast');
  var enabled = false;
  function toast(m) { toastEl.textContent = m; toastEl.classList.add('show'); clearTimeout(toast._t); toast._t = setTimeout(function () { toastEl.classList.remove('show'); }, 2200); }
  function render() { sw.classList.toggle('on', enabled); }
  function load() {
    fetch('/api/autoreceive').then(function (r) { return r.json(); }).then(function (j) {
      enabled = !!j.enabled; dir.value = j.dir || '';
      if (j.defaultDir) hint.setAttribute('data-default', j.defaultDir);
      render();
    }).catch(function (e) { toast('读取设置失败：' + e.message); });
  }
  sw.addEventListener('click', function () { enabled = !enabled; render(); save.click(); });
  save.addEventListener('click', function () {
    fetch('/api/autoreceive', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: enabled, dir: dir.value.trim() })
    }).then(function (r) { return r.json(); }).then(function (j) {
      toast(j.enabled ? '已开启自动接收' : '已保存（自动接收关闭）');
    }).catch(function (e) { toast('保存失败：' + e.message); });
  });
  def.addEventListener('click', function () { dir.value = hint.getAttribute('data-default') || ''; save.click(); });
  load();
})();

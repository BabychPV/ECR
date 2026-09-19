/* =====================================================================
   ECR Hybrid — kit.js · поведінка і фабрики (токени й класи — в index.html)
   Усі фабрики повертають DOM-ВУЗЛИ (HTMLElement), не рядки HTML.
   Документація для авторів екранів — KIT.md (цей файл читати не потрібно).
   ===================================================================== */
(function (E) {
  'use strict';
  /* ---------- core ---------- */
  function append(el, kids) {
    kids.forEach(function (k) {
      if (k == null || k === false || k === true) return;
      if (Array.isArray(k)) return append(el, k);
      el.appendChild(k instanceof Node ? k : document.createTextNode(String(k)));
    });
    return el;
  }
  function h(tag, attrs) {
    var el = document.createElement(tag), kids = Array.prototype.slice.call(arguments, 2);
    if (attrs && (attrs instanceof Node || typeof attrs !== 'object' || Array.isArray(attrs))) { kids.unshift(attrs); attrs = null; }
    if (attrs) Object.keys(attrs).forEach(function (k) {
      var v = attrs[k]; if (v == null || v === false) return;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k === 'html') el.innerHTML = v;
      else if (k === 'on') Object.keys(v).forEach(function (e) { el.addEventListener(e, v[e]); });
      else if (k === 'dataset') Object.keys(v).forEach(function (d) { el.dataset[d] = v[d]; });
      else if (k === 'hidden' || k === 'disabled' || k === 'checked' || k === 'readOnly' || k === 'required') el[k] = !!v;
      else if (k === 'value') el.value = v;
      else el.setAttribute(k, v === true ? '' : v);
    });
    return append(el, kids);
  }
  var esc = function (s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); };
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };
  var store = {
    get: function (k, d) { try { var v = localStorage.getItem('ecrhy.' + k); return v === null ? d : v; } catch (e) { return d; } },
    set: function (k, v) { try { localStorage.setItem('ecrhy.' + k, v); } catch (e) { } },
    del: function (k) { try { localStorage.removeItem('ecrhy.' + k); } catch (e) { } }
  };
  function rng(seed) { var a = seed >>> 0; return function () { a = (a + 0x6D2B79F5) >>> 0; var t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
  var uidN = 0; function uid(p) { return (p || 'ecr') + '-' + (++uidN); }
  function css(prefix, text) {
    if (document.querySelector('style[data-ecr-css="' + prefix + '"]')) return;
    if (/#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(/.test(text)) console.warn('[ECR.css] «' + prefix + '»: знайдено колір-літерал — дозволені лише токени var(--…)');
    document.head.appendChild(h('style', { 'data-ecr-css': prefix, text: text }));
  }

  /* ---------- formatting ---------- */
  var MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  var thin = ' ';
  function grp(s) { return s.replace(/\B(?=(\d{3})+(?!\d))/g, thin); }
  var fmt = {
    number: function (v, scale) { if (v == null || v === '' || isNaN(v)) return '—'; scale = scale == null ? 4 : scale; var s = Math.abs(v).toFixed(scale).split('.'); return (v < 0 ? '−' : '') + grp(s[0]) + (scale ? '.' + s[1] : ''); },
    int: function (v) { if (v == null || isNaN(v)) return '—'; return (v < 0 ? '−' : '') + grp(String(Math.abs(Math.round(v)))); },
    percent: function (v) { return v == null ? '—' : Math.round(v * 100) + ' %'; },
    date: function (d) { if (!d) return '—'; d = d instanceof Date ? d : new Date(d); return ('0' + d.getDate()).slice(-2) + ' ' + MONTHS[d.getMonth()].slice(0, 3) + ' ' + d.getFullYear(); },
    time: function (d) { d = d instanceof Date ? d : new Date(d); return ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2); },
    dateTime: function (d) { if (!d) return '—'; return fmt.date(d) + ', ' + fmt.time(d); },
    period: function (p) { if (!p) return '—'; var a = String(p).split('-'); return MONTHS[+a[1] - 1] + ' ' + a[0]; },
    duration: function (sec) { if (sec == null) return '—'; var m = Math.floor(sec / 60), s = Math.round(sec % 60); return m + ':' + ('0' + s).slice(-2); },
    plural: function (n, f) { return fmt.int(n) + ' ' + (n === 1 ? f.one : f.other); },
    bool: function (v) { return v ? 'Yes' : 'No'; },
    bytes: function (n) { if (n == null) return '—'; return n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; },
    initials: function (name) { return String(name).split(/[\s.]+/).filter(Boolean).map(function (w) { return w[0]; }).join('').slice(0, 2).toUpperCase(); }
  };

  /* ---------- icons: одна родина, сітка 24, stroke 1.5 ---------- */
  var C9 = 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z';
  var I = {
    file: 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5M9 13h6M9 17h6',
    fileX: 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5M9.5 12l5 6M14.5 12l-5 6',
    users: 'M16 20v-1.5a3.5 3.5 0 0 0-3.5-3.5h-5A3.5 3.5 0 0 0 4 18.5V20M10 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7zM20 20v-1.5a3.5 3.5 0 0 0-2.5-3.3M15.5 4.2a3.5 3.5 0 0 1 0 6.6',
    user: 'M18 20v-1.5a3.5 3.5 0 0 0-3.5-3.5h-5A3.5 3.5 0 0 0 6 18.5V20M12 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z',
    layout: 'M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM4 9h16M9 9v11',
    database: 'M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3zM4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3',
    sigma: 'M18 7V5H6l7 7-7 7h12v-2', code: 'M8 7l-5 5 5 5M16 7l5 5-5 5',
    ruler: 'M3 8h18v8H3zM7 8v3M11 8v4M15 8v3M19 8v4',
    plug: 'M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4',
    swap: 'M4 8h14M14 4l4 4-4 4M20 16H6M10 12l-4 4 4 4',
    shield: 'M12 3l7 3v5c0 4.5-3 8.2-7 10-4-1.8-7-5.5-7-10V6zM9 12l2 2 4-4',
    shieldAlert: 'M12 3l7 3v5c0 4.5-3 8.2-7 10-4-1.8-7-5.5-7-10V6zM12 8v4.5M12 15.5v.5',
    calendar: 'M5 5h14a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM4 10h16M8 3v4M16 3v4',
    stack: 'M12 3l9 4.5-9 4.5-9-4.5zM3 12l9 4.5 9-4.5M3 16.5L12 21l9-4.5',
    camera: 'M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1zM12 17a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z',
    history: 'M3.5 12a8.5 8.5 0 1 0 2.8-6.3M3 4v5h5M12 8v4l3 2',
    clock: C9 + 'M12 7v5l3 2',
    alert: 'M12 4l9 16H3zM12 10v4M12 17v.5', message: 'M4 5h16v11H9l-5 4zM8 9h8M8 12h5',
    pulse: 'M3 12h4l2-6 4 12 2-6h6', search: 'M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM20 20l-4-4',
    chevL: 'M15 6l-6 6 6 6', chevR: 'M9 6l6 6-6 6', chevD: 'M6 9l6 6 6-6', chevU: 'M6 15l6-6 6 6',
    arrowR: 'M5 12h14M13 6l6 6-6 6', arrowL: 'M19 12H5M11 6l-6 6 6 6', arrowU: 'M12 19V5M6 11l6-6 6 6',
    lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3', unlock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 6.8-1',
    check: 'M5 12.5l4.5 4.5L19 7.5', x: 'M6 6l12 12M18 6L6 18', minus: 'M5 12h14', plus: 'M12 5v14M5 12h14',
    sun: 'M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
    moon: 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z', monitor: 'M3 5h18v11H3zM8 20h8M12 16v4',
    rows4: 'M4 6h16M4 10h16M4 14h16M4 18h16', rows3: 'M4 7h16M4 12h16M4 17h16',
    upload: 'M12 16V4M7 9l5-5 5 5M4 20h16', download: 'M12 4v12M7 11l5 5 5-5M4 20h16',
    refresh: 'M20 11a8 8 0 0 0-14.5-4M4 4v4h4M4 13a8 8 0 0 0 14.5 4M20 20v-4h-4',
    panelL: 'M4 5h16v14H4zM9 5v14', panelR: 'M4 5h16v14H4zM15 5v14',
    info: C9 + 'M12 11v6M12 7.5v.5', checkCircle: C9 + 'M8 12.5l3 3 5-6', alertCircle: C9 + 'M12 7.5v5.5M12 16v.5', xCircle: C9 + 'M9 9l6 6M15 9l-6 6', ban: C9 + 'M5.6 5.6l12.8 12.8', help: C9 + 'M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.4-1 1-1 1.7M12 16.5v.5',
    circle: 'M12 19a7 7 0 1 0 0-14 7 7 0 0 0 0 14z', send: 'M21 3L10 14M21 3l-7 18-4-7-7-4z',
    undo: 'M4 9h10a6 6 0 0 1 0 12h-3M8 5L4 9l4 4', redo: 'M20 9H10a6 6 0 0 0 0 12h3M16 5l4 4-4 4',
    windows: 'M4 5.5l7-1v7H4zM13 4.2l7-1v8.3h-7zM4 13.5h7v7l-7-1zM13 13.5h7v8.3l-7-1z',
    eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
    eyeOff: 'M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6 0 10 7 10 7a17 17 0 0 1-3.2 3.9M6.5 6.6C3.7 8.5 2 12 2 12s4 7 10 7a9.500 9.500 0 0 0 4.300-1',
    menu: 'M4 7h16M4 12h16M4 17h16', more: 'M5 12h.01M12 12h.01M19 12h.01',
    sliders: 'M4 7h10M18 7h2M4 17h2M10 17h10M16 5v4M8 15v4',
    inbox: 'M4 13l3-8h10l3 8v6H4zM4 13h5l1 2h4l1-2h5',
    'return': 'M9 14L4 9l5-5M4 9h10a6 6 0 0 1 6 6v5', pencil: 'M4 20l4-1L19 8l-3-3L5 16z',
    tasks: 'M4 7l2 2 3-4M4 16l2 2 3-4M13 7h7M13 17h7',
    trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v5M14 11v5',
    copy: 'M9 9h11v11H9zM5 15H4V4h11v1', table: 'M4 5h16v14H4zM4 10h16M4 15h16M10 5v14',
    route: 'M6 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM18 9a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM8 17h7a3 3 0 0 0 0-6H9a3 3 0 0 1 0-6h7',
    filter: 'M4 5h16l-6 8v6l-4-2v-4z', key: 'M14.5 9.500a4 4 0 1 1-3.200 3.200L4 20v-3h3v-3h3l1.300-1.300',
    logout: 'M10 4H5v16h5M15 8l4 4-4 4M19 12H9', globe: C9 + 'M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18',
    play: 'M7 5l12 7-12 7z', stop: 'M6 6h12v12H6z', flag: 'M5 21V4h12l-2 4 2 4H5',
    link: 'M10 14a4 4 0 0 0 5.700 0l3-3a4 4 0 0 0-5.700-5.700l-1 1M14 10a4 4 0 0 0-5.700 0l-3 3a4 4 0 0 0 5.700 5.700l1-1',
    branch: 'M6 4v12M6 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM18 8a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM18 8c0 5-12 3-12 8',
    columns: 'M4 5h16v14H4zM12 5v14', grid: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
    gear: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19 12l2-1-1-3-2 .500-1.500-1.500L17 4l-3-1-1 2h-2l-1-2-3 1 .500 2L6 7.500 4 7l-1 3 2 1v2l-2 1 1 3 2-.500L7.500 18 7 20l3 1 1-2h2l1 2 3-1-.500-2 1.500-1.500 2 .500 1-3-2-1z',
    book: 'M5 4h12a2 2 0 0 1 2 2v14H7a2 2 0 0 1-2-2zM5 18a2 2 0 0 1 2-2h12M9 8h6',
    save: 'M5 4h11l3 3v13H5zM8 4v5h7V4M8 20v-6h8v6', external: 'M14 4h6v6M20 4l-9 9M18 14v6H4V6h6',
    grip: 'M9 6h.01M15 6h.01M9 12h.01M15 12h.01M9 18h.01M15 18h.01', fx: 'M10 20c2 0 2-2 2.500-5l1-7c.500-3 1-4 3-4M8 11h7',
    bolt: 'M13 3L5 14h6l-1 7 8-11h-6z', enter: 'M20 5v8a2 2 0 0 1-2 2H6M10 11l-4 4 4 4', paperclip: 'M20 11l-8 8a5 5 0 0 1-7-7l8-8a3.500 3.500 0 0 1 5 5l-8 8a2 2 0 0 1-3-3l7-7',
    archive: 'M3 5h18v4H3zM5 9v10h14V9M10 13h4', tag: 'M4 4h8l8 8-8 8-8-8zM8.500 8.500h.01', bell: 'M6 16V11a6 6 0 0 1 12 0v5l2 2H4zM10 20a2 2 0 0 0 4 0'
  };
  function iconHtml(n, s) { s = s || 16; return '<svg class="i" width="' + s + '" height="' + s + '" viewBox="0 0 24 24" aria-hidden="true"><path d="' + (I[n] || I.circle) + '"/></svg>'; }
  function icon(n, s) { var t = document.createElement('template'); t.innerHTML = iconHtml(n, s); return t.content.firstChild; }

  /* ---------- layers: dialogs, drawers ---------- */
  var layers = [];
  function focusables(el) { return $$('a[href],button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),[tabindex="0"]', el).filter(function (n) { return !n.closest('[hidden]'); }); }
  function syncInert() { var app = $('#app'), top = layers[layers.length - 1]; if (app) app.inert = layers.length > 0; layers.forEach(function (l) { l.el.inert = l !== top; }); var p = $('#proto'); if (p) p.inert = layers.length > 0; }
  function openLayer(box, o) {
    o = o || {};
    var layer = h('div', { class: 'layer ' + (o.layerClass || '') }, box), rec = { el: layer, box: box, prev: document.activeElement, o: o, closed: false };
    layer.addEventListener('mousedown', function (e) { if (e.target === layer && o.dismissable !== false) rec.close(); });
    layer.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && o.dismissable !== false) { e.stopPropagation(); rec.close(); return; }
      if (e.key !== 'Tab') return; var f = focusables(box); if (!f.length) { e.preventDefault(); return; }
      var first = f[0], last = f[f.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); } else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    });
    rec.close = function (result) {
      if (rec.closed) return; rec.closed = true; layer.remove(); layers.splice(layers.indexOf(rec), 1); syncInert();
      if (E._onLayerClose) E._onLayerClose(rec);
      if (rec.prev && rec.prev.focus && document.contains(rec.prev)) rec.prev.focus();
      if (o.onClose) o.onClose(result);
    };
    closeFloats(); $('#ecr-layers').appendChild(layer); layers.push(rec); syncInert();
    var f = $('[data-autofocus]', box) || focusables(box)[0]; if (f) f.focus();
    return rec;
  }
  function closeAllLayers() { layers.slice().reverse().forEach(function (l) { l.o.onClose = null; l.close(); }); }

  function footer(cls, buttons, ctl) {
    if (!buttons || !buttons.length) return null;
    var f = h('div', { class: cls }), spacer = false;
    buttons.forEach(function (b) {
      if (b.align !== 'left' && !spacer) { f.appendChild(h('span', { class: 'sp' })); spacer = true; }
      var btn = E.Button({ label: b.label, icon: b.icon, variant: b.variant, id: b.id, disabled: b.disabled, onClick: function () { var r = b.onClick ? b.onClick(ctl) : undefined; if (!b.keepOpen && r !== false) ctl.close(b.result); } });
      if (b.autofocus) btn.setAttribute('data-autofocus', ''); f.appendChild(btn);
    });
    return f;
  }
  /** Dialog — модальне вікно. Відкривається одразу. */
  function Dialog(o) {
    var id = o.id || uid('dlg'), ctl = {}, body = h('div', { class: 'dlg-b', id: id + '-b' });
    if (typeof o.body === 'function') o.body(body, ctl); else append(body, [o.body]);
    var box = h('div', { class: 'dialog' + (o.wide ? ' wide' : o.narrow ? ' narrow' : ''), role: o.alert ? 'alertdialog' : 'dialog', 'aria-modal': 'true', 'aria-labelledby': id + '-h', 'aria-describedby': id + '-b', id: id },
      h('div', { class: 'dlg-h' }, h('h2', { id: id + '-h' }, o.title), o.dismissable === false ? null : h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', id: id + '-x', on: { click: function () { ctl.close(); } } }, icon('x'))),
      o.steps || null, body);
    ctl.el = box; ctl.body = body; ctl.close = function (r) { rec.close(r); };
    ctl.setFooter = function (buttons) { var old = $('.dlg-f', box); if (old) old.remove(); var f = footer('dlg-f', buttons, ctl); if (f) box.appendChild(f); };
    ctl.setFooter(o.footer);
    var rec = openLayer(box, { dismissable: o.dismissable, onClose: o.onClose, kind: 'dialog', layerClass: o.layerClass });
    return ctl;
  }
  /** Drawer — права шторка подробиць (фокус-пастка, Esc, клік по тлу). Відкривається одразу. */
  function Drawer(o) {
    var id = o.id || uid('drw'), ctl = {}, body = h('div', { class: 'drawer-b' + (o.flush ? ' flush' : ''), id: id + '-b' });
    var box = h('aside', { class: 'drawer' + (o.wide ? ' wide' : ''), role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': id + '-h', id: id, lang: o.lang || null },
      h('div', { class: 'drawer-h' }, h('div', { class: 'dh-t' }, h('h2', { id: id + '-h' }, o.title), o.subtitle ? h('div', { class: 'muted t-xs' }, o.subtitle) : null, o.badge || null),
        h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', id: id + '-x', on: { click: function () { ctl.close(); } } }, icon('x'))), body);
    ctl.el = box; ctl.body = body; ctl.close = function (r) { rec.close(r); };
    if (typeof o.body === 'function') o.body(body, ctl); else append(body, [o.body]);
    var f = footer('drawer-f', o.footer, ctl); if (f) box.appendChild(f);
    var rec = openLayer(box, { onClose: o.onClose, kind: 'drawer', layerClass: 'layer-drawer' });
    return ctl;
  }

  /* ---------- floats: popover, menu ---------- */
  var floats = [];
  function closeFloats() { floats.slice().forEach(function (f) { f.close(); }); }
  function place(anchor, pop, align) {
    var r = anchor.getBoundingClientRect(), W = document.documentElement.clientWidth, H = document.documentElement.clientHeight;
    pop.style.left = '0px'; pop.style.top = '0px'; var pw = pop.offsetWidth, ph = pop.offsetHeight;
    var x = align === 'left' ? r.left : r.right - pw; x = Math.max(16, Math.min(W - pw - 16, x));
    var y = r.bottom + 4; if (y + ph > H - 8 && r.top - ph - 4 > 8) y = r.top - ph - 4; y = Math.max(8, Math.min(H - ph - 8, y));
    pop.style.left = x + 'px'; pop.style.top = y + 'px';
  }
  function floatOpen(anchor, pop, o) {
    o = o || {}; closeFloats();
    var host = layers.length ? layers[layers.length - 1].el : document.body; host.appendChild(pop); place(anchor, pop, o.align);
    anchor.setAttribute('aria-expanded', 'true');
    var rec = { close: function (refocus) { var i = floats.indexOf(rec); if (i < 0) return; floats.splice(i, 1); pop.remove(); anchor.setAttribute('aria-expanded', 'false'); document.removeEventListener('mousedown', out, true); document.removeEventListener('keydown', key, true); window.removeEventListener('resize', onRz); if (refocus && document.contains(anchor)) anchor.focus(); if (o.onClose) o.onClose(); }, el: pop };
    /* На resize НЕ закриваємо, а переставляємо біля якоря; закриваємо лише коли якір зник із розкладки (сховався на брейкпоінті, прибраний з DOM).
       Поріг «ширина змінилась > 8px» не рятував: headless-знімок перед кадром розтягує вікно 1416×808 → 1440×900 (виміряно), тобто ширина таки міняється на 24px. */
    function onRz() { if (!document.contains(anchor) || !anchor.getClientRects().length) rec.close(); else place(anchor, pop, o.align); }
    function out(e) { if (!pop.contains(e.target) && !anchor.contains(e.target)) rec.close(); }
    function key(e) {
      if (e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); rec.close(true); return; }
      if (!o.menu || (e.key !== 'ArrowDown' && e.key !== 'ArrowUp')) return; e.preventDefault();
      var it = $$('.menu-item:not(:disabled)', pop), i = it.indexOf(document.activeElement); i = e.key === 'ArrowDown' ? (i + 1) % it.length : (i - 1 + it.length) % it.length; if (it[i]) it[i].focus();
    }
    document.addEventListener('mousedown', out, true); document.addEventListener('keydown', key, true); window.addEventListener('resize', onRz);
    floats.push(rec); var f = focusables(pop)[0]; if (f && o.focus !== false) f.focus();
    return rec;
  }
  /** Popover(anchor, {title, content, width, align}) — подробиця без зміни екрана. */
  function Popover(anchor, o) {
    var pop = h('div', { class: 'pop padded', role: 'dialog', 'aria-label': o.title || 'Details', style: o.width ? 'width:' + o.width + 'px' : null }, o.title ? h('h3', o.title) : null);
    if (typeof o.content === 'function') o.content(pop); else append(pop, [typeof o.content === 'string' ? h('p', { class: 'muted' }, o.content) : o.content]);
    return floatOpen(anchor, pop, { align: o.align || 'left', onClose: o.onClose });
  }
  function menuItems(pop, items, close) {
    items.forEach(function (it) {
      if (!it) return;
      if (it.sep) return pop.appendChild(h('div', { class: 'menu-sep' + (it.smOnly ? ' sm-only' : '') }));
      if (it.heading) return pop.appendChild(h('div', { class: 'menu-h' }, it.heading));
      var attrs = { class: 'menu-item' + (it.danger ? ' danger' : '') + (it.smOnly ? ' sm-only' : ''), role: 'menuitem', id: it.id || null, title: it.title || null };
      var kids = [it.icon ? icon(it.icon) : null, it.label, it.kbd ? h('kbd', it.kbd) : null, it.hint ? h('small', it.hint) : null], el;
      if (it.href) { attrs.href = it.href; el = h('a', attrs, kids); el.addEventListener('click', function () { close(); }); }
      else { attrs.type = 'button'; attrs.disabled = !!it.disabled; el = h('button', attrs, kids); el.addEventListener('click', function () { close(); if (it.onClick) it.onClick(); }); }
      pop.appendChild(el);
    });
  }
  function openMenu(anchor, items, o) {
    o = o || {}; var pop = h('div', { class: 'pop', role: 'menu' }), rec;
    menuItems(pop, typeof items === 'function' ? items() : items, function () { rec.close(true); });
    rec = floatOpen(anchor, pop, { align: o.align || 'right', menu: true }); return rec;
  }
  /** Menu — кнопка «More» з випадним переліком дій. */
  function Menu(o) {
    var btn = o.iconOnly ? h('button', { class: 'icon-btn more-btn', type: 'button', id: o.id || null, 'aria-haspopup': 'menu', 'aria-expanded': 'false', 'aria-label': o.label || 'More actions', title: o.label || 'More actions' }, icon(o.icon || 'more'))
      : h('button', { class: 'btn more-btn' + (o.variant === 'ghost' ? ' btn-ghost' : ''), type: 'button', id: o.id || null, 'aria-haspopup': 'menu', 'aria-expanded': 'false' }, o.icon ? icon(o.icon) : null, o.label || 'More', icon('chevD', 14));
    btn.addEventListener('click', function (e) { e.stopPropagation(); if (btn.getAttribute('aria-expanded') === 'true') closeFloats(); else openMenu(btn, o.items, { align: o.align }); });
    return btn;
  }
  /** InlineConfirm(button, {text, verb, danger, onConfirm}) — легке підтвердження біля кнопки. */
  function InlineConfirm(anchor, o) {
    var rec, pop = h('div', { class: 'pop', role: 'alertdialog', 'aria-label': o.text },
      h('div', { class: 'inline-confirm' }, h('span', { class: 'grow' }, o.text),
        h('button', { class: 'btn btn-sm', type: 'button', on: { click: function () { rec.close(true); } } }, 'Cancel'),
        h('button', { class: 'btn btn-sm ' + (o.danger ? 'btn-danger' : 'btn-primary'), type: 'button', on: { click: function () { rec.close(true); o.onConfirm(); } } }, o.verb || 'Confirm')));
    rec = floatOpen(anchor, pop, { align: o.align || 'right' }); return rec;
  }

  /* ---------- toast ---------- */
  function Toast(msg, o) {
    o = typeof o === 'string' ? { tone: o } : (o || {});
    var ic = { success: 'checkCircle', danger: 'alertCircle', warning: 'alert' }[o.tone] || null;
    var t = h('div', { class: 'toast' }, ic ? icon(ic) : null, h('span', msg)), timer;
    function kill() { clearTimeout(timer); t.remove(); }
    if (o.action) t.appendChild(h('button', { class: 'link-btn', type: 'button', on: { click: function () { kill(); o.action.onClick(); } } }, o.action.label));
    $('#toasts').appendChild(t); timer = setTimeout(kill, o.duration || (o.action ? 7000 : 3400));
    var all = $$('#toasts .toast'); if (all.length > 3) all[0].remove();
    return { close: kill };
  }

  Object.assign(E, { h: h, append: append, esc: esc, $: $, $$: $$, store: store, rng: rng, uid: uid, css: css, fmt: fmt, MONTHS: MONTHS, icon: icon, iconHtml: iconHtml, icons: I,
    Dialog: Dialog, Drawer: Drawer, Popover: Popover, Menu: Menu, openMenu: openMenu, InlineConfirm: InlineConfirm, Toast: Toast,
    _float: floatOpen, _openLayer: openLayer, closeFloats: closeFloats, closeAllLayers: closeAllLayers, _layers: layers, _focusables: focusables });
})(window.ECR = window.ECR || {});

/* ===================== components ===================== */
(function (E) {
  'use strict';
  var h = E.h, icon = E.icon, fmt = E.fmt, $ = E.$, $$ = E.$$;

  function Button(o) {
    var cls = o.iconOnly ? 'icon-btn' : 'btn' + (o.variant && o.variant !== 'default' ? ' btn-' + o.variant : '') + (o.size ? ' btn-' + o.size : '');
    var attrs = { class: cls + (o.class ? ' ' + o.class : ''), id: o.id || null, title: o.title || (o.iconOnly ? o.label : null), 'aria-label': o.iconOnly ? o.label : null };
    var kids = [o.icon ? icon(o.icon, o.iconOnly ? 16 : 16) : null, o.iconOnly ? null : o.label, o.count != null ? h('span', { class: 'count ' + (o.countTone || '') }, o.count) : null, o.kbd ? h('kbd', o.kbd) : null], el;
    if (o.href) { attrs.href = o.href; el = h('a', attrs, kids); }
    else { attrs.type = o.submit ? 'submit' : 'button'; attrs.disabled = !!o.disabled; el = h('button', attrs, kids); }
    if (o.pressed != null) el.setAttribute('aria-pressed', String(!!o.pressed));
    if (o.onClick) el.addEventListener('click', function (e) { o.onClick(e, el); });
    return el;
  }
  function actionNode(a) { if (a instanceof Node) return a; return Button(a); }

  /** PageHeader — «← назад» → заголовок → один рядок пояснення → ОДНА головна дія (+≤2 другорядні, решта в More). */
  function PageHeader(o) {
    var back = o.back === false ? null : (o.ctx && o.ctx.from) || o.back || null;
    var sec = (o.secondary || []).slice(), more = (o.more || []).slice();
    if (sec.length > 2) { console.warn('[PageHeader] більше 2 другорядних дій — зайві перенесено в More'); sec.splice(2).forEach(function (a) { more.unshift({ label: a.label, icon: a.icon, onClick: a.onClick, href: a.href }); }); }
    var hadMore = more.length > 0; /* на вузькому екрані другорядні дії ховаються й дублюються в More */
    if (sec.length) { if (hadMore) more.unshift({ sep: true, smOnly: true }); sec.slice().reverse().forEach(function (a) { if (!(a instanceof Node)) more.unshift({ label: a.label, icon: a.icon, onClick: a.onClick, href: a.href, smOnly: true }); }); }
    var acts = h('div', { class: 'ph-actions' }, sec.map(actionNode), more.length ? (function () { var m = E.Menu({ label: 'More', items: more, id: (o.id || 'ph') + '-more' }); if (!hadMore) m.classList.add('sm-only'); return m; })() : null,
      o.primary ? actionNode(o.primary instanceof Node ? o.primary : Object.assign({ variant: 'primary' }, o.primary)) : null);
    return h('header', { class: 'page-head' },
      back ? h('a', { class: 'ph-back', href: back.href, 'data-back': '1' }, icon('arrowL', 14), 'Back to ' + back.label) : null,
      h('div', { class: 'ph-row' }, h('h1', { tabindex: '-1', id: (o.id || 'ph') + '-h1', class: o.mono ? 'mono' : null, 'data-page-title': '1' }, o.title), o.badge || null, o.count != null ? h('span', { class: 'count' }, fmt.int(o.count)) : null, acts.childNodes.length ? acts : null),
      o.subtitle ? h('p', { class: 'ph-sub' }, o.subtitle) : null, o.meta || null);
  }

  /** StatStrip — ОДНА приглушена смуга, ≤4 показники; клік фільтрує перелік. */
  function StatStrip(o) {
    var items = o.items.slice(0, 4), active = o.active || null, el = h('div', { class: 'statstrip', role: 'group', 'aria-label': o.label || 'Summary' });
    if (o.items.length > 4) console.warn('[StatStrip] більше 4 показників — зайві відкинуто');
    function paint() {
      el.textContent = '';
      items.forEach(function (it) {
        var clickable = !!o.onSelect && it.filter !== false, tone = it.tone && it.value ? ' ' + it.tone : '';
        var kids = [tone ? icon(it.tone === 'danger' ? 'alertCircle' : 'alert', 14) : null, h('b', typeof it.value === 'number' ? fmt.int(it.value) : it.value, it.of != null ? h('small', ' / ' + fmt.int(it.of)) : null), h('span', it.label)];
        var n = clickable ? h('button', { class: 'stat' + tone, type: 'button', id: (o.id || 'stat') + '-' + it.id, 'aria-pressed': String(active === it.id), title: it.hint || 'Filter the list: ' + it.label }, kids) : h('div', { class: 'stat' + tone, title: it.hint || null }, kids);
        if (clickable) n.addEventListener('click', function () { active = active === it.id ? null : it.id; paint(); o.onSelect(active); });
        el.appendChild(n);
      });
    }
    el.setActive = function (id) { active = id; paint(); };
    el.setItems = function (next) { items = next.slice(0, 4); paint(); };
    paint(); return el;
  }

  /* ---------- form fields ---------- */
  function fieldWrap(o, control, describe) {
    var ids = [], hint = o.hint ? h('div', { class: 'hint', id: o.id + '-hint' }, o.hint) : null, err = h('div', { class: 'hint bad', id: o.id + '-err', role: 'alert', hidden: true });
    if (hint) ids.push(o.id + '-hint'); ids.push(o.id + '-err'); describe.setAttribute('aria-describedby', ids.join(' '));
    var f = h('div', { class: 'field' + (o.class ? ' ' + o.class : '') }, o.label ? h('label', { for: o.id }, o.label, o.required ? h('span', { class: 'req', 'aria-hidden': 'true' }, '*') : null, o.optional ? h('span', { class: 'opt' }, 'optional') : null) : null, control, hint, err);
    f.input = describe;
    f.setError = function (msg) { err.textContent = ''; if (msg) { err.appendChild(icon('alertCircle', 14)); err.appendChild(document.createTextNode(msg)); } err.hidden = !msg; if (msg) describe.setAttribute('aria-invalid', 'true'); else describe.removeAttribute('aria-invalid'); };
    Object.defineProperty(f, 'value', { get: function () { return describe.type === 'checkbox' ? describe.checked : describe.value; }, set: function (v) { if (describe.type === 'checkbox') describe.checked = !!v; else describe.value = v; } });
    if (o.error) f.setError(o.error);
    return f;
  }
  function need(o, what) { if (!o.id) { o.id = E.uid('f'); console.warn('[' + what + '] поле без стабільного id — задай id'); } if (!o.label && !o.ariaLabel) console.warn('[' + what + '] #' + o.id + ': потрібен label або ariaLabel'); }
  function Input(o) {
    need(o, 'Input');
    var inp = h('input', { class: 'input' + (o.mono ? ' mono' : ''), id: o.id, name: o.name || o.id, type: o.type || 'text', value: o.value == null ? '' : o.value, placeholder: o.placeholder || null, disabled: o.disabled, readOnly: o.readOnly, required: o.required, autocomplete: o.autocomplete || 'off', 'aria-label': o.label ? null : o.ariaLabel, inputmode: o.inputmode || null, maxlength: o.maxlength || null });
    if (o.onInput) inp.addEventListener('input', function () { o.onInput(inp.value, inp); });
    if (o.onChange) inp.addEventListener('change', function () { o.onChange(inp.value, inp); });
    var control = o.icon ? h('div', { class: 'with-icon' }, icon(o.icon, 14), inp) : o.suffix ? h('div', { class: 'with-suffix' }, inp, h('span', { class: 'suffix' }, o.suffix)) : inp;
    if (o.bare) { control.input = inp; return control; }
    return fieldWrap(o, control, inp);
  }
  function Select(o) {
    need(o, 'Select');
    var sel = h('select', { class: 'select', id: o.id, name: o.name || o.id, disabled: o.disabled, 'aria-label': o.label ? null : o.ariaLabel },
      (o.options || []).map(function (op) { op = typeof op === 'object' ? op : { value: op, label: op }; var n = h('option', { value: op.value }, op.label); if (String(op.value) === String(o.value == null ? '' : o.value)) n.selected = true; return n; }));
    if (o.onChange) sel.addEventListener('change', function () { o.onChange(sel.value, sel); });
    if (o.bare) { sel.input = sel; return sel; }
    return fieldWrap(o, sel, sel);
  }
  function Textarea(o) {
    need(o, 'Textarea');
    var ta = h('textarea', { class: 'textarea' + (o.mono ? ' mono' : ''), id: o.id, name: o.name || o.id, rows: o.rows || 3, placeholder: o.placeholder || null, disabled: o.disabled, readOnly: o.readOnly, required: o.required, maxlength: o.maxlength || null, 'aria-label': o.label ? null : o.ariaLabel });
    ta.value = o.value || ''; if (o.onInput) ta.addEventListener('input', function () { o.onInput(ta.value, ta); });
    return fieldWrap(o, ta, ta);
  }
  function Checkbox(o) {
    need(o, 'Checkbox');
    var inp = h('input', { type: 'checkbox', id: o.id, name: o.name || o.id, checked: o.checked, disabled: o.disabled, 'aria-label': o.label ? null : o.ariaLabel, 'aria-describedby': o.hint ? o.id + '-hint' : null });
    if (o.onChange) inp.addEventListener('change', function () { o.onChange(inp.checked, inp); });
    var el = h('label', { class: 'check', for: o.id }, inp, h('span', o.label, o.hint ? h('div', { class: 'hint', id: o.id + '-hint' }, o.hint) : null)); el.input = inp; return el;
  }
  function Switch(o) {
    need(o, 'Switch');
    var st = h('span', { class: 'sw-state' }, o.checked ? 'On' : 'Off');
    var inp = h('input', { type: 'checkbox', role: 'switch', id: o.id, name: o.name || o.id, checked: o.checked, disabled: o.disabled, 'aria-label': o.label ? null : o.ariaLabel });
    inp.addEventListener('change', function () { st.textContent = inp.checked ? 'On' : 'Off'; if (o.onChange) o.onChange(inp.checked, inp); });
    var el = h('label', { class: 'switch', for: o.id }, inp, h('span', { class: 'grow' }, o.label, o.hint ? h('div', { class: 'hint' }, o.hint) : null), st); el.input = inp; return el;
  }
  function Field(o) { need(o, 'Field'); var c = o.control; return fieldWrap(o, c, c.input || c); }
  function Segmented(o) {
    var el = h('div', { class: 'seg', role: 'group', 'aria-label': o.label, id: o.id || null }), val = o.value;
    o.options.forEach(function (op) { op = typeof op === 'object' ? op : { value: op, label: op }; var b = h('button', { type: 'button', id: (o.id || 'seg') + '-' + op.value, 'aria-pressed': String(op.value === val), title: op.title || null, 'aria-label': op.icon && !op.label ? op.title : null }, op.icon ? icon(op.icon, 14) : null, op.label || null);
      b.addEventListener('click', function () { val = op.value; $$('button', el).forEach(function (x) { x.setAttribute('aria-pressed', String(x === b)); }); if (o.onChange) o.onChange(val); }); el.appendChild(b); });
    return el;
  }

  /** FilterBar — пошук + ≤3 вибірки + Clear. З memory фільтри переживають перехід «туди й назад». */
  function FilterBar(o) {
    var mem = o.memory ? (o.memory.filters = o.memory.filters || {}) : {}, el = h('div', { class: 'filters', role: 'search', 'aria-label': o.label || 'Filters' }), ctrls = {}, id = o.id || 'flt';
    function values() { var v = {}; Object.keys(ctrls).forEach(function (k) { v[k] = ctrls[k].value; }); return v; }
    function changed() { var v = values(); Object.keys(v).forEach(function (k) { mem[k] = v[k]; }); clear.hidden = !Object.keys(v).some(function (k) { return v[k]; }); if (o.onChange) o.onChange(v); }
    if (o.search !== false) { var s = Input({ id: id + '-q', bare: true, icon: 'search', type: 'search', ariaLabel: (o.search && o.search.placeholder) || 'Search', placeholder: (o.search && o.search.placeholder) || 'Search', value: mem.q || '', onInput: changed }); ctrls.q = s.input; el.appendChild(s); }
    (o.filters || []).forEach(function (f) { var sel = Select({ id: id + '-' + f.id, bare: true, ariaLabel: f.label, options: [{ value: '', label: f.all || ('All ' + f.label.toLowerCase()) }].concat(f.options), value: mem[f.id] != null ? mem[f.id] : (f.value || ''), onChange: changed }); ctrls[f.id] = sel; el.appendChild(sel); });
    var clear = Button({ label: 'Clear filters', icon: 'x', variant: 'ghost', id: id + '-clear', onClick: function () { el.reset(); } }); el.appendChild(clear);
    if (o.right) { el.appendChild(h('span', { class: 'sp' })); E.append(el, [o.right]); }
    el.values = values;
    el.set = function (k, v) { if (ctrls[k]) { ctrls[k].value = v; changed(); } };
    el.reset = function () { Object.keys(ctrls).forEach(function (k) { ctrls[k].value = ''; }); changed(); };
    clear.hidden = !Object.keys(values()).some(function (k) { return values()[k]; });
    return el;
  }

  /* ---------- states ---------- */
  function stateBox(cls, ic, title, text, rows) { return h('div', { class: 'state-fill' }, h('div', { class: 'state ' + cls }, h('div', { class: 'ico' }, icon(ic, 20)), h('h3', title), text ? h('p', text) : null, rows)); }
  function EmptyState(o) { return stateBox('', o.icon || 'inbox', o.title, o.text, o.actions && o.actions.length ? h('div', { class: 'st-row' }, o.actions.map(actionNode)) : null); }
  function ErrorState(o) {
    o = o || {}; var corr = o.correlationId || 'f0e2fed7-3a1e-49ce-984e-cbe9057fa1c4';
    return stateBox('err', 'alertCircle', o.title || 'This view could not be loaded', o.text || 'The server did not answer in time. Nothing was changed. Try again; if it repeats, send the correlation ID to support.', [
      h('div', { class: 'st-row' }, h('span', { class: 'chip', title: 'Stable error code' }, o.code || 'ECR-SYS-0503'), h('span', { class: 'chip', title: 'Correlation ID — include it when contacting support' }, corr),
        Button({ iconOnly: true, icon: 'copy', label: 'Copy correlation ID', onClick: function () { try { navigator.clipboard.writeText(corr); } catch (e) { } E.Toast('Correlation ID copied'); } })),
      h('div', { class: 'st-row' }, Button({ label: 'Retry', icon: 'refresh', variant: 'primary', onClick: o.onRetry || function () { E.Toast('Retrying…'); } }), o.back ? Button({ label: 'Back to ' + o.back.label, href: o.back.href }) : null)]);
  }
  function ForbiddenState(o) {
    return stateBox('', 'ban', 'You don’t have access to ' + o.area, null, [
      h('p', 'This area requires the permission ', h('b', { class: 't-b' }, o.permissionLabel || o.permission), '. Ask a system administrator to grant it to one of your roles.'),
      h('div', { class: 'st-row' }, h('span', { class: 'chip' }, o.permission), h('span', { class: 'chip' }, o.code || 'ECR-SEC-0403')),
      h('div', { class: 'st-row' }, Button({ label: 'Back to documents', href: '#/' }))]);
  }
  function Skeleton(o) {
    o = o || {}; var kind = o.kind || 'table', rows = o.rows || 8, cols = o.cols || 5, el = h('div', { class: 'sk' + (kind !== 'table' ? ' sk-' + kind : ''), 'aria-busy': 'true', 'aria-label': 'Loading' }), R = E.rng(rows * 31 + cols);
    for (var r = 0; r < rows; r++) {
      if (kind === 'table') { var line = h('div', { class: 'sk-r' }); for (var c = 0; c < cols; c++) line.appendChild(h('i', { style: 'width:' + Math.round(45 + R() * 50) + '%' })); el.appendChild(line); }
      else if (kind === 'form') el.appendChild(h('div', { class: 'sk-f' }, h('i', { style: 'width:' + Math.round(20 + R() * 20) + '%' }), h('i')));
      else el.appendChild(h('i', { style: 'width:' + Math.round(40 + R() * 55) + '%' }));
    }
    return el;
  }
  /** StateSwitch(state, {data, empty, error, loading}) — чотири стани для подань БЕЗ DataTable. */
  function StateSwitch(state, o) {
    if (state === 'loading') return o.loading instanceof Node ? o.loading : Skeleton(o.loading || { kind: 'form', rows: 5 });
    if (state === 'error') return ErrorState(o.error || {});
    if (state === 'empty') return o.empty instanceof Node ? o.empty : EmptyState(o.empty || { title: 'Nothing here yet' });
    return typeof o.data === 'function' ? o.data() : o.data;
  }

  /** DataTable — закріплена шапка, сортування, клікабельний рядок, 4 стани, «Show more». */
  function DataTable(o) {
    var id = o.id || E.uid('dt'), mem = o.memory ? (o.memory[id] = o.memory[id] || {}) : {}, rows = o.rows || [], state = o.state || 'data', filtered = false;
    var sort = mem.sort || o.sort || null, shown = mem.shown || o.pageSize || 50, selected = {}, selectedKey = o.selectedKey || null;
    var cols = o.columns; if (cols.length > 7) console.warn('[DataTable #' + id + '] ' + cols.length + ' колонок — дозволено ≤ 7, решту перенеси в шторку');
    var key = function (r) { return typeof o.rowKey === 'function' ? o.rowKey(r) : r[o.rowKey || 'id']; };
    var wrap = h('div', { class: 'table-wrap', id: id + '-wrap', tabindex: '-1' }), pager = h('div', { class: 'pager' }), el = h('div', { class: 'dt' + (o.auto ? ' auto' : ''), id: id }, wrap, pager);
    function sorted() { if (!sort) return rows; var c = cols.filter(function (x) { return x.key === sort.key; })[0]; if (!c) return rows; var get = c.sortValue || function (r) { return r[c.key]; };
      return rows.slice().sort(function (a, b) { var x = get(a), y = get(b); if (x == null) return 1; if (y == null) return -1; return (x > y ? 1 : x < y ? -1 : 0) * (sort.dir === 'desc' ? -1 : 1); }); }
    function cell(c, r) { var v = c.render ? c.render(r) : r[c.key]; if (v == null || v === '') v = h('span', { class: 'faint' }, '—'); else if (!(v instanceof Node)) v = c.num && typeof v === 'number' ? fmt.number(v, c.scale == null ? 0 : c.scale) : String(v);
      var td = h('td', { class: (c.num ? 'num' : '') + (c.mono ? ' mono' : '') + (c.hideSm ? ' hide-sm' : '') || null, style: c.width ? 'width:' + c.width + 'px' : null }, v); if (!(v instanceof Node)) td.title = v; return td; }
    function paint() {
      wrap.textContent = ''; pager.textContent = ''; wrap.removeAttribute('aria-busy');
      if (state === 'loading') { wrap.setAttribute('aria-busy', 'true'); wrap.appendChild(Skeleton({ kind: 'table', rows: o.skeletonRows || 10, cols: Math.min(cols.length, 6) })); return; }
      if (state === 'error') { wrap.appendChild(ErrorState(o.error || {})); return; }
      if (state === 'empty' || !rows.length) {
        wrap.appendChild(filtered && state !== 'empty' ? EmptyState({ icon: 'search', title: 'Nothing matches these filters', text: 'Records exist, but none fits the current search and filters.', actions: o.onClearFilters ? [{ label: 'Clear filters', onClick: o.onClearFilters }] : [] }) : EmptyState(o.empty || { title: 'Nothing here yet' })); return;
      }
      var list = sorted(), page = list.slice(0, shown), thead = h('tr');
      if (o.selectable) { var all = h('input', { type: 'checkbox', id: id + '-sel-all', 'aria-label': 'Select all rows' }); all.addEventListener('change', function () { page.forEach(function (r) { if (all.checked) selected[key(r)] = 1; else delete selected[key(r)]; }); paint(); emitSel(); }); all.checked = page.length > 0 && page.every(function (r) { return selected[key(r)]; }); thead.appendChild(h('th', { class: 'sel', scope: 'col' }, all)); }
      cols.forEach(function (c) {
        var th = h('th', { scope: 'col', class: (c.num ? 'num' : '') + (c.hideSm ? ' hide-sm' : '') || null, title: c.title || null });
        if (c.sortable === false || !c.key) th.textContent = c.label;
        else { if (sort && sort.key === c.key) th.setAttribute('aria-sort', sort.dir === 'desc' ? 'descending' : 'ascending');
          th.appendChild(h('button', { class: 'th-sort', type: 'button', id: id + '-sort-' + c.key, on: { click: function () { sort = sort && sort.key === c.key ? (sort.dir === 'asc' ? { key: c.key, dir: 'desc' } : null) : { key: c.key, dir: 'asc' }; mem.sort = sort; paint(); } } }, c.label, icon('arrowU', 12))); }
        thead.appendChild(th);
      });
      var tbody = h('tbody');
      page.forEach(function (r) {
        var k = key(r), tr = h('tr', { 'data-key': k, class: o.tall ? 'tall' : null });
        if (o.onRowClick) { tr.tabIndex = 0; tr.setAttribute('aria-label', (o.rowLabel ? o.rowLabel(r) : 'Open ' + k)); if (k === selectedKey) tr.setAttribute('aria-selected', 'true');
          tr.addEventListener('click', function (e) { if (e.target.closest('input,button,a,label')) return; o.onRowClick(r, tr); });
          tr.addEventListener('keydown', function (e) { if ((e.key === 'Enter' || e.key === ' ') && e.target === tr) { e.preventDefault(); o.onRowClick(r, tr); } }); }
        if (o.selectable) { var cb = h('input', { type: 'checkbox', id: id + '-sel-' + k, 'aria-label': 'Select ' + k, checked: !!selected[k] }); cb.addEventListener('change', function () { if (cb.checked) selected[k] = 1; else delete selected[k]; emitSel(); }); tr.appendChild(h('td', { class: 'sel' }, cb)); }
        cols.forEach(function (c) { tr.appendChild(cell(c, r)); }); tbody.appendChild(tr);
      });
      wrap.appendChild(h('table', { class: 'data' }, o.caption ? h('caption', { class: 'sr' }, o.caption) : null, h('thead', thead), tbody));
      pager.appendChild(h('span', 'Showing ' + fmt.int(page.length) + ' of ' + fmt.int(o.total || list.length)));
      if (page.length < list.length) pager.appendChild(Button({ label: 'Show more', size: 'sm', id: id + '-more', onClick: function () { shown += (o.pageSize || 50); mem.shown = shown; paint(); } }));
      if (mem.scroll) wrap.scrollTop = mem.scroll;
    }
    function emitSel() { if (o.onSelect) o.onSelect(Object.keys(selected)); }
    wrap.addEventListener('scroll', function () { mem.scroll = wrap.scrollTop; });
    el.setRows = function (next, opt) { rows = next || []; filtered = !!(opt && opt.filtered); if (state === 'empty' && rows.length) state = 'data'; paint(); };
    el.setState = function (s) { state = s; paint(); };
    el.select = function (k) { selectedKey = k; $$('tr[aria-selected]', wrap).forEach(function (t) { t.removeAttribute('aria-selected'); }); var tr = k != null && $('tr[data-key="' + k + '"]', wrap); if (tr) tr.setAttribute('aria-selected', 'true'); };
    el.selection = function () { return Object.keys(selected); };
    el.clearSelection = function () { selected = {}; paint(); emitSel(); };
    paint(); return el;
  }
  function Pagination(o) { return h('div', { class: 'pager' }, h('span', 'Showing ' + fmt.int(o.shown) + ' of ' + fmt.int(o.total)), o.shown < o.total ? Button({ label: 'Show more ' + fmt.int(Math.min(o.step || 50, o.total - o.shown)), size: 'sm', id: o.id || null, onClick: o.onMore }) : null); }

  /* ---------- StatusBadge: єдиний. Нормальний стан — нейтральний; колір лише в проблеми й «чекає дії». ---------- */
  var BADGES = {
    sheet: { Draft: ['', 'pencil'], Submitted: ['info', 'send', 'Waiting for approval'], Approved: ['', 'checkCircle'], Rejected: ['bad', 'xCircle'], Returned: ['warn', 'return', 'Returned for edits'] },
    period: { Open: ['', 'circle'], Grace: ['warn', 'clock', 'Grace period — closes soon'], Closed: ['', 'lock'], Archived: ['faint', 'archive'], NotOpened: ['faint', 'clock', null, 'Not opened'] },
    job: { Queued: ['', 'clock'], Running: ['info', 'spin'], Done: ['', 'checkCircle'], Failed: ['bad', 'xCircle'], Cancelled: ['faint', 'minus'] },
    version: { Draft: ['', 'pencil'], Published: ['', 'checkCircle'], Archived: ['faint', 'archive'] },
    health: { Healthy: ['', 'checkCircle'], Degraded: ['warn', 'alert'], Unhealthy: ['bad', 'xCircle'] },
    severity: { Info: ['', 'info'], Warning: ['warn', 'alert'], Error: ['bad', 'alertCircle'], Critical: ['crit', 'shieldAlert'] },
    user: { Active: ['', 'checkCircle'], Locked: ['bad', 'lock'], Disabled: ['faint', 'ban'], Invited: ['info', 'send'] }
  };
  function StatusBadge(kind, state, o) {
    o = o || {}; var b = (BADGES[kind] || {})[state] || ['', 'circle'], label = o.label || b[3] || state;
    return h('span', { class: 'badge ' + b[0] + (o.quiet ? ' quiet' : ''), title: o.title || b[2] || null, 'data-state': state }, b[1] === 'spin' ? h('span', { class: 'spin' }) : icon(b[1], 12), label);
  }
  StatusBadge.tone = function (kind, state) { var b = (BADGES[kind] || {})[state]; return b ? b[0] : ''; };

  /** SegmentBar — сегментована смужка аркушів документа (з B). Форма+колір: суцільний=Approved, половина=Submitted, пунктир=Draft. */
  function SegmentBar(o) {
    var segs = h('span', { class: 'segs', role: 'img', 'aria-label': o.segments.map(function (s) { return s.label + ': ' + s.state; }).join(', ') });
    o.segments.forEach(function (s, i) { var t = s.label + ' — ' + s.state + (s.note ? ' · ' + s.note : ''), cls = 'sg ' + String(s.state).toLowerCase();
      var n = o.onSelect ? h('button', { class: cls, type: 'button', title: t, 'aria-label': t, on: { click: function (e) { e.stopPropagation(); o.onSelect(s, i); } } }) : h('i', { class: cls, title: t }); segs.appendChild(n); });
    var done = o.segments.filter(function (s) { return s.state === (o.doneState || 'Approved'); }).length;
    return h('span', { class: 'segbar' + (o.size === 'lg' ? ' lg' : '') }, segs, o.summary === false ? null : h('span', { class: 'sg-l' }, o.summary || (done + ' of ' + o.segments.length + ' ' + (o.doneState || 'approved').toLowerCase())));
  }
  function Progress(o) { var pct = Math.max(0, Math.min(100, Math.round(o.value / (o.max || 100) * 100)));
    return h('div', { class: 'progress', id: o.id || null }, h('div', { class: 'bar ' + (o.tone || ''), role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-label': o.ariaLabel || o.label || 'Progress' }, h('i', { style: 'width:' + pct + '%' })), o.label === false ? null : h('span', { class: 'pg-l' }, o.label || pct + ' %')); }
  /** Stepper — компактний індикатор воркфлоу (з B), одна тонка смужка. */
  function Stepper(o) { var el = h('span', { class: 'stepper' + (o.labels ? ' labels' : ''), role: 'img', 'aria-label': 'Step ' + (o.current + 1) + ' of ' + o.steps.length + ': ' + o.steps[o.current] });
    o.steps.forEach(function (s, i) { el.appendChild(h('span', { class: 'stp' + (i < o.current ? ' done' : i === o.current ? ' cur ' + (o.tone || '') : ''), title: s }, h('i'), h('span', s))); }); return el; }

  /** Tabs — вкладки з синхронізацією ?tab=. */
  function Tabs(o) {
    var id = o.id || 'tabs', cur = (o.ctx && o.ctx.query.tab && o.tabs.some(function (t) { return t.id === o.ctx.query.tab; })) ? o.ctx.query.tab : (o.active || o.tabs[0].id);
    var list = h('div', { class: 'tabs' + (o.inset ? ' inset' : ''), role: 'tablist', 'aria-label': o.label || 'Sections' }), panel = h('div', { class: 'tabpanel', role: 'tabpanel', id: id + '-panel' }), el = h('div', { class: 'tabs-box' + (o.fill ? ' fill' : '') }, list, panel);
    function show(tid, user) { cur = tid; $$('.tab', list).forEach(function (b) { var on = b.dataset.tab === tid; b.setAttribute('aria-selected', String(on)); b.tabIndex = on ? 0 : -1; }); panel.textContent = ''; panel.setAttribute('aria-labelledby', id + '-' + tid);
      var t = o.tabs.filter(function (x) { return x.id === tid; })[0]; if (t.render) t.render(panel); if (user && o.ctx) o.ctx.setQuery({ tab: tid === o.tabs[0].id ? null : tid }); if (o.onChange) o.onChange(tid); }
    o.tabs.forEach(function (t) { var b = h('button', { class: 'tab', type: 'button', role: 'tab', id: id + '-' + t.id, 'data-tab': t.id, 'aria-controls': id + '-panel' }, t.label, t.count ? h('span', { class: 'count ' + (t.tone === 'danger' ? 'bad' : t.tone === 'warning' ? 'warn' : '') }, t.count) : null); b.addEventListener('click', function () { show(t.id, true); }); list.appendChild(b); });
    list.addEventListener('keydown', function (e) { if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return; var ids = o.tabs.map(function (t) { return t.id; }), i = ids.indexOf(cur); i = (i + (e.key === 'ArrowRight' ? 1 : -1) + ids.length) % ids.length; show(ids[i], true); $('#' + id + '-' + ids[i], list).focus(); });
    el.show = function (t) { show(t, true); }; el.panel = panel; el.current = function () { return cur; }; show(cur, false); return el;
  }
  function KeyValue(items, o) { o = o || {}; var dl = h('dl', { class: 'kv' + (o.wide ? ' wide' : '') });
    items.forEach(function (it) { if (!it) return; dl.appendChild(h('dt', it.label)); var v = it.value; dl.appendChild(h('dd', { class: it.mono ? 'mono' : null }, v == null || v === '' ? h('span', { class: 'faint' }, '—') : v, it.hint ? h('small', it.hint) : null)); }); return dl; }
  function CodeText(text, o) { o = o || {}; if (!o.block) return h('code', { class: 'code' }, text);
    return h('div', { class: 'code-wrap' }, h('pre', { class: 'code', tabindex: '0' }, text), o.copy === false ? null : Button({ iconOnly: true, icon: 'copy', label: 'Copy', onClick: function () { try { navigator.clipboard.writeText(text); } catch (e) { } E.Toast('Copied'); } })); }
  /** Collapsible — згорнута секція для рідкісних налаштувань. */
  function Collapsible(o) { var id = o.id || E.uid('cl'), body = h('div', { class: 'cl-b', id: id + '-b', hidden: !o.open }), built = false;
    function build() { if (built) return; built = true; if (typeof o.content === 'function') o.content(body); else E.append(body, [o.content]); }
    var btn = h('button', { class: 'cl-h', type: 'button', id: id, 'aria-expanded': String(!!o.open), 'aria-controls': id + '-b' }, icon('chevR', 14), o.title, o.summary ? h('small', o.summary) : null);
    btn.addEventListener('click', function () { var open = body.hidden; if (open) build(); body.hidden = !open; btn.setAttribute('aria-expanded', String(open)); if (o.onToggle) o.onToggle(open); });
    if (o.open) build(); return h('div', { class: 'collapsible' }, btn, body); }
  /** Banner / ResultBanner — рядок-повідомлення в потоці сторінки; ResultBanner = підсумок дії з «що далі». */
  function Banner(o) { var ic = o.icon || { success: 'checkCircle', warning: 'alert', danger: 'alertCircle', info: 'info' }[o.tone] || 'info', el;
    el = h('div', { class: 'banner ' + (o.tone || '') + (o.flush ? ' flush' : ''), role: o.tone === 'danger' ? 'alert' : 'status', id: o.id || null }, icon(ic), h('div', { class: 'bn-t' }, o.title ? h('b', o.title) : null, o.text ? h('p', o.text) : null),
      h('div', { class: 'bn-a' }, (o.actions || []).map(function (a) { return actionNode(a instanceof Node ? a : Object.assign({ size: 'sm' }, a)); }), o.onDismiss || o.dismissable ? Button({ iconOnly: true, icon: 'x', label: 'Dismiss', onClick: function () { el.remove(); if (o.onDismiss) o.onDismiss(); } }) : null));
    return el; }
  function ResultBanner(o) { return Banner(Object.assign({ tone: 'success', dismissable: true }, o)); }
  function Section(o) { return h('section', { class: 'section', id: o.id || null }, h('div', { class: 'section-h' }, h('h2', o.title), o.hint ? h('span', { class: 'muted t-xs' }, o.hint) : null, h('span', { class: 'sp' }), (o.actions || []).map(actionNode)), o.content); }
  function twoLine(a, b, o) { return h('span', { class: 'two-line' }, h('span', { class: o && o.mono ? 'mono' : null }, a), b ? h('small', b) : null); }

  /** PeriodPicker — «September 2026 · Open» зі стрілками й списком місяців. */
  function PeriodPicker(o) {
    var id = o.id || 'period', periods = o.periods || E.data.periods, val = o.value || E.data.currentPeriod, IC = { Open: 'circle', Grace: 'clock', Closed: 'lock', Archived: 'archive', NotOpened: 'clock' };
    var prev = h('button', { type: 'button', id: id + '-prev', 'aria-label': 'Previous period' }, icon('chevL')), next = h('button', { type: 'button', id: id + '-next', 'aria-label': 'Next period' }, icon('chevR'));
    var lbl = h('span'), st = h('span', { class: 'period-state' }), btn = h('button', { type: 'button', class: 'period-btn', id: id + '-btn', 'aria-haspopup': 'true', 'aria-expanded': 'false' }, icon('calendar'), lbl, st), el = h('div', { class: 'period', role: 'group', 'aria-label': 'Reporting period' }, prev, btn, next);
    function idx() { return periods.map(function (p) { return p.id; }).indexOf(val); }
    function paint() { var i = idx(), p = periods[i]; lbl.textContent = fmt.period(val); st.textContent = ''; st.className = 'period-state' + (p.state === 'Grace' ? ' warn' : ''); st.appendChild(icon(IC[p.state], 12)); st.appendChild(document.createTextNode(p.state === 'NotOpened' ? 'Not opened' : p.state)); btn.title = p.note || ''; prev.disabled = i <= 0; next.disabled = i >= periods.length - 1; }
    function set(v) { if (v === val) return; val = v; paint(); if (o.onChange) o.onChange(val, periods[idx()]); }
    prev.addEventListener('click', function () { set(periods[idx() - 1].id); }); next.addEventListener('click', function () { set(periods[idx() + 1].id); });
    btn.addEventListener('click', function () { var rec, pop = h('div', { class: 'pop', role: 'dialog', 'aria-label': 'Choose period', style: 'width:300px' }, h('div', { class: 'menu-h' }, 'Reporting period · ' + val.slice(0, 4)),
      h('div', { class: 'months' }, periods.map(function (p) { return h('button', { type: 'button', id: id + '-m-' + p.id, 'aria-current': String(p.id === val), title: fmt.period(p.id) + ' · ' + p.state, on: { click: function () { rec.close(true); set(p.id); } } }, E.MONTHS[+p.id.slice(5) - 1].slice(0, 3), icon(IC[p.state], 12)); })));
      rec = E._float(btn, pop, { align: 'left' }); });
    el.value = function () { return val; }; el.set = function (v) { val = v; paint(); }; paint(); return el;
  }

  Object.assign(E, { Button: Button, PageHeader: PageHeader, StatStrip: StatStrip, Input: Input, Select: Select, Textarea: Textarea, Checkbox: Checkbox, Switch: Switch, Field: Field, Segmented: Segmented, FilterBar: FilterBar,
    EmptyState: EmptyState, ErrorState: ErrorState, ForbiddenState: ForbiddenState, Skeleton: Skeleton, StateSwitch: StateSwitch, DataTable: DataTable, Pagination: Pagination, StatusBadge: StatusBadge, SegmentBar: SegmentBar,
    Progress: Progress, Stepper: Stepper, Tabs: Tabs, KeyValue: KeyValue, CodeText: CodeText, Collapsible: Collapsible, Banner: Banner, ResultBanner: ResultBanner, Section: Section, twoLine: twoLine, PeriodPicker: PeriodPicker });
})(window.ECR);

/* ===================== flows: confirm, reason, wizard, guard, result, list page ===================== */
(function (E) {
  'use strict';
  var h = E.h, icon = E.icon, fmt = E.fmt, $ = E.$;

  /** ConfirmDialog — назва об'єкта в заголовку, наслідки переліком, фокус на Cancel, дієслово на червоній кнопці. */
  function ConfirmDialog(o) {
    var danger = o.danger !== false, typed = null;
    var ctl = E.Dialog({ id: o.id || 'dlg-confirm', title: o.title, alert: true, onClose: o.onClose, body: function (b) {
      if (o.text) b.appendChild(h('p', o.text));
      if (o.consequences) b.appendChild(h('ul', { class: 'conseq' }, o.consequences.map(function (c) { c = typeof c === 'string' ? { text: c } : c; return h('li', { class: c.note ? 'note' : null }, icon(c.note ? 'info' : 'alert'), h('span', c.text)); })));
      if (o.typeToConfirm) { typed = E.Input({ id: (o.id || 'dlg-confirm') + '-type', label: 'Type “' + o.typeToConfirm + '” to confirm', mono: true, onInput: function (v) { $('#' + (o.id || 'dlg-confirm') + '-ok').disabled = v !== o.typeToConfirm; } }); b.appendChild(typed); }
    }, footer: [{ label: o.cancelLabel || 'Cancel', autofocus: true, id: (o.id || 'dlg-confirm') + '-cancel' }, { label: o.verb, icon: o.icon, variant: danger ? 'danger' : 'primary', id: (o.id || 'dlg-confirm') + '-ok', disabled: !!o.typeToConfirm, onClick: function () { o.onConfirm(); } }] });
    return ctl;
  }
  /** ReasonDialog — обов'язкове пояснення; кнопка вимкнена, доки порожньо. */
  function ReasonDialog(o) {
    var id = o.id || 'dlg-reason', min = o.minLength || 10, ta;
    return E.Dialog({ id: id, title: o.title, onClose: o.onClose, body: function (b) {
      if (o.text) b.appendChild(h('p', o.text));
      ta = E.Textarea({ id: id + '-text', label: o.label || 'Reason', required: true, rows: 4, maxlength: 500, placeholder: o.placeholder || '', hint: o.hint || 'At least ' + min + ' characters. The author sees this text; it is kept in the audit trail.',
        onInput: function (v) { $('#' + id + '-ok').disabled = v.trim().length < min; } }); ta.input.setAttribute('data-autofocus', ''); b.appendChild(ta);
      if (o.extra) E.append(b, [o.extra]);
    }, footer: [{ label: 'Cancel', id: id + '-cancel' }, { label: o.verb, icon: o.icon, variant: o.danger ? 'danger' : 'primary', id: id + '-ok', disabled: true, onClick: function () { o.onSubmit(ta.input.value.trim()); } }] });
  }
  /** UnsavedGuard — «є незбережені зміни»: Stay (фокус) / Discard / Save and leave. */
  function UnsavedGuard(o) {
    var n = o.count;
    return E.Dialog({ id: 'dlg-unsaved', title: n ? 'Leave with ' + fmt.plural(n, { one: 'unsaved change', other: 'unsaved changes' }) + '?' : 'Leave with unsaved changes?', alert: true, narrow: true, onClose: o.onClose,
      body: h('p', o.text || 'Your changes on this screen are not saved yet. If you leave now without saving, they will be lost.'),
      footer: [{ label: 'Discard changes', variant: 'ghost', align: 'left', id: 'dlg-unsaved-discard', onClick: function () { if (o.onDiscard) o.onDiscard(); } }, { label: 'Stay', autofocus: true, id: 'dlg-unsaved-stay', onClick: function () { if (o.onStay) o.onStay(); } },
        o.onSave ? { label: 'Save and leave', variant: 'primary', id: 'dlg-unsaved-save', onClick: function () { o.onSave(); } } : null].filter(Boolean) });
  }
  /** ResultDialog — проміжний екран результату дії з «що далі». Для легких випадків — ResultBanner. */
  function ResultDialog(o) {
    var ic = { success: 'checkCircle', warning: 'alert', danger: 'alertCircle' }[o.tone || 'success'];
    return E.Dialog({ id: o.id || 'dlg-result', title: o.title, narrow: !o.body, onClose: o.onClose, body: function (b) { if (o.text) b.appendChild(h('div', { class: 'row top' }, h('span', { class: o.tone === 'danger' ? 'danger-text' : o.tone === 'warning' ? 'warning-text' : 'muted' }, icon(ic, 20)), h('p', { class: 'grow' }, o.text))); if (o.body) E.append(b, [o.body]); },
      footer: (o.actions || [{ label: 'Done', variant: 'primary' }]).map(function (a, i, all) { return { label: a.label, icon: a.icon, variant: a.primary || (i === all.length - 1 && a.primary !== false) ? 'primary' : 'default', autofocus: i === all.length - 1, onClick: function () { if (a.href) E.go(a.href); if (a.onClick) a.onClick(); } }; }) });
  }
  /** Wizard — кроки, Back/Next, перевірка кроку, підсумок перед застосуванням. mount → сторінковий режим. */
  function Wizard(o) {
    var id = o.id || 'wizard', data = o.data || {}, steps = o.steps.concat([{ id: 'review', label: o.reviewLabel || 'Review', review: true }]), i = 0, ctl, body, stepsEl = h('div', { class: 'wizard-steps', 'aria-label': 'Steps' }), err;
    function paintSteps() { stepsEl.textContent = ''; steps.forEach(function (s, k) { stepsEl.appendChild(h('span', { class: 'wz-step' + (k === i ? ' cur' : k < i ? ' done' : ''), 'aria-current': k === i ? 'step' : null }, h('span', { class: 'n' }, k < i ? icon('check', 12) : String(k + 1)), s.label)); }); }
    function show() {
      paintSteps(); body.textContent = ''; err = null; var s = steps[i];
      if (s.hint) body.appendChild(h('p', s.hint));
      if (s.review) { body.appendChild(h('p', o.reviewText || 'Nothing is applied until you confirm. Check the summary below.')); E.append(body, [o.summary(data)]); } else s.render(body, data, api);
      setButtons(); var f = E._focusables(body)[0]; if (f) f.focus();
    }
    function fail(msg) { if (err) err.remove(); err = E.Banner({ tone: 'danger', text: msg, id: id + '-err' }); body.insertBefore(err, body.firstChild); }
    function next() { var s = steps[i], msg = s.validate ? s.validate(data) : null; if (msg) { fail(msg); return; } if (s.review) { o.onApply(data, api); return; } i++; show(); }
    var api = { data: data, next: next, fail: fail, close: function () { if (ctl) ctl.close(); }, refreshButtons: function () { setButtons(); } };
    function buttons() { var s = steps[i]; return [{ label: 'Cancel', variant: 'ghost', align: 'left', id: id + '-cancel' }, i > 0 ? { label: 'Back', icon: 'arrowL', id: id + '-back', keepOpen: true, onClick: function () { i--; show(); } } : null,
      { label: s.review ? (o.applyLabel || 'Apply') : 'Next', variant: s.review && o.danger ? 'danger' : 'primary', id: id + '-next', keepOpen: true, disabled: s.canNext ? !s.canNext(data) : false, onClick: next }].filter(Boolean); }
    function setButtons() { if (ctl) ctl.setFooter(buttons()); else { foot.textContent = ''; buttons().forEach(function (b) { if (b.align !== 'left' && !foot.querySelector('.sp')) foot.appendChild(h('span', { class: 'sp' })); foot.appendChild(E.Button({ label: b.label, icon: b.icon, variant: b.variant, id: b.id, disabled: b.disabled, onClick: b.onClick || function () { if (o.onCancel) o.onCancel(); } })); }); } }
    var foot;
    if (o.mount) { body = h('div', { class: 'stack gap-3 p-4' }); foot = h('div', { class: 'dlg-f' }); o.mount.appendChild(h('div', { class: 'panel', id: id }, h('div', { class: 'p-4' }, stepsEl), body, foot)); stepsEl.style.padding = '0'; show(); }
    else { ctl = E.Dialog({ id: id, title: o.title, wide: o.wide !== false, steps: stepsEl, onClose: o.onClose, body: function (b) { body = b; }, footer: [] }); show(); }
    return api;
  }

  /** ListPage — ЄДИНИЙ шаблон сторінки-переліку: PageHeader → StatStrip → FilterBar → DataTable → Drawer. */
  function ListPage(el, ctx, o) {
    var id = o.id || 'list', rows = o.rows || [], page = h('div', { class: 'page page-fill' }), data = ctx.state === 'data';
    var filters = o.filters || [], stats = o.stats || [], statActive = ctx.saved.stat || null, strip = null, bar, table, openKey = null;
    page.appendChild(E.PageHeader(Object.assign({ ctx: ctx, id: id }, o.header, { count: data && o.header.count !== false ? rows.length : null })));
    if (o.banner) E.append(page, [o.banner]);
    function statItems() { return stats.map(function (s) { return { id: s.id, label: s.label, of: s.of, tone: s.tone, hint: s.hint, filter: s.match ? undefined : false, value: typeof s.value === 'function' ? s.value(rows) : s.value != null ? s.value : rows.filter(s.match).length }; }); }
    if (stats.length && data) { strip = E.StatStrip({ id: id + '-stat', items: statItems(), active: statActive, onSelect: function (s) { statActive = ctx.saved.stat = s; apply(); } }); page.appendChild(strip); }
    bar = E.FilterBar({ id: id + '-flt', memory: ctx.saved, search: o.search === false ? false : { placeholder: (o.search && o.search.placeholder) || 'Search' }, filters: filters, right: o.filterRight, onChange: apply });
    if (o.search === false && !filters.length) bar.hidden = true; if (!data) bar.hidden = true; page.appendChild(bar);
    table = E.DataTable(Object.assign({ id: id + '-tbl', memory: ctx.saved, state: ctx.state, empty: o.empty, error: o.error, rowKey: 'id' }, o.table, { rows: [], onRowClick: (o.drawer || o.onRow) ? openRow : null, onClearFilters: function () { statActive = ctx.saved.stat = null; if (strip) strip.setActive(null); bar.reset(); } }));
    page.appendChild(table); el.appendChild(page);
    function keyOf(r) { var k = (o.table && o.table.rowKey) || 'id'; return typeof k === 'function' ? k(r) : r[k]; }
    function apply() {
      var v = bar.values(), q = (v.q || '').toLowerCase(), st = stats.filter(function (s) { return s.id === statActive; })[0];
      var list = rows.filter(function (r) { if (q && (o.search && o.search.text ? o.search.text(r) : Object.keys(r).map(function (k) { return r[k]; }).join(' ')).toLowerCase().indexOf(q) < 0) return false; if (st && st.match && !st.match(r)) return false;
        return filters.every(function (f) { return !v[f.id] || (f.match ? f.match(r, v[f.id]) : String(r[f.id]) === v[f.id]); }); });
      table.setRows(list, { filtered: list.length !== rows.length });
    }
    function openRow(r) {
      var k = keyOf(r); if (!o.drawer) return o.onRow(r); openKey = k; table.select(k); ctx.setQuery({ panel: k });
      E.Drawer(Object.assign({ id: id + '-drawer' }, o.drawer(r, api), { onClose: function () { openKey = null; table.select(null); } }));
    }
    if (o.drawer) ctx.panel('*', function (k) { var r = rows.filter(function (x) { return String(keyOf(x)) === k; })[0]; if (r) openRow(r); });
    var api = { page: page, table: table, filters: bar, stats: strip, setRows: function (next) { rows = next; if (strip) strip.setItems(statItems()); apply(); }, refresh: function () { if (strip) strip.setItems(statItems()); apply(); }, openRow: openRow };
    if (data) apply(); return api;
  }

  Object.assign(E, { ConfirmDialog: ConfirmDialog, ReasonDialog: ReasonDialog, UnsavedGuard: UnsavedGuard, ResultDialog: ResultDialog, Wizard: Wizard, ListPage: ListPage });
})(window.ECR);

/* ===================== router + shell ===================== */
(function (E) {
  'use strict';
  var h = E.h, icon = E.icon, $ = E.$, $$ = E.$$, store = E.store;
  var screens = [], flows = [], cur = null, fromMap = {}, savedMap = {}, pendingToast = null, forceNext = false, lastHash = '', ignoreHash = false, backNav = false, protoState = null, role = 'DataEntry', leaveGuard = null;
  var GROUPS = ['Work', 'Configure', 'Access', 'Operate'];

  E.screen = function (path, def) { var keys = [], rx = new RegExp('^' + path.replace(/\/$/, '').replace(/:[A-Za-z]+/g, function (m) { keys.push(m.slice(1)); return '([^/]+)'; }) + '/?$'); var rec = Object.assign({ path: path, rx: rx, keys: keys, seq: screens.length }, def); screens = screens.filter(function (s) { return s.path !== path; }); screens.push(rec); return rec; };
  E.flow = function (id, def) { flows = flows.filter(function (f) { return f.id !== id; }); flows.push(Object.assign({ id: id }, def)); };
  E.flows = function () { return flows.slice(); };
  E.screens = function () { return screens.slice(); };
  E.href = function (path, params) { var q = []; Object.keys(params || {}).forEach(function (k) { if (params[k] != null && params[k] !== '') q.push(encodeURIComponent(k) + '=' + encodeURIComponent(params[k])); }); return '#' + path + (q.length ? '?' + q.join('&') : ''); };
  function parse(hash) { var s = (hash == null ? location.hash : hash).replace(/^#/, '') || '/', i = s.indexOf('?'), path = i < 0 ? s : s.slice(0, i), query = {}; if (i >= 0) s.slice(i + 1).split('&').forEach(function (p) { if (!p) return; var kv = p.split('='); query[decodeURIComponent(kv[0])] = decodeURIComponent((kv[1] || '').replace(/\+/g, ' ')); }); if (path.charAt(0) !== '/') path = '/' + path; return { path: path, query: query }; }
  function match(path) { for (var i = 0; i < screens.length; i++) { var m = screens[i].rx.exec(path === '/' ? '' : path) || (path === '/' && screens[i].path === '/' ? [''] : null); if (m) { var p = {}; screens[i].keys.forEach(function (k, n) { p[k] = decodeURIComponent(m[n + 1]); }); return { def: screens[i], params: p }; } } return null; }
  function silentHash(hash) { lastHash = hash; try { history.replaceState(null, '', hash); } catch (e) { ignoreHash = true; location.replace(hash); } }
  /** ECR.go(path, {params, toast, force, replace, back}) — path може містити ?query. */
  E.go = function (path, o) {
    o = o || {}; var p = parse(path.replace(/^#/, '')), hash = E.href(p.path, Object.assign({}, p.query, o.params));
    if (o.toast) pendingToast = o.toast; if (o.force) forceNext = true; if (o.back) backNav = true;
    if (hash === location.hash || (hash === '#/' && !location.hash)) return onHash();
    if (o.replace) { silentHash(hash); onHash(); } else location.hash = hash;
  };
  E.back = function (fallback) { var f = cur && cur.ctx.from; E.go(f ? f.href : (fallback || '#/'), { back: true }); };
  E.me = function () { return E.data.users.filter(function (u) { return u.roles[0] === role; })[0] || E.data.users[1]; };
  E.role = function () { return role; };
  E.can = function (code) { return E.data.granted(role, code); };

  function onHash() {
    if (ignoreHash) { ignoreHash = false; return; }
    var next = parse();
    if (cur && !forceNext && next.path !== cur.path && cur.ctx._unsaved && cur.ctx._unsaved.fn()) {
      // Один сторож на екран: повторна спроба піти (Back кілька разів поспіль) лише оновлює ціль,
      // а не кладе ще один діалог поверх — інакше їх доводилось закривати по одному.
      if (leaveGuard) { leaveGuard.target = location.hash; silentHash(cur.hash); return; }
      var u = cur.ctx._unsaved; leaveGuard = { target: location.hash }; silentHash(cur.hash);
      var leave = function () { var t = leaveGuard ? leaveGuard.target : '#/'; leaveGuard = null; E.go(t, { force: true }); };
      E.UnsavedGuard({ count: u.count ? u.count() : null, text: u.text, onClose: function () { leaveGuard = null; }, onDiscard: function () { if (u.onDiscard) u.onDiscard(); leave(); }, onSave: u.onSave ? function () { u.onSave(); leave(); } : null }); return;
    }
    forceNext = false; render(next);
  }
  function applyPrefs(q) {
    var r = document.documentElement;
    if (q.theme === 'light' || q.theme === 'dark') r.setAttribute('data-theme', q.theme);
    if (q.density === 'compact' || q.density === 'comfortable') r.setAttribute('data-density', q.density);
    if (q.as) { var want = E.data.roles.filter(function (x) { return x.id.toLowerCase() === q.as.toLowerCase(); })[0]; if (want) role = want.id; }
  }
  function render(next) {
    applyPrefs(next.query); E.closeFloats(); E.closeAllLayers();
    if (cur) cur.ctx._leave.forEach(function (fn) { try { fn(); } catch (e) { } });
    var m = match(next.path) || { def: screens.filter(function (s) { return s.path === '/404'; })[0] || NOTFOUND, params: {} }, def = m.def, prev = cur;
    if (prev && prev.path !== next.path) { var pf = fromMap[prev.path]; if (!backNav && !(pf && pf.path === next.path)) fromMap[next.path] = { path: prev.path, href: prev.hash, label: prev.title }; }
    backNav = false;
    var state = next.query.state || protoState || 'data'; if (['data', 'empty', 'error', 'loading'].indexOf(state) < 0) state = 'data';
    var el = h('section', { class: 'view' }), dialogs = {}, panels = {}, actions = [], protos = [];
    var ctx = { path: next.path, params: m.params, query: next.query, state: state, from: fromMap[next.path] || null, saved: (savedMap[next.path] = savedMap[next.path] || {}), me: E.me(), role: role, _leave: [], _unsaved: null,
      setQuery: function (obj) { Object.keys(obj).forEach(function (k) { if (obj[k] == null) delete ctx.query[k]; else ctx.query[k] = obj[k]; }); var hs = E.href(ctx.path, ctx.query); cur.hash = hs; silentHash(hs); },
      dialog: function (id, fn) { dialogs[id] = fn; }, panel: function (id, fn) { panels[id] = fn; },
      openDialog: function (id, arg) { if (!dialogs[id]) return console.warn('[ctx.openDialog] невідомий діалог', id); ctx.setQuery({ dialog: id }); dialogs[id](arg); },
      openPanel: function (id, arg) { var fn = panels[id] || panels['*']; if (!fn) return; ctx.setQuery({ panel: id }); fn(panels[id] ? arg : id); },
      action: function (label, ic, fn) { actions.push({ label: label, icon: ic, run: fn }); },
      proto: function (c) { protos.push(c); },
      hasUnsaved: function (fn, o) { ctx._unsaved = Object.assign({ fn: fn }, o); }, onLeave: function (fn) { ctx._leave.push(fn); },
      setTitle: function (t) { cur.title = t; document.title = t + ' · ECR Web'; }, setCrumbs: function (list) { crumbs(list); },
      refresh: function () { render({ path: ctx.path, query: ctx.query }); } };
    cur = { def: def, path: next.path, hash: E.href(next.path, next.query), title: def.title || 'ECR Web', ctx: ctx, dialogs: dialogs, panels: panels, actions: actions, protos: protos }; lastHash = cur.hash;
    $('#app').classList.toggle('bare', def.chrome === false);
    var views = $('#views'); views.textContent = ''; views.appendChild(el); el.setAttribute('aria-label', cur.title);
    document.title = cur.title + ' · ECR Web';
    crumbs(typeof def.crumbs === 'function' ? def.crumbs(ctx) : def.crumbs || (def.group ? [{ label: def.group }, { label: def.title }] : [{ label: def.title }]));
    try { def.render(el, ctx); } catch (e) { console.error(e); el.textContent = ''; el.appendChild(E.ErrorState({ title: 'This screen failed to render', text: String(e && e.message || e), code: 'PROTOTYPE-JS' })); }
    paintRail(def); paintUser(); paintProto();
    var q = next.query;
    if (q.panel) { if (q.panel === 'tasks') openTasks(); else if (q.panel === 'notes') openNotes(); else { var pf2 = panels[q.panel] || panels['*']; if (pf2) pf2(panels[q.panel] ? undefined : q.panel); } }
    if (q.dialog) { if (q.dialog === 'command-palette') openPalette(); else if (q.dialog === 'unsaved-guard') E.UnsavedGuard({ count: 3, onSave: function () { E.Toast('Saved', 'success'); } }); else if (dialogs[q.dialog]) dialogs[q.dialog](); }
    if (!E._layers.length) { var t = $('[data-page-title]', el) || $('h1', el); if (t && !el.contains(document.activeElement)) t.focus({ preventScroll: true }); }
    if (pendingToast) { var pt = pendingToast; pendingToast = null; E.Toast(typeof pt === 'string' ? pt : pt.text, typeof pt === 'string' ? { tone: 'success' } : pt); }
  }
  E._onLayerClose = function (rec) { if (!cur) return; var q = cur.ctx.query; if (rec.o.kind === 'dialog' && q.dialog) cur.ctx.setQuery({ dialog: null }); if (rec.o.kind === 'drawer' && q.panel && !E._layers.some(function (l) { return l.o.kind === 'drawer'; })) cur.ctx.setQuery({ panel: null }); };
  var NOTFOUND = { title: 'Page not found', render: function (el, ctx) { el.appendChild(h('div', { class: 'page' }, E.EmptyState({ icon: 'route', title: 'This page does not exist', text: 'The address ' + ctx.path + ' does not match any screen. It may have been renamed or you may have followed an old link.', actions: [{ label: 'Go to documents', variant: 'primary', href: '#/' }, { label: 'Open the flow map', href: '#/flows' }] }))); } };

  /* ---------- shell ---------- */
  function crumbs(list) { var c = $('#crumbs'); c.textContent = ''; list.forEach(function (it, i) { if (i) c.appendChild(icon('chevR', 12)); c.appendChild(i === list.length - 1 ? h('b', it.label) : it.href ? h('a', { href: it.href }, it.label) : h('span', it.label)); }); }
  function navScreens() { return screens.filter(function (s) { return s.group && s.nav !== false && !s.keys.length; }).sort(function (a, b) { return GROUPS.indexOf(a.group) - GROUPS.indexOf(b.group) || (a.order == null ? 50 : a.order) - (b.order == null ? 50 : b.order) || a.seq - b.seq; }); }
  function paintRail(def) {
    var rail = $('#rail'), active = def.navPath || def.path, g = null; rail.textContent = '';
    navScreens().forEach(function (s) { if (s.group !== g) { g = s.group; rail.appendChild(h('div', { class: 'rail-g' }, g)); } rail.appendChild(h('a', { class: 'rail-item', href: '#' + s.path, title: s.title, 'aria-current': s.path === active ? 'page' : null, id: 'rail-' + (s.path.replace(/\W+/g, '-').replace(/^-|-$/g, '') || 'home') }, icon(s.icon || 'circle', 20), h('span', { class: 'rl' }, s.title))); });
    var exp = store.get('rail', '0') === '1'; rail.classList.toggle('expanded', exp);
    rail.appendChild(h('div', { class: 'rail-foot' }, h('button', { type: 'button', class: 'rail-item', id: 'rail-toggle', 'aria-expanded': String(exp), title: exp ? 'Collapse navigation' : 'Expand navigation', on: { click: function () { store.set('rail', exp ? '0' : '1'); paintRail(def); $('#rail-toggle').focus(); } } }, icon('chevR', 20), h('span', { class: 'rl' }, 'Collapse'))));
  }
  function paintUser() { var u = E.me(), b = $('#btn-user'); b.textContent = E.fmt.initials(u.name); b.setAttribute('aria-label', 'Account: ' + u.name); b.title = u.name + ' · ' + u.roles.join(', '); }
  function setTheme(v) { var r = document.documentElement; if (v === 'system') { r.removeAttribute('data-theme'); store.del('theme'); } else { r.setAttribute('data-theme', v); store.set('theme', v); } }
  function setDensity(v) { document.documentElement.setAttribute('data-density', v); store.set('density', v); }
  function openDisplay(btn) {
    E.Popover(btn, { title: 'Display', align: 'right', width: 240, content: function (p) {
      p.appendChild(E.Select({ id: 'pref-lang', label: 'Language', value: store.get('lang', 'en'), options: [{ value: 'en', label: 'EN · English' }, { value: 'ru', label: 'RU · Русский' }, { value: 'kk', label: 'KZ · Қазақша' }], onChange: function (v) { store.set('lang', v); E.Toast('Interface strings come from the server catalogue (' + v.toUpperCase() + ') — not switched in the prototype'); } }));
      p.appendChild(h('div', { class: 'field' }, h('span', { class: 'lbl' }, 'Theme'), E.Segmented({ id: 'pref-theme', label: 'Theme', value: document.documentElement.getAttribute('data-theme') || 'system', options: [{ value: 'light', icon: 'sun', label: 'Light' }, { value: 'dark', icon: 'moon', label: 'Dark' }, { value: 'system', icon: 'monitor', label: 'Auto' }], onChange: setTheme })));
      p.appendChild(h('div', { class: 'field' }, h('span', { class: 'lbl' }, 'Density'), E.Segmented({ id: 'pref-density', label: 'Density', value: document.documentElement.getAttribute('data-density') || 'compact', options: [{ value: 'compact', icon: 'rows4', label: 'Compact', title: '28 px rows' }, { value: 'comfortable', icon: 'rows3', label: 'Comfortable', title: '36 px rows' }], onChange: setDensity })));
    } });
  }

  /* ---------- My tasks ---------- */
  var tasks = [], tasksDrawer = null;
  function paintTaskCount() { var n = tasks.filter(function (t) { return t.state === 'Running'; }).length, c = $('#tasks-count'); c.textContent = n; c.hidden = !n; c.title = n + ' running'; }
  function taskNode(t) { return h('div', { class: 'task' }, h('div', { class: 'task-top' }, icon(t.icon || 'stack'), h('b', { title: t.title }, t.title), t.state === 'Running' ? h('span', { class: 'mono muted' }, Math.round(t.progress) + ' %') : E.StatusBadge('job', t.state)),
    t.state === 'Running' ? E.Progress({ value: t.progress, tone: 'accent', label: false, ariaLabel: t.title }) : null, h('div', { class: 'row between' }, h('span', { class: 'muted t-xs' }, t.detail || ''), t.state === 'Done' && t.result ? h('button', { class: 'link-btn t-xs', type: 'button', on: { click: t.result.onClick } }, t.result.label) : t.state === 'Failed' ? h('a', { class: 't-xs', href: '#/admin/jobs?panel=' + t.id }, 'See why') : null)); }
  function paintTasks() { if (!tasksDrawer) return; var b = tasksDrawer.body; b.textContent = ''; if (!tasks.length) b.appendChild(E.EmptyState({ icon: 'tasks', title: 'No background operations', text: 'Exports, imports and recalculations you start appear here with their progress.' })); tasks.forEach(function (t) { b.appendChild(taskNode(t)); }); }
  function openTasks() { if (tasksDrawer) return; tasksDrawer = E.Drawer({ id: 'drawer-tasks', title: 'My tasks', subtitle: 'Background operations you started', flush: true, body: function () { }, footer: [{ label: 'Open all jobs', icon: 'stack', align: 'left', onClick: function () { E.go('/admin/jobs'); } }, { label: 'Close' }], onClose: function () { tasksDrawer = null; } }); paintTasks(); }
  E.tasks = { list: function () { return tasks; }, open: openTasks,
    /** start({title, icon, detail, duration, doneDetail, result:{label,onClick}, onProgress, onDone}) */
    start: function (o) { var t = { id: 'J-' + (10430 + tasks.length), title: o.title, icon: o.icon, detail: o.detail, state: 'Running', progress: 0, result: o.result }; tasks.unshift(t); paintTaskCount(); paintTasks();
      var step = 100 / ((o.duration || 4000) / 200), timer = setInterval(function () { t.progress = Math.min(100, t.progress + step); if (o.onProgress) o.onProgress(t.progress); if (t.progress >= 100) { clearInterval(timer); t.state = 'Done'; t.detail = o.doneDetail || 'Finished ' + E.fmt.time(new Date()); paintTaskCount(); if (o.onDone) o.onDone(t); } paintTasks(); }, 200); return t; } };

  /* ---------- command palette (з B) ---------- */
  function openPalette() {
    var items = [], idx = 0, input = h('input', { id: 'palette-input', type: 'text', role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'pal-list', 'aria-label': 'Search screens, documents and actions', placeholder: 'Type a screen, a document key or an action…', autocomplete: 'off', 'data-autofocus': '' }), list = h('div', { class: 'pal-list', id: 'pal-list', role: 'listbox' }), rec;
    var all = [];
    navScreens().concat(screens.filter(function (s) { return s.palette && !s.group; })).forEach(function (s) { all.push({ g: 'Screens', t: s.title, s: s.group || '', i: s.icon || 'layout', run: function () { E.go(s.path); } }); });
    E.data.documents.forEach(function (d) { all.push({ g: 'Documents', t: d.id, s: d.facility + ' · ' + d.project + ' · ' + d.owner, i: 'file', run: function () { E.go('/documents/' + d.id); } }); });
    if (cur) cur.actions.forEach(function (a) { all.push({ g: 'Actions on this screen', t: a.label, s: '', i: a.icon || 'bolt', run: a.run }); });
    [['Open My tasks', 'tasks', openTasks], ['Switch theme — light / dark', 'sun', function () { var d = getComputedStyle(document.documentElement).colorScheme.indexOf('dark') >= 0; setTheme(d ? 'light' : 'dark'); }], ['Toggle density — 28 / 36 px', 'rows4', function () { setDensity(document.documentElement.getAttribute('data-density') === 'comfortable' ? 'compact' : 'comfortable'); }], ['Нотатки дизайнера', 'message', openNotes]].forEach(function (x) { all.push({ g: 'Actions', t: x[0], s: '', i: x[1], run: x[2] }); });
    flows.forEach(function (f) { all.push({ g: 'Flows', t: f.title, s: f.steps.length + ' steps', i: 'route', run: function () { E.go('/flows'); } }); });
    function paint() {
      var q = input.value.trim().toLowerCase(), toks = q.split(/\s+/).filter(Boolean), per = {}, last = '';
      items = (toks.length ? all.filter(function (x) { var hay = (x.t + ' ' + x.s + ' ' + x.g).toLowerCase(); if (!toks.every(function (t) { return hay.indexOf(t) >= 0; })) return false; per[x.g] = (per[x.g] || 0) + 1; return per[x.g] <= 6; }) : all.filter(function (x) { return x.g === 'Actions on this screen' || x.g === 'Screens'; }).slice(0, 10)).slice(0, 18);
      idx = Math.min(idx, Math.max(0, items.length - 1)); list.textContent = '';
      if (!items.length) list.appendChild(h('div', { class: 'mini-empty' }, h('b', { class: 't-sb' }, 'Nothing matches “' + input.value + '”'), h('span', 'Try a document key like DOC-000004, a screen name or an action.')));
      items.forEach(function (x, i) { if (x.g !== last) { last = x.g; list.appendChild(h('div', { class: 'pal-g eyebrow' }, x.g)); } var b = h('button', { class: 'pi', type: 'button', role: 'option', id: 'pi-' + i, tabindex: '-1', 'aria-selected': String(i === idx) }, icon(x.i), h('span', { class: 'tx' }, h('span', x.t), x.s ? h('small', x.s) : null)); b.addEventListener('click', function () { run(i); }); b.addEventListener('mousemove', function () { if (idx !== i) { idx = i; sel(); } }); list.appendChild(b); });
      input.setAttribute('aria-activedescendant', items.length ? 'pi-' + idx : '');
    }
    function sel() { $$('.pi', list).forEach(function (b, i) { b.setAttribute('aria-selected', String(i === idx)); }); var e = $('#pi-' + idx, list); if (e) e.scrollIntoView({ block: 'nearest' }); input.setAttribute('aria-activedescendant', 'pi-' + idx); }
    function run(i) { var x = items[i]; if (!x) return; rec.close(); x.run(); }
    input.addEventListener('input', function () { idx = 0; paint(); });
    input.addEventListener('keydown', function (e) { if (e.key === 'ArrowDown') { e.preventDefault(); idx = (idx + 1) % Math.max(1, items.length); sel(); } else if (e.key === 'ArrowUp') { e.preventDefault(); idx = (idx - 1 + items.length) % Math.max(1, items.length); sel(); } else if (e.key === 'Enter') { e.preventDefault(); run(idx); } });
    var box = h('div', { class: 'palette', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Command palette', id: 'palette' }, h('div', { class: 'pal-in' }, icon('search'), input, h('kbd', 'Esc')), list, h('div', { class: 'pal-f' }, h('span', h('kbd', '↑'), ' ', h('kbd', '↓'), ' move'), h('span', h('kbd', 'Enter'), ' open')));
    paint(); rec = E._openLayer(box, { kind: 'dialog', layerClass: 'layer-top' });
  }

  /* ---------- designer notes ---------- */
  var NOTES = [
    ['Основа — A «Workbench».', 'Токени, IBM Plex Sans/Mono, рейка, верхня смуга 40 px, навігатор таблиць, рядок формул, сітка з усією логікою (клавіатура, вставка TSV, undo/redo, рядок Σ), інспектор, вкладки аркушів унизу, діалоги — ФВ-14.2, 14.4, 14.29.'],
    ['З B «Control Room» — лише статистика, і приглушено.', 'StatStrip: одна смуга, ≤ 4 показники, без плиток і кольорових фонів; показник клікабельний і фільтрує перелік. SegmentBar аркушів документа, компактний степер воркфлоу, робоча командна палітра Ctrl K, шторка «My tasks».'],
    ['Спокій за замовчуванням.', 'Інспектор закритий, доки не клікнуто «N issues» чи History; навігатор показує лише поточний аркуш, групи згорнуті, крім поточної; рейка — лише іконки. Три рядки службових панелей A стиснуто до двох.'],
    ['Одна головна дія на екран.', '≤ 2 другорядні поруч, решта — у «More». PageHeader сам переносить зайві дії в меню. Мова, тема й щільність зведені в одну кнопку «Display» — ФВ-14.14.'],
    ['Колір — лише там, де щось не так.', 'Нормальні стани (Draft, Approved, Open, Healthy, Done) нейтральні: сіра іконка + слово. Кольором і формою — лише Rejected, Returned, Failed, Grace, помилки та «чекає дії» (Submitted). Кожен стан має два носії — ФВ-14.15–14.18.'],
    ['Один шаблон сторінки-переліку.', 'PageHeader → StatStrip → фільтри → таблиця ≤ 7 колонок → шторка подробиць. Подробиці — у Drawer, а не на новій сторінці, щоб не губити контекст; фільтри, сортування й прокрутка переживають перехід «туди й назад».'],
    ['Чотири стани кожного подання.', 'Дані / порожньо (пояснення + дія) / помилка (код, correlation id, Retry) / скелет. Спільний перемикач — «Prototype» або ?state= в адресі — ФВ-14.21–14.25.'],
    ['Переходи пропрацьовані, а не намальовані.', 'Wizard із підсумком перед застосуванням, UnsavedGuard при виході з незбереженим, ResultBanner «що далі» після дії, InlineConfirm для дрібного, Undo в тості після видалення. Карта сценаріїв — екран /flows, кожен крок клікабельний.'],
    ['Небезпечні дії.', 'ConfirmDialog називає об’єкт і наслідки, фокус на Cancel, дієслово на червоній кнопці. Відхилення й повернення аркуша вимагають пояснення (ReasonDialog) — кнопка вимкнена, доки порожньо — ФВ-14.9.'],
    ['Людські підписи замість кодів.', '«September 2026 · Open» замість 202609; код права, ключ проєкту чи ідентифікатор задачі — приглушено другим рядком або в підказці. Числа — моноширинно, праворуч, з вузьким пробілом між розрядами — ФВ-14.29.'],
    ['Доступність і рух.', 'Кільце фокуса 2 px завжди видиме (ФВ-14.19), підписи полів не placeholder’ом, помилки через aria-describedby (ФВ-14.20), фокус-пастка в діалогах і шторках, переходи ≤ 150 мс, prefers-reduced-motion вимикає рух (ФВ-14.27–14.28).'],
    ['Щільність і вузький екран.', 'Compact 28 / Comfortable 36 px через CSS-змінні діє на сітку, таблиці, поля й рейку. На ~400 px рейка — висувний шар за «бургером», панелі — на всю ширину поверх, дії — в «More» — ФВ-14.12–14.14, 14.30.']];
  function openNotes() { E.Drawer({ id: 'drawer-notes', lang: 'uk', title: 'Нотатки дизайнера · гібрид A + B', subtitle: 'Що взято, що спрощено і чому', body: function (b) { b.appendChild(h('ol', { class: 'notes-list' }, NOTES.map(function (n) { return h('li', h('b', n[0]), ' ', h('span', n[1])); }))); b.appendChild(h('div', { class: 'glyphs' }, h('small', 'Перевірка гліфів · IBM Plex Sans / Mono'), 'Қазақша: Шығарындылар көзі — ә ғ қ ң ө ұ ү һ і', h('br'), h('span', { class: 'mono' }, 'Ә Ғ Қ Ң Ө Ұ Ү Һ І · 12 345,6789'))); } }); }

  /* ---------- prototype panel ---------- */
  function paintProto() {
    var p = $('#proto-panel'); p.textContent = '';
    var opts = screens.filter(function (s) { return !s.keys.length || s.example; }).map(function (s) { return { value: s.example || s.path, label: (s.group ? s.group + ' · ' : '') + s.title }; });
    p.appendChild(h('h3', 'Screen')); p.appendChild(E.Select({ id: 'proto-screen', bare: true, ariaLabel: 'Screen', options: opts, value: cur.path, onChange: function (v) { E.go(v); } }));
    p.appendChild(h('h3', 'View state')); p.appendChild(E.Segmented({ id: 'proto-state', label: 'View state', value: cur.ctx.state, options: ['data', 'empty', 'error', 'loading'], onChange: function (v) { protoState = v === 'data' ? null : v; var q = Object.assign({}, cur.ctx.query); delete q.state; E.go(cur.path, { params: q }); } }));
    p.appendChild(h('h3', 'Act as')); p.appendChild(E.Segmented({ id: 'proto-role', label: 'Act as', value: role, options: [{ value: 'DataEntry', label: 'Data entry' }, { value: 'Approver', label: 'Approver' }, { value: 'SystemAdministrator', label: 'Admin' }], onChange: function (v) { role = v; var q = Object.assign({}, cur.ctx.query); delete q.as; E.go(cur.path, { params: q }); } }));
    cur.protos.forEach(function (c) { p.appendChild(h('h3', c.label)); if (c.options) p.appendChild(E.Segmented({ id: 'proto-' + c.id, label: c.label, value: c.value, options: c.options, onChange: c.onChange })); else p.appendChild(h('div', { class: 'prow' }, c.buttons.map(function (b) { return E.Button({ label: b.label, size: 'sm', onClick: b.onClick }); }))); });
    var dl = Object.keys(cur.dialogs); if (dl.length) { p.appendChild(h('h3', 'Dialogs · ?dialog=')); p.appendChild(h('div', { class: 'prow' }, dl.map(function (id) { return E.Button({ label: id, size: 'sm', onClick: function () { cur.ctx.openDialog(id); } }); }))); }
    var pl = Object.keys(cur.panels).filter(function (k) { return k !== '*'; }); if (pl.length) { p.appendChild(h('h3', 'Panels · ?panel=')); p.appendChild(h('div', { class: 'prow' }, pl.map(function (id) { return E.Button({ label: id, size: 'sm', onClick: function () { cur.ctx.openPanel(id); } }); }))); }
    p.appendChild(h('div', { class: 'prow' }, E.Button({ label: 'Flow map', icon: 'route', size: 'sm', href: '#/flows' })));
  }

  /* ---------- /flows: карта переходів ---------- */
  E.screen('/flows', { title: 'Flow map', group: 'Work', icon: 'route', order: 99, render: function (el, ctx) {
    var page = h('div', { class: 'page' }); el.appendChild(page);
    page.appendChild(E.PageHeader({ id: 'flows', title: 'Flow map', count: flows.length, subtitle: 'Every user journey, step by step. Each step is a link to the exact screen, dialog or panel — click through any path from start to end.', back: false }));
    var groups = {}; flows.forEach(function (f) { (groups[f.group || 'Other'] = groups[f.group || 'Other'] || []).push(f); });
    Object.keys(groups).forEach(function (g) { page.appendChild(E.Section({ title: g, hint: E.fmt.plural(groups[g].length, { one: 'flow', other: 'flows' }), content: h('div', groups[g].map(function (f) {
      return h('div', { class: 'flow', id: 'flow-' + f.id }, h('h3', f.title, f.actor ? h('span', { class: 'badge quiet' }, icon('user', 12), f.actor) : null), f.note ? h('p', { class: 'muted t-xs' }, f.note) : null, h('ol', f.steps.map(function (s, i) { return h('li', h('a', { href: s.href, title: s.href }, h('span', { class: 'n' }, i + 1), s.label)); }))); })) })); });
    if (!flows.length) page.appendChild(E.EmptyState({ icon: 'route', title: 'No flows registered', text: 'Screen files register their journeys with ECR.flow(id, {title, steps}).' }));
  } });

  /* ---------- start ---------- */
  E.start = function () {
    var saved = store.get('theme', null); if (saved === 'light' || saved === 'dark') document.documentElement.setAttribute('data-theme', saved); /* немає збереженого вибору — тему глядача НЕ чіпаємо */
    document.documentElement.setAttribute('data-density', store.get('density', 'compact'));
    $('#btn-menu').appendChild(icon('menu', 20)); $('#btn-cmdk').insertBefore(icon('search'), $('#btn-cmdk').firstChild); $('#btn-tasks').insertBefore(icon('tasks'), $('#btn-tasks').firstChild); $('#btn-display').appendChild(icon('sliders'));
    $('#proto-toggle').insertBefore(icon('sliders', 14), $('#proto-toggle').firstChild); $('#notes-toggle').insertBefore(icon('message', 14), $('#notes-toggle').firstChild);
    $('#btn-menu').addEventListener('click', function () { var o = $('#rail').classList.toggle('open'); this.setAttribute('aria-expanded', String(o)); });
    document.addEventListener('click', function (e) { if (!e.target.closest('#rail') && !e.target.closest('#btn-menu')) $('#rail').classList.remove('open'); var a = e.target.closest('a[data-back]'); if (a) backNav = true; });
    $('#btn-cmdk').addEventListener('click', openPalette); $('#btn-tasks').addEventListener('click', openTasks); $('#notes-toggle').addEventListener('click', openNotes);
    $('#btn-display').addEventListener('click', function () { openDisplay(this); });
    $('#btn-user').addEventListener('click', function () { var u = E.me(); E.openMenu(this, [{ heading: u.name + ' · ' + u.login }, { label: 'Roles: ' + u.roles.join(', '), icon: 'shield', disabled: true }, { sep: true }, { label: 'My groups', icon: 'users', href: '#/my-groups' }, { label: 'Change password', icon: 'key', href: '#/change-password' }, { sep: true }, { label: 'Sign out', icon: 'logout', onClick: function () { E.go('/login', { toast: 'You are signed out' }); } }]); });
    $('#proto-toggle').addEventListener('click', function () { var p = $('#proto-panel'); p.hidden = !p.hidden; this.setAttribute('aria-expanded', String(!p.hidden)); });
    document.addEventListener('keydown', function (e) { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); if (!$('#palette')) openPalette(); } });
    window.addEventListener('hashchange', onHash);
    window.addEventListener('beforeunload', function (e) { if (cur && cur.ctx._unsaved && cur.ctx._unsaved.fn()) { e.preventDefault(); e.returnValue = ''; } });
    E.data.jobs.filter(function (j) { return j.by === E.me().name; }).slice(0, 3).forEach(function (j) { tasks.push({ id: j.id, title: j.type + ' · ' + j.target, icon: /export/i.test(j.type) ? 'download' : /import/i.test(j.type) ? 'upload' : 'refresh', state: j.state === 'Running' ? 'Done' : j.state, progress: 100, detail: j.result || (j.error ? j.error.slice(0, 60) + '…' : 'Started ' + j.started) }); });
    paintTaskCount(); onHash();
  };
})(window.ECR);

/* ===================== ECR.data — спільні демо-дані (детерміновано, seed) ===================== */
(function (E) {
  'use strict';
  var rng = E.rng, D = {};
  D.today = '2026-09-18T14:03:00';
  D.project = { id: 'P07131100', name: 'Central processing facility', company: 'Atyrau gas processing plant', template: 'GEN07131100', templateVersion: '1.0.0' };
  D.projects = [['P07131100', 'Central processing facility'], ['P07131200', 'Gas treatment unit'], ['P07131300', 'Oil stabilisation'], ['P07132100', 'Tank farm — East'], ['P07132200', 'Export pipeline KP 0–84'], ['P07133100', 'Power plant'], ['P07133200', 'Well pads — North'], ['P07134100', 'Well pads — South'], ['P07134200', 'Utilities & flare system'], ['P07135100', 'Water injection plant'], ['P07135200', 'Sulphur recovery unit'], ['P07136100', 'Camp & support base']].map(function (a) { return { id: a[0], name: a[1] }; });

  /* periods 2026 */
  D.currentPeriod = '2026-09';
  D.periods = E.MONTHS.map(function (m, i) { var id = '2026-' + ('0' + (i + 1)).slice(-2), st = i < 6 ? 'Archived' : i < 8 ? 'Closed' : i === 8 ? 'Open' : 'NotOpened';
    return { id: id, name: m + ' 2026', state: st, opens: '2026-' + ('0' + (i + 1)).slice(-2) + '-01', closes: i === 8 ? '2026-09-30' : null, daysLeft: i === 8 ? 12 : null, graceDays: 5,
      note: st === 'Open' ? 'closes in 12 days · 30 Sep 2026' : st === 'Closed' ? 'closed ' + (i === 7 ? '05 Sep 2026' : '05 Aug 2026') + ' · read-only' : st === 'Archived' ? 'archived · read-only' : 'opens 01 ' + m.slice(0, 3) + ' 2026', closedBy: st === 'Closed' ? 'B. Sadykov' : null }; });
  D.period = function (id) { return D.periods.filter(function (p) { return p.id === id; })[0]; };

  /* users, roles, permissions */
  D.roles = [['Approver', 'Approves or rejects submitted sheets', 1], ['Auditor', 'Reads everything, changes nothing', 1], ['BootstrapAdministrator', 'First-run account: security only', 1], ['DataEntry', 'Enters, imports and submits data', 1], ['PeriodAdministrator', 'Opens, closes and reopens periods', 1], ['SystemAdministrator', 'Full access', 1], ['TemplateAdministrator', 'Templates, registries and calculations', 1], ['Viewer', 'Read-only access to documents', 1]].map(function (a) { return { id: a[0], name: a[0].replace(/([a-z])([A-Z])/g, '$1 $2'), description: a[1], builtIn: !!a[2] }; });
  D.users = [['u-01', 'A. Iskakov', 'Askar Iskakov', 'CORP\\a.iskakov', ['Approver'], 'Windows', 'Active', '2026-09-18T13:40'], ['u-02', 'D. Akhmetova', 'Dana Akhmetova', 'CORP\\d.akhmetova', ['DataEntry'], 'Windows', 'Active', '2026-09-18T14:01'], ['u-03', 'M. Tulegenov', 'Marat Tulegenov', 'CORP\\m.tulegenov', ['SystemAdministrator'], 'Windows', 'Active', '2026-09-18T09:12'],
    ['u-04', 'M. Petrenko', 'Mykola Petrenko', 'CORP\\m.petrenko', ['DataEntry'], 'Windows', 'Active', '2026-09-18T10:05'], ['u-05', 'S. Nurlanov', 'Serik Nurlanov', 'CORP\\s.nurlanov', ['DataEntry', 'Viewer'], 'Windows', 'Active', '2026-09-17T17:45'], ['u-06', 'G. Tulegenova', 'Gulnara Tulegenova', 'CORP\\g.tulegenova', ['TemplateAdministrator'], 'Windows', 'Active', '2026-09-18T11:20'],
    ['u-07', 'O. Kovalenko', 'Olena Kovalenko', 'CORP\\o.kovalenko', ['Auditor'], 'Windows', 'Active', '2026-09-16T09:12'], ['u-08', 'B. Sadykov', 'Bolat Sadykov', 'CORP\\b.sadykov', ['PeriodAdministrator'], 'Windows', 'Active', '2026-09-05T18:02'], ['u-09', 'R. Dzhaksybekov', 'Ruslan Dzhaksybekov', 'r.dzhaksybekov', ['Viewer'], 'Local', 'Locked', '2026-09-11T08:55'],
    ['u-10', 'svc-import', 'Import service account', 'svc-import', ['DataEntry'], 'Local', 'Active', '2026-09-18T13:52'], ['u-11', 'bootstrap', 'Bootstrap administrator', 'bootstrap', ['BootstrapAdministrator'], 'Local', 'Disabled', '2025-11-03T10:00'], ['u-12', 'K. Abenova', 'Kamila Abenova', 'CORP\\k.abenova', ['DataEntry'], 'Windows', 'Invited', null]
  ].map(function (a) { return { id: a[0], name: a[1], fullName: a[2], login: a[3], roles: a[4], auth: a[5], state: a[6], lastSeen: a[7] }; });
  D.user = function (name) { return D.users.filter(function (u) { return u.name === name || u.id === name; })[0]; };
  D.permissions = [['Document', [['View', 'View documents'], ['Create', 'Create documents'], ['Edit', 'Enter and edit data'], ['Import', 'Import from Excel'], ['Export', 'Export to Excel'], ['Submit', 'Submit sheets for approval'], ['Approve', 'Approve or reject sheets'], ['Reopen', 'Return approved sheets for edits', 1], ['Delete', 'Delete documents and sheets', 1]]],
    ['Calculation', [['EditConstant', 'Edit constants'], ['EditFormula', 'Edit formulas'], ['EditRule', 'Edit validation rules'], ['EditScript', 'Edit scripts', 1], ['ManageRequiredInputs', 'Manage required inputs'], ['Publish', 'Publish calculation versions', 1], ['Recalculate', 'Run recalculation']]],
    ['Template', [['View', 'View templates'], ['Edit', 'Edit template drafts'], ['Publish', 'Publish template versions', 1], ['Clone', 'Clone versions'], ['ManageAccessMatrix', 'Manage cell access matrix']]],
    ['Registry', [['View', 'View registries'], ['Edit', 'Edit registry entries'], ['Import', 'Import registries'], ['ManageUnits', 'Manage units']]],
    ['Period', [['View', 'View periods'], ['Open', 'Open a period'], ['Close', 'Close a period'], ['Reopen', 'Reopen a closed period', 1], ['ManageAccessRules', 'Manage period access rules']]],
    ['Security', [['ViewRoles', 'View roles and grants'], ['ManageRoles', 'Create and change roles', 1], ['ManageGrants', 'Grant roles to users', 1], ['ManageUsers', 'Manage local users']]],
    ['System', [['ViewHealth', 'View system health'], ['ViewAudit', 'View audit trail'], ['ManageJobs', 'Cancel and retry jobs'], ['ManageInterfaceTexts', 'Edit interface texts'], ['ManageSources', 'Configure data sources'], ['PurgeArchive', 'Purge archived data', 1]]]
  ].map(function (g) { return { domain: g[0], items: g[1].map(function (p) { return { code: g[0] + '.' + p[0], label: p[1], dangerous: !!p[2] }; }) }; });
  D.grants = { Approver: ['Document.View', 'Document.Approve', 'Document.Reopen', 'Document.Export', 'Period.View', 'Template.View', 'Registry.View'], Auditor: ['Document.View', 'Document.Export', 'Template.View', 'Registry.View', 'Period.View', 'Security.ViewRoles', 'System.ViewHealth', 'System.ViewAudit'],
    BootstrapAdministrator: ['Security.*'], DataEntry: ['Document.View', 'Document.Create', 'Document.Edit', 'Document.Submit', 'Document.Import', 'Document.Export', 'Calculation.Recalculate', 'Period.View', 'Template.View', 'Registry.View'],
    PeriodAdministrator: ['Document.View', 'Period.*'], SystemAdministrator: ['*'], TemplateAdministrator: ['Document.View', 'Template.*', 'Calculation.*', 'Registry.*'], Viewer: ['Document.View', 'Template.View', 'Period.View', 'Registry.View'] };
  D.granted = function (role, code) { var g = D.grants[role] || []; return g.indexOf('*') >= 0 || g.indexOf(code) >= 0 || g.indexOf(code.split('.')[0] + '.*') >= 0; };
  D.groups = [['g-01', 'CPF — data entry', 'Enter data for Central processing facility', ['u-02', 'u-04', 'u-12'], ['P07131100', 'P07131200']], ['g-02', 'Upstream — data entry', 'Well pads and water injection', ['u-05', 'u-04'], ['P07133200', 'P07134100', 'P07135100']], ['g-03', 'HSE approvers', 'Approve sheets of all projects', ['u-01'], ['*']], ['g-04', 'Auditors', 'Read-only, all projects', ['u-07'], ['*']]
  ].map(function (a) { return { id: a[0], name: a[1], description: a[2], members: a[3], projects: a[4] }; });

  /* sheets of DOC-000001 + documents */
  D.sheetNames = ['Air emissions', 'Water use & discharge', 'Waste', 'GHG inventory', 'Energy'];
  D.sheetTables = [91, 24, 37, 58, 12];
  var LET = { d: 'Draft', s: 'Submitted', a: 'Approved', r: 'Returned', x: 'Rejected' }, OWN = ['D. Akhmetova', 'M. Petrenko', 'S. Nurlanov', 'D. Akhmetova', 'M. Petrenko', 'S. Nurlanov'];
  D.documents = [['dsadr', 0, '2026-09-18T14:03', 0], ['aaaaa', 1, '2026-09-12T16:40', 0], ['aaass', 2, '2026-09-17T17:45', 0], ['aassd', 3, '2026-09-18T11:20', 0], ['aaaas', 4, '2026-09-16T09:12', 0], ['ddddd', 5, '2026-09-18T13:48', 0], ['assdd', 1, '2026-09-18T10:05', 0], ['aaadx', 2, '2026-09-17T18:31', 1], ['ssddd', 0, '2026-09-15T14:22', 0], ['aaddd', 3, '2026-09-18T09:30', 0], ['aasdr', 4, '2026-09-17T12:02', 1], ['ddddd', 5, '2026-09-11T08:55', 0]
  ].map(function (a, i) { var R = rng(91 + i * 13), p = D.projects[i];
    var sheets = a[0].split('').map(function (c, s) { var st = LET[c], total = D.sheetTables[s], filled = st === 'Draft' || st === 'Returned' || st === 'Rejected' ? Math.round(total * (0.2 + R() * 0.7)) : total; return { name: D.sheetNames[s], state: st, tables: total, filled: filled, issues: 0 }; });
    if (i === 0) { sheets[0].filled = 68; sheets[0].issues = 7; sheets[4].issues = 2; }
    return { id: 'DOC-' + ('00000' + (i + 1)).slice(-6), project: p.id, facility: p.name, period: '2026-09', owner: OWN[a[1]], approver: 'A. Iskakov', updated: a[2], late: !!a[3], template: 'GEN' + p.id.slice(1), version: '1.0.0', sheets: sheets }; });
  D.doc = function (id) { return D.documents.filter(function (d) { return d.id === id; })[0]; };
  D.docState = function (d) { var s = d.sheets.map(function (x) { return x.state; }); if (s.every(function (x) { return x === 'Approved'; })) return 'Approved'; if (s.indexOf('Rejected') >= 0) return 'Rejected'; if (s.indexOf('Returned') >= 0) return 'Returned'; if (s.indexOf('Draft') < 0) return 'Submitted'; return 'Draft'; };
  D.campaign = function () { var c = { documents: D.documents.length, sheets: 0, filled: 0, submitted: 0, approved: 0, attention: 0, issues: 0, late: 0 }; D.documents.forEach(function (d) { if (d.late) c.late++; d.sheets.forEach(function (s) { c.sheets++; if (s.filled === s.tables) c.filled++; if (s.state === 'Submitted') c.submitted++; if (s.state === 'Approved') c.approved++; if (s.state === 'Rejected' || s.state === 'Returned') c.attention++; c.issues += s.issues; }); }); return c; };

  /* registries */
  D.registries = [['EmissionSources', 'Emission sources', 'Stacks, flares, engines and other release points', 42, '2026-09-10', 'G. Tulegenova', 6], ['Substances', 'Substances', 'Pollutants with CAS numbers and hazard class', 118, '2026-08-21', 'G. Tulegenova', 5], ['FuelTypes', 'Fuel types', 'Fuels with net calorific value and density', 14, '2026-06-02', 'G. Tulegenova', 4], ['Units', 'Units', 'Units of measure and conversion factors', 37, '2026-05-15', 'M. Tulegenov', 4], ['PermitLimits', 'Permit limits', 'Annual limits by source and substance', 306, '2026-01-12', 'G. Tulegenova', 5], ['WasteCodes', 'Waste codes', 'Classifier of waste types and hazard levels', 214, '2025-12-01', 'G. Tulegenova', 4], ['Facilities', 'Facilities', 'Production sites and their coordinates', 12, '2025-11-20', 'M. Tulegenov', 5], ['MonitoringPoints', 'Monitoring points', 'Water and air sampling locations', 28, '2026-07-08', 'G. Tulegenova', 5]
  ].map(function (a) { return { code: a[0], name: a[1], description: a[2], entries: a[3], updated: a[4], updatedBy: a[5], fields: a[6] }; });
  D.emissionSources = ['Flare stack FS-101', 'Flare stack FS-102', 'Boiler B-1', 'Boiler B-2', 'Boiler B-3', 'Gas turbine GT-1', 'Gas turbine GT-2', 'Gas turbine GT-3', 'Gas turbine GT-4', 'Thermal oxidizer TO-1', 'Thermal oxidizer TO-2', 'Process heater H-201', 'Process heater H-202', 'Process heater H-305', 'Diesel generator DG-1', 'Diesel generator DG-2', 'Glycol reboiler GR-1', 'Amine regenerator AR-1', 'Sulphur recovery unit SRU-1', 'Sulphur recovery unit SRU-2', 'Incinerator IN-1', 'Storage tank T-401', 'Storage tank T-402', 'Storage tank T-403', 'Loading rack LR-1', 'Compressor station CS-1', 'Compressor station CS-2', 'Fired heater FH-11', 'Fired heater FH-12', 'Dehydration unit DU-1', 'Vent stack VS-7', 'Vent stack VS-9', 'Emergency flare EF-1', 'Steam boiler SB-4', 'Crude heater CH-2', 'Hot oil heater HO-1', 'Pilot burner PB-3', 'Fugitive — valves & flanges', 'Fugitive — pump seals', 'Wastewater pond WP-1', 'Cooling tower CT-2', 'Workshop welding WS-1'];
  D.registryEntries = { EmissionSources: D.emissionSources.map(function (n, i) { var R = rng(300 + i); return { id: 'ES-' + ('00' + (i + 1)).slice(-3), name: n, kind: /Fugitive|pond|tower|Workshop|tank|rack/.test(n) ? 'Area' : 'Point', height: /Fugitive|pond/.test(n) ? null : +(8 + R() * 72).toFixed(1), facility: D.projects[i % 4].name, active: i !== 8, validFrom: '2024-01-01' }; }),
    Substances: [['SO₂', 'Sulphur dioxide', '7446-09-5', 3], ['NOₓ', 'Nitrogen oxides (as NO₂)', '10102-44-0', 2], ['CO', 'Carbon monoxide', '630-08-0', 4], ['VOC', 'Volatile organic compounds', '—', 4], ['PM₁₀', 'Particulate matter ≤ 10 µm', '—', 3], ['PM₂.₅', 'Particulate matter ≤ 2.5 µm', '—', 3], ['H₂S', 'Hydrogen sulphide', '7783-06-4', 2], ['CH₄', 'Methane', '74-82-8', 4], ['CO₂', 'Carbon dioxide', '124-38-9', 4], ['N₂O', 'Nitrous oxide', '10024-97-2', 4], ['C₆H₆', 'Benzene', '71-43-2', 2], ['NH₃', 'Ammonia', '7664-41-7', 4]].map(function (a, i) { return { id: 'SB-' + ('00' + (i + 1)).slice(-3), name: a[1], formula: a[0], cas: a[2], hazardClass: a[3], active: true }; }),
    FuelTypes: [['Fuel gas', '10³ m³', 34.2], ['Associated gas', '10³ m³', 38.9], ['Diesel', 't', 42.7], ['Fuel oil', 't', 40.4], ['LPG', 't', 46.1], ['Petrol', 't', 43.9]].map(function (a, i) { return { id: 'FT-' + ('00' + (i + 1)).slice(-3), name: a[0], unit: a[1], ncv: a[2], active: true }; }) };
  D.units = [['t', 'tonne', 'Mass', 1, 'kg', 1000], ['kg', 'kilogram', 'Mass', 0, 'kg', 1], ['g', 'gram', 'Mass', 0, 'kg', 0.001], ['10³ m³', 'thousand cubic metres', 'Volume', 0, 'm³', 1000], ['m³', 'cubic metre', 'Volume', 1, 'm³', 1], ['h', 'hour', 'Time', 1, 'h', 1], ['t·yr⁻¹', 'tonnes per year', 'Mass flow', 0, 't·yr⁻¹', 1], ['g·s⁻¹', 'grams per second', 'Mass flow', 0, 't·yr⁻¹', 31.536], ['GJ', 'gigajoule', 'Energy', 1, 'GJ', 1], ['MWh', 'megawatt-hour', 'Energy', 0, 'GJ', 3.6], ['°C', 'degree Celsius', 'Temperature', 1, '°C', 1], ['%', 'percent', 'Ratio', 1, '%', 1], ['mg·m⁻³', 'milligrams per cubic metre', 'Concentration', 1, 'mg·m⁻³', 1], ['t CO₂e', 'tonnes of CO₂ equivalent', 'GHG', 1, 't CO₂e', 1]
  ].map(function (a, i) { return { id: 'UN-' + ('00' + (i + 1)).slice(-3), symbol: a[0], name: a[1], dimension: a[2], base: !!a[3], baseUnit: a[4], factor: a[5], usedIn: Math.round(rng(40 + i)() * 120) }; });

  /* methodologies, expressions, templates */
  D.methodologies = [['M-01', 'Stationary combustion — emission factors', 'Order № 221-Ө', 'Published', '2.1.0', 3, 'Air emissions'], ['M-02', 'Flaring — mass balance', 'Order № 221-Ө, annex 4', 'Published', '1.4.0', 4, 'Air emissions'], ['M-03', 'Storage tanks — VOC losses', 'Order № 100-п', 'Published', '1.0.2', 2, 'Air emissions'], ['M-04', 'GHG — IPCC 2006 tier 2', 'IPCC 2006, vol. 2', 'Draft', '3.0.0', 5, 'GHG inventory'], ['M-05', 'Wastewater — pollutant load', 'Order № 63', 'Published', '1.2.0', 2, 'Water use & discharge'], ['M-06', 'Waste generation norms', 'Order № 206', 'Archived', '0.9.0', 1, 'Waste']
  ].map(function (a) { return { id: a[0], name: a[1], basis: a[2], state: a[3], version: a[4], versions: a[5], sheet: a[6], updated: '2026-0' + (3 + (+a[0].slice(3))) + '-14', owner: 'G. Tulegenova', constants: 6 + (+a[0].slice(3)) * 2, formulas: 4 + (+a[0].slice(3)) * 3 }; });
  D.methodologyVersions = function (id) { var m = D.methodologies.filter(function (x) { return x.id === id; })[0] || D.methodologies[0], out = [], major = +m.version.split('.')[0]; for (var i = 0; i < m.versions; i++) out.push({ id: m.id + '-v' + (m.versions - i), version: i === 0 ? m.version : Math.max(0, major - (i > 1 ? 1 : 0)) + '.' + (m.versions - i) + '.0', state: i === 0 ? m.state : i === 1 && m.state === 'Draft' ? 'Published' : 'Archived', created: '2026-0' + Math.max(1, 8 - i * 2) + '-14', author: 'G. Tulegenova', changes: [3, 7, 2, 11, 5][i % 5], note: ['Updated NOₓ factors for gas turbines', 'New constants for associated gas', 'Fixed rounding of intermediate totals', 'Initial version', 'Aligned with order revision'][i % 5] }); return out; };
  D.expressions = [['EF_NOX_TURBINE', 'Formula', 'NOₓ from gas turbines', 'FuelGas * EF("NOx","GT") * (1 - Abatement/100)', 'M-01', 'Valid', 14], ['SUM_POLLUTANTS', 'Formula', 'Total emissions of a source', 'SUM(C3:C10)', 'M-01', 'Valid', 91], ['R_NONNEG', 'Rule', 'Value must be ≥ 0', 'Value >= 0', '—', 'Valid', 1140], ['R_HOURS_PERIOD', 'Rule', 'Hours within the period length', 'Hours <= PeriodHours()', '—', 'Valid', 182], ['R_PERMIT_LIMIT', 'Rule', 'Total within the permit limit', 'Total <= Registry("PermitLimits", Source, Substance)', '—', 'Valid', 91], ['FLARE_MASS_BAL', 'Formula', 'Flared gas mass balance', 'Volume * Density * (1 - Efficiency/100)', 'M-02', 'Valid', 7], ['GHG_CO2E', 'Formula', 'CO₂ equivalent', 'CO2 + CH4 * GWP("CH4") + N2O * GWP("N2O")', 'M-04', 'Warning', 58], ['S_IMPORT_PI', 'Script', 'Map PI tags to cells', 'for tag in Source("PI").tags: …', '—', 'Error', 3], ['TANK_VOC_LOSS', 'Formula', 'Standing and working losses', 'Ls(Tank, Month) + Lw(Tank, Throughput)', 'M-03', 'Valid', 21], ['R_FUEL_REQUIRED', 'Rule', 'Fuel is required when hours > 0', 'Hours = 0 OR FuelGas > 0', '—', 'Valid', 91]
  ].map(function (a) { return { id: a[0], kind: a[1], name: a[2], text: a[3], methodology: a[4], check: a[5], usedIn: a[6], updated: '2026-08-2' + (a[6] % 9), author: 'G. Tulegenova' }; });
  D.templates = [['GEN07131100', 'General environmental report — CPF', 'Published', '1.0.0', 3, 5, 222, 12], ['GEN07131200', 'General environmental report — gas treatment', 'Published', '1.0.0', 2, 5, 198, 1], ['GEN-UPSTREAM', 'General environmental report — upstream', 'Published', '2.3.0', 6, 4, 164, 5], ['WTR-QUARTERLY', 'Quarterly water report', 'Draft', '0.4.0', 1, 1, 24, 0], ['WST-ANNUAL', 'Annual waste passport', 'Published', '1.1.0', 2, 2, 41, 3], ['GHG-ANNUAL-OLD', 'Annual GHG report (2024 form)', 'Archived', '3.2.1', 9, 1, 58, 0]
  ].map(function (a) { return { id: a[0], name: a[1], state: a[2], version: a[3], versions: a[4], sheets: a[5], tables: a[6], documents: a[7], updated: '2026-0' + (1 + a[4] % 8) + '-1' + (a[5] % 9), owner: 'G. Tulegenova' }; });
  D.templateVersions = function (id) { var t = D.templates.filter(function (x) { return x.id === id; })[0] || D.templates[0], out = [], p = t.version.split('.'); for (var i = 0; i < t.versions; i++) out.push({ id: 'v' + (t.versions - i), template: t.id, version: i === 0 ? t.version : p[0] + '.' + Math.max(0, +p[1] - i) + '.' + (i % 3), state: i === 0 ? t.state : t.state === 'Draft' && i === 1 ? 'Published' : 'Archived', created: '2026-0' + Math.max(1, 9 - i) + '-0' + (1 + i % 8), author: 'G. Tulegenova', documents: i === 0 ? t.documents : Math.round(rng(i * 7 + 1)() * 8), note: ['Added table 13.7 “Emergency releases — permit comparison”', 'Renamed columns of sheet Waste', 'Fixed units in GHG tables', 'Access matrix: Approver can comment', 'Initial publication'][i % 5] }); return out; };

  /* sources & mapping */
  D.sources = [['SRC-01', 'PI Web API · CPF historian', 'PI Web API', 'Unhealthy', '2026-09-18T11:30', 1482, 'Timeout after 30 s'], ['SRC-02', 'LIMS · laboratory results', 'SQL view', 'Healthy', '2026-09-18T06:00', 212, null], ['SRC-03', 'SAP PM · run hours', 'OData', 'Degraded', '2026-09-18T05:30', 96, '3 of 96 tags returned no value'], ['SRC-04', 'Manual upload · stack tests', 'Excel', 'Healthy', '2026-09-02T15:10', 40, null]
  ].map(function (a) { return { id: a[0], name: a[1], kind: a[2], health: a[3], lastRun: a[4], tags: a[5], problem: a[6], enabled: a[0] !== 'SRC-04', schedule: a[0] === 'SRC-04' ? 'Manual' : 'Daily 05:30' }; });
  D.mappings = D.emissionSources.slice(0, 14).map(function (n, i) { var R = rng(700 + i), ok = R() > 0.2; return { id: 'MAP-' + ('00' + (i + 1)).slice(-3), tag: 'CPF.' + n.replace(/[^A-Z0-9]+/gi, '').toUpperCase().slice(0, 10) + '.FLOW', source: 'SRC-01', sheet: 'Air emissions', table: '1.2', row: n, column: 'Fuel gas', unit: '10³ m³', value: ok ? +(R() * 5000).toFixed(4) : null, status: ok ? 'Info' : i % 2 ? 'Error' : 'Warning', message: ok ? 'Mapped' : i % 2 ? 'Tag not found in the source' : 'Unit mismatch: m³ → 10³ m³ (converted)' }; });

  /* jobs, snapshots, audit, consistency, ui strings, health */
  D.jobs = [['J-10428', 'Excel export', 'DOC-000001', 'Running', 62, '14:06', 48, 'D. Akhmetova'], ['J-10427', 'Formula recalculation', 'DOC-000001 · Air emissions', 'Failed', 41, '14:01', 12, 'D. Akhmetova', 'Division by zero in expression EF_NOX_TURBINE — table 1.3, row “Gas turbine GT-4”: run hours are 0.', 'ECR-CALC-0500', '7c1d2a90-5b7e-4f0c-9a61-2e8f3b6d4410'], ['J-10425', 'Excel import', 'DOC-000001', 'Done', 100, '13:52', 7, 'D. Akhmetova', null, null, null, '12 changes applied · 2 skipped (locked cells)'], ['J-10424', 'Period archive', 'August 2026', 'Queued', 0, null, null, 'System'],
    ['J-10421', 'Report snapshot', 'Annual air emissions report', 'Done', 100, '12:40', 92, 'A. Iskakov'], ['J-10418', 'Formula recalculation', 'DOC-000007', 'Done', 100, '11:58', 21, 'M. Petrenko'], ['J-10412', 'Source collection', 'PI Web API · CPF historian', 'Failed', 8, '11:30', 30, 'System', 'The data source did not respond within 30 s. No values were written.', 'ECR-SRC-0408', 'a94f0c11-0d2b-4e57-b1f3-6c2a9d7e5f02'], ['J-10409', 'Excel export', 'DOC-000003', 'Done', 100, '10:14', 39, 'S. Nurlanov'], ['J-10402', 'Consistency check', 'September 2026', 'Done', 100, '09:00', 125, 'System', null, null, null, 'Finished with 4 warnings'], ['J-10398', 'Formula recalculation', 'DOC-000004', 'Cancelled', 30, '08:47', 4, 'D. Akhmetova']
  ].map(function (a) { return { id: a[0], type: a[1], target: a[2], state: a[3], progress: a[4], started: a[5], duration: a[6], by: a[7], error: a[8] || null, code: a[9] || null, correlationId: a[10] || null, result: a[11] || null, attempts: a[3] === 'Failed' ? 2 : 1 }; });
  D.snapshots = [['SN-0031', 'Annual air emissions report', '2025', 'A. Iskakov', '2026-09-18T12:40', 4812345, 'DOC-000001 … DOC-000012'], ['SN-0030', 'Monthly summary — August 2026', '2026-08', 'A. Iskakov', '2026-09-05T18:30', 1210344, '12 documents'], ['SN-0029', 'Monthly summary — July 2026', '2026-07', 'A. Iskakov', '2026-08-05T17:02', 1198770, '12 documents'], ['SN-0028', 'Quarterly water report — Q2', '2026-Q2', 'O. Kovalenko', '2026-07-12T10:15', 640112, '4 documents'], ['SN-0027', 'Monthly summary — June 2026', '2026-06', 'A. Iskakov', '2026-07-04T16:48', 1187003, '12 documents']
  ].map(function (a) { return { id: a[0], name: a[1], period: a[2], by: a[3], created: a[4], size: a[5], scope: a[6], hash: 'sha256:' + Math.round(rng(a[5])() * 1e16).toString(16) }; });
  var ACT = [['Cell.Edit', 'Edited a value', 'Info'], ['Sheet.Submit', 'Submitted a sheet', 'Info'], ['Sheet.Approve', 'Approved a sheet', 'Info'], ['Sheet.Reject', 'Rejected a sheet', 'Warning'], ['Import.Apply', 'Applied an Excel import', 'Info'], ['Role.Grant', 'Granted a role', 'Warning'], ['Period.Close', 'Closed a period', 'Warning'], ['Template.Publish', 'Published a template version', 'Warning'], ['Login.Failed', 'Failed sign-in', 'Error'], ['Sheet.Reopen', 'Returned an approved sheet for edits', 'Critical']];
  D.audit = (function () { var R = rng(2026), out = [], t = new Date(D.today).getTime(); for (var i = 0; i < 64; i++) { var a = ACT[Math.floor(R() * ACT.length)], u = D.users[Math.floor(R() * 8)]; t -= Math.round(R() * 5400000);
    out.push({ id: 'AU-' + (90412 - i), at: new Date(t).toISOString().slice(0, 16), user: u.name, action: a[0], label: a[1], severity: a[2], object: /Role|Login/.test(a[0]) ? u.login : /Period/.test(a[0]) ? 'August 2026' : /Template/.test(a[0]) ? 'GEN07131100 v1.0.0' : 'DOC-00000' + (1 + Math.floor(R() * 9)) + (a[0] === 'Cell.Edit' ? ' · 1.' + (1 + Math.floor(R() * 7)) + ' · R' + (1 + Math.floor(R() * 40)) + ' C' + (1 + Math.floor(R() * 10)) : ' · ' + D.sheetNames[Math.floor(R() * 5)]), before: a[0] === 'Cell.Edit' ? +(R() * 900).toFixed(4) : null, after: a[0] === 'Cell.Edit' ? +(R() * 900).toFixed(4) : null, origin: ['UserEdit', 'Import', 'Recalculation'][Math.floor(R() * 3)], ip: '10.12.' + Math.floor(R() * 40) + '.' + Math.floor(R() * 250), correlationId: Math.round(R() * 1e12).toString(16) + '-' + i }); } return out; })();
  D.consistency = [['CI-118', 'Error', 'GHG total does not match Air emissions CH₄', 'DOC-000001', 'GHG inventory 4.12 ↔ Air emissions 1.5', 'Open'], ['CI-117', 'Warning', 'Water intake is lower than discharge', 'DOC-000004', 'Water use & discharge 1.1 ↔ 2.1', 'Open'], ['CI-116', 'Warning', 'Fuel in Energy differs from Air emissions by 12 %', 'DOC-000006', 'Energy 1.2 ↔ Air emissions 1.2', 'Open'], ['CI-115', 'Error', 'Registry entry used in data was deactivated', 'DOC-000009', 'Emission sources · Gas turbine GT-4', 'Open'], ['CI-112', 'Info', 'Waste transfer has no receiving facility', 'DOC-000003', 'Waste 3.2', 'Resolved'], ['CI-109', 'Warning', 'Run hours exceed the period length', 'DOC-000001', 'Air emissions 1.3', 'Open'], ['CI-101', 'Critical', 'Approved sheet changed by recalculation', 'DOC-000002', 'Air emissions 2.1', 'Open']
  ].map(function (a) { return { id: a[0], severity: a[1], title: a[2], document: a[3], where: a[4], status: a[5], found: '2026-09-18T09:00', check: 'J-10402' }; });
  D.uiStrings = [['documents.title', 'Documents', 'Документы', 'Құжаттар'], ['documents.new', 'New document', 'Новый документ', 'Жаңа құжат'], ['sheet.submit', 'Submit', 'Отправить', 'Жіберу'], ['sheet.approve', 'Approve', 'Утвердить', 'Бекіту'], ['sheet.reject', 'Reject', 'Отклонить', 'Қабылдамау'], ['grid.source', 'Emission source', 'Источник выбросов', 'Шығарындылар көзі'], ['validation.nonneg', 'Value must be ≥ 0', 'Значение должно быть ≥ 0', ''], ['period.closed', 'The period is closed', 'Период закрыт', 'Кезең жабық'], ['import.preview', 'Review the import', 'Проверьте импорт', ''], ['error.retry', 'Retry', 'Повторить', 'Қайталау'], ['login.windows', 'Sign in with Windows', 'Войти через Windows', 'Windows арқылы кіру'], ['state.empty.docs', 'No documents in this period yet', 'В этом периоде пока нет документов', '']
  ].map(function (a) { return { key: a[0], en: a[1], ru: a[2], kk: a[3], missing: !a[3] ? 1 : 0, area: a[0].split('.')[0] }; });
  D.health = { overall: 'Degraded', checked: '2026-09-18T14:03:12', checks: [{ id: 'db', name: 'Database', state: 'Healthy', value: '12 ms', note: 'Database is available.' }, { id: 'jobs', name: 'Background jobs', state: 'Healthy', value: '1 running · 2 failed today', note: 'The scheduler is running.' }, { id: 'sources', name: 'Data sources', state: 'Degraded', value: '1 of 4 unreachable', note: 'PI Web API · CPF historian timed out at 11:30.' }, { id: 'transport', name: 'Transport', state: 'Degraded', value: 'HTTP', note: 'Served without TLS — the session cookie is not Secure.' }],
    database: [['SQL Server edition', 'Enterprise Developer Edition (64-bit)'], ['Effective mode', 'Enterprise'], ['Major version', '17'], ['Read Committed Snapshot Isolation', 'Yes'], ['Filegroups', 'PRIMARY · DATA_HOT · DATA_ARCHIVE · AUDIT · INDEXES'], ['Partitions ahead', '15 months'], ['Data disk free', '182.4 GB of 293 GB']],
    application: [['Product version', '1.8.2 · build 2026.09.17.4 · a1b9c3e'], ['Authentication', 'Windows (Negotiate) · local accounts enabled'], ['Interface languages', 'English, Русский, Қазақша'], ['Uptime', '6 d 04 h 12 min']] };
  E.data = D;
})(window.ECR);

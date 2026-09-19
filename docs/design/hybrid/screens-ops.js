/* =====================================================================
   screens-ops.js — доступ і експлуатація (Access + Operate).
   Маршрути: /admin/security (?tab=roles|grants|users · ?view=compare · ?diff=1 · ?role=<id>) · /admin/periods (?project=<id>) ·
             /admin/jobs · /admin/snapshots · /admin/audit (?tab=data|structure) · /admin/consistency · /admin/ui-strings (?lang=ru|kk · ?scope=Public|Private) ·
             /admin/health (?case=partitions)
   ?dialog=  security: new-role · rename-role · delete-role · save-role-dangerous · new-grant · revoke-grant · grant-role · new-user ·
                       reset-password · result-password · lock-user · simulate-user · result-role-created · result-granted · result-role-assigned
             periods:  open-period · close-period · reopen-period · archive-period · archive-period-confirm · edit-policy ·
                       result-opened · result-closed · result-reopened · result-archived
             jobs:     retry-job · cancel-job · purge-jobs · result-retried
             snapshots: new-snapshot · result-snapshot        audit: date-range · export-audit
             consistency: ack-issue · run-check · result-check-started · result-acknowledged
             ui-strings: edit-string · import-strings · export-strings · result-imported
             health: create-partitions · result-partitions
   ?panel=   security: u-01…u-12 (користувач) · gr-01… (грант); periods: 2026-01…2026-12; jobs: J-…; snapshots: SN-…;
             audit: CH-… (комірка) · AU-… (структурна зміна); consistency: CI-…; ui-strings: <ключ рядка>
   Наскрізне: ?sim=<user id> — банер симуляції на всю оболонку (живе до «Exit simulation»).
              ?step=N — відкрити Wizard одразу на кроці N: new-role (0–3) · open-period (0–2) · new-snapshot (0–2) · import-strings (0–2).
              ?roles=A,B,C — які ролі порівнювати в матриці (?view=compare).
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, D = E.data, F = E.fmt, icon = E.icon, NOW = D.today;

  E.css('ops', [
    '.ops-slot{flex:none}.ops-slot:empty{display:none}',
    '.ops-headhost{flex:none}',
    '.ops-fill{flex:1;min-height:0}',
    '.ops-sp{flex:1}',
    '.ops-drawer{min-height:0}',
    '.ops-segf>.seg{align-self:flex-start;max-width:100%;flex-wrap:wrap}',
    '.ops-matrix thead a{color:inherit;text-decoration:none}.ops-matrix thead a:hover{color:var(--text);text-decoration:underline}',
    '.ops-arrow{display:inline-flex;align-items:center;gap:var(--s2);font-family:var(--mono);min-width:0}',
    '.ops-arrow svg{color:var(--faint)}',
    '.ops-late{display:inline-flex;align-items:center;gap:var(--s1);color:var(--warning);font-weight:500}',
    '.ops-tag{display:inline-flex;align-items:center;gap:var(--s1);color:var(--muted);font-size:var(--fs-xs);white-space:nowrap}',
    '.ops-danger{display:inline-flex;align-items:center;gap:var(--s1);color:var(--warning);font-size:var(--fs-xs);font-weight:500;white-space:nowrap}',
    /* D6: значення з кнопкою копіювання — один рядок без перенесення кнопки: значення переноситься саме, кнопка праворуч угорі.
       Від'ємний відступ не дає кнопці 28px роздувати рядок тексту й центрує її по першому рядку. */
    '.ops-copy{display:inline-flex;align-items:flex-start;gap:var(--s1);min-width:0;max-width:100%}',
    '.ops-copy>.mono{min-width:0;overflow-wrap:anywhere}',
    '.ops-copy>.icon-btn{flex:none;margin:calc(var(--s1) * -1) 0}',
    '.ops-text{display:grid;gap:var(--s2)}.ops-text p{color:var(--text)}',
    '.ops-rows{display:grid;gap:0;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.ops-rowi{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:var(--s1) var(--s3);align-items:center;padding:var(--s2) var(--s3);border-bottom:1px solid var(--grid-line)}',
    '.ops-rowi:last-child{border-bottom:0}',
    '.ops-rowi .sub{grid-column:1/-1;color:var(--muted);font-size:var(--fs-xs)}',
    '.ops-rowi b{font-weight:500}',
    '.ops-chain{display:flex;flex-wrap:wrap;align-items:center;gap:var(--s1);color:var(--muted);font-size:var(--fs-xs);grid-column:1/-1}',
    '.ops-chain svg{color:var(--faint)}',
    '.ops-ladder{display:inline-flex;align-items:center;gap:var(--s2);white-space:nowrap}',
    '.ops-ladder .ld{display:inline-flex;gap:2px}',
    '.ops-ladder .ld i{display:block;width:10px;height:4px;border-radius:2px;background:var(--border-strong)}',
    '.ops-ladder .ld i.on{background:var(--muted)}',
    /* timeline */
    '.ops-tl{display:grid;gap:0;margin:0;padding:0;list-style:none}',
    '.ops-tl li{display:grid;grid-template-columns:16px minmax(0,1fr);gap:var(--s2);padding-bottom:var(--s3);position:relative}',
    '.ops-tl li:before{content:"";position:absolute;left:var(--s1);top:16px;bottom:0;width:1px;background:var(--border)}',
    '.ops-tl li:last-child:before{display:none}.ops-tl li:last-child{padding-bottom:0}',
    '.ops-tl .dot{margin-top:var(--s1);background-color:var(--raised)}',
    '.ops-tl .dot.filled{background:var(--muted)}',
    '.ops-tl li.cur .dot{border-color:var(--accent);background:var(--accent)}',
    '.ops-tl .tl-h{display:flex;gap:var(--s2);flex-wrap:wrap;align-items:baseline}',
    '.ops-tl .tl-h b{font-weight:500}',
    '.ops-tl .tl-m{color:var(--muted);font-size:var(--fs-xs)}',
    '.ops-tl li.cur .tl-b{background:var(--accent-soft);border-radius:var(--r1);padding:var(--s1) var(--s2);margin:0 0 0 calc(0px - var(--s2))}',
    /* security */
    '.ops-viewbar{flex:none}',
    '.ops-rolesbody{flex:1;min-height:0;display:flex;flex-direction:column}',
    '.ops-roles{flex:1;min-height:0;display:grid;grid-template-columns:320px minmax(0,1fr);gap:var(--s4)}',
    '.ops-roles-list{min-height:0;overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface);align-self:start;max-height:100%}',
    '.ops-role-row{display:flex;align-items:flex-start;border-bottom:1px solid var(--grid-line)}',
    '.ops-role-row:last-child{border-bottom:0}',
    '.ops-role-row[aria-current="true"]{background:var(--accent-soft);box-shadow:inset 2px 0 0 var(--accent)}',
    '.ops-role{flex:1;min-width:0;display:grid;gap:2px;padding:var(--s2) var(--s3);border:0;background:transparent;text-align:left;white-space:normal}',
    '.ops-role:hover{background:var(--hover)}',
    '.ops-role .nm{font-weight:500}',
    '.ops-role .ds{color:var(--muted);font-size:var(--fs-xs)}',
    '.ops-shield{display:inline-flex;align-items:center;gap:var(--s1);margin:var(--s2) var(--s2) 0 0;height:24px;padding:0 var(--s2);border:1px solid transparent;border-radius:var(--r1);background:transparent;color:var(--warning);font:500 var(--fs-xs)/1 var(--mono)}',
    '.ops-shield:hover{background:var(--hover);border-color:var(--border)}',
    '.ops-role-detail{min-height:0;overflow:auto;display:flex;flex-direction:column;gap:var(--s3);padding-right:var(--s1)}',
    '.ops-role-head{display:flex;align-items:flex-start;gap:var(--s3);flex-wrap:wrap}',
    '.ops-role-head h2{font-size:var(--fs-h3);font-weight:600}',
    '.ops-role-head .grow{min-width:240px}',
    '.ops-perm{display:grid;grid-template-columns:16px minmax(0,1fr) auto;gap:var(--s2);align-items:start;padding:var(--s1) 0;border-bottom:1px solid var(--grid-line)}',
    '.ops-perm:last-child{border-bottom:0}',
    '.ops-perm.edit{grid-template-columns:minmax(0,1fr) auto}',
    '.ops-perm>svg{margin-top:2px;color:var(--muted)}',
    '.ops-perm.off>svg{color:var(--faint)}.ops-perm.off .two-line>span{color:var(--muted)}',
    '.ops-perm .two-line span,.ops-perm .two-line small{white-space:normal}',
    '.ops-matrix-wrap{flex:1;min-height:0;overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.ops-matrix{border-collapse:separate;border-spacing:0;width:100%}',
    '.ops-matrix th,.ops-matrix td{height:var(--row);padding:0 var(--s2);border-bottom:1px solid var(--grid-line);border-right:1px solid var(--grid-line);text-align:center}',
    '.ops-matrix thead th{position:sticky;top:0;z-index:3;background:var(--sunken);font-size:var(--fs-xs);font-weight:500;color:var(--muted);vertical-align:bottom;padding:var(--s2);width:104px;min-width:104px;white-space:normal;line-height:1.3;border-bottom:1px solid var(--border-strong)}',
    '.ops-matrix th.pm{position:sticky;left:0;z-index:2;background:var(--surface);text-align:left;min-width:300px;width:300px;font-weight:400;white-space:normal;padding:var(--s1) var(--s3);border-right:1px solid var(--border-strong)}',
    '.ops-matrix thead th.pm{z-index:4;background:var(--sunken);font-weight:500}',
    '.ops-matrix tr.dom th{position:sticky;left:0;background:var(--sunken);text-align:left;font-size:var(--fs-xs);text-transform:uppercase;letter-spacing:.04em;color:var(--muted);font-weight:500;padding:0 var(--s3)}',
    '.ops-matrix td svg{vertical-align:middle}',
    '.ops-matrix td.y{color:var(--text)}.ops-matrix td.n{color:var(--faint)}.ops-matrix td.d{color:var(--warning)}',
    '.ops-matrix tbody tr:hover td,.ops-matrix tbody tr:hover th.pm{background:var(--hover)}',
    '.ops-picks{display:grid;gap:0;max-height:180px;overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.ops-picks button{display:flex;align-items:center;gap:var(--s2);min-height:var(--ctl);padding:var(--s1) var(--s3);border:0;border-bottom:1px solid var(--grid-line);background:transparent;text-align:left}',
    '.ops-picks button:hover{background:var(--hover)}',
    '.ops-picks button[aria-pressed="true"]{background:var(--accent-soft)}',
    '.ops-picks button small{color:var(--muted);font-size:var(--fs-xs);margin-left:auto}',
    '.ops-permlist{display:grid;gap:0;max-height:320px;overflow:auto;border:1px solid var(--border);border-radius:var(--r2);padding:0 var(--s3);background:var(--surface)}',
    '.ops-permlist .eyebrow{padding:var(--s2) 0 0}',
    '.ops-otp{display:flex;align-items:center;gap:var(--s2);padding:var(--s3);border:1px dashed var(--border-strong);border-radius:var(--r2);background:var(--sunken);font:500 var(--fs-h3)/1.2 var(--mono);letter-spacing:.04em;flex-wrap:wrap}',
    /* periods */
    '.ops-yearbar{display:flex;align-items:flex-end;gap:var(--s3);flex-wrap:wrap;flex:none}',
    '.ops-yearbar .field{width:320px;max-width:100%}',
    '.ops-year{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:var(--s1);flex:none}',
    '.ops-month{display:grid;gap:var(--s1);padding:var(--s2);border:1px solid var(--border);border-radius:var(--r2);background:var(--surface);text-align:left;min-width:0;align-content:start;min-height:96px}',
    '.ops-month:hover{background:var(--hover)}',
    '.ops-month .m{font-weight:600}',
    '.ops-month .n{font-size:var(--fs-xs);color:var(--muted);white-space:normal;line-height:1.3}',
    '.ops-month .badge{padding-left:0;justify-self:start}',
    '.ops-month.archived,.ops-month.notopened{background:transparent}',
    '.ops-month.notopened{border-style:dashed}',
    '.ops-month.grace{border-color:var(--warning)}',
    '.ops-month.now{border-color:var(--border-strong);box-shadow:inset 0 2px 0 var(--muted)}',
    '.ops-month[aria-pressed="true"]{border-color:var(--accent);box-shadow:inset 0 0 0 1px var(--accent)}',
    /* ui strings, health */
    '.ops-cov{display:flex;gap:var(--s5);flex-wrap:wrap;align-items:center;flex:none}',
    '.ops-cov-i{display:flex;align-items:center;gap:var(--s2);min-width:220px}',
    '.ops-cov-i b{font-weight:500;width:24px}',
    '.ops-ctl{display:flex;align-items:center;gap:var(--s3);flex-wrap:wrap;flex:none}',
    '.ops-ctl>.select{width:auto;max-width:100%}',
    '.ops-orig{padding:var(--s2) var(--s3);border:1px solid var(--border);border-radius:var(--r2);background:var(--sunken);white-space:pre-wrap;overflow-wrap:anywhere}',
    '.ops-meter{display:flex;align-items:flex-start;gap:var(--s2);font-size:var(--fs-xs);color:var(--muted)}',
    '.ops-meter>span{flex:1;min-width:0}',
    '.ops-meter.warn{color:var(--warning)}.ops-meter.bad{color:var(--danger)}',
    '.ops-drop{display:grid;justify-items:center;gap:var(--s2);padding:var(--s5);border:1px dashed var(--border-strong);border-radius:var(--r2);background:var(--sunken);text-align:center}',
    '.ops-drop svg{color:var(--muted)}',
    '.ops-health{display:grid;gap:var(--s4);max-width:1080px}',
    '.ops-hsec{padding:var(--s3) var(--s4) var(--s4);display:grid;gap:var(--s3)}',
    '.ops-hsec-h{display:flex;align-items:center;gap:var(--s3);flex-wrap:wrap}',
    '.ops-hsec-h h2{font-size:var(--fs-md);font-weight:600}',
    '.ops-hsec-h .val{margin-left:auto;color:var(--muted);font-family:var(--mono);font-size:var(--fs-xs)}',
    '.ops-hsec .concl{font-size:var(--fs-md)}',
    '@media (max-width:1180px){.ops-year{grid-template-columns:repeat(6,minmax(0,1fr))}}',
    '@media (max-width:900px){.ops-roles{grid-template-columns:1fr;overflow:auto;display:block}.ops-roles-list{max-height:220px;margin-bottom:var(--s3)}.ops-role-detail{overflow:visible}.ops-matrix th.pm{min-width:180px;width:180px}}',
    '@media (max-width:640px){.ops-year{grid-template-columns:repeat(3,minmax(0,1fr))}.ops-yearbar .field{width:100%}.ops-cov-i{min-width:100%}.ops-hsec-h .val{margin-left:0;flex-basis:100%}}'
  ].join('\n'));

  /* ================= спільні помічники ================= */
  function later(fn) { setTimeout(fn, 0); }
  /* ?step=N — відкрити Wizard одразу на кроці N (0 — перший); max = кількість кроків до Review, щоб не натиснути Apply */
  function wzJump(ctx, max, api) { var n = Math.max(0, Math.min(max, +ctx.query.step || 0)); for (var i = 0; i < n; i++) api.next(); return api; }
  /* бейдж у шапці шторки: .dh-t — grid, і голий бейдж розтягується на всю ширину; обгортка це знімає */
  function dbadge(kind, state, o) { return h('span', E.StatusBadge(kind, state, o)); }
  function cap(s) { return s.charAt(0).toUpperCase() + s.slice(1); }
  function copy(text, what) { try { navigator.clipboard.writeText(text); } catch (e) { } E.Toast(cap(what) + ' copied'); }
  function copyBtn(text, what) { return E.Button({ iconOnly: true, icon: 'copy', label: 'Copy ' + what, onClick: function () { copy(text, what); } }); }
  function withCopy(text, what) { return h('span', { class: 'ops-copy' }, h('span', { class: 'mono' }, text), copyBtn(text, what)); }
  function arrow(a, b) { return h('span', { class: 'ops-arrow' }, h('span', { class: a === '—' ? 'faint' : 'was' }, a), icon('arrowR', 12), h('span', b)); }
  function tag(ic, text, title) { return h('span', { class: 'ops-tag', title: title || null }, icon(ic, 12), text); }
  function lateMark(text) { return h('span', { class: 'ops-late', title: 'Made after the data entry deadline' }, icon('clock', 12), text || 'Late'); }
  function addDays(iso, n) { var d = new Date(iso.slice(0, 10) + 'T12:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); }
  function daysTo(iso) { return Math.floor((new Date(iso.slice(0, 10) + 'T23:59:00') - new Date(NOW)) / 864e5); }
  function inDays(n) { return n < 0 ? F.plural(-n, { one: 'day', other: 'days' }) + ' ago' : n === 0 ? 'today' : 'in ' + F.plural(n, { one: 'day', other: 'days' }); }
  function timeline(items) { return h('ol', { class: 'ops-tl' }, items.map(function (it) { return h('li', { class: it.cls || null }, h('span', { class: 'dot ' + (it.dot || '') }), h('div', { class: 'tl-b' }, h('div', { class: 'tl-h' }, h('b', it.title), it.right || null), it.body || null, it.meta ? h('div', { class: 'tl-m' }, it.meta) : null)); })); }
  function rowsBox(items) { return h('div', { class: 'ops-rows' }, items); }
  function rowItem(main, right, sub, extra) { return h('div', { class: 'ops-rowi' }, h('div', { class: 'row wrap' }, main), right || h('span'), sub ? h('div', { class: 'sub' }, sub) : null, extra || null); }
  function para() { return h('div', { class: 'ops-text' }, Array.prototype.slice.call(arguments).map(function (t) { return h('p', t); })); }

  /* підсумок дії «що далі»: живе одне перемальовування, зникає при виході з екрана */
  var FLASH = {};
  function slot(page, ctx, key) {
    var s = h('div', { class: 'ops-slot', id: key + '-result' }); page.appendChild(s);
    function show(o) { s.textContent = ''; if (!o) return; s.appendChild(E.ResultBanner(Object.assign({ id: key + '-result-banner' }, o, { onDismiss: function () { delete FLASH[key]; if (/^result-/.test(ctx.query.dialog || '')) ctx.setQuery({ dialog: null }); } }))); }
    if (FLASH[key]) show(FLASH[key].make(ctx));
    ctx.onLeave(function () { var x = FLASH[key]; if (x) { if (x.keep) x.keep = false; else delete FLASH[key]; } });
    return { show: show, flash: function (make) { FLASH[key] = { make: make, keep: true }; ctx.refresh(); } };
  }

  /* перелік усередині вкладки: StatStrip → FilterBar (+перемикачі) → DataTable; шторку відкриває екран через ctx.openPanel */
  function miniList(host, ctx, o) {
    var mem = ctx.saved[o.id] = ctx.saved[o.id] || {}, data = ctx.state === 'data', rows = o.rows || [], stats = o.stats || [], filters = o.filters || [], strip = null, flags = mem.flags = mem.flags || {}, boxes = {}, bar, table;
    function keyOf(r) { var k = (o.table && o.table.rowKey) || 'id'; return typeof k === 'function' ? k(r) : r[k]; }
    function statItems() { return stats.map(function (s) { return { id: s.id, label: s.label, tone: s.tone, hint: s.hint, filter: s.match ? undefined : false, value: typeof s.value === 'function' ? s.value(rows) : s.value != null ? s.value : rows.filter(s.match).length }; }); }
    if (stats.length && data) { strip = E.StatStrip({ id: o.id + '-stat', items: statItems(), active: mem.stat || null, onSelect: function (s) { mem.stat = s; apply(); } }); host.appendChild(strip); }
    var right = o.toggles && o.toggles.length ? h('div', { class: 'row' }, o.toggles.map(function (t) { var c = E.Checkbox({ id: o.id + '-' + t.id, label: t.label, checked: !!flags[t.id], onChange: function (v) { flags[t.id] = v; apply(); } }); boxes[t.id] = c; return c; })) : null;
    bar = E.FilterBar({ id: o.id + '-flt', memory: mem, search: o.search === false ? false : { placeholder: (o.search && o.search.placeholder) || 'Search' }, filters: filters, right: right, onChange: function (v) { if (o.onFilter) o.onFilter(v, bar); apply(); } });
    if (!data) bar.hidden = true; host.appendChild(bar);
    function clearAll() { mem.stat = null; if (strip) strip.setActive(null); Object.keys(boxes).forEach(function (k) { flags[k] = false; boxes[k].input.checked = false; }); bar.reset(); }
    table = E.DataTable(Object.assign({ id: o.id + '-tbl', memory: mem, state: ctx.state, empty: o.empty, error: o.error, rowKey: 'id' }, o.table, { rows: [], onRowClick: o.onOpen ? function (r) { table.select(keyOf(r)); o.onOpen(r); } : null, onClearFilters: clearAll }));
    host.appendChild(table);
    function apply() {
      var v = bar.values(), q = (v.q || '').toLowerCase(), st = stats.filter(function (s) { return s.id === mem.stat; })[0];
      var list = rows.filter(function (r) {
        if (q && (o.search && o.search.text ? o.search.text(r) : JSON.stringify(r)).toLowerCase().indexOf(q) < 0) return false;
        if (st && st.match && !st.match(r)) return false;
        if ((o.toggles || []).some(function (t) { return flags[t.id] && !t.match(r); })) return false;
        return filters.every(function (f) { return !v[f.id] || (f.match ? f.match(r, v[f.id]) : String(r[f.id]) === v[f.id]); }); });
      table.setRows(list, { filtered: list.length !== rows.length });
    }
    if (data) apply();
    return { table: table, filters: bar, stats: strip, setRows: function (next) { rows = next; if (strip) strip.setItems(statItems()); apply(); }, refresh: function () { if (strip) strip.setItems(statItems()); apply(); }, clear: clearAll };
  }

  /* ================= симуляція користувача — банер на ВСЮ оболонку ================= */
  var SIM = { user: null };
  function simExit() { SIM.user = null; var b = E.$('#ops-sim-banner'); if (b) b.remove(); E.go('/admin/security', { params: { tab: 'users', as: 'SystemAdministrator' }, toast: 'Simulation ended — you are yourself again' }); }
  function simPaint() {
    var view = E.$('#views > .view'); if (!view) return;
    var old = E.$('#ops-sim-banner'); if (old) old.remove();
    var m = /[?&]sim=([^&]+)/.exec(location.hash); if (m) SIM.user = D.user(decodeURIComponent(m[1])) || SIM.user;
    if (!SIM.user) return;
    view.insertBefore(E.Banner({ id: 'ops-sim-banner', tone: 'warning', flush: true, icon: 'eye', title: 'You are viewing as ' + SIM.user.name + ' · read-only', text: 'You see exactly what ' + SIM.user.fullName + ' sees. Nothing you do here is saved. The simulation is recorded in the audit trail.', actions: [{ label: 'Exit simulation', icon: 'logout', id: 'ops-sim-exit', onClick: simExit }] }), view.firstChild);
  }
  /* Вада набору: .drawer — grid-елемент шару з автоматичним мінімумом висоти, тож довга шторка виштовхує свій футер за екран.
     Лікується min-height:0. Набір міняти заборонено — ставлю клас на шторки лише МОЇХ маршрутів. */
  var OPS_ROUTES = /^#\/admin\/(security|periods|jobs|snapshots|audit|consistency|ui-strings|health)\b/;
  (function drawerFix() { var host = document.getElementById('ecr-layers'); if (!host) { document.addEventListener('DOMContentLoaded', drawerFix); return; }
    new MutationObserver(function () { if (!OPS_ROUTES.test(location.hash)) return; E.$$('.drawer:not(.ops-drawer)', host).forEach(function (d) { d.classList.add('ops-drawer'); }); }).observe(host, { childList: true }); })();
  (function simInit() { var v = document.getElementById('views'); if (!v) { document.addEventListener('DOMContentLoaded', simInit); return; } new MutationObserver(simPaint).observe(v, { childList: true }); })();

  /* ================= SECURITY · модель ================= */
  var DOMAIN_LABEL = { Document: 'Documents', Calculation: 'Calculations', Template: 'Templates', Registry: 'Registries', Period: 'Periods', Security: 'Security', System: 'System' };
  var DANGER_WHY = { 'Document.Reopen': 'Approved numbers can change after approval.', 'Document.Delete': 'Documents and sheets are deleted together with the entered data.', 'Calculation.EditScript': 'Custom scripts run against document data.', 'Calculation.Publish': 'Changes how every document of a methodology is calculated.', 'Template.Publish': 'Changes the structure of all new documents.', 'Period.Reopen': 'A closed period becomes editable; submitted reports may be superseded.', 'Security.ManageRoles': 'Can give any permission to any role, including this one.', 'Security.ManageGrants': 'Can give any role to any user.', 'System.PurgeArchive': 'Archived data is deleted permanently.' };
  var ALLP = []; D.permissions.forEach(function (g) { g.items.forEach(function (p) { ALLP.push(p); }); });
  function perm(code) { return ALLP.filter(function (p) { return p.code === code; })[0]; }
  var LEVELS = ['None', 'Read', 'Write', 'Approve', 'Manage'];
  var LEVEL_TEXT = { None: 'No access — the resource is not visible.', Read: 'Can open and export, cannot change anything.', Write: 'Can enter, import and submit data.', Approve: 'Can approve, reject and return submitted sheets.', Manage: 'Can change settings of the resource and give access to others.' };
  var LEVEL_NEEDS = { Read: 'Document.View', Write: 'Document.Edit', Approve: 'Document.Approve', Manage: 'Security.ManageGrants' };
  var SEC = { roles: null, perms: {}, sel: 'Approver', open: { Document: true }, draft: null, onlyDiff: false, assign: {}, grants: [], seq: 0 };
  function roleById(id) { return SEC.roles.filter(function (r) { return r.id === id; })[0]; }
  function roleName(id) { var r = roleById(id); return r ? r.name : id; }
  function roleHas(id, code) { return !!(SEC.perms[id] || {})[code]; }
  function roleDanger(id) { return ALLP.filter(function (p) { return p.dangerous && roleHas(id, p.code); }); }
  function usersWithRole(id) { return D.users.filter(function (u) { return (SEC.assign[u.id] || []).some(function (a) { return a.role === id; }); }); }
  function groupsOf(u) { return D.groups.filter(function (g) { return g.members.indexOf(u.id) >= 0; }); }
  function resName(g) { if (g.res === '*') return 'All projects'; var src = g.resType === 'Project' ? D.projects : g.resType === 'Template' ? D.templates : D.registries; var x = src.filter(function (r) { return (r.id || r.code) === g.res; })[0]; return x ? x.name : g.res; }
  function subjName(g) { if (g.subjectType === 'group') { var gr = D.groups.filter(function (x) { return x.id === g.subject; })[0]; return gr ? gr.name : g.subject; } var u = D.user(g.subject); return u ? u.fullName : g.subject; }
  function subjSub(g) { if (g.subjectType === 'group') { var gr = D.groups.filter(function (x) { return x.id === g.subject; })[0]; return 'AD group · ' + F.plural(gr ? gr.members.length : 0, { one: 'member', other: 'members' }); } var u = D.user(g.subject); return u ? u.login : ''; }
  function ladder(level) { var i = LEVELS.indexOf(level); return h('span', { class: 'ops-ladder', title: LEVEL_TEXT[level] }, h('span', { class: 'ld', 'aria-hidden': 'true' }, [1, 2, 3, 4].map(function (k) { return h('i', { class: k <= i ? 'on' : null }); })), level); }
  function addGrant(st, s, rt, r, level, via, by, at) { SEC.seq++; var g = { id: 'gr-' + ('0' + SEC.seq).slice(-2), subjectType: st, subject: s, resType: rt, res: r, level: level, via: via || null, by: by || 'M. Tulegenov', at: at || '2026-01-12' }; SEC.grants.push(g); return g; }
  function secInit() {
    if (SEC.roles) return;
    SEC.roles = D.roles.map(function (r) { return Object.assign({}, r); });
    SEC.roles.push({ id: 'NightShiftEntry', name: 'Night shift data entry', description: 'Enters data, cannot import or submit', builtIn: false });
    var night = ['Document.View', 'Document.Edit', 'Document.Export', 'Period.View', 'Template.View', 'Registry.View'];
    SEC.roles.forEach(function (r) { SEC.perms[r.id] = {}; ALLP.forEach(function (p) { if (r.builtIn ? D.granted(r.id, p.code) : night.indexOf(p.code) >= 0) SEC.perms[r.id][p.code] = true; }); });
    D.users.forEach(function (u) { SEC.assign[u.id] = u.roles.map(function (r) { return { role: r, from: '2026-01-05', to: null, by: 'M. Tulegenov' }; }); });
    SEC.assign['u-04'].push({ role: 'Approver', from: '2026-09-15', to: '2026-09-30', by: 'M. Tulegenov', note: 'Covers A. Iskakov during leave' });
    SEC.assign['u-05'].push({ role: 'NightShiftEntry', from: '2026-09-01', to: '2026-09-25', by: 'M. Tulegenov', note: 'Night shift rota, September' });
    D.groups.forEach(function (g) { var lvl = /approver/i.test(g.name) ? 'Approve' : /auditor/i.test(g.name) ? 'Read' : 'Write'; g.projects.forEach(function (p) { addGrant('group', g.id, 'Project', p, lvl); }); });
    addGrant('user', 'u-06', 'Template', 'GEN07131100', 'Manage', null, 'M. Tulegenov', '2026-02-03');
    addGrant('user', 'u-06', 'Registry', 'EmissionSources', 'Manage', null, 'M. Tulegenov', '2026-02-03');
    addGrant('user', 'u-08', 'Project', '*', 'Manage', null, 'M. Tulegenov', '2026-01-12');
    addGrant('user', 'u-09', 'Project', 'P07133100', 'Read', null, 'M. Tulegenov', '2026-06-20');
    addGrant('group', 'g-01', 'Template', 'GEN07131100', 'Read', 'Project “Central processing facility” uses this template', 'System', '2026-01-12');
    addGrant('group', 'g-01', 'Registry', 'EmissionSources', 'Read', 'Template GEN07131100 reads this registry', 'System', '2026-01-12');
    addGrant('group', 'g-03', 'Project', 'P07131100', 'Approve', 'Grant on “All projects”', 'System', '2026-01-12');
  }
  function roleCap(u) { var best = 0; (SEC.assign[u.id] || []).forEach(function (a) { for (var i = 4; i >= 1; i--) if (roleHas(a.role, LEVEL_NEEDS[LEVELS[i]]) && i > best) best = i; }); return best; }
  function roleFor(u, level) { var a = (SEC.assign[u.id] || []).filter(function (x) { return roleHas(x.role, LEVEL_NEEDS[level]); })[0]; return a ? a.role : null; }
  function effective(u) {
    var out = [];
    if ((SEC.assign[u.id] || []).some(function (a) { return a.role === 'SystemAdministrator'; })) out.push({ res: 'Everything', resSub: 'all projects, templates and registries', level: 'Manage', chain: [['shield', 'Role System Administrator'], ['key', 'full access — no grant needed']] });
    SEC.grants.forEach(function (g) {
      if (g.via) return; var viaGroup = null;
      if (g.subjectType === 'group') { viaGroup = groupsOf(u).filter(function (x) { return x.id === g.subject; })[0]; if (!viaGroup) return; } else if (g.subject !== u.id) return;
      var capI = roleCap(u), want = LEVELS.indexOf(g.level), eff = LEVELS[Math.min(capI, want)], rl = roleFor(u, eff);
      out.push({ res: resName(g), resSub: g.resType + (g.res === '*' ? '' : ' · ' + g.res), level: eff, capped: capI < want ? g.level : null, grant: g,
        chain: [['shield', rl ? 'Role ' + roleName(rl) : 'No role allows this'], [viaGroup ? 'users' : 'user', viaGroup ? 'Grant to AD group “' + viaGroup.name + '”' : 'Direct grant'], ['layout', resName(g)]] });
    });
    return out;
  }

  /* ================= SECURITY · екран ================= */
  E.screen('/admin/security', { title: 'Security', group: 'Access', icon: 'shield', order: 1, render: function (el, ctx) {
    secInit();
    var page = h('div', { class: 'page page-fill' }); el.appendChild(page);
    var headHost = h('div', { class: 'ops-headhost' }); page.appendChild(headHost);
    var S = { ctx: ctx, res: slot(page, ctx, 'sec'), usersList: null, grantsList: null, userDrawer: null, grantDrawer: null, repaintRoles: null };
    if (ctx.query.role && roleById(ctx.query.role)) SEC.sel = ctx.query.role;
    if (!roleById(SEC.sel)) SEC.sel = 'Approver';
    if (ctx.query.diff === '1') SEC.onlyDiff = true;
    function paintHead(tab) {
      headHost.textContent = '';
      var o = { ctx: ctx, id: 'sec', title: 'Security', back: false, more: [{ label: 'Export roles and grants', icon: 'download', onClick: function () { E.Toast('Export started — security-2026-09-18.xlsx', 'success'); } }] };
      if (tab === 'grants') { o.subtitle = 'Who can reach which project, template or registry — and at what level. A grant works together with a role: the role says what a person may do, the grant says where.'; o.primary = { label: 'Grant access', icon: 'plus', id: 'sec-new-grant', onClick: function () { ctx.openDialog('new-grant'); } }; }
      else if (tab === 'users') { o.subtitle = 'People and service accounts. Open a user to see roles with their validity, AD groups and the rights that result from them.'; o.primary = { label: 'Assign role', icon: 'plus', id: 'sec-assign', onClick: function () { ctx.openDialog('grant-role'); } }; o.more.unshift({ label: 'New local user…', icon: 'user', onClick: function () { ctx.openDialog('new-user'); } }); }
      else { o.subtitle = 'A role is a set of permissions. Built-in roles cannot be changed; clone one to make your own. Dangerous permissions are marked with a shield.'; o.primary = { label: 'New role', icon: 'plus', id: 'sec-new-role', onClick: function () { ctx.openDialog('new-role'); } }; }
      headHost.appendChild(E.PageHeader(o));
    }
    var locked = D.users.filter(function (u) { return u.state === 'Locked'; }).length;
    var tabs = E.Tabs({ id: 'sec-tabs', ctx: ctx, fill: true, onChange: paintHead, tabs: [
      { id: 'roles', label: 'Roles', render: function (p) { secRoles(S, p); } },
      { id: 'grants', label: 'Grants', render: function (p) { secGrants(S, p); } },
      { id: 'users', label: 'Users', count: locked || null, tone: 'danger', render: function (p) { secUsers(S, p); } }] });
    tabs.classList.add('ops-fill'); page.appendChild(tabs); S.tabs = tabs;
    secDialogs(S);
    ctx.panel('*', function (id) { if (/^gr-/.test(id)) { var g = SEC.grants.filter(function (x) { return x.id === id; })[0]; if (g) openGrant(S, g); } else { var u = D.user(id); if (u) openUser(S, u); } });
    ctx.hasUnsaved(function () { return !!SEC.draft; }, { count: function () { return SEC.draft ? draftDiff().length : 0; }, text: 'Permission changes of the role “' + roleName(SEC.sel) + '” are not saved yet.', onDiscard: function () { SEC.draft = null; } });
    ctx.action('New role', 'plus', function () { ctx.openDialog('new-role'); });
    ctx.action('Grant access to a resource', 'key', function () { ctx.openDialog('new-grant'); });
    ctx.action('Compare roles', 'columns', function () { E.go('/admin/security', { params: { view: 'compare' } }); });
  } });

  function draftDiff() { if (!SEC.draft) return []; var cur = SEC.perms[SEC.draft.role]; return ALLP.filter(function (p) { return !!cur[p.code] !== !!SEC.draft.perms[p.code]; }); }

  /* ---------- вкладка Roles ---------- */
  function secRoles(S, panel) {
    var ctx = S.ctx;
    if (ctx.state !== 'data') { panel.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'table', rows: 9, cols: 4 }, error: { code: 'ECR-SEC-0503' },
      empty: { icon: 'shield', title: 'No custom roles yet', text: 'The eight built-in roles are always available and cover the common cases. Create a custom role when none of them fits — for example data entry without import.', actions: [{ label: 'New role', variant: 'primary', onClick: function () { ctx.openDialog('new-role'); } }, { label: 'Show built-in roles', href: '#/admin/security?state=data' }] } })); return; }
    var view = ctx.query.view === 'compare' ? 'compare' : 'role', body = h('div', { class: 'ops-rolesbody' });
    var diffBox = E.Checkbox({ id: 'sec-only-diff', label: 'Only differences', checked: SEC.onlyDiff, onChange: function (v) { SEC.onlyDiff = v; ctx.setQuery({ diff: v ? '1' : null }); paint(); } });
    if (ctx.query.roles) SEC.cmp = ctx.query.roles.split(',').filter(roleById);
    function cmpRoles() { var l = SEC.roles.filter(function (r) { return !SEC.cmp || SEC.cmp.indexOf(r.id) >= 0; }); return l.length >= 2 ? l : SEC.roles; }
    var pickBtn = E.Button({ label: 'Roles to compare', icon: 'columns', id: 'sec-cmp-roles', onClick: function (e, btn) { E.Popover(btn, { title: 'Roles to compare', width: 280, align: 'right', content: function (pop) {
      pop.appendChild(h('p', { class: 'muted t-xs' }, 'Pick two or more. “Only differences” compares just the picked roles.'));
      SEC.roles.forEach(function (r) { pop.appendChild(E.Checkbox({ id: 'sec-cmp-' + r.id, label: r.name, checked: cmpRoles().indexOf(r) >= 0, onChange: function (v) { var cur = cmpRoles().map(function (x) { return x.id; }); if (v) cur.push(r.id); else cur = cur.filter(function (x) { return x !== r.id; }); SEC.cmp = cur.length >= 2 && cur.length < SEC.roles.length ? cur : null; ctx.setQuery({ roles: SEC.cmp ? SEC.cmp.join(',') : null }); paint(); } })); }); } }); } });
    panel.appendChild(h('div', { class: 'row wrap ops-viewbar' }, E.Segmented({ id: 'sec-view', label: 'View', value: view, options: [{ value: 'role', label: 'One role', icon: 'shield' }, { value: 'compare', label: 'Compare roles', icon: 'columns' }], onChange: function (v) { view = v; ctx.setQuery({ view: v === 'compare' ? 'compare' : null }); paint(); } }), h('span', { class: 'ops-sp' }), diffBox, pickBtn));
    panel.appendChild(body);
    function paint() { body.textContent = ''; diffBox.hidden = pickBtn.hidden = view !== 'compare'; if (view === 'compare') paintMatrix(); else paintPair(); }
    S.repaintRoles = paint;

    function select(id) { if (SEC.draft && SEC.draft.role !== id) { E.UnsavedGuard({ count: draftDiff().length, text: 'Permission changes of “' + roleName(SEC.draft.role) + '” are not saved yet.', onDiscard: function () { SEC.draft = null; select(id); } }); return; } SEC.sel = id; ctx.setQuery({ role: id }); paint(); var b = E.$('#sec-role-' + id); if (b) b.focus(); }
    function shieldBtn(r, dl) {
      var b = h('button', { class: 'ops-shield', type: 'button', id: 'sec-shield-' + r.id, 'aria-label': dl.length + ' dangerous permissions in ' + r.name, title: F.plural(dl.length, { one: 'dangerous permission', other: 'dangerous permissions' }) + ':\n' + dl.map(function (p) { return '• ' + p.label + ' — ' + DANGER_WHY[p.code]; }).join('\n') }, icon('shieldAlert', 14), String(dl.length));
      b.addEventListener('click', function () { E.Popover(b, { title: 'Dangerous permissions · ' + r.name, width: 340, content: function (pop) { pop.appendChild(h('div', { class: 'stack' }, dl.map(function (p) { return h('div', E.twoLine(p.label, DANGER_WHY[p.code])); }))); pop.querySelectorAll('.two-line span, .two-line small').forEach(function (n) { n.style.whiteSpace = 'normal'; }); } }); });
      return b;
    }
    function paintPair() {
      var list = h('div', { class: 'ops-roles-list', role: 'list', 'aria-label': 'Roles' }), detail = h('div', { class: 'ops-role-detail', id: 'sec-role-detail' });
      SEC.roles.forEach(function (r) { var dl = roleDanger(r.id), n = usersWithRole(r.id).length;
        list.appendChild(h('div', { class: 'ops-role-row', role: 'listitem', 'aria-current': String(r.id === SEC.sel) },
          h('button', { class: 'ops-role', type: 'button', id: 'sec-role-' + r.id, on: { click: function () { select(r.id); } } }, h('span', { class: 'nm' }, r.name), h('span', { class: 'ds' }, r.description), h('span', { class: 'row' }, tag(r.builtIn ? 'lock' : 'pencil', r.builtIn ? 'Built-in' : 'Custom'), tag('users', F.plural(n, { one: 'user', other: 'users' })))),
          dl.length ? shieldBtn(r, dl) : null)); });
      paintDetail(detail); body.appendChild(h('div', { class: 'ops-roles' }, list, detail));
    }
    function paintDetail(detail) {
      detail.textContent = ''; var r = roleById(SEC.sel), editable = !r.builtIn, P = SEC.draft && SEC.draft.role === r.id ? SEC.draft.perms : SEC.perms[r.id], n = usersWithRole(r.id).length;
      var acts = r.builtIn ? E.Button({ label: 'Clone as custom role', icon: 'copy', id: 'sec-clone', onClick: function () { ctx.openDialog('new-role', { from: r.id }); } })
        : E.Menu({ label: 'More', id: 'sec-role-more', items: [{ label: 'Rename…', icon: 'pencil', onClick: function () { ctx.openDialog('rename-role'); } }, { label: 'Clone…', icon: 'copy', onClick: function () { ctx.openDialog('new-role', { from: r.id }); } }, { sep: true }, { label: 'Delete role…', icon: 'trash', danger: true, onClick: function () { ctx.openDialog('delete-role'); } }] });
      detail.appendChild(h('div', { class: 'ops-role-head' }, h('div', { class: 'stack grow' }, h('div', { class: 'row wrap' }, h('h2', r.name), E.CodeText(r.id), tag(r.builtIn ? 'lock' : 'pencil', r.builtIn ? 'Built-in' : 'Custom')), h('p', { class: 'muted' }, r.description + ' · ', h('a', { href: '#/admin/security?tab=users', on: { click: function () { ctx.saved['sec-users'] = { filters: { role: r.id } }; } } }, F.plural(n, { one: 'user has', other: 'users have' }) + ' this role'))), acts));
      if (r.builtIn) detail.appendChild(E.Banner({ icon: 'lock', title: 'Built-in role — read-only', text: 'Built-in roles ship with the product and change only with a product update, so their meaning is the same in every installation. To adjust permissions, clone the role and edit the copy.' }));
      var diff = draftDiff();
      if (editable && diff.length) { var addD = diff.filter(function (p) { return p.dangerous && SEC.draft.perms[p.code]; });
        detail.appendChild(E.Banner({ tone: 'info', id: 'sec-dirty', title: F.plural(diff.length, { one: 'unsaved change', other: 'unsaved changes' }), text: diff.map(function (p) { return (SEC.draft.perms[p.code] ? '+ ' : '− ') + p.label; }).join(' · ') + (addD.length ? ' — includes ' + F.plural(addD.length, { one: 'dangerous permission', other: 'dangerous permissions' }) + ', you will be asked to confirm.' : ''),
          actions: [{ label: 'Discard', id: 'sec-discard', onClick: function () { SEC.draft = null; paintDetail(detail); } }, { label: 'Save changes', icon: 'save', id: 'sec-save', onClick: function () { if (addD.length) ctx.openDialog('save-role-dangerous'); else saveDraft(S); } }] })); }
      D.permissions.forEach(function (g, gi) {
        var on = g.items.filter(function (p) { return P[p.code]; }), dn = on.filter(function (p) { return p.dangerous; }).length;
        detail.appendChild(E.Collapsible({ id: 'sec-dom-' + g.domain, title: DOMAIN_LABEL[g.domain], open: !!SEC.open[g.domain], onToggle: function (o) { SEC.open[g.domain] = o; },
          summary: on.length + ' of ' + g.items.length + ' granted' + (dn ? ' · ' + dn + ' dangerous' : ''),
          content: function (b) { b.appendChild(h('div', g.items.map(function (p) {
            var mark = p.dangerous ? h('span', { class: 'ops-danger', title: DANGER_WHY[p.code] }, icon('shieldAlert', 14), 'Dangerous') : null, pid = 'sec-perm-' + p.code.replace(/\W/g, '-');
            if (!editable) return h('div', { class: 'ops-perm' + (P[p.code] ? '' : ' off') }, icon(P[p.code] ? 'check' : 'minus', 14), E.twoLine(p.label, p.code + (p.dangerous ? ' · ' + DANGER_WHY[p.code] : ''), { mono: false }), mark, h('span', { class: 'sr' }, P[p.code] ? 'granted' : 'not granted'));
            return h('div', { class: 'ops-perm edit' }, E.Checkbox({ id: pid, label: p.label, hint: p.code + (p.dangerous ? ' · ' + DANGER_WHY[p.code] : ''), checked: !!P[p.code], onChange: function (v) {
              if (!SEC.draft) SEC.draft = { role: r.id, perms: Object.assign({}, SEC.perms[r.id]) }; if (v) SEC.draft.perms[p.code] = true; else delete SEC.draft.perms[p.code]; if (!draftDiff().length) SEC.draft = null; paintDetail(detail); var f = E.$('#' + pid); if (f) f.focus(); } }), mark); }))); } }));
      });
    }
    function paintMatrix() {
      var roles = cmpRoles(), tbody = h('tbody'), shown = 0;
      D.permissions.forEach(function (g) {
        var rows = g.items.filter(function (p) { if (!SEC.onlyDiff) return true; var first = roleHas(roles[0].id, p.code); return roles.some(function (r) { return roleHas(r.id, p.code) !== first; }); });
        if (!rows.length) return; shown += rows.length;
        tbody.appendChild(h('tr', { class: 'dom' }, h('th', { colspan: String(roles.length + 1), scope: 'colgroup' }, DOMAIN_LABEL[g.domain])));
        rows.forEach(function (p) { tbody.appendChild(h('tr', h('th', { class: 'pm', scope: 'row' }, h('div', { class: 'row' }, E.twoLine(p.label, p.code), p.dangerous ? h('span', { class: 'ops-danger', title: DANGER_WHY[p.code] }, icon('shieldAlert', 14)) : null)),
          roles.map(function (r) { var y = roleHas(r.id, p.code); return h('td', { class: y ? (p.dangerous ? 'd' : 'y') : 'n', title: r.name + ' — ' + p.label + ': ' + (y ? 'granted' : 'not granted') }, y ? icon(p.dangerous ? 'shieldAlert' : 'check', 14) : '·', h('span', { class: 'sr' }, y ? 'granted' : 'not granted')); }))); });
      });
      body.appendChild(h('p', { class: 'muted t-xs' }, 'Permissions are rows, roles are columns — read across to see who can do one thing. ' + 'Comparing ' + roles.length + ' of ' + SEC.roles.length + ' roles. ' + (SEC.onlyDiff ? 'Showing ' + shown + ' of ' + ALLP.length + ' permissions where these roles differ.' : 'Showing all ' + ALLP.length + ' permissions.')));
      if (SEC.onlyDiff && !shown) { body.appendChild(E.EmptyState({ icon: 'checkCircle', title: 'These roles are identical', text: 'The picked roles grant exactly the same permissions. Pick other roles or switch off “Only differences”.' })); return; }
      body.appendChild(h('div', { class: 'ops-matrix-wrap', tabindex: '0', role: 'region', 'aria-label': 'Role comparison matrix' }, h('table', { class: 'ops-matrix' }, h('thead', h('tr', h('th', { class: 'pm', scope: 'col' }, 'Permission'), roles.map(function (r) { return h('th', { scope: 'col', title: r.description }, h('a', { href: E.href('/admin/security', { role: r.id }) }, r.name), r.builtIn ? null : h('div', tag('pencil', 'Custom'))); }))), tbody)));
    }
    paint();
  }
  function saveDraft(S) { var d = SEC.draft; if (!d) return; var n = draftDiff().length, before = SEC.perms[d.role]; SEC.perms[d.role] = d.perms; SEC.draft = null; if (S.repaintRoles) S.repaintRoles();
    E.Toast(F.plural(n, { one: 'change', other: 'changes' }) + ' saved · ' + F.plural(usersWithRole(d.role).length, { one: 'user', other: 'users' }) + ' affected at next sign-in', { tone: 'success', action: { label: 'Undo', onClick: function () { SEC.perms[d.role] = before; if (S.repaintRoles) S.repaintRoles(); } } }); }

  /* ---------- вкладка Grants ---------- */
  function secGrants(S, panel) {
    var ctx = S.ctx;
    S.grantsList = miniList(panel, ctx, { id: 'sec-grants', rows: ctx.state === 'empty' ? [] : SEC.grants,
      stats: [{ id: 'direct', label: 'direct grants', match: function (g) { return !g.via; } }, { id: 'groups', label: 'to AD groups', match: function (g) { return g.subjectType === 'group'; } }, { id: 'manage', label: 'at Manage level', match: function (g) { return g.level === 'Manage'; } }, { id: 'inherited', label: 'inherited', match: function (g) { return !!g.via; } }],
      search: { placeholder: 'User, group or resource', text: function (g) { return subjName(g) + ' ' + subjSub(g) + ' ' + resName(g) + ' ' + g.res + ' ' + g.level; } },
      filters: [{ id: 'resType', label: 'Resources', all: 'All resource types', options: ['Project', 'Template', 'Registry'] }, { id: 'level', label: 'Levels', options: LEVELS.slice(1) }],
      table: { tall: true, pageSize: 25, rowLabel: function (g) { return 'Open grant of ' + subjName(g); }, columns: [
        { key: 'subject', label: 'Who', sortValue: subjName, render: function (g) { return E.twoLine(subjName(g), subjSub(g)); } },
        { key: 'res', label: 'Resource', sortValue: resName, render: function (g) { return E.twoLine(resName(g), g.resType + (g.res === '*' ? '' : ' · ' + g.res)); } },
        { key: 'level', label: 'Level', sortValue: function (g) { return LEVELS.indexOf(g.level); }, render: function (g) { return ladder(g.level); } },
        { key: 'via', label: 'Inherited from', hideSm: true, render: function (g) { return g.via ? h('span', { title: g.via }, g.via) : h('span', { class: 'muted' }, 'Direct'); } },
        { key: 'by', label: 'Granted by', hideSm: true },
        { key: 'at', label: 'Granted', hideSm: true, render: function (g) { return F.date(g.at); } }] },
      empty: { icon: 'shield', title: 'No grants yet', text: 'Without a grant, a role has nowhere to apply: users see no projects. Give a user or an AD group access to the first project.', actions: [{ label: 'Grant access', variant: 'primary', onClick: function () { ctx.openDialog('new-grant'); } }] },
      error: { code: 'ECR-SEC-0504' }, onOpen: function (g) { ctx.openPanel(g.id); } });
  }
  function openGrant(S, g) {
    var ctx = S.ctx, grp = g.subjectType === 'group' ? D.groups.filter(function (x) { return x.id === g.subject; })[0] : null;
    S.grantDrawer = E.Drawer({ id: 'sec-grant', title: subjName(g), subtitle: g.level + ' · ' + resName(g), onClose: function () { S.grantDrawer = null; if (S.grantsList) S.grantsList.table.select(null); },
      body: function (b) {
        if (g.via) b.appendChild(E.Banner({ icon: 'branch', title: 'Inherited grant', text: g.via + '. It cannot be changed or revoked here — change the grant it comes from.' }));
        b.appendChild(E.KeyValue([{ label: 'Who', value: subjName(g), hint: subjSub(g) }, { label: 'Resource', value: resName(g), hint: g.resType + (g.res === '*' ? ' · every current and future project' : ' · ' + g.res) }, { label: 'Level', value: ladder(g.level) }, { label: 'Source', value: g.via ? 'Inherited' : 'Direct grant' }, { label: 'Granted by', value: g.by }, { label: 'Granted', value: F.date(g.at) }]));
        b.appendChild(E.Section({ title: 'What each level allows', hint: 'every level includes the ones before it', content: timeline(LEVELS.map(function (l, i) { var ci = LEVELS.indexOf(g.level); return { title: l, cls: i === ci ? 'cur' : null, dot: i < ci ? 'filled' : '', body: h('div', { class: i > ci ? 'muted' : null }, LEVEL_TEXT[l]) }; })) }));
        if (grp) b.appendChild(E.Section({ title: 'Members of the group', hint: 'read from Active Directory', content: rowsBox(grp.members.map(function (id) { var u = D.user(id); return rowItem([h('b', u.fullName)], h('a', { href: E.href('/admin/security', { tab: 'users', panel: u.id }) }, 'Open user'), u.login + ' · ' + u.roles.map(roleName).join(', ')); })) }));
        if (!g.via) { var sel = E.Select({ id: 'sec-grant-level', label: 'Change level', value: g.level, options: LEVELS.slice(1), hint: 'Applies immediately; users see the change at their next request.', onChange: function (v) { var old = g.level; g.level = v; if (S.grantsList) S.grantsList.refresh(); if (S.grantDrawer) S.grantDrawer.close(); later(function () { ctx.openPanel(g.id); });
          E.Toast('Level changed: ' + old + ' → ' + v, { tone: 'success', action: { label: 'Undo', onClick: function () { g.level = old; if (S.grantsList) S.grantsList.refresh(); } } }); } }); b.appendChild(sel); }
      },
      footer: g.via ? [{ label: 'Close' }] : [{ label: 'Revoke…', icon: 'ban', id: 'sec-grant-revoke', align: 'left', keepOpen: true, onClick: function () { ctx.openDialog('revoke-grant', g); } }, { label: 'Close' }] });
    if (S.grantsList) S.grantsList.table.select(g.id);
  }

  /* ---------- вкладка Users ---------- */
  function timed(u) { return (SEC.assign[u.id] || []).filter(function (a) { return a.to; }); }
  function secUsers(S, panel) {
    var ctx = S.ctx;
    S.usersList = miniList(panel, ctx, { id: 'sec-users', rows: ctx.state === 'empty' ? [] : D.users,
      stats: [{ id: 'active', label: 'active', match: function (u) { return u.state === 'Active'; } }, { id: 'locked', label: 'locked', tone: 'danger', match: function (u) { return u.state === 'Locked'; } }, { id: 'invited', label: 'invited, not signed in', match: function (u) { return u.state === 'Invited'; } }, { id: 'expiring', label: 'with a role ending ≤ 14 days', tone: 'warning', match: function (u) { return timed(u).some(function (a) { return daysTo(a.to) <= 14; }); } }],
      search: { placeholder: 'Name or account', text: function (u) { return u.fullName + ' ' + u.name + ' ' + u.login; } },
      filters: [{ id: 'role', label: 'Roles', options: SEC.roles.map(function (r) { return { value: r.id, label: r.name }; }), match: function (u, v) { return (SEC.assign[u.id] || []).some(function (a) { return a.role === v; }); } }, { id: 'state', label: 'States', options: ['Active', 'Locked', 'Disabled', 'Invited'] }, { id: 'auth', label: 'Providers', all: 'All sign-in providers', options: ['Windows', 'Local'] }],
      table: { tall: true, rowLabel: function (u) { return 'Open ' + u.fullName; }, columns: [
        { key: 'fullName', label: 'Name', render: function (u) { return E.twoLine(u.fullName, u.login, { mono: false }); } },
        { key: 'auth', label: 'Sign-in', hideSm: true, render: function (u) { return tag(u.auth === 'Windows' ? 'windows' : 'lock', u.auth === 'Windows' ? 'Windows' : 'Local password'); } },
        { key: 'roles', label: 'Roles', sortable: false, render: function (u) { var a = SEC.assign[u.id] || [], t = timed(u); return h('span', { class: 'row', title: a.map(function (x) { return roleName(x.role) + (x.to ? ' (until ' + F.date(x.to) + ')' : ''); }).join(', ') }, a.map(function (x) { return roleName(x.role); }).join(', '), t.length ? tag('clock', 'until ' + F.date(t[0].to).slice(0, 6)) : null); } },
        { key: 'state', label: 'State', render: function (u) { return E.StatusBadge('user', u.state, { quiet: u.state === 'Active' }); } },
        { key: 'lastSeen', label: 'Last sign-in', hideSm: true, render: function (u) { return u.lastSeen ? F.dateTime(u.lastSeen) : h('span', { class: 'muted' }, 'Never'); } }] },
      empty: { icon: 'users', title: 'No users yet', text: 'Windows users appear here after their first sign-in. Local accounts are created by an administrator.', actions: [{ label: 'New local user', variant: 'primary', onClick: function () { ctx.openDialog('new-user'); } }] },
      error: { code: 'ECR-SEC-0505' }, onOpen: function (u) { ctx.openPanel(u.id); } });
  }
  function reopenUser(S, u) { later(function () { if (S.userDrawer) S.userDrawer.close(); if (S.usersList) S.usersList.refresh(); S.ctx.openPanel(u.id); }); }
  function openUser(S, u) {
    var ctx = S.ctx, asg = SEC.assign[u.id] || [], foot = [];
    if (u.state !== 'Disabled' && u.state !== 'Invited') foot.push({ label: 'Simulate this user', icon: 'eye', id: 'sec-user-sim', align: 'left', keepOpen: true, onClick: function () { ctx.openDialog('simulate-user', u); } });
    if (u.auth === 'Local' && u.state !== 'Disabled') foot.push({ label: 'Reset password…', icon: 'refresh', id: 'sec-user-reset', keepOpen: true, onClick: function () { ctx.openDialog('reset-password', u); } });
    if (u.state === 'Locked') foot.push({ label: 'Unlock', icon: 'unlock', id: 'sec-user-unlock', variant: 'primary', keepOpen: true, onClick: function () { unlock(); } });
    else if (u.state === 'Active') foot.push({ label: 'Lock…', icon: 'lock', id: 'sec-user-lock', keepOpen: true, onClick: function () { ctx.openDialog('lock-user', u); } });
    if (!foot.length) foot.push({ label: 'Close' });
    function unlock() { u.state = 'Active'; reopenUser(S, u); E.Toast(u.name + ' unlocked — can sign in again', { tone: 'success', action: { label: 'Undo', onClick: function () { u.state = 'Locked'; reopenUser(S, u); } } }); }
    S.userDrawer = E.Drawer({ id: 'sec-user', wide: true, title: u.fullName, subtitle: u.login, badge: dbadge('user', u.state), onClose: function () { S.userDrawer = null; if (S.usersList) S.usersList.table.select(null); }, footer: foot,
      body: function (b) {
        if (u.state === 'Locked') b.appendChild(E.Banner({ tone: 'danger', title: 'Account is locked', text: 'Locked automatically after 5 failed sign-ins on ' + F.dateTime(u.lastSeen) + '. Unlock it once the user confirms it was them; reset the password if not.', actions: [{ label: 'Unlock', icon: 'unlock', onClick: unlock }] }));
        if (u.state === 'Invited') b.appendChild(E.Banner({ tone: 'info', title: 'Invited — has not signed in yet', text: 'Roles below start working at the first sign-in with the Windows account.' }));
        if (u.state === 'Disabled') b.appendChild(E.Banner({ icon: 'ban', title: 'Account is disabled', text: 'It cannot sign in. History in the audit trail is kept.' }));
        b.appendChild(E.KeyValue([{ label: 'Account', value: withCopy(u.login, 'account name') }, { label: 'Sign-in', value: u.auth === 'Windows' ? 'Windows (Active Directory)' : 'Local password' }, { label: 'Last sign-in', value: u.lastSeen ? F.dateTime(u.lastSeen) : 'Never' }]));
        b.appendChild(E.Section({ id: 'sec-user-roles', title: 'Roles', hint: 'with validity', actions: [{ label: 'Assign role…', icon: 'plus', size: 'sm', id: 'sec-user-assign', onClick: function () { ctx.openDialog('grant-role', u); } }],
          content: asg.length ? rowsBox(asg.map(function (a) { var dl = roleDanger(a.role), left = a.to ? daysTo(a.to) : null, rm = E.Button({ iconOnly: true, icon: 'x', label: 'Remove role ' + roleName(a.role) });
            rm.addEventListener('click', function () { E.InlineConfirm(rm, { text: 'Remove “' + roleName(a.role) + '” from ' + u.name + '?', verb: 'Remove', danger: true, onConfirm: function () { var i = asg.indexOf(a); asg.splice(i, 1); reopenUser(S, u); E.Toast('Role removed', { action: { label: 'Undo', onClick: function () { asg.splice(i, 0, a); reopenUser(S, u); } } }); } }); });
            return rowItem([h('b', roleName(a.role)), E.CodeText(a.role), dl.length ? h('span', { class: 'ops-danger', title: dl.map(function (p) { return p.label; }).join('\n') }, icon('shieldAlert', 14), String(dl.length)) : null], rm,
              h('span', { class: left != null && left <= 14 ? 'warning-text' : null }, a.to ? 'Valid ' + F.date(a.from) + ' → ' + F.date(a.to) + ' · ends ' + inDays(left) + ', then removed automatically' : 'Valid from ' + F.date(a.from) + ' · no end date', a.note ? ' · ' + a.note : '')); }))
            : h('div', { class: 'mini-empty' }, 'No roles — the user can sign in but sees nothing.') }));
        var gs = groupsOf(u);
        b.appendChild(E.Section({ title: 'AD groups', hint: u.auth === 'Windows' ? 'read from Active Directory at sign-in · not editable here' : null, content: gs.length ? rowsBox(gs.map(function (g) { return rowItem([icon('users', 14), h('b', g.name)], null, g.description + ' · gives access to ' + (g.projects[0] === '*' ? 'all projects' : F.plural(g.projects.length, { one: 'project', other: 'projects' }))); }))
          : h('p', { class: 'muted' }, u.auth === 'Local' ? 'Local accounts are not members of AD groups — they need direct grants.' : 'Not a member of any group that has grants in ECR.') }));
        var eff = effective(u);
        b.appendChild(E.Section({ id: 'sec-user-eff', title: 'Effective rights', hint: 'what the roles and grants add up to — and why', content: eff.length ? rowsBox(eff.map(function (e) {
            return rowItem([h('b', e.res), h('span', { class: 'muted t-xs' }, e.resSub)], ladder(e.level), e.capped ? h('span', { class: 'warning-text' }, 'The grant gives ' + e.capped + ', but no role of this user allows it — effective level is ' + e.level + '.') : null,
              h('div', { class: 'ops-chain', 'aria-label': 'Where this level comes from' }, e.chain.slice(0, 2).map(function (c, i) { return [i ? icon('arrowR', 12) : null, h('span', { class: 'row' }, icon(c[0], 12), c[1])]; }), icon('arrowR', 12), h('span', 'this resource'))); }))
          : h('div', { class: 'mini-empty' }, h('b', { class: 't-sb' }, 'No access to any resource'), h('span', 'Roles alone are not enough — add the user to an AD group with grants or grant access directly.')) }));
        b.appendChild(E.Collapsible({ id: 'sec-user-check', title: 'Check one permission', summary: 'Can this user do …?', content: function (c) { var out = h('p', { class: 'muted', id: 'sec-user-check-out', role: 'status' }, 'Pick a permission to see the answer and the role it comes from.');
          c.appendChild(E.Select({ id: 'sec-user-check-perm', label: 'Permission', value: '', options: [{ value: '', label: 'Choose…' }].concat(ALLP.map(function (p) { return { value: p.code, label: DOMAIN_LABEL[p.code.split('.')[0]] + ' · ' + p.label }; })), onChange: function (v) { if (!v) return; var a = asg.filter(function (x) { return roleHas(x.role, v); })[0]; out.className = a ? '' : 'muted'; out.textContent = a ? 'Yes — allowed by the role ' + roleName(a.role) + (a.to ? ' (until ' + F.date(a.to) + ')' : '') + ', on the resources listed above.' : 'No — none of the user’s roles includes “' + perm(v).label + '”.'; } })); c.appendChild(out); } }));
      } });
    if (S.usersList) S.usersList.table.select(u.id);
  }

  /* ---------- діалоги Security ---------- */
  function secDialogs(S) {
    var ctx = S.ctx;
    /* New role — Wizard: назва → права з пошуком → небезпечні з підтвердженням → Review */
    ctx.dialog('new-role', function (arg) {
      var data = { name: '', desc: '', from: (arg && arg.from) || '', perms: {}, ack: false };
      function load(id) { data.perms = id ? Object.assign({}, SEC.perms[id]) : {}; }
      function chosen() { return ALLP.filter(function (p) { return data.perms[p.code]; }); }
      function dang() { return chosen().filter(function (p) { return p.dangerous; }); }
      if (data.from) { load(data.from); data.name = roleName(data.from) + ' (copy)'; }
      else if (!arg && ctx.query.step) { /* адреса ?dialog=new-role&step=N — показовий випадок для обходу */ data.from = 'Approver'; load('Approver'); data.perms['Period.Reopen'] = true; data.name = 'Shift supervisor'; data.desc = 'Approves sheets and can reopen a period during night shifts'; data.ack = +ctx.query.step > 2; }
      wzJump(ctx, 3, E.Wizard({ id: 'sec-nr', title: 'New role', data: data, applyLabel: 'Create role', steps: [
        { id: 'name', label: 'Name', hint: 'The name appears in grants, in the user list and in the audit trail.', render: function (b) {
            b.appendChild(E.Input({ id: 'sec-nr-name', label: 'Role name', required: true, value: data.name, maxlength: 60, onInput: function (v) { data.name = v; } }));
            b.appendChild(E.Input({ id: 'sec-nr-desc', label: 'What is it for', hint: 'One sentence that helps the next administrator pick the right role.', value: data.desc, maxlength: 120, onInput: function (v) { data.desc = v; } }));
            b.appendChild(E.Select({ id: 'sec-nr-from', label: 'Start from', value: data.from, options: [{ value: '', label: 'Empty — no permissions' }].concat(SEC.roles.map(function (r) { return { value: r.id, label: 'Copy of ' + r.name }; })), hint: 'Copying a role is safer than building from nothing: you remove what is not needed.', onChange: function (v) { data.from = v; load(v); data.ack = false; } })); },
          validate: function () { var n = data.name.trim(); if (!n) return 'Give the role a name.'; if (SEC.roles.some(function (r) { return r.name.toLowerCase() === n.toLowerCase(); })) return 'A role named “' + n + '” already exists. Choose another name.'; return null; } },
        { id: 'perms', label: 'Permissions', render: function (b) {
            var q = '', listEl = h('div', { class: 'ops-permlist', id: 'sec-nr-list' }), cnt = h('p', { class: 'muted t-xs', role: 'status' });
            function count() { var d = dang().length; cnt.textContent = chosen().length + ' of ' + ALLP.length + ' selected' + (d ? ' · ' + d + ' dangerous — you will confirm them on the next step' : ''); }
            function paint() { listEl.textContent = ''; var any = false;
              D.permissions.forEach(function (g) { var items = g.items.filter(function (p) { return !q || (p.label + ' ' + p.code).toLowerCase().indexOf(q) >= 0; }); if (!items.length) return; any = true;
                listEl.appendChild(h('div', { class: 'eyebrow' }, DOMAIN_LABEL[g.domain]));
                items.forEach(function (p) { listEl.appendChild(h('div', { class: 'ops-perm edit' }, E.Checkbox({ id: 'sec-nr-p-' + p.code.replace(/\W/g, '-'), label: p.label, hint: p.code, checked: !!data.perms[p.code], onChange: function (v) { if (v) data.perms[p.code] = true; else delete data.perms[p.code]; data.ack = false; count(); } }), p.dangerous ? h('span', { class: 'ops-danger', title: DANGER_WHY[p.code] }, icon('shieldAlert', 14), 'Dangerous') : null)); }); });
              if (!any) listEl.appendChild(h('div', { class: 'mini-empty' }, 'No permission matches “' + q + '”.')); }
            b.appendChild(E.Input({ id: 'sec-nr-q', label: 'Find a permission', icon: 'search', type: 'search', placeholder: 'approve, import, Period.…', onInput: function (v) { q = v.trim().toLowerCase(); paint(); } }));
            b.appendChild(listEl); b.appendChild(cnt); paint(); count(); },
          validate: function () { return chosen().length ? null : 'Select at least one permission — a role without permissions does nothing.'; } },
        { id: 'danger', label: 'Dangerous', canNext: function () { return !dang().length || data.ack; }, render: function (b, d, api) {
            var dl = dang();
            if (!dl.length) { b.appendChild(E.Banner({ icon: 'checkCircle', title: 'No dangerous permissions selected', text: 'Nothing to confirm here. Continue to the summary.' })); return; }
            b.appendChild(E.Banner({ tone: 'warning', title: 'This role includes ' + F.plural(dl.length, { one: 'dangerous permission', other: 'dangerous permissions' }), text: 'Everyone who gets the role will be able to do the following. Read each line — this is what you are signing off.' }));
            b.appendChild(rowsBox(dl.map(function (p) { return rowItem([icon('shieldAlert', 14), h('b', p.label), E.CodeText(p.code)], null, DANGER_WHY[p.code]); })));
            b.appendChild(E.Checkbox({ id: 'sec-nr-ack', label: 'I understand what these permissions allow and want them in this role', checked: data.ack, onChange: function (v) { data.ack = v; api.refreshButtons(); } })); } }],
        summary: function () { var dl = dang(); return h('div', { class: 'stack gap-3' }, E.KeyValue([{ label: 'Name', value: data.name.trim() }, { label: 'Purpose', value: data.desc.trim() }, { label: 'Based on', value: data.from ? roleName(data.from) : 'Empty role' }, { label: 'Permissions', value: chosen().length + ' of ' + ALLP.length, hint: D.permissions.map(function (g) { var n = g.items.filter(function (p) { return data.perms[p.code]; }).length; return n ? DOMAIN_LABEL[g.domain] + ' ' + n : null; }).filter(Boolean).join(' · ') },
          { label: 'Dangerous', value: dl.length ? h('span', { class: 'ops-danger' }, icon('shieldAlert', 14), dl.map(function (p) { return p.label; }).join(' · ')) : 'None' }]), h('p', { class: 'muted' }, 'The role gives nothing to anyone until you assign it to a user.')); },
        onApply: function (d, api) { var id = data.name.trim().replace(/[^A-Za-z0-9]+/g, ' ').replace(/(?:^| )(\w)/g, function (m, c) { return c.toUpperCase(); }) || 'CustomRole'; while (roleById(id)) id += '2';
          SEC.roles.push({ id: id, name: data.name.trim(), description: data.desc.trim() || 'Custom role', builtIn: false }); SEC.perms[id] = Object.assign({}, data.perms); SEC.sel = id; api.close();
          E.go('/admin/security', { params: { role: id, dialog: 'result-role-created' }, toast: 'Role created' }); } }));
    });
    ctx.dialog('result-role-created', function () { var r = roleById(SEC.sel); S.res.show({ title: 'Role “' + r.name + '” is ready', text: 'It has ' + F.plural(Object.keys(SEC.perms[r.id]).length, { one: 'permission', other: 'permissions' }) + ' and no users yet. What next: assign it to a person, then check what they can really do.', actions: [{ label: 'Assign to a user', icon: 'plus', onClick: function () { ctx.openDialog('grant-role', { role: r.id }); } }, { label: 'Compare with Approver and Data Entry', href: E.href('/admin/security', { view: 'compare', diff: '1', roles: r.id + ',Approver,DataEntry' }) }] }); });
    ctx.dialog('rename-role', function () { var r = roleById(SEC.sel); if (r.builtIn) r = roleById('NightShiftEntry'); var f = E.Input({ id: 'sec-rn-name', label: 'Role name', required: true, value: r.name, maxlength: 60 });
      E.Dialog({ id: 'sec-rn', title: 'Rename “' + r.name + '”', narrow: true, body: h('div', { class: 'stack gap-3' }, f, h('p', { class: 'muted' }, 'The code ' + r.id + ' stays the same, so grants and history are not affected.')), footer: [{ label: 'Cancel' }, { label: 'Rename', variant: 'primary', onClick: function () { var v = f.value.trim(); if (!v) { f.setError('Give the role a name.'); f.input.focus(); return false; } var old = r.name; r.name = v; if (S.repaintRoles) S.repaintRoles(); E.Toast('Renamed to “' + v + '”', { tone: 'success', action: { label: 'Undo', onClick: function () { r.name = old; if (S.repaintRoles) S.repaintRoles(); } } }); } }] }); });
    ctx.dialog('delete-role', function () { var r = roleById(SEC.sel); if (r.builtIn) r = roleById('NightShiftEntry'); var us = usersWithRole(r.id);
      E.ConfirmDialog({ id: 'sec-del-role', title: 'Delete role “' + r.name + '”?', consequences: [us.length ? F.plural(us.length, { one: 'user loses', other: 'users lose' }) + ' it immediately: ' + us.map(function (u) { return u.name; }).join(', ') + '.' : 'Nobody has this role, so nobody loses access.', 'Timed assignments of this role are cancelled.', { text: 'History in the audit trail keeps the role name.', note: true }], verb: 'Delete role', icon: 'trash',
        onConfirm: function () { var i = SEC.roles.indexOf(r), saved = {}; SEC.roles.splice(i, 1); Object.keys(SEC.assign).forEach(function (k) { saved[k] = SEC.assign[k]; SEC.assign[k] = SEC.assign[k].filter(function (a) { return a.role !== r.id; }); }); SEC.sel = 'Approver'; SEC.draft = null; ctx.setQuery({ role: null });
          later(function () { if (S.repaintRoles) S.repaintRoles(); }); E.Toast('Role “' + r.name + '” deleted', { action: { label: 'Undo', onClick: function () { SEC.roles.splice(i, 0, r); SEC.assign = saved; SEC.sel = r.id; if (S.repaintRoles) S.repaintRoles(); } } }); } }); });
    ctx.dialog('save-role-dangerous', function () { var r = roleById(SEC.sel); if (!SEC.draft) { r = roleById('NightShiftEntry'); SEC.sel = r.id; SEC.draft = { role: r.id, perms: Object.assign({}, SEC.perms[r.id], { 'Document.Delete': true }) }; if (S.repaintRoles) S.repaintRoles(); }
      var dl = draftDiff().filter(function (p) { return p.dangerous && SEC.draft.perms[p.code]; });
      E.ConfirmDialog({ id: 'sec-save-d', title: 'Add dangerous permissions to “' + r.name + '”?', text: F.plural(usersWithRole(r.id).length, { one: 'user gets', other: 'users get' }) + ' them at the next sign-in:', consequences: dl.map(function (p) { return p.label + ' — ' + DANGER_WHY[p.code]; }).concat([{ text: 'The change is written to the audit trail with your name.', note: true }]), verb: 'Save with dangerous permissions', icon: 'shieldAlert', onConfirm: function () { saveDraft(S); } }); });

    /* Grant access to a resource */
    ctx.dialog('new-grant', function () {
      var pick = null, rt = 'Project', res = D.project.id, level = 'Write', q = '';
      var subjects = D.groups.map(function (g) { return { type: 'group', id: g.id, name: g.name, sub: 'AD group · ' + g.members.length + ' members', icon: 'users' }; }).concat(D.users.filter(function (u) { return u.state !== 'Disabled'; }).map(function (u) { return { type: 'user', id: u.id, name: u.fullName, sub: u.login, icon: 'user' }; }));
      var picks = h('div', { class: 'ops-picks', id: 'sec-ng-picks', role: 'group', 'aria-label': 'Matching users and groups' }), conseq = h('div'), resHost = h('div');
      var search = E.Input({ id: 'sec-ng-q', label: 'Who gets access', required: true, icon: 'search', placeholder: 'Name, account or AD group', hint: 'Prefer an AD group: membership is managed in one place and new people get access automatically.', onInput: function (v) { q = v.trim().toLowerCase(); paintPicks(); } });
      function resOptions() { return rt === 'Project' ? [{ value: '*', label: 'All projects' }].concat(D.projects.map(function (p) { return { value: p.id, label: p.name + ' · ' + p.id }; })) : rt === 'Template' ? D.templates.map(function (t) { return { value: t.id, label: t.name }; }) : D.registries.map(function (r) { return { value: r.code, label: r.name }; }); }
      function paintRes() { resHost.textContent = ''; var o = resOptions(); if (!o.some(function (x) { return x.value === res; })) res = o[0].value; resHost.appendChild(E.Select({ id: 'sec-ng-res', label: rt, value: res, options: o, onChange: function (v) { res = v; paintConseq(); } })); }
      function paintPicks() { picks.textContent = ''; var list = subjects.filter(function (s) { return !q || (s.name + ' ' + s.sub).toLowerCase().indexOf(q) >= 0; }).slice(0, 8);
        list.forEach(function (s) { picks.appendChild(h('button', { type: 'button', id: 'sec-ng-s-' + s.id, 'aria-pressed': String(pick === s), on: { click: function () { pick = s; search.setError(null); paintPicks(); paintConseq(); } } }, icon(s.icon, 14), s.name, h('small', s.sub))); });
        if (!list.length) picks.appendChild(h('div', { class: 'mini-empty' }, 'Nobody matches “' + q + '”. Windows users appear after their first sign-in.')); }
      function paintConseq() { conseq.textContent = ''; var g = { resType: rt, res: res }, who = pick ? pick.name : 'The selected user or group', members = pick && pick.type === 'group' ? D.groups.filter(function (x) { return x.id === pick.id; })[0].members.map(function (id) { return D.user(id).name; }) : null;
        var text = who + ' will be able to: ' + LEVEL_TEXT[level].replace(/^Can /, '').replace(/\.$/, '') + ' — in ' + resName(g) + '.' + (members ? ' Applies to ' + F.plural(members.length, { one: 'member', other: 'members' }) + ' now (' + members.join(', ') + ') and to everyone added to the group later.' : '') + ' The level works only together with a role that allows it.';
        conseq.appendChild(E.Banner({ tone: level === 'Manage' || res === '*' ? 'warning' : null, icon: level === 'Manage' ? 'shieldAlert' : 'info', title: level === 'Manage' ? 'Manage lets them give access to others' : res === '*' ? 'Covers every current and future project' : 'What this grant does', text: text })); }
      E.Dialog({ id: 'sec-ng', title: 'Grant access', wide: true, body: function (b) {
          b.appendChild(search); b.appendChild(picks);
          b.appendChild(h('div', { class: 'form-grid' }, E.Select({ id: 'sec-ng-rt', label: 'Resource type', value: rt, options: ['Project', 'Template', 'Registry'], onChange: function (v) { rt = v; paintRes(); paintConseq(); } }), resHost));
          b.appendChild(h('div', { class: 'field ops-segf' }, h('span', { class: 'lbl', id: 'sec-ng-level-l' }, 'Level'), E.Segmented({ id: 'sec-ng-level', label: 'Level', value: level, options: LEVELS.slice(1), onChange: function (v) { level = v; paintConseq(); } })));
          b.appendChild(conseq); paintPicks(); paintRes(); paintConseq(); },
        footer: [{ label: 'Cancel' }, { label: 'Grant access', variant: 'primary', icon: 'check', id: 'sec-ng-ok', onClick: function () {
          if (!pick) { search.setError('Choose a user or an AD group from the list.'); search.input.focus(); return false; }
          var g = addGrant(pick.type, pick.id, rt, res, level, null, ctx.me.name, NOW.slice(0, 10)); ctx.setQuery({ tab: 'grants' });
          S.res.flash(function (c) { return { title: 'Access granted', text: subjName(g) + ' now has ' + g.level + ' on ' + resName(g) + '. It works at their next request — no sign-out needed.', actions: [{ label: 'Open the grant', onClick: function () { c.openPanel(g.id); } }, { label: 'Undo', icon: 'undo', onClick: function () { SEC.grants.splice(SEC.grants.indexOf(g), 1); delete FLASH.sec; c.refresh(); E.Toast('Grant removed'); } }] }; }); } }] });
    });
    ctx.dialog('result-granted', function () { var g = SEC.grants[0]; S.res.show({ title: 'Access granted', text: subjName(g) + ' now has ' + g.level + ' on ' + resName(g) + '. It works at their next request — no sign-out needed.', actions: [{ label: 'Open the grant', onClick: function () { ctx.openPanel(g.id); } }] }); });
    ctx.dialog('revoke-grant', function (g) { g = g || SEC.grants.filter(function (x) { return !x.via; })[0]; var grp = g.subjectType === 'group' ? D.groups.filter(function (x) { return x.id === g.subject; })[0] : null;
      E.ConfirmDialog({ id: 'sec-revoke', title: 'Revoke ' + g.level + ' on “' + resName(g) + '” from ' + subjName(g) + '?', consequences: [grp ? F.plural(grp.members.length, { one: 'member loses', other: 'members lose' }) + ' access: ' + grp.members.map(function (id) { return D.user(id).name; }).join(', ') + '.' : subjName(g) + ' stops seeing this resource immediately.', 'Edits they have not saved yet will be rejected by the server.', { text: 'Entered data and history stay. You can grant access again at any time.', note: true }], verb: 'Revoke access', icon: 'ban',
        onConfirm: function () { var i = SEC.grants.indexOf(g); SEC.grants.splice(i, 1); later(function () { if (S.grantDrawer) S.grantDrawer.close(); if (S.grantsList) S.grantsList.refresh(); });
          E.Toast('Access revoked', { action: { label: 'Undo', onClick: function () { SEC.grants.splice(i, 0, g); if (S.grantsList) S.grantsList.refresh(); } } }); } }); });

    /* Assign a role to a user — зі строком дії */
    ctx.dialog('grant-role', function (arg) {
      var u = arg && arg.id ? arg : D.user('u-02'), rid = (arg && arg.role) || 'Approver', mode = 'always', dl = h('div');
      var from = E.Input({ id: 'sec-gr-from', label: 'Valid from', type: 'date', value: NOW.slice(0, 10) }), to = E.Input({ id: 'sec-gr-to', label: 'Valid to', type: 'date', value: addDays(NOW, 14), hint: 'The role is removed automatically at the end of this day.' }), dates = h('div', { class: 'form-grid', hidden: true }, from, to);
      function paintD() { dl.textContent = ''; var d = roleDanger(rid); if (d.length) dl.appendChild(E.Banner({ tone: 'warning', icon: 'shieldAlert', title: roleName(rid) + ' includes ' + F.plural(d.length, { one: 'dangerous permission', other: 'dangerous permissions' }), text: d.map(function (p) { return p.label; }).join(' · ') })); else dl.appendChild(E.Banner({ title: 'No dangerous permissions in this role', text: roleById(rid).description + '.' })); }
      E.Dialog({ id: 'sec-gr', title: 'Assign a role', body: function (b) {
          b.appendChild(E.Select({ id: 'sec-gr-user', label: 'User', value: u.id, options: D.users.filter(function (x) { return x.state !== 'Disabled'; }).map(function (x) { return { value: x.id, label: x.fullName + ' · ' + x.login }; }), onChange: function (v) { u = D.user(v); } }));
          b.appendChild(E.Select({ id: 'sec-gr-role', label: 'Role', value: rid, options: SEC.roles.map(function (r) { return { value: r.id, label: r.name }; }), onChange: function (v) { rid = v; paintD(); } })); b.appendChild(dl);
          b.appendChild(h('div', { class: 'field ops-segf' }, h('span', { class: 'lbl' }, 'Validity'), E.Segmented({ id: 'sec-gr-mode', label: 'Validity', value: mode, options: [{ value: 'always', label: 'No end date' }, { value: 'timed', label: 'For a limited time' }], onChange: function (v) { mode = v; dates.hidden = v !== 'timed'; } })));
          b.appendChild(dates); paintD(); },
        footer: [{ label: 'Cancel' }, { label: 'Assign role', variant: 'primary', id: 'sec-gr-ok', onClick: function () {
          if ((SEC.assign[u.id] || []).some(function (a) { return a.role === rid; })) { E.Toast(u.name + ' already has this role', 'warning'); return false; }
          if (mode === 'timed' && !(to.value > from.value)) { to.setError('“Valid to” must be later than “Valid from”.'); to.input.focus(); return false; }
          var a = { role: rid, from: mode === 'timed' ? from.value : NOW.slice(0, 10), to: mode === 'timed' ? to.value : null, by: ctx.me.name }; (SEC.assign[u.id] = SEC.assign[u.id] || []).push(a); ctx.setQuery({ tab: 'users', panel: null });
          S.res.flash(function (c) { return { title: roleName(rid) + ' assigned to ' + u.name, text: (a.to ? 'Valid ' + F.date(a.from) + ' → ' + F.date(a.to) + '; it is removed automatically afterwards. ' : 'No end date. ') + 'A role says what a person may do — check that a grant says where.', actions: [{ label: 'See effective rights', onClick: function () { c.openPanel(u.id); } }] }; }); } }] });
    });
    ctx.dialog('result-role-assigned', function () { var u = D.user('u-04'); S.res.show({ title: 'Approver assigned to ' + u.name, text: 'Valid 15 Sep 2026 → 30 Sep 2026; it is removed automatically afterwards. A role says what a person may do — check that a grant says where.', actions: [{ label: 'See effective rights', onClick: function () { ctx.openPanel(u.id); } }] }); });
    ctx.dialog('new-user', function () { var login = E.Input({ id: 'sec-nu-login', label: 'Account name', required: true, mono: true, hint: 'Local accounts are for services and contractors without a Windows account.' }), name = E.Input({ id: 'sec-nu-name', label: 'Full name', required: true }), role = E.Select({ id: 'sec-nu-role', label: 'First role', value: 'Viewer', options: SEC.roles.map(function (r) { return { value: r.id, label: r.name }; }) });
      E.Dialog({ id: 'sec-nu', title: 'New local user', body: h('div', { class: 'stack gap-3' }, login, name, role, h('p', { class: 'muted' }, 'A one-time password is shown once after creation. The user must change it at the first sign-in.')), footer: [{ label: 'Cancel' }, { label: 'Create user', variant: 'primary', onClick: function () {
        var l = login.value.trim(); if (!l) { login.setError('Enter an account name.'); login.input.focus(); return false; } if (D.users.some(function (x) { return x.login.toLowerCase() === l.toLowerCase(); })) { login.setError('This account name is already taken.'); login.input.focus(); return false; } if (!name.value.trim()) { name.setError('Enter the full name.'); name.input.focus(); return false; }
        var nm = name.value.trim(), u = { id: 'u-' + (D.users.length + 1), name: nm, fullName: nm, login: l, roles: [role.value], auth: 'Local', state: 'Invited', lastSeen: null }; D.users.push(u); SEC.assign[u.id] = [{ role: role.value, from: NOW.slice(0, 10), to: null, by: ctx.me.name }];
        ctx.setQuery({ tab: 'users' }); later(function () { if (S.usersList) S.usersList.refresh(); ctx.openDialog('result-password', u); }); } }] }); });
    ctx.dialog('reset-password', function (u) { u = u || D.user('u-09');
      E.ConfirmDialog({ id: 'sec-reset', title: 'Reset the password of ' + u.name + '?', danger: false, consequences: ['The current password stops working immediately; open sessions are ended.', 'You get a one-time password — it is shown once and never again.', { text: 'The user must set a new password at the first sign-in.', note: true }], verb: 'Reset password', icon: 'key', onConfirm: function () { later(function () { ctx.openDialog('result-password', u); }); } }); });
    ctx.dialog('result-password', function (u) { u = u || D.user('u-09'); var otp = 'Kx7-Tulip-29-Marsh';
      E.Dialog({ id: 'sec-otp', title: 'One-time password for ' + u.name, dismissable: false, alert: true, body: h('div', { class: 'stack gap-3' }, E.Banner({ tone: 'warning', title: 'Shown only once', text: 'Copy it now and pass it to the user over a trusted channel. After you close this window it cannot be displayed again — only reset.' }), h('div', { class: 'ops-otp', id: 'sec-otp-value' }, otp, copyBtn(otp, 'one-time password')), h('p', { class: 'muted' }, 'Valid for 24 hours. ' + u.name + ' will be asked to choose a new password right after signing in.')),
        footer: [{ label: 'I have passed it on', variant: 'primary', id: 'sec-otp-done', onClick: function () { E.Toast('Password reset — what next: tell ' + u.name + ' to sign in within 24 hours', 'success'); } }] }); });
    ctx.dialog('lock-user', function (u) { u = u || D.user('u-05');
      E.ConfirmDialog({ id: 'sec-lock', title: 'Lock ' + u.fullName + '?', consequences: ['Open sessions end now; unsaved edits of this user are lost.', 'The account cannot sign in until someone unlocks it.', { text: 'Roles, grants and history stay as they are.', note: true }], verb: 'Lock account', icon: 'lock', onConfirm: function () { u.state = 'Locked'; reopenUser(S, u); E.Toast(u.name + ' locked', { action: { label: 'Undo', onClick: function () { u.state = 'Active'; reopenUser(S, u); } } }); } }); });
    ctx.dialog('simulate-user', function (u) { u = u || D.user('u-02');
      E.ConfirmDialog({ id: 'sec-sim', title: 'View ECR as ' + u.fullName + '?', danger: false, consequences: [{ text: 'You will see exactly the documents, menus and buttons this user sees.', note: true }, { text: 'Read-only: nothing you click is saved or sent on their behalf.', note: true }, { text: 'A banner stays on every screen until you press “Exit simulation”. The simulation is written to the audit trail.', note: true }], verb: 'Start simulation', icon: 'eye',
        onConfirm: function () { SIM.user = u; later(function () { E.go('/', { params: { as: (SEC.assign[u.id][0] || {}).role || 'Viewer', sim: u.id }, force: true }); }); } }); });
  }

  /* ================= PERIODS ================= */
  var PER = { project: D.project.id, cache: {} };
  function monthEnd(id) { var y = +id.slice(0, 4), m = +id.slice(5); return id + '-' + ('0' + new Date(y, m, 0).getDate()).slice(-2); }
  function periodsOf(pid) {
    if (pid === D.project.id) return D.periods; if (PER.cache[pid]) return PER.cache[pid];
    var i = D.projects.map(function (p) { return p.id; }).indexOf(pid), list = D.periods.map(function (p) { return Object.assign({}, p); });
    if (i % 4 === 1 || i === 10) { list[7].state = 'Grace'; list[7].closedBy = null; list[7].graceUntil = '2026-09-20'; list[7].note = 'grace until 20 Sep 2026 · edits are marked late'; }
    if (i % 5 === 2) { list[8].closes = '2026-09-20'; list[8].daysLeft = 2; list[8].note = 'closes in 2 days · 20 Sep 2026'; }
    if (i === 7) { list[8].closes = '2026-09-21'; list[8].daysLeft = 3; list[8].note = 'closes in 3 days · 21 Sep 2026'; }
    return (PER.cache[pid] = list);
  }
  function projName(pid) { return (D.projects.filter(function (p) { return p.id === pid; })[0] || {}).name || pid; }
  function deadline(p) { return p.closes || p.deadline || monthEnd(p.id); }
  function graceEnd(p) { return p.graceUntil || addDays(deadline(p), p.graceDays == null ? 5 : p.graceDays); }
  function docsOf(pid, p) { var d = D.documents.filter(function (x) { return x.project === pid; })[0]; if (!d || p.state === 'NotOpened') return []; if (p.id === D.currentPeriod) return [d]; return [Object.assign({}, d, { period: p.id, late: false, sheets: d.sheets.map(function (s) { return Object.assign({}, s, { state: 'Approved', filled: s.tables, issues: 0 }); }) })]; }
  function unsubmitted(pid, p) { var n = 0; docsOf(pid, p).forEach(function (d) { d.sheets.forEach(function (s) { if (s.state === 'Draft' || s.state === 'Returned' || s.state === 'Rejected') n++; }); }); return n; }
  function lateEdits(pid) { var d = D.documents.filter(function (x) { return x.project === pid; })[0]; return d && d.late ? 3 + (+d.id.slice(-2)) % 5 : 0; }
  function currentOf(pid) { var l = periodsOf(pid); return l.filter(function (p) { return p.state === 'Open'; })[0] || l.filter(function (p) { return p.state === 'Grace'; })[0] || l[8]; }
  function perNote(p) { if (p.state === 'Open') { var n = daysTo(deadline(p)); return p.reopened ? 'reopened until ' + F.date(p.reopened.until) + ' · edits are late' : 'closes ' + inDays(n) + ' · ' + F.date(deadline(p)); } if (p.state === 'Grace') return 'grace until ' + F.date(graceEnd(p)) + ' · edits are late'; if (p.state === 'NotOpened') return 'opens ' + F.date(p.opens); if (p.state === 'Archived') return 'read-only'; return (p.note || '').replace(/ · read-only$/, ''); }

  E.screen('/admin/periods', { title: 'Periods', group: 'Access', icon: 'calendar', order: 2, render: function (el, ctx) {
    if (ctx.query.project && projName(ctx.query.project) !== ctx.query.project) PER.project = ctx.query.project;
    var page = h('div', { class: 'page' }); el.appendChild(page);
    function nextToOpen() { return periodsOf(PER.project).filter(function (p) { return p.state === 'NotOpened'; })[0]; }
    var nx = nextToOpen();
    page.appendChild(E.PageHeader({ ctx: ctx, id: 'per', title: 'Periods', back: false, subtitle: 'A period is one reporting month of a project. While it is open, data can be entered; after the deadline it closes, and later it is archived. Click a month to see its dates, policy and documents.',
      primary: nx ? { label: 'Open ' + F.period(nx.id), icon: 'unlock', id: 'per-open-next', onClick: function () { ctx.openDialog('open-period'); } } : null,
      more: [{ label: 'Period access rules of the template', icon: 'ruler', href: '#/admin/templates/' + D.project.template + '/period-rules' }, { label: 'Export the calendar', icon: 'download', onClick: function () { E.Toast('Export started — periods-2026.xlsx', 'success'); } }] }));
    var res = slot(page, ctx, 'per');
    if (ctx.state !== 'data') { page.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'table', rows: 8, cols: 6 }, error: { code: 'ECR-PER-0503' }, empty: { icon: 'calendar', title: 'No period has been opened yet', text: 'Documents can be created only inside an open period. Open the first month — you choose the dates and what happens at the deadline.', actions: [{ label: 'Open ' + (nx ? F.period(nx.id) : 'a period'), variant: 'primary', onClick: function () { ctx.openDialog('open-period'); } }] } })); }
    var rows = D.projects.map(function (p) { var c = currentOf(p.id), g = periodsOf(p.id).filter(function (x) { return x.state === 'Grace'; })[0]; return { id: p.id, name: p.name, cur: c, grace: g, left: c.state === 'Open' ? daysTo(deadline(c)) : null, unsub: unsubmitted(p.id, c), late: lateEdits(p.id) }; });
    var stat = ctx.saved.stat || null, STATS = [{ id: 'open', label: 'open', match: function (r) { return r.cur.state === 'Open'; } }, { id: 'grace', label: 'in grace', tone: 'warning', match: function (r) { return !!r.grace; } }, { id: 'soon', label: 'closing in ≤ 3 days', tone: 'warning', match: function (r) { return r.left != null && r.left <= 3; } }, { id: 'late', label: 'with late edits', tone: 'warning', match: function (r) { return r.late > 0; } }];
    var yearHost = h('div', { class: 'ops-year', id: 'per-year', role: 'group', 'aria-label': 'Months of 2026' }), table = null, yearTitle = h('span');
    function paintYear() {
      yearHost.textContent = ''; yearTitle.textContent = projName(PER.project) + ' · 2026';
      periodsOf(PER.project).forEach(function (p) { var isNow = p.id === D.currentPeriod, un = unsubmitted(PER.project, p);
        yearHost.appendChild(h('button', { class: 'ops-month ' + p.state.toLowerCase() + (isNow ? ' now' : ''), type: 'button', id: 'per-m-' + p.id, 'aria-pressed': String(ctx.query.panel === p.id), title: F.period(p.id) + ' · ' + (p.state === 'NotOpened' ? 'Not opened' : p.state) + ' · ' + perNote(p), on: { click: function () { ctx.openPanel(p.id); } } },
          h('span', { class: 'm' }, E.MONTHS[+p.id.slice(5) - 1].slice(0, 3), isNow ? h('span', { class: 'muted t-xs' }, ' · now') : null), E.StatusBadge('period', p.state, { quiet: p.state !== 'Grace' }), h('span', { class: 'n' }, perNote(p)),
          p.state === 'Open' && !p.reopened ? E.Progress({ value: 18, max: 30, label: false, ariaLabel: 'Day 18 of 30' }) : null, (p.state === 'Open' || p.state === 'Grace') && un ? h('span', { class: 'n' }, F.plural(un, { one: 'sheet', other: 'sheets' }) + ' not submitted') : null)); });
    }
    function applyStat() { var s = STATS.filter(function (x) { return x.id === stat; })[0], list = s ? rows.filter(s.match) : rows; table.setRows(list, { filtered: list.length !== rows.length }); table.select(PER.project); }
    function pickProject(id) { PER.project = id; ctx.setQuery({ project: id === D.project.id ? null : id }); paintYear(); if (table) table.select(id); var s = E.$('#per-project'); if (s) s.value = id; }
    if (ctx.state === 'data') {
      var strip = E.StatStrip({ id: 'per-stat', active: stat, items: STATS.map(function (s) { return { id: s.id, label: s.label, tone: s.tone, value: rows.filter(s.match).length, hint: 'Filter the projects below: ' + s.label }; }), onSelect: function (s) { stat = ctx.saved.stat = s; applyStat(); } });
      page.appendChild(strip);
      page.appendChild(h('div', { class: 'ops-yearbar' }, E.Select({ id: 'per-project', label: 'Project', value: PER.project, options: D.projects.map(function (p) { return { value: p.id, label: p.name + ' · ' + p.id }; }), onChange: pickProject }), h('span', { class: 'ops-sp' }), h('span', { class: 'muted t-xs' }, 'Today is ' + F.date(NOW) + ' · deadlines are end of day, server time')));
      page.appendChild(E.Section({ id: 'per-year-sec', title: yearTitle, content: yearHost })); paintYear();
      table = E.DataTable({ id: 'per-tbl', auto: true, tall: true, state: 'data', rows: [], rowKey: 'id', memory: ctx.saved, selectedKey: PER.project, rowLabel: function (r) { return 'Show the calendar of ' + r.name; }, onRowClick: function (r) { pickProject(r.id); E.$('#per-year-sec').scrollIntoView({ block: 'nearest' }); }, onClearFilters: function () { stat = ctx.saved.stat = null; strip.setActive(null); applyStat(); },
        columns: [{ key: 'name', label: 'Project', render: function (r) { return E.twoLine(r.name, r.id); } }, { key: 'cur', label: 'Current period', sortable: false, render: function (r) { return F.period(r.cur.id); } }, { key: 'state', label: 'State', sortable: false, render: function (r) { return h('span', { class: 'row' }, E.StatusBadge('period', r.cur.state, { quiet: true }), r.grace ? E.StatusBadge('period', 'Grace', { label: F.period(r.grace.id).split(' ')[0] + ' in grace' }) : null); } },
          { key: 'left', label: 'Closes', render: function (r) { return r.left == null ? null : h('span', { class: r.left <= 3 ? 'warning-text t-b' : null }, F.date(deadline(r.cur)) + ' · ' + inDays(r.left)); } },
          { key: 'unsub', label: 'Not submitted', num: true, render: function (r) { return r.unsub ? F.plural(r.unsub, { one: 'sheet', other: 'sheets' }) : null; } }, { key: 'late', label: 'Late edits', num: true, render: function (r) { return r.late ? lateMark(String(r.late)) : null; } }] });
      page.appendChild(E.Section({ title: 'All projects · current period', hint: 'click a row to show its calendar above', content: table })); applyStat();
    }

    /* шторка періоду */
    ctx.panel('*', function (id) { var p = periodsOf(PER.project).filter(function (x) { return x.id === id; })[0]; if (p) openPeriod(p); });
    function openPeriod(p) {
      var pid = PER.project, docs = docsOf(pid, p), idx = ['NotOpened', 'Open', 'Grace', 'Closed', 'Archived'].indexOf(p.state), foot;
      if (p.state === 'NotOpened') foot = [{ label: 'Close', align: 'left' }, { label: 'Open period…', icon: 'unlock', variant: 'primary', keepOpen: true, id: 'per-d-open', onClick: function () { ctx.openDialog('open-period', p); } }];
      else if (p.state === 'Open' || p.state === 'Grace') foot = [{ label: 'Edit policy…', icon: 'sliders', align: 'left', keepOpen: true, id: 'per-d-policy', onClick: function () { ctx.openDialog('edit-policy', p); } }, { label: 'Close now…', icon: 'lock', keepOpen: true, id: 'per-d-close', onClick: function () { ctx.openDialog('close-period', p); } }];
      else if (p.state === 'Closed') foot = [{ label: 'Archive…', icon: 'archive', align: 'left', keepOpen: true, id: 'per-d-archive', onClick: function () { ctx.openDialog('archive-period', p); } }, { label: 'Reopen…', icon: 'unlock', keepOpen: true, id: 'per-d-reopen', onClick: function () { ctx.openDialog('reopen-period', p); } }];
      else foot = [{ label: 'Close' }];
      E.Drawer({ id: 'per-drawer', title: F.period(p.id), subtitle: projName(pid) + ' · ' + pid, badge: dbadge('period', p.state), footer: foot, onClose: function () { E.$$('.ops-month[aria-pressed="true"]').forEach(function (b) { b.setAttribute('aria-pressed', 'false'); }); },
        body: function (b) {
          if (p.reopened) b.appendChild(E.Banner({ tone: 'warning', title: 'Reopened until ' + F.date(p.reopened.until) + ', end of day', text: 'By ' + p.reopened.by + ' — “' + p.reopened.reason + '”. Every edit made now is marked late and shown in the audit trail.' }));
          else if (p.state === 'Grace') b.appendChild(E.Banner({ tone: 'warning', title: 'Grace period — closes ' + inDays(daysTo(graceEnd(p))), text: 'The deadline has passed. Data entry is still possible until ' + F.date(graceEnd(p)) + ', but every edit is marked late.' }));
          else if (p.state === 'Archived') b.appendChild(E.Banner({ icon: 'archive', title: 'Archived — read-only for good', text: 'Data was moved to archive storage. It can be read and exported, never edited; an archived period cannot be reopened.' }));
          else if (p.state === 'NotOpened') b.appendChild(E.Banner({ title: 'Not opened yet', text: 'Nobody can create documents for this month until a period administrator opens it.' }));
          b.appendChild(E.Stepper({ steps: ['Not opened', 'Open', 'Grace', 'Closed', 'Archived'], current: idx, labels: true, tone: p.state === 'Grace' ? 'warning' : null }));
          var un = unsubmitted(pid, p);
          b.appendChild(E.KeyValue([{ label: 'Opens', value: F.date(p.opens) }, { label: 'Entry deadline', value: F.date(deadline(p)), hint: p.state === 'Open' && !p.reopened ? inDays(daysTo(deadline(p))) + ' · end of day' : null }, { label: 'Grace until', value: (p.graceDays === 0 ? null : F.date(graceEnd(p))), hint: p.graceDays === 0 ? 'no grace period' : 'edits after the deadline are marked late' },
            p.state === 'Closed' || p.state === 'Archived' ? { label: 'Closed', value: (p.note || '').replace(/^closed /, '').replace(/ · read-only$/, '') || '—', hint: p.closedBy ? 'by ' + p.closedBy : 'automatically at the end of grace' } : null, p.state === 'Open' || p.state === 'Grace' ? { label: 'Not submitted', value: un ? F.plural(un, { one: 'sheet', other: 'sheets' }) : 'Nothing — all sheets are submitted' } : null]));
          b.appendChild(E.Section({ title: 'Policy', hint: 'what happens at the deadline', content: E.KeyValue([{ label: 'Auto-close', value: F.bool(p.auto !== false), hint: p.auto !== false ? 'closes by itself when grace ends' : 'stays open until someone closes it' }, { label: 'Grace length', value: F.plural(p.graceDays == null ? 5 : p.graceDays, { one: 'day', other: 'days' }) }, { label: 'Reminders', value: p.remind === false ? 'Off' : '3 days and 1 day before the deadline', hint: 'sent to owners of sheets that are not submitted' }]) }));
          b.appendChild(E.Section({ title: 'Documents of this period', hint: docs.length ? String(docs.length) : null, content: docs.length ? rowsBox(docs.map(function (d) { return rowItem([h('a', { href: '#/documents/' + d.id, class: 'mono' }, d.id), h('span', d.facility)], E.StatusBadge('sheet', D.docState(d), { quiet: true }), null, h('div', { class: 'ops-chain' }, E.SegmentBar({ segments: d.sheets.map(function (s) { return { label: s.name, state: s.state }; }) }), d.late ? lateMark('late edits') : null)); }))
            : h('div', { class: 'mini-empty' }, p.state === 'NotOpened' ? 'Documents can be created once the period is open.' : 'No documents were created in this period.') }));
        } });
      E.$$('.ops-month').forEach(function (m) { m.setAttribute('aria-pressed', String(m.id === 'per-m-' + p.id)); });
    }

    /* діалоги */
    function dflt(state) { var l = periodsOf(PER.project); return state === 'Closed' ? l.filter(function (p) { return p.state === 'Closed'; }).pop() : l.filter(function (p) { return p.state === state; })[0]; }
    ctx.dialog('open-period', function (p) { p = p || dflt('NotOpened') || periodsOf(PER.project)[9]; var pid = PER.project, data = { opens: p.opens, deadline: monthEnd(p.id), auto: true, grace: 5, remind: true, copy: true };
      wzJump(ctx, 2, E.Wizard({ id: 'per-open', title: 'Open ' + F.period(p.id) + ' · ' + projName(pid), data: data, applyLabel: 'Open period', steps: [
        { id: 'dates', label: 'Dates', hint: 'Data can be entered from the opening day until the end of the deadline day.', render: function (b) { b.appendChild(h('div', { class: 'form-grid' }, E.Input({ id: 'per-open-from', label: 'Opens', type: 'date', required: true, value: data.opens, onInput: function (v) { data.opens = v; } }), E.Input({ id: 'per-open-deadline', label: 'Data entry deadline', type: 'date', required: true, value: data.deadline, hint: 'Usually the last day of the month.', onInput: function (v) { data.deadline = v; } }))); },
          validate: function () { if (!data.opens || !data.deadline) return 'Set both dates.'; if (data.deadline <= data.opens) return 'The deadline must be later than the opening day.'; var prev = periodsOf(pid)[+p.id.slice(5) - 2]; if (prev && prev.state === 'NotOpened') return F.period(prev.id) + ' is not opened yet. Open months in order.'; return null; } },
        { id: 'policy', label: 'Policy', hint: 'What happens when the deadline comes. You can change this while the period is open.', render: function (b) {
            b.appendChild(E.Switch({ id: 'per-open-auto', label: 'Close automatically', hint: 'Without it the period stays open until someone presses “Close now”.', checked: data.auto, onChange: function (v) { data.auto = v; } }));
            b.appendChild(E.Select({ id: 'per-open-grace', label: 'Grace period after the deadline', value: String(data.grace), options: [{ value: '0', label: 'None — close at the deadline' }, { value: '3', label: '3 days' }, { value: '5', label: '5 days' }, { value: '10', label: '10 days' }], hint: 'During grace, data entry still works, but every edit is marked late.', onChange: function (v) { data.grace = +v; } }));
            b.appendChild(E.Checkbox({ id: 'per-open-remind', label: 'Remind owners of unsubmitted sheets', hint: '3 days and 1 day before the deadline', checked: data.remind, onChange: function (v) { data.remind = v; } }));
            b.appendChild(E.Checkbox({ id: 'per-open-copy', label: 'Create documents with the same structure as last month', hint: 'Structure only — no numbers are copied.', checked: data.copy, onChange: function (v) { data.copy = v; } })); } }],
        summary: function () { return E.KeyValue([{ label: 'Period', value: F.period(p.id) }, { label: 'Project', value: projName(pid) }, { label: 'Open for entry', value: F.date(data.opens) + ' → ' + F.date(data.deadline) }, { label: 'Then', value: data.grace ? F.plural(data.grace, { one: 'day', other: 'days' }) + ' of grace (edits marked late), until ' + F.date(addDays(data.deadline, data.grace)) : 'No grace period' }, { label: 'Closing', value: data.auto ? 'Automatic' : 'Manual — someone must press “Close now”' }, { label: 'Reminders', value: F.bool(data.remind) }, { label: 'Documents', value: data.copy ? 'Created from last month’s structure' : 'Created by users when needed' }]); },
        onApply: function (d, api) { p.state = 'Open'; p.opens = data.opens; p.closes = data.deadline; p.graceDays = data.grace; p.auto = data.auto; p.remind = data.remind; p.note = 'closes ' + F.date(data.deadline); api.close(); res.flash(function (c) { return resultOpened(c, p); }); } })); });
    function resultOpened(c, p) { return { title: F.period(p.id) + ' is open', text: 'Data entry is possible until ' + F.date(deadline(p)) + ', end of day' + (p.graceDays ? ', then ' + F.plural(p.graceDays, { one: 'day', other: 'days' }) + ' of grace' : '') + '. What next: create the documents or let users do it.', actions: [{ label: 'Go to documents', href: '#/' }, { label: 'See the period', onClick: function () { c.openPanel(p.id); } }] }; }
    ctx.dialog('result-opened', function () { res.show(resultOpened(ctx, periodsOf(PER.project)[9])); });

    ctx.dialog('close-period', function (p) { p = p || dflt('Open') || dflt('Grace') || periodsOf(PER.project)[8]; var pid = PER.project, un = unsubmitted(pid, p), editors = D.users.filter(function (u) { return u.roles.indexOf('DataEntry') >= 0 && u.state === 'Active'; }).length;
      E.ConfirmDialog({ id: 'per-close', title: 'Close ' + F.period(p.id) + ' now?', text: projName(pid) + ' · the deadline is ' + F.date(deadline(p)) + ' (' + inDays(daysTo(deadline(p))) + ').',
        consequences: [un ? F.plural(un, { one: 'sheet is', other: 'sheets are' }) + ' not submitted — they stay as drafts and cannot be submitted until the period is reopened.' : { text: 'All sheets are submitted — nothing is left behind.', note: true }, 'Data entry becomes read-only for ' + F.plural(editors, { one: 'user', other: 'users' }) + ' right away; the grace period is skipped.', { text: 'Approvers can still approve or return what was submitted.', note: true }, { text: 'A period administrator can reopen it later, with a reason.', note: true }], verb: 'Close period', icon: 'lock',
        onConfirm: function () { var old = Object.assign({}, p); p.state = 'Closed'; p.closes = null; p.reopened = null; p.closedBy = ctx.me.name; p.note = 'closed ' + F.date(NOW) + ' · read-only'; p.unsubAtClose = un;
          res.flash(function (c) { return resultClosed(c, p, function () { Object.assign(p, old); delete FLASH.per; c.refresh(); E.Toast(F.period(p.id) + ' is open again'); }); }); } }); });
    function resultClosed(c, p, undo) { var un = p.unsubAtClose == null ? 3 : p.unsubAtClose, nx2 = nextToOpen(); return { title: F.period(p.id) + ' is closed', text: (un ? F.plural(un, { one: 'sheet was', other: 'sheets were' }) + ' left unsubmitted and are read-only now. ' : 'Everything was submitted. ') + 'What next: open the following month so data entry can continue.',
      actions: [nx2 ? { label: 'Open ' + F.period(nx2.id) + '…', icon: 'unlock', onClick: function () { c.openDialog('open-period'); } } : null, un ? { label: 'See unsubmitted sheets', href: '#/' } : null, undo ? { label: 'Undo', icon: 'undo', onClick: undo } : null].filter(Boolean) }; }
    ctx.dialog('result-closed', function () { res.show(resultClosed(ctx, periodsOf(PER.project)[8])); });

    ctx.dialog('reopen-period', function (p) { p = p || dflt('Closed') || periodsOf(PER.project)[7]; var mode = 'today', until = E.Input({ id: 'per-reopen-until', label: 'Reopen until', type: 'date', value: addDays(NOW, 3), hint: 'End of that day. Keep it short — every extra day is a day of late edits.' }); until.hidden = true;
      var extra = h('div', { class: 'stack gap-3' }, h('div', { class: 'field ops-segf' }, h('span', { class: 'lbl' }, 'For how long'), E.Segmented({ id: 'per-reopen-mode', label: 'For how long', value: mode, options: [{ value: 'today', label: 'Until the end of today' }, { value: 'date', label: 'Until a date' }], onChange: function (v) { mode = v; until.hidden = v !== 'date'; } })), until,
        E.Banner({ tone: 'warning', title: 'Edits will be marked late', text: 'Everything entered while the period is reopened carries a “Late” mark in the document and in the audit trail. If a report was already submitted, submitting again creates a new snapshot and the old one becomes superseded.' }));
      E.ReasonDialog({ id: 'per-reopen', title: 'Reopen ' + F.period(p.id) + '?', text: projName(PER.project) + ' · closed ' + ((p.note || '').replace(/^closed /, '').replace(/ · read-only$/, '') || '') + (p.closedBy ? ' by ' + p.closedBy : '') + '.', label: 'Reason for reopening', placeholder: 'e.g. Laboratory results for GT-4 arrived after the deadline', hint: 'At least 10 characters. Shown to everyone who opens the period and kept in the audit trail.', extra: extra, verb: 'Reopen period', icon: 'unlock',
        onSubmit: function (reason) { var u = mode === 'today' ? NOW.slice(0, 10) : until.value; p.state = 'Open'; p.closes = u; p.reopened = { until: u, reason: reason, by: ctx.me.name }; res.flash(function (c) { return resultReopened(c, p); }); } }); });
    function resultReopened(c, p) { var r = p.reopened || { until: NOW.slice(0, 10) }; return { tone: 'warning', title: F.period(p.id) + ' is reopened until ' + F.date(r.until) + ', end of day', text: 'It closes again by itself. Edits made now are marked late. What next: tell the people who need to fix their data.', actions: [{ label: 'See the period', onClick: function () { c.openPanel(p.id); } }, { label: 'Late edits in the audit trail', href: '#/admin/audit' }] }; }
    ctx.dialog('result-reopened', function () { res.show(resultReopened(ctx, periodsOf(PER.project)[7])); });

    ctx.dialog('archive-period', function (p) { p = p || dflt2(); E.ConfirmDialog({ id: 'per-arch1', title: 'Archive ' + F.period(p.id) + '?', danger: false, text: 'Step 1 of 2 · ' + projName(PER.project), consequences: ['The period can never be reopened or edited again.', 'Its data moves to archive storage; opening old documents becomes slower.', { text: 'Reading, exporting and report snapshots keep working.', note: true }, { text: 'Runs as a background job — usually a few minutes.', note: true }], verb: 'Continue', icon: 'arrowR', onConfirm: function () { later(function () { ctx.openDialog('archive-period-confirm', p); }); } }); });
    function dflt2() { return periodsOf(PER.project).filter(function (p) { return p.state === 'Closed'; })[0] || periodsOf(PER.project)[6]; }
    ctx.dialog('archive-period-confirm', function (p) { p = p || dflt2(); E.ConfirmDialog({ id: 'per-arch2', title: 'Archive ' + F.period(p.id) + ' — final confirmation', text: 'Step 2 of 2 · This cannot be undone. Type the name of the period to make sure it is the right one.', typeToConfirm: F.period(p.id), verb: 'Archive period', icon: 'archive',
      onConfirm: function () { p.state = 'Archived'; p.note = 'archived · read-only'; E.tasks.start({ title: 'Period archive · ' + F.period(p.id), icon: 'archive', detail: projName(PER.project), duration: 6000, doneDetail: 'Archived — data moved to archive storage' }); res.flash(function (c) { return resultArchived(c, p); }); } }); });
    function resultArchived(c, p) { return { title: F.period(p.id) + ' is being archived', text: 'The job continues in the background — you can leave this page. When it finishes, the month is read-only for good.', actions: [{ label: 'Open My tasks', onClick: function () { E.tasks.open(); } }, { label: 'All jobs', href: '#/admin/jobs' }] }; }
    ctx.dialog('result-archived', function () { res.show(resultArchived(ctx, periodsOf(PER.project)[6])); });

    ctx.dialog('edit-policy', function (p) { p = p || dflt('Open') || periodsOf(PER.project)[8]; var auto = p.auto !== false, grace = p.graceDays == null ? 5 : p.graceDays, remind = p.remind !== false;
      E.Dialog({ id: 'per-policy', title: 'Policy of ' + F.period(p.id), body: h('div', { class: 'stack gap-3' }, E.Switch({ id: 'per-pol-auto', label: 'Close automatically', hint: 'When the grace period ends.', checked: auto, onChange: function (v) { auto = v; } }), E.Select({ id: 'per-pol-grace', label: 'Grace period after the deadline', value: String(grace), options: [{ value: '0', label: 'None' }, { value: '3', label: '3 days' }, { value: '5', label: '5 days' }, { value: '10', label: '10 days' }], onChange: function (v) { grace = +v; } }), E.Checkbox({ id: 'per-pol-remind', label: 'Remind owners of unsubmitted sheets', checked: remind, onChange: function (v) { remind = v; } })),
        footer: [{ label: 'Cancel' }, { label: 'Save policy', variant: 'primary', onClick: function () { p.auto = auto; p.graceDays = grace; p.remind = remind; E.Toast('Policy saved — grace until ' + F.date(graceEnd(p)), 'success'); later(function () { E.closeAllLayers(); ctx.openPanel(p.id); }); } }] }); });
    ctx.action('Open the next period', 'unlock', function () { ctx.openDialog('open-period'); });
    ctx.action('Close the current period', 'lock', function () { ctx.openDialog('close-period'); });
  } });

  /* ================= JOBS ================= */
  var JOB_STEPS = { 'Excel export': ['Collect sheets and tables', 'Build the workbook', 'Write the file', 'Register the download'], 'Excel import': ['Read the workbook', 'Match cells to the template', 'Validate values', 'Apply changes'], 'Formula recalculation': ['Load inputs', 'Evaluate expressions', 'Write calculated cells', 'Run validation rules'],
    'Period archive': ['Lock the period', 'Move data to archive storage', 'Rebuild indexes', 'Verify row counts'], 'Report snapshot': ['Freeze the numbers', 'Render the report', 'Compute the checksum', 'Store the snapshot'], 'Source collection': ['Connect to the source', 'Read tags', 'Convert units', 'Write values'], 'Consistency check': ['Load documents of the period', 'Compare linked tables', 'Check registry references', 'Save findings'] };
  var JOB_FIX = { 'ECR-CALC-0500': 'Enter the run hours of Gas turbine GT-4 (or mark the source as out of service for the month), then retry. Nothing was written: calculated cells keep their previous values.', 'ECR-SRC-0408': 'Check that the PI Web API server is reachable from the application server, then retry. The next scheduled collection is tomorrow at 05:30.' };
  var JOBS_LIVE = { list: null };
  D.jobs.forEach(function (j) { if (j.state === 'Failed') j.attempts = 3; });
  function jobDoc(j) { var m = /DOC-\d+/.exec(j.target); return m ? m[0] : null; }
  function jobStarted(j) { return j.started ? '2026-09-18T' + j.started : null; }

  E.screen('/admin/jobs', { title: 'Jobs', group: 'Operate', icon: 'stack', order: 1, render: function (el, ctx) {
    var rows = ctx.state === 'empty' ? [] : D.jobs, me = ctx.me.name, res, list;
    var bannerHost = h('div', { class: 'ops-slot' });
    list = E.ListPage(el, ctx, { id: 'jobs', rows: rows, banner: bannerHost,
      header: { title: 'Jobs', back: false, subtitle: 'Exports, imports, recalculations and nightly checks run in the background. A failed job says why, what was changed (usually nothing) and can be retried.',
        secondary: [{ label: 'Refresh', icon: 'refresh', id: 'jobs-refresh', onClick: function () { ctx.refresh(); E.Toast('Updated ' + F.time(NOW)); } }], more: [{ label: 'Purge finished jobs…', icon: 'trash', danger: true, onClick: function () { ctx.openDialog('purge-jobs'); } }] },
      stats: [{ id: 'running', label: 'running', match: function (j) { return j.state === 'Running'; } }, { id: 'queued', label: 'queued', match: function (j) { return j.state === 'Queued'; } }, { id: 'failed', label: 'failed in 24 h', tone: 'danger', match: function (j) { return j.state === 'Failed'; } }, { id: 'latency', label: 'avg start latency', value: '1.4 s', hint: 'Time between “queued” and “running”, last 24 hours' }],
      search: { placeholder: 'Job, document or person', text: function (j) { return j.id + ' ' + j.type + ' ' + j.target + ' ' + j.by; } },
      filters: [{ id: 'type', label: 'Types', options: Object.keys(JOB_STEPS) }, { id: 'state', label: 'States', options: [{ value: 'Failed', label: 'Failed only' }, 'Running', 'Queued', 'Done', 'Cancelled'] }, { id: 'owner', label: 'Owners', all: 'Started by anyone', options: [{ value: 'mine', label: 'Mine' }, { value: 'system', label: 'System (scheduled)' }], match: function (j, v) { return v === 'mine' ? j.by === me : j.by === 'System'; } }],
      table: { rowKey: 'id', pageSize: 25, tall: true, rowLabel: function (j) { return 'Open ' + j.type + ' ' + j.id; }, columns: [
        { key: 'type', label: 'Job', render: function (j) { return E.twoLine(j.type, j.target, { mono: false }); } },
        { key: 'by', label: 'Started by', hideSm: true },
        { key: 'started', label: 'Started', mono: true, render: function (j) { return j.started || h('span', { class: 'muted' }, 'waiting'); } },
        { key: 'duration', label: 'Duration', num: true, hideSm: true, render: function (j) { return j.duration == null ? null : F.duration(j.duration); } },
        { key: 'progress', label: 'Progress', sortable: false, hideSm: true, render: function (j) { return j.state === 'Running' ? E.Progress({ value: j.progress, tone: 'accent', ariaLabel: j.type + ' progress' }) : j.state === 'Failed' || j.state === 'Cancelled' ? h('span', { class: 'muted' }, 'stopped at ' + j.progress + ' %') : null; } },
        { key: 'state', label: 'State', render: function (j) { return E.StatusBadge('job', j.state, { quiet: j.state === 'Done' || j.state === 'Queued' }); } }] },
      empty: { icon: 'stack', title: 'Nothing is running', text: 'No background jobs in the last 24 hours. Exports, imports and recalculations appear here as soon as someone starts them; nightly checks run at 02:00.', actions: [{ label: 'Open documents', href: '#/' }] },
      error: { code: 'ECR-JOB-0503' },
      drawer: function (j) { var steps = JOB_STEPS[j.type] || ['Run'], at = Math.min(steps.length - 1, Math.floor(j.progress / 100 * steps.length)), doc = jobDoc(j), foot = [];
        if (doc) foot.push({ label: 'Open document', icon: 'file', align: 'left', onClick: function () { E.go('/documents/' + doc); } });
        if (j.state === 'Failed') foot.push({ label: 'Retry', icon: 'refresh', variant: 'primary', id: 'jobs-retry', keepOpen: true, onClick: function () { askRetry(j); } });
        else if (j.state === 'Running' || j.state === 'Queued') foot.push({ label: 'Cancel job…', icon: 'stop', id: 'jobs-cancel', keepOpen: true, onClick: function () { ctx.openDialog('cancel-job', j); } });
        else if (j.state === 'Cancelled') foot.push({ label: 'Start again', icon: 'play', id: 'jobs-again', onClick: function () { doRetry(j); } });
        else foot.push({ label: 'Close' });
        return { title: j.type, subtitle: j.id + ' · ' + j.target, badge: dbadge('job', j.state), footer: foot, body: function (b) {
          if (j.error) b.appendChild(E.Banner({ tone: 'danger', title: 'Why it failed', text: j.error }));
          if (j.error) b.appendChild(E.Section({ title: 'What to do', content: para(JOB_FIX[j.code] || 'Retry. If it fails again, send the correlation ID to support.') }));
          if (j.result) b.appendChild(E.Banner({ icon: 'checkCircle', title: 'Result', text: j.result }));
          if (j.state === 'Running') b.appendChild(E.Progress({ value: j.progress, tone: 'accent', ariaLabel: 'Progress' }));
          b.appendChild(E.Section({ title: 'Steps', content: timeline(steps.map(function (s, i) { var done = j.state === 'Done' || i < at, cur = !done && i === at && j.state !== 'Queued'; return { title: s, dot: done ? 'filled' : cur && j.state === 'Failed' ? 'error' : 'empty', cls: cur && j.state === 'Running' ? 'cur' : null, right: cur ? h('span', { class: j.state === 'Failed' ? 'danger-text t-xs' : 'muted t-xs' }, j.state === 'Failed' ? 'failed here' : j.state === 'Cancelled' ? 'cancelled here' : 'in progress') : null }; })) }));
          b.appendChild(E.KeyValue([{ label: 'Started by', value: j.by }, { label: 'Started', value: j.started ? F.dateTime(jobStarted(j)) : 'Waiting in the queue' }, { label: 'Duration', value: j.duration == null ? null : F.duration(j.duration), mono: true }, j.code ? { label: 'Error code', value: withCopy(j.code, 'error code') } : null, j.correlationId ? { label: 'Correlation ID', value: withCopy(j.correlationId, 'correlation ID'), hint: 'include it when contacting support' } : null, doc ? { label: 'Document', value: h('a', { href: '#/documents/' + doc, class: 'mono' }, doc) } : null]));
          if (j.state === 'Failed') b.appendChild(E.Section({ title: 'Attempts', hint: '3 of 3 · automatic retries are used up', content: timeline([['1', j.started + ':02', 'failed after ' + j.duration + ' s', null], ['2', j.started + ':44', 'failed after ' + j.duration + ' s', 'after a 30 s pause'], ['3', j.started.slice(0, 3) + ('0' + (+j.started.slice(3) + 3)).slice(-2) + ':01', 'failed after ' + j.duration + ' s', 'after a 2 min pause']].map(function (a) { return { title: 'Attempt ' + a[0], dot: 'error', right: h('span', { class: 'mono t-xs muted' }, a[1]), meta: a[2] + (a[3] ? ' · ' + a[3] : '') + ' · same error' }; })) }));
        } }; } });
    JOBS_LIVE.list = list; ctx.onLeave(function () { JOBS_LIVE.list = null; });
    res = { show: function (o) { bannerHost.textContent = ''; if (o) bannerHost.appendChild(E.ResultBanner(Object.assign({}, o, { onDismiss: function () { delete FLASH.jobs; } }))); } };
    if (FLASH.jobs) res.show(FLASH.jobs.make(ctx));
    ctx.onLeave(function () { var x = FLASH.jobs; if (x) { if (x.keep) x.keep = false; else delete FLASH.jobs; } });
    function flash(make) { FLASH.jobs = { make: make, keep: true }; ctx.refresh(); }
    function retried(c, j) { return { title: j.type + ' restarted', text: j.target + ' · attempt 1 of 3 is running now. You can leave this page — the result also appears in My tasks.', actions: [{ label: 'Open the job', onClick: function () { c.openPanel(j.id); } }, { label: 'Open My tasks', onClick: function () { E.tasks.open(); } }] }; }
    function askRetry(j) { var a = E.$('#jobs-retry'); if (!a) return doRetry(j); E.InlineConfirm(a, { text: 'Run “' + j.type + '” for ' + j.target + ' again?', verb: 'Retry', onConfirm: function () { doRetry(j); } }); }
    function doRetry(j) { var tick = -1; j.state = 'Running'; j.progress = 0; j.lastError = j.error; j.error = null; j.result = null; j.started = F.time(NOW); j.duration = 0; j.attempts = 1; j.by = me;
      E.tasks.start({ title: j.type + ' · ' + j.target, icon: 'refresh', detail: 'Retry · attempt 1 of 3', duration: 7000, doneDetail: 'Finished — no errors', onProgress: function (p) { j.progress = Math.round(p); j.duration = Math.round(p / 100 * 14); var t = Math.floor(p / 10); if (t !== tick) { tick = t; if (JOBS_LIVE.list && !E._layers.length) JOBS_LIVE.list.refresh(); } }, onDone: function () { j.state = 'Done'; j.progress = 100; j.result = 'Finished on retry — no errors'; if (JOBS_LIVE.list && !E._layers.length) JOBS_LIVE.list.refresh(); E.Toast(j.type + ' finished', 'success'); } });
      ctx.setQuery({ dialog: null, panel: null }); flash(function (c) { return retried(c, j); }); }
    ctx.dialog('retry-job', function (j) { j = j || D.jobs.filter(function (x) { return x.state === 'Failed'; })[0] || D.jobs[1]; if (j.state !== 'Failed') { j.state = 'Failed'; j.error = j.lastError || j.error; } if (!E.$('#jobs-drawer')) list.openRow(j); later(function () { askRetry(j); });
      /* набір закриває спливні вікна на resize (це робить і headless-знімок) — один раз відкриваємо знову, доки діалог в адресі */
      var tries = 0;
      function again() { if (++tries >= 5) window.removeEventListener('resize', again); later(function () { if (ctx.query.dialog === 'retry-job' && E.$('#jobs-retry') && !E.$('.inline-confirm')) askRetry(j); }); }
      window.addEventListener('resize', again); ctx.onLeave(function () { window.removeEventListener('resize', again); }); });
    ctx.dialog('result-retried', function () { res.show(retried(ctx, D.jobs[1])); });
    ctx.dialog('cancel-job', function (j) { j = j || D.jobs.filter(function (x) { return x.state === 'Running'; })[0] || D.jobs[0];
      E.ConfirmDialog({ id: 'jobs-cancel-dlg', title: 'Cancel “' + j.type + '” for ' + j.target + '?', consequences: [j.state === 'Queued' ? 'The job is removed from the queue before it starts.' : 'The job stops at ' + j.progress + ' %; the partial result is deleted.', { text: /import|recalculation/i.test(j.type) ? 'Changes are applied only at the last step, so the document stays exactly as it was.' : 'Nothing in the document changes.', note: true }, { text: 'You can start it again at any time.', note: true }], verb: 'Cancel job', cancelLabel: 'Keep running', icon: 'stop',
        onConfirm: function () { j.state = 'Cancelled'; ctx.setQuery({ panel: null }); later(function () { ctx.refresh(); E.Toast(j.type + ' cancelled · nothing was changed', { action: { label: 'Start again', onClick: function () { doRetry(j); } } }); }); } }); });
    ctx.dialog('purge-jobs', function () { var n = D.jobs.filter(function (j) { return j.state === 'Done' || j.state === 'Cancelled'; }).length;
      E.ConfirmDialog({ id: 'jobs-purge', title: 'Purge ' + F.plural(n, { one: 'finished job', other: 'finished jobs' }) + '?', consequences: ['Done and cancelled jobs disappear from this list, together with their step logs.', { text: 'Failed, running and queued jobs stay. Files that were exported are not touched.', note: true }], verb: 'Purge finished jobs', icon: 'trash',
        onConfirm: function () { var old = D.jobs.slice(); D.jobs = D.jobs.filter(function (j) { return j.state !== 'Done' && j.state !== 'Cancelled'; }); later(function () { ctx.refresh(); E.Toast(n + ' finished jobs purged', { action: { label: 'Undo', onClick: function () { D.jobs = old; if (JOBS_LIVE.list) JOBS_LIVE.list.setRows(D.jobs); } } }); }); } }); });
  } });

  /* ================= REPORT SNAPSHOTS ================= */
  var SNAP = null;
  function snaps() { if (SNAP) return SNAP;
    SNAP = D.snapshots.map(function (s) { return Object.assign({ version: 1, superseded: null, supersedes: null }, s); });
    var aug = SNAP.filter(function (s) { return s.id === 'SN-0030'; })[0]; if (aug) { aug.version = 2; aug.supersedes = 'SN-0026'; SNAP.push({ id: 'SN-0026', name: aug.name, period: aug.period, by: aug.by, created: '2026-09-03T17:10', size: 1204876, scope: aug.scope, hash: 'sha256:' + Math.round(E.rng(26)() * 1e16).toString(16), version: 1, superseded: 'SN-0030', supersedes: null }); }
    return SNAP; }
  function snapPeriod(s) { return /^\d{4}-\d{2}$/.test(s.period) ? F.period(s.period) : s.period; }
  function snapDiff(s) { var R = E.rng(+s.id.slice(3) * 7), prev = s.supersedes ? 'version 1 (' + s.supersedes + ')' : 'the previous report of this kind';
    return { prev: prev, rows: [['SO₂, t', 412.5531], ['NOₓ, t', 288.1042], ['CO, t', 196.77], ['CH₄, t', 1885.4164], ['Fuel gas, 10³ m³', 48210.5], ['Water discharge, 10³ m³', 1204.2]].map(function (a, i) { var ch = s.supersedes ? i === 1 || i === 3 : R() > 0.2, was = ch ? +(a[1] * (0.93 + R() * 0.1)).toFixed(4) : a[1]; return { name: a[0], was: was, now: a[1], changed: ch }; }) }; }

  E.screen('/admin/snapshots', { title: 'Report snapshots', group: 'Operate', icon: 'camera', order: 2, render: function (el, ctx) {
    var bannerHost = h('div', { class: 'ops-slot' }), month = NOW.slice(0, 7);
    function show(o) { bannerHost.textContent = ''; if (o) bannerHost.appendChild(E.ResultBanner(Object.assign({}, o, { onDismiss: function () { delete FLASH.snap; } }))); }
    var list = E.ListPage(el, ctx, { id: 'snap', rows: ctx.state === 'empty' ? [] : snaps(), banner: bannerHost,
      header: { title: 'Report snapshots', back: false, subtitle: 'A snapshot is the exact copy of a report at the moment it was submitted. It never changes — even if the documents are edited later.', primary: { label: 'New snapshot', icon: 'camera', id: 'snap-new', onClick: function () { ctx.openDialog('new-snapshot'); } } },
      stats: [{ id: 'current', label: 'current', match: function (s) { return !s.superseded; } }, { id: 'month', label: 'made this month', match: function (s) { return s.created.slice(0, 7) === month; } }, { id: 'superseded', label: 'superseded', match: function (s) { return !!s.superseded; } }, { id: 'size', label: 'stored', value: function (r) { return F.bytes(r.reduce(function (a, s) { return a + s.size; }, 0)); } }],
      search: { placeholder: 'Report, period or person', text: function (s) { return s.id + ' ' + s.name + ' ' + s.period + ' ' + s.by; } },
      filters: [{ id: 'by', label: 'People', all: 'Submitted by anyone', options: ['A. Iskakov', 'O. Kovalenko'] }],
      table: { rowKey: 'id', tall: true, sort: { key: 'created', dir: 'desc' }, rowLabel: function (s) { return 'Open snapshot ' + s.id; }, columns: [
        { key: 'name', label: 'Report', render: function (s) { return E.twoLine(s.name, s.id, { mono: false }); } }, { key: 'period', label: 'Period', render: snapPeriod }, { key: 'scope', label: 'Documents', hideSm: true },
        { key: 'version', label: 'Version', render: function (s) { return h('span', { class: 'row' }, h('span', { class: 'mono' }, 'v' + s.version), s.superseded ? tag('history', 'superseded', 'Replaced by ' + s.superseded + ' after the period was reopened') : null); } },
        { key: 'by', label: 'Submitted by', hideSm: true }, { key: 'created', label: 'Submitted', render: function (s) { return F.dateTime(s.created); } }] },
      empty: { icon: 'camera', title: 'No snapshots yet', text: 'A snapshot is made automatically when a report is submitted, or by hand from here. It is the version you can show an inspector a year later.', actions: [{ label: 'New snapshot', variant: 'primary', onClick: function () { ctx.openDialog('new-snapshot'); } }] },
      error: { code: 'ECR-SNP-0503' },
      drawer: function (s) { var df = snapDiff(s), changed = df.rows.filter(function (r) { return r.changed; });
        return { title: s.name, subtitle: s.id + ' · ' + snapPeriod(s) + ' · v' + s.version, wide: true, footer: [{ label: 'Verify checksum', icon: 'checkCircle', align: 'left', keepOpen: true, id: 'snap-verify', onClick: function () { E.Toast('Checksum matches — the snapshot has not been altered', 'success'); } }, { label: 'Download', icon: 'download', id: 'snap-download', keepOpen: true, onClick: function () { E.Toast('Download started — ' + s.id + '.zip · ' + F.bytes(s.size), 'success'); } }],
          body: function (b) {
            if (s.superseded) b.appendChild(E.Banner({ tone: 'warning', icon: 'history', title: 'Superseded by ' + s.superseded, text: 'The period was reopened and the report was submitted again. This version is kept for the record; the current one is ' + s.superseded + '.', actions: [{ label: 'Open current version', onClick: function () { E.closeAllLayers(); later(function () { ctx.openPanel(s.superseded); }); } }] }));
            else b.appendChild(E.Banner({ icon: 'lock', title: 'Immutable', text: 'These are the numbers exactly as submitted. Later edits of the documents never change a snapshot; if the period is reopened and the report is submitted again, a new version appears and this one is marked superseded.' }));
            b.appendChild(E.KeyValue([{ label: 'Report', value: s.name }, { label: 'Period', value: snapPeriod(s) }, { label: 'Documents', value: s.scope }, { label: 'Version', value: 'v' + s.version + (s.supersedes ? ' · replaces ' + s.supersedes : '') }, { label: 'Submitted by', value: s.by }, { label: 'Submitted', value: F.dateTime(s.created) }, { label: 'Size', value: F.bytes(s.size), mono: true }]));
            b.appendChild(E.Section({ title: 'Contents', content: rowsBox([['file', 'report.xlsx', 'The report as the recipient sees it'], ['database', 'data.json', 'Every cell value with its unit, methodology version and rounding'], ['history', 'audit-extract.json', 'Who entered, submitted and approved each sheet'], ['tag', 'manifest.json', 'File list with checksums, template and calculation versions']].map(function (f) { return rowItem([icon(f[0], 14), h('b', { class: 'mono' }, f[1])], null, f[2]); })) }));
            b.appendChild(E.Section({ title: 'Checksum', hint: 'SHA-256 of the whole package', content: E.CodeText(s.hash, { block: true }) }));
            b.appendChild(E.Section({ title: 'Compared with ' + df.prev, hint: changed.length ? F.plural(changed.length, { one: 'indicator differs', other: 'indicators differ' }) : 'no differences', content: h('div', { class: 'table-wrap' }, h('table', { class: 'data' }, h('thead', h('tr', h('th', 'Indicator'), h('th', { class: 'num' }, 'Was'), h('th', { class: 'num' }, 'Now'), h('th', { class: 'num' }, 'Change'))),
              h('tbody', df.rows.map(function (r) { var d = r.now - r.was; return h('tr', h('td', r.name), h('td', { class: 'num' + (r.changed ? '' : ' muted') }, F.number(r.was)), h('td', { class: 'num' }, r.changed ? h('b', { class: 't-b' }, F.number(r.now)) : F.number(r.now)), h('td', { class: 'num' }, r.changed ? (d > 0 ? '+' : '') + F.number(d) : h('span', { class: 'faint' }, '—'))); })))) }));
          } }; } });
    if (FLASH.snap) show(FLASH.snap.make(ctx));
    ctx.onLeave(function () { var x = FLASH.snap; if (x) { if (x.keep) x.keep = false; else delete FLASH.snap; } });
    function made(c, s) { return { title: 'Snapshot ' + s.id + ' is being made', text: s.name + ' · ' + snapPeriod(s) + '. It appears in the list when the job finishes; the checksum is computed at the end.', actions: [{ label: 'Open My tasks', onClick: function () { E.tasks.open(); } }, { label: 'Open the snapshot', onClick: function () { c.openPanel(s.id); } }] }; }
    ctx.dialog('new-snapshot', function () { var data = { report: 'Monthly summary', period: D.currentPeriod };
      wzJump(ctx, 2, E.Wizard({ id: 'snap-wz', title: 'New snapshot', data: data, applyLabel: 'Make snapshot', steps: [
        { id: 'what', label: 'Report', render: function (b) { b.appendChild(E.Select({ id: 'snap-wz-report', label: 'Report', value: data.report, options: ['Monthly summary', 'Annual air emissions report', 'Quarterly water report'], onChange: function (v) { data.report = v; } })); b.appendChild(E.Select({ id: 'snap-wz-period', label: 'Period', value: data.period, options: D.periods.filter(function (p) { return p.state !== 'NotOpened'; }).map(function (p) { return { value: p.id, label: F.period(p.id) + ' · ' + p.state }; }), onChange: function (v) { data.period = v; } })); } },
        { id: 'check', label: 'Readiness', render: function (b) { var c = D.campaign(), open = data.period === D.currentPeriod, notAppr = open ? c.sheets - c.approved : 0;
            b.appendChild(E.KeyValue([{ label: 'Documents', value: String(c.documents) }, { label: 'Approved sheets', value: (open ? c.approved : c.sheets) + ' of ' + c.sheets }, { label: 'Validation errors', value: String(open ? c.issues : 0) }]));
            b.appendChild(notAppr ? E.Banner({ tone: 'warning', title: F.plural(notAppr, { one: 'sheet is', other: 'sheets are' }) + ' not approved yet', text: 'The snapshot will freeze them as they are now and mark them “not approved”. Usually you wait until everything is approved.' }) : E.Banner({ icon: 'checkCircle', title: 'Everything is approved', text: 'The snapshot will contain final numbers.' })); } }],
        summary: function () { var ex = snaps().filter(function (s) { return s.period === data.period && s.name.indexOf(data.report) === 0 && !s.superseded; })[0]; return h('div', { class: 'stack gap-3' }, E.KeyValue([{ label: 'Report', value: data.report }, { label: 'Period', value: F.period(data.period) }, { label: 'Version', value: ex ? 'v' + (ex.version + 1) + ' — ' + ex.id + ' becomes superseded' : 'v1' }]), h('p', { class: 'muted' }, 'A snapshot cannot be edited or deleted afterwards.')); },
        onApply: function (d, api) { var ex = snaps().filter(function (s) { return s.period === data.period && s.name.indexOf(data.report) === 0 && !s.superseded; })[0], id = 'SN-00' + (32 + snaps().length - 6), s = { id: id, name: data.report + ' — ' + F.period(data.period), period: data.period, by: ctx.me.name, created: NOW.slice(0, 16), size: 1215000, scope: '12 documents', hash: 'sha256:' + Math.round(E.rng(snaps().length)() * 1e16).toString(16), version: ex ? ex.version + 1 : 1, supersedes: ex ? ex.id : null, superseded: null };
          if (ex) ex.superseded = id; snaps().unshift(s); api.close(); E.tasks.start({ title: 'Report snapshot · ' + s.name, icon: 'camera', detail: id, duration: 6000, doneDetail: 'Stored · checksum computed' }); FLASH.snap = { make: function (c) { return made(c, s); }, keep: true }; ctx.refresh(); } })); });
    ctx.dialog('result-snapshot', function () { show(made(ctx, snaps()[0])); });
  } });

  /* ================= AUDIT TRAIL ================= */
  var CHG = null, RANGE = { from: '2026-09-01', to: '2026-09-18' };
  var ORIGIN_LABEL = { UserEdit: 'Typed by a user', Import: 'Excel import', Recalculation: 'Recalculation', Migration: 'Migration' }, ORIGIN_ICON = { UserEdit: 'pencil', Import: 'upload', Recalculation: 'sigma', Migration: 'database' };
  var AIR_COLS = ['Fuel gas', 'Hours', 'SO₂', 'NOₓ', 'CO', 'VOC', 'PM₁₀', 'CH₄'], GEN_COLS = ['Volume', 'Hours', 'COD', 'BOD₅', 'TSS', 'Oil products'];
  function changes() {
    if (CHG) return CHG; var R = E.rng(4711), t = new Date(NOW).getTime() - 3e5, out = [];
    for (var i = 0; i < 140; i++) {
      var doc = D.documents[Math.floor(R() * R() * 12)], s = R() < 0.6 ? 0 : 1 + Math.floor(R() * 4), air = s === 0, tbl = (1 + Math.floor(R() * 4)) + '.' + (1 + Math.floor(R() * 6));
      var row = air ? D.emissionSources[Math.floor(R() * 42)] : 'Monitoring point MP-' + (101 + Math.floor(R() * 28)), col = (air ? AIR_COLS : GEN_COLS)[Math.floor(R() * (air ? 8 : 6))];
      var o = R(), origin = i > 131 ? 'Migration' : o < 0.55 ? 'UserEdit' : o < 0.78 ? 'Import' : 'Recalculation', base = +(R() * 900).toFixed(4), was = R() < 0.12 ? null : base, now = +(base * (0.9 + R() * 0.2)).toFixed(4);
      t -= Math.round(R() * (i > 131 ? 4e7 : 3.4e6)); if (i === 132) t = new Date('2025-11-03T10:20:00').getTime();
      out.push({ id: 'CH-' + (58210 - i), at: isoLocal(t), user: origin === 'Recalculation' ? 'System' : origin === 'Migration' ? 'bootstrap' : origin === 'Import' && R() < 0.4 ? 'svc-import' : doc.owner, doc: doc.id, facility: doc.facility, s: s, sheet: D.sheetNames[s], table: tbl, row: row, col: col, was: origin === 'Migration' ? null : was, now: now, origin: origin, late: !!doc.late && origin !== 'Recalculation' && origin !== 'Migration' && R() < 0.8, correlationId: Math.round(R() * 1e12).toString(16) + '-c' + i });
    }
    Object.assign(out[0], { at: '2026-09-18T13:58', user: 'D. Akhmetova', doc: 'DOC-000001', facility: D.project.name, s: 0, sheet: 'Air emissions', table: '1.3', row: 'Gas turbine GT-4', col: 'Hours', was: 744, now: 720, origin: 'UserEdit', late: false });
    Object.assign(out[1], { doc: 'DOC-000008', facility: D.documents[7].facility, user: D.documents[7].owner, origin: 'UserEdit', late: true });
    return (CHG = out);
  }
  function isoLocal(ms) { var d = new Date(ms), p = function (n) { return ('0' + n).slice(-2); }; return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + 'T' + p(d.getHours()) + ':' + p(d.getMinutes()); }
  function cellAddr(c) { return c.sheet + ' › ' + c.table + ' › ' + c.row + ' · ' + c.col; }
  function val(v) { return v == null ? '—' : F.number(v); }
  function cellHistory(c) { var R = E.rng(+c.id.slice(3)), n = 2 + Math.floor(R() * 3), out = [c], v = c.was, t = new Date(c.at).getTime();
    for (var i = 0; i < n && v != null; i++) { t -= Math.round((0.3 + R()) * 2.6e8); var last = i === n - 1, prev = last ? null : +(v * (0.92 + R() * 0.16)).toFixed(4), og = last ? 'Import' : ['UserEdit', 'Recalculation', 'UserEdit'][i % 3];
      out.push({ id: c.id + '-' + i, at: isoLocal(t), user: og === 'Recalculation' ? 'System' : c.user === 'System' ? 'D. Akhmetova' : c.user, was: prev, now: v, origin: og, late: false }); v = prev; }
    return out; }
  var STRUCT = null, STRUCT_AREA = { 'Role.Grant': 'Access', 'Period.Close': 'Periods', 'Template.Publish': 'Templates', 'Sheet.Reopen': 'Documents' };
  var STRUCT_REASON = { 'Role.Grant': ['Covers a colleague during leave', 'New employee in the HSE department', 'Audit preparation — read-only access'], 'Period.Close': ['Deadline reached; all sheets submitted', 'Closed early at the request of the HSE manager'], 'Template.Publish': ['Added table 13.7 “Emergency releases — permit comparison”', 'Fixed units in GHG tables'], 'Sheet.Reopen': ['Laboratory results arrived after approval', 'Wrong fuel gas density was used'] };
  function structRows() { if (STRUCT) return STRUCT; STRUCT = D.audit.filter(function (a) { return STRUCT_AREA[a.action]; }).map(function (a, i) { var rs = STRUCT_REASON[a.action]; return Object.assign({ area: STRUCT_AREA[a.action], reason: rs[i % rs.length] }, a); }); return STRUCT; }

  E.screen('/admin/audit', { title: 'Audit trail', group: 'Operate', icon: 'history', order: 3, render: function (el, ctx) {
    var page = h('div', { class: 'page page-fill' }); el.appendChild(page);
    page.appendChild(E.PageHeader({ ctx: ctx, id: 'aud', title: 'Audit trail', back: false, subtitle: 'Every change of every number: who, when, what it was and what it became. Nothing here can be edited or deleted.', secondary: [{ label: 'Export…', icon: 'download', id: 'aud-export', onClick: function () { ctx.openDialog('export-audit'); } }] }));
    var dataList = null, today = NOW.slice(0, 10), weekAgo = addDays(NOW, -7);
    function inRange(c, v) { var d = c.at.slice(0, 10); return v === 'today' ? d === today : v === 'week' ? d >= weekAgo : v === 'period' ? d.slice(0, 7) === D.currentPeriod : d >= RANGE.from && d <= RANGE.to; }
    function renderData(panel) {
      var rows = ctx.state === 'empty' ? [] : changes(), authors = []; rows.forEach(function (c) { if (authors.indexOf(c.user) < 0) authors.push(c.user); });
      dataList = miniList(panel, ctx, { id: 'aud-data', rows: rows,
        stats: [{ id: 'today', label: 'changes today', match: function (c) { return c.at.slice(0, 10) === today; } }, { id: 'import', label: 'by import', match: function (c) { return c.origin === 'Import'; } }, { id: 'recalc', label: 'by recalculation', match: function (c) { return c.origin === 'Recalculation'; } }, { id: 'late', label: 'late edits', tone: 'warning', match: function (c) { return c.late; } }],
        search: { placeholder: 'Document, sheet, table or row', text: function (c) { return c.doc + ' ' + c.facility + ' ' + cellAddr(c); } },
        filters: [{ id: 'user', label: 'Authors', options: authors.sort() }, { id: 'origin', label: 'Origins', options: Object.keys(ORIGIN_LABEL).map(function (k) { return { value: k, label: ORIGIN_LABEL[k] }; }) }, { id: 'range', label: 'Dates', all: 'Any date', options: [{ value: 'today', label: 'Today' }, { value: 'week', label: 'Last 7 days' }, { value: 'period', label: F.period(D.currentPeriod) }, { value: 'custom', label: 'Custom range…' }], match: inRange }],
        toggles: [{ id: 'late', label: 'Late edits only', match: function (c) { return c.late; } }],
        onFilter: function (v) { if (v.range === 'custom' && !ctx.saved.rangeAsked) { ctx.saved.rangeAsked = true; ctx.openDialog('date-range'); } if (v.range !== 'custom') ctx.saved.rangeAsked = false; },
        table: { tall: true, pageSize: 50, rowLabel: function (c) { return 'History of ' + cellAddr(c); }, columns: [
          { key: 'at', label: 'When', render: function (c) { return h('span', { class: 'mono' }, F.dateTime(c.at)); } }, { key: 'user', label: 'Who' },
          { key: 'doc', label: 'Document · cell', sortValue: function (c) { return c.doc + cellAddr(c); }, render: function (c) { return E.twoLine(c.row + ' · ' + c.col, c.doc + ' · ' + c.sheet + ' › ' + c.table, { mono: false }); } },
          { key: 'now', label: 'Was → becomes', sortable: false, render: function (c) { return arrow(val(c.was), val(c.now)); } },
          { key: 'origin', label: 'Origin', hideSm: true, render: function (c) { return tag(ORIGIN_ICON[c.origin], ORIGIN_LABEL[c.origin], c.origin); } },
          { key: 'late', label: 'Late', sortValue: function (c) { return c.late ? 0 : 1; }, render: function (c) { return c.late ? lateMark() : h('span', { class: 'faint' }, ''); } }] },
        empty: { icon: 'history', title: 'No changes recorded for this selection', text: 'Try a wider date range or remove the author filter. Changes are recorded from the moment a document is created, so an empty list usually means the filters are too strict.', actions: [{ label: 'Show all changes', onClick: function () { E.go('/admin/audit', { params: { state: 'data' } }); } }] },
        error: { code: 'ECR-AUD-0503' }, onOpen: function (c) { ctx.openPanel(c.id); } });
    }
    function renderStruct(panel) {
      miniList(panel, ctx, { id: 'aud-struct', rows: ctx.state === 'empty' ? [] : structRows(),
        search: { placeholder: 'Person, object or reason', text: function (a) { return a.user + ' ' + a.object + ' ' + a.label + ' ' + a.reason; } }, filters: [{ id: 'area', label: 'Areas', options: ['Access', 'Periods', 'Templates', 'Documents'] }],
        table: { tall: true, rowLabel: function (a) { return 'Open ' + a.label; }, columns: [{ key: 'at', label: 'When', render: function (a) { return h('span', { class: 'mono' }, F.dateTime(a.at)); } }, { key: 'user', label: 'Who' }, { key: 'label', label: 'What', render: function (a) { return E.twoLine(a.label, a.object, { mono: false }); } }, { key: 'area', label: 'Area', hideSm: true }, { key: 'reason', label: 'Reason' }] },
        empty: { icon: 'history', title: 'No structure changes for this selection', text: 'Templates, periods and access rights were not changed in the selected range. Widen the range or clear the filters.' }, error: { code: 'ECR-AUD-0504' }, onOpen: function (a) { ctx.openPanel(a.id); } });
    }
    var tabs = E.Tabs({ id: 'aud-tabs', ctx: ctx, fill: true, tabs: [{ id: 'data', label: 'Data changes', render: renderData }, { id: 'structure', label: 'Structure changes', render: renderStruct }] }); tabs.classList.add('ops-fill'); page.appendChild(tabs);
    ctx.panel('*', function (id) {
      if (/^AU-/.test(id)) { var a = structRows().filter(function (x) { return x.id === id; })[0]; if (!a) return; var link = a.area === 'Access' ? ['Open Security', '#/admin/security?tab=users'] : a.area === 'Periods' ? ['Open Periods', '#/admin/periods'] : a.area === 'Templates' ? ['Open the template', '#/admin/templates/GEN07131100'] : ['Open the document', '#/documents/' + a.object.slice(0, 10)];
        E.Drawer({ id: 'aud-struct-drawer', title: a.label, subtitle: a.object, badge: dbadge('severity', a.severity, { quiet: a.severity === 'Info' }), footer: [{ label: link[0], icon: 'external', onClick: function () { E.go(link[1]); } }],
          body: function (b) { b.appendChild(E.Section({ title: 'Reason given', content: para('“' + a.reason + '”') })); b.appendChild(E.KeyValue([{ label: 'When', value: F.dateTime(a.at) }, { label: 'Who', value: a.user }, { label: 'Area', value: a.area }, { label: 'Object', value: a.object }, { label: 'Action', value: E.CodeText(a.action) }, { label: 'From address', value: a.ip, mono: true }, { label: 'Correlation ID', value: withCopy(a.correlationId, 'correlation ID') }])); } }); return; }
      var c = changes().filter(function (x) { return x.id === id; })[0]; if (!c) return; var hist = cellHistory(c), tno = Math.max(0, (+c.table.split('.')[0] - 1) * 7 + (+c.table.split('.')[1] - 1));
      E.Drawer({ id: 'aud-cell-drawer', title: c.row + ' · ' + c.col, subtitle: c.doc + ' · ' + c.sheet + ' › table ' + c.table, footer: [{ label: 'Open the cell in the document', icon: 'external', variant: 'primary', id: 'aud-open-cell', onClick: function () { E.go(E.href('/documents/' + c.doc, { sheet: c.s, table: tno }).slice(1)); } }], onClose: function () { if (dataList) dataList.table.select(null); },
        body: function (b) {
          if (c.late) b.appendChild(E.Banner({ tone: 'warning', icon: 'clock', title: 'Late edit', text: 'Made after the data entry deadline of the period — while it was in grace or reopened.' }));
          b.appendChild(E.KeyValue([{ label: 'Cell', value: cellAddr(c) }, { label: 'Document', value: h('a', { href: '#/documents/' + c.doc, class: 'mono' }, c.doc), hint: c.facility }, { label: 'This change', value: arrow(val(c.was), val(c.now)) }, { label: 'Correlation ID', value: withCopy(c.correlationId, 'correlation ID'), hint: 'all changes of one save, import or recalculation share it' }]));
          b.appendChild(E.Section({ title: 'Full history of this cell', hint: F.plural(hist.length, { one: 'change', other: 'changes' }) + ' · newest first', content: timeline(hist.map(function (x, i) { return { cls: i === 0 ? 'cur' : null, dot: i === 0 ? '' : 'filled', title: x.user, right: h('span', { class: 'mono t-xs muted' }, F.dateTime(x.at)), body: arrow(val(x.was), val(x.now)), meta: [ORIGIN_LABEL[x.origin], x.late ? ' · late' : '', i === 0 ? ' · the change you opened' : '', x.was == null ? ' · first value' : ''].join('') }; })) }));
        } });
    });
    ctx.dialog('date-range', function () { var f = E.Input({ id: 'aud-range-from', label: 'From', type: 'date', value: RANGE.from }), t = E.Input({ id: 'aud-range-to', label: 'To', type: 'date', value: RANGE.to });
      E.Dialog({ id: 'aud-range', title: 'Date range', narrow: true, body: h('div', { class: 'stack gap-3' }, h('div', { class: 'form-grid' }, f, t), h('p', { class: 'muted' }, 'Both days are included. The trail goes back to 03 Nov 2025 — the day data was migrated.')), footer: [{ label: 'Cancel' }, { label: 'Apply range', variant: 'primary', onClick: function () { if (t.value < f.value) { t.setError('“To” must not be earlier than “From”.'); t.input.focus(); return false; } RANGE.from = f.value; RANGE.to = t.value; ctx.saved.rangeAsked = true; if (dataList) { dataList.filters.set('range', 'custom'); } E.Toast('Showing ' + F.date(RANGE.from) + ' → ' + F.date(RANGE.to)); } }] }); });
    ctx.dialog('export-audit', function () { E.ConfirmDialog({ id: 'aud-export-dlg', title: 'Export the audit trail to Excel?', danger: false, consequences: [{ text: 'The current filters apply: you get exactly the rows you see, up to 1 000 000.', note: true }, { text: 'Runs in the background; the file appears in My tasks.', note: true }], verb: 'Start export', icon: 'download', onConfirm: function () { E.tasks.start({ title: 'Audit trail export', icon: 'download', detail: 'audit-2026-09-18.xlsx', duration: 5000, doneDetail: 'Ready · 140 rows', result: { label: 'Download', onClick: function () { E.Toast('Download started', 'success'); } } }); E.Toast('Export continues in My tasks', { action: { label: 'Open', onClick: function () { E.tasks.open(); } } }); } }); });
  } });

  /* ================= CONSISTENCY ISSUES ================= */
  var CI_TEXT = {
    'CI-118': ['The methane total on the GHG sheet is calculated from the Air emissions sheet. The two numbers should match, but they differ — one of them was changed after the other was calculated.', 'Open the GHG table and run recalculation. If the numbers still differ, someone typed over a calculated cell: check the cell history.'],
    'CI-117': ['A facility cannot discharge more water than it takes in plus what it receives. Discharge here is higher than intake, so one of the two tables is incomplete or has a unit mistake.', 'Compare the units of both tables (m³ vs 10³ m³) and check that all intake sources are filled.'],
    'CI-116': ['Fuel gas consumption is entered twice: on Energy and on Air emissions. The two entries differ by 12 %, more than the 5 % allowed.', 'Find out which number is right — usually the metered one on Energy — and correct the other sheet.'],
    'CI-115': ['Data refers to an emission source that was later switched off in the registry. The numbers are kept, but new periods will not offer this source.', 'If the source still works, reactivate it in the registry. If not, nothing to do — acknowledge the finding.'],
    'CI-112': ['A waste transfer must name who received the waste. The field is empty.', 'Fill in the receiving facility on the Waste sheet.'],
    'CI-109': ['A source cannot run more hours than the month has. 744 h were entered for a 720-hour month.', 'Correct the run hours — this also affects every emission calculated from them.'],
    'CI-101': ['A sheet that was already approved got new numbers from a recalculation. Approved data must not change silently.', 'Review the changed cells in the audit trail. Either return the sheet for edits so it can be approved again, or restore the approved values.'] };
  (function () { var first = ['2026-09-16', '2026-09-11', '2026-09-17', '2026-09-18', '2026-09-04', '2026-09-15', '2026-09-18']; D.consistency.forEach(function (c, i) { if (!c.firstSeen) { c.firstSeen = first[i % 7] + 'T02:00'; c.lastSeen = c.status === 'Resolved' ? '2026-09-12T02:00' : c.found; } }); })();
  function ciHref(c) { if (/Emission sources/.test(c.where)) return '#/admin/registries/EmissionSources/entries'; var s = 0; D.sheetNames.forEach(function (n, i) { if (c.where.indexOf(n) === 0) s = i; }); return E.href('/documents/' + c.document, { sheet: s }); }

  E.screen('/admin/consistency', { title: 'Consistency issues', group: 'Operate', icon: 'alert', order: 4, render: function (el, ctx) {
    var bannerHost = h('div', { class: 'ops-slot' }), list;
    function show(o) { bannerHost.textContent = ''; if (o) bannerHost.appendChild(E.ResultBanner(Object.assign({}, o, { onDismiss: function () { delete FLASH.ci; } }))); }
    function stateCell(c) { return c.status === 'Open' ? tag('circle', 'Open') : c.status === 'Acknowledged' ? tag('eye', 'Acknowledged', c.ackNote) : tag('check', 'Resolved'); }
    list = E.ListPage(el, ctx, { id: 'ci', rows: ctx.state === 'empty' ? [] : D.consistency, banner: bannerHost,
      header: { title: 'Consistency issues', back: false, subtitle: 'Every night ECR compares numbers that must agree with each other — across sheets, documents and registries. Each finding says what it means and what to do.', primary: { label: 'Run check now', icon: 'play', id: 'ci-run', onClick: function () { ctx.openDialog('run-check'); } } },
      stats: [{ id: 'critical', label: 'critical', tone: 'danger', match: function (c) { return c.severity === 'Critical' && c.status === 'Open'; } }, { id: 'error', label: 'errors', tone: 'danger', match: function (c) { return c.severity === 'Error' && c.status === 'Open'; } }, { id: 'warning', label: 'warnings', tone: 'warning', match: function (c) { return c.severity === 'Warning' && c.status === 'Open'; } }, { id: 'ack', label: 'acknowledged', match: function (c) { return c.status === 'Acknowledged'; } }],
      search: { placeholder: 'Finding, document or place', text: function (c) { return c.id + ' ' + c.title + ' ' + c.document + ' ' + c.where; } },
      filters: [{ id: 'status', label: 'States', options: ['Open', 'Acknowledged', 'Resolved'] }, { id: 'document', label: 'Documents', options: D.consistency.map(function (c) { return c.document; }).filter(function (d, i, a) { return a.indexOf(d) === i; }).sort() }],
      table: { rowKey: 'id', tall: true, rowLabel: function (c) { return 'Open finding ' + c.id; }, columns: [
        { key: 'severity', label: 'Severity', sortValue: function (c) { return ['Critical', 'Error', 'Warning', 'Info'].indexOf(c.severity); }, render: function (c) { return E.StatusBadge('severity', c.severity, { quiet: c.severity === 'Info' || c.status !== 'Open' }); } },
        { key: 'title', label: 'What', render: function (c) { return E.twoLine(c.title, c.id, { mono: false }); } }, { key: 'where', label: 'Where', render: function (c) { return E.twoLine(c.where, c.document, { mono: false }); } },
        { key: 'firstSeen', label: 'First seen', hideSm: true, render: function (c) { return F.date(c.firstSeen); } }, { key: 'lastSeen', label: 'Last seen', hideSm: true, render: function (c) { return F.date(c.lastSeen); } }, { key: 'status', label: 'State', render: stateCell }] },
      empty: { icon: 'checkCircle', title: 'The last check found nothing', text: 'Last run: ' + F.dateTime(D.consistency[0] ? D.consistency[0].found : NOW) + ' · took 2:05 · 12 documents compared. The next run is tonight at 02:00.', actions: [{ label: 'Run check now', onClick: function () { ctx.openDialog('run-check'); } }, { label: 'Open the last run', href: '#/admin/jobs?panel=J-10402' }] },
      error: { code: 'ECR-CHK-0503' },
      drawer: function (c) { var tx = CI_TEXT[c.id] || ['Two numbers that must agree differ.', 'Open the record and compare.'], nights = Math.max(1, Math.round((new Date(c.lastSeen) - new Date(c.firstSeen)) / 864e5) + 1);
        return { title: c.title, subtitle: c.id + ' · ' + c.document, badge: dbadge('severity', c.severity), footer: [{ label: 'Open affected record', icon: 'external', align: 'left', id: 'ci-open', onClick: function () { E.go(ciHref(c).slice(1)); } }, c.status === 'Open' ? { label: 'Acknowledge…', icon: 'eye', id: 'ci-ack', keepOpen: true, onClick: function () { ctx.openDialog('ack-issue', c); } } : { label: 'Close' }],
          body: function (b) {
            if (c.status === 'Acknowledged') b.appendChild(E.Banner({ icon: 'eye', title: 'Acknowledged by ' + c.ackBy, text: '“' + c.ackNote + '” — the finding stays in the list until the numbers agree again.' }));
            if (c.status === 'Resolved') b.appendChild(E.Banner({ icon: 'check', title: 'Resolved', text: 'The last check no longer finds this problem. It is kept here for 30 days.' }));
            b.appendChild(E.Section({ title: 'What this means', content: para(tx[0]) })); b.appendChild(E.Section({ title: 'What to do', content: para(tx[1]) }));
            b.appendChild(E.KeyValue([{ label: 'Where', value: c.where }, { label: 'Document', value: h('a', { href: '#/documents/' + c.document, class: 'mono' }, c.document) }, { label: 'First seen', value: F.dateTime(c.firstSeen) }, { label: 'Last seen', value: F.dateTime(c.lastSeen), hint: 'found ' + F.plural(nights, { one: 'night', other: 'nights' }) + ' in a row' }, { label: 'Found by', value: h('a', { href: '#/admin/jobs?panel=' + c.check }, 'Consistency check ' + c.check) }]));
          } }; } });
    if (FLASH.ci) show(FLASH.ci.make(ctx));
    ctx.onLeave(function () { var x = FLASH.ci; if (x) { if (x.keep) x.keep = false; else delete FLASH.ci; } });
    function acked(c0, c) { return { title: c.id + ' acknowledged', text: 'It no longer counts as open, but stays in the list until the numbers agree. What next: fix the record — the next nightly check will resolve the finding by itself.', actions: [{ label: 'Open affected record', href: ciHref(c) }, { label: 'Undo', icon: 'undo', onClick: function () { c.status = 'Open'; delete FLASH.ci; c0.refresh(); } }] }; }
    ctx.dialog('ack-issue', function (c) { c = c || D.consistency.filter(function (x) { return x.status === 'Open'; })[0] || D.consistency[0];
      E.ReasonDialog({ id: 'ci-ack-dlg', title: 'Acknowledge “' + c.title + '”?', text: 'Acknowledging means: “we have seen it and know why”. It does not fix anything and does not hide the finding.', label: 'Note', placeholder: 'e.g. GT-4 was out of service; hours will be corrected by D. Akhmetova on Monday', hint: 'At least 10 characters. Everyone who opens the finding sees this note.', verb: 'Acknowledge', icon: 'eye',
        onSubmit: function (note) { c.status = 'Acknowledged'; c.ackNote = note; c.ackBy = ctx.me.name; ctx.setQuery({ panel: null }); FLASH.ci = { make: function (x) { return acked(x, c); }, keep: true }; ctx.refresh(); } }); });
    ctx.dialog('result-acknowledged', function () { show(acked(ctx, D.consistency[0])); });
    function started() { return { title: 'Consistency check is running', text: 'It compares all 12 documents of ' + F.period(D.currentPeriod) + ' and takes about two minutes. Documents stay editable. New findings appear here when it finishes.', actions: [{ label: 'Open My tasks', onClick: function () { E.tasks.open(); } }, { label: 'All jobs', href: '#/admin/jobs' }] }; }
    ctx.dialog('run-check', function () { E.ConfirmDialog({ id: 'ci-run-dlg', title: 'Run the consistency check now?', danger: false, consequences: [{ text: 'Compares every document of ' + F.period(D.currentPeriod) + ' — about two minutes.', note: true }, { text: 'Nothing is changed and nobody is blocked; the check only reads.', note: true }, { text: 'It also runs by itself every night at 02:00.', note: true }], verb: 'Run check', icon: 'play',
      onConfirm: function () { E.tasks.start({ title: 'Consistency check · ' + F.period(D.currentPeriod), icon: 'alert', detail: '12 documents', duration: 8000, doneDetail: 'Finished · no new findings', onDone: function () { E.Toast('Consistency check finished — no new findings', 'success'); } }); FLASH.ci = { make: started, keep: true }; ctx.refresh(); } }); });
    ctx.dialog('result-check-started', function () { show(started()); });
  } });

  /* ================= INTERFACE TEXTS ================= */
  var UIS = null, TOTAL_STRINGS = 1340, LANGS = { ru: 'RU · Русский', kk: 'KZ · Қазақша' };
  function strings() {
    if (UIS) return UIS;
    UIS = D.uiStrings.map(function (s) { return { key: s.key, en: s.en, tr: { ru: s.ru || '', kk: s.kk || '' }, area: s.area, updated: '2026-08-21' }; });
    UIS.push({ key: 'validation.required.named', en: '{name} is required', tr: { ru: '', kk: '' }, area: 'validation', updated: null }, { key: 'grid.rows.shown', en: 'Showing {shown} of {total} rows', tr: { ru: '', kk: '' }, area: 'grid', updated: null });
    var AREAS = ['documents', 'sheet', 'grid', 'validation', 'period', 'import', 'export', 'error', 'login', 'state', 'security', 'jobs', 'audit', 'registry', 'template', 'health', 'common'], NOUN = ['title', 'subtitle', 'action.open', 'action.save', 'action.cancel', 'action.delete', 'confirm.title', 'confirm.text', 'empty.title', 'empty.text', 'error.title', 'error.text', 'hint', 'label.name', 'label.state', 'label.updated', 'tooltip', 'toast.saved', 'toast.deleted', 'filter.all'];
    var EN = { title: 'Title of the section', subtitle: 'One line that explains the section', 'action.open': 'Open', 'action.save': 'Save changes', 'action.cancel': 'Cancel', 'action.delete': 'Delete', 'confirm.title': 'Delete “{name}”?', 'confirm.text': 'This cannot be undone.', 'empty.title': 'Nothing here yet', 'empty.text': 'Records appear here once someone creates them.', 'error.title': 'This view could not be loaded', 'error.text': 'Try again; if it repeats, send the correlation ID to support.', hint: 'Shown under the field', 'label.name': 'Name', 'label.state': 'State', 'label.updated': 'Updated', tooltip: 'More details', 'toast.saved': 'Saved', 'toast.deleted': '{name} deleted', 'filter.all': 'All' };
    var seen = {}; UIS.forEach(function (s) { seen[s.key] = 1; });
    for (var i = 0; UIS.length < TOTAL_STRINGS; i++) { var a = AREAS[i % AREAS.length], k = NOUN[Math.floor(i / AREAS.length) % NOUN.length], sfx = Math.floor(i / (AREAS.length * NOUN.length)), key = a + '.' + k + (sfx ? '.' + sfx : ''); if (seen[key]) continue; seen[key] = 1; UIS.push({ key: key, en: EN[k], tr: { ru: '', kk: '' }, area: a, updated: null }); }
    UIS.forEach(function (s) { s.id = s.key; s.scope = s.area === 'login' || s.area === 'error' ? 'Public' : 'Private'; });
    return UIS;
  }
  function holders(t) { return (t.match(/\{\w+\}/g) || []).sort(); }
  function holdersOk(s, t) { return !t || holders(s.en).join() === holders(t).join(); }
  function growth(s, t) { return t ? (t.length - s.en.length) / s.en.length : 0; }
  function coverage(lang) { var all = strings(); return lang === 'en' ? all.length : all.filter(function (s) { return s.tr[lang]; }).length; }

  E.screen('/admin/ui-strings', { title: 'Interface texts', group: 'Operate', icon: 'message', order: 5, render: function (el, ctx) {
    var lang = ctx.query.lang === 'kk' ? 'kk' : ctx.saved.lang || 'ru', scope = ctx.query.scope || ctx.saved.scope || 'all'; if (ctx.query.lang === 'ru') lang = 'ru'; ctx.saved.lang = lang; ctx.saved.scope = scope;
    var page = h('div', { class: 'page page-fill' }); el.appendChild(page);
    page.appendChild(E.PageHeader({ ctx: ctx, id: 'uis', title: 'Interface texts', back: false, subtitle: 'Every label, message and button text of ECR. English is the source; translate row by row or import a whole catalogue. Missing translations fall back to English.',
      secondary: [{ label: 'Export…', icon: 'download', id: 'uis-export', onClick: function () { ctx.openDialog('export-strings'); } }, { label: 'Import…', icon: 'upload', id: 'uis-import', onClick: function () { ctx.openDialog('import-strings'); } }] }));
    var res = slot(page, ctx, 'uis'), covHost = h('div', { class: 'ops-cov', id: 'uis-coverage', role: 'group', 'aria-label': 'Translation coverage' }), listHost = h('div', { class: 'stack gap-3 ops-fill' }), list = null;
    function paintCov() { covHost.textContent = ''; var total = strings().length; [['en', 'EN'], ['ru', 'RU'], ['kk', 'KZ']].forEach(function (l) { var n = coverage(l[0]), pct = n / total * 100; covHost.appendChild(h('div', { class: 'ops-cov-i', title: F.int(n) + ' of ' + F.int(total) + ' strings' }, h('b', l[1]), E.Progress({ value: pct, label: (pct === 100 || pct === 0 ? pct : pct.toFixed(1)) + ' %', ariaLabel: l[1] + ' coverage' }), h('span', { class: 'muted t-xs nowrap' }, l[0] === 'en' ? 'source' : F.int(n) + ' of ' + F.int(total)))); }); }
    function rowsNow() { return ctx.state === 'empty' ? [] : strings().filter(function (s) { return scope === 'all' || s.scope === scope; }); }
    function paintList() {
      listHost.textContent = '';
      list = miniList(listHost, ctx, { id: 'uis-list', rows: rowsNow(),
        stats: [{ id: 'all', label: 'strings', value: function (r) { return r.length; } }, { id: 'missing', label: 'missing translation', match: function (s) { return !s.tr[lang]; } }, { id: 'long', label: 'much longer than English', tone: 'warning', match: function (s) { return growth(s, s.tr[lang]) > 0.4; } }, { id: 'ph', label: 'broken placeholders', tone: 'danger', match: function (s) { return !holdersOk(s, s.tr[lang]); } }],
        search: { placeholder: 'Key or text', text: function (s) { return s.key + ' ' + s.en + ' ' + s.tr[lang]; } }, filters: [{ id: 'area', label: 'Areas', options: strings().map(function (s) { return s.area; }).filter(function (a, i, arr) { return arr.indexOf(a) === i; }).sort() }],
        toggles: [{ id: 'missing', label: 'Missing translation', match: function (s) { return !s.tr[lang]; } }],
        table: { rowKey: 'id', pageSize: 50, rowLabel: function (s) { return 'Edit ' + s.key; }, columns: [{ key: 'key', label: 'Key', mono: true }, { key: 'en', label: 'English' },
          { key: 'tr', label: LANGS[lang].split(' · ')[1], sortValue: function (s) { return s.tr[lang] || String.fromCharCode(65535); }, render: function (s) { return s.tr[lang] ? h('span', { lang: lang }, s.tr[lang]) : h('span', { class: 'muted' }, 'Missing — English is shown'); } },
          { key: 'len', label: 'Length', num: true, hideSm: true, sortValue: function (s) { return growth(s, s.tr[lang]); }, render: function (s) { var g = growth(s, s.tr[lang]); if (!s.tr[lang]) return null; var t = (g >= 0 ? '+' : '−') + Math.abs(Math.round(g * 100)) + ' %'; return g > 0.4 ? h('span', { class: 'warning-text', title: 'More than 40 % longer than English — may not fit buttons and column headers' }, t) : t; } },
          { key: 'updated', label: 'Updated', hideSm: true, render: function (s) { return s.updated ? F.date(s.updated) : null; } }] },
        empty: { icon: 'message', title: 'The catalogue is empty', text: 'Interface texts are loaded from the server catalogue at start-up. An empty list means the catalogue was not deployed — import it.', actions: [{ label: 'Import catalogue', variant: 'primary', onClick: function () { ctx.openDialog('import-strings'); } }] }, error: { code: 'ECR-I18N-0503' }, onOpen: function (s) { ctx.openPanel(s.key); } });
    }
    if (ctx.state === 'data') { page.appendChild(h('div', { class: 'ops-ctl' }, E.Select({ id: 'uis-lang', bare: true, ariaLabel: 'Language to translate into', value: lang, options: [{ value: 'ru', label: 'Translate into ' + LANGS.ru }, { value: 'kk', label: 'Translate into ' + LANGS.kk }], onChange: function (v) { lang = ctx.saved.lang = v; ctx.setQuery({ lang: v === 'ru' ? null : v }); paintList(); } }),
      E.Segmented({ id: 'uis-scope', label: 'Scope', value: scope, options: [{ value: 'all', label: 'All' }, { value: 'Public', label: 'Public', title: 'Shown before sign-in: login page, error pages' }, { value: 'Private', label: 'Private', title: 'Shown after sign-in' }], onChange: function (v) { scope = ctx.saved.scope = v; ctx.setQuery({ scope: v === 'all' ? null : v }); paintList(); } }), h('span', { class: 'ops-sp' }), covHost)); paintCov(); }
    page.appendChild(listHost); paintList();

    function openString(s, draft) {
      var tr = E.Textarea({ id: 'uis-ed-tr', label: 'Translation · ' + LANGS[lang], rows: 3, maxlength: 500, hint: 'Leave empty to keep showing English.', onInput: check }), meter = h('div', { class: 'ops-meter', id: 'uis-ed-meter', role: 'status' }), phHost = h('div', { class: 'row wrap', id: 'uis-ed-ph' });
      tr.input.value = draft != null ? draft : s.tr[lang]; tr.input.setAttribute('lang', lang);
      function check() { var t = tr.input.value, g = growth(s, t), need = holders(s.en), have = holders(t); meter.textContent = ''; meter.className = 'ops-meter' + (g > 0.4 ? ' warn' : '');
        meter.appendChild(g > 0.4 ? icon('alert', 14) : icon('ruler', 14)); meter.appendChild(h('span', t.length + ' characters · English has ' + s.en.length + (t ? ' · ' + (g >= 0 ? '+' : '−') + Math.abs(Math.round(g * 100)) + ' %' : '') + (g > 0.4 ? ' — more than 40 % longer: check that it fits buttons and column headers' : '')));
        phHost.textContent = ''; if (!need.length) { phHost.appendChild(h('span', { class: 'muted t-xs' }, 'No placeholders in this string.')); return; }
        need.forEach(function (p) { var ok = have.indexOf(p) >= 0; phHost.appendChild(h('span', { class: 'chip', title: ok ? 'Present in the translation' : 'Missing in the translation' }, p + (ok ? ' ✓' : ' — missing'))); }); have.filter(function (p) { return need.indexOf(p) < 0; }).forEach(function (p) { phHost.appendChild(h('span', { class: 'chip' }, p + ' — unknown')); }); }
      function save(next) { var t = tr.input.value.trim(); if (!holdersOk(s, t)) { tr.setError('Keep the placeholders exactly as in English: ' + holders(s.en).join(', ') + '. They are replaced with real values at run time.'); tr.input.focus(); return false; }
        var old = s.tr[lang]; s.tr[lang] = t; s.updated = NOW.slice(0, 10); paintCov(); list.refresh(); E.Toast('Saved · ' + LANGS[lang].slice(0, 2) + ' coverage ' + F.int(coverage(lang)) + ' of ' + F.int(strings().length), { tone: 'success', action: { label: 'Undo', onClick: function () { s.tr[lang] = old; paintCov(); list.refresh(); } } });
        if (next) { var all = rowsNow(), i = all.indexOf(s), nx = all.slice(i + 1).concat(all.slice(0, i)).filter(function (x) { return !x.tr[lang]; })[0]; if (nx) later(function () { ctx.openPanel(nx.key); }); } }
      E.Drawer({ id: 'uis-drawer', title: 'Edit text', subtitle: s.key, onClose: function () { if (list) list.table.select(null); }, footer: [{ label: 'Cancel', align: 'left' }, { label: 'Save and next missing', id: 'uis-ed-next', onClick: function () { return save(true); } }, { label: 'Save', variant: 'primary', id: 'uis-ed-save', onClick: function () { return save(false); } }],
        body: function (b) { b.appendChild(E.KeyValue([{ label: 'Key', value: withCopy(s.key, 'key') }, { label: 'Scope', value: s.scope, hint: s.scope === 'Public' ? 'shown before sign-in' : 'shown after sign-in' }, { label: 'Area', value: s.area }]));
          b.appendChild(h('div', { class: 'field' }, h('span', { class: 'lbl' }, 'English (source)'), h('div', { class: 'ops-orig', id: 'uis-ed-en' }, s.en))); b.appendChild(tr); b.appendChild(meter);
          b.appendChild(h('div', { class: 'field' }, h('span', { class: 'lbl' }, 'Placeholders'), phHost, h('div', { class: 'hint' }, 'Text in {braces} is replaced with a real value. It must stay in the translation unchanged.'))); check(); if (draft != null) save(false); } });
      if (list) list.table.select(s.key);
    }
    ctx.panel('*', function (key) { var s = strings().filter(function (x) { return x.key === key; })[0]; if (s) openString(s); });
    ctx.dialog('edit-string', function () { lang = 'ru'; var s = strings().filter(function (x) { return x.key === 'validation.required.named'; })[0]; ctx.setQuery({ panel: s.key }); openString(s, 'Поле обязательно для заполнения, укажите значение'); });

    ctx.dialog('import-strings', function () { var data = { lang: lang, file: null, skip: true }, DIFF = [['sheet.submit', 'Отправить', 'Отправить на согласование', 'Changed'], ['validation.nonneg', '', 'Мән ≥ 0 болуы керек', 'New'], ['import.preview', '', 'Импортты тексеріңіз', 'New'], ['validation.required.named', '', 'Поле обязательно', 'Problem'], ['grid.rows.shown', '', 'Показано {shown} из {всего}', 'Problem'], ['documents.title', 'Документы', 'Документы', 'Same']];
      if (ctx.query.step) data.file = 'ecr-ui-' + data.lang + '-2026-09-18.xlsx';
      wzJump(ctx, 2, E.Wizard({ id: 'uis-imp', title: 'Import a catalogue', data: data, applyLabel: 'Import', steps: [
        { id: 'file', label: 'File', hint: 'Use a file exported from this page: the keys must match.', render: function (b, d, api) { var nm = h('b', { class: 'mono' }, data.file || 'No file chosen');
            b.appendChild(E.Select({ id: 'uis-imp-lang', label: 'Language of the file', value: data.lang, options: [{ value: 'ru', label: LANGS.ru }, { value: 'kk', label: LANGS.kk }], onChange: function (v) { data.lang = v; } }));
            b.appendChild(h('div', { class: 'ops-drop', id: 'uis-imp-drop' }, icon('upload', 20), nm, h('span', { class: 'muted' }, 'XLSX or JSON, up to 5 MB'), E.Button({ label: 'Choose file…', id: 'uis-imp-choose', onClick: function () { data.file = 'ecr-ui-' + data.lang + '-2026-09-18.xlsx'; nm.textContent = data.file; api.refreshButtons(); } }))); }, validate: function () { return data.file ? null : 'Choose a file first.'; } },
        { id: 'diff', label: 'Changes', hint: 'Nothing is applied yet. This is what the file would change.', render: function (b) {
            b.appendChild(h('div', { class: 'sumline' }, h('span', h('b', { class: 'mono' }, '412'), ' new'), h('span', { class: 'muted' }, '·'), h('span', h('b', { class: 'mono' }, '7'), ' changed'), h('span', { class: 'muted' }, '·'), h('span', h('b', { class: 'mono' }, '12'), ' the same'), h('span', { class: 'muted' }, '·'), h('span', { class: 'danger-text' }, h('b', { class: 'mono' }, '2'), ' with broken placeholders')));
            b.appendChild(h('div', { class: 'table-wrap' }, h('table', { class: 'data' }, h('thead', h('tr', h('th', 'Key'), h('th', 'Now'), h('th', 'In the file'), h('th', 'Change'))), h('tbody', DIFF.map(function (r) { return h('tr', h('td', { class: 'mono' }, r[0]), h('td', r[1] ? h('span', { class: r[3] === 'Changed' ? 'was' : null }, r[1]) : h('span', { class: 'faint' }, '—')), h('td', r[2]), h('td', r[3] === 'Problem' ? h('span', { class: 'danger-text' }, 'Placeholder broken') : r[3] === 'Same' ? h('span', { class: 'muted' }, 'No change') : r[3])); })))));
            b.appendChild(h('p', { class: 'muted t-xs' }, 'Showing 6 of 433 rows of the file.'));
            b.appendChild(E.Checkbox({ id: 'uis-imp-skip', label: 'Skip the 2 rows with broken placeholders', hint: 'They would show raw {braces} to users. Fix them in the file or edit them here afterwards.', checked: data.skip, onChange: function (v) { data.skip = v; } })); }, validate: function () { return data.skip ? null : 'Rows with broken placeholders cannot be imported. Keep them skipped or fix the file.'; } }],
        summary: function () { return E.KeyValue([{ label: 'File', value: data.file, mono: true }, { label: 'Language', value: LANGS[data.lang] }, { label: 'Will add', value: '412 translations' }, { label: 'Will change', value: '7 translations' }, { label: 'Skipped', value: '2 rows with broken placeholders' }, { label: 'Coverage after', value: F.int(coverage(data.lang) + 412) + ' of ' + F.int(strings().length) }]); },
        onApply: function (d, api) { var n = 0; strings().forEach(function (s) { if (n < 412 && !s.tr[data.lang] && !holders(s.en).length) { s.tr[data.lang] = (data.lang === 'ru' ? 'Перевод: ' : 'Аударма: ') + s.en; s.updated = NOW.slice(0, 10); n++; } }); api.close(); res.flash(function (c) { return imported(c, data.lang); }); } })); });
    function imported(c, l) { return { title: 'Catalogue imported · ' + LANGS[l], text: '412 translations added, 7 changed, 2 skipped because of broken placeholders. Users see the new texts after they reload the page. What next: fix the skipped rows.', actions: [{ label: 'Show broken placeholders', onClick: function () { E.go('/admin/ui-strings', { params: { dialog: 'edit-string' } }); } }] }; }
    ctx.dialog('result-imported', function () { res.show(imported(ctx, lang)); });
    ctx.dialog('export-strings', function () { var fmtSel = E.Select({ id: 'uis-exp-format', label: 'Format', value: 'xlsx', options: [{ value: 'xlsx', label: 'Excel (.xlsx) — for translators' }, { value: 'json', label: 'JSON — for deployment' }] }), only = E.Checkbox({ id: 'uis-exp-missing', label: 'Only strings without a translation', checked: true });
      E.Dialog({ id: 'uis-exp', title: 'Export the catalogue · ' + LANGS[lang], narrow: true, body: h('div', { class: 'stack gap-3' }, fmtSel, only, h('p', { class: 'muted' }, 'The file has the key, the English text and an empty column for the translation. Import it back here when it is filled.')), footer: [{ label: 'Cancel' }, { label: 'Export', variant: 'primary', icon: 'download', onClick: function () { E.Toast('Download started — ecr-ui-' + lang + '.' + fmtSel.value, 'success'); } }] }); });
  } });

  /* ================= HEALTH ================= */
  var HEALTH = { partitionsFixed: false, checked: D.health.checked };
  E.screen('/admin/health', { title: 'Health', group: 'Operate', icon: 'pulse', order: 6, render: function (el, ctx) {
    var H = D.health, lowPart = ctx.query['case'] === 'partitions' && !HEALTH.partitionsFixed, page = h('div', { class: 'page' }); el.appendChild(page);
    function check(id) { return H.checks.filter(function (c) { return c.id === id; })[0] || { state: 'Healthy', value: '', note: '' }; }
    var db = check('db'), jobs = check('jobs'), src = check('sources'), tr = check('transport');
    var failed = D.jobs.filter(function (j) { return j.state === 'Failed'; }), running = D.jobs.filter(function (j) { return j.state === 'Running'; }).length, queued = D.jobs.filter(function (j) { return j.state === 'Queued'; }).length, badSrc = D.sources.filter(function (s) { return s.health !== 'Healthy'; });
    var dbState = lowPart ? 'Degraded' : db.state, overall = lowPart && H.overall === 'Healthy' ? 'Degraded' : H.overall, lastErr = failed[0];
    var KV = { 'SQL Server edition': ['Database engine', null], 'Effective mode': ['Feature set in use', 'partitioning and compression are available'], 'Major version': ['SQL Server version', null], 'Read Committed Snapshot Isolation': ['Readers do not block writers', 'Read Committed Snapshot Isolation'], Filegroups: ['Storage groups', null], 'Partitions ahead': ['Monthly partitions prepared ahead', null], 'Data disk free': ['Free space on the data disk', null] };
    function diagnostics() { return ['ECR Web diagnostics · ' + F.dateTime(HEALTH.checked), 'Overall: ' + overall].concat(H.checks.map(function (c) { return c.name + ': ' + c.state + ' · ' + c.value + ' · ' + c.note; }), H.database.map(function (a) { return a[0] + ': ' + a[1]; }), H.application.map(function (a) { return a[0] + ': ' + a[1]; }), ['Log folder: D:\\ECR\\logs', 'Last error: ' + (lastErr ? lastErr.code + ' · ' + lastErr.correlationId : 'none')]).join('\n'); }
    page.appendChild(E.PageHeader({ ctx: ctx, id: 'hl', title: 'Health', back: false, badge: ctx.state === 'data' ? E.StatusBadge('health', overall) : null, subtitle: 'Is ECR working right now? Three parts are checked every minute. Warnings appear only when there is something to do.',
      primary: { label: 'Check now', icon: 'refresh', id: 'hl-check', onClick: function () { HEALTH.checked = NOW.slice(0, 11) + F.time(new Date()) + ':00'; ctx.refresh(); E.Toast('Checked just now — nothing changed', 'success'); } },
      secondary: [{ label: 'Copy diagnostics', icon: 'copy', id: 'hl-copy', onClick: function () { copy(diagnostics(), 'diagnostics'); } }] }));
    var res = slot(page, ctx, 'hl');
    function sec(id, title, state, value, concl, items, extra) { return h('section', { class: 'panel ops-hsec', id: 'hl-' + id, 'aria-labelledby': 'hl-' + id + '-h' }, h('div', { class: 'ops-hsec-h' }, h('h2', { id: 'hl-' + id + '-h' }, title), E.StatusBadge('health', state, { quiet: state === 'Healthy' }), h('span', { class: 'val' }, value)), h('p', { class: 'concl' }, concl), E.KeyValue(items, { wide: true }), extra || null); }
    page.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'form', rows: 6 }, error: { code: 'ECR-SYS-0503', title: 'Health could not be checked', text: 'The health endpoint did not answer. This usually means the application server itself is restarting. Try again in a minute.' },
      empty: { icon: 'pulse', title: 'Health has not been checked yet', text: 'The first check runs a minute after the application starts. You can run it now.', actions: [{ label: 'Check now', variant: 'primary', onClick: function () { E.go('/admin/health', { params: { state: 'data' } }); } }] },
      data: function () {
        var box = h('div', { class: 'ops-health' });
        if (tr.state !== 'Healthy') box.appendChild(E.Banner({ tone: 'warning', id: 'hl-warn-transport', title: 'Transport: ' + tr.value + ' — the session cookie is not Secure', text: 'ECR is served without TLS, so sign-in cookies can be read on the network. Put it behind HTTPS and set “RequireHttps” in the configuration.', actions: [{ label: 'Copy the setting name', onClick: function () { copy('Security:RequireHttps', 'setting name'); } }] }));
        if (lowPart) box.appendChild(E.Banner({ tone: 'warning', id: 'hl-warn-partitions', title: 'Partitions ahead: 2 — create more', text: 'Monthly partitions exist only until November 2026. Without new ones, data of later months lands in one oversized partition and queries slow down. Creating them is safe and takes seconds.', actions: [{ label: 'Create 12 more…', icon: 'plus', id: 'hl-create-part', onClick: function () { ctx.openDialog('create-partitions'); } }] }));
        box.appendChild(sec('db', 'Database', dbState, db.value + ' response', lowPart ? 'The database is available, but only 2 monthly partitions are prepared ahead.' : 'The database is available and answers quickly.',
          H.database.map(function (a) { var k = KV[a[0]] || [a[0], null], v = a[1]; if (a[0] === 'Partitions ahead') v = lowPart ? h('span', { class: 'warning-text t-b' }, '2 months') : HEALTH.partitionsFixed ? '14 months' : v; return { label: k[0], value: v, hint: k[1] }; }).concat([{ label: 'Row limit per query', value: F.int(2000000), mono: true }, { label: 'Last backup', value: F.dateTime('2026-09-18T01:30') }, { label: 'Maintenance window', value: null }])));
        box.appendChild(sec('jobs', 'Background jobs', jobs.state, jobs.value, failed.length ? 'The scheduler is running; ' + F.plural(failed.length, { one: 'job', other: 'jobs' }) + ' failed today and can be retried.' : 'The scheduler is running and nothing is stuck.',
          [{ label: 'Scheduler is running', value: F.bool(true) }, { label: 'Running now', value: String(running) }, { label: 'Waiting in the queue', value: String(queued) }, { label: 'Failed in the last 24 hours', value: failed.length ? h('a', { href: '#/admin/jobs' }, String(failed.length) + ' — open Jobs') : '0' }, { label: 'Average start latency', value: '1.4 s', mono: true }, { label: 'Last nightly check', value: F.dateTime('2026-09-18T09:00'), hint: 'consistency check · finished with 4 warnings' }]));
        box.appendChild(sec('sources', 'Data sources', src.state, src.value, badSrc.length ? F.plural(badSrc.length, { one: 'source needs', other: 'sources need' }) + ' attention; values from the others arrive on schedule.' : 'All sources answer and deliver values on schedule.',
          D.sources.map(function (s) { return { label: s.name, value: h('span', { class: 'row wrap' }, E.StatusBadge('health', s.health, { quiet: s.health === 'Healthy' }), h('span', { class: 'muted' }, 'last run ' + F.dateTime(s.lastRun))), hint: s.problem }; }), h('div', h('a', { href: '#/admin/sources' }, 'Open data sources'))));
        box.appendChild(E.Section({ title: 'About this installation', content: h('div', { class: 'panel ops-hsec' }, E.KeyValue([{ label: 'Product version', value: H.application[0][1].split(' · ')[0], mono: true }, { label: 'Build', value: H.application[0][1].split(' · ').slice(1).join(' · ').replace(/^build /, ''), mono: true }, { label: 'Sign-in', value: H.application[1][1] }, { label: 'Interface languages', value: H.application[2][1] }, { label: 'Running without restart', value: H.application[3][1] },
          { label: 'Log folder', value: withCopy('D:\\ECR\\logs', 'log folder path') }, { label: 'Last error', value: lastErr ? h('a', { href: '#/admin/jobs?panel=' + lastErr.id }, 'Today, ' + lastErr.started + ' · ' + lastErr.code) : null, hint: lastErr ? lastErr.type + ' · ' + lastErr.target : null }, { label: 'Checked', value: F.dateTime(HEALTH.checked) }], { wide: true })) }));
        return box; } }));
    ctx.proto({ id: 'hl-case', label: 'Health case', value: lowPart ? 'partitions' : 'normal', options: [{ value: 'normal', label: 'Normal' }, { value: 'partitions', label: 'Low partitions' }], onChange: function (v) { HEALTH.partitionsFixed = false; E.go('/admin/health', { params: { 'case': v === 'partitions' ? 'partitions' : null } }); } });
    function fixed() { return { title: '12 monthly partitions created', text: 'Partitions now exist until November 2027. Nothing else to do — the warning is gone.', actions: [{ label: 'Copy diagnostics', onClick: function () { copy(diagnostics(), 'diagnostics'); } }] }; }
    ctx.dialog('create-partitions', function () { E.ConfirmDialog({ id: 'hl-part', title: 'Create 12 more monthly partitions?', danger: false, consequences: [{ text: 'Adds empty partitions for December 2026 – November 2027 in the DATA_HOT storage group.', note: true }, { text: 'Takes a few seconds. Nobody is blocked and no data moves.', note: true }], verb: 'Create partitions', icon: 'plus', onConfirm: function () { HEALTH.partitionsFixed = true; res.flash(fixed); } }); });
    ctx.dialog('result-partitions', function () { res.show(fixed()); });
    ctx.action('Copy diagnostics', 'copy', function () { copy(diagnostics(), 'diagnostics'); });
  } });

  /* ================= СЦЕНАРІЇ ================= */
  var SECU = '#/admin/security', PERI = '#/admin/periods', JOBS = '#/admin/jobs';
  E.flow('ops-role-create-dangerous', { group: 'Access', title: 'Create a role with a dangerous permission', actor: 'System administrator', note: 'Wizard: назва → права з пошуком → обов’язкове підтвердження небезпечних → Review.', steps: [
    { label: 'Security · roles (built-in is read-only)', href: SECU }, { label: 'Dangerous permissions of a role — shield with the full list', href: SECU + '?role=Approver' }, { label: 'New role — step 1: name', href: SECU + '?dialog=new-role' }, { label: 'Step 2: permissions with search', href: SECU + '?dialog=new-role&step=1' }, { label: 'Step 3: dangerous permissions — mandatory confirmation', href: SECU + '?dialog=new-role&step=2' }, { label: 'Step 4: review', href: SECU + '?dialog=new-role&step=3' },
    { label: 'Saving a dangerous permission into a custom role — confirmation', href: SECU + '?role=NightShiftEntry&dialog=save-role-dangerous' }, { label: 'Result: role is ready · what next', href: SECU + '?dialog=result-role-created' }, { label: 'Compare roles — transposed matrix', href: SECU + '?view=compare' }, { label: 'Compare three roles — only differences', href: SECU + '?view=compare&diff=1&roles=DataEntry,NightShiftEntry,Viewer' }] });
  E.flow('ops-role-grant', { group: 'Access', title: 'Give a role and access to a resource', actor: 'System administrator', steps: [
    { label: 'Security · roles', href: SECU }, { label: 'Assign a role (dangerous permissions are named)', href: SECU + '?tab=users&dialog=grant-role' }, { label: 'Grants', href: SECU + '?tab=grants' }, { label: 'Grant access: who · resource · level · consequences', href: SECU + '?tab=grants&dialog=new-grant' },
    { label: 'Result: access granted · what next', href: SECU + '?tab=grants&dialog=result-granted' }, { label: 'Grant details (levels, members)', href: SECU + '?tab=grants&panel=gr-01' }, { label: 'Revoke — consequences', href: SECU + '?tab=grants&dialog=revoke-grant' }] });
  E.flow('ops-user-effective-rights', { group: 'Access', title: 'Why can this person do that? — effective rights', actor: 'System administrator', steps: [
    { label: 'Users', href: SECU + '?tab=users' }, { label: 'M. Petrenko: roles, AD groups, effective rights with the chain role → grant → resource', href: SECU + '?tab=users&panel=u-04' }, { label: 'A local account without groups', href: SECU + '?tab=users&panel=u-09' }, { label: 'The grant behind the level', href: SECU + '?tab=grants&panel=gr-01' }] });
  E.flow('ops-user-simulate', { group: 'Access', title: 'See ECR as another user', actor: 'System administrator', steps: [
    { label: 'User details', href: SECU + '?tab=users&panel=u-02' }, { label: 'Simulate this user — what it means', href: SECU + '?tab=users&panel=u-02&dialog=simulate-user' }, { label: 'Documents as D. Akhmetova — banner on the whole shell', href: '#/?as=DataEntry&sim=u-02' }, { label: 'The banner follows to other screens', href: '#/documents/DOC-000001?as=DataEntry&sim=u-02' }, { label: 'Exit simulation → back to Users', href: SECU + '?tab=users&as=SystemAdministrator' }] });
  E.flow('ops-user-timed-assignment', { group: 'Access', title: 'Temporary role for a colleague on leave', actor: 'System administrator', steps: [
    { label: 'Users · “with a role ending ≤ 14 days”', href: SECU + '?tab=users' }, { label: 'Assign a role for a limited time', href: SECU + '?tab=users&dialog=grant-role' }, { label: 'Result: assigned until a date', href: SECU + '?tab=users&dialog=result-role-assigned' }, { label: 'M. Petrenko — Approver until 30 Sep, ends by itself', href: SECU + '?tab=users&panel=u-04' }] });
  E.flow('ops-user-password-lock', { group: 'Access', title: 'Locked local account → unlock or reset the password', actor: 'System administrator', steps: [
    { label: 'Locked user', href: SECU + '?tab=users&panel=u-09' }, { label: 'Reset password — consequences', href: SECU + '?tab=users&panel=u-09&dialog=reset-password' }, { label: 'One-time password, shown once', href: SECU + '?tab=users&dialog=result-password' }, { label: 'Lock an active account', href: SECU + '?tab=users&dialog=lock-user' }, { label: 'New local user', href: SECU + '?tab=users&dialog=new-user' }] });
  E.flow('ops-period-open-close', { group: 'Access', title: 'Close a period and open the next one', actor: 'Period administrator', steps: [
    { label: 'Periods — the year of a project', href: PERI }, { label: 'September: dates, policy, documents', href: PERI + '?panel=2026-09' }, { label: 'Close now — what is left unsubmitted', href: PERI + '?dialog=close-period' }, { label: 'Result: closed · what next', href: PERI + '?dialog=result-closed' },
    { label: 'Open October — dates', href: PERI + '?dialog=open-period' }, { label: 'Open October — policy', href: PERI + '?dialog=open-period&step=1' }, { label: 'Open October — review', href: PERI + '?dialog=open-period&step=2' }, { label: 'Result: opened · what next', href: PERI + '?dialog=result-opened' }, { label: 'Archive — step 1', href: PERI + '?dialog=archive-period' }, { label: 'Archive — step 2, type the name', href: PERI + '?dialog=archive-period-confirm' }, { label: 'Result: archiving in the background', href: PERI + '?dialog=result-archived' }] });
  E.flow('ops-period-reopen-late', { group: 'Access', title: 'Reopen a closed period — edits become late', actor: 'Period administrator', steps: [
    { label: 'August is closed', href: PERI + '?panel=2026-08' }, { label: 'Reopen: reason, until when, warning about late edits', href: PERI + '?dialog=reopen-period' }, { label: 'Result: reopened until the end of day', href: PERI + '?dialog=result-reopened' }, { label: 'A project in grace', href: PERI + '?project=P07131200&panel=2026-08' }, { label: 'Late edits in the audit trail', href: '#/admin/audit' }, { label: 'The old snapshot becomes superseded', href: '#/admin/snapshots?panel=SN-0026' }] });
  E.flow('ops-job-retry', { group: 'Operations', title: 'A job fails → find out why → retry', actor: 'Any user, then administrator', steps: [
    { label: 'My tasks shows the failure', href: '#/documents/DOC-000001?panel=tasks' }, { label: 'Jobs', href: JOBS }, { label: 'Failed job: why, what to do, steps, 3 attempts, correlation id', href: JOBS + '?panel=J-10427' }, { label: 'Retry — inline confirmation', href: JOBS + '?panel=J-10427&dialog=retry-job' }, { label: 'Result: restarted · what next', href: JOBS + '?dialog=result-retried' }] });
  E.flow('ops-job-cancel', { group: 'Operations', title: 'Cancel a running job', actor: 'Administrator', steps: [
    { label: 'Jobs', href: JOBS }, { label: 'Running job', href: JOBS + '?panel=J-10428' }, { label: 'Cancel — what happens to the partial result', href: JOBS + '?panel=J-10428&dialog=cancel-job' }, { label: 'Nothing is running (empty state)', href: JOBS + '?state=empty' }] });
  E.flow('ops-snapshot', { group: 'Operations', title: 'Make a snapshot and compare it with the previous one', actor: 'Approver', steps: [
    { label: 'Report snapshots', href: '#/admin/snapshots' }, { label: 'New snapshot — report → readiness → review', href: '#/admin/snapshots?dialog=new-snapshot' }, { label: 'Result: being made', href: '#/admin/snapshots?dialog=result-snapshot' }, { label: 'Snapshot v2: contents, checksum, was → now', href: '#/admin/snapshots?panel=SN-0030' }, { label: 'Superseded v1', href: '#/admin/snapshots?panel=SN-0026' }] });
  E.flow('ops-audit-cell-history', { group: 'Operations', title: 'Who changed this number?', actor: 'Auditor', steps: [
    { label: 'Audit trail · data changes', href: '#/admin/audit' }, { label: 'Custom date range', href: '#/admin/audit?dialog=date-range' }, { label: 'Full history of one cell', href: '#/admin/audit?panel=CH-58210' }, { label: 'Jump to the cell in the document', href: '#/documents/DOC-000001?sheet=0&table=2' }, { label: 'Structure changes with reasons', href: '#/admin/audit?tab=structure' }, { label: 'Nothing found — advice to relax filters', href: '#/admin/audit?state=empty' }] });
  E.flow('ops-consistency-ack', { group: 'Operations', title: 'Nightly check found a mismatch', actor: 'Administrator', steps: [
    { label: 'Consistency issues', href: '#/admin/consistency' }, { label: 'Finding: what it means and what to do', href: '#/admin/consistency?panel=CI-101' }, { label: 'Acknowledge with a note', href: '#/admin/consistency?panel=CI-101&dialog=ack-issue' }, { label: 'Result: acknowledged · what next', href: '#/admin/consistency?dialog=result-acknowledged' }, { label: 'Run check now', href: '#/admin/consistency?dialog=run-check' }, { label: 'Result: check is running', href: '#/admin/consistency?dialog=result-check-started' }, { label: 'Nothing found', href: '#/admin/consistency?state=empty' }] });
  E.flow('ops-uistrings-translate', { group: 'Operations', title: 'Translate interface texts', actor: 'Administrator', steps: [
    { label: 'Interface texts — honest coverage', href: '#/admin/ui-strings' }, { label: 'Edit a string: length and placeholders', href: '#/admin/ui-strings?panel=sheet.submit' }, { label: 'Broken placeholder is rejected', href: '#/admin/ui-strings?dialog=edit-string' }, { label: 'Kazakh · public strings', href: '#/admin/ui-strings?lang=kk&scope=Public' }, { label: 'Import a catalogue — file', href: '#/admin/ui-strings?dialog=import-strings' }, { label: 'Import — diff of changes', href: '#/admin/ui-strings?dialog=import-strings&step=1' }, { label: 'Import — review', href: '#/admin/ui-strings?dialog=import-strings&step=2' }, { label: 'Result: imported · what next', href: '#/admin/ui-strings?dialog=result-imported' }, { label: 'Export', href: '#/admin/ui-strings?dialog=export-strings' }] });
  E.flow('ops-health-warning', { group: 'Operations', title: 'Health shows a warning → fix it', actor: 'Administrator', steps: [
    { label: 'Health — three parts, one sentence each', href: '#/admin/health' }, { label: 'Warning: partitions ahead', href: '#/admin/health?case=partitions' }, { label: 'Create partitions — confirmation', href: '#/admin/health?case=partitions&dialog=create-partitions' }, { label: 'Result: fixed', href: '#/admin/health?dialog=result-partitions' }, { label: 'Last error → the failed job', href: JOBS + '?panel=J-10412' }] });
})(window.ECR);

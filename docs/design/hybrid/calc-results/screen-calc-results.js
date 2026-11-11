/* =====================================================================
   screen-calc-results.js — /documents/:id/results · «Calculation results»: усі розраховані значення документа за РІК,
   місяці — колонками Jan…Dec. Слово людини 11.11: «переробити цю таблицю щоб вона залежала від того як сконфігурований
   шаблон і відображення результатів має читатися легко та зрозуміло. І треба бачити всі розрахунки за всі місяці».
   kit.js / index.html / KIT.md НЕ змінюються: файл підключає screen-document.js (document.write), гачки — ECR.cr.*.
   Рядки: аркуш → таблиця → обладнання (назва + IDCode) · методологія → вихід (речовина, одиниця).
   Залежність від розкладки шаблону (D-474):
     layout=months (PerMonthCopies)        — «огляд року»; перемикач місяця в документі лишається; заголовок місяця відкриває документ на цьому місяці;
     layout=year   (YearWithMonthColumns)  — єдине місце, де видно розраховане за всі місяці; у сітці документа розраховані колонки
                                             показують «місяць результатів», обраний тут або чипом у смузі таблиці (варіант A, рекомендований).
   ?panel=  : cell-<рядок>-<місяць 1..12>    ?dialog= : export-results · result-exported · recalc-stale
   Параметри: layout=months|year · month=2026-MM (місяць результатів) · sep=stale|current|norun (демо-стан вересня) · state=loading|error|empty
   Опис і серверний план: CALC-RESULTS.md.
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, icon = E.icon, F = E.fmt, D = E.data;
  var YEAR = 2026, MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  function pid(m) { return YEAR + '-' + (m < 10 ? '0' : '') + m; }
  function num(v, d) { return F.number(v, d == null ? 3 : d); }

  E.css('cr', [
    '.cr-page{display:flex;flex-direction:column;min-height:0;height:100%}',
    '.cr-page .cr-top{padding:var(--s3) var(--s4) 0;flex:none}',
    '.cr-page .cr-legend{display:flex;flex-wrap:wrap;align-items:center;gap:var(--s3);padding:var(--s2) var(--s4);color:var(--muted);font-size:var(--fs-xs);border-bottom:1px solid var(--border);flex:none}',
    '.cr-page .cr-legend .lg{display:inline-flex;align-items:center;gap:var(--s1);white-space:nowrap}',
    '.cr-page .cr-legend .sw{display:inline-block;width:14px;height:14px;border:1px solid var(--border);border-radius:var(--r1)}',
    '.cr-page .cr-legend .sw.stale{background:var(--warning-soft)}',
    '.cr-page .cr-legend .sw.none{background-image:repeating-linear-gradient(135deg,var(--hatch) 0 1.5px,transparent 1.5px 7px)}',
    '.cr-page .cr-legend .sw.pick{background:var(--accent-soft);border-color:var(--accent)}',
    '.cr-page .cr-legend .sp{flex:1}',
    '.cr-page .cr-wrap{flex:1;min-height:0;overflow:auto;background:var(--surface)}',
    '.cr-page table.cr-grid{border-collapse:separate;border-spacing:0;font-size:var(--fs-sm);min-width:100%}',
    '.cr-page .cr-grid th,.cr-page .cr-grid td{height:var(--row);padding:0 var(--s2);border-bottom:1px solid var(--grid-line);border-right:1px solid var(--grid-line);white-space:nowrap}',
    '.cr-page .cr-grid thead th{position:sticky;top:0;z-index:3;background:var(--sunken);font-weight:500;text-align:right;height:44px;vertical-align:middle;border-bottom:1px solid var(--border-strong)}',
    '.cr-page .cr-grid thead th.cr-out{left:0;z-index:5;text-align:left;min-width:300px;max-width:300px}',
    '.cr-page .cr-grid thead th.cr-unit{left:300px;z-index:5;text-align:left;min-width:84px;border-right:1px solid var(--border-strong)}',
    '.cr-page .cr-grid thead th.cr-m{min-width:92px;padding:0}',
    '.cr-page .cr-grid thead th.cr-m .mh{display:flex;flex-direction:column;align-items:flex-end;justify-content:center;gap:2px;width:100%;height:100%;padding:0 var(--s2);border:0;background:transparent;color:var(--text);font:inherit;text-decoration:none}',
    '.cr-page .cr-grid thead th.cr-m .mh:hover{background:var(--hover)}',
    '.cr-page .cr-grid thead th.cr-m .mh small{display:inline-flex;align-items:center;gap:2px;font-size:var(--fs-xs);font-weight:400;color:var(--muted)}',
    '.cr-page .cr-grid thead th.cr-m.stale .mh small{color:var(--warning)}',
    '.cr-page .cr-grid thead th.cr-m.pick{background:var(--accent-soft);box-shadow:inset 0 -2px 0 var(--accent)}',
    '.cr-page .cr-grid thead th.cr-tot{min-width:120px;border-left:1px solid var(--border-strong);text-align:right}',
    '.cr-page .cr-grid thead th.cr-tot small{display:block;font-size:var(--fs-xs);font-weight:400;color:var(--muted)}',
    '.cr-page .cr-grid tbody th{position:sticky;left:0;z-index:2;background:var(--surface);text-align:left;font-weight:400;min-width:300px;max-width:300px;overflow:hidden;text-overflow:ellipsis}',
    '.cr-page .cr-grid tbody td.cr-unit{position:sticky;left:300px;z-index:2;background:var(--surface);color:var(--muted);border-right:1px solid var(--border-strong);min-width:84px}',
    '.cr-page .cr-grid tr.band th{background:var(--sunken);max-width:none;overflow:visible;z-index:2}',
    '.cr-page .cr-grid tr.band td{background:var(--sunken);border-right:0}',
    '.cr-page .cr-grid tr.band.l0 th{font-weight:600;font-size:var(--fs-md)}',
    '.cr-page .cr-grid tr.band.l1 th{font-weight:600;padding-left:var(--s4)}',
    '.cr-page .cr-grid tr.band.l2 th{padding-left:var(--s3)}',
    '.cr-page .cr-grid tr.band .tg{display:inline-flex;align-items:center;gap:var(--s1);border:0;background:transparent;color:var(--text);font:inherit;padding:0;height:var(--row)}',
    '.cr-page .cr-grid tr.band .tg[aria-expanded="true"]>svg:first-child{transform:rotate(90deg)}',
    '.cr-page .cr-grid tr.band .eq{font-weight:500}',
    '.cr-page .cr-grid tr.band .mt{color:var(--muted);margin-left:var(--s2)}',
    '.cr-page .cr-grid tr.out th{padding-left:calc(var(--s5) + var(--s4))}',
    '.cr-page .cr-grid tr.out th .fm{font-weight:500;margin-right:var(--s1)}',
    '.cr-page .cr-grid tr.out th .nm{color:var(--muted)}',
    '.cr-page .cr-grid td.v{padding:0;text-align:right}',
    '.cr-page .cr-grid td.v .vb{display:flex;align-items:center;justify-content:flex-end;gap:var(--s1);width:100%;height:var(--row);padding:0 var(--s2);border:0;background:transparent;color:var(--text);font:400 var(--fs-sm)/1 var(--mono);font-variant-numeric:tabular-nums}',
    '.cr-page .cr-grid td.v .vb:hover{background:var(--hover)}',
    '.cr-page .cr-grid td.v .vb:focus-visible{outline:2px solid var(--focus);outline-offset:-2px}',
    '.cr-page .cr-grid td.v.stale{background:var(--warning-soft)}',
    '.cr-page .cr-grid td.v.warn .vb{color:var(--warning);font-family:var(--sans)}',
    '.cr-page .cr-grid td.v.bad .vb{color:var(--danger);font-family:var(--sans)}',
    '.cr-page .cr-grid td.v.none{background-image:repeating-linear-gradient(135deg,var(--hatch) 0 1.5px,transparent 1.5px 7px);color:var(--faint);padding:0 var(--s2)}',
    '.cr-page .cr-grid td.v.pick{box-shadow:inset 2px 0 0 var(--accent),inset -2px 0 0 var(--accent)}',
    '.cr-page .cr-grid td.tot{text-align:right;font:500 var(--fs-sm)/1 var(--mono);font-variant-numeric:tabular-nums;border-left:1px solid var(--border-strong);background:var(--calc-bg)}',
    '.cr-page .cr-grid td.tot.na{color:var(--faint);font-family:var(--sans);font-weight:400}',
    '.cr-page .cr-grid td.tot small{color:var(--muted);font:400 var(--fs-xs)/1 var(--sans);margin-left:var(--s1)}',
    '.cr-page .cr-grid td.tot.warn small{color:var(--warning)}',
    '.cr-page .cr-foot{display:flex;align-items:center;gap:var(--s3);height:32px;padding:0 var(--s4);border-top:1px solid var(--border-strong);background:var(--sunken);color:var(--muted);font-size:var(--fs-xs);flex:none}',
    '.cr-page .cr-foot .sp{flex:1}',
    '.cr-chip{display:inline-flex;align-items:center;gap:var(--s1);white-space:nowrap}',
    '.cr-inputs{width:100%;border-collapse:collapse;font-size:var(--fs-sm)}',
    '.cr-inputs td{padding:var(--s1) var(--s2) var(--s1) 0;border-bottom:1px solid var(--grid-line)}',
    '.cr-inputs td.n{text-align:right;font-family:var(--mono)}'
  ].join(''));

  /* ================= демо-модель: лише похідні від ECR.data ================= */
  var SUB = {}; (D.registryEntries && D.registryEntries.Substances || []).forEach(function (s) { SUB[s.formula] = s.name; });
  function sub(f) { return { formula: f, name: SUB[f] || f }; }
  var METH = {}; (D.methodologies || []).forEach(function (m) { METH[m.id] = m; });
  function meth(id, fallback) { var m = METH[id]; return { id: id, name: m ? m.name : fallback, version: m && m.version ? m.version : '1.0.0' }; }
  /* output: [код, речовина|null, підпис без речовини, одиниця, адитивний, база, знаків] */
  var MODEL = [
    { sheet: 0, tables: [
      { t: 1, no: '1.2', name: 'Combustion sources', m: meth('M-01', 'Stationary combustion — emission factors'), rows: ['Boiler B-1', 'Boiler B-2', 'Gas turbine GT-1', 'Process heater H-201'],
        outs: [['E_SO2', 'SO₂', null, 't', true, 1.84, 3], ['E_NOX', 'NOₓ', null, 't', true, 6.32, 3], ['E_CO', 'CO', null, 't', true, 2.11, 3], ['G_NOX', 'NOₓ', 'max emission rate', 'g·s⁻¹', false, 2.44, 3], ['RHO', null, 'Fuel gas density', 'kg·m⁻³', false, 0.742, 4]] },
      { t: 5, no: '1.6', name: 'Flares', m: meth('M-02', 'Flaring — mass balance'), rows: ['Flare stack FS-101', 'Flare stack FS-102'],
        outs: [['E_CH4', 'CH₄', null, 't', true, 3.47, 3], ['E_VOC', 'VOC', null, 't', true, 1.12, 3], ['E_H2S', 'H₂S', null, 't', true, 0.058, 4], ['EFF', null, 'Combustion efficiency', '%', false, 98.1, 2]] },
      { t: 8, no: '1.9', name: 'Storage tanks', m: meth('M-03', 'Storage tanks — VOC losses'), rows: ['Storage tank T-401', 'Storage tank T-402', 'Storage tank T-403'],
        outs: [['E_VOC', 'VOC', null, 't', true, 0.624, 4], ['E_C6H6', 'C₆H₆', null, 't', true, 0.0031, 5], ['K_T', null, 'Turnover factor', '—', false, 0.86, 3]] } ] },
    { sheet: 3, tables: [
      { t: 0, no: '4.1', name: 'Direct GHG emissions', m: meth('M-04', 'GHG — IPCC 2006 tier 2'), rows: ['Boiler B-1', 'Gas turbine GT-1', 'Flare stack FS-101'],
        outs: [['GHG_CO2', 'CO₂', null, 't', true, 4180, 1], ['GHG_CH4', 'CH₄', 'as CO₂e', 't CO₂e', true, 96.4, 2], ['GHG_N2O', 'N₂O', 'as CO₂e', 't CO₂e', true, 21.7, 2], ['EF_CO2', null, 'CO₂ emission factor', 't·TJ⁻¹', false, 56.1, 2]] } ] }
  ];
  function esCode(name) { var l = (D.registryEntries && D.registryEntries.EmissionSources) || [], i; for (i = 0; i < l.length; i++) if (l[i].name === name) return l[i].id; var n = 0; for (i = 0; i < name.length; i++) n = (n * 31 + name.charCodeAt(i)) % 900; return 'ES-' + ('00' + (n + 40)).slice(-3); }

  /* стан місяця: closed|open|notOpened × run: current|stale|norun|none */
  function months(sep) {
    return MON.map(function (nm, i) { var m = i + 1, p = D.period ? D.period(pid(m)) : null, st = p ? p.state : (m < 9 ? 'Closed' : m === 9 ? 'Open' : 'NotOpened');
      var run = st === 'NotOpened' ? 'none' : m === 9 ? (sep || 'stale') : 'current';
      return { m: m, id: pid(m), name: nm, state: st, run: run, ran: run === 'none' || run === 'norun' ? null : (m === 9 ? '14 Sep 2026, 09:12' : (m < 9 ? '0' + (m % 5 + 2) : '02') + ' ' + MON[m % 12] + ' 2026, 08:40') }; });
  }
  /* плоский перелік рядків-виходів із детермінованими значеннями */
  function build(ms) {
    var rows = [], k = 0;
    MODEL.forEach(function (S) { S.tables.forEach(function (T) { T.rows.forEach(function (eq, ei) { T.outs.forEach(function (o, oi) {
      var R = E.rng(1000 + S.sheet * 97 + T.t * 31 + ei * 7 + oi), base = o[5] * (0.7 + R() * 0.7), key = 'r' + (k++), cells = [];
      ms.forEach(function (mo) {
        var v = base * (0.86 + R() * 0.28), c = { v: +v.toFixed(o[6]), st: mo.run === 'none' || mo.run === 'norun' ? 'none' : mo.run };
        if (c.st === 'none') c.v = null;
        if (eq === 'Storage tank T-403' && mo.m < 6) { c.v = null; c.st = 'absent'; }                                     /* рядок додано в червні */
        if (eq === 'Boiler B-2' && mo.m === 9 && mo.run !== 'norun' && oi < 4) { c.v = null; c.st = 'missing'; }           /* немає обов'язкового входу */
        if (eq === 'Flare stack FS-102' && mo.m === 9 && mo.run !== 'norun' && o[0] === 'EFF') { c.v = null; c.st = 'error'; } /* помилка розрахунку */
        cells.push(c);
      });
      rows.push({ key: key, sheet: S.sheet, sheetName: D.sheetNames[S.sheet], t: T.t, no: T.no, table: T.name, meth: T.m, eq: eq, code: esCode(eq), out: o[0], sub: o[1] ? sub(o[1]) : null, label: o[2], unit: o[3], add: o[4], scale: o[6], cells: cells });
    }); }); }); });
    return rows;
  }
  function total(r) { var s = 0, n = 0, stale = false; r.cells.forEach(function (c) { if (c.v != null) { s += c.v; n++; if (c.st === 'stale') stale = true; } }); return { sum: s, n: n, stale: stale }; }
  function problem(r) { return r.cells.some(function (c) { return c.st === 'missing' || c.st === 'error'; }); }   /* «out of date» — стан місяця, а не рядка */
  function latestCurrent(ms) { var b = null; ms.forEach(function (m) { if (m.run === 'current' || m.run === 'stale') b = m; }); return b || ms[0]; }

  /* ================= гачки для екрана документа (screen-document.js) ================= */
  E.cr = {
    href: function (docId, q) { return E.href('/documents/' + docId + '/results', q || {}); },
    moreItems: function (docId, layout) { return [{ label: 'Calculation results — all months', icon: 'table', href: E.cr.href(docId, layout === 'year' ? { layout: 'year' } : {}) }]; },
    /* смуга таблиці документа: посилання «All months»; у річній розкладці — «Year 2026» замість перемикача місяця і чип місяця результатів */
    decorate: function (c, root, api) {
      var bar = root.querySelector('.tablebar'), sp = bar && bar.querySelector('.sp'); if (!bar || !sp) return;
      ['#doc-cr-all', '#doc-cr-month', '#doc-cr-year'].forEach(function (s) { var n = root.querySelector(s); if (n) n.remove(); });
      var year = c.query.layout === 'year', ms = months(c.query.sep), cur = c.query.month ? ms[+c.query.month.slice(5) - 1] : latestCurrent(ms);
      if (year) {
        var slot = root.querySelector('#doc-period-slot');
        if (slot) { slot.hidden = true; slot.parentNode.insertBefore(h('span', { class: 'chip cr-chip', id: 'doc-cr-year', title: 'This template shows the whole year on one sheet: months are columns. There is no month switch.' }, icon('calendar', 12), 'Year ' + YEAR), slot); }
        bar.insertBefore(E.Menu({ label: 'Calculated: ' + cur.name + ' ' + YEAR, id: 'doc-cr-month', items: [{ heading: 'Calculated columns show results of' }].concat(ms.filter(function (m) { return m.run === 'current' || m.run === 'stale'; }).map(function (m) {
          return { label: F.period(m.id), hint: m.run === 'stale' ? 'out of date' : (m.id === cur.id ? 'shown now' : null), onClick: function () { c.setQuery({ month: m.id }); E.Toast('Calculated columns now show ' + F.period(m.id)); E.cr.decorate(c, root, api); } }; })) }), sp.nextSibling);
      }
      bar.insertBefore(E.Button({ label: 'All months', icon: 'table', variant: 'ghost', size: 'sm', id: 'doc-cr-all', title: 'Calculation results of this document for every month of ' + YEAR, href: E.cr.href(api.docId(), year ? { layout: 'year', month: cur.id } : {}) }), sp.nextSibling);
    }
  };

  /* ================= екран ================= */
  E.screen('/documents/:id/results', { title: 'Calculation results', navPath: '/', example: '/documents/DOC-000001/results', render: function (el, c) {
    var doc = D.doc(c.params.id), docHref = '#/documents/' + c.params.id;
    if (!doc) { el.appendChild(h('div', { class: 'page' }, E.ErrorState({ title: 'This document could not be loaded', code: 'ECR-DOC-0404', back: { label: 'Documents', href: '#/' } }))); return; }
    var year = c.query.layout === 'year', ms = months(c.query.sep), all = build(ms), collapsed = (c.saved.collapsed = c.saved.collapsed || {}), onlyProblems = !!c.saved.problems;
    var pick = c.query.month && /^\d{4}-\d{2}$/.test(c.query.month) ? +c.query.month.slice(5) : (year ? latestCurrent(ms).m : 0);
    c.setTitle('Calculation results · ' + doc.id);
    if (c.setCrumbs) c.setCrumbs([{ label: 'Documents', href: '#/' }, { label: doc.id, href: docHref }, { label: 'Calculation results' }]);
    var back = { label: doc.id, href: docHref + (year ? '?layout=year' : '') };
    var stale = ms.filter(function (m) { return m.run === 'stale' || m.run === 'norun'; });

    var head = E.PageHeader({ ctx: c, id: 'cr', title: 'Calculation results', back: back,
      subtitle: doc.id + ' · ' + doc.facility + ' · ' + YEAR + ' — every calculated value of the document, months side by side.',
      secondary: stale.length ? [{ label: 'Recalculate ' + stale.map(function (m) { return m.name; }).join(', '), icon: 'refresh', id: 'cr-recalc', onClick: function () { c.openDialog('recalc-stale'); } }] : [],
      more: [{ label: 'Collapse all groups', icon: 'chevR', onClick: function () { all.forEach(function (r) { collapsed[grp(r)] = true; }); paint(); } }, { label: 'Expand all groups', icon: 'chevR', onClick: function () { Object.keys(collapsed).forEach(function (k) { delete collapsed[k]; }); paint(); } }],
      primary: { label: 'Export to Excel', icon: 'download', id: 'cr-export', onClick: function () { c.openDialog('export-results'); } } });
    var page = h('div', { class: 'cr-page' }), top = h('div', { class: 'cr-top' }, head), result = h('div', { id: 'cr-result' }); top.appendChild(result); page.appendChild(top); el.appendChild(page);

    if (c.state !== 'data') { page.appendChild(h('div', { class: 'page' }, E.StateSwitch(c.state, { loading: E.Skeleton({ kind: 'table', rows: 14, cols: 9 }), error: { title: 'Calculation results could not be loaded', code: 'ECR-CALC-0503', back: back },
      empty: { icon: 'table', title: 'Nothing has been calculated yet', text: 'This document has no calculation run for ' + YEAR + '. Results appear here after the first recalculation of any month.', actions: [{ label: 'Open the document', href: docHref }] } }))); return; }

    if (year) top.appendChild(E.Banner({ flush: true, icon: 'info', id: 'cr-year-note', title: 'This template shows the year on one sheet', text: 'Monthly inputs are columns of the sheet, so the document has no month switch. Calculated columns in the sheet show one “results month” (' + F.period(pid(pick)) + '); this page shows all of them. Click a month heading to change it.' }));

    var uniq = function (f) { var s = {}, o = []; all.forEach(function (r) { var v = f(r); if (v && !s[v[0]]) { s[v[0]] = 1; o.push({ value: v[0], label: v[1] }); } }); return o; };
    var probBtn = E.Button({ label: 'Problems only', icon: 'alert', id: 'cr-problems', pressed: onlyProblems, onClick: function (e, b) { onlyProblems = !onlyProblems; c.saved.problems = onlyProblems; b.setAttribute('aria-pressed', String(onlyProblems)); paint(); } });
    var fb = E.FilterBar({ id: 'cr-f', memory: c.saved, search: { placeholder: 'Equipment or ID code' }, right: probBtn, onChange: function () { paint(); },
      filters: [{ id: 'sheet', label: 'Sheets', options: uniq(function (r) { return [String(r.sheet), r.sheetName]; }) }, { id: 'meth', label: 'Methodologies', options: uniq(function (r) { return [r.meth.id, r.meth.name]; }) }, { id: 'sub', label: 'Substances', options: uniq(function (r) { return r.sub ? [r.sub.formula, r.sub.formula + ' · ' + r.sub.name] : null; }) }] });
    top.appendChild(fb);

    page.appendChild(h('div', { class: 'cr-legend', id: 'cr-legend' },
      h('span', { class: 'lg' }, icon('lock', 12), 'closed month'), h('span', { class: 'lg' }, h('i', { class: 'sw stale' }), 'out of date — inputs changed after the run'),
      h('span', { class: 'lg' }, h('i', { class: 'sw none' }), 'no run / not opened'), h('span', { class: 'lg' }, icon('alert', 12), 'required input missing'), h('span', { class: 'lg' }, icon('alertCircle', 12), 'calculation error'),
      year ? h('span', { class: 'lg' }, h('i', { class: 'sw pick' }), 'results month shown in the sheet') : null,
      h('span', { class: 'sp' }), h('span', { class: 'lg', title: 'Rates, densities, factors and percentages are not summed.' }, 'Σ Year total only for additive outputs (t, t CO₂e)')));

    var wrap = h('div', { class: 'cr-wrap', id: 'cr-grid', tabindex: '0', role: 'region', 'aria-label': 'Calculation results by month' }), foot = h('div', { class: 'cr-foot', id: 'cr-foot' });
    page.appendChild(wrap); page.appendChild(foot);

    function grp(r) { return r.sheet + '/' + r.t + '/' + r.eq; }
    function srcHref(r, m) { return E.href('/documents/' + doc.id, year ? { layout: 'year', sheet: r.sheet, table: r.t, month: pid(m) } : { sheet: r.sheet, table: r.t, period: pid(m) }); }
    function monthHead(mo) {
      var cls = 'cr-m' + (mo.run === 'stale' ? ' stale' : '') + (pick === mo.m ? ' pick' : ''), st, tip;
      if (mo.run === 'none') { st = [icon('clock', 10), 'not opened']; tip = F.period(mo.id) + ' is not opened yet'; }
      else if (mo.run === 'norun') { st = [icon('clock', 10), 'no run']; tip = 'No calculation has run for ' + F.period(mo.id); }
      else if (mo.run === 'stale') { st = [icon('alert', 10), 'out of date']; tip = 'Inputs changed after the run of ' + mo.ran + '. Recalculate to refresh.'; }
      else { st = [mo.state === 'Open' ? null : icon('lock', 10), mo.state === 'Open' ? 'current' : 'closed']; tip = 'Run of ' + mo.ran + (mo.state === 'Open' ? '' : ' · the month is ' + mo.state.toLowerCase()); }
      var inner = [h('span', mo.name), h('small', st)], node;
      if (year) { node = h('button', { class: 'mh', type: 'button', id: 'cr-m-' + mo.m, title: tip + ' — click: show this month in the sheet’s calculated columns', 'aria-pressed': String(pick === mo.m) }, inner);
        node.addEventListener('click', function () { if (mo.run === 'none' || mo.run === 'norun') { E.Toast(tip); return; } pick = mo.m; c.setQuery({ month: mo.id }); paint(); }); }
      else node = h('a', { class: 'mh', id: 'cr-m-' + mo.m, href: E.href('/documents/' + doc.id, { period: mo.id }), title: tip + ' — click: open the document on ' + F.period(mo.id) }, inner);
      return h('th', { class: cls, scope: 'col' }, node);
    }
    function cell(r, i) {
      var cl = r.cells[i], mo = ms[i], pk = pick === mo.m ? ' pick' : '';
      if (cl.st === 'none') return h('td', { class: 'v none' + pk, title: mo.run === 'norun' ? 'No run for ' + F.period(mo.id) : F.period(mo.id) + ' is not opened yet' }, '');
      if (cl.st === 'absent') return h('td', { class: 'v none' + pk, title: 'This row did not exist in ' + F.period(mo.id) + ' (added 3 Jun 2026)' }, '');
      var cls = 'v' + (cl.st === 'stale' ? ' stale' : cl.st === 'missing' ? ' warn' : cl.st === 'error' ? ' bad' : '') + pk, kids, tip = (r.sub ? r.sub.formula + ' ' : '') + (r.label || '') + ' · ' + r.eq + ' · ' + F.period(mo.id);
      if (cl.st === 'missing') { kids = [icon('alert', 12), 'no input']; tip += ' — required input is missing'; }
      else if (cl.st === 'error') { kids = [icon('alertCircle', 12), 'error']; tip += ' — calculation error'; }
      else { kids = [num(cl.v, r.scale)]; if (cl.st === 'stale') tip += ' — out of date'; }
      var b = h('button', { class: 'vb', type: 'button', id: 'cr-c-' + r.key + '-' + mo.m, title: tip }, kids);
      b.addEventListener('click', function () { c.openPanel('cell-' + r.key + '-' + mo.m); });
      return h('td', { class: cls }, b);
    }
    function totCell(r) {
      if (!r.add) return h('td', { class: 'tot na', title: 'Not additive (' + r.unit + '): a year total would be meaningless. See the months.' }, '—');
      var t = total(r); if (!t.n) return h('td', { class: 'tot na' }, '—');
      return h('td', { class: 'tot' + (t.stale ? ' warn' : ''), title: 'Sum of ' + t.n + ' of 12 months' + (t.stale ? ' · includes an out-of-date month' : '') }, num(t.sum, r.scale), h('small', t.n + '/12'));
    }
    function band(level, key, content, span) {
      var open = !collapsed[key], btn = h('button', { class: 'tg', type: 'button', 'aria-expanded': String(open), id: 'cr-g-' + key.replace(/[^A-Za-z0-9]+/g, '-') }, icon('chevR', 14), content);
      btn.addEventListener('click', function () { if (collapsed[key]) delete collapsed[key]; else collapsed[key] = true; paint(); });
      return h('tr', { class: 'band l' + level }, h('th', { scope: 'rowgroup', colspan: '2' }, btn), h('td', { colspan: String(span) }, ''));
    }
    function paint() {
      var v = fb.values(), q = (v.q || '').trim().toLowerCase();
      var rows = all.filter(function (r) { return (!v.sheet || String(r.sheet) === v.sheet) && (!v.meth || r.meth.id === v.meth) && (!v.sub || (r.sub && r.sub.formula === v.sub)) && (!q || (r.eq + ' ' + r.code).toLowerCase().indexOf(q) >= 0) && (!onlyProblems || problem(r)); });
      wrap.textContent = '';
      if (!rows.length) { wrap.appendChild(E.EmptyState({ icon: 'search', title: 'No results match the filters', text: 'Try another sheet, methodology or substance.', actions: [{ label: 'Clear filters', onClick: function () { onlyProblems = false; c.saved.problems = false; probBtn.setAttribute('aria-pressed', 'false'); fb.reset(); } }] })); foot.textContent = ''; return; }
      var tb = h('tbody'), ls = null, lt = null, lg = null, groups = 0;
      rows.forEach(function (r) {
        var ks = 's' + r.sheet, kt = ks + '/' + r.t, kg = grp(r);
        if (ks !== ls) { ls = ks; lt = lg = null; tb.appendChild(band(0, ks, 'Sheet: ' + r.sheetName, 13)); }
        if (collapsed[ks]) return;
        if (kt !== lt) { lt = kt; lg = null; tb.appendChild(band(1, kt, [h('span', { class: 'mono' }, r.no), ' ' + r.table], 13)); }
        if (collapsed[kt]) return;
        if (kg !== lg) { lg = kg; groups++; tb.appendChild(band(2, kg, [h('span', { class: 'eq' }, r.eq), h('span', { class: 'chip mono', title: 'ID code' }, r.code), h('span', { class: 'mt' }, r.meth.name + ' · v' + r.meth.version)], 13)); }
        if (collapsed[kg]) return;
        var name = r.sub ? [h('span', { class: 'fm' }, r.sub.formula), h('span', { class: 'nm' }, r.label ? r.sub.name + ', ' + r.label : r.sub.name)] : [h('span', r.label)];
        tb.appendChild(h('tr', { class: 'out' }, h('th', { scope: 'row', title: (r.sub ? r.sub.formula + ' · ' + r.sub.name + (r.label ? ', ' + r.label : '') : r.label) + ' — output ' + r.out }, name), h('td', { class: 'cr-unit' }, r.unit), r.cells.map(function (x, i) { return cell(r, i); }), totCell(r)));
      });
      wrap.appendChild(h('table', { class: 'cr-grid' }, h('thead', h('tr', h('th', { class: 'cr-out', scope: 'col' }, 'Equipment · methodology · output'), h('th', { class: 'cr-unit', scope: 'col' }, 'Unit'), ms.map(monthHead), h('th', { class: 'cr-tot', scope: 'col' }, 'Σ Year ' + YEAR, h('small', 'additive outputs')))), tb));
      var probs = rows.filter(problem).length;
      foot.textContent = ''; E.append(foot, [h('span', rows.length + ' outputs' + (rows.length !== all.length ? ' of ' + all.length : '')), h('span', probs ? probs + ' with problems' : 'no problems'), h('span', { class: 'sp' }), h('span', 'Runs: ' + ms.filter(function (m) { return m.run === 'current'; }).length + ' current · ' + ms.filter(function (m) { return m.run === 'stale'; }).length + ' out of date · ' + ms.filter(function (m) { return m.run === 'none' || m.run === 'norun'; }).length + ' without a run')]);
    }
    paint();

    /* шторка значення: звідки число, з яких входів, перехід до комірки-джерела */
    c.panel('*', function (id) {
      var m = /^cell-(r\d+)-(\d+)$/.exec(id || ''); if (!m) return; var r = all.filter(function (x) { return x.key === m[1]; })[0], mo = ms[+m[2] - 1]; if (!r || !mo) return;
      var cl = r.cells[mo.m - 1], R = E.rng(r.key.length * 131 + mo.m * 17 + r.key.charCodeAt(1));
      var inputs = [['Fuel gas consumption', '10³ m³', 120 + R() * 400, 1], ['Operating hours', 'h', 600 + R() * 140, 0], ['Emission factor', r.unit === 't' ? 'kg·GJ⁻¹' : '—', 0.02 + R() * 0.3, 4]];
      if (cl.st === 'missing') inputs[0][2] = null;
      var title = (r.sub ? r.sub.formula + ' ' : '') + (r.label || (r.sub ? r.sub.name : '')) + ' · ' + mo.name + ' ' + YEAR;
      E.Drawer({ id: 'cr-drawer', title: title, subtitle: r.eq + ' · ' + r.code + ' · table ' + r.no + ' ' + r.table,
        body: function (b) {
          if (cl.st === 'stale') b.appendChild(E.Banner({ tone: 'warning', title: 'Out of date', text: 'Inputs changed after the run of ' + mo.ran + '. The value below is from that run.', actions: [{ label: 'Recalculate ' + mo.name, icon: 'refresh', onClick: function () { c.openDialog('recalc-stale'); } }] }));
          if (cl.st === 'missing') b.appendChild(E.Banner({ tone: 'warning', title: 'Required input is missing', text: '“Fuel gas consumption” for ' + F.period(mo.id) + ' is empty, so ' + r.meth.name + ' did not produce this output.', actions: [{ label: 'Open the input cell', href: srcHref(r, mo.m) }] }));
          if (cl.st === 'error') b.appendChild(E.Banner({ tone: 'danger', title: 'Calculation error', text: 'Division by zero in step 3 (flared volume is 0). Nothing was written for this output.' }));
          b.appendChild(E.KeyValue([{ label: 'Value', value: cl.v == null ? null : num(cl.v, r.scale) + ' ' + r.unit, mono: true }, { label: 'Month', value: F.period(mo.id) + (mo.state === 'Open' ? '' : ' · ' + mo.state.toLowerCase()) },
            { label: 'Methodology', value: r.meth.name, hint: 'version ' + r.meth.version + ' — chosen by the month’s date' }, { label: 'Output', value: r.out, mono: true }, { label: 'Calculated', value: mo.ran }, { label: 'Year total', value: r.add ? num(total(r).sum, r.scale) + ' ' + r.unit + ' (' + total(r).n + ' of 12 months)' : 'not additive' }]));
          b.appendChild(h('h3', { style: 'margin-top:var(--s4)' }, 'Inputs used'));
          b.appendChild(h('table', { class: 'cr-inputs' }, h('tbody', inputs.map(function (x) { return h('tr', h('td', x[0]), h('td', { class: 'n' }, x[2] == null ? h('span', { class: 'faint' }, 'empty') : num(x[2], x[3])), h('td', { class: 'muted' }, x[1])); }))));
        },
        footer: [{ label: 'Open source cell', icon: 'table', variant: 'primary', id: 'cr-open-src', onClick: function () { location.hash = srcHref(r, mo.m); } }],
        onClose: function () { c.setQuery({ panel: null }); } });
    });

    c.dialog('export-results', function () {
      var scope = 'filtered', shape = 'pivot';
      E.Dialog({ id: 'cr-dlg-export', narrow: true, title: 'Export calculation results', body: function (b) {
        b.appendChild(h('p', { class: 'muted' }, doc.id + ' · ' + YEAR + '. The file keeps the grouping, units and month states; out-of-date months are marked in a note row.'));
        b.appendChild(E.Field({ id: 'cr-exp-scope', label: 'Rows', control: E.Segmented({ id: 'cr-exp-scope', label: 'Rows', value: scope, options: [{ value: 'filtered', label: 'As filtered' }, { value: 'all', label: 'Whole document' }], onChange: function (v) { scope = v; } }) }));
        b.appendChild(E.Field({ id: 'cr-exp-shape', label: 'Layout', hint: 'Long list: one line per output and month — convenient for pivot tables.', control: E.Segmented({ id: 'cr-exp-shape', label: 'Layout', value: shape, options: [{ value: 'pivot', label: 'Months as columns' }, { value: 'long', label: 'Long list' }], onChange: function (v) { shape = v; } }) })); },
        footer: [{ label: 'Cancel', id: 'cr-exp-cancel' }, { label: 'Export', icon: 'download', variant: 'primary', autofocus: true, id: 'cr-exp-go', onClick: function () { setTimeout(function () { c.openDialog('result-exported'); }, 0); } }] });
    });
    c.dialog('result-exported', function () { result.textContent = ''; result.appendChild(E.ResultBanner({ id: 'cr-rb-export', title: 'Export is ready', text: doc.id + '-calculation-results-' + YEAR + '.xlsx · ' + all.length + ' outputs × 12 months. Next: download it, or find it later in My tasks.', actions: [{ label: 'Download', icon: 'download', variant: 'primary', onClick: function () { E.Toast('Prototype: no file'); } }, { label: 'My tasks', href: E.href(c.path, { panel: 'tasks' }) }] })); });
    c.dialog('recalc-stale', function () {
      E.Dialog({ id: 'cr-dlg-recalc', narrow: true, title: 'Recalculate ' + (stale.length ? stale.map(function (m) { return F.period(m.id); }).join(', ') : 'the open month') + '?', body: function (b) {
        b.appendChild(h('p', 'Only open months are recalculated. Closed months keep the results of their last run; submitted and approved sheets are skipped.'));
        b.appendChild(E.KeyValue([{ label: 'Months', value: stale.map(function (m) { return m.name; }).join(', ') || '—' }, { label: 'Queue', value: 'shared recalculation queue · usually under a minute' }])); },
        footer: [{ label: 'Cancel', id: 'cr-rc-cancel' }, { label: 'Recalculate', icon: 'refresh', variant: 'primary', autofocus: true, id: 'cr-rc-go', onClick: function () { E.Toast('Recalculation queued — the page refreshes when it finishes'); c.setQuery({ sep: 'current' }); setTimeout(function () { if (document.contains(page)) { ms = months('current'); all = build(ms); stale = []; paint(); } }, 600); } }] });
    });

    c.action('Export calculation results', 'download', function () { c.openDialog('export-results'); });
    c.action('Open the document', 'table', function () { location.hash = docHref; });
    c.proto({ id: 'cr-layout', label: 'Template layout', value: year ? 'year' : 'months', options: [{ value: 'months', label: 'Per-month copies' }, { value: 'year', label: 'Year with month columns' }], onChange: function (v) { c.setQuery({ layout: v === 'year' ? 'year' : null, month: null }); c.refresh(); } });
    c.proto({ id: 'cr-sep', label: 'September run', value: c.query.sep || 'stale', options: [{ value: 'stale', label: 'Out of date' }, { value: 'current', label: 'Current' }, { value: 'norun', label: 'No run' }], onChange: function (v) { c.setQuery({ sep: v === 'stale' ? null : v }); ms = months(v); all = build(ms); paint(); } });
  } });

  /* ================= flows ================= */
  var R1 = '#/documents/DOC-000001/results', D1 = '#/documents/DOC-000001';
  E.flow('cr-overview', { group: 'Reporting — document', title: 'Calculation results: all months of the year', actor: 'Data entry, Approver', note: 'Per-month copies: the “year overview”. The month switch of the document stays; a month heading opens the document on that month.', steps: [
    { label: 'Document · More → Calculation results', href: D1 }, { label: 'All months, grouped', href: R1 }, { label: 'Value → where it comes from', href: R1 + '?panel=cell-r1-9' }, { label: 'Missing required input', href: R1 + '?panel=cell-r5-9' },
    { label: 'Recalculate the out-of-date month', href: R1 + '?dialog=recalc-stale' }, { label: 'September recalculated', href: R1 + '?sep=current' }, { label: 'Export', href: R1 + '?dialog=export-results' }, { label: 'Result: export is ready', href: R1 + '?dialog=result-exported' }] });
  E.flow('cr-year', { group: 'Reporting — document', title: 'Calculation results in the Year layout (no month switch)', actor: 'Data entry', note: 'YearWithMonthColumns: this page is the way to see calculated values for every month. The sheet’s calculated columns show one “results month”.', steps: [
    { label: 'Document: Year 2026, “Calculated: Sep”', href: D1 + '?layout=year' }, { label: 'All months · results month marked', href: R1 + '?layout=year' }, { label: 'Pick another results month', href: R1 + '?layout=year&month=2026-06' }, { label: 'Back in the sheet: June shown', href: D1 + '?layout=year&month=2026-06' }] });
  E.flow('cr-states', { group: 'Reporting — document', title: 'Calculation results: states', steps: [{ label: 'No run for September', href: R1 + '?sep=norun' }, { label: 'Nothing calculated yet', href: R1 + '?state=empty' }, { label: 'Loading', href: R1 + '?state=loading' }, { label: 'Load error', href: R1 + '?state=error' }] });
})(window.ECR);

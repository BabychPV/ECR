/* =====================================================================
   screen-document.js — /documents/:id · головний екран документа.
   Основа: A «Workbench» (сітка, рядок формул, навігатор, інспектор, вкладки аркушів).
   Під Принцип №1: інспектор закритий за замовчуванням; навігатор показує лише поточний аркуш,
   групи згорнуті, крім поточної; одна головна дія; компактний степер воркфлоу з B.
   ?dialog= : conflict · import-file · import-preview · export-progress · rounded-list · reject-reason ·
              return-reason · submit-with-warnings · session-expired · new-version-available · delete-sheet ·
              result-submitted · result-approved · result-rejected · result-returned · result-imported ·
              result-exported · result-conflict-resolved · result-restored
   ?panel=  : issues · history · info      (+ глобальні: tasks · notes)
   Додаткові параметри: sheet=0..4 · table=<n> · sheetState=Draft|Submitted|Approved|Rejected|Returned · issues=warnings|clean · as=Approver
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, icon = E.icon, ic = E.iconHtml, esc = E.esc, rng = E.rng, F = E.fmt;
  function fmt(v, d) { return v == null ? '' : F.number(v, d == null ? 4 : d); }
  function plural(n, one, many) { return F.plural(n, { one: one, other: many }); }

  E.css('doc', [
    '.doc-nav .doc-progress{display:grid;gap:var(--s1);padding:var(--s2) var(--s3);border-bottom:1px solid var(--border)}',
    '.doc-nav .doc-progress .dp-l{display:flex;align-items:center;gap:var(--s1);font-size:var(--fs-xs);color:var(--muted);white-space:nowrap}',
    '.doc-nav .doc-progress .dp-l b{font-weight:500;color:var(--text)}',
    '.doc-nav .doc-progress .dp-l .link-btn.muted{color:var(--muted)}',
    '.doc-nav .nav-tools{display:flex;gap:var(--s1);padding:var(--s2);border-bottom:1px solid var(--border)}',
    '.doc-nav .nav-tools .with-icon{flex:1;min-width:0}',
    '.doc-nav .tree{flex:1;min-height:0;overflow:auto;padding:var(--s1) 0}',
    '.doc-nav .tree-group{display:flex;align-items:center;gap:var(--s1);width:100%;height:var(--row);padding:0 var(--s2) 0 var(--s2);border:0;background:transparent;text-align:left;font-weight:500;color:var(--text)}',
    '.doc-nav .tree-group:hover,.doc-nav .tree-table:hover{background:var(--hover)}',
    '.doc-nav .tree-group .nm{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.doc-nav .tree-group .gn{font:400 var(--fs-xs)/1 var(--mono);color:var(--muted);width:16px;text-align:right}',
    '.doc-nav .tree-group .st{color:var(--muted);font-weight:400;font-size:var(--fs-xs)}',
    '.doc-nav .tree-group[aria-expanded="true"]>svg:first-child{transform:rotate(90deg)}',
    '.doc-nav .tree-table{display:flex;align-items:center;gap:var(--s2);width:100%;height:var(--row);padding:0 var(--s2) 0 var(--s5);border:0;background:transparent;text-align:left}',
    '.doc-nav .tree-table .no{font:400 var(--fs-xs)/1 var(--mono);color:var(--muted);width:30px;flex:none}',
    '.doc-nav .tree-table .nm{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.doc-nav .tree-table[aria-current="true"]{background:var(--accent-soft);color:var(--accent-text);font-weight:500}',
    '.doc-head .doc-state{display:inline-flex;align-items:center;gap:var(--s2)}',
    '.doc-head .save{display:inline-flex;align-items:center;gap:var(--s1);color:var(--muted);white-space:nowrap}',
    '.doc-head .save.fail{color:var(--text)}.doc-head .save.fail svg{color:var(--danger)}',
    '.doc-work{background:var(--surface)}',
    '.doc-work .tablebar{display:flex;align-items:center;gap:var(--s2);height:36px;padding:0 var(--s2);border-bottom:1px solid var(--border);flex:none;min-width:0}',
    '.doc-work .tablebar h2{font-size:var(--fs-md);font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.doc-work .tablebar .meta{color:var(--muted);font-size:var(--fs-xs);white-space:nowrap}',
    '.doc-work .tablebar .sp{flex:1}',
    '.doc-work .issues-btn{display:inline-flex;align-items:center;gap:var(--s1);height:28px;padding:0 var(--s2);border:1px solid transparent;border-radius:var(--r1);background:transparent;color:var(--muted);font-weight:500;white-space:nowrap}',
    '.doc-work .issues-btn:hover{background:var(--hover);color:var(--text)}',
    '.doc-work .issues-btn.bad{color:var(--danger)}.doc-work .issues-btn.warn{color:var(--warning)}',
    '.doc-work .issues-btn[aria-pressed="true"]{background:var(--accent-soft);color:var(--accent-text)}',
    '.doc-work .fbar{display:flex;align-items:stretch;height:var(--ctl);min-height:28px;border-bottom:1px solid var(--border-strong);flex:none;min-width:0}',
    '.doc-work .fbar-addr{flex:none;width:96px;display:flex;align-items:center;justify-content:center;font:500 var(--fs-sm)/1 var(--mono);border-right:1px solid var(--border);background:var(--sunken)}',
    '.doc-work .fbar-fx{flex:none;width:32px;display:grid;place-items:center;font:italic 500 var(--fs-sm)/1 var(--mono);color:var(--muted);border-right:1px solid var(--border)}',
    '.doc-work .fbar-val{flex:1;min-width:0;display:flex;align-items:center;gap:var(--s2);padding:0 var(--s2);font-family:var(--mono);white-space:nowrap;overflow:hidden}',
    '.doc-work .fbar-val .why{font-family:var(--sans);color:var(--muted);display:inline-flex;align-items:center;gap:var(--s1);overflow:hidden;text-overflow:ellipsis}',
    '.doc-work .fbar-val .why.bad{color:var(--danger)}',
    '.doc-work .fbar-col{flex:none;display:flex;align-items:center;padding:0 var(--s3);color:var(--muted);font-size:var(--fs-xs);border-left:1px solid var(--border);white-space:nowrap}',
    '.doc-work .statusbar{display:flex;align-items:stretch;height:32px;border-top:1px solid var(--border-strong);background:var(--sunken);flex:none;min-width:0}',
    '.doc-work .sheet-tabs{display:flex;align-items:stretch;min-width:0;overflow-x:auto;scrollbar-width:none}',
    '.doc-work .sheet-tab{display:inline-flex;align-items:center;gap:var(--s2);padding:0 var(--s3);border:0;border-right:1px solid var(--border);background:transparent;color:var(--muted);white-space:nowrap}',
    '.doc-work .sheet-tab:hover{color:var(--text);background:var(--hover)}',
    '.doc-work .sheet-tab[aria-selected="true"]{background:var(--surface);color:var(--text);font-weight:500;margin-top:-1px;box-shadow:inset 0 -2px 0 var(--accent)}',
    '.doc-work .sheet-tab .st{font-weight:400;color:var(--muted)}',
    '.doc-work .selstats{margin-left:auto;display:flex;align-items:center;gap:var(--s3);padding:0 var(--s3);color:var(--muted);font-size:var(--fs-xs);white-space:nowrap}',
    '.doc-work .selstats b{font:500 var(--fs-xs)/1 var(--mono);color:var(--text)}',
    '.doc-insp .insp-body{flex:1;min-height:0;overflow:auto}',
    '.doc-insp .issue{display:grid;grid-template-columns:16px 1fr;gap:var(--s1) var(--s2);width:100%;padding:var(--s2) var(--s3);border:0;border-bottom:1px solid var(--border);background:transparent;text-align:left}',
    '.doc-insp .issue:hover{background:var(--hover)}',
    '.doc-insp .issue .e{color:var(--danger)}.doc-insp .issue .w{color:var(--warning)}',
    '.doc-insp .issue .where{grid-column:2;color:var(--muted);font-size:var(--fs-xs);display:flex;gap:var(--s2);flex-wrap:wrap;align-items:center}',
    '.doc-insp .group-h{padding:var(--s2) var(--s3) var(--s1);font-size:var(--fs-xs);font-weight:500;color:var(--muted);text-transform:uppercase;letter-spacing:.04em;background:var(--sunken);border-bottom:1px solid var(--border);display:flex;justify-content:space-between;gap:var(--s2)}',
    '.doc-insp .hist{padding:var(--s2) var(--s3);border-bottom:1px solid var(--border);display:grid;gap:var(--s1)}',
    '.doc-insp .hist-top{display:flex;justify-content:space-between;gap:var(--s2);align-items:center}',
    '.doc-insp .hist-top b{font-weight:500}',
    '.doc-insp .diff{font-family:var(--mono);display:flex;align-items:center;gap:var(--s2);flex-wrap:wrap}',
    '.doc-insp .diff s{color:var(--muted)}.doc-insp .diff svg{color:var(--faint)}',
    '.doc-insp .cell-legend{display:grid;gap:var(--s1);padding:var(--s2) var(--s3) var(--s3)}',
    '.doc-insp .cell-legend div{display:flex;align-items:center;gap:var(--s2);font-size:var(--fs-xs);color:var(--muted)}',
    '.doc-drop{display:grid;justify-items:center;gap:var(--s2);padding:var(--s5);border:1px dashed var(--border-strong);border-radius:var(--r2);background:var(--sunken);text-align:center}',
    '.doc-drop svg{color:var(--muted)}',
    '@media (max-width:640px){.doc-work .tablebar .meta,.doc-work .selstats .opt,.doc-work .fbar-col,.doc-work .issues-btn span{display:none}.doc-work .fbar-addr{width:80px}}'
  ].join('\n'));

  /* ================= model (живе між перемальовуваннями екрана) ================= */
  var SUF_AIR = ['mass emissions', 'fuel use', 'operating hours', 'emission factors', 'monthly totals', 'stack parameters', 'permit comparison'];
  var SUF_GEN = ['monthly totals', 'by source', 'by method', 'permit comparison', 'analysis results', 'annual forecast', 'corrections', 'supporting data', 'summary', 'notes & evidence', 'year to date', 'previous period', 'deviations'];
  var GROUPS = [
    [['Stationary combustion', 7], ['Flaring', 7], ['Process vents', 7], ['Storage tanks', 7], ['Loading operations', 7], ['Fugitive emissions', 7], ['Mobile sources', 7], ['Wastewater treatment', 7], ['Sulphur recovery', 7], ['Gas treatment', 7], ['Power generation', 7], ['Laboratory & workshops', 7], ['Emergency releases', 7]],
    [['Water intake', 6], ['Water use', 6], ['Wastewater discharge', 6], ['Water quality monitoring', 6]],
    [['Hazardous waste', 13], ['Non-hazardous waste', 12], ['Waste transfers', 12]],
    [['Scope 1 — combustion', 10], ['Scope 1 — flaring', 10], ['Scope 1 — venting', 10], ['Scope 1 — fugitive', 10], ['Scope 2 — purchased energy', 9], ['Removals & offsets', 9]],
    [['Energy consumption', 6], ['Energy generation', 6]]];
  var AIR_COLS = [['Fuel gas', '10³ m³', 5000], ['Hours', 'h', 720], ['SO₂', 't', 900], ['NOₓ', 't', 400], ['CO', 't', 300], ['VOC', 't', 120], ['PM₁₀', 't', 40], ['PM₂.₅', 't', 20], ['H₂S', 't', 5], ['CH₄', 't', 250], ['Permit limit', 't·yr⁻¹', 0, 'locked'], ['Total', 't·yr⁻¹', 0, 'calc']];
  var GEN_COLS = [['Volume', '10³ m³', 5000], ['Hours', 'h', 720], ['COD', 't', 900], ['BOD₅', 't', 400], ['TSS', 't', 300], ['Oil products', 't', 120], ['Chlorides', 't', 40], ['Sulphates', 't', 20], ['Nitrates', 't', 5], ['Phosphates', 't', 250], ['Permit limit', 't·yr⁻¹', 0, 'locked'], ['Total', 't·yr⁻¹', 0, 'calc']];
  var GEN_ROWS = []; for (var gi = 0; gi < 28; gi++) GEN_ROWS.push('Monitoring point MP-' + (101 + gi));
  var TABLES = GROUPS.map(function (groups, s) { var out = []; groups.forEach(function (g, i) { for (var k = 0; k < g[1]; k++) { var sf = (s === 0 ? SUF_AIR : SUF_GEN)[k]; out.push({ no: (i + 1) + '.' + (k + 1), name: g[0] + ' — ' + sf, g: i, gname: g[0], short: sf.charAt(0).toUpperCase() + sf.slice(1) }); } }); return out; });
  var DEMO_DIRTY = [[2, 3], [5, 2], [14, 6]], ERR_T = [0, 9, 36], MAIN = 'DOC-000001';
  var cache = {}, statusCache = {};
  var S = { doc: null, docId: null, s: 0, t: 0, act: { r: 0, c: 0 }, anc: { r: 0, c: 0 }, period: '2026-09', inspOpen: false, inspTab: 'issues', navOpen: null, groupsOpen: {}, navQ: '', navErr: false, result: null, lastImport: null, reason: {}, fixed: false };
  var save = { mode: 'failed', time: '14:03', timer: null, conflictShown: false };
  var root = null, ctx = null, T = null, gridEl = null, tdMap = [], editing = null, dragging = false;
  function q(s) { return root ? root.querySelector(s) : null; }
  function qa(s) { return Array.prototype.slice.call(root.querySelectorAll(s)); }
  function sheet() { return S.doc.sheets[S.s]; }
  function hasDemoErrors() { return S.docId === MAIN && !S.fixed; }

  /* D2: поданий / затверджений аркуш не може мати ні помилок, ні незбереженого. Для нього будується окремий «чистий» варіант
     таблиці (ключ …:ro): ті самі числа, але без демо-помилок і демо-правок; попередження лишаються. Чернетку це не змінює. */
  function lockedSheet(s) { var st = S.doc.sheets[s].state; return st === 'Submitted' || st === 'Approved'; }
  function getTable(s, t, draft) {
    var ro = !draft && lockedSheet(s), k = S.docId + ':' + s + ':' + t + (ro ? ':ro' : ''); if (cache[k]) return cache[k];
    var air = s === 0, R = rng(7919 + s * 131 + t * 17 + (+S.docId.slice(-3)) * 977), rows = air ? E.data.emissionSources : GEN_ROWS, cols = air ? AIR_COLS : GEN_COLS;
    var X = { s: s, t: t, key: k, ro: ro, rows: rows, cols: cols, cells: [], lockedRows: {}, over: {}, undo: [], redo: [], issues: {} };
    rows.forEach(function (name) { var row = []; for (var c = 0; c < 12; c++) { var v = null, rd = false; if (c < 10) { var fug = /^Fugitive|pond|tower/.test(name) && c < 2; if (!fug && R() > 0.06) { rd = R() < 0.05; v = +(R() * cols[c][2]).toFixed(rd ? 7 : 4); } } row.push({ v: v, rounded: rd, dirty: false, orig: undefined, req: false }); } X.cells.push(row); });
    function put(r, c, v) { X.cells[r][c].v = v; X.cells[r][c].rounded = false; }
    if (S.docId === MAIN && s === 0 && ro) {
      if (t === 0) { put(19, 1, 744); X.lockedRows[8] = 'Locked: source out of service in September 2026'; put(21, 3, null); put(24, 2, null); }
      if (t === 36) put(10, 1, 731);
    } else if (S.docId === MAIN && s === 0) {
      if (t === 0) { put(3, 2, -12.5); put(19, 1, 744); X.over[11] = 1; X.lockedRows[8] = 'Locked: source out of service in September 2026'; put(21, 3, null); put(24, 2, null);
        DEMO_DIRTY.forEach(function (d, i) { var cl = X.cells[d[0]][d[1]]; if (cl.v == null) cl.v = +(40 + i * 17.25).toFixed(4); cl.rounded = false; cl.dirty = true; cl.orig = +(cl.v * 0.92).toFixed(4); }); }
      if (t === 9) { put(5, 4, null); X.cells[5][4].req = true; }
      if (t === 36) { put(2, 2, -3.2); put(10, 1, 731); put(16, 3, null); X.cells[16][3].req = true; }
    }
    recalc(X, true); validate(X); cache[k] = X; return X;
  }
  function recalc(X, init) { X.cells.forEach(function (row, r) { var sum = 0, any = false; for (var c = 2; c < 10; c++) if (row[c].v != null) { sum += row[c].v; any = true; } row[11].v = any ? +sum.toFixed(4) : null; if (init) row[10].v = any ? (X.over[r] ? Math.floor(sum * 0.9 / 10) * 10 : Math.ceil(sum * 1.15 / 10) * 10) : null; }); }
  function validate(X) { var m = {}; X.cells.forEach(function (row, r) { for (var c = 0; c < 10; c++) { var v = row[c].v;
      if (v != null && v < 0) m[r + ':' + c] = { sev: 'error', msg: 'Value must be ≥ 0', code: 'ECR-VAL-0202' };
      else if (c === 1 && v > 720) m[r + ':' + c] = { sev: 'warning', msg: 'Hours exceed the period length (720 h)', code: 'ECR-VAL-0330' };
      else if (row[c].req && v == null) m[r + ':' + c] = { sev: 'error', msg: 'Required value is missing', code: 'ECR-VAL-0107' }; }
    if (row[11].v != null && row[10].v != null && row[11].v > row[10].v) m[r + ':11'] = { sev: 'error', msg: 'Total exceeds permit limit by ' + fmt(row[11].v - row[10].v, 1) + ' t', code: 'ECR-VAL-0312' }; });
    X.issues = m; return m; }
  function issueList(X) { return Object.keys(X.issues).map(function (k) { var p = k.split(':'); return { T: X, r: +p[0], c: +p[1], sev: X.issues[k].sev, msg: X.issues[k].msg, code: X.issues[k].code }; }).sort(function (a, b) { return a.r - b.r || a.c - b.c; }); }
  function allIssues() { var ts = []; if (S.docId === MAIN && S.s === 0) ts = ERR_T.slice(); if (ts.indexOf(S.t) < 0) ts.push(S.t); ts.sort(function (a, b) { return a - b; }); var out = []; ts.forEach(function (t) { out = out.concat(issueList(getTable(S.s, t))); }); return out; }
  function counts() { var all = allIssues(), e = all.filter(function (i) { return i.sev === 'error'; }).length; return { all: all.length, errors: e, warnings: all.length - e }; }
  function errCount(X) { return issueList(X).filter(function (i) { return i.sev === 'error'; }).length; }
  function sheetStatuses(s) { var k = S.docId + ':' + s; if (statusCache[k]) return statusCache[k]; var sh = S.doc.sheets[s], n = TABLES[s].length, xs = []; for (var t = 0; t < n; t++) xs.push({ t: t, x: rng(s * 997 + t * 13 + 5)() });
    var empties = Math.max(0, n - sh.filled), order = xs.slice().sort(function (a, b) { return b.x - a.x; }), st = []; order.forEach(function (o, i) { st[o.t] = i < empties && !(S.docId === MAIN && s === 0 && ERR_T.indexOf(o.t) >= 0) ? 'empty' : o.x < 0.4 ? 'filled' : 'partial'; });
    if (sh.state === 'Approved' || sh.state === 'Submitted') st = st.map(function () { return 'filled'; }); statusCache[k] = st; return st; }
  function tableStatus(s, t) { var k = S.docId + ':' + s + ':' + t, base = sheetStatuses(s)[t]; if (cache[k] || (S.docId === MAIN && s === 0 && ERR_T.indexOf(t) >= 0)) { var X = getTable(s, t), n = errCount(X); if (n) return { st: 'error', n: n }; if (base === 'empty') base = 'partial'; } return { st: base, n: 0 }; }
  function addr(r, c) { return 'R' + (r + 1) + ' · C' + (c + 1); }
  function eachCell(fn) { Object.keys(cache).forEach(function (k) { if (k.indexOf(S.docId + ':') !== 0 || cache[k].ro !== lockedSheet(cache[k].s)) return; cache[k].cells.forEach(function (row, r) { row.forEach(function (cl, c) { fn(cl, r, c, cache[k]); }); }); }); }
  function dirtyCount() { var n = 0; if (!S.docId) return 0; eachCell(function (cl) { if (cl.dirty) n++; }); return n; }
  function fixAll(level) { ERR_T.forEach(function (t) { var X = cache[MAIN + ':0:' + t]; if (!X) { var keep = S.docId; S.docId = MAIN; X = getTable(0, t, true); S.docId = keep; } X.over = {}; X.cells.forEach(function (row) { for (var c = 0; c < 10; c++) { if (row[c].v != null && row[c].v < 0) row[c].v = Math.abs(row[c].v); if (row[c].req && row[c].v == null) row[c].v = 12.5; if (level === 'clean' && c === 1 && row[c].v > 720) row[c].v = 720; } }); recalc(X, true); validate(X); }); S.fixed = true; }

  /* ================= read-only logic ================= */
  function periodOpen() { var p = E.data.period(S.period); return p.state === 'Open' || p.state === 'Grace'; }
  function editableState() { var st = sheet().state; return st === 'Draft' || st === 'Returned' || st === 'Rejected'; }
  function readOnlyReason() { var st = sheet().state, p = E.data.period(S.period);
    if (st === 'Approved') return 'Locked: sheet approved 18 Sep 2026 by A. Iskakov'; if (st === 'Submitted') return 'Locked: sheet submitted for approval';
    if (p.state === 'NotOpened') return 'Locked: period not opened yet'; if (!periodOpen()) return 'Locked: period closed'; if (!E.can('Document.Edit')) return 'Read-only: your role cannot edit data'; return null; }
  function lockText(r, c) { var ro = readOnlyReason(); if (T.cols[c][3] === 'calc') return 'Calculated by formula SUM(C3:C10)'; if (ro) return ro; if (T.cols[c][3] === 'locked') return 'Locked: value comes from registry “Permit limits”'; if (T.lockedRows[r]) return T.lockedRows[r]; return null; }
  function canEdit(r, c) { return !lockText(r, c); }

  /* ================= grid ================= */
  function cellCls(r, c) { var cl = T.cells[r][c], k = 'c', col = T.cols[c][3], is = T.issues[r + ':' + c]; if (col === 'calc') k += ' is-calc'; else if (col === 'locked' || T.lockedRows[r]) k += ' is-locked'; if (cl.dirty) k += ' is-dirty'; if (cl.rounded) k += ' is-rounded'; if (is) k += is.sev === 'error' ? ' is-error' : ' is-warn'; return k; }
  function cellTip(r, c) { var cl = T.cells[r][c], p = [], is = T.issues[r + ':' + c], lt = lockText(r, c); if (is) p.push(is.msg); if (lt) p.push(lt); if (cl.dirty) p.push('Unsaved change'); if (cl.rounded) p.push('Shown rounded to 4 decimals'); return p.join(' · '); }
  function cellHtml(r, c) { var cl = T.cells[r][c], s = fmt(cl.v); return cl.rounded ? '<span class="v">' + s + '</span>' : s; }
  function renderGrid() {
    T = getTable(S.s, S.t); var html = '<table class="grid"><thead><tr><th class="rh" scope="col"><span class="cc">#</span>' + (S.s === 0 ? 'Emission source' : 'Monitoring point') + '</th>';
    T.cols.forEach(function (col, c) { html += '<th scope="col" data-hc="' + c + '" class="' + (col[3] === 'calc' ? 'is-calc' : '') + '" title="' + esc(col[0] + ', ' + col[1] + (col[3] === 'calc' ? ' — calculated' : col[3] === 'locked' ? ' — from registry, read-only' : '')) + '"><div class="ch"><span class="cc">C' + (c + 1) + '</span><span class="cn">' + (col[3] === 'calc' ? 'ƒ ' : '') + esc(col[0]) + (col[3] === 'locked' ? ic('lock', 12) : '') + '</span></div><div class="cu">' + esc(col[1]) + '</div></th>'; });
    gridEl.innerHTML = html + '</tr></thead><tbody id="doc-grid-body"></tbody><tfoot id="doc-grid-foot"></tfoot></table>'; gridEl.scrollTop = 0; gridEl.scrollLeft = 0; renderBody();
  }
  function renderBody() {
    var html = ''; T.rows.forEach(function (name, r) { html += '<tr><th class="rh" scope="row" data-hr="' + r + '" title="' + esc(name) + '"><span class="cc">' + (r + 1) + '</span>' + esc(name) + '</th>'; for (var c = 0; c < 12; c++) { var tip = cellTip(r, c); html += '<td class="' + cellCls(r, c) + '" data-r="' + r + '" data-c="' + c + '"' + (tip ? ' title="' + esc(tip) + '"' : '') + '>' + cellHtml(r, c) + '</td>'; } html += '</tr>'; });
    q('#doc-grid-body').innerHTML = html; var f = '<tr><th class="rh" scope="row">Σ Total · ' + T.rows.length + ' rows</th>';
    for (var c = 0; c < 12; c++) { var s = 0, any = false; T.cells.forEach(function (row) { if (row[c].v != null) { s += row[c].v; any = true; } }); f += '<td>' + (any ? fmt(s) : '') + '</td>'; }
    q('#doc-grid-foot').innerHTML = f + '</tr>'; tdMap = []; qa('#doc-grid-body tr').forEach(function (tr, r) { tdMap[r] = Array.prototype.slice.call(tr.querySelectorAll('td')); }); editing = null; paint(true);
  }
  function range() { return { r0: Math.min(S.act.r, S.anc.r), r1: Math.max(S.act.r, S.anc.r), c0: Math.min(S.act.c, S.anc.c), c1: Math.max(S.act.c, S.anc.c) }; }
  function paint(noReveal) {
    Array.prototype.forEach.call(gridEl.querySelectorAll('.sel,.active,.hl'), function (n) { n.classList.remove('sel', 'active', 'hl'); }); var g = range(), multi = g.r0 !== g.r1 || g.c0 !== g.c1;
    if (multi) for (var r = g.r0; r <= g.r1; r++) for (var c = g.c0; c <= g.c1; c++) tdMap[r][c].classList.add('sel');
    var td = tdMap[S.act.r][S.act.c]; td.classList.add('active'); var hc = gridEl.querySelector('[data-hc="' + S.act.c + '"]'), hr = gridEl.querySelector('[data-hr="' + S.act.r + '"]'); if (hc) hc.classList.add('hl'); if (hr) hr.classList.add('hl');
    if (!noReveal) reveal(td); updateFbar(); updateStats(); if (S.inspOpen && S.inspTab !== 'issues') renderInspector();
  }
  function reveal(td) { var w = gridEl, hh = w.querySelector('thead').offsetHeight, fh = w.querySelector('tfoot').offsetHeight, fw = w.querySelector('thead .rh').offsetWidth, t = td.offsetTop, l = td.offsetLeft;
    if (t - hh < w.scrollTop) w.scrollTop = t - hh; else if (t + td.offsetHeight > w.scrollTop + w.clientHeight - fh) w.scrollTop = t + td.offsetHeight - w.clientHeight + fh;
    if (l - fw < w.scrollLeft) w.scrollLeft = l - fw; else if (l + td.offsetWidth > w.scrollLeft + w.clientWidth) w.scrollLeft = l + td.offsetWidth - w.clientWidth; }
  function setActive(r, c, extend) { r = Math.max(0, Math.min(T.rows.length - 1, r)); c = Math.max(0, Math.min(11, c)); S.act = { r: r, c: c }; if (!extend) S.anc = { r: r, c: c }; paint(); }
  function updateFbar() { var cl = T.cells[S.act.r][S.act.c], col = T.cols[S.act.c], lt = lockText(S.act.r, S.act.c), is = T.issues[S.act.r + ':' + S.act.c], html;
    q('#doc-fbar-addr').textContent = addr(S.act.r, S.act.c); var ia = q('#doc-insp-addr'); if (ia) ia.textContent = addr(S.act.r, S.act.c);
    html = '<span>' + (col[3] === 'calc' ? '= SUM(C3:C10)' : cl.v == null ? '' : esc(String(cl.v))) + '</span>';
    if (lt) html += '<span class="why">' + ic(col[3] === 'calc' ? 'sigma' : 'lock', 14) + esc(lt) + '</span>'; else if (cl.rounded) html += '<span class="why">shown as ' + fmt(cl.v) + ' · rounded to 4 decimals</span>';
    if (is) html += '<span class="why ' + (is.sev === 'error' ? 'bad' : '') + '">' + ic(is.sev === 'error' ? 'alertCircle' : 'alert', 14) + esc(is.msg) + '</span>';
    q('#doc-fbar-val').innerHTML = html; q('#doc-fbar-col').textContent = T.rows[S.act.r] + ' · ' + col[0] + ', ' + col[1]; }
  function updateStats() { var g = range(), s = 0, n = 0; for (var r = g.r0; r <= g.r1; r++) for (var c = g.c0; c <= g.c1; c++) { var v = T.cells[r][c].v; if (v != null) { s += v; n++; } }
    q('#doc-selstats').innerHTML = n > 1 ? '<span class="opt">Average <b>' + fmt(s / n) + '</b></span><span>Count <b>' + n + '</b></span><span>Sum <b>' + fmt(s) + '</b></span>' : '<span class="opt">' + T.rows.length + ' rows × 12 columns</span>'; }

  /* ---------- editing, undo, clipboard ---------- */
  function parseNum(s) { s = String(s).trim().replace(/[\s  ]/g, '').replace('−', '-').replace(',', '.'); if (s === '') return null; var n = Number(s); return isFinite(n) ? +n.toFixed(7) : undefined; }
  function snap(cl) { return { v: cl.v, dirty: cl.dirty, orig: cl.orig, rounded: cl.rounded }; }
  function applyChanges(list) { var batch = []; list.forEach(function (ch) { if (!canEdit(ch.r, ch.c)) return; var cl = T.cells[ch.r][ch.c]; if (cl.v === ch.v) return; var prev = snap(cl); if (cl.orig === undefined) cl.orig = cl.v; cl.v = ch.v; cl.dirty = true; cl.rounded = ch.v != null && +ch.v.toFixed(4) !== ch.v; batch.push({ r: ch.r, c: ch.c, prev: prev, next: snap(cl) }); });
    if (!batch.length) return 0; T.undo.push(batch); T.redo = []; afterDataChange(); scheduleAutosave(); return batch.length; }
  function afterDataChange() { recalc(T); validate(T); renderBody(); renderTree(); renderCounts(); q('#doc-undo').disabled = !T.undo.length; q('#doc-redo').disabled = !T.redo.length; if (S.inspOpen) renderInspector(); }
  function undoRedo(from, to, key) { var b = from.pop(); if (!b) return; b.forEach(function (ch) { var cl = T.cells[ch.r][ch.c], s = ch[key]; cl.v = s.v; cl.dirty = s.dirty; cl.orig = s.orig; cl.rounded = s.rounded; }); to.push(b); S.act = { r: b[0].r, c: b[0].c }; S.anc = { r: S.act.r, c: S.act.c }; afterDataChange(); scheduleAutosave(); }
  function startEdit(initial) { if (editing) return; if (!canEdit(S.act.r, S.act.c)) { E.Toast(lockText(S.act.r, S.act.c)); return; }
    var td = tdMap[S.act.r][S.act.c], cl = T.cells[S.act.r][S.act.c], inp = h('input', { class: 'cell-editor', id: 'doc-cell-editor', 'aria-label': 'Edit ' + addr(S.act.r, S.act.c), autocomplete: 'off', inputmode: 'decimal' });
    inp.value = initial != null ? initial : (cl.v == null ? '' : String(cl.v)); td.appendChild(inp); editing = { r: S.act.r, c: S.act.c, inp: inp, done: false }; inp.focus(); if (initial == null) inp.select();
    inp.addEventListener('keydown', function (e) { e.stopPropagation(); if (e.key === 'Enter') { e.preventDefault(); commitEdit(1, 0); } else if (e.key === 'Tab') { e.preventDefault(); commitEdit(0, e.shiftKey ? -1 : 1); } else if (e.key === 'Escape') { e.preventDefault(); cancelEdit(); } });
    inp.addEventListener('blur', function () { commitEdit(0, 0, true); }); }
  function cancelEdit() { if (!editing || editing.done) return; editing.done = true; editing.inp.remove(); editing = null; gridEl.focus({ preventScroll: true }); }
  function commitEdit(dr, dc, fromBlur) { if (!editing || editing.done) return; var e = editing, v = parseNum(e.inp.value); e.done = true; e.inp.remove(); editing = null;
    if (v === undefined) E.Toast('“' + e.inp.value + '” is not a number — the cell was left unchanged', 'warning'); else applyChanges([{ r: e.r, c: e.c, v: v }]); if (!fromBlur) { gridEl.focus({ preventScroll: true }); setActive(e.r + dr, e.c + dc); } }
  function bindGrid() {
    gridEl.addEventListener('keydown', function (e) { if (editing) return; var k = e.key, ctrl = e.ctrlKey || e.metaKey, mv = { ArrowUp: [-1, 0], ArrowDown: [1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1], PageUp: [-10, 0], PageDown: [10, 0] }[k];
      if (mv) { e.preventDefault(); setActive(S.act.r + mv[0], S.act.c + mv[1], e.shiftKey); }
      else if (k === 'Tab') { if ((e.shiftKey && S.act.c === 0) || (!e.shiftKey && S.act.c === 11)) return; e.preventDefault(); setActive(S.act.r, S.act.c + (e.shiftKey ? -1 : 1)); }
      else if (k === 'Home') { e.preventDefault(); setActive(ctrl ? 0 : S.act.r, 0, e.shiftKey); } else if (k === 'End') { e.preventDefault(); setActive(ctrl ? T.rows.length - 1 : S.act.r, 11, e.shiftKey); }
      else if (k === 'Enter' || k === 'F2') { e.preventDefault(); startEdit(); } else if (k === 'F9') { e.preventDefault(); doRecalc(); }
      else if (k === 'Delete' || k === 'Backspace') { e.preventDefault(); var g = range(), l = []; for (var r = g.r0; r <= g.r1; r++) for (var c = g.c0; c <= g.c1; c++) l.push({ r: r, c: c, v: null }); if (!applyChanges(l) && lockText(S.act.r, S.act.c)) E.Toast(lockText(S.act.r, S.act.c)); }
      else if (ctrl && k.toLowerCase() === 'z') { e.preventDefault(); undoRedo(T.undo, T.redo, 'prev'); } else if (ctrl && k.toLowerCase() === 'y') { e.preventDefault(); undoRedo(T.redo, T.undo, 'next'); }
      else if (ctrl && k.toLowerCase() === 'a') { e.preventDefault(); S.anc = { r: 0, c: 0 }; S.act = { r: T.rows.length - 1, c: 11 }; paint(true); }
      else if (k.length === 1 && !ctrl && !e.altKey && /[0-9.,\-−]/.test(k)) { e.preventDefault(); startEdit(k); } });
    gridEl.addEventListener('mousedown', function (e) { var td = e.target.closest('td.c'); if (e.target.id === 'doc-cell-editor') return;
      if (td) { if (editing) commitEdit(0, 0, true); dragging = true; S.act = { r: +td.dataset.r, c: +td.dataset.c }; if (!e.shiftKey) S.anc = { r: S.act.r, c: S.act.c }; paint(true); gridEl.focus({ preventScroll: true }); e.preventDefault(); return; }
      var hr = e.target.closest('[data-hr]'), hc = e.target.closest('[data-hc]');
      if (hr) { S.act = { r: +hr.dataset.hr, c: 0 }; S.anc = { r: S.act.r, c: 11 }; paint(true); gridEl.focus({ preventScroll: true }); } else if (hc) { S.anc = { r: T.rows.length - 1, c: +hc.dataset.hc }; S.act = { r: 0, c: S.anc.c }; paint(true); gridEl.focus({ preventScroll: true }); } });
    gridEl.addEventListener('mouseover', function (e) { if (!dragging) return; var td = e.target.closest('td.c'); if (td) { S.act = { r: +td.dataset.r, c: +td.dataset.c }; paint(true); } });
    gridEl.addEventListener('dblclick', function (e) { if (e.target.closest('td.c')) startEdit(); });
    gridEl.addEventListener('copy', function (e) { if (editing) return; var g = range(), rows = []; for (var r = g.r0; r <= g.r1; r++) { var line = []; for (var c = g.c0; c <= g.c1; c++) { var v = T.cells[r][c].v; line.push(v == null ? '' : String(v)); } rows.push(line.join('\t')); } e.clipboardData.setData('text/plain', rows.join('\n')); e.preventDefault(); E.Toast('Copied ' + plural(rows.length * (g.c1 - g.c0 + 1), 'cell', 'cells')); });
    gridEl.addEventListener('paste', function (e) { if (editing) return; e.preventDefault(); var ro = readOnlyReason(); if (ro) { E.Toast(ro); return; }
      var txt = (e.clipboardData || window.clipboardData).getData('text'), l = [], skipped = 0; txt.replace(/\r/g, '').replace(/\n$/, '').split('\n').forEach(function (line, i) { line.split('\t').forEach(function (s, j) { var r = S.act.r + i, c = S.act.c + j; if (r >= T.rows.length || c > 11) { skipped++; return; } var v = parseNum(s); if (v === undefined || !canEdit(r, c)) { skipped++; return; } l.push({ r: r, c: c, v: v }); }); });
      var n = applyChanges(l); E.Toast('Pasted ' + plural(n, 'cell', 'cells') + (skipped ? ' · ' + skipped + ' skipped (locked, calculated or not a number)' : '')); });
  }
  document.addEventListener('mouseup', function () { dragging = false; });

  /* ================= save state (словами) ================= */
  function renderSave() { var el = q('#doc-save'); if (!el) return; var n = dirtyCount(), ro = readOnlyReason(), html, cls = 'save';
    if (ro && !n) html = lockedSheet(S.s) ? ic('check', 14) + 'Saved ' + save.time : ic('lock', 14) + 'Read-only';
    else if (save.mode === 'saving') html = '<span class="spin"></span>Saving…';
    else if (save.mode === 'failed' && n) { cls += ' fail'; html = ic('alertCircle', 14) + '<span title="The last save did not reach the server. Changes are kept for the whole document — switching sheets does not lose them.">' + plural(n, 'unsaved change', 'unsaved changes') + '</span> · <button type="button" class="link-btn" id="doc-retry">Retry</button>'; }
    else if (n) html = ic('pencil', 14) + '<span title="Changes are kept for the whole document — switching sheets does not lose them.">' + plural(n, 'unsaved change', 'unsaved changes') + '</span>';
    else html = ic('check', 14) + 'Saved ' + save.time;
    el.className = cls; el.innerHTML = html; var b = q('#doc-retry'); if (b) b.addEventListener('click', retrySave); }
  function scheduleAutosave() { clearTimeout(save.timer); if (save.mode !== 'failed') { save.mode = 'dirty'; save.timer = setTimeout(function () { save.mode = 'saving'; renderSave(); save.timer = setTimeout(finishSave, 800); }, 1500); } renderSave(); }
  function finishSave() { eachCell(function (cl) { cl.dirty = false; cl.orig = undefined; }); save.mode = 'saved'; save.time = F.time(new Date()); if (T && root && document.contains(root)) { renderBody(); renderSave(); } }
  function discardChanges() { eachCell(function (cl) { if (cl.dirty) { cl.v = cl.orig === undefined ? cl.v : cl.orig; cl.dirty = false; cl.orig = undefined; } }); Object.keys(cache).forEach(function (k) { recalc(cache[k]); validate(cache[k]); }); save.mode = 'saved'; }
  function retrySave() { save.mode = 'saving'; renderSave(); clearTimeout(save.timer); save.timer = setTimeout(function () { if (!save.conflictShown && S.docId === MAIN) { save.conflictShown = true; save.mode = 'failed'; renderSave(); ctx.openDialog('conflict'); } else finishSave(); }, 800); }

  /* ================= header: стан, степер, дії ================= */
  function renderState() { var st = sheet().state, step = st === 'Approved' ? 2 : st === 'Submitted' ? 1 : 0, box = q('#doc-state'); box.textContent = '';
    box.appendChild(E.StatusBadge('sheet', st, { label: 'Sheet: ' + st })); box.appendChild(E.Stepper({ steps: ['Draft', 'Submitted', 'Approved'], current: step, tone: st === 'Rejected' ? 'danger' : st === 'Returned' ? 'warning' : '' })); }
  function renderActions() {
    var box = q('#doc-actions'), st = sheet().state, ro = !!readOnlyReason(), edit = editableState() && periodOpen(), c = counts(); box.textContent = '';
    var more = [{ label: 'Validate', icon: 'checkCircle', onClick: runValidate, smOnly: true, disabled: !edit }, { label: 'Import from Excel…', icon: 'upload', disabled: ro, onClick: function () { ctx.openDialog('import-file'); } }, { label: 'Export to Excel', icon: 'download', onClick: function () { ctx.openDialog('export-progress'); } },
      { label: 'Recalculate', icon: 'refresh', kbd: 'F9', disabled: ro, onClick: doRecalc }, { label: 'Rounded values…', icon: 'ruler', onClick: function () { ctx.openDialog('rounded-list'); } }, { label: 'Cell history', icon: 'history', onClick: function () { openInspector('history'); } }];
    if (st === 'Submitted' && E.can('Document.Submit')) more.push({ sep: true }, { label: 'Recall submission', icon: 'return', onClick: function () { setSheetState('Draft'); E.Toast('Submission recalled — the sheet is a draft again', { action: { label: 'Undo', onClick: function () { setSheetState('Submitted'); } } }); } });
    more.push({ sep: true }, { label: 'Delete sheet…', icon: 'trash', danger: true, disabled: ro || !E.can('Document.Delete'), title: E.can('Document.Delete') ? null : 'Requires the permission “Delete documents and sheets”', onClick: function () { ctx.openDialog('delete-sheet'); } });
    if (edit && E.can('Document.Submit')) box.appendChild(E.Button({ label: 'Validate', icon: 'checkCircle', id: 'doc-validate', onClick: runValidate }));
    if (st === 'Submitted' && E.can('Document.Approve')) box.appendChild(E.Button({ label: 'Reject', icon: 'xCircle', id: 'doc-reject', onClick: function () { ctx.openDialog('reject-reason'); } }));
    box.appendChild(E.Menu({ label: 'More', id: 'doc-more', items: more }));
    if (edit && E.can('Document.Submit')) box.appendChild(E.Button({ label: st === 'Draft' ? 'Submit' : 'Resubmit', icon: 'send', variant: 'primary', id: 'doc-submit', onClick: submit, title: c.errors ? plural(c.errors, 'error blocks', 'errors block') + ' submission' : 'Send this sheet to A. Iskakov for approval' }));
    if (st === 'Submitted' && E.can('Document.Approve')) box.appendChild(E.Button({ label: 'Approve', icon: 'check', variant: 'primary', id: 'doc-approve', onClick: approve }));
  }
  function renderBanner() {
    var bn = q('#doc-banner'), st = sheet().state, p = E.data.period(S.period), b = null; bn.textContent = '';
    if (st === 'Approved') b = E.Banner({ flush: true, icon: 'lock', title: 'Approved 18 Sep 2026 by A. Iskakov', text: 'This sheet is read-only. Figures keep full contrast; nothing is hatched.', actions: E.can('Document.Reopen') ? [{ label: 'Return for edits', icon: 'return', id: 'doc-return', onClick: function () { ctx.openDialog('return-reason'); } }] : [] });
    else if (st === 'Submitted') b = E.Banner({ flush: true, icon: 'send', title: 'Submitted 17 Sep 2026 by ' + S.doc.owner, text: E.can('Document.Approve') ? 'Waiting for your decision. Check the figures, then approve or reject with a reason.' : 'Waiting for ' + S.doc.approver + '. The sheet is read-only until the approver decides.' });
    else if (!periodOpen()) b = E.Banner({ flush: true, icon: 'lock', title: F.period(S.period) + ' is ' + (p.state === 'NotOpened' ? 'not opened yet' : p.state.toLowerCase()), text: 'Values are read-only.', actions: [{ label: 'Go to ' + F.period(E.data.currentPeriod), onClick: function () { setPeriod(E.data.currentPeriod); } }] });
    else if (st === 'Rejected') b = E.Banner({ flush: true, tone: 'danger', title: 'Rejected by A. Iskakov', text: '“' + (S.reason.rejected || 'NOₓ for Boiler B-2 is ten times higher than in August. Please check the fuel volume and resubmit.') + '”', dismissable: true });
    else if (st === 'Returned') b = E.Banner({ flush: true, tone: 'warning', title: 'Returned for edits by A. Iskakov', text: '“' + (S.reason.returned || 'Stack test results for FS-101 arrived after approval. Please update table 1.6 and resubmit.') + '”', dismissable: true });
    if (b) bn.appendChild(b); q('#doc-work').classList.toggle('readonly', !!readOnlyReason());
  }
  function showResult(node) { var slot = root && q('#doc-result'); if (!slot) return; slot.textContent = ''; if (node) slot.appendChild(node); }
  function nextOpenSheet() { for (var i = 1; i <= S.doc.sheets.length; i++) { var k = (S.s + i) % S.doc.sheets.length, st = S.doc.sheets[k].state; if (k !== S.s && (st === 'Draft' || st === 'Returned' || st === 'Rejected')) return k; } return -1; }

  /* ================= navigator: лише поточний аркуш, групи згорнуті ================= */
  function renderProgress() { var sh = sheet(), st = sheetStatuses(S.s), filled = st.filter(function (x) { return x !== 'empty'; }).length, c = counts(), el = q('#doc-progress'); el.textContent = '';
    el.appendChild(E.Progress({ value: filled, max: st.length, label: false, ariaLabel: 'Tables filled' }));
    var line = h('div', { class: 'dp-l' }, h('b', filled + ' of ' + st.length), ' tables filled');
    if (c.all) { line.appendChild(document.createTextNode(' · ')); var lk = lockedSheet(S.s); line.appendChild(h('button', { class: 'link-btn ' + (lk ? 'muted' : c.errors ? 'danger-text' : 'warning-text'), type: 'button', id: 'doc-progress-issues', title: plural(c.errors, 'error', 'errors') + ', ' + plural(c.warnings, 'warning', 'warnings') + ' — open the list', on: { click: function () { openInspector('issues'); } } }, lk ? plural(c.warnings, 'warning', 'warnings') : plural(c.all, 'issue', 'issues'))); }
    el.appendChild(line); sh.issues = c.all; }
  function renderTree() {
    var html = '', qq = S.navQ.toLowerCase(), tables = TABLES[S.s], curG = tables[S.t].g, groups = GROUPS[S.s], shown = 0;
    groups.forEach(function (g, gi) { var rows = '', n = 0, errs = 0; tables.forEach(function (tb, t) { if (tb.g !== gi) return; var st = tableStatus(S.s, t); if (st.n) errs += st.n; if (qq && (tb.no + ' ' + tb.name).toLowerCase().indexOf(qq) < 0) return; if (S.navErr && st.st !== 'error') return; n++;
        rows += '<button type="button" class="tree-table" data-t="' + t + '" id="doc-tt-' + t + '"' + (t === S.t ? ' aria-current="true"' : '') + ' title="' + esc(tb.no + ' ' + tb.name + ' — ' + st.st + (st.n ? ', ' + plural(st.n, 'error', 'errors') : '')) + '"><i class="dot ' + st.st + '"></i><span class="no">' + tb.no + '</span><span class="nm">' + esc(tb.short) + '</span>' + (st.n ? '<span class="count bad">' + st.n + '</span>' : '') + '</button>'; });
      if ((qq || S.navErr) && !n) return; shown += n; var open = (qq || S.navErr) ? true : S.groupsOpen[S.s + ':' + gi] != null ? S.groupsOpen[S.s + ':' + gi] : gi === curG;
      html += '<button type="button" class="tree-group" data-g="' + gi + '" id="doc-tg-' + gi + '" aria-expanded="' + open + '">' + ic('chevR', 14) + '<span class="gn">' + (gi + 1) + '</span><span class="nm" title="' + esc(g[0]) + '">' + esc(g[0]) + '</span>' + (errs ? '<span class="count bad" title="' + plural(errs, 'error', 'errors') + '">' + errs + '</span>' : '<span class="st">' + g[1] + '</span>') + '</button>' + (open ? rows : ''); });
    if (!shown && (qq || S.navErr)) html = '<div class="mini-empty">' + ic('search', 20) + '<span>No tables ' + (qq ? 'match “' + esc(S.navQ) + '”' : '') + (S.navErr ? ' with errors' : '') + '.</span><button type="button" class="btn" id="doc-nav-reset">Clear filter</button></div>';
    q('#doc-tree').innerHTML = html; renderProgress();
  }
  function gotoTable(t, r, c) { S.t = t; S.act = { r: r || 0, c: c || 0 }; S.anc = { r: S.act.r, c: S.act.c }; delete S.groupsOpen[S.s + ':' + TABLES[S.s][t].g]; ctx.setQuery({ table: t || null }); renderDoc(); }
  function setNav(open) { S.navOpen = open; q('#doc-nav').hidden = !open; q('#doc-nav-toggle').setAttribute('aria-pressed', String(open)); }

  /* ================= inspector: закритий за замовчуванням ================= */
  var USERS = ['D. Akhmetova', 'M. Petrenko', 'S. Nurlanov', 'svc-import'], ORIGIN = { UserEdit: 'pencil', Import: 'upload', Recalculation: 'refresh' };
  function openInspector(tab) { S.inspTab = tab || S.inspTab; S.inspOpen = true; q('#doc-insp').hidden = false; ctx.setQuery({ panel: S.inspTab }); renderInspector(); renderCounts(); var f = q('#doc-insp-tab-' + S.inspTab); if (f) f.focus(); }
  function closeInspector(refocus) { S.inspOpen = false; q('#doc-insp').hidden = true; ctx.setQuery({ panel: null }); renderCounts(); if (refocus) q('#doc-issues-btn').focus(); }
  function renderInspector() {
    var c = counts(); ['issues', 'history', 'info'].forEach(function (t) { var b = q('#doc-insp-tab-' + t); b.setAttribute('aria-selected', String(t === S.inspTab)); }); q('#doc-insp-count').textContent = c.all; q('#doc-insp-count').hidden = !c.all; q('#doc-insp-count').className = 'count ' + (c.errors ? 'bad' : 'warn');
    var body = q('#doc-insp-body'), html = '';
    if (S.inspTab === 'issues') { var all = allIssues(), lastKey = '';
      if (!all.length) html = '<div class="mini-empty">' + ic('checkCircle', 20) + '<b class="t-sb">No issues found</b><span>The last validation run found nothing to fix on “' + esc(sheet().name) + '” for ' + F.period(S.period) + '.</span></div>';
      all.forEach(function (it, i) { if (it.T.key !== lastKey) { lastKey = it.T.key; var tb = TABLES[it.T.s][it.T.t]; html += '<div class="group-h"><span>' + esc(tb.no + ' ' + tb.name) + '</span><span>' + issueList(it.T).length + '</span></div>'; }
        html += '<button type="button" class="issue" data-issue="' + i + '" id="doc-issue-' + i + '"><span class="' + (it.sev === 'error' ? 'e' : 'w') + '">' + ic(it.sev === 'error' ? 'alertCircle' : 'alert') + '</span><span>' + esc(it.msg) + '</span><span class="where"><span class="chip">' + addr(it.r, it.c) + '</span>' + esc(it.T.rows[it.r]) + ' · ' + esc(it.T.cols[it.c][0]) + '<span title="' + it.code + '">' + (it.sev === 'error' ? 'Error' : 'Warning') + '</span></span></button>'; });
      body.innerHTML = html; Array.prototype.forEach.call(body.querySelectorAll('[data-issue]'), function (b) { b.addEventListener('click', function () { var it = all[+b.dataset.issue]; if (it.T.t !== S.t) gotoTable(it.T.t, it.r, it.c); else setActive(it.r, it.c); if (window.innerWidth <= 1180) closeInspector(); gridEl.focus({ preventScroll: true }); }); }); }
    else if (S.inspTab === 'history') { var cl = T.cells[S.act.r][S.act.c], col = T.cols[S.act.c], R = rng(S.act.r * 131 + S.act.c * 17 + T.t * 7 + 3), items = [];
      if (cl.dirty) items.push({ who: 'You', when: 'just now · not saved', o: 'UserEdit', a: cl.orig, b: cl.v, unsaved: true }); var v = cl.dirty ? cl.orig : cl.v;
      if (v != null || cl.dirty) { var n = col[3] === 'calc' ? 2 : 1 + Math.floor(R() * 3), day = 17; for (var i = 0; i < n && v != null; i++) { var last = i === n - 1, prev = last ? null : +(v * (0.85 + R() * 0.3)).toFixed(4), o = col[3] === 'calc' ? 'Recalculation' : col[3] === 'locked' ? 'Import' : last ? 'Import' : (R() < 0.7 ? 'UserEdit' : 'Import');
          items.push({ who: o === 'Recalculation' ? 'System' : USERS[Math.floor(R() * 4)], when: day + ' Sep 2026, ' + ('0' + (8 + Math.floor(R() * 9))).slice(-2) + ':' + ('0' + Math.floor(R() * 60)).slice(-2), o: o, a: prev, b: v }); v = prev; day -= 1 + Math.floor(R() * 4); } }
      if (!items.length) html = '<div class="mini-empty">' + ic('history', 20) + '<b class="t-sb">No history</b><span>This cell has never had a value in ' + F.period(S.period) + '.</span></div>';
      items.forEach(function (it) { html += '<div class="hist"><div class="hist-top"><b>' + esc(it.who) + '</b><span class="muted t-xs">' + esc(it.when) + '</span></div><div class="diff">' + (it.a == null ? '<span class="muted">—</span>' : '<s>' + fmt(it.a) + '</s>') + ic('arrowR', 14) + '<span>' + (it.b == null ? '—' : fmt(it.b)) + '</span></div><div><span class="badge' + (it.unsaved ? ' warn' : ' quiet') + '">' + ic(ORIGIN[it.o], 12) + 'Origin: ' + it.o + '</span></div></div>'; });
      body.innerHTML = html; }
    else { var c2 = T.cols[S.act.c], tb2 = TABLES[S.s][S.t], cl2 = T.cells[S.act.r][S.act.c]; body.textContent = '';
      body.appendChild(h('div', { class: 'p-3' }, E.KeyValue([{ label: 'Cell', value: addr(S.act.r, S.act.c), mono: true }, { label: 'Row', value: T.rows[S.act.r] }, { label: 'Column', value: c2[0] + ', ' + c2[1] }, { label: 'Type', value: 'Decimal(18,4)', mono: true }, { label: 'Input', value: c2[3] === 'calc' ? 'Calculated on the server' : c2[3] === 'locked' ? 'Registry “Permit limits”' : 'Manual · paste · import' }, { label: 'Stored value', value: cl2.v == null ? null : String(cl2.v), mono: true }, { label: 'State', value: cellTip(S.act.r, S.act.c) || 'Editable' }, { label: 'Table', value: tb2.no + ' ' + tb2.name }, { label: 'Template', value: S.doc.template + ' · v' + S.doc.version, mono: true }, { label: 'Methodology', value: 'Order № 221-Ө, rev. 2025' }])));
      body.appendChild(E.Collapsible({ id: 'doc-legend', title: 'Cell markers', summary: '5 states, two carriers each', content: h('div', { class: 'cell-legend', html: '<div><span class="sw c is-dirty">1.25</span>Unsaved change — corner triangle</div><div><span class="sw c is-calc">ƒ 4.50</span>Calculated — tinted, ƒ in header</div><div><span class="sw c is-error" style="padding-left:18px">−2.0</span>Error — frame + marker</div><div><span class="sw c is-locked">3.75</span>Locked — hatch under text + lock</div><div><span class="sw c is-rounded"><span class="v">0.12</span></span>Rounded — dashed underline</div>' }) })); }
  }
  function renderCounts() { var c = counts(), b = q('#doc-issues-btn'), lk = lockedSheet(S.s); b.className = 'issues-btn' + (lk ? '' : c.errors ? ' bad' : c.warnings ? ' warn' : ''); b.hidden = lk && !c.warnings; b.setAttribute('aria-pressed', String(S.inspOpen && S.inspTab === 'issues')); b.innerHTML = ic(c.all ? 'alert' : 'checkCircle') + '<span>' + (lk ? plural(c.warnings, 'warning', 'warnings') : c.all ? plural(c.all, 'issue', 'issues') : 'No issues') + '</span>'; b.title = plural(c.errors, 'error', 'errors') + ', ' + plural(c.warnings, 'warning', 'warnings') + ' on this sheet — open the list';
    q('#doc-history-btn').setAttribute('aria-pressed', String(S.inspOpen && S.inspTab === 'history')); renderProgress(); }

  /* ================= sheet tabs, period, whole document ================= */
  function renderTabs() { var html = ''; S.doc.sheets.forEach(function (sh, s) { var d = sh.state === 'Approved' ? 'filled' : sh.state === 'Submitted' ? 'submitted' : sh.state === 'Rejected' ? 'error' : sh.state === 'Returned' ? 'returned' : 'draft';
      html += '<button type="button" role="tab" class="sheet-tab" id="doc-sheet-' + s + '" data-sheet-tab="' + s + '" aria-selected="' + (s === S.s) + '" title="' + esc(sh.name + ' — ' + sh.state) + '"><i class="dot ' + d + '"></i>' + esc(sh.name) + '<span class="st">· ' + sh.state + '</span></button>'; }); q('#doc-sheet-tabs').innerHTML = html; }
  function setPeriod(p) { S.period = p; periodPicker.set(p); renderDoc(); }
  function setSheet(s) { if (s === S.s) return; S.s = s; S.t = 0; S.act = { r: 0, c: 0 }; S.anc = { r: 0, c: 0 }; showResult(null); ctx.setQuery({ sheet: s || null, table: null }); renderDoc(); }
  function setSheetState(st) { sheet().state = st; delete statusCache[S.docId + ':' + S.s]; renderDoc(); }
  var periodPicker = null;
  function renderDoc() {
    var tb = TABLES[S.s][S.t]; renderState(); renderActions(); renderBanner();
    q('#doc-table-title').textContent = tb.no + ' ' + tb.name; q('#doc-table-title').title = tb.no + ' ' + tb.name;
    renderGrid(); q('#doc-table-meta').textContent = T.rows.length + ' × 12'; q('#doc-undo').disabled = !T.undo.length; q('#doc-redo').disabled = !T.redo.length;
    renderTabs(); renderTree(); renderCounts(); renderSave(); if (S.inspOpen) renderInspector();
    ctx.setCrumbs([{ label: 'Documents', href: '#/' }, { label: S.docId, href: '#/documents/' + S.docId }, { label: sheet().name }]);
    var curBtn = q('.tree-table[aria-current="true"]'); if (curBtn && curBtn.scrollIntoView) curBtn.scrollIntoView({ block: 'nearest' });
  }

  /* ================= actions & transitions ================= */
  function runValidate() { var c = counts(); if (c.all) openInspector('issues'); E.Toast(c.all ? 'Validation finished: ' + plural(c.errors, 'error', 'errors') + ', ' + plural(c.warnings, 'warning', 'warnings') : 'Validation passed — no issues', c.errors ? 'danger' : c.warnings ? 'warning' : 'success'); }
  function doRecalc() { if (readOnlyReason()) return E.Toast(readOnlyReason()); recalc(T); validate(T); renderBody(); renderCounts(); E.Toast('Recalculated ' + T.rows.length + ' formulas in this table'); }
  function submit() { var c = counts(); if (c.errors) { openInspector('issues'); E.Toast('Fix ' + plural(c.errors, 'error', 'errors') + ' before submitting “' + sheet().name + '”', 'danger'); return; }
    if (dirtyCount() && save.mode === 'failed') { E.Toast('Save your changes first — the last save did not reach the server', { tone: 'warning', action: { label: 'Retry', onClick: retrySave } }); return; }
    if (c.warnings) ctx.openDialog('submit-with-warnings'); else doSubmit(); }
  function doSubmit() { if (dirtyCount()) finishSave(); setSheetState('Submitted'); resultSubmitted(); }
  function resultSubmitted() { var nx = nextOpenSheet(); showResult(E.ResultBanner({ flush: true, id: 'doc-result-submitted', title: 'Sheet “' + sheet().name + '” submitted', text: 'Waiting for ' + S.doc.approver + '. You will see the decision here and in the documents list. The sheet is read-only until then.', actions: [{ label: 'View status', href: '#/' }, nx >= 0 ? { label: 'Go to next sheet: ' + S.doc.sheets[nx].name, icon: 'arrowR', variant: 'primary', onClick: function () { setSheet(nx); } } : null].filter(Boolean) })); }
  function approve() { setSheetState('Approved'); resultApproved(); }
  function resultApproved() { var left = S.doc.sheets.filter(function (x) { return x.state === 'Submitted'; }).length; showResult(E.ResultBanner({ flush: true, id: 'doc-result-approved', title: 'Sheet “' + sheet().name + '” approved', text: S.doc.owner + ' is notified. ' + (left ? plural(left, 'more sheet of this document waits', 'more sheets of this document wait') + ' for your decision.' : 'No other sheets of this document wait for you.'), actions: [{ label: 'Undo', onClick: function () { setSheetState('Submitted'); showResult(null); E.Toast('Approval undone'); } }, { label: 'Back to documents', href: '#/', variant: 'primary' }] })); }
  function resultRejected() { showResult(E.ResultBanner({ flush: true, tone: 'info', id: 'doc-result-rejected', title: 'Sheet “' + sheet().name + '” rejected', text: S.doc.owner + ' received your reason and can edit and resubmit the sheet.', actions: [{ label: 'Back to documents', href: '#/', variant: 'primary' }] })); }
  function resultReturned() { showResult(E.ResultBanner({ flush: true, tone: 'info', id: 'doc-result-returned', title: 'Sheet “' + sheet().name + '” returned for edits', text: 'It is a draft again with your reason attached. The approval is kept in the audit trail.', actions: [{ label: 'Open audit trail', href: '#/admin/audit' }] })); }
  function resultImported(n) { showResult(E.ResultBanner({ flush: true, id: 'doc-result-imported', title: plural(n, 'change', 'changes') + ' imported from air-emissions-sep-2026.xlsx', text: '3 cells were skipped because they are locked or calculated. Imported values are marked as unsaved until autosave finishes.', actions: [{ label: 'Undo import', icon: 'undo', id: 'doc-undo-import', onClick: function () { undoRedo(T.undo, T.redo, 'prev'); showResult(null); E.Toast('Import undone — ' + plural(n, 'cell', 'cells') + ' restored'); } }, { label: 'Validate', icon: 'checkCircle', onClick: runValidate }] })); }
  function resultExported() { showResult(E.ResultBanner({ flush: true, id: 'doc-result-exported', title: 'Export is ready', text: S.docId + '_2026-09.xlsx · 5 sheets · 1.4 MB. The file stays in My tasks for 24 hours.', actions: [{ label: 'Open My tasks', onClick: E.tasks.open }, { label: 'Download', icon: 'download', variant: 'primary', onClick: function () { E.Toast('Download started (prototype — no file is written)'); } }] })); }
  function resultConflict() { showResult(E.ResultBanner({ flush: true, id: 'doc-result-conflict', title: 'Conflicts resolved · all changes saved', text: 'Kept 2 of your values and 1 value of M. Petrenko. Both versions stay in the cell history.', actions: [{ label: 'Cell history', onClick: function () { openInspector('history'); } }] })); }
  function resultRestored() { showResult(E.Banner({ flush: true, tone: 'warning', id: 'doc-result-restored', title: 'You are signed in again · 3 unsaved changes were kept in this browser', text: 'They were entered before the session expired at 14:31 and never reached the server.', actions: [{ label: 'Discard', onClick: function () { discardChanges(); renderDoc(); showResult(null); E.Toast('3 kept changes discarded'); } }, { label: 'Restore 3 unsaved changes', variant: 'primary', id: 'doc-restore', onClick: function () { save.mode = 'dirty'; scheduleAutosave(); showResult(null); E.Toast('3 changes restored — saving…', 'success'); } }] })); }

  /* ================= dialogs ================= */
  function tableHtml(head, rows) { return '<thead><tr>' + head.map(function (x) { return '<th' + (x[1] ? ' class="num"' : '') + '>' + x[0] + '</th>'; }).join('') + '</tr></thead><tbody>' + rows.join('') + '</tbody>'; }
  function dlgConflict() { var t0 = getTable(0, 0), R = rng(409), who = ['M. Petrenko', 'S. Nurlanov', 'M. Petrenko'], when = ['18 Sep, 13:52', '18 Sep, 13:47', '18 Sep, 13:31'];
    var rows = DEMO_DIRTY.map(function (d, i) { var cl = t0.cells[d[0]][d[1]]; return { r: d[0], c: d[1], mine: cl.v, theirs: +((cl.v || 10) * (0.9 + R() * 0.25)).toFixed(4), who: who[i], when: when[i] }; }), sum = h('span', { class: 'muted' });
    function summary() { var m = 0; rows.forEach(function (x, i) { x.pick = E.$('#doc-cf-' + i + '-theirs').checked ? 'theirs' : 'mine'; if (x.pick === 'mine') m++; }); sum.textContent = 'Keeping ' + m + ' mine · ' + (rows.length - m) + ' theirs'; }
    function all(w) { rows.forEach(function (x, i) { E.$('#doc-cf-' + i + '-' + w).checked = true; }); summary(); }
    E.Dialog({ id: 'doc-dlg-conflict', wide: true, title: 'Someone else changed the same cells', body: function (b) {
      b.appendChild(h('p', 'Your save was not applied: these cells were changed by another user after you opened the sheet. Choose which value to keep for each cell.'));
      b.appendChild(h('div', { class: 'sumline' }, h('span', { class: 'chip' }, '409 · ECR-DOC-0409'), h('span', { class: 'badge warn' }, icon('alert', 12), plural(rows.length, 'conflicting cell', 'conflicting cells')), sum));
      var tbl = h('table', { class: 'data', html: tableHtml([['Row'], ['Column'], ['Mine', 1], ['Theirs', 1], ['Who'], ['When']], rows.map(function (x, i) { return '<tr><td><span class="chip">R' + (x.r + 1) + '</span> ' + esc(t0.rows[x.r]) + '</td><td><span class="chip">C' + (x.c + 1) + '</span> ' + esc(t0.cols[x.c][0]) + '</td><td class="num"><label class="pick" for="doc-cf-' + i + '-mine"><input type="radio" name="doc-cf-' + i + '" id="doc-cf-' + i + '-mine" checked>' + fmt(x.mine) + '</label></td><td class="num"><label class="pick" for="doc-cf-' + i + '-theirs"><input type="radio" name="doc-cf-' + i + '" id="doc-cf-' + i + '-theirs">' + fmt(x.theirs) + '</label></td><td>' + x.who + '</td><td class="muted">' + x.when + '</td></tr>'; })) });
      tbl.addEventListener('change', summary); b.appendChild(h('div', { class: 'table-wrap' }, tbl)); setTimeout(summary, 0); },
      footer: [{ label: 'Take all theirs', variant: 'ghost', align: 'left', keepOpen: true, id: 'doc-cf-all-theirs', onClick: function () { all('theirs'); } }, { label: 'Keep all mine', variant: 'ghost', align: 'left', keepOpen: true, id: 'doc-cf-all-mine', onClick: function () { all('mine'); } }, { label: 'Cancel', autofocus: true, id: 'doc-cf-cancel' },
        { label: 'Resolve and save', variant: 'primary', id: 'doc-cf-apply', onClick: function () { rows.forEach(function (x) { if (x.pick === 'theirs') t0.cells[x.r][x.c].v = x.theirs; }); recalc(t0); validate(t0); save.conflictShown = true; save.mode = 'saving'; renderSave(); setTimeout(function () { finishSave(); if (!root || !document.contains(root)) return; renderCounts(); resultConflict(); }, 600); } }] }); }
  function dlgImportFile() { if (readOnlyReason()) { sheet().state = 'Draft'; S.period = E.data.currentPeriod; renderDoc(); }
    var chosen = false, ctl = E.Dialog({ id: 'doc-dlg-import-file', title: 'Import from Excel', body: function (b) {
      b.appendChild(h('p', 'Use the file exported from this document — the sheet and table layout must match template ' + S.doc.template + ' v' + S.doc.version + '. Nothing is written until you review the changes.'));
      var drop = h('div', { class: 'doc-drop', id: 'doc-import-drop' }, icon('upload', 24), h('b', { class: 't-sb' }, 'Drop an .xlsx file here'), h('span', { class: 'muted t-xs' }, 'up to 20 MB · one file'), E.Button({ label: 'Choose file…', id: 'doc-import-choose', onClick: function () { chosen = true; drop.textContent = ''; E.append(drop, [icon('fileX', 24), h('b', { class: 't-sb mono' }, 'air-emissions-sep-2026.xlsx'), h('span', { class: 'muted t-xs' }, '412 KB · modified 18 Sep 2026, 13:40')]); E.$('#doc-import-go').disabled = false; E.$('#doc-import-go').focus(); } })); b.appendChild(drop); },
      footer: [{ label: 'Cancel', id: 'doc-import-cancel' }, { label: 'Upload and check', icon: 'arrowR', variant: 'primary', id: 'doc-import-go', disabled: true, keepOpen: true, onClick: function (c) {
        var steps = ['Uploading the file…', 'Reading 5 sheets…', 'Matching tables with the template…', 'Comparing 1 140 cells…'], p = 0, lbl = h('p', steps[0]), bar = h('div'); c.body.textContent = ''; c.body.appendChild(lbl); c.body.appendChild(bar); c.setFooter([{ label: 'Cancel import', id: 'doc-import-abort', onClick: function () { clearInterval(tm); } }]);
        var tm = setInterval(function () { p += 8; bar.textContent = ''; bar.appendChild(E.Progress({ value: p, tone: 'accent', ariaLabel: 'Import check progress' })); lbl.textContent = steps[Math.min(3, Math.floor(p / 26))]; if (p >= 100) { clearInterval(tm); c.close(); ctx.openDialog('import-preview'); } }, 140); } }] }); return ctl; }
  function dlgImportPreview() { if (readOnlyReason()) { sheet().state = 'Draft'; S.period = E.data.currentPeriod; renderDoc(); } var R = rng(1612 + S.t), upd = 0, nw = 0, list = [];
    for (var r = 20; r < 26 && r < T.rows.length; r++) for (var c = 2; c < 4; c++) { var was = T.cells[r][c].v, nv = was == null ? +(R() * T.cols[c][2] * 0.3).toFixed(4) : +(was * (0.88 + R() * 0.24)).toFixed(4); if (!canEdit(r, c)) continue; list.push({ r: r, c: c, was: was, v: nv }); if (was == null) nw++; else upd++; }
    E.Dialog({ id: 'doc-dlg-import-preview', wide: true, title: 'Review the import', body: function (b) {
      b.appendChild(h('div', { class: 'sumline' }, h('span', { class: 'chip' }, 'air-emissions-sep-2026.xlsx'), h('span', { class: 'badge info' }, icon('table', 12), plural(list.length, 'change', 'changes')), h('span', { class: 'muted' }, upd + ' updated · ' + nw + ' new · 3 skipped (locked or calculated)')));
      b.appendChild(h('div', { class: 'table-wrap' }, h('table', { class: 'data', html: tableHtml([['Row'], ['Column'], ['Was', 1], [''], ['Becomes', 1], ['Δ', 1]], list.map(function (x) { var d = x.was == null ? null : x.v - x.was; return '<tr><td><span class="chip">R' + (x.r + 1) + '</span> ' + esc(T.rows[x.r]) + '</td><td><span class="chip">C' + (x.c + 1) + '</span> ' + esc(T.cols[x.c][0]) + '</td><td class="num ' + (x.was == null ? 'muted' : 'was') + '">' + (x.was == null ? '—' : fmt(x.was)) + '</td><td class="muted">' + ic('arrowR', 14) + '</td><td class="num t-b">' + fmt(x.v) + '</td><td class="num muted">' + (d == null ? '<span class="tag-new">new</span>' : (d > 0 ? '+' : '') + fmt(d)) + '</td></tr>'; })) })));
      b.appendChild(h('p', 'Nothing is written until you apply. Locked and calculated cells in the file are ignored. You can undo the whole import in one step.')); },
      footer: [{ label: 'Back to file', variant: 'ghost', align: 'left', id: 'doc-im-back', onClick: function () { setTimeout(function () { ctx.openDialog('import-file'); }, 0); } }, { label: 'Cancel', id: 'doc-im-cancel' }, { label: 'Apply ' + plural(list.length, 'change', 'changes'), variant: 'primary', autofocus: true, id: 'doc-im-apply', onClick: function () { var n = applyChanges(list.map(function (x) { return { r: x.r, c: x.c, v: x.v }; })); setActive(20, 2); resultImported(n); } }] }); }
  function dlgExport() { var bar = h('div'), lbl = h('p', 'Preparing sheet 1 of 5…'), ctl, done = false;
    function paintBar(p) { bar.textContent = ''; bar.appendChild(E.Progress({ value: p, tone: 'accent', ariaLabel: 'Export progress' })); lbl.textContent = p >= 100 ? 'The file is ready.' : 'Writing sheet ' + Math.min(5, 1 + Math.floor(p / 20)) + ' of 5 · ' + S.doc.sheets[Math.min(4, Math.floor(p / 20))].name; }
    ctl = E.Dialog({ id: 'doc-dlg-export', narrow: true, title: 'Exporting ' + S.docId + ' to Excel', body: function (b) { b.appendChild(lbl); b.appendChild(bar); b.appendChild(h('p', { class: 't-xs' }, 'You can keep working: the export continues in the background and appears in My tasks.')); paintBar(0); },
      footer: [{ label: 'Run in background', id: 'doc-export-bg', autofocus: true, onClick: function () { E.Toast('Export continues in My tasks', { action: { label: 'Open', onClick: E.tasks.open } }); } }] });
    E.tasks.start({ title: 'Excel export · ' + S.docId, icon: 'download', detail: 'Started ' + F.time(new Date()), duration: 5000, doneDetail: S.docId + '_2026-09.xlsx · 1.4 MB', result: { label: 'Download', onClick: function () { E.Toast('Download started (prototype — no file is written)'); } },
      onProgress: function (p) { if (document.contains(bar)) paintBar(p); }, onDone: function () { done = true; if (document.contains(bar)) ctl.setFooter([{ label: 'Close', id: 'doc-export-close' }, { label: 'Download', icon: 'download', variant: 'primary', id: 'doc-export-dl', onClick: function () { E.Toast('Download started (prototype — no file is written)'); } }]); if (root && document.contains(root)) resultExported(); else E.Toast('Export of ' + S.docId + ' is ready', { tone: 'success', action: { label: 'Open My tasks', onClick: E.tasks.open } }); } }); }
  function dlgRounded() { var list = []; T.cells.forEach(function (row, r) { row.forEach(function (cl, c) { if (cl.rounded && cl.v != null) list.push({ r: r, c: c, v: cl.v }); }); });
    E.Dialog({ id: 'doc-dlg-rounded', wide: true, title: 'Values shown rounded · table ' + TABLES[S.s][S.t].no, body: function (b) { b.appendChild(h('p', 'These cells store more than 4 decimals. The grid shows them rounded with a dashed underline; calculations and exports use the stored value.'));
      if (!list.length) b.appendChild(E.EmptyState({ icon: 'checkCircle', title: 'No rounded values in this table', text: 'Every stored value has 4 decimals or fewer.' }));
      else b.appendChild(h('div', { class: 'table-wrap' }, h('table', { class: 'data', html: tableHtml([['Cell'], ['Row'], ['Column'], ['Stored', 1], ['Shown', 1]], list.map(function (x) { return '<tr tabindex="0" data-r="' + x.r + '" data-c="' + x.c + '"><td><span class="chip">' + addr(x.r, x.c) + '</span></td><td>' + esc(T.rows[x.r]) + '</td><td>' + esc(T.cols[x.c][0]) + '</td><td class="num">' + esc(String(x.v)) + '</td><td class="num">' + fmt(x.v) + '</td></tr>'; })), on: { click: function (e) { var tr = e.target.closest('tr[data-r]'); if (tr) { E.closeAllLayers(); setActive(+tr.dataset.r, +tr.dataset.c); gridEl.focus(); } } } }))); },
      footer: [{ label: 'Close', autofocus: true, id: 'doc-rounded-close' }] }); }
  function dlgWarnings() { var w = allIssues().filter(function (i) { return i.sev === 'warning'; }); if (!w.length) w = [{ T: T, r: 19, c: 1, msg: 'Hours exceed the period length (720 h)' }, { T: T, r: 10, c: 1, msg: 'Hours exceed the period length (720 h)' }];
    E.Dialog({ id: 'doc-dlg-submit-warnings', title: 'Submit “' + sheet().name + '” with ' + plural(w.length, 'warning', 'warnings') + '?', body: function (b) { b.appendChild(h('p', 'Warnings do not block submission, but ' + S.doc.approver + ' will see them. There are no errors.'));
      b.appendChild(h('ul', { class: 'conseq' }, w.map(function (i) { return h('li', { class: 'note' }, h('span', { class: 'warning-text' }, icon('alert')), h('span', i.msg + ' — ', h('span', { class: 'muted' }, TABLES[i.T.s][i.T.t].no + ' · ' + i.T.rows[i.r] + ' · ' + addr(i.r, i.c)))); }))); },
      footer: [{ label: 'Review warnings', id: 'doc-sw-review', onClick: function () { setTimeout(function () { openInspector('issues'); }, 0); } }, { label: 'Submit anyway', icon: 'send', variant: 'primary', id: 'doc-sw-submit', autofocus: true, onClick: function () { setTimeout(doSubmit, 0); } }] }); }
  function dlgReject() { if (sheet().state !== 'Submitted') { sheet().state = 'Submitted'; renderDoc(); } E.ReasonDialog({ id: 'doc-dlg-reject', title: 'Reject sheet “' + sheet().name + '” of ' + S.docId + '?', text: S.doc.owner + ' will get the sheet back as editable, together with your reason.', label: 'What has to be fixed', placeholder: 'e.g. NOₓ for Boiler B-2 is ten times higher than in August…', verb: 'Reject sheet', icon: 'xCircle', danger: true, onSubmit: function (reason) { S.reason.rejected = reason; setTimeout(function () { setSheetState('Rejected'); resultRejected(); }, 0); } }); }
  function dlgReturn() { if (sheet().state !== 'Approved') { sheet().state = 'Approved'; renderDoc(); } E.ReasonDialog({ id: 'doc-dlg-return', title: 'Return approved sheet “' + sheet().name + '” for edits?', text: 'The approval is withdrawn and the sheet becomes a draft again. This is recorded in the audit trail as a critical event.', label: 'Why the sheet is reopened', verb: 'Return for edits', icon: 'return', onSubmit: function (reason) { S.reason.returned = reason; setTimeout(function () { setSheetState('Returned'); resultReturned(); }, 0); } }); }
  function dlgSession() { E.Dialog({ id: 'doc-dlg-session', narrow: true, alert: true, dismissable: false, title: 'Your session has expired', body: function (b) { b.appendChild(h('p', 'You were signed out after 30 minutes without activity. Nothing is lost: ' + plural(Math.max(3, dirtyCount()), 'unsaved change is', 'unsaved changes are') + ' kept in this browser and will be offered for restore right after you sign in.')); b.appendChild(h('div', { class: 'sumline' }, h('span', { class: 'chip' }, '401 · ECR-AUTH-0401'))); },
      footer: [{ label: 'Sign in again', icon: 'arrowR', variant: 'primary', id: 'doc-session-signin', onClick: function () { E.go('/login', { params: { 'return': '/documents/' + S.docId + '?dialog=result-restored' }, force: true }); } }] }); }
  function dlgNewVersion() { E.Dialog({ id: 'doc-dlg-newversion', narrow: true, title: 'A new version of ECR Web is ready', body: function (b) { b.appendChild(h('p', 'Version 1.8.3 was deployed while you were working. Reload to get it. Your ' + plural(dirtyCount(), 'unsaved change is', 'unsaved changes are') + ' saved first, so nothing is lost.')); b.appendChild(E.KeyValue([{ label: 'Running', value: '1.8.2', mono: true }, { label: 'Available', value: '1.8.3 · build 2026.09.18.2', mono: true }])); },
      footer: [{ label: 'Later', id: 'doc-nv-later', onClick: function () { E.Toast('You will be reminded in 30 minutes'); } }, { label: 'Save and reload', icon: 'refresh', variant: 'primary', autofocus: true, id: 'doc-nv-reload', onClick: function () { finishSave(); E.Toast('Saved — reloading (prototype: no reload)', 'success'); } }] }); }
  function dlgDelete() { E.ConfirmDialog({ id: 'doc-dlg-delete', title: 'Delete sheet “' + sheet().name + '” from ' + S.docId + '?', consequences: [plural(TABLES[S.s].length, 'table', 'tables') + ' and every value entered in them for ' + F.period(S.period) + ' will be removed.', '2 formulas on sheet “GHG inventory” read from this sheet and will stop calculating.', { text: 'The audit trail keeps the record of who deleted it. This cannot be undone.', note: true }], verb: 'Delete sheet', icon: 'trash', onConfirm: function () { E.Toast('Sheet deleted (prototype — nothing was removed)'); } }); }

  /* ================= screen ================= */
  E.screen('/documents/:id', { title: 'Document', navPath: '/', example: '/documents/DOC-000001', render: function (el, c) {
    ctx = c; root = el; var doc = E.data.doc(c.params.id);
    if (!doc) { el.appendChild(h('div', { class: 'page' }, E.ErrorState({ title: 'This document could not be loaded', text: 'The server says: document ' + c.params.id + ' does not exist or was deleted.', code: 'ECR-DOC-0404', back: { label: 'Documents', href: '#/' } }))); return; }
    if (c.state !== 'data') { el.appendChild(h('div', { class: 'page' }, E.PageHeader({ ctx: c, id: 'doc', title: doc.id, mono: true, back: { label: 'Documents', href: '#/' } }), E.StateSwitch(c.state, { loading: E.Skeleton({ kind: 'table', rows: 14, cols: 8 }), error: { title: 'This document could not be loaded', code: 'ECR-DOC-0503', back: { label: 'Documents', href: '#/' } }, empty: { icon: 'table', title: 'This document has no sheets', text: 'Template ' + doc.template + ' v' + doc.version + ' was published without sheets. Ask a template administrator to add them, then recreate the document.', actions: [{ label: 'Open the template', href: '#/admin/templates/' + doc.template }] } }))); return; }
    if (S.docId !== doc.id) { S.docId = doc.id; S.doc = doc; S.s = 0; S.t = 0; S.act = { r: 0, c: 0 }; S.anc = { r: 0, c: 0 }; S.result = null; S.inspOpen = false; if (doc.id !== MAIN && save.mode === 'failed') save.mode = 'saved'; }
    var qy = c.query; if (qy.sheet != null && doc.sheets[+qy.sheet]) { if (S.s !== +qy.sheet) { S.s = +qy.sheet; S.t = 0; } } if (qy.table != null && TABLES[S.s][+qy.table]) S.t = +qy.table;
    if (qy.sheetState && E.StatusBadge.tone('sheet', qy.sheetState) != null && ['Draft', 'Submitted', 'Approved', 'Rejected', 'Returned'].indexOf(qy.sheetState) >= 0) sheet().state = qy.sheetState;
    if (qy.issues && !S.fixed) fixAll(qy.issues); if (qy.period && E.data.period(qy.period)) S.period = qy.period;
    S.inspOpen = ['issues', 'history', 'info'].indexOf(qy.panel) >= 0; if (S.inspOpen) S.inspTab = qy.panel;
    if (S.navOpen == null || window.innerWidth <= 900) S.navOpen = window.innerWidth > 900;
    c.setTitle(doc.id + ' · ' + doc.facility);
    var back = c.from || { label: 'Documents', href: '#/' };
    el.innerHTML = '<div class="editor-head doc-head"><div class="eh-title"><a class="ph-back" data-back="1" href="' + esc(back.href) + '" style="align-self:center">' + ic('arrowL', 14) + 'Back to ' + esc(back.label) + '</a><h1 class="mono" id="doc-h1" tabindex="-1" data-page-title="1">' + esc(doc.id) + '</h1><span class="muted truncate" title="Project ' + esc(doc.project) + ' · template ' + esc(doc.template) + ' v' + esc(doc.version) + '">' + esc(doc.facility) + ' · <span class="mono">' + esc(doc.project) + '</span></span></div>' +
      '<div class="eh-ctx"><span id="doc-period-slot"></span><span class="doc-state" id="doc-state"></span><div class="save" id="doc-save" role="status" aria-live="polite"></div><div class="eh-actions" id="doc-actions"></div></div></div>' +
      '<div id="doc-result"></div><div class="split">' +
      '<aside class="side doc-nav" id="doc-nav" aria-label="Table navigator"><div class="doc-progress" id="doc-progress"></div><div class="nav-tools"><div class="with-icon">' + ic('search', 14) + '<input class="input" id="doc-nav-search" type="search" placeholder="Filter tables" aria-label="Filter tables of this sheet"></div><button class="icon-btn" type="button" id="doc-nav-errors" aria-pressed="false" title="Show only tables with errors" aria-label="Show only tables with errors">' + ic('alert') + '</button></div><div class="tree" id="doc-tree"></div></aside>' +
      '<div class="main doc-work" id="doc-work"><div id="doc-banner"></div><div class="tablebar"><button class="icon-btn" type="button" id="doc-nav-toggle" aria-pressed="true" title="Table navigator" aria-label="Toggle table navigator">' + ic('panelL') + '</button><span class="vsep"></span><h2 id="doc-table-title"></h2><span class="meta" id="doc-table-meta"></span><span class="sp"></span>' +
      '<button class="icon-btn" type="button" id="doc-undo" title="Undo (Ctrl+Z)" aria-label="Undo" disabled>' + ic('undo') + '</button><button class="icon-btn" type="button" id="doc-redo" title="Redo (Ctrl+Y)" aria-label="Redo" disabled>' + ic('redo') + '</button><span class="vsep"></span><button class="issues-btn" type="button" id="doc-issues-btn" aria-pressed="false"></button><button class="icon-btn" type="button" id="doc-history-btn" aria-pressed="false" title="History of the selected cell" aria-label="History of the selected cell">' + ic('history') + '</button></div>' +
      '<div class="fbar" aria-label="Formula bar"><div class="fbar-addr" id="doc-fbar-addr">R1 · C1</div><div class="fbar-fx" aria-hidden="true">fx</div><div class="fbar-val" id="doc-fbar-val"></div><div class="fbar-col" id="doc-fbar-col"></div></div>' +
      '<div class="grid-wrap" id="doc-grid" tabindex="0" role="grid" aria-label="Table data. Arrow keys move, Enter edits, Ctrl+V pastes from Excel."></div><div class="statusbar"><div class="sheet-tabs" id="doc-sheet-tabs" role="tablist" aria-label="Sheets"></div><div class="selstats" id="doc-selstats"></div></div></div>' +
      '<aside class="aside doc-insp" id="doc-insp" aria-label="Inspector" hidden><div class="pane-h"><h2>Inspector <span class="chip" id="doc-insp-addr">R1 · C1</span></h2><button class="icon-btn" type="button" id="doc-insp-close" aria-label="Close inspector" title="Close inspector">' + ic('x') + '</button></div><div class="tabs inset" role="tablist" aria-label="Inspector sections"><button class="tab" type="button" role="tab" id="doc-insp-tab-issues">Issues<span class="count bad" id="doc-insp-count">0</span></button><button class="tab" type="button" role="tab" id="doc-insp-tab-history">History</button><button class="tab" type="button" role="tab" id="doc-insp-tab-info">Info</button></div><div class="insp-body" id="doc-insp-body" role="tabpanel"></div></aside></div>';
    gridEl = q('#doc-grid'); bindGrid();
    periodPicker = E.PeriodPicker({ id: 'doc-period', value: S.period, onChange: function (p) { S.period = p; showResult(null); renderDoc(); } }); q('#doc-period-slot').appendChild(periodPicker);
    q('#doc-nav').hidden = !S.navOpen; q('#doc-nav-toggle').setAttribute('aria-pressed', String(S.navOpen)); q('#doc-insp').hidden = !S.inspOpen;
    q('#doc-nav-search').value = S.navQ; q('#doc-nav-errors').setAttribute('aria-pressed', String(S.navErr));
    q('#doc-tree').addEventListener('click', function (e) { var tb = e.target.closest('.tree-table'), g = e.target.closest('.tree-group'), rs = e.target.closest('#doc-nav-reset');
      if (tb) { gotoTable(+tb.dataset.t); if (window.innerWidth <= 900) setNav(false); } else if (g) { var k = S.s + ':' + g.dataset.g, open = g.getAttribute('aria-expanded') === 'true'; S.groupsOpen[k] = !open; renderTree(); var nb = q('#doc-tg-' + g.dataset.g); if (nb) nb.focus(); }
      else if (rs) { S.navQ = ''; S.navErr = false; q('#doc-nav-search').value = ''; q('#doc-nav-errors').setAttribute('aria-pressed', 'false'); renderTree(); } });
    q('#doc-nav-search').addEventListener('input', function () { S.navQ = this.value.trim(); renderTree(); });
    q('#doc-nav-errors').addEventListener('click', function () { S.navErr = !S.navErr; this.setAttribute('aria-pressed', String(S.navErr)); renderTree(); });
    q('#doc-nav-toggle').addEventListener('click', function () { setNav(q('#doc-nav').hidden); });
    q('#doc-undo').addEventListener('click', function () { undoRedo(T.undo, T.redo, 'prev'); }); q('#doc-redo').addEventListener('click', function () { undoRedo(T.redo, T.undo, 'next'); });
    q('#doc-issues-btn').addEventListener('click', function () { if (S.inspOpen && S.inspTab === 'issues') closeInspector(); else openInspector('issues'); });
    q('#doc-history-btn').addEventListener('click', function () { if (S.inspOpen && S.inspTab === 'history') closeInspector(); else openInspector('history'); });
    q('#doc-insp-close').addEventListener('click', function () { closeInspector(true); });
    ['issues', 'history', 'info'].forEach(function (t) { q('#doc-insp-tab-' + t).addEventListener('click', function () { openInspector(t); }); });
    q('#doc-sheet-tabs').addEventListener('click', function (e) { var b = e.target.closest('[data-sheet-tab]'); if (b) setSheet(+b.dataset.sheetTab); });
    el.addEventListener('keydown', function (e) { if (e.key === 'Escape' && S.inspOpen && !E._layers.length && !editing && el.querySelector('#doc-insp').contains(document.activeElement)) closeInspector(true); });

    /* адресовані діалоги, панелі, дії палітри, перемикачі прототипу */
    c.dialog('conflict', dlgConflict); c.dialog('import-file', dlgImportFile); c.dialog('import-preview', dlgImportPreview); c.dialog('export-progress', dlgExport); c.dialog('rounded-list', dlgRounded);
    c.dialog('submit-with-warnings', dlgWarnings); c.dialog('reject-reason', dlgReject); c.dialog('return-reason', dlgReturn); c.dialog('session-expired', dlgSession); c.dialog('new-version-available', dlgNewVersion); c.dialog('delete-sheet', dlgDelete);
    c.dialog('result-submitted', function () { sheet().state = 'Submitted'; renderDoc(); resultSubmitted(); }); c.dialog('result-approved', function () { sheet().state = 'Approved'; renderDoc(); resultApproved(); }); c.dialog('result-rejected', function () { sheet().state = 'Rejected'; renderDoc(); resultRejected(); });
    c.dialog('result-returned', function () { sheet().state = 'Returned'; renderDoc(); resultReturned(); }); c.dialog('result-imported', function () { resultImported(12); }); c.dialog('result-exported', resultExported); c.dialog('result-conflict-resolved', resultConflict); c.dialog('result-restored', resultRestored);
    c.panel('issues', function () { openInspector('issues'); }); c.panel('history', function () { openInspector('history'); }); c.panel('info', function () { openInspector('info'); });
    c.action('Validate sheet', 'checkCircle', runValidate); c.action('Submit sheet for approval', 'send', submit); c.action('Import from Excel…', 'upload', function () { c.openDialog('import-file'); }); c.action('Export document to Excel', 'download', function () { c.openDialog('export-progress'); }); c.action('Recalculate formulas', 'refresh', doRecalc); c.action('Show issues', 'alert', function () { openInspector('issues'); });
    TABLES[S.s].forEach(function (tb, t) { if (t % 7 === 0) c.action('Go to table ' + tb.no + ' ' + tb.name, 'table', function () { gotoTable(t); }); });
    c.proto({ id: 'sheet-state', label: 'Sheet state', value: sheet().state, options: ['Draft', 'Submitted', 'Approved', 'Rejected', 'Returned'], onChange: function (v) { showResult(null); setSheetState(v); } });
    c.proto({ id: 'save', label: 'Save state', buttons: [{ label: 'Saved', onClick: function () { clearTimeout(save.timer); finishSave(); } }, { label: 'Saving…', onClick: function () { clearTimeout(save.timer); save.mode = 'saving'; renderSave(); } }, { label: 'Failed', onClick: function () { clearTimeout(save.timer); if (!dirtyCount()) { var t0 = getTable(0, 0, true); DEMO_DIRTY.forEach(function (d) { var cl = t0.cells[d[0]][d[1]]; cl.dirty = true; if (cl.orig === undefined) cl.orig = +((cl.v || 10) * 0.92).toFixed(4); }); renderBody(); } save.mode = 'failed'; renderSave(); } }] });
    c.proto({ id: 'issues', label: 'Validation (demo)', buttons: [{ label: 'Fix errors, keep warnings', onClick: function () { fixAll('warnings'); renderDoc(); } }, { label: 'Fix everything', onClick: function () { fixAll('clean'); renderDoc(); } }] });
    c.hasUnsaved(function () { return dirtyCount() > 0; }, { count: dirtyCount, text: 'Changes are kept for the whole document while you move between sheets, but they are lost if you leave the document without saving.', onSave: finishSave, onDiscard: discardChanges });
    c.onLeave(function () { clearTimeout(save.timer); if (save.mode === 'dirty' || save.mode === 'saving') finishSave(); root = null; });
    renderDoc(); if (!c.query.dialog && !c.query.panel) gridEl.focus({ preventScroll: true });
  } });

  /* ================= flows (карта переходів — /flows) ================= */
  var D1 = '#/documents/DOC-000001';
  E.flow('doc-main', { group: 'Reporting — document', title: 'Enter data → validate → submit → approve', actor: 'Data entry, then Approver', note: 'The main journey. Steps 6–7 switch the role with ?as=Approver.', steps: [
    { label: 'Sign in', href: '#/login' }, { label: 'Documents of the period', href: '#/' }, { label: 'Open DOC-000001 (calm default)', href: D1 }, { label: 'Validate → issues list', href: D1 + '?panel=issues' }, { label: 'Errors fixed, warnings left', href: D1 + '?issues=warnings' },
    { label: 'Submit with warnings?', href: D1 + '?issues=warnings&dialog=submit-with-warnings' }, { label: 'Result: submitted · what next', href: D1 + '?issues=warnings&dialog=result-submitted' }, { label: 'Approver opens the sheet', href: D1 + '?as=Approver&sheetState=Submitted' }, { label: 'Result: approved', href: D1 + '?as=Approver&dialog=result-approved' }, { label: 'Approved sheet · lock banner', href: D1 + '?as=Approver&sheetState=Approved' }] });
  E.flow('doc-reject', { group: 'Reporting — document', title: 'Reject with a reason → fix → resubmit', actor: 'Approver, then Data entry', steps: [{ label: 'Submitted sheet', href: D1 + '?as=Approver&sheetState=Submitted' }, { label: 'Reject: reason is required', href: D1 + '?as=Approver&dialog=reject-reason' }, { label: 'Result: rejected', href: D1 + '?as=Approver&dialog=result-rejected' }, { label: 'Author sees the reason', href: D1 + '?as=DataEntry&sheetState=Rejected' }, { label: 'Resubmit', href: D1 + '?as=DataEntry&sheetState=Rejected&issues=clean' }] });
  E.flow('doc-return', { group: 'Reporting — document', title: 'Return an approved sheet for edits', actor: 'Approver', steps: [{ label: 'Approved sheet', href: D1 + '?as=Approver&sheetState=Approved' }, { label: 'Return: reason is required', href: D1 + '?as=Approver&dialog=return-reason' }, { label: 'Result: returned', href: D1 + '?as=Approver&dialog=result-returned' }, { label: 'Author edits the returned sheet', href: D1 + '?as=DataEntry&sheetState=Returned' }] });
  E.flow('doc-import', { group: 'Reporting — document', title: 'Import from Excel', actor: 'Data entry', steps: [{ label: 'Choose a file', href: D1 + '?sheetState=Draft&dialog=import-file' }, { label: 'Review the diff', href: D1 + '?sheetState=Draft&dialog=import-preview' }, { label: 'Result · Undo import', href: D1 + '?sheetState=Draft&dialog=result-imported' }] });
  E.flow('doc-export', { group: 'Reporting — document', title: 'Export to Excel', actor: 'Anyone with Document.Export', steps: [{ label: 'Start · progress', href: D1 + '?dialog=export-progress' }, { label: 'My tasks', href: D1 + '?panel=tasks' }, { label: 'Result: file is ready', href: D1 + '?dialog=result-exported' }] });
  E.flow('doc-conflict', { group: 'Reporting — document', title: 'Save conflict (409)', actor: 'Data entry', steps: [{ label: 'Unsaved changes · Retry', href: D1 }, { label: 'Pick mine or theirs', href: D1 + '?dialog=conflict' }, { label: 'Result: resolved and saved', href: D1 + '?dialog=result-conflict-resolved' }] });
  E.flow('doc-session', { group: 'Reporting — document', title: 'Session expired with unsaved changes', actor: 'Data entry', steps: [{ label: 'Session expired', href: D1 + '?dialog=session-expired' }, { label: 'Sign in again', href: '#/login?return=' + encodeURIComponent('/documents/DOC-000001?dialog=result-restored') }, { label: 'Restore 3 unsaved changes', href: D1 + '?dialog=result-restored' }] });
  E.flow('doc-leave', { group: 'Reporting — document', title: 'Leave a document with unsaved changes', actor: 'Data entry', note: 'Switching sheets keeps the changes (the counter stays). Leaving the document asks.', steps: [{ label: 'Document with 3 unsaved changes', href: D1 }, { label: 'Switch sheet — counter stays', href: D1 + '?sheet=1' }, { label: 'Leave → Save / Discard / Stay', href: D1 + '?dialog=unsaved-guard' }] });
  E.flow('doc-misc', { group: 'Reporting — document', title: 'Edge cases of the document screen', steps: [{ label: 'Rounded values', href: D1 + '?dialog=rounded-list' }, { label: 'New app version', href: D1 + '?dialog=new-version-available' }, { label: 'Delete sheet', href: D1 + '?as=SystemAdministrator&dialog=delete-sheet' }, { label: 'Closed period', href: D1 + '?period=2026-08' }, { label: 'Loading', href: D1 + '?state=loading' }, { label: 'Load error', href: D1 + '?state=error' }, { label: 'Unknown document', href: '#/documents/DOC-000417' }] });
})(window.ECR);

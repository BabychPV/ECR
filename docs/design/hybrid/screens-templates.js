/* =====================================================================
   screens-templates.js — конструктор структури звітності («структура — це дані»).
   Маршрути:
     /admin/templates                                             перелік (ListPage + StatStrip)
     /admin/templates/:id                                         шаблон: ?tab=versions|usage
     /admin/templates/:id/versions/:versionId                     КОНСТРУКТОР: ?node=root|s:<sheet>|t:<table> · ?tab=columns|rows|formulas|rules|preview
                                                                  ?panel=c:<code>|c:new|f:<id>|f:new|r:<id>|r:new|table|issues
     …/relations   ?panel=<REL-id>|new · ?cycle=1                 зв'язки таблиць + схема
     …/compare     ?with=<versionId> · ?only=breaking · ?item=<id> порівняння версій
     …/access-matrix ?role=<Role> · ?dialog=cell-reason           матриця доступу «очима ролі»
     …/period-rules  ?panel=<PR-id>|new                           правила доступу за періодом
   ?dialog= (перелік):     new-template (&step=source|review)
   ?dialog= (шаблон):      clone-version · archive-version · edit-template · archive-template · result-created
   ?dialog= (конструктор): publish-version (&step=changes|reason|review · &issues=clean · &changes=breaking) · result-published ·
                           migrate-documents · clone-version · delete-draft · delete-sheet · delete-table · delete-column · delete-row ·
                           delete-formula · delete-rule · add-sheet · add-table · preview-version · result-presentation
   ?dialog= (relations):   delete-relation · result-cycle     ?dialog= (period-rules): delete-rule
   Демо-перемикачі адреси: issues=clean (усі проблеми публікації виправлено) · changes=breaking (у чернетці є Breaking-зміна)
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, D = E.data, F = E.fmt, icon = E.icon, esc = E.esc, rng = E.rng;
  var BASE = '/admin/templates', VP = BASE + '/:id/versions/:versionId', X = BASE + '/GEN07131100/versions/v4', XP = BASE + '/GEN07131100/versions/v3';

  E.css('tpl', [
    /* tree */
    '.tpl-side .tpl-tools{display:flex;gap:var(--s1);padding:var(--s2);border-bottom:1px solid var(--border)}',
    '.tpl-side .tpl-tools .with-icon{flex:1;min-width:0}',
    '.tpl-side .tpl-iss{display:flex;align-items:center;gap:var(--s2);width:100%;min-height:var(--ctl);padding:0 var(--s3);border:0;border-bottom:1px solid var(--border);background:transparent;text-align:left;color:var(--danger);font-weight:500}',
    '.tpl-side .tpl-iss:hover{background:var(--hover)}',
    '.tpl-side .tpl-iss.ok{color:var(--muted);font-weight:400}',
    '.tpl-tree{flex:1;min-height:0;overflow:auto;padding:var(--s1) 0}',
    '.tpl-tree .tpl-n{display:flex;align-items:center;gap:var(--s2);width:100%;height:var(--row);padding:0 var(--s2);border:0;background:transparent;text-align:left;color:var(--text)}',
    '.tpl-tree .tpl-n:hover{background:var(--hover)}',
    '.tpl-tree .tpl-n[aria-current="true"]{background:var(--accent-soft);color:var(--accent-text);font-weight:500}',
    '.tpl-tree .tpl-n .nm{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.tpl-tree .tpl-n .ct{font:400 var(--fs-xs)/1 var(--mono);color:var(--muted)}',
    '.tpl-tree .tpl-n.sheet{font-weight:500}',
    '.tpl-tree .tpl-n.sheet.off .nm{color:var(--muted);text-decoration:line-through}',
    '.tpl-tree .tpl-n.sheet[aria-expanded="true"]>svg:first-child{transform:rotate(90deg)}',
    '.tpl-tree .tpl-n.table{padding-left:var(--s5)}',
    '.tpl-tree .tpl-n.grp{padding-left:var(--s4);font-size:var(--fs-xs);font-weight:500;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}',
    '.tpl-tree .tpl-n.grp[aria-expanded="true"]>svg:first-child{transform:rotate(90deg)}',
    '.tpl-tree .tpl-n.table.in-g{padding-left:calc(var(--s4) + var(--s5))}',
    '.tpl-grp .muted{font-weight:400}',
    '.tpl-fx{min-width:0;overflow:auto}',
    '.tpl-tree .tpl-n.table .no{font:400 var(--fs-xs)/1 var(--mono);color:var(--muted);width:30px;flex:none}',
    /* centre */
    '.tpl-main{background:var(--ground)}',
    '.tpl-bar{display:flex;align-items:center;gap:var(--s2);min-height:calc(var(--ctl) + var(--s2));padding:0 var(--s3);border-bottom:1px solid var(--border);background:var(--surface);flex:none;min-width:0}',
    '.tpl-bar h2{font-size:var(--fs-md);font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0}',
    '.tpl-bar .meta{color:var(--muted);font-size:var(--fs-xs);white-space:nowrap}',
    '.tpl-bar .sp{flex:1}',
    '.tpl-pad{flex:1;min-height:0;display:flex;flex-direction:column;gap:var(--s3);padding:var(--s3) var(--s4);overflow:auto}',
    '.tpl-pad>.tabs-box{flex:1}',
    '.tpl-pad.form>*{max-width:880px;width:100%}',
    '.tpl-tb{display:flex;align-items:center;gap:var(--s3);flex:none;flex-wrap:wrap}',
    '.tpl-tb p{flex:1;min-width:200px;color:var(--muted)}',
    '.tpl-ord{display:inline-flex;align-items:center;gap:var(--s1)}',
    '.tpl-ord .ix{font:400 var(--fs-xs)/1 var(--mono);color:var(--muted);width:20px;text-align:right}',
    '.tpl-bad{display:inline-flex;align-items:center;gap:var(--s1);color:var(--danger)}',
    '.tpl-warn{display:inline-flex;align-items:center;gap:var(--s1);color:var(--warning)}',
    '.tpl-lang{display:grid;grid-template-columns:32px minmax(0,1fr);gap:var(--s2);align-items:center}',
    '.tpl-lang .lg{font:500 var(--fs-xs)/1 var(--mono);color:var(--muted)}',
    '.tpl-grid{flex:1;min-height:200px;border:1px solid var(--border);border-radius:var(--r2);overflow:hidden;display:flex;flex-direction:column}',
    '.tpl-rowlist{border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.tpl-rowlist .rw{display:flex;align-items:center;gap:var(--s2);min-height:var(--row);padding:0 var(--s2) 0 var(--s3);border-bottom:1px solid var(--grid-line)}',
    '.tpl-rowlist .rw:last-child{border-bottom:0}',
    '.tpl-rowlist .rw .nm{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.tpl-rowlist .rw .ix{font:400 var(--fs-xs)/1 var(--mono);color:var(--muted);width:24px;text-align:right}',
    /* check result in formula editor */
    '.tpl-insert{display:flex;flex-wrap:wrap;gap:var(--s1)}',
    '.tpl-insert button{border:1px solid var(--border);border-radius:var(--r1);background:var(--sunken);min-height:var(--s5);padding:0 var(--s2);font:400 var(--fs-xs)/1 var(--mono);color:var(--text)}',
    '.tpl-insert button:hover{background:var(--hover)}',
    /* timeline */
    '.tpl-tl{list-style:none;margin:0;padding:0;display:grid}',
    '.tpl-tl li{display:grid;grid-template-columns:16px minmax(0,1fr) auto;gap:0 var(--s3);padding-bottom:var(--s4);position:relative}',
    '.tpl-tl li::before{content:"";position:absolute;left:7px;top:16px;bottom:0;width:1px;background:var(--border-strong)}',
    '.tpl-tl li:last-child::before{display:none}',
    '.tpl-tl .pt{display:grid;place-items:center;width:16px;height:var(--s5);background:var(--ground);position:relative}',
    '.tpl-tl .bd{display:grid;gap:var(--s1);min-width:0}',
    '.tpl-tl .hd{display:flex;align-items:center;gap:var(--s2);flex-wrap:wrap}',
    '.tpl-tl .hd b{font:600 var(--fs-md)/1 var(--mono)}',
    '.tpl-tl .ac{display:flex;align-items:flex-start;gap:var(--s1)}',
    '.tpl-tl .old .hd b,.tpl-tl .old .nt{color:var(--muted)}',
    /* option cards, check list */
    '.tpl-opt{display:flex;gap:var(--s3);align-items:flex-start;padding:var(--s3);border:1px solid var(--border);border-radius:var(--r2);cursor:pointer;background:var(--surface)}',
    '.tpl-opt:has(input:checked){border-color:var(--accent);background:var(--accent-soft)}',
    '.tpl-opt input{margin:2px 0 0;accent-color:var(--accent)}',
    '.tpl-opt b{font-weight:600;display:block}',
    '.tpl-opt span{color:var(--muted)}',
    '.tpl-chk{list-style:none;margin:0;padding:0;display:grid;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.tpl-chk li{display:grid;grid-template-columns:16px minmax(0,1fr) auto;gap:var(--s1) var(--s2);align-items:start;padding:var(--s2) var(--s3);border-bottom:1px solid var(--grid-line)}',
    '.tpl-chk li:last-child{border-bottom:0}',
    '.tpl-chk li>svg{margin-top:2px;color:var(--muted)}',
    '.tpl-chk li.bad>svg{color:var(--danger)}',
    '.tpl-chk li.warn>svg{color:var(--warning)}',
    '.tpl-chk .tx{display:grid;gap:2px;min-width:0}',
    '.tpl-chk .tx b{font-weight:500}',
    '.tpl-chk .tx small{color:var(--muted);font-size:var(--fs-xs)}',
    '.tpl-grp{display:flex;align-items:center;gap:var(--s2);font-weight:600}',
    /* compare */
    '.tpl-cmp{flex:1;min-height:0;display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:var(--s3)}',
    '.tpl-cmp>div{min-height:0;overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.tpl-cmp .gh{position:sticky;top:0;z-index:1;display:flex;gap:var(--s2);align-items:center;min-height:var(--row);padding:0 var(--s3);background:var(--sunken);border-bottom:1px solid var(--border);font-size:var(--fs-xs);font-weight:500;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}',
    '.tpl-cmp .it{display:grid;grid-template-columns:16px minmax(0,1fr) auto;gap:var(--s2);align-items:center;width:100%;min-height:calc(var(--row) + 12px);padding:0 var(--s3);border:0;border-bottom:1px solid var(--grid-line);background:transparent;text-align:left;color:var(--text)}',
    '.tpl-cmp .it:hover{background:var(--hover)}',
    '.tpl-cmp .it[aria-current="true"]{background:var(--select)}',
    '.tpl-cmp .it>svg{color:var(--muted)}',
    '.tpl-cmp .dt-b{padding:var(--s4);display:grid;gap:var(--s3);align-content:start}',
    '.tpl-cmp .ws{display:grid;grid-template-columns:minmax(0,1fr) 16px minmax(0,1fr);gap:var(--s2);align-items:start}',
    '.tpl-cmp .ws>div{display:grid;gap:var(--s1);padding:var(--s2) var(--s3);border:1px solid var(--border);border-radius:var(--r1);background:var(--sunken);min-width:0;overflow-wrap:anywhere}',
    '.tpl-cmp .ws>svg{margin-top:var(--s3);color:var(--faint)}',
    '.tpl-pickrow{display:flex;align-items:flex-end;gap:var(--s3);flex-wrap:wrap;flex:none}',
    '.tpl-pickrow .field{min-width:220px}',
    /* matrix */
    '.tpl-mx-wrap{overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface);flex:none}',
    '.tpl-mx{width:100%;border-collapse:separate;border-spacing:0}',
    '.tpl-mx th,.tpl-mx td{height:calc(var(--row) + 12px);padding:0 var(--s3);border-bottom:1px solid var(--grid-line);border-right:1px solid var(--grid-line);text-align:left;white-space:nowrap}',
    '.tpl-mx tr:last-child th,.tpl-mx tr:last-child td{border-bottom:0}',
    '.tpl-mx th:last-child,.tpl-mx td:last-child{border-right:0}',
    '.tpl-mx thead th{background:var(--sunken);font-size:var(--fs-xs);font-weight:500;color:var(--muted);text-transform:uppercase;letter-spacing:.04em;border-bottom:1px solid var(--border-strong)}',
    '.tpl-mx tbody th{font-weight:500}',
    '.tpl-mx td{padding:0}',
    '.tpl-mx .cl{display:flex;align-items:center;gap:var(--s2);width:100%;height:calc(var(--row) + 12px);padding:0 var(--s3);border:0;background:transparent;color:var(--text);text-align:left}',
    '.tpl-mx .cl:hover{background:var(--hover)}',
    '.tpl-mx .cl svg{color:var(--muted)}',
    '.tpl-mx .cl.ro{color:var(--muted)}',
    '.tpl-mx .cl.hid{color:var(--faint);background-image:repeating-linear-gradient(135deg,var(--hatch) 0 1.5px,transparent 1.5px 7px)}',
    '.tpl-mx .cl small{color:var(--muted);font-size:var(--fs-xs)}',
    '.tpl-mx.mini th,.tpl-mx.mini td,.tpl-mx.mini .cl{height:var(--row)}',
    /* diagram */
    '.tpl-dg{overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface);flex:none;padding:var(--s2)}',
    '.tpl-dg svg{display:block}',
    '.tpl-dg .nd rect{fill:var(--sunken);stroke:var(--border-strong)}',
    '.tpl-dg .nd:hover rect{fill:var(--hover)}',
    '.tpl-dg .nd text{fill:var(--text);font:500 var(--fs-xs) var(--sans)}',
    '.tpl-dg .nd text.sub{fill:var(--muted);font-weight:400}',
    '.tpl-dg .ed{fill:none;stroke:var(--muted)}',
    '.tpl-dg .ed.bad{stroke:var(--danger)}',
    '.tpl-dg .hd{fill:var(--muted)}',
    '.tpl-dg .hd.bad{fill:var(--danger)}',
    '.tpl-dg .el{fill:var(--muted);font:400 var(--fs-xs) var(--sans);paint-order:stroke;stroke:var(--surface);stroke-width:4}',
    '.tpl-dg .el.bad{fill:var(--danger)}',
    '@media (max-width:900px){.tpl-cmp{grid-template-columns:1fr;overflow:auto}.tpl-cmp>div{overflow:visible}}',
    '@media (max-width:640px){.tpl-bar .meta{display:none}.tpl-tl li{grid-template-columns:16px minmax(0,1fr)}.tpl-tl .ac{grid-column:2}.tpl-cmp .ws{grid-template-columns:1fr}.tpl-cmp .ws>svg{display:none}}'
  ].join('\n'));

  /* ================= model: templates & versions (живе весь сеанс) ================= */
  var DESC = {
    GEN07131100: 'Monthly environmental report of the Central processing facility: air, water, waste, greenhouse gases and energy.',
    GEN07131200: 'Monthly environmental report of the gas treatment unit. Same sheets as CPF, fewer sources.',
    'GEN-UPSTREAM': 'Monthly report for well pads and water injection sites.',
    'WTR-QUARTERLY': 'Quarterly water intake and discharge report for the basin inspectorate.',
    'WST-ANNUAL': 'Annual waste passport: generation, transfers and disposal by waste code.',
    'GHG-ANNUAL-OLD': 'Annual greenhouse gas report in the 2024 form. Replaced by the GHG inventory sheet of the general report.' };
  var DRAFTS = { GEN07131100: ['1.1.0', 'G. Tulegenova', '2026-09-18T13:40', 'Total in kilograms and a stricter run-hours rule in table 1.1'], 'GEN-UPSTREAM': ['2.4.0', 'M. Tulegenov', '2026-09-16T10:12', 'Water injection tables for two new well pads'] };
  var VERS = {}, ST = {};
  function tpl(id) { return D.templates.filter(function (t) { return t.id === id; })[0] || null; }
  function vname(v) { return 'v' + v.version; }
  function versionsOf(id) {
    if (VERS[id]) return VERS[id];
    var t = tpl(id); if (!t) return [];
    function older(s, i) { var p = s.split('.').map(Number), minor = p[1] - i; return minor >= 0 ? p[0] + '.' + minor + '.0' : Math.max(0, p[0] - 1) + '.' + (10 + minor) + '.0'; }   /* набір дає «v1.0.1 старша за v1.0.0» — тут номери спадають */
    var list = D.templateVersions(id).map(function (v, i) { var dr = v.state === 'Draft'; if (i) v.version = older(t.version, i); if (dr) v.note = 'First draft — structure in progress'; return Object.assign({}, v, { publishedBy: dr ? null : (i % 2 ? 'M. Tulegenov' : 'G. Tulegenova'), publishedAt: dr ? null : v.created, edited: dr ? '2026-09-15T11:05' : null, presRev: 0, base: null }); });
    var d = DRAFTS[id];
    if (d) list.unshift({ id: 'v' + (t.versions + 1), template: id, version: d[0], state: 'Draft', created: '2026-09-12', author: d[1], edited: d[2], documents: 0, note: d[3], base: list[0].id, presRev: 0, publishedBy: null, publishedAt: null });
    return (VERS[id] = list);
  }
  function ver(id, vid) { return versionsOf(id).filter(function (v) { return v.id === vid; })[0] || null; }
  function draftOf(id) { return versionsOf(id).filter(function (v) { return v.state === 'Draft'; })[0] || null; }
  function currentOf(id) { return versionsOf(id).filter(function (v) { return v.state === 'Published'; })[0] || null; }
  function baseOf(id, v) { var l = versionsOf(id), i = l.indexOf(v); return (v.base && ver(id, v.base)) || l[i + 1] || null; }
  function nextVersion(s, part) { var p = String(s).split('.').map(Number); if (part === 'major') return (p[0] + 1) + '.0.0'; return p[0] + '.' + (p[1] + 1) + '.0'; }
  function vhref(tid, vid, sub, q) { return E.href(BASE + '/' + tid + '/versions/' + vid + (sub ? '/' + sub : ''), q); }
  function usageOf(t) { var i = D.templates.indexOf(t), n = Math.min(t.documents, D.documents.length), off = n === D.documents.length ? 0 : (i * 2) % (D.documents.length - n + 1); return D.documents.slice(off, off + n); }

  /* ---------- structure: sheets → tables → columns / rows / formulas / rules ---------- */
  var SUF_AIR = ['mass emissions', 'fuel use', 'operating hours', 'emission factors', 'monthly totals', 'stack parameters', 'permit comparison'];
  var SUF_GEN = ['monthly totals', 'by source', 'by method', 'permit comparison', 'analysis results', 'annual forecast', 'corrections', 'supporting data', 'summary', 'notes & evidence', 'year to date', 'previous period', 'deviations'];
  var GROUPS = [
    [['Stationary combustion', 7], ['Flaring', 7], ['Process vents', 7], ['Storage tanks', 7], ['Loading operations', 7], ['Fugitive emissions', 7], ['Mobile sources', 7], ['Wastewater treatment', 7], ['Sulphur recovery', 7], ['Gas treatment', 7], ['Power generation', 7], ['Laboratory & workshops', 7], ['Emergency releases', 7]],
    [['Water intake', 6], ['Water use', 6], ['Wastewater discharge', 6], ['Water quality monitoring', 6]],
    [['Hazardous waste', 13], ['Non-hazardous waste', 12], ['Waste transfers', 12]],
    [['Scope 1 — combustion', 10], ['Scope 1 — flaring', 10], ['Scope 1 — venting', 10], ['Scope 1 — fugitive', 10], ['Scope 2 — purchased energy', 9], ['Removals & offsets', 9]],
    [['Energy consumption', 6], ['Energy generation', 6]]];
  var SHEET_RU = ['Выбросы в атмосферу', 'Водопользование и сбросы', 'Отходы', 'Инвентаризация ПГ', 'Энергия'], SHEET_KK = ['Атмосфераға шығарындылар', 'Су пайдалану және төгінділер', 'Қалдықтар', 'ПГ түгендеу', 'Энергия'], SHEET_CODE = ['AIR', 'WATER', 'WASTE', 'GHG', 'ENERGY'];
  var TPL_SHEETS = { 'WTR-QUARTERLY': [1], 'GHG-ANNUAL-OLD': [3], 'WST-ANNUAL': [2, 4], 'GEN-UPSTREAM': [0, 1, 2, 3] };
  var AIR = [['FUEL_GAS', 'Fuel gas', 'Топливный газ', 'Отын газы', '10³ m³'], ['HOURS', 'Hours', 'Часы работы', 'Жұмыс сағаты', 'h'], ['SO2', 'SO₂', 'SO₂', 'SO₂', 't'], ['NOX', 'NOₓ', 'NOₓ', 'NOₓ', 't'], ['CO', 'CO', 'CO', 'CO', 't'], ['VOC', 'VOC', 'ЛОС', 'ҰОҚ', 't'], ['PM10', 'PM₁₀', 'PM₁₀', 'PM₁₀', 't'], ['PM25', 'PM₂.₅', 'PM₂.₅', 'PM₂.₅', 't'], ['H2S', 'H₂S', 'H₂S', 'H₂S', 't'], ['CH4', 'CH₄', 'CH₄', 'CH₄', 't'], ['PERMIT_LIMIT', 'Permit limit', 'Лимит по разрешению', 'Рұқсат лимиті', 't', 'lookup'], ['TOTAL', 'Total', 'Всего', 'Барлығы', 't', 'calc']];
  var GEN = [['VOLUME', 'Volume', 'Объём', 'Көлем', '10³ m³'], ['HOURS', 'Hours', 'Часы работы', 'Жұмыс сағаты', 'h'], ['COD', 'COD', 'ХПК', 'ОХТ', 't'], ['BOD5', 'BOD₅', 'БПК₅', 'ОБТ₅', 't'], ['TSS', 'TSS', 'Взвешенные вещества', 'Қалқыма заттар', 't'], ['OIL', 'Oil products', 'Нефтепродукты', 'Мұнай өнімдері', 't'], ['CL', 'Chlorides', 'Хлориды', 'Хлоридтер', 't'], ['SO4', 'Sulphates', 'Сульфаты', 'Сульфаттар', 't'], ['NO3', 'Nitrates', 'Нитраты', 'Нитраттар', 't'], ['PO4', 'Phosphates', 'Фосфаты', 'Фосфаттар', 't'], ['PERMIT_LIMIT', 'Permit limit', 'Лимит по разрешению', 'Рұқсат лимиті', 't', 'lookup'], ['TOTAL', 'Total', 'Всего', 'Барлығы', 't', 'calc']];
  var LANGS = [['en', 'EN'], ['ru', 'RU'], ['kk', 'KZ']], TYPES = ['Decimal', 'Integer', 'Text', 'Date', 'Boolean', 'Lookup'];
  var SRC_LABEL = { input: 'Entered by operator', calc: 'Calculated', lookup: 'Looked up in a registry' };

  function mkCol(a, base) { return { code: a[0], name: { en: a[1], ru: a[2], kk: a[3] }, type: a[5] === 'lookup' ? 'Lookup' : 'Decimal', scale: a[0] === 'HOURS' ? 1 : 4, unit: a[4], required: a[0] === 'HOURS' || a[0] === 'FUEL_GAS' || a[0] === 'VOLUME', src: a[5] || 'input', registry: a[5] === 'lookup' ? 'PermitLimits' : '', def: '', base: base !== false, pres: { width: 116, align: 'right', decimals: a[0] === 'HOURS' ? 1 : 4, sep: true, emph: a[5] === 'calc', hidden: false } }; }
  function ensure(tb) {
    if (tb.cols) return tb;
    var air = tb.si === 0, set = air ? AIR : GEN;
    tb.cols = set.map(function (a) { return mkCol(a); });
    tb.rows = air ? { mode: 'fixed', source: 'EmissionSources', items: D.emissionSources.slice(), max: 200, key: 'Row code', allowDelete: true } : { mode: 'dynamic', source: '', items: [], max: 200, key: 'Row code', allowDelete: true };
    tb.formulas = [{ id: 'F1', target: 'TOTAL', expr: air ? 'SUM(SO2:CH4)' : 'SUM(COD:PO4)', dialect: 'ECR expression', base: true }];
    tb.rules = [
      { id: 'R1', level: 'Error', name: 'Value must be ≥ 0', scope: '*', cond: 'Value >= 0', msg: { en: 'Value must be ≥ 0', ru: 'Значение должно быть ≥ 0', kk: 'Мән ≥ 0 болуы керек' }, base: true },
      { id: 'R2', level: 'Warning', name: 'Hours within the period length', scope: 'HOURS', cond: 'HOURS <= PeriodHours()', msg: { en: 'Hours exceed the period length', ru: 'Часы превышают длительность периода', kk: 'Сағат кезең ұзақтығынан асады' }, base: true },
      { id: 'R3', level: 'Error', name: 'Total within the permit limit', scope: 'TOTAL', cond: 'TOTAL <= PERMIT_LIMIT', msg: { en: 'Total exceeds the permit limit', ru: 'Итог превышает лимит по разрешению', kk: 'Барлығы рұқсат лимитінен асады' }, base: true }];
    tb.seq = 10; recheck(tb); return tb;
  }
  function struct(tid, vid) {
    var key = tid + '/' + vid; if (ST[key]) return ST[key];
    var t = tpl(tid), v = ver(tid, vid), idx = t._blank ? [] : (TPL_SHEETS[tid] || [0, 1, 2, 3, 4]);
    var S = { key: key, tid: tid, vid: vid, dirty: 0, savedAt: '14:03', ui: { open: {}, gopen: {}, q: '', node: null },
      sheets: idx.map(function (si) { var tables = []; GROUPS[si].forEach(function (g, gi) { for (var k = 0; k < g[1]; k++) { var sf = (si === 0 ? SUF_AIR : SUF_GEN)[k]; tables.push({ id: 't' + si + '-' + tables.length, si: si, no: (gi + 1) + '.' + (k + 1), name: { en: g[0] + ' — ' + sf, ru: '', kk: '' }, short: sf.charAt(0).toUpperCase() + sf.slice(1), group: g[0], base: true }); } });
        return { id: 's' + si, si: si, code: SHEET_CODE[si], name: { en: D.sheetNames[si], ru: SHEET_RU[si], kk: SHEET_KK[si] }, enabled: true, base: true, tables: tables }; }),
      changes: [], migrate: false, relations: null, prules: null, props: { note: v.note, lang: 'en' } };
    if (tid === 'GEN07131100' && v.state === 'Draft') seedDraft(S);
    S.saved = snap(S); return (ST[key] = S);
  }
  function snap(S) { return JSON.stringify({ sheets: S.sheets, changes: S.changes, migrate: S.migrate, relations: S.relations, prules: S.prules, props: S.props }); }
  function restore(S, json) { var o = JSON.parse(json); S.sheets = o.sheets; S.changes = o.changes; S.migrate = o.migrate; S.relations = o.relations; S.prules = o.prules; S.props = o.props; }
  function findTable(S, id) { for (var i = 0; i < S.sheets.length; i++) for (var k = 0; k < S.sheets[i].tables.length; k++) if (S.sheets[i].tables[k].id === id) return { tb: S.sheets[i].tables[k], sh: S.sheets[i] }; return null; }
  function findSheet(S, id) { return S.sheets.filter(function (s) { return s.id === id; })[0] || null; }
  function colOf(tb, code) { return tb.cols.filter(function (c) { return c.code === code; })[0] || null; }
  function tlabel(tb) { return tb.no + ' ' + tb.name.en; }
  function cname(c, lang) { return c.name[lang || 'en'] || c.name.en || c.code; }
  function addChange(S, c) { c.id = 'CH-' + (S.changes.length + 1) + '-' + c.kind.charAt(0); S.changes.push(c); return c; }

  function seedDraft(S) {
    var a = ensure(S.sheets[0].tables[0]), b = ensure(S.sheets[0].tables[1]), c = ensure(S.sheets[0].tables[4]);
    a.name.ru = 'Стационарное сжигание — валовые выбросы'; a.name.kk = 'Стационарлық жағу — жалпы шығарындылар';
    a.cols.push(mkCol(['TOTAL_KG', 'Total, kg', 'Всего, кг', 'Барлығы, кг', 'kg', 'calc'], false));
    a.formulas.push({ id: 'F2', target: 'TOTAL_KG', expr: 'TOTAL', dialect: 'ECR expression', base: false });
    a.rules[1].level = 'Error'; colOf(a, 'PERMIT_LIMIT').required = true;
    b.cols.splice(2, 0, mkCol(['ABATEMENT', '', 'Степень очистки NOₓ', '', '%'], false)); colOf(b, 'ABATEMENT').scale = 1; colOf(b, 'ABATEMENT').pres.decimals = 1;
    c.cols.push(mkCol(['YTD', 'Year to date', 'С начала года', 'Жыл басынан', 't', 'calc'], false));
    c.formulas[0].expr = 'YTD - PREV(YTD)'; c.formulas.push({ id: 'F2', target: 'YTD', expr: 'PREV(YTD) + TOTAL', dialect: 'ECR expression', base: false });
    [a, b, c].forEach(recheck);
    var sh = S.sheets[0].name.en;
    addChange(S, { kind: 'added', cls: 'Safe', type: 'Column', code: 'TOTAL_KG', sheet: sh, table: tlabel(a), title: 'Column “Total, kg” added', was: null, now: 'TOTAL_KG · Decimal (4) · kg · calculated', effect: 'Existing documents get the column on the next recalculation. Nothing the operators entered changes.' });
    addChange(S, { kind: 'added', cls: 'Safe', type: 'Formula', sheet: sh, table: tlabel(a), title: 'Formula for “Total, kg” added', was: null, now: 'TOTAL_KG = CONVERT(TOTAL, "kg")', effect: 'Calculated on the next recalculation of each document.' });
    addChange(S, { kind: 'changed', cls: 'Guarded', type: 'Rule', sheet: sh, table: tlabel(a), title: 'Rule “Hours within the period length” is now an error', was: 'Level: Warning', now: 'Level: Error', effect: 'Approved sheets stay approved. Draft and returned sheets with more than 720 h cannot be submitted until the value is corrected — 2 documents are affected today.' });
    addChange(S, { kind: 'changed', cls: 'Guarded', type: 'Column', sheet: sh, table: tlabel(a), title: 'Column “Permit limit” became required', was: 'Required: No', now: 'Required: Yes', effect: 'Rows without a permit limit get a “required” error on the next validation. The registry Permit limits already covers 40 of 42 sources.' });
    addChange(S, { kind: 'added', cls: 'Safe', type: 'Column', code: 'ABATEMENT', sheet: sh, table: tlabel(b), title: 'Column “ABATEMENT” added', was: null, now: 'ABATEMENT · Decimal (1) · % · entered by operator · optional', effect: 'Optional input: existing documents simply show an empty column.' });
    addChange(S, { kind: 'changed', cls: 'Safe', type: 'Formula', sheet: sh, table: tlabel(c), title: 'Formula for “Total” changed, “Year to date” added', was: 'TOTAL = SUM(SO2:CH4)', now: 'TOTAL = YTD - PREV(YTD) · YTD = PREV(YTD) + TOTAL', effect: 'Totals are recalculated. Values entered by operators do not change.' });
  }
  function injectBreaking(S) {
    if (S.changes.some(function (c) { return c.cls === 'Breaking'; })) return;
    var a = ensure(S.sheets[0].tables[0]), c = colOf(a, 'PM25'); if (!c) return;
    removeColumn(S, a, c);
  }
  var BREAK_EFFECT = 'Documents on the previous version hold data here. Publishing would make it unreachable, so it is blocked until you revert the change or plan a migration.';
  function breaking(S, type, sheet, table, title, was, revert) { return addChange(S, { kind: 'removed', cls: 'Breaking', type: type, sheet: sheet, table: table, title: title, was: was, now: null, revert: revert, effect: BREAK_EFFECT }); }
  function removeColumn(S, tb, c) {
    var at = tb.cols.indexOf(c), gone = tb.formulas.filter(function (f) { return f.target === c.code; }); tb.cols.splice(at, 1); tb.formulas = tb.formulas.filter(function (f) { return f.target !== c.code; }); recheck(tb);
    if (c.base) breaking(S, 'Column', findTable(S, tb.id).sh.name.en, tlabel(tb), 'Column “' + cname(c) + '” deleted', c.code + ' · ' + c.type + ' (' + c.scale + ') · ' + (c.unit || '—'), { kind: 'col', tb: tb.id, at: at, item: c, formulas: gone });
    else S.changes = S.changes.filter(function (x) { return x.code !== c.code; });
  }
  function revertChange(S, ch) {
    var r = ch.revert, f;
    if (r && r.kind === 'col' && (f = findTable(S, r.tb))) { f.tb.cols.splice(Math.min(r.at, f.tb.cols.length), 0, r.item); f.tb.formulas = f.tb.formulas.concat(r.formulas || []); recheck(f.tb); }
    if (r && r.kind === 'prop' && (f = findTable(S, r.tb)) && colOf(f.tb, r.code)) { colOf(f.tb, r.code)[r.prop] = r.value; recheck(f.tb); }
    if (r && r.kind === 'row' && (f = findTable(S, r.tb))) f.tb.rows.items.splice(r.at, 0, r.item);
    if (r && r.kind === 'table' && (f = findSheet(S, r.sh))) f.tables.splice(Math.min(r.at, f.tables.length), 0, r.item);
    if (r && r.kind === 'sheet') S.sheets.splice(Math.min(r.at, S.sheets.length), 0, r.item);
    S.changes.splice(S.changes.indexOf(ch), 1);
  }

  /* ---------- expression check (прототипний, але чесний: реагує на те, що ввели) ---------- */
  var FUNCS = ['SUM', 'AVG', 'MIN', 'MAX', 'IF', 'ROUND', 'ABS', 'CONVERT', 'PREV', 'EF', 'GWP', 'REGISTRY', 'PERIODHOURS', 'AND', 'OR', 'NOT', 'VALUE'];
  function refs(expr) { var s = String(expr).replace(/"[^"]*"/g, ' ').replace(/PREV\([^)]*\)/gi, ' '), m = s.match(/[A-Za-z_][A-Za-z0-9_]*/g) || []; return m.filter(function (x) { return FUNCS.indexOf(x.toUpperCase()) < 0; }); }
  function nearest(word, codes) { var w = word.toUpperCase(), best = null; codes.forEach(function (c) { if (c.indexOf(w) === 0 || w.indexOf(c) === 0 || (c.length > 2 && w.slice(0, 3) === c.slice(0, 3))) best = best || c; }); return best; }
  function checkExpr(tb, target, expr, isRule) {
    expr = String(expr || '');
    if (!expr.trim()) return { check: 'Error', kind: 'syntax', msg: 'The expression is empty.' };
    var open = (expr.match(/\(/g) || []).length, close = (expr.match(/\)/g) || []).length;
    if (open !== close) return { check: 'Error', kind: 'syntax', msg: open > close ? 'A closing bracket “)” is missing at the end.' : 'There is one closing bracket “)” too many.', fix: open > close ? expr + ')' : null };
    var codes = tb.cols.map(function (c) { return c.code; }), unknown = refs(expr).filter(function (r) { return codes.indexOf(r) < 0; });
    if (unknown.length) { var near = nearest(unknown[0], codes); return { check: 'Error', kind: 'name', msg: '“' + unknown[0] + '” is not a column of this table.' + (near ? ' Did you mean ' + near + '?' : ' Use a column code from the list below.'), fix: near ? expr.replace(new RegExp('\\b' + unknown[0] + '\\b', 'g'), near) : null }; }
    if (isRule) return { check: 'Valid', msg: 'The condition compiles.' };
    var tc = colOf(tb, target);
    if (tc && refs(expr).indexOf(target) >= 0) return { check: 'Error', kind: 'cycle', msg: 'Circular reference: ' + target + ' refers to itself. Use PREV(' + target + ') to read the previous period.' };
    if (tc && tc.unit && !/CONVERT\(/i.test(expr) && !/[*\/]/.test(expr)) {
      var bad = refs(expr).map(function (r) { return colOf(tb, r); }).filter(function (c) { return c && c.unit && c.unit !== tc.unit; })[0];
      if (bad) return { check: 'Error', kind: 'units', msg: 'Units do not match: “' + cname(bad) + '” is in ' + bad.unit + ', but “' + cname(tc) + '” expects ' + tc.unit + '. Nothing converts between them.', fix: expr.replace(new RegExp('\\b' + bad.code + '\\b'), 'CONVERT(' + bad.code + ', "' + tc.unit + '")') };
    }
    return { check: 'Valid', msg: 'Compiles. Result unit: ' + ((tc && tc.unit) || 'none') + '.' };
  }
  function recheck(tb) {
    var dep = {}, cyc = {};
    tb.formulas.forEach(function (f) { dep[f.target] = refs(f.expr); });
    function visit(n, path) { var at = path.indexOf(n); if (at >= 0) { var loop = path.slice(at).concat(n); if (loop.length > 2) loop.forEach(function (x) { cyc[x] = cyc[x] || loop; }); return; } if (!dep[n] || path.length > 12) return; dep[n].forEach(function (m) { visit(m, path.concat(n)); }); }
    Object.keys(dep).forEach(function (k) { visit(k, []); });
    tb.formulas.forEach(function (f) {
      var r = checkExpr(tb, f.target, f.expr);
      if (r.check === 'Valid' && cyc[f.target]) r = { check: 'Error', kind: 'cycle', msg: cyc[f.target].join(' → ') + ': each column waits for the other. One of them must be calculated from entered values only.', fix: f.target === 'TOTAL' ? (tb.si === 0 ? 'SUM(SO2:CH4)' : 'SUM(COD:PO4)') : null };
      f.check = r.check; f.kind = r.kind || null; f.msg = r.msg; f.fix = r.fix || null;
    });
  }
  var ISSUE_TITLE = { units: 'Incompatible units without CONVERT', cycle: 'Circular reference in formulas', name: 'Formula refers to a missing column', syntax: 'Formula does not compile' };
  function issuesOf(S) {
    var out = [];
    if (!S.sheets.length) out.push({ id: 'nosheets', kind: 'nosheets', title: 'The version has no sheets', text: 'There is nothing to publish yet. Add a sheet, a table in it, and at least one column.', where: 'Version', node: 'root', tab: null, panel: null, tb: null });
    S.sheets.forEach(function (sh) {
      if (sh.enabled && !sh.tables.length) out.push({ id: 'e-' + sh.id, kind: 'notables', title: 'Sheet “' + sh.name.en + '” has no tables', text: 'Operators would see an empty tab. Add a table, or turn the sheet off.', where: sh.name.en, node: 's:' + sh.id, tab: null, panel: null, tb: null });
      sh.tables.forEach(function (tb) {
      if (!tb.cols) return; var cycleSeen = false;
      if (!tb.cols.length) out.push({ id: 'c-' + tb.id, kind: 'nocols', title: 'Table ' + tb.no + ' has no columns', text: 'A table needs at least one column before it can be published.', where: sh.name.en + ' › ' + tlabel(tb), node: 't:' + tb.id, tab: null, panel: null, tb: tb.id });
      tb.cols.forEach(function (c) { if (!c.name[S.props.lang]) out.push({ id: 'n-' + tb.id + '-' + c.code, kind: 'noname', title: 'Column ' + c.code + ' has no name in English', text: 'English is the default language of this template. Operators who use it would see the bare code instead of a caption.', where: sh.name.en + ' › ' + tlabel(tb) + ' › Columns', node: 't:' + tb.id, tab: null, panel: 'c:' + c.code, tb: tb.id }); });
      tb.formulas.forEach(function (f) { if (f.check !== 'Error') return; if (f.kind === 'cycle') { if (cycleSeen) return; cycleSeen = true; }
        out.push({ id: 'f-' + tb.id + '-' + f.id, kind: f.kind, title: ISSUE_TITLE[f.kind] || 'Formula does not compile', text: f.msg, where: sh.name.en + ' › ' + tlabel(tb) + ' › Formulas › ' + f.target, node: 't:' + tb.id, tab: 'formulas', panel: 'f:' + f.id, tb: tb.id }); });
    }); });
    return out;
  }
  function fixAll(S) {
    var n = 0;
    S.sheets.forEach(function (sh) { sh.tables.forEach(function (tb) { if (!tb.cols) return;
      tb.cols.forEach(function (c) { if (!c.name.en) { c.name.en = c.code === 'ABATEMENT' ? 'NOₓ abatement' : c.code; n++; } if (!c.name.kk && c.code === 'ABATEMENT') c.name.kk = 'NOₓ тазарту дәрежесі'; });
      for (var g = 0; g < 3; g++) tb.formulas.forEach(function (f) { if (f.check === 'Error' && f.fix) { f.expr = f.fix; n++; recheck(tb); } }); }); });
    S.changes.forEach(function (c) { if (c.title === 'Column “ABATEMENT” added') { c.title = 'Column “NOₓ abatement” added'; } if (c.type === 'Formula' && c.kind === 'changed') { c.title = 'Column and formula “Year to date” added'; c.was = null; c.now = 'YTD = PREV(YTD) + TOTAL'; c.kind = 'added'; } });
    return n;
  }
  /* ---------- shared bits ---------- */
  function langFields(prefix, label, obj, o) {
    o = o || {}; var inputs = {}, box = h('div', { class: 'stack gap-2' });
    LANGS.forEach(function (l, i) { var f = E.Input({ id: prefix + '-' + l[0], label: i ? null : null, ariaLabel: label + ' (' + l[1] + ')', bare: true, value: obj[l[0]] || '', disabled: o.disabled, placeholder: i ? 'Falls back to English' : null }); inputs[l[0]] = f.input; box.appendChild(h('div', { class: 'tpl-lang' }, h('span', { class: 'lg' }, l[1]), f)); });
    var wrap = h('div', { class: 'field', role: 'group', 'aria-label': label }, h('span', { class: 'lbl' }, label), box, o.hint ? h('div', { class: 'hint' }, o.hint) : null);
    wrap.read = function () { return { en: inputs.en.value.trim(), ru: inputs.ru.value.trim(), kk: inputs.kk.value.trim() }; }; wrap.inputs = inputs; return wrap;
  }
  function jump(api, step, order, max) { var n = order.indexOf(step); for (var i = 0; i < n && i < (max || 9); i++) api.next(); }
  function checkBadge(f) { return f.check === 'Error' ? E.StatusBadge('severity', 'Error', { label: 'Error', title: f.msg }) : f.check === 'Warning' ? E.StatusBadge('severity', 'Warning', { label: 'Warning', title: f.msg }) : E.StatusBadge('job', 'Done', { label: 'Valid', quiet: true }); }
  function classBadge(cls) { return E.StatusBadge('severity', cls === 'Breaking' ? 'Error' : cls === 'Guarded' ? 'Warning' : 'Info', { label: cls, quiet: cls === 'Safe', title: CLASS_HELP[cls] }); }
  var CLASS_HELP = { Breaking: 'Breaking — existing documents would lose data or stop opening. Blocked while documents exist, unless a migration is planned.', Guarded: 'Guarded — allowed, but existing documents behave differently afterwards. Read the consequences.', Safe: 'Safe — existing documents are not affected.' };
  function notFound(el, ctx, what, back) { var page = h('div', { class: 'page' }); el.appendChild(page); page.appendChild(E.PageHeader({ ctx: ctx, id: 'tpl-nf', title: what + ' not found', back: back })); page.appendChild(E.EmptyState({ icon: 'layout', title: 'This ' + what.toLowerCase() + ' does not exist', text: 'It may have been deleted, or the link is old. Nothing was changed.', actions: [{ label: 'Back to ' + back.label, variant: 'primary', href: back.href }] })); }
  function crumbsFor(ctx, tail) { var t = tpl(ctx.params.id), v = t && ctx.params.versionId && ver(t.id, ctx.params.versionId), c = [{ label: 'Configure' }, { label: 'Templates', href: '#' + BASE }]; if (t) c.push({ label: t.name, href: '#' + BASE + '/' + t.id }); if (v) c.push({ label: vname(v), href: vhref(t.id, v.id) }); if (tail) c.push({ label: tail }); if (!tail && c[c.length - 1].href) delete c[c.length - 1].href; return c; }

  function cloneDialog(ctx, tid, from) {
    var t = tpl(tid), d = draftOf(tid), list = versionsOf(tid).filter(function (v) { return v.state !== 'Draft'; }); from = from || currentOf(tid) || list[0];
    if (d) { E.Dialog({ id: 'tpl-clone', title: 'A draft already exists', body: function (b) {
        b.appendChild(E.Banner({ tone: 'warning', title: vname(d) + ' is being edited by ' + d.author, text: 'Last change ' + F.dateTime(d.edited) + '. A template has one draft at a time, so two people cannot publish competing structures.' }));
        b.appendChild(h('p', 'Continue that draft, or delete it first if you want to start again from ' + (from ? vname(from) : 'a published version') + '.')); },
      footer: [{ label: 'Cancel', variant: 'ghost', align: 'left' }, { label: 'Open draft ' + vname(d), variant: 'primary', icon: 'pencil', id: 'tpl-clone-open', onClick: function () { E.go(vhref(tid, d.id)); } }] }); return; }
    if (!from) { E.Toast('There is no version to copy from', { tone: 'warning' }); return; }
    var src = E.Select({ id: 'tpl-clone-from', label: 'Copy structure from', options: list.map(function (v) { return { value: v.id, label: vname(v) + ' · ' + v.state + ' · ' + F.plural(v.documents, { one: 'document', other: 'documents' }) }; }), value: from.id });
    var num = E.Input({ id: 'tpl-clone-number', label: 'New version number', mono: true, required: true, value: nextVersion(versionsOf(tid)[0].version), hint: 'Three numbers. Raise the first one only for changes that need a migration.' });
    var note = E.Textarea({ id: 'tpl-clone-note', label: 'What is this draft for?', rows: 2, hint: 'Optional. Shown in the version history until you publish.' });
    E.Dialog({ id: 'tpl-clone', title: 'New draft of ' + t.name, body: function (b) { b.appendChild(h('p', 'Everything is copied: sheets, tables, columns, formulas, rules, relations, access and period rules. Documents stay on the versions they use now.')); b.appendChild(h('div', { class: 'form-grid' }, src, num, h('div', { class: 'span-2' }, note))); },
      footer: [{ label: 'Cancel', variant: 'ghost', align: 'left' }, { label: 'Create draft', variant: 'primary', icon: 'copy', id: 'tpl-clone-apply', onClick: function () {
        var n = num.value.trim(); if (!/^\d+\.\d+\.\d+$/.test(n)) { num.setError('Use three numbers separated by dots, for example 1.2.0'); num.input.focus(); return false; }
        if (versionsOf(tid).some(function (v) { return v.version === n; })) { num.setError('Version ' + n + ' already exists in this template'); num.input.focus(); return false; }
        var nv = { id: 'v' + (versionsOf(tid).length + 1), template: tid, version: n, state: 'Draft', created: D.today.slice(0, 10), author: ctx.me.name, edited: D.today.slice(0, 16), documents: 0, note: note.value.trim() || 'Draft from ' + vname(ver(tid, src.value)), base: src.value, presRev: 0, publishedBy: null, publishedAt: null };
        versionsOf(tid).unshift(nv); E.go(vhref(tid, nv.id), { toast: 'Draft ' + vname(nv) + ' created from ' + vname(ver(tid, src.value)) }); } }] });
  }

  /* ================= 1. /admin/templates — перелік ================= */
  E.screen(BASE, { title: 'Templates', group: 'Configure', icon: 'layout', order: 1, render: function (el, ctx) {
    function rowsNow() { return D.templates.map(function (t) { var d = draftOf(t.id), c = currentOf(t.id); return { id: t.id, name: t.name, t: t, state: t.state, current: c, draft: d, documents: t.documents, updated: d && d.edited > t.updated ? d.edited : t.updated }; }); }
    var rows = ctx.state === 'empty' ? [] : rowsNow();
    E.ListPage(el, ctx, { id: 'tpl', rows: rows,
      header: { title: 'Templates', back: false, subtitle: 'A template is the structure of a report: sheets, tables, columns, formulas and rules. Documents are created from a published version and stay on it.',
        primary: { label: 'New template', icon: 'plus', id: 'tpl-new-btn', onClick: function () { ctx.openDialog('new-template'); } } },
      stats: [
        { id: 'all', label: 'templates', value: function (r) { return r.length; } },
        { id: 'published', label: 'published versions', value: function (r) { return r.reduce(function (n, x) { return n + versionsOf(x.id).filter(function (v) { return v.state === 'Published'; }).length; }, 0); }, match: function (r) { return !!r.current; }, hint: 'Show templates that have a published version' },
        { id: 'drafts', label: 'drafts in progress', match: function (r) { return !!r.draft; }, hint: 'Show templates with an open draft' },
        { id: 'docs', label: 'documents using them', value: function (r) { return r.reduce(function (n, x) { return n + x.documents; }, 0); }, match: function (r) { return r.documents > 0; }, hint: 'Show templates that documents depend on' }],
      search: { placeholder: 'Template name or code', text: function (r) { return r.name + ' ' + r.id + ' ' + (r.draft ? r.draft.author : ''); } },
      filters: [{ id: 'state', label: 'States', options: ['Published', 'Draft', 'Archived'] }],
      table: { rowKey: 'id', tall: true, rowLabel: function (r) { return 'Open template ' + r.name; },
        columns: [
          { key: 'name', label: 'Template', render: function (r) { return E.twoLine(r.name, r.id); } },
          { key: 'current', label: 'Current version', sortValue: function (r) { return r.current ? r.current.version : ''; }, render: function (r) { var last = versionsOf(r.id).filter(function (v) { return v.state === 'Archived'; })[0]; return r.current ? h('span', { class: 'mono' }, vname(r.current)) : last ? E.twoLine(vname(last), 'archived — no current version', { mono: true }) : h('span', { class: 'muted' }, 'Not published yet'); } },
          { key: 'draft', label: 'Draft', sortValue: function (r) { return r.draft ? r.draft.version : ''; }, render: function (r) { return r.draft ? E.twoLine(vname(r.draft), r.draft.author + ' is editing', { mono: true }) : null; } },
          { key: 'documents', label: 'Documents', num: true },
          { key: 'updated', label: 'Updated', hideSm: true, render: function (r) { return F.date(r.updated); } },
          { key: 'state', label: 'State', render: function (r) { return E.StatusBadge('version', r.state, { quiet: true }); } }] },
      empty: { icon: 'layout', title: 'No templates yet', text: 'A template describes what a report looks like — its sheets, tables, columns and rules. Create the first one, then publish a version so documents can be created from it.', actions: [{ label: 'New template', variant: 'primary', icon: 'plus', onClick: function () { ctx.openDialog('new-template'); } }] },
      error: { code: 'ECR-TPL-0503' },
      onRow: function (r) { E.go(BASE + '/' + r.id); } });
    ctx.action('New template', 'plus', function () { ctx.openDialog('new-template'); });

    ctx.dialog('new-template', function () {
      var demo = !!ctx.query.step, data = { name: demo ? 'Quarterly air monitoring report' : '', code: demo ? 'AIR-QUARTERLY' : '', desc: demo ? 'Quarterly stack monitoring results for the regional inspectorate.' : '', from: demo ? 'clone' : 'blank', src: 'GEN07131100' }, f = {};
      var api = E.Wizard({ id: 'tpl-new', title: 'New template', data: data, steps: [
        { id: 'name', label: 'Name and code', render: function (b, d) {
            f.name = E.Input({ id: 'tpl-new-name', label: 'Name', required: true, value: d.name, hint: 'What people see in lists, for example “Quarterly water report”.', onInput: function (v) { d.name = v; } });
            f.code = E.Input({ id: 'tpl-new-code', label: 'Code', required: true, mono: true, maxlength: 20, value: d.code, placeholder: 'AIR-QUARTERLY', hint: '3–20 characters: capital Latin letters, digits and hyphens; starts with a letter.', onInput: function (v, inp) { d.code = inp.value = v.toUpperCase(); } });
            f.desc = E.Textarea({ id: 'tpl-new-desc', label: 'Description', rows: 2, value: d.desc, hint: 'Optional. One sentence about who needs this report and how often.', onInput: function (v) { d.desc = v; } });
            b.appendChild(h('div', { class: 'stack gap-3' }, f.name, f.code, E.Collapsible({ id: 'tpl-new-why', title: 'Why is the code so restricted?', content: h('p', 'The code becomes part of document numbers, export file names, API addresses and audit records. Those places accept only Latin capitals, digits and hyphens — and once the first version is published the code can never change, because documents and integrations already refer to it. The name stays free to edit at any time.') }), f.desc)); },
          validate: function (d) { var bad = null; f.name.setError(null); f.code.setError(null);
            if (!d.name.trim()) { f.name.setError('Give the template a name'); bad = bad || f.name; }
            if (!/^[A-Z][A-Z0-9-]{2,19}$/.test(d.code) || /-$/.test(d.code)) { f.code.setError(!d.code ? 'Enter a code' : /^[^A-Z]/.test(d.code) ? 'The code must start with a Latin letter' : d.code.length < 3 ? 'At least 3 characters' : 'Only A–Z, 0–9 and hyphen; it cannot end with a hyphen'); bad = bad || f.code; }
            else if (tpl(d.code)) { f.code.setError('Template ' + d.code + ' already exists — “' + tpl(d.code).name + '”'); bad = bad || f.code; }
            if (bad) { bad.input.focus(); return 'Check the highlighted fields.'; } return null; } },
        { id: 'source', label: 'Starting point', render: function (b, d, wz) {
            var srcSel = E.Select({ id: 'tpl-new-src', label: 'Template to copy', options: D.templates.map(function (t) { var c = currentOf(t.id) || versionsOf(t.id)[0]; return { value: t.id, label: t.name + ' · ' + vname(c) }; }), value: d.src, onChange: function (v) { d.src = v; paint(); } }), info = h('p');
            function opt(val, title, text) { var inp = h('input', { type: 'radio', name: 'tpl-new-from', id: 'tpl-new-from-' + val, value: val, checked: d.from === val }); inp.addEventListener('change', function () { d.from = val; paint(); }); return h('label', { class: 'tpl-opt', for: 'tpl-new-from-' + val }, inp, h('div', h('b', title), h('span', text))); }
            function paint() { srcSel.hidden = info.hidden = d.from !== 'clone'; var s = tpl(d.src); info.textContent = 'Copies ' + s.sheets + ' sheets and ' + s.tables + ' tables with their columns, formulas, rules, relations and access rules. Documents, version history and the code are not copied.'; }
            b.appendChild(h('div', { class: 'stack gap-3' }, opt('blank', 'Start from scratch', 'An empty draft v0.1.0. You add sheets and tables yourself.'), opt('clone', 'Copy an existing template', 'Start from a structure that already works and change what differs.'), srcSel, info)); paint(); } }],
        summary: function (d) { var s = tpl(d.src); return E.KeyValue([{ label: 'Name', value: d.name }, { label: 'Code', value: d.code, mono: true, hint: 'Cannot be changed after the first publication' }, { label: 'Description', value: d.desc }, { label: 'Starting point', value: d.from === 'clone' ? 'Copy of ' + s.name : 'Empty structure', hint: d.from === 'clone' ? s.sheets + ' sheets · ' + s.tables + ' tables' : null }, { label: 'First version', value: 'v0.1.0 · Draft', mono: true, hint: 'Nobody can create documents until you publish it' }], { wide: true }); },
        reviewText: 'The template is created with one draft version. Nothing is visible to data entry until you publish.', applyLabel: 'Create template',
        onApply: function (d, wz) { var s = tpl(d.src), clone = d.from === 'clone';
          D.templates.unshift({ id: d.code, name: d.name.trim(), state: 'Draft', version: '0.1.0', versions: 1, sheets: clone ? s.sheets : 0, tables: clone ? s.tables : 0, documents: 0, updated: D.today.slice(0, 10), owner: ctx.me.name, _blank: !clone });
          DESC[d.code] = d.desc.trim(); if (clone && TPL_SHEETS[d.src]) TPL_SHEETS[d.code] = TPL_SHEETS[d.src];
          wz.close(); E.go(BASE + '/' + d.code + '?dialog=result-created'); } });
      jump(api, ctx.query.step, ['name', 'source', 'review']);
    });
  } });

  /* ================= 2. /admin/templates/:id — шаблон ================= */
  E.screen(BASE + '/:id', { title: 'Template', icon: 'layout', navPath: BASE, example: BASE + '/GEN07131100', crumbs: function (ctx) { return crumbsFor(ctx); }, render: function (el, ctx) {
    var tid = ctx.params.id, t = tpl(tid); if (!t) return notFound(el, ctx, 'Template', { label: 'Templates', href: '#' + BASE });
    ctx.setTitle(t.name);
    var vs = ctx.state === 'empty' ? [] : versionsOf(tid), d = draftOf(tid), cur = currentOf(tid), from = cur || versionsOf(tid).filter(function (v) { return v.state !== 'Draft'; })[0];
    var page = h('div', { class: 'page' }); el.appendChild(page);
    var primary = d ? { label: 'Continue draft ' + vname(d), icon: 'pencil', id: 'tpl-continue', href: vhref(tid, d.id) } : from ? { label: 'New draft from ' + vname(from), icon: 'copy', id: 'tpl-newdraft', onClick: function () { ctx.openDialog('clone-version'); } } : null;
    page.appendChild(E.PageHeader({ ctx: ctx, id: 'tpl', title: t.name, subtitle: DESC[tid] || 'No description yet.', badge: E.StatusBadge('version', t.state), back: { label: 'Templates', href: '#' + BASE },
      meta: h('div', { class: 'row gap-3 wrap muted t-xs' }, h('span', { class: 'chip', title: 'Template code — part of document numbers and API addresses' }, t.id), h('span', 'Owner ' + t.owner), h('span', F.plural(t.sheets, { one: 'sheet', other: 'sheets' }) + ' · ' + F.plural(t.tables, { one: 'table', other: 'tables' }))),
      primary: primary, secondary: vs.length > 1 ? [{ label: 'Compare versions', icon: 'columns', id: 'tpl-compare', href: vhref(tid, vs[0].id, 'compare') }] : [],
      more: [{ label: 'Edit name and description…', icon: 'pencil', onClick: function () { ctx.openDialog('edit-template'); } }, { label: 'Export definition', icon: 'download', hint: 'JSON', onClick: function () { E.tasks.start({ title: 'Export ' + t.id + ' definition', icon: 'download', detail: 'Collecting ' + t.tables + ' tables', duration: 4000, doneDetail: t.id + '.template.json is ready', result: { label: 'Download', onClick: function () { E.Toast('Download started'); } } }); E.Toast('Export continues in My tasks'); } }, { sep: true }, { label: 'Archive template…', icon: 'archive', danger: true, disabled: t.state === 'Archived', onClick: function () { ctx.openDialog('archive-template'); } }] }));

    function timeline(panel) {
      panel.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'text', rows: 6 }, error: { code: 'ECR-TPL-0504', onRetry: function () { ctx.refresh(); } },
        empty: { icon: 'branch', title: 'No versions yet', text: 'A version holds the structure. Create the first draft, build the sheets and tables, then publish it so documents can be created.', actions: [{ label: 'Create first draft', variant: 'primary', icon: 'plus', onClick: function () { E.Toast('Draft v0.1.0 created'); } }] },
        data: function () { var ul = h('ol', { class: 'tpl-tl', id: 'tpl-timeline' });
          vs.forEach(function (v) { var isCur = v === cur, old = v.state === 'Archived', b = baseOf(tid, v);
            var items = [{ label: 'Open', icon: 'external', href: vhref(tid, v.id) }, { label: 'Compare with…', icon: 'columns', href: vhref(tid, v.id, 'compare') }];
            if (v.state !== 'Draft') items.push({ label: 'Clone to new draft…', icon: 'copy', onClick: function () { ctx.openDialog('clone-version', v); } });
            if (v.state === 'Published') items.push({ sep: true }, { label: 'Archive…', icon: 'archive', danger: true, onClick: function () { ctx.openDialog('archive-version', v); } });
            if (v.state === 'Archived') items.push({ sep: true }, { label: 'Restore to Published', icon: 'undo', onClick: function () { v.state = 'Published'; if (t.state === 'Archived') t.state = 'Published'; ctx.refresh(); E.Toast(vname(v) + ' restored'); } });
            if (v.state === 'Draft') items.push({ sep: true }, { label: 'Delete draft…', icon: 'trash', danger: true, href: vhref(tid, v.id, null, { dialog: 'delete-draft' }) });
            ul.appendChild(h('li', { class: old ? 'old' : null },
              h('span', { class: 'pt' }, h('span', { class: 'dot ' + (v.state === 'Draft' ? 'draft' : isCur ? 'filled' : 'empty') })),
              h('div', { class: 'bd' },
                h('div', { class: 'hd' }, h('b', vname(v)), E.StatusBadge('version', v.state, { quiet: true }), isCur ? h('span', { class: 'muted t-xs' }, 'new documents use this version') : null),
                h('div', { class: 'nt' }, v.note),
                h('div', { class: 'muted t-xs' }, v.state === 'Draft' ? 'Being edited by ' + v.author + ' · last change ' + F.dateTime(v.edited) + (b ? ' · based on ' + vname(b) : '') : 'Published ' + F.date(v.publishedAt) + ' by ' + v.publishedBy + ' · ' + F.plural(v.documents, { one: 'document', other: 'documents' }) + (v.presRev ? ' · presentation revision ' + v.presRev : ''))),
              h('div', { class: 'ac' }, E.Button({ label: v.state === 'Draft' ? 'Continue' : 'Open', size: 'sm', id: 'tpl-open-' + v.id, href: vhref(tid, v.id) }), E.Menu({ label: 'More actions for ' + vname(v), iconOnly: true, icon: 'more', id: 'tpl-vm-' + v.id, align: 'right', items: items })))); });
          return ul; } }));
    }
    function usage(panel) {
      var docs = ctx.state === 'empty' ? [] : usageOf(t), facilities = {}; docs.forEach(function (x) { facilities[x.facility] = (facilities[x.facility] || 0) + 1; });
      if (ctx.state === 'data' && docs.length) panel.appendChild(h('p', { class: 'muted' }, F.plural(docs.length, { one: 'document', other: 'documents' }) + ' in ' + F.plural(Object.keys(facilities).length, { one: 'project', other: 'projects' }) + ' use this template. They stay on the version they were created from until someone migrates them.'));
      panel.appendChild(E.DataTable({ id: 'tpl-usage', state: ctx.state, rows: docs, rowKey: 'id', tall: true, memory: ctx.saved, rowLabel: function (x) { return 'Open document ' + x.id; }, onRowClick: function (x) { E.go('/documents/' + x.id); },
        columns: [{ key: 'facility', label: 'Project · document', render: function (x) { return E.twoLine(x.facility, x.id); } }, { key: 'period', label: 'Period', render: function (x) { return F.period(x.period); } }, { key: 'ver', label: 'Version', sortable: false, render: function () { return h('span', { class: 'mono' }, cur ? vname(cur) : '—'); } },
          { key: 'state', label: 'State', sortValue: function (x) { return D.docState(x); }, render: function (x) { return E.StatusBadge('sheet', D.docState(x), { quiet: true }); } }, { key: 'owner', label: 'Owner', hideSm: true }, { key: 'updated', label: 'Updated', hideSm: true, render: function (x) { return F.dateTime(x.updated); } }],
        empty: { icon: 'file', title: 'No documents use this template yet', text: t.state === 'Draft' ? 'Publish a version first — documents can only be created from a published version.' : 'Documents created from this template will be listed here with the version they are on.', actions: t.state === 'Draft' ? [] : [{ label: 'Create a document', href: '#/?dialog=create-document' }] },
        error: { code: 'ECR-TPL-0505' } }));
    }
    var tabs = E.Tabs({ id: 'tpl-tabs', ctx: ctx, fill: true, tabs: [{ id: 'versions', label: 'Versions', count: vs.length, render: timeline }, { id: 'usage', label: 'Usage', count: t.documents, render: usage }] });
    page.appendChild(tabs);

    ctx.dialog('clone-version', function (v) { cloneDialog(ctx, tid, v); });
    ctx.dialog('result-created', function () { page.insertBefore(E.ResultBanner({ id: 'tpl-created', title: 'Template created', text: 'It has one draft version, ' + (d ? vname(d) : 'v0.1.0') + '. Next: open the draft, add sheets and tables, then publish — until then nobody can create documents from it.', actions: [{ label: 'Open the draft', icon: 'pencil', href: d ? vhref(tid, d.id) : '#' + BASE }, { label: 'Back to templates', href: '#' + BASE }] }), tabs); });
    ctx.dialog('archive-version', function (v) { v = v || cur || versionsOf(tid)[0]; var only = versionsOf(tid).filter(function (x) { return x.state === 'Published'; }).length === 1;
      E.ConfirmDialog({ id: 'tpl-archive-v', title: 'Archive ' + vname(v) + ' of “' + t.name + '”?', consequences: [F.plural(v.documents, { one: 'document stays', other: 'documents stay' }) + ' on ' + vname(v) + ' and can still be opened and edited.', only ? 'No published version remains: new documents cannot be created from this template until you publish another one.' : 'New documents can no longer be created on ' + vname(v) + '.', { text: 'You can restore it from the version menu at any time.', note: true }],
        verb: 'Archive version', icon: 'archive', onConfirm: function () { v.state = 'Archived'; ctx.refresh(); E.Toast(vname(v) + ' archived', { action: { label: 'Undo', onClick: function () { v.state = 'Published'; ctx.refresh(); } } }); } }); });
    ctx.dialog('edit-template', function () {
      var n = E.Input({ id: 'tpl-edit-name', label: 'Name', required: true, value: t.name }), c = E.Input({ id: 'tpl-edit-code', label: 'Code', mono: true, value: t.id, disabled: true, hint: cur || t.state !== 'Draft' ? 'Fixed since the first publication: documents, exports and integrations refer to it.' : 'Can be changed until the first publication.' }), ds = E.Textarea({ id: 'tpl-edit-desc', label: 'Description', rows: 3, value: DESC[tid] || '' });
      E.Dialog({ id: 'tpl-edit', title: 'Name and description', body: h('div', { class: 'stack gap-3' }, n, c, ds), footer: [{ label: 'Cancel', variant: 'ghost', align: 'left' }, { label: 'Save', variant: 'primary', id: 'tpl-edit-save', onClick: function () { if (!n.value.trim()) { n.setError('Give the template a name'); n.input.focus(); return false; } t.name = n.value.trim(); DESC[tid] = ds.value.trim(); ctx.refresh(); E.Toast('Template details saved', 'success'); } }] }); });
    ctx.dialog('archive-template', function () { var before = { state: t.state, vs: versionsOf(tid).map(function (v) { return v.state; }) };
      E.ConfirmDialog({ id: 'tpl-archive-t', title: 'Archive template “' + t.name + '”?', consequences: [F.plural(t.documents, { one: 'existing document keeps', other: 'existing documents keep' }) + ' working and stay editable.', 'Nobody can create new documents from this template.', d ? 'Draft ' + vname(d) + ' is kept, but cannot be published while the template is archived.' : null, { text: 'An archived template can be restored from its page.', note: true }].filter(Boolean), typeToConfirm: t.documents ? t.id : null, verb: 'Archive template', icon: 'archive',
        onConfirm: function () { t.state = 'Archived'; versionsOf(tid).forEach(function (v) { if (v.state === 'Published') v.state = 'Archived'; });
          E.go(BASE, { toast: { text: '“' + t.name + '” archived', action: { label: 'Undo', onClick: function () { t.state = before.state; versionsOf(tid).forEach(function (v, i) { v.state = before.vs[i]; }); E.go(BASE + '/' + tid); } } } }); } }); });
    if (d) ctx.action('Continue draft ' + vname(d), 'pencil', function () { E.go(vhref(tid, d.id)); });
  } });

  /* ================= 3. /admin/templates/:id/versions/:versionId — КОНСТРУКТОР ================= */
  var CHECKS = ['Every sheet has at least one table', 'Every table has at least one column', 'Column codes are unique within a table', 'Every column has a name in the default language', 'Every formula compiles', 'Formulas have no circular references', 'Units are compatible or converted', 'Every lookup column points to an active registry', 'Rule conditions compile', 'Rule messages exist in the default language', 'Table relations have no cycles', 'Period rules do not conflict', 'Access matrix covers every sheet', 'Dynamic tables have a row limit'];
  var FAIL_IX = { nosheets: 0, notables: 0, nocols: 1, noname: 3, syntax: 4, name: 4, cycle: 5, units: 6 };
  var FROZEN_TIP = 'Structure is frozen in a published version — clone it to change structure';
  function iconBtn(label, ic, fn, o) { o = o || {}; return E.Button({ label: label, icon: ic, iconOnly: true, id: o.id, disabled: o.disabled, title: o.title || label, onClick: fn }); }
  function hashStr(s) { var n = 7; for (var i = 0; i < s.length; i++) n = (n * 31 + s.charCodeAt(i)) % 100003; return n; }

  function previewGrid(tb, lang) {
    var R = rng(hashStr(tb.id + tb.no)), cols = tb.cols.filter(function (c) { return !c.pres.hidden; });
    var names = tb.rows.mode === 'fixed' && tb.rows.items.length ? tb.rows.items.slice(0, 10) : ['Monitoring point MP-101', 'Monitoring point MP-102', 'Monitoring point MP-103', 'Monitoring point MP-104', 'Monitoring point MP-105', 'Monitoring point MP-106'];
    var thead = h('tr', h('th', { class: 'rh', scope: 'col' }, tb.rows.mode === 'fixed' ? 'Source' : 'Row'));
    cols.forEach(function (c, i) { thead.appendChild(h('th', { scope: 'col', class: c.src === 'calc' ? 'is-calc' : null, style: 'min-width:' + c.pres.width + 'px', title: cname(c, lang) + (c.unit ? ', ' + c.unit : '') + ' — ' + SRC_LABEL[c.src].toLowerCase() }, h('div', { class: 'ch' }, h('span', { class: 'cc' }, 'C' + (i + 1)), h('span', { class: 'cn' }, (c.src === 'calc' ? 'ƒ ' : '') + cname(c, lang), c.src === 'lookup' ? icon('lock', 12) : null)), h('div', { class: 'cu' }, c.unit || ' '))); });
    var body = h('tbody'), r1 = tb.rules.filter(function (r) { return r.scope === '*' && r.level === 'Error'; })[0];
    names.forEach(function (nm, r) {
      var tr = h('tr', h('th', { class: 'rh', scope: 'row', title: nm }, h('span', { class: 'cc' }, String(r + 1)), nm)), sum = 0, vals = {};
      cols.forEach(function (c) { if (c.src === 'input' && c.type !== 'Text') { var v = R() < 0.1 ? null : +(R() * (c.unit === 'h' ? 720 : c.unit === '10³ m³' ? 5000 : c.unit === '%' ? 90 : 400)).toFixed(4); vals[c.code] = v; if (v != null && c.unit === 't') sum += v; } });
      cols.forEach(function (c, ci) {
        var v = c.src === 'input' ? vals[c.code] : c.src === 'lookup' ? Math.ceil(sum * 1.15 / 10) * 10 : c.unit === 'kg' ? sum * 1000 : c.code === 'YTD' ? sum * 8.4 : sum, err = r1 && r === 2 && ci === 2 && c.src === 'input';
        if (err) v = -12.5;
        var txt = c.type === 'Text' ? 'text' : v == null ? '' : F.number(v, c.pres.decimals); if (!c.pres.sep) txt = txt.replace(/\s/g, '');
        tr.appendChild(h('td', { class: 'c' + (c.src === 'calc' ? ' is-calc' : '') + (c.src === 'lookup' ? ' is-locked' : '') + (err ? ' is-error' : ''), style: c.pres.align !== 'right' ? 'text-align:' + c.pres.align : null, title: err ? (r1.msg[lang] || r1.msg.en) : null }, c.pres.emph ? h('b', txt) : h('span', { class: 'v' }, txt))); });
      body.appendChild(tr); });
    return h('div', { class: 'tpl-grid' }, h('div', { class: 'grid-wrap', tabindex: '0', 'aria-label': 'Preview of table ' + tlabel(tb) }, h('table', { class: 'grid' }, h('thead', thead), body)));
  }

  E.screen(VP, { title: 'Template version', icon: 'branch', navPath: BASE, example: X, crumbs: function (ctx) { return crumbsFor(ctx); }, render: function (el, ctx) {
    var tid = ctx.params.id, vid = ctx.params.versionId, t = tpl(tid), v = t && ver(tid, vid);
    if (!v) return notFound(el, ctx, 'Version', t ? { label: t.name, href: '#' + BASE + '/' + tid } : { label: 'Templates', href: '#' + BASE });
    ctx.setTitle(t.name + ' · ' + vname(v));
    var S = struct(tid, vid), draft = v.state === 'Draft', archived = v.state === 'Archived', frozen = !draft, base = baseOf(tid, v);
    if (draft && ctx.query.issues === 'clean' && fixAll(S)) S.saved = snap(S);
    if (draft && ctx.query.changes === 'breaking') { injectBreaking(S); S.saved = snap(S); }
    if (draft && ctx.query.migrate) S.migrate = true;
    function SH() { return ctx.state === 'empty' ? [] : S.sheets; }
    function resolve(n) { if (!n || n === 'root') return { kind: 'root' }; var id = n.slice(2), f; if (n.charAt(0) === 's') { f = findSheet(S, id); return f ? { kind: 'sheet', sh: f } : { kind: 'root' }; } f = findTable(S, id); return f ? { kind: 'table', tb: ensure(f.tb), sh: f.sh } : { kind: 'root' }; }
    var node = ctx.query.node || S.ui.node || (SH().length && SH()[0].tables.length ? 't:' + SH()[0].tables[0].id : 'root');
    (function () { var r = resolve(node); if (r.kind === 'root') node = 'root'; if (r.sh) S.ui.open[r.sh.id] = true; })();

    /* ---- head ---- */
    var saveEl = h('span', { class: 'muted nowrap', id: 'tv-save', role: 'status' });
    var saveBtn = E.Button({ label: 'Save', icon: 'save', size: 'sm', id: 'tv-save-btn', onClick: function () { saveAll(); } });
    var primaryBtn = draft ? E.Button({ label: 'Publish…', icon: 'send', variant: 'primary', id: 'tv-publish', onClick: function () { ctx.openDialog('publish-version'); } })
      : archived ? E.Button({ label: 'Clone to new draft', icon: 'copy', variant: 'primary', id: 'tv-clone', onClick: function () { ctx.openDialog('clone-version'); } })
        : E.Button({ label: 'Save presentation', icon: 'save', variant: 'primary', id: 'tv-save-pres', disabled: true, onClick: function () { saveAll(); } });
    var more = [{ label: 'Table relations', icon: 'link', href: vhref(tid, vid, 'relations') }, { label: 'Access matrix', icon: 'grid', href: vhref(tid, vid, 'access-matrix') }, { label: 'Period access rules', icon: 'calendar', href: vhref(tid, vid, 'period-rules') }, { sep: true }, { label: 'Clone version…', icon: 'copy', onClick: function () { ctx.openDialog('clone-version'); } }];
    if (draft) more.push({ label: 'Delete draft…', icon: 'trash', danger: true, onClick: function () { ctx.openDialog('delete-draft'); } });
    var back = ctx.from || { label: t.name, href: '#' + BASE + '/' + tid };
    el.appendChild(h('div', { class: 'editor-head' },
      h('div', { class: 'eh-title' }, h('a', { class: 'ph-back', href: back.href, 'data-back': '1' }, icon('arrowL', 14), 'Back to ' + back.label), h('h1', { tabindex: '-1', 'data-page-title': '1', id: 'tv-h1' }, t.name), h('span', { class: 'muted mono t-xs' }, t.id + ' · ' + vname(v))),
      h('div', { class: 'eh-ctx' }, E.StatusBadge('version', v.state), E.Stepper({ steps: ['Draft', 'Published', 'Archived'], current: draft ? 0 : archived ? 2 : 1 }), saveEl, draft ? saveBtn : null,
        h('div', { class: 'eh-actions' }, E.Button({ label: 'Preview', icon: 'eye', id: 'tv-preview', onClick: function () { ctx.openDialog('preview-version'); } }), E.Button({ label: 'Compare', icon: 'columns', id: 'tv-compare', href: vhref(tid, vid, 'compare') }), E.Menu({ label: 'More', id: 'tv-more', items: more, align: 'right' }), primaryBtn))));
    if (frozen) el.appendChild(E.Banner({ flush: true, id: 'tv-frozen', icon: archived ? 'archive' : 'lock', title: archived ? 'Archived version — read only' : 'Structure is frozen: presentation can still be edited · Clone to change structure',
      text: archived ? 'Nothing can be changed here. Clone it to a new draft to reuse the structure.' : F.plural(v.documents, { one: 'document uses', other: 'documents use' }) + ' this version, so sheets, tables, columns, formulas and rules are locked. Widths, alignment and number formats stay editable and need no migration.',
      actions: archived ? [] : [{ label: 'Clone to new draft', icon: 'copy', id: 'tv-frozen-clone', onClick: function () { ctx.openDialog('clone-version'); } }] }));

    function paintSave() { saveEl.textContent = S.dirty ? F.plural(S.dirty, { one: 'unsaved change', other: 'unsaved changes' }) : archived ? 'Read only' : 'Saved ' + S.savedAt; saveBtn.hidden = !S.dirty; if (frozen && !archived) primaryBtn.disabled = !S.dirty; }
    function markDirty(n) { S.dirty += n == null ? 1 : n; paintSave(); }
    function saveAll(quiet) { var n = S.dirty; if (!n) return; S.dirty = 0; S.savedAt = F.time(D.today); S.saved = snap(S); v.edited = D.today.slice(0, 16);
      if (frozen) { v.presRev++; paintAll(); presResult(); } else { paintSave(); if (!quiet) E.Toast(F.plural(n, { one: 'change', other: 'changes' }) + ' saved to draft ' + vname(v), 'success'); } }
    function revertAll() { restore(S, S.saved); S.dirty = 0; }
    function presResult() { var old = E.$('#tv-pres-result'); if (old) old.remove(); el.insertBefore(E.ResultBanner({ id: 'tv-pres-result', flush: true, title: 'Presentation revision ' + Math.max(1, v.presRev) + ' saved', text: 'Documents pick up the new look the next time they are opened. Structure, data and the version number did not change, so nothing needs to be migrated or re-approved.', actions: [{ label: 'Preview', icon: 'eye', onClick: function () { ctx.openDialog('preview-version'); } }, { label: 'Open template', href: '#' + BASE + '/' + tid }] }), split); }
    function goTo(i) { E.go(vhref(tid, vid, null, { node: i.node, tab: i.tab, panel: i.panel })); }
    function undoable(text, before, dirtyBefore, thenNode) { E.Toast(text, { duration: 8000, action: { label: 'Undo', onClick: function () { restore(S, before); S.dirty = dirtyBefore; if (thenNode) { node = thenNode; ctx.setQuery({ node: node }); } paintAll(); E.Toast('Restored'); } } }); }

    /* ---- tree ---- */
    var treeEl = h('nav', { class: 'tpl-tree', 'aria-label': 'Sheets and tables' }), issBtn = h('button', { class: 'tpl-iss', type: 'button', id: 'tv-issues-btn', on: { click: function () { ctx.openPanel('issues'); } } });
    var search = E.Input({ id: 'tv-tree-q', bare: true, ariaLabel: 'Find a sheet or table', icon: 'search', placeholder: 'Find a table', value: S.ui.q, onInput: function (q) { S.ui.q = q; paintTree(); } });
    var side = h('aside', { class: 'side tpl-side', 'aria-label': 'Structure', id: 'tv-side' },
      h('div', { class: 'pane-h' }, h('h2', 'Structure'), draft ? E.Menu({ label: 'Add to structure', iconOnly: true, icon: 'plus', id: 'tv-add', items: [{ label: 'Add sheet…', icon: 'layout', onClick: function () { ctx.openDialog('add-sheet'); } }, { label: 'Add table…', icon: 'table', disabled: !SH().length, onClick: function () { ctx.openDialog('add-table'); } }] }) : null),
      h('div', { class: 'tpl-tools' }, search), draft ? issBtn : null, treeEl);
    var main = h('div', { class: 'main tpl-main', id: 'tv-main' }), split = h('div', { class: 'split' }, side, main);
    if (window.innerWidth <= 900) side.hidden = true;
    function select(n) { node = n; S.ui.node = n; var rs = resolve(n); if (rs.tb && rs.tb.short) delete S.ui.gopen[rs.sh.id + ':' + rs.tb.group]; ctx.setQuery({ node: n, tab: null, panel: null }); if (window.innerWidth <= 900) side.hidden = true; paintTree(); paintMain(); }
    function nbtn(id, cls, cur, kids, fn, attrs) { return h('button', Object.assign({ class: 'tpl-n ' + cls, type: 'button', id: id, 'aria-current': cur ? 'true' : null, on: { click: fn } }, attrs || {}), kids); }
    function paintTree() {
      treeEl.textContent = ''; var q = S.ui.q.trim().toLowerCase(), iss = {}, all = !draft ? [] : ctx.state === 'empty' ? [{ tb: null, node: 'root' }] : issuesOf(S), shown = 0; all.forEach(function (i) { iss[i.tb] = (iss[i.tb] || 0) + 1; });
      issBtn.textContent = ''; issBtn.className = 'tpl-iss' + (all.length ? '' : ' ok'); E.append(issBtn, [icon(all.length ? 'alertCircle' : 'checkCircle', 14), all.length ? F.plural(all.length, { one: 'problem blocks', other: 'problems block' }) + ' publishing' : 'Nothing blocks publishing']);
      if (!q) treeEl.appendChild(nbtn('tv-n-root', 'root', node === 'root', [icon('branch', 14), h('span', { class: 'nm' }, 'Version ' + vname(v))], function () { select('root'); }));
      SH().forEach(function (sh) {
        var list = sh.tables.filter(function (tb) { return !q || (tb.no + ' ' + tb.name.en).toLowerCase().indexOf(q) >= 0; }); if (q && !list.length) return; shown++;
        var open = q ? true : !!S.ui.open[sh.id], me = 's:' + sh.id, n = sh.tables.reduce(function (a, tb) { return a + (iss[tb.id] || 0); }, 0) + all.filter(function (i) { return i.node === me; }).length;
        treeEl.appendChild(nbtn('tv-n-' + sh.id, 'sheet' + (sh.enabled ? '' : ' off'), node === me, [icon('chevR', 14), h('span', { class: 'nm' }, sh.name.en), n ? h('span', { class: 'count bad', title: F.plural(n, { one: 'publish problem', other: 'publish problems' }) }, n) : h('span', { class: 'ct' }, sh.tables.length)],
          function () { if (node === me) { S.ui.open[sh.id] = !open; paintTree(); } else { S.ui.open[sh.id] = true; select(me); } }, { 'aria-expanded': String(open), title: sh.enabled ? null : 'Not included in this version' }));
        /* D4: групи таблиць згортаються, як у навігаторі документа — розгорнута лише група з вибраним вузлом; проблеми публікації видно лічильником на заголовку */
        var grp = null, grpOpen = true;
        if (open) list.forEach(function (tb) { var g = tb.short ? tb.group : null;
          if (g !== grp) { grp = g; grpOpen = true;
            if (g) { var gk = sh.id + ':' + g, gid = 'tv-g-' + sh.id + '-' + tb.no.split('.')[0], mine = list.filter(function (x) { return x.short && x.group === g; }), gn = mine.reduce(function (a, x) { return a + (iss[x.id] || 0); }, 0);
              var isOpen = q ? true : S.ui.gopen[gk] != null ? S.ui.gopen[gk] : mine.some(function (x) { return node === 't:' + x.id; }); grpOpen = isOpen;
              treeEl.appendChild(nbtn(gid, 'grp', false, [icon('chevR', 14), h('span', { class: 'nm' }, tb.no.split('.')[0] + ' · ' + g), gn ? h('span', { class: 'count bad', title: F.plural(gn, { one: 'publish problem', other: 'publish problems' }) }, gn) : h('span', { class: 'ct' }, mine.length)],
                function () { S.ui.gopen[gk] = !isOpen; paintTree(); var nb = document.getElementById(gid); if (nb) nb.focus(); }, { 'aria-expanded': String(isOpen), title: g })); } }
          if (!grpOpen) return;
          treeEl.appendChild(nbtn('tv-n-' + tb.id, 'table' + (g ? ' in-g' : ''), node === 't:' + tb.id, [h('span', { class: 'no' }, tb.no), h('span', { class: 'nm' }, tb.short || tb.name.en), iss[tb.id] ? h('span', { class: 'dot error', title: F.plural(iss[tb.id], { one: 'publish problem', other: 'publish problems' }) }) : null], function () { select('t:' + tb.id); }, { title: tlabel(tb) })); }); });
      if (!SH().length) treeEl.appendChild(h('div', { class: 'mini-empty' }, 'No sheets yet.'));
      else if (q && !shown) treeEl.appendChild(h('div', { class: 'mini-empty' }, 'No table matches “' + S.ui.q + '”.'));
    }
    function sideToggle() { return iconBtn('Show or hide the structure tree', 'panelL', function () { side.hidden = !side.hidden; }, { id: 'tv-side-toggle' }); }

    /* ---- centre: version (root) ---- */
    function rootView() {
      var nT = 0; S.sheets.forEach(function (s) { nT += s.tables.length; });
      main.appendChild(h('div', { class: 'tpl-bar' }, sideToggle(), h('h2', 'Version ' + vname(v)), h('span', { class: 'meta' }, F.plural(S.sheets.length, { one: 'sheet', other: 'sheets' }) + ' · ' + F.plural(nT, { one: 'table', other: 'tables' })), h('span', { class: 'sp' })));
      var num = E.Input({ id: 'tv-root-number', label: 'Version number', mono: true, value: v.version, disabled: frozen, hint: frozen ? 'Fixed at publication.' : 'Three numbers. Raise the first only when documents will need a migration.', onChange: function (val) { if (!/^\d+\.\d+\.\d+$/.test(val)) { num.setError('Use three numbers separated by dots, for example 1.1.0'); return; } num.setError(null); v.version = val; markDirty(); } });
      var lang = E.Select({ id: 'tv-root-lang', label: 'Default language', options: [{ value: 'en', label: 'English' }, { value: 'ru', label: 'Russian' }, { value: 'kk', label: 'Kazakh' }], value: S.props.lang, disabled: frozen, hint: 'Every sheet, table and column must have a name in this language. The others fall back to it.', onChange: function (val) { S.props.lang = val; markDirty(); paintTree(); } });
      var note = E.Textarea({ id: 'tv-root-note', label: draft ? 'Working note' : 'Reason for change', rows: 2, value: S.props.note, disabled: frozen, hint: draft ? 'For your colleagues. The official reason is asked when you publish.' : null, onInput: function () { } }); note.input.addEventListener('change', function () { S.props.note = note.value; markDirty(); });
      main.appendChild(h('div', { class: 'tpl-pad form' },
        E.Section({ title: 'Version', content: h('div', { class: 'stack gap-3' },
          E.KeyValue([{ label: 'State', value: E.StatusBadge('version', v.state, { quiet: true }) }, { label: 'Based on', value: base ? h('a', { href: vhref(tid, base.id) }, vname(base)) : 'Nothing — first version', hint: base ? F.plural(base.documents, { one: 'document', other: 'documents' }) + ' on it' : null }, draft ? { label: 'Edited by', value: v.author, hint: 'Last change ' + F.dateTime(v.edited) } : { label: 'Published', value: F.date(v.publishedAt) + ' by ' + v.publishedBy }, { label: 'Documents', value: String(v.documents), hint: draft ? 'Documents can be created only after publication' : null }], { wide: true }),
          h('div', { class: 'form-grid' }, num, lang, h('div', { class: 'span-2' }, note))) }),
        E.Section({ title: 'Presentation revision ' + v.presRev, id: 'tv-presrev', content: h('div', { class: 'stack gap-2' },
          h('p', { class: 'muted' }, v.presRev ? 'The look of this version was changed ' + F.plural(v.presRev, { one: 'time', other: 'times' }) + ' after publication.' : 'The look of this version has not been changed since it was ' + (draft ? 'created' : 'published') + '.'),
          h('p', { class: 'muted' }, 'Presentation is how tables look: column widths, alignment, number formats, emphasis and hidden columns. It can be edited even after publication. Every save raises this counter by one, so open documents know to reload styles. Structure, formulas, rules and entered data are never touched by it — no migration, no re-approval.')) }),
        E.Section({ title: 'Structure', hint: 'Pick a sheet or table on the left to edit it', actions: draft ? [{ label: 'Add sheet', icon: 'plus', size: 'sm', id: 'tv-root-add-sheet', onClick: function () { ctx.openDialog('add-sheet'); } }] : [], content: E.DataTable({ id: 'tv-root-sheets', auto: true, tall: true, rows: S.sheets, rowKey: 'id', onRowClick: function (sh) { S.ui.open[sh.id] = true; select('s:' + sh.id); }, rowLabel: function (sh) { return 'Open sheet ' + sh.name.en; },
          columns: [{ key: 'n', label: 'Sheet', sortable: false, render: function (sh) { return E.twoLine(sh.name.en, sh.code); } }, { key: 'tables', label: 'Tables', num: true, sortable: false, render: function (sh) { return String(sh.tables.length); } }, { key: 'enabled', label: 'Included', sortable: false, render: function (sh) { return sh.enabled ? 'Yes' : h('span', { class: 'muted' }, 'No — hidden from documents'); } }],
          empty: { icon: 'layout', title: 'No sheets yet', text: 'Add the first sheet, then tables inside it.' } }) })));
    }

    /* ---- centre: sheet ---- */
    function sheetView(sh) {
      var ix = S.sheets.indexOf(sh);
      function move(d) { S.sheets.splice(ix, 1); S.sheets.splice(ix + d, 0, sh); markDirty(); paintAll(); }
      main.appendChild(h('div', { class: 'tpl-bar' }, sideToggle(), h('span', { class: 'chip' }, sh.code), h('h2', sh.name.en), h('span', { class: 'meta' }, 'Sheet ' + (ix + 1) + ' of ' + S.sheets.length + ' · ' + F.plural(sh.tables.length, { one: 'table', other: 'tables' })), h('span', { class: 'sp' }),
        iconBtn('Move sheet up', 'chevU', function () { move(-1); }, { id: 'tv-sh-up', disabled: frozen || ix === 0 }), iconBtn('Move sheet down', 'chevD', function () { move(1); }, { id: 'tv-sh-down', disabled: frozen || ix === S.sheets.length - 1 }),
        E.Menu({ label: 'More sheet actions', iconOnly: true, icon: 'more', id: 'tv-sh-more', align: 'right', items: [{ label: 'Add table…', icon: 'plus', disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('add-table'); } }, { sep: true }, { label: 'Delete sheet…', icon: 'trash', danger: true, disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('delete-sheet', sh); } }] })));
      var names = langFields('tv-sh-name', 'Sheet name', sh.name, { disabled: frozen, hint: 'Shown on the sheet tab of every document.' });
      Object.keys(names.inputs).forEach(function (k) { names.inputs[k].addEventListener('change', function () { sh.name = names.read(); markDirty(); paintTree(); }); });
      var code = E.Input({ id: 'tv-sh-code', label: 'Code', mono: true, value: sh.code, disabled: frozen || sh.base, hint: sh.base ? 'Fixed: documents of the previous version refer to it.' : 'Capital letters and digits. Used in formulas across sheets and in exports.', onChange: function (val) { sh.code = val.toUpperCase(); markDirty(); } });
      var inc = E.Switch({ id: 'tv-sh-enabled', label: 'Included in this version', checked: sh.enabled, disabled: frozen, hint: 'Turn off to hide the sheet from new documents without deleting its tables.', onChange: function (on) { sh.enabled = on; markDirty(); paintTree(); } });
      main.appendChild(h('div', { class: 'tpl-pad form' }, h('div', { class: 'form-grid' }, names, h('div', { class: 'stack gap-3' }, code, inc)),
        E.Section({ title: 'Tables', hint: 'In the order operators see them', actions: [{ label: 'Add table', icon: 'plus', size: 'sm', id: 'tv-sh-add-table', disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('add-table'); } }],
          content: E.DataTable({ id: 'tv-sh-tables', auto: true, rows: sh.tables, rowKey: 'id', pageSize: 14, onRowClick: function (tb) { select('t:' + tb.id); }, rowLabel: function (tb) { return 'Open table ' + tlabel(tb); },
            columns: [{ key: 'no', label: 'No.', mono: true, width: 64, sortable: false }, { key: 'name', label: 'Table', sortable: false, render: function (tb) { return tb.name.en; } }, { key: 'c', label: 'Columns', num: true, sortable: false, render: function (tb) { return String(tb.cols ? tb.cols.length : 12); } }, { key: 'f', label: 'Formulas', num: true, sortable: false, hideSm: true, render: function (tb) { return String(tb.formulas ? tb.formulas.length : 1); } }, { key: 'r', label: 'Rules', num: true, sortable: false, hideSm: true, render: function (tb) { return String(tb.rules ? tb.rules.length : 3); } }],
            empty: { icon: 'table', title: 'This sheet has no tables', text: 'A sheet without tables cannot be published. Add a table or turn the sheet off.', actions: frozen ? [] : [{ label: 'Add table', icon: 'plus', onClick: function () { ctx.openDialog('add-table'); } }] } }) })));
    }

    /* ---- centre: table ---- */
    function tableView(tb, sh) {
      var errs = tb.formulas.filter(function (f) { return f.check === 'Error'; }).length, noName = tb.cols.filter(function (c) { return !c.name[S.props.lang]; }).length;
      main.appendChild(h('div', { class: 'tpl-bar' }, sideToggle(), h('span', { class: 'chip' }, tb.no), h('h2', { title: tb.name.en }, tb.name.en), h('span', { class: 'meta' }, sh.name.en), h('span', { class: 'sp' }),
        E.Button({ label: 'Table properties', icon: 'sliders', size: 'sm', variant: 'ghost', id: 'tv-tb-props', onClick: function () { ctx.openPanel('table'); } }),
        E.Menu({ label: 'More table actions', iconOnly: true, icon: 'more', id: 'tv-tb-more', align: 'right', items: [{ label: 'Duplicate table', icon: 'copy', disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { var cp = JSON.parse(JSON.stringify(tb)); cp.id = 't' + sh.si + '-n' + (sh.tables.length + 1); cp.no = tb.no + 'a'; cp.name.en = tb.name.en + ' (copy)'; if (cp.short) cp.short += ' (copy)'; cp.base = false; sh.tables.splice(sh.tables.indexOf(tb) + 1, 0, cp); addChange(S, { kind: 'added', cls: 'Safe', type: 'Table', code: cp.id, sheet: sh.name.en, table: tlabel(cp), title: 'Table “' + tlabel(cp) + '” added', was: null, now: cp.cols.length + ' columns · copy of ' + tb.no, effect: 'New table: existing documents get it empty.' }); markDirty(); select('t:' + cp.id); E.Toast('Table duplicated as ' + cp.no); } }, { label: 'Table relations', icon: 'link', href: vhref(tid, vid, 'relations') }, { sep: true }, { label: 'Delete table…', icon: 'trash', danger: true, disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('delete-table', tb); } }] })));
      var pad = h('div', { class: 'tpl-pad' }); main.appendChild(pad);
      pad.appendChild(E.Tabs({ id: 'tv-tabs', ctx: ctx, fill: true, tabs: [
        { id: 'columns', label: 'Columns', count: noName || tb.cols.length, tone: noName ? 'danger' : null, render: colsTab }, { id: 'rows', label: 'Rows', render: rowsTab },
        { id: 'formulas', label: 'Formulas', count: errs || tb.formulas.length, tone: errs ? 'danger' : null, render: formulasTab }, { id: 'rules', label: 'Validation rules', count: tb.rules.length, render: rulesTab }, { id: 'preview', label: 'Preview', render: previewTab }] }));

      function rowMenu(label, items) { return E.Button({ label: label, icon: 'more', iconOnly: true, onClick: function (e, btn) { E.openMenu(btn, items); } }); }
      function colsTab(panel) {
        panel.appendChild(h('div', { class: 'tpl-tb' }, h('p', archived ? 'Columns in the order operators saw them.' : frozen ? 'Structure is locked. Click a column to change how it looks: width, alignment, number format.' : 'Columns in the order operators see them. Click a column to edit its names, type, unit and look.'), frozen ? null : E.Button({ label: 'Add column', icon: 'plus', id: 'tv-col-add', onClick: function () { ctx.openPanel('c:new'); } })));
        var dt = E.DataTable({ id: 'tv-cols', rows: tb.cols, rowKey: 'code', tall: true, pageSize: 100, onRowClick: function (c) { ctx.openPanel('c:' + c.code); }, rowLabel: function (c) { return 'Edit column ' + cname(c); },
          columns: [
            { key: 'ord', label: 'Order', width: 96, sortable: false, render: function (c) { var i = tb.cols.indexOf(c); function mv(d) { tb.cols.splice(i, 1); tb.cols.splice(i + d, 0, c); if (!S.changes.some(function (x) { return x.code === 'order:' + tb.id; })) addChange(S, { kind: 'changed', cls: 'Safe', type: 'Table', code: 'order:' + tb.id, sheet: sh.name.en, table: tlabel(tb), title: 'Column order changed', was: 'Previous order', now: tb.cols.map(function (x) { return x.code; }).join(', '), effect: 'Only the on-screen and export order changes. Values stay with their columns.' }); markDirty(); dt.setRows(tb.cols); }
              if (frozen) return h('span', { class: 'tpl-ord' }, h('span', { class: 'ix' }, String(i + 1)));
              return h('span', { class: 'tpl-ord' }, h('span', { class: 'ix' }, String(i + 1)), iconBtn('Move ' + cname(c) + ' up', 'chevU', function () { mv(-1); }, { disabled: frozen || i === 0 }), iconBtn('Move ' + cname(c) + ' down', 'chevD', function () { mv(1); }, { disabled: frozen || i === tb.cols.length - 1 })); } },
            { key: 'name', label: 'Column', sortable: false, render: function (c) { return c.name[S.props.lang] ? E.twoLine(cname(c), c.code) : h('span', { class: 'two-line' }, h('span', { class: 'tpl-bad' }, icon('alertCircle', 14), 'No English name'), h('small', c.code)); } },
            { key: 'type', label: 'Type', sortable: false, render: function (c) { return c.type + (c.type === 'Decimal' ? ' · ' + c.scale : ''); } },
            { key: 'unit', label: 'Unit', sortable: false, mono: true, render: function (c) { return c.unit || null; } },
            { key: 'required', label: 'Required', sortable: false, hideSm: true, render: function (c) { return c.required ? 'Required' : null; } },
            { key: 'src', label: 'Filled by', sortable: false, hideSm: true, render: function (c) { return c.src === 'calc' ? 'ƒ Formula' : c.src === 'lookup' ? 'Registry · ' + (c.registry || '—') : 'Operator'; } },
            { key: 'act', label: '', width: 44, sortable: false, render: function (c) { return rowMenu('Actions for ' + cname(c), [{ label: 'Edit…', icon: 'pencil', onClick: function () { ctx.openPanel('c:' + c.code); } }, { sep: true }, { label: 'Delete column…', icon: 'trash', danger: true, disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('delete-column', c); } }]); } }],
          empty: { icon: 'columns', title: 'This table has no columns', text: 'Add the first column: what operators enter, or what is calculated.', actions: frozen ? [] : [{ label: 'Add column', icon: 'plus', onClick: function () { ctx.openPanel('c:new'); } }] } });
        panel.appendChild(dt);
      }
      function rowsTab(panel) {
        panel.textContent = ''; var r = tb.rows, shown = r._shown || 8;
        panel.appendChild(h('div', { class: 'tpl-tb' }, h('p', r.mode === 'fixed' ? 'Fixed rows: every document has exactly these rows, in this order. Operators cannot add or remove them.' : 'Dynamic rows: operators add rows themselves, up to the limit below.'),
          frozen ? null : E.Segmented({ id: 'tv-rows-mode', label: 'Row mode', value: r.mode, options: [{ value: 'fixed', label: 'Fixed rows' }, { value: 'dynamic', label: 'Dynamic rows' }], onChange: function (m) { r.mode = m; if (tb.base && !S.changes.some(function (x) { return x.code === 'rows:' + tb.id; })) addChange(S, { kind: 'changed', cls: 'Guarded', type: 'Table', code: 'rows:' + tb.id, sheet: sh.name.en, table: tlabel(tb), title: 'Row mode changed to ' + m, was: m === 'fixed' ? 'Dynamic rows' : 'Fixed rows', now: m === 'fixed' ? 'Fixed rows' : 'Dynamic rows, up to ' + r.max, effect: 'Existing rows and their values stay. From now on operators ' + (m === 'fixed' ? 'cannot add rows of their own.' : 'can add rows of their own.') }); markDirty(); rowsTab(panel); } })));
        if (r.mode === 'dynamic') {
          var max = E.Input({ id: 'tv-rows-max', label: 'Row limit (MaxDynamicRows)', type: 'number', value: r.max, suffix: 'rows', disabled: frozen, hint: 'Between 1 and 5 000. Protects exports and recalculation from runaway tables; operators see “limit reached” with this number.', onChange: function (val) { var n = +val; if (!(n >= 1 && n <= 5000) || n !== Math.round(n)) { max.setError('Enter a whole number from 1 to 5 000'); return; } max.setError(null); r.max = n; markDirty(); } });
          var key = E.Select({ id: 'tv-rows-key', label: 'What identifies a row', options: ['Row code', 'Emission source', 'Substance', 'Monitoring point'], value: r.key, disabled: frozen, hint: 'Used to match rows on import and in table relations.', onChange: function (val) { r.key = val; markDirty(); } });
          var del = E.Switch({ id: 'tv-rows-delete', label: 'Operators may delete rows they added', checked: r.allowDelete, disabled: frozen, onChange: function (on) { r.allowDelete = on; markDirty(); } });
          panel.appendChild(h('div', { class: 'form-grid' }, max, key, h('div', { class: 'span-2' }, del))); return;
        }
        var src = E.Select({ id: 'tv-rows-source', label: 'Rows come from', disabled: frozen, value: r.source, options: [{ value: '', label: 'A list I maintain here' }].concat(D.registries.slice(0, 5).map(function (g) { return { value: g.code, label: 'Registry · ' + g.name + ' (' + g.entries + ' entries)' }; })), hint: r.source ? 'The list below is a copy taken when the version is published. Later registry changes do not alter published versions.' : null, onChange: function (val) { r.source = val; markDirty(); rowsTab(panel); } });
        panel.appendChild(h('div', { class: 'form-grid' }, src));
        if (!r.items.length) panel.appendChild(h('div', { class: 'mini-empty' }, 'No rows yet. Add the first one below.'));
        else { var box = h('div', { class: 'tpl-rowlist', id: 'tv-rows-list' });
          r.items.slice(0, shown).forEach(function (nm, i) { function mv(d) { r.items.splice(i, 1); r.items.splice(i + d, 0, nm); markDirty(); rowsTab(panel); }
            box.appendChild(h('div', { class: 'rw' }, h('span', { class: 'ix' }, String(i + 1)), h('span', { class: 'nm' }, nm), iconBtn('Move “' + nm + '” up', 'chevU', function () { mv(-1); }, { disabled: frozen || i === 0 }), iconBtn('Move “' + nm + '” down', 'chevD', function () { mv(1); }, { disabled: frozen || i === r.items.length - 1 }), iconBtn('Delete row “' + nm + '”…', 'trash', function () { ctx.openDialog('delete-row', { i: i }); }, { disabled: frozen, title: frozen ? FROZEN_TIP : null }))); });
          panel.appendChild(box); panel.appendChild(E.Pagination({ id: 'tv-rows-more', shown: Math.min(shown, r.items.length), total: r.items.length, step: 12, onMore: function () { r._shown = shown + 12; rowsTab(panel); } })); }
        if (!frozen) { var add = E.Input({ id: 'tv-rows-new', bare: true, ariaLabel: 'Name of the new row', placeholder: 'Name of the new row' }); panel.appendChild(h('div', { class: 'row gap-2' }, h('div', { class: 'grow' }, add), E.Button({ label: 'Add row', icon: 'plus', id: 'tv-rows-add', onClick: function () { var nm = add.input.value.trim(); if (!nm) { add.input.focus(); return; } r.items.push(nm); r._shown = r.items.length; markDirty(); rowsTab(panel); E.Toast('Row “' + nm + '” added at the end'); } }))); }
      }
      function formulasTab(panel) {
        panel.appendChild(h('div', { class: 'tpl-tb' }, h('p', 'One formula per calculated column. Expressions are checked as you save; anything that does not compile blocks publishing, not saving.'), frozen ? null : E.Button({ label: 'Add formula', icon: 'plus', id: 'tv-f-add', onClick: function () { ctx.openPanel('f:new'); } })));
        panel.appendChild(E.DataTable({ id: 'tv-formulas', rows: tb.formulas, rowKey: 'id', tall: true, onRowClick: function (f) { ctx.openPanel('f:' + f.id); }, rowLabel: function (f) { return 'Edit formula for ' + f.target; },
          columns: [{ key: 'target', label: 'Target', sortable: false, render: function (f) { var c = colOf(tb, f.target); return E.twoLine(c ? cname(c) : f.target, f.target + (c && c.unit ? ' · ' + c.unit : '')); } }, { key: 'expr', label: 'Expression', sortable: false, render: function (f) { return E.CodeText(f.expr); } }, { key: 'dialect', label: 'Dialect', sortable: false, hideSm: true }, { key: 'check', label: 'Check', sortable: false, render: checkBadge },
            { key: 'act', label: '', width: 44, sortable: false, render: function (f) { return rowMenu('Actions for formula ' + f.target, [{ label: 'Edit…', icon: 'pencil', onClick: function () { ctx.openPanel('f:' + f.id); } }, { sep: true }, { label: 'Delete formula…', icon: 'trash', danger: true, disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('delete-formula', f); } }]); } }],
          empty: { icon: 'sigma', title: 'No formulas in this table', text: 'Every column is entered by hand. Add a calculated column first, then a formula for it.', actions: frozen ? [] : [{ label: 'Add formula', icon: 'plus', onClick: function () { ctx.openPanel('f:new'); } }] } }));
      }
      function rulesTab(panel) {
        panel.appendChild(h('div', { class: 'tpl-tb' }, h('p', 'Errors stop a sheet from being submitted. Warnings and notes only inform the operator.'), frozen ? null : E.Button({ label: 'Add rule', icon: 'plus', id: 'tv-r-add', onClick: function () { ctx.openPanel('r:new'); } })));
        panel.appendChild(E.DataTable({ id: 'tv-rules', rows: tb.rules, rowKey: 'id', tall: true, onRowClick: function (r) { ctx.openPanel('r:' + r.id); }, rowLabel: function (r) { return 'Edit rule ' + r.name; },
          columns: [{ key: 'level', label: 'Level', width: 112, sortable: false, render: function (r) { return E.StatusBadge('severity', r.level); } }, { key: 'name', label: 'Rule', sortable: false, render: function (r) { var c = r.scope !== '*' && colOf(tb, r.scope); return E.twoLine(r.name, r.scope === '*' ? 'Every entered value' : 'Column ' + (c ? cname(c) : r.scope)); } }, { key: 'cond', label: 'Must be true', sortable: false, render: function (r) { return E.CodeText(r.cond); } }, { key: 'msg', label: 'Message', sortable: false, hideSm: true, render: function (r) { return r.msg.en; } },
            { key: 'act', label: '', width: 44, sortable: false, render: function (r) { return rowMenu('Actions for rule ' + r.name, [{ label: 'Edit…', icon: 'pencil', onClick: function () { ctx.openPanel('r:' + r.id); } }, { sep: true }, { label: 'Delete rule…', icon: 'trash', danger: true, disabled: frozen, title: frozen ? FROZEN_TIP : null, onClick: function () { ctx.openDialog('delete-rule', r); } }]); } }],
          empty: { icon: 'shield', title: 'No validation rules', text: 'Without rules any value is accepted. Most tables need at least “value must be ≥ 0”.', actions: frozen ? [] : [{ label: 'Add rule', icon: 'plus', onClick: function () { ctx.openPanel('r:new'); } }] } }));
      }
      function previewTab(panel) {
        var lang = S.ui.lang || 'en', slot = h('div', { class: 'stack fill' }); function paint() { slot.textContent = ''; slot.appendChild(previewGrid(tb, lang)); }
        panel.appendChild(h('div', { class: 'tpl-tb' }, h('p', 'This is what a data-entry operator sees, with sample values. Grey cells are calculated, hatched cells come from a registry' + (tb.rules.length ? ', and row 3 shows how a broken rule looks.' : '.')), E.Segmented({ id: 'tv-pv-lang', label: 'Interface language', value: lang, options: LANGS.map(function (l) { return { value: l[0], label: l[1] }; }), onChange: function (l) { lang = S.ui.lang = l; paint(); } })));
        panel.appendChild(slot); paint();
      }
    }

    function paintMain() {
      main.textContent = ''; var r = resolve(node);
      if (!SH().length) { main.appendChild(h('div', { class: 'tpl-bar' }, sideToggle(), h('h2', 'Version ' + vname(v)), h('span', { class: 'sp' }))); main.appendChild(h('div', { class: 'state-fill' }, E.EmptyState({ icon: 'layout', title: 'This version has no sheets yet', text: 'A sheet is a tab of the report — for example “Air emissions”. Add a sheet, then tables inside it. You can publish once every sheet has a table and every table has a column.', actions: draft ? [{ label: 'Add sheet', variant: 'primary', icon: 'plus', onClick: function () { ctx.openDialog('add-sheet'); } }] : [] }))); return; }
      if (r.kind === 'table') tableView(r.tb, r.sh); else if (r.kind === 'sheet') sheetView(r.sh); else rootView();
    }
    function paintAll() { paintSave(); paintTree(); paintMain(); }

    if (ctx.state === 'loading' || ctx.state === 'error') el.appendChild(h('div', { class: 'page' }, E.StateSwitch(ctx.state, { loading: { kind: 'table', rows: 12, cols: 5 }, error: { title: 'The version could not be loaded', text: 'Nothing was lost: drafts are saved on the server after every Save.', code: 'ECR-TPL-0510', onRetry: function () { ctx.refresh(); }, back: { label: t.name, href: '#' + BASE + '/' + tid } } })));
    else el.appendChild(split);
    paintAll();
    ctx.hasUnsaved(function () { return S.dirty > 0; }, { count: function () { return S.dirty; }, text: frozen ? 'Presentation changes of ' + vname(v) + ' are not saved yet.' : 'Changes to draft ' + vname(v) + ' are not saved yet.', onSave: function () { saveAll(true); }, onDiscard: revertAll });
    ctx.proto({ id: 'tv-demo', label: 'Constructor demo', buttons: [{ label: 'Reset this version', onClick: function () { delete ST[S.key]; E.go(vhref(tid, vid), { force: true }); } }] });
    ctx.action('Preview as operator', 'eye', function () { ctx.openDialog('preview-version'); });
    if (draft) ctx.action('Publish ' + vname(v) + '…', 'send', function () { ctx.openDialog('publish-version'); });

    /* ---- drawers: ?panel=c:<code>|f:<id>|r:<id>|table|issues ---- */
    function curTable() { var r = resolve(node); return r.kind === 'table' ? r : null; }
    function frozenNote(b) { if (frozen) b.appendChild(E.Banner({ icon: archived ? 'archive' : 'lock', text: archived ? 'Archived version: nothing can be edited.' : 'Structure is locked because documents use this version. Only the Presentation section below can be edited. To change anything else, clone the version.' })); }
    function once(code, mk) { if (!S.changes.some(function (x) { return x.code === code; })) { var c = mk(); c.code = code; addChange(S, c); } }
    ctx.panel('*', function (id) {
      if (id === 'issues') return issuesPanel();
      var r = curTable(); if (!r) return; var kind = id.charAt(0), key = id.slice(2);
      if (id === 'table') tablePanel(r.tb, r.sh); else if (kind === 'c') colPanel(r.tb, r.sh, key); else if (kind === 'f') formulaPanel(r.tb, r.sh, key); else if (kind === 'r') rulePanel(r.tb, r.sh, key);
    });

    function colPanel(tb, sh, code) {
      var isNew = code === 'new', c = isNew ? mkCol(['', '', '', '', 't'], false) : colOf(tb, code); if (!c) { E.Toast('Column ' + code + ' is not in this table', { tone: 'warning' }); return; }
      var sd = frozen, p = c.pres, was = JSON.stringify(c);
      var names = langFields('tv-col-name', 'Column name', c.name, { disabled: sd, hint: 'English is the default language of this template: it has to be filled before publishing.' });
      var codeF = E.Input({ id: 'tv-col-code', label: 'Code', mono: true, required: true, value: c.code, disabled: sd || c.base, hint: c.base ? 'Fixed: formulas, imports and documents of the previous version refer to it.' : 'Capital letters, digits and underscore. Formulas refer to the column by this code.', onInput: function (val, inp) { inp.value = val.toUpperCase(); } });
      var type = E.Select({ id: 'tv-col-type', label: 'Data type', options: TYPES, value: c.type, disabled: sd, onChange: sync });
      var scale = E.Input({ id: 'tv-col-scale', label: 'Scale', type: 'number', value: c.scale, disabled: sd, hint: 'Digits stored after the decimal point, 0–7.' });
      var unit = E.Select({ id: 'tv-col-unit', label: 'Unit', options: [{ value: '', label: 'No unit' }].concat(D.units.map(function (u) { return { value: u.symbol, label: u.symbol + ' — ' + u.name }; })), value: c.unit, disabled: sd });
      var src = E.Select({ id: 'tv-col-src', label: 'Filled by', options: [{ value: 'input', label: 'Operator enters the value' }, { value: 'calc', label: 'A formula calculates it' }, { value: 'lookup', label: 'Read from a registry' }], value: c.src, disabled: sd, onChange: sync });
      var reg = E.Select({ id: 'tv-col-registry', label: 'Registry', options: D.registries.map(function (g) { return { value: g.code, label: g.name }; }), value: c.registry || 'PermitLimits', disabled: sd, hint: 'Where the value, or the list to pick from, comes from.' });
      var def = E.Input({ id: 'tv-col-default', label: 'Default value', value: c.def, disabled: sd, hint: 'Pre-filled in new documents. Leave empty for none.' });
      var req = E.Switch({ id: 'tv-col-required', label: 'Required', checked: c.required, disabled: sd, hint: 'An empty cell becomes an error when the sheet is submitted.' });
      var width = E.Input({ id: 'tv-col-width', label: 'Width', type: 'number', value: p.width, suffix: 'px', disabled: archived }), align = E.Select({ id: 'tv-col-align', label: 'Alignment', options: [{ value: 'right', label: 'Right (numbers)' }, { value: 'left', label: 'Left' }, { value: 'center', label: 'Centre' }], value: p.align, disabled: archived });
      var decimals = E.Input({ id: 'tv-col-decimals', label: 'Decimals shown', type: 'number', value: p.decimals, disabled: archived, hint: 'Display only. Stored precision is the scale above.' });
      var sep = E.Switch({ id: 'tv-col-sep', label: 'Group thousands', checked: p.sep, disabled: archived }), emph = E.Switch({ id: 'tv-col-emph', label: 'Emphasise as a total', checked: p.emph, disabled: archived }), hid = E.Switch({ id: 'tv-col-hidden', label: 'Hide on screen', checked: p.hidden, disabled: archived, hint: 'Still calculated and exported.' });
      function sync() { reg.hidden = !(type.value === 'Lookup' || src.value === 'lookup'); scale.hidden = type.value !== 'Decimal'; def.hidden = src.value !== 'input'; req.hidden = src.value === 'calc'; }
      function save() {
        if (!sd) {
          var code2 = codeF.value.trim().toUpperCase(), sc = +scale.value;
          if (!/^[A-Z][A-Z0-9_]{0,29}$/.test(code2)) { codeF.setError(code2 ? 'Start with a letter; only A–Z, 0–9 and underscore' : 'Enter a code — formulas refer to the column by it'); codeF.input.focus(); return false; }
          if (tb.cols.some(function (x) { return x !== c && x.code === code2; })) { codeF.setError('This table already has a column ' + code2); codeF.input.focus(); return false; }
          if (type.value === 'Decimal' && !(sc >= 0 && sc <= 7)) { scale.setError('Scale is a whole number from 0 to 7'); scale.input.focus(); return false; }
          var old = { type: c.type, unit: c.unit, required: c.required, name: c.name.en };
          c.name = names.read(); c.code = code2; c.type = type.value; c.scale = sc || 0; c.unit = unit.value; c.src = src.value; c.registry = reg.hidden ? '' : reg.value; c.def = def.value; c.required = req.hidden ? false : req.input.checked;
          if (c.base && !isNew) { var T = tlabel(tb), SN = sh.name.en;
            if (old.type !== c.type) breaking(S, 'Column', SN, T, 'Data type of “' + cname(c) + '” changed', old.type, { kind: 'prop', tb: tb.id, code: c.code, prop: 'type', value: old.type }).now = c.type;
            if (old.unit !== c.unit) once('unit:' + tb.id + ':' + c.code, function () { return { kind: 'changed', cls: 'Guarded', type: 'Column', sheet: SN, table: T, title: 'Unit of “' + cname(c) + '” changed', was: old.unit || 'No unit', now: c.unit || 'No unit', effect: 'Stored values are not converted: a number entered as ' + (old.unit || 'a plain number') + ' will be read as ' + (c.unit || 'a plain number') + '. Check formulas that use this column.' }; });
            if (!old.required && c.required) once('req:' + tb.id + ':' + c.code, function () { return { kind: 'changed', cls: 'Guarded', type: 'Column', sheet: SN, table: T, title: 'Column “' + cname(c) + '” became required', was: 'Required: No', now: 'Required: Yes', effect: 'Rows where it is empty get a “required” error on the next validation. Approved sheets are not reopened.' }; });
            if (old.name !== c.name.en) once('cap:' + tb.id + ':' + c.code, function () { return { kind: 'changed', cls: 'Safe', type: 'Column', sheet: SN, table: T, title: 'Caption of “' + c.code + '” changed', was: old.name, now: c.name.en, effect: 'Only the header text changes.' }; }); }
        }
        var w = +width.value, dc = +decimals.value;
        if (!(w >= 60 && w <= 400)) { width.setError('Between 60 and 400 px'); width.input.focus(); return false; }
        if (!(dc >= 0 && dc <= 7)) { decimals.setError('From 0 to 7'); decimals.input.focus(); return false; }
        c.pres = { width: w, align: align.value, decimals: dc, sep: sep.input.checked, emph: emph.input.checked, hidden: hid.input.checked };
        if (isNew) { tb.cols.push(c); addChange(S, { kind: 'added', cls: 'Safe', type: 'Column', code: c.code, sheet: sh.name.en, table: tlabel(tb), title: 'Column “' + cname(c) + '” added', was: null, now: c.code + ' · ' + c.type + ' · ' + (c.unit || 'no unit') + ' · ' + SRC_LABEL[c.src].toLowerCase(), effect: c.required ? 'Required input: existing drafts get a “required” error until it is filled.' : 'Existing documents simply show a new empty column.' }); }
        if (isNew || JSON.stringify(c) !== was) { recheck(tb); markDirty(); paintAll();
          if (isNew && c.src === 'calc') E.Toast('Column added. It needs a formula to be calculated.', { action: { label: 'Add formula', onClick: function () { ctx.setQuery({ tab: 'formulas' }); paintMain(); ctx.openPanel('f:new'); } } });
          else E.Toast(isNew ? 'Column “' + cname(c) + '” added' : frozen ? 'Presentation of “' + cname(c) + '” changed — save to apply' : 'Column “' + cname(c) + '” updated'); }
      }
      E.Drawer({ id: 'tv-col', title: isNew ? 'New column' : cname(c), subtitle: tlabel(tb) + (isNew ? '' : ' · ' + c.code), body: function (b) {
          frozenNote(b);
          if (!isNew && !c.name[S.props.lang] && draft) b.appendChild(E.Banner({ tone: 'danger', title: 'No English name', text: 'This blocks publishing. Fill the EN field below — the other languages can stay empty and fall back to it.' }));
          var structure = h('div', { class: 'stack gap-3' }, names, h('div', { class: 'form-grid' }, codeF, src, type, scale, unit, reg, def, h('div', { class: 'span-2' }, req)));
          var pres = E.Collapsible({ id: 'tv-col-pres', title: 'Presentation', summary: frozen ? 'Editable after publication' : 'Width, alignment, number format', open: frozen, content: h('div', { class: 'form-grid' }, width, align, decimals, h('div', { class: 'stack' }, sep, emph, hid)) });
          if (frozen) E.append(b, [pres, E.Collapsible({ id: 'tv-col-struct', title: 'Structure', summary: 'Read-only · ' + c.type + (c.unit ? ' · ' + c.unit : '') + ' · ' + SRC_LABEL[c.src].toLowerCase(), content: structure })]); else E.append(b, [structure, pres]); sync(); },
        footer: archived ? [{ label: 'Close' }] : [isNew || frozen ? null : { label: 'Delete…', icon: 'trash', variant: 'ghost', align: 'left', id: 'tv-col-delete', onClick: function () { setTimeout(function () { ctx.openDialog('delete-column', c); }, 0); } }, { label: 'Cancel', variant: 'ghost' }, { label: isNew ? 'Add column' : frozen ? 'Apply presentation' : 'Apply', variant: 'primary', id: 'tv-col-save', onClick: save }].filter(Boolean) });
    }

    function formulaPanel(tb, sh, id) {
      var isNew = id === 'new', calc = tb.cols.filter(function (c) { return c.src === 'calc'; }), free = calc.filter(function (c) { return !tb.formulas.some(function (f) { return f.target === c.code; }); });
      var f = isNew ? { id: 'F' + (++tb.seq), target: (free[0] || calc[0] || {}).code || '', expr: '', dialect: 'ECR expression', base: false } : tb.formulas.filter(function (x) { return x.id === id; })[0]; if (!f) { E.Toast('Formula ' + id + ' is not in this table', { tone: 'warning' }); return; }
      var target = E.Select({ id: 'tv-f-target', label: 'Target column', options: calc.map(function (c) { return { value: c.code, label: cname(c) + ' · ' + c.code + (c.unit ? ' · ' + c.unit : '') }; }), value: f.target, disabled: frozen, hint: 'Only columns “filled by a formula” can be a target.', onChange: function () { run(); } });
      var dialect = E.Select({ id: 'tv-f-dialect', label: 'Dialect', options: ['ECR expression', 'Excel-style (imported)'], value: f.dialect, disabled: frozen, hint: 'Excel-style formulas come from imported workbooks and are translated on save.' });
      var expr = E.Textarea({ id: 'tv-f-expr', label: 'Expression', mono: true, rows: 4, value: f.expr, disabled: frozen, hint: 'Refer to columns by code. Ranges: SO2:CH4. Previous period: PREV(CODE).', onInput: function () { result.textContent = ''; result.appendChild(h('span', { class: 'muted' }, 'Changed — press Check, or Apply to check and keep it.')); } });
      var result = h('div', { id: 'tv-f-result', role: 'status', class: 'stack gap-2' });
      function simulate() { var tmp = { si: tb.si, cols: tb.cols, formulas: tb.formulas.filter(function (x) { return x !== f; }).map(function (x) { return { id: x.id, target: x.target, expr: x.expr }; }).concat([{ id: '__', target: target.value, expr: expr.value }]) }; recheck(tmp); return tmp.formulas[tmp.formulas.length - 1]; }
      function run() { var r = simulate(); result.textContent = '';
        result.appendChild(r.check === 'Error' ? E.Banner({ tone: 'danger', id: 'tv-f-error', title: ISSUE_TITLE[r.kind] || 'Does not compile', text: r.msg, actions: r.fix && !frozen ? [{ label: 'Apply suggestion', id: 'tv-f-fix', onClick: function () { expr.value = r.fix; run(); expr.input.focus(); } }] : [] }) : E.Banner({ icon: 'checkCircle', id: 'tv-f-ok', title: 'Valid', text: r.msg }));
        if (r.fix && r.check === 'Error') result.appendChild(h('div', { class: 'muted t-xs' }, 'Suggestion: ', E.CodeText(r.fix))); return r; }
      function insert(txt) { var ta = expr.input, a = ta.selectionStart, b2 = ta.selectionEnd; ta.value = ta.value.slice(0, a) + txt + ta.value.slice(b2); ta.focus(); ta.selectionStart = ta.selectionEnd = a + txt.length; }
      E.Drawer({ id: 'tv-formula', wide: true, title: isNew ? 'New formula' : 'Formula for “' + (colOf(tb, f.target) ? cname(colOf(tb, f.target)) : f.target) + '”', subtitle: tlabel(tb) + (isNew ? '' : ' · ' + f.target), body: function (b) {
          frozenNote(b);
          if (!calc.length) b.appendChild(E.Banner({ tone: 'warning', title: 'This table has no calculated columns', text: 'Add a column “filled by a formula” on the Columns tab first.' }));
          b.appendChild(h('div', { class: 'form-grid' }, target, dialect)); b.appendChild(expr);
          if (!frozen) { var ins = h('div', { class: 'tpl-insert', role: 'group', 'aria-label': 'Insert a column code or function' }); tb.cols.forEach(function (c) { if (c.code !== target.value) ins.appendChild(h('button', { type: 'button', title: 'Insert ' + cname(c) + (c.unit ? ', ' + c.unit : ''), on: { click: function () { insert(c.code); } } }, c.code)); }); ['SUM()', 'CONVERT(, "")', 'PREV()', 'IF(, , )', 'ROUND(, 4)'].forEach(function (fn) { ins.appendChild(h('button', { type: 'button', title: 'Insert function', on: { click: function () { insert(fn); } } }, 'ƒ ' + fn)); });
            b.appendChild(E.Collapsible({ id: 'tv-f-insert', title: 'Insert column or function', summary: tb.cols.length + ' columns', content: ins })); }
          b.appendChild(h('div', { class: 'row gap-2' }, E.Button({ label: 'Check', icon: 'checkCircle', id: 'tv-f-check', onClick: run }), h('span', { class: 'muted t-xs' }, 'Checks syntax, column names, units and circular references.'))); b.appendChild(result); if (f.expr) run(); },
        footer: frozen ? [{ label: 'Close' }] : [isNew ? null : { label: 'Delete…', icon: 'trash', variant: 'ghost', align: 'left', id: 'tv-f-delete', onClick: function () { setTimeout(function () { ctx.openDialog('delete-formula', f); }, 0); } }, { label: 'Cancel', variant: 'ghost' }, { label: isNew ? 'Add formula' : 'Apply', variant: 'primary', id: 'tv-f-save', onClick: function () {
          if (!target.value) { target.setError('Pick the column this formula fills'); return false; }
          if (tb.formulas.some(function (x) { return x !== f && x.target === target.value; })) { target.setError('Column ' + target.value + ' already has a formula — edit that one instead'); target.input.focus(); return false; }
          var old = f.expr, changed = isNew || old !== expr.value || f.target !== target.value || f.dialect !== dialect.value; f.target = target.value; f.expr = expr.value.trim(); f.dialect = dialect.value;
          if (isNew) { tb.formulas.push(f); addChange(S, { kind: 'added', cls: 'Safe', type: 'Formula', code: 'f:' + tb.id + ':' + f.id, sheet: sh.name.en, table: tlabel(tb), title: 'Formula for “' + f.target + '” added', was: null, now: f.target + ' = ' + f.expr, effect: 'Calculated on the next recalculation of each document.' }); }
          else if (f.base && old !== f.expr) once('fx:' + tb.id + ':' + f.id, function () { return { kind: 'changed', cls: 'Guarded', type: 'Formula', sheet: sh.name.en, table: tlabel(tb), title: 'Formula for “' + f.target + '” changed', was: f.target + ' = ' + old, now: f.target + ' = ' + f.expr, effect: 'Calculated values change in every document on the next recalculation, including approved sheets — they are flagged for the approver, not reopened.' }; });
          recheck(tb); if (changed) markDirty(); paintAll();
          if (f.check === 'Error') E.Toast('Kept with an error — it blocks publishing until fixed', { tone: 'warning' }); else E.Toast('Formula for ' + f.target + (isNew ? ' added' : ' updated')); } }].filter(Boolean) });
    }

    function rulePanel(tb, sh, id) {
      var isNew = id === 'new', r = isNew ? { id: 'R' + (++tb.seq), level: 'Warning', name: '', scope: '*', cond: '', msg: { en: '', ru: '', kk: '' }, base: false } : tb.rules.filter(function (x) { return x.id === id; })[0]; if (!r) { E.Toast('Rule ' + id + ' is not in this table', { tone: 'warning' }); return; }
      var name = E.Input({ id: 'tv-r-name', label: 'Rule name', required: true, value: r.name, disabled: frozen, hint: 'For administrators. Operators see the message below.' });
      var level = E.Select({ id: 'tv-r-level', label: 'Level', options: [{ value: 'Error', label: 'Error — blocks submission' }, { value: 'Warning', label: 'Warning — operator confirms' }, { value: 'Info', label: 'Info — note only' }], value: r.level, disabled: frozen });
      var scope = E.Select({ id: 'tv-r-scope', label: 'Applies to', options: [{ value: '*', label: 'Every entered value' }].concat(tb.cols.map(function (c) { return { value: c.code, label: 'Column ' + cname(c) }; })), value: r.scope, disabled: frozen });
      var cond = E.Textarea({ id: 'tv-r-cond', label: 'Must be true', mono: true, rows: 2, value: r.cond, disabled: frozen, hint: 'The rule fires when this is false. “Value” means the cell being checked.' });
      var msg = langFields('tv-r-msg', 'Message shown to the operator', r.msg, { disabled: frozen, hint: 'Say what is wrong and what to do. English is required.' }), result = h('div', { role: 'status' });
      function run() { var c = checkExpr(tb, null, cond.value, true); result.textContent = ''; result.appendChild(c.check === 'Error' ? E.Banner({ tone: 'danger', title: 'Condition does not compile', text: c.msg, actions: c.fix ? [{ label: 'Apply suggestion', onClick: function () { cond.value = c.fix; run(); } }] : [] }) : E.Banner({ icon: 'checkCircle', title: 'Valid', text: c.msg })); return c; }
      E.Drawer({ id: 'tv-rule', title: isNew ? 'New validation rule' : r.name, subtitle: tlabel(tb), badge: isNew ? null : h('div', E.StatusBadge('severity', r.level)), body: function (b) { frozenNote(b); b.appendChild(name); b.appendChild(h('div', { class: 'form-grid' }, level, scope)); b.appendChild(cond); b.appendChild(h('div', { class: 'row gap-2' }, E.Button({ label: 'Check', icon: 'checkCircle', id: 'tv-r-check', onClick: run }))); b.appendChild(result); b.appendChild(msg); if (r.cond) run(); },
        footer: frozen ? [{ label: 'Close' }] : [isNew ? null : { label: 'Delete…', icon: 'trash', variant: 'ghost', align: 'left', id: 'tv-r-delete', onClick: function () { setTimeout(function () { ctx.openDialog('delete-rule', r); }, 0); } }, { label: 'Cancel', variant: 'ghost' }, { label: isNew ? 'Add rule' : 'Apply', variant: 'primary', id: 'tv-r-save', onClick: function () {
          var m = msg.read(); if (!name.value.trim()) { name.setError('Name the rule'); name.input.focus(); return false; }
          if (run().check === 'Error') { cond.setError('Fix the condition first'); cond.input.focus(); return false; } cond.setError(null);
          if (!m.en) { msg.inputs.en.focus(); E.Toast('Write the English message — operators see it when the rule fires', { tone: 'warning' }); return false; }
          var oldLevel = r.level, was = JSON.stringify(r); r.name = name.value.trim(); r.level = level.value; r.scope = scope.value; r.cond = cond.value.trim(); r.msg = m;
          if (isNew) { tb.rules.push(r); addChange(S, { kind: 'added', cls: r.level === 'Error' ? 'Guarded' : 'Safe', type: 'Rule', code: 'r:' + tb.id + ':' + r.id, sheet: sh.name.en, table: tlabel(tb), title: 'Rule “' + r.name + '” added', was: null, now: r.level + ' · ' + r.cond, effect: r.level === 'Error' ? 'Draft and returned sheets that break it cannot be submitted until corrected. Approved sheets stay approved.' : 'Operators see a new ' + r.level.toLowerCase() + '; nothing is blocked.' }); }
          else if (r.base && oldLevel !== r.level) once('lvl:' + tb.id + ':' + r.id, function () { var up = r.level === 'Error'; return { kind: 'changed', cls: up ? 'Guarded' : 'Safe', type: 'Rule', sheet: sh.name.en, table: tlabel(tb), title: 'Rule “' + r.name + '” is now ' + (up ? 'an error' : 'a ' + r.level.toLowerCase()), was: 'Level: ' + oldLevel, now: 'Level: ' + r.level, effect: up ? 'Approved sheets stay approved. Draft and returned sheets that break the rule cannot be submitted until corrected.' : 'Less strict: nothing that passed before fails now.' }; });
          if (isNew || JSON.stringify(r) !== was) { markDirty(); paintAll(); E.Toast('Rule “' + r.name + '” ' + (isNew ? 'added' : 'updated')); } } }].filter(Boolean) });
    }

    function tablePanel(tb, sh) {
      tb.pres = tb.pres || { freeze: true, totals: false };
      var names = langFields('tv-tb-name', 'Table name', tb.name, { disabled: frozen }), no = E.Input({ id: 'tv-tb-no', label: 'Number', mono: true, value: tb.no, disabled: frozen, hint: 'Shown before the name and used to sort tables in the sheet.' });
      var fr = E.Switch({ id: 'tv-tb-freeze', label: 'Keep the first column in view when scrolling', checked: tb.pres.freeze, disabled: archived }), tot = E.Switch({ id: 'tv-tb-totals', label: 'Show a totals row at the bottom', checked: tb.pres.totals, disabled: archived });
      E.Drawer({ id: 'tv-table', title: 'Table properties', subtitle: tlabel(tb), body: function (b) { frozenNote(b); b.appendChild(names); b.appendChild(no);
          b.appendChild(E.KeyValue([{ label: 'Sheet', value: sh.name.en }, { label: 'Columns', value: String(tb.cols.length) }, { label: 'Rows', value: tb.rows.mode === 'fixed' ? tb.rows.items.length + ' fixed' : 'Dynamic, up to ' + tb.rows.max }, { label: 'Formulas · rules', value: tb.formulas.length + ' · ' + tb.rules.length }]));
          b.appendChild(E.Collapsible({ id: 'tv-tb-pres', title: 'Presentation', summary: 'Editable after publication', open: frozen, content: h('div', { class: 'stack' }, fr, tot) })); },
        footer: archived ? [{ label: 'Close' }] : [{ label: 'Cancel', variant: 'ghost' }, { label: 'Apply', variant: 'primary', id: 'tv-tb-save', onClick: function () { var was = JSON.stringify([tb.name, tb.no, tb.pres]);
          if (!frozen) { var n = names.read(); if (!n.en) { names.inputs.en.focus(); E.Toast('The English name is required', { tone: 'warning' }); return false; } if (n.en !== tb.name.en) tb.short = null; tb.name = n; tb.no = no.value.trim() || tb.no; }
          tb.pres = { freeze: fr.input.checked, totals: tot.input.checked }; if (JSON.stringify([tb.name, tb.no, tb.pres]) !== was) { markDirty(); paintAll(); E.Toast('Table properties changed'); } } }] });
    }

    function issuesPanel() {
      var list = issuesOf(S);
      E.Drawer({ id: 'tv-issues', title: 'Publish problems', subtitle: list.length ? 'Each one opens the place where it can be fixed' : vname(v), body: function (b, ctl) {
          if (!list.length) { b.appendChild(h('div', { class: 'mini-empty' }, icon('checkCircle', 20), h('b', 'Nothing blocks publishing'), h('span', 'All ' + CHECKS.length + ' checks pass for the tables you have touched. The full check runs when you press Publish.'))); return; }
          var ul = h('ul', { class: 'tpl-chk' }); list.forEach(function (i) { ul.appendChild(h('li', { class: 'bad' }, icon('alertCircle', 14), h('div', { class: 'tx' }, h('b', i.title), h('small', i.text), h('small', { class: 'mono' }, i.where)), E.Button({ label: 'Go to', size: 'sm', id: 'tv-iss-' + i.id, onClick: function () { ctl.close(); goTo(i); } }))); }); b.appendChild(ul); },
        footer: [{ label: 'Close' }] });
    }

    /* ---- dialogs ---- */
    ctx.dialog('clone-version', function () { if (S.dirty) saveAll(true); cloneDialog(ctx, tid, frozen ? v : null); });
    ctx.dialog('preview-version', function () {
      var r = curTable() || (S.sheets[0] && S.sheets[0].tables[0] ? { tb: ensure(S.sheets[0].tables[0]), sh: S.sheets[0] } : null); if (!r) { E.Toast('Nothing to preview yet — add a sheet and a table', { tone: 'warning' }); return; }
      var lang = 'en', tb = r.tb, slot = h('div', { class: 'stack' }); function paint() { slot.textContent = ''; slot.appendChild(previewGrid(tb, lang)); }
      var pick = E.Select({ id: 'tv-pv-table', label: 'Table', options: r.sh.tables.slice(0, 21).map(function (x) { return { value: x.id, label: tlabel(x) }; }), value: tb.id, onChange: function (id) { tb = ensure(findTable(S, id).tb); paint(); } });
      E.Dialog({ id: 'tv-pv', wide: true, title: 'Preview as a data-entry operator', body: function (b) { b.appendChild(h('p', 'Sheet “' + r.sh.name.en + '” with sample values. Unsaved changes are included, so you can check them before saving.')); b.appendChild(h('div', { class: 'tpl-pickrow' }, pick, E.Field({ id: 'tv-pv-lang2', label: 'Language', control: h('div', E.Segmented({ id: 'tv-pv-lang2', label: 'Language', value: lang, options: LANGS.map(function (l) { return { value: l[0], label: l[1] }; }), onChange: function (l) { lang = l; paint(); } })) }))); b.appendChild(slot); paint(); }, footer: [{ label: 'Close', variant: 'primary' }] });
    });
    ctx.dialog('result-presentation', function () { presResult(); });

    function confirmDelete(o) { E.ConfirmDialog({ id: o.id, title: o.title, consequences: o.consequences.filter(Boolean).concat([{ text: draft ? 'You can undo right after deleting. Until you save, leaving the page also discards it.' : '', note: true }]).filter(function (c) { return typeof c === 'string' || c.text; }), verb: o.verb, icon: 'trash', onConfirm: function () { var before = snap(S), dirtyBefore = S.dirty, keep = node; o.run(); markDirty(); paintAll(); undoable(o.done, before, dirtyBefore, keep); } }); }
    var docsOnBase = base ? base.documents : 0, breakLine = docsOnBase ? 'Breaking change: ' + F.plural(docsOnBase, { one: 'document', other: 'documents' }) + ' on ' + vname(base) + ' hold data here. Publishing will be blocked until you revert it or plan a migration.' : null;
    ctx.dialog('delete-column', function (c) { var r = curTable() || resolve('t:' + S.sheets[0].tables[0].id); c = c || colOf(r.tb, 'PM25') || r.tb.cols[0]; if (!draft || !c) return;
      var used = r.tb.formulas.filter(function (f) { return f.target !== c.code && refs(f.expr).indexOf(c.code) >= 0; }), own = r.tb.formulas.filter(function (f) { return f.target === c.code; }), rules = r.tb.rules.filter(function (x) { return x.scope === c.code; });
      confirmDelete({ id: 'tv-del-col', title: 'Delete column “' + cname(c) + '” from table ' + r.tb.no + '?', verb: 'Delete column', done: 'Column “' + cname(c) + '” deleted', consequences: [own.length ? 'Its formula is deleted with it.' : null, used.length ? F.plural(used.length, { one: 'formula refers', other: 'formulas refer' }) + ' to ' + c.code + ' and will stop compiling: ' + used.map(function (f) { return f.target; }).join(', ') + '.' : 'No formula refers to it.', rules.length ? F.plural(rules.length, { one: 'validation rule', other: 'validation rules' }) + ' on this column will no longer fire.' : null, c.base ? breakLine : 'The column was added in this draft, so no document has data in it.'],
        run: function () { removeColumn(S, r.tb, c); r.tb.rules = r.tb.rules.filter(function (x) { return x.scope !== c.code; }); } }); });
    ctx.dialog('delete-table', function (tb) { var r = tb ? findTable(S, tb.id) : (curTable() || findTable(S, S.sheets[0].tables[0].id)); if (!draft || !r) return; tb = ensure(r.tb); var rel = relationsOf(S).filter(function (x) { return x.from === tb.id || x.to === tb.id; });
      confirmDelete({ id: 'tv-del-table', title: 'Delete table “' + tlabel(tb) + '”?', verb: 'Delete table', done: 'Table “' + tb.no + '” deleted', consequences: [F.plural(tb.cols.length, { one: 'column', other: 'columns' }) + ', ' + F.plural(tb.formulas.length, { one: 'formula', other: 'formulas' }) + ' and ' + F.plural(tb.rules.length, { one: 'rule', other: 'rules' }) + ' are deleted with it.', rel.length ? F.plural(rel.length, { one: 'table relation uses', other: 'table relations use' }) + ' this table and will be removed.' : null, tb.base ? breakLine : 'The table was added in this draft, so no document has data in it.'],
        run: function () { var at = r.sh.tables.indexOf(tb); r.sh.tables.splice(at, 1); S.relations = relationsOf(S).filter(function (x) { return x.from !== tb.id && x.to !== tb.id; }); if (tb.base) breaking(S, 'Table', r.sh.name.en, tlabel(tb), 'Table “' + tlabel(tb) + '” deleted', tb.cols.length + ' columns', { kind: 'table', sh: r.sh.id, at: at, item: tb }); else S.changes = S.changes.filter(function (x) { return x.code !== tb.id; }); node = 's:' + r.sh.id; S.ui.node = node; ctx.setQuery({ node: node, tab: null }); } }); });
    ctx.dialog('delete-sheet', function (sh) { sh = sh || (resolve(node).sh) || S.sheets[S.sheets.length - 1]; if (!draft || !sh) return;
      confirmDelete({ id: 'tv-del-sheet', title: 'Delete sheet “' + sh.name.en + '”?', verb: 'Delete sheet', done: 'Sheet “' + sh.name.en + '” deleted', consequences: [F.plural(sh.tables.length, { one: 'table is', other: 'tables are' }) + ' deleted with it, including their columns, formulas and rules.', 'Access and period rules of this sheet are removed.', sh.base ? breakLine : null, { text: 'To hide a sheet without losing it, turn off “Included in this version” instead.', note: true }],
        run: function () { var at = S.sheets.indexOf(sh); S.sheets.splice(at, 1); if (sh.base) breaking(S, 'Sheet', sh.name.en, null, 'Sheet “' + sh.name.en + '” deleted', sh.tables.length + ' tables', { kind: 'sheet', at: at, item: sh }); node = 'root'; S.ui.node = node; ctx.setQuery({ node: node, tab: null }); } }); });
    ctx.dialog('delete-row', function (a) { var r = curTable() || resolve('t:' + S.sheets[0].tables[0].id), i = a ? a.i : 0, nm = r.tb.rows.items[i]; if (!draft || nm == null) return;
      confirmDelete({ id: 'tv-del-row', title: 'Delete row “' + nm + '” from table ' + r.tb.no + '?', verb: 'Delete row', done: 'Row “' + nm + '” deleted', consequences: ['New documents will not have this row.', r.tb.base ? breakLine : null, r.tb.rows.source ? { text: 'The registry entry itself is not touched.', note: true } : null],
        run: function () { r.tb.rows.items.splice(i, 1); if (r.tb.base && docsOnBase) breaking(S, 'Row', r.sh.name.en, tlabel(r.tb), 'Row “' + nm + '” deleted', 'Fixed row ' + (i + 1), { kind: 'row', tb: r.tb.id, at: i, item: nm }); } }); });
    ctx.dialog('delete-formula', function (f) { var r = curTable() || resolve('t:' + S.sheets[0].tables[0].id); f = f || r.tb.formulas[0]; if (!draft || !f) return; var c = colOf(r.tb, f.target);
      confirmDelete({ id: 'tv-del-formula', title: 'Delete the formula for “' + (c ? cname(c) : f.target) + '”?', verb: 'Delete formula', done: 'Formula for ' + f.target + ' deleted', consequences: ['Column ' + f.target + ' stays, but nothing fills it: it will be empty in documents until another formula targets it.', f.base && docsOnBase ? 'Guarded change: calculated values disappear from ' + F.plural(docsOnBase, { one: 'document', other: 'documents' }) + ' on the next recalculation.' : null],
        run: function () { r.tb.formulas.splice(r.tb.formulas.indexOf(f), 1); recheck(r.tb); if (f.base) addChange(S, { kind: 'removed', cls: 'Guarded', type: 'Formula', sheet: r.sh.name.en, table: tlabel(r.tb), title: 'Formula for “' + f.target + '” deleted', was: f.target + ' = ' + f.expr, now: null, effect: 'The column becomes empty in every document on the next recalculation.' }); else S.changes = S.changes.filter(function (x) { return x.code !== 'f:' + r.tb.id + ':' + f.id; }); } }); });
    ctx.dialog('delete-rule', function (x) { var r = curTable() || resolve('t:' + S.sheets[0].tables[0].id); x = x || r.tb.rules[0]; if (!draft || !x) return;
      confirmDelete({ id: 'tv-del-rule', title: 'Delete rule “' + x.name + '”?', verb: 'Delete rule', done: 'Rule “' + x.name + '” deleted', consequences: [x.level === 'Error' ? 'Values that break it will no longer stop a sheet from being submitted.' : 'Operators will no longer see this ' + x.level.toLowerCase() + '.', 'Issues it already raised disappear from open documents on the next validation.'],
        run: function () { r.tb.rules.splice(r.tb.rules.indexOf(x), 1); if (x.base) addChange(S, { kind: 'removed', cls: 'Safe', type: 'Rule', sheet: r.sh.name.en, table: tlabel(r.tb), title: 'Rule “' + x.name + '” deleted', was: x.level + ' · ' + x.cond, now: null, effect: 'Less strict: nothing that passed before fails now.' }); else S.changes = S.changes.filter(function (y) { return y.code !== 'r:' + r.tb.id + ':' + x.id; }); } }); });
    ctx.dialog('delete-draft', function () { if (!draft) return; var list = versionsOf(tid), at = list.indexOf(v);
      E.ConfirmDialog({ id: 'tv-del-draft', title: 'Delete draft ' + vname(v) + ' of “' + t.name + '”?', consequences: [F.plural(S.changes.length, { one: 'change', other: 'changes' }) + ' against ' + (base ? vname(base) : 'an empty structure') + ' will be lost' + (S.dirty ? ', including ' + F.plural(S.dirty, { one: 'unsaved change', other: 'unsaved changes' }) : '') + '.', base ? 'Published ' + vname(base) + ' and its ' + F.plural(base.documents, { one: 'document', other: 'documents' }) + ' are not affected.' : 'The template will have no versions left.', { text: 'You can undo this for a few seconds after deleting.', note: true }], verb: 'Delete draft', icon: 'trash',
        onConfirm: function () { list.splice(at, 1); S.dirty = 0; E.go(BASE + '/' + tid, { force: true, toast: { text: 'Draft ' + vname(v) + ' deleted', duration: 8000, action: { label: 'Undo', onClick: function () { list.splice(at, 0, v); E.go(vhref(tid, vid), { toast: 'Draft restored' }); } } } }); } }); });

    ctx.dialog('add-sheet', function () { if (!draft) return;
      var names = langFields('tv-as-name', 'Sheet name', { en: '', ru: '', kk: '' }, { hint: 'English is required; the others fall back to it.' }), code = E.Input({ id: 'tv-as-code', label: 'Code', mono: true, required: true, maxlength: 12, hint: 'Capital letters and digits, for example NOISE. Used in cross-sheet formulas and exports.', onInput: function (val, inp) { inp.value = val.toUpperCase(); } });
      E.Dialog({ id: 'tv-add-sheet', title: 'Add sheet', body: h('div', { class: 'stack gap-3' }, names, code), footer: [{ label: 'Cancel', variant: 'ghost', align: 'left' }, { label: 'Add sheet', variant: 'primary', id: 'tv-as-apply', onClick: function () { var n = names.read(), c = code.value.trim();
        if (!n.en) { names.inputs.en.focus(); E.Toast('The English name is required', { tone: 'warning' }); return false; } if (!/^[A-Z][A-Z0-9]{1,11}$/.test(c)) { code.setError('2–12 capital letters or digits, starting with a letter'); code.input.focus(); return false; } if (S.sheets.some(function (s) { return s.code === c; })) { code.setError('Sheet ' + c + ' already exists'); code.input.focus(); return false; }
        var sh = { id: 'sn' + (S.sheets.length + 1) + c, si: 9, code: c, name: n, enabled: true, base: false, tables: [] }; S.sheets.push(sh); S.ui.open[sh.id] = true; addChange(S, { kind: 'added', cls: 'Safe', type: 'Sheet', code: sh.id, sheet: n.en, table: null, title: 'Sheet “' + n.en + '” added', was: null, now: c, effect: 'Existing documents get a new empty sheet after migration; until then they do not show it.' });
        markDirty(); if (ctx.state === 'empty') { E.go(vhref(tid, vid, null, { node: 's:' + sh.id }), { toast: 'Sheet “' + n.en + '” added — now add its first table' }); return; } select('s:' + sh.id); E.Toast('Sheet “' + n.en + '” added — now add its first table'); } }] }); });
    ctx.dialog('add-table', function () { if (!draft || !S.sheets.length) return; var r = resolve(node), sh0 = r.sh || S.sheets[0];
      function nextNo(sh) { var l = sh.tables[sh.tables.length - 1]; if (!l) return '1.1'; var p = l.no.split('.'); return p[0] + '.' + ((parseInt(p[1], 10) || 0) + 1); }
      var sheet = E.Select({ id: 'tv-at-sheet', label: 'Sheet', options: S.sheets.map(function (s) { return { value: s.id, label: s.name.en }; }), value: sh0.id, onChange: function (id) { no.value = nextNo(findSheet(S, id)); } }), no = E.Input({ id: 'tv-at-no', label: 'Number', mono: true, required: true, value: nextNo(sh0) });
      var names = langFields('tv-at-name', 'Table name', { en: '', ru: '', kk: '' }), from = E.Select({ id: 'tv-at-from', label: 'Start from', options: [{ value: '', label: 'An empty table' }].concat(r.tb ? [{ value: r.tb.id, label: 'A copy of ' + tlabel(r.tb) }] : [], sh0.tables.slice(0, 7).filter(function (x) { return !r.tb || x.id !== r.tb.id; }).map(function (x) { return { value: x.id, label: 'A copy of ' + tlabel(x) }; })), value: '', hint: 'A copy brings columns, rows, formulas and rules. Relations are not copied.' });
      E.Dialog({ id: 'tv-add-table', title: 'Add table', body: h('div', { class: 'stack gap-3' }, h('div', { class: 'form-grid' }, sheet, no), names, from), footer: [{ label: 'Cancel', variant: 'ghost', align: 'left' }, { label: 'Add table', variant: 'primary', id: 'tv-at-apply', onClick: function () { var n = names.read(), sh = findSheet(S, sheet.value), num = no.value.trim();
        if (!num) { no.setError('Give the table a number, for example ' + nextNo(sh)); no.input.focus(); return false; } if (sh.tables.some(function (x) { return x.no === num; })) { no.setError('Table ' + num + ' already exists in this sheet'); no.input.focus(); return false; } if (!n.en) { names.inputs.en.focus(); E.Toast('The English name is required', { tone: 'warning' }); return false; }
        var srcT = from.value && findTable(S, from.value), tb = srcT ? JSON.parse(JSON.stringify(ensure(srcT.tb))) : { cols: [], rows: { mode: 'dynamic', source: '', items: [], max: 200, key: 'Row code', allowDelete: true }, formulas: [], rules: [], seq: 10 };
        tb.id = 'tn' + sh.id + '-' + (sh.tables.length + 1); tb.si = sh.si; tb.no = num; tb.name = n; tb.short = n.en; tb.base = false; (tb.cols || []).forEach(function (c) { c.base = false; }); tb.formulas.forEach(function (f) { f.base = false; }); tb.rules.forEach(function (x) { x.base = false; });
        sh.tables.push(tb); addChange(S, { kind: 'added', cls: 'Safe', type: 'Table', code: tb.id, sheet: sh.name.en, table: tlabel(tb), title: 'Table “' + tlabel(tb) + '” added', was: null, now: tb.cols.length + ' columns', effect: 'New table: existing documents get it empty after migration.' }); S.ui.open[sh.id] = true; markDirty(); select('t:' + tb.id); E.Toast('Table ' + num + ' added' + (tb.cols.length ? '' : ' — add its first column')); } }] }); });

    /* ---- publish: Wizard → result → migrate ---- */
    ctx.dialog('publish-version', function () {
      if (!draft) { E.Toast('Only a draft can be published', { tone: 'warning' }); return; }
      var demo = ctx.query.step, data = { number: S.migrate && base && S.changes.some(function (x) { return x.cls === 'Breaking'; }) ? nextVersion(base.version, 'major') : v.version, reason: demo === 'review' ? 'Order № 221-Ө, revision of 1 September: totals are also reported in kilograms, and run hours above the period length are rejected.' : '' }, f = {};
      function counts() { var c = { Breaking: 0, Guarded: 0, Safe: 0 }; S.changes.forEach(function (x) { c[x.cls]++; }); return c; }
      function blocked() { return counts().Breaking > 0 && docsOnBase > 0 && !S.migrate; }
      var api = E.Wizard({ id: 'tv-pub', title: 'Publish ' + vname(v) + ' · ' + t.name, data: data, steps: [
        { id: 'checks', label: 'Checks', canNext: function () { return !issuesOf(S).length; }, validate: function () { var n = issuesOf(S).length; return n ? 'Fix ' + F.plural(n, { one: 'problem', other: 'problems' }) + ' first — each one links to the place.' : null; },
          render: function (b, d, wz) { var list = issuesOf(S), failed = {}; list.forEach(function (i) { failed[FAIL_IX[i.kind]] = 1; });
            if (S.dirty) b.appendChild(E.Banner({ icon: 'save', text: F.plural(S.dirty, { one: 'unsaved change is', other: 'unsaved changes are' }) + ' saved to the draft before publishing.' }));
            if (list.length) { b.appendChild(E.Banner({ tone: 'danger', id: 'tv-pub-blocked', title: F.plural(list.length, { one: 'problem blocks', other: 'problems block' }) + ' publishing', text: 'Each one opens the exact place. Nothing here is lost — press Publish again when they are fixed.' }));
              var ul = h('ul', { class: 'tpl-chk', id: 'tv-pub-issues' }); list.forEach(function (i) { ul.appendChild(h('li', { class: 'bad' }, icon('alertCircle', 14), h('div', { class: 'tx' }, h('b', i.title), h('small', i.text), h('small', { class: 'mono' }, i.where)), E.Button({ label: 'Go to', size: 'sm', icon: 'arrowR', id: 'tv-pub-go-' + i.id, onClick: function () { wz.close(); goTo(i); } }))); }); b.appendChild(ul); }
            else b.appendChild(E.Banner({ icon: 'checkCircle', id: 'tv-pub-clean', title: 'All ' + CHECKS.length + ' checks passed', text: 'The structure is consistent. Next: see what the change means for existing documents.' }));
            var ok = CHECKS.filter(function (c, i) { return !failed[i]; }), pl = h('ul', { class: 'tpl-chk' }); ok.forEach(function (c) { pl.appendChild(h('li', icon('check', 14), h('div', { class: 'tx' }, c), null)); });
            b.appendChild(E.Collapsible({ id: 'tv-pub-passed', title: F.plural(ok.length, { one: 'check', other: 'checks' }) + ' passed', summary: list.length ? null : 'Show the list', content: pl })); } },
        { id: 'changes', label: 'Changes', canNext: function () { return !blocked(); }, validate: function () { return blocked() ? 'A breaking change blocks publishing. Revert it, or choose to migrate the documents afterwards.' : null; },
          render: function paint(b, d, wz) { b.textContent = ''; var c = counts();
            b.appendChild(h('p', base ? 'Compared with ' + vname(base) + ', which ' + F.plural(docsOnBase, { one: 'document uses', other: 'documents use' }) + '. Documents stay on ' + vname(base) + ' until someone migrates them.' : 'This is the first version: there is nothing to compare with.'));
            if (c.Breaking && docsOnBase) { b.appendChild(E.Banner({ tone: S.migrate ? 'warning' : 'danger', id: 'tv-pub-breaking', title: S.migrate ? 'Breaking change accepted — migration required afterwards' : 'Publishing is blocked by ' + F.plural(c.Breaking, { one: 'breaking change', other: 'breaking changes' }), text: F.plural(docsOnBase, { one: 'document', other: 'documents' }) + ' on ' + vname(base) + ' hold data that would become unreachable. Two ways out: revert the change below, or publish and migrate the documents afterwards.' }));
              b.appendChild(E.Checkbox({ id: 'tv-pub-migrate', label: 'Publish anyway — I will migrate the ' + docsOnBase + ' documents afterwards', checked: S.migrate, hint: 'They stay on ' + vname(base) + ' and keep working. The migration moves affected values to the document archive; nothing is deleted without a confirmation. The version number becomes ' + nextVersion(base.version, 'major') + '.', onChange: function (on) { S.migrate = on; d.number = on ? nextVersion(base.version, 'major') : v.version; paint(b, d, wz); wz.refreshButtons(); } })); }
            if (!S.changes.length) b.appendChild(h('div', { class: 'mini-empty' }, 'No differences from ' + (base ? vname(base) : 'the previous version') + '.'));
            ['Breaking', 'Guarded', 'Safe'].forEach(function (cls) { var items = S.changes.filter(function (x) { return x.cls === cls; }); if (!items.length) return;
              var help = CLASS_HELP[cls].split(' — ')[1]; b.appendChild(h('div', { class: 'tpl-grp' }, classBadge(cls), h('span', { class: 'muted t-xs' }, help.charAt(0).toUpperCase() + help.slice(1))));
              var ul = h('ul', { class: 'tpl-chk' }); items.forEach(function (x) { ul.appendChild(h('li', { class: cls === 'Breaking' ? 'bad' : cls === 'Guarded' ? 'warn' : null }, icon(x.kind === 'added' ? 'plus' : x.kind === 'removed' ? 'minus' : 'pencil', 14), h('div', { class: 'tx' }, h('b', x.title), h('small', [x.sheet, x.table].filter(Boolean).join(' › ')), h('small', x.effect)),
                x.revert ? E.Button({ label: 'Revert', size: 'sm', icon: 'undo', id: 'tv-pub-revert-' + x.id, onClick: function () { revertChange(S, x); if (!counts().Breaking) { S.migrate = false; d.number = v.version; } markDirty(); paintTree(); paintMain(); paint(b, d, wz); wz.refreshButtons(); E.Toast('Reverted: ' + x.title.toLowerCase()); } }) : null)); }); b.appendChild(ul); }); } },
        { id: 'reason', label: 'Reason', render: function (b, d) {
            f.number = E.Input({ id: 'tv-pub-number', label: 'Version number', mono: true, required: true, value: d.number, hint: S.migrate ? 'Raised to the next major number because documents need a migration.' : 'Three numbers. Documents and the audit trail show it.', onInput: function (val) { d.number = val; } });
            f.reason = E.Textarea({ id: 'tv-pub-reason', label: 'Reason for change', required: true, rows: 4, maxlength: 500, value: d.reason, hint: 'Goes to the audit trail and the version history. Write what changed and why — an order number, a request, a correction. At least 10 characters.', onInput: function (val) { d.reason = val; } });
            b.appendChild(h('div', { class: 'stack gap-3' }, f.number, f.reason)); },
          validate: function (d) { f.number.setError(null); f.reason.setError(null);
            if (!/^\d+\.\d+\.\d+$/.test(d.number.trim())) { f.number.setError('Use three numbers separated by dots'); f.number.input.focus(); return 'Check the version number.'; }
            if (versionsOf(tid).some(function (x) { return x !== v && x.version === d.number.trim(); })) { f.number.setError('Version ' + d.number + ' already exists'); f.number.input.focus(); return 'Pick another version number.'; }
            if (d.reason.trim().length < 10) { f.reason.setError('Explain the change in at least 10 characters'); f.reason.input.focus(); return 'A reason is required: it is the only record of why the structure changed.'; } return null; } }],
        summary: function (d) { var c = counts(); return E.KeyValue([{ label: 'Version', value: h('span', { class: 'mono' }, 'v' + d.number.trim()), hint: base ? 'Replaces ' + vname(base) + ' for new documents' : 'First published version' }, { label: 'Checks', value: CHECKS.length + ' of ' + CHECKS.length + ' passed' }, { label: 'Changes', value: h('span', { class: 'sumline' }, c.Breaking ? classBadge('Breaking') : null, c.Breaking ? String(c.Breaking) : null, classBadge('Guarded'), String(c.Guarded), classBadge('Safe'), String(c.Safe)) }, { label: 'Existing documents', value: docsOnBase ? docsOnBase + ' stay on ' + vname(base) : 'None', hint: S.migrate ? 'Migration is required afterwards — you will be offered to start it' : docsOnBase ? 'They keep working; migrate them when convenient' : null }, { label: 'Reason', value: d.reason.trim() }, { label: 'After publishing', value: 'Structure becomes read-only', hint: 'Only presentation can be edited. Any other change needs a new draft.' }], { wide: true }); },
        reviewText: 'Publishing cannot be undone: the structure is frozen and new documents start using it at once.', applyLabel: 'Publish version',
        onApply: function (d, wz) { S.dirty = 0; S.saved = snap(S); v.state = 'Published'; v.version = d.number.trim(); v.publishedBy = ctx.me.name; v.publishedAt = D.today.slice(0, 10); v.note = d.reason.trim(); v.documents = 0; t.state = 'Published'; t.version = v.version; t.versions = versionsOf(tid).length; t.updated = D.today.slice(0, 10);
          wz.close(); E.go(vhref(tid, vid, null, { dialog: 'result-published' }), { force: true }); } });
      jump(api, demo, ['checks', 'changes', 'reason', 'review']);
    });
    ctx.dialog('result-published', function () { var old = versionsOf(tid).filter(function (x) { return x !== v && x.state === 'Published' && x.documents; })[0] || (draft ? base : null), n = old ? old.documents : 0;
      E.ResultDialog({ id: 'tv-published', title: vname(v) + ' is published', text: 'New documents of “' + t.name + '” use it from now on. The structure is frozen; presentation can still be edited.' + (n ? ' ' + F.plural(n, { one: 'document stays', other: 'documents stay' }) + ' on ' + vname(old) + ' and keep working until you migrate them.' : ''),
        body: E.KeyValue([{ label: 'What next', value: n ? 'Migrate documents when the reporting period allows it' : 'Create the first document from this template' }, { label: 'Audit trail', value: h('a', { href: '#/admin/audit' }, 'Template.Publish recorded') }]),
        actions: [{ label: 'Stay here', primary: false }, { label: 'Open template', href: '#' + BASE + '/' + tid, primary: !n }].concat(n ? [{ label: 'Migrate ' + n + ' documents…', primary: true, onClick: function () { setTimeout(function () { ctx.openDialog('migrate-documents'); }, 0); } }] : []) }); });
    ctx.dialog('migrate-documents', function () { var old = versionsOf(tid).filter(function (x) { return x !== v && x.state === 'Published' && x.documents; })[0] || base; if (!old) return; var n = old.documents;
      E.ConfirmDialog({ id: 'tv-migrate', title: 'Migrate ' + F.plural(n, { one: 'document', other: 'documents' }) + ' to ' + vname(v) + '?', danger: false, consequences: ['Draft, returned and rejected sheets move to the new structure and are validated again.', 'Submitted and approved sheets are not touched: they stay on ' + vname(old) + ' until someone reopens them.', S.migrate ? 'Values of deleted columns, rows and tables move to the document archive. Nothing is deleted.' : null, { text: 'Runs in the background. Every document is listed in the job result, and each one can be rolled back for 30 days.', note: true }].filter(Boolean), verb: 'Start migration', icon: 'play',
        onConfirm: function () { E.tasks.start({ title: 'Migrate ' + n + ' documents to ' + vname(v), icon: 'swap', detail: t.id + ' · ' + vname(old) + ' → ' + vname(v), duration: 6000, doneDetail: n + ' documents migrated · 0 failed', onDone: function () { v.documents += n; old.documents = 0; }, result: { label: 'Open template', onClick: function () { E.go(BASE + '/' + tid + '?tab=usage'); } } }); E.Toast('Migration continues in My tasks'); } }); });
  } });

  /* ---------- shared header for the four sub-pages of a version ---------- */
  function subPage(el, ctx, o) {
    var tid = ctx.params.id, vid = ctx.params.versionId, t = tpl(tid), v = t && ver(tid, vid);
    if (!v) { notFound(el, ctx, 'Version', t ? { label: t.name, href: '#' + BASE + '/' + tid } : { label: 'Templates', href: '#' + BASE }); return null; }
    var S = struct(tid, vid), draft = v.state === 'Draft';
    if (draft && ctx.query.changes === 'breaking') { injectBreaking(S); S.saved = snap(S); }
    ctx.setTitle(o.title + ' · ' + vname(v));
    return { tid: tid, vid: vid, t: t, v: v, S: S, draft: draft, frozenBanner: function (what) { return draft ? null : E.Banner({ icon: v.state === 'Archived' ? 'archive' : 'lock', id: 'tpl-sub-frozen', title: what + ' are read-only in ' + vname(v), text: v.state === 'Archived' ? 'This version is archived.' : 'They are part of the published structure: ' + F.plural(v.documents, { one: 'document relies', other: 'documents rely' }) + ' on them. Clone the version to change them.', actions: v.state === 'Archived' ? [] : [{ label: 'Open the constructor', href: vhref(tid, vid) }] }); },
      head: function (extra) { return Object.assign({ ctx: ctx, title: o.title, subtitle: o.subtitle, badge: E.StatusBadge('version', v.state), back: { label: vname(v), href: vhref(tid, vid) }, meta: h('div', { class: 'muted t-xs' }, t.name + ' · ', h('span', { class: 'mono' }, t.id + ' · ' + vname(v))) }, extra || {}); } };
  }

  /* ================= 4. …/relations ================= */
  var REL_TYPE = { rollup: ['Roll-up', 'Rows of the source are summed into the target by the matching key.'], lookup: ['Lookup', 'The target reads one value from the source by the matching key.'], copy: ['Row copy', 'The target gets the same set of rows as the source.'] };
  function relationsOf(S) {
    if (S.relations) return S.relations;
    var a = S.sheets[0] ? S.sheets[0].tables : [], g = S.sheets.filter(function (s) { return s.si === 3; })[0] || S.sheets[1], out = [];
    function add(f, t, type, key, note) { if (f && t) out.push({ id: 'REL-' + (out.length + 1), from: f.id, to: t.id, type: type, key: key, note: note }); }
    add(a[1], a[0], 'lookup', 'Emission source', 'Fuel gas volume for the emission calculation'); add(a[3], a[0], 'lookup', 'Substance', 'Emission factor by substance'); add(a[0], a[4], 'rollup', 'Emission source', 'Monthly totals are the sum of mass emissions');
    add(a[4], a[6], 'copy', 'Emission source', 'Permit comparison follows the rows of the totals'); add(a[4], g && g.tables[0], 'rollup', 'Substance', 'The CH₄ total feeds the inventory of the next sheet');
    return (S.relations = out);
  }
  function relCycle(rels) { var adj = {}, found = null; rels.forEach(function (r) { (adj[r.from] = adj[r.from] || []).push(r.to); });
    function visit(n, path) { if (found) return; var at = path.indexOf(n); if (at >= 0) { found = path.slice(at).concat(n); return; } (adj[n] || []).forEach(function (m) { visit(m, path.concat(n)); }); }
    Object.keys(adj).forEach(function (k) { visit(k, []); }); return found; }
  function tshort(S, id) { var f = findTable(S, id); return f ? f.sh.code + ' ' + f.tb.no : id; }
  function diagram(S, rels, cyc, tid, vid) {
    var ids = [], depth = {}, rowAt = {}, pos = {}, NW = 184, NH = 40, GX = 80, GY = 16, maxD = 0, maxR = 0, svg = '';
    rels.forEach(function (r) { [r.from, r.to].forEach(function (id) { if (ids.indexOf(id) < 0) { ids.push(id); depth[id] = 0; } }); });
    for (var k = 0; k < ids.length; k++) rels.forEach(function (r) { if (!r.temp && depth[r.to] < depth[r.from] + 1 && depth[r.from] < ids.length) depth[r.to] = depth[r.from] + 1; });
    var PADL = 32;
    ids.forEach(function (id) { var d = depth[id]; rowAt[d] = rowAt[d] || 0; pos[id] = { x: PADL + d * (NW + GX), y: 8 + rowAt[d] * (NH + GY) }; rowAt[d]++; maxD = Math.max(maxD, d); maxR = Math.max(maxR, rowAt[d]); });
    function inCyc(r) { if (!cyc) return false; for (var i = 0; i < cyc.length - 1; i++) if (cyc[i] === r.from && cyc[i + 1] === r.to) return true; return false; }
    var back = rels.some(function (r) { return pos[r.to].x <= pos[r.from].x; }), bottom = 8 + maxR * NH + (maxR - 1) * GY, W = PADL + 24 + (maxD + 1) * NW + maxD * GX, H = bottom + 8 + (back ? 40 : 0), labels = '';
    rels.forEach(function (r) { var a = pos[r.from], b = pos[r.to], bad = inCyc(r), backw = b.x <= a.x, sx = a.x + NW, sy = a.y + NH / 2, ex = b.x, ey = b.y + NH / 2, low = bottom + 24;
      /* зворотне ребро (замикає цикл) іде ортогонально ПІД усіма вузлами, щоб не перетинати їх */
      var d = backw ? 'M' + sx + ' ' + sy + ' H' + (sx + 16) + ' V' + low + ' H' + (ex - 20) + ' V' + ey + ' H' + (ex - 2) : 'M' + sx + ' ' + sy + ' C' + (sx + 40) + ' ' + sy + ',' + (ex - 40) + ' ' + ey + ',' + (ex - 2) + ' ' + ey;
      svg += '<path class="ed' + (bad ? ' bad' : '') + '" stroke-width="1.5" stroke-linejoin="round" d="' + d + '" marker-end="url(#tpl-arr' + (bad ? '-bad' : '') + ')"' + (r.type === 'lookup' ? ' stroke-dasharray="4 3"' : '') + '><title>' + esc(tshort(S, r.from) + ' → ' + tshort(S, r.to) + ' · ' + REL_TYPE[r.type][0]) + '</title></path>';
      labels += '<text class="el' + (bad ? ' bad' : '') + '" text-anchor="middle" x="' + (backw ? (sx + ex) / 2 : sx + GX / 2) + '" y="' + (backw ? low - 6 : sy + (ey - sy) / 2 - 6) + '">' + esc(REL_TYPE[r.type][0].toLowerCase()) + '</text>'; });
    svg += labels;
    ids.forEach(function (id) { var f = findTable(S, id), p = pos[id]; if (!f) return; var nm = f.tb.name.en.length > 26 ? f.tb.name.en.slice(0, 25) + '…' : f.tb.name.en;
      svg += '<a class="nd" href="' + esc(vhref(tid, vid, null, { node: 't:' + id })) + '"><title>' + esc('Open table ' + tlabel(f.tb) + ' in the constructor') + '</title><rect x="' + p.x + '" y="' + p.y + '" width="' + NW + '" height="' + NH + '" rx="4"/><text x="' + (p.x + 10) + '" y="' + (p.y + 16) + '">' + esc(f.sh.code + ' · ' + f.tb.no) + '</text><text class="sub" x="' + (p.x + 10) + '" y="' + (p.y + 31) + '">' + esc(nm) + '</text></a>'; });
    var defs = '<defs><marker id="tpl-arr" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path class="hd" d="M0 0L10 5L0 10z"/></marker><marker id="tpl-arr-bad" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path class="hd bad" d="M0 0L10 5L0 10z"/></marker></defs>';
    return h('div', { class: 'tpl-dg', id: 'tpl-rel-diagram', html: '<svg xmlns="http://www.w3.org/2000/svg" width="' + W + '" height="' + H + '" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="' + esc(ids.length + ' tables connected by ' + rels.length + ' relations. Solid arrows copy or sum rows, dashed arrows look a value up.') + '">' + defs + svg + '</svg>' });
  }
  E.screen(VP + '/relations', { title: 'Table relations', icon: 'link', navPath: BASE, example: X + '/relations', crumbs: function (ctx) { return crumbsFor(ctx, 'Table relations'); }, render: function (el, ctx) {
    var P = subPage(el, ctx, { title: 'Table relations', subtitle: 'How tables feed each other: totals rolled up, values looked up, row sets copied. The arrows show which way data flows.' }); if (!P) return;
    var S = P.S, draft = P.draft, page = h('div', { class: 'page' }), result = h('div', { hidden: true }); el.appendChild(page);
    function rels() { var list = relationsOf(S).slice(), a = S.sheets[0] && S.sheets[0].tables; if (ctx.query.cycle && a && a[6] && !list.some(function (r) { return r.temp; })) list.push({ id: 'REL-X', from: a[6].id, to: a[1].id, type: 'lookup', key: 'Emission source', note: 'Not saved — example of a relation that closes a cycle', temp: true }); return ctx.state === 'empty' ? [] : list; }
    page.appendChild(E.PageHeader(P.head({ id: 'tpl-rel', primary: draft ? { label: 'New relation', icon: 'plus', id: 'tpl-rel-new', onClick: function () { ctx.openPanel('new'); } } : null, secondary: [{ label: 'Check for cycles', icon: 'checkCircle', id: 'tpl-rel-check', onClick: function () { ctx.openDialog('result-cycle'); } }] })));
    E.append(page, [P.frozenBanner('Table relations'), result]);
    function label(r) { return tshort(S, r.from) + ' → ' + tshort(S, r.to); }
    function check() { var cyc = relCycle(rels()); result.hidden = false; result.textContent = '';
      result.appendChild(cyc ? E.Banner({ tone: 'danger', id: 'tpl-rel-cycle', title: 'Cycle found: ' + cyc.map(function (id) { return tshort(S, id); }).join(' → '), text: 'Each of these tables would wait for the other, so recalculation never finishes. Delete or redirect one of the red arrows. A version with a cycle cannot be published.' }) : E.ResultBanner({ id: 'tpl-rel-ok', title: 'No cycles', text: rels().length + ' relations were followed from start to end; data always flows one way. Relations are also checked when you publish.' })); return cyc; }
    var body = h('div', { class: 'stack gap-4' }); page.appendChild(body);
    function paint() { body.textContent = ''; var list = rels(), cyc = relCycle(list); if (cyc) check();
      body.appendChild(E.StateSwitch(ctx.state === 'empty' ? 'empty' : ctx.state, { loading: { kind: 'table', rows: 6, cols: 4 }, error: { code: 'ECR-TPL-0520', onRetry: function () { ctx.refresh(); } },
        empty: { icon: 'link', title: 'No relations between tables', text: 'Every table stands alone: nothing is rolled up or looked up. Add a relation when one table should take data from another.', actions: draft ? [{ label: 'New relation', variant: 'primary', icon: 'plus', onClick: function () { ctx.openPanel('new'); } }] : [] },
        data: function () { return h('div', { class: 'stack gap-4' },
          E.Section({ title: 'Map', hint: 'Solid arrow — rows are summed or copied · dashed — a value is looked up · click a table to open it', content: diagram(S, list, cyc, P.tid, P.vid) }),
          E.Section({ title: 'Relations', content: E.DataTable({ id: 'tpl-rel-tbl', auto: true, rows: list, rowKey: 'id', tall: true, onRowClick: function (r) { ctx.openPanel(r.id); }, rowLabel: function (r) { return 'Open relation ' + label(r); },
            columns: [{ key: 'from', label: 'From', sortable: false, render: function (r) { var f = findTable(S, r.from); return E.twoLine(f.tb.name.en, f.sh.name.en + ' · ' + f.tb.no); } }, { key: 'to', label: 'To', sortable: false, render: function (r) { var f = findTable(S, r.to); return E.twoLine(f.tb.name.en, f.sh.name.en + ' · ' + f.tb.no); } }, { key: 'type', label: 'Type', sortable: false, render: function (r) { return h('span', { title: REL_TYPE[r.type][1] }, REL_TYPE[r.type][0]); } }, { key: 'key', label: 'Matched by', sortable: false },
              { key: 'st', label: '', sortable: false, render: function (r) { return r.temp ? h('span', { class: 'tpl-bad' }, icon('alertCircle', 14), 'Closes a cycle') : null; } }] }) })); } })); }
    paint();
    ctx.panel('*', function (id) {
      var isNew = id === 'new', r = isNew ? { id: 'REL-' + (relationsOf(S).length + 1), from: '', to: '', type: 'rollup', key: 'Emission source', note: '' } : rels().filter(function (x) { return x.id === id; })[0]; if (!r) return; var ro = !draft;
      var opts = [{ value: '', label: 'Choose a table' }]; S.sheets.forEach(function (sh) { sh.tables.slice(0, 7).forEach(function (tb) { opts.push({ value: tb.id, label: sh.code + ' · ' + tlabel(tb) }); }); });
      var from = E.Select({ id: 'tpl-rel-from', label: 'From (source of data)', options: opts, value: r.from, disabled: ro }), to = E.Select({ id: 'tpl-rel-to', label: 'To (table that receives it)', options: opts, value: r.to, disabled: ro });
      var type = E.Select({ id: 'tpl-rel-type', label: 'Type', options: Object.keys(REL_TYPE).map(function (k) { return { value: k, label: REL_TYPE[k][0] + ' — ' + REL_TYPE[k][1] }; }), value: r.type, disabled: ro }), key = E.Select({ id: 'tpl-rel-key', label: 'Rows are matched by', options: ['Emission source', 'Substance', 'Row code', 'Period'], value: r.key, disabled: ro, hint: 'Both tables must have this key. Rows without a match are reported as a warning, not dropped.' });
      var note = E.Input({ id: 'tpl-rel-note', label: 'Note', value: r.note, disabled: ro }), err = h('div');
      E.Drawer({ id: 'tpl-rel-drawer', title: isNew ? 'New relation' : label(r), subtitle: isNew ? vname(P.v) : REL_TYPE[r.type][0] + ' · matched by ' + r.key, body: function (b) { if (ro) b.appendChild(P.frozenBanner('Table relations')); if (r.temp) b.appendChild(E.Banner({ tone: 'danger', title: 'This relation closes a cycle', text: 'It is shown as an example and cannot be saved.' })); E.append(b, [err, from, to, type, key, note]); },
        footer: ro || r.temp ? [{ label: 'Close' }] : [isNew ? null : { label: 'Delete…', icon: 'trash', variant: 'ghost', align: 'left', id: 'tpl-rel-delete', onClick: function () { setTimeout(function () { ctx.openDialog('delete-relation', r); }, 0); } }, { label: 'Cancel', variant: 'ghost' }, { label: isNew ? 'Add relation' : 'Save', variant: 'primary', id: 'tpl-rel-save', onClick: function () {
          from.setError(null); to.setError(null); err.textContent = '';
          if (!from.value) { from.setError('Choose the table data comes from'); from.input.focus(); return false; } if (!to.value) { to.setError('Choose the table that receives data'); to.input.focus(); return false; } if (from.value === to.value) { to.setError('A table cannot feed itself'); to.input.focus(); return false; }
          var next = relationsOf(S).filter(function (x) { return x !== r; }).concat([{ from: from.value, to: to.value }]), cyc = relCycle(next);
          if (cyc) { err.appendChild(E.Banner({ tone: 'danger', id: 'tpl-rel-err', title: 'This relation would close a cycle', text: cyc.map(function (x) { return tshort(S, x); }).join(' → ') + '. Reverse the direction or pick another table.' })); from.input.focus(); return false; }
          r.from = from.value; r.to = to.value; r.type = type.value; r.key = key.value; r.note = note.value.trim(); if (isNew) relationsOf(S).push(r); S.saved = snap(S); paint(); E.Toast('Relation ' + label(r) + (isNew ? ' added' : ' saved') + ' to draft ' + vname(P.v), 'success'); } }].filter(Boolean) }); });
    ctx.dialog('delete-relation', function (r) { r = r || relationsOf(S)[0]; if (!draft || !r) return; var f = findTable(S, r.to);
      E.ConfirmDialog({ id: 'tpl-rel-del', title: 'Delete relation “' + label(r) + '”?', consequences: ['Table ' + tlabel(f.tb) + ' stops receiving data this way: ' + (r.type === 'rollup' ? 'its rolled-up values become empty.' : r.type === 'lookup' ? 'formulas that read the looked-up value return nothing.' : 'its rows are no longer kept in step with the source.'), { text: 'The tables themselves and their data are not touched.', note: true }], verb: 'Delete relation', icon: 'trash',
        onConfirm: function () { var list = relationsOf(S), at = list.indexOf(r); list.splice(at, 1); S.saved = snap(S); paint(); E.Toast('Relation ' + label(r) + ' deleted', { duration: 8000, action: { label: 'Undo', onClick: function () { relationsOf(S).splice(at, 0, r); S.saved = snap(S); paint(); } } }); } }); });
    ctx.dialog('result-cycle', function () { check(); });
  } });

  /* ================= 5. …/compare ================= */
  function diffOf(tid, from, to) {
    if (!from || !to || from === to) return [];
    var S = struct(tid, to.id); if (to.state === 'Draft' && baseOf(tid, to) === from && S.changes.length) return S.changes;
    var R = rng(hashStr(tid + from.id + to.id)), n = S.sheets.length, out = [];
    function tb(k) { var sh = S.sheets[k % n]; return { sh: sh, tb: sh.tables[(k * 5 + 3) % sh.tables.length] }; }
    var pool = [['changed', 'Safe', 'Column', 'Caption of “VOC” changed', 'ЛОС', 'ЛОС (летучие органические соединения)', 'Only the header text changes.'], ['added', 'Safe', 'Table', 'Table added', null, '12 columns · 1 formula · 3 rules', 'Existing documents get it empty after migration.'], ['changed', 'Guarded', 'Column', 'Unit of “CH₄” changed', 'kg', 't', 'Stored values are not converted: check formulas that use this column.'],
      ['changed', 'Guarded', 'Rule', 'Rule “Total within the permit limit” is now an error', 'Level: Warning', 'Level: Error', 'Draft and returned sheets that break it cannot be submitted until corrected.'], ['removed', 'Breaking', 'Column', 'Column “Disposal site” deleted', 'DISPOSAL_SITE · Text', null, BREAK_EFFECT], ['added', 'Safe', 'Formula', 'Formula for “Total” added', null, 'TOTAL = SUM(COD:PO4)', 'Calculated on the next recalculation.'], ['removed', 'Safe', 'Rule', 'Rule “Hours within the period length” deleted', 'Warning · HOURS <= PeriodHours()', null, 'Less strict: nothing that passed before fails now.'], ['changed', 'Safe', 'Table', 'Column order changed', 'Previous order', 'New order', 'Only the on-screen and export order changes.']];
    if (!n) return out;
    pool.forEach(function (p, i) { if (R() < 0.55) { var w = tb(i + out.length); out.push({ id: 'CH-' + (i + 1) + '-' + p[0].charAt(0), kind: p[0], cls: p[1], type: p[2], sheet: w.sh.name.en, table: w.tb ? tlabel(w.tb) : null, title: p[3], was: p[4], now: p[5], effect: p[6] }); } });
    return out;
  }
  E.screen(VP + '/compare', { title: 'Compare versions', icon: 'columns', navPath: BASE, example: X + '/compare', crumbs: function (ctx) { return crumbsFor(ctx, 'Compare'); }, render: function (el, ctx) {
    var P = subPage(el, ctx, { title: 'Compare versions', subtitle: 'What changed between two versions, and what each change means for documents that already exist.' }); if (!P) return;
    var list = versionsOf(P.tid), to = P.v, from = ver(P.tid, ctx.query['with']) || baseOf(P.tid, to) || list[list.indexOf(to) - 1] || null, docs = from ? from.documents : 0;
    var page = h('div', { class: 'page page-fill' }); el.appendChild(page);
    page.appendChild(E.PageHeader(P.head({ id: 'tpl-cmp', secondary: from ? [{ label: 'Swap sides', icon: 'swap', id: 'tpl-cmp-swap', onClick: function () { E.go(vhref(P.tid, from.id, 'compare', { 'with': to.id })); } }] : [] })));
    function opt(v) { return { value: v.id, label: vname(v) + ' · ' + v.state + (v.documents ? ' · ' + F.plural(v.documents, { one: 'document', other: 'documents' }) : '') }; }
    var only = ctx.query.only === 'breaking', kind = ctx.saved.kind || null, all = ctx.state === 'empty' ? [] : diffOf(P.tid, from, to), sel = ctx.query.item || null;
    page.appendChild(h('div', { class: 'tpl-pickrow' }, E.Select({ id: 'tpl-cmp-from', label: 'Was (base)', options: list.map(opt), value: from ? from.id : '', onChange: function (id) { E.go(vhref(P.tid, to.id, 'compare', { 'with': id })); } }), E.Select({ id: 'tpl-cmp-to', label: 'Now (compared)', options: list.map(opt), value: to.id, onChange: function (id) { E.go(vhref(P.tid, id, 'compare', from && from.id !== id ? { 'with': from.id } : null)); } }),
      E.Field({ id: 'tpl-cmp-only', label: 'Show', control: h('div', E.Segmented({ id: 'tpl-cmp-only', label: 'Which changes to show', value: only ? 'breaking' : 'all', options: [{ value: 'all', label: 'All changes' }, { value: 'breaking', label: 'Only breaking' }], onChange: function (val) { only = val === 'breaking'; ctx.setQuery({ only: only ? 'breaking' : null }); paint(); } })) })));
    function n(fn) { return all.filter(fn).length; }
    var strip = E.StatStrip({ id: 'tpl-cmp-stat', active: kind, onSelect: function (k) { kind = ctx.saved.kind = k; paint(); }, items: [{ id: 'added', label: 'added', value: n(function (x) { return x.kind === 'added'; }) }, { id: 'changed', label: 'changed', value: n(function (x) { return x.kind === 'changed'; }) }, { id: 'removed', label: 'removed', value: n(function (x) { return x.kind === 'removed'; }) }, { id: 'breaking', label: 'breaking' + (docs ? ' · ' + docs + ' documents on ' + vname(from) : ''), value: n(function (x) { return x.cls === 'Breaking'; }), tone: 'danger', filter: false }] });
    if (ctx.state === 'data' && all.length) page.appendChild(strip);
    var body = h('div', { class: 'stack fill' }); page.appendChild(body);
    function paint() { body.textContent = '';
      var items = all.filter(function (x) { return (!only || x.cls === 'Breaking') && (!kind || x.kind === kind); });
      body.appendChild(E.StateSwitch(ctx.state === 'data' && !all.length ? 'empty' : ctx.state, { loading: { kind: 'table', rows: 8, cols: 3 }, error: { code: 'ECR-TPL-0530', onRetry: function () { ctx.refresh(); } },
        empty: { icon: 'columns', title: from === to || !from ? 'Pick two different versions' : 'No differences', text: from && from !== to ? vname(from) + ' and ' + vname(to) + ' have the same sheets, tables, columns, formulas and rules. Presentation is not compared.' : 'Choose a base version on the left to see what changed.', actions: [{ label: 'Back to ' + vname(to), href: vhref(P.tid, to.id) }] },
        data: function () {
          if (!items.length) return E.EmptyState({ icon: 'filter', title: only ? 'No breaking changes' : 'Nothing matches this filter', text: only ? 'Nothing between ' + vname(from) + ' and ' + vname(to) + ' would make existing data unreachable.' : 'Clear the filter to see all ' + all.length + ' changes.', actions: [{ label: 'Show all changes', onClick: function () { only = false; kind = ctx.saved.kind = null; strip.setActive(null); ctx.setQuery({ only: null }); E.go(vhref(P.tid, to.id, 'compare', { 'with': ctx.query['with'] })); } }] });
          var cur = items.filter(function (x) { return x.id === sel; })[0] || items[0], left = h('div', { id: 'tpl-cmp-list' }), right = h('div', { id: 'tpl-cmp-detail' }), groups = {}, order = [];
          items.forEach(function (x) { var g = [x.sheet, x.table].filter(Boolean).join(' › '); if (!groups[g]) { groups[g] = []; order.push(g); } groups[g].push(x); });
          order.forEach(function (g) { left.appendChild(h('div', { class: 'gh' }, g)); groups[g].forEach(function (x) { left.appendChild(h('button', { class: 'it', type: 'button', id: 'tpl-cmp-' + x.id, 'aria-current': x === cur ? 'true' : null, on: { click: function () { sel = x.id; ctx.setQuery({ item: x.id }); paint(); } } }, icon(x.kind === 'added' ? 'plus' : x.kind === 'removed' ? 'minus' : 'pencil', 14), E.twoLine(x.title, x.type + ' ' + x.kind), classBadge(x.cls))); }); });
          right.appendChild(h('div', { class: 'dt-b' }, h('div', { class: 'row gap-2 wrap' }, h('h2', { class: 't-md t-sb' }, cur.title), classBadge(cur.cls)),
            E.KeyValue([{ label: 'What', value: cur.type + ' ' + cur.kind }, { label: 'Where', value: [cur.sheet, cur.table].filter(Boolean).join(' › ') }, { label: 'Class', value: cur.cls, hint: (function (s) { return s.charAt(0).toUpperCase() + s.slice(1); })(CLASS_HELP[cur.cls].split(' — ')[1]) }]),
            h('div', { class: 'ws' }, h('div', h('span', { class: 'eyebrow' }, 'Was · ' + vname(from)), cur.was ? h('span', { class: 'mono' }, cur.was) : h('span', { class: 'faint' }, 'Did not exist')), icon('arrowR', 16), h('div', h('span', { class: 'eyebrow' }, 'Now · ' + vname(to)), cur.now ? h('span', { class: 'mono' }, cur.now) : h('span', { class: 'faint' }, 'Deleted'))),
            E.Section({ title: 'What it means for existing documents', content: h('p', { class: cur.cls === 'Breaking' ? null : 'muted' }, cur.effect) }),
            cur.cls === 'Breaking' && to.state === 'Draft' ? E.Banner({ tone: 'danger', title: 'Blocks publishing while documents exist', text: 'Revert it in the Publish wizard, or publish with a planned migration.', actions: [{ label: 'Open Publish…', href: vhref(P.tid, to.id, null, { dialog: 'publish-version', step: 'changes' }) }] }) : null,
            h('div', E.Button({ label: 'Open ' + vname(to) + ' in the constructor', icon: 'external', size: 'sm', href: vhref(P.tid, to.id) }))));
          return h('div', { class: 'tpl-cmp' }, left, right); } })); }
    paint();
  } });

  /* ================= 6 + 7. access: period rules & matrix preview ================= */
  var PSTATES = ['Open', 'Grace', 'Closed'], ACTION = { deny: 'Deny edits', mark: 'Allow and mark as late', confirm: 'Allow after confirmation' }, STRICT = { deny: 3, confirm: 2, mark: 1 }, WHEN = { Grace: 'Period is in grace', Closed: 'Period is closed', AfterDay: 'After a day of the next month' };
  function prulesOf(S) {
    if (S.prules) return S.prules; var s = S.sheets, out = [];
    function add(name, sh, tb, col, when, action, on) { if (sh) out.push({ id: 'PR-0' + (out.length + 1), name: name, sheet: sh.id, table: tb ? tb.id : '', column: col || '', when: when, day: 5, action: action, enabled: on !== false }); }
    add('Late entries in air emissions are marked', s[0], null, null, 'Grace', 'mark'); add('Water figures can be corrected after closing', s[1], null, null, 'Closed', 'confirm'); add('GHG inventory is frozen in grace', s[3] || s[s.length - 1], null, null, 'Grace', 'deny');
    add('Air emissions need a confirmation in grace', s[0], null, null, 'Grace', 'confirm'); add('Permit limit is never edited late', s[0], s[0] && s[0].tables[0], 'PERMIT_LIMIT', 'Grace', 'deny'); add('Energy closes on the 5th', s[4], null, null, 'AfterDay', 'deny', false);
    return (S.prules = out);
  }
  function conflictsOf(rules) { var map = {}; rules.forEach(function (a) { rules.forEach(function (b) { if (a !== b && a.enabled && b.enabled && a.sheet === b.sheet && a.table === b.table && a.column === b.column && a.when === b.when && a.action !== b.action) (map[a.id] = map[a.id] || []).push(b.id); }); }); return map; }
  function scopeText(S, r) { var sh = findSheet(S, r.sheet), f = r.table && findTable(S, r.table); return { a: sh ? sh.name.en : '—', b: f ? 'Table ' + f.tb.no + (r.column ? ' › column ' + r.column : '') : 'Whole sheet' }; }
  function accessOf(S, role, sh, ps, rules) {
    if (!D.granted(role, 'Document.View')) return { a: 'Hidden', why: ['Role ' + role + ' has no permission “View documents”.'] };
    if (!sh.enabled) return { a: 'Hidden', why: ['The sheet is not included in this version.'] };
    if (sh.code === 'ENERGY' && (role === 'DataEntry' || role === 'Viewer')) return { a: 'Hidden', why: ['This version restricts the sheet “' + sh.name.en + '” to the energy reporting group.', 'Role ' + role + ' is not in that group, so the sheet tab is not shown at all.'] };
    if (!D.granted(role, 'Document.Edit')) return { a: 'Read-only', why: ['Role ' + role + ' has “View documents” but not “Enter and edit data”.', D.granted(role, 'Document.Approve') ? 'It can approve or reject the sheet, which does not change values.' : 'Period state does not matter for this role.'] };
    var hit = rules.filter(function (r) { return r.enabled && r.sheet === sh.id && !r.table && (r.when === ps || (r.when === 'AfterDay' && ps === 'Grace')); }).sort(function (a, b) { return STRICT[b.action] - STRICT[a.action]; }), top = hit[0];
    var parts = rules.filter(function (r) { return r.enabled && r.sheet === sh.id && r.table && (r.when === ps || (r.when === 'AfterDay' && ps === 'Grace')); }).length;
    if (ps === 'Open') return { a: 'Edit', why: ['Role ' + role + ' has “Enter and edit data”.', 'The period is open, so period rules do not apply.'] };
    if (top && top.action === 'deny') return { a: 'Read-only', rule: top, why: ['Period rule ' + top.id + ' “' + top.name + '” denies edits.', hit.length > 1 ? 'Rules ' + hit.map(function (r) { return r.id; }).join(', ') + ' overlap here — the strictest one wins.' : null].filter(Boolean) };
    if (top) return { a: 'Edit', note: top.action === 'mark' ? 'marked late' : 'after confirmation', rule: top, parts: parts, why: ['Role ' + role + ' has “Enter and edit data”.', 'Period rule ' + top.id + ' “' + top.name + '”: ' + ACTION[top.action].toLowerCase() + '.', hit.length > 1 ? 'Rules ' + hit.map(function (r) { return r.id; }).join(', ') + ' conflict — the strictest one wins until the conflict is resolved.' : null, parts ? F.plural(parts, { one: 'table or column has', other: 'tables or columns have' }) + ' a stricter rule of its own.' : null].filter(Boolean) };
    if (ps === 'Grace') return { a: 'Edit', parts: parts, why: ['Role ' + role + ' has “Enter and edit data”.', 'The period is in grace and no period rule restricts this sheet.'] };
    return { a: 'Read-only', why: ['The period is closed and no period rule allows late edits of this sheet.', 'A period administrator can reopen the period with a reason.'] };
  }
  function accessCell(x, id, onClick) { var cls = x.a === 'Edit' ? '' : x.a === 'Hidden' ? ' hid' : ' ro'; return h('button', { class: 'cl' + cls, type: 'button', id: id || null, 'aria-haspopup': 'dialog', on: { click: onClick || function () { } } }, icon(x.a === 'Edit' ? 'pencil' : x.a === 'Hidden' ? 'eyeOff' : 'eye', 14), x.a, x.note ? h('small', x.note) : null, x.parts ? h('small', '· exceptions') : null); }
  function headCells() { return PSTATES.map(function (ps) { return h('th', { scope: 'col' }, E.StatusBadge('period', ps, { quiet: true })); }); }

  E.screen(VP + '/access-matrix', { title: 'Access matrix', icon: 'grid', navPath: BASE, example: X + '/access-matrix', crumbs: function (ctx) { return crumbsFor(ctx, 'Access matrix'); }, render: function (el, ctx) {
    var P = subPage(el, ctx, { title: 'Access matrix', subtitle: 'A preview of what a role can do on each sheet while the period is open, in grace, or closed. It is the combined result of role permissions, this version’s sheet restrictions and period rules.' }); if (!P) return;
    var S = P.S, page = h('div', { class: 'page' }), role = D.roles.some(function (r) { return r.id === ctx.query.role; }) ? ctx.query.role : 'DataEntry', cells = {}; el.appendChild(page);
    page.appendChild(E.PageHeader(P.head({ id: 'tpl-mx', secondary: [{ label: 'Period rules', icon: 'calendar', href: vhref(P.tid, P.vid, 'period-rules') }, { label: 'Roles and grants', icon: 'shield', href: '#/admin/security?tab=grants' }] })));
    var slot = h('div', { class: 'stack gap-3' });
    page.appendChild(E.StateSwitch(ctx.state === 'data' && !S.sheets.length ? 'empty' : ctx.state, { loading: { kind: 'table', rows: 5, cols: 4 }, error: { code: 'ECR-TPL-0540', onRetry: function () { ctx.refresh(); } }, empty: { icon: 'grid', title: 'No sheets to show', text: 'The matrix has one row per sheet. Add sheets in the constructor first.', actions: [{ label: 'Open the constructor', href: vhref(P.tid, P.vid) }] },
      data: function () { return h('div', { class: 'stack gap-3' }, h('div', { class: 'tpl-pickrow' }, E.Select({ id: 'tpl-mx-role', label: 'See it through the eyes of', options: D.roles.map(function (r) { return { value: r.id, label: r.name }; }), value: role, hint: 'Nothing is changed here — this is a preview.', onChange: function (val) { role = val; ctx.setQuery({ role: val === 'DataEntry' ? null : val }); paint(); } })), slot); } }));
    function whyBody(x, box) { box.appendChild(h('ul', { class: 'conseq' }, x.why.map(function (w) { return h('li', { class: 'note' }, icon('info', 14), h('span', w)); }))); box.appendChild(h('div', { class: 'row gap-2 wrap' }, x.rule ? E.Button({ label: 'Open rule ' + x.rule.id, size: 'sm', href: vhref(P.tid, P.vid, 'period-rules', { panel: x.rule.id }) }) : null, E.Button({ label: 'Grants of ' + role, size: 'sm', variant: 'ghost', href: '#/admin/security?tab=grants' }))); }
    function why(x, sh, ps, anchor) { E.Popover(anchor, { title: x.a + ' · ' + sh.name.en + ' · ' + ps, width: 340, content: function (pop) { whyBody(x, pop); } }); }
    function paint() { slot.textContent = ''; cells = {}; var rules = prulesOf(S), body = h('tbody');
      S.sheets.forEach(function (sh) { var tr = h('tr', h('th', { scope: 'row' }, E.twoLine(sh.name.en, F.plural(sh.tables.length, { one: 'table', other: 'tables' })))); PSTATES.forEach(function (ps) { var x = accessOf(S, role, sh, ps, rules), b = accessCell(x, 'tpl-mx-' + sh.code + '-' + ps, function () { why(x, sh, ps, b); }); b.setAttribute('aria-label', sh.name.en + ', period ' + ps + ': ' + x.a + (x.note ? ', ' + x.note : '') + '. Why?'); cells[sh.code + ps] = { b: b, x: x, sh: sh, ps: ps }; tr.appendChild(h('td', b)); }); body.appendChild(tr); });
      slot.appendChild(h('div', { class: 'tpl-mx-wrap' }, h('table', { class: 'tpl-mx', id: 'tpl-mx-table' }, h('caption', { class: 'sr' }, 'Access of role ' + role + ' by sheet and period state'), h('thead', h('tr', h('th', { scope: 'col' }, 'Sheet'), headCells())), body)));
      slot.appendChild(h('p', { class: 'muted t-xs' }, 'Click a cell to see why. Edit — values can be entered · Read-only — visible, locked · Hidden — the sheet tab is not shown.'));
      var ex = rules.filter(function (r) { return r.table && r.enabled; });
      slot.appendChild(E.Collapsible({ id: 'tpl-mx-ex', title: 'Table and column exceptions', summary: ex.length ? F.plural(ex.length, { one: 'rule', other: 'rules' }) + ' stricter than the sheet' : 'None', content: ex.length ? h('ul', { class: 'tpl-chk' }, ex.map(function (r) { var s = scopeText(S, r); return h('li', icon('calendar', 14), h('div', { class: 'tx' }, h('b', s.a + ' › ' + s.b), h('small', WHEN[r.when] + ' → ' + ACTION[r.action].toLowerCase() + ' · ' + r.name)), E.Button({ label: 'Open', size: 'sm', href: vhref(P.tid, P.vid, 'period-rules', { panel: r.id }) })); })) : h('p', { class: 'muted' }, 'Every table follows its sheet.') })); }
    if (ctx.state === 'data' && S.sheets.length) paint();
    ctx.dialog('cell-reason', function () { var k = Object.keys(cells).filter(function (key) { return cells[key].x.rule && cells[key].ps === 'Grace'; })[0] || Object.keys(cells)[1], c = cells[k]; if (!c) return;
      /* з адреси — діалог (поповер не переживає автоматичного знімка: набір закриває його на resize); з кліку по клітинці — Popover із тим самим вмістом */
      E.Dialog({ id: 'tpl-mx-why', narrow: true, title: c.x.a + ' · ' + c.sh.name.en + ' · ' + c.ps, body: function (b) { b.appendChild(h('p', 'Role ' + role + (c.x.note ? ' — ' + c.x.note : '') + '. Why:')); whyBody(c.x, b); }, footer: [{ label: 'Close', variant: 'primary' }] }); });
  } });

  E.screen(VP + '/period-rules', { title: 'Period access rules', icon: 'calendar', navPath: BASE, example: X + '/period-rules', crumbs: function (ctx) { return crumbsFor(ctx, 'Period rules'); }, render: function (el, ctx) {
    var P = subPage(el, ctx, { title: 'Period access rules', subtitle: 'What happens to data entry when the reporting window is over: deny, allow with a “late” mark, or allow after a confirmation. While the period is open none of these apply.' }); if (!P) return;
    var S = P.S, draft = P.draft, list;
    function rowsNow() { var rules = prulesOf(S), cf = conflictsOf(rules); return rules.map(function (r) { r.conflict = cf[r.id] || null; return r; }); }
    function banner() { var n = rowsNow().filter(function (r) { return r.conflict; }).length; return h('div', { class: 'stack gap-2' }, P.frozenBanner('Period rules'), n && ctx.state === 'data' ? E.Banner({ tone: 'warning', id: 'tpl-pr-conflicts', title: F.plural(n, { one: 'rule conflicts', other: 'rules conflict' }) + ' with another', text: 'They cover the same place and the same moment but ask for different outcomes. Until you resolve it, the strictest one wins.', actions: [{ label: 'Show them', onClick: function () { ctx.saved.stat = 'conflicts'; ctx.refresh(); } }] }) : null); }
    function effect(r) { var sh = findSheet(S, r.sheet); if (!sh) return h('p', { class: 'muted' }, 'Choose a sheet to see the effect.'); var rules = prulesOf(S).filter(function (x) { return x.id !== r.id; }).concat([r]);
      return h('div', { class: 'tpl-mx-wrap' }, h('table', { class: 'tpl-mx mini' }, h('thead', h('tr', h('th', { scope: 'col' }, sh.name.en), headCells())), h('tbody', h('tr', h('th', { scope: 'row' }, r.table ? 'Rest of the sheet' : 'Whole sheet'), PSTATES.map(function (ps) { return h('td', accessCell(accessOf(S, 'DataEntry', sh, ps, rules.filter(function (x) { return !x.table; })))); })),
        r.table ? h('tr', h('th', { scope: 'row' }, scopeText(S, r).b), PSTATES.map(function (ps) { var on = r.enabled && (r.when === ps || (r.when === 'AfterDay' && ps === 'Grace')); return h('td', accessCell(on ? { a: r.action === 'deny' ? 'Read-only' : 'Edit', note: r.action === 'mark' ? 'marked late' : r.action === 'confirm' ? 'after confirmation' : null } : accessOf(S, 'DataEntry', sh, ps, rules.filter(function (x) { return !x.table; })))); })) : null))); }
    function drawerDef(r, isNew) { var ro = !draft, w = JSON.parse(JSON.stringify(r)), fx = h('div', { id: 'tpl-pr-effect', class: 'tpl-fx' });
      function tablesOf(id) { var sh = findSheet(S, id); return [{ value: '', label: 'Whole sheet' }].concat(sh ? sh.tables.slice(0, 14).map(function (tb) { return { value: tb.id, label: tlabel(tb) }; }) : []); }
      function colsOf(id) { var f = id && findTable(S, id); return [{ value: '', label: 'Every column' }].concat(f ? ensure(f.tb).cols.map(function (c) { return { value: c.code, label: cname(c) }; }) : []); }
      function refill(field, opts, val) { var s = field.input; s.textContent = ''; opts.forEach(function (o) { s.appendChild(h('option', { value: o.value }, o.label)); }); s.value = val; }
      function upd() { w.sheet = sheet.value; w.table = table.value; w.column = w.table ? column.value : ''; w.when = when.value; w.day = +day.value || 5; w.action = action.value; w.enabled = on.input.checked; day.hidden = w.when !== 'AfterDay'; column.hidden = !w.table; fx.textContent = ''; fx.appendChild(effect(w)); }
      var name = E.Input({ id: 'tpl-pr-name', label: 'Rule name', required: true, value: w.name, disabled: ro, hint: 'Say the outcome in plain words — it is shown to operators when the rule stops them.' });
      var sheet = E.Select({ id: 'tpl-pr-sheet', label: 'Sheet', options: S.sheets.map(function (s) { return { value: s.id, label: s.name.en }; }), value: w.sheet, disabled: ro, onChange: function (val) { refill(table, tablesOf(val), ''); refill(column, colsOf(''), ''); upd(); } });
      var table = E.Select({ id: 'tpl-pr-table', label: 'Table', options: tablesOf(w.sheet), value: w.table, disabled: ro, onChange: function (val) { refill(column, colsOf(val), ''); upd(); } }), column = E.Select({ id: 'tpl-pr-column', label: 'Column', options: colsOf(w.table), value: w.column, disabled: ro, onChange: upd });
      var when = E.Select({ id: 'tpl-pr-when', label: 'Applies when', options: Object.keys(WHEN).map(function (k) { return { value: k, label: WHEN[k] }; }), value: w.when, disabled: ro, onChange: upd }), day = E.Input({ id: 'tpl-pr-day', label: 'Day of the next month', type: 'number', value: w.day, disabled: ro, onChange: upd });
      var action = E.Select({ id: 'tpl-pr-action', label: 'Outside the window', options: Object.keys(ACTION).map(function (k) { return { value: k, label: ACTION[k] }; }), value: w.action, disabled: ro, hint: 'Mark as late: the edit is accepted and flagged for the approver. After confirmation: the operator states a reason first.', onChange: upd }), on = E.Switch({ id: 'tpl-pr-enabled', label: 'Rule is on', checked: w.enabled, disabled: ro, onChange: upd });
      return { wide: true, title: isNew ? 'New period rule' : r.name, subtitle: isNew ? vname(P.v) : r.id + ' · ' + scopeText(S, r).a, body: function (b) {
          if (ro) b.appendChild(P.frozenBanner('Period rules'));
          if (r.conflict) b.appendChild(E.Banner({ tone: 'warning', id: 'tpl-pr-conflict', title: 'Conflicts with ' + r.conflict.join(', '), text: 'Same place, same moment, different outcome. Change one of them, or turn one off. Until then the strictest outcome applies.', actions: r.conflict.map(function (id) { return { label: 'Open ' + id, href: vhref(P.tid, P.vid, 'period-rules', { panel: id }) }; }) }));
          E.append(b, [name, h('div', { class: 'form-grid' }, sheet, table, column, when, day, h('div', { class: 'span-2' }, action)), on, h('div', { class: 'stack gap-2' }, h('div', h('b', { class: 't-sb' }, 'Effect preview'), h('span', { class: 'muted t-xs' }, ' · for role DataEntry, with this rule as it is set now')), fx)]); upd(); },
        footer: ro ? [{ label: 'Close' }] : [isNew ? null : { label: 'Delete…', icon: 'trash', variant: 'ghost', align: 'left', id: 'tpl-pr-delete', onClick: function () { setTimeout(function () { ctx.openDialog('delete-rule', r); }, 0); } }, { label: 'Cancel', variant: 'ghost' }, { label: isNew ? 'Add rule' : 'Save', variant: 'primary', id: 'tpl-pr-save', onClick: function () {
          if (!name.value.trim()) { name.setError('Name the rule'); name.input.focus(); return false; } if (w.when === 'AfterDay' && !(w.day >= 1 && w.day <= 28)) { day.setError('A day from 1 to 28'); day.input.focus(); return false; }
          upd(); w.name = name.value.trim(); delete w.conflict; Object.assign(r, w); if (isNew) prulesOf(S).push(r); S.saved = snap(S); list.setRows(rowsNow()); var cf = conflictsOf(prulesOf(S))[r.id];
          E.Toast(cf ? 'Saved, but it conflicts with ' + cf.join(', ') : 'Rule “' + r.name + '” ' + (isNew ? 'added' : 'saved') + ' to draft ' + vname(P.v), cf ? { tone: 'warning' } : 'success'); if (cf) setTimeout(function () { ctx.refresh(); }, 0); } }].filter(Boolean) }; }
    list = E.ListPage(el, ctx, { id: 'tpl-pr', rows: ctx.state === 'empty' ? [] : rowsNow(), banner: banner(),
      header: P.head({ primary: draft ? { label: 'New rule', icon: 'plus', id: 'tpl-pr-new', onClick: function () { ctx.openPanel('new'); } } : null, secondary: [{ label: 'Preview in access matrix', icon: 'grid', href: vhref(P.tid, P.vid, 'access-matrix') }] }),
      stats: [{ id: 'all', label: 'rules', value: function (r) { return r.length; } }, { id: 'deny', label: 'deny edits', match: function (r) { return r.action === 'deny'; } }, { id: 'off', label: 'turned off', match: function (r) { return !r.enabled; } }, { id: 'conflicts', label: 'in conflict', tone: 'warning', match: function (r) { return !!r.conflict; } }],
      search: { placeholder: 'Rule, sheet or table', text: function (r) { var s = scopeText(S, r); return r.id + ' ' + r.name + ' ' + s.a + ' ' + s.b; } }, filters: [{ id: 'when', label: 'Moments', options: Object.keys(WHEN).map(function (k) { return { value: k, label: WHEN[k] }; }) }],
      table: { rowKey: 'id', tall: true, rowLabel: function (r) { return 'Open rule ' + r.name; }, columns: [
        { key: 'name', label: 'Rule', render: function (r) { return E.twoLine(r.name, r.id); } }, { key: 'sheet', label: 'Applies to', sortValue: function (r) { return scopeText(S, r).a; }, render: function (r) { var s = scopeText(S, r); return E.twoLine(s.a, s.b, { mono: false }); } },
        { key: 'when', label: 'When', render: function (r) { return r.when === 'AfterDay' ? 'After day ' + r.day + ' of the next month' : WHEN[r.when]; } }, { key: 'action', label: 'Outside the window', render: function (r) { return ACTION[r.action]; } },
        { key: 'conflict', label: 'Conflict', sortable: false, render: function (r) { return r.conflict ? h('span', { class: 'tpl-warn' }, icon('alert', 14), 'With ' + r.conflict.join(', ')) : null; } }, { key: 'enabled', label: 'On', hideSm: true, render: function (r) { return r.enabled ? 'On' : h('span', { class: 'muted' }, 'Off'); } }] },
      empty: { icon: 'calendar', title: 'No period rules', text: 'Without rules the defaults apply: data entry can edit while the period is open or in grace, and nothing after it closes.', actions: draft ? [{ label: 'New rule', variant: 'primary', icon: 'plus', onClick: function () { ctx.openPanel('new'); } }] : [] }, error: { code: 'ECR-TPL-0550' },
      drawer: function (r) { return drawerDef(r, false); } });
    ctx.panel('new', function () { if (!draft || !S.sheets.length) return; E.Drawer(Object.assign({ id: 'tpl-pr-drawer' }, drawerDef({ id: 'PR-0' + (prulesOf(S).length + 1), name: '', sheet: S.sheets[0].id, table: '', column: '', when: 'Grace', day: 5, action: 'mark', enabled: true }, true))); });
    ctx.dialog('delete-rule', function (r) { r = r || prulesOf(S)[0]; if (!draft || !r) return; var s = scopeText(S, r);
      E.ConfirmDialog({ id: 'tpl-pr-del', title: 'Delete period rule “' + r.name + '”?', consequences: [s.a + ' › ' + s.b + ' falls back to the default: ' + (r.when === 'Closed' ? 'no edits after the period closes.' : 'edits are allowed in grace without a mark or a confirmation.'), { text: 'To pause a rule without losing it, turn it off instead.', note: true }], verb: 'Delete rule', icon: 'trash',
        onConfirm: function () { var all = prulesOf(S), at = all.indexOf(r); all.splice(at, 1); S.saved = snap(S); ctx.refresh(); E.Toast('Rule “' + r.name + '” deleted', { duration: 8000, action: { label: 'Undo', onClick: function () { prulesOf(S).splice(at, 0, r); S.saved = snap(S); E.go(vhref(P.tid, P.vid, 'period-rules')); } } }); } }); });
  } });

  /* ================= flows ================= */
  var T1 = '#' + BASE + '/GEN07131100', HX = '#' + X, HP = '#' + XP, N11 = 'node=t:t0-0';
  E.flow('tpl-create', { group: 'Configuration', title: 'Create a template', actor: 'Template administrator', steps: [
    { label: 'Templates', href: '#' + BASE }, { label: 'New template · name and code', href: '#' + BASE + '?dialog=new-template' }, { label: 'Starting point: scratch or copy', href: '#' + BASE + '?dialog=new-template&step=source' }, { label: 'Review', href: '#' + BASE + '?dialog=new-template&step=review' },
    { label: 'Result: created · what next', href: '#' + BASE + '/WTR-QUARTERLY?dialog=result-created' }, { label: 'Empty draft in the constructor', href: '#' + BASE + '/WTR-QUARTERLY/versions/v1?state=empty' }, { label: 'Add the first sheet', href: '#' + BASE + '/WTR-QUARTERLY/versions/v1?state=empty&dialog=add-sheet' }] });
  E.flow('tpl-edit-structure', { group: 'Configuration', title: 'Edit structure: column → formula → rule → preview', actor: 'Template administrator', steps: [
    { label: 'Template · versions', href: T1 }, { label: 'Constructor · table 1.1 · columns', href: HX + '?' + N11 }, { label: 'Add column', href: HX + '?' + N11 + '&panel=c:new' }, { label: 'Edit column “NOₓ”', href: HX + '?' + N11 + '&panel=c:NOX' }, { label: 'Rows: fixed or dynamic', href: HX + '?' + N11 + '&tab=rows' },
    { label: 'Add formula (checked in place)', href: HX + '?' + N11 + '&tab=formulas&panel=f:new' }, { label: 'Add validation rule', href: HX + '?' + N11 + '&tab=rules&panel=r:new' }, { label: 'Preview as the operator', href: HX + '?' + N11 + '&tab=preview' }, { label: 'Add table', href: HX + '?node=s:s0&dialog=add-table' }, { label: 'Leaving with unsaved changes', href: HX + '?dialog=unsaved-guard' }] });
  E.flow('tpl-publish', { group: 'Configuration', title: 'Publish a version: problems → fix → reason → result', actor: 'Template administrator', steps: [
    { label: 'Template · versions', href: T1 }, { label: 'Draft v1.1.0 in the constructor', href: HX }, { label: 'Publish problems (from the tree counter)', href: HX + '?panel=issues' }, { label: 'Publish… · checks fail', href: HX + '?dialog=publish-version' },
    { label: 'Fix 1: units without CONVERT', href: HX + '?' + N11 + '&tab=formulas&panel=f:F2' }, { label: 'Fix 2: cycle in formulas', href: HX + '?node=t:t0-4&tab=formulas&panel=f:F1' }, { label: 'Fix 3: column without an English name', href: HX + '?node=t:t0-1&panel=c:ABATEMENT' },
    { label: 'Publish… · all checks passed', href: HX + '?dialog=publish-version&issues=clean' }, { label: 'Changes: Guarded and Safe', href: HX + '?dialog=publish-version&issues=clean&step=changes' }, { label: 'Reason for change (required)', href: HX + '?dialog=publish-version&issues=clean&step=reason' }, { label: 'Review', href: HX + '?dialog=publish-version&issues=clean&step=review' },
    { label: 'Result: published · what next', href: HX + '?dialog=result-published' }, { label: 'Migrate documents', href: HX + '?dialog=migrate-documents' }, { label: 'Back to the template', href: T1 }] });
  E.flow('tpl-breaking-change', { group: 'Configuration', title: 'Breaking change: blocked, and two ways out', actor: 'Template administrator', steps: [
    { label: 'Delete a column that documents use', href: HX + '?' + N11 + '&dialog=delete-column' }, { label: 'Compare: only breaking', href: HX + '/compare?changes=breaking&only=breaking' }, { label: 'Publish… · blocked at Changes', href: HX + '?dialog=publish-version&issues=clean&changes=breaking&step=changes' },
    { label: 'Way out: publish with a migration', href: HX + '?dialog=publish-version&issues=clean&changes=breaking&migrate=1&step=reason' }, { label: 'Result · migrate documents', href: HX + '?dialog=migrate-documents' }] });
  E.flow('tpl-presentation-after-publish', { group: 'Configuration', title: 'Change the look of a published version', actor: 'Template administrator', steps: [
    { label: 'Published v1.0.0 · structure frozen', href: HP }, { label: 'Column: structure locked, presentation editable', href: HP + '?' + N11 + '&panel=c:NOX' }, { label: 'Result: presentation revision saved', href: HP + '?dialog=result-presentation' }, { label: 'What “presentation revision” means', href: HP + '?node=root' }, { label: 'Need a structural change → clone', href: HP + '?dialog=clone-version' }] });
  E.flow('tpl-compare', { group: 'Configuration', title: 'Compare two versions', actor: 'Template administrator', steps: [
    { label: 'Template · versions', href: T1 }, { label: 'Compare v1.0.0 → v1.1.0', href: HX + '/compare' }, { label: 'Detail of a guarded change', href: HX + '/compare?item=CH-3-c' }, { label: 'Only breaking', href: HX + '/compare?changes=breaking&only=breaking' }, { label: 'Two archived versions', href: '#' + BASE + '/GEN07131100/versions/v2/compare?with=v1' }] });
  E.flow('tpl-delete-with-undo', { group: 'Configuration', title: 'Delete with consequences and Undo', actor: 'Template administrator', steps: [
    { label: 'Table 1.3 in the constructor', href: HX + '?node=t:t0-2' }, { label: 'Delete table · consequences', href: HX + '?node=t:t0-2&dialog=delete-table' }, { label: 'After: sheet view (toast with Undo)', href: HX + '?node=s:s0' }, { label: 'Delete rule', href: HX + '?' + N11 + '&tab=rules&dialog=delete-rule' }, { label: 'Delete row', href: HX + '?' + N11 + '&tab=rows&dialog=delete-row' },
    { label: 'Delete sheet', href: HX + '?node=s:s4&dialog=delete-sheet' }, { label: 'Delete the whole draft', href: HX + '?dialog=delete-draft' }] });
  E.flow('tpl-access-preview', { group: 'Configuration', title: 'Relations, access matrix and period rules', actor: 'Template administrator', steps: [
    { label: 'Table relations · map', href: HX + '/relations' }, { label: 'Edit a relation', href: HX + '/relations?panel=REL-3' }, { label: 'Cycle check fails', href: HX + '/relations?cycle=1&dialog=result-cycle' }, { label: 'Access matrix · DataEntry', href: HX + '/access-matrix' }, { label: 'Through the eyes of Approver', href: HX + '/access-matrix?role=Approver' }, { label: 'Why is this cell read-only?', href: HX + '/access-matrix?dialog=cell-reason' },
    { label: 'Period rules · conflicts highlighted', href: HX + '/period-rules' }, { label: 'Conflicting rule · effect preview', href: HX + '/period-rules?panel=PR-04' }, { label: 'New period rule', href: HX + '/period-rules?panel=new' }] });
})(window.ECR);

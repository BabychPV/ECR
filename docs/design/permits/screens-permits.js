/* =====================================================================
   screens-permits.js — розділ «Permits» (МОКАП, ДО погодження; коду продукту немає).
   Пункт рейки стоїть у групі Configure одразу після Registries (order 2.5).
   Маршрути:
     /admin/permits                       перелік пермітів (ListPage + шторка)
     /admin/permits/:id                   перміт: огляд + по вкладці на кожну таблицю (?tab=overview|<код таблиці>, ?panel=<рядок>)
     /admin/permits/types                 типи пермітів
     /admin/permits/types/:code           конструктор типу (?tab=structure|rules|versions, ?t=<код таблиці>, ?panel=<код стовпця>)
     /admin/permits/bindings              привʼязки пермітів до аркушів
     /admin/permits/bindings/:id          привʼязка: зіставлення стовпців + попередній перегляд аркуша
   Діалоги: new-permit, import-permit, renew-permit, new-type, add-table, add-column, add-series,
            publish-type, new-binding.
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, D = E.data, F = E.fmt, rng = E.rng, icon = E.icon;

  E.css('pm', [
    '.pm-nav{display:flex;gap:var(--s4);border-bottom:1px solid var(--border);flex:none;overflow-x:auto}',
    '.pm-nav a{display:inline-flex;align-items:center;gap:var(--s1);height:32px;color:var(--muted);font-weight:500;text-decoration:none;white-space:nowrap;box-shadow:inset 0 -2px 0 transparent}',
    '.pm-nav a:hover{color:var(--text)}.pm-nav a[aria-current="page"]{color:var(--text);box-shadow:inset 0 -2px 0 var(--accent)}',
    '.pm-slot{display:grid;gap:var(--s2);flex:none}.pm-slot:empty{display:none}',
    '.pm-body{flex:1;min-height:0;overflow:auto;padding:var(--s4) var(--s5);display:flex;flex-direction:column;gap:var(--s3)}',
    '.pm-body>*{flex:none;min-width:0}',
    '.pm-note{color:var(--muted);max-width:760px}',
    '.pm-tw{overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface);max-width:100%;max-height:520px}',
    '.pm-t{border-collapse:separate;border-spacing:0;width:100%}',
    '.pm-t th,.pm-t td{height:var(--row);padding:0 var(--s3);border-bottom:1px solid var(--grid-line);white-space:nowrap;text-align:left;max-width:320px;overflow:hidden;text-overflow:ellipsis}',
    '.pm-t thead th{position:sticky;top:0;z-index:2;background:var(--sunken);font-weight:500;color:var(--muted);font-size:var(--fs-xs);text-transform:uppercase;letter-spacing:.04em;border-bottom:1px solid var(--border-strong)}',
    '.pm-t thead tr.pm-grp th{top:0;height:24px;text-transform:none;letter-spacing:0;border-bottom:1px solid var(--border)}',
    '.pm-t thead tr.pm-cols th{top:24px}',
    '.pm-t .num{text-align:right;font-family:var(--mono);font-variant-numeric:tabular-nums}',
    '.pm-t .pm-first{position:sticky;left:0;z-index:1;background:var(--surface);border-right:1px solid var(--border-strong)}',
    '.pm-t thead .pm-first{z-index:3;background:var(--sunken)}',
    '.pm-t tbody tr[tabindex]{cursor:pointer}.pm-t tbody tr:hover td{background:var(--hover)}.pm-t tbody tr:hover td.pm-first{background:var(--hover)}',
    '.pm-t td.pm-warn{background:var(--warning-soft)}.pm-t td.pm-bad{background:var(--danger-soft)}',
    '.pm-t td.pm-derived{color:var(--muted)}',
    '.pm-t tfoot td{position:sticky;bottom:0;background:var(--sunken);font-weight:500;border-top:1px solid var(--border-strong);border-bottom:0}',
    '.pm-gbtn{display:inline-flex;align-items:center;gap:var(--s1);border:0;background:none;padding:0;font:inherit;color:var(--muted);font-weight:500;border-radius:2px}',
    '.pm-gbtn:hover{color:var(--text)}.pm-gbtn svg{transition:transform var(--t)}.pm-gbtn[aria-expanded="true"] svg{transform:rotate(90deg)}',
    '.pm-tools{display:flex;align-items:center;gap:var(--s2);flex-wrap:wrap;flex:none}.pm-tools .sp{flex:1}',
    '.pm-cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:var(--s3)}',
    '.pm-card{display:grid;gap:var(--s2);padding:var(--s3);border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.pm-card h3{font-size:var(--fs-sm);font-weight:600}',
    '.pm-tl{position:relative;height:28px;margin:var(--s2) 0}',
    '.pm-tl-bar{position:absolute;left:0;right:0;top:12px;height:4px;border-radius:2px;background:var(--border)}',
    '.pm-tl-run{position:absolute;top:12px;height:4px;border-radius:2px;background:var(--accent)}',
    '.pm-tl-now{position:absolute;top:4px;width:2px;height:20px;background:var(--text)}',
    '.pm-tl-lbl{display:flex;justify-content:space-between;font-size:var(--fs-xs);color:var(--muted)}',
    '.pm-tree{display:grid;gap:0;padding:var(--s1);align-content:start}',
    '.pm-map td.pm-first{min-width:200px}.pm-map td{max-width:none}',
    '.pm-node{display:flex;align-items:center;gap:var(--s2);width:100%;min-height:var(--ctl);padding:0 var(--s2);border:0;border-radius:var(--r1);background:transparent;text-align:left;color:var(--text)}',
    '.pm-node:hover{background:var(--hover)}.pm-node[aria-current="true"]{background:var(--accent-soft);color:var(--accent-text);font-weight:500}',
    '.pm-node .n{margin-left:auto;font:400 var(--fs-xs)/1 var(--mono);color:var(--muted)}',
    '.pm-node.sub{padding-left:var(--s5)}',
    '.pm-arrow{display:flex;align-items:center;gap:var(--s2);color:var(--faint)}',
    '.pm-map td.c,.pm-map th.c{text-align:center}',
    '.pm-role{display:inline-flex;align-items:center;gap:var(--s1);font-size:var(--fs-xs);font-weight:500}',
    '.pm-inspect{display:grid;gap:var(--s3);padding:var(--s3)}',
    '.pm-gridnote{display:flex;align-items:center;gap:var(--s2);color:var(--muted);font-size:var(--fs-xs)}',
    '.pm-fieldwrap>.btn{align-self:flex-start}',
    '.pm-two{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:var(--s3)}',
    '@media (max-width:900px){.pm-two{grid-template-columns:1fr}}',
    '@media (max-width:640px){.pm-body{padding:var(--s4)}}'
  ].join('\n'));

  /* ================= helpers ================= */
  function later(fn) { setTimeout(fn, 0); }
  function find(list, key, val) { for (var i = 0; i < list.length; i++) if (list[i][key] === val) return list[i]; return null; }
  function reload(ctx, q) { setTimeout(function () { E.closeAllLayers(); ctx.setQuery(Object.assign({ dialog: null }, q || {})); ctx.refresh(); }, 0); }
  function note(t) { return h('p', { class: 'pm-note' }, t); }
  function chips(list) { return h('span', { class: 'sumline' }, list.map(function (x) { return h('span', { class: 'chip' }, x); })); }
  function inl(n) { return h('div', { class: 'row' }, n); }
  function days(a, b) { return Math.round((new Date(b) - new Date(a)) / 864e5); }
  var TODAY = D.today.slice(0, 10);

  function subnav(cur) {
    var items = [['/admin/permits', 'Permits', 'file'], ['/admin/permits/types', 'Permit types', 'sliders'], ['/admin/permits/lists', 'Lists', 'rows4'], ['/admin/permits/bindings', 'Sheet bindings', 'link']];
    return h('nav', { class: 'pm-nav', 'aria-label': 'Permits sections' }, items.map(function (x) {
      return h('a', { href: E.href(x[0]), 'aria-current': x[0] === cur ? 'page' : null }, icon(x[2], 14), x[1]); }));
  }

  /* ================= модель: типи пермітів ================= */
  var TYPES_LIST = ['Text', 'Localized text', 'Long text', 'Number', 'Date', 'Yes / No', 'Choice', 'Link to registry'];
  function col(code, en, ru, type, o) { return Object.assign({ code: code, en: en, ru: ru || '', type: type, req: false, key: false, unit: null, group: null, ref: null, via: null }, o || {}); }
  var MONTHS_EN = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  var MONTHS_RU = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
  function monthCols(unit, group) { return MONTHS_EN.map(function (m, i) { return col(m.slice(0, 3), m, MONTHS_RU[i], 'Number', { unit: unit, group: group }); }); }
  var VOL = '10³ m³';

  var HEAD = [
    col('Permit_Number', 'Permit number', 'Номер разрешения', 'Text', { req: true, key: true }),
    col('Location', 'Location', 'Объект', 'Link to registry', { req: true, ref: 'Locations' }),
    col('Issue_Date', 'Issue date', 'Дата выдачи', 'Date', { req: true }),
    col('Start_Date', 'Valid from', 'Действует с', 'Date', { req: true, sys: true }),
    col('End_Date', 'Valid to', 'Действует по', 'Date', { req: true, sys: true }),
    col('Actual_End_Date', 'Actual end', 'Фактическая дата окончания', 'Date', { hint: 'Set when the permit ends earlier than planned' }),
    col('Form_300', 'Reported in form 300', '', 'Yes / No', { group: 'Reporting forms' }),
    col('Form_301', 'Reported in form 301', '', 'Yes / No', { group: 'Reporting forms' }),
    col('Form_302', 'Reported in form 302', '', 'Yes / No', { group: 'Reporting forms' }),
    col('TOx', 'Reported in TOx', '', 'Yes / No', { group: 'Reporting forms' })];

  var TYPES = [
    { code: 'AirPermit', name: 'Air emission permit', ru: 'Разрешение на эмиссии в атмосферу', category: 'Air', state: 'Published', version: 3, updated: '2026-09-04', by: 'G. Tulegenova',
      regCode: 'AIR_PERMIT', tables: [
        { code: 'AirEmissions', name: 'Emission limits', ru: 'Лимиты выбросов', regCode: 'AIR_EMISSIONS', cols: [
            col('Substance', 'Substance', 'Вещество', 'Link to registry', { req: true, key: true, ref: 'Substances' }),
            col('Code', 'Code', 'Код', 'Text', { via: 'Substance.Code' }),
            col('Site', 'Site', 'Площадка', 'Link to registry', { req: true, key: true, ref: 'Sites' }),
            col('Emission_t_year', 'Emission', 'Выброс', 'Number', { unit: 't·yr⁻¹', req: true }),
            col('Emission_mg_nm3', 'Concentration', 'Концентрация', 'Number', { unit: 'mg·Nm⁻³' }),
            col('Emission_g_sec', 'Rate', 'Мощность выброса', 'Number', { unit: 'g·s⁻¹' })],
          rules: [{ name: 'Emission is not negative', text: 'ROW.Emission_t_year >= 0', mode: 'Block' }, { name: 'Rate and annual emission agree in sign', text: 'ROW.Emission_g_sec > 0 OR ROW.Emission_t_year < 0.001', mode: 'Warn' }] }],
      rules: [{ name: 'Substance and site are not repeated', text: 'Unique key: Substance + Site', mode: 'Block' }] },
    { code: 'WastePermit', name: 'Waste permit', ru: 'Разрешение на отходы', category: 'Waste', state: 'Published', version: 2, updated: '2026-08-21', by: 'G. Tulegenova',
      regCode: 'WASTE_PERMIT', tables: [
        { code: 'StoragePlaces', name: 'Storage places', ru: 'Места накопления', regCode: 'WASTE_PLACES', cols: [
            col('Platform', 'Platform', 'Площадка', 'Link to registry', { req: true, key: true, ref: 'Platforms' }),
            col('Place', 'Place', 'Место накопления', 'Link to registry', { req: true, key: true, ref: 'WastePlaces' })], rules: [] },
        { code: 'WasteLimits', name: 'Waste limits', ru: 'Лимиты накопления отходов', regCode: 'WASTE_LIMITS', cols: [
            col('StoragePlace', 'Storage place', 'Место накопления', 'Link to registry', { req: true, key: true, ref: 'StoragePlaces (this permit)' }),
            col('Waste', 'Waste type', 'Вид отхода', 'Link to registry', { req: true, key: true, ref: 'WasteCodes' }),
            col('WasteCode', 'Waste code', 'Код отхода', 'Text', { via: 'Waste.Code' }),
            col('Hazard', 'Category', 'Категория', 'Choice', { via: 'Waste.Hazard' }),
            col('Permitted_t_year', 'Permitted', 'Разрешено', 'Number', { unit: 't·yr⁻¹', req: true })],
          rules: [{ name: 'Storage place belongs to this permit', text: 'Link filtered by StoragePlaces of the same permit', mode: 'Block' }] }],
      rules: [] },
    { code: 'WaterDischarge', name: 'Water discharge permit', ru: 'Разрешение на сброс воды', category: 'Water', state: 'Published', version: 4, updated: '2026-09-10', by: 'M. Petrenko',
      regCode: 'WATER_DISCHARGE_PERMIT', tables: [
        { code: 'Discharge', name: 'Discharge volumes', ru: 'Объёмы сброса', regCode: 'WATER_DISCHARGE', cols: [
            col('WaterBody', 'Water body', 'Водный объект', 'Link to registry', { req: true, key: true, ref: 'WaterBodies' }),
            col('Source_code', 'Source code', 'Код источника', 'Text', { group: 'Location of the point' }),
            col('Sea_river_code', 'Sea/river code', 'Код моря-реки', 'Text', { group: 'Location of the point' }),
            col('Quality_code', 'Quality code', 'Код качества', 'Text', { group: 'Location of the point' }),
            col('Distance_mouth_km', 'Distance from mouth', 'Расстояние от устья', 'Text', { group: 'Location of the point', unit: 'km' }),
            col('Transmitting_org', 'Transmitting organisation', 'Передающая организация', 'Text', { group: 'Location of the point' }),
            col('Water_mgmt_site', 'Water management site', 'Водохозяйственный участок', 'Text', { group: 'Location of the point' })]
          .concat([1, 2, 3, 4, 5].map(function (i) { return col('Tributary_' + i, 'Tributary ' + i, 'Приток ' + i, 'Text', { group: 'Tributaries' }); }))
          .concat([col('Sub_Start', 'Row valid from', 'Дата начала (доп.)', 'Date', { key: true, group: 'Dates' }), col('Sub_End', 'Row valid to', 'Дата окончания (доп.)', 'Date', { group: 'Dates' })])
          .concat([col('Norm_clean', 'Normatively clean', 'Нормативно-чистые', 'Number', { unit: VOL, group: 'Contaminated and clean water' }), col('Norm_treated', 'Normatively treated', 'Нормативно-очищенные', 'Number', { unit: VOL, group: 'Contaminated and clean water' }),
            col('Insuff_treated', 'Insufficiently treated', 'Недостаточно очищенные', 'Number', { unit: VOL, group: 'Contaminated and clean water' }), col('No_treatment', 'Without treatment', 'Без очистки', 'Number', { unit: VOL, group: 'Contaminated and clean water' })])
          .concat(monthCols(VOL, 'Estimated volume by month'))
          .concat([col('Annual', 'Annual stated', 'Годовой объём', 'Number', { unit: VOL, req: true })]),
          rules: [{ name: 'Months add up to the annual volume', text: 'ABS(SUM(Jan:Dec) - Annual) <= 0.01 * Annual', mode: 'Warn' }, { name: 'Row period lies inside the permit', text: 'Sub_Start >= THIS.Start_Date AND Sub_End < THIS.End_Date', mode: 'Block' }] }],
      rules: [] },
    { code: 'WaterIntake', name: 'Water intake permit', ru: 'Разрешение на водозабор', category: 'Water', state: 'Draft', version: 2, updated: '2026-09-16', by: 'M. Petrenko',
      regCode: 'WATER_INTAKE_PERMIT', tables: [
        { code: 'Intake', name: 'Intake volumes', ru: 'Объёмы водозабора', regCode: 'WATER_INTAKE', cols: [
            col('WaterBody', 'Water body', 'Водный объект', 'Link to registry', { req: true, key: true, ref: 'WaterBodies' }),
            col('Source_code', 'Source code', 'Код источника', 'Text', { group: 'Location of the point' }),
            col('Sea_river_code', 'Sea/river code', 'Код моря-реки', 'Text', { group: 'Location of the point' }),
            col('Quality_code', 'Quality code', 'Код качества', 'Text', { group: 'Location of the point' }),
            col('Distance_mouth_km', 'Distance from mouth', 'Расстояние от устья', 'Text', { group: 'Location of the point', unit: 'km' }),
            col('Reliability_50', 'Reliability 50 %', 'Обеспеченность 50 %', 'Text', { group: 'Reliability of annual volumes' }), col('Reliability_75', 'Reliability 75 %', 'Обеспеченность 75 %', 'Text', { group: 'Reliability of annual volumes' }), col('Reliability_95', 'Reliability 95 %', 'Обеспеченность 95 %', 'Text', { group: 'Reliability of annual volumes' }),
            col('Usage_type', 'Type of use', 'Вид использования', 'Choice', { key: true, group: 'Use' }), col('Usage_vol', 'Volume for this use', 'Объём по виду использования', 'Number', { unit: VOL, group: 'Use' })]
          .concat(monthCols(VOL, 'Estimated volume by month'))
          .concat([col('Annual', 'Annual stated', 'Расчётный годовой объём', 'Number', { unit: VOL, req: true })]),
          rules: [{ name: 'Months add up to the annual volume', text: 'ABS(SUM(Jan:Dec) - Annual) <= 0.01 * Annual', mode: 'Warn' }] }],
      rules: [] },
    { code: 'WaterLimits', name: 'Wastewater emission limits', ru: 'Лимиты сброса веществ в воду', category: 'Water', state: 'Published', version: 1, updated: '2026-07-30', by: 'M. Petrenko',
      regCode: 'WATER_LIMITS_PERMIT', tables: [
        { code: 'Limits', name: 'Limits by substance', ru: 'Лимиты по веществам', regCode: 'WATER_LIMITS', cols: [
            col('Substance', 'Substance', 'Вещество', 'Link to registry', { req: true, key: true, ref: 'WaterSubstances' }),
            col('Outlet', 'Outlet (issue number)', 'Выпуск', 'Link to registry', { req: true, key: true, ref: 'IssueNumbers' }),
            col('Formula', 'Formula', 'Формула', 'Text', { via: 'Substance.Formula' }),
            col('Permissible_mg_dm3', 'Permissible concentration', 'Допустимая концентрация', 'Number', { unit: 'mg·dm⁻³', req: true }),
            col('Discharge_g_h', 'Discharge', 'Сброс', 'Number', { unit: 'g·h⁻¹' }),
            col('Discharge_t_y', 'Discharge', 'Сброс', 'Number', { unit: 't·yr⁻¹' }),
            col('Flow_m3_h', 'Wastewater flow', 'Расход сточных вод', 'Number', { unit: 'm³·h⁻¹' }),
            col('Flow_m3_y', 'Wastewater flow', 'Расход сточных вод', 'Number', { unit: 'm³·yr⁻¹' })],
          rules: [{ name: 'Discharge ≈ concentration × flow', text: 'ABS(ROW.Discharge_g_h - ROW.Permissible_mg_dm3 * ROW.Flow_m3_h) <= 0.02 * ROW.Discharge_g_h', mode: 'Warn' }] }],
      rules: [] }];
  function type(code) { return find(TYPES, 'code', code) || TYPES[0]; }
  function table(t, code) { return find(t.tables, 'code', code) || t.tables[0]; }
  /* Варіанти пікерів (демо): у застосунку їх дають реєстри з колонки «Pick from». */
  var LISTS = { Substances: ['Vinyl chloride', 'Nitrogen dioxide', 'Suspended substances', 'Iron oxide', 'Xylene', 'Soot', 'Sulphur dioxide', 'Hydrogen sulphide'], Sites: ['Offshore complex expansion to 450 kbpd', 'Hangar at the process support site'], WaterBodies: ['Caspian Sea (north)', 'Ural River', 'Kigach River'], Platforms: ['Bolashak OPF (LPG)', 'Kashagan Field Facilities'], WastePlaces: ['Temporary storage site No. 1'], WasteCodes: ['20 03 01 · Domestic waste', '08 01 11* · Paint residues', '17 04 05 · Iron and steel'], WaterSubstances: ['Ammonia nitrogen', 'BOD5', 'Chlorides', 'COD', 'Nitrates', 'Sulphates', 'Total iron'], IssueNumbers: ['Outlet 1', 'Outlet 2', 'Outlet 3'], Locations: ['Central processing facility', 'Offshore complex', 'Upstream well pads'] };
  function optsFor(c) {
    if (c.type === 'Choice') return [''].concat(choicesOf(c));
    var ref = (c.ref || '').split(' ')[0];
    if (ref === 'StoragePlaces') return [''].concat(PLACE_ROWS.map(function (x) { return x.Place + ' · ' + x.Platform; }));
    return [''].concat(LISTS[ref] || ['Item 1', 'Item 2']);
  }
  /* Три області списків вибору: спільний реєстр / список лише цього типу / таблиця того самого перміта. */
  function scopeOf(c) { return c.type === 'Choice' ? 'type' : ((c.ref || '').indexOf('this permit') >= 0 ? 'permit' : 'shared'); }
  function choicesOf(c) { return c.choices || { Usage_type: ['ПР – production', 'ХБ – household', 'ПТ – irrigation'], Hazard: ['Hazardous', 'Non-hazardous'] }[c.code] || ['Option 1', 'Option 2']; }
  var SCOPE_LABEL = { shared: 'Shared registry', type: 'This permit type only', permit: 'Table of the same permit' };
  /** Діалог «додати значення» — той самий і з форми рядка, і зі сторінки Lists. */
  function valueDialog(title, hint, onAdd) {
    var name = '', fld;
    E.Dialog({ id: 'pm-addval', title: title, narrow: true,
      body: function (b) { fld = E.Input({ id: 'pm-addval-v', label: 'New value', required: true, onInput: function (v) { name = v; } }); b.appendChild(fld); b.appendChild(note(hint)); },
      footer: [{ label: 'Cancel' }, { label: 'Add value', variant: 'primary', icon: 'plus', keepOpen: true, onClick: function (d) { if (!name.trim()) { fld.setError('Enter a value'); fld.input.focus(); return false; } onAdd(name.trim()); d.close(); E.Toast('Value added', 'success'); } }] });
  }
  function listUsage(ref) {
    var out = []; TYPES.forEach(function (t) { t.tables.forEach(function (tb) { tb.cols.forEach(function (c) { if (c.type === 'Link to registry' && (c.ref || '').split(' ')[0] === ref) out.push(t.name + ' › ' + tb.name + ' › ' + c.en); }); }); }); return out;
  }
  function listRows() {
    var rows = Object.keys(LISTS).map(function (k) { return { id: 'shared-' + k, name: k, scope: 'shared', owner: 'Registries', entries: LISTS[k].length, used: listUsage(k), ref: k }; });
    TYPES.forEach(function (t) { t.tables.forEach(function (tb) { tb.cols.forEach(function (c) {
      if (c.via) return;
      if (c.type === 'Choice') rows.push({ id: 'type-' + t.code + '-' + c.code, name: c.en, scope: 'type', owner: t.name, entries: choicesOf(c).length, used: [t.name + ' › ' + tb.name + ' › ' + c.en], col: c, t: t });
      else if (scopeOf(c) === 'permit') rows.push({ id: 'permit-' + t.code + '-' + c.code, name: c.en, scope: 'permit', owner: t.name, entries: 0, used: [t.name + ' › ' + tb.name + ' › ' + c.en], col: c, t: t });
    }); }); });
    return rows;
  }

  /* ================= демо-дані: екземпляри ================= */
  var AIR_SUB = { 827: ['Vinyl chloride', 'Винилхлорид'], 301: ['Nitrogen dioxide', 'Азота диоксид'], 2902: ['Suspended substances', 'Взвешенные частицы'], 123: ['Iron oxide', 'Железа оксид'], 616: ['Xylene', 'Ксилол'], 143: ['Manganese and its compounds', 'Марганец и его соединения'], 2735: ['Mineral petroleum oil', 'Масло минеральное нефтяное'], 501: ['Pentylenes', 'Пентилены (амилены)'], 2908: ['Inorganic dust, SiO2 70–20 %', 'Пыль неорганическая 70–20 % SiO2'], 328: ['Soot', 'Сажа'], 330: ['Sulphur dioxide', 'Сера диоксид'] };
  var S1 = 'Offshore complex expansion to 450 kbpd', S2 = 'Hangar at the process support site';
  var AIR_ROWS = [[827, S1, 1.76E-05, 0, 0.0002], [301, S1, 0.6669955, 9363.267, 0.4566289], [301, S2, 0.0004104, 75435.958, 0.00684], [2902, S1, 0.0107919, 0, 0.0406], [123, S1, 0.0430369, 0, 0.0651], [616, S1, 0.2382675, 3805.42, 0.1603979], [143, S1, 0.00193444, 0, 0.001908], [2735, S2, 8.7E-07, 299.653, 0.000208], [2735, S1, 0.1396866, 136.104, 0.0077681], [501, S1, 0.0166676, 3319.622, 0.0445481], [2908, S1, 2.7193824, 0, 1.017887], [2908, S2, 0.0031608, 0, 0.042009], [328, S1, 0.0457461, 764.811, 0.03158], [328, S2, 3.75E-05, 6892.906, 0.000625], [330, S2, 0.000882, 162121.138, 0.0147], [330, S1, 0.0947222, 1750.735, 0.0766156]].map(function (r) {
    return { id: r[0] + '|' + r[1], Substance: AIR_SUB[r[0]][0], Substance_ru: AIR_SUB[r[0]][1], Code: String(r[0]), Site: r[1], Emission_t_year: r[2], Emission_mg_nm3: r[3], Emission_g_sec: r[4] }; });
  var WASTE_ROWS = [{ id: 'w1', StoragePlace: 'Temporary storage site No. 1 · Bolashak OPF (LPG)', Waste: 'Domestic waste', WasteCode: '20 03 01', Hazard: '', Permitted_t_year: 0.2271 },
    { id: 'w2', StoragePlace: 'Temporary storage site No. 1 · Kashagan Field Facilities', Waste: 'Paint residues', WasteCode: '08 01 11*', Hazard: 'Hazardous', Permitted_t_year: 3.811 }];
  var PLACE_ROWS = [{ id: 'sp1', Platform: 'Bolashak OPF (LPG)', Place: 'Temporary storage site No. 1' }, { id: 'sp2', Platform: 'Kashagan Field Facilities', Place: 'Temporary storage site No. 1' }];
  var MONTH_V = [16.333, 14.753, 16.333, 15.807, 16.333, 15.807, 16.333, 16.333, 15.807, 16.333, 15.807, 16.333];
  function monthRow(id, wb, vals, annual, extra) { var r = Object.assign({ id: id, WaterBody: wb, Annual: annual }, extra || {}); MONTHS_EN.forEach(function (m, i) { r[m.slice(0, 3)] = vals[i]; }); return r; }
  var DISCH_ROWS = [
    monthRow('d1', 'Caspian Sea (north)', MONTH_V, 192.312, { Source_code: 'sea-10', Sea_river_code: '–', Quality_code: 'МР', Distance_mouth_km: '–', Transmitting_org: '–', Water_mgmt_site: '–', Tributary_1: '–', Tributary_2: '–', Tributary_3: '–', Tributary_4: '–', Tributary_5: '–', Sub_Start: '2026-01-01', Sub_End: '2026-04-05', Norm_clean: 192.312, Norm_treated: 193.312, Insuff_treated: 0, No_treatment: 0 }),
    monthRow('d2', 'Caspian Sea (north)', MONTH_V.map(function (v) { return +(v * 0.5).toFixed(3); }), 96.156, { Source_code: 'sea-10', Sea_river_code: '–', Quality_code: 'МР', Distance_mouth_km: '–', Sub_Start: '2026-04-06', Sub_End: '2026-12-31', Norm_clean: 96.156, Norm_treated: 0, Insuff_treated: 0, No_treatment: 0 })];
  var IN_M = [0.012, 0.011, 0.012, 0.012, 0.012, 0.012, 0.012, 0.012, 0.012, 0.012, 0.012, 0.012];
  var INTAKE_ROWS = [
    monthRow('i1', 'Caspian Sea (north)', IN_M, 0.14, { Source_code: 'sea -10', Sea_river_code: '–', Quality_code: 'МР', Distance_mouth_km: '–', Usage_type: 'ПР – production', Usage_vol: 0.14 }),
    monthRow('i2', 'Caspian Sea (north)', IN_M.map(function (v) { return +(v * 2268 / 1).toFixed(3) / 10 }).map(function (v, i) { return [27.222, 24.587, 27.222, 26.344, 27.222, 26.344, 27.222, 27.222, 26.344, 27.222, 26.344, 27.222][i]; }), 320.52, { Source_code: 'sea -10', Sea_river_code: '–', Quality_code: 'МР', Distance_mouth_km: '–', Usage_type: 'ПР – production', Usage_vol: 320.52 }),
    monthRow('i3', 'Caspian Sea (north)', [47.606, 42.999, 47.606, 46.07, 47.606, 46.07, 47.606, 47.606, 46.07, 47.606, 46.07, 47.606], 560.52, { Source_code: 'sea-10', Sea_river_code: '–', Quality_code: 'МР', Distance_mouth_km: '–', Usage_type: 'ПР – production', Usage_vol: 560.52 })];
  var LIM = [['Ammonia nitrogen', 'Аммоний солевой', 1.8, 82.49, 0.7227, 45.83, 401.5, 'I1'], ['BOD5', 'БПК5', 20, 916.6, 8.03, 45.83, 401.5, 'I1'], ['Chlorides', 'Хлориды', 460, 21081.8, 184.69, 45.83, 401.5, 'I1'], ['COD', 'ХПК', 94, 4308.02, 37.741, 45.83, 401.5, 'I1'], ['Hydrogen sulphide', 'Сероводород', 1.5, 337.005, 1.7232, 224.67, 1148.8, 'I2'], ['Hydrogen sulphide', 'Сероводород', 10, 2.917, 0.02575, 0.292, 2.5745, 'I3'], ['Methanol', 'Метанол', 3, 674.01, 3.4464, 224.67, 1148.8, 'I2'], ['Nitrates', 'Нитраты', 32.12, 3666.4, 32.12, 45.83, 401.5, 'I1'], ['Nitrites', 'Нитриты', 3, 137.49, 1.2045, 45.83, 401.5, 'I1'], ['Petroleum products', 'Нефтепродукты', 3, 674.01, 3.4464, 224.67, 1148.8, 'I2'], ['Phosphates', 'Фосфаты', 9, 412.47, 3.6135, 45.83, 401.5, 'I1'], ['Sulphates', 'Сульфаты', 450, 20623.5, 180.675, 45.83, 401.5, 'I1'], ['Suspended solids', 'Взвешенные вещества', 41.4, 9301.338, 47.56032, 224.67, 1148.8, 'I2'], ['Total iron', 'Железо общее', 2.2, 494.274, 2.52736, 224.67, 1148.8, 'I2']].map(function (r, i) {
    return { id: 'l' + i, Substance: r[0], Outlet: 'Outlet ' + r[7].slice(1), Formula: '', Permissible_mg_dm3: r[2], Discharge_g_h: r[3], Discharge_t_y: r[4], Flow_m3_h: r[5], Flow_m3_y: r[6] }; });
  LIM[7].Formula = 'NO₃'; LIM[8].Formula = 'NO₂'; LIM[10].Formula = 'PO₄'; LIM[11].Formula = 'SO₄';

  var PERMITS = [
    { id: 'P-0001', number: 'KZ06VCZ14825472', type: 'AirPermit', location: 'Offshore complex', issue: '2026-04-29', start: '2026-04-29', end: '2026-12-31', actualEnd: '2026-12-31', state: 'Active', forms: ['301', 'TOx'], rows: { AirEmissions: AIR_ROWS }, total: { AirEmissions: [43, 6.64696] }, bindings: ['B-01', 'B-05'], updated: '2026-09-12', by: 'G. Tulegenova' },
    { id: 'P-0002', number: 'KZ06VCZ14825472', type: 'WastePermit', location: 'Offshore complex', issue: '2026-04-29', start: '2026-04-29', end: '2026-12-31', actualEnd: '', state: 'Active', forms: [], rows: { StoragePlaces: PLACE_ROWS, WasteLimits: WASTE_ROWS }, total: { WasteLimits: [2, 4.0381] }, bindings: ['B-02'], updated: '2026-09-02', by: 'G. Tulegenova' },
    { id: 'P-0003', number: 'KZ00VTE00334648', type: 'WaterDischarge', location: 'Central processing facility', issue: '2025-12-26', start: '2026-01-01', end: '2026-12-31', actualEnd: '2026-04-05', state: 'Expired', forms: [], rows: { Discharge: DISCH_ROWS }, total: { Discharge: [2, 288.468] }, bindings: ['B-03'], updated: '2026-09-10', by: 'M. Petrenko' },
    { id: 'P-0004', number: 'KZ89VTE00309960', type: 'WaterIntake', location: 'Central processing facility', issue: '2025-05-27', start: '2026-01-01', end: '2026-12-31', actualEnd: '', state: 'Draft', forms: [], rows: { Intake: INTAKE_ROWS }, total: { Intake: [3, 881.18] }, bindings: [], updated: '2026-09-16', by: 'M. Petrenko' },
    { id: 'P-0005', number: 'KZ87VCZ14622157', type: 'WaterLimits', location: 'Central processing facility', issue: '2026-01-14', start: '2026-01-01', end: '2026-11-30', actualEnd: '', state: 'Active', forms: ['300'], rows: { Limits: LIM }, total: { Limits: [26, 261.2] }, bindings: [], updated: '2026-07-30', by: 'M. Petrenko' },
    { id: 'P-0006', number: 'KZ79UKR00003155_2023-2030', type: 'AirPermit', location: 'Upstream well pads', issue: '2023-01-10', start: '2023-01-01', end: '2030-12-31', actualEnd: '', state: 'Active', forms: ['301'], rows: {}, total: { AirEmissions: [24, 112.4] }, bindings: ['B-01'], updated: '2026-06-18', by: 'G. Tulegenova' },
    { id: 'P-0007', number: 'KZ14VCZ14523270_2026', type: 'AirPermit', location: 'Central processing facility', issue: '2026-01-12', start: '2026-01-01', end: '2026-12-31', actualEnd: '', state: 'Active', forms: ['301', 'TOx'], rows: {}, total: { AirEmissions: [65, 208.9] }, bindings: ['B-01'], updated: '2026-08-05', by: 'G. Tulegenova' },
    { id: 'P-0008', number: 'No Permit – Vessels', type: 'AirPermit', location: 'Marine vessels', issue: '', start: '2026-01-01', end: '2026-12-31', actualEnd: '', state: 'Active', pseudo: true, forms: [], rows: {}, total: { AirEmissions: [10, 0] }, bindings: ['B-01'], updated: '2026-03-03', by: 'G. Tulegenova' },
    { id: 'P-0009', number: 'KZ50VCZ14912853', type: 'AirPermit', location: 'Central processing facility', issue: '2025-02-11', start: '2025-01-01', end: '2025-12-31', actualEnd: '', state: 'Expired', forms: ['301'], rows: {}, total: { AirEmissions: [64, 190.2] }, bindings: [], updated: '2026-01-09', by: 'G. Tulegenova' }];
  function permit(id) { return find(PERMITS, 'id', id) || PERMITS[0]; }
  function nRows(p) { var n = 0; Object.keys(p.total || {}).forEach(function (k) { n += p.total[k][0]; }); return n; }
  /** Рядки таблиці: явні (з джерел) або детерміновано згенеровані за описом стовпців. */
  function rowsOf(p, tbl) {
    if (p.rows[tbl.code]) return p.rows[tbl.code];
    var R = rng(p.id.length * 31 + tbl.code.length * 7 + p.number.length), n = 9;
    return (p.rows[tbl.code] = Array.apply(null, Array(n)).map(function (_, i) {
      var r = { id: tbl.code + i };
      tbl.cols.forEach(function (c) { r[c.code] = c.type === 'Number' ? +(R() * 40).toFixed(3) : c.type === 'Date' ? '2026-01-01' : c.type === 'Link to registry' ? c.code + ' ' + (i % 5 + 1) : c.type === 'Yes / No' ? R() > .5 : 'Item ' + (i + 1); });
      var air = AIR_ROWS[(i + p.id.length * 3) % AIR_ROWS.length]; if (tbl.code === 'AirEmissions') Object.assign(r, { Substance: air.Substance, Code: air.Code, Site: air.Site }); return r; }));
  }
  function stateOf(p) { return p.state; }
  function stateBadge(p) { var m = { Active: ['version', 'Published', 'Active'], Draft: ['version', 'Draft', 'Draft'], Expired: ['version', 'Archived', 'Expired'] }[p.state]; return E.StatusBadge(m[0], m[1], { label: m[2], quiet: true }); }
  function validity(p) { var d = days(TODAY, p.end); return p.state === 'Active' && d <= 90 && d >= 0 ? F.date(p.start) + ' – ' + F.date(p.end) : F.date(p.start) + ' – ' + F.date(p.end); }
  function expiring(p) { var d = days(TODAY, p.end); return p.state === 'Active' && d >= 0 && d <= 120; }

  /* ================= таблиця, що малюється за ОПИСОМ стовпців ================= */
  function fmtCell(c, v) {
    if (v == null || v === '') return null;
    if (c.type === 'Number') return F.number(v, Math.abs(v) < 1 && v !== 0 ? 5 : 3);
    if (c.type === 'Date') return F.date(v);
    if (c.type === 'Yes / No') return F.bool(v);
    return String(v);
  }
  /** Стовпці довільної кількості: групи згортаються до одного «Σ» (числові) — так 40 стовпців не лякають. */
  function DefTable(tbl, rows, o) {
    o = o || {};
    var collapsed = {}; (o.collapse || []).forEach(function (g) { collapsed[g] = true; });
    var wrap = h('div', { class: 'pm-tw', id: o.id || null, tabindex: '-1' });
    function groupsOf() { var out = [], seen = {}; tbl.cols.forEach(function (c) { if (c.group && !seen[c.group]) { seen[c.group] = 1; out.push(c.group); } }); return out; }
    function render() {
      wrap.textContent = '';
      var vis = [], hasGroups = groupsOf().length > 0;
      tbl.cols.forEach(function (c) {
        if (c.group && collapsed[c.group]) { if (!vis.some(function (v) { return v.sum === c.group; })) vis.push({ sum: c.group, cols: tbl.cols.filter(function (x) { return x.group === c.group; }) }); }
        else vis.push({ c: c });
      });
      var gtr = null;
      if (hasGroups) {
        var cells = [], i = 0;
        while (i < vis.length) {
          var v = vis[i], g = v.sum || (v.c && v.c.group);
          if (!g) { cells.push(h('th', { class: i === 0 ? 'pm-first' : null })); i++; continue; }
          var j = i; while (j < vis.length && (vis[j].sum || (vis[j].c && vis[j].c.group)) === g) j++;
          var open = !collapsed[g], nc = v.sum ? v.cols.length : vis.slice(i, j).length;
          cells.push(h('th', { colspan: String(j - i), scope: 'colgroup' }, h('button', { type: 'button', class: 'pm-gbtn', 'aria-expanded': String(open), 'aria-label': (open ? 'Collapse ' : 'Expand ') + g, on: { click: (function (gg) { return function () { collapsed[gg] = !collapsed[gg]; render(); }; })(g) } }, icon('chevR', 12), g, h('span', { class: 'faint' }, open ? '' : '· ' + nc + ' columns'))));
          i = j;
        }
        gtr = h('tr', { class: 'pm-grp' }, cells);
      }
      var head = h('tr', { class: 'pm-cols' }, vis.map(function (v, i) {
        if (v.sum) return h('th', { class: 'num', scope: 'col' }, (v.cols[0].type === 'Number' ? 'Σ ' : '') + v.cols.length + ' cols');
        var c = v.c; return h('th', { class: (c.type === 'Number' ? 'num ' : '') + (i === 0 ? 'pm-first' : ''), scope: 'col', title: c.code + (c.ru ? ' · ' + c.ru : '') }, c.en, c.unit ? h('span', { class: 'faint' }, ' ' + c.unit) : null, c.req ? h('span', { class: 'danger-text', 'aria-hidden': 'true' }, ' *') : null); }));
      var body = rows.map(function (r) {
        var tr = h('tr', { tabindex: o.onRow ? '0' : null, 'aria-label': o.onRow ? (r[tbl.cols[0].code] || r.id) : null }, vis.map(function (v, i) {
          if (v.sum) { var s = v.cols.reduce(function (a, c) { return a + (typeof r[c.code] === 'number' ? r[c.code] : 0); }, 0); return h('td', { class: 'num' }, v.cols[0].type === 'Number' ? F.number(s, 3) : h('span', { class: 'faint' }, '…')); }
          var c = v.c, val = r[c.code], bad = c.req && (val == null || val === ''), cls = [c.type === 'Number' ? 'num' : '', i === 0 ? 'pm-first' : '', c.via ? 'pm-derived' : '', bad ? 'pm-bad' : '', o.warnCell && o.warnCell(r, c) ? 'pm-warn' : ''].join(' ');
          return h('td', { class: cls, title: bad ? 'Required' : (c.via ? 'Read from the linked record' : null) }, bad ? h('span', { class: 'danger-text' }, 'Required') : (fmtCell(c, val) == null ? h('span', { class: 'faint' }, '—') : fmtCell(c, val)));
        }));
        if (o.onRow) { tr.addEventListener('click', function () { o.onRow(r); }); tr.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); o.onRow(r); } }); }
        return tr;
      });
      var foot = null;
      if (o.totals) foot = h('tfoot', {}, h('tr', {}, vis.map(function (v, i) {
        if (v.sum) return h('td', { class: 'num' });
        var c = v.c; if (i === 0) return h('td', { class: 'pm-first' }, o.totals.label);
        var t = o.totals.sums && o.totals.sums[c.code]; return h('td', { class: 'num' }, t != null ? F.number(t, 5) : null); })));
      wrap.appendChild(h('table', { class: 'pm-t' }, o.caption ? h('caption', { class: 'sr' }, o.caption) : null, h('thead', {}, gtr, head), h('tbody', {}, body), foot));
    }
    render();
    wrap.setCollapsed = function (on) { groupsOf().forEach(function (g) { collapsed[g] = on && tbl.cols.filter(function (c) { return c.group === g; }).length >= 5; }); render(); };
    wrap.hasGroups = groupsOf().length > 0;
    return wrap;
  }

  /* ================= 1. Перелік пермітів ================= */
  E.screen('/admin/permits', { title: 'Permits', group: 'Configure', icon: 'book', order: 2.5, render: function (el, ctx) {
    var slot = h('div', { class: 'pm-slot', id: 'pm-result' });
    var rows = ctx.state === 'empty' ? [] : PERMITS;
    E.ListPage(el, ctx, { id: 'pm', rows: rows, banner: h('div', { class: 'pm-slot' }, subnav('/admin/permits'), slot),
      header: { title: 'Permits', back: false,
        subtitle: 'Environmental permits and everything inside them — limits, volumes, storage places. A sheet can read its permit directly, so limits are never retyped.',
        primary: { label: 'New permit', icon: 'plus', id: 'pm-new', onClick: function () { ctx.openDialog('new-permit'); } },
        secondary: [{ label: 'Import from Excel', icon: 'upload', onClick: function () { ctx.openDialog('import-permit'); } }],
        more: [{ label: 'Export all permits', icon: 'download', onClick: function () { E.Toast('Export started — it continues in My tasks'); } }] },
      stats: [
        { id: 'all', label: 'permits', value: function (r) { return r.length; } },
        { id: 'active', label: 'active', match: function (p) { return p.state === 'Active'; } },
        { id: 'exp', label: 'ending within 120 days', tone: 'warning', match: expiring },
        { id: 'free', label: 'not read by any sheet', match: function (p) { return p.state !== 'Expired' && !p.bindings.length; } }],
      search: { placeholder: 'Permit number or location', text: function (p) { return p.number + ' ' + p.location + ' ' + type(p.type).name; } },
      filters: [{ id: 'type', label: 'Types', options: TYPES.map(function (t) { return t.name; }), match: function (p, v) { return type(p.type).name === v; } }, { id: 'state', label: 'States', options: ['Active', 'Draft', 'Expired'] }],
      table: { rowKey: 'id', tall: true, rowLabel: function (p) { return p.number; }, columns: [
        { key: 'number', label: 'Permit', render: function (p) { return E.twoLine(p.number, p.pseudo ? 'Placeholder for emissions without a permit' : p.location, { mono: false }); } },
        { key: 'type', label: 'Type', render: function (p) { var t = type(p.type); return E.twoLine(t.name, t.category, { mono: false }); }, sortValue: function (p) { return type(p.type).name; } },
        { key: 'start', label: 'Validity', render: function (p) { return E.twoLine(F.date(p.start) + ' – ' + F.date(p.end), expiring(p) ? 'Ends in ' + days(TODAY, p.end) + ' days' : (p.actualEnd && p.actualEnd !== p.end ? 'Ended early ' + F.date(p.actualEnd) : ''), { mono: false }); } },
        { key: 'rows', label: 'Rows', num: true, hideSm: true, sortValue: nRows, render: function (p) { return F.int(nRows(p)) + ' in ' + type(p.type).tables.length + (type(p.type).tables.length > 1 ? ' tables' : ' table'); } },
        { key: 'bindings', label: 'Read by', hideSm: true, sortValue: function (p) { return p.bindings.length; }, render: function (p) { return p.bindings.length ? F.plural(p.bindings.length, { one: 'sheet table', other: 'sheet tables' }) : null; } },
        { key: 'state', label: 'State', render: stateBadge }] },
      empty: { icon: 'book', title: 'No permits yet', text: 'Add the first permit by hand or import the yearly Excel report your team keeps today.', actions: [{ label: 'New permit', variant: 'primary', icon: 'plus', onClick: function () { ctx.openDialog('new-permit'); } }, { label: 'Import from Excel', onClick: function () { ctx.openDialog('import-permit'); } }] },
      error: { code: 'ECR-PRM-0503' },
      onRow: function (p) { E.go('/admin/permits/' + p.id); }
    });

    ctx.dialog('new-permit', function () {
      var f = {};
      E.Wizard({ id: 'pm-new-wz', title: 'New permit', data: { type: 'AirPermit', number: '', location: '', start: '2026-01-01', end: '2026-12-31' },
        steps: [
          { id: 'kind', label: 'Type', hint: 'The type decides which tables the permit has. Types are set up in “Permit types”.', render: function (b, d) {
              b.appendChild(h('div', { class: 'stack' }, TYPES.map(function (t) {
                var ready = t.version > 0;
                return E.Field({ id: 'pm-nw-' + t.code, label: t.name, hint: ready ? t.tables.map(function (x) { return x.name + ' (' + x.cols.length + ' columns)'; }).join(' · ') : 'Not published yet. Publish the type first.', control: h('input', { type: 'radio', name: 'pm-nw-type', id: 'pm-nw-' + t.code, value: t.code, disabled: !ready, checked: d.type === t.code, on: { change: function () { d.type = t.code; } } }) }); })));
            } },
          { id: 'head', label: 'Details', hint: 'The same number can exist in two types — a site often holds an air and a waste permit with one number.', render: function (b, d) {
              f.num = E.Input({ id: 'pm-nw-num', label: 'Permit number', required: true, mono: true, value: d.number, onInput: function (v) { d.number = v; } });
              f.loc = E.Select({ id: 'pm-nw-loc', label: 'Location', required: true, options: ['', 'Central processing facility', 'Offshore complex', 'Upstream well pads'], value: d.location, onChange: function (v) { d.location = v; } });
              f.s = E.Input({ id: 'pm-nw-s', label: 'Valid from', required: true, type: 'date', value: d.start, onInput: function (v) { d.start = v; } });
              f.e = E.Input({ id: 'pm-nw-e', label: 'Valid to', required: true, type: 'date', value: d.end, hint: 'First day the permit is no longer valid is the day after.', onInput: function (v) { d.end = v; } });
              b.appendChild(h('div', { class: 'form-grid' }, f.num, f.loc, f.s, f.e));
            }, validate: function (d) { f.num.setError(null); f.loc.setError && f.loc.setError(null);
              if (!d.number.trim()) { f.num.setError('Enter the permit number'); f.num.input.focus(); return 'A permit needs a number.'; }
              if (PERMITS.some(function (p) { return p.number === d.number.trim() && p.type === d.type; })) { f.num.setError('This type already has a permit with this number'); f.num.input.focus(); return 'The number is taken for this type.'; }
              if (!d.location) return 'Choose a location.';
              if (d.end <= d.start) return 'The end must be after the start.'; return null; } }],
        summary: function (d) { return E.KeyValue([{ label: 'Type', value: type(d.type).name }, { label: 'Number', value: d.number, mono: true }, { label: 'Location', value: d.location }, { label: 'Valid', value: F.date(d.start) + ' – ' + F.date(d.end) }, { label: 'Starts as', value: 'Draft — sheets do not see it until you activate it' }]); },
        reviewText: 'The permit is created empty, with the tables of its type. Add rows or import them next.', applyLabel: 'Create permit',
        onApply: function (d, api) {
          var id = 'P-' + ('000' + (PERMITS.length + 1)).slice(-4);
          PERMITS.unshift({ id: id, number: d.number.trim(), type: d.type, location: d.location, issue: '', start: d.start, end: d.end, actualEnd: '', state: 'Draft', forms: [], rows: {}, total: {}, bindings: [], updated: TODAY, by: ctx.me.name, isNew: true });
          PERMITS[0].rows = {}; type(d.type).tables.forEach(function (t) { PERMITS[0].rows[t.code] = []; });
          api.close(); later(function () { E.go('/admin/permits/' + id, { toast: 'Permit created as a draft' }); }); } });
    });
    ctx.dialog('import-permit', function () {
      E.Wizard({ id: 'pm-imp', title: 'Import permits from Excel', data: { file: 'PermitsInfo.Year.xlsx' },
        steps: [
          { id: 'file', label: 'File', hint: 'The yearly report your team keeps today works as is: one sheet per permit, the table header row tells which columns are which.', render: function (b, d) {
              b.appendChild(h('div', { class: 'data-drop', style: 'display:grid;justify-items:center;gap:8px;padding:24px;border:1px dashed var(--border-strong);border-radius:6px;background:var(--sunken);color:var(--muted)' }, icon('upload', 20), h('b', { class: 'mono' }, d.file), h('span', {}, '16 sheets · 173 KB')));
            } },
          { id: 'map', label: 'Match', hint: 'Each sheet is matched to a permit type by its title row; columns by header text in English or Russian.', render: function (b) {
              b.appendChild(h('div', { class: 'pm-tw' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Sheet', 'Permit type', 'Columns matched', 'Rows'].map(function (x) { return h('th', {}, x); }))),
                h('tbody', {}, [['KZ06VCZ14825472_Air', 'Air emission permit', '9 of 9', '43'], ['KZ14VCZ14523270_2026_Air', 'Air emission permit', '9 of 9', '65'], ['NA_Air', 'Air emission permit (placeholder)', '9 of 9', '10'], ['… 13 more air sheets', 'Air emission permit', '9 of 9', '—']].map(function (r) { return h('tr', {}, r.map(function (x) { return h('td', {}, x); })); })))));
            } }],
        summary: function () { return E.KeyValue([{ label: 'Creates', value: '16 permits (14 new, 2 already exist and are updated)' }, { label: 'Rows', value: '≈ 640' }, { label: 'Rule', value: 'All or nothing: one bad row stops the import and is listed' }]); },
        reviewText: 'Imported permits start as drafts. Nothing changes in sheets until you activate them.', applyLabel: 'Import',
        onApply: function (d, api) { api.close(); later(function () { E.Toast('Import started — it continues in My tasks'); }); } });
    });
    ctx.dialog('renew-permit', function (p) { p = p || PERMITS[0];
      E.ConfirmDialog({ id: 'pm-renew', title: 'Renew permit ' + p.number + '?', consequences: ['A new draft is created with the same tables and rows, valid from ' + F.date(p.end) + ' (the day the current one ends).', { text: 'This permit stays valid until then and then becomes Expired — it is kept, because old periods still read its limits.', note: true }, { text: 'Sheets switch to the new permit by themselves from the first day it is valid.', note: true }], verb: 'Create renewal', icon: 'copy',
        onConfirm: function () { var id = 'P-' + ('000' + (PERMITS.length + 1)).slice(-4); PERMITS.unshift(Object.assign({}, p, { id: id, state: 'Draft', start: p.end, end: '2027-12-31', actualEnd: '', bindings: [], isNew: true })); later(function () { E.go('/admin/permits/' + id, { toast: 'Renewal created as a draft' }); }); } }); });
    ctx.action('New permit', 'plus', function () { ctx.openDialog('new-permit'); });
  } });

  /* ================= 2. Перміт ================= */
  var PERMIT_SCREEN = { title: 'Permit', icon: 'book', navPath: '/admin/permits', example: '/admin/permits/P-0001',
    crumbs: function (ctx) { var p = permit(ctx.params.id); return [{ label: 'Configure' }, { label: 'Permits', href: '#/admin/permits' }, { label: p.number }]; },
    render: function (el, ctx) {
      var p = permit(ctx.params.id), t = type(p.type);
      var actions = h('div', { class: 'eh-actions' },
        p.state === 'Draft' ? null : E.Button({ label: 'Renew…', icon: 'copy', id: 'pm-renew-btn', onClick: function () { ctx.openDialog('renew-permit', p); } }),
        p.state === 'Draft' ? E.Button({ label: 'Activate…', icon: 'check', variant: 'primary', id: 'pm-activate', onClick: function () { ctx.openDialog('activate-permit'); } }) : E.Menu({ label: 'More', id: 'pm-more', items: [{ label: 'End this permit early…', icon: 'stop', danger: true, onClick: function () { ctx.openDialog('end-permit'); } }] }));
      el.appendChild(h('div', { class: 'editor-head' },
        h('div', { class: 'eh-title' }, h('a', { class: 'ph-back', href: '#/admin/permits', 'data-back': '1' }, icon('arrowL', 14), 'Back to Permits'), h('h1', { tabindex: '-1', 'data-page-title': '1', id: 'pm-h1', class: 'mono' }, p.number), h('span', { class: 'muted' }, t.name + ' · ' + p.location)),
        h('div', { class: 'eh-ctx' }, stateBadge(p), E.Stepper({ steps: ['Draft', 'Active', 'Expired'], current: { Draft: 0, Active: 1, Expired: 2 }[p.state] }), h('span', { class: 'muted', role: 'status' }, 'Saved ' + F.time(D.today)), actions)));
      var body = h('div', { class: 'pm-body' });
      el.appendChild(body);

      var tabs = [{ id: 'overview', label: 'Overview', render: function (panel) { overview(panel); } }].concat(t.tables.map(function (tbl) {
        var rows = rowsOf(p, tbl), tot = (p.total && p.total[tbl.code]) || [rows.length]; return { id: tbl.code, label: tbl.name, count: tot[0], render: function (panel) { tableTab(panel, tbl); } }; }));
      body.appendChild(E.Tabs({ id: 'pm-tabs', ctx: ctx, tabs: tabs }));

      function overview(panel) {
        var d = days(TODAY, p.end), total = days(p.start, p.end) || 1, nowPct = Math.max(0, Math.min(100, Math.round(days(p.start, TODAY) / total * 100)));
        if (expiring(p)) panel.appendChild(E.Banner({ tone: 'warning', title: 'Ends in ' + d + ' days (' + F.date(p.end) + ')', text: 'Sheets of periods after that date will find no limits unless a renewal is active.', actions: [{ label: 'Renew…', onClick: function () { ctx.openDialog('renew-permit', p); } }] }));
        if (p.state === 'Draft') panel.appendChild(E.Banner({ tone: 'info', title: 'Draft — sheets do not read this permit yet', text: 'Check the tables, then activate. Activating makes the limits visible to every sheet bound to this type.', actions: [{ label: 'Activate…', onClick: function () { ctx.openDialog('activate-permit'); } }] }));
        var head = t.tables.length ? null : null;
        panel.appendChild(h('div', { class: 'pm-two' },
          h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Permit details')),
            E.KeyValue([{ label: 'Number', value: p.number, mono: true }, { label: 'Type', value: t.name }, { label: 'Location', value: p.location, hint: 'From the Locations registry' }, { label: 'Issued', value: p.issue ? F.date(p.issue) : '—' },
              { label: 'Valid', value: F.date(p.start) + ' – ' + F.date(p.end) }, { label: 'Ended early', value: p.actualEnd && p.actualEnd !== p.end ? F.date(p.actualEnd) : '—' },
              { label: 'Reported in', value: p.forms.length ? chips(p.forms.map(function (x) { return x === 'TOx' ? 'TOx' : 'Form ' + x; })) : 'No reporting form' }])),
          h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Validity'), h('span', { class: 'sp' })),
            h('div', {}, h('div', { class: 'pm-tl', role: 'img', 'aria-label': 'Permit validity with today marked' }, h('div', { class: 'pm-tl-bar' }), h('div', { class: 'pm-tl-run', style: 'left:0;width:' + nowPct + '%' }), h('div', { class: 'pm-tl-now', style: 'left:' + nowPct + '%' })),
              h('div', { class: 'pm-tl-lbl' }, h('span', {}, F.date(p.start)), h('span', {}, 'Today ' + F.date(TODAY)), h('span', {}, F.date(p.end)))),
            note('Sheets pick the permit that is valid on the last day of their period. Between two permits with the same number and type, the one with the later start wins from its first day.'))));
        var calc = t.tables.filter(function (x) { return p.total && p.total[x.code]; });
        panel.appendChild(h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Totals and checks'), E.Button({ label: 'How these are calculated', icon: 'info', size: 'sm', variant: 'ghost', onClick: function (e, b) { E.Popover(b, { title: 'Totals are never typed', content: 'They are summed from the table rows each time, so they cannot drift from the rows. Checks come from the permit type.' }); } })),
          h('div', { class: 'pm-cards' }, calc.map(function (x) { var tot = p.total[x.code], ok = x.code !== 'Intake';
            return h('div', { class: 'pm-card' }, h('h3', {}, x.name), h('div', { class: 'row between' }, h('span', { class: 'muted' }, F.int(tot[0]) + ' rows'), h('b', { class: 'mono' }, F.number(tot[1], 5))),
              x.rules.length ? h('div', { class: 'stack gap-1' }, x.rules.slice(0, 2).map(function (r, i) { var bad = x.code === 'Intake' && i === 0; return h('span', { class: 'row gap-1 ' + (bad ? 'warning-text' : 'muted') }, icon(bad ? 'alert' : 'checkCircle', 14), bad ? 'Row 1: months add to 0.143, annual says 0.14' : r.name); })) : null); }),
            calc.length === 0 ? h('div', { class: 'pm-card' }, h('span', { class: 'muted' }, 'No rows yet.')) : null)));
        panel.appendChild(h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Read by')),
          p.bindings.length ? h('div', { class: 'pm-tw' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Sheet table', 'Template', 'How', ''].map(function (x) { return h('th', {}, x); }))), h('tbody', {}, p.bindings.map(function (id) { var b = binding(id); return h('tr', { tabindex: '0' , on: { click: function () { E.go('/admin/permits/bindings/' + b.id); } } }, h('td', {}, E.twoLine(b.sheet + ' · ' + b.target, b.id, { mono: false })), h('td', {}, b.template), h('td', {}, b.mode), h('td', {}, icon('chevR', 14))); }))))
            : h('div', { class: 'mini-empty' }, h('span', {}, 'No sheet reads this permit yet.'), E.Button({ label: 'Bind to a sheet', icon: 'link', onClick: function () { E.go('/admin/permits/bindings', { params: { dialog: 'new-binding' } }); } }))));
      }

      function tableTab(panel, tbl) {
        var rows = rowsOf(p, tbl), tot = (p.total && p.total[tbl.code]) || [rows.length, null];
        var wide = tbl.cols.length > 12, state = { core: wide };
        var tools = h('div', { class: 'pm-tools' },
          E.Button({ label: 'Add row', icon: 'plus', id: 'pm-addrow-' + tbl.code, variant: 'primary', disabled: p.state === 'Expired', title: p.state === 'Expired' ? 'Expired permits are read-only' : null, onClick: function () { ctx.openPanel(tbl.code + ':__new'); } }),
          E.Button({ label: 'Paste from Excel…', icon: 'paperclip', id: 'pm-paste-' + tbl.code, disabled: p.state === 'Expired', onClick: function () { ctx.openDialog('paste-rows', tbl); } }),
          E.Button({ label: 'Import file…', icon: 'upload', onClick: function () { ctx.openDialog('import-permit'); } }),
          h('span', { class: 'sp' }),
          wide ? E.Segmented({ id: 'pm-cols-' + tbl.code, label: 'Columns', value: 'Grouped', options: ['Grouped', 'All columns'], onChange: function (v) { grid.setCollapsed(v === 'Grouped'); } }) : null);
        panel.appendChild(h('div', { class: 'stack gap-3' },
          h('div', { class: 'pm-gridnote' }, icon('info', 14), h('span', {}, tbl.name + ' has ' + tbl.cols.length + ' columns' + (wide ? '. Related columns are grouped — click a group to open it.' : '.') + ' Click a row to edit it. Showing ' + rows.length + ' of ' + F.int(tot[0]) + ' rows.')),
          tools));
        var groupNames = [], gcount = {}; tbl.cols.forEach(function (c) { if (c.group) { gcount[c.group] = (gcount[c.group] || 0) + 1; if (groupNames.indexOf(c.group) < 0) groupNames.push(c.group); } });
        var sums = {}; if (tot[1] != null) { var main = tbl.cols.filter(function (c) { return c.type === 'Number'; })[0]; if (main) sums[main.code] = tot[1]; }
        var grid = DefTable(tbl, rows, { id: 'pm-grid-' + tbl.code, caption: tbl.name, collapse: wide ? groupNames.filter(function (g) { return gcount[g] >= 5; }) : [], totals: tot[1] != null ? { label: 'Σ all ' + F.int(tot[0]) + ' rows', sums: sums } : null,
          onRow: function (r) { ctx.openPanel(tbl.code + ':' + r.id); },
          warnCell: function (r, c) { return tbl.code === 'Intake' && r.id === 'i1' && (c.code === 'Annual' || c.code === 'Jan'); } });
        panel.appendChild(rows.length ? grid : h('div', { class: 'mini-empty panel' }, h('b', {}, 'No rows in ' + tbl.name + ' yet'),
          h('span', {}, p.state === 'Expired' ? 'This permit has expired and is read-only.' : 'Add the first row by hand, or paste a block of rows from Excel.'),
          p.state === 'Expired' ? null : h('div', { class: 'row' }, E.Button({ label: 'Add row', icon: 'plus', variant: 'primary', onClick: function () { ctx.openPanel(tbl.code + ':__new'); } }), E.Button({ label: 'Paste from Excel…', icon: 'paperclip', onClick: function () { ctx.openDialog('paste-rows', tbl); } }))));
        if (tbl.rules.length) panel.appendChild(E.Collapsible({ id: 'pm-rules-' + tbl.code, title: 'Checks for this table', summary: tbl.rules.length + ' rules', content: h('div', { class: 'stack' }, tbl.rules.map(function (r) { return h('div', { class: 'row between' }, h('span', {}, r.name), h('span', { class: 'row' }, E.CodeText(r.text), h('span', { class: 'chip' }, r.mode === 'Block' ? 'Blocks saving' : 'Warns'))); })) }));
      }
      /* Введення даних: шторка рядка (новий / редагування) і вставка з Excel */
      ctx.panel('*', function (id) {
        var parts = id.split(':'), tbl = find(t.tables, 'code', parts[0]) || t.tables[0], list = rowsOf(p, tbl);
        var isNew = parts[1] === '__new', row = isNew ? null : (find(list, 'id', parts[1]) || list[0]);
        var ro = p.state === 'Expired', m = {}, ctls = {};
        tbl.cols.forEach(function (c) { m[c.code] = row ? row[c.code] : (c.type === 'Yes / No' ? false : ''); });
        function bump(d) { if (p.total && p.total[tbl.code]) p.total[tbl.code][0] += d; }
        function field(c) {
          var idp = 'pm-f-' + c.code, v = m[c.code], ctl;
          if (c.via) ctl = E.Input({ id: idp, label: c.en, value: String(v == null ? '' : v), readOnly: true, hint: 'Read from the linked record' });
          else if (c.type === 'Yes / No') ctl = E.Switch({ id: idp, label: c.en, checked: !!v, onChange: function (x) { m[c.code] = x; } });
          else if (c.type === 'Link to registry' || c.type === 'Choice') {
            var sc = scopeOf(c);
            ctl = E.Select({ id: idp, label: c.en, required: c.req, options: optsFor(c), value: v || '', disabled: ro, onChange: function (x) { m[c.code] = x; }, hint: sc === 'shared' ? 'From the shared registry ' + (c.ref || '').split(' ')[0] : sc === 'type' ? 'A list only for ' + t.name : 'A table of this permit: ' + (c.ref || '').split(' ')[0] });
            ctls[c.code] = ctl;
            return h('div', { class: 'stack gap-1 pm-fieldwrap' }, ctl, ro ? null : E.Button({ label: sc === 'permit' ? 'Open that table' : 'Not in the list? Add a value…', icon: sc === 'permit' ? 'arrowR' : 'plus', size: 'sm', variant: 'ghost', onClick: function () {
              if (sc === 'permit') { E.Toast('Add the row to the table “' + (c.ref || '').split(' ')[0] + '” of this permit first; it then appears here', 'warning'); return; }
              valueDialog('Add a value to ' + c.en, sc === 'shared' ? 'It is added to the shared registry ' + (c.ref || '').split(' ')[0] + ', so every permit type that uses it will see it.' : 'It is added to the list of this permit type only.', function (val) {
                if (sc === 'shared') { var k = (c.ref || '').split(' ')[0]; (LISTS[k] = LISTS[k] || []).push(val); } else { c.choices = choicesOf(c).slice(); c.choices.push(val); }
                var sel = ctl.querySelector('select'), o = document.createElement('option'); o.value = o.textContent = val; sel.appendChild(o); sel.value = val; m[c.code] = val; }); } }));
          }
          else if (c.type === 'Date') ctl = E.Input({ id: idp, label: c.en, required: c.req, type: 'date', value: v || '', readOnly: ro, onInput: function (x) { m[c.code] = x; } });
          else if (c.type === 'Number') ctl = E.Input({ id: idp, label: c.en, required: c.req, mono: true, suffix: c.unit || undefined, value: v == null ? '' : String(v), readOnly: ro, onInput: function (x) { var n = parseFloat(String(x).replace(',', '.')); m[c.code] = isNaN(n) ? '' : n; } });
          else ctl = E.Input({ id: idp, label: c.en, required: c.req, value: v == null ? '' : String(v), readOnly: ro, onInput: function (x) { m[c.code] = x; } });
          ctls[c.code] = ctl; return ctl;
        }
        function save() {
          var bad = tbl.cols.filter(function (c) { return c.req && !c.via && (m[c.code] === '' || m[c.code] == null); });
          Object.keys(ctls).forEach(function (k) { if (ctls[k].setError) ctls[k].setError(null); });
          if (bad.length) { bad.forEach(function (c) { if (ctls[c.code].setError) ctls[c.code].setError('Required'); }); if (bad[0] && ctls[bad[0].code].input) ctls[bad[0].code].input.focus(); E.Toast('Fill the required fields: ' + bad.map(function (c) { return c.en; }).join(', '), 'warning'); return false; }
          var kc = tbl.cols.filter(function (k) { return k.key; });
          if (kc.length && list.some(function (r) { return r !== row && kc.every(function (k) { return String(r[k.code] || '') === String(m[k.code] || ''); }); })) { E.Toast('This permit already has a row with the same ' + kc.map(function (k) { return k.en; }).join(' + '), 'warning'); return false; }
          if (isNew) { list.push(Object.assign({ id: tbl.code + Date.now() }, m)); bump(1); } else Object.assign(row, m);
          p.updated = TODAY; p.by = ctx.me.name; E.Toast(isNew ? 'Row added' : 'Saved', 'success'); return true;
        }
        E.Drawer({ id: 'pm-row', title: isNew ? 'New row' : String(row[tbl.cols[0].code] || row.id), subtitle: tbl.name + ' · ' + p.number + (ro ? ' · read-only (expired)' : ''), wide: true,
          body: function (b) {
            var groups = {}, order = [];
            tbl.cols.forEach(function (c) { var g = c.group || 'Main'; if (!groups[g]) { groups[g] = []; order.push(g); } groups[g].push(c); });
            order.forEach(function (g, gi) {
              var content = h('div', { class: 'form-grid' }, groups[g].map(field));
              b.appendChild(gi === 0 ? h('div', { class: 'stack' }, content) : E.Collapsible({ id: 'pm-g-' + gi, title: g, summary: groups[g].length + ' fields', open: isNew && groups[g].some(function (c) { return c.req; }), content: content }));
            });
          },
          footer: (ro ? [{ label: 'Close' }] : [
            row ? { label: 'Delete row', danger: true, align: 'left', icon: 'trash', onClick: function () { var i = list.indexOf(row); list.splice(i, 1); bump(-1); E.Toast('Row deleted', { action: { label: 'Undo', onClick: function () { list.splice(i, 0, row); bump(1); ctx.refresh(); } } }); later(function () { reload(ctx); }); } } : null,
            { label: 'Close' },
            isNew ? { label: 'Save and add next', keepOpen: true, onClick: function (ctl) { if (!save()) return false; ctl.close(); later(function () { reload(ctx, { panel: tbl.code + ':__new' }); }); } } : null,
            { label: 'Save', variant: 'primary', icon: 'check', keepOpen: true, onClick: function (ctl) { if (!save()) return false; ctl.close(); later(function () { reload(ctx); }); } }]).filter(Boolean) });
      });
      ctx.dialog('paste-rows', function (tbl) {
        tbl = tbl || t.tables[0];
        var cols = tbl.cols.filter(function (c) { return !c.via; }), ta, info;
        var sample = [1, 2, 3].map(function (i) { return cols.map(function (c) { return c.type === 'Number' ? (i * 1.5).toFixed(2) : c.type === 'Date' ? '2026-01-0' + i : c.type === 'Yes / No' ? 'Yes' : (c.type === 'Link to registry' || c.type === 'Choice') ? (optsFor(c)[i] || 'Item ' + i) : c.en + ' ' + i; }).join('\t'); }).join('\n');
        function parse() {
          return ta.value.split(/\r?\n/).filter(function (l) { return l.trim(); }).map(function (l, k) {
            var cells = l.split('\t'), r = { id: tbl.code + Date.now() + '-' + k };
            cols.forEach(function (c, i) { var s = (cells[i] || '').trim(); r[c.code] = c.type === 'Number' ? (s === '' ? '' : parseFloat(s.replace(/\s/g, '').replace(',', '.'))) : c.type === 'Yes / No' ? /^(yes|true|1|так|да)$/i.test(s) : s; });
            return r; });
        }
        function upd() { var n = parse().length; info.textContent = n ? n + ' rows will be added to ' + tbl.name : 'Nothing to add yet'; }
        E.Dialog({ id: 'pm-paste', title: 'Paste rows into ' + tbl.name, wide: true,
          body: function (b) {
            b.appendChild(note('Copy cells in Excel and paste them here. One line is one row. Columns are expected in this order:'));
            b.appendChild(chips(cols.map(function (c) { return c.en; })));
            ta = E.Textarea({ id: 'pm-paste-ta', label: 'Rows', rows: 7, hint: 'Tab-separated, as Excel copies them. A decimal comma is accepted.', onInput: upd });
            b.appendChild(ta);
            info = h('span', { class: 'muted', role: 'status' }, 'Nothing to add yet');
            b.appendChild(h('div', { class: 'row between' }, info, E.Button({ label: 'Fill with an example', size: 'sm', onClick: function () { ta.value = sample; upd(); } })));
            b.appendChild(note('Rows with missing required values are still added and marked red in the table, so you can fix them there.'));
          },
          footer: [{ label: 'Cancel' }, { label: 'Add rows', variant: 'primary', icon: 'plus', keepOpen: true, onClick: function (ctl) {
            var rows = parse(); if (!rows.length) { E.Toast('Paste at least one row first', 'warning'); return false; }
            var list = rowsOf(p, tbl); rows.forEach(function (r) { list.push(r); }); if (p.total && p.total[tbl.code]) p.total[tbl.code][0] += rows.length;
            p.updated = TODAY; ctl.close(); later(function () { reload(ctx); E.Toast(rows.length + ' rows added', 'success'); }); } }] });
      });
      ctx.dialog('activate-permit', function () {
        E.ConfirmDialog({ id: 'pm-activate-dlg', title: 'Activate permit ' + p.number + '?', consequences: ['Sheets bound to “' + t.name + '” start reading these limits for periods inside ' + F.date(p.start) + ' – ' + F.date(p.end) + '.', { text: 'Rows can still be edited; every edit is dated and shows in the history.', note: true }, { text: 'Two active permits of one type, number and overlapping dates are not allowed.', note: true }], verb: 'Activate', icon: 'check',
          onConfirm: function () { p.state = 'Active'; reload(ctx); } }); });
      ctx.dialog('end-permit', function () {
        E.ReasonDialog({ id: 'pm-end', title: 'End permit ' + p.number + ' early?', text: 'Sheets stop reading it from the date you set. Past periods keep their limits.', label: 'Reason', minLength: 10, verb: 'End permit', danger: true, icon: 'stop', onSubmit: function () { p.actualEnd = TODAY; p.state = 'Expired'; reload(ctx); } }); });
      ctx.dialog('renew-permit', function (pp) { pp = pp || p;
        E.ConfirmDialog({ id: 'pm-renew', title: 'Renew permit ' + pp.number + '?', consequences: ['A new draft is created with the same rows, valid from ' + F.date(pp.end) + '.', { text: 'This permit is kept as Expired afterwards, because old periods still read its limits.', note: true }], verb: 'Create renewal', icon: 'copy', onConfirm: function () { later(function () { E.Toast('Renewal created as a draft', 'success'); }); } }); });
    } };

  /* ================= 3. Типи пермітів: перелік ================= */
  E.screen('/admin/permits/types', { title: 'Permit types', icon: 'sliders', navPath: '/admin/permits', render: function (el, ctx) {
    var rows = ctx.state === 'empty' ? [] : TYPES;
    E.ListPage(el, ctx, { id: 'pmt', rows: rows, banner: h('div', { class: 'pm-slot' }, subnav('/admin/permits/types')),
      header: { title: 'Permit types', back: false,
        subtitle: 'A type says what a permit contains: which fields it has and which tables, with which columns. Add a type here and permits of that type work everywhere — no code, no release.',
        primary: { label: 'New type', icon: 'plus', id: 'pmt-new', onClick: function () { ctx.openDialog('new-type'); } } },
      stats: [{ id: 'all', label: 'types', value: function (r) { return r.length; } }, { id: 'tables', label: 'tables', value: function (r) { return r.reduce(function (s, t) { return s + t.tables.length; }, 0); } }, { id: 'draft', label: 'with unpublished changes', tone: 'warning', match: function (t) { return t.state === 'Draft'; } }],
      search: { placeholder: 'Type name', text: function (t) { return t.name + ' ' + t.ru + ' ' + t.code; } },
      filters: [{ id: 'category', label: 'Categories', options: ['Air', 'Water', 'Waste'] }],
      onRow: function (t) { E.go('/admin/permits/types/' + t.code); },
      table: { rowKey: 'code', tall: true, rowLabel: function (t) { return t.name; }, columns: [
        { key: 'name', label: 'Type', render: function (t) { return E.twoLine(t.name, t.ru, { mono: false }); } },
        { key: 'category', label: 'Category' },
        { key: 'tables', label: 'Tables', render: function (t) { return t.tables.map(function (x) { return x.name + ' (' + x.cols.length + ')'; }).join(' · '); }, sortValue: function (t) { return t.tables.length; } },
        { key: 'permits', label: 'Permits', num: true, sortValue: function (t) { return PERMITS.filter(function (p) { return p.type === t.code; }).length; }, render: function (t) { return String(PERMITS.filter(function (p) { return p.type === t.code; }).length); } },
        { key: 'updated', label: 'Updated', hideSm: true, render: function (t) { return E.twoLine(F.date(t.updated), t.by); } },
        { key: 'state', label: 'State', render: function (t) { return E.StatusBadge('version', t.state, { quiet: true, label: t.state === 'Draft' ? 'Draft changes' : 'v' + t.version }); } }] },
      empty: { icon: 'sliders', title: 'No permit types yet', text: 'Create a type, add its tables and columns, publish it. Then permits of that type can be entered.', actions: [{ label: 'New type', variant: 'primary', icon: 'plus', onClick: function () { ctx.openDialog('new-type'); } }] },
      error: { code: 'ECR-PRM-0503' } });
    ctx.dialog('new-type', function () {
      var f = {};
      E.Wizard({ id: 'pmt-wz', title: 'New permit type', data: { name: '', category: 'Air', start: 'blank' },
        steps: [
          { id: 'basics', label: 'Basics', hint: 'Name the kind of permit the way your team says it.', render: function (b, d) {
              f.name = E.Input({ id: 'pmt-wz-name', label: 'Name', required: true, value: d.name, placeholder: 'Noise permit', onInput: function (v) { d.name = v; } });
              f.cat = E.Select({ id: 'pmt-wz-cat', label: 'Category', options: ['Air', 'Water', 'Waste', 'Other'], value: d.category, onChange: function (v) { d.category = v; }, hint: 'Used to group types in lists and reports.' });
              f.from = E.Select({ id: 'pmt-wz-from', label: 'Start from', options: [{ value: 'blank', label: 'An empty type' }].concat(TYPES.map(function (t) { return { value: t.code, label: 'A copy of “' + t.name + '”' }; })), value: d.start, onChange: function (v) { d.start = v; } });
              b.appendChild(h('div', { class: 'form-grid' }, f.name, f.cat, h('div', { class: 'span-2' }, f.from)));
            }, validate: function (d) { f.name.setError(null); if (!d.name.trim()) { f.name.setError('Enter a name'); f.name.input.focus(); return 'A type needs a name.'; } if (TYPES.some(function (t) { return t.name.toLowerCase() === d.name.trim().toLowerCase(); })) { f.name.setError('A type with this name exists'); return 'Choose another name.'; } return null; } }],
        summary: function (d) { return E.KeyValue([{ label: 'Name', value: d.name }, { label: 'Category', value: d.category }, { label: 'Fields', value: chips(['Permit number', 'Location', 'Issue date', 'Valid from', 'Valid to', 'Actual end']) }, { label: 'Starts as', value: 'Draft — no permits can be entered until it is published' }]); },
        reviewText: 'Every type has the same permit header. Tables and extra fields are added next.', applyLabel: 'Create type',
        onApply: function (d, api) { var code = d.name.replace(/[^A-Za-z0-9 ]/g, '').replace(/(?:^| )(\w)/g, function (m, c) { return c.toUpperCase(); }); var src = d.start !== 'blank' ? type(d.start) : null;
          TYPES.unshift({ code: code, name: d.name.trim(), ru: '', category: d.category, state: 'Draft', version: 0, updated: TODAY, by: ctx.me.name, regCode: code.toUpperCase(), tables: src ? JSON.parse(JSON.stringify(src.tables)) : [], rules: [], isNew: true });
          api.close(); later(function () { E.go('/admin/permits/types/' + code, { toast: 'Type created — add tables, then publish' }); }); } });
    });
    ctx.action('New permit type', 'plus', function () { ctx.openDialog('new-type'); });
  } });

  /* ================= 4. Конструктор типу ================= */
  E.screen('/admin/permits/types/:code', { title: 'Permit type', icon: 'sliders', navPath: '/admin/permits', example: '/admin/permits/types/WaterDischarge',
    crumbs: function (ctx) { var t = type(ctx.params.code); return [{ label: 'Configure' }, { label: 'Permits', href: '#/admin/permits' }, { label: 'Permit types', href: '#/admin/permits/types' }, { label: t.name }]; },
    render: function (el, ctx) {
      var t = type(ctx.params.code), sel = ctx.query.t || (t.tables[0] ? t.tables[0].code : '__head'), dirty = t.state === 'Draft' ? 2 : 0;
      var actions = h('div', { class: 'eh-actions' },
        E.Button({ label: 'Check', icon: 'checkCircle', id: 'pmt-check', onClick: function () {
          var probs = []; if (!t.tables.length) probs.push('The type has no tables yet');
          t.tables.forEach(function (x) { if (!x.cols.some(function (c) { return c.key; })) probs.push('“' + x.name + '” has no key column'); });
          if (probs.length) E.Toast(probs.join('. '), 'warning'); else E.Toast('No problems found: every table has a key column and every list exists', 'success'); } }),
        E.Button({ label: 'Publish…', icon: 'send', variant: 'primary', id: 'pmt-publish', onClick: function () { ctx.openDialog('publish-type'); } }));
      el.appendChild(h('div', { class: 'editor-head' },
        h('div', { class: 'eh-title' }, h('a', { class: 'ph-back', href: '#/admin/permits/types', 'data-back': '1' }, icon('arrowL', 14), 'Back to Permit types'), h('h1', { tabindex: '-1', 'data-page-title': '1', id: 'pmt-h1' }, t.name), h('span', { class: 'muted' }, t.ru)),
        h('div', { class: 'eh-ctx' }, E.StatusBadge('version', t.state === 'Draft' ? 'Draft' : 'Published', { label: t.state === 'Draft' ? 'Draft v' + (t.version + 1) : 'Published v' + t.version }), h('span', { class: 'muted', role: 'status' }, dirty ? dirty + ' unpublished changes' : 'Saved ' + F.time(D.today)), actions)));
      var body = h('div', { class: 'pm-body' }); el.appendChild(body);
      body.appendChild(E.Tabs({ id: 'pmt-tabs', ctx: ctx, fill: false, tabs: [
        { id: 'structure', label: 'Structure', render: structure }, { id: 'rules', label: 'Checks', count: t.rules.length + t.tables.reduce(function (s, x) { return s + x.rules.length; }, 0), render: rulesTab }, { id: 'versions', label: 'Versions', render: versions }] }));
      if (ctx.hasUnsaved) ctx.hasUnsaved(function () { return dirty > 0 && false; }, { count: function () { return dirty; }, onSave: function () {}, onDiscard: function () {} });

      function structure(panel) {
        panel.appendChild(E.Banner({ tone: 'info', title: 'Add a table with as many columns as the permit needs', text: 'Each table is stored as a registry that belongs to the permit (a “part of” link), so permits can have any number of tables and every table its own columns. Nothing to install or release.', actions: [{ label: 'Add table…', icon: 'plus', onClick: function () { ctx.openDialog('add-table'); } }] }));
        var side = h('aside', { class: 'side', 'aria-label': 'Tables of this type' }, h('div', { class: 'pane-h' }, h('h2', {}, 'Structure'), E.Button({ label: 'Add table', icon: 'plus', size: 'sm', variant: 'primary', id: 'pmt-addtable', onClick: function () { ctx.openDialog('add-table'); } })), h('div', { class: 'pm-tree scroll' }));
        var tree = side.querySelector('.pm-tree');
        function node(label, code, n, sub) { return h('button', { type: 'button', class: 'pm-node' + (sub ? ' sub' : ''), 'aria-current': String(sel === code), on: { click: function () { ctx.setQuery({ t: code }); sel = code; ctx.refresh(); } } }, icon(code === '__head' ? 'file' : 'table', 14), h('span', { class: 'truncate' }, label), h('span', { class: 'n' }, String(n))); }
        tree.appendChild(node('Permit details', '__head', HEAD.length + (t.extra ? t.extra.length : 0)));
        tree.appendChild(h('div', { class: 'menu-h' }, 'Tables'));
        t.tables.forEach(function (x) { tree.appendChild(node(x.name, x.code, x.cols.length)); });
        if (!t.tables.length) tree.appendChild(h('div', { class: 'mini-empty' }, 'No tables yet. A permit of this type will have only the details above.'));
        tree.appendChild(h('div', { class: 'p-2' }, E.Button({ label: 'Add another table', icon: 'plus', id: 'pmt-addtable-2', onClick: function () { ctx.openDialog('add-table'); } })));
        var main = h('div', { class: 'main' });
        var cur = sel === '__head' ? { name: 'Permit details', code: '__head', cols: HEAD, regCode: t.regCode, head: true } : (find(t.tables, 'code', sel) || t.tables[0]);
        var aside = h('aside', { class: 'aside', 'aria-label': 'Column properties', hidden: !ctx.query.panel });
        main.appendChild(h('div', { class: 'pane-h' }, h('h2', {}, cur ? cur.name : 'Table', cur ? h('span', { class: 'muted' }, cur.cols.length + ' columns') : null),
          E.Button({ label: 'Add column', icon: 'plus', id: 'pmt-addcol', onClick: function () { ctx.openDialog('add-column'); } }),
          E.Menu({ label: 'More', id: 'pmt-colmore', items: [{ label: 'Add months January–December…', icon: 'calendar', onClick: function () { ctx.openDialog('add-series'); } }, { sep: true }, { label: 'Delete table…', icon: 'trash', danger: true, disabled: !!(cur && cur.head), onClick: function () { ctx.openDialog('delete-table'); } }] })));
        if (cur) {
          main.appendChild(h('div', { class: 'scroll fill' }, h('div', { class: 'pm-tw', style: 'border-radius:0;border-width:0;max-height:none' }, h('table', { class: 'pm-t pm-map' }, h('thead', {}, h('tr', {}, ['Column', 'Code', 'Type', 'Unit', 'Required', 'Part of key', 'Group'].map(function (x, i) { return h('th', { class: i > 3 ? 'c' : null, scope: 'col' }, x); }))),
            h('tbody', {}, cur.cols.map(function (c) { var tr = h('tr', { tabindex: '0', 'aria-label': c.en }, h('td', { class: 'pm-first' }, E.twoLine(c.en, c.ru || '—', { mono: false })), h('td', { class: 'mono' }, c.code), h('td', {}, c.via ? 'Read from ' + c.via : (c.type === 'Link to registry' ? 'Link → ' + c.ref : c.type)), h('td', { class: 'mono' }, c.unit || ''), h('td', { class: 'c' }, c.req ? icon('check', 14) : ''), h('td', { class: 'c' }, c.key ? icon('key', 14) : ''), h('td', {}, c.group || ''));
              tr.addEventListener('click', function () { ctx.setQuery({ panel: c.code }); aside.hidden = false; fillAside(c); }); return tr; })))),
            cur.head ? null : h('div', { class: 'p-3 stack' }, E.CodeText('Unique rows: Permit + ' + cur.cols.filter(function (c) { return c.key; }).map(function (c) { return c.en; }).join(' + ') + (cur.cols.some(function (c) { return c.key; }) ? '' : '(no key yet)'))),
            E.Collapsible({ id: 'pmt-hood', title: 'Under the hood', summary: cur.regCode, content: h('div', { class: 'stack' }, note('This table is saved as the registry ' + cur.regCode + ', a part of ' + t.regCode + '. You can see it in Registries; you never have to touch it there.'), E.KeyValue([{ label: 'Registry', value: cur.regCode, mono: true }, { label: 'Part of', value: t.regCode, mono: true }, { label: 'Rows live', value: 'while the permit is valid' }])) })));
        }
        function choicesBlock(c) {
          if (c.type !== 'Link to registry' && c.type !== 'Choice') return null;
          var sc = scopeOf(c), refCode = (c.ref || '').split(' ')[0];
          var scope = E.Select({ id: 'pmt-p-scope', label: 'Choices come from', value: sc, options: [{ value: 'shared', label: 'A shared registry (used by many types)' }, { value: 'type', label: 'A list only for this permit type' }, { value: 'permit', label: 'A table of the same permit' }],
            onChange: function (v) {
              if (v === 'shared') { c.type = 'Link to registry'; c.ref = sc === 'shared' ? c.ref : 'Substances'; }
              else if (v === 'type') { c.type = 'Choice'; c.choices = choicesOf(c); }
              else { c.type = 'Link to registry'; c.ref = ((t.tables.filter(function (x) { return x !== cur; })[0] || { code: 'Table' }).code) + ' (this permit)'; }
              t.state = 'Draft'; later(function () { fillAside(c); }); } });
          var detail = sc === 'shared' ? E.Select({ id: 'pmt-p-ref', label: 'Registry', options: Object.keys(LISTS), value: refCode, hint: 'Edited in Registries. Every type that uses it sees the same values.', onChange: function (v) { c.ref = v; } })
            : sc === 'type' ? E.Textarea({ id: 'pmt-p-choices', label: 'Values, one per line', rows: 5, hint: 'Only this type sees these values. They can also be edited on the Lists page.', onInput: function (v) { c.choices = v.split(/\r?\n/).filter(function (x) { return x.trim(); }); } })
            : E.Select({ id: 'pmt-p-tbl', label: 'Table of this permit', options: t.tables.filter(function (x) { return x !== cur; }).map(function (x) { return { value: x.code, label: x.name }; }), value: refCode, hint: 'Users can pick only rows that this permit has in that table.', onChange: function (v) { c.ref = v + ' (this permit)'; } });
          if (sc === 'type') detail.value = choicesOf(c).join('\n');
          return h('div', { class: 'stack' }, scope, detail);
        }
        function fillAside(c) {
          aside.textContent = '';
          aside.appendChild(h('div', { class: 'pane-h' }, h('h2', {}, c.en), E.Button({ label: 'Close', icon: 'x', iconOnly: true, variant: 'ghost', onClick: function () { aside.hidden = true; ctx.setQuery({ panel: null }); } })));
          aside.appendChild(h('div', { class: 'pm-inspect' },
            E.Input({ id: 'pmt-p-en', label: 'Name (English)', required: true, value: c.en }), E.Input({ id: 'pmt-p-ru', label: 'Name (Russian)', value: c.ru, hint: 'Shown to Russian-speaking users; kk is added the same way.' }),
            E.Input({ id: 'pmt-p-code', label: 'Code', mono: true, value: c.code, readOnly: true, hint: 'Used in formulas. Fixed after publishing.' }),
            E.Select({ id: 'pmt-p-type', label: 'Type', options: TYPES_LIST, value: c.type, disabled: !!c.via }),
            choicesBlock(c),
            c.type === 'Number' ? E.Input({ id: 'pmt-p-unit', label: 'Unit', mono: true, value: c.unit || '' }) : null,
            E.Input({ id: 'pmt-p-group', label: 'Group', value: c.group || '', hint: 'Columns with the same group fold into one when the table is wide.' }),
            E.Switch({ id: 'pmt-p-req', label: 'Required', checked: c.req }), E.Switch({ id: 'pmt-p-key', label: 'Part of the row’s unique key', checked: c.key, hint: 'Two rows of one permit cannot repeat the key columns.' })));
        }
        if (ctx.query.panel && cur) { var pc = find(cur.cols, 'code', ctx.query.panel); if (pc) fillAside(pc); }
        panel.appendChild(h('div', { class: 'split', style: 'min-height:420px;border:1px solid var(--border);border-radius:6px;overflow:hidden;background:var(--surface)' }, side, main, aside));
      }
      function rulesTab(panel) {
        panel.appendChild(note('Checks run every time a row is saved or a permit is activated. “Blocks saving” stops the change; “Warns” lets it through and marks the row.'));
        var all = t.rules.map(function (r) { return Object.assign({ where: 'Whole permit' }, r); });
        t.tables.forEach(function (x) { x.rules.forEach(function (r) { all.push(Object.assign({ where: x.name }, r)); }); });
        panel.appendChild(all.length ? h('div', { class: 'pm-tw' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Check', 'Applies to', 'Rule', 'If it fails'].map(function (x) { return h('th', {}, x); }))), h('tbody', {}, all.map(function (r) { return h('tr', {}, h('td', {}, r.name), h('td', {}, r.where), h('td', {}, E.CodeText(r.text)), h('td', {}, r.mode === 'Block' ? 'Blocks saving' : 'Warns')); }))))
          : h('div', { class: 'mini-empty' }, 'No checks yet.'));
        panel.appendChild(E.Button({ label: 'Add check', icon: 'plus', id: 'pmt-addrule', onClick: function () { ctx.openDialog('add-rule'); } }));
      }
      function versions(panel) {
        var rows = []; for (var v = Math.max(t.version, 1); v >= 1; v--) rows.push({ v: v, state: v === t.version ? 'Published' : 'Archived', date: '2026-0' + Math.max(1, 9 - (t.version - v) * 2) + '-0' + v, by: t.by, note: ['First version', 'Added “Sub-period” columns', 'Monthly volumes grouped', 'Reporting forms moved to the header'][v % 4] });
        panel.appendChild(h('div', { class: 'pm-tw' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Version', 'State', 'Date', 'Author', 'What changed'].map(function (x) { return h('th', {}, x); }))), h('tbody', {}, rows.map(function (r) { return h('tr', {}, h('td', { class: 'mono' }, 'v' + r.v), h('td', {}, E.StatusBadge('version', r.state, { quiet: true })), h('td', {}, F.date(r.date)), h('td', {}, r.by), h('td', {}, r.note)); })))));
        panel.appendChild(note('Published versions are never edited. A permit keeps working with the version it was created in until you move it.'));
      }
      ctx.dialog('add-table', function () {
        var f = {}, box;
        var d = { name: '', ru: '', from: 'blank', copy: t.tables[0] ? t.tables[0].code : '', cols: [col('Name', 'Name', '', 'Text', { req: true, key: true })] };
        function colRows() {
          box.textContent = '';
          d.cols.forEach(function (c, i) {
            box.appendChild(h('div', { class: 'form-grid', style: 'grid-template-columns:minmax(0,2fr) minmax(0,1.3fr) minmax(0,1fr) auto auto auto;align-items:end' },
              E.Input({ id: 'pmt-nt-n' + i, label: 'Column ' + (i + 1), value: c.en, onInput: function (v) { c.en = v; c.code = v.replace(/[^A-Za-z0-9]+/g, '_'); } }),
              E.Select({ id: 'pmt-nt-t' + i, label: 'Type', options: TYPES_LIST, value: c.type, onChange: function (v) { c.type = v; } }),
              E.Input({ id: 'pmt-nt-u' + i, label: 'Unit', mono: true, value: c.unit || '', onInput: function (v) { c.unit = v; } }),
              E.Checkbox({ id: 'pmt-nt-r' + i, label: 'Required', checked: c.req, onChange: function (v) { c.req = v; } }),
              E.Checkbox({ id: 'pmt-nt-k' + i, label: 'Key', checked: c.key, onChange: function (v) { c.key = v; } }),
              E.Button({ label: 'Remove column', icon: 'trash', iconOnly: true, variant: 'ghost', disabled: d.cols.length < 2, onClick: function () { d.cols.splice(i, 1); colRows(); } })));
          });
        }
        E.Wizard({ id: 'pmt-addtable-wz', title: 'Add a table to ' + t.name, wide: true, data: d,
          steps: [
            { id: 'basics', label: 'Table', hint: 'A table is one list of rows inside a permit: limits, volumes, storage places.', render: function (b, dd) {
                f.name = E.Input({ id: 'pmt-nt-name', label: 'Name (English)', required: true, value: dd.name, placeholder: 'Monitoring points', onInput: function (v) { dd.name = v; } });
                f.ru = E.Input({ id: 'pmt-nt-ru', label: 'Name (Russian)', value: dd.ru, onInput: function (v) { dd.ru = v; } });
                var src = E.Select({ id: 'pmt-nt-from', label: 'Start from', options: [{ value: 'blank', label: 'One empty column, I add the rest' }, { value: 'excel', label: 'The header row of an Excel file' }, { value: 'copy', label: 'A copy of another table of this type' }], value: dd.from, onChange: function (v) { dd.from = v; dd.copied = false; dd.imported = false; } });
                b.appendChild(h('div', { class: 'form-grid' }, f.name, f.ru, h('div', { class: 'span-2' }, src)));
              }, validate: function (dd) { f.name.setError(null); if (!dd.name.trim()) { f.name.setError('Enter a name'); f.name.input.focus(); return 'A table needs a name.'; }
                if (t.tables.some(function (x) { return x.name.toLowerCase() === dd.name.trim().toLowerCase(); })) { f.name.setError('This type already has a table with this name'); return 'Choose another name.'; } return null; } },
            { id: 'cols', label: 'Columns', hint: 'As many as the permit needs. Mark the columns that make a row unique as Key.', render: function (b, dd) {
                if (dd.from === 'copy' && !dd.copied) { var src = find(t.tables, 'code', dd.copy); if (src) dd.cols = JSON.parse(JSON.stringify(src.cols)); dd.copied = true; }
                if (dd.from === 'excel' && !dd.imported) { dd.cols = ['Water body', 'Source code', 'Quality code', 'Annual volume'].map(function (n, i) { return col(n.replace(/ /g, '_'), n, '', i === 3 ? 'Number' : 'Text', { key: i === 0, req: i === 0 }); }); dd.imported = true; b.appendChild(E.Banner({ tone: 'info', title: 'Columns read from Permits_Template.xlsx', text: '4 header cells found. Types were guessed from the first rows, so check them.' })); }
                box = h('div', { class: 'stack' }); b.appendChild(box); colRows();
                b.appendChild(h('div', { class: 'row' }, E.Button({ label: 'Add column', icon: 'plus', id: 'pmt-nt-add', onClick: function () { d.cols.push(col('Column_' + (d.cols.length + 1), '', '', 'Text')); colRows(); } }),
                  E.Button({ label: 'Add months January to December', icon: 'calendar', onClick: function () { d.cols = d.cols.concat(monthCols(VOL, 'Estimated volume by month')); colRows(); } })));
              }, validate: function (dd) { if (dd.cols.some(function (c) { return !c.en.trim(); })) return 'Every column needs a name.'; if (!dd.cols.some(function (c) { return c.key; })) return 'Mark at least one column as Key, so rows of one permit cannot repeat.'; return null; } }],
          summary: function (dd) { return E.KeyValue([{ label: 'Table', value: dd.name }, { label: 'Columns', value: dd.cols.length + ': ' + dd.cols.slice(0, 5).map(function (c) { return c.en; }).join(', ') + (dd.cols.length > 5 ? '…' : '') }, { label: 'Unique row', value: 'Permit + ' + dd.cols.filter(function (c) { return c.key; }).map(function (c) { return c.en; }).join(' + ') }, { label: 'Stored as', value: (t.regCode + '_' + dd.name.replace(/[^A-Za-z0-9]+/g, '_')).toUpperCase(), mono: true }], { wide: true }); },
          reviewText: 'The table is added to the draft of this type. Existing permits get it empty after you publish.', applyLabel: 'Add table',
          onApply: function (dd, api) { var code = dd.name.replace(/[^A-Za-z0-9 ]/g, '').replace(/(?:^| )(\w)/g, function (m, c) { return c.toUpperCase(); });
            t.tables.push({ code: code, name: dd.name.trim(), ru: dd.ru, regCode: (t.regCode + '_' + code).toUpperCase(), cols: dd.cols, rules: [] }); t.state = 'Draft'; api.close(); reload(ctx, { t: code }); } });
      });
      ctx.dialog('add-column', function () {
        var d = { en: '', type: 'Text' }, f = {};
        E.Dialog({ id: 'pmt-addcol-dlg', title: 'Add a column', body: function (b) {
            f.en = E.Input({ id: 'pmt-ac-en', label: 'Name (English)', required: true, onInput: function (v) { d.en = v; } });
            f.type = E.Select({ id: 'pmt-ac-type', label: 'Type', options: TYPES_LIST, value: 'Text', onChange: function (v) { d.type = v; } });
            f.unit = E.Input({ id: 'pmt-ac-unit', label: 'Unit', mono: true, hint: 'For numbers, e.g. t·yr⁻¹', onInput: function (v) { d.unit = v; } });
            f.req = E.Switch({ id: 'pmt-ac-req', label: 'Required', checked: false, onChange: function (v) { d.req = v; } });
            b.appendChild(h('div', { class: 'form-grid' }, f.en, f.type, f.unit, f.req)); },
          footer: [{ label: 'Cancel' }, { label: 'Add column', variant: 'primary', icon: 'plus', keepOpen: true, onClick: function (ctl) { if (!d.en.trim()) { f.en.setError('Enter a name'); f.en.input.focus(); return false; }
            var cur = find(t.tables, 'code', sel) || t.tables[0]; if (cur) { cur.cols.push(col(d.en.replace(/[^A-Za-z0-9]+/g, '_'), d.en.trim(), '', d.type, { unit: d.unit || null, req: !!d.req })); t.state = 'Draft'; }
            ctl.close(); reload(ctx); } }] }); });
      ctx.dialog('add-series', function () {
        E.ConfirmDialog({ id: 'pmt-series', title: 'Add 12 month columns?', consequences: ['January … December are added as number columns in a group “Estimated volume by month”.', { text: 'Unit: ' + VOL + ' — change it for the whole group afterwards.', note: true }, { text: 'A check “months add up to the annual value” can be added on the Checks tab.', note: true }], verb: 'Add 12 columns', icon: 'calendar', danger: false,
          onConfirm: function () { var cur = find(t.tables, 'code', sel) || t.tables[0]; if (cur) { cur.cols = cur.cols.concat(monthCols(VOL, 'Estimated volume by month')); t.state = 'Draft'; } reload(ctx); } }); });
      ctx.dialog('delete-table', function () {
        var cur = find(t.tables, 'code', sel) || t.tables[0]; if (!cur) return;
        var rowsN = 0; PERMITS.forEach(function (p) { if (p.type === t.code) rowsN += ((p.total && p.total[cur.code]) || [(p.rows[cur.code] || []).length])[0]; });
        var binds = BINDINGS.filter(function (b) { return b.type === t.code && b.tbl === cur.code; });
        if (rowsN || binds.length) {
          E.Dialog({ id: 'pmt-del-blocked', title: '“' + cur.name + '” cannot be deleted yet', alert: true, body: function (b) {
              b.appendChild(h('ul', { class: 'conseq' }, [rowsN ? h('li', {}, icon('xCircle', 14), F.int(rowsN) + ' rows in permits of this type still use it.') : null, binds.length ? h('li', {}, icon('xCircle', 14), 'Read by ' + F.plural(binds.length, { one: 'sheet binding', other: 'sheet bindings' }) + ': ' + binds.map(function (x) { return x.target; }).join(', ') + '.') : null, h('li', { class: 'note' }, icon('info', 14), 'Nothing is lost by waiting: remove the rows and bindings first, then delete the table.')]));
            }, footer: [{ label: 'Close', variant: 'primary' }] }); return;
        }
        E.ConfirmDialog({ id: 'pmt-del', title: 'Delete table “' + cur.name + '”?', consequences: ['The table and its ' + cur.cols.length + ' columns are removed from the draft of ' + t.name + '.', { text: 'No permit holds rows in it and no sheet reads it.', note: true }], verb: 'Delete table', icon: 'trash',
          onConfirm: function () { t.tables.splice(t.tables.indexOf(cur), 1); t.state = 'Draft'; reload(ctx, { t: null }); } });
      });
      ctx.dialog('add-rule', function () {
        var d = { name: '', text: '', where: '__permit', mode: 'Warn' }, f = {};
        E.Dialog({ id: 'pmt-rule-dlg', title: 'Add a check', wide: true, body: function (b) {
            f.name = E.Input({ id: 'pmt-rule-name', label: 'What it checks', required: true, placeholder: 'Months add up to the annual volume', onInput: function (v) { d.name = v; } });
            f.where = E.Select({ id: 'pmt-rule-where', label: 'Applies to', options: [{ value: '__permit', label: 'The whole permit' }].concat(t.tables.map(function (x) { return { value: x.code, label: 'Table: ' + x.name }; })), value: '__permit', onChange: function (v) { d.where = v; } });
            f.text = E.Textarea({ id: 'pmt-rule-text', label: 'Rule', rows: 3, required: true, hint: 'True means the data is fine. Use column codes, e.g. ROW.Annual >= 0. The same expression editor as in Registries.', onInput: function (v) { d.text = v; } });
            f.mode = E.Segmented({ id: 'pmt-rule-mode', label: 'If it fails', value: 'Warn', options: [{ value: 'Warn', label: 'Warn' }, { value: 'Block', label: 'Block saving' }], onChange: function (v) { d.mode = v; } });
            b.appendChild(h('div', { class: 'form-grid' }, h('div', { class: 'span-2' }, f.name), f.where, f.mode, h('div', { class: 'span-2' }, f.text))); },
          footer: [{ label: 'Cancel' }, { label: 'Add check', variant: 'primary', icon: 'plus', keepOpen: true, onClick: function (ctl) {
            f.name.setError(null); if (!d.name.trim()) { f.name.setError('Describe the check'); f.name.input.focus(); return false; }
            if (!d.text.trim()) { E.Toast('Write the rule expression', 'warning'); return false; }
            var rule = { name: d.name.trim(), text: d.text.trim(), mode: d.mode }; if (d.where === '__permit') t.rules.push(rule); else find(t.tables, 'code', d.where).rules.push(rule);
            t.state = 'Draft'; ctl.close(); reload(ctx, { tab: 'rules' }); } }] }); });
      ctx.dialog('publish-type', function () {
        if (!t.tables.length) {
          E.Dialog({ id: 'pmt-pub-blocked', title: 'Add a table before publishing', alert: true, body: function (b) { b.appendChild(note('A permit type without tables has only the permit details, so there is nothing to fill in. Add at least one table with its columns.')); },
            footer: [{ label: 'Cancel' }, { label: 'Add table…', variant: 'primary', icon: 'plus', onClick: function () { later(function () { ctx.openDialog('add-table'); }); } }] }); return;
        }
        var used = PERMITS.filter(function (p) { return p.type === t.code; }).length;
        E.ReasonDialog({ id: 'pmt-publish-dlg', title: 'Publish “' + t.name + '” v' + (t.version + 1) + '?', text: used ? used + ' existing permits move to the new version. Added columns are empty in old rows; removed columns are hidden, not deleted.' : 'No permits use this type yet.', label: 'What changed', minLength: 10, verb: 'Publish', icon: 'send',
          onSubmit: function () { t.version++; t.state = 'Published'; t.updated = TODAY; reload(ctx); } }); });
    } });

  /* ================= 4a. Списки вибору ================= */
  E.screen('/admin/permits/lists', { title: 'Permit lists', icon: 'rows4', navPath: '/admin/permits', render: function (el, ctx) {
    var rows = ctx.state === 'empty' ? [] : listRows();
    E.ListPage(el, ctx, { id: 'pml', rows: rows, banner: h('div', { class: 'pm-slot' }, subnav('/admin/permits/lists')),
      header: { title: 'Lists', back: false,
        subtitle: 'The pick-lists behind permit columns. A list can be shared by many permit types, belong to one type only, or be a table of the permit itself. Add values here or straight from a row form.',
        secondary: [{ label: 'Open Registries', icon: 'database', onClick: function () { E.go('/admin/registries'); } }] },
      stats: [{ id: 'all', label: 'lists', value: function (r) { return r.length; } }, { id: 'shared', label: 'shared', match: function (r) { return r.scope === 'shared'; } }, { id: 'type', label: 'one type only', match: function (r) { return r.scope === 'type'; } }, { id: 'permit', label: 'from a permit table', match: function (r) { return r.scope === 'permit'; } }],
      search: { placeholder: 'List or permit type', text: function (r) { return r.name + ' ' + r.owner; } },
      filters: [{ id: 'scope', label: 'Kinds', options: ['Shared registry', 'This permit type only', 'Table of the same permit'], match: function (r, v) { return SCOPE_LABEL[r.scope] === v; } }],
      table: { rowKey: 'id', tall: true, rowLabel: function (r) { return r.name; }, columns: [
        { key: 'name', label: 'List', render: function (r) { return E.twoLine(r.name, r.scope === 'shared' ? 'Kept in Registries' : 'Kept in ' + r.owner, { mono: false }); } },
        { key: 'scope', label: 'Kind', render: function (r) { return SCOPE_LABEL[r.scope]; }, sortValue: function (r) { return r.scope; } },
        { key: 'entries', label: 'Values', num: true, render: function (r) { return r.scope === 'permit' ? 'rows of each permit' : F.int(r.entries); } },
        { key: 'used', label: 'Used by', hideSm: true, sortValue: function (r) { return r.used.length; }, render: function (r) { return r.used.length ? F.plural(r.used.length, { one: 'column', other: 'columns' }) : 'No column yet'; } }] },
      empty: { icon: 'rows4', title: 'No lists yet', text: 'Lists appear when a permit column is set to pick from a registry or a list.' },
      error: { code: 'ECR-PRM-0503' },
      drawer: function (r) { return { title: r.name, subtitle: SCOPE_LABEL[r.scope] + (r.scope === 'shared' ? '' : ' · ' + r.owner),
        body: function (b) {
          b.appendChild(E.KeyValue([{ label: 'Kind', value: SCOPE_LABEL[r.scope] }, { label: 'Used by', value: r.used.length ? h('span', { class: 'stack gap-1' }, r.used.map(function (u) { return h('span', {}, u); })) : 'No column yet' }]));
          if (r.scope === 'shared') {
            b.appendChild(note('Shared lists are registries. Every permit type that picks from this list sees the same values, and they can be used in sheets and formulas too.'));
            b.appendChild(chips(LISTS[r.ref].slice(0, 12)));
          } else if (r.scope === 'type') {
            b.appendChild(note('Only ' + r.owner + ' sees these values. One value per line.'));
            var ta = E.Textarea({ id: 'pml-values', label: 'Values', rows: 8, onInput: function () {} }); ta.value = choicesOf(r.col).join('\n'); b.appendChild(ta); r.ta = ta;
          } else {
            b.appendChild(note('The choices are the rows of a table inside each permit. Each permit offers only its own rows, so the list differs from permit to permit.'));
          }
        },
        footer: r.scope === 'shared' ? [{ label: 'Add value…', icon: 'plus', keepOpen: true, onClick: function () { valueDialog('Add a value to ' + r.name, 'It is added to the shared registry, so every permit type that uses it will see it.', function (v) { LISTS[r.ref].push(v); r.entries++; ctx.refresh(); }); } }, { label: 'Open in Registries', icon: 'arrowR', variant: 'primary', onClick: function () { E.go('/admin/registries'); } }]
          : r.scope === 'type' ? [{ label: 'Close' }, { label: 'Save values', variant: 'primary', icon: 'check', onClick: function () { r.col.choices = r.ta.value.split(/\r?\n/).filter(function (x) { return x.trim(); }); r.entries = r.col.choices.length; E.Toast('Values saved', 'success'); } }]
          : [{ label: 'Open the permit type', icon: 'arrowR', variant: 'primary', onClick: function () { E.go('/admin/permits/types/' + r.t.code); } }] }; }
    });
  } });

  /* ================= 5. Привʼязки: перелік ================= */
  var BINDINGS = [
    { id: 'B-01', template: 'GEN07131100 v1.0.0', sheet: 'Air emissions', target: 'Emission sources by substance', type: 'AirPermit', tbl: 'AirEmissions', mode: 'Rows from the permit', pick: 'Permit valid at period end, same location', state: 'Published', cols: 5, sheetTables: 91 },
    { id: 'B-02', template: 'GEN07131100 v1.0.0', sheet: 'Waste', target: 'Waste accumulation', type: 'WastePermit', tbl: 'WasteLimits', mode: 'Rows from the permit', pick: 'Permit valid at period end, same location', state: 'Published', cols: 4, sheetTables: 37 },
    { id: 'B-03', template: 'GEN07131100 v1.0.0', sheet: 'Water use & discharge', target: 'Discharge to water bodies', type: 'WaterDischarge', tbl: 'Discharge', mode: 'Rows from the permit', pick: 'Permit valid at period end, same location', state: 'Published', cols: 3, sheetTables: 24 },
    { id: 'B-04', template: 'GEN07131100 v1.1.0', sheet: 'Water use & discharge', target: 'Intake from water bodies', type: 'WaterIntake', tbl: 'Intake', mode: 'Rows from the permit', pick: 'Permit valid at period end, same location', state: 'Draft', cols: 3, sheetTables: 24 },
    { id: 'B-05', template: 'GEN07131100 v1.0.0', sheet: 'Air emissions', target: 'Permit totals', type: 'AirPermit', tbl: 'AirEmissions', mode: 'Formulas only', pick: 'Chosen in a cell on the sheet', state: 'Published', cols: 0, sheetTables: 91 }];
  function binding(id) { return find(BINDINGS, 'id', id) || BINDINGS[0]; }
  /* Зіставлення стовпців за замовчуванням: ключі рядка + одна колонка-ліміт (за відомими кодами). */
  var LIMIT_CODES = ['Emission_t_year', 'Permitted_t_year', 'Annual', 'Permissible_mg_dm3'];
  function defaultMap(tb) {
    var m = tb.cols.filter(function (c) { return c.key && !c.via; }).map(function (c) { return { col: c.code, sheet: c.en, role: 'Row key' }; });
    var lim = tb.cols.filter(function (c) { return LIMIT_CODES.indexOf(c.code) >= 0; })[0];
    if (lim) m.push({ col: lim.code, sheet: lim.en + ' limit', role: 'Limit' });
    return m;
  }
  function mapOf(b, tb) { return b.map || (b.map = defaultMap(tb)); }

  E.screen('/admin/permits/bindings', { title: 'Sheet bindings', icon: 'link', navPath: '/admin/permits', render: function (el, ctx) {
    var rows = ctx.state === 'empty' ? [] : BINDINGS;
    E.ListPage(el, ctx, { id: 'pmb', rows: rows, banner: h('div', { class: 'pm-slot' }, subnav('/admin/permits/bindings')),
      header: { title: 'Sheet bindings', back: false,
        subtitle: 'Tell a sheet table which permit table to read. Its rows and limits then come from the permit that is valid in the period — people only enter actual values.',
        primary: { label: 'New binding', icon: 'plus', id: 'pmb-new', onClick: function () { ctx.openDialog('new-binding'); } } },
      stats: [{ id: 'all', label: 'bindings', value: function (r) { return r.length; } }, { id: 'rows', label: 'fill rows', match: function (b) { return b.mode === 'Rows from the permit'; } }, { id: 'draft', label: 'in a draft template', tone: 'warning', match: function (b) { return b.state === 'Draft'; } }],
      search: { placeholder: 'Sheet, table or permit type', text: function (b) { return b.sheet + ' ' + b.target + ' ' + type(b.type).name; } },
      filters: [{ id: 'mode', label: 'Modes', options: ['Rows from the permit', 'Formulas only'] }],
      onRow: function (b) { E.go('/admin/permits/bindings/' + b.id); },
      table: { rowKey: 'id', tall: true, rowLabel: function (b) { return b.target; }, columns: [
        { key: 'target', label: 'Sheet table', render: function (b) { return E.twoLine(b.target, b.sheet + ' · ' + b.template, { mono: false }); } },
        { key: 'type', label: 'Reads', render: function (b) { return E.twoLine(table(type(b.type), b.tbl).name, type(b.type).name, { mono: false }); }, sortValue: function (b) { return type(b.type).name; } },
        { key: 'mode', label: 'How' }, { key: 'pick', label: 'Which permit', hideSm: true },
        { key: 'state', label: 'Template', render: function (b) { return E.StatusBadge('version', b.state, { quiet: true }); } }] },
      empty: { icon: 'link', title: 'No bindings yet', text: 'A binding connects a sheet table to a permit table. Create one to see limits next to the figures people enter.', actions: [{ label: 'New binding', variant: 'primary', icon: 'plus', onClick: function () { ctx.openDialog('new-binding'); } }] },
      error: { code: 'ECR-PRM-0503' } });
    ctx.dialog('new-binding', function () {
      var d = { sheet: 'Air emissions', target: '', type: 'AirPermit', tbl: 'AirEmissions', mode: 'Rows from the permit', pick: 'Permit valid at period end, same location' };
      E.Wizard({ id: 'pmb-wz', title: 'Bind a sheet table to a permit', data: d, wide: true,
        steps: [
          { id: 'target', label: 'Sheet table', hint: 'The table in the template that should read a permit. Bindings belong to a template version.', render: function (b, dd) {
              b.appendChild(h('div', { class: 'form-grid' }, E.Select({ id: 'pmb-t-tpl', label: 'Template', options: ['GEN07131100 v1.1.0 (draft)', 'GEN07131100 v1.0.0'], value: 'GEN07131100 v1.1.0 (draft)' }),
                E.Select({ id: 'pmb-t-sheet', label: 'Sheet', options: D.sheetNames, value: dd.sheet, onChange: function (v) { dd.sheet = v; } }),
                h('div', { class: 'span-2' }, E.Input({ id: 'pmb-t-tbl', label: 'Table on the sheet', required: true, placeholder: 'Emission sources by substance', value: dd.target, onInput: function (v) { dd.target = v; } }))));
            }, validate: function (dd) { return dd.target.trim() ? null : 'Name the sheet table.'; } },
          { id: 'src', label: 'Permit table', hint: 'Which permit type, and which of its tables.', render: function (b, dd) {
              var tsel = E.Select({ id: 'pmb-s-tbl', label: 'Table', options: type(dd.type).tables.map(function (x) { return { value: x.code, label: x.name + ' (' + x.cols.length + ' columns)' }; }), value: dd.tbl, onChange: function (v) { dd.tbl = v; } });
              function refillTables() { var s = tsel.querySelector('select'); s.textContent = ''; type(dd.type).tables.forEach(function (x) { s.appendChild(h('option', { value: x.code }, x.name + ' (' + x.cols.length + ' columns)')); }); s.value = dd.tbl; }
              b.appendChild(h('div', { class: 'form-grid' }, E.Select({ id: 'pmb-s-type', label: 'Permit type', options: TYPES.filter(function (t) { return t.version > 0; }).map(function (t) { return { value: t.code, label: t.name }; }), value: dd.type, onChange: function (v) { dd.type = v; dd.tbl = type(v).tables[0].code; refillTables(); } }), tsel,
                h('div', { class: 'span-2' }, E.Segmented({ id: 'pmb-s-mode', label: 'How the sheet uses it', value: dd.mode, options: ['Rows from the permit', 'Formulas only'], onChange: function (v) { dd.mode = v; } })),
                h('div', { class: 'span-2' }, note('“Rows from the permit”: the sheet table gets one row per permit row, with limits locked next to the cells people fill. “Formulas only”: no rows are added; cells use functions such as REGSUM to read the permit.'))));
            } },
          { id: 'pick', label: 'Which permit', hint: 'Many permits of one type can exist. Say how the sheet finds the right one.', render: function (b, dd) {
              b.appendChild(h('div', { class: 'stack' }, ['Permit valid at period end, same location', 'Permit chosen in a cell on the sheet'].map(function (x, i) {
                return E.Field({ id: 'pmb-p-' + i, label: x, hint: i === 0 ? 'Takes the active permit of this type for the document’s facility; the one with the later start wins.' : 'Adds a “Permit” pick-list cell to the sheet header, for sites with several permits of one type.', control: h('input', { type: 'radio', name: 'pmb-p', id: 'pmb-p-' + i, checked: dd.pick === x, on: { change: function () { dd.pick = x; } } }) }); })));
            } },
          { id: 'map', label: 'Columns', hint: 'Match sheet columns to permit columns. Locked columns show the permit’s value and cannot be edited on the sheet.', render: function (b, dd) {
              var t = type(dd.type), tb = table(t, dd.tbl), dm = defaultMap(tb);
              if (dd.mode === 'Formulas only') { b.appendChild(note('In “Formulas only” mode no sheet rows are added, so there is nothing to map. Cells read the permit with REGSUM, REGFIND and REGFIELD.')); return; }
              b.appendChild(note('This is the suggested mapping; you can change it later with “Edit mapping”.'));
              b.appendChild(h('div', { class: 'pm-tw' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Sheet column', '', 'Permit column', 'Role'].map(function (x) { return h('th', {}, x); }))),
                h('tbody', {}, dm.map(function (m) { var c = find(tb.cols, 'code', m.col); return h('tr', {}, h('td', {}, m.sheet), h('td', {}, icon('arrowL', 14)), h('td', {}, c.en), h('td', {}, m.role)); }).concat([h('tr', {}, h('td', {}, 'Actual (entered by people)'), h('td', {}), h('td', { class: 'faint' }, 'not from the permit'), h('td', {}, 'Entered'))])))));
            } }],
        summary: function (dd) { return E.KeyValue([{ label: 'Sheet table', value: dd.sheet + ' · ' + dd.target }, { label: 'Reads', value: type(dd.type).name + ' → ' + table(type(dd.type), dd.tbl).name }, { label: 'How', value: dd.mode }, { label: 'Which permit', value: dd.pick }, { label: 'Effect', value: 'Applies to the draft template; documents get it after the template is published' }], { wide: true }); },
        reviewText: 'The binding is saved in the draft template version. Existing documents are not touched.', applyLabel: 'Create binding',
        onApply: function (dd, api) { var id = 'B-0' + (BINDINGS.length + 1); BINDINGS.push({ id: id, template: 'GEN07131100 v1.1.0', sheet: dd.sheet, target: dd.target, type: dd.type, tbl: dd.tbl, mode: dd.mode, pick: dd.pick, state: 'Draft', cols: 3, sheetTables: 24 }); api.close(); later(function () { E.go('/admin/permits/bindings/' + id, { toast: 'Binding created in the draft template' }); }); } });
    });
    ctx.action('New binding', 'plus', function () { ctx.openDialog('new-binding'); });
  } });

  /* ================= 6. Привʼязка: деталі й перегляд аркуша ================= */
  E.screen('/admin/permits/bindings/:id', { title: 'Sheet binding', icon: 'link', navPath: '/admin/permits', example: '/admin/permits/bindings/B-01',
    crumbs: function (ctx) { var b = binding(ctx.params.id); return [{ label: 'Configure' }, { label: 'Permits', href: '#/admin/permits' }, { label: 'Sheet bindings', href: '#/admin/permits/bindings' }, { label: b.target }]; },
    render: function (el, ctx) {
      var b = binding(ctx.params.id), t = type(b.type), tb = table(t, b.tbl), p = PERMITS.filter(function (x) { return x.type === b.type && x.state === 'Active'; })[0] || PERMITS.filter(function (x) { return x.type === b.type; })[0] || PERMITS[0];
      el.appendChild(h('div', { class: 'editor-head' },
        h('div', { class: 'eh-title' }, h('a', { class: 'ph-back', href: '#/admin/permits/bindings', 'data-back': '1' }, icon('arrowL', 14), 'Back to Sheet bindings'), h('h1', { tabindex: '-1', 'data-page-title': '1', id: 'pmb-h1' }, b.target), h('span', { class: 'muted' }, b.sheet + ' · ' + b.template)),
        h('div', { class: 'eh-ctx' }, E.StatusBadge('version', b.state, { label: b.state === 'Draft' ? 'Draft template' : 'Published template' }), h('div', { class: 'eh-actions' }, E.Button({ label: 'Open the template', icon: 'external', onClick: function () { E.go('/admin/templates'); } })))));
      var body = h('div', { class: 'pm-body' }); el.appendChild(body);
      body.appendChild(h('div', { class: 'pm-arrow' }, E.CodeText(b.sheet + ' › ' + b.target), icon('arrowL', 16), E.CodeText(t.name + ' › ' + tb.name), h('span', { class: 'muted' }, b.mode.toLowerCase() + ' · ' + b.pick.toLowerCase())));
      if (b.mode === 'Formulas only') {
        body.appendChild(h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Formulas the sheet can use')), note('Cells of this table call the permit with the same functions used for registries. The permit that applies is picked in the header cell [Permit].'),
          E.CodeText("REGSUM('" + tb.regCode + "', ROW.PERMIT = [Permit], ROW." + ((tb.cols.filter(function (c) { return LIMIT_CODES.indexOf(c.code) >= 0; })[0] || { code: 'value' }).code.toUpperCase()) + ")\nREGFIELD([Permit], 'END_DATE')\nREGCOUNT('" + tb.regCode + "', ROW.PERMIT = [Permit])", { block: true })));
        return;
      }
      var map = mapOf(b, tb);
      function colOf(code) { return find(tb.cols, 'code', code); }
      var ROLE_NOTE = { 'Row key': 'Locked, one sheet row per permit row', 'Limit': 'Locked, actuals are compared with it', 'Info': 'Locked, shown for reference' };
      body.appendChild(h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Column mapping'), h('span', { class: 'sp' }), E.Button({ label: 'Edit mapping', icon: 'pencil', size: 'sm', id: 'pmb-editmap', onClick: function () { ctx.openDialog('edit-mapping'); } })),
        h('div', { class: 'pm-tw' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Sheet column', '', 'Permit column', 'Role', 'On the sheet'].map(function (x) { return h('th', {}, x); }))),
          h('tbody', {}, map.map(function (m) { var c = colOf(m.col) || { en: m.col }; return h('tr', {}, h('td', {}, m.sheet), h('td', {}, icon('arrowL', 14)), h('td', {}, c.en + (c.unit ? ' (' + c.unit + ')' : '')), h('td', {}, m.role), h('td', {}, ROLE_NOTE[m.role])); })
            .concat([h('tr', {}, h('td', {}, 'Actual'), h('td', {}), h('td', { class: 'faint' }, 'not from the permit'), h('td', {}, 'Entered'), h('td', {}, 'Typed by people or filled from PI')), h('tr', {}, h('td', {}, 'Used, %'), h('td', {}), h('td', { class: 'faint' }, 'calculated'), h('td', {}, 'Formula'), h('td', {}, E.CodeText('Actual / Limit * 100')))]))))));
      ctx.dialog('edit-mapping', function () {
        var draft = tb.cols.filter(function (c) { return !c.via; }).map(function (c) { var m = find(map, 'col', c.code); return { c: c, use: !!m, sheet: m ? m.sheet : c.en, role: m ? m.role : 'Info' }; });
        E.Dialog({ id: 'pmb-map-dlg', title: 'Edit column mapping', wide: true, body: function (bd) {
            bd.appendChild(note('Tick the permit columns the sheet table should show. Row key columns say which permit row a sheet row belongs to. The Limit column is what actuals are compared with. Everything taken from the permit is locked on the sheet.'));
            bd.appendChild(h('div', { class: 'pm-tw', style: 'max-height:340px' }, h('table', { class: 'pm-t' }, h('thead', {}, h('tr', {}, ['Use', 'Permit column', 'Sheet column', 'Role'].map(function (x) { return h('th', {}, x); }))),
              h('tbody', {}, draft.map(function (d, i) { return h('tr', {},
                h('td', {}, h('input', { type: 'checkbox', id: 'pmb-m-u' + i, checked: d.use, 'aria-label': 'Use ' + d.c.en, on: { change: function (e) { d.use = e.target.checked; } } })),
                h('td', {}, d.c.en + (d.c.unit ? ' (' + d.c.unit + ')' : '')),
                h('td', {}, h('input', { class: 'input', id: 'pmb-m-s' + i, value: d.sheet, 'aria-label': 'Sheet column for ' + d.c.en, on: { input: function (e) { d.sheet = e.target.value; } } })),
                h('td', {}, h('select', { class: 'select', id: 'pmb-m-r' + i, 'aria-label': 'Role of ' + d.c.en, on: { change: function (e) { d.role = e.target.value; } } }, ['Row key', 'Limit', 'Info'].map(function (r) { return h('option', { value: r, selected: d.role === r }, r); })))); })))));
          },
          footer: [{ label: 'Cancel' }, { label: 'Save mapping', variant: 'primary', icon: 'check', keepOpen: true, onClick: function (ctl) {
            var used = draft.filter(function (d) { return d.use; });
            if (!used.some(function (d) { return d.role === 'Row key'; })) { E.Toast('Mark at least one column as Row key', 'warning'); return false; }
            if (used.filter(function (d) { return d.role === 'Limit'; }).length > 1) { E.Toast('Only one column can be the Limit', 'warning'); return false; }
            b.map = used.map(function (d) { return { col: d.c.code, sheet: d.sheet.trim() || d.c.en, role: d.role }; }); ctl.close(); reload(ctx); E.Toast('Mapping saved in the draft template', 'success'); } }] });
      });
      /* Попередній перегляд: як це бачить користувач у документі (для будь-якої привʼязаної таблиці) */
      var R = rng(77), prows = rowsOf(p, tb).slice(0, 6);
      var keyMap = map.filter(function (m) { return m.role === 'Row key'; }), infoMap = map.filter(function (m) { return m.role === 'Info'; }), limMap = map.filter(function (m) { return m.role === 'Limit'; })[0], limCol = limMap ? colOf(limMap.col) : null;
      function cellText(m, r) { var c = colOf(m.col); return c ? (fmtCell(c, r[c.code]) || '—') : ''; }
      var data = prows.map(function (r, i) { var lim = limCol && typeof r[limCol.code] === 'number' ? r[limCol.code] : null; var act = lim != null ? +(lim * (i === 2 ? 1.18 : 0.1 + R() * 0.9)).toFixed(5) : null; return { r: r, lim: lim, act: act, pct: lim ? act / lim * 100 : null }; });
      var unit = limCol && limCol.unit ? limCol.unit : '';
      function bodyRow(d, i) {
        var tds = [h('th', { class: 'rh', scope: 'row' }, h('span', { class: 'cc' }, String(i + 1)), keyMap.map(function (m) { return cellText(m, d.r); }).join(' · '))];
        infoMap.forEach(function (m) { tds.push(h('td', { class: 'c is-locked' }, h('span', { class: 'v' }, cellText(m, d.r)))); });
        if (limCol) tds.push(h('td', { class: 'c is-locked' }, h('span', { class: 'v' }, d.lim != null ? F.number(d.lim, 5) : '—')));
        tds.push(h('td', { class: 'c' + (d.pct != null && d.pct > 100 ? ' is-warn' : '') }, h('span', { class: 'v' }, d.act != null ? F.number(d.act, 5) : '')));
        if (limCol) tds.push(h('td', { class: 'c is-calc' }, h('span', { class: 'v' }, d.pct != null ? F.number(d.pct, 1) : '—')));
        return h('tr', {}, tds);
      }
      var headCells = infoMap.map(function (m) { return h('th', {}, h('div', { class: 'ch' }, h('span', { class: 'cn' }, m.sheet))); });
      if (limCol) headCells.push(h('th', { class: 'is-calc' }, h('div', { class: 'ch' }, h('span', { class: 'cn' }, limMap.sheet)), h('div', { class: 'cu' }, unit)));
      headCells.push(h('th', {}, h('div', { class: 'ch' }, h('span', { class: 'cn' }, 'Actual')), h('div', { class: 'cu' }, unit)));
      if (limCol) headCells.push(h('th', { class: 'is-calc' }, h('div', { class: 'ch' }, h('span', { class: 'cn' }, 'Used, %')), h('div', { class: 'cu' }, '%')));
      var grid = h('table', { class: 'grid' }, h('thead', {}, h('tr', {}, [h('th', { class: 'rh' }, keyMap.map(function (m) { return m.sheet; }).join(' · '))].concat(headCells))), h('tbody', {}, data.map(bodyRow)));
      body.appendChild(h('section', { class: 'section' }, h('div', { class: 'section-h' }, h('h2', {}, 'Preview as a user sees it'), h('span', { class: 'sp' }), h('span', { class: 'muted' }, 'September 2026 · permit ' + p.number)),
        p.state === 'Draft' ? E.Banner({ tone: 'info', title: 'This permit is still a draft', text: 'The sheet will read it after it is activated. The preview shows how it will look.', actions: [{ label: 'Open the permit', href: E.href('/admin/permits/' + p.id) }] }) : null,
        h('div', { class: 'pm-gridnote' }, icon('info', 14), h('span', {}, 'Hatched cells come from the permit and cannot be edited.' + (limCol ? ' The amber cell is above its limit, so the sheet warns before it is submitted.' : ' No Limit column is mapped, so nothing is compared.'))),
        h('div', { class: 'pm-tw', style: 'background:var(--ground)' }, grid)));
      var fieldCode = limCol ? limCol.code.toUpperCase() : 'VALUE';
      body.appendChild(E.Collapsible({ id: 'pmb-formulas', title: 'Formulas on this table', summary: 'same functions as registries', content: h('div', { class: 'stack' }, note('Row limits and totals are available to any formula in the template through the permit that applies.'), E.CodeText("REGSUM('" + tb.regCode + "', ROW.PERMIT = [Permit], ROW." + fieldCode + ")", { block: true }), E.CodeText("[Actual] <= REGFIELD(REGFIND('" + tb.regCode + "', [Permit]" + keyMap.map(function (m) { return ', [' + m.sheet + ']'; }).join('') + "), '" + fieldCode + "')", { block: true })) }));
    } });

  /* Динамічний маршрут реєструється ПІСЛЯ статичних types/bindings, інакше :id їх перехоплює. */
  E.screen('/admin/permits/:id', PERMIT_SCREEN);

  /* ================= потоки ================= */
  E.flow('permits-create-fill-activate', { group: 'Permits', title: 'Add a permit, fill its tables and activate it', actor: 'Environmental specialist', steps: [
    { label: 'Permits', href: '#/admin/permits' }, { label: 'New permit (wizard)', href: '#/admin/permits?dialog=new-permit' }, { label: 'Permit overview', href: '#/admin/permits/P-0001' }, { label: 'Table “Emission limits”', href: '#/admin/permits/P-0001?tab=AirEmissions' }, { label: 'Waste permit: add a row (form)', href: '#/admin/permits/P-0002?tab=WasteLimits&panel=WasteLimits:__new' }, { label: 'Waste permit: paste rows from Excel', href: '#/admin/permits/P-0002?tab=WasteLimits&dialog=paste-rows' }, { label: 'Waste permit: edit a row', href: '#/admin/permits/P-0002?tab=WasteLimits&panel=WasteLimits:w1' }, { label: 'Activate (draft permit)', href: '#/admin/permits/P-0004?dialog=activate-permit' }] });
  E.flow('permits-import-excel', { group: 'Permits', title: 'Import the yearly Excel report', actor: 'Environmental specialist', steps: [
    { label: 'Permits', href: '#/admin/permits' }, { label: 'Import from Excel', href: '#/admin/permits?dialog=import-permit' }] });
  E.flow('permits-wide-table', { group: 'Permits', title: 'Work with a 31-column table', actor: 'Environmental specialist', steps: [
    { label: 'Water discharge permit', href: '#/admin/permits/P-0003' }, { label: 'Grouped columns', href: '#/admin/permits/P-0003?tab=Discharge' }, { label: 'Row drawer, grouped by section', href: '#/admin/permits/P-0003?tab=Discharge&panel=Discharge:d1' }] });
  E.flow('permits-renew', { group: 'Permits', title: 'Renew a permit before it ends', actor: 'Environmental specialist', steps: [
    { label: 'Permits (click “ending within 120 days”)', href: '#/admin/permits' }, { label: 'Permit that ends soon', href: '#/admin/permits/P-0005' }, { label: 'Renew…', href: '#/admin/permits/P-0005?dialog=renew-permit' }] });
  E.flow('permits-define-type', { group: 'Permits', title: 'Define a permit type with its own tables', actor: 'Template administrator', steps: [
    { label: 'Permit types', href: '#/admin/permits/types' }, { label: 'New type (wizard)', href: '#/admin/permits/types?dialog=new-type' }, { label: 'Structure: tables and columns', href: '#/admin/permits/types/WaterDischarge' }, { label: 'Add a table (wizard)', href: '#/admin/permits/types/WaterDischarge?dialog=add-table' }, { label: 'Add 12 month columns', href: '#/admin/permits/types/WaterDischarge?dialog=add-series' }, { label: 'Column properties', href: '#/admin/permits/types/WaterDischarge?panel=Annual' }, { label: 'Checks', href: '#/admin/permits/types/WaterDischarge?tab=rules' }, { label: 'Publish', href: '#/admin/permits/types/WaterDischarge?dialog=publish-type' }] });
  E.flow('permits-bind-sheet', { group: 'Permits', title: 'Make a sheet read its permit', actor: 'Template administrator', steps: [
    { label: 'Sheet bindings', href: '#/admin/permits/bindings' }, { label: 'New binding (wizard)', href: '#/admin/permits/bindings?dialog=new-binding' }, { label: 'Mapping and preview', href: '#/admin/permits/bindings/B-01' }, { label: 'Formulas-only binding', href: '#/admin/permits/bindings/B-05' }] });
})(window.ECR);

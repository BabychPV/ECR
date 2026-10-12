"""Відповідність прод-колонок AF (стара система HSE) колонкам шаблону Land + колонки введення, яких у шаблоні немає.

Джерело: прод-зразок 2026-10-12 (.sync-local/reports/LAND-PROD-SAMPLE-2026-10-12.md, прогалини 3-5). Тут лише СТРУКТУРА
(імена колонок/таблиць AF і заголовки шаблону); значень прод-БД немає. Імена прод-колонок узято зі схеми вивантаження.

Принцип «не вигадуємо»:
  * alias знаходить колонку шаблону за ЗАГОЛОВКОМ (regex) у межах таблиці; нуль або кілька збігів — генерація падає;
  * evidence: 'header' — заголовок Excel прямо називає величину; 'single' — у таблиці лише один кандидат такої природи;
    'position' — за порядком в Excel; усе, що не доведено, іде в notProven, а не в мапу;
  * нові колонки (newColumns) — лише там, де в таблиці Excel немає жодної колонки такої природи (перевірено за заголовками).
Перейменувань наявних колонок немає: лише аліаси й додані колонки (стенди з уже побудованим шаблоном не ламаються).
"""
import re

MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
M3 = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

# Прод-таблиця -> (код аркуша, код таблиці шаблону); та сама логіка, що в локальному завантажувачі зразка.
_PROD_RULES = [
    (r'^AF_Air_Land_Stationary_Equipment$', lambda g: ('S03_STATIONARY_EQUIPMENT', 'T3_00')),
    (r'^AF_Air_Land_Mobile_Equipment$', lambda g: ('S04_MOBILE_EQUIPMENT', 'T4_01')),
    (r'^AF_Air_Land_Other_Equipment_(5_\d+)$', lambda g: ('S05_OTHER_EQUIPMENT', 'T' + g[0])),
    (r'^AF_Air_Land_Dust_Producing_Activities_(6_\d+)$', lambda g: ('S06_DUST_PRODUCING_ACTIVITIES', 'T' + g[0])),
    (r'^AF_Air_Land_Utility_(9_\d+)$', lambda g: ('S09_UTILITY', 'T' + g[0])),
    (r'^AF_Air_Land_Off_Utility_DI_(10_\d+)$', lambda g: ('S10_OFF_UTILITY_DI', 'T' + g[0])),
    (r'^AF_Air_Land_Off_Utility_AI_(10a_\d+)$', lambda g: ('S10A_OFF_UTILITY_AI', 'T' + g[0])),
    (r'^AF_Air_Land_Off_Process_DI_(11_\d+)$', lambda g: ('S11_OFF_PROCESS_DI', 'T' + g[0])),
    (r'^AF_Air_Land_11a_Off_Process', lambda g: ('S11A_OFF_PROCESS_AI', 'T11a_01')),
    (r'^AF_Air_Land_11b_Off_Process', lambda g: ('S11B_OFF_PROCESS_EPC2', 'T11b_01')),
    (r'^AF_Air_Land_11c_Off_Process', lambda g: ('S11C_OFF_PROCESS_EPC3', 'T11c_01')),
    (r'^AF_Air_Land_11d_Off_Process', lambda g: ('S11D_OFF_PROCESS_EPC4', 'T11d_01')),
    (r'^AF_Water_Land_07$', lambda g: ('S07_WATER_REPORT', 'T7_00')),
    (r'^AF_Water_Land_07a_(\d+)$', lambda g: ('S07A_WATER_MEASUREMENTS', 'T7a_%02d' % int(g[0]))),
]


def template_of(prod_table):
    for pat, fn in _PROD_RULES:
        m = re.match(pat, prod_table)
        if m:
            return fn(m.groups())
    return None


# Прод-таблиці зразка, що мають дані (AF_Waste_Land_08 — окремо, prodMapping.waste).
PROD_TABLES = [
    'AF_Air_Land_11a_Off_Process_AI', 'AF_Air_Land_11b_Off_Process_EPC2', 'AF_Air_Land_11c_Off_Process_EPC3',
    'AF_Air_Land_11d_Off_Process_EPC4', 'AF_Air_Land_Dust_Producing_Activities_6_11', 'AF_Air_Land_Dust_Producing_Activities_6_12',
    'AF_Air_Land_Mobile_Equipment', 'AF_Air_Land_Off_Process_DI_11_01', 'AF_Air_Land_Off_Process_DI_11_02',
    'AF_Air_Land_Off_Process_DI_11_03', 'AF_Air_Land_Off_Process_DI_11_04', 'AF_Air_Land_Off_Process_DI_11_05',
    'AF_Air_Land_Off_Utility_AI_10a_01', 'AF_Air_Land_Off_Utility_AI_10a_02', 'AF_Air_Land_Off_Utility_DI_10_01',
    'AF_Air_Land_Off_Utility_DI_10_03', 'AF_Air_Land_Off_Utility_DI_10_04', 'AF_Air_Land_Off_Utility_DI_10_05',
    'AF_Air_Land_Other_Equipment_5_08', 'AF_Air_Land_Other_Equipment_5_09', 'AF_Air_Land_Other_Equipment_5_27',
    'AF_Air_Land_Stationary_Equipment',
] + ['AF_Air_Land_Utility_9_%02d' % n for n in range(1, 16)] + ['AF_Water_Land_07', 'AF_Water_Land_07a_16']

# ---------- аліаси за заголовком ----------
# (таблиці шаблону, прод-колонка, regex заголовка (re.I, від початку), monthly, evidence, примітка)
_T11CD = ['T11c_01', 'T11d_01']
_T9_DAYS = ['T9_03', 'T9_05', 'T9_09', 'T9_12', 'T9_13', 'T9_14']
ALIASES = [
    # Promasys-лічильники (Filled by Promasys) у 11c/11d: у шаблоні String, у проді Int
    (_T11CD, 'No_Pump', r'Filled by Promasys: Pump_no$', False, 'header', 'Колонка шаблону String; прод — число (подавати текстом)'),
    (_T11CD, 'No_Flanges', r'Filled by Promasys: Flanges_no$', False, 'header', 'Колонка шаблону String; прод — число'),
    (_T11CD, 'No_Valves', r'Filled by Promasys: Valves_no$', False, 'header', 'Колонка шаблону String; прод — число'),
    (_T11CD, 'No_SafValves', r'Filled by Promasys: Saf_Valves_no$', False, 'header', 'Колонка шаблону String; прод — число'),
    # 6.xx пил
    (['T6_12'], 'Land_Agent_EmissionSourceType', r'Emission source type$', False, 'header', 'Lookup'),
    (['T6_12'], 'Land_NameArea', r'Name of area$', False, 'header', ''),
    (['T6_12'], 'Land_TypeMaterial', r'Type of material$', False, 'header', ''),
    (['T6_12'], 'Land_TrappingEfficienty', r'Dust trapping efficiency', False, 'header', 'У проді опечатка в імені колонки'),
    (['T6_12'], 'Land_NAE', r'ID Code - MPE Project$', False, 'position', 'У Excel заголовок «MPE», у проді NAE — та сама позиція ID-коду'),
    (['T6_12'], 'Land_Agent_Fraction', r'k1 Share of dust fraction', False, 'header', 'k1'),
    (['T6_12'], 'Land_Agent_Transforming', r'k2 Share of volatile dust', False, 'header', 'k2'),
    (['T6_12'], 'Land_Agent_LocalMeteorologicalConditions', r'k3 avr ', False, 'header', 'k3 avr'),
    (['T6_12'], 'Land_Agent_LocalConditions', r'k4 Factor taking into account local conditions', False, 'header', 'k4'),
    (['T6_12'], 'Land_Agent_MoistureMaterial', r'k5 Factor taking into account moisture', False, 'header', 'k5'),
    (['T6_12'], 'Land_Agent_CoarsenessMaterial', r'k7 Factor taking into account coarseness', False, 'header', 'k7'),
    (['T6_12'], 'Land_Agent_MaterialsGrabber', r'k8 Correction factor for different materials', False, 'header', 'k8 (тип грейфера в заголовку)'),
    (['T6_12'], 'Land_Agent_SalvoEmission', r'k9 Correction factor under forceful salvo', False, 'header', 'k9'),
    (['T6_12'], 'Land_Agent_AccountLoading', r'B1 Factor taking into account loading height', False, 'header', 'B1'),
    (['T6_12'], 'Land_Agent_LocalMeteorologicalConditionsMax', r'k3 max ', False, 'header', 'k3 max'),
    # 9.xx
    (['T9_02', 'T9_11'], 'Land_Measure_SubstanceConsumption', r'Substance consumption \(Sm3\) \(20 dgr', True, 'single',
     'Єдина колонка споживання речовини в таблиці; одиниця прод-колонки (Sm3?) звітом не названа'),
    (_T9_DAYS, 'Land_Measure_OperTimeDays', r'Operating time \(days\)$', True, 'header', 'Одиниця — доба (в імені прод-колонки Days)'),
    (['T9_15'], 'Land_OperTimeDays', r'Operating time \(days\)$', True, 'header', ''),
    (['T9_04'], 'Land_Measure_SubstanceConsumptionKgH', r'Substance consumption \(kg/h\)$', True, 'header', 'kg/h'),
    (['T9_04'], 'Land_Measure_SubstanceConsumptionKg', r'Substance consumption \(kg\)$', True, 'header',
     'Місячні колонки з нестандартними кодами (дубль імені в Excel) — див. monthColumns'),
    (['T9_06'], 'Land_Measure_FuelSubstanceConsumptionOrPumpedVolume', r'Substance consumption \(tonne\)$', True, 'single',
     'Єдина колонка споживання в таблиці'),
    (['T9_07'], 'Land_Measure_SubstanceConsumptionSm3H', r'Substance consumption \(Sm3/h\)$', True, 'header',
     'Заголовок Sm3/h, а unit колонки моделі — Sm3: розбіжність одиниці (див. unitMismatches)'),
    # 10/11 позавідомчі
    (['T10_01', 'T11_01'], 'Land_Measure_ReleaseColdVent', r'Release on cold vent', True, 'header', 'Lookup Yes/No'),
    (['T10_01'], 'Land_Measure_NumberOperations', r'Number of operations', True, 'header', ''),
    (['T11_01'], 'Land_Measure_NumOperations', r'Number of operations', True, 'header', ''),
    (['T11_05'], 'Land_Measure_Amount', r'Amount \(st\.m3\)$', True, 'header', ''),
    # 3.00 стаціонарне
    (['T3_00'], 'Land_FuelConsumption', r'Fuel consumption under 100% load', False, 'header',
     'Код колонки в моделі — FuelConsumptionUnder100LoadFromTheTe (ключ AF-словника не збігся через «%»)'),
    (['T3_00'], 'Land_EF_Remarks', r'Remarks$', False, 'position', 'Єдина «Remarks» між блоками EF і AL'),
]
for _num, _name in [('0301', 'NO2'), ('0304', 'NO'), ('0316', 'HCl'), ('0328', 'Soot'), ('0330', 'SO2'), ('0337', 'CO'), ('0342', 'HF'),
                    ('2902', 'SuspendedSolids')]:
    _hdr = {'SuspendedSolids': 'Suspended Solids'}.get(_name, _name)
    ALIASES.append((['T3_00'], 'Land_EF_%s_%s' % (_num, _name), r'Emission Factors per component for Incin: %s %s' % (_num, re.escape(_hdr)),
                    False, 'header', 'g/s'))
for _num, _name in [('0301', 'NO2'), ('0304', 'NO'), ('0316', 'HCl'), ('0328', 'Soot'), ('0330', 'SO2'), ('0337', 'CO'), ('0342', 'HF'),
                    ('0703', 'Benzapyrene'), ('2704', 'Gasoline'), ('2754', 'C12C19'), ('2902', 'SuspendedSolids')]:
    _hdr = {'SuspendedSolids': 'Suspended Solids', 'C12C19': r'C12-C19'}.get(_name, _name)
    ALIASES.append((['T3_00'], 'Land_AL_%s_%s' % (_num, _name), r'Annual limits per component: %s %s' % (_num, _hdr), False, 'header', 'g/s'))

# Місячні/квартальні колонки T3_00: явні коди (заголовок «0301 NO2 (g/s)» повторюється в усіх місяцях).
EXPLICIT_CODES = []  # (table, prod, code, evidence, note)
for _i, _mon in enumerate(MONTHS):
    EXPLICIT_CODES.append(('T3_00', 'Land_Measure_%s' % _mon, '%s_%sRHDuringExceeding' % (M3[_i], _mon), 'position',
                           'Колонка «<Month>. RH during exceeding» перед групою викидів місяця'))
    for _num, _name in [('0301', 'NO2'), ('0304', 'NO'), ('0330', 'SO2'), ('0337', 'CO')]:
        EXPLICIT_CODES.append(('T3_00', 'Land_Measure_%s_%s_%s' % (_mon, _num, _name), '%s_%s%s' % (M3[_i], _num, _name), 'header',
                               'g/s, місяць %d' % (_i + 1)))
for _q in (1, 2, 3, 4):
    EXPLICIT_CODES.append(('T3_00', 'Land_Measure_Quarter%d' % _q, 'C_%dQuarter' % _q, 'header', 'Квартальна колонка'))

# ---------- нові колонки введення (у шаблоні відсутні) ----------
_FUEL = dict(dataType='Lookup', registry='LAND_STATIONARY_TYPE_OF_FUEL', headerEn='Type of fuel', evidence=(
    'Прод-таблиця має Land_TypeFuel, у таблиці Excel колонки палива немає; значення зразка збігаються з довідником '
    'LAND_STATIONARY_TYPE_OF_FUEL (колонка є в типових прод-документах 10.01, 10.03, 11.01, 10.04)'))
NEW_COLUMNS = []  # (table, spec)
for _t in ('T10_01', 'T10_03', 'T10_04', 'T11_01'):
    NEW_COLUMNS.append((_t, dict(_FUEL, code='Land_TypeFuel')))
NEW_COLUMNS.append(('T11_03', dict(code='Land_ProcessTrain', dataType='String', headerEn='Process train',
                                   evidence='Прод-таблиця має Land_ProcessTrain (текст), у 11.03 Excel колонки немає; у 11c.01 така колонка є (String)')))
for _t in ('T5_08', 'T5_09', 'T5_27'):
    NEW_COLUMNS.append((_t, dict(code='Land_ERG_EmissionSourceType', dataType='String', zone='ERG', headerEn='Emission source type (ERG)',
                                 evidence='Прод має Land_ERG_EmissionSourceType поряд із Land_EmissionSourceType (текст «Organized/Non-organized»); '
                                          'у шаблоні лише одна введена колонка типу джерела — другої немає')))
_GTU_EVIDENCE = ('Прод має Land_Measure_GTU_Month і Land_Measure_GTU<n>_<301|304|330|337|O2>; у таблиці Excel відповідних колонок введення немає '
                 '(є лише службові компоненти «Component: Month», «301: 00xx»). Одиниця й семантика звітом НЕ доведені.')
for _t, _n in (('T10_03', 4), ('T11_03', 5)):
    NEW_COLUMNS.append((_t, dict(code='Land_Measure_GTU_Month', dataType='String', headerEn='GTU measurement month', evidence=_GTU_EVIDENCE)))
    for _g in range(1, _n + 1):
        for _c in ('301', '304', '330', '337', 'O2'):
            NEW_COLUMNS.append((_t, dict(code='Land_Measure_GTU%d_%s' % (_g, _c), dataType='Decimal', headerEn='GTU%d %s' % (_g, _c),
                                         evidence=_GTU_EVIDENCE)))

# ---------- явно недоведене ----------
NOT_PROVEN = [
    {'prodTable': 'AF_Air_Land_Dust_Producing_Activities_6_11', 'prodColumn': 'Land_Agent_EmissionSourceType',
     'why': 'У зразку колонка порожня; у T6_11 Excel лише "Type of emission source within unit" (String) — відповідність не доведена.'},
    {'prodTable': 'AF_Air_Land_Other_Equipment_5_09', 'prodColumn': 'Land_DescriptionEquipment',
     'why': 'У шаблоні T5_09 колонка Lookup (довідник), у проді вільний текст; змінити тип чи довідник — рішення замовника (прогалина 5).'},
    {'prodTable': '*', 'prodColumn': 'Land_Rotation / Land_Status',
     'why': 'У проді трапляється текст «NULL» замість порожнього — це вимога до завантажувача (пропускати), не до моделі.'},
    {'prodTable': 'AF_Air_Land_Off_Utility_DI_10_03', 'prodColumn': 'Land_Measure_GTU*',
     'why': 'Одиниця величин GTU (мг/м3? %?) і зв\'язок рядка з місяцем (Land_Measure_GTU_Month) звітом не доведені; колонки додано без unit.'},
    {'prodTable': 'AF_Air_Land_Utility_9_02 / 9_11', 'prodColumn': 'Land_Measure_SubstanceConsumption',
     'why': 'Одиницю прод-колонки звіт не називає; зіставлено з єдиною колонкою споживання (Sm3 20 dgr) за принципом single-candidate.'},
    {'prodTable': '*', 'prodColumn': 'Con_*',
     'why': 'Колонки шапки Con_* виключено з вивантаження: що саме відрізняється в шапці — не доведено (потрібне повторне вивантаження назв).'},
]

# Розбіжності одиниць моделі та заголовка Excel, виявлені під час зіставлення (модель не змінюється).
UNIT_MISMATCHES = [
    {'table': 'T9_07', 'column': 'SubstanceConsumption', 'header': 'Substance consumption (Sm3/h)', 'modelUnit': 'Sm3',
     'note': 'У UNIT_MAP немає одиниці «Sm3/год»; unit_of() знаходить «sm3». Потрібне рішення щодо одиниці (нова похідна одиниця або без unit).'},
]

WATER = {
    'T7_00': {'prodTable': 'AF_Water_Land_07', 'rowKey': 'R{Land_StringReportId}', 'valueProdColumn': 'Land_Measure_Volume',
              'valueColumn': 'місячна колонка <Month> (January…December) обраного місяця',
              'evidence': 'схема завантаження зразка 2026-10-12 (0 помилок валідації); рядки без ключа R<id> у шаблоні не мають відповідника (частина рядків типового прод-документа)'},
    'T7a_16': {'prodTable': 'AF_Water_Land_07a_16', 'rowOrder': 'Land_No -> N-й рядок Item таблиці', 'annualLimitColumn': 'AnnualLimit',
               'valueProdColumn': 'Land_Measure_Concentration', 'valueColumn': 'місячна колонка <Month> обраного місяця',
               'evidence': 'схема завантаження зразка 2026-10-12; Land_ComponentEn/Ru — підписи рядка, не вводяться'},
}


# ---------- застосування до моделі ----------
def _norm_hdr(h):
    return re.sub(r'\s+', ' ', (h or '')).strip()


def _month_columns(cols, rx):
    """12 кодів колонок за заголовком (за номером місяця); рівно по одній на місяць."""
    by_m = {}
    for c in cols:
        if c.get('month') and not c.get('hidden') and not c.get('service') and re.match(rx, _norm_hdr(c['headerEn']), re.I):
            by_m.setdefault(c['month'], []).append(c['code'])
    if sorted(by_m) != list(range(1, 13)) or any(len(v) != 1 for v in by_m.values()):
        raise SystemExit('prod_mapping: колонка за заголовком %r дає %s' % (rx, {k: v for k, v in sorted(by_m.items())}))
    return [by_m[m][0] for m in range(1, 13)]


def _single_column(cols, rx):
    hits = [c['code'] for c in cols if not c.get('month') and not c.get('hidden') and not c.get('service')
            and c.get('excel') and re.match(rx, _norm_hdr(c['headerEn']), re.I)]
    if len(hits) != 1:
        raise SystemExit('prod_mapping: колонка за заголовком %r дає %s' % (rx, hits))
    return hits[0]


def apply(out):
    """Додає колонки введення до таблиць out['sheets'] і повертає (prodColumnMapping, кількість доданих колонок)."""
    tables = {}  # код таблиці -> (sheet code, table); коди T7_00 повторюються, але нас цікавлять унікальні
    for s in out['sheets']:
        for t in s['tables']:
            tables.setdefault(t['code'], []).append((s['code'], t))

    def tab(code):
        v = tables[code]
        if len(v) != 1:
            raise SystemExit('prod_mapping: код таблиці %s неоднозначний' % code)
        return v[0]

    added = 0
    new_by_table = {}
    for tcode, spec in NEW_COLUMNS:
        _, t = tab(tcode)
        if any(c['code'] == spec['code'] for c in t['columns']):
            raise SystemExit('prod_mapping: %s.%s уже існує' % (tcode, spec['code']))
        col = {'code': spec['code'], 'dataType': spec['dataType'], 'headerEn': spec['headerEn'], 'headerRu': '', 'hidden': False,
               'readOnly': False, 'excel': None, 'month': None, 'zone': spec.get('zone'),
               'ecrAdded': 'prod-sample gap (2026-10-12): колонки введення немає в Excel-шаблоні',
               'prodEvidence': spec['evidence']}
        if spec['dataType'] == 'Decimal':
            col['precision'] = 28
            col['scale'] = 12
        if spec['dataType'] == 'Lookup':
            col['registry'] = spec['registry']
        t['columns'].append(col)
        new_by_table.setdefault(tcode, []).append(spec['code'])
        added += 1

    by_tbl = {}

    def entry(tcode):
        sheet, t = tab(tcode)
        return by_tbl.setdefault('%s|%s' % (sheet, tcode), {'aliases': [], 'refs': [], 'newColumns': new_by_table.get(tcode, []), '_t': t})

    for tcode in new_by_table:  # таблиця лише з доданими колонками теж має запис у мапі
        entry(tcode)

    for tcodes, prod, rx, monthly, evidence, note in ALIASES:
        for tcode in tcodes:
            e = entry(tcode)
            cols = e['_t']['columns']
            item = {'prod': prod, 'evidence': evidence}
            if monthly:
                item['monthColumns'] = _month_columns(cols, rx)
            else:
                item['column'] = _single_column(cols, rx)
            if note:
                item['note'] = note
            e['aliases'].append(item)
    for tcode, prod, code, evidence, note in EXPLICIT_CODES:
        e = entry(tcode)
        if not any(c['code'] == code for c in e['_t']['columns']):
            raise SystemExit('prod_mapping: %s.%s не знайдено' % (tcode, code))
        e['aliases'].append({'prod': prod, 'column': code, 'evidence': evidence, 'note': note})
    # Текстові мости методологій: прод-колонка (текст) -> вхідна Lookup-колонка *Ref
    for s in out['sheets']:
        for t in s['tables']:
            for c in t['columns']:
                if c.get('refFor'):
                    entry(t['code'])['refs'].append({'prod': c['refFor'], 'column': c['code'], 'evidence': 'refFor',
                                                     'note': 'прод-текст резолвиться в запис довідника колонки; формула-міст читає NAME'})
    for tcode, e in by_tbl.items():
        e.pop('_t', None)
    return {
        'formatVersion': 1,
        'source': 'LAND-PROD-SAMPLE-2026-10-12 (лише структура)',
        'prodTables': {p: '%s|%s' % template_of(p) for p in PROD_TABLES if template_of(p)},
        'tables': by_tbl,
        'water': WATER,
        'unitMismatches': UNIT_MISMATCHES,
        'notProven': NOT_PROVEN,
    }, added

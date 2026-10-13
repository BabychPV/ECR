# -*- coding: utf-8 -*-
"""LP-10: похідні аргументи методологій Land, які старий код виводить зі словників у C#, а не бере з колонки.

Залишок 0438 після мостів за атрибутами AF (`af_attr_bridges.py`) мав три «похідні» аргументи. Рушій не змінюється:
вистачає REGFIND/REGFIELD/CURRENT_SUBSTANCE() і `if` (діалект методологій), які вже є.

  1. Land_Power (ECW_C11_03, таблиця 9a_17) і Category1 (ECW_C11_11_02, 9_01/9_07/9_08/9a_16/9a_17a/9a_19) -
     довідник LAND_NAE_GROUP (ключ NAE_NORMALIZED -> POWER_GROUP, CATEGORY1). Значення ГЕНЕРУЮТЬСЯ з C#-словників
     (`powerGroup` у ECW_C11_03.cs, `group1` у ECW_C11_11_02.cs; їх виймає extract_nae_groups.py у
     af-land-nae-groups.json), а не вводяться руками й не беруться з прода. Міст - Formula-колонка з кодом аргументу:
         REGFIELD(REGFIND('LAND_NAE_GROUP', [<колонка NAE таблиці>]), 'POWER_GROUP' | 'CATEGORY1')
     NAE немає в довіднику -> REGFIND дає #N/A -> аргумент не обчислено -> рядок без результату (стара система:
     `Error Power value` / `Error category1 - NAE` і `continue`).
     Нормалізація NAE. У C11_11_02 NAE береться ЗІ ЗНЯТИМИ пробілами (`.Replace(" ", "")`), у C11_03 - сирим
     (`naeNumberObj.ToString()`, без жодної нормалізації). Діалект шаблонів не має функції заміни тексту (є лише
     CONVERT/SUM/.../IF/IFERROR/REG*), тому нормалізація виразом неможлива: у довідник пишуться ОБИДВА написання ключа -
     без пробілів (`0788(001)`, як у словнику C#) і з одним пробілом перед дужкою (`0788 (001)`, як у шаблоні Excel;
     те саме написання називає правило категорії C11_11_02, af-land-category-rules.json). Ключ у REGFIND нормалізує
     сам рушій (RegistryKeyNormalizer: обрізка країв, згортання послідовності пробілів в один, без регістру), тож
     `0788  (001)` чи ` 0788 (001) ` знаходяться. Пробіл УСЕРЕДИНІ коду (`07 88`) стара система знімає, а ключ - ні:
     такий NAE недоведений, не вгадується й дасть #N/A (рядок без результату).

  2. Land_MassFraction (ECW_C11_11_04, 9a_19) - аргумент береться з АТРИБУТА з кодом речовини
     `Land_MassFraction_0<код>` за поточною речовиною проходу (`arguments["Land_MassFraction_0" + code]`). Колонку «за
     кодом із виразу» діалект адресувати не вміє, але речовин type1 рівно п'ять (415, 416, 602, 616, 621 -
     `typeCalculationGroupTonne`), тож: (а) мости Land_MassFraction_0<код> на колонки з заголовком «<код>: ...»
     (код у заголовку Excel = код у назві атрибута AF - доказ сильніший за порядок); (б) у формулах версії методології
     токен `@Land_MassFraction` замінюється фрагментом
         if(CURRENT_SUBSTANCE() = '415', @Land_MassFraction_0415, if(... , <#N/A>))
     (Build-LandTemplate.ps1 -Stage MassFraction; як CompositionMass, ярус Extension -> версія Strict).
     Реєстр «речовина -> колонка» не потрібен: колонку за значенням рядка вибрати нічим, а вибір `if` доводиться
     юнітом на справжньому рушії (NaeGroupBridgesTests).
"""
import hashlib
import json
import re

REGISTRY = 'LAND_NAE_GROUP'
SUBSTANCE_REGISTRY = 'LAND_HSE400_SUBSTANCE'   # уже є; потрібен лише для гілки «#N/A» фрагмента MassFraction
GROUPS_FILE = 'af-land-nae-groups.json'

POWER_METHODOLOGY = 'ECW_C11_03'
CATEGORY1_METHODOLOGY = 'ECW_C11_11_02'
MASSFRACTION_METHODOLOGY = 'ECW_C11_11_04'
MASSFRACTION_TOKEN = 'Land_MassFraction'


def _flatten(groups, what):
    mapping = {}
    for group, naes in groups.items():
        for nae in naes:
            if nae in mapping and mapping[nae] != group:
                raise SystemExit('nae_group: %s: NAE %s у двох групах' % (what, nae))
            mapping[nae] = group
    return mapping


def _spaced(key):
    """Написання з одним пробілом перед першою дужкою: `0788(001)` -> `0788 (001)`."""
    return re.sub(r'(?<! )\(', ' (', key, count=1) if '(' in key else key


def entry_code(key):
    return 'N' + hashlib.sha1(key.lower().encode('utf-8')).hexdigest()[:10].upper()


def build_entries(power, category1):
    """Записи LAND_NAE_GROUP. Power - сирі ключі (C11_03 не нормалізує NAE); Category1 - ключі без пробілів і з пробілом
    перед дужкою (C11_11_02 знімає пробіли, а шаблон Excel їх містить)."""
    rows = {}

    def put(key, field, value):
        rows.setdefault(key, {})
        if field in rows[key] and rows[key][field] != value:
            raise SystemExit('nae_group: ключ %r: поле %s має два значення' % (key, field))
        rows[key][field] = value

    for nae, group in power.items():
        put(nae, 'POWER_GROUP', group)
    for nae, group in category1.items():
        put(nae, 'CATEGORY1', group)
        sp = _spaced(nae)
        if sp != nae:
            put(sp, 'CATEGORY1', group)

    ents, codes = [], {}
    for key in sorted(rows):
        code = entry_code(key)
        if code in codes:
            raise SystemExit('nae_group: збіг коду запису для %r і %r' % (key, codes[code]))
        codes[code] = key
        fields = {'NAE_NORMALIZED': key}
        fields.update({f: rows[key][f] for f in ('POWER_GROUP', 'CATEGORY1') if f in rows[key]})
        ents.append({'code': code, 'name': key, 'en': key, 'ru': '', 'fields': fields})
    return ents


def registry(entries):
    return {
        'code': REGISTRY,
        'nameEn': 'Land: NAE groups (NAE -> power group of ECW_C11_03, Category1 of ECW_C11_11_02)',
        'excel': 'ECW_C11_03.cs powerGroup + ECW_C11_11_02.cs group1 (legacy backend dictionaries)',
        'entries': entries, 'fieldNames': ['NAE_NORMALIZED', 'POWER_GROUP', 'CATEGORY1'], 'keyField': 'NAE_NORMALIZED',
        'fieldSpecs': [
            {'code': 'NAE_NORMALIZED', 'dataType': 'String', 'isKey': True},
            {'code': 'POWER_GROUP', 'dataType': 'Decimal', 'isKey': False},
            {'code': 'CATEGORY1', 'dataType': 'String', 'isKey': False},
        ],
        'keys': [{'code': 'PK', 'fields': ['NAE_NORMALIZED'], 'isPrimary': True, 'ignoreCase': True}],
    }


def nae_expression(field, nae_column):
    return "REGFIELD(REGFIND('%s', [%s]), '%s')" % (REGISTRY, nae_column, field)


def mass_fraction_fragment(codes):
    """Фрагмент замість @Land_MassFraction: вибір аргументу Land_MassFraction_0<код> за речовиною проходу."""
    # Ключа `<код>~` у словнику показників немає -> #N/A (речовина поза type1: стара система аргумент не виставляла).
    expr = "REGFIELD(REGFIND('%s', CURRENT_SUBSTANCE() + '~'), 'INDICATOR')" % SUBSTANCE_REGISTRY
    for code in reversed(codes):
        expr = "if(CURRENT_SUBSTANCE() = '%s', @%s_0%s, %s)" % (code, MASSFRACTION_TOKEN, code, expr)
    return expr


def _new_column(code, expression, header, evidence, source_type):
    col = {'headerRu': '', 'hidden': False, 'readOnly': True, 'excel': None, 'month': None,
           'precision': 28, 'scale': 12, 'code': code, 'dataType': 'Formula', 'headerEn': header, 'unit': None,
           'expression': expression, 'ecrAdded': evidence}
    if source_type == 'String':
        col['precision'] = None
        col['scale'] = None
    return col


def apply(out, registries, af_attrs):
    """Додає довідник LAND_NAE_GROUP і мости; повертає кількість доданих колонок."""
    with open(GROUPS_FILE, encoding='utf-8') as fh:
        src = json.load(fh)
    power_table = src['powerGroup']['table']
    power = _flatten(src['powerGroup']['groups'], 'powerGroup')
    category1 = _flatten(src['category1']['groups'], 'group1')
    codes = list(src['massFraction']['type1Codes'])

    by_code = {}
    for s_ in out['sheets']:
        for t_ in s_['tables']:
            by_code.setdefault(t_['code'], t_)
    ab = out['argumentBridges']
    bridged, unresolved = ab['bridged'], ab['unresolved']
    added = 0

    entries = build_entries(power, category1)
    registries[REGISTRY] = registry(entries)

    def nae_source(tcode):
        """Колонка введення NAE таблиці - джерело вже зведеного моста Land_NAE (однозначна колонка, не вгадуємо)."""
        cols = {c['code']: c for c in by_code[tcode]['columns']}
        expr = (cols.get('Land_NAE') or {}).get('expression') or ''
        m = re.match(r'^\[([A-Za-z0-9_]+)\]$', expr)
        if not m or m.group(1) not in cols:
            raise SystemExit('nae_group: %s: міст Land_NAE не є прямою колонкою (%r)' % (tcode, expr))
        return m.group(1)

    def bridge(tcode, arg, field, evidence):
        nonlocal added
        t_ = by_code[tcode]
        if arg in {c['code'] for c in t_['columns']}:
            raise SystemExit('nae_group: %s: колонка %s уже є' % (tcode, arg))
        col = _new_column(
            arg, nae_expression(field, nae_source(tcode)),
            '%s (argument bridge: %s of the NAE group)' % (arg, field), evidence,
            'Decimal' if field == 'POWER_GROUP' else 'String')
        t_['columns'].append(col)
        bridged[tcode] = sorted(set(bridged.get(tcode, [])) | {arg})
        del unresolved[tcode][arg]
        if not unresolved[tcode]:
            del unresolved[tcode]
        added += 1

    power_tables, cat_tables = [], []
    # Land_Power: лише на таблиці, на якій код виставляє групу (guard TableName), і лише де її ще немає як колонки.
    if power_table in by_code and 'Land_Power' in unresolved.get(power_table, {}):
        bridge(power_table, 'Land_Power', 'POWER_GROUP',
               'bridge: methodology argument Land_Power = POWER_GROUP of %s by NAE (%s powerGroup, legacy dictionary; '
               'raw NAE, no normalization)' % (REGISTRY, POWER_METHODOLOGY))
        power_tables.append(power_table)
    # Category1: на кожній таблиці, де аргумент названо «похідним» (її методологія - C11_11_02).
    for tcode in sorted(unresolved):
        if unresolved[tcode].get('Category1', '').startswith('[похідне]'):
            bridge(tcode, 'Category1', 'CATEGORY1',
                   'bridge: methodology argument Category1 = CATEGORY1 of %s by NAE without spaces (%s group1, legacy '
                   'dictionary)' % (REGISTRY, CATEGORY1_METHODOLOGY))
            cat_tables.append(tcode)

    # Land_MassFraction: п'ять мостів Land_MassFraction_0<код> і фрагмент.
    mf_tables = []
    for tcode in sorted(unresolved):
        if MASSFRACTION_TOKEN not in unresolved[tcode]:
            continue
        t_ = by_code[tcode]
        attrs = {a['name'] for a in (af_attrs.get(tcode) or {}).get('attributes', [])}
        cols = {c['code'] for c in t_['columns']}
        for code in codes:
            arg = '%s_0%s' % (MASSFRACTION_TOKEN, code)
            if arg not in attrs:
                raise SystemExit('nae_group: %s: у класі AF немає атрибута %s' % (tcode, arg))
            hits = [c for c in t_['columns']
                    if not c.get('ecrAdded') and not c.get('month') and c['dataType'] in ('String', 'Decimal')
                    and re.match(r'^%s:' % code, c.get('headerEn') or '')]
            if len(hits) != 1:
                raise SystemExit('nae_group: %s: для %s колонок із заголовком «%s:» - %d (очікується 1)'
                                 % (tcode, arg, code, len(hits)))
            if arg in cols:
                raise SystemExit('nae_group: %s: колонка %s уже є' % (tcode, arg))
            src_col = hits[0]
            t_['columns'].append(_new_column(
                arg, '[%s]' % src_col['code'], '%s (argument bridge: AF attribute %s)' % (arg, arg),
                'bridge: methodology argument %s = AF attribute %s of the AF table, header code %s (%s)'
                % (arg, arg, code, MASSFRACTION_METHODOLOGY), src_col['dataType']))
            bridged[tcode] = sorted(set(bridged.get(tcode, [])) | {arg})
            added += 1
        unresolved[tcode][MASSFRACTION_TOKEN] = (
            '[на-речовину] у формулі версії @%s замінюється фрагментом if(CURRENT_SUBSTANCE() = ..., @%s_0<код>) '
            '(argumentBridges.massFraction; Build-LandTemplate -Stage MassFraction); колонки %s_0<код> - мости' % (
                MASSFRACTION_TOKEN, MASSFRACTION_TOKEN, MASSFRACTION_TOKEN))
        mf_tables.append(tcode)

    ab['bridged'] = dict(sorted(bridged.items()))
    ab['unresolved'] = dict(sorted(unresolved.items()))
    ab['naeGroup'] = {
        'registry': REGISTRY,
        'source': {'power': src['powerGroup']['source'], 'category1': src['category1']['source']},
        'powerTables': power_tables,
        'category1Tables': cat_tables,
        'entries': len(entries),
        'rule': 'NAE not in LAND_NAE_GROUP (or the group field empty) -> #N/A / empty -> argument not computed -> row has '
                'no result (legacy: Error Power value / Error category1 - NAE, continue)',
        'normalization': 'dialect of templates has no text replace: keys are stored both without spaces and with one '
                         'space before the parenthesis (Category1); REGFIND trims and collapses space runs itself; a '
                         'space inside the code is not handled; Land_Power keys are raw (legacy does not normalize)',
    }
    ab['massFraction'] = {
        'token': MASSFRACTION_TOKEN,
        'tables': mf_tables,
        'substanceCodes': codes,
        'source': src['massFraction']['source'],
        'rule': 'substance code not in the type1 list -> #N/A (legacy: argument absent, formulas of other types do not '
                'use it)',
        'methodologies': {
            MASSFRACTION_METHODOLOGY: {
                'token': MASSFRACTION_TOKEN,
                'arguments': ['%s_0%s' % (MASSFRACTION_TOKEN, c) for c in codes],
                'fragment': mass_fraction_fragment(codes),
            },
        },
    }
    print('nae group bridges: columns %d; registry %s entries %d; power %s; category1 %s; massFraction %s' % (
        added, REGISTRY, len(entries), power_tables, cat_tables, mf_tables))
    return added

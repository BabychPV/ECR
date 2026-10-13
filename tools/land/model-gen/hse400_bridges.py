# -*- coding: utf-8 -*-
"""LP-10: склад газу HSE400 для розрахунку методологій Land C11/C12 (таблиці 9-11).

Стара система бере склад із dbo.CalcResult_AirGasCompositions_HSE400 за (рік, місяць), а ПОТІК - префіксом колонки,
який вибирає значення Land-поля типу палива/потоку через словник PrefixProvider. У ECR це два довідники БЕЗ нової
міграції й БЕЗ нових функцій рушія (лише REGFIND/REGFIELD, які вже є в діалекті шаблонів):

  LAND_HSE400_STREAM       текст палива/потоку (ключ) -> префікс потоку (FG_Internal, LPG_HMB ...);
  LAND_HSE400_COMPOSITION  нормалізований склад: (YEAR, MONTH, STREAM, INDICATOR) -> VALUE; значень у моделі НЕМАЄ,
                           їх одноразово завантажує tools/land/Import-LandHse400Composition.ps1 з локальної копії прода.

Аргумент методології (Density20, Vddg ...) отримує міст-колонку Formula:
  REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence,
                   REGFIELD(REGFIND('LAND_HSE400_STREAM', <текст потоку>), 'PREFIX'), '<показник>'), 'VALUE')
Немає запису за (рік, місяць, потік, показник) -> REGFIND дає #N/A -> аргумент не обчислено -> результатів рядка немає
(стара система: continue, рядок мовчки пропущено). Це ОДНЕ правило, а не помилка даних.

⛔ Мости робляться лише там, де колонка-джерело потоку однозначна (див. STREAM_SOURCE); решта лишається в
argumentBridges.unresolved з причиною: нічого не вгадуємо.
"""
import hashlib

STREAM_REGISTRY = 'LAND_HSE400_STREAM'
COMPOSITION_REGISTRY = 'LAND_HSE400_COMPOSITION'

# Аргумент методології -> показник реєстру (канонічне ім'я суфікса колонки прода: `_20`->`20`, `_0`->`0`).
# Правила підбору колонки в старій системі: F_Air_GetPropertyValueFromHSE400.cs; аргументи - ECW_C11_*.cs / ECW_C12_*.cs.
INDICATOR_OF_ARG = {
    'Density20': 'Density20',
    'Density0': 'Density0',
    'Density': 'Density20',                  # C11_08: Density_20 ?? Density20
    'Vddg': 'Vddg',
    'Vwdg': 'Vwdg',
    'TotalSulphurContent': 'TotalSulphurContent',
    'MercaptanSulphurContent': 'MercaptanSulphurContent',
    'FuelLowerHeating': 'FuelLowerHeating20',  # C11_08: FuelLowerHeating_20 ?? FuelLowerHeating20
    'H2Smass': 'H2S_mass',
}

# Склад по речовині ({код}_mass): значення залежить від речовини, а аргумент методології один на рядок.
SUBSTANCE_ARGS = ['CompositionMass']

# Код речовини -> показник складу: PollutionCodeService.GetMassName (старий бекенд, DllProject/Services,
# словник BaseCodes із 15 кодів). «{код}» у `{код}_mass` - це числовий код забруднювальної речовини
# (SUBSTANCE_CODE довідника LAND_AIR_SUBSTANCE, AF_PermitConstant.Substance_Code), а назва колонки прода -
# ХІМІЧНА (H2S_mass), тож зіставлення - жорстка таблиця коду, не конкатенація. Код поза таблицею -> "Unknown_mass" ->
# колонки немає -> склад не знайдено (у ECR - #N/A, той самий «рядок без результатів»).
# ⚠ HMB: код 1728 у старій системі для потоку з «HMB» дає EMercaptanV2_mass; це вже зроблено в завантаженні складу
# (Import-LandHse400Composition: EMercaptan_mass для *_HMB береться з колонки V2), тому тут показник один.
SUBSTANCE_REGISTRY = 'LAND_HSE400_SUBSTANCE'
SUBSTANCE_INDICATORS = [
    ('333', 'H2S_mass'),
    ('334', 'CS2_mass'),
    ('370', 'COS_mass'),
    ('415', 'C1_C5_mass'),
    ('416', 'C6_C10_mass'),
    ('602', 'Benzene_mass'),
    ('616', 'pXylene_mass'),
    ('621', 'Toluene_mass'),
    ('627', 'EBenzene_mass'),
    ('1702', 'nBMercaptan_mass'),
    ('1707', 'DimethilSulfide_mass'),
    ('1715', 'MMercaptan_mass'),
    ('1720', 'nPMercaptan_mass'),
    ('1728', 'EMercaptan_mass'),
    ('2754', 'C12_C19_mass'),
]

# Аргумент потоку й вираз префікса потоку для формул методологій, що читають CompositionMass (за старим кодом:
# ECW_C11_01/02/10 - Land_TypeFuelOrStream; ECW_C12_03 - Land_TypeFuel (Lookup); ECW_C12_01 - Land_Streams, де
# єдиний ключ словника PrefixProvider - FuelGas -> FG_Internal, а категорія береться з тексту 'Fuel gas').
STREAM_ARG_TEXT = "REGFIELD(REGFIND('%s', %%s), 'PREFIX')" % STREAM_REGISTRY
COMPOSITION_MASS_METHODOLOGIES = {
    'ECW_C11_01': {'arguments': ['Land_TypeFuelOrStream'],
                   'prefix': STREAM_ARG_TEXT % '@Land_TypeFuelOrStream'},
    'ECW_C11_02': {'arguments': ['Land_TypeFuelOrStream'],
                   'prefix': STREAM_ARG_TEXT % '@Land_TypeFuelOrStream'},
    'ECW_C11_10': {'arguments': ['Land_TypeFuelOrStream'],
                   'prefix': STREAM_ARG_TEXT % '@Land_TypeFuelOrStream'},
    'ECW_C12_03': {'arguments': ['Land_TypeFuel'],
                   'prefix': STREAM_ARG_TEXT % "REGFIELD(@Land_TypeFuel, 'NAME')"},
    'ECW_C12_01': {'arguments': ['Land_Streams'],
                   'prefix': "if(@Land_Streams = 'Fuel gas', 'FG_Internal', @Land_Streams)"},
}

# Словник PrefixProvider.GetPrefixes(CalculatorType.Standard), порядок вставки (перший збіг виграє).
BASE_PREFIXES = [
    ('Makat', 'FG_Makat'),
    ('Fuel gas (HMB)', 'FG_HMB'),
    ('Fuel gas (Export)', 'FG_Export'),
    ('Fuel gas (Internal)', 'FG_Internal'),
    ('LPG (HMB)', 'LPG_HMB'),
    ('LPG (Internal)', 'LPG_Internal'),
]

# Повні двомовні написання, які називає сам код старої системи (PrefixProvider: R_Air_HSE_Thermaloxidizer_General;
# ECW_C11_04/05: категорії LPG/FGHMB) і які не входять у випадний список шаблону.
LEGACY_TEXTS = [
    'Fuel gas (Makat) - Топливный газ (Makat)',
    'Fuel gas (HMB) - Топливный газ (МТБ)',
    'Fuel gas (Export) - Топливный газ (Товарный)',
    'Fuel Gas (Internal) - Топливный газ (Внутренний)',
    'LPG (HMB) - СУГ (МТБ)',
    'LPG (Internal) - СУГ (Внутренний)',
]

# Довідники шаблону, значення яких є «типом палива/потоку» у випадних списках таблиць 9-11.
FUEL_LIST_REGISTRIES = ['LAND_STATIONARY_TYPE_OF_FUEL']

# Колонка-джерело тексту потоку за таблицею. Лише там, де вона ОДНА: у таблицях 9 (не 9a) - колонка `TypeOfFuel`
# (заголовок «Type of fuel» = Land_TypeFuel*), у 10_01/11_01 - Lookup `Land_TypeFuel` (селектор C12_03, назва запису
# довідника). Таблиці 9a мають одразу `Stream` і `TypeOfStream`, 10_03/11_03 - селектор «Land_TypeStream, інакше
# Land_TypeFuel»: це питання власнику, а не здогад.
STREAM_SOURCE = {
    'T9_02': ('TypeOfFuel', 'String'),
    'T9_04': ('TypeOfFuel', 'String'),
    'T9_07': ('TypeOfFuel', 'String'),
    'T9_08': ('TypeOfFuel', 'String'),
    'T9_10': ('TypeOfFuel', 'String'),
    'T9_11': ('TypeOfFuel', 'String'),
    'T10_01': ('Land_TypeFuel', 'Lookup'),
    'T11_01': ('Land_TypeFuel', 'Lookup'),
}

REASON_SUBSTANCE = ('склад газу HSE400 по речовині: колонки-моста бути не може (значення залежить від речовини '
                    'проходу, а діалект шаблонів не має CURRENT_SUBSTANCE()); у формулі версії методології '
                    '@CompositionMass замінюється фрагментом через довідники ' + COMPOSITION_REGISTRY + ' і '
                    + SUBSTANCE_REGISTRY + ' (argumentBridges.hse400.compositionMass; Build-LandTemplate -Stage '
                    'CompositionMass)')
REASON_STREAM = {
    '9a': 'колонка-джерело потоку не доведена: таблиця має і Stream, і TypeOfStream (селектор Land_TypeFuelOrStream)',
    '10_03': 'селектор потоку C12_02: Land_TypeStream, інакше Land_TypeFuel - колонка-джерело не доведена',
    'other': 'колонка-джерело потоку не доведена (немає однозначної колонки типу палива/потоку)',
}


def entry_code(text):
    """Стабільний код запису словника потоків: S + 10 hex SHA1 від нижнього регістру тексту (UTF-8)."""
    return 'S' + hashlib.sha1(text.lower().encode('utf-8')).hexdigest()[:10].upper()


def legacy_prefix(text):
    """Правило старої системи: перший ключ словника, що міститься в тексті або містить його (без регістру)."""
    t = (text or '').lower()
    if not t:
        return None
    for key, prefix in BASE_PREFIXES:
        k = key.lower()
        if k in t or t in k:
            return prefix
    return None


def composition_expression(stream_expr, indicator):
    stream = "REGFIELD(REGFIND('%s', %s), 'PREFIX')" % (STREAM_REGISTRY, stream_expr)
    return "REGFIELD(REGFIND('%s', [Period].Year, [Period].Sequence, %s, '%s'), 'VALUE')" % (
        COMPOSITION_REGISTRY, stream, indicator)


def composition_mass_fragment(prefix_expr):
    """Текст, що підставляється замість @CompositionMass у формулу методології (діалект методологій).

    Потік - з префікса `prefix_expr`; показник - з довідника LAND_HSE400_SUBSTANCE за кодом речовини ПОТОЧНОГО проходу
    (CURRENT_SUBSTANCE()). Код речовини ≠ код запису довідника речовин (E333 при SUBSTANCE_CODE=333), тому ключ
    словника - саме SUBSTANCE_CODE (первинний ключ), а не REGFIND по LAND_AIR_SUBSTANCE.
    """
    indicator = "REGFIELD(REGFIND('%s', CURRENT_SUBSTANCE()), 'INDICATOR')" % SUBSTANCE_REGISTRY
    return "REGFIELD(REGFIND('%s', [Period].Year, [Period].Sequence, %s, %s), 'VALUE')" % (
        COMPOSITION_REGISTRY, prefix_expr, indicator)


def _substance_registry():
    ents = []
    for code, indicator in SUBSTANCE_INDICATORS:
        ents.append({'code': 'C' + code, 'name': code, 'en': '%s -> %s' % (code, indicator), 'ru': '',
                     'fields': {'SUBSTANCE_CODE': code, 'INDICATOR': indicator}})
    return {
        'code': SUBSTANCE_REGISTRY,
        'nameEn': 'Land: HSE400 composition indicator by substance code (SUBSTANCE_CODE -> {indicator}_mass)',
        'excel': 'PollutionCodeService.GetMassName (legacy backend, BaseCodes)',
        'entries': ents, 'fieldNames': ['SUBSTANCE_CODE', 'INDICATOR'], 'keyField': 'SUBSTANCE_CODE',
        'fieldSpecs': [
            {'code': 'SUBSTANCE_CODE', 'dataType': 'String', 'isKey': True},
            {'code': 'INDICATOR', 'dataType': 'String', 'isKey': False},
        ],
        'keys': [{'code': 'PK', 'fields': ['SUBSTANCE_CODE'], 'isPrimary': True, 'ignoreCase': True}],
    }


def _stream_registry(registries):
    texts = list(LEGACY_TEXTS)
    for code in FUEL_LIST_REGISTRIES:
        for e in registries.get(code, {}).get('entries', []):
            texts.append(e['name'])
    seen, ents = set(), []
    for text in texts:
        prefix = legacy_prefix(text)
        key = text.lower()
        if prefix is None or key in seen:
            continue
        seen.add(key)
        ents.append({'code': entry_code(text), 'name': text, 'en': text, 'ru': '',
                     'fields': {'TEXT': text, 'PREFIX': prefix}})
    return {
        'code': STREAM_REGISTRY, 'nameEn': 'Land: HSE400 stream dictionary (fuel/stream text -> column prefix)',
        'excel': 'PrefixProvider.GetPrefixes (legacy backend) + template fuel lists',
        'entries': ents, 'fieldNames': ['TEXT', 'PREFIX'], 'keyField': 'TEXT',
        'fieldSpecs': [
            {'code': 'TEXT', 'dataType': 'String', 'isKey': True},
            {'code': 'PREFIX', 'dataType': 'String', 'isKey': False},
        ],
        'keys': [{'code': 'PK', 'fields': ['TEXT'], 'isPrimary': True, 'ignoreCase': True}],
    }


def _composition_registry():
    return {
        'code': COMPOSITION_REGISTRY,
        'nameEn': 'Land: HSE400 gas composition (period x stream x indicator), normalized',
        'excel': 'dbo.CalcResult_AirGasCompositions_HSE400 (values are loaded locally by Import-LandHse400Composition.ps1)',
        'entries': [], 'fieldNames': ['YEAR', 'MONTH', 'STREAM', 'INDICATOR', 'VALUE'], 'keyField': 'YEAR',
        'fieldSpecs': [
            {'code': 'YEAR', 'dataType': 'Int', 'isKey': True},
            {'code': 'MONTH', 'dataType': 'Int', 'isKey': True},
            {'code': 'STREAM', 'dataType': 'String', 'isKey': True},
            {'code': 'INDICATOR', 'dataType': 'String', 'isKey': True},
            {'code': 'VALUE', 'dataType': 'Decimal', 'isKey': False},
        ],
        'keys': [{'code': 'PK', 'fields': ['YEAR', 'MONTH', 'STREAM', 'INDICATOR'], 'isPrimary': True, 'ignoreCase': True}],
    }


def _stream_reason(tcode):
    if tcode.startswith('T9a_'):
        return REASON_STREAM['9a']
    if tcode == 'T10_03':
        return REASON_STREAM['10_03']
    return REASON_STREAM['other']


def apply(out, registries):
    """Додає довідники й мости; повертає кількість доданих колонок."""
    registries[STREAM_REGISTRY] = _stream_registry(registries)
    registries[COMPOSITION_REGISTRY] = _composition_registry()
    registries[SUBSTANCE_REGISTRY] = _substance_registry()

    by_code = {}
    for s_ in out['sheets']:
        for t_ in s_['tables']:
            by_code.setdefault(t_['code'], t_)

    ab = out['argumentBridges']
    bridged, unresolved = ab['bridged'], ab['unresolved']
    hse_names = set(INDICATOR_OF_ARG) | set(SUBSTANCE_ARGS)
    added = 0
    source_used = {}

    for tcode in sorted(unresolved):
        left = unresolved[tcode]
        hse = [n for n in left if n in hse_names]
        if not hse:
            continue
        t_ = by_code[tcode]
        cols = {c['code']: c for c in t_['columns']}
        src = STREAM_SOURCE.get(tcode)
        stream_expr = None
        if src is not None:
            name, kind = src
            if name not in cols or cols[name]['dataType'] != kind:
                raise SystemExit('hse400: %s: колонка-джерело потоку %s (%s) відсутня' % (tcode, name, kind))
            stream_expr = '[%s]' % name if kind == 'String' else "REGFIELD([%s], 'NAME')" % name
            source_used[tcode] = name

        for arg in hse:
            if arg in SUBSTANCE_ARGS:
                left[arg] = REASON_SUBSTANCE
                continue
            if stream_expr is None:
                left[arg] = _stream_reason(tcode)
                continue
            if arg in cols:
                raise SystemExit('hse400: %s: колонка %s уже є' % (tcode, arg))
            col_ = {'headerRu': '', 'hidden': False, 'readOnly': True, 'excel': None, 'month': None,
                    'precision': 28, 'scale': 12, 'code': arg, 'dataType': 'Formula',
                    'headerEn': '%s (argument bridge: HSE400 composition)' % arg, 'unit': None,
                    'expression': composition_expression(stream_expr, INDICATOR_OF_ARG[arg]),
                    'ecrAdded': 'bridge: methodology argument %s = %s[%s] by period and stream (LP-10, HSE400)'
                                % (arg, COMPOSITION_REGISTRY, INDICATOR_OF_ARG[arg])}
            t_['columns'].append(col_)
            bridged.setdefault(tcode, [])
            bridged[tcode] = sorted(set(bridged[tcode]) | {arg})
            del left[arg]
            added += 1
        if not left:
            del unresolved[tcode]
        else:
            unresolved[tcode] = dict(sorted(left.items()))

    ab['bridged'] = dict(sorted(bridged.items()))
    ab['hse400'] = {
        'registries': [STREAM_REGISTRY, COMPOSITION_REGISTRY, SUBSTANCE_REGISTRY],
        # CompositionMass - склад ПО РЕЧОВИНІ: у шаблоні (діалект без CURRENT_SUBSTANCE) колонки-моста бути не може;
        # у формулі версії методології `@CompositionMass` замінюється фрагментом (Build-LandTemplate -Stage
        # CompositionMass; Convert-LandHse400CompositionMassFormula). Показник береться з LAND_HSE400_SUBSTANCE.
        'compositionMass': {
            'substanceRegistry': SUBSTANCE_REGISTRY,
            'substanceIndicators': dict(SUBSTANCE_INDICATORS),
            'rule': 'substance code not in LAND_HSE400_SUBSTANCE or no composition record -> #N/A (legacy: Unknown_mass '
                    '/ null -> no result for the row)',
            'methodologies': {
                name: {'arguments': spec['arguments'], 'fragment': composition_mass_fragment(spec['prefix'])}
                for name, spec in sorted(COMPOSITION_MASS_METHODOLOGIES.items())
            },
        },
        'indicatorOfArgument': dict(sorted(INDICATOR_OF_ARG.items())),
        'streamSourceColumn': dict(sorted(source_used.items())),
        'rule': 'no record for (year, month, stream, indicator) -> #N/A -> row has no results (legacy: row skipped)',
        'unitsNote': 'unit not set: the legacy unit is unverified (FInfo/AIR.xml)',
    }
    print('hse400 bridges: columns %d, tables %d; registries %s, %s (stream entries %d)' % (
        added, len(source_used), STREAM_REGISTRY, COMPOSITION_REGISTRY, len(registries[STREAM_REGISTRY]['entries'])))
    return added

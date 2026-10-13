# -*- coding: utf-8 -*-
"""LP-10: мости аргументів методологій C08/C09/C11/C12 на колонки введення таблиць 6, 9-11 за АТРИБУТАМИ AF-таблиць.

Чому це потрібно. Збірка аргументів (`CalculationInputBuilder`) бере значення аргументу з колонки з ТИМ САМИМ кодом у
таблиці, до якої прив'язана методологія; публікація (`CheckArgumentTableColumns`, ECR-CALC-0438) вимагає такої колонки для
КОЖНОГО аргументу формул версії в КОЖНІЙ прив'язаній таблиці (а правило категорії - ECR-CALC-0422). Старі колонки шаблону
названо за заголовком Excel (`TypeOfFuel`, `IDCodeNAEProject`), а аргументи методологій - за іменем атрибута AF
(`Land_TypeFuelOrStream`, `Land_NAE`, `Land_Measure_OperatingTime`). Місток - Formula-колонка з кодом аргументу, що копіює
(для місячних - вибирає за [Period].Sequence) колонку введення.

Що ДОВЕДЕНО (інакше - в argumentBridges.unresolved з причиною, нічого не вгадуємо):
  1. Атрибути AF-таблиці й їх ПОРЯДОК - з типізованих класів старої системи (af-land-table-attributes.json, його складає
     extract_af_table_attributes.py з DllProject/Types/Air/Land/T_Air_P*.cs). Мапер Q_Air_*.cs читає колонки за тим самим
     іменем, тож ім'я атрибута = ім'я аргументу методології.
  2. Колонка Excel для атрибута: за заголовком (сім'я величини: operating time, running hours, consumption ...) і порядком
     появи: n-й атрибут сім'ї в класі = n-та колонка сім'ї в таблиці Excel; кількості мусять збігатись, а підказки
     одиниць (days/hours, kg/h, kg, Sm3/h, Sm3) - не суперечити.
  3. ПЕРЕЙМЕНУВАННЯ за таблицею, прямо написані в коді методологій (RENAMES: файл:рядки) - наприклад, на 9a_27 методології
     C11_01/C11_02 беруть Land_Measure_OperatingTime з Land_Measure_OperTimeDays.
  4. DaysInCalcMonth у C11_11_01 і C12_04_02 - календарні дні (DateTime.DaysInMonth), а не відпрацьовані дні (правило 7.7
     C07_07): це й є `[Period].Days`.
Міст створюється лише якщо ВСІ методології таблиці, що вживають аргумент, дають ОДНЕ джерело (інакше - конфлікт, питання).
"""
import re

# Похідні аргументи збірки (DerivedArgumentNames) і контекстні (MethodologyPublishChecks.DefaultContextualArguments):
# колонки не вимагають.
DERIVED_ARGUMENTS = {'Total', 'Duration', 'FlareUnitMode', 'IsPilot', 'Category', 'CalculationDate', 'Location'}

CAL_DAYS_METHODOLOGIES = {
    'ECW_C11_11_01': 'ECW_C11_11_01.cs:76 (DateTime.DaysInMonth)',
    'ECW_C12_04_02': 'ECW_C12_04_02.cs:71 (DateTime.DaysInMonth)',
}

# (методології, таблиці, аргумент, атрибут-джерело AF, посилання на код старої системи)
RENAMES = [
    (['ECW_C11_01', 'ECW_C11_02'], ['T9a_27'], 'Land_Measure_OperatingTime', 'Land_Measure_OperTimeDays',
     'ECW_C11_01.cs:93-103, ECW_C11_02.cs:94-104'),
    (['ECW_C11_01'], ['T9a_17'], 'Land_Measure_OperatingTime', 'Land_Measure_MaterialResidence', 'ECW_C11_01.cs:335-341'),
    (['ECW_C11_03'], ['T9a_17', 'T9_06', 'T9_09'], 'Land_Measure_RunningHours', 'Land_Measure_OperatingTime',
     'ECW_C11_03.cs:114-134'),
    (['ECW_C11_03'], ['T9_13'], 'Land_Measure_RunningHours', 'Land_OperatingTime', 'ECW_C11_03.cs:137-142'),
    (['ECW_C11_10'], ['T9a_24', 'T9a_25', 'T9a_26'], 'Land_Measure_SubstanceConsumption', 'Land_Measure_ReleaseColdVen',
     'ECW_C11_10.cs:143-153'),
    (['ECW_C11_11_02'], ['T9_07'], 'Land_Measure_SubstanceConsumption', 'Land_Measure_SubstanceConsumptionSm3',
     'ECW_C11_11_02.cs:75-84'),
    (['ECW_C11_11_02'], ['T9a_19'], 'Land_Measure_SubstanceConsumption', 'Land_Measure_AmountOil',
     'ECW_C11_11_02.cs:86-95'),
]

# Сім'ї вимірюваних атрибутів: (ім'я сім'ї, regex заголовка (нижній регістр), атрибути AF у порядку класу береться з класу).
FAMILIES = [
    ('optime', r'^operating time',
     ['Land_Measure_OperatingTime', 'Land_Measure_OperTimeDays', 'Land_Measure_OperatingTimeDays', 'Land_OperTimeDays',
      'Land_OperatingTime']),
    ('running', r'^running hours', ['Land_Measure_RunningHours']),
    ('cons', r'^(substance consumption|fuel consumption|liquid fuel consumption|pumped volume)',
     ['Land_Measure_SubstanceConsumption', 'Land_Measure_SubstanceConsumptionKg', 'Land_Measure_SubstanceConsumptionKgH',
      'Land_Measure_SubstanceConsumptionSm3', 'Land_Measure_SubstanceConsumptionSm3H',
      'Land_Measure_FuelSubstanceConsumptionOrPumpedVolume', 'Land_Measure_FuelConsumption']),
    ('gas', r'^gas co(n)?sumption', ['Land_Measure_GasConsumption']),
    ('maxcons', r'^maximum consumption', ['Land_Measure_MaximumConsumption']),
    ('qty', r'^quantity of liquid', ['Land_Measure_QuantityLiquid']),
    ('resid', r'^material residence', ['Land_Measure_MaterialResidence']),
    ('sulfur', r'^flow rate of sulfur', ['Land_Measure_FlowRateSulfur', 'Land_FlowRateSulfur']),
    ('cold', r'^release on cold vent \(', ['Land_Measure_ReleaseColdVen']),
    ('density', r'^density', ['Land_Measure_Density20']),
    ('reid', r'^reid vapour pressure', ['Land_Measure_ReidVapourPressure']),
    ('h2s', r'^hydrogen sulfide', ['Land_Measure_HydrogenSulfide']),
    ('merc', r'^sum methyl', ['Land_Measure_SumMethylEthylMercaptans']),
    ('oil', r'^amount of oil', ['Land_Measure_AmountOil']),
    ('blocks', r'^the number of filled', ['Land_Measure_NumberFilledSulfurBlocks']),
    ('placed', r'^the amount of sulfur placement', ['Land_Measure_AmountSulfurPlacement']),
    ('placedays', r'^the duration of sulfur placement', ['Land_Measure_DurationSulfurPlacement']),
]
ATTR_FAMILY = {a: f for f, _, attrs in FAMILIES for a in attrs}
FAMILY_RX = {f: re.compile(rx) for f, rx, _ in FAMILIES}

# Невимірювані атрибути: однозначний заголовок у не-місячних колонках.
DIRECT = {
    'Land_NAE': r'^id code - (nae|mpe) project$',
    'Land_InvNumEqp': r'^inventory number of equipment$',
    'Land_TypeFuel': r'^(type of fuel|stream)$',
    'Land_TypeFuelOrStream': r'^(type of fuel|stream)$',
    'Land_Stream': r'^stream$',
    'Land_Streams': r'^streams$',
    'Land_TypeStream': r'^type of streams?$',
    'Land_TypeStreams': r'^type of streams?$',
    'Land_TypeStoredMaterial': r'^type of stored material$',
    'Land_Power': r'^power, ?kw$',
    'Land_Kpmax': r'^kp_max ',
    'Land_Gxp': r'^gxp ',
    'Land_Vhmax': r'^v.{1,2}_max ',
    'Land_ProcessTrain': r'^process train$',
    'No_DMSpump': r'^filled by promasys: dms_pump_no$',
    'No_Pump': r'^(filled by promasys: )?pump_no$',
    'No_Flanges': r'^(filled by promasys: )?flanges_no$',
    'No_Valves': r'^(filled by promasys: )?valves_no$',
    'No_SafValves': r'^(filled by promasys: )?saf_valves_no$',
}
DIRECT_RX = {a: re.compile(rx) for a, rx in DIRECT.items()}

# Причини для аргументів, яких немає як атрибута AF-таблиці (старий код бере їх інакше).
def special_reason(arg, tcode):
    if re.match(r'^Land_Measure_Component_', arg):
        return ('[GTU] значення береться з ІНШОГО рядка таблиці: GTU<n>_<код> рядка з Land_Measure_GTU_Month = місяць '
                'періоду, n - за словником NAE->GTU у коді (ECW_C12_02.cs:57-77, 109-160); формулою одного рядка '
                'не виразити - потрібен міжрядковий вираз (рішення про механізм)')
    if arg == 'Land_Power':
        return ('[похідне] Land_Power у старому коді виводиться зі словника груп NAE (powerGroup: ECW_C11_03.cs:94-114, '
                'ECW_C12_05.cs:88-105), а не з колонки таблиці')
    if arg == 'Land_MassFraction':
        return ('[на-речовину] аргумент без коду речовини; код бере Land_MassFraction_0<код> з AF-таблиці за поточною '
                'речовиною (ECW_C11_11_04.cs:128) - потрібна адресація речовини (як CompositionMass)')
    if arg == 'Category1':
        return ('[похідне] Category1 у ECW_C11_11_02 виводиться зі словника груп NAE (ECW_C11_11_02.cs:126-210), '
                'а не з колонки таблиці')
    return None


def _letter_index(letters):
    n = 0
    for ch in (letters or ''):
        n = n * 26 + (ord(ch.upper()) - 64)
    return n or 10 ** 6


def _norm(h):
    return re.sub(r'\s+', ' ', (h or '')).strip().lower()


MONTH_RX = re.compile(r'\b(january|february|march|april|may|june|july|august|september|october|november|december)\b')


def _candidates(t_):
    """Колонки введення таблиці: одиночні й місячні групи.

    Місячна група - k-та за порядком колонка в кожному з 12 місяців (заголовки по місяцях можуть відрізнятися: у Excel
    «(hours)» у січні й «(hours/day)» далі), тому групування за позицією всередині місяця, а не за текстом заголовка.
    Потрібно, щоб у кожному місяці було однакове число колонок із однаковим типом і одиницею.
    """
    singles, by_m = [], {}
    for c in t_['columns']:
        if c.get('hidden') or c.get('service') or c['dataType'] in ('Calculated', 'Formula') or c.get('ecrAdded'):
            continue
        if re.match(r'^(C_|FN_|ECW_)', c['code']):
            continue
        if c.get('month'):
            by_m.setdefault(c['month'], []).append(c)
        else:
            singles.append(c)
    out = []
    for c in singles:
        out.append({'kind': 'single', 'header': _norm(c.get('headerEn')), 'dataType': c['dataType'], 'unit': c.get('unit'),
                    'codes': [c['code']], 'order': _letter_index(c.get('excel'))})
    if by_m:
        for m in by_m:
            by_m[m].sort(key=lambda c: _letter_index(c.get('excel')))
        sizes = {len(v) for v in by_m.values()}
        ok = sorted(by_m) == list(range(1, 13)) and len(sizes) == 1
        for k in range(max(sizes)):
            m1 = by_m[min(by_m)]
            first = m1[k] if k < len(m1) else None
            if not ok or first is None:
                out.append({'kind': 'broken', 'header': _norm(first.get('headerEn')) if first else '', 'dataType': None,
                            'unit': None, 'codes': [], 'order': _letter_index(first.get('excel')) if first else 10 ** 6})
                continue
            group = [by_m[m][k] for m in range(1, 13)]
            same = len({(g['dataType'], g.get('unit')) for g in group}) == 1
            out.append({'kind': 'month' if same else 'broken', 'header': _norm(group[0].get('headerEn')),
                        'dataType': group[0]['dataType'], 'unit': group[0].get('unit'),
                        'codes': [g['code'] for g in group] if same else [],
                        'order': _letter_index(group[0].get('excel'))})
    return sorted(out, key=lambda x: x['order'])


def _map_attributes(t_, attrs):
    """атрибут AF -> (кандидат Excel | None, причина/докази). attrs - впорядкований перелік імен атрибутів AF."""
    cands = _candidates(t_)
    res = {}
    names = [a for a in attrs]
    # сім'ї вимірюваних величин: порядок = порядок класу
    for fam, rx in FAMILY_RX.items():
        fa = [a for a in names if ATTR_FAMILY.get(a) == fam]
        if not fa:
            continue
        fc = [c for c in cands if c['kind'] != 'broken' and rx.search(c['header'])]
        if not fc:
            for a in fa:
                res[a] = (None, 'у таблиці Excel немає колонки сім\'ї «%s»' % fam)
            continue
        if len(fa) != len(fc):
            for a in fa:
                res[a] = (None, 'кількість атрибутів AF сім\'ї «%s» (%d) не збігається з колонками Excel (%d)' % (fam, len(fa), len(fc)))
            continue
        pairs = list(zip(fa, fc))
        bad = None
        for a, c in pairs:
            if re.search(r'days', a) and '(days)' not in c['header']:
                bad = '%s: підказка одиниці (days) не збігається із заголовком «%s»' % (a, c['header'])
            if a.endswith('KgH') and '(kg/h)' not in c['header']:
                bad = '%s: підказка (kg/h) не збігається із заголовком «%s»' % (a, c['header'])
            if a.endswith('Kg') and '(kg)' not in c['header']:
                bad = '%s: підказка (kg) не збігається із заголовком «%s»' % (a, c['header'])
            if a.endswith('Sm3H') and '(sm3/h)' not in c['header']:
                bad = '%s: підказка (Sm3/h) не збігається із заголовком «%s»' % (a, c['header'])
            if a.endswith('Sm3') and not a.endswith('Sm3H') and '(sm3)' not in c['header']:
                bad = '%s: підказка (Sm3) не збігається із заголовком «%s»' % (a, c['header'])
            if len(fa) > 1 and re.search(r'OperatingTime$', a) and '(days)' in c['header']:
                bad = '%s: у сім\'ї кілька колонок, а заголовок «%s» - дні' % (a, c['header'])
        for a, c in pairs:
            res[a] = (None, bad) if bad else (c, 'header+order')
    # прямі атрибути
    singles = [c for c in cands if c['kind'] == 'single']
    for a in names:
        rx = DIRECT_RX.get(a)
        if rx is None:
            continue
        hits = [c for c in singles if rx.search(c['header'])]
        if len(hits) == 1:
            res[a] = (hits[0], 'header+order')
        elif not hits:
            res[a] = (None, 'у таблиці Excel немає колонки із заголовком за шаблоном %s' % DIRECT[a])
        else:
            res[a] = (None, 'неоднозначно: %d колонок із заголовком за шаблоном %s' % (len(hits), DIRECT[a]))
    return res


def stream_sources(out, af_attrs):
    """Джерело тексту потоку (для HSE400) за таблицею, ДОВЕДЕНЕ атрибутами AF і кодом старої системи.

    Кожна методологія читає потік з атрибута Land_TypeFuelOrStream (C11_01/02/08, C11_04/C11_10 на 9_04 і 9a_27),
    Land_TypeFuel (C11_04/05/06/10, C12_03; C12_02 на 10_03) або Land_TypeStream (C12_02). AF-таблиця має лише ті,
    що є в її класі: беремо перший наявний у порядку FuelOrStream, Fuel, Stream і колонку Excel за заголовком.
    Повертає {таблиця: (код колонки, тип)} лише там, де колонка однозначна (рядок String або Lookup Land_TypeFuel).
    """
    res = {}
    for s_ in out['sheets']:
        for t_ in s_['tables']:
            tcode = t_['code']
            if not (tcode.startswith('T9a_') or tcode in ('T10_03', 'T11_03')) or tcode in res:
                continue
            info = af_attrs.get(tcode)
            if not info:
                continue
            names = [a['name'] for a in info['attributes']]
            attr = next((a for a in ('Land_TypeFuelOrStream', 'Land_TypeFuel', 'Land_TypeStream') if a in names), None)
            if attr is None:
                continue
            cols = {c['code']: c for c in t_['columns']}
            if attr == 'Land_TypeFuel' and cols.get('Land_TypeFuel', {}).get('dataType') == 'Lookup':
                res[tcode] = ('Land_TypeFuel', 'Lookup')
                continue
            cand, _ = _map_attributes(t_, [attr]).get(attr, (None, None))
            if cand is not None and cand['kind'] == 'single' and cand['dataType'] == 'String':
                res[tcode] = (cand['codes'][0], 'String')
    return res


def apply(out, ro_all, rules, af_attrs, calc_tables):
    by_code = {}
    for s_ in out['sheets']:
        for t_ in s_['tables']:
            by_code.setdefault(t_['code'], t_)
    arg_rx = re.compile(r'@([A-Za-z_][A-Za-z0-9_]*)')
    rule_args = {r['methodology']: set(arg_rx.findall(r.get('expression') or '')) for r in rules}
    ab = out['argumentBridges']
    bridged, unresolved = ab['bridged'], ab['unresolved']
    hse_names = {'CompositionMass', 'Density20', 'Density0', 'Density', 'Vddg', 'Vwdg', 'TotalSulphurContent',
                 'MercaptanSulphurContent', 'FuelLowerHeating', 'H2Smass'}
    calendar = {'CalculationMonth', 'DaysInCalcYear', 'Winter'}
    added, evidences = 0, {}

    # методології кожної таблиці
    tab_meth = {}
    for m, d in ro_all.items():
        if not re.match(r'^ECW_C(08|09|11|12)', m):
            continue
        for t in d['tables']:
            if t in by_code and t not in calc_tables:
                tab_meth.setdefault(t, []).append(m)

    for tcode in sorted(tab_meth):
        t_ = by_code[tcode]
        cols = {c['code']: c for c in t_['columns']}
        attrs_info = af_attrs.get(tcode)
        attr_names = [a['name'] for a in attrs_info['attributes']] if attrs_info else []
        mapping = _map_attributes(t_, attr_names) if attrs_info else {}

        # потреби: аргумент -> {методологія: джерело}
        need = {}
        for m in sorted(tab_meth[tcode]):
            d = ro_all[m]
            args = set(d.get('inputs', [])) | rule_args.get(m, set()) | set(d.get('categoryInputs', []))
            for a in sorted(args):
                if a in cols or a in hse_names or a in calendar or a in DERIVED_ARGUMENTS:
                    continue
                src, how = a, None
                if a == 'DaysInCalcMonth' and m in CAL_DAYS_METHODOLOGIES:
                    need.setdefault(a, {})[m] = ('calendar', CAL_DAYS_METHODOLOGIES[m])
                    continue
                for ms, ts, arg, attr, ref in RENAMES:
                    if m in ms and tcode in ts and arg == a:
                        src, how = attr, ref
                        break
                need.setdefault(a, {})[m] = ('attr', src, how)

        left = unresolved.setdefault(tcode, {})
        for a, per_m in sorted(need.items()):
            kinds = {v for v in per_m.values()}
            if len(kinds) > 1:
                left[a] = '[конфлікт] методології таблиці беруть аргумент з різних джерел: %s' % '; '.join(
                    '%s <- %s' % (m, v[1]) for m, v in sorted(per_m.items()))
                continue
            kind = next(iter(kinds))
            new = None
            if kind[0] == 'calendar':
                new = {'code': a, 'dataType': 'Formula', 'headerEn': '%s (argument bridge: calendar days)' % a,
                       'unit': 'one', 'expression': '[Period].Days',
                       'ecrAdded': 'bridge: %s = calendar days of the period, as the legacy code does (%s)' % (a, kind[1])}
            else:
                _, srcattr, how = kind
                if srcattr not in attr_names:
                    left[a] = special_reason(a, tcode) or (
                        '[AF-немає] AF-таблиця %s (%s) не має атрибута %s: стара система не передає аргумент (null/пропуск '
                        'рядка за requiredFields) - питання механізму' % (attrs_info['class'] if attrs_info else '?',
                                                                         'клас не знайдено' if not attrs_info else 'атрибути з Types/Air/Land',
                                                                         srcattr))
                    continue
                cand, why = mapping.get(srcattr, (None, 'атрибут AF без правила відповідності колонці Excel'))
                if cand is None:
                    left[a] = '[Excel] атрибут AF %s: %s' % (srcattr, why)
                    continue
                if cand['dataType'] not in ('Decimal', 'Int', 'String'):
                    left[a] = '[не-колонка] колонка-джерело %s має тип %s (текст/число не виводиться)' % (cand['codes'][0], cand['dataType'])
                    continue
                if cand['kind'] == 'month':
                    expr = ''.join('[Period].Sequence = %d ? [%s] : ' % (m_, cand['codes'][m_ - 1]) for m_ in range(1, 12)) + '[%s]' % cand['codes'][11]
                else:
                    expr = '[%s]' % cand['codes'][0]
                ev = 'bridge: methodology argument %s = AF attribute %s of %s, %s%s' % (
                    a, srcattr, attrs_info['class'], 'header+order' if how is None else 'legacy rename (%s)' % how,
                    '' if how is None else ' ')
                new = {'code': a, 'dataType': 'Formula', 'headerEn': '%s (argument bridge: AF attribute %s)' % (a, srcattr),
                       'unit': cand.get('unit'), 'expression': expr, 'ecrAdded': ev}
                if cand['dataType'] == 'String':
                    new['textResult'] = True
            if new is not None:
                col_ = {'headerRu': '', 'hidden': False, 'readOnly': True, 'excel': None, 'month': None, 'precision': 28, 'scale': 12}
                is_text = new.pop('textResult', False)
                col_.update({k: v for k, v in new.items() if v is not None})
                if is_text:
                    col_['precision'] = None
                    col_['scale'] = None
                t_['columns'].append(col_)
                cols[a] = col_
                bridged.setdefault(tcode, [])
                bridged[tcode] = sorted(set(bridged[tcode]) | {a})
                left.pop(a, None)
                evidences.setdefault(tcode, {})[a] = new['ecrAdded']
                added += 1
        if not left:
            unresolved.pop(tcode, None)
        else:
            unresolved[tcode] = dict(sorted(left.items()))

    ab['bridged'] = dict(sorted(bridged.items()))
    ab['afAttributeBridges'] = {
        'source': 'af-land-table-attributes.json (DllProject/Types/Air/Land/T_Air_P*.cs) + RENAMES (R_EmissionCalculationWork)',
        'renames': [{'methodologies': ms, 'tables': ts, 'argument': arg, 'sourceAttribute': attr, 'evidence': ref}
                    for ms, ts, arg, attr, ref in RENAMES],
        'calendarDays': CAL_DAYS_METHODOLOGIES,
        'rule': 'bridge only if every methodology of the table that uses the argument yields the same source',
    }
    print('af attribute bridges: columns %d, tables %d' % (added, len(evidences)))
    return added

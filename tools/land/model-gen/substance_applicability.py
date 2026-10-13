# -*- coding: utf-8 -*-
"""Застосовність речовин на рядок для методологій Land C11/C12 (рішення людини 13.10 «2»).

Склад речовин у ECR - на ВЕРСІЮ методології, а старий C# перебирає список кодів ПО РЯДКУ (потік / InvNum / NAE).
Довідник LAND_SUBSTANCE_APPLICABILITY переносить цю таблицю в дані БЕЗ міграції:

  (METHODOLOGY, SELECTOR_KIND, SELECTOR_VALUE, SUBSTANCE_CODE)   - складений первинний ключ, регістр значущий;
  маркер методології: (METHODOLOGY, '*', '*', '*')               - «ця методологія має застосовність»; без нього
                                                                   SUBSTANCE_APPLICABLE дає TRUE (правило не діє).

METHODOLOGY входить у ключ, бо ті самі тексти потоків мають РІЗНІ набори в різних методологіях (напр. `Diesel - Дизель`
є в C11_01, але C11_02 його не рахує) і бо NAE-словники C11_11_03 / C11_11_05 / C11_19 не збігаються.

Вхід: land-substance-applicability.json (extract_substance_applicability.py: словники C#, без значень прода), правила
категорії (af-land-category-rules.json - перелік допустимих текстів селектора) і довідники палива/потоків моделі.
Селектор - те, що читає правило категорії в Row-фазі: `@Land_TypeFuelOrStream`, `@Land_Streams`, `@Land_InvNumEqp`,
`@Land_NAE`, `REGFIELD(@Land_TypeFuel, 'NAME')`; для C12_04_02 - токен `diesel|oil` із того самого `in(...)`, що в правилі.
Текст селектора поза переліком запису не має -> речовини пропускаються (стара система: continue / виняток).

⛔ Нічого не вгадується: домен текстів - лише LEGACY_TEXTS, довідники шаблону й літерали `in(@arg, ...)` правил.
"""
import hashlib
import json
import re

import hse400_bridges

REGISTRY = 'LAND_SUBSTANCE_APPLICABILITY'
FUNCTION = 'SUBSTANCE_APPLICABLE'
MARKER = '*'
FIELDS = ['METHODOLOGY', 'SELECTOR_KIND', 'SELECTOR_VALUE', 'SUBSTANCE_CODE']

# Довідники шаблону, значення яких є «типом палива/потоку» у випадних списках таблиць 9-11.
FUEL_REGISTRY_RX = re.compile(r'(TYPE_?OF_?FUEL|TYPE_?OF_?STREAMS)$')

IN_RX = re.compile(r"in\(\s*@(\w+)\s*,((?:\s*'[^']*'\s*,?)+)\)")
LITERAL_RX = re.compile(r"'([^']*)'")


def _entry_code(name):
    return 'A' + hashlib.sha1(name.encode('utf-8')).hexdigest()[:10].upper()


def _rule_literals(cat_rules, methodology, argument):
    """Літерали `in(@argument, '...', ...)` правила методології (порядок появи, без дублів)."""
    out = []
    for r in cat_rules:
        if r['methodology'] != methodology:
            continue
        for m in IN_RX.finditer(r['expression']):
            if m.group(1) == argument:
                for lit in LITERAL_RX.findall(m.group(2)):
                    if lit not in out:
                        out.append(lit)
    return out


def _provider_prefix(text, entries):
    """PrefixProvider: перший ключ, що міститься в тексті або містить його (без регістру)."""
    t = text.lower()
    for key, prefix in entries:
        k = key.lower()
        if k in t or t in k:
            return prefix
    return None


def _streamfuel(text):
    """Ланцюг `fuelTypeArgument.Contains(...)` з ECW_C11_01 (кожен наступний перекриває попередній)."""
    s = ''
    if 'gas' in text or 'Gas' in text:
        s = 'FG'
    if 'LPG' in text:
        s = 'LPG'
    if 'sludge' in text:
        s = 'sludge'
    if 'caustic' in text:
        s = 'caustic'
    if 'water' in text:
        s = 'Wwater'
    for token in ('401', '14', '452'):
        if token in text:
            s = token
    if 'Diesel' in text:
        s = 'Diesel'
    return s


def _domain(registries, extra):
    texts = list(hse400_bridges.LEGACY_TEXTS)
    for code, reg in registries.items():
        if FUEL_REGISTRY_RX.search(code) and not code.startswith('LAND_HSE400'):
            for e in reg.get('entries', []):
                texts.append(e['name'])
    texts.extend(extra)
    seen, out = set(), []
    for t in texts:
        if t and t not in seen:
            seen.add(t)
            out.append(t)
    return out


def _invnum_variants(key, normalize):
    """Усі написання InvNum, які C# `Replace("Т","T").Replace("А","A")` зводить до `key` (кожна T/A окремо)."""
    reverse = {v: k for k, v in normalize.items()}  # T -> Т (кирилиця), A -> А
    positions = [i for i, ch in enumerate(key) if ch in reverse]
    variants = []
    for mask in range(1 << len(positions)):
        chars = list(key)
        for bit, pos in enumerate(positions):
            if mask >> bit & 1:
                chars[pos] = reverse[key[pos]]
        variants.append(''.join(chars))
    return variants


def _nae_variants(nae):
    """NAE старого коду - `Trim().Replace(" ", "")`; у шаблоні трапляються обидва написання (`6585(001)`, `6585 (001)`)."""
    out = [nae]
    if '(' in nae and ' (' not in nae:
        out.append(nae.replace('(', ' (', 1))
    return out


def _rows(name, spec, domain, cat_rules):
    """[(selector_value, [codes])] для методології (порядок значень - стабільний)."""
    sel = spec['selector']
    codes = spec['codes']
    rows = []
    if sel in ('prefix-chain', 'prefix-required'):
        entries = spec['providerEntries']
        raws = list(domain)
        raws += [k for k, _ in entries]
        raws += _rule_literals(cat_rules, name, spec['argument'])
        seen = set()
        for raw in raws:
            if raw in seen:
                continue
            seen.add(raw)
            prefix = _provider_prefix(raw, entries)
            if sel == 'prefix-chain':
                prefix = prefix or _streamfuel(raw) or raw
            if prefix is None or prefix not in codes:
                continue  # старий код: пропуск рядка або KeyNotFound
            rows.append((raw, codes[prefix]))
    elif sel == 'map':
        for raw, token in spec['map']:
            if token in codes:
                rows.append((raw, codes[token]))  # AsphalteneInhibitor: у словнику кодів його немає -> виняток
    elif sel == 'invnum':
        for key, key_codes in codes.items():
            for v in _invnum_variants(key, spec['normalize']):
                rows.append((v, key_codes))
    elif sel == 'contains':
        for token, token_codes in codes.items():
            rows.append((token, token_codes))
    elif sel == 'nae':
        for nae, nae_codes in codes.items():
            for v in _nae_variants(nae):
                rows.append((v, nae_codes))
    else:
        raise SystemExit('substance_applicability: невідомий selector %s' % sel)
    return rows


def _value_expression(name, spec, cat_rules):
    if 'valueExpression' in spec:
        return spec['valueExpression']
    if spec['selector'] == 'contains':
        lits = _rule_literals(cat_rules, name, spec['argument'])
        if not lits:
            raise SystemExit('substance_applicability: %s: у правилі немає in(@%s, ...) - нема з чого будувати токен'
                             % (name, spec['argument']))
        quoted = ','.join("'%s'" % lit for lit in lits)
        return "if(in(@%s,%s),'diesel','oil')" % (spec['argument'], quoted)
    return '@' + spec['argument']


def _registry(entries):
    return {
        'code': REGISTRY,
        'nameEn': 'Land: substance applicability by row key (legacy C# code lists per stream / InvNum / NAE)',
        'excel': 'ECW_C11_*/ECW_C12_* polCodeList dictionaries (legacy backend); tools/land/model-gen/extract_substance_applicability.py',
        'entries': entries, 'fieldNames': FIELDS, 'keyField': 'METHODOLOGY',
        'fieldSpecs': [{'code': f, 'dataType': 'String', 'isKey': True} for f in FIELDS],
        'keys': [{'code': 'PK', 'fields': FIELDS, 'isPrimary': True, 'ignoreCase': False}],
    }


def apply(out, registries, cat_rules, spec_path='land-substance-applicability.json'):
    """Додає довідник і опис у модель; повертає кількість записів довідника."""
    doc = json.load(open(spec_path, encoding='utf-8'))
    domain = _domain(registries, [])
    entries, seen_codes, summary = [], {}, {}

    def add(name, kind, value, code):
        label = '%s|%s|%s|%s' % (name, kind, value, code)
        ecode = _entry_code(label)
        if ecode in seen_codes:
            raise SystemExit('substance_applicability: колізія коду запису %s: %s / %s' % (ecode, label, seen_codes[ecode]))
        seen_codes[ecode] = label
        entries.append({
            'code': ecode, 'name': label, 'en': label, 'ru': '',
            'fields': {'METHODOLOGY': name, 'SELECTOR_KIND': kind, 'SELECTOR_VALUE': value, 'SUBSTANCE_CODE': code},
        })

    for name, spec in sorted(doc['methodologies'].items()):
        rows = _rows(name, spec, domain, cat_rules)
        if not rows:
            raise SystemExit('substance_applicability: %s: жодного запису - перевірте домен текстів' % name)
        add(name, MARKER, MARKER, MARKER)
        keys = 0
        for value, codes in rows:
            keys += 1
            for code in codes:
                add(name, spec['kind'], value, code)
        summary[name] = {
            'kind': spec['kind'],
            'argument': spec['argument'],
            'selector': spec['selector'],
            'valueExpression': _value_expression(name, spec, cat_rules),
            'selectorValues': keys,
            'rows': sum(len(c) for _, c in rows),
        }

    registries[REGISTRY] = _registry(entries)
    out['substanceApplicability'] = {
        'registry': REGISTRY,
        'function': FUNCTION,
        'marker': MARKER,
        'keyFields': FIELDS,
        'ruleTemplate': "if(%s('%s','{methodology}','{kind}',{valueExpression}),{rule},'')" % (FUNCTION, REGISTRY),
        'source': 'land-substance-applicability.json (tools/land/model-gen/extract_substance_applicability.py)',
        'rule': 'no record for (methodology, kind, value, substance code) while the methodology has the marker -> the substance '
                'is skipped for the row (legacy: not in the row polCodeList); no marker -> not filtered',
        'methodologies': summary,
    }
    print('substance applicability: methodologies %d, registry entries %d (%s)' % (len(summary), len(entries), REGISTRY))
    return len(entries)

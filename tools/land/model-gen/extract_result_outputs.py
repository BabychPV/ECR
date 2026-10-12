# -*- coding: utf-8 -*-
"""Збирає land-result-outputs.json: які виходи ECW_EC_* (результати емісій) методологій AF C08/C09/C11/C12
потрапляють у які таблиці шаблону Land. Вхід читається лише на рівні структури: імена методологій, імена їхніх
формул і код AF-таблиці правила; значень констант/правил/даних прода файл не містить.

  py -3.12 extract_result_outputs.py --package all-package.json --rules rules.json --out land-result-outputs.json

--package : пакет ecr-methodology-package (tools/Ecr.MethodologyImport export), методології з формулами ECW_EC_*.
--rules   : JSON-масив правил прода [{m: <методологія>, t: <AF-таблиця>, ...}] (з AF_Rules/RuleArg_*); використовуються лише m і t.
Нічого не вгадується: AF-таблиця, якої немає в таблиці відповідності нижче, зупиняє скрипт.
"""
import argparse
import collections
import json
import re
import sys

# AF-таблиця прода -> код таблиці шаблону Land (ті самі правила, що в Load-Rules.ps1 треку A).
PATTERNS = [
    (r'^AF_Air_Land_Stationary_Equipment$', lambda m: 'T3_00'),
    (r'^AF_Air_Land_Mobile_Equipment$', lambda m: 'T4_01'),
    (r'^AF_Air_Land_Other_Equipment_(5_\d+)$', lambda m: 'T' + m.group(1)),
    (r'^AF_Air_Land_Dust_Producing_Activities_(6_\d+)$', lambda m: 'T' + m.group(1)),
    (r'^AF_Air_Land_Utility_(9_\d+)$', lambda m: 'T' + m.group(1)),
    (r'^AF_Air_Land_ProcessUnits_9a_(\d+[ab]?)$', lambda m: 'T9a_' + m.group(1)),
    (r'^AF_Air_Land_Off_Utility_DI_(10_\d+)$', lambda m: 'T' + m.group(1)),
    (r'^AF_Air_Land_Off_Utility_AI_(10a_\d+)$', lambda m: 'T' + m.group(1)),
    (r'^AF_Air_Land_Off_Process_DI_(11_\d+)$', lambda m: 'T' + m.group(1)),
    (r'^AF_Air_Land_11([abcd])_Off_Process', lambda m: 'T11' + m.group(1) + '_01'),
    (r'^AF_Water_Land_07a_(\d+)$', lambda m: 'T7a_%02d' % int(m.group(1))),
    (r'^AF_Water_Land_07b$', lambda m: 'T7b_00'),   # єдина таблиця аркуша 7b (LP-09: у шаблоні це T7b_00, а не T7b)
]
# Методології, чиї таблиці вже мають колонки результатів із CALC генератора (аркуші 3, 4, 5) — тут не дублюються.
ALREADY_IN_CALC = re.compile(r'^ECW_C0[5-7]_')


def table_code(af):
    for rx, fn in PATTERNS:
        m = re.match(rx, af)
        if m:
            return fn(m)
    raise SystemExit('AF-таблиця без відповідності шаблону: %s' % af)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--package', required=True)
    ap.add_argument('--rules', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    pkg = json.load(open(a.package, encoding='utf-8-sig'))
    rules = json.load(open(a.rules, encoding='utf-8-sig'))
    tables = collections.defaultdict(set)
    for r in rules:
        tables[r['m']].add(table_code(r['t']))
    res, excluded = {}, {}
    for m in pkg['methodologies']:
        n = m['name']
        if ALREADY_IN_CALC.match(n) or n not in tables:
            continue
        outs, skip = set(), set()
        for v in m['versions']:
            for f in v['formulas']:
                nm = f['name']
                if not nm.startswith('ECW_EC_'):
                    continue
                # результат емісії: ECW_EC_tons_<компонент> (т) або ECW_EC_gsec_<компонент> (г/с); решта — проміжні значення
                (outs if re.match(r'^ECW_EC_(tons|gsec)_', nm) else skip).add(nm)
        if outs:
            res[n] = {'tables': sorted(tables[n]), 'outputs': sorted(outs)}
        if skip:
            excluded[n] = sorted(skip)
    out = {'formatVersion': 1,
           'source': 'tools/land/model-gen/extract_result_outputs.py: формули ECW_EC_* пакета методологій AF x таблиці правил AF (структура, без значень)',
           'methodologies': dict(sorted(res.items())), 'excludedOutputs': dict(sorted(excluded.items()))}
    with open(a.out, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(out, fh, ensure_ascii=False, indent=1)
        fh.write('\n')
    print('methodologies %d, tables %d' % (len(res), len({t for v in res.values() for t in v['tables']})))


if __name__ == '__main__':
    sys.exit(main())

# Будує land-spec.json (таблиці й колонки «репетиційного» шаблону LAND_REH) з пакета af-land-package.json:
# колонки-аргументи (@X у формулах) і колонки виходів ECW_EC_*. Відповідність методологія -> таблиця — як у
# tools/land/Build-LandTemplate.ps1 ($AF_TABLE). Запуск з кореня репозиторію: py -3 make-land-spec.py <вихідний.json>
import json, re, sys
p = json.load(open('docs/delivery/reference-data/land-template/af-land-package.json', encoding='utf-8-sig'))
AF = {'ECW_C05_01': 'T3_00', 'ECW_C05_02': 'T3_00', 'ECW_C05_03': 'T3_00', 'ECW_C05_04': 'T3_00', 'ECW_C05_05': 'T3_00',
      'ECW_C06_01': 'T4_01', 'ECW_C07_07': 'T5_08', 'ECW_C07_08': 'T5_09', 'ECW_C07_21': 'T5_27'}
TEXT = {'Land_Rotation', 'Land_OverhauledEngines', 'Con_Region', 'Land_TypeBurnFuel', 'Land_TypePetrStation'}
tabs = {}
for m in p['methodologies']:
    if m['name'] not in AF:
        continue
    t = tabs.setdefault(AF[m['name']], {'args': set(), 'outs': set()})
    for v in m['versions']:
        for f in v['formulas']:
            t['args'] |= set(re.findall(r'@([A-Za-z_]\w*)', f['text']))
            if f['name'].startswith('ECW_EC_'):
                t['outs'].add(f['name'])
spec = {'tables': []}
for code, t in tabs.items():
    cols = [{'code': a, 'type': 'String' if a in TEXT else 'Decimal'} for a in sorted(t['args'])]
    cols += [{'code': o, 'type': 'Calculated'} for o in sorted(t['outs'])]
    spec['tables'].append({'code': code, 'columns': cols})
json.dump(spec, open(sys.argv[1], 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

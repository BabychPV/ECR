# -*- coding: utf-8 -*-
"""Словники груп NAE зі старого бекенду -> af-land-nae-groups.json (довідкова структура, без прод-даних).

Читає C#-код методологій (DllProject/Realization/Sections/R_EmissionCalculationWork/**):
  ECW_C11_03.cs     powerGroup   - NAE -> «група потужності» Land_Power, виставляється лише на ОДНІЙ таблиці (TableName);
  ECW_C11_11_02.cs  group1       - NAE (зі зняттям пробілів) -> Category1;
  ECW_C11_11_04.cs  typeCalculationGroupTonne/type1 - коди речовин, для яких береться Land_MassFraction_0<код>.
Значення беруться ЛИШЕ з C#: нічого не вводиться руками й не береться з прода. Файл читає nae_group_bridges.py
(run-model-gen.ps1 копіює його в робочий каталог поруч з af-land-table-attributes.json).

Запуск: py -3.12 extract_nae_groups.py <корінь docs/source> <вихідний json>
"""
import json
import re
import sys
from pathlib import Path


def _find(root, name):
    hits = sorted(Path(root).rglob(name))
    if len(hits) != 1:
        raise SystemExit('extract_nae_groups: очікується рівно один %s у %s, знайдено %d' % (name, root, len(hits)))
    return hits[0]


def _dictionary(text, var):
    """[(ключ, [значення])] зі словника `Dictionary<string, List<string>> <var> = new ... { { "k", new List<string> {...} } }`."""
    decls = list(re.finditer(r'Dictionary<string,\s*List<string>>\s+%s\s*=\s*new[^{]*\{' % re.escape(var), text))
    if len(decls) != 1:
        raise SystemExit('extract_nae_groups: словник %s - оголошень %d (очікується 1)' % (var, len(decls)))
    start = decls[0].end()
    depth, i = 1, start
    while i < len(text) and depth:
        depth += {'{': 1, '}': -1}.get(text[i], 0)
        i += 1
    items = re.findall(r'\{\s*"([^"]*)"\s*,\s*new\s+List<string>\s*\{([^}]*)\}\s*\}', text[start:i - 1])
    if not items:
        raise SystemExit('extract_nae_groups: словник %s порожній' % var)
    line = text.count('\n', 0, decls[0].start()) + 1
    return [(k, re.findall(r'"([^"]*)"', vs)) for k, vs in items], line


def _table_code(af_table_name):
    """AF_Air_Land_ProcessUnits_9a_17 -> T9a_17; AF_Air_Land_Utility_9_06 -> T9_06."""
    m = re.match(r'^AF_Air_Land_(?:ProcessUnits|Utility)_(9a_\d+[a-z]?|\d+_\d+)$', af_table_name)
    if not m:
        raise SystemExit('extract_nae_groups: невідома AF-таблиця %s' % af_table_name)
    return 'T' + m.group(1)


def _groups(pairs, what):
    out = {}
    for key, naes in pairs:
        if key in out:
            raise SystemExit('extract_nae_groups: %s: ключ групи %s двічі' % (what, key))
        out[key] = naes
    seen = {}
    for key, naes in out.items():
        for nae in naes:
            if nae in seen and seen[nae] != key:
                raise SystemExit('extract_nae_groups: %s: NAE %s у двох групах (%s, %s)' % (what, nae, seen[nae], key))
            seen[nae] = key
    return out


def extract(root):
    p03 = _find(root, 'ECW_C11_03.cs')
    t03 = p03.read_text(encoding='utf-8-sig')
    guard = re.search(r'TableName\s*==\s*"([^"]+)"\)\s*\{\s*string\s+power\s*=\s*powerGroup', t03)
    if not guard:
        raise SystemExit('extract_nae_groups: у ECW_C11_03.cs не знайдено guard TableName перед powerGroup')
    power, power_line = _dictionary(t03, 'powerGroup')

    p02 = _find(root, 'ECW_C11_11_02.cs')
    t02 = p02.read_text(encoding='utf-8-sig')
    if 'Replace(" ", "")' not in t02:
        raise SystemExit('extract_nae_groups: у ECW_C11_11_02.cs немає зняття пробілів з NAE')
    group1, group1_line = _dictionary(t02, 'group1')

    p04 = _find(root, 'ECW_C11_11_04.cs')
    t04 = p04.read_text(encoding='utf-8-sig')
    types, types_line = _dictionary(t04, 'typeCalculationGroupTonne')
    type1 = dict(types).get('type1')
    if not type1 or 'arguments[$"Land_MassFraction_0{code}"]' not in t04:
        raise SystemExit('extract_nae_groups: у ECW_C11_11_04.cs немає type1 / читання Land_MassFraction_0{code}')

    return {
        'comment': 'Словники груп NAE зі старого бекенду (C#); без прод-даних. Складає extract_nae_groups.py.',
        'powerGroup': {
            'methodology': 'ECW_C11_03', 'afTable': guard.group(1), 'table': _table_code(guard.group(1)),
            'normalizesNae': False, 'source': '%s:%d' % (p03.name, power_line),
            'groups': _groups(power, 'powerGroup'),
        },
        'category1': {
            'methodology': 'ECW_C11_11_02', 'normalizesNae': True, 'source': '%s:%d' % (p02.name, group1_line),
            'groups': _groups(group1, 'group1'),
        },
        'massFraction': {
            'methodology': 'ECW_C11_11_04', 'argumentPrefix': 'Land_MassFraction_0',
            'source': '%s:%d' % (p04.name, types_line), 'type1Codes': type1,
        },
    }


def main(argv):
    if len(argv) != 3:
        raise SystemExit(__doc__)
    data = extract(argv[1])
    with open(argv[2], 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(data, fh, ensure_ascii=False, indent=1)
        fh.write('\n')
    print('NAE groups: power %d NAE in %d groups (table %s); category1 %d NAE in %d groups; type1 codes %s' % (
        sum(len(v) for v in data['powerGroup']['groups'].values()), len(data['powerGroup']['groups']),
        data['powerGroup']['table'],
        sum(len(v) for v in data['category1']['groups'].values()), len(data['category1']['groups']),
        data['massFraction']['type1Codes']))


if __name__ == '__main__':
    main(sys.argv)

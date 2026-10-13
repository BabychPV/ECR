# -*- coding: utf-8 -*-
"""Збирає af-land-table-attributes.json: впорядкований перелік атрибутів AF-таблиць Land (ім'я + тип C#) за типізованими
класами старої системи (DllProject/Types/Air/Land/T_Air_P*.cs). Це СТРУКТУРА (імена полів, порядок), значень немає.

Порядок полів класу збігається з порядком колонок введення Excel-таблиці; тому за ним (і за заголовком) будуються мости
аргументів методологій на колонки шаблону (af_attr_bridges.py). Той самий клас читають мапери Q_Air_*.cs
(`reader.GetOrdinal("<ім'я>")`) - ім'я поля = ім'я аргументу методології.

  py -3.12 extract_af_table_attributes.py --types "<...>\\DllProject\\Types\\Air\\Land" --out af-land-table-attributes.json
"""
import argparse
import glob
import json
import os
import re
import sys

SKIP_PROPS = {'Identity', 'Contract'}

# клас -> код таблиці шаблону Land
RULES = [
    (r'^T_Air_P06_Dust_Producing_Activities_(\d+)$', lambda m: 'T6_' + m.group(1)),
    (r'^T_Air_P09_Utility_(\d+)$', lambda m: 'T9_' + m.group(1)),
    (r'^T_Air_P09a_ProcessUnits_(\d+[ab]?)$', lambda m: 'T9a_' + m.group(1)),
    (r'^T_Air_P10_Off_Utility_DI_(\d+)$', lambda m: 'T10_' + m.group(1)),
    (r'^T_Air_P10a_Off_Utility_AI_(\d+)$', lambda m: 'T10a_' + m.group(1)),
    (r'^T_Air_P11_Off_Process_DI_(\d+)$', lambda m: 'T11_' + m.group(1)),
    (r'^T_Air_P11a_Off_Process_AI$', lambda m: 'T11a_01'),
    (r'^T_Air_P11b_Off_Process_EPC2$', lambda m: 'T11b_01'),
    (r'^T_Air_P11c_Off_Process_EPC3$', lambda m: 'T11c_01'),
    (r'^T_Air_P11d_Off_Process_EPC4$', lambda m: 'T11d_01'),
]


def table_of(cls):
    for rx, fn in RULES:
        m = re.match(rx, cls)
        if m:
            return fn(m)
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--types', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    tables = {}
    for f in sorted(glob.glob(os.path.join(a.types, 'T_Air_P*.cs'))):
        txt = open(f, encoding='utf-8-sig').read()
        pos = [(m.start(), m.group(1)) for m in re.finditer(r'public class (T_Air_\w+)', txt)]
        for i, (st, cls) in enumerate(pos):
            en = pos[i + 1][0] if i + 1 < len(pos) else len(txt)
            tcode = table_of(cls)
            if tcode is None:
                continue
            props = re.findall(r'public\s+([\w?<>]+)\s+(\w+)\s*\{\s*get;\s*set;\s*\}', txt[st:en])
            attrs = [{'name': n, 'type': t} for t, n in props if n not in SKIP_PROPS]
            if attrs:
                tables[tcode] = {'class': cls, 'attributes': attrs}
    out = {'formatVersion': 1,
           'source': 'tools/land/model-gen/extract_af_table_attributes.py: DllProject/Types/Air/Land/T_Air_P*.cs '
                     '(імена й порядок полів AF-таблиць; значень немає)',
           'tables': dict(sorted(tables.items()))}
    with open(a.out, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(out, fh, ensure_ascii=False, indent=1)
        fh.write('\n')
    print('tables %d' % len(tables))


if __name__ == '__main__':
    sys.exit(main())

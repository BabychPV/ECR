# -*- coding: utf-8 -*-
"""Збирає land-substance-applicability.json: ЯКІ коди речовин старий код Land рахує для рядка, залежно від ключа рядка.

Склад речовин у ECR заданий на ВЕРСІЮ методології, а старі класи ECW_C11_* / ECW_C12_* перебирають список кодів ПО
РЯДКУ (за потоком / InvNum / NAE). Файл - вхід генератора моделі (gen_ecr_model.py -> довідник
LAND_SUBSTANCE_APPLICABILITY, див. substance_applicability.py). Значень прода тут немає: лише словники з C#
(`pollutionCodeByFuelType`, `polCodeList`, `pollutionCodeByInvNum`, `EC`, `codeGroup`, `codePlGroup`) і PrefixProvider.

  py -3.12 extract_substance_applicability.py --source-root H:\\ECR\\docs\\source --out land-substance-applicability.json
  py -3.12 extract_substance_applicability.py --source-root H:\\ECR\\docs\\source --check docs/.../land-substance-applicability.json
                                                  # без запису, код 1 при розбіжності

Нічого не вгадується: словник, якого немає у файлі методології, зупиняє скрипт. Повторний запуск дає побітно той самий файл.
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
# docs/source/ у .gitignore: старий код лежить лише в основному клоні; корінь - параметр --source-root.
BACKEND_REL = os.path.join('ECR Current Back end', 'Assembly', 'DllProject', 'DllProject')
DEFAULT_OUT = os.path.join(REPO, 'docs', 'delivery', 'reference-data', 'land-template', 'land-substance-applicability.json')

# Методологія -> як старий код вибирає список кодів речовин рядка.
#   selector 'prefix-chain'    : PrefixProvider(provider) -> інакше ланцюг streamfuel -> інакше сам текст (C11_01);
#   selector 'prefix-required' : PrefixProvider(provider); немає префікса - рядок пропущено/виняток (C11_02, C11_10, C12_03);
#   selector 'map'             : словник тексту потоку -> токен (C12_01), далі словник токен -> коди;
#   selector 'invnum'          : InvNum із заміною кириличних Т/А на латинські (C12_04_01);
#   selector 'contains'        : підрядок у тексті (C12_04_02);
#   selector 'nae'             : NAE (Trim + без пробілів) входить у список; словник «код -> список NAE» (C11_11_03/05, C11_19).
METHODOLOGIES = {
    'ECW_C11_01': dict(folder='ECW_C11_OPF_Plant_Operational', kind='Stream', argument='Land_TypeFuelOrStream',
                       selector='prefix-chain', provider='Standard', dict_var='pollutionCodeByFuelType'),
    'ECW_C11_02': dict(folder='ECW_C11_OPF_Plant_Operational', kind='Stream', argument='Land_TypeFuelOrStream',
                       selector='prefix-required', provider='ECW_C11_02', dict_var='pollutionCodeByFuelType'),
    'ECW_C11_10': dict(folder='ECW_C11_OPF_Plant_Operational', kind='Stream', argument='Land_TypeFuelOrStream',
                       selector='prefix-required', provider='ECW_C11_10', dict_var='pollutionCodeByFuelType'),
    'ECW_C12_03': dict(folder='ECW_C12_OFFSHORE_OPERATIONAL', kind='TypeFuel', argument='Land_TypeFuel',
                       selector='prefix-required', provider='Standard', dict_var='pollutionCodeByFuelType',
                       value_expression="REGFIELD(@Land_TypeFuel, 'NAME')"),
    'ECW_C12_01': dict(folder='ECW_C12_OFFSHORE_OPERATIONAL', kind='Stream', argument='Land_Streams',
                       selector='map', map_var='typeOfStreamsInRDS', dict_var='polCodeList'),
    'ECW_C12_04_01': dict(folder='ECW_C12_OFFSHORE_OPERATIONAL', kind='InvNum', argument='Land_InvNumEqp',
                          selector='invnum', dict_var='pollutionCodeByInvNum'),
    'ECW_C12_04_02': dict(folder='ECW_C12_OFFSHORE_OPERATIONAL', kind='TypeFuel', argument='Land_TypeStoredMaterial',
                          selector='contains', contains='Diesel'),
    'ECW_C11_11_03': dict(folder='ECW_C11_OPF_Plant_Operational', kind='NAE', argument='Land_NAE',
                          selector='nae', dict_var='EC'),
    'ECW_C11_11_05': dict(folder='ECW_C11_OPF_Plant_Operational', kind='NAE', argument='Land_NAE',
                          selector='nae', dict_var='codeGroup'),
    'ECW_C11_19': dict(folder='ECW_C11_OPF_Plant_Operational', kind='NAE', argument='Land_NAE',
                       selector='nae', dict_var='codePlGroup'),
}

COMMENT_RX = re.compile(r'//[^\n]*')
STRING_RX = re.compile(r'"([^"]*)"')


def read(path):
    with open(path, encoding='utf-8-sig') as f:
        return f.read()


def strip_comments(text):
    """Рядки // у C# цих файлів не містять `//` усередині літералів - достатньо."""
    return COMMENT_RX.sub('', text)


def balanced_block(text, start):
    """Текст між відповідними `{` і `}`, починаючи з першої `{` не раніше `start`."""
    i = text.index('{', start)
    depth = 0
    for j in range(i, len(text)):
        if text[j] == '{':
            depth += 1
        elif text[j] == '}':
            depth -= 1
            if depth == 0:
                return text[i + 1:j]
    raise SystemExit('незбалансовані дужки')


def declaration(text, var):
    m = re.search(r'\b' + re.escape(var) + r'\s*=\s*new\s+(?:Dictionary|List)<', text)
    if not m:
        raise SystemExit('немає оголошення %s' % var)
    return m.end()


def dict_of_lists(text, var):
    """`Dictionary<string, List<string>> var = new ... { { "k", new List<string> {...} }, ... }` -> [(k, [..])]."""
    block = balanced_block(text, declaration(text, var))
    out = []
    for m in re.finditer(r'\{\s*"([^"]*)"\s*,\s*new\s+List<string>\s*(?:\(\s*\))?\s*\{([^}]*)\}\s*\}', block):
        out.append((m.group(1), STRING_RX.findall(m.group(2))))
    if not out:
        raise SystemExit('%s: порожній словник' % var)
    return out


def dict_of_strings(text, var):
    block = balanced_block(text, declaration(text, var))
    return [(m.group(1), m.group(2)) for m in re.finditer(r'\{\s*"([^"]*)"\s*,\s*"([^"]*)"\s*\}', block)]


def prefix_providers(text):
    """PrefixProvider.GetPrefixes: {провайдер: [(ключ, префікс)] у порядку вставки}."""
    text = strip_comments(text)
    base = dict_of_strings(text, 'BasePrefixes')
    providers = {'Standard': base}
    sw = text[text.index('switch (type)'):]
    for case in re.finditer(r'case\s+CalculatorType\.(\w+):\s*return\s+new\s+Dictionary<string,\s*string>(\(BasePrefixes\))?\s*\{', sw):
        name = case.group(1)
        body = balanced_block(sw, case.end() - 1)
        extra = [(m.group(1), m.group(2)) for m in re.finditer(r'\{\s*"([^"]*)"\s*,\s*"([^"]*)"\s*\}', body)]
        providers[name] = (list(base) if case.group(2) else []) + extra
    return providers


def invert(pairs):
    """«код -> список NAE» -> «NAE -> коди» (порядок: за появою коду в словнику)."""
    by_nae = {}
    for code, naes in pairs:
        for nae in naes:
            by_nae.setdefault(nae, [])
            if code not in by_nae[nae]:
                by_nae[nae].append(code)
    return by_nae


def contains_codes(text):
    """C12_04_02: `if (fuelShort == "diesel") polCodeList = new List<string>() { "333", "2754" };`."""
    out = {}
    for m in re.finditer(r'fuelShort\s*==\s*"(\w+)"\s*\)\s*polCodeList\s*=\s*new\s+List<string>\s*\(\s*\)\s*\{([^}]*)\}', text):
        out[m.group(1)] = STRING_RX.findall(m.group(2))
    if not out:
        raise SystemExit('ECW_C12_04_02: немає списків кодів за fuelShort')
    return out


def build(source_root):
    backend = os.path.join(source_root, BACKEND_REL)
    sections = os.path.join(backend, 'Realization', 'Sections', 'R_EmissionCalculationWork')
    providers = prefix_providers(read(os.path.join(backend, 'Providers', 'PrefixProvider.cs')))
    result = {}
    for name, spec in sorted(METHODOLOGIES.items()):
        path = os.path.join(sections, spec['folder'], name + '.cs')
        text = strip_comments(read(path))
        item = {
            'kind': spec['kind'],
            'argument': spec['argument'],
            'selector': spec['selector'],
            'source': 'docs/source/ECR Current Back end/Assembly/DllProject/DllProject/Realization/Sections/'
                      'R_EmissionCalculationWork/%s/%s.cs' % (spec['folder'], name),
        }
        if 'value_expression' in spec:
            item['valueExpression'] = spec['value_expression']
        sel = spec['selector']
        if sel in ('prefix-chain', 'prefix-required'):
            prov = providers[spec['provider']]
            item['provider'] = spec['provider']
            item['providerEntries'] = [[k, v] for k, v in prov]
            item['codes'] = {k: v for k, v in dict_of_lists(text, spec['dict_var'])}
        elif sel == 'map':
            item['map'] = [[k, v] for k, v in dict_of_strings(text, spec['map_var'])]
            item['codes'] = {k: v for k, v in dict_of_lists(text, spec['dict_var'])}
        elif sel == 'invnum':
            item['normalize'] = {'Т': 'T', 'А': 'A'}  # кирилиця -> латиниця (U+0422 -> T, U+0410 -> A)
            item['codes'] = {k: v for k, v in dict_of_lists(text, spec['dict_var'])}
        elif sel == 'contains':
            item['contains'] = spec['contains']
            item['codes'] = contains_codes(text)
            item['tokens'] = {'true': 'diesel', 'false': 'oil'}
        elif sel == 'nae':
            item['codes'] = invert(dict_of_lists(text, spec['dict_var']))
        else:
            raise SystemExit('невідомий selector %s' % sel)
        result[name] = item
    return {
        'format': 1,
        'note': 'Which substance codes the legacy HSE C# classes calculate for a row, by row key (stream / invnum / NAE). '
                'Dictionaries copied from the C# sources; no prod values. Input of model-gen/substance_applicability.py '
                '(registry LAND_SUBSTANCE_APPLICABILITY).',
        'source': 'tools/land/model-gen/extract_substance_applicability.py',
        'methodologies': result,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--source-root', default=os.path.join(REPO, 'docs', 'source'),
                    help='каталог docs/source основного клону (містить ECR Current Back end)')
    ap.add_argument('--out', default=None)
    ap.add_argument('--check', default=None, help='порівняти з наявним файлом; нічого не писати')
    a = ap.parse_args()
    doc = build(a.source_root)
    text = json.dumps(doc, ensure_ascii=False, indent=1, sort_keys=False) + '\n'
    if a.check:
        have = open(a.check, encoding='utf-8').read()
        if have != text:
            print('розбіжність: %s не збігається з C#-словниками' % a.check)
            return 1
        print('збігається: %s' % a.check)
        return 0
    out = a.out or DEFAULT_OUT
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    n = sum(len(m['codes']) for m in doc['methodologies'].values())
    print('%s: методологій %d, ключів %d' % (out, len(doc['methodologies']), n))
    return 0


if __name__ == '__main__':
    sys.exit(main())

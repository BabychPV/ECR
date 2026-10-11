import json, pickle, collections, re
fe = json.load(open('model_fe.json', encoding='utf-8'))
be = json.load(open('model_be.json', encoding='utf-8'))
pfe = pickle.load(open('fe.pkl', 'rb'))
pbe = pickle.load(open('be.pkl', 'rb'))


def key(c):
    return (c['en'] or '', c['ru'] or '', c['month'], c['zone'])


res = {'sheetsFE': len(fe['sheets']), 'sheetsBE': len(be['sheets']), 'sheets': [], 'definedNames': {}, 'dictionaries': {}, 'summary': {}}
tot_cols_fe = tot_cols_be = 0
for a, b in zip(fe['sheets'], be['sheets']):
    ent = {'sheet': a['name'], 'tablesFE': len(a['tables']), 'tablesBE': len(b['tables']), 'dimensionFE': a['dimension'], 'dimensionBE': b['dimension'],
           'dataValidationFE': a['dataValidationCount'], 'dataValidationBE': b['dataValidationCount'], 'condFormatFE': a['condFormatCount'], 'condFormatBE': b['condFormatCount'],
           'mergesFE': a['mergeCount'], 'mergesBE': b['mergeCount'], 'formulaCellsFE': a['formulaCells'], 'formulaCellsBE': b['formulaCells'], 'tables': []}
    for ta, tb in zip(a['tables'], b['tables']):
        ca = collections.Counter(key(c) for c in ta['columns'])
        cb = collections.Counter(key(c) for c in tb['columns'])
        only_fe = list((ca - cb).elements())
        only_be = list((cb - ca).elements())
        tot_cols_fe += len(ta['columns'])
        tot_cols_be += len(tb['columns'])
        ent['tables'].append({'title': ta['title'], 'colsFE': len(ta['columns']), 'colsBE': len(tb['columns']), 'rowsFE': ta['rowCount'], 'rowsBE': tb['rowCount'],
                              'onlyInFE': [list(k) for k in only_fe][:30], 'onlyInBE': [list(k) for k in only_be][:30]})
    # cell-level diff counts
    sa = [x for x in pfe['sheets'] if x['name'] == a['name']][0]
    sb = [x for x in pbe['sheets'] if x['name'] == a['name']][0]
    ca, cb = sa['cells'], sb['cells']
    keys = set(ca) | set(cb)
    chg = fe_only = be_only = 0
    for k in keys:
        x, y = ca.get(k), cb.get(k)
        if x is None:
            be_only += 1
        elif y is None:
            fe_only += 1
        elif x['v'] != y['v'] or x['f'] != y['f']:
            chg += 1
    ent['cellsChanged'] = chg
    ent['cellsOnlyInFE'] = fe_only
    ent['cellsOnlyInBE'] = be_only
    res['sheets'].append(ent)
nf = {(n['name'], n['localSheetId']): n['text'] for n in pfe['names']}
nb = {(n['name'], n['localSheetId']): n['text'] for n in pbe['names']}
res['definedNames'] = {'countFE': len(nf), 'countBE': len(nb),
                       'onlyFE': sorted(str(k[0]) for k in nf if k not in nb),
                       'onlyBE': sorted(str(k[0]) for k in nb if k not in nf),
                       'changed': [{'name': k[0], 'BE': nb[k], 'FE': nf[k]} for k in nf if k in nb and nf[k] != nb[k] and not str(nf[k]).startswith('#REF')][:40]}
df, db = fe['dictionaries'], be['dictionaries']
res['dictionaries'] = {'countFE': len(df), 'countBE': len(db),
                       'changedCounts': [{'name': k, 'FE': df[k]['count'], 'BE': db[k]['count']} for k in df if k in db and df[k]['count'] != db[k]['count']],
                       'onlyFE': [k for k in df if k not in db], 'onlyBE': [k for k in db if k not in df]}
res['summary'] = {'tablesFE': sum(len(s['tables']) for s in fe['sheets']), 'tablesBE': sum(len(s['tables']) for s in be['sheets']),
                  'logicalColumnsFE': tot_cols_fe, 'logicalColumnsBE': tot_cols_be}
json.dump(res, open('fe_vs_be.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(json.dumps(res['summary']))
for s in res['sheets']:
    d = [(t['title'] and t['title'][:20], t['colsFE'], t['colsBE'], len(t['onlyInFE']), len(t['onlyInBE'])) for t in s['tables'] if t['colsFE'] != t['colsBE'] or t['onlyInFE'] or t['onlyInBE']]
    print('%-28s tables %2d/%2d  cells chg=%d FEonly=%d BEonly=%d  %s' % (s['sheet'], s['tablesFE'], s['tablesBE'], s['cellsChanged'], s['cellsOnlyInFE'], s['cellsOnlyInBE'], d[:3]))
print(res['definedNames']['onlyFE'], res['definedNames']['onlyBE'])
print(res['dictionaries'])

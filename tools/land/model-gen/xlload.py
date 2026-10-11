import sys, re, json, pickle, zipfile
import xml.etree.ElementTree as ET
from openpyxl.formula.translate import Translator
from openpyxl.utils import column_index_from_string, get_column_letter

NS = {'m': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main',
      'r': 'http://schemas.openxmlformats.org/officeDocument/2006/relationships',
      'x14': 'http://schemas.microsoft.com/office/spreadsheetml/2009/9/main',
      'xm': 'http://schemas.microsoft.com/office/excel/2006/main'}
M = '{%s}' % NS['m']
R = '{%s}' % NS['r']
X14 = '{%s}' % NS['x14']
XM = '{%s}' % NS['xm']

cell_re = re.compile(r'^([A-Z]+)(\d+)$')


def split_ref(ref):
    m = cell_re.match(ref)
    return m.group(1), int(m.group(2))


def load_shared(z):
    out = []
    root = ET.fromstring(z.read('xl/sharedStrings.xml'))
    for si in root.findall(M + 'si'):
        out.append(''.join(t.text or '' for t in si.iter(M + 't')))
    return out


def load_workbook_meta(z):
    wb = ET.fromstring(z.read('xl/workbook.xml'))
    rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
    relmap = {r.get('Id'): r.get('Target') for r in rels}
    sheets = []
    for s in wb.find(M + 'sheets'):
        rid = s.get(R + 'id')
        t = relmap[rid]
        t = t.lstrip('/')
        if not t.startswith('xl/'):
            t = 'xl/' + t
        sheets.append({'name': s.get('name'), 'sheetId': s.get('sheetId'), 'state': s.get('state', 'visible'), 'path': t})
    names = []
    dn = wb.find(M + 'definedNames')
    if dn is not None:
        for d in dn:
            names.append({'name': d.get('name'), 'localSheetId': d.get('localSheetId'), 'hidden': d.get('hidden'), 'text': d.text})
    return sheets, names


def parse_sheet(z, path, sst):
    """Return dict with meta and cells (ref -> {v,f,t})"""
    data = z.read(path)
    root = ET.fromstring(data)
    meta = {}
    dim = root.find(M + 'dimension')
    meta['dimension'] = dim.get('ref') if dim is not None else None
    sp = root.find(M + 'sheetProtection')
    meta['protected'] = sp is not None
    sv = root.find(M + 'sheetViews')
    meta['tabColor'] = None
    pr = root.find(M + 'sheetPr')
    if pr is not None:
        tc = pr.find(M + 'tabColor')
        if tc is not None:
            meta['tabColor'] = dict(tc.attrib)
        meta['codeName'] = pr.get('codeName')
    # cols
    cols = []
    c = root.find(M + 'cols')
    if c is not None:
        for col in c:
            cols.append({'min': int(col.get('min')), 'max': int(col.get('max')), 'hidden': col.get('hidden') in ('1', 'true'),
                         'width': col.get('width'), 'outline': col.get('outlineLevel')})
    meta['cols'] = cols
    # merges
    merges = []
    mc = root.find(M + 'mergeCells')
    if mc is not None:
        merges = [m.get('ref') for m in mc]
    meta['merges'] = merges
    # DV
    dvs = []
    d = root.find(M + 'dataValidations')
    if d is not None:
        for dv in d:
            f1 = dv.find(M + 'formula1')
            f2 = dv.find(M + 'formula2')
            dvs.append({'type': dv.get('type'), 'op': dv.get('operator'), 'allowBlank': dv.get('allowBlank'), 'sqref': dv.get('sqref'),
                        'f1': f1.text if f1 is not None else None, 'f2': f2.text if f2 is not None else None,
                        'showError': dv.get('showErrorMessage'), 'errStyle': dv.get('errorStyle'), 'errTitle': dv.get('errorTitle'),
                        'err': dv.get('error'), 'promptTitle': dv.get('promptTitle'), 'prompt': dv.get('prompt'), 'x14': False})
    # x14 DV in extLst
    ext = root.find(M + 'extLst')
    cf14 = 0
    if ext is not None:
        for e in ext:
            for dvs14 in e.iter(X14 + 'dataValidations'):
                for dv in dvs14:
                    f1 = dv.find(X14 + 'formula1')
                    f2 = dv.find(X14 + 'formula2')
                    ff1 = f1.find(XM + 'f').text if f1 is not None and f1.find(XM + 'f') is not None else None
                    ff2 = f2.find(XM + 'f').text if f2 is not None and f2.find(XM + 'f') is not None else None
                    sq = dv.find(XM + 'sqref')
                    dvs.append({'type': dv.get('type'), 'op': dv.get('operator'), 'allowBlank': dv.get('allowBlank'),
                                'sqref': sq.text if sq is not None else None, 'f1': ff1, 'f2': ff2,
                                'showError': dv.get('showErrorMessage'), 'errStyle': dv.get('errorStyle'), 'errTitle': dv.get('errorTitle'),
                                'err': dv.get('error'), 'promptTitle': dv.get('promptTitle'), 'prompt': dv.get('prompt'), 'x14': True})
            for cf in e.iter(X14 + 'conditionalFormatting'):
                cf14 += 1
    meta['dataValidations'] = dvs
    cfs = root.findall(M + 'conditionalFormatting')
    meta['condFmtCount'] = len(cfs) + cf14
    meta['condFmt'] = []
    for cf in cfs[:200]:
        rules = []
        for r in cf.findall(M + 'cfRule'):
            fs = [ff.text for ff in r.findall(M + 'formula')]
            rules.append({'type': r.get('type'), 'op': r.get('operator'), 'f': fs})
        meta['condFmt'].append({'sqref': cf.get('sqref'), 'rules': rules})
    tp = root.find(M + 'tableParts')
    meta['tableParts'] = len(tp) if tp is not None else 0
    meta['hasDrawing'] = root.find(M + 'drawing') is not None
    meta['hasLegacyDrawing'] = root.find(M + 'legacyDrawing') is not None
    # rows and cells
    rows_meta = {}
    cells = {}
    shared_masters = {}
    sd = root.find(M + 'sheetData')
    for row in sd:
        rn = int(row.get('r'))
        rm = {}
        if row.get('hidden') in ('1', 'true'):
            rm['hidden'] = True
        if row.get('outlineLevel'):
            rm['outline'] = row.get('outlineLevel')
        if rm:
            rows_meta[rn] = rm
        for c in row:
            ref = c.get('r')
            t = c.get('t')
            v = c.find(M + 'v')
            f = c.find(M + 'f')
            isel = c.find(M + 'is')
            val = None
            if t == 's' and v is not None:
                val = sst[int(v.text)]
            elif t == 'inlineStr' and isel is not None:
                val = ''.join(x.text or '' for x in isel.iter(M + 't'))
            elif v is not None:
                val = v.text
            fx = None
            if f is not None:
                ft = f.get('t')
                if ft == 'shared':
                    si = f.get('si')
                    if f.text:
                        shared_masters[si] = (ref, f.text)
                        fx = f.text
                    else:
                        if si in shared_masters:
                            mref, mtext = shared_masters[si]
                            try:
                                fx = Translator('=' + mtext, origin=mref).translate_formula(ref)[1:]
                            except Exception:
                                fx = mtext
                else:
                    fx = f.text
                    if ft == 'array':
                        fx = '{' + (fx or '') + '}'
            if val is None and fx is None:
                continue
            cells[ref] = {'v': val, 'f': fx, 't': t}
    meta['rowsMeta'] = rows_meta
    return meta, cells


def load(path):
    z = zipfile.ZipFile(path)
    sst = load_shared(z)
    sheets, names = load_workbook_meta(z)
    out = {'sheets': [], 'names': names}
    for s in sheets:
        meta, cells = parse_sheet(z, s['path'], sst)
        s.update(meta)
        s['cells'] = cells
        out['sheets'].append(s)
        print(s['name'], s['state'], s['dimension'], len(cells), file=sys.stderr)
    return out


if __name__ == '__main__':
    d = load(sys.argv[1])
    with open(sys.argv[2], 'wb') as f:
        pickle.dump(d, f)

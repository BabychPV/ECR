# -*- coding: utf-8 -*-
"""FLERT F5: composition bridges GCV_<X> for template HSE30X (design .sync-local/reports/FLERT-F4b-design-1112.md §1-§3).

Adapted from tools/land/model-gen/hse400_bridges.py: there the bridge reads LAND_HSE400_COMPOSITION by (period, stream
prefix, indicator); here it reads the FLERT mirror (D-476) by the row's case link:

  column GCV_<X> = REGSUM('GAS_COMPOSITION', ROW.CASE = [FlertCase] && ROW.COMPONENT = REGFIND('COMPONENT', '<code>'), ROW.MOL_PCT)

<X> is the FLERT column name (= suffix of the legacy GCV_<X> / CST.MW_GCV_<X> arguments, so imported AF formulas need
no renaming); <code> is the ECR component code. The list is NOT kept here: it is read from
src/Ecr.Application/Registries/Flert/FlertCompositionSchema.cs (ComponentColumns, 49 columns -> 46 codes). The three
long aliases (Carbon_Monoxide, Hydrogen, Oxygen) read the SHORT code (CO, H2, O2), as the mirror stores them.

TOx (Thermaloxidizer SG): the methodology argument is GCV_SOR_<X> bound to the row's GCV_<X> (rows of Type SOR);
GCV_EOR_<X> stays unbound (Q12: no EOR rows in 2026). Helium and C2H6S are forced 0 (legacy
R_Air_HSE_Thermaloxidizer_General.cs:364-365) and are not FLERT columns.

Missing case (#N/A / #MULTI) propagates through REGSUM as an error (FlertCaseLinkTests), never as 0. A component with
no composition row (NULL/0 in FLERT) sums to 0, as legacy "NULL -> 0".
"""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMA_CS = os.path.join(HERE, '..', '..', 'src', 'Ecr.Application', 'Registries', 'Flert', 'FlertCompositionSchema.cs')

COMPOSITION_REGISTRY = 'GAS_COMPOSITION'
COMPONENT_REGISTRY = 'COMPONENT'
CASE_COLUMN = 'FlertCase'
TOX_LEGACY_ZERO = ('Helium', 'C2H6S')
TOX_PREFIXES = {'SOR': 'SOR_', 'EOR': 'EOR_'}
TOX_UNBOUND = {'EOR': 'Q12: EOR columns kept but empty (no EOR rows in 2026)'}


def component_columns(schema_file=SCHEMA_CS):
    """[(FLERT column, ECR component code)] in FLERT order, parsed from FlertCompositionSchema.ComponentColumns."""
    with open(schema_file, encoding='utf-8-sig') as f:
        text = f.read()
    m = re.search(r'ComponentColumns\s*=\s*\[(.*?)\];', text, re.S)
    if not m:
        raise SystemExit('flert_bridges: ComponentColumns not found in %s' % schema_file)
    return re.findall(r'\("(\w+)",\s*"(\w+)"\)', m.group(1))


def gcv_expression(component):
    return ("REGSUM('%s', ROW.CASE = [%s] && ROW.COMPONENT = REGFIND('%s', '%s'), ROW.MOL_PCT)"
            % (COMPOSITION_REGISTRY, CASE_COLUMN, COMPONENT_REGISTRY, component))


def bridges(tox=False, pairs=None):
    """Bridge columns: [{'name': 'GCV_<X>', 'component': code | None, 'expression': text}]."""
    out = [{'name': 'GCV_' + col, 'component': comp, 'expression': gcv_expression(comp)}
           for col, comp in (pairs if pairs is not None else component_columns())]
    if tox:
        out += [{'name': 'GCV_' + z, 'component': None, 'expression': '0'} for z in TOX_LEGACY_ZERO]
    return out


def tox_arguments(case_type='SOR', pairs=None):
    """Methodology argument -> table column for one TOx case type. EOR -> {} (unbound, Q12)."""
    if case_type in TOX_UNBOUND:
        return {}
    prefix = TOX_PREFIXES[case_type]
    return {'GCV_' + prefix + b['name'][4:]: b['name'] for b in bridges(tox=True, pairs=pairs)}


def mirror_composition(flert_row, pairs=None):
    """FLERT row {column: mol%} -> mirror {component: mol%}: short name wins, long alias only when short is empty,
    NULL/0 -> no row (FlertCompositionMapping.Components)."""
    pairs = pairs if pairs is not None else component_columns()
    out = {}
    for comp in dict.fromkeys(c for _, c in pairs):
        cols = [col for col, c in pairs if c == comp]
        short = flert_row.get(comp) if comp in cols else None
        longs = [flert_row.get(col) for col in cols if col != comp]
        value = short if short else (longs[0] if longs and longs[0] else None)
        if value:
            out[comp] = value
    return out


def evaluate(flert_row, tox=False, pairs=None):
    """Values of the bridge columns for one case: {GCV_<X>: mol%} (REGSUM over the mirror; no row -> 0)."""
    mirror = mirror_composition(flert_row, pairs)
    return {b['name']: (mirror.get(b['component'], 0) if b['component'] else 0) for b in bridges(tox, pairs)}


if __name__ == '__main__':
    import json
    print(json.dumps({'SG_FLARING': bridges(), 'TOX_SG': bridges(tox=True),
                      'toxArguments': {'SOR': tox_arguments('SOR'), 'EOR': tox_arguments('EOR')}}, indent=1))

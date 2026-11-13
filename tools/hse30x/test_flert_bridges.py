# -*- coding: utf-8 -*-
"""Unit tests for flert_bridges.py. Run: py -3 -I -m unittest discover -s tools/hse30x -p "test_*.py"
(-I ignores the cwd, so the module is loaded from this file's directory explicitly)."""
import importlib.util
import json
import os
import unittest
from decimal import Decimal

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('flert_bridges', os.path.join(HERE, 'flert_bridges.py'))
fb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fb)

FIXTURE = os.path.join(HERE, '..', '..', 'tests', 'Ecr.TestKit', 'Fixtures', 'flert-1d2-370-winter.json')
PS_LIB = os.path.join(HERE, 'Hse30xModel.Lib.ps1')
ALIASES = {'Carbon_Monoxide': 'CO', 'Hydrogen': 'H2', 'Oxygen': 'O2'}


class SchemaAndBridges(unittest.TestCase):
    def setUp(self):
        self.pairs = fb.component_columns()

    def test_flert_list_49_columns_46_codes(self):
        self.assertEqual(49, len(self.pairs))
        self.assertEqual(46, len({c for _, c in self.pairs}))
        self.assertEqual(ALIASES, {col: c for col, c in self.pairs if col != c})

    def test_sg_bridges_one_per_flert_column(self):
        b = fb.bridges()
        self.assertEqual(['GCV_' + col for col, _ in self.pairs], [x['name'] for x in b])
        self.assertEqual(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = [FlertCase] && ROW.COMPONENT = REGFIND('COMPONENT', 'Methane'), ROW.MOL_PCT)",
            next(x for x in b if x['name'] == 'GCV_Methane')['expression'])
        self.assertNotIn('GCV_Helium', [x['name'] for x in b])

    def test_alias_pairs_read_the_short_code(self):
        by = {x['name']: x for x in fb.bridges()}
        for long_name, short in ALIASES.items():
            self.assertEqual(short, by['GCV_' + long_name]['component'])
            self.assertEqual(by['GCV_' + short]['expression'], by['GCV_' + long_name]['expression'])

    def test_tox_helium_and_c2h6s_are_zero(self):
        by = {x['name']: x for x in fb.bridges(tox=True)}
        self.assertEqual(51, len(by))
        self.assertEqual('0', by['GCV_Helium']['expression'])
        self.assertEqual('0', by['GCV_C2H6S']['expression'])

    def test_tox_sor_prefix_and_eor_unbound(self):
        sor = fb.tox_arguments('SOR')
        self.assertEqual(51, len(sor))
        self.assertEqual('GCV_Methane', sor['GCV_SOR_Methane'])
        self.assertEqual('GCV_Helium', sor['GCV_SOR_Helium'])
        self.assertTrue(all(k.startswith('GCV_SOR_') for k in sor))
        self.assertEqual({}, fb.tox_arguments('EOR'))

    def test_mirror_short_wins_long_only_when_short_empty_zero_is_no_row(self):
        m = fb.mirror_composition({'CO': 1.5, 'Carbon_Monoxide': 2.0, 'Hydrogen': 0.7, 'H2': None, 'O2': 0, 'Methane': 0})
        self.assertEqual({'CO': 1.5, 'H2': 0.7}, m)
        v = fb.evaluate({'Hydrogen': 0.7})
        self.assertEqual(0.7, v['GCV_H2'])
        self.assertEqual(0.7, v['GCV_Hydrogen'])
        self.assertEqual(0, v['GCV_Methane'])

    def test_powershell_model_uses_the_same_bridge_text(self):
        with open(PS_LIB, encoding='utf-8-sig') as f:
            lib = f.read()
        self.assertIn('"%s"' % fb.gcv_expression('$Component'), lib)
        self.assertIn("@('Helium', 'C2H6S')", lib)


class Golden1D2Winter(unittest.TestCase):
    """Hse301GoldenTests «1D-2 · 370 Winter»: composition -> FLERT row -> bridges -> μ, S, EF (1e-9)."""

    def setUp(self):
        with open(FIXTURE, encoding='utf-8-sig') as f:
            self.fx = json.load(f, parse_float=Decimal)
        self.pairs = fb.component_columns()
        comps = self.fx['components']
        # The fixture (hse301.xlsx C30:C63) lists the 34 components in FLERT column order; the names differ
        # (HYSYS labels), so the mapping is positional and checked on the unambiguous ones.
        self.flert_of = {c['code']: self.pairs[i][0] for i, c in enumerate(comps)}
        self.comp = {c['code']: c for c in comps}

    def test_fixture_order_is_flert_order(self):
        self.assertEqual(34, len(self.fx['components']))
        for code, col in [('N2', 'Nitrogen'), ('CO2', 'CO2'), ('CH4', 'Methane'), ('C2H6', 'Ethane'), ('BENZENE', 'Benzene'),
                          ('CH4S', 'MMercaptan'), ('C2H6S', 'EMercaptan'), ('CS2', 'CS2'), ('COS', 'COS'),
                          ('H2O', 'H2O'), ('TEG', 'TEGlycol')]:
            self.assertEqual(col, self.flert_of[code])

    def _values(self):
        row = {self.flert_of[x['component']]: x['molPct'] for x in self.fx['composition']}
        gcv = fb.evaluate(row)
        return [(gcv['GCV_' + self.flert_of[code]], c) for code, c in self.comp.items()]

    def test_mu_s_and_ef_from_bridges(self):
        vals = self._values()
        mu = sum(x * c['mw'] for x, c in vals if x and c['mw'] is not None) / 100
        s_wt = self.fx['constants']['M_S'] * sum(x * c['nS'] for x, c in vals) / mu
        ef_raw = self.fx['constants']['M_CO2'] * sum(x * c['nC'] for x, c in vals) / 100 / mu
        exp = self.fx['expected']
        self.assertLessEqual(abs(mu - exp['MU']['value']), abs(exp['MU']['value']) * Decimal('1e-9'))
        self.assertLessEqual(abs(s_wt - exp['S_WT']['value']), abs(exp['S_WT']['value']) * Decimal('1e-9'))
        self.assertEqual(exp['EF_RAW']['value'], round(ef_raw, exp['EF_RAW']['digits']))


if __name__ == '__main__':
    unittest.main()

"""Сторож довідників Land «2. Contract»: python3 -I tools/land/test_contract_dicts.py
Без Excel: перевіряє закомічені CSV і маніфест. Звірка з xlsm — якщо книгу задано в ECR_LAND_XLSM."""
import csv
import json
import os
import re
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
DATA = HERE.parents[1] / "docs/delivery/reference-data/land-contract"
COUNTS = {"area": 25, "contractor": 60, "region": 2, "location": 71, "onoffshore": 2, "activity": 10, "permit": 9}
CODE = re.compile(r"^[A-Za-z0-9_]{1,64}$")  # узгодити з EcrCode.Pattern при заливанні (L2)


def read(name):
    with open(DATA / f"{name}.csv", encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


class ContractDictionaries(unittest.TestCase):
    def test_counts(self):
        for name, n in COUNTS.items():
            self.assertEqual(len(read(name)), n, name)

    def test_codes_and_names(self):
        for name in COUNTS:
            rows = read(name)
            codes = [r["code"] for r in rows]
            names = [" ".join(r["NAME"].split()).casefold() for r in rows]
            self.assertEqual(len(set(codes)), len(rows), name)
            self.assertEqual(len(set(names)), len(rows), name)
            self.assertEqual([int(r["ordinal"]) for r in rows], list(range(1, len(rows) + 1)), name)
            for r in rows:
                self.assertRegex(r["code"], CODE)
                self.assertTrue(r["NAME"].strip() and len(r["NAME"]) <= 1000)

    def test_frozen_codes(self):
        for name, fmt in [("area", "AREA_{:03d}"), ("contractor", "CTR_{:03d}"), ("location", "LOC_{:03d}")]:
            self.assertEqual([r["code"] for r in read(name)], [fmt.format(i) for i in range(1, COUNTS[name] + 1)])

    def test_permit_code_is_number_with_underscore(self):
        for r in read("permit"):
            self.assertEqual(r["code"], r["NAME"].replace("-", "_"))

    def test_contractor_not_split(self):
        self.assertTrue(any(" - " in r["NAME"] and r["display_ru"] == "" and r["display_en"] == r["NAME"] for r in read("contractor")))

    def test_manifest(self):
        fields = json.loads((DATA / "header-fields.json").read_text(encoding="utf-8"))["fields"]
        self.assertEqual([f["key"] for f in fields], ["Area", "Contractor", "Region", "Location", "OnOffshore", "FilledBy", "ContractHolder", "ContractNumber", "TypeOfActivity", "ProcessedOn", "FileNumber", "Permit", "Version"])
        self.assertEqual([f["position"] for f in fields], list(range(1, 14)))
        lookups = [f for f in fields if f["kind"] == "Lookup"]
        self.assertEqual(len(lookups), 7)
        for f in lookups:
            self.assertTrue((DATA / f["csv"]).exists())
        self.assertEqual([f["key"] for f in fields if f.get("isRequired")], ["Permit"])

    @unittest.skipUnless(os.environ.get("ECR_LAND_XLSM"), "ECR_LAND_XLSM не задано")  # локальний режим, у CI не пропуск-гейт
    def test_matches_workbook(self):
        import extract_contract_dicts as ex
        for key, rows in ex.extract(Path(os.environ["ECR_LAND_XLSM"])).items():
            got = [(r["code"], r["NAME"], r["display_en"], r["display_ru"], int(r["ordinal"])) for r in read(key)]
            self.assertEqual(got, rows, key)


if __name__ == "__main__":
    unittest.main()

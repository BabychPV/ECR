#!/usr/bin/env python3
"""Витяг довідників вкладки «2. Contract» з аркуша DropdownList книги Land у CSV.

Використання: python3 -I tools/land/extract_contract_dicts.py <книга.xlsm> [каталог_виводу]
Детермінований: порядок рядків Excel = ordinal; коди заморожені (див. SPECS).
Потрібен openpyxl.
"""
import csv
import re
import sys
from pathlib import Path

import openpyxl

# (файл, колонка DropdownList (1-based), префікс коду, розбір «EN - RU», кількість)
SPECS = [
    ("area", 4, "AREA_{:03d}", True, 25),
    ("contractor", 5, "CTR_{:03d}", False, 60),  # НЕ ділити за « - » (Kaz - MI)
    ("region", 6, "REG_{:03d}", True, 2),
    ("location", 7, "LOC_{:03d}", True, 71),
    ("onoffshore", 8, "ONOFF_{:03d}", True, 2),
    ("activity", 9, "ACT_{:03d}", True, 10),
    ("permit", 10, None, False, 9),
]
FIRST_ROW, LAST_ROW = 5, 80
CYR = re.compile("[А-Яа-яЁё]")


def permit_code(number: str) -> str:
    return number.replace("-", "_")


def split_bilingual(name: str):
    if name.count(" - ") == 1:
        en, ru = (p.strip() for p in name.split(" - "))
        if CYR.search(ru) or ru == en:
            return en, ru
    return name, ""


def extract(xlsm: Path):
    wb = openpyxl.load_workbook(xlsm, read_only=True)
    ws = wb["DropdownList"]
    rows = list(ws.iter_rows(min_row=FIRST_ROW, max_row=LAST_ROW, min_col=4, max_col=10, values_only=True))
    result = {}
    for key, col, fmt, bilingual, expected in SPECS:
        names = []
        for r in rows:
            v = r[col - 4]
            if v is None or not str(v).strip():
                continue
            names.append(" ".join(str(v).split()))
        if len(names) != expected or len(set(names)) != expected:
            raise SystemExit(f"{key}: очікувалось {expected} унікальних, отримано {len(names)}/{len(set(names))}")
        out = []
        for i, name in enumerate(names, 1):
            code = permit_code(name) if fmt is None else fmt.format(i)
            en, ru = split_bilingual(name) if bilingual else (name, "")
            out.append((code, name, en, ru, i))
        result[key] = out
    return result


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    out_dir = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parents[2] / "docs/delivery/reference-data/land-contract"
    out_dir.mkdir(parents=True, exist_ok=True)
    for key, rows in extract(Path(sys.argv[1])).items():
        with open(out_dir / f"{key}.csv", "w", encoding="utf-8", newline="") as f:
            w = csv.writer(f, lineterminator="\n")
            w.writerow(["code", "NAME", "display_en", "display_ru", "ordinal"])
            w.writerows(rows)


if __name__ == "__main__":
    main()

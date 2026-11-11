# tools/hse400/hse400_year_dump.py
# HSE400 крок 4 (приймання): вивантаження аркуша «GasComposition <рік>» звіту HSE400.Year.xlsx у JSON для
# Compare-Hse400Year.ps1. Жодної логіки зіставлення тут немає (вона - у Hse400Year.Lib.ps1, під Pester):
# лише рядок аркуша, текст колонки A і числа місячних колонок (не число - null).
# ⛔ Значення - дані замовника: вихідний файл пишеться лише в tools/hse400/out/ (у .gitignore) або в scratch;
# у stdout - лише кількості.
# Запуск: py -E tools/hse400/hse400_year_dump.py <копія xlsx> <рік> <вихідний json>
import json
import sys

import openpyxl


def main(argv):
    if len(argv) != 4:
        print("usage: hse400_year_dump.py <xlsx> <year> <out.json>", file=sys.stderr)
        return 2
    path, year, out = argv[1], argv[2], argv[3]
    wb = openpyxl.load_workbook(path, read_only=True, data_only=True)
    name = "GasComposition " + year
    if name not in wb.sheetnames:
        print("no sheet '%s' (sheets: %s)" % (name, ", ".join(wb.sheetnames)), file=sys.stderr)
        return 3
    rows = []
    months = None
    for i, r in enumerate(wb[name].iter_rows(values_only=True), 1):
        cells = list(r) if r else []
        label = "" if not cells or cells[0] is None else str(cells[0])
        values = []
        for c in cells[1:]:
            values.append(float(c) if isinstance(c, (int, float)) and not isinstance(c, bool) else None)
        if months is None and label.strip() == "Gas composition":
            months = [None if c is None else str(c) for c in cells[1:]]
        rows.append({"row": i, "label": label, "values": values})
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"sheet": name, "months": months, "rows": rows}, f, ensure_ascii=False)
    print("rows=%d numeric=%d" % (len(rows), sum(1 for x in rows for v in x["values"] if v is not None)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

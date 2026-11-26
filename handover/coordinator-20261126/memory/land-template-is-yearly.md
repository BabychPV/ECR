---
name: land-template-is-yearly
description: "FACT the human repeated many times: the Land template is ONE template holding ALL months of the year (year sheet, months as columns). Never treat Land as a per-month template."
metadata:
  node_type: memory
  type: feedback
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-12T13:16:46.648Z
---

The human, 12.11 (frustrated, after repeating it many times): «ленд темплейт — це темплейт, в якому всі місяці року!»

The Land xlsm is a YEAR workbook. Every table that has monthly values holds them as month columns (Jan…Dec) in the same sheet. There is one document per year, no per-month copies, and no month switcher. The month matters only for submission and reopen.

**Why:** our import (1.0.14–1.0.16) kept Land as PerMonthCopies with a month switcher and computed «current month» values in single columns. The human has corrected this on 10.11, 11.11 and 12.11, and it still reached live.

**How to apply:** every Land design, plan, review, F13 and UI pass starts from «Land = year with all months». Any month switcher, per-month copy or «current month» column in Land is a DEFECT to fix, not a design choice to defend. Check the xlsm before saying otherwise. The rebuild is in progress: [[human-land-rebuild-year-1112]]. Related: [[human-template-yearly-vs-monthly-1110]], [[human-quiz-template-mode-1110]].

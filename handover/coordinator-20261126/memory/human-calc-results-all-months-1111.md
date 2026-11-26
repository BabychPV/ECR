---
name: human-calc-results-all-months-1111
description: "11.11: human — rework the Calculation results table: driven by template config (Layout), easy to read, show ALL calculations for ALL months"
metadata:
  type: project
---

Human 11.11 (after the Year-layout question on Land calculated columns): «Ти маєш переробити цю таблицю щоб вона залежала від того як сконфігурований шаблон і відображення результатів має читатися легко та зрозуміло. І треба бачити всі розрахунки за всі місяці».

**Why:** Land's 669 calculated columns are monthly values in one column; without a month picker the Year layout would show January as if yearly. The human wants every month visible.

**How to apply:** PE builds a «Calculation results» year view (months as columns, grouped sheet→table→row→methodology→output, units, totals only for additive outputs, filters, jump, export), driven by template Layout; mockup first → show the human; perf GET ≤1 s warm. Land 1.0.16.0 ships as PerMonthCopies (E) meanwhile. Related: [[human-quiz-template-mode-1110]], [[human-performance-max-1028]].
**11.11 ~15:00Z:** mockup (lane/calc-results-year-mockup 7fcf74adb, screenshots .sync-local\reports\calc-results-mockup) APPROVED «Так, робити»; year total only additive with n/12 — yes. Year-layout in-grid: option A (results-month chip). PE builds lane/integ-calc-results-year-view.

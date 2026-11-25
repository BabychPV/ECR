---
name: human-land-rebuild-year-1112
description: "12.11 human: rebuild Land for the YEAR layout (monthly calculated values become Jan–Dec columns), no month switcher; option A of the D-474 design doc"
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-12T14:47:08.220Z
---

On 12.11 the human asked why the month switcher is still in Land, because «we were supposed to remove it and adapt the dependent windows (like change history)». Quiz answer: «Перебудувати Land під рік».

So: D-474 option A. Each monthly calculated value (669 Calculated columns plus ~339 non-month formula columns that read [Period]) becomes Jan…Dec month columns, as in the xlsm. The document sheet is yearly with no month switcher; the month is kept only for submit and reopen. Code part (docs/build/D474-year-layout-calculated-cells.md §5): calculated-cell ownership and routing, the aggregate-state gate, rpt views for column types 6/8, and the move PerMonthCopies→Year for existing docs. UI part: History, Issues, Inspector, calc-results, exports and the sheet log adapted to the year view. The publish refusal (§5a) stays as the guard.

Plan approved 12.11 (quiz on .sync-local\reports\LAND-YEAR-plan-1112.md): «Так, запускай». Calculated columns are ordered PER OUTPUT (NOx Jan…Dec, then the next output), in collapsible groups. Year totals: «має бути як в ексель шаблоні, який я тобі давав», so follow the Land xlsm exactly: a total where the xlsm has one, and the same way. The calc-results «all months» screen: FINISH it for the transition period (the human overrode my «park it»). My decisions: T7b_00.ECW_EC_tons becomes monthly too; the 123 «current period» helper columns are removed.

**How to apply:** owner PE (Land builder + layout code), plan first, then slices. The rpt view part coordinates with AN's L13 (05-rpt-views.sql). New Land version: 1.0.17.0 YearWithMonthColumns, then migrate doc 96. Related: [[human-quiz-template-mode-1110]], [[human-template-yearly-vs-monthly-1110]].

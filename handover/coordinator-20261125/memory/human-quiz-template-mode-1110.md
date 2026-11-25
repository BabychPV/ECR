---
name: human-quiz-template-mode-1110
description: "10.11 quiz: month header bands go into live Land 1.0.14.0 without a new version; yearly/monthly mode is a TemplateVersion setting (O1); JanuaryRHDuringExceeding untouched; Columns tab bulk edit in parallel"
metadata:
  node_type: memory
  type: project
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-10T17:00:03.495Z
---

Quiz answers of the human on 10.11 to Permites' plan `.sync-local\reports\PERMITES-land-months-B-plan.md`:

1. Month/quarter/section header bands (names from the xlsm, en+ru) are added to the live published Land version 1.0.14.0 without a new template version (presentation only; scratch first).
2. Mode «yearly / monthly» = `TemplateVersion.MonthMode` (option O1): month picker hidden in yearly mode, month kept for Submit/Reopen, per-month column locks in ALL tables including PerPeriodInstance ones (3.00 etc.). Existing versions stay monthly; then Land 1.0.15.0 in yearly mode.
3. Column `JanuaryRHDuringExceeding` (T9_04/07/08/10, T10_03, T11_03) — do not touch until the human checks the xlsm.
4. Columns tab («Group» column + bulk «Edit selected…», all-or-nothing) — in parallel with the bands.

Later the same day the human said «так» to four more points: (a) model: reporting period belongs to the project (Monthly/Yearly), the template version has a «Layout» (per-month copies | year with month columns), rule «each cell belongs to exactly one period», Yearly project + per-month copies is forbidden, month picker only where a document has several copies; (b) the 7 PerPeriodInstance tables (3.00 etc.) become MonthsInColumns in the new Land version; (c) unhide month columns of 9C Fugitive leaks; (d) DefaultValue only for «Overhauled engines» (as in the xlsm), the other category inputs (type of equipment, rotation, description, fuel type) become required calc inputs, no invented defaults. Also found: 360 month columns in T9_04/07/08/10, T10_03, T11_03 have no month binding → one new Land version carries all fixes.

**Why:** the Land import kept month columns but lost header bands; the workbook is a year sheet, so a month switcher makes no sense there ([[human-template-yearly-vs-monthly-1110]]).

**How to apply:** PE does bands + Columns tab now; mode code starts after AN's batch-1110c merges (same calc/access files). Live run of the bands script is AU's, in a window. Do not ask these again.

**Update 10.11 ~18:15Z:** the human said «так» to binding JanuaryRHDuringExceeding to month 1 in T9_04/07/08/10, T10_03, T11_03 in the new Land version (replaces point 3 «не чіпати»).

**10.11 ~20:10Z:** human allowed PE to use the existing land-owner entry of .sync-local\land-accounts.local.txt on scratch EcrLandMB only, for the §4' new-version build (script reads it, no print, no new accounts, never live). Another run needs a new ask.

**11.11 ~12:50Z quiz (Land 1.0.16.0):** required inputs — «подивися саму методологію і визначи сам для всіх методологій» (derive per methodology for ALL Land methodologies: required where empty makes the row fail/skip; optional args not). Text→numeric fix for Benzene/Xylene/Toluene/C1C5/C6C10 in ALL tables T9a_16, T9a_17, T9a_19, T9a_24…T9a_28.

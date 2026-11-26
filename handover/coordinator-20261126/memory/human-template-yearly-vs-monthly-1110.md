---
name: human-template-yearly-vs-monthly-1110
description: "10.11: human wants a configurable template mode — yearly (month columns in tables, no month switcher in the document except submit) vs monthly (switcher stays); imported Land template dropped month columns of 3.00"
metadata:
  node_type: memory
  type: project
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-10T16:43:13.570Z
---

On 10.11 the human said (about document P1-V9-0093, no calc results): the new xlsm template and the previous one both have month columns in 3.00 Stationary Equipment, and our imported template missed them. They asked to think through configuration: a yearly template (tables with months) needs no month switching at the top of the document, except for submission; a monthly template (set somewhere in configuration) keeps the switcher. «і може щось інше що я не бачу» — list other things that depend on the mode.

**Why:** live template 1.0.14.0 stores data per month and shows a month picker, while the source workbook is a year sheet with month columns; I had wrongly assumed the active template had no months by design.

**How to apply:** Permites prepares a plan only (mode setting, storage, calc, locks, migration); show it to the human before any change. Do not claim a template lacks months without comparing with the xlsm. Related: [[human-hse401-template-1104]], [[human-periods-concept-1107]], [[human-speed-up-a-bit-1110]].

---
name: human-live-doc-cleanup-1110
description: "10.11: human allowed deleting all test documents on live (93 LANDUI2026 + P5-V14-0001), keep HSE401 doc and all configuration; snapshots deleted with them as a one-time exception"
metadata:
  node_type: memory
  type: project
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-10T16:46:48.237Z
---

On 10.11 the human said: «наразі на стенді богато документів я дозволяю видалити всі щоб не втрачати час і створити один тестовий на якому будем все тестувати. Формули шаблони константи методологі пермити залиш і 401».

Quiz answers the same day: delete 93 LANDUI2026 documents + P5-V14-0001; keep the HSE401_2026 document; submission snapshots of deleted documents go with them (one-time exception to [[human-no-snapshot-delete-1026]], which stays in force otherwise); the single new test document is created only AFTER the Land template with months is fixed.

**Why:** many stale test documents slow testing; the active template lacks month columns, so a test doc on it would be wasted.

**How to apply:** Audit-2 does it: COPY_ONLY backup first, rehearsal on scratch, then live. This word covers only this cleanup; any later deletion on live needs a new word. Related: [[human-template-yearly-vs-monthly-1110]].

**Done:** On 10.11 ~18:08Z the coordinator ran the cleanup on live: rc=0, 393 s, 94 docs deleted, HSE401 doc kept intact, 0 orphans. The single test document waits for the new Land version.

**10.11 ~18:45Z:** the human specified the single test document: «дані які охоплять всі методології» (46 active on live). AN builds fill script + coverage table; acceptance: every methodology ≥1 result, 0 rejected rows; scratch first, live after the new Land version is published.

**10.11 ~19:40Z quiz on AN coverage table** (.sync-local\reports\LAND-ALL-METHODOLOGIES-coverage.md): values from 2024/2025 Land workbooks + Параметры_2024 (read-only); Tier A 43 rows; skip m33/m46 («для деяких щитів їх немає»); missing-constant steps = separate known list, not blocking; period 202609.

**11.11 ~00:10Z quiz:** values for the 124 numeric inputs come from the deleted Land docs in backup predelete2 (read-only scratch, cite doc/cell; else plausible marked «test»). Calc/Patch/migrate perf measured on live in W15o on the test doc (no rights change on scratch; my grant letter was blocked by the classifier).

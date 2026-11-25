---
name: w15q-window-1111
description: "W15q 11.11 — live на f446e9b29 (app-w15q), BranchGate stage+publish 9 версій методик (v61–68, v59); бекапи й уроки"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-11T13:12:53.174Z
---

W15q (2026-11-11): live EcrLandUi оновлено 3135a3138 → f446e9b29 (app-w15q, junction на E:), без міграцій і сіду; K1 IDENTICAL 9D9AEBF3…; UNEXPECTED 0.
Потім у тому ж вікні: BranchGate stage (8 нових чернеток id 61–68 + наявна 59; 39 формул; 5 складів речовин; 9 тестів → doc 96/202609; «змінено: 53»), rerun = 0, publish 9/9 (HTTP 200).
Бекапи: `E:\EcrBackup\land-ui\w15q\bak\` — before-W15q-20261111-1436, after-W15q-20261111-1454, after-W15q-publish-20261111-1512.
Перерахунок doc 96 і Verify — координатор, не я.

**Why:** AN-171/172, слово людини «Так, публікувати у вікні».
**How to apply:** наступне вікно копіювати з `w15q-drafts`; PrevBak = after-W15q-publish; K1 після перерахунку doc 96 зміниться — переміряти базу перед наступним вікном. Stage з `-BranchGateCreateDrafts` після публікації НЕ запускати (створить V(n+2)). C: тримати ≥15 ГБ до dry run (перша спроба впала на 14 ГБ). Скрипти: `C:\EcrScratch\w15q\live-branchgate.ps1`, `live-publish.ps1` (EcrApi.ps1 вмикає StrictMode — у хеш-таблицях `ContainsKey`, не `.Key`). Див. [[w15p-window-1111]], [[human-land-branch-gating-1111]].

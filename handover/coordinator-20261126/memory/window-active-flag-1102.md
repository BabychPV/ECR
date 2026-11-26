---
name: window-active-flag-1102
description: Перед будь-якою важкою локальною збіркою/тестом перевіряти .sync-local/WINDOW-ACTIVE (вікно на живому стенді)
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-02T12:59:03.735Z
---

02.11 вікно W14z на живому стенді відхилив CPU-сторож (84.8 % від наших збірок) → відкат, +5 хв простою. Правило координатора: перед кожним важким локальним кроком (dotnet build/test, vitest, tsc) — перевірити `H:\ECR\.sync-local\WINDOW-ACTIVE`; є файл — не стартувати, робити легке, перевірити знову на наступному кроці (без циклів очікування). Файл ставить/знімає Аудит-2.

**Why:** важкі збірки ламають живі вікна замовника.
**How to apply:** вписувати цю перевірку в кожен промпт агента, що збирає локально. Пов'язане: [[machine-load-budget]], [[classifier-denies-build-under-load]].

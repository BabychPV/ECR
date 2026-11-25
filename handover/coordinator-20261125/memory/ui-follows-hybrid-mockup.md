---
name: ui-follows-hybrid-mockup
description: Людина повторно (06.10 19:07Z) — UI-зміни лише від гібридного макета docs/design/hybrid/; UI-lane без рядка «Макет» у листі готовності не зводити
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-10-06T19:08:23.305Z
---

Людина повторно: «я вже просив робити зміни відштовхуючись від гібридного макета» (D15-01: вигляд — за макетом `docs/design/hybrid/`, KIT.md, screens-*.js; піднімається `node docs/design/hybrid/serve.js`, координатор тримає http://127.0.0.1:5197/#/flows).

**Why:** сьогодні влито PeriodPicker, згортання меню, A3-01, сітка F2/стрілки, Hint без звірки з макетом — людина це помітила; аналітик координатора веде звірку (reports\UI-ADOPTION-TASKS-2026-10-06.md).

**How to apply:** (1) UI-lane без рядка в листі готовності «Макет: <файл/розділ KIT.md> + знімок до/після (Light/Dark)» НЕ зводити, особливо shared/ui (TIER2); (2) у реліз-нотатках RC окремий рядок «UI відповідає макету: так/частково (перелік)» за звіркою; (3) у промптах UI-агентам вимагати посилання на макет. Див. [[human-no-permission-questions-0110]] для іншої сталої поведінки.

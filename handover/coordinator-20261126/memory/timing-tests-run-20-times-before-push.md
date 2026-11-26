---
name: timing-tests-run-20-times-before-push
description: "З 30.09 (координатор): новий/змінений тест на таймінг, конкурентність, локи, черги — локально 20 разів поспіль до пушу (N/20 в описі коміту); не 20/20 — не пушити. Свою влиту lane видаляти"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-30T06:25:41.793Z
---

2026-09-30 координатор (з людиною): за 24 год на dev/integration CI success 38 / failure 10 / cancelled 6; майже всі червоні — нові тести на таймінг і конкурентність (Quartz-стеля, Job Object), що пройшли локально й упали в CI.

**Why:** одноразовий локальний зелений прогін не ловить гонки; «22/22 зелених» і «0/30 червоних локально» уже траплялись і не врятували CI.

**How to apply:** у промпті агентові, що пише/змінює тест на таймінг/конкурентність/локи/черги: «прожени 20 разів поспіль (`for` у PowerShell, послідовно), N/20 — у повідомленні коміту; не 20/20 — не пушити; детермінуй (бар'єр/TaskCompletionSource/TestClock), а не Task.Delay». Після ff-пушу своєї lane: `git cherry origin/dev/integration <lane>` без «+» → `git push origin --delete lane/<...>`. WIP-гілки hse301/a4-wip, a5a-wip, f9-wip-no-migration не видаляти. Див. [[flaky-tests-lazy-import-warmup]], [[lane-branches-must-be-linear]].

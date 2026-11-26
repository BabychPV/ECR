---
name: human-sg-flaring-decisions-1125
description: "Людина 25.11 — склад газу в методології факелів: усі три (Int/Cont SG, TOx) разом, події з PI AF, EOR виправити, решта дефолти"
metadata:
  node_type: memory
  type: project
  originSessionId: d15bf7c2-34d1-400b-ad82-057f935d757c
  modified: 2026-11-25T13:10:40.244Z
---

Людина 25.11 (квіз до .sync-local\DESIGN-sg-flaring.md): прив'язка складу газу ECR до методологій — Int SG + Cont SG + Thermal oxidizer одним випуском; події факелів з PI AF event frames; TOx EOR рахувати зі складом EOR (виправити старий дефект); подія на межі версій → версія на початок; TOx-стовпці → ID потоку (417→575); сезон Apr–Sep=Summer; розподіл 19/21/146 — адмінський довідник; старі формули через міст GCV. Бракує складу → missing input, не 0.

**Why:** «привязку … до методологій те що ти почав вчора» = компонентний склад; стара система брала його з FLERT.

✎ 25.11 ~15:20: людина оновила БД ECR даними 2025 + довідник `.sync-local\design\FLARES_TOX_HSE400_DATAFLOW.md`. Паритет 2025: SG — усі 12 міс; FG — лише лип–гру (січ–черв пораховано на складі HSE400 2026); TOx 2025 немає (0 EF у PI) → TOx звіряти на 2026.

**How to apply:** картка integ-sg-flaring-composition (R3), PRIORITY P-1125-SG; паритет зі старими CalcResult_* з відомою різницею EOR. Пов'язано: [[human-gas-composition-in-ecr-1124]].

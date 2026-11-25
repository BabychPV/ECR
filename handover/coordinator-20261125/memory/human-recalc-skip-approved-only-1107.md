---
name: human-recalc-skip-approved-only-1107
description: "Human 07.11 UAT — recalculation skips only APPROVED sheets; submitted ones are recalculated, logged and marked for the approver; documents page defaults to a year view"
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-07T12:57:39.815Z
---

Human 2026-11-07 (UAT, via coordinator), verbatim: «тільки погоджені не мають перераховуватися»; earlier «… інши таблиці мають перерахуватися. це також має логуватися»; «Подумай та виправь щоб система була інтуїтивно зрозумілою».
- Project/document recalc never dead-ends: skips only Approved sheets (listed: doc + sheet + period + link, reason); Draft/Submitted/Returned are recalculated; per-sheet granularity inside one document.
- Recalculated Submitted sheets are logged (job log + audit) and the approver sees «recalculated after submission».
- Documents page: default YEAR view with a 12-month status strip per row; click a month opens it; approval-state filter per year or month.

**Why:** the old «Period has submitted sheets» refusal hid which sheets and blocked the operator.
**How to apply:** don't reintroduce period-level refusals for recalculation; lane lane/integ-uat-recalc-ux.

✎ 16.11 ~11:00Z людина підтвердила загальність правила (питання B79 про ФВ-9.17): «якщо документ поданий або інші статуси окрім погоджений ми маємо перераховувати його якщо вхідні дані для нього змінилися, це можуть бути дані з 400 тощо». Тобто: будь-який стан, крім Approved, перераховується при зміні вхідних даних — і від звичайної правки, і від каскаду між місяцями, і від документа-джерела (HSE400, B80). Подані — з позначкою «перераховано після подання»; зріз подання каскад не оновлює (рішення координатора, B79 D-NEW-14, зміна ФВ-9.17).

✎ 16.11 ~10:55Z людина: зріз подання лишається як подано («перший варіант підходе»); «додай позначку у в'юхи» — колонка «перераховано після подання» у в'юхах SSRS (PLAN B146, у lane B138 rpt). SSRS читає ЖИВІ дані зі статусом; зріз подання в SSRS не видно.

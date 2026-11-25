---
name: human-nae-node-types-1103
description: "Людина 03.11 (квіз через координатора): типи вузлів NAE = рівні AF, атрибути за типом, номер NAE унікальний у регіоні/On-Offshore"
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-03T06:21:52.184Z
---

Людина 03.11 відхилила загальний діалог «New node» NAE і підтвердила модель:
- тип вузла = рівень (шаблони AF Air_Region / Air_OnShoreOffShore / Air_Area / Air_Unit / Equipment → REGION/AREA/SITE/EQUIP/SRC); діалог показує лише поля свого типу; атрибути додаються за типом (поле → рівень, міграція `RegistryFieldDef.LevelId`);
- Q1: назва En обов'язкова, Ru/Kz — ні;
- Q2: Air_Area може вкладатися в Air_Area (SITE під SITE);
- Q3: номер NAE обов'язковий і унікальний у (REGION, On/Offshore); без поділу On/Offshore — у межах REGION; перевірка на сервері з кодом помилки;
- Region/On_Offshore джерела — похідні від предків, не вводяться.

**Why:** реальна модель PI AF ECR_01_Air типізована; загальна форма дозволяла дублікати й ручні регіони.
**How to apply:** lane/integ-nae-node-types (контракт першим → UI у Permites); рішення записати в доках lane. Див. [[human-nae-permit-import-1029]].

✎ 04.11 людина: «навіщо показувати Path AF відображати і тип site не маю бути обовязковим після area» → рядок AF path з картки вузла прибрати повністю; рівень Site необов'язковий — під Area можна одразу Equipment group (і далі), успадкування й унікальність NAE мають працювати без Site. Передано Аналізу в lane/integ-nae-area-code.

✎ 04.11 людина: «так, додай кнопку повернути і повне видалення якщо ніде ні вокористан» → для вузлів NAE: Restore (зняти Retire) і повне видалення лише якщо вузол ніде не використаний (409 з «де використано»). Аналіз — сервер+контракт, Permites — UI. Пріоритет над вимірами Land.

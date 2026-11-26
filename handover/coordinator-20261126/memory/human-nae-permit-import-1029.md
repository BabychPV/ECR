---
name: human-nae-permit-import-1029
description: "Людина 29.10: дерево NAE для пермітів + імпорт параметрів викидів з Excel з налаштовуваними правилами; квіз Q1–Q4; план .sync-local/plans/NAE-PERMIT-IMPORT-PLAN.md"
metadata:
  node_type: memory
  type: project
  originSessionId: 7435a874-e4e3-49fa-9b54-7bfe3eb298ad
  modified: 2026-11-03T06:21:47.043Z
---

29.10 людина: структура NAE (Region→Area→Site→Equipment→NAE, як у PI AF ECR_01_Air) потрібна для пермітів; до перміту додається перелік викидів по джерелах NAE; в UI — завантаження з Excel (зразок «7) Параметры_2024_final.xlsx», таблиця 3.3 ЭРА), перегляд/додавання в дерево, налаштування правил зіставлення колонок. Легасі — VBA HSE_401_Air_Import (Data_01…14).

Квіз: Q1 дерево — ECR після разового завантаження з AF; Q2 повторний імпорт — як у старій (замінює й рядки ІНШИХ пермітів для тих самих NAE) з явним підтвердженням; Q3 перевірки «NAE і в Land, і в 401» НЕ треба — перміт = ліміти, Land = факт, різні речі; Q4 зберігати тип Т/П1, точку викиду, швидкість/об'єм/температуру, координати; газоочистку — ні.

Назва типу дозволу (людина 29.10): **«Air Limits Import»** (код AIR_LIMITS), не «Повітря (HSE 401)».

Макет https://claude.ai/artifact/GsW4cHABVDJVDLcoh7gpdd (дерево NAE, картка дозволу, майстер імпорту) — людина 29.10: «макети не погані плануй коли робити» → погоджено.

✎ 02.11 людина: «таблиця параметрів відноситься до пермиту локації та регіону … ця кнопка має бути тут» + «а не як окремий тип» → БЕЗ окремого типу AIR_LIMITS: таблиці джерел/речовин — частина типу AIR (Air emission permit), кнопка «Import parameters…» на картці AIR-дозволу, крок 1 майстра = Region + Area (як легасі-діалог Permit/Region/Area), регіон/зона — область імпорту на рядках. Назва «Air Limits Import» більше не тип. Людина: «Region і Area береться з Registrates» — значення з наявних реєстрів (довідників), вибір зі списку. На живому: LAND_REGION (Atyrau, Mangistau) і LAND_AREA (25 записів, плоский, серед них Offshore complex / Onshore complex) — створені бутстрапом Land, не в сіді; рішення координатора 02.11 — брати їх, зіставлення з деревом NAE за нормалізованою назвою. Людина 02.11: «тільки Offshore/Onshore створи новий дикшионари … має бути налаштованим … тест чи дикшиорнари чи через крапку з комою» → Area = новий довідник (Offshore/Onshore); поля області імпорту налаштовуються наявними видами полів Text | Registry | List(«;»).

✎ 03.11 людина (скрин діалогу «New node» + 10 скринів PI AF ECR_01_Air): «Створення нодів для NAE не таке!!» — дерево типізоване за шаблонами AF: Air_Region → Air_OnShoreOffShore → Air_Area → Air_Unit → Equipment (код = номер NAE; атрибути Name_En/Name_Ru, On_Offshore, Region). Вимоги: у всіх нод 3 мови; номер NAE обов'язковий і унікальний у розрізі Air_Region + Air_OnShoreOffShore; атрибути налаштовуються для кожного типу ноди. Region/On_Offshore успадковуються від предків. Передано Аналізу (дизайн, lane/integ-nae-node-types) → Permites UI. Квіз 03.11: En обов'язкова, Ru/Kz ні; Air_Area може вкладатися в Air_Area; без поділу On/Offshore (Mangistau) — унікальність NAE у межах регіону. Дизайн: тип ноди = RegistryLevelDef (REGION/AREA/SITE/EQUIP/SRC ← шаблони AF), RegistryFieldDef.LevelId для атрибутів за типом.

**How to apply:** фази N1 hierarchical registry → N2 NAE tree → N3 AIR permit type + мін. UI перміту → N4 Excel import engine (RegistryImportSpec) → N5 wizard UI; макети перед UI. Пов'язано: [[human-table-chat-1029]].

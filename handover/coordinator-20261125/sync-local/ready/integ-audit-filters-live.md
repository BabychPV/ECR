# Картка: audit-filters-live Аудит на live: відбір за документом падає, поля Document/Column приймають лише внутрішні ID
Статус: briefed
Гілка: lane/integ-audit-filters-live
База: 7afb19c97 (live b14)
Пачка: наступна
Ризик: R2 (SQL-запит аудиту, продуктивність на live; UI фільтрів)
Рішення: людина 25.11 23:3x «Для аудиту перевірте всі вкладки та поля фільтрації» — повний прохід координатора на live b14 (акаунт людини land-owner)
Дизайн: не потрібен (запит + поля вибору); AC-1 — спершу план запиту на копії live

## 1. Що і навіщо
Повний прохід 25.11 23:2x–23:5x, вікно From 2026-11-18 / To 2026-11-25, кожне значення звірено з aud.*/wf.* на live (лише читання):

| Вкладка | Поле | Результат |
|---|---|---|
| Cell changes | From/To, Origin ×10 (103 UserEdit, 7294 Recalculation, решта 0), Late edits only (195), By user land-owner (87), Document 98 (16), Row key Methane (16), Column 73809 (12), Archive (2024-08-03…2024-10-31, 0), сортування By user ↑/↓, Load more | ✓ = БД |
| Structure changes | Entity type ×13 (CalculationBinding 9312, ColumnDef 8916, DataSource 1, Reopen 18, Methodology 6, MonthInputAlias 3, Project 2, ProjectTemplate 7, RegistryDef 9, RegistryEntry 10, TableDef 54, TableRow 3, TemplateVersion 1) | ✓ = БД |
| Security events | Event type ×16 (усі = БД, напр. LoginSucceeded 91, SignedOut 23, AccessDenied 11, RegistryValueChanged 24240 «there are more»), By user land-owner (56) | ✓ = БД |
| Workflow events | Action ×6 (Submit 66, Approve 63, ApproveStep 0, Reject 1, Reopen «Returned for rework» 62, Recall 1), By user land-owner (11), From 25.11 (11), 24.11–24.11 (182 через Load more) | ✓ = БД |
| Час | усі вкладки — місцевий час (16:56 = 14:56Z) | ✓ |

**Дефекти:**
- **D1 (головне).** Cell changes → Document **96** (415 змін за вікно): список не з'являється (>80 с скелетон); сервер — `Execution Timeout Expired` → 503 ECR-SYS-0503 (журнал ecr-20261125_016.log 23:25:02, CorrelationId 6c873aac-…); клієнт повторює запит — на live висить сесія SQL ~30 с щоразу. Запит — зведення за колонками з `ColumnDefId IN (SELECT … FROM OPENJSON(@visibleColumns))` + `IF EXISTS … arc.AuditCellChange` (текст: dm_exec_query_stats; doc 96 має тисячі колонок → великий @visibleColumns). Doc 98 (16 змін) — відповідає миттєво.
- **D2.** Поле **Document** — лише числовий DocumentId: введення «P1-V9-0001» (так документ видно скрізь в UI) мовчки стає «190001» → червоне «Document not found ECR-DOC-0404». Користувач не знає внутрішнього Id.
- **D3.** Поле **Column** — лише ColumnDefId: «M10» мовчки стає «10» → «No changes in this window» (хибна відповідь «змін не було»).
- **D4.** **By user**: чотири різні акаунти з однаковою назвою «b12 window temporary (disable after)» — не розрізнити (тимчасові акаунти вікон; набір вікна b14 уже виправлено на «live window temporary», старі назви лишились у БД).
- Питання (не дефект): «Clear filters» знімає Row key/Column/Origin/By user/Late, але лишає Document і дати — свідомо?

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live: Audit trail → Cell changes → документ LAND-TEST-ALL-METHODOLOGIES (P1-V9-0001) за 18–25.11 → список 415 змін ≤ 3 с; без 503 у журналі | live скрин + журнал | — |
| AC-1 | причина тайм-ауту встановлена на копії live (план запиту, час, розмір @visibleColumns) і записана в картку | план/вимір на копії | — |
| AC-2 | запит(и) Cell changes з documentId на копії live (doc 96, doc 100 6950 змін) ≤ 2 с; тест, що червоний на 7afb19c97 (поріг або кількість читань) | тест + вимір | — |
| AC-3 | Document — вибір документа за назвою/кодом (як у Documents), у URL — id; довільний текст не перетворюється в інший документ | vitest + e2e | — |
| AC-4 | Column — вибір колонки документа за підписом (місяць/назва), не числом; некоректний ввід не дає «No changes» | vitest + e2e | — |
| AC-5 | By user — однакові назви розрізнені (логін у дужках) | vitest | — |
| AC-6 | клієнт не повторює запит, що впав з 503/тайм-аутом, безкінечно (повідомлення з Retry) | vitest | — |

## 3. Межі
- Можна змінювати: серверний запит/обробник аудиту клітинок (Ecr.Application/Ecr.Infrastructure — Audit*), індекс лише якщо доведено планом (тоді міграція EF — зупинись і напиши координатору до генерації), клієнт features/audit/** (або де живе екран Audit trail), тести до них, сід лише для нових ключів UI (секція свого імені, #UiChanged за правилом SeedTextUpdateTests).
- Заборонено: live (EcrLandUi, :5094/:6094) — ані читання, ані запису; бази ECR/FLERT/EcrLegacyProd; H:\ECR як робоча папка.
- Копія live для виміру: EcrLandB14Reh (G:\EcrData\b14reh, копія live 25.11 ~21:00) — лише читання й власний Api на вільному порту ≥ 5200.
- Умова зупинки: потрібна міграція або зміна контракту, яку не можна зробити сумісно; тижневий ліміт: комітити й пушити WIP щонайменше кожні 30 хв.

## 4. Звіт
(порожньо)

Координатор 25.11: агента зупинено на 99 % тижневого ліміту до першого коміту (робоча копія C:\ecr-wt\audit-filters чиста, гілка не запушена). Почати заново тим самим брифом після 29.11 17:00.

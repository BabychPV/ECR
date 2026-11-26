# Лист готовності: lane/integ-audit-rows-polish (columnScale у журналах + Document.Delete e2e-адміну)

- Гілка: `lane/integ-audit-rows-polish`, над `lane/integ-audit-rows2` (PR #675) — зводити ПІСЛЯ #675
- База: `3fa3781a8`; head `69a44e1b5`; коміти: cc9d0b4f7 (columnScale + контракт), 69a44e1b5 (e2e-стенд)
- Контрактний коміт: `cc9d0b4f7` — нове необов'язкове поле `columnScale` у CellChangeView і SheetActivityItem; openapi.snapshot.json + schema.d.ts перегенеровано останнім кроком, повтор байт-в-байт
- Лінійна, мутацій у кінчику немає; Sql/EF-міграція/shared-ui/lock/CLAUDE.md/.github/UiString/коди помилок: ні
- columnScale: AuditReader бере `c.Scale` у тому самому запиті з іменами; клієнт `auditValueText(value, type, scale)` → `formatDecimal(…, minFractionDigits = scale)` — той самий виклик, що `cellDisplay` сітки (U-05: не менше Scale знаків, довший дріб не обрізається). Передають SheetLog і CellChangesTable (вкладка Audit). RowChange значень не має.
- e2e-адмін: `Document.Delete` лише в тестовій ролі E2EAdmin (`tools/e2e-stand.ps1`); виняток у `E2eStandPermissionsCoverageTests` прибрано. Сід і ролі продукту не змінено.
- Відновлення рядка: пропущено (за вказівкою).
- Тести: vitest SheetLog.values +1 (columnScale 2 → «12.50»/«3.00»), auditValue +3; SQL SheetActivityReaderTests +1 (ColumnScale 3 / null з бази)
- Мутація (vitest): SheetLog без `item.columnScale` → «Unable to find … 3.00», червоний; повернено
- CI: CI 41417808583 ✓, Evidence 41417808726 ✓ (head 69a44e1b5)
- e2e: workflow_dispatch e2e-stand 41423644751 ✓ (ref lane/integ-audit-rows-polish, grep sheetLogLive / auditTrail / auditSortLive / documentInspector — 9 passed). Перша спроба 41421278398 впала на checkout: я передав короткий SHA як ref; це моя помилка у вхідних даних, не дефект коду.
- Локально: build ☑, Architecture 1157/1157 (без StatusFromGit — pwsh), Application SheetActivity/CellChange/Audit 91, Infrastructure SheetActivity/AuditReader/AuditTests 51, vitest audit+inspector+AuditPage 133, tsc ☑, eslint ☑, OpenApiSnapshot ☑
- Хто зводить: координатор

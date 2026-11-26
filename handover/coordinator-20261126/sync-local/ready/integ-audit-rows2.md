# Лист готовності: lane/integ-audit-rows2 (журнал рядків aud.RowChange) — замінює integ-audit-rows

- Гілка: `lane/integ-audit-rows2`; черга координатора #5
- База: `554b4d60f` (origin/lane/integ-seed-split3, над PR #674) — зводити ПІСЛЯ #674
- Tip: `3fa3781a8`; коміти: 6c580be5f (таблиця/порт/подвійний запис), 3aa6d9f09 (журнал аркуша + контракт), 77c6a486a (інспектор), 3fa3781a8 (тест: межа за Id журналу)
- Лінійна: merge-комітів 0; мутацій у кінчику немає
- Конфлікт при перенесенні: `DocumentReadScope.cs` — збережено обидва методи (`VisibleTablesOfSheet` з бази й `VisibleTableIdsOfSheet` цього lane)
- Sql: так — `Sql/11-audit-tables.sql` (aud.RowChange, CK, IX_RowChange_Document, TR_RowChange_Immutable), `Sql/07-partition-tables.sql`; ідемпотентно; нових грантів не треба
- EF-міграція: ні
- Контракт: так, лише описи полів SheetActivityItem; контрактний коміт `3aa6d9f09`; openapi.snapshot.json + schema.d.ts на новій базі перегенеровано — збіг байт-в-байт з автоматичним злиттям (повтор ідентичний)
- shared/ui / lock / CLAUDE.md / .github: ні
- Нові ключі UiString: ні (наявні `permits.page.row.added/deleted`, вони є в seed/00-prelude.sql і seed/features/PERMITS-ui-page.sql); файлу seed/features/AUDROWS-* не потрібно
- Нові коди помилок: ні
- Тести: див. integ-audit-rows.md; на новій базі додатково виправлено `DeleteRowTests` (Id документів повторюються в спільній тестовій базі → межа за MAX(Id) журналу)
- Мутація: вимкнути `rowAudit.WriteRowChangesAsync` у DeleteRowHandler → `Додавання_і_видалення_рядка…` червоний; повернено
- Таймінгові тести: нових немає
- CI: CI 41409271913 ✓ success, Evidence 41409271888 ✓ success (head 3fa3781a8)
- Локально: build ☑, Architecture 1157/1157 (без StatusFromGitScriptTests — потрібен pwsh 7), Application SheetActivity*/PatchCellsRowAudit* 23/23, Infrastructure DeleteRow/SheetActivity*/AuditImmutability/PatchCellsWorkbook 39/39, OpenApiSnapshot ☑, vitest inspector 34/34
- Ризики: Action 3 (Restored) лише резерв; тіньові рядки Q-17 не журналюються; columnScale не зроблено (окрема задача); історія — вперед від розгортання
- Хто зводить: координатор

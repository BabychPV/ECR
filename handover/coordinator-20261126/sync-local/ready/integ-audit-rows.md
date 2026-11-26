# ⛔ SUPERSEDED → `integ-audit-rows2.md` (lane/integ-audit-rows2 на 554b4d60f). Цей lane не зводити.
# CI цього lane був зелений: CI 41403806252 ✓, Evidence 41403806280 ✓ (head 068b1043c, база main 4cfeff59d).

# Лист готовності: lane/integ-audit-rows (журнал рядків aud.RowChange)

- [x] Гілка / lane: `lane/integ-audit-rows` (повний CI — ім'я `lane/integ-*`)
- [x] Пункт: черга координатора #5 (аудит додавання/видалення рядків)
- [x] Хеш tip: `068b1043c` (068b1043c…; 3 коміти: 121c1061a, 3958a647c, 068b1043c)
- [x] База: `origin/main` = `4cfeff59d4ef7aed007f05d7a7edf6effc262b1d` (свіжа на момент пушу; WIP a1669b47e з бандла відновлено cherry-pick'ом, конфлікт у 07 розв'язано — додано RowChange поруч із CellFormulaOverride)
- [x] Лінійна: merge-комітів 0
- [x] Мутацій у кінчику немає (мутація лише локально, повернено)
- [x] Sql: так; `Sql/11-audit-tables.sql` (aud.RowChange, CK_RowChange_Action, IX_RowChange_Document, TR_RowChange_Immutable), `Sql/07-partition-tables.sql` (карта + перевірка 50031). Ідемпотентно (IF OBJECT_ID / IF NOT EXISTS / CREATE OR ALTER). Нових грантів не треба: застосунок пише в aud.* як у CellChange, TVP-типу немає. arc-дзеркала немає (вікно 396 діб, архівація відкладена — коментар у 11).
- [x] EF-міграція: ні
- [x] Контракт: так, лише описи полів SheetActivityItem (kind/action/origin/rowKey/tableCode/tableNameL10n); форма не змінилась. openapi.snapshot.json + schema.d.ts перегенеровано останнім кроком, повторний прогін байт-в-байт. Коміт контракту: 3958a647c.
- [x] shared/ui: ні
- [x] package*.json / lock / Directory.Packages.props: ні
- [x] CLAUDE.md / .github: ні
- [x] Нові ключі UiString: ні — UI бере наявні `permits.page.row.added` / `permits.page.row.deleted` («Row added»/«Row deleted», ru/kz є). Чернетки сіду `.seed-draft-AUDROWS.sql` немає.
- [x] Нові коди помилок: ні
- [ ] Бюджет клієнта: не міряно локально (зміна — один компонент SheetLog, +~50 рядків) — дивитись CI client
- [x] Тести: DeleteRowTests +2 (журнал додавання/видалення; відкат вставки забирає запис) + перевірка «поданий аркуш — 0 записів»; PatchCellsWorkbookTests +2 (книга й поштучний імпорт → Added/Import; відхилена книга — порожньо; храповик звернень книги 29→30); AuditImmutabilityTests (+RowChange, 6 тригерів); SheetActivityRowChangeTests (SQL, limit 1/2/5: порядок, прихована таблиця, інший період/автор, system); SheetActivityTests +4 (склейка трьох джерел 150 сідів, межі таблиць/автор у фільтрі, закритий аркуш, курсор тегу 2); PatchCellsRowAuditRecordsTests (походження Import/ApproverEdit); vitest SheetLog.rows.test.tsx; Architecture: реєстр з'єднань (RowChangeAudit OwnDb), контракт портів (IRowChangeAudit у 02-contracts.md).
- [x] Мутація-доказ: вимкнено `rowAudit.WriteRowChangesAsync` у DeleteRowHandler → `DeleteRowTests.Додавання_і_видалення_рядка_лишають_запис_у_журналі_рядків` червоний (Actual без «2|R2_1|2|UserEdit»); повернено.
- [x] Таймінгові тести: нових немає (n/a)
- [ ] CI на lane: CI run 41403806252, Evidence run 41403806280 — вердикт: ________
- [x] Перевірено локально: build ☑ (Ecr.sln) Architecture ☑ (1149/1170 до фіксу двох сторожів; 19 червоних StatusFromGitScriptTests — середовище, потрібен pwsh 7, не зачеплено змінами; сторожі, що зачепила зміна, полагоджені) vitest зачепленого ☑ (18/18), tsc ☑, eslint ☑; SQL вузько: DeleteRow/CreateRowRace/DeleteRowGuards/EditAssignmentGateRace/SheetActivity*/AuditImmutability 74/74, PatchCellsWorkbookTests 16/16, Audit*/Partition* 152/152, OpenApiSnapshotTests ☑
- [x] Ризики / відомі обмеження: відновлення рядка (Action 3) — шляху в продукті немає, лише резерв; тіньові рядки Q-17 (MonthRouting) не журналюються; columnScale у SheetActivityItem/CellChangeView НЕ зроблено (поза журналом рядків — окрема задача); історія — вперед від розгортання.
- [ ] Хто зводить: координатор

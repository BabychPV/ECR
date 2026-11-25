---
name: covering-an-exempt-requirement
description: "Покрити вимогу, що була в trace-exempt.md, — це правка ТРЬОХ файлів; два сторожі падають окремо"
metadata: 
  node_type: memory
  type: project
  originSessionId: 535a4194-e29f-466d-8442-18ac071d6aea
  modified: 2026-09-18T12:03:20.864Z
---

Коли вимога з `contracts/trace-exempt.md` стає покритою тестом, самого
`[Trait("Requirement", "ФВ-x.y")]` НЕ досить — падають два різні сторожі,
кожен на своєму прогоні:

1. `RequirementCensusTests.Жодна_вимога_не_звільнена_і_покрита_водночас` —
   поки рядок лишається в `contracts/trace-exempt.md`. Кошики взаємно
   виключні: або звільнено, або покрито.
2. `JournalIntegrityTests.Числа_вимог_у_плані_збігаються_із_заміром` — рядок
   «Вимог ТЗ (листових)» у `docs/build/roadmap.md` (`253 · покрито N ·
   звільнено M · непокрито 0`). Числа треба посунути руками: покрито +1,
   звільнено −1.

Тобто **три файли за одну зміну**: тест із трейтом, `trace-exempt.md`,
`roadmap.md`. Знайти це прогоном CI коштує двох циклів по 10–12 хв — швидше
одразу прогнати `dotnet test tests\Ecr.Architecture.Tests` локально (7 с).

⚠ Суміжне: новий `throw new BusinessRuleException/…` у `src/` без
`Details["messageKey"]` валить `MessageKeyRatchetTests`, а кожен названий ключ
`err.*` мусить мати рядок у `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql`
(`ErrorTitleCatalogTests`). Правильна реакція — додати ключ і рядок сіду, а не
збільшити число в `contracts/localization-debt.md`.

Пов'язане: [[claude-md-entries-go-stale]], [[worktree-branch-switches]].

---
name: registering-an-error-code
description: "Завести один код помилки ECR-xxx-nnnn — це 4 файли й 7 сторожів; арм у ExceptionHandlingMiddleware не з'являється сам"
metadata: 
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-19T08:50:52.190Z
---

Заведення ОДНОГО нового коду помилки (виміряно на `ECR-AUTH-0429`,
2026-09-19) — **чотири обов'язкові файли й сім сторожів**, а не «три файли й
два сторожі», як передбачає [[covering-an-exempt-requirement]].

Файли: `src/Ecr.Domain/Errors/ErrorCodes.cs`, `docs/build/02-contracts.md` §7,
`src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` (заголовок `err.<код>`
**і** подробиця `err.<код>.<ключ>`), місце кидка.

Сторожі: `ContractIntegrityTests` × 2 (звірка каталогу з контрактом у ТРИ
напрями), `ErrorTitleCatalogTests` × 3, `SeedCatalogTextTests` × 2.

⛔ **П'ятий файл не примушує жоден сторож, і саме тому його забувають:**
`src/Ecr.Api/Errors/ExceptionHandlingMiddleware.cs`. Без арма новий код їде з
**422**, хоч би які цифри в ньому стояли. Перевірено двічі за один день:
`ECR-REG-0409` доїхав би як 422, `ECR-AUTH-0429` — теж. Правило «цифри коду =
HTTP-статус» у цьому проєкті виконується руками.

⚠ `contracts/localization-debt.md` рахує рядки `throw new …Exception(`. Код,
що пише `problem+json` напряму (rate limiter), у храповик не потрапляє взагалі.

⚠ Сторож `ErrorTitleCatalogTests.Кожен_messageKey_із_коду_заведено_в_сіді`
читає ТЕКСТ файлу: взятий у лапки префікс `"err.…"` всередині XML-коментаря
прочитався як кинутий ключ і завалив прогін. Див. [[source-text-guards-are-brittle]].

**Why:** ціна помилки — не зайві хвилини, а мовчазна неправда: код без арма
віддає чужий статус, а код без рядка сіду показує користувачеві `⟦ключ⟧`.

**How to apply:** заводячи код, одразу пройти всі п'ять місць і прогнати
`dotnet test tests\Ecr.Architecture.Tests` локально (8 с) — цикл CI коштує
10–12 хв.

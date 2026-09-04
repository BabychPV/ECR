# 09 — Команди збірки і тестів

> **Статус: перевірено частково (2026-09-04, ПК-2).** Команди, які вдалося
> виконати, звірені й позначені в журналі §8. Решту перевірити неможливо, доки
> не зняті блокери `Q-013` і `Q-014` — бекенд не збирається. Попередження
> лишається до повної перевірки.
>
> Шляхи в цьому файлі — **від кореня репозиторію** (`D:/repos/ECR Web`),
> сам пакет документації живе в `docs/` (`Q-001`).

---

## 1. Перевірка середовища (виконати першим)

```bash
dotnet --list-sdks
node -v
npm -v
docker ps
git --version
```

Очікується: .NET SDK 10.0.x, Node 22.x, npm 10.x, Docker відповідає, git є.
Розбіжність → `questions.md`, далі за `04-environment.md` §5.

Перевірка SQL Server:

```bash
sqlcmd -S localhost -E -Q "SELECT SERVERPROPERTY('Edition'), SERVERPROPERTY('EngineEdition'), SERVERPROPERTY('ProductMajorVersion'), DATABASEPROPERTYEX(DB_NAME(),'IsReadCommittedSnapshotOn')"
```

## 2. Backend

```bash
# з кореня репозиторію
dotnet restore Ecr.sln
dotnet build Ecr.sln -c Debug
```

Тести за замовчуванням — **без інтеграційних** (не потребують Docker і SQL Server):

```bash
dotnet test Ecr.sln --filter "Category!=Integration"
```

Інтеграційні тести окремо (потрібен Docker або локальний SQL Server):

```bash
dotnet test Ecr.sln --filter "Category=Integration"
```

Тести одного етапу:

```bash
dotnet test Ecr.sln --filter "Stage=Stage1&Category!=Integration"
dotnet test Ecr.sln --filter "Stage=Stage2&Category!=Integration"
```

Тести одного проєкту:

```bash
dotnet test tests/Ecr.Expressions.Tests/Ecr.Expressions.Tests.csproj
```

Архітектурні тести (правила залежностей — блокуючі, `D-82`):

```bash
dotnet test tests/Ecr.Architecture.Tests/Ecr.Architecture.Tests.csproj
```

## 3. База даних

```bash
# створити міграцію (виконується ОДИН раз на Етапі 1, далі — за потреби)
dotnet ef migrations add InitialCreate \
  --project src/Ecr.Infrastructure \
  --startup-project src/Ecr.Api \
  --output-dir Persistence/Migrations

# застосувати до локальної БД
dotnet ef database update \
  --project src/Ecr.Infrastructure \
  --startup-project src/Ecr.Api

# згенерувати SQL-скрипт (для середовищ, де застосунок не має DDL-прав — D-66)
dotnet ef migrations script \
  --project src/Ecr.Infrastructure \
  --startup-project src/Ecr.Api \
  --idempotent --output artifacts/migration.sql
```

Об'єкти, які **не створюються міграціями EF** і живуть окремими SQL-скриптами
(`src/Ecr.Infrastructure/Persistence/Sql/`), бо їх виконує SQL Agent під окремим
principal (`D-66`): партиційні функції і схеми, файлові групи, процедура
архівації, вʼюхи `rpt.v_*`.

```bash
sqlcmd -S localhost -d Ecr -E -i src/Ecr.Infrastructure/Persistence/Sql/01-filegroups.sql
sqlcmd -S localhost -d Ecr -E -i src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql
sqlcmd -S localhost -d Ecr -E -i src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql
```

## 4. Запуск

```bash
dotnet run --project src/Ecr.Api
# OpenAPI:  http://localhost:5080/openapi/v1.json
# Scalar:   http://localhost:5080/scalar/v1
# Health:   http://localhost:5080/health
```

## 5. Frontend

```bash
cd src/Ecr.Web
npm install
npm run dev          # http://localhost:5173
npm run build
npm run test         # vitest
npm run typecheck    # tsc --noEmit
npm run lint
```

Генерація типів клієнта з нашого OpenAPI (backend має бути запущений):

```bash
cd src/Ecr.Web
npm run api:types    # openapi-typescript http://localhost:5080/openapi/v1.json -o src/api/schema.d.ts
```

## 6. Повний прогін перед завершенням етапу

```bash
dotnet build Ecr.sln -c Release
dotnet test Ecr.sln --filter "Category!=Integration"
dotnet test Ecr.sln --filter "Category=Integration"     # якщо є Docker
cd src/Ecr.Web && npm run typecheck && npm run test && npm run build
```

## 7. Git

```bash
git add -A
git commit -m "stage-N: <що зроблено>"
git tag stage-N
git diff stage-1..HEAD --stat        # для рев'ю
```

---

## 8. Журнал перевірки команд (заповнюється на ПК-2)

| # | Команда | Статус | Факт / примітка |
|---|---|---|---|
| 1 | `dotnet --list-sdks` | ✅ | `10.0.301` (і `8.0.412`) — відповідає `04-environment.md` |
| 1a | `node -v` / `npm -v` / `git --version` | ✅ | `v22.19.0` / `10.9.3` / `2.46.2.windows.1` |
| 1b | `docker ps` | ❌ | Docker Desktop не запущений — `Q-016`, деградований режим |
| 1c | `sqlcmd -S localhost -E -Q "..."` | ⬜ | не виконувалося: Етап 0 до БД не звертається (`04-environment.md` §2.1) |
| 2 | `dotnet restore Ecr.sln` | ⚠ | працює **після** `Q-003` (4 відсутні `.csproj`) і `Q-004` (версії пакетів) |
| 3 | `dotnet build Ecr.sln -c Debug` | ❌ | падає на `src/Ecr.Expressions/FormulaEngine.cs` — `Q-013`, `Q-014`. `Ecr.Domain` окремо: **0 errors, 0 warnings** |
| 4 | `dotnet test Ecr.sln --filter "Category!=Integration"` | ⬜ | недосяжно: усі тестові проєкти залежать від `Ecr.TestKit` → `Ecr.Infrastructure` → `Ecr.Application` → `Ecr.Expressions` |
| 5 | `dotnet test Ecr.sln --filter "Category=Integration"` | ⬜ | недосяжно + немає Docker (`Q-016`) |
| 6 | `dotnet ef migrations add` | ⬜ | недосяжно: `Ecr.Infrastructure` не збирається |
| 7 | `dotnet run --project src/Ecr.Api` | ⬜ | недосяжно |
| 8 | `npm install` | ✅ | у `src/Ecr.Web`; 362 пакети, версії з `04-environment.md` §4 **без правок**; 3 хв |
| 9 | `npm run build` | ⬜ | недосяжно: немає `src/Ecr.Web/index.html` (`Q-015`) |
| 10 | `npm run test` | ✅ | `npx vitest run` — **5 файлів, 23 тести, 23 failed** (`not implemented`) — саме те, чого вимагає Етап 0 |
| 10a | `npm run typecheck` | ❌ | 7 помилок: немає згенерованого `src/api/schema.d.ts` і модуля `@/api/types`; `JSX` під React 19 — `Q-017` |

Статуси: ⬜ не перевірено · ✅ працює · ⚠ працює з правкою · ❌ не працює.

### Фактичні команди, що працюють сьогодні

```bash
# перевірка середовища
dotnet --list-sdks && node -v && npm -v && git --version

# бекенд: restore працює, build падає (Q-013, Q-014)
dotnet restore Ecr.sln
dotnet build src/Ecr.Domain/Ecr.Domain.csproj -c Debug   # єдиний, що зелений

# фронтенд: працює повністю
cd src/Ecr.Web
npm install
npx vitest run          # 23 тести, усі падають — очікувано
```

**Попередження на початку файлу знімається після зняття `Q-013` і `Q-014`
і повного проходу §6.**

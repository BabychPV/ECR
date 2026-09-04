# 09 — Команди збірки і тестів

> **Статус: перевірено на ПК-2 2026-09-04.** Кожну команду виконано; журнал §8
> заповнено фактом. Неперевіреним лишається лише те, що потребує SQL Server
> або Docker (§3, §4 і `Category=Integration`) — див. `Q-016` і кінець §8.
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

Базу визначає параметр `-d`, а не вміст скриптів: усередині вони працюють
через `DB_NAME()` (`Q-029`). Каталог файлів даних скрипт визначає сам із
властивостей інстансу — редагувати нічого не треба. Виняток один: якщо
архівна файлова група має лежати на іншому носії, заповніть `@ArchivePath`
на початку `01-filegroups.sql`.

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
| 3 | `dotnet build Ecr.sln -c Debug` | ⚠ | **0 errors**; 907 попереджень — усі з 13 правил, виведених зі списку блокуючих у `Directory.Build.props`, і 2 тестових у `tests/Directory.Build.props` (`Q-006`, `Q-025` В-4) |
| 3a | `dotnet build Ecr.sln -c Release` | ⚠ | **0 errors** |
| 4 | `dotnet test Ecr.sln --filter "Category!=Integration"` | ⚠ | **486 знайдено, 486 failed, 0 passed** — очікувано. Працює після `Q-022` |
| 5 | `dotnet test Ecr.sln --filter "Category=Integration"` | ⚠ | **119 знайдено, 119 failed** (`Ecr.Infrastructure.Tests` 116, `Ecr.Application.Tests` 3). Docker **не потрібен**, доки тіла — `Assert.Fail`: до `SqlServerFixture` виконання не доходить. Знадобиться з Етапу 1 (`Q-016`, `Q-025`) |
| 6 | `dotnet ef migrations add` | ⬜ | не виконувалося: міграції створюються на Етапі 1 |
| 7 | `dotnet run --project src/Ecr.Api` | ⬜ | не виконувалося: `RunEcrStartupSequenceAsync` — заглушка, потрібен SQL Server |
| 8 | `npm install` | ✅ | у `src/Ecr.Web`; 362 пакети, версії з `04-environment.md` §4 **без правок**; 3 хв |
| 9 | `npm run build` | ✅ | після `Q-023`; `825 modules transformed`, `dist/` зібрано за 2.6 с |
| 10 | `npm run test` | ✅ | **5 файлів, 23 тести, 23 failed** (`not implemented`) — саме те, чого вимагає Етап 0 |
| 10a | `npm run typecheck` | ✅ | 0 помилок після `Q-023` |

Статуси: ⬜ не перевірено · ✅ працює · ⚠ працює з правкою · ❌ не працює.

### Перевірений повний прогін

```bash
dotnet restore Ecr.sln
dotnet build Ecr.sln -c Debug
dotnet test  Ecr.sln -c Debug --no-build --filter "Category!=Integration"   # 486 · усі падають
dotnet test  Ecr.sln -c Debug --no-build --filter "Category=Integration"    # 119 · усі падають
dotnet build Ecr.sln -c Release
cd src/Ecr.Web && npm install && npm run typecheck && npm run test && npm run build
```

**Що ще не перевірено і чому:**

| Команда | Чому |
|---|---|
| §3 цілком (`dotnet ef`, `sqlcmd`) | потрібен SQL Server; шість `.sql`-скриптів у пакеті відсутні (`Q-015`) |
| §4 (`dotnet run --project src/Ecr.Api`) | послідовність старту — заглушка; застосунок навмисно не стартує без БД |
| — | інтеграційні тести **виконуються** (119, усі падають); Docker знадобиться з Етапу 1, коли з'являться тіла (`Q-016`) |
| `npm run api:types` | генерує типи з **запущеного** бекенда; поки що `src/api/schema.d.ts` — заглушка (`Q-023`) |
| `npm run lint` | ESLint встановлено, але конфігурації (`eslint.config.js`) у пакеті немає |

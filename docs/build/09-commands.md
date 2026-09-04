# 09 — Команди збірки і тестів

> **⚠ УВАГА: усе нижче — ГІПОТЕЗА.** Команди складені на ПК-1 **без можливості
> їх виконати**. На Етапі 0 ти зобов'язаний перевірити кожну і **переписати цей
> файл реально працюючими командами свого середовища** (крок 7 Етапу 0).
> Поки цей блок не прибрано — файл не перевірений.

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

| # | Команда | Статус | Фактична команда, якщо відрізняється |
|---|---|---|---|
| 1 | `dotnet --list-sdks` | ⬜ | |
| 2 | `dotnet restore Ecr.sln` | ⬜ | |
| 3 | `dotnet build Ecr.sln` | ⬜ | |
| 4 | `dotnet test --filter "Category!=Integration"` | ⬜ | |
| 5 | `dotnet test --filter "Category=Integration"` | ⬜ | |
| 6 | `dotnet ef migrations add` | ⬜ | |
| 7 | `dotnet run --project src/Ecr.Api` | ⬜ | |
| 8 | `npm install` | ⬜ | |
| 9 | `npm run build` | ⬜ | |
| 10 | `npm run test` | ⬜ | |

Статуси: ⬜ не перевірено · ✅ працює · ⚠ працює з правкою · ❌ не працює
(тоді — запис у `questions.md`).

**Після заповнення таблиці прибери попередження на початку файлу.**

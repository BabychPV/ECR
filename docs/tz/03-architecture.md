# 03. Архітектура

## 3.1 Принципи

| # | Принцип | Наслідок |
|---|---|---|
| **АРХ-1** | **Структура — це дані** | додати колонку = `INSERT` у `cfg.ColumnDef`; DDL у рантаймі немає (D-13) |
| **АРХ-2** | **Ідентичність явна** | `RowKey`, `Code`, `RegistryEntryId`; `Ordinal` — лише відображення (D-17) |
| **АРХ-3** | **Опубліковане незмінне структурно** | тригер БД відхиляє структурний `UPDATE`; презентаційний шар патчиться з `PresentationRevision`; ключ кешу `v{id}:r{rev}` знімає інвалідацію як клас задач (D-16) |
| **АРХ-4** | **БД не рахує** | немає CLR; `rpt.*` — зріз без логіки; агрегації в сервісі (D-15) |
| **АРХ-5** | **Ядро не знає предметної області** | `Af*`/`Legacy*` — лише в `ext`; перевіряється архітектурним тестом (D-18) |
| **АРХ-6** | **Сервер авторитетний** | клієнтський обчислювач — підказка, з тестом еквівалентності (D-20) |
| **АРХ-6a** | **Час — з `IClock`** | `DateTime.Now` заборонений; UTC у сховищі, пояс майданчика в бізнес-логіці (D-68, D-85) |
| **АРХ-7** | **Standard — базовий режим SQL** | Enterprise вмикається автоматично за `SERVERPROPERTY`; редакція впливає лише на операційні стратегії, ніколи — на модель і числа (D-28) |
| **АРХ-7a** | **Навантажувальні тести — в Standard** | Enterprise дає запас, а не умову приймання (D-64) |
| **АРХ-8** | **Одна точка рішення про доступ** | `IAccessDecisionService` повертає причину (D-36) |

### Чому АРХ-3 виглядає саме так

Класична дилема: «структуру опублікували, а підпис колонки треба виправити». Два
поганих варіанти — заборонити правку взагалі (тоді друкарська помилка живе рік)
або дозволити правити все (тоді історія розповзається).

Рішення розділяє шар: **структурні** атрибути (`Code`, `DataType`,
`Ordinal`-у-формулах, формули, правила) — незмінні; **презентаційні**
(`Label*L10n`, стилі, ширина, порядок відображення) — патчабельні з
інкрементом `PresentationRevision`. Ключ кешу містить обидва номери, тому
інвалідація кешу перестає бути задачею: інший `rev` — інший ключ.

### Чому АРХ-7 саме автоматичний

Замовник має Standard, але може мати Enterprise. Писати дві гілки коду — це
писати дві системи. Тому: **режим визначає стратегію, не модель**. Стратегії,
що залежать від редакції: онлайн-перебудова індексів, стиснення партицій,
паралелізм архівації, `RESUMABLE`. Числа, схема, API — однакові.

```csharp
// Ecr.Infrastructure — визначення редакції один раз при старті
var edition = (string)await conn.ExecuteScalarAsync(
    "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)");
// 3 = Enterprise/Developer; 2 = Standard; 5 = Azure SQL DB
```

---

## 3.2 Стек

| Шар | Технологія | Ліцензія | Рішення |
|---|---|---|---|
| Runtime | .NET 10 LTS | MIT | D-01 |
| ORM | EF Core 10 | MIT | D-02 |
| БД | SQL Server 2016 SP1+ (Standard база) | — | D-28 |
| Обчислювач виразів | NCalcSync 5.4 | MIT | D-19 |
| Excel | ClosedXML 0.104 | MIT | ФВ-4.1 |
| Скрипти рівня 2 | Roslyn Scripting | MIT | ФВ-9.3 |
| Планувальник | **Quartz 3.13 (Apache-2.0)** за портом | Apache-2.0 | D-09 |
| API-документація | `Microsoft.AspNetCore.OpenApi` + Scalar | MIT | D-07 |
| Кеш | `IMemoryCache` + `IDistributedCache` на SQL | MIT | D-06 |
| Frontend | React 19 + Vite 6 + TypeScript | MIT | D-03 |
| UI-кіт | Mantine 7 | MIT | D-04 |
| Сітка | RevoGrid | MIT | FQ-1 |
| Тести | xUnit 2.9.2, NSubstitute, Testcontainers, NetArchTest | MIT/Apache | — |
| Навантаження | JMeter | Apache-2.0 | D-08 |

> **Планувальник за портом.** `IBackgroundJobScheduler` дозволяє замінити
> Quartz на Hangfire (LGPL) за день, якщо ІБ дасть добро (L-1). Дефолт —
> Quartz, тому питання ліцензії не блокує старт.

### Заборонені залежності (D-12)

`FluentAssertions` 8+ (комерційна Xceed) · `EFCore.BulkExtensions` (платна для
комерційного використання) · `EPPlus` 5+ (noncommercial) · `HyperFormula`
(GPLv3) · `Handsontable` (несумісна) · `NBomber` 5+ (комерційна) · будь-який
Redis-сервер (AGPL/SSPL) · AG Grid Enterprise · будь-що під
GPL / AGPL / SSPL / Polyform / cFOSS.

Перевірка ліцензій — **блокуючий крок CI** (D-82).

---

## 3.3 Збірки

```
Ecr.Domain            сутності, значеннєві типи, інваріанти. Залежностей немає.
Ecr.Expressions       лексер, парсер, резолвер, граф, обчислювач.
Ecr.Application       use-cases, порти, DTO. Знає Domain і Expressions.
Ecr.Calculations      методології, константи, одиниці, трейс.
Ecr.Infrastructure    EF Core, SQL, кеш, автентифікація, планувальник.
Ecr.Adapters.Excel    ClosedXML: імпорт/експорт.
Ecr.Adapters.PiAf     RTQP і Web API — за одним портом.
Ecr.Api               контролери, OpenAPI, health, ProblemDetails.
Ecr.Web               React SPA (npm, не в .sln).
tools/Ecr.DataGen     генератор синтетичного обсягу для замірів.
```

Правила залежностей (перевіряються NetArchTest, блокують CI):

1. `Ecr.Domain` не залежить ні від чого, крім BCL.
2. `Ecr.Application` не залежить від `Ecr.Infrastructure`.
3. Ніщо, крім `ext`-простору імен, не містить `Af`/`Legacy` у назвах типів.
4. `Ecr.Api` не звертається до `DbContext` напряму.
5. Жоден тип поза `Ecr.Infrastructure` не знає про EF Core.
6. `DateTime.Now`/`DateTimeOffset.Now` не використовуються ніде.
7. `float`/`double` не використовуються в результатних типах.
8. Жодного `Assembly.Load`, `Activator.CreateInstance` за рядком.

---

## 3.4 Порти

| Порт | Реалізація | Примітка |
|---|---|---|
| `ICellStore` | SQL Server | `ReadSliceAsync`, `ApplyAsync`, `BulkInsertAsync` |
| `IMetadataCache` | Memory + Distributed | ключ `v{id}:r{rev}` |
| `IExternalDataSource` | RTQP, PI Web API | два транспорти, одне налаштування |
| `IBackgroundJobScheduler` | Quartz (дефолт), Hangfire (опція) | D-09 |
| `IAccessDecisionService` | вбудована | повертає причину |
| `IClock` | системний, тестовий | АРХ-6a |
| `IUnitConverter` | `uom` | явні конверсії (D-74) |
| `IExpressionEngine` | власний парсер + NCalc | два діалекти |
| `IReportSnapshotWriter` | SQL `rpt.*` | контракт для SSRS |

> **`IExternalDataSink` не існує.** Запису в AF немає (D-44), а порожній
> контракт «на майбутнє» — запрошення його колись реалізувати (D-88).

---

## 3.5 Розгортання

- **≥2 інстанси застосунку** незалежно від виділених ресурсів (D-32).
- Стан у застосунку не зберігається; кеш метаданих прогрівається при старті.
- Фонові задачі — з блокуванням на рівні планувальника, щоб не подвоювалися.
- Health: `/health/live`, `/health/ready`, `/health/db` (остання показує режим
  редакції SQL).
- DDL виконують SQL Agent і збережені процедури під окремим principal;
  застосунок DDL не виконує ніколи (D-66).

## 3.6 Чому не DataObjects.Net

Розглядався як ORM із автоматичною еволюцією схеми. Це його головна перевага —
і саме вона нам не потрібна: за АРХ-1 схема фіксована, а еволюціонують дані.
Платити за автоеволюцію меншою екосистемою, меншим пулом фахівців і
нестандартним LINQ-провайдером не має сенсу. Рішення — EF Core 10 (D-02).

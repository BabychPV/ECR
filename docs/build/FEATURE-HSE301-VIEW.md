# FEATURE-HSE301-VIEW — PI → події → методологія → представлення «HSE301.Year»

> **Дата:** 2026-09-27 · **Статус:** Проєкт до виконання. Рішення **V-1…V-15 схвалені
> людиною 2026-09-27** і вносяться в реєстр `docs/tz/10-decisions.md` як `D-149` (V-1) і
> `D-171…D-184` (V-2…V-15); V-16…V-25 ухвалені за делегуванням людини того ж дня («все інше на
> твій розсуд не обмежуй себе») і внесені як `D-185…D-194` — §13.2 · **Мова продукту:** en/ru/kk · **Мова документа:** українська.
>
> ✎ **2026-10-01, стан реалізації** (звірено з `git log origin/dev/integration` і кодом; текст
> нижче — проєкт, де суперечить цьому абзацу, чинний абзац). Зроблено: **A1** — виконання
> прив'язки вікна рядка (`b3f24d4c`: хук, задача підтягування, щогодинний повтор), CRUD
> `/api/v1/row-window-maps` (`5a191109`, `a77afbff`, `ed152125` — знімок індексу скидається після
> POST/PUT/DELETE; клієнт `7cd73f23`; тест на реальному HTTP і SQL `8a329bd4`; контракт
> `27fa9237`), імпорт книги ставить підтягування вікон рядків (`bec56bb2`); **A6** — вкладка
> «Події з PI» і `source-event-maps`; імпорт пакета методологій є; представлення `rpt-views` —
> стан і публікація див. TESTER-GUIDE §7. Не перевірено в цьому проході: повний перелік
> кроків B0…B8 і I1…I3 таблиці виконання (§ нижче) проти коду — рядки таблиці кроків
> відображають стан на 2026-09-30.
>
> ✎ **2026-09-27, відповіді людини.** (1) «так погоджуюся на Подання» — V-1. (2) «так, вноси
> D-149 в реєстр та Схвалено запропоновані рішення» — V-1…V-15. (3) Про теги, межі подій,
> сезони Winter/Summer і категорії V6–V9: «це вже приходе з pi» — тому **подія (початок,
> кінець), її категорія й сезон (кейс HMB) читаються з PI Event Frames** (§4.7), а не
> вводяться вручну. (4) Про властивості компонентів: «це і є addstream» — §6.5 і
> FEATURE-REGISTRY-TABLES §11.4. (5) «все інше на твій розсуд не обмежуй себе» — V-16…V-25.
>
> **Термін.** «Представлення» (View) — вигляд даних документа лише для читання (§8).
> «Подання» в цьому документі, як і в коді, означає **лише** подачу аркуша на перевірку
> (Submit: `SubmitSheetHandler`, `F-05`).
>
> **Залежності:** [FEATURE-REGISTRY-TABLES](FEATURE-REGISTRY-TABLES.md) — довідники зі
> складеними ключами й функції доступу до них у виразах методологій (склад газу потоку);
> [DIRECTIVE-16 §1 D16-03](DIRECTIVE-16.md) — згортка точок PI в межах періоду екземпляра.
>
> **Факти про форму 301** — повний розбір файлу замовника `excel-analysis.md` (макет,
> 22 відновлені співвідношення, ланцюжки A/B/C, правила зведень, 9 відкритих питань). Це
> робочий артефакт сесії аналізу **поза репозиторієм**; чи переносити його в `docs/` —
> разом із відповіддю на RQ-1 FEATURE-REGISTRY-TABLES (дані з міткою MSIP «Internal»). Тут
> факти не повторюються, лише цитуються як «розбір §N».
>
> **Правило цього документа:** кожне твердження про код — з `файл:рядок`, перевіреним
> 2026-09-27 на `dev/integration`. Доступ до складу газу (§6.5) записано синтаксисом
> [FEATURE-REGISTRY-TABLES §5](FEATURE-REGISTRY-TABLES.md#5-мова-виразів).
>
> **Позначки:** кроки плану — `F0…F9`, `F4e`, `A1…A6`, `B0a…B7`, `I1…I3`, `RG1…RG3`, `F4b`
> (§11; `RG` — кроки, що чекають на довідники); `V-n` — рішення (§13.2); `HQ-n` — питання до
> замовника (§13.3); `HR-n` — ризики (§13.1); `G-…` — золоті тести (§12.1, §12.5).

---

## 0. Коротка відповідь замовнику

**Питання:** «Чи можна вже зараз, після PI SQL Client коннектора, отримати дані,
оформити їх у представлення 301years, додати складні розрахунки й бачити всі вхідні
дані та розрахунки у вигляді 301?»

**Відповідь: частково.**

- **Що працює наскрізь уже сьогодні.** PI SQL Client підключається, показує каталог AF і
  за розкладом збирає **сирі** значення атрибутів у `ext.RawDataPoint`; точки переносяться
  у **фіксовані** комірки документа. Шаблон уміє таблицю подій із динамічними рядками й
  формулою тривалості; методологія рахує кожен рядок із версіонованими константами й
  календарем періоду; результати видно в панелі розрахунків і у вивантаженні Excel.
- **Чого бракує для 301.** (0) **Читання самих подій із PI.** Людина підтвердила: події
  факелювання — початок і кінець до секунди, категорія V6–V9, сезон (кейс HMB Winter/Summer)
  — «це вже приходе з pi», тобто лежать у PI AF як Event Frames з атрибутами. Сьогодні порт
  джерела вміє лише каталог елементів і сирі точки атрибутів (`IExternalDataSource.cs:11-24`),
  а інтеграція пише в комірки тільки числа (`ICellPatcher.cs:45`). (1) Запиту до PI **за
  вікном події** — Total чи середнє за `[початок, кінець)` рядка: сьогодні лише сирі точки, а
  місячна згортка не зважена за часом. (2) Доступу методології до складу газу потоку — це
  закриває FEATURE-REGISTRY-TABLES. (3) Збереження проміжних значень і відповіді «звідки це
  число». (4) Самого **вигляду 301**: транспонування, багаторівневої шапки, групування
  подій за місяцями з `Total`, зведень `HP_LP` (Σ, max, YTD), річного виду й експорту з
  об'єднаними комірками.
- **Що пропонуємо.** Виправити конвеєр PI; додати **джерело подій «PI Event Frames → рядки
  `FLARE_RECORD`»** (§4.7): мапінг «атрибут EF → колонка» з каталогу PI, синхронізація за
  ідентифікатором EF зі збереженням ручних правок; об'єм — з атрибута EF або за вікном рядка
  (прив'язка V-2); одну методологію `HSE301.FLARE` на рядок, збереження трейсу й новий шар
  **Представлення** (`cfg.ViewDef`) лише для читання — погоджено людиною 2026-09-27
  (V-1 → `D-149`: межа `D-52a` на представлення не поширюється). План — 35 власних кроків
  (§11). Перший робочий аркуш `AI_Int_SG_V8` з подіями й об'ємами з PI з'явиться після
  foundation і хвиль A–B. Склад газу з довідника підключається після
  FEATURE-REGISTRY-TABLES; до того властивості складу (ρ, μ, S, T0…) вводяться чи
  імпортуються в рядок події — рівно так, як їх показує сам 301. Ручне введення події
  лишається — для виправлень і подій, яких у PI немає.

---

## 1. Що таке HSE301 і наскрізний ланцюжок

### 1.1 Коротко

`HSE301.Year.xlsx` — вивантаження SSRS. Формул у ньому 0, PI-тегів 0, розрахунок живе на
сервері (розбір §0, §1.4). Структура така:

| Аркуш | Що це | Орієнтація |
|---|---|---|
| `AI_Cont_FG_V7` | безперервне спалювання паливного газу (Pilot, Purge) щомісяця | рядки = атрибути, стовпці = місяць × (Pilot, Purge) |
| `AI_Int_SG_V8`, `AI_Int_SG_V9` | періодичні скиди кислого газу, подія до секунди | рядки = 136 атрибутів, стовпці = події, групи за місяцем + `Total` |
| `AI_Int_FG_V6…V9(_N2)` | періодичні скиди паливного газу й азотні події | те саме, без секції складу |
| `HP_LP_tons`, `HP_LP_gsec` | зведення місяць × режим × HP/LP × категорія V6–V9 | дві групи рядків (Fuel/Sour), 22–24 логічні стовпці на місяць |
| `Permits` | діаграма Ганта дозволів | картинка |
| `Document map` | навігація по аркушах | дерево |

Методологія відновлена й перевірена на 76 подіях (розбір §1.4, табл. рядки 1–28); правила
агрегації в зведеннях перевірені 2 304 співвідношеннями без жодного розходження (§1.3).

### 1.2 Ланцюжок прикладу A в новій системі

Подія 28.01.2026 14:09:20–14:24:50, HP-факел, потік `1D-2` / кейс `370 Winter` (розбір §1.5 A).

```
PI AF: Event Frame шаблону подій факела — StartTime 09:09:20Z, EndTime 09:24:50Z,     [§4.7]
       атрибути: категорія «V8», сезон/кейс «370 Winter», факел, потік (які саме — мапінг)
   │  ReadEventsAsync(шаблон, вікно збору) → SourceEventSyncJob: upsert за ID події
   ▼
ext.SourceEventLink  ← ID події, стан (Synced/Missing/Open/…), ручні правки   [§4.7]
   │  ICellPatcher, origin = Integration: рядок EF-<id>, Start/End у TZ проєкту,
   │  Category, HmbCase, Flare… (ручну правку не перетирає, D-118 → KeptManual)
   ▼
doc.CellValue  FLARE_RECORD[EF-…].Start = 28.01 14:09:20, .End = 14:24:50, .Category = V8
   │  об'єм — один із двох налаштованих режимів (§4.7.5):
   │   а) атрибут EF «об'єм» → конверсія одиниць (§4.2) → Volume_Sm3
   │   б) хук IRowWindowTrigger (Start/End змінилися) → ReadWindowAsync(Total,
   │      [14:09:20; 14:24:50) за TZ проєкту → UTC) → ext.RowWindowValue           [§4.4]
   ▼
doc.CellValue  FLARE_RECORD[EF-…].Volume_Sm3 = 269.258                             [§5.2]
   │  CalculationTrigger → IRecalculationJob(документ, 202601)                     [§4.5]
   ▼
HSE301.FLARE v1.0 (рядок): V_Sm3 = 269.258 → M_t = 269.258·0.9589/1000 = 0.2581915
                           tons[SO2] = M·0.02·17.2965447·0.9984 = 0.0891735
                           gsec[SO2] = 0.0891735·10⁶/930 = 95.885               [§6]
   │  calc.CalculationResult (Output + Intermediate) + CalculationStep(TraceJson)  [§7]
   ▼
Представлення «HSE301.Year»: аркуш AI_Int_SG_V8, стовпець JAN/1, рядок «SO2, g/sec» = 95.885
                             аркуш HP_LP_gsec, JAN · Intermittent · V8 · HP (max) SO2 = 142.525
                             YTD JAN SO2 = 142.525 + 0.000668 (FG continuous) = 142.5258    [§8]
   │  клік на число → «Чому це число?» → формула → входи → точки PI / подія PI / константа / запис
```

---

## 2. Поточний стан

### 2.1 Вимога → стан

| # | Вимога 301 | Стан | Де (перевірено) | Чого бракує |
|---|---|---|---|---|
| 1 | Підключення PI SQL Client, каталог, розклад, ручний збір | **Є** | `PiSqlClientDataSource.cs:106-142` (каталог), `ExternalConfiguration.cs:131-157` (`ext.CollectionSchedule`, Cron, `LookbackDays` 7), `SourcesController.cs:43` (`POST /sources/{id}/collect`) | — |
| 2 | Читання значень атрибута AF | **Частково** | `PiSqlClientDataSource.cs:431-448` — лише сирі `[Master].[Element].[Value]` у `[from,to)`; ≤ 5 000 точок за запит (`CollectionRunner.cs:37`), хвіст дозбирається (`PiSqlClientDataSource.cs:201-211`) | якість завжди `"Good"` (`:198`) |
| 3 | Інтерпольовані значення, summary (Total/середнє), крок | **Немає** | `IExternalDataSource.cs:77-83` — `CollectionRequest` без типу запиту, кроку й агрегації; PI Web API теж лише `streams/{webId}/recorded` (`PiWebApiDataSource.cs:208`) | новий тип запиту в порті (§4.3) |
| 4 | Згортка точок у значення періоду | **Є** — виправлено [D16-03](DIRECTIVE-16.md) (виконано в `3e6d2efa`) | до `3e6d2efa` — `CollectionJob.cs:168-229`, `MaterializeCollectedDataJob.cs:149-196` (згортка за вікном збору). Тепер: ряд — усі точки `ext.RawDataPoint` поля в межах періоду екземпляра `[опівніч PeriodStart, опівніч PeriodEnd+1)` у поясі проєкту (`Period.UtcBounds` → `Period.UtcRange`, `src/Ecr.Domain/Entities/Documents/Period.cs`, та сама арифметика `Period.ToUtc`, D-68; у `3e6d2efa` це була копія `PeriodUtcRange`, прибрана в `3e96e0d3`); вікно збору лише обирає екземпляри (`CollectionJob`: періоди, що перетинають вікно, за спаданням, стеля 200 цілей відсікає найстаріші). Стеля матеріалізації — **500 000 точок на поле** (`MaterializeCollectedDataJob.MaxPoints`): поле понад неї в комірку не пишеться, у `itg.CollectionCoverage` — `SkippedPointCeiling` з кодом поля, у `jobs.materializeDone` — `overCeiling`. ⚠ Не плутати з 5 000 точок на **запит** у `CollectionRunner` (рядок 2). `PeriodFold` не змінено | зважене середнє й інтеграл (рядок 5), конверсія (рядок 6) |
| 5 | Середнє, зважене за часом; інтеграл (Total) | **Немає** | `PeriodFold.cs:53` — `Avg` = сума / кількість точок; перелік закритий `CK_EFM_Transform` (`ExternalConfiguration.cs:81-87`) | §4.1 |
| 6 | Конверсія одиниць при завантаженні (06-integration §6.4 п.3, ФВ-16.10) | **Немає** | `SourceUnitConverter.Convert` (`SourceUnitConverter.cs:109-133`) не викликає ніхто; збір лише перевіряє одиницю (`CollectionRunner.cs:486`). `Ecr.Infrastructure` не посилається на `Ecr.Adapters.PiAf`, тож задача матеріалізації цей клас і не бачить | §4.2 |
| 7 | Атрибут PI → колонка, **вікно = рядок** | **Немає** | `UQ_EntityFieldMap(SourceEntityId, SourceField)` (`ExternalConfiguration.cs:114-115`) — один атрибут дає одну комірку; адресат рядка фіксований і «ключ рядка з джерела» заборонений навмисно (`EntityFieldMap.cs:110-128`) | нова сутність `ext.RowWindowMap` (§4.4) |
| 8 | Події як рядки, час до секунди | **Є** | `TableRowMode.Dynamic` (`Enums.cs:26-34`), `POST /documents/{id}/rows` (`CellsController.cs:111-130`), `ValueDate datetime2(3)` (`CellValueConfiguration.cs:42`) | — |
| 9 | Тривалість формулою | **Є** | `Date − Date` → дні (`Evaluator.cs:528-535`) | обов'язковий `ROUND(…*86400, 0)`: різниця йде через `double` |
| 10 | Методологія на рядок | **Є** | `GenericCalculationModule.cs:99-232`; прив'язка `CalculationBinding.cs:15-27`; правила відбору `MethodologyResolver.cs:110-185` | — |
| 11 | Входи методології з рядка | **Частково** | `CalculationInputBuilder.cs:58-72` передає **лише** `ValueNumeric`/`ValueString`: Lookup, Date і Bool у `@Arg` не доїжджають | ознаки для формул — числові чи текстові колонки (§5.3) |
| 12 | Доступ методології до довідників (склад газу) | **Немає** | `MethodologyEvaluationContext.cs:123-124` → `#REF` | → FEATURE-REGISTRY-TABLES |
| 13 | Коефіцієнти з вікнами чинності | **Є** | `MethodologyConstant` (`ValidFrom/ValidTo`, речовина, `Category`, `Source`); резолв за кінцем періоду (`GenericCalculationModule.cs:327-337`) | — |
| 14 | Календар періоду | **Є** | `[Period].Days/Hours/Seconds/Start/End/Year/Sequence` (`Evaluator.cs:846-860`) | — (пілот `9.7 × години` виражається вже зараз) |
| 15 | Проміжні значення і трейс на комірку | **Частково** | кроки пишуться лише на `TraceLevel.Full` (`TraceRecorder.cs:30`), за замовчуванням `ErrorsOnly` (`MethodologyVersion.cs:61`); `CalculationStep` з `resultId: null` (`CalculationResultStore.cs:154`) і без документа/рядка (`CalculationTrace.cs:97-113`); у `TraceJson` лягає лише код помилки (`GenericCalculationModule.cs:228-230`); `calc.CalculationInput` лише видаляється (`DocumentDeletionStore.cs:54`), не пишеться ніде | §7 |
| 16 | Повний трейс «на пробу» | **Є** | `POST /methodologies/{id}/simulate` (`MethodologiesController.cs:613`) — трейс рядками (`SimulationResultDto.cs:23-28`) | прив'язки до документа немає |
| 17 | Перерахунок після нових даних PI | **Частково** | вручну: `POST /documents/{id}/recalculate` (`DocumentsController.cs:301`), `POST /projects/{id}/recalculate` (`ProjectsController.cs:313`); нічний — опція, **вимкнена** за замовчуванням (`NightlyRecalculationScheduling.cs:65,102`); застарілі числа блокують подання (`SubmitSheetHandler.cs:187-217`) | після матеріалізації не ставиться нічого (§4.5) |
| 18 | Результат по речовині в колонці | **Частково** | `CalculatedCellOverlay.cs:34-35,145` — речовини одного виходу в комірці **сумуються** | представлення читає результати з речовиною напряму (§8.3) |
| 19 | Багаторівнева шапка з об'єднанням | **Немає** | сітка: `name` колонки — рядок (`DocumentGrid.tsx:1943-1945`); Excel: шапка в один рядок (`ExcelExporter.cs:303-337`) | RevoGrid 4.11 групи колонок **вміє**: `ColumnGrouping` (`@revolist/revogrid/dist/types/types/interfaces.d.ts:77-86`) |
| 20 | Групи рядків, згортання | **Немає** | `RowDef.ParentRowDefId` є (`RowDef.cs:39`), а `RowDto` його не несе (`TableSliceDto.cs:115-122`) | §8 |
| 21 | Підсумки | **Частково** | один рядок, рахує клієнт (`gridTotals.ts`, `pinnedBottomSource` — `DocumentGrid.tsx:1769`) | Σ/max по групі, YTD |
| 22 | Транспонування (запис = стовпець) | **Немає** | `TableLayoutKind.MonthsInColumns` поведінки не має (`Enums.cs:13-23`; використовується лише в DTO й редакторі); `ColumnDef.IsMonthColumn/MonthNumber` без сеттера (`ColumnDef.cs:31-32`) | §8 |
| 23 | Звітний шар `rpt.*` | **Частково** | макет — одна група + `sum/count/avg/min/max` (`ReportLayout.cs:54-79`); джерело — лише `CalculationResults` (`ReportSnapshotBuilder.cs:415-419,512-518`); діалект Report — 7 скалярних функцій над рядком (`ReportFunctions.cs:19-29`); **матриці з динамічними колонками виключені** (`10-decisions.md:289`, D-52a) | §8.1 |
| 24 | Річний вид (12 місяців разом) | **Немає** | `GET /documents/{id}/tables?periodKey=` (`DocumentsController.cs:382-385`) — один період | §8.8 |
| 25 | «Дані актуальні на…» по джерелу | **Частково** | `LastRunAt`, `Watermark` розкладу (`ExternalConfiguration.cs:143-144`), журнал прогонів; у документі не показується | §8.8 |
| 26 | Одиниці 301 (Sm3, м/с, об.%, мас.%, МДж/ст.м3, м²) | **Немає** | у сіді лише кг/м3/Дж/с/К/моль/one і похідні (`09-seed.sql:130-169`) | крок F1 |
| 27 | Події з PI: Event Frames шаблону з часом і атрибутами → рядки таблиці | **Немає** | порт має лише `DiscoverAsync` і `ReadAsync` (`IExternalDataSource.cs:11-24`); PI SQL Client читає тільки `[Master].[Element].*` (`PiSqlClientDataSource.cs:64-69`, `:431-448`), хоча `[Master].[EventFrame].[EventFrame]`/`[Attribute]` чинне рішення вже читає через власну в'юху в базі AF (`B14-ecr-integration-and-data.md:30-40`); інтеграція пише лише числа — `IntegrationCellValue(RowKey, ColumnDefId, decimal Value)` (`ICellPatcher.cs:45`) — і лише створенням рядка (`BaseVersion: null`, `IntegrationCellPatcher.cs:103-106`); видалення рядка документа в системі немає (маршрутів у `CellsController.cs` немає) | порт `ReadEventsAsync`, мапінг «атрибут → колонка», синхронізація за ID події (§4.7) |

### 2.2 Дефекти, які зачіпають 301

| # | Дефект | Де | Що робимо |
|---|---|---|---|
| ✓ Д-1 | Згортка PI за вікном **збору**, а не за періодом екземпляра; задача летить у всі екземпляри таблиці | до `3e6d2efa`: `CollectionJob.cs:168-229`, `MaterializeCollectedDataJob.cs:149-196` | **не тут:** [DIRECTIVE-16 §1 D16-03](DIRECTIVE-16.md) — **виконано в `3e6d2efa`** (що саме — §2.1 рядок 4); передумова кроку F2 знята |
| ⛔ Д-2 | `Avg` — просте середнє точок, а не зважене за часом: PI стискає ряд, і точок більше там, де величина змінюється | `PeriodFold.cs:53` | §4.1, крок F2 |
| ⛔ Д-3 | Конверсії на межі немає: значення лягає в комірку в одиниці джерела | `SourceUnitConverter.cs:109-133` без викликачів | §4.2, крок F3 |
| ⚠ Д-4 | Якість точки PI SQL Client — завжди `Good`: системні стани PI (`I/O Timeout`, `Bad`) не відрізнити | `PiSqlClientDataSource.cs:198` | §4.6, крок F4 |
| ⚠ Д-5 | Трейс не прив'язаний до результату й рядка, у `TraceJson` — лише код помилки; входи не зберігаються | `CalculationResultStore.cs:154`, `GenericCalculationModule.cs:228-230` | §7, кроки F6, A3a, A3b |
| ⚠ Д-6 | Lookup/Date/Bool не доходять у `@Arg` | `CalculationInputBuilder.cs:58-72` | обхід у моделі шаблону (§5.3); розіменування Lookup — FEATURE-REGISTRY-TABLES (`R-12`: `EntryRef` лише у версіях `Strict`, крок `RT-23a`) |
| ⚠ Д-7 | `Scale` колонки **відхиляє** зайві знаки, а не округлює (`ColumnDef.cs:92-96`) — інтеграл PI у колонку зі `Scale 3` впаде на `ECR-CELL-0422` | `ColumnDef.cs:301-308` | у 301 об'єм має `Scale 6` + формат показу `0.000` (§5.2) |
| ✓ Д-8 | REGFIELD у правилах валідації | — | лише посиланням: [DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md) — виконано в `5f00b791` для «Перевірити» й подання аркуша, шлях **збереження** (`PatchCellsHandler.Validate`) — у `400c58b1` (знімок будує `TableValidation.LoadRegistryFieldsAsync`) |

---

## 3. Цілі, не-цілі, сценарії

### 3.1 Цілі (V1)

0. **Події — з PI.** Кожен Event Frame шаблону подій факела стає рядком `FLARE_RECORD`:
   початок і кінець до секунди, категорія V6–V9, сезон (кейс HMB) і решта атрибутів — за
   мапінгом, який адміністратор задає з каталогу PI (§4.7). Ручна правка поля не
   перетирається наступною синхронізацією; подія, якої в PI немає, вводиться руками.
1. Об'єм кожної події — з PI: з атрибута EF або **за вікном рядка** (Total), як налаштовано,
   з видимим провенансом і статусом «підтягнуто / ручне / немає даних / частково».
2. Одна методологія `HSE301.FLARE` відтворює 22 співвідношення розбору §1.4 з точністю
   ≤ 1e-9 відносно (золоті A, B, C).
3. Будь-яке число в представленні пояснюється до першоджерела: формула → підставлені значення →
   точки PI, константа з вікном чинності, ручна правка, запис довідника.
4. Представлення «HSE301.Year» відтворює аркуші `AI_Cont_FG_V7`, `AI_Int_SG_V8`, `AI_Int_SG_V9`,
   `AI_Int_FG_V8_N2`, `HP_LP_tons`, `HP_LP_gsec`: структуру, підписи, формати й значення
   (звірка — §12).
5. Експорт XLSX з тією самою структурою (злиття, закріплення, формати чисел), друк.
6. Числа перераховуються самі, щойно приходять нові дані.

### 3.2 Не-цілі (V1)

| Не робимо | Чому | Коли |
|---|---|---|
| Автовиявлення подій у ECR за порогом витрати | події з межами до секунди вже формує PI («це вже приходе з pi», HQ-1); ECR їх читає, а не вгадує | не потрібне |
| Запис у PI (створення чи правка Event Frame) | PI — лише джерело (`D-44`); виправлення живе в ECR як ручна правка | — |
| Склад газу (34 компоненти vol%/wt%) на аркуші подій | потрібні довідники зі складеним ключем | кроки RG1–RG2, після FEATURE-REGISTRY-TABLES |
| Лабораторний аналіз FG у мг/м3 → об.% | те саме | крок RG3 |
| Діаграма Ганта дозволів | допоміжний вигляд; ліміти вже йдуть константами з вікнами | V2 |
| PDF держформи, підписи, передача регулятору | лишається в SSRS (D-52a) | — |
| Onshore-секції | у `Document map` лише Offshore (V-24) | аркушами-даними в конструкторі, коли знадобляться |
| Зміни в `rpt.*` | публічний контракт SSRS (D-53, ФВ-10.12) | — |

### 3.3 Сценарії

| Роль | Сценарій | Екран |
|---|---|---|
| **Методолог** (`Calculation.*`, `Template.Edit`) | заводить `HSE301.FLARE`, константи з вікнами чинності, позначає формули «показувати як проміжний результат», перевіряє на золотих прикладах; збирає представлення в конструкторі й публікує | методики (§10.5), конструктор представлення (§10.5) |
| **Адміністратор інтеграції** (`Integration.Manage`) | обирає з каталогу PI шаблон Event Frame подій факела, зіставляє його атрибути з колонками `FLARE_RECORD` (початок, кінець, категорія, сезон/кейс, факел…), обирає режим об'єму (атрибут EF або вікно рядка) і перевіряє мапінг на реальних подіях; для режиму «вікно рядка» налаштовує прив'язку атрибута витрати до колонки об'єму | вкладки «Події з PI» і «Дані з PI» таблиці (§10.6) |
| **Еколог-інженер** (доступ до документа) | бачить події місяця, що прийшли з PI, з об'ємом і миттєвим попереднім розрахунком; виправляє поле вручну, коли PI бреше (правка зберігається й позначається), або повертає значення з PI; додає вручну подію, якої в PI немає; вирішує долю події, що зникла з PI; відкриває представлення й звіряє з очікуваним | реєстр подій (§10.4), переглядач (§10.2) |
| **Рецензент / погоджувач** (`Document.View`, `Calculation.View`) | відкриває річне представлення, фільтрує місяці, клацає підозріле число й іде ланцюжком до точок PI чи константи; бачить «розрахунок застарів» до подання; вивантажує XLSX | переглядач (§10.2), «Чому це число?» (§10.3) |

---

## 4. Конвеєр PI

### 4.0 Передумова — [DIRECTIVE-16 §1 D16-03](DIRECTIVE-16.md)

Згортка в межах періоду екземпляра й постановка задач лише в екземпляри, чий період
перетинає вікно збору, робиться за D16-03. Цей документ на неї **спирається**, не
дублює: крок F2 не починався, доки D16-03 не в `dev/integration`, бо обидва правлять
`MaterializeCollectedDataJob.cs`.

✓ **D16-03 виконано в `3e6d2efa`**, уточнено в `3e96e0d3` (перевірено читанням коду на
`origin/dev/integration` 2026-09-27):

- `PeriodFold` **не змінено** — та сама сигнатура й семантика (`Avg` досі просте середнє,
  `PeriodFold.cs:53`). Змінився **вхідний ряд**: усі точки `ext.RawDataPoint` поля в межах
  періоду екземпляра `[опівніч PeriodStart, опівніч PeriodEnd+1)` у поясі проєкту — через
  `Period.UtcBounds` → `Period.UtcRange` (`src/Ecr.Domain/Entities/Documents/Period.cs`; той
  самий приватний `Period.ToUtc`, D-68). У `3e6d2efa` це була копія
  `Infrastructure/Integration/PeriodUtcRange.cs`, яку `3e96e0d3` прибрав. Межі періоду для
  F2/F3 беруться саме з `Period.UtcBounds` — другої реалізації не пишемо.
- Вікно збору лише **обирає** екземпляри: `CollectionJob` ставить задачу тільки періодам, що
  перетинають вікно, за спаданням ключа періоду; стеля 200 цілей відсікає найстаріші.
- Стеля матеріалізації — **500 000 точок на поле** (`MaterializeCollectedDataJob.MaxPoints`):
  поле понад стелю в комірку не пишеться (часткова сума — хибне число), у
  `itg.CollectionCoverage` іде `SkippedPointCeiling` з кодом поля, у `jobs.materializeDone` —
  параметр `overCeiling`. ⚠ Це не ліміт 5 000 точок на **запит** у `CollectionRunner`
  (`CollectionRunner.cs:37`, §2.1 рядок 2).
- **Не зроблено й лишається кроками цього документа:** конверсія на межі через
  `SourceUnitConverter` і перехід матеріалізації на згортки за часом (F3). Зважене `Avg` і
  інтеграл `Total` як чиста згортка — ✓ F2 (§4.1).
- Відоме й не виправлене: статуси покриття адміністратору не показуються, а хибний
  `ConflictKeptManual` з'являється щопрогону — ризик HR-11 (§13.1).

### 4.1 Середнє, зважене за часом, та інтеграл

**Рішення (V-3):** два нові способи згортання в `AggregationKind`
(`EntityFieldMap.cs:26-45`) — **адитивно, у кінець переліку**:

| Код | Значення | Формула на вікні `[a, b)` |
|---|---|---|
| `TimeWeightedAvg = 6` | середнє, зважене за часом | `∫ v(t) dt / T_покр` — інтеграл по покритих відрізках, поділений на **покритий** час `T_покр`; при повному покритті це `∫ₐᵇ v(t) dt / (b − a)` |
| `TimeIntegral = 7` | інтеграл (аналог PI Total) | `∫ v(t) dt` по покритих відрізках у «одиниця × секунда», далі конверсія в цільову (§4.2) |

✓ **F2 виконано** (`PeriodFold.Fold(kind, TimedPoint[], from, to, isStep, maxGap)` →
`TimeFoldResult(Value, PercentGood)`; міграція `HSE301M1TimeWeighted`). Уточнення семантики
за реалізацією:

- **Знаменник середнього — покритий час, а не довжина вікна.** Прогалина не входить ні в
  інтеграл, ні в знаменник: інакше вікно, покрите наполовину, дало б удвічі менше середнє —
  правдоподібне й хибне. Частку покриття (0–100) повертає `PercentGood`.
- **Без покриття `Value = null`, а не 0**: «даних не було» і «інтеграл нуль» — різні стани.
- **Без екстраполяції**: частина вікна до першої точки чи після останньої — прогалина.
- **`maxGap` — параметр функції** (`null` — поріг не застосовується). Числа порогу в цьому
  документі немає; його обирає викликач (F3/F4) з конфігурації, не з коду.
- Точка з `IsGood = false` робить прогалиною відрізки, що на неї спираються (для
  ступінчастого — лише той, де вона ліва: саме її значення тримається). Тлумачення якості
  джерела в `IsGood` — справа викликача (§4.6).

- **Інтерполяція між точками** — лінійна (аналогова величина) або ступінчаста
  (`IsStep`, як «Step» атрибута AF). Нове поле `ext.EntityFieldMap.IsStep bit NOT NULL
  DEFAULT 0`.
- **Межі вікна.** Значення на `a` і `b` беруться інтерполяцією: остання точка до `a`
  (`TOP 1 … WHERE Timestamp < a ORDER BY Timestamp DESC`) і перша після `b`. Без неї інтеграл
  короткої події, в якій стиснення PI не лишило жодної точки, дав би нуль, а не об'єм.
- **Прогалини** (точки якості ≠ Good, розрив понад `MaxGap`) у інтеграл не входять;
  частка покриття повертається як `PercentGood` і дає статус `Partial` (§4.4).
- Функція одна на обидва споживачі: `PeriodFold.Fold(kind, series, from, to, isStep, maxGap)` для
  місячної матеріалізації й `WindowFold` для вікна рядка (§4.3). Період — теж вікно.
  ⛔ Друга копія інтеграла розійшлася б із першою на межах — той самий клас, що A7-27,
  від якого `PeriodFold` і винесено (`PeriodFold.cs:10-16`).
- `CK_EFM_Transform` розширюється двома значеннями — міграція M1.

### 4.2 Конверсія одиниць на межі

**Рішення (V-4):** арифметика переїжджає в `Ecr.Application/Sources/BoundaryUnitConversion.cs`
на доменному `UnitConverter` і порту `IUnitCatalog`. `SourceUnitConverter`
(`Ecr.Adapters.PiAf`) делегує їй — копії немає. Причина переїзду: `MaterializeCollectedDataJob`
живе в `Ecr.Infrastructure`, а той на адаптер PI не посилається (`Ecr.Infrastructure.csproj:8-10`).

- `MaterializeCollectedDataJob` і `RowWindowFetchJob` конвертують **після** згортки:
  `SourceUnitId` → `TargetUnitId` мапінгу, а для `TimeIntegral` — ще й множення на час.
  Приклад: `Sm3/h × s → Sm3` через базові одиниці розмірностей `StdVolumeFlow` і `StdVolume`.
- Журнал (06-integration §6.4 п.3): у `itg.CollectionCoverage` пишеться статус
  `Converted` з деталями `Sm3/h→Sm3 ×0.000277…`. Для вікна рядка коефіцієнт лягає в
  `ext.RowWindowValue.ConversionFactor`. Сирі точки лишаються в одиниці джерела (ФВ-11.7).
- **Рішення (V-12):** `Sm3` — окрема розмірність `StdVolume`, а не `m3`. Стандартний і
  робочий кубометр не конвертуються множником, і мовчазна конверсія між ними дала б
  правдоподібне число.

### 4.3 Нові типи запиту в порті

Адитивна зміна `IExternalDataSource.cs` — жоден чинний виклик не змінюється:

```csharp
public enum SourceQueryKind : byte { Raw = 0, Interpolated = 1 }
public enum SourceSummaryKind : byte { Total = 0, Average = 1, Minimum = 2, Maximum = 3, Count = 4 } // Average — зважене за часом

// ⚠ Нові поля — з типовими значеннями: CollectionRunner і всі тести лишаються як є.
public sealed record CollectionRequest(
    int DataSourceId, int SourceEntityId, string SourcePath,
    DateTime FromUtc, DateTime ToUtc, int MaxPoints,
    SourceQueryKind Kind = SourceQueryKind.Raw,
    TimeSpan? Step = null);                       // обов'язковий для Interpolated

public sealed record WindowRequest(
    int DataSourceId, int SourceEntityId, string SourcePath,
    DateTime FromUtc, DateTime ToUtc, SourceSummaryKind Summary, bool IsStep);

public sealed record WindowResult(
    decimal? Value, string? SourceUnitSymbol, int PointCount, decimal? PercentGood,
    WindowComputedBy ComputedBy,                  // Local | Server
    IReadOnlyList<TimeInterval> Gaps, string? ErrorCode);

public interface IExternalDataSource
{
    // … чинні члени …

    /// ⚠ Типова реалізація — ЛОКАЛЬНА: читає Raw із запасом на межі й згортає WindowFold.
    /// Адаптер перевизначає метод, лише якщо вміє summary на сервері й це налаштовано.
    Task<WindowResult> ReadWindowAsync(WindowRequest request, CancellationToken ct)
        => WindowFold.FromRawAsync(this, request, ct);
}
```

**Рішення (V-3):** еталон — **локальна** згортка з сирих точок. Серверний summary —
оптимізація, яку можна увімкнути конфігурацією. Причини:

1. Імена табличних функцій RTQP для інтерпольованих і summary-даних **у репозиторії не
   описані**: B14 §1 (`B14-ecr-integration-and-data.md:13-53`) описує лише в'юхи Event
   Frame. Поле Query з `piarchive..picomp2` у макеті (`screens-data.js:1071`) — синтаксис
   PI OLEDB Provider, а не RTQP. Вигаданий дефолт виглядав би як робоче налаштування —
   той самий аргумент, що для FLERT (`B14-ecr-integration-and-data.md:226-231`).
2. ⚠ PI Total за замовчуванням вважає витрату «за добу». Для атрибута в `Sm3/h` серверне
   число без поправки завищене в 24 рази — і правдоподібне (HQ-16). Локальний інтеграл
   працює в одиницях каталогу й такої пастки не має.

**Реалізація для PI SQL Client (крок F4):**

| Що | Як |
|---|---|
| Інтерпольовані | ключ конфігурації `PiSqlClient:InterpolatedQuery` з заповнювачами `{template}`, `{attribute}` і параметрами `?` (від, до, крок) — за зразком `ValueQueryKey` (`PiSqlClientDataSource.cs:85-93`). **Типового тексту немає**; без ключа — `ECR-INT-0422` `err.ECR-INT-0422.queryKindNotConfigured` |
| Summary | ключ `PiSqlClient:SummaryQuery`; є ключ — `ReadWindowAsync` перевизначено й повертає `ComputedBy = Server`; немає — працює типова локальна реалізація |
| Звірка імен | тексти запитів звіряються з AVEVA PI SQL DAS (RTQP Engine) Reference тією ж процедурою, що `Q-197` (`PiSqlClientDataSource.cs:54-63`), і лише тоді потрапляють у налаштування середовища |
| Якість (Д-4) | запит може повернути необов'язкову колонку `Quality`/`Status`; немає — `Good`, як у `SqlDataSource.cs:205` |
| Поле в UI | «Тип запиту: Сирі / Інтерпольовані (крок) / Summary (тип)» у формі прив'язки (§10.6) і `Aggregation` + `IsStep` у формі мапінгу (`features/mapping/CreateMappingModal.tsx`) |

PI Web API (`/streams/{webId}/summary`, `/interpolated`) — окремий крок F4b, за потреби.

### 4.4 Прив'язка «атрибут PI → колонка, вікно = рядок»

**Рішення (V-2):** нова сутність `ext.RowWindowMap`, а не розширення `EntityFieldMap`:

- `UQ_EntityFieldMap(SourceEntityId, SourceField)` (`ExternalConfiguration.cs:114-115`) не
  дозволяє тому самому тегу водночас давати місячну суму й об'єм кожної події;
- фіксований адресат рядка — свідомий інваріант `EntityFieldMap` (`EntityFieldMap.cs:113-122`),
  і розмивати його заради вікна не варто;
- один атрибут обслуговує **багато** рядків, а вікно береться з самого рядка.

```sql
ext.RowWindowMap                       -- «шаблонна» частина: що з чим і як
    Id int PK,
    TableDefId          int  FK cfg.TableDef,
    TargetColumnDefId   int  FK cfg.ColumnDef,     -- Decimal-колонка: Volume_Sm3
    StartColumnDefId    int  FK cfg.ColumnDef,     -- Date-колонка початку
    EndColumnDefId      int  FK cfg.ColumnDef,     -- Date-колонка кінця (виключно)
    SelectorColumnDefId int  NULL FK cfg.ColumnDef,-- колонка, що обирає джерело (PiSourceKey)
    Summary             tinyint,                   -- SourceSummaryKind
    IsStep              bit  DEFAULT 0,
    TargetUnitId        int  FK uom.Unit,
    MinPercentGood      decimal(5,2) DEFAULT 95,   -- нижче — статус Partial
    RefetchWithinDays   int  DEFAULT 7,            -- пізні дані PI
    IsActive bit, RowVersion rowversion
    UQ(TargetColumnDefId)

ext.RowWindowSource                    -- значення селектора → атрибут
    Id, RowWindowMapId FK, SelectorValue nvarchar(100) NULL,   -- NULL = для всіх рядків
    SourceEntityId FK ext.SourceEntity, SourceField nvarchar(200), SourceUnitId FK uom.Unit
    UQ(RowWindowMapId, SelectorValue)

ext.RowWindowValue                     -- провенанс кожного підтягування; партиція за PeriodKey
    PeriodKey int, Id bigint, PK(PeriodKey, Id),
    TableInstanceId bigint, RowKey nvarchar(100), ColumnDefId int, RowWindowMapId int,
    SourceEntityId int, SourceField nvarchar(200),
    FromUtc, ToUtc datetime2(3), Summary tinyint, ComputedBy tinyint,
    ValueSource decimal(34,16) NULL, SourceUnitSymbol nvarchar(64) NULL,
    ValueTarget decimal(34,16) NULL, TargetUnitId int, ConversionFactor decimal(34,16) NULL,
    PointCount int, PercentGood decimal(5,2) NULL,
    Status nvarchar(32),     -- Fetched | Partial | NoData | KeptManual | SourceError | InvalidWindow | NotApplicable
    ErrorCode varchar(32) NULL, RetrievedAt datetime2(3), IsCurrent bit
    IX(TableInstanceId, RowKey, ColumnDefId) WHERE IsCurrent = 1
```

**Виконання — `RowWindowFetchJob`** (`Ecr.Infrastructure/Jobs`):

| Тригер | Що робить |
|---|---|
| правка рядка, що зачепила Start/End/селектор (порт `IRowWindowTrigger`, одна точка виклику в `PatchCellsHandler`) — **і людиною, і синхронізацією подій** (§4.7): обидві пишуть тим самим обробником | ставить задачу на рядок; дедуплікація за `(TableInstanceId, RowKey)` |
| `RowWindowRefetchJob` щогодини | повтор для `NoData`/`Partial`/`SourceError` у межах `RefetchWithinDays` і для рядків, у яких `ToUtc > RetrievedAt` (вікно на момент читання ще не закрилося) |
| кнопка «Підтягнути з PI» (рядок/таблиця) | `POST /documents/{id}/row-windows/fetch` |

Кроки задачі:

1. Вікно: Start/End із комірок у **часовому поясі проєкту** (`Project.TimeZoneId`,
   `Project.cs:95`) → UTC. `End ≤ Start` чи вікно довше 32 діб — `InvalidWindow`, до PI не йдемо.
2. Джерело: значення селектора (код запису довідника чи текст формули `PiSourceKey`) →
   `RowWindowSource`. Джерела немає (пілот) — `NotApplicable`.
3. `ReadWindowAsync` → згортка → конверсія (§4.2).
4. Запис через `ICellPatcher.ApplyIntegrationAsync` (`IntegrationCellPatcher.cs:31-122`):
   origin `Integration`, ручна правка не перетирається — статус `KeptManual` (D-118).
5. `RowWindowValue` (попередній `IsCurrent = 0`), далі `ICalculationTrigger` (§4.5).

⚠ Подія належить екземпляру місяця свого **початку** за часом проєкту (V-10 → `D-179`).
Подія через північ 31-го числа не ділиться (HQ-13 закрито схваленням V-10).

### 4.5 Автоматичний перерахунок

**Рішення (V-5):** `ICalculationTrigger` (`Ecr.Application/Calculations/CalculationTrigger.cs`)
ставить `IRecalculationJob { ProjectId, DocumentId, PeriodKey }`. Задача вже приймає
документ і період (`RecalculationJob.cs:78-131`). Викликачі:

- `MaterializeCollectedDataJob` після `Applied > 0`;
- `RowWindowFetchJob` після зміни значення.

~~Дедуплікація: у черзі чи в роботі вже є перерахунок того самого `(DocumentId, PeriodKey)`
з міткою `auto` → нова задача не ставиться. Затримка 2 хв збирає пачку подій.~~
✎ 2026-09-30 (A4, як реалізовано): дублі прибирає **черга**, а не тригер. Тригер ставить
`IRecalculationJob` з тим самим маркером і ціллю `doc{id}-p{period}`
(`RecalculateDocumentHandler.TargetOf`), що й кнопка «Перерахувати» та правка шапки, тож на
пару «документ × період» лишається одна жива задача — і між авто- та ручним перерахунком теж.
Постановка — `EnqueueCoalescedAsync` (без витіснення): виконуваний перерахунок не
переривається, нова задача стає **позаду** нього (черга в базі) або зливається з наявною
(Quartz); `EnqueueExclusiveAsync` тут заборонено — він переривав би «гарячий» документ на
кожному записі. Лок документа `ecr:recalc:doc:{id}` бере сама задача, не тригер.
Мітки `auto` й затримки 2 хв немає. Чому: власна дедуплікація поруч із чергою розійшлася б
із її ключем (узгоджено з «Аналізом», власницею черги). Тригер кличуть **один раз на
прогін** викликача, не в циклі.
Закритий період, `Scheduled` і поданий аркуш — нуль задач (`RecalculationWritePolicy`;
гілка стану в `MaterializeCollectedDataJob`).
**Без прапорця:** без перерахунку нові дані однаково блокують подання
(`SubmitSheetHandler.cs:187-217`), тобто прапорець лише відкладав би ту саму дію.
Кнопка «Перерахувати» лишається для «застаріло» з інших причин (§8.8).

### 4.6 Якість точок

- `Quality ≠ Good` — точка не бере участі в згортці й інтегралі, а її інтервал іде в `Gaps`.
- Системні стани PI як текст (`I/O Timeout`) і далі лягають у `ValueString`
  (`PiSqlClientDataSource.cs:476-493`) і в число не перетворюються.

### 4.7 Джерело подій: PI Event Frames → рядки `FLARE_RECORD`

**Вихідне положення (відповідь людини 2026-09-27):** подія факелювання — початок і кінець,
категорія V6–V9 і сезон (кейс HMB Winter/Summer) — «це вже приходе з pi». У PI AF це Event
Frame (EF): запис із `StartTime`/`EndTime`, шаблоном, первинним елементом і атрибутами.
Чинне рішення вже читає EF через RTQP: `[Master].[EventFrame].[EventFrame]` ⋈ `[Attribute]`
⋈ `[AttributeCategory]` у в'юсі `EventFrameAttributes_V`, створеній у самій базі AF
(`B14-ecr-integration-and-data.md:30-40`). FLERT має власні RTQP-об'єкти подій факела
(`FlareEventFull_*`, `B20-source-configurator.md:101`), але їхніх визначень у репозиторії
немає.

**Рішення (V-16):** у ядрі — нейтральне поняття «подія джерела» (`SourceEvent`), а не «Event
Frame»: слова PI AF за межами адаптера й схеми `ext` не живуть
(`B06-integration-ports.md:28-32`). PI SQL Client — перша реалізація. FLERT
(`Ecr.Adapters.Sql`) чи PI Web API (`eventframes/search`) реалізують той самий метод
окремим кроком за потреби (F4b).

#### 4.7.1 Порт

```csharp
// Адитивно до IExternalDataSource (§4.3): чинні реалізації не змінюються.
public enum SourceEventAttributeScope : byte { Event = 0, PrimaryElement = 1 }

public sealed record SourceEventAttributeRef(string Name, SourceEventAttributeScope Scope);

public sealed record SourceEventQuery(
    int DataSourceId, int SourceEntityId, string Template,      // шаблон — Code сутності з каталогу
    DateTime FromUtc, DateTime ToUtc,                           // події, що ПЕРЕТИНАЮТЬ [from, to)
    IReadOnlyList<SourceEventAttributeRef> Attributes,          // лише ті, що є в мапінгу
    int MaxEvents);                                             // повний батч → Truncated

public sealed record SourceEventAttributeValue(
    string Name, SourceEventAttributeScope Scope,
    decimal? ValueNumeric, string? ValueString, string? SourceUnitSymbol);

public sealed record SourceEvent(
    string Id,                                   // ID EF — ключ синхронізації
    string? Name, string Template,
    DateTime StartUtc, DateTime? EndUtc,         // EndUtc = null — подія ще триває
    DateTime? ModifiedUtc, string? PrimaryElementPath,
    IReadOnlyList<SourceEventAttributeValue> Attributes);

public sealed record SourceEventResult(
    IReadOnlyList<SourceEvent> Events, bool Truncated, string? ErrorCode);

public sealed record SourceEventTemplate(
    string Template, IReadOnlyList<SourceEntityDescriptor> Attributes);   // UOM і тип — як у каталозі точок

public interface IExternalDataSource
{
    // … чинні члени, ReadWindowAsync (§4.3) …

    /// Каталог шаблонів подій і їхніх атрибутів — для конфігуратора.
    /// ⚠ Типова реалізація відмовляє ECR-INT-0422 (.eventQueryNotConfigured).
    Task<IReadOnlyList<SourceEventTemplate>> DiscoverEventTemplatesAsync(int dataSourceId, CancellationToken ct);

    /// Події шаблону, що перетинають вікно, з атрибутами мапінгу.
    Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct);
}
```

- Час — UTC: адаптер нормалізує його так само, як мітки точок (`PiSqlClientDataSource.cs:194`).
- Значення атрибута — число **або** текст, тим самим правилом, що точки
  (`PiSqlClientDataSource.cs:476-493`): `double` → `decimal` на межі (D-30).
- Обидва методи мають типову реалізацію-відмову, тож `PiWebApiDataSource`, `SqlDataSource` і
  тестові адаптери не змінюються.

#### 4.7.2 PI SQL Client (крок F4e)

| Що | Як |
|---|---|
| Запит подій | ключ `PiSqlClient:EventQuery`. **Типового тексту немає** (як V-3): імена RTQP-об'єктів подій факела в репозиторії не підтверджені — ні шаблон EF, ні його атрибути, ні в'юхи FLERT. Без ключа — `ECR-INT-0422` `err.ECR-INT-0422.eventQueryNotConfigured`, і вкладка «Події з PI» (§10.6) показує це станом, а не порожнім списком |
| Каталог шаблонів | ключ `PiSqlClient:EventTemplateQuery`, так само без типового тексту |
| Контракт нашого боку — жорсткий, як у FLERT (`Ts`, `Val`, `Uom`, `Quality`, `B14-ecr-integration-and-data.md:226-231`) | параметри `?` — від і до (вікно перетину напіввідкрите); заповнювач `{template}` — літералом через `Literal` (`PiSqlClientDataSource.cs:451-468`). Результат — **довга форма**, рядок на атрибут: `EventId`, `EventName`, `Template`, `StartTime`, `EndTime`, `Modified`, `PrimaryElement`, `AttrScope` (`E`/`P`), `AttrName`, `AttrValue`, `AttrUom`. Подія без атрибутів — рядок з `AttrName = NULL` |
| Звідки текст запиту | зразок — форма чинної в'юхи (`B14-ecr-integration-and-data.md:33-39`), але **не сама в'юха**: адаптер не створює артефактів у базі джерела (ER-I-01, `PiSqlClientDataSource.cs:220-222`). Текст звіряється з AVEVA PI SQL DAS (RTQP Engine) Reference тією ж процедурою, що `Q-197`, і лише тоді потрапляє в налаштування середовища |
| Атрибути елемента | `AttrScope = P` — атрибут первинного елемента EF, значення на момент початку події. Чи лежать категорія й сезон на самому EF, чи на елементі, — невідомо (HQ-18); запит, каталог і мапінг підтримують обидва |
| Стеля | `MaxEvents` = 2 000 на запит; повний батч → `Truncated = true`, і позначку «зникла» за цей прогін не отримує ніхто (§4.7.4, крок 6) |
| Повтори, автентифікація | ті самі `RetryAsync`, `ECR-INT-0502`, `ECR-INT-0503`, що в `ReadAsync` |

#### 4.7.3 Модель мапінгу (міграція M5, крок F9)

```sql
ext.SourceEventMap                    -- «шаблон подій джерела → динамічна таблиця документа»
    Id int PK,
    SourceEntityId  int    FK ext.SourceEntity,   -- сутність = шаблон подій (Code — з каталогу, не руками)
    DocumentId      bigint FK doc.Document,       -- документ ділянки (§5.1), куди лягають події
    TableDefId      int    FK cfg.TableDef,       -- лише динамічна таблиця
    FilterAttribute nvarchar(200) NULL, FilterScope tinyint NULL,
    FilterValue     nvarchar(400) NULL,           -- звуження: події лише цієї ділянки чи факела
    VolumeMode      tinyint,                      -- 0 None | 1 EventAttribute | 2 RowWindow (§4.7.5)
    IsActive bit, RowVersion rowversion,
    UQ(SourceEntityId, DocumentId, TableDefId)

ext.SourceEventFieldMap               -- «атрибут → колонка»
    Id int PK, SourceEventMapId int FK,
    TargetColumnDefId int FK cfg.ColumnDef,
    SourceAttribute   nvarchar(200),              -- ім'я з каталогу; зарезервовані: $start, $end, $name
    AttributeScope    tinyint,                    -- 0 Event | 1 PrimaryElement
    ValueKind         tinyint,                    -- 0 Direct | 1 LookupByCode | 2 LookupByName | 3 ValueMap
    SourceUnitId int NULL FK uom.Unit, TargetUnitId int NULL FK uom.Unit,
    UQ(SourceEventMapId, TargetColumnDefId)

ext.SourceEventValueMap               -- явна відповідність «значення джерела → запис довідника»
    Id int PK, SourceEventFieldMapId int FK,
    SourceValue nvarchar(400), RegistryEntryId int FK dic.RegistryEntry,
    UQ(SourceEventFieldMapId, SourceValue)

ext.SourceEventLink                   -- подія джерела ↔ рядок: провенанс і стан
    Id bigint PK, SourceEventMapId int FK, SourceEventId nvarchar(200),
    PeriodKey int NULL, TableInstanceId bigint NULL, RowKey nvarchar(100) NULL,
    EventName nvarchar(400) NULL, StartUtc datetime2(3), EndUtc datetime2(3) NULL,
    SourceModifiedUtc datetime2(3) NULL,
    Status nvarchar(32),   -- Synced | Open | Missing | PeriodClosed | PeriodChanged | PeriodNotOpen | Unmapped | RowLimit
    KeptManualJson nvarchar(max) NULL,            -- коди колонок, лишених за людиною
    UnmappedJson   nvarchar(max) NULL,            -- [{column, value}] без відповідника
    FirstSeenAt, LastSeenAt, LastSyncAt datetime2(3), RowVersion rowversion,
    UQ(SourceEventMapId, SourceEventId), IX(TableInstanceId, RowKey)
```

- Жодних зашитих імен: шаблон і атрибути обираються з каталогу
  (`DiscoverEventTemplatesAsync`), як атрибути точок (`SourceEntity.cs:10-15`, ФВ-13.13).
- `$start`/`$end` — час самої події; мапляться на Date-колонки `Start`/`End` і обов'язкові
  (`err.ECR-INT-0422.eventMapStartEndRequired`). У комірку лягає час **у поясі проєкту**
  (`Project.TimeZoneId`) — дзеркально до кроку 1 §4.4, тож вікно рядка повертає рівно UTC
  події.
- `ValueKind` для Lookup-колонки: за кодом запису, за назвою (без регістру, з обрізкою) або за
  явною таблицею `SourceEventValueMap`. Для каскадної колонки (`HmbCase` від `Stream`, §5.2)
  пошук обмежено записами батька, тож спершу резолвиться `Stream`.
- Одиниці числових атрибутів — через `BoundaryUnitConversion` (V-4) з тим самим журналом.
- `ext.EntityFieldMap` не змінюється: його фіксований адресат рядка (`EntityFieldMap.cs:113-128`)
  — інша семантика, ніж «подія сама є рядком».

#### 4.7.4 Синхронізація: `SourceEventSyncJob` (крок A5)

Запуск — за розкладом сутності-шаблону (`ext.CollectionSchedule`: Cron, `LookbackDays` 7,
`LastRunAt`, `ExternalConfiguration.cs:131-157`; `CollectionJob` після збору ставить цю задачу,
якщо в сутності є активний `SourceEventMap`) і кнопкою «Отримати з PI зараз»
(`POST …/source-events/sync`). Вікно — `[now − LookbackDays, now)`: пізні правки EF у PI
доходять самі, а Watermark лишається оптимізацією (ER-I-03, `CollectionJob.cs:48-51`).

1. **Читання.** `ReadEventsAsync` з атрибутами мапінгу; далі фільтр `FilterAttribute = FilterValue`.
2. **Ключ рядка.** `EF-` + ID події, якщо разом вони проходять `RowKey.Pattern`
   (`^[A-Za-z0-9_.\-]{1,100}$`, `RowKey.cs:14`; GUID RTQP проходить), інакше `EF-` + 32 hex
   SHA-256 від ID. Ключ детермінований: повтор не дублює рядків. Ручні рядки мають ключ-GUID
   без дефісів (`CreateRowHandler.cs:128-129`) і з ним не перетинаються.
3. **Відкрита подія** (`EndUtc = null`) — рядка ще немає, `Status = Open`. Без кінця немає ні
   тривалості, ні вікна об'єму, а напіврядок дав би розрахунку хибну тривалість.
4. **Період** — місяць початку за TZ проєкту (V-10 → `D-179`). Екземпляра ще немає —
   `PeriodNotOpen`, повтор наступним прогоном. Період закритий — **нічого не пишемо**,
   `PeriodClosed`. Для прив'язаної події, чий початок у PI переїхав в інший місяць, —
   `PeriodChanged` **без** автоматичного перенесення: видалити рядок документа в системі
   нічим, а рядок у двох місяцях подвоїв би викиди.
5. **Запис** — новим методом порту `ICellPatcher.ApplyIntegrationRowsAsync` (адитивно):
   типізовані значення (дата, Lookup, текст, число), `BaseVersion` чинного рядка для
   оновлення і `null` для нового (R-B2). Пише той самий `PatchCellsHandler` з
   `origin = Integration`, тобто з аудитом, валідацією, хуком `IRowWindowTrigger` (§4.4) і
   стелею `MaxDynamicRows` (`RowLimit`). Комірку, останню зміну якої зробила людина
   (`aud.CellChange.Origin = UserEdit`, `IntegrationCellPatcher.cs:137-174`), **не
   перезаписуємо**: вона йде в `KeptManualJson`, а реєстр показує «змінено вручну» (§10.4).
   Значення, для якого не знайшовся запис довідника, у комірку не пишеться — `Unmapped` із
   самим значенням у `UnmappedJson`; вгадування немає.
6. **Зникла подія.** Прив'язка, чий `StartUtc` лежить у вікні прогону, а подію джерело цього
   разу не повернуло, отримує `Status = Missing`; **рядок не видаляється й не змінюється**.
   Позначка ставиться лише після повного прочитання вікна (`Truncated = false`,
   `ErrorCode = null`). Подія повернулася — знову `Synced`.
7. **Далі** — `ICalculationTrigger` (V-5) на кожен зачеплений `(документ, період)`; прогрес
   `jobs.sourceEventsDone` з лічильниками `created`, `updated`, `keptManual`, `missing`,
   `open`, `unmapped`, `closed`.

Рядків **без** прив'язки (подія введена вручну) синхронізація не бачить і не чіпає ніколи.

**Як реалізовано (A5b, рішення людини 2026-09-30):**

- **Запуск.** `CollectionJob` для сутності з активним `SourceEventMap` і без мапінгів атрибутів точок збір точок
  не запускає (точок немає, збирач шукав би за кодом-шаблоном сирий тег) — ставить `ISourceEventSyncJob`
  (`EnqueueCoalescedAsync`, ціль `source-events-e{id}`), прогін розкладу фіксується без watermark; з мапінгами
  точок — синк ставиться після збору. Читання одне на сутність (`Template` — код сутності), мапінги (документи
  ділянок) ділять відповідь і її `Truncated`.
- **Кореневі.** Беруться лише події без `ParentId` (дочірні EF не звітні); `ParentId` лишається сирим полем події.
- **EFID — кеш.** Подія з новим ID, що збіглася з прив'язкою, якої джерело не повернуло, за природним ключем
  лягає в її рядок: `SourceEventLink.RekeyTo`, `RowKey` лишається `EF-<перший ID>`. Лише пара «один до одного»;
  дві однакові за ключем події не зіставляються з жодною. **Повний ключ (M6, `HSE301M6SourceEventKey`)** —
  мапінг (= шаблон) + початок до мс + первинний елемент (`ext.SourceEventLink.PrimaryElement`, Trim + UPPER,
  фільтрований `UX_SEL_NaturalKey`). Порядок: 1) повний ключ; 2) для решти — слабкий (початок + назва без
  регістру) лише проти зв'язків БЕЗ елемента (записані до M6) чи для подій без елемента; зв'язок з ІНШИМ
  елементом слабким ключем не зіставляється. Елемент заповнюється м'яко — першим `Synced`/спостереженням
  (`SourceEventPlanItem.ElementToStore`), не міграцією; неоднозначний ключ (дві події одного прогону чи інший
  зв'язок із тим самим «початок + елемент») не записується, щоб не порушити індекс.
- **Період** — за межами періодів проєкту (`Period.UtcBounds`), не за UTC-датою: 19:00Z 31 січня в `Asia/Atyrau`
  (+05:00) — лютий. `Scheduled` і відсутній екземпляр — `PeriodNotOpen`; `Closed` — `PeriodClosed` (нуль записів).
- **Незакрита** — кінець `NULL` або ≥ `9999-01-01` (сторожова дата PI AF); з рядком (PI знову показує відкритою)
  — прив'язка й рядок лишаються як є.
- **Запис** — групами по екземпляру таблиці; пакет відхилено (`dynamicRowLimit`, валідація) — по одному рядку:
  стеля → `RowLimit`, решта відмов — подія покриття `SkippedWriteConflict`, а не падіння прогону. Прив'язка
  лягає лише для рядка, який існує після запису. `KeptManual` → `KeptManualJson`, `Rejected` патчера і значення
  без відповідника → `UnmappedJson`. Спершу весь запис, потім усі зміни прив'язок одним збереженням.
- **Автоперерахунок** — `ICalculationTrigger` один раз на зачеплений період за прогін мапінгу, лише за `Applied > 0`.
- **Прогрес** — `jobs.sourceEventsDone` (`created`, `updated`, `keptManual`, `missing`, `open`, `unmapped`,
  `closed`, `pending` = не записано й чекає: `PeriodNotOpen`, `PeriodChanged`, `RowLimit`, відмови).
- **Каскадні Lookup** (`HmbCase` від `Stream`) шукаються серед усіх записів довідника колонки; двозначна назва —
  незіставлена. Обмеження записами батька — окремий крок.

**✎ 2026-09-30, звірка тексту кроків 1–7 з кодом** (`dev/integration`, після `4b609f1b`). Де текст вище
розходиться з кодом, чинний код:

- **Кнопки «Отримати з PI зараз» і `POST …/source-events/sync` немає.** Кроки A6 і B8 (§11.2) не
  виконано: у `src/` немає маршрутів `source-event-maps`, `probe-events`, `event-templates`,
  `source-events`. Ручний запуск — чинний `POST /api/v1/sources/{id}/collect` (`SourcesController.cs:135`):
  `CollectionJob` ставить синк сам. Мапінг `ext.SourceEventMap` через API чи UI не заводиться — лише
  прямим записом у БД; без мапінгу задача завершується з `jobs.sourceEventsNoMaps` і нічого не читає.
- **Вікно:** `FromUtc` із завдання, інакше `ToUtc − LookbackDays` **увімкненого** розкладу сутності; розкладу
  немає — 7 днів (`SourceEventSyncJob.DefaultLookbackDays`). `ToUtc` — із завдання, інакше «зараз».
- **Крок 2 (ключ)** — як у тексті: `IntegrationRowUpsert.EventRowKey` (`ICellPatcher.cs:97-109`). Для
  перествореної події з новим ID ключ рядка лишається ключем першого ID (EFID — кеш, див. вище).
- **Крок 3 (відкрита подія)** — зв'язок зі статусом `Open` лягає, рядок — ні; так само `PeriodNotOpen`,
  `PeriodClosed`, `RowLimit` — статуси зв'язку без рядка (`SourceEventSyncJob.Unwritten`).
- **Крок 4 (період)** — межі періоду проєкту (`SourceEventPeriods`, `Period.UtcBounds`), а не «місяць за
  TZ»: для місячної політики це те саме, для інших політик — межі самого періоду.
- **Крок 7** — перерахунок один раз на зачеплений період **мапінгу** (мапінг = документ ділянки), лише за
  `Applied > 0`; лічильник `pending` додано до переліку з тексту.
- ⚠ **Не підтверджено кодом:** поведінка на живому RTQP (тексти `PiSqlClient:EventQuery`/`EventTemplateQuery`
  типового значення не мають, живий PI не перевірявся) і реальна ієрархія Flare-EF — відкриті питання
  PI-адміністратору (TESTER-GUIDE §7.3).

#### 4.7.5 Об'єм: два режими, налаштовуються на мапінгу

| `VolumeMode` | Звідки | Коли обирати |
|---|---|---|
| `EventAttribute` | атрибут EF → `Volume_Sm3` через `SourceEventFieldMap` з конверсією одиниць (V-4) | PI зберігає об'єм події атрибутом EF |
| `RowWindow` | прив'язка «атрибут витрати → колонка, вікно = рядок» (V-2, §4.4): синхронізація пише Start/End, хук `IRowWindowTrigger` ставить `RowWindowFetchJob`, той читає Total за вікном | в EF об'єму немає, а тег витрати факела є |
| `None` | об'єм уводиться руками | перехідний стан |

Режим — дані, не код: перемикання не потребує релізу. Провенанс (§7.3) каже, звідки число:
«атрибут події PI» чи «PI Total за вікном».

#### 4.7.6 Сезон і категорія — атрибути події

- Категорія V6–V9 — Lookup `TugfCategory` (§5.3) з атрибута EF, за кодом або явною
  відповідністю.
- Сезон — атрибут EF. Мапінг задає, **що** в ньому лежить: повна назва кейсу («370 Winter»)
  лягає в `HmbCase` за назвою; лише сезон («Winter», «Summer», «зима») — у прихований Lookup
  `Season`, а `HmbCase` резолвиться як кейс потоку з цим сезоном (поле `Season` довідника
  `HmbCase`; після RG1 — `STREAM_CASE.SEASON`, FEATURE-REGISTRY-TABLES §11.4).
- ⛔ Правила «сезон за місяцем» немає ніде: ні у формулах, ні в дефолтах, ні в підказках.
  Сезон, якого PI не дав, — порожня комірка й `Unmapped`, а не здогадка за датою.

#### 4.7.7 Ручне введення лишається

- Подія поза PI — «Нова подія» (§10.4): рядок без прив'язки.
- Виправлення поля, що прийшло з PI, — явна дія «Виправити вручну» (§10.4). Правку захищає
  D-118, і наступна синхронізація її не перетирає. «Повернути значення з PI» записує поточне
  значення події з `origin = Integration`, і поле знову веде синхронізація.
- Подія, що зникла з PI чи виявилася дублем, — колонка «Не враховувати» (`Exclude`, §5.2):
  рядок лишається в реєстрі й аудиті, а методологія дає по ньому нулі (V-21).

---

## 5. Модель шаблону 301

### 5.1 Документ і періоди

- Шаблон `HSE301`, проєкт із помісячними періодами (`PeriodKind.Monthly`), таблиці
  `PerPeriodInstance`. Один документ на ділянку (`Offshore · Island A`): документ періоду
  не має (`Document.cs:24-42`), екземпляри таблиць — на кожен місяць.
- Річний вид — це 12 місячних екземплярів разом, і рахує його представлення (§8.8), а не таблиця.

### 5.2 Таблиця `FLARE_RECORD` — події й безперервні джерела разом

**Рішення (V-9):** одна динамічна таблиця для подій і безперервних записів. Аркуш
`AI_Cont_FG_V7` має ту саму шапку 1–16, що й аркуші подій: початок, кінець, тривалість
(розбір §1.2). Безперервний запис — це «подія довжиною в місяць». Звідси одна методологія,
одна прив'язка PI й одне представлення-фільтр.

Аркуш `EVENTS`, таблиця `FLARE_RECORD`: `RowMode = Dynamic`, `MaxDynamicRows = 500`.

«PI (EF)» у колонці «Звідки» — атрибут чи час події, **якщо** адміністратор зіставив його в
мапінгу (§4.7.3); незіставлене поле рядка з PI людина вводить сама. Будь-яке поле з PI можна
виправити вручну (§4.7.7).

| Код | Заголовок en / ru | Тип | Звідки | Примітка |
|---|---|---|---|---|
| `SourceEventId` | — | String | PI (EF), лише для читання | прихована; ID події для провенансу, авторитетна копія — `ext.SourceEventLink` |
| `RecordKind` | Record kind / Вид записи | Lookup `FlareRecordKind` | PI (EF) або ввід | Event / Pilot / Purge |
| `IsPilot` | — | Formula `REGFIELD([RecordKind], 'IsPilot')` | формула | прихована; 1/0 для `@IsPilot` (Д-6) |
| `Regime` | Regime / Режим | Formula `REGFIELD([RecordKind], 'Regime')` | формула | `Intermittent`/`Continuous` |
| `Region` | Offshore / На море | Lookup `Region` | PI (EF) або ввід, типово Offshore | |
| `Area` | Area / Район работ | Lookup `Area` | PI (EF) або ввід, типово Island A | |
| `Start` | Start / Начало | Date (з часом) | PI (EF, `$start`) або ввід | до секунди, у поясі проєкту |
| `End` | End / Окончание | Date (з часом) | PI (EF, `$end`) або ввід | виключно; подія без кінця не матеріалізується (§4.7.4) |
| `DurationSec` | Duration, s / Длительность, с | Formula `ROUND(([End] - [Start]) * 86400, 0)` | формула | одиниця `s` |
| `Flare` | Flare unit / Тип факельной установки | Lookup `FlareUnit` | PI (EF) або ввід | HP / LP / HP Pilot, Island A |
| `FlareSide` | — | Formula `REGFIELD([Flare], 'Side')` | формула | HP/LP для зведень (HP Pilot → HP) |
| `TipArea_m2` | — | Formula `REGFIELD([Flare], 'TipArea_m2')` | формула | прихована; площа оголовка (HQ-6) |
| `SourceUnit` | Type of unit – source of discharge | Lookup `ProcessUnit` | PI (EF) або ввід | |
| `Description` | Description how the release occurred | String | PI (EF, `$name` чи атрибут) або ввід | |
| `Category` | TUGF category / Категория ТНС | Lookup `TugfCategory` | **PI (EF)**, за кодом або відповідністю; ввід — для подій поза PI | V6…V9 («це вже приходе з pi») |
| `GasType` | Type of gas / Тип газа | Lookup `FlareGasType` | PI (EF) або ввід | FG / SG |
| `Nitrogen` | Nitrogen event / Азот | Lookup `YesNo` | PI (EF) або ввід, типово No | |
| `IsN2` | — | Formula `REGFIELD([Nitrogen], 'Flag')` | формула | прихована; 1/0 |
| `Exclude` | Do not count / Не учитывать | Lookup `YesNo` | ввід, типово No | ставить людина для події, що зникла з PI чи виявилася дублем (V-21) |
| `IsExcluded` | — | Formula `REGFIELD([Exclude], 'Flag')` | формула | прихована; 1/0 для `@IsExcluded` |
| `Stream` | № Stream / № потока | Lookup `FlaringStream` | PI (EF) або ввід (SG) | |
| `Season` | — | Lookup `Season` (Winter / Summer) | **PI (EF)**, якщо атрибут несе лише сезон | прихована; обирає кейс потоку (§4.7.6) |
| `HmbCase` | What is the HMB / Какой принят МТБ | Lookup `HmbCase`, каскад від `Stream` (`ColumnDef.CascadeFromColumnId`, `ColumnDef.cs:42-43`) | **PI (EF)**: повна назва кейсу або кейс потоку за `Season`; ввід — для подій поза PI | ⚠ на кроці I1 перевірити, що каскад фільтрує кейси потоку; якщо ні — одна Lookup-колонка «потік · кейс». Сезон за місяцем не виводиться ніде |
| `PiSourceKey` | — | Formula `REGFIELD([RecordKind], 'Code') & '/' & REGFIELD([Flare], 'Code')` | формула | прихована; селектор прив'язки PI (§4.4) |
| `Volume_Sm3` | Gas Volume, Sm3 (20°C, 1 atm) | Decimal, `Precision 28`, **`Scale 6`**, `DisplayFormat 0.000`, одиниця `Sm3` | **PI**: атрибут EF або Total за вікном рядка (`VolumeMode`, §4.7.5); ввід | Д-7: `Scale 3` відхилив би інтеграл |
| `Rho20` | Density, kg/Sm3 | Decimal, `Scale 4` | ввід/імпорт (V1) | → RG1 |
| `Mu` | Molecular weight | Decimal, `Scale 7` | ввід/імпорт (V1) | → RG1 |
| `S_wt` | Total sulphur content, wt% | Decimal, `Scale 7` | ввід/імпорт (V1) | → RG1 |
| `T0_C` | Initial temperature To, °C | Decimal, `Scale 6`, одиниця `degC` | ввід/імпорт (V1) | → RG1 |
| `H2S_wt`, `C4H10S_wt`, `CH4S_wt`, `C3H8S_wt`, `C2H6S_wt` | wt% сполук | Decimal, `Scale 7` | ввід/імпорт (V1) | → RG1 |
| `LHV_MJ_Sm3` | Fuel lower heating value, MJ/Sm3 | Decimal, `Scale 4` | ввід/імпорт (V1) | → RG1 |
| `EF_t_t` | GHG Emission factor, tCO2/t | Decimal, `Scale 3` | ввід/імпорт (V1) | у файлі обрізано до 3 знаків (розбір §1.4 р.17) |
| `M_t` | Volume of flared gas, tonne | Calculated → `HSE301.FLARE:M_t` | методологія | прив'язка до проміжного (§7.1) |

⚠ **Колонки складу (`Rho20` … `EF_t_t`) — тимчасові (V1).** Методологія читає їх як `@Arg`,
тож золоті приклади A/B/C проходять без довідників. Після FEATURE-REGISTRY-TABLES
методологія v1.1 бере ці властивості для SG зі складу потоку за парою (`Stream`, `HmbCase`), а
для FG вони й далі вводяться з лабораторного аналізу до кроку RG3 (§6.5). Числа не змінюються —
це і є DoD кроку RG1.

### 5.3 Довідники з одним ключем (є вже зараз)

Lookup потрібен для зручного вводу, а числа для методології з нього дістає формула шаблону
`REGFIELD` (`FunctionRegistry.cs:59-67`, `SliceEvaluationContext.cs:267-271`). Так Д-6
обходиться без правки `CalculationInputBuilder`: у `@Arg` доходить число з
`Formula`-колонки, а трейс показує ланцюжок «колонка → формула → поле запису».

| Довідник | Поля | Записи |
|---|---|---|
| `FlareRecordKind` | `IsPilot` (0/1), `Regime` (text), `Code` | Event, Pilot, Purge |
| `FlareUnit` | `Side` (HP/LP), `TipArea_m2`, `Code` | HP Flare Island A (0.200256265), HP Pilot (0.00092), LP Flare (HQ-6) |
| `TugfCategory` | — | V6, V7, V8, V9 |
| `FlareGasType` | — | FG, SG |
| `YesNo` | `Flag` (0/1) | Yes, No |
| `Season` | — | Winter, Summer (псевдоніми значень PI — у `ext.SourceEventValueMap`, §4.7.3) |
| `ProcessUnit`, `Area`, `Region` | — | з 301 |
| `FlaringStream`, `HmbCase` | `HmbCase`: `Season` (Lookup `Season`) | V1 — для показу, ключа й вибору кейсу за сезоном з PI (§4.7.6); склад — FEATURE-REGISTRY-TABLES: у шаблоні v2 (крок RG1) їх замінюють довідники `STREAM` і `STREAM_CASE` (FEATURE-REGISTRY-TABLES §11.4) |

✓ `REGFIELD` у правилах валідації мав дефект — [DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md),
виконано в `5f00b791` для «Перевірити» й подання аркуша і в `400c58b1` для шляху збереження
(`PatchCellsHandler` будує знімок через `TableValidation.LoadRegistryFieldsAsync`,
`PatchCellsHandler.cs:151-152`). Тут `REGFIELD` і далі використовується лише у формулах
колонок; правила §5.7 його не вживають.

### 5.4 Безперервні джерела

- **Pilot:** запис `RecordKind = Pilot`, вікно = місяць (`Start` — 1-ше 00:00, `End` —
  1-ше наступного місяця 00:00). Об'єм рахує методологія: `CST.PILOT_RATE_SM3H × годин вікна`
  (розбір §1.5 B: 744 год → 7 216.8). Прив'язки PI немає — статус `NotApplicable`.
- **Purge:** запис `RecordKind = Purge`, вікно = місяць, об'єм — PI Total за вікном
  (`PiSourceKey = PURGE/HP_IA`; атрибут витрати адміністратор обирає з каталогу, §10.6).
- Дія «Додати безперервні джерела за місяць» (§10.4) створює обидва записи ідемпотентно.
  Якщо PI веде продувку місячними Event Frames, вона приходить тим самим мапінгом подій
  (`RecordKind` з атрибута, §4.7), а дія лишається для пілота, якого в PI немає.
- ⚠ **Неповний місяць** (вересень до 25.09, розбір §1.3): вікно запису = фактичні дані;
  г/с на аркуші запису = т/тривалість вікна (як у файлі), а в зведенні — т/години
  **календарного** місяця (`perPeriodRate`, §8.7). Чи правильно це — HQ-8.

### 5.5 Лабораторний аналіз паливного газу

V1: властивості місячного аналізу (ρ, μ, S, wt% сполук, LHV) вводяться в записи FG
(Pilot/Purge/події FG) тими самими колонками §5.2 — у файлі `AI_Cont_FG_V7` вони теж
стоять у стовпці кожного місяця. Перерахунок мг/м3 → об.% (22.414, N2 балансом) —
крок RG3 після FEATURE-REGISTRY-TABLES.

### 5.6 Ліміти дозволів

`102.6 ст.м3/с` і `35 398.34316651 г/с` — константи методології з вікнами чинності,
вирівняними на дати дозволів: KZ14VCZ14622113 — 01.01–01.09.2026, KZ50VCZ14912853 —
01.09.2026–01.01.2027 (розбір §1.1). Константа резолвиться за кінцем періоду
(`GenericCalculationModule.cs:336`), і зміна 01.09 припадає рівно на межу місяця. Значення
після 01.09 — HQ-5; нейтральний дефолт — те саме число.

### 5.7 Правила валідації таблиці

| Правило | Рівень |
|---|---|
| `[End] > [Start]` | Error |
| `[DurationSec] <= 2764800` (32 доби) | Error |
| `[GasType]` = SG → `Stream`, `HmbCase`, `S_wt`, `Rho20`, `Mu` заповнені | Error (через `MethodologyRequiredInput`, Block) |
| `Volume_Sm3` порожній у записі не-Pilot | Warning (`MethodologyRequiredInput`, Warn) |
| `Category` порожня (значення з PI не зіставилося — `Unmapped`) | Warning: подія не потрапить у жоден аркуш представлення, фільтри яких ідуть за категорією (§8.5) |

---

## 6. Методологія `HSE301.FLARE`

### 6.1 Модель

- **Одна** методологія `HSE301.FLARE`, `Kind = DataDriven`, версія `1.0.0`,
  `NumericMode = Strict`, `CalendarMode` — календарний, правило відбору `{}` (уся таблиця
  `FLARE_RECORD`).
- 11 речовин (`calc.MethodologySubstance`): NO2, NO, Soot, SO2, H2S, CO, CH4, C4H10S,
  CH4S, C3H8S, C2H6S. ⚠ У файлі пропілмеркаптан записано `C5H8S` (розбір §1.8); у довіднику
  речовин — `C3H8S`, а підпис рядка представлення повторює файл лише за рішенням замовника.
- **Рішення (V-7): область формули `Scope = Row | Substance`.** Сьогодні модуль рахує
  **всі** формули й пише **всі** виходи на кожну речовину (`GenericCalculationModule.cs:155-219`).
  Тоді `M_t` і парникові гази лягли б у `calc.CalculationResult` 11 разів. Нові поля:
  `MethodologyFormula.Scope` (типово `Substance`, тобто поведінка чинних методологій не
  змінюється) і `MethodologyOutput.IsPerSubstance` (типово `true`). Row-формули
  рахуються один раз до циклу речовин і видимі в ньому як `!Code`.
  Перевірка публікації: Row-формула не може посилатися (навіть транзитивно) на
  Substance-формулу чи константу, задану по речовинах — `ECR-CALC-0422`
  `err.ECR-CALC-0422.rowScopeReferencesSubstance`.

### 6.2 Константи (`CST.*`, версіоновані, з вікнами чинності)

| Код | Значення | Одиниця | Речовина | Джерело (`MethodologyConstant.Source`) |
|---|---|---|---|---|
| `PILOT_RATE_SM3H` | 9.7 | Sm3/h | — | розбір §1.5 B; робоче припущення HQ-1 (у PI пілот не вимірюється) |
| `K_MASS` | 0.0024 / 0.00039 / 0.002 / 0.02 / 0.0005 | t/t | NO2 / NO / Soot / CO / CH4 | 0.003·0.8, 0.003·0.13 (розбір §1.4 р.2–6); HQ-2 |
| `K_MASS` | 0 | t/t | SO2, H2S, 4 RSH | ⚠ **нуль задається явно**: відсутня константа дає `#REF` (`MethodologyEvaluationContext.cs:81-84`) |
| `K_S` | 0.02 (SO2) · 0 (решта) | t/(t·wt%) | усі | 2·(64/32)/100 (розбір §1.4 р.7) |
| `ETA` | 0.9984 | one | — | повнота згоряння |
| `SEL_H2S`, `SEL_C4H10S`, `SEL_CH4S`, `SEL_C3H8S`, `SEL_C2H6S` | 1 для «своєї» речовини, 0 для решти | one | усі | селектор wt% (V-11) |
| `K_WSND` | 91.5 | — | — | розбір §1.4 р.11 |
| `K_ADIAB` | 1.3 | one | — | k |
| `T_ABS0` | 273 | K | — | ⚠ у файлі 273, не 273.15 |
| `FLARE_CAP_SM3S` | 102.6 | Sm3/s | — | HQ-5; вікна = дозволи |
| `SO2_LIMIT_GS` | 35398.34316651 | g/s | — | HQ-5; вікна = дозволи |
| `OX_CO2` | 0.995 | one | — | HQ-2 |
| `EF_CH4_KG_TJ` | 1 | kg/TJ | — | розбір §1.4 р.19 |
| `EF_N2O_KG_TJ` | 0.1 | kg/TJ | — | розбір §1.4 р.20 |
| `M_CO2` | **44.00** | g/mol | — | для EF у v1.1 (RG1). ⚠ Не 44.01: лише `Round(K·Σ(nC·x)/100/μ, 3)` з K ∈ [43.9987; 44.0018) відтворює EF файлу на всіх 11 різних складах (3 SG + 8 місяців FG), §6.5 |
| `M_S` | **32.064** | g/mol | — | для S wt% у v1.1 (RG1). ⚠ Не 32.06: з 32.064 S мас.% SG відтворюється до 1e-14 на всіх трьох складах, з 32.06 — з відносною похибкою −1.25e-4 (§6.5) |
| `M_S_LAB` | 32.06 | g/mol | — | для «мгS/м3 → об.%» лабораторії FG у RG3 (розбір §1.4 р.24; повторно не перевірялося) |
| `V_MOLAR_0`, `V_MOLAR_20` | 22.414 / 24.04 | l/mol | — | для RG3 |

**Рішення (V-11): селектор-константи.** Вираз не вміє обрати `@Arg` за кодом поточної
речовини: `SUBSTANCE(code)` читає результат формули, а не аргумент
(`MethodologyFunctions.cs:197-221`). Тому wt% «своєї» сполуки обирається сумою
`SEL_x · @x_wt`. Це громіздко, але явно: трейс показує, яка константа дала одиницю.
FEATURE-REGISTRY-TABLES §5.4 функції «поточна речовина» не дає, тож у RG1 прийом
**лишається**: змінюються лише джерела wt% (`@x_wt` → формули `!x_wt` зі складу, §6.5).

### 6.3 Формули (діалект Methodology; функції — з урахуванням регістру, `DialectCatalog.cs:63-112`)

| # | Код | Scope | Видимий | Вираз | Одиниця |
|---|---|---|---|---|---|
| 1 | `V_Sm3` | Row | ✓ | `if(@IsExcluded = 1, 0, if(@IsPilot = 1, CST.PILOT_RATE_SM3H * CONVERT(@DurationSec, 's', 'h'), @Volume_Sm3))` | Sm3 |
| 2 | `M_t` | Row | ✓ (вихід) | `CONVERT(!V_Sm3 * @Rho20, 'kg', 't')` | t |
| 3 | `NCV` | Row | ✓ | `@LHV_MJ_Sm3 / @Rho20` | MJ/kg (= TJ/тис. т) |
| 4 | `W_SRC` | Row | ✓ | `!V_Sm3 / (@DurationSec * @TipArea_m2)` | m/s |
| 5 | `W_SND` | Row | ✓ | `CST.K_WSND * Sqrt(CST.K_ADIAB * (CST.T_ABS0 + @T0_C) / @Mu)` | m/s |
| 6 | `W_RATIO` | Row | ✓ | `!W_SRC / !W_SND` | one |
| 7 | `V_LIM` | Row | ✓ | `CST.FLARE_CAP_SM3S * @DurationSec` | Sm3 |
| 8 | `SO2_LIM_GS` | Row | ✓ | `CST.SO2_LIMIT_GS` | g/s |
| 9 | `CO2_GHG_t` | Row | вихід | `!M_t * @EF_t_t * CST.OX_CO2` | t |
| 10 | `CH4_GHG_t` | Row | вихід | `CONVERT(CONVERT(!M_t, 't', 'kt') * !NCV * CST.EF_CH4_KG_TJ, 'kg', 't')` | t |
| 11 | `N2O_GHG_t` | Row | вихід | `CONVERT(CONVERT(!M_t, 't', 'kt') * !NCV * CST.EF_N2O_KG_TJ, 'kg', 't')` | t |
| 12 | `W_COMP` | Substance | — | `CST.SEL_H2S * @H2S_wt + CST.SEL_C4H10S * @C4H10S_wt + CST.SEL_CH4S * @CH4S_wt + CST.SEL_C3H8S * @C3H8S_wt + CST.SEL_C2H6S * @C2H6S_wt` | wt% |
| 13 | `tons` | Substance | вихід | `if(@IsN2 = 1, 0, !M_t * (CST.K_MASS + CST.K_S * @S_wt * CST.ETA + (1 - CST.ETA) * !W_COMP / 100))` | t |
| 14 | `gsec` | Substance | вихід | `CONVERT(!tons, 't', 'g') / @DurationSec` | g/s |

- ⛔ Множників `/1000` і `·10⁶` у формулах немає: зміна одиниці — лише `CONVERT` (D-74).
- `@IsExcluded = 1` («Не враховувати», §5.2) дає `V_Sm3 = 0`, тож усі виходи рядка — нулі, як
  в азотної події; рядок і його аудит лишаються (V-21).
- ⚠ Для FG парникові гази теж рахуються, але представлення FG їх не показує: у файлі цих рядків
  у FG немає (розбір §1.2).
- Прапорець «Анализ: превышение макс. расхода» — правило представлення (§8.5), бо його
  визначення не підтверджене (HQ-11). Якщо замовник назве його показником звітності, він
  стає формулою №15.

**Перевірка на прикладі A (розбір §1.5):** `M_t` = 269.258 · 0.9589 = 258.1914962 кг =
**0.2581914962 т**; `tons[SO2]` = 0.2581914962 · (0 + 0.02 · 17.2965447 · 0.9984 + 0) =
**0.0891735** т; `gsec[SO2]` = 89 173.5 г / 930 с = **95.885** г/с. **B:** `V_Sm3` = 9.7 ·
744 = 7 216.8; `M_t` = 5.50714 т; `tons[CO]` = 0.02 · 5.50714 = **0.1101428** т;
`gsec[CO]` = 110 142.8 / 2 678 400 = **0.041123** г/с. **C:** `CO2_GHG_t` = 0.2581915 · 2.087 ·
0.995 = **0.5361514** т; `CH4_GHG_t` = **9.7044e-6** т.

### 6.4 Що змінюється в рушії

| Зміна | Файл | Чому |
|---|---|---|
| `Scope` формули, `IsPerSubstance` виходу | `MethodologyFormula.cs`, `MethodologyOutput.cs`, `GenericCalculationModule.cs` | V-7 |
| `IsVisible` формули → результат `Kind = Intermediate` | `MethodologyFormula.cs`, `CalculationResult.cs`, `CalculationResultStore.cs` | §7.1 |
| прив'язка колонки до видимої формули, а не лише до виходу | `MethodologyAuthoringHandlers.cs` (перевірка `OutputCode`) | `M_t` у сітці |
| перевірка публікації: Row не посилається на Substance | `PublishMethodologyHandler.cs` | V-7 |
| трейс зі входами | `TraceRecorder.cs`, `ReferenceCollector.cs` (новий) | §7.2 |

### 6.5 Після FEATURE-REGISTRY-TABLES (крок RG1)

Синтаксис — [FEATURE-REGISTRY-TABLES §5](FEATURE-REGISTRY-TABLES.md#5-мова-виразів): функції
`REGFIND`, `REGSUM`, `REGFIELD` ([§5.4](FEATURE-REGISTRY-TABLES.md#54-функції)), поля рядка
`ROW.…`, довідники `STREAM` → `STREAM_CASE` → `GAS_COMPOSITION` і `COMPONENT` з полями за
[§11.4](FEATURE-REGISTRY-TABLES.md#114-приклад-налаштування-потоки-flert) того документа; ті самі
формули у вигляді прикладу — його [§5.6](FEATURE-REGISTRY-TABLES.md#56-приклади-на-формулах-форми-301).
Версія `1.1.0` методології `HSE301.FLARE` (`Strict`): Row-формули (V-7) нижче замінюють `@Arg`
колонок складу, а решта формул §6.3 не змінюється, окрім `@X` → `!X`.

```text
CASE       = REGFIND('STREAM_CASE', @Stream, @HmbCase)    // первинний ключ (STREAM, CASE_NAME); @Stream — Lookup → STREAM
Mu         = REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.MW) / 100
S_wt       = CST.M_S * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_S * ROW.MOL_PCT) / !Mu
H2S_wt     = REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE && ROW.COMPONENT = REGFIND('COMPONENT', 'H2S'),
                    ROW.MOL_PCT * ROW.COMPONENT.MW) / !Mu
C4H10S_wt  = REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE && ROW.COMPONENT.FORMULA = 'C4H10S',
                    ROW.MOL_PCT * ROW.COMPONENT.MW) / !Mu        // ізомери сумуються (розбір §1.4 р.24)
// CH4S_wt, C3H8S_wt, C2H6S_wt — те саме з 'CH4S', 'C3H8S', 'C2H6S'
T0_C       = REGFIELD(!CASE, 'T_C')
LHV_MJ_Sm3 = REGFIELD(!CASE, 'LHV_STD')                          // LHV суміші — поле кейсу (див. нижче)
NCV        = !LHV_MJ_Sm3 / @Rho20                                // формула №3 §6.3
EF_RAW     = CST.M_CO2 * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_C * ROW.MOL_PCT) / 100 / !Mu
EF_t_t     = Round(!EF_RAW, 3)                                   // округлення, НЕ обрізання (див. нижче)
```

**Звідки властивості компонентів (відповідь людини «це і є addstream», перевірено на файлах
2026-09-27).** Детально — FEATURE-REGISTRY-TABLES §11.4 і `R-27`.

| Властивість | Що фактично є | Звідки в `COMPONENT` |
|---|---|---|
| `N_C`, `N_S` | прямо не записані; **виводяться з хімічної формули** в підписах Add Stream `streams Onshore!A10:A54` (і тих самих у `TOx!A9:A53`): «Methane (CH4)…», «Butyl mercaptan (C4H10S)…» — 42 з 45 рядків. Без формули — три псевдокомпоненти HYSYS `CN1_35*`, `CN2_35*`, `CN3_16*` | імпорт профілем `ADDSTREAM_COMPONENTS`. Перевірка на 301: `S = 32.064·Σ(n_S·x)/μ` відтворює S мас.% до 1e-14 на трьох складах; EF (нижче) — на 11 складах, з `N_C = 0` у псевдокомпонентів (на 1D-1/370 Summer з 4.1 об.% псевдокомпонентів будь-яке `N_C ≥ 1` дало б EF ≥ 1.887 замість 1.85) |
| `MW` | в Add Stream **немає** молярних мас компонентів: `Current_20250805` має лише `Molecular_Weight` кейсу (HYSYS, 23.05), `streams Onshore` — `Molecular Weight` фаз потоку (рядки 58, 64, 78). Маси з формул (атомні маси IUPAC) μ файлу **не** відтворюють: 23.0572 проти 23.0544679 — файл рахує масами бібліотеки HYSYS, зокрема гіпотетичних компонентів (`IC5_18*` 71.76, `C6_21*` 85.36, `CN1_35*` 230.85) | значення бібліотеки HYSYS, **відновлені з самого 301**: `M_i = wt%_i · μ / об.%_i` (`AI_Int_SG_V8`, рядки 30–63 і 66–99) для 33 із 34 компонентів моря; з ними μ збігається з файлом до 2e-14 на всіх трьох складах. Решта компонентів (суша й `TEG`) — з формули до підтвердження |
| `LHV` | в Add Stream **немає** ні LHV компонентів, ні LHV суміші (пошук по всіх 13 аркушах). У 301 є лише LHV суміші: 36.0412787449075 (1D-2/370 Winter), 37.0428156330934 (1D-2/370 Summer), 56.7489145967575 (1D-1/370 Summer), рядок 101 аркушів SG, і щомісячний LHV FG (рядок 72). Відновити LHV компонентів із трьох сумішей неможливо (34 невідомі) | нейтральний дефолт: `LHV` у `COMPONENT` — необов'язкове поле; LHV суміші — необов'язкове поле кейсу `STREAM_CASE.LHV_STD`, його вводить чи імпортує методолог. Коли `COMPONENT.LHV` заповнять, методологія v1.2 може перейти на `REGSUM(… ROW.MOL_PCT * ROW.COMPONENT.LHV) / 100` без зміни моделі. **Відкрите лише одне:** з якого джерела замовник бере LHV (RQ-2a FEATURE-REGISTRY-TABLES) |

**EF — округлення, а не обрізання (перевірено 2026-09-27).** Розбір §1.4 р.17 припускав
`Truncate(44.01·…)`. На 11 різних складах файлу (3 SG і 8 місяців FG, де
`Σ m·CmHn` дає сам файл, `AI_Cont_FG_V7` рядок 112) цьому суперечать два: 1D-1/370 Summer
(сире 1.8499748 → у файлі 1.85) і FG квітня (2.7100073 → 2.709). Жодна стала K не робить
`Truncate(K·…)` правильним для всіх 11, а `Round(K·Σ(nC·x)/100/μ, 3)` — для будь-якої
K ∈ [43.9987; 44.0018), тому `CST.M_CO2 = 44.00` (§6.2); 44.01 цей інтервал не містить.
Звідси `EF_RAW` = 2.0869542 для прикладу A, а EF = 2.087 — те саме число, що й раніше, тож
`CO2_GHG_t` = 0.5361514 не змінюється.

- `@Stream` приходить як `EntryRef` лише у версії `Strict` (R-12, крок `RT-23a`); `@HmbCase` —
  назва кейсу (`CASE_NAME`, «370 Winter»). Якщо шаблон v2 зробить `HmbCase` Lookup-колонкою на
  `STREAM_CASE`, то `CASE = @HmbCase` і `REGFIND` не потрібен.
- Усі фільтри мають верхній кон'юнкт `ROW.CASE = !CASE`, тобто йдуть індексним шляхом
  (FEATURE-REGISTRY-TABLES §5.4, «Індексний шлях»), а не повним переглядом довідника.
- `EF_RAW` містить і CO₂ складу: у `COMPONENT` для CO₂ `N_C = 1` (розбір §1.4 р.17);
  псевдокомпоненти HYSYS мають `N_C = N_S = 0`.
- `Rho20`: за робочим припущенням RQ-4 FEATURE-REGISTRY-TABLES густина — **вхід події**, тож
  колонка `Rho20` лишається й у шаблоні v2. Якщо замовник підтвердить збережене поле кейсу —
  `Rho20 = REGFIELD(!CASE, 'RHO_STD')` (розбір §1.4 р.15: `μ/24.04` дає 0.9590, а не 0.9589).
- `LHV_MJ_Sm3` — поле кейсу `LHV_STD` (таблиця вище); для золотих A/C воно дорівнює LHV суміші
  з файлу, 36.0412787449075. Порожнє поле кейсу дає `null` (R-11), і `MethodologyRequiredInput`
  (§5.7) не пускає такий рядок у розрахунок мовчки.
- `SEL_*` (V-11) лишаються: формула №12 бере `!H2S_wt … !C2H6S_wt` замість `@…`.
- ⚠ Записи FG (Pilot, Purge, події FG) складу потоку не мають: `@Stream` порожній, `REGFIND`
  повертає `null`, і `Mu` дасть нуль у знаменнику. Тому v1.1 обирає джерело за видом газу:
  SG — склад потоку, FG — колонки лабораторного аналізу (§5.5) до кроку RG3. Ознака для формули —
  числова Formula-колонка `IsSG` за зразком `IsPilot`/`IsN2` (§5.2; поле `IsSG` у `FlareGasType`).

Контрольні числа ті самі, що в AC-1 FEATURE-REGISTRY-TABLES і в §12.1: μ = 23.0544679,
S = 17.2965447 мас.%, EF_RAW = 2.0869542 → EF = 2.087, LHV = 36.0412787, M = 0.2581915 т,
SO2 = 0.0891735 т і 95.885 г/с, CO2 = 0.5361514 т (розбір §1.5 A, C).

Версія `1.1.0` читає склад SG із довідника. У шаблоні v2 колонки складу (`Mu`, `S_wt`, `T0_C`,
wt% сполук, `LHV_MJ_Sm3`, `EF_t_t`) лишаються лише для FG до RG3; `Stream`/`HmbCase` переходять
на `STREAM`/`STREAM_CASE` (§5.3); `Rho20` — за RQ-4.
**DoD RG1:** ті самі числа A/B/C і нуль розбіжностей зі зведеннями. Для величин, які тепер
рахуються зі складу, точність — до 1e-9 відносно, як у §12.1: μ і S (молярні маси — значення
HYSYS, відновлені з файлу; `M_S = 32.064`); формула `EF` дає рівно значення файлу на всіх
11 складах (3 SG + 8 місяців FG, для FG — як тест формули на складі лабораторії); `LHV` —
рівно поле кейсу. **Мутація:** `Round` → `Truncate(…·1000)/1000` → червоніє 1D-1/370 Summer
(1.849 ≠ 1.85); `M_S = 32.06` → червоніє S.

---

## 7. Прозорість розрахунку

### 7.1 Що зберігається

| Сутність | Зміна | Навіщо |
|---|---|---|
| `calc.MethodologyFormula` | `IsVisible bit DEFAULT 0` — «показувати як проміжний результат»; `Scope tinyint DEFAULT 0` | V-6, V-7 |
| `calc.MethodologyOutput` | `IsPerSubstance bit DEFAULT 1` | V-7 |
| `calc.CalculationResult` | `Kind tinyint DEFAULT 0` (`Output = 0`, `Intermediate = 1`) | значення видимої формули — рядок результату (`OutputCode` = код формули, `UnitId` = `OutputUnitId ?? one`, для Row — `SubstanceEntryId = NULL`) |
| `calc.CalculationStep` | `DocumentId bigint NULL`, `SourceRowKey nvarchar(100) NULL`, `SubstanceEntryId bigint NULL`; заповнюється **`ResultId`**; `TraceJson` за схемою v1 (§7.2) | трейс на комірку |
| `calc.CalculationInput` | **починає писатися**: аргументи, які читають формули (не всі колонки рядка), зі значенням на момент прогону | «з яких чисел вийшло це число» (`CalculationTrace.cs:6-19`) |
| `arc.*` | дзеркальні колонки в `12-archive-tables.sql` | архів D-148 |

**Рішення (V-6): проміжні значення — у `calc.CalculationResult` з `Kind = Intermediate`.**
Шлях читання один (`ReadCurrentAsync`, `CalculationResultStore.cs:270-308`), партиція та сама,
і на проміжне можна прив'язати колонку (`M_t` у сітці). ⛔ `ReportSnapshotBuilder` бере
**лише** `Kind = Output`: зрізи `rpt.*` і їхній `ContentHash` лишаються побайтно тими самими
(D-53). Доводить це тест.

**Правила запису:** видимі кроки й входи пишуться на будь-якому `TraceLevel`, крім `Off`;
невидимі кроки — як сьогодні, лише на `Full`. Обсяг 301: ~100 записів на рік × (~15 входів
+ ~12 видимих кроків) ≈ 3 000 рядків на рік — у межах ЗБР-3.

### 7.2 `TraceJson` — схема v1

```jsonc
{
  "v": 1,
  "expr": "CONVERT(!V_Sm3 * @Rho20, 'kg', 't')",
  "result": "0.2581914962", "unit": "t", "masked": null, "error": null,
  "inputs": [
    { "kind": "formula",  "code": "V_Sm3",  "value": "269.258",  "unit": "Sm3", "stepId": 88121 },
    { "kind": "arg",      "code": "Rho20",  "value": "0.9589",   "unit": "kg_per_Sm3",
      "cell": { "table": "FLARE_RECORD", "row": "E-2026-01-001", "column": "Rho20" } },
    { "kind": "const",    "code": "K_MASS", "value": "0.0024",   "unit": "t_per_t",
      "constantId": 412, "substance": "NO2", "validFrom": "2026-01-01", "validTo": null,
      "source": "Методика … табл. 2" },
    { "kind": "period",   "prop": "Hours",  "value": "744" }
  ]
}
```

**Рішення (V-8):** входи збирає обхід AST (`ReferenceCollector`: `@Arg`, `CST.`, `!Formula`,
`[Period].X`) після обчислення, значення беруться з того самого контексту.
Рушій виразів не змінюється, і друга семантика читання не з'являється.

### 7.3 Провенанс входу

Для `kind = arg` сервер додає, звідки взялося значення комірки:

| Походження | Що показується | Звідки |
|---|---|---|
| `UserEdit` | хто, коли, попереднє значення | `aud.CellChange` (остання зміна) |
| `Import` | файл, рядок, коли | `aud.CellChange` + журнал імпорту |
| `Integration` з події PI | шаблон, ID і назва події, атрибут (або `$start`/`$end`), сире значення й одиниця, стан (`Synced`, `Missing`, `PeriodChanged`…), остання синхронізація | `ext.SourceEventLink` + `ext.SourceEventFieldMap` (§4.7) |
| `Integration` за вікном рядка | атрибут PI, вікно (час проєкту + UTC), тип summary, точок, % Good, `Server`/`Local`, коефіцієнт одиниць, коли | `ext.RowWindowValue` (`IsCurrent`) |
| `Integration` за період | атрибут, період, спосіб згортки, точок | `ext.EntityFieldMap` + `itg.CollectionRun` |
| `Formula` | вираз колонки; для `REGFIELD` — запис довідника, його код і вікно чинності | `cfg.FormulaDef`, `dic.RegistryEntry` |

⚠ **Застарілість:** якщо поточне значення комірки ≠ значенню в `calc.CalculationInput`,
вузол позначається «змінилося після розрахунку» — та сама подія, що дає F-05
(`IMethodologyStore.cs:132-133`), лише видима на конкретному вході.

### 7.4 Ендпоінти

Див. §9: `GET …/calculation-trace`, `GET …/cell-provenance`, `GET …/views/{code}/explain`
(для агрегатів представлення).

---

## 8. Представлення (View)

### 8.1 Рішення: нова сутність `cfg.ViewDef`, а не розширення `rpt.*`

**Рішення (V-1, схвалене людиною 2026-09-27):** представлення — окремий шар **лише
для читання** над даними документа й результатами методологій. `rpt.*` не змінюється.

| Критерій | `rpt.*` (D-52a) | `cfg.ViewDef` |
|---|---|---|
| Джерело | лише `calc.CalculationResult` (`ReportSnapshotBuilder.cs:415-419`) | комірки документа + результати + провенанс |
| Живе чи знімок | знімок із `ContentHash` і статусом даних (D-65) | живе читання, як сітка |
| Контракт | публічний для 166 RDL, зміни лише адитивні (D-53, ФВ-10.12) | внутрішній, версіонується разом із представленням |
| Динамічні колонки | **виключені** рішенням замовника (`10-decisions.md:289`); стеля підсумків саме тому (`ReportLayout.cs:74-79`) | основна форма 301 |
| Форма | одна група + підсумки | транспонування, дерево шапки, секції, зведення |

Що **перевикористовується**, щоб не дублювати механізми:

- **Мова умов і похідних рядків** — діалект `Report` (`02b-expressions.md` §8a, 7 функцій) з
  каталогом атрибутів представлення. Потрібен рефакторинг `ReportExpressionChecker` під
  змінний каталог колонок — без зміни поведінки, окремим PR (B0a).
- **Функції агрегації** — `sum/count/avg/min/max` із `ReportLayout.Functions`
  (`ReportLayout.cs:56-72`), винесені в спільний `AggregateFunctions` (рефакторинг B0b).
  Нові лише іменовані агрегації 301: `perPeriodRate` і `worstCaseRate` (B16 §3 A-1, A-4 —
  «іменовані агрегації сервісу з автотестами», `B16-ecr-reporting.md:118-138`).
- **Експорт** — той самий ClosedXML (`ExcelExporter.cs:3`), новий писар `ViewExcelWriter`.

✎ **2026-09-27: погоджено.** Людина схвалила V-1, дослівно: «так погоджуюся на Подання». Це
схвалення рішення, а не назви: у документації термін — «представлення», бо «подання» в коді
означає Submit. Межа, як її зафіксовано: представлення документа з транспонуванням і
групуванням стовпців — частина документа, а не звіт; виняток `D-52a` «матриці з динамічними
колонками» стосується конструктора `rpt.*` і на представлення не поширюється; `D-52a`
лишається чинним для `rpt.*`/SSRS, `rpt.*` не змінюється (`D-53`). Питання HQ-14 закрито.

✓ У реєстр `docs/tz/10-decisions.md` рішення внесене як `D-149` (2026-09-27, разом із V-2…V-15
→ `D-171…D-184`), як того вимагає правило `docs/README.md`.

### 8.2 Модель даних

```sql
cfg.ViewDef       Id, TemplateId FK cfg.Template, Code nvarchar(64), NameL10n, Ordinal, IsActive,
                  UQ(TemplateId, Code)
cfg.ViewVersion   Id, ViewDefId FK, Version nvarchar(20), Status tinyint (Draft|Published|Deprecated),
                  LayoutJson nvarchar(max), CheckedAgainstTemplateVersionId int NULL,
                  CreatedAt, CreatedByUserId, PublishedAt NULL, PublishedByUserId NULL, RowVersion
```

- Представлення належить **шаблону**, а не версії шаблону: макет посилається на **коди**
  колонок і виходів, які переживають клон версії. Публікація перевіряє макет проти
  обраної версії шаблону й чинних версій методологій, прив'язаних до її таблиць.
- Під час показу атрибута, якого у версії документа немає, клітинка отримує `#REF` і
  попередження в `warnings`, а представлення не падає.

### 8.3 Запис і атрибути

**Запис** — рядок таблиці-джерела в екземплярі періоду. Його атрибути:

| Префікс | Приклад | Значення |
|---|---|---|
| — (код колонки) | `Start`, `Volume_Sm3`, `Flare` | комірка (Lookup — підпис запису, Date — дата-час, число — рядком без втрати знаків) |
| `calc_` | `calc_V_Sm3`, `calc_M_t`, `calc_tons_SO2`, `calc_gsec_NO2` | `calc.CalculationResult` актуального прогону (Output або Intermediate), **з речовиною** — не через суму оверлею (`CalculatedCellOverlay.cs:34-35`) |
| `meta_` | `meta_Period`, `meta_Ordinal`, `meta_RowKey` | службові |
| `reg_` | `reg_Composition_N2_vol` | поля запису довідника — лише після FEATURE-REGISTRY-TABLES (RG2) |
| `derived` | `LimitFlag` | вираз діалекту Report над атрибутами запису |

Права: атрибути `calc_*` вимагають `Calculation.View`. Без нього рядки лишаються, а
значення замінюються позначкою «немає доступу» — так само, як панель розрахунків
(`DocumentsController.cs:413-439`). Комірки документа — через ті самі рішення доступу, що
й сітка (`GetTableSliceHandler`).

### 8.4 `LayoutJson` — схема v1 (стисло)

| Вузол | Поля |
|---|---|
| корінь | `schema: 1`, `labelMode` (`single` \| `bilingual` + мови), `navigation[]` (групи аркушів — замість Document map), `sheets[]` |
| `sheet` | `code`, `title`, `kind` (`recordMatrix` \| `pivot`), `source`, далі за видом |
| `source` | `tables[]` (коди), `periods` (`period` \| `year`), `where` (Report), `orderBy[]` |
| `recordMatrix` | `columns.levels[]` (`by`: `period` \| `ordinal` \| `attr:<code>`; `label`-шаблон; `hideEmpty`; `total{label, rows{rowKey: fn}}`), `columns.corner`, `sections[]` |
| `section` | `code`, `title`, `collapsed`, `intermediate` (ховається без перемикача), `headerTone`, `rows[]` |
| `row` | `key`, `value` (атрибут чи `derived`), `expr`, `label`, `format`, `tone[]`, `emphasis`, `provenance` (чіп статусу PI), або `each: "substance"` із переліком |
| `pivot` | `rowBlocks[]` (`code`, `title`, `where`), `measures` (`each`/перелік, `inTotal`), `totalRow`, `columns[]` (дерево: `repeat: period` \| `label` + `children[]` \| листок `cell{where, fn, value, params}`) |
| `format` | `fixed:N`, `date`, `time`, `datetime`, `duration` (hh:mm:ss), `text` |
| `tone` | `{ when: <Report-вираз з [this]>, tone: bad \| warn \| info }` — ⛔ колір лише для проблеми (KIT §1 п.3) |

Стелі: ≤ 20 аркушів, ≤ 400 рядків на аркуш, ≤ 60 000 клітинок відповіді. Більше —
`ECR-VIEW-0422` з порадою звузити місяці (`err.ECR-VIEW-0422.tooLarge`).

### 8.5 Приклад: аркуш `AI_Int_SG_V8`

```jsonc
{
  "code": "AI_Int_SG_V8",
  "title": { "en": "Island A · Intermittent · Sour gas · V8" },
  "kind": "recordMatrix",
  "source": {
    "tables": ["FLARE_RECORD"], "periods": "year",
    "where": "[Regime] = 'Intermittent' AND [GasType] = 'SG' AND [Category] = 'V8' AND [IsN2] = 0",
    "orderBy": ["Start"]
  },
  "columns": {
    "corner": { "en": "Unit 230 – Flare", "ru": "Установка 230 – Факельная сист." },
    "levels": [
      { "by": "period",  "label": "{period:MMM}", "hideEmpty": true,
        "total": { "label": { "en": "Total" }, "rows": { "vol": "sum" } } },
      { "by": "ordinal", "label": "{n}" }
    ]
  },
  "sections": [
    { "code": "event", "title": { "en": "Event", "ru": "Событие" }, "rows": [
      { "key": "area",    "value": "Area",        "label": { "en": "Area", "ru": "Район работ" } },
      { "key": "region",  "value": "Region",      "label": { "en": "Offshore", "ru": "На море" } },
      { "key": "start_d", "value": "Start",       "format": "date", "label": { "en": "Start date", "ru": "Дата начала" } },
      { "key": "start_t", "value": "Start",       "format": "time", "label": { "en": "Start time (hh:mm:ss)", "ru": "Время начала" } },
      { "key": "end_d",   "value": "End",         "format": "date", "label": { "en": "End date", "ru": "Дата окончания" } },
      { "key": "end_t",   "value": "End",         "format": "time", "label": { "en": "End time", "ru": "Время окончания" } },
      { "key": "dur",     "value": "DurationSec", "format": "duration", "label": { "en": "Duration (hh:mm:ss)" } },
      { "key": "dur_s",   "value": "DurationSec", "format": "fixed:0",  "label": { "en": "Duration (sec)" } },
      { "key": "flare",   "value": "Flare",       "label": { "en": "Flare unit", "ru": "Тип факельной установки" } },
      { "key": "unit",    "value": "SourceUnit",  "label": { "en": "Type of unit – source of discharge" } },
      { "key": "descr",   "value": "Description", "label": { "en": "Description how the release occurred" } },
      { "key": "cat",     "value": "Category",    "label": { "en": "TUGF category", "ru": "Категория ТНС" } }
    ]},
    { "code": "volume", "rows": [
      { "key": "vol", "value": "calc_V_Sm3", "format": "fixed:3", "emphasis": "strong", "provenance": true,
        "label": { "en": "Gas Volume, Sm3 (20°C, 1 atm)", "ru": "Объем газа, ст.м3" } }
    ]},
    { "code": "limits", "rows": [
      { "key": "flag", "value": "derived",
        "expr": "IF([calc_V_Sm3] > [calc_V_LIM] OR [calc_gsec_SO2] > [calc_SO2_LIM_GS], 'да', 'нет')",
        "tone": [{ "when": "[this] = 'да'", "tone": "bad" }],
        "label": { "ru": "Анализ: превышение макс. расхода" } },
      { "key": "vlim",   "value": "calc_V_LIM",      "format": "fixed:0", "label": { "ru": "Предельный объем газа, ст.м3" } },
      { "key": "so2gs",  "value": "calc_gsec_SO2",   "format": "fixed:3", "label": { "ru": "Выброс SO2, г/с" } },
      { "key": "so2lim", "value": "calc_SO2_LIM_GS", "format": "fixed:3", "label": { "ru": "Предельно допустимый выброс SO2, г/с" } }
    ]},
    { "code": "gas", "rows": [
      { "key": "gtype", "value": "GasType", "label": { "en": "Type of gas", "ru": "Тип газа" } },
      { "key": "strm",  "value": "Stream",  "label": { "en": "№ Stream", "ru": "№ потока" } },
      { "key": "hmb",   "value": "HmbCase", "label": { "en": "What is the HMB", "ru": "Какой принят МТБ" } },
      { "key": "rho",   "value": "Rho20",   "format": "fixed:4" },
      { "key": "mu",    "value": "Mu",      "format": "fixed:2" },
      { "key": "s",     "value": "S_wt",    "format": "fixed:4" },
      { "key": "t0",    "value": "T0_C",    "format": "fixed:6" }
    ]},
    { "code": "composition", "collapsed": true, "requires": "registry", "rows": [ /* RG2 */ ] },
    { "code": "energy", "intermediate": true, "rows": [
      { "key": "lhv", "value": "LHV_MJ_Sm3", "format": "fixed:3" },
      { "key": "ncv", "value": "calc_NCV",   "format": "fixed:3" },
      { "key": "ef",  "value": "EF_t_t",     "format": "fixed:3" },
      { "key": "m",   "value": "calc_M_t",   "format": "fixed:6" }
    ]},
    { "code": "ghg", "rows": [
      { "key": "co2", "value": "calc_CO2_GHG_t", "format": "fixed:6" },
      { "key": "ch4", "value": "calc_CH4_GHG_t", "format": "fixed:6" },
      { "key": "n2o", "value": "calc_N2O_GHG_t", "format": "fixed:6" }
    ]},
    { "code": "tons", "title": { "en": "tons" }, "headerTone": "accent", "rows": [
      { "each": "substance",
        "of": ["NO2", "NO", "SOOT", "SO2", "H2S", "CO", "CH4", "C4H10S", "CH4S", "C3H8S", "C2H6S"],
        "value": "calc_tons_{substance}", "format": "fixed:6", "label": "{substance.name}" }
    ]},
    { "code": "w", "intermediate": true, "rows": [
      { "key": "ratio", "value": "calc_W_RATIO", "format": "fixed:6", "label": { "en": "Wist/Wsnd" } },
      { "key": "wsrc",  "value": "calc_W_SRC",   "format": "fixed:6" },
      { "key": "wsnd",  "value": "calc_W_SND",   "format": "fixed:6" }
    ]},
    { "code": "gsec", "title": { "en": "g/sec" }, "headerTone": "accent", "rows": [
      { "each": "substance", "of": ["NO2", "NO", "SOOT", "SO2", "H2S", "CO", "CH4", "C4H10S", "CH4S", "C3H8S", "C2H6S"],
        "value": "calc_gsec_{substance}", "format": "fixed:6", "label": "{substance.name}" }
    ]}
  ]
}
```

Результат — шапка у два рівні (`JAN` зі злиттям на 9 подій + `Total`; номери 1…9),
закріплений стовпець підписів, секції згортаються, місяці без подій сховані (`hideEmpty`),
`Total` заповнений лише для об'єму, як у файлі (розбір §1.2).

### 8.6 Приклад: аркуш `HP_LP_gsec`

```jsonc
{
  "code": "HP_LP_gsec", "kind": "pivot",
  "title": { "en": "G/SEC" },
  "source": { "tables": ["FLARE_RECORD"], "periods": "year" },
  "rowBlocks": [
    { "code": "FG", "title": { "en": "Fuel Gas" }, "where": "[GasType] = 'FG'" },
    { "code": "SG", "title": { "en": "Sour Gas" }, "where": "[GasType] = 'SG'" }
  ],
  "measures": { "each": "substance", "of": ["NO2", "NO", "SOOT", "SO2", "H2S", "CO", "CH4", "C4H10S", "CH4S", "C3H8S", "C2H6S"],
                "label": "{substance.name}", "format": "fixed:7" },
  "totalRow": { "label": { "en": "Total" }, "fn": "sum", "emphasis": "strong" },
  "columns": [
    { "repeat": "period", "label": "{period:MMMM}", "children": [
      { "label": { "en": "Intermittent" }, "children": [
        { "code": "i_v6_hp_sum", "label": "HP", "sub": "sum, t={period:hours}",
          "cell": { "where": "[Regime]='Intermittent' AND [FlareSide]='HP' AND [Category]='V6' AND [IsN2]=0",
                    "fn": "perPeriodRate", "value": "calc_tons_{substance}" } },
        { "code": "i_v6_hp_max", "label": "HP", "sub": "max",
          "cell": { "where": "[Regime]='Intermittent' AND [FlareSide]='HP' AND [Category]='V6' AND [IsN2]=0",
                    "fn": "max", "value": "calc_gsec_{substance}" } }
        /* … LP; V7, V8, V9 — те саме; конструктор генерує їх пресетом (§10.5) */
      ]},
      { "label": { "en": "Continuous" }, "children": [
        { "code": "c_hp_sum", "label": "HP", "sub": "sum, t={period:hours}",
          "cell": { "where": "[Regime]='Continuous' AND [FlareSide]='HP'",
                    "fn": "perPeriodRate", "value": "calc_tons_{substance}" } }
      ]},
      { "label": { "en": "Intermittent · Nitrogen" }, "children": [ /* HP, LP: max по [IsN2]=1 */ ] },
      { "code": "ytd", "label": "YTD", "children": [
        { "code": "ytd_hp", "label": "HP", "blockScope": "all", "showIn": { "FG": "totalRowOnly" },
          "cell": { "fn": "worstCaseRate", "value": "calc_tons_{substance}",
                    "params": { "continuousWhere": "[Regime]='Continuous' AND [FlareSide]='HP'",
                                "intermittentWhere": "[Regime]='Intermittent' AND [FlareSide]='HP'",
                                "maxValue": "calc_gsec_{substance}",
                                "groupBy": ["Category", "IsN2"] } } }
      ]}
    ]}
  ]
}
```

### 8.7 Агрегації (закритий перелік)

| `fn` | Семантика | Звідки |
|---|---|---|
| `sum`, `count`, `avg`, `min`, `max` | як `ReportLayout.Functions`; `null` поглинається | `ReportLayout.cs:56-72` (винесено в B0b) |
| `perPeriodRate` | `Σ CONVERT(value, t → g) / секунди календарного періоду` | розбір §1.3: continuous sum = Σт·10⁶/(год місяця·3600) |
| `worstCaseRate` | `Σ perPeriodRate(continuousWhere) + Σ_{групи groupBy} max(maxValue серед intermittentWhere)`; `blockScope: all` — по всіх `rowBlocks` | розбір §1.3 «місячний YTD»; B16 A-4 |

- ⚠ **B16 A-2** (добова подія 86 300…86 500 с не входить у `max`) — фільтр листка, не
  окрема функція. Чи діє для 301 — HQ-12; дефолт: не діє, бо файл його не показує.
- Підсумкові стовпці `no_N2`, `Nitrogen`, `V6/V7/V8/V9` у `HP_LP_tons` — листки з `where`
  по `IsN2`, без обчислюваних стовпців.
- Рядок об'єму в `HP_LP_tons` — міра `calc_V_Sm3` з `inTotal: false`.

### 8.8 Річний вид, свіжість, застарілість

- `periods: year` читає всі періоди року проєкту (`PeriodKey = Year·100 + Sequence`),
  по кожному — зріз таблиці тим самим обробником, що сітка, і актуальні результати
  (`ReadCurrentAsync`). Бюджет: 12 місяців × 100 записів × 136 рядків, p95 ≤ 2 с.
  Більше — завантаження по місяцях під час прокрутки.
- **«Дані актуальні на…»** — окремо на кожне джерело, як у файлі, де час різний на
  різних аркушах (розбір §1.7):

| Джерело | Мітка |
|---|---|
| PI події | `LastRunAt` розкладу сутності-шаблону + лічильники `ext.SourceEventLink`: `Missing`, `Open`, `Unmapped`, `PeriodChanged`, змінені після закриття періоду |
| PI за вікном рядка | `max(RetrievedAt)` поточних `ext.RowWindowValue` у межах аркуша + лічильники `NoData`/`Partial` |
| PI за період | `LastRunAt`/`Watermark` розкладів сутностей, що живлять таблиці |
| ручні правки | остання `aud.CellChange` з `UserEdit` |
| розрахунок | `FinishedAt` актуального прогону по кожному періоду |

- **Застарілість:** `GetCalculationFreshnessAsync` (`IMethodologyStore.cs:132-133`) по
  кожному періоду. Є застаріле — банер «Входи змінилися після розрахунку {час}» з однією
  дією «Перерахувати», а на числах — позначка.

### 8.9 Експорт XLSX і друк

`ViewExcelWriter` (ClosedXML):

| Елемент | Як |
|---|---|
| шапка | кожен рівень — рядок, об'єднання `Range(…).Merge()` на `span` |
| закріплення | `SheetView.Freeze(рядків шапки, 1)` |
| формати | `fixed:N` → `0.000…`, `date` → `dd.mm.yyyy`, `duration` → `[h]:mm:ss` |
| значення | **числа — числами**, дати — датами (у файлі SSRS — текст, розбір §1.2) |
| секції | групування рядків (outline), заголовок секції з заливкою тону |
| підписи | білінгвальні, як у представленні |
| «Document map» | перший аркуш із гіперпосиланнями на аркуші |
| друк | `PageSetup.SetRowsToRepeatAtTop` — рядки шапки повторюються на кожній сторінці |

Задача фонова (`202` + «My tasks», L8), завантаження — тим самим маршрутом, що експорт
документа (`DocumentsController.cs:472`).

**Друк з браузера:** `?print=1` малює те саме представлення звичайною `<table>` з `<thead>`, що
повторюється на кожній сторінці, без оболонки й бічних панелей. PDF держформ лишається в
SSRS.

### 8.10 Рендер на клієнті

**Рішення (V-14):** переглядач малює **нативну `<table>`** (Mantine `Table`,
`position: sticky` для шапки й першого стовпця), а не RevoGrid:

- семантика шапки з коробки: `<th scope="colgroup" colspan>`, асоціація заголовків для
  читалки екрана — те, чого бракувало сітці (U-04, `UX-PASS-2026-09-23.md:117-129`);
- друк без окремого рендеру;
- гарячий `DocumentGrid.tsx` (2 000+ рядків) не зачіпається.

RevoGrid 4.11 такий вигляд теж уміє: `ColumnGrouping` (`interfaces.d.ts:77-86`),
`pin: 'colPinStart'` (`:182-184`), групування рядків `grouping` (`components.d.ts:170-172`).
Тому це запасний шлях для аркушів понад ~60 000 клітинок, де без віртуалізації не обійтися.

---

## 9. API

### 9.1 Ендпоінти

| Метод і маршрут | Право | Тіло / параметри → відповідь | Помилки |
|---|---|---|---|
| `POST /api/v1/data-sources/{id}/probe-window` | `Integration.Manage` | `{path, from, to, timeZone?, summary, isStep, sourceUnitId?, targetUnitId?}` → `WindowProbeDto {local, server?, unit, conversionFactor, pointCount, percentGood, gaps[], points[≤500]}` | `ECR-INT-0404` (шлях; `suggestions`), `ECR-INT-0422` (вікно, одиниця), `ECR-INT-0502`, `ECR-INT-0503` |
| `GET /api/v1/row-window-maps?tableDefId=` | `Integration.View` | → `RowWindowMapDto[]` | — |
| `POST /api/v1/row-window-maps` | `Integration.Manage` | `{tableDefId, targetColumn, startColumn, endColumn, selectorColumn?, summary, isStep, targetUnitId, minPercentGood, refetchWithinDays, sources[]}` → 201 | `ECR-INT-0422` (`.windowColumnsNotDate`, `.targetNotDecimal`, `.targetScaleTooSmall`, `.selectorNotInTable`), `ECR-INT-0409` (`.rowWindowTargetTaken`) |
| `PUT /api/v1/row-window-maps/{id}` | `Integration.Manage` | те саме + `rowVersion` → 200 | + `ECR-INT-0404`, `ECR-INT-0409` (`.concurrency`) |
| `POST /api/v1/row-window-maps/{id}/pause`, `/resume` | `Integration.Manage` | → 200 | `ECR-INT-0409` (як `EntityFieldMap.cs:222-274`) |
| `POST /api/v1/documents/{id}/row-windows/fetch` | `Document.View` + право запису в цільову колонку | `{tableInstanceId, rowKeys?}` → 202 `JobAcceptedResponse` | `ECR-DOC-0404`, `ECR-ACCS-0403`, `ECR-PRD-0409` (період закритий, `ErrorCodes.PeriodClosed`) |
| `GET /api/v1/documents/{id}/row-windows?tableInstanceId=` | `Document.View` | → `[{rowKey, status, value, retrievedAt, fromLocal, toLocal, percentGood, errorCode}]` | `ECR-DOC-0404` |
| `GET /api/v1/data-sources/{id}/event-templates` | `Integration.View` | → `[{template, attributes[{code, displayName, scope, sourceUnitSymbol, dataType}]}]` (каталог шаблонів подій, §4.7.1) | `ECR-INT-0422` (`.eventQueryNotConfigured`), `ECR-INT-0502`, `ECR-INT-0503` |
| `POST /api/v1/data-sources/{id}/probe-events` | `Integration.Manage` | `{template, from, to, timeZone?, mapId?, max ≤ 50}` → `{events[{id, name, startLocal, endLocal, attributes[], resolved{column: value}, unmapped[]}], truncated}` — нічого не пише | `ECR-INT-0404` (шаблон; `suggestions`), `ECR-INT-0422` (`.eventQueryNotConfigured`, `.windowInvalid`), `ECR-INT-0502`, `ECR-INT-0503` |
| `GET /api/v1/source-event-maps?tableDefId=` | `Integration.View` | → `SourceEventMapDto[]` (з полями й відповідностями значень) | — |
| `POST /api/v1/source-event-maps` | `Integration.Manage` | `{sourceEntityId, documentId, tableDefId, filter?, volumeMode, fields[{targetColumn, sourceAttribute, scope, valueKind, sourceUnitId?, targetUnitId?, valueMap[]}]}` → 201 | `ECR-INT-0422` (`.eventMapStartEndRequired`, `.eventMapTableNotDynamic`, `.eventMapColumnNotInTable`, `.eventMapValueKindMismatch`, `.eventMapVolumeModeInvalid`), `ECR-INT-0409` (`.eventMapTaken`) |
| `PUT /api/v1/source-event-maps/{id}` | `Integration.Manage` | те саме + `rowVersion` → 200 | + `ECR-INT-0404` (`.eventMapNotFound`), `ECR-INT-0409` (`.concurrency`) |
| `POST /api/v1/source-event-maps/{id}/pause`, `/resume` | `Integration.Manage` | → 200 | `ECR-INT-0409` |
| `POST /api/v1/documents/{id}/source-events/sync` | `Document.View` + право запису в таблицю | `{tableInstanceId?, from?, to?}` (без меж — вікно `LookbackDays`; з межами — завантаження історії, не довше року) → 202 `JobAcceptedResponse` | `ECR-DOC-0404`, `ECR-ACCS-0403`, `ECR-INT-0404` (`.eventMapNotFound`), `ECR-INT-0422` (`.windowInvalid`) |
| `GET /api/v1/documents/{id}/source-events?tableInstanceId=` | `Document.View` | → `[{rowKey, sourceEventId, eventName, status, keptManual[], unmapped[{column, value}], lastSyncAt}]` | `ECR-DOC-0404` |
| `POST /api/v1/documents/{id}/source-events/{linkId}/revert` | `Document.View` + право запису в колонки | `{columns[]}` → 200: пише поточні значення події з `origin = Integration` («Повернути значення з PI») | `ECR-DOC-0404`, `ECR-ACCS-0403`, `ECR-PRD-0409`, `ECR-INT-0404` (`.sourceEventNotFound`), `ECR-INT-0503` |
| `POST /api/v1/entity-field-maps` (чинний) | `Integration.Manage` | + `aggregation: TimeWeightedAvg \| TimeIntegral`, `isStep` | чинні |
| `GET /api/v1/documents/{id}/calculation-trace?periodKey=&rowKey=&output=&substance=` | `Document.View` + `Calculation.View` | → `CellTraceDto` (дерево вузлів §7.2 + провенанс §7.3 + `stale`) | `ECR-CALC-0404` (`.traceNotFound`, `.noCurrentRun`), `ECR-DOC-0404` |
| `GET /api/v1/documents/{id}/cell-provenance?tableInstanceId=&rowKey=&column=` | `Document.View` | → `CellProvenanceDto` | `ECR-DOC-0404`, `ECR-ROW-0404` |
| `POST /api/v1/documents/{id}/tables/{tableInstanceId}/rows/preview-calculation` | `Document.View` + `Calculation.View` | `{rowKey?, cells{code: value}}` → `{outputs[{code, substance, value, unit}], intermediates[], errors[]}`, **нічого не пише** | `ECR-CALC-0404` (`.noBinding`), `ECR-CALC-0422` (`.previewInvalid`) |
| `GET /api/v1/documents/{id}/views` | `Document.View` | → опубліковані представлення шаблону документа | `ECR-DOC-0404` |
| `GET /api/v1/documents/{id}/views/{code}?year=\|periodKey=&sheet=&months=&intermediate=` | `Document.View` (`calc_*` — `Calculation.View`) | → `ViewGridDto` (§9.2) | `ECR-VIEW-0404`, `ECR-DOC-0404`, `ECR-PRD-0404`, `ECR-VIEW-0422` (`.tooLarge`) |
| `GET /api/v1/documents/{id}/views/{code}/explain?sheet=&cell=` | `Document.View` + `Calculation.View` | → агрегат: `fn`, фільтр, записи-внески зі значеннями й посиланнями `ref` | `ECR-VIEW-0404` |
| `POST /api/v1/documents/{id}/views/{code}/export` | `Document.Export` | `{year\|periodKey, sheets?, months?, intermediate}` → 202 | `ECR-VIEW-0404`, `ECR-VIEW-0422` |
| `GET /api/v1/documents/{id}/freshness?year=\|periodKey=` | `Document.View` | → `[{source, at, detail, counts}]` + `stale[]` | `ECR-DOC-0404` |
| `GET /api/v1/templates/{id}/views` | `Template.View` | → `ViewDefDto[]` (з версіями) | `ECR-TMPL-0404` |
| `POST /api/v1/templates/{id}/views` | `Template.Edit` | `{code, nameL10n}` → 201 + чернетка v1 | `ECR-VIEW-0409` (`.codeTaken`), `ECR-REQ-0422` |
| `PUT /api/v1/templates/{id}/views/{code}/draft` | `Template.Edit` | `{layout, rowVersion}` → 200 + `warnings[]` | `ECR-VIEW-0422` (`.layoutInvalid` з `path`, `.unknownAttribute`, `.expression`, `.aggregateNotNumeric`), `ECR-VIEW-0409` (`.concurrency`) |
| `POST /api/v1/templates/{id}/views/{code}/preview` | `Template.Edit` | `{layout, documentId, year\|periodKey, sheet}` → `ViewGridDto` (без збереження) | як `draft` + `ECR-DOC-0404` |
| `POST /api/v1/templates/{id}/views/{code}/versions/{vid}/publish` | `Template.Publish` | → 200 | `ECR-VIEW-0409` (`.versionNotDraft`), `ECR-VIEW-0422` |
| `POST /api/v1/templates/{id}/views/{code}/versions` | `Template.Edit` | нова чернетка з опублікованої | `ECR-VIEW-0409` (`.draftExists`) |

⚠ `EndpointCoverageTests` вимагає споживача в клієнті для **кожного** ендпоінта, тож
ендпоінт і його екран ідуть одним кроком плану (межа ~800 рядків, тести ≥ 40 %).

### 9.2 `ViewGridDto`

```jsonc
{
  "view": { "code": "HSE301_YEAR", "version": "1.0.0" },
  "sheet": { "code": "AI_Int_SG_V8", "title": "…", "kind": "recordMatrix" },
  "nav": [{ "group": "Offshore", "sheets": [{ "code": "AI_Int_SG_V8", "title": "…", "records": 56, "issues": 0 }] }],
  "header": { "levels": [[{ "label": "JAN", "span": 9 }, { "label": "Total", "span": 1, "kind": "total" }],
                         [{ "label": "1" }, …]], "frozenColumns": 1 },
  "rows": [{ "key": "vol", "section": "volume", "kind": "data", "label": ["Gas Volume, Sm3", "Объем газа, ст.м3"],
             "format": "fixed:3", "intermediate": false }],
  "cells": [[{ "v": "269.258000", "t": "n",
               "ref": { "periodKey": 202601, "rowKey": "E-2026-01-001", "attr": "calc_V_Sm3" },
               "status": "fetched" }]],
  "freshness": [{ "source": "pi-window", "at": "2026-09-27T15:00:07Z", "counts": { "noData": 0, "partial": 1 } }],
  "stale": [{ "periodKey": 202608, "inputsChangedAt": "…", "calculatedAt": "…" }],
  "warnings": [{ "code": "ECR-VIEW-0422", "messageKey": "err.ECR-VIEW-0422.unknownAttribute", "path": "sheets[2].sections[4].rows[1]" }]
}
```

Числа — **рядками**: шістнадцятий знак має доїхати до екрана (`DocumentGrid.tsx:1971-1990`).

### 9.3 Нові коди й ключі

| Код | HTTP | `messageKey` |
|---|---|---|
| `ECR-VIEW-0404` | 404 | `err.ECR-VIEW-0404.viewNotFound`, `.versionNotFound` |
| `ECR-VIEW-0409` | 409 | `err.ECR-VIEW-0409.codeTaken`, `.versionNotDraft`, `.draftExists`, `.concurrency` |
| `ECR-VIEW-0422` | 422 | `err.ECR-VIEW-0422.layoutInvalid`, `.unknownAttribute`, `.expression`, `.aggregateNotNumeric`, `.tooLarge` |
| `ECR-INT-0422` (чинний) | 422 | + `.queryKindNotConfigured`, `.windowColumnsNotDate`, `.targetNotDecimal`, `.targetScaleTooSmall`, `.selectorNotInTable`, `.windowInvalid`; події (§4.7): `.eventQueryNotConfigured`, `.eventMapStartEndRequired`, `.eventMapTableNotDynamic`, `.eventMapColumnNotInTable`, `.eventMapValueKindMismatch`, `.eventMapVolumeModeInvalid` |
| `ECR-INT-0409` (чинний) | 409 | + `.rowWindowTargetTaken`, `.concurrency`, `.eventMapTaken` |
| `ECR-INT-0404` (чинний) | 404 | + `.rowWindowMapNotFound`, `.eventMapNotFound`, `.sourceEventNotFound` |
| `ECR-CALC-0404` (чинний) | 404 | + `.traceNotFound`, `.noCurrentRun`, `.noBinding` |
| `ECR-CALC-0422` (чинний) | 422 | + `.rowScopeReferencesSubstance`, `.previewInvalid` |

⚠ Новий код — це п'ять місць: `ErrorCodes.cs`, `docs/build/02-contracts.md` §7, `09-seed.sql`
(заголовок `err.<код>` і подробиці), місце кидка і **арм у `ExceptionHandlingMiddleware.cs`**.
Без арму код поїде з 422, хоч би які цифри в ньому стояли. Сторожі: `ContractIntegrityTests`,
`ErrorTitleCatalogTests`, `SeedCatalogTextTests`, `MessageKeyRatchetTests`.

Звірено 2026-09-27 з `src/Ecr.Domain/Errors/ErrorCodes.cs` і §7 `02-contracts.md`:

- `ECR-VIEW-*` — новий домен, кодів із ним у коді немає; формат `^ECR-[A-Z]{3,4}-\d{4}$`
  (`ContractIntegrityTests.cs:124`) він задовольняє.
- У чинних кодів нові лише `messageKey`, а опис у §7 **розширюється**: сьогодні `ECR-INT-0422` —
  «UOM атрибута джерела змінився», `ECR-INT-0409` — стан мапінгу, `ECR-CALC-0404` — «версії
  методології не існує».
- Закритий період — чинний `ECR-PRD-0409` (`PeriodClosed`), **не** `ECR-PRD-4223`: той означає
  `Reopen` документа при закритому періоді (`ReopenBlockedByPeriod`).
- З FEATURE-REGISTRY-TABLES §7.2 коди не перетинаються: той додає `ECR-REG-4092`, `ECR-REG-4093`,
  `ECR-REG-4221` і нові `messageKey` чинних `ECR-REG-0422`, `ECR-REQ-0422`.

---

## 10. Інтерфейс

### 10.1 Принципи (KIT §1, DIRECTIVE-15 §0)

Одна головна дія на екран; інспектор закритий за замовчуванням (L2); колір — лише для
проблеми (L3), «да» перевищення — `bad` з іконкою й текстом, «нет» — нейтральний; усі
стани (L10); довга дія — у «My tasks» (L8); стан діалогів і панелей — в адресі
(`?panel=`, `?dialog=`, `?tab=`).

### 10.2 Переглядач представлення

Маршрут: `/documents/:id/views/:code?year=2026&sheet=AI_Int_SG_V8&months=1,2,8&intermediate=1&panel=why`
(новий запис `documentView` у `app/routes.ts` поруч із `documentDetail`, `routes.ts:451-459`;
`numericParams: ['id']`). Вхід — перемикач «Таблиці | Представлення» (`viewer.switchTables` /
`viewer.switchViews`: «Tables» / «Views») у `DocumentToolbar`.

```
┌ ← DOC-000123 · HSE301 Offshore Island A ───────────────────────────────────────────────────┐
│ HSE301.Year · 2026                                        [ Export to Excel ]  Print  More ▾ │
│ ● PI 27.09 20:00 · 1 partial   ● Calculated 27.09 20:02   ● Edited 26.09 17:49 (M. Petrenko)│
├────────────────┬────────────────────────────────────────────────────────────────────────────┤
│ SHEETS         │ Months [Jan][Feb][Mar̶][Apr̶][May̶][Jun̶][Jul̶][Aug]  ⌕ Find row…   ☐ Intermediate │
│ ▾ Offshore     │ ┌──────────────────────────────────┬────────── JAN ──────────┬───────┬─ FEB ┄ │
│   Cont FG V7 18│ │ Unit 230 – Flare                 │   1    │   2    │ … │ 9   │ Total │  1  ┄ │
│ ▸ Int SG V8  56│ │ Установка 230 – Факельная сист.  │        │        │   │     │       │     ┄ │
│   Int SG V9   2│ ├──────────────────────────────────┼────────┼────────┼───┼─────┼───────┼─────┄ │
│   FG V8 N2   18│ │ ▾ Event · Событие                                                         │
│ ▾ HP_LP        │ │   Start date · Дата начала       │28.01.26│29.01.26│   │     │       │     ┄ │
│   Tons         │ │   Duration (sec)                 │    930 │    545 │   │     │       │     ┄ │
│   G/sec        │ │ ▾ Volume                                                                  │
│                │ │   Gas Volume, Sm3 · Объем газа   │269.258⁽ᴾᴵ⁾│174.746│ │89.264│1177.766│    ┄ │
│                │ │ ▾ Limits                                                                  │
│                │ │   Анализ: превышение             │   нет  │   нет  │   │     │       │     ┄ │
│                │ │ ▸ Composition (RG2 · 68 rows)                                             │
│                │ │ ▾ g/sec                                                                   │
│                │ │   SO2                            │ 95.885 │106.189 │   │     │       │     ┄ │
│                │ └──────────────────────────────────┴────────┴────────┴───┴─────┴───────┴─────┄ │
└────────────────┴────────────────────────────────────────────────────────────────────────────┘
```

| Елемент | Поведінка |
|---|---|
| навігатор аркушів (замість Document map) | групи, лічильник записів; ⚠-крапка, якщо на аркуші є `NoData`/застаріле |
| шапка | 2–4 рівні, липка; перший стовпець липкий; `Total` приглушений фоном `--sunken` |
| місяці | чіпи; місяць без даних вимкнений і перекреслений; вибір — в URL |
| пошук | фільтрує рядки за підписом (обидві мови); Enter — до наступного збігу |
| «Проміжні розрахунки» | показує секції `intermediate: true` (μ, NCV, M, W) |
| клік / Enter на числі | «Чому це число?» (§10.3) |
| чіп `ᴾᴵ` біля об'єму | `fetched` без кольору; `partial`/`noData` — `warn`; `manual` — іконка олівця; `stale` — `info` |

**Стани:**

| Стан | Вигляд |
|---|---|
| завантаження | скелет таблиці: 2 рядки шапки + 12 рядків |
| порожньо | «За 2026 рік записів факелювання немає» + дія «Відкрити реєстр подій» |
| фільтр нічого не дав | «Жоден запис не відповідає фільтру аркуша» + «Скинути місяці» |
| помилка | `ErrorState`: код, correlation id з Copy, Retry |
| застаріло | банер `warn`: «Входи змінилися після розрахунку 27.09 20:02 (серпень)» + **Перерахувати** |
| лише читання | представлення завжди лише для читання; підказка «Змінити значення — у реєстрі подій» з посиланням на рядок |
| без права на розрахунки | рядки `calc_*` з замком і підказкою `viewer.noCalcAccess` |
| частково PI | чіпи на числах + лічильник у смузі свіжості |
| застаріла версія представлення | `warnings` зі шляхом; `#REF` у клітинці з підказкою |

**Клавіатура:** стрілки — по клітинках (патерн ARIA `grid`, roving tabindex); `Home`/`End`
— край рядка; `Ctrl+Home` — перша клітинка; `PgUp`/`PgDn`; `Enter` чи `Alt+W` — «Чому?»;
`Esc` закриває верхній шар і повертає фокус; `[`/`]` згортають/розгортають секцію під
курсором; `Ctrl+F` — пошук рядка; `Ctrl+E` — експорт.

### 10.3 «Чому це число?»

Права шухляда (`DetailDrawer`, `?panel=why:<ref>`), та сама на сітці документа
(нова вкладка інспектора поруч із панеллю розрахунків, `DocumentPage.tsx:715`) і в представленні.

```
┌ Why this number? ─────────────────────────────── ✕ ┐
│ SO2 · g/sec · JAN event 1 (28.01 14:09:20)          │
│ 95.885 g/s                         HSE301.FLARE 1.0 │
├─────────────────────────────────────────────────────┤
│ gsec = CONVERT(!tons, 't', 'g') / @DurationSec      │
│  ├ tons (SO2) ............... 0.0891735 t      ›    │
│  │  = M_t · (K_MASS + K_S·S_wt·ETA + (1−ETA)·W/100) │
│  │  ├ M_t .................. 0.2581915 t      ›     │
│  │  │  ├ V_Sm3 ............. 269.258 Sm3      ›     │
│  │  │  │  └ Volume_Sm3 · PI Total                   │
│  │  │  │     HP_Flare_IA|Flow  28.01 14:09:20–14:24:50 (UTC+5)
│  │  │  │     42 points · 100 % good · local integral│
│  │  │  │     Sm3/h·s → Sm3 ×0.000277778 · 27.09 20:00│
│  │  │  └ Rho20 ............. 0.9589 kg/Sm3          │
│  │  │     entered by D. Akhmetova 03.02 10:12       │
│  │  ├ K_S (SO2) ............ 0.02   2026-01-01 → ∞  │
│  │  ├ S_wt ................. 17.2965447 wt%         │
│  │  └ ETA .................. 0.9984                 │
│  └ DurationSec ............. 930 s                  │
│     = ROUND(([End] − [Start]) · 86400, 0)           │
├─────────────────────────────────────────────────────┤
│ [Copy explanation]   Open row in register ›         │
└─────────────────────────────────────────────────────┘
```

- Дерево розкривається на вимогу (`›`); крихти вгорі — шлях назад.
- Константа: значення, одиниця, вікно чинності, речовина, джерело, версія методології.
- Вхід, що змінився після розрахунку, — рядок `info` «змінилося після розрахунку: було 0.9589,
  зараз 0.9601» + «Перерахувати».
- Клітинка зведення (max/Σ/YTD): спершу функція й фільтр, далі записи-внески (для `max` —
  переможець і наступні три), кожен веде в дерево свого запису (`…/views/{code}/explain`).
- «Copy explanation» копіює дерево текстом (для листа чи рецензії).
- Стани: завантаження (скелет дерева), `ECR-CALC-0404` («Розрахунку ще не було —
  Перерахувати»), без права (`ForbiddenState` з назвою `Calculation.View`).

### 10.4 Реєстр подій

Сітка аркуша `EVENTS` (чинний `DocumentGrid`) показує ≤ 10 ключових колонок: походження,
початок, кінець, тривалість, факел, категорія, газ, потік, об'єм, `M_t`. Решта — у шухляді
запису. Події приходять із PI самі (§4.7), тож головна дія сторінки лишається для того, чого
в PI немає, — **«Нова подія»**. «Отримати з PI зараз», «Отримати з PI за період…» і «Додати
безперервні джерела за місяць» — у «More». `[PI]` у макеті нижче — позначка поля з PI,
замкненого до «Виправити вручну»; `[edited]` — поле, виправлене людиною.

**Походження рядка** — чіп у першій колонці (дані — `GET …/source-events`, §9.1):

| Стан | Чіп | Тон | Що може людина |
|---|---|---|---|
| `Synced` | «PI» | без кольору | виправити поле вручну |
| `Synced` + `KeptManual` | «PI · змінено вручну» з олівцем | без кольору | «Повернути значення з PI» для кожного поля |
| `Missing` | «Зникла в PI» | `warn` | «Не враховувати» або лишити як є |
| `PeriodChanged` | «У PI перенесена на {місяць}» | `warn` | перенести вручну (нова подія в тому місяці + «Не враховувати» тут) |
| `Unmapped` | «PI: значення без відповідника» | `warn` | обрати значення вручну; адміністратору — посилання на мапінг |
| рядок без прив'язки | «Вручну» | без кольору | усе, як у V1 |

**Що лише для читання.** `SourceEventId` і формульні колонки — завжди. Поле, що прийшло з PI,
показується з позначкою PI і **замкненим** до явної дії «Виправити вручну» (олівець у полі
чи `F2` на комірці сітки): випадковий ввід не має мовчки відключати поле від PI. Після правки
поле живе як ручне (D-118, `KeptManual`), доки людина не натисне «Повернути значення з PI».
Рядок події поза PI редагується весь, як сьогодні.

```
┌ Flaring event · from PI ───────────────────────── ✕ ┐
│ SOURCE  PI event HP_Flare_IA 2026-01-28 14:09:20    │
│         synced 27.09 20:00 · 1 field edited manually│
│ WHEN    Start 28.01.2026 14:09:20 [PI]  End 14:24:50 [PI]
│ WHAT    Category V8 [PI]   HMB 370 Winter [PI]      │
│         Flare HP Flare – Island A [edited]  [Revert to PI value]
│ VOLUME  269.258 Sm3 ● From PI · Total over the row window
│ ☐ Do not count this event                           │
└─────────────────────────────────────────────────────┘
```

```
┌ New flaring event ─────────────────────────────── ✕ ┐
│ WHEN                                                │
│  Start [28.01.2026 14:09:20]   End [28.01.2026 14:24:50]
│  Duration 00:15:30 (930 s)  · period: January 2026  │
│ WHERE                                               │
│  Flare [HP Flare – Island A ▾]  Unit [Unit 100 ▾]   │
│  Description [Production Wellhead B1-100-DW-001   ] │
│ WHAT                                                │
│  Kind (•) Event ( ) Pilot ( ) Purge   Category [V8 ▾]
│  Gas [SG ▾]  Nitrogen [No ▾]                        │
│  Stream [1D-2 · HP Separator Gas ▾]  HMB [370 Winter ▾]
│ VOLUME                                              │
│  269.258 Sm3   ● From PI · 27.09 20:00  [Fetch again]
│  ( ) Enter manually                                  │
│ COMPOSITION (until registry)   ▸ ρ 0.9589 · μ 23.05 · S 17.2965 …
│ PREVIEW                                  recalculates as you type
│  M 0.25819 t · SO2 0.08917 t · SO2 95.885 g/s · Wist/Wsnd 0.0012
│  Limits: volume 95 418 Sm3 · SO2 35 398.343 g/s — not exceeded
├─────────────────────────────────────────────────────┤
│                          [Cancel]  [ Save event ]   │
└─────────────────────────────────────────────────────┘
```

| Деталь | Рішення |
|---|---|
| дата-час | `@mantine/dates` `DateTimePicker` з секундами, локаль продукту (заборона `<input type="date">`, DIRECTIVE-15 §2) |
| період | обчислюється з початку за TZ проєкту; закритий період — поле з помилкою `ECR-PRD-0409` до збереження |
| потік | пошуковий `Select` (номер + назва); неоднозначна назва (Train 1/2) — з номером у підписі |
| кейс HMB | список кейсів обраного потоку; зміна потоку скидає кейс |
| об'єм | чіп статусу: `fetched` / `partial` (`warn`, % Good) / `noData` (`warn`, «PI не має даних за вікно») / `pending` / `manual` (олівець); «Enter manually» знімає прив'язку лише для цієї комірки — ручна правка далі захищена D-118 |
| попередній розрахунок | `preview-calculation` з debounce 400 мс; помилки входів — під полями, а не тостом |
| клавіатура | `Ctrl+Enter` зберегти, `Esc` закрити (`UnsavedGuard`), `Alt+N` нова подія зі сторінки |
| стани | новий / редагування / лише читання (поданий аркуш) / помилка збереження (`saveErrors.ts`) / конфлікт версії (`ConflictPanel.tsx`) |
| подія з PI | заголовок «Flaring event · from PI» і секція SOURCE (подія, остання синхронізація, скільки полів виправлено); поля з PI замкнені до «Виправити вручну»; «Повернути значення з PI» — `POST …/source-events/{linkId}/revert` |
| зникла в PI | банер `warn` «Подію не знайдено в PI під час синхронізації {time}. Рядок лишається» + прапорець «Не враховувати» (колонка `Exclude`) |
| клавіатура (подія з PI) | `F2` на замкненому полі — «Виправити вручну»; `Alt+R` — «Повернути значення з PI» для поля під фокусом |

Історія 2026: «Отримати з PI за період…» (`POST …/source-events/sync` з `from`/`to`)
підтягує події року тим самим шляхом, що щоденна синхронізація; для подій, яких у PI немає,
лишається чинний імпорт Excel у динамічну таблицю з пласким шаблоном (рядок = подія).
Транспонований SSRS-файл напряму не імпортується.

### 10.5 Конструктор представлення

Маршрути: `/admin/templates/:id/views` (перелік) і `/admin/templates/:id/views/:code`
(редактор; шаблон сторінки-редактора KIT §4). Кроки — вертикальним `Stepper` ліворуч,
праворуч живий перегляд.

```
┌ ← HSE301 · Views ─ HSE301.Year v1.1 (draft) ─────────── [Save draft] Publish  More ▾ ┐
├──────────────────────┬──────────────────────────────────────────────────────────────┤
│ 1 Source         ✓   │ Sheet [AI_Int_SG_V8 ▾]  + Add sheet   Preset: [Event matrix ▾] │
│ 2 Orientation    ✓   │ ┌ Rows ─────────────────────────┐  ┌ Attributes ──────────────┐│
│ 3 Column groups  ✓   │ │ ≡ ▾ Event                     │  │ ⌕ search                  ││
│ 4 Row sections   ●   │ │ ≡    Area                     │  │ Table columns             ││
│ 5 Totals & pivots    │ │ ≡    Start (date)             │  │   Start · End · Flare …   ││
│ 6 Format             │ │ ≡ ▾ Volume                    │  │ Calculation (HSE301.FLARE)││
│ 7 Preview            │ │ ≡    Gas Volume  fixed:3 bold │  │   V_Sm3 · M_t · tons × 11 ││
│                      │ │ ≡ ▸ tons (11)                 │  │ Derived (Report rule)  +  ││
│                      │ └───────────────────────────────┘  └───────────────────────────┘│
│                      │ Preview on [DOC-000123 ▾] [2026 ▾]            ⚠ 1 warning      │
│                      │ ┌ живий перегляд тим самим компонентом, що переглядач ────────┐│
└──────────────────────┴──────────────────────────────────────────────────────────────┘
```

| Крок | Що обирає людина | Під капотом |
|---|---|---|
| 1 Джерело | таблиця(і), «цей період / рік», фільтр конструктором умов (поле — оператор — значення) або виразом | `source` |
| 2 Орієнтація | «записи в стовпцях» / «записи в рядках» / «зведення» | `kind` |
| 3 Групи стовпців | «за місяцем → номер», `Total` групи (які рядки й функція), ховати порожні | `columns.levels` |
| 4 Секції рядків | перетягування атрибутів із палітри в секції; **без миші**: «Додати в секцію», `Alt+↑/↓` переміщення | `sections` |
| 5 Підсумки й зведення | **пресети**: «Зведення тонн (як HP_LP_tons)», «Зведення г/с (як HP_LP_gsec)» — генерують дерево листків; далі правка листка: фільтр, функція | `pivot` |
| 6 Формат | знаки, дата/час/тривалість, тон секції, білінгвальні підписи (en/ru/kk) | `format`, `label` |
| 7 Перегляд | реальний документ і рік; різниця з опублікованою версією | `POST …/preview` |

«More»: Duplicate sheet, Export JSON, Import JSON. Публікація — `ConfirmDialog` із
переліком попереджень. Стани: чернетки немає → «Створити чернетку з v1.0»; помилки макета —
банер зі шляхами й фокус на перше хибне поле (L9).

### 10.6 Налаштування PI: події й вікно рядка

Дві вкладки в редакторі таблиці версії шаблону (`features/templates/TableEditor.tsx` — лише
підключення вкладок): **«Події з PI»** (`features/integration/sourceEvents/**`, крок A6) —
звідки беруться самі рядки, і **«Дані з PI»** (`features/integration/rowWindow/**`, крок A2) —
звідки об'єм за вікном рядка, якщо `VolumeMode = RowWindow`.

#### «Події з PI»

```
┌ Table FLARE_RECORD · Events from PI ─────────────────────────── [ Save ] ┐
│ Source     [PI SQL Client · NCOC ▾]   Template [ HP flare events ▾ ] (catalog…)
│ Document   [DOC-000123 HSE301 Offshore Island A ▾]                         │
│ Only events where  [Area (event) ▾] = [Island A      ]                     │
│ ┌ Column ───────┬ PI attribute ───────────────┬ Where ──┬ How ──────────┐  │
│ │ Start *       │ event start time            │ event   │ as is         │  │
│ │ End *         │ event end time              │ event   │ as is         │  │
│ │ Category      │ TUGF_Category  (catalog…)   │ event ▾ │ by code ▾     │  │
│ │ HmbCase       │ HMB_Case       (catalog…)   │ event ▾ │ by name ▾     │  │
│ │ Flare         │ Flare          (catalog…)   │ element▾│ value map (3) │  │
│ │ Volume_Sm3    │ — row window (tab "PI data")│         │               │  │
│ │ + Map a column                                                        │  │
│ └───────────────┴─────────────────────────────┴─────────┴───────────────┘  │
│ Volume   ( ) event attribute  (•) total over the row window  ( ) manual    │
│ Schedule every 15 min · look back 7 days · last run 27.09 20:00 ✓  [Run now]│
│ [ Test on recent events ▶ ]                                                │
└────────────────────────────────────────────────────────────────────────────┘
```

(Імена атрибутів у макеті ілюстративні: справжні обираються з каталогу, §4.7.3.)

| Елемент | Поведінка |
|---|---|
| шаблон, атрибути | пікер каталогу `GET …/event-templates`; колонка «Where» — атрибут події чи первинного елемента (HQ-18); ручного введення імені немає |
| «How» | `Direct` для дат, чисел, тексту; для Lookup — «за кодом», «за назвою» або «таблиця відповідностей» (редактор пар «значення PI → запис довідника», підказує незіставлені значення з останньої перевірки) |
| обов'язкові | `Start` і `End`; без них «Save» вимкнено з поясненням |
| «Test on recent events» | `probe-events` на реальних подіях за 7 днів: таблиця подій з розв'язаними значеннями; незіставлені значення — `warn` з дією «Додати відповідність». Нічого не пише |
| стани | запит подій не налаштовано на середовищі (`.eventQueryNotConfigured`) — `Banner info` з назвою ключа `PiSqlClient:EventQuery` і посиланням на інструкцію; джерело недоступне (`ECR-INT-0503`, Retry); відмова автентифікації (`ECR-INT-0502`, без Retry); шаблон зник із каталогу (`ECR-INT-0404` + `suggestions`) |
| клавіатура | таблиця мапінгу — `Tab` по полях, `Alt+↓` відкриває пікер, `Ctrl+Enter` — новий рядок мапінгу, `Ctrl+S` — зберегти |

#### «Дані з PI»

```
┌ Table FLARE_RECORD · PI data by row window ───────────────────────── [ Save ] ┐
│ Target column   [Volume_Sm3 ▾]  unit Sm3 · scale 6 ✓                          │
│ Window          Start [Start ▾]   End [End ▾]   time zone of project: Asia/Atyrau│
│ Query           ( ) Raw points folded here  (•) Summary   Type [Total ▾]  ☐ Step │
│ Source by       [PiSourceKey ▾]                                               │
│ ┌ Selector value ───┬ PI attribute ─────────────────────┬ Unit ──┬ Test ─┐     │
│ │ EVENT/HP_IA       │ CPF\Flare\HP_IA|Flow   (catalog…) │ Sm3/h  │  ▶   │     │
│ │ PURGE/HP_IA       │ CPF\Flare\HP_IA|PurgeFlow         │ Sm3/h  │  ▶   │     │
│ │ PILOT/HP_PILOT    │ — not from PI (calculated)        │   —    │      │     │
│ └───────────────────┴───────────────────────────────────┴────────┴──────┘     │
│ ▸ Advanced: min % good 95 · refetch late data within 7 days                    │
└────────────────────────────────────────────────────────────────────────────────┘
```

(Шляхи атрибутів у макеті ілюстративні: справжні обираються з каталогу, в пакеті I1 їх немає.)

**«Перевірити на прикладі» (▶):** модальне вікно — обрати рядок реального документа або
ввести вікно вручну → значення (`local`, і `server`, якщо налаштовано), одиниця й
коефіцієнт, точок, % Good, прогалини, перші/останні точки. Розбіжність `server` і `local`
понад 0.1 % — банер `warn` «Серверний summary відрізняється — перевірте одиницю часу (HQ-16)».
Атрибут обирається чинним `PiAfCatalogPicker` (`features/mapping/PiAfCatalogPicker.tsx`).
Стани: джерело недоступне (`ECR-INT-0503` з Retry), шляху немає (`ECR-INT-0404` +
`suggestions`), відмова автентифікації (`ECR-INT-0502`, без Retry).

### 10.7 Компоненти

| Перевикористовується | Нове |
|---|---|
| `shared/ui/PageHeader`, `DetailDrawer`, `AsyncBoundary` (`EmptyState/ErrorState/ForbiddenState`), `Banner`, `ConfirmModal`, `StatusBadge`, `FilterBar`, `Timestamp`, `CodeText`, `KeyValue`, `useUrlState`, `UnsavedGuard` | `features/views/viewer/ViewTable.tsx` (нативна таблиця, липка шапка, секції) |
| `shared/format` (числа, дати, локаль продукту) | `features/views/viewer/ViewSheetNav.tsx`, `ViewToolbar.tsx`, `FreshnessStrip.tsx`, `viewApi.ts` |
| `features/grid/DocumentGrid.tsx` (реєстр подій — без змін) | `features/views/constructor/**` (`ViewConstructorPage`, `SectionEditor`, `PivotPresetPicker`, `AttributePalette`) |
| `features/mapping/PiAfCatalogPicker.tsx`, `PiAfProbeAction.tsx` | `features/trace/WhyPanel.tsx`, `TraceNode.tsx`, `traceApi.ts` |
| `features/methodologies/CalculationResultsPanel.tsx` (групування Output/Intermediate) | `features/events/EventEditorDrawer.tsx`, `VolumeStatusChip.tsx`, `eventsApi.ts` |
| `features/documents/DocumentToolbar.tsx` (перемикач «Таблиці \| Представлення») | `features/integration/rowWindow/RowWindowTab.tsx`, `ProbeWindowModal.tsx`, `rowWindowApi.ts` |
| `features/mapping/PiAfCatalogPicker.tsx` (зразок пікера каталогу) | `features/integration/sourceEvents/SourceEventsTab.tsx`, `EventTemplatePicker.tsx`, `ValueMapEditor.tsx`, `ProbeEventsModal.tsx`, `sourceEventsApi.ts`; `features/events/EventOriginChip.tsx`, `LockedPiField.tsx` |

### 10.8 Доступність і теми

- `<table role="grid">` з `aria-rowcount`/`aria-colcount`; клітинки шапки — `<th scope="colgroup"
  colspan>` і `<th scope="col">`; перший стовпець — `<th scope="row">`; кнопки згортання
  секцій — `aria-expanded` і `aria-controls` на `<tbody>`.
- Тон — завжди текст і іконка, не лише колір: «да» + іконка тривоги, `bad`.
- Кольори — лише токени (`--danger`, `--warning`, `--sunken`, `--grid-line`), пари «текст/тло»
  нових станів — рядками в `contrast.test.ts` (4.5 для тексту, 3 для кільця фокуса).
- a11y-гейти `a11y (dark)` і `a11y (light)`: новий файл `src/test/accessibility.part5.a11y.test.tsx`
  з переглядачем, «Чому?», шухлядою події, конструктором і вкладками «Події з PI» й «Дані з PI».
- Замкнене поле з PI — `aria-readonly="true"` і `aria-describedby` з текстом «From PI. Press F2
  to edit manually»; чіп походження — текст, не лише колір.
- Щільність `compact`/`comfortable` — висота рядка з `--row`.

### 10.9 Ключі текстів (сід — лише `en`, DIRECTIVE-15 §6)

| Префікс | Приклади (`en`) |
|---|---|
| `viewer.*` | `viewer.switchTables` «Tables», `viewer.switchViews` «Views», `viewer.months` «Months», `viewer.findRow` «Find a row», `viewer.showIntermediate` «Show intermediate results», `viewer.export` «Export to Excel», `viewer.print` «Print», `viewer.total` «Total», `viewer.empty.noRecords` «No flaring records for {year}», `viewer.empty.openRegister` «Open the flaring register», `viewer.stale` «Inputs changed after the calculation of {time}», `viewer.recalculate` «Recalculate», `viewer.noCalcAccess` «You need Calculation.View to see calculated values», `viewer.freshness.piWindow` «PI {time}», `viewer.freshness.calc` «Calculated {time}», `viewer.freshness.edit` «Edited {time} ({user})», `viewer.collapseSection` «Collapse {name}» |
| `why.*` | `why.title` «Why this number?», `why.formula` «Formula», `why.piWindow` «PI {summary} over {from}–{to}», `why.points` «{count} points · {good}% good», `why.computedLocal` «computed here from raw points», `why.computedServer` «PI server summary», `why.manual` «Entered by {user} on {time}», `why.constant` «Constant {code}, valid {from} → {to}», `why.changedAfter` «Changed after the calculation: was {old}, now {new}», `why.copy` «Copy explanation», `why.openRow` «Open row in register», `why.aggregate` «{fn} over {count} records» |
| `events.*` | `events.new` «New event», `events.addContinuous` «Add continuous sources for {month}», `events.when` «When», `events.where` «Where», `events.what` «What», `events.volume` «Volume», `events.preview` «Preview», `events.volumeStatus.fetched` «From PI · {time}», `.partial` «Partial PI data ({good}%)», `.noData` «No PI data for this window», `.pending` «Fetching from PI…», `.manual` «Entered manually», `events.fetchAgain` «Fetch again», `events.enterManually` «Enter manually»; походження: `events.origin.pi` «PI», `.piEdited` «PI · edited manually», `.missing` «Missing in PI», `.periodChanged` «Moved to {month} in PI», `.unmapped` «PI value without a match», `.manual` «Manual»; `events.syncNow` «Get from PI now», `events.syncRange` «Get from PI for a period…», `events.editManually` «Edit manually», `events.revertToPi` «Revert to PI value», `events.doNotCount` «Do not count this event», `events.missingBanner` «This event was not found in PI during the sync at {time}. The row stays.» |
| `sourceEvents.*` | `sourceEvents.tab` «Events from PI», `.template` «Template», `.onlyWhere` «Only events where», `.where.event` «event», `.where.element` «element», `.how.direct` «as is», `.how.byCode` «by code», `.how.byName` «by name», `.how.valueMap` «value map», `.volume.eventAttribute` «Event attribute», `.volume.rowWindow` «Total over the row window», `.volume.manual` «Manual», `.test` «Test on recent events», `.addMatch` «Add a match», `.notConfigured` «The PI event query is not configured on this environment ({key}).» |
| `rowWindow.*` | `rowWindow.tab` «PI data by row window», `rowWindow.target` «Target column», `rowWindow.window` «Window», `rowWindow.query.raw` «Raw points folded here», `rowWindow.query.summary` «Summary», `rowWindow.summary.Total` «Total (time integral)», `.Average` «Time-weighted average», `rowWindow.isStep` «Step values», `rowWindow.sourceBy` «Source by», `rowWindow.test` «Test on an example», `rowWindow.serverDiffers` «Server summary differs from the local integral by {pct}% — check the rate time unit» |
| `viewDef.*` | `viewDef.step.source` «Source», `.orientation`, `.columnGroups`, `.sections`, `.totals`, `.format`, `.preview`; `viewDef.preset.tonsSummary` «Tons summary (as HP_LP_tons)», `viewDef.preset.gsecSummary` «g/s summary (as HP_LP_gsec)», `viewDef.preset.eventMatrix` «Event matrix» |
| `err.*` | §9.3 |

Підписи рядків і стовпців представлення — **дані** (`LocalizedText` у `LayoutJson`), а не ключі каталогу.

---

## 11. План виконання

### 11.1 Правила, за якими складено план (CLAUDE.md §1–§5)

- Foundation (спільні ресурси, міграції, коди, одиниці) — **послідовно**; реалізації —
  паралельно ≤ 3–4 гілки **без перетину «Файлів для запису»**; інтеграція — послідовно.
- ≤ 300–400 рядків diff на крок; «ендпоінт + споживач + тести» — до ~800 рядків, якщо тести
  ≥ 40 % (без `openapi.snapshot.json`, `schema.d.ts`, файлів міграцій). Розмір і частку
  тестів називати в коміті.
- **Одна міграція на крок**, міграції строго по черзі (M1 → M5): кожна переписує
  `EcrDbContextModelSnapshot.cs`, і паралельні міграції конфліктують саме там. Черга **спільна**
  з міграціями FEATURE-REGISTRY-TABLES (`RK01…RK05`) — §11.3.
- Нова таблиця чи колонка — рядок у `docs/build/02a-db-schema.md` **у тому самому кроці**, що
  й міграція, і не раніше: `PhysicalModelTests.Кожна_таблиця_контрактної_схеми_існує_або_явно_відкладена`
  читає `CREATE TABLE` з 02a й вимагає, щоб таблиця вже існувала.
- Рефакторинг — окремим кроком, без зміни поведінки (B0a, B0b).
- Для кожного DoD — **мутаційний доказ**: названа поломка, на якій тест червоніє.
- Спільні файли (`09-seed.sql`, `openapi.snapshot.json`, `schema.d.ts`, `shared/ui/**`,
  `app/routes.ts`, `Infrastructure/DependencyInjection.cs`): лише дописування в кінець
  своєї секції `-- HSE301:<крок>` / `// HSE301:<крок>`, попередження іншій сесії;
  контракт перегенеровується останнім кроком після rebase.
- Перед пушем — **повний** прогін (`dotnet test` по кожному проєкту, `npm test`, `npm run lint`,
  `npm run typecheck`), а не лише свої каталоги: сторожі читають чужі файли.
- Там, де зачеплено шлях користувача, — `tools/smoke.ps1` і `tools/e2e-stand.ps1` з
  **окремого worktree** (`git worktree add … --detach` + `dotnet restore Ecr.sln`).

### 11.2 Таблиця

| # | Підзадача | Файли для запису | Залежить від | DoD (як перевіримо) | Режим |
|---|---|---|---|---|---|
| F0 | **D16-03 — згортка в межах періоду** (чужий крок) — ✓ **виконано в `3e6d2efa`** (§4.0) | за [DIRECTIVE-16 §1 D16-03](DIRECTIVE-16.md) | — | за D16-03 | послідовно, передумова (знята) |
| F1 | Одиниці 301 у сіді: розмірності `StdVolume`, `StdVolumeFlow`, `Velocity`, `Area`, `MassPerStdVolume`, `EnergyPerStdVolume`, `EnergyPerMass`, `MassPerEnergy`; одиниці `Sm3`, `Sm3_per_h`, `Sm3_per_s`, `kt`, `m_per_s`, `m2`, `pct_vol`, `pct_wt`, `MJ_per_Sm3`, `MJ_per_kg`, `kg_per_Sm3`, `t_per_t`, `kg_per_TJ`, `g_per_mol` (~150 р.) | `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql` (секція `-- HSE301:F1`), `src/Ecr.Calculations/MethodologyEvaluationContext.cs` (лише `UnitTable.Seed`, `:220-235`), `tests/Ecr.Calculations.Tests/Hse301UnitsTests.cs` | — | `Sm3/h × 930 s → Sm3`, `kg → t`, `t → g`, `s → h` точні; `Sm3 ↔ m3` — `#UNIT`. **Мутація:** `FactorToBase` для `Sm3_per_h` = 1/60 → тест червоний. `verify-sql-scripts.ps1` зелений | послідовно |
| F2 | ✓ **виконано** (§4.1) — `TimeWeightedAvg`, `TimeIntegral`, `IsStep`: чиста згортка + **міграція M1** (`CK_EFM_Transform`, `EntityFieldMap.IsStep`) (~350 р.) | `src/Ecr.Application/Sources/PeriodFold.cs`, `src/Ecr.Domain/Entities/External/EntityFieldMap.cs`, `src/Ecr.Infrastructure/Persistence/Configurations/ExternalConfiguration.cs`, міграція `…_HSE301M1TimeWeighted.cs`, `docs/build/02a-db-schema.md`, `tests/Ecr.Application.Tests/Sources/PeriodFoldTimeWeightedTests.cs` | F0 | ряд `[0 @0 с, 10 @10 с, 10 @20 с]` на `[0,20)`: avg = 7.5, інтеграл = 150; ступінчастий — 100; межа з точкою до вікна; прогалина не входить. **Мутація:** трапеція → сума точок — 4 тести червоні | послідовно |
| F3 | Конверсія на межі в Application + матеріалізація: точки на межах вікна, нові згортки, `Converted` у журналі (~380 р.) | `src/Ecr.Application/Sources/BoundaryUnitConversion.cs` (новий), `src/Ecr.Adapters.PiAf/SourceUnitConverter.cs` (делегує), `src/Ecr.Infrastructure/Jobs/MaterializeCollectedDataJob.cs`, `tests/Ecr.Application.Tests/Sources/BoundaryUnitConversionTests.cs`, `tests/Ecr.Infrastructure.Tests/Jobs/MaterializeTimeWeightedTests.cs` | F1, F2 | місячний Total `Sm3/h` лягає в `Sm3`, коефіцієнт у `itg.CollectionCoverage`; `Avg` старих мапінгів не змінився. **Мутація:** прибрати виклик конверсії → тест «у комірці Sm3» червоний | послідовно |
| F4 | Порт: `SourceQueryKind`, `SourceSummaryKind`, `WindowRequest/Result`, типовий `ReadWindowAsync` через `WindowFold`; PI SQL Client: `Quality`, ключі `InterpolatedQuery`/`SummaryQuery` без типового тексту (~400 р.) | `src/Ecr.Application/Ports/IExternalDataSource.cs`, `src/Ecr.Application/Sources/WindowFold.cs` (новий), `src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs`, `tests/Ecr.Application.Tests/Sources/WindowFoldTests.cs`, `tests/Ecr.Adapters.Tests/PiSqlClientWindowTests.cs` | F2 | локальне вікно = `PeriodFold` на тих самих точках; без ключа `Interpolated` → `ECR-INT-0422`; точка `Quality=Bad` не входить. **Мутація:** типова реалізація без точки до вікна → тест «подія без точок усередині» дає 0 і червоніє | послідовно |
| F4e | Порт подій: `DiscoverEventTemplatesAsync`, `ReadEventsAsync`, записи `SourceEvent*` з типовою реалізацією-відмовою; PI SQL Client: ключі `PiSqlClient:EventQuery`/`EventTemplateQuery` **без типового тексту**, розгортання довгої форми (рядок на атрибут) у події чистою функцією `SourceEventFolder`, `Literal` для `{template}`, стеля `MaxEvents`, UTC (~400 р., тести ≥ 40 %) | `src/Ecr.Application/Ports/IExternalDataSource.cs` (адитивно, після F4), `src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs`, `src/Ecr.Adapters.PiAf/SourceEventFolder.cs` (новий), `09-seed.sql` (секція `-- HSE301:F4e`: `err.ECR-INT-0422.eventQueryNotConfigured` — ключ кидає вже цей крок), `tests/Ecr.Adapters.Tests/PiAf/PiSqlClientEventTests.cs`, `tests/Ecr.Adapters.Tests/PiAf/SourceEventFolderTests.cs` (нові) | F4 | без ключа → `ECR-INT-0422` `.eventQueryNotConfigured`; два EF з однаковою назвою, але різними ID — дві події; атрибути `E` і `P` розведені за областю; повний батч → `Truncated`; `SqlDataSource` і `PiWebApiDataSource` не змінені й компілюються. **Мутація:** групувати за `EventName` замість `EventId` → тест «однакові назви» червоний | послідовно |
| F5 | **Міграція M2:** `ext.RowWindowMap`, `RowWindowSource`, `RowWindowValue` + домен + конфігурації + партиція (~350 р.) | `src/Ecr.Domain/Entities/External/RowWindowMap.cs` (новий), `src/Ecr.Infrastructure/Persistence/Configurations/RowWindowConfiguration.cs` (новий), `src/Ecr.Infrastructure/Persistence/EcrDbContext.cs` (3 `DbSet`), `src/Ecr.Infrastructure/Persistence/Sql/07-partition-tables.sql` (рядок `RowWindowValue`), міграція `…_HSE301M2RowWindow.cs`, `docs/build/02a-db-schema.md` (3 `CREATE TABLE`), `tests/Ecr.Domain.Tests/External/RowWindowMapTests.cs` | F4e | інваріанти: Start/End — Date, ціль — Decimal, дубль селектора → доменна відмова; партиція за `PeriodKey`. **Мутація:** зняти перевірку типу Start → тест червоний | послідовно |
| F6 | **Міграція M3:** `MethodologyFormula.IsVisible/Scope`, `MethodologyOutput.IsPerSubstance`, `CalculationResult.Kind`, `CalculationStep.DocumentId/SourceRowKey/SubstanceEntryId`; дзеркало `arc.*` (~300 р.) | `src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs`, `MethodologyOutput.cs`, `CalculationResult.cs`, `CalculationTrace.cs`, `src/Ecr.Domain/Enums/Enums.cs` (у кінець: `MethodologyFormulaScope`, `CalculationResultKind`), `src/Ecr.Infrastructure/Persistence/Configurations/CalculationsConfiguration.cs`, `src/Ecr.Infrastructure/Persistence/Sql/12-archive-tables.sql`, міграція `…_HSE301M3Trace.cs`, `docs/build/02a-db-schema.md` (колонки), `tests/Ecr.Domain.Tests/Calculations/FormulaScopeTests.cs` | F5 | типові значення = поведінка сьогодні (`Substance`, `IsPerSubstance = 1`, `Kind = 0`); архівні таблиці мають нові колонки. **Мутація:** типове `Scope = Row` → чинний `GoldenCalculationTests` червоний | послідовно |
| F7 | **Міграція M4:** `cfg.ViewDef`, `cfg.ViewVersion` (~250 р.) | `src/Ecr.Domain/Entities/Configuration/ViewDef.cs` (новий), `src/Ecr.Infrastructure/Persistence/Configurations/ViewConfiguration.cs` (новий), `EcrDbContext.cs` (2 `DbSet`), міграція `…_HSE301M4ViewDef.cs`, `docs/build/02a-db-schema.md` (2 `CREATE TABLE`), `tests/Ecr.Domain.Tests/Configuration/ViewVersionTests.cs` | F6 | публікація лише з `Draft`; `UQ(TemplateId, Code)`. **Мутація:** дозволити `Publish` з `Published` → тест червоний | послідовно |
| F8 | Коди `ECR-VIEW-*`, нові `messageKey` §9.3 (разом із ключами подій `.eventMap*`, `.sourceEventNotFound`; `.eventQueryNotConfigured` уже заводить F4e), арми middleware (~280 р.) | `src/Ecr.Domain/Errors/ErrorCodes.cs`, `docs/build/02-contracts.md` §7, `09-seed.sql` (секція `-- HSE301:F8`), `src/Ecr.Api/Errors/ExceptionHandlingMiddleware.cs`, `tests/Ecr.Api.Tests/Errors/ViewErrorStatusTests.cs` | F7 | `ContractIntegrityTests`, `ErrorTitleCatalogTests`, `SeedCatalogTextTests` зелені; `ECR-VIEW-0404` віддається як 404. **Мутація:** прибрати арм → статус 422, тест червоний | послідовно |
| F9 | **Міграція M5:** `ext.SourceEventMap`, `SourceEventFieldMap`, `SourceEventValueMap`, `SourceEventLink` + домен + конфігурації (~350 р.) | `src/Ecr.Domain/Entities/External/SourceEventMap.cs`, `SourceEventLink.cs` (нові), `src/Ecr.Domain/Enums/Enums.cs` (у кінець: `SourceEventVolumeMode`, `SourceEventValueKind`, `SourceEventLinkStatus`), `src/Ecr.Infrastructure/Persistence/Configurations/SourceEventConfiguration.cs` (новий), `EcrDbContext.cs` (4 `DbSet`), міграція `…_HSE301M5SourceEvents.cs`, `docs/build/02a-db-schema.md` (4 `CREATE TABLE`), `tests/Ecr.Domain.Tests/External/SourceEventMapTests.cs` | F8 | мапінг без `$start`/`$end` → доменна відмова; ціль — лише динамічна таблиця; `UQ(SourceEventMapId, SourceEventId)`; переходи стану зв'язку (`Synced ↔ Missing`, `Open → Synced`). **Мутація:** дозволити мапінг без `$end` → тест червоний | послідовно |
| A4 | `ICalculationTrigger` + виклик із матеріалізації, дедуплікація (~300 р.) — ✓ **виконано** в `179341ec` (код), `cf006e8b` (тести), `8155273b` (контракт портів). ✎ 2026-09-30: дедуплікацію робить черга, а не тригер (§4.5); тести — `tests/Ecr.Application.Tests/Calculations/CalculationTriggerTests.cs`, `tests/Ecr.Infrastructure.Tests/Jobs/AutoRecalcAfterMaterializeTests.cs`, `tests/Ecr.Infrastructure.Tests/Jobs/CalculationTriggerQueueTests.cs` | `src/Ecr.Application/Calculations/CalculationTrigger.cs` (новий), `src/Ecr.Application/Ports/ICalculationTrigger.cs` (новий), `src/Ecr.Infrastructure/Jobs/MaterializeCollectedDataJob.cs`, `src/Ecr.Infrastructure/DependencyInjection.cs` (секція `// HSE301:A4`), `tests/Ecr.Infrastructure.Tests/Jobs/AutoRecalcAfterMaterializeTests.cs` | F3 | `Applied > 0` → рівно одна задача на `(doc, period)` за будь-якої кількості викликів; закритий період — нуль. **Мутація:** прибрати дедуплікацію → тест «3 виклики = 1 задача» червоний | паралельно з A2, A3a |
| A2 | API прив'язки PI за вікном рядка + `probe-window` + вкладка «Дані з PI» (ендпоінт + споживач, ~750 р., тести ≥ 40 %) | `src/Ecr.Application/Integration/RowWindowMapHandlers.cs`, `ProbeWindowHandler.cs` (нові), `src/Ecr.Api/Controllers/RowWindowMapsController.cs` (новий; `probe-window` — абсолютним маршрутом, `DataSourcesController.cs` не чіпається), `src/Ecr.Web/src/features/integration/rowWindow/**` (нові), `src/Ecr.Web/src/features/templates/TableEditor.tsx` (лише підключення вкладки), `09-seed.sql` (`-- HSE301:A2`, ключі `rowWindow.*`), тести `tests/Ecr.Api.Tests/RowWindowMapsTests.cs`, `src/Ecr.Web/src/features/integration/rowWindow/__tests__/**`, `contracts/openapi.snapshot.json` + `schema.d.ts` (останнім) | F5, F8 | `EndpointCoverageTests`, `OpenApiSnapshotTests` зелені; probe повертає `local` для фейкового джерела; L1/L9 тестами. **Мутація:** не перевіряти `Scale` цілі → тест `targetScaleTooSmall` червоний | паралельно з A4, A3a |
| A3a | Рушій: `Scope`, `IsPerSubstance`, `IsVisible` → `Kind = Intermediate`; перевірка публікації; `rpt` бере лише `Output`; прив'язка колонки до видимої формули (~400 р.) | `src/Ecr.Calculations/GenericCalculationModule.cs`, `src/Ecr.Application/Ports/ICalculationModule.cs`, `src/Ecr.Infrastructure/Persistence/CalculationResultStore.cs` (`WriteResultsAsync`), `src/Ecr.Application/Calculations/PublishMethodologyHandler.cs`, `src/Ecr.Application/Calculations/MethodologyAuthoringHandlers.cs` (перевірка `OutputCode`), `src/Ecr.Infrastructure/Reporting/ReportSnapshotBuilder.cs` (фільтр `Kind`), тести `tests/Ecr.Calculations.Tests/RowScopeTests.cs`, `IntermediateResultsTests.cs`, `tests/Ecr.Infrastructure.Tests/Reporting/SnapshotIgnoresIntermediateTests.cs` | F6 | методологія з 11 речовинами пише `M_t` один раз; `ContentHash` зрізу не змінився від появи проміжних. **Мутація:** прибрати фільтр `Kind` у `ReportSnapshotBuilder` → хеш інший, тест червоний | паралельно з A4, A2 |
| A1 | Виконання прив'язки: `RowWindowFetchJob`, щогодинний `RowWindowRefetchJob`, хук `IRowWindowTrigger` у `PatchCellsHandler`, TZ проєкту, `KeptManual`, виклик `ICalculationTrigger` (~450 р.; ⚠ `PatchCellsHandler.cs` — гарячий файл, лише одна точка виклику) | `src/Ecr.Application/Integration/RowWindowFetch.cs`, `src/Ecr.Application/Ports/IRowWindowTrigger.cs` (нові), `src/Ecr.Infrastructure/Jobs/RowWindowFetchJob.cs`, `RowWindowRefetchJob.cs` (нові), `src/Ecr.Application/Documents/PatchCellsHandler.cs` (1 виклик), `src/Ecr.Api/Startup/RecurringScheduleService.cs` (1 тригер), `DependencyInjection.cs` (`// HSE301:A1`), тести `tests/Ecr.Application.Tests/Integration/RowWindowFetchTests.cs`, `tests/Ecr.Infrastructure.Tests/Jobs/RowWindowFetchJobTests.cs` | A4, F4, F5 | 14:09:20–14:24:50 Asia/Atyrau → вікно UTC 09:09:20–09:24:50; ручна правка лишається, статус `KeptManual`; `End ≤ Start` → `InvalidWindow` без звернення до PI; після запису — задача перерахунку. **Мутація:** вікно в UTC без TZ → тест червоний | паралельно з A3b |
| A3b | Трейс: `ReferenceCollector`, `TraceJson v1`, `CalculationStep.ResultId/DocumentId/RowKey`, запис `calc.CalculationInput` (~400 р.) | `src/Ecr.Calculations/ReferenceCollector.cs` (новий), `src/Ecr.Calculations/TraceRecorder.cs`, `GenericCalculationModule.cs` (після A3a), `src/Ecr.Infrastructure/Persistence/CalculationResultStore.cs` (`WriteTraceAsync`, `:119-157`), `src/Ecr.Calculations/CalculationOutputWriter.cs`, тести `tests/Ecr.Calculations.Tests/TraceJsonTests.cs`, `tests/Ecr.Infrastructure.Tests/Persistence/CalculationInputWrittenTests.cs` | A3a | для `M_t` прикладу A `TraceJson.inputs` = {`V_Sm3`, `Rho20`}, `ResultId` не null; на `TraceLevel.Off` — нічого. **Мутація:** `resultId: null` → тест «крок має результат» червоний | паралельно з A1 |
| A6 | API й вкладка «Події з PI»: каталог шаблонів, `probe-events`, `source-event-maps` (CRUD, пауза), редактор відповідностей значень (ендпоінт + споживач, ~780 р., тести ≥ 40 %) | `src/Ecr.Application/Integration/SourceEventMapHandlers.cs`, `ProbeEventsHandler.cs` (нові), `src/Ecr.Api/Controllers/SourceEventMapsController.cs` (новий; `event-templates` і `probe-events` — абсолютними маршрутами, `DataSourcesController.cs` не чіпається), `src/Ecr.Web/src/features/integration/sourceEvents/**` (нові), `src/Ecr.Web/src/features/templates/TableEditor.tsx` (лише підключення вкладки, після A2), `09-seed.sql` (`-- HSE301:A6`, `sourceEvents.*`), DI (`// HSE301:A6`), тести `tests/Ecr.Api.Tests/SourceEventMapsTests.cs`, `src/Ecr.Web/src/features/integration/sourceEvents/__tests__/**`, контракт — останнім | F4e, F9, A2 | `EndpointCoverageTests`, `OpenApiSnapshotTests` зелені; `probe-events` на фейковому джерелі повертає розв'язані значення й незіставлені; стан «запит не налаштовано» — банер, а не порожній список; «Run now» — чинний `POST /sources/{id}/collect` (`SourcesController.cs:43`). **Мутація:** не перевіряти обов'язковість `$start`/`$end` → тест `eventMapStartEndRequired` червоний | паралельно з A1, A3b |
| A5a | Запис рядків від інтеграції: `ICellPatcher.ApplyIntegrationRowsAsync` — типізовані значення (дата, Lookup, текст, число), `BaseVersion` чинного рядка для оновлення й `null` для нового, відсів комірок `UserEdit` → `KeptManual` (~300 р.) — ✓ **виконано** в `c25065b7` (код), `20d17c31` (тести). ✎ 2026-09-30, понад план: значення, яких колонка не приймає, відсіюються до обробника в `IntegrationWriteResult.Rejected` (інакше одна зіпсована подія відхиляла б увесь пакет); гонка створення (`ECR-ROW-0409 rowKeyExists`) — обмежений повтор; ключ рядка — `IntegrationRowUpsert.EventRowKey` | `src/Ecr.Application/Ports/ICellPatcher.cs` (адитивний метод і запис `IntegrationRowUpsert`), `src/Ecr.Infrastructure/Integration/IntegrationCellPatcher.cs` (новий метод поруч із чинним), `tests/Ecr.Infrastructure.Tests/Integration/IntegrationRowUpsertTests.cs` (новий) | F9 | новий рядок створюється, наявний оновлюється без `ECR-ROW-0409 rowKeysExist`; комірка з останньою правкою людини не перезаписується й повертається в `KeptManual`; Lookup пишеться id запису; чинний `ApplyIntegrationAsync` поводиться як до кроку (його тести без правок). **Мутація:** надсилати `BaseVersion: null` і для наявного рядка → тест «оновлення наявного» червоний (`rowKeysExist`) | паралельно з A4, A2, A3a |
| A5b | Синхронізація подій: `SourceEventSyncJob` (upsert за ID, ключ `EF-…`, TZ проєкту, `Missing` лише за повним прочитанням, закритий період, відкрита подія, `PeriodChanged`, `Unmapped`, фільтр мапінгу), постановка з `CollectionJob`, виклик `ICalculationTrigger` (~400 р.) — ✓ **виконано** в `c406d2ff` (планувальник), `e72afc56` (подія → комірки), `ad666f0b` (задача), `616004ae` (автоперерахунок), `14902df9` (SQL-тести), `4ee86b1c` (постановка з `CollectionJob`), `4b609f1b` (документація). ✎ 2026-09-30 — фактичні файли замість запланованих: `src/Ecr.Application/Integration/SourceEvents/SourceEventSyncPlanner.cs`, `SourceEventPeriods.cs`, `SourceEventRowBuilder.cs`, `src/Ecr.Application/Ports/ISourceEventSyncJob.cs`, `src/Ecr.Domain/Entities/External/SourceEventLink.cs` (`RekeyTo`), `src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs`, `CollectionJob.cs`, `09-seed.sql` (секція `HSE301:a5b`, ключі `jobs.sourceEvents*`); тести `tests/Ecr.Application.Tests/Integration/SourceEventSyncPlannerTests.cs`, `SourceEventRowBuilderTests.cs`, `tests/Ecr.Domain.Tests/External/SourceEventLinkTests.cs`, `tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncJobTests.cs`, `CollectionJobSourceEventsTests.cs`. Відхилення від плану — §4.7.4 «Як реалізовано» і «Звірка з кодом» | `src/Ecr.Application/Integration/SourceEventSync.cs`, `src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs` (нові), `src/Ecr.Infrastructure/Jobs/CollectionJob.cs` (1 виклик після `SaveRunAsync`), DI (`// HSE301:A5b`), тести `tests/Ecr.Application.Tests/Integration/SourceEventSyncTests.cs`, `tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncJobTests.cs` | F4e, A5a, A1, A4 | фейкова подія 09:09:20Z–09:24:50Z з `Category = V8`, `HMB = 370 Winter` → рядок `EF-…` зі `Start` 14:09:20 (Asia/Atyrau), `Category`, `HmbCase`; повтор — нуль змін; ручна правка категорії лишається (`KeptManual`); подія зникла → `Missing`, рядок той самий; той самий прогін з `Truncated` — жодного `Missing`; закритий період — нуль записів; подія без кінця — рядка немає; запис Start/End поставив `RowWindowFetchJob` (хук A1). **Мутація:** ставити `Missing` і при `Truncated` → тест червоний; брати місяць за UTC замість TZ проєкту → подія 31.01 22:00 (UTC+5 — вже 01.02) лягає не в той період, тест червоний | послідовно, після хвилі A2 |
| B0a | **Рефакторинг:** `ReportExpressionChecker` приймає каталог колонок (`IReportColumnCatalog`), поведінка та сама (~200 р.) | `src/Ecr.Expressions/Binding/ReportExpressionChecker.cs`, `src/Ecr.Expressions/Binding/IReportColumnCatalog.cs` (новий), місця створення каталогу `ReportSourceColumns` | F8 | чинні тести діалекту Report зелені **без правок** | послідовно |
| B0b | **Рефакторинг:** `AggregateFunctions` винесено з `ReportLayout` (~150 р.) | `src/Ecr.Application/Reporting/ReportLayout.cs`, `src/Ecr.Application/Reporting/AggregateFunctions.cs` (новий) | B0a | чинні тести `ReportLayout` зелені без правок | послідовно |
| B2 | Рушій представлення: `ViewLayout` (схема, компіляція, перевірка), `ViewRecordReader` (рік, права, результати з речовиною), `RecordMatrixRenderer`; `GET …/views`, `GET …/views/{code}`, `GET …/freshness` (~500 р. бекенд + мінімальний споживач `viewApi.ts`; ≤ 800, тести ≥ 40 %) | `src/Ecr.Application/Views/**` (нові), `src/Ecr.Api/Controllers/DocumentViewsController.cs` (новий), `src/Ecr.Web/src/features/views/viewApi.ts` (новий), тести `tests/Ecr.Application.Tests/Views/**`, `tests/Ecr.Api.Tests/DocumentViewsTests.cs`, контракт — останнім | A3a, F7, B0a | фікстура SG_V8 січень: шапка `JAN` зі `span = 9` + `Total`, `Total` об'єму = 1 177.766; рядок без права — замок. **Мутація:** `hideEmpty` ігнорується → тест «FEB–JUL сховані» червоний | послідовно |
| B3 | Зведення: `PivotRenderer`, `perPeriodRate`, `worstCaseRate`, `rowBlocks`, `blockScope` (~400 р.) | `src/Ecr.Application/Views/PivotRenderer.cs`, `ViewAggregates.cs` (нові), `tests/Ecr.Application.Tests/Views/PivotGoldenTests.cs` | B2, B0b | січень: Continuous HP sum CO = 0.3790546 г/с; V8 HP max SO2 = 142.525; YTD NO2 = 1.0358825 (§12). **Мутація:** ділити на фактичні секунди замість календарних → червоний | паралельно з B1, B5 |
| B1 | «Чому це число?»: `GET …/calculation-trace`, `…/cell-provenance`, `…/views/{code}/explain`; `WhyPanel` у документі й представленні (~750 р., тести ≥ 40 %) | `src/Ecr.Application/Calculations/GetCellTraceHandler.cs`, `src/Ecr.Application/Documents/GetCellProvenanceHandler.cs`, `src/Ecr.Application/Views/ExplainViewCellHandler.cs` (нові), `src/Ecr.Api/Controllers/CalculationTraceController.cs` (новий), `src/Ecr.Web/src/features/trace/**` (нові), `src/Ecr.Web/src/pages/DocumentPage.tsx` (вкладка інспектора), `09-seed.sql` (`-- HSE301:B1`, `why.*`), тести | A3b, A1, B2 | дерево прикладу A містить PI-вікно з точками й константу з вікном; застарілий вхід позначений; L2 (закрито за замовчуванням). **Мутація:** не порівнювати поточне значення з `CalculationInput` → тест «змінилося після» червоний | паралельно з B3, B5 |
| B5 | Переглядач: `DocumentViewPage`, `ViewTable` (нативна, липка шапка, секції), навігатор, чіпи місяців, пошук, перемикач проміжних, смуга свіжості, банер застарілості, клавіатура, друк (~700 р., лише клієнт) | `src/Ecr.Web/src/pages/DocumentViewPage.tsx`, `src/Ecr.Web/src/features/views/viewer/**` (нові), `src/Ecr.Web/src/app/routes.ts` (запис `documentView`), `src/Ecr.Web/src/app/router.tsx`, `src/Ecr.Web/src/features/documents/DocumentToolbar.tsx` (перемикач), `src/Ecr.Web/src/test/accessibility.part5.a11y.test.tsx` (новий), `09-seed.sql` (`-- HSE301:B5`, `viewer.*`) | B2 | `a11y (dark)`, `a11y (light)`, `renderFeedback` зелені; клавіатурний шлях тестом; `th` шапки має `colspan`. **Мутація:** прибрати `scope="colgroup"` → a11y-тест червоний | паралельно з B1, B3 |
| B6 | Експорт XLSX представлення: `ViewExcelWriter`, `POST …/export`, кнопка (~600 р., тести ≥ 40 %) | `src/Ecr.Adapters.Excel/ViewExcelWriter.cs` (новий), `src/Ecr.Application/Views/ExportDocumentViewHandler.cs` (новий), `DocumentViewsController.cs` (після B2), `src/Ecr.Web/src/features/views/viewer/ViewToolbar.tsx` (після B5), тести `tests/Ecr.Adapters.Tests/ViewExcelWriterTests.cs` | B2, B3, B5 | у книзі злиття `C3:K3`, закріплення `B5`, числа — числами, формат `0.000`, аркуш «Document map» з посиланнями. **Мутація:** писати значення текстом → тест «тип комірки Number» червоний | послідовно |
| B7 | Реєстр подій: шухляда події + `preview-calculation` + «Додати безперервні джерела» + статус об'єму (~750 р., тести ≥ 40 %) | `src/Ecr.Application/Calculations/PreviewRowCalculationHandler.cs` (новий), `src/Ecr.Api/Controllers/RowCalculationPreviewController.cs` (новий), `src/Ecr.Web/src/features/events/**` (нові), `DocumentPage.tsx` (після B1), `09-seed.sql` (`-- HSE301:B7`, `events.*`) | B1, A1 | попередній розрахунок прикладу A = 95.885 г/с без запису в БД; закритий період — помилка до збереження. **Мутація:** preview пише результат → тест «нічого не записано» червоний | послідовно |
| B8 | Реєстр подій: походження з PI — чіп стану, замкнені поля з PI з «Виправити вручну» (`F2`), «Повернути значення з PI», банер зниклої події й «Не враховувати», «Отримати з PI зараз / за період»; `GET …/source-events`, `POST …/source-events/sync`, `POST …/source-events/{linkId}/revert` (ендпоінт + споживач, ~750 р., тести ≥ 40 %) | `src/Ecr.Application/Integration/SourceEventLinkHandlers.cs` (новий), `src/Ecr.Api/Controllers/DocumentSourceEventsController.cs` (новий), `src/Ecr.Web/src/features/events/EventOriginChip.tsx`, `LockedPiField.tsx`, `sourceEventLinksApi.ts` (нові), `src/Ecr.Web/src/features/events/EventEditorDrawer.tsx` (після B7), `09-seed.sql` (`-- HSE301:B8`, `events.origin.*`, `events.*`), тести, `accessibility.part5.a11y.test.tsx` (дописати), контракт — останнім | B7, A5b | «Повернути значення з PI» пише значення з `origin = Integration`, і наступна синхронізація знову веде поле; замкнене поле не приймає ввід без `F2`; `Missing` — `warn` з текстом; `sync` з `from`/`to` довше року → `ECR-INT-0422`; a11y обох тем. **Мутація:** `revert` пише з `origin = UserEdit` → тест «після повернення поле знову веде PI» червоний | послідовно |
| B4 | Конструктор представлення: API (`templates/{id}/views…`), сторінка-редактор, пресети зведень, перегляд на реальному документі (~800 р., тести ≥ 40 %) | `src/Ecr.Application/Views/ViewDefinitionHandlers.cs` (новий), `src/Ecr.Api/Controllers/TemplateViewsController.cs` (новий), `src/Ecr.Web/src/features/views/constructor/**` (нові), `routes.ts`/`router.tsx` (після B5), `09-seed.sql` (`-- HSE301:B4`, `viewDef.*`) | B5, B3 | пресет «як HP_LP_gsec» дає дерево листків §8.6; публікація з помилкою макета неможлива, фокус на полі (L9). **Мутація:** не перевіряти `aggregateNotNumeric` → тест червоний | послідовно |
| I1 | Пакет HSE301: довідники §5.3 (з `Season`), шаблон §5.2, методологія §6 (константи з вікнами), **структура** мапінгу подій і прив'язки PI (колонки й режим об'єму; імена шаблону й атрибутів адміністратор обирає з каталогу на середовищі — у пакеті їх немає), представлення 6 аркушів; фікстура 2026 з файлу; золоті тести | `tools/hse301/*.json`, `tools/hse301/Install-Hse301.ps1` (нові), `tests/Ecr.Scenarios.Tests/Hse301/**` (нові: фікстура 94 записів, фейкове джерело подій, `Hse301GoldenTests.cs`, `Hse301SourceEventsScenarioTests.cs`, `Hse301SummaryReconciliationTests.cs`) | B3, B4, B8 | §12: A/B/C, NO2 січня, 2 304 співвідношення зведень — 0 розбіжностей; §12.5 G-E1…G-E6. **Мутація:** `ETA = 0.998` → SO2 A червоний; фейкова подія з `Category = V9` → рядок зникає з аркуша `AI_Int_SG_V8` і з'являється у V9 | послідовно |
| I2 | E2E і знімки: шлях «подія PI (фейк) → рядок → об'єм PI за вікном (фейк) → перерахунок → представлення → Чому? → XLSX» і «виправити вручну → синхронізація не перетирає» | `src/Ecr.Web/e2e/hse301View.spec.ts` (новий), `src/Ecr.Web/e2e/screenshots.spec.ts` (2 знімки: SG_V8 світла/темна) | I1 | `tools/e2e-stand.ps1` і `tools/smoke.ps1` з окремого worktree — зелені; знімки в PR поруч з аркушем файлу | послідовно |
| I3 | Документація: `B24-hse301-view.md`; позначки «реалізовано» в цьому документі. V-1…V-15 уже внесено в реєстр як `D-149`, `D-171…D-184` (2026-09-27); V-16…V-25 — як `D-185…D-194` (за делегуванням, 2026-09-27) | `docs/reference/backend/B24-hse301-view.md`, `docs/build/FEATURE-HSE301-VIEW.md` | I2 | `git grep` посилань на цей документ зелений; `JournalIntegrityTests` зелений | послідовно |
| RG1 | Методологія v1.1 зі складом SG із довідника (§6.5); шаблон v2: `Stream`/`HmbCase` на `STREAM`/`STREAM_CASE`, колонки складу лишаються лише для FG | методологія й шаблон — дані в `tools/hse301/*.json`; тести `Hse301GoldenTests.cs` | I1; FEATURE-REGISTRY-TABLES `RT-23a` (методологія бачить довідник), `RT-23b` (публікація з функціями довідників); дані `STREAM`…`GAS_COMPOSITION` — імпортом `RT-18b` або CSV; `COMPONENT` — профілем `ADDSTREAM_COMPONENTS` (FEATURE-REGISTRY-TABLES §11.4) | ті самі числа A/B/C з точністю DoD §6.5; зведення — 0 розбіжностей; `EF` = значенню файлу на 11 складах. **Мутація:** μ з поля кейсу `MW_HYSYS` (23.05, `Molecular_Weight` HYSYS) замість складу (23.0544679) → червоний (розбір §3); `Round` → `Truncate` у `EF_t_t` → червоний 1D-1/370 Summer | послідовно |
| RG2 | Секція «Склад» представлення: 34 компоненти vol%/wt%, контроль Σ = 100 | `src/Ecr.Application/Views/ViewRecordReader.cs` (атрибути `reg_*`), `tools/hse301/views.json` | RG1; FEATURE-REGISTRY-TABLES `RT-22` (знімок довідників) | рядок «Сумма должна быть равна 100» = 100.0000000000 для 1D-2/370 Winter | послідовно |
| RG3 | Лабораторний аналіз FG: мг/м3 → об.% (22.414), N2 балансом, похідні властивості | `tools/hse301/*.json`, тести | RG1 | склад січня FG збігається з `AI_Cont_FG_V7` (похибка ≤ 0.05 %, розбір §1.4 р.24) | послідовно |
| F4b | (за потреби) PI Web API `summary`/`interpolated` і `ReadEventsAsync` через `eventframes/search` (форма запиту — `as-is/03-pi-af-integration.md` §4) | `src/Ecr.Adapters.PiAf/PiWebApiDataSource.cs`, тести | F4e | серверний Total = локальному на фейковому транспорті; події з фейкового `eventframes/search` = тим самим подіям із PI SQL Client | паралельно будь-коли після F4e |

Разом 36 рядків: F0 — чужий крок (D16-03, виконано в `3e6d2efa`), власних — 35 (F1–F9 і F4e,
A1–A6 з A3a/A3b і A5a/A5b, B0a–B8, I1–I3, RG1–RG3, F4b); F4b — лише за потреби. Джерело подій
(§4.7) додало шість кроків: F4e, F9, A5a, A5b, A6, B8.

### 11.3 Хвилі й перетин файлів

```
F0 → F1 → F2 → F3 → F4 → F4e → F5 → F6 → F7 → F8 → F9          (foundation, послідовно)
                   │
Хвиля A1:  A4 ‖ A2 ‖ A3a ‖ A5a   файли не перетинаються: A4 — MaterializeCollectedDataJob+Trigger;
                                 A2 — rowWindow API/UI; A3a — рушій+rpt; A5a — ICellPatcher+IntegrationCellPatcher
Хвиля A2:  A1 ‖ A3b ‖ A6         A1 — RowWindow*Job+PatchCellsHandler; A3b — TraceRecorder+ResultStore.Trace;
                                 A6 — sourceEvents API/UI (TableEditor.tsx — після A2)
Хвиля A3:  A5b                   SourceEventSync+CollectionJob (після A1, A4, A5a)
Хвиля B0:  B0a → B0b             (рефакторинг, послідовно)
Хвиля B1:  B2                    (ядро представлення)
Хвиля B2:  B3 ‖ B1 ‖ B5          B3 — Pivot*; B1 — trace/**+DocumentPage; B5 — viewer/**+routes
Хвиля B3:  B6 → B7 → B8 → B4     (торкаються файлів B2/B5/B1 і features/events/** — послідовно)
Інтеграція: I1 → I2 → I3
Після FEATURE-REGISTRY-TABLES (RT-22, RT-23a, RT-23b): RG1 → RG2 → RG3
```

⚠ Перетини, які план навмисно серіалізує: `MaterializeCollectedDataJob.cs` (F0 → F3 → A4),
`IExternalDataSource.cs` і `PiSqlClientDataSource.cs` (F4 → F4e), `GenericCalculationModule.cs`
(A3a → A3b), `TableEditor.tsx` (A2 → A6), `ICellPatcher.cs`/`IntegrationCellPatcher.cs`
(A5a → A5b), `CollectionJob.cs` (F0 → A5b), `DocumentPage.tsx` (B1 → B7),
`features/events/EventEditorDrawer.tsx` (B7 → B8), `routes.ts` (B5 → B4),
`DocumentViewsController.cs` (B2 → B6). Спільні дописування (`09-seed.sql`,
`DependencyInjection.cs`, `Enums.cs`, контракт) — лише у свої секції, з rebase перед пушем.

⚠ **Спільне з [FEATURE-REGISTRY-TABLES](FEATURE-REGISTRY-TABLES.md#93-хвилі--4-паралельно-файли-не-перетинаються)
(узгоджено 2026-09-27).** Спільних **таблиць** немає: тут — `ext.EntityFieldMap`,
`ext.RowWindow*`, `ext.SourceEvent*`, `calc.MethodologyFormula`, `calc.MethodologyOutput`,
`calc.CalculationResult`, `calc.CalculationStep`, `cfg.ViewDef`/`cfg.ViewVersion`; там —
`cfg.Registry*`, `dic.Registry*`, `calc.CalculationRun` (`RegistryAsOfUtc`). Серіалізується інше:

- **Міграції** обох документів (`HSE301M1…M5` тут, `RK01…RK05` там) переписують
  `EcrDbContextModelSnapshot.cs`: у польоті не більше **однієї на обидві сесії**, наступна
  генерується після rebase на попередню, у порядку готовності.
- **Код розрахунку:** `ICalculationModule.cs`, `GenericCalculationModule.cs` (тут A3a → A3b →
  там RT-23a), `PublishMethodologyHandler.cs` (A3a → RT-23b), `MethodologyEvaluationContext.cs`
  (F1 → RT-23a). Якщо обидва кроки готові одночасно, першим іде HSE301: типові `Scope = Substance`
  і `Kind = Output` чинної поведінки не змінюють.
- **Дописування у свої секції:** `Enums.cs`, `EcrDbContext.cs`, `ErrorCodes.cs`,
  `ExceptionHandlingMiddleware.cs`, `09-seed.sql` (`-- HSE301:<крок>` тут, `-- RT: …` там),
  `DependencyInjection.cs`, `Ecr.Adapters.Excel/DependencyInjection.cs` (B6 тут, RT-16/RT-18a
  там), `02-contracts.md` §7/§9, `02a-db-schema.md`.

### 11.4 Порядок пушу в `dev/integration`

1. Worktree від `origin/dev/integration`: `git worktree add ..\ECR-hse301-<крок> -b hse301/<крок> origin/dev/integration`.
2. `git fetch` → `git rebase origin/dev/integration` (лише незапушені коміти) → повний
   прогін → `git push origin HEAD:dev/integration`.
3. Міграції: перед генерацією — rebase на свіжу гілку, щоб `EcrDbContextModelSnapshot.cs`
   був останнім; одна міграція в польоті на обидві сесії.
4. Контракт (`openapi.snapshot.json`, `schema.d.ts`) — останнім комітом кроку, після rebase.
5. Червоний гейт — наступним комітом у ту саму гілку; `--no-verify` і `--force` заборонені.
6. Коміти — `[CODE]`/`[TEST]`/`[DOCS]` + опис українською; звіт кроку — Done/Verified/Next steps.

### 11.5 Що паралельно з FEATURE-REGISTRY-TABLES

| Можна вже зараз | Чекає на FEATURE-REGISTRY-TABLES |
|---|---|
| F0–F9, F4e, A1–A6, B0–B8, I1–I3: весь конвеєр PI, події з PI, прив'язка вікна, методологія на тимчасових колонках складу, прозорість, представлення, експорт, реєстр подій | RG1 (склад у методології), RG2 (секція складу в представленні), RG3 (лабораторія FG) |
| довідники з одним ключем (§5.3) — чинний конструктор довідників | довідники `STREAM` → `STREAM_CASE` (первинний ключ `STREAM` + `CASE_NAME`) → `GAS_COMPOSITION` (ключ `CASE` + `COMPONENT`) і `COMPONENT` (FEATURE-REGISTRY-TABLES §11.4) та імпорт HYSYS (`RT-18a…RT-18c`) |

⚠ **Точки дотику:** (1) атрибути `reg_*` представлення (RG2) читають довідник через знімок
FEATURE-REGISTRY-TABLES (`IRegistrySnapshotLoader`, §5.7, `RT-22`), а не власним запитом;
(2) вузол трейсу `kind = registry` (§7.2) описує запис у формі `reads[]` прев'ю виразу
(FEATURE-REGISTRY-TABLES §7.1, `RT-26`: функція, довідник, ключ, результат) і момент знімка
`calc.CalculationRun.RegistryAsOfUtc` (там §3.6, `RT-05`); (3) розіменування Lookup у `@Arg`
(Д-6) — там (`R-12`, `RT-23a`), а тут його обходять формули `REGFIELD`.

### 11.6 Крок V — імпорт методологій з AF: аналізатор (MVP, 2026-09-30)

`tools/Ecr.MethodologyImport` (лише BCL, без запису в БД):
`Ecr.MethodologyImport analyze <AF.xml> [--json] [--top N] [--library Common] [--out report.json]`.

- **Що робить.** Потоково (`XmlReader`; справжній `ECR_01_Air.xml` 192 МБ — ~35 с, пік ~60 МБ) читає AF XML →
  нейтральна модель (методологія → версія → формули/константи) → звіт: кількості; посилання з `FInfo_Arguments`
  (`;`-список: `@параметр`, `!Формула`, `CST.Константа`); `Trim` імен, кодів, версій, текстів і токенів; резолвінг
  «власна версія → бібліотека Common (усі версії)»; цикли (SCC); лічильник непізнаного.
- **Блокери (код виходу 2):** нерезолвне `!`/`CST.` посилання (ціль не вгадується; підказка — лише текстом),
  цикл між формулами, «не розпізнано жодної формули чи константи». 1 — помилка запуску/XML.
  Токен у тексті формули без запису в аргументах лічиться окремо й не блокує (CLR такий токен не підставляє).
- **Формат AF XML (звірено зі справжнім `ECR_01_Air.xml`, AF 3.1, `ExportMode="…Flat…"`).** Корінь `AF` →
  `AFDatabase` → ПЛОСКИЙ перелік `AFElement`, де `Name` — повний шлях із `\`; `AFAttribute` (`Name`, `Value`
  або `ConfigString` String Builder виду `"%..\..\Element%";`). Формула — версійний елемент
  `Methodologies\<М…>\<ВерсіяМ>\Formulas\<Ф>\<ВерсіяФ>` (Formula_El_V, `FInfo_*`); елемент `…\Formulas\<Ф>`
  (Formula_El) — лише контейнер. Константа — визначення `…\Constants\<К>` і значення
  `…\Constants\<К>\<Категорія>\<Версія>` (`CInfo_Value`; Категорія = Location/набір). Методологія може бути
  вкладеною (`Methodologies\EmissionCalculationWork\ECW_C05_…\ECW_C05_01`), тому ім'я/версія беруться з
  `MInfo_*` (ConfigString `%..\..\..\..\Element%`), шлях — запасний варіант і перевірка. Обчислювані String
  Builder (`'CInfo_Parameter';_;…`) не використовуються. Rules, Settings, корінь методології — пропущені.
  Уся прив'язка до XML — `Reading/AfXmlReader.cs`, `Model/MethodologyModelBuilder.cs`.
- **Прогін на справжньому файлі:** 48 методологій (по одній версії V1), 2009 формул (1801 контейнер), 1124 імені
  констант / 6506 значень; `!`-посилань 3746 (265 через межу — збіг із 149+116 з `open-questions-pi-admin`),
  `CST.` 7974 (у Common: Flert 821, HSE400 1269, Thermaloxidizer 1188; у файлі питань було 3276 — розбіжність 2 не
  розібрана); нерезолвних 17 (6 різних токенів), циклів 0. Блокер лишається: імпорт по червоному не робити.
- **`export <AF.xml> --out package.json [--allow-blockers]`** пише пакет `ecr-methodology-package` v1 (без запису в БД,
  детермінований; на справжньому файлі 3.4 МБ). ⚠ Схему пакета ввів цей інструмент (раніше її ніде не було) —
  її треба узгодити зі споживачем (крок V, `POST /methodologies/import?dryRun`): `{format, version: 1, library,
  methodologies: [{name, versions: [{version, formulas: [{name, version, arguments, text, startDate, endDate,
  isAvailable, report}], constants: [{name, parameter, unit, values: [{category, version, value, startDate,
  endDate}]}]}]}], blockers: []}`. Дати — рядки AF як є (пояс і півінтервал `[from, to)` вирішує імпортер).
  За наявності блокерів пакет не пишеться (код 2); `--allow-blockers` пише його з непорожнім `blockers`, і
  імпортер зобов'язаний відмовити.
- **`unresolved <AF.xml> [--out unresolved.md]`** (2026-10-01) — звіт для методолога: кожне нерезолвне посилання з
  категорією й рекомендацією (`Reporting/UnresolvedReferenceReport.cs`): регістр (механічна заміна), ім'я
  `CInfo_Parameter` замість імені константи, визначення лише в іншій методології/версії, ймовірна описка
  (Левенштейн 1 для імен від 4 символів, 2 — від 8; нічия — без кандидата), некоректний токен, немає визначення
  (окремо — у формулі з `IsAvailable=False`). Кандидат — лише підказка: інструмент нічого не виправляє. Тести —
  на синтетичних фрагментах (`UnresolvedReferenceReportTests`); 17 справжніх посилань у репозиторії немає, бо
  файл лише на пристрої користувача. Запуск там (з кореня `H:\ECR`, потрібен .NET 10):
  `dotnet run --project tools\Ecr.MethodologyImport -c Release -- unresolved "docs\source\ECR Current Back end\PI AF\ECR_01_Air.xml" --out .sync-local\af-unresolved.md`
  — код виходу 2 за наявності блокерів очікуваний; результат передати методологу.
- **Не зроблено:** запис у БД (`POST /methodologies/import?dryRun` у продукті ще немає), розбір дат, порядок
  формул (B13 §7 крок 6), правила (`Rules`, 321 елемент), налаштування (`Settings`).

---

## 12. Критерії приймання

### 12.1 Золоті тести (`tests/Ecr.Scenarios.Tests/Hse301/Hse301GoldenTests.cs`)

Точність — відносна ≤ 1e-9 до значень файлу повної точності (фікстура читає їх із
`hse301.xlsx`), для меркаптанів ≤ 5e-4 (розбір §1.4); показ — рівно у форматі файлу.

| # | Що | Вхід | Очікувано |
|---|---|---|---|
| G-A1 | `M_t`, подія 28.01 | V 269.258, ρ 0.9589 | 0.2581914962 т |
| G-A2 | `tons[SO2]` | + S 17.2965447 | 0.0891735 т |
| G-A3 | `gsec[SO2]` | + 930 с | 95.885 г/с |
| G-A4 | прапорець | V_LIM 95 418; SO2_LIM 35 398.343 | «нет» |
| G-A5 | `HP_LP_tons` Sour · Jan · V8 · HP, NO2 | 9 подій | 0.0027104636 т; V = 1 177.766 |
| G-A6 | `HP_LP_gsec` Sour · Jan · V8 · HP (max), SO2 | 9 подій | 142.525 г/с |
| G-A7 | YTD Jan SO2 | + FG continuous 0.000668 | 142.5258 г/с |
| G-B1 | `V_Sm3` Pilot · Jan | 9.7 · 744 | 7 216.8 |
| G-B2 | `tons[CO]` Pilot · Jan | ρ 0.7631 | 0.1101428016 т |
| G-B3 | `gsec[CO]` Pilot · Jan | 2 678 400 с | 0.041123 г/с |
| G-B4 | `HP_LP_gsec` Jan · Continuous · HP (sum), CO | Pilot + Purge | 0.3790546 г/с |
| G-C1 | `CO2_GHG_t` | EF 2.087 | 0.5361514 т |
| G-C2 | `CH4_GHG_t`, `N2O_GHG_t` | NCV 37.58607 | 9.7044e-6 т, 9.7044e-7 т |
| G-N | NO2 Jan YTD | 0.9903959 + 0.0454866 | 1.0358825 г/с |
| G-N2 | азотні події серпня й вересня | 18 подій | викиди 0; `Nitrogen` V = 586.162 |

### 12.2 Звірка зведень

`Hse301SummaryReconciliationTests`: представлення `HP_LP_tons` і `HP_LP_gsec`, побудоване з
фікстури, звіряється з файлом клітинка в клітинку. Джерело — ті самі 2 304 співвідношення,
що в розборі (`verify_hplp.py`, `verify_tons.py`). **0 розбіжностей**; порожні LP-стовпці
лишаються порожніми, а не нулями.

### 12.3 Вигляд

- **Структурний тест** (`tests/Ecr.Application.Tests/Views/Sg8LayoutTests.cs`): рівні шапки
  (`JAN` span 9 + `Total`, `FEB` span 18 + `Total`, `AUG` span 29 + `Total`), 56 подій,
  підписи en/ru, порядок секцій, формати (`0.000` об'єм, `0.000000` викиди), `Total` лише
  в об'ємі.
- **Знімки** (`screenshots.spec.ts`): `AI_Int_SG_V8`, світла й темна; у PR — поруч зі знімком
  аркуша файлу.
- **XLSX:** злиття шапки, закріплення, числа — числами, «Document map» з посиланнями.
- **Прозорість:** для G-A3 «Чому?» показує подію PI (шаблон, ID, атрибути `Start`/`End`),
  PI-вікно (точки, % Good), `Rho20` (хто ввів), `K_S` (вікно чинності) і формулу тривалості.

### 12.4 Нефункціональні

| Вимога | Поріг |
|---|---|
| рендер річного представлення 301 | p95 ≤ 2 с на стенді |
| попередній розрахунок у шухляді | p95 ≤ 400 мс |
| від правки кінця події до нового числа в представленні (PI доступний) | ≤ 3 хв (дедуплікація 2 хв) |
| від появи закритої події в PI до рядка з об'ємом і розрахунком | ≤ період розкладу сутності-шаблону + 5 хв |
| синхронізація місяця подій (≤ 500 подій, 20 атрибутів) | ≤ 60 с на стенді з фейковим джерелом |
| a11y | гейти `a11y (dark)`/`a11y (light)` зелені |
| `smoke.ps1`, `e2e-stand.ps1` | зелені з окремого worktree |

### 12.5 Події з PI (`tests/Ecr.Scenarios.Tests/Hse301/Hse301SourceEventsScenarioTests.cs`)

Джерело — фейкова реалізація `IExternalDataSource` з подіями й атрибутами (жодних імен
реальних тегів), далі весь шлях продукту: синхронізація → рядок → об'єм → розрахунок →
представлення.

| # | Що | Вхід | Очікувано |
|---|---|---|---|
| G-E1 | подія з EF → рядок → розрахунок = приклад A | EF 28.01.2026 09:09:20Z–09:24:50Z, `Category = V8`, кейс «370 Winter», факел HP; об'єм — (а) атрибут EF 269.258 Sm3, (б) `RowWindow` на фейкових точках з Total 269.258 | рядок `EF-…` зі `Start` 28.01 14:09:20 і `End` 14:24:50 (Asia/Atyrau), `DurationSec` 930; `M_t` 0.2581914962 т; `gsec[SO2]` 95.885 г/с; подія в аркуші `AI_Int_SG_V8`, стовпець JAN/1 — **однаково в обох режимах об'єму** |
| G-E2 | повторна синхронізація | те саме вікно ще раз | нуль змінених комірок, нуль нових рядків, `aud.CellChange` не росте |
| G-E3 | ручна правка не перетирається | людина змінила `Category` на V9, у PI лишилося V8 | після синхронізації — V9, стан `Synced` + `KeptManual = [Category]`; «Повернути значення з PI» → V8, і наступна синхронізація знову веде поле |
| G-E4 | зникла подія | EF прибрали з фейкового джерела | `Missing`, рядок і числа ті самі; «Не враховувати» → усі виходи рядка 0, подія лишається в реєстрі; той самий прогін із `Truncated` — `Missing` не ставиться |
| G-E5 | закритий період | січень закрито, у PI змінився кінець події | жодного запису в січень; стан `PeriodClosed`; смуга свіжості показує «змінено в PI після закриття» |
| G-E6 | сезон і категорія без здогадок | атрибут сезону «Winter» (лише сезон) і невідоме значення категорії «V10» | `HmbCase` = кейс потоку з сезоном Winter; `Category` порожня, стан `Unmapped` зі значенням «V10»; жодного виводу сезону з місяця |

---

## 13. Ризики, рішення, питання, чек-лист

### 13.1 Ризики

| # | Ризик | Наслідок | Пом'якшення |
|---|---|---|---|
| HR-1 | Події з PI («це вже приходе з pi»), але текст RTQP-запиту подій у репозиторії не підтверджений, і невідомо, де лежать категорія й сезон — на EF чи на елементі (HQ-18) | події не приходять або приходять без категорії | ключ `PiSqlClient:EventQuery` без типового тексту й видимий стан `.eventQueryNotConfigured`; мапінг підтримує обидві області; «Test on recent events» до збереження; ручне введення лишається |
| HR-2 | PI Total «за добу» проти `Sm3/h` | ×24 правдоподібне число | еталон — локальний інтеграл; серверний лише за ключем і з банером розбіжності (§10.6) |
| HR-3 | Стиснення PI: коротка подія без точок усередині | нуль замість об'єму | інтерполяція меж вікна (§4.1), статус `Partial`/`NoData` |
| HR-4 | ~~Відмова D-52a не прийнята замовником~~ | — | **знято 2026-09-27:** людина погодила V-1 («так погоджуюся на Подання») |
| HR-5 | Тиждень роботи поза `main` (`dev/integration`) | зелений гейт = «зелена гілка» | щоденний rebase, `git merge origin/main` |
| HR-6 | Перетин з FEATURE-REGISTRY-TABLES у `09-seed.sql`, `Enums.cs`, `ICalculationModule.cs`, `GenericCalculationModule.cs`, міграціях | конфлікти | секції `-- HSE301:*`, попередження сесії, спільна черга міграцій і порядок A3a/A3b → RT-23a (§11.3); `CalculationInputBuilder` (Д-6) не чіпаємо — його змінює RT-23a |
| HR-7 | Селектор-константи `SEL_*` (V-11) незграбні | помилка налаштування дасть 0 | золоті тести по кожній із 11 речовин; FEATURE-REGISTRY-TABLES функції «поточна речовина» не дає, тож прийом лишається й після RG1 (§6.5) |
| HR-8 | Обсяг трейсу на інших методологіях | роздування `calc.*` | видимі кроки — лише за явним `IsVisible`; решта як сьогодні |
| HR-9 | Неповний місяць: календарні проти фактичних годин (HQ-8) | інші г/с за вересень | поведінка файлу за замовчуванням, окремий параметр `perPeriodRate.divisor` |
| HR-10 | Дати файлу в DD/MM при підписі MM/DD | плутанина на звірці | представлення показує дату форматом продукту, підпис виправлено (розбір §1.8) |
| HR-11 | Статуси покриття збору (`SkippedPointCeiling`, `SkippedPeriodClosed`, `ConflictKeptManual`) адміністратору ніде не показуються; мапінги не звужуються до таблиці екземпляра, тож щопрогону з'являється хибний `ConflictKeptManual` (відомо після D16-03, `3e6d2efa`) | смуга свіжості «PI за період» (§8.8) і чіп статусу PI (§10.2) казали б «актуально» про поле, яке пропущено | ці статуси мають потрапити в смугу свіжості й чіп статусу PI; хибний `ConflictKeptManual` треба прибрати раніше, ніж його показувати, інакше позначка горітиме завжди |
| HR-12 | Подію змінили в PI після закриття періоду | числа закритого періоду розходяться з PI | нічого не пишемо (`D-39`), стан `PeriodClosed` і позначка «змінено в PI після закриття» в смузі свіжості; далі — чинна процедура перевідкриття |
| HR-13 | Дубль у PI: дві події з різними ID на один скид | подвоєний викид | у реєстрі дублі стоять поруч (сортування за початком); «Не враховувати» (V-21); однаковий ID дубля не дає за побудовою (G-E2) |
| HR-14 | Подія довго лишається відкритою в PI (без кінця) | місяць без події, яка вже була | стан `Open` у лічильниках смуги свіжості й навігатора аркушів; ручне введення можливе, а коли подія закриється в PI, людина вирішує, яку з двох лишити («Не враховувати») |
| HR-15 | `ApplyIntegrationRowsAsync` — другий метод запису від інтеграції | розбіжність адресації двох шляхів (клас A7-27) | обидва методи — поверх того самого `PatchCellsHandler`; тести чинного методу проходять без правок (DoD A5a) |

### 13.2 Реєстр рішень

**V-1…V-15 схвалені людиною 2026-09-27** («так, вноси D-149 в реєстр та Схвалено
запропоновані рішення») і внесені в `docs/tz/10-decisions.md` за відповідністю V-1 → `D-149`,
V-n → `D-(169+n)` для n = 2…15. Реєстр — єдине джерело істини; тут лишаються обґрунтування й
посилання на розділи. **V-16…V-25 ухвалені за делегуванням людини 2026-09-27** («все інше на
твій розсуд не обмежуй себе») і внесені в реєстр як `D-185…D-194` (V-n → `D-(169+n)`).

| # | Статус | Рішення | Обґрунтування |
|---|---|---|---|
| **V-1** | ✓ схвалено 2026-09-27 → `D-149` | Дослівно: «так погоджуюся на Подання» (у документації — «представлення», бо «подання» = Submit). Представлення — нова сутність `cfg.ViewDef`/`cfg.ViewVersion` лише для читання; `rpt.*` не змінюється; виняток D-52a «матриці з динамічними колонками» стосується конструктора `rpt.*`, а не представлення документа | `rpt.*` — публічний контракт SSRS (D-53) зі знімками й хешем; джерело лише результати; D-52a прямо виключає матриці (`10-decisions.md:289`); мова умов, агрегати й ClosedXML перевикористовуються |
| **V-2** | ✓ схвалено 2026-09-27 → `D-171` | Прив'язка «атрибут → колонка, вікно = рядок» — нова `ext.RowWindowMap` з провенансом `ext.RowWindowValue` | `UQ_EntityFieldMap` і фіксований адресат рядка (`EntityFieldMap.cs:113-122`) — інша семантика; один тег обслуговує багато рядків |
| **V-3** | ✓ схвалено 2026-09-27 → `D-172` | Еталон згортки — локальний (з сирих точок, межі інтерпольовано); серверний summary — ключем конфігурації без типового тексту | імена RTQP-функцій у репозиторії не підтверджені; пастка «Total за добу»; принцип FLERT — без вигаданих дефолтів |
| **V-4** | ✓ схвалено 2026-09-27 → `D-173` | Конверсія одиниць на межі — у `Ecr.Application` (`BoundaryUnitConversion`), `SourceUnitConverter` делегує | інфраструктура не бачить адаптера PI; одна арифметика замість двох |
| **V-5** | ✓ схвалено 2026-09-27 → `D-174` | Автоперерахунок після матеріалізації й вікна рядка, з дедуплікацією, без прапорця | інакше нові дані лише блокують подання (F-05); ручна кнопка лишається. Синхронізація подій (V-18) кличе той самий `ICalculationTrigger` |
| **V-6** | ✓ схвалено 2026-09-27 → `D-175` | Проміжні значення — `calc.CalculationResult` з `Kind = Intermediate`; `rpt` бере лише `Output` | один шлях читання, прив'язка колонки до проміжного; `ContentHash` зрізів не змінюється |
| **V-7** | ✓ схвалено 2026-09-27 → `D-176` | `Scope` формули (`Row`/`Substance`) і `IsPerSubstance` виходу; типові значення = чинна поведінка | без цього Row-величини пишуться N разів |
| **V-8** | ✓ схвалено 2026-09-27 → `D-177` | Входи трейсу збирає обхід AST, рушій не змінюється; `TraceJson` — схема v1 | немає другої семантики читання; детерміновано |
| **V-9** | ✓ схвалено 2026-09-27 → `D-178` | Одна таблиця `FLARE_RECORD` для подій і безперервних джерел; властивості складу — тимчасові колонки до RG1 | шапка 1–16 спільна у файлі; одна методологія й одна прив'язка; золоті тести без довідників |
| **V-10** | ✓ схвалено 2026-09-27 → `D-179` | Подія належить періоду свого початку за TZ проєкту, не ділиться | модель «екземпляр на період»; закриває HQ-13 і HQ-15 |
| **V-11** | ✓ схвалено 2026-09-27 → `D-180` | Вибір wt% «своєї» сполуки — селектор-константами `SEL_*` | `SUBSTANCE()` не обирає `@Arg`; явність у трейсі |
| **V-12** | ✓ схвалено 2026-09-27 → `D-181` | `Sm3` — окрема розмірність `StdVolume` | стандартний і робочий кубометри не конвертуються множником |
| **V-13** | ✓ схвалено 2026-09-27 → `D-182` | Прапорець «превышение» — правило представлення (діалект Report), не формула методології | визначення не підтверджене (HQ-11); у V1 не показник звітності |
| **V-14** | ✓ схвалено 2026-09-27 → `D-183` | Переглядач — нативна `<table>` зі sticky; RevoGrid — запасний шлях для > 60 000 клітинок | семантика шапки й друк; гарячий `DocumentGrid` не чіпається |
| **V-15** | ✓ схвалено 2026-09-27 → `D-184` | Агрегації представлення — закритий перелік: `sum/count/avg/min/max` + іменовані `perPeriodRate`, `worstCaseRate` | B16 §3: правила агрегації — іменовані з автотестами, не мова |
| **V-16** | ✓ ухвалено за делегуванням 2026-09-27 → `D-185` | Події — з джерела через нейтральний порт `DiscoverEventTemplatesAsync`/`ReadEventsAsync` (у ядрі немає слова «Event Frame»); PI SQL Client — ключі `PiSqlClient:EventQuery`/`EventTemplateQuery` без типового тексту, контракт колонок результату фіксує наш бік (§4.7.1–4.7.2) | факт від людини: «це вже приходе з pi»; межа ядра (`B06-integration-ports.md:28-32`); імена RTQP-об'єктів подій не підтверджені — той самий аргумент, що V-3 |
| **V-17** | ✓ ухвалено за делегуванням 2026-09-27 → `D-186` | Мапінг «атрибут події → колонка» — `ext.SourceEventMap`/`FieldMap`/`ValueMap`: атрибут лише з каталогу, область `Event` або `PrimaryElement`, Lookup — за кодом, назвою чи явною відповідністю; `$start`/`$end` обов'язкові (§4.7.3) | ФВ-13.13 — жодних введених руками імен; де лежать атрибути, невідомо (HQ-18) — підтримуються обидва місця; `EntityFieldMap` має інший інваріант адресата |
| **V-18** | ✓ ухвалено за делегуванням 2026-09-27 → `D-187` | Синхронізація: upsert за ID події (рядок `EF-…`); ручна правка не перетирається (`KeptManual`); зникла подія — `Missing` лише після повного прочитання вікна, без видалення; закритий період не чіпається; подія без кінця не матеріалізується; перенесення в інший місяць — `PeriodChanged` без автоперенесення; розклад — `ext.CollectionSchedule` сутності-шаблону з `LookbackDays` (§4.7.4) | D-118, D-39, ER-I-03; видалити рядок документа в системі нічим, а подвійний облік гірший за позначку |
| **V-19** | ✓ ухвалено за делегуванням 2026-09-27 → `D-188` | Об'єм — `VolumeMode` мапінгу: атрибут події, Total за вікном рядка (V-2) або вручну (§4.7.5) | невідомо, чи зберігає PI об'єм в EF; обидва шляхи — дані, без релізу |
| **V-20** | ✓ ухвалено за делегуванням 2026-09-27 → `D-189` | Сезон і категорія — атрибути події; правила «сезон за місяцем» немає ніде; незіставлене значення — порожня комірка й `Unmapped`, без здогадки (§4.7.6) | людина: «це вже приходе з pi»; вгадане значення гірше за назване відсутнє |
| **V-21** | ✓ ухвалено за делегуванням 2026-09-27 → `D-190` | «Не враховувати» — Lookup `Exclude` і прихована `IsExcluded`, що обнуляє `V_Sm3` (§5.2, §6.3) | рядок документа видалити нічим; прийом той самий, що для азотних подій (`@IsN2`) |
| **V-22** | ✓ ухвалено за делегуванням 2026-09-27 → `D-191` | Реєстр: поле з PI замкнене до явного «Виправити вручну» (`F2`); «Повернути значення з PI»; лише для читання — `SourceEventId` і формули (§10.4) | випадковий ввід не повинен мовчки відв'язувати поле від PI; явна дія й видимий стан (KIT L9) |
| **V-23** | ✓ ухвалено за делегуванням 2026-09-27 → `D-192` | Для RG1: `CST.M_CO2 = 44.00` і `EF_t_t = Round(!EF_RAW, 3)`; `CST.M_S = 32.064`; молярні маси компонентів — значення HYSYS, відновлені з 301; LHV суміші — поле кейсу `LHV_STD` (§6.2, §6.5) | перевірено на файлі: EF — 11 складів, S і μ — 3 склади, збіг до 1e-14; `Truncate(44.01·…)` і `M_S = 32.06` дають розбіжності; LHV компонентів в Add Stream немає |
| **V-24** | ✓ ухвалено за делегуванням 2026-09-27 → `D-193` | HQ-9: V1 — лише Offshore, як у `Document map` файлу; Onshore-аркуші додаються в конструкторі представлення як дані, без коду | рішення про обсяг, яке нічого не блокує й не потребує переробки |
| **V-25** | ✓ ухвалено за делегуванням 2026-09-27 → `D-194` | HQ-17: підписи представлення — EN+RU білінгвально, як у файлі; KZ — третьою мовою `LocalizedText`, щойно з'явиться переклад; перемикає `labelMode` | UX-судження; мови продукту en/ru/kk уже підтримані |

### 13.3 Питання до замовника

**Закриті 2026-09-27.**

| # | Питання | Як закрито |
|---|---|---|
| HQ-1 | Які теги/атрибути AF дають події й об'єми на HP/LP-факелах; як визначаються межі події (розбір §7 п.1) | людина: «це вже приходе з pi» — подія з межами до секунди є в PI (Event Frame), ECR її читає (§4.7, V-16…V-19). Хвіст питання — «чи пілот справді константа 9.7» — факт, переходить у робочі припущення нижче |
| HQ-3 | Визначення категорій V6–V9 і їхня прив'язка до типів подій (п.3) | людина: «це вже приходе з pi» — категорія є атрибутом події (§4.7.6, V-20); вгадувати її за типом події не треба |
| HQ-4 | Межі сезонів Winter/Summer для кейсу HMB (п.4) | людина: «це вже приходе з pi» — сезон (кейс HMB) є атрибутом події (§4.7.6, V-20); межі сезону системі не потрібні |
| HQ-9 | Чи потрібні Onshore-секції в 301 (п.9) | ухвалено за делегуванням — V-24 |
| HQ-10 | Де живуть події факелювання — FLERT чи PI; що master на 2026 | людина: «це вже приходе з pi» — master подій PI; історія 2026 — синхронізацією за період (§10.4) |
| HQ-13 | Подія через межу місяця: до місяця початку чи ділити | схвалено V-10 → `D-179` |
| HQ-14 | Зміна D-52a (V-1) | людина: «так погоджуюся на Подання» → `D-149` (§8.1) |
| HQ-15 | Часовий пояс часу подій у звітності | схвалено V-10 → `D-179`: пояс проєкту (`Project.TimeZoneId`); час події з PI приходить в UTC і перекладається в пояс проєкту (§4.7.3) |
| HQ-16 | UOM атрибута витрати в AF і налаштування PI Total «за добу» | питати не треба: UOM видно в каталозі на екрані налаштування (ФВ-16.9, §10.6), а еталон — локальний інтеграл (V-3 → `D-172`), серверний summary — лише з банером розбіжності |
| HQ-17 | Мови підписів представлення | ухвалено за делегуванням — V-25 |

**Лишається відкритим — одне питання, яке не можна ні знати, ні побачити в каталозі PI з
екрана налаштування.**

| # | Питання | Хто | Чому важливо | Нейтральний дефолт |
|---|---|---|---|---|
| HQ-18 | Категорія V6–V9, сезон (кейс HMB) і, можливо, об'єм — це атрибути самого Event Frame події чи атрибути елемента (факела, установки), до якого EF прив'язано? | PI-адмін | де читати значення: на EF чи на елементі в момент початку події | мапінг задає адміністратор, і він підтримує обидва місця (`AttributeScope` = `Event` \| `PrimaryElement`, §4.7.3); не блокує |

**Робочі припущення — до підтвердження на приймальному тестуванні, не блокують.** Це факти
світу замовника, яких система знати не може. Діє нейтральний дефолт; кожне значення —
константа з `Source` чи налаштування, тож заміна не потребує переробки.

| # | Факт | Хто підтверджує | Робоче припущення |
|---|---|---|---|
| HQ-1a | Пілот не вимірюється й дорівнює 9.7 ст.м3/год (файл: V = 9.7 · години на всіх 9 місяцях) | еколог | `CST.PILOT_RATE_SM3H = 9.7` з вікном чинності |
| HQ-2 | Методика й редакція (0.003/0.8/0.13/0.02/0.002/0.0005/0.9984, k = 1.3), чому коефіцієнти не залежать від Wист/Wзв < 0.2, що таке 0.995 | методолог замовника | коефіцієнти з файлу, `Source` = «відновлено з HSE301.Year 2026» |
| HQ-5 | Походження 102.6 ст.м3/с і 35 398.343 г/с; чи змінюються вони з дозволом 01.09.2026 | еколог, юрист | те саме значення в обох вікнах чинності |
| HQ-6 | Площі оголовків HP (0.200256 м²) і Pilot (0.00092 м²); площа LP | технолог | значення з файлу; LP — порожньо, Wист LP = `#REF` з поясненням |
| HQ-7 | Чи має «Average-weighted stream» бути зваженим | еколог | як у файлі — просте середнє |
| HQ-8 | Неповний місяць: ділити на фактичний чи календарний час | еколог | календарний, як у файлі; параметр `perPeriodRate.divisor` (HR-9) |
| HQ-11 | Точне визначення «Анализ: превышение макс. расхода» | еколог | «да», якщо V > Vlim **або** SO2 > ліміту |
| HQ-12 | Чи діє для 301 правило B16 A-2 (добова подія 86 300…86 500 с не йде в max) | еколог | не діє, бо файл його не показує |

### 13.4 Чек-лист

- [x] HQ-14 (D-52a) поставлено першим; відповідь зафіксовано — V-1 схвалено людиною 2026-09-27 (§8.1).
- [x] V-1…V-15 схвалені людиною 2026-09-27 і внесені в `docs/tz/10-decisions.md` як `D-149`, `D-171…D-184`.
- [x] V-16…V-25 (ухвалені за делегуванням) внесено в реєстр як `D-185…D-194` (2026-09-27).
- [x] D16-03 у `dev/integration` до старту F2 — виконано в `3e6d2efa`.
- [x] D16-04 на шляху збереження — виконано в `400c58b1`.
- [ ] F1–F9 і F4e злиті послідовно; у польоті не більше однієї міграції.
- [ ] На середовищі заданий і звірений з RTQP Reference `PiSqlClient:EventQuery`; мапінг подій
      збережено з каталогу й перевірено «Test on recent events»; відповідь на HQ-18 внесена в мапінг.
- [ ] Кожен крок: план-таблиця, DoD з мутаційним доказом, розмір і частка тестів у коміті,
      повний прогін перед пушем.
- [ ] Нові коди — у п'яти місцях, включно з армом `ExceptionHandlingMiddleware`.
- [ ] Нові ключі — у `09-seed.sql` (`en`), у своїй секції `-- HSE301:*`.
- [ ] Золоті G-A1…G-N2 і G-E1…G-E6 зелені; звірка зведень — 0 розбіжностей.
- [ ] `ContentHash` зрізів `rpt.*` не змінився (тест A3a).
- [ ] Переглядач: L1–L10, `a11y (dark/light)`, клавіатурний шлях, друк.
- [ ] `smoke.ps1` і `e2e-stand.ps1` — з окремого worktree, зелені.
- [ ] Після FEATURE-REGISTRY-TABLES (RT-22, RT-23a, RT-23b): RG1 з тими самими числами, RG2, RG3.

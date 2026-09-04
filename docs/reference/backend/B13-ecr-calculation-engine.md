# B13 — Розрахунковий рушій ECR

> **Замінює B08** («Схема `calc` — попередній проєкт»). Питання, які там були
> позначені «з'ясувати після аналізу бекенду», тепер мають відповіді з джерел —
> див. §1. Решта B08 (контракт `ICalculationModule`, принцип «не припускати, що всі
> обчислення — Excel-формули») лишається чинною і повторюється тут у розширеному вигляді.
>
> Спирається на факти [B12](B12-ecr-as-is-backend.md) §3.4–§3.6. Обмеження ядра —
> `architecture/docs/16-handover-brief.md` §3.
>
> **UI і контракти конфігуратора** цих сутностей (веб-редактор, версійність із
> поясненням, драбина no-code → скрипт C# → модуль, прив'язка правилами) —
> [B19](B19-calc-configurator.md).

---

## 1. Вісім питань B08 §6 — відповіді

| # | Питання B08 | Відповідь | Джерело |
|---|-------------|-----------|---------|
| 1 | Які методики, скільки, яка складність | **Дві родини.** ~44 методології `ECW_C**` (data-driven, «рядок → формули»), покриття — `skills/ecr-methodology/references/coverage.md`; **4 bespoke-модулі** (HSE400, Thermaloxidizer, Flaring Continuous/Intermittent) з багатоетапною доменною логікою | [B12](B12-ecr-as-is-backend.md) §3.4 |
| 2 | Де коефіцієнти і хто їх змінює | У PI AF, дзеркало `AF_Constants`; змінює **адміністратор у PI Vision** (розширення Methodologies) — з категоріями, версіями і датами дії | User Manual §2.1.9; DDL `AF_Constants` |
| 3 | Частота і обсяг перерахунку | Подієво (71 тригер), щохвилинні calc-джоби, нічний повний річний прогін; одиниця = (джоб × діапазон дат × опційно `LandTable`/`FileNumbers`), split на 5 частин або по місяцях | `JobQueue_Execution` |
| 4 | Чи є перерахунок історії при зміні методики | **Так.** Реалізується режимами задачі перерахунку ([B15](B15-ecr-recalc-orchestration.md) §2.2) і резолвінгом версії методології на дату періоду (§5.1); окремий `calc.RecalculationPlan` не потрібен | [B15](B15-ecr-recalc-orchestration.md) §2 |
| 4а | Рівень розрахунку (додатково) | **Рядок** для Land (`ECW_C**` рахує рядок `AF_*`-таблиці); **таблиця/період** для bespoke (trains, потоки, зважені середні) | `R_Land_General.cs`, `R_Air_HSE_Thermaloxidizer_General.cs` |
| 5 | Які сирі дані на вході | RDS-рядки (`AF_*_Land_*`), gas composition (HSE400 + FLERT), об'єми/тривалості потоків (FLERT), перміти і речовини (`AF_PermitConstant`), константи/формули/правила | [B12](B12-ecr-as-is-backend.md) §3.1, §3.5 |
| 6 | Вимога відтворюваності | **Так, і вона вже частково реалізована**: `CalcResult_Metadata(OperationId, FormulaName, ArgumentsJson, ResultValue, …)` — журнал «як вийшло число». Втратити це не можна | DDL `CalcResult_Metadata` |
| 7 | Що лишається в AF після перенесення | Відповідь у [B14](B14-ecr-integration-and-data.md) §2 | — |
| 8 | Зв'язок методик із пермітами і ставками | Через `AF_PermitConstant` (речовини, `Substance_Code`) і `HSE_Pollutant_Emission_Fees_T5` (ставки); у розрахунок входять як довідник, у звіти — як ліміт | [B12](B12-ecr-as-is-backend.md) §3.5 |

**Наслідок для B08:** розділ §5 («що зробити вже зараз») виконано; схема `calc`
з §3 B08 переписана нижче під реальну метамодель ECR.

---

## 2. Три рішення, з яких випливає все інше

### ER-C-01. Методологія — це **дані**, а не клас

Чинна система вже майже там: формули, константи, правила, версії і дати живуть
у PI AF і редагуються в UI ([B12](B12-ecr-as-is-backend.md) §3.5). Кодом лишається
рівно те, що кодом і має бути:

| Що | Де живе в новій системі |
|----|-------------------------|
| Текст формули, аргументи, версія, дати дії | `calc.FormulaDef` (у складі `MethodologyVersion`) — **дані** |
| Значення константи, категорія, дати | `calc.ConstantDef` + `calc.ConstantValue` — **дані** |
| Правило зіставлення (таблиця + параметри + значення) | `calc.RuleDef` — **дані** |
| Порядок виконання формул усередині методології | **дані** (`calc.FormulaDef.Ordinal`) |
| Вибір категорії константи, якщо він нетривіальний | **дані** (вираз) або код модуля |
| Багатоетапна доменна логіка (trains, SOR/EOR, зважені середні) | **код** — `ICalculationModule` |

Тобто ~44 класи `ECW_C**` **зникають** як окремі одиниці коду: те, що вони роблять
(взяти константи/формули за `MInfo_Name`, підставити аргументи рядка, обчислити,
записати результат), стає **одним generic-модулем**, керованим `calc.*`.
Bespoke-розділів лишається 4. Основний варіант їх реалізації після ревізії —
**декларативний конвеєр + кроки-скрипти** (§4.2, [B22](B22-nocode-and-realism.md) §2,
ER-N-01); модуль коду — запасний, якщо прототип на Thermaloxidizer покаже, що
конвеєр не витягує (питання N-1).

> Це прямий аналог рішення ядра «структура — це дані»: додати методологію = `INSERT`,
> а не реліз збірки. Але критерій зворотної сумісності жорсткий (§8): generic-модуль
> зобов'язаний дати **той самий результат**, що й відповідний `ECW_C**`.

### ER-C-02. Розрахунок виконується **поза SQL Server**

Причина не архітектурна мода, а факт: спільний AppDomain CLR + паралельні
джоби дають каскад `701 → 6535 → 233`, який кладе сервер
(`skills/ecr-jobs-ops/references/memory-oom.md`), а `NCalc.Expression.Parameters`
мутується на спільному кеші, через що паралелізм усередині CLR уже пробували і
відкотили (`Utilities.cs:42-45`).

```
Ecr.Api ──► IJobScheduler ──► Ecr.Calculations.Worker (окремий процес, .NET 10)
                                 ├─ generic-рушій + виконавець конвеєра (§4.2)
                                 ├─ ICalculationModule (запасний шлях, N-1)
                                 ├─ Ecr.Expressions (компільовані, іммутабельні)
                                 └─ ICellStore / calc-репозиторії  ──► SQL Server
```

Наслідки: пам'ять обмежена процесом, а не інстансом SQL; падіння worker-а не кладе
БД; паралелізм — штатний і безпечний; `PERMISSION_SET = UNSAFE` зникає як вимога ІБ.

### ER-C-03. Результати — **не** `doc.CellValue`

`doc.*` — це те, що ввів користувач. Результат розрахунку має іншу природу:
він похідний, перераховуваний, має власну версію методики і власний журнал.
Змішувати їх означало б, що перерахунок пише в аудит змін даних і конфліктує
з оптимістичним блокуванням рядка (B04 §2).

Тому результати живуть у **`calc.CalculationResult`** (§5), а в документ потрапляють
лише **посиланням**: `ColumnDef.DataType = Calculated` + `cfg.CalculationBinding`
показує гриду, звідки брати число. Це також знімає питання «чи можна редагувати
обчислену комірку» — її не існує як комірки.

---

## 3. Мова виразів: у ECR їх **дві**

B03 спроєктував одну граматику для формул RDS-шаблону (11 Excel-функцій, [15] §4).
Методології ECR використовують **інший діалект** — NCalc-вирази PI AF:

| | Діалект A (RDS/Excel) | Діалект B (методології AF) |
|---|---|---|
| Приклад | `SUM([Water_07].[Main].[7001001:7001005].[{Period}])` | `(!ECW_EC_tons_NOx / CST.k7_ECW_C05_01_) * CST.k6_ECW_C05_01_` |
| Посилання | комірки шаблону, періоди, `[__Header]` | `!Formula` (інша формула), `@Arg` (вхід рядка), `CST.Name` (константа) |
| Функції | 11 Excel-функцій, з них ~7 обчислювальних | **24**: `Abs Acos Asin Atan Ceiling Cos Exp Floor IEEERemainder Ln Log Log10 Max Min Pow Round Sign Sin Sqrt Tan Truncate` + `in`, `if`, `ifs` |
| Аргументи | виводяться з виразу | **оголошені явно** в `FInfo_Arguments` (`;`-список) |
| Версійність | версія шаблону | `FInfo_StartDate`/`FInfo_EndDate` + `FInfo_Version` |
| Джерело | `.xlsx` | PI Vision, з валідацією і автодоповненням |

### ER-C-04. Один парсер, два діалекти

`Ecr.Expressions` (B03) отримує **п'ятий контекст** — `Methodology`:

| Контекст | Доступні посилання | Результат |
|----------|--------------------|-----------|
| `Methodology` | `@Arg`, `CST.Name`, `!Formula`, літерали | скаляр |

Реалізація: той самий лексер/парсер/AST; змінюється лише резолвер посилань і
набір дозволених функцій. Обчислювач у обох випадках — NCalc, але **скомпільований
у делегат і закешований іммутабельно** (знімає P-08 і відкриває паралелізм).

**Три семантичні пастки діалекту B, які треба перенести 1:1** (інакше числа поїдуть):

| # | Пастка | Правило |
|---|--------|---------|
| 1 | Цілочисельне ділення NCalc: `365/31 = 11` | арифметика над константами — у `decimal`; літерали приводяться до дробових. Тест на кожній формулі з діленням |
| 2 | `FInfo_Arguments` — **джерело істини** про список аргументів, а не сам вираз | збірка підставляє рівно те, що перелічено; токен, відсутній у списку, у вираз **не потрапляє**. Зберігаємо цю семантику явно, з валідацією «усі токени виразу є в списку» при публікації |
| 3 | `CInfo_Value` — `nvarchar` | парсинг у `decimal` з інваріантною культурою; нечислові значення — помилка публікації, а не тихий `NULL` |

**Правка до B03:** §2 і §3 доповнюються контекстом `Methodology` і 24 функціями;
§7 (тест еквівалентності) розширюється на **обидва** діалекти. Внесено в [B10](B10-decisions.md) §2 як П-14.

---

## 4. Модулі розрахунку

### 4.1 Generic-модуль (заміна 44 класів `ECW_C**`)

```csharp
public sealed class DataDrivenMethodologyModule : ICalculationModule
{
    public string Code => "ecr.methodology.datadriven";

    public async Task<CalculationResult> CalculateAsync(CalculationContext ctx, CancellationToken ct)
    {
        // 1. За правилами знайти методологію для рядка (рівно одна — інакше Issue)
        var methodology = _rules.Match(ctx.SourceRow);          // calc.RuleDef
        // 2. Взяти версію методології, чинну на дату періоду
        var version = _repo.ResolveVersion(methodology, ctx.PeriodDate);
        // 3. Резолвити категорію константи (вираз або поле рядка)
        var category = _categories.Resolve(version, ctx.SourceRow);
        // 4. Обчислити формули в порядку FormulaDef.Ordinal
        //    !Formula → результат попереднього кроку; @Arg → поле рядка; CST.X → константа
        // 5. Кожен крок → CalculationStep у Trace
        // 6. Результати → CalculationResult.Outputs (речовина → тонни/г-с)
    }
}
```

**Що тут важливе і чого не було в чинній системі:**

* **порядок виконання формул — явний** (`FormulaDef.Ordinal`), а не «reflection
  викликав клас, а клас знає порядок»;
* **зіставлення правил детерміноване і перевіряється при публікації**: «рівно одне
  правило на комбінацію» стає інваріантом версії методології, а не помилкою runtime;
* **резолвінг версії — за датою періоду**, а не «`MAX(Methodology)`» (чинна система
  на цьому вже спіткнулась: двопаливний котел 0906, кейс IEC FIX 4).

### 4.2 Складні розрахунки: конвеєр, а не 4 модулі коду

> **Уточнено після ревізії** ([B22](B22-nocode-and-realism.md) §2). Первісно ці
> чотири розділи планувалися як модулі коду. Ревізія показала, що їхня оркестрація
> — це той самий **конвеєр** `source → join → filter → group → compute → emit`,
> що потрібен для звітів (`rpt.ReportRule`) і мапінгу (`ext.DataMappingDef`).
> Тому основний варіант — **декларативний конвеєр + кроки-скрипти**, а модуль коду —
> запасний, якщо прототип на Thermaloxidizer покаже, що конвеєр не витягує (питання N-1).

#### Якщо конвеєр не витягує — модулі коду (запасний варіант)

`HSE400GasCompositionModule`, `ThermaloxidizerModule`,
`FlaringContinuousModule`, `FlaringIntermittentModule`.

Кожен — `ICalculationModule` з `MethodologyVersion`, який використовує
`Ecr.Expressions` для формул, але **власну оркестрацію** (потоки, trains, режими
SOR/EOR, FG/SG/AGR, daily+monthly, зважені середні). Це рівно те, про що
попереджає [13] §6: не всі обчислення виражаються формульним синтаксисом.

**Функції `fn_Air_30X/TOx_Emissions_{gsec,tonn}`** — це не розрахунок, а
**звітна агрегація** над результатами (`MAX`-then-`SUM`, множник Sour Analyzers,
фільтр добових подій). Вони переїжджають у **рушій звітності в сервісі** ([B16](B16-ecr-reporting.md) §3),
а не в `calc` і не в БД — інакше правило «максимум г/с за період» опиниться всередині модуля,
який рахує окремий потік і про період нічого не знає.

### 4.3 Залежності між модулями

```
HSE400 (gas composition)
   ├──► Thermaloxidizer
   └──► Flaring 30X (Continuous / Intermittent FG / SG)
Land ECW_C** — незалежні між собою
```

Порядок повного перерахунку в чинній системі задано неявно каденцією джобів
(`HSE400` — перший, `Thermaloxidizer` — останній, `ecr-jobs-ops/references/recalc.md`).
У новій — це **явне ребро графа**: `calc.MethodologyDependency(FromCode, ToCode)`,
топологічний порядок рахується при публікації і використовується планувальником
([B15](B15-ecr-recalc-orchestration.md) §3).

---

## 5. Схема `calc`

> Фізична схема фіксована; методологія додається `INSERT`-ом. Партиційний ключ —
> `PeriodKey` (B02 §3), типи чисел — `decimal(28,10)` (закриває P-11).
> Тип **зберігання** не визначає арифметику: її задає `MethodologyVersion.NumericMode`
> (ER-C-11, §8).

### 5.1 Конфігурація (immutable-версії, як `cfg.TemplateVersion`)

```sql
calc.MethodologyDef        Id, Code UQ ('ECW_C05_01'), Kind (DataDriven|Pipeline|Module),
                           ModuleCode NULL,          -- лише Kind = Module
                           PipelineDefId NULL,      -- лише Kind = Pipeline
                           NameL10n, GroupCode ('ECW_C05'), IsActive

calc.PipelineDef           Id, Code UQ ('TOx.Monthly'), UsageKind (Calc|Report|Mapping),
                           NameL10n, IsActive       -- один механізм для calc/rpt/ext (ER-N-01)
calc.PipelineVersion       Id, PipelineDefId, Version, Status, ValidFrom/ValidTo,
                           PublishedAt/By, ContentHash varbinary(32)
calc.PipelineStep          Id, PipelineVersionId, Ordinal,
                           StepKind (source|join|filter|group|compute|script|emit),
                           ConfigJson nvarchar(max),
                           ScriptVersionId NULL     -- лише StepKind = script (B19 §4.2)
                           UQ (PipelineVersionId, Ordinal)

calc.MethodologyVersion    Id, MethodologyDefId, Version, Status (Draft|Published|Deprecated),
                           ValidFrom, ValidTo, PublishedAt/By, ClonedFromVersionId,
                           NumericMode (Legacy|Strict),       -- сумісність чисел, ER-C-11
                           ContentHash varbinary(32)          -- відтворюваність

calc.FormulaDef            Id, MethodologyVersionId, Code ('ECW_EC_tons_NOx'), Ordinal,
                           Expression nvarchar(max),          -- діалект B
                           ArgumentsCsv nvarchar(max),         -- перенос FInfo_Arguments 1:1
                           OutputKind (Intermediate|Emission), SubstanceRegistryEntryId NULL,
                           Unit ('t'|'g/s')

calc.ConstantDef           Id, MethodologyVersionId, Code ('k7_ECW_C05_01_'), Parameter, Unit
calc.ConstantValue         Id, ConstantDefId, CategoryKey ('Common'|'Loc_BeforeMR_A'|'6561'),
                           Value decimal(28,10), ValidFrom, ValidTo
                           UQ (ConstantDefId, CategoryKey, ValidFrom)

calc.CategoryRule          Id, MethodologyVersionId, Expression   -- рядок → CategoryKey

calc.RuleDef               Id, MethodologyVersionId, Code ('Rule_017'),
                           SourceTableDefId,                   -- замість RuleArg_TableLandFromDB
                           MatchJson nvarchar(max),            -- {"Land_NAE":{"entryIds":[1042,1043]}}
                                                               -- значення = ValueRegistryEntryId (B19 §5.1)
                           ValidFrom, ValidTo

calc.MethodologyDependency FromMethodologyDefId, ToMethodologyDefId   -- HSE400 → TOx/30X

cfg.CalculationBinding     Id, TableDefId, MethodologyGroupCode,
                           TriggerKind (OnChange|Scheduled|OnDemand|AfterBinding),
                           ScheduleMode (Interval|Cron) NULL, IntervalSec NULL,
                           CronExpr NULL, WindowFrom/WindowTo time NULL,
                           DependsOnBindingId NULL,      -- ланцюжок замість каденції джобів
                           Priority tinyint, MaxDurationSec int,
                           InputMapJson, OutputMapJson, Scope (Row|Table|Document), IsActive
```

**Періодичність розрахунку — теж конфігурація.** `TriggerKind` каже *коли*
запускати прив'язку, а поля розкладу — *як часто*, тим самим набором понять,
що й `ext.CollectionSchedule` ([B20](B20-source-configurator.md) §4):

| `TriggerKind` | Коли запускається | Чим замінює AS-IS |
|---------------|-------------------|-------------------|
| `OnChange` | зміна даних, довідника або версії методології — інкрементно, тільки зачеплені `(модуль × період × рядки)` | 71 тригер + `JobsQueue` |
| `Scheduled` | за `IntervalSec` / `CronExpr` у вікні `WindowFrom…WindowTo` | 56 calc-джобів SQL Agent із каденцією «щохвилини» |
| `OnDemand` | кнопка «Перерахувати» (`Origin = Manual`, окрема квота) | `AF_RecalcManualInfo` + опитування кожні 10 с |
| `AfterBinding` | після успішного завершення `DependsOnBindingId` | «HSE400 перший, Thermaloxidizer останній» — знання в голові |

Розклад **не дублює** граф `calc.MethodologyDependency`: граф задає порядок
усередині одного прогону, `AfterBinding` — ланцюжок між прив'язками.
Обидва читає планувальник ([B15](B15-ecr-recalc-orchestration.md) §2.4);
жодного `sp_start_job`.

`MatchJson` замість трьох текстових полів (`RuleArg_Parameters` / `_Values` /
`_TableLandFromDB`) — бо саме їхня текстова природа давала «Rule conflict» і
«No matching rule» як runtime-помилки. Тут перекриття правил ловиться **при публікації**.

### 5.2 Результати і журнал

```sql
calc.CalculationRun        Id bigint, PeriodKey int, RunKind (Full|Incremental|Manual|Migration),
                           MethodologyVersionId, DocumentId NULL, SourceTableDefId NULL,
                           StartedAt, FinishedAt, Status, TriggeredByUserId NULL,
                           CorrelationId, InputHash varbinary(32)

calc.CalculationResult     PeriodKey int, RunId bigint, RowKey nvarchar(100),
                           SourceTableDefId int, SubstanceRegistryEntryId int,
                           EmissionTons  decimal(28,10) NULL,
                           EmissionGsec  decimal(28,10) NULL,
                           MethodologyVersionId int, IsCurrent bit,
                           PRIMARY KEY CLUSTERED (PeriodKey, RunId, SourceTableDefId, RowKey, SubstanceRegistryEntryId)
                             ON ps_ByPeriodKey(PeriodKey)

calc.CalculationInput      RunId, RowKey, ArgumentCode, Value nvarchar(400)   -- зліпок входів
calc.CalculationStep       RunId, RowKey, Ordinal, FormulaCode, Expression, ResolvedArgsJson, Result
calc.CalculationIssue      Id, RunId, RowKey NULL, Severity, Code, FormulaCode NULL,
                           Message, DetailsJson
```

**Одна таблиця результатів замість 21.** 21 `CalcResult_*` різняться лише набором
денормалізованих колонок скоупу (`Area`, `Region`, `File_Number`…) — а це атрибути
**джерела**, не результату, і в новій моделі беруться join-ом до `doc.*`.
Обсяг: ~кілька мільйонів рядків на рік — на порядки менше за `doc.CellValue`,
партиціонування по `PeriodKey` достатньо.

**`IsCurrent`** замість `DELETE by EFID + INSERT`: новий прогін додає рядки і
перемикає прапорець **однією транзакцією**. Наслідок — звіт ніколи не бачить
напівстану, і `IsBusy`-гард стає непотрібним ([B16](B16-ecr-reporting.md) §4).
Старі прогони прибирає retention-задача.

### 5.3 Таксономія `calc.CalculationIssue`

Перенесення `LogCalculationErrors` з наведенням ладу
(таксономія — `skills/ecr-methodology/references/troubleshooting.md`):

| Code | Означає | Коли ловиться |
|------|---------|---------------|
| `NO_MATCHING_RULE` | жодне правило не збіглося з рядком | runtime |
| `RULE_CONFLICT` | збіглося ≥2 | **публікація** (було runtime) |
| `FORMULA_NOT_FOUND` | немає формули з таким іменем/версією | публікація |
| `ARGUMENT_MISSING` | токен виразу не оголошений в `ArgumentsCsv` | публікація |
| `CATEGORY_NOT_RESOLVED` | немає значення константи для категорії | runtime |
| `CONSTANT_NOT_NUMERIC` | `Value` не парситься | публікація |
| `SOURCE_VALUE_INVALID` | вхідне значення не число / порожнє | runtime |
| `DIVISION_BY_ZERO`, `OVERFLOW` | арифметика | runtime |
| `MODULE_FAILED` | bespoke-модуль кинув | runtime |

Половина класів переїжджає з runtime у **публікацію версії** — це і є головний
виграш переходу «конфігурація → перевірювана конфігурація».

---

## 6. Відтворюваність

**ER-C-05. Кожен опублікований результат має повний слід.**

`CalculationRun.InputHash` + `calc.CalculationInput` (зліпок вхідних значень) +
`calc.CalculationStep` (покроково: вираз, підставлені аргументи, проміжний результат)
дають відповідь на питання регулятора «поясніть це число» **без запуску коду
тієї версії на тих даних**.

Це не нова вимога — це збереження наявної (`CalcResult_Metadata.ArgumentsJson`),
з двома доповненнями: зліпок **входів**, а не лише аргументів формули, і
`MethodologyVersionId` на кожному рядку результату.

Retention: `CalculationStep`/`CalculationInput` — не менший, ніж у самих даних;
партиції по `PeriodKey`, columnstore в `arc` разом із роком (B02 §6).
Точний строк — відкрите питання B-3 ([B10](B10-decisions.md) §4.2).

---

## 7. Перенесення конфігурації з PI AF

Однократна міграція, інструмент `tools/Ecr.Migration.Methodology`:

```
1. Прочитати дзеркала: AF_Methodology, AF_Formulas, AF_Constants,
   AF_EmissionCalculationWork_Rules  (вони вже в SQL — у PI AF ходити не треба)
2. Згрупувати за MInfo_Name → MethodologyDef + MethodologyVersion (за FInfo_Version + датами)
3. FInfo_Text → FormulaDef.Expression;  FInfo_Arguments → ArgumentsCsv (1:1, без нормалізації)
4. CInfo_* → ConstantDef + ConstantValue (CategoryKey = CInfo_Category)
5. RuleArg_* → RuleDef.MatchJson  (розбір ';'-списків параметрів і значень)
6. Порядок формул: із коду ECW_C** (аналіз послідовності присвоєнь) → FormulaDef.Ordinal
   ⚠ напівавтоматично: це єдине, чого немає в даних — воно у класі
7. Валідація: усі токени виразів резолвяться; жодного RULE_CONFLICT; усі константи числові
8. Звіт: скільки методологій/формул/констант/правил перенесено, що потребує ручного розбору
```

**Крок 6 — межа автоматики.** Порядок обчислення формул усередині методології
сьогодні заданий кодом класу; у 44 класах його треба витягти і **звірити**
(той самий підхід, що для `LegacyFieldIndex` у [B09](B09-bootstrap-and-migration.md) §2.2).
Закладати 1–2 тижні.

---

## 8. Критерій приймання: золоте порівняння

**ER-C-06. Новий рушій приймається лише за числовою рівністю з чинним CLR
на точності подання.**

```
для кожної з 21 CalcResult_* таблиці, за 2025 і 2026 роки:
   старий результат  ⟷  calc.CalculationResult нового рушія
   ключ звірки: (LandTable/модуль, EFID→RowKey, період, речовина)
   допуск: 0 ПІСЛЯ округлення до знаків, у яких колонка подається
           (ColumnDef.DisplayFormat) — БЛОКУЄ cutover;
           розбіжність ДО округлення (float-джерела, P-11) логується
           і пояснюється покейсно, cutover не блокує
   розбіжність → у звіт, ручний розбір, рішення бізнесу
```

Допуск сформульований на **точності подання**, а не на сирому значенні, свідомо:
побайтна рівність двох різних кодових шляхів на `double` недосяжна в принципі,
а критерій, який неможливо виконати, перестають перевіряти. Число має значення
рівно настільки, наскільки воно потрапляє у форму.

---

### ER-C-11. Числова сумісність із чинним рушієм — вимога, а не побажання

**Рішення по R-1 ухвалено: зміна останніх знаків у вже поданих формах
неприпустима.** Це не одне обмеження, а три механізми, бо й загроза трояка —
перерахунок минулого, інша арифметика, недетермінована агрегація.

**1. Подані зрізи не перераховуються ніколи.**
`rpt.ReportSnapshot.IsSubmitted = 1` іммутабельний, retention безстроковий
([B16](B16-ecr-reporting.md) §5). Те, що подано регулятору, лишається тим, що
подано, незалежно від будь-яких пізніших змін рушія чи методології. Це головна
гарантія, і вона взагалі не залежить від точності обчислень.

**2. `MethodologyVersion.NumericMode` — два режими арифметики.**

| Режим | Арифметика | Де застосовується |
|-------|-----------|-------------------|
| **`Legacy`** | подвійна точність, **той самий порядок операцій і ті самі точки округлення**, що й чинний CLR; семантичні пастки NCalc зберігаються (цілочисельне ділення `365/31 = 11`, §3) | за замовчуванням — усі методології на фазі співіснування і всі періоди до cutover розділу |
| **`Strict`** | наскрізний `decimal(28,10)` | вмикається **явним рішенням і лише з нової `ValidFrom`** |

Перемикання режиму — зміна класу `Breaking`: обов'язкові `ChangeReason`, аналіз
впливу, нова версія. **Заднім числом режим не змінюється ніколи** — саме тому він
живе на версії методології, а не в конфігурації застосунку.

**3. Тип зберігання лишається `decimal(28,10)`** — BR-22 і П-22 чинні. Це рішення
про **сховище**, а не про арифметику, і воно працює *на* сумісність, а не проти
неї: `float` у колонці дає результат агрегації, залежний від порядку рядків, —
тобто робить саму звірку неможливою. Значення, пораховане в `Legacy`,
конвертується в `decimal(28,10)` при збереженні; десять знаків — на кілька
порядків більше, ніж потребує будь-яка держформа.

**Наслідок для плану:** звірка ER-C-06 стає **блокуючою умовою cutover розділу**,
а не звітом про якість ([B17](B17-ecr-transition.md) §5).

Це прямий аналог round-trip-звірки міграції даних ([B09](B09-bootstrap-and-migration.md) §4)
і єдиний спосіб довести, що «конфігурація замість 44 класів» не змінила жодного числа.

Окремо звіряються **звітні агрегації** (`fn_Air_30X/TOx_*`) — але вже в
[B16](B16-ecr-reporting.md), бо вони не частина розрахунку.

---

## 9. Реєстр рішень документа

| # | Рішення | Обґрунтування |
|---|---------|---------------|
| **ER-C-01** | Методологія — дані (`calc.*`); 44 класи `ECW_C**` → один generic-модуль | конфігурація вже існує як дані в PI AF; додати методологію має бути `INSERT` |
| **ER-C-02** | Розрахунок — поза SQL Server, у worker-процесах | спільний AppDomain CLR дає `701→6535→233`; NCalc не потокобезпечний у чинній реалізації |
| **ER-C-03** | Результати — `calc.CalculationResult`, не `doc.CellValue` | інша природа даних; інакше перерахунок конфліктує з аудитом і блокуванням рядка |
| **ER-C-04** | `Ecr.Expressions` отримує контекст `Methodology` (діалект AF, 24 функції) | у ECR два діалекти; B03 покривав один |
| **ER-C-05** | Повний слід розрахунку (`Run`/`Input`/`Step`) — обов'язковий | вимога регулятора; часткова реалізація вже є (`CalcResult_Metadata`) |
| **ER-C-06** | Приймання — золоте порівняння з CLR за 2 роки по всіх 21 таблиці | єдиний спосіб довести відсутність зсуву чисел |
| **ER-C-07** | `IsCurrent` + `RunId` замість `DELETE by EFID + INSERT` | звіт не бачить напівстану; `IsBusy`-гард стає непотрібним |
| **ER-C-08** | `decimal(28,10)` замість `float` для емісій — тип **зберігання** | P-11: `float` у 26 таблицях чинної схеми; агрегація `float` залежить від порядку рядків, тобто робить звірку неможливою |
| **ER-C-09** | `fn_Air_30X/TOx_*` — **у сервіс**, як іменовані агрегації звітності (не в `calc` і не в БД) | це агрегація над періодом, а не розрахунок джерела; у БД логіки не лишається взагалі ([B16](B16-ecr-reporting.md) §3) |
| **ER-C-10** | Половина класів помилок переїжджає з runtime у публікацію версії | `RULE_CONFLICT`, `ARGUMENT_MISSING`, `CONSTANT_NOT_NUMERIC` виявні статично |
| **ER-C-11** | **Числова сумісність обов'язкова** (закриває R-1): `NumericMode = Legacy` за замовчуванням, подані зрізи іммутабельні, звірка блокує cutover | звітність уже подана регулятору з чинними числами; «трохи інші останні знаки» — це інша подана форма |

## 10. Відкриті питання

> ## ⬛ Оновлено 2026-09-03 — рішення прийняті
>
> Питання цього розділу опрацьовані в
> [`../17-open-questions-answers.md`](../design/17-open-questions-answers.md);
> прийняті рішення внесені в [`../06-tz-architecture.md`](../design/06-tz-architecture.md) §12.
> Таблиця нижче лишається як історія; актуальний статус — тут.
>
> | # | Статус | Рішення |
> |---|--------|---------|
> | **Одиниці** | ✅ **додано 2026-09-03** | `calc.MethodologyConstant` отримує `UnitId` (→ `uom.Unit`); `calc.MethodologyOutput` і `calc.CalculationResult` — теж. **Контекстні коефіцієнти (щільність, теплотворність, молярна маса) — це константи методології, а не конверсії одиниць**: вони залежать від речовини й умов і змінюються з часом. Внесення їх у `uom.Conversion` заборонене і неможливе за побудовою (конверсія лише в межах однієї розмірності). Див. `../tz/02-requirements.md` ФВ-16 |
> | **Календар** | ✅ **додано 2026-09-03** | `MethodologyVersion.CalendarMode` (`Actual` / зафіксована legacy-конвенція) визначає джерело `Period.Days`/`Hours`/`Seconds`. Перерахунок у `г/с` ділить на кількість секунд у періоді: різниця конвенції (365 vs фактичний календар, 30-денний місяць) змінює **всі** числа і виглядає як помилка формули. Режим видимий у конфігураторі й обов'язковий у diff при публікації. Див. D-78 |
> | **C-1** | 🔧 робота Етапу 0 | Класифікувати 44 класи **за формою**, не за предметом. Результат — таблиця на 44 рядки: `форма` · `рівень 1/2/3` · `причина винятку`. Установка — **«generic + явний список винятків»**, а не доведення універсальності |
> | **C-2** | ✅ **вирішено** | Порядок **не зберігається**. Зберігаються залежності; порядок — топологічний, рахується при `Publish`; цикл = помилка публікації. Міграція: порядок із коду класу береться як **підказка**, далі звірка результатів на золотому наборі. Розбіжність = пропущена залежність або прихований побічний ефект, і те й інше має стати явним входом |
> | **C-3** | ✅ **вирішено** (замовник) | **Нічого не затирається.** `CalculationStep`/`Input` зберігаються **назавжди** і архівуються в `arc.*` разом з рештою. Обсяг керується не строком, а **рівнем трейсу**: `Off` / `ErrorsOnly` / `Full` — налаштування методології і конкретного прогону. Записане живе вічно; непотрібне просто не пишеться. Відтворюваність забезпечує **іммутабельний зріз при поданні**, а не трейс |



| # | Питання | Хто | Коли | Чому не можна зараз |
|---|---------|------|-----|---------------------|
| C-1 | Чи всі 44 `ECW_C**` зводяться до generic-модуля, чи є винятки | архітектор | Етап 0 | потрібен суцільний перегляд 44 класів; оцінка — 1 тиждень, робиться на Етапі 0 |
| C-2 | Порядок формул усередині методології | розробник + методолог | до першої публікації методології | у даних його немає (§7 крок 6) |
| C-3 | Строк зберігання `CalculationStep`/`Input` | бізнес + ІБ | до проєктування retention | обсяг залежить від відповіді |
| ~~C-4~~ | *злито з **R-1***, і **R-1 закрито**: сумісність обов'язкова — див. ER-C-11 (§8) | — | закрито | — |

## 11. Чек-лист

- [ ] `calc.*` створено порожнім у першій міграції (не додавати схему на живій БД пізніше)
- [ ] `Ecr.Expressions` має контекст `Methodology` і всі 24 функції
- [ ] Вирази компілюються в делегати і кешуються **іммутабельно**
- [ ] `RULE_CONFLICT` і `ARGUMENT_MISSING` ловляться при публікації, не в runtime
- [ ] Версія методології резолвиться **за датою періоду**, не `MAX()`
- [ ] `MethodologyDependency` бере участь у топологічному порядку разом із формулами
- [ ] Результат пишеться з `RunId` + `IsCurrent`, одна транзакція на перемикання
- [ ] Емісії — `decimal(28,10)`
- [ ] Кожен прогін лишає `Run` + `Input` + `Step`
- [ ] Золоте порівняння з CLR зелене на 2025–2026 по всіх 21 таблиці

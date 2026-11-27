# B12 — Чинний бекенд ECR: карта AS-IS і вимоги до заміни

> Пакет `architecture/docs` описує лише **Excel/VBA-частину** ECR (введення даних).
> Другої половини системи там немає — а саме вона рахує викиди і формує звіти
> регулятору. Цей документ фіксує її фактами з першоджерел і виводить вимоги,
> яким має задовольняти новий бекенд.
>
> Джерела: `SQL/DB.txt` (дамп БД `ECR`, 13 МБ, зріз 2026-08-13), `SQL/Jobs.txt`
> (дамп SQL Agent), `Assembly/DllProject/`, `PI AF/ECR_01_Air*.{xml,sql}`,
> `Project Info/{Data_Lifecycle,ECR_v_1_17,User Manual}.docx`,
> `Project Info/Performance_Analysis_ECR_Pipeline.md`, скіли `skills/ecr-*`.
> Усі числа нижче — **пораховані по дампу**, не оцінені.

---

## 1. Що таке бекенд ECR одним абзацом

**PI AF — сховище введених даних і конфігурації розрахунку. SQL Server — дзеркало,
рушій і звітність.** Excel-файли пишуть рядки таблиць у PI AF як Event Frames
(`Attribute_XXXX` = `;`-рядок). Щохвилинні sync-джоби вичитують їх через RTQP,
розбирають `;`-рядки **позиційно** у типізовані колонки `AF_*`-таблиць. Тригери на
цих таблицях ставлять роботу в чергу `JobsQueue`. Диспетчер (кожні 30 с) запускає
SQL Agent-джоби, ті кличуть SQL CLR-збірку `DllProject`, яка бере методології,
формули і константи (теж із PI AF, теж через дзеркала) і рахує через NCalc.
Результати лягають у `CalcResult_*`. 127 процедур `Report_*` віддають їх у 166 RDL
на окремому SSRS-сервері.

```
Excel RDS/HSE400/401/401A/372 ─┐
PI Vision (адмін-UI)          ─┼──►  PI AF  ECR_01_Air (NCATAPPV0154)
FLERT (потоки, об'єми)        ─┘       185 шаблонів елементів, 4 487 атрибутів
                                        │  RTQP-в'юхи [Master].[Category].*_V
                                        │  (живуть в AF-базі, не в SQL)
                                        ▼  OPENQUERY через linked server
                          ┌─────────────────────────────────────────┐
                          │  SQL Server  БД ECR (NCATSQLV92)        │
                          │  79 AF_* дзеркал (63 land) + 93 _History│
                          │  21 CalcResult_* + історія              │
                          │  71 тригер → JobsQueue                  │
                          │  344 процедури (127 з них Report_*)     │
                          │  CLR DllProject + NCalc (UNSAFE)        │
                          └──────────────┬──────────────────────────┘
                                         │
                          100 джобів SQL Agent   ──►  SSRS ncatsqlv42\PI
                          (56 land calc, 18 flare,      166 RDL
                           2 calc, 16 sync, 8 service)
```

---

## 2. Інвентар (пораховано по `SQL/DB.txt` і `SQL/Jobs.txt`)

| Об'єкт | Кількість | Примітка |
|--------|----------:|----------|
| Таблиці всього | **211** | з них **93** — `_History` |
| `AF_*` (дзеркала AF), без історії | **79** | з них `AF_Air_Land_*` — **63** |
| `CalcResult_*`, без історії | **21** | результати розрахунку |
| Процедури | **344** | |
| … з них `Report_*` | **127** | живлять SSRS |
| … з них `Utility_*` | 56 | операційні |
| … з них `AF_*_Sync` | 80 | з них 63 land |
| Тригери | **71** | 63 land + 30X/HSE400 + `ConstantsTrigger`/`FormulasTrigger` |
| Функції | 17 | `fn_Air_30X/TOx_Emissions_{gsec,tonn}`, `ufnSplit`, `fn_CleanAFValue`, … |
| Збірки CLR | 2 | `DllProject`, `NCalc` — обидві `PERMISSION_SET = UNSAFE` |
| Файлів C# у збірці | 148 | `Assembly/DllProject/DllProject/**` |
| Джоби SQL Agent | **100** | розклад нижче |
| Шаблони елементів PI AF | **185** | `PI AF/ECR_01_Air.xml` |
| Шаблони атрибутів PI AF | **4 487** | |
| RDL-звіти | **166** | `SSRS/ECR/{01. Production, 02. Acceptence}` |

**Джоби за родинами** (`Jobs.txt`, унікальні `@job_name`):

| Родина | Кількість | Каденція |
|--------|----------:|----------|
| `ECR_Air_Calc_Land_*` (+ `_1…_12` split) | **56** | 1 хв |
| `ECR_Air_Calc_Flaring_*` (Continuous / Int FG / Int SG, + `_1…_5`) | **18** | 1 хв |
| `ECR_Air_Calc_{GasComposition_HSE400, HSE_Thermaloxidizer}` | 2 | 1 хв |
| `ECR_*_Sync_*` (`Land_*`, `HSE3XX`, `HSE_401`, `Sync_Common`) | **16** | ~1 хв |
| Службові: `ECR_Execution`, `ECR_ManualRecalc`, `ECR StartUp`, `ECR CleanUp Logs`, `ECR Manual Maintenance Job`, `ECR_Utility_RequeueFailedJobs`, `ECR_DataVolume_CaptureAfter`, `ECR Sync_ReportAndTransfer` | 8 | 30 с / 10 с / щодня |

---

## 3. Ключові механізми — як воно працює насправді

### 3.1 Синхронізація AF → SQL: позиційний розбір `;`-рядка **в SQL**

Кожна з 63 land-процедур `AF_Air_Land_*_Sync` має однакову форму
(перевірено на `AF_Air_Land_Off_Process_DI_11_01_Sync`, `DB.txt:28407`):

```sql
AF_Land_Watchdog_Initialize          -- зайняти вартового, взяти @JobId
@LastUpdateDateTime = ufnGetSyncStartDateTime(@ProcedureName, …)   -- watermark
EXEC AF_Land_ID_Get …                -- OPENQUERY → [Master].[Category].[EventFrameAttributes_V]
EXEC AF_Land_DeleteObsoleteEFID_ByTemplate …
-- розбір рядка:
CROSS APPLY dbo.ufnSplit(i.ParameterValue, ';') AS s     -- Category = 'Tab_Data'
SELECT MAX(CASE WHEN rn = 1  THEN value END) AS Land_Status,
       TRY_CAST(MAX(CASE WHEN rn = 2 THEN value END) AS INT) AS Land_No,
       MAX(CASE WHEN rn = 5  THEN value END) AS Land_DescEqp,
       …                                                  -- ← ПОЗИЦІЯ = СЕМАНТИКА
-- шапка контракту:
dbo.fn_CleanAFValue(MAX(CASE WHEN ParameterName = 'File_Number' THEN ParameterValue END))
MERGE … WHEN MATCHED AND (target.ModifiedTime < source.ModifiedTime) …
```

**Що це означає:**

* Позиційна ідентичність полів із Excel/VBA (`Attribute_0010 = "1;7001001;ITEM;…"`)
  **не зникає на межі SQL — вона там відтворюється вручну**, 63 рази, у вигляді
  `CASE WHEN rn = N`. Вставка колонки в Excel-аркуш ламає розбір мовчки
  (підтверджено кейсом 2026-08-24: `HSE_Land_07` 10 → 11 слотів зсунув місяці `H:S`→`I:T`).
* Кожна таблиця дублює **20 колонок `Con_*`** (шапка контракту) в кожному рядку —
  `AF_Air_Land_Utility_9_05` має 20 `Con_*` проти 12 `Land_*` змістовних
  (`DB.txt`, DDL таблиці).
* Синк **інкрементний за watermark** `WatchdogStatus_Land.LastUpdateEF`. Наслідок,
  який коштував даних: `DROP TABLE` не рухає watermark, тому після перестворення
  таблиці старі EF **не повертаються ніколи** (кейс 2026-08-31, `CHANGE_…waste_chain.md`).
* PI SQL DAS (RTQP) **уже у продуктиві**: `AF_Land_ID_FileNum_Get` (`DB.txt:58194`)
  будує `OPENQUERY(<linked server>, 'SELECT … FROM [Master].[Category].[EventFrameAttributes_V] …')`.
  В'юхи створюються **на AF-сервері** (`PI AF/ECR_01_Air_PISqlClientExportedObjects.sql`),
  тому не їдуть разом із БД — це джерело поломок при переїзді середовища.

### 3.2 Черга: рядок-параметр, курсор, `sp_start_job`

`AF_Land_Trigger` (`DB.txt:58435`) складає **рядок**:

```
StartDate:2026-09-01T00:00:00;EndDate:2026-09-30T23:59:59;LandTable:AF_Air_Land_Utility_9_05;FileNumbers:test;
```

і кладе його в `JobsQueue(Job_Id, Job_Parameters, Status=1)` з дедупом за
точним збігом рядка. `JobQueue_Optimize` зливає перекриті діапазони і піднімає
`1 → 2`. `JobQueue_Execution` (`DB.txt:59294`) — **курсор**, який:

* пропускає рядок, якщо для того самого `Job_Id` уже є `Status = 3` —
  **один осиротілий рядок блокує весь джоб**;
* якщо `JobsList.Split = 1 AND DATEDIFF(DAY) > 1 AND @FileNumbers IS NULL` — ділить
  діапазон на `DATEDIFF(MONTH)+1` частин (коли є `LandTable`) або на **5**, видаляє
  батьківський рядок і стартує `<Job>_1…_N` через `msdb.dbo.sp_start_job`;
* інакше — `Status = 3` і `sp_start_job`.

`Utility_JobExecute` (`DB.txt:89243`) той самий рядок **розбирає назад**
`CHARINDEX`/`SUBSTRING` і будує `sp_executesql 'EXEC DllProject_… @StartDateTime, @EndDateTime[, @LandTable, @FileNumbers]'`.

> Тобто контракт задачі — **нетипізований рядок**, який тричі складається і
> двічі розбирається текстом. Це не стиль, це джерело класу помилок.

### 3.3 Тригери довідників: рекурсивний обхід за **входженням підрядка**

`ConstantsTrigger` (`DB.txt:96614`) при зміні константи шукає залежні формули так:

```sql
INNER JOIN [dbo].[AF_Formulas] f ON CHARINDEX(ac.CName, f.FInfo_Arguments) > 0
WHERE (ac.CMethodology NOT LIKE 'ECW_C%' OR f.MInfo_Name = ac.CMethodology)
```

— **пошук за входженням підрядка в текстовий список аргументів**, далі рекурсія по
формулах, що посилаються одна на одну, і постановка в чергу всіх джобів із
`FInfo_UsedInJobs`. Граф залежностей існує **лише як текст**; префіксні імена
(`k1_…` ⊂ `k1_GasComp_…`) дають хибні спрацювання, і в коді вже є «ранній вихід»,
доданий проти циклу.

### 3.4 Розрахунок: два підходи на спільному скелеті

| | Підхід A — Land (data-driven) | Підхід B — bespoke |
|---|---|---|
| Entry-proc | один: `DllProject_Land_General` | свій на модуль: `DllProject_Air_GasCompositions_HSE400`, `_HSE_Thermaloxidizer`, `_Air_Flaring_{Continuous,Intermittent_FG,Intermittent_SG}` |
| Маршрутизація | `R_Land_RuleMatcher` зіставляє рядок із правилами AF → ім'я методології → `F_MethodologyInvoker` через **reflection** → клас `ECW_C**` | методологія зашита (`"HSE400"`, `"Thermaloxidizer"`, `"Flert"`) |
| Логіка | «рядок → формули NCalc → результат» | багатоетапна доменна (trains, SOR/EOR, FG/SG/AGR, daily+monthly, зважені середні) |
| Додати джерело | AF-конфіг + дрібний клас | правити C# |

Спільне: `DateProcessing` → `Get_Methodologies(year)` → `*_Cleanup` → `Get_Constants`/`Get_Formulas` → збір даних → **Calculate** → `Merge_CalcResult_*` (TVP) + `Merge_CalcResult_Metadata` + `ErrorHandlerBatchAdd`.

### 3.5 Конфігурація розрахунку — це вже дані, але в PI AF

Чотири дзеркала (DDL із `DB.txt`) — фактично готова **метамодель розрахунку**:

| Таблиця | Ключові поля |
|---------|--------------|
| `AF_Methodology` | `MetName`, `VersName`, `StartDate`/`EndDate`, `Status` |
| `AF_Formulas` | `FInfo_Name`, **`FInfo_Text`** (NCalc), **`FInfo_Arguments`** (`;`-список), `FInfo_StartDate/EndDate`, `FInfo_Version`, `FInfo_UsedInJobs`, `FInfo_ExtField`, `MInfo_Name` |
| `AF_Constants` | `CInfo_Parameter`, `CInfo_Name`, **`CInfo_Value` `nvarchar(255)`**, `CInfo_Category`, `CInfo_Gas`, `CInfo_StartDate/EndDate`, `MInfo_Name` |
| `AF_EmissionCalculationWork_Rules` | **`RuleArg_TableLandFromDB`**, **`RuleArg_Parameters`**, **`RuleArg_Values`**, дати, `MInfo_Name` |

Ланцюг з'єднаний **іменем класу**: `ECW_Cxx` == `MetName` == `MInfo_Name` усіх його
формул/констант/правил. Правило має збігтися **рівно одне**: 0 → «No matching rule»,
≥2 → «Rule conflict».

`PI Vision` (`https://pivision.ncoc.kz/pivision/`) уже дає адміністратору редактор
цього всього — розширення **Dropdown Lists / Permit tool / Recalculation /
Methodologies** (User Manual §2.1.6–2.1.9), із валідацією виразу, автодоповненням
`CST.`/`!`/`@` і версіями. Набір функцій редактора формул задокументовано:

```
Abs Acos Asin Atan Ceiling Cos Exp Floor IEEERemainder Ln Log Log10
Max Min Pow Round Sign Sin Sqrt Tan Truncate  +  in  if  ifs
```

> **Це найважливіший факт документа.** Предметна конфігурація ECR **уже
> є конфігурацією**, а не кодом. Новий бекенд не має її «винаходити» — він має
> перенести її з PI AF у власну схему, зберігши семантику 1:1.

### 3.6 Результати і трасування

`CalcResult_Emissions_Air_Land_Utility_1` (типова):
`ID` identity, `StartDate`/`EndDate`/`MonthNumber`, `ModifiedTime`, `EFID`,
11 денормалізованих колонок скоупу (`Region_Atyrau`, `Onshore_Offshore`, `Area`,
`Contractor_Or_Company`, `File_Number`, `Permit_Number`, `MPE_Number`, `MonPoint`…),
`Component` `int`, `ComponentName`, **`Emission_Tons` `float`**, **`Emission_Gsec` `float`**,
`LandTable`, `Methodology`.

⚠ **`float` для регуляторних чисел** — у 26 таблицях `Emission_Tons` і в 24
`Emission_Gsec` оголошені `float`; `decimal` лише у 2. Це не «оптимізація», це
джерело незбіжностей у звірках і невідтворюваності підсумків.

`CalcResult_Metadata` — уже наявний **журнал розрахунку**:
`OperationId`, `TargetTable`, `TargetColumn`, `EFID`, `DataDate`, `ResultValue`,
`FormulaName`, **`ArgumentsJson`**, `Methodology`, `HSENumber`, `HSELandNo`.
`LogCalculationErrors`: `OperationId`, `Function`, `Report`, `Formula`, `Arguments`, `Text`.

> Тобто вимога відтворюваності («покажіть, як вийшло це число») у чинній системі
> вже частково закрита — і новий бекенд не має її **втратити** (див. [B13](B13-ecr-calculation-engine.md) §6, ER-C-05).

### 3.7 Звіти читають `MAIN ∪ _History` позиційно

`Report_Air_Land_Utility_9_01` (`DB.txt`):

```sql
WITH RawData AS (
    SELECT * FROM AF_Air_Land_Utility_9_01          WHERE Time BETWEEN … AND Con_File_Number = @File_Number
    UNION ALL
    SELECT * FROM AF_Air_Land_Utility_9_01_History  WHERE …
),
LatestGeneralData AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY Land_No ORDER BY Time DESC) AS rn FROM RawData)
```

`SELECT *` + `UNION ALL` → **порядок колонок MAIN і `_History` мусить збігатися
побітово**; будь-який `ALTER TABLE ADD` треба робити в обидві таблиці однаково.
Далі — «останній за часом виграє» і PIVOT місяців у 12 колонок.

---

## 4. Чого в чинному бекенді немає взагалі

| Немає | Наслідок сьогодні |
|-------|-------------------|
| **Аудиту змін даних** | немає відповіді «хто і коли змінив це значення»; `_History` зберігає версії рядка, але не автора і не причину |
| **Розмежування доступу** | доступ = права на БД/AF/SSRS; предметних ролей немає, «тільки читання по аркушу» неможливе |
| **Версійності структури** | схема таблиці = поточний стан; додати колонку = `ALTER TABLE` в MAIN + `_History` + 63 однакові правки |
| **Затвердження даних** | немає стану `Draft/Submitted/Approved`; порахований результат = дійсний результат |
| **Тестів** | автотестів немає; єдина перевірка — звірка звіту з Excel руками |
| **Типізованого контракту задачі** | `StartDate:…;EndDate:…;` як рядок |
| **Транзакційної цілісності результату** | `CalcResult_*` перезаписується `DELETE by EFID + INSERT`; під час перерахунку звіт бачить напівстан (звідси `IsBusy`-гард) |

---

## 5. Реєстр болів → вимоги (зведено, з доказами)

| # | Біль | Доказ | Вимога до нового бекенду | Пріор. |
|---|------|-------|--------------------------|:------:|
| P-01 | Позиційний розбір `;`-рядка повторено 63 рази в SQL | `AF_*_Sync`, `CASE WHEN rn = N` | ідентичність поля — `ColumnDef.Code`; позиція живе **лише** в `ext.LegacyColumnMapping` | Must |
| P-02 | 20 колонок `Con_*` дублюються в кожному рядку кожної таблиці | DDL `AF_Air_Land_Utility_9_05` | шапка — `MetadataSource` із `SyncMode = Reference` (B01, [09] §5.2) | Must |
| P-03 | Watermark + `DROP TABLE` = безповоротна втрата | `CHANGE_2026-08-31_waste_chain.md` | збір ідемпотентний за `(DataSourceId, SourcePath, Timestamp)`, без прихованого стану | Must |
| P-04 | RTQP-в'юхи живуть в AF-базі → двосерверний деплой | `01_PISQL/*.sql`, кейс DEVV08 2026-08-25 | доступ до джерела — конфігурація `ext.DataSource`, жодних артефактів у чужій БД | Must |
| P-05 | Контракт задачі — нетипізований рядок | `AF_Land_Trigger` / `Utility_JobExecute` | типізований `JobArgs`, валідований на межі | Must |
| P-06 | Один `Status = 3` блокує весь джоб; ghost/phantom | `JobQueue_Execution`, скіл `ecr-jobs-ops` | черга з orphan-детекцією і liveness, at-least-once + ідемпотентність | Must |
| P-07 | Паралельний CLR у спільному AppDomain → OOM 701→6535→233 | `memory-oom.md`, `Performance_Analysis` | розрахунок **поза** SQL Server, у worker-процесах з лімітом пам'яті | Must |
| P-08 | NCalc `Expression.Parameters` мутується на спільному кеші | `Utilities.cs:42-45`, коміт «Test async - Failed» | іммутабельні скомпільовані вирази; паралелізм безпечний | Must |
| P-09 | `ErrorHandlerBatchAdd` у циклі → O(G²) вставок і дублі | `R_Land_General.cs:145` | помилки — набором, один раз | Should |
| P-10 | Граф залежностей формул = `CHARINDEX` по тексту | `ConstantsTrigger` | явний граф `FormulaDependency` (B03 §4.2) | Must |
| P-11 | `Emission_Tons/Gsec` — `float` у 26/24 таблицях | DDL `CalcResult_*` | `decimal(28,10)`, як `doc.CellValue.ValueNumeric` (B02 §2.2) | Must |
| P-12 | `SELECT *` + `UNION ALL` MAIN/`_History` | `Report_Air_Land_Utility_9_01` | архів прозорий у репозиторії (B02 §6.4), звіт не знає про дві таблиці | Must |
| P-13 | `IsBusy` блокує звіти під час перерахунку | `Utility_CheckIsBusy_Land`, датасети RDL | консистентне читання замість блокування (B16 §4) | Should |
| P-14 | Помилки ковтаються: порожній `TRY/CATCH`, `on_fail_action = 3` | кейс IEC 2026-07-30 | збій задачі видимий; жодного мовчазного `RETURN` | Must |
| P-15 | Немає аудиту, RBAC, затвердження | §4 | `aud.*`, `IAccessDecisionService`, `Draft→Submitted→Approved` — уже в ядрі | Must |
| P-16 | Додати розділ = ~30 файлів × 2 сервери | `WaterAndWaste/_docs/00_README_layered.md` | новий розділ = конфігурація, без коду і без DDL | Must |
| P-17 | Нічний `Manual Maintenance Job` робить **shrink БД** щоразу | `Jobs.txt` | обслуговування без shrink; фрагментація не лікується щоночі | Should |
| P-18 | Retention логів 31 день, історія розрахунку — ні | `ECR CleanUp Logs` | явна політика retention на кожен журнал ([B07](B07-jobs-and-observability.md) §5а) | Should |

---

## 6. Карта «AS-IS → TO-BE»

| Компонент AS-IS | Куди лягає | Документ |
|-----------------|-----------|----------|
| Excel RDS + VBA (введення) | веб-грид, `doc.*` | `architecture/docs/06`, B01 |
| PI AF: EF `HSE_Land_*` (`;`-рядки) | джерело на перехід → `ext.RawDataPoint` → `doc.*`; потім зникає | B14 §3 |
| PI AF: `Dictionary_*`, `Permit*`, `PermitConstant` | універсальні реєстри `cfg.Registry*` / `dic.Registry*` | B14 §5, [12] |
| PI AF: `Methodology`/`Formula_El`/`Constant_El`/`Rules` | схема `calc.*` | **B13** |
| PI AF: `Settings_General` (`CALCULATION_*`) | `doc.Project.ExternalSettingsJson` | B14 §6 |
| PI AF: `Check_*` + `AFNotificationRule` | алерти системи | B15 §6 |
| FLERT (потоки, gas composition) | `ext.DataSource(Kind = Sql)` — **лишається назавжди** | B14 §7 |
| 63 `AF_*_Sync` + 71 тригер + RTQP-в'юхи | `DataCollectionJob` + `ext.DataMappingDef` | B14 §4 |
| `JobsQueue`/`JobsList`/`WatchdogStatus` + 100 джобів | `IJobScheduler` + worker-пул | **B15** |
| CLR `DllProject` (148 файлів) | `Ecr.Calculations` (out-of-process) | **B13** §4 |
| `AF_Formulas.FInfo_Text` (NCalc) | `Ecr.Expressions`, діалект AF | B13 §3, правка до B03 |
| 21 `CalcResult_*` + `_History` | `calc.CalculationResult` (партиції, `arc`) | B13 §5 |
| `CalcResult_Metadata` + `LogCalculationErrors` | `calc.CalculationRun` + `Trace` + `calc.CalculationIssue` | B13 §6 |
| 127 `Report_*` + 11 `fn_Air_*` + 166 RDL | **рушій звітності в сервісі**; `rpt.*` — матеріалізований зріз без логіки; SSRS рендерить | **B16** |
| `Utility_MasterArchiveControl` (`_History`) | `ArchiveJob` → `arc.*` | B02 §6 |
| PI Vision (Dropdown/Permit/Methodologies/Recalculation) | екрани реєстрів + конструктор методологій + запуск перерахунку | B13 §7, B15 §2 |

---

## 7. Що з цього випливає для пакета B01–B11

Три уточнення, які цей документ додає і які розкриті далі:

1. **Розрахунки — не «наступна фаза».** Вони є половиною системи, і їхня
   конфігурація вже існує у вигляді даних. B08 («попередній проєкт `calc`»)
   замінюється конкретним B13.
2. **Мова виразів у ECR — не Excel.** Крім 11 Excel-функцій RDS-шаблону
   ([15] §4) є **другий діалект** — NCalc-формули методологій із `CST.`/`!`/`@`
   і 24 функціями (§3.5). B03 покривав лише перший — потрібна правка.
3. **RTQP не «треба перевірити» — він у продуктиві.** Відкрите питання ТЗ §12 п.13
   закривається фактом: `OPENQUERY` до `[Master].[Category].*_V` працює щохвилини
   у 63 процедурах (§3.1). Це впливає на оцінку міграції історії.

Повний перелік правок — [B10](B10-decisions.md) §2 (після цього пакета) і B18.
